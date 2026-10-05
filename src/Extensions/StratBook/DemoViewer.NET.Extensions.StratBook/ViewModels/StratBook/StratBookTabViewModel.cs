#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.ViewModels.UtilityBook;
using GrenadeKind = DemoViewer.NET.Modules.UtilityBook.GrenadeKind;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Strat Book tab: the book selector over Team Identity's teams plus <c>me</c>, the
///     map and side filters, the strat list from the store's index, and the editor over the one open
///     <see cref="StratSession" />.
///     <para>
///         <b>Commits follow the tab.</b> Leaving the tab, a demo swap, opening another strat, another book and
///         shutdown each commit the open strat's pending edits; the session's own timers cover the
///         30 s idle rule and the working copy between.
///     </para>
///     <para>
///         Delegate-injected (the Highlights precedent): the composition root supplies the store, Team Identity and
///         the UI-thread post; the VM references no shell. Team names are raw in the service and sanitized here, at
///         the render boundary, like every player name.
///     </para>
/// </summary>
public sealed partial class StratBookTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>The map filter's "no filter" entry.</summary>
    public const string AllMaps = "all maps";

    /// <summary>The side filter's "no filter" entry.</summary>
    public const string AllSides = "all sides";

    private readonly CalloutResolverSource _calloutResolvers;
    private readonly GrenadeIndex? _grenades;
    private readonly LineupOriginSource? _lineupOrigins;
    private readonly Func<string, IMapAsset?, UtilityBookTabViewModel>? _lineupMap;

    // What the open lineup picker writes to; it closes when either goes away.
    private (Guid Strat, Guid Step)? _pickerTarget;
    private readonly StratSpawnSource? _spawns;
    private bool _creating;

    // A New strat click that had no map: the template it asked for (null is blank) waits for the map pick.
    private bool _awaitingMap;
    private string? _awaitingTemplate;
    private readonly Action<Action> _post;
    private readonly StratStore _store;
    private readonly TeamIdentityService? _teams;

    private IModuleContext? _context;
    private bool _disposed;

    // Built on the first Export and kept: its chip is mounted once, and a finished export's result stays on it.
    private ExportJobService? _exportJob;

    // The shell's feature projection, captured at activation so deactivation unsubscribes the same instance.
    private IModuleFeatureGate? _features;

    /// <summary>The open lineup picker, or null. Non-null shows it over the tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLineupPicker))]
    private LineupPickerViewModel? _lineupPicker;

    /// <summary>The open export pane, or null. Non-null is what the view binds the pane's visibility to.</summary>
    [ObservableProperty]
    private Playback2DExportDialogViewModel? _exportDialog;
    private bool _refreshing;

    // What the callouts editor was last pointed at, so a RefreshList that changed nothing about the
    // book or map (the open strat's own half-second working-copy writes) does not reload it.
    private (StratOwner Owner, string Map)? _calloutsConfiguredFor;

    [ObservableProperty]
    private string _listLine = "";

    /// <summary>Shown beside New strat while a create waits for a map; empty otherwise.</summary>
    [ObservableProperty]
    private string _newStratHint = "";

    /// <summary>New strat had no map to use; the view opens the map filter for the pick that finishes the create.</summary>
    public event Action? MapChoiceRequested;

    /// <summary>True while the list pane shows the Detected inbox instead of the book.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBookList))]
    [NotifyPropertyChangedFor(nameof(CollapsedListLabel))]
    private bool _isDetectedView;

    [ObservableProperty]
    private string _selectedMap = AllMaps;

    [ObservableProperty]
    private StratOwnerOption? _selectedOwner;

    [ObservableProperty]
    private string _selectedSide = AllSides;

    [ObservableProperty]
    private StratListRow? _selectedStrat;

    /// <param name="store">The strat store.</param>
    /// <param name="teams">Team Identity, for the books; null offers the <c>me</c> book alone.</param>
    /// <param name="post">Marshals the session's timers onto the UI thread; defaults to synchronous.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    /// <param name="calloutResolvers">Builds a resolver over the store's aliases and a map's zones; one built from the store when omitted.</param>
    /// <param name="canvasMapLoader">Loads the canvas's map bundle by name; the baked assets when omitted.</param>
    /// <param name="tags">The Tag Store the Strat Record Panel reads and listens to; a session-only store when omitted.</param>
    /// <param name="evidence">Computes a strat's record from <paramref name="tags" />; built without Demo Provenance Labels when omitted.</param>
    /// <param name="review">Where the record panel's numbers send their clips; null says there is none on this host.</param>
    /// <param name="indexBySha">Hash to library row, for a clip's path (<see cref="DemoCacheStore.TryGetIndexBySha256" />).</param>
    /// <param name="selectTab">Shows a tab by id, for the Review tab after the record panel sends clips; null stays on the Strat Book.</param>
    /// <param name="grenades">
    ///     The Utility Book's Grenade Index (Lineup On A Strat Step): fills a step's lineup choices in the
    ///     editor and resolves a lineup reference to its title in Role View and LAN Print; null offers only
    ///     "none" and prints a raw id, the previous behaviour.
    /// </param>
    /// <param name="mining">Strat Mining, for the Detected inbox; null says the host has no demo cache.</param>
    /// <param name="playback">Opens a detected pattern's round in 2D Playback, resolved at click time.</param>
    /// <param name="spawns">Where a new blank strat's tokens start; null starts them unplaced.</param>
    /// <param name="layout">The collapsed panes, shared with the hub rail; a fresh one when omitted.</param>
    /// <param name="lineupMap">
    ///     Builds the lineup picker's Utility Book view model locked to a map, with the Utility Book's own queue
    ///     section and clip directory, drawing the bundle it is handed (the canvas's; it must not dispose it); one
    ///     over <paramref name="grenades" /> reading on the thread pool when omitted.
    /// </param>
    /// <param name="canvasPlaces">The canvas's zone loader; the app's zone source through the processing queue when omitted.</param>
    /// <param name="canvasRouting">Whether the canvas routes tokens; the <c>stratbook.routing</c> feature when omitted.</param>
    /// <param name="canvasServices">
    ///     The gate, zone resolver and settings the canvas (and the Detected preview's) fall back on when
    ///     <paramref name="canvasPlaces" /> / <paramref name="canvasRouting" /> are not supplied; null gives both
    ///     canvases nothing to fall back on.
    /// </param>
    public StratBookTabViewModel(StratStore store, TeamIdentityService? teams = null, Action<Action>? post = null, bool? isBrowser = null,
        CalloutResolverSource? calloutResolvers = null, Func<string?, IMapAsset?>? canvasMapLoader = null,
        TagStore? tags = null, StratEvidenceService? evidence = null, ReviewQueue? review = null,
        Func<string, DemoCacheIndexEntry?>? indexBySha = null, Func<string, bool>? selectTab = null,
        GrenadeIndex? grenades = null, StratMiningService? mining = null, Func<ISituationPlayback?>? playback = null,
        StratSpawnSource? spawns = null,
        StratBookLayout? layout = null, Func<string, IMapAsset?, UtilityBookTabViewModel>? lineupMap = null,
        Func<string, Task<IZonePlaceResolver?>>? canvasPlaces = null, Func<bool>? canvasRouting = null,
        StratCanvasServices? canvasServices = null)
    {
        _spawns = spawns;
        ArgumentNullException.ThrowIfNull(store);
        Layout = layout ?? new StratBookLayout();
        _store = store;
        _teams = teams;
        _post = post ?? (action => action());
        _calloutResolvers = calloutResolvers ?? new CalloutResolverSource(store);
        _grenades = grenades;
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        Session = new StratSession(store, _post, () => IsBrowser, () => DateTime.UtcNow);
        _lineupOrigins = grenades is null ? null : new LineupOriginSource(grenades, _post);
        _lineupMap = lineupMap ?? (grenades is null
            ? null
            : (map, asset) => new UtilityBookTabViewModel(grenades, loadMapAsset: _ => asset, lockedMap: map, ownsMapAsset: false,
                background: work => Task.Run(work), post: _post));
        // A new step carries a thrower at the origin the canvas shows, on the canvas's floors.
        Editor = new StratEditorViewModel(Session, _lineupOrigins is null ? null : new StratLineupCatalog(_lineupOrigins),
            _lineupOrigins is { } origins
                ? (map, utility) => origins.Resolve(map, utility, StratFromRound.FloorLevelKeys(Canvas?.MapAsset?.Floors))
                : null,
            (place, level) => Canvas?.CurrentPlaceCentres?.Invoke(place, level));
        if (_lineupOrigins is not null)
        {
            // A map not grouped yet answers null, so the validator keeps "not checked" until it is; For only
            // reads the cache and starts the grouping off the UI thread.
            Session.LineupLookup = map => _lineupOrigins.For(map) is { } lineups ? (_, id) => lineups.ById.ContainsKey(id) : null;
            _lineupOrigins.Changed += OnLineupsChanged;
        }

        Callouts = new CalloutsEditorViewModel(store, _calloutResolvers);

        // Strat Record Panel: run / won / aborted, split by Demo
        // Provenance Labels, the failure breakdown, every number a clip. A session-only Tag Store when
        // the host wired none, so the tab still renders (empty) rather than needing a fourth optional
        // to become required.
        TagStore recordTags = tags ?? new TagStore(null);
        StratEvidenceService recordEvidence = evidence ?? new StratEvidenceService(recordTags);
        RecordPanel = new StratRecordPanelViewModel(recordEvidence, recordTags, review, indexBySha, selectTab, _post);

        // Strat Version History: the append-only diff log as a pane,
        // the record split either side of each entry. Reads the same evidence and Tag Store as the
        // Record Panel above, so the two never disagree about what "run" or "won" means.
        HistoryPanel = new StratHistoryPanelViewModel(store, recordEvidence, recordTags, _calloutResolvers, _post);

        // Role View And LAN Print: one slot's parts on screen and the
        // button that prints all five as one HTML page. Print has nothing to write to on the browser head.
        RoleView = new StratRoleViewPanelViewModel(IsBrowser);

        // A branch into another strat plays that strat's steps read from the store; it is not checked out,
        // since the canvas does not write it.
        Canvas = new StratCanvasViewModel(Session, canvasMapLoader, lookup: id => _store.Load(id).Document, lineupOrigins: _lineupOrigins,
            placesFor: canvasPlaces, post: post, routing: canvasRouting, spawnsFor: _spawns is { } spawnSource ? spawnSource.ForAsync : null,
            lookups: canvasServices);
        Editor.Spawns = () => Canvas.CurrentSpawns;
        Editor.LegacyCarriedFor = Canvas.LegacyCarried;
        Editor.StartRefusal = Canvas.StartRefusal;
        StepSelection = new StratStepSelection(Editor, Canvas);
        Canvas.LinesShownApart = Editor.ShowsLinesApart;
        Editor.PlaceAt = Canvas.PlaceAt;
        Editor.PlacesKnown = () => Canvas.HasPlaces;
        Session.GeometryChecks = Canvas.DepartureChecks;
        Editor.LinesViewChanged += Canvas.RefreshSetPlace;

        // Place warnings from the canvas's loaded zones: none until they land, then a fresh validation.
        Session.PlaceLookup = map => Canvas.LoadedPlaces(map);
        Canvas.PlacesLoaded += OnPlacesLoaded;

        Detected = new DetectedStratsViewModel(mining, playback ?? (() => null),
            id => _teams?.AllTeams.FirstOrDefault(t => t.Id == id)?.Name, () => SelectedOwner?.Owner, ShowStratFromDetected, _post, canvasMapLoader, _lineupOrigins,
            canvasServices);
        Detected.PropertyChanged += OnDetectedChanged;

        Session.Changed += OnSessionChanged;
        _store.Changed += OnStoreChanged;
        if (_teams is not null)
        {
            _teams.Changed += RefreshOwners;
        }

        RefreshOwners();
    }

    /// <summary>The Strat Export feature id. A persisted key; desktop only.</summary>
    public const string ExportFeatureId = "stratbook.export";

    /// <summary>
    ///     The sizes the strat export offers: the 640 square first, then the 2D tab's presets. Square
    ///     because a radar is, so the map fills the frame and the text arithmetic at 640 px holds.
    /// </summary>
    public static IReadOnlyList<ExportSizeOption> ExportSizes { get; } =
    [
        new(string.Create(CultureInfo.InvariantCulture,
            $"GIF square ({StratExportJob.DefaultWidth}×{StratExportJob.DefaultHeight})"),
            StratExportJob.DefaultWidth, StratExportJob.DefaultHeight),
        .. Playback2DExportDialogViewModel.SizePresets
    ];

    /// <summary>The line the tab shows on the browser host.</summary>
    public static string BrowserNote => "session only: this browser tab forgets strats when it reloads";

    /// <summary>True on the WASM head.</summary>
    public bool IsBrowser { get; }

    /// <summary>The one live strat. Step Authoring's canvas edits through it too.</summary>
    public StratSession Session { get; }

    public StratEditorViewModel Editor { get; }

    /// <summary>Whether the strat list (and the hub rail) are collapsed; shared with the hub.</summary>
    public StratBookLayout Layout { get; }

    /// <summary>What the collapsed list's strip reads: the pane it hides.</summary>
    public string CollapsedListLabel => IsDetectedView ? "DETECTED" : "STRATS";

    /// <summary>The alias table editor for the selected book and map (Callout Aliases).</summary>
    public CalloutsEditorViewModel Callouts { get; }

    /// <summary>The Step Authoring canvas over the open strat.</summary>
    public StratCanvasViewModel Canvas { get; }

    /// <summary>The step the rows highlight and the canvas edits.</summary>
    public StratStepSelection StepSelection { get; }

    /// <summary>The book's list shows when it has strats and the Detected inbox is not up.</summary>
    public bool ShowBookList => HasStrats && !IsDetectedView;

    /// <summary>Strat Mining's inbox: repeated setups and executes found in the library.</summary>
    public DetectedStratsViewModel Detected { get; }

    /// <summary>The list toggle's second label, with how many new patterns the filters show.</summary>
    public string DetectedHeader => Detected.NewCount > 0 ? $"Detected ({Detected.NewCount})" : "Detected";

    /// <summary>The Strat Record Panel over the open strat.</summary>
    public StratRecordPanelViewModel RecordPanel { get; }

    /// <summary>Strat Version History over the open strat.</summary>
    public StratHistoryPanelViewModel HistoryPanel { get; }

    /// <summary>Role View And LAN Print over the open strat.</summary>
    public StratRoleViewPanelViewModel RoleView { get; }

    /// <summary>Every book: <c>me</c>, then each visible team.</summary>
    public ObservableCollection<StratOwnerOption> Owners { get; } = [];

    public ObservableCollection<string> MapOptions { get; } = [];

    public static IReadOnlyList<string> SideOptions { get; } = [AllSides, StratVocabulary.SideT, StratVocabulary.SideCt];

    public ObservableCollection<StratListRow> Strats { get; } = [];

    public bool HasStrats => Strats.Count > 0;

    public bool HasOpenStrat => Session.Document is not null;

    public bool CanUndo => Session.CanUndo;

    public bool CanRedo => Session.CanRedo;

    public bool HasPending => Session.HasPending;

    public string StatusLine => Session.StatusText;

    /// <summary>
    ///     True when the open strat can be exported: the feature is on, the shell wired a host (a desktop build),
    ///     and a strat is open. Re-raised wherever one of the three changes.
    /// </summary>
    public bool CanExport =>
        _context?.Features?.IsEnabled(ExportFeatureId) is not false &&
        _context?.GetService<IStratExport>() is not null &&
        HasOpenStrat;

    /// <summary>
    ///     What the Export button's slot says when the button cannot exist: on the browser, where there is no file
    ///     to write and no ffmpeg to drive. Empty everywhere else, including when the user switched the feature off.
    /// </summary>
    public string ExportUnavailableNote => IsBrowser ? "Export strat: unavailable in the browser" : "";

    /// <summary>Whether <see cref="ExportUnavailableNote" /> has something to say.</summary>
    public bool HasExportUnavailableNote => ExportUnavailableNote.Length > 0;

    /// <summary>The export's chip, or null before the first Export. Internal so a test reaches it as the shell does.</summary>
    internal Playback2DExportStatusViewModel? ExportStatus { get; private set; }

    /// <summary>
    ///     Makes the export job. The production job when unset; a test swaps in one with a fixed ffmpeg answer.
    /// </summary>
    internal Func<IStratExport, StratExportJob>? ExportJobFactory { get; set; }

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null)
        {
            _context.DemoReset -= OnDemoReset;
        }

        _context = context;
        _context.DemoReset += OnDemoReset;

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
        }

        _features = context.Features;
        if (_features is not null)
        {
            _features.Changed += OnFeaturesChanged;
        }

        RaiseExportState();

        // The open demo's map is the likely one to author for; a filter the user chose is kept.
        if (SelectedMap == AllMaps && !string.IsNullOrEmpty(context.MapName))
        {
            RefreshMaps(context.MapName);
            SelectedMap = context.MapName.ToLowerInvariant();
        }

        // A rebind made in Settings while the tab was hidden reaches the canvas's keys here.
        Canvas.RefreshKeymap();
        RefreshList();
    }

    /// <inheritdoc />
    public void OnDeactivated()
    {
        CancelLineupPicker();
        CancelMapChoice();
        if (_context is not null)
        {
            _context.DemoReset -= OnDemoReset;
            _context = null;
        }

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
            _features = null;
        }

        Canvas.Transport.Pause();
        Detected.Preview?.Canvas?.Transport.Pause();
        Session.Commit();
        RaiseExportState();
    }

    /// <summary>
    ///     Shutdown, called by the shell: commits the open strat, leaves anything the store refused in the working
    ///     copy for the next session, and writes the deferred index.
    /// </summary>
    public void Shutdown()
    {
        Session.Close();
        _store.SaveIndex();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context is not null)
        {
            _context.DemoReset -= OnDemoReset;
        }

        if (_teams is not null)
        {
            _teams.Changed -= RefreshOwners;
        }

        Detected.PropertyChanged -= OnDetectedChanged;
        Detected.Dispose();

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
        }

        CloseExport();
        ExportStatus?.Dispose();
        _exportJob?.Dispose();
        _store.Changed -= OnStoreChanged;
        Session.Changed -= OnSessionChanged;
        StepSelection.Dispose();
        Canvas.Dispose();
        CancelLineupPicker();
        if (_lineupOrigins is not null)
        {
            _lineupOrigins.Changed -= OnLineupsChanged;
            _lineupOrigins.Dispose();
        }

        Canvas.PlacesLoaded -= OnPlacesLoaded;
        Session.Dispose();
        RecordPanel.Dispose();
        HistoryPanel.Dispose();
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>True while the lineup picker is open.</summary>
    public bool HasLineupPicker => LineupPicker is not null;

    /// <summary>Whether a step can pick its lineup on the map: this host has a Grenade Index.</summary>
    public bool CanPickLineups => _lineupMap is not null;

    /// <summary>
    ///     Opens the lineup picker for a throw step on the strat's map, with the step's lineup selected when it has
    ///     one (an alias id resolved). Confirm writes kind, lineup and technique as one undo entry.
    /// </summary>
    /// <summary>A location field's pick button: the next map click writes that field; again, it cancels.</summary>
    /// <param name="field">The field.</param>
    [RelayCommand]
    private void PickOnMap(StratLocationField? field)
    {
        if (field is not null)
        {
            StepSelection.PickOnMap(field);
        }
    }

    /// <summary>A placed chip's click: its step and its player's line become the selection, at the step's time.</summary>
    /// <param name="chip">The chip.</param>
    [RelayCommand]
    private void SelectPlaced(StratPlacedChip? chip)
    {
        if (chip is not null && Editor.Steps.FirstOrDefault(r => r.Placed.Contains(chip)) is { } row)
        {
            StepSelection.SelectLine(row.Id, StratVocabulary.Slots.Contains(chip.Slot) ? chip.Slot : null);
        }
    }

    [RelayCommand]
    private void OpenLineupPicker(StratStepRow? row)
    {
        if (row is null || _lineupMap is null || Session.Document is not { } document)
        {
            return;
        }

        CancelLineupPicker();
        Editor.EndEditBurst();
        StratStep? step = document.Steps.FirstOrDefault(s => s.Id == row.Id);
        StratLineupChoice? current = step?.Utility?.LineupId is { } id ? Editor.ResolveLineup(id) : null;
        Guid stepId = row.Id;
        LineupPickerViewModel? picker = null;
        using (QueueWork.UserAction())
        {
            // The canvas holds this map's bundle already: the picker draws it rather than decoding another.
            picker = new LineupPickerViewModel(_lineupMap(document.Map, Canvas.MapAsset), step?.Utility?.Kind ?? StratEditorViewModel.None, current,
                step?.Utility?.Technique, pick =>
                {
                    if (ReferenceEquals(LineupPicker, picker))
                    {
                        LineupPicker = null;
                        _pickerTarget = null;
                    }

                    if (pick is not null)
                    {
                        Editor.ApplyLineupPick(stepId, pick.UtilityKind, pick.LineupId, pick.Technique);
                    }
                });
        }

        _pickerTarget = (document.Id, stepId);
        LineupPicker = picker;
    }

    private void CancelLineupPicker() => LineupPicker?.CancelCommand.Execute(null);

    /// <summary>
    ///     Opens the export pane over the open strat: GIF at 20 fps, 640 square, the first
    ///     step to the last plus 2 s. The job is the 2D export's service over a <see cref="StratExportJob" />, so
    ///     the gate, the interlocks, the chip and cancel are the same ones.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void OpenExport()
    {
        if (!CanExport || _context?.GetService<IStratExport>() is not { } host
                       || Canvas.Projection is not { } projection || Session.Document is not { } document)
        {
            return;
        }

        if (_exportJob is null)
        {
            // Every seam named, the 2D tab's rule: an omitted optional is invisible at the call site.
            StratExportJob job = ExportJobFactory?.Invoke(host) ?? new StratExportJob(
                Canvas.MapLoader,
                surfaces: RenderSurfaceProviderFactory.CreateCpu,
                managedFfmpegDirectory: static () => FfmpegDependency.ManagedDirectory,
                log: AppendExportLog,
                encoderProbe: EncoderProbeCache.Shared,
                locateFfmpeg: null);
            _exportJob = new ExportJobService(job, host.Gate, host.IsLiveSyncBusy, host.IsReelRunning,
                AppendExportLog);
            ExportStatus = new Playback2DExportStatusViewModel(_exportJob, host.OpenExportFolder);
            host.MountStatusChip?.Invoke(ExportStatus);
        }

        // The strat's own defaults, not the 2D tab's saved ones: a strat is shared as a short GIF, and
        // choosing here must not rewrite what the 2D tab opens with. Only the folder is shared.
        Playback2DSettings saved = host.Settings().Playback2D;
        Playback2DSettings seed = new()
        {
            ExportFormatId = ExportFormats.Gif,
            ExportFps = StratExportJob.DefaultFps,
            ExportWidth = StratExportJob.DefaultWidth,
            ExportHeight = StratExportJob.DefaultHeight,
            ExportOutputDirectory = saved.ExportOutputDirectory,
            ExportIncludeHud = true,
            ExportIncludeHudClock = true,
            ExportIncludeAnnotations = true,
            ExportIncludeVision = false,
            ExportQuality = saved.ExportQuality,
            ExportEncoder = EncoderLadder.Auto
        };

        CloseExport();
        ExportDialog = new Playback2DExportDialogViewModel(
            StratExportJob.Ranges(projection),
            seed,
            _exportJob,

            // An empty fixed script: the session's first-frame fit frames the map's bounds, which is the
            // strat's camera. There is no live pan to mirror.
            captureLiveCamera: null,
            outputFrameCount: StratFrameSource.OutputFrameCount,
            ffmpegLocator: static () => FfmpegLocator.Locate(FfmpegDependency.ManagedDirectory),
            isLiveSyncSessionActive: host.IsLiveSyncBusy,

            // The folder is written at Start below; the rest are the strat's constants, never the user's 2D ones.
            persistDefaults: null,
            fileExists: null,
            captureInk: CaptureExport,
            acquireFfmpeg: Playback2DExportDialogViewModel.ProductionAcquisition(FfmpegDependency.ManagedDirectory),
            capturePalette: CaptureExportPalette,
            scene: new ExportDialogScene("Export strat", ExportFileStem(document.Name), ExportSizes,
                StratExportJob.LayerIds));

        ExportDialog.StartRequested += OnExportStarted;
    }

    /// <summary>Closes the export pane. A started export keeps running; its progress is on the chip.</summary>
    [RelayCommand]
    private void CloseExport()
    {
        if (ExportDialog is { } dialog)
        {
            dialog.StartRequested -= OnExportStarted;
            dialog.Dispose();
        }

        ExportDialog = null;
    }

    private void OnExportStarted()
    {
        if (ExportDialog is { } dialog && _context?.GetService<IStratExport>() is { } host
                                       && Path.GetDirectoryName(dialog.OutputPath) is { Length: > 0 } folder)
        {
            host.PersistSettings(settings => settings.Playback2D.ExportOutputDirectory = folder);
        }

        CloseExport();
    }

    // At Start, on the UI thread: the canvas's tracks and ink as they are now, keyed onto the ink session the
    // request carries. Null with no strat open, which the job refuses.
    private AnnotationSession? CaptureExport() =>
        Canvas.CaptureForExport() is { } capture ? StratExportJob.Register(capture) : null;

    // Resolved at Start for the 2D export's reason: the theme is only readable on the UI thread.
    private static ScenePalette CaptureExportPalette() =>
        Dispatcher.UIThread.CheckAccess()
            ? ScenePaletteFactory.Build(Application.Current?.ActualThemeVariant)
            : ScenePalette.Dark;

    // From the export's pool thread and ffmpeg's stderr pump; the chip marshals.
    private void AppendExportLog(string line) => ExportStatus?.AppendLog(line);

    private void OnFeaturesChanged()
    {
        RaiseExportState();
        if (!CanExport)
        {
            CloseExport();
        }
    }

    private void RaiseExportState()
    {
        OnPropertyChanged(nameof(CanExport));
        OpenExportCommand.NotifyCanExecuteChanged();
    }

    // The strat's name as a file name: characters no file system takes become '-', and an unnamed strat is "strat".
    internal static string ExportFileStem(string? name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string stem = new((name ?? "").Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return stem.Length == 0 ? "strat" : stem;
    }

    /// <summary>
    ///     A new strat in the selected book, on the filtered map (else the open demo's), on the filtered side (else
    ///     T), committed as revision 1 and opened. With neither map it waits for the next map pick. Disabled while
    ///     a cold map's spawns load.
    /// </summary>
    /// <param name="templateId">
    ///     A <see cref="StratTemplates" /> id, or null for a blank strat. The template sets the type, and the side when
    ///     it has one; its steps follow the spawn start.
    /// </param>
    [RelayCommand(CanExecute = nameof(CanNewStrat))]
    private void NewStrat(string? templateId)
    {
        if (_creating || SelectedOwner is not { } owner)
        {
            return;
        }

        StratTemplate? template = StratTemplates.Find(templateId);
        if (templateId is not null && template is null)
        {
            return;
        }

        string? map = SelectedMap != AllMaps ? SelectedMap : _context?.MapName;
        if (string.IsNullOrWhiteSpace(map))
        {
            _awaitingMap = true;
            _awaitingTemplate = templateId;
            NewStratHint = "choose a map for the new strat";
            MapChoiceRequested?.Invoke();
            return;
        }

        CancelMapChoice();

        string side = SelectedSide == StratVocabulary.SideCt ? StratVocabulary.SideCt : StratVocabulary.SideT;
        if (template is not null)
        {
            side = StratTemplates.SideFor(template, side);
        }

        if (_spawns is null)
        {
            CreateNew(owner.Owner, map, side, null, template, true);
            return;
        }

        // The zones read is file IO: a cold map finishes the create when the read lands.
        Task<StratSpawns?> spawns = _spawns.ForAsync(map);
        if (spawns.IsCompleted)
        {
            CreateNew(owner.Owner, map, side, spawns.IsCompletedSuccessfully ? spawns.Result : null, template, true);
            return;
        }

        SetCreating(true);
        (StratOwnerOption Owner, string Map, string Side, Guid? Open) atClick = (owner, SelectedMap, SelectedSide, Session.Document?.Id);
        spawns.ContinueWith(t => _post(() =>
        {
            SetCreating(false);
            if (_disposed)
            {
                return;
            }

            // Opened only if the user is still where they clicked; otherwise it is made but the open strat stays.
            bool stillHere = SelectedOwner == atClick.Owner && SelectedMap == atClick.Map && SelectedSide == atClick.Side
                             && Session.Document?.Id == atClick.Open;
            CreateNew(owner.Owner, map, side, t.IsCompletedSuccessfully ? t.Result : null, template, stillHere);
        }), TaskScheduler.Default);
    }

    private bool CanNewStrat(string? templateId) => !_creating;

    /// <summary>Drops a create that was waiting for a map, and its hint.</summary>
    public void CancelMapChoice()
    {
        _awaitingMap = false;
        _awaitingTemplate = null;
        NewStratHint = "";
    }

    private void SetCreating(bool creating)
    {
        _creating = creating;
        NewStratCommand.NotifyCanExecuteChanged();
    }

    private void CreateNew(StratOwner owner, string map, string side, StratSpawns? spawns, StratTemplate? template, bool open)
    {
        string type = template?.Type ?? (side == StratVocabulary.SideCt ? "setup" : "default");
        StratDocument created = _store.Create(owner, map, side, type, template?.Name ?? "New strat",
            document =>
            {
                spawns?.PlaceStart(document);
                if (template is not null)
                {
                    StratTemplates.Apply(document, template);
                }
            });
        if (created.Revision == 0)
        {
            ListLine = "strat could not be saved";
            return;
        }

        RefreshList();
        if (open)
        {
            SelectedStrat = Strats.FirstOrDefault(r => r.Id == created.Id);
            if (SelectedStrat?.Id != created.Id)
            {
                // A template's side can fall outside the side filter.
                OpenStrat(created.Id);
            }
        }
        else
        {
            ListLine = string.Create(CultureInfo.InvariantCulture, $"a new strat was added on {map}");
        }
    }

    /// <summary>
    ///     Opens a strat by id with its book and map as the filters, the way Create Strat From Round lands the user
    ///     on what it just made. A strat the index does not know, or whose book is not offered, is left alone.
    /// </summary>
    /// <param name="id">The strat.</param>
    public void OpenStrat(Guid id)
    {
        if (_store.Index.FirstOrDefault(e => e.Id == id) is not { } entry
            || Owners.FirstOrDefault(o => o.Owner.Equals(entry.Owner)) is not { } owner)
        {
            return;
        }

        _refreshing = true;
        try
        {
            SelectedOwner = owner;
            RefreshMaps(entry.Map);
            SelectedMap = entry.Map;
            SelectedSide = AllSides;
        }
        finally
        {
            _refreshing = false;
        }

        RefreshList();
        SelectedStrat = Strats.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Moves the selected strat to its folder's <c>.trash/</c>, committing its edits first.</summary>
    [RelayCommand]
    private void DeleteStrat()
    {
        if (SelectedStrat is not { } row)
        {
            return;
        }

        if (Session.Document?.Id == row.Id)
        {
            Session.Close();
        }

        SelectedStrat = null;
        if (!_store.Delete(row.Id))
        {
            ListLine = "strat could not be deleted";
        }

        RefreshList();
    }

    /// <summary>The explicit save: a commit of the pending edits.</summary>
    [RelayCommand]
    private void Save() => Session.Commit();

    /// <summary>The templates the open strat can take: its side's, and none once it has steps.</summary>
    public IReadOnlyList<StratTemplate> ApplicableTemplates =>
        Session.Document is { } document && StratTemplates.HasNoSteps(document)
            ? [.. StratTemplates.Templates.Where(t => t.AppliesTo(document.Side))]
            : [];

    public bool CanApplyTemplate => ApplicableTemplates.Count > 0;

    /// <summary>Fills the open strat from a template as one undo entry. Refused once it has steps.</summary>
    /// <param name="templateId">A <see cref="StratTemplates" /> id.</param>
    [RelayCommand(CanExecute = nameof(CanApplyTemplateId))]
    private void ApplyTemplate(string? templateId)
    {
        if (Session.Document is not { } document || StratTemplates.Find(templateId) is not { } template)
        {
            return;
        }

        IReadOnlyList<PatchOp> ops = StratTemplates.Ops(document, template);
        if (ops.Count == 0)
        {
            ListLine = "a template fills only a strat with no steps";
            return;
        }

        Session.Apply(ops);
    }

    private bool CanApplyTemplateId(string? templateId) =>
        StratTemplates.Find(templateId) is { } template && ApplicableTemplates.Contains(template);

    [RelayCommand]
    private void Undo() => Session.Undo();

    [RelayCommand]
    private void Redo() => Session.Redo();

    // ── Selection ────────────────────────────────────────────────────────────────────────────────

    partial void OnIsDetectedViewChanged(bool value)
    {
        if (value)
        {
            Detected.EnsureMined();
        }
        else
        {
            Detected.ClosePreview();
        }
    }

    [RelayCommand]
    private void ShowBook() => IsDetectedView = false;

    [RelayCommand]
    private void ShowDetected() => IsDetectedView = true;

    private void OnDetectedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetectedStratsViewModel.NewCount))
        {
            OnPropertyChanged(nameof(DetectedHeader));
        }
    }

    // A pattern just added to the book: back to the book with it open.
    private void ShowStratFromDetected(Guid id)
    {
        IsDetectedView = false;
        RefreshList();
        SelectedStrat = Strats.FirstOrDefault(r => r.Id == id) ?? SelectedStrat;
    }

    partial void OnSelectedOwnerChanged(StratOwnerOption? value)
    {
        if (_refreshing)
        {
            return;
        }

        // Another book: the open strat is not in it.
        CancelMapChoice();
        SelectedStrat = null;
        RefreshList();
    }

    partial void OnSelectedMapChanged(string value)
    {
        if (value != AllMaps)
        {
            _spawns?.Warm(value);
        }

        if (_refreshing)
        {
            return;
        }

        RefreshList();
        if (_awaitingMap && value != AllMaps)
        {
            string? template = _awaitingTemplate;
            CancelMapChoice();
            NewStrat(template);
        }
    }

    partial void OnSelectedSideChanged(string value)
    {
        if (!_refreshing)
        {
            RefreshList();
        }
    }

    partial void OnSelectedStratChanged(StratListRow? value)
    {
        if (_refreshing || value?.Id == Session.Document?.Id)
        {
            return;
        }

        if (value is null)
        {
            Session.Close();
            return;
        }

        StratLoadResult result = Session.Open(value.Id);
        if (result.Document is null)
        {
            ListLine = "that strat could not be opened";
            return;
        }

        ConfigureEditor();
    }

    // ── Projection ───────────────────────────────────────────────────────────────────────────────

    private void OnDemoReset() => Session.Commit();

    // A map finished grouping: lineup ids there can now be checked, and the rows can name them.
    private void OnPlacesLoaded()
    {
        Session.InvalidateIssues();
        Editor.Project();
    }

    private void OnLineupsChanged()
    {
        Session.InvalidateIssues();
        Editor.Project();
    }

    private void OnSessionChanged()
    {
        // Another strat, none, or the picker's step deleted: there is nothing left for it to write to.
        if (_pickerTarget is { } target
            && (Session.Document is not { } open || open.Id != target.Strat || open.Steps.All(st => st.Id != target.Step)))
        {
            CancelLineupPicker();
        }

        Editor.Project();
        RecordPanel.Configure(Session.Document);
        HistoryPanel.Configure(Session.Document);
        RoleView.Configure(Session.Document,
            Session.Document is { } doc ? _calloutResolvers.For(doc.Owner, doc.Map) : null,
            RosterFromEditor(), id => _store.Load(id).Document, _grenades is null ? null : LineupTitle);
        OnPropertyChanged(nameof(HasOpenStrat));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(ApplicableTemplates));
        OnPropertyChanged(nameof(CanApplyTemplate));
        ApplyTemplateCommand.NotifyCanExecuteChanged();
        RaiseExportState();
    }

    // The open strat's own working-copy writes land here every half second while it is edited; they change
    // its list row and nothing the editor offers, so only another strat's change reconfigures the editor.
    private void OnStoreChanged(Guid? id) => RefreshList(id is null || id != Session.Document?.Id);

    // Books are "me" plus every visible team, "us" first among the teams; a book whose team went away is
    // dropped and the selection falls back to the default, "us" when a team is marked, else "me".
    private void RefreshOwners()
    {
        StratOwner? keep = SelectedOwner?.Owner;
        List<StratOwnerOption> options = [new(StratOwner.Me(), "me")];
        if (_teams is not null)
        {
            options.AddRange(_teams.Teams
                .OrderByDescending(t => t.IsUs)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new StratOwnerOption(StratOwner.Team(t.Id), DisplayText.Sanitize(t.Name) + (t.IsUs ? " (us)" : ""))));
        }

        _refreshing = true;
        try
        {
            Owners.Clear();
            foreach (StratOwnerOption option in options)
            {
                Owners.Add(option);
            }

            StratOwner fallback = _teams?.Us is { } us ? StratOwner.Team(us.Id) : StratOwner.Me();
            SelectedOwner = Owners.FirstOrDefault(o => keep is not null && o.Owner.Equals(keep))
                            ?? Owners.FirstOrDefault(o => o.Owner.Equals(fallback))
                            ?? Owners[0];
        }
        finally
        {
            _refreshing = false;
        }

        if (Session.Document is { } open && !open.Owner.Equals(SelectedOwner?.Owner))
        {
            SelectedStrat = null;
        }

        RefreshList();
    }

    private void RefreshMaps(string? extra)
    {
        SortedSet<string> maps = new(StringComparer.Ordinal);
        maps.UnionWith(CanonicalPlaces.Maps);
        maps.UnionWith(_store.Index.Select(e => e.Map));
        if (!string.IsNullOrEmpty(extra))
        {
            maps.Add(extra.ToLowerInvariant());
        }

        List<string> options = [AllMaps, .. maps];
        if (MapOptions.SequenceEqual(options))
        {
            return;
        }

        string keep = SelectedMap;
        _refreshing = true;
        try
        {
            MapOptions.Clear();
            foreach (string option in options)
            {
                MapOptions.Add(option);
            }

            SelectedMap = MapOptions.Contains(keep) ? keep : AllMaps;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshList(bool configureEditor = true)
    {
        if (_disposed)
        {
            return;
        }

        RefreshMaps(_context?.MapName);
        Detected.SetFilter(SelectedMap == AllMaps ? null : SelectedMap, SelectedSide == AllSides ? null : SelectedSide, SelectedOwner?.Owner);
        Guid? keep = SelectedStrat?.Id ?? Session.Document?.Id;
        IReadOnlyList<StratIndexEntry> rows = SelectedOwner is { } owner
            ? _store.Query(owner.Owner, SelectedMap == AllMaps ? null : SelectedMap, SelectedSide == AllSides ? null : SelectedSide, null)
            : [];

        _refreshing = true;
        try
        {
            Strats.Clear();
            foreach (StratIndexEntry row in rows)
            {
                Strats.Add(new StratListRow(row));
            }

            SelectedStrat = keep is { } id ? Strats.FirstOrDefault(r => r.Id == id) : null;
        }
        finally
        {
            _refreshing = false;
        }

        ListLine = rows.Count == 0
            ? SelectedOwner is null ? "" : "no strats in this book yet"
            : string.Create(CultureInfo.InvariantCulture, $"{rows.Count} strat{(rows.Count == 1 ? "" : "s")}");
        OnPropertyChanged(nameof(HasStrats));
        OnPropertyChanged(nameof(ShowBookList));

        // The open strat's branch targets are the book's strats on its map, which a commit elsewhere changes.
        if (configureEditor && Session.Document is not null)
        {
            ConfigureEditor();
        }

        RefreshCallouts();
    }

    // The callouts editor follows the book selector, not the open strat: aliases are per owner per map,
    // so "all maps" or no book leaves it with nothing to edit. Skipped when nothing it reads changed,
    // since RefreshList also runs on the open strat's own half-second working-copy writes and a table
    // reload mid-edit would drop the "copy from" selection out from under the user for no reason.
    private void RefreshCallouts()
    {
        (StratOwner Owner, string Map) target = SelectedOwner is { } book && SelectedMap != AllMaps
            ? (book.Owner, SelectedMap)
            : (StratOwner.Me(), "");

        if (_calloutsConfiguredFor is { } current && current.Owner.Equals(target.Owner)
            && string.Equals(current.Map, target.Map, StringComparison.Ordinal) && Callouts.CopySources.Count == Owners.Count - 1)
        {
            return;
        }

        _calloutsConfiguredFor = target;
        Callouts.Configure(target.Owner, target.Map, Owners);
    }

    private void ConfigureEditor()
    {
        if (Session.Document is not { } document)
        {
            return;
        }

        CalloutResolver places = _calloutResolvers.For(document.Owner, document.Map);
        List<StratTargetOption> targets =
        [
            .. _store.Query(document.Owner, document.Map, null, null)
                .Select(e => new StratTargetOption(e.Id, e.Id == document.Id ? "this strat" : e.Name))
        ];
        Editor.Configure(places, PinsFor(document.Owner), targets);
        Canvas.SetCallouts(places);
    }

    // Role View's roster, strat-pin level only: a slot the editor shows pinned to a real person,
    // not "book default" (that resolves no further here, so the sheet prints the blank line to write on,
    // same as an unpinned slot).
    private Dictionary<string, string?> RosterFromEditor() =>
        Editor.Slots.Where(s => s.Pin?.SteamId is not null)
            .ToDictionary(s => s.Letter, s => (string?)s.Pin!.Label);

    // Raw-id fallback (RoleSheet's own convention): the open strat's map picks the corpus to search, so a
    // strat with no document open (nothing to resolve against) leaves the id as its own text.
    private string? LineupTitle(Guid lineupId) =>
        Session.Document is { } document ? _grenades?.DescribeLineup(document.Map, lineupId)?.Title : null;

    // The latest roster's members for a team, by last-seen name; the confirmed me accounts for the me book.
    private List<StratPinOption> PinsFor(StratOwner owner)
    {
        if (_teams is null)
        {
            return [];
        }

        if (!owner.IsTeam)
        {
            return [.. _teams.MyAccounts.Select(id => new StratPinOption(id, id))];
        }

        Team? team = _teams.AllTeams.FirstOrDefault(t => t.Id == owner.TeamId);
        Roster? latest = team?.Rosters.OrderBy(r => r.Since).LastOrDefault();
        if (team is null || latest is null)
        {
            return [];
        }

        return
        [
            .. _teams.MembersOf(team.Id, latest.Id)
                .OrderByDescending(m => m.Value.Count)
                .ThenBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => new StratPinOption(m.Key, DisplayText.Sanitize(m.Value.LastName)))
        ];
    }
}

/// <summary>A book the selector offers.</summary>
public sealed record StratOwnerOption(StratOwner Owner, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One strat in the list, from its index row.</summary>
public sealed class StratListRow(StratIndexEntry entry)
{
    public Guid Id { get; } = entry.Id;

    public string Name { get; } = entry.Name;

    public string Summary { get; } = string.Create(CultureInfo.InvariantCulture,
        $"{entry.Map} · {entry.Side} · {entry.Type}{(entry.TargetSite is null ? "" : " " + entry.TargetSite)} · {entry.Status} · {entry.StepCount} step{(entry.StepCount == 1 ? "" : "s")} · rev {entry.Revision}");
}
