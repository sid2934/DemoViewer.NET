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
            return [.. StratVocabulary.Slots.Select(s => new StepAssignment
            {
                Slot = s,
                To = step.To is { } to ? Clone(to) : null,
                Via = step.Via is { } via ? [.. via] : null,
                ViaPoints = step.ViaPoints is { } points ? [.. points.Select(Clone)] : null
            })];
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

    /// <summary>A watch with no place, no point, no angle and no unknown field: stored as no watch.</summary>
    /// <param name="watch">The watch.</param>
    public static bool IsEmpty(StepWatch watch) =>
        watch.Places.Count == 0 && watch.Points is not { Count: > 0 } && watch.YawDegrees is null && watch.Extra is not { Count: > 0 };

    /// <summary>
    ///     Ops that rewrite a step's who and where to <paramref name="lines" />. Empty when nothing changes. A line
    ///     added at the end, or one removed, is a single op on <c>assignments</c>; equal counts change per member.
    /// </summary>
    /// <param name="step">The step as it stands.</param>
    /// <param name="stepPath">Its pointer, <c>/steps/{i}</c>.</param>
    /// <param name="lines">What the lines should be.</param>
    /// <param name="bySlot">
    ///     A change of who: when the kept lines are unchanged and in order and the new ones come last, a <c>remove</c>
    ///     per player dropped and an <c>add</c> per player added; otherwise the whole array. Never an index-wise rewrite
    ///     that moves one player's line onto another.
    /// </param>
    public static List<PatchOp> Write(StratStep step, string stepPath, IReadOnlyList<StepAssignment> lines, bool bySlot = false)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(lines);
        List<PatchOp> ops = WriteLines(step, stepPath, lines, bySlot);
        if (ops.Count > 0 && StratStepFields.MovesToTo(step.Verb))
        {
            PlaceRef? DestinationAfter(string slot) =>
                lines.Count == 0 ? step.To : lines.FirstOrDefault(l => string.Equals(l.Slot, slot, StringComparison.Ordinal))?.To;
            ops.AddRange(DropCarried(step, stepPath, StratVocabulary.Slots.Where(s =>
                StratLocations.IsSet(DestinationAfter(s)) && !SamePlace(StratStepLines.LocationFor(step, s), DestinationAfter(s)))));
        }

        return ops;
    }

    /// <summary>
    ///     The <c>remove</c> of each carried position (<see cref="StepPosition.Carried" />) the step holds for
    ///     <paramref name="slots" />, highest index first: Add step copied where the token stood, and a destination set
    ///     since says where it goes. A position a person placed is never removed.
    /// </summary>
    /// <param name="step">The step as it stands.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="slots">The slots whose destination the edit sets.</param>
    public static IEnumerable<PatchOp> DropCarried(StratStep step, string stepPath, IEnumerable<string> slots)
    {
        ArgumentNullException.ThrowIfNull(step);
        HashSet<string> set = [.. slots];
        for (int k = step.Positions.Count - 1; k >= 0; k--)
        {
            if (step.Positions[k].Carried == true && set.Contains(step.Positions[k].Slot))
            {
                yield return PatchOp.RemoveOp(Invariant($"{stepPath}/positions/{k}"), null);
            }
        }
    }

    private static List<PatchOp> WriteLines(StratStep step, string stepPath, IReadOnlyList<StepAssignment> lines, bool bySlot)
    {
        List<StepAssignment> target = [.. lines.Select(Clone)];
        target.ForEach(Normalize);

        List<PatchOp> ops = [];
        bool stored = StratStepLines.HasLines(step);

        // Five bare lines to one place are a step for all, which is how an expanded step for all folds back.
        if (target.Count == StratVocabulary.Slots.Count && target.All(IsBare)
                                                        && StratVocabulary.Slots.All(s => target.Exists(l => l.Slot == s))
                                                        && target.All(l => SamePlace(l.To, target[0].To) && SameVia(l, target[0])))
        {
            StepAssignment shared = target[0];
            target = [];
            if (!SamePlace(step.To, shared.To))
            {
                ops.Add(shared.To is null ? PatchOp.RemoveOp(stepPath + "/to", null) : PatchOp.ReplaceOp(stepPath + "/to", null, Node(shared.To)));
            }

            StepViaOps(ops, stepPath, step, shared.Via, shared.ViaPoints);
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

            // No lines keeps the step's own to and via: it is where everyone goes.
            if (target.Count == 1 && !SamePlace(step.To, target[0].To))
            {
                ops.Add(target[0].To is { } to
                    ? PatchOp.ReplaceOp(stepPath + "/to", null, Node(to))
                    : PatchOp.RemoveOp(stepPath + "/to", null));
            }

            if (target.Count == 1)
            {
                StepViaOps(ops, stepPath, step, target[0].Via, target[0].ViaPoints);
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

        StepViaOps(ops, stepPath, step, null, null);
        if (!stored)
        {
            ops.Add(PatchOp.AddOp(stepPath + "/assignments", new JsonArray(target.Select(l => (JsonNode?)Node(l)).ToArray())));
            return ops;
        }

        List<StepAssignment> before = step.Assignments!;
        string array = stepPath + "/assignments";
        if (bySlot && !before.Select(l => l.Slot).ToHashSet().SetEquals(target.Select(l => l.Slot)))
        {
            // Never index-wise: that would hand one player's unknown fields to another.
            ops.AddRange(BySlot(before, target, array)
                         ?? [PatchOp.ReplaceOp(array, null, new JsonArray(target.Select(l => (JsonNode?)Node(l)).ToArray()))]);
        }
        else if (target.Count == before.Count + 1 && before.Select(Json).SequenceEqual(target.Take(before.Count).Select(Json)))
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
    /// <param name="expandAll">Read a step for <c>all</c> as a line per slot (<see cref="Copy" />).</param>
    public static List<PatchOp> Edit(StratStep step, string stepPath, Action<List<StepAssignment>> edit, bool expandAll = false)
    {
        ArgumentNullException.ThrowIfNull(edit);
        List<StepAssignment> lines = Copy(step, expandAll);
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

    /// <summary>
    ///     Whether every line says the same apart from its slot: one place, one watch, one angle. What the row shows as
    ///     one "who" with one place and one watching. True for one line or none.
    /// </summary>
    /// <param name="lines">The lines.</param>
    public static bool Agree(IReadOnlyList<StepAssignment> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count < 2)
        {
            return true;
        }

        string first = WithoutSlot(lines[0]);
        return lines.Skip(1).All(l => string.Equals(WithoutSlot(l), first, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The step's players set to <paramref name="slots" />, as one write. A slot that keeps its line keeps it; a new
    ///     one copies the shared place and watch while the lines agree, and starts empty once they differ. Five bare lines
    ///     to one place fold back to <c>all</c>. Nothing for an empty set.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="slots">The slots that take part.</param>
    public static List<PatchOp> SetWho(StratStep step, string stepPath, IReadOnlyCollection<string> slots)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
        {
            return [];
        }

        List<StepAssignment> lines = Copy(step, true);
        StepAssignment? shared = lines.Count > 0 && Agree(lines) ? lines[0] : null;
        List<StepAssignment> target = [.. lines.Where(l => slots.Contains(l.Slot))];
        foreach (string slot in StratVocabulary.Slots)
        {
            if (slots.Contains(slot) && !target.Exists(l => string.Equals(l.Slot, slot, StringComparison.Ordinal)))
            {
                StepAssignment line = shared is null ? new StepAssignment() : Clone(shared);
                line.Slot = slot;
                target.Add(line);
            }
        }

        return Write(step, stepPath, target, true);
    }

    /// <summary>
    ///     The line at <paramref name="line" /> moved whole to <paramref name="slot" />: place, watch, angle and
    ///     unknown fields go with it. When another line holds that slot the two swap, so re-lettering players never
    ///     drops one. The step's positions for the two slots swap with it, marks included; their carried entries are
    ///     dropped. Nothing when the lines come out unchanged.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="line">The line's index in <see cref="Copy" />.</param>
    /// <param name="slot">The new slot.</param>
    /// <param name="expandAll">Read a step for <c>all</c> as a line per slot (<see cref="Copy" />).</param>
    public static List<PatchOp> ChangeSlot(StratStep step, string stepPath, int line, string slot, bool expandAll = false)
    {
        ArgumentNullException.ThrowIfNull(step);
        List<StepAssignment> lines = Copy(step, expandAll);
        if (line < 0 || line >= lines.Count || !StratVocabulary.Slots.Contains(slot))
        {
            return [];
        }

        string old = lines[line].Slot;
        if (string.Equals(old, slot, StringComparison.Ordinal))
        {
            return [];
        }

        foreach (StepAssignment other in lines.Where((l, j) => j != line && string.Equals(l.Slot, slot, StringComparison.Ordinal)))
        {
            other.Slot = old;
        }

        lines[line].Slot = slot;
        List<PatchOp> ops = WriteLines(step, stepPath, lines, false);
        if (ops.Count == 0)
        {
            return ops;
        }

        // Slot replaces first, on the original indices; the removes after them, highest index first.
        List<int> carried = [];
        for (int k = 0; k < step.Positions.Count; k++)
        {
            StepPosition position = step.Positions[k];
            string? other = string.Equals(position.Slot, old, StringComparison.Ordinal) ? slot
                : string.Equals(position.Slot, slot, StringComparison.Ordinal) ? old
                : null;
            if (other is null)
            {
                continue;
            }

            if (position.Carried == true)
            {
                carried.Add(k);
            }
            else
            {
                ops.Add(PatchOp.ReplaceOp(Invariant($"{stepPath}/positions/{k}/slot"), null, JsonValue.Create(other)));
            }
        }

        carried.Reverse();
        ops.AddRange(carried.Select(k => PatchOp.RemoveOp(Invariant($"{stepPath}/positions/{k}"), null)));
        return ops;
    }

    /// <summary>
    ///     One edit applied to every line, for a row that shows its lines as one. A step for <c>all</c> is read as a
    ///     line per slot, so a watch keeps all five and a place alone folds back.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="edit">Changes one line.</param>
    public static List<PatchOp> EditAll(StratStep step, string stepPath, Action<StepAssignment> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Edit(step, stepPath, lines => lines.ForEach(edit), true);
    }

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

        ViaOps(ops, path, before.Via, before.ViaPoints, after.Via, after.ViaPoints);

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

    private static string WithoutSlot(StepAssignment line)
    {
        StepAssignment copy = Clone(line);
        copy.Slot = "";
        if (copy.Watch is { } watch && IsEmpty(watch))
        {
            copy.Watch = null;
        }

        return Json(copy);
    }

    private static string WithoutYaw(StepWatch watch) =>
        JsonSerializer.Serialize(new StepWatch { Places = watch.Places, Points = watch.Points, Extra = watch.Extra }, StratJsonContext.Default.StepWatch);

    /// <summary>The ops that make a step's own <c>via</c> and <c>viaPoints</c> hold these; nothing when they already do.</summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="via">The places, or null for none.</param>
    /// <param name="points">The points, or null for none.</param>
    public static List<PatchOp> StepVia(StratStep step, string stepPath, List<string>? via, List<PlaceRef>? points)
    {
        ArgumentNullException.ThrowIfNull(step);
        List<PatchOp> ops = [];
        StepViaOps(ops, stepPath, step, via, points);
        return ops;
    }

    // The step's own via made to say what the lines leave on it; nothing when it already does.
    private static void StepViaOps(List<PatchOp> ops, string stepPath, StratStep step, List<string>? via, List<PlaceRef>? points) =>
        ViaOps(ops, stepPath, step.Via, step.ViaPoints, via is { Count: > 0 } ? via : null, points is { Count: > 0 } ? points : null);

    private static void ViaOps(List<PatchOp> ops, string path, List<string>? beforeVia, List<PlaceRef>? beforePoints, List<string>? afterVia,
        List<PlaceRef>? afterPoints)
    {
        Member(ops, path + "/via", beforeVia is null ? null : JsonSerializer.SerializeToNode(beforeVia, StratJsonContext.Default.ListString),
            afterVia is null ? null : JsonSerializer.SerializeToNode(afterVia, StratJsonContext.Default.ListString));
        Member(ops, path + "/viaPoints", beforePoints is null ? null : JsonSerializer.SerializeToNode(beforePoints, StratJsonContext.Default.ListPlaceRef),
            afterPoints is null ? null : JsonSerializer.SerializeToNode(afterPoints, StratJsonContext.Default.ListPlaceRef));
    }

    private static void Member(List<PatchOp> ops, string path, JsonNode? before, JsonNode? after)
    {
        if (JsonNode.DeepEquals(before, after))
        {
            return;
        }

        ops.Add(after is null ? PatchOp.RemoveOp(path, null) : before is null ? PatchOp.AddOp(path, after) : PatchOp.ReplaceOp(path, null, after));
    }

    private static bool SameVia(StepAssignment a, StepAssignment b) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a.Via, StratJsonContext.Default.ListString),
            JsonSerializer.SerializeToNode(b.Via, StratJsonContext.Default.ListString))
        && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a.ViaPoints, StratJsonContext.Default.ListPlaceRef),
            JsonSerializer.SerializeToNode(b.ViaPoints, StratJsonContext.Default.ListPlaceRef));

    // The stored form: no empty watch, no empty points, no empty via.
    private static void Normalize(StepAssignment line)
    {
        if (line.Via is { Count: 0 })
        {
            line.Via = null;
        }

        if (line.ViaPoints is { Count: 0 })
        {
            line.ViaPoints = null;
        }

        if (line.Watch is { } watch && IsEmpty(watch))
        {
            line.Watch = null;
        }
        else if (line.Watch is { Points.Count: 0 } noPoints)
        {
            noPoints.Points = null;
        }
    }

    // Null unless the kept lines, normalised, are equal and in order, with the added ones after them.
    private static List<PatchOp>? BySlot(List<StepAssignment> before, List<StepAssignment> target, string array)
    {
        HashSet<string> after = [.. target.Select(l => l.Slot)];
        HashSet<string> had = [.. before.Select(l => l.Slot)];
        List<StepAssignment> kept = [.. before.Where(l => after.Contains(l.Slot)).Select(Clone)];
        kept.ForEach(Normalize);
        if (kept.Count > target.Count || !kept.Select(Json).SequenceEqual(target.Take(kept.Count).Select(Json))
                                      || target.Skip(kept.Count).Any(l => had.Contains(l.Slot)))
        {
            return null;
        }

        List<PatchOp> ops = [];
        for (int j = before.Count - 1; j >= 0; j--)
        {
            if (!after.Contains(before[j].Slot))
            {
                ops.Add(PatchOp.RemoveOp(Invariant($"{array}/{j}"), null));
            }
        }

        for (int k = kept.Count; k < target.Count; k++)
        {
            ops.Add(PatchOp.AddOp(Invariant($"{array}/{k}"), Node(target[k])));
        }

        return ops;
    }

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
