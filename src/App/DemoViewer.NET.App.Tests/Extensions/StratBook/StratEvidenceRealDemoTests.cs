#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.TestSupport;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Record Panel's won/lost rule on a real Valve matchmaking demo: every live round tagged as a run of
///     a T-side strat, the facts refreshed from the production Round Facts evaluator, and each run's outcome read
///     from the round's <c>winner</c> rather than the human label. The rows come from the production Round
///     Facts evaluator over the shipped engine ruleset; the synthetic variant is in
///     <see cref="StratEvidenceTests" />.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class StratEvidenceRealDemoTests
{
    [Test]
    public async Task EveryTaggedRound_IsWonOrLost_ByTheRoundsWinner()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);
        Guid stratId = Guid.NewGuid();

        DemoCacheStore cache = new(null);
        RoundFactsEvaluator evaluator = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        RoundFactsSource facts = new(cache, evaluator);
        TagStore tags = new(null);

        // The human label says "won" on every round, so a record that read it would show no losses.
        tags.Save(Document(Sha,
        [
            .. rounds.Select(r => Instance("A execute", r.StartTickFrameClock + 64, r.StartTickFrameClock + 640,
                (TagStore.StratGroup, stratId.ToString("D")), (StratEvidence.RevisionGroup, "1"), (StratEvidence.OutcomeGroup, StratEvidence.OutcomeWon)))
        ]));
        using TagFactsRefresher refresher = new(tags, facts, _ => Sha, action => action());
        evaluator.OnParsedOpportunistically(path, parsed);

        RoundFactsRows rows = facts.TryGet(path) ?? throw new InvalidOperationException("the evaluator wrote no rows");
        StratDocument strat = StratDocument.Create(stratId, StratOwner.Me(), parsed.MapName ?? "de_mirage", StratVocabulary.SideT, "execute", "real", Created);
        StratRecord record = new StratEvidenceService(tags).Compute(strat);

        using (Assert.Multiple())
        {
            await Assert.That(record.Runs.Count).IsEqualTo(rounds.Count);
            foreach (StratRun run in record.Runs)
            {
                RoundFacts round = RoundFactsSource.FindRound(rows.Rounds, run.Ref.FromTick)!;
                RunOutcome expected = round.WinnerLabel switch
                {
                    StratVocabulary.SideT => RunOutcome.Won,
                    StratVocabulary.SideCt => RunOutcome.Lost,
                    _ => RunOutcome.Won
                };
                await Assert.That(run.Outcome).IsEqualTo(expected);
            }

            await Assert.That(record.Total.Lost).IsGreaterThan(0).Because("a real match has rounds the T side lost");
        }
    }
}
