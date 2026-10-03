#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Services.RoundIndex;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     Draws a Result Card's mini-radar: the alive players of one sampled step, from the positions
///     file, through the same headless path <c>dv2d render</c> takes (<see cref="SceneLayerCatalog" />
///     stack, <see cref="HeadlessSceneRenderer" />, the CPU provider, the dark palette) at
///     <see cref="Size" />. The scene is the radar and the marker discs and nothing else: no yaw, no
///     ring state, no label, so the picture shows the arrangement the token matched, byte for byte
///     consistent with it because both come from the same row.
///     <para>
///         <b>A card never opens a demo to draw itself.</b> Measured, a seek from the demo is 0.6 to
///         3.3 s and half a gigabyte of heap per hit; a frame from ten tuples renders in half a
///         millisecond. This class takes tuples only; a hit whose demo has no positions file gets the
///         placeholder, and the only demo open in the Situations tab is the one the user clicked.
///     </para>
///     <para>
///         One instance serves one batch: map bundles are loaded once per map and held until
///         <see cref="Dispose" />, since forty hits are typically ten to twenty demos on a handful of
///         maps. Not thread-safe; the cards VM drives it from one worker.
///     </para>
/// </summary>
public sealed class SituationThumbnailRenderer : IDisposable
{
    /// <summary>The thumbnail size: sixteen by nine at a card's width. The golden is named for it.</summary>
    public static readonly SKSizeI Size = new(320, 180);

    private readonly Dictionary<string, LoadedMapAsset?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, LoadedMapAsset?> _loadMapAsset;
    private bool _disposed;

    /// <param name="loadMapAsset">Finds a map's baked bundle; the pipeline's loader in the app, a stub in a test.</param>
    public SituationThumbnailRenderer(Func<string, LoadedMapAsset?>? loadMapAsset = null) =>
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

    /// <summary>
    ///     The PNG for a hit's matched step, or null when the positions file has no such round or no
    ///     tuple at that step (a step the walk did not sample; the card then says so).
    /// </summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="roundNumber">The hit's round.</param>
    /// <param name="tick">The hit's matched tick, frame clock.</param>
    public byte[]? Render(string map, RoundPositionsDocument positions, int roundNumber, int tick)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(positions);

        Scene2DFrame? frame = BuildFrame(map, positions, roundNumber, tick, AssetFor(map));
        return frame is null ? null : RenderFrame(frame, AssetFor(map));
    }

    /// <summary>
    ///     The scene a thumbnail draws: one marker per alive tuple at the step the tick falls in, team
    ///     from the round's CT slots, the map's radar art and bounds when this host has the bundle.
    ///     Public so a fixture and a golden can be built from the exact frame the card draws.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="roundNumber">The round.</param>
    /// <param name="tick">The matched tick, frame clock.</param>
    /// <param name="asset">The map's bundle, or null to draw on the synthetic grid.</param>
    public static Scene2DFrame? BuildFrame(string map, RoundPositionsDocument positions, int roundNumber, int tick,
        LoadedMapAsset? asset)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(positions);

        if (positions.Round(roundNumber) is not { } round)
        {
            return null;
        }

        IReadOnlyList<RoundPosition> tuples = round.At(positions.StepFor(round, tick));
        if (tuples.Count == 0)
        {
            return null;
        }

        HashSet<int> ct = [.. round.Ct];
        List<PlayerMarker> markers = new(tuples.Count);
        foreach (RoundPosition tuple in tuples)
        {
            // No label: the glyph allowance on the golden is then zero, and a card has no room for
            // initials anyway. No yaw and the team ring, because the row carries neither.
            markers.Add(new PlayerMarker(tuple.Slot, ct.Contains(tuple.Slot) ? 3 : 2, tuple.X, tuple.Y, tuple.Z,
                0, RingState.Team, 1.0, "", true, 0, 0, 0, positions.PlaceOf(tuple.PlaceId)));
        }

        // The fixture time is the matched tick with a discontinuity, so no layer smooths from a
        // previous frame it never saw.
        SceneTime time = new(tick, 0, 0, 1.0 / 64, true);
        return new Scene2DFrame
        {
            Time = time,
            Markers = markers,
            Map = MapInfoFor(map, asset, markers)
        };
    }

    /// <summary>The camera a thumbnail uses: the whole map, the way <c>dv2d render</c> fits a frame with no camera spec.</summary>
    /// <param name="frame">The frame to fit.</param>
    public static ViewportTransform CameraFor(Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        WorldBounds bounds = frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;
        return ViewportTransform.Fit(Size.Width, Size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
    }

    /// <summary>
    ///     Renders a frame the way <c>dv2d render</c> and <c>SceneGoldenTests</c> do: the catalogue's
    ///     radar and marker layers, the dark palette, the export purpose, the bundle's floors and radar
    ///     binder, and the camera as a pin.
    /// </summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="asset">The map's bundle, or null.</param>
    public static byte[] RenderFrame(Scene2DFrame frame, LoadedMapAsset? asset)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor =
            SceneLayerCatalog.CreateSceneStack([SceneLayerIds.Radar, SceneLayerIds.Markers]);
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Purpose = RenderPurpose.Export,
            Camera = CameraFor(frame)
        };
        renderer.Levels.SetAuthoritativeFloors(asset?.Floors);
        renderer.Levels.RadarBinder = asset is null ? null : new MapRadarBinder(asset);

        SceneTime time = frame.Time;
        return renderer.RenderPng(frame, in time, Size);
    }

    private static SceneMapInfo MapInfoFor(string map, LoadedMapAsset? asset, IReadOnlyList<PlayerMarker> markers)
    {
        if (asset is not null)
        {
            WorldBounds bounds = MapAssetPipeline.RadarBounds(asset);
            return new SceneMapInfo
            {
                MapName = map,
                NetworkedBounds = bounds,
                ObservedBounds = bounds,
                Radars = MapAssetPipeline.DescribeRadars(asset)
            };
        }

        // No bundle on this host: the synthetic grid over the players' own extent, padded so a
        // marker never sits on the edge.
        double minX = markers.Min(m => m.WorldX) - 512;
        double minY = markers.Min(m => m.WorldY) - 512;
        double maxX = markers.Max(m => m.WorldX) + 512;
        double maxY = markers.Max(m => m.WorldY) + 512;
        return new SceneMapInfo
        {
            MapName = map,
            ObservedBounds = new WorldBounds(minX, minY, maxX, maxY)
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
