#region

using System.Globalization;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Goldens;

/// <summary>
///     The measurement that licenses <see cref="GoldenTolerance.ForLabelledFrame" />: where a render
///     disagrees with its golden, and whether that disagreement is glyph ink.
///     <para>
///         <b>The mask is exact, not approximate.</b> It is the difference between the full render and
///         the same render with the text layers silenced, so it is the set of pixels the text layers
///         painted, produced by the render path under test rather than by a lookalike. Two assertions
///         ride on it: outside the ink no pixel may exceed even the ±8 band, and the golden's own pixels
///         substituted under the ink must pass <see cref="GoldenTolerance.DefaultPerceptual" /> with
///         nothing relaxed. A displaced marker, a dropped smoke, a recoloured trail or a re-baked radar
///         tile lands outside the mask and survives the substitution.
///     </para>
///     <para>
///         Three suites discharge this obligation, once per reader of the corpus:
///         <c>SceneGoldenTests</c> for the synthetics, <c>LevelGoldenTests</c> for the nuke goldens, and
///         <c>GoldenAttributionTests</c> for every entry <c>dv2d golden</c> judges. Each must guard on
///         <see cref="InkPixels" />: an empty mask makes every assertion under it vacuous.
///     </para>
/// </summary>
/// <param name="InkPixels">
///     Pixels the text layers painted.
///     <b>
///         Zero with labels on the frame means the mask silenced
///         nothing
///     </b>
///     , and every assertion below it is then vacuous. The caller must guard on this.
/// </param>
/// <param name="OverCeilingOutsideInk">
///     Pixels outside the mask over <see cref="GoldenTolerance.OutlierChannelDelta" />. The glyph tier
///     forgives none of these, so this is the number that must be zero.
/// </param>
/// <param name="OverCeilingUnderInk">Pixels under the mask over that same ceiling, what the tier buys.</param>
/// <param name="WorstOutsideInk">The largest per-channel difference outside the mask.</param>
/// <param name="WorstUnderInk">The largest per-channel difference under it.</param>
/// <param name="WorstX">X of a pixel achieving <paramref name="WorstOutsideInk" />, so it can be looked at.</param>
/// <param name="WorstY">Y of that pixel.</param>
/// <param name="GlyphPatchedPng">
///     The render with the golden's own pixels substituted under the ink, which neutralises every
///     allowance the tier grants. Run through <see cref="GoldenTolerance.DefaultPerceptual" /> it is the
///     assertion that actually licenses the budget.
/// </param>
public readonly record struct GlyphAttribution(
    long InkPixels,
    long OverCeilingOutsideInk,
    long OverCeilingUnderInk,
    int WorstOutsideInk,
    int WorstUnderInk,
    int WorstX,
    int WorstY,
    byte[] GlyphPatchedPng)
{
    /// <summary>
    ///     Attributes one render against its golden. Whole-bitmap reads rather than <c>GetPixel</c> per
    ///     pixel: <c>full-scene-budget</c> is 1920x1080, and four bitmaps' worth of per-pixel interop
    ///     across 2.07 M pixels is minutes of CI time for a number one marshalled array gives in
    ///     milliseconds.
    /// </summary>
    /// <param name="goldenPng">The committed golden.</param>
    /// <param name="renderPng">The full render.</param>
    /// <param name="silencedPng">The same render with every text layer off.</param>
    /// <exception cref="InvalidOperationException">A payload did not decode, or the sizes disagree.</exception>
    public static GlyphAttribution Measure(byte[] goldenPng, byte[] renderPng, byte[] silencedPng)
    {
        SKColor[] golden = Pixels(goldenPng, out int width, out int height);
        SKColor[] actual = Pixels(renderPng, out _, out _);
        SKColor[] noText = Pixels(silencedPng, out _, out _);
        SKColor[] patched = new SKColor[golden.Length];

        // A golden is named for its size and the render is pinned to it, so this cannot drift, but an
        // IndexOutOfRange three lines down would be a terrible way to learn that it had.
        if (actual.Length != golden.Length || noText.Length != golden.Length)
        {
            throw new InvalidOperationException(
                $"the golden is {width}x{height} and the render is not.");
        }

        int ceiling = GoldenTolerance.DefaultPerceptual.OutlierChannelDelta;
        int worstOutsideInk = 0, worstUnderInk = 0, worstX = 0, worstY = 0;
        long inkPixels = 0, overCeilingOutsideInk = 0, overCeilingUnderInk = 0;

        for (int i = 0; i < golden.Length; i++)
        {
            SKColor e = golden[i];
            SKColor a = actual[i];
            bool underInk = a != noText[i];
            int delta = Math.Max(Math.Abs(e.Red - a.Red),
                Math.Max(Math.Abs(e.Green - a.Green), Math.Abs(e.Blue - a.Blue)));

            // The glyph tier's allowance, neutralised: under the ink the golden judges itself, so
            // whatever survives is by construction NOT a text difference.
            patched[i] = underInk ? e : a;

            if (underInk)
            {
                inkPixels++;
                worstUnderInk = Math.Max(worstUnderInk, delta);
                if (delta > ceiling)
                {
                    overCeilingUnderInk++;
                }

                continue;
            }

            if (delta > worstOutsideInk)
            {
                worstOutsideInk = delta;
                worstX = i % width;
                worstY = i / width;
            }

            if (delta > ceiling)
            {
                overCeilingOutsideInk++;
            }
        }

        return new GlyphAttribution(inkPixels, overCeilingOutsideInk, overCeilingUnderInk,
            worstOutsideInk, worstUnderInk, worstX, worstY, Encode(patched, width, height));
    }

    /// <summary>
    ///     A one-line log of the measurement. The per-label rate it prints is what the constant in
    ///     <see cref="GoldenTolerance.ForLabelledFrame" /> is re-derived from, so it is readable off any
    ///     CI log rather than taken on trust.
    /// </summary>
    /// <param name="name">The corpus entry, for the log line.</param>
    /// <param name="labels">How many text labels the frame draws.</param>
    public string Describe(string name, int labels)
    {
        int ceiling = GoldenTolerance.DefaultPerceptual.OutlierChannelDelta;
        return string.Create(CultureInfo.InvariantCulture,
            $"{name}: {labels} labels, glyph ink {InkPixels} px; " +
            $"worst under ink {WorstUnderInk} ({OverCeilingUnderInk} over {ceiling} = " +
            $"{(labels == 0 ? 0 : OverCeilingUnderInk / (double)labels):F2} per label); " +
            $"worst outside ink {WorstOutsideInk} at ({WorstX},{WorstY}) " +
            $"({OverCeilingOutsideInk} over {ceiling})");
    }

    private static SKColor[] Pixels(byte[] png, out int width, out int height)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png)
                                ?? throw new InvalidOperationException("the image did not decode");
        width = bitmap.Width;
        height = bitmap.Height;
        return bitmap.Pixels;
    }

    private static byte[] Encode(SKColor[] pixels, int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Pixels = pixels;
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
