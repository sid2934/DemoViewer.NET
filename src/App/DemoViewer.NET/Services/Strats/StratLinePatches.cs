#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The one writer of a step's lines. An edit takes the step's lines as <see cref="StratStepLines.Of" /> reads
///     them, changes a copy, and <see cref="Write" /> turns the result into ops in the stored shape:
///     no lines is a step for <c>all</c>; one line with no watch is a plain step (<c>actor</c> and <c>to</c>);
///     anything else is <c>assignments</c> with the summary <c>actor</c> and no step-level <c>to</c>.
/// </summary>
public static class StratLinePatches
{
    /// <summary>A deep copy of the step's lines, stored or implicit, safe to change.</summary>
    /// <param name="step">The step.</param>
    /// <param name="expandAll">
    ///     Read a step for <c>all</c> as a line per slot, each with the step's place, so an edit of one player's line
    ///     keeps the others in the step.
    /// </param>
    public static List<StepAssignment> Copy(StratStep step, bool expandAll = false)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (expandAll && !StratStepLines.HasLines(step) && string.Equals(step.Actor, StratVocabulary.ActorAll, StringComparison.Ordinal))
        {
            return [.. StratVocabulary.Slots.Select(s => new StepAssignment { Slot = s, To = step.To is { } to ? Clone(to) : null })];
        }

        return [.. StratStepLines.Of(step).Select(Clone)];
    }

    /// <summary>A copy of one line, unknown fields included.</summary>
    /// <param name="line">The line.</param>
    public static StepAssignment Clone(StepAssignment line) =>
        JsonSerializer.SerializeToNode(line, StratJsonContext.Default.StepAssignment)!
            .Deserialize(StratJsonContext.Default.StepAssignment)!;

    /// <summary>Whether a line carries nothing but a slot and a place, so a step of it alone stays plain.</summary>
    /// <param name="line">The line.</param>
    public static bool IsBare(StepAssignment line) =>
        line.Extra is not { Count: > 0 } && (line.Watch is null || IsEmpty(line.Watch));

    /// <summary>A watch with no place, no angle and no unknown field: stored as no watch.</summary>
    /// <param name="watch">The watch.</param>
    public static bool IsEmpty(StepWatch watch) =>
        watch.Places.Count == 0 && watch.YawDegrees is null && watch.Extra is not { Count: > 0 };

    /// <summary>
    ///     Ops that rewrite a step's who and where to <paramref name="lines" />. Empty when nothing changes. A line
    ///     added at the end, or one removed, is a single op on <c>assignments</c>; equal counts change per member.
    /// </summary>
    /// <param name="step">The step as it stands.</param>
    /// <param name="stepPath">Its pointer, <c>/steps/{i}</c>.</param>
    /// <param name="lines">What the lines should be.</param>
    public static List<PatchOp> Write(StratStep step, string stepPath, IReadOnlyList<StepAssignment> lines)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(lines);
        List<StepAssignment> target = [.. lines.Select(Clone)];
        foreach (StepAssignment line in target)
        {
            if (line.Watch is { } watch && IsEmpty(watch))
            {
                line.Watch = null;
            }
        }

        List<PatchOp> ops = [];
        bool stored = StratStepLines.HasLines(step);

        // Five bare lines to one place are a step for all, which is how an expanded step for all folds back.
        if (target.Count == StratVocabulary.Slots.Count && target.All(IsBare)
                                                        && StratVocabulary.Slots.All(s => target.Exists(l => l.Slot == s))
                                                        && target.All(l => SamePlace(l.To, target[0].To)))
        {
            PlaceRef? shared = target[0].To;
            target = [];
            if (!SamePlace(step.To, shared))
            {
                ops.Add(shared is null ? PatchOp.RemoveOp(stepPath + "/to", null) : PatchOp.ReplaceOp(stepPath + "/to", null, Node(shared)));
            }
        }

        if (target.Count == 0 || (target.Count == 1 && IsBare(target[0])))
        {
            if (stored)
            {
                ops.Add(PatchOp.RemoveOp(stepPath + "/assignments", null));
            }

            string actor = target.Count == 0 ? StratVocabulary.ActorAll : target[0].Slot;
            if (!string.Equals(step.Actor, actor, StringComparison.Ordinal))
            {
                ops.Add(PatchOp.ReplaceOp(stepPath + "/actor", null, JsonValue.Create(actor)));
            }

            // No lines keeps the step's own to: it is where everyone goes.
            if (target.Count == 1 && !SamePlace(step.To, target[0].To))
            {
                ops.Add(target[0].To is { } to
                    ? PatchOp.ReplaceOp(stepPath + "/to", null, Node(to))
                    : PatchOp.RemoveOp(stepPath + "/to", null));
            }

            return ops;
        }

        string summary = StratStepLines.ActorFor(target);
        if (!string.Equals(step.Actor, summary, StringComparison.Ordinal))
        {
            ops.Add(PatchOp.ReplaceOp(stepPath + "/actor", null, JsonValue.Create(summary)));
        }

        if (step.To is not null)
        {
            ops.Add(PatchOp.RemoveOp(stepPath + "/to", null));
        }

        if (!stored)
        {
            ops.Add(PatchOp.ReplaceOp(stepPath + "/assignments", null, new JsonArray(target.Select(l => (JsonNode?)Node(l)).ToArray())));
            return ops;
        }

        List<StepAssignment> before = step.Assignments!;
        string array = stepPath + "/assignments";
        if (target.Count == before.Count + 1 && before.Select(Json).SequenceEqual(target.Take(before.Count).Select(Json)))
        {
            ops.Add(PatchOp.AddOp(Invariant($"{array}/{before.Count}"), Node(target[^1])));
        }
        else if (target.Count == before.Count - 1 && RemovedAt(before, target) is { } removed)
        {
            ops.Add(PatchOp.RemoveOp(Invariant($"{array}/{removed}"), null));
        }
        else if (target.Count == before.Count)
        {
            for (int j = 0; j < target.Count; j++)
            {
                MemberOps(ops, Invariant($"{array}/{j}"), before[j], target[j]);
            }
        }
        else
        {
            ops.Add(PatchOp.ReplaceOp(array, null, new JsonArray(target.Select(l => (JsonNode?)Node(l)).ToArray())));
        }

        return ops;
    }

    /// <summary><see cref="Write" /> of the step's lines after <paramref name="edit" /> changes a copy of them.</summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="edit">Changes the copy.</param>
    public static List<PatchOp> Edit(StratStep step, string stepPath, Action<List<StepAssignment>> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        List<StepAssignment> lines = Copy(step);
        edit(lines);
        return Write(step, stepPath, lines);
    }

    /// <summary>
    ///     The slot's line with <paramref name="edit" /> applied, adding the line when the slot has none. A step for
    ///     <c>all</c> is read as a line per slot first, so the others stay in it.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="slot">A slot letter.</param>
    /// <param name="edit">Changes the line.</param>
    public static List<PatchOp> EditLine(StratStep step, string stepPath, string slot, Action<StepAssignment> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        List<StepAssignment> lines = Copy(step, true);
        StepAssignment? line = lines.Find(l => string.Equals(l.Slot, slot, StringComparison.Ordinal));
        if (line is null)
        {
            line = new StepAssignment { Slot = slot };
            lines.Add(line);
        }

        edit(line);

        return Write(step, stepPath, lines);
    }

    /// <summary>The slot's view angle, adding its line when it has none; null clears the angle.</summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="slot">A slot letter.</param>
    /// <param name="yawDegrees">World yaw, or null.</param>
    public static List<PatchOp> WatchYaw(StratStep step, string stepPath, string slot, double? yawDegrees) =>
        EditLine(step, stepPath, slot, line =>
        {
            line.Watch ??= new StepWatch();
            line.Watch.YawDegrees = yawDegrees is { } yaw ? Math.Round(StratFromRound.NormalizeYaw(yaw), 2) : null;
        });

    /// <summary>The first slot with no line on the step, or null when all five have one.</summary>
    /// <param name="lines">The lines.</param>
    public static string? FreeSlot(IReadOnlyList<StepAssignment> lines) =>
        StratVocabulary.Slots.FirstOrDefault(s => lines.All(l => !string.Equals(l.Slot, s, StringComparison.Ordinal)));

    private static void MemberOps(List<PatchOp> ops, string path, StepAssignment before, StepAssignment after)
    {
        if (!string.Equals(before.Slot, after.Slot, StringComparison.Ordinal))
        {
            ops.Add(PatchOp.ReplaceOp(path + "/slot", null, JsonValue.Create(after.Slot)));
        }

        if (!SamePlace(before.To, after.To))
        {
            ops.Add(after.To is { } to ? PatchOp.ReplaceOp(path + "/to", null, Node(to)) : PatchOp.RemoveOp(path + "/to", null));
        }

        string? beforeWatch = before.Watch is null ? null : JsonSerializer.Serialize(before.Watch, StratJsonContext.Default.StepWatch);
        string? afterWatch = after.Watch is null ? null : JsonSerializer.Serialize(after.Watch, StratJsonContext.Default.StepWatch);
        if (string.Equals(beforeWatch, afterWatch, StringComparison.Ordinal))
        {
            return;
        }

        if (after.Watch is null)
        {
            ops.Add(PatchOp.RemoveOp(path + "/watch", null));
        }
        else if (before.Watch is not null && string.Equals(WithoutYaw(before.Watch), WithoutYaw(after.Watch), StringComparison.Ordinal))
        {
            // Only the angle: its own op, which the history phrases as the view angle.
            ops.Add(after.Watch.YawDegrees is { } yaw
                ? PatchOp.ReplaceOp(path + "/watch/yawDegrees", null, JsonValue.Create(yaw))
                : PatchOp.RemoveOp(path + "/watch/yawDegrees", null));
        }
        else
        {
            ops.Add(PatchOp.ReplaceOp(path + "/watch", null, JsonSerializer.SerializeToNode(after.Watch, StratJsonContext.Default.StepWatch)));
        }
    }

    private static string WithoutYaw(StepWatch watch) =>
        JsonSerializer.Serialize(new StepWatch { Places = watch.Places, Extra = watch.Extra }, StratJsonContext.Default.StepWatch);

    private static int? RemovedAt(List<StepAssignment> before, List<StepAssignment> after)
    {
        for (int j = 0; j < before.Count; j++)
        {
            if (before.Where((_, k) => k != j).Select(Json).SequenceEqual(after.Select(Json)))
            {
                return j;
            }
        }

        return null;
    }

    private static bool SamePlace(PlaceRef? a, PlaceRef? b) =>
        (a is null && b is null) || (a is not null && b is not null
                                                   && JsonSerializer.Serialize(a, StratJsonContext.Default.PlaceRef)
                                                   == JsonSerializer.Serialize(b, StratJsonContext.Default.PlaceRef));

    private static string Json(StepAssignment line) => JsonSerializer.Serialize(line, StratJsonContext.Default.StepAssignment);

    private static JsonNode Node(StepAssignment line) => JsonSerializer.SerializeToNode(line, StratJsonContext.Default.StepAssignment)!;

    private static PlaceRef Clone(PlaceRef place) =>
        JsonSerializer.SerializeToNode(place, StratJsonContext.Default.PlaceRef)!.Deserialize(StratJsonContext.Default.PlaceRef)!;

    private static JsonNode Node(PlaceRef place) => JsonSerializer.SerializeToNode(place, StratJsonContext.Default.PlaceRef)!;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
