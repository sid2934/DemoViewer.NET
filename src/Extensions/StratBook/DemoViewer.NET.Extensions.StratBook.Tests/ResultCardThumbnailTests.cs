#region

using System.Diagnostics;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The card thumbnail: the frame built from one step of a positions file (a marker per alive
///     tuple, team from the CT slots, no label, no yaw), the render through the headless path at the
///     card's size, the <c>dv2d</c> golden the same frame is pinned by, and the forty-card budget.
///     <para>
///         <b>One frame, two readers.</b> <c>tests/fixtures/playback2d/scenes/result-card-nuke.scene.json</c>
///         is written by this class (<c>RI_GOLDEN_UPDATE=1</c>) from <see cref="FixturePositions" />
///         and rendered by <c>dv2d golden</c> into the committed PNG; the golden test here renders the
///         same document through the App's own <see cref="SituationThumbnailRenderer" /> and compares
///         against that PNG, so the card path and the pixel gate cannot draw two different pictures.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class ResultCardThumbnailTests
{
    private const string FixtureName = "result-card-nuke";

    // Round 3 on nuke, matched at step 5 (tick 10746 + 5 * 64): five CT on the upper storey, three T
    // with one on the lower Ramp. Z -416 falls in the upper band (floor key -512), -700 below it.
    private const int MatchTick = 10746 + 5 * 64;

    /// <summary>The document the fixture, the golden and the in-process render all come from.</summary>
    internal static RoundPositionsDocument FixturePositions() => new()
    {
        Fingerprint = "ri1;cadence=1;token=1;rf=1;src=pawn;pos=1",
        Demo = new RoundPositionsDemo
        {
            StableKey = "3f9c0a1b2c3d4e5f60718293"
        },
        CadenceTicks = 64,
        Places = ["BombsiteA", "Outside", "Ramp", "Lobby", "Silo"],
        Rounds =
        [
            new RoundPositionsRound
            {
                Number = 3,
                FreezeEndTick = 10746,
                Ct = [0, 2, 5, 7, 9],
                Pos =
                [
                    [], [], [], [], [],
                    [
                        new RoundPosition(0, 620, -420, -416, 0),
                        new RoundPosition(1, 1420, -760, -700, 2),
                        new RoundPosition(2, 960, -640, -416, 0),
                        new RoundPosition(4, -900, 200, -416, 3),
                        new RoundPosition(5, -1300, -900, -416, 1),
                        new RoundPosition(7, -1340, -880, -416, 1),
                        new RoundPosition(8, 2600, 900, -416, 4),
                        new RoundPosition(9, 1380, -980, -416, 2)
                    ]
                ]
            }
        ]
    };

    [Test]
    public async Task TheFrame_HasAMarkerPerTuple_TeamFromTheCtSlots_NoLabelNoYaw()
    {
        RoundPositionsDocument positions = FixturePositions();
        Scene2DFrame? frame = SituationThumbnailRenderer.BuildFrame("de_nuke", positions, 3, MatchTick, null);

        using (Assert.Multiple())
        {
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Markers.Count).IsEqualTo(8);
            await Assert.That(frame.Markers.Count(m => m.Team == 3)).IsEqualTo(5);
            await Assert.That(frame.Markers.Count(m => m.Team == 2)).IsEqualTo(3);
            await Assert.That(frame.Markers.All(m => m.IsAlive && m.Ring == RingState.Team && m.Label.Length == 0 && m.YawDegrees == 0)).IsTrue();
            await Assert.That(frame.Markers.Single(m => m.Slot == 1).Place).IsEqualTo("Ramp");
            await Assert.That(frame.Markers.Single(m => m.Slot == 1).WorldZ).IsEqualTo(-700f);
            await Assert.That(frame.Time.Tick).IsEqualTo(MatchTick);
            await Assert.That(frame.Time.IsDiscontinuity).IsTrue();
            await Assert.That(frame.Map.MapName).IsEqualTo("de_nuke");
            await Assert.That(frame.Map.NetworkedBounds).IsNull().Because("no bundle: the synthetic grid over the players");
            await Assert.That(frame.Map.ObservedBounds.MinX).IsEqualTo(-1340 - 512);

            await Assert.That(SituationThumbnailRenderer.BuildFrame("de_nuke", positions, 4, MatchTick, null)).IsNull();
            await Assert.That(SituationThumbnailRenderer.BuildFrame("de_nuke", positions, 3, 10746, null)).IsNull()
                .Because("step 0 was not sampled");
        }
    }

    [Test]
    public async Task TheRender_IsAPngAtTheCardsSize_WithOrWithoutABundle()
    {
        using SituationThumbnailRenderer renderer = new(_ => null);
        byte[]? png = renderer.Render("de_nuke", FixturePositions(), 3, MatchTick);

        await Assert.That(png).IsNotNull();
        using SKBitmap decoded = SKBitmap.Decode(png!);
        using (Assert.Multiple())
        {
            await Assert.That(decoded.Width).IsEqualTo(SituationThumbnailRenderer.Size.Width);
            await Assert.That(decoded.Height).IsEqualTo(SituationThumbnailRenderer.Size.Height);
            await Assert.That(renderer.Render("de_nuke", FixturePositions(), 3, 10746)).IsNull();
        }
    }

    /// <summary>
    ///     The card's picture over the de_nuke bundle is the committed <c>dv2d</c> golden.
    ///     <c>RI_GOLDEN_UPDATE=1</c> rewrites the scene fixture from <see cref="FixturePositions" />;
    ///     <c>dv2d golden update --name result-card-nuke</c> then rewrites the PNG this compares against.
    /// </summary>
    [Test]
    public async Task TheCardThumbnail_MatchesTheDv2dGolden()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        using LoadedMapAsset? asset = MapAssetPipeline.TryLoad("de_nuke");
        if (asset is null)
        {
            throw new SkipTestException("de_nuke bundle not baked (run tools/DemoViewer.NET.AssetBaker)");
        }

        Scene2DFrame frame = SituationThumbnailRenderer.BuildFrame("de_nuke", FixturePositions(), 3, MatchTick, asset)!;
        string corpus = Path.Combine(repo, "tests", "fixtures", "playback2d");
        string scenePath = Path.Combine(corpus, "scenes", FixtureName + ".scene.json");
        if (Environment.GetEnvironmentVariable("RI_GOLDEN_UPDATE") == "1")
        {
            new SceneFixture
            {
                Frame = frame,
                Time = frame.Time,
                Camera = SituationThumbnailRenderer.CameraFor(frame),
                Size = SituationThumbnailRenderer.Size,
                MapName = "de_nuke",
                MapVersion = asset.Bundle.MapVersion,
                Notes = "A Result Card thumbnail: SituationThumbnailRenderer.BuildFrame over one sampled step of a " +
                        "positions file (ResultCardThumbnailTests.FixturePositions), eight alive markers with no label " +
                        "over de_nuke's two floors at the card's 320x180. Written by ResultCardThumbnailTests with " +
                        "RI_GOLDEN_UPDATE=1; the golden is dv2d golden update --name result-card-nuke."
            }.Save(scenePath);
            Console.WriteLine($"[fixture] wrote {scenePath}");
        }

        string goldenPath = Path.Combine(corpus, "goldens", "cpu",
            $"{FixtureName}@{SituationThumbnailRenderer.Size.Width}x{SituationThumbnailRenderer.Size.Height}.png");
        if (!File.Exists(goldenPath))
        {
            throw new SkipTestException($"missing {goldenPath}; run dv2d golden update --name {FixtureName}");
        }

        byte[] actual = SituationThumbnailRenderer.RenderFrame(frame, asset);
        byte[] expected = await File.ReadAllBytesAsync(goldenPath);

        // Zero labels, so the glyph allowance is zero and this is the unrelaxed perceptual gate.
        GoldenTolerance tolerance = GoldenTolerance.ForLabelledFrame(SituationThumbnailRenderer.Size.Width,
            SituationThumbnailRenderer.Size.Height, 0);
        GoldenComparison result = GoldenImageComparer.Compare(expected, actual, tolerance);
        Console.WriteLine($"[golden] {FixtureName} {result.Summary}");
        if (!result.Match)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{FixtureName}.actual.png"), actual);
            if (GoldenImageComparer.CreateDiffPng(expected, actual) is { } diff)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"{FixtureName}.diff.png"), diff);
            }
        }

        // The committed scene is the frame this build makes, so dv2d and the card agree on the input too.
        SceneFixture committed = SceneFixture.Load(scenePath);
        using (Assert.Multiple())
        {
            await Assert.That(result.FailureReason).IsNull();
            await Assert.That(result.Match).IsTrue();
            await Assert.That(committed.Frame.Markers.Count).IsEqualTo(frame.Markers.Count);
            await Assert.That(committed.Frame.Markers.Select(m => (m.Slot, m.Team, m.WorldX, m.WorldY, m.WorldZ)))
                .IsEquivalentTo(frame.Markers.Select(m => (m.Slot, m.Team, m.WorldX, m.WorldY, m.WorldZ)));
            await Assert.That(committed.MapVersion).IsEqualTo(asset.Bundle.MapVersion);
        }
    }

    /// <summary>Forty synthetic cards on the grid fallback, one worker: the note's bar is under 100 ms in process.</summary>
    [Test]
    [Category("Budget")]
    public async Task FortyThumbnails_RenderInWellUnderASecond()
    {
        RoundPositionsDocument positions = FixturePositions();
        using SituationThumbnailRenderer renderer = new(_ => null);
        renderer.Render("de_nuke", positions, 3, MatchTick); // warm

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < 40; i++)
        {
            await Assert.That(renderer.Render("de_nuke", positions, 3, MatchTick)).IsNotNull();
        }

        watch.Stop();
        Console.WriteLine($"forty thumbnails, no bundle: {watch.ElapsedMilliseconds} ms");
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }
}
