#region

using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="Playback2DTabViewModel.ExecuteAction" /> against a recording context. The assertions are
///     deliberately about WHICH host call happened, not about VM fields: every keyboard mutation has to land
///     on <c>IModuleContext.Request*</c>, because that is the surface LiveSync's <c>SyncStateObserver</c>
///     watches — a VM that moved the clock itself would pass a state-based test and silently desync a
///     Synced session.
/// </summary>
public class Playback2DActionDispatchTests
{
    [Test]
    public async Task TogglePlay_CallsRequestPlayThenRequestPause()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();

        await Assert.That(vm.ExecuteAction(Playback2DAction.TogglePlay)).IsTrue();
        await Assert.That(ctx.PlayCount).IsEqualTo(1);
        await Assert.That(ctx.PauseCount).IsEqualTo(0);

        ctx.IsPlaying = true;
        vm.ExecuteAction(Playback2DAction.TogglePlay);
        await Assert.That(ctx.PauseCount).IsEqualTo(1);
    }

    [Test]
    public async Task StepForward_RequestsCurrentPlusOne()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();
        ctx.CurrentFrameIndex = 42;

        await Assert.That(vm.ExecuteAction(Playback2DAction.StepForward)).IsTrue();
        int[] expected = [43];
        await Assert.That(ctx.SeekFrames).IsEquivalentTo(expected);
    }

    [Test]
    public async Task StepBack_AtFrameZero_DoesNotRequestNegative()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();
        ctx.CurrentFrameIndex = 0;

        await Assert.That(vm.ExecuteAction(Playback2DAction.StepBack)).IsFalse();
        await Assert.That(ctx.SeekFrames).IsEmpty();
    }

    [Test]
    public async Task StepForward_AtLastFrame_DoesNotRequestPastTheEnd()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();
        ctx.TotalFrames = 100;
        ctx.CurrentFrameIndex = 99;

        await Assert.That(vm.ExecuteAction(Playback2DAction.StepForward)).IsFalse();
        await Assert.That(ctx.SeekFrames).IsEmpty();
    }

    [Test]
    public async Task SpeedUp_WalksThePresetLadder()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();

        vm.ExecuteAction(Playback2DAction.SpeedUp);
        vm.ExecuteAction(Playback2DAction.SpeedUp);
        vm.ExecuteAction(Playback2DAction.SpeedDown);

        double[] expected = [2.0, 4.0, 2.0];
        await Assert.That(ctx.Speeds).IsEquivalentTo(expected);
    }

    [Test]
    public async Task SpeedUp_WhenLocked_DoesNotRequestSpeed()
    {
        // The LiveSync interlock: a Synced session without the plugin's timescale capability pins speed,
        // and the NavStrip ComboBox is disabled for the same reason. The key must not open a side door.
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();
        ctx.IsSpeedLocked = true;

        vm.ExecuteAction(Playback2DAction.SpeedUp);

        await Assert.That(ctx.Speeds).IsEmpty();
        await Assert.That(vm.SpeedLockNote).IsNotEmpty();

        // The key is CONSUMED (it must not fall through to the card list), so the note has to reach the
        // footer or the user is left with a dead key and no reason.
        await Assert.That(vm.Timeline.SpeedLockNote).IsEqualTo(vm.SpeedLockNote);

        // ...and it clears again the moment the lock lifts.
        ctx.IsSpeedLocked = false;
        vm.ExecuteAction(Playback2DAction.SpeedUp);
        await Assert.That(vm.Timeline.SpeedLockNote).IsEqualTo("");
    }

    [Test]
    public async Task NextRound_RequestsNextEventWithFreezeEndFilter()
    {
        // Rounds OPEN at round_freeze_end, so round nav and the timeline's bands key off the same event.
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();

        vm.ExecuteAction(Playback2DAction.NextRound);
        vm.ExecuteAction(Playback2DAction.PrevRound);

        string[] expected = ["round_freeze_end"];
        await Assert.That(ctx.NextEvents.Count).IsEqualTo(1);
        await Assert.That(ctx.NextEvents[0]).IsEquivalentTo(expected);
        await Assert.That(ctx.PrevEvents[0]).IsEquivalentTo(expected);
    }

    [Test]
    public async Task NextKill_RequestsNextEventWithPlayerDeathFilter()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();

        vm.ExecuteAction(Playback2DAction.NextKill);

        string[] expected = ["player_death"];
        await Assert.That(ctx.NextEvents[0]).IsEquivalentTo(expected);
    }

    [Test]
    public async Task CycleFollowNext_WrapsAroundFollowablePlayers()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();
        ctx.Push(1, 2);

        int[] order = [.. vm.FollowablePlayers.Select(p => p.Slot)];
        await Assert.That(order.Length).IsEqualTo(3);

        vm.ExecuteAction(Playback2DAction.CycleFollowNext);
        await Assert.That(vm.FollowedSlot).IsEqualTo(order[0]);

        vm.ExecuteAction(Playback2DAction.CycleFollowNext);
        vm.ExecuteAction(Playback2DAction.CycleFollowNext);
        await Assert.That(vm.FollowedSlot).IsEqualTo(order[2]);

        vm.ExecuteAction(Playback2DAction.CycleFollowNext);
        await Assert.That(vm.FollowedSlot).IsEqualTo(order[0]);

        vm.ExecuteAction(Playback2DAction.CycleFollowPrev);
        await Assert.That(vm.FollowedSlot).IsEqualTo(order[2]);
    }

    [Test]
    public async Task ClearFollow_RaisesFitRequestedAndDoesNotNotifySpectate()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();
        ctx.Push(1, 2);
        vm.ExecuteAction(Playback2DAction.CycleFollowNext);
        ctx.SpectateTargets.Clear();

        int fits = 0;
        vm.FitRequested += () => fits++;

        await Assert.That(vm.ExecuteAction(Playback2DAction.ClearFollow)).IsTrue();

        await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
        await Assert.That(fits).IsEqualTo(1);
        await Assert.That(ctx.SpectateTargets).IsEmpty();
    }

    [Test]
    public async Task ExecuteAction_WithoutContext_ReturnsFalse()
    {
        Playback2DTabViewModel vm = new();

        await Assert.That(vm.ExecuteAction(Playback2DAction.TogglePlay)).IsFalse();
        await Assert.That(vm.ExecuteAction(Playback2DAction.NextRound)).IsFalse();
    }

    [Test]
    public async Task FollowKeys_AreInertWhenTheFollowGateIsOff()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Activated();
        ctx.Push(1, 2);
        ctx.Gate!.SetEnabled("playback2d.follow", false);

        await Assert.That(vm.IsFollowEnabled).IsFalse();
        await Assert.That(vm.ExecuteAction(Playback2DAction.CycleFollowNext)).IsFalse();
        await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
    }

    /// <param name="demoPath">
    ///     The demo the context is on, set BEFORE activation. Null (the default) is the shape every
    ///     pre-round-3A caller had. It matters because the tab's resync clears the follow target when the
    ///     path changes under it, so a test that assigns the path after activation has already staged a
    ///     demo swap without meaning to.
    /// </param>
    internal static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx) Activated(string? demoPath = null)
    {
        Playback2DFakeContext ctx = new()
        {
            Gate = new FakeModuleFeatureGate(),
            DemoPath = demoPath
        };
        ctx.AddPlayer(0, "Alpha", 2);
        ctx.AddPlayer(1, "Bravo", 2);
        ctx.AddPlayer(2, "Charlie", 3);
        ctx.Frames["round_freeze_end"] = [0, 300, 600];
        ctx.Timelines["player_death"] = [];

        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);
        return (vm, ctx);
    }
}
