#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The CI pixel gate. Every case works on a temp COPY of the corpus, so a failing assertion can
///     never rewrite a committed golden.
/// </summary>
[NotInParallel]
public class GoldenCommandTests
{
    [Test]
    public async Task Verify_OnThePristineCorpus_ExitsZero()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonObject counts = (JsonObject)run.Json()["counts"]!;
        await Assert.That(counts["mismatched"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(counts["missing"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(counts["matched"]!.GetValue<int>()).IsGreaterThan(0);
    }

    [Test]
    public async Task Verify_PendingEntries_AreSkippedNotFailed()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");

        JsonObject payload = run.Json();
        await Assert.That(((JsonObject)payload["counts"]!)["skipped"]!.GetValue<int>()).IsGreaterThan(0);

        foreach (JsonNode? result in (JsonArray)payload["results"]!)
        {
            string status = result!["status"]!.GetValue<string>();
            await Assert.That(status is "match" or "skipped").IsTrue();
        }
    }

    [Test]
    public async Task Verify_CorruptedGolden_ExitsFour_AndWritesADiff()
    {
        using CorpusCopy copy = new();
        copy.CorruptGolden("synthetic-tenplayers");
        using TempDirectory diffs = new();

        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--diff-dir", diffs.Path,
            "--tolerance", "byte-exact", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        JsonObject payload = run.Json();
        await Assert.That(((JsonObject)payload["counts"]!)["mismatched"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(diffs.Path, "synthetic-tenplayers.actual.png"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(diffs.Path, "synthetic-tenplayers.diff.png"))).IsTrue();
    }

    [Test]
    public async Task Update_ThenVerify_IsGreen()
    {
        using CorpusCopy copy = new();
        copy.CorruptGolden("synthetic-tenplayers");

        CliRun update = Dv2d.InProcess("golden", "update", "--corpus", copy.Path, "--name",
            "synthetic-tenplayers", "--quiet");
        await Assert.That(update.ExitCode).IsEqualTo(0);

        CliRun verify = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--quiet");
        await Assert.That(verify.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Verify_MissingGolden_ExitsFour()
    {
        using CorpusCopy copy = new();

        // Not synthetic-empty: since the C1 merge that entry is `pending` (see its manifest note), and a
        // pending entry is skipped before its golden is ever looked for — which would make this case
        // pass for the wrong reason.
        File.Delete(Path.Combine(copy.Path, "goldens", "cpu", "synthetic-tenplayers@640x360.png"));
        using TempDirectory diffs = new();

        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--diff-dir", diffs.Path,
            "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        await Assert.That(((JsonObject)run.Json()["counts"]!)["missing"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Verify_StaleMapVersion_IsRefused_RatherThanDiffed()
    {
        using CorpusCopy copy = new();
        copy.SetMapVersion("fitmap-mirage-eco", "deadbeef");

        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--assets",
            Dv2d.AssetsDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        JsonArray results = (JsonArray)run.Json()["results"]!;
        bool sawStale = results.Any(r => r!["status"]!.GetValue<string>() == "stale-assets");
        await Assert.That(sawStale).IsTrue();
    }

    [Test]
    public async Task UnknownName_ExitsOne()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--name",
            "no-such-fixture");

        await Assert.That(run.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task SizeOverride_IsRefused_BecauseAGoldenIsNamedForItsSize()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--size",
            "800x600");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("unknown option");
    }
}

/// <summary>A writable copy of the committed corpus, for cases that must mutate it.</summary>
internal sealed class CorpusCopy : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>Copies the corpus.</summary>
    public CorpusCopy()
    {
        Path = System.IO.Path.Combine(_temp.Path, "playback2d");
        CopyDirectory(Dv2d.CorpusDirectory, Path);
    }

    /// <summary>The copy's root.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>Flips one pixel in an entry's golden — the smallest change byte-exact must catch.</summary>
    /// <param name="name">The entry name.</param>
    public void CorruptGolden(string name)
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Path);
        GoldenCorpusEntry entry = corpus.Find(name)!;
        string goldenPath = entry.GoldenPath(Playback2D.Core.Rendering.RenderBackend.CpuRaster);

        using SKBitmap bitmap = SKBitmap.Decode(goldenPath);
        SKColor pixel = bitmap.GetPixel(1, 1);
        bitmap.SetPixel(1, 1, new SKColor((byte)(pixel.Red ^ 0xFF), pixel.Green, pixel.Blue, pixel.Alpha));

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(goldenPath, data.ToArray());
    }

    /// <summary>Rewrites one entry's <c>map_version</c>, to simulate a re-baked radar.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="version">The version to claim.</param>
    public void SetMapVersion(string name, string version)
    {
        string manifestPath = System.IO.Path.Combine(Path, GoldenCorpus.ManifestFileName);
        JsonObject manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(manifestPath))!;
        foreach (JsonNode? entry in (JsonArray)manifest["entries"]!)
        {
            if (entry!["name"]!.GetValue<string>() == name)
            {
                entry["map_version"] = version;
            }
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory,
                System.IO.Path.Combine(destination, System.IO.Path.GetFileName(directory)));
        }
    }
}
