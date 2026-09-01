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
///     Gate for the bomb plant/defuse + C4 detonation timers. The C4 detonation countdown
///     (<c>m_flC4Blow − correctedCurtime</c>) must read ≈40s at a <c>bomb_planted</c> frame, and the
///     defuse-in-progress second timer (<c>m_flDefuseCountDown − correctedCurtime</c>) must read a
///     sensible value bounded by <c>m_flDefuseLength</c> at a real defuse frame.
///     <para>
///         Independent cross-check of the shared offset: the SAME <c>clockBase</c> derived from the first
///         <c>round_freeze_end</c> (Phase 1) lands the bomb timer at ≈40. <c>m_flC4Blow</c> is unrelated
///         to <c>m_fRoundStartTime</c>, so 40 here proves the offset is genuinely shared, not fitted to
///         the round clock.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DBombTimerTests
{
    [Test]
    public async Task BombTimer_IsC4TimerLength_AtPlant_AndDecreases()
    {
        (ParsedDemo demo, IReadOnlyList<DemoFrame> frames, double clockBase, int tickRate, string path)
            = LoadCalibrated();

        int plantFrame = FirstEventFrame(frames, "bomb_planted");
        if (plantFrame < 0)
        {
            throw new SkipTestException("no bomb_planted event in demo");
        }

        // A few frames after the plant so the CPlantedC4 entity exists and m_flC4Blow is populated.
        int gateFrame = Math.Min(plantFrame + 8, frames.Count - 1);
        GameInfo at = ReadGameInfoAt(demo, frames, path, clockBase, tickRate, gateFrame);

        int laterFrame = FrameAfterSeconds(frames, gateFrame, tickRate, 10);
        GameInfo later = ReadGameInfoAt(demo, frames, path, clockBase, tickRate, laterFrame);

        Console.WriteLine($"[bomb-timer] {Path.GetFileName(path)} clockBase={clockBase:F3} " +
                          $"plantFrame={plantFrame} gateFrame={gateFrame} ticking={at.BombTicking} " +
                          $"detonation={at.RoundSeconds:F2}s later={later.RoundSeconds:F2}s");

        await Assert.That(at.BombTicking).IsTrue();
        // C4 timer is 40s (m_flTimerLength = mp_c4timer). ~40 at plant, allowing the few-frames-after slack.
        await Assert.That(at.RoundSeconds).IsGreaterThan(38.5);
        await Assert.That(at.RoundSeconds).IsLessThanOrEqualTo(40.5);

        // Decreasing ~1s/sec: ~10s later it dropped ~10s.
        double delta = at.RoundSeconds - later.RoundSeconds;
        await Assert.That(delta).IsGreaterThan(8.5);
        await Assert.That(delta).IsLessThan(11.5);
    }

    [Test]
    public async Task DefuseTimer_IsSane_DuringDefuseInProgress()
    {
        (ParsedDemo demo, IReadOnlyList<DemoFrame> frames, double clockBase, int tickRate, string path)
            = LoadCalibrated();

        int defuseFrame = FirstEventFrame(frames, "bomb_defused");
        if (defuseFrame < 0)
        {
            throw new SkipTestException("no bomb_defused event in demo");
        }

        // Find the contiguous m_bBeingDefused run ending at bomb_defused with ONE forward-advancing
        // tracker (O(n), not a fresh replay-from-0 per frame), then take a frame mid-run: sampling near
        // completion (as the throwaway probe did) leaves ~0s remaining.
        (int runStart, int runEnd) = DefuseRun(frames, defuseFrame);
        if (runStart < 0)
        {
            throw new SkipTestException("could not locate a m_bBeingDefused run before bomb_defused");
        }

        int midFrame = (runStart + runEnd) / 2;
        GameInfo mid = ReadGameInfoAt(demo, frames, path, clockBase, tickRate, midFrame);

        Console.WriteLine($"[defuse-timer] {Path.GetFileName(path)} run=[{runStart}..{runEnd}] " +
                          $"defuseFrame={defuseFrame} midFrame={midFrame} inProgress={mid.DefuseInProgress} " +
                          $"defuse={mid.DefuseSeconds:F2}s kit='{mid.DefuseKitNote}' detonation={mid.RoundSeconds:F2}s");

        await Assert.That(mid.DefuseInProgress).IsTrue();
        // 0 < defuse remaining ≤ m_flDefuseLength (10 no-kit / 5 with-kit) + slack. The C4 is also ticking,
        // so the main detonation countdown is live alongside (the defuse-vs-detonation race).
        await Assert.That(mid.DefuseSeconds).IsGreaterThan(0.0);
        await Assert.That(mid.DefuseSeconds).IsLessThanOrEqualTo(10.5);
        await Assert.That(mid.BombTicking).IsTrue();
    }

    // ── Shared helpers ──────────────────────────────────────────────────────────

    private static (ParsedDemo, IReadOnlyList<DemoFrame>, double, int, string) LoadCalibrated()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int tickRate = demo.TickRate > 0 ? demo.TickRate : 64;

        int firstFreeze = FirstEventFrame(frames, "round_freeze_end");
        if (firstFreeze < 0)
        {
            throw new SkipTestException("no round_freeze_end to calibrate the game clock");
        }

        // Same calibration the host does at load (and the round-timer gate). One clockBase, shared by both
        // timers. That is precisely what the bomb≈40 assertion independently proves.
        (double clockBase, bool valid) = GameClock.ComputeClockBase(frames, firstFreeze, tickRate);
        if (!valid)
        {
            throw new SkipTestException("game clock did not calibrate");
        }

        return (demo, frames, clockBase, tickRate, path);
    }

    // Activates a real VM against a real ModuleContext at `frame`, returns the resulting GameInfo.
    private static GameInfo ReadGameInfoAt(ParsedDemo demo, IReadOnlyList<DemoFrame> frames, string path,
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
        context.SetGameClock(clockBase);

        Playback2DTabViewModel vm = new();
        vm.OnActivated(context);
        return vm.GameInfo;
    }

    private static int FirstEventFrame(IReadOnlyList<DemoFrame> frames, string name)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].InnerMessages.Any(m => m is GameEventMessage gem &&
                                                 gem.DecodedEvent.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    // Finds the contiguous m_bBeingDefused run that ends at (just before) `defuseFrame`, using ONE tracker
    // advanced FORWARD through a bounded window. ReplayToIndex over ascending indices replays each frame
    // once total (O(window)), versus a fresh tracker per frame (replay-from-0 each = O(window²)). Returns
    // (-1,-1) if no being-defused frame is seen. The window (~1200 frames ≈ ≥10s defuse) is generous.
    private static (int Start, int End) DefuseRun(IReadOnlyList<DemoFrame> frames, int defuseFrame)
    {
        int from = Math.Max(0, defuseFrame - 1200);
        EntityTracker tracker = new();
        tracker.ReplayToIndex(from, frames); // prime to the window start ONCE (replay-from-0 just here)
        int start = -1, end = -1;

        for (int i = from; i < defuseFrame; i++)
        {
            if (i > from)
            {
                tracker.AdvanceOneFrame(frames[i]); // O(1) step forward, no replay-from-0 per frame
            }

            if (IsBeingDefused(tracker))
            {
                if (start < 0)
                {
                    start = i;
                }

                end = i;
            }
            else if (start >= 0)
            {
                // The run we want is the one adjacent to bomb_defused; reset on a gap so an earlier,
                // aborted defuse attempt doesn't get merged in.
                start = -1;
                end = -1;
            }
        }

        return (start, end);
    }

    private static bool IsBeingDefused(EntityTracker tracker)
    {
        foreach ((int _, EntityState e) in tracker.CurrentEntities.AllIndexed())
        {
            if (e.ClassName.Contains("CPlantedC4", StringComparison.OrdinalIgnoreCase))
            {
                return e["m_bBeingDefused"] is int v && v != 0;
            }
        }

        return false;
    }

    // First frame at/after `from` whose ServerTick is >= the start tick + secondsAfter (clamped to end).
    private static int FrameAfterSeconds(IReadOnlyList<DemoFrame> frames, int from, int tickRate,
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
