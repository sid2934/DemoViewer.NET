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
/// <param name="MaxChannelDelta">Perceptual only: the largest allowed per-channel difference.</param>
/// <param name="MaxMismatchedFraction">Fraction of pixels allowed to differ, e.g. 0.005 = 0.5%.</param>
/// <param name="MinSsim">Mean SSIM floor. Reported as 1 until C2 implements it.</param>
/// <param name="OutlierChannelDelta">C2: no single pixel may exceed this per-channel difference.</param>
/// <param name="MaxAlphaDelta">C2: the largest allowed alpha difference.</param>
/// <param name="MinWindowSsim">C2: the floor for the worst 11×11 SSIM window.</param>
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
/// <param name="MismatchedFraction">Fraction of pixels that differed at all.</param>
/// <param name="Ssim">Mean SSIM; 1 until C2 implements it.</param>
/// <param name="Width">Compared width, or the expected image's width when the sizes disagree.</param>
/// <param name="Height">Compared height.</param>
/// <param name="FailureReason">A one-line diagnosis when <paramref name="Match" /> is false.</param>
public readonly record struct GoldenComparison(
    bool Match,
    int MaxChannelDelta,
    double MismatchedFraction,
    double Ssim,
    int Width,
    int Height,
    string? FailureReason);

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
