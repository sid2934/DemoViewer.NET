#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Strat Book tab (strat-model.md §3.11): the book selector over Team Identity's teams plus <c>me</c>, the
///     map and side filters, the strat list from the store's index, and the editor over the one open
///     <see cref="StratSession" />.
///     <para>
///         <b>Commits follow the tab.</b> Leaving the tab, a demo swap, opening another strat, another book and
///         shutdown each commit the open strat's pending edits (§3.8's triggers); the session's own timers cover the
///         30 s idle rule and the working copy between.
///     </para>
///     <para>
///         Delegate-injected (the Highlights precedent): the composition root supplies the store, Team Identity and
///         the UI-thread post; the VM references no shell. Team names are raw in the service and sanitized here, at
///         the render boundary, like every player name.
///     </para>
/// </summary>
public sealed partial class StratBookTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>The map filter's "no filter" entry.</summary>
    public const string AllMaps = "all maps";

    /// <summary>The side filter's "no filter" entry.</summary>
    public const string AllSides = "all sides";

    private readonly CalloutResolverSource _calloutResolvers;
    private readonly Action<Action> _post;
    private readonly StratStore _store;
    private readonly TeamIdentityService? _teams;

    private IModuleContext? _context;
    private bool _disposed;

    // Built on the first Export and kept: its chip is mounted once, and a finished export's result stays on it.
    private ExportJobService? _exportJob;

    // The shell's feature projection, captured at activation so deactivation unsubscribes the same instance.
    private IModuleFeatureGate? _features;

    /// <summary>The open export pane, or null. Non-null is what the view binds the pane's visibility to.</summary>
    [ObservableProperty]
    private Playback2DExportDialogViewModel? _exportDialog;
    private bool _refreshing;

    // What the callouts editor was last pointed at, so a RefreshList that changed nothing about the
    // book or map (the open strat's own half-second working-copy writes) does not reload it.
    private (StratOwner Owner, string Map)? _calloutsConfiguredFor;

    [ObservableProperty]
    private string _listLine = "";

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
    public StratBookTabViewModel(StratStore store, TeamIdentityService? teams = null, Action<Action>? post = null, bool? isBrowser = null,
        CalloutResolverSource? calloutResolvers = null, Func<string?, LoadedMapAsset?>? canvasMapLoader = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _teams = teams;
        _post = post ?? (action => action());
        _calloutResolvers = calloutResolvers ?? new CalloutResolverSource(store);
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        Session = new StratSession(store, _post, () => IsBrowser, () => DateTime.UtcNow);
        Editor = new StratEditorViewModel(Session);
        Callouts = new CalloutsEditorViewModel(store, _calloutResolvers);

        // A branch into another strat plays that strat's steps read from the store; it is not checked out,
        // since the canvas does not write it.
        Canvas = new StratCanvasViewModel(Session, canvasMapLoader, lookup: id => _store.Load(id).Document);

        Session.Changed += OnSessionChanged;
        _store.Changed += OnStoreChanged;
        if (_teams is not null)
        {
            _teams.Changed += RefreshOwners;
        }

        RefreshOwners();
    }

    /// <summary>The Strat Export feature id (step-authoring.md §3.10). A persisted key; desktop only.</summary>
    public const string ExportFeatureId = "stratbook.export";

    /// <summary>
    ///     The sizes the strat export offers: the 640 square first (O-29), then the 2D tab's presets. Square
    ///     because a radar is, so the map fills the frame and §6's text arithmetic at 640 px holds.
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

    /// <summary>The one live strat. Step Authoring's canvas edits through it too (overview correction 18).</summary>
    public StratSession Session { get; }

    public StratEditorViewModel Editor { get; }

    /// <summary>The alias table editor for the selected book and map (Callout Aliases, strat-model.md §3.7).</summary>
    public CalloutsEditorViewModel Callouts { get; }

    /// <summary>The Step Authoring canvas over the open strat (step-authoring.md §3.10).</summary>
    public StratCanvasViewModel Canvas { get; }

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
        _context is ModuleContext { StratExportHost: not null } &&
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
    internal Func<StratExportHost, StratExportJob>? ExportJobFactory { get; set; }

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

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
        }

        CloseExport();
        ExportStatus?.Dispose();
        _exportJob?.Dispose();
        _store.Changed -= OnStoreChanged;
        Session.Changed -= OnSessionChanged;
        Canvas.Dispose();
        Session.Dispose();
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Opens the export pane over the open strat (step-authoring.md §3.6): GIF at 20 fps, 640 square, the first
    ///     step to the last plus 2 s. The job is the 2D export's service over a <see cref="StratExportJob" />, so
    ///     the gate, the interlocks, the chip and cancel are the same ones.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void OpenExport()
    {
        if (!CanExport || _context is not ModuleContext { StratExportHost: { } host }
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

        // The strat's own defaults, not the 2D tab's saved ones: a strat is shared as a short GIF (O-29), and
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
            // strat's camera (§3.6). There is no live pan to mirror.
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
        if (ExportDialog is { } dialog && _context is ModuleContext { StratExportHost: { } host }
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
    ///     T), committed as revision 1 and opened.
    /// </summary>
    [RelayCommand]
    private void NewStrat()
    {
        if (SelectedOwner is not { } owner)
        {
            return;
        }

        string? map = SelectedMap != AllMaps ? SelectedMap : _context?.MapName;
        if (string.IsNullOrWhiteSpace(map))
        {
            ListLine = "choose a map for the new strat";
            return;
        }

        string side = SelectedSide == StratVocabulary.SideCt ? StratVocabulary.SideCt : StratVocabulary.SideT;
        StratDocument created = _store.Create(owner.Owner, map, side, side == StratVocabulary.SideCt ? "setup" : "default", "New strat");
        if (created.Revision == 0)
        {
            ListLine = "strat could not be saved";
            return;
        }

        RefreshList();
        SelectedStrat = Strats.FirstOrDefault(r => r.Id == created.Id);
    }

    /// <summary>Moves the selected strat to its folder's <c>.trash/</c> (decision 10), committing its edits first.</summary>
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

    [RelayCommand]
    private void Undo() => Session.Undo();

    [RelayCommand]
    private void Redo() => Session.Redo();

    // ── Selection ────────────────────────────────────────────────────────────────────────────────

    partial void OnSelectedOwnerChanged(StratOwnerOption? value)
    {
        if (_refreshing)
        {
            return;
        }

        // Another book: the open strat is not in it.
        SelectedStrat = null;
        RefreshList();
    }

    partial void OnSelectedMapChanged(string value)
    {
        if (!_refreshing)
        {
            RefreshList();
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

    private void OnSessionChanged()
    {
        Editor.Project();
        OnPropertyChanged(nameof(HasOpenStrat));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(StatusLine));
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
