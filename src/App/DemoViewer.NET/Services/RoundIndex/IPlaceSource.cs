#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     Mints the place string for one alive sample. The builder, the row shape, the sidecar and the
///     query service are identical in both token modes; only this one string differs, so it is the one
///     seam. Find Rounds Like This encodes the current tick through the same source, which is what keeps
///     the Query Canvas round-trip true in both modes.
/// </summary>
public interface IPlaceSource
{
    /// <summary>The <c>src</c> fingerprint field: <c>pawn</c> or <c>zones</c>.</summary>
    string SourceId { get; }

    /// <summary>The effective zone set version the names come from, or null when they come from the pawn.</summary>
    string? ZonesVersion { get; }

    /// <summary>The place for a sample, or null when unknown (the token writes <c>?</c>).</summary>
    /// <param name="sample">One alive player's position sample.</param>
    string? PlaceFor(in PositionSample sample);
}

/// <summary>The default: the pawn's <c>m_szLastPlaceName</c>, Valve's own vocabulary, no asset needed.</summary>
public sealed class PawnPlaceSource : IPlaceSource
{
    /// <summary>The one instance; it holds no state.</summary>
    public static PawnPlaceSource Instance { get; } = new();

    /// <inheritdoc />
    public string SourceId => "pawn";

    /// <inheritdoc />
    public string? ZonesVersion => null;

    /// <inheritdoc />
    public string? PlaceFor(in PositionSample sample) => sample.Place;
}

/// <summary>
///     The opt-in mode: the effective zone set (baked plus the user overlay) resolved over the sample
///     position, so search results speak the team's names when a team has authored its own zones.
/// </summary>
public sealed class ZonePlaceSource : IPlaceSource
{
    private readonly IZonePlaceResolver _resolver;

    /// <param name="resolver">The map's resolver over its effective zone set.</param>
    public ZonePlaceSource(IZonePlaceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public string SourceId => "zones";

    /// <inheritdoc />
    public string? ZonesVersion => _resolver.ZonesVersion;

    /// <inheritdoc />
    public string? PlaceFor(in PositionSample sample) => _resolver.Resolve(sample.Position);
}

/// <summary>
///     What the index needs from Zone Baking's per-map <c>PlaceResolver</c>: a name for a world point
///     and the adjacency graph, both over the effective zone set. Zone Baking's resolver is not in the
///     tree yet (part 1 baked the files; the resolver is its part 2), so this is the shape it plugs
///     into: an adapter over <c>PlaceResolver.Resolve(world).Name</c> and <c>Adjacent(placeId)</c>.
/// </summary>
public interface IZonePlaceResolver
{
    /// <summary><c>ZoneSet.EffectiveVersion</c>: the baked <c>zonesVersion</c> folded with the overlay.</summary>
    string ZonesVersion { get; }

    /// <summary>The place a world point falls in, or null for none.</summary>
    /// <param name="world">A world position.</param>
    string? Resolve(Vector3 world);

    /// <summary>The places sharing a nav connection with <paramref name="place" />; empty for an unknown place.</summary>
    /// <param name="place">A raw place name.</param>
    IReadOnlySet<string> Adjacent(string place);
}

/// <summary>Finds a map's zone resolver, or none when the map has no <c>zones.json</c>.</summary>
public interface IZonePlaceResolverSource
{
    /// <summary>The resolver for a map, or null when the map has no zones; the caller falls back to the pawn.</summary>
    /// <param name="map">The map name as the demo header spells it.</param>
    IZonePlaceResolver? TryGet(string map);
}

/// <summary>The source that has no zones for any map: the composition root's answer until Zone Baking's resolver lands.</summary>
public sealed class NoZonePlaceResolverSource : IZonePlaceResolverSource
{
    /// <summary>The one instance.</summary>
    public static NoZonePlaceResolverSource Instance { get; } = new();

    /// <inheritdoc />
    public IZonePlaceResolver? TryGet(string map) => null;
}
