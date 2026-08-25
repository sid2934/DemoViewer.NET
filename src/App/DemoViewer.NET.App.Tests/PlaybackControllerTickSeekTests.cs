#region

using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="PlaybackController.FrameIndexAtTick" /> — the O(log n) lower_bound that replaced
///     <c>SeekToTick</c>'s linear scan. The removed scan IS the oracle these assert against: the timeline
///     places every tick-stamped marker through this seam, so an off-by-one here silently mis-places
///     hundreds of markers rather than failing loudly.
/// </summary>
public class PlaybackControllerTickSeekTests
{
    [Test]
    public async Task FrameIndexAtTick_EmptyController_ReturnsMinusOne()
    {
        using PlaybackController controller = new();
        await Assert.That(controller.FrameIndexAtTick(0)).IsEqualTo(-1);
        await Assert.That(controller.FrameIndexAtTick(1000)).IsEqualTo(-1);
    }

    [Test]
    public async Task FrameIndexAtTick_ExactTick_ReturnsFirstFrameOfThatTick()
    {
        using PlaybackController controller = Loaded(Frames(0, 0, 64, 64, 64, 128));

        // Three frames carry tick 64; lower_bound must land on the FIRST of them.
        await Assert.That(controller.FrameIndexAtTick(64)).IsEqualTo(2);
        await Assert.That(controller.FrameIndexAtTick(128)).IsEqualTo(5);
    }

    [Test]
    public async Task FrameIndexAtTick_BetweenTicks_ReturnsNextFrame()
    {
        using PlaybackController controller = Loaded(Frames(0, 64, 128, 192));
        await Assert.That(controller.FrameIndexAtTick(65)).IsEqualTo(2);
        await Assert.That(controller.FrameIndexAtTick(191)).IsEqualTo(3);
    }

    [Test]
    public async Task FrameIndexAtTick_BeyondLastTick_ReturnsMinusOne()
    {
        using PlaybackController controller = Loaded(Frames(0, 64, 128));

        // -1, never a clamped index: a marker past the end of the frame list has nowhere to draw and
        // must be DROPPED, not stacked on the last frame.
        await Assert.That(controller.FrameIndexAtTick(129)).IsEqualTo(-1);
    }

    [Test]
    public async Task FrameIndexAtTick_BeforeFirstTick_ReturnsZero()
    {
        using PlaybackController controller = Loaded(Frames(100, 164, 228));
        await Assert.That(controller.FrameIndexAtTick(0)).IsEqualTo(0);
        await Assert.That(controller.FrameIndexAtTick(-5)).IsEqualTo(0);
    }

    [Test]
    public async Task SeekToTick_MovesToSameFrameAsLinearScan()
    {
        // 5 000 frames with repeating ticks — the shape a real demo has (several frames per tick).
        int[] ticks = new int[5000];
        for (int i = 0; i < ticks.Length; i++)
        {
            ticks[i] = i / 3 * 2;
        }

        using PlaybackController controller = Loaded(Frames(ticks));

        int lastTick = ticks[^1];
        for (int tick = -2; tick <= lastTick + 2; tick++)
        {
            await Assert.That(controller.FrameIndexAtTick(tick)).IsEqualTo(LinearScan(ticks, tick));
        }
    }

    // The exact body of the deleted linear scan, kept here as the oracle.
    private static int LinearScan(int[] ticks, int tick)
    {
        for (int i = 0; i < ticks.Length; i++)
        {
            if (ticks[i] >= tick)
            {
                return i;
            }
        }

        return -1;
    }

    private static PlaybackController Loaded(IReadOnlyList<DemoFrame> frames)
    {
        PlaybackController controller = new();
        controller.LoadDemo(frames, 64);
        return controller;
    }

    private static DemoFrame[] Frames(params int[] ticks)
    {
        DemoFrame[] frames = new DemoFrame[ticks.Length];
        for (int i = 0; i < ticks.Length; i++)
        {
            frames[i] = new DemoFrame
            {
                Command = "DEM_Packet",
                FrameNumber = i,
                ServerTick = ticks[i],
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false
            };
        }

        return frames;
    }
}
