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

    /// <summary>Creates a controller. Every dependency is optional so a headless test needs no container.</summary>
    /// <param name="store">The sidecar store, or null to run session-only.</param>
    /// <param name="settings">The app settings service, or null to use the built-in defaults.</param>
    public AnnotationSessionController(AnnotationStore? store, SettingsService? settings)
    {
        _store = store;
        _settings = settings;

        Session = new AnnotationSession(new AnnotationDocument());
        ApplySettings();
        Session.Document.Changed += OnDocumentChanged;
        StatusText = DescribeLocation(null);
    }

    /// <summary>The session the tools, the layer and the panel share.</summary>
    public AnnotationSession Session { get; }

    /// <summary>The document being edited.</summary>
    public AnnotationDocument Document => Session.Document;

    /// <summary>How long changes coalesce before a save. 750 ms in the app; shortened by tests.</summary>
    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromMilliseconds(750);

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
                StatusText = DescribeLocation(null);
                StateChanged?.Invoke();
                return;
            }

            AnnotationLoadResult result =
                await _store.LoadAsync(_demoPath, clock).ConfigureAwait(false);

            ClockMismatch = result.ClockMismatch;
            DemoMismatch = result.DemoMismatch;
            Session.Document.Reset(result.Elements);
            StatusText = DescribeLocation(result);
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

        Session.Style = new AnnotationStyle(
            prefs.AnnotationColorArgb,
            (float)prefs.AnnotationWidth,
            (float)Math.Clamp(prefs.AnnotationOpacity, 0, 1));

        Session.DefaultVisibility =
            Enum.TryParse(prefs.AnnotationDefaultVisibility, ignoreCase: true, out EnvelopeMode mode)
                ? mode
                : EnvelopeMode.Always;

        Session.FadeInTicks = Math.Max(0, prefs.AnnotationFadeInTicks);
        Session.FadeOutTicks = Math.Max(0, prefs.AnnotationFadeOutTicks);
        Session.HoldTicks = Math.Max(0, prefs.AnnotationHoldTicks);
        Session.AnchorToEntities = prefs.AnnotationAnchorToEntities;
        Session.ActiveTool = Enum.TryParse(prefs.LastTool, ignoreCase: true, out ToolKind tool)
            ? tool
            : ToolKind.PanZoom;
    }

    /// <summary>Persists the current ink style, envelope defaults and tool. Best-effort.</summary>
    public void PersistSettings()
    {
        if (_settings is null)
        {
            return;
        }

        AnnotationStyle style = Session.Style;
        EnvelopeMode visibility = Session.DefaultVisibility;
        ToolKind tool = Session.ActiveTool;
        bool anchor = Session.AnchorToEntities;
        int fadeIn = Session.FadeInTicks;
        int fadeOut = Session.FadeOutTicks;
        int hold = Session.HoldTicks;

        try
        {
            _settings.Write(s =>
            {
                s.Playback2D.AnnotationColorArgb = style.ColorArgb;
                s.Playback2D.AnnotationWidth = style.WidthWorld;
                s.Playback2D.AnnotationOpacity = style.Opacity;
                s.Playback2D.AnnotationDefaultVisibility = visibility.ToString();
                s.Playback2D.AnnotationFadeInTicks = fadeIn;
                s.Playback2D.AnnotationFadeOutTicks = fadeOut;
                s.Playback2D.AnnotationHoldTicks = hold;
                s.Playback2D.AnnotationAnchorToEntities = anchor;
                s.Playback2D.LastTool = tool.ToString();
                s.Playback2D.AnnotationRecentColors = [.. RecentColors];
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

    /// <summary>Recently used ink colours, newest first, as <c>#AARRGGBB</c>. Capped at eight.</summary>
    public IReadOnlyList<string> RecentColors => _recentColors;

    private readonly List<string> _recentColors = [];

    /// <summary>Records a colour as recently used and moves it to the front.</summary>
    /// <param name="argb">Packed ARGB.</param>
    public void RememberColor(uint argb)
    {
        string text = "#" + argb.ToString("X8", CultureInfo.InvariantCulture);
        _recentColors.Remove(text);
        _recentColors.Insert(0, text);
        while (_recentColors.Count > 8)
        {
            _recentColors.RemoveAt(_recentColors.Count - 1);
        }
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
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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

    private void OnDocumentChanged()
    {
        StateChanged?.Invoke();

        if (_disposed || _loading || _demoPath is null || _store is null || !IsEnabled)
        {
            return;
        }

        if (_settings?.Current.Playback2D.AnnotationAutoSave == false)
        {
            return;
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

    private string DescribeLocation(AnnotationLoadResult? result)
    {
        if (!IsEnabled)
        {
            return "annotations are switched off";
        }

        if (_demoPath is null || _store is null)
        {
            return "session only — annotations are not saved";
        }

        if (result?.DemoMismatch == true)
        {
            return "an existing sidecar belongs to a different demo — it will not be touched";
        }

        string? path = _store.ResolvePath(_demoPath);
        if (path is null)
        {
            return "session only — no writable location for a sidecar";
        }

        string prefix = result?.ClockMismatch == true
            ? "loaded from a different parse — time anchors may be off · "
            : "";

        return prefix + "saving to " + path;
    }
}
