#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Assets;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Zones;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Assets;

/// <summary>
///     A loaded map-asset bundle: the parsed <see cref="MapAssetBundle" /> plus its radar layers
///     decoded to <see cref="SKImage" />.
///     <para>
///         Moved out of the App and re-typed from Avalonia's <c>Bitmap</c> to <c>SKImage</c>, so
///         the radar draw is renderer-agnostic and export, the CLI and CI can all load a map without a
///         windowing system. The App keeps exactly one bitmap job: the library card thumbnail, which
///         needs <c>Bitmap.DecodeToWidth</c>'s downscale-on-decode and has no <c>SKImage</c> analogue.
///     </para>
/// </summary>
public sealed class LoadedMapAsset : IMapAsset, IDisposable
{
    private readonly Lock _zoneLock = new();
    private bool _disposed;
    private IReadOnlyList<FloorSlice>? _floors;
    private Lazy<IReadOnlyDictionary<string, SKImage>> _radarImages;
    private OverlayStamp _zoneStamp;
    private ZoneLoadResult? _zones;

    /// <summary>Creates an undecoded bundle; the radar art is decoded on the first read.</summary>
    public LoadedMapAsset() =>
        // A method group, not a lambda over Bundle and BakedDir: those are init-only and still unset
        // here, and the factory only reads them once something forces it.
        _radarImages = new Lazy<IReadOnlyDictionary<string, SKImage>>(
            DecodeOwnRadars, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The parsed bundle manifest.</summary>
    public required MapAssetBundle Bundle { get; init; }

    /// <summary>
    ///     Decoded radar images by file name (best-effort, empty when nothing decoded).
    ///     <para>
    ///         <b>Decoded on the first read</b>, which is the first draw: a radar is 1024x1024 RGBA and a
    ///         holder that only wants floors, bounds or places (the zone sources, a thumbnail index) would
    ///         otherwise pin megabytes of unmanaged pixels per map it touched. Setting it pre-seeds the
    ///         decode instead, for a test that builds a bundle by hand.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<string, SKImage> RadarImages
    {
        get => _radarImages.Value;
        init => _radarImages = new Lazy<IReadOnlyDictionary<string, SKImage>>(value);
    }

    /// <summary>The directory the bundle was loaded from (holds sibling artifacts like <c>collision.tris</c>).</summary>
    public required string BakedDir { get; init; }

    /// <summary>
    ///     The bundle's nav-derived floor bands, low→high.
    ///     <para>
    ///         <b>Cached.</b> The pre-v2 property projected and materialised a fresh <c>List</c> on every
    ///         read, and the viewport read it once per push: a per-frame allocation for data that is
    ///         constant for the whole map.
    ///     </para>
    /// </summary>
    public IReadOnlyList<FloorSlice> Floors =>
        _floors ??= MapAssetPipeline.BuildFloors(Bundle);

    /// <summary>Absolute path to the baked collision blob, or null when the bundle has no mesh.</summary>
    public string? CollisionTrisPath =>
        Bundle.CollisionMesh is { } mesh ? Path.Combine(BakedDir, mesh.File) : null;

    /// <summary>
    ///     The user's <c>zones/</c> directory the overlay is read from, or null for the baked set alone.
    ///     Set at decode time by whoever knows the config root; Pipeline cannot. Init-only: the zones read
    ///     is lazy, so a later write would let one acquirer cache a baked-only resolver and its version
    ///     while another reads the overlay set.
    /// </summary>
    public string? ZoneOverlayDirectory { get; init; }

    /// <summary>
    ///     The place resolver over this map's <c>zones.json</c> plus the user overlay, or null when the
    ///     bundle directory has no zones file. <b>Lazy</b>: the file is parsed and the grid built on the
    ///     first read, so <c>EnsureMapAsset</c> needs no new wiring and a map whose zones nobody asks
    ///     for costs nothing. Consumers outside playback read it off the lease they hold, through
    ///     <see cref="RefreshZones" />.
    /// </summary>
    public PlaceResolver? Zones => ZoneLoad.Resolver;

    /// <summary>The last zones load in full: resolver, baked set, overlay path and diagnostics.</summary>
    internal ZoneLoadResult ZoneLoad
    {
        get
        {
            lock (_zoneLock)
            {
                return _zones ??= LoadZones();
            }
        }
    }

    /// <summary>True once <see cref="Zones" /> or <see cref="ZoneLoad" /> has been read.</summary>
    public bool ZonesLoaded
    {
        get
        {
            lock (_zoneLock)
            {
                return _zones is not null;
            }
        }
    }

    /// <summary>
    ///     Re-reads <c>zones.json</c> and the overlay unconditionally: the "Reload zones" command, so an
    ///     author sees an edited overlay without a map reload. The previous resolver stays valid for anyone
    ///     still holding it; a fresh one is built and returned.
    /// </summary>
    internal ZoneLoadResult ReloadZones()
    {
        lock (_zoneLock)
        {
            return _zones = LoadZones();
        }
    }

    /// <summary>
    ///     The map's places, re-read only when the overlay file's stamp has moved since the last read.
    ///     Unchanged means the <b>same instance</b>, which is what lets a caller cache the graph it folded
    ///     out of a resolver and compare by reference instead of by zones version.
    /// </summary>
    public PlaceResolver? RefreshZones()
    {
        lock (_zoneLock)
        {
            OverlayStamp stamp = OverlayStamp.Of(OverlayPath());
            if (_zones is { } current && stamp == _zoneStamp)
            {
                return current.Resolver;
            }

            _zones = LoadZones(stamp);
            return _zones.Resolver;
        }
    }

    /// <summary>
    ///     Releases the decoded radar images. One of the few places <see cref="IDisposable" /> genuinely
    ///     buys something: an <see cref="SKImage" />'s pixel buffer is unmanaged, invisible to the
    ///     managed heap counters, and a CS2 radar is 1024×1024 RGBA ≈ 4 MB: one or two per map, orphaned
    ///     on every map swap.
    ///     <para>
    ///         Idempotent: the lease registry owns the release, and it drops an entry as its last hold ends
    ///         rather than when the deferred dispose runs, so a second call has to be harmless.
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_radarImages.IsValueCreated)
        {
            return; // nothing drew this bundle, so there are no pixels to release
        }

        foreach (SKImage image in _radarImages.Value.Values)
        {
            image.Dispose();
        }
    }

    /// <inheritdoc />
    WorldBounds IMapAsset.RadarBounds => MapAssetPipeline.RadarBounds(this);

    /// <inheritdoc />
    IReadOnlyList<MapRadarImage> IMapAsset.DescribeRadars() => MapAssetPipeline.DescribeRadars(this);

    /// <inheritdoc />
    ILevelRadarBinder IMapAsset.CreateRadarBinder() => new MapRadarBinder(this);

    private IReadOnlyDictionary<string, SKImage> DecodeOwnRadars() => MapAssetPipeline.DecodeRadars(BakedDir, Bundle);

    // Caller holds _zoneLock. The stamp is taken BEFORE the read, so a save that lands during it is
    // seen by the next refresh instead of being folded into this result's stamp.
    private ZoneLoadResult LoadZones(OverlayStamp? stamp = null)
    {
        _zoneStamp = stamp ?? OverlayStamp.Of(OverlayPath());
        return ZoneAssetPipeline.Load(BakedDir, ZoneOverlayDirectory);
    }

    // The manifest's map name, not the zones file's: this has to resolve without reading anything.
    private string? OverlayPath() => ZoneAssetPipeline.OverlayPathFor(ZoneOverlayDirectory, Bundle.MapName);

    // Existence, length and write time: enough to see a save, and one stat per refresh rather than per
    // read, because Zones is read per frame and RefreshZones is called once per unit of work.
    private readonly record struct OverlayStamp(bool Exists, long Length, DateTime WrittenUtc)
    {
        public static OverlayStamp Of(string? path)
        {
            if (path is null)
            {
                return default;
            }

            try
            {
                FileInfo file = new(path);
                return file.Exists ? new OverlayStamp(true, file.Length, file.LastWriteTimeUtc) : default;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return default;
            }
        }
    }
}

/// <summary>
///     Loads a baked map-asset bundle. <see cref="MapAssetBundleReader" /> (in the package) does the
///     directory walk-up and the JSON; this does the image decode.
///     <b>Never throws</b>: a missing or malformed bundle returns null and the scene degrades to its
///     grid + Z-histogram fallback.
/// </summary>
public static class MapAssetPipeline
{
    private static int _radarDecodes;

    /// <summary>Loads the bundle for a map name, or null when there is none.</summary>
    /// <param name="mapName">e.g. <c>de_nuke</c>.</param>
    public static LoadedMapAsset? TryLoad(string? mapName)
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(mapName);
        return dir is null ? null : TryLoadFromDirectory(dir);
    }

    /// <summary>
    ///     Loads a map's bundle from an <b>explicit</b> assets root: the entry point <c>dv2d</c> needs.
    ///     <para>
    ///         Explicit-root is the whole point of the overload:
    ///         <see cref="MapAssetBundleReader.FindBundleDirectory" /> walks up from
    ///         <see cref="AppContext.BaseDirectory" />, which is the wrong answer for a tool that was told
    ///         where the art lives (<c>--assets</c> / <c>DV2D_ASSETS</c>).
    ///     </para>
    /// </summary>
    /// <param name="assetsRoot">The directory holding one subdirectory per map.</param>
    /// <param name="mapName">The map, e.g. <c>de_nuke</c>.</param>
    public static LoadedMapAsset? TryLoad(string? assetsRoot, string? mapName)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot) || string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        return TryLoadFromDirectory(Path.Combine(assetsRoot, mapName));
    }

    /// <summary>
    ///     The bundle's extent and floor bands straight from the manifest, with no image decode: the same
    ///     numbers a loaded bundle answers with, for a caller that draws nothing. Null when there is no
    ///     readable bundle.
    /// </summary>
    /// <param name="mapName">e.g. <c>de_nuke</c>.</param>
    public static (WorldBounds Bounds, IReadOnlyList<FloorSlice> Floors)? TryDescribe(string? mapName)
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(mapName);
        if (dir is null || MapAssetBundleReader.TryRead(dir) is not { } bundle)
        {
            return null;
        }

        return (BoundsOf(bundle), BuildFloors(bundle));
    }

    /// <summary>
    ///     Reads just the bundle's <c>mapVersion</c>: the cheap call a fixture capture and a
    ///     <c>golden verify</c> staleness check both need, with no image decode. Null when there is no
    ///     readable bundle.
    /// </summary>
    /// <param name="assetsRoot">The directory holding one subdirectory per map.</param>
    /// <param name="mapName">The map, e.g. <c>de_nuke</c>.</param>
    public static string? TryReadMapVersion(string? assetsRoot, string? mapName)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot) || string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        return MapAssetBundleReader.TryRead(Path.Combine(assetsRoot, mapName))?.MapVersion;
    }

    /// <summary>
    ///     The walk-up fallback: the first <c>assets/</c> directory at or above the process base
    ///     directory. Only used when neither <c>--assets</c> nor <c>DV2D_ASSETS</c> was given.
    /// </summary>
    public static string? TryLocateAssetsRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "assets");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Loads a bundle from an explicit directory. Null-graceful.</summary>
    /// <param name="dir">The bundle directory.</param>
    /// <param name="zoneOverlayDirectory">
    ///     The user's <c>zones/</c> directory, or null for the baked set alone. Passed at decode time
    ///     because <see cref="LoadedMapAsset.Zones" /> is read lazily and possibly off-thread.
    /// </param>
    public static LoadedMapAsset? TryLoadFromDirectory(string dir, string? zoneOverlayDirectory = null)
    {
        if (MapAssetBundleReader.TryRead(dir) is not { } bundle)
        {
            return null;
        }

        return new LoadedMapAsset
        {
            Bundle = bundle,
            BakedDir = dir,
            ZoneOverlayDirectory = zoneOverlayDirectory
        };
    }

    /// <summary>Radar decodes since the process started. For tests: the count "one decode per map" means.</summary>
    internal static int RadarDecodes => Volatile.Read(ref _radarDecodes);

    // One bundle's radar art. Best-effort per image: a decode failure still yields the floor metadata
    // and the transform, which is what the level split actually needs.
    internal static IReadOnlyDictionary<string, SKImage> DecodeRadars(string dir, MapAssetBundle bundle)
    {
        Dictionary<string, SKImage> images = new(StringComparer.Ordinal);
        foreach (string name in bundle.RadarImages)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using SKData data = SKData.Create(path);
                if (SKImage.FromEncodedData(data) is { } image)
                {
                    images[name] = image;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // ignore: floors and the transform remain usable without the picture
            }
        }

        Interlocked.Increment(ref _radarDecodes);
        return images;
    }

    /// <summary>
    ///     Describes the bundle's radar layers as <see cref="MapRadarImage" />s for
    ///     <see cref="SceneMapInfo.Radars" />.
    ///     <para>
    ///         The scene's own radar draw reads <c>MapLevel.Radar</c>, bound by
    ///         <see cref="MapRadarBinder" /> once per level-set rebuild. This exists so the frame carries
    ///         the radar identity too, which is what lets a captured <c>SceneFixture</c> say <i>which</i>
    ///         map picture it was rendered against rather than leaving a re-render to guess.
    ///     </para>
    /// </summary>
    /// <param name="asset">A loaded bundle.</param>
    public static IReadOnlyList<MapRadarImage> DescribeRadars(LoadedMapAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        WorldBounds bounds = RadarBounds(asset);
        IReadOnlyList<RadarLayerDto> layers = asset.Bundle.RadarLayers;

        if (layers.Count == 0)
        {
            if (asset.Bundle.RadarImages is not { Count: > 0 } images)
            {
                return [];
            }

            return
            [
                new MapRadarImage
                {
                    Name = images[0],
                    Image = asset.RadarImages.GetValueOrDefault(images[0]),
                    Bounds = bounds
                }
            ];
        }

        MapRadarImage[] described = new MapRadarImage[layers.Count];
        for (int i = 0; i < layers.Count; i++)
        {
            RadarLayerDto layer = layers[i];
            described[i] = new MapRadarImage
            {
                Name = layer.Image,
                Image = asset.RadarImages.GetValueOrDefault(layer.Image),
                Bounds = bounds,
                MinZ = layer.MinZ,
                MaxZ = layer.MaxZ
            };
        }

        return described;
    }

    /// <summary>The world rectangle the bundle's radar images span.</summary>
    /// <param name="asset">A loaded bundle.</param>
    public static WorldBounds RadarBounds(LoadedMapAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return BoundsOf(asset.Bundle);
    }

    // Shared by the loaded bundle and by TryDescribe, so a description cannot drift from a decode.
    internal static FloorSlice[] BuildFloors(MapAssetBundle bundle)
    {
        if (bundle.Floors is not { Count: > 0 } floors)
        {
            return [];
        }

        FloorSlice[] slices = new FloorSlice[floors.Count];
        for (int i = 0; i < floors.Count; i++)
        {
            slices[i] = new FloorSlice(floors[i].MinZ, floors[i].MaxZ);
        }

        return slices;
    }

    // Fully qualified: the fixture serializer declares its own WorldBoundsDto in this assembly.
    private static WorldBounds BoundsOf(MapAssetBundle bundle)
    {
        CS2DemoKit.Analysis.Visibility.WorldBoundsDto b = bundle.Bounds;
        return new WorldBounds(b.MinX, b.MinY, b.MaxX, b.MaxY);
    }
}
