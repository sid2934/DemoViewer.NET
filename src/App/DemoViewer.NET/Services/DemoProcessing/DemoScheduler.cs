#region

using System.Runtime.CompilerServices;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Turns "which passes want this demo?" into one visit of the demo on the queue, fed by events only: the
///     library adds or changes a demo, a user forces a pass, a pass's configuration changes, an extension
///     comes on. Each event marks a demo dirty; a light queue item then plans the dirty demos off the UI
///     thread by the closure rule (every pass that says yes, plus every pass waiting on one of those) and
///     submits them as one <see cref="DemoVisitRequest" /> each. The queue reads the demo once and runs the
///     passes on that read, in After order. Nothing polls: the only whole-library walk is
///     <see cref="RecheckAll" />, a user action that also runs once at startup.
///     <para>
///         A demo the queue refused (its tier was full) stays dirty and is planned again when the queue
///         reports room. A pass that threw on a demo is not offered that demo again this session.
///     </para>
///     <para>
///         Every set is keyed by the demo, not the path: two paths of one demo's bytes are one visit, one
///         outstanding pass and one fault. The path planned is the one marked last at the highest level.
///     </para>
/// </summary>
public sealed class DemoScheduler : IDisposable
{
    private const string PlanTitle = "Checking demos for work";
    private const string PlanKey = "scheduler.plan";

    private readonly object _lock = new();
    // Keyed by demo key, holding the path to plan and its level.
    private readonly Dictionary<string, (string Path, PassLevel Level)> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Pass, string Demo)> _outstanding = new(PassDemoKeyComparer.Instance);
    private readonly HashSet<(string Pass, string Demo)> _faulted = new(PassDemoKeyComparer.Instance);
    private readonly Func<IReadOnlyList<IDemoPass>> _passes;
    private readonly IDemoProcessingQueue _queue;
    private readonly Func<IEnumerable<string>> _allDemos;
    private readonly Func<string, string> _demoKey;
    private readonly Action? _validatePasses;
    private bool _recheckAll;
    private bool _planQueued;
    private bool _disposed;

    /// <param name="passes">
    ///     Read fresh on every plan (a <see cref="PassRegistry" />'s <c>Resolve</c>, typically): a pack pass is
    ///     never constructed until its pack is on and something plans, and an enable is seen on the next plan.
    /// </param>
    /// <param name="queue">The shared processing queue.</param>
    /// <param name="allDemos">Every demo the library knows, read off the UI thread by <see cref="RecheckAll" />.</param>
    /// <param name="validatePasses">
    ///     The registry's own <c>Validate</c>, so <see cref="ValidatePasses" /> fails fast on a cycle or an
    ///     unknown After id without constructing any pass.
    /// </param>
    /// <param name="demoKey">
    ///     What identifies the demo at a path: the cache's content id when it has one, so two paths of the same
    ///     bytes are one demo. Compared ignoring case. Null keys by path.
    /// </param>
    public DemoScheduler(Func<IReadOnlyList<IDemoPass>> passes, IDemoProcessingQueue queue,
        Func<IEnumerable<string>> allDemos, Action? validatePasses = null, Func<string, string>? demoKey = null)
    {
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _allDemos = allDemos ?? throw new ArgumentNullException(nameof(allDemos));
        _validatePasses = validatePasses;
        _demoKey = demoKey ?? (static path => path);
        _queue.CapacityAvailable += OnCapacityAvailable;
    }

    /// <param name="evaluators">A fixed set of evaluators, each wrapped as a pass in list order.</param>
    /// <param name="queue">The shared processing queue.</param>
    /// <param name="allDemos">Every demo the library knows.</param>
    /// <param name="demoKey">What identifies the demo at a path; null keys by path.</param>
    public DemoScheduler(IReadOnlyList<IDemoEvaluator> evaluators, IDemoProcessingQueue queue, Func<IEnumerable<string>> allDemos,
        Func<string, string>? demoKey = null)
        : this(Snapshot(evaluators), queue, allDemos, null, demoKey)
    {
    }

    private static Func<IReadOnlyList<IDemoPass>> Snapshot(IReadOnlyList<IDemoEvaluator> evaluators)
    {
        ArgumentNullException.ThrowIfNull(evaluators);
        IReadOnlyList<IDemoPass> passes = evaluators.Select(e => (IDemoPass)new EvaluatorPassAdapter(e, [])).ToList();
        return () => passes;
    }

    /// <summary>
    ///     Told when a pass throws from any call made into it, with the demo when there is one. The
    ///     composition root counts it against the extension that contributed the pass.
    /// </summary>
    public Action<string, string?, Exception>? Faulted { get; set; }

    /// <summary>The record passes, re-checked with every <see cref="RecheckAll" />. Null runs none.</summary>
    public RecordPassRunner? Records { get; set; }

    /// <summary>
    ///     Raised with a pass id when that pass takes a demo or finishes one, on whatever thread changed it. A
    ///     handler must not block: it runs in the planning item or in the queue's slot.
    /// </summary>
    public event Action<string>? OutstandingChanged;

    /// <summary>
    ///     The registered passes' ids in run order. The order is a contract other features build on (a pass
    ///     may read what the one before it wrote on the same visit), so the composition root's list is pinned
    ///     by a test through this.
    /// </summary>
    public IReadOnlyList<string> PassIds => [.. _passes().Select(p => p.Id)];

    /// <summary>The registered passes in order, for tests that check what each runs after.</summary>
    internal IReadOnlyList<IDemoPass> Passes => _passes();

    /// <summary>Every planned pass's outcome on its visit: demo path, pass id, outcome. Test seam.</summary>
    internal event Action<string, string, PassOutcome>? PassFinished;

    /// <summary>Validates the pass graph without constructing any pass: a cycle or an unknown After id throws here.</summary>
    public void ValidatePasses() => _validatePasses?.Invoke();

    /// <summary>The library added this demo or found it changed: plan its visit.</summary>
    public void DemoChanged(string path) => MarkDirty(path, PassLevel.Backlog);

    /// <summary>A user asked for work on this demo: plan its visit ahead of the backlog.</summary>
    public void Request(string path) => MarkDirty(path, PassLevel.UserRequested);

    /// <summary>
    ///     Plans every demo the library knows, off the UI thread. The manual re-check, run once at startup
    ///     and whenever a pass's configuration changes or an extension comes on.
    /// </summary>
    public void RecheckAll()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _recheckAll = true;
        }

        Schedule(PassLevel.Background);
        Records?.RecheckAll();
    }

    /// <summary>
    ///     Plans the open demo's visit on the calling thread. Called from the open's pass run, off the UI
    ///     thread, right before the queue takes the visits waiting on that open: what this submits runs on
    ///     the open's parse.
    /// </summary>
    public void PlanOpenDemo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Plan(DemoKey(path), path, PassLevel.OpenDemo, _passes());
    }

    /// <summary>True while the named pass has at least one demo in flight: backs a feature's "is scanning" indicator.</summary>
    public bool HasOutstanding(string passId)
    {
        lock (_lock)
        {
            foreach ((string Pass, string Demo) key in _outstanding)
            {
                if (string.Equals(key.Pass, passId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///     <see cref="HasOutstanding(string)" /> for a pass that belongs to <paramref name="owner" />; false for a
    ///     pass id another owner registered.
    /// </summary>
    public bool HasOutstanding(string passId, string owner) =>
        string.Equals(OwnerOf(passId), owner, StringComparison.Ordinal) && HasOutstanding(passId);

    /// <summary>The owner of the registered pass <paramref name="passId" />, or null when no enabled pass has that id.</summary>
    public string? OwnerOf(string passId)
    {
        try
        {
            return _passes().FirstOrDefault(p => string.Equals(p.Id, passId, StringComparison.Ordinal))?.Owner;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault("scheduler", null, ex);
            return null;
        }
    }

    /// <summary>
    ///     True when the pass threw on this demo, through any of its paths, earlier in the session; it is not
    ///     offered the demo again until restart.
    /// </summary>
    public bool IsFaulted(string passId, string path)
    {
        string[] keys = KeysOf(path);
        lock (_lock)
        {
            return keys.Any(k => _faulted.Contains((passId, k)));
        }
    }

    /// <summary>
    ///     Clears every pass's fault for <paramref name="path" />: the file changed, so the bytes a pass threw
    ///     on are gone and each one may be offered the demo again. Another path still holding those bytes keeps
    ///     its own fault.
    /// </summary>
    public void ForgetFaults(string path)
    {
        string demo = DemoKey(path);
        lock (_lock)
        {
            _faulted.RemoveWhere(k => string.Equals(k.Demo, demo, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(k.Demo, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Detaches from the queue; nothing is planned after this.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dirty.Clear();
        }

        _queue.CapacityAvailable -= OnCapacityAvailable;
    }

    private void MarkDirty(string path, PassLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string demo = DemoKey(path);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            AddDirty(demo, path, level);
        }

        Schedule(level);
    }

    // Under _lock. A later mark of the demo moves it to its path, and never lowers its level.
    private void AddDirty(string demo, string path, PassLevel level)
    {
        if (!_dirty.TryGetValue(demo, out (string Path, PassLevel Level) current) || level >= current.Level)
        {
            _dirty[demo] = (path, level);
        }
    }

    // A path's demo key changes when its file is first hashed, so the outstanding and fault sets hold a pass
    // under the path and the key both, and a lookup by either finds it.
    private string[] KeysOf(string path) =>
        DemoKey(path) is var key && !string.Equals(key, path, StringComparison.OrdinalIgnoreCase) ? [key, path] : [path];

    // A key resolver that throws must not stop a mark or a plan; the path stands in.
    private string DemoKey(string path)
    {
        try
        {
            return _demoKey(path) is { Length: > 0 } key ? key : path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault("scheduler", path, ex);
            return path;
        }
    }

    // One planning item at a time. A dirty mark while one is queued raises its priority when a user asked;
    // a mark while one is running is drained by that run, or by the next item its drain submits.
    private void Schedule(PassLevel level)
    {
        bool userRequested = level >= PassLevel.UserRequested;
        lock (_lock)
        {
            if (_planQueued && !userRequested)
            {
                return;
            }

            _planQueued = true;
        }

        // A planning item the user removes before it starts never drains; the next mark must still queue one.
        StrongBox<bool> started = new(false);
        _ = QueueWork.Run(_queue, QueueJobKind.Scheduling, PlanTitle, "scheduler", token =>
                {
                    started.Value = true;
                    Drain(token);
                },
                userRequested ? DemoJobPriority.UserRequested : DemoJobPriority.Background, PlanKey)
            .ContinueWith(_ =>
            {
                if (!started.Value)
                {
                    lock (_lock)
                    {
                        _planQueued = false;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnCapacityAvailable()
    {
        bool pending;
        lock (_lock)
        {
            pending = !_disposed && (_dirty.Count > 0 || _recheckAll);
        }

        if (pending)
        {
            Schedule(PassLevel.Background);
        }
    }

    // Runs as the planning item, off the UI thread. Loops until nothing is dirty, so a mark made while a
    // batch was planning is not left for the next event.
    private void Drain(CancellationToken token)
    {
        try
        {
            while (true)
            {
                List<(string Demo, string Path, PassLevel Level)> batch;
                bool recheck;
                lock (_lock)
                {
                    recheck = _recheckAll;
                    _recheckAll = false;
                    if (!recheck && (_dirty.Count == 0 || _disposed))
                    {
                        _planQueued = false;
                        return;
                    }

                    batch = [.. _dirty.Select(d => (d.Key, d.Value.Path, d.Value.Level))];
                    _dirty.Clear();
                }

                if (recheck)
                {
                    HashSet<string> seen = new(batch.Select(b => b.Demo), StringComparer.OrdinalIgnoreCase);
                    foreach (string path in SafeAllDemos())
                    {
                        string demo = DemoKey(path);
                        if (seen.Add(demo))
                        {
                            batch.Add((demo, path, PassLevel.Background));
                        }
                    }
                }

                IReadOnlyList<IDemoPass> passes = _passes();
                for (int i = 0; i < batch.Count; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        // Stopped by the user: what is left stays dirty for the next event.
                        lock (_lock)
                        {
                            foreach ((string demo, string path, PassLevel level) in batch.Skip(i))
                            {
                                if (!_dirty.TryGetValue(demo, out (string Path, PassLevel Level) current) || level > current.Level)
                                {
                                    _dirty[demo] = (path, level);
                                }
                            }

                            _planQueued = false;
                        }

                        return;
                    }

                    Plan(batch[i].Demo, batch[i].Path, batch[i].Level, passes);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_lock)
            {
                _planQueued = false;
            }

            ReportFault("scheduler", null, ex);
        }
    }

    private List<string> SafeAllDemos()
    {
        try
        {
            return [.. _allDemos()];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault("scheduler", null, ex);
            return [];
        }
    }

    // Plans one demo: the closure of interested passes, the level the highest of them or the caller asks for,
    // one visit. A demo the queue refuses goes back on the dirty set for the next capacity event.
    // The keys are taken once, here: a visit that hashes the file changes the path's key before it ends.
    private void Plan(string demoKey, string path, PassLevel level, IReadOnlyList<IDemoPass> passes)
    {
        VisitedDemo demo = new(path);
        string[] keys = [.. KeysOf(path).Append(demoKey).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<IDemoPass> candidates;
        lock (_lock)
        {
            candidates = [.. passes.Where(p => !keys.Any(k => _faulted.Contains((p.Id, k))))];
        }

        List<IDemoPass> planned = VisitPlanner.Plan(candidates, demo, level, (pass, ex) => ReportFault(pass.Id, path, ex));

        long orderHint = 0;
        foreach (IDemoPass pass in planned)
        {
            if (pass is IPassScheduling scheduling)
            {
                PassLevel asked = SafeLevel(pass, scheduling, demo);
                if (asked > level)
                {
                    level = asked;
                }

                orderHint = Math.Max(orderHint, SafeOrderHint(pass, scheduling, demo));
            }
        }

        List<IDemoPass> submitting = [];
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (IDemoPass pass in planned)
            {
                if (!keys.Any(k => _outstanding.Contains((pass.Id, k))))
                {
                    foreach (string key in keys)
                    {
                        _outstanding.Add((pass.Id, key));
                    }

                    submitting.Add(pass);
                }
            }
        }

        if (submitting.Count == 0)
        {
            return;
        }

        foreach (IDemoPass pass in submitting)
        {
            RaiseOutstanding(pass.Id);
        }

        IDemoQueueHandle handle = _queue.SubmitVisit(new DemoVisitRequest(path, level, submitting, orderHint,
            Path.GetFileName(path), (pass, outcome, error) => PassEnded(keys, path, pass, outcome, error)));

        if (handle.State == DemoQueueItemState.Rejected)
        {
            lock (_lock)
            {
                foreach (IDemoPass pass in submitting)
                {
                    foreach (string key in keys)
                    {
                        _outstanding.Remove((pass.Id, key));
                    }
                }

                if (!_dirty.TryGetValue(demoKey, out (string Path, PassLevel Level) current) || level > current.Level)
                {
                    _dirty[demoKey] = (path, level);
                }
            }

            foreach (IDemoPass pass in submitting)
            {
                RaiseOutstanding(pass.Id);
            }
        }
    }

    // Runs in the queue's slot. Clears the outstanding entry either way so a demo that becomes interesting
    // again can be planned again; a throw puts the pass on the session's skip list for that demo.
    private void PassEnded(string[] keys, string path, IDemoPass pass, PassOutcome outcome, Exception? error)
    {
        PassFinished?.Invoke(path, pass.Id, outcome);
        string[] faultKeys = outcome == PassOutcome.Failed ? [.. keys.Concat(KeysOf(path)).Distinct(StringComparer.OrdinalIgnoreCase)] : [];
        lock (_lock)
        {
            foreach (string key in keys)
            {
                _outstanding.Remove((pass.Id, key));
            }

            foreach (string key in faultKeys)
            {
                _faulted.Add((pass.Id, key));
            }
        }

        if (outcome == PassOutcome.Failed && error is not null)
        {
            ReportFault(pass.Id, path, error);
        }

        RaiseOutstanding(pass.Id);
    }

    private void RaiseOutstanding(string passId)
    {
        try
        {
            OutstandingChanged?.Invoke(passId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault("scheduler", null, ex);
        }
    }

    private PassLevel SafeLevel(IDemoPass pass, IPassScheduling scheduling, VisitedDemo demo)
    {
        try
        {
            return scheduling.LevelFor(demo);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault(pass.Id, demo.Path, ex);
            return PassLevel.Background;
        }
    }

    private long SafeOrderHint(IDemoPass pass, IPassScheduling scheduling, VisitedDemo demo)
    {
        try
        {
            return scheduling.OrderHint(demo);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault(pass.Id, demo.Path, ex);
            return 0;
        }
    }

    // The callback is the composition root's; it must never break the planning item either.
    private void ReportFault(string passId, string? path, Exception exception)
    {
        try
        {
            Faulted?.Invoke(passId, path, exception);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing to fall back to.
        }
    }

}
