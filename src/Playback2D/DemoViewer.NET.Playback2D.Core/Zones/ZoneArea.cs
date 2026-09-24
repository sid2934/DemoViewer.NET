namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>
///     One nav area with the place the bake (or the overlay) assigned it. Corners are flat world XY,
///     as the baker wrote them; the bounding box and centroid are derived once here because the
///     resolver's grid and the outline layer both need them.
/// </summary>
public sealed class ZoneArea
{
    /// <summary>Creates an area.</summary>
    /// <param name="id">The nav area id.</param>
    /// <param name="placeId">The place, or -1 for an unreachable island.</param>
    /// <param name="floorKey">The floor key of the band containing the area's mean corner Z.</param>
    /// <param name="isSeed">True when a place volume seeded this area directly.</param>
    /// <param name="z">The mean corner Z.</param>
    /// <param name="xy">Corners, flat: <c>x0, y0, x1, y1, ...</c>.</param>
    public ZoneArea(int id, int placeId, double floorKey, bool isSeed, double z, IReadOnlyList<double> xy)
    {
        ArgumentNullException.ThrowIfNull(xy);

        Id = id;
        PlaceId = placeId;
        FloorKey = floorKey;
        IsSeed = isSeed;
        Z = z;
        Xy = xy;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        double sumX = 0, sumY = 0;
        int corners = xy.Count / 2;
        for (int i = 0; i < corners; i++)
        {
            double x = xy[2 * i];
            double y = xy[2 * i + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            sumX += x;
            sumY += y;
        }

        CornerCount = corners;
        if (corners == 0)
        {
            minX = minY = maxX = maxY = 0;
        }

        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
        CentroidX = corners == 0 ? 0 : sumX / corners;
        CentroidY = corners == 0 ? 0 : sumY / corners;
        Area = ZoneGeometry.PolygonArea(xy);
    }

    /// <summary>The nav area id.</summary>
    public int Id { get; }

    /// <summary>The place, or -1.</summary>
    public int PlaceId { get; }

    /// <summary>The floor key (a level lower bound, never an index).</summary>
    public double FloorKey { get; }

    /// <summary>True when a place volume seeded this area directly rather than the flood.</summary>
    public bool IsSeed { get; }

    /// <summary>Mean corner Z.</summary>
    public double Z { get; }

    /// <summary>Corners, flat world XY.</summary>
    public IReadOnlyList<double> Xy { get; }

    /// <summary>Corner count.</summary>
    public int CornerCount { get; }

    /// <summary>Bounding box.</summary>
    public double MinX { get; }

    /// <summary>Bounding box.</summary>
    public double MinY { get; }

    /// <summary>Bounding box.</summary>
    public double MaxX { get; }

    /// <summary>Bounding box.</summary>
    public double MaxY { get; }

    /// <summary>Mean of the corners.</summary>
    public double CentroidX { get; }

    /// <summary>Mean of the corners.</summary>
    public double CentroidY { get; }

    /// <summary>Polygon area in square units.</summary>
    public double Area { get; }

    /// <summary>Whether a world XY lies inside the corner polygon.</summary>
    public bool ContainsXy(double x, double y) =>
        x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && ZoneGeometry.PointInPolygon(Xy, x, y);

    /// <summary>Distance from a world XY to the polygon: 0 inside, else the nearest edge.</summary>
    public double DistanceXy(double x, double y) =>
        ContainsXy(x, y) ? 0 : ZoneGeometry.DistanceToPolygonEdges(Xy, x, y);

    /// <summary>The same area under another place id.</summary>
    /// <param name="placeId">The new place id.</param>
    public ZoneArea WithPlace(int placeId) => new(Id, placeId, FloorKey, IsSeed, Z, Xy);
}
