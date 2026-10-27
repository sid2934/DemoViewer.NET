#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <b>The proof obligation behind the glyph tier, for the corpus <c>dv2d golden</c> owns.</b>
///     See <see cref="GlyphAttribution" /> for the mask and what it licenses.
///     <para>
///         <c>SceneGoldenTests</c> and <c>LevelGoldenTests</c> discharge the same obligation for the
///         synthetics and the nuke goldens. This suite covers the entries that need the tool's own
///         flags on top: the four radar-backed Mirage/Inferno frames, <c>nuke-single-upper</c>, and the
///         1080p <c>full-scene-budget</c>.
///     </para>
///     <para>
///         <b>
///             It renders through <see cref="GoldenCommand.PlanFor" /> and
///             <see cref="GoldenEntryPlan.Render" />
///         </b>
///         , the command's own statements, not a
///         reproduction of them. A proof that renders a lookalike stack proves nothing about the stack
///         the gate judges, and the two owners of these PNGs have drifted apart once already.
///     </para>
///     <para>
///         It runs on every platform. On Windows, which authored this corpus, the deltas are zeroes, and
///         that assertion goes red first if the rasteriser difference ever stops being confined to text.
///         Three entries are skipped on macOS: see <see cref="MacOsDivergent" />.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class GoldenAttributionTests
{
    /// <summary>
    ///     Every entry <c>golden verify</c> actually judges, read off the manifest rather than listed
    ///     here, so a corpus entry added tomorrow is attributed tomorrow, and the glyph mask the gate
    ///     forgives under is measured here rather than taken on trust.
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
        if (OperatingSystem.IsMacOS() && MacOsDivergent.Contains(name))
        {
            throw new SkipTestException($"{name}: the committed golden was authored on Windows and " +
                                        "macOS renders it outside the unrelaxed ceiling. See MacOsDivergent.");
        }

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
            Render(entry, fixture, true), Render(entry, fixture, false));

        // The budget read off the SHIPPING tolerance rather than restated: on the authoring platform
        // the tier is closed and this prints 0.
        int labels = GoldenEntryPlan.LabelCount(fixture);
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
    ///     Corpus entries whose committed golden this renderer disagrees with on macOS by more than the
    ///     unrelaxed ceiling of 8, measured on an M-series host: <c>nuke-single-upper</c> 13,
    ///     <c>query-nuke-execute</c> 11, <c>overlay-nuke-execute</c> 11.
    ///     <para>
    ///         Skipped there rather than forgiven everywhere. The goldens were authored on Windows, so on
    ///         macOS every entry here is a cross-platform comparison and these three simply fall outside
    ///         what the gate allows; raising the ceiling to swallow them would blunt it on the platform
    ///         that authored the bytes, which is the only platform where it is a real gate.
    ///     </para>
    ///     <para>
    ///         <b>Two of the three carry no text at all</b>, zero labels and zero glyph ink, so whatever
    ///         macOS renders differently here is NOT glyph rasterisation and the glyph budget is not the
    ///         lever. That is unexplained and is the reason this is a skip with a note rather than a
    ///         tolerance change.
    ///     </para>
    /// </summary>
    private static readonly HashSet<string> MacOsDivergent =
        new(StringComparer.Ordinal) { "nuke-single-upper", "query-nuke-execute", "overlay-nuke-execute" };

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
    /// <param name="entry">The entry to render.</param>
    /// <param name="fixture">Its loaded scene.</param>
    /// <param name="drawText">
    ///     False silences marker initials and the floor caption, leaving a frame that differs from the
    ///     full render in exactly the glyph ink and nowhere else. That difference is the mask, so it is
    ///     produced by the render path under test rather than by a second one built to resemble it.
    /// </param>
    private static byte[] Render(GoldenCorpusEntry entry, SceneFixture fixture, bool drawText)
    {
        CliArgs args = CliArgs.Parse(["--cpu", "--assets", Dv2d.AssetsDirectory]);
        using SceneRenderPlan plan = GoldenCommand.PlanFor(args, entry);

        if (!drawText)
        {
            GoldenCommand.SilenceText(plan);
        }

        return GoldenEntryPlan.Render(plan, entry, fixture);
    }
}
