#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Overlay;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The Overlay View's Core half: the document, the heatmap layer's determinism, its per-floor pane
///     assignment and per-side tint, the fixture format's round trip, and the catalog's opt-in
///     registration of the layer.
/// </summary>
public class OverlayViewTests
{
    private static readonly WorldBounds Bounds = new(-2000, -2000, 2000, 2000);

    [Test]
    public async Task TheDocument_IsReplacedWhole_AndBumpsOnEveryChange()
    {
        OverlayDocument document = new();
        int changes = 0;
        document.Changed += () => changes++;

        using (Assert.Multiple())
        {
            await Assert.That(document.IsEmpty).IsTrue();
            await Assert.That(document.Version).IsEqualTo(0);
        }

        document.Replace("de_nuke", [new OverlayPoint(1, 2, 3, QuerySide.Ct), new OverlayPoint(4, 5, 6, QuerySide.T)], 7);
        using (Assert.Multiple())
        {
            await Assert.That(document.Count).IsEqualTo(2);
            await Assert.That(document.StateCount).IsEqualTo(7);
            await Assert.That(document.MapName).IsEqualTo("de_nuke");
            await Assert.That(document.Points[1].Side).IsEqualTo(QuerySide.T);
            await Assert.That(changes).IsEqualTo(1);
        }

        document.Replace("de_nuke", [new OverlayPoint(0, 0, 0, QuerySide.Ct)], 1);
        await Assert.That(document.Count).IsEqualTo(1).Because("a replacement is not an append");
        await Assert.That(document.Version).IsEqualTo(2);

        document.Clear();
        using (Assert.Multiple())
        {
            await Assert.That(document.IsEmpty).IsTrue();
            await Assert.That(document.MapName).IsEqualTo("");
            await Assert.That(document.StateCount).IsEqualTo(0);
            await Assert.That(changes).IsEqualTo(3);
        }

        document.Clear();
        await Assert.That(changes).IsEqualTo(3).Because("clearing an empty document changes nothing");
    }

    /// <summary>
    ///     Two nav floors, a CT cluster on the upper one and a T cluster on the lower one at another
    ///     spot: each cluster warms its own pane in its side's colour and nothing else, and the whole
    ///     render is the same bytes twice.
    /// </summary>
    [Test]
    [Category("Render")]
    public async Task TheLayer_IsDeterministic_TintsBySide_AndKeepsEachFloorsPointsOnItsPane()
    {
        OverlayDocument document = new();
        List<OverlayPoint> points = [];
        for (int i = 0; i < 12; i++)
        {
            points.Add(new OverlayPoint(i * 6 - 33, 0, -300 + 40, QuerySide.Ct)); // upper storey
            points.Add(new OverlayPoint(900, 900 + i * 6 - 33, -1000 + 40, QuerySide.T)); // lower storey
        }

        document.Replace("synthetic", points, 12);

        byte[] first = Render(document, out IReadOnlyList<LevelPaneSnapshot> panes, out ScenePalette palette);
        byte[] second = Render(document, out _, out _);
        await Assert.That(second).IsEquivalentTo(first).Because("one document under one camera is one set of bytes");

        using SKBitmap bitmap = SKBitmap.Decode(first);
        await Assert.That(panes.Count).IsEqualTo(2);
        LevelPaneSnapshot lower = panes.Single(p => p.Level.ZMin < -500);
        LevelPaneSnapshot upper = panes.Single(p => p.Level.ZMin >= -500);

        SKColor ctSpot = Probe(bitmap, upper, 0, 0);
        SKColor tSpot = Probe(bitmap, lower, 900, 900);
        using (Assert.Multiple())
        {
            await Assert.That(Near(ctSpot, palette.Background)).IsFalse().Because("the CT cluster warms the upper pane");
            await Assert.That(Distance(ctSpot, palette.TeamCt)).IsLessThan(Distance(ctSpot, palette.TeamT))
                .Because("and in the CT colour");
            await Assert.That(Near(tSpot, palette.Background)).IsFalse().Because("the T cluster warms the lower pane");
            await Assert.That(Distance(tSpot, palette.TeamT)).IsLessThan(Distance(tSpot, palette.TeamCt))
                .Because("and in the T colour");
            await Assert.That(Near(Probe(bitmap, lower, 0, 0), palette.Background)).IsTrue()
                .Because("the upper storey's points never reach the lower pane");
            await Assert.That(Near(Probe(bitmap, upper, 900, 900), palette.Background)).IsTrue()
                .Because("nor the lower storey's the upper pane");
            await Assert.That(Near(Probe(bitmap, upper, -900, -900), palette.Background)).IsTrue()
                .Because("a spot far from every point stays the map");
        }
    }

    [Test]
    [Category("Render")]
    public async Task AnEmptyDocument_DrawsNothing()
    {
        OverlayDocument document = new();
        byte[] withLayer = Render(document, out _, out _);

        using CpuSurfaceProvider provider = new();
        using SceneCompositor bare = new();
        using HeadlessSceneRenderer renderer = Renderer(provider, bare);
        SceneTime time = new(0, 0, 0, 1 / 64.0, true);
        byte[] withoutLayer = renderer.RenderPng(Frame(), in time, renderer.Size);

        await Assert.That(withLayer).IsEquivalentTo(withoutLayer);
    }

    [Test]
    public async Task TheFixtureStore_RoundTrips_AndReadsAsFarAsItParses()
    {
        OverlayDocument document = new();
        document.Replace("de_nuke",
        [
            new OverlayPoint(620, -420, -416, QuerySide.Ct),
            new OverlayPoint(1420.5f, -760.25f, -700, QuerySide.T),
            new OverlayPoint(-1300, -900, -416, QuerySide.Ct)
        ], 2);

        using MemoryStream first = new();
        OverlayFixtureStore.Write(document, first);
        first.Position = 0;
        OverlayDocument again = OverlayFixtureStore.Read(first);

        using (Assert.Multiple())
        {
            await Assert.That(again.MapName).IsEqualTo("de_nuke");
            await Assert.That(again.StateCount).IsEqualTo(2);
            // Written side by side: the CT points first, then the T points, each in its own order.
            await Assert.That(again.Points).IsEquivalentTo(
            [
                new OverlayPoint(620, -420, -416, QuerySide.Ct),
                new OverlayPoint(-1300, -900, -416, QuerySide.Ct),
                new OverlayPoint(1420.5f, -760.25f, -700, QuerySide.T)
            ]);
        }

        using MemoryStream second = new();
        OverlayFixtureStore.Write(again, second);
        await Assert.That(second.ToArray()).IsEquivalentTo(first.ToArray()).Because("a fixture round-trips byte for byte");
        string text = System.Text.Encoding.UTF8.GetString(first.ToArray());
        await Assert.That(text).DoesNotContain("\r\n");
        await Assert.That(text).Contains("    [620, -420, -416]").Because("one point per line, the shape a diff can read");

        const string partial = """
                               {"schemaVersion":"dvoverlay/1","map":"de_nuke","states":3,
                                "ct":[[1,2,3],[4,5]],"t":[[7,8,9,10]]}
                               """;
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(partial));
        OverlayDocument tolerant = OverlayFixtureStore.Read(stream);
        await Assert.That(tolerant.Count).IsEqualTo(2).Because("a tuple short of three numbers is dropped, not fatal");
        await Assert.That(tolerant.Points[1]).IsEqualTo(new OverlayPoint(7, 8, 9, QuerySide.T));

        using MemoryStream empty = new(System.Text.Encoding.UTF8.GetBytes("""{"schemaVersion":"dvoverlay/1","map":"de_nuke","states":0}"""));
        await Assert.That(OverlayFixtureStore.Read(empty).IsEmpty).IsTrue();
    }

    [Test]
    public async Task TheCatalog_RegistersTheOverlayLayer_OnlyWhenNamedAndFed()
    {
        await Assert.That(SceneLayerCatalog.SceneStackIds).Contains(SceneLayerIds.Overlay);
        await Assert.That(SceneLayerIds.OptIn).Contains(SceneLayerIds.Overlay);

        OverlayDocument document = new();
        using SceneCompositor byDefault = SceneLayerCatalog.CreateSceneStack(overlay: document);
        using SceneCompositor named = SceneLayerCatalog.CreateSceneStack(["radar", "overlay"], overlay: document);
        using SceneCompositor starved = SceneLayerCatalog.CreateSceneStack(["radar", "overlay"]);

        using (Assert.Multiple())
        {
            await Assert.That(byDefault.Find(SceneLayerIds.Overlay)).IsNull().Because("opt-in: absent unless named");
            await Assert.That(named.Find(SceneLayerIds.Overlay)).IsTypeOf<OverlayHeatmapLayer>();
            await Assert.That(((OverlayHeatmapLayer)named.Find(SceneLayerIds.Overlay)!).Document).IsSameReferenceAs(document);
            await Assert.That(starved.Find(SceneLayerIds.Overlay)).IsNull().Because("named with nothing to draw is skipped, like the ink");
        }

        // Under the tokens and over the ink, so a token being placed sits on the heat rather than under it.
        using OverlayHeatmapLayer layer = new(document);
        using QueryTokenLayer tokens = new(new QueryCanvasDocument());
        await Assert.That(layer.Slot).IsEqualTo(tokens.Slot);
        await Assert.That(layer.Order).IsLessThan(tokens.Order);
        await Assert.That(layer.Order).IsGreaterThan(100);
        await Assert.That(layer.Cache).IsEqualTo(LayerCacheHint.PerCamera);
    }

    private static byte[] Render(OverlayDocument document, out IReadOnlyList<LevelPaneSnapshot> panes,
        out ScenePalette palette)
    {
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = new();
        compositor.Add(new OverlayHeatmapLayer(document));
        using HeadlessSceneRenderer renderer = Renderer(provider, compositor);

        SceneTime time = new(0, 0, 0, 1 / 64.0, true);
        byte[] png = renderer.RenderPng(Frame(), in time, renderer.Size);
        panes = renderer.LastSubmission.Panes;
        palette = renderer.Palette;
        return png;
    }

    private static HeadlessSceneRenderer Renderer(CpuSurfaceProvider provider, SceneCompositor compositor)
    {
        HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Size = new SKSizeI(400, 400)
        };
        renderer.Levels.SetAuthoritativeFloors([new FloorSlice(-1000, -300), new FloorSlice(-300, 500)]);
        return renderer;
    }

    private static Scene2DFrame Frame() => new()
    {
        Map = new SceneMapInfo
        {
            MapName = "synthetic",
            NetworkedBounds = Bounds,
            ObservedBounds = Bounds
        }
    };

    private static SKColor Probe(SKBitmap bitmap, LevelPaneSnapshot pane, float worldX, float worldY)
    {
        (double sx, double sy) = pane.Transform.WorldToScreen(worldX, worldY);
        int x = (int)Math.Round(sx + pane.ViewportRect.Left);
        int y = (int)Math.Round(sy + pane.ViewportRect.Top);
        return bitmap.GetPixel(x, y);
    }

    private static bool Near(SKColor actual, SKColor expected) => Distance(actual, expected) <= 9;

    private static int Distance(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
}
