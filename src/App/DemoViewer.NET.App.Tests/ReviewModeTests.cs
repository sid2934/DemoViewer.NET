#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using DemoViewer.NET.Views.Playback2D;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Modules.SuggestedTags;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Review mode in 2D Playback: off by default, it hides the labelling panels and lanes and leaves every
///     tagging key unhandled; Shift+R turns it on, which shows them, collapses the player cards to a strip
///     and hands the keys back. The lanes are hidden by mode, never by overwriting the user's own toggle.
/// </summary>
public class ReviewModeTests
{
    private static bool Press(Playback2DTabViewModel vm, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        vm.Keymap.TryResolve(key, modifiers, false, out Playback2DAction action) && vm.ExecuteAction(action);

    [Test]
    public async Task OffByDefault_TheTaggingKeysAreUnhandled_AndShiftRTurnsItOn() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();

            using (Assert.Multiple())
            {
                await Assert.That(vm.IsReviewMode).IsFalse().Because("plain playback keeps the full player cards");
                await Assert.That(vm.IsReviewAvailable).IsTrue();
                await Assert.That(vm.ShowTagPalette).IsFalse();
                await Assert.That(vm.ShowSuggestionQueue).IsFalse();
                await Assert.That(vm.IsCardStrip).IsFalse();
                await Assert.That(Press(vm, Key.C)).IsFalse().Because("the palette is not on screen");
                await Assert.That(vm.IsTagPaletteFocused).IsFalse();
                await Assert.That(Press(vm, Key.N)).IsFalse().Because("N must not reject a suggestion no one can see");
                await Assert.That(Press(vm, Key.Y, KeyModifiers.Control)).IsFalse();
                await Assert.That(vm.Timeline.IsTrackSuppressed(TagTrack.TrackId)).IsTrue();
                await Assert.That(vm.Timeline.IsTrackSuppressed(ProposalTrack.TrackId)).IsTrue();
            }

            await Assert.That(Press(vm, Key.R, KeyModifiers.Shift)).IsTrue();
            using (Assert.Multiple())
            {
                await Assert.That(vm.IsReviewMode).IsTrue();
                await Assert.That(vm.ShowTagPalette).IsTrue();
                await Assert.That(vm.ShowSuggestionQueue).IsTrue();
                await Assert.That(vm.IsCardStrip).IsTrue();
                await Assert.That(vm.Timeline.IsTrackSuppressed(TagTrack.TrackId)).IsFalse();
                await Assert.That(vm.Timeline.IsTrackSuppressed(ProposalTrack.TrackId)).IsFalse();
            }

            await Assert.That(Press(vm, Key.R, KeyModifiers.Shift)).IsTrue();
            await Assert.That(vm.IsReviewMode).IsFalse().Because("Shift+R toggles back out");
        });

    [Test]
    public async Task TheMode_NeverOverwritesTheUsersOwnLaneToggle() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
            bool before = vm.Timeline.Tracks.Single(t => t.Id == TagTrack.TrackId).IsEnabled;
            int saves = 0;
            vm.Timeline.TrackVisibilityChanged += () => saves++;

            vm.IsReviewMode = true;
            vm.IsReviewMode = false;

            using (Assert.Multiple())
            {
                await Assert.That(vm.Timeline.Tracks.Single(t => t.Id == TagTrack.TrackId).IsEnabled).IsEqualTo(before);
                await Assert.That(saves).IsEqualTo(0).Because("the per-track setting is the user's, not the mode's");
            }
        });

    [Test]
    [Category("Integration")]
    public async Task TheView_ShowsFullCardsOff_AndAStripWithThePanelsOn()
    {
        double offCards = 0, onCards = 0;
        bool offPalette = true, onPalette = false;
        await HeadlessSession.RunOnUi(() =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Push(1, 2);
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm, 1280, 800);
            ListBox cards = view.FindControl<ListBox>("PlayerCards")!;
            Control palette = view.FindControl<Control>("TagPaletteHost")!;
            Playback2DTimelineHarness.Pump();
            offCards = cards.Bounds.Height;
            offPalette = palette.IsEffectivelyVisible;
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "review-off.png"), new PngBitmapEncoderOptions());

            vm.IsReviewMode = true;
            Playback2DTimelineHarness.Pump();
            onCards = cards.Bounds.Height;
            onPalette = palette.IsEffectivelyVisible;
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "review-on.png"), new PngBitmapEncoderOptions());
            window.Close();
            return Task.CompletedTask;
        });

        Console.WriteLine($"[review] cards off={offCards:0} on={onCards:0}");
        using (Assert.Multiple())
        {
            await Assert.That(offPalette).IsFalse().Because("Review mode is off by default");
            await Assert.That(onPalette).IsTrue();
            await Assert.That(onCards).IsLessThan(offCards).Because("the cards collapse to a strip");
        }
    }
}
