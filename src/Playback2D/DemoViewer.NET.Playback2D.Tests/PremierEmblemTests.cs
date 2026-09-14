#region

using DemoViewer.NET.GameIcons;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Pixel-level guards on the baked Premier emblems. These live in this suite rather than beside the
///     other catalogue tests because they need an image decoder, and the icon assembly deliberately has
///     none.
/// </summary>
[Category("Render")]
public class PremierEmblemTests
{
    // Source artwork is 178x64; the badge is a parallelogram, so these two corners sit outside it.
    private const double ClearTopLeftX = 2 / 178.0, ClearTopLeftY = 2 / 64.0;
    private const double ClearBottomRightX = 174 / 178.0, ClearBottomRightY = 61 / 64.0;

    /// <summary>
    ///     The emblem's slanted corners must stay transparent on <b>every</b> tier.
    ///     <para>
    ///         This is a regression test with a specific bug behind it. The tint was first written with
    ///         <c>SKBlendMode.Multiply</c>, which is a separable Porter-Duff mode: against a transparent
    ///         destination it resolves to <c>ar = as + ad - as*ad = 1</c>, so it painted the tier colour
    ///         at full opacity across the whole bounding box and turned each badge into a rectangle. The
    ///         untinted plate was unaffected, which is exactly why only a per-tier check catches it.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("premier/unranked")]
    [Arguments("premier/tier0")]
    [Arguments("premier/tier1")]
    [Arguments("premier/tier2")]
    [Arguments("premier/tier3")]
    [Arguments("premier/tier4")]
    [Arguments("premier/tier5")]
    [Arguments("premier/tier6")]
    public async Task SlantedCorners_AreTransparent(string key)
    {
        foreach (int scale in IconCatalogue.Scales)
        {
            using SKBitmap bmp = Decode(key, scale);

            await Assert.That(Alpha(bmp, ClearTopLeftX, ClearTopLeftY))
                        .IsLessThan((byte)16).Because($"{key}@{scale} top-left corner");
            await Assert.That(Alpha(bmp, ClearBottomRightX, ClearBottomRightY))
                        .IsLessThan((byte)16).Because($"{key}@{scale} bottom-right corner");
        }
    }

    /// <summary>
    ///     The tint has to actually tint: each tier's bright chevron must land near that tier's colour,
    ///     and no two tiers may render the same. A blend mode that silently no-ops would still pass the
    ///     transparency test above.
    /// </summary>
    [Test]
    public async Task EachTier_CarriesItsOwnColour()
    {
        List<SKColor> chevrons = [];
        foreach (PremierTier tier in IconCatalogue.PremierTiers)
        {
            using SKBitmap bmp = Decode(tier.Key, 96);
            SKColor chevron = At(bmp, 2 / 178.0, 60 / 64.0);
            chevrons.Add(chevron);

            // Modulate against a near-white chevron reproduces the tier hue, so hue ordering survives:
            // a gold tier must not come back blue. Compared channel-wise against the declared colour.
            SKColor want = SKColor.Parse(tier.Hex);
            await Assert.That(chevron.Red > chevron.Blue).IsEqualTo(want.Red > want.Blue)
                        .Because($"{tier.Key} red-vs-blue balance");
        }

        await Assert.That(chevrons.Distinct().Count()).IsEqualTo(IconCatalogue.PremierTiers.Count);
    }

    /// <summary>The emblem is pre-coloured, so its interior is opaque — it is a plate, not a glyph.</summary>
    [Test]
    public async Task ThePlate_IsOpaque()
    {
        using SKBitmap bmp = Decode("premier/tier3", 96);
        await Assert.That(Alpha(bmp, 100 / 178.0, 32 / 64.0)).IsGreaterThan((byte)240);
    }

    private static SKBitmap Decode(string key, int scale)
    {
        IconRef icon = IconCatalogue.Get(key)
                       ?? throw new InvalidOperationException($"{key} was not baked");
        return SKBitmap.Decode(icon.Bytes(scale))
               ?? throw new InvalidOperationException($"{key}@{scale} did not decode");
    }

    private static SKColor At(SKBitmap bmp, double u, double v) =>
        bmp.GetPixel((int)(u * bmp.Width), (int)(v * bmp.Height));

    private static byte Alpha(SKBitmap bmp, double u, double v) => At(bmp, u, v).Alpha;
}
