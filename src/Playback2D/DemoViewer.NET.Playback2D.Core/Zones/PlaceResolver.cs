#region

using System.Numerics;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>
///     Resolves a world point to a place over one <see cref="ZoneSet" />: the volumes first (exact,
///     the game's own rule), then the nearest nav area on the point's floor (covers gaps and edges),
///     then nothing. The two graphs and the per-floor outlines hang off the same object.
///     <para>
///         Built once per set: a 128-unit XY grid over the areas and the place volumes, so a resolve is
///         a handful of AABB and plane tests plus one cell scan. That matters because the Grenade Index
///         and Click To Tag Position call it in loops. Safe to share between threads after construction;
///         the outline cache takes a lock.
///     </para>
///     <para>
///         Cascade order is the volume order of the set: user volumes first, then baked volumes in
///         entity-lump order, so a custom zone always wins inside its own polygon and never affects
///         anything outside it, and two overlapping baked volumes resolve to the first (decision D4).
///     </para>
/// </summary>
public sealed class PlaceResolver
{
    /// <summary>The XY grid cell edge, in world units.</summary>
    public const double GridCell = 128.0;

    /// <summary>How far a point may be from the nearest same-floor area and still snap to it.</summary>
    public const double MaxSnap = 128.0;

    /// <summary>The second volume probe: a pawn stands on the floor, and some volumes start above it.</summary>
    public const double LiftZ = 32.0;

    // The per-map override table (D4). Empty rows mean the default.
    private static readonly Dictionary<string, VolumeTieRule> _tieRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_mirage"] = VolumeTieRule.SmallestVolume
    };

    private readonly Dictionary<long, List<int>> _areaCells = new();
    private readonly Dictionary<long, List<int>> _volumeCells = new();
    private readonly List<int> _bombsites = [];
    private readonly Dictionary<int, int[]> _adjacent = new();
    private readonly HashSet<long> _adjacentPairs = [];
    private readonly Dictionary<string, int> _placeByName = new(StringComparer.Ordinal);
    private readonly Dictionary<double, IReadOnlyList<PlaceOutline>> _outlines = new();
    private readonly Lock _outlineLock = new();
    private readonly VolumeTieRule _tieRule;

    /// <summary>Builds the resolver and its grid.</summary>
    /// <param name="zones">The effective set.</param>
    public PlaceResolver(ZoneSet zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        Zones = zones;
        _tieRule = TieRuleFor(zones.MapName);

        for (int i = 0; i < zones.Areas.Count; i++)
        {
            ZoneArea area = zones.Areas[i];
            if (area.CornerCount == 0)
            {
                continue;
            }

            Insert(_areaCells, i, area.MinX, area.MinY, area.MaxX, area.MaxY);
        }

        for (int i = 0; i < zones.Volumes.Count; i++)
        {
            ZoneVolume volume = zones.Volumes[i];
            if (!volume.HasGeometry)
            {
                continue;
            }

            switch (volume.Kind)
            {
                case ZoneVolumeKind.Place when volume.PlaceId >= 0:
                    Insert(_volumeCells, i, volume.Min.X, volume.Min.Y, volume.Max.X, volume.Max.Y);
                    break;
                case ZoneVolumeKind.Bombsite when volume.Site is not null:
                    _bombsites.Add(i);
                    break;
            }
        }

        Dictionary<int, SortedSet<int>> adjacent = new();
        foreach ((int a, int b) in zones.Adjacency)
        {
            if (a == b)
            {
                continue;
            }

            Neighbours(adjacent, a).Add(b);
            Neighbours(adjacent, b).Add(a);
            _adjacentPairs.Add(Pair(a, b));
        }

        foreach ((int place, SortedSet<int> set) in adjacent)
        {
            _adjacent[place] = [.. set];
        }

        // First writer wins: the overlay never produces two live places with one name, and a synthetic
        // set that does gets the lower id, deterministically.
        foreach (ZonePlace place in zones.Places)
        {
            _placeByName.TryAdd(place.Name, place.Id);
        }
    }

    /// <summary>The set this resolver answers over.</summary>
    public ZoneSet Zones { get; }

    /// <summary>
    ///     How two overlapping baked place volumes are broken for a map: decision D4's default is the
    ///     first in entity-lump order, with a per-map override where the validation run showed another
    ///     rule winning. Custom volumes always come first regardless.
    /// </summary>
    /// <param name="mapName">The map, e.g. <c>de_mirage</c>.</param>
    public static VolumeTieRule TieRuleFor(string? mapName) =>
        mapName is not null && _tieRules.TryGetValue(mapName, out VolumeTieRule rule) ? rule : VolumeTieRule.FirstInLump;

    /// <summary>
    ///     The place at a world point: a place volume at the point or 32 units above it, else the nearest
    ///     area on the point's floor within <see cref="MaxSnap" />, else <see cref="PlaceHit.None" />.
    /// </summary>
    /// <param name="world">World position.</param>
    public PlaceHit Resolve(Vector3 world)
    {
        double x = world.X, y = world.Y, z = world.Z;

        if (VolumeAt(x, y, z, LiftZ) is { } hit)
        {
            return hit;
        }

        return NearestArea(x, y, FloorKeyFor(z), z);
    }

    /// <summary>
    ///     The place under a click on a 2D pane: volumes whose Z range meets the floor's band, then the
    ///     nearest area of that floor only. Areas of another floor never answer.
    /// </summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="floorKey">The pane's floor key (a level lower bound, <c>MapSpace.QuantizeZ(band.MinZ)</c>).</param>
    public PlaceHit ResolveOnFloor(double x, double y, double floorKey)
    {
        if (BandFor(floorKey) is { } band)
        {
            List<int>? candidates = CellList(_volumeCells, x, y);
            if (candidates is not null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    ZoneVolume volume = Zones.Volumes[candidates[i]];
                    if (!volume.MeetsBand(band.MinZ, band.MaxZ))
                    {
                        continue;
                    }

                    // A click has no Z, so the hull is probed mid-way through the part of it that lies
                    // in this band; place volumes are vertical prisms, so any Z in the overlap answers.
                    double lo = Math.Max(volume.Min.Z, band.MinZ);
                    double hi = Math.Min(volume.Max.Z, band.MaxZ);
                    if (volume.Contains(x, y, (lo + hi) / 2))
                    {
                        return Hit(volume.PlaceId, PlaceHitKind.Volume, 0);
                    }
                }
            }
        }

        return NearestArea(x, y, floorKey, null);
    }

    /// <summary>The bombsite whose volume contains the point, or null. Volume test only; nothing snaps.</summary>
    /// <param name="world">World position.</param>
    public Bombsite? BombsiteAt(Vector3 world)
    {
        for (int i = 0; i < _bombsites.Count; i++)
        {
            ZoneVolume volume = Zones.Volumes[_bombsites[i]];
            if (volume.Contains(in world))
            {
                return volume.Site;
            }
        }

        return null;
    }

    /// <summary>Whether the point is inside the named bombsite's volume.</summary>
    /// <param name="world">World position.</param>
    /// <param name="site">The site.</param>
    public bool IsInside(Vector3 world, Bombsite site)
    {
        for (int i = 0; i < _bombsites.Count; i++)
        {
            ZoneVolume volume = Zones.Volumes[_bombsites[i]];
            if (volume.Site == site && volume.Contains(in world))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The floor key for a world Z: the key of the band containing it, else the nearest band by
    ///     centre (the outer bands reach ±100 000, so only a set with no floors falls through to the
    ///     quantized Z itself).
    /// </summary>
    /// <param name="z">World Z.</param>
    public double FloorKeyFor(double z)
    {
        IReadOnlyList<ZoneFloor> floors = Zones.Floors;
        for (int i = 0; i < floors.Count; i++)
        {
            if (floors[i].Contains(z))
            {
                return floors[i].Key;
            }
        }

        if (floors.Count == 0)
        {
            return MapSpace.QuantizeZ(z);
        }

        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < floors.Count; i++)
        {
            double d = Math.Abs(z - (floors[i].MinZ + floors[i].MaxZ) / 2);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        return floors[nearest].Key;
    }

    /// <summary>The places sharing a nav connection with this one, ascending. Empty for an unknown id.</summary>
    /// <param name="placeId">A place id.</param>
    public IReadOnlyList<int> Adjacent(int placeId) =>
        _adjacent.TryGetValue(placeId, out int[]? list) ? list : [];

    /// <summary>Whether two places share a nav connection. Symmetric; a place is not adjacent to itself.</summary>
    public bool AreAdjacent(int a, int b) => a != b && _adjacentPairs.Contains(Pair(a, b));

    /// <summary>The id of a place by its raw name, ordinal. Null when the vocabulary has no such name.</summary>
    /// <param name="name">The raw name.</param>
    public int? PlaceId(string name) =>
        name is not null && _placeByName.TryGetValue(name, out int id) ? id : null;

    /// <summary>
    ///     The outlines of every place on one floor, built once per floor and cached. Boundary edges are
    ///     area polygon edges with no <c>areaLinks</c> partner of the same place across them, so no
    ///     polygon union is computed anywhere; a custom zone with a polygon draws that polygon.
    /// </summary>
    /// <param name="floorKey">The floor key.</param>
    public IReadOnlyList<PlaceOutline> OutlinesFor(double floorKey)
    {
        lock (_outlineLock)
        {
            if (_outlines.TryGetValue(floorKey, out IReadOnlyList<PlaceOutline>? cached))
            {
                return cached;
            }

            IReadOnlyList<PlaceOutline> built = ZoneOutlineBuilder.Build(Zones, floorKey);
            _outlines[floorKey] = built;
            return built;
        }
    }

    private PlaceHit? VolumeAt(double x, double y, double z, double lift)
    {
        List<int>? candidates = CellList(_volumeCells, x, y);
        if (candidates is null)
        {
            return null;
        }

        int best = -1;
        double bestSize = double.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            ZoneVolume volume = Zones.Volumes[candidates[i]];
            if (!volume.Contains(x, y, z) && !volume.Contains(x, y, z + lift))
            {
                continue;
            }

            // A custom volume, or the default rule: the first hit in cascade order answers.
            if (volume.Origin == PlaceOrigin.Custom || _tieRule == VolumeTieRule.FirstInLump)
            {
                return Hit(volume.PlaceId, PlaceHitKind.Volume, 0);
            }

            double size = (volume.Max.X - volume.Min.X) * (volume.Max.Y - volume.Min.Y) * (volume.Max.Z - volume.Min.Z);
            if (size < bestSize)
            {
                bestSize = size;
                best = candidates[i];
            }
        }

        return best < 0 ? null : Hit(Zones.Volumes[best].PlaceId, PlaceHitKind.Volume, 0);
    }

    // Nearest by XY distance to the area polygon; among areas at the same distance (stacked geometry
    // on one band, a tunnel under a walkway), the one whose mean Z is nearest the point's; then the
    // lower area id, so two enumerations of the grid agree. A floor-keyed click has no Z to offer.
    private PlaceHit NearestArea(double x, double y, double floorKey, double? z)
    {
        int cx0 = Cell(x - MaxSnap), cx1 = Cell(x + MaxSnap);
        int cy0 = Cell(y - MaxSnap), cy1 = Cell(y + MaxSnap);

        int bestIndex = -1;
        double best = double.PositiveInfinity;
        double bestDz = double.PositiveInfinity;

        for (int cx = cx0; cx <= cx1; cx++)
        {
            for (int cy = cy0; cy <= cy1; cy++)
            {
                if (!_areaCells.TryGetValue(Key(cx, cy), out List<int>? list))
                {
                    continue;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    ZoneArea area = Zones.Areas[list[i]];
                    if (area.PlaceId < 0 || !area.FloorKey.Equals(floorKey))
                    {
                        continue;
                    }

                    // Box distance first: it is a lower bound on the polygon distance and rejects most
                    // of a cell without touching the corners.
                    double dx = Math.Max(0, Math.Max(area.MinX - x, x - area.MaxX));
                    double dy = Math.Max(0, Math.Max(area.MinY - y, y - area.MaxY));
                    if (dx > MaxSnap || dy > MaxSnap || dx * dx + dy * dy > best * best)
                    {
                        continue;
                    }

                    double d = area.DistanceXy(x, y);
                    if (d > MaxSnap)
                    {
                        continue;
                    }

                    double dz = z is { } worldZ ? Math.Abs(area.Z - worldZ) : 0;
                    if (d < best - 1e-6 ||
                        (Math.Abs(d - best) <= 1e-6 &&
                         (dz < bestDz - 1e-6 ||
                          (Math.Abs(dz - bestDz) <= 1e-6 && bestIndex >= 0 && area.Id < Zones.Areas[bestIndex].Id))))
                    {
                        best = d;
                        bestDz = dz;
                        bestIndex = list[i];
                    }
                }
            }
        }

        return bestIndex < 0
            ? PlaceHit.None
            : Hit(Zones.Areas[bestIndex].PlaceId, PlaceHitKind.NearestArea, best);
    }

    private PlaceHit Hit(int placeId, PlaceHitKind kind, double distance) =>
        new(placeId, Zones.PlaceName(placeId), kind, distance);

    private ZoneFloor? BandFor(double floorKey)
    {
        IReadOnlyList<ZoneFloor> floors = Zones.Floors;
        for (int i = 0; i < floors.Count; i++)
        {
            if (floors[i].Key.Equals(floorKey))
            {
                return floors[i];
            }
        }

        return null;
    }

    private static void Insert(Dictionary<long, List<int>> cells, int index, double minX, double minY,
        double maxX, double maxY)
    {
        int cx0 = Cell(minX), cx1 = Cell(maxX);
        int cy0 = Cell(minY), cy1 = Cell(maxY);
        for (int cx = cx0; cx <= cx1; cx++)
        {
            for (int cy = cy0; cy <= cy1; cy++)
            {
                long key = Key(cx, cy);
                if (!cells.TryGetValue(key, out List<int>? list))
                {
                    list = [];
                    cells[key] = list;
                }

                list.Add(index);
            }
        }
    }

    private static List<int>? CellList(Dictionary<long, List<int>> cells, double x, double y) =>
        cells.TryGetValue(Key(Cell(x), Cell(y)), out List<int>? list) ? list : null;

    private static int Cell(double v) => (int)Math.Floor(v / GridCell);

    private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    private static long Pair(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static SortedSet<int> Neighbours(Dictionary<int, SortedSet<int>> map, int place)
    {
        if (!map.TryGetValue(place, out SortedSet<int>? set))
        {
            set = [];
            map[place] = set;
        }

        return set;
    }
}

/// <summary>Decision D4: which of two overlapping baked place volumes claims a point.</summary>
public enum VolumeTieRule
{
    /// <summary>The first volume in entity-lump order. The default.</summary>
    FirstInLump,

    /// <summary>The volume with the smallest bounding box: the more specific place.</summary>
    SmallestVolume
}
