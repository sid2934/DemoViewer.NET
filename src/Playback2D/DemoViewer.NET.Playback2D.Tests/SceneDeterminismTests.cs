#region

using System.Globalization;
using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <b>The byte-exact half of B1's exit criterion.</b> The parity gate proves the port landed where
///     the pre-v2 control was; this proves it stays there. Same fixture, same <c>dt</c>, same pixels —
///     every time, on this machine, in this process.
///     <para>
///         It is also what makes export trustworthy: a frame rendered twice must be the same frame, or
///         an encoder's inter-frame compression is being fed noise and a "deterministic export" is a
///         claim nobody checked.
///     </para>
/// </summary>
[NotInParallel]
public class SceneDeterminismTests
{
    private static readonly SKSizeI _size = new(640, 480);

    [Test]
    public async Task SameFixtureAndDt_ProduceIdenticalFramesAcrossRuns()
    {
        string[] first = HashRun(96);
        string[] second = HashRun(96);

        int firstDifference = -1;
        for (int i = 0; i < first.Length; i++)
        {
            if (!string.Equals(first[i], second[i], StringComparison.Ordinal))
            {
                firstDifference = i;
                break;
            }
        }

        Console.WriteLine($"[determinism] 96 frames, first divergence: " +
                          (firstDifference < 0 ? "none" : firstDifference.ToString(CultureInfo.InvariantCulture)));
        await Assert.That(firstDifference).IsEqualTo(-1);
    }

    /// <summary>
    ///     Draw order is <c>(Slot, Order, Id)</c>, so the sequence is a pure function of the registered
    ///     set. Registering the same layers in a different order must therefore produce the same image —
    ///     otherwise a golden silently depends on construction order, and B2 or B4 adding a layer would
    ///     re-baseline the corpus for no visible reason.
    /// </summary>
    [Test]
    public async Task LayerRegistrationOrder_DoesNotChangeTheOutput()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();

        string forward = HashOnce(fixture, false);
        string reversed = HashOnce(fixture, true);

        Console.WriteLine($"[determinism] forward={forward[..16]} reversed={reversed[..16]}");
        await Assert.That(reversed).IsEqualTo(forward);
    }

    /// <summary>
    ///     Picture caching is an optimisation and must be invisible. A layer whose cached replay differs
    ///     from its direct draw is a rendering bug that only shows up on the second frame — the hardest
    ///     kind to notice, and the reason <c>SceneCompositorOptions.EnablePictureCaching</c> exists as a
    ///     bisecting switch.
    /// </summary>
    [Test]
    public async Task PictureCaching_DoesNotChangeTheOutput()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();

        string cached = HashWithOptions(fixture, new SceneCompositorOptions());
        string uncached = HashWithOptions(fixture, new SceneCompositorOptions(false));

        Console.WriteLine($"[determinism] cached={cached[..16]} uncached={uncached[..16]}");
        await Assert.That(uncached).IsEqualTo(cached);
    }

    /// <summary>
    ///     Export renders at a fixed timestep; interactive renders at whatever the animation frame
    ///     reports. Fed the same <c>dt</c>, they must agree — the purpose is a hint to the layers, never
    ///     an input to the drawing.
    /// </summary>
    [Test]
    public async Task ExportAndInteractivePurpose_AgreeAtTheSameDt()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();

        string export = HashOnce(fixture, false, RenderPurpose.Export);
        string interactive = HashOnce(fixture, false, RenderPurpose.Interactive);

        await Assert.That(interactive).IsEqualTo(export);
    }

    private static string[] HashRun(int frames)
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        using SceneStage stage = new(_size);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);
        stage.Renderer.AdvanceCameras = true;

        string[] hashes = new string[frames];
        SceneTime time = fixture.Time;
        for (int i = 0; i < frames; i++)
        {
            SceneTime frameTime = time with
            {
                DeltaSeconds = 1.0 / 64
            };
            stage.Renderer.Advance(fixture.Frame, in frameTime);
            if (i == 0)
            {
                stage.Renderer.FitAll(fixture.Frame);
            }

            stage.Renderer.Render();
            hashes[i] = Sha256(stage.Renderer.SnapshotPng());
        }

        return hashes;
    }

    private static string HashOnce(SceneFixture fixture, bool reverseRegistration,
        RenderPurpose purpose = RenderPurpose.Export)
    {
        using SceneStage stage = new(_size, reverseRegistration: reverseRegistration);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);
        stage.Renderer.Purpose = purpose;

        SceneTime time = fixture.Time;
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.SetAllCameras(fixture.Camera);
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.Render();
        return Sha256(stage.Renderer.SnapshotPng());
    }

    private static string HashWithOptions(SceneFixture fixture, SceneCompositorOptions options)
    {
        using SceneStage stage = new(_size, options: options);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        SceneTime time = fixture.Time;
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.SetAllCameras(fixture.Camera);

        // Twice, so the cached run actually REPLAYS a picture rather than recording one — comparing two
        // first frames would compare two recordings and prove nothing.
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.Render();
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.Render();
        return Sha256(stage.Renderer.SnapshotPng());
    }

    private static string Sha256(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));
}

/// <summary>
///     Keeps the committed <c>full-scene-budget</c> corpus entry in step with the generator that
///     produces it, and writes the file when <c>PB2D_GOLDEN_UPDATE=1</c>.
///     <para>
///         The budget scene is authored in code (<see cref="SyntheticScenes" />) rather than captured,
///         but it is still a corpus entry: C1's <c>dv2d</c> loads it by name and B4 exports it. Two
///         copies of a fixture that can drift is worse than either, so this asserts they agree and
///         regenerates on request.
///     </para>
/// </summary>
[NotInParallel]
public class BudgetFixtureCorpusTests
{
    private const string UpdateEnvVar = "PB2D_GOLDEN_UPDATE";

    [Test]
    public async Task CommittedBudgetFixture_MatchesTheGenerator()
    {
        SceneFixture generated = SyntheticScenes.FullSceneBudget();
        string path = Path.Combine(FixtureCorpus.Root, "scenes",
            $"{SyntheticScenes.FullSceneBudgetName}.scene.json");

        if (!File.Exists(path) ||
            string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.Ordinal))
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"no fixture at {path}. Regenerate deliberately with " +
                    "scripts/update-playback2d-goldens.sh.");
            }

            generated.Save(path);
            Console.WriteLine($"[fixture] wrote {path}");
            return;
        }

        // Round-trip both sides through the serializer before comparing: the committed file has been
        // through it and the generated one has not, so comparing them directly would report the
        // serializer's own normalisation as a mismatch.
        string temp = Path.Combine(Path.GetTempPath(),
            $"{SyntheticScenes.FullSceneBudgetName}-{Guid.NewGuid():N}.scene.json");
        try
        {
            generated.Save(temp);
            string expected = await File.ReadAllTextAsync(path);
            string actual = await File.ReadAllTextAsync(temp);

            await Assert.That(Normalize(actual)).IsEqualTo(Normalize(expected));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string Normalize(string json) => json.ReplaceLineEndings("\n").Trim();
}
