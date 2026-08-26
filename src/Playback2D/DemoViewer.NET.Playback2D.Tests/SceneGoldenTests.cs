#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Golden images for the synthetic fixture family, rendered through the CPU provider with no demo,
///     no window and no Avalonia — so this gate runs everywhere, CI included.
///     <para>
///         <b>These are the same PNGs <c>dv2d golden verify</c> reads.</b> One file, two readers, so the
///         render path here has to be the render path there — which is why it is
///         <see cref="SceneLayerCatalog.CreateSceneStack" /> plus <see cref="HeadlessSceneRenderer" />
///         with the camera <i>pinned</i>, statement for statement what <c>SceneRenderPlan</c> +
///         <c>GoldenCommand</c> do. Until D6 this rendered a single <c>DebugGridLayer</c> through Core's
///         <c>SceneRenderer</c> and agreed with the CLI only because the CLI's catalog registered that
///         same grid and nothing else (D6 G-1); the moment the catalog grew the real stack the two
///         owners of these files would have disagreed by construction.
///     </para>
///     <para>
///         <b>What this is not.</b> Not the B1 parity corpus, which pins the <i>pre-v2 control's</i>
///         <c>DrawingContext</c> output and is captured by <c>Playback2DGoldenCaptureTests</c> from a
///         real demo.
///     </para>
///     <para>
///         Compared at <see cref="GoldenTolerance.DefaultPerceptual" /> rather than byte-exact: CPU
///         rasterisation of anti-aliased edges can differ by a least-significant bit between SIMD paths,
///         so a committed image would otherwise be machine-specific. Same-machine byte-exactness is
///         gated separately by <c>SceneRendererTests.Render_Twice_ProducesByteIdenticalPixels</c>.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
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

        // PB2D_GOLDEN_UPDATE=1 rewrites an EXISTING golden too, which is what "update" has to mean:
        // filling in only the missing ones made scripts/update-playback2d-goldens.sh incapable of
        // re-baselining anything, so a deliberate visual change needed an undocumented `rm` first.
        // `dv2d golden update` has always overwritten; these three suites now agree with it.
        bool updating = string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1",
            StringComparison.Ordinal);
        if (!File.Exists(goldenPath) || updating)
        {
            if (!updating)
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

    /// <summary>
    ///     The <c>dv2d golden</c> render, re-stated. Every line has a counterpart in
    ///     <c>SceneRenderPlan.Build</c> / <c>GoldenCommand.Run</c>: the production layer stack, the dark
    ///     palette, <c>RenderPurpose.Export</c>, and the camera as a <b>pin</b> rather than a
    ///     <c>SetAllCameras</c> call — the pin is re-applied inside <c>Advance</c> after the panes are
    ///     reconciled, which is what lets a one-shot render supply its camera as data.
    ///     <para>
    ///         No map bundle is bound, and none of the three synthetic entries names a map: they render
    ///         on <c>RadarLayer</c>'s synthetic grid fallback, which is the state a user with no baked
    ///         asset is in.
    ///     </para>
    /// </summary>
    /// <param name="fixture">The scene to draw.</param>
    /// <param name="size">The output size, which is also the size in the golden's file name.</param>
    private static byte[] Render(SceneFixture fixture, SKSizeI size)
    {
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Camera = fixture.Camera
        };

        SceneTime time = fixture.Time;
        return renderer.RenderPng(fixture.Frame, in time, size, RenderPurpose.Export);
    }
}
