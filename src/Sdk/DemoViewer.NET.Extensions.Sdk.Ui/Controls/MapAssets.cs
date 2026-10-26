#region

using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Hud;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Overlay;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     The maps the app ships, for drawing a map without a <see cref="MapView" />: a map's art held under a
///     lease, what a map's bundle says about itself without decoding anything, and a still picture of a frame.
///     Safe to call from any thread. Outside the app nothing is installed: <see cref="Acquire" /> and
///     <see cref="Describe" /> answer null and <see cref="RenderPng" /> throws.
/// </summary>
public static class MapAssets
{
    /// <summary>What the app installs at startup; null outside it.</summary>
    internal static IMapAssetsBackend? Backend { get; set; }

    /// <summary>
    ///     Takes a hold on a map's art: floors, radar images and places. One decode serves every holder of
    ///     the map process-wide, so dispose the lease to give the hold back rather than to free the art.
    ///     Null when the map name is empty, the app ships no art for it, or there is no app.
    /// </summary>
    /// <param name="mapName">The map, as the demo header spells it (<c>de_mirage</c>).</param>
    public static IMapAssetLease? Acquire(string? mapName) =>
        string.IsNullOrWhiteSpace(mapName) ? null : Backend?.Acquire(mapName);

    /// <summary>
    ///     What the map's bundle says about its extent and its floors, read from the bundle manifest with no
    ///     radar decode: the cheap answer for a list, a filter or a hit test that draws nothing. Exact, not
    ///     approximate: the same numbers <see cref="Acquire" /> would hand back.
    /// </summary>
    /// <param name="mapName">The map, as the demo header spells it (<c>de_mirage</c>).</param>
    public static MapDescription? Describe(string? mapName) =>
        string.IsNullOrWhiteSpace(mapName) ? null : Backend?.Describe(mapName);

    /// <summary>
    ///     Renders one frame to a PNG the way the app's export draws: the dark palette, the export level of
    ///     detail, the map's floors and radar images when <paramref name="map" /> is given, and the synthetic
    ///     grid when it is not.
    /// </summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="map">The map's art, or null for the grid.</param>
    /// <param name="layerIds">The app's layers to draw, from <c>SceneLayerIds</c>.</param>
    /// <param name="camera">Where the picture looks.</param>
    /// <param name="size">The picture's size in pixels.</param>
    /// <param name="overlay">The points the overlay heatmap layer draws, when <paramref name="layerIds" /> includes it.</param>
    /// <exception cref="InvalidOperationException">No app is installed to render.</exception>
    public static byte[] RenderPng(Scene2DFrame frame, IMapAsset? map, IReadOnlyList<string> layerIds,
        ViewportTransform camera, SKSizeI size, OverlayDocument? overlay = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(layerIds);
        IMapAssetsBackend backend = Backend
                                    ?? throw new InvalidOperationException("Map pictures are rendered by the app, and none is running.");
        return backend.RenderPng(frame, map, layerIds, camera, size, overlay);
    }
}

/// <summary>
///     A hold on one map's art, from <see cref="MapAssets.Acquire" />. It reads as the art itself, and
///     disposing it gives the hold back: the decode outlives the lease while any other holder has one, and the
///     radar images are released once the last hold ends.
///     <para>
///         Disposing is idempotent and callable from any thread. Read nothing off a lease after disposing it.
///     </para>
/// </summary>
public interface IMapAssetLease : IMapAsset, IDisposable
{
    /// <summary>The map the hold is on, as it was acquired.</summary>
    string MapName { get; }
}

/// <summary>What a map's bundle says about itself with nothing decoded.</summary>
public sealed class MapDescription
{
    /// <param name="radarBounds">The world rectangle the radar images cover.</param>
    /// <param name="floors">The map's floor bands, lowest first; empty when the bundle has none.</param>
    public MapDescription(WorldBounds radarBounds, IReadOnlyList<FloorSlice> floors)
    {
        ArgumentNullException.ThrowIfNull(floors);
        RadarBounds = radarBounds;
        Floors = floors;
    }

    /// <summary>The world rectangle the radar images cover.</summary>
    public WorldBounds RadarBounds { get; }

    /// <summary>The map's floor bands, lowest first; empty when the bundle has none.</summary>
    public IReadOnlyList<FloorSlice> Floors { get; }
}

/// <summary>
///     Game icons for a layer that draws them, such as weapon and grenade art, rasterised at the height asked
///     for. Dispose releases the rasterised images. Outside the app every lookup answers null.
/// </summary>
public sealed class MapIcons : IIconSource, IDisposable
{
    private readonly IIconSource? _inner = MapAssets.Backend?.CreateIcons();

    /// <inheritdoc />
    public SKImage? Lookup(string key, float pixelHeight) => _inner?.Lookup(key, pixelHeight);

    /// <inheritdoc />
    public bool IsBlankByDesign(string key) => _inner?.IsBlankByDesign(key) ?? false;

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}

/// <summary>What the app installs behind <see cref="MapAssets" /> and <see cref="MapIcons" />.</summary>
internal interface IMapAssetsBackend
{
    /// <summary>A hold on the map's art, or null. One decode serves every holder.</summary>
    IMapAssetLease? Acquire(string mapName);

    /// <summary>The map's extent and floors from its manifest, with no decode, or null.</summary>
    MapDescription? Describe(string mapName);

    /// <summary>A new icon source the caller disposes.</summary>
    IIconSource CreateIcons();

    /// <summary>One frame drawn to a PNG.</summary>
    byte[] RenderPng(Scene2DFrame frame, IMapAsset? map, IReadOnlyList<string> layerIds, ViewportTransform camera,
        SKSizeI size, OverlayDocument? overlay);
}
