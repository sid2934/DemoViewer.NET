#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Tools;
using SkiaSharp;

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

    [Test]
    [Arguments(SceneLayerIds.Radar)]
    [Arguments(SceneLayerIds.FloorLabel)]
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
            await Assert.That(tool.Calls).IsEquivalentTo(["press", "move", "release"]);

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
            MapView first = new() { MapName = Map };
            MapView second = new() { MapName = Map };
            Window window = new() { Width = 800, Height = 600, Content = new StackPanel { Children = { first, second } } };
            window.Show();
            Playback2DTimelineHarness.Pump();

            await Assert.That(MapBundleLeases.HoldersOf(Map)).IsEqualTo(2);
            window.Content = null;
            Playback2DTimelineHarness.Pump();
            await Assert.That(MapBundleLeases.HoldersOf(Map)).IsEqualTo(0);
            window.Close();
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
