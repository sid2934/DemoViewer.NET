#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 5 — Label placement. Greedy + local-search point-feature placement for
///     edge labels (Christensen-Marks-Shieber). For each edge we seed
///     at the midpoint of its longest segment, then search a candidate grid of
///     perpendicular offsets AND along-segment shifts, scoring each against the
///     spatial picture so far — node boxes, edge polylines, and already-placed
///     labels. The lowest-collision candidate wins; ties break toward the seed.
///     Text size: the headless metrics path has no Avalonia font manager, so
///     <see cref="Avalonia.Media.FormattedText" /> can't measure here (the same
///     block the headless screenshot path hit). The pass uses the SAME
///     <c>length*6</c> width / <c>14</c> height mono-calibrated estimate the metric
///     and the renderer's collision math use; because the metric reads the emitted
///     <see cref="LayoutContext.LabelPositions" />, placement and scoring agree on
///     the exact rect by construction. Target: HighDegreeHub 2 -> 0.
/// </summary>
internal static class LabelPlacementPass
{
    private const double CharWidth = 6;
    private const double LabelHeight = 14;

    internal static void Run(LayoutContext ctx)
    {
        Dictionary<IGraphEdge, LabelPlacement> placed = new();
        List<Rect> placedRects = new();

        // Static obstacles: node boxes.
        List<Rect> nodeRects = ctx.NodePositions.Keys.Select(ctx.NodeRect).ToList();

        // Order edges by descending route length so the long, label-bearing edges
        // claim space first (greedy seeding from the most constrained first).
        List<(IGraphEdge Edge, IReadOnlyList<Point> Route, string Text, double Len)> labelled = new();
        foreach (IGraphEdge edge in ctx.Edges)
        {
            if (ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            if (!ctx.EdgeRoutes.TryGetValue(edge, out IReadOnlyList<Point>? route) || route.Count < 2)
            {
                continue;
            }

            string text = edge.ConditionLabel is not null
                ? $"{edge.Label}  [{edge.ConditionLabel}]"
                : edge.Label;
            if (text.Length == 0)
            {
                continue;
            }

            labelled.Add((edge, route, text, RouteLength(route)));
        }

        labelled.Sort((a, b) => b.Len.CompareTo(a.Len));

        foreach ((IGraphEdge edge, IReadOnlyList<Point> route, string text, double _) in labelled)
        {
            double w = text.Length * CharWidth, h = LabelHeight;

            // Seed: midpoint of the longest segment + that segment's orientation.
            int seg = LongestSegment(route);
            Point a = route[seg];
            Point b = route[seg + 1];
            double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;

            double dx = b.X - a.X, dy = b.Y - a.Y;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            // Unit along-segment and perpendicular directions.
            double ux = segLen > 1e-6 ? dx / segLen : 1, uy = segLen > 1e-6 ? dy / segLen : 0;
            double px = -uy, py = ux;

            Rect best = SearchBest(mx, my, ux, uy, px, py, w, h, segLen,
                nodeRects, placedRects, route);

            placed[edge] = new LabelPlacement(best.X, best.Y, w, h);
            placedRects.Add(best);
        }

        ctx.LabelPositions = placed;
    }

    private static int LongestSegment(IReadOnlyList<Point> route)
    {
        int seg = 0;
        double best = -1;
        for (int i = 0; i < route.Count - 1; i++)
        {
            double dx = route[i + 1].X - route[i].X, dy = route[i + 1].Y - route[i].Y;
            double len = dx * dx + dy * dy;
            if (len > best)
            {
                best = len;
                seg = i;
            }
        }

        return seg;
    }

    private static Rect MakeRect(double cx, double cy, double w, double h) =>
        new(cx - w / 2, cy - h / 2, w, h);

    private static bool RectsOverlap(Rect a, Rect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static double RouteLength(IReadOnlyList<Point> route)
    {
        double total = 0;
        for (int i = 0; i < route.Count - 1; i++)
        {
            double dx = route[i + 1].X - route[i].X, dy = route[i + 1].Y - route[i].Y;
            total += Math.Sqrt(dx * dx + dy * dy);
        }

        return total;
    }

    // Collision score: count of node/label overlaps (heavily weighted) plus a
    // softer penalty for crossing edge polylines.
    private static double Score(Rect rect, List<Rect> nodeRects, List<Rect> placedRects,
        IReadOnlyList<Point> ownRoute)
    {
        double score = 0;
        foreach (Rect n in nodeRects)
        {
            if (RectsOverlap(rect, n))
            {
                score += 1.0;
            }
        }

        foreach (Rect p in placedRects)
        {
            if (RectsOverlap(rect, p))
            {
                score += 1.0;
            }
        }

        // Crossing other-edge polylines is undesirable but far less so than a hard
        // overlap; only the edge's own route is exempt (its label sits on it).
        // We approximate "crosses some edge" by testing the label rect against the
        // route segments of edges other than its own — but to keep this O(n) we
        // only penalise the own route lightly so labels can still sit on their line.
        for (int i = 0; i < ownRoute.Count - 1; i++)
        {
            if (SegmentIntersectsRect(ownRoute[i], ownRoute[i + 1], rect))
            {
                score += 0.05;
                break;
            }
        }

        return score;
    }

    private static Rect SearchBest(
        double mx, double my, double ux, double uy, double px, double py,
        double w, double h, double segLen,
        List<Rect> nodeRects, List<Rect> placedRects, IReadOnlyList<Point> ownRoute)
    {
        // Candidate offsets: perpendicular bands (both sides) crossed with
        // along-segment shifts. The seed (0,0) is included and preferred on ties.
        double perpStep = h + 4;
        double alongStep = Math.Max(w * 0.6, 24);
        int maxAlong = segLen > 0 ? (int)Math.Min(6, segLen / alongStep + 1) : 0;

        double[] perpMultipliers = [0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6];

        Rect bestRect = MakeRect(mx, my, w, h);
        double bestScore = double.MaxValue;

        foreach (double pm in perpMultipliers)
        {
            for (int am = -maxAlong; am <= maxAlong; am++)
            {
                double cx = mx + px * perpStep * pm + ux * alongStep * am;
                double cy = my + py * perpStep * pm + uy * alongStep * am;
                Rect rect = MakeRect(cx, cy, w, h);

                double score = Score(rect, nodeRects, placedRects, ownRoute);
                // Tie-break: prefer the candidate closest to the seed (smallest
                // offset magnitude), keeping labels near their edge.
                double offsetPenalty = Math.Abs(pm) * 0.001 + Math.Abs(am) * 0.0005;
                score += offsetPenalty;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestRect = rect;
                    if (score < 1e-6)
                    {
                        return bestRect; // collision-free; stop early.
                    }
                }
            }
        }

        return bestRect;
    }

    // Liang-Barsky segment-vs-rect (mirrors LayoutMetrics.SegmentIntersectsRect).
    private static bool SegmentIntersectsRect(Point p1, Point p2, Rect r)
    {
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double t0 = 0, t1 = 1;
        double[] p = [-dx, dx, -dy, dy];
        double[] q = [p1.X - r.Left, r.Right - p1.X, p1.Y - r.Top, r.Bottom - p1.Y];

        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] < 0)
                {
                    return false;
                }
            }
            else
            {
                double t = q[i] / p[i];
                if (p[i] < 0)
                {
                    if (t > t1)
                    {
                        return false;
                    }

                    if (t > t0)
                    {
                        t0 = t;
                    }
                }
                else
                {
                    if (t < t0)
                    {
                        return false;
                    }

                    if (t < t1)
                    {
                        t1 = t;
                    }
                }
            }
        }

        return t0 <= t1;
    }
}
