#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Assets;

/// <summary>
///     A loaded map-asset bundle: the parsed <see cref="MapAssetBundle" /> plus its radar layers
///     decoded to <see cref="SKImage" />.
///     <para>
///         Moved out of the App in B1 and re-typed from Avalonia's <c>Bitmap</c> to <c>SKImage</c>, so
///         the radar draw is renderer-agnostic and export, the CLI and CI can all load a map without a
///         windowing system. The App keeps exactly one bitmap job: the library card thumbnail, which
///         needs <c>Bitmap.DecodeToWidth</c>'s downscale-on-decode and has no <c>SKImage</c> analogue
///         (plan decision D-16).
///     </para>
/// </summary>
public sealed class LoadedMapAsset : IDisposable
{
    private IReadOnlyList<FloorSlice>? _floors;
    private bool _disposed;

    /// <summary>The parsed bundle manifest.</summary>
    public required MapAssetBundle Bundle { get; init; }

    /// <summary>Decoded radar images by file name (best-effort — empty when nothing decoded).</summary>
    public required IReadOnlyDictionary<string, SKImage> RadarImages { get; init; }

    /// <summary>The directory the bundle was loaded from (holds sibling artifacts like <c>collision.tris</c>).</summary>
    public required string BakedDir { get; init; }

    /// <summary>
    ///     The bundle's nav-derived floor bands, low→high.
    ///     <para>
    ///         <b>Cached.</b> The pre-v2 property projected and materialised a fresh <c>List</c> on every
    ///         read, and the viewport read it once per push — a per-frame allocation for data that is
    ///         constant for the whole map (plan §4 T15 item 7).
    ///     </para>
    /// </summary>
    public IReadOnlyList<FloorSlice> Floors =>
        _floors ??= BuildFloors(Bundle);

    /// <summary>Absolute path to the baked collision blob, or null when the bundle has no mesh.</summary>
    public string? CollisionTrisPath =>
        Bundle.CollisionMesh is { } mesh ? Path.Combine(BakedDir, mesh.File) : null;

    /// <summary>
    ///     Releases the decoded radar images. One of the few places <see cref="IDisposable" /> genuinely
    ///     buys something: an <see cref="SKImage" />'s pixel buffer is unmanaged, invisible to the
    ///     managed heap counters, and a CS2 radar is 1024×1024 RGBA ≈ 4 MB — one or two per map, orphaned
    ///     on every map swap.
    ///     <para>
    ///         Idempotent: the tab view-model disposes on map change and on unload, and those overlap.
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SKImage image in RadarImages.Values)
        {
            image.Dispose();
        }
    }

    private static FloorSlice[] BuildFloors(MapAssetBundle bundle)
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
}

/// <summary>
///     Loads a baked map-asset bundle. <see cref="MapAssetBundleReader" /> (in the package) does the
///     directory walk-up and the JSON; this does the image decode.
///     <b>Never throws</b>: a missing or malformed bundle returns null and the scene degrades to its
///     grid + Z-histogram fallback.
/// </summary>
public static class MapAssetPipeline
{
    /// <summary>Loads the bundle for a map name, or null when there is none.</summary>
    /// <param name="mapName">e.g. <c>de_nuke</c>.</param>
    public static LoadedMapAsset? TryLoad(string? mapName)
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(mapName);
        return dir is null ? null : TryLoadFromDirectory(dir);
    }

    /// <summary>Loads a bundle from an explicit directory. Null-graceful.</summary>
    /// <param name="dir">The bundle directory.</param>
    public static LoadedMapAsset? TryLoadFromDirectory(string dir)
    {
        if (MapAssetBundleReader.TryRead(dir) is not { } bundle)
        {
            return null;
        }

        // Best-effort per image: a decode failure still yields the floor metadata and the transform,
        // which is what the level split actually needs.
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
                // ignore — floors and the transform remain usable without the picture
            }
        }

        return new LoadedMapAsset
        {
            Bundle = bundle,
            RadarImages = images,
            BakedDir = dir
        };
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
        // Fully qualified: the fixture serializer declares its own WorldBoundsDto in this assembly.
        CS2DemoKit.Analysis.Visibility.WorldBoundsDto b = asset.Bundle.Bounds;
        return new WorldBounds(b.MinX, b.MinY, b.MaxX, b.MaxY);
    }
}
