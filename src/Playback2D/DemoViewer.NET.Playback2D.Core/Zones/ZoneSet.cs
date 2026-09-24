namespace DemoViewer.NET.Playback2D.Core.Zones;

/// <summary>Where a place in the effective vocabulary came from.</summary>
public enum PlaceOrigin
{
    /// <summary>Valve's own <c>env_cs_place</c> volume set, as the baker wrote it.</summary>
    Baked,

    /// <summary>Defined by the user overlay: a custom polygon, or a merge of baked places.</summary>
    Custom
}

/// <summary>
///     One floor band of the baked set. <see cref="Key" /> is <c>MapSpace.QuantizeZ(MinZ)</c>, the same
///     value a <c>SpaceRef.World</c> annotation anchor carries; never a floor index.
/// </summary>
/// <param name="Key">The quantized lower Z that keys this band.</param>
/// <param name="MinZ">Lower world Z, inclusive.</param>
/// <param name="MaxZ">Upper world Z, exclusive.</param>
public readonly record struct ZoneFloor(double Key, double MinZ, double MaxZ)
{
    /// <summary>Whether a world Z falls in this band's half-open range.</summary>
    /// <param name="z">World Z.</param>
    public bool Contains(double z) => z >= MinZ && z < MaxZ;
}

/// <summary>One entry of the effective place vocabulary. The index in <see cref="ZoneSet.Places" /> is the id.</summary>
/// <param name="Id">The place id.</param>
/// <param name="Name">The raw place name (<c>BombsiteA</c>, <c>TopofMid</c>) or the custom zone's name.</param>
/// <param name="Origin">Baked or custom.</param>
public sealed record ZonePlace(int Id, string Name, PlaceOrigin Origin);

/// <summary>
///     The parsed <c>zones.json</c> for one map, after the user overlay has been applied: the place
///     vocabulary, the volumes, every nav area with its place, and the two graphs. Immutable; a
///     <see cref="PlaceResolver" /> is built over one.
///     <para>
///         <see cref="EffectiveVersion" /> is the stamp consumers store beside a resolved place. It is
///         the baked <see cref="ZonesVersion" /> alone when no overlay exists, and a CRC over both when
///         one does, so an edit to the overlay re-resolves every cached place on the next read.
///     </para>
/// </summary>
public sealed class ZoneSet
{
    /// <summary>Creates a set. Every list is retained as given.</summary>
    public ZoneSet(string mapName, string zonesVersion, string bundleMapVersion, string? bakerVersion,
        double floorQuantum, IReadOnlyList<ZoneFloor> floors, IReadOnlyList<ZonePlace> places,
        IReadOnlyList<ZoneVolume> volumes, IReadOnlyList<ZoneArea> areas,
        IReadOnlyList<(int A, int B)> areaLinks, IReadOnlyList<(int A, int B)> adjacency,
        double? bombRadius, string? effectiveVersion = null, string? overlayPath = null)
    {
        ArgumentNullException.ThrowIfNull(mapName);
        ArgumentNullException.ThrowIfNull(zonesVersion);
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(areaLinks);
        ArgumentNullException.ThrowIfNull(adjacency);

        MapName = mapName;
        ZonesVersion = zonesVersion;
        BundleMapVersion = bundleMapVersion ?? "";
        BakerVersion = bakerVersion;
        FloorQuantum = floorQuantum;
        Floors = floors;
        Places = places;
        Volumes = volumes;
        Areas = areas;
        AreaLinks = areaLinks;
        Adjacency = adjacency;
        BombRadius = bombRadius;
        EffectiveVersion = effectiveVersion ?? zonesVersion;
        OverlayPath = overlayPath;
    }

    /// <summary>The map, e.g. <c>de_nuke</c>.</summary>
    public string MapName { get; }

    /// <summary>The baked stamp: a CRC32 over the entity lump, every volume model and the nav bytes.</summary>
    public string ZonesVersion { get; }

    /// <summary>The <c>bundle.json</c> <c>mapVersion</c> the file was baked beside; drift detection only.</summary>
    public string BundleMapVersion { get; }

    /// <summary>The baker that wrote the file, or null on a synthetic set.</summary>
    public string? BakerVersion { get; }

    /// <summary>The level quantum the floor keys were minted with (64).</summary>
    public double FloorQuantum { get; }

    /// <summary>The floor bands, low to high, with the key each mints.</summary>
    public IReadOnlyList<ZoneFloor> Floors { get; }

    /// <summary>The effective vocabulary. Index equals <see cref="ZonePlace.Id" />.</summary>
    public IReadOnlyList<ZonePlace> Places { get; }

    /// <summary>
    ///     Every volume, in cascade order: user volumes first, then the baked ones in entity-lump order
    ///     (decision D4's tie rule is "first wins").
    /// </summary>
    public IReadOnlyList<ZoneVolume> Volumes { get; }

    /// <summary>One entry per nav area.</summary>
    public IReadOnlyList<ZoneArea> Areas { get; }

    /// <summary>Undirected nav connections between area ids, deduplicated.</summary>
    public IReadOnlyList<(int A, int B)> AreaLinks { get; }

    /// <summary>Undirected place pairs that share a nav connection.</summary>
    public IReadOnlyList<(int A, int B)> Adjacency { get; }

    /// <summary><c>info_map_parameters.bombradius</c>, or null when the map did not carry one.</summary>
    public double? BombRadius { get; }

    /// <summary>
    ///     The version every consumer stores: <see cref="ZonesVersion" /> with no overlay,
    ///     <c>CRC32(zonesVersion ‖ overlay bytes)</c> with one.
    /// </summary>
    public string EffectiveVersion { get; }

    /// <summary>The overlay file this set was built with, or null for the baked set alone.</summary>
    public string? OverlayPath { get; }

    /// <summary>True when an overlay changed the set.</summary>
    public bool HasOverlay => OverlayPath is not null;

    /// <summary>The name of a place id, or null for -1 and out-of-range ids.</summary>
    /// <param name="placeId">A place id.</param>
    public string? PlaceName(int placeId) =>
        placeId >= 0 && placeId < Places.Count ? Places[placeId].Name : null;
}
