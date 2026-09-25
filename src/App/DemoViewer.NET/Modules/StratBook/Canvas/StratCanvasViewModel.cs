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
    private readonly StratSession _session;
    private readonly StepTrack _stepTrack = new();

    private int _activeIndex = -1;
    private CalloutResolver? _callouts;
    private bool _disposed;
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
    public StratCanvasViewModel(StratSession session, Func<string?, LoadedMapAsset?>? mapLoader = null,
        IStratTicker? ticker = null, Func<Guid, StratDocument?>? lookup = null,
        Func<IEnumerable<string>>? keybindOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
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

    /// <summary>Whether a tool other than pan is selected: what makes the keymap's tool scope shadow Space and Esc.</summary>
    public bool IsToolActive => Annotations.ActiveTool != ToolKind.PanZoom;

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
    public ITokenEditor? TokenEditor => this;

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

    /// <summary>Nothing to tag on a strat: every click goes to the pointer tools.</summary>
    public bool TryTagPositionAt(MapLevel level, double worldX, double worldY) => false;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Changed -= OnSessionChanged;
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
    private void SelectTokenTool() => Annotations.SelectTool(ToolKind.Token);

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

        // The drag writes the active step's keyframe, so the canvas shows that step's moment while it does:
        // what is dragged is what is placed.
        Transport.Pause();
        int tick = ActiveTick;
        if (Transport.Tick != tick)
        {
            Transport.Seek(tick);
        }

        TokenKeyframe start = Tracks.Get(slot) is { } track && track.TrySample(tick, out TokenKeyframe at)
            ? at
            : new TokenKeyframe(tick, 0, 0, projection.Canvas.DefaultLevelMinZ ?? 0, 0);
        _drag = new DragState(slot, grip, _activeIndex, start.X, start.Y, start.LevelMinZ, start.YawDegrees);
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
            return;
        }

        // A body move keeps the entry's own facing (absent: the previous keyframe's); a heading drag or a
        // Shift snap writes one.
        double? yaw = yawDegrees ?? (drag.Turned ? drag.Yaw : null);
        Apply([
            StepAuthoringPatches.TokenPosition(document, projection.Path[drag.PathIndex].StepIndex, drag.Slot,
                drag.X, drag.Y, drag.LevelMinZ, yaw)
        ]);
    }

    /// <inheritdoc />
    public void CancelDrag()
    {
        _drag = null;
        RestoreTracks();
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
        StratDocument? document = _session.Document;

        if (document?.Id != _projectedStrat)
        {
            // Another strat: its branches are not this one's, and a playhead from the last strat means nothing.
            _projectedStrat = document?.Id;
            _drag = null;
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
        }

        RefreshPathOptions(document);
        IReadOnlyList<StratPathStep> path = SelectedPath?.BranchId is { } branch
            ? StratPath.Through(document, branch, _lookup) ?? StratPath.MainLine(document)
            : StratPath.MainLine(document);

        StratSceneProjection projection = StratSceneProjection.Build(document, path);
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
        if (_seekToStep is { } seekTo)
        {
            _seekToStep = null;
            int index = projection.IndexOf(seekTo);
            if (index >= 0)
            {
                Transport.Seek(projection.Ticks[index]);
            }
        }

        _stepTrack.Update(projection, document, _callouts);
        Timeline.Rebuild(new StratTimelineData(projection.LastTick));

        StatusLine = projection.ClockClamped
            ? "a step's time runs backwards: it plays at the step before it until the table is fixed"
            : "";
        UpdateActiveStep();
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
        _projection is { } p && _activeIndex >= 0 && _activeIndex < p.Path.Count && p.Path[_activeIndex].Editable
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
        Apply([StepAuthoringPatches.AddStep(document, after, atSeconds, id)]);
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

        int tick = Transport.Tick;
        int target = -1;
        if (direction > 0)
        {
            for (int i = 0; i < projection.Ticks.Count; i++)
            {
                if (projection.Ticks[i] > tick)
                {
                    target = i;
                    break;
                }
            }
        }
        else
        {
            // Back from inside a step's window goes to that step's start first, as a player's previous does.
            for (int i = projection.Ticks.Count - 1; i >= 0; i--)
            {
                if (projection.Ticks[i] < tick)
                {
                    target = i;
                    break;
                }
            }
        }

        if (target < 0)
        {
            return false;
        }

        Transport.Pause();
        Transport.Seek(projection.Ticks[target]);
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
            StepWindow active = projection.Schedule.Windows[index];
            AnnotationSession session = _ink.Session;
            session.DefaultVisibility = EnvelopeMode.Custom;
            session.FadeInTicks = projection.Canvas.FadeInTicks;
            session.FadeOutTicks = projection.Canvas.FadeOutTicks;
            session.SetCustomWindow(active.FromTick, active.UntilTick ?? projection.LastTick);
        }

        if (index != _activeIndex)
        {
            _activeIndex = index;
            OnPropertyChanged(nameof(ActiveStepIndex));
            OnPropertyChanged(nameof(ActiveStep));
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

/// <summary>A path the canvas can play: the main line, or the one through a branch.</summary>
/// <param name="BranchId">The branch, or null for the main line.</param>
/// <param name="Label">What the picker shows.</param>
public sealed record StratPathOption(Guid? BranchId, string Label)
{
    public static StratPathOption MainLine { get; } = new(null, "main line");

    public override string ToString() => Label;
}
