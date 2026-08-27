#region

using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     The perceptual half of the comparator: the outlier budget, the alpha bound and SSIM.
///     <para>
///         <b>A comparator has to be trustworthy before it is allowed to judge anything.</b> The
///         discriminating case is the one-pixel translation: every pixel is close to a pixel, so a
///         per-channel tolerance passes an entire scene that has slid sideways. That case is the whole
///         reason SSIM is in the policy, and it is pinned below.
///     </para>
/// </summary>
public class GoldenPerceptualToleranceTests
{
    [Test]
    public async Task Identical_Passes_WithPerfectSsim()
    {
        byte[] png = Solid(32, 32, new SKColor(120, 130, 140));

        GoldenComparison result =
            GoldenImageComparer.Compare(png, png, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.Match).IsTrue();
        await Assert.That(result.MaxChannelDelta).IsEqualTo(0);
        await Assert.That(result.OutlierFraction).IsEqualTo(0);
        await Assert.That(result.Ssim).IsEqualTo(1.0);
        await Assert.That(result.MinWindowSsim).IsEqualTo(1.0);
    }

    /// <summary>
    ///     A uniform lift inside the 8/255 band passes — on a mid-to-bright base. SSIM's luminance term
    ///     is a ratio, so the same absolute step is a larger relative change on a dark base; the
    ///     companion case below pins that, because it is the sort of asymmetry a future reader would
    ///     otherwise rediscover as a mysterious flake.
    /// </summary>
    [Test]
    public async Task UniformPlusSix_OnABrightBase_Passes()
    {
        byte[] expected = Solid(32, 32, new SKColor(200, 200, 200));
        byte[] actual = Solid(32, 32, new SKColor(206, 206, 206));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.MaxChannelDelta).IsEqualTo(6);
        await Assert.That(result.OutlierFraction).IsEqualTo(0);
        await Assert.That(result.Match).IsTrue();
    }

    /// <summary>
    ///     The same +6 on a near-black base fails on mean SSIM, and that is the correct answer: six
    ///     levels above 18 is a 33 % lift in luminance, which is a visible change to a dark scene rather
    ///     than rounding noise.
    /// </summary>
    [Test]
    public async Task UniformPlusSix_OnADarkBase_FailsOnMeanSsim()
    {
        byte[] expected = Solid(32, 32, new SKColor(10, 20, 30));
        byte[] actual = Solid(32, 32, new SKColor(16, 26, 36));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.MaxChannelDelta).IsEqualTo(6);
        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("SSIM");
    }

    [Test]
    public async Task UniformPlusTwelve_FailsOnTheOutlierBudget()
    {
        byte[] expected = Solid(32, 32, new SKColor(200, 200, 200));
        byte[] actual = Solid(32, 32, new SKColor(212, 212, 212));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.OutlierFraction).IsEqualTo(1.0);
        await Assert.That(result.FailureReason).Contains("budget");
    }

    /// <summary>
    ///     Two percent of pixels lifted by 20: under the 32 ceiling, so the fraction rule is what
    ///     must catch it. Distinguishing the two failures matters — one means "a few edges rounded
    ///     differently", the other means "something moved".
    /// </summary>
    [Test]
    public async Task SparseLifts_OverTheFractionBudget_Fail()
    {
        byte[] expected = Solid(64, 64, new SKColor(160, 160, 160));
        byte[] actual = Pixels(64, 64, (x, y) =>
            (y * 64 + x) % 50 == 0 ? new SKColor(180, 180, 180) : new SKColor(160, 160, 160));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.MaxChannelDelta).IsEqualTo(20);
        await Assert.That(result.OutlierFraction).IsGreaterThan(0.005);
        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("budget");
    }

    [Test]
    public async Task ASinglePixelOverTheCeiling_FailsOnTheCeiling()
    {
        byte[] expected = Solid(64, 64, new SKColor(160, 160, 160));
        byte[] actual = Pixels(64, 64, (x, y) =>
            x == 31 && y == 31 ? new SKColor(200, 200, 200) : new SKColor(160, 160, 160));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.MaxChannelDelta).IsEqualTo(40);
        await Assert.That(result.OutlierFraction).IsLessThan(0.005);
        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("ceiling");
    }

    /// <summary>
    ///     The boundary case this policy pins. A lone pixel lifted 30 levels sits under both
    ///     per-channel rules — under the 32 ceiling and far under the 0.5 % budget — and still fails, on
    ///     the worst-window SSIM.
    ///     <para>
    ///         That is the metric doing exactly its job rather than a false positive: a solitary spike in
    ///         an otherwise flat neighbourhood is local structure that was not there before, which is the
    ///         same signature as a missing glyph or a stray marker. It is also why the windowed floor,
    ///         not the mean, is the interesting number — a global mean averages one bad window away.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ALonePixelUnderBothChannelRules_StillFailsOnWindowedSsim()
    {
        byte[] expected = Solid(64, 64, new SKColor(160, 160, 160));
        byte[] actual = Pixels(64, 64, (x, y) =>
            x == 31 && y == 31 ? new SKColor(190, 190, 190) : new SKColor(160, 160, 160));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.MaxChannelDelta).IsEqualTo(30);
        await Assert.That(result.OutlierFraction).IsLessThan(0.005);
        await Assert.That(result.Ssim).IsGreaterThan(0.995);
        await Assert.That(result.MinWindowSsim).IsLessThan(0.95);
        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("window");
    }

    /// <summary>
    ///     <b>The case per-channel tolerance alone would pass.</b> A low-amplitude checkerboard shifted
    ///     one pixel changes every pixel by six levels — inside the band, zero outliers, no alpha drift —
    ///     while the structure is now anti-correlated with the original. If this test ever goes green,
    ///     SSIM has stopped working and the whole cross-backend policy is decorative.
    /// </summary>
    [Test]
    public async Task OnePixelShift_PassesEveryChannelRule_AndFailsOnSsim()
    {
        byte[] expected = Pixels(64, 64, (x, y) => Checker(x, y));
        byte[] actual = Pixels(64, 64, (x, y) => Checker(x + 1, y));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.MaxChannelDelta).IsEqualTo(6);
        await Assert.That(result.OutlierFraction).IsEqualTo(0);
        await Assert.That(result.MaxAlphaDelta).IsEqualTo(0);
        await Assert.That(result.Ssim).IsLessThan(0.9);
        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("SSIM");
    }

    /// <summary>
    ///     Alpha gets its own, far tighter bound: a backend that disagrees about coverage is a real
    ///     bug, not an anti-aliasing difference.
    /// </summary>
    [Test]
    public async Task AlphaDrift_Fails()
    {
        byte[] expected = Solid(32, 32, new SKColor(200, 200, 200, 255));
        byte[] actual = Solid(32, 32, new SKColor(200, 200, 200, 250));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.MaxAlphaDelta).IsGreaterThanOrEqualTo(3);
        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("alpha");
    }

    /// <summary>
    ///     Byte-exact mode must not pay for SSIM, and must not be softened by it either: a single
    ///     differing least-significant bit is still a failure there.
    /// </summary>
    [Test]
    public async Task ByteExact_StillFailsOnOneLeastSignificantBit()
    {
        byte[] expected = Solid(16, 16, new SKColor(200, 200, 200));
        byte[] actual = Pixels(16, 16, (x, y) =>
            x == 4 && y == 4 ? new SKColor(201, 200, 200) : new SKColor(200, 200, 200));

        GoldenComparison result = GoldenImageComparer.Compare(expected, actual, GoldenTolerance.ByteExact);

        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.Ssim).IsEqualTo(1.0);
    }

    /// <summary>
    ///     An image narrower than the 11×11 window still gets a real SSIM rather than a free pass — the
    ///     window shrinks to fit. A thumbnail comparison that silently reported 1.0 would be worse than
    ///     no comparison at all.
    /// </summary>
    [Test]
    public async Task SmallerThanTheWindow_StillComputesSsim()
    {
        byte[] expected = Pixels(6, 6, (x, y) => Checker(x, y));
        byte[] actual = Pixels(6, 6, (x, y) => Checker(x + 1, y));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.Ssim).IsLessThan(0.995);
    }

    /// <summary>
    ///     <b>The comparator's arithmetic, against a closed form rather than against itself.</b> Two flat
    ///     images have zero variance and zero covariance, so SSIM collapses to its luminance term alone:
    ///     <c>(2·μx·μy + C₁) / (μx² + μy² + C₁)</c>, with <c>C₁ = (0.01·255)² = 6.5025</c>. For greys 100
    ///     and 110 that is <c>22006.5025 / 22106.5025 = 0.9954764…</c>, independent of the window size and
    ///     of the Gaussian weights.
    ///     <para>
    ///         This is the case that catches a wrong <c>C₁</c>, a mis-normalised kernel, or luma weights
    ///         that do not sum to one — none of which the pass/fail cases above would notice, because
    ///         every one of them would still fall on the same side of its threshold.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Ssim_OnTwoFlatImages_MatchesTheClosedFormLuminanceTerm()
    {
        byte[] expected = Solid(48, 48, new SKColor(100, 100, 100));
        byte[] actual = Solid(48, 48, new SKColor(110, 110, 110));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        const double c1 = 0.01 * 255 * (0.01 * 255);
        double closedForm = ((2 * 100.0 * 110.0) + c1) / ((100.0 * 100.0) + (110.0 * 110.0) + c1);

        await Assert.That(closedForm).IsEqualTo(0.99547).Within(0.00001);
        await Assert.That(result.Ssim).IsEqualTo(closedForm).Within(0.00002);
        await Assert.That(result.MinWindowSsim).IsEqualTo(closedForm).Within(0.00002);
    }

    /// <summary>
    ///     The other half of the formula — the contrast/structure term, which the flat case leaves at
    ///     exactly 1 and therefore cannot test at all.
    ///     <para>
    ///         A period-2 checkerboard of <c>m ± d</c> has, under any symmetric normalised window,
    ///         μ = m and σ² = d²; shifting it one pixel flips its sign, giving σxy = −d² while μ is
    ///         unchanged. SSIM then reduces to <c>(−2d² + C₂) / (2d² + C₂)</c> with
    ///         <c>C₂ = (0.03·255)² = 58.5225</c>, which for d = 3 is
    ///         <c>40.5225 / 76.5225 = 0.529554…</c> — pinning the covariance path, <c>C₂</c>, and the
    ///         separable two-pass convolution together, since a pass that failed to compose into a true
    ///         2-D window would not land on this number.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Ssim_OnAnAntiCorrelatedCheckerboard_MatchesTheClosedFormStructureTerm()
    {
        byte[] expected = Pixels(48, 48, (x, y) => Checker(x, y));
        byte[] actual = Pixels(48, 48, (x, y) => Checker(x + 1, y));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        const double c2 = 0.03 * 255 * (0.03 * 255);
        const double variance = 3.0 * 3.0;
        double closedForm = ((-2 * variance) + c2) / ((2 * variance) + c2);

        await Assert.That(closedForm).IsEqualTo(0.52955).Within(0.00001);
        await Assert.That(result.Ssim).IsEqualTo(closedForm).Within(0.0005);
        await Assert.That(result.MinWindowSsim).IsEqualTo(closedForm).Within(0.0005);
    }

    // ── The glyph tier ──────────────────────────────────────────────────────────────────────────────
    // GoldenTolerance.ForLabelledFrame resolves per platform, so asserting the comparator's RULES
    // through it would test one branch on Windows and the other on Linux. These use an open tier as a
    // literal instead — a stand-in for the shape the factory produces, not its numbers — and the
    // factory's own policy is pinned separately below.

    private static readonly GoldenTolerance _openGlyphTier =
        GoldenTolerance.DefaultPerceptual with
        {
            GlyphOutlierChannelDelta = 96,
            MaxGlyphOutlierFraction = 0.0001,
            MinWindowSsim = 0.90
        };

    // The channel rules, isolated. A lone 70-level spike in a flat field is exactly the local structure
    // ALonePixelUnderBothChannelRules_StillFailsOnWindowedSsim exists to catch, so SSIM is stood down
    // here to keep each case about one rule.
    private static GoldenTolerance ChannelRulesOnly(GoldenTolerance tolerance) =>
        tolerance with { MinSsim = 0, MinWindowSsim = 0 };

    /// <summary>
    ///     <b>The Windows gate did not move.</b> Every stock tolerance keeps the glyph tier shut, which is
    ///     what makes the two rules added for it collapse back into the single ceiling rule the
    ///     comparator has always had.
    /// </summary>
    [Test]
    public async Task EveryStockTolerance_KeepsTheGlyphTierClosed()
    {
        foreach (GoldenTolerance tolerance in new[]
                 {
                     GoldenTolerance.ByteExact, GoldenTolerance.DefaultPerceptual,
                     GoldenTolerance.CrossBackend
                 })
        {
            await Assert.That(tolerance.GlyphOutlierChannelDelta).IsEqualTo(0);
            await Assert.That(tolerance.MaxGlyphOutlierFraction).IsEqualTo(0);
        }

        await Assert.That(GoldenTolerance.DefaultPerceptual.OutlierChannelDelta).IsEqualTo(32);
        await Assert.That(GoldenTolerance.DefaultPerceptual.MaxChannelDelta).IsEqualTo(8);
        await Assert.That(GoldenTolerance.DefaultPerceptual.MaxMismatchedFraction).IsEqualTo(0.005);
        await Assert.That(GoldenTolerance.DefaultPerceptual.MaxAlphaDelta).IsEqualTo(2);
        await Assert.That(GoldenTolerance.DefaultPerceptual.MinSsim).IsEqualTo(0.995);
        await Assert.That(GoldenTolerance.DefaultPerceptual.MinWindowSsim).IsEqualTo(0.95);
    }

    /// <summary>
    ///     The platform policy, stated as an assertion rather than left implicit in a factory: on the
    ///     machine that authored the corpus a text-bearing golden is judged by exactly the default
    ///     budget, and off it by the default budget plus three named, bounded allowances — the third of
    ///     which is counted in labels, so a frame that draws none gets no allowance on any platform.
    /// </summary>
    [Test]
    public async Task ForLabelledFrame_RelaxesNothing_OnTheAuthoringPlatform()
    {
        GoldenTolerance resolved = GoldenTolerance.ForLabelledFrame(900, 900, 10);

        await Assert.That(GoldenTolerance.ForLabelledFrame(900, 900, 0))
            .IsEqualTo(GoldenTolerance.DefaultPerceptual)
            .Because("no text means no allowance, on the authoring platform and off it");

        if (GoldenTolerance.GlyphsMatchTheCorpus)
        {
            await Assert.That(resolved).IsEqualTo(GoldenTolerance.DefaultPerceptual);
            return;
        }

        await Assert.That(resolved.GlyphOutlierChannelDelta).IsEqualTo(96);
        await Assert.That(resolved.MinWindowSsim).IsEqualTo(0.88);
        await Assert.That(resolved.MaxGlyphOutlierFraction).IsEqualTo(60 / 810_000.0).Within(1e-12)
            .Because("six pixels a label, over the frame the comparer wants a fraction of");

        // Everything the tier does NOT touch, so a future edit widening it has to come through here.
        await Assert.That(resolved.MaxChannelDelta)
            .IsEqualTo(GoldenTolerance.DefaultPerceptual.MaxChannelDelta);
        await Assert.That(resolved.OutlierChannelDelta)
            .IsEqualTo(GoldenTolerance.DefaultPerceptual.OutlierChannelDelta);
        await Assert.That(resolved.MaxMismatchedFraction)
            .IsEqualTo(GoldenTolerance.DefaultPerceptual.MaxMismatchedFraction);
        await Assert.That(resolved.MaxAlphaDelta)
            .IsEqualTo(GoldenTolerance.DefaultPerceptual.MaxAlphaDelta);
        await Assert.That(resolved.MinSsim).IsEqualTo(GoldenTolerance.DefaultPerceptual.MinSsim);
    }

    /// <summary>
    ///     Eight pixels of 102 400 at a 70-level difference: over the strict 32 ceiling, under the tier's
    ///     96, and inside the 0.01 % budget. The default tolerance rejects the same image outright, which
    ///     is what makes this a test of the tier rather than of the picture.
    /// </summary>
    [Test]
    public async Task TheGlyphTier_AdmitsABudgetedFewPixelsOverTheStrictCeiling()
    {
        byte[] expected = Solid(320, 320, new SKColor(100, 100, 100));
        byte[] actual = Pixels(320, 320, (x, y) =>
            y == 7 && x < 8 ? new SKColor(170, 170, 170) : new SKColor(100, 100, 100));

        GoldenComparison strict =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);
        await Assert.That(strict.Match).IsFalse();
        await Assert.That(strict.FailureReason).Contains("ceiling");

        GoldenComparison tiered =
            GoldenImageComparer.Compare(expected, actual, ChannelRulesOnly(_openGlyphTier));
        await Assert.That(tiered.MaxChannelDelta).IsEqualTo(70);
        await Assert.That(tiered.AboveCeilingFraction).IsEqualTo(8 / 102400.0).Within(1e-12);
        await Assert.That(tiered.FailureReason).IsNull();
    }

    /// <summary>Sixteen of the same pixels is over the 0.01 % budget, and the tier says so by name.</summary>
    [Test]
    public async Task TheGlyphTier_StillFails_WhenTooManyPixelsSpendIt()
    {
        byte[] expected = Solid(320, 320, new SKColor(100, 100, 100));
        byte[] actual = Pixels(320, 320, (x, y) =>
            y == 7 && x < 16 ? new SKColor(170, 170, 170) : new SKColor(100, 100, 100));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, ChannelRulesOnly(_openGlyphTier));

        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("glyph-tier budget");
    }

    /// <summary>
    ///     And the tier has a ceiling of its own. One pixel at 130 is past 96, so it fails however few
    ///     pixels are involved — a wrong colour is not a rasterisation difference at any count.
    /// </summary>
    [Test]
    public async Task TheGlyphTier_StillFails_AboveItsOwnCeiling()
    {
        byte[] expected = Solid(320, 320, new SKColor(100, 100, 100));
        byte[] actual = Pixels(320, 320, (x, y) =>
            x == 7 && y == 7 ? new SKColor(230, 230, 230) : new SKColor(100, 100, 100));

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, ChannelRulesOnly(_openGlyphTier));

        await Assert.That(result.MaxChannelDelta).IsEqualTo(130);
        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("ceiling 96");
    }

    private static SKColor Checker(int x, int y) =>
        (x + y) % 2 == 0 ? new SKColor(100, 100, 100) : new SKColor(106, 106, 106);

    private static byte[] Solid(int width, int height, SKColor color) =>
        Pixels(width, height, (_, _) => color);

    private static byte[] Pixels(int width, int height, Func<int, int, SKColor> pixel)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, pixel(x, y));
            }
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
