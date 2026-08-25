#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Benchmarking;

/// <summary>What to measure.</summary>
/// <param name="Frames">Measured frames, after the warmup.</param>
/// <param name="Size">Output size in pixels.</param>
/// <param name="WarmupFrames">Frames run before measurement starts, to settle JIT and caches.</param>
/// <param name="LayerIds">Layers to enable; null means every registered layer.</param>
/// <param name="MeasureAllocations">Whether to sample the thread's allocation counter.</param>
/// <param name="Speed">Playback speed; scales the injected <c>DeltaSeconds</c>.</param>
public sealed record BenchmarkRequest(
    int Frames,
    SKSizeI Size,
    int WarmupFrames = 64,
    IReadOnlySet<string>? LayerIds = null,
    bool MeasureAllocations = true,
    double Speed = 1.0);

/// <summary>
///     A distribution of frame times, in milliseconds. <b>p99, not max</b>: on a shared runner the
///     maximum is whatever else the machine was doing, and a gate that fires on that gets muted within
///     a week (plan risk R11).
/// </summary>
/// <param name="P50Ms">Median.</param>
/// <param name="P95Ms">95th percentile.</param>
/// <param name="P99Ms">99th percentile — what the budget gates on.</param>
/// <param name="MaxMs">Slowest single frame. Reported, never gated.</param>
/// <param name="MeanMs">Arithmetic mean.</param>
public readonly record struct FrameTimeStats(
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    double MeanMs)
{
    /// <summary>Computes the distribution from a sample buffer. Sorts in place.</summary>
    /// <param name="samplesMs">Frame times in milliseconds; reordered by this call.</param>
    public static FrameTimeStats From(Span<double> samplesMs)
    {
        if (samplesMs.Length == 0)
        {
            return default;
        }

        double total = 0;
        for (int i = 0; i < samplesMs.Length; i++)
        {
            total += samplesMs[i];
        }

        samplesMs.Sort();
        return new FrameTimeStats(
            Percentile(samplesMs, 0.50),
            Percentile(samplesMs, 0.95),
            Percentile(samplesMs, 0.99),
            samplesMs[^1],
            total / samplesMs.Length);
    }

    private static double Percentile(ReadOnlySpan<double> sorted, double fraction)
    {
        int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>One benchmark run's result.</summary>
/// <param name="Id">Run identity, e.g. the fixture name.</param>
/// <param name="Frames">Measured frames.</param>
/// <param name="Size">Output size.</param>
/// <param name="Backend">Which surface backend produced it.</param>
/// <param name="Advance">UI-thread advance times.</param>
/// <param name="Render">Draw times.</param>
/// <param name="Total">Advance + render per frame.</param>
/// <param name="AllocatedBytesPerFrame">Steady-state managed allocation per frame. The budget is 0.</param>
/// <param name="AllocatedBytesTotal">Total managed allocation across the measured frames.</param>
/// <param name="RunUtc">When the run finished.</param>
public sealed record BenchmarkReport(
    string Id,
    int Frames,
    SKSizeI Size,
    RenderBackend Backend,
    FrameTimeStats Advance,
    FrameTimeStats Render,
    FrameTimeStats Total,
    long AllocatedBytesPerFrame,
    long AllocatedBytesTotal,
    DateTimeOffset RunUtc)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Serializes the report.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Dto(this), _json);

    /// <summary>
    ///     Writes the report to <c>&lt;directory&gt;/dv2d-&lt;Id&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c>,
    ///     matching the repo's existing <c>bench-reports/</c> convention.
    /// </summary>
    /// <param name="directory">Destination directory; created when absent.</param>
    /// <returns>The path written.</returns>
    public string WriteToBenchReports(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);

        string stamp = RunUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"dv2d-{Id}-{stamp}.json");
        File.WriteAllText(path, ToJson());
        return path;
    }

    // A flat DTO rather than serializing the record directly: SKSizeI has no parameterless constructor
    // and would round-trip as an empty object.
    private sealed record Dto
    {
        public Dto(BenchmarkReport r)
        {
            Id = r.Id;
            Frames = r.Frames;
            Width = r.Size.Width;
            Height = r.Size.Height;
            Backend = r.Backend.ToString();
            Advance = r.Advance;
            Render = r.Render;
            Total = r.Total;
            AllocatedBytesPerFrame = r.AllocatedBytesPerFrame;
            AllocatedBytesTotal = r.AllocatedBytesTotal;
            RunUtc = r.RunUtc;
        }

        public string Id { get; }
        public int Frames { get; }
        public int Width { get; }
        public int Height { get; }
        public string Backend { get; }
        public FrameTimeStats Advance { get; }
        public FrameTimeStats Render { get; }
        public FrameTimeStats Total { get; }
        public long AllocatedBytesPerFrame { get; }
        public long AllocatedBytesTotal { get; }
        public DateTimeOffset RunUtc { get; }
    }
}

/// <summary>
///     The frame budget from design §6: a 64 fps floor at 1× means 15.6 ms per frame, split ≤2 ms of
///     advance and ≤8 ms of draw at 1080p, with zero steady-state allocation.
/// </summary>
/// <param name="AdvanceP99Ms">Advance p99 ceiling.</param>
/// <param name="RenderP99Ms">Render p99 ceiling.</param>
/// <param name="AllocatedBytesPerFrame">Allocation ceiling. Zero, and never scaled.</param>
public sealed record BudgetPolicy(double AdvanceP99Ms, double RenderP99Ms, long AllocatedBytesPerFrame)
{
    /// <summary>Environment variable scaling the time budgets. Default 2.0 in CI.</summary>
    public const string ScaleEnvironmentVariable = "DV2D_BUDGET_SCALE";

    /// <summary>The design §6 numbers, which a local run reports against.</summary>
    public static readonly BudgetPolicy Baseline = new(2.0, 8.0, 0);

    /// <summary>
    ///     Baseline with the time budgets scaled by <see cref="ScaleEnvironmentVariable" /> (default
    ///     2.0). A GitHub hosted runner is not the design's mid-tier laptop, and a gate that fires on
    ///     runner noise gets disabled within a week — so it is deliberately loose enough to catch only
    ///     real regressions (an O(n) blow-up, a re-introduced per-frame allocation).
    ///     <para>
    ///         <b>The allocation ceiling is not scaled.</b> Zero is zero on every machine.
    ///     </para>
    /// </summary>
    public static BudgetPolicy Ci
    {
        get
        {
            double scale = 2.0;
            string? raw = Environment.GetEnvironmentVariable(ScaleEnvironmentVariable);
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
                parsed > 0)
            {
                scale = parsed;
            }

            return new BudgetPolicy(Baseline.AdvanceP99Ms * scale, Baseline.RenderP99Ms * scale,
                Baseline.AllocatedBytesPerFrame);
        }
    }

    /// <summary>Every budget this report breaks, as human-readable lines. Empty means green.</summary>
    /// <param name="report">The run to check.</param>
    public IReadOnlyList<string> Violations(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<string> violations = [];
        if (report.Advance.P99Ms > AdvanceP99Ms)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture,
                $"advance p99 {report.Advance.P99Ms:F3} ms > {AdvanceP99Ms:F3} ms"));
        }

        if (report.Render.P99Ms > RenderP99Ms)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture,
                $"render p99 {report.Render.P99Ms:F3} ms > {RenderP99Ms:F3} ms"));
        }

        if (report.AllocatedBytesPerFrame > AllocatedBytesPerFrame)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture,
                $"allocation {report.AllocatedBytesPerFrame} B/frame > {AllocatedBytesPerFrame} B/frame"));
        }

        return violations;
    }
}
