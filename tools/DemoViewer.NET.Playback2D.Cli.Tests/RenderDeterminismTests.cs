#region

using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     Determinism is a contract, not a nicety (design §5.1): the same fixture must produce the same
///     bytes twice in one process <b>and</b> across two processes. The cross-process half is what
///     catches a static initialised from a wall clock, a hash-order dependency, or a JIT-order leak —
///     none of which the in-process repeat would show.
/// </summary>
[NotInParallel]
[Category("Render")]
public class RenderDeterminismTests
{
    [Test]
    public async Task SameFixture_TwiceInOneProcess_IsByteIdentical()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        string first = Hash(Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "a.png"), "--json"));
        string second = Hash(Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "b.png"), "--json"));

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    [Category("Integration")]
    public async Task SameFixture_InAFreshProcess_IsByteIdentical()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        string inProcess = Hash(Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "in.png"), "--json"));

        CliRun subprocess = Dv2d.Subprocess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "sub.png"), "--json");

        await Assert.That(subprocess.ExitCode).IsEqualTo(0);
        await Assert.That(Hash(subprocess)).IsEqualTo(inProcess);
    }

    private static string Hash(CliRun run)
    {
        JsonObject payload = run.Json();
        return payload["png_sha256"]!.GetValue<string>();
    }
}
