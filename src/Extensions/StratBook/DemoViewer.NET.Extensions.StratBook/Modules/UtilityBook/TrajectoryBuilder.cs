#region

using System.Numerics;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     Thins a projectile's flight to a polyline: the first point is
///     where the grenade left the hand, a moved sample is kept when the stride divides the moved-sample
///     count or the bounce count rose since the last kept point, and the last moved sample is always kept
///     so the line ends where the projectile came to rest or went off.
/// </summary>
public sealed class TrajectoryBuilder
{
    private readonly List<TrajectoryPoint> _points = [];
    private readonly int _stride;
    private int _keptBounces;
    private Vector3? _lastSeen;
    private int _moved;
    private TrajectoryPoint? _pending;

    /// <param name="stride">Keep every n-th moved sample; 1 keeps them all.</param>
    public TrajectoryBuilder(int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        _stride = stride;
    }

    /// <summary>The number of kept points so far, the pending last one excluded.</summary>
    public int Count => _points.Count;

    /// <summary>The first point: the release point on the spawn tick, never a cell read.</summary>
    /// <param name="tick">The spawn tick.</param>
    /// <param name="position"><c>m_vInitialPosition</c>.</param>
    public void Start(int tick, Vector3 position)
    {
        if (_points.Count > 0 || _lastSeen is not null)
        {
            return;
        }

        _points.Add(new TrajectoryPoint(tick, position.X, position.Y, position.Z, 0));
        _lastSeen = position;
    }

    /// <summary>Offers one sample. Returns true when it moved.</summary>
    /// <param name="tick">Its tick.</param>
    /// <param name="position">Its position.</param>
    /// <param name="bounces">Its <c>m_nBounces</c>.</param>
    public bool Offer(int tick, Vector3 position, int bounces)
    {
        if (_lastSeen is null)
        {
            // No release point was offered (a projectile already in flight when the recording began).
            _points.Add(new TrajectoryPoint(tick, position.X, position.Y, position.Z, bounces));
            _lastSeen = position;
            _keptBounces = bounces;
            return true;
        }

        if (Vector3.Distance(position, _lastSeen.Value) <= GrenadeRules.MovedEpsilon)
        {
            return false;
        }

        _lastSeen = position;
        _moved++;
        TrajectoryPoint point = new(tick, position.X, position.Y, position.Z, bounces);
        if (_moved % _stride == 0 || bounces > _keptBounces)
        {
            _points.Add(point);
            _keptBounces = bounces;
            _pending = null;
        }
        else
        {
            _pending = point;
        }

        return true;
    }

    /// <summary>The polyline, with the last moved sample appended when the stride skipped it.</summary>
    public List<TrajectoryPoint> Finish()
    {
        if (_pending is { } last)
        {
            _points.Add(last);
            _pending = null;
        }

        return [.. _points];
    }
}
