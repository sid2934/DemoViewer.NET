#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Suggested Tags step 5 (suggested-tags.md §3.6, §7.5): the Proposal Track's bands, the queue's
///     keyboard flow over hand-written proposals, the six keymap rows and their scope, and the 2D tab
///     leaving the keys unhandled when nothing is selected.
/// </summary>
public class SuggestedTagsQueueTests
{
    private static readonly TagProposal Execute = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 1320, 1800, 1500, 0.8);
    private static readonly TagProposal Opener = Proposal("opener|r1|T|BombsiteA|s=5", "opener", 1, 1300, 1500, 1400, 0.6);
    private static readonly TagProposal Default = Proposal("default|r2|T", "default", 2, 8000, 9000, 8500, 0.4, null);

    // Frame = tick / 2; a tick past the last frame resolves to -1 like the host's.
    private sealed class HalfTickData(int totalFrames) : ITimelineData
    {
        public int TotalFrames { get; } = totalFrames;
        public int TickRate => 64;

        public int FrameIndexAtTick(int tick)
        {
            int frame = tick / 2;
            return frame >= TotalFrames ? -1 : frame;
        }

        public IReadOnlyList<int> FramesForEvent(string eventName) => [];
        public IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName) => [];
        public bool HasEvent(string eventName) => false;
    }

    private static (SuggestedTagsReviewHarness Harness, SuggestionQueueViewModel Queue, ProposalTrack Track, List<int> Seeks)
        Queue(params TagProposal[] proposals)
    {
        SuggestedTagsReviewHarness h = new();
        h.Write(proposals);
        ProposalTrack track = new();
        List<int> seeks = [];
        SuggestionQueueViewModel queue = new(h.Service, track, seeks.Add, () => 64, static a => a());
        queue.Attach(DemoPath, Sha);
        return (h, queue, track, seeks);
    }

    [Test]
    public async Task TheTrack_MergesOverlapsIntoRuns_LabelledCodeAtSite()
    {
        ProposalTrack track = new();
        int changed = 0;
        track.MarkersChanged += () => changed++;
        track.SetProposals([Execute, Opener, Default]);
        HalfTickData data = new(20000);

        IReadOnlyList<TimelineBand> bands = track.BuildBands(data);

        using (Assert.Multiple())
        {
            await Assert.That(changed).IsEqualTo(1);
            await Assert.That(bands.Count).IsEqualTo(2).Because("the execute and the opener overlap");
            await Assert.That(bands[0].Label).IsEqualTo("2");
            await Assert.That(bands[0].StartFrameIndex).IsEqualTo(650);
            await Assert.That(bands[0].EndFrameIndex).IsEqualTo(900);
            await Assert.That(bands[1].Label).IsEqualTo("default").Because("no site: the code alone");
            await Assert.That(bands.All(b => b.TrackId == ProposalTrack.TrackId)).IsTrue();
            await Assert.That(track.BuildMarkers(data)).IsEmpty();
            await Assert.That(string.Join(' ', track.ProposalsInRun(data, 650))).IsEqualTo($"{Opener.Id} {Execute.Id}");
            await Assert.That(track.ProposalsInRun(data, 660)).IsEmpty();
        }

        track.SetProposals([Execute]);
        await Assert.That(track.BuildBands(data).Single().Label).IsEqualTo("execute@BombsiteA");
    }

    [Test]
    public async Task TheTrack_TintsByThreeConfidenceSteps_ThroughTheHost()
    {
        ProposalTrack track = new() { StepColour = step => step == ConfidenceStep.High ? 0xFF00FF00u : null };
        track.SetProposals([Execute]);

        using (Assert.Multiple())
        {
            await Assert.That(ProposalTrack.StepOf(0.49)).IsEqualTo(ConfidenceStep.Low);
            await Assert.That(ProposalTrack.StepOf(0.5)).IsEqualTo(ConfidenceStep.Medium);
            await Assert.That(ProposalTrack.StepOf(0.75)).IsEqualTo(ConfidenceStep.Medium);
            await Assert.That(ProposalTrack.StepOf(0.76)).IsEqualTo(ConfidenceStep.High);
            await Assert.That(track.BuildBands(new HalfTickData(20000)).Single().Argb).IsEqualTo(0xFF00FF00u);
        }
    }

    [Test]
    public async Task WithNothingSelected_TheSixKeysDoNothing()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, List<int> seeks) = Queue(Execute, Opener, Default);
        using (h)
        {
            foreach (Playback2DAction action in (Playback2DAction[])
                     [
                         Playback2DAction.SuggestionNext, Playback2DAction.SuggestionPrev,
                         Playback2DAction.SuggestionAccept, Playback2DAction.SuggestionReject,
                         Playback2DAction.SuggestionEdit, Playback2DAction.SuggestionAcceptAll
                     ])
            {
                await Assert.That(queue.Execute(action)).IsFalse().Because($"{action} is inert without a selection");
            }

            await Assert.That(queue.Rows.Count).IsEqualTo(3);
            await Assert.That(seeks).IsEmpty();
            await Assert.That(h.Tags.TryLoad(Sha)).IsNull();
        }
    }

    [Test]
    public async Task TheRows_AreInRoundThenTriggerOrder_AndReviewSelectsTheFirst()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, List<int> seeks) = Queue(Default, Execute, Opener);
        using (h)
        {
            // The opener triggers at 1400, before the execute at 1500, and both are round 1.
            await Assert.That(string.Join(' ', queue.Rows.Select(r => r.Proposal.Id)))
                .IsEqualTo($"{Opener.Id} {Execute.Id} {Default.Id}");

            queue.ReviewCommand.Execute(null);
            await Assert.That(queue.Selected!.Proposal.Id).IsEqualTo(Opener.Id);
            await Assert.That(queue.HasSelection).IsTrue();
            await Assert.That(seeks.Single()).IsEqualTo(Opener.FromTick);
            await Assert.That(queue.Rows[0].SecondText).IsEqualTo("0:06").Because("(1400 - 1000) / 64 is 6.25 s");
        }
    }

    [Test]
    public async Task TheWalk_SeeksToEachStart_AndStopsAtTheEnds()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, List<int> seeks) = Queue(Execute, Opener, Default);
        using (h)
        {
            queue.ReviewCommand.Execute(null);
            await Assert.That(queue.Selected!.Proposal.Id).IsEqualTo(Opener.Id);
            await Assert.That(queue.Execute(Playback2DAction.SuggestionPrev)).IsFalse().Because("the ends do not wrap");
            await Assert.That(queue.Execute(Playback2DAction.SuggestionNext)).IsTrue();
            await Assert.That(queue.Execute(Playback2DAction.SuggestionNext)).IsTrue();
            await Assert.That(queue.Execute(Playback2DAction.SuggestionNext)).IsFalse();
            await Assert.That(queue.Selected!.Proposal.Id).IsEqualTo(Default.Id);
            await Assert.That(queue.Execute(Playback2DAction.SuggestionPrev)).IsTrue();
            int[] expected = [Opener.FromTick, Execute.FromTick, Default.FromTick, Execute.FromTick];
            await Assert.That(string.Join(',', seeks)).IsEqualTo(string.Join(',', expected));
        }
    }

    [Test]
    public async Task Y_AcceptsIntoTheTagStore_AndAdvances_N_Rejects()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, ProposalTrack track, _) = Queue(Execute, Opener, Default);
        using (h)
        {
            queue.ReviewCommand.Execute(null);

            await Assert.That(queue.Execute(Playback2DAction.SuggestionAccept)).IsTrue();
            await Assert.That(queue.Selected!.Proposal.Id).IsEqualTo(Execute.Id).Because("a verdict advances");
            await Assert.That(queue.Execute(Playback2DAction.SuggestionReject)).IsTrue();

            TagInstance accepted = h.Tags.TryLoad(Sha)!.Instances.Single();
            Dictionary<string, SuggestionVerdict> verdicts = h.Tags.LoadVerdicts(Sha)!.Verdicts;
            using (Assert.Multiple())
            {
                await Assert.That(accepted.Code).IsEqualTo("opener");
                await Assert.That(accepted.Source).IsEqualTo(TagSources.Suggested);
                await Assert.That(verdicts[Opener.Id].Verdict).IsEqualTo(SuggestionVerdicts.Accepted);
                await Assert.That(verdicts[Execute.Id].Verdict).IsEqualTo(SuggestionVerdicts.Rejected);
                await Assert.That(queue.Rows.Select(r => r.Proposal.Id)).IsEquivalentTo([Default.Id]);
                await Assert.That(queue.Selected!.Proposal.Id).IsEqualTo(Default.Id);
                await Assert.That(queue.HeaderText).IsEqualTo("Suggested: 1");
                await Assert.That(track.BuildBands(new HalfTickData(20000)).Single().Label).IsEqualTo("default")
                    .Because("accepted and rejected proposals leave the track");
            }

            await Assert.That(queue.Execute(Playback2DAction.SuggestionReject)).IsTrue();
            await Assert.That(queue.HasSelection).IsFalse().Because("the last verdict empties the queue");
            await Assert.That(queue.Rows).IsEmpty();
        }
    }

    [Test]
    public async Task Enter_OpensTheEditor_AndSavingAcceptsAsEdited()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, _) = Queue(Execute);
        using (h)
        {
            queue.ReviewCommand.Execute(null);
            await Assert.That(queue.Execute(Playback2DAction.SuggestionEdit)).IsTrue();
            await Assert.That(queue.IsEditing).IsTrue();
            await Assert.That(queue.EditFrom).IsEqualTo("5").Because("(1320 - 1000) / 64 seconds since freeze end");
            await Assert.That(queue.EditLabels).IsEqualTo("site=BombsiteA");

            queue.EditFrom = "4";
            queue.EditTo = "12.5";
            queue.EditLabels = "site=BombsiteA, tempo=slow";
            queue.SaveEditCommand.Execute(null);

            TagInstance instance = h.Tags.TryLoad(Sha)!.Instances.Single();
            using (Assert.Multiple())
            {
                await Assert.That(queue.IsEditing).IsFalse();
                await Assert.That(instance.FromTick).IsEqualTo(FreezeEnd + 4 * 64);
                await Assert.That(instance.ToTick).IsEqualTo(FreezeEnd + 800);
                await Assert.That(instance.Labels.Any(l => l.Group == "tempo" && l.Value == "slow")).IsTrue();
                await Assert.That(h.Tags.LoadVerdicts(Sha)!.Verdicts[Execute.Id].Verdict).IsEqualTo(SuggestionVerdicts.Edited);
            }
        }
    }

    [Test]
    public async Task EscFromTheEditor_LeavesNoVerdict()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, _) = Queue(Execute);
        using (h)
        {
            queue.ReviewCommand.Execute(null);
            queue.Execute(Playback2DAction.SuggestionEdit);
            queue.CancelEditCommand.Execute(null);

            await Assert.That(queue.IsEditing).IsFalse();
            await Assert.That(h.Tags.LoadVerdicts(Sha)!.Verdicts).IsEmpty();
            await Assert.That(queue.Rows.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task CtrlY_AsksFirst_ThenAcceptsWhatTheFiltersShow()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, _) = Queue(Execute, Opener, Default);
        using (h)
        {
            queue.MinConfidence = 0.5;
            await Assert.That(queue.Rows.Count).IsEqualTo(2).Because("the 0.4 default is filtered out");
            queue.ReviewCommand.Execute(null);

            await Assert.That(queue.Execute(Playback2DAction.SuggestionAcceptAll)).IsTrue();
            await Assert.That(queue.IsConfirmingAcceptAll).IsTrue();
            await Assert.That(h.Tags.TryLoad(Sha)).IsNull().Because("nothing is accepted before the confirm");

            await Assert.That(queue.Execute(Playback2DAction.SuggestionAcceptAll)).IsTrue();

            using (Assert.Multiple())
            {
                await Assert.That(queue.IsConfirmingAcceptAll).IsFalse();
                await Assert.That(string.Join(' ', h.Tags.TryLoad(Sha)!.Instances.Select(i => i.Code).Order(StringComparer.Ordinal)))
                    .IsEqualTo("execute opener");
                await Assert.That(queue.PendingCount).IsEqualTo(1);
                await Assert.That(queue.Rows).IsEmpty();
            }
        }
    }

    [Test]
    public async Task TheDetectorFilter_ShowsOneDetector()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, _) = Queue(Execute, Opener, Default);
        using (h)
        {
            queue.DetectorFilter = "opener";
            await Assert.That(queue.Rows.Select(r => r.Proposal.Id)).IsEquivalentTo([Opener.Id]);
            await Assert.That(queue.PendingCount).IsEqualTo(3).Because("the header counts the demo, not the filter");
            await Assert.That(queue.DetectorFilters[0]).IsEqualTo(SuggestionQueueViewModel.AllDetectors);
        }
    }

    [Test]
    public async Task ABandPress_SelectsItsFirstProposal_ThenWalksTheRun()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, ProposalTrack track, List<int> seeks) =
            Queue(Execute, Opener, Default);
        using (h)
        {
            HalfTickData data = new(20000);
            track.BuildBands(data);

            queue.SelectFromTrack(track.ProposalsInRun(data, 650));
            await Assert.That(queue.Selected!.Proposal.Id).IsEqualTo(Opener.Id);
            queue.SelectFromTrack(track.ProposalsInRun(data, 650));
            await Assert.That(queue.Selected!.Proposal.Id).IsEqualTo(Execute.Id);
            await Assert.That(seeks).IsEmpty().Because("the band press seeks to the band's start itself");
        }
    }

    [Test]
    public async Task TheBrowserHost_SaysSessionOnly()
    {
        (SuggestedTagsReviewHarness h, SuggestionQueueViewModel queue, _, _) = Queue(Execute);
        using (h)
        {
            await Assert.That(queue.IsSessionOnly).IsTrue();
        }
    }

    [Test]
    public async Task TheKeymap_ClaimsTheSixKeys_JAndKOnlyWhileASuggestionIsSelected()
    {
        Playback2DKeymapProfile keymap = Playback2DKeymapProfile.Default;

        using (Assert.Multiple())
        {
            await Assert.That(keymap.TryResolve(Key.J, KeyModifiers.None, false, out Playback2DAction j)).IsTrue();
            await Assert.That(j).IsEqualTo(Playback2DAction.NextSituationResult).Because("J stays the result walk otherwise");
            await Assert.That(keymap.TryResolveInScope(Playback2DBindingScope.WhenSuggestionSelected, Key.J,
                KeyModifiers.None, out Playback2DAction sj)).IsTrue();
            await Assert.That(sj).IsEqualTo(Playback2DAction.SuggestionNext);
            await Assert.That(keymap.TryResolveInScope(Playback2DBindingScope.WhenSuggestionSelected, Key.K,
                KeyModifiers.None, out Playback2DAction sk)).IsTrue();
            await Assert.That(sk).IsEqualTo(Playback2DAction.SuggestionPrev);

            await Assert.That(Resolve(keymap, Key.Y, KeyModifiers.None)).IsEqualTo(Playback2DAction.SuggestionAccept);
            await Assert.That(Resolve(keymap, Key.N, KeyModifiers.None)).IsEqualTo(Playback2DAction.SuggestionReject);
            await Assert.That(Resolve(keymap, Key.Enter, KeyModifiers.None)).IsEqualTo(Playback2DAction.SuggestionEdit);
            await Assert.That(Resolve(keymap, Key.Y, KeyModifiers.Control)).IsEqualTo(Playback2DAction.SuggestionAcceptAll);
            await Assert.That(Playback2DKeymap.FindConflicts(Playback2DKeymap.Default, Playback2DKeymap.ReservedGestures(true)))
                .IsEmpty().Because("none of the six is a shell or browser gesture");
            await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.SuggestionEdit)).IsEqualTo("Enter");
        }
    }

    [Test]
    public async Task TheTab_LeavesTheKeysUnhandled_WithNoSelection_AndHidesTheQueueWhenGatedOff()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActionDispatchTests.Activated();

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsSuggestedTagsEnabled).IsTrue();
            await Assert.That(vm.ExecuteAction(Playback2DAction.SuggestionAccept)).IsFalse();
            await Assert.That(vm.TryHandleSuggestionKey(Key.J, KeyModifiers.None)).IsFalse();
            await Assert.That(vm.Timeline.Tracks.Any(t => t.Id == ProposalTrack.TrackId)).IsTrue();
        }

        ctx.Gate!.SetEnabled(SuggestedTagsService.FeatureId, false);
        await Assert.That(vm.IsSuggestedTagsEnabled).IsFalse();
        await Assert.That(vm.SuggestionQueue.DemoPath).IsNull();
    }

    private static Playback2DAction Resolve(Playback2DKeymapProfile keymap, Key key, KeyModifiers modifiers) =>
        keymap.TryResolve(key, modifiers, false, out Playback2DAction action) ? action : Playback2DAction.None;
}
