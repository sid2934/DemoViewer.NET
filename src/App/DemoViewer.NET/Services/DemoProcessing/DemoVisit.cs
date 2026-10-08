namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     One demo's visit on a queue entry: the passes joined so far, the level the highest joiner asked
///     for, and the read they need. Owned by <see cref="DemoProcessingQueue" /> and mutated under its lock;
///     the ordering and the mode choice here are pure.
/// </summary>
internal sealed class DemoVisit
{
    private readonly List<VisitPass> _passes = [];
    private long _arrival;

    public DemoVisit(VisitedDemo demo, PassLevel level)
    {
        Demo = demo;
        Level = level;
    }

    public VisitedDemo Demo { get; }

    /// <summary>The highest level any joiner asked for.</summary>
    public PassLevel Level { get; private set; }

    public int Count => _passes.Count;

    /// <summary>The pass ids, each once, in arrival order.</summary>
    public IReadOnlyList<string> OwnerIds =>
        _passes.Select(p => p.Pass.Id).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Adds a pass with the needs it declared for this demo; the same instance is not added twice.</summary>
    public bool Add(IDemoPass pass, PassNeeds needs, Action<IDemoPass, PassOutcome, Exception?>? ended)
    {
        if (_passes.Any(p => ReferenceEquals(p.Pass, pass)))
        {
            return false;
        }

        _passes.Add(new VisitPass(pass, needs, ended, _arrival++));
        return true;
    }

    /// <summary>Moves every pass of <paramref name="other" /> onto this visit, preserving their order.</summary>
    public void TakeFrom(DemoVisit other)
    {
        AddAll(other);
        other._passes.Clear();
    }

    /// <summary>Adds every pass of <paramref name="other" /> to this visit; <paramref name="other" /> keeps them.</summary>
    public void AddAll(DemoVisit other)
    {
        foreach (VisitPass p in other._passes.OrderBy(p => p.Arrival))
        {
            Add(p.Pass, p.Needs, p.Ended);
        }

        Raise(other.Level);
    }

    public void Raise(PassLevel level)
    {
        if (level > Level)
        {
            Level = level;
        }
    }

    public int RemoveWhere(Predicate<VisitPass> match) => _passes.RemoveAll(match);

    public bool Contains(IDemoPass pass) => _passes.Exists(p => ReferenceEquals(p.Pass, pass));

    /// <summary>True when a job of one of <paramref name="owners" /> rides this visit.</summary>
    public bool HasJobOwnedBy(IReadOnlySet<string> owners) =>
        _passes.Exists(p => p.Pass is DemoJobPass && owners.Contains(p.Pass.Owner));

    /// <summary>True when a pass needing <paramref name="needs" /> can join a visit already reading this way.</summary>
    public static bool CanJoinRunning(PassNeeds needs, bool forward, ForwardNeeds produced, bool userCommands) =>
        forward
            ? needs.Mode == ParseMode.Forward && (needs.Forward & ~produced) == 0
            : userCommands || !needs.UserCommands;

    /// <summary>
    ///     The read the passes need together: forward only when every pass takes one and the host can read
    ///     forward, else retained; the union of forward needs; player inputs when any pass reads them.
    /// </summary>
    public (bool Forward, ForwardNeeds Needs, bool UserCommands) ChooseMode(bool forwardAvailable)
    {
        bool forward = forwardAvailable && _passes.Count > 0 && _passes.TrueForAll(p => p.Needs.Mode == ParseMode.Forward);
        ForwardNeeds needs = ForwardNeeds.None;
        bool userCommands = false;
        foreach (VisitPass p in _passes)
        {
            needs |= p.Needs.Forward;
            userCommands |= p.Needs.UserCommands;
        }

        return (forward, needs, userCommands);
    }

    /// <summary>
    ///     The passes in <see cref="IDemoPass.After" /> order: a pass runs after every pass it names that is
    ///     on this visit, whatever order they joined in. Arrival breaks ties. An After id not on the visit is
    ///     not waited for.
    /// </summary>
    public IReadOnlyList<VisitPass> Ordered()
    {
        List<VisitPass> pending = _passes.OrderBy(p => p.Arrival).ToList();
        HashSet<string> present = new(pending.Select(p => p.Pass.Id), StringComparer.Ordinal);
        HashSet<string> emitted = new(StringComparer.Ordinal);
        List<VisitPass> ordered = new(pending.Count);
        while (pending.Count > 0)
        {
            int pick = pending.FindIndex(p => p.Pass.After.All(dep =>
                !present.Contains(dep) || emitted.Contains(dep) || string.Equals(dep, p.Pass.Id, StringComparison.Ordinal)));
            if (pick < 0)
            {
                // A cycle the registry did not see (a visit mixes registered and one-shot passes): arrival
                // order is the only order left that runs everything.
                pick = 0;
            }

            VisitPass next = pending[pick];
            pending.RemoveAt(pick);
            emitted.Add(next.Pass.Id);
            ordered.Add(next);
        }

        // A job joining the visit reads what the registered passes wrote on this read.
        return [.. ordered.Where(p => p.Pass is not DemoJobPass), .. ordered.Where(p => p.Pass is DemoJobPass)];
    }
}

/// <summary>A pass on a visit, with the needs it declared when it joined and the submitter's completion callback.</summary>
internal sealed record VisitPass(IDemoPass Pass, PassNeeds Needs, Action<IDemoPass, PassOutcome, Exception?>? Ended, long Arrival);

/// <summary>
///     Plans which passes a demo's visit should carry: every pass that says yes, plus every pass that
///     answers <see cref="PassInterest.IfUpstreamRuns" /> and has an <see cref="IDemoPass.After" /> ancestor
///     already in the set, to a fixpoint.
/// </summary>
internal static class VisitPlanner
{
    /// <param name="passes">The candidate passes, in registry order.</param>
    /// <param name="demo">The demo.</param>
    /// <param name="level">The level the visit is planned at.</param>
    /// <param name="faulted">Told when a pass throws from <see cref="IDemoPass.Interest" />; that pass is left out.</param>
    /// <returns>The passes to submit, in the input order.</returns>
    public static List<IDemoPass> Plan(IReadOnlyList<IDemoPass> passes, VisitedDemo demo, PassLevel level,
        Action<IDemoPass, Exception>? faulted = null)
    {
        Dictionary<IDemoPass, PassInterest> answers = new(ReferenceEqualityComparer.Instance);
        foreach (IDemoPass pass in passes)
        {
            try
            {
                answers[pass] = pass.Interest(demo, level);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                faulted?.Invoke(pass, ex);
                answers[pass] = PassInterest.No;
            }
        }

        HashSet<string> chosen = new(StringComparer.Ordinal);
        foreach (IDemoPass pass in passes)
        {
            if (answers[pass] == PassInterest.Yes)
            {
                chosen.Add(pass.Id);
            }
        }

        // Each round can enable the next link of a chain (facts after library, index after facts).
        Dictionary<string, IDemoPass> byId = passes.ToDictionary(p => p.Id, StringComparer.Ordinal);
        bool added = true;
        while (added)
        {
            added = false;
            foreach (IDemoPass pass in passes)
            {
                if (answers[pass] == PassInterest.IfUpstreamRuns && !chosen.Contains(pass.Id)
                    && Ancestors(pass, byId).Any(chosen.Contains))
                {
                    chosen.Add(pass.Id);
                    added = true;
                }
            }
        }

        return passes.Where(p => chosen.Contains(p.Id)).ToList();
    }

    // Every id reachable through After among the candidates. A pass that reads what its grandparent wrote
    // still joins when the parent is current and sits the visit out.
    private static HashSet<string> Ancestors(IDemoPass pass, Dictionary<string, IDemoPass> byId)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new(pass.After);
        while (pending.Count > 0)
        {
            string id = pending.Pop();
            if (!seen.Add(id) || !byId.TryGetValue(id, out IDemoPass? parent))
            {
                continue;
            }

            foreach (string dep in parent.After)
            {
                pending.Push(dep);
            }
        }

        return seen;
    }
}
