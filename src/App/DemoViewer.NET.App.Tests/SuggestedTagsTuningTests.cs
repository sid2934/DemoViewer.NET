#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tuning view's pure half (suggested-tags.md §3.7, step 6): verdict aggregation and the
///     overlap-rule score, both exercised with hand-built fixtures: no demo, no parse, no store.
/// </summary>
public class SuggestedTagsTuningTests
{
    private static SuggestionVerdict Verdict(string verdict) => new() { Verdict = verdict, At = Now };

    private static ProposalEntry Entry(TagProposal proposal, SuggestionVerdict? verdict) =>
        new(proposal, verdict is null ? null : proposal.Id, verdict);

    private const string DemoA = "demo-a";
    private const string DemoB = "demo-b";

    private static FiredProposal In(string demo, TagProposal proposal) => new(demo, proposal);

    // Demo A and BombsiteA unless a test says otherwise: the site every hand-built execute carries.
    private static HandTagWindow Hand(int round, int from, int to, string? site = "BombsiteA", string demo = DemoA) =>
        new(demo, round, from, to, site);

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

        (double? recall, double? precision) = SuggestedTagsTuning.Score([In(DemoA, fired)], []);

        using (Assert.Multiple())
        {
            await Assert.That(recall).IsNull().Because("no ground truth for this code exists yet");
            await Assert.That(precision).IsNull().Because("a fired proposal cannot be judged right or wrong against nothing");
        }
    }

    [Test]
    public async Task Score_HandTagWithNothingFired_RecallZeroPrecisionNoData()
    {
        (double? recall, double? precision) = SuggestedTagsTuning.Score([], [Hand(1, 100, 200)]);

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
        HandTagWindow hand = Hand(1, 100, 200);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([In(DemoA, fired)], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)1.0, (double?)1.0));
    }

    [Test]
    public async Task Score_OverlapExactlyHalfTheShorterWindow_Matches()
    {
        // Fired window is 100 ticks wide (100..200); a hand tag overlapping exactly 50 of it is the
        // §7.3 item 3 boundary ("at least half of the shorter one") and must still match.
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        HandTagWindow hand = Hand(1, 150, 250);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([In(DemoA, fired)], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)1.0, (double?)1.0));
    }

    [Test]
    public async Task Score_OverlapUnderHalf_DoesNotMatch()
    {
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        HandTagWindow hand = Hand(1, 190, 290); // 10 of the fired window's 100, well under half

        (double? recall, double? precision) = SuggestedTagsTuning.Score([In(DemoA, fired)], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)0.0, (double?)0.0));
    }

    [Test]
    public async Task Score_DifferentRounds_NeverMatch()
    {
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        HandTagWindow hand = Hand(2, 100, 200); // identical ticks, different round

        (double? recall, double? precision) = SuggestedTagsTuning.Score([In(DemoA, fired)], [hand]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)0.0, (double?)0.0));
    }

    [Test]
    public async Task Score_GreedyPairing_TakesTheLargestOverlapFirst()
    {
        // Two fired proposals both plausibly match one hand tag; the closer one (full overlap) must
        // win it, leaving the other fired proposal unmatched: a one-to-one pairing, not double counting.
        TagProposal close = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        TagProposal far = Proposal("exec|r1|T|BombsiteA|s=30", "execute", 1, 140, 240, 190, 0.6);
        HandTagWindow hand = Hand(1, 100, 200);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([In(DemoA, close), In(DemoA, far)], [hand]);

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
            new Dictionary<string, IReadOnlyList<FiredProposal>>(),
            new Dictionary<string, IReadOnlyList<HandTagWindow>>(), 0, 0);

        await Assert.That(report.Rows.Select(r => r.Detector))
            .IsEquivalentTo(ProposalDetection.All.Select(d => d.Id));
        await Assert.That(report.Rows.All(r => r is { Made: 0, Recall: null, Precision: null })).IsTrue();
    }

    [Test]
    public async Task Score_SameRoundAndWindowInTwoDemos_NeverMatchesAcrossThem()
    {
        // Round 1 of demo A and round 1 of demo B are different rounds that happen to share a number
        // and a tick range; A's proposal and B's hand tag must not pair up.
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);

        (double? recall, double? precision) = SuggestedTagsTuning.Score(
            [In(DemoA, fired)], [Hand(1, 100, 200, demo: DemoB)]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)0.0, (double?)0.0));
    }

    [Test]
    public async Task Score_TwoDemos_EachProposalMatchesOnlyItsOwnDemosHandTag()
    {
        // A's proposal sits exactly on B's hand tag and B's proposal exactly on A's; within each demo the
        // overlap is 50 of 100, the §7.3 boundary. Pairing by the larger overlap across demos would
        // cross them; matching within one demo keeps both, and both still count.
        TagProposal inA = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        TagProposal inB = Proposal("exec|r1|T|BombsiteA|s=11", "execute", 1, 150, 250, 200, 0.8);

        (double? recall, double? precision) = SuggestedTagsTuning.Score(
            [In(DemoA, inA), In(DemoB, inB)],
            [Hand(1, 150, 250, demo: DemoA), Hand(1, 100, 200, demo: DemoB)]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)1.0, (double?)1.0));
    }

    [Test]
    public async Task Score_TwoExecutesAtDifferentSitesInOneRound_MatchOnlyTheirOwnSite()
    {
        // Two executes in round 1, one at each site. The hand tag windows are swapped against the sites
        // (the A-labelled hand tag lies on the B execute's window and vice versa), so pairing on window
        // alone scores two perfect matches where §7.3 item 3 says there are none.
        TagProposal atA = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8, "BombsiteA");
        TagProposal atB = Proposal("exec|r1|T|BombsiteB|s=40", "execute", 1, 300, 400, 350, 0.7, "BombsiteB");

        (double? recall, double? precision) = SuggestedTagsTuning.Score(
            [In(DemoA, atA), In(DemoA, atB)],
            [Hand(1, 300, 400, "BombsiteA"), Hand(1, 100, 200, "BombsiteB")]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)0.0, (double?)0.0));
    }

    [Test]
    public async Task Score_TwoExecutesAtDifferentSitesInOneRound_EachMatchesItsSite()
    {
        TagProposal atA = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8, "BombsiteA");
        TagProposal atB = Proposal("exec|r1|T|BombsiteB|s=12", "execute", 1, 110, 210, 160, 0.7, "BombsiteB");

        // Each hand tag overlaps the other site's execute more (100) than its own (90); with the site
        // compared, each execute still pairs with the tag of its own site.
        (double? recall, double? precision) = SuggestedTagsTuning.Score(
            [In(DemoA, atA), In(DemoA, atB)],
            [Hand(1, 110, 210, "BombsiteA"), Hand(1, 100, 200, "BombsiteB")]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)1.0, (double?)1.0));
    }

    [Test]
    public async Task Score_SiteBearingProposal_DoesNotMatchAHandTagWithNoSite()
    {
        TagProposal fired = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);

        (double? recall, double? precision) = SuggestedTagsTuning.Score([In(DemoA, fired)], [Hand(1, 100, 200, null)]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)0.0, (double?)0.0));
    }

    [Test]
    public async Task Score_SitelessCode_IgnoresTheHandTagsSite()
    {
        TagProposal fired = Proposal("default|r1|T", "default", 1, 100, 200, 150, 0.5, null);

        (double? recall, double? precision) = SuggestedTagsTuning.Score(
            [In(DemoA, fired)], [Hand(1, 100, 200, "BombsiteB")]);

        await Assert.That((recall, precision)).IsEqualTo(((double?)1.0, (double?)1.0));
    }

    [Test]
    public async Task SiteOf_ReadsSiteThenTheFakedSite()
    {
        TagProposal execute = Proposal("exec|r1|T|BombsiteA|s=10", "execute", 1, 100, 200, 150, 0.8);
        TagProposal fake = Proposal("fake|r1|T|BombsiteB>BombsiteA", "fake", 1, 100, 200, 150, 0.5, null) with
        {
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["fake"] = "BombsiteB", ["real"] = "BombsiteA" }
        };
        TagProposal opener = Proposal("opener|r1|T", "opener", 1, 100, 200, 150, 0.5, null);

        using (Assert.Multiple())
        {
            await Assert.That(SuggestedTagsTuning.SiteOf(execute)).IsEqualTo("BombsiteA");
            await Assert.That(SuggestedTagsTuning.SiteOf(fake)).IsEqualTo("BombsiteB");
            await Assert.That(SuggestedTagsTuning.SiteOf(opener)).IsNull();
        }
    }
}
