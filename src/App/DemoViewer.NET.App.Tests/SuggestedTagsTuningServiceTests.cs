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
    // A tagger following §7.3 item 1 puts the site label on a site-bearing code, so the fixture does too.
    private static TagInstance HumanTagFor(TagProposal proposal, string? site = null)
    {
        TagInstance instance = new()
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
        if ((site ?? proposal.Labels.GetValueOrDefault("site")) is { } label)
        {
            instance.Labels.Add(new TagLabel("site", label));
        }

        return instance;
    }

    private static readonly ClockIdentity Clock = new(ClockIdentity.DvFrameClock, 64, 2, 1, 20000);

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

    [Test]
    public async Task BuildStoredReport_NeverMatchesAProposalAgainstAnotherDemosHandTag()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build(); // demo A: the execute fires in round 1

        // Demo B: same rounds and the same walk, but a profile under which the execute never fires, so
        // its only execute-coded evidence is a hand tag sitting exactly on A's execute window. Round
        // numbers and ticks coincide across the two demos; only the demo identity tells them apart.
        const string otherPath = "/d/other.dem";
        string otherSha = new('b', 64);
        h.Cache.Upsert(RoundIndexTestData.ParsedRecord(otherPath, SuggestedTagsTestData.Map, otherSha,
            SuggestedTagsReviewHarness.Rows()));
        h.Profile = h.Profile.With("execute", "minSecond", 100);
        h.Service.Evaluate(otherPath, Parse());
        h.Profile = DetectorProfile.Default;

        TagProposal execute = h.Service.Load(DemoPath).Entries.Single(e => e.Proposal.Detector == "execute").Proposal;
        await Assert.That(h.Service.Load(otherPath).Entries.Any(e => e.Proposal.Detector == "execute")).IsFalse();
        h.Tags.Append(new DemoIdentity(otherSha, "other.dem", 10), Clock, HumanTagFor(execute));

        TuningReport report = Tuning(h).BuildStoredReport();
        DetectorTuningRow row = report.Rows.Single(r => r.Detector == "execute");

        using (Assert.Multiple())
        {
            await Assert.That(row.Recall).IsEqualTo(0.0).Because("B's hand tag has no execute in B to match");
            await Assert.That(row.Precision).IsEqualTo(0.0).Because("A's execute has no hand tag in A");
        }
    }

    [Test]
    public async Task BuildStoredReport_HandTagAtAnotherSite_DoesNotMatch()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        TagProposal execute = h.Service.Load(DemoPath).Entries.Single(e => e.Proposal.Detector == "execute").Proposal;
        await Assert.That(execute.Labels["site"]).IsEqualTo("BombsiteA");
        h.Tags.Append(new DemoIdentity(Sha, "review.dem", 10), Clock, HumanTagFor(execute, "BombsiteB"));

        DetectorTuningRow row = Tuning(h).BuildStoredReport().Rows.Single(r => r.Detector == "execute");

        using (Assert.Multiple())
        {
            await Assert.That(row.Recall).IsEqualTo(0.0).Because("§7.3 item 3: code and site must both agree");
            await Assert.That(row.Precision).IsEqualTo(0.0);
        }
    }
}
