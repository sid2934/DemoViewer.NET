#region

using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The comparator every golden gate in the track routes through. A comparator that says "match" too
///     easily makes the whole corpus decorative, so the discriminating case here is the one-channel
///     difference: byte-exact must reject it and the perceptual budget must accept it.
/// </summary>
public class GoldenImageComparerTests
{
    [Test]
    public async Task IdenticalImages_MatchByteExact()
    {
        byte[] png = Png(16, 16, static (_, _) => new SKColor(10, 20, 30));

        GoldenComparison result = GoldenImageComparer.Compare(png, png, GoldenTolerance.ByteExact);

        await Assert.That(result.Match).IsTrue();
        await Assert.That(result.MaxChannelDelta).IsEqualTo(0);
        await Assert.That(result.MismatchedFraction).IsEqualTo(0);
        await Assert.That(result.FailureReason).IsNull();
        await Assert.That(GoldenImageComparer.CreateDiffPng(png, png)).IsNull();
    }

    [Test]
    public async Task OnePixelOneChannel_FailsByteExact_ButPassesDefaultPerceptual()
    {
        byte[] expected = Png(16, 16, static (_, _) => new SKColor(10, 20, 30));
        byte[] actual = Png(16, 16, static (x, y) =>
            x == 3 && y == 4 ? new SKColor(11, 20, 30) : new SKColor(10, 20, 30));

        GoldenComparison exact = GoldenImageComparer.Compare(expected, actual, GoldenTolerance.ByteExact);
        GoldenComparison perceptual =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(exact.Match).IsFalse();
        await Assert.That(exact.FailureReason).IsNotNull();
        await Assert.That(exact.MaxChannelDelta).IsEqualTo(1);
        await Assert.That(perceptual.Match).IsTrue();
    }

    [Test]
    public async Task LargeChannelDelta_FailsEvenPerceptually()
    {
        byte[] expected = Png(16, 16, static (_, _) => SKColors.Black);
        byte[] actual = Png(16, 16, static (x, y) => x == 8 && y == 8 ? SKColors.White : SKColors.Black);

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);

        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("channel delta");
    }

    [Test]
    public async Task SizeMismatch_IsAStatedFailure_NotAnException()
    {
        byte[] expected = Png(16, 16, static (_, _) => SKColors.Black);
        byte[] actual = Png(8, 16, static (_, _) => SKColors.Black);

        GoldenComparison result = GoldenImageComparer.Compare(expected, actual, GoldenTolerance.ByteExact);

        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("size mismatch");
        await Assert.That(GoldenImageComparer.CreateDiffPng(expected, actual)).IsNull();
    }

    [Test]
    public async Task UndecodablePayload_IsAStatedFailure()
    {
        byte[] expected = Png(4, 4, static (_, _) => SKColors.Black);

        GoldenComparison result =
            GoldenImageComparer.Compare(expected, [1, 2, 3], GoldenTolerance.ByteExact);

        await Assert.That(result.Match).IsFalse();
        await Assert.That(result.FailureReason).Contains("could not be decoded");
    }

    [Test]
    public async Task CreateDiffPng_IsNonNull_ExactlyWhenPixelsDiffer()
    {
        byte[] expected = Png(8, 8, static (_, _) => SKColors.Black);
        byte[] actual = Png(8, 8, static (x, y) => x == 2 && y == 2 ? SKColors.White : SKColors.Black);

        byte[]? diff = GoldenImageComparer.CreateDiffPng(expected, actual);
        await Assert.That(diff).IsNotNull();

        using SKBitmap decoded = SKBitmap.Decode(diff);
        await Assert.That(decoded.Width).IsEqualTo(8);
        await Assert.That(decoded.GetPixel(2, 2)).IsEqualTo(new SKColor(255, 0, 0, 255));
        await Assert.That(decoded.GetPixel(0, 0).Red).IsEqualTo(decoded.GetPixel(0, 0).Blue);
    }

    private static byte[] Png(int width, int height, Func<int, int, SKColor> pixel)
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
