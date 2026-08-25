#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
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

                using SceneRenderPlan plan = SceneRenderPlan.Build(args, entry.Size, entry.MapName,
                    entry.Layers, allowSizeOverride: false);
                backend ??= plan.Backend;

                // A re-baked radar silently changes every pixel under it. Refuse rather than diff.
                string? bundleVersion = plan.MapAssets?.MapVersion;
                if (entry.MapVersion is { Length: > 0 } expected && bundleVersion is { Length: > 0 } actualVersion &&
                    !string.Equals(expected, actualVersion, StringComparison.OrdinalIgnoreCase))
                {
                    mismatched++;
                    results.Add(Result(entry, "stale-assets", null,
                        $"map_version {expected} in the manifest, {actualVersion} in {plan.Assets.Path}"));
                    continue;
                }

                SceneFixture fixture = SceneFixture.Load(entry.ScenePath);
                Scene2DFrame frame = plan.WithRadarArt(fixture.Frame);
                SceneTime time = fixture.Time;
                plan.Renderer.Camera = CameraSpec.Resolve(null, frame, entry.Size, fixture.Camera);
                byte[] actual = plan.Renderer.RenderPng(frame, in time, entry.Size, RenderPurpose.Export);

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
                GoldenTolerance tolerance = (toleranceOverride ?? entry.Tolerance) == GoldenMode.ByteExact
                    ? GoldenTolerance.ByteExact
                    : GoldenTolerance.DefaultPerceptual;
                GoldenComparison comparison = GoldenImageComparer.Compare(expectedPng, actual, tolerance);

                JsonObject row = Result(entry, comparison.Match ? "match" : "mismatch", goldenPath,
                    comparison.FailureReason);
                row["mismatched_fraction"] = comparison.MismatchedFraction;
                row["max_channel_delta"] = comparison.MaxChannelDelta;
                row["ssim"] = comparison.Ssim;
                row["tolerance"] = tolerance.Mode == GoldenMode.ByteExact ? "byte-exact" : "perceptual";

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

                    ConsoleOut.Info($"MISMATCH {entry.Name}: {comparison.FailureReason}");
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
