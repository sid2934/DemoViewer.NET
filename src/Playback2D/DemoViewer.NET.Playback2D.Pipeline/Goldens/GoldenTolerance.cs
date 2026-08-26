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
///     <see cref="ForTextBearingGolden" />: Skia's glyph rasteriser is not the same code on every
///     operating system, so a golden containing text cannot be held to a single-pixel ceiling sized for
///     anti-aliasing rounding.
/// </param>
/// <param name="MaxGlyphOutlierFraction">
///     How much of the frame may sit in the glyph tier — above <paramref name="OutlierChannelDelta" />
///     and at or below <paramref name="GlyphOutlierChannelDelta" />. Zero, the default, means "none",
///     i.e. any pixel over the hard ceiling fails. This is the budget that keeps the tier honest: it is
///     sized to a few glyph stems, far below the area of any element a regression could move.
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
    ///     the rasteriser is not the corpus's. Measured at 3.5 and 4.0 across the two label-bearing
    ///     synthetic fixtures; 1.5× that, and the whole derivation, is on
    ///     <see cref="ForLabelledFrame" />.
    /// </summary>
    private const int GlyphOutlierPixelsPerLabel = 6;

    /// <summary>Exact equality. The CPU corpus is authored and checked at this tolerance.</summary>
    public static readonly GoldenTolerance ByteExact = new(GoldenMode.ByteExact, 0, 0, 1.0, 0, 0, 1.0);

    /// <summary>The reviewed-difference budget for cross-backend and cross-OS comparisons.</summary>
    public static readonly GoldenTolerance DefaultPerceptual =
        new(GoldenMode.Perceptual, 8, 0.005, 0.995, 32, 2, 0.95);

    /// <summary>Alias for <see cref="DefaultPerceptual" /> — the name C2's parity lane uses.</summary>
    public static GoldenTolerance CrossBackend => DefaultPerceptual;

    /// <summary>
    ///     Whether this process's glyph rasteriser is the one that rendered the committed goldens.
    ///     <para>
    ///         The corpus is regenerated by <c>scripts/update-playback2d-goldens.sh</c>, and its
    ///         demo-derived step needs a staged <c>.dem</c>, so in practice it is refreshed on a
    ///         maintainer's Windows box — the glyphs baked into the committed PNGs carry the Windows text
    ///         stack's rasterisation. If the corpus is ever re-authored on another OS, this predicate is
    ///         the one line that has to move with it.
    ///     </para>
    /// </summary>
    public static bool GlyphsMatchTheCorpus => OperatingSystem.IsWindows();

    /// <summary>
    ///     The budget for a golden that <b>contains text</b>, resolved for the platform this process is
    ///     running on. On the authoring platform it is <see cref="DefaultPerceptual" />, unchanged, so
    ///     nothing about the Windows gate moves. Everywhere else it opens the glyph tier.
    ///     <para>
    ///         <b>Why the tier is necessary and why it is safe.</b> The typeface is embedded and the font
    ///         flags are pinned (<c>TextBlobCache</c>), which removes every input a golden could depend on
    ///         — except the rasteriser itself, which is not ours. Measured directly on the
    ///         <c>nuke-multilevel</c> corpus: the same <c>SKTextBlob</c>, drawn at the same origin into a
    ///         blank bitmap, lays down 65 ink pixels under the Windows text stack and 70 under FreeType,
    ///         and the blob's own measured bounds differ in the fourth decimal at 10 px, which shifts a
    ///         centred label by a fraction of a pixel on top of that. Geometry has no such problem —
    ///         Skia's own scan converter is deterministic across platforms, and outside the label ink
    ///         those same 900×900 frames agree to within <b>1/255</b>.
    ///     </para>
    ///     <para>
    ///         So the tier is scoped to exactly what the evidence supports. Three limits move and no
    ///         others: a handful of pixels may reach 96 rather than 32; the budget for those is 0.01 %,
    ///         against a measured need of 13 pixels in 810 000 (0.0016 %); and the worst 11×11 SSIM
    ///         window drops to 0.90, against a measured 0.9396 for a window sitting on a two-letter
    ///         label. <c>MaxChannelDelta</c>, the 0.5 % coverage budget, the alpha ceiling and the
    ///         <i>mean</i> SSIM floor are all untouched — the last of those measures 0.99995 on Linux,
    ///         nowhere near its 0.995 limit.
    ///     </para>
    ///     <para>
    ///         <b>The proof obligation.</b> A relaxed number is only worth what the test beside it
    ///         proves. <c>LevelGoldenTests.EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk</c> re-renders
    ///         each of these goldens with the text layers silenced, uses the difference as an exact
    ///         glyph-ink mask, substitutes the golden's own pixels there, and then runs the resulting
    ///         frame through <see cref="DefaultPerceptual" /> — <i>unrelaxed</i>, every rule, including
    ///         both SSIM floors. Everything this tier forgives therefore has to be glyph ink and nothing
    ///         else, and that is asserted on every platform, the authoring one included.
    ///     </para>
    /// </summary>
    public static GoldenTolerance ForTextBearingGolden => GlyphsMatchTheCorpus
        ? DefaultPerceptual
        : DefaultPerceptual with
        {
            GlyphOutlierChannelDelta = 96,
            MaxGlyphOutlierFraction = 0.0001,
            MinWindowSsim = 0.90
        };

    /// <summary>
    ///     The glyph tier for a frame whose text load is <b>stated rather than assumed</b>: the same tier
    ///     as <see cref="ForTextBearingGolden" />, with the one number that has no business being a
    ///     constant computed from the frame instead.
    ///     <para>
    ///         <b>Why a flat fraction is the wrong unit.</b> <see cref="ForTextBearingGolden" />'s 0.01 %
    ///         was sized against a nuke frame that needed 13 pixels of its 810 000. A fraction-of-frame
    ///         budget scales with <i>area</i>, and glyph ink does not — a 10 px label is 10 px whether it
    ///         sits in a 900×900 export or a 640×360 one. So the same 0.01 % is 81 pixels at 900×900 and
    ///         23 at 640×360: it hands the roomy frame the larger allowance and starves the cramped one.
    ///         The synthetic corpus is the cramped one <i>and</i> the crowded one — ten two-letter labels
    ///         in 230 400 pixels, needing 60 against those 23 — so it could not fit. Widening the flat
    ///         constant until it did would have multiplied the roomy frame's allowance by the same
    ///         factor, to some sixteen times the 13 pixels it actually needs, which is how a budget stops
    ///         being a gate.
    ///     </para>
    ///     <para>
    ///         <b>The two knobs move for different reasons, so only one of them scales.</b> The budget
    ///         counts pixels — an extensive quantity, so it is stated per label and divided by the frame
    ///         area the comparer wants a fraction of. The worst-window floor is an extremum over a
    ///         <i>fixed</i> 11×11 aperture: what one window can see of one glyph does not depend on the
    ///         frame or on how many other labels there are, so scaling it would be curve-fitting. Only the
    ///         <i>chance</i> of drawing a bad window rises with the label count, and a floor has to cover
    ///         the worst case rather than the typical one — which is why it is a constant set under the
    ///         worst measured, and why the ten-label frame is where that worst was measured.
    ///     </para>
    ///     <para>
    ///         <b>The measurement.</b> Taken by comparing the committed goldens against the frames the
    ///         ubuntu llvmpipe runner actually produced, i.e. against FreeType rather than the Windows
    ///         text stack. <c>synthetic-utility</c> (2 labels): 7 pixels over the strict ceiling, worst
    ///         channel delta 65, worst window 0.9304. <c>synthetic-tenplayers</c> (10 labels): 40 pixels
    ///         over, worst delta 80, worst window 0.8998. That is 3.5 and 4.0 pixels per two-letter
    ///         label. The attribution test measures the other half of the ratio — the ink itself is 117 px
    ///         over those 2 labels and 592 px over these 10, i.e. 58.5 and 59.2 px per label — so the
    ///         disagreement is <b>6.0 % and 6.8 % of the glyph ink</b> in two frames whose areas are
    ///         identical and whose text loads differ fivefold. Two independent quantities both come out
    ///         per-label and neither comes out per-area; that is the evidence the unit is right, not an
    ///         assumption behind it.
    ///     </para>
    ///     <para>
    ///         <see cref="GlyphOutlierPixelsPerLabel" /> is therefore 6 — 1.5× the worse observed rate,
    ///         and about a tenth of one label's ink, so the tier forgives at most ~10 % of the text
    ///         against a measured 6-7 % and a different FreeType build has somewhere to go. The 96
    ///         ceiling is <see cref="ForTextBearingGolden" />'s own and is not re-derived: 80 is the worst
    ///         delta seen. Everything else measured comfortably inside <see cref="DefaultPerceptual" />
    ///         and is therefore left there — 0.0855 % of pixels over ±8 against a 0.5 % budget, mean SSIM
    ///         0.99988 against a 0.995 floor, and an alpha delta of exactly 0.
    ///     </para>
    ///     <para>
    ///         <b>What the budget deliberately does not try to do.</b> Ten labels earn 60 pixels and one
    ///         label's ink is 59, so a label that vanished outright would fit inside the allowance it
    ///         helped earn. Sizing the constant around that would be the wrong fix — it would have to drop
    ///         below the disagreement actually measured — because a count of forgiven pixels cannot tell
    ///         which pixels they were. That is the attribution test's job and the reason it is not
    ///         optional: a vanished label is absent from the silenced render too, so the mask is empty
    ///         where it used to be and its pixels are judged <i>outside</i> the ink, unrelaxed, against
    ///         the golden that still has it.
    ///     </para>
    ///     <para>
    ///         <b>A frame with no text gets no allowance</b>, which a constant could not express:
    ///         <paramref name="labels" /> of zero returns <see cref="DefaultPerceptual" /> itself, so
    ///         <c>synthetic-empty</c> is held to the unrelaxed gate on every platform and would go red on
    ///         a single pixel over 32. It currently matches its golden byte for byte on Linux.
    ///     </para>
    ///     <para>
    ///         <b>The proof obligation</b> is <see cref="ForTextBearingGolden" />'s, discharged for this
    ///         corpus by <c>SceneGoldenTests.EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk</c>: it
    ///         re-renders each fixture with the labels silenced, uses the difference as an exact glyph-ink
    ///         mask, substitutes the golden's own pixels under the ink and puts the result through
    ///         <see cref="DefaultPerceptual" /> unrelaxed — so everything this budget forgives has to be
    ///         glyph ink, asserted on Windows as well as off it. That test also prints the per-label rate
    ///         it observes, which is where the numbers above are re-measured from rather than trusted.
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
    public string Describe() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"identical {IdenticalPixels / (double)TotalPixels:P2}, " +
        $"within±1 {FractionWithin(1):P2}, within±2 {FractionWithin(2):P2}, " +
        $"within±8 {FractionWithin(8):P2}, within±32 {FractionWithin(32):P2}, " +
        $"max {MaxChannelDelta} at ({MaxDeltaX},{MaxDeltaY})");
}
