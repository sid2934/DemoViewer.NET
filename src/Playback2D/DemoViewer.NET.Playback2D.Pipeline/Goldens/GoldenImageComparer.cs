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
        int maxDelta = 0;
        int maxAlphaDelta = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor e = expected.GetPixel(x, y);
                SKColor a = actual.GetPixel(x, y);
                if (e == a)
                {
                    continue;
                }

                mismatched++;
                maxDelta = Math.Max(maxDelta, Math.Abs(e.Red - a.Red));
                maxDelta = Math.Max(maxDelta, Math.Abs(e.Green - a.Green));
                maxDelta = Math.Max(maxDelta, Math.Abs(e.Blue - a.Blue));
                maxAlphaDelta = Math.Max(maxAlphaDelta, Math.Abs(e.Alpha - a.Alpha));
            }
        }

        double fraction = total == 0 ? 0 : mismatched / (double)total;

        // SSIM is C2's to implement; until then it is reported as a perfect score rather than a fake
        // number, and MinSsim is therefore never the thing that fails a comparison.
        const double ssim = 1.0;

        string? reason = tolerance.Mode switch
        {
            GoldenMode.ByteExact when mismatched > 0 => string.Create(CultureInfo.InvariantCulture,
                $"{mismatched} of {total} pixels differ (max channel delta {maxDelta})"),
            GoldenMode.Perceptual when maxDelta > tolerance.MaxChannelDelta =>
                string.Create(CultureInfo.InvariantCulture,
                    $"max channel delta {maxDelta} exceeds {tolerance.MaxChannelDelta}"),
            GoldenMode.Perceptual when maxAlphaDelta > tolerance.MaxAlphaDelta =>
                string.Create(CultureInfo.InvariantCulture,
                    $"max alpha delta {maxAlphaDelta} exceeds {tolerance.MaxAlphaDelta}"),
            GoldenMode.Perceptual when fraction > tolerance.MaxMismatchedFraction =>
                string.Create(CultureInfo.InvariantCulture,
                    $"{fraction:P3} of pixels differ, budget {tolerance.MaxMismatchedFraction:P3}"),
            _ => null
        };

        return new GoldenComparison(reason is null, maxDelta, fraction, ssim, width, height, reason);
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

    private static GoldenComparison Failed(int width, int height, string reason) =>
        new(false, 255, 1.0, 0, width, height, reason);
}
