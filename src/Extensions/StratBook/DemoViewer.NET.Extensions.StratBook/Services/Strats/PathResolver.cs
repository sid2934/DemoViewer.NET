#region

using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats;

/// <summary>
///     Where a strat token walks between two points: a route round the walls, for the projection
///     (docs/strat-format.md, "Motion on the canvas"). Answers from memory, so it is called on the UI thread.
/// </summary>
public abstract class PathResolver
{
    /// <summary>
    ///     The route from one point to another, both ends included, each point carrying the floor key from there on.
    ///     Null when either end is off the mesh or nothing joins them; the caller then goes straight.
    /// </summary>
    /// <param name="fromX">Start X.</param>
    /// <param name="fromY">Start Y.</param>
    /// <param name="fromLevel">The start's level key.</param>
    /// <param name="toX">End X.</param>
    /// <param name="toY">End Y.</param>
    /// <param name="toLevel">The end's level key.</param>
    /// <param name="toPlace">The place the end is in, which picks the level where two overlap; null for a bare point.</param>
    public abstract IReadOnlyList<NavWaypoint>? Route(double fromX, double fromY, double fromLevel, double toX, double toY,
        double toLevel, string? toPlace);

    /// <summary>
    ///     The nearest walkable point within <paramref name="maxDistance" />: the point itself when it is on the mesh. Null
    ///     when nothing is that close.
    /// </summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="level">The level key.</param>
    /// <param name="place">Only the place's own areas; null for any.</param>
    /// <param name="maxDistance">How far off the mesh the point may be.</param>
    public abstract (double X, double Y)? Snap(double x, double y, double level, string? place, double maxDistance);

    /// <summary>Whether a straight line stays within <paramref name="tolerance" /> of the mesh on a level, tested every 8 units.</summary>
    /// <param name="x0">Start X.</param>
    /// <param name="y0">Start Y.</param>
    /// <param name="x1">End X.</param>
    /// <param name="y1">End Y.</param>
    /// <param name="level">The level key.</param>
    /// <param name="tolerance">How far off the mesh a sample may be.</param>
    public bool Clear(double x0, double y0, double x1, double y1, double level, double tolerance = 12)
    {
        double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int samples = Math.Max(1, (int)Math.Ceiling(length / 8));
        for (int s = 1; s < samples; s++)
        {
            double t = s / (double)samples;
            if (Snap(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, level, null, tolerance) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a point stands on the mesh on its level.</summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="level">The level key.</param>
    public bool OnMesh(double x, double y, double level) => Snap(x, y, level, null, 0) is { } at && at.X == x && at.Y == y;
}

/// <summary>
///     <see cref="PathResolver" /> over one map's nav graph (<see cref="NavPathfinder" />). Built with the zones, off
///     the UI thread; place names resolve to the effective zone set's ids.
/// </summary>
public sealed class NavPathResolver : PathResolver
{
    private readonly NavPathfinder _nav;
    private readonly Dictionary<string, int> _places;

    /// <summary>Builds the graph for a zone set.</summary>
    /// <param name="zones">The map's effective zones.</param>
    public NavPathResolver(ZoneSet zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _nav = NavPathfinder.Build(zones);
        _places = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ZonePlace place in zones.Places)
        {
            _places.TryAdd(place.Name, place.Id);
        }
    }

    /// <summary>The graph.</summary>
    public NavPathfinder Graph => _nav;

    /// <inheritdoc />
    public override IReadOnlyList<NavWaypoint>? Route(double fromX, double fromY, double fromLevel, double toX, double toY, double toLevel,
        string? toPlace)
    {
        int start = Area(fromX, fromY, fromLevel, null);
        int end = Area(toX, toY, toLevel, toPlace);
        return _nav.FindPath(fromX, fromY, start, toX, toY, end);
    }

    /// <inheritdoc />
    public override (double X, double Y)? Snap(double x, double y, double level, string? place, double maxDistance)
    {
        int? id = place is not null && _places.TryGetValue(place, out int known) ? known : null;
        return place is not null && id is null
            ? null
            : _nav.Snap(x, y, level, id, maxDistance) is { } at ? (at.X, at.Y) : null;
    }

    // On the level, in the place when there is one; then on the level; then anywhere, since a level key that is not a
    // zone floor's (a default level of 0) would otherwise lose the route.
    private int Area(double x, double y, double level, string? place)
    {
        if (place is not null && _places.TryGetValue(place, out int id))
        {
            int inPlace = _nav.Locate(x, y, level, id);
            if (inPlace >= 0)
            {
                return inPlace;
            }

            inPlace = _nav.Locate(x, y, null, id);
            if (inPlace >= 0)
            {
                return inPlace;
            }
        }

        int onLevel = _nav.Locate(x, y, level);
        return onLevel >= 0 ? onLevel : _nav.Locate(x, y);
    }
}
