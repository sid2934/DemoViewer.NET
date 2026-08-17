#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

internal static class SelfLoopRenderer
{
    internal static Dictionary<IGraphEdge, IReadOnlyList<Point>> ComputeSelfLoopRoutes(
        IEnumerable<IGraphEdge> selfLoopEdges,
        IReadOnlyDictionary<IGraphNode, NodePosition> positions,
        GraphStyle style)
    {
        Dictionary<IGraphEdge, IReadOnlyList<Point>> result = new();
        double halfW = style.Node.Width / 2;
        double halfH = style.Node.Height / 2;
        double loopH = style.Edge.LoopHeight;
        double stackOffset = style.Edge.LoopStackOffset;

        // Per-node counter so the Nth loop on a node sits at LoopHeight + N*offset
        // and remains visually distinct from siblings (instead of being dropped).
        Dictionary<IGraphNode, int> perNodeCount = new();

        foreach (IGraphEdge edge in selfLoopEdges)
        {
            if (!positions.TryGetValue(edge.Source, out NodePosition? pos))
            {
                continue;
            }

            int n = perNodeCount.GetValueOrDefault(edge.Source);
            perNodeCount[edge.Source] = n + 1;

            double h = loopH + n * stackOffset;
            double cx = pos.X;
            double topY = pos.Y - halfH;
            double startX = cx - halfW * 0.4;
            double endX = cx + halfW * 0.4;

            result[edge] = new List<Point>
            {
                new(startX, topY),
                new(startX - 30, topY - h),
                new(endX + 30, topY - h),
                new(endX, topY)
            };
        }

        return result;
    }
}
