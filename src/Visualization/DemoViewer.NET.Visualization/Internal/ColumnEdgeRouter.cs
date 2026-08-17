#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Column-edge router. Builds 4-point orthogonal channels above the table,
///     deconflicting the two ways routes can coincide:
///     1. Each edge gets its OWN horizontal channel (sharing one when two edges
///     have the same ColumnIndex would overlap their horizontal runs).
///     2. Edges that target the SAME column approach it at distinct x offsets
///     within the cell, so their vertical drop segments no longer overlap.
///     Routes are returned in the original edge order so the renderer's label /
///     colour indexing stays aligned.
/// </summary>
internal static class ColumnEdgeRouter
{
    internal static List<IReadOnlyList<Point>> RouteColumnEdges(
        IReadOnlyList<ITableColumnEdge> columnEdges,
        IReadOnlyDictionary<IGraphNode, NodePosition> nodePositions,
        TableLayout tableLayout,
        GraphStyle style)
    {
        TableStyleConfig ts = style.Table;
        double cellWidth = ts.CellWidth;

        // Sort by source x then column so adjacent channels cross less; keep the
        // original index to restore order at the end.
        List<(ITableColumnEdge ce, int idx)> ordered = columnEdges
            .Select((ce, idx) => (ce, idx))
            .OrderBy(t => nodePositions.TryGetValue(t.ce.SourceNode, out NodePosition? p) ? p.X : double.MaxValue)
            .ThenBy(t => t.ce.ColumnIndex)
            .ToList();

        // How many edges target each column → spread their approach x within the cell.
        Dictionary<int, int> perColumnCount = new();
        foreach (ITableColumnEdge ce in columnEdges)
        {
            perColumnCount[ce.ColumnIndex] = perColumnCount.GetValueOrDefault(ce.ColumnIndex) + 1;
        }

        Dictionary<int, int> perColumnSeen = new();

        Dictionary<int, List<Point>> routeByIdx = new();
        double halfH = style.Node.Height / 2;

        for (int ci = 0; ci < ordered.Count; ci++)
        {
            (ITableColumnEdge ce, int origIdx) = ordered[ci];
            if (!nodePositions.TryGetValue(ce.SourceNode, out NodePosition? srcPos))
            {
                routeByIdx[origIdx] = [];
                continue;
            }

            int col = Math.Min(ce.ColumnIndex, tableLayout.ColumnXCenters.Count - 1);
            double colCenterX = tableLayout.ColumnXCenters[col];

            // Spread sibling edges to the same column across the cell width.
            int count = perColumnCount[ce.ColumnIndex];
            int seen = perColumnSeen.GetValueOrDefault(ce.ColumnIndex);
            perColumnSeen[ce.ColumnIndex] = seen + 1;
            double colX = colCenterX;
            if (count > 1)
            {
                double usable = cellWidth * 0.6;
                double step = usable / (count - 1);
                colX = colCenterX - usable / 2 + seen * step;
            }

            double srcX = srcPos.X;
            double srcBottomY = srcPos.Y + halfH;
            // Each edge owns a distinct horizontal channel (top channel = first drawn).
            double channelY = tableLayout.Y - ts.ChannelTopGap - (ordered.Count - 1 - ci) * ts.ChannelSpacing;

            routeByIdx[origIdx] =
            [
                new Point(srcX, srcBottomY),
                new Point(srcX, channelY),
                new Point(colX, channelY),
                new Point(colX, tableLayout.Y)
            ];
        }

        List<IReadOnlyList<Point>> result = new(columnEdges.Count);
        for (int i = 0; i < columnEdges.Count; i++)
        {
            result.Add(routeByIdx.GetValueOrDefault(i, []));
        }

        return result;
    }
}
