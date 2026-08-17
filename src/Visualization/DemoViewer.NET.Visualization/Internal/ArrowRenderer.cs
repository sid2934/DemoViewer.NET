#region

using Avalonia;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

internal static class ArrowRenderer
{
    internal static void Draw(DrawingContext dc, IBrush brush, Point tip, Point from, double size)
    {
        double dirX = tip.X - from.X;
        double dirY = tip.Y - from.Y;
        double len = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (len < 1e-10)
        {
            return;
        }

        dirX /= len;
        dirY /= len;

        double perpX = -dirY;
        double perpY = dirX;
        double wing = size * 0.45;

        Point left = new(tip.X - dirX * size + perpX * wing, tip.Y - dirY * size + perpY * wing);
        Point right = new(tip.X - dirX * size - perpX * wing, tip.Y - dirY * size - perpY * wing);

        StreamGeometry geom = new();
        using (StreamGeometryContext sg = geom.Open())
        {
            sg.BeginFigure(tip, true);
            sg.LineTo(left);
            sg.LineTo(right);
            sg.EndFigure(true);
        }

        dc.DrawGeometry(brush, null, geom);
    }
}
