#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The round-clock mapping (strat-model.md §3.4), both directions, frame clock only. Fixture values are the
///     design's: a round starting at tick 1761, 64 ticks per second, and the 115 s round length measured at every
///     freeze-end of the two Steam matchmaking replays.
/// </summary>
public class StratClockTests
{
    private static readonly CachedRound Round = new() { Number = 7, StartTickFrameClock = 1761 };

    [Test]
    public async Task AStepAt90Seconds_Is25SecondsIntoTheRound()
    {
        await Assert.That(StratClock.TickFor(90, Round, 64, 115)).IsEqualTo(1761 + 1600);
    }

    [Test]
    public async Task APlant26Point2SecondsIn_ReadsAs1Minute28Point8()
    {
        int plant = 1761 + (int)Math.Round(26.2 * 64);
        double? atSeconds = StratClock.AtSecondsFor(plant, Round, 64, 115);

        await Assert.That(atSeconds!.Value).IsEqualTo(88.8).Within(1.0 / 64).Because("the tick is whole; the error is under one tick");
        await Assert.That(StratClock.Format(atSeconds.Value)).IsEqualTo("1:28.8");
    }

    [Test]
    public async Task ATickBeforeFreezeEnd_HasNoRoundClockValue()
    {
        using (Assert.Multiple())
        {
            await Assert.That(StratClock.AtSecondsFor(1760, Round, 64, 115)).IsNull();
            await Assert.That(StratClock.AtSecondsFor(1761, Round, 64, 115)).IsEqualTo(115);
        }
    }

    [Test]
    public async Task NegativeAtSeconds_RoundTrips_AndFormatsAfterTheTimer()
    {
        int tick = StratClock.TickFor(-12.5, Round, 64, 115);

        using (Assert.Multiple())
        {
            await Assert.That(tick).IsEqualTo(1761 + (int)(127.5 * 64));
            await Assert.That(StratClock.AtSecondsFor(tick, Round, 64, 115)).IsEqualTo(-12.5);
            await Assert.That(StratClock.Format(-12.5)).IsEqualTo("+0:12.5");
        }
    }

    [Test]
    [Arguments(0, 64)]
    [Arguments(40, 64)]
    [Arguments(75.5, 128)]
    [Arguments(115, 64)]
    public async Task EveryWholeTick_RoundTrips(double atSeconds, int tickRate)
    {
        int tick = StratClock.TickFor(atSeconds, Round, tickRate, 115);
        await Assert.That(StratClock.TickFor(StratClock.AtSecondsFor(tick, Round, tickRate, 115)!.Value, Round, tickRate, 115)).IsEqualTo(tick);
    }

    [Test]
    public async Task Format_ShowsATenthOnlyWhenThereIsOne()
    {
        using (Assert.Multiple())
        {
            await Assert.That(StratClock.Format(75)).IsEqualTo("1:15");
            await Assert.That(StratClock.Format(75.5)).IsEqualTo("1:15.5");
            await Assert.That(StratClock.Format(5)).IsEqualTo("0:05");
            await Assert.That(StratClock.Format(0)).IsEqualTo("0:00");
            await Assert.That(StratClock.Format(59.96)).IsEqualTo("1:00");
        }
    }

    [Test]
    public async Task RoundSeconds_PrefersTheRoundTimeFact_ThenTheStrat_Then115()
    {
        using (Assert.Multiple())
        {
            await Assert.That(StratClock.RoundSecondsFor(105, new StratClockInfo { RoundSeconds = 115 })).IsEqualTo(105);
            await Assert.That(StratClock.RoundSecondsFor(null, new StratClockInfo { RoundSeconds = 120 })).IsEqualTo(120);
            await Assert.That(StratClock.RoundSecondsFor(null, null)).IsEqualTo(115);
        }
    }

    [Test]
    public async Task TryParse_ReadsWhatTheStepTableAccepts()
    {
        using (Assert.Multiple())
        {
            await Assert.That(StratClock.TryParse("1:15", out double a) && a == 75).IsTrue();
            await Assert.That(StratClock.TryParse(" 1:15.5 ", out double b) && b == 75.5).IsTrue();
            await Assert.That(StratClock.TryParse("+0:05", out double c) && c == -5).IsTrue().Because("after the timer stopped");
            await Assert.That(StratClock.TryParse("90", out double d) && d == 90).IsTrue();
            await Assert.That(StratClock.TryParse("1:60", out _)).IsFalse();
            await Assert.That(StratClock.TryParse("soon", out _)).IsFalse();
            await Assert.That(StratClock.TryParse("", out _)).IsFalse();
            await Assert.That(StratClock.TryParse(null, out _)).IsFalse();
        }
    }

    [Test]
    [Arguments(75.0)]
    [Arguments(75.5)]
    [Arguments(-5.0)]
    [Arguments(0.0)]
    public async Task TryParse_InvertsFormat(double atSeconds)
    {
        await Assert.That(StratClock.TryParse(StratClock.Format(atSeconds), out double parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(atSeconds);
    }
}
