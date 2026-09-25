#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     Turns a closed canvas gesture into <see cref="PatchOp" />s against the strat (step-authoring.md §3.8,
///     overview correction 18): the canvas's one history is <see cref="StratSession" />'s, so a stroke, an
///     erase, a token drag or a step edit reaches it as ops and nothing else. The session fills every
///     <c>from</c> itself; the ops here name only paths and values.
///     <para>
///         <b>Pure.</b> Each method reads the document it is handed and returns ops; applying them is the
///         caller's, as one undo entry per gesture.
///     </para>
/// </summary>
public static class StepAuthoringPatches
{
    /// <summary>How far after the active step <see cref="DuplicateStep" /> puts the copy (§3.7).</summary>
    public const double DuplicateOffsetSeconds = 5;

    /// <summary>
    ///     A token drag as one op: a <c>replace</c> of the slot's entry at the step, or an <c>add</c> when the
    ///     step had none for it. Forty moves collapse into this one op, which is the drag's whole history.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step the drag wrote.</param>
    /// <param name="slot">The token.</param>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="levelMinZ">The level key.</param>
    /// <param name="yawDegrees">The facing, or null to leave the entry's (absent means the previous keyframe's).</param>
    public static PatchOp TokenPosition(StratDocument document, int stepIndex, string slot, double x, double y,
        double levelMinZ, double? yawDegrees)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<StepPosition> positions = document.Steps[stepIndex].Positions;
        int existing = positions.FindLastIndex(p => string.Equals(p.Slot, slot, StringComparison.Ordinal));

        // From the stored entry, so a field a newer build wrote on it survives the drag.
        StepPosition position = existing >= 0 ? Clone(positions[existing]) : new StepPosition { Slot = slot };
        position.X = Round(x);
        position.Y = Round(y);
        position.LevelMinZ = levelMinZ;
        if (yawDegrees is { } yaw)
        {
            position.YawDegrees = Round(Normalize(yaw));
        }

        JsonNode? value = JsonSerializer.SerializeToNode(position, StratJsonContext.Default.StepPosition);
        return existing >= 0
            ? PatchOp.ReplaceOp(Pointer($"/steps/{stepIndex}/positions/{existing}"), null, value)
            : PatchOp.AddOp(Pointer($"/steps/{stepIndex}/positions/-"), value);
    }

    /// <summary>
    ///     The ops that carry an ink gesture into the strat: what changed between the projected elements and
    ///     the document's elements after the gesture. Replaces first, while every index still names what it
    ///     meant; then removes, highest index first per step; then appends. An element whose step cannot be
    ///     written (another strat's, or none at all) yields nothing, and the caller re-projects it away.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="projection">The projection the gesture started from.</param>
    /// <param name="after">The ink document's elements once the gesture closed.</param>
    /// <param name="stepFor">Which path step a new element belongs to, or -1.</param>
    public static IReadOnlyList<PatchOp> FromInk(StratDocument document, StratSceneProjection projection,
        IReadOnlyList<AnnotationElement> after, Func<AnnotationElement, int> stepFor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(stepFor);

        Dictionary<Guid, AnnotationElement> before = projection.Elements.ToDictionary(e => e.Id);
        HashSet<Guid> present = [.. after.Select(e => e.Id)];

        List<PatchOp> replaces = [];
        List<(int Step, int Stroke)> removes = [];
        List<PatchOp> adds = [];

        foreach (AnnotationElement element in after)
        {
            if (before.TryGetValue(element.Id, out AnnotationElement? was))
            {
                if (was.Equals(element) || !projection.TryFindStroke(element.Id, out StrokeRef at)
                                        || !projection.Path[at.PathIndex].Editable)
                {
                    continue;
                }

                int stepIndex = projection.Path[at.PathIndex].StepIndex;
                JsonObject original = document.Steps[stepIndex].Strokes[at.StrokeIndex];
                replaces.Add(PatchOp.ReplaceOp(Pointer($"/steps/{stepIndex}/strokes/{at.StrokeIndex}"), null,
                    StratStrokes.ToJson(element, original)));
                continue;
            }

            int pathIndex = stepFor(element);
            if (pathIndex < 0 || pathIndex >= projection.Path.Count || !projection.Path[pathIndex].Editable)
            {
                continue;
            }

            adds.Add(PatchOp.AddOp(Pointer($"/steps/{projection.Path[pathIndex].StepIndex}/strokes/-"),
                StratStrokes.ToJson(element)));
        }

        foreach (AnnotationElement element in projection.Elements)
        {
            if (!present.Contains(element.Id) && projection.TryFindStroke(element.Id, out StrokeRef at)
                                              && projection.Path[at.PathIndex].Editable)
            {
                removes.Add((projection.Path[at.PathIndex].StepIndex, at.StrokeIndex));
            }
        }

        return [.. replaces, .. RemoveStrokes(removes), .. adds];
    }

    /// <summary>One <c>remove</c> per stroke, highest index first within a step so each pointer still names its stroke.</summary>
    /// <param name="strokes">Step index and stroke index pairs.</param>
    public static IReadOnlyList<PatchOp> RemoveStrokes(IEnumerable<(int Step, int Stroke)> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        return
        [
            .. strokes.Distinct()
                .OrderBy(s => s.Step)
                .ThenByDescending(s => s.Stroke)
                .Select(s => PatchOp.RemoveOp(Pointer($"/steps/{s.Step}/strokes/{s.Stroke}"), null))
        ];
    }

    /// <summary>Every stroke of one step removed: Ctrl+X on the canvas clears the active step, not the strat (§3.7).</summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step.</param>
    public static IReadOnlyList<PatchOp> ClearStrokes(StratDocument document, int stepIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        return RemoveStrokes(Enumerable.Range(0, document.Steps[stepIndex].Strokes.Count).Select(k => (stepIndex, k)));
    }

    /// <summary>
    ///     A new step inserted after <paramref name="afterIndex" /> (or first, at -1), at a round-clock time
    ///     held between its neighbours so the clock still counts down. A move for every slot, with nothing
    ///     placed: the stationary rule keeps each token where it was until the author drags it.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="afterIndex">The step it follows, or -1 to insert first.</param>
    /// <param name="atSeconds">The wanted time; clamped between the neighbours.</param>
    /// <param name="id">The new step's id.</param>
    public static PatchOp AddStep(StratDocument document, int afterIndex, double atSeconds, Guid id)
    {
        ArgumentNullException.ThrowIfNull(document);
        StratStep step = new()
        {
            Id = id,
            AtSeconds = ClampBetween(document, afterIndex, atSeconds),
            Actor = StratVocabulary.ActorAll,
            Verb = "move"
        };
        return PatchOp.AddOp(Pointer($"/steps/{afterIndex + 1}"),
            JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep));
    }

    /// <summary>
    ///     A copy of a step's positions and strokes as a new step <see cref="DuplicateOffsetSeconds" /> later,
    ///     right after it. The strokes get new ids: two elements with one id cannot both be on the canvas.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="index">The step to copy.</param>
    /// <param name="id">The copy's id.</param>
    public static PatchOp DuplicateStep(StratDocument document, int index, Guid id)
    {
        ArgumentNullException.ThrowIfNull(document);
        StratStep source = document.Steps[index];
        JsonObject node = JsonSerializer.SerializeToNode(source, StratJsonContext.Default.StratStep)!.AsObject();
        node["id"] = id.ToString("D", CultureInfo.InvariantCulture);
        node["atSeconds"] = ClampBetween(document, index, source.AtSeconds - DuplicateOffsetSeconds);

        if (node["strokes"] is JsonArray strokes)
        {
            foreach (JsonNode? stroke in strokes)
            {
                if (stroke is JsonObject obj)
                {
                    obj["id"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
                }
            }
        }

        return PatchOp.AddOp(Pointer($"/steps/{index + 1}"), node);
    }

    /// <summary>
    ///     A step removed with every branch that hangs from it or targets it, as the step table removes one: a
    ///     branch left pointing at nothing is refused.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="index">The step.</param>
    public static IReadOnlyList<PatchOp> DeleteStep(StratDocument document, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        Guid id = document.Steps[index].Id;
        List<PatchOp> ops = [];
        for (int b = document.Branches.Count - 1; b >= 0; b--)
        {
            StratBranch branch = document.Branches[b];
            if (branch.AfterStepId == id || (branch.Target.StratId == document.Id && branch.Target.StepId == id))
            {
                ops.Add(PatchOp.RemoveOp(Pointer($"/branches/{b}"), null));
            }
        }

        ops.Add(PatchOp.RemoveOp(Pointer($"/steps/{index}"), null));
        return ops;
    }

    /// <summary>
    ///     Positions for every slot the path never places, in a row across the middle of the map at the
    ///     active step: without a keyframe a token is not drawn, so there is nothing to drag. The strat's own
    ///     five sit left of centre, the opponents right. One gesture, so one undo entry.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step the entries land on.</param>
    /// <param name="slots">The slots with no track.</param>
    /// <param name="centreX">World X of the map's middle.</param>
    /// <param name="centreY">World Y of the map's middle.</param>
    /// <param name="spacing">World distance between tokens.</param>
    /// <param name="levelMinZ">The level to place them on.</param>
    public static IReadOnlyList<PatchOp> PlaceTokens(StratDocument document, int stepIndex, IReadOnlyList<string> slots,
        double centreX, double centreY, double spacing, double levelMinZ)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(slots);
        List<PatchOp> ops = [];
        int own = 0, other = 0;
        foreach (string slot in slots)
        {
            bool opponent = StratVocabulary.OpponentSlots.Contains(slot);
            int n = opponent ? other++ : own++;
            double x = centreX + (opponent ? 1 : -1) * spacing * 1.5;
            double y = centreY + (n - 2) * spacing;
            StepPosition position = new() { Slot = slot, X = Round(x), Y = Round(y), LevelMinZ = levelMinZ };
            ops.Add(PatchOp.AddOp(Pointer($"/steps/{stepIndex}/positions/-"),
                JsonSerializer.SerializeToNode(position, StratJsonContext.Default.StepPosition)));
        }

        return ops;
    }

    // A time between the step it follows and the one after, so an insert never breaks the countdown the
    // validator enforces. Rounded to a tick: the canvas's clock has no finer grain to show.
    private static double ClampBetween(StratDocument document, int afterIndex, double atSeconds)
    {
        double upper = afterIndex >= 0 && afterIndex < document.Steps.Count ? document.Steps[afterIndex].AtSeconds : double.PositiveInfinity;
        double lower = afterIndex + 1 < document.Steps.Count ? document.Steps[afterIndex + 1].AtSeconds : double.NegativeInfinity;
        double clamped = Math.Max(lower, Math.Min(upper, atSeconds));
        return Math.Round(clamped * StepSchedule.TicksPerSecond, MidpointRounding.AwayFromZero) / StepSchedule.TicksPerSecond;
    }

    private static StepPosition Clone(StepPosition position) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(position, StratJsonContext.Default.StepPosition),
            StratJsonContext.Default.StepPosition)!;

    private static double Normalize(double yaw)
    {
        double wrapped = yaw % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }

    // Two decimals in the file: world units finer than that are pointer noise, and a strat is read by people.
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Pointer(FormattableString path) => path.ToString(CultureInfo.InvariantCulture);
}
