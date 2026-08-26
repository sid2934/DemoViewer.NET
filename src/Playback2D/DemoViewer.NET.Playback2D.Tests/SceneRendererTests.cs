#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The offscreen render loop and its PNG output, including the §11 determinism gate: two renders of
///     the same request must be byte-identical on a given backend, or no golden means anything.
/// </summary>
[Category("Render")]
public class SceneRendererTests
{
    [Test]
    public async Task Render_EmptyCompositor_ProducesBackgroundOnlyImage()
    {
        using CpuSurfaceProvider provider = new();
        SceneRenderer renderer = new(provider);
        using SceneCompositor compositor = new();

        SceneTime time = default;
        using SKImage image = renderer.Render(compositor, Scene2DFrame.Empty, in time,
            in TestContexts.Default, new SKSizeI(32, 32));

        using SKBitmap bitmap = SKBitmap.FromImage(image);
        SKColor background = ScenePalette.Dark.Background;
        bool allBackground = true;
        for (int y = 0; y < bitmap.Height && allBackground; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != background)
                {
                    allBackground = false;
                    break;
                }
            }
        }

        await Assert.That(renderer.Backend).IsEqualTo(RenderBackend.CpuRaster);
        await Assert.That(allBackground).IsTrue();
    }

    [Test]
    public async Task WritePng_ProducesDecodablePng_OfRequestedSize()
    {
        using CpuSurfaceProvider provider = new();
        SceneRenderer renderer = new(provider);
        using SceneCompositor compositor = new();
        compositor.Add(new RecordingLayer("fill")
        {
            Fill = SKColors.Magenta
        });

        SceneTime time = default;
        SceneRenderContext ctx = TestContexts.For(Scene2DFrame.Empty, default, 64, 48);
        using SKImage image = renderer.Render(compositor, Scene2DFrame.Empty, in time, in ctx,
            new SKSizeI(64, 48));

        using MemoryStream stream = new();
        SceneRenderer.WritePng(image, stream);
        stream.Position = 0;

        using SKBitmap decoded = SKBitmap.Decode(stream);
        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded.Width).IsEqualTo(64);
        await Assert.That(decoded.Height).IsEqualTo(48);
        await Assert.That(decoded.GetPixel(32, 24)).IsEqualTo(SKColors.Magenta);
    }

    [Test]
    public async Task WritePng_ToPath_CreatesTheDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pb2d-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "nested", "out.png");
        try
        {
            using CpuSurfaceProvider provider = new();
            using SKSurface surface = provider.CreateSurface(new SKSizeI(4, 4));
            surface.Canvas.Clear(SKColors.Blue);
            using SKImage image = surface.Snapshot();

            SceneRenderer.WritePng(image, path);

            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(new FileInfo(path).Length).IsGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task Render_Twice_ProducesByteIdenticalPixels()
    {
        SceneFixture fixtureA = FixtureCorpus.Load("synthetic-tenplayers");
        byte[] first = RenderToPng(fixtureA);
        byte[] second = RenderToPng(FixtureCorpus.Load("synthetic-tenplayers"));

        await Assert.That(second).IsEquivalentTo(first);
    }

    private static byte[] RenderToPng(SceneFixture fixture)
    {
        using CpuSurfaceProvider provider = new();
        SceneRenderer renderer = new(provider);
        using SceneCompositor compositor = new();

        // The grid layer, not a flat fill: a determinism gate over a single-colour image proves nothing.
        compositor.Add(new DebugGridLayer());

        SceneTime time = fixture.Time;
        SceneRenderContext ctx = TestContexts.For(fixture.Frame, fixture.Camera, fixture.Size.Width,
            fixture.Size.Height);
        using SKImage image = renderer.Render(compositor, fixture.Frame, in time, in ctx, fixture.Size);

        using MemoryStream stream = new();
        SceneRenderer.WritePng(image, stream);
        return stream.ToArray();
    }
}
