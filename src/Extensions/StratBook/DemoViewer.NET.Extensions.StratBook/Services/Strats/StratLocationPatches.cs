#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats;

/// <summary>The location members a step has.</summary>
public enum StratLocationKind
{
    /// <summary>The step's <c>from</c>, shared by its lines.</summary>
    From,

    /// <summary>The step's <c>to</c>, or a line's when the field names a slot.</summary>
    To,

    /// <summary>The step's <c>utility.landing</c>.</summary>
    Landing,

    /// <summary>A line's watched entries: its places, then its points.</summary>
    Watch,

    /// <summary>The step's lurk areas: its places, then its points.</summary>
    LurkArea,

    /// <summary>Where the step's lurk rotates to.</summary>
    RotateTo,

    /// <summary>What a travel goes through, the step's or a line's when the field names a slot: places, then points.</summary>
    Via,

    /// <summary>A token's start (<see cref="StratStartBlock" />): the slot names the token and the step id is empty.</summary>
    Start
}

/// <summary>
///     One location field: what a map pick or a location control writes. <see cref="Slot" /> names a line for
///     <see cref="StratLocationKind.To" /> (null is the step's own) and is required for
///     <see cref="StratLocationKind.Watch" />; <see cref="AllLines" /> writes every line, for a row that shows its
///     lines as one. <see cref="Entry" /> is the list entry a pick replaces, in reading order; null adds one.
/// </summary>
/// <param name="StepId">The step.</param>
/// <param name="Slot">The line's slot, or null for the step's own member.</param>
/// <param name="Kind">Which member.</param>
/// <param name="Entry">For a watch, the entry to replace; null appends.</param>
public sealed record StratLocationField(Guid StepId, string? Slot, StratLocationKind Kind, int? Entry = null)
{
    /// <summary>The <see cref="Slot" /> that means every line of the step.</summary>
    public const string AllLines = StratVocabulary.ActorAll;

    /// <summary>Whether the field holds a list (watching, lurk areas) rather than one location.</summary>
    public bool IsMulti => Kind is StratLocationKind.Watch or StratLocationKind.LurkArea or StratLocationKind.Via;

    /// <summary>Whether it writes every line rather than one.</summary>
    public bool IsAllLines => string.Equals(Slot, AllLines, StringComparison.Ordinal);
}

/// <summary>
///     Reads and writes one location field as a list of <see cref="PlaceRef" /> (at most one for a single field), and
///     turns a map pick into the ops for it. Lines go through <see cref="StratLinePatches" />, so the stored shape
///     stays canonical; a pick or a commit is one ops list, which the caller applies as one undo entry.
/// </summary>
public static class StratLocationPatches
{
    /// <summary>The field's value: a single field's location, or a watch's entries (places, then points).</summary>
    /// <param name="step">The step.</param>
    /// <param name="field">The field.</param>
    public static IReadOnlyList<PlaceRef> Read(StratStep step, StratLocationField field)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(field);
        PlaceRef? single = field.Kind switch
        {
            StratLocationKind.From => step.From,
            StratLocationKind.To when field.Slot is null => step.To,
            StratLocationKind.To => LineOf(step, field)?.To,
            StratLocationKind.Landing => step.Utility?.Landing is { } landing ? StratLocations.FromLanding(landing) : null,
            StratLocationKind.RotateTo => step.Lurk?.Rotate?.To,
            _ => null
        };

        if (field.Kind == StratLocationKind.Watch)
        {
            return field.Slot is null ? [] : StratLocations.Watched(LineOf(step, field)?.Watch);
        }

        if (field.Kind == StratLocationKind.LurkArea)
        {
            return StratLocations.LurkAreas(step.Lurk);
        }

        if (field.Kind == StratLocationKind.Via)
        {
            return field.Slot is null
                ? StratLocations.Via(step.Via, step.ViaPoints)
                : LineOf(step, field) is { } line ? StratLocations.Via(line.Via, line.ViaPoints) : [];
        }

        return single is null ? [] : [single];
    }

    /// <summary>
    ///     Whether the field can be written on the step: its member exists for the step's verb or holds a value, a
    ///     landing needs the step's utility, and a line's place or watch needs a player the step still names.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="field">The field.</param>
    public static bool Applies(StratStep step, StratLocationField field)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(field);
        return field.Kind switch
        {
            StratLocationKind.From => StratStepFields.Uses(step.Verb, StratStepField.From) || step.From is not null,
            StratLocationKind.To when field.Slot is null => !StratStepLines.HasLines(step)
                                                            && (StratStepFields.Uses(step.Verb, StratStepField.To) || step.To is not null),
            StratLocationKind.To => IsInvolved(step, field)
                                    && (StratStepFields.Uses(step.Verb, StratStepField.To) || LineOf(step, field)?.To is not null),
            StratLocationKind.Landing => step.Utility is not null,
            StratLocationKind.Watch => IsInvolved(step, field)
                                       && (StratStepFields.Uses(step.Verb, StratStepField.Watch) || LineOf(step, field)?.Watch is not null),
            StratLocationKind.LurkArea or StratLocationKind.RotateTo => StratStepFields.Uses(step.Verb, StratStepField.Lurk) || step.Lurk is not null,
            StratLocationKind.Via when field.Slot is null => !StratStepLines.HasLines(step)
                                                             && (StratStepFields.Uses(step.Verb, StratStepField.Via) || step.Via is not null
                                                                 || step.ViaPoints is not null),
            StratLocationKind.Via => IsInvolved(step, field)
                                     && (StratStepFields.Uses(step.Verb, StratStepField.Via) || LineOf(step, field) is { Via: not null } or { ViaPoints: not null }),
            _ => false
        };
    }

    /// <summary>
    ///     Ops that make the field hold <paramref name="entries" />; empty when it already does. A single field takes
    ///     the first entry, or clears on none. A watch stores each entry with a place as that place and each other
    ///     entry with a point as a point, keeping its angle and unknown members.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step's index.</param>
    /// <param name="field">The field.</param>
    /// <param name="entries">What the field should hold.</param>
    public static List<PatchOp> Write(StratDocument document, int stepIndex, StratLocationField field, IReadOnlyList<PlaceRef> entries)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(entries);
        StratStep step = document.Steps[stepIndex];
        string stepPath = string.Create(CultureInfo.InvariantCulture, $"/steps/{stepIndex}");
        PlaceRef? next = entries.FirstOrDefault(StratLocations.IsSet) is { } first ? StratLocations.Clone(first) : null;

        switch (field.Kind)
        {
            case StratLocationKind.From:
                return Member(stepPath + "/from", step.From, next);
            case StratLocationKind.To when field.Slot is null:
                List<PatchOp> to = Member(stepPath + "/to", step.To, next);
                if (to.Count > 0 && next is not null && StratStepFields.MovesToTo(step.Verb))
                {
                    to.AddRange(StratLinePatches.DropCarried(step, stepPath, StratVocabulary.Slots.Where(s => StratStepLines.Involves(step, s))));
                }

                return to;
            case StratLocationKind.To when field.IsAllLines:
                return StratLinePatches.EditAll(step, stepPath, line => line.To = next is null ? null : StratLocations.Clone(next));
            case StratLocationKind.To:
                return StratLinePatches.EditLine(step, stepPath, field.Slot, line => line.To = next);
            case StratLocationKind.RotateTo:
                return StratLurkPatches.Edit(step, stepPath, lurk =>
                {
                    if (next is not null || lurk.Rotate is not null)
                    {
                        (lurk.Rotate ??= new LurkRotate()).To = next;
                    }
                });
            case StratLocationKind.LurkArea:
                IReadOnlyList<PlaceRef> areasBefore = StratLocations.LurkAreas(step.Lurk);
                List<PatchOp> areas = StratLurkPatches.Edit(step, stepPath, lurk =>
                {
                    lurk.Areas = [.. entries.Where(StratLocations.HasPlace).Select(e => e.Place!).Distinct(StringComparer.Ordinal)];
                    List<PlaceRef> areaPoints = [.. entries.Where(e => !StratLocations.HasPlace(e) && StratLocations.HasPoint(e)).Select(StratLocations.Clone)];
                    lurk.AreaPoints = areaPoints.Count == 0 ? null : areaPoints;
                });

                // A lurk walks to its first area, so a new first area is a new destination.
                IReadOnlyList<PlaceRef> areasAfter = StratLocations.LurkAreas(new StepLurk
                {
                    Areas = [.. entries.Where(StratLocations.HasPlace).Select(e => e.Place!)],
                    AreaPoints = [.. entries.Where(e => !StratLocations.HasPlace(e) && StratLocations.HasPoint(e))]
                });
                if (areas.Count > 0 && areasAfter.Count > 0 && StratStepFields.MotionOf(step.Verb) == StepMotion.Lurk
                    && (areasBefore.Count == 0 || !SameEntry(areasBefore[0], areasAfter[0])))
                {
                    areas.AddRange(StratLinePatches.DropCarried(step, stepPath, StratVocabulary.Slots.Where(s => StratStepLines.Involves(step, s))));
                }

                return areas;
            case StratLocationKind.Landing:
                if (step.Utility is null)
                {
                    return [];
                }

                PlaceRef? stored = step.Utility.Landing is { } landing ? StratLocations.FromLanding(landing) : null;
                if (StratLocations.Same(stored, next))
                {
                    return [];
                }

                string path = stepPath + "/utility/landing";
                if (next is null)
                {
                    return stored is null ? [] : [PatchOp.RemoveOp(path, null)];
                }

                JsonNode? value = JsonSerializer.SerializeToNode(StratLocations.ToLanding(next), StratJsonContext.Default.UtilityLanding);
                return [stored is null ? PatchOp.AddOp(path, value) : PatchOp.ReplaceOp(path, null, value)];
            case StratLocationKind.Via:
                List<string>? viaPlaces = [.. entries.Where(StratLocations.HasPlace).Select(e => e.Place!).Distinct(StringComparer.Ordinal)];
                List<PlaceRef>? viaPoints = [.. entries.Where(e => !StratLocations.HasPlace(e) && StratLocations.HasPoint(e)).Select(StratLocations.Clone)];
                viaPlaces = viaPlaces.Count == 0 ? null : viaPlaces;
                viaPoints = viaPoints.Count == 0 ? null : viaPoints;
                void SetVia(StepAssignment line)
                {
                    line.Via = viaPlaces is null ? null : [.. viaPlaces];
                    line.ViaPoints = viaPoints?.Select(StratLocations.Clone).ToList();
                }

                return field.Slot is null
                    ? StratLinePatches.StepVia(step, stepPath, viaPlaces, viaPoints)
                    : field.IsAllLines
                        ? StratLinePatches.EditAll(step, stepPath, SetVia)
                        : StratLinePatches.EditLine(step, stepPath, field.Slot, SetVia);
            case StratLocationKind.Watch when field.Slot is not null:
                List<string> places = [.. entries.Where(StratLocations.HasPlace).Select(e => e.Place!).Distinct(StringComparer.Ordinal)];
                List<PlaceRef> points = [.. entries.Where(e => !StratLocations.HasPlace(e) && StratLocations.HasPoint(e)).Select(StratLocations.Clone)];
                void SetWatch(StepAssignment line)
                {
                    line.Watch ??= new StepWatch();
                    line.Watch.Places = [.. places];
                    line.Watch.Points = points.Count == 0 ? null : [.. points.Select(StratLocations.Clone)];
                }

                return field.IsAllLines ? StratLinePatches.EditAll(step, stepPath, SetWatch) : StratLinePatches.EditLine(step, stepPath, field.Slot, SetWatch);
            default:
                return [];
        }
    }

    /// <summary>
    ///     The ops a map click writes on the field: a single field becomes the place under the click plus the point
    ///     (<see cref="StratLocations.Picked" />); a watch adds the place, or the point when the click is in no place,
    ///     or replaces <see cref="StratLocationField.Entry" />. Empty when the field already says so.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step's index.</param>
    /// <param name="field">The field.</param>
    /// <param name="place">The place under the click, or null.</param>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="levelMinZ">The clicked floor's level key.</param>
    /// <param name="placesKnown">False when the map's places could not be read.</param>
    public static List<PatchOp> Pick(StratDocument document, int stepIndex, StratLocationField field, string? place, double x, double y,
        double levelMinZ, bool placesKnown)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(field);
        StratStep step = document.Steps[stepIndex];
        IReadOnlyList<PlaceRef> current = Read(step, field);
        if (!field.IsMulti)
        {
            return Write(document, stepIndex, field, [StratLocations.Picked(current.Count > 0 ? current[0] : null, place, x, y, levelMinZ, placesKnown)]);
        }

        PlaceRef entry = placesKnown && !string.IsNullOrEmpty(place)
            ? new PlaceRef { Place = place }
            : StratLocations.Picked(null, null, x, y, levelMinZ, true);
        List<PlaceRef> entries = [.. current];
        if (field.Entry is { } replace && replace >= 0 && replace < entries.Count)
        {
            entries[replace] = entry;
        }
        else if (!entries.Any(e => SameEntry(e, entry)))
        {
            entries.Add(entry);
        }

        return Write(document, stepIndex, field, entries);
    }

    // A line field names a player the step still has: a pick armed on a removed player must not add them back.
    private static bool IsInvolved(StratStep step, StratLocationField field) =>
        field.IsAllLines || (field.Slot is not null && StratVocabulary.Slots.Contains(field.Slot) && StratStepLines.Involves(step, field.Slot));

    // A line field's line: the named slot's, or for every line the first, since the row shows them only while they agree.
    private static StepAssignment? LineOf(StratStep step, StratLocationField field) =>
        field.IsAllLines
            ? StratLinePatches.Copy(step, true) is { Count: > 0 } lines ? lines[0] : null
            : StratStepLines.LineFor(step, field.Slot!);

    /// <summary>Whether two list entries are one: the same place, or for points the same coordinates and level.</summary>
    internal static bool SameEntry(PlaceRef a, PlaceRef b) =>
        StratLocations.HasPlace(a) || StratLocations.HasPlace(b)
            ? string.Equals(a.Place, b.Place, StringComparison.Ordinal)
            : a.X == b.X && a.Y == b.Y && a.LevelMinZ == b.LevelMinZ;

    private static List<PatchOp> Member(string path, PlaceRef? stored, PlaceRef? next)
    {
        if (StratLocations.Same(stored, next))
        {
            return [];
        }

        if (next is null)
        {
            return [PatchOp.RemoveOp(path, null)];
        }

        JsonNode? value = JsonSerializer.SerializeToNode(next, StratJsonContext.Default.PlaceRef);
        return [stored is null ? PatchOp.AddOp(path, value) : PatchOp.ReplaceOp(path, null, value)];
    }
}
