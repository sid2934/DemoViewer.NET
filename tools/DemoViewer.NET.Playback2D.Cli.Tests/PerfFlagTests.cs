#region

using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The <c>--perf</c> flag surface (plan <c>P1-perf-instrumentation</c> §2, §4): the block is
///     <b>additive</b> — absent without the flag, and never displacing anything that was already in the
///     <c>schema_version: 1</c> payload.
/// </summary>
[NotInParallel]
public class PerfFlagTests
{
    [Test]
    public async Task WithoutTheFlag_ThePayloadHasNoPerfBlock()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "8", "--warmup", "2", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Json()["perf"]).IsNull();
    }

    [Test]
    public async Task Bench_Perf_BreaksTheFrameDownByStageAndLayer()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "32", "--warmup", "4", "--perf", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);

        JsonObject payload = run.Json();

        // Everything the block is added to must survive untouched: additive means additive.
        await Assert.That(payload["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(payload["frames"]!.GetValue<int>()).IsEqualTo(32);
        await Assert.That(payload["render_ms"]).IsNotNull();

        JsonObject perf = (JsonObject)payload["perf"]!;
        await Assert.That(perf["frames"]!.GetValue<int>()).IsEqualTo(32);
        await Assert.That(perf["max_render_fps"]!.GetValue<double>()).IsGreaterThan(0);
        await Assert.That(perf["max_frame_fps"]!.GetValue<double>()).IsGreaterThan(0);

        // The three stages a bench drives; readback and encode belong to export and must NOT appear
        // here, because a stage nobody measured would read as "free" rather than "absent".
        JsonArray stages = (JsonArray)perf["stages"]!;
        HashSet<string> names = [.. stages.Select(s => ((JsonObject)s!)["name"]!.GetValue<string>())];
        await Assert.That(names).Contains("advance");
        await Assert.That(names).Contains("render");
        await Assert.That(names).Contains("source");
        await Assert.That(names).DoesNotContain("encode");

        JsonArray layers = (JsonArray)perf["layers"]!;
        await Assert.That(layers.Count).IsGreaterThan(0);

        foreach (JsonNode? node in layers)
        {
            JsonObject layer = (JsonObject)node!;
            string phase = layer["phase"]!.GetValue<string>();
            await Assert.That(phase is "advance" or "render").IsTrue();
            await Assert.That(layer["samples"]!.GetValue<int>()).IsEqualTo(32);
            await Assert.That(layer["cache"]).IsNotNull();

            double p50 = layer["p50"]!.GetValue<double>();
            double p99 = layer["p99"]!.GetValue<double>();
            await Assert.That(p99).IsGreaterThanOrEqualTo(p50);
        }

        // The ranking is the deliverable: slowest first, and drawn from rows that are actually in the
        // report rather than recomputed independently.
        JsonArray slowest = (JsonArray)perf["slowest"]!;
        await Assert.That(slowest.Count).IsGreaterThan(0);

        double previous = double.MaxValue;
        foreach (JsonNode? node in slowest)
        {
            double total = ((JsonObject)node!)["total_ms"]!.GetValue<double>();
            await Assert.That(total).IsLessThanOrEqualTo(previous);
            previous = total;
        }
    }

    [Test]
    public async Task Profile_IsAcceptedAsTheAlias()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "8", "--warmup", "2", "--profile", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Json()["perf"]).IsNotNull();
    }

    /// <summary>
    ///     The stage shares must account for the frame, or the decomposition is not a decomposition.
    ///     Layers are nested inside advance and render, so they are excluded from this sum by design.
    /// </summary>
    [Test]
    public async Task StageShares_SumToTheWholeFrame()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "32", "--warmup", "4", "--perf", "--json");

        JsonArray stages = (JsonArray)((JsonObject)run.Json()["perf"]!)["stages"]!;
        double sum = stages.Sum(s => ((JsonObject)s!)["share_pct"]!.GetValue<double>());

        await Assert.That(sum).IsBetween(99.0, 101.0);
    }

    /// <summary>
    ///     <c>--perf</c> is only offered where there is something to capture. On a command that does not
    ///     support it, the unknown-option check must reject it rather than silently ignore it — the same
    ///     discipline every other flag on this tool is held to.
    /// </summary>
    [Test]
    public async Task Render_RejectsThePerfFlag_AsUnknown()
    {
        CliRun run = Dv2d.InProcess("render", "--fixture",
            Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json"),
            "--out", Path.Combine(Path.GetTempPath(), "dv2d-perf-unknown.png"), "--perf");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("--perf");
    }
}
