#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     One utility detonation the detectors read (suggested-tags.md §3.2). Ticks are frame clock
///     (<c>GameEvent.GameTick</c>).
/// </summary>
/// <param name="Tick">The detonation.</param>
/// <param name="Kind"><c>smoke</c>, <c>flash</c>, <c>he</c>, <c>inferno</c> or <c>decoy</c>.</param>
/// <param name="ThrowerSlot">The thrower's slot, or -1 when the event does not name one as a slot.</param>
/// <param name="Side">The thrower's side in the round (2 or 3), or 0 when it has none.</param>
/// <param name="Position">Where it went off.</param>
/// <param name="Place">The resolved place, or null when nothing was near enough.</param>
public sealed record PlacedEvent(int Tick, string Kind, int ThrowerSlot, int Side, Vector3 Position, string? Place);

/// <summary>Reads the detonations out of a held parse.</summary>
public static class DetonationEvents
{
    public const string Smoke = "smoke";
    public const string Flash = "flash";
    public const string He = "he";
    public const string Inferno = "inferno";
    public const string Decoy = "decoy";

    /// <summary>
    ///     Every smoke, flash, HE, inferno start and decoy start in tick order, unplaced;
    ///     <see cref="ProposalDetection" /> seats them per round. <c>inferno_startburn</c> has no thrower
    ///     on the wire (21 to 27 percent of detonations on the measured demos), and <c>decoy_started</c>'s
    ///     <c>UserId</c> is a pawn handle rather than a slot, so both come out with -1 unless
    ///     <paramref name="projectiles" /> names the projectile that caused them (#56, #59).
    /// </summary>
    /// <param name="demo">The held parse.</param>
    /// <param name="projectiles">
    ///     The demo's <c>Removed</c> projectile samples, or null to skip the match and leave inferno and decoy
    ///     unresolved as before.
    /// </param>
    public static List<PlacedEvent> From(ParsedDemo demo, IReadOnlyList<ProjectileSample>? projectiles = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        List<PlacedEvent> events = [];
        foreach (GameEvent fire in demo.AllGameEvents)
        {
            PlacedEvent? placed = fire.Payload switch
            {
                SmokeGrenadeDetonateEvent e => new PlacedEvent(fire.GameTick, Smoke, e.UserId, 0, new Vector3(e.X, e.Y, e.Z), null),
                FlashbangDetonateEvent e => new PlacedEvent(fire.GameTick, Flash, e.UserId, 0, new Vector3(e.X, e.Y, e.Z), null),
                HegrenadeDetonateEvent e => new PlacedEvent(fire.GameTick, He, e.UserId, 0, new Vector3(e.X, e.Y, e.Z), null),
                InfernoStartburnEvent e => new PlacedEvent(fire.GameTick, Inferno,
                    ThrowerSlotNear(projectiles, GrenadeProjectileClasses.Molotov, fire.GameTick, new Vector3(e.X, e.Y, e.Z)),
                    0, new Vector3(e.X, e.Y, e.Z), null),
                DecoyStartedEvent e => new PlacedEvent(fire.GameTick, Decoy,
                    ThrowerSlotNear(projectiles, GrenadeProjectileClasses.Decoy, fire.GameTick, new Vector3(e.X, e.Y, e.Z)),
                    0, new Vector3(e.X, e.Y, e.Z), null),
                _ => null
            };
            if (placed is not null)
            {
                events.Add(placed);
            }
        }

        events.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return events;
    }

    // -1 with no projectiles offered or no match near the detonation; ProjectileThrowerMatch does the rest.
    private static int ThrowerSlotNear(IReadOnlyList<ProjectileSample>? projectiles, string className, int tick, Vector3 position) =>
        projectiles is not null && ProjectileThrowerMatch.Nearest(projectiles, className, tick, position) is { ThrowerSlot: >= 0 } sample
            ? sample.ThrowerSlot
            : -1;
}

/// <summary>
///     The private sparse sample cloud (suggested-tags.md §3.2): every 25th alive placed sample of the
///     fallback walk, 5,000 to 8,000 points per demo. A detonation takes the place of its nearest
///     point within the resolver's distance. It exists only when the walk runs; with the index or
///     with zones the resolver never reaches it (integrator correction 16).
/// </summary>
public sealed class DetonationCloud
{
    /// <summary>One sample in this many is kept.</summary>
    public const int KeepEvery = 25;

    private readonly List<(Vector3 Position, string Place)> _points = [];
    private int _offered;

    /// <summary>The kept points.</summary>
    public int Count => _points.Count;

    /// <summary>Offers one alive placed sample; every <see cref="KeepEvery" />-th is kept.</summary>
    /// <param name="position">The sample's world position.</param>
    /// <param name="place">Its place.</param>
    public void Offer(Vector3 position, string place)
    {
        if (_offered++ % KeepEvery == 0)
        {
            _points.Add((position, place));
        }
    }

    /// <summary>Adds a point unconditionally; tests build a cloud by hand.</summary>
    /// <param name="position">A world position.</param>
    /// <param name="place">Its place.</param>
    public void Add(Vector3 position, string place) => _points.Add((position, place));

    /// <summary>The place of the nearest point in world distance, or null when none is within <paramref name="maxDistance" />.</summary>
    /// <param name="position">A detonation's world position.</param>
    /// <param name="maxDistance">World units.</param>
    public string? Nearest(Vector3 position, double maxDistance)
    {
        double best = maxDistance * maxDistance;
        string? place = null;
        foreach ((Vector3 point, string name) in _points)
        {
            double d = Vector3.DistanceSquared(point, position);
            if (d <= best)
            {
                best = d;
                place = name;
            }
        }

        return place;
    }
}

/// <summary>
///     The one resolver call behind which a detonation gets its place, in the precedence integrator
///     correction 16 fixes: the map's zones, else the nearest index place centroid on the detonation's
///     Z band, else the walk's private cloud. The 400-unit distance Suggested Tags measured (2.6 to 8.5
///     percent unresolved with the cloud) is the limit for both fallbacks.
/// </summary>
public sealed class DetonationPlaceResolver
{
    /// <summary>World units beyond which no sample or centroid is near enough.</summary>
    public const double MaxDistance = 400;

    // Utility pops above the floor a player stands on (a flash in the air, a smoke on a ledge), so the
    // centroid band reaches two 64-unit buckets either way of the detonation's own.
    private const double BandHalfHeight = 2 * MapSpace.LevelQuantum;

    private readonly DetonationCloud? _cloud;
    private readonly IReadOnlyList<PlaceSummary>? _places;
    private readonly IZonePlaceResolver? _zones;

    /// <param name="zones">The map's zone resolver, or null when the map has none.</param>
    /// <param name="places">The index's place summaries for the map, or null when nothing is indexed.</param>
    /// <param name="cloud">The walk's cloud, or null when occupancy came from the index.</param>
    public DetonationPlaceResolver(IZonePlaceResolver? zones, IReadOnlyList<PlaceSummary>? places, DetonationCloud? cloud)
    {
        _zones = zones;
        _places = places is { Count: > 0 } ? places : null;
        _cloud = cloud;
    }

    /// <summary>The place a detonation went off in, or null.</summary>
    /// <param name="position">Its world position.</param>
    public string? Resolve(Vector3 position)
    {
        if (_zones?.Resolve(position) is { Length: > 0 } zoned)
        {
            return zoned;
        }

        if (_places is not null)
        {
            double bucket = MapSpace.QuantizeZ(position.Z);
            if (PlaceSnap.Nearest(_places, position.X, position.Y, bucket - BandHalfHeight, bucket + BandHalfHeight + 1,
                    MaxDistance) is { } snapped)
            {
                return snapped.Place;
            }
        }

        return _cloud?.Nearest(position, MaxDistance);
    }

    /// <summary>Every event with its place filled by <see cref="Resolve" />.</summary>
    /// <param name="events">Unplaced events.</param>
    public List<PlacedEvent> Place(IEnumerable<PlacedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return [.. events.Select(e => e with { Place = Resolve(e.Position) })];
    }
}
