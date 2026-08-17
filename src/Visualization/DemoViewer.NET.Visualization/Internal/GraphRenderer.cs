#region

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

internal static class GraphRenderer
{
    // SolidColorBrush is immutable in practice (we never mutate Color/Opacity after
    // construction). Cache one instance per ARGB key so each pan/zoom Render frame
    // doesn't allocate hundreds of fresh brushes for the same colours.
    private static readonly ConcurrentDictionary<uint, IBrush> _brushCache = new();

    // Debugger-red breakpoint disc + a darker ring for contrast against any fill; a light hole punched
    // out for the conditional variant.
    private static readonly Color _breakpointMarker = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color _breakpointMarkerRing = Color.FromRgb(0x5A, 0x12, 0x0E);
    private static readonly Color _breakpointMarkerHole = Color.FromRgb(0xFF, 0xE0, 0xDC);
    private static Typeface Mono => LabelRenderer.GetTypeface();

    internal static void DrawColumnEdgeLabels(DrawingContext dc,
        INodeTable table, TableLayoutWithEdges tableEntry, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        if (tableEntry.ColumnEdgeRoutes is null)
        {
            return;
        }

        List<Rect> placedLabels = new();

        for (int i = 0; i < table.ColumnEdges.Count && i < tableEntry.ColumnEdgeRoutes.Count; i++)
        {
            ITableColumnEdge ce = table.ColumnEdges[i];
            if (!ce.IsVisible)
            {
                continue;
            }

            IReadOnlyList<Point> route = tableEntry.ColumnEdgeRoutes[i];
            if (route.Count < 4)
            {
                continue;
            }

            string label = ce.ConditionLabel ?? ce.Label;
            if (label.Length == 0)
            {
                continue;
            }

            // Place on the last V segment (near column header) for better spread
            Point lastPt = toScreen(route[^1].X, route[^1].Y);
            Point prevPt = toScreen(route[^2].X, route[^2].Y);
            double lx = lastPt.X + 8 * scale;
            double ly = prevPt.Y + (lastPt.Y - prevPt.Y) * 0.3;

            double labelW = label.Length * 6 * scale;
            double labelH = 12 * scale;
            Rect labelRect = new(lx - labelH / 2, ly - labelW / 2, labelH, labelW);

            bool overlaps = false;
            foreach (Rect placed in placedLabels)
            {
                if (labelRect.Intersects(placed))
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
            {
                LabelRenderer.DrawLabel(dc, label, lx, ly, scale, style, -90);
                placedLabels.Add(labelRect);
            }
        }
    }

    internal static void DrawColumnEdges(DrawingContext dc,
        INodeTable table, TableLayoutWithEdges tableEntry, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        if (tableEntry.ColumnEdgeRoutes is null)
        {
            return;
        }

        EdgeStyleConfig es = style.Edge;

        for (int i = 0; i < table.ColumnEdges.Count && i < tableEntry.ColumnEdgeRoutes.Count; i++)
        {
            ITableColumnEdge ce = table.ColumnEdges[i];
            if (!ce.IsVisible)
            {
                continue;
            }

            IReadOnlyList<Point> route = tableEntry.ColumnEdgeRoutes[i];
            if (route.Count < 2)
            {
                continue;
            }

            EdgeStyle? perStyle = ce.Style;
            Color color = perStyle?.Color ?? es.ColorForEffect(ce.Effect);
            IBrush brush = Brush(color);
            Pen pen = new(brush, Math.Max(es.StrokeThickness * scale * 0.7, 0.5));

            List<Point> pts = new(route.Count);
            foreach (Point wp in route)
            {
                pts.Add(toScreen(wp.X, wp.Y));
            }

            StreamGeometry path = new();
            using (StreamGeometryContext sg = path.Open())
            {
                sg.BeginFigure(pts[0], false);
                for (int j = 1; j < pts.Count; j++)
                {
                    sg.LineTo(pts[j]);
                }

                sg.EndFigure(false);
            }

            dc.DrawGeometry(null, pen, path);
            ArrowRenderer.Draw(dc, brush, pts[^1], pts[^2], es.ArrowSize * scale);
        }
    }

    internal static void DrawEdges(DrawingContext dc,
        IReadOnlyList<IGraphEdge> edges, LayoutResult layout, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        EdgeStyleConfig es = style.Edge;

        foreach (IGraphEdge edge in edges)
        {
            if (ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            if (!edge.IsVisible)
            {
                continue;
            }

            if (!layout.EdgeRoutes.TryGetValue(edge, out IReadOnlyList<Point>? route) || route.Count < 2)
            {
                continue;
            }

            EdgeStyle? perStyle = edge.Style;
            Color color = perStyle?.Color ?? es.ColorForEffect(edge.Effect);
            double thickness = perStyle?.Thickness ?? es.StrokeThickness;
            bool dashed = perStyle?.IsDashed ?? EdgeStyleConfig.IsDashedByDefault(edge.Effect);

            IBrush brush = Brush(color);
            DashStyle? dash = dashed ? new DashStyle([5, 4], 0) : null;
            Pen pen = new(brush, Math.Max(thickness * scale, 0.5), dash);

            List<Point> pts = new(route.Count);
            foreach (Point wp in route)
            {
                pts.Add(toScreen(wp.X, wp.Y));
            }

            StreamGeometry path = new();
            using (StreamGeometryContext sg = path.Open())
            {
                sg.BeginFigure(pts[0], false);
                for (int i = 1; i < pts.Count; i++)
                {
                    sg.LineTo(pts[i]);
                }

                sg.EndFigure(false);
            }

            dc.DrawGeometry(null, pen, path);
            ArrowRenderer.Draw(dc, brush, pts[^1], pts[^2], es.ArrowSize * scale);

            // Breakpoint marker at the route's arc-length midpoint (cheap node-state repaint path).
            if (edge.HasBreakpoint)
            {
                Point mid = EdgeGeometry.PolylineMidpoint(route);
                DrawBreakpointDisc(dc, toScreen(mid.X, mid.Y), scale, edge.HasConditionalBreakpoint);
            }

            string label = edge.ConditionLabel is not null
                ? $"{edge.Label}  [{edge.ConditionLabel}]"
                : edge.Label;
            if (label.Length > 0 && layout.LabelPositions.TryGetValue(edge, out LabelPlacement? lp))
            {
                // The label-placement pass resolved a collision-free rect; draw
                // the label at its centre.
                Point c = toScreen(lp.X + lp.Width / 2, lp.Y + lp.Height / 2);
                LabelRenderer.DrawLabel(dc, label, c.X, c.Y, scale, style);
            }
        }
    }

    internal static void DrawGroups(DrawingContext dc, LayoutResult layout, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        SolidColorBrush bgBrush = new(style.GroupBackground, 0.6);
        Pen borderPen = new(Brush(style.GroupBorder), Math.Max(scale, 0.5));
        IBrush labelFg = Brush(style.GroupLabelColor);
        double labelSz = Math.Max(10 * scale, 1);

        foreach (GroupBounds g in layout.Groups)
        {
            Point tl = toScreen(g.X, g.Y);
            Rect rect = new(tl.X, tl.Y, g.Width * scale, g.Height * scale);
            dc.DrawRectangle(bgBrush, borderPen, rect, 8 * scale, 8 * scale);

            FormattedText ft = new(g.Label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, labelSz, labelFg);
            dc.DrawText(ft, new Point(tl.X + 8 * scale, tl.Y + 4 * scale));
        }
    }

    internal static void DrawNodeBackgrounds(DrawingContext dc,
        IReadOnlyList<IGraphNode> nodes, LayoutResult layout, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        NodeStyleConfig ns = style.Node;
        double hw = ns.Width / 2 * scale;
        double hh = ns.Height / 2 * scale;
        double nw = ns.Width * scale;
        double nh = ns.Height * scale;
        double cr = ns.CornerRadius * scale;

        foreach (IGraphNode node in nodes)
        {
            if (!layout.NodePositions.TryGetValue(node, out NodePosition? pos))
            {
                continue;
            }

            Point center = toScreen(pos.X, pos.Y);
            Rect box = new(center.X - hw, center.Y - hh, nw, nh);

            bool active = node.IsActive;
            NodeStyle? perStyle = node.Style;

            Color bgColor = active
                ? perStyle?.ActiveBackground ?? (node.IsRoot ? ns.RootBackground : ns.ActiveBackground)
                : perStyle?.InactiveBackground ?? ns.InactiveBackground;
            Color borderColor = active
                ? perStyle?.ActiveBorder ?? (node.IsRoot ? ns.RootBorder : ns.ActiveBorder)
                : perStyle?.InactiveBorder ?? ns.InactiveBorder;
            double borderThick = (active ? ns.ActiveBorderThickness : ns.InactiveBorderThickness) * scale;

            dc.DrawRectangle(Brush(bgColor),
                new Pen(Brush(borderColor), Math.Max(borderThick, 0.5)),
                box, cr, cr);

            // Breakpoint marker: a red disc in the node's top-left corner (debugger-red, matches the
            // app's DebuggerPanel language). Rides this cheap node-state repaint path, so arming/
            // disarming never relayouts. A CONDITIONAL breakpoint gets a hollow centre so it reads
            // distinctly from an unconditional one.
            if (node.HasBreakpoint)
            {
                double r = Math.Max(4.5 * scale, 3);
                Point dot = new(box.X + r * 0.6, box.Y + r * 0.6);
                DrawBreakpointDisc(dc, dot, scale, node.HasConditionalBreakpoint);
            }
        }
    }

    // Draws the breakpoint disc CENTRED at `center` (node markers pass a box corner; edge markers pass
    // the route midpoint). A conditional breakpoint punches a light core → a ring.
    private static void DrawBreakpointDisc(DrawingContext dc, Point center, double scale, bool conditional)
    {
        double r = Math.Max(4.5 * scale, 3);
        dc.DrawEllipse(Brush(_breakpointMarker),
            new Pen(Brush(_breakpointMarkerRing), Math.Max(scale, 0.75)),
            center, r, r);

        if (conditional)
        {
            dc.DrawEllipse(Brush(_breakpointMarkerHole), null, center, r * 0.42, r * 0.42);
        }
    }

    internal static void DrawNodeText(DrawingContext dc,
        IReadOnlyList<IGraphNode> nodes, LayoutResult layout, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        NodeStyleConfig ns = style.Node;

        foreach (IGraphNode node in nodes)
        {
            if (!layout.NodePositions.TryGetValue(node, out NodePosition? pos))
            {
                continue;
            }

            Point center = toScreen(pos.X, pos.Y);
            bool active = node.IsActive;
            NodeStyle? perStyle = node.Style;

            Color fgColor = active
                ? perStyle?.ActiveForeground ?? (node.IsRoot ? ns.RootForeground : ns.ActiveForeground)
                : perStyle?.InactiveForeground ?? ns.InactiveForeground;
            Color subColor = active ? ns.ActiveSubForeground : ns.InactiveSubForeground;

            IBrush fg = Brush(fgColor);
            IBrush sub = Brush(subColor);

            bool hasSubtitle = node.Subtitle is { Length: > 0 };
            double yShift = hasSubtitle ? 5 * scale : 0;

            double nameSz = Math.Max(ns.NameFontSize * scale, 1);
            FormattedText nameFt = new(node.Name, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, nameSz, fg);
            dc.DrawText(nameFt,
                new Point(center.X - nameFt.Width / 2, center.Y - nameFt.Height / 2 - 6 * scale - yShift));

            if (hasSubtitle)
            {
                double subtitleSz = Math.Max(ns.StateFontSize * scale * 0.9, 1);
                FormattedText subtitleFt = new(node.Subtitle!, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Mono, subtitleSz, sub);
                dc.DrawText(subtitleFt,
                    new Point(center.X - subtitleFt.Width / 2, center.Y - subtitleFt.Height / 2));
            }

            string stateText = node.IsRoot ? "always active"
                : node.DisplayValue is { Length: > 0 } dv ? dv
                : node.IsActive ? "ACTIVE" : "inactive";
            double subSz = Math.Max(ns.StateFontSize * scale, 1);
            FormattedText subFt = new(stateText, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, subSz, sub);
            dc.DrawText(subFt,
                new Point(center.X - subFt.Width / 2, center.Y + 4 * scale + yShift));
        }
    }

    internal static void DrawSelfLoops(DrawingContext dc,
        IReadOnlyList<IGraphEdge> edges, LayoutResult layout, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        EdgeStyleConfig es = style.Edge;

        foreach (IGraphEdge edge in edges)
        {
            if (!ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            if (!edge.IsVisible)
            {
                continue;
            }

            if (!layout.SelfLoopRoutes.TryGetValue(edge, out IReadOnlyList<Point>? route))
            {
                continue;
            }

            if (route.Count < 4)
            {
                continue;
            }

            EdgeStyle? perStyle = edge.Style;
            Color color = perStyle?.Color ?? es.ColorForEffect(edge.Effect);
            IBrush brush = Brush(color);
            Pen pen = new(brush, Math.Max(es.StrokeThickness * scale, 0.5));

            Point p0 = toScreen(route[0].X, route[0].Y);
            Point cp1 = toScreen(route[1].X, route[1].Y);
            Point cp2 = toScreen(route[2].X, route[2].Y);
            Point p3 = toScreen(route[3].X, route[3].Y);

            StreamGeometry path = new();
            using (StreamGeometryContext sg = path.Open())
            {
                sg.BeginFigure(p0, false);
                sg.CubicBezierTo(cp1, cp2, p3);
                sg.EndFigure(false);
            }

            dc.DrawGeometry(null, pen, path);
            ArrowRenderer.Draw(dc, brush, p3, cp2, es.ArrowSize * scale);

            // Breakpoint marker at the loop's Bézier midpoint (t=0.5).
            if (edge.HasBreakpoint)
            {
                Point mid = EdgeGeometry.CubicBezierPoint(route[0], route[1], route[2], route[3], 0.5);
                DrawBreakpointDisc(dc, toScreen(mid.X, mid.Y), scale, edge.HasConditionalBreakpoint);
            }

            string label = edge.ConditionLabel is not null
                ? $"{edge.Label}  [{edge.ConditionLabel}]"
                : edge.Label;
            if (label.Length > 0)
            {
                double mx = (p0.X + p3.X) / 2;
                double my = Math.Min(cp1.Y, cp2.Y) - 16 * scale;
                LabelRenderer.DrawLabel(dc, label, mx, my, scale, style);
            }
        }
    }

    private static IBrush Brush(Color c)
    {
        uint key = (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
        return _brushCache.GetOrAdd(key, _ => new SolidColorBrush(c));
    }
}
