namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Resolves the demo-evaluation fan-out order from declared ids and <c>After</c> constraints instead
///     of a hand-written array (strat-book-plugin.md §6 item 11). Core evaluators register unconditionally;
///     a pack's evaluators are added through a deferred callback so the pack contribution set is never read
///     until something actually resolves the fan-out, and a pack's factory is never invoked while its gate
///     is off.
///     <para>
///         The <c>After</c> graph is validated and sorted once, over every declared id regardless of gate
///         state, so an unknown id or a cycle throws the first time anything is resolved, not only when the
///         owning pack happens to be on. <see cref="Resolve" /> re-applies the gate on every call: an
///         evaluator whose pack just came on is included on the very next call, with no separate refresh step.
///     </para>
/// </summary>
public sealed class EvaluatorRegistry
{
    private readonly object _gate = new();
    private readonly List<string> _declaredOrder = [];
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private Action? _populatePacks;
    private bool _packsPopulated;
    private IReadOnlyList<string>? _sortedIds;

    private sealed record Entry(Func<IDemoEvaluator> Factory, IReadOnlyList<string> After, Func<bool> Enabled);

    /// <summary>A core evaluator: always in the graph, never gated.</summary>
    public void AddCore(string id, Func<IDemoEvaluator> factory, params string[] after) =>
        Add(id, factory, after, null);

    /// <summary>
    ///     Defers reading pack contributions until the first <see cref="Resolve" />. <paramref name="populate" />
    ///     is expected to call <see cref="AddPackEvaluator" /> for each contributed evaluator; it runs exactly
    ///     once, the first time this registry is asked to resolve anything.
    /// </summary>
    public void AddPacksLazily(Action populate) => _populatePacks = populate;

    /// <summary>Called back from the <see cref="AddPacksLazily" /> delegate, once per contributed evaluator.</summary>
    public void AddPackEvaluator(string id, Func<IDemoEvaluator> factory, IReadOnlyList<string> after, Func<bool> enabled) =>
        Add(id, factory, after, enabled);

    /// <summary>
    ///     The live, ordered, currently-enabled evaluators: every core entry, plus every pack evaluator
    ///     whose owning pack resolves on right now. A disabled pack's factories are never invoked, so its
    ///     evaluators are never constructed. Thread-safe; callable from the rescan thread and from worker
    ///     callbacks.
    /// </summary>
    public IReadOnlyList<IDemoEvaluator> Resolve()
    {
        lock (_gate)
        {
            EnsurePacksPopulated();

            List<IDemoEvaluator> result = new(_declaredOrder.Count);
            foreach (string id in SortedIds())
            {
                Entry entry = _entries[id];
                if (!entry.Enabled())
                {
                    continue;
                }

                IDemoEvaluator instance = entry.Factory();
                if (!string.Equals(instance.Id, id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Evaluator registered as '{id}' built an instance whose Id is '{instance.Id}'.");
                }

                result.Add(instance);
            }

            return result;
        }
    }

    /// <summary>
    ///     Populates and sorts without materializing anything: a cycle or an unknown After id throws here,
    ///     at startup, instead of waiting for the first real <see cref="Resolve" />. The sort it computes is
    ///     cached, so a <see cref="Resolve" /> right after does no extra work.
    /// </summary>
    public void Validate()
    {
        lock (_gate)
        {
            EnsurePacksPopulated();
            SortedIds();
        }
    }

    // The latch is set BEFORE the callback runs, not after: if populate throws partway through, a later
    // call must not retry it, which would re-add the same ids and throw "registered more than once",
    // masking the real failure behind a second, confusing one.
    private void EnsurePacksPopulated()
    {
        if (_packsPopulated)
        {
            return;
        }

        _packsPopulated = true;
        _populatePacks?.Invoke();
    }

    private void Add(string id, Func<IDemoEvaluator> factory, IReadOnlyList<string> after, Func<bool>? enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(after);
        lock (_gate)
        {
            if (!_entries.TryAdd(id, new Entry(factory, after, enabled ?? (static () => true))))
            {
                throw new InvalidOperationException($"Evaluator id '{id}' is registered more than once.");
            }

            _declaredOrder.Add(id);
            _sortedIds = null;
        }
    }

    // Stable topological sort: declared order breaks ties among evaluators whose After ids are already
    // satisfied, so a looser or empty After still resolves deterministically. Validated and cached against
    // the full declared set, independent of gate state, so a cycle or an unknown After id throws on the
    // first Resolve() regardless of which packs are on.
    private IReadOnlyList<string> SortedIds()
    {
        if (_sortedIds is not null)
        {
            return _sortedIds;
        }

        Dictionary<string, int> indexOf = new(_declaredOrder.Count, StringComparer.Ordinal);
        for (int i = 0; i < _declaredOrder.Count; i++)
        {
            indexOf[_declaredOrder[i]] = i;
        }

        foreach (string id in _declaredOrder)
        {
            foreach (string dep in _entries[id].After)
            {
                if (!indexOf.ContainsKey(dep))
                {
                    throw new InvalidOperationException(
                        $"Evaluator '{id}' declares After '{dep}', which is not a registered evaluator id.");
                }
            }
        }

        bool[] emitted = new bool[_declaredOrder.Count];
        List<string> ordered = new(_declaredOrder.Count);
        for (int pass = 0; pass < _declaredOrder.Count; pass++)
        {
            int pick = -1;
            for (int i = 0; i < _declaredOrder.Count; i++)
            {
                if (emitted[i])
                {
                    continue;
                }

                if (_entries[_declaredOrder[i]].After.All(dep => emitted[indexOf[dep]]))
                {
                    pick = i;
                    break;
                }
            }

            if (pick < 0)
            {
                string cycle = string.Join(", ", _declaredOrder.Where((_, i) => !emitted[i]));
                throw new InvalidOperationException($"Evaluator After constraints form a cycle among: {cycle}.");
            }

            emitted[pick] = true;
            ordered.Add(_declaredOrder[pick]);
        }

        _sortedIds = ordered;
        return ordered;
    }
}
