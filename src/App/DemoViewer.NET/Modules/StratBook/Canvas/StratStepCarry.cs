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
    /// <param name="placeCentres">
    ///     The projection's place centres, so a token turned by a watching line carries that facing; null carries
    ///     only an explicit view angle.
    /// </param>
    public static List<StepPosition> PositionsAt(StratDocument document, int stepIndex, ThrowOriginResolver? throwOrigins,
        PlaceCentreResolver? placeCentres = null)
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
        foreach (string slot in StratVocabulary.Slots.Concat(StratVocabulary.OpponentSlots))
        {
            TokenPlacement?[] placements = StratSceneProjection.Placements(path, origins, slot, placeCentres);
            for (int k = last; k >= 0; k--)
            {
                if (placements[k] is not { } placement)
                {
                    continue;
                }

                // An authored entry the projection did not turn is carried as stored, unknown fields included.
                StepPosition? stored = document.Steps[k].Positions.LastOrDefault(p => string.Equals(p.Slot, slot, StringComparison.Ordinal)
                                                                                     && double.IsFinite(p.X) && double.IsFinite(p.Y));
                carried.Add(stored is not null && Same(stored, placement)
                    ? stored
                    : new StepPosition
                    {
                        Slot = slot, X = Round(placement.X), Y = Round(placement.Y), LevelMinZ = placement.LevelMinZ,
                        YawDegrees = placement.YawDegrees is { } yaw ? Round(yaw) : null
                    });
                break;
            }
        }

        return carried;
    }

    private static bool Same(StepPosition stored, TokenPlacement placement) =>
        (float)stored.X == placement.X && (float)stored.Y == placement.Y
                                       && (stored.YawDegrees is { } yaw ? (float?)yaw : null) == placement.YawDegrees;

    // Two decimals, as a drag writes them.
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
