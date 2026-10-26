#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Tools;
using SkiaSharp;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The embeddable <see cref="MapView" /> over the app's renderer: a map by name, layers and a primary
///     tool added by composition, the refused-press pan, and one bundle decode shared by every view of a map.
/// </summary>
[NotInParallel]
[Category("Render")]
public class MapViewTests
{
    private const string Map = "de_mirage";

    // A map no other test in this assembly takes a hold on, so the registry's decode count is readable.
    private const string Shared = "de_train";

    [Test]
    public async Task AMapByName_BindsTheBundle_AndArrangesItsFloors()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, MapView view) = Mount();
            int bound = 0;
            view.MapBound += (_, _) => bound++;
            view.MapName = Map;
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame();

            using (Assert.Multiple())
            {
                await Assert.That(view.HasMap).IsTrue();
                await Assert.That(bound).IsEqualTo(1);
                await Assert.That(view.Space!.Levels.Count).IsGreaterThanOrEqualTo(1);
                await Assert.That(view.Panes.Count).IsGreaterThanOrEqualTo(1);
                await Assert.That(view.PaneAt(new Point(400, 300))).IsNotNull();
            }

            view.MapName = "de_nosuchmap";
            await Assert.That(view.HasMap).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task AnAddedLayer_Draws_IsRebuiltFromItsFactory_AndLeavesOnDispose()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, MapView view) = Mount();
            view.MapName = Map;
            CountingLayer? last = null;
            int built = 0;
            IDisposable added = view.AddLayer("sample.dots", () =>
            {
                built++;
                return last = new CountingLayer("sample.dots");
            });

            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame();
            await Assert.That(last!.Renders).IsGreaterThan(0);

            // Leaving the tree releases the scene; coming back builds it again from the factory.
            window.Content = null;
            Playback2DTimelineHarness.Pump();
            window.Content = view;
            Playback2DTimelineHarness.Pump();
            await Assert.That(built).IsEqualTo(2);

            added.Dispose();
            int before = last.Renders;
            view.Invalidate();
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame();
            await Assert.That(last.Renders).IsEqualTo(before);
            window.Close();
        });
    }

    /// <summary>
    ///     A second layer under one id is refused rather than replacing the first. A replacement left the
    ///     first owner holding a handle that removed nothing.
    /// </summary>
    [Test]
    public async Task AddLayer_RefusesAnIdTheViewAlreadyHolds()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, MapView view) = Mount();
            view.MapName = Map;
            CountingLayer? first = null;
            IDisposable added = view.AddLayer("sample.dots", () => first = new CountingLayer("sample.dots"));
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame();

            Assert.Throws<ArgumentException>(() => view.AddLayer("sample.dots", () => new CountingLayer("sample.dots")));

            int before = first!.Renders;
            view.Invalidate();
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame();
            await Assert.That(first.Renders).IsGreaterThan(before).Because("the refused add left the first layer drawing");

            // The refused add must leave the first handle in charge of the id: a factory it had recorded
            // over the first one would make this dispose a no-op and the layer would never leave.
            added.Dispose();
            before = first.Renders;
            view.Invalidate();
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame();
            await Assert.That(first.Renders).IsEqualTo(before);
            window.Close();
        });
    }

    // A data source rather than [Arguments]: the ids are static readonly, so an attribute argument
    // cannot take them, and a literal here would stop referencing the type this test is about.
    public static IEnumerable<string> ViewOwnLayerIds()
    {
        yield return SceneLayerIds.Radar;
        yield return SceneLayerIds.FloorLabel;
    }

    [Test]
    [MethodDataSource(nameof(ViewOwnLayerIds))]
    public async Task AddLayer_RefusesTheViewsOwnLayers(string id)
    {
        await HeadlessSession.RunOnUi(() =>
        {
            MapView view = new();
            Assert.Throws<ArgumentException>(() => view.AddLayer(id, () => new CountingLayer(id)));
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ThePrimaryTool_GetsThePress_ARefusedLeftPressPans()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, MapView view) = Mount();
            view.MapName = Map;
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame();

            RecordingTool tool = new();
            using IDisposable added = view.AddTool(tool);
            view.SetPrimaryTool(tool);
            window.MouseDown(new Point(400, 300), MouseButton.Left);
            window.MouseMove(new Point(420, 310));
            window.MouseUp(new Point(420, 310), MouseButton.Left);
            await Assert.That(tool.Calls).IsEquivalentTo(["press", "move", "release"], CollectionOrdering.Matching);

            tool.Accept = false;
            LevelPane pane = view.PaneAt(new Point(400, 300))!;
            (double x0, double y0) = pane.Camera.Current.WorldToScreen(0, 0);
            window.MouseDown(new Point(400, 300), MouseButton.Left);
            window.MouseMove(new Point(460, 340));
            window.MouseUp(new Point(460, 340), MouseButton.Left);
            (double x1, double y1) = pane.Camera.Current.WorldToScreen(0, 0);

            await Assert.That(Math.Abs(x1 - x0) + Math.Abs(y1 - y0)).IsGreaterThan(10)
                .Because("a press the primary tool refuses pans the map");
            Assert.Throws<ArgumentException>(() => view.SetPrimaryTool(new RecordingTool()));
            window.Close();
        });
    }

    [Test]
    public async Task TwoViewsOfOneMap_ShareOneBundle_AndTheLastToLeaveReleasesIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // Deltas, not absolutes: the registry is process-wide and a tab elsewhere in the suite may
            // hold this map too.
            int before = MapBundleLeases.HoldersOf(Map);
            MapView first = new() { MapName = Map };
            MapView second = new() { MapName = Map };
            Window window = new() { Width = 800, Height = 600, Content = new StackPanel { Children = { first, second } } };
            window.Show();
            Playback2DTimelineHarness.Pump();

            await Assert.That(MapBundleLeases.HoldersOf(Map)).IsEqualTo(before + 2);
            window.Content = null;
            Playback2DTimelineHarness.Pump();
            await Assert.That(MapBundleLeases.HoldersOf(Map)).IsEqualTo(before);
            window.Close();
        });
    }

    /// <summary>
    ///     What the registry is for: a 2D Playback tab, a <see cref="MapView" /> an extension embeds, and an
    ///     extension's own hold taken off the UI thread are three holders of ONE decode, reading one bundle.
    ///     On de_train, which no other test in this assembly holds, so the decode count means something.
    /// </summary>
    [Test]
    public async Task ATabAndTwoExtensionHolders_ShareOneDecode()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            await Assert.That(MapBundleLeases.HoldersOf(Shared)).IsEqualTo(0)
                .Because("the decode count only means something while nobody else holds the map");
            int decodes = MapBundleLeases.Decodes;
            int radarDecodes = MapAssetPipeline.RadarDecodes;

            Playback2DFakeContext ctx = new()
            {
                MapName = Shared,
                Gate = new FakeModuleFeatureGate()
            };
            ctx.AddPlayer(0, "Alpha", 2);
            Playback2DTabViewModel tab = new();
            tab.OnActivated(ctx);

            MapView view = new() { MapName = Shared };
            Window window = new() { Width = 800, Height = 600, Content = view };
            window.Show();
            Playback2DTimelineHarness.Pump();

            // The third holder off the UI thread, the way a thumbnail batch on a worker takes one.
            IMapAssetLease? offThread = null;
            Thread worker = new(() => offThread = MapAssets.Acquire(Shared));
            worker.Start();
            worker.Join();

            using (Assert.Multiple())
            {
                await Assert.That(offThread).IsNotNull();
                await Assert.That(MapBundleLeases.HoldersOf(Shared)).IsEqualTo(3);
                await Assert.That(MapBundleLeases.Decodes - decodes).IsEqualTo(1)
                    .Because("three holders of one map decode it once");
                await Assert.That(MapAssetPipeline.RadarDecodes - radarDecodes).IsEqualTo(1)
                    .Because("and the two of them that draw share one radar decode");
                await Assert.That(tab.MapAsset).IsSameReferenceAs(((MapBundleLease)offThread!).Bundle)
                    .Because("the tab and the extension read the same decode");
            }

            offThread!.Dispose();
            window.Content = null;
            Playback2DTimelineHarness.Pump();
            tab.Dispose();

            await Assert.That(MapBundleLeases.HoldersOf(Shared)).IsEqualTo(0)
                .Because("the last hold given back drops the entry");
            window.Close();
        });
    }

    /// <summary>
    ///     What the lazy radar decode is for: a holder that only wants floors, bounds or places opens the
    ///     bundle and decodes no pixels. The zone sources hold one lease per cached map and never draw.
    /// </summary>
    [Test]
    public async Task AHolderThatDrawsNothing_OpensTheBundle_AndDecodesNoRadarArt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            await Assert.That(MapBundleLeases.HoldersOf(Shared)).IsEqualTo(0)
                .Because("the decode counts only mean something while nobody else holds the map");
            int decodes = MapBundleLeases.Decodes;
            int radarDecodes = MapAssetPipeline.RadarDecodes;

            using IMapAssetLease? lease = MapAssets.Acquire(Shared);
            await Assert.That(lease).IsNotNull();

            using (Assert.Multiple())
            {
                await Assert.That(lease!.Floors.Count).IsGreaterThanOrEqualTo(1);
                await Assert.That(lease.RadarBounds.MaxX).IsGreaterThan(lease.RadarBounds.MinX);
                await Assert.That(lease.Zones).IsNotNull();
                await Assert.That(MapBundleLeases.Decodes - decodes).IsEqualTo(1)
                    .Because("the bundle manifest was read");
                await Assert.That(MapAssetPipeline.RadarDecodes - radarDecodes).IsEqualTo(0)
                    .Because("floors, bounds and places are all in the manifest and the zones file");
            }

            lease.DescribeRadars();
            await Assert.That(MapAssetPipeline.RadarDecodes - radarDecodes).IsEqualTo(1)
                .Because("asking for the art is what decodes it");
        });
    }

    private static (Window Window, MapView View) Mount()
    {
        MapView view = new();
        Window window = new() { Width = 800, Height = 600, Content = view };
        window.Show();
        Playback2DTimelineHarness.Pump();
        return (window, view);
    }

    private sealed class CountingLayer(string id) : ISceneLayer
    {
        public int Renders { get; private set; }

        public string Id => id;

        public LayerSlot Slot => LayerSlot.Overlay;

        public int Order => 0;

        public LayerCacheHint Cache => LayerCacheHint.Dynamic;

        public bool IsEnabled { get; set; } = true;

        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx) => Renders++;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingTool : IMapTool
    {
        public bool Accept { get; set; } = true;

        public List<string> Calls { get; } = [];

        public bool OnPressed(in MapToolEvent e, IMapToolContext context)
        {
            Calls.Add("press");
            return Accept;
        }

        public void OnMoved(in MapToolEvent e, IMapToolContext context) => Calls.Add("move");

        public void OnReleased(in MapToolEvent e, IMapToolContext context) => Calls.Add("release");

        public void OnCancelled(IMapToolContext context) => Calls.Add("cancel");
    }
}
