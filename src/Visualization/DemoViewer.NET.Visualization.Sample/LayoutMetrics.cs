#region

using System.Diagnostics;
using Avalonia;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization.Sample;

/// <summary>
///     Headless, deterministic geometry metrics computed directly from a
///     <see cref="LayoutResult" /> (no rendering / GUI). These are the objective
///     "good layout" gates from the v2 layout review used to baseline v1 and to
///     judge v2 against. All computation is in logical (pre-zoom) coordinates.
/// </summary>
public sealed record LayoutMetrics(
    int NodeNodeOverlaps,
    int EdgeNodeIntersections,
    int EdgeCrossings,
    double TotalEdgeLength,
    int SharedPortEndpoints,
    int LabelOverlaps,
    int OutOfBoundsPrimitives,
    int SelfLoopOverlaps,
    double AspectRatio,
    double LayoutMilliseconds)
{
    /// <summary>
    ///     Runs the v1 layout pipeline under a stopwatch and computes all metrics.
    /// </summary>
    public static LayoutMetrics Compute(
        IReadOnlyList<IGraphNode> nodes,
        IReadOnlyList<IGraphEdge> edges,
        IReadOnlyList<INodeGroup>? groups,
        IReadOnlyList<INodeTable>? tables,
        GraphStyle style)
    {
        Stopwatch sw = Stopwatch.StartNew();
        LayoutResult layout = LayoutPipeline.ComputeFullLayout(nodes, edges, groups, tables, style);
        sw.Stop();

        return Compute(nodes, edges, tables, style, layout, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Computes metrics over an already-computed layout (timing supplied separately).</summary>
    internal static LayoutMetrics Compute(
        IReadOnlyList<IGraphNode> nodes,
        IReadOnlyList<IGraphEdge> edges,
        IReadOnlyList<INodeTable>? tables,
        GraphStyle style,
        LayoutResult layout,
        double layoutMs)
    {
        Rect RectFor(IGraphNode n)
        {
            double w = n.Style?.Width ?? style.Node.Width;
            double h = n.Style?.Height ?? style.Node.Height;
            NodePosition pos = layout.NodePositions[n];
            return new Rect(pos.X - w / 2, pos.Y - h / 2, w, h);
        }

        List<IGraphNode> placedNodes = nodes.Where(n => layout.NodePositions.ContainsKey(n)).ToList();
        Dictionary<IGraphNode, Rect> rects = placedNodes.ToDictionary(n => n, RectFor);

        return new LayoutMetrics(
            CountNodeNodeOverlaps(placedNodes, rects),
            CountEdgeNodeIntersections(edges, placedNodes, rects, layout),
            CountEdgeCrossings(edges, layout),
            SumEdgeLength(edges, layout),
            CountSharedPortEndpoints(edges, layout),
            CountLabelOverlaps(edges, rects, layout, style),
            CountOutOfBounds(rects, layout),
            CountSelfLoopOverlaps(edges, rects, layout),
            layout.TotalHeight > 0 ? layout.TotalWidth / layout.TotalHeight : 0,
            layoutMs);
    }

    // ── Metric 3: edge crossings (segment-segment intersections, distinct edges) ─
    private static int CountEdgeCrossings(IReadOnlyList<IGraphEdge> edges, LayoutResult layout)
    {
        List<IReadOnlyList<Point>> routed = edges
            .Where(e => !ReferenceEquals(e.Source, e.Destination)
                        && layout.EdgeRoutes.TryGetValue(e, out IReadOnlyList<Point>? r) && r.Count >= 2)
            .Select(e => layout.EdgeRoutes[e])
            .ToList();

        int count = 0;
        for (int a = 0; a < routed.Count; a++)
        {
            for (int b = a + 1; b < routed.Count; b++)
            {
                count += CrossingsBetween(routed[a], routed[b]);
            }
        }

        return count;
    }

    // ── Metric 2: edge-node intersections (excludes an edge's own endpoints) ─
    private static int CountEdgeNodeIntersections(
        IReadOnlyList<IGraphEdge> edges, List<IGraphNode> nodes,
        Dictionary<IGraphNode, Rect> rects, LayoutResult layout)
    {
        int count = 0;
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

            foreach (IGraphNode node in nodes)
            {
                if (ReferenceEquals(node, edge.Source) || ReferenceEquals(node, edge.Destination))
                {
                    continue;
                }

                Rect rect = rects[node];
                bool hit = false;
                for (int i = 0; i < route.Count - 1 && !hit; i++)
                {
                    if (SegmentIntersectsRect(route[i], route[i + 1], rect))
                    {
                        hit = true;
                    }
                }

                if (hit)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ── Metric 6: label overlaps (label-label and label-node) ───────────────
    // Uses the same coarse width estimate the v1 renderer uses (len*6) in logical
    // units; height ~14. Approximate but deterministic and comparable v1↔v2.
    private static int CountLabelOverlaps(
        IReadOnlyList<IGraphEdge> edges, Dictionary<IGraphNode, Rect> rects, LayoutResult layout,
        GraphStyle style)
    {
        List<Rect> labelRects = new();
        foreach (IGraphEdge edge in edges)
        {
            if (ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            // The pipeline emits a collision-resolved label rect for every
            // labelled edge; measure that.
            if (layout.LabelPositions.TryGetValue(edge, out LabelPlacement? placed))
            {
                labelRects.Add(placed.ToRect());
            }
        }

        int overlaps = 0;
        for (int i = 0; i < labelRects.Count; i++)
        {
            for (int j = i + 1; j < labelRects.Count; j++)
            {
                if (RectsOverlap(labelRects[i], labelRects[j]))
                {
                    overlaps++;
                }
            }

            foreach (Rect node in rects.Values)
            {
                if (RectsOverlap(labelRects[i], node))
                {
                    overlaps++;
                }
            }
        }

        return overlaps;
    }

    // ── Metric 1: node-node overlaps ────────────────────────────────────────
    private static int CountNodeNodeOverlaps(
        List<IGraphNode> nodes, Dictionary<IGraphNode, Rect> rects)
    {
        int count = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                if (RectsOverlap(rects[nodes[i]], rects[nodes[j]]))
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ── Metric 7: bbox containment ──────────────────────────────────────────
    private static int CountOutOfBounds(Dictionary<IGraphNode, Rect> rects, LayoutResult layout)
    {
        double w = layout.TotalWidth, h = layout.TotalHeight;

        bool Inside(double x, double y)
        {
            return x >= -0.5 && y >= -0.5 && x <= w + 0.5 && y <= h + 0.5;
        }

        int outside = 0;
        foreach (Rect r in rects.Values)
        {
            if (!Inside(r.Left, r.Top) || !Inside(r.Right, r.Bottom))
            {
                outside++;
            }
        }

        foreach (IReadOnlyList<Point> route in layout.EdgeRoutes.Values)
        {
            foreach (Point p in route)
            {
                if (!Inside(p.X, p.Y))
                {
                    outside++;
                }
            }
        }

        foreach (IReadOnlyList<Point> route in layout.SelfLoopRoutes.Values)
        {
            foreach (Point p in route)
            {
                if (!Inside(p.X, p.Y))
                {
                    outside++;
                }
            }
        }

        foreach (GroupBounds g in layout.Groups)
        {
            if (!Inside(g.X, g.Y) || !Inside(g.X + g.Width, g.Y + g.Height))
            {
                outside++;
            }
        }

        foreach (TableLayoutWithEdges t in layout.Tables)
        {
            TableLayout tl = t.Layout;
            if (!Inside(tl.X, tl.Y) || !Inside(tl.X + tl.Width, tl.Y + tl.Height))
            {
                outside++;
            }

            if (t.ColumnEdgeRoutes is not null)
            {
                foreach (IReadOnlyList<Point> route in t.ColumnEdgeRoutes)
                {
                    foreach (Point p in route)
                    {
                        if (!Inside(p.X, p.Y))
                        {
                            outside++;
                        }
                    }
                }
            }
        }

        foreach (LabelPlacement l in layout.LabelPositions.Values)
        {
            if (!Inside(l.X, l.Y) || !Inside(l.X + l.Width, l.Y + l.Height))
            {
                outside++;
            }
        }

        return outside;
    }

    // ── Metric: self-loop overlaps (loop bbox vs another-source loop bbox / non-source node) ─
    private static int CountSelfLoopOverlaps(
        IReadOnlyList<IGraphEdge> edges,
        Dictionary<IGraphNode, Rect> rects,
        LayoutResult layout)
    {
        List<(IGraphNode Source, Rect Bbox)> loops = new();
        foreach (IGraphEdge edge in edges)
        {
            if (!ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            if (!layout.SelfLoopRoutes.TryGetValue(edge, out IReadOnlyList<Point>? route))
            {
                continue;
            }

            if (route.Count == 0)
            {
                continue;
            }

            double minX = route[0].X, maxX = route[0].X, minY = route[0].Y, maxY = route[0].Y;
            for (int i = 1; i < route.Count; i++)
            {
                Point p = route[i];
                if (p.X < minX)
                {
                    minX = p.X;
                }

                if (p.X > maxX)
                {
                    maxX = p.X;
                }

                if (p.Y < minY)
                {
                    minY = p.Y;
                }

                if (p.Y > maxY)
                {
                    maxY = p.Y;
                }
            }

            loops.Add((edge.Source, new Rect(minX, minY, maxX - minX, maxY - minY)));
        }

        int count = 0;
        // Loop vs loop on a DIFFERENT source (same-source stacking is expected to share bbox space).
        for (int i = 0; i < loops.Count; i++)
        {
            for (int j = i + 1; j < loops.Count; j++)
            {
                if (!ReferenceEquals(loops[i].Source, loops[j].Source)
                    && RectsOverlap(loops[i].Bbox, loops[j].Bbox))
                {
                    count++;
                }
            }
        }

        // Loop vs any non-source node rect (loop bleeds into a neighbour box).
        foreach ((IGraphNode src, Rect bbox) in loops)
        {
            foreach ((IGraphNode n, Rect r) in rects)
            {
                if (!ReferenceEquals(n, src) && RectsOverlap(bbox, r))
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ── Metric 5: shared port endpoints ─────────────────────────────────────
    // On a degree>1 node, count how many incident edges share the same (rounded)
    // anchor point. v1 anchors every edge at the node centre, so all incident
    // edges collide → a high count. v2's port assignment should drive this to 0.
    private static int CountSharedPortEndpoints(IReadOnlyList<IGraphEdge> edges, LayoutResult layout)
    {
        // node -> list of endpoint anchors that touch it.
        Dictionary<IGraphNode, List<(int X, int Y)>> anchors = new();
        Dictionary<IGraphNode, int> degree = new();

        void Bump(IGraphNode n)
        {
            degree[n] = degree.GetValueOrDefault(n) + 1;
        }

        void Add(IGraphNode n, Point p)
        {
            if (!anchors.TryGetValue(n, out List<(int X, int Y)>? list))
            {
                anchors[n] = list = [];
            }

            list.Add(((int)Math.Round(p.X), (int)Math.Round(p.Y)));
        }

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

            Bump(edge.Source);
            Bump(edge.Destination);
            Add(edge.Source, route[0]);
            Add(edge.Destination, route[^1]);
        }

        int shared = 0;
        foreach ((IGraphNode node, List<(int X, int Y)> list) in anchors)
        {
            if (degree.GetValueOrDefault(node) < 2)
            {
                continue;
            }

            // Count anchors beyond the first at each coincident position.
            foreach (IGrouping<(int X, int Y), (int X, int Y)> grp in list.GroupBy(a => a))
            {
                if (grp.Count() > 1)
                {
                    shared += grp.Count() - 1;
                }
            }
        }

        return shared;
    }

    private static double Cross(Point o, Point a, Point b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static int CrossingsBetween(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
    {
        int count = 0;
        for (int i = 0; i < a.Count - 1; i++)
        {
            for (int j = 0; j < b.Count - 1; j++)
            {
                if (ProperlyIntersect(a[i], a[i + 1], b[j], b[j + 1]))
                {
                    count++;
                }
            }
        }

        return count;
    }

    // True only for a proper (interior) crossing — shared endpoints (e.g. two
    // edges meeting at a node) do NOT count as a crossing.
    private static bool ProperlyIntersect(Point a, Point b, Point c, Point d)
    {
        double d1 = Cross(c, d, a);
        double d2 = Cross(c, d, b);
        double d3 = Cross(a, b, c);
        double d4 = Cross(a, b, d);

        if ((d1 > 0 && d2 < 0 || d1 < 0 && d2 > 0) &&
            (d3 > 0 && d4 < 0 || d3 < 0 && d4 > 0))
        {
            return true;
        }

        return false;
    }

    // ── Geometry helpers ────────────────────────────────────────────────────
    private static bool RectsOverlap(Rect a, Rect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    // True if any part of the segment lies inside the rect. Uses Liang-Barsky
    // clipping, which handles interior endpoints, edge crossings, and segments
    // running collinear-along a rect edge uniformly (the last case matters for
    // v2's orthogonal routing, where a route may hug a node boundary).
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
                    return false; // parallel and outside this boundary
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

    // ── Metric 4: total edge length ─────────────────────────────────────────
    private static double SumEdgeLength(IReadOnlyList<IGraphEdge> edges, LayoutResult layout)
    {
        double total = 0;
        foreach (IGraphEdge edge in edges)
        {
            if (ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            if (!layout.EdgeRoutes.TryGetValue(edge, out IReadOnlyList<Point>? route))
            {
                continue;
            }

            for (int i = 0; i < route.Count - 1; i++)
            {
                double dx = route[i + 1].X - route[i].X;
                double dy = route[i + 1].Y - route[i].Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
        }

        return total;
    }
}
