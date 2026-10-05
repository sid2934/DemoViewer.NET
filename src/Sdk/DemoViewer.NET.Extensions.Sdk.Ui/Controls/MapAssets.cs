#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Overlay;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     The maps the app ships, for drawing a map without a <see cref="MapView" />: a map's art loaded by name
///     and a still picture of a frame. Safe to call from any thread. Outside the app nothing is installed:
///     <see cref="TryLoad" /> answers null and <see cref="RenderPng" /> throws.
/// </summary>
public static class MapAssets
{
    /// <summary>What the app installs at startup; null outside it.</summary>
    internal static IMapAssetsBackend? Backend { get; set; }

    /// <summary>
    ///     Loads a map's art: floors, radar images and places. Each call decodes its own copy, which the caller
    ///     disposes. Null when the map name is empty, the app ships no art for it, or there is no app.
    /// </summary>
    /// <param name="mapName">The map, as the demo header spells it (<c>de_mirage</c>).</param>
    public static IMapAsset? TryLoad(string? mapName) =>
        string.IsNullOrWhiteSpace(mapName) ? null : Backend?.TryLoad(mapName);

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
    /// <summary>A fresh copy of the map's art, or null.</summary>
    IMapAsset? TryLoad(string mapName);

    /// <summary>A new icon source the caller disposes.</summary>
    IIconSource CreateIcons();

    /// <summary>One frame drawn to a PNG.</summary>
    byte[] RenderPng(Scene2DFrame frame, IMapAsset? map, IReadOnlyList<string> layerIds, ViewportTransform camera,
        SKSizeI size, OverlayDocument? overlay);
}
