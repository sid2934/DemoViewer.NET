#region

using System.Text.Json.Nodes;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <c>--query</c>: the query fixture reaches <c>playback2d.query</c>, and naming the layer with
///     nothing to feed it is a usage error rather than a PNG that quietly lacks it, the same contract
///     <c>--ink</c> has.
/// </summary>
[NotInParallel]
[Category("Render")]
public class QueryFlagTests
{
    private static string Scene => Path.Combine(Dv2d.CorpusDirectory, "scenes", "query-nuke-execute.scene.json");

    private static string Query =>
        Path.Combine(Dv2d.CorpusDirectory, FixtureQuery.CorpusDirectoryName, "query-nuke-execute.dvquery.json");

    [Test]
    public async Task Render_WithAQueryFixture_DrawsTheQueryLayer()
    {
        using TempDirectory temp = new();
        string outPath = Path.Combine(temp.Path, "query.png");

        CliRun run = Dv2d.InProcess("render", "--fixture", Scene, "--query", Query,
            "--layers", "radar,query", "--out", outPath, "--cpu", "--assets", Dv2d.AssetsDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.StdErr);
        JsonObject payload = run.Json();
        string[] layers = [.. payload["layers"]!.AsArray().Select(n => n!.GetValue<string>())];
        await Assert.That(layers).Contains("playback2d.query");
        await Assert.That(File.Exists(outPath)).IsTrue();
    }

    [Test]
    public async Task NamingTheQueryLayer_WithNoFixture_IsAUsageError()
    {
        using TempDirectory temp = new();
        CliRun run = Dv2d.InProcess("render", "--fixture", Scene, "--layers", "query",
            "--out", Path.Combine(temp.Path, "never.png"), "--cpu");

        await Assert.That(run.ExitCode).IsEqualTo(ExitCode.Usage.ToInt());
        await Assert.That(run.StdErr).Contains("--query");
    }

    [Test]
    public async Task AMissingQueryFixture_IsAUsageError()
    {
        using TempDirectory temp = new();
        CliRun run = Dv2d.InProcess("render", "--fixture", Scene,
            "--query", Path.Combine(temp.Path, "absent.dvquery.json"),
            "--out", Path.Combine(temp.Path, "never.png"), "--cpu");

        await Assert.That(run.ExitCode).IsEqualTo(ExitCode.Usage.ToInt());
    }

    [Test]
    public async Task TheCorpusConvention_FeedsTheGoldenEntry()
    {
        await Assert.That(FixtureQuery.ForCorpusEntry(Dv2d.CorpusDirectory, "query-nuke-execute")).IsNotNull();
        await Assert.That(FixtureQuery.ForCorpusEntry(Dv2d.CorpusDirectory, "synthetic-empty")).IsNull()
            .Because("an entry with no query fixture keeps the layer out of its stack");
    }
}
