#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 1 — Coarse layout. Runs MSAGL Sugiyama (layer assignment + crossing
///     minimisation + coordinate assignment — the genuinely hard parts, which MSAGL
///     does well) via <see cref="MsaglTranslator" /> to produce node coordinates and
///     edge routes.
///     Crossing-reduction note: raising MSAGL's ordering effort
///     (RepetitionCoefficientForOrdering / extra median passes) cut BigStandard
///     crossings 587 -> ~440-485 but REGRESSED DenseCluster (116 -> 130+), could
///     create coincident endpoint anchors (SharedPorts > 0) on the small dense
///     graph, and cost 2-3x layout time. Crossing-min is NP-hard and the heuristic
///     trades crossings between graphs; the default ordering is near-optimal for
///     these fixtures, so the tuning was rejected.
/// </summary>
internal static class CoarseLayoutPass
{
    internal static void Run(LayoutContext ctx)
    {
        (IReadOnlyDictionary<IGraphNode, NodePosition> positions, IReadOnlyDictionary<IGraphEdge, IReadOnlyList<Point>> routes, _, _) =
            MsaglTranslator.RunLayout(ctx.Nodes, ctx.Edges, ctx.Style);

        ctx.NodePositions = new Dictionary<IGraphNode, NodePosition>(positions);
        ctx.EdgeRoutes = new Dictionary<IGraphEdge, IReadOnlyList<Point>>(routes);
    }
}
