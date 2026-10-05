#region

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DemoViewer.NET.AppTests.Extensions.StratBook;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Review;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Views.Playback2D;
using DemoViewer.NET.Extensions.StratBook.Views.RoundTagger;
using ModeToggle = DemoViewer.NET.Extensions.Sdk.Playback.ModeToggle;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Review mode in 2D Playback, the Strat Book's mode toggle: off by default, it hides the contributed
///     panels and the lanes and leaves every tagging key unhandled; Shift+R turns it on, which shows them,
///     collapses the player cards to a strip and hands the keys back. The lanes are hidden by mode, never by
///     overwriting the user's own toggle. Without a pack there is no mode, no toggle and nothing to show.
/// </summary>
public class ReviewModeTests
{
    private static bool Press(Playback2DTabViewModel vm, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        vm.Keymap.TryResolve(key, modifiers, false, out Playback2DAction action) && vm.ExecuteAction(action);

    [Test]
    public async Task OffByDefault_TheTaggingKeysAreUnhandled_AndShiftRTurnsItOn() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab();
            ModeToggle mode = review.ReviewMode!;

            using (Assert.Multiple())
            {
                await Assert.That(mode.IsOn).IsFalse().Because("plain playback keeps the full player cards");
                await Assert.That(mode.IsAvailable).IsTrue();
                await Assert.That(vm.Surface.ModeToggles).IsEquivalentTo([mode]);
                await Assert.That(vm.IsReviewAvailable).IsTrue();
                await Assert.That(review.PalettePanel!.IsShown).IsFalse();
                await Assert.That(review.QueuePanel!.IsShown).IsFalse();
                await Assert.That(vm.IsCardStrip).IsFalse();
                await Assert.That(Press(vm, Key.C)).IsFalse().Because("the palette is not on screen");
                await Assert.That(review.IsPaletteFocused).IsFalse();
                await Assert.That(Press(vm, Key.N)).IsFalse().Because("N must not reject a suggestion no one can see");
                await Assert.That(Press(vm, Key.Y, KeyModifiers.Control)).IsFalse();
                await Assert.That(vm.Timeline.IsTrackSuppressed(TagTrack.TrackId)).IsTrue();
                await Assert.That(vm.Timeline.IsTrackSuppressed(ProposalTrack.TrackId)).IsTrue();
            }

            await Assert.That(Press(vm, Key.R, KeyModifiers.Shift)).IsTrue();
            using (Assert.Multiple())
            {
                await Assert.That(mode.IsOn).IsTrue();
                await Assert.That(review.PalettePanel.IsShown).IsTrue();
                await Assert.That(review.QueuePanel.IsShown).IsTrue();
                await Assert.That(vm.IsCardStrip).IsTrue();
                await Assert.That(vm.Timeline.IsTrackSuppressed(TagTrack.TrackId)).IsFalse();
                await Assert.That(vm.Timeline.IsTrackSuppressed(ProposalTrack.TrackId)).IsFalse();
            }

            await Assert.That(Press(vm, Key.R, KeyModifiers.Shift)).IsTrue();
            await Assert.That(mode.IsOn).IsFalse().Because("Shift+R toggles back out");
            vm.Dispose();
        });

    [Test]
    public async Task WithoutAPack_ThereIsNoMode_AndNoToggleIsOffered() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();

            using (Assert.Multiple())
            {
                await Assert.That(vm.IsReviewAvailable).IsFalse();
                await Assert.That(vm.Surface.Panels).IsEmpty();
                await Assert.That(vm.Surface.ModeToggles).IsEmpty();
                await Assert.That(vm.Timeline.Tracks.Select(t => t.Id)).DoesNotContain(TagTrack.TrackId);
                await Assert.That(Press(vm, Key.R, KeyModifiers.Shift)).IsFalse().Because("nothing contributed a mode");
                await Assert.That(vm.IsCardStrip).IsFalse();
            }

            vm.Dispose();
        });

    [Test]
    public async Task TheMode_NeverOverwritesTheUsersOwnLaneToggle() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab();
            bool before = vm.Timeline.Tracks.Single(t => t.Id == TagTrack.TrackId).IsEnabled;
            int saves = 0;
            vm.Timeline.TrackVisibilityChanged += () => saves++;

            review.ReviewMode!.IsOn = true;
            review.ReviewMode.IsOn = false;

            using (Assert.Multiple())
            {
                await Assert.That(vm.Timeline.Tracks.Single(t => t.Id == TagTrack.TrackId).IsEnabled).IsEqualTo(before);
                await Assert.That(saves).IsEqualTo(0).Because("the per-track setting is the user's, not the mode's");
            }

            vm.Dispose();
        });

    [Test]
    [Category("Integration")]
    public async Task TheView_ShowsFullCardsOff_AndAStripWithThePanelsOn()
    {
        double offCards = 0, onCards = 0;
        bool offPalette = true, onPalette = false, toggleShown = false;
        await HeadlessSession.RunOnUi(() =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab();
            ctx.Push(1, 2);
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm, 1280, 800);
            ListBox cards = view.FindControl<ListBox>("PlayerCards")!;
            Playback2DTimelineHarness.Pump();
            offCards = cards.Bounds.Height;
            offPalette = view.GetVisualDescendants().OfType<TagPaletteView>().Any(p => p.IsEffectivelyVisible);
            toggleShown = view.GetVisualDescendants().OfType<ToggleButton>()
                .Any(t => t.IsEffectivelyVisible && Equals(t.Content, review.ReviewMode!.Label));
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "review-off.png"), new PngBitmapEncoderOptions());

            review.ReviewMode!.IsOn = true;
            Playback2DTimelineHarness.Pump();
            onCards = cards.Bounds.Height;
            onPalette = view.GetVisualDescendants().OfType<TagPaletteView>().Any(p => p.IsEffectivelyVisible);
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "review-on.png"), new PngBitmapEncoderOptions());
            window.Close();
            vm.Dispose();
            return Task.CompletedTask;
        });

        Console.WriteLine($"[review] cards off={offCards:0} on={onCards:0}");
        using (Assert.Multiple())
        {
            await Assert.That(toggleShown).IsTrue().Because("the toolbar renders the contributed toggle");
            await Assert.That(offPalette).IsFalse().Because("Review mode is off by default");
            await Assert.That(onPalette).IsTrue();
            await Assert.That(onCards).IsLessThan(offCards).Because("the cards collapse to a strip");
        }
    }
}
