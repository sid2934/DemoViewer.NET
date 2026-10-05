#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     The single submitter that turns "which passes want this demo?" into one visit of the demo on the
///     queue (demo-processing-queue "one parse, many evaluators"). It asks every registered
///     <see cref="IDemoPass" /> about a path, plans the closure (every pass that says yes, plus every pass
///     waiting on one of those), and submits them together as one <see cref="DemoVisitRequest" />. The queue
///     reads the demo once in the mode the passes need and runs each one on that read, in After order.
///     <para>
///         Centralizing the submit decision (vs each feature running its own pump) means: a demo's
///         passes are submitted TOGETHER, so they always coalesce onto one entry (closing the
///         finalizing-race window between independently-timed feeders); one backlog + one
///         <see cref="IDemoProcessingQueue.CapacityAvailable" /> re-feed instead of N; and a future
///         feature plugs in by registering a pass: no new parse path.
///     </para>
///     <para>
///         Thread-safety: the outstanding/backlog sets are lock-guarded; <see cref="Consider(string)" /> may be
///         called from the rescan thread and from the (posted) capacity handler.
///     </para>
/// </summary>
public sealed class DemoEvaluationCoordinator : IDisposable
{
    // (passId, path) rejected because the tier was full, re-submitted on the next CapacityAvailable.
    private readonly HashSet<(string Pass, string Path)> _backlog = [];
    private readonly Func<IEnumerable<string>> _candidatePaths;
    private readonly IReadOnlyList<IDemoPass> _passes;

    // Set only by the live-registry constructor. Re-read on every poll so a pass whose pack just
    // came on is included on the next Consider/ConsiderAll, with no separate refresh step.
    private readonly Func<IReadOnlyList<IDemoPass>>? _livePasses;

    // Set only by the live-registry constructor: the registry's own Validate, so BuildServices can fail
    // fast on a cycle or an unknown After id without materializing any pass.
    private readonly Action? _validatePasses;

    private readonly object _lock = new();

    // (passId, path) currently submitted and not yet terminal, never re-submitted while present.
    private readonly HashSet<(string Pass, string Path)> _outstanding = [];
    private readonly IDemoProcessingQueue _queue;
    private readonly Action<ParsedDemo>? _parseReleased;

    // (passId, path) whose Run, Interest or OnFailed threw. Skipped for the rest of the session: a throw
    // leaves the pass interested, so without this every capacity re-feed parses the demo again.
    private readonly HashSet<(string Pass, string Path)> _faulted = [];

    private bool _disposed;

    /// <summary>
    ///     Told when a pass throws from any call made into it, with the demo when there is one. The
    ///     composition root counts it against the extension that contributed the pass.
    /// </summary>
    public Action<string, string?, Exception>? Faulted { get; set; }

    /// <param name="evaluators">The registered background features (order = run order within a slot). Each is wrapped as a pass.</param>
    /// <param name="queue">The shared processing queue that owns the workers + gate + coalescing.</param>
    /// <param name="candidatePaths">
    ///     Yields the current universe of demo paths to (re-)poll: typically the
    ///     library's known demos. Re-polled on <see cref="IDemoProcessingQueue.CapacityAvailable" />.
    /// </param>
    /// <param name="parseReleased">Called after <see cref="FanOutParsed" /> has handed a parse to every evaluator.</param>
    public DemoEvaluationCoordinator(
        IReadOnlyList<IDemoEvaluator> evaluators,
        IDemoProcessingQueue queue,
        Func<IEnumerable<string>> candidatePaths,
        Action<ParsedDemo>? parseReleased = null)
    {
        _parseReleased = parseReleased;
        _passes = evaluators.Select(e => (IDemoPass)new EvaluatorPassAdapter(e, [])).ToList();
        _queue = queue;
        _candidatePaths = candidatePaths;
        _queue.CapacityAvailable += OnCapacityAvailable;
    }

    /// <param name="passes">
    ///     Read fresh on every poll (a <see cref="PassRegistry" />'s <c>Resolve</c>, typically), not
    ///     snapshotted once: a pack pass is never constructed until the pack is enabled AND something
    ///     actually polls, and an enable mid-session is picked up on the very next poll.
    /// </param>
    /// <param name="queue">The shared processing queue that owns the workers + gate + coalescing.</param>
    /// <param name="candidatePaths">
    ///     Yields the current universe of demo paths to (re-)poll: typically the
    ///     library's known demos. Re-polled on <see cref="IDemoProcessingQueue.CapacityAvailable" />.
    /// </param>
    /// <param name="parseReleased">Called after <see cref="FanOutParsed" /> has handed a parse to every evaluator.</param>
    /// <param name="validatePasses">
    ///     The registry's own <c>Validate</c>: populates and sorts without constructing anything, so
    ///     <see cref="ValidateEvaluators" /> can fail fast on a cycle or an unknown After id at startup.
    /// </param>
    public DemoEvaluationCoordinator(
        Func<IReadOnlyList<IDemoPass>> passes,
        IDemoProcessingQueue queue,
        Func<IEnumerable<string>> candidatePaths,
        Action<ParsedDemo>? parseReleased = null,
        Action? validatePasses = null)
        : this(Array.Empty<IDemoEvaluator>(), queue, candidatePaths, parseReleased)
    {
        _livePasses = passes;
        _validatePasses = validatePasses;
    }

    private IReadOnlyList<IDemoPass> CurrentPasses => _livePasses?.Invoke() ?? _passes;

    /// <summary>
    ///     Validates the live registry's After graph without constructing any pass: a cycle or an
    ///     unknown After id throws here. No-op for a coordinator built from a plain snapshot.
    /// </summary>
    public void ValidateEvaluators() => _validatePasses?.Invoke();

    /// <summary>Detaches the capacity handler.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CapacityAvailable -= OnCapacityAvailable;
    }

    /// <summary>
    ///     The registered passes' ids in run order. The order is a contract other features
    ///     build on (a pass may read what the one before it wrote in the same visit), so the
    ///     composition root's list is pinned by a test through this.
    /// </summary>
    public IReadOnlyList<string> EvaluatorIds => [.. CurrentPasses.Select(p => p.Id)];

    /// <summary>Asks every pass about one path and submits one visit for the interested, not-outstanding ones.</summary>
    public void Consider(string path) => Consider(path, CurrentPasses);

    // ConsiderAll resolves the live list ONCE for the whole batch and passes it here, instead of every
    // Consider(path) re-resolving it: the registry's factory calls are cheap (DI caches the singleton)
    // but the gate checks and the sort lookup are not free to repeat per path.
    private void Consider(string path, IReadOnlyList<IDemoPass> passes)
    {
        VisitedDemo demo = new(path);
        List<IDemoPass> candidates = passes.Where(p => !IsFaulted(p.Id, path)).ToList();
        List<IDemoPass> planned = VisitPlanner.Plan(candidates, demo, PassLevel.Background,
            (pass, ex) => ReportFault(pass.Id, path, ex));

        // The level comes from the planned evaluators' own forced sets until visits are planned from events.
        PassLevel level = PassLevel.Background;
        foreach (IDemoPass pass in planned)
        {
            if (pass is EvaluatorPassAdapter adapter && SafeLevel(adapter, demo) > level)
            {
                level = PassLevel.UserRequested;
            }
        }

        List<IDemoPass> submitting = [];
        lock (_lock)
        {
            foreach (IDemoPass pass in planned)
            {
                (string Id, string Path) key = (pass.Id, path);
                if (_outstanding.Contains(key))
                {
                    continue; // already in flight for this pass
                }

                _outstanding.Add(key);
                _backlog.Remove(key);
                submitting.Add(pass);
            }
        }

        if (submitting.Count == 0)
        {
            return;
        }

        long orderHint = 0;
        foreach (IDemoPass pass in submitting)
        {
            if (pass is EvaluatorPassAdapter adapter)
            {
                orderHint = Math.Max(orderHint, SafeOrderHint(adapter, demo));
            }
        }

        IDemoQueueHandle handle = _queue.SubmitVisit(new DemoVisitRequest(path, level, submitting, orderHint,
            Path.GetFileName(path), (pass, outcome, error) => PassEnded(path, pass, outcome, error)));

        if (handle.State == DemoQueueItemState.Rejected)
        {
            // Tier full: hold for the next CapacityAvailable so it isn't dropped.
            lock (_lock)
            {
                foreach (IDemoPass pass in submitting)
                {
                    _outstanding.Remove((pass.Id, path));
                    _backlog.Add((pass.Id, path));
                }
            }
        }
    }

    // Runs in the queue's slot. Clears the outstanding entry either way so a demo that becomes interesting
    // again can be re-evaluated; a throw puts the pass on the session's skip list for that demo.
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

    /// <summary>
    ///     True while the named pass has at least one demo in flight (submitted, not yet
    ///     terminal): backs a feature's "is scanning" indicator.
    /// </summary>
    public bool HasOutstanding(string evaluatorId)
    {
        lock (_lock)
        {
            foreach ((string Pass, string Path) key in _outstanding)
            {
                if (string.Equals(key.Pass, evaluatorId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///     True when the pass threw on this demo earlier in the session; it is not offered the demo
    ///     again until the app restarts.
    /// </summary>
    public bool IsFaulted(string evaluatorId, string path)
    {
        lock (_lock)
        {
            return _faulted.Contains((evaluatorId, path));
        }
    }

    /// <summary>
    ///     Clears every pass's fault for <paramref name="path" />: the file changed, so the bytes a
    ///     pass threw on are gone and each one may be offered the demo again.
    /// </summary>
    public void ForgetFaults(string path)
    {
        lock (_lock)
        {
            _faulted.RemoveWhere(k => string.Equals(k.Path, path, StringComparison.Ordinal));
        }
    }

    /// <summary>Re-polls the whole candidate universe (rescan + capacity re-feed). Idempotent.</summary>
    public void ConsiderAll()
    {
        IReadOnlyList<IDemoPass> passes = CurrentPasses;
        foreach (string path in _candidatePaths())
        {
            Consider(path, passes);
        }
    }

    /// <summary>
    ///     Fans an ALREADY-parsed demo out to every registered evaluator's
    ///     <see cref="IDemoEvaluator.OnParsedOpportunistically" /> (except any in <paramref name="skip" />),
    ///     the "one processing event" generalization. Two callers: the
    ///     Library tier-2 slot hands its held parse to the OTHER evaluators (skip=<c>{library}</c>,
    ///     replacing the old <c>Tier2DemoParsed</c> piggyback), and an interactive open hands its parse in
    ///     so an un-indexed library demo fills its card from THAT parse instead of a second background one.
    ///     Because the hand-off is NOT gated on <see cref="IDemoEvaluator.Wants" />, it is order-independent
    ///     (a target whose backlog row doesn't exist yet still refreshes). Runs synchronously on the
    ///     caller's thread (offload the UI thread: an evaluator's replay/analysis can be multi-second);
    ///     each evaluator is isolated so one failure never blocks the others or the trigger. Only evaluators
    ///     have the hook; a pass that is not one is not fed.
    /// </summary>
    /// <param name="path">The .dem path of the already-parsed demo.</param>
    /// <param name="parsed">The held parse to hand to each evaluator (immutable: safe to read concurrently).</param>
    /// <param name="skip">
    ///     Evaluator <see cref="IDemoEvaluator.Id" />s already satisfied by this trigger and
    ///     therefore not re-fed: the parse's producer, or an evaluator with a richer channel for it (e.g.
    ///     Highlights on open, fed via the completed analysis run). Null → fan to all.
    /// </param>
    public void FanOutParsed(string path, ParsedDemo parsed, IReadOnlySet<string>? skip = null)
    {
        foreach (IDemoEvaluator evaluator in Evaluators(skip))
        {
            try
            {
                evaluator.OnParsedOpportunistically(path, parsed);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportFault(evaluator.Id, path, ex);
                // Isolated: a misbehaving hand-off handler must not fail the trigger or the other evaluators.
            }
        }

        try
        {
            _parseReleased?.Invoke(parsed);
        }
        catch (Exception)
        {
            // Releasing a cache must not fail the trigger.
        }
    }

    /// <summary>
    ///     <see cref="FanOutParsed" /> for a forward pass: the library's tier-2 pass hands its result to the
    ///     other evaluators that can read one.
    /// </summary>
    public void FanOutForward(string path, ForwardDemoResult pass, IReadOnlySet<string>? skip = null)
    {
        foreach (IDemoEvaluator evaluator in Evaluators(skip))
        {
            try
            {
                evaluator.OnForwardOpportunistically(path, pass);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportFault(evaluator.Id, path, ex);
                // Isolated, like FanOutParsed.
            }
        }
    }

    private IEnumerable<IDemoEvaluator> Evaluators(IReadOnlySet<string>? skip)
    {
        foreach (IDemoPass pass in CurrentPasses)
        {
            if (pass is EvaluatorPassAdapter adapter && (skip is null || !skip.Contains(pass.Id)))
            {
                yield return adapter.Evaluator;
            }
        }
    }

    private void OnCapacityAvailable() => ConsiderAll();

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

    // The callback is the composition root's; it must never break the pump either.
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
