#region

using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     The positions a new step starts with: every token where the projection has it at the step it follows,
///     written out. A copy, not a reference: the stationary rule would already show the same places, but a later
///     drag on the earlier step would then move the new step's tokens too.
/// </summary>
public static class StratStepCarry
{
    /// <summary>
    ///     Per token (A to E, O1 to O5), where <see cref="StratSceneProjection" /> places it at
    ///     <paramref name="stepIndex" />: a throw's resolved lineup origin for its actor, else the last authored entry
    ///     at or before that step. An unresolved lineup (not grouped yet, or a stale id) places nothing, as in the
    ///     projection.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step the new one follows; -1 carries nothing.</param>
    /// <param name="throwOrigins">The projection's resolver; null resolves no lineup.</param>
    public static List<StepPosition> PositionsAt(StratDocument document, int stepIndex, ThrowOriginResolver? throwOrigins)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<StepPosition> carried = [];
        int last = Math.Min(stepIndex, document.Steps.Count - 1);
        foreach (string slot in StratVocabulary.Slots.Concat(StratVocabulary.OpponentSlots))
        {
            for (int k = last; k >= 0; k--)
            {
                StratStep step = document.Steps[k];
                if (OriginOf(document.Map, step, slot, throwOrigins) is { } origin)
                {
                    carried.Add(new StepPosition
                    {
                        Slot = slot, X = Round(origin.X), Y = Round(origin.Y), LevelMinZ = origin.LevelMinZ,
                        YawDegrees = origin.YawDegrees is { } yaw ? Round(yaw) : null
                    });
                    break;
                }

                // The last entry for a slot wins, as in the projection.
                if (step.Positions.LastOrDefault(p => string.Equals(p.Slot, slot, StringComparison.Ordinal)
                                                      && double.IsFinite(p.X) && double.IsFinite(p.Y)) is { } found)
                {
                    carried.Add(found);
                    break;
                }
            }
        }

        return carried;
    }

    // The projection's rule (StratSceneProjection.ThrowOriginOf): a throw by this one slot whose lineup resolves.
    private static TokenPlacement? OriginOf(string map, StratStep step, string slot, ThrowOriginResolver? resolve) =>
        resolve is not null
        && string.Equals(step.Verb, "throw", StringComparison.Ordinal)
        && string.Equals(step.Actor, slot, StringComparison.Ordinal)
        && step.Utility is { LineupId: not null } utility
        && resolve(map, utility) is { } placement
        && float.IsFinite(placement.X) && float.IsFinite(placement.Y)
            ? placement
            : null;

    // Two decimals, as a drag writes them.
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
