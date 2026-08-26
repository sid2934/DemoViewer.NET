#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <b>The proof obligation behind the glyph tier, for the corpus <c>dv2d golden</c> owns.</b>
///     <para>
///         <see cref="GoldenTolerance.ForLabelledFrame" /> lets a budgeted handful of pixels reach 96
///         rather than 32, and drops the worst-window SSIM floor to 0.88, on every platform that is not
///         the one which authored the PNGs. <c>SceneGoldenTests</c> proves that is only glyph ink for
///         the three synthetics and <c>LevelGoldenTests</c> for the two nuke goldens — but those two
///         suites render through the Pipeline, and six corpus entries are reachable only through the
///         CLI's own plan: the four radar-backed Mirage/Inferno frames, <c>nuke-single-upper</c>, and
///         the 1080p <c>full-scene-budget</c>. Until this suite existed those six were relaxed by that
///         budget and proved by nothing, which is the whole failure mode the rule is written against —
///         a relaxed number is worth exactly what the test beside it proves.
///     </para>
///     <para>
///         Each entry is re-rendered with the text layers silenced, and the difference between that and
///         the full render is an <b>exact</b> glyph-ink mask — not an approximation of one, since it is
///         literally the set of pixels the text layers painted. Two assertions ride on it. The cheap
///         one: outside the ink no pixel may exceed even the ±8 band. The complete one: substitute the
///         golden's own pixels under the ink, which neutralises every allowance the tier grants, and run
///         the result through <see cref="GoldenTolerance.DefaultPerceptual" /> with <b>nothing</b>
///         relaxed — the 32 ceiling, the 0.5 % budget, the alpha bound and both SSIM floors. A displaced
///         marker, a dropped smoke, a recoloured trail or a re-baked radar tile lands outside the mask
///         and survives the substitution.
///     </para>
///     <para>
///         It renders through <see cref="GoldenCommand.PlanFor" /> and
///         <see cref="GoldenCommand.RenderEntry" /> — the command's own statements, not a reproduction
///         of them. A proof that renders a lookalike stack proves nothing about the stack the gate
///         judges, and the two owners of these PNGs have drifted apart once already (D6 G-1).
///     </para>
///     <para>
///         It runs on every platform. On Windows the deltas are zeroes, which is worth pinning precisely
///         because it is the assertion that goes red first if the rasteriser difference ever stops being
///         confined to text.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class GoldenAttributionTests
{
    /// <summary>
    ///     Every entry <c>golden verify</c> actually judges, read off the manifest rather than listed
    ///     here — so a corpus entry added tomorrow is attributed tomorrow, and cannot be quietly relaxed
    ///     by <see cref="GoldenTolerance.ForLabelledFrame" /> while nothing proves what it forgives.
    ///     <see cref="EveryEntryTheGateJudges_IsAlsoAttributed" /> asserts the two sets are the same.
    /// </summary>
    public static IEnumerable<Func<string>> AttributedEntries()
    {
        foreach (string name in Names())
        {
            yield return () => name;
        }
    }

    /// <param name="name">The corpus entry to attribute.</param>
    [Test]
    [MethodDataSource(nameof(AttributedEntries))]
    public async Task EveryPixelOverTheStrictCeiling_LiesUnderGlyphInk(string name)
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);
        GoldenCorpusEntry entry = corpus.Find(name)
                                 ?? throw new InvalidOperationException($"no corpus entry '{name}'");
        SceneFixture fixture = SceneFixture.Load(entry.ScenePath);

        string goldenPath = entry.GoldenPath(RenderBackend.CpuRaster);
        if (!File.Exists(goldenPath))
        {
            // Not a skip: `golden verify` reports a non-pending entry with no golden as `missing` and
            // exits 4, so silently passing here would hide a corpus defect the gate fails on.
            throw new InvalidOperationException(
                $"no golden at {goldenPath}. Regenerate deliberately with " +
                "scripts/update-playback2d-goldens.sh.");
        }

        byte[] goldenPng = await File.ReadAllBytesAsync(goldenPath);

        // Whole-bitmap reads rather than GetPixel per pixel: full-scene-budget is 1920x1080, and four
        // bitmaps' worth of per-pixel interop across 2.07 M pixels is minutes of CI time for a number
        // one marshalled array gives in milliseconds.
        SKColor[] golden = Pixels(goldenPng, out int width, out int height);
        SKColor[] actual = Pixels(Render(corpus, entry, fixture, true), out _, out _);
        SKColor[] noText = Pixels(Render(corpus, entry, fixture, false), out _, out _);
        SKColor[] patched = new SKColor[golden.Length];

        // A golden is named for its size and the render is pinned to it, so this cannot drift — but an
        // IndexOutOfRange three lines down would be a terrible way to learn that it had.
        if (actual.Length != golden.Length || noText.Length != golden.Length)
        {
            throw new InvalidOperationException(
                $"{name}: the golden is {width}x{height} and the render is not.");
        }

        int strictCeiling = GoldenTolerance.DefaultPerceptual.OutlierChannelDelta;
        int softCeiling = GoldenTolerance.DefaultPerceptual.MaxChannelDelta;
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
                if (delta > strictCeiling)
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

            if (delta > strictCeiling)
            {
                overCeilingOutsideInk++;
            }
        }

        // The budget read off the SHIPPING tolerance rather than restated: on the authoring platform
        // the tier is closed and this prints 0, which is the honest number there.
        int labels = GoldenCommand.LabelCount(fixture);
        long budget = (long)(GoldenCommand.ToleranceFor(entry, labels, null).MaxGlyphOutlierFraction
                             * width * height);
        Console.WriteLine($"[attribution] {name}: {labels} labels, glyph ink {inkPixels} px; " +
                          $"worst under ink {worstUnderInk} ({overCeilingUnderInk} over {strictCeiling}" +
                          $" = {(labels == 0 ? 0 : overCeilingUnderInk / (double)labels):F2} per label, " +
                          $"budget {budget} px); " +
                          $"worst outside ink {worstOutsideInk} at ({worstX},{worstY}) " +
                          $"({overCeilingOutsideInk} over {strictCeiling})");

        // A silenced render that silenced nothing would make the whole proof vacuous on Windows, where
        // every delta is zero and an empty mask still passes everything below. It is not hypothetical:
        // the mask depends on MarkerLayer being findable under SceneLayerIds.Markers, and a renamed id
        // would fail open rather than closed.
        if (labels > 0)
        {
            await Assert.That(inkPixels).IsGreaterThan(0L);
        }

        // Not a tautology: the mask is "where the text layers changed the picture", and the deltas are
        // measured against the COMMITTED golden. A geometry regression lands outside the mask.
        await Assert.That(overCeilingOutsideInk).IsEqualTo(0L);
        await Assert.That(worstOutsideInk).IsLessThanOrEqualTo(softCeiling);

        // And the whole unrelaxed policy over the glyph-patched frame — the assertion that actually
        // licenses the budget, because it re-imposes every limit ForLabelledFrame loosens.
        GoldenComparison strict = GoldenImageComparer.Compare(goldenPng,
            Encode(patched, width, height), GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[attribution] {name} glyph-patched, unrelaxed: {strict.Summary}");
        await Assert.That(strict.FailureReason).IsNull();
    }

    /// <summary>
    ///     The set this suite attributes is the set the gate relaxes — asserted, not assumed. Adding a
    ///     corpus entry that <c>golden verify</c> judges, without it appearing here, means a frame whose
    ///     glyph allowance nothing accounts for; this is the assertion that makes that impossible to do
    ///     by accident.
    /// </summary>
    [Test]
    public async Task EveryEntryTheGateJudges_IsAlsoAttributed()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");

        HashSet<string> judged =
        [
            .. ((JsonArray)run.Json()["results"]!)
            .Where(static r => r!["status"]!.GetValue<string>() is "match" or "mismatch")
            .Select(static r => r!["name"]!.GetValue<string>())
        ];

        HashSet<string> attributed = [.. Names()];
        Console.WriteLine($"[attribution] judged={string.Join(",", judged.Order(StringComparer.Ordinal))}");
        await Assert.That(attributed.SetEquals(judged)).IsTrue();
    }

    /// <summary>
    ///     The names <c>GoldenCommand.Run</c> would reach a comparison for: everything that is neither
    ///     pending nor missing its scene, which is exactly the loop's own two `continue`s.
    /// </summary>
    private static IEnumerable<string> Names()
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);
        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            if (!entry.Pending && File.Exists(entry.ScenePath))
            {
                yield return entry.Name;
            }
        }
    }

    /// <summary>
    ///     The <c>dv2d golden</c> render, optionally with every text layer silenced.
    ///     <para>
    ///         <c>--assets</c> names the root the walk-up probe would find anyway. Explicit because the
    ///         ladder's middle rung is <c>DV2D_ASSETS</c>, and a developer who has that set for another
    ///         checkout would otherwise swap the radar art out from under the mask and see this fail as
    ///         a geometry difference.
    ///     </para>
    /// </summary>
    /// <param name="corpus">The corpus, for the annotation sidecar convention.</param>
    /// <param name="entry">The entry to render.</param>
    /// <param name="fixture">Its loaded scene.</param>
    /// <param name="drawText">
    ///     False silences marker initials and the floor caption, leaving a frame that differs from the
    ///     full render in exactly the glyph ink and nowhere else. That difference is the mask, so it is
    ///     produced by the render path under test rather than by a second one built to resemble it.
    /// </param>
    private static byte[] Render(GoldenCorpus corpus, GoldenCorpusEntry entry, SceneFixture fixture,
        bool drawText)
    {
        CliArgs args = CliArgs.Parse(["--cpu", "--assets", Dv2d.AssetsDirectory]);
        using SceneRenderPlan plan = GoldenCommand.PlanFor(args, corpus, entry);

        if (!drawText)
        {
            if (plan.Compositor.Find(SceneLayerIds.Markers) is MarkerLayer markers)
            {
                markers.DrawLabels = false;
            }

            plan.Compositor.SetEnabled(SceneLayerIds.FloorLabel, false);
        }

        return GoldenCommand.RenderEntry(plan, entry, fixture);
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
