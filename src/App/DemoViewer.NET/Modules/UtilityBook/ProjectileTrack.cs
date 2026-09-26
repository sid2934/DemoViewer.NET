#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     Thins a projectile's flight to a polyline (grenade-walk.md §3.3 step 3, D1): the first point is
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

/// <summary>
///     One projectile's life, folded from its <see cref="ProjectileSample" />s: the creation, every sample
///     it was seen at, and the removal. What the engine walk knows; the row assembly adds the events and
///     the thrower's pawn.
/// </summary>
public sealed class ProjectileTrack
{
    private readonly TrajectoryBuilder _trajectory;
    private List<TrajectoryPoint>? _finished;

    /// <param name="created">The projectile's first sample.</param>
    /// <param name="stride">The trajectory stride.</param>
    public ProjectileTrack(ProjectileSample created, int stride)
    {
        EntityIndex = created.EntityIndex;
        Serial = created.Serial;
        ClassName = created.ClassName;
        SpawnFrame = created.FrameIndex;
        SpawnTick = created.Tick;
        InitialPosition = created.InitialPosition;
        InitialVelocity = created.InitialVelocity;
        LastFrame = created.FrameIndex;
        LastTick = created.Tick;
        EndTick = created.Tick;
        LastMovedTick = created.Tick;
        _trajectory = new TrajectoryBuilder(stride);
        if (created.InitialPosition is { } start)
        {
            _trajectory.Start(created.Tick, start);
        }

        Observe(created);
    }

    public int EntityIndex { get; }
    public int Serial { get; }
    public string ClassName { get; }

    /// <summary>The frame index the projectile appeared on.</summary>
    public int SpawnFrame { get; }

    public int SpawnTick { get; }
    public Vector3? InitialPosition { get; }
    public Vector3? InitialVelocity { get; }

    /// <summary>The first resolved thrower slot, held for the projectile's life; -1 when it never resolved.</summary>
    public int ThrowerSlot { get; private set; } = -1;

    /// <summary>The last frame the projectile existed on.</summary>
    public int LastFrame { get; private set; }

    /// <summary>The tick of <see cref="LastFrame" />.</summary>
    public int LastTick { get; private set; }

    /// <summary>The removal frame's tick when removed, else <see cref="LastTick" />.</summary>
    public int EndTick { get; private set; }

    /// <summary>The last position a cell read gave, or null when none decoded.</summary>
    public Vector3? LastPosition { get; private set; }

    /// <summary>The tick of the last sample that moved: where the flight ended.</summary>
    public int LastMovedTick { get; private set; }

    public int MaxBounces { get; private set; }

    /// <summary>True once the <c>Removed</c> sample arrived.</summary>
    public bool Removed { get; private set; }

    /// <summary>The row id: <c>g{index}-{serial}</c>.</summary>
    public string Id => $"g{EntityIndex}-{Serial}";

    /// <summary>The polyline, fixed at the first call.</summary>
    public List<TrajectoryPoint> Trajectory => _finished ??= _trajectory.Finish();

    /// <summary>Folds one later sample in.</summary>
    /// <param name="sample">A sample for this projectile.</param>
    public void Observe(ProjectileSample sample)
    {
        if (sample.ThrowerSlot >= 0 && ThrowerSlot < 0)
        {
            ThrowerSlot = sample.ThrowerSlot;
        }

        if (sample.Removed)
        {
            // Every value but the frame and tick is the last one seen, on the frame before.
            Removed = true;
            EndTick = sample.Tick;
            if (sample.Position is { } end)
            {
                LastPosition = end;
            }

            return;
        }

        LastFrame = sample.FrameIndex;
        LastTick = sample.Tick;
        EndTick = sample.Tick;
        MaxBounces = Math.Max(MaxBounces, sample.Bounces);
        if (sample.Position is not { } position)
        {
            return;
        }

        LastPosition = position;
        if (_trajectory.Offer(sample.Tick, position, sample.Bounces) && !sample.Created)
        {
            LastMovedTick = sample.Tick;
        }
    }

    /// <summary>
    ///     Folds a walk's samples into tracks, in creation order. Streaming: nothing is buffered but the
    ///     live projectiles and their polylines, so a stride-1 walk costs a few hundred small objects.
    /// </summary>
    /// <param name="samples">A <c>ProjectileSampler.Walk</c>, or a synthetic sequence in the same order.</param>
    /// <param name="stride">The trajectory stride.</param>
    /// <param name="ct">Checked every few thousand samples.</param>
    public static List<ProjectileTrack> Collect(IEnumerable<ProjectileSample> samples, int stride, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        List<ProjectileTrack> tracks = [];
        Dictionary<(int Index, int Serial), ProjectileTrack> live = [];
        int seen = 0;
        foreach (ProjectileSample sample in samples)
        {
            if ((++seen & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            (int, int) key = (sample.EntityIndex, sample.Serial);
            if (live.TryGetValue(key, out ProjectileTrack? track) && !sample.Created)
            {
                track.Observe(sample);
                if (sample.Removed)
                {
                    live.Remove(key);
                }

                continue;
            }

            if (sample.Removed)
            {
                continue; // a removal for a projectile never created in this walk
            }

            track = new ProjectileTrack(sample with { Created = true }, stride);
            tracks.Add(track);
            live[key] = track;
        }

        return tracks;
    }
}
