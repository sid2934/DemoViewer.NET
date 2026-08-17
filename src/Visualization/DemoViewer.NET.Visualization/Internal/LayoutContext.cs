#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Mutable state threaded through the layout passes. Each pass reads what
///     earlier passes produced and enriches it; <see cref="LayoutPipeline" />
///     freezes the final state into an immutable <see cref="LayoutResult" />.
/// </summary>
internal sealed class LayoutContext
{
    internal LayoutContext(
        IReadOnlyList<IGraphNode> nodes,
        IReadOnlyList<IGraphEdge> edges,
        IReadOnlyList<INodeGroup>? groups,
        IReadOnlyList<INodeTable>? tables,
        GraphStyle style)
    {
        Nodes = nodes;
        Edges = edges;
        Groups = groups;
        Tables = tables;
        Style = style;
    }

    internal Dictionary<IGraphEdge, IReadOnlyList<Point>> EdgeRoutes { get; set; } = new();
    internal IReadOnlyList<IGraphEdge> Edges { get; }

    // Filled by GroupBoundsPass.
    internal List<GroupBounds> GroupBounds { get; set; } = new();
    internal IReadOnlyList<INodeGroup>? Groups { get; }

    // Filled by LabelPlacementPass.
    internal Dictionary<IGraphEdge, LabelPlacement> LabelPositions { get; set; } = new();

    // Filled by CoarseLayoutPass (MSAGL).
    internal Dictionary<IGraphNode, NodePosition> NodePositions { get; set; } = new();

    internal IReadOnlyList<IGraphNode> Nodes { get; }

    // Filled by SelfLoopPass — keyed by edge so multiple loops on one node stay distinct (mirrors EdgeRoutes shape).
    internal Dictionary<IGraphEdge, IReadOnlyList<Point>> SelfLoopRoutes { get; set; } = new();
    internal GraphStyle Style { get; }

    // Filled by TablePlacementPass.
    internal List<TableLayoutWithEdges> TableLayouts { get; set; } = new();
    internal IReadOnlyList<INodeTable>? Tables { get; }
    internal double TotalHeight { get; set; }

    // Filled by ContainmentPass.
    internal double TotalWidth { get; set; }

    /// <summary>Resolves a node's box height (per-node override or theme default).</summary>
    internal double NodeHeight(IGraphNode n) => n.Style?.Height ?? Style.Node.Height;

    /// <summary>Axis-aligned box for a placed node, in logical coordinates.</summary>
    internal Rect NodeRect(IGraphNode n)
    {
        NodePosition pos = NodePositions[n];
        double w = NodeWidth(n), h = NodeHeight(n);
        return new Rect(pos.X - w / 2, pos.Y - h / 2, w, h);
    }

    /// <summary>Resolves a node's box width (per-node override or theme default).</summary>
    internal double NodeWidth(IGraphNode n) => n.Style?.Width ?? Style.Node.Width;

    internal LayoutResult ToResult() => new(
        NodePositions, EdgeRoutes, SelfLoopRoutes, GroupBounds, TableLayouts,
        TotalWidth, TotalHeight)
    {
        LabelPositions = LabelPositions
    };

    /// <summary>Translates every placed primitive by (dx, dy). Used by containment to remove negative coordinates.</summary>
    internal void Translate(double dx, double dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        Dictionary<IGraphNode, NodePosition> movedNodes = new(NodePositions.Count);
        foreach ((IGraphNode n, NodePosition p) in NodePositions)
        {
            movedNodes[n] = new NodePosition(p.X + dx, p.Y + dy);
        }

        NodePositions = movedNodes;

        Dictionary<IGraphEdge, IReadOnlyList<Point>> movedEdges = new(EdgeRoutes.Count);
        foreach ((IGraphEdge e, IReadOnlyList<Point> r) in EdgeRoutes)
        {
            movedEdges[e] = Shift(r, dx, dy);
        }

        EdgeRoutes = movedEdges;

        Dictionary<IGraphEdge, IReadOnlyList<Point>> movedLoops = new(SelfLoopRoutes.Count);
        foreach ((IGraphEdge e, IReadOnlyList<Point> r) in SelfLoopRoutes)
        {
            movedLoops[e] = Shift(r, dx, dy);
        }

        SelfLoopRoutes = movedLoops;

        GroupBounds = GroupBounds
            .Select(g => g with
            {
                X = g.X + dx,
                Y = g.Y + dy
            })
            .ToList();

        TableLayouts = TableLayouts
            .Select(t => new TableLayoutWithEdges(
                t.Layout with
                {
                    X = t.Layout.X + dx,
                    Y = t.Layout.Y + dy,
                    ColumnXCenters = t.Layout.ColumnXCenters.Select(c => c + dx).ToList()
                },
                t.ColumnEdgeRoutes?.Select(r => (IReadOnlyList<Point>)Shift(r, dx, dy)).ToList()))
            .ToList();

        if (LabelPositions.Count > 0)
        {
            Dictionary<IGraphEdge, LabelPlacement> movedLabels = new(LabelPositions.Count);
            foreach ((IGraphEdge e, LabelPlacement l) in LabelPositions)
            {
                movedLabels[e] = l with
                {
                    X = l.X + dx,
                    Y = l.Y + dy
                };
            }

            LabelPositions = movedLabels;
        }
    }

    private static List<Point> Shift(IReadOnlyList<Point> route, double dx, double dy)
    {
        List<Point> moved = new(route.Count);
        foreach (Point p in route)
        {
            moved.Add(new Point(p.X + dx, p.Y + dy));
        }

        return moved;
    }
}
