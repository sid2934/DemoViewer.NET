#region

using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The nav pathfinder over the committed <c>zones.json</c> bakes (docs/strat-book/token-pathing.md): every map
///     builds, known routes stay on the mesh and go round walls, the climb rule keeps mirage's window a drop, and a
///     route across nuke's floors changes key where it crosses. Reads committed assets only; skipped without them.
/// </summary>
public class NavPathfinderTests
{
    private static readonly string[] Maps =
        ["de_ancient", "de_anubis", "de_cache", "de_dust2", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_train", "de_vertigo"];

    [Test]
    public async Task EveryShippedMap_Builds_AndRoutesBetweenItsPlaces()
    {
        int maps = 0;
        foreach (string map in Maps)
        {
            if (Zones(map) is not { } zones)
            {
                continue;
            }

            maps++;
            NavPathfinder nav = NavPathfinder.Build(zones);
            await Assert.That(nav.AreaCount).IsEqualTo(zones.Areas.Count);
            await Assert.That(nav.LinkCount).IsGreaterThan(zones.AreaLinks.Count);

            // Every place to the next in the file: almost all join, since each map is one component bar a few islands.
            List<ZonePlace> places = [.. zones.Places.Where(p => zones.Areas.Any(a => a.PlaceId == p.Id))];
            int routed = 0, tried = 0;
            for (int i = 1; i < places.Count; i++)
            {
                (double X, double Y, int Area) a = Anchor(zones, nav, places[i - 1].Name);
                (double X, double Y, int Area) b = Anchor(zones, nav, places[i].Name);
                tried++;
                if (nav.FindPath(a.X, a.Y, a.Area, b.X, b.Y, b.Area) is { } path)
                {
                    routed++;
                    await Assert.That(path[0].X).IsEqualTo(a.X);
                    await Assert.That(path[^1].Y).IsEqualTo(b.Y);
                }
            }

            Console.WriteLine($"[nav] {map}: {nav.AreaCount} areas, {nav.LinkCount} links, {nav.GapLinkCount} gaps, {routed}/{tried} routed");
            await Assert.That(routed).IsGreaterThanOrEqualTo(tried * 9 / 10);
        }

        if (maps == 0)
        {
            throw new SkipTestException("no zones.json bakes in this checkout");
        }

        await Assert.That(maps).IsEqualTo(Maps.Length);
    }

    [Test]
    [Arguments("de_dust2", "TSpawn", "LongDoors", 1.15)]
    [Arguments("de_dust2", "TSpawn", "BombsiteB", 1.15)]
    [Arguments("de_dust2", "CTSpawn", "LongA", 1.05)]
    [Arguments("de_mirage", "TSpawn", "Jungle", 1.2)]
    [Arguments("de_mirage", "Middle", "SnipersNest", 1.8)]
    [Arguments("de_nuke", "TSpawn", "BombsiteA", 1.1)]
    public async Task KnownRoute_StaysOnTheMesh_AndGoesRound(string map, string from, string to, double minRatio)
    {
        if (Zones(map) is not { } zones)
        {
            throw new SkipTestException($"no {map} zones in this checkout");
        }

        NavPathfinder nav = NavPathfinder.Build(zones);
        (double X, double Y, int Area) a = Anchor(zones, nav, from);
        (double X, double Y, int Area) b = Anchor(zones, nav, to);
        IReadOnlyList<NavWaypoint>? path = nav.FindPath(a.X, a.Y, a.Area, b.X, b.Y, b.Area);
        await Assert.That(path).IsNotNull();

        double straight = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        double routed = NavPathfinder.Length(path!);
        double off = OffMesh(nav, path!);
        Console.WriteLine($"[nav] {map} {from} -> {to}: straight {straight:F0}, routed {routed:F0}, off-mesh {off:P1}");

        // Window from Middle has to go round through Connector and Jungle: the climb rule refuses the ledge.
        await Assert.That(routed / straight).IsGreaterThanOrEqualTo(minRatio);
        await Assert.That(off).IsLessThanOrEqualTo(0.04);
        await Assert.That(OffMesh(nav, [path![0], path[^1]])).IsGreaterThan(off);
    }

    [Test]
    public async Task NukeOutsideToBViaRamp_ChangesFloorWhereItCrosses()
    {
        if (Zones("de_nuke") is not { } zones)
        {
            throw new SkipTestException("no de_nuke zones in this checkout");
        }

        NavPathfinder nav = NavPathfinder.Build(zones);
        (double X, double Y, int Area) outside = Anchor(zones, nav, "Outside");
        (double X, double Y, int Area) ramp = Anchor(zones, nav, "Ramp");
        (double X, double Y, int Area) site = Anchor(zones, nav, "BombsiteB");
        List<NavWaypoint> path = [.. nav.FindPath(outside.X, outside.Y, outside.Area, ramp.X, ramp.Y, ramp.Area)!];
        path.AddRange(nav.FindPath(ramp.X, ramp.Y, ramp.Area, site.X, site.Y, site.Area)!.Skip(1));

        await Assert.That(path[0].FloorKey).IsEqualTo(nav.AreaAt(outside.Area).FloorKey);
        await Assert.That(path[^1].FloorKey).IsEqualTo(nav.AreaAt(site.Area).FloorKey);
        await Assert.That(path[0].FloorKey).IsNotEqualTo(path[^1].FloorKey);

        // Each change of key is at a point of its own: just before it the route is on the old floor's mesh, just
        // after it on the new one's.
        int changes = 0;
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i].FloorKey == path[i - 1].FloorKey)
            {
                continue;
            }

            changes++;
            (double bx, double by) = Toward(path[i], path[i - 1], 6);
            await Assert.That(nav.Locate(bx, by, path[i - 1].FloorKey, snap: 12)).IsGreaterThanOrEqualTo(0);
            if (i + 1 < path.Count)
            {
                (double ax, double ay) = Toward(path[i], path[i + 1], 6);
                await Assert.That(nav.Locate(ax, ay, path[i].FloorKey, snap: 12)).IsGreaterThanOrEqualTo(0);
            }
        }

        Console.WriteLine($"[nav] nuke Outside -> Ramp -> B: {path.Count} points, {changes} floor changes, {NavPathfinder.Length(path):F0} u");
        await Assert.That(changes).IsGreaterThanOrEqualTo(1);
        await Assert.That(OffMesh(nav, path)).IsLessThanOrEqualTo(0.04);
    }

    [Test]
    public async Task Island_And_OffMesh_ReturnNull()
    {
        ZoneSet zones = ZoneFixtures.Build();
        NavPathfinder nav = NavPathfinder.Build(zones);
        int island = Index(zones, 6);
        int ramp = Index(zones, 1);
        ZoneArea a = zones.Areas[ramp], b = zones.Areas[island];

        await Assert.That(nav.FindPath(a.CentroidX, a.CentroidY, ramp, b.CentroidX, b.CentroidY, island)).IsNull();
        await Assert.That(nav.FindPath(a.CentroidX, a.CentroidY, -1, b.CentroidX, b.CentroidY, ramp)).IsNull();
        await Assert.That(nav.Locate(1e6, 1e6)).IsEqualTo(-1);
        await Assert.That(nav.Snap(1e6, 1e6)).IsNull();
    }

    [Test]
    public async Task Snap_KeepsAPointOnTheMesh_AndPullsAnOffMeshOneOnto_It()
    {
        ZoneSet zones = ZoneFixtures.Build();
        NavPathfinder nav = NavPathfinder.Build(zones);
        ZoneArea area = zones.Areas[Index(zones, 1)];

        NavWaypoint inside = nav.Snap(area.CentroidX, area.CentroidY, area.FloorKey)!.Value;
        await Assert.That(inside.X).IsEqualTo(area.CentroidX);
        await Assert.That(inside.FloorKey).IsEqualTo(area.FloorKey);

        NavWaypoint pulled = nav.Snap(area.MinX - 40, area.CentroidY, area.FloorKey)!.Value;
        await Assert.That(nav.Locate(pulled.X, pulled.Y, area.FloorKey, snap: 0)).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task RepeatedQuery_IsAnsweredFromMemory_WithoutAllocating()
    {
        if (Zones("de_dust2") is not { } zones)
        {
            throw new SkipTestException("no de_dust2 zones in this checkout");
        }

        NavPathfinder nav = NavPathfinder.Build(zones);
        (double X, double Y, int Area) a = Anchor(zones, nav, "TSpawn");
        (double X, double Y, int Area) b = Anchor(zones, nav, "BombsiteB");
        IReadOnlyList<NavWaypoint>? first = nav.FindPath(a.X, a.Y, a.Area, b.X, b.Y, b.Area);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<NavWaypoint>? again = nav.FindPath(a.X, a.Y, a.Area, b.X, b.Y, b.Area);
        long hit = GC.GetAllocatedBytesForCurrentThread() - before;

        // A miss on warm scratch allocates its answer and the memo's slot, nothing per area.
        nav.FindPath(a.X + 1, a.Y, a.Area, b.X, b.Y, b.Area);
        before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<NavWaypoint>? miss = nav.FindPath(a.X + 2, a.Y, a.Area, b.X, b.Y, b.Area);
        long missBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[nav] memo hit {hit} B, warm miss {missBytes} B for {miss!.Count} points");

        await Assert.That(ReferenceEquals(first, again)).IsTrue();
        await Assert.That(hit).IsEqualTo(0);
        await Assert.That(missBytes).IsLessThan(4096);
    }

    internal static ZoneSet? Zones(string map)
    {
        using LoadedMapAsset? asset = MapAssetPipeline.TryLoad(map);
        return asset is null ? null : ZoneAssetPipeline.TryReadBaked(asset.BakedDir);
    }

    // The place's area-weighted centroid, snapped into one of its own areas.
    internal static (double X, double Y, int Area) Anchor(ZoneSet zones, NavPathfinder nav, string place)
    {
        int id = zones.Places.First(p => p.Name == place).Id;
        List<ZoneArea> mine = [.. zones.Areas.Where(a => a.PlaceId == id)];
        double w = mine.Sum(a => a.Area);
        double x = mine.Sum(a => a.CentroidX * a.Area) / w, y = mine.Sum(a => a.CentroidY * a.Area) / w;
        int area = nav.Locate(x, y, placeId: id, snap: 4096);
        ZoneArea at = nav.AreaAt(area);
        return at.ContainsXy(x, y) ? (x, y, area) : (at.CentroidX, at.CentroidY, area);
    }

    // Share of points, every 8 units along the line, more than 8 units from any nav area on any floor.
    internal static double OffMesh(NavPathfinder nav, IReadOnlyList<NavWaypoint> line)
    {
        int total = 0, off = 0;
        for (int i = 1; i < line.Count; i++)
        {
            double dx = line[i].X - line[i - 1].X, dy = line[i].Y - line[i - 1].Y;
            int n = Math.Max(1, (int)(Math.Sqrt(dx * dx + dy * dy) / 8));
            for (int s = 0; s < n; s++)
            {
                double t = s / (double)n;
                total++;
                if (nav.Locate(line[i - 1].X + dx * t, line[i - 1].Y + dy * t, snap: 8) < 0)
                {
                    off++;
                }
            }
        }

        return total == 0 ? 0 : off / (double)total;
    }

    private static (double X, double Y) Toward(NavWaypoint from, NavWaypoint to, double distance)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? (from.X, from.Y) : (from.X + dx / len * Math.Min(distance, len / 2), from.Y + dy / len * Math.Min(distance, len / 2));
    }

    private static int Index(ZoneSet zones, int areaId)
    {
        for (int i = 0; i < zones.Areas.Count; i++)
        {
            if (zones.Areas[i].Id == areaId)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"no area {areaId}");
    }
}
