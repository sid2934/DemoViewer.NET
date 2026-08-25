#region

using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests.Rendering;

/// <summary>
///     The perceptual half of the comparator, which C2 fills in: the outlier budget, the alpha bound and
///     SSIM (plans/C2-gpu-provider.md §7.1, §7.3).
///     <para>
///         <b>A comparator has to be trustworthy before it is allowed to judge anything.</b> The
///         discriminating case is the one-pixel translation: every pixel is close to <i>a</i> pixel, so a
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
    ///     is a <i>ratio</i>, so the same absolute step is a larger relative change on a dark base; the
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
    ///     Two percent of pixels lifted by 20: under the 32 ceiling, so the <i>fraction</i> rule is what
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
    ///     The boundary the plan asks to be pinned. A lone pixel lifted 30 levels sits <i>under</i> both
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
    ///     Alpha gets its own, far tighter bound: a backend that disagrees about <i>coverage</i> is a real
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
