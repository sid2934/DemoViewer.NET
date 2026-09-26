#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Dossier;

/// <summary>
///     Draws one Setup Heatmap: the map's radar with the Overlay View's heatmap layer over it, fed an
///     <see cref="OverlayDocument" /> the Dossier filled from the positions files (the Overlay View
///     follow-up: reuse the document and the layer rather than a second heat renderer). The headless
///     path a Result Card thumbnail takes (<see cref="SceneLayerCatalog" /> stack,
///     <see cref="HeadlessSceneRenderer" />, the CPU provider, the dark palette), so no demo opens and
///     the wash is the one the Situations canvas shows.
///     <para>
///         One instance serves one build: map bundles are loaded once per map and held until
///         <see cref="Dispose" />. Not thread-safe; the Dossier drives it from one worker.
///     </para>
/// </summary>
public sealed class SetupHeatmapRenderer : IDisposable
{
    /// <summary>The heatmap size: square, because a radar is.</summary>
    public static readonly SKSizeI Size = new(320, 320);

    private readonly Dictionary<string, LoadedMapAsset?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, LoadedMapAsset?> _loadMapAsset;
    private bool _disposed;

    /// <param name="loadMapAsset">Finds a map's baked bundle; the pipeline's loader in the app, a stub in a test.</param>
    public SetupHeatmapRenderer(Func<string, LoadedMapAsset?>? loadMapAsset = null) =>
        _loadMapAsset = loadMapAsset ?? (map => MapAssetPipeline.TryLoad(map));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (LoadedMapAsset? asset in _assets.Values)
        {
            asset?.Dispose();
        }

        _assets.Clear();
    }

    /// <summary>The PNG of an overlay on its map, or null when the overlay is empty.</summary>
    /// <param name="overlay">The points, with the map they lie on.</param>
    public byte[]? Render(OverlayDocument overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay.IsEmpty)
        {
            return null;
        }

        LoadedMapAsset? asset = AssetFor(overlay.MapName);
        Scene2DFrame frame = BuildFrame(overlay, asset);
        WorldBounds bounds = frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor =
            SceneLayerCatalog.CreateSceneStack([SceneLayerIds.Radar, SceneLayerIds.Overlay], overlay: overlay);
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Purpose = RenderPurpose.Export,
            Camera = ViewportTransform.Fit(Size.Width, Size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY)
        };
        renderer.Levels.SetAuthoritativeFloors(asset?.Floors);
        renderer.Levels.RadarBinder = asset is null ? null : new MapRadarBinder(asset);

        SceneTime time = frame.Time;
        return renderer.RenderPng(frame, in time, Size);
    }

    /// <summary>
    ///     The markerless scene a heatmap draws on: the map's radar art and bounds when this host has
    ///     the bundle, else the synthetic grid over the points' own extent.
    /// </summary>
    /// <param name="overlay">The points.</param>
    /// <param name="asset">The map's bundle, or null.</param>
    public static Scene2DFrame BuildFrame(OverlayDocument overlay, LoadedMapAsset? asset)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        SceneMapInfo map;
        if (asset is not null)
        {
            WorldBounds bounds = MapAssetPipeline.RadarBounds(asset);
            map = new SceneMapInfo
            {
                MapName = overlay.MapName,
                NetworkedBounds = bounds,
                ObservedBounds = bounds,
                Radars = MapAssetPipeline.DescribeRadars(asset)
            };
        }
        else
        {
            // Padded so the kernel at the edge of the extent is not cut off by the frame.
            IReadOnlyList<OverlayPoint> points = overlay.Points;
            map = new SceneMapInfo
            {
                MapName = overlay.MapName,
                ObservedBounds = new WorldBounds(
                    points.Min(p => p.WorldX) - 512, points.Min(p => p.WorldY) - 512,
                    points.Max(p => p.WorldX) + 512, points.Max(p => p.WorldY) + 512)
            };
        }

        return new Scene2DFrame
        {
            Time = new SceneTime(0, 0, 0, 1.0 / 64, true),
            Markers = [],
            Map = map
        };
    }

    private LoadedMapAsset? AssetFor(string map)
    {
        if (!_assets.TryGetValue(map, out LoadedMapAsset? asset))
        {
            asset = _loadMapAsset(map);
            _assets[map] = asset;
        }

        return asset;
    }
}
