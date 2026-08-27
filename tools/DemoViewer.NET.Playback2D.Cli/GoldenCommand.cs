#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d golden verify | update</c> — the CI pixel gate over
///     <c>tests/fixtures/playback2d/manifest.json</c>.
///     <para>
///         A mismatch exits <see cref="ExitCode.GateFailure" /> and writes the actual and diff PNGs, so a
///         CI job's artifact upload carries the evidence. <c>update</c> rewrites the images and prints a
///         summary meant to be eyeballed in the PR diff — a golden that is silently rewritten is a test
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

        try
        {
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

                using SceneRenderPlan plan = PlanFor(args, corpus, entry);
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
                byte[] actual = RenderEntry(plan, entry, fixture);

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
                int labels = LabelCount(fixture);
                GoldenTolerance tolerance = ToleranceFor(entry, labels, toleranceOverride);
                GoldenComparison comparison = GoldenImageComparer.Compare(expectedPng, actual, tolerance);

                JsonObject row = Result(entry, comparison.Match ? "match" : "mismatch", goldenPath,
                    comparison.FailureReason);
                row["mismatched_fraction"] = comparison.MismatchedFraction;
                row["max_channel_delta"] = comparison.MaxChannelDelta;
                row["ssim"] = comparison.Ssim;
                row["tolerance"] = tolerance.Mode == GoldenMode.ByteExact ? "byte-exact" : "perceptual";

                // Additive on schema_version 1, and not decoration. The glyph tier is spent in
                // `above_ceiling_fraction` and bounded by `min_window_ssim`, and neither was in this
                // payload while this gate was the one going red in CI — the artifact upload carried the
                // pixels but the log could not say which rule they broke, or how close the rest came.
                // `labels` and `glyph_budget` are printed together because a budget nobody can see the
                // denominator of is a budget nobody can check.
                row["above_ceiling_fraction"] = comparison.AboveCeilingFraction;
                row["min_window_ssim"] = comparison.MinWindowSsim;
                row["labels"] = labels;
                row["glyph_budget"] = tolerance.MaxGlyphOutlierFraction;

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
        }
        finally
        {
            // The plans own their providers and are disposed per entry; nothing else to release here.
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
                ["backend"] = (backend?.Backend ?? Core.Rendering.RenderBackend.CpuRaster).ToString(),
                ["corpus"] = corpus.Directory,
                ["tolerance"] = new JsonObject
                {
                    ["mode"] = toleranceOverride is null
                        ? "per-entry"
                        : toleranceOverride == GoldenMode.ByteExact ? "byte-exact" : "perceptual"
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
    ///     The layer stack, backend and map art one corpus entry renders through. The plan is the
    ///     caller's to dispose.
    ///     <para>
    ///         <c>defaultBackend: ForceCpu</c>. The committed corpus is <c>goldens/cpu/</c> and CPU is
    ///         authoritative, so an unqualified <c>dv2d golden verify</c> must not auto-probe onto a GPU
    ///         and report a rasteriser difference as a pixel regression. <c>--gpu</c> / <c>--backend</c> /
    ///         <c>DV2D_RENDER_BACKEND</c> still override, for the parity lane.
    ///     </para>
    ///     <para>
    ///         Extracted from <see cref="Run" /> rather than inlined because
    ///         <c>GoldenAttributionTests</c> has to render these entries through this exact plan, with
    ///         one layer silenced, to prove what the glyph tier forgives. A proof that renders a
    ///         lookalike stack proves nothing about the stack the gate judges.
    ///     </para>
    /// </summary>
    /// <param name="args">The parsed arguments, for the backend / assets / layer flags.</param>
    /// <param name="corpus">The corpus, for the annotation sidecar convention.</param>
    /// <param name="entry">The entry to plan for.</param>
    internal static SceneRenderPlan PlanFor(CliArgs args, GoldenCorpus corpus, GoldenCorpusEntry entry)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(entry);

        return SceneRenderPlan.Build(args, entry.Size, entry.MapName, entry.Layers,
            allowSizeOverride: false, defaultBackend: RenderBackendPreference.ForceCpu,
            annotations: FixtureInk.ForCorpusEntry(corpus.Directory, entry.Name));
    }

    /// <summary>Renders one entry through a plan <see cref="PlanFor" /> built.</summary>
    /// <param name="plan">The plan. Its camera is overwritten.</param>
    /// <param name="entry">The entry, for the size a golden is named for.</param>
    /// <param name="fixture">The loaded scene.</param>
    internal static byte[] RenderEntry(SceneRenderPlan plan, GoldenCorpusEntry entry,
        SceneFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(fixture);

        Scene2DFrame frame = plan.WithRadarArt(fixture.Frame);
        SceneTime time = fixture.Time;
        plan.Renderer.Camera = CameraSpec.Resolve(null, frame, entry.Size, fixture.Camera);
        return plan.Renderer.RenderPng(frame, in time, entry.Size, RenderPurpose.Export);
    }

    /// <summary>
    ///     How many text labels the frame draws, which is what the glyph budget is denominated in.
    ///     <para>
    ///         <b>Read off the scene, never off the manifest.</b> A count a maintainer can edit is a
    ///         budget a maintainer can inflate, and <c>manifest.json</c> is a hand-edited file; this
    ///         number cannot be raised without adding a labelled player to the capture, which changes
    ///         the golden and therefore gets reviewed. Same definition and same source as
    ///         <c>SceneGoldenTests.LabelCount</c>, which is what lets the two owners of these PNGs agree
    ///         on the budget as well as on the pixels.
    ///     </para>
    ///     <para>
    ///         Marker labels only. The floor caption <c>FloorLabelLayer</c> draws is glyph ink too, and
    ///         a long string, some 400-500 px of it per pane against ~57 px for a two-letter initial — so
    ///         on the two stacked entries it spends a budget it earns nothing towards. That is
    ///         deliberate: it makes the budget tighter where there is more text, never looser, and both
    ///         entries still measure comfortably inside it (1.20 and 3.20 px per marker label against
    ///         the 6 allowed). Counting captions would be the change that needs justifying.
    ///     </para>
    /// </summary>
    /// <param name="fixture">The scene about to be drawn.</param>
    internal static int LabelCount(SceneFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return fixture.Frame.Markers.Count(static m => !string.IsNullOrEmpty(m.Label));
    }

    /// <summary>
    ///     The budget one entry is judged at: <see cref="GoldenTolerance.ByteExact" /> when the entry or
    ///     the caller asks for it, and otherwise <see cref="GoldenTolerance.ForLabelledFrame" />.
    ///     <para>
    ///         Not <see cref="GoldenTolerance.DefaultPerceptual" />: eight of the nine entries carry
    ///         labelled markers, and Skia's glyph rasteriser is not the same code on every operating
    ///         system, so ubuntu failed the whole clean corpus on text alone — every one of those eight
    ///         on <c>max channel delta</c>, the first rule, at 45 to 94 against a 32 ceiling. The
    ///         reasoning and the measurements are on <see cref="GoldenTolerance.ForLabelledFrame" />;
    ///         <c>GoldenAttributionTests</c> proves the allowance is spent on glyph ink and nothing else.
    ///     </para>
    ///     <para>
    ///         <c>--tolerance</c> overrides the mode the manifest states, not the budget that mode
    ///         resolves to: <c>byte-exact</c> is still every channel of every pixel, and
    ///         <c>perceptual</c> still means whatever a manifest entry saying "perceptual" means.
    ///     </para>
    /// </summary>
    /// <param name="entry">The entry, for its size and its declared mode.</param>
    /// <param name="labels">The count from <see cref="LabelCount" />.</param>
    /// <param name="toleranceOverride">The parsed <c>--tolerance</c>, or null.</param>
    internal static GoldenTolerance ToleranceFor(GoldenCorpusEntry entry, int labels,
        GoldenMode? toleranceOverride)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return (toleranceOverride ?? entry.Tolerance) == GoldenMode.ByteExact
            ? GoldenTolerance.ByteExact
            : GoldenTolerance.ForLabelledFrame(entry.Size.Width, entry.Size.Height, labels);
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
