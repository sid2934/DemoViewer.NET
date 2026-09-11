#region

using CS2DemoKit.Analysis.Visibility;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Per-process cache of built <see cref="VisibilityEngine" />s, keyed by the bake file's identity.
///     Three consumers (the analysis run, the Stats visibility replay and the Playback2D vision
///     overlay) each want the engine for the same map; without this every one of them re-reads the
///     <c>collision.tris</c> (7 to 35 MB) and rebuilds the BVH (up to a second on de_ancient).
///     <para>
///         Consumers that ask concurrently get the SAME instance and may query it at the same time.
///         That is sound only because <see cref="VisibilityEngine" /> is immutable after construction
///         and documents its queries as thread-safe (its one field is a readonly BVH whose traversal
///         scratch is stack-allocated). An engine change that adds per-instance mutable scratch
///         breaks this cache, not just the engine.
///     </para>
///     <para>
///         <b>Lifetime policy: a strong LRU with a fixed entry cap, DEFAULT <see cref="DefaultCapacity" />.</b>
///         A built engine retains 14 to 75 MB, so a never-evicting map would leak several hundred
///         MB over a session that browses a few maps. Weak references were rejected because the
///         engine is exactly the kind of object the GC reclaims between the analysis run and the
///         Stats click that wants it next, which would make the hit rate a coin toss; tying the
///         lifetime to a demo close was rejected because the whole point is that the NEXT demo on
///         the same map pays nothing. Only BUILT entries count against the cap and only built
///         entries are evicted: an entry whose build is in flight is never dropped, so two asks for
///         one bake always share one build. Settled-state retention is therefore <c>Capacity</c>
///         times the largest bake (2 x 75 MB with today's assets); while builds are in flight the
///         ceiling is <c>Capacity</c> plus one engine per distinct bake being built, which is at
///         most one per consumer. An evicted engine that a view model still holds lives on through
///         that reference alone, so eviction never invalidates a caller's copy.
///     </para>
///     <para>
///         The key is full path plus file length plus last-write time, so a re-baked asset at the
///         same path is a miss rather than stale geometry. On Windows the path compares
///         case-insensitively, so an env override spelled differently from the bundle walk-up does
///         not build the same file twice; elsewhere two spellings may be two files and stay two
///         keys. Two concurrent asks for one bake share one build: the first caller starts it on
///         the thread pool and every caller, including the first, awaits the same task, so the UI
///         thread is never blocked while it runs. A failed build is forgotten so the next ask
///         retries rather than replaying the exception.
///     </para>
/// </summary>
public sealed class VisibilityEngineCache
{
    /// <summary>
    ///     Entries kept alive by default. Two covers the common shapes without thrash: one demo with
    ///     three consumers on one map, or a user alternating between two demos on two maps.
    /// </summary>
    public const int DefaultCapacity = 2;

    private readonly int _capacity;
    private readonly Dictionary<BakeKey, Entry> _entries = [];
    private readonly Func<string, VisibilityEngine> _loader;
    private readonly object _sync = new();
    private long _builds;
    private long _clock;
    private long _hits;

    /// <summary>
    ///     Creates a cache. <paramref name="loader" /> defaults to <see cref="VisibilityEngine.Load" />;
    ///     tests substitute a counting loader over a synthetic mesh.
    /// </summary>
    /// <param name="capacity">Strong entries to keep; clamped to at least 1.</param>
    /// <param name="loader">Builds an engine from a bake path. Runs on the thread pool.</param>
    public VisibilityEngineCache(int capacity = DefaultCapacity, Func<string, VisibilityEngine>? loader = null)
    {
        _capacity = Math.Max(1, capacity);
        _loader = loader ?? VisibilityEngine.Load;
    }

    /// <summary>The process-wide instance every view model shares.</summary>
    public static VisibilityEngineCache Shared { get; } = new();

    /// <summary>Built entries this cache will hold at most once every build in flight has landed.</summary>
    public int Capacity => _capacity;

    /// <summary>Entries currently held, built or building.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    ///     Builds started over the cache's lifetime, whether they succeeded or not. Diagnostic: a
    ///     consumer that sees this climb once per ask has a key that never repeats.
    /// </summary>
    public long Builds => Interlocked.Read(ref _builds);

    /// <summary>
    ///     Asks served by an entry that already existed, built or still building. With
    ///     <see cref="Builds" /> this is the hit rate; it also tells a test that an ask has
    ///     registered, which the pooled file stat makes asynchronous to the call.
    /// </summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>
    ///     Returns the engine for <paramref name="trisPath" />, building it once per bake identity.
    ///     <para>
    ///         <paramref name="token" /> abandons THIS caller's wait only. The build starts and keeps
    ///         running and lands in the cache, even for a token that was already signalled, because
    ///         another consumer is likely to want it, and because a half-cancelled build would
    ///         otherwise be repeated by the next ask. The file stat that forms the key runs on the
    ///         thread pool too, so a slow (network, cloud-synced) assets directory never stalls the
    ///         dispatcher.
    ///     </para>
    /// </summary>
    /// <exception cref="FileNotFoundException">The bake does not exist.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="token" /> was signalled.</exception>
    public async Task<VisibilityEngine> GetOrLoadAsync(string trisPath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(trisPath);

        BakeKey key = await Task.Run(() => BakeKey.For(trisPath)).ConfigureAwait(false);
        Task<VisibilityEngine> build = Acquire(key, trisPath);
        // Checked AFTER the build is registered: a cancelled caller gives up its wait, not the build.
        token.ThrowIfCancellationRequested();
        return await build.WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Synchronous <see cref="GetOrLoadAsync" /> for a caller that is prepared to block (the
    ///     Playback2D overlay's synchronous test seam, and the UiCapture variant that drives it from
    ///     whatever thread it is on; the production path awaits the async form from a pool thread).
    ///     Blocks the calling thread for the whole build. It cannot deadlock the dispatcher, because
    ///     every await inside the cache is <c>ConfigureAwait(false)</c>, but a UI thread that calls it
    ///     is frozen for up to a second, which is why no production path does.
    /// </summary>
    public VisibilityEngine GetOrLoad(string trisPath)
    {
        return GetOrLoadAsync(trisPath).GetAwaiter().GetResult();
    }

    /// <summary>Drops every entry. In-flight builds complete for their waiters and are then discarded.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private Task<VisibilityEngine> Acquire(BakeKey key, string trisPath)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out Entry? hit))
            {
                hit.LastUse = ++_clock;
                Interlocked.Increment(ref _hits);
                return hit.Build;
            }

            // Registered before the build starts so a build that faults at once finds its own entry
            // to forget rather than a hole, whatever the thread pool does with timing.
            Entry entry = new(key);
            entry.LastUse = ++_clock;
            _entries[key] = entry;
            entry.Build = BuildAsync(entry, trisPath);
            Interlocked.Increment(ref _builds);
            EvictOverCapacity();
            return entry.Build;
        }
    }

    private async Task<VisibilityEngine> BuildAsync(Entry entry, string trisPath)
    {
        VisibilityEngine engine;
        try
        {
            engine = await Task.Run(() => _loader(trisPath)).ConfigureAwait(false);
        }
        catch
        {
            Forget(entry);
            throw;
        }

        // Only now does the entry count against the cap; it may itself be the LRU victim, in which
        // case its waiters keep the engine through this task and nothing is lost.
        lock (_sync)
        {
            entry.IsBuilt = true;
            EvictOverCapacity();
        }

        return engine;
    }

    // Removes the entry only if it is still the one registered for its key: a re-bake may already
    // have replaced it, and that replacement must not be dropped for the old build's failure.
    private void Forget(Entry entry)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(entry.Key, out Entry? current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.Key);
            }
        }
    }

    // Caller holds _sync. Evicts the least recently used BUILT entry while more than Capacity are
    // built; an entry still building is never a victim. Linear over at most Capacity plus the
    // in-flight builds, so no ordered structure is kept.
    private void EvictOverCapacity()
    {
        while (true)
        {
            int built = 0;
            BakeKey oldestKey = default;
            long oldestUse = long.MaxValue;
            foreach (KeyValuePair<BakeKey, Entry> pair in _entries)
            {
                if (!pair.Value.IsBuilt)
                {
                    continue;
                }

                built++;
                if (pair.Value.LastUse < oldestUse)
                {
                    oldestUse = pair.Value.LastUse;
                    oldestKey = pair.Key;
                }
            }

            if (built <= _capacity)
            {
                return;
            }

            _entries.Remove(oldestKey);
        }
    }

    /// <summary>
    ///     Identity of a bake on disk. Length and last-write time change on every re-bake, so a
    ///     stale engine cannot be served for a path whose contents moved under it. The path
    ///     compares with the platform's default file-system case rule (folded on Windows only).
    /// </summary>
    private readonly struct BakeKey(string fullPath, long length, DateTime lastWriteUtc) : IEquatable<BakeKey>
    {
        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public string FullPath { get; } = fullPath;
        public long Length { get; } = length;
        public DateTime LastWriteUtc { get; } = lastWriteUtc;

        public static BakeKey For(string trisPath)
        {
            FileInfo file = new(Path.GetFullPath(trisPath));
            if (!file.Exists)
            {
                throw new FileNotFoundException("Collision bake not found.", file.FullName);
            }

            return new BakeKey(file.FullName, file.Length, file.LastWriteTimeUtc);
        }

        public bool Equals(BakeKey other)
        {
            return Length == other.Length
                   && LastWriteUtc == other.LastWriteUtc
                   && PathComparer.Equals(FullPath, other.FullPath);
        }

        public override bool Equals(object? obj)
        {
            return obj is BakeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(PathComparer.GetHashCode(FullPath ?? string.Empty), Length, LastWriteUtc);
        }
    }

    private sealed class Entry(BakeKey key)
    {
        public BakeKey Key { get; } = key;
        public Task<VisibilityEngine> Build { get; set; } = null!;
        public bool IsBuilt { get; set; }
        public long LastUse { get; set; }
    }
}
