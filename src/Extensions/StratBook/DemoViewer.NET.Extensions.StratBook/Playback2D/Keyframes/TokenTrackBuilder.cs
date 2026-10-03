namespace DemoViewer.NET.Playback2D.Core.Keyframes;

/// <summary>One step as the track builder sees it: its tick and how a token leaves it.</summary>
/// <param name="Tick">The step's strat frame-clock tick (<see cref="StepSchedule" />).</param>
/// <param name="HoldTicks">The step's <c>holdSeconds</c> × 64; 0 when null.</param>
/// <param name="Interpolation">The step's <c>interpolation</c>; linear when null.</param>
public readonly record struct TokenStep(int Tick, int HoldTicks, TokenInterpolation Interpolation);

/// <summary>One slot's explicit entry at one step, the <c>positions[]</c> shape.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="LevelMinZ"><c>MapSpace.QuantizeZ(level.ZMin)</c>.</param>
/// <param name="YawDegrees">Facing, or null to keep the previous keyframe's (0 when there is none).</param>
public readonly record struct TokenPlacement(float X, float Y, double LevelMinZ, float? YawDegrees);

/// <summary>
///     Turns one slot's entries across a strat's steps into a <see cref="TokenTrack" />, applying the
///     <b>stationary rule</b> (step-authoring.md §3.3, decision 1).
///     <para>
///         A step with no entry for the slot leaves the token where it is. It moves only in the segment
///         that ends at its next explicit entry: the window of the last step before that entry. Adding an
///         entry at step 4 therefore changes the motion between steps 3 and 4 and nothing else, and never
///         re-times a move the author already saw. Motion that should start earlier is authored by
///         dragging the token part-way at the earlier step.
///     </para>
///     <para>
///         <b>Why the builder inserts a keyframe the author did not write.</b> Appending one keyframe per
///         explicit entry and lerping between them would interpolate across the gap, the alternative §4
///         rejects. So when steps without an entry sit between two entries, the builder pins the token
///         at the tick of the last of them with a copy of the previous keyframe, and that step's hold and
///         interpolation shape the move, as they would had the author placed it there unchanged. With no
///         gap, the step that owns the previous keyframe shapes the move, which is the §3.3 rule as
///         written.
///     </para>
///     <para>
///         Steps sharing a tick: an entry at a tick that already has a keyframe replaces it, so the later
///         step in authoring order wins, as it owns the shared tick in <see cref="StepSchedule" />.
///     </para>
/// </summary>
public static class TokenTrackBuilder
{
    /// <summary>Builds a slot's track.</summary>
    /// <param name="slot">One of <see cref="TokenSlots.All" />.</param>
    /// <param name="steps">Every step on the path, in authoring order, ticks non-decreasing.</param>
    /// <param name="placements">Per step, the slot's entry or null; same length as <paramref name="steps" />.</param>
    /// <exception cref="ArgumentException">Mismatched lengths, or a tick earlier than the one before it.</exception>
    public static TokenTrack Build(string slot, IReadOnlyList<TokenStep> steps, IReadOnlyList<TokenPlacement?> placements)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(placements);

        if (steps.Count != placements.Count)
        {
            throw new ArgumentException("One placement (or null) per step.", nameof(placements));
        }

        List<TokenKeyframe> keyframes = [];
        List<int> holds = [];
        List<TokenInterpolation> segments = [];

        for (int i = 0; i < steps.Count; i++)
        {
            TokenStep step = steps[i];
            if (i > 0 && step.Tick < steps[i - 1].Tick)
            {
                throw new ArgumentException(
                    $"Step {i} at tick {step.Tick} comes before step {i - 1}; the round clock counts down.",
                    nameof(steps));
            }

            if (placements[i] is not { } placement)
            {
                continue;
            }

            if (keyframes.Count > 0)
            {
                TokenKeyframe previous = keyframes[^1];
                float yaw = placement.YawDegrees ?? previous.YawDegrees;
                TokenKeyframe entry = new(step.Tick, placement.X, placement.Y, placement.LevelMinZ, yaw);

                if (previous.Tick == step.Tick)
                {
                    keyframes[^1] = entry;
                    holds[^1] = step.HoldTicks;
                    segments[^1] = step.Interpolation;
                    continue;
                }

                // The step whose window the move plays in: the last one before this entry's tick.
                int mover = i - 1;
                while (steps[mover].Tick == step.Tick)
                {
                    mover--;
                }

                TokenStep from = steps[mover];
                if (from.Tick > previous.Tick)
                {
                    keyframes.Add(previous with { Tick = from.Tick });
                    holds.Add(0);
                    segments.Add(TokenInterpolation.Linear);
                }

                holds[^1] = Math.Max(0, from.HoldTicks);
                segments[^1] = from.Interpolation;

                keyframes.Add(entry);
            }
            else
            {
                keyframes.Add(new TokenKeyframe(step.Tick, placement.X, placement.Y, placement.LevelMinZ,
                    placement.YawDegrees ?? 0));
            }

            // Provisional: the next entry's move overwrites these from whichever step it plays in.
            holds.Add(Math.Max(0, step.HoldTicks));
            segments.Add(step.Interpolation);
        }

        return new TokenTrack(slot, keyframes, holds, segments);
    }
}
