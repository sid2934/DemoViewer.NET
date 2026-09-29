#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.Playback2D;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     The Step Authoring canvas in the Strat Book tab (step-authoring.md §3.10): the open strat projected
///     onto the 2D scene with no demo behind it. It is the frame host <see cref="Scene2DHost" /> binds, the
///     token editor <see cref="TokenTool" /> drags through, and the keymap executor the tab's keys reach.
///     <para>
///         <b>One history, the session's</b> (overview correction 18). Nothing here keeps an undo stack. A
///         closed gesture (a stroke, an erase, a token drag, a step key) becomes <see cref="PatchOp" />s through
///         <see cref="StepAuthoringPatches" /> and one <see cref="StratSession.Apply(IReadOnlyList{PatchOp})" />;
///         the session's change then rebuilds the projection, and the ink reaches the annotation document as
///         migrations (or a reset that drops the tools' own history), so Ctrl+Z always means the strat's undo.
///     </para>
///     <para>
///         <b>A private clock.</b> <see cref="StratTransport" /> plays and seeks the strat frame clock; nothing
///         here touches <c>IModuleContext</c>, so a strat never moves the shared playback clock or LiveSync.
///     </para>
///     <para>
///         <b>Frames are the export's frames.</b> Each published frame is a copy of
///         <see cref="StratFrameSource.FrameAt" /> at the playhead, so the canvas and an export draw the same
///         tokens, smokes and clock. Copied because the source pools its frames and the render thread replays a
///         published one after the next is built.
///     </para>
///     <para>UI-thread affine, like the session it edits.</para>
/// </summary>
public sealed partial class StratCanvasViewModel : ObservableObject, ISceneFrameHost, ITokenEditor, IDisposable
{
    /// <summary>World size of the square a map without a bundle is framed on, before any token is placed.</summary>
    private const double FallbackHalfExtent = 2048;

    /// <summary>World distance between the tokens <see cref="PlaceTokensCommand" /> lays out.</summary>
    private const double PlacementSpacing = 96;

    private readonly AnnotationSessionController _ink;
    private readonly AnnotationTrack _inkTrack;
    private readonly Func<Guid, StratDocument?>? _lookup;
    private readonly Func<string?, LoadedMapAsset?> _mapLoader;
    private readonly Func<IEnumerable<string>> _keybindOverrides;
    private readonly LineupOriginSource? _lineupOrigins;
    private readonly Func<string, Task<IZonePlaceResolver?>> _placesFor;
    private readonly Action<Action> _post;
    private readonly StratSession _session;
    private readonly StepTrack _stepTrack = new();

    private int _activeIndex = -1;
    private Guid? _activeStepId;
    private ArmedPlace? _armed;
    private Task<IZonePlaceResolver?>? _places;
    private string? _placesMap;
    private Guid? _pinnedStep;
    private CalloutResolver? _callouts;
    private bool _disposed;
    private bool _originsMoved;
    private DragState? _drag;
    private string? _mapName;
    private Guid? _projectedStrat;
    private int _projectedVersion = -1;
    private StratSceneProjection? _projection;
    private Guid? _seekToStep;
    private StratFrameSource? _source;
    private bool _syncingInk;
    private bool _syncingPaths;

    [ObservableProperty]
    private StratPathOption? _selectedPath;

    [ObservableProperty]
    private string _statusLine = "";

    /// <param name="session">The strat session: the document read, and the one history written.</param>
    /// <param name="mapLoader">Loads a map's bundle by name; the baked assets when omitted, null for none.</param>
    /// <param name="ticker">The transport's clock source; a dispatcher timer when omitted.</param>
    /// <param name="lookup">Reads another strat for a branch into it; null plays in-strat branches only.</param>
    /// <param name="keybindOverrides">The user's keymap rows; the settings file's when omitted.</param>
    /// <param name="readOnly">Plays the strat without editing it: no tools, no token drag, no step edits.</param>
    /// <param name="lineupOrigins">Puts a throw's actor at its lineup's throw origin; null projects none.</param>
    /// <param name="placesFor">
    ///     A map's zone places for Set On Map, loaded off the UI thread; the app's zone source through the processing
    ///     queue when omitted.
    /// </param>
    /// <param name="post">Returns a late place lookup to the UI thread; the dispatcher when omitted.</param>
    public StratCanvasViewModel(StratSession session, Func<string?, LoadedMapAsset?>? mapLoader = null,
        IStratTicker? ticker = null, Func<Guid, StratDocument?>? lookup = null,
        Func<IEnumerable<string>>? keybindOverrides = null, bool readOnly = false, LineupOriginSource? lineupOrigins = null,
        Func<string, Task<IZonePlaceResolver?>>? placesFor = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _lineupOrigins = lineupOrigins;
        _placesFor = placesFor ?? QueuedPlaces;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        IsReadOnly = readOnly;
        _mapLoader = mapLoader ?? MapAssetPipeline.TryLoad;
        _lookup = lookup;
        _keybindOverrides = keybindOverrides ?? SettingsOverrides;

        // Session only by construction: the strat file is the persistence, so there is no sidecar, and no
        // settings either, so the canvas's tool choice never becomes the 2D tab's remembered tool.
        Transport = new StratTransport(ticker);
        Transport.Changed += OnTransportChanged;

        _ink = new AnnotationSessionController(null, null);
        Annotations = new AnnotationsPanelViewModel(_ink, () => Transport.Tick);
        Annotations.PropertyChanged += OnAnnotationsPropertyChanged;
        _ink.Document.Changed += OnInkChanged;

        _inkTrack = new AnnotationTrack(_ink.Document);
        Timeline.RegisterTrack(_stepTrack);
        Timeline.RegisterTrack(_inkTrack);
        Timeline.SeekRequested += OnSeekRequested;
        Timeline.IsVisible = true;

        RefreshKeymap();
        _session.Changed += OnSessionChanged;
        if (_lineupOrigins is not null)
        {
            _lineupOrigins.Changed += OnProjectionInputChanged;
        }

        Reproject();
    }

    /// <summary>The tool row and ink pickers, over a session-only annotation controller.</summary>
    public AnnotationsPanelViewModel Annotations { get; }

    /// <summary>The private clock.</summary>
    public StratTransport Transport { get; }

    /// <summary>The scrubber: steps and strokes on the strat frame clock.</summary>
    public Playback2DTimelineViewModel Timeline { get; } = new();

    /// <summary>The token tracks the scene samples. Replaced slot by slot as the strat or a drag changes.</summary>
    public TokenTrackSet Tracks { get; } = new();

    /// <summary>The current projection, or null with no strat open.</summary>
    public StratSceneProjection? Projection => _projection;

    /// <summary>The resolved keymap: the shared table with the user's overrides on top.</summary>
    public Playback2DKeymapProfile Keymap { get; private set; } = Playback2DKeymapProfile.Default;

    /// <summary>The paths the canvas can play: the main line and one per branch.</summary>
    public ObservableCollection<StratPathOption> PathOptions { get; } = [];

    public bool HasDocument => _session.Document is not null;

    /// <summary>True when the canvas only plays: the Detected preview.</summary>
    public bool IsReadOnly { get; }

    public bool HasBranches => PathOptions.Count > 1;

    public bool IsPlaying => Transport.IsPlaying;

    /// <summary>The active step's position on the path, or -1 with no steps.</summary>
    public int ActiveStepIndex => _activeIndex;

    /// <summary>The active step, or null.</summary>
    public StratStep? ActiveStep =>
        _projection is { } p && _activeIndex >= 0 && _activeIndex < p.Path.Count ? p.Path[_activeIndex].Step : null;

    /// <summary>The playhead on the round clock, and which step it is in.</summary>
    public string ClockText
    {
        get
        {
            if (_projection is not { } p)
            {
                return "";
            }

            string clock = StratClock.Format(StepSchedule.AtSecondsFor(Transport.Tick, p.RoundSeconds));
            return p.Path.Count == 0
                ? clock + " · no steps"
                : string.Create(CultureInfo.InvariantCulture, $"{clock} · step {Math.Max(1, _activeIndex + 1)} of {p.Path.Count}");
        }
    }

    /// <summary>
    ///     Whether a tool other than pan is selected, or Set On Map is waiting for a click: what makes the keymap's
    ///     tool scope shadow Space and Esc.
    /// </summary>
    public bool IsToolActive => Annotations.ActiveTool != ToolKind.PanZoom || IsSettingPlace;

    /// <summary>What Set On Map would write on the active step: its <c>to</c>, its landing, or nothing.</summary>
    public StratPlaceTarget PlaceTarget =>
        EditableActiveStep() is not null && ActiveStep is { } step ? PlaceTargetFor(step) : StratPlaceTarget.None;

    public bool CanSetPlace => PlaceTarget != StratPlaceTarget.None;

    /// <summary>True while Set On Map waits for a click on the map.</summary>
    public bool IsSettingPlace => _armed is not null;

    /// <summary>The Set On Map button's words, naming the field the way the step row does.</summary>
    public string SetPlaceText => PlaceTarget switch
    {
        StratPlaceTarget.To => "Set “" + StratStepFields.ToLabel(ActiveStep?.Verb) + "” on map",
        StratPlaceTarget.Landing => "Set landing on map",
        _ => ""
    };

    public string SetPlaceToolTip => PlaceTarget switch
    {
        StratPlaceTarget.To => string.Create(CultureInfo.InvariantCulture,
            $"Click the map to set step {_activeIndex + 1}'s “{StratStepFields.ToLabel(ActiveStep?.Verb)}” to the place there (Esc cancels)"),
        StratPlaceTarget.Landing => string.Create(CultureInfo.InvariantCulture,
            $"Click the map where step {_activeIndex + 1}'s grenade lands: the point and the place there (Esc cancels)"),
        _ => ""
    };

    /// <summary>
    ///     The field a map click sets on a step (<see cref="StratStepFields" />): <c>to</c> for a verb that uses it
    ///     (fake included), the landing for one that uses only utility and has a kind and no lineup (a lineup says
    ///     where it lands), else nothing.
    /// </summary>
    /// <param name="step">The step.</param>
    public static StratPlaceTarget PlaceTargetFor(StratStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (StratStepFields.Uses(step.Verb, StratStepField.To))
        {
            return StratPlaceTarget.To;
        }

        return StratStepFields.Uses(step.Verb, StratStepField.Utility) && step.Utility is { LineupId: null }
            ? StratPlaceTarget.Landing
            : StratPlaceTarget.None;
    }

    public bool IsTokenToolSelected => Annotations.ActiveTool == ToolKind.Token;

    /// <summary>The token button's hint, with the resolved key.</summary>
    public string TokenToolTip =>
        "Token tool" + (Keymap.GestureText(Playback2DAction.ToolToken) is { Length: > 0 } key ? $" ({key})" : "")
                     + ": drag a token to place it at the active step; drag its heading stub to turn it";

    public string PlayGlyph => Transport.IsPlaying ? "⏸" : "▶";

    public string SpeedText => Transport.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    // ── ISceneFrameHost ──────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Scene2DFrame CurrentFrame { get; private set; } = Scene2DFrame.Empty;

    /// <inheritdoc />
    public LoadedMapAsset? MapAsset { get; private set; }

    /// <summary>No cones: a strat has no collision solve to run and no players to see from.</summary>
    public VisibilityEngine? VisionEngine => null;

    /// <inheritdoc />
    public AnnotationSession? AnnotationSession => _ink.Session;

    /// <summary>
    ///     Always on: the ink is the strat's content, not an overlay, so the 2D tab's annotations gate does not
    ///     apply to it.
    /// </summary>
    public bool IsAnnotationsEnabled => true;

    public bool ShowRadar => true;

    public bool ShowTrails => false;

    public bool ShowAreaEffects => true;

    public bool ShowVision => false;

    public bool ShowBombRing => false;

    public bool ShowZones => false;

    public PlaceResolver? Zones => null;

    /// <inheritdoc />
    public ITokenEditor? TokenEditor => IsReadOnly ? null : this;

    /// <inheritdoc />
    public event Action? FrameUpdated;

    /// <summary>
    ///     Rebases the projected ink onto a moved level set without touching the strat: the strat's
    ///     <c>levelMinZ</c> values are the truth, and a bundle's floors rarely move. A migration, so no undo.
    /// </summary>
    public void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double, double> zMinMap)
    {
        _syncingInk = true;
        try
        {
            _ink.Document.RemapWorldLevels(zMinMap);
        }
        finally
        {
            _syncingInk = false;
        }
    }

    /// <summary>
    ///     A plain left click while Set On Map waits: the place under it, on the clicked pane's floor, goes on the
    ///     step the mode was started for. Otherwise false, and the click goes to the pointer tools.
    /// </summary>
    public bool TryTagPositionAt(MapLevel level, double worldX, double worldY)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (_armed is not { } armed)
        {
            return false;
        }

        if (armed.Pending)
        {
            return true;
        }

        armed.Pending = true;
        double levelMinZ = MapSpace.QuantizeZ(level.ZMin);
        Task<IZonePlaceResolver?> places = PlacesFor(armed.Map, false);
        if (places.IsCompleted)
        {
            FinishPlace(armed, places, worldX, worldY, levelMinZ);
        }
        else
        {
            StatusLine = "reading this map's places…";
            places.ContinueWith(_ => _post(() => FinishPlace(armed, places, worldX, worldY, levelMinZ)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return true;
    }

    /// <summary>
    ///     Makes a step the active one: pauses, moves the playhead to the step's time, and keeps that step active
    ///     there even when the next step shares its tick. False when the path being played does not hold it.
    /// </summary>
    /// <param name="stepId">The step.</param>
    public bool SelectStep(Guid stepId)
    {
        if (_projection is not { } projection)
        {
            return false;
        }

        int index = projection.IndexOf(stepId);
        if (index < 0)
        {
            StatusLine = "that step is not on the path being played";
            return false;
        }

        if (_pinnedStep == stepId && _activeIndex == index && Transport.Tick == projection.Ticks[index] && !Transport.IsPlaying)
        {
            return true;
        }

        GoToStep(projection, index);
        return true;
    }

    // Pause before pinning: a pause while playing raises Changed, and that update would drop a pin set at a tick
    // the playhead has already left.
    private void GoToStep(StratSceneProjection projection, int index)
    {
        Transport.Pause();
        _pinnedStep = projection.Path[index].Step.Id;
        if (Transport.Tick != projection.Ticks[index])
        {
            Transport.Seek(projection.Ticks[index]);
        }
        else
        {
            UpdateActiveStep();
            Publish(false);
        }
    }

    /// <summary>Starts Set On Map for the active step. False when the step takes no place from the map.</summary>
    public bool BeginSetPlace()
    {
        if (_session.Document is not { } document || ActiveStep is not { } step || PlaceTarget is var target
            && target == StratPlaceTarget.None)
        {
            return false;
        }

        _armed = new ArmedPlace(document.Id, step.Id, target, document.Map, _activeIndex + 1,
            string.Create(CultureInfo.InvariantCulture, $"click the map for step {_activeIndex + 1}'s {TargetLabel(target, step)}; Esc cancels"));
        _ = PlacesFor(document.Map, true);
        StatusLine = _armed.Prompt;
        RaiseSetPlace();
        return true;
    }

    /// <summary>Stops Set On Map without writing. False when it was not on.</summary>
    public bool CancelSetPlace()
    {
        if (_armed is null)
        {
            return false;
        }

        _armed = null;
        StatusLine = "";
        RaiseSetPlace();
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _armed = null;
        _session.Changed -= OnSessionChanged;
        if (_lineupOrigins is not null)
        {
            _lineupOrigins.Changed -= OnProjectionInputChanged;
        }

        Annotations.PropertyChanged -= OnAnnotationsPropertyChanged;
        _ink.Document.Changed -= OnInkChanged;
        Transport.Changed -= OnTransportChanged;
        Timeline.SeekRequested -= OnSeekRequested;
        Transport.Dispose();
        Timeline.Dispose();
        _inkTrack.Dispose();
        Annotations.Dispose();
        _ink.Dispose();
        ReplaceMapAsset(null);
    }

    /// <summary>The owner's words for the map's places, for the step tooltips. Set by the tab with the editor's.</summary>
    /// <param name="callouts">The resolver for the strat's owner and map.</param>
    public void SetCallouts(CalloutResolver? callouts)
    {
        _callouts = callouts;
        _stepTrack.Update(_projection, _session.Document, _callouts);
    }

    /// <summary>Re-reads the user's keymap overrides: on construction and each time the tab is shown.</summary>
    public void RefreshKeymap()
    {
        IEnumerable<string> overrides;
        try
        {
            overrides = _keybindOverrides();
        }
        catch (Exception)
        {
            overrides = [];
        }

        Keymap = Playback2DKeymapProfile.FromOverrides(overrides, out _);
        Annotations.ApplyKeymap(Keymap);
        OnPropertyChanged(nameof(Keymap));
        OnPropertyChanged(nameof(TokenToolTip));
    }

    // ── Keymap executor ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Runs a resolved key (step-authoring.md §3.7). True when it acted. Round, kill, follow, tag and
    ///     suggestion actions mean nothing on a strat and are left unhandled, as are hold-pan and cancel,
    ///     which belong to the surface.
    /// </summary>
    /// <param name="action">The action the keymap resolved.</param>
    public bool ExecuteAction(Playback2DAction action)
    {
        if (_session.Document is null)
        {
            return false;
        }

        // Handled, not ignored: an unhandled Ctrl+Z would bubble to the tab and undo the book's open strat.
        if (IsReadOnly && action is not (Playback2DAction.TogglePlay or Playback2DAction.StepBack
                or Playback2DAction.StepForward or Playback2DAction.SpeedUp or Playback2DAction.SpeedDown
                or Playback2DAction.PrevStep or Playback2DAction.NextStep))
        {
            return action is Playback2DAction.Undo or Playback2DAction.Redo or Playback2DAction.ClearAnnotations
                or Playback2DAction.AddStep or Playback2DAction.DuplicateStep or Playback2DAction.DeleteStep
                or Playback2DAction.ToolDraw or Playback2DAction.ToolErase or Playback2DAction.ToolLine
                or Playback2DAction.ToolArrow or Playback2DAction.ToolRect or Playback2DAction.ToolEllipse
                or Playback2DAction.ToolText or Playback2DAction.ToolToken;
        }

        switch (action)
        {
            case Playback2DAction.TogglePlay:
                Transport.TogglePlay();
                return true;
            case Playback2DAction.StepBack:
                return Transport.Step(-1);
            case Playback2DAction.StepForward:
                return Transport.Step(1);
            case Playback2DAction.SpeedUp:
                return Transport.StepSpeed(1);
            case Playback2DAction.SpeedDown:
                return Transport.StepSpeed(-1);

            case Playback2DAction.ToolDraw:
                return Toggle(ToolKind.Draw);
            case Playback2DAction.ToolErase:
                return Toggle(ToolKind.Erase);
            case Playback2DAction.ToolLine:
                return Toggle(ToolKind.Line);
            case Playback2DAction.ToolArrow:
                return Toggle(ToolKind.Arrow);
            case Playback2DAction.ToolRect:
                return Toggle(ToolKind.Rect);
            case Playback2DAction.ToolEllipse:
                return Toggle(ToolKind.Ellipse);
            case Playback2DAction.ToolText:
                return Toggle(ToolKind.Text);
            case Playback2DAction.ToolToken:
                return Toggle(ToolKind.Token);

            // The strat's history, not the annotation document's: there is one, and a gesture in flight
            // finishes first, the rule the annotation document keeps for its own stack.
            case Playback2DAction.Undo:
                return !IsGestureOpen && _session.Undo();
            case Playback2DAction.Redo:
                return !IsGestureOpen && _session.Redo();

            case Playback2DAction.ClearAnnotations:
                return ClearActiveStepStrokes();
            case Playback2DAction.AddStep:
                return InsertStep();
            case Playback2DAction.DuplicateStep:
                return DuplicateActiveStep();
            case Playback2DAction.DeleteStep:
                return DeleteActiveStep();
            case Playback2DAction.PrevStep:
                return SeekStep(-1);
            case Playback2DAction.NextStep:
                return SeekStep(1);

            default:
                return false;
        }
    }

    [RelayCommand]
    private void TogglePlay() => ExecuteAction(Playback2DAction.TogglePlay);

    [RelayCommand]
    private void PrevStep() => ExecuteAction(Playback2DAction.PrevStep);

    [RelayCommand]
    private void NextStep() => ExecuteAction(Playback2DAction.NextStep);

    [RelayCommand]
    private void AddStep() => ExecuteAction(Playback2DAction.AddStep);

    [RelayCommand]
    private void DuplicateStep() => ExecuteAction(Playback2DAction.DuplicateStep);

    [RelayCommand]
    private void DeleteStep() => ExecuteAction(Playback2DAction.DeleteStep);

    [RelayCommand]
    private void ToggleSetPlace()
    {
        if (!CancelSetPlace())
        {
            BeginSetPlace();
        }
    }

    [RelayCommand]
    private void SelectTokenTool()
    {
        if (!IsReadOnly)
        {
            Annotations.SelectTool(ToolKind.Token);
        }
    }

    /// <summary>The loader the canvas reads its map bundle with; the export loads its own copy through it.</summary>
    internal Func<string?, LoadedMapAsset?> MapLoader => _mapLoader;

    /// <summary>
    ///     The open strat on the selected path as it stands now, for an export (step-authoring.md §3.6), or null
    ///     with no strat open. On the UI thread only, at Start: the track set is copied and the ink is a
    ///     <c>Reset</c> copy in a session of its own, so the canvas can keep editing while the file renders
    ///     (the catalog's "never the live document" rule, <c>SceneLayerCatalog.CreateSceneStack</c>).
    /// </summary>
    internal StratExportCapture? CaptureForExport()
    {
        if (_projection is not { } projection || _session.Document is not { } document)
        {
            return null;
        }

        AnnotationDocument frozen = new();
        frozen.Reset([.. _ink.Document.Elements]);
        return new StratExportCapture(projection, new TokenTrackSet(Tracks.Tracks), new AnnotationSession(frozen),
            document.Map, document.Name,
            MapAsset is { } asset ? MapAssetPipeline.RadarBounds(asset) : BoundsFor(projection));
    }

    /// <summary>
    ///     Puts every slot the path never places on the map at the active step, so there is a token to drag:
    ///     a slot with no keyframe is not drawn. One undo entry.
    /// </summary>
    [RelayCommand]
    private void PlaceTokens()
    {
        if (_session.Document is not { } document || _projection is not { } projection
                                                   || EditableActiveStep() is not { } stepIndex)
        {
            return;
        }

        IEnumerable<string> slots = projection.Canvas.ShowOpponents ? TokenSlots.All : TokenSlots.Own;
        List<string> missing = [.. slots.Where(s => projection.Tracks.All(t => t.Slot != s))];
        if (missing.Count == 0)
        {
            return;
        }

        WorldBounds bounds = CurrentFrame.Map.NetworkedBounds ?? BoundsFor(projection);
        Apply(StepAuthoringPatches.PlaceTokens(document, stepIndex, missing,
            (bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2, PlacementSpacing,
            projection.Canvas.DefaultLevelMinZ ?? 0));
    }

    // ── ITokenEditor ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public int ActiveTick =>
        _projection is { } p && _activeIndex >= 0 && _activeIndex < p.Ticks.Count ? p.Ticks[_activeIndex] : Transport.Tick;

    /// <inheritdoc />
    public bool TryHitToken(LevelPane pane, SKPoint world, float worldRadius, out string slot, out TokenGrip grip)
    {
        ArgumentNullException.ThrowIfNull(pane);
        slot = "";
        grip = TokenGrip.Body;
        double best = double.MaxValue;

        foreach (PlayerMarker marker in CurrentFrame.Markers)
        {
            // Only the tokens drawn in this pane: a stacked map shows the same XY on every floor.
            if (!pane.Level.Contains(marker.WorldZ) || marker.Slot < 0 || marker.Slot >= TokenSlots.All.Count
                || TokenHitTest.Classify(marker.WorldX, marker.WorldY, marker.YawDegrees, world, worldRadius) is not { } hit)
            {
                continue;
            }

            double dx = world.X - marker.WorldX, dy = world.Y - marker.WorldY;
            double distance = dx * dx + dy * dy;
            if (distance < best)
            {
                best = distance;
                slot = TokenSlots.All[marker.Slot];
                grip = hit;
            }
        }

        return best < double.MaxValue;
    }

    /// <inheritdoc />
    public void BeginDrag(string slot, TokenGrip grip)
    {
        _drag = null;
        if (_projection is not { } projection || EditableActiveStep() is null)
        {
            StatusLine = ActiveStep is null ? "add a step first: a token is placed at a step" : ReadOnlyNote;
            return;
        }

        if (projection.ThrowOriginAt(_activeIndex, slot) is not null)
        {
            StatusLine = LineupPlacedNote;
            return;
        }

        // The drag writes the active step's keyframe, so the canvas shows that step's moment while it does:
        // what is dragged is what is placed. Taken before the seek, which on a shared tick would pick the next step.
        int pathIndex = _activeIndex;
        Transport.Pause();
        _pinnedStep = projection.Path[pathIndex].Step.Id;
        int tick = projection.Ticks[pathIndex];
        if (Transport.Tick != tick)
        {
            Transport.Seek(tick);
        }

        TokenKeyframe start = Tracks.Get(slot) is { } track && track.TrySample(tick, out TokenKeyframe at)
            ? at
            : new TokenKeyframe(tick, 0, 0, projection.Canvas.DefaultLevelMinZ ?? 0, 0);
        _drag = new DragState(slot, grip, pathIndex, start.X, start.Y, start.LevelMinZ, start.YawDegrees);
    }

    /// <inheritdoc />
    public void MoveTo(string slot, SKPoint world, double levelMinZ)
    {
        if (_drag is not { } drag || _projection is not { } projection || drag.Slot != slot)
        {
            return;
        }

        if (drag.Grip == TokenGrip.Heading)
        {
            drag.Yaw = (float)(Math.Atan2(world.Y - drag.Y, world.X - drag.X) * 180 / Math.PI);
            drag.Turned = true;
        }
        else
        {
            drag.X = world.X;
            drag.Y = world.Y;
            if (double.IsFinite(levelMinZ))
            {
                drag.LevelMinZ = levelMinZ;
            }
        }

        drag.Moved = true;
        Tracks.Replace(projection.TrackWith(slot, drag.PathIndex,
            new TokenPlacement(drag.X, drag.Y, drag.LevelMinZ, drag.Yaw)));
        Publish(false);
    }

    /// <inheritdoc />
    public void EndDrag(float? yawDegrees)
    {
        if (_drag is not { } drag || _projection is not { } projection || _session.Document is not { } document)
        {
            _drag = null;
            return;
        }

        _drag = null;
        if (!drag.Moved && yawDegrees is null)
        {
            RestoreTracks();
            ReprojectIfOriginsMoved();
            return;
        }

        // A body move keeps the entry's own facing (absent: the previous keyframe's); a heading drag or a
        // Shift snap writes one.
        double? yaw = yawDegrees ?? (drag.Turned ? drag.Yaw : null);
        Apply([
            StepAuthoringPatches.TokenPosition(document, projection.Path[drag.PathIndex].StepIndex, drag.Slot,
                drag.X, drag.Y, drag.LevelMinZ, yaw)
        ]);
        ReprojectIfOriginsMoved();
    }

    /// <inheritdoc />
    public void CancelDrag()
    {
        _drag = null;
        RestoreTracks();
        ReprojectIfOriginsMoved();
    }

    // ── Projection ───────────────────────────────────────────────────────────────────────────────

    partial void OnSelectedPathChanged(StratPathOption? value)
    {
        if (!_syncingPaths)
        {
            Reproject();
        }
    }

    private bool IsGestureOpen => _drag is not null || _ink.Document.IsGestureOpen;

    private const string ReadOnlyNote = "this step belongs to another strat: open that strat to edit it";

    private const string LineupPlacedNote = "placed by its lineup: clear the lineup to move it";

    // Lineup origins or place centres moved. A gesture keeps the projection it started on; its end picks the change up.
    private void OnProjectionInputChanged()
    {
        if (IsGestureOpen)
        {
            _originsMoved = true;
            return;
        }

        Reproject();
    }

    private void ReprojectIfOriginsMoved()
    {
        if (_originsMoved && !IsGestureOpen)
        {
            Reproject();
        }
    }

    private void OnSessionChanged()
    {
        if (_session.Version != _projectedVersion)
        {
            Reproject();
        }
    }

    // Rebuilds everything the canvas shows from the session's document: path, schedule, tracks, ink, the
    // frame source, the transport's range and the timeline. Cheap next to a render: ten tracks and a few
    // dozen strokes.
    private void Reproject()
    {
        if (_disposed)
        {
            return;
        }

        _projectedVersion = _session.Version;
        _originsMoved = false;
        StratDocument? document = _session.Document;

        if (document?.Id != _projectedStrat)
        {
            // Another strat: its branches are not this one's, and a playhead from the last strat means nothing.
            _projectedStrat = document?.Id;
            _drag = null;
            _pinnedStep = null;
            _armed = null;
            _projection = null;
            _source = null;
            _syncingPaths = true;
            SelectedPath = null;
            _syncingPaths = false;
            Transport.Pause();
            Transport.SetRange(0, 0);
            Transport.Seek(0);
        }

        if (document is null)
        {
            _projection = null;
            _source = null;
            _activeIndex = -1;
            foreach (string slot in TokenSlots.All)
            {
                Tracks.Remove(slot);
            }

            SyncInk([]);
            PathOptions.Clear();
            _stepTrack.Update(null, null, null);
            Timeline.Rebuild(null);
            CurrentFrame = Scene2DFrame.Empty;
            StatusLine = "";
            RaiseState();
            FrameUpdated?.Invoke();
            return;
        }

        if (!string.Equals(document.Map, _mapName, StringComparison.Ordinal))
        {
            _mapName = document.Map;
            ReplaceMapAsset(SafeLoad(document.Map));

            // Read-only canvases too: a watching token faces its place in the Detected preview as well.
            string map = document.Map;
            Task<IZonePlaceResolver?> places = PlacesFor(map, false);
            if (!places.IsCompleted)
            {
                places.ContinueWith(_ => _post(() => OnPlacesLoaded(map)), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        Guid? pinned = _pinnedStep;

        RefreshPathOptions(document);
        IReadOnlyList<StratPathStep> path = SelectedPath?.BranchId is { } branch
            ? StratPath.Through(document, branch, _lookup) ?? StratPath.MainLine(document)
            : StratPath.MainLine(document);

        Func<double, double> levelFor = StratFromRound.FloorLevelKeys(MapAsset?.Floors);
        StratSceneProjection projection = StratSceneProjection.Build(document, path,
            _lineupOrigins is { } origins ? (map, utility) => origins.Resolve(map, utility, levelFor) : null,
            PlaceCentres(document.Map));
        _projection = projection;

        foreach (string slot in TokenSlots.All)
        {
            TokenTrack? track = projection.Tracks.FirstOrDefault(t => t.Slot == slot);
            if (track is null || (!projection.Canvas.ShowOpponents && TokenSlots.IsOpponent(slot)))
            {
                Tracks.Remove(slot);
            }
            else
            {
                Tracks.Replace(track);
            }
        }

        SyncInk(projection.Elements);

        WorldBounds bounds = MapAsset is { } asset ? MapAssetPipeline.RadarBounds(asset) : BoundsFor(projection);
        _source = new StratFrameSource(new StratSceneSpec(Tracks, projection.Schedule, _ink.Session, projection.Labels,
            document.Map, MapAsset is { } radarAsset ? MapAssetPipeline.DescribeRadars(radarAsset) : [], bounds, null,
            projection.Utility, projection.RoundSeconds, 0, projection.LastTick, StepSchedule.TicksPerSecond, 1));

        int first = projection.Ticks.Count > 0 ? projection.Ticks[0] : 0;
        Transport.SetRange(first, projection.LastTick);

        // A selected step stays selected through an edit, its time included; a new step becomes the selection.
        _pinnedStep = pinned;
        if (_seekToStep is { } seekTo)
        {
            _seekToStep = null;
            _pinnedStep = seekTo;
        }

        if (_pinnedStep is { } pin && !Transport.IsPlaying && projection.IndexOf(pin) is var pinIndex and >= 0
            && Transport.Tick != projection.Ticks[pinIndex])
        {
            Transport.Seek(projection.Ticks[pinIndex]);
        }

        _stepTrack.Update(projection, document, _callouts);
        Timeline.Rebuild(new StratTimelineData(projection.LastTick));

        StatusLine = projection.ClockClamped
            ? "a step's time runs backwards: it plays at the step before it until the table is fixed"
            : "";
        UpdateActiveStep();
        if (_armed is { } armed && (ActiveStep?.Id != armed.StepId || PlaceTarget != armed.Target))
        {
            _armed = null;
        }
        else if (_armed is { Pending: false } still)
        {
            StatusLine = still.Prompt;
        }

        RaiseSetPlace();
        Publish(true);
    }

    private void RefreshPathOptions(StratDocument document)
    {
        List<StratPathOption> options = [StratPathOption.MainLine];
        foreach (StratBranch branch in document.Branches)
        {
            int after = document.Steps.FindIndex(s => s.Id == branch.AfterStepId);
            if (after < 0)
            {
                continue;
            }

            string condition = string.IsNullOrWhiteSpace(branch.Condition.Text) ? "branch" : branch.Condition.Text;
            options.Add(new StratPathOption(branch.Id,
                string.Create(CultureInfo.InvariantCulture, $"after step {after + 1}: {condition}")));
        }

        Guid? keep = SelectedPath?.BranchId;
        _syncingPaths = true;
        try
        {
            if (!PathOptions.SequenceEqual(options))
            {
                PathOptions.Clear();
                foreach (StratPathOption option in options)
                {
                    PathOptions.Add(option);
                }
            }

            SelectedPath = PathOptions.FirstOrDefault(o => o.BranchId == keep) ?? PathOptions[0];
        }
        finally
        {
            _syncingPaths = false;
        }

        OnPropertyChanged(nameof(HasBranches));
    }

    // The projected ink into the annotation document. A document the tools left history on is reset, which
    // drops that history (the session holds the real one); otherwise the difference goes in as migrations,
    // which consume no undo entry, so an undo or a step-table edit redraws only what moved.
    private void SyncInk(IReadOnlyList<AnnotationElement> elements)
    {
        AnnotationDocument document = _ink.Document;
        if (document.IsGestureOpen)
        {
            return; // the gesture's close re-reads the projection
        }

        _syncingInk = true;
        try
        {
            if (document.UndoDepth > 0 || document.RedoDepth > 0)
            {
                document.Reset(elements);
                return;
            }

            HashSet<Guid> wanted = [.. elements.Select(e => e.Id)];
            foreach (AnnotationElement existing in document.Elements.ToList())
            {
                if (!wanted.Contains(existing.Id))
                {
                    document.ApplyMigration(new DocDelta.Remove(existing.Id));
                }
            }

            for (int i = 0; i < elements.Count; i++)
            {
                AnnotationElement element = elements[i];
                if (!document.TryGet(element.Id, out AnnotationElement current))
                {
                    document.ApplyMigration(new DocDelta.Add(element, i));
                }
                else if (!current.Equals(element))
                {
                    document.ApplyMigration(new DocDelta.Replace(element.Id, element));
                }
            }
        }
        finally
        {
            _syncingInk = false;
        }
    }

    // A tool closed a gesture on the ink: carry it into the strat as ops, one undo entry. What the strat
    // cannot hold (a stroke with no step to belong to, or on another strat's step) is put back as it was.
    private void OnInkChanged()
    {
        if (_syncingInk || _ink.Document.IsGestureOpen || _projection is not { } projection
            || _session.Document is not { } document)
        {
            return;
        }

        try
        {
            CarryInk(projection, document);
        }
        finally
        {
            ReprojectIfOriginsMoved();
        }
    }

    private void CarryInk(StratSceneProjection projection, StratDocument document)
    {
        IReadOnlyList<PatchOp> ops = StepAuthoringPatches.FromInk(document, projection, _ink.Document.Elements, StepFor);
        if (ops.Count > 0)
        {
            Apply(ops);
            return;
        }

        if (!_ink.Document.Elements.SequenceEqual(projection.Elements) || _ink.Document.UndoDepth > 0)
        {
            if (_ink.Document.Elements.Count != projection.Elements.Count)
            {
                StatusLine = ActiveStep is null ? "add a step first: a stroke belongs to a step" : ReadOnlyNote;
            }

            SyncInk(projection.Elements);
        }
    }

    // The step a new stroke belongs to: the one whose window opens at its FromTick (§3.5), the active step
    // first when two share the tick.
    private int StepFor(AnnotationElement element)
    {
        if (_projection is not { } projection || element.Time.FromTick is not { } from)
        {
            return _activeIndex;
        }

        if (_activeIndex >= 0 && _activeIndex < projection.Ticks.Count && projection.Ticks[_activeIndex] == from)
        {
            return _activeIndex;
        }

        return projection.Schedule.At(from) is { } window ? projection.Schedule.IndexOf(window.StepId) : -1;
    }

    private void Apply(IReadOnlyList<PatchOp> ops)
    {
        if (ops.Count == 0)
        {
            return;
        }

        if (IsReadOnly)
        {
            Reproject();
            return;
        }

        try
        {
            _session.Apply(ops);
        }
        catch (InvalidOperationException e)
        {
            // The document moved under the gesture; draw what it says rather than what the gesture assumed.
            StatusLine = "that edit did not apply: " + e.Message;
            Reproject();
        }
    }

    private void RestoreTracks()
    {
        if (_projection is not { } projection)
        {
            return;
        }

        foreach (TokenTrack track in projection.Tracks)
        {
            if (projection.Canvas.ShowOpponents || !track.IsOpponent)
            {
                Tracks.Replace(track);
            }
        }

        Publish(false);
    }

    // ── Steps ────────────────────────────────────────────────────────────────────────────────────

    private int? EditableActiveStep() =>
        !IsReadOnly && _projection is { } p && _activeIndex >= 0 && _activeIndex < p.Path.Count && p.Path[_activeIndex].Editable
            ? p.Path[_activeIndex].StepIndex
            : null;

    private bool InsertStep()
    {
        if (_session.Document is not { } document || _projection is not { } projection)
        {
            return false;
        }

        // After the active step, or first when the strat has none or the active one is another strat's.
        int after = EditableActiveStep() ?? (projection.Path.Count == 0 ? -1 : document.Steps.Count - 1);
        double atSeconds = StepSchedule.AtSecondsFor(Transport.Tick, projection.RoundSeconds);
        Guid id = Guid.NewGuid();
        _seekToStep = id;
        Func<double, double> levelFor = StratFromRound.FloorLevelKeys(MapAsset?.Floors);
        Apply([StepAuthoringPatches.AddCarriedStep(document, after, atSeconds, id,
            _lineupOrigins is { } origins ? (map, utility) => origins.Resolve(map, utility, levelFor) : null,
            PlaceCentres(document.Map))]);
        return true;
    }

    private bool DuplicateActiveStep()
    {
        if (_session.Document is not { } document || EditableActiveStep() is not { } index)
        {
            return false;
        }

        Guid id = Guid.NewGuid();
        _seekToStep = id;
        Apply([StepAuthoringPatches.DuplicateStep(document, index, id)]);
        return true;
    }

    private bool DeleteActiveStep()
    {
        if (_session.Document is not { } document || EditableActiveStep() is not { } index)
        {
            return false;
        }

        if (index > 0)
        {
            _seekToStep = document.Steps[index - 1].Id;
        }

        Apply(StepAuthoringPatches.DeleteStep(document, index));
        return true;
    }

    private bool ClearActiveStepStrokes()
    {
        if (_session.Document is not { } document || EditableActiveStep() is not { } index
                                                   || document.Steps[index].Strokes.Count == 0)
        {
            return false;
        }

        Apply(StepAuthoringPatches.ClearStrokes(document, index));
        return true;
    }

    private bool SeekStep(int direction)
    {
        if (_projection is not { Ticks.Count: > 0 } projection)
        {
            return false;
        }

        // By index, not by tick, so two steps on one tick are both visited. Short of the active step's start (before
        // the first step) next goes to it; back from inside a window goes to that step's start first, as a
        // player's previous does.
        int tick = Transport.Tick;
        int active = Math.Clamp(_activeIndex, 0, projection.Ticks.Count - 1);
        int start = projection.Ticks[active];
        int target = direction > 0
            ? tick < start ? active : active + 1
            : tick > start ? active : active - 1;
        if (target < 0 || target >= projection.Ticks.Count)
        {
            return false;
        }

        GoToStep(projection, target);
        return true;
    }

    private bool Toggle(ToolKind kind)
    {
        Annotations.SelectTool(Annotations.ActiveTool == kind ? ToolKind.PanZoom : kind);
        return true;
    }

    private void OnAnnotationsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotationsPanelViewModel.ActiveTool))
        {
            OnPropertyChanged(nameof(IsToolActive));
            OnPropertyChanged(nameof(IsTokenToolSelected));
        }
    }

    // ── Clock ────────────────────────────────────────────────────────────────────────────────────

    private void OnSeekRequested(int frameIndex)
    {
        Transport.Pause();
        Transport.Seek(frameIndex);
    }

    private void OnTransportChanged(bool discontinuity)
    {
        UpdateActiveStep();
        Publish(discontinuity);
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayGlyph));
        OnPropertyChanged(nameof(SpeedText));
    }

    // The active step is the one whose window holds the playhead; before the first step, the first. New
    // strokes are stamped with its window, so the tools need no knowledge of steps (§3.5).
    private void UpdateActiveStep()
    {
        int index = -1;
        if (_projection is { } projection && projection.Path.Count > 0)
        {
            index = projection.Schedule.At(Transport.Tick) is { } window ? projection.Schedule.IndexOf(window.StepId) : 0;
            if (_pinnedStep is { } pin)
            {
                int pinned = projection.IndexOf(pin);
                if (pinned >= 0 && projection.Ticks[pinned] == Transport.Tick)
                {
                    index = pinned;
                }
                else
                {
                    _pinnedStep = null;
                }
            }

            StepWindow active = projection.Schedule.Windows[index];
            AnnotationSession session = _ink.Session;
            session.DefaultVisibility = EnvelopeMode.Custom;
            session.FadeInTicks = projection.Canvas.FadeInTicks;
            session.FadeOutTicks = projection.Canvas.FadeOutTicks;
            session.SetCustomWindow(active.FromTick, active.UntilTick ?? projection.LastTick);
        }

        Guid? id = _projection is { } p && index >= 0 ? p.Path[index].Step.Id : null;
        if (index != _activeIndex || id != _activeStepId)
        {
            _activeIndex = index;
            _activeStepId = id;
            if (_armed is { } armed && armed.StepId != id)
            {
                _armed = null;
                StatusLine = "";
            }

            OnPropertyChanged(nameof(ActiveStepIndex));
            OnPropertyChanged(nameof(ActiveStep));
            RaiseSetPlace();
        }
    }

    private void Publish(bool discontinuity)
    {
        int tick = Transport.Tick;
        if (_source is { } source)
        {
            // The pooled frame is copied, never published: the render thread still replays the last one.
            Scene2DFrame pooled = source.FrameAt(tick);
            CurrentFrame = new Scene2DFrame
            {
                Time = new SceneTime(tick, tick, tick / (double)StepSchedule.TicksPerSecond, Transport.LastDeltaSeconds,
                    discontinuity),
                Markers = [.. pooled.Markers],
                AreaEffects = [.. pooled.AreaEffects],
                GameInfo = pooled.GameInfo,
                Map = pooled.Map
            };
        }
        else
        {
            CurrentFrame = Scene2DFrame.Empty;
        }

        Timeline.UpdatePlayhead(tick, tick);
        RaiseState();
        FrameUpdated?.Invoke();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(ClockText));
        OnPropertyChanged(nameof(Projection));
    }

    // ── Set On Map ───────────────────────────────────────────────────────────────────────────────

    // One click, one write: the mode ends here whether the click found a place or not. A result that lands after
    // a cancel, another strat or a deleted step writes nothing.
    private void FinishPlace(ArmedPlace armed, Task<IZonePlaceResolver?> places, double x, double y, double levelMinZ)
    {
        if (!ReferenceEquals(_armed, armed))
        {
            return;
        }

        _armed = null;
        RaiseSetPlace();
        int index = _session.Document is { } open && open.Id == armed.StratId ? open.Steps.FindIndex(s => s.Id == armed.StepId) : -1;
        if (index < 0 || _session.Document is not { } document)
        {
            StatusLine = "";
            return;
        }

        IZonePlaceResolver? zones = places.IsCompletedSuccessfully ? places.Result : null;
        string? place = zones?.ResolveOnFloor(x, y, levelMinZ);
        StratStep step = document.Steps[index];
        string label = TargetLabel(armed.Target, step);
        string number = armed.Number.ToString(CultureInfo.InvariantCulture);

        if (armed.Target == StratPlaceTarget.To)
        {
            if (place is null)
            {
                StatusLine = zones is null ? "no places for this map: nothing set" : "no place there: nothing set";
                return;
            }

            IReadOnlyList<PatchOp> ops = StepAuthoringPatches.ToPlace(document, index, place);
            Apply(ops);
            StatusLine = ops.Count == 0 ? $"step {number}'s {label} is already {Display(place)}" : $"step {number}'s {label} is now {Display(place)}";
            return;
        }

        if (step.Utility is null)
        {
            StatusLine = "";
            return;
        }

        // Without zones there is no answer about the place, so the stored one stays; with zones, a miss drops it.
        string? landingPlace = zones is null ? step.Utility.Landing?.Place : place;
        Apply(StepAuthoringPatches.Landing(document, index, landingPlace, x, y, levelMinZ));
        StatusLine = zones is null
            ? $"no places for this map: step {number}'s landing point is set, its place kept"
            : place is null
                ? $"no place there: step {number}'s landing is the point only"
                : $"step {number} lands at {Display(place)}";
    }

    private static string TargetLabel(StratPlaceTarget target, StratStep step) =>
        target == StratPlaceTarget.Landing ? "landing" : "“" + StratStepFields.ToLabel(step.Verb) + "”";

    private string Display(string place) => _callouts is { } callouts && callouts.IsCanonical(place) ? callouts.Display(place) : place;

    // Only once the map's zones are in memory; until then a watching token keeps its authored facing.
    private PlaceCentreResolver? PlaceCentres(string map) =>
        _places is { IsCompletedSuccessfully: true, Result: { } zones } && string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
            ? zones.PlaceCentre
            : null;

    private void OnPlacesLoaded(string map)
    {
        if (!_disposed && string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(_session.Document?.Map, map, StringComparison.OrdinalIgnoreCase))
        {
            OnProjectionInputChanged();
        }
    }

    // One load per map, kept for the canvas's life; a failed one, or on request an empty one, is asked again.
    private Task<IZonePlaceResolver?> PlacesFor(string map, bool retryEmpty)
    {
        bool stale = _places is null || !string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
                     || _places.IsFaulted || _places.IsCanceled
                     || (retryEmpty && _places.IsCompletedSuccessfully && _places.Result is null);
        if (stale)
        {
            _placesMap = map;
            try
            {
                _places = _placesFor(map);
            }
            catch (Exception e)
            {
                _places = Task.FromException<IZonePlaceResolver?>(e);
            }
        }

        return _places!;
    }

    // The zone source reads files under a lock the first time, so it runs as a queue item, at the front: a user
    // opened the strat or clicked for a place.
    // One read in flight per map across every canvas (the editor's and a Detected preview's), so the queue shows one.
    private static Task<IZonePlaceResolver?> QueuedPlaces(string map)
    {
        lock (_placesGate)
        {
            if (_placesInFlight.TryGetValue(map, out Task<IZonePlaceResolver?>? running) && !running.IsCompleted)
            {
                return running;
            }

            Task<IZonePlaceResolver?> read = QueueWork.RunAsync(QueueWork.Ambient, QueueJobKind.SectionCompute,
                "Strat places: " + map, "Strat Book",
                () => (App.Services?.GetService<IZonePlaceResolverSource>() ?? NoZonePlaceResolverSource.Instance).TryGet(map),
                null, DemoJobPriority.UserRequested);
            _placesInFlight[map] = read;
            return read;
        }
    }

    private static readonly Lock _placesGate = new();
    private static readonly Dictionary<string, Task<IZonePlaceResolver?>> _placesInFlight = new(StringComparer.OrdinalIgnoreCase);

    private void RaiseSetPlace()
    {
        OnPropertyChanged(nameof(PlaceTarget));
        OnPropertyChanged(nameof(CanSetPlace));
        OnPropertyChanged(nameof(IsSettingPlace));
        OnPropertyChanged(nameof(SetPlaceText));
        OnPropertyChanged(nameof(SetPlaceToolTip));
        OnPropertyChanged(nameof(IsToolActive));
    }

    // ── Map ──────────────────────────────────────────────────────────────────────────────────────

    // Everything the strat places, with a margin, when the map has no bundle to frame: the camera's first
    // fit needs a rectangle, and the grid fallback draws under it.
    private static WorldBounds BoundsFor(StratSceneProjection projection)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (TokenTrack track in projection.Tracks)
        {
            foreach (TokenKeyframe keyframe in track.Keyframes)
            {
                minX = Math.Min(minX, keyframe.X);
                minY = Math.Min(minY, keyframe.Y);
                maxX = Math.Max(maxX, keyframe.X);
                maxY = Math.Max(maxY, keyframe.Y);
            }
        }

        if (minX > maxX)
        {
            return new WorldBounds(-FallbackHalfExtent, -FallbackHalfExtent, FallbackHalfExtent, FallbackHalfExtent);
        }

        const double margin = 512;
        return new WorldBounds(minX - margin, minY - margin, maxX + margin, maxY + margin);
    }

    private LoadedMapAsset? SafeLoad(string map)
    {
        try
        {
            return _mapLoader(map);
        }
        catch (Exception)
        {
            return null; // no bundle draws the grid, which is still a usable canvas
        }
    }

    // The previous bundle's radar images are native memory; disposed a dispatcher hop later because the
    // render thread may still be replaying a picture that draws one, the 2D tab's rule.
    private void ReplaceMapAsset(LoadedMapAsset? next)
    {
        LoadedMapAsset? previous = MapAsset;
        MapAsset = next;
        if (previous is not null && !ReferenceEquals(previous, next))
        {
            Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
        }
    }

    private static string[] SettingsOverrides() =>
        App.Services?.GetService<SettingsService>()?.Current.Playback2D.KeybindOverrides ?? [];

    // Set On Map waiting for its click. Pending once the click is in and the place lookup has not answered.
    private sealed class ArmedPlace(Guid stratId, Guid stepId, StratPlaceTarget target, string map, int number, string prompt)
    {
        public string Prompt { get; } = prompt;
        public Guid StratId { get; } = stratId;
        public Guid StepId { get; } = stepId;
        public StratPlaceTarget Target { get; } = target;
        public string Map { get; } = map;
        public int Number { get; } = number;
        public bool Pending { get; set; }
    }

    // A drag's working state. A class, not a struct: MoveTo updates it in place across forty samples.
    private sealed class DragState(string slot, TokenGrip grip, int pathIndex, float x, float y, double levelMinZ, float yaw)
    {
        public string Slot { get; } = slot;
        public TokenGrip Grip { get; } = grip;
        public int PathIndex { get; } = pathIndex;
        public float X { get; set; } = x;
        public float Y { get; set; } = y;
        public double LevelMinZ { get; set; } = levelMinZ;
        public float Yaw { get; set; } = yaw;
        public bool Moved { get; set; }
        public bool Turned { get; set; }
    }
}

/// <summary>What a Set On Map click writes on a step.</summary>
public enum StratPlaceTarget
{
    /// <summary>The step takes no place from the map.</summary>
    None,

    /// <summary>The step's <c>to</c> place.</summary>
    To,

    /// <summary>The step's utility landing: the point, its level and the place under it.</summary>
    Landing
}

/// <summary>A path the canvas can play: the main line, or the one through a branch.</summary>
/// <param name="BranchId">The branch, or null for the main line.</param>
/// <param name="Label">What the picker shows.</param>
public sealed record StratPathOption(Guid? BranchId, string Label)
{
    public static StratPathOption MainLine { get; } = new(null, "main line");

    public override string ToString() => Label;
}
