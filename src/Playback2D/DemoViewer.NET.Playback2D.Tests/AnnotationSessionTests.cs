#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The session's two authoring maps: button → ink, and <see cref="EnvelopeMode" /> → envelope.
///     <para>
///         The envelope half is D2 §2.4's regression. <c>Custom</c> shipped as a synonym for
///         <c>Always</c>: <c>NewElementEnvelope</c> was declared, read once, and never assigned, so the
///         mode changed one persisted string and nothing a user could see.
///     </para>
/// </summary>
public class AnnotationSessionTests
{
    [Test]
    [Arguments(ToolPointerButton.Left)]
    [Arguments(ToolPointerButton.Middle)]
    [Arguments(ToolPointerButton.None)]
    public async Task StyleFor_EverythingButRight_IsThePrimaryPen(ToolPointerButton button)
    {
        AnnotationSession session = Session();

        await Assert.That(session.StyleFor(button)).IsEqualTo(session.Style);
    }

    [Test]
    public async Task StyleFor_Right_IsTheSecondaryPen()
    {
        AnnotationSession session = Session();

        await Assert.That(session.StyleFor(ToolPointerButton.Right)).IsEqualTo(session.SecondaryStyle);
        await Assert.That(session.SecondaryStyle.ColorArgb)
            .IsNotEqualTo(AnnotationStyle.Default.ColorArgb)
            .Because("two pens the same colour would make the right button's whole point invisible");
    }

    [Test]
    public async Task Always_IsStatic()
    {
        AnnotationSession session = Session();
        session.DefaultVisibility = EnvelopeMode.Always;

        await Assert.That(session.EnvelopeForNewElement(9000)).IsEqualTo(TimeEnvelope.Static);
    }

    [Test]
    public async Task Fade_PinsToThePlayhead()
    {
        AnnotationSession session = Session();
        session.DefaultVisibility = EnvelopeMode.Fade;
        session.HoldTicks = 100;

        TimeEnvelope envelope = session.EnvelopeForNewElement(9000);

        await Assert.That(envelope.FromTick).IsEqualTo(9000);
        await Assert.That(envelope.UntilTick).IsEqualTo(9100);
    }

    /// <summary>
    ///     D7 §3: RealTime's ELEMENT-level window is Fade's, deliberately. Each section is then rendered
    ///     through this same trapezoid shifted by the offset it was drawn at, which is what lets
    ///     <c>HoldTicks</c> keep its meaning per section — hold longer than the draw and the whole stroke
    ///     stands before dissolving from the start; shorter, and it chases its own tail.
    /// </summary>
    [Test]
    public async Task RealTime_PinsToThePlayhead_ExactlyAsFadeDoes()
    {
        AnnotationSession fade = Session();
        fade.DefaultVisibility = EnvelopeMode.Fade;
        fade.HoldTicks = 100;

        AnnotationSession realTime = Session();
        realTime.DefaultVisibility = EnvelopeMode.RealTime;
        realTime.HoldTicks = 100;

        await Assert.That(realTime.EnvelopeForNewElement(9000))
            .IsEqualTo(fade.EnvelopeForNewElement(9000));
        await Assert.That(realTime.EnvelopeForNewElement(9000)).IsNotEqualTo(TimeEnvelope.Static)
            .Because("an unhandled mode falls through to Static, which would silently pin nothing");
    }

    /// <summary>
    ///     D8 §1: the rate is a property of the loaded parse, so a session that has met no demo assumes
    ///     the shipped 64 and REFUSES anything that cannot be a divisor. <c>ClockIdentity.Unknown</c>
    ///     carries 0, and a zero rate turns every duration in the toolbar into an infinity.
    /// </summary>
    [Test]
    [Arguments(0, AnnotationSession.DefaultTicksPerSecond)]
    [Arguments(-8, AnnotationSession.DefaultTicksPerSecond)]
    [Arguments(128, 128)]
    [Arguments(102, 102)]
    public async Task TicksPerSecond_TakesARealRate_AndRefusesTheRest(int assigned, int expected)
    {
        AnnotationSession session = Session();

        await Assert.That(session.TicksPerSecond).IsEqualTo(AnnotationSession.DefaultTicksPerSecond);

        session.TicksPerSecond = assigned;
        await Assert.That(session.TicksPerSecond).IsEqualTo(expected);
    }

    /// <summary>
    ///     D8 §3: Round takes the resolver's window verbatim and keeps the session's own ramps — the two
    ///     controls the mode still offers.
    /// </summary>
    [Test]
    public async Task Round_TakesTheResolversWindow_AndTheSessionsRamps()
    {
        AnnotationSession session = Session();
        session.DefaultVisibility = EnvelopeMode.Round;
        session.FadeInTicks = 6;
        session.FadeOutTicks = 18;
        session.HoldTicks = 320;
        session.RoundWindowResolver = _ => (5000, 8000);

        TimeEnvelope envelope = session.EnvelopeForNewElement(6400);

        await Assert.That(envelope.FromTick).IsEqualTo(5000);
        await Assert.That(envelope.UntilTick).IsEqualTo(8000);
        await Assert.That(envelope.FadeInTicks).IsEqualTo(6);
        await Assert.That(envelope.FadeOutTicks).IsEqualTo(18);
        await Assert.That(envelope.OpacityAt(8000)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(8019)).IsEqualTo(0.0)
            .Because("the window is the round; HoldTicks has no say in where it ends");
    }

    /// <summary>
    ///     The LAST round has no following freeze-end, so its window is open at the far end — which is
    ///     already what a null <see cref="TimeEnvelope.UntilTick" /> means, and costs no last-tick source.
    /// </summary>
    [Test]
    public async Task Round_LastRound_IsOpenAtTheFarEnd()
    {
        AnnotationSession session = Session();
        session.DefaultVisibility = EnvelopeMode.Round;
        session.RoundWindowResolver = _ => (5000, null);

        TimeEnvelope envelope = session.EnvelopeForNewElement(6400);

        await Assert.That(envelope.FromTick).IsEqualTo(5000);
        await Assert.That(envelope.UntilTick).IsNull();
        await Assert.That(envelope.OpacityAt(999_999)).IsEqualTo(1.0)
            .Because("the last round runs to the end of the demo, and so does its annotation");
    }

    /// <summary>
    ///     A demo with no rounds — a warmup clip, a partial parse, a source that carries no
    ///     <c>round_freeze_end</c> — degrades to Fade's pinned trapezoid. A mode that produced an empty or
    ///     inverted window there would draw nothing, which is worse than drawing the wrong thing.
    /// </summary>
    [Test]
    public async Task Round_WithNothingToResolve_FallsBackToThePinnedTrapezoid()
    {
        AnnotationSession noResolver = Session();
        noResolver.DefaultVisibility = EnvelopeMode.Round;
        noResolver.HoldTicks = 100;

        AnnotationSession declines = Session();
        declines.DefaultVisibility = EnvelopeMode.Round;
        declines.HoldTicks = 100;
        declines.RoundWindowResolver = _ => null;

        AnnotationSession fade = Session();
        fade.DefaultVisibility = EnvelopeMode.Fade;
        fade.HoldTicks = 100;

        TimeEnvelope expected = fade.EnvelopeForNewElement(9000);

        await Assert.That(noResolver.EnvelopeForNewElement(9000)).IsEqualTo(expected)
            .Because("no resolver at all is a Core-only session, and it still has to produce ink");
        await Assert.That(declines.EnvelopeForNewElement(9000)).IsEqualTo(expected)
            .Because("a demo without round_freeze_end is the same answer arriving one layer down");
        await Assert.That(expected.UntilTick).IsEqualTo(9100)
            .Because("the fallback must be a real window, not an empty or inverted one");
    }

    /// <summary>A window handed back inverted collapses; it never inverts the envelope.</summary>
    [Test]
    public async Task Round_InvertedWindow_CollapsesToZeroLength()
    {
        AnnotationSession session = Session();
        session.DefaultVisibility = EnvelopeMode.Round;
        session.RoundWindowResolver = _ => (8000, 5000);

        TimeEnvelope envelope = session.EnvelopeForNewElement(6400);

        await Assert.That(envelope.FromTick).IsEqualTo(8000);
        await Assert.That(envelope.UntilTick).IsEqualTo(8000);
    }

    /// <summary>D2 §2.4's exit criterion: Custom is no longer a second spelling of Always.</summary>
    [Test]
    public async Task Custom_WithAWindow_IsNotStatic()
    {
        AnnotationSession session = Session();
        session.DefaultVisibility = EnvelopeMode.Custom;
        session.FadeInTicks = 4;
        session.FadeOutTicks = 12;
        session.SetCustomWindow(500, 800);

        TimeEnvelope envelope = session.EnvelopeForNewElement(9000);

        await Assert.That(envelope).IsNotEqualTo(TimeEnvelope.Static);
        await Assert.That(envelope.FromTick).IsEqualTo(500);
        await Assert.That(envelope.UntilTick).IsEqualTo(800);
        await Assert.That(envelope.FadeInTicks).IsEqualTo(4);
        await Assert.That(envelope.FadeOutTicks).IsEqualTo(12);
        await Assert.That(envelope.OpacityAt(650)).IsEqualTo(1.0);
        await Assert.That(envelope.OpacityAt(812)).IsEqualTo(0.0);
    }

    /// <summary>
    ///     The one behavioural difference from Fade, and the reason Custom exists: absolute ticks. Two
    ///     strokes drawn ten seconds apart land in the SAME window.
    /// </summary>
    [Test]
    public async Task Custom_IgnoresThePlayhead()
    {
        AnnotationSession session = Session();
        session.DefaultVisibility = EnvelopeMode.Custom;
        session.SetCustomWindow(500, 800);

        await Assert.That(session.EnvelopeForNewElement(0))
            .IsEqualTo(session.EnvelopeForNewElement(64_000));
    }

    /// <summary>A window typed backwards collapses; it never throws and never inverts.</summary>
    [Test]
    public async Task Custom_InvertedWindow_CollapsesToZeroLength()
    {
        AnnotationSession session = Session();
        session.SetCustomWindow(800, 500);

        await Assert.That(session.NewElementEnvelope.FromTick).IsEqualTo(800);
        await Assert.That(session.NewElementEnvelope.UntilTick).IsEqualTo(800);
    }

    /// <summary>
    ///     The ramps are shared with Fade, so re-composing after a ramp change is what keeps the two
    ///     modes from disagreeing about the same two spin boxes.
    /// </summary>
    [Test]
    public async Task Custom_TakesItsRampsFromTheSessionAtCompositionTime()
    {
        AnnotationSession session = Session();
        session.FadeInTicks = 30;
        session.FadeOutTicks = 40;
        session.SetCustomWindow(100, 200);

        await Assert.That(session.NewElementEnvelope.FadeInTicks).IsEqualTo(30);
        await Assert.That(session.NewElementEnvelope.FadeOutTicks).IsEqualTo(40);
    }

    private static AnnotationSession Session() => new(new AnnotationDocument());
}
