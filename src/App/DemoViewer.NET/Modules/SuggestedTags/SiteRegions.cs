#region

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The site regions the detectors read for one map (suggested-tags.md §3.1, §3.2): per bombsite,
///     the site place plus its approach places, and the places a side stands in at spawn. Composed
///     from three sources in precedence order: a shipped table, the table learned from the library,
///     and the team's edits in the profile, which apply last.
/// </summary>
public sealed class SiteRegions
{
    /// <summary>The A site's place, as every Valve map names it.</summary>
    public const string SiteA = "BombsiteA";

    /// <summary>The B site's place.</summary>
    public const string SiteB = "BombsiteB";

    /// <summary>The T spawn place, spawn-adjacent on every map whatever the table says.</summary>
    public const string TSpawn = "TSpawn";

    private readonly Dictionary<string, HashSet<string>> _regions;
    private readonly HashSet<string> _spawn;

    private SiteRegions(string map, Dictionary<string, HashSet<string>> regions, HashSet<string> spawn, string source)
    {
        Map = map;
        _regions = regions;
        _spawn = spawn;
        Source = source;
    }

    /// <summary>The two sites, A first.</summary>
    public static IReadOnlyList<string> Sites { get; } = [SiteA, SiteB];

    /// <summary>The map.</summary>
    public string Map { get; }

    /// <summary><c>shipped</c>, <c>learned:&lt;demoCount&gt;</c> or <c>site-only</c>, with <c>+profile</c> when an edit applied.</summary>
    public string Source { get; }

    /// <summary>
    ///     Places the T side crowds at the start of a round (two or more players in seconds 0 to 10 of
    ///     more than 30 percent of rounds), plus <see cref="TSpawn" />. Excluded from learned regions and
    ///     from the default detector's spread.
    /// </summary>
    public IReadOnlySet<string> SpawnAdjacent => _spawn;

    /// <summary>A site's region; the site alone for a site the table does not know.</summary>
    /// <param name="site">A site place.</param>
    public IReadOnlySet<string> RegionOf(string site) =>
        _regions.TryGetValue(site, out HashSet<string>? region) ? region : new HashSet<string>(StringComparer.Ordinal) { site };

    /// <summary>The sites whose region holds <paramref name="place" />, A first. Regions can overlap.</summary>
    /// <param name="place">A place.</param>
    public IReadOnlyList<string> SitesContaining(string place) => [.. Sites.Where(s => RegionOf(s).Contains(place))];

    /// <summary>Each site alone, and the T spawn: what a map with no plants to learn from gets.</summary>
    /// <param name="map">The map.</param>
    public static SiteRegions SiteOnly(string map) => new(
        map,
        Sites.ToDictionary(s => s, s => new HashSet<string>(StringComparer.Ordinal) { s }, StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal) { TSpawn },
        "site-only");

    /// <summary>
    ///     Composes the regions in force: the shipped table when there is one, else the learned table,
    ///     else each site alone; then the profile's edits for the map.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="shipped">A shipped table, or null (none ship in the first release).</param>
    /// <param name="learned">The library's learned table, or null.</param>
    /// <param name="profile">The profile whose <see cref="DetectorProfile.SiteRegionOverrides" /> apply.</param>
    public static SiteRegions Compose(string map, SiteRegionTable? shipped, SiteRegionTable? learned, DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(profile);
        SiteRegionTable? table = shipped ?? learned;
        Dictionary<string, HashSet<string>> regions = new(StringComparer.Ordinal);
        HashSet<string> spawn = new(StringComparer.Ordinal) { TSpawn };
        string source = "site-only";
        if (table is not null)
        {
            foreach ((string site, IReadOnlyList<string> places) in table.Regions)
            {
                regions[site] = new HashSet<string>(places, StringComparer.Ordinal) { site };
            }

            spawn.UnionWith(table.SpawnAdjacent);
            source = ReferenceEquals(table, shipped)
                ? "shipped"
                : string.Create(CultureInfo.InvariantCulture, $"learned:{table.DemoCount}");
        }

        foreach (string site in Sites)
        {
            regions.TryAdd(site, new HashSet<string>(StringComparer.Ordinal) { site });
        }

        if (profile.SiteRegionOverrides.TryGetValue(map, out IReadOnlyDictionary<string, SiteRegionOverride>? edits)
            && edits.Count > 0)
        {
            foreach ((string site, SiteRegionOverride edit) in edits)
            {
                if (!regions.TryGetValue(site, out HashSet<string>? region))
                {
                    region = new HashSet<string>(StringComparer.Ordinal) { site };
                    regions[site] = region;
                }

                region.UnionWith(edit.Add);
                region.ExceptWith(edit.Remove);
            }

            source += "+profile";
        }

        return new SiteRegions(map, regions, spawn, source);
    }
}

/// <summary>
///     One per-map region table as it is stored: <c>site-regions.&lt;map&gt;.json</c> beside the profile
///     (suggested-tags.md §3.2), with the demo count and the plants per site it was learned from, so a
///     reader can tell a table from four demos from one from forty.
/// </summary>
public sealed class SiteRegionTable
{
    /// <summary>The file shape version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The map.</summary>
    public string Map { get; init; } = "";

    /// <summary>Demos the table was learned from; 0 for a shipped table.</summary>
    public int DemoCount { get; init; }

    /// <summary>Rounds the spawn filter counted over.</summary>
    public int RoundCount { get; init; }

    /// <summary>Plants seen per site.</summary>
    public IReadOnlyDictionary<string, int> Plants { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Per site, its region, site first then ordinal order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Regions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The learned spawn-adjacent places, ordinal order.</summary>
    public IReadOnlyList<string> SpawnAdjacent { get; init; } = [];

    /// <summary>The file text: indented, keys in a fixed order, so a re-learn with no change is byte-identical.</summary>
    public string ToJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("map", Map);
            writer.WriteNumber("demoCount", DemoCount);
            writer.WriteNumber("roundCount", RoundCount);
            writer.WriteStartObject("plants");
            foreach ((string site, int count) in Plants.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(site, count);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("regions");
            foreach ((string site, IReadOnlyList<string> places) in Regions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray(site);
                foreach (string place in places)
                {
                    writer.WriteStringValue(place);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteStartArray("spawnAdjacent");
            foreach (string place in SpawnAdjacent)
            {
                writer.WriteStringValue(place);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The table a file's text describes, or null when it is not one this build reads.</summary>
    /// <param name="json">The file text.</param>
    public static SiteRegionTable? TryParse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || (root["schemaVersion"]?.GetValue<int>() ?? 0) is < 1 or > CurrentSchemaVersion)
            {
                return null;
            }

            Dictionary<string, int> plants = new(StringComparer.Ordinal);
            if (root["plants"] is JsonObject plantObject)
            {
                foreach ((string site, JsonNode? count) in plantObject)
                {
                    plants[site] = count?.GetValue<int>() ?? 0;
                }
            }

            Dictionary<string, IReadOnlyList<string>> regions = new(StringComparer.Ordinal);
            if (root["regions"] is JsonObject regionObject)
            {
                foreach ((string site, JsonNode? places) in regionObject)
                {
                    regions[site] = Names(places);
                }
            }

            return new SiteRegionTable
            {
                Map = root["map"]?.GetValue<string>() ?? "",
                DemoCount = root["demoCount"]?.GetValue<int>() ?? 0,
                RoundCount = root["roundCount"]?.GetValue<int>() ?? 0,
                Plants = plants,
                Regions = regions,
                SpawnAdjacent = Names(root["spawnAdjacent"])
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static List<string> Names(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(n => n?.GetValue<string>()).OfType<string>()] : [];
}

/// <summary>
///     Learns a map's site regions from the library (suggested-tags.md §3.2, item 2). For each site, the
///     places T players stood in during the 12 seconds before a plant there are kept when they appear
///     before at least 40 percent of that site's plants and are not spawn-adjacent. The spawn filter is
///     what makes this work: without it the learned set pulled in <c>Outside</c>, <c>Mini</c> and
///     <c>Lobby</c> on de_nuke and the execute detector fired in 12 of 14 non-plant rounds at a median of
///     3 s after freeze end. A site with fewer than four plants learns nothing and is the site alone.
/// </summary>
public static class SiteRegionLearner
{
    /// <summary>Seconds before a plant whose places count toward the site's region.</summary>
    public const int LookbackSeconds = 12;

    /// <summary>The share of a site's plants a place must precede.</summary>
    public const double PlantShare = 0.4;

    /// <summary>Seconds 0 to this, inclusive, are the spawn window.</summary>
    public const int SpawnSeconds = 10;

    /// <summary>T players in one place at once that make it crowded at spawn.</summary>
    public const int SpawnPlayers = 2;

    /// <summary>A place crowded at spawn in more than this share of rounds is spawn-adjacent.</summary>
    public const double SpawnShare = 0.3;

    /// <summary>Plants a site needs before anything is learned for it.</summary>
    public const int MinPlants = 4;

    /// <summary>Learns the table for one map from every demo's rounds on it.</summary>
    /// <param name="map">The map.</param>
    /// <param name="demos">Per demo, its rounds' occupancy with the bomb from Round Facts.</param>
    public static SiteRegionTable Learn(string map, IReadOnlyList<IReadOnlyList<RoundOccupancy>> demos)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(demos);

        int rounds = 0;
        Dictionary<string, int> crowded = new(StringComparer.Ordinal);
        Dictionary<string, int> plants = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, int>> before = new(StringComparer.Ordinal);

        foreach (RoundOccupancy round in demos.SelectMany(d => d))
        {
            rounds++;
            HashSet<string> atSpawn = new(StringComparer.Ordinal);
            for (int s = 0; s <= SpawnSeconds && s < round.Seconds; s++)
            {
                foreach ((string place, int count) in round.CountsAt(2, s))
                {
                    if (count >= SpawnPlayers)
                    {
                        atSpawn.Add(place);
                    }
                }
            }

            foreach (string place in atSpawn)
            {
                crowded[place] = crowded.GetValueOrDefault(place) + 1;
            }

            if (round.Bomb is not { Site: { } site } bomb)
            {
                continue;
            }

            plants[site] = plants.GetValueOrDefault(site) + 1;
            int plantSecond = round.SecondAt(bomb.PlantTick);
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int s = Math.Max(0, plantSecond - LookbackSeconds); s < plantSecond && s < round.Seconds; s++)
            {
                seen.UnionWith(round.CountsAt(2, s).Keys);
            }

            if (!before.TryGetValue(site, out Dictionary<string, int>? counts))
            {
                counts = new Dictionary<string, int>(StringComparer.Ordinal);
                before[site] = counts;
            }

            foreach (string place in seen)
            {
                counts[place] = counts.GetValueOrDefault(place) + 1;
            }
        }

        List<string> spawn =
        [
            .. crowded
                .Where(c => rounds > 0 && c.Value / (double)rounds > SpawnShare)
                .Select(c => c.Key)
                .Order(StringComparer.Ordinal)
        ];
        HashSet<string> spawnSet = new(spawn, StringComparer.Ordinal) { SiteRegions.TSpawn };

        Dictionary<string, IReadOnlyList<string>> regions = new(StringComparer.Ordinal);
        foreach (string site in SiteRegions.Sites)
        {
            int planted = plants.GetValueOrDefault(site);
            List<string> region = [site];
            if (planted >= MinPlants && before.TryGetValue(site, out Dictionary<string, int>? counts))
            {
                region.AddRange(counts
                    .Where(c => c.Key != site && !spawnSet.Contains(c.Key) && c.Value / (double)planted >= PlantShare)
                    .Select(c => c.Key)
                    .Order(StringComparer.Ordinal));
            }

            regions[site] = region;
        }

        return new SiteRegionTable
        {
            Map = map,
            DemoCount = demos.Count,
            RoundCount = rounds,
            Plants = plants,
            Regions = regions,
            SpawnAdjacent = spawn
        };
    }
}

/// <summary>
///     The learned tables on disk: <c>&lt;config&gt;/suggested-tags/site-regions.&lt;map&gt;.json</c>,
///     beside the profile. Derived data a team may still want to read and share, so it lives with the
///     profile rather than in the cache. Whole-file atomic writes; a null directory (the browser,
///     tests) keeps the tables for the session only, the way the cache's in-memory mode does.
/// </summary>
public sealed class SiteRegionStore
{
    private readonly string? _directory;
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <param name="directory">The suggested-tags config directory, or null for a session-only store.</param>
    public SiteRegionStore(string? directory)
    {
        _directory = directory;
    }

    /// <summary>The file name for a map's table.</summary>
    /// <param name="map">The map.</param>
    /// <exception cref="ArgumentException">The map name cannot be part of a file name.</exception>
    public static string FileNameFor(string map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(map);
        if (map.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || map.Contains('/', StringComparison.Ordinal)
                                                                 || map.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{map}' cannot name a file", nameof(map));
        }

        return $"site-regions.{map}.json";
    }

    /// <summary>The stored table for a map, or null when there is none or it cannot be read.</summary>
    /// <param name="map">The map.</param>
    public SiteRegionTable? TryLoad(string map)
    {
        string name = FileNameFor(map);
        lock (_gate)
        {
            if (_directory is null)
            {
                return _memory.TryGetValue(name, out string? json) ? SiteRegionTable.TryParse(json) : null;
            }
        }

        string path = Path.Combine(_directory, name);
        try
        {
            return File.Exists(path) ? SiteRegionTable.TryParse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a map's table, replacing what was there.</summary>
    /// <param name="table">The table.</param>
    public void Save(SiteRegionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        string name = FileNameFor(table.Map);
        string json = table.ToJson();
        lock (_gate)
        {
            if (_directory is null)
            {
                _memory[name] = json;
                return;
            }
        }

        Directory.CreateDirectory(_directory);
        DemoCacheStore.WriteAtomic(Path.Combine(_directory, name), json + "\n");
    }
}
