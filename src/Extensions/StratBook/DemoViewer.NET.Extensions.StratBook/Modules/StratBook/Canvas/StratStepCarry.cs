#region

using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;

/// <summary>
///     The positions a new step starts with: every token where the projection has it at the step it follows,
///     written out and marked <c>carried</c>. A copy, not a reference: the stationary rule would already show the same
///     places, but a later drag on the earlier step would then move the new step's tokens too. The mark lets a
///     destination set on the new step win over the copy.
/// </summary>
public static class StratStepCarry
{
    /// <summary>
    ///     Per token (A to E, O1 to O5), where <see cref="StratSceneProjection" /> places it at
    ///     <paramref name="stepIndex" />: a throw's resolved lineup origin for its actor, else the last authored entry
    ///     at or before that step. An unresolved lineup (not grouped yet, or a stale id) places nothing, as in the
    ///     projection. A token a destination has sent somewhere since its last authored entry is not carried: where it
    ///     ends up is the destination's, and a copy of where it stood would pull it back.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step the new one follows; -1 carries nothing.</param>
    /// <param name="throwOrigins">The projection's resolver; null resolves no lineup.</param>
    /// <param name="placeCentres">
    ///     The projection's place centres, so a token turned by a watching line carries that facing; null carries
    ///     only an explicit view angle.
    /// </param>
    /// <param name="atSeconds">
    ///     The new step's time. A lurker whose rotate is at or before it, to a place that resolves, is not carried: the
    ///     rotate has moved it. Null, or a time before the rotate, carries the lurker where it stood.
    /// </param>
    public static List<StepPosition> PositionsAt(StratDocument document, int stepIndex, ThrowOriginResolver? throwOrigins,
        PlaceCentreResolver? placeCentres = null, double? atSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<StepPosition> carried = [];
        int last = Math.Min(stepIndex, document.Steps.Count - 1);
        if (last < 0)
        {
            return carried;
        }

        // The projection's own entries, so a lineup origin and a watch's facing carry as the canvas shows them.
        IReadOnlyList<StratPathStep> path = [.. StratPath.MainLine(document).Take(last + 1)];
        StratSceneProjection.ThrowOrigin?[] origins = throwOrigins is null
            ? new StratSceneProjection.ThrowOrigin?[path.Count]
            : [.. path.Select(p => StratSceneProjection.ThrowOriginOf(document.Map, p.Step, throwOrigins))];
        StratSceneProjection.PlaceSet places = StratSceneProjection.PlacesOf(document, placeCentres, null, null, null);
        int[] ticks = StratSceneProjection.TicksOf(path, places.ClockOrRound, out _);
        foreach (string slot in StratVocabulary.Slots.Concat(StratVocabulary.OpponentSlots))
        {
            StratSceneProjection.SlotPlan plan = StratSceneProjection.PlanOf(path, ticks, origins, slot, places);
            if (plan.Moved || (atSeconds is { } at && placeCentres is not null && HasRotated(document, last, slot, at, placeCentres)))
            {
                continue;
            }

            TokenPlacement?[] placements = plan.Placements;

            for (int k = last; k >= 0; k--)
            {
                if (placements[k] is not { } placement)
                {
                    continue;
                }

                // An authored entry the projection did not turn is carried as stored, unknown fields included.
                StepPosition? stored = document.Steps[k].Positions.LastOrDefault(p => string.Equals(p.Slot, slot, StringComparison.Ordinal)
                                                                                     && double.IsFinite(p.X) && double.IsFinite(p.Y));
                StepPosition copy = stored is not null && Same(stored, placement)
                    ? Clone(stored)
                    : new StepPosition
                    {
                        Slot = slot, X = Round(placement.X), Y = Round(placement.Y), LevelMinZ = placement.LevelMinZ,
                        YawDegrees = placement.YawDegrees is { } yaw ? Round(yaw) : null
                    };
                copy.Carried = true;
                copy.Observed = null;
                carried.Add(copy);
                break;
            }
        }

        return carried;
    }

    // Whether a rotate of a step up to `last` that names the slot fires by `atSeconds` and has a place to go to.
    private static bool HasRotated(StratDocument document, int last, string slot, double atSeconds, PlaceCentreResolver centres)
    {
        if (!StratVocabulary.Slots.Contains(slot))
        {
            return false;
        }

        StratClockInfo clock = StratSceneProjection.ClockOf(document);
        int at = StratClock.StratTickOf(clock, atSeconds);
        for (int k = 0; k <= last; k++)
        {
            StratStep step = document.Steps[k];
            if (StratStepLines.Involves(step, slot)
                && StratSceneProjection.RotateTickOf(step, StratClock.StratTickOf(clock, step.AtSeconds), clock) is { } tick
                && tick <= at && (StratLocations.HasPoint(step.Lurk!.Rotate!.To) || centres(step.Lurk.Rotate.To!.Place!, 0) is not null))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(StepPosition stored, TokenPlacement placement) =>
        (float)stored.X == placement.X && (float)stored.Y == placement.Y
                                       && (stored.YawDegrees is { } yaw ? (float?)yaw : null) == placement.YawDegrees;

    private static StepPosition Clone(StepPosition position) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(position, StratJsonContext.Default.StepPosition),
            StratJsonContext.Default.StepPosition)!;

    // Two decimals, as a drag writes them.
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
