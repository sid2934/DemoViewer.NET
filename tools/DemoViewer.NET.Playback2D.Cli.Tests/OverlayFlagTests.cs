#region

using System.Text.Json.Nodes;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <c>--overlay</c>: the overlay fixture reaches <c>playback2d.overlay</c>, and naming the layer
///     with nothing to feed it is a usage error rather than a PNG that quietly lacks it, the same
///     contract <c>--ink</c> and <c>--query</c> have.
/// </summary>
[NotInParallel]
[Category("Render")]
public class OverlayFlagTests
{
    private static string Scene => Path.Combine(Dv2d.CorpusDirectory, "scenes", "overlay-nuke-execute.scene.json");

    private static string Overlay =>
        Path.Combine(Dv2d.CorpusDirectory, FixtureOverlay.CorpusDirectoryName, "overlay-nuke-execute.dvoverlay.json");

    [Test]
    public async Task Render_WithAnOverlayFixture_DrawsTheOverlayLayer()
    {
        using TempDirectory temp = new();
        string outPath = Path.Combine(temp.Path, "overlay.png");

        CliRun run = Dv2d.InProcess("render", "--fixture", Scene, "--overlay", Overlay,
            "--layers", "radar,overlay", "--out", outPath, "--cpu", "--assets", Dv2d.AssetsDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.StdErr);
        JsonObject payload = run.Json();
        string[] layers = [.. payload["layers"]!.AsArray().Select(n => n!.GetValue<string>())];
        await Assert.That(layers).Contains("playback2d.overlay");
        await Assert.That(File.Exists(outPath)).IsTrue();
    }

    [Test]
    public async Task NamingTheOverlayLayer_WithNoFixture_IsAUsageError()
    {
        using TempDirectory temp = new();
        CliRun run = Dv2d.InProcess("render", "--fixture", Scene, "--layers", "overlay",
            "--out", Path.Combine(temp.Path, "never.png"), "--cpu");

        await Assert.That(run.ExitCode).IsEqualTo(ExitCode.Usage.ToInt());
        await Assert.That(run.StdErr).Contains("--overlay");
    }

    [Test]
    public async Task AMissingOverlayFixture_IsAUsageError()
    {
        using TempDirectory temp = new();
        CliRun run = Dv2d.InProcess("render", "--fixture", Scene,
            "--overlay", Path.Combine(temp.Path, "absent.dvoverlay.json"),
            "--out", Path.Combine(temp.Path, "never.png"), "--cpu");

        await Assert.That(run.ExitCode).IsEqualTo(ExitCode.Usage.ToInt());
    }

    [Test]
    public async Task TheCorpusConvention_FeedsTheGoldenEntry()
    {
        await Assert.That(FixtureOverlay.ForCorpusEntry(Dv2d.CorpusDirectory, "overlay-nuke-execute")).IsNotNull();
        await Assert.That(FixtureOverlay.ForCorpusEntry(Dv2d.CorpusDirectory, "query-nuke-execute")).IsNull()
            .Because("an entry with no overlay fixture keeps the layer out of its stack");
    }
}
