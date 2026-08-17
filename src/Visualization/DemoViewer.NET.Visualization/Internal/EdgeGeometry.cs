#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pure geometry helpers for edge hit-testing and the breakpoint-marker midpoint, all in the
///     layout's logical coordinate space. Extracted so the point-to-polyline math (the easy place for
///     an off-by-one) is unit-testable independent of the Avalonia control.
/// </summary>
internal static class EdgeGeometry
{
    /// <summary>
    ///     Minimum squared distance from <paramref name="px" />,<paramref name="py" /> to the polyline
    ///     through <paramref name="pts" /> (each consecutive pair is a segment). Returns
    ///     <see cref="double.PositiveInfinity" /> for a degenerate (&lt; 2-point) polyline.
    /// </summary>
    internal static double MinDistanceSquaredToPolyline(double px, double py, IReadOnlyList<Point> pts)
    {
        if (pts.Count < 2)
        {
            return double.PositiveInfinity;
        }

        double best = double.PositiveInfinity;
        for (int i = 1; i < pts.Count; i++)
        {
            double d = PointToSegmentDistanceSquared(px, py, pts[i - 1], pts[i]);
            if (d < best)
            {
                best = d;
            }
        }

        return best;
    }

    /// <summary>Squared distance from a point to a single segment a→b, clamped to the segment.</summary>
    internal static double PointToSegmentDistanceSquared(double px, double py, Point a, Point b)
    {
        double abx = b.X - a.X;
        double aby = b.Y - a.Y;
        double apx = px - a.X;
        double apy = py - a.Y;

        double lenSq = abx * abx + aby * aby;
        // Degenerate segment (a == b) → distance to the point.
        double t = lenSq <= double.Epsilon ? 0 : (apx * abx + apy * aby) / lenSq;
        t = Math.Clamp(t, 0, 1);

        double dx = px - (a.X + t * abx);
        double dy = py - (a.Y + t * aby);
        return dx * dx + dy * dy;
    }

    /// <summary>
    ///     The point at half the polyline's total arc length — a stable midpoint for the edge's
    ///     breakpoint marker even when the route bends. Falls back to the first point for a degenerate
    ///     polyline.
    /// </summary>
    internal static Point PolylineMidpoint(IReadOnlyList<Point> pts)
    {
        if (pts.Count == 0)
        {
            return new Point(0, 0);
        }

        if (pts.Count == 1)
        {
            return pts[0];
        }

        double total = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            total += Distance(pts[i - 1], pts[i]);
        }

        double half = total / 2;
        double acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double seg = Distance(pts[i - 1], pts[i]);
            if (acc + seg >= half)
            {
                double t = seg <= double.Epsilon ? 0 : (half - acc) / seg;
                return new Point(
                    pts[i - 1].X + t * (pts[i].X - pts[i - 1].X),
                    pts[i - 1].Y + t * (pts[i].Y - pts[i - 1].Y));
            }

            acc += seg;
        }

        return pts[^1];
    }

    /// <summary>A point on the cubic Bézier defined by 4 control points, at parameter <paramref name="t" />.</summary>
    internal static Point CubicBezierPoint(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1 - t;
        double a = u * u * u;
        double b = 3 * u * u * t;
        double c = 3 * u * t * t;
        double d = t * t * t;
        return new Point(
            a * p0.X + b * p1.X + c * p2.X + d * p3.X,
            a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    /// <summary>
    ///     Samples a 4-control-point cubic Bézier (the self-loop route shape) into a short polyline so
    ///     the segment-distance hit-test approximates the actual curve, not the control hull. Returns
    ///     the route unchanged if it isn't a 4-point Bézier.
    /// </summary>
    internal static IReadOnlyList<Point> SampleBezierRoute(IReadOnlyList<Point> route, int samples = 10)
    {
        if (route.Count != 4 || samples < 2)
        {
            return route;
        }

        List<Point> pts = new(samples);
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / (samples - 1);
            pts.Add(CubicBezierPoint(route[0], route[1], route[2], route[3], t));
        }

        return pts;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
