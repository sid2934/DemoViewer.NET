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
    private readonly Action<Action> _post;
    private readonly string _root;
    private Dictionary<(string Key, string Facet), Entry>? _entries;

    /// <param name="extensionId">The extension the data belongs to.</param>
    /// <param name="extensionCacheFolder">The extension's own cache folder; the store lives in its <see cref="DirectoryName" /> subfolder.</param>
    /// <param name="library">The library: a demo's content hash, and which demos are still in it.</param>
    /// <param name="post">Runs a change notice on the UI thread.</param>
    /// <param name="extensionVersion">The extension's version, recorded in every header.</param>
    public ExtensionDemoDataStore(string extensionId, string extensionCacheFolder, DemoCacheStore library, Action<Action> post,
        string? extensionVersion)
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
        lock (_gate)
        {
            return Find(demoPath, facet)?.ToStamp();
        }
    }

    public IReadOnlyList<DemoDataStamp> Stamps(string facet)
    {
        ExtensionFolders.SafeName(facet);
        lock (_gate)
        {
            return [.. Entries().Values.Where(e => string.Equals(e.Facet, facet, StringComparison.Ordinal)).Select(e => e.ToStamp())];
        }
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

        string? key;
        lock (_gate)
        {
            key = Find(demoPath, facet)?.Key;
        }

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
        lock (_gate)
        {
            Dictionary<(string, string), Entry> entries = Entries();
            if (sha is not null)
            {
                Drop(entries, (PathKey(demoPath), write.Facet));
            }

            entries[(key, write.Facet)] = entry;
            SaveIndex();
        }

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

            SaveIndex();
        }

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
        lock (_gate)
        {
            if (Find(demoPath, facet) is not { } entry)
            {
                return;
            }

            Drop(Entries(), (entry.Key, facet));
            SaveIndex();
        }

        Raise(demoPath);
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
        lock (_gate)
        {
            Entry? entry = Find(demoPath, facet);
            if (entry is null)
            {
                if (!create)
                {
                    return;
                }

                string? sha = Sha256Of(demoPath);
                entry = new Entry(KeyFor(demoPath, sha), facet, demoPath, sha, 0, null, DemoDataState.Pending, 0, 0);
            }

            Entry changed = change(entry);
            if (changed == entry && Entries().ContainsKey((entry.Key, facet)))
            {
                return;
            }

            Entries()[(entry.Key, facet)] = changed;
            SaveIndex();
        }

        Raise(demoPath);
    }

    // Called under the lock. The content hash's entry, else the path's; a path entry whose demo now has a
    // known hash moves over to it, files and all.
    private Entry? Find(string demoPath, string facet)
    {
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
        SaveIndex();
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

    // Called under the lock. Removes the entry and its files; a file that will not delete is left for the next sweep.
    private void Drop(Dictionary<(string Key, string Facet), Entry> entries, (string Key, string Facet) id)
    {
        entries.Remove(id);
        foreach (string file in FilesOf(id.Facet, id.Key))
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

    // A demo the library dropped takes its files along, unless another demo in the library has its content.
    private void OnLibraryChanged(string? path)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_entries is null)
            {
                return; // nothing loaded, nothing to keep in step; the load sweeps
            }

            changed = Sweep(path);
        }

        if (changed)
        {
            Raise(path);
        }
    }

    // Called under the lock. Null sweeps every entry.
    private bool Sweep(string? path)
    {
        Dictionary<(string Key, string Facet), Entry> entries = Entries();
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
                Drop(entries, id);
            }

            changed = true;
        }

        if (changed)
        {
            SaveIndex();
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

    // Called under the lock.
    private Dictionary<(string Key, string Facet), Entry> Entries()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = LoadIndex() ?? RebuildIndex();
        if (_library.Count > 0)
        {
            Sweep(null);
        }

        return _entries;
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

    // Called under the lock.
    private void SaveIndex()
    {
        string file = Path.Combine(_root, IndexFileName);
        IndexFile index = new(Format, [.. _entries!.Values]);
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

    private sealed record IndexFile(int Version, List<Entry> Entries);
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
