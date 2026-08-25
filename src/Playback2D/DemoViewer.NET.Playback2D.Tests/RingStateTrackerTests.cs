#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Gates for the event-driven ring-colour state machine + per-slot delta cache. Pure /
///     deterministic — no Avalonia, no demo. The reset-on-backward-seek case is the load-bearing
///     correctness gate: a backward jump must not manufacture a false flash off a stale prior sample.
/// </summary>
public class RingStateTrackerTests
{
    [Test]
    public async Task FirstObservation_SeedsBaseline_NoFlash()
    {
        RingStateTracker t = new();
        // Health 100, shots 0 — the very first sample of a slot never flashes (no prior sample yet).
        (RingState state, _) = t.Evaluate(0, 0, true, 0,
            100, 0);
        await Assert.That(state).IsEqualTo(RingState.Team);
    }

    [Test]
    public async Task HealthDecrease_FlashesTakingDamage_ThenDecays()
    {
        RingStateTracker t = new(4);
        t.Evaluate(0, 0, true, 0, 100, 0); // baseline
        (RingState hit, _) = t.Evaluate(0, 1, true, 0, 80, 0); // -20 hp
        await Assert.That(hit).IsEqualTo(RingState.TakingDamage);

        // Within the decay window it stays lit.
        (RingState still, _) = t.Evaluate(0, 2, true, 0, 80, 0);
        await Assert.That(still).IsEqualTo(RingState.TakingDamage);

        // Past the decay window it returns to team.
        (RingState back, _) = t.Evaluate(0, 10, true, 0, 80, 0);
        await Assert.That(back).IsEqualTo(RingState.Team);
    }

    [Test]
    public async Task ShotsIncrease_FlashesShooting()
    {
        RingStateTracker t = new();
        t.Evaluate(0, 0, true, 0, 100, 5);
        (RingState shoot, _) = t.Evaluate(0, 1, true, 0, 100, 7); // shots 5 → 7
        await Assert.That(shoot).IsEqualTo(RingState.Shooting);
    }

    [Test]
    public async Task Precedence_DeadBeatsBlindBeatsDamageBeatsShoot()
    {
        RingStateTracker t = new();
        t.Evaluate(0, 0, true, 0, 100, 0);

        // Simultaneous damage + shots + flash, but DEAD wins.
        (RingState dead, _) = t.Evaluate(0, 1, false, 2, 0, 5);
        await Assert.That(dead).IsEqualTo(RingState.Dead);

        // Alive + blinded + damage + shots → BLINDED wins over the flash states.
        t.Reset();
        t.Evaluate(1, 0, true, 0, 100, 0);
        (RingState blind, double alpha) = t.Evaluate(1, 1, true, 2, 50, 9);
        await Assert.That(blind).IsEqualTo(RingState.Blinded);
        await Assert.That(alpha).IsGreaterThan(0);

        // Alive, no flash, damage + shots → TAKING DAMAGE wins over shooting.
        t.Reset();
        t.Evaluate(2, 0, true, 0, 100, 0);
        (RingState dmg, _) = t.Evaluate(2, 1, true, 0, 90, 3);
        await Assert.That(dmg).IsEqualTo(RingState.TakingDamage);
    }

    [Test]
    public async Task BackwardSeek_Reset_PreventsFalseFlash()
    {
        RingStateTracker t = new(4);

        // Forward: at frame 50 the player has 30 hp / 20 shots.
        t.Evaluate(0, 50, true, 0, 30, 20);

        // Backward seek to frame 10 where the player had FULL hp / 0 shots. WITHOUT a reset the tracker
        // would see health 30→100 (no flash, fine) but shots 20→0 (decrease, also no flash) — the real
        // hazard is the OPPOSITE direction: seeking back to a LOWER-shots / HIGHER-hp state then forward
        // again. So the contract is: after Reset(), the next sample is a fresh baseline and never flashes.
        t.Reset();
        (RingState afterReset, _) = t.Evaluate(0, 10, true, 0, 100, 0);
        await Assert.That(afterReset).IsEqualTo(RingState.Team);

        // And a subsequent genuine decrease from the new baseline DOES flash (cache is functional again).
        (RingState afterDmg, _) = t.Evaluate(0, 11, true, 0, 70, 0);
        await Assert.That(afterDmg).IsEqualTo(RingState.TakingDamage);
    }

    [Test]
    public async Task WithoutReset_BackwardJump_WouldFalseFlash_DemonstratingWhyResetMatters()
    {
        RingStateTracker t = new(4);

        // Player at frame 50 has 100 hp. Seek back to frame 10 where they had only 40 hp — WITHOUT a
        // reset the tracker sees 100→40 as a decrease and spuriously flashes damage. Exactly the
        // false flash a backward seek must never produce; the VM guards it by calling Reset() on a
        // backward FrameIndex.
        t.Evaluate(0, 50, true, 0, 100, 0);
        (RingState falseFlash, _) = t.Evaluate(0, 10, true, 0, 40, 0);
        await Assert.That(falseFlash).IsEqualTo(RingState.TakingDamage); // the bug the reset prevents

        // With the reset between the two samples, no false flash (the positive control).
        RingStateTracker guarded = new(4);
        guarded.Evaluate(0, 50, true, 0, 100, 0);
        guarded.Reset();
        (RingState clean, _) = guarded.Evaluate(0, 10, true, 0, 40, 0);
        await Assert.That(clean).IsEqualTo(RingState.Team);
    }

    [Test]
    public async Task Blinded_AlphaScalesWithRemainingFlash()
    {
        RingStateTracker t = new();
        t.Evaluate(0, 0, true, 0, 100, 0);

        (_, double low) = t.Evaluate(0, 1, true, 0.3f, 100, 0);
        (_, double high) = t.Evaluate(0, 2, true, 2.5f, 100, 0);
        await Assert.That(high).IsGreaterThan(low);
    }
}
