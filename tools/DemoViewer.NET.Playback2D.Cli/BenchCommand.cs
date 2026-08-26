#region

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Benchmarking;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d bench</c> — frame-time and allocation numbers CI can gate on (design §6).
///     <para>
///         <b>The clock lives in Pipeline, not Core.</b> Core is banned from wall-clock APIs (§5.1)
///         precisely so that motion is a function of the injected <c>SceneTime</c>; the harness therefore
///         measures from outside.
///     </para>
///     <para>
///         <b>Seam closed at the C1 merge.</b> The measurement loop that shipped here has been absorbed
///         into B1's <see cref="ScenePipelineBenchmark" /> (C1 deviation 7's stated merge action). This
///         command keeps what the plan said it keeps — the JSON shape, the budget resolution and the
///         gate — and owns no timing loop of its own. There is one harness, not two.
///     </para>
/// </summary>
internal static class BenchCommand
{
    /// <summary>The environment variable that relaxes the time budgets on a shared runner.</summary>
    public const string BudgetScaleVariable = "DV2D_BUDGET_SCALE";

    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    public static ExitCode Run(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        GoldenCorpusEntry? entry = ResolveCorpusEntry(args, out string? fixturePath);

        using SceneProvider source = fixturePath is not null
            ? FixtureSceneProvider.Load(fixturePath)
            : SceneProvider.Build(args);

        // Same corpus-sidecar convention as `golden`: a bench must time the stack the golden pins,
        // ink included, or the §6 numbers describe a scene nobody renders.
        using SceneRenderPlan plan = SceneRenderPlan.Build(args, entry?.Size ?? source.DefaultSize,
            source.MapName, entry?.Layers,
            annotations: entry is null ? null : FixtureInk.ForCorpusEntry(entry.CorpusDirectory, entry.Name));

        int frames = args.Int("frames", 2000);
        int warmup = args.Int("warmup", 128);
        bool gate = args.Flag("gate");
        bool perf = PerfOutput.Requested(args);
        string? reportDir = args.String("report-dir");

        if (frames <= 0)
        {
            throw new CliUsageException("--frames must be positive.");
        }

        if (warmup < 0)
        {
            throw new CliUsageException("--warmup cannot be negative.");
        }

        double scale = args.Double("budget-scale", DefaultBudgetScale());
        GoldenBudget budget = (entry?.Budget ?? GoldenBudget.Default).Scaled(scale);
        budget = new GoldenBudget(
            args.Double("budget-p99-ms", budget.RenderP99Ms),
            args.Double("budget-advance-p99-ms", budget.AdvanceP99Ms),
            (long)args.Double("budget-bytes-per-frame", budget.BytesPerFrame));

        args.ThrowIfUnconsumed();

        ViewportTransform camera = CameraSpec.Resolve(null, plan.WithRadarArt(source.FrameAt(0)),
            plan.Size, source.Camera);

        ScenePipelineBenchmark benchmark = new(plan.Compositor, plan.Backend.Provider,
            new StackedLayout(), ScenePalette.Dark)
        {
            Id = entry?.Name ?? source.Name,
            AuthoritativeFloors = plan.AuthoritativeFloors,
            RadarBinder = plan.RadarBinder,
            Camera = camera,

            // Sized to the measured window so the rings hold the whole run rather than its tail; the
            // harness attaches it before the warmup, which is what allocates them outside the §6
            // bytes/frame window.
            Perf = perf ? new ScenePerfRecorder(Math.Max(1, frames)) : null
        };

        BenchmarkReport result = benchmark.Run(new PlanFrameSource(plan, source),
            new BenchmarkRequest(frames, plan.Size, warmup));

        PerfReport? perfReport = benchmark.Perf?.Snapshot();

        List<string> violations = [];
        if (gate)
        {
            if (result.Render.P99Ms > budget.RenderP99Ms)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"render_p99_ms {result.Render.P99Ms:F3} > {budget.RenderP99Ms:F3}"));
            }

            if (result.Advance.P99Ms > budget.AdvanceP99Ms)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"advance_p99_ms {result.Advance.P99Ms:F3} > {budget.AdvanceP99Ms:F3}"));
            }

            if (result.AllocatedBytesPerFrame > budget.BytesPerFrame)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"allocated_bytes_per_frame {result.AllocatedBytesPerFrame} > {budget.BytesPerFrame}"));
            }
        }

        bool passed = violations.Count == 0;
        JsonObject payload = BuildPayload(plan, source, entry, result, frames, warmup, scale, budget, gate,
            violations, passed);

        // Additive: one new key on the documented schema_version 1 shape, absent entirely without the
        // flag, so nothing that reads the payload today has to change.
        if (perfReport is not null)
        {
            payload["perf"] = PerfOutput.ToJson(perfReport);
        }

        if (reportDir is not null)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeName = string.Concat((entry?.Name ?? source.Name)
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
            string path = Path.Combine(reportDir, $"dv2d-{safeName}_{stamp}.json");
            RenderCommand.WriteFile(path,
                System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString(ConsoleOut.Pretty)));
            ConsoleOut.Info($"wrote {path}");
        }

        if (ConsoleOut.IsJson)
        {
            ConsoleOut.Json(payload);
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"{source.Kind} {entry?.Name ?? source.Name}  {plan.Size.Width}x{plan.Size.Height}  " +
                $"{plan.Backend.Backend}  {frames} frames (+{warmup} warmup)"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  advance ms p50={result.Advance.P50Ms:F3} p95={result.Advance.P95Ms:F3} " +
                $"p99={result.Advance.P99Ms:F3} max={result.Advance.MaxMs:F3}"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  render  ms p50={result.Render.P50Ms:F3} p95={result.Render.P95Ms:F3} " +
                $"p99={result.Render.P99Ms:F3} max={result.Render.MaxMs:F3}"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  frame   ms p50={result.Total.P50Ms:F3} p95={result.Total.P95Ms:F3} " +
                $"p99={result.Total.P99Ms:F3} mean={result.Total.MeanMs:F3}"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  alloc   {result.AllocatedBytesPerFrame} bytes/frame  " +
                $"gc {result.Gen0Collections}/{result.Gen1Collections}/{result.Gen2Collections}"));

            if (perfReport is not null)
            {
                PerfOutput.WriteHuman(perfReport);
            }
        }

        foreach (string violation in violations)
        {
            ConsoleOut.Error("budget violation: " + violation);
        }

        return passed ? ExitCode.Success : ExitCode.GateFailure;
    }

    /// <summary>The <c>DV2D_BUDGET_SCALE</c> value, or 1.0.</summary>
    public static double DefaultBudgetScale() =>
        double.TryParse(Environment.GetEnvironmentVariable(BudgetScaleVariable), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double scale) && scale > 0
            ? scale
            : 1.0;

    private static GoldenCorpusEntry? ResolveCorpusEntry(CliArgs args, out string? fixturePath)
    {
        fixturePath = null;
        string? name = args.String("name");
        if (name is null)
        {
            return null;
        }

        GoldenCorpus corpus = CorpusLocator.Load(args);
        GoldenCorpusEntry entry = corpus.Find(name)
                                  ?? throw new CliUsageException(
                                      $"--name {name} matches no entry in {corpus.Directory}.");
        if (!File.Exists(entry.ScenePath))
        {
            throw new FileNotFoundException($"corpus entry '{name}' has no scene at {entry.ScenePath}.",
                entry.ScenePath);
        }

        fixturePath = entry.ScenePath;
        return entry;
    }

    // The dv2d JSON shape is C1's and stays C1's (deviation 7): B1's FrameTimeStats is the source of
    // the numbers, this is the projection onto the documented snake_case block.
    private static JsonObject Percentiles(FrameTimeStats stats) => new()
    {
        ["p50"] = Round(stats.P50Ms),
        ["p95"] = Round(stats.P95Ms),
        ["p99"] = Round(stats.P99Ms),
        ["max"] = Round(stats.MaxMs),
        ["mean"] = Round(stats.MeanMs)
    };

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static JsonObject BuildPayload(SceneRenderPlan plan, SceneProvider source,
        GoldenCorpusEntry? entry, BenchmarkReport result, int frames, int warmup, double scale,
        GoldenBudget budget, bool gate, IReadOnlyList<string> violations, bool passed)
    {
        JsonArray violationArray = RenderCommand.ToArray(violations);

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["command"] = "bench",
            ["ok"] = passed,
            ["backend"] = plan.Backend.Backend.ToString(),
            ["source"] = new JsonObject
            {
                ["kind"] = source.Kind,
                ["name"] = entry?.Name ?? source.Name
            },
            ["frames"] = frames,
            ["warmup"] = warmup,
            ["size"] = new JsonObject
            {
                ["width"] = plan.Size.Width,
                ["height"] = plan.Size.Height
            },
            ["layers"] = RenderCommand.ToArray(plan.LayerIds),
            ["advance_ms"] = Percentiles(result.Advance),
            ["render_ms"] = Percentiles(result.Render),
            ["frame_ms"] = Percentiles(result.Total),
            ["allocated_bytes_per_frame"] = result.AllocatedBytesPerFrame,
            ["gc"] = new JsonObject
            {
                ["gen0"] = result.Gen0Collections,
                ["gen1"] = result.Gen1Collections,
                ["gen2"] = result.Gen2Collections
            },
            ["budget"] = new JsonObject
            {
                ["scale"] = scale,
                ["render_p99_ms"] = budget.RenderP99Ms,
                ["advance_p99_ms"] = budget.AdvanceP99Ms,
                ["bytes_per_frame"] = budget.BytesPerFrame
            },
            ["gate"] = new JsonObject
            {
                ["enabled"] = gate,
                ["passed"] = passed,
                ["violations"] = violationArray
            },
            ["metadata"] = Metadata()
        };
    }

    private static JsonObject Metadata() => new()
    {
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        ["git_commit"] = GitCommit(),
        ["machine"] = new JsonObject
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["cpu"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ??
                      RuntimeInformation.ProcessArchitecture.ToString(),
            ["logical_cores"] = Environment.ProcessorCount,
            ["ram_bytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ["dotnet_version"] = RuntimeInformation.FrameworkDescription
        }
    };

    // Read from .git rather than shelling out: a benchmark that spawns a process to label itself is a
    // benchmark that fails on a machine without git on PATH.
    private static string? GitCommit()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: >= 7 } sha)
        {
            return sha[..7];
        }

        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            for (int depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
            {
                string gitPath = Path.Combine(dir.FullName, ".git");
                string? head = File.Exists(gitPath)
                    ? ResolveWorktreeHead(gitPath)
                    : Directory.Exists(gitPath)
                        ? ReadHead(gitPath)
                        : null;
                if (head is { Length: >= 7 })
                {
                    return head[..7];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A label is a nicety; never fail a benchmark over one.
        }

        return null;
    }

    private static string? ResolveWorktreeHead(string gitFilePath)
    {
        string content = File.ReadAllText(gitFilePath).Trim();
        const string prefix = "gitdir:";
        return content.StartsWith(prefix, StringComparison.Ordinal)
            ? ReadHead(content[prefix.Length..].Trim())
            : null;
    }

    private static string? ReadHead(string gitDir)
    {
        string headPath = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        string head = File.ReadAllText(headPath).Trim();
        if (!head.StartsWith("ref:", StringComparison.Ordinal))
        {
            return head;
        }

        string reference = head[4..].Trim();
        string relative = reference.Replace('/', Path.DirectorySeparatorChar);

        // A linked worktree's git dir holds HEAD but not the ref it names — loose refs live in the
        // COMMON dir. Without this a bench report from a worktree is labelled "unknown commit".
        foreach (string dir in RefRoots(gitDir))
        {
            string refPath = Path.Combine(dir, relative);
            if (File.Exists(refPath))
            {
                return File.ReadAllText(refPath).Trim();
            }

            string packed = Path.Combine(dir, "packed-refs");
            if (!File.Exists(packed))
            {
                continue;
            }

            foreach (string line in File.ReadLines(packed))
            {
                if (line.Length > 41 && line.EndsWith(" " + reference, StringComparison.Ordinal))
                {
                    return line[..40];
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> RefRoots(string gitDir)
    {
        yield return gitDir;

        string commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
        {
            yield break;
        }

        string common = File.ReadAllText(commonDirFile).Trim();
        yield return Path.GetFullPath(Path.IsPathRooted(common) ? common : Path.Combine(gitDir, common));
    }
}

/// <summary>
///     Presents the CLI's <see cref="SceneProvider" /> as the <see cref="ISceneFrameSource" /> B1's
///     harness consumes, re-attaching the plan's decoded radar art on the way through.
///     <para>
///         The enrichment is memoised by <c>SceneRenderPlan.WithRadarArt</c>, so replaying one fixture
///         a few thousand times allocates nothing here — which matters, because this adapter sits
///         inside the window the §6 bytes/frame gate reads.
///     </para>
/// </summary>
internal sealed class PlanFrameSource : ISceneFrameSource
{
    private readonly SceneRenderPlan _plan;
    private readonly SceneProvider _source;

    /// <summary>Wraps a provider for a resolved plan.</summary>
    /// <param name="plan">The resolved render plan supplying the map art.</param>
    /// <param name="source">The frames to replay.</param>
    public PlanFrameSource(SceneRenderPlan plan, SceneProvider source)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);

        _plan = plan;
        _source = source;
    }

    /// <inheritdoc />
    public int FrameCount => Math.Max(1, _source.Count);

    /// <inheritdoc />
    public SceneTime TimeAt(int frameIndex) => _source.TimeAt(frameIndex);

    /// <inheritdoc />
    public Scene2DFrame FrameAt(int frameIndex) => _plan.WithRadarArt(_source.FrameAt(frameIndex));
}
