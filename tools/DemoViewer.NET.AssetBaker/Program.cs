#region

using System.IO.Hashing;
using DemoViewer.NET.AssetBaker;
using SkiaSharp;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

#endregion

const int SchemaVersion = 1;
const string BakerVersion = "0.1+vrf19.2.6339";

// ── args: <map> [<map>...] [--diag] ──
// With no map args, bake the full shipping set (the Active Duty / commonly-demoed pool). Each
// needs a source vpk (cs2-assets/maps/), a radar vtex_c, and an overview txt, all present in the
// gitignored cs2-assets/ cache. Pass explicit map names to bake a subset.
bool diag = args.Contains("--diag");
string[] maps = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (maps.Length == 0)
{
    maps =
    [
        "de_nuke", "de_dust2", "de_mirage", "de_inferno", "de_anubis",
        "de_ancient", "de_overpass", "de_vertigo", "de_cache"
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
    try
    {
        const string TrisName = "collision.tris";
        CollisionMesh.Result cm = CollisionMesh.Extract(vpk, map, Path.Combine(outDir, TrisName));
        crc.Append(File.ReadAllBytes(Path.Combine(outDir, TrisName)));
        collision = new CollisionMeshRef(
            TrisName, cm.TriangleCount,
            cm.Min.X, cm.Min.Y, cm.Min.Z, cm.Max.X, cm.Max.Y, cm.Max.Z);
        Console.WriteLine(cm.Diagnostic);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  collision: (skipped — {ex.GetType().Name}: {ex.Message})");
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
    Console.WriteLine($"  → {bundlePath}\n");
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
