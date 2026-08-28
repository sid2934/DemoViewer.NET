#region

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     The unified demo-information cache — one tiered record per demo, replacing the overlapping
///     <c>library.json</c> and <c>highlights.json</c> stores.
///     <para>
///         <b>Storage shape.</b> A thin always-loaded <c>index.json</c> plus one lazily-read sidecar
///         per demo under <c>demos/</c>. The surfaces that want the fat payload — Match Overview, the reel
///         tray — want it for exactly ONE demo at a time, which is what "Match Overview is a cache render"
///         means; the Library grid wants a small projection of all of them. A monolith serves neither well: it
///         deserializes in full at every app start regardless of which demo the user cares about, and a
///         library backfill rewrites the whole growing file after every single demo.
///     </para>
///     <para>
///         <b>Durability.</b> Every write is atomic (temp file + replace), inherited from
///         <c>HighlightsCacheStore</c>, whose own note is the reason: a crash mid-write must never destroy an
///         hour of scan progress. Beyond atomicity, I/O failure is swallowed — this cache is rebuildable and
///         is never a source of truth.
///     </para>
///     <para>
///         <b>WASM.</b> <see cref="AppPaths.ConfigRoot" /> is null on the browser host, so the store runs
///         fully in-memory: nothing is loaded, nothing is written, and every API still works.
///     </para>
/// </summary>
public sealed class DemoCacheStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string? _cacheRoot;
    private readonly object _gate = new();

    // The always-loaded projection, keyed by demo path (the same case-insensitive keying the library and
    // highlights caches both use — macOS and Windows default filesystems are case-insensitive).
    private readonly Dictionary<string, DemoCacheIndexEntry> _index =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Record store used when there is no cache root — the browser host, and tests.
    ///     <para>
    ///         <b>Not a test convenience.</b> Without it the no-root mode can hold exactly ONE record: writes
    ///         go nowhere and reads are served only by the capacity-1 JSON cache, so the second demo upserted
    ///         evicts the first and <see cref="TryLoadRecord" /> returns null for it forever. Every surface
    ///         that resolves more than one demo — the Reels clip tray is cross-demo BY DEFINITION — silently
    ///         loses all but the most recent. The class contract already promised "nothing is written and
    ///         every API still works"; this is what makes the second half true.
    ///     </para>
    /// </summary>
    private readonly Dictionary<string, string> _memoryRecords = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Action> _post;

    /// <summary>
    ///     Serializes whole read-modify-write cycles (<see cref="Update" /> / <see cref="UpdateExisting" />).
    ///     Distinct from <see cref="_gate" />, which guards short index/cache critical sections only and is
    ///     taken INSIDE this one by the load and upsert steps.
    ///     <para>
    ///         Needed because one demo has several tier writers running concurrently — an interactive open
    ///         fires the highlights mirror (off-thread, from <c>OnOpenDemoEvaluated</c>) and the scoreboard
    ///         write at nearly the same moment, on the same record. Without this, both read the pre-write
    ///         record, both mutate their own copy, and whichever upserts last silently erases the other's
    ///         tier — losing exactly the highlights this cache was fixed to store.
    ///     </para>
    /// </summary>
    private readonly object _rmwGate = new();

    private int _batchDepth; // under _gate
    private bool _batchDirty; // under _gate

    // Capacity-1 record cache. Match Overview re-reads the same record on every property touch while a demo
    // is selected, and arrow-keying the Library grid walks one demo at a time — so remembering exactly the
    // last one collapses the common case to zero I/O without holding a library's worth of records live.
    // (The same capacity-1 idiom the demo GetOrParse cache uses, for the same reason.)
    //
    // Cached as JSON TEXT, not as a live object, and every read deserializes a fresh instance. Handing out a
    // shared mutable record would let a UI-thread reader (Match Overview, rendering the selected demo) watch
    // fields change under it while a background tier-2 pass mutates the same instance through Update — a
    // torn read with no lock a caller could reasonably take. The cache still spares the disk I/O, which is
    // what it was for; a few KB of deserialization per selection change is not worth a data race.
    private string? _lastRecordJson;
    private string? _lastRecordPath;
    private int _legacyMigrationVersion; // under _gate

    /// <param name="cacheRoot">
    ///     The cache directory (<c>&lt;config&gt;/cache</c>), or null for an in-memory store (WASM, and tests
    ///     that do not care about persistence).
    /// </param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    public DemoCacheStore(string? cacheRoot, Action<Action>? post = null)
    {
        _cacheRoot = cacheRoot;
        _post = post ?? (action => action());
        LoadIndex();
    }

    /// <summary>A point-in-time snapshot of every index row (safe to enumerate off-lock).</summary>
    public IReadOnlyList<DemoCacheIndexEntry> Index
    {
        get
        {
            lock (_gate)
            {
                return [.. _index.Values];
            }
        }
    }

    /// <summary>Number of demos known to the index.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>
    ///     Which revision of the one-shot legacy migration has already run against this cache. See
    ///     <see cref="DemoCacheIndexFile.LegacyMigrationVersion" /> for why this is an explicit marker rather
    ///     than a file-existence check.
    /// </summary>
    public int LegacyMigrationVersion
    {
        get
        {
            lock (_gate)
            {
                return _legacyMigrationVersion;
            }
        }
        set
        {
            lock (_gate)
            {
                _legacyMigrationVersion = value;
            }
        }
    }

    private string? IndexPath => _cacheRoot is null ? null : Path.Combine(_cacheRoot, "index.json");

    private string? SidecarDir => _cacheRoot is null ? null : Path.Combine(_cacheRoot, "demos");

    /// <summary>
    ///     Raised (via the post delegate) after any mutation — or, inside a <see cref="BeginBatch" /> scope,
    ///     exactly once when the scope closes. Consumers re-project wholesale per event, so an O(library) pass
    ///     MUST batch or it becomes an O(n²) re-projection storm on the dispatcher.
    ///     <para>
    ///         The argument is the demo path that changed, or <c>null</c> when the change spans many (a batch,
    ///         a bulk remove). <b>Carrying it is not a nicety.</b> A per-demo consumer — Match Overview
    ///         re-rendering the demo it is showing — would otherwise re-project on every unrelated write:
    ///         arrow-keying the Library while the indexer works would cost a capacity-1 cache miss and a full
    ///         page rebuild per demo indexed, and rebuilding the highlight groups pops open every group the
    ///         user had collapsed.
    ///     </para>
    /// </summary>
    public event Action<string?>? Changed;

    /// <summary>Opens a batch scope: mutations inside coalesce into one <see cref="Changed" /> at dispose.</summary>
    public IDisposable BeginBatch()
    {
        lock (_gate)
        {
            _batchDepth++;
        }

        return new BatchScope(this);
    }

    /// <summary>The index row for a demo, or null when it has never been seen.</summary>
    public DemoCacheIndexEntry? TryGetIndex(string path)
    {
        lock (_gate)
        {
            return _index.GetValueOrDefault(path);
        }
    }

    /// <summary>
    ///     The full record for a demo — read from its sidecar on demand. Returns null when the demo is unknown
    ///     or its sidecar is missing/corrupt; callers treat that exactly as "not cached" and re-index.
    ///     <para>
    ///         <b>Does no work beyond a small file read.</b> No parse, no header read, no queue — the cached
    ///         render's credibility rests on this page starting nothing the user did not ask for.
    ///     </para>
    /// </summary>
    public DemoCacheRecord? TryLoadRecord(string path)
    {
        string? cachedJson = null;
        lock (_gate)
        {
            if (_lastRecordPath is not null
                && string.Equals(_lastRecordPath, path, StringComparison.OrdinalIgnoreCase))
            {
                cachedJson = _lastRecordJson;
            }
        }

        try
        {
            if (cachedJson is not null)
            {
                return JsonSerializer.Deserialize<DemoCacheRecord>(cachedJson);
            }

            string? file = SidecarPathFor(path);
            if (file is null)
            {
                // No cache root: the record lives in memory or nowhere.
                string? held;
                lock (_gate)
                {
                    held = _memoryRecords.GetValueOrDefault(path);
                }

                return held is null ? null : JsonSerializer.Deserialize<DemoCacheRecord>(held);
            }

            if (!File.Exists(file))
            {
                return null;
            }

            string json = File.ReadAllText(file);
            DemoCacheRecord? record = JsonSerializer.Deserialize<DemoCacheRecord>(json);
            if (record is null)
            {
                return null;
            }

            lock (_gate)
            {
                _lastRecordPath = path;
                _lastRecordJson = json;
            }

            return record;
        }
        catch (Exception)
        {
            // Corrupt sidecar = treat the demo as un-indexed and let it be rebuilt.
            return null;
        }
    }

    /// <summary>
    ///     The record for a demo if one exists, else a fresh identity-tier record for
    ///     <paramref name="path" />. The entry point for every evaluator that is about to fill a tier.
    /// </summary>
    public DemoCacheRecord LoadOrCreate(string path, long size, long modifiedTicks)
    {
        DemoCacheRecord? existing = TryLoadRecord(path);

        // A record whose file no longer matches describes a DIFFERENT demo at the same path — the user
        // replaced or re-downloaded it. Keeping any tier would attribute the old match's rosters and score to
        // the new file, so identity drift discards everything rather than trying to salvage tiers.
        if (existing is not null && existing.MatchesFile(size, modifiedTicks))
        {
            return existing;
        }

        return new DemoCacheRecord
        {
            Path = path,
            Size = size,
            ModifiedTicks = modifiedTicks
        };
    }

    /// <summary>
    ///     Bulk-reads full records for every index row matching <paramref name="where" /> — the one genuinely
    ///     cross-demo consumer, the Reels <c>Add clips…</c> picker, which flattens every harvested highlight in
    ///     the library into one selectable list.
    ///     <para>
    ///         <b>Deliberately not the normal path.</b> This store is index-plus-lazy-sidecars precisely so
    ///         nothing deserializes the whole library at startup; every other reader wants one demo at a time
    ///         and must keep using <see cref="TryLoadRecord" />. Filter on
    ///         <see cref="DemoCacheIndexEntry.HighlightCount" /> — that field exists so a caller can decide
    ///         which sidecars are worth opening without opening them.
    ///     </para>
    ///     <para>
    ///         <b>Do not call this on the UI thread.</b> Measured against a real 348-demo cache (3.0 MB,
    ///         8,098 highlights, Debug build): ~32 ms warm, ~297 ms cold. Warm is unnoticeable; cold is a
    ///         visible hitch on a button press, and it scales with the library.
    ///     </para>
    /// </summary>
    /// <param name="where">Index-row predicate; null loads everything the index knows about.</param>
    public List<DemoCacheRecord> LoadRecords(Func<DemoCacheIndexEntry, bool>? where = null)
    {
        List<DemoCacheRecord> records = [];
        foreach (DemoCacheIndexEntry entry in Index)
        {
            if (where is not null && !where(entry))
            {
                continue;
            }

            if (TryLoadRecord(entry.Path) is { } record)
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <summary>
    ///     Applies <paramref name="mutate" /> to a demo's record WITHOUT re-asserting file identity, keeping
    ///     whatever <see cref="DemoCacheRecord.Size" /> / <see cref="DemoCacheRecord.ModifiedTicks" /> the
    ///     record already carries.
    ///     <para>
    ///         <b>This exists because the two writers do not agree on what "modified" means.</b> The library
    ///         indexer stamps <c>FileInfo.LastWriteTime</c> (LOCAL), the highlights scanner stamps
    ///         <c>LastWriteTimeUtc</c>. Routing a tier-3 fill through <see cref="Update" /> would therefore
    ///         hand <see cref="LoadOrCreate" /> a UTC tick count to compare against a locally-stamped record,
    ///         <see cref="DemoCacheRecord.MatchesFile" /> would fail for every user not on UTC, and the
    ///         "identity drift discards everything" rule would throw away the tier-2 roster and score on every
    ///         single scan. The record's identity belongs to whoever established it; a later tier fill has no
    ///         business restating it in a different unit.
    ///     </para>
    ///     <para>
    ///         When no record exists yet, one is created using the LIBRARY's convention (local ticks) — the
    ///         convention every record on disk was written with — so that the library's next reconcile matches
    ///         it instead of discarding the tier we just wrote.
    ///     </para>
    /// </summary>
    public void UpdateExisting(string path, Action<DemoCacheRecord> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_rmwGate)
        {
            DemoCacheRecord? record = TryLoadRecord(path);
            if (record is null)
            {
                FileInfo info = new(path);
                record = new DemoCacheRecord
                {
                    Path = path,
                    Size = info.Exists ? info.Length : 0,
                    ModifiedTicks = info.Exists ? info.LastWriteTime.Ticks : 0
                };
            }

            mutate(record);
            Upsert(record);
        }
    }

    /// <summary>
    ///     Writes a record's sidecar and refreshes its index projection. The single write path — every tier
    ///     fill goes through here so the index can never drift from the sidecars.
    /// </summary>
    public void Upsert(DemoCacheRecord record)
    {
        if (string.IsNullOrEmpty(record.Path))
        {
            return;
        }

        string json = JsonSerializer.Serialize(record, _jsonOptions);
        lock (_gate)
        {
            _index[record.Path] = record.ToIndexEntry();
            _lastRecordPath = record.Path;
            _lastRecordJson = json;
        }

        WriteSidecar(record.Path, json);
        RaiseChanged(record.Path);
    }

    /// <summary>
    ///     Loads a demo's record, applies <paramref name="mutate" />, and persists it. Convenience over
    ///     <see cref="LoadOrCreate" /> + <see cref="Upsert" /> for the common single-tier fill.
    /// </summary>
    public void Update(string path, long size, long modifiedTicks, Action<DemoCacheRecord> mutate)
    {
        lock (_rmwGate)
        {
            DemoCacheRecord record = LoadOrCreate(path, size, modifiedTicks);
            record.Size = size;
            record.ModifiedTicks = modifiedTicks;
            mutate(record);
            Upsert(record);
        }
    }

    /// <summary>Stamps a tier as written now, at its current schema version.</summary>
    public static void StampHeader(DemoCacheRecord record) =>
        Stamp(record.Header, DemoCacheRecord.HeaderSchema);

    /// <summary>Stamps the parse tier as written now.</summary>
    public static void StampParse(DemoCacheRecord record) =>
        Stamp(record.Parse, DemoCacheRecord.ParseSchema);

    /// <summary>Stamps the analysis tier as written now.</summary>
    public static void StampAnalysis(DemoCacheRecord record) =>
        Stamp(record.Analysis, DemoCacheRecord.AnalysisSchema);

    private static void Stamp(TierStamp stamp, int schema)
    {
        stamp.Schema = schema;
        stamp.ComputedAtTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>Forgets a demo entirely — index row and sidecar.</summary>
    public void Remove(string path)
    {
        bool removed;
        lock (_gate)
        {
            removed = _index.Remove(path);
            if (_lastRecordPath is not null
                && string.Equals(_lastRecordPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _lastRecordPath = null;
                _lastRecordJson = null;
            }
        }

        DeleteSidecar(path);

        if (removed)
        {
            RaiseChanged(path);
        }
    }

    /// <summary>Drops every row matching <paramref name="predicate" /> — library reconciliation.</summary>
    public void RemoveWhere(Func<DemoCacheIndexEntry, bool> predicate)
    {
        List<string> doomed;
        lock (_gate)
        {
            doomed = [.. _index.Values.Where(predicate).Select(e => e.Path)];
        }

        foreach (string path in doomed)
        {
            Remove(path);
        }
    }

    /// <summary>
    ///     Persists <c>index.json</c> atomically. Sidecars are written eagerly by <see cref="Upsert" />; only
    ///     the index is deferred, because a library pass touches it once per demo and rewriting it each time
    ///     is the exact cost the split storage exists to avoid.
    /// </summary>
    public void SaveIndex()
    {
        string? indexPath = IndexPath;
        if (indexPath is null)
        {
            return;
        }

        try
        {
            DemoCacheIndexFile file;
            lock (_gate)
            {
                file = new DemoCacheIndexFile
                {
                    LegacyMigrationVersion = _legacyMigrationVersion,
                    Entries = [.. _index.Values]
                };
            }

            WriteAtomic(indexPath, JsonSerializer.Serialize(file, _jsonOptions));
        }
        catch (Exception)
        {
            // Rebuildable cache — persistence noise is never surfaced.
        }
    }

    /// <summary>
    ///     The sidecar file for a demo path. Named by a CONTENT-INDEPENDENT hash of the path rather than by
    ///     the demo's sha256, because the sha is not known until a parse has run and a file name must exist
    ///     from the identity tier onwards.
    ///     <para>
    ///         SHA-256 rather than <c>string.GetHashCode</c>/<c>System.HashCode</c> deliberately: those are
    ///         RANDOMIZED PER PROCESS, so a file named from one would be unfindable on the next launch.
    ///     </para>
    /// </summary>
    public string? SidecarPathFor(string demoPath)
    {
        string? dir = SidecarDir;
        return dir is null ? null : Path.Combine(dir, $"{StableKey(demoPath)}.json");
    }

    /// <summary>A stable, filesystem-safe key for a demo path. See <see cref="SidecarPathFor" />.</summary>
    public static string StableKey(string demoPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(demoPath.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private void WriteSidecar(string demoPath, string json)
    {
        string? file = SidecarPathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                _memoryRecords[demoPath] = json;
            }

            return;
        }

        try
        {
            WriteAtomic(file, json);
        }
        catch (Exception)
        {
            // Rebuildable.
        }
    }

    private void DeleteSidecar(string path)
    {
        string? file = SidecarPathFor(path);
        if (file is null)
        {
            lock (_gate)
            {
                _memoryRecords.Remove(path);
            }

            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // Best effort — an orphaned sidecar is harmless, it is simply never read again.
        }
    }

    private static void WriteAtomic(string targetPath, string content)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".dc-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content);
        if (File.Exists(targetPath))
        {
            File.Replace(tempPath, targetPath, null);
        }
        else
        {
            File.Move(tempPath, targetPath);
        }
    }

    private void LoadIndex()
    {
        string? indexPath = IndexPath;
        if (indexPath is null || !File.Exists(indexPath))
        {
            return;
        }

        try
        {
            DemoCacheIndexFile? file =
                JsonSerializer.Deserialize<DemoCacheIndexFile>(File.ReadAllText(indexPath));
            if (file?.Entries is null)
            {
                return;
            }

            lock (_gate)
            {
                _legacyMigrationVersion = file.LegacyMigrationVersion;
                foreach (DemoCacheIndexEntry entry in file.Entries.Where(e => !string.IsNullOrEmpty(e.Path)))
                {
                    _index[entry.Path] = entry;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt index = start empty and rebuild.
        }
    }

    private void EndBatch()
    {
        bool fire;
        lock (_gate)
        {
            _batchDepth--;
            fire = _batchDepth == 0 && _batchDirty;
            if (fire)
            {
                _batchDirty = false;
            }
        }

        if (fire)
        {
            // A batch spans many demos, so there is no single path to name.
            _post(() => Changed?.Invoke(null));
        }
    }

    private void RaiseChanged(string? path)
    {
        lock (_gate)
        {
            if (_batchDepth > 0)
            {
                _batchDirty = true;
                return;
            }
        }

        _post(() => Changed?.Invoke(path));
    }

    private sealed class BatchScope(DemoCacheStore owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndBatch();
            }
        }
    }
}
