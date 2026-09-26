#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     Draws a Lineup Card's "landing point on the radar": the map's baked radar art with one marker
///     where the grenade went off, through the same headless path <see cref="Situations.SituationThumbnailRenderer" />
///     draws a Result Card's mini-radar (<c>dv2d render</c>'s stack, the CPU provider, the dark
///     palette). No players; the marker takes the thrower's side colour when the row read one, an
///     unteamed colour otherwise.
///     <para>
///         A map with no baked bundle on this host draws nothing (<see langword="null" />): the card
///         then shows the landing point as text, the same "never load-bearing" rule the library card's
///         radar background follows (<see cref="ViewModels.Library.MapRadarConverter" />).
///     </para>
///     <para>
///         One instance serves a batch of cards on the Utility Book tab: map bundles are loaded once
///         per map and held until <see cref="Dispose" />. Not thread-safe; the tab VM drives it from
///         one worker.
///     </para>
/// </summary>
public sealed class GrenadeLineupThumbnailRenderer : IDisposable
{
    /// <summary>The card's radar size: small and square, enough to read the spot at a glance.</summary>
    public static readonly SKSizeI Size = new(160, 160);

    private readonly Dictionary<string, LoadedMapAsset?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, LoadedMapAsset?> _loadMapAsset;
    private bool _disposed;

    /// <param name="loadMapAsset">Finds a map's baked bundle; the pipeline's loader in the app, a stub in a test.</param>
    public GrenadeLineupThumbnailRenderer(Func<string, LoadedMapAsset?>? loadMapAsset = null) =>
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

    /// <summary>The PNG for a landing point, or null when the map has no baked bundle on this host.</summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    /// <param name="landing">Where the grenade went off.</param>
    /// <param name="throwerTeam">2 = T, 3 = CT; anything else draws an unteamed marker.</param>
    public byte[]? Render(string map, WorldPoint landing, int throwerTeam)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (AssetFor(map) is not { } asset)
        {
            return null;
        }

        WorldBounds bounds = MapAssetPipeline.RadarBounds(asset);
        PlayerMarker marker = new(0, throwerTeam is 2 or 3 ? throwerTeam : 0, landing.X, landing.Y, landing.Z,
            0, RingState.Team, 1.0, "", true);
        SceneTime time = new(0, 0, 0, 1.0 / 64, true);
        Scene2DFrame frame = new()
        {
            Time = time,
            Markers = [marker],
            Map = new SceneMapInfo
            {
                MapName = map,
                NetworkedBounds = bounds,
                ObservedBounds = bounds,
                Radars = MapAssetPipeline.DescribeRadars(asset)
            }
        };

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor =
            SceneLayerCatalog.CreateSceneStack([SceneLayerIds.Radar, SceneLayerIds.Markers]);
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Purpose = RenderPurpose.Export,
            Camera = ViewportTransform.Fit(Size.Width, Size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY)
        };
        renderer.Levels.SetAuthoritativeFloors(asset.Floors);
        renderer.Levels.RadarBinder = new MapRadarBinder(asset);

        return renderer.RenderPng(frame, in time, Size);
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
