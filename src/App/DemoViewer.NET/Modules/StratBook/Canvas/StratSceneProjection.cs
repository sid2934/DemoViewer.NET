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

    private readonly Dictionary<Guid, StrokeRef> _strokes;
    private readonly TokenStep[] _tokenSteps;

    private StratSceneProjection(IReadOnlyList<StratPathStep> path, StepSchedule schedule, IReadOnlyList<int> ticks,
        TokenStep[] tokenSteps, IReadOnlyList<TokenTrack> tracks, IReadOnlyList<AnnotationElement> elements,
        Dictionary<Guid, StrokeRef> strokes, IReadOnlyList<TokenLabel> labels, IReadOnlyList<UtilityCue> utility,
        int roundSeconds, StratCanvas canvas, bool clockClamped)
    {
        _tokenSteps = tokenSteps;
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

    /// <summary>Smoke and fire landings from steps that carry a landing point.</summary>
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

    /// <summary>Builds the projection of a path.</summary>
    /// <param name="document">The open strat: its side, clock and canvas block.</param>
    /// <param name="path">The steps to play, from <see cref="StratPath" />.</param>
    public static StratSceneProjection Build(StratDocument document, IReadOnlyList<StratPathStep> path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);

        double roundSeconds = document.Clock.RoundSeconds > 0 ? document.Clock.RoundSeconds : StratClock.DefaultRoundSeconds;
        StratCanvas canvas = document.Canvas ?? new StratCanvas();

        // Ticks from each step's own time, held non-decreasing: the schedule refuses a clock that runs
        // backwards, and a strat mid-edit in the step table (or a branch into a strat whose times start
        // earlier) is still worth drawing in order.
        int[] ticks = new int[path.Count];
        bool clamped = false;
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

        StepSchedule schedule = new(path.Select((p, i) => (p.Step.Id, ticks[i])));

        TokenStep[] tokenSteps = new TokenStep[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            StratStep step = path[i].Step;
            tokenSteps[i] = new TokenStep(ticks[i], HoldTicks(step.HoldSeconds), InterpolationOf(step.Interpolation));
        }

        List<TokenTrack> tracks = [];
        TokenPlacement?[] placements = new TokenPlacement?[path.Count];
        foreach (string slot in TokenSlots.All)
        {
            bool any = false;
            for (int i = 0; i < path.Count; i++)
            {
                placements[i] = PlacementOf(path[i].Step, slot);
                any |= placements[i] is not null;
            }

            if (any)
            {
                tracks.Add(TokenTrackBuilder.Build(slot, tokenSteps, placements));
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

        return new StratSceneProjection(path, schedule, ticks, tokenSteps, tracks, elements, strokes, LabelsFor(document),
            UtilityFor(path, ticks), (int)Math.Round(roundSeconds), canvas, clamped);
    }

    /// <summary>
    ///     A slot's track with one path step's entry swapped for <paramref name="placement" />: what the
    ///     canvas shows mid-drag, before the drag closes into an op and a new projection.
    /// </summary>
    /// <param name="slot">The token.</param>
    /// <param name="pathIndex">The step being written.</param>
    /// <param name="placement">The entry the drag has reached.</param>
    public TokenTrack TrackWith(string slot, int pathIndex, TokenPlacement placement)
    {
        TokenPlacement?[] placements = new TokenPlacement?[Path.Count];
        for (int i = 0; i < Path.Count; i++)
        {
            placements[i] = i == pathIndex ? placement : PlacementOf(Path[i].Step, slot);
        }

        return TokenTrackBuilder.Build(slot, _tokenSteps, placements);
    }

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

    private static TokenPlacement? PlacementOf(StratStep step, string slot)
    {
        // The last entry for a slot wins: a document holding two is refused by nothing today, and the later
        // one is what an edit that appended it meant.
        TokenPlacement? found = null;
        foreach (StepPosition position in step.Positions)
        {
            if (!string.Equals(position.Slot, slot, StringComparison.Ordinal)
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            {
                continue;
            }

            found = new TokenPlacement((float)position.X, (float)position.Y,
                double.IsFinite(position.LevelMinZ) ? position.LevelMinZ : 0,
                position.YawDegrees is { } yaw && double.IsFinite(yaw) ? (float)yaw : null);
        }

        return found;
    }

    private static List<UtilityCue> UtilityFor(IReadOnlyList<StratPathStep> path, int[] ticks)
    {
        List<UtilityCue> cues = [];
        for (int i = 0; i < path.Count; i++)
        {
            if (path[i].Step.Utility is not { Landing: { X: { } x, Y: { } y } landing } utility
                || GrenadeOf(utility.Kind) is not { } kind)
            {
                continue;
            }

            // The middle of the level's first quantum, as a token's marker Z is, so the effect lands on the
            // pane of the floor the landing names.
            float z = (float)((landing.LevelMinZ ?? 0) + MapSpace.LevelQuantum / 2);
            cues.Add(new UtilityCue(ticks[i], kind, (float)x, (float)y, z));
        }

        return cues;
    }

    private static GrenadeKind? GrenadeOf(string? kind) => kind switch
    {
        "smoke" => GrenadeKind.Smoke,
        "molotov" => GrenadeKind.Molotov,
        "he" => GrenadeKind.He,
        "flash" => GrenadeKind.Flash,
        "decoy" => GrenadeKind.Decoy,
        _ => null
    };
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
