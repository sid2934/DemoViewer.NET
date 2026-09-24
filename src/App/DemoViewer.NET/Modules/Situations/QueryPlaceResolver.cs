#region

using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>The place a canvas drop resolved to, and which source answered.</summary>
/// <param name="Place">The raw place name, as the index rows spell it.</param>
/// <param name="Source"><c>zones:&lt;zonesVersion&gt;</c> or <c>index</c>, the same spelling the adjacency graph uses.</param>
public sealed record QueryPlaceHit(string Place, string Source);

/// <summary>Turns a drop on a 2D pane into a place name. The one seam the tool resolves through, so a test can answer it by hand.</summary>
public interface IQueryPlaceResolver
{
    /// <summary>The place at a drop, or null when nothing is near enough on that floor.</summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    /// <param name="x">World X of the drop.</param>
    /// <param name="y">World Y of the drop.</param>
    /// <param name="levelMinZ">The pane's band lower Z, raw.</param>
    /// <param name="levelMaxZ">The pane's band upper Z, raw.</param>
    QueryPlaceHit? Resolve(string map, double x, double y, double levelMinZ, double levelMaxZ);
}

/// <summary>
///     The shipped resolver: Zone Baking's <c>ResolveOnFloor</c> when the map has zones, else
///     <see cref="PlaceSnap.Nearest" /> over the index's own place centroids on the pane's band. Both
///     produce a raw place name, so the token the canvas builds is the same either way.
///     <para>
///         The zone path sits behind <see cref="IZonePlaceResolverSource" />, the seam the index
///         builder already mints tokens through; until Zone Baking's resolver lands every map answers
///         "no zones" there and only the snap runs.
///     </para>
/// </summary>
public sealed class QueryPlaceResolver : IQueryPlaceResolver
{
    private readonly ISituationIndex _index;
    private readonly IZonePlaceResolverSource _zones;

    /// <param name="index">The place centroids, for the snap.</param>
    /// <param name="zones">Where a map's zone resolver comes from; none until Zone Baking's resolver lands.</param>
    public QueryPlaceResolver(ISituationIndex index, IZonePlaceResolverSource? zones = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _zones = zones ?? NoZonePlaceResolverSource.Instance;
    }

    /// <inheritdoc />
    public QueryPlaceHit? Resolve(string map, double x, double y, double levelMinZ, double levelMaxZ)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (_zones.TryGet(map) is { } zones)
        {
            // The floor key is the quantized band floor, the same key a world-anchored stroke stores.
            string? place = zones.ResolveOnFloor(x, y, MapSpace.QuantizeZ(levelMinZ));
            return place is null ? null : new QueryPlaceHit(place, $"zones:{zones.ZonesVersion}");
        }

        (string Place, double Distance)? nearest = PlaceSnap.Nearest(_index.Places(map), x, y, levelMinZ, levelMaxZ);
        return nearest is null ? null : new QueryPlaceHit(nearest.Value.Place, "index");
    }
}
