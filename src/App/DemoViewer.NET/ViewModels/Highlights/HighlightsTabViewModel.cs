#region

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.LiveSync;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.ViewModels.Highlights;

/// <summary>
///     The <b>Reels dashboard</b>: what the Highlights tab
///     became. Left: the ordered, provenance-bearing <b>clip tray</b>. Right: the promoted reel
///     <b>configuration pane</b> (<see cref="ReelConfig" />). Below: the inline job strip and the enrichment
///     slot. The tab is an <em>authoring</em> surface now, not a browser.
///     <para>
///         <b>What went away and where it went.</b> The library-wide demo card grid is gone. Per-game
///         exploration moved to Match Overview (its highlight section owns Verify-live, the per-row
///         <c>[ + ]</c>, staleness and failed-retry); cross-demo assembly moved to the <c>Add clips…</c>
///         picker (<see cref="AddClipsPickerViewModel" />, rendered as an overlay over this tab). The card
///         grid's library-wide scan progress moved to <see cref="HighlightScanStatusViewModel" />, the fourth
///         <c>StatusChip</c> consumer. The card grid's chunked <c>CardRow</c> machinery existed only because
///         <c>WrapPanel</c> has no virtualizing counterpart, a constraint that disappears with the grid.
///     </para>
///     <para>
///         <b>The tray is today's <c>_selection</c>, promoted.</b> Same <see cref="HighlightKey" />-keyed
///         dictionary (O(1) <see cref="IsStaged" />, cross-demo stable, never cleared on demo switch), now
///         paired with an explicit ORDER list and rendered. Order is held here, not on the config pane's
///         group view-models, because those are rebuilt on every lead-in keystroke.
///     </para>
///     <para>
///         <b>Delegate-injected (Library precedent).</b> The VM owns no engine: it reads the unified
///         <see cref="DemoCacheStore" />, drives the <see cref="HighlightScanService" />, and reaches
///         shell behaviours through delegates. All state lives here so it survives the framework's
///         view-teardown on tab switch.
///     </para>
/// </summary>
public partial class HighlightsTabViewModel : ObservableObject, IWorkspaceTabViewModel, IClipTrayHost
{
    private const double NarrowBreakpoint = 760;

    // CASE-INSENSITIVE ON PURPOSE. Today the shell writes module state with
    // JsonSerializer.SerializeToElement(state) and SettingsService reads the section with options that set no
    // naming policy, so both sides say "StagedClips" and a default read matches. That agreement is INCIDENTAL:
    // one `JsonSerializerDefaults.Web` added anywhere on the write path silently renames every property to
    // camelCase, this binds nothing, and the tray restores to empty with no error anywhere: the exact silent
    // loss this whole path exists to prevent. One flag makes the read immune to that.
    private static readonly JsonSerializerOptions _restoreOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DemoCacheStore _demoCache;

    private readonly IFeatureGate? _featureGate;

    // Canonical tray ORDER. Paired with _selection rather than folded into it: a Dictionary gives the O(1)
    // IsStaged that Match Overview's [ + ] buttons need on every row build, and a List gives the sequence the
    // rendered reel is emitted in. Keeping both in sync is three lines; deriving either from the other is not.
    private readonly List<HighlightKey> _order = [];

    private readonly HighlightScanService _scanner;

    // The staged set, keyed for cross-rebuild / cross-demo stability. The VALUE bundles the owning cache row
    // so the config pane needs nothing else.
    private readonly Dictionary<HighlightKey, HighlightSelection> _selection = [];

    private readonly SettingsService? _settingsService;

    /// <summary>Config-pane column star weight (splitter position).</summary>
    [ObservableProperty]
    private GridLength _configColumnWidth = new(1, GridUnitType.Star);

    // ── Responsive master-detail ──────────────────────────────────────────────

    [ObservableProperty]
    private bool _isNarrow;

    private ReelJobStatusViewModel? _jobStatus;

    private AddClipsPickerViewModel? _picker;

    // Index rows only: the tray no longer holds every demo's events resident. Highlight COUNTS live on
    // the index entry precisely so this stays cheap; the fat sidecars are read one demo at a time.
    private IReadOnlyList<DemoCacheIndexEntry> _rows = [];

    private HighlightScanStatusViewModel? _scanStatus;

    /// <summary>The destructive-action guard rail for <c>Clear tray</c> (an inline confirm, never a modal).</summary>
    [ObservableProperty]
    private bool _showClearConfirm;

    /// <summary>Narrow layout: the config pane is drilled into (the tray is the landing pane).</summary>
    [ObservableProperty]
    private bool _showConfigPane;

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>Tray column star weight: the CONTENT-DENSE pane here, so the weights invert.</summary>
    [ObservableProperty]
    private GridLength _trayColumnWidth = new(1.4, GridUnitType.Star);

    /// <summary>
    ///     Builds the dashboard over the highlights cache. The four Verify/open-in-workspace delegates the
    ///     browser tab used to take are gone: those surfaces re-homed to Match Overview, and the
    ///     composition root no longer passes them.
    /// </summary>
    /// <param name="demoCache">The unified per-demo cache: tier 3 is where highlights now live.</param>
    /// <param name="scanner">The background scan/backfill service.</param>
    /// <param name="settings">Live app settings (reel defaults seed the config pane).</param>
    /// <param name="settingsService">Settings writer: the scan opt-in and the reel "set once" defaults.</param>
    /// <param name="reelJob">
    ///     The background reel service the config pane hands off to. Null on the browser host and in tests.
    /// </param>
    /// <param name="isLiveSyncSessionActive">The single-CS2 interlock probe.</param>
    /// <param name="dryRunOnly">Platform mode: macOS renders a labelled dry run only.</param>
    /// <param name="fileExists">Demo-existence predicate for the staging-time pre-flight (tests inject it).</param>
    /// <param name="featureGate">
    ///     Resolves the <c>highlights.encoding</c> SubFeature (<c>Defaults(false, true, true)</c>) onto
    ///     <c>ReelConfig.IsEncodingVisible</c>. Null (tests / capture) leaves the encoder section visible.
    /// </param>
    /// <param name="ffmpegLocator">
    ///     ffmpeg presence probe for the reel pre-flight (the App passes <c>FfmpegDependency.Locate</c>).
    ///     Null (tests) = assume present, keeping the plan tests machine-independent.
    /// </param>
    public HighlightsTabViewModel(
        DemoCacheStore demoCache,
        HighlightScanService scanner,
        IOptionsMonitor<AppSettings>? settings = null,
        SettingsService? settingsService = null,
        IReelJobService? reelJob = null,
        Func<bool>? isLiveSyncSessionActive = null,
        bool dryRunOnly = false,
        Func<string, bool>? fileExists = null,
        IFeatureGate? featureGate = null,
        Func<FfmpegStatus>? ffmpegLocator = null)
    {
        _demoCache = demoCache;
        _scanner = scanner;
        _settingsService = settingsService;
        _featureGate = featureGate;

        AppSettings current = settings?.CurrentValue ?? new AppSettings();
        ReelConfig = new HighlightReelDialogViewModel(
            [], current.Highlights, reelJob,
            settingsService is null ? null : mutate => settingsService.Write(mutate),
            isLiveSyncSessionActive, dryRunOnly, fileExists,
            ffmpegLocator)
        {
            Tray = this
        };

        _demoCache.Changed += OnStoreChanged;
        _scanner.ScanProgressChanged += OnScanProgressChanged;

        // Careful: a GATE axis, not a LOAD axis. Skeleton-first forbids toggling a section because a parse
        // finished; a feature gate is user-initiated and stable for a whole load, and it must RE-RECONCILE.
        // Hence the Changed subscription rather than a one-shot read the user's Settings toggle can't reach.
        if (_featureGate is not null)
        {
            _featureGate.Changed += OnFeatureGateChanged;
            ApplyEncodingGate();
        }

        // Self-notifying, so a composition-time registration cannot silently render a zero-height slot
        // because someone forgot the explicit raise. The failure mode was invisible by construction.
        EnrichmentSections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEnrichments));

        Reproject();
    }

    // ── The clip tray ─────────────────────────────────────────────────────────

    /// <summary>
    ///     The promoted reel configuration (padding / display / output / encoding / pre-flight) AND the
    ///     computed clip plan the tray renders. One computation, one truth. See its own doc comment.
    /// </summary>
    public HighlightReelDialogViewModel ReelConfig { get; }

    /// <summary>The staged highlights, IN TRAY ORDER. The exact shape the config pane consumes.</summary>
    public IReadOnlyList<HighlightSelection> StagedSelections =>
        [.. _order.Select(k => _selection[k])];

    /// <summary>How many highlights are staged (pre-coalescing).</summary>
    public int StagedCount => _order.Count;

    /// <summary>True once anything is staged.</summary>
    public bool HasStagedClips => _order.Count > 0;

    // ── Job strip + enrichments ───────────────────────────────────────────────

    /// <summary>
    ///     The SAME <see cref="ReelJobStatusViewModel" /> the status-strip chip is bound to (the inline
    ///     strip is a second view of one job, never a second job model). Shell-owned and
    ///     <see cref="IDisposable" />. This VM subscribes but must never dispose it.
    /// </summary>
    public ReelJobStatusViewModel? JobStatus
    {
        get => _jobStatus;
        set
        {
            if (ReferenceEquals(_jobStatus, value))
            {
                return;
            }

            if (_jobStatus is not null)
            {
                _jobStatus.PropertyChanged -= OnJobStatusPropertyChanged;
            }

            _jobStatus = value;
            if (_jobStatus is not null)
            {
                _jobStatus.PropertyChanged += OnJobStatusPropertyChanged;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowJobStrip));
        }
    }

    /// <summary>
    ///     The library-wide scan chip's view-model (the FOURTH <c>StatusChip</c> consumer),
    ///     assigned by the shell exactly as <see cref="JobStatus" /> is: constructed and owned there, added
    ///     to <c>MainViewModel.Chips</c> there, never disposed here.
    ///     <para>
    ///         It differs from <see cref="JobStatus" /> in one way worth stating, because the "never a
    ///         second job model" rule does NOT apply: <see cref="HighlightScanStatusViewModel" /> is a pure
    ///         projection of the scanner + cache and holds no job state, so a shell that builds its own
    ///         instance (so the chip exists before this tab is first activated) cannot drift from this one.
    ///     </para>
    /// </summary>
    public HighlightScanStatusViewModel? ScanStatus
    {
        get => _scanStatus;
        set
        {
            if (ReferenceEquals(_scanStatus, value))
            {
                return;
            }

            // No PropertyChanged subscription: with the transitional badge gone the tab renders nothing
            // derived from this mapper. The status-strip chip does, and the shell drives that.
            _scanStatus = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     The inline job strip shows only while a job exists. Without the PropertyChanged subscription in
    ///     <see cref="JobStatus" /> this would be evaluated once at bind time and then be permanently right or
    ///     permanently wrong: a strip that never appears, or never leaves.
    /// </summary>
    public bool ShowJobStrip => _jobStatus is not null && !_jobStatus.IsIdle;

    /// <summary>
    ///     The enrichment slot. An enrichment is a <c>ViewModelBase</c> appended HERE AT COMPOSITION plus a
    ///     matching View: zero edits to this file or the view. Registering mid-run is forbidden: the slot is
    ///     in the tree from frame one and renders zero height when empty, and a section that appears later is
    ///     exactly the layout jump the redesign exists to prevent.
    /// </summary>
    public ObservableCollection<object> EnrichmentSections { get; } = [];

    /// <summary>Gates the enrichment container so an empty collection costs zero height.</summary>
    public bool HasEnrichments => EnrichmentSections.Count > 0;

    // ── Scan / empty states ───────────────────────────────────────────────────

    /// <summary>Reel authoring needs a real filesystem and a local CS2: desktop only (WASM degrades).</summary>
    public bool CanScan { get; } = !OperatingSystem.IsBrowser();

    /// <summary>WASM: the tab is registered-but-degraded. An explanatory body replaces the dashboard.</summary>
    public bool IsBrowser { get; } = OperatingSystem.IsBrowser();

    // Library-wide scan progress lives in the status-strip chip now (HighlightScanStatusViewModel),
    // visible from every tab, which is the point: a background sweep is not a thing you should have to open
    // the Reels tab to notice. The tab's own transitional badge and its ScanQueueSummary / ShowScanChip /
    // ShowScanBadge / HasScanStatus backing went with it once the shell registered that chip.

    /// <summary>The dashboard is the authoring surface: hidden only on the browser host.</summary>
    public bool ShowBrowseSurface => !IsBrowser;

    /// <summary>WASM degraded note: the tab's purpose is now authoring, wholly absent in a browser.</summary>
    public bool ShowWasmNote => IsBrowser;

    /// <summary>Nothing staged: the tray's own empty state (never the library's).</summary>
    public bool ShowEmptyTray => !HasStagedClips && !IsBrowser;

    /// <summary>
    ///     A trap worth naming: "no clips staged" and "library not indexed" are DIFFERENT emptinesses. The
    ///     primary copy is always about the tray; this secondary line appears only when no demo anywhere has a
    ///     usable highlight record, because only then is scanning the user's actual next step.
    /// </summary>
    public bool ShowLibraryNotIndexedLine => ShowEmptyTray && CanScan && !AnyHighlightsIndexed;

    /// <summary>The "Scan my library" CTA is offered only when nothing is already scanning.</summary>
    public bool ShowScanCta => CanScan && !_scanner.IsScanning && _scanner.QueueLength == 0;

    /// <summary>Any demo in the cache carries at least one harvested highlight (a usable T3 record).</summary>
    public bool AnyHighlightsIndexed => _rows.Any(r => r.HighlightCount > 0);

    // ── Layout flags (the shipped master-detail pattern, weights inverted) ────

    /// <summary>Column span of the tray pane: full (3) when narrow, else 1.</summary>
    public int TrayColumnSpan => IsNarrow ? 3 : 1;

    /// <summary>Config pane's grid column: 0 when narrow (it replaces the tray), else 2.</summary>
    public int ConfigColumn => IsNarrow ? 0 : 2;

    /// <summary>Config pane's column span.</summary>
    public int ConfigColumnSpan => IsNarrow ? 3 : 1;

    /// <summary>Tray visible when wide, or narrow-and-not-drilled-in.</summary>
    public bool TrayVisible => !IsNarrow || !ShowConfigPane;

    /// <summary>Config pane visible when wide, or narrow-and-drilled-in.</summary>
    public bool ConfigVisible => !IsNarrow || ShowConfigPane;

    /// <summary>The GridSplitter shows only in the wide two-column layout.</summary>
    public bool SplitterVisible => !IsNarrow;

    /// <summary>"◀ Back to clips" shows only in the narrow drilled-in state.</summary>
    public bool ShowBackButton => IsNarrow && ShowConfigPane;

    /// <summary>The narrow-layout "Reel settings ▸" affordance.</summary>
    public bool ShowConfigButton => IsNarrow && !ShowConfigPane;

    /// <summary>Drives the inline note's visibility.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>
    ///     Tooltip for <c>Add clips…</c>. It stays ENABLED with nothing indexed: opening an empty
    ///     picker that names the reason beats a disabled button the user cannot interrogate, but the tip
    ///     says so up front.
    /// </summary>
    public string AddClipsHint => AnyHighlightsIndexed
        ? "Pick highlights from across your library"
        : "Nothing in your library has been analysed for highlights yet, so the picker will be empty.";

    /// <summary>
    ///     The open <c>Add clips…</c> picker, or null. Held here rather than in the view so the overlay
    ///     survives the module framework's view teardown on a tab switch, like every other piece of tab state.
    /// </summary>
    public AddClipsPickerViewModel? Picker
    {
        get => _picker;
        set
        {
            if (ReferenceEquals(_picker, value))
            {
                return;
            }

            _picker = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPickerOpen));
        }
    }

    /// <summary>Gates the overlay (and the scrim that swallows clicks on the dashboard behind it).</summary>
    public bool IsPickerOpen => _picker is not null;

    // ── IClipTrayHost (the ▲▼✕ seam) ──────────────────────────────────────────

    /// <inheritdoc />
    public void MoveGroup(string groupKey, int delta)
    {
        List<string> groups = GroupOrder();
        int from = groups.IndexOf(groupKey);
        if (from >= 0)
        {
            MoveGroupTo(groupKey, from + delta);
        }
    }

    /// <inheritdoc />
    public void MoveGroupTo(string groupKey, int targetIndex)
    {
        List<string> groups = GroupOrder();
        int from = groups.IndexOf(groupKey);
        if (from < 0)
        {
            return;
        }

        int to = Math.Clamp(targetIndex, 0, groups.Count - 1);
        if (to == from)
        {
            return;
        }

        groups.RemoveAt(from);
        groups.Insert(to, groupKey);

        // Rewrite the canonical order group-by-group. This NORMALISES the tray to be group-contiguous, which
        // is the property the renderer cares about (ReelJobService reloads the demo whenever clip.DemoPath
        // changes). Relative order INSIDE a group is preserved.
        Dictionary<string, List<HighlightKey>> byGroup = new(StringComparer.Ordinal);
        foreach (HighlightKey key in _order)
        {
            string g = GroupKeyOf(key);
            if (!byGroup.TryGetValue(g, out List<HighlightKey>? bucket))
            {
                bucket = [];
                byGroup[g] = bucket;
            }

            bucket.Add(key);
        }

        _order.Clear();
        foreach (string g in groups)
        {
            _order.AddRange(byGroup.GetValueOrDefault(g, []));
        }

        PushTray();
    }

    /// <inheritdoc />
    public void RemoveGroup(string groupKey)
    {
        List<HighlightKey> doomed = [.. _order.Where(k => GroupKeyOf(k) == groupKey)];
        if (doomed.Count == 0)
        {
            return;
        }

        foreach (HighlightKey key in doomed)
        {
            _selection.Remove(key);
            _order.Remove(key);
        }

        PushTray();
    }

    /// <inheritdoc />
    public void RemoveClip(HighlightKey key) => Unstage(key);

    // ── IWorkspaceTabViewModel ────────────────────────────────────────────────

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        // Tab-activation staleness trigger. Cheap (no parse): reconciles rows against the library and
        // re-fingerprints, then re-projects when the store raises Changed.
        if (CanScan)
        {
            _scanner.RefreshStaleness();
        }

        Reproject();
    }

    /// <inheritdoc />
    public void OnDeactivated()
    {
    }

    /// <inheritdoc />
    public object? SnapshotState() => new HighlightsSessionState
    {
        StagedClips =
        [
            .. _order.Select(k => new StagedClipState
            {
                FilePath = k.FilePath,
                RulesetId = k.RulesetId,
                HighlightId = k.HighlightId,
                Tick = k.Tick,
                PlayerSlot = k.PlayerSlot
            })
        ],
        TrayColumnStars = TrayColumnWidth.IsStar ? TrayColumnWidth.Value : 1.4,
        ConfigColumnStars = ConfigColumnWidth.IsStar ? ConfigColumnWidth.Value : 1.0
    };

    /// <inheritdoc />
    public void RestoreState(object? state)
    {
        // TWO shapes reach here and both are real. The shell round-trips module state through the session
        // FILE, so what comes back is a JsonElement, not the DTO SnapshotState() returned. A direct cast
        // would silently restore nothing and the tray would evaporate on every restart, which is precisely
        // the exact loss this branch exists to prevent. The DTO branch stays because tests (and any in-process
        // save/restore) hand back the object itself.
        HighlightsSessionState? s = state switch
        {
            HighlightsSessionState direct => direct,
            JsonElement element => TryDeserialize(element),
            _ => null
        };

        if (s is null)
        {
            return;
        }

        if (s.TrayColumnStars > 0)
        {
            TrayColumnWidth = new GridLength(s.TrayColumnStars, GridUnitType.Star);
        }

        if (s.ConfigColumnStars > 0)
        {
            ConfigColumnWidth = new GridLength(s.ConfigColumnStars, GridUnitType.Star);
        }

        // Keys are RE-RESOLVED against the live cache, never trusted. A demo can be deleted, re-scanned under
        // a new ruleset, or moved between sessions; a stale key would otherwise resurrect a clip whose window
        // maths (tickRate / tickCount / rounds) no longer exists, and the plan would silently be wrong.
        _selection.Clear();
        _order.Clear();
        int dropped = 0;
        foreach (StagedClipState clip in s.StagedClips ?? [])
        {
            if (!StageQuietly(clip))
            {
                dropped++;
            }
        }

        StatusMessage = dropped == 0
            ? ""
            : $"{dropped} staged clip{(dropped == 1 ? "" : "s")} could not be restored — the demo or its " +
              "highlights are no longer in the cache.";
        PushTray();
    }

    /// <summary>O(1) staged test: Match Overview's <c>[ + ]</c> buttons call this per row build.</summary>
    /// <param name="key">The highlight's identity.</param>
    public bool IsStaged(HighlightKey key) => _selection.ContainsKey(key);

    /// <summary>Stages one highlight at the END of the tray (no-op when already staged).</summary>
    /// <param name="record">The owning demo's cache record.</param>
    /// <param name="highlight">The harvested highlight.</param>
    public void Stage(DemoCacheRecord record, CachedHighlightEvent highlight)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(highlight);
        StageRange([new HighlightSelection(record, highlight)]);
    }

    /// <summary>
    ///     Stages a BATCH at the end of the tray in ONE push: the picker's <c>Add N selected</c> path.
    ///     Batching is not cosmetic: every push re-runs the whole plan (<c>ClipWindows.Compute</c> +
    ///     <c>Coalesce</c>) and rebuilds <c>ClipGroups</c>, so adding twenty clips one at a time would tear
    ///     the tray's containers down twenty times under the user's pointer.
    /// </summary>
    /// <param name="selections">The highlights to stage, in the order they should appear.</param>
    public void StageRange(IReadOnlyList<HighlightSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);

        bool any = false;
        foreach (HighlightSelection selection in selections)
        {
            if (_selection.TryAdd(selection.Key, selection))
            {
                _order.Add(selection.Key);
                any = true;
            }
        }

        if (any)
        {
            PushTray();
        }
    }

    /// <summary>Un-stages one highlight (no-op when it is not staged).</summary>
    /// <param name="key">The highlight's identity.</param>
    public void Unstage(HighlightKey key)
    {
        if (!_selection.Remove(key))
        {
            return;
        }

        _order.Remove(key);
        PushTray();
    }

    /// <summary>Stages or un-stages one highlight and reports the resulting state.</summary>
    /// <param name="record">The owning demo's cache record.</param>
    /// <param name="highlight">The harvested highlight.</param>
    public bool ToggleStaged(DemoCacheRecord record, CachedHighlightEvent highlight)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(highlight);

        HighlightKey key = new(record.Path, highlight.RulesetId, highlight.HighlightId,
            highlight.Tick, highlight.PlayerSlot);
        if (IsStaged(key))
        {
            Unstage(key);
            return false;
        }

        Stage(record, highlight);
        return true;
    }

    /// <summary>
    ///     Stages by identity, resolving the cache record from the store. This is the seam Match Overview's
    ///     <c>[ + ]</c> uses (step 8): that page holds only an identity, and the tray needs the record's
    ///     tickRate / tickCount / rounds to compute a window at all. Returns false when the demo or the
    ///     highlight is no longer cached.
    /// </summary>
    /// <param name="demoPath">Owning demo path.</param>
    /// <param name="rulesetId">Ruleset that emitted the highlight.</param>
    /// <param name="highlightId">Highlight id inside that ruleset.</param>
    /// <param name="tick">Firing tick (frame clock).</param>
    /// <param name="playerSlot">Attributed player slot.</param>
    public bool StageFromCache(string demoPath, string rulesetId, string highlightId, int tick, int playerSlot)
    {
        if (Resolve(new HighlightKey(demoPath, rulesetId, highlightId, tick, playerSlot)) is not { } selection)
        {
            return false;
        }

        StageRange([selection]);
        return true;
    }

    /// <summary>
    ///     Re-raises <see cref="HasEnrichments" />. The constructor already subscribes to the collection, so
    ///     callers do not have to remember this. It stays public only as an explicit escape hatch.
    /// </summary>
    public void NotifyEnrichmentsChanged() => OnPropertyChanged(nameof(HasEnrichments));

    // A session file is user-writable, survives app versions, and predates any field this DTO grows later,
    // so a shape that no longer matches must degrade to "restore nothing", not throw. Activate() wraps this
    // in a try/catch as a backstop; relying on that would cost the user their whole tab restore for one bad
    // property.
    private static HighlightsSessionState? TryDeserialize(JsonElement element)
    {
        try
        {
            return element.Deserialize<HighlightsSessionState>(_restoreOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // Deserialize throws this (not JsonException) for a shape it cannot represent at all. Both must
            // degrade to "restore nothing": catching only one leaves half the failure mode uncovered.
            return null;
        }
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    // ── Feature gate (highlights.encoding) ────────────────────────────────────

    private void OnFeatureGateChanged(object? sender, EventArgs e) => ApplyEncodingGate();

    // CRF / bitrate / FPS / container are OBS-encoder knobs a consumer cannot reason about: the textbook
    // hidden-but-enableable tier. Everything a consumer needs to ship a reel (tray, Default/No-HUD, folder,
    // name, Generate) stays outside this section.
    private void ApplyEncodingGate() =>
        ReelConfig.IsEncodingVisible = _featureGate?.IsEnabled("highlights.encoding") ?? true;

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Opens the cross-demo <c>Add clips…</c> picker as an overlay over the dashboard.
    ///     <para>
    ///         <b>Overlay, not a window.</b> A second window would need <c>IWindowService</c>, the very
    ///         surface the reel modal's retirement is removing, and would be unreachable on the browser
    ///         host. The overlay also keeps the tray visible behind it, which matters: the picker's whole job
    ///         is feeding that tray.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private async Task AddClipsAsync()
    {
        // OFF-THREAD, because this is the one cross-demo read in the app: the unified cache keeps the fat
        // per-demo payloads in lazy sidecars, so building the picker means opening every sidecar that has
        // highlights. Measured on a real 348-demo cache: ~32 ms warm but ~297 ms cold, unnoticeable and a
        // visible hitch respectively, and it scales with the library. Only sidecars the index says carry
        // highlights are opened; the rest cost a dictionary read.
        int libraryRowCount = _demoCache.Count;
        List<DemoCacheRecord> records = await Task.Run(() => _demoCache.LoadRecords(e => e.HighlightCount > 0));

        // Re-opening rebuilds from the CURRENT cache: the picker snapshots rows deliberately (a backfill
        // would otherwise reset scroll and wipe the multi-select mid-assembly), so "re-open to see new
        // highlights" is the documented refresh path, and it has to actually refresh.
        Picker = new AddClipsPickerViewModel(
            records,
            libraryRowCount,
            IsStaged,
            StageRange,
            Unstage,
            () => Picker = null,
            ReelConfig.LeadInSeconds,
            ReelConfig.LeadOutSeconds,
            ReelConfig.DontCrossRoundStart,
            CanScan ? _scanner.RescanAll : null,
            _scanner.QueueLength);
    }

    /// <summary>Dismisses the picker: the scrim click, the ✕, and Escape all land here.</summary>
    [RelayCommand]
    private void ClosePicker() => Picker = null;

    /// <summary>Toolbar "Rescan all".</summary>
    [RelayCommand]
    private void RescanAll()
    {
        if (CanScan)
        {
            _scanner.RescanAll();
        }
    }

    /// <summary>Empty-state "Scan my library": flips the background-scan opt-in on (persisted) and kicks the backfill.</summary>
    [RelayCommand]
    private void ScanLibrary()
    {
        if (!CanScan)
        {
            return;
        }

        _settingsService?.Write(s => s.Highlights.BackgroundScan = true);
        _scanner.RefreshStaleness();
        _scanner.EnsureBackfillRunning();
        RaiseScanState();
    }

    /// <summary>Narrow-layout "◀ Back to clips".</summary>
    [RelayCommand]
    private void BackToTray() => ShowConfigPane = false;

    /// <summary>Narrow-layout "Reel settings ▸".</summary>
    [RelayCommand]
    private void ShowConfig() => ShowConfigPane = true;

    /// <summary>Arms the Clear-tray confirmation (never clears directly: see <see cref="ConfirmClearTray" />).</summary>
    [RelayCommand(CanExecute = nameof(HasStagedClips))]
    private void ClearTray() => ShowClearConfirm = true;

    /// <summary>
    ///     Empties the tray, once confirmed. Confirmed rather than immediate because a 12-clip cross-demo tray
    ///     is minutes of curation with no undo, and the button sits beside Generate.
    /// </summary>
    [RelayCommand]
    private void ConfirmClearTray()
    {
        ShowClearConfirm = false;
        _selection.Clear();
        _order.Clear();
        PushTray();
    }

    /// <summary>Dismisses the Clear-tray confirmation.</summary>
    [RelayCommand]
    private void CancelClearTray() => ShowClearConfirm = false;

    // ── View inputs (code-behind) ─────────────────────────────────────────────

    /// <summary>Sets narrow/wide from the tab's measured width (responsive collapse).</summary>
    /// <param name="width">Measured viewport width in DIPs.</param>
    public void SetViewportWidth(double width) => IsNarrow = width > 0 && width < NarrowBreakpoint;

    partial void OnIsNarrowChanged(bool value)
    {
        // Leaving narrow: both panes show again. Entering narrow: land on the TRAY (the content-dense pane
        // is the landing surface here, the inverse of the browser layout this pattern came from).
        if (!value)
        {
            ShowConfigPane = false;
        }

        RaiseLayoutFlags();
    }

    partial void OnShowConfigPaneChanged(bool value) => RaiseLayoutFlags();

    private void RaiseLayoutFlags()
    {
        OnPropertyChanged(nameof(TrayColumnSpan));
        OnPropertyChanged(nameof(ConfigColumn));
        OnPropertyChanged(nameof(ConfigColumnSpan));
        OnPropertyChanged(nameof(TrayVisible));
        OnPropertyChanged(nameof(ConfigVisible));
        OnPropertyChanged(nameof(SplitterVisible));
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(ShowConfigButton));
    }

    // ── Projection ────────────────────────────────────────────────────────────

    // The unified store names the demo that changed (or null for a batch). The tray re-projects
    // wholesale either way: its own view is library-wide counts plus the staged set, and both can
    // move for any demo, so the argument is accepted and ignored rather than pretended to be used.
    private void OnStoreChanged(string? changedPath) => Reproject();

    private void OnScanProgressChanged() => RaiseScanState();

    private void OnJobStatusPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(ShowJobStrip));

    private void RaiseScanState()
    {
        OnPropertyChanged(nameof(ShowScanCta));
        OnPropertyChanged(nameof(ShowLibraryNotIndexedLine));
    }

    private void Reproject()
    {
        _rows = _demoCache.Index;
        RefreshStagedAgainstStore();
        RaiseScanState();
        OnPropertyChanged(nameof(AnyHighlightsIndexed));
        OnPropertyChanged(nameof(AddClipsHint));
        OnPropertyChanged(nameof(ShowBrowseSurface));
        OnPropertyChanged(nameof(ShowWasmNote));
    }

    /// <summary>
    ///     Re-resolves every staged clip against the CURRENT store rows, dropping what no longer exists.
    ///     <para>
    ///         The tray holds <see cref="DemoCacheRecord" /> OBJECTS, and every window in the plan is
    ///         computed from that record's tickRate / tickCount / rounds. The store hands back a FRESH record
    ///         per read, so without this the tray would keep computing against a detached snapshot, and would keep
    ///         showing highlights the re-run ruleset no longer emits, or a demo the user deleted while the tab
    ///         was open. <c>OnActivated</c> calls <c>RefreshStaleness()</c>, so this is reachable in ordinary
    ///         use, not a corner case.
    ///     </para>
    /// </summary>
    private void RefreshStagedAgainstStore()
    {
        if (_order.Count == 0)
        {
            return;
        }

        List<HighlightKey> survivors = new(_order.Count);
        Dictionary<HighlightKey, HighlightSelection> refreshed = new(_order.Count);
        foreach (HighlightKey key in _order)
        {
            if (Resolve(key) is { } selection)
            {
                survivors.Add(key);
                refreshed[key] = selection;
            }
        }

        // Nothing moved and nothing was re-fingerprinted → skip the push. Reproject runs on EVERY store
        // Changed (i.e. repeatedly through a long backfill), and rebuilding ClipGroups each time would tear
        // the tray's containers down under the user's pointer mid-drag.
        bool unchanged = survivors.Count == _order.Count
                         && survivors.All(k =>
                             WindowInputs(refreshed[k].Record) == WindowInputs(_selection[k].Record));
        if (unchanged)
        {
            return;
        }

        int dropped = _order.Count - survivors.Count;
        _selection.Clear();
        foreach (KeyValuePair<HighlightKey, HighlightSelection> pair in refreshed)
        {
            _selection[pair.Key] = pair.Value;
        }

        _order.Clear();
        _order.AddRange(survivors);

        if (dropped > 0)
        {
            StatusMessage = $"{dropped} staged clip{(dropped == 1 ? " is" : "s are")} no longer in the " +
                            "highlights cache and " + (dropped == 1 ? "was" : "were") + " removed.";
        }

        PushTray();
    }

    // Resolves a staged identity against the live store. Shared by restore and the store-change refresh so
    // the two can never disagree about what counts as "still there".
    private HighlightSelection? Resolve(HighlightKey key)
    {
        DemoCacheRecord? record = _demoCache.TryLoadRecord(key.FilePath);
        CachedHighlightEvent? highlight = record?.Highlights.FirstOrDefault(e =>
            e.RulesetId == key.RulesetId && e.HighlightId == key.HighlightId
                                         && e.Tick == key.Tick && e.PlayerSlot == key.PlayerSlot);
        return record is null || highlight is null ? null : new HighlightSelection(record, highlight);
    }

    // The window inputs a staged clip is computed from. Everything ClipWindows.Compute reads off the record,
    // and nothing else, so two records that agree here produce byte-identical clips.
    //
    // This REPLACES a ReferenceEquals check, and the replacement is not optional. The old highlights store
    // handed back the same row instance until an Upsert replaced it, which made reference identity a real
    // "was this re-fingerprinted" signal. The unified store deliberately deserializes a FRESH instance on
    // every read (so a UI-thread reader cannot watch a background write mutate a record under it), so
    // ReferenceEquals is now always false, and the guard it protects exists to stop Reproject rebuilding
    // ClipGroups on every store change, which tears the tray's containers down under the user's pointer
    // mid-drag. Left as a reference check, this would have looked correct and quietly regressed dragging
    // during any backfill.
    private static (string?, int, int, int, string?) WindowInputs(DemoCacheRecord r) =>
        (r.ConfigFingerprint, r.TickRate, r.TickCount, r.Rounds.Count, r.Sha256);

    // ── Tray plumbing ─────────────────────────────────────────────────────────

    // One funnel for every tray mutation: push the ordered selections into the plan builder, then raise the
    // handful of derived flags. Every path (stage / unstage / reorder / clear / restore) goes through here,
    // so the tray and the plan cannot drift apart.
    private void PushTray()
    {
        ReelConfig.SetSelections(StagedSelections);
        // The picker's [ + ] / [ ✓ ] is a VIEW of the tray, so it re-reads the tray rather than tracking its
        // own idea of what is staged. This is the round trip: un-stage in the tray with the picker open and
        // the picker row flips back to [ + ]: a divergence here would let a user "add" a clip that is
        // already in the reel and see nothing happen.
        _picker?.SyncStagedFlags(IsStaged);
        OnPropertyChanged(nameof(StagedSelections));
        OnPropertyChanged(nameof(StagedCount));
        OnPropertyChanged(nameof(HasStagedClips));
        OnPropertyChanged(nameof(ShowEmptyTray));
        OnPropertyChanged(nameof(ShowLibraryNotIndexedLine));
        ClearTrayCommand.NotifyCanExecuteChanged();
        if (!HasStagedClips)
        {
            ShowClearConfirm = false;
        }
    }

    // Restore path: adds without pushing (the caller pushes once at the end).
    private bool StageQuietly(StagedClipState clip)
    {
        HighlightSelection? selection = Resolve(new HighlightKey(
            clip.FilePath, clip.RulesetId, clip.HighlightId, clip.Tick, clip.PlayerSlot));
        if (selection is null)
        {
            return false;
        }

        if (_selection.TryAdd(selection.Key, selection))
        {
            _order.Add(selection.Key);
        }

        return true;
    }

    // The tray's group sequence, derived from the canonical order by first appearance: the SAME rule the
    // plan builder uses, so the ▲▼ buttons move what the user sees.
    private List<string> GroupOrder()
    {
        List<string> groups = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (HighlightKey key in _order)
        {
            string g = GroupKeyOf(key);
            if (seen.Add(g))
            {
                groups.Add(g);
            }
        }

        return groups;
    }

    private string GroupKeyOf(HighlightKey key) =>
        ClipTrayKeys.Group(key.FilePath, _selection.GetValueOrDefault(key)?.SteamId64);
}
