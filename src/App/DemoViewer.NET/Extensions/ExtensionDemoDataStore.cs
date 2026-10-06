#region

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     One extension's per-demo data under <c>&lt;cache&gt;/extension-data/&lt;id&gt;/demo-data/</c>: a folder per
///     facet holding one gzipped file per demo (and per part), named by the demo's content hash, or by a hash
///     of its path until the library has hashed it. Each file starts with a one-line JSON header (format,
///     facet, part, schema, fingerprint, the demo's hash and path, the extension's version, the write time)
///     followed by the payload. <c>index.json.gz</c> holds one stamp per demo and facet so a staleness check
///     opens no data file; a missing or unreadable index is rebuilt from the file headers.
///     <para>
///         A demo that leaves the library loses its files, unless another demo in the library has the same
///         content. Files keyed by a path hash move to the content hash once the library knows it.
///     </para>
///     <para>
///         The lock guards only the in-memory index. The index file is written and dropped files are deleted
///         after it is released, so <see cref="Stamp" /> and <see cref="Stamps" /> never wait on disk. On the UI
///         thread they also never load the index: before it is loaded they answer nothing, queue the load, and
///         raise <see cref="Changed" /> once it is in.
///     </para>
/// </summary>
internal sealed class ExtensionDemoDataStore : IExtensionDemoData
{
    /// <summary>The folder under the extension's cache folder the host owns. The extension's own file writes may not enter it.</summary>
    public const string DirectoryName = "demo-data";

    private const int Format = 1;
    private const string IndexFileName = "index.json.gz";
    private const string Suffix = ".json.gz";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _extensionId;
    private readonly string? _extensionVersion;
    private readonly object _gate = new();
    private readonly DemoCacheStore _library;
    private readonly object _loadGate = new();
    private readonly Func<bool> _onUiThread;
    private readonly Action<Action>? _loadInBackground;
    private readonly Action<Action> _post;
    private readonly string _root;
    private readonly object _saveGate = new();
    private Dictionary<(string Key, string Facet), Entry>? _entries;
    private int _loadQueued;
    private long _savedVersion;
    private long _version;

    /// <param name="extensionId">The extension the data belongs to.</param>
    /// <param name="extensionCacheFolder">The extension's own cache folder; the store lives in its <see cref="DirectoryName" /> subfolder.</param>
    /// <param name="library">The library: a demo's content hash, and which demos are still in it.</param>
    /// <param name="post">Runs a change notice on the UI thread.</param>
    /// <param name="extensionVersion">The extension's version, recorded in every header.</param>
    /// <param name="onUiThread">True on the UI thread, where a stamp read never loads the index. Null: never.</param>
    /// <param name="loadInBackground">Runs the index load off the UI thread. Null loads on the caller's thread.</param>
    public ExtensionDemoDataStore(string extensionId, string extensionCacheFolder, DemoCacheStore library, Action<Action> post,
        string? extensionVersion, Func<bool>? onUiThread = null, Action<Action>? loadInBackground = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        ArgumentException.ThrowIfNullOrEmpty(extensionCacheFolder);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(post);
        _extensionId = extensionId;
        _root = Path.Combine(extensionCacheFolder, DirectoryName);
        _library = library;
        _post = post;
        _extensionVersion = extensionVersion;
        _onUiThread = onUiThread ?? (static () => false);
        _loadInBackground = loadInBackground;
        _library.Changed += OnLibraryChanged;
    }

    /// <summary>The folder the store writes into.</summary>
    public string Root => _root;

    private static ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ExtensionsCategory);

    public bool IsAvailable => true;

    public event Action<string?>? Changed;

    public DemoDataStamp? Stamp(string demoPath, string facet)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        ExtensionFolders.SafeName(facet);
        if (!ReadyForStamps())
        {
            return null;
        }

        lock (_gate)
        {
            return Lookup(_entries!, demoPath, facet)?.ToStamp();
        }
    }

    public IReadOnlyList<DemoDataStamp> Stamps(string facet)
    {
        ExtensionFolders.SafeName(facet);
        if (!ReadyForStamps())
        {
            return [];
        }

        lock (_gate)
        {
            return [.. _entries!.Values.Where(e => string.Equals(e.Facet, facet, StringComparison.Ordinal)).Select(e => e.ToStamp())];
        }
    }

    // Loads the index here off the UI thread; on it, queues the load once and answers false until it is in.
    private bool ReadyForStamps()
    {
        if (Volatile.Read(ref _entries) is not null)
        {
            return true;
        }

        if (!_onUiThread() || _loadInBackground is null)
        {
            EnsureLoaded();
            return true;
        }

        if (Interlocked.Exchange(ref _loadQueued, 1) == 0)
        {
            _loadInBackground(() =>
            {
                Interlocked.Exchange(ref _loadQueued, 0);
                if (EnsureLoaded())
                {
                    Raise(null);
                }
            });
        }

        return false;
    }

    public byte[]? Read(string demoPath, string facet, int schema, string? fingerprint, string? part = null) =>
        ReadAny(demoPath, facet, part) is { } record
        && record.Schema == schema
        && string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? record.Content
            : null;

    public DemoDataRecord? ReadAny(string demoPath, string facet, string? part = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        ExtensionFolders.SafeName(facet);
        if (part is not null)
        {
            ExtensionFolders.SafeName(part);
        }

        EnsureLoaded();
        string? key;
        IndexFile? save;
        lock (_gate)
        {
            key = Find(demoPath, facet, out save)?.Key;
        }

        Save(save);

        if (key is null)
        {
            return null;
        }

        string file = FileFor(facet, key, part);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            (Header header, byte[] content) = ReadFile(file);
            string? sha = Sha256Of(demoPath);
            bool sameDemo = header.Sha256 is null || sha is null || string.Equals(header.Sha256, sha, StringComparison.OrdinalIgnoreCase);
            return header.Format == Format && sameDemo && string.Equals(header.Facet, facet, StringComparison.Ordinal)
                   && string.Equals(header.Part, part, StringComparison.Ordinal)
                ? new DemoDataRecord(header.Schema, header.Fingerprint, header.ExtensionVersion, header.WrittenAtTicks, content)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            AppLog.ExtensionStoreUnreadable(Log, _extensionId, file, ex.Message);
            return null;
        }
    }

    public DemoDataStamp? Write(string demoPath, DemoDataWrite write)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        ArgumentNullException.ThrowIfNull(write);
        ExtensionFolders.SafeName(write.Facet);
        foreach (string part in write.Parts.Keys)
        {
            ExtensionFolders.SafeName(part);
        }

        string? sha = Sha256Of(demoPath);
        string key = KeyFor(demoPath, sha);
        long now = DateTime.UtcNow.Ticks;
        foreach ((string part, ReadOnlyMemory<byte> content) in write.Parts)
        {
            WriteFile(FileFor(write.Facet, key, part), new Header(Format, write.Facet, part, write.Schema, write.Fingerprint, sha,
                demoPath, _extensionVersion, now), content.Span);
        }

        WriteFile(FileFor(write.Facet, key, null), new Header(Format, write.Facet, null, write.Schema, write.Fingerprint, sha,
            demoPath, _extensionVersion, now), write.Payload.Span);

        Entry entry = new(key, write.Facet, demoPath, sha, write.Schema, write.Fingerprint, DemoDataState.Written, now, write.Count);
        EnsureLoaded();
        List<(string Key, string Facet)> dropped = [];
        IndexFile save;
        lock (_gate)
        {
            Dictionary<(string, string), Entry> entries = Entries();
            if (sha is not null && entries.Remove((PathKey(demoPath), write.Facet)))
            {
                dropped.Add((PathKey(demoPath), write.Facet));
            }

            entries[(key, write.Facet)] = entry;
            save = Snapshot();
        }

        DeleteFiles(dropped);
        Save(save);
        Raise(demoPath);
        return entry.ToStamp();
    }

    public void MarkFailed(string demoPath, string facet) =>
        Mutate(demoPath, facet, e => e with { State = DemoDataState.Failed }, create: true);

    public void ClearFailed(string demoPath, string facet) =>
        Mutate(demoPath, facet, e => e.State == DemoDataState.Failed ? e with { State = DemoDataState.Pending } : e, create: false);

    public void SetCount(string demoPath, string facet, int count) =>
        Mutate(demoPath, facet, e => e with { Count = count }, create: true);

    public void Invalidate(string facet, string? demoPath = null)
    {
        ExtensionFolders.SafeName(facet);
        if (demoPath is not null)
        {
            Mutate(demoPath, facet, Stale, create: false);
            return;
        }

        EnsureLoaded();
        IndexFile save;
        lock (_gate)
        {
            Dictionary<(string Key, string Facet), Entry> entries = Entries();
            foreach (((string Key, string Facet) id, Entry entry) in entries.ToList())
            {
                if (string.Equals(id.Facet, facet, StringComparison.Ordinal))
                {
                    entries[id] = Stale(entry);
                }
            }

            save = Snapshot();
        }

        Save(save);
        Raise(null);

        static Entry Stale(Entry e) => e with
        {
            Fingerprint = null,
            State = e.State == DemoDataState.Failed ? DemoDataState.Pending : e.State
        };
    }

    public void Delete(string demoPath, string facet)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        ExtensionFolders.SafeName(facet);
        EnsureLoaded();
        IndexFile? save;
        (string Key, string Facet)? id = null;
        lock (_gate)
        {
            if (Find(demoPath, facet, out save) is { } entry)
            {
                id = (entry.Key, facet);
                Entries().Remove(id.Value);
                save = Snapshot();
            }
        }

        Save(save);
        if (id is { } removed)
        {
            DeleteFiles([removed]);
            Raise(demoPath);
        }
    }

    /// <summary>Forgets every stamp after the folder was deleted from outside, and tells the extension once.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _entries = null;
        }

        Raise(null);
    }

    private void Mutate(string demoPath, string facet, Func<Entry, Entry> change, bool create)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        ExtensionFolders.SafeName(facet);
        EnsureLoaded();
        IndexFile? save;
        bool changedAny = false;
        lock (_gate)
        {
            Entry? entry = Find(demoPath, facet, out save);
            if (entry is null && create)
            {
                string? sha = Sha256Of(demoPath);
                entry = new Entry(KeyFor(demoPath, sha), facet, demoPath, sha, 0, null, DemoDataState.Pending, 0, 0);
            }

            if (entry is not null)
            {
                Entry changed = change(entry);
                if (changed != entry || !Entries().ContainsKey((entry.Key, facet)))
                {
                    Entries()[(entry.Key, facet)] = changed;
                    save = Snapshot();
                    changedAny = true;
                }
            }
        }

        Save(save);
        if (changedAny)
        {
            Raise(demoPath);
        }
    }

    // Called under the lock. The content hash's entry, else the path's, reported under the hash once the
    // library knows it; the files move on the next read or write. Opens no file.
    private Entry? Lookup(Dictionary<(string Key, string Facet), Entry> entries, string demoPath, string facet)
    {
        string? sha = Sha256Of(demoPath);
        if (sha is not null && entries.TryGetValue((sha, facet), out Entry? bySha))
        {
            return bySha;
        }

        return entries.GetValueOrDefault((PathKey(demoPath), facet)) is { } byPath && sha is not null
            ? byPath with { Key = sha, Sha256 = sha }
            : entries.GetValueOrDefault((PathKey(demoPath), facet));
    }

    // Called under the lock, never from a stamp read. The content hash's entry, else the path's; a path entry
    // whose demo now has a known hash moves over to it, files and all, and the index to save comes back.
    private Entry? Find(string demoPath, string facet, out IndexFile? save)
    {
        save = null;
        Dictionary<(string Key, string Facet), Entry> entries = Entries();
        string? sha = Sha256Of(demoPath);
        if (sha is not null && entries.TryGetValue((sha, facet), out Entry? bySha))
        {
            return bySha;
        }

        string pathKey = PathKey(demoPath);
        if (!entries.TryGetValue((pathKey, facet), out Entry? byPath))
        {
            return null;
        }

        if (sha is null)
        {
            return byPath;
        }

        Entry moved = byPath with { Key = sha, Sha256 = sha };
        try
        {
            foreach (string file in FilesOf(facet, pathKey))
            {
                string name = Path.GetFileName(file);
                File.Move(file, Path.Combine(Path.GetDirectoryName(file)!, sha + name[pathKey.Length..]), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return byPath;
        }

        entries.Remove((pathKey, facet));
        entries[(sha, facet)] = moved;
        save = Snapshot();
        return moved;
    }

    private string? Sha256Of(string demoPath) =>
        _library.TryGetIndex(demoPath)?.Sha256 is { Length: > 0 } sha ? sha.ToLowerInvariant() : null;

    private static string KeyFor(string demoPath, string? sha) => sha ?? PathKey(demoPath);

    private static string PathKey(string demoPath) => "p-" + DemoCacheStore.StableKey(demoPath);

    private string FileFor(string facet, string key, string? part) =>
        Path.Combine(_root, facet, part is null ? key + Suffix : key + "." + part + Suffix);

    private List<string> FilesOf(string facet, string key)
    {
        string folder = Path.Combine(_root, facet);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, key + "*" + Suffix)
            .Where(f => Path.GetFileName(f) is var name && (name == key + Suffix || name.StartsWith(key + ".", StringComparison.Ordinal)))
            .ToList();
    }

    // Outside the lock. The files of entries already gone from the index; one that will not delete is left
    // for the next rebuild to find without an entry.
    private void DeleteFiles(IEnumerable<(string Key, string Facet)> ids)
    {
        foreach ((string key, string facet) in ids)
        {
            foreach (string file in FilesOf(facet, key))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.ExtensionStoreWriteFailed(Log, _extensionId, file, ex.Message);
                }
            }
        }
    }

    // A demo the library dropped takes its files along, unless another demo in the library has its content.
    private void OnLibraryChanged(string? path)
    {
        List<(string Key, string Facet)> dropped = [];
        IndexFile? save = null;
        lock (_gate)
        {
            if (_entries is null)
            {
                return; // nothing loaded, nothing to keep in step; the load sweeps
            }

            if (Sweep(_entries, path, dropped))
            {
                save = Snapshot();
            }
        }

        if (save is null)
        {
            return;
        }

        DeleteFiles(dropped);
        Save(save);
        Raise(path);
    }

    // Called under the lock. Null sweeps every entry. What it drops is added to dropped, to delete outside the lock.
    private bool Sweep(Dictionary<(string Key, string Facet), Entry> entries, string? path, List<(string Key, string Facet)> dropped)
    {
        bool changed = false;
        foreach (((string Key, string Facet) id, Entry entry) in entries.ToList())
        {
            if (path is not null && !string.Equals(entry.DemoPath, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_library.TryGetIndex(entry.DemoPath) is not null)
            {
                continue;
            }

            if (entry.Sha256 is not null && _library.TryGetIndexBySha256(entry.Sha256) is { } other)
            {
                entries[id] = entry with { DemoPath = other.Path };
            }
            else
            {
                entries.Remove(id);
                dropped.Add(id);
            }

            changed = true;
        }

        return changed;
    }

    private void Raise(string? demoPath)
    {
        if (Changed is { } handlers)
        {
            _post(() => handlers(demoPath));
        }
    }

    // Called under the lock, after EnsureLoaded. A Reset in between loads again here.
    private Dictionary<(string Key, string Facet), Entry> Entries() => _entries ??= LoadIndex() ?? RebuildIndex();

    // Outside the lock. Reads the index, or rebuilds it from the file headers, without holding the lock a stamp
    // read takes. True when this call loaded it.
    private bool EnsureLoaded()
    {
        if (Volatile.Read(ref _entries) is not null)
        {
            return false;
        }

        lock (_loadGate)
        {
            if (Volatile.Read(ref _entries) is not null)
            {
                return false;
            }

            Dictionary<(string Key, string Facet), Entry> loaded = LoadIndex() ?? RebuildIndex();
            List<(string Key, string Facet)> dropped = [];
            IndexFile? save = null;
            lock (_gate)
            {
                if (_entries is not null)
                {
                    return false;
                }

                if (_library.Count > 0 && Sweep(loaded, null, dropped))
                {
                    Volatile.Write(ref _entries, loaded);
                    save = Snapshot();
                }
                else
                {
                    Volatile.Write(ref _entries, loaded);
                }
            }

            DeleteFiles(dropped);
            Save(save);
            return true;
        }
    }

    private Dictionary<(string Key, string Facet), Entry>? LoadIndex()
    {
        string file = Path.Combine(_root, IndexFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(file);
            using GZipStream gzip = new(stream, CompressionMode.Decompress);
            IndexFile? index = JsonSerializer.Deserialize<IndexFile>(gzip, JsonOptions);
            if (index is not { Version: Format })
            {
                return null;
            }

            Dictionary<(string Key, string Facet), Entry> entries = [];
            foreach (Entry entry in index.Entries)
            {
                if (ExtensionFolders.IsSafeName(entry.Key) && ExtensionFolders.IsSafeName(entry.Facet))
                {
                    entries[(entry.Key, entry.Facet)] = entry;
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            AppLog.ExtensionStoreUnreadable(Log, _extensionId, file, ex.Message);
            return null;
        }
    }

    // The index is a cache of the headers: every main payload file still says what it is.
    private Dictionary<(string Key, string Facet), Entry> RebuildIndex()
    {
        Dictionary<(string Key, string Facet), Entry> entries = [];
        if (!Directory.Exists(_root))
        {
            return entries;
        }

        foreach (string folder in Directory.EnumerateDirectories(_root))
        {
            string facet = Path.GetFileName(folder);
            if (!ExtensionFolders.IsSafeName(facet))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(folder, "*" + Suffix))
            {
                string key = Path.GetFileName(file)[..^Suffix.Length];
                if (key.Contains('.', StringComparison.Ordinal) || !ExtensionFolders.IsSafeName(key))
                {
                    continue; // a part, or not ours
                }

                try
                {
                    Header header = ReadHeader(file);
                    if (header.Format == Format && string.Equals(header.Facet, facet, StringComparison.Ordinal) && header.DemoPath is { } path)
                    {
                        entries[(key, facet)] = new Entry(key, facet, path, header.Sha256, header.Schema, header.Fingerprint,
                            DemoDataState.Written, header.WrittenAtTicks, 0);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    AppLog.ExtensionStoreUnreadable(Log, _extensionId, file, ex.Message);
                }
            }
        }

        return entries;
    }

    // Called under the lock: the index as it is now, numbered so an older snapshot never overwrites a newer one.
    private IndexFile Snapshot() => new(Format, [.. _entries!.Values]) { Sequence = ++_version };

    // Outside the lock. Null saves nothing.
    private void Save(IndexFile? index)
    {
        if (index is null)
        {
            return;
        }

        lock (_saveGate)
        {
            if (index.Sequence <= _savedVersion)
            {
                return;
            }

            _savedVersion = index.Sequence;
            WriteIndex(index);
        }
    }

    private void WriteIndex(IndexFile index)
    {
        string file = Path.Combine(_root, IndexFileName);
        try
        {
            AtomicFile.Write(file, stream =>
            {
                using GZipStream gzip = new(stream, CompressionLevel.Fastest, true);
                JsonSerializer.Serialize(gzip, index, JsonOptions);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The files carry their headers; the next load rebuilds what this save lost.
            AppLog.ExtensionStoreWriteFailed(Log, _extensionId, file, ex.Message);
        }
    }

    private static void WriteFile(string file, Header header, ReadOnlySpan<byte> payload)
    {
        byte[] content = payload.ToArray();
        AtomicFile.Write(file, stream =>
        {
            using GZipStream gzip = new(stream, CompressionLevel.Fastest, true);
            gzip.Write(JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));
            gzip.WriteByte((byte)'\n');
            gzip.Write(content);
        });
    }

    private static (Header Header, byte[] Content) ReadFile(string file)
    {
        using FileStream stream = File.OpenRead(file);
        using GZipStream gzip = new(stream, CompressionMode.Decompress);
        using MemoryStream all = new();
        gzip.CopyTo(all);
        byte[] bytes = all.ToArray();
        int newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline < 0)
        {
            throw new InvalidDataException("No header line.");
        }

        Header header = JsonSerializer.Deserialize<Header>(bytes.AsSpan(0, newline), JsonOptions)
                        ?? throw new InvalidDataException("Empty header.");
        return (header, bytes[(newline + 1)..]);
    }

    private static Header ReadHeader(string file)
    {
        using FileStream stream = File.OpenRead(file);
        using GZipStream gzip = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8);
        string line = reader.ReadLine() ?? throw new InvalidDataException("No header line.");
        return JsonSerializer.Deserialize<Header>(line, JsonOptions) ?? throw new InvalidDataException("Empty header.");
    }

    private sealed record Header(
        int Format,
        string Facet,
        string? Part,
        int Schema,
        string? Fingerprint,
        string? Sha256,
        string? DemoPath,
        string? ExtensionVersion,
        long WrittenAtTicks);

    private sealed record Entry(
        string Key,
        string Facet,
        string DemoPath,
        string? Sha256,
        int Schema,
        string? Fingerprint,
        DemoDataState State,
        long WrittenAtTicks,
        int Count)
    {
        public DemoDataStamp ToStamp() => new(DemoPath, Sha256, Facet, Schema, Fingerprint, State, WrittenAtTicks, Count);
    }

    private sealed record IndexFile(int Version, List<Entry> Entries)
    {
        [JsonIgnore]
        public long Sequence { get; init; }
    }
}

/// <summary>The browser build's per-demo data: nothing is kept, and every read finds nothing.</summary>
internal sealed class UnavailableDemoData : IExtensionDemoData
{
    public static UnavailableDemoData Instance { get; } = new();

    public bool IsAvailable => false;

    public DemoDataStamp? Stamp(string demoPath, string facet) => null;

    public IReadOnlyList<DemoDataStamp> Stamps(string facet) => [];

    public byte[]? Read(string demoPath, string facet, int schema, string? fingerprint, string? part = null) => null;

    public DemoDataRecord? ReadAny(string demoPath, string facet, string? part = null) => null;

    public DemoDataStamp? Write(string demoPath, DemoDataWrite write) => null;

    public void MarkFailed(string demoPath, string facet)
    {
    }

    public void ClearFailed(string demoPath, string facet)
    {
    }

    public void Invalidate(string facet, string? demoPath = null)
    {
    }

    public void SetCount(string demoPath, string facet, int count)
    {
    }

    public void Delete(string demoPath, string facet)
    {
    }

    public event Action<string?>? Changed
    {
        add { }
        remove { }
    }
}
