#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The <see cref="LayerCacheHint" /> mechanism. Each hint's re-record trigger is asserted directly,
///     because a cache that never re-records is a frozen frame and a cache that always re-records is no
///     cache at all — and both look fine in a screenshot.
///     <para>
///         <c>Static</c> has no B1 consumer (plan decision D-5): the radar's single <c>DrawImage</c>
///         shares the grid's <c>PerCamera</c> picture. It is built and tested here against a synthetic
///         layer anyway, because B2's dry annotation ink is its real customer and discovering the
///         mechanism is broken a phase later is expensive.
///     </para>
/// </summary>
public class LayerCachePictureTests
{
    [Test]
    public async Task PerCamera_RecordsOnce_AndReplaysUntilTheCameraEpochBumps()
    {
        CountingLayer layer = new("test.percamera", LayerCacheHint.PerCamera);
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        for (int i = 0; i < 5; i++)
        {
            compositor.Render(surface.Canvas, Submission(1));
        }

        await Assert.That(layer.RenderCalls).IsEqualTo(1);
        await Assert.That(compositor.Stats.PicturesReplayed).IsEqualTo(4);

        compositor.Render(surface.Canvas, Submission(2));
        await Assert.That(layer.RenderCalls).IsEqualTo(2);
    }

    [Test]
    public async Task PerCamera_ReRecordsWhenContentVersionBumps()
    {
        CountingLayer layer = new("test.percamera", LayerCacheHint.PerCamera);
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        compositor.Render(surface.Canvas, Submission(1));
        compositor.Render(surface.Canvas, Submission(1));
        await Assert.That(layer.RenderCalls).IsEqualTo(1);

        layer.Version++;
        compositor.Render(surface.Canvas, Submission(1));
        await Assert.That(layer.RenderCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Static_ReplaysUnderTheCameraMatrix_SoAPanMovesItWithoutReRecording()
    {
        // The synthetic Static layer draws a world-space square. If the replay ignored the camera
        // matrix the square would sit at the same pixels after a pan — which is exactly the bug the
        // hint exists to avoid, so this asserts on PIXELS rather than on call counts alone.
        WorldSquareLayer layer = new();
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(128, 128));

        ViewportTransform camera = ViewportTransform.Fit(128, 128, -512, -512, 512, 512);
        compositor.Render(surface.Canvas, Submission(1, camera));
        int firstCentre = ColumnOfInk(surface);

        ViewportTransform panned = camera.WithPanDelta(30, 0);
        compositor.Render(surface.Canvas, Submission(2, panned));
        int secondCentre = ColumnOfInk(surface);

        Console.WriteLine($"[static-cache] ink column {firstCentre} → {secondCentre}, " +
                          $"renders={layer.RenderCalls}");

        await Assert.That(layer.RenderCalls).IsEqualTo(1); // world-space recording survives a camera move
        await Assert.That(secondCentre - firstCentre).IsEqualTo(30).Within(1);
    }

    [Test]
    public async Task Dynamic_NeverRecords()
    {
        CountingLayer layer = new("test.dynamic", LayerCacheHint.Dynamic);
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        for (int i = 0; i < 4; i++)
        {
            compositor.Render(surface.Canvas, Submission(1));
        }

        await Assert.That(layer.RenderCalls).IsEqualTo(4);
        await Assert.That(compositor.Stats.PicturesRecorded).IsEqualTo(0);
    }

    [Test]
    public async Task EnablePictureCaching_False_DrawsEveryLayerDirectly()
    {
        CountingLayer layer = new("test.percamera", LayerCacheHint.PerCamera);
        using SceneCompositor compositor = new(new SceneCompositorOptions(false));
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        compositor.Render(surface.Canvas, Submission(1));
        compositor.Render(surface.Canvas, Submission(1));

        await Assert.That(layer.RenderCalls).IsEqualTo(2);
        await Assert.That(compositor.Stats.PicturesRecorded).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidatePaneCaches_DropsOnlyThatLevel()
    {
        CountingLayer layer = new("test.percamera", LayerCacheHint.PerCamera);
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        compositor.Render(surface.Canvas, Submission(1, levelId: new MapLevelId(1)));
        compositor.Render(surface.Canvas, Submission(1, levelId: new MapLevelId(2)));
        await Assert.That(layer.RenderCalls).IsEqualTo(2);

        compositor.InvalidatePaneCaches(new MapLevelId(1));

        compositor.Render(surface.Canvas, Submission(1, levelId: new MapLevelId(2)));
        await Assert.That(layer.RenderCalls).IsEqualTo(2); // level 2 still cached

        compositor.Render(surface.Canvas, Submission(1, levelId: new MapLevelId(1)));
        await Assert.That(layer.RenderCalls).IsEqualTo(3); // level 1 re-recorded
    }

    [Test]
    public async Task MaxCachedPictures_EvictsLeastRecentlyUsed()
    {
        CountingLayer layer = new("test.percamera", LayerCacheHint.PerCamera);
        using SceneCompositor compositor = new(new SceneCompositorOptions(true, 2));
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        for (int epoch = 1; epoch <= 6; epoch++)
        {
            compositor.Render(surface.Canvas, Submission(epoch));
        }

        await Assert.That(layer.RenderCalls).IsEqualTo(6);

        // Epoch 1 was evicted long ago, so it has to be re-recorded rather than replayed.
        compositor.Render(surface.Canvas, Submission(1));
        await Assert.That(layer.RenderCalls).IsEqualTo(7);
    }

    [Test]
    public async Task DisabledLayer_IsSkippedInBothAdvanceAndRender()
    {
        CountingLayer layer = new("test.dynamic", LayerCacheHint.Dynamic)
        {
            IsEnabled = false
        };
        using SceneCompositor compositor = new();
        compositor.Add(layer);

        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(new SKSizeI(64, 64));

        SceneTime time = default;
        compositor.Advance(in time, Scene2DFrame.Empty);
        compositor.Render(surface.Canvas, Submission(1));

        await Assert.That(layer.AdvanceCalls).IsEqualTo(0);
        await Assert.That(layer.RenderCalls).IsEqualTo(0);
    }

    private static SceneSubmission Submission(int cameraEpoch, ViewportTransform camera = default,
        MapLevelId? levelId = null)
    {
        MapLevelId id = levelId ?? new MapLevelId(0);
        MapLevel level = new()
        {
            Id = id,
            Name = "floor 0",
            ZMin = -1000,
            ZMax = 1000
        };

        LevelPaneSnapshot pane = new(id, 0, level, camera, new SKRect(0, 0, 128, 128), cameraEpoch);
        return new SceneSubmission(1, Scene2DFrame.Empty, default, [pane], ScenePalette.Dark,
            RenderPurpose.Interactive, new SKRect(0, 0, 128, 128), 1f);
    }

    // The x of the leftmost non-background pixel on the middle row.
    private static int ColumnOfInk(SKSurface surface)
    {
        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        int y = bitmap.Height / 2;
        for (int x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y) != ScenePalette.Dark.Background)
            {
                return x;
            }
        }

        return -1;
    }

    private sealed class CountingLayer : ISceneLayer
    {
        public CountingLayer(string id, LayerCacheHint cache)
        {
            Id = id;
            Cache = cache;
        }

        public int Version { get; set; }
        public int RenderCalls { get; private set; }
        public int AdvanceCalls { get; private set; }
        public string Id { get; }
        public LayerSlot Slot => LayerSlot.World;
        public int Order => 0;
        public LayerCacheHint Cache { get; }
        public bool IsEnabled { get; set; } = true;
        public int ContentVersion => Version;

        public bool Advance(in SceneTime time, Scene2DFrame frame)
        {
            AdvanceCalls++;
            return false;
        }

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            RenderCalls++;
            using SKPaint paint = new();
            paint.Color = SKColors.Red;
            canvas.DrawRect(new SKRect(2, 2, 10, 10), paint);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A Static layer draws in WORLD space — the replay applies the camera matrix.</summary>
    private sealed class WorldSquareLayer : ISceneLayer
    {
        public int RenderCalls { get; private set; }
        public string Id => "test.static";
        public LayerSlot Slot => LayerSlot.World;
        public int Order => 0;
        public LayerCacheHint Cache => LayerCacheHint.Static;
        public bool IsEnabled { get; set; } = true;
        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            RenderCalls++;
            using SKPaint paint = new();
            paint.Color = SKColors.Red;
            paint.IsAntialias = false;
            canvas.DrawRect(new SKRect(-64, -64, 64, 64), paint);
        }

        public void Dispose()
        {
        }
    }
}
