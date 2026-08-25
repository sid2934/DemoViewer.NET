#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     What the compositor does after it has been disposed.
///     <para>
///         <b>This is a teardown race, not a hypothetical.</b> <c>Scene2DHost</c> hands the shared
///         <see cref="SceneCompositor" /> to a <c>SceneDrawOperation</c> that runs on Avalonia's render
///         thread, and disposes that same compositor from <c>OnDetachedFromVisualTree</c> on the UI
///         thread when the tab deactivates. The render gate serializes the two but does not order them:
///         a frame already queued when the tab closes reaches <c>Render</c> after <c>Dispose</c>. The
///         background fill and the band divider are compositor-owned <see cref="SKPaint" />s, so an
///         unguarded render there writes through freed native handles.
///     </para>
/// </summary>
public class CompositorLifetimeTests
{
    [Test]
    public async Task Render_AfterDispose_IsANoOp_NotAUseAfterFree()
    {
        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        SceneCompositor compositor = new();
        compositor.Add(new InkLayer());
        compositor.Render(surface.Canvas, Submission());

        compositor.Dispose();

        // The queued frame. Both overloads, because the CPU fallback path uses the submission overload
        // and an offscreen consumer uses the single-pane one.
        compositor.Render(surface.Canvas, Submission());
        compositor.Render(surface.Canvas, Context());

        await Assert.That(compositor.Stats.PanesRendered).IsEqualTo(0);
    }

    [Test]
    public async Task Advance_AfterDispose_IsANoOp_AndDoesNotReArmTheLoop()
    {
        SceneCompositor compositor = new();
        InkLayer layer = new();
        compositor.Add(layer);
        compositor.Dispose();

        SceneTime time = default;
        bool keepArmed = compositor.Advance(in time, Scene2DFrame.Empty);

        await Assert.That(keepArmed).IsFalse();
        await Assert.That(layer.AdvanceCalls).IsEqualTo(0);
    }

    private static SceneSubmission Submission()
    {
        MapLevelId id = new(0);
        MapLevel level = new()
        {
            Id = id,
            Name = "floor 0",
            ZMin = -1000,
            ZMax = 1000
        };

        LevelPaneSnapshot pane = new(id, 0, level, default, new SKRect(0, 0, 64, 64), 1);
        return new SceneSubmission(1, Scene2DFrame.Empty, default, [pane], ScenePalette.Dark,
            RenderPurpose.Interactive, new SKRect(0, 0, 64, 64), 1f);
    }

    private static SceneRenderContext Context() =>
        new(Scene2DFrame.Empty, default, default, new SKRect(0, 0, 64, 64), -1, -1000, 1000,
            RenderPurpose.Interactive, ScenePalette.Dark, 1f);

    private sealed class InkLayer : ISceneLayer
    {
        public int AdvanceCalls { get; private set; }
        public string Id => "test.ink";
        public LayerSlot Slot => LayerSlot.World;
        public int Order => 0;
        public LayerCacheHint Cache => LayerCacheHint.Dynamic;
        public bool IsEnabled { get; set; } = true;
        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame)
        {
            AdvanceCalls++;
            return true;
        }

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            using SKPaint paint = new();
            paint.Color = SKColors.Red;
            canvas.DrawRect(new SKRect(2, 2, 10, 10), paint);
        }

        public void Dispose()
        {
        }
    }
}
