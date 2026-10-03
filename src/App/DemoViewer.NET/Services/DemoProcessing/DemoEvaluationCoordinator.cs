#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     The single submitter that turns "which features want this demo?" into "parse it once, fan the
///     result out to all of them" (demo-processing-queue "one parse, many evaluators"). It polls every
///     registered <see cref="IDemoEvaluator" /> for a path and, for each interested one, submits a queue
///     request tagged with that evaluator's <see cref="IDemoEvaluator.Id" />. Because
///     <see cref="IDemoProcessingQueue.SubmitBackground" /> coalesces by path, all interested evaluators'
///     requests merge into one entry → one parse → each evaluator's <see cref="IDemoEvaluator.Evaluate" />
///     runs (isolated) on the single held <see cref="ParsedDemo" />.
///     <para>
///         Centralizing the submit decision (vs each feature running its own pump) means: a demo's
///         evaluators are submitted TOGETHER, so they always coalesce onto one entry (closing the
///         finalizing-race window between independently-timed feeders); one backlog + one
///         <see cref="IDemoProcessingQueue.CapacityAvailable" /> re-feed instead of N; and a future
///         feature plugs in by registering an evaluator: no new parse path.
///     </para>
///     <para>
///         Per entry, one pass: forward when every owner on the entry can take one
///         (<see cref="IDemoEvaluator.ForwardFor" />), retained otherwise, with every owner run on that one
///         retained parse. Two passes back to back peak at the retained one and cost both, so a mixed entry
///         never splits; an owner that arrives while a forward pass it cannot join is running stops that pass
///         (the queue moves its owners onto the entry that replaces it).
///     </para>
///     <para>
///         Thread-safety: the outstanding/backlog sets are lock-guarded; <see cref="Consider(string)" /> may be
///         called from the rescan thread and from the (posted) capacity handler.
///     </para>
/// </summary>
public sealed class DemoEvaluationCoordinator : IDisposable
{
    // (evaluatorId, path) rejected because the tier was full, re-submitted on the next CapacityAvailable.
    private readonly HashSet<(string Eval, string Path)> _backlog = [];
    private readonly Func<IEnumerable<string>> _candidatePaths;
    private readonly IReadOnlyList<IDemoEvaluator> _evaluators;

    // Set only by the live-registry constructor. Re-read on every poll so an evaluator whose pack just
    // came on is included on the next Consider/ConsiderAll, with no separate refresh step.
    private readonly Func<IReadOnlyList<IDemoEvaluator>>? _liveEvaluators;

    // Set only by the live-registry constructor: the registry's own Validate, so BuildServices can fail
    // fast on a cycle or an unknown After id without materializing any evaluator.
    private readonly Action? _validateEvaluators;

    private readonly object _lock = new();

    // (evaluatorId, path) currently submitted and not yet terminal, never re-submitted while present.
    private readonly HashSet<(string Eval, string Path)> _outstanding = [];
    private readonly IDemoProcessingQueue _queue;
    private readonly Action<ParsedDemo>? _parseReleased;

    private bool _disposed;

    /// <param name="evaluators">The registered background features (order = fan-out order within a slot).</param>
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
        _evaluators = evaluators;
        _queue = queue;
        _candidatePaths = candidatePaths;
        _queue.CapacityAvailable += OnCapacityAvailable;
    }

    /// <param name="evaluators">
    ///     Read fresh on every poll (an <see cref="EvaluatorRegistry" />'s <c>Resolve</c>, typically), not
    ///     snapshotted once: a pack evaluator is never constructed until the pack is enabled AND something
    ///     actually polls, and an enable mid-session is picked up on the very next poll.
    /// </param>
    /// <param name="queue">The shared processing queue that owns the workers + gate + coalescing.</param>
    /// <param name="candidatePaths">
    ///     Yields the current universe of demo paths to (re-)poll: typically the
    ///     library's known demos. Re-polled on <see cref="IDemoProcessingQueue.CapacityAvailable" />.
    /// </param>
    /// <param name="parseReleased">Called after <see cref="FanOutParsed" /> has handed a parse to every evaluator.</param>
    /// <param name="validateEvaluators">
    ///     The registry's own <c>Validate</c>: populates and sorts without constructing anything, so
    ///     <see cref="ValidateEvaluators" /> can fail fast on a cycle or an unknown After id at startup.
    /// </param>
    public DemoEvaluationCoordinator(
        Func<IReadOnlyList<IDemoEvaluator>> evaluators,
        IDemoProcessingQueue queue,
        Func<IEnumerable<string>> candidatePaths,
        Action<ParsedDemo>? parseReleased = null,
        Action? validateEvaluators = null)
        : this([], queue, candidatePaths, parseReleased)
    {
        _liveEvaluators = evaluators;
        _validateEvaluators = validateEvaluators;
    }

    private IReadOnlyList<IDemoEvaluator> CurrentEvaluators => _liveEvaluators?.Invoke() ?? _evaluators;

    /// <summary>
    ///     Validates the live registry's After graph without constructing any evaluator: a cycle or an
    ///     unknown After id throws here. No-op for a coordinator built from a plain snapshot.
    /// </summary>
    public void ValidateEvaluators() => _validateEvaluators?.Invoke();

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
    ///     The registered evaluators' ids in fan-out order. The order is a contract other features
    ///     build on (an evaluator may read what the one before it wrote in the same pass), so the
    ///     composition root's list is pinned by a test through this.
    /// </summary>
    public IReadOnlyList<string> EvaluatorIds => [.. CurrentEvaluators.Select(e => e.Id)];

    /// <summary>Polls every evaluator for one path and submits for each interested, not-outstanding one.</summary>
    public void Consider(string path) => Consider(path, CurrentEvaluators);

    // ConsiderAll resolves the live list ONCE for the whole batch and passes it here, instead of every
    // Consider(path) re-resolving it: the registry's factory calls are cheap (DI caches the singleton)
    // but the gate checks and the sort lookup are not free to repeat per path.
    private void Consider(string path, IReadOnlyList<IDemoEvaluator> evaluators)
    {
        List<IDemoEvaluator> wanting = [];
        foreach (IDemoEvaluator evaluator in evaluators)
        {
            bool wants;
            try
            {
                wants = evaluator.Wants(path);
            }
            catch (Exception)
            {
                // A misbehaving interest check must not stall the others or the pump.
                continue;
            }

            if (wants)
            {
                wanting.Add(evaluator);
            }
        }

        // Every request carries the union: the first one may start parsing before the next is submitted.
        bool userCommands = wanting.Any(SafeReadsUserCommands);
        Dictionary<IDemoEvaluator, ForwardNeeds?> forward = wanting.ToDictionary(e => e, e => SafeForwardFor(e, path));
        ForwardNeeds needs = forward.Values.Aggregate(ForwardNeeds.None, (all, n) => all | (n ?? ForwardNeeds.None));
        foreach (IDemoEvaluator evaluator in wanting)
        {
            (string Id, string path) key = (evaluator.Id, path);
            lock (_lock)
            {
                if (_outstanding.Contains(key))
                {
                    continue; // already in flight for this evaluator
                }

                _outstanding.Add(key);
                _backlog.Remove(key);
            }

            Submit(evaluator, path, key, userCommands, forward[evaluator] is not null, needs);
        }
    }

    /// <summary>
    ///     True while the named evaluator has at least one demo in flight (submitted, not yet
    ///     terminal): backs a feature's "is scanning" indicator.
    /// </summary>
    public bool HasOutstanding(string evaluatorId)
    {
        lock (_lock)
        {
            foreach ((string Eval, string Path) key in _outstanding)
            {
                if (string.Equals(key.Eval, evaluatorId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Re-polls the whole candidate universe (rescan + capacity re-feed). Idempotent.</summary>
    public void ConsiderAll()
    {
        IReadOnlyList<IDemoEvaluator> evaluators = CurrentEvaluators;
        foreach (string path in _candidatePaths())
        {
            Consider(path, evaluators);
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
    ///     each evaluator is isolated so one failure never blocks the others or the trigger.
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
        foreach (IDemoEvaluator evaluator in CurrentEvaluators)
        {
            if (skip is not null && skip.Contains(evaluator.Id))
            {
                continue;
            }

            try
            {
                evaluator.OnParsedOpportunistically(path, parsed);
            }
            catch (Exception)
            {
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

    private void Submit(IDemoEvaluator evaluator, string path, (string Eval, string Path) key, bool userCommands,
        bool forward, ForwardNeeds needs)
    {
        IDemoQueueHandle handle = _queue.SubmitBackground(new DemoProcessingRequest(
            path,
            evaluator.Id,
            SafePriority(evaluator, path),
            SafeOrderHint(evaluator, path),
            parsed => Complete(key, () => evaluator.Evaluate(path, parsed)),
            _ => Complete(key, () => evaluator.OnFailed(path)),
            Path.GetFileName(path),
            userCommands,
            forward ? pass => Complete(key, () => evaluator.EvaluateForward(path, pass)) : null,
            forward ? needs : ForwardNeeds.None));

        if (handle.State == DemoQueueItemState.Rejected)
        {
            // Tier full: hold for the next CapacityAvailable so it isn't dropped.
            lock (_lock)
            {
                _outstanding.Remove(key);
                _backlog.Add(key);
            }
        }
    }

    // Runs in the queue's gate slot (the queue already isolates the whole delegate via its SafeInvoke,
    // so a throw is logged there); the finally guarantees the outstanding entry clears either way so the
    // path can be re-evaluated later if it becomes interesting again.
    private void Complete((string Eval, string Path) key, Action work)
    {
        try
        {
            work();
        }
        finally
        {
            lock (_lock)
            {
                _outstanding.Remove(key);
            }
        }
    }

    private void OnCapacityAvailable() => ConsiderAll();

    /// <summary>
    ///     <see cref="FanOutParsed" /> for a forward pass: the library's tier-2 pass hands its result to the
    ///     other evaluators that can read one.
    /// </summary>
    public void FanOutForward(string path, ForwardDemoResult pass, IReadOnlySet<string>? skip = null)
    {
        foreach (IDemoEvaluator evaluator in CurrentEvaluators)
        {
            if (skip is not null && skip.Contains(evaluator.Id))
            {
                continue;
            }

            try
            {
                evaluator.OnForwardOpportunistically(path, pass);
            }
            catch (Exception)
            {
                // Isolated, like FanOutParsed.
            }
        }
    }

    private static ForwardNeeds? SafeForwardFor(IDemoEvaluator evaluator, string path)
    {
        try
        {
            return evaluator.ForwardFor(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DemoJobPriority SafePriority(IDemoEvaluator evaluator, string path)
    {
        try
        {
            return evaluator.PriorityFor(path);
        }
        catch (Exception)
        {
            return DemoJobPriority.Background;
        }
    }

    private static bool SafeReadsUserCommands(IDemoEvaluator evaluator)
    {
        try
        {
            return evaluator.ReadsUserCommands;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static long SafeOrderHint(IDemoEvaluator evaluator, string path)
    {
        try
        {
            return evaluator.OrderHint(path);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
