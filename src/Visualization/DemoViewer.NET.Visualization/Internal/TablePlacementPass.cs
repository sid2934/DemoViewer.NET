#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 4 — Table placement. Anchors each table to the REAL graph content bbox:
///     the table is centred under the centroid of the graph nodes that fan
///     column-edges into it (so the connectors drop roughly vertically), falling
///     back to the graph's horizontal centre when a table has no column edges.
///     Multiple tables stack with a gap that grows with each table's column-edge
///     channel band, so per-round/per-game tables don't crowd their connectors.
///     Note: none of the layout metrics discriminate table alignment (a misplaced table
///     is still inside the bbox, so OOB is unaffected). This is a visual /
///     structural correctness fix; the metric table is unchanged by design.
/// </summary>
internal static class TablePlacementPass
{
    internal static void Run(LayoutContext ctx)
    {
        List<TableLayoutWithEdges> tableLayouts = new();
        if (ctx.Tables is null || ctx.Tables.Count == 0)
        {
            ctx.TableLayouts = tableLayouts;
            return;
        }

        // Real graph content extent (x for centring, bottom for stacking).
        double graphMinX = double.MaxValue, graphMaxX = double.MinValue, graphBottom = 0;
        foreach (IGraphNode node in ctx.NodePositions.Keys)
        {
            Rect r = ctx.NodeRect(node);
            if (r.Left < graphMinX)
            {
                graphMinX = r.Left;
            }

            if (r.Right > graphMaxX)
            {
                graphMaxX = r.Right;
            }

            if (r.Bottom > graphBottom)
            {
                graphBottom = r.Bottom;
            }
        }

        if (graphMinX > graphMaxX)
        {
            graphMinX = 0;
            graphMaxX = 0;
        }

        double graphCenterX = (graphMinX + graphMaxX) / 2;

        TableStyleConfig ts = ctx.Style.Table;
        double currentTopY = graphBottom;

        foreach (INodeTable table in ctx.Tables)
        {
            int cols = table.ColumnNames.Count;
            int rows = table.Rows.Count;
            double totalW = ts.RowHeaderWidth + cols * ts.CellWidth;
            double totalH = ts.HeaderHeight + rows * ts.CellHeight;

            // Centre the table under the centroid of its column-edge sources so
            // connectors drop vertically; fall back to the graph centre.
            double anchorX = ColumnSourceCentroidX(ctx, table) ?? graphCenterX;
            double tableX = anchorX - totalW / 2;

            // The column-edge channel band sits above the table; reserve room for
            // it in the inter-table gap so the next table doesn't overlap channels.
            double channelBand = table.ColumnEdges.Count > 0
                ? ts.ChannelTopGap + table.ColumnEdges.Count * ts.ChannelSpacing
                : 0;
            double tableY = currentTopY + ts.GapAboveTable + channelBand;

            List<double> colCenters = new(cols);
            for (int c = 0; c < cols; c++)
            {
                colCenters.Add(tableX + ts.RowHeaderWidth + c * ts.CellWidth + ts.CellWidth / 2);
            }

            TableLayout tl = new(tableX, tableY, totalW, totalH, colCenters);

            IReadOnlyList<IReadOnlyList<Point>>? colEdgeRoutes = null;
            if (table.ColumnEdges.Count > 0)
            {
                colEdgeRoutes = ColumnEdgeRouter.RouteColumnEdges(
                    table.ColumnEdges, ctx.NodePositions, tl, ctx.Style);
            }

            tableLayouts.Add(new TableLayoutWithEdges(tl, colEdgeRoutes));
            currentTopY = tl.Y + tl.Height;
        }

        ctx.TableLayouts = tableLayouts;
    }

    private static double? ColumnSourceCentroidX(LayoutContext ctx, INodeTable table)
    {
        double sum = 0;
        int n = 0;
        foreach (ITableColumnEdge ce in table.ColumnEdges)
        {
            if (!ctx.NodePositions.TryGetValue(ce.SourceNode, out NodePosition? pos))
            {
                continue;
            }

            sum += pos.X;
            n++;
        }

        return n > 0 ? sum / n : null;
    }
}
