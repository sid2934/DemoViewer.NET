#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Services.Strats;

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
    Watch
}

/// <summary>
///     One location field: what a map pick or a location control writes. <see cref="Slot" /> names a line for
///     <see cref="StratLocationKind.To" /> (null is the step's own) and is required for
///     <see cref="StratLocationKind.Watch" />. <see cref="Entry" /> is the watched entry a pick replaces, in reading
///     order; null adds one.
/// </summary>
/// <param name="StepId">The step.</param>
/// <param name="Slot">The line's slot, or null for the step's own member.</param>
/// <param name="Kind">Which member.</param>
/// <param name="Entry">For a watch, the entry to replace; null appends.</param>
public sealed record StratLocationField(Guid StepId, string? Slot, StratLocationKind Kind, int? Entry = null)
{
    /// <summary>Whether the field holds a list (watching) rather than one location.</summary>
    public bool IsMulti => Kind == StratLocationKind.Watch;
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
            StratLocationKind.To => StratStepLines.LineFor(step, field.Slot)?.To,
            StratLocationKind.Landing => step.Utility?.Landing is { } landing ? StratLocations.FromLanding(landing) : null,
            _ => null
        };

        if (field.Kind == StratLocationKind.Watch)
        {
            return field.Slot is null ? [] : StratLocations.Watched(StratStepLines.LineFor(step, field.Slot)?.Watch);
        }

        return single is null ? [] : [single];
    }

    /// <summary>
    ///     Whether the field can be written on the step: its member exists for the step's verb or holds a value, a
    ///     landing needs the step's utility, and a watch or a line's place needs a slot.
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
            StratLocationKind.To => StratVocabulary.Slots.Contains(field.Slot)
                                    && (StratStepFields.Uses(step.Verb, StratStepField.To) || StratStepLines.LineFor(step, field.Slot)?.To is not null),
            StratLocationKind.Landing => step.Utility is not null,
            StratLocationKind.Watch => field.Slot is not null && StratVocabulary.Slots.Contains(field.Slot),
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
                return Member(stepPath + "/to", step.To, next);
            case StratLocationKind.To:
                return StratLinePatches.EditLine(step, stepPath, field.Slot, line => line.To = next);
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
            case StratLocationKind.Watch when field.Slot is not null:
                List<string> places = [.. entries.Where(StratLocations.HasPlace).Select(e => e.Place!).Distinct(StringComparer.Ordinal)];
                List<PlaceRef> points = [.. entries.Where(e => !StratLocations.HasPlace(e) && StratLocations.HasPoint(e)).Select(StratLocations.Clone)];
                return StratLinePatches.EditLine(step, stepPath, field.Slot, line =>
                {
                    line.Watch ??= new StepWatch();
                    line.Watch.Places = places;
                    line.Watch.Points = points.Count == 0 ? null : points;
                });
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

    private static bool SameEntry(PlaceRef a, PlaceRef b) =>
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
