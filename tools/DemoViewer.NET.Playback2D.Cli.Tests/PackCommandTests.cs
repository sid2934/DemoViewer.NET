#region

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <c>dv2d pack</c> (Headless Packs, plan.md §3): the queue file, argument parsing, and the exit
///     codes a scheduled run depends on. Nothing here parses a demo: a plan needs one, but planning and
///     the CLI's own refusals do not, and that is exactly what a bad queue file or a bad flag hits first.
/// </summary>
[NotInParallel]
public class PackCommandTests
{
    private const string EmptyQueue = """{ "schemaVersion": 1, "clock": { "kind": "dv-frame-clock" }, "entries": [] }""";

    [Test]
    public async Task WithoutAQueue_IsAUsageError()
    {
        CliRun run = Dv2d.InProcess("pack");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("--queue");
    }

    [Test]
    public async Task MissingQueueFile_ExitsTwo()
    {
        CliRun run = Dv2d.InProcess("pack", "--queue", "no/such/review-queue.json");

        await Assert.That(run.ExitCode).IsEqualTo(2);
        await Assert.That(run.StdErr).Contains("queue file not found");
    }

    [Test]
    public async Task MalformedQueueFile_ExitsThree()
    {
        using TempDirectory temp = new();
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, "{ not json");

        CliRun run = Dv2d.InProcess("pack", "--queue", queue);

        await Assert.That(run.ExitCode).IsEqualTo(3);
    }

    [Test]
    public async Task QueueFile_AtANewerSchema_IsRefused()
    {
        using TempDirectory temp = new();
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, """{ "schemaVersion": 99, "clock": { "kind": "dv-frame-clock" }, "entries": [] }""");

        CliRun run = Dv2d.InProcess("pack", "--queue", queue);

        await Assert.That(run.ExitCode).IsEqualTo(3);
        await Assert.That(run.StdErr).Contains("schema 99");
    }

    [Test]
    public async Task EveryClipsDemoMissing_LeavesEveryOneOut_AndRefusesBeforeOpeningAFile()
    {
        using TempDirectory temp = new();
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, $$"""
            {
              "schemaVersion": 1,
              "clock": { "kind": "dv-frame-clock" },
              "entries": [
                { "id": "{{Guid.NewGuid()}}", "kind": "clip", "demoPath": "no/such/a.dem",
                  "fromTick": 0, "toTick": 640, "tickRate": 64, "note": "opener", "source": "manual" },
                { "id": "{{Guid.NewGuid()}}", "kind": "section", "title": "A executes" },
                { "id": "{{Guid.NewGuid()}}", "kind": "clip", "demoPath": "no/such/b.dem",
                  "fromTick": 0, "toTick": 640, "tickRate": 64, "note": "smokes", "source": "manual" }
              ]
            }
            """);
        string outPath = Path.Combine(temp.Path, "pack.mp4");

        CliRun run = Dv2d.InProcess("pack", "--queue", queue, "--out", outPath);

        await Assert.That(run.ExitCode).IsEqualTo(3);
        await Assert.That(run.StdErr).Contains("no clip in the queue that can be rendered");
        // Warnings (a clip left out before anything opened) are Info-tier and, without --json, share
        // stdout with the rest of the human output; only the final failure goes to stderr.
        await Assert.That(run.StdOut).Contains("a.dem: demo not found");
        await Assert.That(run.StdOut).Contains("b.dem: demo not found");
        await Assert.That(File.Exists(outPath)).IsFalse();
    }

    [Test]
    public async Task AnEmptyRange_IsLeftOutBeforeRendering()
    {
        using TempDirectory temp = new();
        string demo = Path.Combine(temp.Path, "a.dem");
        File.WriteAllBytes(demo, [0]); // never opened: the range is empty, so PackPlanner skips it first.
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, $$"""
            {
              "schemaVersion": 1,
              "clock": { "kind": "dv-frame-clock" },
              "entries": [
                { "id": "{{Guid.NewGuid()}}", "kind": "clip", "demoPath": "{{demo.Replace("\\", "\\\\")}}",
                  "fromTick": 64, "toTick": 64, "tickRate": 64, "note": "zero-length", "source": "manual" }
              ]
            }
            """);

        CliRun run = Dv2d.InProcess("pack", "--queue", queue, "--out", Path.Combine(temp.Path, "pack.mp4"));

        await Assert.That(run.ExitCode).IsEqualTo(3);
        await Assert.That(run.StdOut).Contains("empty range");
    }

    [Test]
    public async Task NonSquareSize_IsAUsageError()
    {
        using TempDirectory temp = new();
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, EmptyQueue);

        CliRun run = Dv2d.InProcess("pack", "--queue", queue, "--size", "1920x1080");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("square");
    }

    [Test]
    public async Task UnknownQuality_IsAUsageError()
    {
        using TempDirectory temp = new();
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, EmptyQueue);

        CliRun run = Dv2d.InProcess("pack", "--queue", queue, "--quality", "cinematic");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("--quality");
    }

    [Test]
    public async Task UnknownOption_ExitsOne()
    {
        using TempDirectory temp = new();
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, EmptyQueue);

        CliRun run = Dv2d.InProcess("pack", "--queue", queue, "--frobnicate", "1");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("unknown option");
    }

    /// <summary>
    ///     The same check <c>ProgramDispatchTests.EveryOptionTheExportUsageAdvertises_IsAnOptionExportAccepts</c>
    ///     runs for <c>export</c>: every flag the usage block documents for <c>pack</c> must be an option the
    ///     parser actually consumes, or a documented invocation exits 1 with "unknown option". An empty
    ///     queue reaches <c>PackExporter</c>'s "no clip" refusal only after every flag is parsed, which is
    ///     the boundary this checks: past it, the process no longer cares whether a name was recognised.
    /// </summary>
    [Test]
    public async Task EveryOptionThePackUsageAdvertises_IsAnOptionPackAccepts()
    {
        using TempDirectory temp = new();
        string queue = Path.Combine(temp.Path, "review-queue.json");
        File.WriteAllText(queue, EmptyQueue);

        foreach (string option in UsageOptionsFor("pack"))
        {
            CliRun run = Dv2d.InProcess("pack", "--queue", queue, option);

            await Assert.That(run.StdErr).DoesNotContain($"unknown option: {option}");
        }
    }

    [Test]
    public async Task Json_KeepsStdoutToOneObject_EvenOnFailure()
    {
        CliRun run = Dv2d.InProcess("pack", "--queue", "no/such/review-queue.json", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(2);
        await Assert.That(run.StdOut).IsEmpty();
        await Assert.That(run.StdErr).Contains("queue file not found");
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
}
