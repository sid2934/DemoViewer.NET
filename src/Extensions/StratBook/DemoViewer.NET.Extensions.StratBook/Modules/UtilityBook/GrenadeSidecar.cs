#region

using DemoViewer.NET.Extensions.StratBook;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>How the rows were made: the header block both grenade siblings carry.</summary>
public sealed class GrenadeWalkerHeader
{
    /// <summary><see cref="GrenadeWalker.Version" />.</summary>
    public string Version { get; set; } = "";

    /// <summary>The engine package the walk ran on, e.g. <c>CS2DemoKit.Parser 0.13.0-beta0001</c>.</summary>
    public string Engine { get; set; } = "";

    /// <summary>The input source's name; a better decoder later is what re-indexes a demo for jump-throw flags.</summary>
    public string InputDecoder { get; set; } = "";
}

/// <summary>Which demo the rows belong to.</summary>
public sealed class GrenadeDemoHeader
{
    /// <summary>Null until Content Identity has hashed the demo; a reader with a hash ignores a mismatching file.</summary>
    public string? Sha256 { get; set; }

    /// <summary><c>DemoKeys.StableKey</c> of the path.</summary>
    public string StableKey { get; set; } = "";

    public string FileName { get; set; } = "";

    public long SizeBytes { get; set; }
}

/// <summary>Where the demo came from and how its inputs and paths were read.</summary>
public sealed class GrenadeSourceHeader
{
    /// <summary>The demo's <c>DemoSourceKind</c> by name, or null when the parse did not classify it.</summary>
    public string? Kind { get; set; }

    public int Build { get; set; }

    /// <summary>Share of the demo's commands that decoded; the jump-throw flag comes from inputs this often.</summary>
    public double InputCoverage { get; set; }

    public int TrajectoryStride { get; set; }
}

/// <summary>
///     The header and one row per grenade without its trajectory, kept as the throw log in the
///     <see cref="GrenadeStore" />. Read across the library by the Grenade Index and per demo by the
///     Lineup Cards.
/// </summary>
public sealed class GrenadeDocument
{
    public int SchemaVersion { get; set; } = GrenadeSidecar.CurrentSchema;

    public GrenadeWalkerHeader Walker { get; set; } = new();

    public GrenadeDemoHeader Demo { get; set; } = new();

    /// <summary>The <c>dv-frame-clock</c> header, the annotation sidecar's block verbatim.</summary>
    public RoundFactsClock Clock { get; set; } = new();

    public GrenadeSourceHeader Source { get; set; } = new();

    public List<GrenadeRow> Grenades { get; set; } = [];
}

/// <summary>
///     The same header and each row's trajectory by id, at the configured stride with the bounce vertices
///     kept. Built with the rows; the walk hands the flights to the lineup store and nothing writes them per demo.
/// </summary>
public sealed class GrenadePathsDocument
{
    public int SchemaVersion { get; set; } = GrenadeSidecar.CurrentSchema;

    public GrenadeWalkerHeader Walker { get; set; } = new();

    public GrenadeDemoHeader Demo { get; set; } = new();

    public RoundFactsClock Clock { get; set; } = new();

    public GrenadeSourceHeader Source { get; set; } = new();

    public Dictionary<string, List<TrajectoryPoint>> Paths { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
///     Building a demo's grenade documents from a walk, and the rules a reader holds them to: a hash that
///     differs from the library's is another demo's and is ignored, the annotation rule.
/// </summary>
public static class GrenadeSidecar
{
    /// <summary>The shape of both documents.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Camel case like the other sidecars, compact, enums by name, nulls written.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Builds both documents for a walk of <paramref name="parsed" />.</summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="record">The demo's library row, for its hash, size and source; null when there is none yet.</param>
    /// <param name="parsed">The held parse, for the clock and the source.</param>
    /// <param name="walk">The walk's output.</param>
    public static (GrenadeDocument Rows, GrenadePathsDocument Paths) Build(string path, LibraryDemo? record,
        ParsedDemo parsed, GrenadeWalk walk)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(walk);

        GrenadeWalkerHeader walker = new()
        {
            Version = GrenadeWalker.Version,
            Engine = GrenadeWalker.EngineVersion,
            InputDecoder = walk.InputDecoder
        };
        RoundFactsClock clock = RoundFactsClock.For(parsed);
        GrenadeSourceHeader source = new()
        {
            Kind = record?.SourceKind,
            Build = parsed.BuildNumber,
            InputCoverage = Math.Round(walk.InputCoverage, 4),
            TrajectoryStride = walk.TrajectoryStride
        };

        GrenadeDocument rows = new()
        {
            Walker = walker,
            Demo = DemoHeader(path, record),
            Clock = clock,
            Source = source,
            Grenades = [.. walk.Rows]
        };
        GrenadePathsDocument paths = new()
        {
            Walker = walker,
            Demo = DemoHeader(path, record),
            Clock = clock,
            Source = source
        };
        foreach (GrenadeRow row in walk.Rows)
        {
            paths.Paths[row.Id] = row.Trajectory;
        }

        return (rows, paths);
    }

    /// <summary>The compact JSON of a rows document.</summary>
    public static string Serialize(GrenadeDocument document) => JsonSerializer.Serialize(document, JsonOptions);

    /// <summary>The compact JSON of a paths document.</summary>
    public static string Serialize(GrenadePathsDocument document) => JsonSerializer.Serialize(document, JsonOptions);

    /// <summary>A rows document from its text, or null when it does not parse or is another schema.</summary>
    public static GrenadeDocument? TryDeserializeRows(string json) =>
        TryDeserialize<GrenadeDocument>(json) is { SchemaVersion: CurrentSchema } document ? document : null;

    /// <summary>A paths document from its text, or null when it does not parse or is another schema.</summary>
    public static GrenadePathsDocument? TryDeserializePaths(string json) =>
        TryDeserialize<GrenadePathsDocument>(json) is { SchemaVersion: CurrentSchema } document ? document : null;

    /// <summary>Fills each row's thrower name from the record's players, by SteamID and then by slot.</summary>
    internal static void Name(GrenadeDocument document, IReadOnlyList<LibraryPlayer>? players)
    {
        if (players is null)
        {
            return;
        }

        Dictionary<string, string> bySteam = new(StringComparer.Ordinal);
        Dictionary<int, string> bySlot = [];
        foreach (LibraryPlayer player in players)
        {
            if (player.Name.Length == 0)
            {
                continue;
            }

            if (player.SteamId64 != 0)
            {
                bySteam.TryAdd(DemoKeys.SteamIdText(player.SteamId64), player.Name);
            }

            bySlot.TryAdd(player.Slot, player.Name);
        }

        foreach (GrenadeRow row in document.Grenades)
        {
            row.ThrowerName ??= row.ThrowerSteamId64 is { } steam && bySteam.TryGetValue(steam, out string? name) ? name
                : row.ThrowerSlot >= 0 ? bySlot.GetValueOrDefault(row.ThrowerSlot)
                : null;
        }
    }

    /// <summary>A file with no hash is path-keyed only and is accepted; two hashes must agree.</summary>
    public static bool SameDemo(string? recordSha256, string? fileSha256) =>
        string.IsNullOrEmpty(recordSha256) || string.IsNullOrEmpty(fileSha256)
                                           || string.Equals(recordSha256, fileSha256, StringComparison.OrdinalIgnoreCase);

    private static GrenadeDemoHeader DemoHeader(string path, LibraryDemo? record) => new()
    {
        Sha256 = record?.Sha256,
        StableKey = DemoKeys.StableKey(path),
        FileName = Path.GetFileName(path),
        SizeBytes = record?.FileSizeBytes ?? 0
    };

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
