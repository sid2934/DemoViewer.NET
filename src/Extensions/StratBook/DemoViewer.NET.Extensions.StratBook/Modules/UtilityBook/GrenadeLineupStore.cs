#region

using System.IO.Compression;
using System.Text.Json;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     A lineup's fixed identity: where it is thrown from and where it lands, as first seen. Never moves, so
///     a throw added later joins the same id.
/// </summary>
/// <param name="Id">The lineup id strat steps, mined patterns and clip files store.</param>
/// <param name="Kind">What is thrown.</param>
/// <param name="Origin">The members' mean release point when it was minted.</param>
/// <param name="Landing">The members' mean landing when it was minted.</param>
/// <param name="Seed">The key of the throw it was seeded from.</param>
public sealed record LineupAnchor(Guid Id, GrenadeKind Kind, WorldPoint Origin, WorldPoint Landing, string Seed);

/// <summary>One position's representative flight: the throw it came from and its trajectory.</summary>
/// <param name="Throw">The throw's <see cref="IndexedGrenade.Key" />.</param>
/// <param name="Points">The flight at the walk's stride.</param>
public sealed record LineupPath(string Throw, IReadOnlyList<TrajectoryPoint> Points);

/// <summary>One map's lineups.</summary>
public sealed class MapLineups
{
    public List<LineupAnchor> Anchors { get; set; } = [];

    /// <summary>Old (grid) lineup id to the one lineup that took most of its throws. Built once per map, then frozen.</summary>
    public Dictionary<Guid, Guid> Aliases { get; set; } = [];

    /// <summary>True once <see cref="Aliases" /> has been built from this map's rows.</summary>
    public bool AliasesBuilt { get; set; }

    /// <summary>Representative flights by <c>{lineupId:N}/{technique}</c>.</summary>
    public Dictionary<string, LineupPath> Paths { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The file: every map's lineups.</summary>
public sealed class GrenadeLineupDocument
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public Dictionary<string, MapLineups> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     The persisted lineup identities, alias maps and representative flights: one gzipped JSON file. In the app
///     it is a file of the extension's own cache folder, copied once from where an older build kept it, since
///     it holds hand-tuned state a rebuild would lose. A store with nowhere to write (the browser) keeps them in
///     memory. Not thread-safe: the <see cref="GrenadeIndex" /> calls it under its own lock.
/// </summary>
public sealed class GrenadeLineupStore
{
    public const string FileName = "grenade-lineups.json.gz";

    private readonly Func<byte[]?> _read;
    private readonly Action<byte[]>? _write;
    private GrenadeLineupDocument? _loaded;

    /// <param name="directory">The folder holding <see cref="FileName" />, or null to keep everything in memory.</param>
    public GrenadeLineupStore(string? directory)
        : this(directory is null ? () => null : () => ReadFile(Path.Combine(directory, FileName)),
            directory is null ? null : bytes => AtomicFile.WriteAllBytes(Path.Combine(directory, FileName), bytes))
    {
    }

    private GrenadeLineupStore(Func<byte[]?> read, Action<byte[]>? write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>
    ///     The store in the extension's own cache folder. When that has no file yet and <paramref name="legacyFile" />
    ///     reads as a lineup document, its bytes are copied over first. The browser build, which has no folders,
    ///     keeps the lineups in memory.
    /// </summary>
    /// <param name="storage">The extension's files.</param>
    /// <param name="legacyFile">Where an older build kept the file, or null.</param>
    public static GrenadeLineupStore In(IExtensionStorage storage, string? legacyFile)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return new GrenadeLineupStore(() =>
        {
            byte[]? bytes = storage.ReadAsync(StoreRoot.Cache, FileName).GetAwaiter().GetResult();
            if (bytes is not null || legacyFile is null || ReadFile(legacyFile) is not { } legacy || Parse(legacy) is null)
            {
                return bytes;
            }

            storage.WriteAtomicAsync(StoreRoot.Cache, FileName, legacy).GetAwaiter().GetResult();
            return legacy;
        }, bytes => storage.WriteAtomicAsync(StoreRoot.Cache, FileName, bytes).GetAwaiter().GetResult());
    }

    // Read on first use, not in the constructor: the file holds every flight and the index is built on the UI thread.
    private GrenadeLineupDocument Doc => _loaded ??= Read() ?? new GrenadeLineupDocument();

    /// <summary>Drops the in-memory document; the next use reads the file again. In memory this empties the store.</summary>
    public void Unload() => _loaded = null;

    /// <summary>A map's lineups, created empty on first use.</summary>
    public MapLineups For(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!Doc.Maps.TryGetValue(map, out MapLineups? lineups))
        {
            lineups = new MapLineups();
            Doc.Maps[map] = lineups;
        }

        return lineups;
    }

    /// <summary>Writes the file whole. A no-op in memory.</summary>
    public void Save() => Write(Snapshot());

    /// <summary>
    ///     A copy of the document that later edits do not reach, for writing without the index's lock. The
    ///     anchors and paths are immutable records, so copying the collections is enough.
    /// </summary>
    public GrenadeLineupDocument Snapshot()
    {
        GrenadeLineupDocument copy = new() { SchemaVersion = Doc.SchemaVersion };
        foreach ((string map, MapLineups lineups) in Doc.Maps)
        {
            copy.Maps[map] = new MapLineups
            {
                Anchors = [.. lineups.Anchors],
                Aliases = new Dictionary<Guid, Guid>(lineups.Aliases),
                AliasesBuilt = lineups.AliasesBuilt,
                Paths = new Dictionary<string, LineupPath>(lineups.Paths, StringComparer.Ordinal)
            };
        }

        return copy;
    }

    /// <summary>Writes a <see cref="Snapshot" />. Thread-safe with respect to the live document.</summary>
    public void Write(GrenadeLineupDocument snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_write is null)
        {
            return;
        }

        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Optimal, true))
        {
            JsonSerializer.Serialize(gzip, snapshot, GrenadeSidecar.JsonOptions);
        }

        _write(buffer.ToArray());
    }

    /// <summary>Re-reads the file, as a check that a save reads back. False when it does not.</summary>
    public bool ReadsBack()
    {
        if (_write is null)
        {
            return true;
        }

        GrenadeLineupDocument? back = Read();
        return back is not null && back.Maps.Count == Doc.Maps.Count
                                && back.Maps.All(m => Doc.Maps.TryGetValue(m.Key, out MapLineups? mine)
                                                      && mine.Anchors.Count == m.Value.Anchors.Count
                                                      && mine.Paths.Count == m.Value.Paths.Count
                                                      && mine.Aliases.Count == m.Value.Aliases.Count);
    }

    private GrenadeLineupDocument? Read()
    {
        try
        {
            return _read() is { } bytes ? Parse(bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? ReadFile(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Gzipped or plain, sniffed by the magic bytes.
    private static GrenadeLineupDocument? Parse(byte[] bytes)
    {
        try
        {
            GrenadeLineupDocument? document;
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using GZipStream gzip = new(new MemoryStream(bytes), CompressionMode.Decompress);
                document = JsonSerializer.Deserialize<GrenadeLineupDocument>(gzip, GrenadeSidecar.JsonOptions);
            }
            else
            {
                document = JsonSerializer.Deserialize<GrenadeLineupDocument>(bytes, GrenadeSidecar.JsonOptions);
            }

            if (document is not { SchemaVersion: GrenadeLineupDocument.CurrentSchema })
            {
                return null;
            }

            // The reader builds dictionaries with the default comparers.
            document.Maps = new Dictionary<string, MapLineups>(document.Maps, StringComparer.OrdinalIgnoreCase);
            foreach (MapLineups map in document.Maps.Values)
            {
                map.Paths = new Dictionary<string, LineupPath>(map.Paths, StringComparer.Ordinal);
            }

            return document;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }
}
