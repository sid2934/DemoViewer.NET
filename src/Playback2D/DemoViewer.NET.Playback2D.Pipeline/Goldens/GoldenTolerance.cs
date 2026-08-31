#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Goldens;

/// <summary>How strictly a golden is compared.</summary>
public enum GoldenMode
{
    /// <summary>Every channel of every pixel must match exactly.</summary>
    ByteExact,

    /// <summary>Small per-channel differences are allowed within the stated budgets.</summary>
    Perceptual
}

/// <summary>
///     The comparison budget for one golden check.
///     <para>
///         CPU goldens are authoritative and compared byte-exact. The perceptual mode exists because
///         GPU rasterisation legitimately differs from software raster in anti-aliasing and rounding, and
///         because headless text metrics vary across operating systems — differences that must be
///         reviewed, not auto-failed. B0 ships the byte-exact path plus the channel/fraction budgets;
///         C2 implements the SSIM fields against these same tolerances.
///     </para>
/// </summary>
/// <param name="Mode">Byte-exact or perceptual.</param>
/// <param name="MaxChannelDelta">
///     Perceptual only: the per-channel difference a pixel may reach before it counts against
///     <paramref name="MaxMismatchedFraction" />. 8/255 is below the just-noticeable difference for a
///     single flat-region step and comfortably covers rounding on gradients and AA edges.
/// </param>
/// <param name="MaxMismatchedFraction">
///     Fraction of pixels allowed to exceed <paramref name="MaxChannelDelta" />, e.g. 0.005 = 0.5%. Sized
///     to allow a scene's whole anti-aliased fringe (well under 0.5% of a 1080p frame) without allowing a
///     recoloured or displaced element.
/// </param>
/// <param name="MinSsim">Mean SSIM floor over all windows.</param>
/// <param name="OutlierChannelDelta">
///     The per-channel difference no single pixel may exceed. A lone edge pixel can legitimately land on
///     the other side of a coverage rounding; 32/255 is far too small to hide a wrong colour, a missing
///     glyph or a displaced marker.
/// </param>
/// <param name="MaxAlphaDelta">
///     The largest allowed alpha difference, checked separately and far tighter: a backend that disagrees
///     about <i>coverage</i> is a real bug, not an anti-aliasing difference.
/// </param>
/// <param name="MinWindowSsim">
///     The floor for the worst 11×11 SSIM window. This is the metric that makes the policy mean anything:
///     a global mean averages away one missing glyph or one absent cone, and the worst window does not.
/// </param>
/// <param name="GlyphOutlierChannelDelta">
///     The <b>glyph tier</b>: a second, higher ceiling that a small, budgeted number of pixels may reach.
///     Zero — the default — disables the tier entirely, which leaves
///     <paramref name="OutlierChannelDelta" /> as the one hard ceiling and reproduces the pre-tier
///     behaviour exactly. It exists for one measured reason, stated in full on
///     <see cref="ForLabelledFrame" />: Skia's glyph rasteriser is not the same code on every
///     operating system, so a golden containing text cannot be held to a single-pixel ceiling sized for
///     anti-aliasing rounding.
/// </param>
/// <param name="MaxGlyphOutlierFraction">
///     How much of the frame may sit in the glyph tier — above <paramref name="OutlierChannelDelta" />
///     and at or below <paramref name="GlyphOutlierChannelDelta" />. Zero, the default, means "none",
///     i.e. any pixel over the hard ceiling fails. Sized to a few glyph stems, far below the area of any
///     element a regression could move.
/// </param>
public readonly record struct GoldenTolerance(
    GoldenMode Mode,
    int MaxChannelDelta,
    double MaxMismatchedFraction,
    double MinSsim,
    int OutlierChannelDelta = 32,
    int MaxAlphaDelta = 2,
    double MinWindowSsim = 0.95,
    int GlyphOutlierChannelDelta = 0,
    double MaxGlyphOutlierFraction = 0)
{
    /// <summary>
    ///     How many pixels of one two-letter label may land over <see cref="OutlierChannelDelta" /> when
    ///     the rasteriser is not the corpus's. Measured at 0.40-4.00 across all eight label-bearing
    ///     entries of the <c>dv2d golden</c> corpus; this is 1.5× the worst of those.
    /// </summary>
    private const int GlyphOutlierPixelsPerLabel = 6;

    /// <summary>Exact equality. The CPU corpus is authored and checked at this tolerance.</summary>
    public static readonly GoldenTolerance ByteExact = new(GoldenMode.ByteExact, 0, 0, 1.0, 0, 0, 1.0);

    /// <summary>The reviewed-difference budget for cross-backend and cross-OS comparisons.</summary>
    public static readonly GoldenTolerance DefaultPerceptual =
        new(GoldenMode.Perceptual, 8, 0.005, 0.995);

    /// <summary>Alias for <see cref="DefaultPerceptual" /> — the name C2's parity lane uses.</summary>
    public static GoldenTolerance CrossBackend => DefaultPerceptual;

    /// <summary>
    ///     Whether this process's glyph rasteriser is the one that rendered the committed goldens.
    /// </summary>
    /// <remarks>
    ///     <c>scripts/update-playback2d-goldens.sh</c> needs a staged <c>.dem</c> for its demo-derived
    ///     step, so the corpus is refreshed on a maintainer's Windows box and its glyphs carry the Windows
    ///     text stack's rasterisation. Re-authoring the corpus on another OS moves this predicate with it.
    /// </remarks>
    public static bool GlyphsMatchTheCorpus => OperatingSystem.IsWindows();

    /// <summary>
    ///     The budget for a golden that <b>contains text</b>, resolved for the platform this process is
    ///     running on and denominated in the frame's own text load. On the authoring platform it is
    ///     <see cref="DefaultPerceptual" />, unchanged, so nothing about the Windows gate moves.
    ///     Everywhere else it opens the glyph tier.
    ///     <para>
    ///         <b>Skia's glyph rasteriser is not the same code on every OS</b>, and an embedded typeface
    ///         does not change that: the same <c>SKTextBlob</c> at the same origin lays down 65 ink pixels
    ///         under the Windows text stack and 70 under FreeType. Geometry has no such problem, so exactly
    ///         three limits move — the 96 ceiling, the per-label budget, and a 0.88 worst-window floor.
    ///         <c>MaxChannelDelta</c>, the 0.5 % coverage budget, the alpha ceiling and the mean SSIM floor
    ///         are untouched.
    ///     </para>
    ///     <para>
    ///         <b>The budget is per label, not per frame</b>, because glyph ink does not scale with area:
    ///         a fraction-of-frame constant hands the roomy frame the larger allowance and starves the
    ///         cramped one. <see cref="GlyphOutlierPixelsPerLabel" /> is 1.5× the worst rate measured
    ///         across the corpus; the table is in <c>docs/playback2d-v2/dv2d.md</c>. Zero
    ///         <paramref name="labels" /> returns <see cref="DefaultPerceptual" /> itself. A count of
    ///         forgiven pixels cannot say WHICH pixels they were, so <see cref="GlyphAttribution" /> is
    ///         not optional.
    ///     </para>
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="labels">
    ///     How many text labels the frame draws — read off the scene, never tuned. Zero closes the tier.
    /// </param>
    public static GoldenTolerance ForLabelledFrame(int width, int height, int labels)
    {
        long area = (long)width * height;
        if (labels <= 0 || area <= 0 || GlyphsMatchTheCorpus)
        {
            return DefaultPerceptual;
        }

        return DefaultPerceptual with
        {
            GlyphOutlierChannelDelta = 96,
            MaxGlyphOutlierFraction = GlyphOutlierPixelsPerLabel * (double)labels / area,
            MinWindowSsim = 0.88
        };
    }
}

/// <summary>The result of one golden comparison.</summary>
/// <param name="Match">Whether the images are within tolerance.</param>
/// <param name="MaxChannelDelta">The largest per-channel difference observed.</param>
/// <param name="MismatchedFraction">Fraction of pixels that differed at all, however slightly.</param>
/// <param name="Ssim">Mean SSIM over all windows.</param>
/// <param name="Width">Compared width, or the expected image's width when the sizes disagree.</param>
/// <param name="Height">Compared height.</param>
/// <param name="FailureReason">A one-line diagnosis when <paramref name="Match" /> is false.</param>
/// <param name="OutlierFraction">
///     Fraction of pixels whose per-channel difference exceeded <c>GoldenTolerance.MaxChannelDelta</c> —
///     the quantity the 0.5% budget is actually spent on. Distinct from
///     <paramref name="MismatchedFraction" />, which counts a 1/255 wobble the same as a wrong colour.
/// </param>
/// <param name="MaxAlphaDelta">The largest alpha difference observed.</param>
/// <param name="MinWindowSsim">The worst single 11×11 SSIM window.</param>
/// <param name="AboveCeilingFraction">
///     Fraction of pixels whose per-channel difference exceeded <c>GoldenTolerance.OutlierChannelDelta</c>
///     — the pixels that spend the glyph tier's budget when one is open, and the pixels that fail the
///     comparison outright when one is not.
/// </param>
public readonly record struct GoldenComparison(
    bool Match,
    int MaxChannelDelta,
    double MismatchedFraction,
    double Ssim,
    int Width,
    int Height,
    string? FailureReason,
    double OutlierFraction = 0,
    int MaxAlphaDelta = 0,
    double MinWindowSsim = 1.0,
    double AboveCeilingFraction = 0)
{
    /// <summary>
    ///     A one-line, assertion-message-ready description. Reports every metric on success as well as
    ///     failure: a comparison that passed at SSIM 0.9951 against a 0.995 floor is a comparison worth
    ///     knowing about before it goes red on somebody else's machine.
    /// </summary>
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{(Match ? "match" : "MISMATCH")} {Width}x{Height} maxDelta={MaxChannelDelta} " +
        $"outliers={OutlierFraction:P4} aboveCeiling={AboveCeilingFraction:P4} " +
        $"alphaDelta={MaxAlphaDelta} ssim={Ssim:F5} " +
        $"minWindowSsim={MinWindowSsim:F5}{(FailureReason is null ? "" : " — " + FailureReason)}");
}

/// <summary>
///     The per-pixel delta distribution between two images of the same size, as produced by
///     <c>GoldenImageComparer.Analyze</c>. Every count is over the largest of the three colour-channel
///     differences at a pixel; alpha is tracked separately.
/// </summary>
/// <param name="Width">Image width.</param>
/// <param name="Height">Image height.</param>
/// <param name="IdenticalPixels">Pixels that matched bit for bit.</param>
/// <param name="MaxChannelDelta">The largest per-channel difference anywhere in the frame.</param>
/// <param name="MaxAlphaDelta">The largest alpha difference anywhere in the frame.</param>
/// <param name="MaxDeltaX">X of a pixel achieving <paramref name="MaxChannelDelta" />.</param>
/// <param name="MaxDeltaY">Y of that pixel — so a reviewer can go and look at it.</param>
/// <param name="CumulativeAtOrBelow">
///     256 entries; entry <c>d</c> is how many pixels differ by at most <c>d</c>.
/// </param>
public readonly record struct GoldenDeltaProfile(
    int Width,
    int Height,
    long IdenticalPixels,
    int MaxChannelDelta,
    int MaxAlphaDelta,
    int MaxDeltaX,
    int MaxDeltaY,
    IReadOnlyList<long> CumulativeAtOrBelow)
{
    /// <summary>Total pixels compared.</summary>
    public long TotalPixels => (long)Width * Height;

    /// <summary>The fraction of the frame whose per-channel difference is at most <paramref name="delta" />.</summary>
    /// <param name="delta">A per-channel difference, 0-255.</param>
    public double FractionWithin(int delta)
    {
        long total = TotalPixels;
        if (total == 0)
        {
            return 1.0;
        }

        int index = Math.Clamp(delta, 0, CumulativeAtOrBelow.Count - 1);
        return CumulativeAtOrBelow[index] / (double)total;
    }

    /// <summary>A one-line summary for a test log or a review write-up.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"identical {IdenticalPixels / (double)TotalPixels:P2}, " +
        $"within±1 {FractionWithin(1):P2}, within±2 {FractionWithin(2):P2}, " +
        $"within±8 {FractionWithin(8):P2}, within±32 {FractionWithin(32):P2}, " +
        $"max {MaxChannelDelta} at ({MaxDeltaX},{MaxDeltaY})");
}

/// <summary>
///     The gate for the <c>nuke-multilevel</c> golden — the pre-v2 control's own capture — stated over the
///     delta <b>distribution</b> instead of over a maximum.
///     <para>
///         Both of its readers reach it across a rasteriser boundary, and a maximum describes neither
///         crossing. The v2 compositor reaches it through raw <c>SKCanvas</c> where the golden came from
///         Avalonia's <c>DrawingContext</c>, so one anti-aliased edge pixel rounding the other way is
///         already a full-amplitude difference. The pre-v2 capture reaches it through the same drawing
///         code but a different OS text stack, and this golden is LIGHT-palette with a live marker's
///         initials drawn in black on a bright disc — so one coverage flip at a glyph stem is worth the
///         whole ~200 of text-to-disc contrast, roughly four times what the same flip costs on the dark
///         synthetic corpus. <see cref="GoldenTolerance.DefaultPerceptual" /> cannot express that and
///         <see cref="GoldenTolerance.ForLabelledFrame" /> cannot either: its 96 ceiling and its 6 px
///         per-label budget were both measured on that dark corpus. What separates "the same picture"
///         from "a regression" here is how much of the frame sits within a delta anyone could see.
///     </para>
///     <para>
///         The two numbers are the ones measured for <c>GoldenParityTests</c> and written up in
///         <c>docs/playback2d-v2/plans/B1-text-metrics-review.md</c>, set just below the observed curve so
///         a mis-placed layer, a wrong colour or a dropped pass moves it far enough to fail. They live
///         here so that both readers of the golden are held to one curve.
///     </para>
/// </summary>
/// <param name="MinWithin8">Fraction of the frame that must differ by at most 8 per channel.</param>
/// <param name="MinWithin32">Fraction that must differ by at most 32.</param>
public readonly record struct GoldenDistribution(double MinWithin8, double MinWithin32)
{
    /// <summary>The measured gate for the pre-v2 <c>nuke-multilevel</c> capture.</summary>
    public static readonly GoldenDistribution PreV2Capture = new(0.99, 0.995);

    /// <summary>
    ///     Judges one profile, returning null when it passes and a one-line diagnosis when it does not —
    ///     the same shape as <see cref="GoldenComparison.FailureReason" />, so a caller can assert on
    ///     either without a second code path.
    /// </summary>
    /// <param name="profile">The measured delta distribution.</param>
    public string? Evaluate(GoldenDeltaProfile profile)
    {
        double within8 = profile.FractionWithin(8);
        if (within8 < MinWithin8)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"only {within8:P4} of the frame is within ±8, under the {MinWithin8:P2} floor");
        }

        double within32 = profile.FractionWithin(32);
        return within32 < MinWithin32
            ? string.Create(CultureInfo.InvariantCulture,
                $"only {within32:P4} of the frame is within ±32, under the {MinWithin32:P2} floor")
            : null;
    }
}
