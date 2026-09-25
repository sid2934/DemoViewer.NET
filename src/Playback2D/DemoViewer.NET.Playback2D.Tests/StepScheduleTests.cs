#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Keyframes;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The strat frame clock and its step windows (step-authoring.md §3.3, correction 7): 64 ticks per
///     second from freeze-end, one window per step in authoring order, shared ticks resolved to the later
///     step, the last window open-ended.
/// </summary>
public class StepScheduleTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Third = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Fourth = Guid.Parse("00000000-0000-0000-0000-000000000004");

    [Test]
    public async Task TheRate_IsTheAnnotationSessionsFallback()
    {
        int rate = StepSchedule.TicksPerSecond;
        int fallback = AnnotationSession.DefaultTicksPerSecond;

        await Assert.That(rate).IsEqualTo(64);
        await Assert.That(rate).IsEqualTo(fallback);
    }

    [Test]
    public async Task Windows_FollowAtSeconds_CountingDownFromTheRoundLength()
    {
        StepSchedule schedule = StepSchedule.FromRoundClock([(First, 115), (Second, 110), (Third, 102.5)], 115);

        await Assert.That(schedule.Windows.Count).IsEqualTo(3);
        await Assert.That(schedule.Windows[0]).IsEqualTo(new StepWindow(First, 0, 319));
        await Assert.That(schedule.Windows[1]).IsEqualTo(new StepWindow(Second, 320, 799));
        await Assert.That(schedule.Windows[2]).IsEqualTo(new StepWindow(Third, 800, null))
            .Because("the last window is open-ended, TimeEnvelope's 'until the end'");
        await Assert.That(schedule.LastTick).IsEqualTo(800);
    }

    [Test]
    public async Task At_FindsTheWindowOwningATick()
    {
        StepSchedule schedule = new([(First, 64), (Second, 128), (Third, 256)]);

        await Assert.That(schedule.At(63)).IsNull().Because("before the first step no window owns the tick");
        await Assert.That(schedule.At(64)!.Value.StepId).IsEqualTo(First);
        await Assert.That(schedule.At(127)!.Value.StepId).IsEqualTo(First);
        await Assert.That(schedule.At(128)!.Value.StepId).IsEqualTo(Second);
        await Assert.That(schedule.At(1_000_000)!.Value.StepId).IsEqualTo(Third);
        await Assert.That(schedule.IndexOf(Second)).IsEqualTo(1);
        await Assert.That(schedule.IndexOf(Guid.Empty)).IsEqualTo(-1);
    }

    [Test]
    public async Task SharedTicks_GiveTheEarlierStepAZeroLengthWindow_InAuthoringOrder()
    {
        StepSchedule schedule = StepSchedule.FromRoundClock(
            [(First, 115), (Second, 100), (Third, 100), (Fourth, 90)], 115);

        StepWindow second = schedule.Windows[1];
        StepWindow third = schedule.Windows[2];

        await Assert.That(second.StepId).IsEqualTo(Second).Because("authoring order is kept");
        await Assert.That(second.FromTick).IsEqualTo(960);
        await Assert.That(second.UntilTick).IsEqualTo(959);
        await Assert.That(second.IsEmpty).IsTrue();
        await Assert.That(second.Contains(960)).IsFalse();
        await Assert.That(third.FromTick).IsEqualTo(960);
        await Assert.That(third.IsEmpty).IsFalse();
        await Assert.That(schedule.At(960)!.Value.StepId).IsEqualTo(Third)
            .Because("the later of two steps sharing a tick owns it");
    }

    [Test]
    public async Task NegativeAtSeconds_IsATickPastTheRoundLength_AndLegal()
    {
        StepSchedule schedule = StepSchedule.FromRoundClock([(First, 5), (Second, -10.5)], 115);

        await Assert.That(schedule.Windows[1].FromTick).IsEqualTo((115 + 10) * 64 + 32);
        await Assert.That(schedule.LastTick).IsGreaterThan(115 * 64);
        await Assert.That(StepSchedule.AtSecondsFor(schedule.LastTick, 115)).IsEqualTo(-10.5);
    }

    [Test]
    public async Task TickFor_RoundsHalfAwayFromZero_AsStratClockDoes()
    {
        // 1/128 s is half a tick.
        await Assert.That(StepSchedule.TickFor(115 - 1.0 / 128, 115)).IsEqualTo(1);
        await Assert.That(StepSchedule.TickFor(115 + 1.0 / 128, 115)).IsEqualTo(-1);
        await Assert.That(StepSchedule.TickFor(0, 115)).IsEqualTo(7360);
    }

    [Test]
    public void AClockThatCountsUp_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => StepSchedule.FromRoundClock([(First, 100), (Second, 101)], 115));
    }

    [Test]
    public async Task NoSteps_IsAnEmptySchedule()
    {
        StepSchedule schedule = new([]);

        await Assert.That(schedule.Windows).IsEmpty();
        await Assert.That(schedule.LastTick).IsEqualTo(0);
        await Assert.That(schedule.At(0)).IsNull();
    }
}
