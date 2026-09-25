namespace DemoViewer.NET.Playback2D.Core.Keyframes;

/// <summary>
///     One step's span of the strat frame clock: its strokes are visible over it and its token edits land
///     on it (step-authoring.md §3.3, §3.5).
/// </summary>
/// <param name="StepId">The step.</param>
/// <param name="FromTick">The step's own tick.</param>
/// <param name="UntilTick">
///     The last tick before the next step, inclusive, or null for the last step ("until the end", as
///     <c>TimeEnvelope</c> spells it). A step sharing its tick with the next has <c>FromTick - 1</c>
///     here: a zero-length window.
/// </param>
public readonly record struct StepWindow(Guid StepId, int FromTick, int? UntilTick)
{
    /// <summary>Whether the window covers no tick at all: a step sharing its tick with the next.</summary>
    public bool IsEmpty => UntilTick is { } until && until < FromTick;

    /// <summary>Whether a tick falls inside the window.</summary>
    /// <param name="tick">A strat frame-clock tick.</param>
    public bool Contains(int tick) => tick >= FromTick && (UntilTick is not { } until || tick <= until);
}

/// <summary>
///     A strat's steps on the <b>strat frame clock</b>: tick 0 is the round start (freeze-end) and the rate
///     is a constant <see cref="TicksPerSecond" />. This clock never meets a demo clock; the App's
///     <c>StratClock</c> is the only bridge and it works in seconds (correction 7 puts the constant here,
///     in Core, so the App reads it rather than the other way round).
///     <para>
///         A step at <c>atSeconds</c> sits at <c>round((roundSeconds - atSeconds) × 64)</c>, rounded half
///         away from zero as <c>StratClock.TickFor</c> rounds. A negative <c>atSeconds</c> (after the timer
///         stopped for a plant) is a tick past <c>roundSeconds × 64</c> and is legal.
///     </para>
///     <para>
///         Windows are in authoring order, which the Strat Model keeps non-increasing in
///         <c>atSeconds</c>, so non-decreasing in tick. Two steps sharing a time share a tick, and the
///         earlier one gets a zero-length window: the later one owns the tick.
///     </para>
/// </summary>
public sealed class StepSchedule
{
    /// <summary>The strat frame rate, the same fallback <c>AnnotationSession.DefaultTicksPerSecond</c> uses.</summary>
    public const int TicksPerSecond = 64;

    private readonly StepWindow[] _windows;

    /// <summary>Creates a schedule from each step's tick, in authoring order.</summary>
    /// <param name="steps">Step id and tick; ticks non-decreasing.</param>
    /// <exception cref="ArgumentException">A tick earlier than the one before it.</exception>
    public StepSchedule(IEnumerable<(Guid StepId, int Tick)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        (Guid StepId, int Tick)[] list = [.. steps];
        _windows = new StepWindow[list.Length];

        for (int i = 0; i < list.Length; i++)
        {
            (Guid id, int tick) = list[i];
            if (i > 0 && tick < list[i - 1].Tick)
            {
                // The validator refuses this document; a schedule built anyway would hand strokes to
                // the wrong step, so it is refused here too rather than quietly re-sorted.
                throw new ArgumentException(
                    $"Step {i} at tick {tick} comes before step {i - 1} at tick {list[i - 1].Tick}; the round clock counts down.",
                    nameof(steps));
            }

            int? until = i + 1 < list.Length ? list[i + 1].Tick - 1 : null;
            _windows[i] = new StepWindow(id, tick, until);
        }

        LastTick = list.Length > 0 ? list[^1].Tick : 0;
    }

    /// <summary>The windows, in authoring order.</summary>
    public IReadOnlyList<StepWindow> Windows => _windows;

    /// <summary>The last step's tick; 0 for a strat with no steps.</summary>
    public int LastTick { get; }

    /// <summary>Builds a schedule from each step's round-clock time, in authoring order.</summary>
    /// <param name="steps">Step id and <c>atSeconds</c>; non-increasing.</param>
    /// <param name="roundSeconds">The strat's round length.</param>
    public static StepSchedule FromRoundClock(IEnumerable<(Guid StepId, double AtSeconds)> steps, double roundSeconds)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return new StepSchedule(steps.Select(s => (s.StepId, TickFor(s.AtSeconds, roundSeconds))));
    }

    /// <summary>The strat frame-clock tick of a round-clock time.</summary>
    /// <param name="atSeconds">Seconds left on the round clock; negative after the timer stopped.</param>
    /// <param name="roundSeconds">The round length.</param>
    public static int TickFor(double atSeconds, double roundSeconds) =>
        (int)Math.Round((roundSeconds - atSeconds) * TicksPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>The round-clock time of a strat frame-clock tick.</summary>
    /// <param name="tick">A strat frame-clock tick.</param>
    /// <param name="roundSeconds">The round length.</param>
    public static double AtSecondsFor(int tick, double roundSeconds) => roundSeconds - tick / (double)TicksPerSecond;

    /// <summary>
    ///     The window the tick falls in, or null before the first step. A zero-length window is never
    ///     the answer: the step after it owns the shared tick.
    /// </summary>
    /// <param name="tick">A strat frame-clock tick.</param>
    public StepWindow? At(int tick)
    {
        // The last window opening at or before the tick. Windows tile the clock from the first step
        // onward, so that one contains it; a shared tick resolves to the later step by construction.
        int lo = 0, hi = _windows.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_windows[mid].FromTick <= tick)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 ? _windows[found] : null;
    }

    /// <summary>The index of a step's window, or -1.</summary>
    /// <param name="stepId">The step.</param>
    public int IndexOf(Guid stepId)
    {
        for (int i = 0; i < _windows.Length; i++)
        {
            if (_windows[i].StepId == stepId)
            {
                return i;
            }
        }

        return -1;
    }
}
