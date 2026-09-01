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

        foreach (string phase in new[]
                 {
                     "advance_ms", "render_ms", "frame_ms"
                 })
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
///     The §6 zero-allocation contract, over the layer stack <c>dv2d</c> actually builds.
///     <para>
///         <b>This is a live gate.</b> While the catalog registered only a placeholder debug-grid layer,
///         which built three <c>SKPaint</c>s inside <c>Render</c>, it measured 3336 B/frame and stayed
///         red for four phases with <c>[Category("Budget")]</c> doing a <c>[Skip]</c>'s job and saying
///         nothing. The catalog registers the real stack now and it passes at <b>0 B/frame</b>. It is a
///         failure again the moment a layer allocates.
///     </para>
///     <para>
///         <c>Budget</c> is kept as the label: every allocation assertion in this repository carries it,
///         because an allocation figure must not flap a required correctness check. That does mean it
///         runs only in the <c>full</c> tier and in the <c>playback2d-budget</c> CI lane, which has to
///         name THIS project and not <c>Playback2D.Tests</c> alone.
///     </para>
/// </summary>
[NotInParallel]
[Category("Budget")]
public class BenchAllocationTests
{
    [Test]
    public async Task SmallestDrawingFixture_AllocatesNothingPerFrame()
    {
        // Deliberately NOT synthetic-empty. Because dv2d sits on the pane pipeline, a frame with no
        // players derives no floor band, gets no pane, and renders nothing at all. It reports 0
        // bytes/frame whatever the layers do: a green light that measures nothing.
        // synthetic-tenplayers is the smallest entry that actually reaches a layer's Render.
        CliRun run = Dv2d.InProcess("bench", "--name", "synthetic-tenplayers", "--corpus",
            Dv2d.CorpusDirectory, "--frames", "512", "--warmup", "64", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Json()["allocated_bytes_per_frame"]!.GetValue<long>()).IsEqualTo(0);
    }

    /// <summary>
    ///     The worst case design §6's numbers are actually stated against: 1080p, two derived floors,
    ///     ten markers, four sixty-four-point trails, twelve area effects, a defusing bomb and both
    ///     floor captions. It was a <c>pending</c> manifest entry (skipped, never run) for a long
    ///     stretch, so the one fixture the budget is written for was the one fixture nothing benched.
    ///     <para>
    ///         Gated through <c>--gate</c> rather than by reading the numbers, so what is asserted is the
    ///         corpus entry's own budget: editing <c>manifest.json</c> moves this test.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WorstCase1080pScene_MeetsItsDeclaredBudget()
    {
        // DV2D_BUDGET_SCALE is what CI relaxes the TIME halves by on a shared runner; it is not set here,
        // so this asserts the unscaled §6 numbers on a developer machine. The allocation half is never
        // scaled anywhere: 0 bytes is 0 bytes.
        CliRun run = Dv2d.InProcess("bench", "--name", "full-scene-budget", "--corpus",
            Dv2d.CorpusDirectory, "--frames", "256", "--warmup", "64", "--cpu", "--gate", "--json");

        JsonObject payload = run.Json();
        Console.WriteLine($"[budget] full-scene-budget render p99 " +
                          $"{payload["render_ms"]!["p99"]!.GetValue<double>():F3} ms, " +
                          $"{payload["allocated_bytes_per_frame"]!.GetValue<long>()} B/frame");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(((JsonObject)payload["gate"]!)["passed"]!.GetValue<bool>()).IsTrue();
    }
}
