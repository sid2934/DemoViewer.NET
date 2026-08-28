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
///     See <see cref="GlyphAttribution" /> for the mask and what it licenses.
///     <para>
///         <c>SceneGoldenTests</c> and <c>LevelGoldenTests</c> discharge the same obligation for the
///         synthetics and the nuke goldens, but both render through the Pipeline. Six corpus entries are
///         reachable only through the CLI's own plan: the four radar-backed Mirage/Inferno frames,
///         <c>nuke-single-upper</c>, and the 1080p <c>full-scene-budget</c>.
///     </para>
///     <para>
///         <b>
///             It renders through <see cref="GoldenCommand.PlanFor" /> and
///             <see cref="GoldenCommand.RenderEntry" />
///         </b>
///         , the command's own statements, not a
///         reproduction of them. A proof that renders a lookalike stack proves nothing about the stack
///         the gate judges, and the two owners of these PNGs have drifted apart once already.
///     </para>
///     <para>
///         It runs on every platform. On Windows the deltas are zeroes, and that assertion goes red first
///         if the rasteriser difference ever stops being confined to text.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class GoldenAttributionTests
{
    /// <summary>
    ///     Every entry <c>golden verify</c> actually judges, read off the manifest rather than listed
    ///     here, so a corpus entry added tomorrow is attributed tomorrow and cannot be quietly relaxed
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
        GlyphAttribution ink = GlyphAttribution.Measure(goldenPng,
            Render(corpus, entry, fixture, true), Render(corpus, entry, fixture, false));

        // The budget read off the SHIPPING tolerance rather than restated: on the authoring platform
        // the tier is closed and this prints 0.
        int labels = GoldenCommand.LabelCount(fixture);
        SKSizeI size = entry.Size;
        long budget = (long)(GoldenCommand.ToleranceFor(entry, labels, null).MaxGlyphOutlierFraction
                             * size.Width * size.Height);
        Console.WriteLine($"[attribution] {ink.Describe(name, labels)}, budget {budget} px");

        // The mask depends on MarkerLayer being findable under SceneLayerIds.Markers, so a renamed id
        // would fail open. GlyphAttribution.InkPixels is the guard.
        if (labels > 0)
        {
            await Assert.That(ink.InkPixels).IsGreaterThan(0L);
        }

        await Assert.That(ink.OverCeilingOutsideInk).IsEqualTo(0L);
        await Assert.That(ink.WorstOutsideInk)
            .IsLessThanOrEqualTo(GoldenTolerance.DefaultPerceptual.MaxChannelDelta);

        // The unrelaxed policy over the glyph-patched frame re-imposes every limit ForLabelledFrame
        // loosens.
        GoldenComparison strict = GoldenImageComparer.Compare(goldenPng, ink.GlyphPatchedPng,
            GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[attribution] {name} glyph-patched, unrelaxed: {strict.Summary}");
        await Assert.That(strict.FailureReason).IsNull();
    }

    /// <summary>
    ///     The set this suite attributes is the set the gate relaxes, asserted rather than assumed.
    ///     A corpus entry <c>golden verify</c> judges without appearing here is a frame whose glyph
    ///     allowance nothing accounts for.
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
}
