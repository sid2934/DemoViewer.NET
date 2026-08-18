#region

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using System.Windows.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis;
using System.Text.Json;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.PlayerStats;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Debugging;
using DemoViewer.NET.Features;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.Parser.Models;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.Services.Idle;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.Services.Tutorial;
using DemoViewer.NET.Services.Update;
using DemoViewer.NET.ViewModels.Analysis;
using DemoViewer.NET.ViewModels.Commands;
using DemoViewer.NET.ViewModels.Common;
using DemoViewer.NET.ViewModels.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.EntityTracking;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Idle;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.Tutorial;
using DemoViewer.NET.ViewModels.LiveSync;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.Parser;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.ViewModels.Replay;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.ViewModels.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.ViewModels.Shell;

/// <summary>Main view model.</summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    ///     Safety cap on per-click Continue scan distance. Beyond this the user needs to
    ///     click Continue again. Prevents a UI freeze when no breakpoints match.
    /// </summary>
    private const int MaxContinueFrames = 200_000;

    // Interactive-open fan-out skip set: Highlights is fed the open demo through the completed analysis run
    // (OnOpenDemoEvaluated) — a richer, re-analysis-free channel — so it is not re-fed on the open path.
    private static readonly IReadOnlySet<string> _openFanOutSkip =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "highlights"
        };

    // Maps a WorkspaceTabDescriptor.TabId (module-owned, e.g. "builtin.parser") to its FeatureCatalog tab
    // feature id (e.g. "tab.parser"). A descriptor whose TabId is ABSENT here is never gated → always shown
    // (fail-open); a mapped id not in the catalog also fails open via IFeatureGate.IsEnabled. Static +
    // readonly (CA1861/CA1859-clean; the value type is the concrete Dictionary the lookup uses directly).
    private static readonly Dictionary<string, string> _tabFeatureIds = new(StringComparer.Ordinal)
    {
        ["builtin.library"] = "tab.library",
        ["builtin.matchoverview"] = "tab.matchoverview",
        ["builtin.parser"] = "tab.parser",
        ["builtin.entity"] = "tab.entity",
        ["builtin.stats"] = "tab.stats",
        ["highlights.browser"] = "tab.highlights",
        ["builtin.analysis"] = "tab.analysis",
        ["builtin.diagnostics"] = "tab.diagnostics",
        ["playback2d.viewport"] = "tab.playback2d",
        ["ruleworkbench.editor"] = "tab.authoring"
    };

    // The FULL descriptor set from every registered module, built ONCE in BuildWorkspaceTabs. The gate
    // FILTERS which of these become Tabs; the live reconcile re-adds / removes the SAME descriptor objects
    // by reference from this cache, so a re-enabled tab keeps its cached module-tab VM state (tabs reconcile by TabId —
    // never re-running CreateTabs, which would tear down that state).
    private readonly List<WorkspaceTabDescriptor> _allTabDescriptors = [];

    // The "one parse, many evaluators" coordinator. Used on an interactive
    // open to fan the just-parsed demo out to the background evaluators — so an un-indexed library demo
    // fills its card from THAT parse rather than a second background one. Null (designer / tests) → no fan-out.
    private readonly DemoEvaluationCoordinator? _evaluationCoordinator;

    // ── Feature gating ────────────────────────────────────────────────────────
    // The live show/hide authority for gated tabs. NULL on the designer / unit-test path (every existing
    // `new MainViewModel(...)` without a gate) → FAIL-OPEN: no tab is filtered, so the shell shows exactly
    // the tabs it did before gating existed. Filtering + live reconcile only run when a gate is injected
    // (the real app + the gating tests).
    private readonly IFeatureGate? _gate;

    // ── Heavy-parse coordination + highlights pipeline ──
    // Null on the designer / unit-test path → interactive loads run ungated (pre-gate behavior)
    // and no highlight harvesting happens.
    private readonly HeavyJobGate? _heavyJobGate;
    private readonly HighlightScanService? _highlightScanner;

    // ── Demo library (landing tab) ────────────────────────────────────────────
    // Background indexer for the "Library" browser tab. Owned here so its lifetime + disposal track the
    // shell; the tab VM wraps it and the BuiltInTabsModule mounts the tab first (default landing surface).
    // Injectable so tests supply an empty temp-path service (never scanning the user's real library.json).
    private readonly DemoLibraryService _library;

    // ── Module framework ──────────────────────────────────────────────────────
    private readonly ModuleRegistry _moduleRegistry;

    // The shell-owned semantic-navigation service: the "boundary movement"
    // counterpart to the clock. Boundary indices are precomputed once after parse (BuildUnknownMessageCensus
    // co-location) and consumed by the six *Frame* methods below — replacing their per-press re-scans.

    /// <summary>
    ///     Named handler for the STATIC <see cref="DemoParser.OnUnknownMessageType" /> event so
    ///     it can be unsubscribed in <see cref="Dispose" /> — a static event would otherwise pin this
    ///     view-model for the process lifetime.
    /// </summary>
    private readonly Action<UnknownMessageInfo> _onUnknownMessageType;

    private readonly DispatcherTimer? _perfTimer;

    // The single authoritative clock. Owns "current position"; every position move routes
    // through it so there is exactly one code path that advances the clock: it is the
    // fan-out point + observable position state, incremental stepping, and the play loop.

    // CPU/RAM perf tracking
    private readonly Process _process = Process.GetCurrentProcess();

    /// <summary>
    ///     This process's OS PID, shown in the window title. Exposed so the running instance can be fed
    ///     directly to the diagnostics CLI (<c>dotnet-gcdump</c> / <c>dotnet-dump</c> / <c>footprint</c>):
    ///     attaching to the LIVE process is how memory questions about this app get answered, and picking
    ///     the pid out of <c>ps</c> is ambiguous whenever a test host or a second build is also running.
    /// </summary>
    public static int ProcessId => Environment.ProcessId;

    // The global demo-processing queue (demo-processing-queue.md). An interactive open is submitted as
    // the highest-priority AWAITABLE foreground request (preempts background, refuses during a reel,
    // best-effort coalesces onto an in-flight parse). Null (designer / tests) → the direct gate path.
    private readonly IDemoProcessingQueue? _processingQueue;

    // The unified demo cache — the source for a cached Match Overview render. Null on WASM and in tests
    // that do not exercise the preview path.
    private readonly DemoCacheStore? _demoCache;

    // The queue → status-strip chip mapper; built in the ctor when a queue is
    // injected. Owns the "Processing" StatusChip added to Chips while the chrome.processingQueue gate is on
    // AND the queue has activity (running/queued) or is paused. Null on the designer / tests (no queue).
    private readonly ProcessingQueueStatusViewModel? _processingQueueStatus;

    // Idle-mode controller (desktop only): watches for inactivity via the global input hook + a coarse poll
    // timer and fires EnterIdleModeAsync when the configured wait elapses with no interaction and no active
    // playback. Constructed in the ctor when a settings monitor is available; Start()ed by the desktop
    // composition root (never on WASM). Null on the designer / tests without a monitor.
    private readonly IdleController? _idle;

    // First-run Visual Walkthrough engine — drives the tutorial overlay + tab navigation. Always built (UI
    // only); starts on the post-setup trigger or the Settings replay affordance.
    private readonly TutorialController _tutorial;

    // The transient session state captured when the app entered idle (which demo was closed + where it
    // resumes + the active tab), consumed once by ResumeFromIdle. Null when not idle, or when nothing was open.
    private IdleResumeState? _idleResume;

    // Proto source index (built once, immutable) and repo-root path. Created here
    // for now (still used by file-load + handed to ParserTab via callbacks); both
    // move to Shell/MainViewModel in 3.5c.
    private readonly ProtoIndex _protoIndex;

    /// <summary>
    ///     Recently-opened-demos store. Every successful open through the shared load core
    ///     records here; the Library landing tab binds its recents to it. Null on the designer / older
    ///     test path — recording then no-ops (fail-safe, like the other optional deps).
    /// </summary>
    private readonly RecentFilesStore? _recentFiles;

    private readonly string? _repoRoot; // absolute path to repo root (for local source links)

    // Stateless checkpoint-replay seek core, shared by EntityTab's three seek pipelines.
    private readonly EntitySeekService? _seekService;

    /// <summary>
    ///     Best-effort UI session persistence — the <c>Session</c> section of the single
    ///     consolidated config file. Constructed in the ctor from the injected
    ///     <see cref="SettingsService" />; no-op when none (WASM / older-test path).
    /// </summary>
    private readonly SessionStore _sessionStore;

    /// <summary>
    ///     Live user-settings monitor. Null on the designer / test path. Held as the seam
    ///     for future Settings / first-run surfaces (exposed via <see cref="Settings" />); nothing routes
    ///     through it behaviourally yet.
    /// </summary>
    private readonly IOptionsMonitor<AppSettings>? _settings;

    /// <summary>
    ///     Thread-safe sink for unknown-message occurrences raised during the (parallel) parse.
    ///     Drained once after parse into the grouped Output rows + the per-frame census — far
    ///     cheaper than the old per-occurrence UI dispatch (tens of thousands of posts per demo).
    /// </summary>
    private readonly ConcurrentBag<UnknownMessageInfo> _unknownAccumulator = new();

    /// <summary>
    ///     Window-spawning abstraction. Desktop opens a real
    ///     <c>ParseChainWindow</c>; browser no-ops. Null only when constructed parameterless by the
    ///     XAML designer, in which case the parse-chain command is inert.
    /// </summary>
    private readonly IWindowService? _windowService;

    /// <summary>
    ///     The single config-file service. Held (in addition to feeding
    ///     <see cref="SessionStore" />) for the "What's new" version gate, which reads and advances
    ///     <c>AppSettings.LastSeenVersion</c>. Null in the designer / older tests → the gate no-ops.
    /// </summary>
    private readonly SettingsService? _settingsService;

    // The per-run update-notice VM: created on first show, reused so the notes fetch happens once
    // and the banner's "Details…" re-activates the same window (see DesktopWindowService).
    private UpdateNoticeViewModel? _updateNotice;

    // Raw parsed frames stored for seeking
    private List<DemoFrame>? _allFrames;

    private byte[]? _demoBytes;

    // The library tier-2 fan-out started by the last open (LoadDemoFromBytesAsync). It reads the just-parsed
    // ParsedDemo on a background thread, so it ROOTS the demo until it finishes — which is why a close
    // immediately after an open used to leave RAM committed for a few seconds. CloseDemoAsync awaits this
    // before its reclaim collection so the whole frame graph is unrooted when the GC runs. Not held across
    // a reload (the new open's UnloadDemoState clears it; the old fan-out finishing late is harmless).
    private Task? _openFanOutTask;

    // The in-flight team-name lookup. It holds no ParsedDemo (it only reads the library entry), but it
    // awaits the fan-out, so it is tracked and awaited on close like _openFanOutTask.
    private Task? _teamNamesTask;

    // Coarse app-orchestration logging (unified diagnostics pillar). Resolved LAZILY, not in a field
    // initializer: the shell is constructed during DI composition, BEFORE App wires the real ambient
    // factory, so a field initializer would cache a NullLogger. First use is a demo load, after wiring.
    private ILogger? _diagLog;

    /// <summary>
    ///     The active first-run wizard when shown as an in-app OVERLAY (P2b — the WASM host has no OS
    ///     windows). Null when none is up. Desktop opens a modal <c>FirstRunWizardWindow</c> instead and
    ///     leaves this null, so the overlay panel in <c>MainView</c> is WASM-only in practice.
    /// </summary>
    [ObservableProperty]
    private FirstRunWizardViewModel? _firstRunOverlay;

    // FrameHeaderText / HasMessageCards / IsDecompressedTabAvailable / ShowRawHex
    // moved to ParserTab (3.3b). Legacy MainViewModel binding paths stay alive via
    // pass-through shims further down + the PropertyChanged forwarder in the ctor.
    //
    // HasInnerMessages, HasParseChain, SelectedFrame, SelectedFrameRow,
    // SelectedMessage, SelectedPayloadNode + all selection-coupled partials,
    // _selectedCard / _selectedProp / _cachedDecompressedPayload / _isNormalizedView
    // / _msgHlInfo / _msgDecompressedRanges / _cardModeActive moved to ParserTab (3.5a).
    //
    // HasEntities / HasEntitySelection moved to EntityTab in 3.4b — see comment above.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseDemoCommand))]
    private bool _hasFile;

    /// <summary>
    ///     True while the app is in idle mode — the <see cref="Views.Idle.IdleView" /> overlay is shown over
    ///     the shell (MainView binds its visibility here). Set by <see cref="EnterIdleModeAsync" /> and cleared
    ///     by <see cref="ResumeFromIdle" />.
    /// </summary>
    [ObservableProperty]
    private bool _isIdle;

    /// <summary>Toolbar toggle: show/hide the right-side debugger panel.</summary>
    [ObservableProperty]
    private bool _isDebuggerPanelVisible;
    // _hasSubTickEvents / _hasFrameGameEvents moved to ReplayTabViewModel (3.5b).

    [ObservableProperty]
    private bool _isLoading;

    // ── Tick view state ───────────────────────────────────────────────────────
    // _isTickView moved to ReplayTabViewModel (3.5b).
    private DateTime _lastCpuAt = DateTime.UtcNow;
    private TimeSpan _lastCpuUse = TimeSpan.Zero;

    // ── Live CS2 sync (CSVG) ──────────────────────────────────────────────────
    // The engine impl lives in the desktop-only DemoViewer.NET.LiveSync project and arrives via
    // AppHostHooks.LiveSyncFactory; null on Browser / tests / designer.

    // The state→chip mapper; constructed in AttachLiveSync once the engine
    // arrives. Owns the Live Sync StatusChip VM added to Chips when the chrome.livesync gate is on.
    private LiveSyncStatusViewModel? _liveSyncStatus;

    // Full path of the loaded demo, retained for the Diagnostics tab's Session card.
    // Set on load (both paths); read via the Func<string?> handed to the Diagnostics VM.
    private string? _loadedDemoPath;

    // The bundled tour sample's resolved path (null = none ships). Shared by the Library CTA and the
    // Match Overview "sample clip" banner so both key off the same file.
    private readonly string? _tourSamplePath;
    private ModuleContext? _moduleContext;

    // Keyed by game-event userid; built from PlayerConnectEvent (more reliable than binary string-table parsing in CS2).
    // Populated by PlayerSnapshotBuilder.BuildNameLookups on file load — primary
    // for nameByUserId is parsed.Players (string-table), secondary is PlayerConnectEvents.
    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance")]
    private IReadOnlyDictionary<int, string> _nameByUserId = new Dictionary<int, string>();

    // ── Nav-strip frame readout (navigation-review Phase C) ───────────────────
    // The shell nav strip's editable "frame N / MAX" box. Movement is frame-index based (the user's
    // locked decision); the tick is shown as a read-only label via Playback.CurrentTick. NavFrameText
    // mirrors Playback.CurrentFrameIndex (kept in sync via the controller's PropertyChanged) and commits
    // through Playback.SeekToFrame on Enter / LostFocus.
    [ObservableProperty]
    private string _navFrameText = "0";

    /// <summary>The last valid frame index shown in the nav-strip box (reverts target on bad input).</summary>
    private int _navLastValidFrame;

    /// <summary>
    ///     Loaded-but-not-yet-applied session snapshot. We can't restore frame selection until a demo
    ///     is loaded, so the payload is stashed in the ctor and consumed once <see cref="HasFile" />
    ///     flips true after the next file load. Cleared after a one-shot restore.
    /// </summary>
    private SessionPayload? _pendingRestore;

    private IReadOnlyDictionary<int, PlayerInfo>? _players;

    private Dictionary<int, PlayerInfo>? _playersByUserId; // keyed by player_info_t.userId (matches game-event userid)

    // The Reel chip is present while a job runs OR a finished result has not yet been dismissed. A new
    // running status clears this; the flyout's Dismiss sets it and removes the chip.
    private bool _reelDismissed;

    // ── Reel job ──────────────────────────────────────────────────────────────
    // The reel-generation engine impl lives in the desktop-only DemoViewer.NET.LiveSync project and arrives
    // via AppHostHooks.ReelJobFactory; null on Browser / tests / designer.
    private ReelJobStatusViewModel? _reelJobStatus;

    // The highlight-scan chip mapper. Shell-owned; the Reels tab shares the instance.
    private HighlightScanStatusViewModel? _highlightScanStatus;

    // Cached event-context built once per file load; used for per-tick stat computation.
    private DemoContext? _replayDemoContext;

    // _cardBuildCts moved to ReplayTabViewModel (3.5b).
    // SelectedEntityItem / SelectedEntityListItem moved to EntityTab in 3.4c.
    // Their partial handlers (OnSelectedEntityItemChanged + OnSelectedEntityListItemChanged)
    // moved with them; the parse-chain refresh that OnSelectedEntityItemChanged
    // triggered is now a callback (EntityTab.OnEntitySelectionChanged) wired in
    // the constructor.
    private int _selectedFrameIndex = -1;

    // ── Main tab state ────────────────────────────────────────────────────────
    // Tab selection is NAME-BASED end to end: SelectedTab (the descriptor, keyed by its stable TabId) is
    // the single source of truth the ItemsSource TabControl drives and the only thing persisted.
    // There is deliberately no int index mirror — the tab set is dynamic (feature gating, new built-ins),
    // so a position means a different tab from one build to the next. Navigate with SelectTabById.

    /// <summary>The currently-selected workspace tab descriptor. Drives activation/deactivation.</summary>
    [ObservableProperty]
    private WorkspaceTabDescriptor? _selectedTab;

    /// <summary>
    ///     The active Settings screen when shown as an in-app OVERLAY (the WASM host has no OS windows). Null
    ///     when no overlay is up. Desktop opens a real <c>SettingsWindow</c> instead and leaves this null, so
    ///     the overlay panel in <c>MainView</c> is WASM-only in practice (harmless/collapsed on desktop).
    /// </summary>
    [ObservableProperty]
    private SettingsViewModel? _settingsOverlay;

    // _selectedTickFrame / _selectedTickFrameRow / _selectedTickGroup moved to ReplayTabViewModel (3.5b).
    // ShowDeltaFieldsOnly moved to EntityTab in 3.4b — see HasEntities comment above.
    // ShowDormantEntities moved to EntityTab in 3.4c with its OnChanged + RefreshEntityView.
    // ShowRawHex now lives on ParserTab (3.3b) — see FrameHeaderText note.
    [ObservableProperty]
    private string _statusText = "Open a .dem file to begin.";

    private IStorageProvider? _storageProvider;

    [ObservableProperty]
    private string _windowTitle = "DemoViewer.NET";

    /// <param name="windowService">
    ///     Injected by <c>App.axaml.cs</c> per application lifetime. Defaults to <c>null</c> so the
    ///     XAML designer's parameterless construction path keeps working.
    /// </param>
    /// <param name="moduleRegistry">
    ///     First-party module registry from the composition root. Null → the shell builds a
    ///     default registry with just the built-in tabs (the designer / tests / single-arg callers).
    /// </param>
    /// <param name="library">
    ///     Demo-library indexer for the landing tab. Null → a default instance persisting to
    ///     <c>%AppData%/DemoViewer.NET/library.json</c>. Tests inject an empty temp-path service so shell
    ///     construction never scans the developer's real demo folders.
    /// </param>
    /// <param name="settings">
    ///     Live user-settings monitor from the composition root. Null → the designer / tests
    ///     path. Held as the seam for future Settings / first-run-wizard surfaces; the live consumers
    ///     (library folders, Workbench DeveloperMode) receive their own injected settings dependency.
    /// </param>
    /// <param name="gate">
    ///     Feature-gating authority. When supplied (the real app), the shell FILTERS the
    ///     workspace tab strip to the tabs enabled for the current user category and reconciles live on
    ///     <see cref="IFeatureGate.Changed" />. <b>Null → fail-open</b>: no tab is filtered, so every
    ///     existing <c>new MainViewModel(...)</c> caller (designer / tests) sees the full tab set exactly as
    ///     before gating existed.
    /// </param>
    /// <param name="recentFiles">
    ///     Recently-opened-demos store. When supplied (the real app), every open through the shared
    ///     load core is recorded and the Library tab surfaces the recents. Null → the designer / older-test
    ///     path: recording no-ops and the Library tab shows no recents (fail-safe).
    /// </param>
    /// <param name="settingsService">
    ///     The consolidated-config serializer that owns the UI session-restore <c>Session</c> section.
    ///     When supplied (the real app), the session is saved at shutdown and restored at launch. Null → the
    ///     designer / older-test / WASM path: session persistence no-ops.
    /// </param>
    /// <param name="heavyJobGate">
    ///     The machine-wide heavy-parse gate. When supplied, the shell's
    ///     interactive demo loads acquire it (preempting background indexing/scans; refused with a clear
    ///     message during a reel render). Null → ungated loads, the pre-gate behavior (designer / tests).
    /// </param>
    /// <param name="highlightScanner">
    ///     The highlights scanner. When supplied, the shell wires the
    ///     Library tier-2 piggyback, the open-demo harvest, and the staleness triggers. Null → no
    ///     highlight harvesting (designer / tests / WASM).
    /// </param>
    /// <param name="processingQueue">
    ///     The global demo-processing queue. When supplied, an interactive
    ///     open is submitted as the highest-priority awaitable foreground request (preempts background,
    ///     coalesces onto an in-flight parse, refuses during a reel). Null → the direct
    ///     <paramref name="heavyJobGate" /> path (designer / tests).
    /// </param>
    /// <param name="evaluationCoordinator">
    ///     The "one parse, many evaluators" coordinator. When supplied, an
    ///     interactive open fans its just-parsed demo out to the background evaluators, so an
    ///     un-indexed library demo fills its card from that parse instead of a second one. Null → no fan-out.
    /// </param>
    /// <param name="tourSampleLocator">
    ///     Resolves the bundled sample demo's path (the real app passes
    ///     <c>TourDemoLocator.FindSampleDemo</c>; it returns null on Browser/WASM). Invoked once here.
    ///     Null func (designer / tests) → no sample: the Library hero's "Try a sample match" CTA hides and
    ///     the walkthrough gateway keeps its library-card / Open-Demo-button behavior — which also keeps
    ///     every test shell deterministic regardless of what ships next to the test binary.
    /// </param>
    /// <param name="demoCache">
    ///     The unified demo-information cache. Backs the Library-selection preview: a single click renders
    ///     that demo's cached record on Match Overview without parsing anything. Null (WASM, most tests) →
    ///     the preview is simply inert.
    /// </param>
    public MainViewModel(
        IWindowService? windowService = null, ModuleRegistry? moduleRegistry = null,
        DemoLibraryService? library = null, IOptionsMonitor<AppSettings>? settings = null,
        IFeatureGate? gate = null, RecentFilesStore? recentFiles = null,
        SettingsService? settingsService = null, HeavyJobGate? heavyJobGate = null,
        HighlightScanService? highlightScanner = null,
        IDemoProcessingQueue? processingQueue = null,
        DemoEvaluationCoordinator? evaluationCoordinator = null,
        Func<string?>? tourSampleLocator = null,
        DemoCacheStore? demoCache = null)
    {
        _demoCache = demoCache;
        _windowService = windowService;
        _settingsService = settingsService;
        _settings = settings;
        _gate = gate;
        _recentFiles = recentFiles;
        _heavyJobGate = heavyJobGate;
        _highlightScanner = highlightScanner;
        _processingQueue = processingQueue;
        _evaluationCoordinator = evaluationCoordinator;
        // UI session-restore persistence: the Session section of the single config file. Null
        // settingsService (designer / older tests / WASM) → the store no-ops, so nothing is restored/saved.
        _sessionStore = new SessionStore(settingsService);
        _moduleRegistry = moduleRegistry ?? new ModuleRegistry();
        _library = library ?? new DemoLibraryService();
        // Semantic-navigation service drives the same clock; boundaries are filled by Build() after parse.
        Navigator = new SemanticNavigator(Playback);
        // The strip-ready event-filter flyout wraps the demo-derived GameEventFilters (single source).
        EventFilterFlyout = new EventFilterFlyoutViewModel(GameEventFilters);
        // Keep the nav-strip frame box mirroring the controller's index (seek / step / play loop).
        Playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackController.CurrentFrameIndex))
            {
                SyncNavFrameTextFromController();
            }
        };
        Navigation = new FrameNavigationViewModel();
        ParserTab = new ParserTabViewModel(Navigation);
        EntityTab = new EntityTrackingTabViewModel(Navigation);
        AnalysisTab = new AnalysisTabViewModel(Navigation);
        ReplayTab = new ReplayTabViewModel(Navigation);

        // Unified diagnostics telemetry hub — the single log sink for both the internal ILogger pillar
        // and the CSVG host logs. Ring cap read live from Diagnostics.MaxLogRows so a settings change
        // takes effect immediately (null settings → 5000 default).
        Telemetry = new DiagnosticsTelemetryHub(() => _settings?.CurrentValue.Diagnostics.MaxLogRows ?? 5000);

        // Diagnostics tab — reads (never owns) the analysis state, the loaded demo path,
        // and the frame list. Refreshes lazily on tab activation and after each evaluation.
        Diagnostics = new DiagnosticsTabViewModel(AnalysisTab, () => _loadedDemoPath, () => _allFrames, Telemetry);

        // Stats tab (release plan P1-3.1) — the user-facing scoreboard. Subscribes to the engine's
        // EvaluationCompleted and projects the MetricTables itself; reads (never owns) analysis state.
        StatsTab = new StatsTabViewModel(Analysis, () => _loadedDemoPath);

        // Match Overview — the demo landing page shown the instant a demo is opened (before the parse), so a
        // double-click has an immediate visible effect. The shell drives it through the load pipeline; its CTAs
        // just switch to the Stats / 2D-Playback tabs.
        MatchOverviewTab = new MatchOverviewTabViewModel(
            () => SelectTabById("builtin.stats"),
            () => SelectTabById("playback2d.viewport"),
            // Compute full stats — the per-demo replacement for the all-or-nothing library sweep. Resolved
            // lazily through a settable handler because the scanner that owns it lives in the Highlights
            // module, which the shell must not reference (the Library-delegate precedent).
            ComputeFullStats,
            path => _ = LoadDemoFromPathAsync(path),
            RestoreLiveMatchOverview,
            // Frame clock, passed AS-IS — the same handler the Analysis tab's Verify uses.
            (tick, spectateName, ct) =>
                LiveSync?.VerifyMomentAsync(tick, spectateName: spectateName, cancellationToken: ct)
                ?? Task.FromResult(false),
            () => IsLiveSyncEnabled,
            // The cross-demo clip tray lives on the Reels tab, which is a LAZY module tab — so these resolve
            // it at press time rather than holding it. Staging from Match Overview must work whether or not
            // the user has ever opened Reels; resolving on demand builds it if needed.
            (demo, ruleset, id, tick, slot) =>
                ReelTrayLocator?.Invoke()?.StageFromCache(demo, ruleset, id, tick, slot) ?? false,
            (demo, ruleset, id, tick, slot) =>
                ReelTrayLocator?.Invoke()?.Unstage(new HighlightKey(demo, ruleset, id, tick, slot)),
            (demo, ruleset, id, tick, slot) =>
                ReelTrayLocator?.Invoke()?.IsStaged(new HighlightKey(demo, ruleset, id, tick, slot)) ?? false);

        // 3.3b + 3.5a forwarder: re-raise ParserTab scalar property changes on the
        // shell for every name that still has a legacy pass-through shim. Subscribed
        // BEFORE any other ctor wiring so we don't miss early writes during the
        // initial state push.
        ParserTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FrameHeaderText)
                or nameof(HasMessageCards)
                or nameof(IsDecompressedTabAvailable)
                or nameof(ShowRawHex)
                // 3.5a additions:
                or nameof(SelectedFrame)
                or nameof(SelectedFrameRow)
                or nameof(SelectedMessage)
                or nameof(SelectedPayloadNode)
                or nameof(HasInnerMessages)
                or nameof(HasParseChain))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        // 3.4b/3.4c forwarder: same pattern for the twelve EntityTab scalars
        // that have pass-through shims on the shell. Once XAML retargets in 3.5
        // these forwarders + their shims can be deleted together.
        EntityTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasEntities)
                or nameof(HasEntitySelection)
                or nameof(EntityHeaderText)
                or nameof(EntityStatusText)
                or nameof(EntityDeltaFieldCount)
                or nameof(IsSeekingEntities)
                or nameof(HasWatched)
                or nameof(ShowDeltaFieldsOnly)
                or nameof(ShowDormantEntities)
                or nameof(SelectedEntityItem)
                or nameof(SelectedEntityListItem))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        // 3.5b forwarder: ReplayTab scalar property changes that still have legacy
        // pass-through shims on the shell.
        ReplayTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasTickGroups)
                or nameof(HasSubTickEvents)
                or nameof(HasFrameGameEvents)
                or nameof(IsTickView)
                or nameof(SelectedTickGroup)
                or nameof(SelectedTickFrame)
                or nameof(SelectedTickFrameRow))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        // Navigation hooks consumed by the command palette + Output panel. These
        // route through the controller too, so the palette / Output navigation share the one clock.
        Navigation.SeekToFrameHandler = Playback.SeekToFrame;
        Navigation.SeekToTickHandler = Playback.SeekToTick;
        Navigation.RevealClassHandler = RevealEntityClass;

        // Output panel. Subscribe to the STATIC unknown-message-type event
        // with a named handler so Dispose can detach it (otherwise the static event pins this
        // VM forever). The event fires on the parse background thread → marshal to the UI thread.
        Bookmarks = new BookmarksViewModel(Navigation);
        Output = new OutputPanelViewModel(Navigation);
        // Accumulate on the parse threads (thread-safe, allocation-light); the grouped Output rows
        // and the per-frame census are built once after parse (see BuildUnknownMessageCensus).
        _onUnknownMessageType = info => _unknownAccumulator.Add(info);
        DemoParser.OnUnknownMessageType += _onUnknownMessageType;

        DebuggerPanel = new DebuggerViewModel(Debugger);

        // Reflect breakpoint adds/removes onto the frame-row gutter markers.
        Debugger.Breakpoints.CollectionChanged += (_, _) => RefreshFrameBreakpointMarkers();

        // 3.4b: WatchedValues -> HasWatched subscription moved into EntityTab's
        // constructor (both ends are now EntityTab-owned). No subscription here.

        _lastCpuUse = _process.TotalProcessorTime;
        _perfTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _perfTimer.Tick += (_, _) => UpdatePerfStats();
        _perfTimer.Start();

        _repoRoot = FindRepoRoot();
        _protoIndex = _repoRoot != null
            ? ProtoIndex.Build(Path.Combine(_repoRoot, "cs2-opendocs", "data", "Protobufs"))
            : new ProtoIndex();

        // Command palette. Sources read live so the palette always reflects
        // the loaded demo: tracker from EntityTab, the startup ProtoIndex, current frame count.
        Palette = new CommandPaletteViewModel(
            Navigation,
            () => EntityTab.CurrentTrackerInternal,
            () => _protoIndex,
            () => Frames.Count);

        // navigation-review Phase D — the per-tab SeekControls (control + VM) is fully retired: Phase C
        // removed its last view mount, so the shell NavStrip is the single nav surface. The six legacy
        // *Frame* wrapper methods remain (they delegate to the SemanticNavigator and are still the
        // implementation the NavStrip's Nav*Command targets route through indirectly).

        Analysis.CardFactory = msg => ParserTab.BuildHarvestCardExternal(msg, null);
        Analysis.OnFrameSeeked = idx =>
        {
            AnalysisTab.RunSuppressedFrameSeek(() =>
            {
                if (idx >= 0 && idx < Frames.Count)
                {
                    SelectedFrame = Frames[idx];
                }
            });
        };

        // "Verify in CS2". Same decoupled-delegate direction as
        // CardFactory / OnFrameSeeked above — the Analysis VM never references the Live Sync contract. The
        // two-level gate lives here: PRESENT iff the Live Sync chip is (chrome.livesync + desktop); ENABLED
        // only while an actual Synced session exists. The tick is already frame-clock — passed AS-IS. The
        // engine surface never throws for playback failures (returns false), so no try/catch here.
        Analysis.IsVerifyInCs2Present = () => IsLiveSyncEnabled;
        Analysis.CanVerifyMoment = () => IsLiveSyncEnabled && (LiveSync?.State.IsSynced ?? false);
        Analysis.VerifyMomentHandler = (tick, spectateName, ct) =>
            LiveSync?.VerifyMomentAsync(tick, spectateName: spectateName, cancellationToken: ct)
            ?? Task.FromResult(false);

        // 3.4c: wire EntityTab's seek pipeline back into the shell. The four hooks
        // below let EntityTab drive its async seeks without holding a reference
        // to MainViewModel itself -- same dependency direction as the
        // Analysis.OnFrameSeeked wiring above.
        EntityTab.CreateTracker = CreateTracker;
        // The shared checkpoint-replay seek core. EntityTab's three pipelines delegate the
        // tracker-build + replay to this; the per-pipeline snapshot policy stays in EntityTab.
        _seekService = new EntitySeekService(CreateTracker);
        EntityTab.SeekService = _seekService;

        // Route the single position fan-out through the controller. ApplySeek IS the
        // lifted body of HandleFrameSelectedFromParserTab (light-sync + heavy async entity seek);
        // ApplyLightSeek is the light-sync half the incremental StepForward / play loop reuse. The
        // controller wraps both in a re-entrancy guard so the SelectedFrame= assignment can't
        // double-fire.
        Playback.ApplySeek = HandleFrameSelectedFromParserTab;
        Playback.ApplyLightSeek = ApplyLightSeekFanOut;
        Playback.NotifyFrameChanged = Navigation.RaiseSelectedFrameChanged;

        // The controller is the sole owner of the authoritative tracker instance. EntityTab's
        // async seeks publish their freshly-built tracker here; CurrentTrackerInternal reads it back.
        // The incremental StepForward steps this instance in place and rebuilds EntityTab synchronously.
        EntityTab.TrackerSource = () => Playback.AuthoritativeTracker;
        EntityTab.PublishTracker = Playback.PublishTracker;
        Playback.StepEntityRebuild = EntityTab.RebuildFromSteppedTracker;
        Playback.CancelInFlightSeek = () =>
        {
            EntityTab.SeekCts?.Cancel();
            EntityTab.SeekCts = null;
        };

        // Play loop — on-Pause discrete-tab snap. The snap runs the light fan-out (SelectedFrame /
        // analysis seek) plus a SYNCHRONOUS EntityTab rebuild from the already-stepped authoritative
        // tracker — not an O(N) async re-seek (the loop already advanced the tracker to
        // CurrentFrameIndex). The per-tick frame readout is no longer pushed here: the NavStrip's
        // NavFrameText tracks PlaybackController.CurrentFrameIndex via PropertyChanged (set every tick).
        Playback.SnapDiscreteTabsToCurrent = idx =>
        {
            ApplyLightSeekFanOut(idx);
            if (Playback.AuthoritativeTracker is { } stepped)
            {
                EntityTab.RebuildFromSteppedTracker(stepped, null);
            }
        };
        EntityTab.FrameSource = () => _allFrames;
        EntityTab.PublishTrackerStats = PublishTrackerStats;
        EntityTab.OnSeekCompleted = () =>
        {
            // Refresh entity_data decode in the selected card (if it's a PacketEntities
            // message). All logic now owned by ParserTab; the shell just routes the call.
            ParserTab.RefreshSelectedPacketEntitiesCard();
        };
        EntityTab.OnSeekFinally = () =>
        {
            // Auto-clear breakpoint suppression after every seek completes. JumpToHitFrame
            // sets this so the back-navigation seek doesn't re-fire the same breakpoint;
            // once that seek is done, breakpoints are armed again for the next user action.
            Debugger.Suppress = false;
        };
        EntityTab.OnEntitySelectionChanged = entity =>
        {
            // EntityTab raises this after rebuilding AllEntityFieldNodes (or clearing
            // selection). Refresh the parse-chain so the bottom strip stays in sync.
            ParserTab.RebuildParseChainForEntity(entity);
        };

        // 3.5a: wire ParserTab's shell callbacks. Same dependency direction as
        // EntityTab/Analysis — ParserTab calls these to reach shell-owned state
        // without holding a reference to MainViewModel.
        ParserTab.FrameListSource = () => _allFrames;
        ParserTab.DemoBytesSource = () => _demoBytes;
        ParserTab.ProtoIndexSource = () => _protoIndex;
        ParserTab.RepoRootSource = () => _repoRoot;
        ParserTab.EntityTrackerSource = () => EntityTab.CurrentTrackerInternal;
        ParserTab.EntityFieldNodesSource = () => EntityTab.EntityFieldNodes;
        ParserTab.EnrichmentResolver = ResolveEnrichmentHint;
        ParserTab.PopulateFrameGameEvents = PopulateFrameGameEventsFromFrame;
        // The master frame-selection setter now routes through the controller, the single
        // position fan-out point. The controller runs HandleFrameSelectedFromParserTab (via ApplySeek)
        // under its re-entrancy guard, then updates its observable position state.
        ParserTab.OnFrameSelected = Playback.SeekToFrame;

        // 3.5b: wire ReplayTab's shell callbacks. Same dependency direction as the
        // other tabs — ReplayTab reads frames + raw bytes via Funcs and pushes card
        // builds into ParserTab via the ParserCard* hooks.
        ReplayTab.FrameSource = () => _allFrames;
        ReplayTab.DemoBytesSource = () => _demoBytes;
        ReplayTab.ParserCardReset = ParserTab.ResetForTickGroupBuild;
        ReplayTab.ParserCardAppend = card => ParserTab.MessageCards.Add(card);
        ReplayTab.ParserHeaderSink = text => ParserTab.FrameHeaderText = text;
        ReplayTab.ParserHasMessageCardsSink = has => ParserTab.HasMessageCards = has;
        ReplayTab.ParserCardFactory = (msg, msgBytes, normOffset) =>
            ParserTab.BuildHarvestCardExternal(msg, msgBytes, normOffset);
        ReplayTab.SlotNameResolver = SlotToName;
        // navigation-review Phase D — ReplayTab.GameEventFilterProvider removed with the orphaned
        // NextGameEventTick; the single demo-derived filter (GameEventFilters) now drives the NavStrip.
        ReplayTab.OnTickGroupSelected = group => _ = EntityTab.SeekEntitiesWithDeltaAsync(group);
        ReplayTab.OnTickFrameSelected = frame =>
        {
            ParserTab.BuildCardsForFrameExternal(frame);
            if (_allFrames is not null)
            {
                int idx = -1;
                for (int i = 0; i < _allFrames.Count; i++)
                {
                    if (ReferenceEquals(_allFrames[i], frame))
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx >= 0)
                {
                    _ = EntityTab.SeekEntitiesForTickFrameAsync(idx);
                }
            }
        };
        ReplayTab.NotifyCanGoNextTickChanged = NotifyDebuggerCommandsCanExecute;

        // Demo-library landing tab: wraps the shared indexer and routes opening a demo through the same
        // path-based load core the Open-file picker uses. The load funnel owns the landing tab —
        // Match Overview on a normal open, stay-put while the tutorial is touring — so the Library
        // passes no tab-switch delegate (the old pre-switch to Parser is what used to yank the tour's
        // spotlighted card-click onto the Parser tab).
        // Resolved ONCE and shared: the Library's "Try a sample match" CTA and the Match Overview
        // "sample clip" banner must agree on what the sample is.
        _tourSamplePath = tourSampleLocator?.Invoke();
        LibraryTab = new LibraryTabViewModel(
            _library,
            LoadDemoFromPathAsync,
            PickFoldersAsync,
            OpenFileAsync, // the Library's "Open Demo…" CTA shares the one picker → LoadDemoFromBytesAsync funnel
            _recentFiles,
            _tourSamplePath); // bundled sample (assets/tour) → the hero's "Try a sample match" CTA

        // Selecting a card (single click / arrow key) renders that demo's CACHED record on Match Overview —
        // browsing, not opening. Reads the cache and starts nothing; double-click still owns the parse.
        LibraryTab.DemoPreviewRequested += PreviewDemoFromCache;

        // A scan finishing must reach the page that is SHOWING that demo. Without this, pressing "Compute
        // full stats" queued real work, wrote a real record, and changed nothing on screen until the user
        // navigated away and back — which reads exactly like the button doing nothing.
        if (_demoCache is not null)
        {
            _demoCache.Changed += RefreshCachedMatchOverview;
        }

        // ── Module framework ──────────────────────────────────────────────────
        BuildWorkspaceTabs();

        // Highlights pipeline wiring: the open-demo harvest (the Analysis
        // tab's own evaluation refreshes the open demo's row for free) and the staleness triggers — app
        // start now, and after every library scan phase (the start-time library is empty until its folder
        // scan lands). The former Library tier-2 → Highlights piggyback is gone: the coordinator now fans
        // a held parse to the OTHER evaluators, covering both the
        // Library tier-2 slot and an interactive open.
        if (_highlightScanner is { } scanner)
        {
            if (library is not null)
            {
                library.Changed += scanner.RefreshStaleness;
            }

            Analysis.EvaluationCompleted += (run, demo) =>
                scanner.OnOpenDemoEvaluated(_loadedDemoPath, run, demo);
            scanner.RefreshStaleness();

            // Enabling the background-scan opt-in in Settings must actually start the scan —
            // without this, the persisted flag did nothing until an unrelated trigger (tab
            // activation, library rescan, app restart) happened to run the scanner.
            if (_settings is not null)
            {
                bool wasScanEnabled = _settings.CurrentValue.Highlights.BackgroundScan;
                _settings.OnChange(current =>
                {
                    bool nowEnabled = current.Highlights.BackgroundScan;
                    if (nowEnabled && !wasScanEnabled)
                    {
                        scanner.RefreshStaleness();
                        scanner.EnsureBackfillRunning();
                    }

                    wasScanEnabled = nowEnabled;
                });
            }
        }

        // Global demo-processing queue — the live status-strip surface. Build
        // the chip mapper over the injected queue (null on the designer / older tests) and reconcile its
        // presence per the chrome.processingQueue gate + queue activity. The queue posts Changed on the UI
        // thread, so the reconcile handler runs there; a gate flip reconciles via ApplyGateChange.
        if (_processingQueue is not null)
        {
            _processingQueueStatus = new ProcessingQueueStatusViewModel(_processingQueue, OpenSettings);
            _processingQueue.Changed += OnProcessingQueueChanged;
            ReconcileQueueChip();
        }

        // Idle mode — the surface VM (always built so the overlay binds; the embedded queue is the shared
        // status-strip mapper, null on hosts without a queue) and the controller (only when a live settings
        // monitor exists). The controller is inert until StartIdleMonitoring() is called by the DESKTOP
        // composition root — WASM never starts it, so idle mode is desktop-only. Playback.IsPlaying is the
        // single "don't go idle" signal (paused / ended playback both read false).
        IdleView = new IdleViewModel(ResumeFromIdle, OpenSettings, _processingQueueStatus);
        if (_settings is not null)
        {
            _idle = new IdleController(
                _settings,
                IsIdleBlocked,
                () => _ = EnterIdleModeAsync());
        }

        // First-run Visual Walkthrough. The controller drives the overlay VM the shell binds to; it switches
        // tabs by TabId so each step's region is realized before the overlay measures it. Inert until Start()
        // is called (post-setup by the desktop root, or the Settings replay affordance).
        _tutorial = new TutorialController(
            TutorialSteps.Default, SelectTabById, () => HasFile, () => { }, () => _library.Entries.Count > 0,
            // The sample CTA is only spotlightable while it is ON SCREEN: a sample resolved AND the hero
            // (empty-library) state showing — with folders configured the hero (and the CTA) is hidden.
            () => LibraryTab.HasSampleDemo && LibraryTab.HasNoFolders);

        // Session restore used to run HERE, and must never come back. It selects the persisted
        // tab, and activating a tab builds that tab's view-model, which may legitimately need the shell —
        // but a DI singleton is not cached until its factory RETURNS, so resolving MainViewModel from
        // inside its own constructor builds a SECOND shell that restores again, without bound. The
        // composition root now calls RestoreSession() after construction (see
        // App.OnFrameworkInitializationCompleted); App.BuildShell guards the invariant.
    }

    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger(AppLog.ShellCategory);

    /// <summary>
    ///     The shared playback clock. Read-only access for tabs / modules / tests; the only
    ///     way to move position is through its operations, which the shell wires to the legacy
    ///     navigation fan-out.
    /// </summary>
    public PlaybackController Playback { get; } = new();

    /// <summary>
    ///     The idle-mode surface view-model (bound by the <see cref="Views.Idle.IdleView" /> overlay in
    ///     MainView). Always non-null; the overlay is shown only while <see cref="IsIdle" /> is true.
    /// </summary>
    public IdleViewModel IdleView { get; }

    /// <summary>
    ///     The first-run Visual Walkthrough overlay VM (bound by <see cref="Views.Tutorial.TutorialView" />).
    ///     The overlay shows only while <c>Tutorial.IsActive</c> is true.
    /// </summary>
    public TutorialViewModel Tutorial => _tutorial.ViewModel;

    /// <summary>
    ///     In-app updater VM — backs the shell's update banner and the Settings update controls.
    ///     Always non-null; when the host supplied no <c>IUpdateService</c> (Browser, tests, dev
    ///     runs) it reports <c>IsSupported == false</c> and every path is inert, so neither the
    ///     banner nor Settings needs a platform branch.
    /// </summary>
    public UpdateViewModel Update { get; } = UpdateViewModel.Shared;

    /// <summary>
    ///     Fires the launch update check. Called from the desktop lifetime branch once the shell
    ///     exists, deliberately fire-and-forget: a slow or offline feed request must never delay the
    ///     window appearing. When the check finds an update, the notice pop-up opens (v0.6.0) and
    ///     the banner appears as its persistent re-entry point.
    /// </summary>
    public void StartUpdateCheck()
    {
        if (!Update.IsSupported)
        {
            return;
        }

        _ = RunStartupUpdateCheckAsync();
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        await Update.CheckOnStartupAsync().ConfigureAwait(true);
        if (Update.IsUpdateAvailable)
        {
            ShowUpdateDetails();
        }
    }

    /// <summary>
    ///     Opens the update-notice pop-up (auto after the startup check; also the banner's
    ///     "Details…" button). One VM per run, so re-opening is instant and re-uses the fetched
    ///     notes; no-op without a window service (designer / tests) or when no update is offered.
    /// </summary>
    [RelayCommand]
    private void ShowUpdateDetails()
    {
        if (_windowService is null || !Update.IsUpdateAvailable)
        {
            return;
        }

        _updateNotice ??= new UpdateNoticeViewModel(Update, GitHubReleaseNotesService.Shared);
        _windowService.ShowUpdateNotice(_updateNotice);
    }

    /// <summary>
    ///     The post-update "What's new" gate. Called from the desktop lifetime branch after
    ///     <see cref="StartUpdateCheck" />: compares the running version against the persisted
    ///     <c>AppSettings.LastSeenVersion</c> and, when they differ on an already-set-up install,
    ///     shows the What's New window once. The stored version is advanced BEFORE the window opens
    ///     so a crash can never re-show it in a loop. A fresh install (first-run wizard pending)
    ///     just records the version silently — the wizard is enough for one launch.
    /// </summary>
    public void StartWhatsNewCheck()
    {
        if (_settingsService is null || _windowService is null)
        {
            return;
        }

        string? version = AppVersionInfo.CurrentReleaseVersion;
        if (version is null)
        {
            return;
        }

        string? lastSeen = GitHubReleaseNotesService.NormalizeVersion(_settingsService.Current.LastSeenVersion);
        if (string.Equals(lastSeen, version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool firstRun = _settingsService.NeedsFirstRun;
        _settingsService.Write(s => s.LastSeenVersion = version);
        if (firstRun)
        {
            return;
        }

        WhatsNewViewModel whatsNew = new(version, GitHubReleaseNotesService.Shared);
        _windowService.ShowWhatsNew(whatsNew);
    }

    /// <summary>
    ///     The shared semantic navigator. Read-only access for the nav strip
    ///     (Phase C) and tests; movement happens through its <c>Next*</c>/<c>Prev*</c> methods, which
    ///     binary-search the precomputed boundaries and drive <see cref="Playback" />.
    /// </summary>
    public SemanticNavigator Navigator { get; }

    /// <summary>
    ///     The strip-ready event-filter flyout VM. Wraps the
    ///     demo-derived <see cref="GameEventFilters" /> with Select-all / Deselect-all + a live tooltip;
    ///     the Phase C nav strip's event-jump flyout binds to this. One filter, one source.
    /// </summary>
    public EventFilterFlyoutViewModel EventFilterFlyout { get; }

    /// <summary>
    ///     The module-facing playback/demo context, exposed for the Desktop-hosted live-sync engine
    ///     : it observes <see cref="IModuleContext.DemoPath" /> and the
    ///     <c>DemoReset</c> load-complete signal through this. Null only before the shell finishes
    ///     constructing.
    /// </summary>
    public IModuleContext? ModuleContext => _moduleContext;

    /// <summary>The live CS2 sync engine, when the host provides one (desktop only). Null otherwise.</summary>
    public ILiveSyncService? LiveSync { get; private set; }

    /// <summary>
    ///     Status-strip chips — the Live Sync chip (and the Reel-job
    ///     chip). Bound by <c>StatusStrip.Chips</c>. Empty when no chip is active, so the strip reads exactly
    ///     as before. The Live Sync chip is present only while <see cref="IsLiveSyncEnabled" /> and an engine
    ///     is attached (reconciled by <see cref="ReconcileChips" />); a session never auto-starts.
    /// </summary>
    public ObservableCollection<StatusChipViewModel> Chips { get; } = [];

    /// <summary>The background reel-generation service, when the host provides one (desktop). Null otherwise.</summary>
    public IReelJobService? ReelJob { get; private set; }

    /// <summary>Demo-library landing tab (folder scan + filterable card/list browser).</summary>
    public LibraryTabViewModel LibraryTab { get; }

    /// <summary>
    ///     The workspace tab strip. ItemsSource-driven; the four built-in tabs are registered
    ///     descriptors (via BuiltInTabsModule) alongside any module tabs, so the shell has one code
    ///     path for all tabs. Sorted by (Placement, Order).
    /// </summary>
    public ObservableCollection<WorkspaceTabDescriptor> Tabs { get; } = [];

    // ── Feature-gating shims (sub-feature + chrome enforcement) ────────────────────────────────────────
    // Read-only projections of the injected gate for XAML IsVisible bindings. FAIL-OPEN when the gate is
    // null (the designer / every existing no-gate `new MainViewModel(...)`): each shim returns true, so the
    // shell renders exactly the chrome it did before gating existed. ApplyGateChange re-raises PropertyChanged
    // for all of these on a live category/override write so the bound chrome reflows.

    /// <summary>Gate shim: parser RAW/hex byte pane (<c>parser.hex</c> sub-feature).</summary>
    public bool IsHexPaneEnabled => _gate?.IsEnabled("parser.hex") ?? true;

    /// <summary>Gate shim: toolbar Parse-Chain button (<c>chrome.parseChain</c>).</summary>
    public bool IsParseChainEnabled => _gate?.IsEnabled("chrome.parseChain") ?? true;

    /// <summary>Gate shim: right-side debugger rail (<c>chrome.debugger</c>).</summary>
    public bool IsDebuggerChromeEnabled => _gate?.IsEnabled("chrome.debugger") ?? true;

    /// <summary>Gate shim: bottom Output drawer chrome (<c>chrome.output</c>).</summary>
    public bool IsOutputChromeEnabled => _gate?.IsEnabled("chrome.output") ?? true;

    /// <summary>Gate shim: NavStrip TO-BREAKPOINT cluster (<c>chrome.breakpointNav</c>).</summary>
    public bool IsBreakpointNavEnabled => _gate?.IsEnabled("chrome.breakpointNav") ?? true;

    /// <summary>Gate shim: entity Schema-Lens strip (<c>entity.schema</c> sub-feature).</summary>
    public bool IsSerializerSchemaEnabled => _gate?.IsEnabled("entity.schema") ?? true;

    /// <summary>Gate shim: analysis graph-breakpoint chrome (<c>analysis.breakpoints</c> sub-feature).</summary>
    public bool IsAnalysisBreakpointsEnabled => _gate?.IsEnabled("analysis.breakpoints") ?? true;

    /// <summary>
    ///     Gate shim: the Live Sync (CS2) chip / flyout + NavStrip speed-lock (<c>chrome.livesync</c>).
    ///     <b>Fail-CLOSED</b> (unlike the other shims) — a null gate returns <c>false</c> — AND ANDed with
    ///     <c>!OperatingSystem.IsBrowser()</c>, because a browser build must never surface Live Sync even in
    ///     the fail-open case. Developer default-on; power/consumer opt in via Settings.
    /// </summary>
    public bool IsLiveSyncEnabled => (_gate?.IsEnabled("chrome.livesync") ?? false) && !OperatingSystem.IsBrowser();

    /// <summary>
    ///     Gate shim: the demo-processing-queue chip / flyout (<c>chrome.processingQueue</c>, demo-processing-
    ///     queue.md). <b>Fail-CLOSED</b> like Live Sync (a null gate returns <c>false</c>) and ANDed with
    ///     <c>!OperatingSystem.IsBrowser()</c> — background parse/analyse work needs a filesystem, so a browser
    ///     build never surfaces the queue surface. Power-user+ default; the chip appears only while the queue
    ///     has activity or is paused (see <see cref="ReconcileQueueChip" />).
    /// </summary>
    public bool IsProcessingQueueEnabled =>
        (_gate?.IsEnabled("chrome.processingQueue") ?? false) && !OperatingSystem.IsBrowser();

    /// <summary>
    ///     True while a Live Sync session is in any Synced sub-state AND the connected plugin lacks the
    ///     v1.1 timescale capability — the NavStrip speed ComboBox binds its <c>IsEnabled</c> to the
    ///     inverse. With "timescale-set" advertised, Speed becomes a mirrored control-plane
    ///     property (the lock simply stops reporting locked). False without an engine.
    /// </summary>
    public bool IsPlaybackSpeedLocked =>
        LiveSync is { State.IsSynced: true } sync && !(sync.Capabilities?.TimescaleSet ?? false);

    /// <summary>How many developer-visible features the current user does not see (0 when no gate / developer).</summary>
    public int HiddenFeatureCount => _gate?.HiddenCount ?? 0;

    /// <summary>
    ///     Informational status-strip note for the "N features hidden" affordance — empty when nothing is
    ///     hidden (0 for a developer / null gate), which the status strip binds to hide the affordance.
    /// </summary>
    public string HiddenFeatureNote
    {
        get
        {
            int n = HiddenFeatureCount;
            if (n <= 0)
            {
                return "";
            }

            string noun = n == 1 ? "feature" : "features";
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + " hidden";
        }
    }


    /// <summary>
    ///     Live user-settings monitor from the composition root, or <c>null</c> on the
    ///     designer / test path. The resolution seam for future Settings / first-run surfaces; the current
    ///     live consumers (library folders, Workbench DeveloperMode) receive their own injected dependency.
    /// </summary>
    public IOptionsMonitor<AppSettings>? Settings => _settings;

    /// <summary>
    ///     Compatibility shim for existing XAML bindings (<c>{Binding Analysis}</c>) and
    ///     in-class call sites. Delegates to <see cref="AnalysisTabViewModel.Analysis" />.
    /// </summary>
    public AnalysisViewModel Analysis => AnalysisTab.Analysis;

    /// <summary>Analysis tab.</summary>
    public AnalysisTabViewModel AnalysisTab { get; }

    /// <summary>Diagnostics tab — session/system info + per-layer profiling panels.</summary>
    public DiagnosticsTabViewModel Diagnostics { get; }

    /// <summary>
    ///     Bounded, app-lifetime sink for ALL diagnostics logs — the internal ILogger pillar plus the
    ///     CSVG host logs. Fed by the internal logger provider and (across the <c>AppHostHooks</c> seam)
    ///     the desktop LiveSync engine; bound by the Diagnostics tab and mirrored into the Output
    ///     drawer's lazy "Live Sync" channel. Empty on the Browser head. Constructed in the ctor so the
    ///     ring cap can read <c>Diagnostics.MaxLogRows</c> live from settings.
    /// </summary>
    public DiagnosticsTelemetryHub Telemetry { get; }

    /// <summary>Stats tab (release plan P1-3.1) — the user-facing scoreboard + per-round browser.</summary>
    public StatsTabViewModel StatsTab { get; }

    /// <summary>Match Overview tab — the demo landing page (identity + load progress + summary).</summary>
    public MatchOverviewTabViewModel MatchOverviewTab { get; }

    /// <summary>
    ///     Runs the full analysis pass for ONE demo — the Match Overview completeness chip's
    ///     <c>Compute full stats</c>, and the per-demo replacement for the all-or-nothing library sweep the
    ///     opt-in offers.
    ///     <para>
    ///         <b>Enqueues; never opens.</b> It hands the path to the highlight scanner's forced-rescan path,
    ///         which submits at user-requested priority — outranking background work and bypassing the queue
    ///         size cap, because a user action is never rejected. From there it routes through
    ///         <c>HeavyJobGate</c> (one heavy parse machine-wide), surfaces in the processing-queue chip, and
    ///         fans that ONE parse out to every evaluator — so a single press fills the parse gaps, the
    ///         scoreboard and the highlights together.
    ///     </para>
    /// </summary>
    private void ComputeFullStats(string path)
    {
        if (path is { Length: > 0 })
        {
            _highlightScanner?.RequestScan(path);
        }
    }

    /// <summary>
    ///     Re-renders Match Overview when the cache record behind it changes — the completion signal for
    ///     <c>Compute full stats</c> and for any background scan that happens to land on the demo on screen.
    ///     <para>
    ///         <b>Cached mode only.</b> <c>SetCachedRecord</c> calls <c>ResetValues</c> and flips the page to
    ///         <see cref="OverviewMode.Cached" />; firing it during a live open would wipe the pipeline's fills
    ///         mid-load and then silently drop every subsequent one, because the keyed setters only accept
    ///         pushes while the page is Live. A live page needs no refresh anyway — its own pipeline is the
    ///         thing writing the record.
    ///     </para>
    /// </summary>
    private void RefreshCachedMatchOverview(string? changedPath)
    {
        if (_demoCache is null || MatchOverviewTab.SubjectKey is not { Length: > 0 } path)
        {
            return;
        }

        // Only this demo's own write, or a bulk change that may include it. Re-rendering on every unrelated
        // write would mean browsing the Library during a background index cost a sidecar read and a full
        // page rebuild per demo indexed — and since the rebuild recreates the highlight groups, every group
        // the user had collapsed would pop back open under them.
        if (changedPath is not null
            && !string.Equals(changedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_demoCache.TryLoadRecord(path) is not { } record)
        {
            return;
        }

        if (MatchOverviewTab.Mode == OverviewMode.Cached)
        {
            MatchOverviewTab.SetCachedRecord(record);
            return;
        }

        // LIVE: fill only the highlight section, never the whole page. The open's own harvest lands here —
        // it runs off-thread and completes after SetAnalysis, so without this the demo you just opened shows
        // an empty moments column until you navigate away and back. A full SetCachedRecord would flip the
        // page to Cached and drop every pipeline push that followed.
        if (MatchOverviewTab.Mode == OverviewMode.Live)
        {
            MatchOverviewTab.RefreshHighlightsFromCache(path, record);
        }
    }

    /// <summary>
    ///     Paints an opening demo's cached record onto Match Overview before the parse begins.
    ///     <para>
    ///         <b>Validates file identity first.</b> <c>TryLoadRecord</c> deliberately does not — only
    ///         <c>LoadOrCreate</c> compares — so a record for a demo that has since been replaced at the same
    ///         path would otherwise paint the PREVIOUS match's score and rosters over the new one, and the
    ///         wrong score would sit there for the whole load. The size/mtime pair comes from the library
    ///         entry rather than a fresh <c>FileInfo</c> read, because the library is what wrote the record and
    ///         the two stamp <c>modified</c> in different units — deriving it independently here would fail
    ///         every comparison and silently disable the seed.
    ///     </para>
    /// </summary>
    private void SeedMatchOverviewFromCache(string? localPath, string subjectKey)
    {
        if (_demoCache is null || localPath is not { Length: > 0 })
        {
            return;
        }

        DemoCacheRecord? record = _demoCache.TryLoadRecord(localPath);
        if (record is null)
        {
            return;
        }

        DemoEntry? entry = _library.Entries
            .FirstOrDefault(e => string.Equals(e.FilePath, localPath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null && !record.MatchesFile(entry.FileSizeBytes, entry.Modified.Ticks))
        {
            // The file changed since it was indexed: this record describes a different match.
            return;
        }

        MatchOverviewTab.SeedFromCache(subjectKey, record);
    }

    /// <summary>
    ///     Stores the interactive run's per-player table as the demo's tier-3 scoreboard.
    ///     <para>
    ///         Paired with the highlights scanner's mirror, this is what completes tier 3: the scanner supplies
    ///         highlights (bare mode, affordable library-wide), this supplies the scoreboard (snapshot mode,
    ///         which only a real open runs). Neither alone makes a record <c>FULL</c>, and Match Overview
    ///         reports exactly which half it holds.
    ///     </para>
    /// </summary>
    private void WriteTier3ScoreboardToCache(string? localPath, MetricTable? gameTable, int roundCount)
    {
        if (_demoCache is null || gameTable is null || localPath is not { Length: > 0 })
        {
            return;
        }

        try
        {
            List<CachedStatRow> scoreboard = DemoCacheAnalysisProjector.ProjectScoreboard(gameTable);
            if (scoreboard.Count == 0)
            {
                // A run that produced no per-player rows is not a scoreboard. Writing an empty list would
                // stamp the tier and let ClassifyCached read FULL off a record with nothing in it.
                return;
            }

            (int? ctSide, int? tSide) = DemoCacheAnalysisProjector.ComputeSideWins(gameTable);

            // UpdateExisting, not Update: the record's file identity belongs to whichever writer established
            // it, and the two existing writers disagree on local-vs-UTC mtime. Restating it here in a third
            // convention would fail MatchesFile and discard the tier-2 roster. See DemoCacheStore.
            _demoCache.UpdateExisting(localPath, record =>
            {
                record.Scoreboard = scoreboard;
                record.CtSideWins = ctSide;
                record.TSideWins = tSide;
                record.AnalysisRoundCount = roundCount;

                // Only the analysis STAMP is set here, NEVER AnalysisState: that field tracks the highlights
                // scan's own lifecycle and is the scanner's alone. Setting it from here said "a scan
                // succeeded" on a demo that had never been scanned, so the highlight section — which reads it
                // — asserted "No highlights fired for this demo" while the harvest was still running, and
                // would have overridden the failure copy if that harvest then threw.
                DemoCacheStore.StampAnalysis(record);
            });
        }
        catch (Exception ex)
        {
            // Non-fatal: the open itself succeeded and the page is already showing these numbers live. Logged
            // rather than swallowed — a silently-failing cache write is precisely how the highlight section
            // came to be empty for every demo in the first place.
            AppLog.CacheWriteFailed(DiagLog, localPath, ex);
        }
    }

    /// <summary>
    ///     Renders a Library selection's CACHED record on Match Overview — the browsing gesture, as opposed to
    ///     the double-click that opens it.
    ///     <para>
    ///         <b>Starts nothing.</b> One index lookup and one small sidecar read: no parser, no header read,
    ///         no <c>HeavyJobGate</c>, no queue. That is the whole premise of "Match Overview is a cache
    ///         render" — and with one heavy parse allowed machine-wide, a preview that parsed would make
    ///         arrow-keying the library strictly worse than the card grid it replaces.
    ///     </para>
    ///     <para>
    ///         Deliberately does NOT switch tabs: the page fills behind the user so it is already there when
    ///         they go looking, and arrow-key browsing stays usable. It also never routes through
    ///         <c>BeginOpening</c>, which means "a load is starting" and would light the stage strip for a
    ///         demo nothing is doing anything to.
    ///     </para>
    /// </summary>
    private void PreviewDemoFromCache(DemoEntry entry)
    {
        if (_demoCache is null)
        {
            return;
        }

        // Never replace the page for the demo that is actually open — the live render is strictly richer
        // than its own cached record, and a selection is not a request to leave it.
        if (_loadedDemoPath is { Length: > 0 } open
            && string.Equals(open, entry.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Identity-checked for the same reason the open-path seed is: TryLoadRecord does not compare, so a
        // demo replaced at a path the library still lists would render the OLD match's score and rosters under
        // the new file's name — and a preview gives the user no pipeline to correct it.
        DemoCacheRecord? record = _demoCache.TryLoadRecord(entry.FilePath);
        if (record is not null && !record.MatchesFile(entry.FileSizeBytes, entry.Modified.Ticks))
        {
            record = null;
        }

        if (record is null)
        {
            // Known to the library but never indexed (or its sidecar is gone). Still worth rendering: the
            // page's NOT INDEXED state carries the action that fixes it.
            record = new DemoCacheRecord
            {
                Path = entry.FilePath,
                Size = entry.FileSizeBytes,
                ModifiedTicks = entry.Modified.Ticks,
                Map = entry.MapName
            };
        }

        MatchOverviewTab.SetCachedRecord(record);

        // Offer the way back only while a different demo is genuinely open.
        MatchOverviewTab.LiveDemoName = _loadedDemoPath is { Length: > 0 } live
            ? Path.GetFileName(live)
            : null;
    }

    /// <summary>
    ///     Re-renders Match Overview for the demo that is actually OPEN, after the user has been previewing a
    ///     cached one. The shell owns this because only it can re-derive the live pipeline state; the page
    ///     keeps no stashed copy to go stale.
    /// </summary>
    private void RestoreLiveMatchOverview()
    {
        if (_loadedDemoPath is not { Length: > 0 } path)
        {
            MatchOverviewTab.Clear();
            return;
        }

        ParsedDemo? parsed = (ModuleContext as ICurrentDemoSource)?.CurrentDemo;
        if (parsed is null)
        {
            MatchOverviewTab.Clear();
            return;
        }

        // Replay the same pushes the load funnel made, in the same order, against the same key. Re-deriving
        // beats stashing: a stash taken mid-load would be a snapshot of a page that was still filling in.
        MatchOverviewTab.BeginOpening(Path.GetFileName(path), null, null, path);
        MatchOverviewTab.IsSampleClip = IsTourSample(path);
        MatchOverviewTab.SetSummary(path, parsed);
        MatchOverviewTab.SetParseWarnings(path, parsed.Warnings); // S11 damaged-demo banner
        TryPushTeamNames(path);

        if (StatsTab.GameTable is not null)
        {
            MatchOverviewTab.BeginAnalysis(path);
            MatchOverviewTab.SetAnalysis(path, StatsTab.GameTable, StatsTab.TeamScoresBySort,
                StatsTab.Rounds.Count);
            MatchOverviewTab.SetTeamScores(path,
                StatsTab.TeamScoresBySort.GetValueOrDefault(0),
                StatsTab.TeamScoresBySort.GetValueOrDefault(1));
        }
    }

    /// <summary>Bookmarks panel. In-memory + best-effort desktop persistence.</summary>
    public BookmarksViewModel Bookmarks { get; }

    // 3.5a — Parser-tab RelayCommand shims. XAML retargets in 3.5d.
    /// <summary>Collapse all cards command.</summary>
    public ICommand CollapseAllCardsCommand => ParserTab.CollapseAllCardsCommand;

    /// <summary>Current tracker.</summary>
    public EntityTracker? CurrentTracker => EntityTab.CurrentTracker;

    /// <summary>
    ///     Session-only debugger holding the user's active breakpoints. Tier 1 (frame/tick/
    ///     game-event/round transitions) + Tier 3 (parser-internal packet#, decode error,
    ///     delta-on-unknown). Cleared when the app exits.
    /// </summary>
    public DebuggerService Debugger { get; } = new();

    /// <summary>UI binding surface for <see cref="Debugger" />.</summary>
    public DebuggerViewModel DebuggerPanel { get; }

    /// <summary>Entity delta field count.</summary>

    public int EntityDeltaFieldCount
    {
        get => EntityTab.EntityDeltaFieldCount;
        set => EntityTab.EntityDeltaFieldCount = value;
    }

    // 3.4a — Entity-tracking collections now owned by EntityTab. Pass-through shims
    //         keep current XAML bindings ({Binding EntityListItems}, EntityFieldNodes)
    //         and in-class call sites working unchanged. XAML retargets in 3.5.
    /// <summary>Entity field nodes.</summary>
    public ObservableCollection<PayloadNode> EntityFieldNodes => EntityTab.EntityFieldNodes;

    /// <summary>Entity groups.</summary>
    public ObservableCollection<EntityGroup> EntityGroups => EntityTab.EntityGroups;

    /// <summary>Entity header text.</summary>

    public string EntityHeaderText
    {
        get => EntityTab.EntityHeaderText;
        set => EntityTab.EntityHeaderText = value;
    }

    /// <summary>Entity list items.</summary>
    public ObservableCollection<EntityListItem> EntityListItems => EntityTab.EntityListItems;

    /// <summary>Entity status text.</summary>

    public string EntityStatusText
    {
        get => EntityTab.EntityStatusText;
        set => EntityTab.EntityStatusText = value;
    }

    /// <summary>Entity tab.</summary>
    public EntityTrackingTabViewModel EntityTab { get; }

    /// <summary>Expand all cards command.</summary>
    public ICommand ExpandAllCardsCommand => ParserTab.ExpandAllCardsCommand;

    // PayloadNodes / SelectedFrameMessages moved to ParserTab (3.5a). Compat shims
    // exposed above route the legacy paths to the ParserTab collections.
    // FrameGameEvents moved to ReplayTab (3.5b) — see shim below.
    /// <summary>Frame game events.</summary>
    public ObservableCollection<FrameGameEventViewModel> FrameGameEvents => ReplayTab.FrameGameEvents;

    // ── Parser-tab state (now owned by ParserTab; exposed here as compat shims
    //    so existing in-class call sites and current XAML bindings keep working
    //    unchanged. XAML may opt into `ParserTab.X` paths whenever convenient).
    /// <summary>Frame header fields.</summary>
    public ObservableCollection<FrameHeaderFieldViewModel> FrameHeaderFields => ParserTab.FrameHeaderFields;

    // ── Parser-tab scalars (3.3b) — pass-through shims. The forwarder hooked
    //    in the constructor re-raises PropertyChanged for these names whenever
    //    ParserTab raises its own, keeping legacy `{Binding FrameHeaderText}`
    //    etc. XAML bindings live until the 3.5 XAML sweep retargets them.
    /// <summary>Frame header text.</summary>
    public string FrameHeaderText
    {
        get => ParserTab.FrameHeaderText;
        set => ParserTab.FrameHeaderText = value;
    }

    /// <summary>Frame rows.</summary>
    public TrimmableObservableCollection<HarvestFrameRowViewModel> FrameRows => ParserTab.FrameRows;

    /// <summary>Frames.</summary>
    public TrimmableObservableCollection<DemoFrame> Frames { get; } = [];

    // ── Game event seek filter ────────────────────────────────────────────────
    // Shown as right-click context menu on the ▶⚡ button.  When the list is empty (cleared
    // between file loads) every event type passes.  Items are never removed — new event names
    // seen in a loaded demo are appended with IsEnabled=true.
    /// <summary>Game event filters.</summary>
    public ObservableCollection<GameEventFilterItem> GameEventFilters { get; } =
    [
        new("player_death"),
        new("player_hurt"),
        new("bomb_planted"),
        new("bomb_defused"),
        new("bomb_exploded"),
        new("bomb_beginplant"),
        new("bomb_begindefuse"),
        new("round_start"),
        new("round_end"),
        new("round_officially_ended"),
        new("round_freeze_end"),
        new("weapon_fire"),
        new("player_blind"),
        new("flashbang_detonate"),
        new("hegrenade_detonate"),
        new("inferno_startburn"),
        new("inferno_expire"),
        new("molotov_detonate"),
        new("grenade_thrown"),
        new("player_connect"),
        new("player_disconnect"),
        new("player_team")
    ];

    // ── Entity-tab scalars (3.4b) — pass-through shims. The forwarder hooked in
    //     the constructor re-raises PropertyChanged for these names whenever
    //     EntityTab raises its own. XAML retargets in 3.5.
    /// <summary>Has entities.</summary>
    public bool HasEntities
    {
        get => EntityTab.HasEntities;
        set => EntityTab.HasEntities = value;
    }

    /// <summary>Has entity selection.</summary>

    public bool HasEntitySelection
    {
        get => EntityTab.HasEntitySelection;
        set => EntityTab.HasEntitySelection = value;
    }

    /// <summary>Has frame game events.</summary>

    public bool HasFrameGameEvents
    {
        get => ReplayTab.HasFrameGameEvents;
        set => ReplayTab.HasFrameGameEvents = value;
    }

    /// <summary>Has inner messages.</summary>

    public bool HasInnerMessages
    {
        get => ParserTab.HasInnerMessages;
        set => ParserTab.HasInnerMessages = value;
    }

    /// <summary>Has message cards.</summary>

    public bool HasMessageCards
    {
        get => ParserTab.HasMessageCards;
        set => ParserTab.HasMessageCards = value;
    }

    /// <summary>Has parse chain.</summary>

    public bool HasParseChain
    {
        get => ParserTab.HasParseChain;
        set => ParserTab.HasParseChain = value;
    }

    /// <summary>Has sub tick events.</summary>

    public bool HasSubTickEvents
    {
        get => ReplayTab.HasSubTickEvents;
        set => ReplayTab.HasSubTickEvents = value;
    }

    // ── Replay-tab scalar / command pass-through shims (3.5b) ─────────────────
    // The ReplayTab forwarder above re-raises PropertyChanged for these names
    // when ReplayTab raises its own, keeping legacy {Binding HasTickGroups} etc.
    // XAML bindings alive until 3.5d retargets them.
    /// <summary>Has tick groups.</summary>
    public bool HasTickGroups
    {
        get => ReplayTab.HasTickGroups;
        set => ReplayTab.HasTickGroups = value;
    }

    // HasTickGroups moved to ReplayTab in 3.5b — see shim block below.
    /// <summary>Has watched.</summary>
    public bool HasWatched
    {
        get => EntityTab.HasWatched;
        set => EntityTab.HasWatched = value;
    }

    /// <summary>Hex view decompressed.</summary>
    public HarvestHexViewModel HexViewDecompressed => ParserTab.HexViewDecompressed;

    /// <summary>Hex view raw.</summary>
    public HarvestHexViewModel HexViewRaw => ParserTab.HexViewRaw;

    /// <summary>Is decompressed tab available.</summary>

    public bool IsDecompressedTabAvailable
    {
        get => ParserTab.IsDecompressedTabAvailable;
        set => ParserTab.IsDecompressedTabAvailable = value;
    }

    /// <summary>Is seeking entities.</summary>

    public bool IsSeekingEntities
    {
        get => EntityTab.IsSeekingEntities;
        set => EntityTab.IsSeekingEntities = value;
    }

    /// <summary>Is tick view.</summary>

    public bool IsTickView
    {
        get => ReplayTab.IsTickView;
        set => ReplayTab.IsTickView = value;
    }

    /// <summary>Message cards.</summary>
    public ObservableCollection<HarvestCardViewModel> MessageCards => ParserTab.MessageCards;

    // ── Per-tab seams ─────────────────────────────────────────────────────────
    // These VMs are instantiated up-front so XAML can bind through `ParserTab.*`,
    // `EntityTab.*`, `AnalysisTab.*` once state migration begins. While the
    // migration is in progress they remain empty shells that hold a shared
    // <see cref="Navigation"/> reference; ownership of observable state moves
    // out of <c>MainViewModel</c> one tab at a time.
    /// <summary>Navigation.</summary>
    public FrameNavigationViewModel Navigation { get; }

    /// <summary>
    ///     VS Code-style Output panel. Aggregates unknown-message-type warnings (from the
    ///     static <see cref="DemoParser.OnUnknownMessageType" />), decode errors (from each per-seek
    ///     tracker's <c>DecodeErrorRaised</c>), tracker errors, and build/test output.
    /// </summary>
    public OutputPanelViewModel Output { get; }

    /// <summary>Command palette (Ctrl+P). Reads tracker / proto-index / frame-count live.</summary>
    public CommandPaletteViewModel Palette { get; }

    /// <summary>Parse chain.</summary>
    public ObservableCollection<ParseChainEntry> ParseChain => ParserTab.ParseChain;

    /// <summary>Parser tab.</summary>
    public ParserTabViewModel ParserTab { get; }

    // 3.5a additions — collections that previously lived on the shell:
    /// <summary>Payload nodes.</summary>
    public ObservableCollection<PayloadNode> PayloadNodes => ParserTab.PayloadNodes;

    /// <summary>
    ///     Slot-keyed player names (from string tables + connect events). Used for entity-list display + game-event
    ///     filters.
    /// </summary>
    public IReadOnlyDictionary<int, string> PlayerNames { get; private set; } = new Dictionary<int, string>();

    /// <summary>Replay tab.</summary>
    public ReplayTabViewModel ReplayTab { get; }

    /// <summary>Select decompressed tab command.</summary>
    public ICommand SelectDecompressedTabCommand => ParserTab.SelectDecompressedTabCommand;

    /// <summary>Select raw tab command.</summary>
    public ICommand SelectRawTabCommand => ParserTab.SelectRawTabCommand;

    /// <summary>Selected entity item.</summary>

    public object? SelectedEntityItem
    {
        get => EntityTab.SelectedEntityItem;
        set => EntityTab.SelectedEntityItem = value;
    }

    /// <summary>Selected entity list item.</summary>

    public EntityListItem? SelectedEntityListItem
    {
        get => EntityTab.SelectedEntityListItem;
        set => EntityTab.SelectedEntityListItem = value;
    }

    // ── Parser-tab selection-coupled shims (3.5a) ─────────────────────────────
    // XAML retargets in 3.5d; until then these keep `{Binding SelectedFrame}`,
    // `{Binding HasParseChain}`, etc. unchanged.
    /// <summary>Selected frame.</summary>
    public DemoFrame? SelectedFrame
    {
        get => ParserTab.SelectedFrame;
        set => ParserTab.SelectedFrame = value;
    }

    /// <summary>Selected frame messages.</summary>
    public ObservableCollection<NetMessage> SelectedFrameMessages => ParserTab.SelectedFrameMessages;

    /// <summary>Selected frame row.</summary>

    public HarvestFrameRowViewModel? SelectedFrameRow
    {
        get => ParserTab.SelectedFrameRow;
        set => ParserTab.SelectedFrameRow = value;
    }

    /// <summary>Selected message.</summary>

    public NetMessage? SelectedMessage
    {
        get => ParserTab.SelectedMessage;
        set => ParserTab.SelectedMessage = value;
    }

    /// <summary>Selected payload node.</summary>

    public PayloadNode? SelectedPayloadNode
    {
        get => ParserTab.SelectedPayloadNode;
        set => ParserTab.SelectedPayloadNode = value;
    }

    /// <summary>Selected tick frame.</summary>

    public DemoFrame? SelectedTickFrame
    {
        get => ReplayTab.SelectedTickFrame;
        set => ReplayTab.SelectedTickFrame = value;
    }

    /// <summary>Selected tick frame row.</summary>

    public HarvestFrameRowViewModel? SelectedTickFrameRow
    {
        get => ReplayTab.SelectedTickFrameRow;
        set => ReplayTab.SelectedTickFrameRow = value;
    }

    /// <summary>Selected tick group.</summary>

    public TickGroup? SelectedTickGroup
    {
        get => ReplayTab.SelectedTickGroup;
        set => ReplayTab.SelectedTickGroup = value;
    }

    /// <summary>Show delta fields only.</summary>

    public bool ShowDeltaFieldsOnly
    {
        get => EntityTab.ShowDeltaFieldsOnly;
        set => EntityTab.ShowDeltaFieldsOnly = value;
    }

    // ── Entity-tab selection-coupled state (3.4c) ─────────────────────────────
    /// <summary>Show dormant entities.</summary>
    public bool ShowDormantEntities
    {
        get => EntityTab.ShowDormantEntities;
        set => EntityTab.ShowDormantEntities = value;
    }

    /// <summary>Show raw hex.</summary>

    public bool ShowRawHex
    {
        get => ParserTab.ShowRawHex;
        set => ParserTab.ShowRawHex = value;
    }

    // SubTickEvents / TickGroups / TickViewFrames / TickViewFrameRows moved to
    // ReplayTab (3.5b). TickGroups had a brief stop on EntityTab (3.4a) — the
    // collection never had logic-coupling there; the placement is reassessed in
    // 3.5b and the cluster is unified under ReplayTab. WatchedValues is still
    // owned by EntityTab.
    /// <summary>Sub tick events.</summary>
    public ObservableCollection<SubTickEventViewModel> SubTickEvents => ReplayTab.SubTickEvents;

    /// <summary>Tick groups.</summary>
    public ObservableCollection<TickGroup> TickGroups => ReplayTab.TickGroups;

    /// <summary>Styled row view-models for the tick-view frame list — parallel to <see cref="TickViewFrames" />.</summary>
    public ObservableCollection<HarvestFrameRowViewModel> TickViewFrameRows => ReplayTab.TickViewFrameRows;

    /// <summary>Tick view frames.</summary>
    public ObservableCollection<DemoFrame> TickViewFrames => ReplayTab.TickViewFrames;

    /// <summary>Toggle tick view command.</summary>
    public ICommand ToggleTickViewCommand => ReplayTab.ToggleTickViewCommand;

    /// <summary>Watched values.</summary>
    public ObservableCollection<WatchedValue> WatchedValues => EntityTab.WatchedValues;

    // UpdateWatchedValues / WatchField moved to EntityTab in 3.4c.
    // WriteUInt32LE moved to ParserTab in 3.5a.

    /// <summary>Dispose.</summary>
    public void Dispose()
    {
        // Persist the session on teardown too (belt-and-suspenders alongside the
        // App.OnExit hook). No-op on WASM (SessionStore self-guards).
        SaveSession();
        if (_gate is not null)
        {
            _gate.Changed -= OnGateChanged;
        }

        if (LiveSync is not null)
        {
            LiveSync.StateChanged -= OnLiveSyncStateChanged;
        }

        _liveSyncStatus?.Dispose();

        if (ReelJob is not null)
        {
            ReelJob.StatusChanged -= OnReelStatusChanged;
        }

        if (_reelJobStatus is not null)
        {
            _reelJobStatus.DismissRequested -= OnReelDismissRequested;
            _reelJobStatus.Dispose();
        }

        if (_processingQueue is not null)
        {
            _processingQueue.Changed -= OnProcessingQueueChanged;
        }

        _processingQueueStatus?.Dispose();
        _idle?.Dispose();

        if (SettingsOverlay is { } overlay)
        {
            overlay.CloseRequested -= OnSettingsOverlayCloseRequested;
            overlay.Dispose();
            SettingsOverlay = null;
        }

        if (FirstRunOverlay is { } wizard)
        {
            wizard.Completed -= OnFirstRunOverlayCompleted;
            FirstRunOverlay = null;
        }

        EntityTab.SeekCts?.Cancel();
        _perfTimer?.Stop();
        _library.Save();
        _library.Dispose();
        SelectedTab?.Deactivate();
        _moduleContext?.Dispose();
        Playback.Dispose();
        // Detach the STATIC unknown-message-type handler — otherwise this VM is pinned for the
        // process lifetime and a subsequent shell would leak alongside it.
        DemoParser.OnUnknownMessageType -= _onUnknownMessageType;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Called once by the App bootstrap after constructing the shell (desktop lifetime only). Builds the
    ///     Live Sync status mapper over the engine, subscribes for the speed-lock re-raise, and reconciles the
    ///     chip into <see cref="Chips" /> per the current <c>chrome.livesync</c> gate.
    /// </summary>
    internal void AttachLiveSync(ILiveSyncService liveSync)
    {
        LiveSync = liveSync;
        _liveSyncStatus = new LiveSyncStatusViewModel(liveSync, _moduleContext, Playback, OpenSettings,
            LoadDemoFromPathAsync, () => IsLiveSyncEnabled);

        // Wire the 2D-tab CS2 indicator: the status VM IS the ILiveSyncHudState projection. Set once
        // (never cleared) — the projection folds the chrome.livesync gate into its own IsActive, so a gate
        // flip while the 2D tab is active still shows/hides the indicator (via NotifyHudGateChanged below).
        _moduleContext?.SetLiveSyncHud(_liveSyncStatus);

        // The shell tracks StateChanged only for the NavStrip speed-lock shim; the mapper handles the chip.
        liveSync.StateChanged += OnLiveSyncStateChanged;
        ReconcileChips();
        OnPropertyChanged(nameof(IsPlaybackSpeedLocked));
    }

    // Raised on the UI thread (ILiveSyncService contract). Re-raise the speed-lock shim so the NavStrip
    // ComboBox enables/disables as the session enters/leaves the Synced sub-states.
    private void OnLiveSyncStateChanged(object? sender, LiveSyncStateChangedEventArgs e) =>
        OnPropertyChanged(nameof(IsPlaybackSpeedLocked));

    // Adds/removes the Live Sync chip to match the gate. Called from AttachLiveSync and on every gate change.
    private void ReconcileChips()
    {
        if (_liveSyncStatus is null)
        {
            return;
        }

        bool shouldShow = IsLiveSyncEnabled;
        bool present = Chips.Contains(_liveSyncStatus.Chip);
        if (shouldShow && !present)
        {
            Chips.Add(_liveSyncStatus.Chip);
        }
        else if (!shouldShow && present)
        {
            Chips.Remove(_liveSyncStatus.Chip);
        }

        // The gate feeds the 2D indicator's visibility too — re-raise so an active 2D tab reflows.
        _liveSyncStatus.NotifyHudGateChanged();
    }

    /// <summary>
    ///     Called once by the App bootstrap after <see cref="AttachLiveSync" /> (desktop lifetime only). Builds
    ///     the Reel status mapper over the job service and reconciles the Reel chip into <see cref="Chips" />
    ///     per its lifecycle. The service handles the live-sync↔reel single-CS2 interlock engine-side; the
    ///     shell only surfaces the chip.
    /// </summary>
    /// <summary>
    ///     The one reel-job status mapper. The Reels dashboard's inline job strip binds to THIS instance —
    ///     it is a second VIEW of one job, never a second job model, so a second mapper would give the chip
    ///     and the strip independently-drifting progress for the same render. The shell owns its lifetime;
    ///     the tab must not dispose it. Null until <see cref="AttachReelJob" /> runs (Browser, tests).
    /// </summary>
    internal ReelJobStatusViewModel? ReelJobStatus => _reelJobStatus;

    /// <summary>
    ///     Resolves the Reels tab's clip tray on demand — set by the composition root, which owns the
    ///     container. A locator rather than a held reference because the Reels tab is a lazy module tab and
    ///     the shell is constructed long before it: staging a clip from Match Overview has to work whether or
    ///     not the user has ever opened Reels, and resolving at press time builds it if it does not exist yet.
    ///     Null (tests, browser) leaves the staging buttons inert rather than absent.
    /// </summary>
    internal Func<HighlightsTabViewModel?>? ReelTrayLocator { get; set; }

    internal void AttachReelJob(IReelJobService reelJob)
    {
        ReelJob = reelJob;
        _reelJobStatus = new ReelJobStatusViewModel(reelJob, OpenReelFolder);
        _reelJobStatus.DismissRequested += OnReelDismissRequested;
        reelJob.StatusChanged += OnReelStatusChanged;
        ReconcileReelChip();
    }

    private void OnReelStatusChanged(object? sender, ReelJobStatus status)
    {
        // A newly-running (or continuing) job un-dismisses, so the chip re-appears for a fresh render.
        if (status.IsRunning)
        {
            _reelDismissed = false;
        }

        ReconcileReelChip();
    }

    private void OnReelDismissRequested(object? sender, EventArgs e)
    {
        _reelDismissed = true;
        ReconcileReelChip();
    }

    // Adds/removes the Reel chip: shown while running, or when a finished result is not yet dismissed.
    private void ReconcileReelChip()
    {
        if (_reelJobStatus is null || ReelJob is null)
        {
            return;
        }

        ReelJobStatus s = ReelJob.Status;
        bool shouldShow = s.IsRunning || s.Phase is not ReelJobPhase.Idle && !_reelDismissed;
        bool present = Chips.Contains(_reelJobStatus.Chip);
        if (shouldShow && !present)
        {
            Chips.Add(_reelJobStatus.Chip);
        }
        else if (!shouldShow && present)
        {
            Chips.Remove(_reelJobStatus.Chip);
        }
    }

    // ── Demo-processing queue chip ─────────────────────────────────────────────

    // The queue posts Changed on the UI thread; reconcile the chip's presence on every change (activity or a
    // pause/resume can flip whether it should show). The status VM handles its own refresh via its own Changed
    // subscription (subscribed first, in its ctor), so counts are fresh when this reconcile reads the queue.
    private void OnProcessingQueueChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ReconcileQueueChip();
        }
        else
        {
            Dispatcher.UIThread.Post(ReconcileQueueChip);
        }
    }

    // Adds/removes the "Processing" chip: shown only while the chrome.processingQueue gate is on (desktop) AND
    // the queue has activity (running/queued) or is transiently paused — so an idle, enabled queue adds no
    // status-strip clutter, and a paused queue always offers a Resume affordance.
    private void ReconcileQueueChip()
    {
        if (_processingQueueStatus is null || _processingQueue is null)
        {
            return;
        }

        bool active = _processingQueue.RunningCount > 0
                      || _processingQueue.QueuedCount > 0
                      || _processingQueue.IsPaused;
        bool shouldShow = IsProcessingQueueEnabled && active;
        bool present = Chips.Contains(_processingQueueStatus.Chip);
        if (shouldShow && !present)
        {
            Chips.Add(_processingQueueStatus.Chip);
        }
        else if (!shouldShow && present)
        {
            Chips.Remove(_processingQueueStatus.Chip);
        }
    }

    /// <summary>
    ///     The library-wide highlight-scan chip — the FOURTH <c>StatusChip</c> consumer.
    ///     Carries what the retired demo card grid used to show through its per-card animation and header
    ///     badge: queue depth, which demo is scanning, and how many rows are stale or failed.
    ///     <para>
    ///         Attached once by the composition root, which owns the scanner. The shell owns the mapper's
    ///         lifetime (it subscribes to the scanner AND the cache store); the Reels tab is handed the same
    ///         instance and never disposes it.
    ///     </para>
    /// </summary>
    internal void AttachHighlightScanStatus(HighlightScanStatusViewModel scanStatus)
    {
        _highlightScanStatus = scanStatus;
        scanStatus.PropertyChanged += (_, e) =>
        {
            // IsRelevant is the only thing chip PRESENCE depends on; the chip re-renders itself from the
            // rest. Subscribing beats polling — the scanner already raises on every queue/store change.
            if (e.PropertyName is nameof(HighlightScanStatusViewModel.IsRelevant))
            {
                ReconcileHighlightScanChip();
            }
        };

        ReconcileHighlightScanChip();
    }

    // Deliberately UNGATED: the chip appears only while work is actually happening, and
    // chrome.processingQueue already established that background work done on the user's behalf is visible
    // to every category. Browser-excluded because scanning needs a filesystem — the same shim the
    // processing-queue chip uses.
    private void ReconcileHighlightScanChip()
    {
        if (_highlightScanStatus is null)
        {
            return;
        }

        bool shouldShow = _highlightScanStatus.IsRelevant && !OperatingSystem.IsBrowser();
        bool present = Chips.Contains(_highlightScanStatus.Chip);
        if (shouldShow && !present)
        {
            Chips.Add(_highlightScanStatus.Chip);
        }
        else if (!shouldShow && present)
        {
            Chips.Remove(_highlightScanStatus.Chip);
        }
    }

    // Opens the finished reel's output folder ("Open folder"). Desktop-only (the reel feature is), and
    // best-effort — a launcher failure must never crash the UI thread.
    private void OpenReelFolder(string path)
    {
        if (OperatingSystem.IsBrowser() || string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            string target = File.Exists(path)
                ? Path.GetDirectoryName(path) ?? path
                : path;
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch
        {
            // Best effort, but no longer silent (v0.6.0): a click that does nothing reads as a
            // broken button, so say what failed in the strip.
            StatusText = $"Couldn't open the reel folder ({path}) — no file manager responded.";
        }
    }

    /// <summary>
    ///     Builds the ItemsSource-driven tab strip from the module registry. The four built-in
    ///     tabs are always present (BuiltInTabsModule auto-registered if the composition root didn't);
    ///     module tabs follow. The concrete read-only <see cref="ModuleContext" /> (with the per-tick
    ///     host player-join) is constructed here and handed to each module's host. Descriptors are
    ///     sorted by (Placement, Order); the initial tab is activated explicitly (the first-tab
    ///     edge — the ItemsSource auto-select may not round-trip through OnSelectedTabChanged).
    /// </summary>
    private void BuildWorkspaceTabs()
    {
        // The read-only push surface every module subscribes to. Per-frame game events resolved from
        // the current frame's GameEventMessages (best-effort; the 2D pilot uses entity deltas).
        _moduleContext = new ModuleContext(
            Playback,
            () => _loadedDemoPath,
            Navigator); // Phase E: modules drive "jump to next event of my type" through the shared navigator

        // Ensure the built-in tabs are registered (idempotent by Id) even if the composition root
        // passed a registry without them, so the shell always has its four tabs.
        _moduleRegistry.Register(new BuiltInTabsModule(this, Diagnostics, LibraryTab, StatsTab, MatchOverviewTab));

        List<WorkspaceTabDescriptor> descriptors = new();
        foreach (IWorkspaceModule module in _moduleRegistry.Modules)
        {
            IEnumerable<string> caps = HostCapabilitiesFor(module);
            ModuleHost host = new(_moduleContext, caps, RouteModuleLog);

            try
            {
                descriptors.AddRange(module.CreateTabs(host));
            }
            catch (Exception ex)
            {
                // Failure isolation: a misbehaving module never crashes the shell.
                RouteModuleLog(ModuleLogLevel.Error, $"Module '{module.Id}' CreateTabs failed: {ex.Message}");
            }
        }

        // Cache the FULL descriptor set (every module, unfiltered) so the live reconcile can re-add a
        // re-enabled tab by reference without re-running CreateTabs. Built once, never rebuilt.
        _allTabDescriptors.Clear();
        _allTabDescriptors.AddRange(descriptors);

        // The gate FILTERS which descriptors become Tabs. A null gate fails open
        // (IsTabEnabled returns true for every tab), preserving the pre-gating behaviour for the
        // designer / test path. Then sort by (Placement, Order) and add, exactly as before.
        foreach (WorkspaceTabDescriptor d in descriptors
                     .Where(IsTabEnabled)
                     .OrderBy(d => d.Placement)
                     .ThenBy(d => d.Order))
        {
            Tabs.Add(d);
        }

        // First-tab edge — activate the initial tab explicitly so the first descriptor reliably
        // receives OnActivated even if the ItemsSource binding doesn't round-trip the setter.
        if (Tabs.Count > 0)
        {
            SelectedTab = Tabs[0];
        }

        // Live reconcile: re-run the filter whenever gate decisions change (a category / override write).
        // Only when a gate is present — the null path never filters, so it never needs to reconcile.
        if (_gate is not null)
        {
            _gate.Changed += OnGateChanged;
        }
    }

    // True when the tab should be shown. Fail-open in two ways: a null gate (no filtering at all) and a
    // TabId with no mapped feature (an ungated tab is always shown).
    private bool IsTabEnabled(WorkspaceTabDescriptor descriptor)
    {
        if (_gate is null)
        {
            return true;
        }

        if (!_tabFeatureIds.TryGetValue(descriptor.TabId, out string? featureId))
        {
            return true;
        }

        return _gate.IsEnabled(featureId);
    }

    // IFeatureGate.Changed handler. The production gate already marshals Changed to the UI thread, and a
    // test gate raises it inline on the writing (UI) thread — so CheckAccess() is normally true and the
    // work runs synchronously (observable without a RunJobs pump). The Post branch is a defensive marshal
    // for any off-thread raise, since ApplyGateChange mutates the bound Tabs collection AND re-raises
    // PropertyChanged (both must land on the UI thread).
    private void OnGateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyGateChange();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyGateChange);
        }
    }

    // The single UI-thread body both OnGateChanged branches route through. Reconciles the
    // tab strip to the new gate decisions (tab-level enforcement, B-i), force-closes any owned chrome whose
    // gate just hid it (so a previously-open drawer / rail doesn't linger after its toggle is gated away),
    // then re-raises PropertyChanged for every gate shim so the bound sub-feature / chrome IsVisible reflows.
    // The gate reads live, so each shim already reflects the new decision when read here.
    private void ApplyGateChange()
    {
        ReconcileTabs();

        // Force owned panels closed when their chrome is now gated off. Without this a drawer/rail a
        // developer left open would stay open after a downgrade even though its toggle button is hidden.
        if (!IsDebuggerChromeEnabled)
        {
            IsDebuggerPanelVisible = false;
        }

        if (!IsOutputChromeEnabled)
        {
            Output.IsVisible = false;
        }

        OnPropertyChanged(nameof(IsHexPaneEnabled));
        OnPropertyChanged(nameof(IsParseChainEnabled));
        OnPropertyChanged(nameof(IsDebuggerChromeEnabled));
        OnPropertyChanged(nameof(IsOutputChromeEnabled));
        OnPropertyChanged(nameof(IsBreakpointNavEnabled));
        OnPropertyChanged(nameof(IsSerializerSchemaEnabled));
        OnPropertyChanged(nameof(IsAnalysisBreakpointsEnabled));
        OnPropertyChanged(nameof(IsLiveSyncEnabled));
        OnPropertyChanged(nameof(IsProcessingQueueEnabled));
        OnPropertyChanged(nameof(HiddenFeatureCount));
        OnPropertyChanged(nameof(HiddenFeatureNote));

        // The chrome.livesync gate may have flipped — add/remove the Live Sync chip to match.
        ReconcileChips();
        // The chrome.processingQueue gate may have flipped — add/remove the Processing chip to match.
        ReconcileQueueChip();
    }

    /// <summary>
    ///     Reconciles the <see cref="Tabs" /> collection to the gate-enabled set BY TabId — never a full
    ///     rebuild (that would tear down cached module-tab VM state). Removes now-disabled tabs
    ///     (neighbor-selecting FIRST when the removed tab is selected, so Avalonia's auto-reselect never
    ///     lands somewhere arbitrary) and inserts now-enabled tabs at their sorted (Placement, Order)
    ///     position. Selection is by descriptor identity, so shifting positions need no resync.
    /// </summary>
    private void ReconcileTabs()
    {
        if (_gate is null)
        {
            return;
        }

        // Desired = every cached descriptor the gate now enables, in the strip's sort order.
        List<WorkspaceTabDescriptor> desired = _allTabDescriptors
            .Where(IsTabEnabled)
            .OrderBy(d => d.Placement)
            .ThenBy(d => d.Order)
            .ToList();
        HashSet<string> desiredIds = new(desired.Select(d => d.TabId), StringComparer.Ordinal);

        // (1) Remove tabs that are now disabled. Snapshot first (mutating Tabs during the walk).
        foreach (WorkspaceTabDescriptor removed in Tabs.Where(d => !desiredIds.Contains(d.TabId)).ToList())
        {
            if (ReferenceEquals(SelectedTab, removed))
            {
                // Neighbor-select BEFORE the remove so the selection is already on a surviving tab when
                // Avalonia's TabControl reacts — no auto-reselect race through the sync guard.
                SelectedTab = ChooseNeighbor(removed, desired);
            }

            removed.Deactivate(); // idempotent; drops the realized View if it was the (old) selected tab.
            Tabs.Remove(removed);
        }

        // (2) Insert tabs that are now enabled but absent, each at its sorted position. Same descriptor
        // objects as the cache → reference-equality dedupe keeps a still-present tab from doubling.
        foreach (WorkspaceTabDescriptor add in desired)
        {
            if (Tabs.Any(t => ReferenceEquals(t, add)))
            {
                continue;
            }

            Tabs.Insert(SortedInsertIndex(add), add);
        }

        // (3) Inserts/removes shift positions but never identity, so there is nothing to re-sync — the
        // selected DESCRIPTOR is still the selected descriptor. Only the "nothing is selected" case needs
        // handling (shouldn't happen — Library is Required).
        if (SelectedTab is null && Tabs.Count > 0)
        {
            SelectedTab = Tabs[0];
        }
    }

    // The tab selection lands on when the SELECTED tab is removed: the nearest still-enabled tab with a
    // LOWER sort position, else the first (Library) tab. `desired` is already sorted, so the last entry
    // that sorts before the removed tab is that nearest-lower neighbor.
    private static WorkspaceTabDescriptor? ChooseNeighbor(
        WorkspaceTabDescriptor removed, List<WorkspaceTabDescriptor> desired)
    {
        if (desired.Count == 0)
        {
            return null;
        }

        WorkspaceTabDescriptor? nearestLower = null;
        foreach (WorkspaceTabDescriptor candidate in desired)
        {
            if (CompareTabs(candidate, removed) < 0)
            {
                nearestLower = candidate;
            }
            else
            {
                break;
            }
        }

        return nearestLower ?? desired[0];
    }

    // First index in Tabs at which `descriptor` sorts strictly before the existing entry — i.e. the
    // insert point that keeps Tabs ordered by (Placement, Order), placing a tie AFTER equal-key tabs
    // (matching the initial OrderBy/ThenBy stable-sort append-for-ties).
    private int SortedInsertIndex(WorkspaceTabDescriptor descriptor)
    {
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (CompareTabs(descriptor, Tabs[i]) < 0)
            {
                return i;
            }
        }

        return Tabs.Count;
    }

    // The strip's sort key: Placement first, then Order — identical to BuildWorkspaceTabs' OrderBy/ThenBy.
    private static int CompareTabs(WorkspaceTabDescriptor a, WorkspaceTabDescriptor b)
    {
        int byPlacement = ((int)a.Placement).CompareTo((int)b.Placement);
        return byPlacement != 0 ? byPlacement : a.Order.CompareTo(b.Order);
    }

    // First-party modules get all capabilities; (future) third-party defaults are read-only +
    // Playback.Observe + UI.Contribute.
    private static IEnumerable<string> HostCapabilitiesFor(IWorkspaceModule module) =>
        ModuleHost.FirstPartyCapabilities;

    // Routes a module log message to the Output panel's decode-errors channel (best-effort).
    private void RouteModuleLog(ModuleLogLevel level, string message) =>
        Output.DecodeErrors.Append(new OutputRow(-1, "MOD", level.ToString().ToUpperInvariant(), message));


    /// <summary>
    ///     Activates the newly-selected workspace tab and deactivates the previous one. Drives
    ///     the realize-View / set-DataContext / OnActivated lifecycle on the descriptor.
    /// </summary>
    partial void OnSelectedTabChanged(WorkspaceTabDescriptor? oldValue, WorkspaceTabDescriptor? newValue)
    {
        oldValue?.Deactivate();

        if (newValue is not null && _moduleContext is not null)
        {
            newValue.Activate(_moduleContext);
        }
    }

    /// <summary>
    ///     Loads a demo from a file path through the full shell pipeline (parse → frames → modules →
    ///     analysis). A plain load helper, compiled in ALL configurations: the headless test harness
    ///     drives it (and must also run in Release); only the DEV auto-load CALL SITE in
    ///     <c>MainView.axaml.cs</c> (DEMO_PATH env var on attach) stays <c>#if DEBUG</c>-gated.
    /// </summary>
    public async Task AutoLoadDemoAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        UnloadDemoState();

        IsLoading = true;
        StatusText = $"Auto-loading {Path.GetFileName(path)}…";
        // Match Overview parity with the interactive funnel: show the landing immediately (CLI/debug auto-load).
        MatchOverviewTab.BeginOpening(Path.GetFileName(path), null, null, path);
        MatchOverviewTab.IsSampleClip = IsTourSample(path);
        MatchOverviewTab.SetStage(path, "Parsing demo…", 0.15);

        try
        {
            byte[] rawBytes = await File.ReadAllBytesAsync(path);
            _demoBytes = rawBytes;
            _loadedDemoPath = path; // Diagnostics Session card
            // The interactive load takes the machine-wide
            // heavy-parse gate — background indexing/scanning yields at its next demo boundary.
            // During a reel render the acquisition throws ReelInProgressException, which the
            // site's existing failure handling surfaces with its clear user-facing message.
            ParsedDemo parsed;
            using (_heavyJobGate is null ? null : await _heavyJobGate.AcquireInteractiveAsync())
            {
                parsed = await Task.Run(() => DemoParser.Parse(rawBytes.AsMemory()));
            }

            MatchOverviewTab.SetSummary(path, parsed);
            MatchOverviewTab.SetParseWarnings(path, parsed.Warnings); // S11 damaged-demo banner
            FrameRows.Clear();
            int frameNum = 0;
            foreach (DemoFrame frame in parsed.Frames)
            {
                Frames.Add(frame);
                frameNum++;
                FrameRows.Add(new HarvestFrameRowViewModel
                {
                    FrameNumber = frameNum,
                    FrameType = frame.Command,
                    MessageCount = frame.InnerMessages.Count,
                    ByteSize = frame.RawLength,
                    Source = frame
                });
            }

            HasFile = Frames.Count > 0;
            _allFrames = Frames.ToList();
            // Register the demo with the controller (frame list + tick rate for the play loop).
            Playback.LoadDemo(_allFrames, parsed.TickRate);
            // Hand the module context the stable identity roster (slot / steamID / name; no
            // team — team is per-tick via the host player-join).
            _moduleContext?.SetRoster(parsed.Players.Values.Select(p => new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));
            _moduleContext?.SetGameEvents(parsed.AllGameEvents); // pre-decoded timeline for event-driven modules
            _moduleContext?.SetMapName(parsed.MapName); // data-driven map identity for asset selection
            _moduleContext?.SetDemo(parsed); // M5: expose the loaded demo to the first-party Workbench
            BuildUnknownMessageCensus(parsed);
            // navigation-review Phase A — precompute round / event / tick boundary indices once,
            // drained alongside the unknown-message census. The six *Frame* nav methods + the Phase C
            // strip binary-search these instead of re-scanning the frame list on every press.
            Navigator.Build(_allFrames);
            // #4/#5 — calibrate the shared game-clock once (first round_freeze_end) for the 2D round
            // timer + bomb/defuse timers; consumed via IModuleContext.CurtimeSeconds.
            ApplyGameClock(_allFrames, parsed.TickRate);
            // Mirror the production load path: signal active modules to resync to the new demo (see the
            // detailed rationale on the RaiseDemoReset call in LoadDemoFromBytesAsync).
            _moduleContext?.RaiseDemoReset();
            _players = parsed.Players;
            _playersByUserId = parsed.Players.Values
                .Where(p => p.UserId > 0)
                .ToDictionary(p => p.UserId);

            (PlayerNames, _nameByUserId) = PlayerSnapshotBuilder.BuildNameLookups(parsed);

            _replayDemoContext = DemoAnalyzer.BuildEventContext(parsed);
            // Match Overview stage parity with the interactive funnel (see LoadDemoFromBytesAsync).
            MatchOverviewTab.BeginAnalysis(path);
            // SHA-256 the demo bytes (off-thread) to key its persisted graph breakpoints.
            string demoKey = await Task.Run(() => GraphBreakpointStore.ComputeDemoKey(rawBytes));
            await Analysis.RunAsync(parsed, demoKey);
            MatchOverviewTab.SetAnalysis(path, StatsTab.GameTable, StatsTab.TeamScoresBySort, StatsTab.Rounds.Count);
            // Per-team round wins from the same evaluation — each team's total across BOTH halves.
            MatchOverviewTab.SetTeamScores(
                path,
                StatsTab.TeamScoresBySort.GetValueOrDefault(0),
                StatsTab.TeamScoresBySort.GetValueOrDefault(1));
            _teamNamesTask = ResolveTeamNamesAsync(path);
            HexViewRaw.Load(rawBytes);
            StatusText = $"{Path.GetFileName(path)}  —  {Frames.Count} frames  •  Select a frame";

            // Parity with the interactive load path: resume the walkthrough's deferred demo segment if a
            // first-run user's first demo arrives via CLI/debug auto-load. No-op unless the tour is awaiting.
            _tutorial.NotifyDemoLoaded();
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-load failed: {ex.Message}";
            MatchOverviewTab.Fail(path, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Unknown net-message census (reverse-engineering support) ───────────────

    /// <summary>
    ///     Drains the unknown-message accumulator after a parse into (a) a per-frame census handed
    ///     to the Parser tab for its unknown-message cards, and (b) one grouped, seekable
    ///     Output-panel row per unknown type (clicking jumps to the first occurrence). Runs on the
    ///     UI thread once <see cref="Frames" /> is populated. Replaces the old per-occurrence UI
    ///     dispatch, which posted tens of thousands of rows on a typical pro demo.
    /// </summary>
    private void BuildUnknownMessageCensus(ParsedDemo parsed)
    {
        if (_unknownAccumulator.IsEmpty)
        {
            ParserTab.UnknownByFrame = null;
            return;
        }

        Dictionary<int, List<UnknownMessageInfo>> census = new();
        Dictionary<int, UnknownTypeAggregate> byType = new();
        foreach (UnknownMessageInfo info in _unknownAccumulator)
        {
            if (!census.TryGetValue(info.FrameNumber, out List<UnknownMessageInfo>? list))
            {
                census[info.FrameNumber] = list = new List<UnknownMessageInfo>();
            }

            list.Add(info);

            if (!byType.TryGetValue(info.TypeId, out UnknownTypeAggregate? agg))
            {
                byType[info.TypeId] = new UnknownTypeAggregate(info.TypeName, info.FrameNumber, info.Length);
            }
            else
            {
                agg.Count++;
                if (info.FrameNumber < agg.FirstFrame)
                {
                    agg.FirstFrame = info.FrameNumber;
                }
            }
        }

        ParserTab.UnknownByFrame = census;

        foreach ((int typeId, UnknownTypeAggregate agg) in byType.OrderByDescending(kv => kv.Value.Count))
        {
            string tick = agg.FirstFrame >= 0 && agg.FirstFrame < parsed.Frames.Count
                ? (parsed.Frames[agg.FirstFrame].GameTick ?? parsed.Frames[agg.FirstFrame].ServerTick)
                .ToString(CultureInfo.InvariantCulture)
                : "—";

            Output.UnknownMessages.Append(new OutputRow(agg.FirstFrame, tick, "WARN",
                $"type {typeId} ({agg.TypeName})  ×{agg.Count}  •  ~{agg.SampleSize} B  •  first @ frame {agg.FirstFrame}"));
        }
    }

    // ── Entity-state refresh notification ─────────────────────────────────────
    // 3.4a — event now owned by EntityTab. Pass-through add/remove forwards to it
    //         so any external subscribers (currently none) keep their behaviour.
    /// <summary>Fired on the UI thread whenever entity state is rebuilt (after seeking).</summary>
    public event Action? EntitiesRefreshed
    {
        add => EntityTab.EntitiesRefreshed += value;
        remove => EntityTab.EntitiesRefreshed -= value;
    }

    /// <summary>
    ///     Persists the current UI session. Called from <c>App.OnExit</c> on desktop;
    ///     no-op on WASM (the store self-guards). Safe to call any time.
    /// </summary>
    public void SaveSession() => _sessionStore.Save(SnapshotSession());

    /// <summary>Set storage provider.</summary>
    public void SetStorageProvider(IStorageProvider? provider) => _storageProvider = provider;

    // ── Value formatting ──────────────────────────────────────────────────────

    // 3.4c: promoted to `internal` so EntityTrackingTabViewModel can call it from
    // moved methods (WatchField, UpdateWatchedValues, RebuildEntityGroupsWithDelta,
    // RebuildEntityListItems, OnSelectedEntityItemChanged). Still stateless.
    internal static string FormatValue(object? v) => v switch
    {
        null => "<null>",
        Vector3 vec => $"({vec.X:F3}, {vec.Y:F3}, {vec.Z:F3})",
        Vector2 vec => $"({vec.X:F3}, {vec.Y:F3})",
        Vector4 vec => $"({vec.X:F3}, {vec.Y:F3}, {vec.Z:F3}, {vec.W:F3})",
        float f => f.ToString("F4", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "<null>"
    };

    /// <summary>
    ///     Bookmarks the currently selected frame. Labels it with the frame command + tick;
    ///     the bookmark persists on desktop (no-op on WASM). No-op when no frame is selected.
    /// </summary>
    [RelayCommand]
    private void BookmarkCurrentFrame()
    {
        if (_selectedFrameIndex < 0 || _selectedFrameIndex >= Frames.Count)
        {
            return;
        }

        DemoFrame frame = Frames[_selectedFrameIndex];
        int tick = frame.GameTick ?? frame.ServerTick;
        Bookmarks.Add(_selectedFrameIndex, tick, $"{frame.Command} · tick {tick}");
    }

    /// <summary>
    ///     CanExecute for debugger Continue / Step Tick / Step Round. Looser than
    ///     <see cref="CanGoNext" /> — doesn't require a selected frame, because a common
    ///     debugger workflow is: open demo, add ParserDecodeError breakpoint, click
    ///     Continue — which should run from frame 0 without forcing a click first.
    /// </summary>
    private bool CanDebugStep() => HasFile && !IsLoading && _selectedFrameIndex < Frames.Count - 1;

    // ── Helpers ───────────────────────────────────────────────────────────────
    //
    // Add / AddPayloadNodeSteps / ApplyDecompressedHighlights / BuildCardsForFrame
    // / BuildChainForEntity / BuildChainForFrame / BuildHarvestCard / AdaptNode
    // / BuildHarvestProperties / BuildEntityUpdateNode / GetAccentBrush /
    // GetDecompressedPayload / GetMsgBytes / GetNetMessageTypeId / GetProtoEnumName
    // / HandleCardSelected / HandlePropertySelected / InjectEntityDataNodes /
    // IsPacketFrame / BuildNormalizedBitstream / PopulateFrameHeaderFields /
    // ProtoPath / RawFrameHighlightInfo / RebuildParseChain / SetMessageHighlight
    // (both overloads) / SetPayload / SyncPayloadNodesToCard / TryFindPath /
    // WriteUInt32LE all moved to ParserTab in 3.5a.
    //
    // CollapseAllCards / ExpandAllCards / SelectDecompressedTab / SelectRawTab
    // (RelayCommands) moved with them — they're command targets on ParserTab now;
    // XAML still binds via the shell paths (PropertyChanged forwarder + the
    // RelayCommand generator auto-exposes them on ParserTab's surface).

    // SeekToGameTick / BuildCardsForTickGroup / BuildCardsAsync / BuildTickGroups
    // all moved to ReplayTabViewModel (3.5b). File-load orchestration calls
    // ReplayTab.ResetForFileLoad + ReplayTab.BuildTickGroups directly.

    private bool CanGoNext() =>
        HasFile && !IsLoading && _selectedFrameIndex >= 0 && _selectedFrameIndex < Frames.Count - 1;

    // CanGoNextTick / CanGoPreviousTick / CanGoNextGameEventTick moved to
    // ReplayTabViewModel (3.5b) — they gate the tick-level navigation commands
    // which are now owned by ReplayTab.

    private bool CanGoPrev() =>
        HasFile && !IsLoading && _selectedFrameIndex > 0;

    /// <summary>
    ///     "Continue" — advance frame-by-frame until either a breakpoint trips or the demo
    ///     ends. Capped at <see cref="MaxContinueFrames" /> frames per click so we never hang
    ///     the UI on a runaway loop. Selecting a frame fires the normal display pipeline so
    ///     the user sees the same UI state as if they'd manually stepped there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDebugStep))]
    private void ContinueToBreakpoint()
    {
        // Clear any prior "stopped at" state so this run can record a fresh hit.
        Debugger.Continue();

        int startIdx = _selectedFrameIndex + 1;
        int endIdx = Math.Min(Frames.Count, startIdx + MaxContinueFrames);

        for (int i = startIdx; i < endIdx; i++)
        {
            DemoFrame frame = Frames[i];
            Breakpoint? hit = Debugger.CheckFrame(frame);
            if (hit is not null)
            {
                // Tier 1 hit found at this frame. Suppress further Tier 3 fires during the
                // back-navigation seek so they don't overwrite the panel's "stopped at" text.
                // The seek's finally block auto-clears Suppress.
                Debugger.Suppress = true;
                SelectedFrame = frame;
                return;
            }
        }

        // No Tier 1 hit. Land at the end-of-scan frame; if a Tier 3 hit fired DURING the
        // seek triggered by that landing, the panel will surface it with a Jump button.
        if (endIdx - 1 >= 0 && endIdx - 1 < Frames.Count)
        {
            SelectedFrame = Frames[endIdx - 1];
        }
    }

    /// <summary>
    ///     Construct an <see cref="EntityTracker" /> with the debugger subscribed so Tier 3
    ///     breakpoints (packet#, decode error, delta-on-unknown) can fire during seek.
    ///     The subscription auto-clears with the tracker — there's a new tracker per seek call.
    /// </summary>
    private EntityTracker CreateTracker()
    {
        EntityTracker tracker = new();
        tracker.PacketProcessed += (packetCount, hasNewDecodeError, deltaUnknownDelta) =>
        {
            // Pass the tracker's currently-iterating frame index so the debugger can later
            // "Jump to" the frame containing the bad packet. We capture it here rather than
            // having the debugger reach into EntityTracker, so the parser stays UI-agnostic.
            Debugger.CheckParserState(packetCount, hasNewDecodeError, deltaUnknownDelta, tracker.CurrentFrameIndex);
        };

        // Surface per-packet decode failures in the Output panel's "Decode errors"
        // channel. Fires from the mutating Replay path on a background thread → marshal to UI.
        tracker.DecodeErrorRaised += err => Dispatcher.UIThread.Post(() =>
            Output.DecodeErrors.Append(new OutputRow(
                err.FrameIndex,
                err.FrameIndex.ToString(CultureInfo.InvariantCulture),
                "ERR",
                $"{err.ClassName} ent#{err.EntityIndex}: {err.Message}")));

        return tracker;
    }

    // CollapseAllCards / ExpandAllCards / SelectDecompressedTab / SelectRawTab moved to
    // ParserTab in 3.5a — their RelayCommand shims are exposed above.

    // FilterAndSetEntityFieldNodes moved to EntityTab in 3.4b. The single remaining
    // in-class caller (OnSelectedEntityItemChanged) reaches in via EntityTab until
    // it migrates too in 3.4c.

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // FrameContainsGameEvent / FrameContainsRoundEvent retired in navigation-review Phase A — the
    // per-frame scans they backed are now precomputed once in SemanticNavigator.Build (the *Frame*
    // methods delegate to the navigator). FrameHasRoundTransition stays — it serves StepRoundToBreakpoint.

    private static bool FrameHasRoundTransition(DemoFrame frame)
    {
        foreach (NetMessage msg in frame.InnerMessages)
        {
            if (msg is GameEventMessage { DecodedEvent.Name: "round_start" or "round_end" })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Shell-side fallout of ParserTab raising a frame-selection change. Owns the
    ///     shell-only concerns (selected-frame index, seek controls, entity seek
    ///     kickoff, debugger command CanExecute refresh, Analysis seek).
    /// </summary>
    private void HandleFrameSelectedFromParserTab(int idx)
    {
        // DISCRETE fan-out = the light-sync work + the heavy ASYNC checkpoint-replay entity seek.
        // The controller wires this to ApplySeek (used by SeekToFrame / scrub / palette / StepBack).
        ApplyLightSeekFanOut(idx);

        if (idx >= 0)
        {
            _ = EntityTab.SeekEntitiesAsync(idx);
        }
    }

    /// <summary>
    ///     The LIGHT-SYNC half of the position fan-out: selected-frame sync, seek controls, command
    ///     CanExecute refresh, and the Analysis seek. Deliberately excludes the heavy async entity
    ///     seek so the incremental <c>StepForward</c> / play loop (which steps the authoritative
    ///     tracker synchronously and rebuilds EntityTab in place) can reuse it without triggering an
    ///     O(N) async replay. Wired to the controller's <c>ApplyLightSeek</c>.
    /// </summary>
    private void ApplyLightSeekFanOut(int idx)
    {
        _selectedFrameIndex = idx;

        // Keep the Parser tab's master selection in sync for moves that did NOT originate from its
        // own setter (e.g. command-palette / Output-panel navigation, or the incremental step, now
        // route through the controller, not via SelectedFrame=). When this IS the originating setter,
        // the value is already Frames[idx] so the assignment is a no-op; if it weren't, the
        // controller's re-entrancy guard absorbs the resulting OnFrameSelected callback.
        if (idx >= 0 && idx < Frames.Count && !ReferenceEquals(SelectedFrame, Frames[idx]))
        {
            SelectedFrame = Frames[idx];
        }

        NextFrameCommand.NotifyCanExecuteChanged();
        PreviousFrameCommand.NotifyCanExecuteChanged();
        // Debugger commands share CanGoNext as their CanExecute guard. CommunityToolkit
        // doesn't auto-observe the underlying state, so without these the toolbar
        // ▶▶ / ▶| / ▶|| buttons stay disabled even after a file is loaded.
        ContinueToBreakpointCommand.NotifyCanExecuteChanged();
        StepTickToBreakpointCommand.NotifyCanExecuteChanged();
        StepRoundToBreakpointCommand.NotifyCanExecuteChanged();

        if (idx >= 0 && !AnalysisTab.IsFrameSeekSuppressed)
        {
            Analysis.SeekToFirstMessageOfFrame(idx);
        }
    }

    /// <summary>
    ///     Navigate the frame-list selection to <see cref="Debugging.DebuggerService.LastHitFrameIndex" />,
    ///     suppressing breakpoints during the back-navigation seek so the same hit doesn't fire again
    ///     immediately. After the suppressed seek completes, breakpoints are re-armed.
    /// </summary>
    [RelayCommand]
    private void JumpToHitFrame()
    {
        int targetFrame = Debugger.LastHitFrameIndex;
        if (targetFrame < 0 || targetFrame >= Frames.Count)
        {
            return;
        }

        if (_selectedFrameIndex == targetFrame)
        {
            return; // already there
        }

        Debugger.Suppress = true;
        try
        {
            SelectedFrame = Frames[targetFrame];
            // The seek triggered by SelectedFrame= runs on a Task.Run so it's async; we can't
            // tightly bracket the await here. The PacketProcessed handler reads Debugger.Suppress
            // at each fire, so as long as the user doesn't click anything else during the seek,
            // no breakpoint will retrigger. Re-arm in OnSelectedFrameChanged after the seek
            // completes — see the bottom of that handler.
            // (Alternative: drive the seek with a cancellation-aware await here. Out of scope.)
        }
        catch
        {
            Debugger.Suppress = false;
            throw;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextFrame()
    {
        if (_selectedFrameIndex + 1 < Frames.Count)
        {
            SelectedFrame = Frames[_selectedFrameIndex + 1];
        }
    }

    // ── Semantic-nav delegating wrappers (navigation-review Phase A) ──────────
    // The six *Frame* methods below delegate to the shell-owned SemanticNavigator, which
    // binary-searches the precomputed boundary indices from PlaybackController.CurrentFrameIndex and
    // drives PlaybackController.SeekToFrame. SeekControls still calls these wrappers (behavior-identical
    // to the legacy per-press scans); the strip (Phase C) calls the navigator directly.

    private void NextFrameByRound() => Navigator.NextRound();

    // ── Frame-list seek helpers (used by SeekControls on Parser Details tab) ──

    private void NextFrameByTick() => Navigator.NextTick();

    // ── Special seek — parser tab (frame-level, reads SeekControls filters) ──────

    private void NextSpecialFrame() => Navigator.NextEvent(SelectedSpecialFilter());

    /// <summary>
    ///     Re-evaluate the debugger commands' enabled state. The CommunityToolkit MVVM
    ///     generator doesn't auto-observe HasFile/IsLoading on our behalf, so we trigger
    ///     these manually from the OnXxxChanged partial methods + the frame-selection path.
    /// </summary>
    private void NotifyDebuggerCommandsCanExecute()
    {
        ContinueToBreakpointCommand.NotifyCanExecuteChanged();
        StepTickToBreakpointCommand.NotifyCanExecuteChanged();
        StepRoundToBreakpointCommand.NotifyCanExecuteChanged();
    }

    // Re-evaluate the nav-strip semantic commands' CanExecute (they gate on HasFile/IsLoading, which
    // CommunityToolkit doesn't auto-observe). Called from the same OnXxxChanged hooks as the debugger.
    private void NotifyNavCommandsCanExecute()
    {
        NavNextEventCommand.NotifyCanExecuteChanged();
        NavPrevEventCommand.NotifyCanExecuteChanged();
        NavNextRoundCommand.NotifyCanExecuteChanged();
        NavPrevRoundCommand.NotifyCanExecuteChanged();
        NavNextTickCommand.NotifyCanExecuteChanged();
        NavPrevTickCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasFileChanged(bool value)
    {
        NotifyDebuggerCommandsCanExecute();
        NotifyNavCommandsCanExecute();

        // One-shot session restore once a demo is loaded. Re-select the persisted frame
        // (which rebuilds the parser payload tree) then push the within-frame Parser + Entity
        // selection. Best-effort: out-of-range / missing targets are silently skipped.
        if (value && _pendingRestore is { } restore)
        {
            _pendingRestore = null;

            int? frameIdx = restore.Parser?.SelectedFrameIndex;
            if (frameIdx is { } idx and >= 0 && idx < Frames.Count)
            {
                SelectedFrame = Frames[idx];
            }

            if (restore.Parser is { } parser)
            {
                ParserTab.RestoreState(parser);
            }

            if (restore.Entity is { } entity)
            {
                EntityTab.RestoreState(entity);
            }

            if (restore.Analysis is { } analysis)
            {
                AnalysisTab.RestoreState(analysis);
            }

            // Module tabs: parked on their descriptors, applied when each VM is first built (they are lazy).
            RestoreModuleTabs(restore.ModuleTabs);
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        NotifyDebuggerCommandsCanExecute();
        NotifyNavCommandsCanExecute();
    }

    // Categorise moved to EntityTab in 3.4c (helper for RebuildEntityGroups).

    /// <summary>Opens the command palette (Ctrl+P). Delegates to the palette VM's open action.</summary>
    [RelayCommand]
    private void OpenCommandPalette() => Palette.OpenCommand.Execute(null);

    // NextTick / PreviousTick / NextGameEventTick / ToggleTickView commands +
    // OnIsTickViewChanged / OnSelectedTickFrameRowChanged / OnSelectedTickFrameChanged
    // / OnSelectedTickGroupChanged partials all moved to ReplayTabViewModel in 3.5b.
    // Shell-side fallouts (entity-tracking seeks, parser-tab card pushes) flow
    // back via the ReplayTab.OnTickGroupSelected / OnTickFrameSelected /
    // ParserCard* callbacks wired in the constructor.

    // ── Entity selection & field watching ────────────────────────────────────
    // OnSelectedEntityItemChanged and OnSelectedEntityListItemChanged moved to
    // EntityTab in 3.4c. The parse-chain refresh that lived in the body of
    // OnSelectedEntityItemChanged is now driven via the
    // EntityTab.OnEntitySelectionChanged callback wired in this ctor.

    // ── Frame selection ───────────────────────────────────────────────────────
    //
    // OnSelectedFrameChanged, OnSelectedFrameRowChanged, OnSelectedMessageChanged,
    // OnSelectedPayloadNodeChanged all moved to ParserTab in 3.5a. Shell-side
    // fallouts (selected-frame index, command CanExecute refresh, Analysis seek,
    // entity-seek kickoff, FrameGameEvents population) flow back via the
    // ParserTab.OnFrameSelected + PopulateFrameGameEvents callbacks wired in the
    // constructor. See HandleFrameSelectedFromParserTab + PopulateFrameGameEventsFromFrame.

    // OnShowDeltaFieldsOnlyChanged moved to EntityTab in 3.4b (paired with
    // FilterAndSetEntityFieldNodes which it called).
    // OnShowDormantEntitiesChanged + RefreshEntityView moved to EntityTab in 3.4c.

    // ── File loading ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (_storageProvider is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open CS2 Demo",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CS2 Demo")
                {
                    Patterns = ["*.dem"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        if (files is not { Count: > 0 })
        {
            return;
        }

        IStorageFile file = files[0];
        string? pickedLocalPath = file.TryGetLocalPath();
        byte[] pickedBytes;
        if (pickedLocalPath is not null)
        {
            pickedBytes = await File.ReadAllBytesAsync(pickedLocalPath);
        }
        else
        {
            await using Stream rawStream = await file.OpenReadAsync();
            MemoryStream ms = new();
            await rawStream.CopyToAsync(ms);
            pickedBytes = ms.ToArray();
        }

        await LoadDemoFromBytesAsync(pickedBytes, pickedLocalPath, file.Name);
    }

    /// <summary>
    ///     Loads a demo directly from a filesystem path — the demo-library browser's open path. Reads the
    ///     bytes and routes through the shared <see cref="LoadDemoFromBytesAsync" /> core (the same load the
    ///     Open-file picker uses). Callers have real local paths (desktop).
    /// </summary>
    public async Task LoadDemoFromPathAsync(string path)
    {
        if (!File.Exists(path))
        {
            StatusText = $"File not found: {path}";
            return;
        }

        byte[] rawBytes;
        try
        {
            rawBytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex)
        {
            StatusText = $"Error reading {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        await LoadDemoFromBytesAsync(rawBytes, path, Path.GetFileName(path));
    }

    /// <summary>
    ///     Opens a demo in the workspace through the shared load core. The Highlights tab's
    ///     "Open in workspace" delegate. The funnel lands the tab
    ///     (Match Overview — the demo-opening surface every open path shares); the old pre-switch to
    ///     Parser only produced a one-frame Parser flash before the funnel took over.
    /// </summary>
    public async Task OpenDemoInWorkspaceAsync(string path) => await LoadDemoFromPathAsync(path);

    /// <summary>
    ///     Drops every shell-held reference to the currently-loaded demo. Shared by the two load entry
    ///     points (<see cref="LoadDemoFromBytesAsync" /> / <see cref="AutoLoadDemoAsync" />, which used to
    ///     carry near-identical hand-maintained copies of this block) and by the standalone
    ///     <see cref="CloseDemoCommand" />.
    ///     <para>
    ///         Completeness matters far more here than it reads: the parser slices frames ZERO-COPY into the
    ///         demo byte buffer (<see cref="DemoFrame" />.RawStart/RawLength over the raw
    ///         <see cref="ReadOnlyMemory{T}" />), so a single surviving frame reference anywhere pins the
    ///         ENTIRE file — a multi-gigabyte retention from one missed field. <c>MemoryReleaseTests</c>
    ///         guards this with a weak-reference assertion; if you add a demo-scale cache, clear it here.
    ///     </para>
    /// </summary>
    private void UnloadDemoState()
    {
        _replayDemoContext = null;
        _demoBytes = null;
        // Drop our handle to the previous open's fan-out (it may still be running on a reload — that is
        // fine and pre-existing; it holds only the OLD demo, which is being replaced anyway). CloseDemoAsync
        // captures the handle before calling this, so the explicit-close await is unaffected.
        _openFanOutTask = null;
        _teamNamesTask = null;
        _allFrames = null;
        _loadedDemoPath = null;
        _players = null;
        _playersByUserId = null;
        _nameByUserId = new Dictionary<int, string>();
        PlayerNames = new Dictionary<int, string>();

        // _cachedDecompressedPayload + _msgHlInfo + _msgDecompressedRanges live on ParserTab (3.5a).
        // The SelectedFrame = null below triggers ParserTab.OnSelectedFrameChanged which clears them as
        // part of its frame-cleared branch; ResetForDemoUnload then drops the two demo-scale byte caches.
        Analysis.Reset();

        HasFile = false;
        HasEntities = false;
        EntityStatusText = "";
        Output.ClearAll();
        _unknownAccumulator.Clear();
        Playback.Unload();
        Navigator.Reset();
        _moduleContext?.SetRoster([]);
        _moduleContext?.SetGameEvents([]);
        _moduleContext?.SetGameClock(0);
        _moduleContext?.SetMapName(null);
        _moduleContext?.SetDemo(null);
        // ClearAndTrim, not Clear: these grow to one slot per frame (~131k on a long demo) and
        // Clear() does not shrink the backing array, so a closed demo left 1 MB of nulls in each.
        Frames.ClearAndTrim();
        FrameRows.ClearAndTrim();
        // Demo-derived event filters are rebuilt from the new demo's event names by the load path's append
        // loop. Clearing here means a reload fully restores to the first-load point — otherwise the previous
        // demo's event names (and the user's per-event toggle state) leak into the new demo's filter set.
        // (The auto-load path's hand-copied block was missing this clear; folding the two together fixes it.)
        GameEventFilters.Clear();
        SelectedFrame = null;
        SelectedMessage = null;
        SelectedEntityItem = null;
        SelectedEntityListItem = null;
        SelectedPayloadNode = null;
        HexViewRaw.Clear();
        HexViewDecompressed.Clear();
        IsDecompressedTabAvailable = false;
        ShowRawHex = true;

        EntityTab.ResetForDemoUnload();
        ParserTab.ResetForDemoUnload();
        ReplayTab.ResetForDemoUnload();
        StatsTab.ResetForDemoUnload();
        MatchOverviewTab.Clear(); // reset the landing page to its empty state (a following open re-populates it)
    }

    /// <summary>
    ///     Closes the open demo and hands its memory back. Drops every shell/tab reference via
    ///     <see cref="UnloadDemoState" />, notifies modules, then forces an aggressive compacting
    ///     collection so the freed demo buffer and frame graph are actually decommitted to the OS.
    ///     <para>
    ///         The explicit <see cref="GC.Collect(int, GCCollectionMode, bool, bool)" /> is normally a smell;
    ///         it is deliberate here because returning RAM IS the user-visible point of the action. Under
    ///         Server GC an ordinary collection leaves the freed segments committed, so
    ///         <see cref="GCCollectionMode.Aggressive" /> plus a one-shot LOH compaction is what makes RSS
    ///         drop — the demo buffer is a single multi-hundred-megabyte LOH array.
    ///     </para>
    ///     <para>
    ///         Best-effort by design: background work started by the open (the library tier-2 fan-out at the
    ///         end of the load path, highlight scans) can still hold the <c>ParsedDemo</c> until it finishes,
    ///         so RAM may come back a few seconds late if a close immediately follows an open.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasFile))]
    private async Task CloseDemoAsync()
    {
        // Capture BEFORE UnloadDemoState nulls it. A running library tier-2 fan-out roots the ParsedDemo,
        // so the reclaim collection below would free nothing while it is in flight. Awaiting it first makes
        // the close deterministic: the whole frame graph is unrooted when the GC runs. For an already-indexed
        // demo the fan-out is a near-instant skip; only a close that races a fresh demo's first indexing waits
        // (rare), and the status line reflects it. Failures are swallowed — the fan-out already isolates them.
        Task? fanOut = _openFanOutTask;
        Task? teamNames = _teamNamesTask;

        UnloadDemoState();
        _moduleContext?.RaiseDemoReset();

        if (fanOut is { IsCompleted: false })
        {
            StatusText = "Closing demo…";
            try
            {
                await fanOut;
            }
            catch
            {
                // The fan-out isolates its own evaluator failures; nothing to surface on close.
            }
        }

        // The team-name lookup awaits the same fan-out; drain it so nothing is left running past the close.
        if (teamNames is { IsCompleted: false })
        {
            try
            {
                await teamNames;
            }
            catch
            {
                // ResolveTeamNamesAsync already swallows its own failures; belt and braces.
            }
        }

        StatusText = "No demo loaded.";
        AppLog.DemoClosed(DiagLog);

        // Off the UI thread — a blocking gen-2 compacting collection over a demo-sized heap is long
        // enough to be felt as a hitch.
        await Task.Run(static () =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        });
    }

    // ── Idle mode ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Begins idle-mode monitoring (global input hook + poll timer). Called by the DESKTOP composition
    ///     root only — WASM never starts it, so idle mode is desktop-only. Inert if no controller was built
    ///     (no settings monitor).
    /// </summary>
    public void StartIdleMonitoring() => _idle?.Start();

    /// <summary>
    ///     Records a user interaction, resetting the idle countdown. Called by the view's global tunneling
    ///     input handlers (pointer / key / wheel). One field write; inert if idle mode isn't running.
    /// </summary>
    public void NotifyIdleActivity() => _idle?.NotifyActivity();

    // ── First-run Visual Walkthrough ──────────────────────────────────────────

    /// <summary>
    ///     Starts the first-run walkthrough from the top. Called by the desktop composition root after setup
    ///     completes (when the user opted in) and by the Settings "Replay walkthrough" affordance.
    /// </summary>
    public void StartWalkthrough() => _tutorial.Start();

    // Switches the workspace to the tab with the given TabId (null / absent = no-op). Wired to the tutorial
    // controller so a step can bring its target region on screen; a gated-off tab simply isn't found, and the
    // step degrades to a callout with no spotlight (anchor-missing → graceful).
    private void SelectTabById(string? tabId)
    {
        if (tabId is { Length: > 0 } && Tabs.FirstOrDefault(t => t.TabId == tabId) is { } tab)
        {
            SelectedTab = tab;
        }
    }

    /// <summary>
    ///     Ctrl+1..9 accelerator (v0.6.0): selects the Nth tab of the CURRENTLY VISIBLE, gate-filtered
    ///     strip — live positional, evaluated at press time. This does not violate the name-based
    ///     tab-identity rule above: nothing positional is ever persisted, and "the third tab I can see
    ///     right now" is exactly the contract a numeric accelerator promises. Out-of-range = no-op.
    /// </summary>
    /// <param name="ordinal">1-based tab position, as bound from the KeyBinding's CommandParameter.</param>
    [RelayCommand]
    private void SelectTabByOrdinal(string? ordinal)
    {
        if (int.TryParse(ordinal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n >= 1 && n <= Tabs.Count)
        {
            SelectedTab = Tabs[n - 1];
        }
    }

    // What must NOT be interrupted by idle. Playback is the primary signal (paused / ended both read false),
    // but a running reel render or an ACTIVE live-sync session are long-running operations with no per-frame
    // input either — closing the demo under them would break work in flight, so they block idle too. Evaluated
    // fresh each poll tick (ReelJob / LiveSync are attached after the ctor by the desktop host).
    private bool IsIdleBlocked() =>
        Playback.IsPlaying
        || _tutorial.IsActive
        || ReelJob is { Status.IsRunning: true }
        || LiveSync is { State.IsSessionActive: true };

    /// <summary>
    ///     Enters idle mode: captures where to resume, shows the idle overlay, optionally parks background
    ///     processing, and releases the open demo's memory via the same deterministic-close path the
    ///     "Close Demo" button uses. Fired once by the idle controller on the UI thread.
    /// </summary>
    internal async Task EnterIdleModeAsync()
    {
        // Runs as a fire-and-forget continuation off the idle timer; guard the whole body so an unexpected
        // throw can't become an unobserved task exception (CloseDemoAsync isolates its own fan-out failures).
        try
        {
            // Capture the resume point BEFORE closing — the demo graph is about to be torn down, so this is the
            // one clean moment to record where playback sat. ResumeFrameIndex is a playback frame index (the
            // clock's own unit), NOT a CS2 demo tick.
            TimeSpan wait = _settings?.CurrentValue.Idle.IdleTimeoutWait ?? TimeSpan.FromMinutes(15);
            IdleView.MessageText = IdleViewModel.BuildMessage(wait);

            if (HasFile && _loadedDemoPath is { Length: > 0 } path && File.Exists(path))
            {
                _idleResume = new IdleResumeState(path, Playback.CurrentFrameIndex, SelectedTab?.TabId);
                IdleView.SessionStateText = Playback.CurrentFrameIndex >= 0
                    ? $"Closed {Path.GetFileName(path)} — resumes at frame {Playback.CurrentFrameIndex}."
                    : $"Closed {Path.GetFileName(path)} — resumes at the start.";
            }
            else
            {
                _idleResume = null;
                IdleView.SessionStateText = "No demo was open. Resume returns you to the app.";
            }

            // Optionally park background processing too (transient pause; resumed on leaving idle).
            if (_settings?.CurrentValue.Idle.KeepBackgroundProcessing == false)
            {
                _processingQueue?.Pause();
            }

            IsIdle = true;

            // Drop resource usage — the deterministic close (awaits the library fan-out, then an aggressive
            // compacting reclaim). Guarded so an idle with nothing open pays nothing.
            if (HasFile)
            {
                await CloseDemoAsync();
            }
        }
        catch (Exception ex)
        {
            // Never crash the app over idle housekeeping. The overlay is already up (IsIdle set above); the
            // user can still Resume. Surface it to the diagnostics log only.
            AppLog.DemoLoadFailed(DiagLog, "idle", ex.Message);
        }
    }

    /// <summary>
    ///     Leaves idle mode: dismisses the overlay, restarts the idle countdown, resumes any parked
    ///     background processing, and reopens the captured demo — restoring the active tab and playback
    ///     position. Wired to the idle surface's Resume button.
    /// </summary>
    private void ResumeFromIdle() => _ = ResumeFromIdleAsync();

    internal async Task ResumeFromIdleAsync()
    {
        IsIdle = false;
        _idle?.ClearIdle();

        if (_settings?.CurrentValue.Idle.KeepBackgroundProcessing == false)
        {
            _processingQueue?.Resume();
        }

        if (_idleResume is not { } resume)
        {
            return; // nothing was open — dismissing the overlay is the whole resume.
        }

        _idleResume = null;

        if (!File.Exists(resume.DemoPath))
        {
            StatusText = $"Cannot resume — file not found: {Path.GetFileName(resume.DemoPath)}";
            return;
        }

        // Full re-parse: releasing the parsed graph was the point of going idle, so returning re-loads it.
        await LoadDemoFromPathAsync(resume.DemoPath);

        // Restore the active tab + playback position now that the reopened demo is fully loaded.
        if (resume.ActiveTabId is { Length: > 0 } tabId
            && Tabs.FirstOrDefault(t => t.TabId == tabId) is { } match)
        {
            SelectedTab = match;
        }

        if (resume.ResumeFrameIndex >= 0 && resume.ResumeFrameIndex < Playback.TotalFrames)
        {
            Playback.SeekToFrame(resume.ResumeFrameIndex);
        }
    }

    /// <summary>
    ///     Fills the Match Overview's TEAM NAMES (clan tags) for the score plate. Pro demos carry them;
    ///     matchmaking demos do not, and the plate then labels each team by the side it finished on.
    ///     <para>
    ///         Names only — the SCORE comes from the analysis engine's per-team round wins, which counts the
    ///         rounds each team actually won and therefore survives a demo cut at the buzzer. The clan names
    ///         ride along on the library entry that this open's own fan-out already populates, so this costs
    ///         nothing extra: no second parse, no entity replay, and no ParsedDemo held past the close.
    ///     </para>
    /// </summary>
    private async Task ResolveTeamNamesAsync(string? localPath)
    {
        try
        {
            if (localPath is not { Length: > 0 })
            {
                return;
            }

            if (TryPushTeamNames(localPath))
            {
                return; // already indexed
            }

            if (_openFanOutTask is { } fanOut)
            {
                await fanOut.ConfigureAwait(true);
                TryPushTeamNames(localPath);
            }
        }
        catch (Exception ex)
        {
            // Cosmetic: without names the plate falls back to "ENDED CT" / "ENDED T".
            AppLog.DemoLoadFailed(DiagLog, "team-names", ex.Message);
        }
    }

    // Is this path the bundled tour sample? Ordinal-ignore-case: macOS/Windows default filesystems are
    // case-insensitive, and both paths come from the same locator/funnel plumbing anyway.
    private bool IsTourSample(string? path) =>
        _tourSamplePath is not null
        && path is not null
        && string.Equals(path, _tourSamplePath, StringComparison.OrdinalIgnoreCase);

    private bool TryPushTeamNames(string localPath)
    {
        DemoEntry? entry = _library.Entries
            .FirstOrDefault(e => string.Equals(e.FilePath, localPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null || (string.IsNullOrWhiteSpace(entry.CtClan) && string.IsNullOrWhiteSpace(entry.TClan)))
        {
            return false;
        }

        MatchOverviewTab.SetTeamNames(localPath, entry.CtClan, entry.TClan);
        return true;
    }

    /// <summary>
    ///     Shared demo-load core: resets all per-file state, parses <paramref name="rawBytes" /> off-thread,
    ///     and fans the result out to every tab / module and the playback clock. Both the Open-file picker
    ///     (<see cref="OpenFileAsync" />) and the library browser (<see cref="LoadDemoFromPathAsync" />) route
    ///     here so there is exactly one load path. <paramref name="localPath" /> is retained for the Diagnostics
    ///     Session card (null on browser hosts with no local path).
    /// </summary>
    private async Task LoadDemoFromBytesAsync(byte[] rawBytes, string? localPath, string fileName)
    {
        UnloadDemoState();

        IsLoading = true;
        StatusText = $"Parsing {fileName}…";
        AppLog.DemoLoadStarted(DiagLog, fileName, rawBytes.Length);

        // Match Overview landing (responsiveness): surface the demo the INSTANT the open begins — before the
        // multi-second parse — so a double-click has an immediate, visible effect instead of a silent wait. The
        // cheap header read (~first 256 KB) gives the map/server right away; the full summary lands post-parse.
        string? quickMap = null;
        string? quickServer = null;
        int headSpan = Math.Min(rawBytes.Length, 262144);
        if (headSpan > 0 && DownstreamUtilities.TryReadQuickInfo(rawBytes.AsSpan(0, headSpan), out DownstreamUtilities.DemoQuickInfo qi))
        {
            quickMap = string.IsNullOrEmpty(qi.MapName) ? null : DemoEntry.PrettifyMap(qi.MapName);
            quickServer = qi.ServerName;
        }

        // Match Overview's subject identity for this open. The browser host has no local path, so the file
        // name stands in — the key only has to be stable and comparable, and every keyed fill below derives
        // it the same way. Late continuations from a PREVIOUS open present that open's key and are dropped.
        string subjectKey = localPath ?? fileName;

        MatchOverviewTab.BeginOpening(fileName, quickMap, quickServer, subjectKey);
        // Everything this demo is already known to be, painted before the parse starts. A demo that has been
        // indexed (most of the library) shows its rosters, score and highlights immediately and keeps them on
        // screen while the pipeline re-derives them, instead of watching a skeleton rebuild facts that were
        // sitting in a sidecar file the whole time.
        SeedMatchOverviewFromCache(localPath, subjectKey);
        MatchOverviewTab.IsSampleClip = IsTourSample(localPath);
        // Switch to the landing page for normal opens — BUT NOT while the first-run tour is active: the tour
        // owns navigation then (its gateway spotlights the Library card), and switching away would unload the
        // Library and strand the coach-mark's spotlight over the wrong content until the tour advances. The VM
        // is still populated above/below, so Match Overview is correct if the user visits it after the tour.
        if (!_tutorial.IsActive)
        {
            SelectTabById("builtin.matchoverview");
        }

        List<DemoFrame> allFrames = new();

        try
        {
            // _demoBytes held for on-demand decompression and the hex view; the bytes were read by the caller
            // (the picker copies from the storage file, the library reads the local path).
            _demoBytes = rawBytes;
            // Retain the full path for the Diagnostics Session card; null on browser hosts.
            _loadedDemoPath = localPath ?? fileName;

            // Parse on a background thread — zero-copy: the parser slices directly into rawBytes for
            // uncompressed frames via ReadOnlyMemory<byte>. The open is the HIGHEST-priority, awaitable
            // FOREGROUND request on the global demo-processing queue: it
            // preempts background indexing/scanning (which yields at its next demo boundary), best-effort
            // coalesces onto an in-flight parse of the same demo, and during a reel render throws
            // ReelInProgressException — surfaced by this site's existing failure handling. The queue
            // parses the in-hand rawBytes (no re-read). Legacy fallbacks: the direct gate, then ungated.
            MatchOverviewTab.SetStage(subjectKey, "Parsing demo…", 0.15);
            ParsedDemo parsed;
            if (_processingQueue is not null)
            {
                parsed = await _processingQueue.RequestForegroundAsync(_loadedDemoPath, rawBytes);
            }
            else
            {
                using (_heavyJobGate is null ? null : await _heavyJobGate.AcquireInteractiveAsync())
                {
                    parsed = await Task.Run(() => DemoParser.Parse(rawBytes.AsMemory()));
                }
            }

            // Fill the Match Overview quick facts + rosters from the parsed result and advance its stage strip
            // to "Enriching". The page does NOT leave its loading state here — the score and scoreboard are
            // still placeholders until the analysis run below lands.
            MatchOverviewTab.SetSummary(subjectKey, parsed);
            MatchOverviewTab.SetParseWarnings(subjectKey, parsed.Warnings); // S11 damaged-demo banner

            FrameRows.Clear();
            int frameNum = 0;
            foreach (DemoFrame frame in parsed.Frames)
            {
                allFrames.Add(frame);
                Frames.Add(frame);
                frameNum++;
                FrameRows.Add(new HarvestFrameRowViewModel
                {
                    FrameNumber = frameNum,
                    FrameType = frame.Command,
                    MessageCount = frame.InnerMessages.Count,
                    ByteSize = frame.RawLength,
                    Source = frame
                });
            }

            HasFile = Frames.Count > 0;
            _allFrames = allFrames;
            // Record this open in the recent-files store (most-recent-first, capped,
            // de-duped by path). This is the ONE user-facing open funnel: the toolbar Open Demo, the Parser
            // empty-state, the Library "Open Demo…" CTA, and the library card double-click all route here
            // (via OpenFileAsync / LoadDemoFromPathAsync), so every open records exactly once. Guarded on a
            // real local path (browser hosts have none) and on a demo that actually parsed to frames.
            if (localPath is not null && HasFile)
            {
                _recentFiles?.RecordOpen(localPath, parsed.MapName);
            }

            // Register the demo with the controller (frame list + tick rate for the play loop).
            Playback.LoadDemo(_allFrames, parsed.TickRate);
            // Hand the module context the stable identity roster (slot / steamID / name; no
            // team — team is per-tick via the host player-join).
            _moduleContext?.SetRoster(parsed.Players.Values.Select(p => new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));
            _moduleContext?.SetGameEvents(parsed.AllGameEvents); // pre-decoded timeline for event-driven modules
            _moduleContext?.SetMapName(parsed.MapName); // data-driven map identity for asset selection
            _moduleContext?.SetDemo(parsed); // M5: expose the loaded demo to the first-party Workbench
            BuildUnknownMessageCensus(parsed);
            // navigation-review Phase A — precompute round / event / tick boundary indices once,
            // drained alongside the unknown-message census. The six *Frame* nav methods + the Phase C
            // strip binary-search these instead of re-scanning the frame list on every press.
            Navigator.Build(_allFrames);
            // #4/#5 — calibrate the shared game-clock once (first round_freeze_end) for the 2D round
            // timer + bomb/defuse timers; consumed via IModuleContext.CurtimeSeconds.
            ApplyGameClock(_allFrames, parsed.TickRate);
            // The context now holds the NEW demo's roster / events / map / clock. Signal any ACTIVE module to
            // fully resync — LoadDemo above reset the clock WITHOUT an Advanced push, so a tab left open across
            // a reload (Open-file button OR the library browser) would otherwise keep the previous demo's map
            // image, marker labels, and trails. Inactive tabs resync on their next OnActivated. This is the
            // load-path parity the two entry points must share (full state restoration on every new demo).
            _moduleContext?.RaiseDemoReset();
            _players = parsed.Players;
            _playersByUserId = parsed.Players.Values
                .Where(p => p.UserId > 0)
                .ToDictionary(p => p.UserId);

            // Primary: string-table names (parsed.Players). Secondary: PlayerConnectEvents.
            // Shared logic with the snapshot builder to keep ordering invariants identical.
            (PlayerNames, _nameByUserId) = PlayerSnapshotBuilder.BuildNameLookups(parsed);

            _replayDemoContext = DemoAnalyzer.BuildEventContext(parsed);

            // ── Analysis engine ───────────────────────────────────────────────────
            // Everything above this line is the Match Overview's "Enriching" stage (roster, navigation index,
            // game clock, module fan-out); the run below is its "Analysing" stage.
            MatchOverviewTab.BeginAnalysis(subjectKey);
            // SHA-256 the demo bytes (off-thread) to key its persisted graph breakpoints.
            string demoKey = await Task.Run(() => GraphBreakpointStore.ComputeDemoKey(rawBytes));
            await Analysis.RunAsync(parsed, demoKey);
            // StatsTab is fed by AnalysisViewModel.EvaluationCompleted, which is raised SYNCHRONOUSLY inside
            // RunAsync (AnalysisViewModel.cs) — so its tables are already built by the time this await
            // returns. Reading them here rather than subscribing keeps the Match Overview's score and
            // scoreboard byte-identical to the Stats tab's with no handler-order dependency.
            MatchOverviewTab.SetAnalysis(subjectKey, StatsTab.GameTable, StatsTab.TeamScoresBySort, StatsTab.Rounds.Count);
            // Per-team round wins from the same evaluation — each team's total across BOTH halves.
            MatchOverviewTab.SetTeamScores(
                subjectKey,
                StatsTab.TeamScoresBySort.GetValueOrDefault(0),
                StatsTab.TeamScoresBySort.GetValueOrDefault(1));
            // The tier-3 SCOREBOARD producer, and the cheapest one there is: this run already computed the
            // table, and it ran in snapshot mode, which is the only mode that can produce per-player stats at
            // all (the background highlights sweep is deliberately snapshot-free). Storing it here is what
            // makes a demo you have opened once render as FULL forever after, with no second pass.
            WriteTier3ScoreboardToCache(localPath, StatsTab.GameTable, StatsTab.Rounds.Count);
            _teamNamesTask = ResolveTeamNamesAsync(localPath);

            // Append any event names seen in this demo that aren't already in the filter list.
            HashSet<string> existingFilters = GameEventFilters
                .Select(f => f.EventName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string name in parsed.AllGameEvents.Select(e => e.Name).Distinct())
            {
                if (existingFilters.Add(name))
                {
                    GameEventFilters.Add(new GameEventFilterItem(name));
                }
            }

            // Now safe to load the full demo into the raw hex view — parsing is done and the UI
            // thread is no longer needed for the channel loop.
            HexViewRaw.Load(rawBytes);

            // If the user selected a frame while parsing, re-apply the frame highlight now that
            // the raw buffer is ready.
            if (SelectedFrame is { } alreadySelected)
            {
                HexViewRaw.SetSpans(
                    [new HexSpan(alreadySelected.RawStart, alreadySelected.RawLength)]);
            }

            StatusText = $"{fileName}  —  {Frames.Count} frames  •  Select a frame";
            AppLog.DemoLoaded(DiagLog, fileName, Frames.Count);

            // "One processing event": hand this just-parsed demo to
            // the background evaluators so an un-indexed library demo fills its Library card from THIS parse
            // instead of a redundant second background parse. Off the UI thread — the Library score replay is
            // multi-second — and isolated (a handler failure never fails the open). `parsed` is immutable
            // post-parse, so the concurrent read-only replay is safe. Highlights is skipped: it is fed the
            // open demo through the completed analysis run (OnOpenDemoEvaluated), a re-analysis-free channel.
            if (localPath is { } openPath && HasFile && _evaluationCoordinator is { } coordinator)
            {
                ParsedDemo openParsed = parsed;
                // Tracked (not fire-and-forget) so CloseDemoAsync can await it before reclaiming — a
                // running fan-out roots the demo, so an un-awaited close would free nothing.
                _openFanOutTask = Task.Run(() => coordinator.FanOutParsed(openPath, openParsed, _openFanOutSkip));
            }

            // Resume the walkthrough's demo segment (stats / playback) if it was deferred at first run for
            // want of an open demo. No-op unless the tour is awaiting a load, so it is safe on every open.
            _tutorial.NotifyDemoLoaded();
        }
        catch (Exception ex)
        {
            // Clean text on the user surfaces (v0.6.0 — a corrupt .dem used to surface as raw CLR
            // text like "Index was outside the bounds of the array"); the full exception goes to
            // the Diagnostics tab + file.
            string described = UserFacingError.Describe("load the demo", ex);
            StatusText = described;
            ILogger diagLog = DiagLog;
            if (diagLog.IsEnabled(LogLevel.Error))
            {
                string loadOperation = $"load the demo '{fileName}'";
                AppLog.OperationFailed(diagLog, loadOperation, ex);
            }

            MatchOverviewTab.Fail(subjectKey, described);
        }
        finally
        {
            IsLoading = false;
        }

        // Always build tick groups — needed by both the legacy Tick View and the Replay tab.
        ReplayTab.ResetForFileLoad();
        if (_allFrames is not null)
        {
            ReplayTab.BuildTickGroups();
        }
    }

    /// <summary>
    ///     Opens the OS folder picker (multi-select) for the library browser and returns the chosen local
    ///     folder paths. Empty when no storage provider is wired (designer / headless) or the user cancels.
    /// </summary>
    private async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        if (_storageProvider is null)
        {
            return [];
        }

        IReadOnlyList<IStorageFolder> folders = await _storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Add demo folder",
                AllowMultiple = true
            });

        return folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
    }

    /// <summary>
    ///     Opens the parse-chain inspector via <see cref="IWindowService" /> —
    ///     replaces the former <c>MainView.axaml.cs</c> code-behind window spawn. Desktop opens a
    ///     real window; browser no-ops. Inert when no window service was injected (XAML designer).
    /// </summary>
    [RelayCommand]
    private void OpenParseChainInspector() => _windowService?.OpenParseChainInspector(this);

    /// <summary>
    ///     Opens the Settings screen (P2a-i). Resolves a FRESH <see cref="SettingsViewModel" /> from the
    ///     composition root (a manual-new factory — see <c>App.BuildServices</c>) and hands it to the window
    ///     service: a non-modal window on desktop, an in-app overlay on WASM. Inert on the designer / test
    ///     path (no window service, or no container yet).
    /// </summary>
    [RelayCommand]
    private void OpenSettings() => OpenSettingsCore(null);

    /// <summary>
    ///     Opens Settings scrolled to the USER CATEGORY section — the status strip's "N features
    ///     hidden" note routes here (v0.6.0), so the note is an entry point to changing the gate
    ///     rather than a dead end. Note: when a Settings window is already open, the window service
    ///     re-activates it as-is (no re-scroll) — acceptable, the user is already in Settings.
    /// </summary>
    [RelayCommand]
    private void OpenSettingsAtCategory() => OpenSettingsCore("SectionUserCategory");

    private void OpenSettingsCore(string? scrollTargetSection)
    {
        if (_windowService is null)
        {
            return;
        }

        Func<SettingsViewModel>? factory = App.Services?.GetService<Func<SettingsViewModel>>();
        SettingsViewModel? vm = factory?.Invoke();
        if (vm is null)
        {
            return;
        }

        vm.ScrollTargetSection = scrollTargetSection;
        _windowService.OpenSettings(vm);
    }

    /// <summary>
    ///     Shows <paramref name="viewModel" /> as the in-app Settings overlay (the WASM host path, wired by
    ///     <c>App.axaml.cs</c>). Replaces and disposes any prior overlay VM, and clears + disposes this one
    ///     when its Close is requested.
    /// </summary>
    public void ShowSettingsOverlay(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (SettingsOverlay is { } existing)
        {
            existing.CloseRequested -= OnSettingsOverlayCloseRequested;
            existing.Dispose();
        }

        viewModel.CloseRequested += OnSettingsOverlayCloseRequested;
        SettingsOverlay = viewModel;
    }

    private void OnSettingsOverlayCloseRequested(object? sender, EventArgs e)
    {
        if (sender is SettingsViewModel vm)
        {
            vm.CloseRequested -= OnSettingsOverlayCloseRequested;
            vm.Dispose();
        }

        SettingsOverlay = null;
    }

    /// <summary>
    ///     Shows <paramref name="viewModel" /> as the in-app first-run wizard overlay (P2b — the WASM host
    ///     path, wired by <c>App.axaml.cs</c> and reached only via Settings' "Re-run first-time setup").
    ///     Replaces any prior overlay VM, and clears this one when the wizard raises Completed (Finish / Skip).
    /// </summary>
    public void ShowFirstRunOverlay(FirstRunWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (FirstRunOverlay is { } existing)
        {
            existing.Completed -= OnFirstRunOverlayCompleted;
        }

        viewModel.Completed += OnFirstRunOverlayCompleted;
        FirstRunOverlay = viewModel;
    }

    private void OnFirstRunOverlayCompleted(object? sender, EventArgs e)
    {
        if (sender is FirstRunWizardViewModel vm)
        {
            vm.Completed -= OnFirstRunOverlayCompleted;
        }

        FirstRunOverlay = null;
    }

    /// <summary>
    ///     Populates the shell-owned <see cref="FrameGameEvents" /> collection from the
    ///     inner messages of <paramref name="frame" />, or clears it when invoked with
    ///     <c>null</c>. Kept on shell because the tick-view code path
    ///     (<c>OnSelectedTickFrameChanged</c>, 3.5b) populates the same collection
    ///     from a different angle.
    /// </summary>
    private void PopulateFrameGameEventsFromFrame(DemoFrame? frame)
    {
        FrameGameEvents.Clear();
        if (frame is null)
        {
            HasFrameGameEvents = false;
            return;
        }

        Func<int, string> playerName = SlotToName;
        foreach (NetMessage msg in frame.InnerMessages)
        {
            if (msg is GameEventMessage gem)
            {
                FrameGameEvents.Add(new FrameGameEventViewModel(gem.DecodedEvent, playerName));
            }
        }

        HasFrameGameEvents = FrameGameEvents.Count > 0;
    }

    // PopulateFrameHeaderFields moved to ParserTab in 3.5a.

    [RelayCommand(CanExecute = nameof(CanGoPrev))]
    private void PreviousFrame()
    {
        if (_selectedFrameIndex > 0)
        {
            SelectedFrame = Frames[_selectedFrameIndex - 1];
        }
    }

    private void PreviousFrameByRound() => Navigator.PrevRound();

    private void PreviousFrameByTick() => Navigator.PrevTick();

    private void PreviousSpecialFrame() => Navigator.PrevEvent(SelectedSpecialFilter());

    /// <summary>
    ///     The currently-selected special-seek event names. navigation-review Phase B: the single
    ///     filter is now the demo-derived <c>GameEventFilters</c> (the hardcoded 7-event
    ///     <c>EventTypeFilters</c> list is retired as the source). Returns null when nothing is enabled
    ///     so the navigator falls back to "match any" (preserving the legacy convenience so the
    ///     event-jump buttons always work).
    /// </summary>
    private List<string>? SelectedSpecialFilter()
    {
        List<string> enabled = GameEventFilters
            .Where(f => f.IsEnabled).Select(f => f.EventName).ToList();
        return enabled.Count > 0 ? enabled : null;
    }

    // ── Nav-strip semantic commands (navigation-review Phase C) ───────────────
    // The shell nav strip binds these. They delegate to the SemanticNavigator (the same service the
    // legacy *Frame* wrappers route through), so the strip and the per-tab SeekControls drive one
    // implementation. Gated on a loaded demo; the navigator no-ops when no boundary exists.

    private bool CanSemanticNav() => HasFile && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanSemanticNav))]
    private void NavNextEvent() => Navigator.NextEvent(SelectedSpecialFilter());

    [RelayCommand(CanExecute = nameof(CanSemanticNav))]
    private void NavPrevEvent() => Navigator.PrevEvent(SelectedSpecialFilter());

    [RelayCommand(CanExecute = nameof(CanSemanticNav))]
    private void NavNextRound() => Navigator.NextRound();

    [RelayCommand(CanExecute = nameof(CanSemanticNav))]
    private void NavPrevRound() => Navigator.PrevRound();

    [RelayCommand(CanExecute = nameof(CanSemanticNav))]
    private void NavNextTick() => Navigator.NextTick();

    [RelayCommand(CanExecute = nameof(CanSemanticNav))]
    private void NavPrevTick() => Navigator.PrevTick();

    /// <summary>
    ///     Commits the nav-strip frame box (Enter / LostFocus from the control). Parses
    ///     <see cref="NavFrameText" />, clamps into [0, TotalFrames-1], and seeks via the controller;
    ///     reverts to the last valid frame on bad input. Frame-index movement, per the locked decision.
    /// </summary>
    public void CommitNavFrameText()
    {
        int max = Playback.TotalFrames > 0 ? Playback.TotalFrames - 1 : 0;
        if (int.TryParse(NavFrameText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            int clamped = Math.Clamp(parsed, 0, max);
            _navLastValidFrame = clamped;
            NavFrameText = clamped.ToString(CultureInfo.InvariantCulture);
            Playback.SeekToFrame(clamped);
        }
        else
        {
            NavFrameText = _navLastValidFrame.ToString(CultureInfo.InvariantCulture);
        }
    }

    // Mirror the controller's frame index into the nav-strip box without re-seeking. Wired to
    // Playback.PropertyChanged in the ctor; covers seeks, steps, and the play loop's per-tick updates.
    private void SyncNavFrameTextFromController()
    {
        int idx = Playback.CurrentFrameIndex;
        _navLastValidFrame = idx < 0 ? 0 : idx;
        NavFrameText = _navLastValidFrame.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Push current tracker counters to the debugger panel so the user can see packet
    ///     count / delta-on-unknown / decode error state without leaving the app.
    /// </summary>
    private void PublishTrackerStats(EntityTracker? tracker)
    {
        DebuggerPanel.UpdateTrackerStats(
            tracker?.PacketCount ?? 0,
            tracker?.DeltaUnknownCount ?? 0,
            tracker?.LastEntityError);
    }

    // Computes the shared game-clock calibration once per demo load and hands it to the module context
    // (mirrors SetRoster). Both demo-load paths call this AFTER _navigator.Build, so the precomputed
    // round_freeze_end frames are available — the first one calibrates the curtime offset that the 2D
    // round timer (#4) and bomb/defuse timers (#5) consume via IModuleContext.CurtimeSeconds. Reads
    // game-rules entity state by advancing a fresh tracker to that early frame (cheap, run-once).
    private void ApplyGameClock(IReadOnlyList<DemoFrame> frames, int tickRate)
    {
        if (_moduleContext is null)
        {
            return;
        }

        int firstFreezeEnd = -1;
        if (Navigator.EventBoundaryFramesByName.TryGetValue("round_freeze_end", out int[]? freezeEnds)
            && freezeEnds.Length > 0)
        {
            firstFreezeEnd = freezeEnds[0];
        }

        (double clockBase, bool _) = GameClock.ComputeClockBase(frames, firstFreezeEnd, tickRate);
        _moduleContext.SetGameClock(clockBase);
    }

    /// <summary>
    ///     Rebuild a small set of frame-number → bool for which rows should show the red gutter
    ///     dot, then push the flag onto each <see cref="HarvestFrameRowViewModel" />. Cheap because
    ///     <c>FrameRows</c> is the bench-list size (a few hundred items per visible window) and
    ///     breakpoints are typically &lt; 10. Called from the Breakpoints CollectionChanged handler.
    /// </summary>
    private void RefreshFrameBreakpointMarkers()
    {
        // Snapshot the set of frame numbers with FrameNumber breakpoints active.
        HashSet<int> setFrames = new();
        foreach (Breakpoint bp in Debugger.Breakpoints)
        {
            if (bp.Kind == BreakpointKind.FrameNumber)
            {
                setFrames.Add(bp.IntValue);
            }
        }

        foreach (HarvestFrameRowViewModel row in FrameRows)
        {
            bool shouldBeSet = setFrames.Contains(row.FrameNumber);
            if (row.IsBreakpointSet != shouldBeSet)
            {
                row.IsBreakpointSet = shouldBeSet;
            }
        }
    }

    /// <summary>
    ///     Resolves an enriched hint from a semantic + raw int (e.g. a PlayerUserId →
    ///     player display name). Owned by shell because the player-name lookups
    ///     (<c>_nameByUserId</c> / <c>_nameBySlot</c> / <c>_players</c>) live here
    ///     until the file-load pipeline moves in 3.5c.
    /// </summary>
    private string? ResolveEnrichmentHint(FieldSemantic sem, int rawInt) => sem switch
    {
        FieldSemantic.PlayerUserId => SlotToName(rawInt),
        // EntityHandle and EntityIndex enrichment not implemented yet.
        _ => null
    };

    /// <summary>
    ///     Applies the persisted session. Demo-independent state (active tab, debugger
    ///     / output visibility) is set immediately; per-tab selection is deferred via
    ///     <see cref="_pendingRestore" /> until a demo finishes loading.
    ///     <para>
    ///         <b>Must be called by the host AFTER the shell is fully constructed</b> — never from the
    ///         constructor. Restoring selects the persisted tab, and <c>WorkspaceTabDescriptor.Activate</c>
    ///         builds that tab's view-model and runs its <c>OnActivated</c>, either of which may resolve
    ///         the shell from the DI container. A singleton is not cached until its factory returns, so
    ///         doing this during construction builds a second shell that restores again — unbounded, and
    ///         without a <c>StackOverflowException</c> to stop it, because ServiceProvider's StackGuard
    ///         hops to a fresh thread as the stack deepens. It presents as a launch that pins a core and
    ///         never shows a window (shipped in v0.5.0 for anyone whose last active tab was Highlights).
    ///         <c>App.BuildShell</c> enforces this with a re-entrancy tripwire.
    ///     </para>
    /// </summary>
    public void RestoreSession()
    {
        SessionPayload? p = _sessionStore.Load();
        if (p is null)
        {
            return;
        }

        RestoreActiveTab(p);
        // Never restore an owned panel OPEN when its chrome is gated off for the current
        // category — otherwise a drawer/rail a developer left open would return open at startup with its
        // toggle button hidden (no way to close it). The gate reads live, so the shims are already resolved.
        IsDebuggerPanelVisible = p.DebuggerVisible && IsDebuggerChromeEnabled;
        Output.IsVisible = p.OutputVisible && IsOutputChromeEnabled;

        // Window geometry is the HOST's to apply (the VM has no Window reference) — parked here for
        // App.axaml.cs to read right after this returns, before the window shows.
        RestoredWindowBounds = p.Window;

        // Per-tab selection needs a loaded demo; keep the payload until HasFile flips.
        _pendingRestore = p;
    }

    /// <summary>
    ///     Main-window geometry for the NEXT snapshot. Written by the desktop host (which tracks the
    ///     window's last-Normal bounds — the VM deliberately has no <c>Window</c> reference) and read
    ///     by <see cref="SnapshotSession" />. Null on WASM/tests → nothing persisted.
    /// </summary>
    public WindowBoundsState? WindowBounds { get; set; }

    /// <summary>
    ///     Geometry loaded by <see cref="RestoreSession" /> for the host to apply to the MainWindow
    ///     before it shows. Null when the session file predates v0.6.0 or was never saved.
    /// </summary>
    public WindowBoundsState? RestoredWindowBounds { get; private set; }

    // Re-selects the persisted tab by its durable TabId — the ONLY key, because the tab
    // set is dynamic (feature gating, new built-ins landing mid-strip) and a position means a different tab
    // from one build to the next. A stale, gated-out, or absent id falls back to the first tab (Library);
    // for a session predating TabId persistence that is a one-time, self-healing loss of the remembered
    // tab, which beats confidently restoring the wrong one. Called after BuildWorkspaceTabs, so Tabs is
    // already populated.
    private void RestoreActiveTab(SessionPayload p)
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        SelectedTab = p.ActiveTabId is { Length: > 0 } tabId
            ? Tabs.FirstOrDefault(t => t.TabId == tabId) ?? Tabs[0]
            : Tabs[0];
    }

    /// <summary>
    ///     Switches to the Entity Tracking tab and reveals <paramref name="className" /> by setting the
    ///     class-browser filter and selecting the matching class (if present in the registry).
    /// </summary>
    private void RevealEntityClass(string className)
    {
        SelectTabById("builtin.entity");
        EntityTab.ClassBrowser.Filter = className;
        EntityTab.ClassBrowser.SelectedClass =
            EntityTab.ClassBrowser.Classes.FirstOrDefault(c => c.ClassName == className);
    }

    // ── Special seek — replay tab (tick-group-level) ──────────────────────────
    // NextSpecialTick / PreviousSpecialTick / NextRoundTick / PreviousRoundTick /
    // TickGroupContainsRoundEvent / TickGroupContainsGameEvent moved to
    // ReplayTabViewModel in 3.5b.

    // ── Navigation hooks ──────────────────────────────────────────────────────
    // The shell-side SeekToFrameIndex / SeekToServerTick were replaced in the modular-UI refactor
    // by PlaybackController.SeekToFrame / SeekToTick (the single position-move code path). Navigation
    // hooks now wire straight to the controller in the ctor.

    // GetAccentBrush / GetDecompressedPayload / GetMsgBytes (both overloads) /
    // GetNetMessageTypeId / GetProtoEnumName / HandleCardSelected / HandlePropertySelected /
    // InjectEntityDataNodes / BuildEntityUpdateNode / IsPacketFrame all moved to
    // ParserTab in 3.5a.

    /// <summary>
    ///     Resolves a game-event <c>userid</c> (or slot index) to a display name.
    ///     Priority: connect-event userid map → connect-event slot map → string-table map → "P{n}".
    /// </summary>
    private string SlotToName(int userId)
    {
        if (_nameByUserId.TryGetValue(userId, out string? n1) && n1.Length > 0)
        {
            return n1;
        }

        if (PlayerNames.TryGetValue(userId, out string? n2) && n2.Length > 0)
        {
            return n2;
        }

        if (_playersByUserId is not null && _playersByUserId.TryGetValue(userId, out PlayerInfo? byId) && byId.Name.Length > 0)
        {
            return byId.Name;
        }

        if (_players is not null && _players.TryGetValue(userId, out PlayerInfo? bySlot) && bySlot.Name.Length > 0)
        {
            return bySlot.Name;
        }

        return $"P{userId}";
    }

    /// <summary>
    ///     Snapshots the current UI session for persistence. Per-tab states delegate to each
    ///     tab VM's <c>SnapshotState()</c>; demo-independent shell flags are read directly.
    /// </summary>
    private SessionPayload SnapshotSession() => new(
        ParserTab.SnapshotState(),
        EntityTab.SnapshotState(),
        AnalysisTab.SnapshotState(),
        IsDebuggerPanelVisible,
        Output.IsVisible,
        SelectedTab?.TabId, // the durable, name-based key — the only tab identity persisted.
        SnapshotModuleTabs(),
        WindowBounds);

    /// <summary>
    ///     Collects session state from MODULE-contributed tabs. The framework has always declared
    ///     <c>IWorkspaceTabViewModel.SnapshotState()</c> and never called it, so no module tab's state
    ///     survived a restart — the Reels tray being the first one where that is a real loss rather than a
    ///     theoretical one (a half-built cross-demo reel is minutes of work).
    ///     <para>
    ///         Only tabs whose VM ALREADY EXISTS are asked. <c>TabViewModel</c> is null until first
    ///         activation, and building every module VM at shutdown purely to ask it for state would pay each
    ///         module's construction cost on every exit — for tabs the user never opened.
    ///     </para>
    /// </summary>
    private Dictionary<string, JsonElement>? SnapshotModuleTabs()
    {
        Dictionary<string, JsonElement> states = [];
        foreach (WorkspaceTabDescriptor tab in Tabs)
        {
            if (tab.TabViewModel?.SnapshotState() is not { } state)
            {
                continue;
            }

            try
            {
                states[tab.TabId] = JsonSerializer.SerializeToElement(state);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // A module returned something unserializable. That module's state is lost; the SESSION is
                // not — one badly-behaved tab must never cost the user their whole restored layout.
                AppLog.DemoLoadFailed(DiagLog, tab.TabId, ex.Message);
            }
        }

        return states.Count > 0 ? states : null;
    }

    /// <summary>
    ///     Hands each module tab its persisted blob. Parked on the descriptor rather than applied here:
    ///     module VMs are built lazily on first activation, so at restore time most of them do not exist yet
    ///     — <c>WorkspaceTabDescriptor.Activate</c> applies the state the moment it builds one, exactly once.
    /// </summary>
    private void RestoreModuleTabs(IReadOnlyDictionary<string, JsonElement>? states)
    {
        if (states is null)
        {
            return;
        }

        foreach (WorkspaceTabDescriptor tab in Tabs)
        {
            if (states.TryGetValue(tab.TabId, out JsonElement state))
            {
                tab.PendingRestoreState = state;
            }
        }
    }

    /// <summary>
    ///     "Step Round" — same shape as StepTick but the boundary is "first frame whose tick
    ///     contains a round_start or round_end game event after the current position".
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDebugStep))]
    private void StepRoundToBreakpoint()
    {
        Debugger.Continue();
        int startIdx = _selectedFrameIndex + 1;
        if (startIdx >= Frames.Count)
        {
            return;
        }

        for (int i = startIdx; i < Frames.Count; i++)
        {
            DemoFrame frame = Frames[i];
            // Tier 1 hit check first.
            if (Debugger.CheckFrame(frame) is not null)
            {
                Debugger.Suppress = true; // protect the just-recorded Tier 1 hit during the seek
                SelectedFrame = frame;
                return;
            }

            // Boundary check: round_start or round_end.
            if (FrameHasRoundTransition(frame))
            {
                SelectedFrame = frame;
                return;
            }
        }

        // Reached end of demo without finding a round boundary.
        SelectedFrame = Frames[^1];
    }

    /// <summary>
    ///     "Step Tick" — like NextTick, but scans Tier 1 breakpoints between current frame
    ///     and the next tick boundary. Halts at the first hit, or at the tick boundary if
    ///     none. Tier 3 hits (PacketIndex / DecodeError / DeltaOnUnknown) surface AFTER
    ///     the seek lands — the Jump-to button on the panel navigates there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDebugStep))]
    private void StepTickToBreakpoint()
    {
        Debugger.Continue();
        int startIdx = _selectedFrameIndex + 1;
        if (startIdx >= Frames.Count)
        {
            return;
        }

        int startTick = Frames[startIdx].ServerTick;

        for (int i = startIdx; i < Frames.Count; i++)
        {
            DemoFrame frame = Frames[i];
            // Tier 1 hit check first — overrides the tick-boundary stop.
            if (Debugger.CheckFrame(frame) is not null)
            {
                Debugger.Suppress = true; // protect the just-recorded Tier 1 hit during the seek
                SelectedFrame = frame;
                return;
            }

            // Stop at the boundary too. We "land" on the first frame of the next tick.
            if (frame.ServerTick > startTick)
            {
                SelectedFrame = frame;
                return;
            }
        }

        // Reached end of demo.
        SelectedFrame = Frames[^1];
    }

    /// <summary>
    ///     Toggle a <see cref="Debugging.BreakpointKind.FrameNumber" /> breakpoint at the
    ///     given frame number. If one already exists for that frame, removes it; otherwise
    ///     adds it. Used by the frame-list gutter click.
    /// </summary>
    [RelayCommand]
    private void ToggleFrameBreakpoint(object? frameNumberObj)
    {
        if (frameNumberObj is null)
        {
            return;
        }

        int frameNumber;
        if (frameNumberObj is int i)
        {
            frameNumber = i;
        }
        else if (!int.TryParse(frameNumberObj.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out frameNumber))
        {
            return;
        }

        // Find existing FrameNumber bp for this frame, if any.
        foreach (Breakpoint bp in Debugger.Breakpoints)
        {
            if (bp.Kind == BreakpointKind.FrameNumber && bp.IntValue == frameNumber)
            {
                Debugger.Remove(bp.Id);
                return;
            }
        }

        Debugger.Add(BreakpointKind.FrameNumber, frameNumber);
    }

    // PreviousTick / NextTick / ToggleTickView (RelayCommands) moved to
    // ReplayTabViewModel in 3.5b. Shell exposes them via the pass-through
    // ICommand shims above.

    // ProtoPath / SrcPath / RawFrameHighlightInfo / RebuildParseChain /
    // SetMessageHighlight (both overloads) / SetPayload / SyncPayloadNodesToCard /
    // TryFindPath / SelectDecompressedTab / SelectRawTab moved to ParserTab in 3.5a.

    // ── Entity seeking ────────────────────────────────────────────────────────
    // The three async seek pipelines moved to EntityTrackingTabViewModel in 3.4c.
    // Shell-side callers (OnSelectedTickFrameChanged, OnSelectedTickGroupChanged
    // -- both since moved to ReplayTab in 3.5b) now invoke EntityTab.SeekEntities*Async(...)
    // via the ReplayTab.OnTickGroupSelected / OnTickFrameSelected callbacks.
    // EntityTab reaches back for the EntityTracker factory, frame source, and
    // the post-seek card refresh via callbacks wired in the ctor.

    // ── Performance stats ─────────────────────────────────────────────────────

    private void UpdatePerfStats()
    {
        _process.Refresh();
        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _lastCpuAt).TotalMilliseconds;
        double used = (_process.TotalProcessorTime - _lastCpuUse).TotalMilliseconds;
        _lastCpuAt = now;
        _lastCpuUse = _process.TotalProcessorTime;

        double cpuPct = elapsed > 0 ? used / (elapsed * Environment.ProcessorCount) * 100.0 : 0;
        long ramMb = _process.WorkingSet64 / 1_048_576;
        // PID is in the title so the running instance can be handed straight to the diagnostics CLI
        // (dotnet-gcdump / dotnet-dump / footprint) without hunting for it in ps — memory questions about
        // this app are answered by attaching to a LIVE process, and `pgrep` is ambiguous while a test host
        // or a second build is running. Constant for the process, so it is read once, not per tick.
        WindowTitle = $"DemoViewer.NET  |  PID {ProcessId}  |  CPU {cpuPct:F1}%  RAM {ramMb} MB";
    }

    /// <summary>Per-type aggregate used while grouping unknown-message occurrences for the Output panel.</summary>
    private sealed class UnknownTypeAggregate(string typeName, int firstFrame, int sampleSize)
    {
        public string TypeName { get; } = typeName;
        public int FirstFrame { get; set; } = firstFrame;
        public int SampleSize { get; } = sampleSize;
        public int Count { get; set; } = 1;
    }
}
