namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Multi-pass graph layout pipeline. Ordered passes share one mutable
///     <see cref="LayoutContext" />; the result is frozen into an immutable
///     <see cref="LayoutResult" /> for the renderer.
///     Pass order:
///     1. CoarseLayout   — MSAGL Sugiyama (layer assignment + crossing
///     minimisation + coordinate assignment; the hard parts).
///     2. SelfLoops      — loop routes per node.
///     3. GroupBounds    — AABB over each group's member node boxes.
///     4. TablePlacement — tables anchored to the real graph content bbox.
///     5. LabelPlacement — collision-resolved edge-label rectangles.
///     6. Containment    — true bbox over EVERY primitive; shift out negatives so
///     the reported bounds actually contain all geometry.
/// </summary>
internal static class LayoutPipeline
{
    internal static LayoutResult ComputeFullLayout(
        IReadOnlyList<IGraphNode> nodes,
        IReadOnlyList<IGraphEdge> edges,
        IReadOnlyList<INodeGroup>? groups,
        IReadOnlyList<INodeTable>? tables,
        GraphStyle style)
    {
        LayoutContext ctx = new(nodes, edges, groups, tables, style);

        CoarseLayoutPass.Run(ctx);
        SelfLoopPass.Run(ctx);
        GroupBoundsPass.Run(ctx);
        TablePlacementPass.Run(ctx);
        LabelPlacementPass.Run(ctx);
        ContainmentPass.Run(ctx);

        return ctx.ToResult();
    }
}
