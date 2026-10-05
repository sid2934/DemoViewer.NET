#region

using System.Numerics;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>What a volume in <c>zones.json</c> is, by the entity class the baker read it from.</summary>
public enum ZoneVolumeKind
{
    /// <summary><c>env_cs_place</c>: a named place.</summary>
    Place,

    /// <summary><c>func_bomb_target</c>: one of the two bombsites.</summary>
    Bombsite,

    /// <summary><c>func_buyzone</c>.</summary>
    Buyzone,

    /// <summary><c>func_hostage_rescue</c>. Not seen on the ten shipped maps.</summary>
    HostageRescue
}

/// <summary>The two bombsites, by <c>bomb_site_designation</c> (0 = A, 1 = B).</summary>
public enum Bombsite
{
    /// <summary>Designation 0.</summary>
    A,

    /// <summary>Designation 1.</summary>
    B
}

/// <summary>
///     One half-space of a convex hull. Inside is <c>n·p - d &lt;= 0</c>; the baker normalises every
///     hull to this convention because the opposite sign gives the mirror image and silently halves
///     coverage.
/// </summary>
/// <param name="Nx">Normal X.</param>
/// <param name="Ny">Normal Y.</param>
/// <param name="Nz">Normal Z.</param>
/// <param name="D">Plane offset.</param>
public readonly record struct ZonePlane(double Nx, double Ny, double Nz, double D)
{
    /// <summary><c>n·p - d</c>: negative inside, positive outside.</summary>
    public double Distance(double x, double y, double z) => Nx * x + Ny * y + Nz * z - D;
}

/// <summary>A convex hull as planes. World space; the entity origin is already applied.</summary>
public sealed class ZoneHull
{
    // A hair of slack so a point exactly on a face (a nav centroid on a volume edge, a click on a
    // boundary) is inside rather than outside; the baker rounds plane offsets to 0.1 units anyway.
    private const double Epsilon = 1e-3;

    /// <summary>Creates a hull.</summary>
    /// <param name="planes">The half-spaces.</param>
    public ZoneHull(IReadOnlyList<ZonePlane> planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        Planes = planes;
    }

    /// <summary>The half-spaces.</summary>
    public IReadOnlyList<ZonePlane> Planes { get; }

    /// <summary>Whether a world point satisfies every plane.</summary>
    public bool Contains(double x, double y, double z)
    {
        for (int i = 0; i < Planes.Count; i++)
        {
            if (Planes[i].Distance(x, y, z) > Epsilon)
            {
                return false;
            }
        }

        return Planes.Count > 0;
    }
}

/// <summary>
///     One trigger or place volume: a set of convex hulls with a world AABB as the pre-reject. A user
///     zone is the same thing built from a polygon and a Z band, so the resolver has one code path.
/// </summary>
public sealed class ZoneVolume
{
    /// <summary>Creates a volume.</summary>
    /// <param name="kind">The entity class.</param>
    /// <param name="placeId">The place this volume names, or -1 for a bombsite or buy zone.</param>
    /// <param name="site">The bombsite, for <see cref="ZoneVolumeKind.Bombsite" />.</param>
    /// <param name="team"><c>T</c> or <c>CT</c>, for a buy zone.</param>
    /// <param name="entity">The baker's entity key, for diagnostics; null on a user zone.</param>
    /// <param name="min">World AABB minimum.</param>
    /// <param name="max">World AABB maximum.</param>
    /// <param name="hulls">The convex hulls. Empty for a volume the baker kept without geometry.</param>
    /// <param name="origin">Baked or custom.</param>
    /// <param name="polygon">A user zone's flat world-XY polygon, kept so the outline layer can draw it exactly.</param>
    public ZoneVolume(ZoneVolumeKind kind, int placeId, Bombsite? site, string? team, string? entity,
        Vector3 min, Vector3 max, IReadOnlyList<ZoneHull> hulls, PlaceOrigin origin = PlaceOrigin.Baked,
        IReadOnlyList<double>? polygon = null)
    {
        ArgumentNullException.ThrowIfNull(hulls);

        Kind = kind;
        PlaceId = placeId;
        Site = site;
        Team = team;
        Entity = entity;
        Min = min;
        Max = max;
        Hulls = hulls;
        Origin = origin;
        Polygon = polygon;
    }

    /// <summary>The entity class.</summary>
    public ZoneVolumeKind Kind { get; }

    /// <summary>The place id, or -1 when the volume names no place.</summary>
    public int PlaceId { get; }

    /// <summary>The bombsite, for a <see cref="ZoneVolumeKind.Bombsite" /> volume.</summary>
    public Bombsite? Site { get; }

    /// <summary><c>T</c> or <c>CT</c> for a buy zone.</summary>
    public string? Team { get; }

    /// <summary>The baker's entity key, or null on a user zone.</summary>
    public string? Entity { get; }

    /// <summary>World AABB minimum. Meaningless when <see cref="HasGeometry" /> is false.</summary>
    public Vector3 Min { get; }

    /// <summary>World AABB maximum.</summary>
    public Vector3 Max { get; }

    /// <summary>The hulls.</summary>
    public IReadOnlyList<ZoneHull> Hulls { get; }

    /// <summary>Baked or custom. Custom volumes sit first in the cascade.</summary>
    public PlaceOrigin Origin { get; }

    /// <summary>The flat world-XY polygon a user zone was built from, or null for a baked hull.</summary>
    public IReadOnlyList<double>? Polygon { get; }

    /// <summary>False for a volume the baker logged and kept without any hull.</summary>
    public bool HasGeometry => Hulls.Count > 0;

    /// <summary>Whether a world point is inside any hull. The AABB is tested first.</summary>
    public bool Contains(in Vector3 world) => Contains(world.X, world.Y, world.Z);

    /// <summary>Whether a world point is inside any hull. The AABB is tested first.</summary>
    public bool Contains(double x, double y, double z)
    {
        if (!HasGeometry || x < Min.X || x > Max.X || y < Min.Y || y > Max.Y || z < Min.Z || z > Max.Z)
        {
            return false;
        }

        for (int i = 0; i < Hulls.Count; i++)
        {
            if (Hulls[i].Contains(x, y, z))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the volume's Z range meets a half-open band.</summary>
    /// <param name="minZ">Band lower Z, inclusive.</param>
    /// <param name="maxZ">Band upper Z, exclusive.</param>
    public bool MeetsBand(double minZ, double maxZ) => HasGeometry && Max.Z >= minZ && Min.Z < maxZ;

    /// <summary>The same geometry under another place id (a merge).</summary>
    /// <param name="placeId">The new place id.</param>
    public ZoneVolume WithPlace(int placeId) =>
        new(Kind, placeId, Site, Team, Entity, Min, Max, Hulls, Origin, Polygon);
}
