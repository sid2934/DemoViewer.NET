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
public readonly record struct GoldenTolerance(
    GoldenMode Mode,
    int MaxChannelDelta,
    double MaxMismatchedFraction,
    double MinSsim,
    int OutlierChannelDelta = 32,
    int MaxAlphaDelta = 2,
    double MinWindowSsim = 0.95)
{
    /// <summary>Exact equality. The CPU corpus is authored and checked at this tolerance.</summary>
    public static readonly GoldenTolerance ByteExact = new(GoldenMode.ByteExact, 0, 0, 1.0, 0, 0, 1.0);

    /// <summary>The reviewed-difference budget for cross-backend and cross-OS comparisons.</summary>
    public static readonly GoldenTolerance DefaultPerceptual =
        new(GoldenMode.Perceptual, 8, 0.005, 0.995, 32, 2, 0.95);

    /// <summary>Alias for <see cref="DefaultPerceptual" /> — the name C2's parity lane uses.</summary>
    public static GoldenTolerance CrossBackend => DefaultPerceptual;
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
    double MinWindowSsim = 1.0)
{
    /// <summary>
    ///     A one-line, assertion-message-ready description. Reports every metric on success as well as
    ///     failure: a comparison that passed at SSIM 0.9951 against a 0.995 floor is a comparison worth
    ///     knowing about before it goes red on somebody else's machine.
    /// </summary>
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{(Match ? "match" : "MISMATCH")} {Width}x{Height} maxDelta={MaxChannelDelta} " +
        $"outliers={OutlierFraction:P4} alphaDelta={MaxAlphaDelta} ssim={Ssim:F5} " +
        $"minWindowSsim={MinWindowSsim:F5}{(FailureReason is null ? "" : " — " + FailureReason)}");
}
