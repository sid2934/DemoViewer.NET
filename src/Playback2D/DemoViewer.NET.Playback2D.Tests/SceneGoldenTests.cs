#region

using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Golden images for the corpus entries this suite can judge, rendered through the CPU provider with
///     no demo, no window and no Avalonia, so this gate runs everywhere, CI included.
///     <para>
///         <b>These are the same PNGs <c>dv2d golden verify</c> reads.</b> One file, two readers, and
///         one render: <see cref="GoldenEntryPlan" /> maps the corpus entry to the plan and renders it,
///         so the entry's size, layer set, sidecars and camera reach this gate and the tool's identically.
///         Not the pre-v2 parity corpus, which pins the pre-v2 control's <c>DrawingContext</c> output
///         and is read by <c>GoldenParityTests</c>.
///     </para>
///     <para>
///         The asset-backed entries render over the repo's committed <c>assets/</c> bundles; see
///         <see cref="RepoAssets" />. Four non-pending entries are left out because macOS renders them
///         outside what this gate allows, and a committed golden is not re-baselined to fit a second
///         platform. <c>nuke-single-upper</c>, <c>query-nuke-execute</c> and <c>overlay-nuke-execute</c>
///         are <c>GoldenAttributionTests.MacOsDivergent</c>, 11 to 13 against a ceiling of 8.
///         <c>annotated-shapes-mirage-b</c> fails differently: its over-ceiling pixels do all lie under
///         glyph ink, but they reach 154 against <see cref="GoldenTolerance.ForLabelledFrame" />'s glyph
///         ceiling of 96, with a worst window of 0.72 against its 0.88 floor. It is the only
///         non-pending entry whose text is an <b>annotation label</b> rather than an initial on a team
///         disc, and the tier was sized on initials; <c>dv2d.md</c> records the same roughly 150 for a
///         large text annotation on ubuntu, so this is not macOS alone. <c>zones-nuke</c> and
///         <c>zones-nuke-overlay</c> remain <c>dv2d</c>'s alone.
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
    [Arguments("fitmap-mirage-eco")]
    [Arguments("duel-mirage-b")]
    [Arguments("bomb-planted-inferno")]
    [Arguments("annotated-mirage-b")]
    [Arguments("full-scene-budget")]
    public async Task CorpusEntry_MatchesCommittedGolden(string name)
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
    ///         The proof obligation behind <see cref="GoldenTolerance.ForLabelledFrame" />, for every
    ///         entry this suite judges
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
    [Arguments("fitmap-mirage-eco")]
    [Arguments("duel-mirage-b")]
    [Arguments("bomb-planted-inferno")]
    [Arguments("annotated-mirage-b")]
    [Arguments("full-scene-budget")]
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
    ///     The repo's committed <c>assets/</c> tree, which is where the baked bundles live and the root
    ///     <c>dv2d</c>'s walk-up probe settles on in a checkout. Named outright rather than probed: the
    ///     ladder's middle rung is <c>DV2D_ASSETS</c>, and a developer who has that set for another
    ///     checkout would otherwise swap the radar art out from under these comparisons and see a
    ///     geometry difference.
    /// </summary>
    private static AssetsRoot RepoAssets()
    {
        string? assets = TestTiers.FindRepoRoot() is { } repo ? Path.Combine(repo, "assets") : null;
        if (assets is null || !Directory.Exists(assets))
        {
            throw new InvalidOperationException(
                $"no baked assets/ tree above {AppContext.BaseDirectory}; the asset-backed entries " +
                "cannot be rendered without one.");
        }

        return new AssetsRoot(assets, AssetsRootSource.Flag, [assets]);
    }

    /// <summary>
    ///     A re-baked or absent bundle moves every pixel under the radar. Refused here the way
    ///     <c>golden verify</c> reports <c>stale-assets</c>, so it cannot read as a pixel regression.
    /// </summary>
    /// <param name="entry">The entry being rendered.</param>
    /// <param name="plan">The plan built for it.</param>
    private static void RequireTheAuthoredBundle(GoldenCorpusEntry entry, SceneRenderPlan plan)
    {
        if (entry.MapName is null)
        {
            return;
        }

        if (plan.MapAssets?.Bundle.MapVersion is not { Length: > 0 } actual)
        {
            throw new InvalidOperationException(
                $"{entry.Name} names map {entry.MapName} and no bundle for it loaded from " +
                $"{plan.Assets.Path}.");
        }

        if (entry.MapVersion is { Length: > 0 } expected &&
            !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{entry.Name}: map_version {expected} in the manifest, {actual} in {plan.Assets.Path}.");
        }
    }

    /// <summary>
    ///     The <c>dv2d golden</c> render itself, not a restatement of it. An entry that names a map
    ///     draws over the baked bundle; one that does not falls through to <c>RadarLayer</c>'s synthetic
    ///     grid, the same one a user with no baked asset sees.
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
        AssetsRoot assets = entry.MapName is null ? AssetsRoot.None : RepoAssets();
        using SceneRenderPlan plan =
            SceneRenderPlan.Build(GoldenEntryPlan.OptionsFor(entry, assets: assets));
        RequireTheAuthoredBundle(entry, plan);
        if (!drawText)
        {
            SceneLayerCatalog.SilenceText(plan.Compositor);
        }

        return GoldenEntryPlan.Render(plan, entry, fixture);
    }
}
