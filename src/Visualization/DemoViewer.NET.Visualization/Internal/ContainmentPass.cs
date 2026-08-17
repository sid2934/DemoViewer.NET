#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 6 — Containment / fit. Computes the TRUE content bbox over EVERY emitted
///     primitive — node rects, edge routes, self-loop routes, group bounds, tables,
///     column-edge routes, and placed labels.
///     If any primitive extends to negative coordinates (a self-loop above a top-row
///     node pushes y &lt; 0), the whole layout is translated so the content origin
///     sits at (Padding, Padding). TotalWidth/Height then span the real bbox plus
///     padding on every side. This is the structural fix for the SelfLoopHeavy OOB
///     bug: the reported bounds now actually contain all geometry, so the renderer's
///     fit-scale and ClipToBounds frame the whole graph.
/// </summary>
internal static class ContainmentPass
{
    private const double Padding = 60;

    internal static void Run(LayoutContext ctx)
    {
        (double minX, double minY, double maxX, double maxY) = ComputeBounds(ctx);

        // Nothing placed — keep the minimum canvas.
        if (minX > maxX || minY > maxY)
        {
            ctx.TotalWidth = 400;
            ctx.TotalHeight = 300;
            return;
        }

        // Shift so the content origin lands at (Padding, Padding); this also
        // pulls any negative-coordinate primitive (self-loops) back in-bounds.
        double dx = Padding - minX;
        double dy = Padding - minY;
        ctx.Translate(dx, dy);

        ctx.TotalWidth = Math.Max(maxX - minX + 2 * Padding, 400);
        ctx.TotalHeight = Math.Max(maxY - minY + 2 * Padding, 300);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) ComputeBounds(
        LayoutContext ctx)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Acc(double x, double y)
        {
            if (x < minX)
            {
                minX = x;
            }

            if (y < minY)
            {
                minY = y;
            }

            if (x > maxX)
            {
                maxX = x;
            }

            if (y > maxY)
            {
                maxY = y;
            }
        }

        void AccRect(double x, double y, double w, double h)
        {
            Acc(x, y);
            Acc(x + w, y + h);
        }

        void AccRoute(IReadOnlyList<Point> route)
        {
            foreach (Point p in route)
            {
                Acc(p.X, p.Y);
            }
        }

        foreach (IGraphNode node in ctx.NodePositions.Keys)
        {
            Rect r = ctx.NodeRect(node);
            AccRect(r.X, r.Y, r.Width, r.Height);
        }

        foreach (IReadOnlyList<Point> route in ctx.EdgeRoutes.Values)
        {
            AccRoute(route);
        }

        foreach (IReadOnlyList<Point> route in ctx.SelfLoopRoutes.Values)
        {
            AccRoute(route);
        }

        foreach (GroupBounds g in ctx.GroupBounds)
        {
            AccRect(g.X, g.Y, g.Width, g.Height);
        }

        foreach (TableLayoutWithEdges entry in ctx.TableLayouts)
        {
            AccRect(entry.Layout.X, entry.Layout.Y, entry.Layout.Width, entry.Layout.Height);
            if (entry.ColumnEdgeRoutes is not null)
            {
                foreach (IReadOnlyList<Point> route in entry.ColumnEdgeRoutes)
                {
                    AccRoute(route);
                }
            }
        }

        foreach (LabelPlacement l in ctx.LabelPositions.Values)
        {
            AccRect(l.X, l.Y, l.Width, l.Height);
        }

        return (minX, minY, maxX, maxY);
    }
}
