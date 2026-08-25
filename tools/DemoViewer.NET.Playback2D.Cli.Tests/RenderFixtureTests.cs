#region

using System.Diagnostics;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The design's exit criterion for this phase: any fixture renders to a correct, non-blank PNG in
///     well under a second, with no app and no window.
/// </summary>
[NotInParallel]
public class RenderFixtureTests
{
    /// <summary>Every non-pending corpus entry, as test data.</summary>
    public static IEnumerable<Func<string>> RenderableEntries()
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);
        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            if (entry.Pending || !File.Exists(entry.ScenePath))
            {
                continue;
            }

            string name = entry.Name;
            yield return () => name;
        }
    }

    [Test]
    [MethodDataSource(nameof(RenderableEntries))]
    public async Task EveryCorpusEntry_RendersToACorrectNonBlankPng(string name)
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);
        GoldenCorpusEntry entry = corpus.Find(name)!;

        using TempDirectory temp = new();
        string outPath = Path.Combine(temp.Path, name + ".png");

        CliRun run = Dv2d.InProcess("render", "--fixture", entry.ScenePath, "--out", outPath,
            "--size", $"{entry.Size.Width}x{entry.Size.Height}", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonObject payload = run.Json();
        await Assert.That(payload["width"]!.GetValue<int>()).IsEqualTo(entry.Size.Width);
        await Assert.That(payload["height"]!.GetValue<int>()).IsEqualTo(entry.Size.Height);

        using SKBitmap bitmap = SKBitmap.Decode(outPath);
        await Assert.That(bitmap).IsNotNull();
        await Assert.That(bitmap.Width).IsEqualTo(entry.Size.Width);
        await Assert.That(bitmap.Height).IsEqualTo(entry.Size.Height);
        await Assert.That(IsUniform(bitmap)).IsFalse();
    }

    [Test]
    public async Task WarmRender_IsUnderOneSecond()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");
        using TempDirectory temp = new();
        string outPath = Path.Combine(temp.Path, "warm.png");

        // First run pays the JIT and the Skia native load; the design's claim is about the loop a
        // designer actually sits in, which is the second run onward.
        Dv2d.InProcess("render", "--fixture", fixturePath, "--out", outPath, "--quiet");

        long started = Stopwatch.GetTimestamp();
        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--out", outPath, "--json");
        double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Json()["elapsed_ms"]!.GetValue<double>()).IsLessThan(1000);
        await Assert.That(wallMs).IsLessThan(1000);
    }

    [Test]
    public async Task NoRadar_RendersWithoutMapArt()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "no-radar.png"), "--no-radar", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Json()["assets_source"]!.GetValue<string>()).IsEqualTo("disabled");
    }

    [Test]
    public async Task ExplicitAssetsRoot_IsReportedAsSuchInJson()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "assets.png"), "--assets", Dv2d.AssetsDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonObject payload = run.Json();
        await Assert.That(payload["assets_source"]!.GetValue<string>()).IsEqualTo("flag");
        await Assert.That(payload["map_version"]!.GetValue<string>()).IsEqualTo("1efb9403");
    }

    // "Not blank" has to mean "more than one colour", not "not black": a fixture whose camera is wrong
    // renders a uniform background, which a byte-length or a header check would happily accept.
    private static bool IsUniform(SKBitmap bitmap)
    {
        SKColor first = bitmap.GetPixel(0, 0);
        for (int y = 0; y < bitmap.Height; y += 3)
        {
            for (int x = 0; x < bitmap.Width; x += 3)
            {
                if (bitmap.GetPixel(x, y) != first)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
