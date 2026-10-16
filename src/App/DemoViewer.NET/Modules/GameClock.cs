#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Computes the shared game-clock calibration that both the 2D-playback round timer and the
///     bomb/defuse timers consume (see <c>IModuleContext.CurtimeSeconds</c>).
///     <para>
///         The naive curtime <c>m_flGameStartTime + tick/tickRate</c> reads a constant offset ahead of
///         the entity time base that <c>m_fRoundStartTime</c> and the planted-C4's <c>m_flC4Blow</c>
///         stamp against (~5.4s on the verified demo). The correction is a single scalar
///         <c>clockBase</c> derived ONCE from the first <c>round_freeze_end</c>, where the corrected
///         curtime must equal that round's <c>m_fRoundStartTime</c>:
///     </para>
///     <code>
///          clockBase       = firstFreezeServerTick/tickRate − roundStart(firstFreeze)
///          CurtimeSeconds  = tick/tickRate − clockBase
///      </code>
///     <para>
///         <c>m_flGameStartTime</c> cancels out of the consume-time formula, so the module needs only
///         the one <c>clockBase</c> scalar. Lives in the App project (not Abstractions) because it
///         reads an <see cref="EntityTracker" />, the abstractions assembly stays Parser-free. An open
///         reads it off its rules run (<see cref="GameClockSource" />); <see cref="ComputeClockBase" />
///         is the replay that run must agree with.
///     </para>
/// </summary>
public static class GameClock
{
    /// <summary>
    ///     Replays a fresh tracker to <paramref name="firstFreezeEndFrame" /> and reads the game-rules
    ///     entity to derive <c>clockBase</c>. Returns <c>(0, false)</c> when no game-rules entity or
    ///     <c>m_fRoundStartTime</c> is present (callers fall back to the naive reading, offset 0).
    /// </summary>
    public static (double ClockBase, bool Valid) ComputeClockBase(
        IReadOnlyList<DemoFrame> frames, int firstFreezeEndFrame, int tickRate)
    {
        if (frames.Count == 0 || firstFreezeEndFrame < 0 || firstFreezeEndFrame >= frames.Count
            || tickRate <= 0)
        {
            return (0, false);
        }

        EntityTracker tracker = new();
        tracker.ReplayToIndex(firstFreezeEndFrame, frames);
        return Read(tracker, frames[firstFreezeEndFrame].ServerTick, tickRate);
    }

    /// <summary>
    ///     Derives <c>clockBase</c> from <paramref name="tracker" /> as it stands after the first
    ///     <c>round_freeze_end</c> frame, whose server tick is <paramref name="serverTick" />.
    /// </summary>
    internal static (double ClockBase, bool Valid) Read(EntityTracker tracker, int serverTick, int tickRate)
    {
        EntityState? rules = null;
        foreach ((int _, EntityState e) in tracker.CurrentEntities.AllIndexed())
        {
            if (e.ClassName.Contains("CCSGameRulesProxy", StringComparison.OrdinalIgnoreCase))
            {
                rules = e;
                break;
            }
        }

        if (rules?["m_pGameRules.m_fRoundStartTime"] is not { } roundStartObj)
        {
            return (0, false);
        }

        double roundStart = roundStartObj switch
        {
            float f => f,
            double d => d,
            _ => double.NaN
        };

        if (double.IsNaN(roundStart))
        {
            return (0, false);
        }

        double clockBase = serverTick / (double)tickRate - roundStart;
        return (clockBase, true);
    }
}
