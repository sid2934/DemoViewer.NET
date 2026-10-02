namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>One corner of a walked route: world XY and the floor key of the area it enters.</summary>
public readonly record struct NavWaypoint(double X, double Y, double FloorKey);

/// <summary>
///     Spike: routes a token over the baked nav areas (<see cref="ZoneSet.Areas" />, <see cref="ZoneSet.AreaLinks" />).
///     A* over the area graph picks the corridor, then a funnel pass pulls the string through the shared edges.
///     Immutable once built and safe to query from any thread.
/// </summary>
public sealed class NavPathfinder
{
    // A link whose edges are collinear within this and overlap by more than it shares a real edge.
    private const double CollinearTolerance = 1.0;

    private const double CellSize = 256;

    private readonly ZoneArea[] _areas;
    private readonly int[] _linkStart;
    private readonly Link[] _links;
    private readonly Dictionary<long, List<int>> _grid = new();

    private NavPathfinder(ZoneArea[] areas, int[] linkStart, Link[] links)
    {
        _areas = areas;
        _linkStart = linkStart;
        _links = links;
        for (int i = 0; i < areas.Length; i++)
        {
            ZoneArea a = areas[i];
            for (long cx = Cell(a.MinX); cx <= Cell(a.MaxX); cx++)
            {
                for (long cy = Cell(a.MinY); cy <= Cell(a.MaxY); cy++)
                {
                    long key = Key(cx, cy);
                    if (!_grid.TryGetValue(key, out List<int>? list))
                    {
                        _grid[key] = list = [];
                    }

                    list.Add(i);
                }
            }
        }
    }

    /// <summary>Area count.</summary>
    public int AreaCount => _areas.Length;

    /// <summary>Directed link count after the climb rule.</summary>
    public int LinkCount => _links.Length;

    /// <summary>Links whose areas share no collinear edge: drops, jumps and gaps.</summary>
    public int GapLinkCount { get; private init; }

    /// <summary>Builds the graph.</summary>
    /// <param name="zones">The effective zone set.</param>
    /// <param name="maxClimb">
    ///     A gap link may be taken upward only when the far area's mean Z is at most this much higher. The bake keeps
    ///     links undirected, so this is a stand-in for the nav's own one-way drops.
    /// </param>
    public static NavPathfinder Build(ZoneSet zones, double maxClimb = 64)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ZoneArea[] areas = [.. zones.Areas];
        Dictionary<int, int> index = new(areas.Length);
        for (int i = 0; i < areas.Length; i++)
        {
            index[areas[i].Id] = i;
        }

        List<(int From, Link Link)> directed = [];
        int gaps = 0;
        foreach ((int a, int b) in zones.AreaLinks)
        {
            if (!index.TryGetValue(a, out int ia) || !index.TryGetValue(b, out int ib))
            {
                continue;
            }

            bool shared = TryOverlap(areas[ia], areas[ib], out Portal portal);
            if (!shared)
            {
                gaps++;
                portal = ClosestPortal(areas[ia], areas[ib]);
            }

            double dz = areas[ib].Z - areas[ia].Z;
            if (shared || dz <= maxClimb)
            {
                directed.Add((ia, new Link(ib, portal)));
            }

            if (shared || -dz <= maxClimb)
            {
                directed.Add((ib, new Link(ia, portal)));
            }
        }

        directed.Sort(static (l, r) => l.From.CompareTo(r.From));
        int[] start = new int[areas.Length + 1];
        Link[] links = new Link[directed.Count];
        for (int i = 0; i < directed.Count; i++)
        {
            links[i] = directed[i].Link;
            start[directed[i].From + 1]++;
        }

        for (int i = 0; i < areas.Length; i++)
        {
            start[i + 1] += start[i];
        }

        return new NavPathfinder(areas, start, links) { GapLinkCount = gaps };
    }

    /// <summary>
    ///     The area a point stands in, else the nearest within <paramref name="snap" /> units, on the floor and, when
    ///     given, in the place. -1 when none.
    /// </summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="floorKey">Only areas on this floor.</param>
    /// <param name="placeId">Only areas of this place, so a point over two levels picks the intended one.</param>
    /// <param name="snap">How far off the mesh a point may be.</param>
    public int Locate(double x, double y, double? floorKey = null, int? placeId = null, double snap = 256)
    {
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        int reach = (int)Math.Ceiling(snap / CellSize);
        long cx0 = Cell(x), cy0 = Cell(y);
        for (long cx = cx0 - reach; cx <= cx0 + reach; cx++)
        {
            for (long cy = cy0 - reach; cy <= cy0 + reach; cy++)
            {
                if (!_grid.TryGetValue(Key(cx, cy), out List<int>? list))
                {
                    continue;
                }

                foreach (int i in list)
                {
                    ZoneArea a = _areas[i];
                    if ((floorKey is { } f && a.FloorKey != f) || (placeId is { } p && a.PlaceId != p))
                    {
                        continue;
                    }

                    // Inside two areas at once means stacked levels; the smaller one is the more specific.
                    double d = a.DistanceXy(x, y);
                    if (d < bestDistance || (d == 0 && bestDistance == 0 && a.Area < _areas[best].Area))
                    {
                        bestDistance = d;
                        best = i;
                    }
                }
            }
        }

        return bestDistance <= snap ? best : -1;
    }

    /// <summary>The area at an index.</summary>
    /// <param name="index">From <see cref="Locate" />.</param>
    public ZoneArea AreaAt(int index) => _areas[index];

    /// <summary>
    ///     A walkable route from one point to another, both ends included, or null when either end is off the mesh or no
    ///     corridor joins them.
    /// </summary>
    /// <param name="sx">Start X.</param>
    /// <param name="sy">Start Y.</param>
    /// <param name="startArea">From <see cref="Locate" />.</param>
    /// <param name="ex">End X.</param>
    /// <param name="ey">End Y.</param>
    /// <param name="endArea">From <see cref="Locate" />.</param>
    /// <param name="clearance">How far the route keeps off a corner, where the portal is wide enough.</param>
    public IReadOnlyList<NavWaypoint>? FindPath(double sx, double sy, int startArea, double ex, double ey, int endArea,
        double clearance = 16)
    {
        if (startArea < 0 || endArea < 0)
        {
            return null;
        }

        if (startArea == endArea)
        {
            double f = _areas[startArea].FloorKey;
            return [new NavWaypoint(sx, sy, f), new NavWaypoint(ex, ey, f)];
        }

        List<(int Area, Portal Portal)>? corridor = Corridor(sx, sy, startArea, ex, ey, endArea);
        return corridor is null ? null : Funnel(sx, sy, startArea, ex, ey, corridor, clearance);
    }

    /// <summary>Total XY length of a route.</summary>
    /// <param name="path">The route.</param>
    public static double Length(IReadOnlyList<NavWaypoint> path)
    {
        double total = 0;
        for (int i = 1; i < path.Count; i++)
        {
            total += Math.Sqrt(Sq(path[i].X - path[i - 1].X) + Sq(path[i].Y - path[i - 1].Y));
        }

        return total;
    }

    // A* with the cost measured between portal midpoints, the usual navmesh approximation; the funnel then shortens it.
    private List<(int Area, Portal Portal)>? Corridor(double sx, double sy, int start, double ex, double ey, int goal)
    {
        int n = _areas.Length;
        double[] g = new double[n];
        int[] cameBy = new int[n];
        int[] cameFrom = new int[n];
        (double X, double Y)[] at = new (double, double)[n];
        Array.Fill(g, double.PositiveInfinity);
        Array.Fill(cameFrom, -1);
        PriorityQueue<int, double> open = new();
        g[start] = 0;
        at[start] = (sx, sy);
        open.Enqueue(start, Math.Sqrt(Sq(ex - sx) + Sq(ey - sy)));
        while (open.TryDequeue(out int current, out double priority))
        {
            if (current == goal)
            {
                break;
            }

            (double cx, double cy) = at[current];
            if (priority - Math.Sqrt(Sq(ex - cx) + Sq(ey - cy)) > g[current] + 1e-6)
            {
                continue;
            }

            for (int li = _linkStart[current]; li < _linkStart[current + 1]; li++)
            {
                Link link = _links[li];
                (double mx, double my) = link.Portal.Mid;
                if (link.To == goal)
                {
                    (mx, my) = (ex, ey);
                }

                double cost = g[current] + Math.Sqrt(Sq(mx - cx) + Sq(my - cy));
                if (cost >= g[link.To])
                {
                    continue;
                }

                g[link.To] = cost;
                cameFrom[link.To] = current;
                cameBy[link.To] = li;
                at[link.To] = (mx, my);
                open.Enqueue(link.To, cost + Math.Sqrt(Sq(ex - mx) + Sq(ey - my)));
            }
        }

        if (double.IsPositiveInfinity(g[goal]))
        {
            return null;
        }

        List<(int, Portal)> corridor = [];
        for (int a = goal; a != start; a = cameFrom[a])
        {
            corridor.Add((a, _links[cameBy[a]].Portal));
        }

        corridor.Reverse();
        return corridor;
    }

    // Simple stupid funnel over the corridor's portals, each oriented left/right for the direction of travel.
    private List<NavWaypoint> Funnel(double sx, double sy, int startArea, double ex, double ey,
        List<(int Area, Portal Portal)> corridor, double clearance)
    {
        int count = corridor.Count + 1;
        var left = new (double X, double Y)[count];
        var right = new (double X, double Y)[count];
        double[] floorAfter = new double[count];
        for (int i = 0; i < corridor.Count; i++)
        {
            (int area, Portal p) = corridor[i];
            (double ax, double ay, double bx, double by) = (p.Ax, p.Ay, p.Bx, p.By);
            double len = Math.Sqrt(Sq(bx - ax) + Sq(by - ay));
            if (len > 2 * clearance + 1)
            {
                double ux = (bx - ax) / len, uy = (by - ay) / len;
                (ax, ay, bx, by) = (ax + ux * clearance, ay + uy * clearance, bx - ux * clearance, by - uy * clearance);
            }
            else
            {
                double mx = (ax + bx) / 2, my = (ay + by) / 2;
                (ax, ay, bx, by) = (mx, my, mx, my);
            }

            // Nav areas are convex, so centroid to edge midpoint points out of the area being left.
            ZoneArea next = _areas[area];
            ZoneArea from = _areas[i == 0 ? startArea : corridor[i - 1].Area];
            double mx2 = (p.Ax + p.Bx) / 2, my2 = (p.Ay + p.By) / 2;
            double ox = mx2 - from.CentroidX, oy = my2 - from.CentroidY;
            if (Cross(ox, oy, ax - mx2, ay - my2) > Cross(ox, oy, bx - mx2, by - my2))
            {
                left[i] = (ax, ay);
                right[i] = (bx, by);
            }
            else
            {
                left[i] = (bx, by);
                right[i] = (ax, ay);
            }

            floorAfter[i] = next.FloorKey;
        }

        left[^1] = right[^1] = (ex, ey);
        floorAfter[^1] = _areas[corridor[^1].Area].FloorKey;

        List<NavWaypoint> path = [new NavWaypoint(sx, sy, _areas[startArea].FloorKey)];
        (double X, double Y) apex = (sx, sy), fl = left[0], fr = right[0];
        int li = 0, ri = 0;
        for (int i = 1; i < count; i++)
        {
            (double X, double Y) l = left[i];
            (double X, double Y) r = right[i];

            if (Tri(apex, fr, r) <= 0)
            {
                if (Same(apex, fr) || Tri(apex, fl, r) > 0)
                {
                    fr = r;
                    ri = i;
                }
                else
                {
                    path.Add(new NavWaypoint(fl.X, fl.Y, floorAfter[li]));
                    apex = fl;
                    int restart = li;
                    fl = fr = apex;
                    li = ri = restart;
                    i = restart;
                    continue;
                }
            }

            if (Tri(apex, fl, l) >= 0)
            {
                if (Same(apex, fl) || Tri(apex, fr, l) < 0)
                {
                    fl = l;
                    li = i;
                }
                else
                {
                    path.Add(new NavWaypoint(fr.X, fr.Y, floorAfter[ri]));
                    apex = fr;
                    int restart = ri;
                    fl = fr = apex;
                    li = ri = restart;
                    i = restart;
                }
            }
        }

        NavWaypoint last = path[^1];
        if (last.X != ex || last.Y != ey)
        {
            path.Add(new NavWaypoint(ex, ey, floorAfter[^1]));
        }

        return path;
    }

    // Twice the signed area of (a, b, c), negated: positive when c is right of a->b, as the funnel's tests expect.
    private static double Tri((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        -Cross(b.X - a.X, b.Y - a.Y, c.X - a.X, c.Y - a.Y);

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    private static bool Same((double X, double Y) a, (double X, double Y) b) => Sq(a.X - b.X) + Sq(a.Y - b.Y) < 1e-6;

    private static double Sq(double v) => v * v;

    private static long Cell(double v) => (long)Math.Floor(v / CellSize);

    private static long Key(long cx, long cy) => (cx << 32) ^ (cy & 0xffffffffL);

    private static bool TryOverlap(ZoneArea a, ZoneArea b, out Portal portal)
    {
        portal = default;
        double best = 1.0;
        for (int i = 0; i < a.CornerCount; i++)
        {
            (double ax, double ay, double bx, double by) = Edge(a, i);
            double len = Math.Sqrt(Sq(bx - ax) + Sq(by - ay));
            if (len < 1e-6)
            {
                continue;
            }

            double ux = (bx - ax) / len, uy = (by - ay) / len;
            for (int j = 0; j < b.CornerCount; j++)
            {
                (double cx, double cy, double dx, double dy) = Edge(b, j);
                if (Math.Abs(Cross(ux, uy, cx - ax, cy - ay)) > CollinearTolerance ||
                    Math.Abs(Cross(ux, uy, dx - ax, dy - ay)) > CollinearTolerance)
                {
                    continue;
                }

                double t0 = (cx - ax) * ux + (cy - ay) * uy, t1 = (dx - ax) * ux + (dy - ay) * uy;
                double lo = Math.Max(0, Math.Min(t0, t1)), hi = Math.Min(len, Math.Max(t0, t1));
                if (hi - lo > best)
                {
                    best = hi - lo;
                    portal = new Portal(ax + ux * lo, ay + uy * lo, ax + ux * hi, ay + uy * hi);
                }
            }
        }

        return best > 1.0;
    }

    // A drop or jump between areas that share no edge: a point portal midway between their nearest boundary points.
    private static Portal ClosestPortal(ZoneArea a, ZoneArea b)
    {
        double best = double.PositiveInfinity;
        (double X, double Y) mid = (0, 0);
        for (int i = 0; i < a.CornerCount; i++)
        {
            (double ax, double ay, double bx, double by) = Edge(a, i);
            for (int j = 0; j < b.CornerCount; j++)
            {
                (double cx, double cy, double dx, double dy) = Edge(b, j);
                foreach ((double px, double py, double qx, double qy, double qx2, double qy2) in
                         new[] { (ax, ay, cx, cy, dx, dy), (bx, by, cx, cy, dx, dy), (cx, cy, ax, ay, bx, by), (dx, dy, ax, ay, bx, by) })
                {
                    (double nx, double ny) = Nearest(px, py, qx, qy, qx2, qy2);
                    double d = Sq(nx - px) + Sq(ny - py);
                    if (d < best)
                    {
                        best = d;
                        mid = ((nx + px) / 2, (ny + py) / 2);
                    }
                }
            }
        }

        return new Portal(mid.X, mid.Y, mid.X, mid.Y);
    }

    private static (double, double) Nearest(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-9 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        return (ax + t * dx, ay + t * dy);
    }

    private static (double, double, double, double) Edge(ZoneArea a, int i)
    {
        int j = (i + 1) % a.CornerCount;
        return (a.Xy[2 * i], a.Xy[2 * i + 1], a.Xy[2 * j], a.Xy[2 * j + 1]);
    }

    private readonly record struct Portal(double Ax, double Ay, double Bx, double By)
    {
        public (double X, double Y) Mid => ((Ax + Bx) / 2, (Ay + By) / 2);
    }

    private readonly record struct Link(int To, Portal Portal);
}
