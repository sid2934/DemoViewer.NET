#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The Kinovea-style visibility trapezoid. The shape is plan decision D5: the ramps sit OUTSIDE the
///     window, which is the only arrangement in which <c>default</c> is a constant 1.0, and design §5.4
///     requires <c>TimeEnvelope.Static == default</c>.
/// </summary>
public class TimeEnvelopeTests
{
    [Test]
    public async Task Static_IsAlwaysFullyOpaque()
    {
        await Assert.That(TimeEnvelope.Static.OpacityAt(int.MinValue)).IsEqualTo(1.0);
        await Assert.That(TimeEnvelope.Static.OpacityAt(0)).IsEqualTo(1.0);
        await Assert.That(TimeEnvelope.Static.OpacityAt(int.MaxValue)).IsEqualTo(1.0);
        await Assert.That(TimeEnvelope.Static.IsAnchored).IsFalse();
    }

    [Test]
    public async Task Default_EqualsStatic()
    {
        TimeEnvelope fresh = default;
        await Assert.That(fresh).IsEqualTo(TimeEnvelope.Static);
    }

    /// <summary>
    ///     The whole of D5 in one case: full opacity across the window, a lead-in BEFORE it and a
    ///     lead-out AFTER it, zero outside both. An inside-the-window fade would make "pin to now" open
    ///     transparent, which is the opposite of what the gesture means.
    /// </summary>
    [Test]
    public async Task Trapezoid_RampsInBeforeFrom_AndOutAfterUntil()
    {
        TimeEnvelope envelope = new(100, 200, 10, 20);

        await Assert.That(envelope.OpacityAt(89)).IsEqualTo(0.0);
        await Assert.That(envelope.OpacityAt(90)).IsEqualTo(0.0);
        await Assert.That(envelope.OpacityAt(95)).IsEqualTo(0.5);
        await Assert.That(envelope.OpacityAt(99)).IsEqualTo(0.9);
        await Assert.That(envelope.OpacityAt(100)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(200)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(210)).IsEqualTo(0.5);
        await Assert.That(envelope.OpacityAt(220)).IsEqualTo(0.0);
        await Assert.That(envelope.OpacityAt(221)).IsEqualTo(0.0);
        await Assert.That(envelope.IsAnchored).IsTrue();
    }

    [Test]
    public async Task NullFrom_IsNegativeInfinity()
    {
        TimeEnvelope envelope = new(null, 200, 0, 0);
        await Assert.That(envelope.OpacityAt(int.MinValue)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(200)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(201)).IsEqualTo(0.0);
    }

    [Test]
    public async Task NullUntil_IsPositiveInfinity()
    {
        TimeEnvelope envelope = new(100, null, 0, 0);
        await Assert.That(envelope.OpacityAt(99)).IsEqualTo(0.0);
        await Assert.That(envelope.OpacityAt(100)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(int.MaxValue)).IsEqualTo(1.0);
    }

    [Test]
    public async Task ZeroLengthWindow_StillVisibleAtFromTick()
    {
        TimeEnvelope envelope = new(500, 500, 0, 0);
        await Assert.That(envelope.OpacityAt(500)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(499)).IsEqualTo(0.0);
        await Assert.That(envelope.OpacityAt(501)).IsEqualTo(0.0);
    }

    /// <summary>
    ///     Scrub safety. The playhead moves backwards as often as forwards, and an envelope that carried
    ///     state would make a rewound frame differ from the same frame reached forwards.
    /// </summary>
    [Test]
    public async Task OpacityAt_IsPure_SameAnswerRegardlessOfCallOrder()
    {
        TimeEnvelope envelope = new(100, 200, 10, 20);

        double[] forwards = new double[60];
        for (int i = 0; i < forwards.Length; i++)
        {
            forwards[i] = envelope.OpacityAt(85 + i);
        }

        for (int i = forwards.Length - 1; i >= 0; i--)
        {
            await Assert.That(envelope.OpacityAt(85 + i)).IsEqualTo(forwards[i]);
        }
    }

    [Test]
    public async Task PinnedTo_OpensAtTheTick_AndHoldsForTheGivenSpan()
    {
        TimeEnvelope envelope = TimeEnvelope.Static.PinnedTo(1000, 320, 8, 16);

        await Assert.That(envelope.FromTick).IsEqualTo(1000);
        await Assert.That(envelope.UntilTick).IsEqualTo(1320);
        await Assert.That(envelope.OpacityAt(1000)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(1320)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(1336)).IsEqualTo(0.0);
    }

    /// <summary>
    ///     A negative tick difference must not wrap. The demo clock is an <c>int</c> and an envelope
    ///     anchored near <c>int.MaxValue</c> with a tick near <c>int.MinValue</c> would overflow a naive
    ///     subtraction into a positive number, and a fully-opaque stroke at the wrong end of the demo.
    /// </summary>
    [Test]
    public async Task ExtremeTicks_DoNotOverflowIntoVisibility()
    {
        TimeEnvelope envelope = new(int.MaxValue, int.MaxValue, 8, 8);
        await Assert.That(envelope.OpacityAt(int.MinValue)).IsEqualTo(0.0);

        TimeEnvelope trailing = new(int.MinValue, int.MinValue, 8, 8);
        await Assert.That(trailing.OpacityAt(int.MaxValue)).IsEqualTo(0.0);
    }
}
