#region

using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Playback2D.Core.Hud;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Hud;

/// <summary>
///     The HUD for a strat: the round clock counting down from the strat's round length, and nothing else.
///     No round number, no score, no kills, no roster; a strat has none of them,
///     so <c>hud.clock</c> burns in the countdown and the other HUD layers draw their empty state.
///     <para>
///         The clock is <see cref="StratFrameSource.RoundSecondsAt" />, the same function the frame's
///         <c>GameInfo</c> reads, so the burnt-in clock and the frame cannot disagree.
///     </para>
/// </summary>
public sealed class StratHudDataSource : IHudDataSource
{
    private readonly bool _countsUp;
    private readonly int _roundSeconds;

    /// <summary>Creates a source.</summary>
    /// <param name="roundSeconds">The strat's round length.</param>
    /// <param name="countsUp">The strat is timed from its trigger: the clock counts up from 0:00.</param>
    public StratHudDataSource(int roundSeconds, bool countsUp = false)
    {
        _roundSeconds = roundSeconds;
        _countsUp = countsUp;
    }

    /// <inheritdoc />
    public HudSnapshot At(int tick)
    {
        // Floored so the counted-up clock shows a whole second only once it has passed; the layer rounds up.
        double remaining = _countsUp ? Math.Floor(StratFrameSource.ElapsedAt(tick)) : StratFrameSource.RoundSecondsAt(_roundSeconds, tick);

        // Clamped at zero: a step after the plant sits past the round clock, and the clock there reads
        // 0:00, as the frame's own RoundTime does.
        return HudSnapshot.Empty with
        {
            Tick = tick,
            CountdownSeconds = Math.Max(0, remaining)
        };
    }
}
