#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     What <see cref="SingleLayout" /> and the per-level radar binding actually draw, over the real
///     two-floor <c>nuke-multilevel</c> capture.
///     <para>
///         Two committed goldens (<c>nuke-single-upper</c>, <c>nuke-multilevel-noradar</c>) plus the
///         assertion that matters most for a phase that touched the shared pane machinery: the
///         <b>stacked</b> picture is byte-identical after a Stacked → Single → Stacked round trip. That
///         is the acceptance line "StackedLayout's output is byte-identical to B1's golden", proved
///         against the renderer rather than by committing the same PNG twice — B1's own
///         <c>GoldenParityTests</c> already pins the stacked path against the pre-v2 control.
///     </para>
/// </summary>
[NotInParallel]
public class LevelGoldenTests
{
    private const string Corpus = "nuke-multilevel";
    private const string UpdateEnvVar = "PB2D_GOLDEN_UPDATE";

    [Test]
    public async Task SingleLayout_ShowsOneFloorFullHeight_AndMatchesItsGolden()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName);

        byte[] upper = RenderSingle(stage, fixture, TopMostIndex);

        await Assert.That(stage.Renderer.Panes.Panes).HasCount().EqualTo(1);
        await Assert.That(stage.Renderer.Panes.Panes[0].ViewportRect)
            .IsEqualTo(new SKRect(0, 0, fixture.Size.Width, fixture.Size.Height));

        await CompareOrWrite("nuke-single-upper", fixture.Size, upper);
    }

    /// <summary>
    ///     The two floors must not render the same picture. On this capture every marker is on the upper
    ///     floor, so the lower pane is the map with no players on it — which is exactly the difference a
    ///     level filter is supposed to make, and would be invisible if the single pane still passed the
    ///     "no Z filtering" sentinel.
    /// </summary>
    [Test]
    public async Task EachLevel_RendersItsOwnFloor_NotBoth()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName);

        byte[] lower = RenderSingle(stage, fixture, _ => 0);
        byte[] upper = RenderSingle(stage, fixture, TopMostIndex);

        GoldenComparison result = GoldenImageComparer.Compare(lower, upper, GoldenTolerance.ByteExact);
        Console.WriteLine($"[levels] lower vs upper differ on {result.MismatchedFraction:P4} of pixels");
        await Assert.That(result.Match).IsFalse();
    }

    [Test]
    public async Task NoRadarBinding_FallsThroughToTheGrid_AndMatchesItsGolden()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName, false);

        byte[] png = stage.RenderFixturePng(fixture);

        MapSpace space = stage.Renderer.Levels.Space;
        await Assert.That(space.Levels).HasCount().EqualTo(2);
        await Assert.That(space.RadarBinding).IsEqualTo(RadarBindingQuality.None);
        foreach (MapLevel level in space.Levels)
        {
            await Assert.That(level.HasRadar).IsFalse();
        }

        await CompareOrWrite("nuke-multilevel-noradar", fixture.Size, png);
    }

    /// <summary>
    ///     <b>The no-regression gate for the stacked path.</b> Switching to Single and back must return
    ///     the exact same pixels: if the id-keyed pane retention lost a camera, or the single-pane
    ///     sentinel leaked into the stacked branch, this is where it shows.
    /// </summary>
    [Test]
    public async Task StackedRender_IsByteIdentical_AfterASingleModeRoundTrip()
    {
        SceneFixture fixture = LoadNuke();
        using SceneStage stage = new(fixture.Size);
        stage.TryBindMap(fixture.MapName);

        byte[] before = stage.RenderFixturePng(fixture);

        SingleLayout single = new();
        StackedLayout stacked = (StackedLayout)stage.Renderer.Panes.Policy;
        RenderSingle(stage, fixture, TopMostIndex, single);
        RenderSingle(stage, fixture, _ => 0, single);

        stage.Renderer.Panes.Policy = stacked;
        stage.Renderer.DisplayMode = LevelDisplayMode.Stacked;
        byte[] after = stage.RenderFixturePng(fixture);

        GoldenComparison result = GoldenImageComparer.Compare(before, after, GoldenTolerance.ByteExact);
        Console.WriteLine($"[levels] stacked round trip: match={result.Match} " +
                          $"maxDelta={result.MaxChannelDelta}");
        await Assert.That(result.Match).IsTrue();
    }

    private static int TopMostIndex(MapSpace space) => space.Levels.Count - 1;

    private static byte[] RenderSingle(SceneStage stage, SceneFixture fixture,
        Func<MapSpace, int> pick, SingleLayout? policy = null)
    {
        // One stacked advance first, so the level set exists to pick from — the same two-advance shape
        // SceneStage.RenderFixturePng uses, and for the same reason.
        SceneTime time = fixture.Time;
        stage.Renderer.Advance(fixture.Frame, in time);

        SingleLayout single = policy ?? new SingleLayout();
        MapSpace space = stage.Renderer.Levels.Space;
        single.ActiveLevelId = space.Levels[pick(space)].Id;

        stage.Renderer.Panes.Policy = single;
        stage.Renderer.DisplayMode = LevelDisplayMode.Single;
        return stage.RenderFixturePng(fixture);
    }

    private static SceneFixture LoadNuke()
    {
        string path = Path.Combine(FixtureCorpus.Root, "scenes", $"{Corpus}.scene.json");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"no captured scene for '{Corpus}'");
        }

        return SceneFixture.Load(path);
    }

    private static async Task CompareOrWrite(string name, SKSizeI size, byte[] actual)
    {
        string goldenPath = Path.Combine(FixtureCorpus.Root, "goldens", "cpu",
            $"{name}@{size.Width}x{size.Height}.png");

        if (!File.Exists(goldenPath))
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"no golden at {goldenPath}. Regenerate deliberately with " +
                    "scripts/update-playback2d-goldens.sh.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllBytesAsync(goldenPath, actual);
            Console.WriteLine($"[golden] wrote {goldenPath} ({actual.Length} bytes)");
            return;
        }

        byte[] expected = await File.ReadAllBytesAsync(goldenPath);
        GoldenComparison result =
            GoldenImageComparer.Compare(expected, actual, GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[golden] {name} match={result.Match} maxDelta={result.MaxChannelDelta} " +
                          $"diff={result.MismatchedFraction:P4}");

        if (!result.Match)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.actual.png"), actual);
            if (GoldenImageComparer.CreateDiffPng(expected, actual) is { } diff)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}.diff.png"), diff);
            }

            Console.WriteLine($"[golden] wrote the actual + diff images to {dir}");
        }

        await Assert.That(result.FailureReason).IsNull();
        await Assert.That(result.Match).IsTrue();
    }
}
