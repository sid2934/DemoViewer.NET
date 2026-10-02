#region

using System.Globalization;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The mapping between a strat's round clock and a demo's frame clock (strat-model.md §3.4). Pure, both
///     directions, on the frame clock only: a round starts at its freeze-end <c>GameTick</c>, which is
///     <c>CachedRound.StartTickFrameClock</c> (measured equal on 43 of 43 rounds), and a step's
///     <c>atSeconds</c> counts DOWN from the round length. Also the strat clock's own rules: a strat timed from its
///     trigger counts UP from 0, and both clocks run from strat tick 0 at the start.
/// </summary>
public static class StratClock
{
    /// <summary>The v1 strat clock: seconds remaining in the round.</summary>
    public const string RoundKind = "round";

    /// <summary>Reserved for post-plant strats counted from <c>bomb_planted</c>; not defined in v1 (decision 9).</summary>
    public const string PlantKind = "plant";

    /// <summary>A strat timed from its trigger: <c>atSeconds</c> counts UP from 0.</summary>
    public const string TriggerKind = "trigger";

    /// <summary>The competitive round length, <c>m_iRoundTime</c> at every measured freeze-end.</summary>
    public const double DefaultRoundSeconds = 115;

    /// <summary>Whether the strat is timed from its trigger rather than on the round clock.</summary>
    /// <param name="clock">The strat's clock block.</param>
    public static bool IsTrigger(StratClockInfo? clock) =>
        clock is not null && string.Equals(clock.Kind, TriggerKind, StringComparison.Ordinal);

    /// <summary>The round length the strat is authored against: its own, else 115.</summary>
    /// <param name="clock">The strat's clock block.</param>
    public static double LengthOf(StratClockInfo? clock) => clock is { RoundSeconds: > 0 } ? clock.RoundSeconds : DefaultRoundSeconds;

    /// <summary>The <c>atSeconds</c> of the strat's start: the round length on the round clock, 0 from a trigger.</summary>
    /// <param name="clock">The strat's clock block.</param>
    public static double StartOf(StratClockInfo? clock) => IsTrigger(clock) ? 0 : LengthOf(clock);

    /// <summary>Seconds from the strat's start to <paramref name="atSeconds" />, on either clock.</summary>
    /// <param name="clock">The strat's clock block.</param>
    /// <param name="atSeconds">A step's time as stored.</param>
    public static double ElapsedOf(StratClockInfo? clock, double atSeconds) => IsTrigger(clock) ? atSeconds : LengthOf(clock) - atSeconds;

    /// <summary>The stored time <paramref name="elapsed" /> seconds after the strat's start.</summary>
    /// <param name="clock">The strat's clock block.</param>
    /// <param name="elapsed">Seconds from the start.</param>
    public static double AtSecondsOf(StratClockInfo? clock, double elapsed) => IsTrigger(clock) ? elapsed : LengthOf(clock) - elapsed;

    /// <summary>
    ///     The strat tick of a stored time, never before the start: <c>round(elapsed × 64)</c>, rounded half away from
    ///     zero as <c>StepSchedule.TickFor</c> rounds, so a round clock lands on the same tick it always did.
    /// </summary>
    /// <param name="clock">The strat's clock block.</param>
    /// <param name="atSeconds">A step's time as stored.</param>
    public static int StratTickOf(StratClockInfo? clock, double atSeconds) =>
        Math.Max(0, (int)Math.Round(ElapsedOf(clock, atSeconds) * Playback2D.Core.Keyframes.StepSchedule.TicksPerSecond,
            MidpointRounding.AwayFromZero));

    /// <summary>The stored time at a strat tick.</summary>
    /// <param name="clock">The strat's clock block.</param>
    /// <param name="tick">A strat tick.</param>
    public static double AtSecondsAtTick(StratClockInfo? clock, int tick) =>
        AtSecondsOf(clock, tick / (double)Playback2D.Core.Keyframes.StepSchedule.TicksPerSecond);

    /// <summary>Whether <paramref name="a" /> comes after <paramref name="b" /> in the strat: lower on the round clock, higher from a trigger.</summary>
    /// <param name="clock">The strat's clock block.</param>
    /// <param name="a">A stored time.</param>
    /// <param name="b">Another.</param>
    public static bool IsAfter(StratClockInfo? clock, double a, double b) => IsTrigger(clock) ? a > b : a < b;

    /// <summary>
    ///     A stored time moved from one clock to the other: <c>roundSeconds − t</c> both ways, so it is its own inverse
    ///     and every tick stays where it was. Rounded to a thousandth so 115 − 57.8 is stored as 57.2.
    /// </summary>
    /// <param name="atSeconds">The time on the clock it leaves.</param>
    /// <param name="roundSeconds">The strat's round length.</param>
    public static double Convert(double atSeconds, double roundSeconds) =>
        Math.Round(roundSeconds - atSeconds, 3, MidpointRounding.AwayFromZero);

    /// <summary>
    ///     The ops that put a strat on the other clock, one undo entry: <c>clock.kind</c>, then every step's
    ///     <c>atSeconds</c> and every lurk rotate's, each through <see cref="Convert" />. A time after the plant
    ///     (negative on the round clock) becomes one past the round length from the trigger, and back. <c>holdSeconds</c>
    ///     is a duration and <c>trigger.atSeconds</c> is a round time, so neither moves. Empty when it is on that clock.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="kind"><see cref="RoundKind" /> or <see cref="TriggerKind" />.</param>
    public static List<PatchOp> SwitchOps(StratDocument document, string kind)
    {
        ArgumentNullException.ThrowIfNull(document);
        bool toTrigger = string.Equals(kind, TriggerKind, StringComparison.Ordinal);
        if (toTrigger == IsTrigger(document.Clock) || (!toTrigger && !string.Equals(kind, RoundKind, StringComparison.Ordinal)))
        {
            return [];
        }

        double length = LengthOf(document.Clock);
        List<PatchOp> ops = [PatchOp.ReplaceOp("/clock/kind", JsonValue.Create(document.Clock.Kind), JsonValue.Create(kind))];
        for (int i = 0; i < document.Steps.Count; i++)
        {
            StratStep step = document.Steps[i];
            string path = string.Create(CultureInfo.InvariantCulture, $"/steps/{i}");
            ops.Add(PatchOp.ReplaceOp(path + "/atSeconds", JsonValue.Create(step.AtSeconds), JsonValue.Create(Convert(step.AtSeconds, length))));
            if (step.Lurk?.Rotate?.AtSeconds is { } rotate)
            {
                ops.Add(PatchOp.ReplaceOp(path + "/lurk/rotate/atSeconds", JsonValue.Create(rotate), JsonValue.Create(Convert(rotate, length))));
            }
        }

        return ops;
    }

    /// <summary>A stored time as the strat's clock shows it: <c>1:15</c> on the round clock, <c>+0:08</c> from a trigger.</summary>
    /// <param name="clock">The strat's clock block.</param>
    /// <param name="atSeconds">A stored time.</param>
    public static string Format(StratClockInfo? clock, double atSeconds) => IsTrigger(clock) ? FormatElapsed(atSeconds) : Format(atSeconds);

    /// <summary>Seconds after a trigger as <c>+m:ss</c>, a tenth only when there is one; before it, <c>-m:ss</c>.</summary>
    /// <param name="seconds">Seconds after the trigger.</param>
    public static string FormatElapsed(double seconds)
    {
        string text = Format(Math.Abs(seconds));
        return (seconds < 0 && text != "0:00" ? "-" : "+") + text;
    }

    /// <summary>
    ///     What a person types for a time on the strat's clock. The round clock is <see cref="TryParse(string, out double)" />.
    ///     From a trigger, <c>+0:08</c>, <c>0:08</c> or <c>8</c> are 8 s after it, and <c>-0:02</c> is before it.
    /// </summary>
    /// <param name="clock">The strat's clock block.</param>
    /// <param name="text">The typed time.</param>
    /// <param name="atSeconds">The stored time.</param>
    public static bool TryParse(StratClockInfo? clock, string? text, out double atSeconds)
    {
        if (!IsTrigger(clock))
        {
            return TryParse(text, out atSeconds);
        }

        string trimmed = text?.Trim() ?? "";
        bool before = trimmed.StartsWith('-');
        if (before || trimmed.StartsWith('+'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.StartsWith('+') || trimmed.StartsWith('-') || !TryParse(trimmed, out atSeconds))
        {
            atSeconds = 0;
            return false;
        }

        atSeconds = before ? -atSeconds : atSeconds;
        return true;
    }

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

    /// <summary>
    ///     The inverse of <see cref="Format(double)" /> for what a person types in the step table: <c>1:15</c>,
    ///     <c>1:15.5</c>, <c>+0:05</c> for after the timer stopped, or plain seconds (<c>75</c>). False for anything
    ///     else, including a seconds part of 60 or more.
    /// </summary>
    /// <param name="text">The typed time.</param>
    /// <param name="atSeconds">Round clock remaining.</param>
    public static bool TryParse(string? text, out double atSeconds)
    {
        atSeconds = 0;
        string trimmed = text?.Trim() ?? "";
        bool after = trimmed.StartsWith('+');
        if (after)
        {
            trimmed = trimmed[1..];
        }

        int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        double value;
        if (colon < 0)
        {
            if (!double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
        }
        else if (int.TryParse(trimmed[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                 && double.TryParse(trimmed[(colon + 1)..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds)
                 && seconds < 60)
        {
            value = minutes * 60 + seconds;
        }
        else
        {
            return false;
        }

        atSeconds = after ? -value : value;
        return true;
    }
}
