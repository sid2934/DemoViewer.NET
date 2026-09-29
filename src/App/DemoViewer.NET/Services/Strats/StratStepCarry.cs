#region

using System.Text.Json;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The positions a new step starts with: every token where it stands at the step it follows, as copies. A
///     copy, not a reference: the stationary rule would already show the same places, but a later drag on the
///     earlier step would then move the new step's tokens too.
/// </summary>
public static class StratStepCarry
{
    /// <summary>
    ///     Per token (A to E, O1 to O5), the last entry at or before <paramref name="stepIndex" />. A throw with a
    ///     lineup by that slot ends the walk with no entry: its actor stands at the lineup origin in the projection
    ///     only, and with no entry the stationary rule keeps it there, whether or not the lineup has resolved yet.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="stepIndex">The step the new one follows; -1 carries nothing.</param>
    public static List<StepPosition> PositionsAt(StratDocument document, int stepIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<StepPosition> carried = [];
        int last = Math.Min(stepIndex, document.Steps.Count - 1);
        foreach (string slot in StratVocabulary.Slots.Concat(StratVocabulary.OpponentSlots))
        {
            for (int k = last; k >= 0; k--)
            {
                StratStep step = document.Steps[k];
                if (string.Equals(step.Verb, "throw", StringComparison.Ordinal) && step.Utility?.LineupId is not null
                                                                                && string.Equals(step.Actor, slot, StringComparison.Ordinal))
                {
                    break;
                }

                // The last entry for a slot wins, as in the projection.
                StepPosition? found = step.Positions.LastOrDefault(p => string.Equals(p.Slot, slot, StringComparison.Ordinal)
                                                                        && double.IsFinite(p.X) && double.IsFinite(p.Y));
                if (found is not null)
                {
                    carried.Add(JsonSerializer.Deserialize(JsonSerializer.Serialize(found, StratJsonContext.Default.StepPosition),
                        StratJsonContext.Default.StepPosition)!);
                    break;
                }
            }
        }

        return carried;
    }
}
