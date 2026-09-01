#region

using System.Text.Json.Nodes;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Pipeline.Benchmarking;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The <c>--perf</c> flag surface and the two shapes a <see cref="PerfReport" /> comes out in
///     (plan <c>P1-perf-instrumentation</c> §2, §4).
///     <para>
///         <b>The switch is the repo's existing one, extended.</b>
///         <see cref="Profiling.Enabled" /> is the single process-wide runtime gate for every profiling
///         accumulator in the stack (<c>docs/profiling.md</c>); <c>dv2d</c> had no flag surface of its
///         own. <c>--perf</c> attaches the scene recorder for one run, and the env switch (read through
///         <see cref="Profiling.Enabled" />, which resolves <c>CS2DEMOKIT_PROFILE</c> on first touch)
///         turns it on implicitly, alongside the parse and entity trees it already governs.
///     </para>
///     <para>
///         The implication runs one way only. <c>--perf</c> does <b>not</b> set
///         <see cref="Profiling.Enabled" />, because the tracker decode is one of the stages being timed
///         and switching on its own per-call instrumentation would perturb the very number the flag
///         exists to produce. A caller who wants both asks for both.
///     </para>
/// </summary>
internal static class PerfOutput
{
    /// <summary>
    ///     The spelling <c>docs/profiling.md</c> and <c>RuntimeEnvInfo</c> still carry. The switch itself
    ///     moved into the CS2DemoKit package, whose own variable is <c>CS2DEMOKIT_PROFILE</c> and is
    ///     resolved by <see cref="Profiling.Enabled" />; honouring both means neither spelling silently
    ///     does nothing.
    /// </summary>
    public const string LegacyEnvironmentVariable = "DEMOVIEWER_PROFILE";

    /// <summary>How many rows the "slowest operations" ranking prints.</summary>
    private const int RankedRows = 6;

    /// <summary>
    ///     Whether this run captures. Consumes <c>--perf</c> and its <c>--profile</c> alias so
    ///     <see cref="CliArgs.ThrowIfUnconsumed" /> accepts them on the commands that support capture,
    ///     and only on those.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    public static bool Requested(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Both are consumed unconditionally (| not ||): a short-circuit would leave the second spelling
        // looking like an unknown option whenever the first was given.
        bool flagged = args.Flag("perf") | args.Flag("profile");
        return flagged || Profiling.Enabled || IsTruthy(Environment.GetEnvironmentVariable(
            LegacyEnvironmentVariable));
    }

    /// <summary>The human table, in the tool's existing two-space-indent style.</summary>
    /// <param name="report">The capture to print.</param>
    public static void WriteHuman(PerfReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Frames == 0)
        {
            ConsoleOut.Info("  perf: nothing captured");
            return;
        }

        ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
            $"  perf {report.Frames} frames  frame p50={report.Frame.P50Ms:F3} p99={report.Frame.P99Ms:F3} ms  " +
            $"max {report.MaxFrameFps:F1} fps  render-only {report.MaxRenderFps:F1} fps"));

        ConsoleOut.Info($"  {"stage",-28} {"p50",8} {"p99",8} {"total ms",10} {"share",7}");
        foreach (PerfRow row in report.Stages)
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  {row.Name,-28} {row.Times.P50Ms,8:F3} {row.Times.P99Ms,8:F3} {row.TotalMs,10:F1} " +
                $"{row.SharePct,6:F1}%"));
        }

        // Layers are nested inside the advance and render stages, never additional to them. The two
        // columns do not sum to 100 %.
        ConsoleOut.Info($"  {"layer (nested in stage)",-28} {"p50",8} {"p99",8} {"total ms",10} {"share",7} cache");
        foreach (PerfRow row in report.Layers)
        {
            string cache = row.CacheHitRate is { } rate
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{rate * 100:F1}% hit ({row.CacheReplayed}/{row.CacheReplayed + row.CacheRecorded})")
                : row.CacheUncached > 0
                    ? "uncached"
                    : "";

            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"  {Truncate(row.Label, 28),-28} {row.Times.P50Ms,8:F3} {row.Times.P99Ms,8:F3} " +
                $"{row.TotalMs,10:F1} {row.SharePct,6:F1}% {cache}"));
        }

        IReadOnlyList<PerfRow> slowest = report.Slowest(RankedRows);
        if (slowest.Count > 0)
        {
            ConsoleOut.Info("  slowest: " + string.Join(", ", slowest.Select(r =>
                string.Create(CultureInfo.InvariantCulture, $"{r.Label} {r.SharePct:F1}%"))));
        }
    }

    /// <summary>
    ///     The machine block. Additive by construction: it is one new <c>perf</c> key on the existing
    ///     <c>schema_version: 1</c> payload, and it is absent entirely when capture is off.
    /// </summary>
    /// <param name="report">The capture to project.</param>
    public static JsonObject ToJson(PerfReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        JsonArray stages = [];
        foreach (PerfRow row in report.Stages)
        {
            stages.Add(Row(row));
        }

        JsonArray layers = [];
        foreach (PerfRow row in report.Layers)
        {
            JsonObject node = Row(row);
            node["phase"] = row.Phase == LayerPhase.Advance ? "advance" : "render";
            node["cache"] = new JsonObject
            {
                ["replayed"] = row.CacheReplayed,
                ["recorded"] = row.CacheRecorded,
                ["uncached"] = row.CacheUncached,
                ["hit_rate"] = row.CacheHitRate is { } rate ? Round(rate) : null
            };
            layers.Add(node);
        }

        JsonArray slowest = [];
        foreach (PerfRow row in report.Slowest(RankedRows))
        {
            slowest.Add(new JsonObject
            {
                ["name"] = row.Label,
                ["kind"] = row.Kind == PerfRowKind.Stage ? "stage" : "layer",
                ["total_ms"] = Round(row.TotalMs),
                ["share_pct"] = Round(row.SharePct)
            });
        }

        return new JsonObject
        {
            ["frames"] = report.Frames,
            ["frame_ms"] = Percentiles(report.Frame),
            ["frame_total_ms"] = Round(report.FrameTotalMs),
            ["max_render_fps"] = Round(report.MaxRenderFps),
            ["max_frame_fps"] = Round(report.MaxFrameFps),
            ["stages"] = stages,
            ["layers"] = layers,
            ["slowest"] = slowest
        };
    }

    private static JsonObject Row(PerfRow row)
    {
        JsonObject node = Percentiles(row.Times);
        node["name"] = row.Name;
        node["samples"] = row.Samples;
        node["total_ms"] = Round(row.TotalMs);
        node["share_pct"] = Round(row.SharePct);
        return node;
    }

    private static JsonObject Percentiles(FrameTimeStats stats) => new()
    {
        ["p50"] = Round(stats.P50Ms),
        ["p95"] = Round(stats.P95Ms),
        ["p99"] = Round(stats.P99Ms),
        ["max"] = Round(stats.MaxMs),
        ["mean"] = Round(stats.MeanMs)
    };

    // Four decimals, matching the bench payload's own rounding: a p50 of 0.0412 ms is a real
    // measurement, and RenderCommand.Round's one decimal would report it as 0.
    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..width];

    private static bool IsTruthy(string? raw) =>
        raw is not null &&
        (string.Equals(raw, "1", StringComparison.Ordinal) ||
         string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase));
}
