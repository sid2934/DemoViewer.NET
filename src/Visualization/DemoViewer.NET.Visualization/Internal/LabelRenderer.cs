#region

using System.Globalization;
using Avalonia;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

internal static class LabelRenderer
{
    private static readonly Typeface _monoTypeface =
        new(FontFamily.Parse("Consolas,Menlo,monospace"));

    internal static void DrawLabel(DrawingContext dc, string text,
        double cx, double cy, double scale, GraphStyle style,
        double angleDegrees = 0)
    {
        if (text.Length == 0)
        {
            return;
        }

        double sz = Math.Max(style.Edge.LabelFontSize * scale, 1);
        IBrush fg = new SolidColorBrush(style.Edge.LabelForeground);
        FormattedText ft = new(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, sz, fg);

        double pad = 3 * scale;
        SolidColorBrush bgBrush = new(style.LabelBackground);

        if (Math.Abs(angleDegrees) < 0.1)
        {
            dc.DrawRectangle(bgBrush, null,
                new Rect(cx - ft.Width / 2 - pad, cy - ft.Height / 2 - pad,
                    ft.Width + 2 * pad, ft.Height + 2 * pad),
                2 * scale, 2 * scale);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }
        else
        {
            double rad = angleDegrees * Math.PI / 180;
            Matrix matrix = Matrix.CreateTranslation(-cx, -cy)
                            * Matrix.CreateRotation(rad)
                            * Matrix.CreateTranslation(cx, cy);
            using (dc.PushTransform(matrix))
            {
                dc.DrawRectangle(bgBrush, null,
                    new Rect(cx - ft.Width / 2 - pad, cy - ft.Height / 2 - pad,
                        ft.Width + 2 * pad, ft.Height + 2 * pad),
                    2 * scale, 2 * scale);
                dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
            }
        }
    }

    internal static Typeface GetTypeface() => _monoTypeface;
}
