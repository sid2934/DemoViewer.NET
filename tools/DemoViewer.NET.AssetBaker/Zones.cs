#region

using System.Globalization;
using System.IO.Hashing;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.NavMesh;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;

#endregion

namespace DemoViewer.NET.AssetBaker;

/// <summary>
///     Bakes the map's <b>place and trigger volumes</b>, joined to its nav mesh, into
///     <c>assets/&lt;map&gt;/zones.json</c>, so the app can turn a world point into a named place or a
///     bombsite VRF-free. The game defines places by <c>env_cs_place</c> brush volumes and the nav mesh
///     defines where a player can stand; the bake carries both: every nav area gets the place whose
///     volume contains its centroid, and the areas no volume covers inherit one from their nearest
///     seeded neighbour by a flood over the nav connections. Outlines and place adjacency then fall out
///     of the joined data with no polygon union anywhere.
///     <para>
///         <b>Why a sibling file and not a bundle field.</b> <c>bundle.json</c> is parsed by the
///         engine's <c>MapAssetBundleReader</c> into a DTO the app cannot extend, and the
///         <c>--zones</c> top-up mode cannot restamp the bundle's <c>mapVersion</c> without the radar
///         source bytes only a full bake has. So the app finds this file by path, exactly as it finds
///         <c>collision.tris.gz</c>, and <c>bundleMapVersion</c> inside it is how drift between the two
///         files is detected.
///     </para>
///     <para>
///         <b>Geometry conventions (measured on all 438 volumes of the ten shipped maps).</b> Hull vertices
///         are in entity-local space and the entity <c>origin</c> translates them; every plane is stored in
///         world space with the inside test <c>dot(n, p) - d &lt;= 0</c>, verified per hull against the hull's
///         own vertex centroid. The opposite sign gives the mirror image and silently halves coverage,
///         which is why the check is not optional. <c>angles</c> is applied when non-zero and logged,
///         because only one rotated volume (a nuke buyzone) has ever been seen.
///     </para>
/// </summary>
public static class Zones
{
    public const int SchemaVersion = 1;

    /// <summary>
    ///     <c>MapSpace.LevelQuantum</c>, restated so a reader without Core can key floors. An area's
    ///     <c>floor</c> is the quantized lower Z of the bundle band that contains it, the same value a
    ///     <c>SpaceRef.World</c> annotation anchor carries, never a floor index.
    /// </summary>
    public const double FloorQuantum = 64.0;

    // A point on a hull face counts as inside; the nav centroid of an area whose edge is a volume wall
    // sits exactly on that wall more often than chance suggests.
    private const float ContainSlack = 0.5f;

    // An area is seeded when its centroid, or the centroid lifted by one of these, is inside a place
    // volume. The lifts catch volumes whose floor sits a little above the nav surface (stairs, ramps).
    private static readonly float[] ProbeLifts = [0f, 8f, 32f];

    private static readonly string[] VolumeClasses =
        ["env_cs_place", "func_bomb_target", "func_buyzone", "func_hostage_rescue"];

    /// <summary>
    ///     The coverage table of the design's section 2.5, measured on the CS2 build the design was
    ///     written against. <c>--zones --diag</c> compares each bake with its row: a drift of a few areas
    ///     is a map update, a large one is a baker bug and never a new baseline.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Coverage> ExpectedCoverage =
        new Dictionary<string, Coverage>(StringComparer.Ordinal)
        {
            ["de_nuke"] = new("de_nuke", 58, 29, 3040, 2556, 2968, 72, 0),
            ["de_dust2"] = new("de_dust2", 43, 24, 2242, 2172, 2242, 0, 10),
            ["de_mirage"] = new("de_mirage", 23, 23, 2544, 2295, 2540, 4, 91),
            ["de_inferno"] = new("de_inferno", 46, 23, 2738, 2425, 2730, 8, 0),
            ["de_anubis"] = new("de_anubis", 62, 28, 2633, 2431, 2633, 0, 2),
            ["de_ancient"] = new("de_ancient", 18, 18, 1969, 1760, 1969, 0, 2),
            ["de_overpass"] = new("de_overpass", 26, 25, 3938, 3452, 3923, 15, 287),
            ["de_vertigo"] = new("de_vertigo", 50, 23, 2105, 1589, 2102, 3, 0),
            ["de_train"] = new("de_train", 26, 16, 2154, 1996, 2154, 0, 2),
            ["de_cache"] = new("de_cache", 66, 48, 2209, 2185, 2208, 1, 23)
        };

    /// <summary>
    ///     Reads the volumes and the nav out of the per-map vpk, joins them, and writes
    ///     <paramref name="outPath" />. <paramref name="floors" /> and <paramref name="bundleMapVersion" />
    ///     come from the bundle beside the output (a full bake has just computed them; the top-up mode
    ///     reads them back) so the floor keys match what the app already shows.
    /// </summary>
    public static Result Bake(
        string vpkPath, string mapName, IReadOnlyList<FloorBand> floors, string bundleMapVersion,
        string bakerVersion, string outPath)
    {
        List<string> notes = new();
        List<string> failures = new();
        Crc32 crc = new();

        using Package package = new();
        package.Read(vpkPath);

        List<Volume> volumes = ExtractVolumes(package, mapName, crc, notes, failures, out double? bombRadius);

        string navPath = $"maps/{mapName}.nav";
        PackageEntry navEntry = package.FindEntry(navPath)
                                ?? throw new FileNotFoundException($"{navPath} not found in {vpkPath}");
        package.ReadEntry(navEntry, out byte[] navBytes);
        crc.Append(navBytes);
        string zonesVersion = Convert.ToHexString(crc.GetCurrentHash()).ToLowerInvariant();

        NavMeshFile nav = new();
        using (MemoryStream ms = new(navBytes))
        {
            nav.Read(ms);
        }

        // Sorted by name so the id a place gets is stable across re-bakes; consumers store the name,
        // never the id, but a stable id keeps the diff of a re-bake readable.
        List<string> places = volumes
            .Where(v => v.Kind == "place" && v.PlaceName is not null)
            .Select(v => v.PlaceName!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Dictionary<string, int> placeIds = new(StringComparer.Ordinal);
        for (int i = 0; i < places.Count; i++)
        {
            placeIds[places[i]] = i;
        }

        Assignment assignment = AssignAreas(nav, volumes, placeIds);
        List<(int A, int B)> adjacency = BuildAdjacency(nav, assignment);
        List<(uint A, uint B)> areaLinks = BuildAreaLinks(nav);

        int bombsites = volumes.Count(v => v.Kind == "bombsite");
        int hullLess = volumes.Count(v => v.Kind is "place" or "bombsite" && v.Hulls.Count == 0);
        if (hullLess > 0)
        {
            failures.Add($"{hullLess} place/bombsite volume(s) have no hull");
        }

        if (mapName.StartsWith("de_", StringComparison.Ordinal))
        {
            string designations = string.Join(",", volumes
                .Where(v => v.Kind == "bombsite")
                .Select(v => v.Site ?? "?")
                .OrderBy(s => s, StringComparer.Ordinal));
            if (bombsites != 2 || designations != "A,B")
            {
                failures.Add($"expected two bombsites designated 0 and 1, got {bombsites} ({designations})");
            }
        }

        Coverage coverage = new(
            mapName,
            volumes.Count(v => v.Kind == "place"),
            places.Count,
            nav.Areas.Count,
            assignment.Seeded,
            assignment.Place.Count,
            nav.Areas.Count - assignment.Place.Count,
            assignment.Multi);

        string json = Write(
            mapName, bundleMapVersion, zonesVersion, bakerVersion, floors, places, placeIds, volumes,
            nav, assignment, areaLinks, adjacency, bombRadius);
        File.WriteAllText(outPath, json);

        string adjacencyText = FormatAdjacency(places, adjacency);
        string diagnostic =
            $"  zones: places={coverage.DistinctPlaces} volumes={volumes.Count} " +
            $"(place {coverage.PlaceVolumes}, bombsite {bombsites}, buyzone {volumes.Count(v => v.Kind == "buyzone")}) " +
            $"areas={coverage.NavAreas} seeded={coverage.Seeded} flooded={coverage.Flooded} " +
            $"unreachable={coverage.Unreachable} multi={coverage.Multi} links={areaLinks.Count} " +
            $"adjacency={adjacency.Count}  version {zonesVersion}  {json.Length / 1024.0:F0} KiB";

        return new Result(coverage, zonesVersion, notes, failures, adjacencyText, diagnostic);
    }

    /// <summary>
    ///     The floor bands and <c>mapVersion</c> of an existing <c>bundle.json</c>, which is all the top-up
    ///     mode needs from it. Read with a document walk rather than the bundle record so an older bundle
    ///     with fewer fields still reads.
    /// </summary>
    public static (IReadOnlyList<FloorBand> Floors, string MapVersion) ReadBundle(string bundlePath)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(bundlePath));
        JsonElement root = doc.RootElement;
        string mapVersion = root.GetProperty("mapVersion").GetString()
                            ?? throw new InvalidDataException($"{bundlePath}: mapVersion is null");
        List<FloorBand> floors = new();
        foreach (JsonElement band in root.GetProperty("floors").EnumerateArray())
        {
            floors.Add(new FloorBand(band.GetProperty("minZ").GetDouble(), band.GetProperty("maxZ").GetDouble()));
        }

        if (floors.Count == 0)
        {
            throw new InvalidDataException($"{bundlePath}: no floors");
        }

        return (floors, mapVersion);
    }

    /// <summary>
    ///     Failures of a bake's coverage against <see cref="ExpectedCoverage" />; empty when the map has no
    ///     row or every column is within tolerance. Volume and place counts are exact, because a change
    ///     there is a map update worth noticing; area counts move by a handful between nav rebuilds.
    /// </summary>
    public static IReadOnlyList<string> CheckAgainstBaseline(Coverage actual)
    {
        List<string> failures = new();
        if (!ExpectedCoverage.TryGetValue(actual.Map, out Coverage? expected))
        {
            return failures;
        }

        const int AreaTolerance = 16;
        void Exact(string column, int a, int e)
        {
            if (a != e)
            {
                failures.Add($"{actual.Map}: {column} {a}, baseline {e}");
            }
        }

        void Near(string column, int a, int e)
        {
            if (Math.Abs(a - e) > AreaTolerance)
            {
                failures.Add($"{actual.Map}: {column} {a}, baseline {e} (tolerance {AreaTolerance})");
            }
        }

        Exact("env_cs_place", actual.PlaceVolumes, expected.PlaceVolumes);
        Exact("distinct places", actual.DistinctPlaces, expected.DistinctPlaces);
        Near("nav areas", actual.NavAreas, expected.NavAreas);
        Near("seeded", actual.Seeded, expected.Seeded);
        Near("flooded", actual.Flooded, expected.Flooded);
        Near("unreachable", actual.Unreachable, expected.Unreachable);
        Near("multi", actual.Multi, expected.Multi);
        return failures;
    }

    /// <summary>The section 2.5 coverage table, one row per bake, in the design's column order.</summary>
    public static string FormatCoverageTable(IEnumerable<Coverage> rows)
    {
        StringBuilder sb = new();
        sb.AppendLine("  map          env_cs_place  places  nav areas  seeded  flooded  unreachable  multi");
        foreach (Coverage c in rows)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {c.Map,-12} {c.PlaceVolumes,12}  {c.DistinctPlaces,6}  {c.NavAreas,9}  {c.Seeded,6}  " +
                $"{c.Flooded,7}  {c.Unreachable,11}  {c.Multi,5}"));
        }

        return sb.ToString();
    }

    // ── 1. volumes ────────────────────────────────────────────────────────────────────────────────

    private static List<Volume> ExtractVolumes(
        Package package, string mapName, Crc32 crc, List<string> notes, List<string> failures,
        out double? bombRadius)
    {
        List<Volume> volumes = new();
        bombRadius = null;
        int hullsChecked = 0, unexpectedSign = 0;

        if (package.Entries is null || !package.Entries.TryGetValue("vents_c", out List<PackageEntry>? lumps))
        {
            throw new FileNotFoundException($"no entity lump in the vpk for {mapName}");
        }

        string prefix = $"maps/{mapName}/entities/";
        // Path order, so "first in entity-lump order" (decision D4) is the same rule on every run.
        foreach (PackageEntry lumpEntry in lumps
                     .Where(e => e.GetFullPath().StartsWith(prefix, StringComparison.Ordinal))
                     .OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            package.ReadEntry(lumpEntry, out byte[] lumpBytes);
            crc.Append(lumpBytes);

            using Resource res = new();
            res.Read(new MemoryStream(lumpBytes), false);
            if (res.DataBlock is not EntityLump lump)
            {
                notes.Add($"  ! {lumpEntry.GetFullPath()}: not an entity lump ({res.DataBlock?.GetType().Name})");
                continue;
            }

            int index = 0;
            foreach (EntityLump.Entity entity in lump.GetEntities())
            {
                index++;
                string? classname = Str(entity, "classname");
                if (classname == "info_map_parameters")
                {
                    if (entity.TryGetValue("bombradius", out KVObject? radius))
                    {
                        bombRadius = radius.ToDouble(CultureInfo.InvariantCulture);
                    }

                    continue;
                }

                if (classname is null || Array.IndexOf(VolumeClasses, classname) < 0)
                {
                    continue;
                }

                Volume volume = new()
                {
                    Kind = classname switch
                    {
                        "env_cs_place" => "place",
                        "func_bomb_target" => "bombsite",
                        "func_buyzone" => "buyzone",
                        _ => "hostageRescue"
                    },
                    PlaceName = classname == "env_cs_place" ? Str(entity, "place_name") : null,
                    Site = classname == "func_bomb_target"
                        ? Str(entity, "bomb_site_designation") switch { "0" => "A", "1" => "B", _ => null }
                        : null,
                    Team = classname == "func_buyzone"
                        ? Str(entity, "teamnum") switch { "2" => "T", "3" => "CT", _ => null }
                        : null,
                    Entity = Str(entity, "hammeruniqueid") ?? $"{lump.Name}:{index}"
                };
                string label = $"{classname} {volume.PlaceName ?? volume.Site ?? volume.Team ?? volume.Entity}";

                if (volume.Kind == "place" && string.IsNullOrEmpty(volume.PlaceName))
                {
                    notes.Add($"  ! {label}: no place_name, skipped");
                    continue;
                }

                if (volume.Kind == "bombsite" && volume.Site is null)
                {
                    notes.Add($"  ! {label}: bomb_site_designation is {Str(entity, "bomb_site_designation") ?? "absent"}");
                }

                string? model = Str(entity, "model");
                if (string.IsNullOrEmpty(model))
                {
                    notes.Add($"  ! {label}: no model, skipped");
                    continue;
                }

                PackageEntry? modelEntry = package.FindEntry(model + "_c");
                if (modelEntry is null)
                {
                    notes.Add($"  ! {label}: {model}_c not in vpk, skipped");
                    continue;
                }

                package.ReadEntry(modelEntry, out byte[] modelBytes);
                crc.Append(modelBytes);

                using Resource modelRes = new();
                modelRes.Read(new MemoryStream(modelBytes), false);
                PhysAggregateData? phys = modelRes.DataBlock switch
                {
                    Model m => m.GetEmbeddedPhys(),
                    PhysAggregateData p => p,
                    _ => null
                };
                if (phys is null)
                {
                    notes.Add($"  ! {label}: no embedded phys in {model}, skipped");
                    continue;
                }

                if (phys.BindPose is { Length: > 0 })
                {
                    notes.Add($"  ! {label}: bind pose with {phys.BindPose.Length} transform(s), not applied");
                }

                Vector3 origin = entity.GetVector3Property("origin", Vector3.Zero);
                Vector3 angles = entity.GetVector3Property("angles", Vector3.Zero);
                volume.Rotated = angles != Vector3.Zero;
                if (volume.Rotated)
                {
                    notes.Add($"  ~ {label}: angles ({angles.X:F0},{angles.Y:F0},{angles.Z:F0}) applied");
                }

                int meshes = 0;
                foreach (Part part in phys.Parts)
                {
                    meshes += part.Shape.Meshes?.Length ?? 0;
                    foreach (HullDescriptor hd in part.Shape.Hulls ?? Array.Empty<HullDescriptor>())
                    {
                        hullsChecked++;
                        HullPlanes? hull = ToWorldHull(hd.Shape, origin, angles, out bool signAsExpected);
                        if (hull is null)
                        {
                            failures.Add($"{mapName}: {label}: hull centroid satisfies neither plane convention");
                            continue;
                        }

                        if (!signAsExpected)
                        {
                            unexpectedSign++;
                        }

                        volume.Hulls.Add(hull);
                        volume.Min = Vector3.Min(volume.Min, hull.Min);
                        volume.Max = Vector3.Max(volume.Max, hull.Max);
                    }
                }

                if (meshes > 0)
                {
                    // A mesh-shaped volume has no half-space form; the nav flood still covers its place
                    // when another volume touches it. The note is the guard for the map that first ships one.
                    notes.Add($"  ! {label}: {meshes} physics mesh(es) ignored, {volume.Hulls.Count} hull(s) kept");
                }

                if (volume.Hulls.Count == 0)
                {
                    notes.Add($"  ! {label}: no hull, volume kept without geometry");
                }

                volumes.Add(volume);
            }
        }

        if (unexpectedSign > 0)
        {
            // Every hull measured so far stores planes as n.p - d <= 0. The other convention is handled,
            // but a map that mixes them is worth a look before its zones ship.
            notes.Add($"  ~ {unexpectedSign}/{hullsChecked} hull(s) store planes as n.p + d <= 0; normalised");
        }

        if (bombRadius is null)
        {
            notes.Add("  ~ info_map_parameters.bombradius absent; parameters.bombRadius omitted");
        }

        return volumes;
    }

    // Converts a hull's local planes to world space and normalises them to "inside is n.p - d <= 0".
    // The sign is decided per hull by the hull's own vertex centroid, which is inside by construction:
    // returns null when neither convention holds for every plane, which means the hull is not convex
    // or the reader is wrong, and either way the volume must not ship.
    private static HullPlanes? ToWorldHull(Hull hull, Vector3 origin, Vector3 angles, out bool signAsExpected)
    {
        ReadOnlySpan<Vector3> verts = hull.GetVertexPositions();
        ReadOnlySpan<Hull.Plane> planes = hull.GetPlanes();
        signAsExpected = true;
        if (verts.Length == 0 || planes.Length == 0)
        {
            return null;
        }

        Vector3 centroid = Vector3.Zero;
        foreach (Vector3 v in verts)
        {
            centroid += v;
        }

        centroid /= verts.Length;

        bool minusOk = true, plusOk = true;
        foreach (Hull.Plane plane in planes)
        {
            float along = Vector3.Dot(plane.Normal, centroid);
            if (along - plane.Offset > 1e-3f)
            {
                minusOk = false;
            }

            if (along + plane.Offset > 1e-3f)
            {
                plusOk = false;
            }
        }

        if (!minusOk && !plusOk)
        {
            return null;
        }

        signAsExpected = minusOk;
        Rotation rot = Rotation.FromAngles(angles);

        // n.(R^-1 (p - o)) - d <= 0  becomes  (R n).p - (d + (R n).o) <= 0 for a rotation R.
        Vector4[] world = new Vector4[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            Vector3 n = rot.Apply(planes[i].Normal);
            float d = minusOk ? planes[i].Offset : -planes[i].Offset;
            world[i] = new Vector4(n, d + Vector3.Dot(n, origin));
        }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 v in verts)
        {
            Vector3 w = rot.Apply(v) + origin;
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }

        return new HullPlanes(world, min, max);
    }

    private static string? Str(EntityLump.Entity entity, string key) =>
        entity.TryGetValue(key, out KVObject? value) && !value.IsNull ? value.ToString(CultureInfo.InvariantCulture) : null;

    // ── 2. areas ──────────────────────────────────────────────────────────────────────────────────

    // Seeds every area whose centroid (or centroid lifted 8 or 32 units) is in a place volume, the first
    // volume in lump order winning an overlap (decision D4), then floods over the nav connections so
    // every reachable area takes the place of its nearest seeded neighbour by hop count. Areas the
    // flood never reaches are isolated islands and get no place.
    private static Assignment AssignAreas(NavMeshFile nav, List<Volume> volumes, Dictionary<string, int> placeIds)
    {
        List<Volume> places = volumes.Where(v => v.Kind == "place" && v.PlaceName is not null).ToList();
        Dictionary<uint, int> place = new();
        HashSet<uint> seeds = new();
        int multi = 0;

        foreach (uint id in nav.Areas.Keys.OrderBy(k => k))
        {
            NavMeshArea area = nav.Areas[id];
            if (area.Corners is null || area.Corners.Length == 0)
            {
                continue;
            }

            Vector3 centroid = Centroid(area.Corners);
            int first = -1;
            HashSet<int> hits = new();
            foreach (Volume v in places)
            {
                bool hit = false;
                foreach (float lift in ProbeLifts)
                {
                    if (v.Contains(centroid + new Vector3(0, 0, lift)))
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    continue;
                }

                int pid = placeIds[v.PlaceName!];
                hits.Add(pid);
                if (first < 0)
                {
                    first = pid;
                }
            }

            if (first < 0)
            {
                continue;
            }

            if (hits.Count > 1)
            {
                multi++;
            }

            place[id] = first;
            seeds.Add(id);
        }

        // Multi-source breadth-first: the first visit to an area is from the seed nearest by hops, and
        // ties fall to the lower area id because the queue starts in id order.
        Queue<uint> queue = new(seeds.OrderBy(s => s));
        while (queue.Count > 0)
        {
            uint id = queue.Dequeue();
            int pid = place[id];
            foreach (NavMeshConnection[] side in nav.Areas[id].Connections ?? Array.Empty<NavMeshConnection[]>())
            {
                foreach (NavMeshConnection conn in side ?? Array.Empty<NavMeshConnection>())
                {
                    if (place.ContainsKey(conn.AreaId) || !nav.Areas.ContainsKey(conn.AreaId))
                    {
                        continue;
                    }

                    place[conn.AreaId] = pid;
                    queue.Enqueue(conn.AreaId);
                }
            }
        }

        return new Assignment(place, seeds, seeds.Count, multi);
    }

    // Every connection between two areas of different places adds one undirected pair.
    private static List<(int A, int B)> BuildAdjacency(NavMeshFile nav, Assignment assignment)
    {
        HashSet<(int, int)> pairs = new();
        foreach ((uint id, NavMeshArea area) in nav.Areas)
        {
            if (!assignment.Place.TryGetValue(id, out int a))
            {
                continue;
            }

            foreach (NavMeshConnection[] side in area.Connections ?? Array.Empty<NavMeshConnection[]>())
            {
                foreach (NavMeshConnection conn in side ?? Array.Empty<NavMeshConnection>())
                {
                    if (assignment.Place.TryGetValue(conn.AreaId, out int b) && a != b)
                    {
                        pairs.Add((Math.Min(a, b), Math.Max(a, b)));
                    }
                }
            }
        }

        return pairs.OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToList();
    }

    // Undirected nav connections, deduplicated: the outline layer needs them to find the edges with no
    // same-place partner, and a finer tolerance can walk them instead of the place graph.
    private static List<(uint A, uint B)> BuildAreaLinks(NavMeshFile nav)
    {
        HashSet<(uint, uint)> links = new();
        foreach ((uint id, NavMeshArea area) in nav.Areas)
        {
            foreach (NavMeshConnection[] side in area.Connections ?? Array.Empty<NavMeshConnection[]>())
            {
                foreach (NavMeshConnection conn in side ?? Array.Empty<NavMeshConnection>())
                {
                    if (conn.AreaId != id && nav.Areas.ContainsKey(conn.AreaId))
                    {
                        links.Add((Math.Min(id, conn.AreaId), Math.Max(id, conn.AreaId)));
                    }
                }
            }
        }

        return links.OrderBy(l => l.Item1).ThenBy(l => l.Item2).ToList();
    }

    private static Vector3 Centroid(Vector3[] corners)
    {
        Vector3 sum = Vector3.Zero;
        foreach (Vector3 c in corners)
        {
            sum += c;
        }

        return sum / corners.Length;
    }

    // ── 3. the file ───────────────────────────────────────────────────────────────────────────────

    // Written by hand rather than through the serializer: one record per line with its number arrays
    // inline is what keeps a 3000-area file diffable after a re-bake, and neither the indented nor the
    // compact serializer output is. The text is parsed back before it is returned, so a formatting
    // slip fails the bake rather than the app.
    private static string Write(
        string mapName, string bundleMapVersion, string zonesVersion, string bakerVersion,
        IReadOnlyList<FloorBand> floors, List<string> places, Dictionary<string, int> placeIds,
        List<Volume> volumes, NavMeshFile nav, Assignment assignment,
        List<(uint A, uint B)> areaLinks, List<(int A, int B)> adjacency, double? bombRadius)
    {
        StringBuilder sb = new();
        sb.Append("{\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"schemaVersion\": {SchemaVersion},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"mapName\": {Quote(mapName)},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"bundleMapVersion\": {Quote(bundleMapVersion)},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"zonesVersion\": {Quote(zonesVersion)},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"bakerVersion\": {Quote(bakerVersion)},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"floorQuantum\": {Num(FloorQuantum, 0)},\n");

        sb.Append("  \"floors\": [\n");
        sb.Append(string.Join(",\n", floors.Select(f =>
            $"    {{ \"key\": {Num(FloorKey(f.MinZ), 0)}, \"minZ\": {Num(f.MinZ, 1)}, \"maxZ\": {Num(f.MaxZ, 1)} }}")));
        sb.Append("\n  ],\n");

        sb.Append("  \"places\": [\n");
        sb.Append(string.Join(",\n", places.Select((p, i) => $"    {{ \"id\": {i}, \"name\": {Quote(p)} }}")));
        sb.Append("\n  ],\n");

        sb.Append("  \"volumes\": [\n");
        sb.Append(string.Join(",\n", volumes.Select(v => VolumeLine(v, placeIds))));
        sb.Append("\n  ],\n");

        sb.Append("  \"areas\": [\n");
        List<string> areaLines = new(nav.Areas.Count);
        foreach (uint id in nav.Areas.Keys.OrderBy(k => k))
        {
            NavMeshArea area = nav.Areas[id];
            Vector3[] corners = area.Corners ?? Array.Empty<Vector3>();
            double z = corners.Length == 0 ? 0 : Centroid(corners).Z;
            int pid = assignment.Place.TryGetValue(id, out int p) ? p : -1;
            bool seed = assignment.Seeds.Contains(id);
            string xy = string.Join(", ", corners.Select(c => $"{Num(c.X, 1)}, {Num(c.Y, 1)}"));
            areaLines.Add(
                $"    {{ \"id\": {id}, \"place\": {pid}, \"floor\": {Num(FloorKeyFor(floors, z), 0)}, " +
                $"\"seed\": {(seed ? "true" : "false")}, \"z\": {Num(z, 1)}, \"xy\": [{xy}] }}");
        }

        sb.Append(string.Join(",\n", areaLines));
        sb.Append("\n  ],\n");

        sb.Append("  \"areaLinks\": [\n");
        sb.Append(PairLines(areaLinks.Select(l => $"[{l.A}, {l.B}]")));
        sb.Append("\n  ],\n");

        sb.Append("  \"adjacency\": [\n");
        sb.Append(PairLines(adjacency.Select(a => $"[{a.A}, {a.B}]")));
        sb.Append("\n  ],\n");

        sb.Append("  \"parameters\": { ");
        if (bombRadius is { } r)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\"bombRadius\": {Num(r, 1)} ");
        }

        sb.Append("}\n");
        sb.Append("}\n");

        string json = sb.ToString();
        using (JsonDocument.Parse(json))
        {
        }

        return json;
    }

    private static string VolumeLine(Volume v, Dictionary<string, int> placeIds)
    {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"    {{ \"kind\": {Quote(v.Kind)}");
        if (v.PlaceName is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $", \"place\": {placeIds[v.PlaceName]}");
        }

        if (v.Site is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $", \"site\": {Quote(v.Site)}");
        }

        if (v.Team is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $", \"team\": {Quote(v.Team)}");
        }

        sb.Append(CultureInfo.InvariantCulture, $", \"entity\": {Quote(v.Entity)}");
        if (v.Hulls.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $", \"min\": {Vec(v.Min)}, \"max\": {Vec(v.Max)}");
        }

        sb.Append(", \"hulls\": [");
        sb.Append(string.Join(", ", v.Hulls.Select(h =>
            "{ \"planes\": [" + string.Join(", ", h.Planes.Select(p =>
                $"[{Num(p.X, 6)}, {Num(p.Y, 6)}, {Num(p.Z, 6)}, {Num(p.W, 1)}]")) + "] }")));
        sb.Append("] }");
        return sb.ToString();
    }

    private static string PairLines(IEnumerable<string> pairs)
    {
        const int PerLine = 16;
        List<string> lines = new();
        foreach (string[] chunk in pairs.Chunk(PerLine))
        {
            lines.Add("    " + string.Join(", ", chunk));
        }

        return string.Join(",\n", lines);
    }

    private static string Vec(Vector3 v) => $"[{Num(v.X, 1)}, {Num(v.Y, 1)}, {Num(v.Z, 1)}]";

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    // Rounded to the given decimals with no trailing zeros, and "-0" folded to "0" so a re-bake that
    // moves a coordinate across zero by less than the rounding step does not show up as a diff.
    private static string Num(double v, int decimals)
    {
        string s = Math.Round(v, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals),
            CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static double FloorKey(double minZ) => Math.Floor(minZ / FloorQuantum + 0.5) * FloorQuantum;

    // The band whose [minZ, maxZ) contains z; the last band when z is above every top, which cannot
    // happen with the baker's outer bands at plus and minus 100 000 but must not throw.
    private static double FloorKeyFor(IReadOnlyList<FloorBand> floors, double z)
    {
        foreach (FloorBand band in floors)
        {
            if (z >= band.MinZ && z < band.MaxZ)
            {
                return FloorKey(band.MinZ);
            }
        }

        return FloorKey(floors[^1].MinZ);
    }

    private static string FormatAdjacency(List<string> places, List<(int A, int B)> adjacency)
    {
        StringBuilder sb = new();
        foreach (IGrouping<int, int> g in adjacency
                     .SelectMany(p => new[] { (p.A, p.B), (p.B, p.A) })
                     .GroupBy(p => p.Item1, p => p.Item2)
                     .OrderBy(g => places[g.Key], StringComparer.Ordinal))
        {
            sb.AppendLine($"    {places[g.Key]}: " +
                          string.Join(", ", g.Select(i => places[i]).OrderBy(n => n, StringComparer.Ordinal)));
        }

        return sb.ToString();
    }

    /// <summary>One bake's outcome: the coverage row, the stamp written, and what the diag mode prints.</summary>
    public sealed record Result(
        Coverage Coverage,
        string ZonesVersion,
        IReadOnlyList<string> Notes,
        IReadOnlyList<string> Failures,
        string AdjacencyText,
        string Diagnostic);

    /// <summary>One row of the coverage table: how much of the nav mesh the volumes and the flood explain.</summary>
    public sealed record Coverage(
        string Map,
        int PlaceVolumes,
        int DistinctPlaces,
        int NavAreas,
        int Seeded,
        int Flooded,
        int Unreachable,
        int Multi);

    // A convex hull as world-space planes (xyz = normal, w = offset; inside is n.p - w <= 0) and its AABB.
    private sealed record HullPlanes(Vector4[] Planes, Vector3 Min, Vector3 Max);

    private sealed record Assignment(Dictionary<uint, int> Place, HashSet<uint> Seeds, int Seeded, int Multi);

    private sealed class Volume
    {
        public required string Kind { get; init; }
        public string? PlaceName { get; init; }
        public string? Site { get; init; }
        public string? Team { get; init; }
        public required string Entity { get; init; }
        public bool Rotated { get; set; }
        public Vector3 Min = new(float.MaxValue);
        public Vector3 Max = new(float.MinValue);
        public List<HullPlanes> Hulls { get; } = new();

        public bool Contains(Vector3 p)
        {
            foreach (HullPlanes h in Hulls)
            {
                if (p.X < h.Min.X - 1 || p.Y < h.Min.Y - 1 || p.Z < h.Min.Z - 1 ||
                    p.X > h.Max.X + 1 || p.Y > h.Max.Y + 1 || p.Z > h.Max.Z + 1)
                {
                    continue;
                }

                bool inside = true;
                foreach (Vector4 plane in h.Planes)
                {
                    if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z - plane.W > ContainSlack)
                    {
                        inside = false;
                        break;
                    }
                }

                if (inside)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Source's AngleMatrix: angles are (pitch about Y, yaw about Z, roll about X) in degrees, applied
    // roll first, then pitch, then yaw. Only one volume on the ten maps has a non-zero angle, so the
    // identity is the common case and the matrix is built only when needed.
    private readonly struct Rotation
    {
        private readonly float _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;
        private readonly bool _rotated;

        private Rotation(Vector3 angles)
        {
            const float DegToRad = MathF.PI / 180f;
            (float sp, float cp) = MathF.SinCos(angles.X * DegToRad);
            (float sy, float cy) = MathF.SinCos(angles.Y * DegToRad);
            (float sr, float cr) = MathF.SinCos(angles.Z * DegToRad);
            _m00 = cp * cy;
            _m10 = cp * sy;
            _m20 = -sp;
            _m01 = sp * sr * cy - cr * sy;
            _m11 = sp * sr * sy + cr * cy;
            _m21 = sr * cp;
            _m02 = sp * cr * cy + sr * sy;
            _m12 = sp * cr * sy - sr * cy;
            _m22 = cr * cp;
            _rotated = true;
        }

        public static Rotation FromAngles(Vector3 angles) => angles == Vector3.Zero ? default : new Rotation(angles);

        public Vector3 Apply(Vector3 v) => _rotated
            ? new Vector3(
                _m00 * v.X + _m01 * v.Y + _m02 * v.Z,
                _m10 * v.X + _m11 * v.Y + _m12 * v.Z,
                _m20 * v.X + _m21 * v.Y + _m22 * v.Z)
            : v;
    }
}
