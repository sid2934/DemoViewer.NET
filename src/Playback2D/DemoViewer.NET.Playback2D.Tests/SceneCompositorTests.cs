#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The layer stack's ordering and lifecycle rules. Draw order is a pure function of the registered
///     set — <c>(Slot, Order, Id)</c>, never insertion order — so a golden image cannot come to depend
///     on when a layer happened to be registered.
/// </summary>
public class SceneCompositorTests
{
    [Test]
    public async Task Layers_SortBy_SlotThenOrderThenId()
    {
        using SceneCompositor compositor = new();
        compositor.Add(new RecordingLayer("z.hud", LayerSlot.Hud));
        compositor.Add(new RecordingLayer("b.world", LayerSlot.World, 5));
        compositor.Add(new RecordingLayer("a.world", LayerSlot.World, 5));
        compositor.Add(new RecordingLayer("m.world", LayerSlot.World, 1));
        compositor.Add(new RecordingLayer("q.under", LayerSlot.Underlay));

        string[] ids = [.. compositor.Layers.Select(l => l.Id)];
        await Assert.That(string.Join(",", ids)).IsEqualTo("q.under,m.world,a.world,b.world,z.hud");
    }

    [Test]
    public async Task Add_DuplicateId_Throws()
    {
        using SceneCompositor compositor = new();
        compositor.Add(new RecordingLayer("dup"));

        ArgumentException? thrown = null;
        try
        {
            compositor.Add(new RecordingLayer("dup"));
        }
        catch (ArgumentException e)
        {
            thrown = e;
        }

        await Assert.That(thrown).IsNotNull();
    }

    [Test]
    public async Task Advance_ReturnsTrue_WhenAnyEnabledLayerReturnsTrue()
    {
        using SceneCompositor compositor = new();
        RecordingLayer quiet = new("quiet");
        RecordingLayer busy = new("busy")
        {
            AdvanceResult = true
        };
        compositor.Add(quiet);
        compositor.Add(busy);

        SceneTime time = new(1, 1, 0, 0, false);
        await Assert.That(compositor.Advance(in time, Scene2DFrame.Empty)).IsTrue();

        // Every layer is advanced even once one has claimed the loop — Advance is where layers mutate.
        await Assert.That(quiet.AdvanceCount).IsEqualTo(1);
        await Assert.That(busy.AdvanceCount).IsEqualTo(1);

        busy.AdvanceResult = false;
        await Assert.That(compositor.Advance(in time, Scene2DFrame.Empty)).IsFalse();
    }

    [Test]
    public async Task Advance_And_Render_SkipDisabledLayers()
    {
        using SceneCompositor compositor = new();
        RecordingLayer on = new("on");
        RecordingLayer off = new("off")
        {
            IsEnabled = false
        };
        compositor.Add(on);
        compositor.Add(off);

        SceneTime time = new(1, 1, 0, 0, false);
        compositor.Advance(in time, Scene2DFrame.Empty);

        using SKSurface surface = SKSurface.Create(new SKImageInfo(16, 16));
        compositor.Render(surface.Canvas, in TestContexts.Default);

        await Assert.That(on.AdvanceCount).IsEqualTo(1);
        await Assert.That(on.RenderCount).IsEqualTo(1);
        await Assert.That(off.AdvanceCount).IsEqualTo(0);
        await Assert.That(off.RenderCount).IsEqualTo(0);
    }

    [Test]
    public async Task Render_DrawsInSortOrder_SoTheLastLayerWins()
    {
        using SceneCompositor compositor = new();
        compositor.Add(new RecordingLayer("under", LayerSlot.Underlay)
        {
            Fill = SKColors.Red
        });
        compositor.Add(new RecordingLayer("over", LayerSlot.Overlay)
        {
            Fill = SKColors.Lime
        });

        using SKSurface surface = SKSurface.Create(new SKImageInfo(16, 16, SKColorType.Rgba8888,
            SKAlphaType.Premul));
        compositor.Render(surface.Canvas, in TestContexts.Default);

        await Assert.That(surface.PeekPixels().GetPixelColor(8, 8)).IsEqualTo(SKColors.Lime);
    }

    [Test]
    public async Task SetEnabled_TogglesOne_AndIgnoresUnknownIds()
    {
        using SceneCompositor compositor = new();
        RecordingLayer layer = new("known");
        compositor.Add(layer);

        compositor.SetEnabled("known", false);
        compositor.SetEnabled("nope", false); // must not throw

        await Assert.That(layer.IsEnabled).IsFalse();
        await Assert.That(compositor.Find("nope")).IsNull();
    }

    [Test]
    public async Task Remove_DisposesTheLayer_AndReportsUnknownIds()
    {
        using SceneCompositor compositor = new();
        RecordingLayer layer = new("gone");
        compositor.Add(layer);

        await Assert.That(compositor.Remove("gone")).IsTrue();
        await Assert.That(layer.Disposed).IsTrue();
        await Assert.That(compositor.Remove("gone")).IsFalse();
        await Assert.That(compositor.Layers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_DisposesAllLayers_AndIsIdempotent()
    {
        SceneCompositor compositor = new();
        RecordingLayer a = new("a");
        RecordingLayer b = new("b");
        compositor.Add(a);
        compositor.Add(b);

        compositor.Dispose();
        compositor.Dispose();

        await Assert.That(a.Disposed).IsTrue();
        await Assert.That(b.Disposed).IsTrue();
        await Assert.That(compositor.Layers.Count).IsEqualTo(0);
    }
}
