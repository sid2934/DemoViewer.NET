#region

using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The UI kit's map surface over the app: a shipped map's art by name, game icons, and a still picture that
///     is the headless renderer's, pixel for pixel.
/// </summary>
[NotInParallel]
[Category("Render")]
public class MapAssetsTests
{
    private const string Map = "de_mirage";
    private static readonly SKSizeI Size = new(320, 180);

    [Test]
    public async Task AShippedMap_LoadsItsFloorsRadarsAndPlaces_AndAnUnknownOneIsNull()
    {
        using IMapAsset? map = MapAssets.TryLoad(Map);
        await Assert.That(map).IsNotNull().Because("the baked de_mirage bundle ships with the app");

        using (Assert.Multiple())
        {
            await Assert.That(map!.Floors.Count).IsGreaterThanOrEqualTo(1);
            await Assert.That(map.DescribeRadars().Count).IsGreaterThanOrEqualTo(1);
            await Assert.That(map.DescribeRadars().All(r => r.Image is not null)).IsTrue();
            await Assert.That(map.RadarBounds.MaxX).IsGreaterThan(map.RadarBounds.MinX);
            await Assert.That(map.Zones).IsNotNull();
            await Assert.That(MapAssets.TryLoad("de_nosuchmap")).IsNull();
            await Assert.That(MapAssets.TryLoad(" ")).IsNull();
        }
    }

    [Test]
    public async Task APicture_IsTheHeadlessRenderersPicture()
    {
        using LoadedMapAsset asset = MapAssetPipeline.TryLoad(Map)!;
        IMapAsset map = asset;
        WorldBounds bounds = map.RadarBounds;
        Scene2DFrame frame = new()
        {
            Time = new SceneTime(0, 0, 0, 1.0 / 64, true),
            Markers = [new PlayerMarker(0, 2, (float)((bounds.MinX + bounds.MaxX) / 2), (float)((bounds.MinY + bounds.MaxY) / 2), 0, 0, RingState.Team, 1.0, "", true)],
            Map = new SceneMapInfo
            {
                MapName = Map, NetworkedBounds = bounds, ObservedBounds = bounds, Radars = map.DescribeRadars()
            }
        };
        ViewportTransform camera = ViewportTransform.Fit(Size.Width, Size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);

        byte[] viaKit = MapAssets.RenderPng(frame, map, [SceneLayerIds.Radar, SceneLayerIds.Markers], camera, Size);

        byte[] direct;
        using (CpuSurfaceProvider provider = new())
        using (SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack([SceneLayerIds.Radar, SceneLayerIds.Markers]))
        using (HeadlessSceneRenderer renderer = new(provider, compositor)
               {
                   Palette = ScenePalette.Dark, Purpose = RenderPurpose.Export, Camera = camera
               })
        {
            renderer.Levels.SetAuthoritativeFloors(asset.Floors);
            renderer.Levels.RadarBinder = new MapRadarBinder(asset);
            SceneTime time = frame.Time;
            direct = renderer.RenderPng(frame, in time, Size);
        }

        using SKBitmap a = SKBitmap.Decode(viaKit);
        using SKBitmap b = SKBitmap.Decode(direct);
        await Assert.That(a.Width).IsEqualTo(Size.Width);
        await Assert.That(a.Bytes.SequenceEqual(b.Bytes)).IsTrue();
    }

    [Test]
    public async Task MapIcons_FindsGameIcons_AndAnUnknownKeyIsNull()
    {
        using MapIcons icons = new();

        using (Assert.Multiple())
        {
            await Assert.That(icons.Lookup("equipment/awp", 12f)).IsNotNull();
            await Assert.That(icons.Lookup("equipment/not_a_weapon", 12f)).IsNull();
        }
    }
}
