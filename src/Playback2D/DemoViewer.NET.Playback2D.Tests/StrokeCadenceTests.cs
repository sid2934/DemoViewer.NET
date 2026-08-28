#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     A <see cref="EnvelopeMode.RealTime" /> stroke records WHEN each sample was drawn as a sparse run
///     table, and commits it on release. The encoding is specced in
///     <c>docs/playback2d-v2/plans/D7-realtime-ink.md</c>.
///     <para>
///         Every case here drives <see cref="FakeToolServices.NowMilliseconds" /> by hand while
///         <see cref="FakeToolServices.CurrentTick" /> stays wherever it was put. That separation is the
///         feature, not a testing convenience: the playhead is frozen for the length of a gesture drawn
///         on a paused demo, so a cadence read from it would be instantaneous.
///     </para>
/// </summary>
public class StrokeCadenceTests
{
    /// <summary>
    ///     One steady drag: two boundaries (the first sample and the last) and a duration that is the
    ///     wall-clock the hand actually spent, converted once.
    /// </summary>
    [Test]
    public async Task SteadyCadence_IsAMonotonicTable_WhoseDurationIsTheElapsedTime()
    {
        Hand h = new();

        h.Press();
        for (int i = 0; i < 60; i++)
        {
            h.Step(16);
        }

        h.Lift(16);

        AnnotationElement element = h.Committed;
        StrokeTiming timing = element.Timing!;
        Console.WriteLine($"[cadence] steady: {element.Points.Count} samples, "
                          + $"k={timing.Runs.Count}, {timing.DurationTicks} ticks");

        await Assert.That(timing).IsNotNull();
        await Assert.That(timing.Runs[0].SampleIndex).IsEqualTo(0);
        await Assert.That(timing.Runs[0].TickOffset).IsEqualTo(0)
            .Because("offsets are re-based at the press, whatever origin the host clock counts from");
        await Assert.That(timing.Runs[^1].SampleIndex).IsEqualTo(element.Points.Count - 1)
            .Because("the last sample is always a boundary — it is what DurationTicks means");
        await Assert.That(timing.DurationTicks).IsEqualTo(62)
            .Because("61 samples 16 ms apart is 976 ms, and 976 ms at 64 tick is 62.46 ticks");

        await AssertMonotonic(timing);
    }

    /// <summary>
    ///     <b>The whole feature.</b> An author who stops to think mid-stroke gets a boundary at the stop,
    ///     and the replayed head stalls there instead of gliding through it.
    ///     <para>
    ///         The stall is the assertion that matters. A run table that recorded only the total
    ///         duration would pass "there are boundaries" and still interpolate straight across the
    ///         pause, and that is invisible until you ask what
    ///         <see cref="StrokeTiming.RevealedCount" /> does tick by tick.
    ///     </para>
    /// </summary>
    [Test]
    public async Task APauseMidStroke_BecomesABoundary_AndTheRevealStallsAcrossIt()
    {
        Hand h = new();

        h.Press();
        for (int i = 0; i < 10; i++)
        {
            h.Step(50); // samples 1..10, at 50..500 ms
        }

        h.Step(600); // sample 11 at 1100 ms; the author stopped to think

        for (int i = 0; i < 10; i++)
        {
            h.Step(50); // samples 12..21
        }

        h.Lift(50); // sample 22, at 1650 ms

        AnnotationElement element = h.Committed;
        StrokeTiming timing = element.Timing!;
        int samples = element.Points.Count;

        Console.WriteLine($"[cadence] pause: {samples} samples, k={timing.Runs.Count}, "
                          + $"runs={Describe(timing)}");

        // The pause is the widest gap between two adjacent boundaries, read off the table rather than
        // assumed at a sample index, so this still means "the stop" whatever the spacing filter kept.
        (int intoThePause, int outOfIt) = WidestGap(timing);
        await Assert.That(outOfIt - intoThePause).IsGreaterThanOrEqualTo(36)
            .Because("600 ms of thinking is 38 ticks; a table that ran a single linear fit through the "
                     + "whole stroke would place those two neighbours about 3 ticks apart");

        // The stall itself, tick by tick over the whole pause. The reveal is monotone AND continuous in
        // the tick, so this is what a 30 fps export samples too, whichever ticks it happens to hit.
        int stalled = timing.RevealedCount(intoThePause, samples);
        for (int tick = intoThePause; tick < outOfIt; tick++)
        {
            await Assert.That(timing.RevealedCount(tick, samples)).IsEqualTo(stalled)
                .Because("the hand was not moving; nor may the head that is replaying it");
        }

        await Assert.That(timing.RevealedCount(outOfIt, samples)).IsGreaterThan(stalled)
            .Because("...and it must start again the moment the hand did");
        await Assert.That(timing.RevealedCount(intoThePause / 2, samples)).IsLessThan(stalled)
            .Because("...having advanced all the way to the pause before it");
    }

    /// <summary>
    ///     The encoding stated as a budget: a boundary marks a change of speed, so a realistic
    ///     telestration carries a handful of them and not one per point. Smooth variation inside a
    ///     continuous motion is deliberately NOT a boundary. It is invisible at 64 Hz through a fading
    ///     tail, and paying 300 near-identical deltas to record it is what the sparse table exists to
    ///     avoid.
    /// </summary>
    [Test]
    public async Task ARealisticTelestration_StaysInTheBoundaryBudget()
    {
        Hand h = new();

        h.Press();
        Sweep(h, 90, 9, 2); // circle the spot, hand speeding up and slowing twice
        h.Step(620); // ...stop and think
        Sweep(h, 70, 7, 1); // run a line out
        h.Step(380); // ...stop
        Sweep(h, 60, 11, 1); // hook it round
        h.Step(500); // ...stop
        Sweep(h, 79, 8, 2); // and the tail
        h.Lift(8);

        AnnotationElement element = h.Committed;
        StrokeTiming timing = element.Timing!;
        int k = timing.Runs.Count;

        Console.WriteLine($"[cadence] telestration: {element.Points.Count} samples, k={k}, "
                          + $"{timing.DurationTicks} ticks, runs={Describe(timing)}");

        await Assert.That(k).IsGreaterThanOrEqualTo(6)
            .Because("three pauses is §2's own worked example: two entries plus a pair each, so eight");
        await Assert.That(k).IsLessThanOrEqualTo(16)
            .Because("smooth speed variation inside one continuous motion must NOT split a run");
        await Assert.That(k * 10).IsLessThan(element.Points.Count)
            .Because("a stamp per point is the +26 % encoding this table was chosen over");

        await AssertMonotonic(timing);
    }

    /// <summary>
    ///     Why the clock is on <c>IToolServices</c> at all: most annotation happens on a PAUSED demo,
    ///     where <c>CurrentTick</c> does not move for the length of the gesture. A cadence anchored to it
    ///     collapses to nothing.
    /// </summary>
    [Test]
    public async Task AStrokeDrawnWhileThePlayheadIsFrozen_StillRecordsRealCadence()
    {
        Hand h = new();
        h.Services.CurrentTick = 4096; // paused: nothing below moves it

        h.Press();
        for (int i = 0; i < 25; i++)
        {
            h.Step(40);
        }

        h.Lift(40);

        AnnotationElement element = h.Committed;
        StrokeTiming timing = element.Timing!;
        Console.WriteLine($"[cadence] paused: tick={h.Services.CurrentTick}, "
                          + $"clock={h.Services.NowMilliseconds} ms, {timing.DurationTicks} ticks");

        await Assert.That(h.Services.CurrentTick).IsEqualTo(4096)
            .Because("the demo was paused for the whole gesture — that is the premise");
        await Assert.That(element.Time.FromTick).IsEqualTo(4096)
            .Because("RealTime pins to the playhead exactly as Fade does; §3 shifts each section from it");
        await Assert.That(timing.DurationTicks).IsEqualTo(67)
            .Because("26 samples 40 ms apart is 1040 ms of authoring, or 66.6 ticks — every one of "
                     + "which came from the authoring clock, because the playhead supplied none");
    }

    /// <summary>
    ///     The same hand, the same milliseconds, on a 128-tick parse: the cadence is expressed in the
    ///     DEMO's ticks, so it carries twice as many of them.
    ///     <para>
    ///         Converting through a hard-coded 64 replays a stroke drawn on a 128-tick demo at HALF the
    ///         speed it was drawn at: the run table says "one second" where the renderer counts two.
    ///         Both assertions below fail on the literal.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ANon64TickParse_ConvertsTheCadenceAtTheDemosOwnRate()
    {
        Hand at64 = new();
        Hand at128 = new(ticksPerSecond: 128);

        foreach (Hand h in new[]
                 {
                     at64, at128
                 })
        {
            h.Press();
            for (int i = 0; i < 24; i++)
            {
                h.Step(40); // 25 samples, 40 ms apart
            }

            h.Lift(40); // ...and the release at 1000 ms
        }

        StrokeTiming slow = at64.Committed.Timing!;
        StrokeTiming fast = at128.Committed.Timing!;
        Console.WriteLine($"[cadence] rate: 64 -> {slow.DurationTicks} ticks, "
                          + $"128 -> {fast.DurationTicks} ticks");

        await Assert.That(slow.DurationTicks).IsEqualTo(64);
        await Assert.That(fast.DurationTicks).IsEqualTo(128)
            .Because("1000 ms is 128 ticks on a 128-tick parse; converted at a literal 64 it would say "
                     + "64, and the replay would crawl at half the speed the hand actually moved");

        // Ticks back into seconds through each session's own rate: the SAME second of authoring. That is
        // the invariant a rate-blind conversion breaks.
        await Assert.That(fast.DurationTicks / 128.0).IsEqualTo(slow.DurationTicks / 64.0);
    }

    /// <summary>
    ///     A coalesced batch carries no times of its own (Avalonia stamps the EVENT), so the samples in
    ///     one are spread across the interval since the previous event.
    ///     <para>
    ///         Read through the reveal, the only thing that consumes the offsets: a quarter of the
    ///         batch's 1000 ms has to have revealed a quarter of it. Stamped at the event time instead,
    ///         all four would sit at the far end and 16 ticks in would still show one sample.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CoalescedSamples_GetInterpolatedOffsets_InOrder_WithNoDuplicates()
    {
        Hand h = new();

        h.Press();
        h.StepCoalesced(1000, 3); // three sub-frame samples plus the primary, across one 1000 ms event
        for (int i = 0; i < 4; i++)
        {
            h.Step(16);
        }

        h.Lift(16);

        StrokeTiming timing = h.Committed.Timing!;
        int samples = h.Committed.Points.Count;
        Console.WriteLine($"[cadence] coalesced: runs={Describe(timing)}, revealed="
                          + $"{timing.RevealedCount(16, samples)},{timing.RevealedCount(32, samples)},"
                          + $"{timing.RevealedCount(48, samples)},{timing.RevealedCount(64, samples)}");

        await Assert.That(timing.RevealedCount(16, samples)).IsEqualTo(2)
            .Because("250 ms into a 1000 ms batch is one sample past the press; stamped at the event "
                     + "instead the whole batch would land at 64 ticks and this would still be 1");
        await Assert.That(timing.RevealedCount(32, samples)).IsEqualTo(3);
        await Assert.That(timing.RevealedCount(48, samples)).IsEqualTo(4);
        await Assert.That(timing.RevealedCount(64, samples)).IsEqualTo(5);

        await AssertMonotonic(timing);
    }

    /// <summary>
    ///     A tap has no cadence to record. It still commits, as a dot, and the timing it carries is the
    ///     "everything at once" one, not a table with a single useless entry in it.
    /// </summary>
    [Test]
    public async Task ATapWithNoMovement_CommitsTheInstantTiming()
    {
        Hand h = new();

        h.Press();
        h.Lift(0, false);

        AnnotationElement element = h.Committed;
        await Assert.That(element.Points.Count).IsEqualTo(2);
        await Assert.That(element.Timing).IsEqualTo(StrokeTiming.Instant);
        await Assert.That(element.Timing!.RevealedCount(0, 2)).IsEqualTo(2)
            .Because("a dot is drawn whole from its first tick");
    }

    /// <summary>
    ///     A clock that steps backwards mid-gesture, the case the monotonic contract on
    ///     <c>IToolServices.NowMilliseconds</c> exists to forbid, must degrade to a repeated instant.
    ///     A table with a negative offset in it is the one shape <see cref="StrokeTiming" />'s readers
    ///     assume away.
    /// </summary>
    [Test]
    public async Task AClockThatWentBackwards_DoesNotInvertTheTable()
    {
        Hand h = new();

        h.Press();
        h.Step(200);
        h.Step(200);
        h.Services.NowMilliseconds -= 5_000; // an NTP correction, or a host that got this wrong
        h.Step(0);
        h.Step(200);
        h.Lift(200);

        StrokeTiming timing = h.Committed.Timing!;
        Console.WriteLine($"[cadence] backwards: runs={Describe(timing)}");

        await AssertMonotonic(timing);
    }

    private static async Task AssertMonotonic(StrokeTiming timing)
    {
        for (int i = 1; i < timing.Runs.Count; i++)
        {
            await Assert.That(timing.Runs[i].SampleIndex).IsGreaterThan(timing.Runs[i - 1].SampleIndex)
                .Because("the table is ordered by sample index, and never repeats one");
            await Assert.That(timing.Runs[i].TickOffset)
                .IsGreaterThanOrEqualTo(timing.Runs[i - 1].TickOffset)
                .Because("time does not run backwards inside a stroke");
        }

        await Assert.That(timing.Runs[^1].TickOffset).IsEqualTo(timing.DurationTicks);
    }

    // The adjacent boundary pair furthest apart in time: the stop the hand made, as the table saw it.
    private static (int Before, int After) WidestGap(StrokeTiming timing)
    {
        int widest = 1;
        for (int i = 2; i < timing.Runs.Count; i++)
        {
            if (timing.Runs[i].TickOffset - timing.Runs[i - 1].TickOffset
                > timing.Runs[widest].TickOffset - timing.Runs[widest - 1].TickOffset)
            {
                widest = i;
            }
        }

        return (timing.Runs[widest - 1].TickOffset, timing.Runs[widest].TickOffset);
    }

    // A hand whose speed rises and falls smoothly, `cycles` times over `samples` strokes of the pen.
    // No pauses: this is the variation that must NOT produce boundaries.
    private static void Sweep(Hand h, int samples, double baseGapMs, int cycles)
    {
        for (int i = 1; i <= samples; i++)
        {
            double phase = 2 * Math.PI * cycles * i / samples;
            h.Step((long)Math.Max(1, baseGapMs * (1 + 0.5 * Math.Sin(phase))));
        }
    }

    private static string Describe(StrokeTiming timing) =>
        string.Join(" ", timing.Runs.Select(r =>
            string.Create(CultureInfo.InvariantCulture, $"{r.SampleIndex}@{r.TickOffset}")));

    /// <summary>
    ///     A hand drawing a straight line to the right, one <see cref="StepWorld" /> at a time. Well
    ///     clear of the spacing filter, so every sample driven here is a sample the element keeps and the
    ///     boundary indices below mean what they say.
    /// </summary>
    private sealed class Hand
    {
        private const float StepWorld = 8f;

        private float _x;

        public Hand(EnvelopeMode visibility = EnvelopeMode.RealTime,
            int ticksPerSecond = AnnotationSession.DefaultTicksPerSecond)
        {
            Pane = AnnotationFakes.Pane(600, 400);
            Document = new AnnotationDocument();
            Session = new AnnotationSession(Document)
            {
                DefaultVisibility = visibility,
                TicksPerSecond = ticksPerSecond
            };
            Services = new FakeToolServices(Session, Pane);
            Tool = new DrawTool();
        }

        public LevelPane Pane { get; }

        public AnnotationDocument Document { get; }

        public AnnotationSession Session { get; }

        public FakeToolServices Services { get; }

        public DrawTool Tool { get; }

        public AnnotationElement Committed => Document.Elements[0];

        public void Press() => Tool.OnPressed(AnnotationFakes.Press(Pane, new SKPoint(_x, 0)), Services);

        /// <summary>Waits, then delivers one pointer move carrying one sample.</summary>
        /// <param name="ms">How long the hand took to get here.</param>
        public void Step(long ms)
        {
            Services.Advance(ms);
            _x += StepWorld;
            Tool.OnMoved(AnnotationFakes.Press(Pane, new SKPoint(_x, 0)), Services);
        }

        /// <summary>Waits, then delivers one pointer move carrying a coalesced batch plus its primary.</summary>
        /// <param name="ms">How long the whole batch spans.</param>
        /// <param name="count">How many sub-frame samples the batch carries.</param>
        public void StepCoalesced(long ms, int count)
        {
            Services.Advance(ms);

            InkPoint[] batch = new InkPoint[count];
            for (int i = 0; i < count; i++)
            {
                _x += StepWorld;
                batch[i] = new InkPoint(_x, 0, 0.5f);
            }

            _x += StepWorld;
            Tool.OnMoved(AnnotationFakes.Press(Pane, new SKPoint(_x, 0), intermediate: batch), Services);
        }

        /// <summary>Waits, then releases. The release is what commits the element.</summary>
        /// <param name="ms">How long the hand took to get here.</param>
        /// <param name="move">False to release where the press landed, i.e. a tap.</param>
        public void Lift(long ms, bool move = true)
        {
            Services.Advance(ms);
            if (move)
            {
                _x += StepWorld;
            }

            Tool.OnReleased(AnnotationFakes.Press(Pane, new SKPoint(_x, 0)), Services);
        }
    }
}
