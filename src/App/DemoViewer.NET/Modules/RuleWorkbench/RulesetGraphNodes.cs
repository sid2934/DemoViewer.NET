#region

using Avalonia;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     One node of the Workbench's ruleset graph, shaped for a node-based renderer: a position and
///     the text to put in the box, and nothing else.
///     <para>
///         Read-only by construction. There is no setter on <see cref="Location" /> and no command
///         on the type, so nothing an editor would need to drag, connect or rename exists yet. That
///         is design.md §6.4 item 3, which is a VIEW; editing is items 4 and 5 and is deliberately
///         not started.
///     </para>
/// </summary>
/// <param name="Key">Identity within one render, used to join connections to their endpoints.</param>
/// <param name="Location">Top-left of the node box in graph units, which is what a canvas positions by.</param>
/// <param name="Title">The node's name.</param>
/// <param name="Subtitle">The secondary line, or <c>null</c>.</param>
/// <param name="Value">The node's display value, or <c>null</c>.</param>
/// <param name="IsRoot">Whether this is the graph's entry point.</param>
/// <param name="IsPerPlayer">Whether the node came from a per-player template.</param>
public sealed record RulesetGraphNode(
    int Key,
    Point Location,
    string Title,
    string? Subtitle,
    string? Value,
    bool IsRoot,
    bool IsPerPlayer);

/// <summary>
///     One edge, as the two points it runs between rather than as a routed polyline.
///     <para>
///         A node renderer draws its own connection between two boxes, so the MSAGL route is not
///         used here and is not exposed: <c>GraphViewModel</c> publishes positions only, and this is
///         the consumer that decided that was enough.
///     </para>
/// </summary>
/// <param name="Source">Where the connection leaves the source box, in graph units.</param>
/// <param name="Target">Where it enters the destination box.</param>
/// <param name="Label">The event name, or empty.</param>
/// <param name="Condition">The predicate, or <c>null</c>.</param>
public sealed record RulesetGraphConnection(
    Point Source,
    Point Target,
    string Label,
    string? Condition);

/// <summary>
///     Turns a laid-out <see cref="GraphViewModel" /> into the two lists a node renderer binds to.
///     <para>
///         Pure and static so it can be tested without an Avalonia app: everything it reads is
///         already computed, and everything it produces is a record.
///     </para>
/// </summary>
public static class RulesetGraphProjection
{
    /// <summary>
    ///     Projects the laid-out graph. Returns empty lists when layout has not finished, which is
    ///     the normal state between a topology change and the background layout completing.
    /// </summary>
    public static (IReadOnlyList<RulesetGraphNode> Nodes, IReadOnlyList<RulesetGraphConnection> Connections)
        Project(
            GraphViewModel graph,
            IReadOnlyList<IGraphNode> nodes,
            IReadOnlyList<IGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        IReadOnlyDictionary<IGraphNode, GraphNodePlacement> placements = graph.NodePlacements;
        if (placements.Count == 0)
        {
            return ([], []);
        }

        NodeStyleConfig style = graph.Style.Node;

        double Width(IGraphNode n) => n.Style?.Width ?? style.Width;
        double Height(IGraphNode n) => n.Style?.Height ?? style.Height;

        List<RulesetGraphNode> projected = new(nodes.Count);

        foreach (IGraphNode node in nodes)
        {
            if (!placements.TryGetValue(node, out GraphNodePlacement at))
            {
                // A node the layout did not place cannot be drawn anywhere. It is skipped rather
                // than stacked at the origin, where it would read as a real node in a wrong place.
                continue;
            }

            projected.Add(new RulesetGraphNode(
                projected.Count,
                new Point(at.X - Width(node) / 2, at.Y - Height(node) / 2),
                node.Name,
                node.Subtitle,
                node.DisplayValue,
                node.IsRoot,
                node is GraphNodeViewModel { IsPerPlayer: true }));
        }

        List<RulesetGraphConnection> connections = new(edges.Count);
        foreach (IGraphEdge edge in edges)
        {
            if (!edge.IsVisible || ReferenceEquals(edge.Source, edge.Destination)
                || !placements.TryGetValue(edge.Source, out GraphNodePlacement from)
                || !placements.TryGetValue(edge.Destination, out GraphNodePlacement to))
            {
                continue;
            }

            // Leave the bottom of the source and enter the top of the destination. The layout is
            // rotated so edges run downward, so those are the faces an edge actually uses, and
            // anchoring at the centres instead would draw every line through its own node box.
            connections.Add(new RulesetGraphConnection(
                new Point(from.X, from.Y + Height(edge.Source) / 2),
                new Point(to.X, to.Y - Height(edge.Destination) / 2),
                edge.Label,
                edge.ConditionLabel));
        }

        return (projected, connections);
    }
}
