#region

using System.Diagnostics;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The design's exit criterion for this phase: any fixture renders to a correct, non-blank PNG in
///     well under a second, with no app and no window.
/// </summary>
[NotInParallel]
[Category("Render")]
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

        // `synthetic-empty` is exactly that: no players, no map bundle, and therefore no derived floor
        // band, no pane, and a background-only frame. That is the shipping behaviour of the app for a
        // scene with nothing in it, not a regression in the CLI, so the entry named "empty" is the one
        // entry allowed to be uniform.
        if (!string.Equals(name, "synthetic-empty", StringComparison.Ordinal))
        {
            await Assert.That(IsUniform(bitmap)).IsFalse();
        }
    }

    // Budget, for the same reason every allocation figure is: a wall-clock ceiling measures the
    // machine as much as the code. This failed three times in one session at 1.6 s, 2.4 s and 6.4 s
    // while concurrent builds loaded the box, and passed alone at 12 s for the whole suite. The
    // budget lane is where a figure that can be wrong about a correct build belongs.
    [Test]
    [Category("Budget")]
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

    /// <summary>
    ///     <b>From the command line.</b> <c>dv2d render --layers markers</c> answered
    ///     <i>"unknown layer id(s): markers. Known: playback2d.debuggrid"</i> for four phases, while
    ///     <c>dv2d.md</c> documented this command as the design-iteration loop for "a marker style, a
    ///     cone fill, an ink outline". Asserted through the reported <c>layers</c> array rather than a
    ///     zero exit code: the whole failure mode being closed here is a command that succeeds while
    ///     drawing something else.
    /// </summary>
    /// <param name="spelling">The <c>--layers</c> value; both the bare and prefixed forms are accepted.</param>
    [Test]
    [Arguments("markers")]
    [Arguments("playback2d.markers")]
    [Arguments("radar,markers,bomb")]
    public async Task Layers_NamesTheRealSceneLayers(string spelling)
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "layers.png"), "--size", "640x360", "--layers", spelling, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);

        string[] drawn = [.. ((JsonArray)run.Json()["layers"]!).Select(n => n!.GetValue<string>())];
        Console.WriteLine($"[layers] --layers {spelling} -> {string.Join(",", drawn)}");

        await Assert.That(drawn.Length).IsEqualTo(spelling.Split(',').Length);
        await Assert.That(drawn).Contains(SceneLayerIds.Markers);
        await Assert.That(drawn).DoesNotContain("playback2d.debuggrid");
    }

    /// <summary>
    ///     The default stack is the seven scene layers, in draw order — what an export draws, minus the
    ///     opt-in chrome. This is the assertion that would have read <c>["playback2d.debuggrid"]</c>.
    /// </summary>
    [Test]
    public async Task DefaultStack_IsTheSevenSceneLayers()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "default.png"), "--size", "640x360", "--json");

        string[] drawn = [.. ((JsonArray)run.Json()["layers"]!).Select(n => n!.GetValue<string>())];
        string[] expected =
            [.. SceneLayerCatalog.SceneStackIds.Where(id => !SceneLayerIds.OptIn.Contains(id))];

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(drawn).IsEquivalentTo(expected);
    }

    /// <summary>
    ///     An opt-in layer this command cannot feed is <b>refused</b>, not silently dropped.
    ///     <c>CreateSceneStack</c> skips a starved opt-in id on purpose — an export request naming
    ///     <c>hud.clock</c> against a source with no clock should draw no HUD rather than an empty box —
    ///     but on a command line, "I asked for it and got a PNG" must not be able to mean "it was not
    ///     there". Both refusals name the command that CAN draw the layer.
    /// </summary>
    /// <param name="layerId">The opt-in id to ask for.</param>
    /// <param name="expectedHint">A phrase the refusal must carry.</param>
    [Test]
    [Arguments("hud.clock", "dv2d export --hud")]
    [Arguments("hud.killfeed", "dv2d export --hud")]
    [Arguments("hud.roster", "dv2d export --hud")]
    [Arguments("annotations", "--ink")]
    public async Task UnfeedableOptInLayer_IsRefusedWithTheCommandThatCanDrawIt(
        string layerId, string expectedHint)
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        using TempDirectory temp = new();

        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath, "--out",
            Path.Combine(temp.Path, "starved.png"), "--layers", layerId);

        Console.WriteLine($"[layers] --layers {layerId} -> exit {run.ExitCode}: {run.StdErr.Trim()}");
        await Assert.That(run.ExitCode).IsEqualTo((int)ExitCode.Usage);
        await Assert.That(run.StdErr).Contains(expectedHint);
    }

    /// <summary>
    ///     <c>--ink</c> feeds the annotation layer for a render with no demo, which is what makes the
    ///     <c>annotated-mirage-b</c> corpus entry — the only golden anywhere covering burned-in ink —
    ///     possible at all. The sidecar is read through the production <c>AnnotationStore</c>, so a
    ///     document the app wrote and one the corpus ships take one code path.
    /// </summary>
    [Test]
    public async Task Ink_RegistersTheAnnotationLayer_AndChangesThePicture()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes",
            "annotated-mirage-b.scene.json");
        string inkPath = Path.Combine(Dv2d.CorpusDirectory, "annotations",
            "annotated-mirage-b.dvann.json");
        using TempDirectory temp = new();
        string withInk = Path.Combine(temp.Path, "ink.png");
        string without = Path.Combine(temp.Path, "no-ink.png");

        CliRun inked = Dv2d.InProcess("render", "--fixture", fixturePath, "--out", withInk,
            "--size", "640x360", "--cpu", "--ink", inkPath,
            "--layers", "radar,trails,areaeffects,vision,markers,bomb,floorlabel,annotations", "--json");
        CliRun bare = Dv2d.InProcess("render", "--fixture", fixturePath, "--out", without,
            "--size", "640x360", "--cpu", "--json");

        await Assert.That(inked.ExitCode).IsEqualTo(0);
        await Assert.That(bare.ExitCode).IsEqualTo(0);

        string[] drawn = [.. ((JsonArray)inked.Json()["layers"]!).Select(n => n!.GetValue<string>())];
        await Assert.That(drawn).Contains(SceneLayerIds.Annotations);

        // Not just "the layer registered": the two SHA-256s the CLI reports must differ, which is the
        // difference between an ink layer that is mounted and an ink layer that draws.
        await Assert.That(inked.Json()["png_sha256"]!.GetValue<string>())
            .IsNotEqualTo(bare.Json()["png_sha256"]!.GetValue<string>());
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
