#region

using System.Globalization;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Goldens;

/// <summary>
///     The one image comparator in the repo. B0's capture test, B1's parity gate, C1's
///     <c>dv2d golden</c> command and C2's cross-backend lane all call this — a second implementation
///     would mean two different definitions of "the goldens are green".
/// </summary>
public static class GoldenImageComparer
{
    /// <summary>
    ///     Compares two encoded PNGs against a tolerance. A size mismatch or an undecodable payload is a
    ///     failure with a stated reason, never an exception: a golden check reports, it does not crash.
    /// </summary>
    /// <param name="expectedPng">The committed golden.</param>
    /// <param name="actualPng">The freshly captured image.</param>
    /// <param name="tolerance">The comparison budget.</param>
    public static GoldenComparison Compare(ReadOnlySpan<byte> expectedPng, ReadOnlySpan<byte> actualPng,
        GoldenTolerance tolerance)
    {
        using SKBitmap? expected = Decode(expectedPng);
        using SKBitmap? actual = Decode(actualPng);

        if (expected is null || actual is null)
        {
            string which = expected is null ? "expected" : "actual";
            return Failed(0, 0, $"the {which} image could not be decoded as a PNG");
        }

        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return Failed(expected.Width, expected.Height,
                string.Create(CultureInfo.InvariantCulture,
                    $"size mismatch: expected {expected.Width}x{expected.Height}, " +
                    $"got {actual.Width}x{actual.Height}"));
        }

        int width = expected.Width;
        int height = expected.Height;
        long total = (long)width * height;
        long mismatched = 0;
        long outliers = 0;
        int maxDelta = 0;
        int maxAlphaDelta = 0;

        bool perceptual = tolerance.Mode == GoldenMode.Perceptual;
        float[]? lumaExpected = perceptual ? new float[total] : null;
        float[]? lumaActual = perceptual ? new float[total] : null;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor e = expected.GetPixel(x, y);
                SKColor a = actual.GetPixel(x, y);

                if (lumaExpected is not null && lumaActual is not null)
                {
                    int i = y * width + x;
                    lumaExpected[i] = Luma(e);
                    lumaActual[i] = Luma(a);
                }

                if (e == a)
                {
                    continue;
                }

                mismatched++;
                int delta = Math.Max(Math.Abs(e.Red - a.Red),
                    Math.Max(Math.Abs(e.Green - a.Green), Math.Abs(e.Blue - a.Blue)));
                maxDelta = Math.Max(maxDelta, delta);
                maxAlphaDelta = Math.Max(maxAlphaDelta, Math.Abs(e.Alpha - a.Alpha));
                if (delta > tolerance.MaxChannelDelta)
                {
                    outliers++;
                }
            }
        }

        double fraction = total == 0 ? 0 : mismatched / (double)total;
        double outlierFraction = total == 0 ? 0 : outliers / (double)total;

        // Identical pixels are identical structure; skipping the convolution here is not an
        // approximation, and it keeps the byte-exact-in-practice cases (most golden runs) cheap.
        double meanSsim = 1.0;
        double minWindowSsim = 1.0;
        if (perceptual && mismatched > 0)
        {
            Ssim.Compute(lumaExpected!, lumaActual!, width, height, out meanSsim, out minWindowSsim);
        }

        // The §7.3 rule, in the order that makes a failure message most informative: a wrong colour
        // first, then wrong coverage, then too much AA disagreement, then structure. Note that
        // MaxChannelDelta is a BUDGET THRESHOLD, not a hard ceiling — the ceiling is
        // OutlierChannelDelta. One edge pixel landing on the far side of a coverage rounding must not
        // fail a frame; half a percent of them must.
        string? reason = tolerance.Mode switch
        {
            GoldenMode.ByteExact when mismatched > 0 => string.Create(CultureInfo.InvariantCulture,
                $"{mismatched} of {total} pixels differ (max channel delta {maxDelta})"),
            GoldenMode.Perceptual when maxDelta > tolerance.OutlierChannelDelta =>
                string.Create(CultureInfo.InvariantCulture,
                    $"max channel delta {maxDelta} exceeds the outlier ceiling " +
                    $"{tolerance.OutlierChannelDelta}"),
            GoldenMode.Perceptual when maxAlphaDelta > tolerance.MaxAlphaDelta =>
                string.Create(CultureInfo.InvariantCulture,
                    $"max alpha delta {maxAlphaDelta} exceeds {tolerance.MaxAlphaDelta}"),
            GoldenMode.Perceptual when outlierFraction > tolerance.MaxMismatchedFraction =>
                string.Create(CultureInfo.InvariantCulture,
                    $"{outlierFraction:P3} of pixels differ by more than {tolerance.MaxChannelDelta}, " +
                    $"budget {tolerance.MaxMismatchedFraction:P3}"),
            GoldenMode.Perceptual when meanSsim < tolerance.MinSsim =>
                string.Create(CultureInfo.InvariantCulture,
                    $"mean SSIM {meanSsim:F5} is below {tolerance.MinSsim:F5}"),
            GoldenMode.Perceptual when minWindowSsim < tolerance.MinWindowSsim =>
                string.Create(CultureInfo.InvariantCulture,
                    $"worst SSIM window {minWindowSsim:F5} is below {tolerance.MinWindowSsim:F5}"),
            _ => null
        };

        return new GoldenComparison(reason is null, maxDelta, fraction, meanSsim, width, height, reason,
            outlierFraction, maxAlphaDelta, minWindowSsim);
    }

    /// <summary>
    ///     A diff image: the expected frame desaturated, with every differing pixel tinted red. Returns
    ///     null when the images match or either cannot be decoded — so a non-null result always means
    ///     "here is what changed".
    /// </summary>
    /// <param name="expectedPng">The committed golden.</param>
    /// <param name="actualPng">The freshly captured image.</param>
    public static byte[]? CreateDiffPng(ReadOnlySpan<byte> expectedPng, ReadOnlySpan<byte> actualPng)
    {
        using SKBitmap? expected = Decode(expectedPng);
        using SKBitmap? actual = Decode(actualPng);

        if (expected is null || actual is null || expected.Width != actual.Width ||
            expected.Height != actual.Height)
        {
            return null;
        }

        using SKBitmap diff = new(expected.Width, expected.Height, SKColorType.Rgba8888,
            SKAlphaType.Premul);
        bool anyDifference = false;

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                SKColor e = expected.GetPixel(x, y);
                SKColor a = actual.GetPixel(x, y);
                if (e == a)
                {
                    byte grey = (byte)((e.Red * 30 + e.Green * 59 + e.Blue * 11) / 100 / 3);
                    diff.SetPixel(x, y, new SKColor(grey, grey, grey, 255));
                    continue;
                }

                anyDifference = true;
                diff.SetPixel(x, y, new SKColor(255, 0, 0, 255));
            }
        }

        if (!anyDifference)
        {
            return null;
        }

        using SKImage image = SKImage.FromBitmap(diff);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap? Decode(ReadOnlySpan<byte> png)
    {
        if (png.IsEmpty)
        {
            return null;
        }

        try
        {
            using SKData data = SKData.CreateCopy(png.ToArray());
            return SKBitmap.Decode(data);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // Rec. 709 luma on the unpremultiplied channels SKColor already hands back. SSIM is defined on a
    // single-channel signal, and luma is the channel human structure perception actually rides on.
    private static float Luma(SKColor c) => (0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue);

    private static GoldenComparison Failed(int width, int height, string reason) =>
        new(false, 255, 1.0, 0, width, height, reason, 1.0, 255, 0);
}
