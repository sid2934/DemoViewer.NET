#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The timeline against a REAL demo, end to end through the real <see cref="ModuleContext" />: parse →
///     navigator → context → adapter → tracks. Synchronous throughout (no headless dispatcher), and skipped
///     when no demo is staged.
///     <para>
///         <see cref="FrameIndexAtTick_MatchesLinearScan_AcrossWholeDemo" /> is the one that matters:
///         the binary search assumes <c>ServerTick</c> is non-decreasing across the frame list, and only
///         real tick data can prove that assumption. If it ever fails, the fix is a once-per-load
///         <c>isSorted</c> check with the linear scan as the fallback — not a quiet mis-seek.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DTimelineRealDemoTests
{
    [Test]
    public async Task FrameIndexAtTick_MatchesLinearScan_AcrossWholeDemo()
    {
        (ParsedDemo demo, PlaybackController controller, ModuleContext _) = Load();
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        // R4: the monotonicity the search rests on, asserted on real data rather than assumed.
        int outOfOrder = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].ServerTick < frames[i - 1].ServerTick)
            {
                outOfOrder++;
            }
        }

        Console.WriteLine($"[tick-monotonic] frames={frames.Count} outOfOrder={outOfOrder}");
        await Assert.That(outOfOrder).IsEqualTo(0);

        // Sample across the whole tick range plus the exact boundaries, comparing against the deleted scan.
        int lastTick = frames[^1].ServerTick;
        int step = Math.Max(1, lastTick / 500);
        int checks = 0;

        for (int tick = -3; tick <= lastTick + 3; tick += step)
        {
            await Assert.That(controller.FrameIndexAtTick(tick)).IsEqualTo(LinearScan(frames, tick));
            checks++;
        }

        for (int i = 0; i < frames.Count; i += Math.Max(1, frames.Count / 200))
        {
            int tick = frames[i].ServerTick;
            await Assert.That(controller.FrameIndexAtTick(tick)).IsEqualTo(LinearScan(frames, tick));
            checks++;
        }

        Console.WriteLine($"[tick-oracle] ticks compared={checks} lastTick={lastTick}");
        await Assert.That(checks).IsGreaterThan(100);
    }

    [Test]
    public async Task RoundTrack_BandCount_MatchesFreezeEndCount()
    {
        (ParsedDemo demo, PlaybackController _, ModuleContext context) = Load();
        ModuleTimelineData data = new(context);

        if (!data.HasEvent(RoundTrack.FreezeEndEvent))
        {
            throw new SkipTestException("demo carries no round_freeze_end (warmup-only / truncated)");
        }

        IReadOnlyList<int> freeze = data.FramesForEvent(RoundTrack.FreezeEndEvent);
        IReadOnlyList<TimelineBand> bands = new RoundTrack().BuildBands(data);

        // One band per freeze-end, plus a leading warmup band when the demo starts before the first one.
        int expected = freeze.Count + (freeze[0] > 0 ? 1 : 0);
        Console.WriteLine($"[round-bands] freezeEnds={freeze.Count} bands={bands.Count} frames={demo.Frames.Count}");
        await Assert.That(bands.Count).IsEqualTo(expected);

        // Ascending, non-overlapping, and covering to the last frame.
        for (int i = 1; i < bands.Count; i++)
        {
            await Assert.That(bands[i].StartFrameIndex).IsGreaterThan(bands[i - 1].EndFrameIndex);
        }

        await Assert.That(bands[^1].EndFrameIndex).IsEqualTo(demo.Frames.Count - 1);
    }

    [Test]
    public async Task KillTrack_MarkerCount_EqualsPlayerDeathCount()
    {
        (ParsedDemo _, PlaybackController __, ModuleContext context) = Load();
        ModuleTimelineData data = new(context);

        if (!data.HasEvent(KillTrack.DeathEvent))
        {
            throw new SkipTestException("demo carries no player_death");
        }

        IReadOnlyList<TimelineEventRecord> records = data.EventsOfType(KillTrack.DeathEvent);
        IReadOnlyList<TimelineMarker> markers = new KillTrack().BuildMarkers(data);

        Console.WriteLine($"[kill-markers] deaths(host)={context.GetEventTimeline(KillTrack.DeathEvent).Count} "
                          + $"placed={records.Count} markers={markers.Count}");

        await Assert.That(markers.Count).IsEqualTo(records.Count);
        await Assert.That(markers.Count).IsGreaterThan(0);

        // Ascending on the FRAME axis, and every marker inside the frame list.
        for (int i = 1; i < markers.Count; i++)
        {
            await Assert.That(markers[i].FrameIndex).IsGreaterThanOrEqualTo(markers[i - 1].FrameIndex);
        }

        await Assert.That(markers.All(m => m.FrameIndex >= 0 && m.FrameIndex < context.TotalFrames)).IsTrue();

        // Names came from the roster, not from raw slot numbers.
        await Assert.That(records.Any(r => r.Fields.ContainsKey(TimelineEventKeys.Attacker))).IsTrue();
    }

    // The body of the linear scan FrameIndexAtTick replaced — kept as the oracle.
    private static int LinearScan(IReadOnlyList<DemoFrame> frames, int tick)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].ServerTick >= tick)
            {
                return i;
            }
        }

        return -1;
    }

    private static (ParsedDemo Demo, PlaybackController Controller, ModuleContext Context) Load()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        PlaybackController controller = new();
        controller.LoadDemo(demo.Frames, demo.TickRate > 0 ? demo.TickRate : 64);

        SemanticNavigator navigator = new(controller);
        navigator.Build(demo.Frames);

        ModuleContext context = new(controller, () => path, navigator);
        context.SetGameEvents(demo.AllGameEvents);
        context.SetRoster(demo.Players.Values.Select(p => new PlayerRosterEntry
        {
            Slot = p.Slot,
            Name = p.Name,
            SteamId = p.SteamId64
        }));

        return (demo, controller, context);
    }
}
