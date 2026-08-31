#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <b>B1's exit criterion.</b> Re-renders each captured <c>SceneFixture</c> through the v2
///     compositor and compares it to the PNG the pre-v2 <c>Playback2DViewport</c> produced from the very
///     same push.
///     <para>
///         <b>What parity can honestly mean across two rasterisers.</b> The golden came from Avalonia's
///         <c>DrawingContext</c>; this comes from raw <c>SKCanvas</c> calls. Two things follow, and both
///         are why the gate is written against a delta <i>distribution</i> rather than a maximum. A
///         single anti-aliased edge pixel whose sub-pixel coverage rounds the other way produces a
///         full-amplitude difference, so the worst pixel in the frame is always large and says nothing.
///         And a mismatched-pixel count includes ±1 differences, of which resampling the radar produces
///         plenty. What distinguishes "the same picture" from "a regression" is how much of the frame
///         sits within a delta anyone could see.
///     </para>
///     <para>
///         The measured curve, the identified outliers and the sign-off are in
///         <c>docs/playback2d-v2/plans/B1-text-metrics-review.md</c> — the "reviewed, not auto-failed"
///         treatment design risk 1 and plan decision D-17 ask for, applied to the whole image rather
///         than to text alone.
///     </para>
///     <para>
///         The byte-exact half of the criterion is <c>SceneDeterminismTests</c>, which pins the v2
///         renderer against ITSELF. That is the regression gate protecting the port from here on; this
///         one proves the port landed where the old control was.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class GoldenParityTests
{
    // Measured on the nuke-multilevel corpus entry and written up in the review. Set just below the
    // observed values, so a genuine regression — a mis-placed layer, a wrong colour, a dropped pass —
    // moves the curve far enough to fail while resampling noise does not. Held in the pipeline rather
    // than here because Playback2DGoldenCaptureTests judges the SAME golden against the SAME curve, and
    // a second copy of these two numbers is a second thing to keep in step. Judge through Evaluate for
    // the same reason: sharing the numbers but re-deciding the pass locally leaves two gates to keep in
    // step instead of two constants.
    private static readonly GoldenDistribution Gate = GoldenDistribution.PreV2Capture;

    /// <summary>
    ///     The palette the corpus was captured under. The headless app resolves the <b>Light</b> theme
    ///     variant, so the pre-v2 PNGs have a light canvas; a headless re-render has no theme system to
    ///     ask, and defaulting to Dark here would compare two different colour schemes and report the
    ///     result as a total mismatch. (That is exactly what it did before this was noticed: 100 % of
    ///     pixels differing, on a picture that is otherwise pixel-for-pixel right.)
    /// </summary>
    private static ScenePalette CapturePalette => ScenePalette.Light;

    [Test]
    [Arguments("nuke-multilevel")]
    public async Task PreV2Golden_IsReproducedByTheCompositor(string name)
    {
        (SceneFixture fixture, byte[] expected) = LoadCorpusEntry(name);

        using SceneStage stage = new(fixture.Size, palette: CapturePalette);
        bool bound = stage.TryBindMap(fixture.MapName);
        byte[] actual = stage.RenderFixturePng(fixture);

        GoldenDeltaProfile profile = GoldenImageComparer.Analyze(expected, actual)
                                     ?? throw new InvalidOperationException("images are not comparable");

        Console.WriteLine($"[parity] {name} map={fixture.MapName} bundle={bound} " +
                          $"levels={stage.Renderer.Levels.Space.Levels.Count} " +
                          $"radar={stage.Renderer.Levels.Space.RadarBinding}");
        Console.WriteLine($"[parity] {name} {profile.Describe()}");

        await WriteArtifacts(name, expected, actual);

        await Assert.That(profile.Width).IsEqualTo(fixture.Size.Width);
        await Assert.That(Gate.Evaluate(profile)).IsNull();
    }

    /// <summary>
    ///     The same comparison with every text layer off. Isolating the glyphs is what turns "the images
    ///     differ" into a number a reviewer can act on: what is left is geometry, and geometry is what
    ///     the port was supposed to preserve.
    /// </summary>
    [Test]
    [Arguments("nuke-multilevel")]
    public async Task Geometry_WithoutText_IsAtLeastAsCloseAsTheFullFrame(string name)
    {
        (SceneFixture fixture, byte[] expected) = LoadCorpusEntry(name);

        using SceneStage withText = new(fixture.Size, palette: CapturePalette);
        withText.TryBindMap(fixture.MapName);
        byte[] withTextPng = withText.RenderFixturePng(fixture);
        GoldenDeltaProfile full = GoldenImageComparer.Analyze(expected, withTextPng)!.Value;

        using SceneStage noText = new(fixture.Size, palette: CapturePalette);
        noText.TryBindMap(fixture.MapName);
        noText.Markers.DrawLabels = false;
        noText.Compositor.SetEnabled(SceneLayerIds.FloorLabel, false);
        byte[] noTextPng = noText.RenderFixturePng(fixture);
        GoldenDeltaProfile geometry = GoldenImageComparer.Analyze(expected, noTextPng)!.Value;

        Console.WriteLine($"[parity] {name} full      {full.Describe()}");
        Console.WriteLine($"[parity] {name} geometry  {geometry.Describe()}");

        // Removing text can only move the DISTRIBUTION toward agreement. If it ever does not, the
        // difference is NOT the typeface and the review's conclusion is wrong. Asserted at every tier
        // the review quotes, rather than at ±8 alone.
        await Assert.That(geometry.FractionWithin(1)).IsGreaterThanOrEqualTo(full.FractionWithin(1));
        await Assert.That(geometry.FractionWithin(2)).IsGreaterThanOrEqualTo(full.FractionWithin(2));
        await Assert.That(geometry.FractionWithin(8)).IsGreaterThanOrEqualTo(full.FractionWithin(8));
        await Assert.That(geometry.FractionWithin(32)).IsGreaterThanOrEqualTo(full.FractionWithin(32));

        // The MAXIMUM is a different claim, and a weaker one — this file's own header says why a single
        // worst pixel across two rasterisers says nothing. It has exactly one legitimate way to move the
        // wrong way: the worst pixel in the frame can be one a GLYPH was sitting on, and taking the
        // glyph away uncovers it. (Observed on Linux: the worst pixel, (63,276), sits inside a marker
        // label and goes 200 → 201 when the label is removed.) So allow that case and no other, by
        // checking whether the text layers actually drew on that pixel.
        if (geometry.MaxChannelDelta > full.MaxChannelDelta)
        {
            bool underInk = PixelChanged(withTextPng, noTextPng, geometry.MaxDeltaX, geometry.MaxDeltaY);
            Console.WriteLine($"[parity] {name} geometry's worst pixel " +
                              $"({geometry.MaxDeltaX},{geometry.MaxDeltaY}) rose to " +
                              $"{geometry.MaxChannelDelta} from {full.MaxChannelDelta}; " +
                              $"under glyph ink: {underInk}");
            await Assert.That(underInk).IsTrue();
        }
    }

    /// <summary>Whether the two renders disagree at one pixel — i.e. the text layers drew there.</summary>
    /// <param name="withTextPng">The render with every layer on.</param>
    /// <param name="noTextPng">The same render with the text layers silenced.</param>
    /// <param name="x">Pixel X.</param>
    /// <param name="y">Pixel Y.</param>
    private static bool PixelChanged(byte[] withTextPng, byte[] noTextPng, int x, int y)
    {
        using SKBitmap a = SKBitmap.Decode(withTextPng);
        using SKBitmap b = SKBitmap.Decode(noTextPng);
        return a.GetPixel(x, y) != b.GetPixel(x, y);
    }

    private static (SceneFixture Fixture, byte[] Golden) LoadCorpusEntry(string name)
    {
        string scenePath = Path.Combine(FixtureCorpus.Root, "scenes", $"{name}.scene.json");
        if (!File.Exists(scenePath))
        {
            throw new SkipTestException($"no captured scene for '{name}'");
        }

        SceneFixture fixture = SceneFixture.Load(scenePath);
        string goldenPath = Path.Combine(FixtureCorpus.Root, "goldens", "cpu",
            $"{name}@{fixture.Size.Width}x{fixture.Size.Height}.png");
        if (!File.Exists(goldenPath))
        {
            throw new SkipTestException($"no captured pre-v2 golden for '{name}'");
        }

        return (fixture, File.ReadAllBytes(goldenPath));
    }

    private static async Task WriteArtifacts(string name, byte[] expected, byte[] actual)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.parity-actual.png"), actual);

        if (GoldenImageComparer.CreateDiffPng(expected, actual) is { } diff)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.parity-diff.png"), diff);
        }

        Console.WriteLine($"[parity] artifacts in {dir}");
    }
}
