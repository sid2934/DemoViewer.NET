#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The strat as a frame source (step-authoring.md §3.6, §7): the frame count is the tracker's
///     arithmetic at 64, only frame 0 is a discontinuity, markers are the sampled tracks, a smoke lasts
///     18 s, and the whole thing is a pure function of the tick, so two runs hash alike and two frame
///     rates agree wherever their ticks meet.
/// </summary>
public class StratFrameSourceTests
{
    private const int RoundSeconds = 115;
    private const int SmokeTick = 320;
    private const int MolotovTick = 800;

    [Test]
    [Arguments(0, 3840, 20, 1.0)]
    [Arguments(0, 7360, 10, 1.0)]
    [Arguments(0, 7360, 20, 1.5)]
    [Arguments(128, 1000, 50, 1.0)]
    [Arguments(64, 64, 20, 1.0)]
    [Arguments(0, 1, 60, 0.5)]
    public async Task FrameCount_IsTheTrackerArithmeticAt64(int start, int end, int fps, double speed)
    {
        // TrackerFrameSource.OutputFrameCount's formula, written out with the rate at 64: a dialog that
        // sized its request any other way would refuse one length and encode another.
        double ticksPerOutputFrame = speed * 64 / fps;
        int span = end - start;
        int expected = span == 0 ? 1 : 1 + (int)Math.Floor(span / ticksPerOutputFrame);

        StratFrameSource source = new(Spec(start, end, fps, speed));

        await Assert.That(StratFrameSource.OutputFrameCount(start, end, fps, speed)).IsEqualTo(expected);
        await Assert.That(source.FrameCount).IsEqualTo(expected);
    }

    [Test]
    public async Task FrameCount_MatchesTheDesignsWorkedExamples()
    {
        // 1:55 to 0:55 at 20 fps fits the GIF cap; a whole 115 s round needs 10 fps (§3.6).
        await Assert.That(StratFrameSource.OutputFrameCount(0, 60 * 64, 20, 1.0)).IsEqualTo(1201);
        await Assert.That(StratFrameSource.OutputFrameCount(0, 115 * 64, 10, 1.0)).IsEqualTo(1151);
        await Assert.That(StratFrameSource.OutputFrameCount(10, 9, 20, 1.0)).IsEqualTo(0);
    }

    [Test]
    public async Task TimeAt_IsADiscontinuityOnlyAtFrameZero()
    {
        StratFrameSource source = new(Spec(0, 640, 20, 1.0));

        await Assert.That(source.TimeAt(0).IsDiscontinuity).IsTrue();
        for (int i = 1; i < source.FrameCount; i++)
        {
            await Assert.That(source.TimeAt(i).IsDiscontinuity).IsFalse();
        }

        SceneTime time = source.TimeAt(5);
        await Assert.That(time.Tick).IsEqualTo(16);
        await Assert.That(time.FrameIndex).IsEqualTo(5);
        await Assert.That(time.DemoSeconds).IsEqualTo(0.25).Within(1e-12);
        await Assert.That(time.DeltaSeconds).IsEqualTo(1.0 / 20).Within(1e-12);
    }

    [Test]
    public async Task Markers_AreTheSampledTracks_WithLabelAndTeam()
    {
        StratSceneSpec spec = Spec(0, 1280, 20, 1.0);
        StratFrameSource source = new(spec);
        List<TokenSample> expected = [];

        for (int i = 0; i < source.FrameCount; i++)
        {
            Scene2DFrame frame = source.FrameAt(i);
            spec.Tracks.Sample(source.TimeAt(i).Tick, expected);

            await Assert.That(frame.Markers.Count).IsEqualTo(expected.Count);
            for (int m = 0; m < expected.Count; m++)
            {
                PlayerMarker marker = frame.Markers[m];
                TokenKeyframe at = expected[m].Position;
                await Assert.That(marker.Slot).IsEqualTo(TokenSlots.OrderOf(expected[m].Slot));
                await Assert.That(marker.WorldX).IsEqualTo(at.X);
                await Assert.That(marker.WorldY).IsEqualTo(at.Y);
                await Assert.That((double)marker.WorldZ).IsEqualTo(at.MarkerZ).Within(1e-3);
                await Assert.That(marker.YawDegrees).IsEqualTo(at.YawDegrees);
                await Assert.That(marker.Ring).IsEqualTo(RingState.Team);
                await Assert.That(marker.RingAlpha).IsEqualTo(1.0);
                await Assert.That(marker.IsAlive).IsTrue();
                await Assert.That(marker.SteamId).IsEqualTo(0UL);
            }
        }

        // A before its first keyframe is not drawn; B is, and so is the opponent.
        Scene2DFrame first = source.FrameAt(0);
        await Assert.That(first.Markers.Select(m => m.Label).ToArray()).IsEquivalentTo(["BB", "O1"]);
        await Assert.That(first.Markers.Single(m => m.Label == "BB").Team).IsEqualTo(2);
        await Assert.That(first.Markers.Single(m => m.Label == "O1").Team).IsEqualTo(3);
    }

    [Test]
    public async Task Smoke_IsUpFor18Seconds_ThenGone()
    {
        // 64 fps at speed 1 is one output frame per tick, so a frame index is a tick.
        StratFrameSource source = new(Spec(0, SmokeTick + 18 * 64 + 10, 64, 1.0));

        await Assert.That(Smokes(source.FrameAt(SmokeTick - 1))).IsEqualTo(0);
        await Assert.That(Smokes(source.FrameAt(SmokeTick))).IsEqualTo(1);
        await Assert.That(Smokes(source.FrameAt(SmokeTick + 18 * 64 - 1))).IsEqualTo(1);
        await Assert.That(Smokes(source.FrameAt(SmokeTick + 18 * 64))).IsEqualTo(0);

        AreaEffect smoke = source.FrameAt(SmokeTick).AreaEffects.Single(e => e.Kind == AreaEffectKind.Smoke);
        await Assert.That(smoke.WorldX).IsEqualTo(-300f);
        await Assert.That(smoke.WorldY).IsEqualTo(200f);
    }

    [Test]
    public async Task Molotov_BurnsFor7Seconds_AndAFlashDrawsNothing()
    {
        StratFrameSource source = new(Spec(0, MolotovTick + 7 * 64 + 10, 64, 1.0));

        await Assert.That(Fires(source.FrameAt(MolotovTick - 1))).IsEqualTo(0);
        await Assert.That(Fires(source.FrameAt(MolotovTick))).IsGreaterThan(0);
        await Assert.That(Fires(source.FrameAt(MolotovTick + 7 * 64 - 1))).IsGreaterThan(0);
        await Assert.That(Fires(source.FrameAt(MolotovTick + 7 * 64))).IsEqualTo(0);

        // The flash cue sits at tick 100 with no smoke or fire up yet.
        await Assert.That(source.FrameAt(100).AreaEffects.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GameInfoAndMap_CarryTheRoundClockAndTheBundle()
    {
        StratFrameSource source = new(Spec(0, 1280, 64, 1.0));

        Scene2DFrame start = source.FrameAt(0);
        await Assert.That(start.GameInfo.Phase).IsEqualTo("Live");
        await Assert.That(start.GameInfo.RoundTime).IsEqualTo("1:55");
        await Assert.That(start.GameInfo.RoundSeconds).IsEqualTo(115.0);
        await Assert.That(start.GameInfo.TScore).IsEqualTo(0);
        await Assert.That(start.Map.MapName).IsEqualTo("de_mirage");
        await Assert.That(start.Map.NetworkedBounds).IsEqualTo(Bounds);
        await Assert.That(start.Bomb).IsNull();
        await Assert.That(start.Trails.Count).IsEqualTo(0);
        await Assert.That(start.KillFeed.Count).IsEqualTo(0);

        Scene2DFrame later = source.FrameAt(640);
        await Assert.That(later.GameInfo.RoundTime).IsEqualTo("1:45");
        await Assert.That(later.GameInfo.RoundSeconds).IsEqualTo(105.0);
    }

    [Test]
    public async Task Hud_CountsTheSameClockDown_AndStopsAtZero()
    {
        StratHudDataSource hud = new(RoundSeconds);

        await Assert.That(hud.At(0).CountdownSeconds).IsEqualTo(115.0);
        await Assert.That(hud.At(640).CountdownSeconds).IsEqualTo(105.0);
        await Assert.That(hud.At(640).Tick).IsEqualTo(640);
        await Assert.That(hud.At(116 * 64).CountdownSeconds).IsEqualTo(0.0);
        await Assert.That(hud.At(0).KillRows.Count).IsEqualTo(0);
        await Assert.That(hud.At(0).Roster.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FrameAt_IsPureInTheTick_ARewindAgrees()
    {
        StratFrameSource source = new(Spec(0, 1280, 20, 1.0));

        string before = Describe(source.FrameAt(40));
        _ = source.FrameAt(200);
        _ = source.FrameAt(3);
        string after = Describe(source.FrameAt(40));

        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task TwoFrameRates_AgreeWhereverTheirTicksMeet()
    {
        StratFrameSource slow = new(Spec(0, 1280, 20, 1.0));
        StratFrameSource fast = new(Spec(0, 1280, 50, 1.0));

        Dictionary<int, int> fastByTick = [];
        for (int j = 0; j < fast.FrameCount; j++)
        {
            fastByTick.TryAdd(fast.TimeAt(j).Tick, j);
        }

        int met = 0;
        for (int i = 0; i < slow.FrameCount; i++)
        {
            int tick = slow.TimeAt(i).Tick;
            if (!fastByTick.TryGetValue(tick, out int j))
            {
                continue;
            }

            met++;
            await Assert.That(Describe(fast.FrameAt(j))).IsEqualTo(Describe(slow.FrameAt(i)));
        }

        // Every whole second is on both clocks, and more besides.
        await Assert.That(met).IsGreaterThanOrEqualTo(21);
    }

    [Test]
    public async Task TwoRuns_HashIdentically_At20Fps()
    {
        IReadOnlyList<string> first = await HashRun();
        IReadOnlyList<string> second = await HashRun();

        await Assert.That(first.Count).IsEqualTo(StratFrameSource.OutputFrameCount(0, 960, 20, 1.0));
        await Assert.That(second.Count).IsEqualTo(first.Count);
        for (int i = 0; i < first.Count; i++)
        {
            await Assert.That(second[i]).IsEqualTo(first[i]);
        }

        // The design default: GIF at 20 fps. Moving tokens and a smoke: the frames are not all one picture, so equal hashes mean something.
        await Assert.That(first.Distinct(StringComparer.Ordinal).Count()).IsGreaterThan(1);
    }

    private static async Task<IReadOnlyList<string>> HashRun()
    {
        StratSceneSpec spec = Spec(0, 960, 20, 1.0);
        StratFrameSource source = new(spec);
        StratHudDataSource hud = new(spec.RoundSeconds);

        string[] layers =
        [
            SceneLayerIds.Radar, SceneLayerIds.Trails, SceneLayerIds.AreaEffects, SceneLayerIds.Vision,
            SceneLayerIds.Markers, SceneLayerIds.Bomb, SceneLayerIds.FloorLabel, SceneLayerIds.HudClock
        ];

        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(layers, hud: hud);
        using CpuSurfaceProvider surfaces = new();
        HashingFrameSink sink = new();

        await new SceneExportSession(compositor).RunAsync(
            ExportFixtures.Request(source.FrameCount, ExportFormats.Gif, new SKSizeI(96, 64),
                layerIds: new HashSet<string>(layers, StringComparer.Ordinal), fps: 20),
            source, sink, surfaces, null, CancellationToken.None);

        return sink.FrameHashes;
    }

    private static readonly WorldBounds Bounds = new(-3000, -3000, 1500, 1500);

    private static StratSceneSpec Spec(int start, int end, int fps, double speed)
    {
        // A from 1:50 (tick 320) walking to 1:40 then held; B from the start, holding, then a jump; one
        // opponent standing still. Enough motion that a wrong tick would show.
        TokenTrackSet tracks = new(
        [
            new TokenTrack("A", [new TokenKeyframe(320, -500, 0, 0, 0), new TokenKeyframe(960, 500, 400, 0, 90)]),
            new TokenTrack("B",
                [new TokenKeyframe(0, 0, -800, 0, 180), new TokenKeyframe(640, 200, -600, 0, 270)],
                [128, 0], [TokenInterpolation.Linear, TokenInterpolation.Linear]),
            new TokenTrack("O1", [new TokenKeyframe(0, -1500, 900, 0, 45)])
        ]);

        StepSchedule schedule = new([(Guid.NewGuid(), 0), (Guid.NewGuid(), 320), (Guid.NewGuid(), 960)]);

        return new StratSceneSpec(
            tracks, schedule, new AnnotationSession(new AnnotationDocument()),
            [new TokenLabel("A", null, 2), new TokenLabel("B", "BB", 2), new TokenLabel("O1", "", 3)],
            "de_mirage", [], Bounds, null,
            [
                new UtilityCue(100, GrenadeKind.Flash, 0, 0, 0),
                new UtilityCue(SmokeTick, GrenadeKind.Smoke, -300, 200, 0),
                new UtilityCue(MolotovTick, GrenadeKind.Molotov, 400, -100, 0)
            ],
            RoundSeconds, start, end, fps, speed);
    }

    private static int Smokes(Scene2DFrame frame) => frame.AreaEffects.Count(e => e.Kind == AreaEffectKind.Smoke);

    private static int Fires(Scene2DFrame frame) => frame.AreaEffects.Count(e => e.Kind == AreaEffectKind.Fire);

    // The frame's world state as text, so two frames compare by value; the time is left out because two
    // sources at different rates number their frames differently.
    private static string Describe(Scene2DFrame frame) =>
        string.Join(";", frame.Markers.Select(m => $"{m.Slot}:{m.WorldX}:{m.WorldY}:{m.WorldZ}:{m.YawDegrees}:{m.Label}")) +
        "|" + string.Join(";", frame.AreaEffects.Select(e => $"{e.Kind}:{e.WorldX}:{e.WorldY}")) +
        "|" + frame.GameInfo.RoundTime + "|" + frame.GameInfo.RoundSeconds;
}
