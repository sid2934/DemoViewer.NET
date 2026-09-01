#region

using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Update;
using FuzzySharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
// Aliased: the release-notes service namespace's short name collides with this VM's `Update`
// property (the shared UpdateViewModel), which XAML binds by that exact name.
using UpdateSvc = DemoViewer.NET.Services.Update;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     Backs the Settings screen (P2a-i): the core, always-available settings: user <b>category</b>,
///     <b>library folders</b>, and <b>theme</b>. All settings logic lives here (not in the shell): each
///     change is persisted through <see cref="SettingsService" /> and the VM keeps its bound state in sync
///     with <em>external</em> changes (another surface's write, a hand-edited <c>settings.json</c>) via the
///     injected <c>IOptionsMonitor&lt;AppSettings&gt;</c>. Derives from <see cref="ViewModelBase" /> so the
///     app's <c>ViewLocator</c> resolves <c>Views.Settings.SettingsView</c> for it (window + WASM overlay).
///     <para>
///         P2a-ii adds the per-feature toggle list: <see cref="TabFeatureRows" /> + <see cref="ChromeFeatureRows" />
///         (one <see cref="FeatureToggleRow" /> per <see cref="FeatureCatalog" /> entry) let the user force any
///         feature on/off regardless of category. Each row's displayed state is authoritative from the injected
///         <see cref="IFeatureGate" /> (so cascade + group semantics are honoured); a flip writes an explicit
///         <c>AppSettings.Features.Overrides[id]</c>. The rows refresh live on <see cref="IFeatureGate.Changed" />
///         (a self-write, an external edit, or a category change all re-resolve them).
///     </para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    // ── Findability: filter + grouped sections (v0.6.x package, review R4+R5) ─────────────────
    // Each section's EFFECTIVE visibility = its platform gate AND the fuzzy filter; groups show
    // while any member does, and a non-empty filter auto-expands matching groups. Keywords are the
    // search surface: section title + the labels a user would hunt for.

    private static readonly (string Section, string Keywords)[] _sectionKeywords =
    [
        ("UserCategory", "user category consumer power-user developer tier show hide"),
        ("Theme", "theme dark light high contrast egirl colors appearance reload drop-in"),
        ("Updates", "updates version check release notes update restart"),
        ("Folders", "library folders scan demos dem watch add remove folder"),
        ("Processing", "background processing queue max demos concurrency parse ram"),
        ("Idle", "idle timeout auto close memory ram resume inactivity"),
        ("Features", "features tabs toggles chrome overrides hidden reset gate sub-features"),
        ("LiveSync", "live sync cs2 csvg install path game window fullscreen width height mock "
                     + "tick offset plugin session log verbosity grpc"),
        ("Highlights", "highlights reels clips lead-in lead-out padding output format fps crf "
                       + "bitrate resolution audio scan"),
        ("Diagnostics", "diagnostics logging log level rows file rolling caps size count"),
        ("Playback2DKeys", "keys keybinds keybindings keyboard shortcuts hotkeys gestures rebind "
                           + "controls 2d playback radar draw erase undo pan follow round kill speed")
    ];

    // Every feature row, in one flat list, for the gate-driven refresh sweep (the bound collections below are
    // the same rows split by scope for grouped display).
    private readonly List<FeatureToggleRow> _featureRows = [];

    // The live show/hide authority. Its GET is the source of truth for every FeatureToggleRow.IsEnabled; its
    // Changed event is the cue to refresh the rows. A SINGLETON shared with the shell (composition root), so a
    // toggle here reconciles the app's tabs/chrome and this list from the one gate.
    private readonly IFeatureGate _gate;

    // Whether this is the WASM head. Injected, not read from OperatingSystem here. See the internal ctor.
    private readonly Func<bool> _isBrowser;

    // The external-change subscription (IOptionsMonitor.OnChange). Disposed with the VM.
    private readonly IDisposable? _onChange;

    // The central theme catalogue (built-ins + user drop-ins). SINGLETON shared with App.WireTheme, so a
    // "Reload themes" here re-scans the same registry the running app themes from.
    private readonly ThemeRegistry _registry;

    // Starts the first-run Visual Walkthrough (resolves the shell). Null on hosts that don't wire it (tests).
    private readonly Action? _replayWalkthrough;
    private readonly SettingsService _settings;

    // true while reflecting an EXTERNAL change into the bound properties, so their change-hooks do NOT
    // persist it straight back (which would be a redundant write, and could echo).
    private bool _applyingExternal;

    // ── Background processing: desktop only; suppressed on WASM like Highlights. ──

    /// <summary>
    ///     Master enable for background processing (the persisted "disable" switch, default ON) →
    ///     <c>AppSettings.ProcessingQueue.BackgroundProcessingEnabled</c>. Opening a demo always runs regardless.
    /// </summary>
    [ObservableProperty]
    private bool _backgroundProcessingEnabled = true;

    /// <summary>
    ///     The row currently waiting for a keypress, or null. At most one at a time: two armed rows would
    ///     make the next key ambiguous, and the view routes every key to this one while it is set.
    /// </summary>
    [ObservableProperty]
    private KeybindRow? _capturingKeybind;

    /// <summary>CS2 install path override (empty = auto-detect) → <c>AppSettings.LiveSync.Cs2RootInstallationDirectory</c>.</summary>
    [ObservableProperty]
    private string? _cs2InstallPath;

    /// <summary>Rolled log files retained → <c>AppSettings.Diagnostics.FileMaxCount</c> (next launch).</summary>
    [ObservableProperty]
    private int _diagnosticsFileMaxCount = 5;

    /// <summary>Rolling-file size cap in KB → <c>AppSettings.Diagnostics.FileMaxSizeKilobytes</c> (next launch).</summary>
    [ObservableProperty]
    private int _diagnosticsFileMaxSizeKb = 4096;

    /// <summary>Minimum severity for the internal log stream → <c>AppSettings.Diagnostics.MinimumLogLevel</c>. Live.</summary>
    [ObservableProperty]
    private LiveSyncLogLevel _diagnosticsLogLevel = LiveSyncLogLevel.Information;

    // ── Diagnostics logging (the unified internal ILogger pillar) ─────────────

    /// <summary>Master switch for internal diagnostics logging → <c>AppSettings.Diagnostics.EnableInternalLogging</c>. Live.</summary>
    [ObservableProperty]
    private bool _diagnosticsLoggingEnabled = true;

    /// <summary>Max rows in the in-app log window → <c>AppSettings.Diagnostics.MaxLogRows</c>. Live (v0.6.0).</summary>
    [ObservableProperty]
    private int _diagnosticsMaxLogRows = 5000;

    /// <summary>
    ///     Mirror internal logs to a rolling file → <c>AppSettings.Diagnostics.WriteLogFile</c> (takes effect next
    ///     launch).
    /// </summary>
    [ObservableProperty]
    private bool _diagnosticsWriteLogFile = true;

    private bool _disposed;

    /// <summary>Advanced (developer): force an incompatible plugin → <c>AppSettings.LiveSync.ForceIncompatiblePlugin</c>.</summary>
    [ObservableProperty]
    private bool _forceIncompatiblePlugin;

    // ── Highlights: desktop only; suppressed on WASM like Live Sync. ──

    /// <summary>Background library scan opt-in → <c>AppSettings.Highlights.BackgroundScan</c> (default OFF).</summary>
    [ObservableProperty]
    private bool _highlightsBackgroundScan;

    // ── Idle mode: desktop only; suppressed on WASM like Background processing. ──

    /// <summary>Master enable for idle mode → <c>AppSettings.Idle.Enabled</c> (default ON). Live.</summary>
    [ObservableProperty]
    private bool _idleEnabled = true;

    /// <summary>Keep background processing running while idle → <c>AppSettings.Idle.KeepBackgroundProcessing</c>.</summary>
    [ObservableProperty]
    private bool _idleKeepBackgroundProcessing = true;

    /// <summary>
    ///     Idle wait in MINUTES (editable via a numeric spinner; decimals allowed for finer control, e.g. 0.5 =
    ///     30 s). Maps to the model's <see cref="TimeSpan" /> <c>AppSettings.Idle.IdleTimeoutWait</c> on persist.
    /// </summary>
    [ObservableProperty]
    private double _idleTimeoutMinutes = 15;

    [ObservableProperty]
    private bool _isGroupDiagnosticsExpanded = true;

    [ObservableProperty]
    private bool _isGroupFeaturesExpanded;

    [ObservableProperty]
    private bool _isGroupGeneralExpanded = true;

    [ObservableProperty]
    private bool _isGroupLibraryExpanded = true;

    [ObservableProperty]
    private bool _isGroupLiveCs2Expanded = true;

    /// <summary>
    ///     What a hand-edited settings file got wrong, one line per dropped row, or "". The rebind UI can
    ///     only write valid rows, so a non-empty note here always means the file was edited by hand.
    /// </summary>
    [ObservableProperty]
    private string _keybindRejectionNote = "";

    /// <summary>Also surface framework (ASP.NET/gRPC) log lines → <c>AppSettings.LiveSync.CaptureFrameworkLogs</c>.</summary>
    [ObservableProperty]
    private bool _liveSyncCaptureFrameworkLogs;

    // ── Live Sync (CS2) section: desktop only; suppressed on WASM like the theme drop-ins. ──

    /// <summary>The <c>chrome.livesync</c> opt-in: the non-dev two-step entry. Writes an override; does NOT start a session.</summary>
    [ObservableProperty]
    private bool _liveSyncEnabled;

    /// <summary>Launch CS2 fullscreen → <c>AppSettings.LiveSync.GameFullscreen</c>.</summary>
    [ObservableProperty]
    private bool _liveSyncGameFullscreen;

    /// <summary>CS2 game window height → <c>AppSettings.LiveSync.GameWindowHeight</c>.</summary>
    [ObservableProperty]
    private int _liveSyncGameWindowHeight = 800;

    /// <summary>CS2 game window width → <c>AppSettings.LiveSync.GameWindowWidth</c> (v0.6.0 UI).</summary>
    [ObservableProperty]
    private int _liveSyncGameWindowWidth = 1280;

    /// <summary>
    ///     Minimum severity for the CSVG log surface → <c>AppSettings.LiveSync.MinimumLogLevel</c>.
    ///     Live: lowering it surfaces more detail on a running session with no reconnect.
    /// </summary>
    [ObservableProperty]
    private LiveSyncLogLevel _liveSyncLogLevel = LiveSyncLogLevel.Information;

    /// <summary>Mock-mode toggle (developer) → <c>AppSettings.LiveSync.MockMode</c>.</summary>
    [ObservableProperty]
    private bool _liveSyncMockMode;

    /// <summary>
    ///     The frame→CS2-demo-tick skew shim → <c>AppSettings.LiveSync.TickOffset</c> (v0.6.0 UI:
    ///     its own doc says "override only if validation finds a fixed skew", but until now the only
    ///     way to override it was hand-editing settings.json). Developer-expander surface.
    /// </summary>
    [ObservableProperty]
    private int _liveSyncTickOffset;

    /// <summary>
    ///     Max concurrent heavy parses → <c>AppSettings.ProcessingQueue.MaxConcurrency</c>. DEFAULT 1: a
    ///     16 GB OOM-safety invariant; &gt; 1 is advanced and clamped to [1, <see cref="ConcurrencyMax" />].
    /// </summary>
    [ObservableProperty]
    private int _maxConcurrency = 1;

    /// <summary>Max background-tier items held in the queue → <c>AppSettings.ProcessingQueue.MaxQueueSize</c>.</summary>
    [ObservableProperty]
    private int _maxQueueSize = 200;

    /// <summary>Reel bitrate kbps (0 = unset) → <c>AppSettings.Highlights.ReelBitrateKbps</c> when in bitrate mode.</summary>
    [ObservableProperty]
    private int _reelBitrateKbps;

    /// <summary>Capture game audio in reels → <c>AppSettings.Highlights.ReelCaptureAudio</c>.</summary>
    [ObservableProperty]
    private bool _reelCaptureAudio = true;

    /// <summary>Concatenate the reel's clips into one video → <c>AppSettings.Highlights.ReelConcatenate</c>.</summary>
    [ObservableProperty]
    private bool _reelConcatenate = true;

    /// <summary>Reel container format → <c>AppSettings.Highlights.ReelContainerFormat</c>.</summary>
    [ObservableProperty]
    private string _reelContainerFormat = "mp4";

    /// <summary>Reel CRF quality → <c>AppSettings.Highlights.ReelCrf</c> (lower = better).</summary>
    [ObservableProperty]
    private int _reelCrf = 20;

    /// <summary>Reel capture frame rate → <c>AppSettings.Highlights.ReelFps</c>.</summary>
    [ObservableProperty]
    private int _reelFps = 60;

    /// <summary>Reel default lead-in seconds → <c>AppSettings.Highlights.ClipLeadInSeconds</c>.</summary>
    [ObservableProperty]
    private double _reelLeadInSeconds = 15;

    /// <summary>Reel default lead-out seconds → <c>AppSettings.Highlights.ClipLeadOutSeconds</c>.</summary>
    [ObservableProperty]
    private double _reelLeadOutSeconds = 5;

    /// <summary>Reel output folder → <c>AppSettings.Highlights.ReelOutputDirectory</c>.</summary>
    [ObservableProperty]
    private string? _reelOutputFolder;

    /// <summary>Encoding mode radio: CRF (quality) when true, else bitrate. Persisted via the CRF ⊕ bitrate write.</summary>
    [ObservableProperty]
    private bool _reelUseCrf = true;

    /// <summary>The selected category card (bound to the ListBox SelectedItem). Persisted on change.</summary>
    [ObservableProperty]
    private CategoryOption _selectedCategoryOption;

    /// <summary>The selected theme (bound to the ComboBox SelectedItem). Its <see cref="Theme.Id" /> is persisted on change.</summary>
    [ObservableProperty]
    private Theme _selectedTheme;

    /// <summary>The settings search box. Empty = everything shows (gates permitting).</summary>
    [ObservableProperty]
    private string _settingsFilterText = "";

    [ObservableProperty]
    private bool _showGroupDiagnostics = true;

    [ObservableProperty]
    private bool _showGroupFeatures = true;

    // Group visibility (any member visible) + expansion. Features starts COLLAPSED: its ~25
    // toggle rows are ~35% of the whole page, which is the wall the grouping exists to remove.
    [ObservableProperty]
    private bool _showGroupGeneral = true;

    [ObservableProperty]
    private bool _showGroupLibrary = true;

    [ObservableProperty]
    private bool _showGroupLiveCs2 = true;

    [ObservableProperty]
    private bool _showSectionDiagnostics = true;

    [ObservableProperty]
    private bool _showSectionFeatures = true;

    [ObservableProperty]
    private bool _showSectionFolders = true;

    [ObservableProperty]
    private bool _showSectionHighlights = true;

    [ObservableProperty]
    private bool _showSectionIdle = true;

    [ObservableProperty]
    private bool _showSectionLiveSync = true;

    [ObservableProperty]
    private bool _showSectionPlayback2DKeys = true;

    [ObservableProperty]
    private bool _showSectionProcessing = true;

    [ObservableProperty]
    private bool _showSectionTheme = true;

    [ObservableProperty]
    private bool _showSectionUpdates = true;

    // Per-section effective visibility (gate AND filter).
    [ObservableProperty]
    private bool _showSectionUserCategory = true;

    // Desktop folder-picker source, handed in by the view code-behind (mirrors MainView's storage-provider
    // handoff). Null on WASM / headless, so the folder picker is then unavailable (see CanAddFolder).
    private IStorageProvider? _storageProvider;

    // true while THIS VM is persisting a change, so the synchronous OnChange echo of its own write is
    // skipped as redundant (the bound state already matches what was just written).
    private bool _writing;

    /// <summary>
    ///     Constructs over the live <see cref="SettingsService" />, its bound options monitor, the shared
    ///     <see cref="IFeatureGate" />, and the central <see cref="ThemeRegistry" /> (the theme catalogue). All
    ///     come from the composition root (see <c>App.BuildServices</c>); tests supply a temp-dir service, a
    ///     monitor over its configuration, a gate over that same monitor, and a fresh registry.
    /// </summary>
    public SettingsViewModel(
        SettingsService settings, IOptionsMonitor<AppSettings> monitor, IFeatureGate gate, ThemeRegistry themes,
        Action? replayWalkthrough = null)
        : this(settings, monitor, gate, themes, OperatingSystem.IsBrowser, replayWalkthrough)
    {
    }

    /// <summary>
    ///     Test seam: the same view-model with the host predicate injected.
    ///     <c>OperatingSystem.IsBrowser()</c> is a JIT-folded intrinsic and cannot be faked from outside, so
    ///     without this seam every browser-specific statement this screen renders would have no test
    ///     exercising it. Same seam <c>ShellModuleFeatureGate</c> and <c>AnnotationSessionController</c>
    ///     already use.
    /// </summary>
    /// <param name="settings">The live settings service.</param>
    /// <param name="monitor">Its bound options monitor.</param>
    /// <param name="gate">The shared feature gate.</param>
    /// <param name="themes">The theme catalogue.</param>
    /// <param name="isBrowser">Whether the host is the WASM head.</param>
    /// <param name="replayWalkthrough">Re-runs the tutorial walkthrough, or null.</param>
    internal SettingsViewModel(
        SettingsService settings, IOptionsMonitor<AppSettings> monitor, IFeatureGate gate, ThemeRegistry themes,
        Func<bool> isBrowser, Action? replayWalkthrough = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(isBrowser);
        _settings = settings;
        _gate = gate;
        _isBrowser = isBrowser;
        _replayWalkthrough = replayWalkthrough;
        _registry = themes;

        Categories = BuildCategoryOptions();
        // Populate the theme list from the registry. Held in an ObservableCollection so "Reload themes" can
        // refresh it in place (a drop-in added/edited/deleted) and the ComboBox reflects it.
        RepopulateThemes();

        // Seed the bound state directly from the FIELDS (not the properties) so construction does not
        // trip the change-hooks and write settings straight back.
        AppSettings current = settings.Current;
        _selectedCategoryOption = OptionFor(current.UserCategory);
        _selectedTheme = ThemeFor(current.Theme);
        // Live Sync section: the enable toggle mirrors the GATE decision (an override write flips it); the
        // rest mirror AppSettings.LiveSync. Seed from fields so construction trips no change-hooks.
        _liveSyncEnabled = gate.IsEnabled("chrome.livesync");
        _liveSyncMockMode = current.LiveSync.MockMode;
        _cs2InstallPath = current.LiveSync.Cs2RootInstallationDirectory;
        _forceIncompatiblePlugin = current.LiveSync.ForceIncompatiblePlugin;
        _liveSyncLogLevel = current.LiveSync.MinimumLogLevel;
        _liveSyncCaptureFrameworkLogs = current.LiveSync.CaptureFrameworkLogs;
        _liveSyncGameWindowWidth = current.LiveSync.GameWindowWidth;
        _liveSyncGameWindowHeight = current.LiveSync.GameWindowHeight;
        _liveSyncGameFullscreen = current.LiveSync.GameFullscreen;
        _liveSyncTickOffset = current.LiveSync.TickOffset;
        _diagnosticsLoggingEnabled = current.Diagnostics.EnableInternalLogging;
        _diagnosticsLogLevel = current.Diagnostics.MinimumLogLevel;
        _diagnosticsWriteLogFile = current.Diagnostics.WriteLogFile;
        _diagnosticsMaxLogRows = current.Diagnostics.MaxLogRows;
        _diagnosticsFileMaxSizeKb = current.Diagnostics.FileMaxSizeKilobytes;
        _diagnosticsFileMaxCount = current.Diagnostics.FileMaxCount;
        // Highlights section: seed from fields so construction trips no change-hooks.
        _highlightsBackgroundScan = current.Highlights.BackgroundScan;
        _reelOutputFolder = current.Highlights.ReelOutputDirectory;
        _reelContainerFormat = current.Highlights.ReelContainerFormat;
        _reelFps = current.Highlights.ReelFps;
        _reelLeadInSeconds = current.Highlights.ClipLeadInSeconds;
        _reelLeadOutSeconds = current.Highlights.ClipLeadOutSeconds;
        _reelConcatenate = current.Highlights.ReelConcatenate;
        _reelCaptureAudio = current.Highlights.ReelCaptureAudio;
        _reelUseCrf = current.Highlights.ReelBitrateKbps is null;
        _reelCrf = current.Highlights.ReelCrf;
        _reelBitrateKbps = current.Highlights.ReelBitrateKbps ?? 0;
        // Background processing section: seed from fields so construction trips no change-hooks.
        _backgroundProcessingEnabled = current.ProcessingQueue.BackgroundProcessingEnabled;
        _maxQueueSize = current.ProcessingQueue.MaxQueueSize;
        _maxConcurrency = current.ProcessingQueue.MaxConcurrency;
        // Idle section: seed from fields so construction trips no change-hooks. The model is a TimeSpan;
        // the editable surface is whole/fractional minutes.
        _idleEnabled = current.Idle.Enabled;
        _idleTimeoutMinutes = current.Idle.IdleTimeoutWait.TotalMinutes;
        _idleKeepBackgroundProcessing = current.Idle.KeepBackgroundProcessing;
        foreach (string folder in current.Library.Folders)
        {
            LibraryFolders.Add(folder);
        }

        // Build the feature-toggle rows (grouped: Tabs each followed by their SubFeatures, then Chrome), seed
        // their state from the gate, and subscribe for live re-resolution. The gate marshals Changed to the UI
        // thread in the headed app, so the handler need not marshal again.
        BuildFeatureRows();
        RefreshFeatureRows();
        _gate.Changed += OnGateChanged;

        // The 2D keybinding rows: the shipped table is the list, the resolved profile is the state.
        BuildKeybindRows();
        RefreshKeybindRows();

        // React to external settings changes. Self-writes raise this too, synchronously, while _writing is
        // set, so they are skipped as a redundant echo. The name arg is unused.
        _onChange = monitor.OnChange((updated, _) => OnSettingsChanged(updated));

        // Findability (v0.6.x): seed the section/group visibility from the platform gates (empty filter).
        ApplySectionFilter();
    }

    /// <summary>Log-level choices for the CSVG-log severity ComboBox.</summary>
    public IReadOnlyList<LiveSyncLogLevel> LiveSyncLogLevelOptions { get; } = Enum.GetValues<LiveSyncLogLevel>();

    /// <summary>The rolling-log-file directory (null on WASM), shown so users can find the files.</summary>
    public string? DiagnosticsLogsFolderPath { get; } = AppPaths.LogsDir;

    /// <summary>Whether the diagnostics-logging section is manageable (desktop only: no filesystem on WASM).</summary>
    public bool CanManageDiagnosticsLogging { get; } = !OperatingSystem.IsBrowser();

    /// <summary>The gate's hard concurrency ceiling ([1, 8]): surfaces the bound in the picker + copy.</summary>
    public int ConcurrencyMax { get; } = HeavyJobGate.HardCapConcurrency;

    /// <summary>The selectable max-concurrency values (1..<see cref="ConcurrencyMax" />) for the picker.</summary>
    public IReadOnlyList<int> ConcurrencyOptions { get; } =
        [.. Enumerable.Range(1, HeavyJobGate.HardCapConcurrency)];

    /// <summary>True when max-concurrency is above the safe default of 1, revealing the RAM-risk warning.</summary>
    public bool ShowConcurrencyRiskWarning => MaxConcurrency > 1;

    /// <summary>
    ///     Whether the Background-processing section is shown: desktop only (background work needs a
    ///     filesystem; there is none on the browser host, so nothing to configure there).
    /// </summary>
    public bool CanManageProcessingQueue { get; } = !OperatingSystem.IsBrowser();

    /// <summary>Whether the Idle-mode section is shown: desktop only (idle mode is a no-op on WASM).</summary>
    public bool CanManageIdle { get; } = !OperatingSystem.IsBrowser();

    /// <summary>
    ///     The shared updater VM: the SAME instance the shell banner binds to, so a check started
    ///     here raises the banner and the resolved update stays installable. Bound directly by the
    ///     Settings view (version line, Check button, status text).
    /// </summary>
    public UpdateViewModel Update { get; } = UpdateViewModel.Shared;

    /// <summary>Offered reel container formats.</summary>
    public IReadOnlyList<string> ReelContainerFormats { get; } = ["mp4", "mkv", "mov"];

    /// <summary>Offered reel frame rates.</summary>
    public IReadOnlyList<int> ReelFpsOptions { get; } = [30, 60, 120];

    /// <summary>The three selectable user-category cards, each with a one-line description.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Watched library folders, mirroring <c>AppSettings.Library.Folders</c>.</summary>
    public ObservableCollection<string> LibraryFolders { get; } = [];

    /// <summary>
    ///     Offered themes from the central <see cref="ThemeRegistry" />: the
    ///     built-in Dark / Light / System plus any custom built-ins (High-Contrast, E-Girl) and user drop-ins.
    ///     The ComboBox shows each theme's <see cref="Theme.DisplayName" />; selecting one persists its
    ///     <see cref="Theme.Id" />, which <c>App.WireTheme</c> resolves onto <c>RequestedThemeVariant</c>.
    ///     An <see cref="ObservableCollection{T}" /> so <see cref="ReloadThemesCommand" /> refreshes it in place.
    /// </summary>
    public ObservableCollection<Theme> Themes { get; } = [];

    /// <summary>The user theme drop-in folder path (for the hint), or <c>null</c> on WASM (no filesystem).</summary>
    public string? ThemesFolderPath { get; } = AppPaths.ThemesDirectory;

    /// <summary>Whether user theme drop-ins are available (a real filesystem: false on the browser host).</summary>
    public bool CanManageThemes { get; } = !OperatingSystem.IsBrowser();

    /// <summary>Whether the Live Sync (CS2) section is shown: desktop only (suppressed on WASM).</summary>
    public bool CanManageLiveSync { get; } = !OperatingSystem.IsBrowser();

    /// <summary>Whether the Highlights section is shown: desktop only (cache/scan/reel need a filesystem).</summary>
    public bool CanManageHighlights { get; } = !OperatingSystem.IsBrowser();

    /// <summary>The effective user category: the selected card's value. Convenience for callers/tests.</summary>
    public UserCategory SelectedCategory => SelectedCategoryOption.Value;

    /// <summary>
    ///     Tab rows and their nested SubFeature rows (each tab immediately followed by its children, indented),
    ///     in catalog order, the first grouped block of the feature-toggle list.
    /// </summary>
    public ObservableCollection<FeatureToggleRow> TabFeatureRows { get; } = [];

    /// <summary>Global-chrome rows (no parent tab), the second grouped block of the feature-toggle list.</summary>
    public ObservableCollection<FeatureToggleRow> ChromeFeatureRows { get; } = [];

    /// <summary>
    ///     How many non-Required features the current user has hidden versus the developer-full baseline (from
    ///     the gate). Drives the "N hidden for <c>Category</c>" section affordance; 0 for a developer.
    /// </summary>
    public int HiddenCount => _gate.HiddenCount;

    /// <summary>The gate's effective category label (respects DeveloperMode escalation), for the header/reset copy.</summary>
    public string FeatureCategoryLabel => LabelFor(_gate.Category);

    /// <summary>Section sub-heading, e.g. "2 hidden for Power-User".</summary>
    public string FeaturesHeaderText => $"{HiddenCount} hidden for {FeatureCategoryLabel}";

    /// <summary>Reset-button caption, e.g. "Reset to Power-User defaults".</summary>
    public string ResetButtonText => $"Reset to {FeatureCategoryLabel} defaults";

    /// <summary>
    ///     Whether the folder picker is available. The browser sandbox has no OS folder picker, so Add is
    ///     disabled there. A get-only property (instance-backed) so XAML can bind it.
    /// </summary>
    public bool CanAddFolder { get; } = !OperatingSystem.IsBrowser();

    /// <summary>
    ///     Deep-link target (v0.6.0): the <c>x:Name</c> of a section header the view scrolls into view on
    ///     attach (e.g. <c>"SectionUserCategory"</c> from the status strip's "N features hidden" note).
    ///     Set by the opener BEFORE the VM is handed to the window service; null → open at the top as always.
    /// </summary>
    public string? ScrollTargetSection { get; set; }

    /// <summary>
    ///     Gates the UPDATES "View release notes" button (v0.6.0): needs a stamped version to look
    ///     up, and a desktop host (the browser window service's What's New surface is a no-op, so
    ///     the button would silently do nothing there).
    /// </summary>
    public bool CanViewReleaseNotes { get; } =
        UpdateSvc.AppVersionInfo.CurrentReleaseVersion is not null && !OperatingSystem.IsBrowser();

    // ── 2D playback controls: the keybinding surface ──────────────────────────
    // The shipped table is the row list; the RESOLVED profile is what each row displays. Every write is
    // validated before it is persisted, so the settings file can only ever hold rows that resolve. The
    // profile's drop-and-report path exists for a HAND-edited file, not for anything this screen writes.

    /// <summary>One row per keymap action, in the shipped table's authored order.</summary>
    public ObservableCollection<KeybindRow> Playback2DKeybindRows { get; } = [];

    /// <summary>Whether any override row was dropped, revealing the note.</summary>
    public bool HasKeybindRejections => KeybindRejectionNote.Length > 0;

    /// <summary>
    ///     Whether rebinds survive a restart. False on the browser head, where <c>SettingsService</c>
    ///     selects its fileless in-memory provider. Every write lands in a dictionary that dies with the
    ///     page. A user rebinds twenty gestures, watches every one of them apply live, and loses the lot on
    ///     refresh.
    /// </summary>
    public bool KeybindsPersist => !_isBrowser();

    /// <summary>
    ///     The sentence shown when they do not, or "". Deliberately the same shape used for annotations
    ///     (<c>"session only — this browser tab forgets annotations when it reloads"</c>): this is a
    ///     second surface with the same property, so it uses the same wording.
    /// </summary>
    public string KeybindPersistenceNote => KeybindsPersist
        ? ""
        : "Session only — this browser tab forgets rebound keys when it reloads.";

    /// <summary>
    ///     Where the dropped override rows came from. On desktop that is a hand-edited
    ///     <c>settings.json</c>; on the browser there is no such file, and naming one sends the user
    ///     looking for something that does not exist on their machine.
    /// </summary>
    public string KeybindRejectionSource => KeybindsPersist
        ? "Some keybinding overrides in settings.json were ignored; the shipped gestures are used for them."
        : "Some stored keybinding overrides were ignored; the shipped gestures are used for them.";

    /// <summary>How many actions are bound to something other than their shipped gesture.</summary>
    public int CustomKeybindCount { get; private set; }

    /// <summary>Whether anything is rebound, revealing the section header's "N custom" badge.</summary>
    public bool HasCustomKeybinds => CustomKeybindCount > 0;

    /// <summary>Whether the "Replay walkthrough" affordance is shown (a starter was wired: desktop app only).</summary>
    public bool CanReplayWalkthrough => _replayWalkthrough is not null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Changed -= OnGateChanged;
        _onChange?.Dispose();
    }

    /// <summary>
    ///     The three selectable user-category cards with their one-line descriptions: the single shared
    ///     source used by both the Settings screen and the first-run wizard, so the copy never drifts.
    /// </summary>
    public static IReadOnlyList<CategoryOption> BuildCategoryOptions() =>
    [
        new(UserCategory.Consumer, "Consumer",
            "Viewing and built-in analysis only."),
        new(UserCategory.PowerUser, "Power-User",
            "Adds Analysis and Authoring tools (some guarded)."),
        new(UserCategory.Developer, "Developer",
            "Full access, including the parser and diagnostics workbenches.")
    ];

    /// <summary>Raised when the user asks to dismiss the screen (Close). The window closes; the overlay clears.</summary>
    public event EventHandler? CloseRequested;

    partial void OnSettingsFilterTextChanged(string value) => ApplySectionFilter();

    private static bool SectionMatches(string section, string filter)
    {
        if (filter.Length == 0)
        {
            return true;
        }

        string keywords = Array.Find(_sectionKeywords, k => k.Section == section).Keywords;
        return keywords.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || Fuzz.PartialRatio(filter.ToLowerInvariant(), keywords) >= 80;
    }

    // Recomputes every section/group visibility from (platform gate × filter). Called from the
    // ctor once and on every filter keystroke; a non-empty filter auto-expands matching groups so
    // a hit inside a collapsed group is never invisible.
    private void ApplySectionFilter()
    {
        string filter = SettingsFilterText.Trim();

        ShowSectionUserCategory = SectionMatches("UserCategory", filter);
        ShowSectionTheme = SectionMatches("Theme", filter);
        ShowSectionUpdates = SectionMatches("Updates", filter);
        ShowSectionFolders = SectionMatches("Folders", filter);
        ShowSectionProcessing = CanManageProcessingQueue && SectionMatches("Processing", filter);
        ShowSectionIdle = CanManageIdle && SectionMatches("Idle", filter);
        ShowSectionFeatures = SectionMatches("Features", filter);
        ShowSectionLiveSync = CanManageLiveSync && SectionMatches("LiveSync", filter);
        ShowSectionHighlights = CanManageHighlights && SectionMatches("Highlights", filter);
        ShowSectionDiagnostics = CanManageDiagnosticsLogging && SectionMatches("Diagnostics", filter);
        // No platform gate: the 2D tab (and therefore its keymap) is WASM-reachable.
        ShowSectionPlayback2DKeys = SectionMatches("Playback2DKeys", filter);

        ShowGroupGeneral = ShowSectionUserCategory || ShowSectionTheme || ShowSectionUpdates
                           || ShowSectionPlayback2DKeys;
        ShowGroupLibrary = ShowSectionFolders || ShowSectionProcessing || ShowSectionIdle;
        ShowGroupFeatures = ShowSectionFeatures;
        ShowGroupLiveCs2 = ShowSectionLiveSync || ShowSectionHighlights;
        ShowGroupDiagnostics = ShowSectionDiagnostics;

        if (filter.Length > 0)
        {
            IsGroupGeneralExpanded |= ShowGroupGeneral;
            IsGroupLibraryExpanded |= ShowGroupLibrary;
            IsGroupFeaturesExpanded |= ShowGroupFeatures;
            IsGroupLiveCs2Expanded |= ShowGroupLiveCs2;
            IsGroupDiagnosticsExpanded |= ShowGroupDiagnostics;
        }
    }

    partial void OnKeybindRejectionNoteChanged(string value) =>
        OnPropertyChanged(nameof(HasKeybindRejections));

    // One row per SHIPPED binding, reserved rows included: a reserved gesture that is simply absent from
    // the list reads as free, which is the opposite of what the reservation means.
    private void BuildKeybindRows()
    {
        foreach (Playback2DBinding binding in Playback2DKeymapProfile.Default.Bindings)
        {
            Playback2DKeybindRows.Add(new KeybindRow(this, binding));
        }
    }

    // Re-resolve every row from the persisted overrides. Called at construction, after each write, and
    // from Reflect (an external edit / another surface).
    private void RefreshKeybindRows()
    {
        // The host is passed explicitly: on the browser the reserved set also carries the gestures the
        // BROWSER takes before the page sees them (Ctrl+T, F12, …), which a rebind must be refused for.
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(
            _settings.Current.Playback2D.KeybindOverrides, out IReadOnlyList<string> rejected,
            _isBrowser());

        int custom = 0;
        foreach (KeybindRow row in Playback2DKeybindRows)
        {
            row.Refresh(profile);
            if (row.IsOverridden)
            {
                custom++;
            }
        }

        CustomKeybindCount = custom;
        OnPropertyChanged(nameof(CustomKeybindCount));
        OnPropertyChanged(nameof(HasCustomKeybinds));
        KeybindRejectionNote = rejected.Count == 0 ? "" : string.Join("\n", rejected);
    }

    /// <summary>
    ///     Arms <paramref name="row" /> for capture: the next keypress inside the Settings view becomes
    ///     its gesture. Re-arming a different row disarms the previous one.
    /// </summary>
    /// <param name="row">The row to rebind.</param>
    internal void BeginKeybindCapture(KeybindRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsBindable)
        {
            return;
        }

        CancelKeybindCapture();
        row.Conflict = "";
        row.IsCapturing = true;
        CapturingKeybind = row;
    }

    /// <summary>Disarms whatever row is capturing. Idempotent.</summary>
    internal void CancelKeybindCapture()
    {
        if (CapturingKeybind is { } row)
        {
            row.IsCapturing = false;
        }

        CapturingKeybind = null;
    }

    /// <summary>
    ///     Feeds a keypress to the armed row. Returns true when the key was CONSUMED: the view marks it
    ///     handled, which is what stops a captured <c>Space</c> from also clicking the button underneath
    ///     it and a captured letter from typing into the search box.
    ///     <para>
    ///         A bare modifier is consumed but does not complete the capture: <c>Ctrl</c> arrives as its
    ///         own key event a moment before <c>Ctrl+Z</c> does, and finishing on it would make every
    ///         modified gesture impossible to enter.
    ///     </para>
    /// </summary>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifiers">The modifiers held with it.</param>
    internal bool HandleKeybindCapture(Key key, KeyModifiers modifiers)
    {
        if (CapturingKeybind is not { } row)
        {
            return false;
        }

        if (IsModifierKey(key))
        {
            return true;
        }

        // Esc backs out, so it can never be captured this way even though it IS a bindable gesture
        // (clear-follow / cancel). The reset affordance is the way to rebind it. Capture is a mode the
        // user can enter by accident, and it must always have an exit.
        if (key == Key.Escape)
        {
            CancelKeybindCapture();
            return true;
        }

        ApplyKeybindCapture(row, key, modifiers);
        return true;
    }

    /// <summary>Drops <paramref name="row" />'s override, reverting it to the shipped gesture.</summary>
    /// <param name="row">The row to reset.</param>
    internal void ResetKeybind(KeybindRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        CancelKeybindCapture();
        row.Conflict = "";

        string[] remaining = WithoutAction(_settings.Current.Playback2D.KeybindOverrides, row.Action);
        if (remaining.Length == _settings.Current.Playback2D.KeybindOverrides.Length)
        {
            return; // not overridden
        }

        Persist(s => s.Playback2D.KeybindOverrides = remaining);
        RefreshKeybindRows();
    }

    /// <summary>Clears every 2D keybinding override, returning the whole table to the shipped gestures.</summary>
    [RelayCommand]
    private void ResetAllKeybinds()
    {
        CancelKeybindCapture();
        foreach (KeybindRow row in Playback2DKeybindRows)
        {
            row.Conflict = "";
        }

        Persist(s => s.Playback2D.KeybindOverrides = []);
        RefreshKeybindRows();
    }

    // Validate FIRST, persist second. A conflicting rebind is refused with its reason on the row rather
    // than written and silently dropped on the next load. The user has to be able to see WHY the key
    // they pressed did not take.
    private void ApplyKeybindCapture(KeybindRow row, Key key, KeyModifiers modifiers)
    {
        string[] existing = _settings.Current.Playback2D.KeybindOverrides;
        string candidate = Playback2DKeymapProfile.Row(row.Action, key, modifiers);

        string reason = Playback2DKeymapProfile.ValidateOverride(existing, candidate, _isBrowser());
        if (reason.Length > 0)
        {
            row.Conflict = reason;
            CancelKeybindCapture();
            return;
        }

        // Rebinding an action back to its shipped gesture REMOVES the row instead of storing a redundant
        // one: an override is a promise to keep that key even if the default moves, and pressing the key
        // that was already there does not make that promise.
        Playback2DBinding? shipped = Playback2DKeymapProfile.Default.BindingFor(row.Action);
        bool isShippedGesture = shipped is { } d && d.Key == key && d.Modifiers == modifiers;

        string[] updated = WithoutAction(existing, row.Action);
        if (!isShippedGesture)
        {
            updated = [.. updated, candidate];
        }

        row.Conflict = "";
        CancelKeybindCapture();
        Persist(s => s.Playback2D.KeybindOverrides = updated);
        RefreshKeybindRows();
    }

    private static string[] WithoutAction(string[] rows, Playback2DAction action)
    {
        string prefix = action + "=";
        return [.. rows.Where(r => !r.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
    }

    // A modifier's own key event carries the modifier in neither Key nor KeyModifiers reliably across
    // platforms, so they are matched by key identity rather than by inspecting the flags.
    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin or Key.System or Key.None;

    /// <summary>
    ///     Re-opens the "What's new" window for the RUNNING version. The post-update gate shows it
    ///     once; this is the on-demand path (and the only way to re-read the notes without leaving
    ///     the app). Notes come from the shared per-version cache, so a re-open never re-fetches.
    /// </summary>
    [RelayCommand]
    private static void ViewReleaseNotes()
    {
        if (UpdateSvc.AppVersionInfo.CurrentReleaseVersion is not { } version)
        {
            return;
        }

        IWindowService? windows = App.Services?.GetService<IWindowService>();
        if (windows is null)
        {
            return;
        }

        windows.ShowWhatsNew(new WhatsNewViewModel(version, UpdateSvc.GitHubReleaseNotesService.Shared));
    }

    /// <summary>
    ///     Supplies the desktop folder-picker source (mirrors <c>MainView</c>'s storage-provider handoff).
    ///     Null on WASM / headless leaves the picker unavailable.
    /// </summary>
    public void SetStorageProvider(IStorageProvider? provider) => _storageProvider = provider;

    /// <summary>
    ///     Adds the given folders to the watched set (deduped, order-preserving) and persists. This is the
    ///     write path <see cref="AddFolderAsync" /> feeds after the picker; exposed <c>internal</c> so tests
    ///     exercise it without an OS folder picker.
    /// </summary>
    internal void AddFolders(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] updated = LibraryFolders
            .Concat(paths)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (updated.Length == LibraryFolders.Count)
        {
            return; // nothing new
        }

        ApplyFolders(updated);
    }

    partial void OnSelectedCategoryOptionChanged(CategoryOption value)
    {
        OnPropertyChanged(nameof(SelectedCategory));
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.UserCategory = value.Value);
    }

    partial void OnSelectedThemeChanged(Theme value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Theme = value.Id);
    }

    // ── Live Sync section change-hooks (echo-guarded like the theme/category hooks) ──

    // Writes the chrome.livesync OVERRIDE (the opt-in): it makes the chip AVAILABLE; it never starts a
    // session. The override write fires gate.Changed → RefreshFeatureRows re-syncs this back under the guard.
    partial void OnLiveSyncEnabledChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        WriteFeatureOverride("chrome.livesync", value);
    }

    partial void OnLiveSyncMockModeChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.LiveSync.MockMode = value);
    }

    partial void OnCs2InstallPathChanged(string? value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.LiveSync.Cs2RootInstallationDirectory = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    partial void OnForceIncompatiblePluginChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.LiveSync.ForceIncompatiblePlugin = value);
    }

    partial void OnLiveSyncLogLevelChanged(LiveSyncLogLevel value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.LiveSync.MinimumLogLevel = value);
    }

    partial void OnLiveSyncCaptureFrameworkLogsChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.LiveSync.CaptureFrameworkLogs = value);
    }

    // Game-window geometry (v0.6.0): clamped to sane pixel ranges like the other numeric fields.
    // A 0×0 or absurd window is a typo, not a preference. Applies to the NEXT session launch.

    partial void OnLiveSyncGameWindowWidthChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        int clamped = Math.Clamp(value, 640, 7680);
        if (clamped != value)
        {
            LiveSyncGameWindowWidth = clamped; // re-enters with the clamped value, which then persists
            return;
        }

        Persist(s => s.LiveSync.GameWindowWidth = value);
    }

    partial void OnLiveSyncGameWindowHeightChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        int clamped = Math.Clamp(value, 480, 4320);
        if (clamped != value)
        {
            LiveSyncGameWindowHeight = clamped;
            return;
        }

        Persist(s => s.LiveSync.GameWindowHeight = value);
    }

    partial void OnLiveSyncGameFullscreenChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.LiveSync.GameFullscreen = value);
    }

    partial void OnLiveSyncTickOffsetChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.LiveSync.TickOffset = value);
    }

    partial void OnDiagnosticsLoggingEnabledChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Diagnostics.EnableInternalLogging = value);
    }

    partial void OnDiagnosticsLogLevelChanged(LiveSyncLogLevel value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Diagnostics.MinimumLogLevel = value);
    }

    partial void OnDiagnosticsWriteLogFileChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Diagnostics.WriteLogFile = value);
    }

    // The three caps (v0.6.0) clamp defensively like MaxQueueSize: a hand-edited settings.json or a
    // typo'd field must not produce a zero-row log window or an unbounded file set.

    partial void OnDiagnosticsMaxLogRowsChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        int clamped = Math.Clamp(value, 100, 100_000);
        if (clamped != value)
        {
            DiagnosticsMaxLogRows = clamped; // re-enters with the clamped value, which then persists
            return;
        }

        Persist(s => s.Diagnostics.MaxLogRows = value);
    }

    partial void OnDiagnosticsFileMaxSizeKbChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        int clamped = Math.Clamp(value, 64, 1_048_576);
        if (clamped != value)
        {
            DiagnosticsFileMaxSizeKb = clamped;
            return;
        }

        Persist(s => s.Diagnostics.FileMaxSizeKilobytes = value);
    }

    partial void OnDiagnosticsFileMaxCountChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        int clamped = Math.Clamp(value, 1, 50);
        if (clamped != value)
        {
            DiagnosticsFileMaxCount = clamped;
            return;
        }

        Persist(s => s.Diagnostics.FileMaxCount = value);
    }

    // ── Highlights section change-hooks (echo-guarded like the Live Sync hooks) ──

    partial void OnHighlightsBackgroundScanChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.BackgroundScan = value);
    }

    partial void OnReelOutputFolderChanged(string? value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.ReelOutputDirectory = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    partial void OnReelContainerFormatChanged(string value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.ReelContainerFormat = value);
    }

    partial void OnReelFpsChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.ReelFps = value);
    }

    partial void OnReelLeadInSecondsChanged(double value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.ClipLeadInSeconds = value);
    }

    partial void OnReelLeadOutSecondsChanged(double value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.ClipLeadOutSeconds = value);
    }

    partial void OnReelConcatenateChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.ReelConcatenate = value);
    }

    partial void OnReelCaptureAudioChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Highlights.ReelCaptureAudio = value);
    }

    partial void OnReelUseCrfChanged(bool value) => PersistReelEncoding();
    partial void OnReelCrfChanged(int value) => PersistReelEncoding();
    partial void OnReelBitrateKbpsChanged(int value) => PersistReelEncoding();

    // CRF ⊕ bitrate is structurally exclusive: CRF mode persists a null bitrate; bitrate mode persists the
    // kbps (0 → null, treated as unset). One write covers all three inputs.
    private void PersistReelEncoding()
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s =>
        {
            s.Highlights.ReelCrf = ReelCrf;
            s.Highlights.ReelBitrateKbps = ReelUseCrf || ReelBitrateKbps <= 0 ? null : ReelBitrateKbps;
        });
    }

    // ── Background processing section change-hooks (echo-guarded like the Highlights hooks) ──

    partial void OnBackgroundProcessingEnabledChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.ProcessingQueue.BackgroundProcessingEnabled = value);
    }

    partial void OnMaxQueueSizeChanged(int value)
    {
        if (_applyingExternal)
        {
            return;
        }

        // Clamp defensively (a hand-edited settings.json / bad input): at least 1 (0 would reject everything).
        int clamped = Math.Clamp(value, 1, 10000);
        if (clamped != value)
        {
            MaxQueueSize = clamped; // re-enters this hook with the clamped value, which then persists
            return;
        }

        Persist(s => s.ProcessingQueue.MaxQueueSize = value);
    }

    partial void OnMaxConcurrencyChanged(int value)
    {
        // The risk warning tracks the value regardless of source (an external edit flips it too).
        OnPropertyChanged(nameof(ShowConcurrencyRiskWarning));
        if (_applyingExternal)
        {
            return;
        }

        // Clamp to the gate's hard cap [1, 8] (the picker already constrains, but an external edit may not).
        int clamped = Math.Clamp(value, 1, ConcurrencyMax);
        if (clamped != value)
        {
            MaxConcurrency = clamped; // re-enters this hook with the clamped value, which then persists
            return;
        }

        Persist(s => s.ProcessingQueue.MaxConcurrency = value);
    }

    // ── Idle mode handlers ────────────────────────────────────────────────────

    partial void OnIdleEnabledChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Idle.Enabled = value);
    }

    partial void OnIdleTimeoutMinutesChanged(double value)
    {
        if (_applyingExternal)
        {
            return;
        }

        // Clamp defensively (hand-edited value / bad input): floor at 0.1 min (6 s) so the countdown is
        // never effectively disabled by a zero here. The master Enabled toggle is the way to turn it off.
        double clamped = Math.Clamp(value, 0.1, 1440); // up to 24h
        if (Math.Abs(clamped - value) > double.Epsilon)
        {
            IdleTimeoutMinutes = clamped; // re-enters with the clamped value, which then persists
            return;
        }

        Persist(s => s.Idle.IdleTimeoutWait = TimeSpan.FromMinutes(value));
    }

    partial void OnIdleKeepBackgroundProcessingChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Idle.KeepBackgroundProcessing = value);
    }

    /// <summary>Picks the reel output folder via the OS folder picker (desktop). No-op when no picker is wired.</summary>
    [RelayCommand]
    private async Task BrowseReelOutputAsync()
    {
        if (_storageProvider is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await _storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select the reel output folder",
                AllowMultiple = false
            });
        string? path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            ReelOutputFolder = path;
        }
    }

    /// <summary>
    ///     Picks the CS2 install folder via the OS folder picker (desktop). No-op when no picker is wired
    ///     (WASM / headless). The whole section is suppressed there via <see cref="CanManageLiveSync" />.
    /// </summary>
    [RelayCommand]
    private async Task BrowseCs2InstallAsync()
    {
        if (_storageProvider is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await _storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select your CS2 install folder",
                AllowMultiple = false
            });
        string? path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            Cs2InstallPath = path;
        }
    }

    /// <summary>Selects a category by value (e.g. a card button, or a test). Persists via the property hook.</summary>
    [RelayCommand]
    private void SelectCategory(UserCategory category) => SelectedCategoryOption = OptionFor(category);

    /// <summary>
    ///     Adds one or more folders via the OS folder picker (desktop). No-op when no picker is wired
    ///     (WASM / headless). The Add button is disabled there via <see cref="CanAddFolder" />.
    /// </summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_storageProvider is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await _storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Add demo folder",
                AllowMultiple = true
            });

        List<string> paths = picked
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        AddFolders(paths);
    }

    /// <summary>Removes a watched folder and persists.</summary>
    [RelayCommand]
    private void RemoveFolder(string path)
    {
        string[] updated = LibraryFolders
            .Where(f => !string.Equals(f, path, StringComparison.Ordinal))
            .ToArray();
        if (updated.Length == LibraryFolders.Count)
        {
            return; // not present
        }

        ApplyFolders(updated);
    }

    /// <summary>Requests the screen be dismissed.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    ///     Replays the first-run Visual Walkthrough from the top, then closes Settings so the tour is visible
    ///     on the main window. Inert when no starter was wired (tests / degraded host).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReplayWalkthrough))]
    private void ReplayWalkthrough()
    {
        _replayWalkthrough?.Invoke();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Re-scans the drop-in theme folder (T3) so a newly-added / edited / deleted <c>*.json</c> shows up
    ///     without a restart. <see cref="ThemeRegistry.Reload" /> raises <c>Reloaded</c>, which
    ///     <c>App.WireTheme</c> handles by repainting the running app; here the picker list refreshes, keeping
    ///     the current selection (or falling back if its drop-in was removed). No settings are persisted (a reload
    ///     is not a user choice). The reselect is guarded.
    /// </summary>
    [RelayCommand]
    private void ReloadThemes()
    {
        string currentId = SelectedTheme.Id;
        _registry.Reload();

        _applyingExternal = true;
        try
        {
            RepopulateThemes();
            SelectedTheme = ThemeFor(currentId);
        }
        finally
        {
            _applyingExternal = false;
        }
    }

    /// <summary>
    ///     Re-launches the first-run setup wizard (P2b: the relaunchable first-run experience). Resolves
    ///     the host window service + a fresh wizard VM from the composition root, the same service-locator
    ///     seam <c>MainViewModel.OpenSettings</c> uses, so it needs no extra constructor dependency. Inert
    ///     on the designer / test path (no container, or a partial one).
    /// </summary>
    [RelayCommand]
    private void RerunFirstTimeSetup()
    {
        IServiceProvider? services = App.Services;
        if (services is null)
        {
            return;
        }

        IWindowService? windowService = services.GetService<IWindowService>();
        FirstRunWizardViewModel? wizard = services.GetService<Func<FirstRunWizardViewModel>>()?.Invoke();
        if (windowService is null || wizard is null)
        {
            return;
        }

        windowService.ShowFirstRunWizard(wizard);

        // Dismiss this Settings surface behind the wizard: on desktop it closes the (non-modal) Settings
        // window so the modal wizard isn't stacked over it; on WASM it clears the Settings overlay so the
        // wizard overlay stands alone.
        Close();
    }

    /// <summary>
    ///     Clears every per-feature override, reverting the whole list to the category defaults. The rows then
    ///     refresh via the gate's Changed event (no per-row writes).
    /// </summary>
    [RelayCommand]
    private void ResetOverrides() => Persist(s => s.Features.Overrides.Clear());

    /// <summary>Persists an explicit on/off override for <paramref name="featureId" /> (the row-toggle write path).</summary>
    internal void WriteFeatureOverride(string featureId, bool enabled) =>
        Persist(s => s.Features.Overrides[featureId] = enabled);

    /// <summary>Removes the explicit override for <paramref name="featureId" /> (the per-row clear affordance).</summary>
    internal void ClearFeatureOverride(string featureId) =>
        Persist(s => s.Features.Overrides.Remove(featureId));

    // Build the grouped row list once: from FeatureCatalog.All, each Tab immediately followed by its
    // SubFeature children (indented), then all Chrome rows. The flat _featureRows mirror is the refresh sweep.
    private void BuildFeatureRows()
    {
        foreach (FeatureDescriptor descriptor in FeatureCatalog.All)
        {
            if (descriptor.Scope != FeatureScope.Tab)
            {
                continue;
            }

            AddFeatureRow(TabFeatureRows, descriptor, 0);
            foreach (FeatureDescriptor child in FeatureCatalog.Children(descriptor.Id))
            {
                AddFeatureRow(TabFeatureRows, child, 1);
            }
        }

        foreach (FeatureDescriptor descriptor in FeatureCatalog.All)
        {
            if (descriptor.Scope == FeatureScope.Chrome)
            {
                AddFeatureRow(ChromeFeatureRows, descriptor, 0);
            }
        }
    }

    private void AddFeatureRow(
        ObservableCollection<FeatureToggleRow> group, FeatureDescriptor descriptor, int indentLevel)
    {
        // The PLATFORM half of the answer, which the raw IFeatureGate does not know. See
        // FeatureToggleRow.IsPlatformUnavailable for why this matters on the browser head.
        //
        // Resolved through ShellModuleFeatureGate.DesktopOnlyIds itself rather than a second copy of the
        // list: that set is documented as "the ONE !OperatingSystem.IsBrowser() AND site for
        // module-facing ids", and a second answer to the same question is how this diverged in the first
        // place.
        bool platformUnavailable =
            _isBrowser() && ShellModuleFeatureGate.DesktopOnlyIds.Contains(descriptor.Id);

        FeatureToggleRow row = new(this, _gate, descriptor, indentLevel, platformUnavailable);
        group.Add(row);
        _featureRows.Add(row);
    }

    // Re-resolve every row's displayed state from the gate (each row guards its own write-echo), and re-raise
    // the derived header/count/label properties. Called at construction and on every IFeatureGate.Changed.
    private void RefreshFeatureRows()
    {
        if (_disposed)
        {
            return;
        }

        Dictionary<string, bool> overrides = _settings.Current.Features.Overrides;
        foreach (FeatureToggleRow row in _featureRows)
        {
            row.Refresh(_gate, overrides);
        }

        // The dedicated "Enable Live Sync" toggle mirrors the same gate decision as its generic FEATURES row,
        // so re-resolve it here (an override write, a category change, or an external edit all land through
        // gate.Changed → this sweep). Guarded so the reflected value is not persisted straight back.
        _applyingExternal = true;
        try
        {
            LiveSyncEnabled = _gate.IsEnabled("chrome.livesync");
        }
        finally
        {
            _applyingExternal = false;
        }

        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(FeatureCategoryLabel));
        OnPropertyChanged(nameof(FeaturesHeaderText));
        OnPropertyChanged(nameof(ResetButtonText));
    }

    // IFeatureGate.Changed handler. The gate marshals Changed to the UI thread in the headed app (and raises
    // it inline in unit tests), so the refresh runs on the right thread without marshaling here.
    private void OnGateChanged(object? sender, EventArgs e) => RefreshFeatureRows();

    private static string LabelFor(UserCategory category) => category switch
    {
        UserCategory.Consumer => "Consumer",
        UserCategory.PowerUser => "Power-User",
        UserCategory.Developer => "Developer",
        _ => category.ToString()
    };

    // Persist a folder-set change AND mirror it into the bound collection. The self-write echo is skipped
    // (_writing), so the collection would not otherwise refresh. It is refreshed here, under the external guard.
    private void ApplyFolders(string[] folders)
    {
        Persist(s => s.Library.Folders = folders);
        ReplaceFolders(folders);
    }

    private void ReplaceFolders(IReadOnlyList<string> folders)
    {
        _applyingExternal = true;
        try
        {
            LibraryFolders.Clear();
            foreach (string folder in folders)
            {
                LibraryFolders.Add(folder);
            }
        }
        finally
        {
            _applyingExternal = false;
        }
    }

    // Mutate + persist through the settings service, guarding the synchronous OnChange echo of its own write.
    private void Persist(Action<AppSettings> mutate)
    {
        if (_disposed)
        {
            return;
        }

        _writing = true;
        try
        {
            _settings.Write(mutate);
        }
        finally
        {
            _writing = false;
        }
    }

    // IOptionsMonitor.OnChange handler. Skips its own writes (the synchronous echo). An external change (the
    // file watcher) can arrive on a threadpool thread, so marshal to the UI thread before touching bound
    // state (per the SettingsService OnChange-threading contract).
    private void OnSettingsChanged(AppSettings settings)
    {
        if (_writing || _disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Reflect(settings);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Reflect(settings));
        }
    }

    // Push external settings into the bound properties WITHOUT persisting them back (the _applyingExternal
    // guard neuters the change-hooks). Idempotent: unchanged values short-circuit.
    private void Reflect(AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        _applyingExternal = true;
        try
        {
            SelectedCategoryOption = OptionFor(settings.UserCategory);
            SelectedTheme = ThemeFor(settings.Theme);
            // Live Sync section (the enable toggle is gate-driven, re-synced via RefreshFeatureRows).
            LiveSyncMockMode = settings.LiveSync.MockMode;
            Cs2InstallPath = settings.LiveSync.Cs2RootInstallationDirectory;
            ForceIncompatiblePlugin = settings.LiveSync.ForceIncompatiblePlugin;
            LiveSyncLogLevel = settings.LiveSync.MinimumLogLevel;
            LiveSyncCaptureFrameworkLogs = settings.LiveSync.CaptureFrameworkLogs;
            LiveSyncGameWindowWidth = settings.LiveSync.GameWindowWidth;
            LiveSyncGameWindowHeight = settings.LiveSync.GameWindowHeight;
            LiveSyncGameFullscreen = settings.LiveSync.GameFullscreen;
            LiveSyncTickOffset = settings.LiveSync.TickOffset;
            DiagnosticsLoggingEnabled = settings.Diagnostics.EnableInternalLogging;
            DiagnosticsLogLevel = settings.Diagnostics.MinimumLogLevel;
            DiagnosticsWriteLogFile = settings.Diagnostics.WriteLogFile;
            DiagnosticsMaxLogRows = settings.Diagnostics.MaxLogRows;
            DiagnosticsFileMaxSizeKb = settings.Diagnostics.FileMaxSizeKilobytes;
            DiagnosticsFileMaxCount = settings.Diagnostics.FileMaxCount;
            // Highlights section.
            HighlightsBackgroundScan = settings.Highlights.BackgroundScan;
            ReelOutputFolder = settings.Highlights.ReelOutputDirectory;
            ReelContainerFormat = settings.Highlights.ReelContainerFormat;
            ReelFps = settings.Highlights.ReelFps;
            ReelLeadInSeconds = settings.Highlights.ClipLeadInSeconds;
            ReelLeadOutSeconds = settings.Highlights.ClipLeadOutSeconds;
            ReelConcatenate = settings.Highlights.ReelConcatenate;
            ReelCaptureAudio = settings.Highlights.ReelCaptureAudio;
            ReelUseCrf = settings.Highlights.ReelBitrateKbps is null;
            ReelCrf = settings.Highlights.ReelCrf;
            ReelBitrateKbps = settings.Highlights.ReelBitrateKbps ?? 0;
            // Background processing section.
            BackgroundProcessingEnabled = settings.ProcessingQueue.BackgroundProcessingEnabled;
            MaxQueueSize = settings.ProcessingQueue.MaxQueueSize;
            MaxConcurrency = settings.ProcessingQueue.MaxConcurrency;
            // Idle section.
            IdleEnabled = settings.Idle.Enabled;
            IdleTimeoutMinutes = settings.Idle.IdleTimeoutWait.TotalMinutes;
            IdleKeepBackgroundProcessing = settings.Idle.KeepBackgroundProcessing;
        }
        finally
        {
            _applyingExternal = false;
        }

        string[] folders = settings.Library.Folders;
        if (!LibraryFolders.SequenceEqual(folders, StringComparer.Ordinal))
        {
            ReplaceFolders(folders);
        }

        // Outside the _applyingExternal block on purpose: the keybind rows carry no persisting
        // change-hook (their writes are explicit commands), so a refresh here can never echo.
        RefreshKeybindRows();
    }

    private CategoryOption OptionFor(UserCategory category)
    {
        foreach (CategoryOption option in Categories)
        {
            if (option.Value == category)
            {
                return option;
            }
        }

        return Categories[1]; // PowerUser: the default tier
    }

    // Refresh the bound theme list from the registry (built-ins + current user drop-ins). Called at
    // construction and on every ReloadThemes.
    private void RepopulateThemes()
    {
        Themes.Clear();
        foreach (Theme theme in _registry.Themes)
        {
            Themes.Add(theme);
        }
    }

    // Map a persisted theme id onto one of the offered Theme instances (case-insensitive, matching the
    // registry's id lookup, so a legacy capitalized "Dark"/"Light"/"System" still selects the right theme).
    // An unknown id falls back to the first theme (Dark) for DISPLAY only; it is never silently rewritten
    // (only an explicit selection or an external edit persists, guarded by _applyingExternal). Returns an
    // instance FROM the bound Themes list so the ComboBox SelectedItem matches by reference.
    private Theme ThemeFor(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
}

/// <summary>A selectable user-category card: the enum value plus its display title and one-line description.</summary>
public sealed record CategoryOption(UserCategory Value, string Title, string Description);
