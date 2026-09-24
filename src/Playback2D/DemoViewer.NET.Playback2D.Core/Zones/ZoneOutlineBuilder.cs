#region

using System.Numerics;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>
///     Turns one floor of a <see cref="ZoneSet" /> into <see cref="PlaceOutline" />s. An edge of an
///     area's polygon is a boundary edge unless the same-place neighbours linked to that area cover it;
///     a neighbour may cover only part of an edge (nav meshes are full of T-junctions), so coverage is
///     tracked as intervals along the edge and only the uncovered remainder is emitted.
/// </summary>
internal static class ZoneOutlineBuilder
{
    // How far a neighbour's edge may sit off this edge's line and still count as the same edge. The
    // baker rounds corners to 0.1 units; nav areas that touch share corners to well under a unit.
    private const double CollinearTolerance = 1.0;

    // Uncovered remainders shorter than this are corner slop, not boundary.
    private const double MinSegment = 0.5;

    public static IReadOnlyList<PlaceOutline> Build(ZoneSet zones, double floorKey)
    {
        Dictionary<int, ZoneArea> byId = new(zones.Areas.Count);
        foreach (ZoneArea area in zones.Areas)
        {
            byId[area.Id] = area;
        }

        Dictionary<int, List<int>> links = new();
        foreach ((int a, int b) in zones.AreaLinks)
        {
            Link(links, a, b);
            Link(links, b, a);
        }

        // Areas of this floor, grouped by place, in place-id order so the result is deterministic.
        SortedDictionary<int, List<ZoneArea>> byPlace = new();
        foreach (ZoneArea area in zones.Areas)
        {
            if (area.PlaceId < 0 || !area.FloorKey.Equals(floorKey) || area.CornerCount < 2)
            {
                continue;
            }

            if (!byPlace.TryGetValue(area.PlaceId, out List<ZoneArea>? list))
            {
                list = [];
                byPlace[area.PlaceId] = list;
            }

            list.Add(area);
        }

        ZoneFloor? band = null;
        foreach (ZoneFloor floor in zones.Floors)
        {
            if (floor.Key.Equals(floorKey))
            {
                band = floor;
                break;
            }
        }

        List<PlaceOutline> outlines = [];
        HashSet<int> drawnAsPolygon = [];

        // A custom zone with a polygon draws that polygon: it is what the author wrote, and the areas it
        // took are only an approximation of it at nav granularity.
        foreach (ZoneVolume volume in zones.Volumes)
        {
            if (volume.Origin != PlaceOrigin.Custom || volume.Polygon is not { Count: >= 6 } polygon ||
                volume.PlaceId < 0 || !drawnAsPolygon.Add(volume.PlaceId))
            {
                continue;
            }

            if (band is { } b && !volume.MeetsBand(b.MinZ, b.MaxZ))
            {
                drawnAsPolygon.Remove(volume.PlaceId);
                continue;
            }

            int n = polygon.Count / 2;
            List<(Vector2 A, Vector2 B)> edges = new(n);
            double sumX = 0, sumY = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                edges.Add((new Vector2((float)polygon[2 * i], (float)polygon[2 * i + 1]),
                    new Vector2((float)polygon[2 * j], (float)polygon[2 * j + 1])));
                sumX += polygon[2 * i];
                sumY += polygon[2 * i + 1];
            }

            outlines.Add(new PlaceOutline(volume.PlaceId, zones.PlaceName(volume.PlaceId) ?? "",
                PlaceOrigin.Custom, edges, new Vector2((float)(sumX / n), (float)(sumY / n))));
        }

        foreach ((int placeId, List<ZoneArea> areas) in byPlace)
        {
            if (drawnAsPolygon.Contains(placeId))
            {
                continue;
            }

            List<(Vector2 A, Vector2 B)> edges = [];
            List<(double X, double Y, double Weight)> weights = new(areas.Count);
            List<(double T0, double T1)> covered = [];

            foreach (ZoneArea area in areas)
            {
                weights.Add((area.CentroidX, area.CentroidY, area.Area));

                links.TryGetValue(area.Id, out List<int>? neighbours);
                int n = area.CornerCount;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    double ax = area.Xy[2 * i], ay = area.Xy[2 * i + 1];
                    double bx = area.Xy[2 * j], by = area.Xy[2 * j + 1];
                    double ex = bx - ax, ey = by - ay;
                    double len2 = ex * ex + ey * ey;
                    if (len2 < MinSegment * MinSegment)
                    {
                        continue;
                    }

                    covered.Clear();
                    if (neighbours is not null)
                    {
                        foreach (int neighbourId in neighbours)
                        {
                            if (!byId.TryGetValue(neighbourId, out ZoneArea? neighbour) ||
                                neighbour.PlaceId != placeId)
                            {
                                continue;
                            }

                            CollectCoverage(neighbour, ax, ay, ex, ey, len2, covered);
                        }
                    }

                    EmitUncovered(covered, ax, ay, ex, ey, Math.Sqrt(len2), edges);
                }
            }

            (double lx, double ly) = ZoneGeometry.WeightedCentroid(weights);
            outlines.Add(new PlaceOutline(placeId, zones.PlaceName(placeId) ?? "",
                zones.Places[placeId].Origin, edges, new Vector2((float)lx, (float)ly)));
        }

        outlines.Sort(static (l, r) => l.PlaceId.CompareTo(r.PlaceId));
        return outlines;
    }

    // Every edge of the neighbour that lies on this edge's line contributes the interval of the edge it
    // overlaps, in the edge's own [0, 1] parameter.
    private static void CollectCoverage(ZoneArea neighbour, double ax, double ay, double ex, double ey,
        double len2, List<(double T0, double T1)> covered)
    {
        int n = neighbour.CornerCount;
        for (int k = 0; k < n; k++)
        {
            int l = (k + 1) % n;
            double px = neighbour.Xy[2 * k], py = neighbour.Xy[2 * k + 1];
            double qx = neighbour.Xy[2 * l], qy = neighbour.Xy[2 * l + 1];

            if (OffLine(ax, ay, ex, ey, len2, px, py) > CollinearTolerance ||
                OffLine(ax, ay, ex, ey, len2, qx, qy) > CollinearTolerance)
            {
                continue;
            }

            double tp = ((px - ax) * ex + (py - ay) * ey) / len2;
            double tq = ((qx - ax) * ex + (qy - ay) * ey) / len2;
            double t0 = Math.Max(0, Math.Min(tp, tq));
            double t1 = Math.Min(1, Math.Max(tp, tq));
            if (t1 - t0 > 1e-9)
            {
                covered.Add((t0, t1));
            }
        }
    }

    private static void EmitUncovered(List<(double T0, double T1)> covered, double ax, double ay,
        double ex, double ey, double length, List<(Vector2 A, Vector2 B)> edges)
    {
        covered.Sort(static (l, r) => l.T0.CompareTo(r.T0));

        double cursor = 0;
        foreach ((double t0, double t1) in covered)
        {
            if (t0 > cursor)
            {
                AddSegment(cursor, t0);
            }

            cursor = Math.Max(cursor, t1);
        }

        if (cursor < 1)
        {
            AddSegment(cursor, 1);
        }

        return;

        void AddSegment(double t0, double t1)
        {
            if ((t1 - t0) * length < MinSegment)
            {
                return;
            }

            edges.Add((new Vector2((float)(ax + ex * t0), (float)(ay + ey * t0)),
                new Vector2((float)(ax + ex * t1), (float)(ay + ey * t1))));
        }
    }

    // Perpendicular distance from a point to the infinite line through the edge.
    private static double OffLine(double ax, double ay, double ex, double ey, double len2, double px, double py) =>
        Math.Abs(ex * (py - ay) - ey * (px - ax)) / Math.Sqrt(len2);

    private static void Link(Dictionary<int, List<int>> links, int from, int to)
    {
        if (!links.TryGetValue(from, out List<int>? list))
        {
            list = [];
            links[from] = list;
        }

        list.Add(to);
    }
}
