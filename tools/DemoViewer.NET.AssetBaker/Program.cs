#region

using System.IO.Hashing;
using DemoViewer.NET.AssetBaker;
using SkiaSharp;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

#endregion

const int SchemaVersion = 1;
const string BakerVersion = "0.4+vrf19.2.6339";

// ── args: <map> [<map>...] [--diag] ──
// Modes that stop early and need no staged cs2-assets/ cache: --list-icons, --icons, --collision, --zones.
// With no map args, bake the full shipping set (the Active Duty / commonly-demoed pool). Each
// needs a source vpk (cs2-assets/maps/), a radar vtex_c, and an overview txt, all present in the
// gitignored cs2-assets/ cache. Pass explicit map names to bake a subset.
bool diag = args.Contains("--diag");

// ── --list-icons: what this CS2 build ships, and what the curation already takes ──
// The discovery half of adding an icon. Without it, finding a source path means dumping the archive
// by hand; with it the exact string to paste into IconSet is on screen, already marked [x] if it is
// spoken for. Filter with --list-icons=<substring>; narrow with --free or --taken.
if (args.Any(a => a.StartsWith("--list-icons", StringComparison.Ordinal)))
{
    string? listCs2 = args
        .FirstOrDefault(a => a.StartsWith("--cs2=", StringComparison.Ordinal))?["--cs2=".Length..];

    string listPak = SteamLibrary.FindCs2Pak(listCs2)
                     ?? throw new DirectoryNotFoundException(
                         "Counter-Strike 2 not found. Pass --cs2=<path to pak01_dir.vpk or install root>.");

    string listFilter = args
        .FirstOrDefault(a => a.StartsWith("--list-icons=", StringComparison.Ordinal))
        ?["--list-icons=".Length..] ?? "";

    Console.WriteLine(Icons.List(listPak, listFilter,
        takenOnly: args.Contains("--taken"), freeOnly: args.Contains("--free")));
    return;
}

// ── --icons: bake the weapon/HUD iconography and stop ──
// A separate mode, not a step of the map bake, because it shares nothing with one: it reads a single
// archive (game/csgo/pak01_dir.vpk) straight out of the CS2 install and needs no staged cs2-assets/
// cache at all. Pass --cs2=<path> to point at a vpk or an install root Steam discovery cannot find.
if (args.Contains("--icons"))
{
    string? explicitCs2 = args
        .FirstOrDefault(a => a.StartsWith("--cs2=", StringComparison.Ordinal))?["--cs2=".Length..];

    string pak = SteamLibrary.FindCs2Pak(explicitCs2)
                 ?? throw new DirectoryNotFoundException(
                     "Counter-Strike 2 not found. Pass --cs2=<path to pak01_dir.vpk or install root>.");

    string iconsOut = Path.Combine(FindRepoRoot(), "assets", "icons");
    Console.WriteLine($"cs2 pak:  {pak}");
    Console.WriteLine($"icons ->  {iconsOut}");
    Console.WriteLine();
    Console.WriteLine(Icons.Bake(pak, iconsOut, BakerVersion));
    return;
}

// -- --collision: bake ONLY the line-of-sight triangle soups and stop --
// A separate mode for the same reason --icons is one: it needs no staged cs2-assets/ cache. The soups
// come straight out of the per-map vpks in the CS2 install, so a map whose 2D assets were baked long
// ago can be given line-of-sight support without re-staging or re-baking anything 2D. That is not a
// hypothetical: the committed bundles were all written by baker 0.1, which had no collision step, and
// four of the nine shipped maps went years without a soup as a result. With no map args this tops up
// every map already under assets/, which is idempotent because an unchanged vpk re-extracts to the
// same bytes.
//
// It deliberately does NOT touch bundle.json. The bundle's collision field feeds a version CRC taken
// over the radar and overview SOURCE bytes, which only a full bake has in hand, so writing one here
// would mean either a wrong CRC or a re-stage. Nothing reads that field in any case: the app finds a
// soup by the assets/<map>/collision.tris path, which is why the five maps that do ship one work today
// with collision:null in their bundle.
if (args.Contains("--collision"))
{
    string? collisionCs2 = args
        .FirstOrDefault(a => a.StartsWith("--cs2=", StringComparison.Ordinal))?["--cs2=".Length..];

    string mapsSource = SteamLibrary.FindCs2MapsDir(collisionCs2)
                        ?? throw new DirectoryNotFoundException(
                            "Counter-Strike 2 map archives not found. Pass --cs2=<install root, pak01_dir.vpk, "
                            + "or the game/csgo/maps directory>.");

    string soupRoot = Path.Combine(FindRepoRoot(), "assets");
    string[] soupMaps = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (soupMaps.Length == 0)
    {
        // Discovered, NOT the hardcoded list the full bake defaults to further down. The two resolve
        // to the same nine maps today and they are not the same rule, because the two modes have
        // opposite jobs: a full bake CREATES a map directory, so it needs to be told which to create,
        // while this one TOPS UP directories that already exist, so it can read them off disk and
        // needs no edit when the shipped set grows. The asymmetry is worth knowing: a map added to the
        // list below and baked once is picked up here for free afterwards, but this mode alone will
        // never bake a map that has no directory yet, which is why de_train and de_dogtown still have
        // no soup.
        //
        // A bundle.json is what makes a directory a baked map rather than a stray folder.
        soupMaps = Directory.Exists(soupRoot)
            ? Directory.GetDirectories(soupRoot)
                .Where(d => File.Exists(Path.Combine(d, "bundle.json")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    Console.WriteLine($"cs2 maps:  {mapsSource}");
    Console.WriteLine($"soups ->   {soupRoot}");
    Console.WriteLine($"baking {soupMaps.Length} soup(s): {string.Join(", ", soupMaps)}\n");

    int baked = 0;
    foreach (string map in soupMaps)
    {
        string mapVpk = Path.Combine(mapsSource, map + ".vpk");
        if (!File.Exists(mapVpk))
        {
            Console.WriteLine($"  x {map}: no {map}.vpk in {mapsSource}");
            continue;
        }

        string mapOut = Path.Combine(soupRoot, map);
        Directory.CreateDirectory(mapOut);
        try
        {
            CollisionMesh.Result cm = CollisionMesh.Extract(
                mapVpk, map, Path.Combine(mapOut, "collision.tris"));
            Console.WriteLine($"# {map}");
            Console.WriteLine(cm.Diagnostic);
            baked++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  x {map}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    Console.WriteLine($"\n{baked}/{soupMaps.Length} soup(s) baked.");
    return;
}

// -- --zones: bake ONLY the place and trigger volumes joined to the nav (zones.json) and stop --
// The third top-up mode, for the reasons --collision is one: the volumes and the nav live in the
// per-map vpks in the CS2 install, so a map baked years ago by baker 0.1 gets zones without a
// re-stage. Maps are discovered by the presence of bundle.json, the floors and mapVersion are read
// back out of that bundle so the floor keys in zones.json are the ones the app already shows, and
// bundle.json itself is never written (decision D1 of the zone-baking design): the app locates
// zones.json by path, and bundleMapVersion inside it is what detects drift between the two files.
//
// With --diag, prints the per-map coverage table beside the design's baseline and the place
// adjacency list, and exits non-zero when a self-check fails: a hull whose planes satisfy neither
// sign convention, a place or bombsite volume with no hull, a de_ map without exactly two bombsites
// designated 0 and 1, or a coverage column outside tolerance. Those are the checks a test project
// would carry, run inside the bake that produces the artifacts, because every one of them needs the
// installed CS2 and the tool is outside the solution's test runner.
if (args.Contains("--zones"))
{
    string? zonesCs2 = args
        .FirstOrDefault(a => a.StartsWith("--cs2=", StringComparison.Ordinal))?["--cs2=".Length..];

    string zonesMapsSource = SteamLibrary.FindCs2MapsDir(zonesCs2)
                             ?? throw new DirectoryNotFoundException(
                                 "Counter-Strike 2 map archives not found. Pass --cs2=<install root, pak01_dir.vpk, "
                                 + "or the game/csgo/maps directory>.");

    string zonesRoot = Path.Combine(FindRepoRoot(), "assets");
    string[] zoneMaps = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (zoneMaps.Length == 0)
    {
        zoneMaps = Directory.Exists(zonesRoot)
            ? Directory.GetDirectories(zonesRoot)
                .Where(d => File.Exists(Path.Combine(d, "bundle.json")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    Console.WriteLine($"cs2 maps:  {zonesMapsSource}");
    Console.WriteLine($"zones ->   {zonesRoot}");
    Console.WriteLine($"baking zones for {zoneMaps.Length} map(s): {string.Join(", ", zoneMaps)}\n");

    List<Zones.Coverage> coverage = new();
    List<string> zoneFailures = new();
    foreach (string map in zoneMaps)
    {
        string mapVpk = Path.Combine(zonesMapsSource, map + ".vpk");
        string bundlePath = Path.Combine(zonesRoot, map, "bundle.json");
        if (!File.Exists(mapVpk))
        {
            Console.WriteLine($"  x {map}: no {map}.vpk in {zonesMapsSource}");
            continue;
        }

        if (!File.Exists(bundlePath))
        {
            Console.WriteLine($"  x {map}: no bundle.json under {zonesRoot}; run a full bake first");
            continue;
        }

        try
        {
            (IReadOnlyList<FloorBand> floors, string mapVersion) = Zones.ReadBundle(bundlePath);
            Zones.Result zr = Zones.Bake(
                mapVpk, map, floors, mapVersion, BakerVersion, Path.Combine(zonesRoot, map, "zones.json"));
            Console.WriteLine($"# {map}");
            foreach (string note in zr.Notes)
            {
                Console.WriteLine(note);
            }

            Console.WriteLine(zr.Diagnostic);
            coverage.Add(zr.Coverage);
            zoneFailures.AddRange(zr.Failures);
            if (diag)
            {
                zoneFailures.AddRange(Zones.CheckAgainstBaseline(zr.Coverage));
                Console.WriteLine("  adjacency:");
                Console.Write(zr.AdjacencyText);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  x {map}: {ex.GetType().Name}: {ex.Message}");
            zoneFailures.Add($"{map}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    Console.WriteLine($"\n{coverage.Count}/{zoneMaps.Length} zone file(s) baked.");
    if (diag)
    {
        Console.WriteLine("\ncoverage (this bake):");
        Console.Write(Zones.FormatCoverageTable(coverage));
        Console.WriteLine("\ncoverage (design baseline):");
        Console.Write(Zones.FormatCoverageTable(
            coverage.Select(c => Zones.ExpectedCoverage.GetValueOrDefault(c.Map)).OfType<Zones.Coverage>()));
    }

    if (zoneFailures.Count > 0)
    {
        Console.WriteLine($"\n{zoneFailures.Count} self-check failure(s):");
        foreach (string f in zoneFailures)
        {
            Console.WriteLine("  ! " + f);
        }

        Environment.ExitCode = 1;
    }

    return;
}

string[] maps = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (maps.Length == 0)
{
    // A map earns a place here by being bakeable AND demoed. Bakeable is a hard filter, not a
    // preference: the bake needs resource/overviews/<map>.txt and a <map>_radar_psd.vtex_c, both of
    // which live in pak01_dir.vpk, and six maps this CS2 build ships (de_boulder, de_debris,
    // de_eldorado, de_fachwerk, de_poseidon, cs_shelter) have NEITHER, in pak01 or in their own
    // vpk, so they cannot be baked from an install at all. Of the twelve that can, cs_italy and
    // cs_office are left out as casual hostage maps: italy alone would add 45 MiB of collision soup
    // for a map that does not appear in competitive demos. de_dogtown is not here because the map
    // is gone from the game entirely; only its econ map tokens survive in pak01, so a dogtown demo
    // can never get geometry and shows the board's no-data notice instead.
    maps =
    [
        "de_nuke", "de_dust2", "de_mirage", "de_inferno", "de_anubis",
        "de_ancient", "de_overpass", "de_vertigo", "de_train", "de_cache"
    ];
}

string assets = FindCs2Assets();
string radarDir = Path.Combine(assets, "radar");
string overviewDir = Path.Combine(radarDir, "overviews");
string mapsDir = Path.Combine(assets, "maps");
// Bake OUT of the gitignored raw cache (cs2-assets/) and INTO the committed, shipped assets/ dir
// (repo root, sibling of cs2-assets/). scripts/publish.sh copies assets/ next to the exe, and the
// app's MapAssetLoader + CollisionAssetLocator probe assets/<map>/, so a re-bake ships as-is.
string bakedRoot = Path.Combine(Directory.GetParent(assets)!.FullName, "assets");

Console.WriteLine($"cs2-assets (raw source): {assets}");
Console.WriteLine($"assets (baked output):  {bakedRoot}");
Console.WriteLine($"baking {maps.Length} map(s): {string.Join(", ", maps)}\n");

foreach (string map in maps)
{
    try
    {
        BakeMap(map);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ {map}: {ex.GetType().Name}: {ex.Message}\n");
    }
}

return;

void BakeMap(string map)
{
    Console.WriteLine($"■ {map}");
    string outDir = Path.Combine(bakedRoot, map);
    Directory.CreateDirectory(outDir);

    // 1. overview .txt → transform + verticalsections
    string overviewPath = Path.Combine(overviewDir, $"{map}.txt");
    OverviewTxt.Parsed ov = OverviewTxt.Parse(overviewPath);
    RadarTransform t = ov.Transform;
    Console.WriteLine($"  transform: pos=({t.PosX},{t.PosY}) scale={t.Scale} rotate={t.Rotate} zoom={t.Zoom}");

    // 2. radar vtex_c → PNG (default + optional _lower), and remember source bytes for the version hash.
    List<string> radarImages = new();
    Crc32 crc = new();

    (string vtex, string png)? def = ResolveRadar(map, false);
    (string vtex, string png)? low = ResolveRadar(map, true);

    if (def is { } d)
    {
        crc.Append(File.ReadAllBytes(d.vtex));
        DecodeVtexToPng(d.vtex, Path.Combine(outDir, d.png));
        radarImages.Add(d.png);
        Console.WriteLine($"  radar: {d.png}");
    }
    else
    {
        Console.WriteLine("  radar: (none — no *_radar_psd/tga.vtex_c; will fall back to nav footprint later)");
    }

    if (low is { } l)
    {
        crc.Append(File.ReadAllBytes(l.vtex));
        DecodeVtexToPng(l.vtex, Path.Combine(outDir, l.png));
        radarImages.Add(l.png);
        Console.WriteLine($"  radar: {l.png} (lower)");
    }

    // 3. nav → floor bands (the headline)
    string vpk = Path.Combine(mapsDir, $"{map}.vpk");
    byte[] navBytes = NavFloors.ExtractNav(vpk, map);
    crc.Append(navBytes);
    NavFloors.Result floors = NavFloors.ComputeFloors(navBytes);
    Console.WriteLine($"  floors: {floors.Floors.Count}  " +
                      string.Join(" ", floors.Floors.Select(f => $"[{f.MinZ:F0}..{f.MaxZ:F0}]")));
    if (diag)
    {
        Console.WriteLine(floors.Diagnostic);
    }

    // 4. bounds from the radar coverage (pos = upper-left world; image is ImageSize px at `scale` u/px)
    double worldSpan = t.ImageSize * t.Scale;
    WorldBounds bounds = new(
        t.PosX, MaxX: t.PosX + worldSpan,
        MinY: t.PosY - worldSpan, MaxY: t.PosY);

    // 5. radar layers (which PNG applies over which Z band) from verticalsections
    List<RadarLayer> layers = BuildRadarLayers(map, ov, radarImages);

    // 5b. world collision → triangle soup (collision.tris) for 3D line-of-sight. Optional: a map without
    //     extractable physics still bakes its 2D assets. See the design notes in git history.
    CollisionMeshRef? collision = null;
    const string TrisName = "collision.tris";
    try
    {
        CollisionMesh.Result cm = CollisionMesh.Extract(vpk, map, Path.Combine(outDir, TrisName));
        crc.Append(File.ReadAllBytes(Path.Combine(outDir, TrisName)));
        // The CRC above is over the UNCOMPRESSED bytes on purpose: mapVersion identifies the geometry
        // and not the container it ships in, so compressing must not restamp every bundle and trip the
        // golden stale-assets guard. That is why this runs after the CRC rather than inside Extract.
        string gzName = TrisName + ".gz";
        long gzLen = CollisionMesh.CompressAndReplace(
            Path.Combine(outDir, TrisName), Path.Combine(outDir, gzName));
        collision = new CollisionMeshRef(
            gzName, cm.TriangleCount,
            cm.Min.X, cm.Min.Y, cm.Min.Z, cm.Max.X, cm.Max.Y, cm.Max.Z);
        Console.WriteLine($"{cm.Diagnostic}  gz {gzLen / 1024.0 / 1024.0:F1} MiB");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  collision: (skipped — {ex.GetType().Name}: {ex.Message})");

        // Take the partial output with it. A plain .tris left behind by a compression that threw
        // outlives the failure: the bundle written below says there is no mesh, so Playback2D draws
        // no cones, while the Stats board resolves through CollisionSoup, which probes the plain
        // name FIRST and hands it a soup. That split between the two surfaces is the thing the
        // gzip migration set out to remove. A half-written .gz goes too, since it inflates to a
        // truncation error rather than to an honest absence.
        try
        {
            File.Delete(Path.Combine(outDir, TrisName));
            File.Delete(Path.Combine(outDir, TrisName + ".gz"));
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  collision: partial output left in {outDir} — {cleanup.Message}");
        }
    }

    // 6. version = CRC32 over source bytes (radar + nav + collision) + overview txt
    crc.Append(File.ReadAllBytes(overviewPath));
    string mapVersion = Convert.ToHexString(crc.GetCurrentHash()).ToLowerInvariant();

    AssetBundle bundle = new(
        SchemaVersion, map, mapVersion, BakerVersion,
        t, bounds, floors.Floors, layers, radarImages, collision);

    string bundlePath = Path.Combine(outDir, "bundle.json");
    File.WriteAllText(bundlePath, bundle.ToJson());
    Console.WriteLine($"  version: {mapVersion}");
    Console.WriteLine($"  → {bundlePath}");

    // 7. place and trigger volumes joined to the nav → zones.json. After the bundle rather than after the
    //    nav step because the file records the mapVersion it was baked beside. Optional like collision:
    //    a map whose zones fail still bakes its 2D assets, and --zones can top it up later.
    try
    {
        Zones.Result zr = Zones.Bake(
            vpk, map, floors.Floors, mapVersion, BakerVersion, Path.Combine(outDir, "zones.json"));
        foreach (string note in zr.Notes)
        {
            Console.WriteLine(note);
        }

        Console.WriteLine(zr.Diagnostic);
        foreach (string failure in zr.Failures)
        {
            Console.WriteLine($"  ! zones: {failure}");
        }

        if (diag)
        {
            Console.Write(zr.AdjacencyText);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  zones: (skipped, {ex.GetType().Name}: {ex.Message})");
    }

    Console.WriteLine();
}

// Prefer *_radar_psd.vtex_c, then *_radar_tga.vtex_c. Returns (source vtex path, output png name) or null.
(string vtex, string png)? ResolveRadar(string map, bool lower)
{
    string suffix = lower ? "_lower_radar" : "_radar";
    string png = lower ? $"{map}_lower.png" : $"{map}.png";
    foreach (string kind in new[]
             {
                 "psd", "tga"
             })
    {
        string candidate = Path.Combine(radarDir, $"{map}{suffix}_{kind}.vtex_c");
        if (File.Exists(candidate))
        {
            return (candidate, png);
        }
    }

    return null;
}

List<RadarLayer> BuildRadarLayers(string map, OverviewTxt.Parsed ov, List<string> images)
{
    List<RadarLayer> layers = new();
    if (ov.VerticalSections.Count == 0)
    {
        // Single-image map: one layer covering all Z with the default image (if any).
        string img = images.FirstOrDefault() ?? $"{map}.png";
        layers.Add(new RadarLayer(-100_000, 100_000, img));
        return layers;
    }

    foreach ((string name, double altMin, double altMax) in ov.VerticalSections)
    {
        string img = name.Equals("lower", StringComparison.OrdinalIgnoreCase) ? $"{map}_lower.png" : $"{map}.png";
        layers.Add(new RadarLayer(altMin, altMax, img));
    }

    return layers;
}

static void DecodeVtexToPng(string vtexPath, string outPng)
{
    using Resource resource = new();
    resource.Read(vtexPath);
    Texture texture = (Texture)resource.DataBlock!;
    using SKBitmap bitmap = texture.GenerateBitmap();
    File.WriteAllBytes(outPng, TextureExtract.ToPngImage(bitmap));
}

// Walk up from the executable until the repository root (the directory holding the solution) is found.
// The icon bake writes into assets/ there, next to the committed map bundles.
static string FindRepoRoot()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException("repository root not found walking up from " + AppContext.BaseDirectory);
}

// Walk up from the executable until a directory containing cs2-assets/ is found.
static string FindCs2Assets()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, "cs2-assets");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException("cs2-assets/ not found walking up from " + AppContext.BaseDirectory);
}
