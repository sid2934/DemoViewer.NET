#region

using System.Numerics;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     A projectile's own fields as the frame observer saw them, whatever its thrower. The only source for a grenade
///     whose thrower never resolved, which has no <c>grenade_projectiles</c> row.
/// </summary>
/// <param name="ClassName">The server class.</param>
/// <param name="SpawnTick">The frame-clock tick it first appeared on.</param>
/// <param name="InitialPosition"><c>m_vInitialPosition</c> on that frame.</param>
/// <param name="InitialVelocity"><c>m_vInitialVelocity</c> on that frame.</param>
/// <param name="IsIncendiary"><c>m_bIsIncGrenade</c> on that frame.</param>
/// <param name="EndTick">The removal frame's tick, or the last tick it was seen on while live.</param>
/// <param name="Removed">False for a grenade still in the world when the run ended.</param>
/// <param name="LastPosition">The last position read.</param>
/// <param name="LastMovedTick">The tick it came to rest on.</param>
/// <param name="Bounces">The most bounces seen.</param>
public sealed record FlightFacts(
    string ClassName,
    int SpawnTick,
    Vector3? InitialPosition,
    Vector3? InitialVelocity,
    bool? IsIncendiary,
    int EndTick,
    bool Removed,
    Vector3? LastPosition,
    int LastMovedTick,
    int Bounces);

/// <summary>Every grenade's flight at the configured stride, by projectile, as the frame observer kept it.</summary>
/// <param name="Flights">The polylines, keyed by entity index and serial.</param>
/// <param name="Stride">The stride the flights were thinned at.</param>
public sealed record GrenadeTrajectories(IReadOnlyDictionary<(int Index, int Serial), List<TrajectoryPoint>> Flights, int Stride)
{
    /// <summary>Each flight's projectile fields, by the same key.</summary>
    public IReadOnlyDictionary<(int Index, int Serial), FlightFacts> Facts { get; init; } = new Dictionary<(int Index, int Serial), FlightFacts>();

    /// <summary>A projectile's flight, or an empty line when the observer never saw it.</summary>
    /// <param name="index">The entity index.</param>
    /// <param name="serial">The entity serial.</param>
    public List<TrajectoryPoint> Of(int index, int serial) => Flights.GetValueOrDefault((index, serial)) ?? [];
}

/// <summary>
///     Thins each live projectile's positions into its polyline, frame by frame: the release point first, then
///     every n-th moved sample with the bounce vertices and the last moved sample kept. The one part of the grenade
///     walk that still rides the frame observer; everything else comes off the rules tables.
/// </summary>
public sealed class GrenadeTrajectoryFold
{
    private readonly Dictionary<(int Index, int Serial), Flight> _flights = [];
    private readonly int _stride;

    /// <param name="stride">Keep every n-th moved sample; 1 keeps them all.</param>
    public GrenadeTrajectoryFold(int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        _stride = stride;
    }

    /// <summary>A projectile's first frame: the release point, then its first position, and the fields <see cref="FlightFacts" /> keeps.</summary>
    public void Created(int index, int serial, string className, int tick, Vector3? initialPosition, Vector3? initialVelocity,
        bool? isIncendiary, Vector3? position, int bounces)
    {
        (int, int) key = (index, serial);
        if (_flights.ContainsKey(key))
        {
            return;
        }

        Flight flight = new(new TrajectoryBuilder(_stride), tick)
        {
            ClassName = className, InitialPosition = initialPosition, InitialVelocity = initialVelocity, IsIncendiary = isIncendiary,
            EndTick = tick, LastPosition = position, LastMovedTick = tick, Bounces = bounces
        };
        if (initialPosition is { } start)
        {
            flight.Builder.Start(tick, start);
        }

        if (position is { } at)
        {
            flight.Builder.Offer(tick, at, bounces);
        }

        _flights[key] = flight;
    }

    /// <summary>A later frame of a live projectile. The spawn frame's own row is skipped: <see cref="Created" /> offered it.</summary>
    public void Seen(int index, int serial, int tick, Vector3? position, int bounces)
    {
        if (!_flights.TryGetValue((index, serial), out Flight? flight) || flight.SpawnTick == tick)
        {
            return;
        }

        flight.EndTick = tick;
        flight.Bounces = Math.Max(flight.Bounces, bounces);
        if (position is { } at)
        {
            flight.LastPosition = at;
            if (flight.Builder.Offer(tick, at, bounces))
            {
                flight.LastMovedTick = tick;
            }
        }
    }

    /// <summary>A projectile gone on this frame, with its last-seen position.</summary>
    public void Removed(int index, int serial, int tick, Vector3? position)
    {
        if (_flights.TryGetValue((index, serial), out Flight? flight))
        {
            flight.Removed = true;
            flight.EndTick = tick;
            flight.LastPosition = position ?? flight.LastPosition;
        }
    }

    /// <summary>The flights so far, each line finished.</summary>
    public GrenadeTrajectories Finish() =>
        new(_flights.ToDictionary(f => f.Key, f => f.Value.Builder.Finish()), _stride)
        {
            Facts = _flights.ToDictionary(f => f.Key, f => f.Value.Facts())
        };

    private sealed record Flight(TrajectoryBuilder Builder, int SpawnTick)
    {
        public string ClassName { get; init; } = "";
        public Vector3? InitialPosition { get; init; }
        public Vector3? InitialVelocity { get; init; }
        public bool? IsIncendiary { get; init; }
        public int EndTick { get; set; }
        public bool Removed { get; set; }
        public Vector3? LastPosition { get; set; }
        public int LastMovedTick { get; set; }
        public int Bounces { get; set; }

        public FlightFacts Facts() =>
            new(ClassName, SpawnTick, InitialPosition, InitialVelocity, IsIncendiary, EndTick, Removed, LastPosition, LastMovedTick, Bounces);
    }
}

/// <summary>The trajectory fold on a visit's rules run, as the grenade pass's collector.</summary>
public sealed class GrenadeTrajectoryCollector : IFrameCollector
{
    /// <summary>The collector's id, which the grenade pass reads the flights by.</summary>
    public const string CollectorId = "net.demoviewer.pack.stratbook.grenades.trajectories";

    /// <summary>Bumped when what it puts changes.</summary>
    public const int Version = 2;

    private readonly GrenadeTrajectoryFold _fold;

    /// <param name="trajectoryStride">Keep every n-th moved sample of each flight.</param>
    public GrenadeTrajectoryCollector(int trajectoryStride)
    {
        _fold = new GrenadeTrajectoryFold(trajectoryStride);
    }

    /// <summary>The registration: every projectile class, no pawn reads, no commands, no events.</summary>
    /// <param name="trajectoryStride">The live stride setting, read when a run starts.</param>
    public static CollectorContribution Contribution(Func<int> trajectoryStride)
    {
        ArgumentNullException.ThrowIfNull(trajectoryStride);
        return new CollectorContribution(CollectorId, GrenadeIndexEvaluator.EvaluatorId, Version, [], GrenadeProjectileClasses.All,
            () => new GrenadeTrajectoryCollector(Math.Max(1, trajectoryStride())));
    }

    /// <inheritdoc />
    public void OnFrame(IFrameReadView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        int tick = view.Tick;
        IReadOnlyList<ObservedProjectile> created = view.Created;
        foreach (ObservedProjectile row in created.Count > 1 ? [.. created.OrderBy(r => r.EntityIndex)] : created)
        {
            _fold.Created(row.EntityIndex, row.Serial, row.ClassName, tick, row.InitialPosition, row.InitialVelocity, row.IsIncendiary,
                row.Position, row.Bounces);
        }

        foreach (ObservedProjectile row in view.Projectiles)
        {
            _fold.Seen(row.EntityIndex, row.Serial, tick, row.Position, row.Bounces);
        }

        foreach (ObservedProjectile row in view.Removed)
        {
            _fold.Removed(row.EntityIndex, row.Serial, tick, row.Position);
        }
    }

    /// <inheritdoc />
    public void Finish(ICollectorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Put(_fold.Finish());
    }
}

/// <summary>The trajectory fold on the engine's own observer, for a run made over a held parse outside a visit.</summary>
public sealed class GrenadeTrajectoryObserver : IFrameObserver
{
    private readonly GrenadeTrajectoryFold _fold;

    /// <param name="trajectoryStride">Keep every n-th moved sample of each flight.</param>
    public GrenadeTrajectoryObserver(int trajectoryStride)
    {
        _fold = new GrenadeTrajectoryFold(trajectoryStride);
    }

    /// <inheritdoc />
    public void OnFrame(FrameView frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int tick = frame.Tick;
        foreach (ProjectileRow row in frame.Created)
        {
            _fold.Created(row.EntityIndex, row.Serial, row.ClassName, tick, row.InitialPosition, row.InitialVelocity, row.IsIncendiary,
                row.Position, row.Bounces);
        }

        foreach (ProjectileRow row in frame.Projectiles)
        {
            _fold.Seen(row.EntityIndex, row.Serial, tick, row.Position, row.Bounces);
        }

        foreach (ProjectileRow row in frame.Removed)
        {
            _fold.Removed(row.EntityIndex, row.Serial, tick, row.Position);
        }
    }

    /// <inheritdoc />
    public void Finish()
    {
    }

    /// <summary>The flights, each line finished.</summary>
    public GrenadeTrajectories Flights() => _fold.Finish();
}
