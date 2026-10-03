#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     Joins a grenade detonation to the projectile that caused it (#56, #59): a <see cref="ProjectileSample" />'s
///     <c>ThrowerSlot</c> is held from the projectile's creation, so it survives the thrower dying mid-flight,
///     where the live entity joins this replaces (<c>m_hOwnerEntity</c>, a pawn handle) do not. Pure: a
///     detonation's tick, position and class in, the matching sample out. Row assembly (the release tick, the
///     detonation join) stays with the caller, per <see cref="ProjectileSampler" />'s own contract.
/// </summary>
public static class ProjectileThrowerMatch
{
    /// <summary>Ticks either side of the detonation a <c>Removed</c> sample may fall within ("a few frames").</summary>
    public const int TickTolerance = 8;

    /// <summary>World units a <c>Removed</c> sample's position may differ from the detonation's ("a small radius").</summary>
    public const double PositionTolerance = 256;

    /// <summary>
    ///     The <c>Removed</c> sample of <paramref name="className" /> nearest <paramref name="position" />, within
    ///     <see cref="TickTolerance" /> of <paramref name="tick" /> and <see cref="PositionTolerance" /> of
    ///     <paramref name="position" />; null when nothing matches. Ties break on the closer position.
    /// </summary>
    /// <param name="samples">A demo's or a round window's projectile samples; only <c>Removed</c> ones are considered.</param>
    /// <param name="className">One of <see cref="GrenadeProjectileClasses" />.</param>
    /// <param name="tick">The detonation's frame-clock tick.</param>
    /// <param name="position">The detonation's world position.</param>
    public static ProjectileSample? Nearest(IEnumerable<ProjectileSample> samples, string className, int tick, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double maxDistanceSquared = PositionTolerance * PositionTolerance;
        ProjectileSample? best = null;
        double bestDistanceSquared = double.MaxValue;
        foreach (ProjectileSample sample in samples)
        {
            if (!sample.Removed || !string.Equals(sample.ClassName, className, StringComparison.Ordinal)
                                 || Math.Abs(sample.Tick - tick) > TickTolerance
                                 || sample.Position is not { } samplePosition)
            {
                continue;
            }

            double distanceSquared = Vector3.DistanceSquared(samplePosition, position);
            if (distanceSquared <= maxDistanceSquared && distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = sample;
            }
        }

        return best;
    }
}
