#region

using System.Globalization;
using System.Text.RegularExpressions;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>What a token drag writes (docs/strat-book/drag-semantics.md, option A).</summary>
public enum StratDragAction
{
    /// <summary>A location field of the step: its <c>to</c>, <c>at</c>, <c>site</c>, a lurk area or <c>rotate to</c>.</summary>
    Field,

    /// <summary>A via on the run the token is on, at the leg it is on.</summary>
    Via,

    /// <summary>Alt: the slot's position entry, an exact spot shown as a <c>placed</c> chip.</summary>
    Pin,

    /// <summary>An opponent's position entry, its yaw on a cone drag.</summary>
    Opponent,

    /// <summary>A cone drag: the line's view angle.</summary>
    Turn,

    /// <summary>Nothing: <see cref="StratDragTarget.Refusal" /> says why.</summary>
    Refused
}

/// <summary>
///     Where a token drag lands in the strat: the step, the field and how it is written. <see cref="Field" /> names
///     the slot's own line for A to E, which <see cref="StratLinePatches.EditLine" /> folds back to the step's own
///     member on a one-player step.
/// </summary>
/// <param name="Slot">The token.</param>
/// <param name="Action">What the drop writes.</param>
/// <param name="PathIndex">The step's position on the path; -1 when refused.</param>
/// <param name="StepIndex">Its index in <c>steps[]</c>; -1 when refused.</param>
/// <param name="Field">The location field written, for <see cref="StratDragAction.Field" /> and <see cref="StratDragAction.Via" />.</param>
/// <param name="ViaIndex">Where a via place is inserted among the via places.</param>
/// <param name="DropsOwnPosition">A position verb: the slot's own entry on the step goes, or it would beat the field.</param>
/// <param name="Joins">The step does not name the slot: the write adds its line.</param>
/// <param name="Refusal">Why nothing is written.</param>
/// <param name="StepId">The step's id: the write is refused when <paramref name="StepIndex" /> no longer holds it.</param>
public sealed record StratDragTarget(
    string Slot,
    StratDragAction Action,
    int PathIndex,
    int StepIndex,
    StratLocationField? Field = null,
    int ViaIndex = 0,
    bool DropsOwnPosition = false,
    bool Joins = false,
    string? Refusal = null,
    Guid StepId = default)
{
    public const string NoStepNote = "add a step first: a token is placed at a step";
    public const string ReadOnlyNote = "this step belongs to another strat: open that strat to edit it";
    public const string LineupNote = "placed by its lineup: pick another lineup or clear it";
    public const string RunnerTurnNote = "a runner faces its run; turn it on the hold or push after";
    public static string NothingPlacesNote(string slot) => $"no step places {slot} here to edit: hold Alt to pin it";

    /// <summary>Whether a drop writes anything.</summary>
    public bool IsRefused => Action == StratDragAction.Refused;

    private static StratDragTarget Refuse(string slot, string why) => new(slot, StratDragAction.Refused, -1, -1, Refusal: why);

    /// <summary>
    ///     The step and field a drag edits. Paused on the active step's tick: the step that wins the tick for the slot
    ///     (the later one naming it), else the active step, by the verb table. Paused between steps: the run the token is
    ///     on gets a via; a standing token edits the step that put it there. Pin writes the active step's position entry.
    /// </summary>
    /// <param name="projection">The canvas's projection.</param>
    /// <param name="tick">The playhead, paused.</param>
    /// <param name="activeIndex">The selected step's path index.</param>
    /// <param name="slot">The token.</param>
    /// <param name="grip">Body moves it, heading turns it.</param>
    /// <param name="pin">Alt, or the toolbar's Pin.</param>
    public static StratDragTarget Resolve(StratSceneProjection projection, int tick, int activeIndex, string slot, TokenGrip grip, bool pin)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Path.Count == 0 || activeIndex < 0 || activeIndex >= projection.Path.Count)
        {
            return Refuse(slot, NoStepNote);
        }

        bool own = StratVocabulary.Slots.Contains(slot);
        bool onTick = projection.Ticks[activeIndex] == tick;
        int pinAt = own && onTick ? Winner(projection, tick, slot) ?? activeIndex : activeIndex;
        if (!own || (pin && grip == TokenGrip.Body))
        {
            return !projection.Path[pinAt].Editable
                ? Refuse(slot, ReadOnlyNote)
                : projection.ThrowOriginAt(pinAt, slot) is not null
                    ? Refuse(slot, LineupNote)
                    : new StratDragTarget(slot, own ? StratDragAction.Pin : StratDragAction.Opponent, pinAt, projection.Path[pinAt].StepIndex,
                        StepId: projection.Path[pinAt].Step.Id);
        }

        if (onTick)
        {
            return ByVerb(projection, pinAt, tick, slot, grip);
        }

        if (projection.RunAt(slot, tick) is { } run)
        {
            if (grip == TokenGrip.Heading)
            {
                return Refuse(slot, RunnerTurnNote);
            }

            StratStep step = projection.Path[run.PathIndex].Step;
            if (!projection.Path[run.PathIndex].Editable)
            {
                return Refuse(slot, ReadOnlyNote);
            }

            if (run.Rotate)
            {
                return FieldOn(projection, run.PathIndex, slot, StratLocationKind.RotateTo, null);
            }

            if (StratStepFields.MotionOf(step.Verb) is StepMotion.Travel or StepMotion.Lurk)
            {
                int places = StratStepLines.LineFor(step, slot)?.Via?.Count ?? (StratStepLines.HasLines(step) ? 0 : step.Via?.Count ?? 0);
                return new StratDragTarget(slot, StratDragAction.Via, run.PathIndex, projection.Path[run.PathIndex].StepIndex,
                    new StratLocationField(step.Id, slot, StratLocationKind.Via), Math.Min(run.LegAt(tick), places), StepId: step.Id);
            }

            return ByVerb(projection, run.PathIndex, tick, slot, grip);
        }

        if (projection.PlacedBy(slot, tick) is not { } placed)
        {
            return Refuse(slot, NothingPlacesNote(slot));
        }

        return placed.Rotate
            ? grip == TokenGrip.Heading ? Refuse(slot, RunnerTurnNote) : FieldOn(projection, placed.PathIndex, slot, StratLocationKind.RotateTo, null)
            : ByVerb(projection, placed.PathIndex, tick, slot, grip);
    }

    // On one tick the later step naming the slot wins it, as the projection plays it.
    private static int? Winner(StratSceneProjection projection, int tick, string slot)
    {
        int? found = null;
        for (int i = 0; i < projection.Path.Count; i++)
        {
            if (projection.Ticks[i] == tick && StratStepLines.Involves(projection.Path[i].Step, slot))
            {
                found = i;
            }
        }

        return found;
    }

    private static StratDragTarget ByVerb(StratSceneProjection projection, int pathIndex, int tick, string slot, TokenGrip grip)
    {
        StratPathStep at = projection.Path[pathIndex];
        StratStep step = at.Step;
        if (projection.ThrowOriginAt(pathIndex, slot) is not null)
        {
            return Refuse(slot, LineupNote);
        }

        if (!at.Editable)
        {
            return Refuse(slot, ReadOnlyNote);
        }

        StepMotion motion = StratStepFields.MotionOf(step.Verb);
        bool names = StratStepLines.Involves(step, slot);
        if (grip == TokenGrip.Heading)
        {
            return StratStepFields.Uses(step.Verb, StratStepField.Watch) || StratStepLines.LineFor(step, slot)?.Watch is not null
                ? new StratDragTarget(slot, StratDragAction.Turn, pathIndex, at.StepIndex, Joins: !names, StepId: step.Id)
                : Refuse(slot, RunnerTurnNote);
        }

        // Throw, wait and call move no one, and a lurk is shared by its lurkers: the drag edits the step that placed the token.
        if (motion == StepMotion.None || (!names && !StratStepFields.Uses(step.Verb, StratStepField.To)))
        {
            // Strictly earlier steps only, so this ends.
            if (projection.PlacedBy(slot, projection.Ticks[pathIndex], i => i < pathIndex) is not { } placed)
            {
                return Refuse(slot, NothingPlacesNote(slot));
            }

            return placed.Rotate
                ? FieldOn(projection, placed.PathIndex, slot, StratLocationKind.RotateTo, null)
                : ByVerb(projection, placed.PathIndex, tick, slot, grip);
        }

        if (motion == StepMotion.Lurk)
        {
            bool rotating = StratSceneProjection.RotateTickOf(step, projection.Ticks[pathIndex], projection.RoundSeconds) is { } rotate && rotate <= tick;
            return FieldOn(projection, pathIndex, slot, rotating ? StratLocationKind.RotateTo : StratLocationKind.LurkArea, null);
        }

        return FieldOn(projection, pathIndex, slot, StratLocationKind.To, slot) with
        {
            DropsOwnPosition = motion == StepMotion.Position,
            Joins = !names
        };
    }

    private static StratDragTarget FieldOn(StratSceneProjection projection, int pathIndex, string slot, StratLocationKind kind, string? fieldSlot) =>
        new(slot, StratDragAction.Field, pathIndex, projection.Path[pathIndex].StepIndex,
            new StratLocationField(projection.Path[pathIndex].Step.Id, fieldSlot, kind), StepId: projection.Path[pathIndex].Step.Id);
}

/// <summary>Where a drag let go, and how the place under it is read.</summary>
/// <param name="Place">The place under the drop, or null.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="LevelMinZ">The floor's level key.</param>
/// <param name="PlacesKnown">False when the map's places are not in memory.</param>
/// <param name="Snapped">Near the place's arrival centre: the place alone is stored.</param>
/// <param name="PointOnly">Shift: the point alone, no place.</param>
/// <param name="YawDegrees">A cone drag's facing.</param>
public sealed record StratDrop(string? Place, double X, double Y, double LevelMinZ, bool PlacesKnown, bool Snapped = false, bool PointOnly = false,
    double? YawDegrees = null);

/// <summary>
///     A drag's ops, one undo entry, through the field writers (<see cref="StratLocationPatches" />,
///     <see cref="StratLinePatches" />, <see cref="StratLurkPatches" />). Pure.
/// </summary>
public static partial class StratDragPatches
{
    /// <summary>What the drop stores in a field: the place alone near its centre, the point alone with Shift, else what a map click stores.</summary>
    /// <param name="stored">The single field's location as it stands, or null.</param>
    /// <param name="drop">The drop.</param>
    /// <param name="multi">A list field: a place, or the point outside every place.</param>
    public static PlaceRef Entry(PlaceRef? stored, StratDrop drop, bool multi)
    {
        ArgumentNullException.ThrowIfNull(drop);
        if (drop.PointOnly)
        {
            PlaceRef point = StratLocations.Picked(stored, null, drop.X, drop.Y, drop.LevelMinZ, true);
            point.Place = null;
            return point;
        }

        if (drop.PlacesKnown && !string.IsNullOrEmpty(drop.Place) && (drop.Snapped || multi))
        {
            return new PlaceRef { Place = drop.Place, Extra = multi ? null : stored?.Extra };
        }

        return multi
            ? StratLocations.Picked(null, null, drop.X, drop.Y, drop.LevelMinZ, true)
            : StratLocations.Picked(stored, drop.Place, drop.X, drop.Y, drop.LevelMinZ, drop.PlacesKnown);
    }

    /// <summary>The ops for a drop; empty when refused or when the field already says so.</summary>
    /// <param name="document">The strat.</param>
    /// <param name="target">Where the drag lands.</param>
    /// <param name="drop">Where it let go.</param>
    public static List<PatchOp> Ops(StratDocument document, StratDragTarget target, StratDrop drop)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(drop);
        if (!Holds(document, target))
        {
            return [];
        }

        int index = target.StepIndex;
        StratStep step = document.Steps[index];
        string stepPath = string.Create(CultureInfo.InvariantCulture, $"/steps/{index}");
        switch (target.Action)
        {
            case StratDragAction.Pin:
            case StratDragAction.Opponent:
                return [StepAuthoringPatches.TokenPosition(document, index, target.Slot, drop.X, drop.Y, drop.LevelMinZ, drop.YawDegrees)];
            case StratDragAction.Turn:
                return drop.YawDegrees is { } yaw ? StratLinePatches.WatchYaw(step, stepPath, target.Slot, yaw) : [];
            case StratDragAction.Via:
                return ViaOps(document, index, target, drop);
            case StratDragAction.Field when target.Field is { } field:
                List<PatchOp> ops = field.Kind switch
                {
                    StratLocationKind.LurkArea => LurkFirst(document, index, field, drop),
                    _ => StratLocationPatches.Write(document, index, field,
                        [Entry(StratLocationPatches.Read(step, field) is { Count: > 0 } current ? current[0] : null, drop, false)])
                };
                if (target.DropsOwnPosition)
                {
                    return WithPositionsRemoved(step, index, ops, target.Slot);
                }

                if (ReplacesSeen(document, target))
                {
                    bool legacy = StratSceneProjection.IsLegacyObserved(document);
                    return WithPositionsRemoved(step, index, ops, target.Slot,
                        seen: k => step.Positions[k].Carried != true && (legacy || step.Positions[k].Observed == true));
                }

                return ops;
            default:
                return [];
        }
    }

    /// <summary>Whether the target's step is still at its index: a step edit during a drag moves the indices under it.</summary>
    /// <param name="document">The strat as it is now.</param>
    /// <param name="target">Where the drag lands.</param>
    public static bool Holds(StratDocument document, StratDragTarget target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        return !target.IsRefused && target.StepIndex >= 0 && target.StepIndex < document.Steps.Count
               && (target.StepId == Guid.Empty || document.Steps[target.StepIndex].Id == target.StepId);
    }

    /// <summary>
    ///     Whether the drop replaces the slot's observed entry on the step: a travel's <c>to</c> or a lurk's area, which the
    ///     entry would otherwise beat, as a capture's spot is the token's spot on every verb.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="target">Where the drag lands.</param>
    public static bool ReplacesSeen(StratDocument document, StratDragTarget target)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        if (!Holds(document, target) || target.Action != StratDragAction.Field || target.DropsOwnPosition
            || target.Field?.Kind is not (StratLocationKind.To or StratLocationKind.LurkArea))
        {
            return false;
        }

        bool legacy = StratSceneProjection.IsLegacyObserved(document);
        return document.Steps[target.StepIndex].Positions.Any(p => p.Slot == target.Slot && p.Carried != true && (legacy || p.Observed == true));
    }

    /// <summary>
    ///     The ops that make a position entry the field the verb table names for its step (the place under it plus the
    ///     point) and remove the entry: a <c>placed</c> chip's "make it the …". Empty when the step names no field for it.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step.</param>
    /// <param name="positionIndex">The entry.</param>
    /// <param name="placeAt">The place under a point, or null when the map's places are not loaded.</param>
    public static List<PatchOp> Convert(StratDocument document, int stepIndex, int positionIndex, Func<double, double, double, string?>? placeAt)
    {
        ArgumentNullException.ThrowIfNull(document);
        StratStep step = document.Steps[stepIndex];
        StepPosition position = step.Positions[positionIndex];
        if (FieldFor(step, position.Slot) is not { } field)
        {
            return [];
        }

        StratDrop drop = new(placeAt?.Invoke(position.X, position.Y, position.LevelMinZ), position.X, position.Y, position.LevelMinZ, placeAt is not null);
        StratDragTarget target = new(position.Slot, StratDragAction.Field, -1, stepIndex, field, StepId: step.Id);
        List<PatchOp> ops = field.Kind == StratLocationKind.LurkArea
            ? LurkFirst(document, stepIndex, field, drop)
            : StratLocationPatches.Write(document, stepIndex, field,
                [Entry(StratLocationPatches.Read(step, field) is { Count: > 0 } current ? current[0] : null, drop, false)]);
        return WithPositionsRemoved(step, stepIndex, ops, target.Slot, positionIndex);
    }

    /// <summary>The field a chip's point would go into on its step: <c>to</c>, <c>at</c> or <c>site</c>, or lurk area 1; null for none.</summary>
    /// <param name="step">The step.</param>
    /// <param name="slot">The entry's slot.</param>
    public static StratLocationField? FieldFor(StratStep step, string slot)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!StratVocabulary.Slots.Contains(slot) || !StratStepLines.Involves(step, slot))
        {
            return null;
        }

        return StratStepFields.MotionOf(step.Verb) switch
        {
            StepMotion.Travel or StepMotion.Position => new StratLocationField(step.Id, slot, StratLocationKind.To),
            StepMotion.Lurk => new StratLocationField(step.Id, null, StratLocationKind.LurkArea),
            _ => null
        };
    }

    /// <summary>The <c>remove</c> of one position entry, a chip's clear.</summary>
    /// <param name="stepIndex">The step.</param>
    /// <param name="positionIndex">The entry.</param>
    public static PatchOp Clear(int stepIndex, int positionIndex) =>
        PatchOp.RemoveOp(string.Create(CultureInfo.InvariantCulture, $"/steps/{stepIndex}/positions/{positionIndex}"), null);

    // The drop first: a place already listed moves to first. Points read after places in the file, so a point goes last.
    private static List<PatchOp> LurkFirst(StratDocument document, int index, StratLocationField field, StratDrop drop)
    {
        PlaceRef entry = Entry(null, drop, true);
        List<PlaceRef> entries = [entry, .. StratLocationPatches.Read(document.Steps[index], field).Where(e => !StratLocationPatches.SameEntry(e, entry))];
        return StratLocationPatches.Write(document, index, field, entries);
    }

    // A place goes in before the via the token was heading for; a point goes last, where the file reads points.
    private static List<PatchOp> ViaOps(StratDocument document, int index, StratDragTarget target, StratDrop drop)
    {
        StratLocationField field = target.Field!;
        PlaceRef entry = Entry(null, drop, true);
        // The slot's own reading: a step for everyone has no line yet, and its via is every player's.
        List<PlaceRef> current = [.. StratStepLines.ViaFor(document.Steps[index], target.Slot)];
        List<PlaceRef> places = [.. current.Where(StratLocations.HasPlace).Where(e => !StratLocationPatches.SameEntry(e, entry))];
        List<PlaceRef> points = [.. current.Where(e => !StratLocations.HasPlace(e))];
        if (StratLocations.HasPlace(entry))
        {
            places.Insert(Math.Clamp(target.ViaIndex, 0, places.Count), entry);
        }
        else if (!points.Any(p => StratLocationPatches.SameEntry(p, entry)))
        {
            points.Add(entry);
        }

        return StratLocationPatches.Write(document, index, field, [.. places, .. points]);
    }

    // The slot's own entries on the step go with the edit, as one set of removes after every other op, highest first, so
    // the field writers' carried removes and these never name one index twice.
    private static List<PatchOp> WithPositionsRemoved(StratStep step, int index, List<PatchOp> ops, string slot, int? only = null,
        Func<int, bool>? seen = null)
    {
        Regex pointer = PositionPointer();
        SortedSet<int> removes = [];
        List<PatchOp> rest = [];
        foreach (PatchOp op in ops)
        {
            Match m = pointer.Match(op.Path);
            if (op.Op == PatchOp.Remove && m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == index)
            {
                removes.Add(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            }
            else
            {
                rest.Add(op);
            }
        }

        for (int k = 0; k < step.Positions.Count; k++)
        {
            if (only is { } single ? k == single : string.Equals(step.Positions[k].Slot, slot, StringComparison.Ordinal) && (seen is null || seen(k)))
            {
                removes.Add(k);
            }
        }

        if (rest.Count == 0 && removes.Count == 0)
        {
            return [];
        }

        rest.AddRange(removes.Reverse().Select(k => Clear(index, k)));
        return rest;
    }

    [GeneratedRegex(@"^/steps/(\d+)/positions/(\d+)$")]
    private static partial Regex PositionPointer();
}
