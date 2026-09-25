#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tuning view's pure half (suggested-tags.md §3.7, step 6): verdict aggregation and the
///     overlap-rule score, both exercised with hand-built fixtures — no demo, no parse, no store.
/// </summary>
public class SuggestedTagsTuningTests
{
    private static SuggestionVerdict Verdict(string verdict) => new() { Verdict = verdict, At = Now };

    private static ProposalEntry Entry(TagProposal proposal, SuggestionVerdict? verdict) =>
        new(proposal, verdict is null ? null : proposal.Id, verdict);

    [Test]
    public async Task Aggregate_CountsEveryVerdictKindPerDetector()
    {
        TagProposal e1 = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        TagProposal e2 = Proposal("exec|r2|T|BombsiteA|s=10", "execute", 2, 300, 400, 350, 0.7);
        TagProposal e3 = Proposal("exec|r3|T|BombsiteA|s=10", "execute", 3, 500, 600, 550, 0.6);
        TagProposal d1 = Proposal("default|r1|T", "default", 1, 700, 800, 750, 0.5, null);

        Dictionary<string, VerdictCounts> counts = SuggestedTagsTuning.Aggregate(
        [
            Entry(e1, Verdict(SuggestionVerdicts.Accepted)),
            Entry(e2, Verdict(SuggestionVerdicts.Edited)),
            Entry(e3, Verdict(SuggestionVerdicts.Rejected)),
            Entry(d1, null)
        ]);

        using (Assert.Multiple())
        {
            await Assert.That(counts["execute"]).IsEqualTo(new VerdictCounts(3, 1, 1, 1, 0));
            await Assert.That(counts["default"]).IsEqualTo(new VerdictCounts(1, 0, 0, 0, 1));
        }
    }

    [Test]
    public async Task Score_NoHandTags_IsNoDataForBothNumbers()
    {
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([fired], []);

        using (Assert.Multiple())
        {
            await Assert.That(recall).IsNull().Because("no ground truth for this code exists yet");
            await Assert.That(precision).IsNull().Because("a fired proposal cannot be judged right or wrong against nothing");
        }
    }

    [Test]
    public async Task Score_HandTagWithNothingFired_RecallZeroPrecisionNoData()
    {
        (double? recall, double? precision) = SuggestedTagsTuning.Score([], [new HandTagWindow(1, 100, 200)]);

        using (Assert.Multiple())
        {
            await Assert.That(recall).IsEqualTo(0.0).Because("a real hand tag exists and nothing matched it");
            await Assert.That(precision).IsNull().Because("nothing fired: 0/0 is undefined, not 0%");
        }
    }

    [Test]
    public async Task Score_FullOverlap_MatchesBoth()
    {
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        HandTagWindow hand = new(1, 100, 200);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([fired], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)1.0, (double?)1.0));
    }

    [Test]
    public async Task Score_OverlapExactlyHalfTheShorterWindow_Matches()
    {
        // Fired window is 100 ticks wide (100..200); a hand tag overlapping exactly 50 of it is the
        // §7.3 item 3 boundary ("at least half of the shorter one") and must still match.
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        HandTagWindow hand = new(1, 150, 250);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([fired], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)1.0, (double?)1.0));
    }

    [Test]
    public async Task Score_OverlapUnderHalf_DoesNotMatch()
    {
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        HandTagWindow hand = new(1, 190, 290); // 10 of the fired window's 100, well under half

        (double? recall, double? precision) = SuggestedTagsTuning.Score([fired], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)0.0, (double?)0.0));
    }

    [Test]
    public async Task Score_DifferentRounds_NeverMatch()
    {
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        HandTagWindow hand = new(2, 100, 200); // identical ticks, different round

        (double? recall, double? precision) = SuggestedTagsTuning.Score([fired], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)0.0, (double?)0.0));
    }

    [Test]
    public async Task Score_GreedyPairing_TakesTheLargestOverlapFirst()
    {
        // Two fired proposals both plausibly match one hand tag; the closer one (full overlap) must
        // win it, leaving the other fired proposal unmatched — a one-to-one pairing, not double counting.
        TagProposal close = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        TagProposal far = Proposal("exec|r1|T|BombsiteA|s=30", "execute", 1, 140, 240, 190, 0.6);
        HandTagWindow hand = new(1, 100, 200);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([close, far], [hand]);

        using (Assert.Multiple())
        {
            await Assert.That(recall).IsEqualTo(1.0).Because("the one hand tag was matched");
            await Assert.That(precision).IsEqualTo(0.5).Because("one of the two fired proposals matched");
        }
    }

    [Test]
    public async Task Build_GivesEveryShippedDetectorARow_EvenWithNothingFired()
    {
        TuningReport report = SuggestedTagsTuning.Build([],
            new Dictionary<string, IReadOnlyList<TagProposal>>(),
            new Dictionary<string, IReadOnlyList<HandTagWindow>>(), 0, 0);

        await Assert.That(report.Rows.Select(r => r.Detector))
            .IsEquivalentTo(ProposalDetection.All.Select(d => d.Id));
        await Assert.That(report.Rows.All(r => r is { Made: 0, Recall: null, Precision: null })).IsTrue();
    }
}
