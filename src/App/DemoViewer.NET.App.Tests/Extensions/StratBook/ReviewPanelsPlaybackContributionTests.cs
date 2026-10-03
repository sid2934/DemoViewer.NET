#region

using Avalonia.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Modules.RoundTagger.Review;
using DemoViewer.NET.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     Review mode's panels as the pack's playback contribution (item 17): with the pack off nothing is
///     built or registered; on, the palette, the review panel and the queue are the column's three panels
///     in that order, shown in Review mode only; the palette's focus is the column's keyboard and feeds the
///     WhenPaletteFocused scope; the narrower gates hide their panel; and turning the pack off with an
///     editor open takes every panel, handler and lane hook with it.
/// </summary>
[NotInParallel]
public class ReviewPanelsPlaybackContributionTests
{
    private const string DemoPath = "/d/match.dem";

    [Test]
    public async Task PackOff_BuildsNothing_AndTheToggleAddsTheThreePanelsInOrder()
    {
        FakeGate gate = new() { On = false };
        (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab(gate);
        vm.IsReviewMode = true;
        TagTrack lane = vm.Timeline.RegisteredTracks.OfType<TagTrack>().Single();

        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels).IsEmpty();
            await Assert.That(review.Palette).IsNull().Because("no view model is constructed while the pack is off");
            await Assert.That(review.Queue).IsNull();
            await Assert.That(review.Review).IsNull();
            await Assert.That(lane.CodeColour).IsNull();
            await Assert.That(vm.IsReviewAvailable).IsFalse();
            await Assert.That(vm.ExecuteAction(Playback2DAction.FocusTagPalette)).IsFalse();
        }

        gate.On = true;
        gate.Raise();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels.Select(p => p.Content!.GetType()))
                .IsEquivalentTo([typeof(TagPaletteViewModel), typeof(ReviewPanelViewModel), typeof(SuggestionQueueViewModel)]);
            await Assert.That(vm.Surface.Panels.Select(p => p.IsShown)).IsEquivalentTo([true, true, true]);
            await Assert.That(((Playback2DPanel)review.PalettePanel!).FeatureId).IsEqualTo(RoundTaggerModule.PaletteFeatureId);
            await Assert.That(((Playback2DPanel)review.QueuePanel!).FeatureId).IsEqualTo(SuggestedTagsService.FeatureId);
            await Assert.That(lane.CodeColour).IsNotNull().Because("the palette colours the lane's bands");
            await Assert.That(vm.IsReviewAvailable).IsTrue();
            await Assert.That(vm.IsCardStrip).IsTrue();
        }

        vm.IsReviewMode = false;
        await Assert.That(vm.Surface.Panels.Select(p => p.IsShown)).IsEquivalentTo([false, false, false]);

        vm.Dispose();
    }

    [Test]
    public async Task ThePalettesFocus_IsTheColumnsKeyboard_AndFeedsItsScope()
    {
        (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab();
        await review.Session!.AttachAsync(Demo, Clock, DemoPath);

        await Assert.That(ReviewPanelsHarness.Press(vm, Key.C)).IsFalse().Because("outside Review mode the palette is not on screen");

        vm.IsReviewMode = true;
        await Assert.That(ReviewPanelsHarness.Press(vm, Key.C)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(review.IsPaletteFocused).IsTrue();
            await Assert.That(review.PalettePanel!.HasKeyboard).IsTrue();
            await Assert.That(vm.Surface.HasKeyboard).IsTrue();
            await Assert.That(vm.Timeline.IsLaneEditable).IsTrue();
        }

        // Esc is TagPaletteBack in the WhenPaletteFocused scope: on the root panel it leaves the palette.
        await Assert.That(vm.Surface.TryHandleKey(Key.Escape, KeyModifiers.None)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(review.IsPaletteFocused).IsFalse();
            await Assert.That(vm.Surface.HasKeyboard).IsFalse();
            await Assert.That(vm.Surface.TryHandleKey(Key.Escape, KeyModifiers.None)).IsFalse().Because("unfocused, Esc is the tab's");
        }

        // Leaving Review mode takes the keyboard back and writes a tag still being made.
        ReviewPanelsHarness.Press(vm, Key.C);
        ReviewPanelsHarness.Press(vm, Key.D1); // A execute: waits for its labels
        vm.IsReviewMode = false;
        using (Assert.Multiple())
        {
            await Assert.That(review.IsPaletteFocused).IsFalse();
            await Assert.That(review.Session.Document!.Instances.Select(i => i.Code)).IsEquivalentTo(["A execute"]);
            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheNarrowerGates_HideTheirPanel_AndBothOffClosesTheReviewPanel()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab();
        await review.Session!.AttachAsync(Demo, Clock, DemoPath);
        vm.IsReviewMode = true;
        ReviewPanelsHarness.Press(vm, Key.C);
        await Assert.That(review.IsPaletteFocused).IsTrue();

        ctx.Gate!.SetEnabled(RoundTaggerModule.PaletteFeatureId, false);
        using (Assert.Multiple())
        {
            await Assert.That(review.PalettePanel!.IsShown).IsFalse();
            await Assert.That(review.IsPaletteFocused).IsFalse().Because("gated off, the palette gives the keyboard back");
            await Assert.That(review.Review!.IsLabelsAvailable).IsFalse();
            await Assert.That(review.Review.IsSuggestedAvailable).IsTrue();
            await Assert.That(review.QueuePanel!.IsShown).IsTrue();
            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
            await Assert.That(vm.IsReviewAvailable).IsTrue();
        }

        ctx.Gate.SetEnabled(SuggestedTagsService.FeatureId, false);
        using (Assert.Multiple())
        {
            await Assert.That(review.Review).IsNull().Because("the shared editor's panel exists while either gate is on");
            await Assert.That(review.ReviewPanel!.IsOpen).IsFalse();
            await Assert.That(vm.Surface.Panels.Count(p => p.IsShown)).IsEqualTo(0);
            await Assert.That(vm.IsReviewAvailable).IsFalse();
        }

        ctx.Gate.SetEnabled(RoundTaggerModule.PaletteFeatureId, true);
        using (Assert.Multiple())
        {
            await Assert.That(review.Review).IsNotNull();
            await Assert.That(review.Review!.IsLabelsAvailable).IsTrue();
            await Assert.That(review.Review.IsSuggestedAvailable).IsFalse();
            await Assert.That(vm.IsReviewAvailable).IsTrue();
        }

        vm.Dispose();
    }

    [Test]
    public async Task PackOff_WithAnEditorOpen_TakesEveryPanelHandlerAndLaneHookWithIt()
    {
        FakeGate gate = new() { On = true };
        (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab(gate);
        TagSession session = review.Session!;
        await session.AttachAsync(Demo, Clock, DemoPath);
        vm.IsReviewMode = true;
        TagTrack lane = vm.Timeline.RegisteredTracks.OfType<TagTrack>().Single();

        review.Review!.LabelHereCommand.Execute(null);
        ReviewPanelsHarness.Press(vm, Key.C);
        using (Assert.Multiple())
        {
            await Assert.That(review.Review.HasEditor).IsTrue();
            await Assert.That(vm.Timeline.HasEditSpan).IsTrue();
            await Assert.That(vm.Surface.HasKeyboard).IsTrue();
        }

        gate.On = false;
        gate.Raise();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels).IsEmpty();
            await Assert.That(review.Palette).IsNull();
            await Assert.That(review.Review).IsNull();
            await Assert.That(review.Queue).IsNull();
            await Assert.That(vm.Surface.HasKeyboard).IsFalse();
            await Assert.That(vm.Timeline.HasEditSpan).IsFalse();
            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
            await Assert.That(lane.CodeColour).IsNull();
            await Assert.That(vm.IsReviewAvailable).IsFalse();
            await Assert.That(vm.ExecuteAction(Playback2DAction.FocusTagPalette)).IsFalse().Because("no handler is registered");
            await Assert.That(vm.Surface.TryHandleKey(Key.D1, KeyModifiers.None)).IsFalse();
        }

        // The session is the lane's and outlives the panels: a change after the detach reaches no disposed panel.
        session.Apply(new TagDelta.Add(new TagInstance
        {
            Id = Guid.NewGuid(), Code = "Default", FromTick = 100, ToTick = 200, CreatedUtc = Created, ModifiedUtc = Created
        }));
        await Assert.That(session.Document!.Instances.Count).IsEqualTo(1);

        vm.Dispose();
    }

    private sealed class FakeGate : IFeatureGate
    {
        public bool On { get; set; } = true;
        public UserCategory Category => UserCategory.PowerUser;
        public int HiddenCount => 0;
        public bool IsEnabled(string featureId) => On;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}

