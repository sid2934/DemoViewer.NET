#region

using System.Text.Json;
using System.Text.Json.Serialization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

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

    /// <summary><c>DemoCacheStore.StableKey</c> of the path.</summary>
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
///     <c>demos/&lt;key&gt;.grenades.json</c>: the header and one row per grenade without its trajectory,
///     about 120 KB at 300 grenades. Read across the library by the Grenade Index and per demo by the
///     Lineup Cards (grenade-walk.md §3.9).
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
///     <c>demos/&lt;key&gt;.grenades.paths.json</c>: the same header and each row's trajectory by id, at
///     the configured stride with the bounce vertices kept. Read one demo at a time, by the card's radar path
///     and the clip render.
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
///     The grenade siblings of a demo's cache record: building them from a walk, and reading them back
///     under the reader's rules (the stamp on the index row says they are current; a hash that differs from
///     the row's is another demo's file and is ignored, the annotation rule).
/// </summary>
public static class GrenadeSidecar
{
    /// <summary>The rows sibling's suffix.</summary>
    public const string Suffix = ".grenades.json";

    /// <summary>The paths sibling's suffix.</summary>
    public const string PathsSuffix = ".grenades.paths.json";

    /// <summary>The shape of both files; equal to <see cref="DemoCacheRecord.GrenadeSchema" />.</summary>
    public const int CurrentSchema = DemoCacheRecord.GrenadeSchema;

    /// <summary>Camel case like the other sidecars, compact, enums by name, nulls written.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Builds both documents for a walk of <paramref name="parsed" />.</summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="record">The demo's cache record, for its hash and size; null when there is none yet.</param>
    /// <param name="parsed">The held parse, for the clock and the source.</param>
    /// <param name="walk">The walk's output.</param>
    public static (GrenadeDocument Rows, GrenadePathsDocument Paths) Build(string path, DemoCacheRecord? record,
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
        RoundFactsClock clock = RoundFactsClock.From(FrameClock.IdentityFor(parsed));
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

    /// <summary>
    ///     A demo's rows, or null when the index row does not say they are current, the file is missing or
    ///     does not parse, or its hash names another demo than the row's.
    /// </summary>
    /// <param name="cache">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    public static GrenadeDocument? TryReadRows(DemoCacheStore cache, string path)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.TryGetIndex(path) is not { } entry || !entry.IsGrenadesCurrent(GrenadeWalker.Version)
                                                     || cache.TryReadSibling(path, Suffix) is not { } json
                                                     || TryDeserializeRows(json) is not { } document)
        {
            return null;
        }

        return SameDemo(entry.Sha256, document.Demo.Sha256) ? document : null;
    }

    /// <summary>A demo's trajectories, under the same rules as <see cref="TryReadRows" />.</summary>
    /// <param name="cache">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    public static GrenadePathsDocument? TryReadPaths(DemoCacheStore cache, string path)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.TryGetIndex(path) is not { } entry || !entry.IsGrenadesCurrent(GrenadeWalker.Version)
                                                     || cache.TryReadSibling(path, PathsSuffix) is not { } json
                                                     || TryDeserializePaths(json) is not { } document)
        {
            return null;
        }

        return SameDemo(entry.Sha256, document.Demo.Sha256) ? document : null;
    }

    /// <summary>A file with no hash is path-keyed only and is accepted; two hashes must agree.</summary>
    public static bool SameDemo(string? recordSha256, string? fileSha256) =>
        string.IsNullOrEmpty(recordSha256) || string.IsNullOrEmpty(fileSha256)
                                           || string.Equals(recordSha256, fileSha256, StringComparison.OrdinalIgnoreCase);

    private static GrenadeDemoHeader DemoHeader(string path, DemoCacheRecord? record) => new()
    {
        Sha256 = record?.Sha256,
        StableKey = DemoCacheStore.StableKey(path),
        FileName = Path.GetFileName(path),
        SizeBytes = record?.Size ?? 0
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
