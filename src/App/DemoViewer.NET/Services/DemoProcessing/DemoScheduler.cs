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
/// </summary>
public sealed class DemoScheduler : IDisposable
{
    private const string PlanTitle = "Checking demos for work";
    private const string PlanKey = "scheduler.plan";

    private readonly object _lock = new();
    private readonly Dictionary<string, PassLevel> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Pass, string Path)> _outstanding = [];
    private readonly HashSet<(string Pass, string Path)> _faulted = [];
    private readonly Func<IReadOnlyList<IDemoPass>> _passes;
    private readonly IDemoProcessingQueue _queue;
    private readonly Func<IEnumerable<string>> _allDemos;
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
    public DemoScheduler(Func<IReadOnlyList<IDemoPass>> passes, IDemoProcessingQueue queue,
        Func<IEnumerable<string>> allDemos, Action? validatePasses = null)
    {
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _allDemos = allDemos ?? throw new ArgumentNullException(nameof(allDemos));
        _validatePasses = validatePasses;
        _queue.CapacityAvailable += OnCapacityAvailable;
    }

    /// <param name="evaluators">A fixed set of evaluators, each wrapped as a pass in list order.</param>
    /// <param name="queue">The shared processing queue.</param>
    /// <param name="allDemos">Every demo the library knows.</param>
    public DemoScheduler(IReadOnlyList<IDemoEvaluator> evaluators, IDemoProcessingQueue queue, Func<IEnumerable<string>> allDemos)
        : this(Snapshot(evaluators), queue, allDemos)
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

    /// <summary>
    ///     The registered passes' ids in run order. The order is a contract other features build on (a pass
    ///     may read what the one before it wrote on the same visit), so the composition root's list is pinned
    ///     by a test through this.
    /// </summary>
    public IReadOnlyList<string> PassIds => [.. _passes().Select(p => p.Id)];

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
    }

    /// <summary>
    ///     Plans the open demo's visit on the calling thread. Called from the open's pass run, off the UI
    ///     thread, right before the queue takes the visits waiting on that open: what this submits runs on
    ///     the open's parse.
    /// </summary>
    public void PlanOpenDemo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Plan(path, PassLevel.OpenDemo, _passes());
    }

    /// <summary>True while the named pass has at least one demo in flight: backs a feature's "is scanning" indicator.</summary>
    public bool HasOutstanding(string passId)
    {
        lock (_lock)
        {
            foreach ((string Pass, string Path) key in _outstanding)
            {
                if (string.Equals(key.Pass, passId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>True when the pass threw on this demo earlier in the session; it is not offered the demo again until restart.</summary>
    public bool IsFaulted(string passId, string path)
    {
        lock (_lock)
        {
            return _faulted.Contains((passId, path));
        }
    }

    /// <summary>
    ///     Clears every pass's fault for <paramref name="path" />: the file changed, so the bytes a pass threw
    ///     on are gone and each one may be offered the demo again.
    /// </summary>
    public void ForgetFaults(string path)
    {
        lock (_lock)
        {
            _faulted.RemoveWhere(k => string.Equals(k.Path, path, StringComparison.Ordinal));
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
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (!_dirty.TryGetValue(path, out PassLevel current) || level > current)
            {
                _dirty[path] = level;
            }
        }

        Schedule(level);
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

        _ = QueueWork.Run(_queue, QueueJobKind.Scheduling, PlanTitle, "scheduler", Drain,
            userRequested ? DemoJobPriority.UserRequested : DemoJobPriority.Background, PlanKey);
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
                List<KeyValuePair<string, PassLevel>> batch;
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

                    batch = [.. _dirty];
                    _dirty.Clear();
                }

                if (recheck)
                {
                    HashSet<string> seen = new(batch.Select(b => b.Key), StringComparer.OrdinalIgnoreCase);
                    foreach (string path in SafeAllDemos())
                    {
                        if (seen.Add(path))
                        {
                            batch.Add(new KeyValuePair<string, PassLevel>(path, PassLevel.Background));
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
                            foreach ((string path, PassLevel level) in batch.Skip(i))
                            {
                                if (!_dirty.TryGetValue(path, out PassLevel current) || level > current)
                                {
                                    _dirty[path] = level;
                                }
                            }

                            _planQueued = false;
                        }

                        return;
                    }

                    Plan(batch[i].Key, batch[i].Value, passes);
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
    private void Plan(string path, PassLevel level, IReadOnlyList<IDemoPass> passes)
    {
        VisitedDemo demo = new(path);
        List<IDemoPass> candidates = passes.Where(p => !IsFaulted(p.Id, path)).ToList();
        List<IDemoPass> planned = VisitPlanner.Plan(candidates, demo, level, (pass, ex) => ReportFault(pass.Id, path, ex));

        long orderHint = 0;
        foreach (IDemoPass pass in planned)
        {
            if (pass is EvaluatorPassAdapter adapter)
            {
                if (SafeLevel(adapter, demo) > level)
                {
                    level = PassLevel.UserRequested;
                }

                orderHint = Math.Max(orderHint, SafeOrderHint(adapter, demo));
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
                (string Id, string Path) key = (pass.Id, path);
                if (_outstanding.Add(key))
                {
                    submitting.Add(pass);
                }
            }
        }

        if (submitting.Count == 0)
        {
            return;
        }

        IDemoQueueHandle handle = _queue.SubmitVisit(new DemoVisitRequest(path, level, submitting, orderHint,
            Path.GetFileName(path), (pass, outcome, error) => PassEnded(path, pass, outcome, error)));

        if (handle.State == DemoQueueItemState.Rejected)
        {
            lock (_lock)
            {
                foreach (IDemoPass pass in submitting)
                {
                    _outstanding.Remove((pass.Id, path));
                }

                if (!_dirty.TryGetValue(path, out PassLevel current) || level > current)
                {
                    _dirty[path] = level;
                }
            }
        }
    }

    // Runs in the queue's slot. Clears the outstanding entry either way so a demo that becomes interesting
    // again can be planned again; a throw puts the pass on the session's skip list for that demo.
    private void PassEnded(string path, IDemoPass pass, PassOutcome outcome, Exception? error)
    {
        (string Id, string Path) key = (pass.Id, path);
        lock (_lock)
        {
            _outstanding.Remove(key);
            if (outcome == PassOutcome.Failed)
            {
                _faulted.Add(key);
            }
        }

        if (outcome == PassOutcome.Failed && error is not null)
        {
            ReportFault(key.Id, key.Path, error);
        }
    }

    private PassLevel SafeLevel(EvaluatorPassAdapter adapter, VisitedDemo demo)
    {
        try
        {
            return adapter.LevelFor(demo);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault(adapter.Id, demo.Path, ex);
            return PassLevel.Background;
        }
    }

    private long SafeOrderHint(EvaluatorPassAdapter adapter, VisitedDemo demo)
    {
        try
        {
            return adapter.Evaluator.OrderHint(demo.Path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFault(adapter.Id, demo.Path, ex);
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
