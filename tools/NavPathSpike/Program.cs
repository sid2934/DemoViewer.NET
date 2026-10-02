using System.Diagnostics;
using System.Globalization;
using System.Text;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

// Usage: NavPathSpike <assetsDir> <outDir>
// Measures the nav pathfinder on a few real moves and writes, per map, an ink file (straight line red, route green)
// and a scene to render it on with dv2d.
string assets = args.Length > 0 ? args[0] : "assets";
string outDir = args.Length > 1 ? args[1] : "/tmp/navspike";
double maxClimb = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 64;
Directory.CreateDirectory(outDir);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

(string Map, string Name, string[] Places)[] routes =
[
    ("de_dust2", "T spawn to Long Doors", ["TSpawn", "LongDoors"]),
    ("de_dust2", "T spawn to B via upper tunnel", ["TSpawn", "UpperTunnel", "BombsiteB"]),
    ("de_dust2", "T spawn to B (free)", ["TSpawn", "BombsiteB"]),
    ("de_dust2", "CT spawn to Long A", ["CTSpawn", "LongA"]),
    ("de_mirage", "T spawn to Jungle", ["TSpawn", "Jungle"]),
    ("de_mirage", "Top of mid to Underpass", ["TopofMid", "Underpass"]),
    ("de_mirage", "T spawn to B (free)", ["TSpawn", "BombsiteB"]),
    ("de_mirage", "Middle to Window, no climb", ["Middle", "SnipersNest"]),
    ("de_nuke", "Outside to B site via Ramp", ["Outside", "Ramp", "BombsiteB"]),
    ("de_nuke", "T spawn to A site", ["TSpawn", "BombsiteA"])
];

Console.WriteLine("map        build ms  alloc KiB  retained KiB  areas  links  gapLinks");
Dictionary<string, NavPathfinder> graphs = [];
Dictionary<string, ZoneSet> sets = [];
foreach (string map in routes.Select(r => r.Map).Distinct())
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(assets, map, "zones.json"));
    ZoneSet zones = ZoneSetReader.Read(bytes);
    sets[map] = zones;

    // Warm the JIT once, then time a cold-data build.
    NavPathfinder.Build(zones, maxClimb);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long before = GC.GetTotalMemory(true);
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    Stopwatch sw = Stopwatch.StartNew();
    NavPathfinder nav = NavPathfinder.Build(zones, maxClimb);
    sw.Stop();
    long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    long retained = GC.GetTotalMemory(true) - before;
    graphs[map] = nav;
    Console.WriteLine($"{map,-10} {sw.Elapsed.TotalMilliseconds,8:F2}  {alloc / 1024,9}  {retained / 1024,12}  {nav.AreaCount,5}  {nav.LinkCount,5}  {nav.GapLinkCount,8}");
}

Console.WriteLine();
Console.WriteLine("route                                    straight  pathed  ratio  corners  offMesh straight/pathed  query us p50/p99  alloc B/query");
Dictionary<string, List<string>> inks = [];
foreach ((string map, string name, string[] places) in routes)
{
    ZoneSet zones = sets[map];
    NavPathfinder nav = graphs[map];
    List<(double X, double Y, int Area)> stops = [.. places.Select(p => Anchor(zones, nav, p))];

    List<NavWaypoint> route = [];
    for (int i = 1; i < stops.Count; i++)
    {
        IReadOnlyList<NavWaypoint>? leg = nav.FindPath(stops[i - 1].X, stops[i - 1].Y, stops[i - 1].Area, stops[i].X, stops[i].Y, stops[i].Area);
        if (leg is null)
        {
            Console.WriteLine($"{name}: no route on leg {i}");
            route.Clear();
            break;
        }

        route.AddRange(i == 1 ? leg : leg.Skip(1));
    }

    if (route.Count == 0)
    {
        continue;
    }

    Console.Error.WriteLine($"{name}: {stops[0].X:F0},{stops[0].Y:F0} -> {stops[^1].X:F0},{stops[^1].Y:F0}");
    double straight = Math.Sqrt(Sq(stops[^1].X - stops[0].X) + Sq(stops[^1].Y - stops[0].Y));
    double pathed = NavPathfinder.Length(route);
    double offStraight = OffMesh(nav, [new NavWaypoint(stops[0].X, stops[0].Y, 0), new NavWaypoint(stops[^1].X, stops[^1].Y, 0)]);
    double offPathed = OffMesh(nav, route);

    // Query cost: locate both ends by place and route, as the projection would per run.
    double[] samples = new double[1000];
    (double X, double Y, int Area) a = stops[0], b = stops[^1];
    int placeA = PlaceId(zones, places[0]), placeB = PlaceId(zones, places[^1]);
    // Long enough for tiered JIT to promote the hot loop; a short warmup measured tier 0 on the first route.
    Stopwatch warm = Stopwatch.StartNew();
    while (warm.ElapsedMilliseconds < 500)
    {
        Query();
    }

    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < samples.Length; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        Query();
        samples[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
    }

    long allocPer = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / samples.Length;
    Array.Sort(samples);
    Console.WriteLine($"{map[3..] + ": " + name,-40} {straight,8:F0}  {pathed,6:F0}  {pathed / straight,5:F2}  {route.Count - 2,7}  {offStraight,9:P0}/{offPathed,-12:P0}  {samples[500],7:F1}/{samples[990],-8:F1}  {allocPer,8}");

    if (!inks.TryGetValue(map, out List<string>? elements))
    {
        inks[map] = elements = [];
    }

    elements.Add(Stroke(elements.Count, 0xFFFF4040u, 14, route[0].FloorKey, (stops[0].X, stops[0].Y), (stops[^1].X, stops[^1].Y)));
    for (int i = 1; i < route.Count; i++)
    {
        elements.Add(Stroke(elements.Count, 0xFF30E060u, 18, route[i].FloorKey, (route[i - 1].X, route[i - 1].Y), (route[i].X, route[i].Y)));
    }

    void Query()
    {
        int sa = nav.Locate(a.X, a.Y, placeId: placeA);
        int sb = nav.Locate(b.X, b.Y, placeId: placeB);
        nav.FindPath(a.X, a.Y, sa, b.X, b.Y, sb);
    }
}

foreach ((string map, List<string> elements) in inks)
{
    File.WriteAllText(Path.Combine(outDir, $"{map}.dvann.json"),
        "{\n  \"schemaVersion\": 1,\n  \"demo\": { \"fileName\": \"" + map + ".scene.json\", \"sizeBytes\": 0 },\n" +
        "  \"clock\": { \"kind\": \"dv-frame-clock\", \"tickRate\": 64, \"frameCount\": 0, \"firstTick\": 0, \"lastTick\": 0 },\n" +
        "  \"elements\": [\n" + string.Join(",\n", elements) + "\n  ]\n}\n");
}

return 0;

static double Sq(double v) => v * v;

static int PlaceId(ZoneSet zones, string name) => zones.Places.First(p => p.Name == name).Id;

// The place's area-weighted centroid, snapped into one of its own areas.
static (double X, double Y, int Area) Anchor(ZoneSet zones, NavPathfinder nav, string place)
{
    int id = PlaceId(zones, place);
    List<ZoneArea> mine = [.. zones.Areas.Where(a => a.PlaceId == id)];
    double w = mine.Sum(a => a.Area);
    double x = mine.Sum(a => a.CentroidX * a.Area) / w, y = mine.Sum(a => a.CentroidY * a.Area) / w;
    int area = nav.Locate(x, y, placeId: id, snap: 4096);
    ZoneArea at = nav.AreaAt(area);
    return at.ContainsXy(x, y) ? (x, y, area) : (at.CentroidX, at.CentroidY, area);
}

// Share of points, every 8 units along the line, more than 8 units from any nav area on any floor.
static double OffMesh(NavPathfinder nav, IReadOnlyList<NavWaypoint> line)
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

static string Stroke(int index, uint argb, double width, double levelMinZ, params (double X, double Y)[] points)
{
    StringBuilder sb = new();
    sb.Append(CultureInfo.InvariantCulture,
        $"    {{ \"id\": \"{new Guid(index + 1, (short)0x4111, unchecked((short)0x8111), 0, 0, 0, 0, 0, 0, 0, 0)}\", \"kind\": \"Line\", \"colorArgb\": {argb}, \"widthWorld\": {width}, ");
    sb.Append(CultureInfo.InvariantCulture,
        $"\"opacity\": 0.9, \"revealOnFadeIn\": false, \"space\": \"world\", \"levelMinZ\": {levelMinZ}, \"steamId\": 0, \"dx\": 0, \"dy\": 0, ");
    sb.Append("\"fadeInTicks\": 0, \"fadeOutTicks\": 0, \"points\": [");
    sb.Append(string.Join(", ", points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:F1}, {p.Y:F1}, 0.6"))));
    sb.Append("] }");
    return sb.ToString();
}
