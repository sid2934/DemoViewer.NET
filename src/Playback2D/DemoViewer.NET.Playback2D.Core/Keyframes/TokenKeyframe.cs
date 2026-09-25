#region

using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Keyframes;

/// <summary>
///     One token's position at one tick of the strat frame clock (step-authoring.md §3.3).
///     <para>
///         <b>The floor is keyed the way ink is.</b> <see cref="LevelMinZ" /> is
///         <c>MapSpace.QuantizeZ(level.ZMin)</c>, the same level key <c>SpaceRef.World</c> and a strat's
///         <c>positions[].levelMinZ</c> carry, never a sample Z and never a floor index.
///     </para>
/// </summary>
/// <param name="Tick">Strat frame-clock tick: 0 is freeze-end, <see cref="StepSchedule.TicksPerSecond" /> per second.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="LevelMinZ">The quantized lower Z of the level the token stands on.</param>
/// <param name="YawDegrees">Facing, world yaw: 0 is +X, 90 is +Y.</param>
public readonly record struct TokenKeyframe(int Tick, float X, float Y, double LevelMinZ, float YawDegrees)
{
    /// <summary>
    ///     The world Z a marker at this keyframe carries: the middle of the level's first quantum, so the
    ///     pane's Z-band lookup lands on the intended floor rather than on its boundary.
    /// </summary>
    public double MarkerZ => LevelMinZ + MapSpace.LevelQuantum / 2;
}

/// <summary>How a token moves from one keyframe toward the next.</summary>
public enum TokenInterpolation
{
    /// <summary>Straight line in world space once the keyframe's hold has run out. The default.</summary>
    Linear,

    /// <summary>Stays put for the whole segment and jumps at the next keyframe.</summary>
    Hold
}

/// <summary>One token as sampled at a tick: its slot and where it is.</summary>
/// <param name="Slot">The slot, one of <see cref="TokenSlots.All" />.</param>
/// <param name="Position">The sampled position; its <c>Tick</c> is the tick that was sampled.</param>
public readonly record struct TokenSample(string Slot, TokenKeyframe Position)
{
    /// <summary>Whether this is one of the opponent tokens <c>O1..O5</c>.</summary>
    public bool IsOpponent => TokenSlots.IsOpponent(Slot);
}

/// <summary>
///     The ten token slots: the strat's own <c>A..E</c> and the opponent tokens <c>O1..O5</c>, an
///     additive extension of the <c>positions[].slot</c> vocabulary (step-authoring.md §3.3, decision 2).
///     Opponents carry no identity and no role; they exist because a setup means nothing without where
///     the other side is expected.
/// </summary>
public static class TokenSlots
{
    /// <summary>The strat's own side, in order.</summary>
    public static readonly IReadOnlyList<string> Own = ["A", "B", "C", "D", "E"];

    /// <summary>The opponent tokens, in order.</summary>
    public static readonly IReadOnlyList<string> Opponents = ["O1", "O2", "O3", "O4", "O5"];

    /// <summary>All ten, own side first. This order is the order a track set samples in.</summary>
    public static readonly IReadOnlyList<string> All = [.. Own, .. Opponents];

    /// <summary>A slot's position in <see cref="All" />, or -1 when it is not one of the ten.</summary>
    /// <param name="slot">The slot.</param>
    public static int OrderOf(string? slot)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], slot, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether a slot is one of the ten. Ordinal: <c>a</c> is not <c>A</c>.</summary>
    /// <param name="slot">The slot.</param>
    public static bool IsKnown(string? slot) => OrderOf(slot) >= 0;

    /// <summary>Whether a slot is one of <see cref="Opponents" />.</summary>
    /// <param name="slot">The slot.</param>
    public static bool IsOpponent(string? slot) => OrderOf(slot) >= Own.Count;
}
