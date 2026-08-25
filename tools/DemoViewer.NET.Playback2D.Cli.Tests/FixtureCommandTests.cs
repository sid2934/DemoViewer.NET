#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>Corpus authoring and checking.</summary>
[NotInParallel]
public class FixtureCommandTests
{
    [Test]
    public async Task Verify_OnTheCommittedCorpus_ExitsZero()
    {
        CliRun run = Dv2d.InProcess("fixture", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonObject counts = (JsonObject)run.Json()["counts"]!;
        await Assert.That(counts["failed"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(counts["ok"]!.GetValue<int>()).IsGreaterThan(0);
    }

    [Test]
    public async Task List_NamesEveryManifestEntry()
    {
        CliRun run = Dv2d.InProcess("fixture", "list", "--corpus", Dv2d.CorpusDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonArray listed = (JsonArray)run.Json()["entries"]!;
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);

        await Assert.That(listed.Count).IsEqualTo(corpus.Entries.Count);
        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            bool found = listed.Any(e => e!["name"]!.GetValue<string>() == entry.Name);
            await Assert.That(found).IsTrue();
        }
    }

    [Test]
    public async Task Verify_MalformedScene_ExitsFour()
    {
        using CorpusCopy copy = new();
        File.WriteAllText(Path.Combine(copy.Path, "scenes", "synthetic-empty.scene.json"), "{ not json");

        CliRun run = Dv2d.InProcess("fixture", "verify", "--corpus", copy.Path, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        await Assert.That(((JsonObject)run.Json()["counts"]!)["failed"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Capture_WritesAFixtureThatRoundTripsAndRegistersIt()
    {
        string demo = Dv2d.RequireDemo();
        using CorpusCopy copy = new();

        CliRun run = Dv2d.InProcess("fixture", "capture", "--demo", demo, "--frame", "500",
            "--name", "capture-under-test", "--corpus", copy.Path, "--size", "320x180", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonObject payload = run.Json();
        string scenePath = payload["scene"]!.GetValue<string>();
        await Assert.That(File.Exists(scenePath)).IsTrue();

        SceneFixture loaded = SceneFixture.Load(scenePath);
        await Assert.That(loaded.Size.Width).IsEqualTo(320);
        await Assert.That(loaded.Size.Height).IsEqualTo(180);
        await Assert.That(loaded.MapName).IsEqualTo(payload["map"]!.GetValue<string>());
        await Assert.That(loaded.Time.Tick).IsEqualTo(payload["tick"]!.GetValue<int>());
        await Assert.That(loaded.SourceDemoId).IsEqualTo(Path.GetFileName(demo));

        GoldenCorpus corpus = GoldenCorpus.Load(copy.Path);
        await Assert.That(corpus.Find("capture-under-test")).IsNotNull();

        // And the freshly-authored fixture is renderable and gateable straight away.
        CliRun verify = Dv2d.InProcess("fixture", "verify", "--corpus", copy.Path, "--quiet");
        await Assert.That(verify.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Capture_UnsafeName_ExitsOne()
    {
        using CorpusCopy copy = new();

        CliRun run = Dv2d.InProcess("fixture", "capture", "--demo", "irrelevant.dem", "--frame", "0",
            "--name", "../escape", "--corpus", copy.Path);

        await Assert.That(run.ExitCode).IsEqualTo(1);
    }
}
