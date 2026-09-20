#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 2, the port fan. Spreads edge endpoints that MSAGL landed on the same point of the same
///     node, so parallel edges stop being drawn on top of each other.
///     <para>
///         MSAGL routes an edge between two node SHAPES, not between ports, so two edges with the
///         same endpoints get the same curve. The shipped Analysis graph has 88 such duplicates on
///         one player: the root writes to one lifecycle node fourteen times on fourteen different
///         events, and all fourteen arrows, arrowheads and labels land on one another. The
///         <c>SharedPorts</c> gate counts exactly this, and it read 60 on the captured fixture.
///     </para>
///     <para>
///         Only a coincident anchor moves. An anchor MSAGL already placed on its own is left alone,
///         because the high-degree nodes in this corpus are already spread and re-slotting them
///         would trade a solved problem for a new one.
///     </para>
///     <para>
///         A displaced anchor walks the node's whole PERIMETER rather than the one face it started
///         on. A face does not always have the room: the busiest node in the shipped graph takes 134
///         incident edges and its 56-unit side offers 56 whole-unit slots. The route stays
///         rectilinear either way, because the elbow behind a moved anchor is inserted on the axis
///         the new face is entered from.
///     </para>
/// </summary>
internal static class EdgePortFanPass
{
    // Preferred spacing between fanned anchors, in logical units. Wide enough that two arrowheads
    // read as two at a normal zoom, narrow enough that fourteen of them still fit a 180-unit face.
    private const double PreferredStep = 8;

    // Fallback spacings when the preferred one finds no free slot on the current ring.
    private static readonly double[] _stepLadder = [PreferredStep, 4, MinGap];

    // Two anchors closer than this on BOTH axes count as the same point. A distance rather than a
    // shared integer cell on purpose: the containment pass translates the whole layout by a
    // fractional amount afterwards, so which pairs share a rounded cell is not decided yet when this
    // runs. Separating by a real distance is invariant under that shift, and 1.5 is the smallest
    // separation that survives the gate's rounding, including its banker's-rounding ties.
    private const double MinGap = 1.5;

    // How far out each successive ring sits, and how many there are. A node face holds
    // width/MinGap anchors; past that the overflow steps off the box rather than walking round to a
    // face that points the wrong way, because a detour around the node costs far more crossings than
    // a line that starts a few units short of it. The busiest node in the shipped graph takes 195
    // outgoing edges against a 180-unit face, so two rings of headroom is what this is sized for.
    private const double RingStep = 3;
    private const int Rings = 6;

    internal static void Run(LayoutContext ctx)
    {
        // (edge, isSourceEnd) -> where its anchor moves to, and which face it lands on.
        Dictionary<(IGraphEdge Edge, bool Source), Port> ports = new();

        Dictionary<IGraphNode, List<Endpoint>> incident = new(ReferenceEqualityComparer.Instance);

        // Ties are broken on declaration order so the layout is a pure function of the input.
        Dictionary<IGraphEdge, int> order = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < ctx.Edges.Count; i++)
        {
            order[ctx.Edges[i]] = i;
        }

        void Note(IGraphNode node, IGraphEdge edge, bool source, Point anchor)
        {
            if (!ctx.NodePositions.ContainsKey(node))
            {
                return;
            }

            if (!incident.TryGetValue(node, out List<Endpoint>? list))
            {
                incident[node] = list = [];
            }

            list.Add(new Endpoint(edge, source, anchor, order[edge]));
        }

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

            Note(edge.Source, edge, true, route[0]);
            Note(edge.Destination, edge, false, route[^1]);
        }

        foreach ((IGraphNode node, List<Endpoint> ends) in incident)
        {
            if (ends.Count < 2)
            {
                continue;
            }

            FanNode(ctx.NodeRect(node), ends, ports);
        }

        if (ports.Count == 0)
        {
            return;
        }

        Dictionary<IGraphEdge, IReadOnlyList<Point>> rerouted = new(ctx.EdgeRoutes.Count);
        foreach ((IGraphEdge edge, IReadOnlyList<Point> route) in ctx.EdgeRoutes)
        {
            List<Point> points = [.. route];
            if (ports.TryGetValue((edge, true), out Port head))
            {
                Slide(points, head, true);
            }

            if (ports.TryGetValue((edge, false), out Port tail))
            {
                Slide(points, tail, false);
            }

            rerouted[edge] = points;
        }

        ctx.EdgeRoutes = rerouted;
    }

    private static void FanNode(
        Rect rect, List<Endpoint> ends, Dictionary<(IGraphEdge, bool), Port> ports)
    {
        // Every anchor this node has already claimed. A displaced one has to clear all of them, not
        // just the ones it piled up with.
        List<Point> occupied = new(ends.Count);

        // Declaration order decides who keeps their place, so the layout is a pure function of the
        // input rather than of dictionary iteration order.
        foreach (Endpoint e in ends.OrderBy(e => e.Order).ThenBy(e => e.Source))
        {
            if (IsFree(e.Anchor, occupied))
            {
                occupied.Add(e.Anchor);
                continue;
            }

            if (TryPlace(rect, e.Anchor, occupied, out Port port))
            {
                ports[(e.Edge, e.Source)] = port;
                occupied.Add(port.Anchor);
            }
        }
    }

    private static bool IsFree(Point candidate, List<Point> occupied)
    {
        foreach (Point p in occupied)
        {
            if (Math.Abs(p.X - candidate.X) < MinGap && Math.Abs(p.Y - candidate.Y) < MinGap)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryPlace(Rect rect, Point anchor, List<Point> occupied, out Port port)
    {
        double perimeter = 2 * (rect.Width + rect.Height);
        double origin = Parameterise(anchor, rect);

        for (int ring = 0; ring < Rings; ring++)
        {
            foreach (double step in _stepLadder)
            {
                int span = (int)(perimeter / step);
                for (int i = ring == 0 ? 1 : 0; i <= span; i++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        Point moved = Locate(origin + sign * i * step, rect, out bool horizontal);
                        moved = Outset(moved, rect, ring * RingStep, horizontal);
                        if (IsFree(moved, occupied))
                        {
                            port = new Port(moved, horizontal);
                            return true;
                        }

                        if (i == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        port = default;
        return false;
    }

    // Pushes a boundary point off the node along the face normal. The edge then starts (or ends) a
    // few units short of the box, which at the zooms this graph is read at is the width of the node
    // border.
    private static Point Outset(Point p, Rect rect, double distance, bool horizontalFace)
    {
        if (distance <= 0)
        {
            return p;
        }

        if (horizontalFace)
        {
            return new Point(p.X, p.Y <= rect.Top + rect.Height / 2 ? p.Y - distance : p.Y + distance);
        }

        return new Point(p.X <= rect.Left + rect.Width / 2 ? p.X - distance : p.X + distance, p.Y);
    }

    // Boundary point to distance travelled clockwise from the top-left corner.
    private static double Parameterise(Point p, Rect rect)
    {
        double w = rect.Width, h = rect.Height;
        double toTop = Math.Abs(p.Y - rect.Top), toBottom = Math.Abs(p.Y - rect.Bottom);
        double toLeft = Math.Abs(p.X - rect.Left), toRight = Math.Abs(p.X - rect.Right);
        double nearest = Math.Min(Math.Min(toTop, toBottom), Math.Min(toLeft, toRight));

        if (nearest == toTop)
        {
            return Math.Clamp(p.X - rect.Left, 0, w);
        }

        if (nearest == toRight)
        {
            return w + Math.Clamp(p.Y - rect.Top, 0, h);
        }

        if (nearest == toBottom)
        {
            return w + h + Math.Clamp(rect.Right - p.X, 0, w);
        }

        return 2 * w + h + Math.Clamp(rect.Bottom - p.Y, 0, h);
    }

    // The inverse, plus which kind of face the result landed on: a horizontal face is entered
    // vertically and a vertical face horizontally, which is what keeps the re-elbowed route
    // axis-aligned.
    private static Point Locate(double t, Rect rect, out bool horizontalFace)
    {
        double w = rect.Width, h = rect.Height;
        double perimeter = 2 * (w + h);
        t -= Math.Floor(t / perimeter) * perimeter;

        if (t < w)
        {
            horizontalFace = true;
            return new Point(rect.Left + t, rect.Top);
        }

        if (t < w + h)
        {
            horizontalFace = false;
            return new Point(rect.Right, rect.Top + (t - w));
        }

        if (t < 2 * w + h)
        {
            horizontalFace = true;
            return new Point(rect.Right - (t - w - h), rect.Bottom);
        }

        horizontalFace = false;
        return new Point(rect.Left, rect.Bottom - (t - 2 * w - h));
    }

    // Moves one end of the route to its new port and re-elbows the segment behind it so the polyline
    // stays axis-aligned. With only two points there is no interior vertex to bend, so a pair of
    // waypoints at the midpoint of the run take that job; bending at the neighbour instead would put
    // the turn flush against the far node's face.
    private static void Slide(List<Point> points, Port port, bool head)
    {
        int end = head ? 0 : points.Count - 1;
        int next = head ? 1 : points.Count - 2;
        Point moved = port.Anchor;

        if (points.Count == 2)
        {
            Point other = points[next];
            double midX = (moved.X + other.X) / 2, midY = (moved.Y + other.Y) / 2;
            Point a = port.HorizontalFace ? new Point(moved.X, midY) : new Point(midX, moved.Y);
            Point b = port.HorizontalFace ? new Point(other.X, midY) : new Point(midX, other.Y);
            points.Clear();
            points.AddRange(head ? [moved, a, b, other] : new[] { other, b, a, moved });
            return;
        }

        Point neighbour = points[next];
        Point elbow = port.HorizontalFace
            ? new Point(moved.X, neighbour.Y)
            : new Point(neighbour.X, moved.Y);

        points[end] = moved;
        points.Insert(head ? 1 : points.Count - 1, elbow);
    }

    private readonly record struct Endpoint(IGraphEdge Edge, bool Source, Point Anchor, int Order);

    private readonly record struct Port(Point Anchor, bool HorizontalFace);
}
