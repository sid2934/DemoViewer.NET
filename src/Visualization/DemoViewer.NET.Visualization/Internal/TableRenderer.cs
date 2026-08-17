#region

using System.Globalization;
using Avalonia;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

internal static class TableRenderer
{
    private static Typeface Mono => LabelRenderer.GetTypeface();

    internal static void DrawTable(DrawingContext dc,
        INodeTable table, TableLayoutWithEdges tableEntry, GraphStyle style,
        double scale, Func<double, double, Point> toScreen)
    {
        TableLayout tl = tableEntry.Layout;
        TableStyleConfig ts = style.Table;

        Point origin = toScreen(tl.X, tl.Y);
        double sw = ts.CellWidth * scale;
        double sh = ts.CellHeight * scale;
        double shh = ts.HeaderHeight * scale;
        double srw = ts.RowHeaderWidth * scale;
        double stw = tl.Width * scale;
        double sth = tl.Height * scale;

        int cols = table.ColumnNames.Count;
        int rows = table.Rows.Count;

        SolidColorBrush tableBg = new(ts.Background);
        Pen gridPen = new(new SolidColorBrush(ts.GridLine), Math.Max(1, scale * 0.5));
        SolidColorBrush headerBg = new(ts.HeaderBackground);
        SolidColorBrush activeCellBg = new(ts.ActiveCellBackground);
        SolidColorBrush headerFg = new(ts.HeaderForeground);
        SolidColorBrush cellFg = new(ts.CellForeground);
        SolidColorBrush dimFg = new(ts.DimForeground);
        Pen annotPen = new(dimFg, Math.Max(0.5, scale * 0.5));

        dc.DrawRectangle(tableBg, gridPen,
            new Rect(origin.X, origin.Y, stw, sth), 4 * scale, 4 * scale);

        dc.DrawRectangle(headerBg, null,
            new Rect(origin.X + srw, origin.Y, stw - srw, shh));

        double fontSize = Math.Max(9 * scale, 1);
        for (int c = 0; c < cols; c++)
        {
            double cx = origin.X + srw + c * sw + sw / 2;
            FormattedText ft = new(table.ColumnNames[c], CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, fontSize, headerFg);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, origin.Y + (shh - ft.Height) / 2));
            dc.DrawLine(gridPen,
                new Point(origin.X + srw + c * sw, origin.Y),
                new Point(origin.X + srw + c * sw, origin.Y + sth));
        }

        for (int r = 0; r < rows; r++)
        {
            ITableRow row = table.Rows[r];
            double ry = origin.Y + shh + r * sh;

            dc.DrawLine(gridPen,
                new Point(origin.X, ry), new Point(origin.X + stw, ry));

            FormattedText nameFt = new(row.Label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, fontSize, headerFg);
            dc.DrawText(nameFt,
                new Point(origin.X + 8 * scale, ry + (sh - nameFt.Height) / 2));

            if (row.FilterAnnotation is { Length: > 0 })
            {
                double annotFontSz = Math.Max(7.5 * scale, 1);
                FormattedText annotFt = new(row.FilterAnnotation, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Mono, annotFontSz, dimFg);
                double lineEndX = origin.X - 4 * scale;
                double lineStartX = lineEndX - 20 * scale;
                double lineY = ry + sh / 2;
                dc.DrawLine(annotPen, new Point(lineStartX, lineY), new Point(lineEndX, lineY));
                dc.DrawText(annotFt, new Point(
                    lineStartX - annotFt.Width - 4 * scale,
                    lineY - annotFt.Height / 2));
            }

            for (int c = 0; c < row.Cells.Count && c < cols; c++)
            {
                ITableCell cell = row.Cells[c];
                double cx = origin.X + srw + c * sw;

                if (cell.IsActive)
                {
                    dc.DrawRectangle(activeCellBg, null, new Rect(cx, ry, sw, sh));
                }

                string text = cell.DisplayValue ?? (cell.IsActive ? "ACTIVE" : "-");
                SolidColorBrush fg = cell.IsActive ? cellFg : dimFg;
                FormattedText cellFt = new(text, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Mono, fontSize, fg);
                dc.DrawText(cellFt,
                    new Point(cx + (sw - cellFt.Width) / 2, ry + (sh - cellFt.Height) / 2));
            }
        }
    }
}
