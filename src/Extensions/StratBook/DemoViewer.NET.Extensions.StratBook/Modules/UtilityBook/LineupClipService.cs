#region

using System.Globalization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
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
using DemoViewer.NET.Services.Export;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>Renders the GIFs of one demo's planned clips. The seam every Lineup Clip test replaces.</summary>
public interface ILineupClipRenderer
{
    /// <summary>
    ///     Renders each job's GIF to its <see cref="LineupClipJob.GifPath" />. Runs on a worker; one demo
    ///     per call, on the parse the demo's visit read, so the demo is parsed once for all of its clips.
    /// </summary>
    /// <param name="demoPath">The demo every job is in.</param>
    /// <param name="demo">The demo's parse; null only where no queue reads demos (tests).</param>
    /// <param name="jobs">That demo's clips.</param>
    /// <param name="ct">Stops the batch; a GIF cut short does not survive.</param>
    /// <returns>The jobs whose GIF was written.</returns>
    Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, ParsedDemo? demo, IReadOnlyList<LineupClipJob> jobs,
        CancellationToken ct);
}

/// <summary>
///     Lineup Clip Render: every Lineup Card with a repeated throw position gets a
///     short GIF of the throw, the camera on the thrower, written beside the <c>setpos</c>/<c>setang</c>
///     line it was thrown from, with no one pressing anything.
///     <para>
///         <b>One clip per lineup and technique, automatically.</b> The clips are not Review Queue items: the
///         Utility Book's position card shows a position's clip. Planning runs when the Grenade Index
///         changes, so a demo indexed in the background has its clips rendered after it.
///     </para>
///     <para>
///         <b>The pair.</b> The renderer writes the GIF through the existing export session; this service
///         writes the sidecar only once the GIF exists, so a pair on disk is always a finished one, and a
///         lineup is planned again while either half is missing. Pairs are named by
///         <see cref="GrenadeLineup.Id" />, so a new representative throw keeps the pair it has.
///     </para>
///     <para>
///         <b>Bounded.</b> Once the index is complete, a pair no current lineup names is deleted after
///         <see cref="DefaultOrphanGrace" />. Clips are
///         planned most-thrown first; once the byte cap is full a lineup is planned only if it outranks the
///         lowest-ranked pair on disk, so a full directory stops rendering instead of trading one kept clip for
///         another. After each render the directory is held under the cap by deleting the lowest-ranked pairs;
///         an evicted pair is listed in <see cref="EvictedFileName" /> and rendered again only through
///         <see cref="Request" />.
///     </para>
///     <para>
///         <b>Threading.</b> <see cref="PlanSoon" /> is what an index change calls: it coalesces a burst of
///         changes and runs <see cref="Plan" /> on the pool, one plan at a time, so planning every lineup never
///         blocks the UI thread. Renders run one demo at a time as jobs on the demo's visit, so the clips share
///         the read the demo's passes get, or the parse the shell holds when the demo is open.
///     </para>
/// </summary>
public sealed class LineupClipService : IExtensionResident, IDisposable
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

    // Read once: a batch's continuation may run after Dispose has disposed the source.
    private readonly CancellationToken _ct;
    private readonly string? _directory;
    private readonly Func<bool> _enabled;
    private readonly Func<string, bool>? _fileExists;
    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private readonly Func<long> _maxBytes;
    private readonly TimeSpan _orphanGrace;
    private readonly Dictionary<string, DateTime> _orphanSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _evictedAbsentSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LineupClipJob> _pending = [];

    // Stem to the representative's key it was planned with this session.
    private readonly Dictionary<string, string> _planned = new(StringComparer.OrdinalIgnoreCase);
    private readonly IExtensionJobs? _jobs;
    private readonly Dictionary<string, int> _ranks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILineupClipRenderer _renderer;
    private readonly object _sweepGate = new();

    // One plan at a time: Plan and Request share _planned, _ranks and the directory listing.
    private readonly object _planLock = new();
    private readonly object _soonGate = new();
    private readonly TimeSpan _planDebounce;
    private bool _soonScheduled;
    private Task _soon = Task.CompletedTask;

    // Held by EnforceCap from its listing to its last delete, and by Request while it saves a pair, so a
    // pair asked for mid-pass is either seen as just used or evicted and then un-evicted.
    private readonly object _capGate = new();
    private readonly Action<string, string> _writeText;
    private bool _disposed;
    private bool _released;
    private HashSet<string>? _evicted;
    private TaskCompletionSource? _idle;
    private bool _running;

    /// <param name="clusters">Every landing cluster in the Grenade Index, all maps.</param>
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
    /// <param name="jobs">
    ///     The processing queue each demo's batch runs in, on the demo's parse; null renders on a worker of this
    ///     service with no parse (tests).
    /// </param>
    /// <param name="planDebounce">How long <see cref="PlanSoon" /> waits for more index changes; 500 ms when null.</param>
    public LineupClipService(Func<IReadOnlyList<GrenadeCluster>> clusters, string? directory,
        Func<bool> enabled, ILineupClipRenderer renderer, Func<string, bool>? fileExists = null,
        Action<string, string>? writeText = null, Action<string>? log = null, Func<bool>? complete = null,
        Func<long>? maxBytes = null, TimeSpan? orphanGrace = null, IExtensionJobs? jobs = null,
        TimeSpan? planDebounce = null)
    {
        _planDebounce = planDebounce ?? TimeSpan.FromMilliseconds(500);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(renderer);
        _clusters = clusters;
        _directory = directory;
        _enabled = enabled;
        _renderer = renderer;
        _fileExists = fileExists;
        _writeText = writeText ?? AtomicFile.WriteAllText;
        _log = log;
        _complete = complete ?? (static () => true);
        _maxBytes = maxBytes ?? (static () => 0);
        _orphanGrace = orphanGrace ?? DefaultOrphanGrace;
        _jobs = jobs;
        _ct = _cts.Token;
    }

    /// <summary>The clips queued and not yet rendered, oldest first.</summary>
    public IReadOnlyList<LineupClipJob> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending];
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
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc />
    /// <remarks>Plans again: the index it reads has reloaded by the time the pack calls this.</remarks>
    public void Attach()
    {
        lock (_gate)
        {
            if (!_released || _disposed)
            {
                return;
            }

            _released = false;
        }

        _ = PlanSoon();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The plan, the ranks, the session's requests and the eviction cache go; the queue items the
    ///     worker submitted are the pack's to cancel by owner. Until <see cref="Attach" />, a plan is a no-op.
    /// </remarks>
    public void Release()
    {
        Task worker;
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            _pending.Clear();
            _planned.Clear();
            _ranks.Clear();
            _requested.Clear();
            _orphanSince.Clear();
            _evictedAbsentSince.Clear();
            _evicted = null;
            worker = WorkerTask;
        }

        // A render still running writes nothing back once released (the pack cancelled its queue item too);
        // the worker ends after it. Bounded: one demo's batch.
        try
        {
            worker.Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException)
        {
            // Its failure is its own; the release goes on.
        }
    }

    /// <summary>
    ///     Plans every clip the index calls for that has no pair on disk, was not evicted, and was not
    ///     planned earlier this session with the same representative, and starts the worker. A pair of the
    ///     same throw left under an older name is renamed rather than rendered again. Any thread; one plan
    ///     runs at a time.
    /// </summary>
    /// <returns>How many clips were planned.</returns>
    public int Plan()
    {
        lock (_planLock)
        {
            return PlanLocked();
        }
    }

    /// <summary>
    ///     Plans on the pool once index changes stop arriving for a moment. Returns the pending run, so a
    ///     test can await it; a call while one is scheduled joins it.
    /// </summary>
    public Task PlanSoon()
    {
        lock (_gate)
        {
            if (_released || _disposed)
            {
                return Task.CompletedTask;
            }
        }

        lock (_soonGate)
        {
            if (_soonScheduled)
            {
                return _soon;
            }

            _soonScheduled = true;
            // The debounce is only a timer; the planning itself is a queue item.
            _soon = Task.Delay(_planDebounce).ContinueWith(_ =>
            {
                lock (_soonGate)
                {
                    _soonScheduled = false;
                }

                return RunJob("Lineup clips: plan", () =>
                {
                    try
                    {
                        Plan();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        _log?.Invoke($"lineup clips: plan: {ex.Message}");
                    }
                }, new JobOptions(Key: "lineup-clips:plan"));
            }, TaskScheduler.Default).Unwrap();
            return _soon;
        }
    }

    private int PlanLocked()
    {
        bool released;
        lock (_gate)
        {
            released = _released;
        }

        if (released || _disposed || _directory is null || !_enabled())
        {
            return 0;
        }

        IReadOnlyList<LineupClipJob> every = LineupClipPlanner.PlanEvery(_clusters(), _directory);
        List<LineupClipJob> jobs = ChooseLocked(every, _directory);
        if (_complete())
        {
            Prune(every);
        }

        if (jobs.Count == 0)
        {
            return 0;
        }

        HashSet<string> replanned = new(jobs.Select(j => j.Stem), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _pending.RemoveAll(p => replanned.Contains(p.Stem));
            _pending.AddRange(jobs);
        }

        StartWorker();
        return jobs.Count;
    }

    // The jobs to render out of every planned one: no finished pair, not evicted or adopted, not planned already
    // with the same throw, and within the cap. Marks them planned. With `only`, a job still waiting for its own
    // read counts as unplanned, so a visit that can show it takes it over. Under _planLock.
    private List<LineupClipJob> ChooseLocked(IReadOnlyList<LineupClipJob> every, string directory,
        Func<LineupClipJob, bool>? only = null)
    {
        HashSet<string> waiting = new(StringComparer.OrdinalIgnoreCase);
        if (only is not null)
        {
            lock (_gate)
            {
                waiting.UnionWith(_pending.Select(p => p.Stem));
            }
        }

        HashSet<string> listing = Listing(directory);
        Func<string, bool> exists = _fileExists ?? listing.Contains;
        HashSet<string> evicted;
        lock (_gate)
        {
            evicted = new HashSet<string>(LoadEvicted(), StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> requested;
        lock (_gate)
        {
            _ranks.Clear();
            foreach (LineupClipJob job in every)
            {
                _ranks[job.Stem] = _requested.Contains(job.Stem) ? int.MaxValue : job.Throws;
            }

            requested = new HashSet<string>(_requested, StringComparer.OrdinalIgnoreCase);
        }

        List<LineupClipJob> candidates = [];
        // Stable: equal ranks keep the index's cluster order.
        foreach (LineupClipJob job in every.OrderByDescending(j => requested.Contains(j.Stem))
                     .ThenByDescending(j => j.Throws))
        {
            bool gif = exists(job.GifPath);
            bool setpos = exists(job.SetposPath);
            if ((gif && setpos) || IsEvicted(job, evicted) || Adopt(job)
                || (_planned.TryGetValue(job.Stem, out string? key) && string.Equals(key, job.Key, StringComparison.Ordinal)
                    && !waiting.Contains(job.Stem)))
            {
                continue;
            }

            candidates.Add(job);
        }

        List<LineupClipJob> jobs = [.. WithinCap(candidates, requested).Where(j => only?.Invoke(j) ?? true)];
        foreach (LineupClipJob job in jobs)
        {
            if (exists(job.GifPath) || exists(job.SetposPath))
            {
                DeletePair(job.GifPath);
            }

            _planned[job.Stem] = job.Key;
        }

        return jobs;
    }

    /// <summary>True while clips are wanted: the setting is on, there is a directory and the pack is attached.</summary>
    public bool Renders
    {
        get
        {
            lock (_gate)
            {
                if (_released || _disposed)
                {
                    return false;
                }
            }

            return _directory is not null && _enabled();
        }
    }

    /// <summary>
    ///     Renders, on the parse of a demo's visit, every missing clip a throw in that demo can show, so the read
    ///     the visit already paid for serves them. A lineup the demo just made repeated always has a throw in it.
    ///     Runs on the visit's queue thread, after the demo's grenades are in the index.
    /// </summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="parsed">Its parse, held by the visit.</param>
    /// <param name="ct">Stops the renders; a GIF cut short does not survive.</param>
    /// <returns>How many pairs were written.</returns>
    public int RenderOn(string demoPath, ParsedDemo parsed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        ArgumentNullException.ThrowIfNull(parsed);
        List<LineupClipJob> jobs;
        lock (_planLock)
        {
            lock (_gate)
            {
                if (_released || _disposed)
                {
                    return 0;
                }
            }

            if (_directory is null || !_enabled())
            {
                return 0;
            }

            jobs = ChooseLocked(LineupClipPlanner.PlanEvery(_clusters(), _directory, demoPath), _directory,
                j => string.Equals(j.DemoPath, demoPath, StringComparison.OrdinalIgnoreCase));
        }

        if (jobs.Count == 0)
        {
            return 0;
        }

        HashSet<string> taken = new(jobs.Select(j => j.Stem), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _pending.RemoveAll(p => taken.Contains(p.Stem));
        }

        IReadOnlyList<LineupClipJob> rendered;
        try
        {
            // The renderer never touches the UI thread, so blocking this queue thread on it cannot deadlock.
            rendered = _renderer.RenderAsync(demoPath, parsed, jobs, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"lineup clips: {demoPath}: {ex.Message}");
            return 0;
        }

        return Finish(rendered);
    }

    // Writes the sidecars of the rendered GIFs, drops pairs left under older names, and holds the cap.
    private int Finish(IReadOnlyList<LineupClipJob> rendered)
    {
        lock (_gate)
        {
            // Released meanwhile: the GIFs on disk are adopted by the next plan; nothing else is touched.
            if (_released || _disposed)
            {
                return 0;
            }
        }

        foreach (LineupClipJob done in rendered)
        {
            WriteSidecar(done);
            if (File.Exists(done.SetposPath))
            {
                DeleteFormerPairs(done);
            }
        }

        if (rendered.Count > 0)
        {
            EnforceCap(_directory!);
        }

        return rendered.Count;
    }

    /// <summary>
    ///     A lineup's clip is wanted now: an evicted pair is un-evicted and planned again, and a pair on disk
    ///     counts as just used, so the cap evicts it last. Any thread.
    /// </summary>
    /// <param name="lineupId"><see cref="GrenadeLineup.Id" /> or one of its aliases.</param>
    /// <param name="techniqueKey">The technique; the lineup's first when null.</param>
    /// <returns>How many clips were planned.</returns>
    public int Request(Guid lineupId, string? techniqueKey = null)
    {
        lock (_planLock)
        {
            return RequestLocked(lineupId, techniqueKey);
        }
    }

    private int RequestLocked(Guid lineupId, string? techniqueKey)
    {
        if (_disposed || _directory is null)
        {
            return 0;
        }

        if (LineupClipPlanner.PlanEvery(_clusters(), _directory)
                .FirstOrDefault(j => (j.LineupId == lineupId || j.AliasIds.Contains(lineupId))
                                     && (techniqueKey is null || string.Equals(j.TechniqueKey, techniqueKey, StringComparison.Ordinal)))
            is not { } job)
        {
            return 0;
        }

        lock (_capGate)
        {
            lock (_gate)
            {
                _requested.Add(job.Stem);
                HashSet<string> evicted = LoadEvicted();
                bool removed = evicted.Remove(job.Stem);
                if (evicted.Count > 0)
                {
                    // An older name for the same clip would evict it again on the next plan.
                    foreach (string former in job.FormerGifPaths)
                    {
                        removed |= evicted.Remove(Path.GetFileNameWithoutExtension(former));
                    }
                }

                if (removed)
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

        _planned.Remove(job.Stem);
        return Plan();
    }

    /// <summary>What a pair is guessed to take before any is on disk.</summary>
    internal const long DefaultPairBytes = 2L * 1024 * 1024;

    // The candidates, best first, that the cap has room for. Room is guessed at the mean pair size; past it, a
    // lineup goes in only by displacing a lower-ranked pair, never an equal one, so a full cap stops the renders.
    // A requested lineup always goes in.
    private IEnumerable<LineupClipJob> WithinCap(List<LineupClipJob> candidates, HashSet<string> requested)
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
            HashSet<string> stems = new(candidates.Select(c => c.Stem), StringComparer.OrdinalIgnoreCase);
            alreadyPending = _pending.Count(p => !stems.Contains(p.Stem));
        }

        long room = cap - total - (alreadyPending * mean);
        List<(string Stem, int Rank, DateTime Used)> kept =
        [
            .. pairs.Select(p => (p.Key, _ranks.GetValueOrDefault(p.Key), p.Value.Used))
                .OrderBy(k => k.Item2).ThenBy(k => k.Used).ThenBy(k => k.Key, StringComparer.Ordinal)
        ];

        foreach (LineupClipJob job in candidates)
        {
            if (requested.Contains(job.Stem) || room >= mean)
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

    // An evicted pair stays evicted under its new name: a stem an earlier build evicted is carried over.
    private bool IsEvicted(LineupClipJob job, HashSet<string> evicted)
    {
        if (evicted.Contains(job.Stem))
        {
            return true;
        }

        if (evicted.Count == 0 || !job.FormerGifPaths.Any(f => evicted.Contains(Path.GetFileNameWithoutExtension(f))))
        {
            return false;
        }

        lock (_gate)
        {
            if (LoadEvicted().Add(job.Stem))
            {
                SaveEvicted();
            }
        }

        evicted.Add(job.Stem);
        return true;
    }

    // A finished pair under an alias's or an older build's name becomes this lineup's pair.
    // Only a pair of the same throw: its setpos line must be this job's. An old per-lineup pair shows the
    // lineup's representative, which need not be this technique's; that one renders again instead.
    private bool Adopt(LineupClipJob job)
    {
        foreach (string former in job.FormerGifPaths)
        {
            string formerSetpos = LineupClipPlanner.SetposPathFor(former);
            if (!File.Exists(former) || !File.Exists(formerSetpos) || !SameThrow(formerSetpos, job))
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
        HashSet<string> current = new(every.Select(j => j.Stem), StringComparer.OrdinalIgnoreCase);
        foreach (string gone in _planned.Keys.Where(stem => !current.Contains(stem)).ToList())
        {
            _planned.Remove(gone);
        }

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
            SweepTask = RunJob("Lineup clips: remove old clips", () => Sweep(directory, keep, now),
                new JobOptions(BuiltInJobKinds.Save));
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
            AtomicFile.WriteAllText(Path.Combine(_directory!, EvictedFileName),
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
            if (_running || _released || _pending.Count == 0)
            {
                return;
            }

            _running = true;
            _idle = idle;
            WorkerTask = idle.Task;
        }
        if (_jobs is null)
        {
            _ = Task.Run(() => DrainAsync(_ct), CancellationToken.None);
        }
        else
        {
            SubmitNext(_jobs);
        }
    }

    // A queue job, or the pool where there is no queue.
    private Task RunJob(string title, Action work, JobOptions options) =>
        _jobs is null
            ? Task.Run(work)
            : _jobs.RunAsync(title, _ =>
            {
                work();
                return Task.CompletedTask;
            }, options);

    // Runs every pending batch here, best demo first. Only without a processing queue.
    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && NextDemo() is { } demo)
        {
            await RenderBatchAsync(demo.Path, null, null, ct).ConfigureAwait(false);
        }

        Stop(true);
    }

    // One queue item at a time: the next demo is chosen when the last one ends, so a replan re-ranks it.
    private void SubmitNext(IExtensionJobs jobs)
    {
        if (_disposed || NextDemo() is not { } demo)
        {
            Stop(!_disposed);
            return;
        }

        string title = string.Create(CultureInfo.InvariantCulture,
            $"Lineup clips: {demo.Map}, {demo.Count} {(demo.Count == 1 ? "clip" : "clips")} from {Path.GetFileName(demo.Path)}");
        CancellationToken ct = _ct;
        IJobHandle handle = jobs.Enqueue(JobRequest.OnDemo(title, demo.Path,
            job => RenderBatchAsync(demo.Path, job.Parsed, job, ct),
            new JobOptions(StratBookJobKinds.LineupClips, demo.Requested ? JobPriority.UserRequested : JobPriority.Background)));
        if (handle.Status == JobStatus.Rejected)
        {
            Stop(false);
            return;
        }

        _ = handle.Completion.ContinueWith(_ =>
        {
            // Cancelled from the queue list: this session does not render that demo's clips.
            if (handle.Status == JobStatus.Cancelled)
            {
                lock (_gate)
                {
                    _pending.RemoveAll(p => string.Equals(p.DemoPath, demo.Path, StringComparison.OrdinalIgnoreCase));
                }
            }

            SubmitNext(jobs);
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

            LineupClipJob best = _pending
                .OrderByDescending(j => _requested.Contains(j.Stem)).ThenByDescending(j => j.Throws).First();
            List<LineupClipJob> batch = [.. _pending
                .Where(j => string.Equals(j.DemoPath, best.DemoPath, StringComparison.OrdinalIgnoreCase))];
            return new NextBatch(best.DemoPath, best.Map, batch.Count, batch.Any(j => _requested.Contains(j.Stem)));
        }
    }

    // Takes the demo's clips still pending, so the demo is parsed once for all of them, and renders them.
    private async Task RenderBatchAsync(string demoPath, ParsedDemo? parsed, IJobContext? job, CancellationToken ct)
    {
        List<LineupClipJob> batch;
        lock (_gate)
        {
            batch = [.. _pending.Where(p => string.Equals(p.DemoPath, demoPath, StringComparison.OrdinalIgnoreCase))];
            _pending.RemoveAll(p => string.Equals(p.DemoPath, demoPath, StringComparison.OrdinalIgnoreCase));
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
            rendered = await _renderer.RenderAsync(demoPath, parsed, batch, linked.Token).ConfigureAwait(false);
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

        Finish(rendered);
        job?.Report(batch.Count, batch.Count, string.Create(CultureInfo.InvariantCulture, $"{rendered.Count} written"));
    }

    private bool SameThrow(string setposPath, LineupClipJob job)
    {
        try
        {
            return string.Equals(File.ReadAllText(setposPath).Trim(), job.ConsoleText.Trim(), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"lineup clips: {setposPath}: {ex.Message}");
            return false;
        }
    }

    // A pair left under an older name that was not adopted goes once this job's own pair exists.
    private void DeleteFormerPairs(LineupClipJob job)
    {
        foreach (string former in job.FormerGifPaths)
        {
            if (File.Exists(former) || File.Exists(LineupClipPlanner.SetposPathFor(former)))
            {
                DeletePair(former);
            }
        }
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
///     The production <see cref="ILineupClipRenderer" />: renders each clip of one demo, on the parse the demo's
///     visit holds, through <see cref="SceneExportRunner" />, the 2D export's own runner and
///     <c>SceneExportSession</c>, as a GIF (the managed encoder when no ffmpeg is installed). Its own map bundle,
///     compositor and surface, as every export.
/// </summary>
public sealed class LineupClipRenderer : ILineupClipRenderer
{
    private readonly Func<string, LoadedMapAsset?> _loadMap;
    private readonly Action<string>? _log;
    private readonly Func<Scene2DExportRequest, ExportSceneSetup, CancellationToken, Task> _render;

    /// <summary>Where a GIF is written until it is finished; the directory listing never sees it.</summary>
    public const string PartialDirectoryName = ".rendering";

    /// <param name="loadMap">Finds a map's baked bundle; the pipeline's loader when null.</param>
    /// <param name="log">Line sink for the encoder choice, ffmpeg's stderr and a failed clip.</param>
    /// <param name="render">Renders one clip request; the 2D export's runner when null.</param>
    public LineupClipRenderer(Func<string, LoadedMapAsset?>? loadMap = null, Action<string>? log = null,
        Func<Scene2DExportRequest, ExportSceneSetup, CancellationToken, Task>? render = null)
    {
        _loadMap = loadMap ?? (map => MapAssetPipeline.TryLoad(map));
        _log = log;
        _render = render ?? ((request, setup, ct) => new SceneExportRunner(_ => setup, RenderSurfaceProviderFactory.CreateCpu,
            static () => FfmpegDependency.ManagedDirectory, _log, EncoderProbeCache.Shared).RunAsync(request, NoProgress.Instance, ct));
    }

    /// <summary>The unfinished GIF's path for <paramref name="gifPath" />.</summary>
    public static string PartialPathFor(string gifPath) =>
        Path.Combine(Path.GetDirectoryName(gifPath)!, PartialDirectoryName, Path.GetFileName(gifPath));

    /// <inheritdoc />
    public async Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, ParsedDemo? demo,
        IReadOnlyList<LineupClipJob> jobs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
        {
            return [];
        }

        if (demo is null)
        {
            throw new InvalidOperationException("Lineup clips render on the parse of the demo's visit.");
        }

        using LoadedMapAsset? asset = SafeLoad(jobs[0].Map);

        ExportSceneSetup setup = new(demo.Frames, demo.TickRate, jobs[0].Map, ScenePalette.Dark,
            LevelDisplayMode.Stacked, null, null, asset);
        List<LineupClipJob> rendered = [];
        foreach (LineupClipJob job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (LineupClipPlanner.BuildRequest(job, demo.Frames, demo.TickRate) is not { } request)
            {
                _log?.Invoke($"lineup clips: {job.Key} lies outside {demoPath}");
                continue;
            }

            // Rendered aside and renamed on success: a GIF at GifPath is a finished one, which Plan relies on.
            string partial = PartialPathFor(job.GifPath);
            try
            {
                SceneExportSession.Validate(request.Core);
                Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
                await _render(request with { OutputPath = partial }, setup, ct).ConfigureAwait(false);
                File.Move(partial, job.GifPath, true);
                rendered.Add(job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke($"lineup clips: {job.GifPath}: {ex.Message}");
            }
            finally
            {
                DeletePartial(partial);
            }
        }

        return rendered;
    }

    private void DeletePartial(string partial)
    {
        try
        {
            File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"lineup clips: {partial}: {ex.Message}");
        }
    }

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
