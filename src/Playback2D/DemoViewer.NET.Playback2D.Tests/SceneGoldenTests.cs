#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Golden images for the synthetic fixture family, rendered through the CPU provider with no demo,
///     no window and no Avalonia, so this gate runs everywhere, CI included.
///     <para>
///         <b>These are the same PNGs <c>dv2d golden verify</c> reads.</b> One file, two readers, so the
///         render path here mirrors <see cref="SceneLayerCatalog.CreateSceneStack" /> plus
///         <see cref="HeadlessSceneRenderer" /> with the camera pinned, statement for statement what
///         <c>SceneRenderPlan</c> + <c>GoldenCommand</c> do, or the two would disagree by construction.
///         Not the pre-v2 parity corpus, which pins the pre-v2 control's <c>DrawingContext</c> output via
///         <c>Playback2DGoldenCaptureTests</c>.
///     </para>
///     <para>
///         Compared perceptually rather than byte-exact, since anti-aliased edges can differ by a
///         least-significant bit between SIMD paths (same-machine byte-exactness is gated separately by
///         <c>SceneRendererTests.Render_Twice_ProducesByteIdenticalPixels</c>). Specifically
///         <see cref="GoldenTolerance.ForLabelledFrame" />, not <see cref="GoldenTolerance.DefaultPerceptual" />:
///         see that method for why the budget is per label; the proof is
///         <see cref="EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk" /> below.
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

        // Summary rather than a hand-rolled line, because aboveCeiling is the quantity the glyph budget
        // is spent on and it was invisible in CI's output while this gate was the one going red.
        GoldenTolerance tolerance =
            GoldenTolerance.ForLabelledFrame(size.Width, size.Height, LabelCount(fixture));
        GoldenComparison result = GoldenImageComparer.Compare(expected, actual, tolerance);
        Console.WriteLine($"[golden] {name} labels={LabelCount(fixture)} " +
                          $"glyphBudget={tolerance.MaxGlyphOutlierFraction:P4} {result.Summary}");

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
    ///     <b>
    ///         The proof obligation behind <see cref="GoldenTolerance.ForLabelledFrame" />, for the
    ///         synthetic corpus
    ///     </b>:
    ///     see <see cref="GlyphAttribution" />. <c>synthetic-empty</c> has no ink
    ///     and is therefore held to the unrelaxed gate outright, which is also what
    ///     <see cref="GoldenTolerance.ForLabelledFrame" /> gives it.
    /// </summary>
    /// <param name="name">The fixture to attribute.</param>
    [Test]
    [Arguments("synthetic-empty")]
    [Arguments("synthetic-tenplayers")]
    [Arguments("synthetic-utility")]
    public async Task EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk(string name)
    {
        SceneFixture fixture = FixtureCorpus.Load(name);
        SKSizeI size = fixture.Size;
        string goldenPath = Path.Combine(FixtureCorpus.Root, "goldens", "cpu",
            $"{name}@{size.Width}x{size.Height}.png");
        if (!File.Exists(goldenPath))
        {
            throw new InvalidOperationException($"no golden at {goldenPath}");
        }

        byte[] goldenPng = await File.ReadAllBytesAsync(goldenPath);
        GlyphAttribution ink = GlyphAttribution.Measure(goldenPng, Render(fixture, size),
            Render(fixture, size, false));

        int labels = LabelCount(fixture);
        Console.WriteLine($"[attribution] {ink.Describe(name, labels)}");

        // See GlyphAttribution.InkPixels for why this guard exists. It also depends on MarkerLayer being
        // findable under SceneLayerIds.Markers, so a renamed id fails open, not closed.
        if (labels > 0)
        {
            await Assert.That(ink.InkPixels).IsGreaterThan(0L);
        }

        // The deltas are measured against the COMMITTED golden, so a geometry regression lands outside
        // the mask rather than passing by tautology.
        await Assert.That(ink.OverCeilingOutsideInk).IsEqualTo(0L);
        await Assert.That(ink.WorstOutsideInk)
            .IsLessThanOrEqualTo(GoldenTolerance.DefaultPerceptual.MaxChannelDelta);

        // See GlyphAttribution.GlyphPatchedPng for why this comparison licenses the budget.
        GoldenComparison strict = GoldenImageComparer.Compare(goldenPng, ink.GlyphPatchedPng,
            GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[attribution] {name} glyph-patched, unrelaxed: {strict.Summary}");
        await Assert.That(strict.FailureReason).IsNull();
    }

    /// <summary>
    ///     How many labels the frame draws: what the glyph budget is denominated in. Read off the scene
    ///     rather than declared per fixture, since a hand-written count could be raised to buy slack; this
    ///     one cannot be raised without adding a player to the capture.
    ///     <para>
    ///         The twin of <c>GoldenCommand.LabelCount</c>, which denominates the same budget for the same
    ///         three PNGs on the <c>dv2d golden</c> side. Two assemblies means two copies of a one-liner;
    ///         they must stay the same definition even though the two owners render these images
    ///         independently by design.
    ///     </para>
    /// </summary>
    /// <param name="fixture">The scene about to be drawn.</param>
    private static int LabelCount(SceneFixture fixture) =>
        fixture.Frame.Markers.Count(m => !string.IsNullOrEmpty(m.Label));

    /// <summary>
    ///     The <c>dv2d golden</c> render, re-stated. Every line has a counterpart in
    ///     <c>SceneRenderPlan.Build</c> / <c>GoldenCommand.Run</c>: the production layer stack, the dark
    ///     palette, <c>RenderPurpose.Export</c>, and the camera as a <b>pin</b> rather than a
    ///     <c>SetAllCameras</c> call. The pin is re-applied inside <c>Advance</c> after the panes are
    ///     reconciled, which is what lets a one-shot render supply its camera as data.
    ///     <para>
    ///         No map bundle is bound, and none of the three synthetic entries names a map, so they render
    ///         on <c>RadarLayer</c>'s synthetic grid fallback, the same one a user with no baked asset
    ///         sees.
    ///     </para>
    /// </summary>
    /// <param name="fixture">The scene to draw.</param>
    /// <param name="size">The output size, which is also the size in the golden's file name.</param>
    /// <param name="drawText">
    ///     False silences every text layer, marker initials and the floor caption, leaving a frame that
    ///     differs from the full render in exactly the glyph ink and nowhere else. That difference is the
    ///     mask <see cref="EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk" /> attributes with, so it is
    ///     produced by the render path under test rather than by a second one built to resemble it.
    /// </param>
    private static byte[] Render(SceneFixture fixture, SKSizeI size, bool drawText = true)
    {
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        if (!drawText)
        {
            if (compositor.Find(SceneLayerIds.Markers) is MarkerLayer markers)
            {
                markers.DrawLabels = false;
            }

            compositor.SetEnabled(SceneLayerIds.FloorLabel, false);
        }

        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Camera = fixture.Camera
        };

        SceneTime time = fixture.Time;
        return renderer.RenderPng(fixture.Frame, in time, size);
    }
}
