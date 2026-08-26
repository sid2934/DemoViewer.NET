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
///         Compared perceptually rather than byte-exact: CPU rasterisation of anti-aliased edges can
///         differ by a least-significant bit between SIMD paths, so a committed image would otherwise be
///         machine-specific. Same-machine byte-exactness is gated separately by
///         <c>SceneRendererTests.Render_Twice_ProducesByteIdenticalPixels</c>.
///     </para>
///     <para>
///         <b>Specifically <see cref="GoldenTolerance.ForLabelledFrame" />, not
///         <see cref="GoldenTolerance.DefaultPerceptual" />.</b> Until D6 these three fixtures rendered a
///         lone <c>DebugGridLayer</c> — straight lines and no text — so a single perceptual budget held
///         across operating systems. Round 2A registered the real stack here and re-baselined the goldens
///         on Windows, which put ten marker labels into one of them and left the tolerance alone; ubuntu
///         then failed on the glyphs and nothing else. The label count is read off the fixture and passed
///         in, because the budget is sized per label rather than per frame — the reasoning is on
///         <see cref="GoldenTolerance.ForLabelledFrame" /> and the proof is
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
    ///     <b>The proof obligation behind <see cref="GoldenTolerance.ForLabelledFrame" />.</b> The same
    ///     assertion <c>LevelGoldenTests.EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk</c> makes for
    ///     the nuke corpus, made here for the synthetic one — and it has to be made here separately,
    ///     because this corpus is where the per-label budget is spent and a budget is worth exactly what
    ///     the test beside it proves.
    ///     <para>
    ///         Each fixture is re-rendered with the labels silenced, and the difference between that and
    ///         the full render is an <i>exact</i> glyph-ink mask — not an approximation of one, since it
    ///         is literally the set of pixels the text layers painted. Two assertions ride on it. The
    ///         cheap one: outside the ink no pixel may exceed even the ±8 band. The complete one:
    ///         substitute the golden's own pixels under the ink, which neutralises every allowance the
    ///         glyph tier grants, and run the result through <see cref="GoldenTolerance.DefaultPerceptual" />
    ///         with <b>nothing</b> relaxed — the 32 ceiling, the 0.5 % budget, the alpha bound and both
    ///         SSIM floors. Passing that is what makes "the tier only forgives glyphs" a fact rather than
    ///         a hope: a displaced marker, a dropped smoke or a recoloured trail lands outside the mask
    ///         and survives the substitution.
    ///     </para>
    ///     <para>
    ///         It runs on every platform. On Windows the deltas are zeroes, which is worth pinning
    ///         precisely because it is the assertion that goes red first if the rasteriser difference
    ///         ever stops being confined to text. <c>synthetic-empty</c> is in the list for the same
    ///         reason from the other direction: it has no ink, so the mask is empty and this degenerates
    ///         into "the unrelaxed policy passes outright" — which is also what
    ///         <see cref="GoldenTolerance.ForLabelledFrame" /> gives it, there being no labels to buy an
    ///         allowance with.
    ///     </para>
    ///     <para>
    ///         The per-label rate it prints is the measurement the budget is derived from, so the
    ///         constant in <see cref="GoldenTolerance.ForLabelledFrame" /> can be re-checked from any
    ///         CI log rather than taken on trust.
    ///     </para>
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
        using SKBitmap golden = Decode(goldenPng);
        using SKBitmap actual = Decode(Render(fixture, size));
        using SKBitmap noText = Decode(Render(fixture, size, false));
        using SKBitmap patched = new(golden.Width, golden.Height, SKColorType.Rgba8888,
            SKAlphaType.Premul);

        int strictCeiling = GoldenTolerance.DefaultPerceptual.OutlierChannelDelta;
        int softCeiling = GoldenTolerance.DefaultPerceptual.MaxChannelDelta;
        int worstOutsideInk = 0, worstUnderInk = 0, worstX = 0, worstY = 0;
        long inkPixels = 0, overCeilingOutsideInk = 0, overCeilingUnderInk = 0;

        for (int y = 0; y < golden.Height; y++)
        {
            for (int x = 0; x < golden.Width; x++)
            {
                SKColor e = golden.GetPixel(x, y);
                SKColor a = actual.GetPixel(x, y);
                bool underInk = a != noText.GetPixel(x, y);
                int delta = Math.Max(Math.Abs(e.Red - a.Red),
                    Math.Max(Math.Abs(e.Green - a.Green), Math.Abs(e.Blue - a.Blue)));

                // The glyph tier's allowance, neutralised: under the ink the golden judges itself, so
                // whatever survives is by construction NOT a text difference.
                patched.SetPixel(x, y, underInk ? e : a);

                if (underInk)
                {
                    inkPixels++;
                    worstUnderInk = Math.Max(worstUnderInk, delta);
                    if (delta > strictCeiling)
                    {
                        overCeilingUnderInk++;
                    }

                    continue;
                }

                if (delta > worstOutsideInk)
                {
                    worstOutsideInk = delta;
                    worstX = x;
                    worstY = y;
                }

                if (delta > strictCeiling)
                {
                    overCeilingOutsideInk++;
                }
            }
        }

        int labels = LabelCount(fixture);
        Console.WriteLine($"[attribution] {name}: {labels} labels, glyph ink {inkPixels} px; " +
                          $"worst under ink {worstUnderInk} ({overCeilingUnderInk} over {strictCeiling}" +
                          $" = {(labels == 0 ? 0 : overCeilingUnderInk / (double)labels):F2} per label); " +
                          $"worst outside ink {worstOutsideInk} at ({worstX},{worstY}) " +
                          $"({overCeilingOutsideInk} over {strictCeiling})");

        // Not a tautology: the mask is "where the text layers changed the picture", and the deltas are
        // measured against the COMMITTED golden. A geometry regression lands outside the mask.
        await Assert.That(overCeilingOutsideInk).IsEqualTo(0L);
        await Assert.That(worstOutsideInk).IsLessThanOrEqualTo(softCeiling);

        // And the whole unrelaxed policy over the glyph-patched frame — the assertion that actually
        // licenses the budget, because it re-imposes every limit ForLabelledFrame loosens.
        GoldenComparison strict = GoldenImageComparer.Compare(goldenPng, Encode(patched),
            GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[attribution] {name} glyph-patched, unrelaxed: {strict.Summary}");
        await Assert.That(strict.FailureReason).IsNull();
    }

    /// <summary>
    ///     How many labels the frame draws, which is what the glyph budget is denominated in. Read off
    ///     the scene rather than declared per fixture: a hand-written count is a number someone can raise
    ///     to buy slack, and this one cannot be raised without adding a player to the capture.
    /// </summary>
    /// <param name="fixture">The scene about to be drawn.</param>
    private static int LabelCount(SceneFixture fixture) =>
        fixture.Frame.Markers.Count(m => !string.IsNullOrEmpty(m.Label));

    private static SKBitmap Decode(byte[] png) =>
        SKBitmap.Decode(png) ?? throw new InvalidOperationException("the image did not decode");

    private static byte[] Encode(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
    /// <param name="drawText">
    ///     False silences every text layer — marker initials and the floor caption — leaving a frame that
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
        return renderer.RenderPng(fixture.Frame, in time, size, RenderPurpose.Export);
    }
}
