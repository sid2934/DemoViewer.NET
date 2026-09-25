#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tuning harness end to end (suggested-tags.md §3.7, step 6): the stored table over a demo the
///     evaluator already built and verdicted, and an in-memory preview that redoes only recall and
///     precision. Built on <see cref="SuggestedTagsReviewHarness" />'s synthetic walk, never a demo file.
/// </summary>
public class SuggestedTagsTuningServiceTests
{
    private static TagInstance HumanTagFor(TagProposal proposal) => new()
    {
        Id = Guid.NewGuid(),
        Code = proposal.Code,
        Round = proposal.Round,
        FromTick = proposal.FromTick,
        ToTick = proposal.ToTick,
        Source = TagSources.Human,
        CreatedUtc = Now,
        ModifiedUtc = Now
    };

    private static SuggestedTagsTuningService Tuning(SuggestedTagsReviewHarness h) =>
        new(h.Cache, h.Service, h.Tags, h.Regions, _ => Parse());

    [Test]
    public async Task BuildStoredReport_ReflectsHistoryAndScoresAgainstHandTags()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build(); // seeds Round Facts and evaluates both rounds via the synthetic walk

        ProposalSet set = h.Service.Load(DemoPath);
        TagProposal execute = set.Entries.Single(e => e.Proposal.Detector == "execute").Proposal;
        TagProposal @default = set.Entries.Single(e => e.Proposal.Detector == "default").Proposal;

        await Assert.That(h.Service.Accept(DemoPath, execute.Id)).IsTrue();
        await Assert.That(h.Service.Reject(DemoPath, @default.Id)).IsTrue();

        // A human tag that exactly covers the fired execute's window: a perfect match.
        h.Tags.Append(new DemoIdentity(Sha, "review.dem", 10),
            new ClockIdentity(ClockIdentity.DvFrameClock, 64, 2, 1, 20000), HumanTagFor(execute));

        TuningReport report = Tuning(h).BuildStoredReport();
        DetectorTuningRow executeRow = report.Rows.Single(r => r.Detector == "execute");
        DetectorTuningRow defaultRow = report.Rows.Single(r => r.Detector == "default");

        using (Assert.Multiple())
        {
            await Assert.That(executeRow.Made).IsEqualTo(1);
            await Assert.That(executeRow.Accepted).IsEqualTo(1);
            await Assert.That(executeRow.Recall).IsEqualTo(1.0);
            await Assert.That(executeRow.Precision).IsEqualTo(1.0);
            await Assert.That(defaultRow.Made).IsEqualTo(1);
            await Assert.That(defaultRow.Rejected).IsEqualTo(1);
            await Assert.That(defaultRow.Recall).IsNull().Because("no hand tag of the default code exists yet");
            await Assert.That(defaultRow.Precision).IsNull();
            await Assert.That(report.DemosWithVerdicts).IsEqualTo(1);
            await Assert.That(report.DemosWithHandTags).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ScoredDemoPaths_OnlyListsDemosTheEvaluatorHasBuilt()
    {
        using SuggestedTagsReviewHarness h = new();

        await Assert.That(Tuning(h).ScoredDemoPaths()).IsEmpty().Because("nothing has been built yet");

        h.Build();

        await Assert.That(Tuning(h).ScoredDemoPaths()).IsEquivalentTo([DemoPath]);
    }

    [Test]
    public async Task PreviewAsync_WithTheSameProfile_ReproducesTheStoredRecallAndPrecision()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        ProposalSet set = h.Service.Load(DemoPath);
        TagProposal execute = set.Entries.Single(e => e.Proposal.Detector == "execute").Proposal;
        await Assert.That(h.Service.Accept(DemoPath, execute.Id)).IsTrue();
        h.Tags.Append(new DemoIdentity(Sha, "review.dem", 10),
            new ClockIdentity(ClockIdentity.DvFrameClock, 64, 2, 1, 20000), HumanTagFor(execute));

        SuggestedTagsTuningService tuning = Tuning(h);
        TuningReport baseline = tuning.BuildStoredReport();

        TuningReport preview = await tuning.PreviewAsync(h.Profile, baseline, tuning.ScoredDemoPaths());

        DetectorTuningRow row = preview.Rows.Single(r => r.Detector == "execute");
        using (Assert.Multiple())
        {
            await Assert.That(row.Recall).IsEqualTo(1.0);
            await Assert.That(row.Precision).IsEqualTo(1.0);
            await Assert.That(row.Accepted).IsEqualTo(1)
                .Because("verdict counts are history and a preview never recomputes them");
        }
    }

    [Test]
    public async Task PreviewAsync_ALooserProfile_CanChangeWhatFires_WithoutTouchingVerdictCounts()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        ProposalSet set = h.Service.Load(DemoPath);
        TagProposal execute = set.Entries.Single(e => e.Proposal.Detector == "execute").Proposal;
        await Assert.That(h.Service.Accept(DemoPath, execute.Id)).IsTrue();
        h.Tags.Append(new DemoIdentity(Sha, "review.dem", 10),
            new ClockIdentity(ClockIdentity.DvFrameClock, 64, 2, 1, 20000), HumanTagFor(execute));

        SuggestedTagsTuningService tuning = Tuning(h);
        TuningReport baseline = tuning.BuildStoredReport();

        // minSecond above where the execute actually fires: the detector no longer proposes it at all.
        DetectorProfile candidate = h.Profile.With("execute", "minSecond", 100);
        TuningReport preview = await tuning.PreviewAsync(candidate, baseline, tuning.ScoredDemoPaths());

        DetectorTuningRow row = preview.Rows.Single(r => r.Detector == "execute");
        using (Assert.Multiple())
        {
            await Assert.That(row.Recall).IsEqualTo(0.0).Because("the candidate profile fires nothing for this code");
            await Assert.That(row.Precision).IsNull().Because("nothing fired: undefined, not 0%");
            await Assert.That(row.Accepted).IsEqualTo(1).Because("history is untouched by a preview");
        }
    }
}
