#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Key routing, the regression suite. A tunneling handler is the only way transport keys can beat a
///     focused control inside the playback surface, and it is also the only way to silently break a text
///     field, so all three focus states are pinned here.
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DKeyRoutingTests
{
    [Test]
    public async Task SpaceOverFocusedCheckbox_TogglesPlay_NotTheCheckbox()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            // The overlay toggles ship CLOSED (they were once "always displayed, taking up screen
            // area"), so the hazard has to be set up rather than assumed: the check boxes are still
            // FOCUSABLE and still inside the tunnel, which is exactly what this test exists to prove.
            vm.IsOverlayBarOpen = true;
            Playback2DTimelineHarness.Pump();

            CheckBox radar = view.GetVisualDescendants().OfType<CheckBox>()
                .First(c => c.Content as string == "Radar");
            bool focused = radar.Focus();
            Playback2DTimelineHarness.Pump();
            await Assert.That(focused).IsTrue()
                .Because("a non-focusable check box would retire the hazard instead of proving the "
                         + "tunnelling handler still covers it");

            bool before = vm.ShowRadar;
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(ctx.PlayCount).IsEqualTo(1);
            await Assert.That(vm.ShowRadar).IsEqualTo(before);
        });
    }

    [Test]
    public async Task ArrowKeys_DoNotChangeListBoxSelection()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            ListBox cards = view.GetVisualDescendants().OfType<ListBox>().First();

            // The shipped list is not focusable (its containers are Focusable=False), so make it focusable
            // here on purpose: the assertion has to hold for the WORST case, not only for the one the
            // template happens to prevent today.
            cards.Focusable = true;
            bool focused = cards.Focus();
            vm.NotifyFollowSlotChanged(0);
            Playback2DTimelineHarness.Pump();

            PlayerAttributes? selected = vm.SelectedPlayer;
            await Assert.That(focused).IsTrue();
            await Assert.That(selected).IsNotNull();

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            // ↓ is the speed key, not a list navigation key.
            await Assert.That(vm.SelectedPlayer).IsSameReferenceAs(selected);
            await Assert.That(ctx.Speeds).IsNotEmpty();
        });
    }

    [Test]
    public async Task TextBoxFocused_KeysAreNotIntercepted()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);

            // Inject a text field INSIDE the tunnel's subtree — outside it the guard would never be
            // reached, so an in-subtree field is the only form of this test that proves anything.
            TextBox field = new()
            {
                Width = 120,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ((Grid)view.Content!).Children.Add(field);
            Playback2DTimelineHarness.Pump();

            field.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(ctx.PlayCount).IsEqualTo(0);
            await Assert.That(ctx.SeekFrames).IsEmpty();
        });
    }

    [Test]
    public async Task EscapeClearsFollowAndRefits()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Push(1, 2);
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            Playback2DViewport viewport = Playback2DTimelineHarness.Viewport(view);

            vm.NotifyFollowSlotChanged(1);
            Playback2DTimelineHarness.Pump();
            await Assert.That(viewport.FollowSlot).IsEqualTo(1);
            await Assert.That(viewport.Mode).IsEqualTo(CameraMode.FollowPlayer);

            view.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
            await Assert.That(viewport.FollowSlot).IsEqualTo(-1);
            await Assert.That(viewport.Mode).IsEqualTo(CameraMode.Fit);
        });
    }

    [Test]
    public async Task RoundAndKillKeys_RouteThroughRequestEvent()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.Shift);
            Playback2DTimelineHarness.Pump();

            await Assert.That(ctx.NextEvents.Count).IsEqualTo(2);
            await Assert.That(ctx.NextEvents[0][0]).IsEqualTo("round_freeze_end");
            await Assert.That(ctx.NextEvents[1][0]).IsEqualTo("player_death");
        });
    }
}
