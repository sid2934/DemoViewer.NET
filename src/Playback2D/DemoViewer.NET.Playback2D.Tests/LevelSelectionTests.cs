#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     "Which level does the single pane show." Under stacked bands the follow camera can hold a pane
///     whose player is elsewhere; under one pane that stops being a filter and becomes a decision, and
///     these cases pin how the decision behaves when the followed player is missing, pinned, or gone.
/// </summary>
public class LevelSelectionTests
{
    private const double Dt = 1.0 / 64;

    /// <summary>
    ///     The graceful-orphan rule, ported from the pre-v2 <c>TryFollow</c>: a dead, disconnected or
    ///     not-yet-spawned player leaves the view exactly where it was. Falling back to level 0 would
    ///     yank the viewer to the basement every time their player died.
    /// </summary>
    [Test]
    public async Task AutoFollow_HoldsLevel_WhenFollowedMarkerAbsent()
    {
        MapSpace space = Two();
        LevelSelection selection = new(space)
        {
            FollowedSlot = 3
        };

        SceneTime time = Time();
        selection.Update(in time, FrameWith(3, -300));
        MapLevelId lower = space.Levels[0].Id;
        await Assert.That(selection.ActiveLevelId).IsEqualTo(lower);

        // Slot 3 is no longer in the frame at all.
        for (int i = 0; i < 128; i++)
        {
            selection.Update(in time, FrameWith(7, 400));
        }

        await Assert.That(selection.ActiveLevelId).IsEqualTo(lower);
    }

    [Test]
    public async Task ManualPick_PinsLevel_AgainstFollowedPlayerMove()
    {
        MapSpace space = Two();
        LevelSelection selection = new(space)
        {
            FollowedSlot = 3
        };

        MapLevelId lower = space.Levels[0].Id;
        selection.PickManually(lower);
        await Assert.That(selection.Mode).IsEqualTo(LevelSelectionMode.Manual);

        SceneTime time = Time();
        for (int i = 0; i < 128; i++)
        {
            selection.Update(in time, FrameWith(3, 400));
        }

        await Assert.That(selection.ActiveLevelId).IsEqualTo(lower);
    }

    [Test]
    public async Task EnableAutoFollow_ClearsDwell()
    {
        MapSpace space = Two();
        LevelSelection selection = new(space)
        {
            FollowedSlot = 3
        };

        SceneTime time = Time();
        selection.Update(in time, FrameWith(3, -300));

        // Half a dwell's worth of the other floor, then a manual pick and a re-arm.
        for (int i = 0; i < 10; i++)
        {
            selection.Update(in time, FrameWith(3, 400));
        }

        await Assert.That(selection.Hysteresis.PendingSeconds).IsGreaterThan(0);

        selection.EnableAutoFollow();
        await Assert.That(selection.Hysteresis.PendingSeconds).IsEqualTo(0);
        await Assert.That(selection.Mode).IsEqualTo(LevelSelectionMode.AutoFollow);

        // Re-armed, the very next frame on the other floor adopts it — the user just asked for this and
        // making them wait 0.35 s for it reads as a dead control.
        selection.Update(in time, FrameWith(3, 400));
        await Assert.That(selection.ActiveLevelId).IsEqualTo(space.Levels[1].Id);
    }

    [Test]
    public async Task LevelSetChanged_WithRemovedActive_FallsBackToTopMost()
    {
        MapSpace space = Two();
        LevelSelection selection = new(space);
        space.LevelSetChanged += selection.OnLevelSetChanged;

        selection.PickManually(space.Levels[0].Id);
        await Assert.That(selection.ActiveLevelId).IsEqualTo(space.Levels[0].Id);

        space.Rebuild([new FloorSlice(0, 640)]);

        await Assert.That(space.Levels).HasCount().EqualTo(1);
        await Assert.That(selection.ActiveLevelId).IsEqualTo(space.Levels[^1].Id);
    }

    [Test]
    public async Task Update_AllocatesNothing_InTheSteadyState()
    {
        MapSpace space = Two();
        LevelSelection selection = new(space)
        {
            FollowedSlot = 3
        };

        SceneTime time = Time();
        Scene2DFrame frame = FrameWith(3, -300);
        for (int i = 0; i < 64; i++)
        {
            selection.Update(in time, frame);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            selection.Update(in time, frame);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 LevelSelection.Update: {delta} bytes");
        await Assert.That(delta).IsEqualTo(0);
    }

    private static SceneTime Time() => new(0, 0, 0, Dt, false);

    private static Scene2DFrame FrameWith(int slot, float z) => new()
    {
        Markers = [new PlayerMarker(slot, 2, 0, 0, z, 0, RingState.Team, 1, "P", true)]
    };

    private static MapSpace Two()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-640, 0), new FloorSlice(0, 640)]);
        return space;
    }
}
