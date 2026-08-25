#region

using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The <c>--json</c> discipline (decision 8): exactly one object on stdout, humans on stderr,
///     <c>schema_version: 1</c>, snake_case keys. Asserted from a real subprocess, because the split
///     between the two streams is the whole point and only a subprocess has two real streams.
/// </summary>
[NotInParallel]
public class JsonContractTests
{
    /// <summary>Every command that emits a stdout payload, as an argument vector.</summary>
    public static IEnumerable<Func<(string Name, string[] Args)>> JsonCommands()
    {
        yield return () => ("render", [
            "render", "--fixture",
            Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json"),
            "--out", Path.Combine(Path.GetTempPath(), "dv2d-json-contract.png"), "--json"
        ]);
        yield return () => ("golden", ["golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json"]);
        yield return () => ("bench", [
            "bench", "--name", "synthetic-empty", "--corpus", Dv2d.CorpusDirectory,
            "--frames", "8", "--warmup", "1", "--json"
        ]);
        yield return () => ("fixture-list", ["fixture", "list", "--corpus", Dv2d.CorpusDirectory, "--json"]);
        yield return () => ("fixture-verify",
            ["fixture", "verify", "--corpus", Dv2d.CorpusDirectory, "--json"]);
    }

    [Test]
    [MethodDataSource(nameof(JsonCommands))]
    public async Task StdoutIsOneObject_StderrCarriesTheProse(
        (string Name, string[] Args) command)
    {
        CliRun run = Dv2d.Subprocess(command.Args);

        await Assert.That(run.ExitCode).IsEqualTo(0);

        JsonObject payload = run.Json();
        await Assert.That(payload["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(payload["command"]).IsNotNull();
        await Assert.That(payload["ok"]).IsNotNull();

        foreach (string key in payload.Select(static p => p.Key))
        {
            await Assert.That(IsSnakeCase(key)).IsTrue();
        }

        // Nothing human-readable may leak onto stdout: a caller piping into jq must not have to filter.
        await Assert.That(run.StdOut.TrimStart()).StartsWith("{");
        await Assert.That(run.StdOut.TrimEnd()).EndsWith("}");
    }

    [Test]
    public async Task WithoutJson_HumanLinesGoToStdout()
    {
        CliRun run = Dv2d.Subprocess("fixture", "list", "--corpus", Dv2d.CorpusDirectory);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.StdOut).Contains("duel-mirage-b");
    }

    [Test]
    public async Task WithJson_HumanLinesGoToStderr()
    {
        CliRun run = Dv2d.Subprocess("fixture", "list", "--corpus", Dv2d.CorpusDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.StdErr).Contains("duel-mirage-b");
    }

    private static bool IsSnakeCase(string key) =>
        key.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}
