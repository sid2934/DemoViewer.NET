#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
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
    ///     How fast a token sent by a travel verb runs: a rifle's run speed, along its route when the map's nav is in
    ///     memory, else in a straight line.
    /// </summary>
    public const double RunUnitsPerSecond = 215;

    /// <summary>
    ///     How fast a lurker walks, to its area and on its rotate: a rifle's shift-walk, about half its run. Nothing
    ///     about a lurk runs.
    /// </summary>
    public const double WalkUnitsPerSecond = 115;

    private readonly PlaceSet _places;
    private readonly Dictionary<string, SlotPlan> _plans;
    private readonly Dictionary<string, List<RunSpan>> _runs;
    private readonly Dictionary<Guid, StrokeRef> _strokes;
    private readonly ThrowOrigin?[] _throwOrigins;
    private readonly TokenStep[] _tokenSteps;

    private StratSceneProjection(IReadOnlyList<StratPathStep> path, StepSchedule schedule, IReadOnlyList<int> ticks,
        TokenStep[] tokenSteps, ThrowOrigin?[] throwOrigins, IReadOnlyList<TokenTrack> tracks,
        IReadOnlyList<AnnotationElement> elements, Dictionary<Guid, StrokeRef> strokes, IReadOnlyList<TokenLabel> labels,
        IReadOnlyList<UtilityCue> utility, int roundSeconds, StratCanvas canvas, bool clockClamped, PlaceSet places,
        Dictionary<string, SlotPlan> plans, Dictionary<string, List<RunSpan>> runs)
    {
        _places = places;
        _plans = plans;
        _runs = runs;
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
        ClockInfo = places.ClockOrRound;
    }

    /// <summary>The strat's clock: which way its times count, and its round length.</summary>
    public StratClockInfo ClockInfo { get; }

    /// <summary>Whether the clock counts up from a trigger rather than down the round.</summary>
    public bool CountsUp => StratClock.IsTrigger(ClockInfo);

    /// <summary>A strat tick as the strat's clock shows it: <c>1:15</c>, or <c>+0:08</c> from a trigger.</summary>
    /// <param name="tick">A strat tick.</param>
    public string ClockTextAt(int tick) => StratClock.Format(ClockInfo, StratClock.AtSecondsAtTick(ClockInfo, tick));

    /// <summary>Whether any token has a start: then the clock runs from tick 0, where the tokens stand before step 1.</summary>
    public bool HasStart => _places.Starts is { Count: > 0 };

    /// <summary>
    ///     The step index of a step on the start's tick whose own entry for the token wins over the start, or null: a start
    ///     edit would change nothing on screen while it is there (a capture's freeze-end step, an entry a person put on a
    ///     step at the start). An older file's round-start entries its start was read from do not count.
    /// </summary>
    /// <param name="slot">The token.</param>
    public int? StartShadowedBy(string slot)
    {
        if (!_plans.TryGetValue(slot, out SlotPlan? plan) || plan.Authored is not { } authored)
        {
            return null;
        }

        int? found = null;
        for (int i = 0; i < Path.Count && Ticks[i] == 0; i++)
        {
            if (authored[i] && Path[i].Editable)
            {
                found = Path[i].StepIndex;
            }
        }

        return found;
    }

    /// <summary>Where a token stands at the start, or null when the strat gives it none.</summary>
    /// <param name="slot">The token.</param>
    public TokenPlacement? StartOf(string slot) =>
        _places.Starts is { } starts && starts.TryGetValue(slot, out TokenPlacement start) ? start : null;

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

    /// <summary>Whether tokens follow routes round walls: a path resolver was given.</summary>
    public bool Routed => _places.Paths is not null;

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
    /// <param name="paths">Routes tokens round walls; null moves them in straight lines.</param>
    /// <param name="spawns">The map's spawns, for an older file's start (<see cref="StratStartBlock.Effective" />); null for none.</param>
    public static StratSceneProjection Build(StratDocument document, IReadOnlyList<StratPathStep> path,
        ThrowOriginResolver? throwOrigins = null, PlaceCentreResolver? placeCentres = null, PlaceArrivalResolver? placeArrivals = null,
        PlaceContainsResolver? placeContains = null, ThrowFlightResolver? throwFlights = null, PathResolver? paths = null,
        StratSpawns? spawns = null)
    {
        placeArrivals ??= ArrivalsFrom(placeCentres);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);

        double roundSeconds = StratClock.LengthOf(document.Clock);
        StratClockInfo clock = ClockOf(document);
        StratCanvas canvas = document.Canvas ?? new StratCanvas();

        int[] ticks = TicksOf(path, clock, out bool clamped);
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

        PlaceSet places = PlacesOf(document, placeCentres, placeArrivals, placeContains, paths, spawns);
        Dictionary<string, SlotPlan> planned = TokenSlots.All.ToDictionary(slot => slot, slot => PlanOf(path, ticks, origins, slot, places),
            StringComparer.Ordinal);
        Dictionary<string, SlotPlan> plans = Fanned(planned);

        // A lurk's later areas are timed only once its walk is laid out, so a strat with one is laid out twice: the
        // first pass says when each lurker is at each area, the second fans the areas two lurkers share.
        Dictionary<string, List<ChainStay>> chained = new(StringComparer.Ordinal);
        Dictionary<string, TokenTrack> built = new(StringComparer.Ordinal);
        Dictionary<string, List<RunSpan>> runs = new(StringComparer.Ordinal);
        foreach (string slot in TokenSlots.All)
        {
            List<ChainStay> stays = [];
            runs[slot] = [];
            built[slot] = TrackOf(plans[slot], slot, tokenSteps, places, stays, runs[slot]);
            if (stays.Count > 0)
            {
                chained[slot] = stays;
            }
        }

        if (chained.Count > 0)
        {
            plans = Fanned(planned, chained);
            foreach (string slot in TokenSlots.All)
            {
                runs[slot] = [];
                built[slot] = TrackOf(plans[slot], slot, tokenSteps, places, null, runs[slot]);
            }
        }

        List<TokenTrack> tracks = [.. TokenSlots.All.Select(slot => built[slot]).Where(t => t.Keyframes.Count > 0)];

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
            utility, (int)Math.Round(roundSeconds), canvas, clamped, places, plans, runs);
    }

    /// <summary>
    ///     Each step's tick from its own time, held non-decreasing: the schedule refuses a clock that runs backwards, and
    ///     a strat mid-edit in the step table (or a branch into a strat whose times start earlier) is still worth drawing
    ///     in order.
    /// </summary>
    internal static int[] TicksOf(IReadOnlyList<StratPathStep> path, StratClockInfo clock, out bool clamped)
    {
        int[] ticks = new int[path.Count];
        clamped = false;
        for (int i = 0; i < path.Count; i++)
        {
            int tick = StratClock.StratTickOf(clock, path[i].Step.AtSeconds);
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
    /// <param name="clock">The strat's clock.</param>
    internal static int? RotateTickOf(StratStep step, int stepTick, StratClockInfo clock)
    {
        if (step.Lurk?.Rotate is not { AtSeconds: { } at, To: { } to } || !StratLocations.IsSet(to) || !StratLurkPatches.IsLater(at, step.AtSeconds, clock))
        {
            return null;
        }

        // The step's own tick may be held later than its time when the clock runs backwards.
        int tick = StratClock.StratTickOf(clock, at);
        return tick > stepTick ? tick : null;
    }

    /// <summary>The document's clock with its round length filled, for the projection's tick maths.</summary>
    /// <param name="document">The strat.</param>
    internal static StratClockInfo ClockOf(StratDocument document) =>
        new() { Kind = StratClock.IsTrigger(document.Clock) ? StratClock.TriggerKind : StratClock.RoundKind, RoundSeconds = StratClock.LengthOf(document.Clock) };

    /// <summary>
    ///     What the projection knows about a document's map and marks, its clock, and where its tokens start
    ///     (<see cref="StartsOf" />). The carry and the zip check build the same set the canvas does.
    /// </summary>
    internal static PlaceSet PlacesOf(StratDocument document, PlaceCentreResolver? centres, PlaceArrivalResolver? arrivals,
        PlaceContainsResolver? contains, PathResolver? paths, StratSpawns? spawns = null)
    {
        StratCanvas canvas = document.Canvas ?? new StratCanvas();
        PlaceSet places = new(centres, arrivals ?? ArrivalsFrom(centres), contains, canvas.DefaultLevelMinZ ?? 0, StratClock.LengthOf(document.Clock),
            IsLegacyCarry(document), IsLegacyObserved(document), paths, Clock: ClockOf(document));
        IReadOnlyList<StartSource> sources = [];
        StratStart? start = document.Start ?? StratStartBlock.Legacy(document, out sources, spawns);
        return places with { Starts = StartsOf(start, places), SeedEntries = SeedEntriesOf(document, sources) };
    }

    // The round-start step's own entries an older file's start was read from: authored, so never a copy of that start.
    // A start recovered from a later step's copy is left out: that entry is a copy, and reads as one.
    private static HashSet<(int Step, string Slot)>? SeedEntriesOf(StratDocument document, IReadOnlyList<StartSource> sources)
    {
        HashSet<(int, string)> seed = [.. sources.Where(s => s.Step == 0).Select(s => (s.Step, document.Steps[s.Step].Positions[s.Position].Slot))];
        return seed.Count > 0 ? seed : null;
    }

    /// <summary>
    ///     Each token's start as a placement at tick 0: a point where it was picked, a place alone at its arrival, fanned
    ///     out as destinations are when several tokens start at one place. A place the map lacks, or zones not in yet,
    ///     gives that token no start. Null with no start.
    /// </summary>
    /// <param name="start">The strat's start, or null.</param>
    /// <param name="places">Where places are.</param>
    internal static Dictionary<string, TokenPlacement>? StartsOf(StratStart? start, PlaceSet places)
    {
        if (start is not { Positions.Count: > 0 })
        {
            return null;
        }

        Dictionary<string, TokenPlacement> starts = new(StringComparer.Ordinal);
        List<StartPosition> entries = [.. TokenSlots.All.Select(slot => StratStartBlock.For(start, slot)).OfType<StartPosition>()];
        foreach (StartPosition entry in entries)
        {
            float? yaw = entry.YawDegrees is { } y && double.IsFinite(y) ? (float)y : null;
            if (entry is { X: { } x, Y: { } py } && double.IsFinite(x) && double.IsFinite(py))
            {
                double level = entry.LevelMinZ is { } l && double.IsFinite(l) ? l : places.DefaultLevelMinZ;
                starts[entry.Slot] = new TokenPlacement((float)x, (float)py, level, yaw);
                continue;
            }

            if (string.IsNullOrEmpty(entry.Place) || places.Arrivals?.Invoke(entry.Place, entry.LevelMinZ ?? places.DefaultLevelMinZ) is not { } centre)
            {
                continue;
            }

            bool shared = entries.Count(e => e.X is null && string.Equals(e.Place, entry.Place, StringComparison.Ordinal)) > 1;
            (double sx, double sy) = shared ? SpotFor(entry.Place, centre, entry.Slot, places.Contains, places.Paths) : (centre.X, centre.Y);
            starts[entry.Slot] = new TokenPlacement((float)sx, (float)sy, centre.LevelMinZ, yaw);
        }

        return starts.Count > 0 ? starts : null;
    }

    /// <summary>
    ///     What a step's verb does with its destination: move, push, rotate, <c>other</c> and any verb outside the
    ///     vocabulary travel there; hold, peek, fake, plant and defuse are there at the step's time; a lurk walks to
    ///     its first area and on through the rest; throw, wait and call do not move for it.
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
        bool[] authored = new bool[path.Count];
        List<SlotEvent> events = [];
        List<int> rotates = [];

        // The start is the earliest placement: tick 0, before any step's entry on that tick.
        TokenPlacement? start = places.Starts is { } starts && starts.TryGetValue(slot, out TokenPlacement s0) ? s0 : null;
        TokenPlacement? last = start, lastStored = start;

        // Since the slot's last origin or authored entry, a destination or a rotate has moved it: a carried entry is
        // stale then, and where it stands is only known once the runs are laid out.
        bool moved = false;

        // The tick the slot was last sent from by a step, for same-tick steps.
        int departed = -1;
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
            bool seed = path[i].Editable && places.SeedEntries?.Contains((path[i].StepIndex, slot)) == true;
            bool carried = stored is { } s && (marked || (places.LegacyCarry && !seed && SameSpot(s, lastStored)));
            StepMotion motion = MotionOf(step.Verb);
            bool names = StratVocabulary.Slots.Contains(slot) && StratStepLines.Involves(step, slot);
            double level = (last ?? stored)?.LevelMinZ ?? places.DefaultLevelMinZ;
            IReadOnlyList<PlaceRef> areas = motion == StepMotion.Lurk && names ? StratLocations.LurkAreas(step.Lurk) : [];
            int firstArea = places.Arrivals is { } resolver
                ? areas.ToList().FindIndex(a => StratLocations.IsSet(a) && ArrivalAt(a, level, resolver) is not null)
                : -1;
            PlaceRef? to = motion == StepMotion.Lurk
                ? firstArea >= 0 ? areas[firstArea] : null
                : DestinationOf(step, slot) is { } d && places.Arrivals is { } arrivals && ArrivalAt(d, level, arrivals) is not null
                    ? d
                    : null;
            TokenPlacement? placement = null;
            bool fromOrigin = false;
            if (i != overrideIndex && origins is not null && origins[i] is { } origin && string.Equals(origin.Slot, slot, StringComparison.Ordinal))
            {
                placement = origin.Placement;
                authored[i] = true;
                fromOrigin = true;
            }
            else if (i == overrideIndex && overridePlacement is { } dragged)
            {
                placement = keepOverrideYaw ? dragged : Turned(dragged, watch, places.Centres);
                authored[i] = true;
            }
            else if (stored is { } own && !carried)
            {
                placement = Turned(own, watch, places.Centres);
                authored[i] = !(path[i].Editable && places.SeedEntries?.Contains((path[i].StepIndex, slot)) == true);
            }

            // Positions on one tick settle before its destinations: after a same-tick send, an entry is where the token
            // leaves from, unless it is an exact spot (a lineup, or the entry of a step naming the slot whose verb does not
            // travel), which as the later step cancels the send. An earlier exact spot stays where a later send leaves from.
            if (placement is not null && (departed != tick || fromOrigin || (names && motion is StepMotion.Position or StepMotion.None)))
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
                SendTo(events, tick, i, motion, to, watch,
                    motion is StepMotion.Travel or StepMotion.Lurk && StratStepLines.ViaFor(step, slot) is { Count: > 0 } via ? via : null);
                if (motion == StepMotion.Lurk && firstArea + 1 < areas.Count)
                {
                    events[^1] = events[^1] with { Areas = [.. areas.Skip(firstArea + 1)] };
                }

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

            if (names && RotateTickOf(step, tick, places.ClockOrRound) is { } rotateTick
                && places.Arrivals is not null)
            {
                PlaceRef rotateTo = step.Lurk!.Rotate!.To!;
                events.Add(new SlotEvent(rotateTick, i, SlotEventKind.Travel, rotateTo, null, false, false, true));
                rotates.Add(rotateTick);
            }
        }

        events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
        return new SlotPlan(placements, [.. ticks], events, moved, start, authored);
    }

    // A watching line turns the entry: its angle, else towards the first watched entry.
    private static TokenPlacement Turned(TokenPlacement at, StepWatch? watch, PlaceCentreResolver? centres) =>
        watch is not null && FacingOf(watch, at, centres) is { } yaw ? at with { YawDegrees = yaw } : at;

    // On one tick a later step's destination replaces an earlier one's, whatever the verbs, and moves at its own pace.
    private static void SendTo(List<SlotEvent> events, int tick, int order, StepMotion motion, PlaceRef to, StepWatch? watch,
        IReadOnlyList<PlaceRef>? via)
    {
        events.RemoveAll(e => e.Tick == tick && e.Shaped && e.To is not null);
        events.Add(new SlotEvent(tick, order, motion == StepMotion.Position ? SlotEventKind.Arrive : SlotEventKind.Travel, to, watch, false, true,
            motion == StepMotion.Lurk, via));
    }

    /// <summary>
    ///     The plans with <see cref="SlotEvent.Fan" /> set on every destination another slot shares while both are
    ///     there: the same place or point, from the event's tick until the slot's next destination or entry. Which step
    ///     sent each does not matter. Only events the precedence let through count, so a token alone goes to the centre.
    /// </summary>
    /// <param name="plans">Every slot's plan.</param>
    /// <param name="chained">
    ///     Per slot, when a laid-out lurk walk is at each of its areas. Such an event stays at each area only until its
    ///     next leg leaves, not until the slot's next destination.
    /// </param>
    internal static Dictionary<string, SlotPlan> Fanned(Dictionary<string, SlotPlan> plans,
        IReadOnlyDictionary<string, List<ChainStay>>? chained = null)
    {
        List<(string Slot, int Index, int Area, string Key, int From, int Until)> stays = [];
        foreach ((string slot, SlotPlan plan) in plans)
        {
            List<ChainStay>? walked = chained?.GetValueOrDefault(slot);
            for (int k = 0; k < plan.Events.Count; k++)
            {
                SlotEvent e = plan.Events[k];
                if (e.To is null)
                {
                    continue;
                }

                if (walked?.Where(c => c.Event == k).ToList() is { Count: > 0 } legs)
                {
                    stays.AddRange(legs.Select(c => (slot, k, c.Area, DestinationKey(c.Area < 0 ? e.To : e.Areas![c.Area]), c.From, c.Until)));
                    continue;
                }

                // Before its walk is laid out a lurk is not fanned, so two lurkers that leave together are timed together.
                if (chained is null && e.Areas is not null)
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

                stays.Add((slot, k, -1, DestinationKey(e.To), e.Tick, until));
            }
        }

        Dictionary<string, SlotPlan> fanned = new(plans, StringComparer.Ordinal);
        foreach ((string slot, int index, int area, string key, int from, int until) in stays)
        {
            if (stays.Any(o => o.Slot != slot && o.Key == key && o.From < until && from < o.Until))
            {
                SlotPlan plan = fanned[slot];
                List<SlotEvent> events = [.. plan.Events];
                SlotEvent e = events[index];
                events[index] = area < 0 ? e with { Fan = true } : e with { FanAreas = new HashSet<int>(e.FanAreas ?? Enumerable.Empty<int>()) { area } };
                fanned[slot] = plan with { Events = events };
            }
        }

        return fanned;
    }

    /// <summary>When a laid-out lurk walk stands at one of its areas.</summary>
    /// <param name="Event">The lurk's event index in its slot's plan.</param>
    /// <param name="Area">The index in its <see cref="SlotEvent.Areas" />, or -1 for its first area.</param>
    /// <param name="From">The tick it arrives.</param>
    /// <param name="Until">The tick after the one its next leg leaves on.</param>
    internal readonly record struct ChainStay(int Event, int Area, int From, int Until);

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
    ///     the place at its tick, walking from its previous keyframe, and runs instead when it has no time to walk. A lurk
    ///     walks on through its later areas, each leg queued as an event once the leg before is laid out.
    /// </summary>
    /// <param name="plan">The slot's plan, fanned.</param>
    /// <param name="slot">The token.</param>
    /// <param name="steps">Each path step's tick, hold and interpolation.</param>
    /// <param name="places">Where places are, and the routes.</param>
    /// <param name="stays">Filled with when each lurk walk stands at each area, for fanning; null for none.</param>
    /// <param name="runs">Filled with the track's runs; null for none.</param>
    internal static TokenTrack TrackOf(SlotPlan plan, string slot, IReadOnlyList<TokenStep> steps, PlaceSet places,
        List<ChainStay>? stays = null, List<RunSpan>? runs = null)
    {
        List<TrackEntry> entries = [.. steps.Select((s, i) => new TrackEntry(s, plan.Placements[i], false, i))];
        if (plan.Start is { } start)
        {
            entries.Insert(0, new TrackEntry(new TokenStep(0, 0, TokenInterpolation.Linear), start));
        }

        // A rebuild per event: events are the steps, the lurk rotates and a leg per lurk area, so this is a few dozen
        // squared, far under a frame.
        List<SlotEvent> queue = [.. plan.Events];
        for (int i = 0; i < queue.Count; i++)
        {
            SlotEvent e = queue[i];
            TokenTrack track = Build(slot, entries);
            bool standing = TrySampleRouted(track, e.Tick, entries, places, out TokenKeyframe at);

            // A lurk's later legs move as its step does: its interpolation, but not its hold, which only delays the start.
            TokenStep shape = e.Shaped ? steps[e.Order] with { Tick = e.Tick }
                : new TokenStep(e.Tick, 0, e.Leg ? steps[e.Order].Interpolation : TokenInterpolation.Linear);
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
                ChainAreas(plan, queue, i, slot, target, e.Tick + shape.HoldTicks, entries, places, stays);
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

                ChainAreas(plan, queue, i, slot, Placement(at), e.Tick + shape.HoldTicks, entries, places, stays);
                continue;
            }

            if (Run(entries, track, e, shape, at, target, runYaw, watched, places, runs) is { } arrived)
            {
                ChainAreas(plan, queue, i, slot, target, arrived, entries, places, stays);
            }
        }

        return Bent(Build(slot, entries), entries, places);
    }

    // From the tick the token runs from where it stands to the target, facing the way it runs, and turns to what it
    // watches on arrival. The step's hold delays the start. A later entry it cannot reach first wins. With a route the
    // run takes its length and leaves an entry at each bend, timed along it at the run's one speed.
    private static int? Run(List<TrackEntry> entries, TokenTrack track, SlotEvent e, TokenStep shape, TokenKeyframe start,
        TokenPlacement target, float runYaw, float? watched, PlaceSet places, List<RunSpan>? runs = null)
    {
        int tick = e.Tick;
        int[]? stops = e.Via is { Count: > 0 } ? new int[e.Via.Count] : null;
        List<NavWaypoint>? route = places.Paths is { } paths ? RouteOf(paths, start, target, e, places, stops) : null;
        double distance = route is not null
            ? NavPathfinder.Length(route)
            : Math.Sqrt((target.X - start.X) * (double)(target.X - start.X) + (target.Y - start.Y) * (double)(target.Y - start.Y));
        double speed = e.Walk ? WalkUnitsPerSecond : RunUnitsPerSecond;
        int arrive = tick + shape.HoldTicks + Math.Max(1, (int)Math.Ceiling(distance / speed * StepSchedule.TicksPerSecond));
        float leaveYaw = route is not null && Heading(route, 0) is { } first ? first : runYaw;
        float arriveYaw = watched ?? (route is not null && Heading(route, route.Count - 2) is { } last ? last : runYaw);

        // Pinned a tick early so the token turns at the run, not across the whole step before it.
        if (LastKeyTick(entries, tick - 1) < tick - 1 && TrySampleRouted(track, tick - 1, entries, places, out TokenKeyframe held))
        {
            Insert(entries, tick - 1, Placement(held));
        }

        Place(entries, e, shape, Placement(start) with { YawDegrees = leaveYaw });
        int next = entries.FindIndex(x => x.Step.Tick > tick && x.Placement is not null);
        bool blocked = next >= 0 && entries[next].Step.Tick <= arrive;
        int until = blocked ? entries[next].Step.Tick : arrive;

        // A step with no entry inside the run would pin the token where it started.
        entries.RemoveAll(x => x.Placement is null && x.Step.Tick > tick && x.Step.Tick < until);
        runs?.Add(new RunSpan(e.Order, IsRotate(e), tick + shape.HoldTicks, until,
            blocked ? null : ViaTicks(route, stops, tick + shape.HoldTicks, arrive)));
        if (blocked)
        {
            return null;
        }

        // A hold-interpolated run jumps at its end, so it has no corners to stop at.
        if (route is { Count: > 2 } && shape.Interpolation == TokenInterpolation.Linear)
        {
            foreach (Corner corner in Corners(route, tick + shape.HoldTicks, arrive, start.LevelMinZ, places.Paths))
            {
                Insert(entries, corner.Tick, corner.Placement, true);
            }
        }

        Insert(entries, arrive, target with { YawDegrees = arriveYaw }, true);
        return arrive;
    }

    // A lurk's later areas, each leg walked from the area before as soon as the token is there. A leg that would leave at
    // or after the slot's next destination or placed entry (its rotate, usually) is not walked. An area the map lacks is
    // passed over.
    private static void ChainAreas(SlotPlan plan, List<SlotEvent> queue, int index, string slot, TokenPlacement first, int arrived,
        List<TrackEntry> entries, PlaceSet places, List<ChainStay>? stays)
    {
        SlotEvent e = queue[index];
        if (e.Areas is not { Count: > 0 } areas)
        {
            return;
        }

        int limit = queue.Skip(index + 1).FirstOrDefault(x => x.To is not null && x.Tick > e.Tick)?.Tick ?? int.MaxValue;
        if (entries.FirstOrDefault(x => !x.Arrival && x.Placement is not null && x.Step.Tick > e.Tick) is { Placement: not null } placed)
        {
            limit = Math.Min(limit, placed.Step.Tick);
        }

        List<(SlotEvent Leg, int Area, int Ticks)> legs = [];
        TokenPlacement from = first;
        for (int k = 0; k < areas.Count; k++)
        {
            SlotEvent leg = new(0, e.Order, SlotEventKind.Travel, areas[k], e.Watch, e.FanAreas?.Contains(k) == true, false, true, Leg: true);
            if (Target(leg, slot, from.LevelMinZ, places) is not { } to)
            {
                continue;
            }

            TokenKeyframe start = new(0, from.X, from.Y, from.LevelMinZ, 0);
            double length = places.Paths is { } paths && RouteOf(paths, start, to, leg, places) is { } route
                ? NavPathfinder.Length(route)
                : Math.Sqrt((to.X - from.X) * (double)(to.X - from.X) + (to.Y - from.Y) * (double)(to.Y - from.Y));
            legs.Add((leg, k, Math.Max(1, (int)Math.Ceiling(length / WalkUnitsPerSecond * StepSchedule.TicksPerSecond))));
            from = to;
        }

        // Each leg leaves on the tick the one before arrives. Its entry comes after that arrival's on the tick, so the
        // arrival's yaw (turned to the watch, often back the way it came) is never shown at an area it walks through.
        int planned = plan.Events.FindIndex(x => ReferenceEquals(x, e));
        int tick = arrived, area = -1;
        foreach ((SlotEvent leg, int k, int ticks) in legs)
        {
            if (tick >= limit)
            {
                break;
            }

            stays?.Add(new ChainStay(planned, area, tick, tick + 1));
            int at = queue.FindIndex(index + 1, x => x.Tick > tick);
            queue.Insert(at < 0 ? queue.Count : at, leg with { Tick = tick });
            (tick, area) = (tick + ticks, k);
        }

        if (tick < limit)
        {
            stays?.Add(new ChainStay(planned, area, tick, limit));
        }
    }

    // The run's route from where the token stands through each via in order to the target. A leg with no route is
    // straight; null when no leg routes and there is no via to go through.
    private static List<NavWaypoint>? RouteOf(PathResolver paths, TokenKeyframe start, TokenPlacement target, SlotEvent e, PlaceSet places,
        int[]? stops = null)
    {
        List<NavWaypoint> route = [];
        bool routed = false;
        (double X, double Y, double Level) from = (start.X, start.Y, start.LevelMinZ);
        IReadOnlyList<PlaceRef> vias = e.Via ?? [];
        for (int v = 0; v < vias.Count; v++)
        {
            if (stops is not null)
            {
                stops[v] = -1;
            }

            if (ViaAt(vias[v], from.Level, places) is not { } stop)
            {
                continue;
            }

            routed |= Leg(route, paths, from, stop, vias[v].Place);
            from = stop;
            if (stops is not null)
            {
                stops[v] = route.Count - 1;
            }
        }

        routed |= Leg(route, paths, from, (target.X, target.Y, target.LevelMinZ), e.To?.Place);
        return route.Count >= 2 && (routed || route.Count > 2) ? route : null;
    }

    private static bool Leg(List<NavWaypoint> route, PathResolver paths, (double X, double Y, double Level) from,
        (double X, double Y, double Level) to, string? place)
    {
        IReadOnlyList<NavWaypoint>? leg = paths.Route(from.X, from.Y, from.Level, to.X, to.Y, to.Level, place);
        IReadOnlyList<NavWaypoint> points = leg ?? [new NavWaypoint(from.X, from.Y, from.Level), new NavWaypoint(to.X, to.Y, to.Level)];

        // No repeated point: a via that is also the destination would end the route on a leg with no heading.
        foreach (NavWaypoint point in route.Count == 0 ? points : points.Skip(1))
        {
            if (route.Count == 0 || Distance(route[^1], point) > 1e-3 || route[^1].FloorKey != point.FloorKey)
            {
                route.Add(point);
            }
        }

        return leg is not null;
    }

    // A via point is where it was picked; a via place is its arrival, from the floor the token is on.
    private static (double X, double Y, double Level)? ViaAt(PlaceRef via, double level, PlaceSet places)
    {
        if (StratLocations.HasPoint(via))
        {
            return (via.X!.Value, via.Y!.Value, via.LevelMinZ ?? level);
        }

        return StratLocations.HasPlace(via) && places.Arrivals?.Invoke(via.Place!, level) is { } arrival
            ? (arrival.X, arrival.Y, arrival.LevelMinZ)
            : null;
    }

    // When the run passes each via stop, by its share of the route's length; null for a stop that did not resolve.
    private static int?[]? ViaTicks(List<NavWaypoint>? route, int[]? stops, int fromTick, int untilTick)
    {
        if (route is null || stops is null)
        {
            return null;
        }

        double[] along = new double[route.Count];
        for (int i = 1; i < route.Count; i++)
        {
            along[i] = along[i - 1] + Distance(route[i - 1], route[i]);
        }

        double total = along[^1];
        int?[] ticks = new int?[stops.Length];
        for (int v = 0; v < stops.Length; v++)
        {
            if (stops[v] >= 0 && stops[v] < route.Count)
            {
                ticks[v] = total < 1e-6
                    ? fromTick
                    : fromTick + (int)Math.Round((untilTick - fromTick) * along[stops[v]] / total, MidpointRounding.AwayFromZero);
            }
        }

        return ticks;
    }

    /// <summary>
    ///     One run on a track: the path step that sent it, whether it is a lurk's rotate walk, when it leaves (after the
    ///     step's hold) and arrives, and when it passes each via in reading order. <see cref="ViaTicks" /> is null for a
    ///     straight run, which goes through no via.
    /// </summary>
    public sealed record RunSpan(int PathIndex, bool Rotate, int StartTick, int ArriveTick, int?[]? ViaTicks)
    {
        /// <summary>How many vias the run has passed at a tick: the leg it is on. <see cref="int.MaxValue" /> for a straight run.</summary>
        /// <param name="tick">A tick inside the run.</param>
        public int LegAt(int tick)
        {
            if (ViaTicks is null)
            {
                return int.MaxValue;
            }

            int leg = 0;
            for (int v = 0; v < ViaTicks.Length; v++)
            {
                if (ViaTicks[v] is { } at && at <= tick)
                {
                    leg = v + 1;
                }
            }

            return leg;
        }
    }

    /// <summary>The run a slot is on at a tick, after it leaves and before it arrives; the latest to leave wins. Null when it stands.</summary>
    /// <param name="slot">The token.</param>
    /// <param name="tick">The playhead.</param>
    public RunSpan? RunAt(string slot, int tick)
    {
        RunSpan? found = null;
        if (_runs.TryGetValue(slot, out List<RunSpan>? runs))
        {
            foreach (RunSpan run in runs)
            {
                if (run.StartTick < tick && tick < run.ArriveTick && (found is null || run.StartTick >= found.StartTick))
                {
                    found = run;
                }
            }
        }

        return found;
    }

    /// <summary>The route resolver the tracks were built with; null when they are straight.</summary>
    internal PathResolver? Paths => _places.Paths;

    /// <summary>
    ///     Where a slot last stood or arrived before a tick, and when: a run's arrival, a run's start while it still runs
    ///     at the tick, or a placed entry, whichever is latest. Null when nothing placed it.
    /// </summary>
    /// <param name="slot">The token.</param>
    /// <param name="beforeTick">The tick.</param>
    internal (int Tick, TokenKeyframe At)? LastStand(string slot, int beforeTick)
    {
        int best = int.MinValue;
        foreach (RunSpan run in _runs.TryGetValue(slot, out List<RunSpan>? runs) ? runs : [])
        {
            if (run.StartTick < beforeTick)
            {
                best = Math.Max(best, run.ArriveTick <= beforeTick ? run.ArriveTick : run.StartTick);
            }
        }

        if (_plans.TryGetValue(slot, out SlotPlan? plan))
        {
            if (plan.Start is not null && beforeTick > 0)
            {
                best = Math.Max(best, 0);
            }

            for (int i = 0; i < plan.Placements.Length; i++)
            {
                if (plan.Placements[i] is not null && plan.Ticks[i] < beforeTick)
                {
                    best = Math.Max(best, plan.Ticks[i]);
                }
            }
        }

        return best > int.MinValue && Tracks.FirstOrDefault(t => t.Slot == slot) is { } track && track.TrySample(best, out TokenKeyframe at)
            ? (best, at)
            : null;
    }

    /// <summary>The runs one path step sends: its destinations', or its lurk's rotate walks.</summary>
    /// <param name="pathIndex">The step's position on the path.</param>
    /// <param name="rotate">The rotate walks instead of the destinations.</param>
    public IEnumerable<(string Slot, RunSpan Run)> RunsOf(int pathIndex, bool rotate = false)
    {
        foreach (string slot in TokenSlots.All)
        {
            if (!_runs.TryGetValue(slot, out List<RunSpan>? runs))
            {
                continue;
            }

            foreach (RunSpan run in runs)
            {
                if (run.PathIndex == pathIndex && run.Rotate == rotate)
                {
                    yield return (slot, run);
                }
            }
        }
    }

    /// <summary>
    ///     The step that last put a slot where it stands at a tick: its entry, its destination or its lurk's rotate,
    ///     whichever came latest at or before the tick, the later step on a shared tick. Null when nothing has.
    /// </summary>
    /// <param name="slot">The token.</param>
    /// <param name="tick">The playhead.</param>
    /// <param name="accept">Which path indices may answer; null takes any.</param>
    public (int PathIndex, bool Rotate)? PlacedBy(string slot, int tick, Func<int, bool>? accept = null)
    {
        if (!_plans.TryGetValue(slot, out SlotPlan? plan))
        {
            return null;
        }

        (int Tick, int Order, bool Rotate)? best = null;
        void Offer(int at, int order, bool rotate)
        {
            if (at <= tick && (accept is null || accept(order))
                           && (best is not { } b || at > b.Tick || (at == b.Tick && order >= b.Order)))
            {
                best = (at, order, rotate);
            }
        }

        for (int i = 0; i < plan.Placements.Length; i++)
        {
            if (plan.Placements[i] is not null)
            {
                Offer(plan.Ticks[i], i, false);
            }
        }

        foreach (SlotEvent e in plan.Events)
        {
            if (e.To is not null)
            {
                Offer(e.Tick, e.Order, IsRotate(e));
            }
        }

        return best is { } found ? (found.Order, found.Rotate) : null;
    }

    // A lurk's rotate walk: off its step's tick, and not one of the lurk's later area legs.
    private static bool IsRotate(SlotEvent e) => !e.Shaped && !e.Leg;

    /// <summary>One bend of a route as a keyframe: when the token is there, where, on which level, facing the next leg.</summary>
    internal readonly record struct Corner(int Tick, float X, float Y, double LevelMinZ, float YawDegrees)
    {
        public TokenPlacement Placement => new(X, Y, LevelMinZ, YawDegrees);
    }

    /// <summary>
    ///     A route's inner points as keyframes strictly between <paramref name="fromTick" /> and <paramref name="untilTick" />,
    ///     each at the tick its share of the length puts it, facing the next leg. Points whose ticks collide are pushed onto
    ///     consecutive ticks. With more points than ticks, the ones whose removal leaves the line on the mesh go first, then
    ///     the smallest turns; a floor change goes last. Where the floor changes and a tick is free, a keyframe a tick earlier
    ///     keeps the old floor, so the token switches at the point and not half way along the leg before it.
    /// </summary>
    /// <param name="route">The route, both ends included.</param>
    /// <param name="fromTick">When the token leaves the first point.</param>
    /// <param name="untilTick">When it reaches the last.</param>
    /// <param name="level">The token's level as it leaves.</param>
    /// <param name="paths">Tests a shortcut against the mesh when points must go; null keeps the sharpest turns.</param>
    internal static List<Corner> Corners(IReadOnlyList<NavWaypoint> route, int fromTick, int untilTick, double level,
        PathResolver? paths = null)
    {
        List<Corner> corners = [];
        int room = untilTick - fromTick - 1;
        double total = route.Count < 3 ? 0 : NavPathfinder.Length(route);
        if (total < 1e-6 || room < 1)
        {
            return corners;
        }

        double[] along = new double[route.Count];
        for (int i = 1; i < route.Count; i++)
        {
            along[i] = along[i - 1] + Distance(route[i - 1], route[i]);
        }

        List<int> kept = [.. Enumerable.Range(1, route.Count - 2)];
        if (kept.Count > room)
        {
            Thin(route, kept, room, paths);
        }

        // Ideal ticks, then pushed apart: up from fromTick, then down from untilTick. kept.Count <= room, so both fit.
        int[] ticks = new int[kept.Count];
        for (int j = 0, previous = fromTick; j < kept.Count; j++)
        {
            int ideal = fromTick + (int)Math.Round((untilTick - fromTick) * along[kept[j]] / total, MidpointRounding.AwayFromZero);
            previous = ticks[j] = Math.Max(ideal, previous + 1);
        }

        for (int j = kept.Count - 1, next = untilTick; j >= 0; j--)
        {
            next = ticks[j] = Math.Min(ticks[j], next - 1);
        }

        int lastTick = fromTick;
        NavWaypoint last = route[0];
        for (int j = 0; j < kept.Count; j++)
        {
            NavWaypoint here = route[kept[j]];
            NavWaypoint ahead = route[j + 1 < kept.Count ? kept[j + 1] : route.Count - 1];
            int tick = ticks[j];
            if (here.FloorKey != level && tick - 1 > lastTick)
            {
                double f = (tick - 1 - lastTick) / (double)(tick - lastTick);
                corners.Add(new Corner(tick - 1, (float)(last.X + (here.X - last.X) * f), (float)(last.Y + (here.Y - last.Y) * f), level,
                    Yaw(last, here)));
            }

            corners.Add(new Corner(tick, (float)here.X, (float)here.Y, here.FloorKey, Yaw(here, ahead)));
            level = here.FloorKey;
            lastTick = tick;
            last = here;
        }

        return corners;
    }

    // Drops inner points (indices into route) until `room` are left. Each pass looks at the cheapest few candidates, by
    // floor change then turn, and drops the first whose shortcut stays on the mesh, else the cheapest.
    private static void Thin(IReadOnlyList<NavWaypoint> route, List<int> kept, int room, PathResolver? paths)
    {
        const int Checked = 8;
        while (kept.Count > room)
        {
            int[] order = [.. Enumerable.Range(0, kept.Count).OrderBy(j => Cost(route, kept, j))];
            int drop = order[0];
            for (int c = 0; paths is not null && c < Math.Min(Checked, order.Length); c++)
            {
                int j = order[c];
                NavWaypoint before = route[j == 0 ? 0 : kept[j - 1]];
                NavWaypoint after = route[j + 1 < kept.Count ? kept[j + 1] : route.Count - 1];
                if (paths.Clear(before.X, before.Y, after.X, after.Y, before.FloorKey))
                {
                    drop = j;
                    break;
                }
            }

            kept.RemoveAt(drop);
        }
    }

    // The turn at kept[j] in radians, plus a floor change's weight, so a floor point is the last to go.
    private static double Cost(IReadOnlyList<NavWaypoint> route, List<int> kept, int j)
    {
        int i = kept[j];
        NavWaypoint before = route[j == 0 ? 0 : kept[j - 1]], here = route[i], after = route[j + 1 < kept.Count ? kept[j + 1] : route.Count - 1];
        double turn = Math.Abs(Math.Atan2(
            (here.X - before.X) * (after.Y - here.Y) - (here.Y - before.Y) * (after.X - here.X),
            (here.X - before.X) * (after.X - here.X) + (here.Y - before.Y) * (after.Y - here.Y)));
        return turn + (here.FloorKey != route[i - 1].FloorKey ? 2 * Math.PI : 0);
    }

    private static double Distance(NavWaypoint a, NavWaypoint b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static float Yaw(NavWaypoint from, NavWaypoint to) =>
        (float)Math.Round(StratFromRound.NormalizeYaw(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI), 2);

    // The heading of the leg from point i to point i + 1, or null for a leg too short to face along.
    private static float? Heading(List<NavWaypoint> route, int i)
    {
        if (i < 0 || i + 1 >= route.Count)
        {
            return null;
        }

        double dx = route[i + 1].X - route[i].X, dy = route[i + 1].Y - route[i].Y;
        return dx * dx + dy * dy < 1e-6 ? null : (float)Math.Round(StratFromRound.NormalizeYaw(Math.Atan2(dy, dx) * 180 / Math.PI), 2);
    }

    // The ticks of run entries. A segment ending on one belongs to a run, already routed and timed by its length.
    private static HashSet<int> RunTicks(List<TrackEntry> entries) => [.. entries.Where(x => x.Arrival).Select(x => x.Step.Tick)];

    // A segment whose ends' ticks were fixed by steps (an authored entry, a position verb, a run cut short): a route bends
    // it and keeps both ticks.
    private static bool Bendable(TokenTrack track, int k, HashSet<int> runTicks)
    {
        IReadOnlyList<TokenKeyframe> keys = track.Keyframes;
        if (k < 0 || k + 1 >= keys.Count || track.Segments[k] != TokenInterpolation.Linear || runTicks.Contains(keys[k + 1].Tick))
        {
            return false;
        }

        TokenKeyframe a = keys[k], b = keys[k + 1];
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return dx * dx + dy * dy >= 1 && b.Tick - ((long)a.Tick + track.HoldTicks[k]) >= 2;
    }

    // Segment k as its route's keyframes, both ends included, or null when the route is straight.
    private static List<TokenKeyframe>? BendSegment(TokenTrack track, int k, PathResolver paths)
    {
        TokenKeyframe a = track.Keyframes[k], b = track.Keyframes[k + 1];
        if (paths.Route(a.X, a.Y, a.LevelMinZ, b.X, b.Y, b.LevelMinZ, null) is not { Count: > 2 } route)
        {
            return null;
        }

        List<Corner> corners = Corners(route, a.Tick + track.HoldTicks[k], b.Tick, a.LevelMinZ, paths);
        if (corners.Count == 0)
        {
            return null;
        }

        List<TokenKeyframe> keys = [a];
        keys.AddRange(corners.Select(c => new TokenKeyframe(c.Tick, c.X, c.Y, c.LevelMinZ, c.YawDegrees)));
        keys.Add(b);
        return keys;
    }

    // A sample on the route where a fixed-time segment bends. A run's corners are entries already.
    private static bool TrySampleRouted(TokenTrack track, int tick, List<TrackEntry> entries, PlaceSet places, out TokenKeyframe at)
    {
        if (!track.TrySample(tick, out at))
        {
            return false;
        }

        if (places.Paths is not { } paths)
        {
            return true;
        }

        IReadOnlyList<TokenKeyframe> keys = track.Keyframes;
        int k = keys.Count - 1;
        while (k > 0 && keys[k].Tick > tick)
        {
            k--;
        }

        if (!Bendable(track, k, RunTicks(entries)) || BendSegment(track, k, paths) is not { } bent)
        {
            return true;
        }

        int[] holds = new int[bent.Count];
        holds[0] = track.HoldTicks[k];
        return new TokenTrack(track.Slot, bent, holds).TrySample(tick, out at);
    }

    // The built track with every fixed-time segment bent along its route.
    private static TokenTrack Bent(TokenTrack track, List<TrackEntry> entries, PlaceSet places)
    {
        if (places.Paths is not { } paths || track.Keyframes.Count < 2)
        {
            return track;
        }

        HashSet<int> runTicks = RunTicks(entries);
        List<TokenKeyframe> keys = [];
        List<int> holds = [];
        List<TokenInterpolation> segments = [];
        bool bentAny = false;
        for (int k = 0; k < track.Keyframes.Count; k++)
        {
            keys.Add(track.Keyframes[k]);
            holds.Add(track.HoldTicks[k]);
            segments.Add(track.Segments[k]);
            if (!Bendable(track, k, runTicks) || BendSegment(track, k, paths) is not { } bent)
            {
                continue;
            }

            for (int c = 1; c < bent.Count - 1; c++)
            {
                keys.Add(bent[c]);
                holds.Add(0);
                segments.Add(TokenInterpolation.Linear);
            }

            bentAny = true;
        }

        return bentAny ? new TokenTrack(track.Slot, keys, holds, segments) : track;
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

        (double x, double y) = e.Fan ? SpotFor(e.To.Place, centre, slot, places.Contains, places.Paths) : (centre.X, centre.Y);
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
    /// <param name="paths">
    ///     When given, a spot must also stand on the nav and the ring is snapped onto it; a ring that does not snap is the
    ///     arrival itself.
    /// </param>
    internal static (double X, double Y) SpotFor(string? place, (double X, double Y, double LevelMinZ) centre, string slot,
        PlaceContainsResolver? contains, PathResolver? paths = null)
    {
        int k = Math.Max(0, IndexOfSlot(slot));
        double angle = (90 + 72 * k) * Math.PI / 180;
        (double cos, double sin) = (Math.Cos(angle), Math.Sin(angle));
        if (place is not null && contains is not null)
        {
            foreach (double r in SpotRadii)
            {
                (double x, double y) = (centre.X + r * cos, centre.Y + r * sin);
                if (contains(place, x, y, centre.LevelMinZ) && (paths is null || paths.OnMesh(x, y, centre.LevelMinZ)))
                {
                    return (x, y);
                }
            }
        }

        (double X, double Y) ring = (centre.X + RingRadius * cos, centre.Y + RingRadius * sin);
        return paths is null
            ? ring
            : paths.Snap(ring.X, ring.Y, centre.LevelMinZ, place, NavPathfinder.DefaultSnap) ?? (centre.X, centre.Y);
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
    /// <param name="Via">Where a run goes through first, in order; null for the shortest route.</param>
    /// <param name="Areas">A lurk's areas after <paramref name="To" />, walked in order once it arrives; null for none.</param>
    /// <param name="FanAreas">The indices in <paramref name="Areas" /> another slot stands at while this one does.</param>
    /// <param name="Leg">One of a lurk's later legs: it moves with its step's interpolation.</param>
    internal sealed record SlotEvent(int Tick, int Order, SlotEventKind Kind, PlaceRef? To, StepWatch? Watch, bool Fan, bool Shaped,
        bool Walk = false, IReadOnlyList<PlaceRef>? Via = null, IReadOnlyList<PlaceRef>? Areas = null, IReadOnlySet<int>? FanAreas = null,
        bool Leg = false);

    /// <summary>
    ///     A slot's entries per step, the steps' ticks, its events, whether it has moved since its last authored entry, its
    ///     start, and per step whether the entry is the step's own (a lineup, a drag, a stored entry not read as a copy or as
    ///     the start).
    /// </summary>
    internal sealed record SlotPlan(TokenPlacement?[] Placements, int[] Ticks, List<SlotEvent> Events, bool Moved, TokenPlacement? Start = null,
        bool[]? Authored = null);

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
    /// <param name="Paths">Routes round walls; null keeps every move straight.</param>
    /// <param name="Starts">Each token's start at tick 0 (<see cref="StartsOf" />); null for none.</param>
    /// <param name="Clock">The strat's clock; null is the round clock of <paramref name="RoundSeconds" />.</param>
    /// <param name="SeedEntries">An older file's round-start entries its start was read from, as (step, slot): never read as copies.</param>
    internal sealed record PlaceSet(PlaceCentreResolver? Centres, PlaceArrivalResolver? Arrivals, PlaceContainsResolver? Contains,
        double DefaultLevelMinZ, double RoundSeconds, bool LegacyCarry = false, bool Observed = false, PathResolver? Paths = null,
        IReadOnlyDictionary<string, TokenPlacement>? Starts = null, StratClockInfo? Clock = null,
        IReadOnlySet<(int Step, string Slot)>? SeedEntries = null)
    {
        /// <summary>The clock, its round length filled.</summary>
        public StratClockInfo ClockOrRound => Clock ?? new StratClockInfo { RoundSeconds = RoundSeconds };
    }

    /// <summary>Whether Create Strat From Round or Strat Mining made the strat: it has an origin or the mined tag.</summary>
    /// <param name="document">The strat.</param>
    public static bool IsCaptured(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Origin is not null || document.Tags.Contains(MinedStratBuilder.Tag, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Whether every position in the strat reads as observed: a captured or mined strat written before positions
    ///     carried the <c>observed</c> mark. A capture made since marks each entry it writes, and only those.
    /// </summary>
    /// <param name="document">The strat.</param>
    public static bool IsLegacyObserved(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (document.Origin is not null || document.Tags.Contains(MinedStratBuilder.Tag, StringComparer.Ordinal))
               && !document.Steps.Any(s => s.Positions.Any(p => p.Observed is not null))
               && document.Start?.Positions.Any(p => p.Observed is not null) != true;
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
    ///     The entries a file from before the carried mark reads as carried copies (<see cref="IsLegacyCarry" />), as
    ///     (step, entry): what has to be marked when this build writes its first mark without changing what plays. Empty
    ///     for any other file.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="throwOrigins">The canvas's lineup resolver, since a lineup origin is a placement copies match; null for none.</param>
    /// <param name="spawns">The map's spawns, as the projection reads the start; null for none.</param>
    public static IReadOnlyList<StartSource> LegacyCarriedEntries(StratDocument document, ThrowOriginResolver? throwOrigins = null,
        StratSpawns? spawns = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsLegacyCarry(document))
        {
            return [];
        }

        PlaceSet places = PlacesOf(document, null, null, null, null, spawns);
        List<StartSource> found = [];
        foreach (string slot in TokenSlots.All)
        {
            TokenPlacement? lastStored = places.Starts is { } starts && starts.TryGetValue(slot, out TokenPlacement s0) ? s0 : null;
            for (int i = 0; i < document.Steps.Count; i++)
            {
                StratStep step = document.Steps[i];
                ThrowOrigin? origin = throwOrigins is null ? null : ThrowOriginOf(document.Map, step, throwOrigins);
                TokenPlacement? stored = PlacementOf(step, slot, out bool marked, out _);
                if (stored is { } s && !marked && places.SeedEntries?.Contains((i, slot)) != true && SameSpot(s, lastStored))
                {
                    found.Add(new StartSource(i, step.Positions.FindLastIndex(p => string.Equals(p.Slot, slot, StringComparison.Ordinal)
                                                                                   && double.IsFinite(p.X) && double.IsFinite(p.Y))));
                }

                lastStored = origin is not null && string.Equals(origin.Slot, slot, StringComparison.Ordinal) ? origin.Placement : stored ?? lastStored;
            }
        }

        return found;
    }

    /// <summary>
    ///     The yaw a watch turns a token standing at <paramref name="at" /> to: the explicit angle, else towards
    ///     the first watched entry (places, then points), passing over any the token stands on. Null when neither
    ///     applies (no angle, an entry the map lacks, or the token stands on every one).
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

        // An unknown entry stops the search, so a watch does not face its second entry until the zones load.
        foreach (PlaceRef watched in StratLocations.Watched(watch))
        {
            if (Where(watched, at.LevelMinZ, centres) is not { } centre)
            {
                return null;
            }

            double dx = centre.X - at.X, dy = centre.Y - at.Y;
            if (dx * dx + dy * dy >= MinFacingDistance * MinFacingDistance)
            {
                return (float)Math.Round(StratFromRound.NormalizeYaw(Math.Atan2(dy, dx) * 180 / Math.PI), 2);
            }
        }

        return null;
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
