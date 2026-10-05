#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Overlay;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.Dossier;

/// <summary>
///     Draws one Setup Heatmap: the map's radar with the Overlay View's heatmap layer over it, fed an
///     <see cref="OverlayDocument" /> the Dossier filled from the positions files (the Overlay View
///     follow-up: reuse the document and the layer rather than a second heat renderer). Drawn through
///     <see cref="MapAssets.RenderPng" />, the path a Result Card thumbnail takes, so no demo opens and
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

    private readonly Dictionary<string, IMapAsset?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IMapAsset?> _loadMapAsset;
    private bool _disposed;

    /// <param name="loadMapAsset">Finds a map's baked bundle; the pipeline's loader in the app, a stub in a test.</param>
    public SetupHeatmapRenderer(Func<string, IMapAsset?>? loadMapAsset = null) =>
        _loadMapAsset = loadMapAsset ?? (map => MapAssets.TryLoad(map));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IMapAsset? asset in _assets.Values)
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

        IMapAsset? asset = AssetFor(overlay.MapName);
        Scene2DFrame frame = BuildFrame(overlay, asset);
        WorldBounds bounds = frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;

        return MapAssets.RenderPng(frame, asset, [SceneLayerIds.Radar, SceneLayerIds.Overlay],
            ViewportTransform.Fit(Size.Width, Size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY), Size,
            overlay);
    }

    /// <summary>
    ///     The markerless scene a heatmap draws on: the map's radar art and bounds when this host has
    ///     the bundle, else the synthetic grid over the points' own extent.
    /// </summary>
    /// <param name="overlay">The points.</param>
    /// <param name="asset">The map's bundle, or null.</param>
    public static Scene2DFrame BuildFrame(OverlayDocument overlay, IMapAsset? asset)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        SceneMapInfo map;
        if (asset is not null)
        {
            WorldBounds bounds = asset.RadarBounds;
            map = new SceneMapInfo
            {
                MapName = overlay.MapName,
                NetworkedBounds = bounds,
                ObservedBounds = bounds,
                Radars = asset.DescribeRadars()
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

    private IMapAsset? AssetFor(string map)
    {
        if (!_assets.TryGetValue(map, out IMapAsset? asset))
        {
            asset = _loadMapAsset(map);
            _assets[map] = asset;
        }

        return asset;
    }
}
