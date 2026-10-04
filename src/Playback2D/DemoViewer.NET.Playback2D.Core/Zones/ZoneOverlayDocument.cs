namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>
///     The parsed user overlay, <c>&lt;config&gt;/zones/&lt;map&gt;.zones.json</c>: what differs from the
///     baked set it was written against. Applied by <see cref="ZoneOverlayApplier" />; the JSON reader
///     lives in Pipeline beside the file read.
/// </summary>
/// <param name="SchemaVersion">The overlay schema, 1.</param>
/// <param name="MapName">The map the author wrote it for, or null.</param>
/// <param name="BasedOn">The baked <c>zonesVersion</c> the author looked at; a mismatch warns, never rejects.</param>
/// <param name="Zones">Custom zones, applied in order after <see cref="Hidden" /> and <see cref="Merges" />.</param>
/// <param name="Merges">Several baked places becoming one custom place.</param>
/// <param name="Hidden">Baked places removed from the vocabulary; their areas re-flood from neighbours.</param>
public sealed record ZoneOverlayDocument(
    int SchemaVersion,
    string? MapName,
    string? BasedOn,
    IReadOnlyList<ZoneOverlayZone> Zones,
    IReadOnlyList<ZoneOverlayMerge> Merges,
    IReadOnlyList<string> Hidden)
{
    /// <summary>The empty overlay: applying it changes nothing but the stamp.</summary>
    public static readonly ZoneOverlayDocument Empty = new(1, null, null, [], [], []);
}

/// <summary>A custom zone: a simple polygon on one floor, optionally with its own Z band.</summary>
/// <param name="Name">Becomes a place; the name is the callout.</param>
/// <param name="Floor">A floor key of the baked set (<c>MapSpace.QuantizeZ</c> of the band's <c>minZ</c>).</param>
/// <param name="Polygon">World XY, flat, a simple polygon.</param>
/// <param name="MinZ">Lower Z, or null for the floor band's.</param>
/// <param name="MaxZ">Upper Z, or null for the floor band's.</param>
/// <param name="Replaces">
///     False: the zone only claims points inside it. True: a baked place whose every area the zone took
///     is removed, and a baked place with the same name is hidden so the name has one owner.
/// </param>
public sealed record ZoneOverlayZone(
    string Name,
    double Floor,
    IReadOnlyList<double> Polygon,
    double? MinZ,
    double? MaxZ,
    bool Replaces);

/// <summary>Several baked places becoming one custom place with the union of their areas and volumes.</summary>
/// <param name="Into">The new place's name.</param>
/// <param name="From">The baked places it absorbs.</param>
public sealed record ZoneOverlayMerge(string Into, IReadOnlyList<string> From);
