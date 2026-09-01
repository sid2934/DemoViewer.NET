#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The private-tracker frame source the export session consumes. Every case here is skip-guarded on
///     a demo: CI has none, and a silent pass would be worse than a skip.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class TrackerFrameSourceTests
{
    [Test]
    public async Task FrameIndexForTick_MatchesALinearScan()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        // Sample across the whole demo rather than the first few frames: the binary search's interesting
        // cases are the tick that no frame carries and the tick several frames share.
        for (int i = 0; i < frames.Count; i += Math.Max(1, frames.Count / 40))
        {
            int tick = frames[i].ServerTick;
            int expected = LinearScan(frames, tick);
            await Assert.That(TrackerFrameSource.FrameIndexForTick(frames, tick)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task FrameIndexForTick_OutsideTheDemo_IsMinusOne()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        await Assert.That(TrackerFrameSource.FrameIndexForTick(frames, frames[0].ServerTick - 1))
            .IsEqualTo(-1);
        await Assert.That(TrackerFrameSource.FrameIndexForTick(frames, frames[^1].ServerTick + 1))
            .IsEqualTo(-1);
    }

    [Test]
    public async Task SequentialFrames_MatchAFromZeroReplay()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int start = frames.Count / 4;
        int tickRate = demo.TickRate > 0 ? (int)Math.Round((double)demo.TickRate) : 64;

        using TrackerFrameSource source = new(frames, new SceneFrameBuilder(), start,
            Math.Min(frames.Count - 1, start + 200), tickRate, 1.0, tickRate,
            throwOnNonSequentialAccess: true);
        source.Prepare(CancellationToken.None);

        // Three sampled output frames, each cross-checked against an independent tracker replayed from
        // zero: the definition of "the source did not lose state while stepping".
        foreach (int index in new[]
                 {
                     0, 25, 60
                 })
        {
            Scene2DFrame produced = source.FrameAt(index);
            int demoIndex = source.DemoFrameIndexOf(index);

            EntityTracker oracle = new();
            for (int i = 0; i <= demoIndex; i++)
            {
                oracle.AdvanceOneFrame(frames[i]);
            }

            await Assert.That(source.TimeAt(index).Tick).IsEqualTo(frames[demoIndex].ServerTick);
            await Assert.That(oracle.CurrentTick).IsEqualTo(frames[demoIndex].ServerTick);

            // The builder emits a marker per SEATED slot, so a dead player still has one (held at the
            // last-known position). The live-pawn count is therefore a floor, not an equality.
            int livePawns = LivePawnCount(oracle);
            await Assert.That(livePawns).IsGreaterThan(0);
            await Assert.That(produced.Markers.Count).IsGreaterThanOrEqualTo(livePawns);
        }
    }

    [Test]
    public async Task Rewind_OnAStrictSource_Throws()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int tickRate = demo.TickRate > 0 ? (int)Math.Round((double)demo.TickRate) : 64;

        using TrackerFrameSource source = new(frames, new SceneFrameBuilder(), 0,
            Math.Min(frames.Count - 1, 400), tickRate, 1.0, tickRate,
            throwOnNonSequentialAccess: true);
        source.Prepare(CancellationToken.None);

        source.FrameAt(50);
        await Assert.That(Throws.Capture<InvalidOperationException>(() => source.FrameAt(1))).IsNotNull();
    }

    [Test]
    public async Task SceneTime_IsFixedByFpsAndSpeed_NotByAWallClock()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        int tickRate = demo.TickRate > 0 ? (int)Math.Round((double)demo.TickRate) : 64;

        using TrackerFrameSource source = new(demo.Frames, new SceneFrameBuilder(), 0,
            Math.Min(demo.Frames.Count - 1, 400), 30, 2.0, tickRate);

        // DeltaSeconds = speed / fps, exactly (design 5.1). Anything else and an export's motion
        // depends on how fast the encoder happened to run.
        await Assert.That(source.TimeAt(0).DeltaSeconds).IsEqualTo(2.0 / 30);
        await Assert.That(source.TimeAt(0).IsDiscontinuity).IsTrue();
        await Assert.That(source.TimeAt(1).IsDiscontinuity).IsFalse();
    }

    [Test]
    public async Task DefaultFactory_BuildsItsOwnTracker_NeverASharedOne()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        int tickRate = demo.TickRate > 0 ? (int)Math.Round((double)demo.TickRate) : 64;

        // The private-tracker rule (design 5.7) is a construction-time property: the source is handed a
        // factory, never an instance, so there is no way for it to receive the app's authoritative
        // tracker. Asserted by counting the factory's invocations.
        int created = 0;
        using TrackerFrameSource source = new(demo.Frames, new SceneFrameBuilder(), 0,
            Math.Min(demo.Frames.Count - 1, 100), tickRate, 1.0, tickRate,
            () =>
            {
                created++;
                return new EntityTracker();
            });

        source.Prepare(CancellationToken.None);
        source.FrameAt(0);

        await Assert.That(created).IsEqualTo(1);
    }

    private static int LinearScan(IReadOnlyList<DemoFrame> frames, int tick)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].ServerTick == tick)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LivePawnCount(EntityTracker tracker)
    {
        int count = 0;
        PawnLookup.ForEachLivePawn(tracker, (_, _) => count++);
        return count;
    }
}
