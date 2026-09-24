#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The <c>.dvri.json</c> sidecars and their <c>.dvrp.json.gz</c> positions siblings: one pair per
///     demo under <c>&lt;config&gt;/cache/round-index/</c>, named by <see cref="DemoCacheStore.StableKey" />
///     like the record sidecars beside them. The stamp that says whether a pair is current lives on the
///     record; this store only holds the payloads.
///     <para>
///         Same durability rules as the cache it sits beside: atomic writes (temp file and replace) so
///         a crash mid-write never leaves a half-file that reads as an index, in memory when there is no
///         cache root (the browser host, tests), and a corrupt file is simply "absent" so its demo
///         re-indexes. Two things the cache does not do for a sibling directory it does not know about
///         are done here: a <see cref="DemoCacheStore.Changed" /> subscriber deletes the files of a
///         demo that left the index, and <see cref="SweepOrphans" /> deletes at startup any file whose
///         key the index no longer carries. Orphans are harmless, they just cost disk.
///     </para>
///     <para>
///         The positions file is written first and the sidecar second (the evaluator's order), so a
///         crash between them leaves a positions file with no index, which the sweep or the next build
///         overwrites, and never an index whose cards have nothing to draw.
///     </para>
/// </summary>
public sealed class RoundIndexStore : IDisposable
{
    /// <summary>The sidecar suffix; the whole file name is <c>&lt;StableKey&gt;.dvri.json</c>.</summary>
    public const string Suffix = ".dvri.json";

    /// <summary>The positions sibling's suffix; the whole file name is <c>&lt;StableKey&gt;.dvrp.json.gz</c>.</summary>
    public const string PositionsSuffix = ".dvrp.json.gz";

    private readonly DemoCacheStore _demoCache;
    private readonly object _gate = new();

    // In-memory sidecars and positions, keyed by stable key, used when there is no cache root.
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _memoryPositions = new(StringComparer.Ordinal);
    private readonly string? _root;

    private bool _disposed;

    /// <param name="cacheRoot">The cache directory (<c>&lt;config&gt;/cache</c>), or null for an in-memory store.</param>
    /// <param name="demoCache">The index the sidecars follow: a demo it forgets loses its files here.</param>
    public RoundIndexStore(string? cacheRoot, DemoCacheStore demoCache)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        _root = cacheRoot is null ? null : Path.Combine(cacheRoot, "round-index");
        _demoCache = demoCache;
        _demoCache.Changed += OnCacheChanged;
    }

    /// <summary>The sidecar directory, or null in memory.</summary>
    public string? Root => _root;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _demoCache.Changed -= OnCacheChanged;
    }

    /// <summary>The sidecar path for a demo, or null in memory.</summary>
    /// <param name="demoPath">The demo's path as the library knows it.</param>
    public string? PathFor(string demoPath) =>
        _root is null ? null : Path.Combine(_root, DemoCacheStore.StableKey(demoPath) + Suffix);

    /// <summary>The positions file path for a demo, or null in memory.</summary>
    /// <param name="demoPath">The demo's path as the library knows it.</param>
    public string? PositionsPathFor(string demoPath) =>
        _root is null ? null : Path.Combine(_root, DemoCacheStore.StableKey(demoPath) + PositionsSuffix);

    /// <summary>Writes a demo's sidecar atomically. Throws on an I/O failure: the evaluator stamps Failed from it.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="document">The index to write.</param>
    public void Write(string demoPath, RoundIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string json = document.Serialize();
        string? file = PathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                _memory[DemoCacheStore.StableKey(demoPath)] = json;
            }

            return;
        }

        WriteAtomic(file, tempPath => File.WriteAllText(tempPath, json));
    }

    /// <summary>Writes a demo's positions file atomically, gzipped. Throws on an I/O failure like <see cref="Write" />.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="positions">The positions to write.</param>
    public void WritePositions(string demoPath, RoundPositionsDocument positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        byte[] bytes = positions.SerializeGzip();
        string? file = PositionsPathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                _memoryPositions[DemoCacheStore.StableKey(demoPath)] = bytes;
            }

            return;
        }

        WriteAtomic(file, tempPath => File.WriteAllBytes(tempPath, bytes));
    }

    /// <summary>A demo's sidecar, or null when it is missing or does not parse.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public RoundIndexDocument? TryRead(string demoPath)
    {
        string? json = TryReadText(demoPath);
        return json is null ? null : RoundIndexDocument.TryDeserialize(json);
    }

    /// <summary>
    ///     A demo's positions file, or null when it is missing, does not inflate or parse, was built
    ///     under another fingerprint than <paramref name="expectedFingerprint" />, or names another
    ///     demo's hash than <paramref name="sha256" /> (the sidecar's own rule: a reader with a hash
    ///     ignores a mismatching file). A stale file is "absent", so a card shows the placeholder rather
    ///     than positions the current rows were not built from.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="expectedFingerprint">The fingerprint in force, or null to accept any.</param>
    /// <param name="sha256">The record's hash, or null when neither side has one.</param>
    public RoundPositionsDocument? TryReadPositions(string demoPath, string? expectedFingerprint = null,
        string? sha256 = null)
    {
        byte[]? bytes = TryReadPositionsBytes(demoPath);
        RoundPositionsDocument? positions = bytes is null ? null : RoundPositionsDocument.TryDeserializeGzip(bytes);
        if (positions is null)
        {
            return null;
        }

        if (expectedFingerprint is not null
            && !string.Equals(positions.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        if (sha256 is not null && positions.Demo.Sha256 is { } written
            && !string.Equals(written, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return positions;
    }

    /// <summary>A demo's sidecar text, or null when it is missing or unreadable.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public string? TryReadText(string demoPath)
    {
        string? file = PathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                return _memory.GetValueOrDefault(DemoCacheStore.StableKey(demoPath));
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

    /// <summary>Forgets a demo's sidecar and positions file. Best effort: a file that will not delete is an orphan the next sweep takes.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void Delete(string demoPath)
    {
        string? file = PathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                string key = DemoCacheStore.StableKey(demoPath);
                _memory.Remove(key);
                _memoryPositions.Remove(key);
            }

            return;
        }

        TryDelete(file);
        TryDelete(PositionsPathFor(demoPath)!);
    }

    /// <summary>
    ///     Deletes every sidecar and positions file whose key is not in the index: demos removed while
    ///     the app was not running, or whose record lost its stamp. Returns how many files went. Call
    ///     it off the UI thread.
    /// </summary>
    public int SweepOrphans()
    {
        HashSet<string> live = new(StringComparer.Ordinal);
        foreach (DemoCacheIndexEntry entry in _demoCache.Index)
        {
            live.Add(DemoCacheStore.StableKey(entry.Path));
        }

        if (_root is null)
        {
            lock (_gate)
            {
                List<string> gone = [.. _memory.Keys.Where(k => !live.Contains(k))];
                foreach (string key in gone)
                {
                    _memory.Remove(key);
                }

                List<string> gonePositions = [.. _memoryPositions.Keys.Where(k => !live.Contains(k))];
                foreach (string key in gonePositions)
                {
                    _memoryPositions.Remove(key);
                }

                return gone.Count + gonePositions.Count;
            }
        }

        int removed = 0;
        try
        {
            if (!Directory.Exists(_root))
            {
                return 0;
            }

            foreach ((string pattern, string suffix) in new[] { ("*" + Suffix, Suffix), ("*" + PositionsSuffix, PositionsSuffix) })
            {
                foreach (string file in Directory.EnumerateFiles(_root, pattern))
                {
                    string name = Path.GetFileName(file);
                    string key = name[..^suffix.Length];
                    if (live.Contains(key))
                    {
                        continue;
                    }

                    if (TryDelete(file))
                    {
                        removed++;
                    }
                }
            }
        }
        catch (Exception)
        {
            // A directory that cannot be listed has nothing to sweep.
        }

        return removed;
    }

    private byte[]? TryReadPositionsBytes(string demoPath)
    {
        string? file = PositionsPathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                return _memoryPositions.GetValueOrDefault(DemoCacheStore.StableKey(demoPath));
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

    // A path that changed and is no longer in the index was removed: its files go with it. A null
    // path is a batch; the startup sweep and the query service's diff cover those.
    private void OnCacheChanged(string? path)
    {
        if (path is not null && _demoCache.TryGetIndex(path) is null)
        {
            Delete(path);
        }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
            return true;
        }
        catch (Exception)
        {
            return false; // Best effort.
        }
    }

    private static void WriteAtomic(string targetPath, Action<string> writeTemp)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".ri-{Guid.NewGuid():N}.tmp");
        writeTemp(tempPath);
        if (File.Exists(targetPath))
        {
            File.Replace(tempPath, targetPath, null);
        }
        else
        {
            File.Move(tempPath, targetPath);
        }
    }
}
