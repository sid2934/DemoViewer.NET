#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d golden verify | update</c>: the CI pixel gate over
///     <c>tests/fixtures/playback2d/manifest.json</c>.
///     <para>
///         A mismatch exits <see cref="ExitCode.GateFailure" /> and writes the actual and diff PNGs, so a
///         CI job's artifact upload carries the evidence. <c>update</c> rewrites the images and prints a
///         summary meant to be eyeballed in the PR diff. A golden that rewrites itself silently is a test
///         that no longer tests.
///     </para>
///     <para>
///         What "within tolerance" means here is <see cref="ToleranceFor" />, and it is not a constant:
///         eight of the nine entries this command judges draw text, and Skia's glyph rasteriser is not
///         the same code on every operating system. <c>GoldenAttributionTests</c> verifies the allowance
///         is spent on glyph ink and nothing else.
///     </para>
/// </summary>
internal static class GoldenCommand
{
    /// <summary>The default directory diffs are written to.</summary>
    public const string DefaultDiffDirectory = "artifacts/playback2d-goldens";

    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    public static ExitCode Run(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string action = args.SubVerb ?? throw new CliUsageException("golden takes verify | update.");
        bool update = action switch
        {
            "verify" => false,
            "update" => true,
            _ => throw new CliUsageException($"golden takes verify | update, got '{action}'.")
        };

        GoldenCorpus corpus = CorpusLocator.Load(args);
        string? only = args.String("name");
        string diffDir = args.String("diff-dir") ?? DefaultDiffDirectory;
        GoldenMode? toleranceOverride = ParseTolerance(args.String("tolerance"));

        int matched = 0;
        int mismatched = 0;
        int missing = 0;
        int skipped = 0;
        int updated = 0;
        JsonArray results = [];
        ResolvedBackend? backend = null;

        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            if (only is not null && !string.Equals(entry.Name, only, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Pending || !File.Exists(entry.ScenePath))
            {
                skipped++;
                results.Add(Result(entry, "skipped", null,
                    entry.Pending
                        ? "marked pending: its inputs have not all landed yet"
                        : "no scene file"));
                continue;
            }

            using SceneRenderPlan plan = PlanFor(args, entry);
            backend ??= plan.Backend;

            // A re-baked radar silently changes every pixel under it. Refuse rather than diff.
            string? bundleVersion = plan.MapAssets?.Bundle.MapVersion;
            if (entry.MapVersion is { Length: > 0 } expected && bundleVersion is { Length: > 0 } actualVersion &&
                !string.Equals(expected, actualVersion, StringComparison.OrdinalIgnoreCase))
            {
                mismatched++;
                results.Add(Result(entry, "stale-assets", null,
                    $"map_version {expected} in the manifest, {actualVersion} in {plan.Assets.Path}"));
                continue;
            }

            SceneFixture fixture = SceneFixture.Load(entry.ScenePath);
            byte[] actual = GoldenEntryPlan.Render(plan, entry, fixture);

            string goldenPath = entry.GoldenPath(plan.Backend.Backend);

            if (update)
            {
                RenderCommand.WriteFile(goldenPath, actual);
                updated++;
                results.Add(Result(entry, "updated", goldenPath, null));
                ConsoleOut.Info($"updated {goldenPath} ({actual.Length} bytes)");
                continue;
            }

            if (!File.Exists(goldenPath))
            {
                missing++;
                string actualPath = WriteArtifact(diffDir, entry.Name, ".actual.png", actual);
                results.Add(Result(entry, "missing", goldenPath,
                    $"no golden; the render was written to {actualPath}. Run 'dv2d golden update'."));
                continue;
            }

            byte[] expectedPng = File.ReadAllBytes(goldenPath);
            int labels = GoldenEntryPlan.LabelCount(fixture);
            GoldenTolerance tolerance = ToleranceFor(entry, labels, toleranceOverride);

            // Text is judged under its own ink and nowhere else. The glyph rasteriser differs per
            // operating system, so a second render with every text layer silenced marks the glyph
            // pixels; under them the golden stands in for the render, and the strict tolerance then
            // judges geometry alone. The attribution test prints what the ink itself measured.
            byte[] judged = actual;
            GlyphAttribution? ink = null;
            if (tolerance.Mode != GoldenMode.ByteExact)
            {
                // A fresh plan, not the used one: layers cache their dry pictures, so a switch flipped
                // after the first render would not reach the second.
                using SceneRenderPlan silencedPlan = PlanFor(args, entry);
                SilenceText(silencedPlan);
                byte[] silenced = GoldenEntryPlan.Render(silencedPlan, entry, fixture);
                ink = GlyphAttribution.Measure(expectedPng, actual, silenced);
                judged = ink.Value.GlyphPatchedPng;
            }

            GoldenComparison comparison = GoldenImageComparer.Compare(expectedPng, judged, tolerance);

            JsonObject row = Result(entry, comparison.Match ? "match" : "mismatch", goldenPath,
                comparison.FailureReason);
            row["mismatched_fraction"] = comparison.MismatchedFraction;
            row["max_channel_delta"] = comparison.MaxChannelDelta;
            row["ssim"] = comparison.Ssim;
            row["tolerance"] = tolerance.Mode == GoldenMode.ByteExact ? "byte-exact" : "perceptual";

            // Additive on schema_version 1: the geometry rules' margins, and what the glyph ink
            // measured, so a CI log says which rule broke and how close the rest came.
            row["above_ceiling_fraction"] = comparison.AboveCeilingFraction;
            row["min_window_ssim"] = comparison.MinWindowSsim;
            row["labels"] = labels;
            if (ink is { } measured)
            {
                row["ink_pixels"] = measured.InkPixels;
                row["worst_under_ink"] = measured.WorstUnderInk;
                row["under_ink_over_ceiling"] = measured.OverCeilingUnderInk;
            }

            if (comparison.Match)
            {
                matched++;
            }
            else
            {
                mismatched++;
                row["actual"] = WriteArtifact(diffDir, entry.Name, ".actual.png", actual);
                if (GoldenImageComparer.CreateDiffPng(expectedPng, actual) is { } diff)
                {
                    row["diff"] = WriteArtifact(diffDir, entry.Name, ".diff.png", diff);
                }

                // Summary, not just the reason: the reason names the one rule that broke, and the
                // next question is always how the other six did.
                ConsoleOut.Info($"MISMATCH {entry.Name} (labels={labels}): {comparison.Summary}");
            }

            results.Add(row);
        }

        if (only is not null && results.Count == 0)
        {
            throw new CliUsageException(
                $"--name {only} matches no corpus entry in {corpus.Directory}.");
        }

        args.ThrowIfUnconsumed();

        bool ok = mismatched == 0 && missing == 0;
        if (ConsoleOut.IsJson)
        {
            ConsoleOut.Json(new JsonObject
            {
                ["schema_version"] = 1,
                ["command"] = "golden",
                ["action"] = update ? "update" : "verify",
                ["ok"] = ok,
                ["backend"] = (backend?.Backend ?? RenderBackend.CpuRaster).ToString(),
                ["corpus"] = corpus.Directory,
                ["tolerance"] = new JsonObject
                {
                    ["mode"] = toleranceOverride is null
                        ? "per-entry"
                        : toleranceOverride == GoldenMode.ByteExact
                            ? "byte-exact"
                            : "perceptual"
                },
                ["counts"] = new JsonObject
                {
                    ["total"] = results.Count,
                    ["matched"] = matched,
                    ["mismatched"] = mismatched,
                    ["missing"] = missing,
                    ["skipped"] = skipped,
                    ["updated"] = updated
                },
                ["results"] = results
            });
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"{(update ? "update" : "verify")}: {results.Count} entries — matched {matched}, " +
                $"mismatched {mismatched}, missing {missing}, skipped {skipped}, updated {updated}"));
        }

        return ok ? ExitCode.Success : ExitCode.GateFailure;
    }

    /// <summary>
    ///     The layer stack, backend and map art one corpus entry renders through: the flags this
    ///     command accepts, over <see cref="GoldenEntryPlan.OptionsFor" />. The plan is the caller's to
    ///     dispose.
    ///     <para>
    ///         <c>--size</c> is deliberately left unconsumed, so naming it here is the usage error it
    ///         is: a golden is named for its size, and an override would compare one image against a
    ///         differently-named other.
    ///     </para>
    ///     <para>
    ///         Extracted from <see cref="Run" /> rather than inlined because
    ///         <c>GoldenAttributionTests</c> has to render these entries through this exact plan, with
    ///         one layer silenced, to prove what the glyph tier forgives. A proof that renders a
    ///         lookalike stack proves nothing about the stack the gate judges.
    ///     </para>
    /// </summary>
    /// <param name="args">The parsed arguments, for the backend / assets / layer flags.</param>
    /// <param name="entry">The entry to plan for.</param>
    internal static SceneRenderPlan PlanFor(CliArgs args, GoldenCorpusEntry entry)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(entry);

        CliScenePlan.RequireSingleLevelLayout(args);

        return CliScenePlan.Build(GoldenEntryPlan.OptionsFor(entry, args.List("layers"),
            args.List("exclude-layers"),
            BackendResolver.Preference(args, RenderBackendPreference.ForceCpu),
            AssetsRootResolver.Resolve(args), ConsoleOut.Info));
    }

    /// <summary>
    ///     Turns off every layer's text so a second render marks the glyph pixels: marker labels, the
    ///     floor caption, text annotations and zone names. Shapes, markers and outlines still draw.
    /// </summary>
    /// <param name="plan">The plan whose compositor is switched; the change is not undone.</param>
    internal static void SilenceText(SceneRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SceneLayerCatalog.SilenceText(plan.Compositor);
    }

    /// <summary>
    ///     The tolerance one entry is judged at: <see cref="GoldenTolerance.ByteExact" /> when the entry
    ///     or the caller asks for it, else <see cref="GoldenTolerance.DefaultPerceptual" /> over the
    ///     glyph-patched render (see <see cref="SilenceText" />), which is why no text budget is needed.
    ///     <c>--tolerance</c> overrides the mode the manifest states, not what a mode means.
    /// </summary>
    /// <param name="entry">The entry, for its declared mode.</param>
    /// <param name="labels">The count from <see cref="GoldenEntryPlan.LabelCount" />, reported in the result row.</param>
    /// <param name="toleranceOverride">The parsed <c>--tolerance</c>, or null.</param>
    internal static GoldenTolerance ToleranceFor(GoldenCorpusEntry entry, int labels,
        GoldenMode? toleranceOverride)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return (toleranceOverride ?? entry.Tolerance) == GoldenMode.ByteExact
            ? GoldenTolerance.ByteExact
            : GoldenTolerance.DefaultPerceptual;
    }

    private static GoldenMode? ParseTolerance(string? raw) => raw switch
    {
        null => null,
        "byte-exact" => GoldenMode.ByteExact,
        "perceptual" => GoldenMode.Perceptual,
        _ => throw new CliUsageException($"--tolerance expects byte-exact|perceptual, got '{raw}'.")
    };

    private static JsonObject Result(GoldenCorpusEntry entry, string status, string? goldenPath,
        string? reason)
    {
        JsonObject o = new()
        {
            ["name"] = entry.Name,
            ["status"] = status
        };

        if (goldenPath is not null)
        {
            o["golden"] = goldenPath;
        }

        if (reason is not null)
        {
            o["reason"] = reason;
        }

        return o;
    }

    private static string WriteArtifact(string diffDir, string name, string suffix, byte[] bytes)
    {
        string path = Path.Combine(diffDir, name + suffix);
        RenderCommand.WriteFile(path, bytes);
        return path;
    }
}

/// <summary>Resolves <c>--corpus</c> against the default walk-up. Shared by golden, bench and fixture.</summary>
internal static class CorpusLocator
{
    /// <summary>Loads the corpus the caller named, or the one beside the checkout.</summary>
    /// <param name="args">The parsed arguments. Consumes <c>--corpus</c>.</param>
    /// <exception cref="FileNotFoundException">Neither a flag nor a probe found a manifest.</exception>
    public static GoldenCorpus Load(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return GoldenCorpus.Load(Directory(args));
    }

    /// <summary>The corpus directory the caller named, or the one beside the checkout.</summary>
    /// <param name="args">The parsed arguments. Consumes <c>--corpus</c>.</param>
    /// <exception cref="FileNotFoundException">Neither a flag nor a probe found a manifest.</exception>
    public static string Directory(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? corpusDir = args.String("corpus") ?? GoldenCorpus.FindDefaultCorpusDirectory();
        return corpusDir ?? throw new FileNotFoundException(
            "no fixture corpus found. Pass --corpus <dir>, or run from inside a checkout " +
            "(tests/fixtures/playback2d/manifest.json).");
    }
}
