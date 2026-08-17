#region

using Cs2VideoGenerator.Core.Models;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="InboundLogic.Decide" /> unit battery — the pure mirroring map the
///     UI-coupled <see cref="InboundSync" /> applies. Origin gating, demo-change preemption,
///     end-as-pause, remote-seek distance, and pause/play mirroring.
/// </summary>
[Category("Unit")]
public class InboundLogicTests
{
    private static DemoState State(
        DemoStateOrigin origin = DemoStateOrigin.User,
        bool? isPlaying = true,
        bool? isPaused = null,
        int? demoTick = null,
        string? demoFilePath = null) =>
        new(isPlaying, isPaused, demoTick, demoFilePath, null, null, origin);

    [Test]
    public async Task HostOrigin_IsAlwaysNone()
    {
        InboundLogic.Decision decision = InboundLogic.Decide(
            State(DemoStateOrigin.HostCommand, isPaused: true, demoTick: 9000, demoFilePath: "/x/other.dem"),
            true, 5000, true, "/demos/a.dem");

        await Assert.That(decision.IsNone).IsTrue();

        InboundLogic.Decision unknown = InboundLogic.Decide(
            State(DemoStateOrigin.Unknown, isPaused: true),
            true, 0, true, "/demos/a.dem");
        await Assert.That(unknown.IsNone).IsTrue();
    }

    [Test]
    public async Task DemoChange_PreemptsEverythingElse_UnderDemoIdentity()
    {
        InboundLogic.Decision decision = InboundLogic.Decide(
            State(isPaused: true, demoTick: 9000, demoFilePath: "/cs2/replays/other.dem"),
            true, 5000, true, "/demos/a.dem");

        await Assert.That(decision.DemoChangedPath).IsEqualTo("/cs2/replays/other.dem");
        await Assert.That(decision.SeekToTick).IsNull();
        await Assert.That(decision.SetPlaying).IsNull();

        // Same basename (different root) is the SAME demo — no change offer, pause mirrors.
        InboundLogic.Decision sameDemo = InboundLogic.Decide(
            State(isPaused: true, demoFilePath: "/cs2/replays/a.dem"),
            true, 0, true, "/demos/a.dem");
        await Assert.That(sameDemo.DemoChangedPath).IsNull();
        await Assert.That(sameDemo.SetPlaying).IsEqualTo(false);

        // Without the capability the path is not trustable — never a change offer.
        InboundLogic.Decision noCap = InboundLogic.Decide(
            State(demoFilePath: "/cs2/replays/other.dem"),
            false, 0, false, "/demos/a.dem");
        await Assert.That(noCap.DemoChangedPath).IsNull();
    }

    [Test]
    public async Task DemoEnd_MirrorsAsPause()
    {
        InboundLogic.Decision decision = InboundLogic.Decide(
            State(isPlaying: false),
            true, null, true, "/demos/a.dem");

        await Assert.That(decision.SetPlaying).IsEqualTo(false);
        await Assert.That(decision.SeekToTick).IsNull();
    }

    [Test]
    public async Task RemoteSeek_OnlyBeyondDistance_AndPausePlayMirror()
    {
        // Far tick → seek; user pause while DV plays → pause too.
        InboundLogic.Decision far = InboundLogic.Decide(
            State(isPaused: true, demoTick: 9000),
            true, 5000, true, "/demos/a.dem");
        await Assert.That(far.SeekToTick).IsEqualTo(9000);
        await Assert.That(far.SetPlaying).IsEqualTo(false);

        // Near tick → no seek; states already agree → no toggle.
        InboundLogic.Decision near = InboundLogic.Decide(
            State(isPaused: false, demoTick: 505),
            true, 5, true, "/demos/a.dem");
        await Assert.That(near.IsNone).IsTrue();

        // The threshold boundary itself: exactly the distance is near; one past is a seek.
        InboundLogic.Decision atThreshold = InboundLogic.Decide(
            State(isPaused: false, demoTick: 700),
            true, InboundLogic.RemoteSeekDistance,
            true, "/demos/a.dem");
        await Assert.That(atThreshold.SeekToTick).IsNull();
        InboundLogic.Decision pastThreshold = InboundLogic.Decide(
            State(isPaused: false, demoTick: 700),
            true, InboundLogic.RemoteSeekDistance + 1,
            true, "/demos/a.dem");
        await Assert.That(pastThreshold.SeekToTick).IsEqualTo(700);

        // User resumed while DV paused → play.
        InboundLogic.Decision resume = InboundLogic.Decide(
            State(isPaused: false, demoTick: 500),
            false, 0, true, "/demos/a.dem");
        await Assert.That(resume.SetPlaying).IsEqualTo(true);
    }

    [Test]
    public async Task IsPaused_IsUntrusted_WithoutEnginePauseDetection()
    {
        // A mixed build (demo-state-events without engine-pause-detection): IsPaused reads an
        // unvalidated vtable slot there — the pause/play mirror must stand down (the same gate
        // SyncEngine.NotifyTick applies); tick-silence inference carries the pause instead.
        InboundLogic.Decision gated = InboundLogic.Decide(
            State(isPaused: true, demoTick: 500),
            true, 0, true, "/demos/a.dem",
            false);
        await Assert.That(gated.SetPlaying).IsNull();

        // The demo-END signal (IsPlayingDemo=false) is a transition fact, not the pause slot —
        // it mirrors as pause regardless of the token.
        InboundLogic.Decision ended = InboundLogic.Decide(
            State(isPlaying: false),
            true, null, true, "/demos/a.dem",
            false);
        await Assert.That(ended.SetPlaying).IsEqualTo(false);
    }

    // ── ClassifyTickAdvance — the pump's per-tick jump/restart decision ────────

    [Test]
    public async Task TickAdvance_NormalAdvance_NoContext_OrOwnSeek_AreNone()
    {
        // Normal cadence advance.
        await Assert.That(InboundLogic.ClassifyTickAdvance(1001, 1000, false,
            false)).IsEqualTo(InboundLogic.TickSignal.None);

        // First observed tick — no previous context, nothing to classify.
        await Assert.That(InboundLogic.ClassifyTickAdvance(5000, null, false,
            true)).IsEqualTo(InboundLogic.TickSignal.None);

        // A huge jump while OUR seek is in flight is our own seek arriving, never a user's.
        await Assert.That(InboundLogic.ClassifyTickAdvance(9000, 100, true,
            true)).IsEqualTo(InboundLogic.TickSignal.None);
    }

    [Test]
    public async Task TickAdvance_JumpBeyondThreshold_IsUserSeek_OnEveryVersion()
    {
        // User seeks never emit a DemoStateEvent — the tick jump is the one wire signal,
        // v1.0 and v1.1 alike. Boundary: exactly the threshold is cadence, one past is a seek.
        foreach (bool v11 in (bool[])[false, true])
        {
            await Assert.That(InboundLogic.ClassifyTickAdvance(
                    1000 + InboundLogic.RemoteSeekJump, 1000, false, v11))
                .IsEqualTo(InboundLogic.TickSignal.None);
            await Assert.That(InboundLogic.ClassifyTickAdvance(
                    1000 + InboundLogic.RemoteSeekJump + 1, 1000, false, v11))
                .IsEqualTo(InboundLogic.TickSignal.UserSeek);

            // Backward jumps landing ABOVE the restart ceiling are user seeks on both versions.
            await Assert.That(InboundLogic.ClassifyTickAdvance(500, 9000, false, v11))
                .IsEqualTo(InboundLogic.TickSignal.UserSeek);
        }
    }

    [Test]
    public async Task TickAdvance_BackwardJumpToNearZero_RestartOnV10_UserSeekOnV11()
    {
        // v1.0: pause/end/reload all restart the stream near 0 — indistinguishable from a
        // seek-to-start, so the honest answer is Degraded-unknown, never a guess.
        await Assert.That(InboundLogic.ClassifyTickAdvance(10, 9000, false, false))
            .IsEqualTo(InboundLogic.TickSignal.DemoStateUnknown);

        // v1.1: a real restart emits stop/start DemoStateEvents, so a bare near-zero jump can
        // only be a user seek — it must be applied, not swallowed.
        await Assert.That(InboundLogic.ClassifyTickAdvance(10, 9000, false, true))
            .IsEqualTo(InboundLogic.TickSignal.UserSeek);
    }

    [Test]
    public async Task TickAdvance_LowTicksWithoutABackwardJump_AreNeverARestart()
    {
        // The old high-water heuristic's failure mode: a fresh demo ticking 1, 2, … (after a
        // within-session demo change, or after our own confirmed seek back to the start) must
        // read as normal cadence — spurious Degraded here wedged v1.0 sync until tick 64.
        await Assert.That(InboundLogic.ClassifyTickAdvance(2, 1, false, false))
            .IsEqualTo(InboundLogic.TickSignal.None);
        await Assert.That(InboundLogic.ClassifyTickAdvance(64, 40, false, false))
            .IsEqualTo(InboundLogic.TickSignal.None);

        // A small backward step near 0 (jitter) is not a restart either — the backward jump
        // must exceed the seek-jump threshold.
        await Assert.That(InboundLogic.ClassifyTickAdvance(10, 100, false, false))
            .IsEqualTo(InboundLogic.TickSignal.None);
    }

    // ── ShouldInferPause — the v1.0 tick-silence watchdog fallback ─────────────

    [Test]
    public async Task InferPause_RequiresV10_Following_AndSilencePastTheWindow()
    {
        TimeSpan past = InboundLogic.TickSilenceWindow + TimeSpan.FromMilliseconds(50);
        TimeSpan within = InboundLogic.TickSilenceWindow - TimeSpan.FromMilliseconds(50);

        await Assert.That(InboundLogic.ShouldInferPause(
            false, true, past)).IsTrue();

        // v1.1 engine-pause-detection carries the truth per tick — never infer.
        await Assert.That(InboundLogic.ShouldInferPause(
            true, true, past)).IsFalse();

        // Holding: silence is expected, not evidence.
        await Assert.That(InboundLogic.ShouldInferPause(
            false, false, past)).IsFalse();

        // Inside the window: not yet.
        await Assert.That(InboundLogic.ShouldInferPause(
            false, true, within)).IsFalse();
    }
}
