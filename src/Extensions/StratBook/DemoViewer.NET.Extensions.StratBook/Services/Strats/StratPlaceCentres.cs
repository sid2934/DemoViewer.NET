#region

using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     Where each place of one map is, for a token that watches it: the area-weighted centre of the place's nav
///     areas on each floor, and over all floors. Built once from the zones, off the UI thread; lookups are
///     dictionary reads.
/// </summary>
public sealed class StratPlaceCentres
{
    private readonly Dictionary<string, Dictionary<double, (double X, double Y)>> _byFloor;
    private readonly Dictionary<string, double> _mainFloor;
    private readonly Dictionary<string, (double X, double Y)> _overall;

    private StratPlaceCentres(Dictionary<string, Dictionary<double, (double X, double Y)>> byFloor,
        Dictionary<string, (double X, double Y)> overall, Dictionary<string, double> mainFloor)
    {
        _byFloor = byFloor;
        _overall = overall;
        _mainFloor = mainFloor;
    }

    /// <summary>The centres of every place in <paramref name="zones" />.</summary>
    /// <param name="zones">The map's effective zones.</param>
    public static StratPlaceCentres From(ZoneSet zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        Dictionary<(int Place, double Floor), (double X, double Y, double W)> floorSums = [];
        Dictionary<int, (double X, double Y, double W)> sums = [];
        foreach (ZoneArea area in zones.Areas)
        {
            if (area.PlaceId < 0 || area.CornerCount == 0)
            {
                continue;
            }

            double w = Math.Max(area.Area, 1);
            floorSums[(area.PlaceId, area.FloorKey)] = Add(floorSums.GetValueOrDefault((area.PlaceId, area.FloorKey)), area, w);
            sums[area.PlaceId] = Add(sums.GetValueOrDefault(area.PlaceId), area, w);
        }

        Dictionary<string, Dictionary<double, (double X, double Y)>> byFloor = new(StringComparer.Ordinal);
        Dictionary<string, (double X, double Y)> overall = new(StringComparer.Ordinal);
        Dictionary<string, (double Floor, double W)> largest = new(StringComparer.Ordinal);
        foreach (((int place, double floor), (double x, double y, double w)) in floorSums)
        {
            if (zones.PlaceName(place) is not { } name)
            {
                continue;
            }

            if (!byFloor.TryGetValue(name, out Dictionary<double, (double X, double Y)>? floors))
            {
                byFloor[name] = floors = [];
            }

            floors[floor] = (x / w, y / w);
            if (!largest.TryGetValue(name, out (double Floor, double W) best) || w > best.W || (w == best.W && floor < best.Floor))
            {
                largest[name] = (floor, w);
            }
        }

        foreach ((int place, (double x, double y, double w)) in sums)
        {
            if (zones.PlaceName(place) is { } name)
            {
                overall.TryAdd(name, (x / w, y / w));
            }
        }

        // A custom zone drawn with no nav areas under it: the middle of its box.
        foreach (ZoneVolume volume in zones.Volumes)
        {
            if (volume is { Kind: ZoneVolumeKind.Place, PlaceId: >= 0 } && zones.PlaceName(volume.PlaceId) is { } name)
            {
                overall.TryAdd(name, ((volume.Min.X + volume.Max.X) / 2.0, (volume.Min.Y + volume.Max.Y) / 2.0));
            }
        }

        return new StratPlaceCentres(byFloor, overall, largest.ToDictionary(p => p.Key, p => p.Value.Floor, StringComparer.Ordinal));
    }

    /// <summary>
    ///     The place's centre on the floor keyed <paramref name="floorKey" /> when it has areas there, else over all
    ///     its floors; null for a place the map does not have.
    /// </summary>
    /// <param name="place">A canonical place name.</param>
    /// <param name="floorKey">The token's level key, which is the zones' floor key.</param>
    public (double X, double Y)? Centre(string place, double floorKey)
    {
        if (string.IsNullOrEmpty(place))
        {
            return null;
        }

        if (_byFloor.TryGetValue(place, out Dictionary<double, (double X, double Y)>? floors)
            && floors.TryGetValue(floorKey, out (double X, double Y) onFloor))
        {
            return onFloor;
        }

        return _overall.TryGetValue(place, out (double X, double Y) centre) ? centre : null;
    }

    /// <summary>
    ///     Where a token arrives at the place: its centre on <paramref name="floorKey" /> when it has areas there, else
    ///     on the floor holding most of its area, with that floor's key; the overall centre and the given key for a
    ///     place with no areas. Null for a place the map does not have.
    /// </summary>
    /// <param name="place">A canonical place name.</param>
    /// <param name="floorKey">The token's level key, which is the zones' floor key.</param>
    public (double X, double Y, double FloorKey)? Arrival(string place, double floorKey)
    {
        if (string.IsNullOrEmpty(place))
        {
            return null;
        }

        if (_byFloor.TryGetValue(place, out Dictionary<double, (double X, double Y)>? floors) && floors.Count > 0)
        {
            if (floors.TryGetValue(floorKey, out (double X, double Y) here))
            {
                return (here.X, here.Y, floorKey);
            }

            double main = _mainFloor[place];
            (double X, double Y) there = floors[main];
            return (there.X, there.Y, main);
        }

        return _overall.TryGetValue(place, out (double X, double Y) centre) ? (centre.X, centre.Y, floorKey) : null;
    }

    private static (double X, double Y, double W) Add((double X, double Y, double W) sum, ZoneArea area, double w) =>
        (sum.X + area.CentroidX * w, sum.Y + area.CentroidY * w, sum.W + w);
}
