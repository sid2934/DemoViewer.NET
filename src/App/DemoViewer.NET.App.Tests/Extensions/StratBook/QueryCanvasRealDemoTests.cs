#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Query Canvas done bar over a real Valve matchmaking demo: tokens dropped on the index's own
///     place centroids encode to a token the index stored for that demo. The index joins alive and
///     side against the production Round Facts rows.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class QueryCanvasRealDemoTests
{
    [Test]
    public async Task TokensDroppedOnTheIndexCentroids_EncodeToATokenTheIndexStored()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheStore cache = new(null);
        RoundFactsEvaluator facts = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.OnParsedOpportunistically(path, parsed);
        RoundFactsRows rows = cache.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no rows");

        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundIndexDocument document = RoundIndexBuilder.Build(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance);
        RoundIndexTestData.Indexed(cache, sidecars, path, document);
        using SituationIndex index = new(cache, sidecars, sources);
        index.Load();

        // The first run with both sides alive and every place known: its CT token is the target.
        RoundIndexRun run = document.Rounds.SelectMany(r => r.Runs)
            .First(r => r.Ct.Length > 0 && !r.Ct.Contains('?') && r.T.Length > 0 && !r.T.Contains('?'));
        IReadOnlyList<PlaceSummary> places = index.Places(document.Map);
        QueryPlaceResolver resolver = new(index, sources.Zones);
        QueryCanvasDocument canvas = new()
        {
            MapName = document.Map
        };

        // One token per alive player on each side, dropped on the folded centroid of its place over
        // the whole Z range, so the snap's nearest answer is that place by construction.
        foreach ((QuerySide side, string token) in new[] { (QuerySide.Ct, run.Ct), (QuerySide.T, run.T) })
        {
            int slot = 0;
            foreach ((string place, int count) in PlaceCountToken.Decode(token))
            {
                PlaceSummary summary = places.Single(p => p.Place == place);
                double weight = summary.Buckets.Sum(b => b.Count);
                double x = summary.Buckets.Sum(b => b.CentroidX * b.Count) / weight;
                double y = summary.Buckets.Sum(b => b.CentroidY * b.Count) / weight;
                for (int i = 0; i < count; i++)
                {
                    QueryPlaceHit? hit = resolver.Resolve(document.Map, x, y, -100000, 100000);
                    canvas.Place(new QueryToken(side, slot++, (float)x, (float)y, -100000, hit?.Place));
                }
            }
        }

        SituationQueryDraft draft = new(canvas);
        await Assert.That(draft.TokenFor(QuerySide.Ct)).IsEqualTo(run.Ct);
        await Assert.That(draft.TokenFor(QuerySide.T)).IsEqualTo(run.T);
        await Assert.That(index.Count(draft.ToQuery())).IsGreaterThanOrEqualTo(1);
    }
}
