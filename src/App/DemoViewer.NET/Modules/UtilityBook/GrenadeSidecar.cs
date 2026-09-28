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
///     <c>demos/&lt;key&gt;.grenades.json.gz</c>: the header and one row per grenade without its trajectory,
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
///     <c>demos/&lt;key&gt;.grenades.paths.json.gz</c>: the same header and each row's trajectory by id, at
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
    public const string Suffix = ".grenades.json.gz";

    /// <summary>The paths sibling's suffix.</summary>
    public const string PathsSuffix = ".grenades.paths.json.gz";

    /// <summary>The rows sibling as written before gzip. Read when <see cref="Suffix" /> is absent.</summary>
    public const string LegacySuffix = ".grenades.json";

    /// <summary>The paths sibling as written before gzip. Read when <see cref="PathsSuffix" /> is absent.</summary>
    public const string LegacyPathsSuffix = ".grenades.paths.json";

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
                                                     || Read<GrenadeDocument>(cache, path, Suffix, LegacySuffix)
                                                         is not { SchemaVersion: CurrentSchema } document)
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
                                                     || Read<GrenadePathsDocument>(cache, path, PathsSuffix, LegacyPathsSuffix)
                                                         is not { SchemaVersion: CurrentSchema } document)
        {
            return null;
        }

        return SameDemo(entry.Sha256, document.Demo.Sha256) ? document : null;
    }

    /// <summary>
    ///     Writes both siblings gzipped, paths first. The caller stamps the record next and then calls
    ///     <see cref="DeleteLegacy" />.
    /// </summary>
    public static void Write(DemoCacheStore cache, string path, GrenadeDocument rows, GrenadePathsDocument paths)
    {
        ArgumentNullException.ThrowIfNull(cache);
        lock (cache.StripeFor(path))
        {
            cache.WriteSiblingBytes(path, PathsSuffix, SidecarJson.SerializeGzip(paths, JsonOptions));
            cache.WriteSiblingBytes(path, Suffix, SidecarJson.SerializeGzip(rows, JsonOptions));
        }
    }

    /// <summary>
    ///     Writes the rows sibling gzipped and removes a paths sibling a previous walk left: nothing reads
    ///     trajectories from disk. The caller stamps the record next and then calls <see cref="DeleteLegacy" />.
    /// </summary>
    public static void WriteRows(DemoCacheStore cache, string path, GrenadeDocument rows)
    {
        ArgumentNullException.ThrowIfNull(cache);
        lock (cache.StripeFor(path))
        {
            cache.WriteSiblingBytes(path, Suffix, SidecarJson.SerializeGzip(rows, JsonOptions));
            cache.DeleteSibling(path, PathsSuffix);
            cache.DeleteSibling(path, LegacyPathsSuffix);
        }
    }

    /// <summary>
    ///     Removes the pre-gzip siblings of one demo once the new ones read back. Call after the new pair is
    ///     written and stamped. False when a new file did not verify; the legacy files are then kept.
    /// </summary>
    public static bool DeleteLegacy(DemoCacheStore cache, string path)
    {
        ArgumentNullException.ThrowIfNull(cache);
        lock (cache.StripeFor(path))
        {
            string? sha = cache.TryGetIndex(path)?.Sha256;
            if (!Verify<GrenadeDocument>(cache, path, Suffix, sha, d => d.SchemaVersion, d => d.Demo.Sha256))
            {
                return false;
            }

            if (cache.TryReadSiblingBytes(path, PathsSuffix) is null
                || Verify<GrenadePathsDocument>(cache, path, PathsSuffix, sha, d => d.SchemaVersion, d => d.Demo.Sha256))
            {
                cache.DeleteSibling(path, LegacyPathsSuffix);
            }

            cache.DeleteSibling(path, LegacySuffix);
            return true;
        }
    }

    /// <summary>
    ///     Re-encodes a demo's pre-gzip siblings as gzip without a walk. Each legacy file goes only after
    ///     its new file reads back; one that does not read, or is another demo's, is kept.
    /// </summary>
    internal static SidecarConversion ConvertLegacy(DemoCacheStore cache, string path)
    {
        ArgumentNullException.ThrowIfNull(cache);
        lock (cache.StripeFor(path))
        {
            if (cache.TryGetIndex(path) is not { } entry)
            {
                return SidecarConversion.None;
            }

            SidecarConversion paths = ConvertOne<GrenadePathsDocument>(cache, path, entry.Sha256, PathsSuffix, LegacyPathsSuffix,
                d => d.SchemaVersion, d => d.Demo.Sha256);
            SidecarConversion rows = ConvertOne<GrenadeDocument>(cache, path, entry.Sha256, Suffix, LegacySuffix,
                d => d.SchemaVersion, d => d.Demo.Sha256);
            return paths == SidecarConversion.Failed || rows == SidecarConversion.Failed ? SidecarConversion.Failed
                : paths == SidecarConversion.Converted || rows == SidecarConversion.Converted ? SidecarConversion.Converted
                : SidecarConversion.None;
        }
    }

    // Under the demo's stripe.
    private static SidecarConversion ConvertOne<T>(DemoCacheStore cache, string path, string? sha, string suffix,
        string legacySuffix, Func<T, int> schema, Func<T, string?> fileSha) where T : class
    {
        if (cache.TryReadSiblingBytes(path, legacySuffix) is not { } raw)
        {
            return SidecarConversion.None;
        }

        if (Verify(cache, path, suffix, sha, schema, fileSha))
        {
            cache.DeleteSibling(path, legacySuffix);
            return SidecarConversion.Converted;
        }

        T? legacy;
        try
        {
            legacy = SidecarJson.Deserialize<T>(raw, JsonOptions);
        }
        catch (Exception)
        {
            legacy = null;
        }

        if (legacy is null || schema(legacy) != CurrentSchema || !SameDemo(sha, fileSha(legacy)))
        {
            return SidecarConversion.Failed;
        }

        cache.WriteSiblingBytes(path, suffix, SidecarJson.Gzip(SidecarJson.Minify(raw)));
        if (!Verify(cache, path, suffix, sha, schema, fileSha))
        {
            cache.DeleteSibling(path, suffix);
            return SidecarConversion.Failed;
        }

        cache.DeleteSibling(path, legacySuffix);
        return SidecarConversion.Converted;
    }

    // The gzipped sibling alone, with the reader's schema and hash checks.
    private static bool Verify<T>(DemoCacheStore cache, string path, string suffix, string? sha, Func<T, int> schema,
        Func<T, string?> fileSha) where T : class =>
        cache.TryReadSiblingJson(path, suffix, JsonOptions, out T? value)
        && value is not null && schema(value) == CurrentSchema && SameDemo(sha, fileSha(value));

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

    // The gzipped sibling when it reads; else the pre-gzip one, the last good write.
    private static T? Read<T>(DemoCacheStore cache, string path, string suffix, string legacySuffix) where T : class =>
        cache.TryReadSiblingJson(path, suffix, JsonOptions, out T? value) && value is not null
            ? value
            : cache.TryReadSiblingJson(path, legacySuffix, JsonOptions, out T? legacy) ? legacy : null;

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
