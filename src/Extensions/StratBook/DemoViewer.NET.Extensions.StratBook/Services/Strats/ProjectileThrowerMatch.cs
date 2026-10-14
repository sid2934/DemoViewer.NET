#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats;

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
        (bool found, ProjectileSample? best) = Nearest(samples.Where(s => s.Removed), RemovedProjectile.From, className, tick, position);
        return found ? best : null;
    }

    /// <summary>The same match over a collector's <see cref="RemovedProjectile" /> rows.</summary>
    /// <param name="removed">A demo's removed projectiles.</param>
    /// <param name="className">One of <see cref="GrenadeProjectileClasses" />.</param>
    /// <param name="tick">The detonation's frame-clock tick.</param>
    /// <param name="position">The detonation's world position.</param>
    public static RemovedProjectile? Nearest(IEnumerable<RemovedProjectile> removed, string className, int tick, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(removed);
        (bool found, RemovedProjectile best) = Nearest(removed, r => r, className, tick, position);
        return found ? best : null;
    }

    private static (bool Found, T? Best) Nearest<T>(IEnumerable<T> rows, Func<T, RemovedProjectile> view, string className, int tick,
        Vector3 position)
    {
        double maxDistanceSquared = PositionTolerance * PositionTolerance;
        T? best = default;
        bool found = false;
        double bestDistanceSquared = double.MaxValue;
        foreach (T row in rows)
        {
            RemovedProjectile removed = view(row);
            if (!string.Equals(removed.ClassName, className, StringComparison.Ordinal)
                || Math.Abs(removed.Tick - tick) > TickTolerance
                || removed.Position is not { } removedPosition)
            {
                continue;
            }

            double distanceSquared = Vector3.DistanceSquared(removedPosition, position);
            if (distanceSquared <= maxDistanceSquared && distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = row;
                found = true;
            }
        }

        return (found, best);
    }
}

/// <summary>
///     A grenade projectile on the frame it left, with its last-seen state: the part of a <c>Removed</c>
///     <see cref="ProjectileSample" /> the thrower match reads.
/// </summary>
/// <param name="Tick">The frame clock of the frame it was gone on.</param>
/// <param name="ClassName">The projectile class.</param>
/// <param name="ThrowerSlot">The thrower's player slot, held from creation, or -1.</param>
/// <param name="Position">Where it ended, or null when never seen.</param>
public readonly record struct RemovedProjectile(int Tick, string ClassName, int ThrowerSlot, Vector3? Position)
{
    /// <summary>The row of a <c>Removed</c> sampler sample.</summary>
    /// <param name="sample">A sample with <c>Removed</c> set.</param>
    public static RemovedProjectile From(ProjectileSample sample) =>
        new(sample.Tick, sample.ClassName, sample.ThrowerSlot, sample.Position);
}
