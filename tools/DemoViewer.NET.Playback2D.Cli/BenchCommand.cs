#region

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d bench</c> — frame-time and allocation numbers CI can gate on (design §6).
///     <para>
///         <b>The clock lives here, not in Core.</b> Core is banned from wall-clock APIs (§5.1) precisely
///         so that motion is a function of the injected <c>SceneTime</c>; the harness therefore measures
///         from outside, timing <c>Advance</c> and <c>Render</c> separately with
///         <see cref="Stopwatch.GetTimestamp" />.
///     </para>
///     <para>
///         <b>Seam for B1.</b> The measurement loop below is what B1's
///         <c>Pipeline.Benchmarking.ScenePipelineBenchmark</c> absorbs; when it lands, this command keeps
///         its JSON shape and its gate and calls that instead of <see cref="Measure" />. There must be one
///         harness, not two — see the deviations note in the C1 plan.
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

        using SceneRenderPlan plan = SceneRenderPlan.Build(args, entry?.Size ?? source.DefaultSize,
            source.MapName, entry?.Layers);

        int frames = args.Int("frames", 2000);
        int warmup = args.Int("warmup", 128);
        bool gate = args.Flag("gate");
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

        plan.Renderer.Camera = CameraSpec.Resolve(null, plan.WithRadarArt(source.FrameAt(0)), plan.Size,
            source.Camera);

        BenchResult result = Measure(plan, source, frames, warmup);

        List<string> violations = [];
        if (gate)
        {
            if (result.Render.P99 > budget.RenderP99Ms)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"render_p99_ms {result.Render.P99:F3} > {budget.RenderP99Ms:F3}"));
            }

            if (result.Advance.P99 > budget.AdvanceP99Ms)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"advance_p99_ms {result.Advance.P99:F3} > {budget.AdvanceP99Ms:F3}"));
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
                $"  advance ms p50={result.Advance.P50:F3} p95={result.Advance.P95:F3} " +
                $"p99={result.Advance.P99:F3} max={result.Advance.Max:F3}"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  render  ms p50={result.Render.P50:F3} p95={result.Render.P95:F3} " +
                $"p99={result.Render.P99:F3} max={result.Render.Max:F3}"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  frame   ms p50={result.Frame.P50:F3} p95={result.Frame.P95:F3} " +
                $"p99={result.Frame.P99:F3} mean={result.Frame.Mean:F3}"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  alloc   {result.AllocatedBytesPerFrame} bytes/frame  " +
                $"gc {result.Gen0}/{result.Gen1}/{result.Gen2}"));
        }

        foreach (string violation in violations)
        {
            ConsoleOut.Error("budget violation: " + violation);
        }

        return passed ? ExitCode.Success : ExitCode.GateFailure;
    }

    /// <summary>
    ///     The measurement loop. Warm up, then time the two phases across a fixed window with no
    ///     allocation inside it — the sample arrays are pre-sized, and the percentile sort happens after
    ///     the window closes, so the loop's own bookkeeping cannot pollute the bytes/frame figure.
    /// </summary>
    /// <param name="plan">The resolved render plan.</param>
    /// <param name="source">The frame source.</param>
    /// <param name="frames">Measured frames.</param>
    /// <param name="warmup">Frames to run and discard first.</param>
    public static BenchResult Measure(SceneRenderPlan plan, SceneProvider source, int frames, int warmup)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);

        double[] advanceMs = new double[frames];
        double[] renderMs = new double[frames];

        using SKSurface surface = plan.Backend.Provider.CreateSurface(plan.Size);

        for (int i = 0; i < warmup; i++)
        {
            Scene2DFrame frame = plan.WithRadarArt(source.FrameAt(i % Math.Max(1, source.Count)));
            SceneTime time = source.TimeAt(i % Math.Max(1, source.Count));
            plan.Renderer.Advance(in time, frame);
            plan.Renderer.Render(surface, frame, RenderPurpose.Export);
        }

        // Settle the heap so the delta below measures the loop, not the warm-up's garbage.
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        int count = Math.Max(1, source.Count);
        for (int i = 0; i < frames; i++)
        {
            int index = i % count;
            Scene2DFrame frame = plan.WithRadarArt(source.FrameAt(index));
            SceneTime time = source.TimeAt(index);

            long t0 = Stopwatch.GetTimestamp();
            plan.Renderer.Advance(in time, frame);
            long t1 = Stopwatch.GetTimestamp();
            plan.Renderer.Render(surface, frame, RenderPurpose.Export);
            long t2 = Stopwatch.GetTimestamp();

            advanceMs[i] = Ms(t1 - t0);
            renderMs[i] = Ms(t2 - t1);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        double[] frameMs = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            frameMs[i] = advanceMs[i] + renderMs[i];
        }

        return new BenchResult(
            FrameTimeStats.From(advanceMs),
            FrameTimeStats.From(renderMs),
            FrameTimeStats.From(frameMs),
            allocated / frames,
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2);
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

    private static JsonObject BuildPayload(SceneRenderPlan plan, SceneProvider source,
        GoldenCorpusEntry? entry, BenchResult result, int frames, int warmup, double scale,
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
            ["advance_ms"] = result.Advance.ToJson(),
            ["render_ms"] = result.Render.ToJson(),
            ["frame_ms"] = result.Frame.ToJson(),
            ["allocated_bytes_per_frame"] = result.AllocatedBytesPerFrame,
            ["gc"] = new JsonObject
            {
                ["gen0"] = result.Gen0,
                ["gen1"] = result.Gen1,
                ["gen2"] = result.Gen2
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

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>Percentiles over one phase's samples.</summary>
/// <param name="P50">Median, in milliseconds.</param>
/// <param name="P95">95th percentile.</param>
/// <param name="P99">99th percentile — the figure CI gates on.</param>
/// <param name="Max">The worst sample.</param>
/// <param name="Mean">The arithmetic mean.</param>
internal readonly record struct FrameTimeStats(double P50, double P95, double P99, double Max, double Mean)
{
    /// <summary>Computes the percentiles. Sorts a copy, so the caller's sample order survives.</summary>
    /// <param name="samples">The per-frame samples in milliseconds.</param>
    public static FrameTimeStats From(double[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0)
        {
            return default;
        }

        double sum = 0;
        foreach (double sample in samples)
        {
            sum += sample;
        }

        double[] sorted = [.. samples];
        Array.Sort(sorted);

        return new FrameTimeStats(
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1],
            sum / samples.Length);
    }

    /// <summary>The JSON block for this phase.</summary>
    public JsonObject ToJson() => new()
    {
        ["p50"] = Round(P50),
        ["p95"] = Round(P95),
        ["p99"] = Round(P99),
        ["max"] = Round(Max),
        ["mean"] = Round(Mean)
    };

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    // Nearest-rank on a sorted array: the smallest sample at or above the requested rank. No
    // interpolation, so a p99 is always a real observed frame time.
    private static double Percentile(double[] sorted, double q)
    {
        int rank = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>One bench run's numbers.</summary>
/// <param name="Advance">Advance-phase percentiles.</param>
/// <param name="Render">Render-phase percentiles.</param>
/// <param name="Frame">Combined per-frame percentiles.</param>
/// <param name="AllocatedBytesPerFrame">Steady-state allocation, the §6 zero-allocation contract.</param>
/// <param name="Gen0">Gen-0 collections during the measured window.</param>
/// <param name="Gen1">Gen-1 collections.</param>
/// <param name="Gen2">Gen-2 collections.</param>
internal readonly record struct BenchResult(
    FrameTimeStats Advance,
    FrameTimeStats Render,
    FrameTimeStats Frame,
    long AllocatedBytesPerFrame,
    int Gen0,
    int Gen1,
    int Gen2);
