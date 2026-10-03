#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Input;
using DemoViewer.NET.Features;
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
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.Playback2D;
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
public sealed partial class StratCanvasViewModel : ObservableObject, ISceneFrameHost, ITokenEditingHost, IGuidesHost, ITokenEditor, IDisposable
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
    private readonly Func<string, Task<StratSpawns?>>? _spawnsFor;
    private Task<StratSpawns?>? _spawns;
    private string? _spawnsMap;
    private readonly Action<Action> _post;
    private readonly IFeatureGate? _gate;
    private readonly Func<bool> _routing;
    private readonly StratSession _session;
    private readonly StepTrack _stepTrack = new();

    private int _activeIndex = -1;
    private bool _startSelected;
    private Guid? _activeStepId;
    private ArmedPlace? _armed;
    private Task<IZonePlaceResolver?>? _places;
    private string? _placesMap;
    private Guid? _pinnedStep;
    private string? _selectedSlot;
    private CalloutResolver? _callouts;
    private bool _disposed;
    private bool _originsMoved;
    private DragState? _drag;
    private string? _mapName;
    private Guid? _projectedStrat;
    private int _projectedVersion = -1;
    private bool _projectedRouting;
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
    /// <param name="routing">
    ///     Whether tokens follow the map's nav round walls; the <c>stratbook.routing</c> feature when omitted, read off
    ///     <paramref name="lookups" />'s gate, which a settings change re-reads. With no gate injected, routing is off.
    /// </param>
    /// <param name="spawnsFor">
    ///     A map's spawns, for an older strat's start (<see cref="StratStartBlock.Effective" />); null reads no spawns.
    /// </param>
    /// <param name="lookups">
    ///     The feature gate, zone place resolver and settings a caller two hops up resolved from the app; null
    ///     members (and a null bundle) mean the same as before: routing off, the baked-in place resolver, no
    ///     keybind overrides.
    /// </param>
    public StratCanvasViewModel(StratSession session, Func<string?, LoadedMapAsset?>? mapLoader = null,
        IStratTicker? ticker = null, Func<Guid, StratDocument?>? lookup = null,
        Func<IEnumerable<string>>? keybindOverrides = null, bool readOnly = false, LineupOriginSource? lineupOrigins = null,
        Func<string, Task<IZonePlaceResolver?>>? placesFor = null, Action<Action>? post = null, Func<bool>? routing = null,
        Func<string, Task<StratSpawns?>>? spawnsFor = null, StratCanvasServices? lookups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _spawnsFor = spawnsFor;
        _lineupOrigins = lineupOrigins;
        _placesFor = placesFor ?? (map => QueuedPlaces(map, lookups?.Places));
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        // No gate injected (a headless test, a designer instance): routing stays off.
        _gate = routing is null ? lookups?.Gate : null;
        _routing = routing ?? (() => _gate?.IsEnabled(FeatureCatalog.StratRoutingFeatureId) ?? false);
        if (_gate is not null)
        {
            _gate.Changed += OnGateChanged;
        }

        IsReadOnly = readOnly;
        _mapLoader = mapLoader ?? MapAssetPipeline.TryLoad;
        _lookup = lookup;
        _keybindOverrides = keybindOverrides ?? (() => lookups?.Settings?.Current.Playback2D.KeybindOverrides ?? []);

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

    /// <summary>
    ///     Whether the Start row is selected: the playhead is on tick 0 and a drag or a map pick there writes a token's
    ///     start. Cleared when the playhead leaves tick 0 or a step is selected.
    /// </summary>
    public bool IsStartSelected => _startSelected;

    /// <summary>Selects the start: pauses at tick 0, where the tokens stand before step 1.</summary>
    public void SelectStart()
    {
        if (_session.Document is null)
        {
            return;
        }

        Transport.Pause();
        _pinnedStep = null;
        _startSelected = true;
        if (Transport.Tick != 0)
        {
            Transport.Seek(0);
        }
        else
        {
            UpdateActiveStep();
            Publish(false);
        }

        OnPropertyChanged(nameof(IsStartSelected));
    }

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

            string clock = p.ClockTextAt(Transport.Tick);
            return p.Path.Count == 0
                ? clock + " · no steps"
                : string.Create(CultureInfo.InvariantCulture, $"{clock} · step {Math.Max(1, _activeIndex + 1)} of {p.Path.Count}");
        }
    }

    /// <summary>
    ///     Whether a tool other than pan is selected, or Set On Map is waiting for a click: what makes the keymap's
    ///     tool scope shadow Space and Esc.
    /// </summary>
    public bool IsToolActive => Annotations.ActiveTool != ToolKind.PanZoom || IsSettingPlace || _drag is not null;

    /// <summary>
    ///     The slot whose line is selected on the active step: the chosen one when the step has a line for it, else its
    ///     first line; null for a step for everyone. Set On Map writes this line's place.
    /// </summary>
    public string? SelectedLineSlot => ActiveStep is { } step ? LineSlotOn(step, _selectedSlot) : null;

    /// <summary>Chooses a line by its slot: the rows, and a press on a token, set it. Kept across steps.</summary>
    /// <param name="slot">A slot letter, or null.</param>
    public void SelectLine(string? slot)
    {
        if (string.Equals(_selectedSlot, slot, StringComparison.Ordinal))
        {
            return;
        }

        _selectedSlot = slot;
        if (_armed is { Field.Slot: { } armedSlot } && !string.Equals(armedSlot, SelectedLineSlot, StringComparison.Ordinal))
        {
            CancelSetPlace();
        }

        RaiseSetPlace();
    }

    private string? LineSlotOn(StratStep step, string? wanted)
    {
        IReadOnlyList<StepAssignment> lines = WritesOneLine(step) && !StratStepLines.HasLines(step)
            ? StratLinePatches.Copy(step, true)
            : StratStepLines.Of(step);
        return lines.Any(l => string.Equals(l.Slot, wanted, StringComparison.Ordinal)) ? wanted : lines.Count > 0 ? lines[0].Slot : null;
    }

    /// <summary>The place centres the projection faces tokens with, or null until the map's zones are in memory.</summary>
    public PlaceCentreResolver? CurrentPlaceCentres => _session.Document is { } document ? PlaceCentres(document.Map) : null;

    /// <summary>What Set On Map would write on the active step: its <c>to</c>, its landing, or nothing.</summary>
    public StratPlaceTarget PlaceTarget =>
        EditableActiveStep() is not null && ActiveStep is { } step ? PlaceTargetFor(step) : StratPlaceTarget.None;

    public bool CanSetPlace => PlaceTarget != StratPlaceTarget.None || _armed is not null;

    /// <summary>True while Set On Map waits for a click on the map.</summary>
    public bool IsSettingPlace => _armed is not null;

    /// <summary>The location field a map click will write, or null when Set On Map is off.</summary>
    public StratLocationField? ArmedField => _armed?.Field ?? _drag?.ShownField;

    /// <summary>The Set On Map button's words, naming the field the way the step row does.</summary>
    public string SetPlaceText => _armed is { } armed && ActiveStep is { } armedStep && !IsToolbarField(armed.Field, armedStep)
        ? "Set " + FieldLabel(armed.Field, armedStep) + " on map"
        : PlaceTarget switch
    {
        StratPlaceTarget.To => "Set " + (SelectedLineSlot is { } slot && ActiveStep is { } step && WritesOneLine(step)
                                                                     && !WritesAllLines(step)
                                   ? slot + "'s "
                                   : "")
                               + "“" + StratStepFields.ToLabel(ActiveStep?.Verb) + "” on map",
        StratPlaceTarget.Landing => "Set landing on map",
        StratPlaceTarget.LurkArea => "Set “lurk area” on map",
        _ => ""
    };

    public string SetPlaceToolTip => PlaceTarget switch
    {
        StratPlaceTarget.To => string.Create(CultureInfo.InvariantCulture,
            $"Click the map to set step {_activeIndex + 1}'s “{StratStepFields.ToLabel(ActiveStep?.Verb)}” to the place there (Esc cancels)"),
        StratPlaceTarget.Landing => string.Create(CultureInfo.InvariantCulture,
            $"Click the map where step {_activeIndex + 1}'s grenade lands: the point and the place there (Esc cancels)"),
        StratPlaceTarget.LurkArea => string.Create(CultureInfo.InvariantCulture,
            $"Click the map to add the place there to step {_activeIndex + 1}'s lurk areas (Esc cancels)"),
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

        if (StratStepFields.Uses(step.Verb, StratStepField.Lurk))
        {
            return StratPlaceTarget.LurkArea;
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

    // ── ISceneFrameHost, IGuidesHost, ITokenEditingHost ─────────────────────────────────────────────

    /// <inheritdoc />
    public Scene2DFrame CurrentFrame { get; private set; } = Scene2DFrame.Empty;

    /// <inheritdoc />
    public LoadedMapAsset? MapAsset { get; private set; }

    /// <inheritdoc />
    /// <remarks>Replaced whole on each publish, never changed in place: the render thread reads it.</remarks>
    public SceneGuides Guides { get; private set; } = SceneGuides.None;

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

    public bool ShowTrails => true;

    public bool ShowAreaEffects => true;

    public bool ShowVision => false;

    public bool ShowBombRing => false;

    /// <summary>Each token shows where it looks; on the editing canvas a drag of the cone turns it.</summary>
    public bool ShowViewCones => true;

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

    /// <inheritdoc />
    bool ISceneFrameHost.TryPointerPreHandler(ScenePointer pointer) =>
        TryTagPositionAt(pointer.Level, pointer.WorldX, pointer.WorldY);

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

        if (_pinnedStep == stepId && _activeIndex == index && Transport.Tick == projection.Ticks[index] && !Transport.IsPlaying && !_startSelected)
        {
            return true;
        }

        ClearStart();

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

    /// <summary>Starts Set On Map for the active step's toolbar field. False when the step takes no place from the map.</summary>
    public bool BeginSetPlace()
    {
        if (PlaceTarget == StratPlaceTarget.None || ActiveStep is not { } step || ToolbarField(step) is not { } field || !BeginSetPlace(field))
        {
            return false;
        }

        _armed!.FromToolbar = true;
        return true;
    }

    /// <summary>
    ///     Starts Set On Map for one location field: its step (and line) becomes the selection, then the next map click
    ///     writes that field. False when the step is not editable on the path played or the field does not apply.
    /// </summary>
    /// <param name="field">The field the click writes.</param>
    public bool BeginSetPlace(StratLocationField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_session.Document is not { } document || IsReadOnly)
        {
            return false;
        }

        CancelSetPlace();
        if (field.Kind == StratLocationKind.Start)
        {
            if (field.Slot is { } startSlot && StartRefusal(startSlot) is { } refusal)
            {
                StatusLine = refusal;
                return false;
            }

            SelectStart();
            _armed = new ArmedPlace(document.Id, field, document.Map, 0, $"click the map for {field.Slot}'s start; Esc cancels");
            _ = PlacesFor(document.Map, true);
            StatusLine = _armed.Prompt;
            RaiseSetPlace();
            return true;
        }

        if (field.Slot is { } slot && !field.IsAllLines)
        {
            SelectLine(slot);
        }

        // Only a step that is not already active is selected: the toolbar's pick leaves the playhead inside its window.
        if ((ActiveStep?.Id != field.StepId && !SelectStep(field.StepId)) || EditableActiveStep() is null || ActiveStep is not { } step || step.Id != field.StepId
            || !StratLocationPatches.Applies(step, field))
        {
            return false;
        }

        _armed = new ArmedPlace(document.Id, field, document.Map, _activeIndex + 1,
            string.Create(CultureInfo.InvariantCulture, $"click the map for step {_activeIndex + 1}'s {FieldLabel(field, step)}; Esc cancels"));
        _ = PlacesFor(document.Map, true);
        StatusLine = _armed.Prompt;
        RaiseSetPlace();
        return true;
    }

    // What the toolbar toggle sets on a step: its to (the selected line's on a row shown apart, else every line's, which
    // is the step's own to without stored lines, as the compact field writes it), a lurk area, or its landing.
    private StratLocationField? ToolbarField(StratStep step) => PlaceTargetFor(step) switch
    {
        StratPlaceTarget.To => new StratLocationField(step.Id,
            WritesOneLine(step) && !WritesAllLines(step) ? LineSlotOn(step, _selectedSlot) : StratLocationField.AllLines,
            StratLocationKind.To),
        StratPlaceTarget.LurkArea => new StratLocationField(step.Id, null, StratLocationKind.LurkArea),
        StratPlaceTarget.Landing => new StratLocationField(step.Id, null, StratLocationKind.Landing),
        _ => null
    };

    private bool IsToolbarField(StratLocationField field, StratStep step) => ToolbarField(step) == field;

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

        if (_gate is not null)
        {
            _gate.Changed -= OnGateChanged;
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
                CancelOpenDrag();
                return InsertStep();
            case Playback2DAction.DuplicateStep:
                CancelOpenDrag();
                return DuplicateActiveStep();
            case Playback2DAction.DeleteStep:
                CancelOpenDrag();
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
                || TokenHitTest.Classify(marker.WorldX, marker.WorldY, marker.YawDegrees, world, worldRadius, ShowViewCones) is not { } hit)
            {
                continue;
            }

            // A disc beats a cone: cones overlap other tokens, and a press on a token must move it.
            double dx = world.X - marker.WorldX, dy = world.Y - marker.WorldY;
            double distance = dx * dx + dy * dy + (hit == TokenGrip.Heading ? 1e12 : 0);
            if (distance < best)
            {
                best = distance;
                slot = TokenSlots.All[marker.Slot];
                grip = hit;
            }
        }

        // A destination pin drags as its token does.
        if (best == double.MaxValue)
        {
            foreach (GuidePin pin in Guides.Pins)
            {
                double dx = world.X - pin.At.X, dy = world.Y - pin.At.Y;
                if (pane.Level.Contains(pin.At.Z) && dx * dx + dy * dy <= worldRadius * worldRadius * 2 && dx * dx + dy * dy < best)
                {
                    best = dx * dx + dy * dy;
                    slot = pin.At.Label;
                    grip = TokenGrip.Body;
                }
            }
        }

        return best < double.MaxValue;
    }

    /// <inheritdoc />
    public void BeginDrag(string slot, TokenGrip grip)
    {
        _drag = null;
        if (_projection is not { } projection || _session.Document is null)
        {
            StatusLine = StratDragTarget.NoStepNote;
            return;
        }

        // A press while playing stops where it is: the moment grabbed is the moment edited.
        if (Transport.IsPlaying)
        {
            Transport.Pause();
        }

        if (StratVocabulary.Slots.Contains(slot))
        {
            SelectLine(slot);
        }

        int tick = Transport.Tick;
        TokenKeyframe start = Tracks.Get(slot) is { } track && track.TrySample(tick, out TokenKeyframe at)
            ? at
            : new TokenKeyframe(tick, 0, 0, projection.Canvas.DefaultLevelMinZ ?? 0, 0);
        DragState drag = new(slot, grip, tick, start.X, start.Y, start.LevelMinZ, start.YawDegrees) { Pin = PinNextDrag };
        _drag = drag;
        Retarget(drag, projection);
        if (drag.Target.IsRefused)
        {
            StatusLine = drag.Target.Refusal ?? "";
        }

        Publish(false);
    }

    /// <inheritdoc />
    public void MoveTo(string slot, SKPoint world, double levelMinZ, ToolModifiers modifiers = ToolModifiers.None, double worldUnitsPerPixel = 0)
    {
        if (_drag is not { } drag || _projection is not { } projection || drag.Slot != slot)
        {
            return;
        }

        bool pin = PinNextDrag || (modifiers & ToolModifiers.Alt) != 0;
        if (pin != drag.Pin)
        {
            drag.Pin = pin;
            Retarget(drag, projection);
        }

        // A step edit while the drag is open moves the indices it writes by.
        if (!drag.Target.IsRefused && _session.Document is { } document && !StratDragPatches.Holds(document, drag.Target))
        {
            drag.Target = drag.Target with { Action = StratDragAction.Refused, Refusal = StepsChangedNote };
        }

        drag.Modifiers = modifiers;
        drag.WorldUnitsPerPixel = worldUnitsPerPixel;
        if (drag.Grip == TokenGrip.Heading)
        {
            drag.Yaw = (float)(Math.Atan2(world.Y - drag.StartY, world.X - drag.StartX) * 180 / Math.PI);
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

            drag.Moved = true;
        }

        if (drag.Target.IsRefused)
        {
            StatusLine = drag.Target.Refusal ?? "";
            DragLabel = drag.Target.Refusal ?? "";
            DragHint = "";
            Publish(false);
            return;
        }

        Preview(drag);
        Publish(false);
    }

    /// <inheritdoc />
    public void EndDrag(ToolModifiers modifiers = ToolModifiers.None)
    {
        if (_drag is not { } drag || _projection is not { } projection || _session.Document is not { } document)
        {
            EndDragState();
            return;
        }

        bool pin = PinNextDrag || (modifiers & ToolModifiers.Alt) != 0;
        if (pin != drag.Pin)
        {
            drag.Pin = pin;
            Retarget(drag, projection);
        }

        drag.Modifiers = modifiers;
        EndDragState();
        if ((!drag.Moved && !drag.Turned) || drag.Target.IsRefused)
        {
            if (drag.Target.IsRefused && (drag.Moved || drag.Turned))
            {
                StatusLine = drag.Target.Refusal ?? "";
            }

            RestoreTracks();
            ReprojectIfOriginsMoved();
            return;
        }

        StratDragTarget target = drag.Target;
        StratDrop drop = DropOf(drag);
        PinNextDrag = false;
        if (!StratDragPatches.Holds(document, target))
        {
            StatusLine = StepsChangedNote;
            RestoreTracks();
            ReprojectIfOriginsMoved();
            return;
        }

        string before = FieldText(document, target, drop);
        bool seen = StratDragPatches.ReplacesSeen(document, target);
        List<PatchOp> ops = StratDragPatches.Ops(document, target, drop, LegacyCarried(document), CurrentSpawns);
        if (ops.Count == 0)
        {
            StatusLine = target.Action is StratDragAction.Field or StratDragAction.Via
                ? $"{StepName(target)}{target.Slot}'s {FieldWord(document, target)} is already {before}"
                : "";
            RestoreTracks();
            ReprojectIfOriginsMoved();
            return;
        }

        Apply(ops);
        if (_session.Document is { } after && (target.StepIndex < after.Steps.Count || target.Action == StratDragAction.Start))
        {
            string now = FieldText(after, target, drop);
            StatusLine = target.Action switch
            {
                StratDragAction.Turn => $"{StepName(target)}{target.Slot}'s view angle is set. Ctrl+Z undoes",
                StratDragAction.Pin => $"{StepName(target)}{target.Slot} is pinned at {now}. Ctrl+Z undoes",
                StratDragAction.Opponent => $"{StepName(target)}{target.Slot} is at {now}. Ctrl+Z undoes",
                StratDragAction.Start when drop.YawDegrees is not null => $"{target.Slot}'s start facing is set. Ctrl+Z undoes",
                StratDragAction.Start => $"{target.Slot} now starts at {now}" + (before.Length > 0 && before != now ? $" (was {before})" : "") + ". Ctrl+Z undoes",
                _ => $"{StepName(target)}{target.Slot}'s {FieldWord(after, target)} is now {now}"
                     + (before.Length > 0 && before != now ? $" (was {before})" : "")
                     + (seen ? "; replaced the seen position" : "") + ". Ctrl+Z undoes"
            };
            MarkEarlier(target);
        }

        ReprojectIfOriginsMoved();
    }

    private const string StepsChangedNote = "the steps changed during the drag: nothing written";

    // A step key during a drag moves the indices the drag writes by: the drag ends unwritten first.
    private void CancelOpenDrag()
    {
        if (_drag is not null)
        {
            CancelDrag();
            StatusLine = "drag cancelled: the steps changed";
        }
    }

    // A drop whose run the playhead has already passed changes nothing on screen at this tick: a faint ring at where the
    // step now sends the token says the edit landed. Gone when the playhead moves.
    private void MarkEarlier(StratDragTarget target)
    {
        _earlier = null;
        if (_projection is not { } projection || target.PathIndex < 0 || target.PathIndex >= projection.Path.Count
            || projection.Tracks.FirstOrDefault(t => t.Slot == target.Slot) is not { } track)
        {
            return;
        }

        bool rotate = target.Field?.Kind == StratLocationKind.RotateTo;
        StratSceneProjection.RunSpan? first = projection.RunsOf(target.PathIndex, rotate)
            .Where(r => r.Slot == target.Slot).Select(r => r.Run).OrderBy(r => r.StartTick).FirstOrDefault();
        if (first is null || first.ArriveTick > Transport.Tick || !track.TrySample(first.ArriveTick, out TokenKeyframe at))
        {
            return;
        }

        _earlier = (Transport.Tick, new GuidePin(new GuideToken(at.X, at.Y, (float)at.MarkerZ, StratSceneProjection.TeamOf(_session.Document?.Side),
            target.Slot), [], true));
        Publish(false);
    }

    private (int Tick, GuidePin Pin)? _earlier;

    /// <inheritdoc />
    public void CancelDrag()
    {
        EndDragState();
        RestoreTracks();
        ReprojectIfOriginsMoved();
    }

    /// <summary>The words beside the pointer while a token is dragged: who, which field and what it becomes.</summary>
    [ObservableProperty]
    private string _dragLabel = "";

    /// <summary>The modifier keys under <see cref="DragLabel" />.</summary>
    [ObservableProperty]
    private string _dragHint = "";

    /// <summary>The toolbar's Pin: the next drag pins its token as Alt does, for a window manager that takes Alt+drag.</summary>
    [ObservableProperty]
    private bool _pinNextDrag;

    /// <summary>Whether a drag is open, for the label beside the pointer.</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>The target a drag would write now, for tests and the label; null with no drag.</summary>
    public StratDragTarget? DragTarget => _drag?.Target;

    partial void OnPinNextDragChanged(bool value)
    {
        if (_drag is { } drag && _projection is { } projection && drag.Pin != value)
        {
            drag.Pin = value;
            Retarget(drag, projection);
            Preview(drag);
            Publish(false);
        }
    }

    private void EndDragState()
    {
        _drag = null;
        DragLabel = "";
        DragHint = "";
        OnPropertyChanged(nameof(IsDragging));
        OnPropertyChanged(nameof(DragTarget));
        RaiseSetPlace();
        OnPropertyChanged(nameof(IsToolActive));
    }

    // Who the drag writes, read once at the press and again when Alt or Pin flips. The step it writes becomes the
    // selection only when it owns the playhead's tick: selecting another would move the playhead.
    private void Retarget(DragState drag, StratSceneProjection projection)
    {
        drag.Target = StratDragTarget.Resolve(projection, drag.Tick, _activeIndex, drag.Slot, drag.Grip, drag.Pin, _startSelected);
        if (drag.Target.Action == StratDragAction.Start && SpawnsPending)
        {
            drag.Target = drag.Target with { Action = StratDragAction.Refused, Refusal = StratDragTarget.SpawnsPendingNote, Field = null };
        }

        if (drag.Target is { IsRefused: false, PathIndex: var index } && index >= 0 && index != _activeIndex && projection.Ticks[index] == Transport.Tick)
        {
            _pinnedStep = projection.Path[index].Step.Id;
            UpdateActiveStep();
        }

        drag.ShownField = ShownField(drag.Target);
        OnPropertyChanged(nameof(IsDragging));
        OnPropertyChanged(nameof(IsToolActive));
        OnPropertyChanged(nameof(DragTarget));
        RaiseSetPlace();
    }

    // The field as the row shows it, so the row lights the field the drop will change: a compact row's one field
    // stands for every line.
    private StratLocationField? ShownField(StratDragTarget target)
    {
        if (target.Action == StratDragAction.Start)
        {
            return target.Field;
        }

        if (target.Field is not { } field || _session.Document is not { } document || target.StepIndex < 0 || target.StepIndex >= document.Steps.Count)
        {
            return null;
        }

        if (field.Slot is null || field.IsAllLines)
        {
            return field;
        }

        StratStep step = document.Steps[target.StepIndex];
        List<StepAssignment> lines = StratLinePatches.Copy(step, true);
        bool apart = LinesShownApart?.Invoke(step.Id) == true || (lines.Count > 1 && !StratLinePatches.Agree(lines));
        return apart && !target.Joins ? field : field with { Slot = StratLocationField.AllLines };
    }

    // The drop as the field writers take it: the place under it, snapped to the place alone near its centre, or the
    // point alone with Shift.
    private StratDrop DropOf(DragState drag)
    {
        string? map = _session.Document?.Map;
        IZonePlaceResolver? zones = map is not null && _places is { IsCompletedSuccessfully: true, Result: { } loaded }
                                                    && string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
            ? loaded
            : null;
        bool pointOnly = (drag.Modifiers & ToolModifiers.Shift) != 0;
        string? place = zones?.ResolveOnFloor(drag.X, drag.Y, drag.LevelMinZ);
        bool snapped = false;
        if (place is not null && !pointOnly && zones?.PlaceArrival(place, drag.LevelMinZ) is { } centre)
        {
            double reach = SnapPixels * (drag.WorldUnitsPerPixel > 0 ? drag.WorldUnitsPerPixel : 1);
            double dx = centre.X - drag.X, dy = centre.Y - drag.Y;
            snapped = dx * dx + dy * dy <= reach * reach;
        }

        double? yaw = drag.Target.Action switch
        {
            StratDragAction.Turn => drag.Yaw,
            StratDragAction.Opponent when drag.Turned => drag.Yaw,
            StratDragAction.Start when drag.Grip == TokenGrip.Heading && drag.Turned => drag.Yaw,
            _ => null
        };
        return drag.Grip == TokenGrip.Heading
            ? new StratDrop(null, drag.StartX, drag.StartY, drag.StartLevelMinZ, zones is not null, YawDegrees: yaw)
            : new StratDrop(place, drag.X, drag.Y, drag.LevelMinZ, zones is not null, snapped, pointOnly, yaw);
    }

    /// <summary>On screen, how near a place's arrival centre a drop stores the place alone.</summary>
    public const double SnapPixels = 12;

    // The ops the drop would write, applied to a copy and projected, so the ghost route and the label show what the
    // release stores, not the pointer.
    private void Preview(DragState drag)
    {
        if (_session.Document is not { } document || _projection is not { } projection)
        {
            return;
        }

        StratDrop drop = DropOf(drag);
        List<PatchOp> ops = StratDragPatches.Ops(document, drag.Target, drop, LegacyCarried(document), CurrentSpawns);
        StratSceneProjection preview = projection;
        StratDocument shown = document;
        if (ops.Count > 0)
        {
            try
            {
                shown = StratHistory.Apply(document, ops);
                preview = Project(shown, _projectedRouting);
            }
            catch (InvalidOperationException)
            {
                // The ghost shows nothing new; the release reports what did not apply.
            }
        }

        drag.Preview = preview;
        drag.Drop = drop;
        drag.Outline = drop is { Place: { } place, PointOnly: false } && _places is { IsCompletedSuccessfully: true, Result: { } zones }
            ? zones.OutlineOf(place, drag.LevelMinZ)
            : null;
        string value = FieldText(shown, drag.Target, drop);
        DragLabel = drag.Target.Action switch
        {
            StratDragAction.Turn => $"{StepName(drag.Target)}{drag.Target.Slot} · view angle",
            StratDragAction.Pin => $"{StepName(drag.Target)}{drag.Target.Slot} · pinned: {value}",
            StratDragAction.Opponent => $"{StepName(drag.Target)}{drag.Target.Slot} · at: {value}",
            StratDragAction.Start => $"{drag.Target.Slot} · start: {value}",
            _ => $"{StepName(drag.Target)}{Who(document, drag.Target)} · {FieldWord(shown, drag.Target)}: {value}"
        };
        DragHint = drag.Grip == TokenGrip.Heading
            ? "Esc: cancel"
            : drag.Target.Action == StratDragAction.Opponent
                ? "Esc: cancel"
                : drag.Target.Action == StratDragAction.Start
                    ? "Shift: point only   Esc: cancel"
                : drag.Pin
                    ? "pinning: release to pin   Esc: cancel"
                    : $"Shift: point only   Alt: pin {drag.Slot} here   Esc: cancel";
    }

    // "step 3 · " when the drag writes a step other than the selected one.
    private string StepName(StratDragTarget target) =>
        target.PathIndex >= 0 && target.PathIndex != _activeIndex
            ? string.Create(CultureInfo.InvariantCulture, $"step {target.PathIndex + 1} · ")
            : "";

    // Who the field moves: every lurker of the step for a lurk area, else the dragged token.
    private static string Who(StratDocument document, StratDragTarget target) =>
        target.Field is { Kind: StratLocationKind.LurkArea } && target.StepIndex < document.Steps.Count
            ? string.Join(", ", StratLinePatches.Copy(document.Steps[target.StepIndex], true).Select(l => l.Slot).DefaultIfEmpty(target.Slot))
            : target.Slot;

    // The field in the row's own words: to, at, site, via +, lurk area 2, rotate to.
    private static string FieldWord(StratDocument document, StratDragTarget target)
    {
        StratStep? step = target.StepIndex < document.Steps.Count ? document.Steps[target.StepIndex] : null;
        return target.Field?.Kind switch
        {
            StratLocationKind.Via => "via +",
            StratLocationKind.RotateTo => "rotate to",
            StratLocationKind.LurkArea => "lurk area 1",
            _ => StratStepFields.ToLabel(step?.Verb)
        };
    }

    // What the target's field reads in the document: the entry the drop put first for a list, the slot's position for a pin.
    private string FieldText(StratDocument document, StratDragTarget target, StratDrop? drop)
    {
        if (target.Action == StratDragAction.Start)
        {
            return StratStartBlock.LocationOf(document, target.Slot, CurrentSpawns) is { } start ? StratLocations.Text(start, _callouts) ?? "" : "";
        }

        if (target.StepIndex < 0 || target.StepIndex >= document.Steps.Count)
        {
            return "";
        }

        StratStep step = document.Steps[target.StepIndex];
        if (target.Action is StratDragAction.Pin or StratDragAction.Opponent)
        {
            return step.Positions.LastOrDefault(p => p.Slot == target.Slot) is { } position ? StratLocations.PointText(position.X, position.Y) : "";
        }

        if (target.Field is not { } field)
        {
            return "";
        }

        IReadOnlyList<PlaceRef> value = StratLocationPatches.Read(step, field);
        if (field.Kind == StratLocationKind.Via && drop is not null)
        {
            PlaceRef entry = StratDragPatches.Entry(null, drop, true);
            value = [.. value.Where(e => StratLocationPatches.SameEntry(e, entry))];
        }

        return value.Count == 0 ? "" : StratLocations.Text(value[0], _callouts) ?? "";
    }

    // While dragging: the hollow start, the dashed route the drop stores, the place under the pointer and the step's vias.
    // Paused otherwise: the selected step's destination pins.
    private SceneGuides GuidesFor(int tick)
    {
        if (_projection is not { } projection || Transport.IsPlaying || IsReadOnly)
        {
            return SceneGuides.None;
        }

        int team = StratSceneProjection.TeamOf(_session.Document?.Side);
        if (_drag is not { } drag)
        {
            SceneGuides selected = _activeIndex >= 0 ? PinsGuides(projection, _activeIndex, tick, team, null) : SceneGuides.None;
            if (_earlier is { } earlier && earlier.Tick == tick)
            {
                return new SceneGuides { Pins = [.. selected.Pins, earlier.Pin], ViaMarks = selected.ViaMarks };
            }

            _earlier = null;
            return selected;
        }

        int slotTeam = TokenSlots.IsOpponent(drag.Slot) ? (team == 2 ? 3 : 2) : team;
        GuideToken ghost = new(drag.StartX, drag.StartY, (float)(drag.StartLevelMinZ + MapSpace.LevelQuantum / 2), slotTeam, drag.Slot);
        if (drag.Target.IsRefused || !drag.Moved || drag.Grip == TokenGrip.Heading)
        {
            return new SceneGuides { Ghost = drag.Moved ? ghost : null };
        }

        StratSceneProjection preview = drag.Preview ?? projection;
        SceneGuides pins = drag.Target.PathIndex >= 0 && drag.Target.PathIndex < preview.Path.Count
            ? PinsGuides(preview, drag.Target.PathIndex, tick, team, drag.Slot)
            : SceneGuides.None;
        List<GrenadeTrailPoint> route = RouteOf(preview, drag.Target, drag.Slot) ?? [];
        if (route.Count < 2)
        {
            route = [new GrenadeTrailPoint(drag.StartX, drag.StartY, ghost.Z), new GrenadeTrailPoint(drag.X, drag.Y, (float)(drag.LevelMinZ + MapSpace.LevelQuantum / 2))];
        }

        return new SceneGuides
        {
            Ghost = ghost,
            GhostRoute = route,
            GhostTeam = slotTeam,
            DropOutline = drag.Outline ?? [],
            DropOutlineZ = (float)(drag.LevelMinZ + MapSpace.LevelQuantum / 2),
            ViaMarks = pins.ViaMarks
        };
    }

    // The way the token goes for the step the drag writes, in the preview: its run from where it leaves to its arrival.
    private static List<GrenadeTrailPoint>? RouteOf(StratSceneProjection preview, StratDragTarget target, string slot)
    {
        // A start has no step: the way shown is the token's first run from the new start.
        if (target.Action == StratDragAction.Start)
        {
            StratSceneProjection.RunSpan? first = Enumerable.Range(0, preview.Path.Count)
                .SelectMany(i => preview.RunsOf(i)).Where(r => r.Slot == slot).Select(r => r.Run).OrderBy(r => r.StartTick).FirstOrDefault();
            return first is not null && preview.Tracks.FirstOrDefault(t => t.Slot == slot) is { } startTrack
                ? Polyline(startTrack, 0, first.ArriveTick)
                : null;
        }

        bool rotate = target.Field?.Kind == StratLocationKind.RotateTo;
        foreach ((string runSlot, StratSceneProjection.RunSpan run) in preview.RunsOf(target.PathIndex, rotate))
        {
            if (runSlot == slot && preview.Tracks.FirstOrDefault(t => t.Slot == slot) is { } track)
            {
                return Polyline(track, run.StartTick, run.ArriveTick);
            }
        }

        return null;
    }

    private static List<GrenadeTrailPoint> Polyline(TokenTrack track, int from, int until)
    {
        List<GrenadeTrailPoint> points = [];
        if (track.TrySample(from, out TokenKeyframe start))
        {
            points.Add(new GrenadeTrailPoint(start.X, start.Y, (float)start.MarkerZ));
        }

        foreach (TokenKeyframe key in track.Keyframes)
        {
            if (key.Tick > from && key.Tick <= until)
            {
                points.Add(new GrenadeTrailPoint(key.X, key.Y, (float)key.MarkerZ));
            }
        }

        return points;
    }

    // Where a step sends each token it moves, while the token has not arrived: a hollow ring at the arrival and the
    // way there; and its vias as marks. Only the dragged token's pin while it is dragged.
    private SceneGuides PinsGuides(StratSceneProjection projection, int pathIndex, int tick, int team, string? only)
    {
        List<GuidePin> pins = [];
        List<GrenadeTrailPoint> vias = [];
        StratStep step = projection.Path[pathIndex].Step;
        foreach ((string slot, StratSceneProjection.RunSpan run) in projection.RunsOf(pathIndex))
        {
            if ((only is not null && slot != only) || run.ArriveTick <= tick
                                                   || projection.Tracks.FirstOrDefault(t => t.Slot == slot) is not { } track
                                                   || !track.TrySample(run.ArriveTick, out TokenKeyframe arrive))
            {
                continue;
            }

            pins.Add(new GuidePin(new GuideToken(arrive.X, arrive.Y, (float)arrive.MarkerZ, team, slot),
                Polyline(track, Math.Max(tick, run.StartTick), run.ArriveTick)));
            if (CurrentPlaceArrivals is { } arrivals)
            {
                foreach (PlaceRef via in StratStepLines.ViaFor(step, slot))
                {
                    (double X, double Y, double Level)? at = StratLocations.HasPoint(via)
                        ? (via.X!.Value, via.Y!.Value, via.LevelMinZ ?? arrive.LevelMinZ)
                        : StratLocations.HasPlace(via) && arrivals(via.Place!, arrive.LevelMinZ) is { } a ? (a.X, a.Y, a.LevelMinZ) : null;
                    if (at is { } p)
                    {
                        vias.Add(new GrenadeTrailPoint((float)p.X, (float)p.Y, (float)(p.Level + MapSpace.LevelQuantum / 2)));
                    }
                }
            }
        }

        return pins.Count == 0 && vias.Count == 0 ? SceneGuides.None : new SceneGuides { Pins = pins, ViaMarks = vias };
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

    private const string ReadOnlyNote = StratDragTarget.ReadOnlyNote;

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

    // The routing feature flipped in settings: reproject only when it changes what the canvas draws.
    private void OnGateChanged(object? sender, EventArgs e) =>
        _post(() =>
        {
            if (!_disposed && _routing() != _projectedRouting)
            {
                OnProjectionInputChanged();
            }
        });

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
            if (places is { IsCompletedSuccessfully: true, Result: not null })
            {
                _post(() => RaisePlacesLoaded(map));
            }
            else if (!places.IsCompleted)
            {
                places.ContinueWith(t =>
                {
                    if (t is { IsCompletedSuccessfully: true, Result: not null })
                    {
                        _post(() => OnPlacesLoaded(map));
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        Guid? pinned = _pinnedStep;

        WatchSpawns(document);
        RefreshPathOptions(document);
        _projectedRouting = _routing();
        StratSceneProjection projection = Project(document, _projectedRouting);
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
            projection.Utility, projection.RoundSeconds, 0, projection.ContentEndTick, StepSchedule.TicksPerSecond, 1)
        {
            Routes = projection.Routed,
            CountsUp = projection.CountsUp
        });

        // With a start the tokens stand somewhere before step 1, so the clock runs from it.
        int first = projection.HasStart || projection.Ticks.Count == 0 ? 0 : projection.Ticks[0];
        Transport.SetRange(first, projection.ContentEndTick);

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
        Timeline.Rebuild(new StratTimelineData(projection.ContentEndTick));

        StatusLine = projection.ClockClamped
            ? "a step's time runs backwards: it plays at the step before it until the table is fixed"
            : "";
        UpdateActiveStep();
        if (_armed is { Field.Kind: StratLocationKind.Start } startPick)
        {
            if (!_startSelected)
            {
                _armed = null;
            }
            else if (!startPick.Pending)
            {
                StatusLine = startPick.Prompt;
            }
        }
        else if (_armed is { } armed && (ActiveStep is not { } armedStep || armedStep.Id != armed.StepId
                                    || (armed.FromToolbar ? ToolbarField(armedStep) != armed.Field : !StratLocationPatches.Applies(armedStep, armed.Field))))
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

    // The selected path of a document, projected with this canvas's resolvers: the live one, and a drag's preview.
    private StratSceneProjection Project(StratDocument document, bool routing, bool mainLine = false)
    {
        IReadOnlyList<StratPathStep> path = !mainLine && SelectedPath?.BranchId is { } branch
            ? StratPath.Through(document, branch, _lookup) ?? StratPath.MainLine(document)
            : StratPath.MainLine(document);

        Func<double, double> levelFor = StratFromRound.FloorLevelKeys(MapAsset?.Floors);
        return StratSceneProjection.Build(document, path,
            _lineupOrigins is { } origins ? (map, utility) => origins.Resolve(map, utility, levelFor) : null,
            PlaceCentres(document.Map), PlaceArrivals(document.Map), PlaceContains(document.Map),
            _lineupOrigins is { } flights ? (map, utility) => flights.ResolveFlight(map, utility, levelFor) : null,
            routing ? Paths(document.Map) : null, SpawnsFor(document.Map));
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
        double atSeconds = StratClock.AtSecondsAtTick(projection.ClockInfo, Transport.Tick);
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
        if (_startSelected && Transport.Tick != 0)
        {
            ClearStart();
        }

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
            if (_armed is { } armed && armed.StepId != id && armed.Field.Kind != StratLocationKind.Start)
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
                Markers = DraggedMarkers(pooled.Markers),
                AreaEffects = [.. pooled.AreaEffects],
                Trails = CopyTrails(pooled.Trails),
                Routes = CopyRoutes(pooled.Routes),
                GameInfo = pooled.GameInfo,
                Map = pooled.Map
            };
        }
        else
        {
            CurrentFrame = Scene2DFrame.Empty;
        }

        Guides = GuidesFor(tick);
        Timeline.UpdatePlayhead(tick, tick);
        RaiseState();
        FrameUpdated?.Invoke();
    }

    // The dragged token follows the pointer, or turns with it; the track is not touched until the drop.
    private List<PlayerMarker> DraggedMarkers(IReadOnlyList<PlayerMarker> pooled)
    {
        List<PlayerMarker> markers = [.. pooled];
        if (_drag is not { } drag || drag.Target is null || drag.Target.IsRefused || (!drag.Moved && !drag.Turned))
        {
            return markers;
        }

        int order = TokenSlots.OrderOf(drag.Slot);
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Slot == order)
            {
                markers[i] = drag.Grip == TokenGrip.Heading
                    ? markers[i] with { YawDegrees = drag.Yaw }
                    : markers[i] with
                    {
                        WorldX = drag.X, WorldY = drag.Y, WorldZ = (float)(drag.LevelMinZ + MapSpace.LevelQuantum / 2)
                    };
            }
        }

        return markers;
    }

    // A pooled trail is refilled two frames on while the render thread may still hold this one.
    private static List<GrenadeTrail> CopyTrails(IReadOnlyList<GrenadeTrail> pooled)
    {
        List<GrenadeTrail> copies = new(pooled.Count);
        foreach (GrenadeTrail trail in pooled)
        {
            GrenadeTrail copy = new() { Kind = trail.Kind, Team = trail.Team, LastTick = trail.LastTick, Alpha = trail.Alpha };
            copy.Points.AddRange(trail.Points);
            copies.Add(copy);
        }

        return copies;
    }

    private static List<TokenRouteLine> CopyRoutes(IReadOnlyList<TokenRouteLine> pooled)
    {
        List<TokenRouteLine> copies = new(pooled.Count);
        foreach (TokenRouteLine route in pooled)
        {
            TokenRouteLine copy = new() { Team = route.Team };
            copy.Points.AddRange(route.Points);
            copies.Add(copy);
        }

        return copies;
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
        if (armed.Field.Kind == StratLocationKind.Start)
        {
            FinishStartPlace(armed, places, x, y, levelMinZ);
            return;
        }

        int index = _session.Document is { } open && open.Id == armed.StratId ? open.Steps.FindIndex(s => s.Id == armed.StepId) : -1;
        if (index < 0 || _session.Document is not { } document)
        {
            StatusLine = "";
            return;
        }

        IZonePlaceResolver? zones = places.IsCompletedSuccessfully ? places.Result : null;
        string? place = zones?.ResolveOnFloor(x, y, levelMinZ);
        StratStep step = document.Steps[index];
        if (!StratLocationPatches.Applies(step, armed.Field))
        {
            StatusLine = "";
            return;
        }

        string label = FieldLabel(armed.Field, step);
        string number = armed.Number.ToString(CultureInfo.InvariantCulture);

        // A miss stores the point alone; without zones there is no answer about the place, so a stored one stays.
        List<PatchOp> ops = StratLocationPatches.Pick(document, index, armed.Field, place, x, y, levelMinZ, zones is not null);
        Apply(ops);
        string shown = place is not null ? Display(place) : StratLocations.PointText(x, y);
        StatusLine = zones is null
            ? $"no places for this map: step {number}'s {label} is the point {StratLocations.PointText(x, y)}, its place kept"
            : ops.Count == 0
                ? $"step {number}: {label} is already {shown}"
                : place is null
                    ? $"no place there: step {number}'s {label} is the point {shown}"
                    : $"step {number}: {label} is now {shown}";
    }

    // A start pick: the place under the click and the point, as a map click stores a single field.
    private void FinishStartPlace(ArmedPlace armed, Task<IZonePlaceResolver?> places, double x, double y, double levelMinZ)
    {
        if (_session.Document is not { } document || document.Id != armed.StratId || armed.Field.Slot is not { } slot)
        {
            StatusLine = "";
            return;
        }

        if (StartRefusal(slot) is { } refusal)
        {
            StatusLine = refusal;
            return;
        }

        IZonePlaceResolver? zones = places.IsCompletedSuccessfully ? places.Result : null;
        string? place = zones?.ResolveOnFloor(x, y, levelMinZ);
        PlaceRef picked = StratLocations.Picked(StratStartBlock.LocationOf(document, slot, CurrentSpawns), place, x, y, levelMinZ, zones is not null);
        List<PatchOp> ops = StratStartBlock.WriteLocation(document, slot, picked, LegacyCarried(document), spawns: CurrentSpawns);
        Apply(ops);
        string shown = place is not null ? Display(place) : StratLocations.PointText(x, y);
        StatusLine = ops.Count == 0 ? $"{slot} already starts at {shown}" : $"{slot} now starts at {shown}";
    }

    /// <summary>
    ///     The entries an older file reads as carried copies, for the first start write to mark
    ///     (<see cref="StratStartBlock.Ops" />).
    /// </summary>
    /// <param name="document">The strat.</param>
    internal IReadOnlyList<StartSource> LegacyCarried(StratDocument document)
    {
        Func<double, double> levelFor = StratFromRound.FloorLevelKeys(MapAsset?.Floors);
        return StratSceneProjection.LegacyCarriedEntries(document,
            _lineupOrigins is { } origins ? (map, utility) => origins.Resolve(map, utility, levelFor) : null, SpawnsFor(document.Map));
    }

    /// <summary>The open map's spawns once read, for an older strat's start; null before, or with none.</summary>
    public StratSpawns? CurrentSpawns => _session.Document is { } document ? SpawnsFor(document.Map) : null;

    private StratSpawns? SpawnsFor(string map) =>
        _spawns is { IsCompletedSuccessfully: true, Result: { } spawns } && string.Equals(_spawnsMap, map, StringComparison.OrdinalIgnoreCase)
            ? spawns
            : null;

    // An older strat's start can take the spawns: reproject once they are read, as for the places. Asked on every
    // projection, so a strat opened while the read is in flight is still redrawn; the redraw goes to whatever strat is
    // open when they land, and only when that one takes them.
    private void WatchSpawns(StratDocument document)
    {
        if (_spawnsFor is null)
        {
            return;
        }

        string map = document.Map;
        if (!string.Equals(_spawnsMap, map, StringComparison.OrdinalIgnoreCase))
        {
            _spawnsMap = map;
            _spawnsWatched = false;
            try
            {
                _spawns = _spawnsFor(map);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _spawns = null;
            }
        }

        if (_spawns is not { IsCompleted: false } pending || _spawnsWatched || !NeedsSpawns(document))
        {
            return;
        }

        _spawnsWatched = true;
        pending.ContinueWith(_ => _post(() =>
        {
            if (!_disposed && ReferenceEquals(_spawns, pending) && _session.Document is { } open
                && string.Equals(open.Map, map, StringComparison.OrdinalIgnoreCase) && NeedsSpawns(open))
            {
                OnProjectionInputChanged();
                PlacesLoaded?.Invoke();
            }
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private bool _spawnsWatched;

    // A strat whose start the spawns can fill: an older file, not a capture, beginning at the start.
    private static bool NeedsSpawns(StratDocument document) =>
        document is { Start: null, Steps.Count: > 0 } && !StratSceneProjection.IsCaptured(document)
                                                      && StratClock.StratTickOf(document.Clock, document.Steps[0].AtSeconds) == 0;

    /// <summary>
    ///     Whether the open strat's start waits on the map's spawns, which are still being read: its first start write
    ///     would bake in whatever had loaded, so start edits wait too.
    /// </summary>
    public bool SpawnsPending =>
        _session.Document is { } document && _spawnsFor is not null && NeedsSpawns(document)
        && (!string.Equals(_spawnsMap, document.Map, StringComparison.OrdinalIgnoreCase) || _spawns is { IsCompleted: false });

    /// <summary>Why a token's start cannot be edited now, or null: the spawns are still being read, or a step on the start's tick places it.</summary>
    /// <param name="slot">The token.</param>
    public string? StartRefusal(string slot) =>
        SpawnsPending ? StratDragTarget.SpawnsPendingNote
        : _projection?.StartShadowedBy(slot) is { } step ? StratDragTarget.ShadowedNote(slot, step)
        : null;

    private void ClearStart()
    {
        if (_startSelected)
        {
            _startSelected = false;
            OnPropertyChanged(nameof(IsStartSelected));
        }
    }

    // The field as the row names it: "“to”", "C's “at”", "landing", "“from”", "B's watching".
    private static string FieldLabel(StratLocationField field, StratStep step)
    {
        string who = field.Slot is { } slot && slot != StratLocationField.AllLines
                                            && (field.Kind is StratLocationKind.Watch or StratLocationKind.Via || StratStepLines.HasLines(step))
            ? slot + "'s "
            : "";
        return who + field.Kind switch
        {
            StratLocationKind.From => "“from”",
            StratLocationKind.Landing => "landing",
            StratLocationKind.Watch => "watching",
            StratLocationKind.LurkArea => "“lurk area”",
            StratLocationKind.RotateTo => "“rotate to”",
            StratLocationKind.Via => "“via”",
            _ => "“" + StratStepFields.ToLabel(step.Verb) + "”"
        };
    }

    /// <summary>
    ///     Whether the row shows the step's lines as one (they agree and it is not split), so a map click sets every
    ///     line's place; null <see cref="LinesShownApart" /> reads agreement alone.
    /// </summary>
    private bool WritesAllLines(StratStep step) =>
        step.Assignments is { Count: > 1 } lines && StratLinePatches.Agree(lines) && LinesShownApart?.Invoke(step.Id) != true;

    // A map click sets one player's place: the step stores lines, or the editor shows a step for everyone split.
    private bool WritesOneLine(StratStep step) =>
        StratStepLines.HasLines(step)
        || (string.Equals(step.Actor, StratVocabulary.ActorAll, StringComparison.Ordinal) && LinesShownApart?.Invoke(step.Id) == true);

    /// <summary>The place under a point on a floor, from the open map's zones; null in no place or before they load.</summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="levelMinZ">The floor's level key.</param>
    public string? PlaceAt(double x, double y, double levelMinZ) =>
        LoadedZones() is { } zones ? zones.ResolveOnFloor(x, y, levelMinZ) : null;

    /// <summary>The zip check against this canvas's places and routes (<see cref="StratDepartureCheck" />).</summary>
    /// <param name="document">The strat.</param>
    public IReadOnlyList<StratIssue> DepartureChecks(StratDocument document) =>
        StratDepartureCheck.Check(document, d => Project(d, _routing(), true));

    /// <summary>Whether the open map's zones are in memory, so <see cref="PlaceAt" />'s null means no place.</summary>
    public bool HasPlaces => LoadedZones() is not null;

    private IZonePlaceResolver? LoadedZones() =>
        _session.Document is { } document && _places is { IsCompletedSuccessfully: true, Result: { } zones }
                                          && string.Equals(_placesMap, document.Map, StringComparison.OrdinalIgnoreCase)
            ? zones
            : null;

    /// <summary>Whether the editor shows a step's lines one per player; the tab wires it to the editor.</summary>
    public Func<Guid, bool>? LinesShownApart { get; set; }

    /// <summary>Re-reads the Set On Map words after the editor shows a step's lines differently.</summary>
    public void RefreshSetPlace() => RaiseSetPlace();

    private string Display(string place) => _callouts is { } callouts && callouts.IsCanonical(place) ? callouts.Display(place) : place;

    // Only once the map's zones are in memory; until then a watching token keeps its authored facing.
    /// <summary>Where a rotating lurker arrives, or null until the map's zones are in memory.</summary>
    public PlaceArrivalResolver? CurrentPlaceArrivals => _session.Document is { } document ? PlaceArrivals(document.Map) : null;

    private PlaceArrivalResolver? PlaceArrivals(string map) =>
        _places is { IsCompletedSuccessfully: true, Result: { } zones } && string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
            ? zones.PlaceArrival
            : null;

    private PlaceContainsResolver? PlaceContains(string map) =>
        _places is { IsCompletedSuccessfully: true, Result: { } zones } && string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
            ? (place, x, y, level) => string.Equals(zones.ResolveOnFloor(x, y, level), place, StringComparison.Ordinal)
            : null;

    private PathResolver? Paths(string map) =>
        _places is { IsCompletedSuccessfully: true, Result: { } zones } && string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
            ? zones.Paths
            : null;

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
            PlacesLoaded?.Invoke();
        }
    }

    private void RaisePlacesLoaded(string map)
    {
        if (!_disposed && string.Equals(_session.Document?.Map, map, StringComparison.OrdinalIgnoreCase))
        {
            PlacesLoaded?.Invoke();
        }
    }

    /// <summary>Raised on the UI thread when the open map's zones are in memory: place checks can run.</summary>
    public event Action? PlacesLoaded;

    /// <summary>The open map's places from its loaded zones, for the place warnings; null until they are loaded.</summary>
    /// <param name="map">The map.</param>
    public CalloutResolver? LoadedPlaces(string map)
    {
        if (_places is not { IsCompletedSuccessfully: true, Result: { } zones }
            || !string.Equals(_placesMap, map, StringComparison.OrdinalIgnoreCase)
            || zones.PlaceNames is not { Count: > 0 } names)
        {
            return null;
        }

        if (!ReferenceEquals(_placeNamesFrom, zones))
        {
            _placeNamesFrom = zones;
            _placeNames = new CalloutResolver(names);
        }

        return _placeNames;
    }

    private IZonePlaceResolver? _placeNamesFrom;
    private CalloutResolver? _placeNames;

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
    // One read in flight per (map, source) across every canvas (the editor's and a Detected preview's) that
    // shares the same source, so the queue shows one; a different source never waits on another's read.
    private static Task<IZonePlaceResolver?> QueuedPlaces(string map, IZonePlaceResolverSource? resolverSource)
    {
        (string Map, IZonePlaceResolverSource? Source) key = (map.ToLowerInvariant(), resolverSource);
        lock (_placesGate)
        {
            if (_placesInFlight.TryGetValue(key, out Task<IZonePlaceResolver?>? running) && !running.IsCompleted)
            {
                return running;
            }

            Task<IZonePlaceResolver?> read = QueueWork.RunAsync(QueueWork.Ambient, QueueJobKind.SectionCompute,
                "Strat places: " + map, "Strat Book",
                () => (resolverSource ?? NoZonePlaceResolverSource.Instance).TryGet(map),
                null, DemoJobPriority.UserRequested);
            _placesInFlight[key] = read;
            return read;
        }
    }

    private static readonly Lock _placesGate = new();
    private static readonly Dictionary<(string Map, IZonePlaceResolverSource? Source), Task<IZonePlaceResolver?>> _placesInFlight = new();

    private void RaiseSetPlace()
    {
        OnPropertyChanged(nameof(PlaceTarget));
        OnPropertyChanged(nameof(CanSetPlace));
        OnPropertyChanged(nameof(IsSettingPlace));
        OnPropertyChanged(nameof(ArmedField));
        OnPropertyChanged(nameof(SetPlaceText));
        OnPropertyChanged(nameof(SetPlaceToolTip));
        OnPropertyChanged(nameof(IsToolActive));
        OnPropertyChanged(nameof(SelectedLineSlot));
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

    // Set On Map waiting for its click. Pending once the click is in and the place lookup has not answered.
    private sealed class ArmedPlace(Guid stratId, StratLocationField field, string map, int number, string prompt)
    {
        public string Prompt { get; } = prompt;
        public StratLocationField Field { get; } = field;
        public Guid StratId { get; } = stratId;
        public Guid StepId => Field.StepId;
        public string Map { get; } = map;
        public int Number { get; } = number;
        public bool Pending { get; set; }

        // Armed by the toolbar toggle: it ends when the toolbar would pick another field, as the toggle always did.
        public bool FromToolbar { get; set; }
    }

    // A drag's working state. A class, not a struct: MoveTo updates it in place across forty samples.
    private sealed class DragState(string slot, TokenGrip grip, int tick, float x, float y, double levelMinZ, float yaw)
    {
        public string Slot { get; } = slot;
        public TokenGrip Grip { get; } = grip;
        public int Tick { get; } = tick;
        public float StartX { get; } = x;
        public float StartY { get; } = y;
        public double StartLevelMinZ { get; } = levelMinZ;
        public float X { get; set; } = x;
        public float Y { get; set; } = y;
        public double LevelMinZ { get; set; } = levelMinZ;
        public float Yaw { get; set; } = yaw;
        public bool Moved { get; set; }
        public bool Turned { get; set; }
        public bool Pin { get; set; }
        public ToolModifiers Modifiers { get; set; }
        public double WorldUnitsPerPixel { get; set; }
        public StratDragTarget Target { get; set; } = null!;
        public StratLocationField? ShownField { get; set; }
        public StratSceneProjection? Preview { get; set; }
        public StratDrop? Drop { get; set; }
        public IReadOnlyList<(System.Numerics.Vector2 A, System.Numerics.Vector2 B)>? Outline { get; set; }
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
    Landing,

    /// <summary>A place added to the step's lurk areas.</summary>
    LurkArea
}

/// <summary>A path the canvas can play: the main line, or the one through a branch.</summary>
/// <param name="BranchId">The branch, or null for the main line.</param>
/// <param name="Label">What the picker shows.</param>
public sealed record StratPathOption(Guid? BranchId, string Label)
{
    public static StratPathOption MainLine { get; } = new(null, "main line");

    public override string ToString() => Label;
}
