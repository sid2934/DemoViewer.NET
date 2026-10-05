namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>
///     Flat-polygon helpers shared by the areas, the overlay and the outline builder. Polygons are
///     <c>x0, y0, x1, y1, ...</c> in world XY; every routine tolerates a degenerate input by answering
///     "outside" or zero rather than throwing, because a malformed overlay entry is a diagnostic, not a
///     crash.
/// </summary>
internal static class ZoneGeometry
{
    /// <summary>Absolute shoelace area.</summary>
    public static double PolygonArea(IReadOnlyList<double> xy)
    {
        int n = xy.Count / 2;
        if (n < 3)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            sum += xy[2 * i] * xy[2 * j + 1] - xy[2 * j] * xy[2 * i + 1];
        }

        return Math.Abs(sum) / 2;
    }

    /// <summary>Signed shoelace area: positive for a counter-clockwise polygon in world XY (Y up).</summary>
    public static double SignedArea(IReadOnlyList<double> xy)
    {
        int n = xy.Count / 2;
        if (n < 3)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            sum += xy[2 * i] * xy[2 * j + 1] - xy[2 * j] * xy[2 * i + 1];
        }

        return sum / 2;
    }

    /// <summary>Even-odd point in polygon. A point on an edge counts as inside.</summary>
    public static bool PointInPolygon(IReadOnlyList<double> xy, double x, double y)
    {
        int n = xy.Count / 2;
        if (n < 3)
        {
            return false;
        }

        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = xy[2 * i], yi = xy[2 * i + 1];
            double xj = xy[2 * j], yj = xy[2 * j + 1];

            if (DistanceToSegment(xi, yi, xj, yj, x, y) <= 1e-6)
            {
                return true;
            }

            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The nearest distance from a point to any polygon edge.</summary>
    public static double DistanceToPolygonEdges(IReadOnlyList<double> xy, double x, double y)
    {
        int n = xy.Count / 2;
        if (n == 0)
        {
            return double.PositiveInfinity;
        }

        if (n == 1)
        {
            return Math.Sqrt((x - xy[0]) * (x - xy[0]) + (y - xy[1]) * (y - xy[1]));
        }

        double best = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double d = DistanceToSegment(xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1], x, y);
            if (d < best)
            {
                best = d;
            }
        }

        return best;
    }

    /// <summary>Distance from <c>(px, py)</c> to the segment <c>(ax, ay)-(bx, by)</c>.</summary>
    public static double DistanceToSegment(double ax, double ay, double bx, double by, double px, double py)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double len2 = dx * dx + dy * dy;
        double t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double cx = ax + t * dx - px;
        double cy = ay + t * dy - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>
    ///     Whether a polygon is simple: at least three distinct corners, no repeated consecutive
    ///     corner, and no two non-adjacent edges crossing. O(n²), which is nothing for a hand-written
    ///     polygon.
    /// </summary>
    public static bool IsSimple(IReadOnlyList<double> xy)
    {
        int n = xy.Count / 2;
        if (n < 3 || xy.Count % 2 != 0)
        {
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (Math.Abs(xy[2 * i] - xy[2 * j]) < 1e-9 && Math.Abs(xy[2 * i + 1] - xy[2 * j + 1]) < 1e-9)
            {
                return false;
            }
        }

        if (PolygonArea(xy) <= 1e-9)
        {
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            int i2 = (i + 1) % n;
            for (int k = i + 1; k < n; k++)
            {
                int k2 = (k + 1) % n;
                if (k == i2 || k2 == i)
                {
                    continue; // adjacent edges share a corner by construction
                }

                if (SegmentsCross(xy[2 * i], xy[2 * i + 1], xy[2 * i2], xy[2 * i2 + 1],
                        xy[2 * k], xy[2 * k + 1], xy[2 * k2], xy[2 * k2 + 1]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The area-weighted centroid of a set of polygons, or the mean of the inputs when the area is zero.</summary>
    public static (double X, double Y) WeightedCentroid(IEnumerable<(double X, double Y, double Weight)> points)
    {
        double sumW = 0, sumX = 0, sumY = 0;
        double meanX = 0, meanY = 0;
        int count = 0;
        foreach ((double x, double y, double w) in points)
        {
            sumW += w;
            sumX += x * w;
            sumY += y * w;
            meanX += x;
            meanY += y;
            count++;
        }

        if (count == 0)
        {
            return (0, 0);
        }

        return sumW > 0 ? (sumX / sumW, sumY / sumW) : (meanX / count, meanY / count);
    }

    private static bool SegmentsCross(double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double d1 = Cross(cx, cy, dx, dy, ax, ay);
        double d2 = Cross(cx, cy, dx, dy, bx, by);
        double d3 = Cross(ax, ay, bx, by, cx, cy);
        double d4 = Cross(ax, ay, bx, by, dx, dy);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static double Cross(double ax, double ay, double bx, double by, double px, double py) =>
        (bx - ax) * (py - ay) - (by - ay) * (px - ax);
}
