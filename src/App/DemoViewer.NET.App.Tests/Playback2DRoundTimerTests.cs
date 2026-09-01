#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Regression gate for the 2D Playback round timer. The broken calc showed ~3:00 because it used the
///     naive curtime (<c>tick/tickRate</c>) against the entity time base, which runs a constant offset
///     ahead. The fix derives that offset ONCE from the first <c>round_freeze_end</c> (the host's
///     <see cref="GameClock" />) and reads the networked <c>m_iRoundTime</c> (115), so the displayed
///     round remaining is ≈115s (1:55) at a freeze_end and decreases ~1s/sec after.
///     <para>
///         Non-tautological by construction: the offset is derived from the FIRST freeze_end but the
///         assertion is made at a LATER freeze_end (round 2+). That only closes if the offset is truly
///         constant across rounds (the physical claim), not because the algebra forces it.
///     </para>
///     Sync parse path (no rendering): see <see cref="Playback2DRealDemoRenderTests" />.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DRoundTimerTests
{
    [Test]
    public async Task RoundTimer_IsRoundLength_AtFreezeEnd_AndDecreases()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int tickRate = demo.TickRate > 0 ? demo.TickRate : 64;

        List<int> freezeEnds = FreezeEndFrames(frames);
        if (freezeEnds.Count < 2)
        {
            throw new SkipTestException("need at least two round_freeze_end events");
        }

        // The host derives clockBase ONCE from the FIRST freeze_end (exactly what MainViewModel.ApplyGameClock
        // does at load). The assertion below is at a LATER freeze_end → the gate proves the offset is
        // constant across rounds, not a fitted tautology.
        (double clockBase, bool valid) = GameClock.ComputeClockBase(frames, freezeEnds[0], tickRate);
        await Assert.That(valid).IsTrue();

        // A LATER round's freeze_end, a few frames AFTER the event so m_bFreezePeriod has cleared and the
        // round clock is running just under the round length.
        int laterFreeze = freezeEnds[1];
        int gateFrame = FrameSlightlyAfter(frames, laterFreeze, tickRate, 1);

        double remainAtGate = RoundSecondsAt(frames, demo, path, clockBase, tickRate, gateFrame);
        // ~10s later: the round clock must have ticked down ~10s (±1.5s slack for frame granularity).
        int laterFrame = FrameSlightlyAfter(frames, gateFrame, tickRate, 10);
        double remainLater = RoundSecondsAt(frames, demo, path, clockBase, tickRate, laterFrame);

        Console.WriteLine($"[round-timer] {Path.GetFileName(path)} clockBase={clockBase:F3} " +
                          $"round2 freeze_end frame={laterFreeze} gateFrame={gateFrame} " +
                          $"remainAtGate={remainAtGate:F2}s laterFrame={laterFrame} remainLater={remainLater:F2}s");

        // ≈115 at the (later) freeze_end, allowing ~1s for the "few frames after" and rounding residue.
        await Assert.That(remainAtGate).IsGreaterThan(112.0);
        await Assert.That(remainAtGate).IsLessThanOrEqualTo(115.5);

        // Decreasing ~1s/sec: ~10s later it dropped ~10s (not frozen, not running backward).
        double delta = remainAtGate - remainLater;
        await Assert.That(delta).IsGreaterThan(8.5);
        await Assert.That(delta).IsLessThan(11.5);
    }

    // Activates a real VM against a real ModuleContext (controller-published tracker advanced to the frame,
    // host-join driven), with the host's clock calibration applied, then reads GameInfo.RoundSeconds.
    private static double RoundSecondsAt(IReadOnlyList<DemoFrame> frames, ParsedDemo demo, string path,
        double clockBase, int tickRate, int frame)
    {
        EntityTracker tracker = new();
        tracker.ReplayToIndex(frame, frames);

        PlaybackController controller = new();
        controller.LoadDemo(frames, tickRate);
        controller.SyncPositionFromShell(frame);
        controller.PublishTracker(tracker);

        ModuleContext context = new(controller, () => path);
        context.SetRoster(demo.Players.Values.Select(p =>
            new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));
        context.SetGameClock(clockBase); // host calibration, applied BEFORE OnActivated (like SetRoster)

        Playback2DTabViewModel vm = new();
        vm.OnActivated(context); // BuildFrame → UpdateGameInfo runs immediately

        return vm.GameInfo.RoundSeconds;
    }

    private static List<int> FreezeEndFrames(IReadOnlyList<DemoFrame> frames)
    {
        List<int> result = new();
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].InnerMessages.Any(m => m is GameEventMessage gem &&
                                                 gem.DecodedEvent.Name.Equals("round_freeze_end", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(i);
            }
        }

        return result;
    }

    // First frame at/after `from` whose ServerTick is >= the start tick + secondsAfter (clamped to end).
    private static int FrameSlightlyAfter(IReadOnlyList<DemoFrame> frames, int from, int tickRate,
        int secondsAfter)
    {
        int targetTick = frames[from].ServerTick + secondsAfter * tickRate;
        for (int i = from; i < frames.Count; i++)
        {
            if (frames[i].ServerTick >= targetTick)
            {
                return i;
            }
        }

        return frames.Count - 1;
    }
}
