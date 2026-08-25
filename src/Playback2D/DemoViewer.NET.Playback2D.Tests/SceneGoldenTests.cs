#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Golden images for the synthetic fixture family, rendered through the CPU provider with no demo,
///     no window and no Avalonia — so this gate runs everywhere, CI included.
///     <para>
///         <b>What this is not.</b> These pin <i>B0's own</i> render loop: the fixture format, the
///         palette, the world→screen transform and the compositor. They are not the B1 parity corpus,
///         which pins the <i>pre-v2 control's</i> <c>DrawingContext</c> output and is captured by
///         <c>Playback2DGoldenCaptureTests</c> from a real demo.
///     </para>
///     <para>
///         Compared at <see cref="GoldenTolerance.DefaultPerceptual" /> rather than byte-exact: CPU
///         rasterisation of anti-aliased edges can differ by a least-significant bit between SIMD paths,
///         so a committed image would otherwise be machine-specific. Same-machine byte-exactness is
///         gated separately by <c>SceneRendererTests.Render_Twice_ProducesByteIdenticalPixels</c>.
///     </para>
/// </summary>
[NotInParallel]
public class SceneGoldenTests
{
    private const string UpdateEnvVar = "PB2D_GOLDEN_UPDATE";

    [Test]
    [Arguments("synthetic-empty")]
    [Arguments("synthetic-tenplayers")]
    [Arguments("synthetic-utility")]
    public async Task SyntheticFixture_MatchesCommittedGolden(string name)
    {
        SceneFixture fixture = FixtureCorpus.Load(name);
        SKSizeI size = fixture.Size;
        byte[] actual = Render(fixture, size);

        string goldenPath = Path.Combine(FixtureCorpus.Root, "goldens", "cpu",
            $"{name}@{size.Width}x{size.Height}.png");

        if (!File.Exists(goldenPath))
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"no golden at {goldenPath}. Regenerate deliberately with " +
                    "scripts/update-playback2d-goldens.sh.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllBytesAsync(goldenPath, actual);
            Console.WriteLine($"[golden] wrote {goldenPath} ({actual.Length} bytes)");
            return;
        }

        byte[] expected = await File.ReadAllBytesAsync(goldenPath);
        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[golden] {name} match={result.Match} maxDelta={result.MaxChannelDelta} " +
                          $"diff={result.MismatchedFraction:P4}");

        if (!result.Match)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.actual.png"), actual);
            if (GoldenImageComparer.CreateDiffPng(expected, actual) is { } diff)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.diff.png"), diff);
            }

            Console.WriteLine($"[golden] wrote the actual + diff images to {dir}");
        }

        await Assert.That(result.FailureReason).IsNull();
        await Assert.That(result.Match).IsTrue();
    }

    private static byte[] Render(SceneFixture fixture, SKSizeI size)
    {
        using CpuSurfaceProvider provider = new();
        SceneRenderer renderer = new(provider);
        using SceneCompositor compositor = new();
        compositor.Add(new DebugGridLayer());

        SceneTime time = fixture.Time;
        SceneRenderContext ctx = TestContexts.For(fixture.Frame, fixture.Camera, size.Width, size.Height);
        using SKImage image = renderer.Render(compositor, fixture.Frame, in time, in ctx, size);

        using MemoryStream stream = new();
        SceneRenderer.WritePng(image, stream);
        return stream.ToArray();
    }
}
