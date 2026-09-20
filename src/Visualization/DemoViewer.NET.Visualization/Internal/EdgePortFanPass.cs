#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 2, the port fan. Spreads edge endpoints that MSAGL landed on the same point of the same
///     node, so parallel edges stop being drawn on top of each other.
///     <para>
///         MSAGL routes between node SHAPES, not between ports, so two edges with the same endpoints
///         get the same curve. The shipped Analysis graph has 88 such duplicates on one player: the
///         root writes one lifecycle node fourteen times on fourteen different events, and all
///         fourteen lines, arrowheads and labels land on each other. The <c>SharedPorts</c> gate
///         counts exactly this, and it read 60 on the captured fixture.
///     </para>
///     <para>
///         Only a crowded anchor moves. One MSAGL already placed clear of its neighbours is left
///         alone, because the high-degree nodes in this corpus are already spread and re-slotting
///         them would trade a solved problem for a new one.
///     </para>
///     <para>
///         A displaced anchor slides along the face its edge already leaves from, and steps onto a
///         further-out ring when that face is full. It stays on that face deliberately. An earlier
///         cut walked the whole perimeter, and then 67 of the 143 anchors it moved on the shipped
///         graph left from a face whose route had to cross the node box to reach the rest of itself.
///         No gate can see that: <c>EdgeNodeIntersections</c> excludes an edge's own endpoints.
///     </para>
///     <para>
///         The rebuilt route stays axis-aligned and stays clear of the box. A moved end turns twice,
///         once a <see cref="Clearance" /> off the face and once back onto the old route, so the
///         detour sits in the routing channel the edge was already using.
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

    // How far out each successive ring sits, and how many there are. A face holds faceLength/MinGap
    // anchors per ring, so a 180-unit face over six rings is 720 slots, far more headroom than the
    // busiest node in this corpus needs.
    private const double RingStep = 3;
    private const int Rings = 6;

    // Keeps the outermost anchor inside its face rather than on a corner.
    private const double FaceMargin = 4;

    // How far off the face a moved end turns. Matches the default EdgeRoutingPadding, which is where
    // MSAGL already puts the first bend, and it doubles as a floor on the length of the segment
    // carrying the arrowhead, because ArrowRenderer draws nothing for a zero-length one.
    private const double Clearance = 12;

    internal static void Run(LayoutContext ctx)
    {
        // (edge, isSourceEnd) -> where its anchor moves to and which face it lands on. The default
        // comparer, matching ctx.EdgeRoutes, so a lookup here resolves for exactly the edge
        // instances that dictionary resolves for.
        Dictionary<(IGraphEdge Edge, bool Source), Port> ports = new();

        Dictionary<IGraphNode, List<Endpoint>> incident = new(ReferenceEqualityComparer.Instance);

        // Ties are broken on declaration order so the layout is a pure function of the input.
        Dictionary<IGraphEdge, int> order = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < ctx.Edges.Count; i++)
        {
            order[ctx.Edges[i]] = i;
        }

        void Note(IGraphNode node, IGraphEdge edge, bool source, IReadOnlyList<Point> route)
        {
            if (!ctx.NodePositions.ContainsKey(node))
            {
                return;
            }

            if (!incident.TryGetValue(node, out List<Endpoint>? list))
            {
                incident[node] = list = [];
            }

            Point anchor = source ? route[0] : route[^1];
            Point neighbour = source ? route[1] : route[^2];
            // Which face this end leaves from is decided by the route, not by which side of the rect
            // the anchor is nearest. An anchor on a corner is nearest two of them, and picking the
            // one the route does not travel puts the fan on the wrong axis.
            bool horizontalFace = Math.Abs(neighbour.Y - anchor.Y) >= Math.Abs(neighbour.X - anchor.X);
            list.Add(new Endpoint(edge, source, anchor, horizontalFace, order[edge]));
        }

        // Two edges that compare EQUAL resolve to ONE entry in EdgeRoutes, because that dictionary
        // and MSAGL's own use the default comparer while IGraphEdge is implemented by records in
        // places. Nothing here can undo that collapse, but noting the shared route twice would make
        // this pass see a coincidence that does not exist and move a route that had no collision.
        HashSet<IReadOnlyList<Point>> noted = new(ReferenceEqualityComparer.Instance);

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

            if (!noted.Add(route))
            {
                continue;
            }

            Note(edge.Source, edge, true, route);
            Note(edge.Destination, edge, false, route);
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

            if (TryPlace(rect, e, occupied, out Port port))
            {
                ports[(e.Edge, e.Source)] = port;
                occupied.Add(port.Anchor);
                continue;
            }

            // Nowhere on the face to go. The anchor keeps its contested spot, and it still counts as
            // occupied: leaving it out would let the next endpoint read the spot as free and pile a
            // third one on. This surfaces as a SharedPorts gate failure with nothing naming this
            // pass, so it is the thing to look at first if that gate goes red on a new graph.
            occupied.Add(e.Anchor);
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

    // Slides along the face the edge leaves from, alternating sides, widest spacing first, stepping
    // to a further-out ring when the face is full.
    private static bool TryPlace(Rect rect, Endpoint end, List<Point> occupied, out Port port)
    {
        bool horizontal = end.HorizontalFace;
        double min = (horizontal ? rect.Left : rect.Top) + FaceMargin;
        double max = (horizontal ? rect.Right : rect.Bottom) - FaceMargin;
        double origin = horizontal ? end.Anchor.X : end.Anchor.Y;

        // Which of the two faces on that axis, and which way is away from the node, read off where
        // the anchor already is rather than assumed.
        bool low = horizontal
            ? end.Anchor.Y <= rect.Top + rect.Height / 2
            : end.Anchor.X <= rect.Left + rect.Width / 2;
        double face = horizontal
            ? low ? rect.Top : rect.Bottom
            : low ? rect.Left : rect.Right;
        double outward = low ? -1 : 1;

        for (int ring = 0; ring < Rings; ring++)
        {
            double perpendicular = face + outward * ring * RingStep;
            foreach (double step in _stepLadder)
            {
                int span = (int)((max - min) / step) + 1;

                // Ring 0 skips offset 0, which is the contested spot the anchor is being moved off.
                for (int i = ring == 0 ? 1 : 0; i <= span; i++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        double along = origin + sign * i * step;
                        if (along >= min && along <= max)
                        {
                            Point moved = horizontal
                                ? new Point(along, perpendicular)
                                : new Point(perpendicular, along);
                            if (IsFree(moved, occupied))
                            {
                                port = new Port(moved, horizontal, outward);
                                return true;
                            }
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

    // Moves one end of the route to its new port and rebuilds the two segments behind it, so the
    // polyline stays axis-aligned and stays clear of the node box.
    private static void Slide(List<Point> points, Port port, bool head)
    {
        int end = head ? 0 : points.Count - 1;
        int next = head ? 1 : points.Count - 2;
        Point moved = port.Anchor;
        Point neighbour = points[next];

        Point a, b;
        if (port.HorizontalFace)
        {
            double turn = port.Outward < 0
                ? Math.Min(neighbour.Y, moved.Y - Clearance)
                : Math.Max(neighbour.Y, moved.Y + Clearance);
            a = new Point(moved.X, turn);
            b = new Point(neighbour.X, turn);
        }
        else
        {
            double turn = port.Outward < 0
                ? Math.Min(neighbour.X, moved.X - Clearance)
                : Math.Max(neighbour.X, moved.X + Clearance);
            a = new Point(turn, moved.Y);
            b = new Point(turn, neighbour.Y);
        }

        points[end] = moved;

        // One turn is enough whenever the two would coincide, which happens when the old route
        // already bent at the height the new one turns at. Inserting the duplicate anyway is
        // harmless to draw and noisy to read back out of the route.
        List<Point> turns = [a];
        if (!Same(a, b))
        {
            turns.Add(b);
        }

        if (Same(turns[^1], neighbour))
        {
            turns.RemoveAt(turns.Count - 1);
        }

        if (turns.Count == 0)
        {
            return;
        }

        if (head)
        {
            points.InsertRange(1, turns);
        }
        else
        {
            turns.Reverse();
            points.InsertRange(points.Count - 1, turns);
        }
    }

    private static bool Same(Point a, Point b) =>
        Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    private readonly record struct Endpoint(
        IGraphEdge Edge, bool Source, Point Anchor, bool HorizontalFace, int Order);

    private readonly record struct Port(Point Anchor, bool HorizontalFace, double Outward);
}
