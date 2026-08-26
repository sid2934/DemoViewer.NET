#region

using System.Globalization;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Annotations;

/// <summary>
///     Owns the annotation session for the 2D tab: the document, the sidecar, the settings that seed the
///     ink style, and the feature gate.
///     <para>
///         <b>The gate is read through <see cref="IModuleContext.Features" /></b> (registry §3.10), never
///         through an injected <c>IFeatureGate</c>, and a null projection fails OPEN. With the gate off
///         nothing is loaded, nothing is autosaved and nothing is written on flush — "gated off touches
///         no disk" is a testable claim, not a comment.
///     </para>
///     <para>
///         <b>Autosave is debounced and off the UI thread.</b> A stroke commits one document change; a
///         drag-erase across thirty strokes commits one too. Writing on each would put a file write in
///         the middle of a gesture, so changes coalesce into one save after
///         <see cref="AutoSaveDelay" />. <see cref="StateChanged" /> may therefore be raised from a
///         thread-pool thread — the view-model marshals.
///     </para>
///     <para>
///         <b>Preference writes are debounced too</b>, for the same reason and a louder trigger: the ink
///         pickers raise a change on every pointer sample, and <c>SettingsService.Write</c> is a
///         synchronous write-and-reload whose reload re-composes the keymap and the Settings page inline.
///         See <see cref="PersistSettings" /> and <see cref="StylePersistDelay" />.
///     </para>
/// </summary>
public sealed class AnnotationSessionController : IDisposable
{
    /// <summary>The feature id gating the whole annotation surface. A persisted key; never renamed.</summary>
    public const string FeatureId = "playback2d.annotations";

    private readonly SettingsService? _settings;
    private readonly AnnotationStore? _store;
    private readonly Lock _saveGate = new();

    // Saves are SERIALIZED. Cancelling the debounce does not stop a save already inside the store's
    // write, so a flush-on-deactivate immediately after a stroke could run concurrently with it: both
    // wrote the same "<path>.tmp" and then raced to File.Move it, which can leave the OLDER snapshot on
    // disk with nothing scheduled to correct it. Taking the snapshot inside this gate makes the last
    // writer the newest one, by construction.
    private readonly SemaphoreSlim _saveSerializer = new(1, 1);

    private bool _attached;
    private ClockIdentity _clock = ClockIdentity.Unknown;
    private CancellationTokenSource? _debounce;
    private DemoIdentity? _demo;
    private string? _demoPath;
    private bool _disposed;
    private IModuleFeatureGate? _features;
    private int _lastSavedVersion = -1;
    private bool _loading;

    // Injected so the browser branch of DescribeLocation is testable on a desktop runner:
    // OperatingSystem.IsBrowser() is a JIT-folded intrinsic and cannot be faked from outside.
    private readonly Func<bool> _isBrowser;

    /// <summary>Creates a controller. Every dependency is optional so a headless test needs no container.</summary>
    /// <param name="store">The sidecar store, or null to run session-only.</param>
    /// <param name="settings">The app settings service, or null to use the built-in defaults.</param>
    public AnnotationSessionController(AnnotationStore? store, SettingsService? settings)
        : this(store, settings, OperatingSystem.IsBrowser)
    {
    }

    /// <summary>Test seam: the same controller with the host predicate injected.</summary>
    /// <param name="store">The sidecar store, or null to run session-only.</param>
    /// <param name="settings">The app settings service, or null to use the built-in defaults.</param>
    /// <param name="isBrowser">Whether the host is the WASM head.</param>
    internal AnnotationSessionController(AnnotationStore? store, SettingsService? settings,
        Func<bool> isBrowser)
    {
        ArgumentNullException.ThrowIfNull(isBrowser);
        _isBrowser = isBrowser;
        _store = store;
        _settings = settings;

        Session = new AnnotationSession(new AnnotationDocument());
        ApplySettings();
        Session.Document.Changed += OnDocumentChanged;
        StatusText = DescribeLocation();
    }

    /// <summary>The session the tools, the layer and the panel share.</summary>
    public AnnotationSession Session { get; }

    /// <summary>The document being edited.</summary>
    public AnnotationDocument Document => Session.Document;

    /// <summary>How long changes coalesce before a save. 750 ms in the app; shortened by tests.</summary>
    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    ///     Whether the document is written to its sidecar automatically — the live face of
    ///     <c>AppSettings.Playback2D.AnnotationAutoSave</c>.
    ///     <para>
    ///         D6 finding 26: the key was READ here and written by nothing, with no UI anywhere, so a
    ///         user who wanted session-only ink had to hand-edit <c>settings.json</c> — and every reader
    ///         of the key only ever saw its default. It is surfaced rather than deleted because the
    ///         branch it drives is correct behaviour somebody wants (a clean demo folder, a shared
    ///         machine); what was missing was one checkbox, not the feature.
    ///     </para>
    ///     <para>
    ///         Held here, not re-read from settings per change, because the panel writes it through
    ///         <see cref="PersistSettings" />'s debounce like every other preference — a per-change read
    ///         would race the 250 ms window and answer with the pre-toggle value.
    ///     </para>
    /// </summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>
    ///     Whether a sidecar could be written at all right now — a demo is attached, a store exists, the
    ///     host has a real filesystem, and the store resolved a path. False makes the auto-save toggle
    ///     meaningless, and a checkbox that claims to control saving where nothing can be saved is the
    ///     same defect one layer down.
    /// </summary>
    public bool CanAutoSave => SidecarPath() is not null;

    /// <summary>Whether the <c>playback2d.annotations</c> feature is on. Fails OPEN on a null projection.</summary>
    public bool IsEnabled => _features?.IsEnabled(FeatureId) ?? true;

    /// <summary>One line for the panel: where annotations are saved, or why they are not.</summary>
    public string StatusText { get; private set; }

    /// <summary>
    ///     True when the loaded sidecar was authored against a different parse. Static elements are
    ///     unaffected; time anchors may be off, and the panel says so rather than the app pretending.
    /// </summary>
    public bool ClockMismatch { get; private set; }

    /// <summary>True when a load found a sidecar belonging to a different demo, and ignored it.</summary>
    public bool DemoMismatch { get; private set; }

    /// <summary>How many saves have completed. Test hook.</summary>
    public int SaveCount { get; private set; }

    /// <summary>Raised when <see cref="StatusText" /> or the document changed. May fire off the UI thread.</summary>
    public event Action? StateChanged;

    /// <summary>
    ///     Points the controller at the shell's live gate projection. Re-resolved on the gate's
    ///     <c>Changed</c>, never cached for the tab's lifetime.
    /// </summary>
    /// <param name="features">The projection, or null (fails open).</param>
    public void SetFeatures(IModuleFeatureGate? features)
    {
        if (ReferenceEquals(_features, features))
        {
            return;
        }

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
        }

        _features = features;

        if (_features is not null)
        {
            _features.Changed += OnFeaturesChanged;
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    ///     Binds the controller to a demo and loads its sidecar. Flushes whatever was pending for the
    ///     previous demo first, so switching demos never loses a stroke.
    /// </summary>
    /// <param name="demoPath">Path to the <c>.dem</c>, or null to detach.</param>
    /// <param name="clock">The clock the anchors will be authored against.</param>
    /// <param name="force">
    ///     Reload even when already attached to this demo. False on a tab RE-activation — the view is
    ///     rebuilt every time a tab is selected, and reloading there would throw away the in-memory
    ///     document (including anything not yet autosaved) for no reason. True on a demo reload, which is
    ///     the one moment the file on disk really is the newer truth.
    /// </param>
    public async Task AttachDemoAsync(string? demoPath, ClockIdentity clock, bool force = true)
    {
        ArgumentNullException.ThrowIfNull(clock);

        // BEFORE the already-attached early return, and off the CLOCK rather than a second read of the
        // context: ClockIdentity.TickRate is already "ticks per second of the parse the anchors were
        // written against", which is exactly the divisor a RealTime cadence and the toolbar's second-
        // valued spinners need. A second path to the same number is a second thing to get wrong, and this
        // one is already load-bearing — it is what the ClockMismatch warning compares. Unknown carries 0
        // and the setter refuses it, so a detach keeps the last real rate rather than dividing by zero.
        Session.TicksPerSecond = clock.TickRate;

        string? normalized = string.IsNullOrWhiteSpace(demoPath) ? null : demoPath;
        if (!force && _attached
                   && string.Equals(_demoPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await FlushAsync().ConfigureAwait(false);

        _demoPath = normalized;
        _clock = clock;
        _demo = null;
        _attached = true;
        ClockMismatch = false;
        DemoMismatch = false;

        // Loading is not editing. Without this the Reset below raises Changed, which schedules an
        // autosave, which drops an empty .dvann.json next to every demo the user has ever opened.
        _loading = true;
        try
        {
            Session.Document.Reset([]);
            ApplySettings();

            if (_demoPath is null || _store is null || !IsEnabled)
            {
                StatusText = DescribeLocation();
                StateChanged?.Invoke();
                return;
            }

            AnnotationLoadResult result =
                await _store.LoadAsync(_demoPath, clock).ConfigureAwait(false);

            ClockMismatch = result.ClockMismatch;
            DemoMismatch = result.DemoMismatch;
            Session.Document.Reset(result.Elements);
            StatusText = DescribeLocation();
            StateChanged?.Invoke();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    ///     Writes any pending changes now. Called on tab deactivate, on <c>DemoReset</c> and at shutdown.
    ///     Never throws — a failed write becomes a status line.
    /// </summary>
    public async Task FlushAsync()
    {
        CancelDebounce();
        FlushStyleSettings();
        await SaveNowAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     Blocking flush, for the two moments there is no "later": tab deactivation (the shell calls it
    ///     on the way out of <c>MainViewModel.Dispose</c>) and this controller's own disposal.
    ///     <para>
    ///         Deliberately synchronous. A fire-and-forget flush at shutdown races the process exit, and
    ///         losing the stroke someone drew ten seconds before quitting is exactly the failure the
    ///         autosave exists to prevent. Every await inside is <c>ConfigureAwait(false)</c>, so there is
    ///         no context to deadlock on, and the payload is a small JSON file — the same trade
    ///         <c>SettingsService.SaveSession</c> already makes on this thread.
    ///     </para>
    /// </summary>
    public void Flush()
    {
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            // Best-effort by contract (plan decision D12): a failed write is a status line, never an
            // exception thrown out of a tab switch.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Rebases world anchors across a level-set rebuild, including the wet stroke. Consumes no undo
    ///     slot (plan decision D6) and marks the document dirty so the rebase is persisted.
    /// </summary>
    /// <param name="zMinMap">Old quantized level ZMin → new quantized level ZMin.</param>
    public void ApplyLevelRebuild(IReadOnlyDictionary<double, double> zMinMap)
    {
        ArgumentNullException.ThrowIfNull(zMinMap);

        Session.Document.RemapWorldLevels(zMinMap);
        Session.Wet.RemapWorldLevel(zMinMap);
    }

    /// <summary>Reads the ink style, envelope defaults and tool from settings into the session.</summary>
    public void ApplySettings()
    {
        Playback2DSettings prefs = _settings?.Current.Playback2D ?? new Playback2DSettings();

        float width = (float)prefs.AnnotationWidth;
        float opacity = (float)Math.Clamp(prefs.AnnotationOpacity, 0, 1);

        Session.Style = new AnnotationStyle(prefs.AnnotationColorArgb, width, opacity);

        // The two pens share width and opacity by construction — only the colour is per-button — so a
        // user who widens the pen never discovers that the right one stayed thin.
        Session.SecondaryStyle = new AnnotationStyle(prefs.AnnotationSecondaryColorArgb, width, opacity);
        Session.SecondaryTool = ParseSecondaryTool(prefs.AnnotationSecondaryTool);

        // Enum.IsDefined as well as TryParse, for the reason AnnotationStore.ToElement fences its kind:
        // this is a hand-editable string AND Enum.TryParse accepts any NUMBER in range, so "7" would
        // parse to an EnvelopeMode nothing switches on — a session whose mode reaches the toolbar's
        // ComboBox as an out-of-range SelectedIndex and silently deselects. Always is the degrade, which
        // is also what a mode written by a newer build should look like from here.
        Session.DefaultVisibility =
            Enum.TryParse(prefs.AnnotationDefaultVisibility, ignoreCase: true, out EnvelopeMode mode)
            && Enum.IsDefined(mode)
                ? mode
                : EnvelopeMode.Always;

        Session.FadeInTicks = Math.Max(0, prefs.AnnotationFadeInTicks);
        Session.FadeOutTicks = Math.Max(0, prefs.AnnotationFadeOutTicks);
        Session.HoldTicks = Math.Max(0, prefs.AnnotationHoldTicks);

        // AFTER the ramps: the Custom window borrows them, so seeding it first would compose an
        // envelope out of whatever the previous demo's fades happened to be.
        Session.SetCustomWindow(prefs.AnnotationCustomFromTick, prefs.AnnotationCustomUntilTick);

        Session.AnchorToEntities = prefs.AnnotationAnchorToEntities;
        Session.ActiveTool = Enum.TryParse(prefs.LastTool, ignoreCase: true, out ToolKind tool)
            ? tool
            : ToolKind.PanZoom;

        // Not session state (the session knows nothing about files), but it is read on the same path and
        // written on the same debounce as everything above, so it is seeded here with them.
        AutoSave = prefs.AnnotationAutoSave;
    }

    /// <summary>The persisted spelling of "the right button does what the left one does".</summary>
    public const string SecondaryToolSame = "Same";

    // "Same" and every unrecognised string mean "no override" — the right button then runs the selected
    // tool with the secondary ink. PanZoom is refused on purpose: middle and Ctrl+drag already pan under
    // every tool (D2 §2.3), and a third way to pan bound to the button that is supposed to draw would be
    // a setting whose only effect is to take the second pen away.
    private static ToolKind? ParseSecondaryTool(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out ToolKind kind) && kind != ToolKind.PanZoom
            ? kind
            : null;

    /// <summary>
    ///     How long preference changes coalesce before <c>settings.json</c> is rewritten. 250 ms in the
    ///     app; shortened (or zeroed, which writes inline) by tests.
    /// </summary>
    public TimeSpan StylePersistDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Persists the current ink style, envelope defaults and tool. Best-effort, and <b>debounced</b>.
    ///     <para>
    ///         <b>Why it cannot write inline.</b> <c>SettingsService.Write</c> is a synchronous
    ///         read-serialize-temp-write-<c>File.Move</c>-<c>Reload()</c>, and the reload fires
    ///         <c>IOptionsMonitor.OnChange</c> on this thread — which re-composes the 2D keymap profile
    ///         and, with Settings open, re-reflects thirty properties and twenty-one keybind rows. This
    ///         method's loudest caller is a <c>ColorPicker</c> drag, which raises a change on <i>every
    ///         pointer move through its spectrum</i> — the same fact <see cref="RememberNewestColor" />
    ///         was written around, applied to the swatch list and not to the file. A one-second drag was
    ///         a few hundred full cycles on the UI thread.
    ///     </para>
    ///     <para>
    ///         The snapshot is taken HERE, on the calling thread, because the session is UI-thread state;
    ///         only the write is deferred, and the last snapshot wins.
    ///     </para>
    /// </summary>
    public void PersistSettings()
    {
        if (_settings is null)
        {
            return;
        }

        lock (_styleGate)
        {
            _pendingStyle = CaptureStyle();
        }

        CancelStylePersist();

        if (StylePersistDelay <= TimeSpan.Zero)
        {
            WritePendingStyle();
            return;
        }

        CancellationTokenSource cts = new();
        _stylePersist = cts;
        _ = DelayThenPersistStyleAsync(cts.Token);
    }

    /// <summary>
    ///     Writes any debounced preference change NOW. Called from <see cref="FlushAsync" /> and
    ///     <see cref="Dispose" /> — the two moments there is no "later".
    /// </summary>
    public void FlushStyleSettings()
    {
        CancelStylePersist();
        WritePendingStyle();
    }

    private readonly Lock _styleGate = new();
    private CancellationTokenSource? _stylePersist;
    private StyleSnapshot? _pendingStyle;

    private StyleSnapshot CaptureStyle() => new(
        Session.Style,
        Session.DefaultVisibility,
        Session.ActiveTool,
        Session.AnchorToEntities,
        Session.FadeInTicks,
        Session.FadeOutTicks,
        Session.HoldTicks,
        Session.SecondaryStyle.ColorArgb,
        Session.SecondaryTool?.ToString() ?? SecondaryToolSame,
        Session.NewElementEnvelope,
        [.. RecentColors],
        AutoSave);

    private async Task DelayThenPersistStyleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StylePersistDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        WritePendingStyle();
    }

    private void WritePendingStyle()
    {
        StyleSnapshot? pending;
        lock (_styleGate)
        {
            pending = _pendingStyle;
            _pendingStyle = null;
        }

        if (_settings is null || pending is not { } style)
        {
            return;
        }

        try
        {
            _settings.Write(s =>
            {
                s.Playback2D.AnnotationColorArgb = style.Style.ColorArgb;
                s.Playback2D.AnnotationWidth = style.Style.WidthWorld;
                s.Playback2D.AnnotationOpacity = style.Style.Opacity;
                s.Playback2D.AnnotationDefaultVisibility = style.Visibility.ToString();
                s.Playback2D.AnnotationFadeInTicks = style.FadeInTicks;
                s.Playback2D.AnnotationFadeOutTicks = style.FadeOutTicks;
                s.Playback2D.AnnotationHoldTicks = style.HoldTicks;
                s.Playback2D.AnnotationAnchorToEntities = style.AnchorToEntities;
                s.Playback2D.LastTool = style.Tool.ToString();
                s.Playback2D.AnnotationRecentColors = style.RecentColors;
                s.Playback2D.AnnotationSecondaryColorArgb = style.SecondaryColorArgb;
                s.Playback2D.AnnotationSecondaryTool = style.SecondaryTool;

                // The WRITER AnnotationAutoSave shipped without (D6 finding 26). The key had a reader
                // and a WriteInMemory row from B2, so it round-tripped perfectly and could never change.
                s.Playback2D.AnnotationAutoSave = style.AutoSave;

                // The window is read back OFF the composed envelope, so what is persisted is what the
                // renderer will actually honour — including PinnedTo's clamp of an inverted window.
                s.Playback2D.AnnotationCustomFromTick = style.Custom.FromTick ?? 0;
                s.Playback2D.AnnotationCustomUntilTick = style.Custom.UntilTick ?? 0;
            });
        }
        catch (IOException)
        {
            // Preferences are best-effort, exactly as SettingsService.SaveSession is: a failed write
            // must never surface as an exception from a colour-picker click.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void CancelStylePersist()
    {
        CancellationTokenSource? cts = _stylePersist;
        _stylePersist = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    // Everything a preference write needs, copied off the session at the moment the user changed it.
    // A record STRUCT so a coalesced drag allocates one box per snapshot rather than a class per sample.
    private readonly record struct StyleSnapshot(
        AnnotationStyle Style,
        EnvelopeMode Visibility,
        ToolKind Tool,
        bool AnchorToEntities,
        int FadeInTicks,
        int FadeOutTicks,
        int HoldTicks,
        uint SecondaryColorArgb,
        string SecondaryTool,
        TimeEnvelope Custom,
        string[] RecentColors,
        bool AutoSave);

    /// <summary>How many swatches the strip keeps. Eight fits one toolbar line at 820 px.</summary>
    public const int MaxRecentColors = 8;

    /// <summary>Recently used ink colours, newest first, as <c>#AARRGGBB</c>. Capped at eight.</summary>
    public IReadOnlyList<string> RecentColors => _recentColors;

    /// <summary>
    ///     Bumped whenever <see cref="RecentColors" /> changed. The panel rebuilds its swatch collection
    ///     off this rather than off every <see cref="StateChanged" />, which also fires on each stroke.
    /// </summary>
    public int RecentColorsVersion { get; private set; }

    private readonly List<string> _recentColors = [];

    /// <summary>Records a colour as recently used and moves it to the front.</summary>
    /// <param name="argb">Packed ARGB.</param>
    /// <returns>True when the list actually changed — a colour already at the front changes nothing.</returns>
    public bool RememberColor(uint argb)
    {
        string text = "#" + argb.ToString("X8", CultureInfo.InvariantCulture);
        if (_recentColors.Count > 0 && string.Equals(_recentColors[0], text, StringComparison.Ordinal))
        {
            return false;
        }

        _recentColors.Remove(text);
        _recentColors.Insert(0, text);
        while (_recentColors.Count > MaxRecentColors)
        {
            _recentColors.RemoveAt(_recentColors.Count - 1);
        }

        RecentColorsVersion++;
        return true;
    }

    /// <summary>Seeds the recent-colour list from settings.</summary>
    public void LoadRecentColors()
    {
        _recentColors.Clear();
        foreach (string colour in _settings?.Current.Playback2D.AnnotationRecentColors ?? [])
        {
            if (!string.IsNullOrWhiteSpace(colour))
            {
                _recentColors.Add(colour);
            }
        }

        RecentColorsVersion++;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // A colour drag that ended a debounce-window ago still has its write parked. Before the flag,
        // because this is the last chance the preference has.
        FlushStyleSettings();

        _disposed = true;
        Session.Document.Changed -= OnDocumentChanged;

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
            _features = null;
        }

        CancelDebounce();
    }

    private void OnFeaturesChanged() => StateChanged?.Invoke();

    // "Recent" means recently DRAWN WITH, not recently hovered in the picker. A ColorPicker raises a
    // change on every pointer move through its spectrum, so remembering there filled the strip with
    // eight shades of one drag and buried the colours the user had actually committed to. One new
    // element is one use — of whichever pen drew it, which is how the secondary ink earns its place in
    // the strip too. A LOAD is not a use, and neither is a batch: only a single-element growth counts.
    private void RememberNewestColor()
    {
        int count = Session.Document.Elements.Count;
        int previous = _lastElementCount;
        _lastElementCount = count;

        if (_loading || count != previous + 1)
        {
            return;
        }

        if (RememberColor(Session.Document.Elements[^1].Style.ColorArgb))
        {
            PersistSettings();
        }
    }

    private int _lastElementCount;

    private void OnDocumentChanged()
    {
        RememberNewestColor();
        StateChanged?.Invoke();

        if (_disposed || _loading || _demoPath is null || _store is null || !IsEnabled)
        {
            return;
        }

        if (!AutoSave)
        {
            return; // cheap: do not even arm a timer SaveNowAsync would refuse.
        }

        ScheduleSave();
    }

    private void ScheduleSave()
    {
        CancelDebounce();

        CancellationTokenSource cts = new();
        _debounce = cts;
        _ = DelayThenSaveAsync(cts.Token);
    }

    private async Task DelayThenSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveNowAsync(ct).ConfigureAwait(false);
    }

    private async Task SaveNowAsync(CancellationToken ct)
    {
        if (_disposed || _demoPath is null || _store is null || !IsEnabled || DemoMismatch)
        {
            return;
        }

        // The AUTHORITATIVE auto-save check, and deliberately here rather than only at the schedule.
        // Every automatic write funnels through this method — the debounce, FlushAsync on a demo swap or
        // a tab deactivate, and the blocking flush at shutdown — and only the first of those was gated
        // before. "Session only" that still writes the sidecar when you close the tab is not session
        // only; it is the same file arriving at a moment the user is even less likely to notice.
        if (!AutoSave)
        {
            return;
        }

        // Snapshot the elements before waiting for the gate: the document is UI-thread state, and
        // handing the live list to an async write would race the next stroke. The version stamp taken
        // with it is what lets a slower writer stand down instead of putting a stale document on disk.
        AnnotationElement[] elements = [.. Session.Document.Elements];
        int version = Session.Document.Version;
        string demoPath = _demoPath;
        ClockIdentity clock = _clock;

        try
        {
            await _saveSerializer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || version < _lastSavedVersion)
            {
                return; // a newer snapshot already reached the disk; this one would undo it
            }

            // Nothing to say, and nothing already on disk to correct: opening a demo must not litter a
            // .dvann.json beside it. An EXISTING sidecar is still rewritten when the last stroke is
            // erased — that is the user clearing their annotations, and it has to stick.
            string? target = _store.ResolvePath(demoPath);
            if (elements.Length == 0 && (target is null || !File.Exists(target)))
            {
                return;
            }

            DemoIdentity demo;
            lock (_saveGate)
            {
                _demo ??= AnnotationStore.IdentityFor(demoPath);
                demo = _demo;
            }

            bool saved = await _store.SaveAsync(demoPath, demo, clock, elements, ct)
                .ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (saved)
            {
                _lastSavedVersion = version;
            }

            SaveCount++;
            StatusText = saved
                ? "saved to " + (_store.ResolvePath(demoPath) ?? "?")
                : "annotations could not be saved — session only";
            StateChanged?.Invoke();
        }
        finally
        {
            _saveSerializer.Release();
        }
    }

    private void CancelDebounce()
    {
        CancellationTokenSource? cts = _debounce;
        _debounce = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    // Where this demo's sidecar would go, or null when there is nowhere for one. The single answer both
    // DescribeLocation and CanAutoSave read, so the status line and the auto-save toggle can never
    // disagree about whether a file is possible.
    private string? SidecarPath()
    {
        if (!IsEnabled || _demoPath is null || _store is null || _isBrowser())
        {
            return null;
        }

        return _store.ResolvePath(_demoPath);
    }

    /// <summary>
    ///     Re-derives <see cref="StatusText" /> from the current state and notifies. The auto-save
    ///     toggle needs it: the line names a destination, and flipping the toggle changes whether that
    ///     destination is a promise — with no document change to ride in on.
    /// </summary>
    public void RefreshStatus()
    {
        StatusText = DescribeLocation();
        StateChanged?.Invoke();
    }

    // Reads the RETAINED mismatch flags rather than a load result: both are assigned from the result
    // immediately before this is called on the load path, and reset to false on every attach, so there
    // is exactly one source for them — which is what lets RefreshStatus above exist without a result.
    private string DescribeLocation()
    {
        if (!IsEnabled)
        {
            return "annotations are switched off";
        }

        if (_demoPath is null || _store is null)
        {
            return "session only — annotations are not saved";
        }

        if (DemoMismatch)
        {
            return "an existing sidecar belongs to a different demo — it will not be touched";
        }

        // The browser head has no filesystem: System.IO writes land in the WASM runtime's in-memory
        // virtual FS, which is real enough that the store finds a "writable" path and reports it — and
        // gone the instant the tab reloads. Naming a path there is worse than saying nothing, because
        // the user reads it as a promise. Design §8 asks for exactly this sentence: annotations work in
        // session, a reload loses them, AND THE UI SAYS SO. Found by B5's WASM verification pass, which
        // is what happens when somebody finally opens the published head with a demo in it.
        if (_isBrowser())
        {
            return "session only — this browser tab forgets annotations when it reloads";
        }

        string? path = SidecarPath();
        if (path is null)
        {
            return "session only — no writable location for a sidecar";
        }

        string prefix = ClockMismatch
            ? "loaded from a different parse — time anchors may be off · "
            : "";

        // With the toggle off nothing reaches that path at all — not on a stroke, not on a demo swap,
        // not at shutdown. Leaving "saving to <path>" standing would be a promise the setting revoked,
        // which is the shape of defect this whole audit is about.
        return prefix + (AutoSave ? "saving to " : "auto-save off · would save to ") + path;
    }
}
