#region

using Avalonia.Controls;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Follow-by-card end to end through the real control tree: the ListBox selection, the followed
///     treatment on exactly one card, and the viewport mirror. The pure-VM funnel tests cover the LiveSync
///     hop; this covers the wiring between them.
/// </summary>
[NotInParallel]
public class Playback2DFollowCardRenderTests
{
    [Test]
    public async Task SelectingCard_HighlightsExactlyOneCard_AndSetsViewportFollowSlot()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Push(1, 2);
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            Playback2DViewport viewport = Playback2DTimelineHarness.Viewport(view);

            ListBox cards = view.GetVisualDescendants().OfType<ListBox>().First();
            await Assert.That(cards.ItemCount).IsEqualTo(vm.Attributes.Count);

            cards.SelectedItem = vm.Attributes.First(a => a.Slot == 2);
            Playback2DTimelineHarness.Pump();

            await Assert.That(vm.FollowedSlot).IsEqualTo(2);
            await Assert.That(vm.Attributes.Count(a => a.IsFollowed)).IsEqualTo(1);
            await Assert.That(viewport.FollowSlot).IsEqualTo(2);
            await Assert.That(viewport.Mode).IsEqualTo(CameraMode.FollowPlayer);
            await Assert.That(ctx.SpectateTargets).Contains(2);

            // Exactly one card carries the followed class — the treatment must not smear across the panel.
            int followed = view.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("followed"));
            Console.WriteLine($"[follow-card] followed borders={followed}");
            await Assert.That(followed).IsEqualTo(1);
        });
    }

    [Test]
    public async Task CardList_IsDisabledWhenTheFollowGateIsOff()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            (Window _, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            ListBox cards = view.GetVisualDescendants().OfType<ListBox>().First();
            await Assert.That(cards.IsEnabled).IsTrue();

            ctx.Gate!.SetEnabled("playback2d.follow", false);
            Playback2DTimelineHarness.Pump();

            await Assert.That(cards.IsEnabled).IsFalse();
        });
    }
}
