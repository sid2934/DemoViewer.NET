#region

using System.Globalization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>Renders the GIFs of one demo's planned clips. The seam every Lineup Clip test replaces.</summary>
public interface ILineupClipRenderer
{
    /// <summary>
    ///     Renders each job's GIF to its <see cref="LineupClipJob.GifPath" />. Runs on a worker; one demo
    ///     per call, so the demo is parsed once for all of its clips.
    /// </summary>
    /// <param name="demoPath">The demo every job is in.</param>
    /// <param name="jobs">That demo's clips.</param>
    /// <param name="ct">Stops the batch; a GIF cut short does not survive.</param>
    /// <returns>The jobs whose GIF was written.</returns>
    Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, IReadOnlyList<LineupClipJob> jobs,
        CancellationToken ct);
}

/// <summary>
///     Lineup Clip Render (plan.md §3, Phase 4): every Lineup Card with a repeated throw position gets a
///     short GIF of the throw, the camera on the thrower, written beside the <c>setpos</c>/<c>setang</c>
///     line it was thrown from, with no one pressing anything.
///     <para>
///         <b>Queued through the Review Queue.</b> <see cref="Plan" /> puts each clip in the queue under a
///         "Lineup clips, &lt;map&gt;" title card, one card per map and one entry per lineup, and the worker
///         renders only clips still in it: taking a clip out of the queue before its turn is how a user says
///         not to render it. Planning runs when the Grenade Index changes, so a demo indexed in the
///         background has its clips queued and rendered after it.
///     </para>
///     <para>
///         <b>The pair.</b> The renderer writes the GIF through the existing export session; this service
///         writes the sidecar only once the GIF exists, so a pair on disk is always a finished one, and a
///         lineup is planned again while either half is missing. Pairs are named by
///         <see cref="GrenadeLineup.Id" />, so a new representative throw keeps the pair it has.
///     </para>
///     <para>
///         <b>Bounded.</b> Once the index is complete, a pair no current lineup names is deleted after
///         <see cref="DefaultOrphanGrace" />, and queue entries of lineups that are gone are dropped. Clips are
///         planned most-thrown first; once the byte cap is full a lineup is planned only if it outranks the
///         lowest-ranked pair on disk, so a full directory stops rendering instead of trading one kept clip for
///         another. After each render the directory is held under the cap by deleting the lowest-ranked pairs;
///         an evicted pair is listed in <see cref="EvictedFileName" /> and rendered again only through
///         <see cref="Request" />.
///     </para>
///     <para>
///         <b>Threading.</b> <see cref="Plan" /> and the queue's <c>Changed</c> run on the UI thread (the
///         queue's own rule). Renders run one demo at a time as <see cref="QueueJobKind.LineupClips" /> items of
///         the processing queue, one item at a time; the sweep runs on the pool.
///     </para>
/// </summary>
public sealed class LineupClipService : IDisposable
{
    /// <summary>The file in the clip directory listing evicted pair stems, one per line.</summary>
    public const string EvictedFileName = "evicted.txt";

    /// <summary>
    ///     How long a pair must go unplanned before the sweep deletes it. A re-index drops a demo's rows for
    ///     a moment, and its lineups with them.
    /// </summary>
    public static readonly TimeSpan DefaultOrphanGrace = TimeSpan.FromMinutes(10);

    private readonly Func<IReadOnlyList<GrenadeCluster>> _clusters;
    private readonly Func<bool> _complete;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _directory;
    private readonly Func<bool> _enabled;
    private readonly Func<string, bool>? _fileExists;
    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private readonly Func<long> _maxBytes;
    private readonly TimeSpan _orphanGrace;
    private readonly Dictionary<string, DateTime> _orphanSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, DateTime> _entryAbsentSince = [];
    private readonly Dictionary<string, DateTime> _evictedAbsentSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Guid EntryId, LineupClipJob Job)> _pending = [];
    private readonly Dictionary<Guid, string> _planned = [];
    private readonly IDemoProcessingQueue? _processing;
    private readonly ReviewQueue _queue;
    private readonly Dictionary<string, int> _ranks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _requested = [];
    private readonly ILineupClipRenderer _renderer;
    private readonly object _sweepGate = new();

    // Held by EnforceCap from its listing to its last delete, and by Request while it saves a pair, so a
    // pair asked for mid-pass is either seen as just used or evicted and then un-evicted.
    private readonly object _capGate = new();
    private readonly Action<string, string> _writeText;
    private bool _disposed;
    private HashSet<string>? _evicted;
    private TaskCompletionSource? _idle;
    private bool _running;

    /// <param name="clusters">Every landing cluster in the Grenade Index, all maps.</param>
    /// <param name="queue">The Review Queue the clips are put in.</param>
    /// <param name="directory">Where the pairs are written; null (the browser) plans nothing.</param>
    /// <param name="enabled">The live <c>GrenadesSettings.RenderLineupClips</c>.</param>
    /// <param name="renderer">Renders a demo's GIFs.</param>
    /// <param name="fileExists">The existence probe; a listing of the directory taken per plan when null.</param>
    /// <param name="writeText">Writes a sidecar; an atomic write when null.</param>
    /// <param name="log">Optional line sink.</param>
    /// <param name="complete">
    ///     True once the index holds the whole library; the sweep and the queue pruning wait for it. Always
    ///     true when null.
    /// </param>
    /// <param name="maxBytes">The live byte cap on the directory; zero or less, or null, caps nothing.</param>
    /// <param name="orphanGrace">Overrides <see cref="DefaultOrphanGrace" />.</param>
    /// <param name="processing">
    ///     The processing queue each demo's batch runs in; null renders on a worker of this service (tests).
    /// </param>
    public LineupClipService(Func<IReadOnlyList<GrenadeCluster>> clusters, ReviewQueue queue, string? directory,
        Func<bool> enabled, ILineupClipRenderer renderer, Func<string, bool>? fileExists = null,
        Action<string, string>? writeText = null, Action<string>? log = null, Func<bool>? complete = null,
        Func<long>? maxBytes = null, TimeSpan? orphanGrace = null, IDemoProcessingQueue? processing = null)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(renderer);
        _clusters = clusters;
        _queue = queue;
        _directory = directory;
        _enabled = enabled;
        _renderer = renderer;
        _fileExists = fileExists;
        _writeText = writeText ?? DemoCacheStore.WriteAtomic;
        _log = log;
        _complete = complete ?? (static () => true);
        _maxBytes = maxBytes ?? (static () => 0);
        _orphanGrace = orphanGrace ?? DefaultOrphanGrace;
        _processing = processing;
        _queue.Changed += OnQueueChanged;
    }

    /// <summary>The clips queued and not yet rendered, oldest first.</summary>
    public IReadOnlyList<LineupClipJob> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending.Select(p => p.Job)];
            }
        }
    }

    /// <summary>The current run until nothing is pending, so a test can await it.</summary>
    internal Task WorkerTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last orphan sweep, so a test can await it.</summary>
    internal Task SweepTask { get; private set; } = Task.CompletedTask;

    /// <summary>The evicted pair stems, for a test.</summary>
    internal IReadOnlyCollection<string> Evicted
    {
        get
        {
            lock (_gate)
            {
                return [.. LoadEvicted()];
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Changed -= OnQueueChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    ///     Plans every clip the index calls for that has no pair on disk, was not evicted, and was not
    ///     planned earlier this session with the same representative; puts them in the Review Queue by map,
    ///     and starts the worker. A pair left under an alias or a pre-lineup-id name is renamed rather than
    ///     rendered again. UI thread.
    /// </summary>
    /// <returns>How many clips were planned.</returns>
    public int Plan()
    {
        if (_disposed || _directory is null || !_enabled())
        {
            return 0;
        }

        IReadOnlyList<LineupClipJob> every = LineupClipPlanner.PlanEvery(_clusters(), _directory);
        HashSet<string> listing = Listing(_directory);
        Func<string, bool> exists = _fileExists ?? listing.Contains;
        HashSet<string> evicted;
        lock (_gate)
        {
            evicted = new HashSet<string>(LoadEvicted(), StringComparer.OrdinalIgnoreCase);
        }

        HashSet<Guid> requested;
        lock (_gate)
        {
            _ranks.Clear();
            foreach (LineupClipJob job in every)
            {
                _ranks[job.Stem] = _requested.Contains(job.LineupId) ? int.MaxValue : job.Throws;
            }

            requested = [.. _requested];
        }

        List<LineupClipJob> candidates = [];
        // Stable: equal ranks keep the index's cluster order.
        foreach (LineupClipJob job in every.OrderByDescending(j => requested.Contains(j.LineupId))
                     .ThenByDescending(j => j.Throws))
        {
            bool gif = exists(job.GifPath);
            bool setpos = exists(job.SetposPath);
            if ((gif && setpos) || evicted.Contains(job.Stem) || Adopt(job)
                || (_planned.TryGetValue(job.LineupId, out string? key) && string.Equals(key, job.Key, StringComparison.Ordinal)))
            {
                continue;
            }

            candidates.Add(job);
        }

        List<LineupClipJob> jobs = [.. WithinCap(candidates, requested)];
        foreach (LineupClipJob job in jobs)
        {
            if (exists(job.GifPath) || exists(job.SetposPath))
            {
                DeletePair(job.GifPath);
            }

            _planned[job.LineupId] = job.Key;
        }

        if (_complete())
        {
            Prune(every);
        }

        if (jobs.Count == 0)
        {
            return 0;
        }

        foreach (IGrouping<string, LineupClipJob> map in jobs.GroupBy(j => j.Map, StringComparer.OrdinalIgnoreCase))
        {
            _queue.Merge(map.Select(LineupClipPlanner.ToReviewEntry), LineupClipPlanner.SectionTitle(map.Key),
                static (queued, incoming) => string.Equals(queued.Source, ReviewSources.Lineup, StringComparison.Ordinal)
                                             && queued.LineupId is { } id && id == incoming.LineupId);
        }

        // Found rather than taken from what was merged: a clip queued in an earlier session is skipped as a
        // duplicate, and it is still the entry this render belongs to.
        Dictionary<Guid, Guid> byLineup = [];
        Dictionary<(string, int, int), Guid> byRange = [];
        foreach (ReviewEntry clip in _queue.Clips.Where(c => string.Equals(c.Source, ReviewSources.Lineup, StringComparison.Ordinal)))
        {
            if (clip.LineupId is { } id)
            {
                byLineup.TryAdd(id, clip.Id);
            }
            else
            {
                byRange.TryAdd(RangeKey(clip.DemoPath, clip.FromTick, clip.ToTick), clip.Id);
            }
        }

        List<(Guid, LineupClipJob)> planned = [];
        foreach (LineupClipJob job in jobs)
        {
            if (byLineup.TryGetValue(job.LineupId, out Guid entry)
                || byRange.TryGetValue(RangeKey(job.DemoPath, job.FromTick, job.ToTick), out entry))
            {
                planned.Add((entry, job));
            }
        }

        HashSet<Guid> replanned = [.. jobs.Select(j => j.LineupId)];
        lock (_gate)
        {
            _pending.RemoveAll(p => replanned.Contains(p.Job.LineupId));
            _pending.AddRange(planned);
        }

        StartWorker();
        return jobs.Count;
    }

    /// <summary>
    ///     A lineup's clip is wanted now: an evicted pair is un-evicted and planned again, and a pair on disk
    ///     counts as just used, so the cap evicts it last. UI thread.
    /// </summary>
    /// <param name="lineupId"><see cref="GrenadeLineup.Id" /> or one of its aliases.</param>
    /// <returns>How many clips were planned.</returns>
    public int Request(Guid lineupId)
    {
        if (_disposed || _directory is null)
        {
            return 0;
        }

        if (LineupClipPlanner.PlanEvery(_clusters(), _directory)
                .FirstOrDefault(j => j.LineupId == lineupId || j.AliasIds.Contains(lineupId)) is not { } job)
        {
            return 0;
        }

        lock (_capGate)
        {
            lock (_gate)
            {
                _requested.Add(job.LineupId);
                if (LoadEvicted().Remove(job.Stem))
                {
                    SaveEvicted();
                }
            }

            try
            {
                if (File.Exists(job.GifPath))
                {
                    File.SetLastWriteTimeUtc(job.GifPath, DateTime.UtcNow);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"lineup clips: {job.GifPath}: {ex.Message}");
            }
        }

        _planned.Remove(job.LineupId);
        return Plan();
    }

    /// <summary>What a pair is guessed to take before any is on disk.</summary>
    internal const long DefaultPairBytes = 2L * 1024 * 1024;

    // The candidates, best first, that the cap has room for. Room is guessed at the mean pair size; past it, a
    // lineup goes in only by displacing a lower-ranked pair, never an equal one, so a full cap stops the renders.
    // A requested lineup always goes in.
    private IEnumerable<LineupClipJob> WithinCap(List<LineupClipJob> candidates, HashSet<Guid> requested)
    {
        long cap = _maxBytes();
        if (cap <= 0)
        {
            foreach (LineupClipJob job in candidates)
            {
                yield return job;
            }

            yield break;
        }

        Dictionary<string, (long Bytes, DateTime Used)> pairs = ReadPairs(_directory!) ?? [];
        long total = pairs.Values.Sum(p => p.Bytes);
        long mean = pairs.Count > 0 ? Math.Max(1, total / pairs.Count) : DefaultPairBytes;
        int alreadyPending;
        lock (_gate)
        {
            alreadyPending = _pending.Count(p => !candidates.Any(c => c.LineupId == p.Job.LineupId));
        }

        long room = cap - total - (alreadyPending * mean);
        List<(string Stem, int Rank, DateTime Used)> kept =
        [
            .. pairs.Select(p => (p.Key, _ranks.GetValueOrDefault(p.Key), p.Value.Used))
                .OrderBy(k => k.Item2).ThenBy(k => k.Used).ThenBy(k => k.Key, StringComparer.Ordinal)
        ];

        foreach (LineupClipJob job in candidates)
        {
            if (requested.Contains(job.LineupId) || room >= mean)
            {
                room -= mean;
                yield return job;
            }
            else if (kept.Count > 0 && job.Throws > kept[0].Rank)
            {
                kept.RemoveAt(0);
                yield return job;
            }
            else
            {
                yield break;
            }
        }
    }

    // A clip the user took out of the queue is not rendered.
    private void OnQueueChanged()
    {
        HashSet<Guid> ids = [.. _queue.Entries.Select(e => e.Id)];
        lock (_gate)
        {
            _pending.RemoveAll(p => !ids.Contains(p.EntryId));
        }
    }

    private static (string, int, int) RangeKey(string demoPath, int from, int to) => (demoPath.ToUpperInvariant(), from, to);

    // A finished pair under an alias's or an older build's name becomes this lineup's pair.
    private bool Adopt(LineupClipJob job)
    {
        foreach (string former in job.FormerGifPaths)
        {
            string formerSetpos = LineupClipPlanner.SetposPathFor(former);
            if (!File.Exists(former) || !File.Exists(formerSetpos))
            {
                continue;
            }

            try
            {
                File.Move(former, job.GifPath, true);
                File.Move(formerSetpos, job.SetposPath, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"lineup clips: {former}: {ex.Message}");
            }
        }

        return false;
    }

    // The whole index is in: forget lineups that are gone, fix up the queue, and sweep the directory.
    private void Prune(IReadOnlyList<LineupClipJob> every)
    {
        HashSet<Guid> current = [.. every.Select(j => j.LineupId)];
        foreach (Guid gone in _planned.Keys.Where(id => !current.Contains(id)).ToList())
        {
            _planned.Remove(gone);
        }

        ReconcileQueue(every);

        HashSet<string> keep = new(every.Select(j => j.Stem), StringComparer.OrdinalIgnoreCase);
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            HashSet<string> evicted = LoadEvicted();
            if (evicted.RemoveWhere(stem => !keep.Contains(stem) && GoneFor(_evictedAbsentSince, stem, now)) > 0)
            {
                SaveEvicted();
            }

            Forget(_evictedAbsentSince, evicted, keep);
        }

        if (SweepTask.IsCompleted)
        {
            string directory = _directory!;
            SweepTask = Task.Run(() => Sweep(directory, keep, now), CancellationToken.None);
        }
    }

    // One entry per current lineup: entries from before lineup ids are matched by demo and range, and
    // entries of lineups that are gone, or second entries of one lineup, are dropped.
    private void ReconcileQueue(IReadOnlyList<LineupClipJob> every)
    {
        Dictionary<Guid, Guid> canonical = [];
        Dictionary<(string, int, int), Guid> byRange = [];
        foreach (LineupClipJob job in every)
        {
            canonical[job.LineupId] = job.LineupId;
            foreach (Guid alias in job.AliasIds)
            {
                canonical.TryAdd(alias, job.LineupId);
            }

            byRange.TryAdd(RangeKey(job.DemoPath, job.FromTick, job.ToTick), job.LineupId);
        }

        HashSet<Guid> seen = [];
        HashSet<Guid> absent = [];
        DateTime now = DateTime.UtcNow;
        _queue.Reconcile(e =>
        {
            if (e.Kind != ReviewEntryKind.Clip || !string.Equals(e.Source, ReviewSources.Lineup, StringComparison.Ordinal))
            {
                return e;
            }

            // A stamped entry whose lineup is missing waits out the grace; an unstamped one that matches no
            // current range is from a re-key and goes now.
            if (e.LineupId is { } stored && !canonical.ContainsKey(stored))
            {
                absent.Add(stored);
                return GoneFor(_entryAbsentSince, stored, now) ? null : e;
            }

            Guid found;
            bool known = e.LineupId is { } id
                ? canonical.TryGetValue(id, out found)
                : byRange.TryGetValue(RangeKey(e.DemoPath, e.FromTick, e.ToTick), out found);
            if (!known || !seen.Add(found))
            {
                return null;
            }

            return e.LineupId == found ? e : e with { LineupId = found };
        }, IsLineupCard);
        Forget(_entryAbsentSince, absent, null);

        foreach (string title in _queue.Entries.Where(IsLineupCard).Select(e => e.Title).Distinct(StringComparer.Ordinal).ToList())
        {
            _queue.Merge([], title, static (_, _) => false);
        }
    }

    // Starts the clock for a key seen missing; true once it has been missing for the grace period.
    private bool GoneFor<TKey>(Dictionary<TKey, DateTime> since, TKey key, DateTime now) where TKey : notnull
    {
        if (!since.TryGetValue(key, out DateTime first))
        {
            since[key] = first = now;
        }

        if (now - first < _orphanGrace)
        {
            return false;
        }

        since.Remove(key);
        return true;
    }

    // Drops clocks for keys no longer missing: not in `missing`, or back in `current`.
    private static void Forget<TKey>(Dictionary<TKey, DateTime> since, HashSet<TKey> missing, HashSet<TKey>? current)
        where TKey : notnull
    {
        foreach (TKey key in since.Keys.Where(k => !missing.Contains(k) || (current?.Contains(k) ?? false)).ToList())
        {
            since.Remove(key);
        }
    }

    private static bool IsLineupCard(ReviewEntry e) =>
        e.Kind == ReviewEntryKind.Section
        && e.Title.StartsWith(LineupClipPlanner.SectionPrefix + ",", StringComparison.Ordinal);

    // Deletes pairs no current lineup names once they have stayed that way for the grace period.
    private void Sweep(string directory, HashSet<string> keep, DateTime now)
    {
        lock (_sweepGate)
        {
            HashSet<string> orphans = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Listing(directory))
            {
                if (LineupClipPlanner.StemOf(file) is { } stem && !keep.Contains(stem))
                {
                    orphans.Add(stem);
                }
            }

            foreach (string stale in _orphanSince.Keys.Where(s => !orphans.Contains(s)).ToList())
            {
                _orphanSince.Remove(stale);
            }

            int deleted = 0;
            foreach (string stem in orphans)
            {
                if (!_orphanSince.TryGetValue(stem, out DateTime since))
                {
                    _orphanSince[stem] = since = now;
                }

                if (now - since >= _orphanGrace)
                {
                    DeletePair(Path.Combine(directory, stem + LineupClipPlanner.GifExtension));
                    _orphanSince.Remove(stem);
                    deleted++;
                }
            }

            if (deleted > 0)
            {
                _log?.Invoke($"lineup clips: swept {deleted} orphaned pairs");
            }
        }
    }

    /// <summary>Runs inside the eviction pass, between the listing and the deletes. For a test.</summary>
    internal Action? BeforeEvictionDeletes { get; set; }

    // Holds the directory under the cap, lowest rank first, then oldest write. Worker thread, between renders.
    private void EnforceCap(string directory)
    {
        long cap = _maxBytes();
        if (cap <= 0)
        {
            return;
        }

        lock (_capGate)
        {
            EnforceCapLocked(directory, cap);
        }
    }

    // Every pair in the directory with its bytes and latest write; null when it cannot be listed.
    private static Dictionary<string, (long Bytes, DateTime Used)>? ReadPairs(string directory)
    {
        Dictionary<string, (long Bytes, DateTime Used)> pairs = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory))
            {
                return pairs;
            }

            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (LineupClipPlanner.StemOf(file.Name) is not { } stem)
                {
                    continue;
                }

                (long bytes, DateTime used) = pairs.GetValueOrDefault(stem);
                DateTime written = file.LastWriteTimeUtc;
                pairs[stem] = (bytes + file.Length, written > used ? written : used);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return pairs;
    }

    private void EnforceCapLocked(string directory, long cap)
    {
        if (ReadPairs(directory) is not { } pairs)
        {
            return;
        }

        BeforeEvictionDeletes?.Invoke();
        long total = pairs.Values.Sum(p => p.Bytes);
        Dictionary<string, int> ranks;
        lock (_gate)
        {
            ranks = new Dictionary<string, int>(_ranks, StringComparer.OrdinalIgnoreCase);
        }

        List<string> evicted = [];
        foreach ((string stem, (long bytes, _)) in pairs.OrderBy(p => ranks.GetValueOrDefault(p.Key))
                     .ThenBy(p => p.Value.Used).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (total <= cap)
            {
                break;
            }

            DeletePair(Path.Combine(directory, stem + LineupClipPlanner.GifExtension));
            total -= bytes;
            evicted.Add(stem);
        }

        if (evicted.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            LoadEvicted().UnionWith(evicted);
            SaveEvicted();
        }

        _log?.Invoke($"lineup clips: evicted {evicted.Count} pairs to stay under {cap / (1024 * 1024)} MB");
    }

    private void DeletePair(string gifPath)
    {
        foreach (string path in (ReadOnlySpan<string>)[gifPath, LineupClipPlanner.SetposPathFor(gifPath)])
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"lineup clips: {path}: {ex.Message}");
            }
        }
    }

    private static HashSet<string> Listing(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new HashSet<string>(Directory.EnumerateFiles(directory), StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Under _gate.
    private HashSet<string> LoadEvicted()
    {
        if (_evicted is not null)
        {
            return _evicted;
        }

        _evicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(_directory!, EvictedFileName);
            if (File.Exists(path))
            {
                _evicted.UnionWith(File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"lineup clips: {EvictedFileName}: {ex.Message}");
        }

        return _evicted;
    }

    // Under _gate.
    private void SaveEvicted()
    {
        try
        {
            Directory.CreateDirectory(_directory!);
            DemoCacheStore.WriteAtomic(Path.Combine(_directory!, EvictedFileName),
                string.Join('\n', LoadEvicted().Order(StringComparer.Ordinal)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"lineup clips: {EvictedFileName}: {ex.Message}");
        }
    }

    private void StartWorker()
    {
        TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_running || _pending.Count == 0)
            {
                return;
            }

            _running = true;
            _idle = idle;
        }

        WorkerTask = idle.Task;
        if (_processing is null)
        {
            CancellationToken ct = _cts.Token;
            _ = Task.Run(() => DrainAsync(ct), CancellationToken.None);
        }
        else
        {
            SubmitNext(_processing);
        }
    }

    // Runs every pending batch here, best demo first. Only without a processing queue.
    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && NextDemo() is { } demo)
        {
            await RenderBatchAsync(demo.Path, null, ct).ConfigureAwait(false);
        }

        Stop(true);
    }

    // One queue item at a time: the next demo is chosen when the last one ends, so a replan re-ranks it.
    private void SubmitNext(IDemoProcessingQueue processing)
    {
        if (_disposed || NextDemo() is not { } demo)
        {
            Stop(!_disposed);
            return;
        }

        string title = string.Create(CultureInfo.InvariantCulture,
            $"Lineup clips: {demo.Map}, {demo.Count} {(demo.Count == 1 ? "clip" : "clips")} from {Path.GetFileName(demo.Path)}");
        CancellationToken ct = _cts.Token;
        IDemoQueueHandle handle = processing.SubmitJob(new QueueJobRequest(QueueJobKind.LineupClips, title,
            "lineup-clips", demo.Requested ? DemoJobPriority.UserRequested : DemoJobPriority.Background,
            job => RenderBatchAsync(demo.Path, job, ct), "lineup-clips", demo.Path));
        if (handle.State == DemoQueueItemState.Rejected)
        {
            Stop(false);
            return;
        }

        _ = handle.Completion.ContinueWith(_ =>
        {
            // Cancelled from the queue list: this session does not render that demo's clips.
            if (handle.State == DemoQueueItemState.Cancelled)
            {
                lock (_gate)
                {
                    _pending.RemoveAll(p => string.Equals(p.Job.DemoPath, demo.Path, StringComparison.OrdinalIgnoreCase));
                }
            }

            SubmitNext(processing);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // Ends the run. A restart picks up a plan that landed while the last batch was finishing; a queue that
    // refused the item is not asked again until the next plan.
    private void Stop(bool restart)
    {
        TaskCompletionSource? idle;
        lock (_gate)
        {
            _running = false;
            idle = _idle;
            _idle = null;
        }

        idle?.TrySetResult();
        if (restart && !_disposed)
        {
            StartWorker();
        }
    }

    private sealed record NextBatch(string Path, string Map, int Count, bool Requested);

    // The demo of the best pending clip: a requested one, else the most thrown.
    private NextBatch? NextDemo()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            LineupClipJob best = _pending.Select(p => p.Job)
                .OrderByDescending(j => _requested.Contains(j.LineupId)).ThenByDescending(j => j.Throws).First();
            List<LineupClipJob> batch = [.. _pending.Select(p => p.Job)
                .Where(j => string.Equals(j.DemoPath, best.DemoPath, StringComparison.OrdinalIgnoreCase))];
            return new NextBatch(best.DemoPath, best.Map, batch.Count, batch.Any(j => _requested.Contains(j.LineupId)));
        }
    }

    // Takes the demo's clips still pending, so the demo is parsed once for all of them, and renders them.
    private async Task RenderBatchAsync(string demoPath, IQueueJobContext? job, CancellationToken ct)
    {
        List<LineupClipJob> batch;
        lock (_gate)
        {
            batch = [.. _pending.Where(p => string.Equals(p.Job.DemoPath, demoPath, StringComparison.OrdinalIgnoreCase)).Select(p => p.Job)];
            _pending.RemoveAll(p => string.Equals(p.Job.DemoPath, demoPath, StringComparison.OrdinalIgnoreCase));
        }

        if (batch.Count == 0 || _disposed)
        {
            return;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct,
            job?.CancellationToken ?? CancellationToken.None);
        job?.Report(0, batch.Count, string.Create(CultureInfo.InvariantCulture, $"rendering {batch.Count}"));
        IReadOnlyList<LineupClipJob> rendered;
        try
        {
            rendered = await _renderer.RenderAsync(demoPath, batch, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job is not null && job.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"lineup clips: {demoPath}: {ex.Message}");
            return;
        }

        foreach (LineupClipJob done in rendered)
        {
            WriteSidecar(done);
        }

        if (rendered.Count > 0)
        {
            EnforceCap(_directory!);
        }

        job?.Report(batch.Count, batch.Count, string.Create(CultureInfo.InvariantCulture, $"{rendered.Count} written"));
    }

    private void WriteSidecar(LineupClipJob job)
    {
        try
        {
            _writeText(job.SetposPath, job.ConsoleText + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"lineup clips: {job.SetposPath}: {ex.Message}");
        }
    }
}

/// <summary>
///     The production <see cref="ILineupClipRenderer" />: parses the demo once, inside the processing queue
///     item that holds the heavy-job slot, then renders each clip through <see cref="SceneExportRunner" />, the 2D export's own
///     runner and <c>SceneExportSession</c>, as a GIF (the managed encoder when no ffmpeg is installed).
///     Private everything, as every export: its own parse, map bundle, compositor and surface.
/// </summary>
public sealed class LineupClipRenderer : ILineupClipRenderer
{
    private readonly Func<string, LoadedMapAsset?> _loadMap;
    private readonly Action<string>? _log;
    private readonly Func<string, ParsedDemo> _parse;

    /// <param name="parse">Reads and parses a demo; when null, mapped if <see cref="MapsFile" />, else read into a byte[].</param>
    /// <param name="loadMap">Finds a map's baked bundle; the pipeline's loader when null.</param>
    /// <param name="log">Line sink for the encoder choice, ffmpeg's stderr and a failed clip.</param>
    /// <param name="time">The clock <see cref="MappedParsePolicy" /> judges a file settled by; the system clock when null.</param>
    public LineupClipRenderer(Func<string, ParsedDemo>? parse = null,
        Func<string, LoadedMapAsset?>? loadMap = null, Action<string>? log = null, TimeProvider? time = null)
    {
        TimeProvider clock = time ?? TimeProvider.System;
        _parse = parse ?? (path => MapsFile(path, clock, MappedParsePolicy.StatFile)
            ? MemoryMappedDemoSource.ParseFile(path)
            : DemoParser.Parse(File.ReadAllBytes(path).AsMemory()));
        _loadMap = loadMap ?? (map => MapAssetPipeline.TryLoad(map));
        _log = log;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, IReadOnlyList<LineupClipJob> jobs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0 || !File.Exists(demoPath))
        {
            return [];
        }

        ParsedDemo demo = _parse(demoPath);
        using LoadedMapAsset? asset = SafeLoad(jobs[0].Map);

        ExportSceneSetup setup = new(demo.Frames, demo.TickRate, jobs[0].Map, ScenePalette.Dark,
            LevelDisplayMode.Stacked, null, null, asset);
        SceneExportRunner runner = new(_ => setup, RenderSurfaceProviderFactory.CreateCpu,
            static () => FfmpegDependency.ManagedDirectory, _log, EncoderProbeCache.Shared);

        List<LineupClipJob> rendered = [];
        foreach (LineupClipJob job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (LineupClipPlanner.BuildRequest(job, demo.Frames, demo.TickRate) is not { } request)
            {
                _log?.Invoke($"lineup clips: {job.Key} lies outside {demoPath}");
                continue;
            }

            try
            {
                SceneExportSession.Validate(request.Core);
                Directory.CreateDirectory(Path.GetDirectoryName(job.GifPath)!);
                await runner.RunAsync(request, NoProgress.Instance, ct).ConfigureAwait(false);
                rendered.Add(job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke($"lineup clips: {job.GifPath}: {ex.Message}");
            }
        }

        return rendered;
    }

    /// <summary>
    ///     Whether the default parse maps the demo: never on the browser, and only for a settled file, the
    ///     processing queue's rule. A mapped file truncated under the parse is a fatal access violation.
    /// </summary>
    internal static bool MapsFile(string path, TimeProvider time, Func<string, FileStat> stat) =>
        !OperatingSystem.IsBrowser() && MappedParsePolicy.IsSettled(path, time, stat);

    private LoadedMapAsset? SafeLoad(string map)
    {
        try
        {
            return _loadMap(map);
        }
        catch (Exception)
        {
            return null; // the grid, as a strat export without a bundle
        }
    }

    // Nobody watches a background clip's progress; Progress<T> would post each report to the pool for nothing.
    private sealed class NoProgress : IProgress<ExportProgress>
    {
        public static readonly NoProgress Instance = new();

        public void Report(ExportProgress value)
        {
        }
    }
}
