#region

using System.Collections.ObjectModel;
using System.ComponentModel;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.Debugging;

/// <summary>
///     Holds the Analysis-graph breakpoint set and answers the "where does the message-seek timeline
///     stop next?" queries that drive Run / Continue / Step. Mirrors the shape of
///     <see cref="DebuggerService" /> (collection + <see cref="LastHit" /> + change event) but
///     operates on message indices, not frames: each breakpoint carries a sorted index list
///     (<see cref="GraphBreakpoint.HitIndices" />, filled by the view-model from the evaluation), and
///     Continue is a lower-bound scan over the enabled breakpoints' lists.
/// </summary>
public sealed class GraphBreakpointService
{
    /// <summary>All graph breakpoints, in insertion order. UI lists bind to this.</summary>
    public ObservableCollection<GraphBreakpoint> Breakpoints { get; } = [];

    /// <summary>The breakpoint the timeline most recently halted on; <c>null</c> when running.</summary>
    public GraphBreakpoint? LastHit { get; private set; }

    /// <summary>Message index of <see cref="LastHit" />, or -1 when none.</summary>
    public int LastHitMessageIndex { get; private set; } = -1;

    /// <summary>Fired when the breakpoint set changes (add/remove/enabled/condition): host recomputes hits + repaints.</summary>
    public event Action? Changed;

    /// <summary>Fired when <see cref="LastHit" /> changes (including back to null on Continue).</summary>
    public event Action? HitChanged;

    // ── Add / find / remove ───────────────────────────────────────────────────

    /// <summary>Returns the node breakpoint for <paramref name="name" />, or <c>null</c>.</summary>
    public GraphBreakpoint? FindNode(string name) =>
        Breakpoints.FirstOrDefault(b => b.TargetKind == GraphBreakpointTarget.Node && b.NodeName == name);

    /// <summary>Returns the edge breakpoint for the given source/dest/label/condition, or <c>null</c>.</summary>
    public GraphBreakpoint? FindEdge(string source, string dest, string label, string? conditionLabel) =>
        Breakpoints.FirstOrDefault(b => b.TargetKind == GraphBreakpointTarget.Edge
                                        && b.EdgeSource == source && b.EdgeDest == dest
                                        && b.EdgeLabel == label && b.EdgeConditionLabel == conditionLabel);

    /// <summary>Adds (or returns the existing) breakpoint on <paramref name="node" />.</summary>
    public GraphBreakpoint AddNode(IGraphNode node, string? condition = null)
    {
        GraphBreakpoint? existing = FindNode(node.Name);
        if (existing is not null)
        {
            return existing;
        }

        GraphBreakpoint bp = new()
        {
            TargetKind = GraphBreakpointTarget.Node,
            NodeName = node.Name,
            Condition = condition
        };
        AddInternal(bp);
        return bp;
    }

    /// <summary>Adds (or returns the existing) breakpoint on <paramref name="edge" />.</summary>
    public GraphBreakpoint AddEdge(IGraphEdge edge, string? condition = null)
    {
        GraphBreakpoint? existing = FindEdge(edge.Source.Name, edge.Destination.Name, edge.Label, edge.ConditionLabel);
        if (existing is not null)
        {
            return existing;
        }

        GraphBreakpoint bp = new()
        {
            TargetKind = GraphBreakpointTarget.Edge,
            EdgeSource = edge.Source.Name,
            EdgeDest = edge.Destination.Name,
            EdgeLabel = edge.Label,
            EdgeConditionLabel = edge.ConditionLabel,
            Condition = condition
        };
        AddInternal(bp);
        return bp;
    }

    /// <summary>Adds a fully-formed breakpoint (used when loading persisted state).</summary>
    public void Add(GraphBreakpoint bp) => AddInternal(bp);

    /// <summary>Removes the breakpoint with this id; returns true if found.</summary>
    public bool Remove(int id)
    {
        for (int i = 0; i < Breakpoints.Count; i++)
        {
            if (Breakpoints[i].Id == id)
            {
                Breakpoints[i].PropertyChanged -= OnBreakpointChanged;
                Breakpoints.RemoveAt(i);
                if (LastHit?.Id == id)
                {
                    ClearHit();
                }

                Changed?.Invoke();
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes all breakpoints.</summary>
    public void Clear()
    {
        if (Breakpoints.Count == 0)
        {
            return;
        }

        foreach (GraphBreakpoint bp in Breakpoints)
        {
            bp.PropertyChanged -= OnBreakpointChanged;
        }

        Breakpoints.Clear();
        ClearHit();
        Changed?.Invoke();
    }

    // ── Continue / step queries ───────────────────────────────────────────────

    /// <summary>
    ///     The first enabled-breakpoint hit at a message index strictly greater than
    ///     <paramref name="fromExclusive" />, or <c>null</c> if none. Lower-bound across all
    ///     breakpoints, taking the smallest qualifying index.
    /// </summary>
    public (GraphBreakpoint Breakpoint, int Index)? NextHit(int fromExclusive)
    {
        (GraphBreakpoint Breakpoint, int Index)? best = null;
        foreach (GraphBreakpoint bp in Breakpoints)
        {
            if (!bp.Enabled)
            {
                continue;
            }

            int idx = LowerBound(bp.HitIndices, fromExclusive + 1);
            if (idx < bp.HitIndices.Count && (best is null || bp.HitIndices[idx] < best.Value.Index))
            {
                best = (bp, bp.HitIndices[idx]);
            }
        }

        return best;
    }

    /// <summary>
    ///     The last enabled-breakpoint hit at a message index strictly less than
    ///     <paramref name="fromExclusive" />, or <c>null</c> if none. Upper-bound, taking the
    ///     largest qualifying index.
    /// </summary>
    public (GraphBreakpoint Breakpoint, int Index)? PrevHit(int fromExclusive)
    {
        (GraphBreakpoint Breakpoint, int Index)? best = null;
        foreach (GraphBreakpoint bp in Breakpoints)
        {
            if (!bp.Enabled)
            {
                continue;
            }

            int idx = LowerBound(bp.HitIndices, fromExclusive) - 1;
            if (idx >= 0 && (best is null || bp.HitIndices[idx] > best.Value.Index))
            {
                best = (bp, bp.HitIndices[idx]);
            }
        }

        return best;
    }

    /// <summary>Records that the timeline halted on <paramref name="bp" /> at <paramref name="index" />.</summary>
    public void MarkHit(GraphBreakpoint bp, int index)
    {
        bp.HitCount++;
        LastHit = bp;
        LastHitMessageIndex = index;
        HitChanged?.Invoke();
    }

    /// <summary>Clears the current stop (Continue is running again).</summary>
    public void ClearHit()
    {
        if (LastHit is null)
        {
            return;
        }

        LastHit = null;
        LastHitMessageIndex = -1;
        HitChanged?.Invoke();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void AddInternal(GraphBreakpoint bp)
    {
        bp.PropertyChanged += OnBreakpointChanged;
        Breakpoints.Add(bp);
        Changed?.Invoke();
    }

    // Enabled / Condition edits re-raise Changed so the host recomputes hits + repaints markers.
    private void OnBreakpointChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphBreakpoint.Enabled) or nameof(GraphBreakpoint.Condition))
        {
            Changed?.Invoke();
        }
    }

    // Smallest index i in a sorted list with list[i] >= value (i.e. first hit at or after value).
    private static int LowerBound(IReadOnlyList<int> sorted, int value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = lo + hi >> 1;
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
