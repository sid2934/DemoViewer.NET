#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Tools;
using SkiaSharp;
using SdkP = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Scene layers and map tools an extension adds through the SDK playback surface: filed under the
///     extension's own id root, guarded, and handed only what the compositor and the press give them.
/// </summary>
[NotInParallel]
public class SdkPlaybackSurfaceSceneTests
{
    private static readonly MapLevel Floor = new() { Id = MapSpace.IdForZMin(-500), Name = "upper", ZMin = -500, ZMax = 100_000 };

    private static (Playback2DTabViewModel Vm, Playback2DSurface Surface) Surface(Func<Scene2DFrame>? frame = null)
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        return (vm, new Playback2DSurface(vm.Timeline, () => [Floor], _ => true, frame ?? (() => Scene2DFrame.Empty)));
    }

    [Test]
    public async Task AddLayer_FilesTheLayerUnderTheExtensionsRoot_AndDisposingRemovesIt()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);

        IDisposable added = surface.AddLayer("heat", () => new RecordingLayer("playback2d.radar"));
        string filed = inner.Layers.Single().Key;
        ISceneLayer built = inner.Layers.Single().Value();

        using (Assert.Multiple())
        {
            await Assert.That(filed).IsEqualTo("ext.pack.fake.heat");
            await Assert.That(built.Id).IsEqualTo(filed).Because("the layer's own id is never used, so it cannot claim a host id");
            await Assert.That(filed.StartsWith("playback2d.", StringComparison.Ordinal) || filed.StartsWith("hud.", StringComparison.Ordinal))
                .IsFalse();
        }

        added.Dispose();
        await Assert.That(inner.Layers).IsEmpty();
        vm.Dispose();
    }

    [Test]
    [Arguments("")]
    [Arguments("has space")]
    [Arguments("slash/es")]
    public async Task AddLayer_RefusesAnIdOutsideTheIdAlphabet(string id)
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);

        Assert.Throws<ArgumentException>(() => surface.AddLayer(id, () => new RecordingLayer("x")));
        await Assert.That(inner.Layers).IsEmpty();
        vm.Dispose();
    }

    [Test]
    public async Task ALayerThatThrowsInRender_LeavesTheCanvasAsItFoundIt_StopsDrawing_AndCounts()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);
        RecordingLayer layer = new("x") { ThrowInRender = true };
        using IDisposable _ = surface.AddLayer("x", () => layer);
        ISceneLayer guarded = inner.Layers.Single().Value();

        using SKSurface target = SKSurface.Create(new SKImageInfo(16, 16));
        SKCanvas canvas = target.Canvas;
        int saves = canvas.SaveCount;
        SKMatrix before = canvas.TotalMatrix;
        guarded.Render(canvas, default);

        using (Assert.Multiple())
        {
            await Assert.That(canvas.SaveCount).IsEqualTo(saves);
            await Assert.That(canvas.TotalMatrix).IsEqualTo(before);
            await Assert.That(guarded.IsEnabled).IsFalse().Because("a layer that threw stops drawing for the session");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
            await Assert.That(guarded.Advance(default, Scene2DFrame.Empty)).IsFalse();
            await Assert.That(layer.Renders).IsEqualTo(1);
        }

        guarded.Render(canvas, default);
        await Assert.That(layer.Renders).IsEqualTo(1);
        vm.Dispose();
    }

    [Test]
    public async Task ALayerThatLeavesTheCanvasTransformed_IsRestoredAfterItsRender()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        using IDisposable _ = surface.AddLayer("x", () => new RecordingLayer("x") { LeaveTransformed = true });
        ISceneLayer guarded = inner.Layers.Single().Value();

        using SKSurface target = SKSurface.Create(new SKImageInfo(16, 16));
        SKMatrix before = target.Canvas.TotalMatrix;
        guarded.Render(target.Canvas, default);

        await Assert.That(target.Canvas.TotalMatrix).IsEqualTo(before);
        vm.Dispose();
    }

    [Test]
    public async Task AThrowingLayerFactory_BuildsALayerThatDrawsNothing()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);
        using IDisposable _ = surface.AddLayer("x", () => throw new InvalidOperationException("no layer"));

        ISceneLayer built = inner.Layers.Single().Value();

        await Assert.That(built.Id).IsEqualTo("ext.pack.fake.x");
        await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
        vm.Dispose();
    }

    [Test]
    public async Task AddTool_IsGuarded_AThrowingPressIsARefusedOne_AndDisposingRemovesIt()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(inner, rig.Guard);
        ThrowingTool tool = new();

        IDisposable added = surface.AddTool(tool);
        IMapTool hosted = inner.Tools.Single();
        bool taken = hosted.OnPressed(new MapToolEvent { Button = MapToolButton.Left }, new NullContext());

        using (Assert.Multiple())
        {
            await Assert.That(taken).IsFalse();
            await Assert.That(tool.Presses).IsEqualTo(1);
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
        }

        added.Dispose();
        await Assert.That(inner.Tools).IsEmpty();
        vm.Dispose();
    }

    [Test]
    public async Task AToolbarItemAndAPress_SeeTheFrameOnScreen()
    {
        Scene2DFrame shown = new() { Time = new SceneTime(640, 10, 10, 1, false) };
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface(() => shown);
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        Scene2DFrame? ranWith = null;
        Scene2DFrame? pressedWith = null;
        using IDisposable item = surface.AddToolbarItem(new SdkP.ToolbarItem("x", "X", "", moment =>
        {
            ranWith = moment.Frame;
            return true;
        }, "x.run"));
        using IDisposable pre = surface.AddPointerPreHandler(p =>
        {
            pressedWith = p.Frame;
            return false;
        });

        inner.TryExecute("x.run");
        inner.TryHandlePointerPress(new ScenePointer(Floor, 1, 2, SKPoint.Empty, ToolModifiers.None, shown, () => null));

        await Assert.That(ranWith).IsSameReferenceAs(shown);
        await Assert.That(pressedWith).IsSameReferenceAs(shown);
        vm.Dispose();
    }

    [Test]
    public async Task ComposedIds_NeverStartWithAHostRoot()
    {
        string[] hostIds =
        [
            SceneLayerIds.Radar, SceneLayerIds.Guides, SceneLayerIds.HudClock, SceneLayerIds.Query, SceneLayerIds.Utility
        ];

        string composed = ExtensionLayerIds.Compose("playback2d", "radar");

        await Assert.That(hostIds).DoesNotContain(composed);
        await Assert.That(composed).IsEqualTo("ext.playback2d.radar");
    }

    private sealed class RecordingLayer(string id) : ISceneLayer
    {
        public bool ThrowInRender { get; init; }

        public bool LeaveTransformed { get; init; }

        public int Renders { get; private set; }

        public string Id => id;

        public LayerSlot Slot => LayerSlot.Overlay;

        public int Order => 0;

        public LayerCacheHint Cache => LayerCacheHint.Dynamic;

        public bool IsEnabled { get; set; } = true;

        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            Renders++;
            canvas.Save();
            canvas.Translate(5, 5);
            if (ThrowInRender)
            {
                throw new InvalidOperationException("render");
            }

            if (!LeaveTransformed)
            {
                canvas.Restore();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingTool : IMapTool
    {
        public int Presses { get; private set; }

        public bool OnPressed(in MapToolEvent e, IMapToolContext context)
        {
            Presses++;
            throw new InvalidOperationException("press");
        }

        public void OnMoved(in MapToolEvent e, IMapToolContext context)
        {
        }

        public void OnReleased(in MapToolEvent e, IMapToolContext context)
        {
        }

        public void OnCancelled(IMapToolContext context)
        {
        }
    }

    private sealed class NullContext : IMapToolContext
    {
        public LevelPane? PaneAt(SKPoint screen) => null;

        public SKPoint WorldToScreen(LevelPane pane, SKPoint world) => world;

        public double WorldUnitsPerPixel(LevelPane pane) => 1;

        public void RequestRender()
        {
        }
    }
}
