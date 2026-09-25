#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Search Filters And Live Count against a real Valve matchmaking demo: a fact filter narrows the
///     index's hits, and the count equals the result set and the live counter's answer, the way
///     round-index.md §3.11 asks for it "on a fixture and on a real demo". The filter reads Round Facts
///     rows through the production evaluators, joined against the shipped engine ruleset.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class SearchFiltersRealDemoTests
{
    [Test]
    public async Task AFactFilter_NarrowsTheRealHits_AndTheCountEqualsTheResultSet()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        DemoCacheStore store = new(null);
        using RoundIndexStore sidecars = new(null, store);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundFactsEvaluator facts = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        RoundFactsSource factsSource = new(store, facts);
        RoundIndexEvaluator evaluator = new(store, sidecars, sources, () => true);
        using SituationIndex index = new(store, sidecars, sources, factsSource, evaluator: evaluator);
        index.Load();

        store.Upsert(new DemoCacheRecord
        {
            Path = path,
            Map = parsed.MapName,
            Parse = new TierStamp
            {
                Schema = DemoCacheRecord.ParseSchema,
                ComputedAtTicks = 1
            }
        });
        facts.OnParsedOpportunistically(path, parsed);
        evaluator.OnParsedOpportunistically(path, parsed);
        await Assert.That(index.IndexedDemoCount).IsEqualTo(1);

        SituationQuery all = new(parsed.MapName, [], []);
        SituationQuery postPlant = all with
        {
            Facts = new RoundFactsFilter
            {
                Phase = RoundPhase.PostPlant
            }
        };
        SituationQuery ctFullLate = all with
        {
            Facts = new RoundFactsFilter
            {
                BuyCt = BuyType.Full,
                ClockBand = ClockBand.Late
            }
        };

        List<(SituationQuery Query, int Count)> counted = [];
        using SituationLiveCount counter = new(index, action => action(), TimeSpan.Zero);
        counter.Counted += (query, count) => counted.Add((query, count));
        counter.Request(postPlant);
        await counter.Pending;

        IReadOnlyList<SituationHit> everyRound = index.Query(all);
        IReadOnlyList<SituationHit> planted = index.Query(postPlant);
        IReadOnlyList<SituationHit> fullAndLate = index.Query(ctFullLate);
        RoundFactsRows rows = factsSource.TryGet(path) ?? throw new InvalidOperationException("the evaluator wrote no rows");

        using (Assert.Multiple())
        {
            await Assert.That(index.Count(all)).IsEqualTo(everyRound.Count);
            await Assert.That(index.Count(postPlant)).IsEqualTo(planted.Count);
            await Assert.That(index.Count(ctFullLate)).IsEqualTo(fullAndLate.Count);
            await Assert.That(counted.Select(c => c.Count)).IsEquivalentTo([planted.Count]).Because("the live counter runs the same path");
            await Assert.That(planted.Count).IsEqualTo(rows.Rounds.Count(r => r.IsLive && r.PlantTick is not null))
                .Because("every planted round has a sampled tick after its plant");
            foreach (SituationHit hit in planted)
            {
                RoundFacts round = rows.Rounds.Single(r => r.Number == hit.RoundNumber);
                await Assert.That(hit.FirstMatchTick).IsGreaterThanOrEqualTo(round.PlantTick!.Value)
                    .Because("the window narrows to the post-plant steps");
            }

            await Assert.That(fullAndLate.Count).IsLessThanOrEqualTo(everyRound.Count);
        }
    }
}
