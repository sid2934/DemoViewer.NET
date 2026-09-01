#region

using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The pure tick ↔ pixel mapping envelope drag math converts through. It is <b>not</b> the
///     timeline's layout: A1's control lays out on the frame-index axis (correction 6), and this exists
///     so an edit authored in ticks can be converted at the seam rather than in three places.
/// </summary>
public class TickAxisTests
{
    [Test]
    public async Task XOf_And_TickAt_RoundTrip()
    {
        TickAxis axis = new(1000, 2000, 500);

        await Assert.That(axis.XOf(1000)).IsEqualTo(0d).Within(1e-9);
        await Assert.That(axis.XOf(1500)).IsEqualTo(250d).Within(1e-9);
        await Assert.That(axis.XOf(2000)).IsEqualTo(500d).Within(1e-9);

        await Assert.That(axis.TickAt(0)).IsEqualTo(1000);
        await Assert.That(axis.TickAt(250)).IsEqualTo(1500);
        await Assert.That(axis.TickAt(500)).IsEqualTo(2000);
        await Assert.That(axis.TicksPerPixel).IsEqualTo(2d).Within(1e-9);
    }

    [Test]
    public async Task BothEnds_AreClamped()
    {
        TickAxis axis = new(1000, 2000, 500);

        await Assert.That(axis.XOf(-5000)).IsEqualTo(0d).Within(1e-9);
        await Assert.That(axis.XOf(9999)).IsEqualTo(500d).Within(1e-9);
        await Assert.That(axis.TickAt(-40)).IsEqualTo(1000);
        await Assert.That(axis.TickAt(4000)).IsEqualTo(2000);
    }

    /// <summary>A zero-width axis, or one whose demo has a single tick, must answer rather than divide.</summary>
    [Test]
    public async Task DegenerateAxis_IsSafe()
    {
        TickAxis zeroWidth = new(0, 100, 0);
        await Assert.That(zeroWidth.XOf(50)).IsEqualTo(0d);
        await Assert.That(zeroWidth.TickAt(10)).IsEqualTo(0);
        await Assert.That(zeroWidth.TicksPerPixel).IsEqualTo(0d);

        TickAxis oneTick = new(64, 64, 500);
        await Assert.That(oneTick.XOf(64)).IsEqualTo(0d);
        await Assert.That(oneTick.TickAt(250)).IsEqualTo(64);
    }
}
