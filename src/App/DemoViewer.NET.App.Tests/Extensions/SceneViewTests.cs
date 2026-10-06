#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Timeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The SDK's <see cref="SceneView" /> and <see cref="SceneTimeline" /> over the app's scene host and
///     timeline: a source's frames drawn and its presses taken first, tools and layers added and removed, an
///     extension's pointer tool guarded, and a timeline built from tracks that seeks inside its clock.
/// </summary>
[NotInParallel]
[Category("Render")]
public class SceneViewTests
{
    private static readonly WorldBounds Mirage = new(-3230, -3410, 1860, 1680);

    [Test]
    public async Task ASourcesFrame_IsDrawn_AndANewOneReplacesIt_WithTheTogglesTheViewFixes()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new();
            source.Publish(Frame(1));
            (Window window, SceneView view) = Mount(source);
            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().Single();

            await Assert.That(host.CurrentSceneFrame).IsSameReferenceAs(source.Frame);
            Scene2DFrame next = Frame(2);
            source.Publish(next);
            Playback2DTimelineHarness.Pump();

            ISceneFrameHost frameHost = host.FrameHost!;
            using (Assert.Multiple())
            {
                await Assert.That(host.CurrentSceneFrame).IsSameReferenceAs(next);
                await Assert.That(frameHost.AnnotationSession).IsSameReferenceAs(source.Ink);
                await Assert.That(frameHost.ShowRadar).IsTrue();
                await Assert.That(frameHost.ShowTrails).IsTrue();
                await Assert.That(frameHost.ShowVision).IsFalse();
                await Assert.That(frameHost.ShowZones).IsFalse();
                await Assert.That(frameHost.ShowViewCones).IsTrue().Because("the source asked for cones");
                await Assert.That(view.Panes.Count).IsGreaterThanOrEqualTo(1);
            }

            view.Source = null;
            await Assert.That(host.FrameHost).IsNull();
            window.Close();
        });
    }

    [Test]
    public async Task APress_ReachesTheSourceFirst_WithItsModifiers_AndARefusedOnePans()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new();
            source.Publish(Frame(1));
            (Window window, SceneView view) = Mount(source);
            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().Single();

            Point down = Playback2DTimelineHarness.ToWindow(host, window, 300, 300);
            Point moved = Playback2DTimelineHarness.ToWindow(host, window, 330, 320);
            source.Take = true;
            window.MouseDown(down, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(moved, RawInputModifiers.Shift);
            using (Assert.Multiple())
            {
                await Assert.That(source.Presses.Count).IsEqualTo(1);
                await Assert.That(source.Presses[0].Modifiers).IsEqualTo(KeyModifiers.Shift);
                await Assert.That(source.Presses[0].Screen.X).IsEqualTo(300).Within(1);
                await Assert.That(host.Router.IsGestureOpen).IsFalse().Because("the source took the press");
            }

            window.MouseUp(moved, MouseButton.Left);
            source.Take = false;
            window.MouseDown(down, MouseButton.Left);
            window.MouseMove(moved);
            await Assert.That(host.Router.IsGestureOpen).IsTrue().Because("a refused press is the pan tool's");
            window.MouseUp(moved, MouseButton.Left);
            window.Close();
        });
    }

    [Test]
    public async Task AnAddedToolAndLayer_JoinTheScene_AndLeaveOnDispose()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new();
            source.Publish(Frame(1));
            (Window window, SceneView view) = Mount(source);
            Scene2DHost host = view.GetVisualDescendants().OfType<Scene2DHost>().Single();

            IDisposable tool = view.AddTool(new InertTool(ToolKind.Token));
            view.SetActiveTool(ToolKind.Token);
            await Assert.That(view.ActiveTool).IsEqualTo(ToolKind.Token);
            tool.Dispose();
            await Assert.That(view.ActiveTool).IsEqualTo(ToolKind.PanZoom).Because("removing the active tool pans");

            IDisposable layer = view.AddLayer("sample.guides", () => new InertLayer("sample.guides"));
            await Assert.That(host.Compositor.Layers.Any(l => l.Id == "sample.guides")).IsTrue();
            layer.Dispose();
            await Assert.That(host.Compositor.Layers.Any(l => l.Id == "sample.guides")).IsFalse();
            window.Close();
        });
    }

    [Test]
    [Arguments(SceneLayerIds.Radar)]
    [Arguments(SceneLayerIds.Annotations)]
    public async Task AddLayer_RefusesTheScenesOwnLayers(string id)
    {
        await HeadlessSession.RunOnUi(() =>
        {
            SceneView view = new();
            Assert.Throws<ArgumentException>(() => view.AddLayer(id, () => new InertLayer(id)));
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task AnExtensionsPointerTool_ThatThrows_IsReported_AndRefusesThePress()
    {
        HostLibraryTests.LibraryTestExtension extension = new();
        ExtensionGuard guard = ExtensionGuard.Standalone(extension);
        GuardedPointerTool tool = new(new ThrowingTool(), guard);
        ToolPointerEvent sample = default;

        bool taken = tool.OnPressed(in sample, null!);
        tool.OnCancelled(null!);

        using (Assert.Multiple())
        {
            await Assert.That(taken).IsFalse();
            await Assert.That(tool.Kind).IsEqualTo(ToolKind.Token);
            await Assert.That(guard.Faults.StateOf(extension.FeatureId).Count).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task AnExtensionsSource_WhoseMembersThrow_ReadsAsEmpty_AndIsReported()
    {
        HostLibraryTests.LibraryTestExtension extension = new();
        ExtensionGuard guard = ExtensionGuard.Standalone(extension);
        HostedSceneView.SourceFrameHost host = new(new ThrowingSource(), guard);

        using (Assert.Multiple())
        {
            await Assert.That(host.CurrentFrame).IsSameReferenceAs(Scene2DFrame.Empty);
            await Assert.That(host.MapAsset).IsNull();
            await Assert.That(host.AnnotationSession).IsNull();
            await Assert.That(host.IsAnnotationsEnabled).IsFalse();
            await Assert.That(host.ShowViewCones).IsFalse();
            await Assert.That(host.TokenEditor).IsNull();
            await Assert.That(guard.Faults.StateOf(extension.FeatureId).Count).IsGreaterThanOrEqualTo(1);
        }

        host.Detach();
    }

    [Test]
    public async Task AnExtensionsOwnTrack_ThatThrows_BuildsNothing_AndIsReported()
    {
        HostLibraryTests.LibraryTestExtension extension = new();
        ExtensionGuard guard = ExtensionGuard.Standalone(extension);
        GuardedTimelineTrack track = new(new ThrowingTrack(), guard);
        FakeTimelineData data = new(100);

        using (Assert.Multiple())
        {
            await Assert.That(track.Id).IsEqualTo(guard.Scope.Id + ".track");
            await Assert.That(track.IsAvailable(data)).IsFalse();
            await Assert.That(track.BuildMarkers(data)).IsEmpty();
            await Assert.That(track.BuildBands(data)).IsEmpty();
            await Assert.That(guard.Faults.StateOf(extension.FeatureId).Count).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task ATimeline_BuildsItsTracks_SeeksInsideItsClock_AndClearsOnNull()
    {
        using SceneTimeline timeline = new();
        timeline.RegisterTrack(new FixedTrack([10, 40]));
        List<int> seeks = [];
        timeline.SeekRequested += seeks.Add;

        timeline.RequestSeek(5);
        await Assert.That(seeks).IsEmpty().Because("no clock yet");

        timeline.Rebuild(new FakeTimelineData(100));
        timeline.RequestSeek(500);
        timeline.RequestSeek(-3);
        using (Assert.Multiple())
        {
            await Assert.That(timeline.TotalFrames).IsEqualTo(100);
            await Assert.That(timeline.Markers.Select(m => m.FrameIndex)).IsEquivalentTo([10, 40]);
            await Assert.That(seeks).IsEquivalentTo([99, 0]);
        }

        timeline.Rebuild(null);
        await Assert.That(timeline.TotalFrames).IsEqualTo(0);
        await Assert.That(timeline.Markers).IsEmpty();
    }

    private static (Window Window, SceneView View) Mount(ISceneSource source)
    {
        SceneView view = new() { Source = source };
        Window window = new() { Width = 1000, Height = 700, Content = view };
        window.Show();
        Playback2DTimelineHarness.Pump();
        view.Fit();
        Playback2DTimelineHarness.Pump();
        return (window, view);
    }

    private static Scene2DFrame Frame(int tick) => new()
    {
        Time = new SceneTime(tick, tick, tick / 64.0, 1 / 64.0, true),
        Markers =
        [
            new PlayerMarker(0, 2, -800f, 600f, 64f, 0f, RingState.Team, 1, "T1", true, SteamId: 1),
            new PlayerMarker(5, 3, 900f, -500f, 64f, 0f, RingState.Team, 1, "CT1", true, SteamId: 6)
        ],
        Map = new SceneMapInfo { MapName = "de_mirage", ObservedBounds = Mirage, NetworkedBounds = Mirage }
    };

    private sealed class FakeSource : ISceneSource
    {
        public bool Take { get; set; }

        public List<ScenePress> Presses { get; } = [];

        public Scene2DFrame Frame { get; private set; } = Scene2DFrame.Empty;

        public IMapAsset? MapAsset => null;

        public AnnotationSession? Ink { get; } = new(new AnnotationDocument());

        public bool ShowViewCones => true;

        public event Action? FrameUpdated;

        public bool OnPress(ScenePress press)
        {
            Presses.Add(press);
            return Take;
        }

        public void Publish(Scene2DFrame frame)
        {
            Frame = frame;
            FrameUpdated?.Invoke();
        }
    }

    private sealed class InertTool(ToolKind kind) : IPointerTool
    {
        public ToolKind Kind => kind;

        public bool OnPressed(in ToolPointerEvent e, IToolServices s) => false;

        public void OnMoved(in ToolPointerEvent e, IToolServices s)
        {
        }

        public void OnReleased(in ToolPointerEvent e, IToolServices s)
        {
        }

        public void OnCancelled(IToolServices s)
        {
        }
    }

    private sealed class ThrowingTool : IPointerTool
    {
        public ToolKind Kind => ToolKind.Token;

        public bool OnPressed(in ToolPointerEvent e, IToolServices s) => throw new InvalidOperationException("press");

        public void OnMoved(in ToolPointerEvent e, IToolServices s) => throw new InvalidOperationException("move");

        public void OnReleased(in ToolPointerEvent e, IToolServices s) => throw new InvalidOperationException("release");

        public void OnCancelled(IToolServices s) => throw new InvalidOperationException("cancel");
    }

    private sealed class InertLayer(string id) : ISceneLayer
    {
        public string Id => id;

        public LayerSlot Slot => LayerSlot.Overlay;

        public int Order => 0;

        public LayerCacheHint Cache => LayerCacheHint.Dynamic;

        public bool IsEnabled { get; set; } = true;

        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingSource : ISceneSource
    {
        public Scene2DFrame Frame => throw new InvalidOperationException("frame");

        public IMapAsset? MapAsset => throw new InvalidOperationException("map");

        public AnnotationSession? Ink => throw new InvalidOperationException("ink");

        public bool ShowViewCones => throw new InvalidOperationException("cones");

        public ITokenEditor? TokenEditor => throw new InvalidOperationException("tokens");

        public event Action? FrameUpdated
        {
            add { }
            remove { }
        }
    }

    private sealed class ThrowingTrack : ITimelineTrack
    {
        public string Id => throw new InvalidOperationException("id");

        public string DisplayName => throw new InvalidOperationException("name");

        public bool IsAvailable(ITimelineData data) => throw new InvalidOperationException("available");

        public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => throw new InvalidOperationException("markers");

        public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => throw new InvalidOperationException("bands");

        public event Action? MarkersChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class FixedTrack(int[] frames) : ITimelineTrack
    {
        public string Id => "sample.fixed";

        public string DisplayName => "Fixed";

        public bool IsAvailable(ITimelineData data) => true;

        public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) =>
            [.. frames.Select(f => new TimelineMarker(Id, f, f * 2, TimelineMarkerKind.Round, "•", "", 0))];

        public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => [];

        public event Action? MarkersChanged
        {
            add { }
            remove { }
        }
    }
}
