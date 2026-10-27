#region

using DemoViewer.NET.Playback2D.Pipeline.Rendering;
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
///         <b>These are the same PNGs <c>dv2d golden verify</c> reads.</b> One file, two readers, and
///         one render: <see cref="GoldenEntryPlan" /> maps the corpus entry to the plan and renders it,
///         so the entry's size, layer set, sidecars and camera reach this gate and the tool's identically.
///         Not the pre-v2 parity corpus, which pins the pre-v2 control's <c>DrawingContext</c> output
///         and is read by <c>GoldenParityTests</c>.
///     </para>
///     <para>
///         Compared perceptually rather than byte-exact, since anti-aliased edges can differ by a
///         least-significant bit between SIMD paths (same-machine byte-exactness is gated separately by
///         <c>SceneRendererTests.Render_Twice_ProducesByteIdenticalPixels</c>). Specifically
///         <see cref="GoldenTolerance.ForLabelledFrame" />, not <see cref="GoldenTolerance.DefaultPerceptual" />:
///         see that method for why the budget is per label; the proof is
///         <see cref="EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk" /> below. <c>dv2d golden</c>
///         spends its text allowance differently, on a glyph-patched render under the unrelaxed
///         tolerance, which is a judging policy rather than a render and is deliberately not shared.
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
        GoldenCorpusEntry entry = Entry(name);
        SceneFixture fixture = SceneFixture.Load(entry.ScenePath);
        byte[] actual = Render(entry, fixture);

        string goldenPath = entry.GoldenPath(RenderBackend.CpuRaster);

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
        SKSizeI size = entry.Size;
        int labels = GoldenEntryPlan.LabelCount(fixture);
        GoldenTolerance tolerance = GoldenTolerance.ForLabelledFrame(size.Width, size.Height, labels);
        GoldenComparison result = GoldenImageComparer.Compare(expected, actual, tolerance);
        Console.WriteLine($"[golden] {name} labels={labels} " +
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
        GoldenCorpusEntry entry = Entry(name);
        SceneFixture fixture = SceneFixture.Load(entry.ScenePath);
        string goldenPath = entry.GoldenPath(RenderBackend.CpuRaster);
        if (!File.Exists(goldenPath))
        {
            throw new InvalidOperationException($"no golden at {goldenPath}");
        }

        byte[] goldenPng = await File.ReadAllBytesAsync(goldenPath);
        GlyphAttribution ink = GlyphAttribution.Measure(goldenPng, Render(entry, fixture),
            Render(entry, fixture, false));

        int labels = GoldenEntryPlan.LabelCount(fixture);
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

    private static GoldenCorpusEntry Entry(string name) =>
        GoldenCorpus.Load(FixtureCorpus.Root).Find(name)
        ?? throw new InvalidOperationException(
            $"no '{name}' entry in {FixtureCorpus.Root}/{GoldenCorpus.ManifestFileName}");

    /// <summary>
    ///     The <c>dv2d golden</c> render itself, not a restatement of it. No map bundle is bound (none
    ///     of the three synthetic entries names a map), so they render on <c>RadarLayer</c>'s synthetic
    ///     grid fallback, the same one a user with no baked asset sees.
    /// </summary>
    /// <param name="entry">The corpus entry: its size, layer set, sidecars and tolerance.</param>
    /// <param name="fixture">The scene to draw.</param>
    /// <param name="drawText">
    ///     False silences every text layer, leaving a frame that differs from the full render in exactly
    ///     the glyph ink and nowhere else. That difference is the mask
    ///     <see cref="EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk" /> attributes with, so it is
    ///     produced by the render path under test rather than by a second one built to resemble it.
    /// </param>
    private static byte[] Render(GoldenCorpusEntry entry, SceneFixture fixture, bool drawText = true)
    {
        using SceneRenderPlan plan = SceneRenderPlan.Build(GoldenEntryPlan.OptionsFor(entry));
        if (!drawText)
        {
            SceneLayerCatalog.SilenceText(plan.Compositor);
        }

        return GoldenEntryPlan.Render(plan, entry, fixture);
    }
}
