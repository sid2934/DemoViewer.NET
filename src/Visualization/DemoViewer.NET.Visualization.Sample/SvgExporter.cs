#region

using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization.Sample;

/// <summary>
///     Headless SVG export of a graph layout, drawn directly from the
///     <see cref="LayoutResult" /> geometry — no render backend / display required
///     (works where the GUI dies at Avalonia.Native error -6661). Mirrors
///     GraphRenderer / TableRenderer in logical (pre-zoom) coordinates and reuses
///     the same GraphStyle colours/sizes so the SVG matches the on-screen rendering
///     for visual review (positions, routes, tables, overlaps, containment) — not
///     pixel/font-exact.
/// </summary>
internal static class SvgExporter
{
    private static readonly CultureInfo _ci = CultureInfo.InvariantCulture;

    /// <summary>To svg.</summary>
    public static string ToSvg(
        IReadOnlyList<IGraphNode> nodes,
        IReadOnlyList<IGraphEdge> edges,
        IReadOnlyList<INodeTable>? tables,
        GraphStyle style,
        LayoutResult layout)
    {
        double w = layout.TotalWidth, h = layout.TotalHeight;
        StringBuilder sb = new();

        sb.AppendFormat(_ci,
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {0:F1} {1:F1}' font-family='monospace'>",
            w, h);
        sb.AppendFormat(_ci, "<rect x='0' y='0' width='{0:F1}' height='{1:F1}' fill='{2}'/>",
            w, h, Hex(style.CanvasBackground));

        foreach (GroupBounds g in layout.Groups)
        {
            sb.AppendFormat(_ci,
                "<rect x='{0:F1}' y='{1:F1}' width='{2:F1}' height='{3:F1}' rx='8' fill='{4}' fill-opacity='0.6' stroke='{5}'/>",
                g.X, g.Y, g.Width, g.Height, Hex(style.GroupBackground), Hex(style.GroupBorder));
            sb.Append(Txt(g.X + 8, g.Y + 12, g.Label, 10, style.GroupLabelColor, "start"));
        }

        DrawTables(sb, tables, layout, style);

        EdgeStyleConfig es = style.Edge;
        foreach (IGraphEdge edge in edges)
        {
            if (ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            if (!layout.EdgeRoutes.TryGetValue(edge, out IReadOnlyList<Point>? route) || route.Count < 2)
            {
                continue;
            }

            Color c = edge.Style?.Color ?? es.ColorForEffect(edge.Effect);
            bool dashed = edge.Style?.IsDashed ?? EdgeStyleConfig.IsDashedByDefault(edge.Effect);
            sb.Append(Polyline(route, c, dashed));
            sb.Append(Arrow(route[^1], route[^2], c, es.ArrowSize));
        }

        foreach ((IGraphEdge _, IReadOnlyList<Point> route) in layout.SelfLoopRoutes)
        {
            if (route.Count >= 2)
            {
                sb.Append(Polyline(route, es.SetValueColor, false));
            }
        }

        NodeStyleConfig ns = style.Node;
        foreach (IGraphNode node in nodes)
        {
            if (!layout.NodePositions.TryGetValue(node, out NodePosition? pos))
            {
                continue;
            }

            double nw = node.Style?.Width ?? ns.Width;
            double nh = node.Style?.Height ?? ns.Height;
            bool active = node.IsActive;
            Color bg = active ? node.IsRoot ? ns.RootBackground : ns.ActiveBackground : ns.InactiveBackground;
            Color bd = active ? node.IsRoot ? ns.RootBorder : ns.ActiveBorder : ns.InactiveBorder;
            Color fg = active ? node.IsRoot ? ns.RootForeground : ns.ActiveForeground : ns.InactiveForeground;
            Color subC = active ? ns.ActiveSubForeground : ns.InactiveSubForeground;

            sb.AppendFormat(_ci,
                "<rect x='{0:F1}' y='{1:F1}' width='{2:F1}' height='{3:F1}' rx='{4:F1}' fill='{5}' stroke='{6}'/>",
                pos.X - nw / 2, pos.Y - nh / 2, nw, nh, ns.CornerRadius, Hex(bg), Hex(bd));

            bool hasSub = node.Subtitle is { Length: > 0 };
            double yShift = hasSub ? 5 : 0;
            sb.Append(Txt(pos.X, pos.Y - 6 - yShift, node.Name, ns.NameFontSize, fg, "middle"));
            if (hasSub)
            {
                sb.Append(Txt(pos.X, pos.Y, node.Subtitle!, ns.StateFontSize * 0.9, subC, "middle"));
            }

            string state = node.IsRoot ? "always active"
                : node.DisplayValue is { Length: > 0 } dv ? dv
                : active ? "ACTIVE" : "inactive";
            sb.Append(Txt(pos.X, pos.Y + 8 + yShift, state, ns.StateFontSize, subC, "middle"));
        }

        foreach ((IGraphEdge edge, LabelPlacement lp) in layout.LabelPositions)
        {
            string label = edge.ConditionLabel is not null
                ? $"{edge.Label}  [{edge.ConditionLabel}]"
                : edge.Label;
            if (label.Length == 0)
            {
                continue;
            }

            sb.Append(Txt(lp.X + lp.Width / 2, lp.Y + lp.Height / 2,
                label, es.LabelFontSize, es.LabelForeground, "middle"));
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Arrow(Point tip, Point from, Color c, double size)
    {
        double dx = tip.X - from.X, dy = tip.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
        {
            return "";
        }

        dx /= len;
        dy /= len;
        double px = -dy, py = dx, wing = size * 0.45;
        return string.Format(_ci,
            "<polygon points='{0:F1},{1:F1} {2:F1},{3:F1} {4:F1},{5:F1}' fill='{6}'/>",
            tip.X, tip.Y,
            tip.X - dx * size + px * wing, tip.Y - dy * size + py * wing,
            tip.X - dx * size - px * wing, tip.Y - dy * size - py * wing,
            Hex(c));
    }

    // Mirrors TableRenderer: outline, header bg, column headers + grid lines,
    // per-row grid line + label + filter annotation + cell backgrounds/values,
    // then the graph->table column-edge connectors. The INodeTable supplies the
    // cell content; the TableLayout supplies geometry (origin + width/height).
    private static void DrawTables(StringBuilder sb, IReadOnlyList<INodeTable>? tables,
        LayoutResult layout, GraphStyle style)
    {
        TableStyleConfig ts = style.Table;
        double sw = ts.CellWidth, sh = ts.CellHeight, shh = ts.HeaderHeight, srw = ts.RowHeaderWidth;

        for (int ti = 0; ti < layout.Tables.Count; ti++)
        {
            TableLayoutWithEdges entry = layout.Tables[ti];
            TableLayout tl = entry.Layout;
            INodeTable? table = tables is not null && ti < tables.Count ? tables[ti] : null;

            sb.AppendFormat(_ci,
                "<rect x='{0:F1}' y='{1:F1}' width='{2:F1}' height='{3:F1}' rx='4' fill='{4}' stroke='{5}'/>",
                tl.X, tl.Y, tl.Width, tl.Height, Hex(ts.Background), Hex(ts.GridLine));

            if (table is not null)
            {
                int cols = table.ColumnNames.Count;
                sb.AppendFormat(_ci,
                    "<rect x='{0:F1}' y='{1:F1}' width='{2:F1}' height='{3:F1}' fill='{4}'/>",
                    tl.X + srw, tl.Y, tl.Width - srw, shh, Hex(ts.HeaderBackground));

                for (int c = 0; c < cols; c++)
                {
                    double cxLeft = tl.X + srw + c * sw;
                    sb.AppendFormat(_ci, "<line x1='{0:F1}' y1='{1:F1}' x2='{0:F1}' y2='{2:F1}' stroke='{3}'/>",
                        cxLeft, tl.Y, tl.Y + tl.Height, Hex(ts.GridLine));
                    sb.Append(Txt(cxLeft + sw / 2, tl.Y + shh / 2, table.ColumnNames[c], 9, ts.HeaderForeground, "middle"));
                }

                for (int r = 0; r < table.Rows.Count; r++)
                {
                    ITableRow row = table.Rows[r];
                    double ry = tl.Y + shh + r * sh;
                    sb.AppendFormat(_ci, "<line x1='{0:F1}' y1='{1:F1}' x2='{2:F1}' y2='{1:F1}' stroke='{3}'/>",
                        tl.X, ry, tl.X + tl.Width, Hex(ts.GridLine));
                    sb.Append(Txt(tl.X + 8, ry + sh / 2, row.Label, 9, ts.HeaderForeground, "start"));
                    if (row.FilterAnnotation is { Length: > 0 })
                    {
                        sb.Append(Txt(tl.X - 8, ry + sh / 2, row.FilterAnnotation, 7.5, ts.DimForeground, "end"));
                    }

                    for (int c = 0; c < row.Cells.Count && c < cols; c++)
                    {
                        ITableCell cell = row.Cells[c];
                        double cxLeft = tl.X + srw + c * sw;
                        if (cell.IsActive)
                        {
                            sb.AppendFormat(_ci,
                                "<rect x='{0:F1}' y='{1:F1}' width='{2:F1}' height='{3:F1}' fill='{4}'/>",
                                cxLeft, ry, sw, sh, Hex(ts.ActiveCellBackground));
                        }

                        string text = cell.DisplayValue ?? (cell.IsActive ? "ACTIVE" : "-");
                        Color fg = cell.IsActive ? ts.CellForeground : ts.DimForeground;
                        sb.Append(Txt(cxLeft + sw / 2, ry + sh / 2, text, 9, fg, "middle"));
                    }
                }
            }

            if (entry.ColumnEdgeRoutes is not null)
            {
                foreach (IReadOnlyList<Point> route in entry.ColumnEdgeRoutes)
                {
                    sb.Append(Polyline(route, style.Edge.SetValueColor, false));
                }
            }
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Polyline(IReadOnlyList<Point> pts, Color c, bool dashed)
    {
        StringBuilder sb = new("<polyline points='");
        foreach (Point p in pts)
        {
            sb.AppendFormat(_ci, "{0:F1},{1:F1} ", p.X, p.Y);
        }

        sb.AppendFormat(_ci, "' fill='none' stroke='{0}' stroke-width='1.5'{1}/>",
            Hex(c), dashed ? " stroke-dasharray='5,4'" : "");
        return sb.ToString();
    }

    private static string Txt(double x, double y, string text, double size, Color c, string anchor) =>
        string.Format(_ci,
            "<text x='{0:F1}' y='{1:F1}' font-size='{2:F1}' fill='{3}' text-anchor='{4}' dominant-baseline='central'>{5}</text>",
            x, y, size, Hex(c), anchor, Escape(text));
}
