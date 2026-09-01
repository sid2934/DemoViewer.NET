#region

using System.Reflection;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <b>B0's exit-criterion test.</b> A committed fixture is loaded from JSON, composited through the
///     CPU surface provider and written to a PNG, and the process is asserted to have loaded no
///     Avalonia assembly at all. "Frames render to PNG with zero Avalonia dependencies" is a claim about
///     the running process, not about a csproj, so this checks the running process.
/// </summary>
[NotInParallel]
[Category("Render")]
public class SceneSmokeRenderTests
{
    [Test]
    public async Task DebugGridLayer_RendersFixtureToPng_WithZeroAvaloniaLoaded()
    {
        SceneFixture fixture = FixtureCorpus.Load("synthetic-tenplayers");
        SKSizeI size = new(640, 360);

        using CpuSurfaceProvider provider = new();
        SceneRenderer renderer = new(provider);
        using SceneCompositor compositor = new();
        compositor.Add(new DebugGridLayer());

        SceneTime time = fixture.Time;
        SceneRenderContext ctx = TestContexts.For(fixture.Frame, fixture.Camera, size.Width, size.Height);
        using SKImage image = renderer.Render(compositor, fixture.Frame, in time, in ctx, size);

        string outPath = Path.Combine(AppContext.BaseDirectory, "artifacts",
            "synthetic-tenplayers@640x360.png");
        SceneRenderer.WritePng(image, outPath);

        using SKBitmap bitmap = SKBitmap.FromImage(image);
        SKColor background = ScenePalette.Dark.Background;
        int nonBackground = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != background)
                {
                    nonBackground++;
                }
            }
        }

        Console.WriteLine($"[smoke] {outPath} nonBg={nonBackground}");

        await Assert.That(File.Exists(outPath)).IsTrue();
        await Assert.That(nonBackground).IsGreaterThan(2000);
        await Assert.That(LoadedAvaloniaAssemblies()).IsEmpty();
    }

    [Test]
    public async Task EveryCommittedFixture_Renders_WithoutThrowing()
    {
        IReadOnlyList<string> paths = FixtureCorpus.ScenePaths();
        await Assert.That(paths.Count).IsGreaterThanOrEqualTo(3);

        using CpuSurfaceProvider provider = new();
        SceneRenderer renderer = new(provider);

        foreach (string path in paths)
        {
            SceneFixture fixture = SceneFixture.Load(path);
            SKSizeI size = fixture.Size.Width > 0 ? fixture.Size : new SKSizeI(320, 180);

            using SceneCompositor compositor = new();
            compositor.Add(new DebugGridLayer());

            SceneTime time = fixture.Time;
            SceneRenderContext ctx = TestContexts.For(fixture.Frame, fixture.Camera, size.Width, size.Height);
            using SKImage image = renderer.Render(compositor, fixture.Frame, in time, in ctx, size);

            await Assert.That(image.Width).IsEqualTo(size.Width);
            await Assert.That(image.Height).IsEqualTo(size.Height);
        }
    }

    private static List<string> LoadedAvaloniaAssemblies()
    {
        List<string> found = [];
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name is not null && name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(name);
            }
        }

        return found;
    }
}
