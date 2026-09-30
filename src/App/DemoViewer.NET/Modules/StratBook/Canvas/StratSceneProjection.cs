#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>One step on a path, and where it lives: this strat, or the strat a branch continues in.</summary>
/// <param name="Step">The step as stored.</param>
/// <param name="StratId">The strat the step belongs to.</param>
/// <param name="StepIndex">Its index in that strat's <c>steps[]</c>, which is what an op's pointer names.</param>
/// <param name="Editable">
///     Whether the canvas may write it: true for the open strat's own steps. Another strat's steps play
///     but are not edited from here, since that document has its own session and single writer.
/// </param>
public sealed record StratPathStep(StratStep Step, Guid StratId, int StepIndex, bool Editable);

/// <summary>
///     Which steps the canvas plays (step-authoring.md §3.4). A branch adds no second step list: a path is
///     the steps up to the branch point, then the target's. Choosing one is view state, not an edit, so it
///     takes no undo slot.
/// </summary>
public static class StratPath
{
    /// <summary>The step list in authoring order.</summary>
    /// <param name="document">The open strat.</param>
    public static IReadOnlyList<StratPathStep> MainLine(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.Steps.Select((s, i) => new StratPathStep(s, document.Id, i, true))];
    }

    /// <summary>
    ///     The steps up to and including the branch's <c>afterStepId</c>, then the target: in this strat, the
    ///     steps from the target step on (the ones between are skipped); in another strat, that strat's steps
    ///     from its target step, or from its first. Null when the branch or its hook is gone, or the other
    ///     strat cannot be read.
    /// </summary>
    /// <param name="document">The open strat.</param>
    /// <param name="branchId">The branch.</param>
    /// <param name="lookup">Reads another strat by id; null plays only in-document targets.</param>
    public static IReadOnlyList<StratPathStep>? Through(StratDocument document, Guid branchId,
        Func<Guid, StratDocument?>? lookup)
    {
        ArgumentNullException.ThrowIfNull(document);

        StratBranch? branch = document.Branches.FirstOrDefault(b => b.Id == branchId);
        int hook = branch is null ? -1 : document.Steps.FindIndex(s => s.Id == branch.AfterStepId);
        if (branch is null || hook < 0)
        {
            return null;
        }

        List<StratPathStep> path = [.. MainLine(document).Take(hook + 1)];

        if (branch.Target.StratId == document.Id)
        {
            // A target in this strat without a step means its start, which would replay the steps before
            // the branch point; the step after the hook is the only reading that moves forward.
            int from = branch.Target.StepId is { } stepId
                ? document.Steps.FindIndex(s => s.Id == stepId)
                : hook + 1;
            if (from < 0)
            {
                return null;
            }

            path.AddRange(document.Steps.Skip(from).Select((s, i) => new StratPathStep(s, document.Id, from + i, true)));
            return path;
        }

        if (lookup?.Invoke(branch.Target.StratId) is not { } target)
        {
            return null;
        }

        int start = branch.Target.StepId is { } targetStep ? Math.Max(0, target.Steps.FindIndex(s => s.Id == targetStep)) : 0;
        path.AddRange(target.Steps.Skip(start).Select((s, i) => new StratPathStep(s, target.Id, start + i, false)));
        return path;
    }
}

/// <summary>
///     Where a throw's lineup is thrown from, as a token placement, or null when the lineup does not resolve.
///     Called on the UI thread, so it answers from memory.
/// </summary>
/// <param name="map">The strat's map.</param>
/// <param name="utility">The step's utility, with a lineup id.</param>
public delegate TokenPlacement? ThrowOriginResolver(string map, UtilityRef utility);

/// <summary>
///     Where a place's centre is on a floor (over all its floors when it has none there), or null for a place the
///     map lacks. Called on the UI thread, so it answers from memory.
/// </summary>
/// <param name="place">A canonical place name.</param>
/// <param name="levelMinZ">The token's level key.</param>
public delegate (double X, double Y)? PlaceCentreResolver(string place, double levelMinZ);

/// <summary>
///     Where a token arrives at a place: its centre on the token's floor when the place has areas there, else on the
///     place's own floor, with that floor's level key; null for a place the map lacks. Answers from memory.
/// </summary>
/// <param name="place">A canonical place name.</param>
/// <param name="levelMinZ">The token's level key.</param>
public delegate (double X, double Y, double LevelMinZ)? PlaceArrivalResolver(string place, double levelMinZ);

/// <summary>
///     Whether a point is inside a place on a floor, for spreading tokens sent to one place. Answers from memory.
/// </summary>
/// <param name="place">A canonical place name.</param>
/// <param name="x">World X.</param>
/// <param name="y">World Y.</param>
/// <param name="levelMinZ">The floor's level key.</param>
public delegate bool PlaceContainsResolver(string place, double x, double y, double levelMinZ);

/// <summary>Where a projected stroke came from: the path step and its index in that step's <c>strokes[]</c>.</summary>
/// <param name="PathIndex">The step's position on the path.</param>
/// <param name="StrokeIndex">The stroke's index in the step's <c>strokes[]</c>.</param>
public readonly record struct StrokeRef(int PathIndex, int StrokeIndex);

/// <summary>
///     A strat as the canvas and an export see it (step-authoring.md §3.3, §3.5, §3.6): the path's step
///     schedule on the strat frame clock, one token track per placed slot, every step's strokes as one
///     <see cref="AnnotationElement" /> list whose envelopes are the step windows, the marker labels and the
///     utility landings. Pure: built from a document and a path, never edited; an edit goes to the strat
///     session and a new projection is built from the result.
/// </summary>
public sealed class StratSceneProjection
{
    /// <summary>The strat clock's header for anything that serializes the projected document (§3.5).</summary>
    public const string ClockKind = "dv-strat-clock";

    /// <summary>
    ///     How fast a token sent by a travel verb runs: a rifle's run speed. The canvas has no path to follow, so this
    ///     only sets when the token arrives.
    /// </summary>
    public const double RunUnitsPerSecond = 215;

    /// <summary>
    ///     How fast a lurker walks, to its area and on its rotate: a rifle's shift-walk, about half its run. Nothing
    ///     about a lurk runs.
    /// </summary>
    public const double WalkUnitsPerSecond = 115;

    private readonly PlaceSet _places;
    private readonly Dictionary<string, SlotPlan> _plans;
    private readonly Dictionary<Guid, StrokeRef> _strokes;
    private readonly ThrowOrigin?[] _throwOrigins;
    private readonly TokenStep[] _tokenSteps;

    private StratSceneProjection(IReadOnlyList<StratPathStep> path, StepSchedule schedule, IReadOnlyList<int> ticks,
        TokenStep[] tokenSteps, ThrowOrigin?[] throwOrigins, IReadOnlyList<TokenTrack> tracks,
        IReadOnlyList<AnnotationElement> elements, Dictionary<Guid, StrokeRef> strokes, IReadOnlyList<TokenLabel> labels,
        IReadOnlyList<UtilityCue> utility, int roundSeconds, StratCanvas canvas, bool clockClamped, PlaceSet places,
        Dictionary<string, SlotPlan> plans)
    {
        _places = places;
        _plans = plans;
        _tokenSteps = tokenSteps;
        _throwOrigins = throwOrigins;
        Path = path;
        Schedule = schedule;
        Ticks = ticks;
        Tracks = tracks;
        Elements = elements;
        _strokes = strokes;
        Labels = labels;
        Utility = utility;
        RoundSeconds = roundSeconds;
        Canvas = canvas;
        ClockClamped = clockClamped;
    }

    /// <summary>The steps played, in order.</summary>
    public IReadOnlyList<StratPathStep> Path { get; }

    /// <summary>The path's step windows.</summary>
    public StepSchedule Schedule { get; }

    /// <summary>Each path step's tick, as scheduled.</summary>
    public IReadOnlyList<int> Ticks { get; }

    /// <summary>One track per slot that has at least one keyframe on the path, in slot order.</summary>
    public IReadOnlyList<TokenTrack> Tracks { get; }

    /// <summary>Every path step's strokes, step by step, each windowed to its step.</summary>
    public IReadOnlyList<AnnotationElement> Elements { get; }

    /// <summary>Per slot, the marker text and side.</summary>
    public IReadOnlyList<TokenLabel> Labels { get; }

    /// <summary>Every throw on the path: its flight, its landing and when it goes off (<see cref="StratThrows" />).</summary>
    public IReadOnlyList<UtilityCue> Utility { get; }

    /// <summary>The strat's round length, whole seconds, for the clock.</summary>
    public int RoundSeconds { get; }

    /// <summary>The strat's canvas block, defaults filled.</summary>
    public StratCanvas Canvas { get; }

    /// <summary>
    ///     True when a step's time ran backwards along the path and was held at the one before it. The
    ///     validator refuses such a strat; the canvas still plays it, in order, rather than going blank.
    /// </summary>
    public bool ClockClamped { get; }

    /// <summary>The last step's tick; 0 with no steps.</summary>
    public int LastTick => Schedule.LastTick;

    /// <summary>
    ///     The last tick anything moves or shows: the last step's, a run's arrival after it (a destination's or a lurk
    ///     rotate's), or a throw's last effect tick. The transport, the step row and an export run to here, so a run
    ///     after the last step still plays and a smoke plays out.
    /// </summary>
    public int ContentEndTick => Math.Max(Math.Max(LastTick, Tracks.Count == 0 ? 0 : Tracks.Max(t => t.Keyframes.Count == 0 ? 0 : t.Keyframes[^1].Tick)),
        UtilityEndTick);

    /// <summary>The last tick a throw draws: its effect's end, or its flight line's fade. 0 with none.</summary>
    public int UtilityEndTick => Utility.Count == 0 ? 0 : Utility.Max(StratFrameSource.EndTickOf) - 1;

    /// <summary>
    ///     The projected document's clock header: <c>ClockIdentity("dv-strat-clock", 64, lastTick + 1, 0,
    ///     lastTick)</c> (§3.5, correction 7).
    /// </summary>
    public ClockIdentity Clock => new(ClockKind, StepSchedule.TicksPerSecond, LastTick + 1, 0, LastTick);

    /// <summary>Where a projected stroke came from, or false for an element this projection did not make.</summary>
    /// <param name="elementId">The element's id.</param>
    /// <param name="stroke">Its step and index.</param>
    public bool TryFindStroke(Guid elementId, out StrokeRef stroke) => _strokes.TryGetValue(elementId, out stroke);

    /// <summary>The path index of a step, or -1.</summary>
    /// <param name="stepId">The step.</param>
    public int IndexOf(Guid stepId)
    {
        for (int i = 0; i < Path.Count; i++)
        {
            if (Path[i].Step.Id == stepId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     The placement a path step's lineup puts its actor at, or null when the step has none. It wins over the
    ///     step's authored position for that slot.
    /// </summary>
    /// <param name="pathIndex">The step's position on the path.</param>
    /// <param name="slot">The token.</param>
    public TokenPlacement? ThrowOriginAt(int pathIndex, string slot) =>
        pathIndex >= 0 && pathIndex < _throwOrigins.Length && _throwOrigins[pathIndex] is { } origin
                                                          && string.Equals(origin.Slot, slot, StringComparison.Ordinal)
            ? origin.Placement
            : null;

    /// <summary>Builds the projection of a path.</summary>
    /// <param name="document">The open strat: its side, clock and canvas block.</param>
    /// <param name="path">The steps to play, from <see cref="StratPath" />.</param>
    /// <param name="throwOrigins">Resolves a throw's lineup to where it is thrown from; null projects no throw origins.</param>
    /// <param name="placeCentres">Where a watched place is, for a token's facing; null faces only an explicit view angle.</param>
    /// <param name="placeArrivals">
    ///     Where a token sent to a place arrives, on the place's own floor; null arrives at
    ///     <paramref name="placeCentres" />'s centre on the token's floor.
    /// </param>
    /// <param name="placeContains">Whether a point is in a place, to keep fanned-out tokens inside it; null keeps to a ring.</param>
    /// <param name="throwFlights">Resolves a throw's lineup to its flight and landing; null flies only authored landings.</param>
    public static StratSceneProjection Build(StratDocument document, IReadOnlyList<StratPathStep> path,
        ThrowOriginResolver? throwOrigins = null, PlaceCentreResolver? placeCentres = null, PlaceArrivalResolver? placeArrivals = null,
        PlaceContainsResolver? placeContains = null, ThrowFlightResolver? throwFlights = null)
    {
        placeArrivals ??= ArrivalsFrom(placeCentres);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);

        double roundSeconds = document.Clock.RoundSeconds > 0 ? document.Clock.RoundSeconds : StratClock.DefaultRoundSeconds;
        StratCanvas canvas = document.Canvas ?? new StratCanvas();

        int[] ticks = TicksOf(path, roundSeconds, out bool clamped);
        StepSchedule schedule = new(path.Select((p, i) => (p.Step.Id, ticks[i])));

        TokenStep[] tokenSteps = new TokenStep[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            StratStep step = path[i].Step;
            tokenSteps[i] = new TokenStep(ticks[i], HoldTicks(step.HoldSeconds), InterpolationOf(step.Interpolation));
        }

        ThrowOrigin?[] origins = new ThrowOrigin?[path.Count];
        if (throwOrigins is not null)
        {
            for (int i = 0; i < path.Count; i++)
            {
                origins[i] = ThrowOriginOf(document.Map, path[i].Step, throwOrigins);
            }
        }

        PlaceSet places = new(placeCentres, placeArrivals, placeContains, canvas.DefaultLevelMinZ ?? 0, roundSeconds,
            IsLegacyCarry(document), IsLegacyObserved(document));
        Dictionary<string, SlotPlan> plans = Fanned(TokenSlots.All.ToDictionary(slot => slot, slot => PlanOf(path, ticks, origins, slot, places),
            StringComparer.Ordinal));
        List<TokenTrack> tracks = [];
        foreach (string slot in TokenSlots.All)
        {
            TokenTrack track = TrackOf(plans[slot], slot, tokenSteps, places);
            if (track.Keyframes.Count > 0)
            {
                tracks.Add(track);
            }
        }

        List<AnnotationElement> elements = [];
        Dictionary<Guid, StrokeRef> strokes = [];
        for (int i = 0; i < path.Count; i++)
        {
            StepWindow window = schedule.Windows[i];
            TimeEnvelope time = new(window.FromTick, window.UntilTick, canvas.FadeInTicks, canvas.FadeOutTicks);
            List<JsonObject> stepStrokes = path[i].Step.Strokes;
            for (int k = 0; k < stepStrokes.Count; k++)
            {
                Guid fallback = FallbackStrokeId(path[i].Step.Id, k);
                if (StratStrokes.ToElement(stepStrokes[k], fallback, time, canvas.DefaultLevelMinZ ?? 0) is not { } element
                    || !strokes.TryAdd(element.Id, new StrokeRef(i, k)))
                {
                    continue;
                }

                elements.Add(element);
            }
        }

        IReadOnlyList<TokenLabel> labels = LabelsFor(document);
        List<UtilityCue> utility = StratThrows.Cues(document.Map, path, ticks, tracks, labels, throwFlights, placeArrivals,
            canvas.DefaultLevelMinZ ?? 0);
        return new StratSceneProjection(path, schedule, ticks, tokenSteps, origins, tracks, elements, strokes, labels,
            utility, (int)Math.Round(roundSeconds), canvas, clamped, places, plans);
    }

    /// <summary>
    ///     Each step's tick from its own time, held non-decreasing: the schedule refuses a clock that runs backwards, and
    ///     a strat mid-edit in the step table (or a branch into a strat whose times start earlier) is still worth drawing
    ///     in order.
    /// </summary>
    internal static int[] TicksOf(IReadOnlyList<StratPathStep> path, double roundSeconds, out bool clamped)
    {
        int[] ticks = new int[path.Count];
        clamped = false;
        for (int i = 0; i < path.Count; i++)
        {
            int tick = Math.Max(0, StepSchedule.TickFor(path[i].Step.AtSeconds, roundSeconds));
            if (i > 0 && tick < ticks[i - 1])
            {
                tick = ticks[i - 1];
                clamped = true;
            }

            ticks[i] = tick;
        }

        return ticks;
    }

    /// <summary>Arrivals at <paramref name="centres" />' centre on the token's own floor; null for null.</summary>
    /// <param name="centres">Place centres, or null.</param>
    public static PlaceArrivalResolver? ArrivalsFrom(PlaceCentreResolver? centres) =>
        centres is null ? null : (place, level) => centres(place, level) is { } c ? (c.X, c.Y, level) : null;

    /// <summary>
    ///     The tick a step's lurk rotates at, or null when it does not move a token: no time, no rotate-to place, or a
    ///     time not later than the step's own tick.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="stepTick">The step's tick.</param>
    /// <param name="roundSeconds">The strat's round length.</param>
    internal static int? RotateTickOf(StratStep step, int stepTick, double roundSeconds)
    {
        if (step.Lurk?.Rotate is not { AtSeconds: { } at, To: { } to } || !StratLocations.IsSet(to) || !StratLurkPatches.IsLater(at, step.AtSeconds, roundSeconds))
        {
            return null;
        }

        // The step's own tick may be held later than its time when the clock runs backwards.
        int tick = StratLurkPatches.TickOf(at, roundSeconds);
        return tick > stepTick ? tick : null;
    }

    /// <summary>
    ///     What a step's verb does with its destination: move, push, rotate, <c>other</c> and any verb outside the
    ///     vocabulary travel there; hold, peek, fake, plant and defuse are there at the step's time; a lurk walks to
    ///     its first area; throw, wait and call do not move for it.
    /// </summary>
    /// <param name="verb">A step verb.</param>
    public static StepMotion MotionOf(string? verb) => StratStepFields.MotionOf(verb);

    /// <summary>
    ///     Where the step's verb sends <paramref name="slot" />: its <c>to</c> (the line's, or the step's), or for a lurk
    ///     its first area. Null when the verb does not move for a destination or the slot has none.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="slot">A slot letter.</param>
    public static PlaceRef? DestinationOf(StratStep step, string slot)
    {
        ArgumentNullException.ThrowIfNull(step);

        // A step names only the strat's own five; "all" never sends the opponent tokens anywhere.
        if (!StratVocabulary.Slots.Contains(slot))
        {
            return null;
        }

        PlaceRef? to = MotionOf(step.Verb) switch
        {
            StepMotion.None => null,
            StepMotion.Lurk => StratStepLines.Involves(step, slot) && StratLocations.LurkAreas(step.Lurk) is { Count: > 0 } areas ? areas[0] : null,
            _ => StratStepLines.LocationFor(step, slot)
        };
        return StratLocations.IsSet(to) ? to : null;
    }

    // A picked point is where the token goes, on the point's own level; a place goes through the arrivals.
    private static (double X, double Y, double LevelMinZ)? ArrivalAt(PlaceRef to, double levelMinZ, PlaceArrivalResolver arrivals) =>
        StratLocations.HasPoint(to)
            ? (to.X!.Value, to.Y!.Value, to.LevelMinZ ?? levelMinZ)
            : StratLocations.HasPlace(to) ? arrivals(to.Place!, levelMinZ) : null;

    /// <summary>
    ///     A slot's track with one path step's entry swapped for <paramref name="placement" />: what the
    ///     canvas shows mid-drag, before the drag closes into an op and a new projection.
    /// </summary>
    /// <param name="slot">The token.</param>
    /// <param name="pathIndex">The step being written.</param>
    /// <param name="placement">The entry the drag has reached.</param>
    /// <param name="keepYaw">The placement's yaw is the drag's own (a turn), not to be replaced by the line's watch.</param>
    public TokenTrack TrackWith(string slot, int pathIndex, TokenPlacement placement, bool keepYaw = false)
    {
        SlotPlan plan = PlanOf(Path, Ticks, _throwOrigins, slot, _places, pathIndex, placement, keepYaw);
        if (_plans.TryGetValue(slot, out SlotPlan? projected))
        {
            // The drag moves one token; the spots stand as projected.
            plan = plan with
            {
                Events = [.. plan.Events.Select(e => e with
                {
                    Fan = projected.Events.Any(p => p.Fan && p.Tick == e.Tick && p.Order == e.Order && p.Kind == e.Kind)
                })]
            };
        }

        return TrackOf(plan, slot, _tokenSteps, _places);
    }

    /// <summary>
    ///     Per slot, how the path places it (docs/strat-format.md, "Motion on the canvas"): an entry per step, in
    ///     order of precedence a throw's lineup origin, an authored position, the step's destination, a carried
    ///     position; and the runs, arrivals and turns the destinations and lurk rotates add on the strat clock. On a
    ///     travel verb an authored position is the departure point and the destination still applies.
    /// </summary>
    /// <param name="path">The steps.</param>
    /// <param name="ticks">Each step's tick.</param>
    /// <param name="origins">Each step's throw origin, or null entries; null for none at all.</param>
    /// <param name="slot">The token.</param>
    /// <param name="places">Where places are; nothing moves for a destination without them.</param>
    /// <param name="overrideIndex">A step whose authored entry is replaced, for a drag in progress; -1 for none.</param>
    /// <param name="overridePlacement">That step's entry.</param>
    /// <param name="keepOverrideYaw">The override's yaw stands; the line's watch does not turn it.</param>
    internal static SlotPlan PlanOf(IReadOnlyList<StratPathStep> path, IReadOnlyList<int> ticks, ThrowOrigin?[]? origins, string slot,
        PlaceSet places, int overrideIndex = -1, TokenPlacement? overridePlacement = null, bool keepOverrideYaw = false)
    {
        TokenPlacement?[] placements = new TokenPlacement?[path.Count];
        List<SlotEvent> events = [];
        List<int> rotates = [];
        TokenPlacement? last = null, lastStored = null;

        // Since the slot's last origin or authored entry, a destination or a rotate has moved it: a carried entry is
        // stale then, and where it stands is only known once the runs are laid out.
        bool moved = false;

        // The tick the slot was last sent from by a step, and the tick of the last lurk naming it, for same-tick steps.
        int departed = -1, lurking = -1;
        for (int i = 0; i < path.Count; i++)
        {
            StratStep step = path[i].Step;
            int tick = ticks[i];
            if (rotates.RemoveAll(t => t <= tick) > 0)
            {
                moved = true;
            }

            StepWatch? watch = StratStepLines.HasLines(step) ? StratStepLines.LineFor(step, slot)?.Watch : null;
            TokenPlacement? stored = PlacementOf(step, slot, out bool marked, out bool observed);
            bool carried = stored is { } s && (marked || (places.LegacyCarry && SameSpot(s, lastStored)));
            StepMotion motion = MotionOf(step.Verb);
            bool names = StratVocabulary.Slots.Contains(slot) && StratStepLines.Involves(step, slot);
            if (motion == StepMotion.Lurk && names)
            {
                lurking = tick;
            }

            PlaceRef? to = DestinationOf(step, slot) is { } d && places.Arrivals is { } arrivals
                                                    && ArrivalAt(d, (last ?? stored)?.LevelMinZ ?? places.DefaultLevelMinZ, arrivals) is not null
                ? d
                : null;
            TokenPlacement? placement = null;
            bool fromOrigin = false;
            if (i != overrideIndex && origins is not null && origins[i] is { } origin && string.Equals(origin.Slot, slot, StringComparison.Ordinal))
            {
                placement = origin.Placement;
                fromOrigin = true;
            }
            else if (i == overrideIndex && overridePlacement is { } dragged)
            {
                placement = keepOverrideYaw ? dragged : Turned(dragged, watch, places.Centres);
            }
            else if (stored is { } own && !carried)
            {
                placement = Turned(own, watch, places.Centres);
            }

            // Positions on one tick settle before its destinations: after a same-tick send, an entry is where the token
            // leaves from, unless it is the exact spot of a lineup or of a position verb naming the slot.
            if (placement is not null && (departed != tick || fromOrigin || (motion == StepMotion.Position && names)))
            {
                if (departed == tick)
                {
                    events.RemoveAll(e => e.Tick == tick && e.Shaped && e.To is not null);
                    departed = -1;
                }

                moved = false;
            }

            // A travel verb's entry is its departure point; only a position verb's entry blocks its destination. An
            // observed entry is where the player was seen, so it is the spot on every verb.
            bool seen = observed && placement is not null && i != overrideIndex;
            if (to is not null && !fromOrigin && (placement is null || (!seen && !places.Observed && motion is StepMotion.Travel or StepMotion.Lurk)))
            {
                SendTo(events, tick, i, motion, to, watch, lurking == tick);
                departed = tick;
                moved = true;
            }
            else if (placement is null)
            {
                if (stored is { } kept && !moved)
                {
                    placement = Turned(kept, watch, places.Centres);
                }
                else if (watch is not null && moved)
                {
                    events.Add(new SlotEvent(tick, i, SlotEventKind.Face, null, watch, false, true));
                }
                else if (watch is not null && last is { } at && FacingOf(watch, at, places.Centres) is { } yaw)
                {
                    // Where the token stands, so the stationary rule re-times no move.
                    placement = at with { YawDegrees = yaw };
                }
            }

            lastStored = fromOrigin ? placement : stored ?? lastStored;
            placements[i] = placement;
            last = placement ?? last;

            if (names && RotateTickOf(step, tick, places.RoundSeconds) is { } rotateTick && places.Arrivals is not null)
            {
                PlaceRef rotateTo = step.Lurk!.Rotate!.To!;
                events.Add(new SlotEvent(rotateTick, i, SlotEventKind.Travel, rotateTo, null, false, false, true));
                rotates.Add(rotateTick);
            }
        }

        events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
        return new SlotPlan(placements, [.. ticks], events, moved);
    }

    // A watching line turns the entry: its angle, else towards the first watched entry.
    private static TokenPlacement Turned(TokenPlacement at, StepWatch? watch, PlaceCentreResolver? centres) =>
        watch is not null && FacingOf(watch, at, centres) is { } yaw ? at with { YawDegrees = yaw } : at;

    /// <summary>
    ///     Whether a lurk's first area gives way to a place a non-lurk step on the same tick sent the lurker to (the
    ///     lurker walks there instead). False would let the lurk, as the later step, send it to its first area. The owner
    ///     has not settled this; this is the one switch.
    /// </summary>
    internal const bool LurkAreaYieldsToSameTickPlace = true;

    // On one tick a later step's destination replaces an earlier one's, a later lurk's included, except as
    // LurkAreaYieldsToSameTickPlace says. A lurker walks every trip on the lurk's tick.
    private static void SendTo(List<SlotEvent> events, int tick, int order, StepMotion motion, PlaceRef to, StepWatch? watch, bool lurking)
    {
        int earlier = events.FindIndex(e => e.Tick == tick && e.Shaped && e.To is not null);
        if (LurkAreaYieldsToSameTickPlace && earlier >= 0 && motion == StepMotion.Lurk && !events[earlier].FromLurk)
        {
            events[earlier] = events[earlier] with { Walk = true };
            return;
        }

        events.RemoveAll(e => e.Tick == tick && e.Shaped && e.To is not null);
        events.Add(new SlotEvent(tick, order, motion == StepMotion.Position ? SlotEventKind.Arrive : SlotEventKind.Travel, to, watch, false, true,
            lurking || motion == StepMotion.Lurk, motion == StepMotion.Lurk));
    }

    /// <summary>
    ///     The plans with <see cref="SlotEvent.Fan" /> set on every destination another slot shares while both are
    ///     there: the same place or point, from the event's tick until the slot's next destination or entry. Which step
    ///     sent each does not matter. Only events the precedence let through count, so a token alone goes to the centre.
    /// </summary>
    /// <param name="plans">Every slot's plan.</param>
    internal static Dictionary<string, SlotPlan> Fanned(Dictionary<string, SlotPlan> plans)
    {
        List<(string Slot, int Index, string Key, int From, int Until)> stays = [];
        foreach ((string slot, SlotPlan plan) in plans)
        {
            for (int k = 0; k < plan.Events.Count; k++)
            {
                SlotEvent e = plan.Events[k];
                if (e.To is null)
                {
                    continue;
                }

                int until = plan.Events.Skip(k + 1).FirstOrDefault(x => x.To is not null && x.Tick > e.Tick)?.Tick ?? int.MaxValue;
                for (int i = 0; i < plan.Placements.Length; i++)
                {
                    if (plan.Placements[i] is not null && plan.Ticks[i] > e.Tick)
                    {
                        until = Math.Min(until, plan.Ticks[i]);
                        break;
                    }
                }

                stays.Add((slot, k, DestinationKey(e.To), e.Tick, until));
            }
        }

        Dictionary<string, SlotPlan> fanned = new(plans, StringComparer.Ordinal);
        foreach ((string slot, int index, string key, int from, int until) in stays)
        {
            if (stays.Any(o => o.Slot != slot && o.Key == key && o.From < until && from < o.Until))
            {
                SlotPlan plan = fanned[slot];
                List<SlotEvent> events = [.. plan.Events];
                events[index] = events[index] with { Fan = true };
                fanned[slot] = plan with { Events = events };
            }
        }

        return fanned;
    }

    private static string DestinationKey(PlaceRef to) =>
        StratLocations.HasPoint(to)
            ? string.Create(CultureInfo.InvariantCulture, $"{to.X}|{to.Y}|{to.LevelMinZ}")
            : "place:" + to.Place;

    // Within this of the entry before it, an unmarked position is one Add step copied before carried entries were
    // marked: two decimals in the file against a float on the clock.
    private const double CarriedEpsilon = 0.02;

    private static bool SameSpot(TokenPlacement a, TokenPlacement? b) =>
        b is { } p && Math.Abs(a.X - p.X) <= CarriedEpsilon && Math.Abs(a.Y - p.Y) <= CarriedEpsilon
        && Math.Abs(a.LevelMinZ - p.LevelMinZ) <= CarriedEpsilon;

    /// <summary>
    ///     A slot's track: the builder's keyframes from its entries, then each event on the strat clock in order. A run
    ///     leaves where the token stands at its tick and arrives at <see cref="RunUnitsPerSecond" />, or
    ///     <see cref="WalkUnitsPerSecond" /> for a lurker; a later entry it
    ///     cannot reach first wins, and the token heads there from the run's tick instead. An arrival puts the token at
    ///     the place at its tick, walking from its previous keyframe, and runs instead when it has no time to walk.
    /// </summary>
    internal static TokenTrack TrackOf(SlotPlan plan, string slot, IReadOnlyList<TokenStep> steps, PlaceSet places)
    {
        List<TrackEntry> entries = [.. steps.Select((s, i) => new TrackEntry(s, plan.Placements[i], false, i))];

        // A rebuild per event: events are at most the steps plus the lurk rotates, so this is steps squared over a few
        // dozen steps, far under a frame.
        foreach (SlotEvent e in plan.Events)
        {
            TokenTrack track = Build(slot, entries);
            bool standing = track.TrySample(e.Tick, out TokenKeyframe at);
            TokenStep shape = e.Shaped ? steps[e.Order] with { Tick = e.Tick } : new TokenStep(e.Tick, 0, TokenInterpolation.Linear);
            if (e.Kind == SlotEventKind.Face)
            {
                if (standing && FacingOf(e.Watch!, Placement(at), places.Centres) is { } yaw)
                {
                    Place(entries, e, shape, Placement(at) with { YawDegrees = yaw });
                }

                continue;
            }

            if (Target(e, slot, standing ? at.LevelMinZ : places.DefaultLevelMinZ, places) is not { } target)
            {
                continue;
            }

            // A new destination cuts a run that has not arrived: the token turns from where it is now.
            bool cut = entries.RemoveAll(x => x.Arrival && x.Step.Tick > e.Tick) > 0;
            float? watched = e.Watch is not null ? FacingOf(e.Watch, target, places.Centres) : null;
            if (!standing)
            {
                // Nowhere to leave from: the token starts at its destination.
                Place(entries, e, shape, target with { YawDegrees = watched });
                continue;
            }

            double dx = target.X - at.X, dy = target.Y - at.Y;
            float? heading = dx * dx + dy * dy >= MinFacingDistance * MinFacingDistance
                ? (float)Math.Round(StratFromRound.NormalizeYaw(Math.Atan2(dy, dx) * 180 / Math.PI), 2)
                : null;
            if (e.Kind == SlotEventKind.Arrive && LastKeyTick(entries, e.Tick) != e.Tick)
            {
                Place(entries, e, shape, target with { YawDegrees = watched ?? heading });
                continue;
            }

            if (heading is not { } runYaw)
            {
                // Already there: it stops where it is, turned to what it watches.
                if (cut || watched is not null)
                {
                    Place(entries, e, shape, Placement(at) with { YawDegrees = watched ?? at.YawDegrees });
                }

                continue;
            }

            Run(entries, track, e, shape, at, target, runYaw, watched ?? runYaw);
        }

        return Build(slot, entries);
    }

    // From the tick the token runs from where it stands to the target, facing the way it runs, and turns to what it
    // watches on arrival. The step's hold delays the start. A later entry it cannot reach first wins.
    private static void Run(List<TrackEntry> entries, TokenTrack track, SlotEvent e, TokenStep shape, TokenKeyframe start,
        TokenPlacement target, float runYaw, float arriveYaw)
    {
        int tick = e.Tick;
        double distance = Math.Sqrt((target.X - start.X) * (double)(target.X - start.X) + (target.Y - start.Y) * (double)(target.Y - start.Y));
        double speed = e.Walk ? WalkUnitsPerSecond : RunUnitsPerSecond;
        int arrive = tick + shape.HoldTicks + Math.Max(1, (int)Math.Ceiling(distance / speed * StepSchedule.TicksPerSecond));

        // Pinned a tick early so the token turns at the run, not across the whole step before it.
        if (LastKeyTick(entries, tick - 1) < tick - 1 && track.TrySample(tick - 1, out TokenKeyframe held))
        {
            Insert(entries, tick - 1, Placement(held));
        }

        Place(entries, e, shape, Placement(start) with { YawDegrees = runYaw });
        int next = entries.FindIndex(x => x.Step.Tick > tick && x.Placement is not null);
        bool blocked = next >= 0 && entries[next].Step.Tick <= arrive;
        int until = blocked ? entries[next].Step.Tick : arrive;

        // A step with no entry inside the run would pin the token where it started.
        entries.RemoveAll(x => x.Placement is null && x.Step.Tick > tick && x.Step.Tick < until);
        if (!blocked)
        {
            Insert(entries, arrive, target with { YawDegrees = arriveYaw }, true);
        }
    }

    // An event's entry at its tick: its own step's entry when that step owns the tick, so the step's hold and
    // interpolation shape the move out of it as they do an authored entry's; else one after every entry at the tick.
    private static void Place(List<TrackEntry> entries, SlotEvent e, TokenStep shape, TokenPlacement placement)
    {
        int own = e.Shaped ? entries.FindIndex(x => x.PathIndex == e.Order && x.Step.Tick == e.Tick) : -1;
        if (own >= 0 && entries.FindLastIndex(x => x.Step.Tick == e.Tick) == own)
        {
            entries[own] = entries[own] with { Placement = placement };
            return;
        }

        int at = entries.FindIndex(x => x.Step.Tick > e.Tick);
        entries.Insert(at < 0 ? entries.Count : at, new TrackEntry(shape, placement));
    }

    // Where an event sends the token: the point, else the place's arrival; fanned out when another token is there too.
    private static TokenPlacement? Target(SlotEvent e, string slot, double levelMinZ, PlaceSet places)
    {
        if (places.Arrivals is not { } arrivals || e.To is null || ArrivalAt(e.To, levelMinZ, arrivals) is not { } centre)
        {
            return null;
        }

        (double x, double y) = e.Fan ? SpotFor(e.To.Place, centre, slot, places.Contains) : (centre.X, centre.Y);
        return new TokenPlacement((float)x, (float)y, centre.LevelMinZ, null);
    }

    // Tried outward in; the first inside the place wins.
    private static readonly double[] SpotRadii = [160, 120, 80, 48];

    private const double RingRadius = 96;

    /// <summary>
    ///     Where one of several tokens sent to one place stands: on a ray fixed by its slot letter, so its spot does not
    ///     move when the group changes, at the widest of <see cref="SpotRadii" /> still inside the place on the arrival's
    ///     floor, else on a small ring.
    /// </summary>
    /// <param name="place">The place, or null for a point.</param>
    /// <param name="centre">The arrival.</param>
    /// <param name="slot">The token.</param>
    /// <param name="contains">Whether a point is in a place on a floor; null keeps to the ring.</param>
    internal static (double X, double Y) SpotFor(string? place, (double X, double Y, double LevelMinZ) centre, string slot,
        PlaceContainsResolver? contains)
    {
        int k = Math.Max(0, IndexOfSlot(slot));
        double angle = (90 + 72 * k) * Math.PI / 180;
        (double cos, double sin) = (Math.Cos(angle), Math.Sin(angle));
        if (place is not null && contains is not null)
        {
            foreach (double r in SpotRadii)
            {
                (double x, double y) = (centre.X + r * cos, centre.Y + r * sin);
                if (contains(place, x, y, centre.LevelMinZ))
                {
                    return (x, y);
                }
            }
        }

        return (centre.X + RingRadius * cos, centre.Y + RingRadius * sin);
    }

    private static int IndexOfSlot(string slot)
    {
        for (int i = 0; i < StratVocabulary.Slots.Count; i++)
        {
            if (string.Equals(StratVocabulary.Slots[i], slot, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static TokenPlacement Placement(TokenKeyframe k) => new(k.X, k.Y, k.LevelMinZ, k.YawDegrees);

    // The tick of the last entry at or before `tick` that places the token, or int.MinValue for none.
    private static int LastKeyTick(List<TrackEntry> entries, int tick)
    {
        int found = int.MinValue;
        foreach (TrackEntry entry in entries)
        {
            if (entry.Step.Tick > tick)
            {
                break;
            }

            if (entry.Placement is not null)
            {
                found = entry.Step.Tick;
            }
        }

        return found;
    }

    // After every entry at the tick, so it wins the tick as the later step does.
    private static void Insert(List<TrackEntry> entries, int tick, TokenPlacement placement, bool arrival = false)
    {
        int at = entries.FindIndex(x => x.Step.Tick > tick);
        entries.Insert(at < 0 ? entries.Count : at, new TrackEntry(new TokenStep(tick, 0, TokenInterpolation.Linear), placement, arrival, -1));
    }

    private static TokenTrack Build(string slot, List<TrackEntry> entries) =>
        TokenTrackBuilder.Build(slot, [.. entries.Select(x => x.Step)], [.. entries.Select(x => x.Placement)]);

    // Arrival: the end of a run, which a later destination may cut. PathIndex: the step it is, or -1 for one added.
    private readonly record struct TrackEntry(TokenStep Step, TokenPlacement? Placement, bool Arrival = false, int PathIndex = -1);

    internal enum SlotEventKind
    {
        Travel,
        Arrive,
        Face
    }

    /// <summary>Something that moves or turns a token at a tick, laid out once the entries are.</summary>
    /// <param name="Tick">When it happens.</param>
    /// <param name="Order">The path index of the step it comes from.</param>
    /// <param name="Kind">A run, an arrival or a turn.</param>
    /// <param name="To">Where it sends the token; null for a turn.</param>
    /// <param name="Watch">The line's watch at that step.</param>
    /// <param name="Fan">Another token is at the same destination at the same time.</param>
    /// <param name="Shaped">At its step's tick, so the step's hold and interpolation shape it.</param>
    /// <param name="Walk">At <see cref="WalkUnitsPerSecond" />: a lurker's move.</param>
    /// <param name="FromLurk">A lurk's first area sent it.</param>
    internal sealed record SlotEvent(int Tick, int Order, SlotEventKind Kind, PlaceRef? To, StepWatch? Watch, bool Fan, bool Shaped,
        bool Walk = false, bool FromLurk = false);

    /// <summary>A slot's entries per step, the steps' ticks, its events, and whether it has moved since its last authored entry.</summary>
    internal sealed record SlotPlan(TokenPlacement?[] Placements, int[] Ticks, List<SlotEvent> Events, bool Moved);

    /// <summary>What the projection knows about the map's places, and the clock the rotates run on.</summary>
    /// <param name="Centres">Where places are, for facing.</param>
    /// <param name="Arrivals">Where a token sent to a place arrives.</param>
    /// <param name="Contains">Whether a point is in a place, for fanned-out spots.</param>
    /// <param name="DefaultLevelMinZ">The level for a token with none yet.</param>
    /// <param name="RoundSeconds">The strat's round length, for the rotate clock.</param>
    /// <param name="LegacyCarry">
    ///     The file predates the carried mark (<see cref="IsLegacyCarry" />), so an unmarked copy of the entry before is
    ///     read as carried.
    /// </param>
    /// <param name="Observed">Every entry is observed: a capture that predates the per-entry mark (<see cref="IsLegacyObserved" />).</param>
    internal sealed record PlaceSet(PlaceCentreResolver? Centres, PlaceArrivalResolver? Arrivals, PlaceContainsResolver? Contains,
        double DefaultLevelMinZ, double RoundSeconds, bool LegacyCarry = false, bool Observed = false);

    /// <summary>
    ///     Whether every position in the strat reads as observed: a captured or mined strat written before positions
    ///     carried the <c>observed</c> mark. A capture made since marks each entry it writes, and only those.
    /// </summary>
    /// <param name="document">The strat.</param>
    public static bool IsLegacyObserved(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (document.Origin is not null || document.Tags.Contains(MinedStratBuilder.Tag, StringComparer.Ordinal))
               && !document.Steps.Any(s => s.Positions.Any(p => p.Observed is not null));
    }

    /// <summary>
    ///     Whether an unmarked position that copies the slot's entry before it reads as carried: only in a file written
    ///     before the mark. A captured or mined strat never had Add step's copies, and one <c>carried</c> key anywhere
    ///     means this build wrote the file, which marks every copy it makes.
    /// </summary>
    /// <param name="document">The strat.</param>
    public static bool IsLegacyCarry(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Origin is null && !document.Tags.Contains(MinedStratBuilder.Tag, StringComparer.Ordinal)
                                       && !document.Steps.Any(s => s.Positions.Any(p => p.Carried is not null));
    }

    /// <summary>
    ///     The yaw a watch turns a token standing at <paramref name="at" /> to: the explicit angle, else towards
    ///     the first watched entry (places, then points). Null when neither applies (no angle, the place is unknown,
    ///     or the token stands on it).
    /// </summary>
    /// <param name="watch">The line's watch.</param>
    /// <param name="at">Where the token stands.</param>
    /// <param name="centres">Where watched places are; null for nowhere.</param>
    public static float? FacingOf(StepWatch watch, TokenPlacement at, PlaceCentreResolver? centres)
    {
        ArgumentNullException.ThrowIfNull(watch);
        if (watch.YawDegrees is { } explicitYaw && double.IsFinite(explicitYaw))
        {
            return (float)StratFromRound.NormalizeYaw(explicitYaw);
        }

        IReadOnlyList<PlaceRef> watched = StratLocations.Watched(watch);
        if (watched.Count == 0 || Where(watched[0], at.LevelMinZ, centres) is not { } centre)
        {
            return null;
        }

        double dx = centre.X - at.X, dy = centre.Y - at.Y;
        if (dx * dx + dy * dy < MinFacingDistance * MinFacingDistance)
        {
            return null;
        }

        return (float)Math.Round(StratFromRound.NormalizeYaw(Math.Atan2(dy, dx) * 180 / Math.PI), 2);
    }

    /// <summary>Where a location is for a token on <paramref name="levelMinZ" />: its point when it has one, else its place's centre.</summary>
    /// <param name="location">A location.</param>
    /// <param name="levelMinZ">The token's level key, for the place's centre on that floor.</param>
    /// <param name="centres">Where places are; null for nowhere.</param>
    public static (double X, double Y)? Where(PlaceRef location, double levelMinZ, PlaceCentreResolver? centres)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (StratLocations.HasPoint(location))
        {
            return (location.X!.Value, location.Y!.Value);
        }

        return centres is not null && StratLocations.HasPlace(location) ? centres(location.Place!, levelMinZ) : null;
    }

    // Closer than this to a place's centre, there is no direction worth turning to.
    private const double MinFacingDistance = 16;

    /// <summary>The team number a side draws in: 2 for T, 3 for CT.</summary>
    /// <param name="side">The strat's side.</param>
    public static int TeamOf(string? side) => string.Equals(side, StratVocabulary.SideCt, StringComparison.Ordinal) ? 3 : 2;

    /// <summary>
    ///     The ten markers' text and side: the strat's own slots by letter in its side's colour, the opponent
    ///     tokens as <c>1</c> to <c>5</c> in the other side's (§3.3).
    /// </summary>
    /// <param name="document">The strat.</param>
    public static IReadOnlyList<TokenLabel> LabelsFor(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        int own = TeamOf(document.Side);
        int other = own == 2 ? 3 : 2;
        return
        [
            .. TokenSlots.Own.Select(s => new TokenLabel(s, s, own)),
            .. TokenSlots.Opponents.Select(s => new TokenLabel(s, s[1..], other))
        ];
    }

    /// <summary>A step's hold in strat ticks: <c>holdSeconds × 64</c>, null and negatives as 0.</summary>
    /// <param name="holdSeconds">The step's <c>holdSeconds</c>.</param>
    public static int HoldTicks(double? holdSeconds) =>
        holdSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0
            ? (int)Math.Min(int.MaxValue, Math.Round(seconds * StepSchedule.TicksPerSecond, MidpointRounding.AwayFromZero))
            : 0;

    /// <summary><c>hold</c> holds; everything else, reserved <c>path</c> included, is linear (correction 17).</summary>
    /// <param name="interpolation">The step's <c>interpolation</c>.</param>
    public static TokenInterpolation InterpolationOf(string? interpolation) =>
        string.Equals(interpolation, "hold", StringComparison.Ordinal) ? TokenInterpolation.Hold : TokenInterpolation.Linear;

    /// <summary>
    ///     The id a stroke written without one is drawn under: stable for the step and index, so the same
    ///     document projects to the same ids every time.
    /// </summary>
    /// <param name="stepId">The step.</param>
    /// <param name="strokeIndex">The stroke's index in it.</param>
    public static Guid FallbackStrokeId(Guid stepId, int strokeIndex)
    {
        Span<byte> bytes = stackalloc byte[16];
        stepId.TryWriteBytes(bytes);
        bytes[15] ^= (byte)(strokeIndex + 1);
        bytes[14] ^= (byte)((strokeIndex + 1) >> 8);
        bytes[13] ^= 0x5A;
        return new Guid(bytes);
    }

    // Only a throw by one named slot: "all" names no one to stand at the origin.
    internal static ThrowOrigin? ThrowOriginOf(string map, StratStep step, ThrowOriginResolver resolve)
    {
        if (!string.Equals(step.Verb, "throw", StringComparison.Ordinal)
            || step.Utility is not { LineupId: not null } utility
            || StratStepLines.ActorOf(step) is not { } actor || !StratVocabulary.Slots.Contains(actor)
            || resolve(map, utility) is not { } placement
            || !float.IsFinite(placement.X) || !float.IsFinite(placement.Y))
        {
            return null;
        }

        return new ThrowOrigin(actor, placement);
    }

    private static TokenPlacement? PlacementOf(StratStep step, string slot, out bool carried, out bool observed)
    {
        // The last entry for a slot wins: a document holding two is refused by nothing today, and the later
        // one is what an edit that appended it meant.
        TokenPlacement? found = null;
        carried = false;
        observed = false;
        foreach (StepPosition position in step.Positions)
        {
            if (!string.Equals(position.Slot, slot, StringComparison.Ordinal)
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            {
                continue;
            }

            carried = position.Carried == true;
            observed = position.Observed == true;
            found = new TokenPlacement((float)position.X, (float)position.Y,
                double.IsFinite(position.LevelMinZ) ? position.LevelMinZ : 0,
                position.YawDegrees is { } yaw && double.IsFinite(yaw) ? (float)yaw : null);
        }

        return found;
    }

    internal sealed record ThrowOrigin(string Slot, TokenPlacement Placement);
}

/// <summary>
///     A step's stroke between its stored JSON and an <see cref="AnnotationElement" /> (step-authoring.md
///     §3.11): the <c>.dvann.json</c> element shape with the time fields and <c>timing</c> left off, and
///     <c>space</c> always <c>world</c>.
///     <para>
///         <b>Read leniently, write canonically.</b> The reader also takes the shorthand the schema sample
///         was written in (<c>space</c> as an object, points as <c>[x, y]</c> pairs, <c>color</c> as hex,
///         <c>width</c>), so every stroke a strat already holds draws. The writer emits the canonical
///         names only, and starts from the stored object, so a field this build does not know survives an
///         edit of the stroke.
///     </para>
/// </summary>
public static class StratStrokes
{
    // What the writer owns. Anything else on a stored stroke is carried through untouched.
    private static readonly string[] _written =
    [
        "id", "kind", "colorArgb", "widthWorld", "opacity", "revealOnFadeIn", "space", "levelMinZ", "points", "text",
        "color", "width", "fromTick", "untilTick", "fadeInTicks", "fadeOutTicks", "timing", "steamId", "dx", "dy"
    ];

    /// <summary>
    ///     A stored stroke as an element, or null when it is not a world-space stroke (an entity anchor has
    ///     no meaning on a strat, which has no players).
    /// </summary>
    /// <param name="stroke">The stored stroke.</param>
    /// <param name="fallbackId">The id to use when the stroke carries none or an unreadable one.</param>
    /// <param name="time">The step's window: a stroke stores no time of its own.</param>
    /// <param name="defaultLevelMinZ">The level when the stroke names none.</param>
    public static AnnotationElement? ToElement(JsonObject stroke, Guid fallbackId, TimeEnvelope time, double defaultLevelMinZ = 0)
    {
        ArgumentNullException.ThrowIfNull(stroke);

        Guid id = Guid.TryParse(String(stroke["id"]), out Guid parsed) ? parsed : fallbackId;

        // Enum.IsDefined fences a number or an unknown name, the sidecar's own rule; the points are a
        // polyline either way, so an unknown kind draws as freehand rather than vanishing.
        if (!Enum.TryParse(String(stroke["kind"]), true, out AnnotationKind kind) || !Enum.IsDefined(kind)
                                                                                  || int.TryParse(String(stroke["kind"]), out _))
        {
            kind = AnnotationKind.Freehand;
        }

        double levelMinZ = defaultLevelMinZ;
        switch (stroke["space"])
        {
            case JsonObject space:
                if (!IsWorld(String(space["kind"])))
                {
                    return null;
                }

                levelMinZ = Number(space["levelMinZ"]) ?? Number(stroke["levelMinZ"]) ?? defaultLevelMinZ;
                break;
            case JsonValue value when !IsWorld(String(value)):
                return null;
            default:
                levelMinZ = Number(stroke["levelMinZ"]) ?? defaultLevelMinZ;
                break;
        }

        uint color = Number(stroke["colorArgb"]) is { } argb && argb >= 0 && argb <= uint.MaxValue
            ? (uint)argb
            : ParseHex(String(stroke["color"])) ?? AnnotationStyle.Default.ColorArgb;
        float width = (float)(Number(stroke["widthWorld"]) ?? Number(stroke["width"]) ?? AnnotationStyle.Default.WidthWorld);
        float opacity = (float)(Number(stroke["opacity"]) ?? 1);
        bool reveal = stroke["revealOnFadeIn"] is JsonValue r && r.TryGetValue(out bool b) && b;

        return new AnnotationElement(id, kind, new AnnotationStyle(color, width, opacity, reveal),
            new SpaceRef.World(levelMinZ), time, Points(stroke["points"]), String(stroke["text"]));
    }

    /// <summary>
    ///     An element as a stored stroke: the stored one it came from with the canonical fields rewritten, or a
    ///     fresh object. Time fields are never written; the step's time is the stroke's.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="original">The stroke it was projected from, or null for a new one.</param>
    public static JsonObject ToJson(AnnotationElement element, JsonObject? original = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        JsonObject stroke = original?.DeepClone().AsObject() ?? [];
        foreach (string key in _written)
        {
            stroke.Remove(key);
        }

        // Written first so a person reading the file sees the identity before the numbers; the carried
        // unknown fields follow in their stored order.
        JsonObject ordered = new()
        {
            ["id"] = element.Id.ToString("D", CultureInfo.InvariantCulture),
            ["kind"] = element.Kind.ToString(),
            ["colorArgb"] = element.Style.ColorArgb,
            ["widthWorld"] = element.Style.WidthWorld,
            ["opacity"] = element.Style.Opacity,
            ["revealOnFadeIn"] = element.Style.RevealOnFadeIn,
            ["space"] = "world",
            ["levelMinZ"] = element.Space is SpaceRef.World world ? world.LevelMinZ : 0
        };

        JsonArray points = [];
        foreach (InkPoint point in element.Points)
        {
            points.Add(point.X);
            points.Add(point.Y);
            points.Add(point.Pressure);
        }

        ordered["points"] = points;
        if (element.Text is { } text)
        {
            ordered["text"] = text;
        }

        foreach ((string key, JsonNode? value) in stroke.ToList())
        {
            stroke.Remove(key);
            ordered[key] = value;
        }

        return ordered;
    }

    private static bool IsWorld(string? space) => space is null || string.Equals(space, "world", StringComparison.OrdinalIgnoreCase);

    // Flat triples, the sidecar's spelling; or [x, y] / [x, y, pressure] arrays, the sample's. A pair with
    // no pressure gets the half pressure a device that reports none is given.
    private static InkPoint[] Points(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return [];
        }

        if (array[0] is JsonArray)
        {
            List<InkPoint> nested = new(array.Count);
            foreach (JsonNode? item in array)
            {
                if (item is JsonArray { Count: >= 2 } xy && Number(xy[0]) is { } x && Number(xy[1]) is { } y)
                {
                    nested.Add(new InkPoint((float)x, (float)y, (float)(xy.Count > 2 ? Number(xy[2]) ?? 0.5 : 0.5)));
                }
            }

            return [.. nested];
        }

        int count = array.Count / 3;
        InkPoint[] flat = new InkPoint[count];
        for (int i = 0; i < count; i++)
        {
            flat[i] = new InkPoint((float)(Number(array[i * 3]) ?? 0), (float)(Number(array[i * 3 + 1]) ?? 0),
                (float)(Number(array[i * 3 + 2]) ?? 0.5));
        }

        return flat;
    }

    private static uint? ParseHex(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string hex = text.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }

        return hex.Length switch
        {
            6 => 0xFF000000u | value,
            8 => value,
            _ => null
        };
    }

    private static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // A stroke read off disk holds JsonElement-backed values, one built in memory holds the CLR number it was
    // made from, and TryGetValue converts neither way on its own, so each width is asked for in turn.
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        double number;
        if (value.TryGetValue(out double d))
        {
            number = d;
        }
        else if (value.TryGetValue(out float f))
        {
            number = f;
        }
        else if (value.TryGetValue(out long l))
        {
            number = l;
        }
        else if (value.TryGetValue(out int i))
        {
            number = i;
        }
        else if (value.TryGetValue(out uint u))
        {
            number = u;
        }
        else
        {
            return null;
        }

        return double.IsFinite(number) ? number : null;
    }
}
