#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The <c>.dvri.json</c> sidecars: one per demo under <c>&lt;config&gt;/cache/round-index/</c>,
///     named by <see cref="DemoCacheStore.StableKey" /> like the record sidecars beside them. The stamp
///     that says whether a sidecar is current lives on the record; this store only holds the payload.
///     <para>
///         Same durability rules as the cache it sits beside: atomic writes (temp file and replace) so
///         a crash mid-write never leaves a half-file that reads as an index, in memory when there is no
///         cache root (the browser host, tests), and a corrupt file is simply "absent" so its demo
///         re-indexes. Two things the cache does not do for a sibling directory it does not know about
///         are done here: a <see cref="DemoCacheStore.Changed" /> subscriber deletes the sidecar of a
///         demo that left the index, and <see cref="SweepOrphans" /> deletes at startup any sidecar
///         whose key the index no longer carries. Orphans are harmless, they just cost disk.
///     </para>
/// </summary>
public sealed class RoundIndexStore : IDisposable
{
    /// <summary>The sidecar suffix; the whole file name is <c>&lt;StableKey&gt;.dvri.json</c>.</summary>
    public const string Suffix = ".dvri.json";

    private readonly DemoCacheStore _demoCache;
    private readonly object _gate = new();

    // In-memory sidecars, keyed by stable key, used when there is no cache root.
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly string? _root;

    private bool _disposed;

    /// <param name="cacheRoot">The cache directory (<c>&lt;config&gt;/cache</c>), or null for an in-memory store.</param>
    /// <param name="demoCache">The index the sidecars follow: a demo it forgets loses its sidecar here.</param>
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

        WriteAtomic(file, json);
    }

    /// <summary>A demo's sidecar, or null when it is missing or does not parse.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public RoundIndexDocument? TryRead(string demoPath)
    {
        string? json = TryReadText(demoPath);
        return json is null ? null : RoundIndexDocument.TryDeserialize(json);
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

    /// <summary>Forgets a demo's sidecar. Best effort: a sidecar that will not delete is an orphan the next sweep takes.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void Delete(string demoPath)
    {
        string? file = PathFor(demoPath);
        if (file is null)
        {
            lock (_gate)
            {
                _memory.Remove(DemoCacheStore.StableKey(demoPath));
            }

            return;
        }

        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>
    ///     Deletes every sidecar whose key is not in the index: demos removed while the app was not
    ///     running, or whose record lost its stamp. Returns how many went. Call it off the UI thread.
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

                return gone.Count;
            }
        }

        int removed = 0;
        try
        {
            if (!Directory.Exists(_root))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(_root, "*" + Suffix))
            {
                string name = Path.GetFileName(file);
                string key = name[..^Suffix.Length];
                if (live.Contains(key))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }
        }
        catch (Exception)
        {
            // A directory that cannot be listed has nothing to sweep.
        }

        return removed;
    }

    // A path that changed and is no longer in the index was removed: its sidecar goes with it. A null
    // path is a batch; the startup sweep and the query service's diff cover those.
    private void OnCacheChanged(string? path)
    {
        if (path is not null && _demoCache.TryGetIndex(path) is null)
        {
            Delete(path);
        }
    }

    private static void WriteAtomic(string targetPath, string content)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".ri-{Guid.NewGuid():N}.tmp");
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
}
