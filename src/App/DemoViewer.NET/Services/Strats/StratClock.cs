#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The mapping between a strat's round clock and a demo's frame clock (strat-model.md §3.4). Pure, both
///     directions, on the frame clock only: a round starts at its freeze-end <c>GameTick</c>, which is
///     <c>CachedRound.StartTickFrameClock</c> (measured equal on 43 of 43 rounds), and a step's
///     <c>atSeconds</c> counts DOWN from the round length.
/// </summary>
public static class StratClock
{
    /// <summary>The v1 strat clock: seconds remaining in the round.</summary>
    public const string RoundKind = "round";

    /// <summary>Reserved for post-plant strats counted from <c>bomb_planted</c>; not defined in v1 (decision 9).</summary>
    public const string PlantKind = "plant";

    /// <summary>The competitive round length, <c>m_iRoundTime</c> at every measured freeze-end.</summary>
    public const double DefaultRoundSeconds = 115;

    /// <summary>The frame-clock tick a round-clock time falls on in one round.</summary>
    /// <param name="atSeconds">Round clock remaining; negative after the timer stopped for a plant.</param>
    /// <param name="round">The round, frame clock.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="roundSeconds">The round length (<see cref="RoundSecondsFor" />).</param>
    public static int TickFor(double atSeconds, CachedRound round, int tickRate, double roundSeconds)
    {
        ArgumentNullException.ThrowIfNull(round);
        return TickFor(atSeconds, round.StartTickFrameClock, tickRate, roundSeconds);
    }

    /// <summary>The frame-clock tick a round-clock time falls on, from the round's start tick.</summary>
    /// <param name="atSeconds">Round clock remaining; negative after the timer stopped for a plant.</param>
    /// <param name="roundStartTick">The round's freeze-end tick.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="roundSeconds">The round length.</param>
    public static int TickFor(double atSeconds, int roundStartTick, int tickRate, double roundSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRate);
        return roundStartTick + (int)Math.Round((roundSeconds - atSeconds) * tickRate, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    ///     The round-clock time of a frame-clock tick in one round, or null for a tick before the round's
    ///     freeze-end, which has no round-clock value (the <c>ResolveRoundWindow</c> rule for warmup).
    /// </summary>
    /// <param name="tick">The frame-clock tick.</param>
    /// <param name="round">The round, frame clock.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="roundSeconds">The round length.</param>
    public static double? AtSecondsFor(int tick, CachedRound round, int tickRate, double roundSeconds)
    {
        ArgumentNullException.ThrowIfNull(round);
        return AtSecondsFor(tick, round.StartTickFrameClock, tickRate, roundSeconds);
    }

    /// <summary>The round-clock time of a tick, from the round's start tick; null before it.</summary>
    /// <param name="tick">The frame-clock tick.</param>
    /// <param name="roundStartTick">The round's freeze-end tick.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="roundSeconds">The round length.</param>
    public static double? AtSecondsFor(int tick, int roundStartTick, int tickRate, double roundSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRate);
        return tick < roundStartTick ? null : roundSeconds - (tick - roundStartTick) / (double)tickRate;
    }

    /// <summary>
    ///     The round length to map a demo round with: Round Facts' <c>roundTime</c> fact when the round has one,
    ///     else the strat's own authored length, else 115.
    /// </summary>
    /// <param name="roundTimeFact">The round's <c>roundTime</c> fact in seconds, when present.</param>
    /// <param name="clock">The strat's clock block.</param>
    public static double RoundSecondsFor(double? roundTimeFact, StratClockInfo? clock) =>
        roundTimeFact is > 0 ? roundTimeFact.Value
        : clock is { RoundSeconds: > 0 } ? clock.RoundSeconds
        : DefaultRoundSeconds;

    /// <summary>
    ///     <c>m:ss</c>, with a tenth only when there is one (<c>1:15</c>, <c>1:15.5</c>); a negative time, after the
    ///     timer stopped, as <c>+m:ss</c>.
    /// </summary>
    /// <param name="atSeconds">Round clock remaining.</param>
    public static string Format(double atSeconds)
    {
        double tenths = Math.Round(Math.Abs(atSeconds) * 10, MidpointRounding.AwayFromZero);
        long whole = (long)(tenths / 10);
        int tenth = (int)(tenths % 10);
        string text = string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
        if (tenth != 0)
        {
            text += "." + tenth.ToString(CultureInfo.InvariantCulture);
        }

        return atSeconds < 0 && tenths > 0 ? "+" + text : text;
    }
}
