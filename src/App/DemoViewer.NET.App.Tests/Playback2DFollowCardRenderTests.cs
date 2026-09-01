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
[Category("Render")]
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

            // Exactly one card carries the followed class. The treatment must not smear across the panel.
            int followed = view.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("followed"));
            Console.WriteLine($"[follow-card] followed borders={followed}");
            await Assert.That(followed).IsEqualTo(1);
        });
    }

    [Test]
    public async Task RebuiltView_ReprojectsTheRetainedFollowOntoTheFreshViewport()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // WorkspaceTabDescriptor DESTROYS the view on deactivation and rebuilds it from ViewFactory on
            // the next activation, keeping the cached VM. Without a re-projection the followed card and the
            // "requested" chip come back while the new viewport sits in Fit. The follow would look live and
            // be dead.
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Push(1, 2);
            (Window _, Playback2DView first) = Playback2DTimelineHarness.Show(vm);

            ListBox cards = first.GetVisualDescendants().OfType<ListBox>().First();
            cards.SelectedItem = vm.Attributes.First(a => a.Slot == 2);
            Playback2DTimelineHarness.Pump();
            await Assert.That(vm.FollowedSlot).IsEqualTo(2);

            // The rebuild: a brand-new view over the SAME cached view-model.
            (Window _, Playback2DView second) = Playback2DTimelineHarness.Show(vm);
            Playback2DViewport viewport = Playback2DTimelineHarness.Viewport(second);

            Console.WriteLine($"[follow-rebind] vm={vm.FollowedSlot} viewport={viewport.FollowSlot}");
            await Assert.That(viewport.FollowSlot).IsEqualTo(2);
            await Assert.That(viewport.Mode).IsEqualTo(CameraMode.FollowPlayer);
            await Assert.That(vm.FollowedSlot).IsEqualTo(2);
            await Assert.That(vm.Attributes.Count(a => a.IsFollowed)).IsEqualTo(1);
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
