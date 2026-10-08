#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions;
using Avalonia.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Palette;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Review;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     Review mode as the pack's playback contribution: with the pack off nothing is built
///     or registered, no session, no lane, no toggle; on, the session is built from the context's services,
///     the tag and suggestion lanes register in order, the Review toggle is listed and the palette, the
///     review panel and the queue are the column's three panels in that order, shown in Review mode only;
///     the palette's focus is the column's keyboard and feeds the WhenPaletteFocused scope; the narrower
///     gates hide their panel; the demo-change signal attaches the session and a swap writes the pending
///     tag to the old document first; the mode persists to the tab's old settings key; and turning the pack
///     off with a tag pending writes it, then takes every panel, handler, lane and toggle with it.
/// </summary>
[NotInParallel]
public class ReviewPanelsPlaybackContributionTests
{
    private const string DemoPath = "/d/match.dem";
    private const string OtherPath = "/d/other.dem";
    private static readonly string OtherSha = new('b', 64);

    // These bodies run off the UI thread (no RunOnUi), where nothing pumps the dispatcher: the session's
    // changes, the lane's re-query and the queue's reload are delivered synchronously here, and on the
    // production post in the tests that run on the UI thread.
    private static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx, ReviewPanelsPlaybackContribution Review) Tab(
        IFeatureGate? gate = null, Action<Playback2DFakeContext>? configure = null,
        Func<string, string?, Task<DemoIdentity?>>? identity = null) =>
        ReviewPanelsHarness.Tab(gate, configure: configure, post: ReviewPanelsHarness.SynchronousPost, identity: identity);

    [Test]
    public async Task PackOff_BuildsNothing_AndTheToggleAddsTheSessionTheLanesTheToggleAndThePanels()
    {
        FakeGate gate = new() { On = false };
        (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = Tab(gate);
        string[] coreTracks = [.. vm.Timeline.Tracks.Select(t => t.Id)];

        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels).IsEmpty();
            await Assert.That(vm.Surface.ModeToggles).IsEmpty();
            await Assert.That(review.Session).IsNull().Because("no session is constructed while the pack is off");
            await Assert.That(review.ReviewMode).IsNull();
            await Assert.That(review.Palette).IsNull().Because("no view model is constructed while the pack is off");
            await Assert.That(review.Queue).IsNull();
            await Assert.That(review.Review).IsNull();
            await Assert.That(coreTracks).DoesNotContain(TagTrack.TrackId);
            await Assert.That(coreTracks).DoesNotContain(ProposalTrack.TrackId);
            await Assert.That(vm.IsReviewAvailable).IsFalse();
            await Assert.That(vm.ExecuteAction(StratBookActions.FocusTagPalette)).IsFalse();
            await Assert.That(vm.ExecuteAction(StratBookActions.ToggleReviewMode)).IsFalse();
        }

        gate.On = true;
        gate.Raise();
        TagTrack lane = review.Tags!;
        using (Assert.Multiple())
        {
            await Assert.That(review.Session).IsNotNull();
            await Assert.That(lane.Session).IsSameReferenceAs(review.Session).Because("the lane reads the contribution's session");
            await Assert.That(vm.Timeline.Tracks.Select(t => t.Id)).IsEquivalentTo([.. coreTracks, TagTrack.TrackId, ProposalTrack.TrackId])
                .Because("the tag lane comes first, then the suggestions, after the tab's own tracks");
            await Assert.That(vm.Timeline.Lanes.Select(l => l.Track.Id)).IsEquivalentTo([TagTrack.TrackId, ProposalTrack.TrackId]);
            await Assert.That(vm.Surface.ModeToggles.Select(t => t.Id)).IsEquivalentTo([ReviewPanelsPlaybackContribution.ReviewModeId]);
            await Assert.That(review.ReviewMode!.ActionId).IsEqualTo(StratBookActions.ToggleReviewMode);
            await Assert.That(review.ReviewMode.IsOn).IsFalse();
            await Assert.That(vm.Timeline.IsTrackSuppressed(TagTrack.TrackId)).IsTrue().Because("the lanes are the mode's");
            await Assert.That(vm.Surface.Panels.Select(p => p.Content!.GetType()))
                .IsEquivalentTo([typeof(TagPaletteViewModel), typeof(ReviewPanelViewModel), typeof(SuggestionQueueViewModel)]);
            await Assert.That(vm.Surface.Panels.Select(p => p.IsShown)).IsEquivalentTo([false, false, false]);
            await Assert.That(((Playback2DPanel)review.PalettePanel!).FeatureId).IsEqualTo(RoundTaggerModule.PaletteFeatureId);
            await Assert.That(((Playback2DPanel)review.QueuePanel!).FeatureId).IsEqualTo(SuggestedTagsService.FeatureId);
            await Assert.That(vm.Surface.Panels.All(p => ReferenceEquals(p.Mode, review.ReviewMode))).IsTrue()
                .Because("every panel is bound to the Review toggle");
            await Assert.That(lane.CodeColour).IsNotNull().Because("the palette colours the lane's bands");
            await Assert.That(vm.IsReviewAvailable).IsTrue();
            await Assert.That(vm.IsCardStrip).IsFalse();
        }

        review.ReviewMode.IsOn = true;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Surface.Panels.Select(p => p.IsShown)).IsEquivalentTo([true, true, true]);
            await Assert.That(vm.IsCardStrip).IsTrue();
            await Assert.That(vm.Timeline.IsTrackSuppressed(TagTrack.TrackId)).IsFalse();
            await Assert.That(vm.Timeline.IsTrackSuppressed(ProposalTrack.TrackId)).IsFalse();
        }

        review.ReviewMode.IsOn = false;
        await Assert.That(vm.Surface.Panels.Select(p => p.IsShown)).IsEquivalentTo([false, false, false]);

        vm.Dispose();
    }

    // On the UI thread with the dispatcher post: a swap's save raises Changed off it, and the palette's
    // view must only be touched there.
    [Test]
    public async Task TheSession_IsBuiltFromTheContextsServices_AndTheDemoChangeSignalAttachesIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            TagStore store = new(null);
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab(
                configure: c =>
                {
                    c.SetService(store);
                    c.DemoPath = DemoPath;
                    c.DemoSha256 = Sha;
                });
            TagSession session = review.Session!;
            await AttachedAsync(session, DemoPath);
            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Demo.Sha256).IsEqualTo(Sha).Because("attached on the activation's demo change");
                await Assert.That(review.Queue!.DemoPath).IsEqualTo(DemoPath);
            }

            // The same demo again (a re-activation) keeps the document; another one swaps it and the write lands in the store.
            TagDocument first = session.Document!;
            vm.OnDeactivated();
            vm.OnActivated(ctx);
            await Assert.That(session.Document).IsSameReferenceAs(first);

            session.Apply(new TagDelta.Add(new TagInstance
            {
                Id = Guid.NewGuid(), Code = "Default", FromTick = 100, ToTick = 200, CreatedUtc = Created, ModifiedUtc = Created
            }));
            ctx.DemoPath = OtherPath;
            ctx.DemoSha256 = OtherSha;
            ctx.RaiseDemoReset();
            await AttachedAsync(session, OtherPath);
            using (Assert.Multiple())
            {
                await Assert.That(session.Document!.Demo.Sha256).IsEqualTo(OtherSha);
                await Assert.That(session.Document.Instances).IsEmpty();
                await Assert.That(store.TryLoad(Sha)!.Instances.Select(i => i.Code)).IsEquivalentTo(["Default"])
                    .Because("the swap flushed the old document to the store the contribution resolved");
            }

            vm.Dispose();
        });

    [Test]
    public async Task TheFirstActivation_AttachesTheDemoOnce_WhileItsIdentityIsStillResolving()
    {
        TaskCompletionSource<DemoIdentity?> slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> asked = [];
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, ReviewPanelsPlaybackContribution review) = Tab(
            configure: c => c.DemoPath = DemoPath,
            identity: (path, _) =>
            {
                asked.Add(path);
                return slow.Task;
            });

        // Attach and the activation's demo-change signal both asked for this demo; one identity is in flight.
        await Assert.That(asked).IsEquivalentTo([DemoPath]).Because("the second request sees the first still attaching");

        ctx.RaiseDemoReset(); // the same demo again, still in flight
        await Assert.That(asked.Count).IsEqualTo(1);

        slow.SetResult(Demo);
        TagSession session = review.Session!;
        await AttachedAsync(session, DemoPath);
        ctx.RaiseDemoReset(); // attached now: the path guard holds
        using (Assert.Multiple())
        {
            await Assert.That(asked.Count).IsEqualTo(1);
            await Assert.That(session.Document!.Demo.Sha256).IsEqualTo(Sha);
        }

        vm.Dispose();
    }

    [Test]
    public async Task ThePalettesFocus_IsTheColumnsKeyboard_AndFeedsItsScope()
    {
        (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = Tab();
        await review.Session!.AttachAsync(Demo, Clock, DemoPath);

        await Assert.That(ReviewPanelsHarness.Press(vm, Key.C)).IsFalse().Because("outside Review mode the palette is not on screen");

        review.ReviewMode!.IsOn = true;
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
        review.ReviewMode.IsOn = false;
        using (Assert.Multiple())
        {
            await Assert.That(review.IsPaletteFocused).IsFalse();
            await Assert.That(review.Session.Document!.Instances.Select(i => i.Code)).IsEquivalentTo(["A execute"]);
            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheNarrowerGates_HideTheirPanel_AndBothOffClosesTheReviewPanelAndHidesTheToggle()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, ReviewPanelsPlaybackContribution review) = Tab();
        await review.Session!.AttachAsync(Demo, Clock, DemoPath);
        review.ReviewMode!.IsOn = true;
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
            await Assert.That(review.ReviewMode.IsAvailable).IsTrue();
        }

        ctx.Gate.SetEnabled(SuggestedTagsService.FeatureId, false);
        using (Assert.Multiple())
        {
            await Assert.That(review.Review).IsNull().Because("the shared editor's panel exists while either gate is on");
            await Assert.That(review.ReviewPanel!.IsOpen).IsFalse();
            await Assert.That(vm.Surface.Panels.Count(p => p.IsShown)).IsEqualTo(0);
            await Assert.That(vm.IsReviewAvailable).IsFalse();
            await Assert.That(review.ReviewMode.IsAvailable).IsFalse().Because("the toolbar hides the toggle with nothing to show");
            await Assert.That(vm.ExecuteAction(StratBookActions.ToggleReviewMode)).IsTrue().Because("a mode that is on can still be left");
            await Assert.That(review.ReviewMode.IsOn).IsFalse();
            await Assert.That(vm.ExecuteAction(StratBookActions.ToggleReviewMode)).IsFalse().Because("off with nothing to show, Shift+R is nobody's");
        }

        ctx.Gate.SetEnabled(RoundTaggerModule.PaletteFeatureId, true);
        using (Assert.Multiple())
        {
            await Assert.That(review.Review).IsNotNull();
            await Assert.That(review.Review!.IsLabelsAvailable).IsTrue();
            await Assert.That(review.Review.IsSuggestedAvailable).IsFalse();
            await Assert.That(vm.IsReviewAvailable).IsTrue();
            await Assert.That(review.ReviewMode.IsAvailable).IsTrue();
        }

        vm.Dispose();
    }

    [Test]
    public async Task PackOff_WithATagPending_WritesIt_ThenTakesEveryPanelHandlerLaneAndToggleWithIt()
    {
        FakeGate gate = new() { On = true };
        TagStore store = new(null);
        (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = Tab(gate, c => c.SetService(store));
        TagSession session = review.Session!;
        await session.AttachAsync(Demo, Clock, DemoPath);
        review.ReviewMode!.IsOn = true;
        TagTrack lane = review.Tags!;

        review.Review!.LabelHereCommand.Execute(null);
        ReviewPanelsHarness.Press(vm, Key.C);
        ReviewPanelsHarness.Press(vm, Key.D1); // A execute: pending until its labels or a Finish
        using (Assert.Multiple())
        {
            await Assert.That(review.Review.HasEditor).IsTrue();
            await Assert.That(vm.Timeline.HasEditSpan).IsTrue();
            await Assert.That(vm.Surface.HasKeyboard).IsTrue();
            await Assert.That(session.Document!.Instances).IsEmpty().Because("the tag is still being made");
        }

        gate.On = false;
        gate.Raise();
        using (Assert.Multiple())
        {
            await Assert.That(store.TryLoad(Sha)!.Instances.Select(i => i.Code)).IsEquivalentTo(["A execute"])
                .Because("the pending tag is written before the session goes");
            await Assert.That(vm.Surface.Panels).IsEmpty();
            await Assert.That(vm.Surface.ModeToggles).IsEmpty();
            await Assert.That(review.Session).IsNull();
            await Assert.That(review.ReviewMode).IsNull();
            await Assert.That(review.Palette).IsNull();
            await Assert.That(review.Review).IsNull();
            await Assert.That(review.Queue).IsNull();
            await Assert.That(vm.Surface.HasKeyboard).IsFalse();
            await Assert.That(vm.Timeline.HasEditSpan).IsFalse();
            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
            await Assert.That(vm.Timeline.Lanes).IsEmpty();
            await Assert.That(vm.Timeline.Tracks.Select(t => t.Id)).DoesNotContain(TagTrack.TrackId);
            await Assert.That(vm.Timeline.Tracks.Select(t => t.Id)).DoesNotContain(ProposalTrack.TrackId);
            await Assert.That(vm.Timeline.LaneBands).IsEmpty();
            await Assert.That(lane.Session.Document).IsNull().Because("the session went with the pack");
            await Assert.That(vm.IsReviewAvailable).IsFalse();
            await Assert.That(vm.IsCardStrip).IsFalse();
            await Assert.That(vm.ExecuteAction(StratBookActions.FocusTagPalette)).IsFalse().Because("no handler is registered");
            await Assert.That(vm.ExecuteAction(StratBookActions.ToggleReviewMode)).IsFalse().Because("no toggle is registered");
            await Assert.That(vm.Surface.TryHandleKey(Key.D1, KeyModifiers.None)).IsFalse();
        }

        // Back on: a fresh session over the same store sees the written tag.
        gate.On = true;
        gate.Raise();
        await review.Session!.AttachAsync(Demo, Clock, DemoPath);
        await Assert.That(review.Session.Document!.Instances.Select(i => i.Code)).IsEquivalentTo(["A execute"]);

        vm.Dispose();
    }

    [Test]
    public async Task ReviewMode_PersistsToTheStratBooksSettings_AndANewAttachmentReadsIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dv-review-mode-{Guid.NewGuid():N}");
        try
        {
            StratBookSettings settings = new(new ExtensionSettingsStore(StratBookPack.PackId, dir, a => a()));
            (Playback2DTabViewModel vm, _, ReviewPanelsPlaybackContribution review) = Tab(configure: c => c.SetService(settings));
            await Assert.That(review.ReviewMode!.IsOn).IsFalse();

            review.ReviewMode.IsOn = true;
            await Assert.That(new StratBookSettings(new ExtensionSettingsStore(StratBookPack.PackId, dir, a => a())).ReviewMode).IsTrue()
                .Because("the mode is in the Strat Book's own settings file");

            StratBookSettings reopened = new(new ExtensionSettingsStore(StratBookPack.PackId, dir, a => a()));
            (Playback2DTabViewModel second, _, ReviewPanelsPlaybackContribution again) = Tab(configure: c => c.SetService(reopened));
            using (Assert.Multiple())
            {
                await Assert.That(again.ReviewMode!.IsOn).IsTrue().Because("a new attachment starts as the user left it");
                await Assert.That(again.PalettePanel!.IsShown).IsTrue();
                await Assert.That(second.IsCardStrip).IsTrue();
            }

            again.ReviewMode.IsOn = false;
            await Assert.That(reopened.ReviewMode).IsFalse();

            second.Dispose();
            vm.Dispose();
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Test]
    public async Task Deactivation_TakesTheKeyboardBack_SoTheNextDigitIsNobodysKey()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, ReviewPanelsPlaybackContribution review) = Tab();
        await review.Session!.AttachAsync(Demo, Clock, DemoPath);
        review.ReviewMode!.IsOn = true;
        ReviewPanelsHarness.Press(vm, Key.C);
        await Assert.That(vm.Surface.HasKeyboard).IsTrue();

        vm.OnDeactivated();
        vm.OnActivated(ctx);
        using (Assert.Multiple())
        {
            await Assert.That(review.IsPaletteFocused).IsFalse();
            await Assert.That(vm.Surface.HasKeyboard).IsFalse();
            await Assert.That(vm.Surface.TryHandleKey(Key.D1, KeyModifiers.None)).IsFalse().Because("unfocused, 1 is nobody's key");
            await Assert.That(review.Session.Document!.Instances).IsEmpty();
        }

        vm.Dispose();
    }

    // On the UI thread with the dispatcher post: a swap's save raises Changed off it, and the palette's
    // view must only be touched there.
    [Test]
    public async Task ADemoSwap_WritesThePendingTagToTheOldDocument_DropsTheNote_AndTakesTheKeyboardBack() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx, ReviewPanelsPlaybackContribution review) = ReviewPanelsHarness.Tab(
                configure: c =>
                {
                    c.DemoPath = DemoPath;
                    c.DemoSha256 = Sha;
                });
            TagSession session = review.Session!;
            await AttachedAsync(session, DemoPath);
            review.ReviewMode!.IsOn = true;
            ReviewPanelsHarness.Press(vm, Key.C);
            ReviewPanelsHarness.Press(vm, Key.D1); // A execute: pending until its labels or a Finish
            ReviewPanelsHarness.Press(vm, Key.M, KeyModifiers.Control);
            review.Palette!.NoteDraft = "half a note";
            TagDocument old = session.Document!;
            using (Assert.Multiple())
            {
                await Assert.That(review.Palette.IsEditingNote).IsTrue();
                await Assert.That(old.Instances).IsEmpty().Because("the tag is still being made");
            }

            // The swap arrives the way the app delivers it: the context moved on and raised DemoReset.
            ctx.DemoPath = OtherPath;
            ctx.DemoSha256 = OtherSha;
            ctx.RaiseDemoReset();
            await AttachedAsync(session, OtherPath);
            using (Assert.Multiple())
            {
                await Assert.That(old.Instances.Select(i => i.Code)).IsEquivalentTo(["A execute"]).Because("written before the swap");
                await Assert.That(old.Instances[0].Note).IsNull().Because("the note was never kept");
                await Assert.That(session.Document!.Instances).IsEmpty().Because("the new document starts clean");
                await Assert.That(review.Palette.IsEditingNote).IsFalse();
                await Assert.That(review.IsPaletteFocused).IsFalse();
                await Assert.That(vm.Surface.HasKeyboard).IsFalse();
                await Assert.That(vm.Surface.TryHandleKey(Key.D1, KeyModifiers.None)).IsFalse().Because("unfocused, 1 is nobody's key");
            }

            vm.Dispose();
        });

    // The attach is fire-and-forget from the demo-change signal, and a swap with a pending save hops to the
    // thread pool for the flush, so wait for the session to land on the path with its document.
    private static async Task AttachedAsync(TagSession session, string demoPath)
    {
        for (int i = 0; i < 1000 && !Landed(); i++)
        {
            await Task.Delay(10);
        }

        await Assert.That(session.DemoPath).IsEqualTo(demoPath);
        await Assert.That(session.Document).IsNotNull();

        bool Landed() => string.Equals(session.DemoPath, demoPath, StringComparison.Ordinal) && session.Document is not null;
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
