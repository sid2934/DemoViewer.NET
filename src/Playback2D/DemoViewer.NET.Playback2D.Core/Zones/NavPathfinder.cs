namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>One point of a walked route: world XY and the floor key from this point on.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="FloorKey">The floor key of the area the route is in after this point.</param>
public readonly record struct NavWaypoint(double X, double Y, double FloorKey);

/// <summary>
///     Routes a token over the baked nav areas (<see cref="ZoneSet.Areas" />, <see cref="ZoneSet.AreaLinks" />).
///     A* over the area graph picks the corridor, then a funnel pass pulls the string through the shared edges
///     (docs/strat-book/token-pathing.md). Immutable once built and safe to query from any thread: each thread
///     has its own search scratch, and recent answers are kept per graph.
///     <para>
///         The bake folds the nav's directed links into undirected pairs and drops ladders. A link through a shared
///         edge is walked both ways; a link with no shared edge (a drop, a jump, a gap) may be climbed only when the
///         far area's mean Z is at most <c>maxClimb</c> higher.
///     </para>
/// </summary>
public sealed class NavPathfinder
{
    /// <summary>The climb a gap link allows, in world units: a player's jump onto a crate.</summary>
    public const double DefaultMaxClimb = 64;

    /// <summary>How far off the mesh a point still snaps onto it.</summary>
    public const double DefaultSnap = 256;

    /// <summary>How far a route keeps off a corner, where the portal is wide enough.</summary>
    public const double DefaultClearance = 16;

    // A link whose edges are collinear within this and overlap by more than it shares a real edge.
    private const double CollinearTolerance = 1.0;

    private const double CellSize = 256;

    private const int MemoCapacity = 1024;

    [ThreadStatic] private static Scratch? _scratch;

    private readonly ZoneArea[] _areas;
    private readonly Dictionary<long, int[]> _grid;
    private readonly int[] _linkStart;
    private readonly Link[] _links;
    private readonly Lock _memoLock = new();
    private readonly Dictionary<Query, NavWaypoint[]?> _memo = new(MemoCapacity);
    private readonly Queue<Query> _memoOrder = new(MemoCapacity);

    private NavPathfinder(ZoneArea[] areas, int[] linkStart, Link[] links, int gaps)
    {
        _areas = areas;
        _linkStart = linkStart;
        _links = links;
        GapLinkCount = gaps;

        Dictionary<long, List<int>> grid = [];
        for (int i = 0; i < areas.Length; i++)
        {
            ZoneArea a = areas[i];
            for (long cx = Cell(a.MinX); cx <= Cell(a.MaxX); cx++)
            {
                for (long cy = Cell(a.MinY); cy <= Cell(a.MaxY); cy++)
                {
                    long key = Key(cx, cy);
                    if (!grid.TryGetValue(key, out List<int>? list))
                    {
                        grid[key] = list = [];
                    }

                    list.Add(i);
                }
            }
        }

        _grid = grid.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>Area count.</summary>
    public int AreaCount => _areas.Length;

    /// <summary>Directed link count after the climb rule.</summary>
    public int LinkCount => _links.Length;

    /// <summary>Links whose areas share no collinear edge: drops, jumps and gaps.</summary>
    public int GapLinkCount { get; }

    /// <summary>Builds the graph.</summary>
    /// <param name="zones">The effective zone set.</param>
    /// <param name="maxClimb">How much higher a gap link's far area may be and still be taken.</param>
    public static NavPathfinder Build(ZoneSet zones, double maxClimb = DefaultMaxClimb)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ZoneArea[] areas = [.. zones.Areas];
        Dictionary<int, int> index = new(areas.Length);
        for (int i = 0; i < areas.Length; i++)
        {
            index.TryAdd(areas[i].Id, i);
        }

        List<(int From, Link Link)> directed = [];
        int gaps = 0;
        foreach ((int a, int b) in zones.AreaLinks)
        {
            if (!index.TryGetValue(a, out int ia) || !index.TryGetValue(b, out int ib) || ia == ib)
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

        return new NavPathfinder(areas, start, links, gaps);
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
    public int Locate(double x, double y, double? floorKey = null, int? placeId = null, double snap = DefaultSnap)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return -1;
        }

        int best = -1;
        double bestDistance = double.PositiveInfinity;
        int reach = (int)Math.Ceiling(snap / CellSize);
        long cx0 = Cell(x), cy0 = Cell(y);
        for (long cx = cx0 - reach; cx <= cx0 + reach; cx++)
        {
            for (long cy = cy0 - reach; cy <= cy0 + reach; cy++)
            {
                if (!_grid.TryGetValue(Key(cx, cy), out int[]? list))
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
    ///     The nearest point on the mesh: the point itself inside an area, else the closest point of the nearest area
    ///     within <paramref name="snap" />. Null when nothing is that close.
    /// </summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="floorKey">Only areas on this floor.</param>
    /// <param name="placeId">Only areas of this place.</param>
    /// <param name="snap">How far off the mesh a point may be.</param>
    public NavWaypoint? Snap(double x, double y, double? floorKey = null, int? placeId = null, double snap = DefaultSnap)
    {
        int area = Locate(x, y, floorKey, placeId, snap);
        if (area < 0)
        {
            return null;
        }

        ZoneArea a = _areas[area];
        if (a.ContainsXy(x, y))
        {
            return new NavWaypoint(x, y, a.FloorKey);
        }

        // Nudged a unit inside along the way from the edge to the centroid, so the snapped point locates in its area.
        (double nx, double ny) = NearestOnPolygon(a, x, y);
        double dx = a.CentroidX - nx, dy = a.CentroidY - ny, len = Math.Sqrt(dx * dx + dy * dy);
        return len > 1 ? new NavWaypoint(nx + dx / len, ny + dy / len, a.FloorKey) : new NavWaypoint(nx, ny, a.FloorKey);
    }

    /// <summary>
    ///     A walkable route from one point to another, both ends included, or null when either end is off the mesh or no
    ///     corridor joins them. A point is added wherever the route crosses into another floor, carrying the new key, so
    ///     the floor changes at the crossing rather than at the next corner. Answers are kept per exact query.
    /// </summary>
    /// <param name="sx">Start X.</param>
    /// <param name="sy">Start Y.</param>
    /// <param name="startArea">From <see cref="Locate" />.</param>
    /// <param name="ex">End X.</param>
    /// <param name="ey">End Y.</param>
    /// <param name="endArea">From <see cref="Locate" />.</param>
    /// <param name="clearance">How far the route keeps off a corner, where the portal is wide enough.</param>
    public IReadOnlyList<NavWaypoint>? FindPath(double sx, double sy, int startArea, double ex, double ey, int endArea,
        double clearance = DefaultClearance)
    {
        if (startArea < 0 || endArea < 0 || startArea >= _areas.Length || endArea >= _areas.Length
            || !double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(ex) || !double.IsFinite(ey))
        {
            return null;
        }

        Query query = new(sx, sy, startArea, ex, ey, endArea, clearance);
        lock (_memoLock)
        {
            if (_memo.TryGetValue(query, out NavWaypoint[]? known))
            {
                return known;
            }
        }

        NavWaypoint[]? path = Search(query);
        lock (_memoLock)
        {
            if (_memo.TryAdd(query, path))
            {
                _memoOrder.Enqueue(query);
                if (_memoOrder.Count > MemoCapacity)
                {
                    _memo.Remove(_memoOrder.Dequeue());
                }
            }
        }

        return path;
    }

    /// <summary>Total XY length of a route.</summary>
    /// <param name="path">The route.</param>
    public static double Length(IReadOnlyList<NavWaypoint> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        double total = 0;
        for (int i = 1; i < path.Count; i++)
        {
            total += Math.Sqrt(Sq(path[i].X - path[i - 1].X) + Sq(path[i].Y - path[i - 1].Y));
        }

        return total;
    }

    private NavWaypoint[]? Search(Query q)
    {
        if (q.StartArea == q.EndArea)
        {
            double f = _areas[q.StartArea].FloorKey;
            return [new NavWaypoint(q.Sx, q.Sy, f), new NavWaypoint(q.Ex, q.Ey, f)];
        }

        Scratch s = _scratch ??= new Scratch();
        s.Prepare(_areas.Length);
        return Corridor(s, q) ? Funnel(s, q) : null;
    }

    // A* with the cost measured between portal midpoints, the usual navmesh approximation; the funnel then shortens it.
    private bool Corridor(Scratch s, Query q)
    {
        int start = q.StartArea, goal = q.EndArea;
        double ex = q.Ex, ey = q.Ey;
        s.Open(start, q.Sx, q.Sy);
        s.Queue.Enqueue(start, Math.Sqrt(Sq(ex - q.Sx) + Sq(ey - q.Sy)));
        while (s.Queue.TryDequeue(out int current, out double priority))
        {
            if (current == goal)
            {
                break;
            }

            double cx = s.AtX[current], cy = s.AtY[current], gc = s.G(current);
            if (priority - Math.Sqrt(Sq(ex - cx) + Sq(ey - cy)) > gc + 1e-6)
            {
                continue;
            }

            for (int li = _linkStart[current]; li < _linkStart[current + 1]; li++)
            {
                Link link = _links[li];
                (double mx, double my) = link.To == goal ? (ex, ey) : link.Portal.Mid;
                double cost = gc + Math.Sqrt(Sq(mx - cx) + Sq(my - cy));
                if (cost >= s.G(link.To))
                {
                    continue;
                }

                s.Set(link.To, cost, current, li, mx, my);
                s.Queue.Enqueue(link.To, cost + Math.Sqrt(Sq(ex - mx) + Sq(ey - my)));
            }
        }

        if (double.IsPositiveInfinity(s.G(goal)))
        {
            return false;
        }

        s.Corridor.Clear();
        for (int a = goal; a != start; a = s.CameFrom[a])
        {
            s.Corridor.Add((a, s.CameBy[a]));
        }

        s.Corridor.Reverse();
        return true;
    }

    // Simple stupid funnel over the corridor's portals, each oriented left/right for the direction of travel.
    private NavWaypoint[] Funnel(Scratch s, Query q)
    {
        List<(int Area, int Link)> corridor = s.Corridor;
        int count = corridor.Count + 1;
        s.Size(count);
        (double X, double Y)[] left = s.Left, right = s.Right;
        double[] floorAfter = s.FloorAfter;
        double startFloor = _areas[q.StartArea].FloorKey;
        for (int i = 0; i < corridor.Count; i++)
        {
            (int area, int link) = corridor[i];
            Portal p = _links[link].Portal;
            (double ax, double ay, double bx, double by) = (p.Ax, p.Ay, p.Bx, p.By);
            double len = Math.Sqrt(Sq(bx - ax) + Sq(by - ay));
            if (len > 2 * q.Clearance + 1)
            {
                double ux = (bx - ax) / len, uy = (by - ay) / len;
                (ax, ay, bx, by) = (ax + ux * q.Clearance, ay + uy * q.Clearance, bx - ux * q.Clearance, by - uy * q.Clearance);
            }
            else
            {
                double mx = (ax + bx) / 2, my = (ay + by) / 2;
                (ax, ay, bx, by) = (mx, my, mx, my);
            }

            // Nav areas are convex, so centroid to edge midpoint points out of the area being left.
            ZoneArea from = _areas[i == 0 ? q.StartArea : corridor[i - 1].Area];
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

            floorAfter[i] = _areas[area].FloorKey;
        }

        left[count - 1] = right[count - 1] = (q.Ex, q.Ey);
        floorAfter[count - 1] = _areas[corridor[^1].Area].FloorKey;

        // Corners with the portal index each sits on: -1 for the start, count - 1 for the end.
        List<(double X, double Y, int Portal)> corners = s.Corners;
        corners.Clear();
        corners.Add((q.Sx, q.Sy, -1));
        (double X, double Y) apex = (q.Sx, q.Sy), fl = left[0], fr = right[0];
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
                    corners.Add((fl.X, fl.Y, li));
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
                    corners.Add((fr.X, fr.Y, ri));
                    apex = fr;
                    int restart = ri;
                    fl = fr = apex;
                    li = ri = restart;
                    i = restart;
                }
            }
        }

        if (corners[^1].X != q.Ex || corners[^1].Y != q.Ey)
        {
            corners.Add((q.Ex, q.Ey, count - 1));
        }

        // Every portal the string crosses into another floor gets a point of its own.
        List<NavWaypoint> path = s.Path;
        path.Clear();
        path.Add(new NavWaypoint(q.Sx, q.Sy, startFloor));
        for (int c = 1; c < corners.Count; c++)
        {
            (double ax, double ay, int pa) = corners[c - 1];
            (double bx, double by, int pb) = corners[c];
            for (int portal = pa + 1; portal < pb && portal < corridor.Count; portal++)
            {
                double before = portal == 0 ? startFloor : floorAfter[portal - 1];
                if (floorAfter[portal] == before)
                {
                    continue;
                }

                Portal edge = _links[corridor[portal].Link].Portal;
                double t = Crossing(ax, ay, bx, by, edge);
                Append(path, new NavWaypoint(ax + (bx - ax) * t, ay + (by - ay) * t, floorAfter[portal]));
            }

            Append(path, new NavWaypoint(bx, by, floorAfter[Math.Max(0, pb)]));
        }

        return [.. path];
    }

    private static void Append(List<NavWaypoint> path, NavWaypoint point)
    {
        NavWaypoint last = path[^1];
        if (Sq(last.X - point.X) + Sq(last.Y - point.Y) < 1e-6)
        {
            path[^1] = last with { FloorKey = point.FloorKey };
            return;
        }

        path.Add(point);
    }

    // Where segment a-b meets the portal's line, as a fraction of a-b; a point portal is projected onto a-b.
    private static double Crossing(double ax, double ay, double bx, double by, Portal p)
    {
        double dx = bx - ax, dy = by - ay, ex = p.Bx - p.Ax, ey = p.By - p.Ay;
        double denominator = Cross(dx, dy, ex, ey);
        double l2 = dx * dx + dy * dy;
        if (l2 < 1e-9)
        {
            return 0;
        }

        double t = Math.Abs(denominator) < 1e-9
            ? (((p.Ax + p.Bx) / 2 - ax) * dx + ((p.Ay + p.By) / 2 - ay) * dy) / l2
            : Cross(p.Ax - ax, p.Ay - ay, ex, ey) / denominator;
        return Math.Clamp(t, 0, 1);
    }

    private static (double, double) NearestOnPolygon(ZoneArea a, double x, double y)
    {
        double best = double.PositiveInfinity;
        (double, double) at = (a.CentroidX, a.CentroidY);
        for (int i = 0; i < a.CornerCount; i++)
        {
            (double ax, double ay, double bx, double by) = Edge(a, i);
            (double nx, double ny) = Nearest(x, y, ax, ay, bx, by);
            double d = Sq(nx - x) + Sq(ny - y);
            if (d < best)
            {
                best = d;
                at = (nx, ny);
            }
        }

        return at;
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
                Consider(ax, ay, cx, cy, dx, dy);
                Consider(bx, by, cx, cy, dx, dy);
                Consider(cx, cy, ax, ay, bx, by);
                Consider(dx, dy, ax, ay, bx, by);
            }
        }

        return new Portal(mid.X, mid.Y, mid.X, mid.Y);

        void Consider(double px, double py, double qx, double qy, double qx2, double qy2)
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

    // Exact doubles: a rounded key would hand one caller another's endpoints, and the projection must be pure.
    private readonly record struct Query(double Sx, double Sy, int StartArea, double Ex, double Ey, int EndArea, double Clearance);

    // One thread's search state, reused across queries and graphs. A generation stamp stands in for clearing g.
    private sealed class Scratch
    {
        private int[] _stamp = [];
        private double[] _g = [];
        private int _generation;

        public int[] CameFrom = [];
        public int[] CameBy = [];
        public double[] AtX = [];
        public double[] AtY = [];
        public (double X, double Y)[] Left = [];
        public (double X, double Y)[] Right = [];
        public double[] FloorAfter = [];
        public readonly PriorityQueue<int, double> Queue = new();
        public readonly List<(int Area, int Link)> Corridor = [];
        public readonly List<(double X, double Y, int Portal)> Corners = [];
        public readonly List<NavWaypoint> Path = [];

        public void Prepare(int areas)
        {
            if (_g.Length < areas)
            {
                _stamp = new int[areas];
                _g = new double[areas];
                CameFrom = new int[areas];
                CameBy = new int[areas];
                AtX = new double[areas];
                AtY = new double[areas];
                _generation = 0;
            }

            if (++_generation == int.MaxValue)
            {
                Array.Clear(_stamp);
                _generation = 1;
            }

            Queue.Clear();
        }

        public void Size(int count)
        {
            if (Left.Length < count)
            {
                int size = Math.Max(count, Left.Length * 2);
                Left = new (double, double)[size];
                Right = new (double, double)[size];
                FloorAfter = new double[size];
            }
        }

        public double G(int area) => _stamp[area] == _generation ? _g[area] : double.PositiveInfinity;

        public void Open(int area, double x, double y) => Set(area, 0, -1, -1, x, y);

        public void Set(int area, double g, int from, int by, double x, double y)
        {
            _stamp[area] = _generation;
            _g[area] = g;
            CameFrom[area] = from;
            CameBy[area] = by;
            AtX[area] = x;
            AtY[area] = y;
        }
    }
}
