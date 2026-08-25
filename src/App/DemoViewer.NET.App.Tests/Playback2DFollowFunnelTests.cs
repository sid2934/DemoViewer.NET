#region

using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The single follow funnel. The load-bearing test here is
///     <see cref="SelectingCard_CallsNotifySpectateTarget" />: that hop is what
///     <c>SyncStateObserver.OnSpectateTargetChanged</c> consumes to drive CS2's
///     <c>SetDesiredSpectator</c>, and a card-selection path that forgot it would look perfectly correct in
///     the 2D view while doing nothing in-game.
/// </summary>
public class Playback2DFollowFunnelTests
{
    [Test]
    public async Task SelectingCard_RaisesFollowSlotChangedOnce()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DActionDispatchTests.Activated();

        List<int> raised = [];
        vm.FollowSlotChanged += raised.Add;

        vm.SelectedPlayer = vm.Attributes.First(a => a.Slot == 1);

        int[] expected = [1];
        await Assert.That(raised).IsEquivalentTo(expected);
    }

    [Test]
    public async Task SelectingCard_CallsNotifySpectateTarget()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActionDispatchTests.Activated();

        vm.SelectedPlayer = vm.Attributes.First(a => a.Slot == 2);

        int[] expected = [2];
        await Assert.That(ctx.SpectateTargets).IsEquivalentTo(expected);
    }

    [Test]
    public async Task MenuPickAndCardPick_TakeTheSameFunnel()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActionDispatchTests.Activated();

        // The camera-mode SplitButton submenu calls NotifyFollowSlotChanged directly.
        vm.NotifyFollowSlotChanged(1);
        int slotAfterMenu = vm.FollowedSlot;
        object? selectedAfterMenu = vm.SelectedPlayer;
        string statusAfterMenu = vm.FollowStatus;

        vm.NotifyFollowSlotChanged(-1);
        ctx.SpectateTargets.Clear();

        // The card list goes through SelectedPlayer.
        vm.SelectedPlayer = vm.Attributes.First(a => a.Slot == 1);

        await Assert.That(vm.FollowedSlot).IsEqualTo(slotAfterMenu);
        await Assert.That(vm.SelectedPlayer).IsSameReferenceAs(selectedAfterMenu);
        await Assert.That(vm.FollowStatus).IsEqualTo(statusAfterMenu);
        int[] expectedSpectate = [1];
        await Assert.That(ctx.SpectateTargets).IsEquivalentTo(expectedSpectate);
    }

    [Test]
    public async Task FollowedSlot_SetsIsFollowedOnExactlyOneRow()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DActionDispatchTests.Activated();

        vm.NotifyFollowSlotChanged(1);

        await Assert.That(vm.Attributes.Count(a => a.IsFollowed)).IsEqualTo(1);
        await Assert.That(vm.Attributes.First(a => a.IsFollowed).Slot).IsEqualTo(1);

        vm.NotifyFollowSlotChanged(2);

        await Assert.That(vm.Attributes.Count(a => a.IsFollowed)).IsEqualTo(1);
        await Assert.That(vm.Attributes.First(a => a.IsFollowed).Slot).IsEqualTo(2);
    }

    [Test]
    public async Task ClearFollow_ResetsEveryIsFollowed()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DActionDispatchTests.Activated();
        vm.NotifyFollowSlotChanged(0);

        vm.ClearFollow();

        await Assert.That(vm.Attributes.Any(a => a.IsFollowed)).IsFalse();
        await Assert.That(vm.SelectedPlayer).IsNull();
        await Assert.That(vm.FollowStatus).IsEqualTo("");
    }

    [Test]
    public async Task FollowStatus_SaysRequested_NeverConfirmed()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DActionDispatchTests.Activated();

        vm.NotifyFollowSlotChanged(0);

        await Assert.That(vm.FollowStatus).Contains("requested");
        await Assert.That(vm.FollowStatus).Contains("Alpha");
        await Assert.That(vm.FollowStatus.Contains("confirm", StringComparison.OrdinalIgnoreCase)).IsFalse();

        // The timeline footer shows the same string, so the wording cannot drift between the two surfaces.
        await Assert.That(vm.Timeline.FollowStatus).IsEqualTo(vm.FollowStatus);
    }

    [Test]
    public async Task StraySelectionNull_KeepsARetainedFollow()
    {
        // The view is rebuilt on every tab activation, and its ListBox writes a transient null back through
        // the two-way SelectedItem binding while it re-templates. That must not silently drop follow state
        // the VM is holding across the deactivation.
        (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DActionDispatchTests.Activated();
        vm.NotifyFollowSlotChanged(1);

        bool refit = false;
        vm.FitRequested += () => refit = true;

        vm.SelectedPlayer = null;

        await Assert.That(vm.FollowedSlot).IsEqualTo(1);
        await Assert.That(vm.SelectedPlayer?.Slot).IsEqualTo(1);
        await Assert.That(vm.Attributes.Count(a => a.IsFollowed)).IsEqualTo(1);
        await Assert.That(refit).IsFalse();
    }

    [Test]
    public async Task SelectionNull_AfterTheRowLeavesTheRoster_ClearsTheFollow()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DActionDispatchTests.Activated();
        vm.NotifyFollowSlotChanged(1);

        vm.Attributes.Clear();
        vm.SelectedPlayer = null;

        await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
        await Assert.That(vm.SelectedPlayer).IsNull();
    }

    [Test]
    public async Task GateTurningOff_ClearsAnExistingFollow()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActionDispatchTests.Activated();
        vm.NotifyFollowSlotChanged(0);

        ctx.Gate!.SetEnabled("playback2d.follow", false);

        await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
        await Assert.That(vm.Attributes.Any(a => a.IsFollowed)).IsFalse();
    }
}
