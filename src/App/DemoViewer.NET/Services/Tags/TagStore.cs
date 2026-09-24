#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Services.Tags;

/// <summary>
///     The Round Tagger's persisted store: <c>&lt;config&gt;/tags/index.json</c> plus one
///     <c>demos/&lt;sha256&gt;.dvtag.json</c> per demo (tag-store.md §3.1, §3.5).
///     <para>
///         <b>User truth, not a cache.</b> The storage shape is <c>DemoCacheStore</c>'s, the semantics are
///         the opposite: a sidecar is never discarded on identity drift, and a failed write is reported
///         (<see cref="Save" /> returns false) rather than swallowed, so the session can say so. A file that
///         cannot be read, or that names a different demo, is refused: loads return nothing for it and
///         writes to that hash are declined, because overwriting someone's hand-edited file with an empty
///         document is the one mistake this store cannot take back.
///     </para>
///     <para>
///         <b>Keyed by content hash, always, under the config root</b> (D1). Never beside the demo: the
///         Matrix needs every document from one directory, and most demos sit in the read-only replays
///         folder anyway. The store never hashes a file; callers bring the identity.
///     </para>
///     <para>
///         <b>The index is derived.</b> Rebuilt from <c>demos/</c> when missing or corrupt, and reconciled
///         against the directory listing at start, so a crash between a sidecar write and
///         <see cref="SaveIndex" /> costs one file read rather than a document the Matrix cannot see.
///     </para>
///     <para>
///         <b>Single writer.</b> A session that holds a document calls <see cref="CheckOut" />; while it is
///         checked out, <see cref="Update" /> posts the mutation to that session instead of touching disk,
///         which is what stops a facts refresh from being overwritten by the session's next autosave.
///     </para>
///     <para>
///         <b>Browser.</b> A null root keeps every document in a dictionary: nothing is read or written and
///         every API still works, the <c>DemoCacheStore._memoryRecords</c> rule.
///     </para>
/// </summary>
public sealed class TagStore
{
    /// <summary>The sidecar schema version this build writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The sidecar extension, after the hash.</summary>
    public const string SidecarExtension = ".dvtag.json";

    /// <summary>The reserved human label group that links an instance to a strat (overview correction 23).</summary>
    public const string StratGroup = "strat";

    private readonly Dictionary<string, TagSession> _checkedOut = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TagIndexEntry> _index = new(StringComparer.Ordinal);

    // The browser's (and tests') documents, as JSON text so a reader never shares a live object with a
    // writer. Under _gate.
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);

    private readonly Action<Action> _post;

    // Hashes whose file could not be read or names another demo. Writes to them are declined until a
    // load reads the file cleanly or it is deleted. Under _gate.
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

    // Serializes whole read-modify-write cycles, the DemoCacheStore._rmwGate pattern; Save takes it too so
    // an Update and an autosave of one document cannot interleave their replace.
    private readonly Lock _rmwGate = new();

    private readonly string? _root;

    private int _batchDepth; // under _gate
    private bool _batchDirty; // under _gate

    /// <param name="tagsRoot">
    ///     <c>&lt;config&gt;/tags</c>, or null for an in-memory store (the browser host, tests).
    /// </param>
    /// <param name="post">Marshals <see cref="Changed" /> and routed updates onto the UI thread; defaults to synchronous.</param>
    public TagStore(string? tagsRoot, Action<Action>? post = null)
    {
        _root = string.IsNullOrWhiteSpace(tagsRoot) ? null : tagsRoot;
        _post = post ?? (action => action());
        LoadIndex();
    }

    /// <summary>False on the browser host and in tests without a root: nothing survives the process.</summary>
    public bool IsPersistent => _root is not null;

    /// <summary>A point-in-time snapshot of every index row, safe to enumerate off-lock.</summary>
    public IReadOnlyList<TagIndexEntry> Index
    {
        get
        {
            lock (_gate)
            {
                return [.. _index.Values];
            }
        }
    }

    private string? IndexPath => _root is null ? null : Path.Combine(_root, "index.json");

    private string? DemosDir => _root is null ? null : Path.Combine(_root, "demos");

    /// <summary>
    ///     Raised through the post delegate after a save or a delete, with the hash that changed, or once
    ///     with null when a <see cref="BeginBatch" /> scope closes.
    /// </summary>
    public event Action<string?>? Changed;

    /// <summary>The sidecar file for a hash under a tags root.</summary>
    /// <param name="tagsRoot">The tags root.</param>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    public static string SidecarPathFor(string tagsRoot, string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(tagsRoot);
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        return Path.Combine(tagsRoot, "demos", Normalize(sha256) + SidecarExtension);
    }

    /// <summary>The sidecar this store would use for a hash, or null when it keeps documents in memory.</summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    public string? PathFor(string sha256) => _root is null ? null : SidecarPathFor(_root, sha256);

    /// <summary>Whether a document exists for a hash: on disk, or in memory on the browser.</summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    public bool Contains(string sha256)
    {
        if (string.IsNullOrEmpty(sha256))
        {
            return false;
        }

        string key = Normalize(sha256);
        lock (_gate)
        {
            if (_index.ContainsKey(key) || _memory.ContainsKey(key))
            {
                return true;
            }
        }

        string? path = PathFor(key);
        return path is not null && File.Exists(path);
    }

    /// <summary>Opens a batch scope: saves inside it coalesce into one <see cref="Changed" /> at dispose.</summary>
    public IDisposable BeginBatch()
    {
        lock (_gate)
        {
            _batchDepth++;
        }

        return new BatchScope(this);
    }

    /// <summary>A document, or null when it is absent, unreadable or names a different demo.</summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    public TagDocument? TryLoad(string sha256) => Load(sha256, ClockIdentity.Unknown).Document;

    /// <summary>
    ///     The document for a demo, or a fresh empty one when none exists. A refused file (unreadable, or
    ///     another demo's) also yields a fresh document, which <see cref="Save" /> will then decline to write.
    /// </summary>
    /// <param name="demo">The demo's identity.</param>
    /// <param name="clock">The clock the caller's parse is on; stamped on a fresh document.</param>
    public TagDocument LoadOrCreate(DemoIdentity demo, ClockIdentity clock)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(clock);
        return Load(demo.Sha256, clock).Document ?? TagDocument.Create(demo, clock);
    }

    /// <summary>
    ///     Loads a document with the facts a session's status line needs. Never throws for a missing,
    ///     truncated or foreign file.
    /// </summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    /// <param name="current">The clock the caller's parse is on.</param>
    public TagLoadResult Load(string sha256, ClockIdentity current)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        ArgumentNullException.ThrowIfNull(current);

        string key = Normalize(sha256);
        string? path = PathFor(key);
        string? json;
        if (path is null)
        {
            lock (_gate)
            {
                json = _memory.GetValueOrDefault(key);
            }
        }
        else
        {
            try
            {
                json = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Refuse(key);
                return new TagLoadResult(null, path, false, false, true, SchemaVersion);
            }
        }

        if (json is null)
        {
            return TagLoadResult.Empty(path);
        }

        TagDocument? document = Parse(json);
        if (document is null)
        {
            Refuse(key);
            return new TagLoadResult(null, path, false, false, true, SchemaVersion);
        }

        // Only reachable by a hand-edit, since the file is named by its hash; a copy of someone else's
        // sidecar under this name is still theirs, so it is refused rather than claimed.
        if (!string.Equals(Normalize(document.Demo.Sha256), key, StringComparison.Ordinal))
        {
            Refuse(key);
            return new TagLoadResult(null, path, true, false, false, document.SchemaVersion);
        }

        lock (_gate)
        {
            _refused.Remove(key);
        }

        bool clockMismatch = !current.Matches(document.Clock.ToClock());
        return new TagLoadResult(document, path, false, clockMismatch, false, document.SchemaVersion);
    }

    /// <summary>
    ///     Writes a document atomically and refreshes its index row. Returns false on any I/O failure and
    ///     for a refused hash; never throws. The caller must not mutate <paramref name="document" /> while
    ///     this runs (the session hands in a clone).
    /// </summary>
    /// <param name="document">The document; its <c>demo.sha256</c> names the file.</param>
    public bool Save(TagDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrEmpty(document.Demo.Sha256))
        {
            return false;
        }

        string key = Normalize(document.Demo.Sha256);
        document.Demo.Sha256 = key;

        lock (_rmwGate)
        {
            lock (_gate)
            {
                if (_refused.Contains(key))
                {
                    return false;
                }
            }

            string json = JsonSerializer.Serialize(document, TagJsonContext.Default.TagDocument);
            string? path = PathFor(key);
            if (path is null)
            {
                lock (_gate)
                {
                    _memory[key] = json;
                }
            }
            else
            {
                try
                {
                    WriteAtomic(path, json);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return false;
                }
            }

            lock (_gate)
            {
                _index[key] = TagIndexEntry.From(document, DateTime.UtcNow);
            }
        }

        RaiseChanged(key);
        return true;
    }

    /// <summary>
    ///     Read-modify-write of one document. When a session has it checked out, the mutation is posted to
    ///     that session instead (applied outside its undo history, then autosaved). A hash with no document
    ///     is left alone: this is the refresh path, and there is nothing to refresh.
    /// </summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    /// <param name="mutate">The change.</param>
    public void Update(string sha256, Action<TagDocument> mutate)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        ArgumentNullException.ThrowIfNull(mutate);

        string key = Normalize(sha256);
        TagSession? holder;
        lock (_gate)
        {
            holder = _checkedOut.GetValueOrDefault(key);
        }

        if (holder is not null)
        {
            _post(() => holder.ApplyExternal(mutate));
            return;
        }

        lock (_rmwGate)
        {
            if (TryLoad(key) is not { } document)
            {
                return;
            }

            mutate(document);
            Save(document);
        }
    }

    /// <summary>Deletes a document and its index row. Best-effort; false when there was nothing to delete.</summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    public bool Delete(string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        string key = Normalize(sha256);
        bool removed;
        lock (_rmwGate)
        {
            lock (_gate)
            {
                removed = _index.Remove(key) | _memory.Remove(key);
                _refused.Remove(key);
            }

            string? path = PathFor(key);
            if (path is not null && File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removed = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        if (removed)
        {
            RaiseChanged(key);
        }

        return removed;
    }

    /// <summary>
    ///     Every document whose index row matches: the cross-demo reader the Matrix and Watched Situations
    ///     use. Filter on <see cref="TagIndexEntry.Codes" /> or <see cref="TagIndexEntry.InstanceCount" /> to
    ///     skip files that cannot match. Not on the UI thread: measured at 222 ms warm over 1000 documents.
    /// </summary>
    /// <param name="where">Index-row predicate; null loads every document.</param>
    public List<TagDocument> LoadDocuments(Func<TagIndexEntry, bool>? where = null)
    {
        List<TagDocument> documents = [];
        foreach (TagIndexEntry entry in Index)
        {
            if (where is not null && !where(entry))
            {
                continue;
            }

            if (TryLoad(entry.Sha256) is { } document)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    /// <summary>
    ///     Persists <c>index.json</c>. Deferred like <c>DemoCacheStore.SaveIndex</c>: the session calls it on
    ///     detach and the shell at shutdown. A failure is silent because the index is derived.
    /// </summary>
    public void SaveIndex()
    {
        string? indexPath = IndexPath;
        if (indexPath is null)
        {
            return;
        }

        TagIndexFile file;
        lock (_gate)
        {
            file = new TagIndexFile
            {
                Entries = [.. _index.Values.OrderBy(e => e.Sha256, StringComparer.Ordinal)]
            };
        }

        try
        {
            WriteAtomic(indexPath, JsonSerializer.Serialize(file, TagJsonContext.Default.TagIndexFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Rebuildable from demos/.
        }
    }

    /// <summary>Re-reads every sidecar under <c>demos/</c> into the index: the recovery for a lost index.</summary>
    public void RebuildIndexFromDisk()
    {
        string? dir = DemosDir;
        if (dir is null)
        {
            return;
        }

        lock (_gate)
        {
            _index.Clear();
        }

        // Nothing has ever been tagged: writing an empty index here would create the tags directory on
        // every first run for no reader.
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in EnumerateSidecars(dir))
        {
            IndexFile(file);
        }

        SaveIndex();
        RaiseChanged(null);
    }

    /// <summary>
    ///     Makes a session the single writer for a document until the returned handle is disposed.
    ///     Exclusive per process: a second session on the same hash is a programming error.
    /// </summary>
    /// <param name="sha256">Lowercase-hex SHA-256 of the demo.</param>
    /// <param name="session">The session taking the document.</param>
    public IDisposable CheckOut(string sha256, TagSession session)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        ArgumentNullException.ThrowIfNull(session);
        string key = Normalize(sha256);
        lock (_gate)
        {
            if (_checkedOut.TryGetValue(key, out TagSession? holder) && !ReferenceEquals(holder, session))
            {
                throw new InvalidOperationException($"tags for {key} are already checked out by another session");
            }

            _checkedOut[key] = session;
        }

        return new CheckOutScope(this, key, session);
    }

    /// <summary>
    ///     Temp file plus replace, the config-root write idiom (<c>DemoCacheStore.WriteAtomic</c>), with this
    ///     store's own temp prefix so a stray file says whose it was.
    /// </summary>
    private static void WriteAtomic(string targetPath, string content)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".tag-{Guid.NewGuid():N}.tmp");
        try
        {
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
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the write's own failure is what the caller hears about.
        }
    }

    private static string Normalize(string sha256) => sha256.ToLowerInvariant();

    private static TagDocument? Parse(string json)
    {
        try
        {
            TagDocument? document = JsonSerializer.Deserialize(json, TagJsonContext.Default.TagDocument);

            // A document without a demo header cannot be matched to anything, so it is as unreadable as
            // a truncated one.
            return document?.Demo is { Sha256.Length: > 0 } ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateSidecars(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? [.. Directory.EnumerateFiles(dir, "*" + SidecarExtension)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Refuse(string key)
    {
        lock (_gate)
        {
            _refused.Add(key);
        }
    }

    // Reads one sidecar into the index. A file that does not parse, or whose body names another hash,
    // stays out of the index; Load refuses it when someone asks for it.
    private void IndexFile(string file)
    {
        string name = Path.GetFileName(file);
        string key = Normalize(name[..^SidecarExtension.Length]);
        TagLoadResult result = Load(key, ClockIdentity.Unknown);
        if (result.Document is not { } document)
        {
            return;
        }

        DateTime modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            modified = DateTime.UtcNow;
        }

        lock (_gate)
        {
            _index[key] = TagIndexEntry.From(document, modified);
        }
    }

    private void LoadIndex()
    {
        string? indexPath = IndexPath;
        string? dir = DemosDir;
        if (indexPath is null || dir is null)
        {
            return;
        }

        TagIndexFile? file = null;
        try
        {
            if (File.Exists(indexPath))
            {
                file = JsonSerializer.Deserialize(File.ReadAllText(indexPath), TagJsonContext.Default.TagIndexFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            file = null;
        }

        if (file?.Entries is null)
        {
            // Missing or corrupt: the sidecars are the truth, so the index is simply read back off them.
            RebuildIndexFromDisk();
            return;
        }

        lock (_gate)
        {
            foreach (TagIndexEntry entry in file.Entries.Where(e => !string.IsNullOrEmpty(e.Sha256)))
            {
                _index[Normalize(entry.Sha256)] = entry;
            }
        }

        // Reconcile against the listing, which costs no reads: a sidecar the index does not name (a crash
        // after its write and before SaveIndex) is read and added, and a row whose file is gone is dropped.
        HashSet<string> onDisk = new(StringComparer.Ordinal);
        bool changed = false;
        foreach (string sidecar in EnumerateSidecars(dir))
        {
            string name = Path.GetFileName(sidecar);
            string key = Normalize(name[..^SidecarExtension.Length]);
            onDisk.Add(key);
            bool known;
            lock (_gate)
            {
                known = _index.ContainsKey(key);
            }

            if (!known)
            {
                IndexFile(sidecar);
                changed = true;
            }
        }

        lock (_gate)
        {
            foreach (string gone in _index.Keys.Where(k => !onDisk.Contains(k)).ToList())
            {
                _index.Remove(gone);
                changed = true;
            }
        }

        if (changed)
        {
            SaveIndex();
        }
    }

    private void Release(string key, TagSession session)
    {
        lock (_gate)
        {
            if (_checkedOut.TryGetValue(key, out TagSession? holder) && ReferenceEquals(holder, session))
            {
                _checkedOut.Remove(key);
            }
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
            _post(() => Changed?.Invoke(null));
        }
    }

    private void RaiseChanged(string? sha256)
    {
        lock (_gate)
        {
            if (_batchDepth > 0)
            {
                _batchDirty = true;
                return;
            }
        }

        _post(() => Changed?.Invoke(sha256));
    }

    private sealed class BatchScope(TagStore owner) : IDisposable
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

    private sealed class CheckOutScope(TagStore owner, string key, TagSession session) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(key, session);
            }
        }
    }
}

/// <summary>
///     The outcome of a load. Carries the three problems as flags rather than throwing, because none is an
///     error the user can act on mid-session and each has a correct degraded behaviour.
/// </summary>
/// <param name="Document">The document, or null when there was none or it was refused.</param>
/// <param name="Path">The file that was read, or null in memory.</param>
/// <param name="DemoMismatch">The file names a different demo. Ignored and never overwritten.</param>
/// <param name="ClockMismatch">The ticks were written against a different parse. Loaded; the status line warns.</param>
/// <param name="Unreadable">The file exists and could not be read or parsed. Never overwritten.</param>
/// <param name="SchemaVersion">The schema version the file declared.</param>
public sealed record TagLoadResult(
    TagDocument? Document,
    string? Path,
    bool DemoMismatch,
    bool ClockMismatch,
    bool Unreadable,
    int SchemaVersion)
{
    /// <summary>Nothing stored, nothing wrong.</summary>
    /// <param name="path">Where the store looked, or null in memory.</param>
    public static TagLoadResult Empty(string? path) => new(null, path, false, false, false, TagStore.SchemaVersion);
}
