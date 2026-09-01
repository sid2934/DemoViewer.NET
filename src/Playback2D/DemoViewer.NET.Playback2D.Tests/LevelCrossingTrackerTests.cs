#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The crossing detector and the marker snap it drives: design §5.3's "trail and smoothing buffers
///     reset when an entity crosses levels", i.e. the streak-across-the-map artefact.
/// </summary>
public class LevelCrossingTrackerTests
{
    [Test]
    public async Task Crossed_TrueOnlyOnTheFrameOfChange()
    {
        MapSpace space = Two();
        LevelCrossingTracker tracker = new();

        tracker.Update(0, -300, space);
        await Assert.That(tracker.Crossed(0)).IsFalse().Because("a first observation is not a crossing");

        tracker.EndFrame();
        await Assert.That(tracker.Update(0, 400, space)).IsTrue();
        await Assert.That(tracker.Crossed(0)).IsTrue();

        tracker.EndFrame();
        await Assert.That(tracker.Update(0, 400, space)).IsFalse();
        await Assert.That(tracker.Crossed(0)).IsFalse();
    }

    [Test]
    public async Task EndFrame_ClearsCrossedSet()
    {
        MapSpace space = Two();
        LevelCrossingTracker tracker = new();

        tracker.Update(0, -300, space);
        tracker.Update(1, -300, space);
        tracker.EndFrame();
        tracker.Update(0, 400, space);

        await Assert.That(tracker.CrossedSlots).HasCount().EqualTo(1);
        tracker.EndFrame();
        await Assert.That(tracker.CrossedSlots).IsEmpty();
    }

    /// <summary>
    ///     Step 10 of the remap algorithm: after a rebuild every cached assignment describes bands that
    ///     may no longer exist, so the tracker starts over rather than reporting a crossing for an entity
    ///     that merely got re-keyed.
    /// </summary>
    [Test]
    public async Task Reset_OnRebuild_DoesNotReportPhantomCrossings()
    {
        MapSpace space = Two();
        LevelCrossingTracker tracker = new();
        space.LevelSetChanged += tracker.Reset;

        tracker.Update(0, -300, space);
        tracker.EndFrame();

        space.Rebuild([new FloorSlice(-1280, -640), new FloorSlice(-640, 0), new FloorSlice(0, 640)]);

        await Assert.That(tracker.Count).IsEqualTo(0);
        await Assert.That(tracker.Update(0, -300, space)).IsFalse();
        await Assert.That(tracker.Crossed(0)).IsFalse();
    }

    [Test]
    public async Task DitherAcrossTheBoundary_ReportsNoCrossing()
    {
        MapSpace space = Two();
        LevelCrossingTracker tracker = new();

        tracker.Update(0, -100, space);
        int crossings = 0;
        for (int i = 0; i < 128; i++)
        {
            tracker.EndFrame();
            if (tracker.Update(0, i % 2 == 0 ? 10 : -10, space))
            {
                crossings++;
            }
        }

        await Assert.That(crossings).IsEqualTo(0);
    }

    /// <summary>
    ///     The actual defect: a player who changes floor and X/Y at the same instant. Without the snap
    ///     the dot glides the whole plan distance between the two floors, painting a line across a map
    ///     it never walked.
    /// </summary>
    [Test]
    public async Task MarkerSmoothing_SnapsOnCrossing_RatherThanGliding()
    {
        MapSpace space = Two();
        LevelCrossingTracker tracker = new();
        MarkerSmoother smoother = new()
        {
            LevelCrossings = tracker
        };

        PlayerMarker[] onLower = [Marker(0, 0, 0, -300)];
        tracker.Update(0, -300, space);
        smoother.Advance(onLower, 1.0 / 64);
        tracker.EndFrame();

        // 200 units away in plan, under the 250u teleport threshold, so the distance rule would glide.
        PlayerMarker[] onUpper = [Marker(0, 200, 0, 400)];
        tracker.Update(0, 400, space);
        smoother.Advance(onUpper, 1.0 / 64);

        (float X, float Y)? snapped = smoother.Position(0);
        await Assert.That(snapped!.Value.X).IsEqualTo(200f);

        // Without the tracker the same move glides: this is what the snap is being measured against.
        MarkerSmoother gliding = new();
        gliding.Advance(onLower, 1.0 / 64);
        gliding.Advance(onUpper, 1.0 / 64);
        await Assert.That(gliding.Position(0)!.Value.X).IsLessThan(200f);
    }

    [Test]
    [Category("Budget")]
    public async Task Update_AllocatesNothing_InTheSteadyState()
    {
        MapSpace space = Two();
        LevelCrossingTracker tracker = new();

        for (int i = 0; i < 10; i++)
        {
            tracker.Update(i, -300, space);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 512; f++)
        {
            for (int i = 0; i < 10; i++)
            {
                tracker.Update(i, -300, space);
            }

            tracker.EndFrame();
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 frames × 10 slots: {delta} bytes");
        await Assert.That(delta).IsEqualTo(0);
    }

    private static PlayerMarker Marker(int slot, float x, float y, float z) =>
        new(slot, 2, x, y, z, 0, RingState.Team, 1, "P", true);

    private static MapSpace Two()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-640, 0), new FloorSlice(0, 640)]);
        return space;
    }
}
