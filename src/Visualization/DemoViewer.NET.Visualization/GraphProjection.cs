namespace DemoViewer.NET.Visualization;

/// <summary>
///     The result of projecting a graph onto a subset of its nodes: the surviving nodes, the edges
///     induced among them, and the groups restricted to them. A pure data carrier handed straight
///     to <see cref="GraphViewModel.SetGraphAsync" />.
/// </summary>
/// <param name="Nodes">The selected nodes, in their original relative order.</param>
/// <param name="Edges">Edges whose <em>both</em> endpoints are in <see cref="Nodes" /> (induced subgraph).</param>
/// <param name="Groups">Groups restricted to selected members; empty groups dropped.</param>
public sealed record SubGraph(
    IReadOnlyList<IGraphNode> Nodes,
    IReadOnlyList<IGraphEdge> Edges,
    IReadOnlyList<INodeGroup> Groups);

/// <summary>
///     Pure graph-theory projection: given a graph and a node predicate, produce the
///     <em>induced sub-graph</em> (selected nodes + the edges among them + restricted groups).
///     <para>
///         This operates purely on the rendering interfaces (<see cref="IGraphNode" />,
///         <see cref="IGraphEdge" />, <see cref="INodeGroup" />) — it selects references, never
///         rebuilds elements and never touches application/evaluation state. Consumers that carry
///         per-node state by identity (e.g. a snapshot column index) keep resolving it on every
///         surviving node, so the full data set is untouched while only a subset is rendered. No
///         layout happens here; the caller hands the result to
///         <see cref="GraphViewModel.SetGraphAsync" />, which lays out just the subset.
///     </para>
/// </summary>
public static class GraphProjection
{
    /// <summary>
    ///     Projects <paramref name="nodes" />/<paramref name="edges" />/<paramref name="groups" />
    ///     onto the subset selected by <paramref name="include" />.
    /// </summary>
    /// <param name="nodes">All nodes.</param>
    /// <param name="edges">All edges.</param>
    /// <param name="groups">All groups, or <c>null</c>.</param>
    /// <param name="include">Predicate selecting which nodes survive. Edges survive iff both endpoints do.</param>
    /// <returns>The induced sub-graph.</returns>
    public static SubGraph Induce(
        IReadOnlyList<IGraphNode> nodes,
        IReadOnlyList<IGraphEdge> edges,
        IReadOnlyList<INodeGroup>? groups,
        Func<IGraphNode, bool> include)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(include);

        // Selected node set (reference identity — same instances the layout/state paths use).
        HashSet<IGraphNode> selected = new(ReferenceEqualityComparer.Instance);
        List<IGraphNode> selectedNodes = new();
        foreach (IGraphNode node in nodes)
        {
            if (include(node))
            {
                selected.Add(node);
                selectedNodes.Add(node);
            }
        }

        // Induced edges: both endpoints must survive.
        List<IGraphEdge> inducedEdges = new();
        foreach (IGraphEdge edge in edges)
        {
            if (selected.Contains(edge.Source) && selected.Contains(edge.Destination))
            {
                inducedEdges.Add(edge);
            }
        }

        // Groups restricted to surviving members; drop any that end up empty so the layout doesn't
        // draw a container around nothing. Group bounds are recomputed by the layout pass from the
        // restricted member set, so no stale full-size boxes.
        List<INodeGroup> restrictedGroups = new();
        if (groups is not null)
        {
            foreach (INodeGroup group in groups)
            {
                List<IGraphNode> members = new();
                foreach (IGraphNode member in group.Members)
                {
                    if (selected.Contains(member))
                    {
                        members.Add(member);
                    }
                }

                if (members.Count > 0)
                {
                    restrictedGroups.Add(new ProjectedGroup(group.GroupName, members));
                }
            }
        }

        return new SubGraph(selectedNodes, inducedEdges, restrictedGroups);
    }

    /// <summary>A group whose membership has been restricted to a sub-graph's surviving nodes.</summary>
    private sealed record ProjectedGroup(string GroupName, IReadOnlyList<IGraphNode> Members) : INodeGroup;
}
