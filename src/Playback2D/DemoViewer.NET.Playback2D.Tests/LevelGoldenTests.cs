#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     What <see cref="SingleLayout" /> and the per-level radar binding actually draw, over the real
///     two-floor <c>nuke-multilevel</c> capture.
///     <para>
///         Two committed goldens (<c>nuke-single-upper</c>, <c>nuke-multilevel-noradar</c>) plus the
///         assertion that matters most for a phase that touched the shared pane machinery: the
///         <b>stacked</b> picture is byte-identical after a Stacked → Single → Stacked round trip. That
///         is the acceptance line "StackedLayout's output is byte-identical to B1's golden", proved
///         against the renderer rather than by committing the same PNG twice — B1's own
///         <c>GoldenParityTests</c> already pins the stacked path against the pre-v2 control.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class LevelGoldenTests
{
    private const string Corpus = "nuke-multilevel";
    private const string UpdateEnvVar = "PB2D_GOLDEN_UPDATE";

    [Test]
    public async Task SingleLayout_ShowsOneFloorFullHeight_AndMatchesItsGolden()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName);

        byte[] upper = RenderSingle(stage, fixture, TopMostIndex);

        await Assert.That(stage.Renderer.Panes.Panes).HasCount().EqualTo(1);
        await Assert.That(stage.Renderer.Panes.Panes[0].ViewportRect)
            .IsEqualTo(new SKRect(0, 0, fixture.Size.Width, fixture.Size.Height));

        await CompareOrWrite("nuke-single-upper", fixture.Size, upper);
    }

    /// <summary>
    ///     The two floors must not render the same picture. On this capture every marker is on the upper
    ///     floor, so the lower pane is the map with no players on it — which is exactly the difference a
    ///     level filter is supposed to make, and would be invisible if the single pane still passed the
    ///     "no Z filtering" sentinel.
    /// </summary>
    [Test]
    public async Task EachLevel_RendersItsOwnFloor_NotBoth()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName);

        byte[] lower = RenderSingle(stage, fixture, _ => 0);
        byte[] upper = RenderSingle(stage, fixture, TopMostIndex);

        GoldenComparison result = GoldenImageComparer.Compare(lower, upper, GoldenTolerance.ByteExact);
        Console.WriteLine($"[levels] lower vs upper differ on {result.MismatchedFraction:P4} of pixels");
        await Assert.That(result.Match).IsFalse();
    }

    [Test]
    public async Task NoRadarBinding_FallsThroughToTheGrid_AndMatchesItsGolden()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName, false);

        byte[] png = stage.RenderFixturePng(fixture);

        MapSpace space = stage.Renderer.Levels.Space;
        await Assert.That(space.Levels).HasCount().EqualTo(2);
        await Assert.That(space.RadarBinding).IsEqualTo(RadarBindingQuality.None);
        foreach (MapLevel level in space.Levels)
        {
            await Assert.That(level.HasRadar).IsFalse();
        }

        await CompareOrWrite("nuke-multilevel-noradar", fixture.Size, png);
    }

    /// <summary>
    ///     <b>The no-regression gate for the stacked path.</b> Switching to Single and back must return
    ///     the exact same pixels: if the id-keyed pane retention lost a camera, or the single-pane
    ///     sentinel leaked into the stacked branch, this is where it shows.
    /// </summary>
    [Test]
    public async Task StackedRender_IsByteIdentical_AfterASingleModeRoundTrip()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName);

        byte[] before = stage.RenderFixturePng(fixture);

        SingleLayout single = new();
        StackedLayout stacked = (StackedLayout)stage.Renderer.Panes.Policy;
        RenderSingle(stage, fixture, TopMostIndex, single);
        RenderSingle(stage, fixture, _ => 0, single);

        stage.Renderer.Panes.Policy = stacked;
        stage.Renderer.DisplayMode = LevelDisplayMode.Stacked;
        byte[] after = stage.RenderFixturePng(fixture);

        GoldenComparison result = GoldenImageComparer.Compare(before, after, GoldenTolerance.ByteExact);
        Console.WriteLine($"[levels] stacked round trip: match={result.Match} " +
                          $"maxDelta={result.MaxChannelDelta}");
        await Assert.That(result.Match).IsTrue();
    }

    /// <summary>
    ///     <b>The proof obligation behind <see cref="GoldenTolerance.ForTextBearingGolden" />.</b> The
    ///     glyph tier forgives a budgeted handful of pixels and one SSIM window off the platform that
    ///     authored the corpus. That is only defensible if what it forgives is glyphs — so this
    ///     re-renders each golden with the text layers off and uses the difference as an exact glyph-ink
    ///     mask.
    ///     <para>
    ///         Two assertions ride on that mask. The cheap one: no pixel outside the ink may exceed even
    ///         the ±8 band. The complete one: substitute the golden's own pixels under the ink and run
    ///         the frame through <see cref="GoldenTolerance.DefaultPerceptual" /> with <b>nothing</b>
    ///         relaxed — same ceiling, same 0.5 % budget, same alpha bound, both SSIM floors. Passing
    ///         that means the tier's allowance is spent on glyph ink and on nothing else.
    ///     </para>
    ///     <para>
    ///         It runs everywhere, not just off Windows. On the authoring platform it passes with zeroes,
    ///         which is itself worth pinning: it is the assertion that would go red first if the
    ///         rasteriser difference ever stopped being confined to text.
    ///     </para>
    /// </summary>
    /// <param name="name">The golden to attribute.</param>
    [Test]
    [Arguments("nuke-multilevel-noradar")]
    [Arguments("nuke-single-upper")]
    public async Task EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk(string name)
    {
        SceneFixture fixture = LoadNuke();
        string goldenPath = GoldenPath(name, fixture.Size);
        if (!File.Exists(goldenPath))
        {
            throw new SkipTestException($"no golden at {goldenPath}");
        }

        byte[] goldenPng = await File.ReadAllBytesAsync(goldenPath);
        using SKBitmap golden = Decode(goldenPng);
        using SKBitmap actual = Decode(Render(name, fixture, true));
        using SKBitmap noText = Decode(Render(name, fixture, false));
        using SKBitmap patched = new(golden.Width, golden.Height, SKColorType.Rgba8888,
            SKAlphaType.Premul);

        int strictCeiling = GoldenTolerance.DefaultPerceptual.OutlierChannelDelta;
        int softCeiling = GoldenTolerance.DefaultPerceptual.MaxChannelDelta;
        int worstOutsideInk = 0, worstUnderInk = 0, worstX = 0, worstY = 0;
        long inkPixels = 0, overCeilingOutsideInk = 0, overCeilingUnderInk = 0;

        for (int y = 0; y < golden.Height; y++)
        {
            for (int x = 0; x < golden.Width; x++)
            {
                SKColor e = golden.GetPixel(x, y);
                SKColor a = actual.GetPixel(x, y);
                bool underInk = a != noText.GetPixel(x, y);
                int delta = Math.Max(Math.Abs(e.Red - a.Red),
                    Math.Max(Math.Abs(e.Green - a.Green), Math.Abs(e.Blue - a.Blue)));

                // The glyph tier's allowance, neutralised: under the ink the golden judges itself, so
                // whatever survives is by construction NOT a text difference.
                patched.SetPixel(x, y, underInk ? e : a);

                if (underInk)
                {
                    inkPixels++;
                    worstUnderInk = Math.Max(worstUnderInk, delta);
                    if (delta > strictCeiling)
                    {
                        overCeilingUnderInk++;
                    }

                    continue;
                }

                if (delta > worstOutsideInk)
                {
                    worstOutsideInk = delta;
                    worstX = x;
                    worstY = y;
                }

                if (delta > strictCeiling)
                {
                    overCeilingOutsideInk++;
                }
            }
        }

        Console.WriteLine($"[attribution] {name}: glyph ink {inkPixels} px; " +
                          $"worst under ink {worstUnderInk} ({overCeilingUnderInk} over {strictCeiling}); " +
                          $"worst outside ink {worstOutsideInk} at ({worstX},{worstY}) " +
                          $"({overCeilingOutsideInk} over {strictCeiling})");

        // Not a tautology: the mask is "where the text layers changed the picture", and the deltas are
        // measured against the COMMITTED golden. A geometry regression lands outside the mask.
        await Assert.That(overCeilingOutsideInk).IsEqualTo(0L);
        await Assert.That(worstOutsideInk).IsLessThanOrEqualTo(softCeiling);

        // And the whole unrelaxed policy over the glyph-patched frame — the assertion that actually
        // licenses the tier, because it re-imposes every limit ForTextBearingGolden loosens.
        GoldenComparison strict = GoldenImageComparer.Compare(goldenPng, Encode(patched),
            GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[attribution] {name} glyph-patched, unrelaxed: {strict.Summary}");
        await Assert.That(strict.FailureReason).IsNull();
    }

    private static int TopMostIndex(MapSpace space) => space.Levels.Count - 1;

    private static string GoldenPath(string name, SKSizeI size) =>
        Path.Combine(FixtureCorpus.Root, "goldens", "cpu", $"{name}@{size.Width}x{size.Height}.png");

    private static SKBitmap Decode(byte[] png) =>
        SKBitmap.Decode(png) ?? throw new InvalidOperationException("the image did not decode");

    private static byte[] Encode(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Renders one of the two level goldens, optionally with every text layer silenced.</summary>
    /// <param name="name">The golden name, which selects the radar binding and the pane layout.</param>
    /// <param name="fixture">The nuke capture both goldens are drawn from.</param>
    /// <param name="drawText">False silences marker initials and the floor caption.</param>
    private static byte[] Render(string name, SceneFixture fixture, bool drawText)
    {
        bool single = string.Equals(name, "nuke-single-upper", StringComparison.Ordinal);

        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName, single);
        if (!drawText)
        {
            stage.Markers.DrawLabels = false;
            stage.Compositor.SetEnabled(SceneLayerIds.FloorLabel, false);
        }

        return single ? RenderSingle(stage, fixture, TopMostIndex) : stage.RenderFixturePng(fixture);
    }

    private static byte[] RenderSingle(SceneStage stage, SceneFixture fixture,
        Func<MapSpace, int> pick, SingleLayout? policy = null)
    {
        // One stacked advance first, so the level set exists to pick from — the same two-advance shape
        // SceneStage.RenderFixturePng uses, and for the same reason.
        SceneTime time = fixture.Time;
        stage.Renderer.Advance(fixture.Frame, in time);

        SingleLayout single = policy ?? new SingleLayout();
        MapSpace space = stage.Renderer.Levels.Space;
        single.ActiveLevelId = space.Levels[pick(space)].Id;

        stage.Renderer.Panes.Policy = single;
        stage.Renderer.DisplayMode = LevelDisplayMode.Single;
        return stage.RenderFixturePng(fixture);
    }

    private static SceneFixture LoadNuke()
    {
        string path = Path.Combine(FixtureCorpus.Root, "scenes", $"{Corpus}.scene.json");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"no captured scene for '{Corpus}'");
        }

        return SceneFixture.Load(path);
    }

    private static async Task CompareOrWrite(string name, SKSizeI size, byte[] actual)
    {
        string goldenPath = GoldenPath(name, size);

        if (!File.Exists(goldenPath))
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"no golden at {goldenPath}. Regenerate deliberately with " +
                    "scripts/update-playback2d-goldens.sh.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllBytesAsync(goldenPath, actual);
            Console.WriteLine($"[golden] wrote {goldenPath} ({actual.Length} bytes)");
            return;
        }

        byte[] expected = await File.ReadAllBytesAsync(goldenPath);

        // ForTextBearingGolden, not DefaultPerceptual: both of these goldens carry marker initials and
        // (in the stacked case) floor captions, and glyph rasterisation is the one input this renderer
        // cannot pin. On the platform that authored the corpus the two tolerances are the same value.
        // EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk above is what keeps the difference honest.
        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.ForTextBearingGolden);
        Console.WriteLine($"[golden] {name} {result.Summary}");

        if (!result.Match)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.actual.png"), actual);
            if (GoldenImageComparer.CreateDiffPng(expected, actual) is { } diff)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.diff.png"), diff);
            }

            Console.WriteLine($"[golden] wrote the actual + diff images to {dir}");
        }

        await Assert.That(result.FailureReason).IsNull();
        await Assert.That(result.Match).IsTrue();
    }
}
