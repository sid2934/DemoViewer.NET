#region

using DemoViewer.NET.Playback2D.Cli;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <c>Main</c>'s dispatch and the exit-code table. Console redirection is process-global, so every
///     case here runs serially.
/// </summary>
[NotInParallel]
public class ProgramDispatchTests
{
    private static readonly int[] _documentedExitCodes = [0, 1, 2, 3, 4, 5, 6];

    [Test]
    public async Task NoArguments_PrintsUsage_AndExitsOne()
    {
        CliRun run = Dv2d.InProcess();

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdOut).Contains("dv2d");
    }

    [Test]
    public async Task Help_ExitsZero()
    {
        CliRun run = Dv2d.InProcess("--help");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.StdOut).Contains("exit codes:");
    }

    [Test]
    public async Task UnknownVerb_ExitsOne()
    {
        CliRun run = Dv2d.InProcess("frobnicate");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("unknown command");
    }

    [Test]
    public async Task Usage_ListsEveryImplementedVerb()
    {
        string usage = Program.Usage;
        foreach (string verb in Program.Verbs)
        {
            await Assert.That(usage).Contains(verb);
        }
    }

    [Test]
    public async Task ExitCodeTable_IsTheDocumentedOne()
    {
        // The numbers are a contract with CI: 4 means "the change is bad", everything else means
        // "the run is broken". Renumbering silently would make a green build out of a regression.
        int[] actual =
        [
            ExitCode.Success.ToInt(), ExitCode.Usage.ToInt(), ExitCode.InputMissing.ToInt(),
            ExitCode.RuntimeFailure.ToInt(), ExitCode.GateFailure.ToInt(), ExitCode.Cancelled.ToInt(),
            ExitCode.EnvironmentUnavailable.ToInt()
        ];

        await Assert.That(actual).IsEquivalentTo(_documentedExitCodes);
        string usage = Program.Usage;
        await Assert.That(usage).Contains("GATE FAILURE");
    }

    [Test]
    public async Task MissingFixture_ExitsTwo()
    {
        CliRun run = Dv2d.InProcess("render", "--fixture", "no/such/fixture.scene.json");

        await Assert.That(run.ExitCode).IsEqualTo(2);
        await Assert.That(run.StdErr).Contains("fixture not found");
    }

    [Test]
    public async Task UnknownOption_ExitsOne()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");
        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(Path.GetTempPath(), "dv2d-unknown-option.png"), "--frobnicate", "1");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("unknown option");
    }

    [Test]
    public async Task UnknownLayerId_ExitsOne()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");
        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--layers", "not-a-layer");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("unknown layer id");
    }

    [Test]
    public async Task StrictGpu_ExitsSix()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");
        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--gpu", "--strict-backend");

        await Assert.That(run.ExitCode).IsEqualTo(6);
    }

    [Test]
    public async Task MultiLevelLayout_ExitsSix_RatherThanRenderingOnePaneSilently()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");
        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--layout", "single");

        await Assert.That(run.ExitCode).IsEqualTo(6);
        await Assert.That(run.StdErr).Contains("level model");
    }

    [Test]
    public async Task Export_IsDeferredToB4_WithExitSix()
    {
        CliRun run = Dv2d.InProcess("export", "--demo", "whatever.dem", "--from", "0", "--to", "10");

        await Assert.That(run.ExitCode).IsEqualTo(6);
        await Assert.That(run.StdErr).Contains("B4 export session");
    }
}
