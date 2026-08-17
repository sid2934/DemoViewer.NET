#region

using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.GameEvents;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Real-demo proof for the <see cref="SemanticNavigator" /> precompute
///     and the game-event fix. Built on the SYNCHRONOUS parse path (parse + build the
///     navigator from the parsed frame list), so no headless dispatcher pumping is involved — the
///     navigator is a pure VM driven by a <see cref="PlaybackController" /> over a real frame list.
///     <para>
///         Verifies (1) Next/Prev round / event / tick land exactly on the precomputed boundary frames,
///         (2) the boundary indices the navigator reports actually contain the boundary they claim, and
///         (3) the demo contains a game event that the OLD hardcoded 7-event list omitted and the
///         navigator can now jump to it — the concrete proof the game-event fix works on real data.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class SemanticNavigatorRealDemoTests
{
    // The legacy hardcoded special-seek list baked into SeekControlsViewModel.
    private static readonly string[] _legacyHardcodedEvents =
    [
        "player_death", "round_start", "round_end",
        "bomb_planted", "bomb_defused", "player_hurt", "weapon_fire"
    ];

    [Test]
    public async Task RealDemo_NavigatorBoundaries_AreCorrectAndReachable()
    {
        string path = DemoTestHelper.RequireDemo();

        // ── Synchronous heavy work, off any UI thread. ──
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        await Assert.That(frames.Count).IsGreaterThan(100);

        PlaybackController controller = new();
        controller.LoadDemo(frames, demo.TickRate > 0 ? demo.TickRate : 64);

        SemanticNavigator navigator = new(controller);
        navigator.Build(frames);

        // The demo must have rounds and game events for this to mean anything.
        await Assert.That(navigator.RoundBoundaryFrames.Count).IsGreaterThan(1);
        await Assert.That(navigator.EventBoundaryFramesByName.Count).IsGreaterThan(0);
        await Assert.That(navigator.TickBoundaryFrames.Count).IsGreaterThan(1);

        // ── (1) Every precomputed round-boundary frame really contains a round_* event. ──
        foreach (int fi in navigator.RoundBoundaryFrames)
        {
            bool hasRound = frames[fi].InnerMessages.Any(m =>
                m is GameEventMessage gem &&
                gem.DecodedEvent.Name.StartsWith("round_", StringComparison.OrdinalIgnoreCase));
            await Assert.That(hasRound).IsTrue();
        }

        // Round boundaries are sorted strictly ascending (de-duped).
        for (int i = 1; i < navigator.RoundBoundaryFrames.Count; i++)
        {
            await Assert.That(navigator.RoundBoundaryFrames[i]).IsGreaterThan(navigator.RoundBoundaryFrames[i - 1]);
        }

        // ── (2) NextRound from start lands on the FIRST round boundary; symmetric back-step. ──
        controller.SeekToFrame(0);
        navigator.NextRound();
        int firstRound = navigator.RoundBoundaryFrames.First(f => f > 0);
        // From frame 0, NextRound finds the first boundary strictly > 0. If frame 0 itself is a round
        // boundary it is correctly skipped; assert the landing is a real round boundary > 0.
        await Assert.That(navigator.RoundBoundaryFrames).Contains(controller.CurrentFrameIndex);
        await Assert.That(controller.CurrentFrameIndex).IsGreaterThan(0);

        int afterFirstNext = controller.CurrentFrameIndex;
        navigator.NextRound();
        int afterSecondNext = controller.CurrentFrameIndex;
        await Assert.That(afterSecondNext).IsGreaterThan(afterFirstNext);

        navigator.PrevRound();
        await Assert.That(controller.CurrentFrameIndex).IsEqualTo(afterFirstNext);

        // ── (3) NextTick / PrevTick land on the precomputed tick boundaries, symmetric. ──
        controller.SeekToFrame(0);
        navigator.NextTick();
        int t1 = controller.CurrentFrameIndex;
        await Assert.That(navigator.TickBoundaryFrames).Contains(t1);
        navigator.NextTick();
        int t2 = controller.CurrentFrameIndex;
        await Assert.That(t2).IsGreaterThan(t1);
        navigator.PrevTick();
        await Assert.That(controller.CurrentFrameIndex).IsEqualTo(t1);

        // ── (3b) PrevTick from MID-GROUP moves to a strictly EARLIER tick (not the same group's
        // start). Seed a frame that is inside a tick group but is NOT itself a boundary, then assert
        // the landed frame's ServerTick is strictly less than where we started. This is the case the
        // boundary→next→prev round-trip above cannot exercise. ──
        int[] tickBoundaries = navigator.TickBoundaryFrames.ToArray();
        int midGroupFrame = -1;
        for (int b = 0; b < tickBoundaries.Length - 1; b++)
        {
            // A group with at least two frames has a non-boundary interior frame at start+1.
            if (tickBoundaries[b + 1] - tickBoundaries[b] >= 2 && tickBoundaries[b] > 0)
            {
                midGroupFrame = tickBoundaries[b] + 1;
                break;
            }
        }

        await Assert.That(midGroupFrame).IsGreaterThan(0);
        controller.SeekToFrame(midGroupFrame);
        int startTick = frames[midGroupFrame].ServerTick;
        navigator.PrevTick();
        int landedTick = frames[controller.CurrentFrameIndex].ServerTick;
        await Assert.That(landedTick).IsLessThan(startTick);
        // And it landed on an actual tick-boundary (a group start), not an arbitrary frame.
        await Assert.That(navigator.TickBoundaryFrames).Contains(controller.CurrentFrameIndex);

        // ── (4) Filtered NextEvent lands on a frame that actually contains that event. ──
        // Pick the most-frequent event name as a stable, demo-present target.
        string targetEvent = navigator.EventBoundaryFramesByName
            .OrderByDescending(kv => kv.Value.Length)
            .First().Key;
        controller.SeekToFrame(0);
        navigator.NextEvent([targetEvent]);
        int eventFrame = controller.CurrentFrameIndex;
        bool landedOnTarget = frames[eventFrame].InnerMessages.Any(m =>
            m is GameEventMessage gem &&
            string.Equals(gem.DecodedEvent.Name, targetEvent, StringComparison.OrdinalIgnoreCase));
        await Assert.That(landedOnTarget).IsTrue();
        await Assert.That(navigator.EventBoundaryFramesByName[targetEvent]).Contains(eventFrame);

        // PrevEvent from the end of that array lands on the prior occurrence.
        int[] targetFrames = navigator.EventBoundaryFramesByName[targetEvent];
        if (targetFrames.Length >= 2)
        {
            controller.SeekToFrame(targetFrames[^1]);
            navigator.PrevEvent([targetEvent]);
            await Assert.That(controller.CurrentFrameIndex).IsEqualTo(targetFrames[^2]);
        }

        // ── (5) THE GAME-EVENT FIX: an event the old hardcoded list OMITTED is now reachable. ──
        // The demo-derived precompute knows every event the demo actually has. Find one that the
        // legacy SeekControls list could never offer, and prove NextEvent jumps to it.
        string? omitted = navigator.EventBoundaryFramesByName.Keys
            .FirstOrDefault(name => !_legacyHardcodedEvents.Contains(name, StringComparer.OrdinalIgnoreCase));

        // Real CS2 demos always carry events outside the 7-event list (e.g. player_spawn,
        // begin_new_match, cs_round_start, item_pickup, …). If this demo somehow didn't, the fix is
        // vacuously satisfied — but assert non-null so a regression in the precompute surfaces.
        await Assert.That(omitted).IsNotNull();

        controller.SeekToFrame(0);
        navigator.NextEvent([omitted!]);
        int omittedFrame = controller.CurrentFrameIndex;
        bool reachedOmitted = frames[omittedFrame].InnerMessages.Any(m =>
            m is GameEventMessage gem &&
            string.Equals(gem.DecodedEvent.Name, omitted, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"[nav] demo={Path.GetFileName(path)} rounds={navigator.RoundBoundaryFrames.Count} " +
                          $"ticks={navigator.TickBoundaryFrames.Count} eventNames={navigator.EventBoundaryFramesByName.Count} " +
                          $"omittedEvent='{omitted}' reachedAtFrame={omittedFrame}");

        await Assert.That(reachedOmitted).IsTrue();
        // And it was genuinely unreachable under the old list.
        await Assert.That(_legacyHardcodedEvents).DoesNotContain(omitted!);

        // ── (6) "Deselect all = match any": null/empty filter finds the next event of ANY type. ──
        controller.SeekToFrame(0);
        navigator.NextEvent(null);
        int anyFrame = controller.CurrentFrameIndex;
        bool anyHasEvent = frames[anyFrame].InnerMessages.Any(m => m is GameEventMessage);
        await Assert.That(anyHasEvent).IsTrue();
    }
}
