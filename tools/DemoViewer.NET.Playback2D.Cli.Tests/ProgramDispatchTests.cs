#region

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

    /// <summary>
    ///     Every option the export usage block advertises must actually be an option.
    ///     <para>
    ///         The usage text is the only documentation a user of a headless tool reads, and this repo's
    ///         parser rejects anything it does not consume, so an advertised-but-unimplemented flag is a
    ///         documented invocation that exits 1 with "unknown option". Review found four of them
    ///         (<c>--round</c>, <c>--camera</c>, <c>--ffmpeg</c>, <c>--progress</c>) shipped alongside
    ///         three real ones that went unmentioned.
    ///     </para>
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task EveryOptionTheExportUsageAdvertises_IsAnOptionExportAccepts()
    {
        string demo = Dv2d.RequireDemo();
        string output = Path.Combine(Path.GetTempPath(), $"dv2d-usage-{Guid.NewGuid():N}.gif");

        foreach (string option in UsageOptionsFor("export"))
        {
            // A one-frame GIF: fast, needs no ffmpeg, and reaches the parser either way. All that is
            // asserted is that the parser did not reject the NAME.
            CliRun run = Dv2d.InProcess("export", "--demo", demo, "--from", "0", "--to", "0",
                "--format", "gif", "--fps", "20", "--size", "64x64", "--out", output, option);

            await Assert.That(run.StdErr).DoesNotContain($"unknown option: {option}");
        }

        if (File.Exists(output))
        {
            File.Delete(output);
        }
    }

    /// <summary>Pulls the <c>--name</c> tokens out of one verb's block of <see cref="Program.Usage" />.</summary>
    /// <param name="verb">The verb whose block to read.</param>
    private static List<string> UsageOptionsFor(string verb)
    {
        List<string> options = [];
        bool inBlock = false;

        foreach (string raw in Program.Usage.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith(verb + " ", StringComparison.Ordinal))
            {
                inBlock = true;
            }
            else if (inBlock && trimmed.Length > 0 && !trimmed.StartsWith('-') &&
                     !trimmed.StartsWith('[') && !trimmed.StartsWith('('))
            {
                break;
            }

            if (!inBlock)
            {
                continue;
            }

            foreach (string token in line.Split([' ', '\t', '[', ']', '(', ')', '|'],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
                {
                    options.Add(token);
                }
            }
        }

        return options;
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
    public async Task Probe_IsDispatched()
    {
        // The exit-code half of `probe` is BackendFlagTests' subject; this is Main's dispatch table.
        CliRun run = Dv2d.InProcess("probe");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.StdOut).Contains("backend=");
    }

    [Test]
    public async Task MultiLevelLayout_ExitsSix_RatherThanRenderingOnePaneSilently()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");
        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--layout", "single");

        await Assert.That(run.ExitCode).IsEqualTo(6);
        await Assert.That(run.StdErr).Contains("level model");
    }

    /// <summary>
    ///     <c>export</c> is a real verb now, so a missing demo fails the way it does on every other
    ///     command: a runtime failure naming the file, not the exit 6 that once meant "this verb does not
    ///     exist yet".
    /// </summary>
    [Test]
    public async Task Export_WithAMissingDemo_FailsNamingTheFile()
    {
        CliRun run = Dv2d.InProcess("export", "--demo", "whatever.dem", "--from", "0", "--to", "10");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.StdErr).Contains("whatever.dem");
    }

    [Test]
    public async Task Export_WithoutADemo_IsAUsageError()
    {
        CliRun run = Dv2d.InProcess("export");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("--demo");
    }
}
