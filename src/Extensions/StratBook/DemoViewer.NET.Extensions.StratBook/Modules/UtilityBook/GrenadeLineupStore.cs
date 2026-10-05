#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

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
///     The persisted lineup identities, alias maps and representative flights, one gzipped JSON file in the
///     cache root. A host with no cache root (the browser) keeps them in memory. Not thread-safe: the
///     <see cref="GrenadeIndex" /> calls it under its own lock.
/// </summary>
public sealed class GrenadeLineupStore
{
    public const string FileName = "grenade-lineups.json.gz";

    private readonly string? _file;
    private GrenadeLineupDocument? _loaded;

    /// <param name="cacheRoot">The demo cache root, or null to keep everything in memory.</param>
    public GrenadeLineupStore(string? cacheRoot)
    {
        _file = cacheRoot is null ? null : Path.Combine(cacheRoot, FileName);
    }

    // Read on first use, not in the constructor: the file holds every flight and the index is built on the UI thread.
    private GrenadeLineupDocument Doc => _loaded ??= Read(_file) ?? new GrenadeLineupDocument();

    /// <summary>The store for <paramref name="cache" />'s root.</summary>
    public static GrenadeLineupStore For(DemoCacheStore cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return new GrenadeLineupStore(cache.CacheRoot);
    }

    /// <summary>Drops the in-memory document; the next use reads the file again. In memory this empties the store.</summary>
    public void Unload() => _loaded = null;

    /// <summary>True when the file exists (always false in memory).</summary>
    public bool Exists => _file is not null && File.Exists(_file);

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

    /// <summary>Writes the file (temp plus replace). A no-op in memory.</summary>
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
        if (_file is not null)
        {
            AtomicFile.WriteAllBytes(_file, SidecarJson.SerializeGzip(snapshot, GrenadeSidecar.JsonOptions));
        }
    }

    /// <summary>Re-reads the file, as a check that a save reads back. False when it does not.</summary>
    public bool ReadsBack()
    {
        if (_file is null)
        {
            return true;
        }

        GrenadeLineupDocument? back = Read(_file);
        return back is not null && back.Maps.Count == Doc.Maps.Count
                                && back.Maps.All(m => Doc.Maps.TryGetValue(m.Key, out MapLineups? mine)
                                                      && mine.Anchors.Count == m.Value.Anchors.Count
                                                      && mine.Paths.Count == m.Value.Paths.Count
                                                      && mine.Aliases.Count == m.Value.Aliases.Count);
    }

    private static GrenadeLineupDocument? Read(string? file)
    {
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        try
        {
            if (SidecarJson.ReadFile<GrenadeLineupDocument>(file, GrenadeSidecar.JsonOptions) is not
                { SchemaVersion: GrenadeLineupDocument.CurrentSchema } document)
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
        catch (Exception)
        {
            return null;
        }
    }
}
