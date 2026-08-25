#region

using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The budget gate. The numbers themselves are machine-specific; what is asserted here is the
///     shape, the monotonicity, and that <c>--gate</c> actually fails a run that misses its budget.
/// </summary>
[NotInParallel]
public class BenchCommandTests
{
    [Test]
    public async Task Json_CarriesEveryDocumentedField_AndMonotonePercentiles()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "16", "--warmup", "2", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonObject payload = run.Json();

        await Assert.That(payload["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(payload["command"]!.GetValue<string>()).IsEqualTo("bench");
        await Assert.That(payload["frames"]!.GetValue<int>()).IsEqualTo(16);
        await Assert.That(payload["warmup"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(((JsonObject)payload["source"]!)["name"]!.GetValue<string>())
            .IsEqualTo("duel-mirage-b");
        await Assert.That(payload["allocated_bytes_per_frame"]).IsNotNull();
        await Assert.That(payload["gc"]).IsNotNull();
        await Assert.That(payload["budget"]).IsNotNull();
        await Assert.That(payload["metadata"]).IsNotNull();

        foreach (string phase in new[] { "advance_ms", "render_ms", "frame_ms" })
        {
            JsonObject stats = (JsonObject)payload[phase]!;
            double p50 = stats["p50"]!.GetValue<double>();
            double p95 = stats["p95"]!.GetValue<double>();
            double p99 = stats["p99"]!.GetValue<double>();
            double max = stats["max"]!.GetValue<double>();

            await Assert.That(p95).IsGreaterThanOrEqualTo(p50);
            await Assert.That(p99).IsGreaterThanOrEqualTo(p95);
            await Assert.That(max).IsGreaterThanOrEqualTo(p99);
        }
    }

    [Test]
    public async Task Gate_ImpossibleBudget_ExitsFour_NamingTheViolation()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "16", "--warmup", "2", "--gate", "--budget-p99-ms", "0.0000001",
            "--budget-bytes-per-frame", "1000000000", "--budget-advance-p99-ms", "1000", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        JsonObject gate = (JsonObject)run.Json()["gate"]!;
        await Assert.That(gate["passed"]!.GetValue<bool>()).IsFalse();
        await Assert.That(((JsonArray)gate["violations"]!).Count).IsEqualTo(1);
        await Assert.That(gate["violations"]![0]!.GetValue<string>()).Contains("render_p99_ms");
    }

    [Test]
    public async Task Gate_GenerousBudget_ExitsZero()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "16", "--warmup", "2", "--gate", "--budget-p99-ms", "10000",
            "--budget-advance-p99-ms", "10000", "--budget-bytes-per-frame", "1000000000", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(((JsonObject)run.Json()["gate"]!)["passed"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task BudgetScale_MultipliesTimeBudgetsButNotTheAllocationBudget()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "8", "--warmup", "1", "--budget-scale", "4", "--budget-bytes-per-frame",
            "1000000000", "--json");

        JsonObject budget = (JsonObject)run.Json()["budget"]!;
        await Assert.That(budget["scale"]!.GetValue<double>()).IsEqualTo(4);
        await Assert.That(budget["render_p99_ms"]!.GetValue<double>()).IsEqualTo(32.0);
        await Assert.That(budget["advance_p99_ms"]!.GetValue<double>()).IsEqualTo(8.0);
    }

    [Test]
    public async Task ReportDir_WritesOneJsonFile()
    {
        using TempDirectory reports = new();

        CliRun run = Dv2d.InProcess("bench", "--name", "duel-mirage-b", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "8", "--warmup", "1", "--report-dir", reports.Path, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        string[] written = Directory.GetFiles(reports.Path, "*.json");
        await Assert.That(written.Length).IsEqualTo(1);
        await Assert.That(Path.GetFileName(written[0])).StartsWith("dv2d-duel-mirage-b_");
    }

    [Test]
    public async Task UnknownCorpusName_ExitsOne()
    {
        CliRun run = Dv2d.InProcess("bench", "--name", "no-such-fixture", "--corpus",
            Dv2d.CorpusDirectory, "--frames", "4");

        await Assert.That(run.ExitCode).IsEqualTo(1);
    }
}

/// <summary>
///     The §6 zero-allocation contract. <b>Expected to fail until <c>SceneLayerCatalog</c> registers B1's
///     seven layers</b> — the stack <c>dv2d</c> builds today is still B0's smoke layer, which constructs
///     its three <c>SKPaint</c>s inside <c>Render</c> — so it is categorised <c>Budget</c> and kept out of
///     the correctness lane. Enable it in the PR that closes that seam (C1 risk R6 / deviation 14).
/// </summary>
[NotInParallel]
[Category("Budget")]
public class BenchAllocationTests
{
    [Test]
    public async Task SmallestDrawingFixture_AllocatesNothingPerFrame()
    {
        // Deliberately NOT synthetic-empty. Since the C1 merge put dv2d on B1's pane pipeline, a frame
        // with no players derives no floor band, gets no pane, and therefore renders nothing at all — it
        // reports 0 bytes/frame whatever the layers do, which is a green light that measures nothing.
        // synthetic-tenplayers is the smallest entry that actually reaches a layer's Render.
        CliRun run = Dv2d.InProcess("bench", "--name", "synthetic-tenplayers", "--corpus",
            Dv2d.CorpusDirectory, "--frames", "512", "--warmup", "64", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Json()["allocated_bytes_per_frame"]!.GetValue<long>()).IsEqualTo(0);
    }
}
