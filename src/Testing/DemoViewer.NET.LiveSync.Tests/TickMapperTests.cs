#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="TickMapper" /> unit battery. Frame-clock facts under
///     test: pre-game frames carry a large NEGATIVE sentinel <see cref="DemoFrame.ServerTick" />
///     (clamped to 0 on the CS2 side), gameplay ticks start at 1, several frames can share one
///     tick (boundary = first frame of the tick), and the <c>TickOffset</c> shim applies
///     symmetrically in both directions.
/// </summary>
[Category("Unit")]
public class TickMapperTests
{
    private const int Sentinel = -999_999;

    // index:      0         1         2  3  4  5  6  7
    // ServerTick: sentinel  sentinel  1  1  2  4  4  7
    private static readonly int[] _ticks = [Sentinel, Sentinel, 1, 1, 2, 4, 4, 7];

    // First frame of each distinct tick.
    private static readonly int[] _boundaries = [0, 2, 4, 5, 7];

    private static IReadOnlyList<DemoFrame> BuildFrames() =>
        [.. _ticks.Select((tick, index) => Frame(index, tick))];

    private static DemoFrame Frame(int index, int tick) => new()
    {
        Command = "dem_packet",
        FrameNumber = index,
        HeaderLength = 0,
        IsCompressed = false,
        RawLength = 0,
        RawStart = 0,
        ServerTick = tick
    };

    [Test]
    public async Task Cs2DemoTick_ClampsPreGameSentinelToZero()
    {
        TickMapper mapper = new(BuildFrames(), _boundaries);

        await Assert.That(mapper.Cs2DemoTick(0)).IsEqualTo(0);
        await Assert.That(mapper.Cs2DemoTick(1)).IsEqualTo(0);
        await Assert.That(mapper.Cs2DemoTick(2)).IsEqualTo(1);
        await Assert.That(mapper.Cs2DemoTick(7)).IsEqualTo(7);
    }

    [Test]
    public async Task Cs2DemoTick_AppliesTickOffset()
    {
        TickMapper mapper = new(BuildFrames(), _boundaries, 100);

        await Assert.That(mapper.Cs2DemoTick(0)).IsEqualTo(100); // clamp first, then offset
        await Assert.That(mapper.Cs2DemoTick(5)).IsEqualTo(104);
    }

    [Test]
    public async Task FrameIndexOf_ExactTick_ReturnsFirstFrameOfTick()
    {
        TickMapper mapper = new(BuildFrames(), _boundaries);

        await Assert.That(mapper.FrameIndexOf(1)).IsEqualTo(2);
        await Assert.That(mapper.FrameIndexOf(2)).IsEqualTo(4);
        await Assert.That(mapper.FrameIndexOf(4)).IsEqualTo(5);
        await Assert.That(mapper.FrameIndexOf(7)).IsEqualTo(7);
    }

    [Test]
    public async Task FrameIndexOf_TickGap_ReturnsLastBoundaryAtOrBefore()
    {
        TickMapper mapper = new(BuildFrames(), _boundaries);

        // Tick 3 does not exist in the demo — the state visible at tick 3 is tick 2's.
        await Assert.That(mapper.FrameIndexOf(3)).IsEqualTo(4);
        await Assert.That(mapper.FrameIndexOf(5)).IsEqualTo(5);
        await Assert.That(mapper.FrameIndexOf(6)).IsEqualTo(5);
    }

    [Test]
    public async Task FrameIndexOf_ClampsBelowFirstAndPastEnd()
    {
        TickMapper mapper = new(BuildFrames(), _boundaries);

        await Assert.That(mapper.FrameIndexOf(0)).IsEqualTo(0); // only the sentinel boundary is <= 0
        await Assert.That(mapper.FrameIndexOf(int.MinValue + 1_000_000)).IsEqualTo(0);
        await Assert.That(mapper.FrameIndexOf(1_000_000)).IsEqualTo(7);
    }

    [Test]
    public async Task FrameIndexOf_WithOffset_InvertsSymmetrically()
    {
        TickMapper mapper = new(BuildFrames(), _boundaries, 100);

        await Assert.That(mapper.FrameIndexOf(104)).IsEqualTo(5);
        await Assert.That(mapper.FrameIndexOf(101)).IsEqualTo(2);
    }

    [Test]
    public async Task RoundTrip_GameplayFrames_LandOnTheirTickBoundary()
    {
        IReadOnlyList<DemoFrame> frames = BuildFrames();
        TickMapper mapper = new(frames, _boundaries, 7);

        for (int i = 2; i < frames.Count; i++)
        {
            int back = mapper.FrameIndexOf(mapper.Cs2DemoTick(i));
            // Round trip lands on the FIRST frame of frame i's tick.
            await Assert.That(frames[back].ServerTick).IsEqualTo(frames[i].ServerTick);
            await Assert.That(back).IsEqualTo(_boundaries.First(b => frames[b].ServerTick == frames[i].ServerTick));
        }
    }

    [Test]
    public async Task FrameIndexOf_NoBoundaries_ReturnsZero()
    {
        TickMapper mapper = new([], []);

        await Assert.That(mapper.FrameIndexOf(42)).IsEqualTo(0);
    }

    [Test]
    public void Cs2DemoTick_OutOfRange_Throws()
    {
        TickMapper mapper = new(BuildFrames(), _boundaries);

        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Cs2DemoTick(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Cs2DemoTick(_ticks.Length));
    }
}
