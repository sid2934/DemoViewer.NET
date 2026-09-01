#region

using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.Debugging;

/// <summary>
///     The graph element a (conditional) breakpoint is being authored on: a node or an edge. Owns
///     the element plus its breakpoint <b>identity</b> (node name, or the edge
///     <c>(Source, Dest, Label, ConditionLabel)</c> 4-tuple) and the editor-seam metadata that doesn't
///     depend on a substrate (display name, whether the node-picker applies, find / add).
///     <para>
///         This is the structural fix for the old <c>_editingNode</c> XOR <c>_editingEdge</c>
///         invariant: a target is built via <see cref="ForNode" /> / <see cref="ForEdge" /> (exactly
///         one element set), and the view-model holds a single <c>_editingTarget</c>, so "both" or
///         "neither" can't happen. The substrate-specific work (snapshot state vs event payload) stays
///         in the view-model, dispatched once on <see cref="Kind" />.
///     </para>
/// </summary>
public sealed class ConditionTarget
{
    private ConditionTarget(GraphBreakpointTarget kind, IGraphNode? node, IGraphEdge? edge)
    {
        Kind = kind;
        Node = node;
        Edge = edge;
    }

    /// <summary>Whether this targets a node or an edge.</summary>
    public GraphBreakpointTarget Kind { get; }

    /// <summary>The node: non-null iff <see cref="Kind" /> is <see cref="GraphBreakpointTarget.Node" />.</summary>
    public IGraphNode? Node { get; }

    /// <summary>The edge: non-null iff <see cref="Kind" /> is <see cref="GraphBreakpointTarget.Edge" />.</summary>
    public IGraphEdge? Edge { get; }

    /// <summary>Whether the node-picker affordance applies (nodes only, picking a node makes no sense for an edge).</summary>
    public bool SupportsPicker => Kind == GraphBreakpointTarget.Node;

    /// <summary>Display label for the editor header (<c>NodeName</c>, or <c>A→B [event]</c>).</summary>
    public string DisplayName => Kind == GraphBreakpointTarget.Node
        ? Node!.Name
        : $"{Edge!.Source.Name}→{Edge.Destination.Name} [{Edge.Label}]";

    /// <summary>Builds a node target.</summary>
    public static ConditionTarget ForNode(IGraphNode node) => new(GraphBreakpointTarget.Node, node, null);

    /// <summary>Builds an edge target.</summary>
    public static ConditionTarget ForEdge(IGraphEdge edge) => new(GraphBreakpointTarget.Edge, null, edge);

    /// <summary>Finds this target's existing breakpoint in <paramref name="service" />, or <c>null</c>.</summary>
    public GraphBreakpoint? Find(GraphBreakpointService service) => Kind == GraphBreakpointTarget.Node
        ? service.FindNode(Node!.Name)
        : service.FindEdge(Edge!.Source.Name, Edge.Destination.Name, Edge.Label, Edge.ConditionLabel);

    /// <summary>Adds (or returns the existing) breakpoint for this target, with an optional condition.</summary>
    public GraphBreakpoint Add(GraphBreakpointService service, string? condition) => Kind == GraphBreakpointTarget.Node
        ? service.AddNode(Node!, condition)
        : service.AddEdge(Edge!, condition);
}
