#region

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Playback2D.Pipeline;
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

    private const string ProvisionalPrefix = "p-";

    private readonly string? _cacheRoot;
    private readonly object _gate = new();

    // The stored rows: one per content id, or per path while a file has no hash yet ("p-" + StableKey).
    // The only thing persisted; everything below is derived from it. Under _gate.
    private readonly Dictionary<string, DemoCacheIndexEntry> _rows = new(StringComparer.Ordinal);

    // Path to the key of the row that lists it. Case-insensitive like every path map here: macOS and
    // Windows default filesystems are. Under _gate.
    private readonly Dictionary<string, string> _keyByPath = new(StringComparer.OrdinalIgnoreCase);

    // One view per location (the row re-stamped with that path's size and time), rebuilt only when its row
    // changes, so an unchanged demo keeps the same instance across Index reads. Under _gate.
    private readonly Dictionary<string, DemoCacheIndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);

    // Rows whose files moved key in this session; their old names go after the next index save. Under _gate.
    private readonly HashSet<string> _settleAfterSave = new(StringComparer.Ordinal);
    private readonly object _indexWriteGate = new();

    /// <summary>
    ///     Record store used when there is no cache root: the browser host, and tests. Keyed by file key.
    ///     <para>
    ///         <b>Not a test convenience.</b> Without it the no-root mode can hold exactly ONE record: writes
    ///         go nowhere and reads are served only by the capacity-1 JSON cache, so the second demo upserted
    ///         evicts the first and <see cref="TryLoadRecord(string)" /> returns null for it forever. Every surface
    ///         that resolves more than one demo, the Reels clip tray is cross-demo BY DEFINITION, silently
    ///         loses all but the most recent. The class contract already promised "nothing is written and
    ///         every API still works"; this is what makes the second half true.
    ///     </para>
    /// </summary>
    private readonly Dictionary<string, byte[]> _memoryRecords = new(StringComparer.Ordinal);

    // Sibling files held in memory when there is no cache root, keyed by file key plus suffix. Under _gate.
    private readonly Dictionary<string, byte[]> _memorySiblings = new(StringComparer.Ordinal);

    private readonly Action<Action> _post;

    /// <summary>
    ///     Serializes whole read-modify-write cycles (<see cref="Update" /> / <see cref="UpdateExisting" />) and
    ///     every <see cref="Upsert" />. Distinct from <see cref="_gate" />, which guards short index/cache
    ///     critical sections only and is taken INSIDE this one by the load and upsert steps.
    ///     <para>
    ///         Needed because one demo has several tier writers running concurrently: an interactive open
    ///         fires the highlights mirror (off-thread, from <c>OnOpenDemoEvaluated</c>) and the scoreboard
    ///         write at nearly the same moment, on the same record. Without this, both read the pre-write
    ///         record, both mutate their own copy, and whichever upserts last silently erases the other's
    ///         tier, losing exactly the highlights this cache was fixed to store.
    ///     </para>
    /// </summary>
    private readonly object _rmwGate = new();

    // Serialize the writes, deletes, conversions and renames of one file key. Taken outside _gate, never
    // inside it; two are always taken in index order.
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
    // Keyed by row key: every location of one content reads the same bytes.
    private byte[]? _lastRecordBytes;
    private string? _lastRecordKey;
    private int _legacyMigrationVersion; // under _gate
    private int _contentKeyMigrationVersion = ContentKeyMigration.CurrentVersion; // under _gate
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

    /// <summary>
    ///     One row per demo, seen from its primary path (<see cref="TryGetByContentId" />'s pick); a demo not
    ///     hashed yet is its own row. What a walk doing per-demo work iterates: <see cref="Index" /> holds a
    ///     view per path, so a demo with two copies appears there twice. A snapshot, safe to enumerate off-lock.
    /// </summary>
    public IReadOnlyList<DemoCacheIndexEntry> Contents
    {
        get
        {
            lock (_gate)
            {
                return [.. _rows.Values.Select(r => _index[r.Path])];
            }
        }
    }

    /// <summary>
    ///     The row listing <paramref name="path" />, seen from the row's primary path, or null when no row lists
    ///     it. The same instance <see cref="Contents" /> holds for that demo until the row changes. A path whose
    ///     bytes are not confirmed to be the row's is seen from itself, as <see cref="TryGetIndex" /> sees it.
    /// </summary>
    /// <param name="path">Any path of the demo.</param>
    public DemoCacheIndexEntry? TryGetPrimary(string path)
    {
        lock (_gate)
        {
            if (!_keyByPath.TryGetValue(path, out string? key))
            {
                return null;
            }

            return LocationIn(_rows[key], path) is { Confirmed: false } ? _index[path] : _index[_rows[key].Path];
        }
    }

    /// <summary>
    ///     What identifies the demo at <paramref name="path" />: its content id when a row lists the path with
    ///     one and the path's bytes were confirmed, else the path itself. Two paths of one hashed demo answer the
    ///     same; compare with <see cref="StringComparer.OrdinalIgnoreCase" />, as paths are compared everywhere here.
    /// </summary>
    /// <param name="path">A demo path.</param>
    public string DemoKeyOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate)
        {
            return _keyByPath.TryGetValue(path, out string? key) && !IsProvisional(_rows[key])
                                                                 && LocationIn(_rows[key], path)?.Confirmed == true
                ? key
                : path;
        }
    }

    /// <summary>True when both paths are one demo: the same path, or two paths a hashed row lists.</summary>
    /// <param name="a">A demo path.</param>
    /// <param name="b">Another demo path.</param>
    public bool SameDemo(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return SamePath(a, b) || string.Equals(DemoKeyOf(a), DemoKeyOf(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Number of paths known to the index; <see cref="Contents" /> counts demos.</summary>
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

    /// <summary>
    ///     Which revision of <see cref="ContentKeyMigration" /> has finished against this cache. See
    ///     <see cref="DemoCacheIndexFile.ContentKeyMigrationVersion" />.
    /// </summary>
    public int ContentKeyMigrationVersion
    {
        get
        {
            lock (_gate)
            {
                return _contentKeyMigrationVersion;
            }
        }
        set
        {
            lock (_gate)
            {
                _contentKeyMigrationVersion = value;
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

    /// <summary>
    ///     The index row for a demo path, or null when no row lists it. The row is seen from that path: its
    ///     <see cref="DemoCacheIndexEntry.Path" />, size and write time are that location's, everything else is
    ///     the content's.
    /// </summary>
    public DemoCacheIndexEntry? TryGetIndex(string path)
    {
        lock (_gate)
        {
            return _index.GetValueOrDefault(path);
        }
    }

    /// <summary>
    ///     The index row carrying a content hash, or null when no indexed demo has it (never hashed yet,
    ///     or not in the library). The bridge from a user-truth store keyed by hash back to a path. Same
    ///     answer as <see cref="TryGetByContentId" />.
    /// </summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo's bytes.</param>
    public DemoCacheIndexEntry? TryGetIndexBySha256(string sha256) => TryGetByContentId(sha256);

    /// <summary>
    ///     The primary index row for a content id, or null when no indexed row carries it. A content id is
    ///     <see cref="Playback2D.Pipeline.DemoContentHash" />'s lowercase-hex SHA-256 of the whole file; a row that has not been
    ///     hashed yet is invisible here and only reachable by <see cref="TryGetIndex" />, and so is a row no
    ///     path of which has been confirmed to hold those bytes.
    ///     <para>
    ///         One row holds every path with those bytes. It is seen from the ordinally smallest confirmed
    ///         path, the same primary the library shows as the card (<c>DemoLibraryService.ResolveContentIdentities</c>),
    ///         so a store joined by hash and the card name the same file.
    ///     </para>
    /// </summary>
    /// <param name="contentId">Lowercase-hex SHA-256 of the demo's bytes. Matched exactly.</param>
    public DemoCacheIndexEntry? TryGetByContentId(string? contentId)
    {
        if (string.IsNullOrEmpty(contentId))
        {
            return null;
        }

        lock (_gate)
        {
            return _rows.TryGetValue(contentId, out DemoCacheIndexEntry? row) && !IsProvisional(row)
                                                                              && row.Locations.Any(l => l.Confirmed)
                ? _index.GetValueOrDefault(row.Path)
                : null;
        }
    }

    /// <summary>
    ///     The row for a content id seen from each of its confirmed paths, primary first
    ///     (<see cref="TryGetByContentId" />'s pick), the rest in ordinal path order. Empty when no row carries it.
    ///     A snapshot: later writes do not change it.
    /// </summary>
    /// <param name="contentId">Lowercase-hex SHA-256 of the demo's bytes. Matched exactly.</param>
    public IReadOnlyList<DemoCacheIndexEntry> RowsForContentId(string? contentId)
    {
        if (string.IsNullOrEmpty(contentId))
        {
            return [];
        }

        lock (_gate)
        {
            if (!_rows.TryGetValue(contentId, out DemoCacheIndexEntry? row) || IsProvisional(row))
            {
                return [];
            }

            List<DemoCacheIndexEntry> rows = [.. row.Locations.Where(l => l.Confirmed).Select(l => _index[l.Path])];
            if (rows.Count == 0)
            {
                return [];
            }

            int primary = rows.FindIndex(r => string.Equals(r.Path, row.Path, StringComparison.Ordinal));
            if (primary > 0)
            {
                (rows[0], rows[primary]) = (rows[primary], rows[0]);
                rows.Sort(1, rows.Count - 1, Comparer<DemoCacheIndexEntry>.Create((a, b) => string.CompareOrdinal(a.Path, b.Path)));
            }

            return rows;
        }
    }

    /// <summary>
    ///     Lists <paramref name="path" /> as an unconfirmed location of the hashed row
    ///     <paramref name="contentId" />: the file's fingerprint matched the row's, and nothing has read it in
    ///     full. Writes only the index. Does nothing when a row already lists the path or no hashed row carries
    ///     the content id.
    /// </summary>
    /// <param name="contentId">The content id the fingerprint matched.</param>
    /// <param name="path">The file the fingerprint was taken from.</param>
    /// <param name="size">The file's size when fingerprinted.</param>
    /// <param name="modifiedTicks">The file's write time when fingerprinted, in the library's tick convention.</param>
    /// <returns>True when the path was attached.</returns>
    public bool AttachUnconfirmed(string contentId, string path, long size, long modifiedTicks)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentId);
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_rmwGate)
        {
            lock (_gate)
            {
                if (_keyByPath.ContainsKey(path) || !_rows.TryGetValue(contentId, out DemoCacheIndexEntry? row)
                                                 || IsProvisional(row))
                {
                    return false;
                }

                List<DemoLocation> locations =
                [
                    .. row.Locations,
                    new DemoLocation(path, false, size, modifiedTicks, DateTime.UtcNow.Ticks)
                ];
                Link(contentId, Shape(row.Copy(), locations, row.SidecarKeys));
            }
        }

        RaiseChanged(path);
        return true;
    }

    /// <summary>
    ///     Settles a path against a content hash just read from its whole file. A path its row lists without
    ///     confirmation becomes confirmed when the hash and size agree with the row. A path of a hashed row whose
    ///     bytes hash to something else leaves that row, as <see cref="Remove" /> would take it, so the next
    ///     write there starts a record of its own and the row keeps every other path and its data. A path with
    ///     no row, or a row not hashed yet, is left alone.
    /// </summary>
    /// <param name="path">The file that was read.</param>
    /// <param name="contentId">The hash of the bytes read.</param>
    /// <param name="size">The number of bytes read.</param>
    /// <returns>False when the path left its row.</returns>
    public bool ConfirmLocation(string path, string contentId, long size)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(contentId);
        lock (_rmwGate)
        {
            bool split;
            lock (_gate)
            {
                if (!_keyByPath.TryGetValue(path, out string? key) || IsProvisional(_rows[key])
                    || LocationIn(_rows[key], path) is not { } here)
                {
                    return true;
                }

                split = !string.Equals(key, contentId, StringComparison.Ordinal) || here.Size != size;
                if (!split)
                {
                    if (here.Confirmed)
                    {
                        return true;
                    }

                    DemoCacheIndexEntry row = _rows[key];
                    List<DemoLocation> locations =
                    [
                        .. row.Locations.Where(l => !SamePath(l.Path, path)),
                        here with { Confirmed = true, LastSeenUtcTicks = DateTime.UtcNow.Ticks }
                    ];
                    Link(key, Shape(row.Copy(), locations, row.SidecarKeys));
                }
            }

            if (split)
            {
                RemoveCore(path);
                return false;
            }
        }

        RaiseChanged(path);
        return true;
    }

    /// <summary>
    ///     Stores a fingerprint on a hashed row that has none, read from one of its confirmed paths. Does nothing
    ///     when the path is not a confirmed location of a hashed row, the row already has one, or the sizes differ.
    /// </summary>
    /// <param name="path">A confirmed path of the row.</param>
    /// <param name="fingerprint">The fingerprint just read from it.</param>
    internal void SetFingerprint(string path, DemoContentFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        lock (_rmwGate)
        {
            string key;
            lock (_gate)
            {
                if (!_keyByPath.TryGetValue(path, out string? k) || IsProvisional(_rows[k])
                    || _rows[k].ContentFingerprint is not null
                    || LocationIn(_rows[k], path) is not { Confirmed: true } here || here.Size != fingerprint.Size)
                {
                    return;
                }

                key = k;
            }

            if (TryLoadRecord(path, false) is { } record && string.Equals(record.Sha256, key, StringComparison.Ordinal))
            {
                record.ContentFingerprint = fingerprint;
                Upsert(record);
            }
        }
    }

    /// <summary>The hashed row listing <paramref name="path" />, and that path's location, or null.</summary>
    /// <param name="path">A demo path.</param>
    internal (string ContentId, DemoLocation Location)? LocationOf(string path)
    {
        lock (_gate)
        {
            return _keyByPath.TryGetValue(path, out string? key) && !IsProvisional(_rows[key])
                                                                 && LocationIn(_rows[key], path) is { } here
                ? (key, here)
                : null;
        }
    }

    /// <summary>Every hashed row: its content id, its fingerprint when it has one, and every path it lists.</summary>
    internal IReadOnlyList<(string ContentId, DemoContentFingerprint? Fingerprint, IReadOnlyList<DemoLocation> Locations)>
        KnownContents()
    {
        lock (_gate)
        {
            return [.. _rows.Where(r => !IsProvisional(r.Value)).Select(r => (r.Key, r.Value.ContentFingerprint, r.Value.Locations))];
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
        string cacheKey;
        DemoCacheIndexEntry? row;
        List<string> keys;
        byte[]? cached = null;
        lock (_gate)
        {
            (cacheKey, row) = RowFor(path);
            keys = ReadKeys(row, path);
            if (_lastRecordKey is not null && string.Equals(_lastRecordKey, cacheKey, StringComparison.Ordinal))
            {
                cached = _lastRecordBytes;
            }
        }

        try
        {
            if (cached is not null)
            {
                return Project(SidecarJson.Deserialize<DemoCacheRecord>(cached, _jsonOptions), path, row);
            }

            foreach (string key in keys)
            {
                // The new file wins whenever it reads. When it does not, a legacy file beside it is the last
                // good write.
                byte[]? bytes = ReadRecordBytes(key);
                if (bytes is not null && TryDeserializeRecord(bytes) is { } record && Belongs(record, row))
                {
                    if (remember)
                    {
                        lock (_gate)
                        {
                            _lastRecordKey = cacheKey;
                            _lastRecordBytes = bytes;
                        }
                    }

                    return Project(record, path, row);
                }

                if (TryReadLegacyRecord(key) is { } old && Belongs(old, row))
                {
                    return Project(old, path, row);
                }
            }

            return null;
        }
        catch (Exception)
        {
            // Corrupt sidecar = treat the demo as un-indexed and let it be rebuilt.
            return null;
        }
    }

    private byte[]? ReadRecordBytes(string key)
    {
        if (_cacheRoot is null)
        {
            lock (_gate)
            {
                return _memoryRecords.GetValueOrDefault(key);
            }
        }

        try
        {
            string file = RecordFile(key);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private DemoCacheRecord? TryReadLegacyRecord(string key)
    {
        if (_cacheRoot is null)
        {
            return null;
        }

        try
        {
            string legacy = LegacyRecordFile(key);
            return File.Exists(legacy) ? SidecarJson.ReadFile<DemoCacheRecord>(legacy, _jsonOptions) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A record read under a row describes it unless it names other bytes: a file left under a fallback name
    // by an earlier demo at the same path must not stand in for this one.
    private static bool Belongs(DemoCacheRecord record, DemoCacheIndexEntry? row) =>
        row is null || IsProvisional(row) || string.IsNullOrEmpty(record.Sha256)
        || string.Equals(record.Sha256, row.Sha256, StringComparison.Ordinal);

    // The sidecar holds whichever location wrote it last; the reader gets the one it asked for.
    private static DemoCacheRecord? Project(DemoCacheRecord? record, string path, DemoCacheIndexEntry? row)
    {
        if (record is null || row is null)
        {
            return record;
        }

        if (!string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase)
            && row.Locations.FirstOrDefault(l => SamePath(l.Path, path)) is { } here)
        {
            record.Path = here.Path;
            record.Size = here.Size;
            record.ModifiedTicks = here.ModifiedTicks;
        }

        record.Locations = row.Locations;
        return record;
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

    // True when the file reads back as a record of this demo: the check before a legacy file is deleted.
    private static bool VerifyRecordFile(string file, string demoPath, DemoCacheIndexEntry? row)
    {
        try
        {
            return SidecarJson.ReadFile<DemoCacheRecord>(file, _jsonOptions) is { } record && IsRecordOf(record, demoPath, row);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A record of a hashed row carries its hash; one written before the hash was known names one of its paths.
    private static bool IsRecordOf(DemoCacheRecord record, string demoPath, DemoCacheIndexEntry? row)
    {
        if (row is not null && !IsProvisional(row) && !string.IsNullOrEmpty(record.Sha256))
        {
            return string.Equals(record.Sha256, row.Sha256, StringComparison.Ordinal);
        }

        return SamePath(record.Path, demoPath) || (row is not null && row.Locations.Any(l => SamePath(l.Path, record.Path)));
    }

    /// <summary>
    ///     Re-encodes a demo's pre-gzip record as <c>&lt;key&gt;.json.gz</c> without a parse. The legacy
    ///     file goes only after the new one reads back; a legacy file that does not read is kept.
    /// </summary>
    /// <param name="demoPath">An indexed demo.</param>
    internal SidecarConversion ConvertLegacyRecord(string demoPath)
    {
        if (_cacheRoot is null)
        {
            return SidecarConversion.None;
        }

        SidecarConversion result = SidecarConversion.None;
        WithWriteKey(demoPath, (key, row) =>
        {
            string file = RecordFile(key);
            string legacy = LegacyRecordFile(key);
            string legacyName = Path.GetFileName(legacy);
            try
            {
                if (!File.Exists(legacy) || row is null)
                {
                    return;
                }

                if (File.Exists(file) && VerifyRecordFile(file, demoPath, row))
                {
                    File.Delete(legacy);
                    result = SidecarConversion.Converted;
                    return;
                }

                byte[] compact = SidecarJson.Minify(File.ReadAllBytes(legacy));
                if (TryDeserializeRecord(compact) is not { } record || !IsRecordOf(record, demoPath, row))
                {
                    SidecarFormatLog.LegacyUnreadable(Log, legacyName);
                    result = SidecarConversion.Failed;
                    return;
                }

                AtomicFile.WriteAllBytes(file, SidecarJson.Gzip(compact));
                if (!VerifyRecordFile(file, demoPath, row))
                {
                    File.Delete(file);
                    SidecarFormatLog.KeptLegacy(Log, legacyName);
                    result = SidecarConversion.Failed;
                    return;
                }

                File.Delete(legacy);
                result = SidecarConversion.Converted;
            }
            catch (Exception ex)
            {
                SidecarFormatLog.ConversionFailed(Log, legacyName, ex);
                result = SidecarConversion.Failed;
            }
        });
        return result;
    }

    /// <summary>
    ///     The record for a demo if one exists, else a fresh identity-tier record for
    ///     <paramref name="path" />. The entry point for every evaluator that is about to fill a tier.
    /// </summary>
    public DemoCacheRecord LoadOrCreate(string path, long size, long modifiedTicks)
    {
        SettleBeforeWrite(path);
        DemoCacheRecord? existing = TryLoadOwnRecord(path);

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
    /// <param name="where">
    ///     Predicate over <see cref="Contents" />: one record per demo, read from its primary path. Null loads
    ///     every demo the index knows about.
    /// </param>
    public List<DemoCacheRecord> LoadRecords(Func<DemoCacheIndexEntry, bool>? where = null)
    {
        List<DemoCacheRecord> records = [];
        foreach (DemoCacheIndexEntry entry in Contents)
        {
            if (where is not null && !where(entry))
            {
                continue;
            }

            // A walk never takes the capacity-1 slot: it would leave the last demo walked there and evict
            // the one the user has selected.
            if (TryLoadRecord(entry.Path, false) is { } record)
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
        SettleBeforeWrite(path);

        lock (_rmwGate)
        {
            DemoCacheRecord? record = TryLoadOwnRecord(path);
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
    ///     Writes a record's sidecar and refreshes its index row. The single write path: every tier fill goes
    ///     through here so the index can never drift from the sidecars.
    ///     <para>
    ///         The row is the record's content id, or the path's own provisional row while it has none. A path
    ///         listed under another row leaves it first: the bytes there changed. A record joining a row that
    ///         already exists keeps every tier it lacks from that row, so a copy written at the header tier
    ///         never erases the analysis another path of the same bytes produced.
    ///     </para>
    /// </summary>
    public void Upsert(DemoCacheRecord record)
    {
        if (string.IsNullOrEmpty(record.Path))
        {
            return;
        }

        string path = record.Path;
        if (!Monitor.IsEntered(_rmwGate))
        {
            SettleBeforeWrite(path);
        }

        string newKey = RowKeyFor(path, record.Sha256);
        lock (_rmwGate)
        {
            string? oldKey;
            DemoCacheIndexEntry? joining;
            lock (_gate)
            {
                oldKey = _keyByPath.GetValueOrDefault(path);
                joining = oldKey != newKey ? _rows.GetValueOrDefault(newKey) : null;
            }

            if (joining is not null && TryLoadRecord(joining.Path, false) is { } existing)
            {
                record.FillMissingFrom(existing);
            }

            // A row that keeps other paths may still hold its files under this path's key. They move off it
            // before this path writes there, or the new bytes overwrite the other paths' record and siblings.
            string pathKey = StableKey(path);
            List<string> claimants;
            lock (_gate)
            {
                bool leaving = oldKey is not null && oldKey != newKey
                    && _rows.TryGetValue(oldKey, out DemoCacheIndexEntry? leavingRow)
                    && leavingRow.Locations.Any(l => !SamePath(l.Path, path));
                claimants = [.. _rows
                    .Where(r => r.Key != newKey && (r.Key != oldKey || leaving)
                                && r.Value.SidecarKeys?.Contains(pathKey) == true)
                    .Select(r => r.Key)];
            }

            foreach (string claimant in claimants)
            {
                if (!ReleaseKey(claimant, pathKey))
                {
                    // Nothing changes: the path keeps its old row and stamps, so the next drift check retries.
                    return;
                }
            }

            DemoCacheIndexEntry? dropped = null;
            DemoCacheIndexEntry row;
            lock (_gate)
            {
                DemoCacheIndexEntry? oldRow = oldKey is null ? null : _rows.GetValueOrDefault(oldKey);
                DemoCacheIndexEntry? target = _rows.GetValueOrDefault(newKey);
                List<DemoLocation> locations = [.. (target?.Locations ?? []).Where(l => !SamePath(l.Path, path))];
                // Only ConfirmLocation confirms a path a fingerprint attached: a write is no full read.
                bool confirmed = target is null || LocationIn(target, path)?.Confirmed != false;
                locations.Add(new DemoLocation(path, confirmed, record.Size, record.ModifiedTicks, DateTime.UtcNow.Ticks));
                // A new row writes under the path's key until a saved index names the content id: without
                // the index only that name leads back to the file.
                List<string> keys = target is null ? [StableKey(path)] : [.. WriteKeys(target)];

                if (oldRow is not null && oldKey != newKey)
                {
                    List<DemoLocation> rest = [.. oldRow.Locations.Where(l => !SamePath(l.Path, path))];
                    Unlink(oldKey!);
                    if (rest.Count > 0)
                    {
                        Link(oldKey!, Shape(oldRow.Copy(), rest, oldRow.SidecarKeys));
                    }
                    else if (IsProvisional(oldRow))
                    {
                        // Same bytes, now hashed: its files move under the content id once the index says so.
                        keys.AddRange(WriteKeys(oldRow));
                        _settleAfterSave.Remove(oldKey!);
                    }
                    else
                    {
                        dropped = oldRow;
                        _settleAfterSave.Remove(oldKey!);
                    }
                }

                row = Shape(record.ToIndexEntry(), locations, keys);
                Link(newKey, row);
                if (row.SidecarKeys is not null)
                {
                    _settleAfterSave.Add(newKey);
                }
            }

            record.Locations = row.Locations;
            byte[] bytes = SidecarJson.SerializeGzip(record, _jsonOptions);
            if (dropped is not null)
            {
                DeleteFiles(FileKeys(dropped).Except(FileKeys(row), StringComparer.Ordinal).ToList());
            }

            lock (_gate)
            {
                _lastRecordKey = newKey;
                _lastRecordBytes = bytes;
            }

            WriteSidecar(path, bytes);

            if (_cacheRoot is null)
            {
                // Nothing persists, so nothing has to land before the old names go.
                SettleRow(newKey);
            }
        }

        RaiseChanged(path);
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
            if (!_keyByPath.TryGetValue(record.Path, out string? key)
                || !string.Equals(key, RowKeyFor(record.Path, record.Sha256), StringComparison.Ordinal)
                || _rows[key].Locations.FirstOrDefault(l => SamePath(l.Path, record.Path)) is not { } here
                || here.Size != record.Size || here.ModifiedTicks != record.ModifiedTicks)
            {
                return false;
            }

            DemoCacheIndexEntry current = _rows[key];
            Link(key, Shape(record.ToIndexEntry(), [.. current.Locations], current.SidecarKeys));
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
        SettleBeforeWrite(path);
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

    /// <summary>
    ///     Forgets a demo path. The row and its files go only with its last path: another path to the same bytes
    ///     keeps everything.
    /// </summary>
    public void Remove(string path)
    {
        lock (_rmwGate)
        {
            RemoveCore(path);
        }
    }

    private void RemoveCore(string path)
    {
        string pathKey = StableKey(path);
        string? owner;
        lock (_gate)
        {
            owner = _keyByPath.TryGetValue(path, out string? k) && _rows[k].Locations.Count > 1
                && _rows[k].SidecarKeys?.Contains(pathKey) == true
                    ? k
                    : null;
        }

        if (owner is not null)
        {
            ReleaseKey(owner, pathKey);
        }

        bool removed = false;
        DemoCacheIndexEntry? dropped = null;
        lock (_gate)
        {
            if (_keyByPath.TryGetValue(path, out string? key))
            {
                DemoCacheIndexEntry row = _rows[key];
                List<DemoLocation> rest = [.. row.Locations.Where(l => !SamePath(l.Path, path))];
                Unlink(key);
                removed = true;
                if (rest.Count > 0)
                {
                    Link(key, Shape(row.Copy(), rest, row.SidecarKeys));
                }
                else
                {
                    dropped = row;
                    _settleAfterSave.Remove(key);
                    if (string.Equals(_lastRecordKey, key, StringComparison.Ordinal))
                    {
                        _lastRecordKey = null;
                        _lastRecordBytes = null;
                    }
                }
            }
        }

        if (dropped is not null)
        {
            DeleteFiles(FileKeys(dropped));
        }
        else if (!removed)
        {
            DeleteFiles([StableKey(path)]);
        }

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
    ///     is the exact cost the split storage exists to avoid. Files a row took over under a new key in this
    ///     session are renamed once the index naming that key is on disk.
    /// </summary>
    public void SaveIndex() => TrySaveIndex();

    /// <summary><see cref="SaveIndex" />, reporting whether <c>index.json</c> was written.</summary>
    internal bool TrySaveIndex()
    {
        string? indexPath = IndexPath;
        if (indexPath is null)
        {
            return true;
        }

        if (!WriteIndex(indexPath, out List<string> settle))
        {
            return false;
        }

        // Written again once the files moved, so the next launch writes under the new names straight away.
        bool moved = false;
        foreach (string key in settle)
        {
            moved |= SettleRow(key);
        }

        return !moved || WriteIndex(indexPath, out _);
    }

    private bool WriteIndex(string indexPath, out List<string> settle)
    {
        // Snapshots land in the order they were taken: an older one written last would name keys already left.
        lock (_indexWriteGate)
        {
            return WriteIndexCore(indexPath, out settle);
        }
    }

    private bool WriteIndexCore(string indexPath, out List<string> settle)
    {
        try
        {
            DemoCacheIndexFile file;
            lock (_gate)
            {
                file = new DemoCacheIndexFile
                {
                    LegacyMigrationVersion = _legacyMigrationVersion,
                    ContentKeyMigrationVersion = _contentKeyMigrationVersion,
                    Entries = [.. _rows.Values]
                };
                settle = [.. _settleAfterSave];
            }

            AtomicFile.WriteAllBytes(indexPath, JsonSerializer.SerializeToUtf8Bytes(file, _jsonOptions));
            return true;
        }
        catch (Exception)
        {
            // Rebuildable cache: persistence noise is never surfaced.
            settle = [];
            return false;
        }
    }

    /// <summary>Keys of the rows whose files still sit under an older key.</summary>
    internal List<string> UnsettledRows()
    {
        lock (_gate)
        {
            return [.. _rows.Where(r => r.Value.SidecarKeys is not null).Select(r => r.Key)];
        }
    }

    /// <summary>
    ///     Moves a row's files from every older key to its own. The current write key's files replace what
    ///     is there; any other key's fill only what is missing, and its duplicates are dropped. Idempotent: a
    ///     file already moved is simply not found again.
    /// </summary>
    /// <param name="rowKey">The row's key.</param>
    /// <returns>True when nothing is left under an older key.</returns>
    internal bool SettleRow(string rowKey)
    {
        List<string> sources;
        string target;
        DemoCacheIndexEntry row;
        lock (_gate)
        {
            if (!_rows.TryGetValue(rowKey, out row!) || row.SidecarKeys is null)
            {
                _settleAfterSave.Remove(rowKey);
                return true;
            }

            sources = row.SidecarKeys;
            target = FileKeyOf(row);
        }

        object[] stripes = [.. sources.Append(target).Select(StripeIndex).Distinct().Order().Select(i => _fileStripes[i])];
        bool ok = true;
        EnterAll(stripes);
        try
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue(rowKey, out DemoCacheIndexEntry? now) || !ReferenceEquals(now, row))
                {
                    return false;
                }
            }

            for (int i = 0; i < sources.Count; i++)
            {
                if (!string.Equals(sources[i], target, StringComparison.Ordinal))
                {
                    ok &= MoveKey(sources[i], target, i == 0);
                }
            }

            if (ok)
            {
                lock (_gate)
                {
                    DemoCacheIndexEntry settled = row.Copy();
                    settled.SidecarKeys = null;
                    _rows[rowKey] = settled;
                    _settleAfterSave.Remove(rowKey);
                }
            }
        }
        finally
        {
            ExitAll(stripes);
        }

        return ok;
    }

    /// <summary>
    ///     Moves a row's files off one of its older keys onto the next key it keeps, or a spare key when that
    ///     fails, then saves the index, so a path leaving the row can write under its own key without touching
    ///     the row's files.
    /// </summary>
    /// <returns>False when a file is still under the key or the index could not be saved.</returns>
    private bool ReleaseKey(string rowKey, string key)
    {
        while (true)
        {
            DemoCacheIndexEntry row;
            List<string> keys;
            string target;
            lock (_gate)
            {
                if (!_rows.TryGetValue(rowKey, out row!) || row.SidecarKeys is null || !row.SidecarKeys.Contains(key))
                {
                    return true;
                }

                keys = row.SidecarKeys;
                target = keys.FirstOrDefault(k => k != key) ?? FileKeyOf(row);
            }

            if (string.Equals(target, key, StringComparison.Ordinal))
            {
                return false;
            }

            object[] stripes = [.. new[] { key, target }.Select(StripeIndex).Distinct().Order().Select(i => _fileStripes[i])];
            bool released = true;
            EnterAll(stripes);
            try
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_rows.GetValueOrDefault(rowKey), row))
                    {
                        continue;
                    }
                }

                bool first = keys[0] == key;
                List<string> rest = [.. keys.Where(k => k != key)];
                if (!MoveKey(key, target, first))
                {
                    // Whatever could not reach the target goes under a key no other row can name.
                    string spare = "m-" + Guid.NewGuid().ToString("N");
                    released = MoveKey(key, spare, first);
                    rest = first
                        ? [spare, target, .. rest.Where(k => k != target)]
                        : [.. rest, spare];
                    if (!released)
                    {
                        rest.Insert(first ? 0 : rest.Count, key);
                    }
                }

                lock (_gate)
                {
                    DemoCacheIndexEntry shaped = Shape(row.Copy(), [.. row.Locations], rest);
                    Link(rowKey, shaped);
                    if (shaped.SidecarKeys is null)
                    {
                        _settleAfterSave.Remove(rowKey);
                    }
                    else
                    {
                        _settleAfterSave.Add(rowKey);
                    }
                }
            }
            finally
            {
                ExitAll(stripes);
            }

            // The saved index must stop naming the old key before anything else is written under it.
            bool saved = IndexPath is not { } indexPath || WriteIndex(indexPath, out _);
            return released && saved;
        }
    }

    // Moves every file named <source>.* to <target>.*. Caller holds both stripes.
    private bool MoveKey(string source, string target, bool replace)
    {
        if (_cacheRoot is null)
        {
            lock (_gate)
            {
                MoveMemory(_memoryRecords, source, target, replace);
                foreach (string name in _memorySiblings.Keys.Where(k => k.StartsWith(source + ".", StringComparison.Ordinal)).ToList())
                {
                    MoveMemory(_memorySiblings, name, target + name[source.Length..], replace);
                }
            }

            return true;
        }

        string dir = SidecarDir!;
        if (!Directory.Exists(dir))
        {
            return true;
        }

        bool ok = true;
        foreach (string file in Directory.EnumerateFiles(dir, source + ".*").ToList())
        {
            string to = Path.Combine(dir, target + Path.GetFileName(file)[source.Length..]);
            try
            {
                if (replace)
                {
                    File.Move(file, to, true);
                }
                else if (!File.Exists(to))
                {
                    File.Move(file, to);
                }
                else
                {
                    File.Delete(file);
                }
            }
            catch (Exception)
            {
                ok = false;
            }
        }

        return ok;
    }

    private static void MoveMemory(Dictionary<string, byte[]> map, string from, string to, bool replace)
    {
        if (map.Remove(from, out byte[]? bytes) && (replace || !map.ContainsKey(to)))
        {
            map[to] = bytes;
        }
    }

    /// <summary>
    ///     Where a demo's record sidecar is written: <c>demos/&lt;key&gt;.json.gz</c>. The key is the content
    ///     id for a hashed demo and <see cref="StableKey" /> of the path for one not hashed yet, because a file
    ///     name must exist from the identity tier onwards. A row whose files have not moved to the content id
    ///     yet still writes under the key they are at.
    ///     <para>
    ///         SHA-256 rather than <c>string.GetHashCode</c>/<c>System.HashCode</c> deliberately: those are
    ///         RANDOMIZED PER PROCESS, so a file named from one would be unfindable on the next launch.
    ///     </para>
    /// </summary>
    public string? SidecarPathFor(string demoPath)
    {
        if (_cacheRoot is null)
        {
            return null;
        }

        lock (_gate)
        {
            return RecordFile(WriteKeyOf(demoPath));
        }
    }

    /// <summary>Where the record sidecar lived before it was gzipped. Read when the new file is absent.</summary>
    internal string? LegacySidecarPathFor(string demoPath)
    {
        if (_cacheRoot is null)
        {
            return null;
        }

        lock (_gate)
        {
            return LegacyRecordFile(WriteKeyOf(demoPath));
        }
    }

    /// <summary>
    ///     A sibling file of a demo's sidecar: <c>demos/&lt;key&gt;&lt;suffix&gt;</c>, e.g. the grenade walk's
    ///     <c>.grenades.json</c>, under the same key as the record (<see cref="SidecarPathFor" />). For a
    ///     payload too large to ride the record, which Match Overview re-reads on every property touch, yet
    ///     owned by the same demo, so it goes when the demo goes.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">Starts with a dot, names a file, and is neither record name (<c>.json</c>, <c>.json.gz</c>).</param>
    public string? SiblingPathFor(string demoPath, string suffix)
    {
        ValidateSuffix(suffix);
        string? dir = SidecarDir;
        if (dir is null)
        {
            return null;
        }

        lock (_gate)
        {
            return Path.Combine(dir, WriteKeyOf(demoPath) + suffix);
        }
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
        ValidateSuffix(suffix);
        WithWriteKey(demoPath, (key, _) =>
        {
            if (_cacheRoot is null)
            {
                lock (_gate)
                {
                    _memorySiblings[key + suffix] = content;
                }

                return;
            }

            AtomicFile.WriteAllBytes(Path.Combine(SidecarDir!, key + suffix), content);
        });
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
        ValidateSuffix(suffix);
        try
        {
            if (_cacheRoot is null)
            {
                if (TryReadSiblingBytes(demoPath, suffix) is not { } held)
                {
                    return false;
                }

                value = SidecarJson.Deserialize<T>(held, options);
                return true;
            }

            if (ExistingSibling(demoPath, suffix) is not { } file)
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
        ValidateSuffix(suffix);
        if (_cacheRoot is null)
        {
            lock (_gate)
            {
                foreach (string key in ReadKeys(RowFor(demoPath).Row, demoPath))
                {
                    if (_memorySiblings.TryGetValue(key + suffix, out byte[]? held))
                    {
                        return held;
                    }
                }

                return null;
            }
        }

        try
        {
            return ExistingSibling(demoPath, suffix) is { } file ? File.ReadAllBytes(file) : null;
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
        ValidateSuffix(suffix);
        WithWriteKey(demoPath, (_, row) =>
        {
            List<string> keys;
            lock (_gate)
            {
                keys = ReadKeys(row, demoPath);
            }

            foreach (string key in keys)
            {
                if (_cacheRoot is null)
                {
                    lock (_gate)
                    {
                        _memorySiblings.Remove(key + suffix);
                    }

                    continue;
                }

                try
                {
                    File.Delete(Path.Combine(SidecarDir!, key + suffix));
                }
                catch (Exception)
                {
                    // An orphaned sibling is never read once the new one exists.
                }
            }
        });
    }

    /// <summary>A sibling's text, or null when it is missing or unreadable (the cache's "not cached").</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="suffix">See <see cref="SiblingPathFor" />.</param>
    public string? TryReadSibling(string demoPath, string suffix) =>
        TryReadSiblingBytes(demoPath, suffix) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>A stable, filesystem-safe key for a demo path. See <see cref="SidecarPathFor" />.</summary>
    public static string StableKey(string demoPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(demoPath.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    // The first file that exists for a sibling, newest key first.
    private string? ExistingSibling(string demoPath, string suffix)
    {
        List<string> keys;
        lock (_gate)
        {
            keys = ReadKeys(RowFor(demoPath).Row, demoPath);
        }

        return keys.Select(k => Path.Combine(SidecarDir!, k + suffix)).FirstOrDefault(File.Exists);
    }

    private void WriteSidecar(string demoPath, byte[] bytes) =>
        WithWriteKey(demoPath, (key, row) =>
        {
            if (_cacheRoot is null)
            {
                lock (_gate)
                {
                    _memoryRecords[key] = bytes;
                }

                return;
            }

            try
            {
                string file = RecordFile(key);
                AtomicFile.WriteAllBytes(file, bytes);

                // Only once the new file reads back: otherwise the legacy record stays the reader's fallback.
                string legacy = LegacyRecordFile(key);
                string legacyName = Path.GetFileName(legacy);
                if (File.Exists(legacy))
                {
                    if (VerifyRecordFile(file, demoPath, row))
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
        });

    // Runs write under the stripe of the key a demo's files are written under. The key is resolved again
    // once the stripe is held, since a rename may have moved the row to another key meanwhile.
    private void WithWriteKey(string demoPath, Action<string, DemoCacheIndexEntry?> write)
    {
        while (true)
        {
            string key;
            lock (_gate)
            {
                key = WriteKeyOf(demoPath);
            }

            lock (_fileStripes[StripeIndex(key)])
            {
                DemoCacheIndexEntry? row;
                lock (_gate)
                {
                    if (!string.Equals(WriteKeyOf(demoPath), key, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    row = RowFor(demoPath).Row;
                }

                write(key, row);
                return;
            }
        }
    }

    private void DeleteFiles(IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            if (_cacheRoot is null)
            {
                lock (_gate)
                {
                    _memoryRecords.Remove(key);
                    foreach (string sibling in _memorySiblings.Keys.Where(k => k.StartsWith(key + ".", StringComparison.Ordinal)).ToList())
                    {
                        _memorySiblings.Remove(sibling);
                    }
                }

                continue;
            }

            lock (_fileStripes[StripeIndex(key)])
            {
                try
                {
                    // The record and the siblings share the key and a dot, so one pattern finds every one of
                    // them and nothing else.
                    string dir = SidecarDir!;
                    if (Directory.Exists(dir))
                    {
                        foreach (string file in Directory.EnumerateFiles(dir, key + ".*").ToList())
                        {
                            File.Delete(file);
                        }
                    }
                }
                catch (Exception)
                {
                    // Best effort: an orphaned sidecar is harmless, it is simply never read again.
                }
            }
        }
    }

    private string RecordFile(string key) => Path.Combine(SidecarDir!, key + RecordSuffix);

    private string LegacyRecordFile(string key) => Path.Combine(SidecarDir!, key + LegacyRecordSuffix);

    private int StripeIndex(string key) =>
        (int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_fileStripes.Length);

    private static void EnterAll(object[] locks)
    {
        foreach (object l in locks)
        {
            Monitor.Enter(l);
        }
    }

    private static void ExitAll(object[] locks)
    {
        for (int i = locks.Length - 1; i >= 0; i--)
        {
            Monitor.Exit(locks[i]);
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

    // ── Keys ─────────────────────────────────────────────────────────────────

    // A row's key: its content id, or the provisional key of its one path while it has none.
    private static string RowKeyFor(string path, string? sha256) =>
        string.IsNullOrEmpty(sha256) ? ProvisionalPrefix + StableKey(path) : sha256;

    private static bool IsProvisional(DemoCacheIndexEntry row) => string.IsNullOrEmpty(row.Sha256);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The file key a row's files settle under. A content id outside [0-9a-z_-] is hashed into a safe name.
    private static string FileKeyOf(DemoCacheIndexEntry row)
    {
        if (IsProvisional(row))
        {
            return StableKey(row.Path);
        }

        string id = row.Sha256!;
        return id.Length <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id.ToLowerInvariant()
            : "c-" + StableKey(id);
    }

    // Every key a row's files may sit under, current write key first.
    private static List<string> WriteKeys(DemoCacheIndexEntry row) => row.SidecarKeys ?? [FileKeyOf(row)];

    // The keys a row owns on disk: what a delete takes.
    private static List<string> FileKeys(DemoCacheIndexEntry row) =>
        [.. WriteKeys(row).Append(FileKeyOf(row)).Distinct(StringComparer.Ordinal)];

    // Under _gate. The keys a read tries in order: the row's own, then each path's, the names a rename cut
    // short or an older index left behind.
    private static List<string> ReadKeys(DemoCacheIndexEntry? row, string path)
    {
        if (row is null)
        {
            return [StableKey(path)];
        }

        return [.. FileKeys(row).Concat(row.Locations.Select(l => StableKey(l.Path))).Distinct(StringComparer.Ordinal)];
    }

    private static DemoLocation? LocationIn(DemoCacheIndexEntry row, string path) =>
        row.Locations.FirstOrDefault(l => SamePath(l.Path, path));

    // A write at an unconfirmed path hashes the file first. Whatever writes there has just read it, so the read
    // is warm, and without it the write could only start a record of its own, taking the path out of the row
    // and, when it was the row's last path, the row's files with it. Outside the write gate: the read is long.
    private void SettleBeforeWrite(string path)
    {
        lock (_gate)
        {
            if (!_keyByPath.TryGetValue(path, out string? key) || IsProvisional(_rows[key])
                || LocationIn(_rows[key], path) is not { Confirmed: false })
            {
                return;
            }
        }

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (DemoContentHash.TryCompute(path) is { } sha)
        {
            ConfirmLocation(path, sha, size);
        }
    }

    // A writer's starting point. The bytes at an unconfirmed path may not be the row's, so a write there
    // starts from nothing rather than carry the row's tiers onto whatever the file really holds.
    private DemoCacheRecord? TryLoadOwnRecord(string path)
    {
        lock (_gate)
        {
            if (_keyByPath.TryGetValue(path, out string? key) && LocationIn(_rows[key], path) is { Confirmed: false })
            {
                return null;
            }
        }

        return TryLoadRecord(path);
    }

    // Under _gate.
    private (string Key, DemoCacheIndexEntry? Row) RowFor(string path) =>
        _keyByPath.TryGetValue(path, out string? key) ? (key, _rows[key]) : (ProvisionalPrefix + StableKey(path), null);

    // Under _gate.
    private string WriteKeyOf(string path) =>
        RowFor(path).Row is { } row ? WriteKeys(row)[0] : StableKey(path);

    // Fills a row's location fields: ordinal path order, the primary the smallest confirmed path, and the
    // file keys dropped once they say nothing beyond the row's own.
    private static DemoCacheIndexEntry Shape(DemoCacheIndexEntry entry, List<DemoLocation> locations, List<string>? keys)
    {
        List<DemoLocation> sorted = [];
        foreach (DemoLocation location in locations.OrderBy(l => l.Path, StringComparer.Ordinal))
        {
            if (!sorted.Any(l => SamePath(l.Path, location.Path)))
            {
                sorted.Add(location);
            }
        }

        DemoLocation primary = sorted.FirstOrDefault(l => l.Confirmed) ?? sorted[0];
        entry.Path = primary.Path;
        entry.Size = primary.Size;
        entry.ModifiedTicks = primary.ModifiedTicks;
        entry.Locations = sorted;

        string own = FileKeyOf(entry);
        List<string>? distinct = keys is null ? null : [.. keys.Distinct(StringComparer.Ordinal)];
        entry.SidecarKeys = distinct is null || distinct.Count == 0 || (distinct.Count == 1 && distinct[0] == own)
            ? null
            : distinct;
        return entry;
    }

    // Under _gate. The only writers of the row maps, so the path lookup and the views move with the rows.
    private void Link(string key, DemoCacheIndexEntry row)
    {
        _rows[key] = row;
        foreach (DemoLocation location in row.Locations)
        {
            _keyByPath[location.Path] = key;
            _index[location.Path] = row.At(location);
        }

        _indexVersion++;
    }

    // Under _gate.
    private void Unlink(string key)
    {
        if (!_rows.Remove(key, out DemoCacheIndexEntry? row))
        {
            return;
        }

        foreach (DemoLocation location in row.Locations)
        {
            if (_keyByPath.TryGetValue(location.Path, out string? owner) && owner == key)
            {
                _keyByPath.Remove(location.Path);
                _index.Remove(location.Path);
            }
        }

        _indexVersion++;
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

            // Before version 5 a row is one path and its files are named by the path. Rows of one hash
            // become one row whose files still sit under every old name until the rename pass moves them.
            bool byPath = file.Version < 5;
            Dictionary<string, List<DemoCacheIndexEntry>> groups = new(StringComparer.Ordinal);
            foreach (DemoCacheIndexEntry entry in file.Entries.Where(e => !string.IsNullOrEmpty(e.Path)))
            {
                string key = RowKeyFor(entry.Path, entry.Sha256);
                if (!groups.TryGetValue(key, out List<DemoCacheIndexEntry>? group))
                {
                    groups[key] = group = [];
                }

                group.Add(entry);
            }

            lock (_gate)
            {
                _legacyMigrationVersion = file.LegacyMigrationVersion;
                _contentKeyMigrationVersion = byPath ? 0 : file.ContentKeyMigrationVersion;
                foreach ((string key, List<DemoCacheIndexEntry> group) in groups)
                {
                    // The copy with the most tiers speaks for the row; its files go first.
                    List<DemoCacheIndexEntry> ordered =
                    [
                        .. group.OrderByDescending(e => e.Tier).ThenBy(e => e.Path, StringComparer.Ordinal)
                    ];
                    List<DemoLocation> locations = [];
                    List<string> keys = [];
                    foreach (DemoCacheIndexEntry entry in ordered)
                    {
                        locations.AddRange(entry.Locations.Count > 0
                            ? entry.Locations
                            : [new DemoLocation(entry.Path, true, entry.Size, entry.ModifiedTicks, 0)]);
                        if (byPath)
                        {
                            keys.Add(StableKey(entry.Path));
                        }
                        else
                        {
                            keys.AddRange(WriteKeys(entry));
                        }
                    }

                    DemoCacheIndexEntry row = Shape(ordered[0], locations, keys);
                    Link(key, row);
                    if (row.SidecarKeys is not null && _contentKeyMigrationVersion >= ContentKeyMigration.CurrentVersion)
                    {
                        _settleAfterSave.Add(key);
                    }
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
