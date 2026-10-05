#region

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2DemoKit.Analysis.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     The unified demo-information cache: one tiered record per demo, replacing the overlapping
///     <c>library.json</c> and <c>highlights.json</c> stores.
///     <para>
///         <b>Storage shape.</b> A thin always-loaded <c>index.json</c> plus one lazily-read sidecar
///         per demo under <c>demos/</c>. The surfaces that want the fat payload, Match Overview and the reel
///         tray, want it for exactly ONE demo at a time, which is what "Match Overview is a cache render"
///         means; the Library grid wants a small projection of all of them. A monolith serves neither well: it
///         deserializes in full at every app start regardless of which demo the user cares about, and a
///         library backfill rewrites the whole growing file after every single demo.
///     </para>
///     <para>
///         <b>Durability.</b> Every write is atomic (temp file + replace), inherited from
///         <c>HighlightsCacheStore</c>, whose own note is the reason: a crash mid-write must never destroy an
///         hour of scan progress. Beyond atomicity, I/O failure is swallowed: this cache is rebuildable and
///         is never a source of truth.
///     </para>
///     <para>
///         <b>WASM.</b> <see cref="AppPaths.ConfigRoot" /> is null on the browser host, so the store runs
///         fully in-memory: nothing is loaded, nothing is written, and every API still works.
///     </para>
/// </summary>
public sealed class DemoCacheStore
{
    /// <summary>The record sidecar: gzipped compact JSON, named <c>&lt;key&gt;.json.gz</c>.</summary>
    public const string RecordSuffix = ".json.gz";

    // Record sidecars written before gzip. Still read; replaced at the demo's next write.
    private const string LegacyRecordSuffix = ".json";

    private static readonly JsonSerializerOptions _jsonOptions = new();

    private static ILogger? _diagLog;

    private readonly string? _cacheRoot;
    private readonly object _gate = new();

    // The always-loaded projection, keyed by demo path (the same case-insensitive keying the library and
    // highlights caches both use: macOS and Windows default filesystems are case-insensitive).
    private readonly Dictionary<string, DemoCacheIndexEntry> _index =
        new(StringComparer.OrdinalIgnoreCase);

    // The reverse of _index: content hash → the paths that carry it, maintained by the same three
    // mutations (load, upsert, remove) so it can never disagree. A set rather than one entry because a
    // copied demo legitimately puts two rows under one hash; the lookup picks the primary the same way
    // the library does. Under _gate.
    private readonly Dictionary<string, HashSet<string>> _pathsBySha = new(StringComparer.Ordinal);

    /// <summary>
    ///     Record store used when there is no cache root: the browser host, and tests.
    ///     <para>
    ///         <b>Not a test convenience.</b> Without it the no-root mode can hold exactly ONE record: writes
    ///         go nowhere and reads are served only by the capacity-1 JSON cache, so the second demo upserted
    ///         evicts the first and <see cref="TryLoadRecord(string)" /> returns null for it forever. Every surface
    ///         that resolves more than one demo, the Reels clip tray is cross-demo BY DEFINITION, silently
    ///         loses all but the most recent. The class contract already promised "nothing is written and
    ///         every API still works"; this is what makes the second half true.
    ///     </para>
    /// </summary>
    private readonly Dictionary<string, byte[]> _memoryRecords = new(StringComparer.OrdinalIgnoreCase);

    // Sibling files held in memory when there is no cache root, keyed by stable key plus suffix. Under _gate.
    private readonly Dictionary<string, byte[]> _memorySiblings = new(StringComparer.Ordinal);

    private readonly Action<Action> _post;

    /// <summary>
    ///     Serializes whole read-modify-write cycles (<see cref="Update" /> / <see cref="UpdateExisting" />).
    ///     Distinct from <see cref="_gate" />, which guards short index/cache critical sections only and is
    ///     taken INSIDE this one by the load and upsert steps.
    ///     <para>
    ///         Needed because one demo has several tier writers running concurrently: an interactive open
    ///         fires the highlights mirror (off-thread, from <c>OnOpenDemoEvaluated</c>) and the scoreboard
    ///         write at nearly the same moment, on the same record. Without this, both read the pre-write
    ///         record, both mutate their own copy, and whichever upserts last silently erases the other's
    ///         tier, losing exactly the highlights this cache was fixed to store.
    ///     </para>
    /// </summary>
    private readonly object _rmwGate = new();

    // Serialize a demo's sidecar file writes, deletes and legacy conversion, so a conversion can never
    // write a stale file over a fresher one. Taken outside _gate, never inside it.
    private readonly object[] _fileStripes = [.. Enumerable.Range(0, 16).Select(_ => new object())];

    private int _batchDepth; // under _gate
    private bool _batchDirty; // under _gate

    // Capacity-1 record cache. Match Overview re-reads the same record on every property touch while a demo
    // is selected, and arrow-keying the Library grid walks one demo at a time, so remembering exactly the
    // last one collapses the common case to zero I/O without holding a library's worth of records live.
    // (The same capacity-1 idiom the demo GetOrParse cache uses, for the same reason.)
    //
    // Cached as the file's BYTES, not as a live object, and every read deserializes a fresh instance. Handing
    // out a shared mutable record would let a UI-thread reader (Match Overview, rendering the selected demo)
    // watch fields change under it while a background tier-2 pass mutates the same instance through Update,
    // a torn read with no lock a caller could reasonably take. Gzipped bytes keep the entry off the LOH.
    private byte[]? _lastRecordBytes;
    private string? _lastRecordPath;
    private int _legacyMigrationVersion; // under _gate
    private long _indexVersion; // under _gate

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

    /// <summary>
    ///     Moves on every change to the index: a row set, replaced or removed. Equal versions mean an equal
    ///     <see cref="Index" />, so a projection of it can be cached against this.
    /// </summary>
    public long IndexVersion
    {
        get
        {
            lock (_gate)
            {
                return _indexVersion;
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

    /// <summary>The cache directory, or null for an in-memory store.</summary>
    internal string? CacheRoot => _cacheRoot;

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(SidecarFormatLog.Category);

    private string? SidecarDir => _cacheRoot is null ? null : Path.Combine(_cacheRoot, "demos");

    /// <summary>
    ///     Raised (via the post delegate) after any mutation, or, inside a <see cref="BeginBatch" /> scope,
    ///     exactly once when the scope closes. Consumers re-project wholesale per event, so an O(library) pass
    ///     MUST batch or it becomes an O(n²) re-projection storm on the dispatcher.
    ///     <para>
    ///         The argument is the demo path that changed, or <c>null</c> when the change spans many (a batch,
    ///         a bulk remove). <b>Carrying it is not a nicety.</b> A per-demo consumer, Match Overview
    ///         re-rendering the demo it is showing, would otherwise re-project on every unrelated write:
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
    ///     The index row carrying a content hash, or null when no indexed demo has it (never hashed yet,
    ///     or not in the library). The bridge from a user-truth store keyed by hash back to a path.
    ///     <para>
    ///         Two rows with one hash are a copied demo; the lexicographically-smallest path wins, which is
    ///         the primary the library shows as the card (<c>DemoLibraryService.ResolveContentIdentities</c>),
    ///         so both sides of the join name the same file.
    ///     </para>
    /// </summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo's bytes.</param>
    public DemoCacheIndexEntry? TryGetIndexBySha256(string sha256)
    {
        if (string.IsNullOrEmpty(sha256))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_pathsBySha.TryGetValue(sha256, out HashSet<string>? paths))
            {
                return null;
            }

            string? primary = null;
            foreach (string path in paths)
            {
                if (primary is null || string.CompareOrdinal(path, primary) < 0)
                {
                    primary = path;
                }
            }

            return primary is null ? null : _index.GetValueOrDefault(primary);
        }
    }

    /// <summary>
    ///     The full record for a demo: read from its sidecar on demand. Returns null when the demo is unknown
    ///     or its sidecar is missing/corrupt; callers treat that exactly as "not cached" and re-index.
    ///     <para>
    ///         <b>Does no work beyond a small file read.</b> No parse, no header read, no queue: the cached
    ///         render's credibility rests on this page starting nothing the user did not ask for.
    ///     </para>
    /// </summary>
    public DemoCacheRecord? TryLoadRecord(string path) => TryLoadRecord(path, true);

    /// <summary>
    ///     <see cref="TryLoadRecord(string)" />, leaving the capacity-1 cache alone when
    ///     <paramref name="remember" /> is false: a reader walking many demos does not evict the one the user
    ///     is looking at.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="remember">False keeps the read out of the capacity-1 cache.</param>
    public DemoCacheRecord? TryLoadRecord(string path, bool remember)
    {
        byte[]? cached = null;
        lock (_gate)
        {
            if (_lastRecordPath is not null
                && string.Equals(_lastRecordPath, path, StringComparison.OrdinalIgnoreCase))
            {
                cached = _lastRecordBytes;
            }
        }

        try
        {
            if (cached is not null)
            {
                return SidecarJson.Deserialize<DemoCacheRecord>(cached, _jsonOptions);
            }

            string? file = SidecarPathFor(path);
            if (file is null)
            {
                // No cache root: the record lives in memory or nowhere.
                byte[]? held;
                lock (_gate)
                {
                    held = _memoryRecords.GetValueOrDefault(path);
                }

                return held is null ? null : SidecarJson.Deserialize<DemoCacheRecord>(held, _jsonOptions);
            }

            // The new file wins whenever it reads. When it does not, a legacy file beside it is the last
            // good write.
            if (File.Exists(file))
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (TryDeserializeRecord(bytes) is { } record)
                {
                    if (remember)
                    {
                        lock (_gate)
                        {
                            _lastRecordPath = path;
                            _lastRecordBytes = bytes;
                        }
                    }

                    return record;
                }
            }

            string legacy = LegacySidecarPathFor(path)!;
            return File.Exists(legacy) ? SidecarJson.ReadFile<DemoCacheRecord>(legacy, _jsonOptions) : null;
        }
        catch (Exception)
        {
            // Corrupt sidecar = treat the demo as un-indexed and let it be rebuilt.
            return null;
        }
    }

    private static DemoCacheRecord? TryDeserializeRecord(byte[] bytes)
    {
        try
        {
            return SidecarJson.Deserialize<DemoCacheRecord>(bytes, _jsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // True when the file reads back as this demo's record: the check before a legacy file is deleted.
    private static bool VerifyRecordFile(string file, string demoPath)
    {
        try
        {
            return SidecarJson.ReadFile<DemoCacheRecord>(file, _jsonOptions) is { } record
                   && string.Equals(record.Path, demoPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Re-encodes a demo's pre-gzip record as <c>&lt;key&gt;.json.gz</c> without a parse. The legacy
    ///     file goes only after the new one reads back; a legacy file that does not read is kept.
    /// </summary>
    /// <param name="demoPath">An indexed demo.</param>
    internal SidecarConversion ConvertLegacyRecord(string demoPath)
    {
        string? file = SidecarPathFor(demoPath);
        if (file is null)
        {
            return SidecarConversion.None;
        }

        string legacy = LegacySidecarPathFor(demoPath)!;
        string legacyName = Path.GetFileName(legacy);
        lock (StripeFor(demoPath))
        {
            try
            {
                if (!File.Exists(legacy) || TryGetIndex(demoPath) is null)
                {
                    return SidecarConversion.None;
                }

                if (File.Exists(file) && VerifyRecordFile(file, demoPath))
                {
                    File.Delete(legacy);
                    return SidecarConversion.Converted;
                }

                byte[] compact = SidecarJson.Minify(File.ReadAllBytes(legacy));
                if (TryDeserializeRecord(compact) is not { } record
                    || !string.Equals(record.Path, demoPath, StringComparison.OrdinalIgnoreCase))
                {
                    SidecarFormatLog.LegacyUnreadable(Log, legacyName);
                    return SidecarConversion.Failed;
                }

                AtomicFile.WriteAllBytes(file, SidecarJson.Gzip(compact));
                if (!VerifyRecordFile(file, demoPath))
                {
                    File.Delete(file);
                    SidecarFormatLog.KeptLegacy(Log, legacyName);
                    return SidecarConversion.Failed;
                }

                File.Delete(legacy);
                return SidecarConversion.Converted;
            }
            catch (Exception ex)
            {
                SidecarFormatLog.ConversionFailed(Log, legacyName, ex);
                return SidecarConversion.Failed;
            }
        }
    }

    /// <summary>The lock that orders one demo's sidecar file writes against its legacy conversion.</summary>
    internal object StripeFor(string demoPath) =>
        _fileStripes[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(demoPath) % (uint)_fileStripes.Length)];

    /// <summary>
    ///     The record for a demo if one exists, else a fresh identity-tier record for
    ///     <paramref name="path" />. The entry point for every evaluator that is about to fill a tier.
    /// </summary>
    public DemoCacheRecord LoadOrCreate(string path, long size, long modifiedTicks)
    {
        DemoCacheRecord? existing = TryLoadRecord(path);

        // A record whose file no longer matches describes a DIFFERENT demo at the same path: the user
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
    ///     Bulk-reads full records for every index row matching <paramref name="where" />: the one genuinely
    ///     cross-demo consumer, the Reels <c>Add clips…</c> picker, which flattens every harvested highlight in
    ///     the library into one selectable list.
    ///     <para>
    ///         <b>Deliberately not the normal path.</b> This store is index-plus-lazy-sidecars precisely so
    ///         nothing deserializes the whole library at startup; every other reader wants one demo at a time
    ///         and must keep using <see cref="TryLoadRecord(string)" />. Filter on
    ///         <see cref="DemoCacheIndexEntry.HighlightCount" />: that field exists so a caller can decide
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
    ///         When no record exists yet, one is created using the LIBRARY's convention (local ticks), the
    ///         convention every record on disk was written with, so that the library's next reconcile matches
    ///         it instead of discarding the tier this call just wrote.
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
    ///     Writes a record's sidecar and refreshes its index projection. The single write path: every tier
    ///     fill goes through here so the index can never drift from the sidecars.
    /// </summary>
    public void Upsert(DemoCacheRecord record)
    {
        if (string.IsNullOrEmpty(record.Path))
        {
            return;
        }

        byte[] bytes = SidecarJson.SerializeGzip(record, _jsonOptions);
        lock (_gate)
        {
            SetIndexEntry(record.ToIndexEntry());
            _lastRecordPath = record.Path;
            _lastRecordBytes = bytes;
        }

        WriteSidecar(record.Path, bytes);
        RaiseChanged(record.Path);
    }

    /// <summary>
    ///     Sets a demo's index row to the projection of <paramref name="record" /> without writing its sidecar:
    ///     for a row written by an older index that lacks what the projection now carries. Nothing happens when
    ///     the demo has no row, or the row describes another file.
    /// </summary>
    /// <param name="record">The demo's record as read from its sidecar.</param>
    /// <returns>True when the row was replaced.</returns>
    public bool RefreshIndexRow(DemoCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (!_index.TryGetValue(record.Path, out DemoCacheIndexEntry? current)
                || !current.MatchesFile(record.Size, record.ModifiedTicks))
            {
                return false;
            }

            SetIndexEntry(record.ToIndexEntry());
        }

        RaiseChanged(record.Path);
        return true;
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

    /// <summary>
    ///     Typed access to one pack's payload on the records (<see cref="DemoCacheRecord.Packs" />), serialised
    ///     with this store's options. Stateless: hold one per pack or ask again.
    /// </summary>
    /// <param name="packId">The pack's id.</param>
    public IPackPayloads Payloads(string packId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        return new PackPayloads(this, packId, _jsonOptions);
    }

    private static void Stamp(TierStamp stamp, int schema)
    {
        stamp.Schema = schema;
        stamp.ComputedAtTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>Forgets a demo entirely: index row and sidecar.</summary>
    public void Remove(string path)
    {
        bool removed;
        lock (_gate)
        {
            removed = _index.Remove(path, out DemoCacheIndexEntry? gone);
            if (removed)
            {
                UnlinkSha(gone!);
                _indexVersion++;
            }

            if (_lastRecordPath is not null
                && string.Equals(_lastRecordPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _lastRecordPath = null;
                _lastRecordBytes = null;
            }
        }

        DeleteSidecar(path);

        if (removed)
        {
            RaiseChanged(path);
        }
    }

    /// <summary>Drops every row matching <paramref name="predicate" />: library reconciliation.</summary>
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

            AtomicFile.WriteAllBytes(indexPath, JsonSerializer.SerializeToUtf8Bytes(file, _jsonOptions));
        }
        catch (Exception)
        {
            // Rebuildable cache: persistence noise is never surfaced.
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
        return dir is null ? null : Path.Combine(dir, StableKey(demoPath) + RecordSuffix);
    }

    /// <summary>Where the record sidecar lived before it was gzipped. Read when the new file is absent.</summary>
    internal string? LegacySidecarPathFor(string demoPath)
    {
        string? dir = SidecarDir;
        return dir is null ? null : Path.Combine(dir, StableKey(demoPath) + LegacyRecordSuffix);
    }

    /// <summary>
    ///     A sibling file of a demo's sidecar: <c>demos/&lt;StableKey&gt;&lt;suffix&gt;</c>, e.g. the grenade
    ///     walk's <c>.grenades.json</c>. For a payload too large to ride the record, which Match Overview
    ///     re-reads on every property touch, yet owned by the same demo, so it goes when the demo goes.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">Starts with a dot, names a file, and is neither record name (<c>.json</c>, <c>.json.gz</c>).</param>
    public string? SiblingPathFor(string demoPath, string suffix)
    {
        ValidateSuffix(suffix);
        string? dir = SidecarDir;
        return dir is null ? null : Path.Combine(dir, StableKey(demoPath) + suffix);
    }

    /// <summary>
    ///     Writes a sibling atomically, or into memory when there is no cache root. Unlike the record write
    ///     this throws on an I/O failure: the caller stamps the record only after its siblings landed, so
    ///     the failure has to reach it.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">See <see cref="SiblingPathFor" />.</param>
    /// <param name="content">The whole file.</param>
    public void WriteSibling(string demoPath, string suffix, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        WriteSiblingBytes(demoPath, suffix, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>As <see cref="WriteSibling" />, for a binary (e.g. gzipped) sibling.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">See <see cref="SiblingPathFor" />.</param>
    /// <param name="content">The whole file.</param>
    public void WriteSiblingBytes(string demoPath, string suffix, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string? file = SiblingPathFor(demoPath, suffix);
        if (file is null)
        {
            lock (_gate)
            {
                _memorySiblings[StableKey(demoPath) + suffix] = content;
            }

            return;
        }

        AtomicFile.WriteAllBytes(file, content);
    }

    /// <summary>
    ///     Reads a JSON sibling, gzipped or plain. False when there is no such file; true with a null
    ///     <paramref name="value" /> when it exists but does not read.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">See <see cref="SiblingPathFor" />.</param>
    /// <param name="options">The document's serializer options.</param>
    /// <param name="value">The document, or null.</param>
    public bool TryReadSiblingJson<T>(string demoPath, string suffix, JsonSerializerOptions? options, out T? value)
        where T : class
    {
        value = null;
        string? file = SiblingPathFor(demoPath, suffix);
        try
        {
            if (file is null)
            {
                byte[]? held;
                lock (_gate)
                {
                    held = _memorySiblings.GetValueOrDefault(StableKey(demoPath) + suffix);
                }

                if (held is null)
                {
                    return false;
                }

                value = SidecarJson.Deserialize<T>(held, options);
                return true;
            }

            if (!File.Exists(file))
            {
                return false;
            }

            value = SidecarJson.ReadFile<T>(file, options);
            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>A sibling's raw bytes, or null when it is missing or unreadable.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">See <see cref="SiblingPathFor" />.</param>
    public byte[]? TryReadSiblingBytes(string demoPath, string suffix)
    {
        string? file = SiblingPathFor(demoPath, suffix);
        if (file is null)
        {
            lock (_gate)
            {
                return _memorySiblings.GetValueOrDefault(StableKey(demoPath) + suffix);
            }
        }

        try
        {
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Deletes one sibling, best effort.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">See <see cref="SiblingPathFor" />.</param>
    public void DeleteSibling(string demoPath, string suffix)
    {
        string? file = SiblingPathFor(demoPath, suffix);
        if (file is null)
        {
            lock (_gate)
            {
                _memorySiblings.Remove(StableKey(demoPath) + suffix);
            }

            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // An orphaned sibling is never read once the new one exists.
        }
    }

    /// <summary>A sibling's text, or null when it is missing or unreadable (the cache's "not cached").</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">See <see cref="SiblingPathFor" />.</param>
    public string? TryReadSibling(string demoPath, string suffix)
    {
        string? file = SiblingPathFor(demoPath, suffix);
        if (file is null)
        {
            lock (_gate)
            {
                return _memorySiblings.GetValueOrDefault(StableKey(demoPath) + suffix) is { } held
                    ? Encoding.UTF8.GetString(held)
                    : null;
            }
        }

        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A stable, filesystem-safe key for a demo path. See <see cref="SidecarPathFor" />.</summary>
    public static string StableKey(string demoPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(demoPath.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private void WriteSidecar(string demoPath, byte[] bytes)
    {
        string? file = SidecarPathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                _memoryRecords[demoPath] = bytes;
            }

            return;
        }

        lock (StripeFor(demoPath))
        {
            try
            {
                AtomicFile.WriteAllBytes(file, bytes);

                // Only once the new file reads back: otherwise the legacy record stays the reader's fallback.
                string legacy = LegacySidecarPathFor(demoPath)!;
                string legacyName = Path.GetFileName(legacy);
                if (File.Exists(legacy))
                {
                    if (VerifyRecordFile(file, demoPath))
                    {
                        File.Delete(legacy);
                    }
                    else
                    {
                        SidecarFormatLog.KeptLegacy(Log, legacyName);
                    }
                }
            }
            catch (Exception)
            {
                // Rebuildable.
            }
        }
    }

    private void DeleteSidecar(string path)
    {
        string? file = SidecarPathFor(path);
        string key = StableKey(path);
        if (file is null)
        {
            lock (_gate)
            {
                _memoryRecords.Remove(path);
                foreach (string sibling in _memorySiblings.Keys.Where(k => k.StartsWith(key + ".", StringComparison.Ordinal)).ToList())
                {
                    _memorySiblings.Remove(sibling);
                }
            }

            return;
        }

        lock (StripeFor(path))
        {
            try
            {
                File.Delete(file);

                // The siblings share the key and a dot, so one pattern finds every one of them and nothing else.
                string dir = Path.GetDirectoryName(file)!;
                if (Directory.Exists(dir))
                {
                    foreach (string sibling in Directory.EnumerateFiles(dir, key + ".*"))
                    {
                        File.Delete(sibling);
                    }
                }
            }
            catch (Exception)
            {
                // Best effort: an orphaned sidecar is harmless, it is simply never read again.
            }
        }
    }

    private static void ValidateSuffix(string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(suffix);
        if (suffix[0] != '.' || string.Equals(suffix, LegacyRecordSuffix, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(suffix, RecordSuffix, StringComparison.OrdinalIgnoreCase)
                             || suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{suffix}' is not a sibling suffix", nameof(suffix));
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
                SidecarJson.ReadFile<DemoCacheIndexFile>(indexPath, _jsonOptions);
            if (file?.Entries is null)
            {
                return;
            }

            lock (_gate)
            {
                _legacyMigrationVersion = file.LegacyMigrationVersion;
                foreach (DemoCacheIndexEntry entry in file.Entries.Where(e => !string.IsNullOrEmpty(e.Path)))
                {
                    SetIndexEntry(entry);
                }
            }
        }
        catch (Exception)
        {
            // Corrupt index = start empty and rebuild.
        }
    }

    // The only writer of _index, so the reverse map moves with it. Under _gate. A row re-upserted with a
    // different hash (the file was replaced and re-indexed) leaves the old hash's set first.
    private void SetIndexEntry(DemoCacheIndexEntry entry)
    {
        if (_index.TryGetValue(entry.Path, out DemoCacheIndexEntry? previous))
        {
            UnlinkSha(previous);
        }

        _index[entry.Path] = entry;
        _indexVersion++;
        if (!string.IsNullOrEmpty(entry.Sha256))
        {
            if (!_pathsBySha.TryGetValue(entry.Sha256, out HashSet<string>? paths))
            {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _pathsBySha[entry.Sha256] = paths;
            }

            paths.Add(entry.Path);
        }
    }

    // Under _gate. Drops the hash key entirely once no path carries it, so the map never outgrows the index.
    private void UnlinkSha(DemoCacheIndexEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Sha256)
            || !_pathsBySha.TryGetValue(entry.Sha256, out HashSet<string>? paths))
        {
            return;
        }

        paths.Remove(entry.Path);
        if (paths.Count == 0)
        {
            _pathsBySha.Remove(entry.Sha256);
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
