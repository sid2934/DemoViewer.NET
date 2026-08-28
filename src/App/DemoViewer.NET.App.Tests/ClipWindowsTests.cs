#region

using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Clip-window math battery. The two table cases the design
///     REQUIRES: <c>TickOffset ≠ 0</c> (every clamp must happen in the frame clock before the
///     offset applies once) and a mid-match demo (<c>ServerStartTick ≠ 0</c> — highlight ticks
///     are already frame clock, so NOTHING may subtract it; the math never sees it at all).
///     <para>
///         The math itself moved into <c>CS2DemoKit.Analysis.Clips</c> for packaging; this
///         battery stayed behind deliberately, unchanged apart from the <c>using</c> and the
///         cache-row → <see cref="ClipRound" /> projection, as the App-side proof that the extraction
///         is behaviour-identical.
///     </para>
/// </summary>
public class ClipWindowsTests
{
    // 64 tick, lead-in 15 s (960 ticks), lead-out 5 s (320 ticks).
    private const int Rate = 64;

    [Test]
    public async Task Compute_PadsAroundEvent_InFrameClock()
    {
        (long start, long end) = ClipWindows.Compute(
            5000, null, Rate,
            15, 5, 100_000);

        await Assert.That(start).IsEqualTo(5000 - 960);
        await Assert.That(end).IsEqualTo(5000 + 320);
    }

    [Test]
    public async Task Compute_RoundStartFloorsLeadIn_AndDemoStartClamps()
    {
        // Round started 200 ticks before the event — the lead-in must not reach the prior round.
        (long start, _) = ClipWindows.Compute(5000, 4800, Rate, 15, 5, 100_000);
        await Assert.That(start).IsEqualTo(4800);

        // Event near demo start — clamps at 0, never negative.
        (long early, _) = ClipWindows.Compute(100, null, Rate, 15, 5, 100_000);
        await Assert.That(early).IsEqualTo(0);
    }

    [Test]
    public async Task Compute_ClipStartPullsStartBack_ButRoundStartStillFloors()
    {
        // A 4K spanning 20 s: fires at the completing kill (tick 6000), first kill at tick 4600 —
        // further back than the 15 s (960-tick) lead-in would reach (6000 − 960 = 5040). The window
        // must start at the first kill, not the lead-in.
        (long start, _) = ClipWindows.Compute(6000, null, Rate, 15, 5, 100_000, 0, 4600);
        await Assert.That(start).IsEqualTo(4600)
            .Because("the clip start reaches back to the first contributing kill, past the lead-in");

        // A clipStart INSIDE the lead-in must never shrink the window — the lead-in still wins.
        (long leadInWins, _) = ClipWindows.Compute(6000, null, Rate, 15, 5, 100_000, 0, 5500);
        await Assert.That(leadInWins).IsEqualTo(6000 - 960)
            .Because("a clipStart later than the lead-in reach must not pull the start forward");

        // The round-start floor still bounds a first-kill that sits in the prior round: round began
        // at 4800, so even a first kill recorded at 4600 cannot cross into the previous round.
        (long floored, _) = ClipWindows.Compute(6000, 4800, Rate, 15, 5, 100_000, 0, 4600);
        await Assert.That(floored).IsEqualTo(4800)
            .Because("the round-start floor applies AFTER the clip-start pull-back, bounding the clip to its round");

        // A null clipStart is byte-identical to the pre-existing lead-in-only behavior.
        (long noClip, _) = ClipWindows.Compute(6000, null, Rate, 15, 5, 100_000);
        await Assert.That(noClip).IsEqualTo(6000 - 960)
            .Because("null clipStart preserves the current behavior");

        // The offset still applies exactly once, after all clamps, with a clip-start pull-back.
        (long offsetStart, _) = ClipWindows.Compute(6000, null, Rate, 15, 5, 100_000, 500, 4600);
        await Assert.That(offsetStart).IsEqualTo(4600 + 500);
    }

    [Test]
    public async Task Compute_DemoEndClampsLeadOut()
    {
        (_, long end) = ClipWindows.Compute(99_900, null, Rate, 15, 5, 100_000);
        await Assert.That(end).IsEqualTo(100_000);
    }

    [Test]
    public async Task Compute_TickOffsetAppliesOnce_AfterAllClamps()
    {
        const int Offset = 500;

        // Plain case: both ends shift by exactly the offset.
        (long start, long end) = ClipWindows.Compute(5000, null, Rate, 15, 5, 100_000, Offset);
        await Assert.That(start).IsEqualTo(5000 - 960 + Offset);
        await Assert.That(end).IsEqualTo(5000 + 320 + Offset);

        // Clamp-at-demo-start case: the FRAME clamp lands at 0, THEN the offset applies — if the
        // offset were mixed into the clamp space the start would wrongly stick at 0.
        (long clampedStart, _) = ClipWindows.Compute(100, null, Rate, 15, 5, 100_000, Offset);
        await Assert.That(clampedStart).IsEqualTo(0 + Offset);

        // Clamp-at-demo-end case symmetrically.
        (_, long clampedEnd) = ClipWindows.Compute(99_900, null, Rate, 15, 5, 100_000, Offset);
        await Assert.That(clampedEnd).IsEqualTo(100_000 + Offset);
    }

    [Test]
    public async Task Compute_MidMatchDemo_NeverSubtractsServerStartTick()
    {
        // A mid-match GOTV recording has ServerStartTick ≈ 40_000 — but highlight ticks are
        // ALREADY frame clock (small values near demo start). The window must come out around
        // the small tick untouched; any accidental −ServerStartTick would go hugely negative
        // and clamp to 0, which this asserts against.
        (long start, long end) = ClipWindows.Compute(2000, null, Rate, 15, 5, 60_000);
        await Assert.That(start).IsEqualTo(2000 - 960);
        await Assert.That(end).IsEqualTo(2000 + 320);
    }

    [Test]
    public async Task Compute_DegenerateWindow_YieldsAtLeastOneTick()
    {
        (long start, long end) = ClipWindows.Compute(0, null, Rate, 0, 0, 0);
        await Assert.That(end).IsEqualTo(start + 1);
    }

    [Test]
    public async Task RoundStartFor_PicksLatestAtOrBefore()
    {
        List<CachedRound> rounds =
        [
            new()
            {
                Number = 1,
                StartTickFrameClock = 1000
            },
            new()
            {
                Number = 2,
                StartTickFrameClock = 8000
            },
            new()
            {
                Number = 3,
                StartTickFrameClock = 16_000
            }
        ];

        IReadOnlyList<ClipRound> projected = rounds.ToClipRounds();

        await Assert.That(ClipWindows.RoundStartFor(projected, 9000)).IsEqualTo(8000);
        await Assert.That(ClipWindows.RoundStartFor(projected, 16_000)).IsEqualTo(16_000);
        await Assert.That(ClipWindows.RoundStartFor(projected, 500)).IsNull();
    }

    [Test]
    public async Task Coalesce_MergesOverlaps_PerPlayerAndRound_AndSortsByDemoThenStart()
    {
        ClipWindows.Candidate C(string demo, string steam, int round, long s, long e, string title)
        {
            return new ClipWindows.Candidate(demo, steam, "raw", round, s, e, title);
        }

        List<ClipWindows.Clip> clips = ClipWindows.Coalesce(
        [
            // Same player+round, overlapping → one merged clip with both titles.
            C("b.dem", "76561", 7, 1000, 2000, "double"),
            C("b.dem", "76561", 7, 1500, 2600, "triple"),
            // Same player, DIFFERENT round overlapping ticks → never merged.
            C("b.dem", "76561", 8, 2500, 3000, "clutch"),
            // Different player in the same window → never merged.
            C("b.dem", "9999", 7, 1200, 1800, "other"),
            // Earlier demo path sorts first regardless of tick.
            C("a.dem", "76561", 2, 9000, 9500, "ace")
        ]);

        await Assert.That(clips.Count).IsEqualTo(4);
        await Assert.That(clips[0].DemoPath).IsEqualTo("a.dem");

        ClipWindows.Clip merged = clips.Single(c => c.Titles.Count == 2);
        await Assert.That(merged.StartTick).IsEqualTo(1000);
        await Assert.That(merged.EndTick).IsEqualTo(2600);
        await Assert.That(merged.RoundNumber).IsEqualTo(7);
        await Assert.That(clips.Count(c => c.SteamId64 == "76561" && c.RoundNumber == 8)).IsEqualTo(1);
    }

    [Test]
    public async Task Coalesce_TouchingWindows_MergeToo()
    {
        List<ClipWindows.Clip> clips = ClipWindows.Coalesce(
        [
            new ClipWindows.Candidate("a.dem", "1", "n", 1, 100, 200, "x"),
            new ClipWindows.Candidate("a.dem", "1", "n", 1, 200, 300, "y")
        ]);

        await Assert.That(clips.Count).IsEqualTo(1);
        await Assert.That(clips[0].EndTick).IsEqualTo(300);
    }
}
