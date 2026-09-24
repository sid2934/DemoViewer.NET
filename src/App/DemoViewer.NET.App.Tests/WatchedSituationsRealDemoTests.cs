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
///     Watched Situations over a real Valve matchmaking demo: a watch on a run the demo's own index
///     stored counts that demo as new once its stamp is past the watermark, on the hook and after a
///     restart alike. The index joins alive and side against Round Facts rows, and the engine row
///     source writes none until CS2DemoKit #54, so the test is written against the record and skipped
///     with that reason.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class WatchedSituationsRealDemoTests
{
    private const string WaitingOnEngine =
        "waiting on CS2DemoKit #54: the index joins alive and side against Round Facts rows, and the engine row source writes none yet";

    [Test]
    [Skip(WaitingOnEngine)]
    public async Task AWatchOnARunTheDemoStored_CountsTheDemoAsNew_OnTheHookAndAfterARestart()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        string root = Path.Combine(Path.GetTempPath(), $"dv-watched-real-{Guid.NewGuid():N}");
        try
        {
            DemoCacheStore cache = new(null);
            RoundFactsEvaluator facts = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
            facts.OnParsedOpportunistically(path, parsed);
            using RoundIndexStore sidecars = new(null, cache);
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            RoundIndexEvaluator evaluator = new(cache, sidecars, sources, () => true);
            using SituationIndex index = new(cache, sidecars, sources, evaluator: evaluator);
            index.Load();

            // The run is only known once the demo is indexed, so the watch is saved against the
            // document and the hook is driven by indexing the demo a second time.
            evaluator.Evaluate(path, parsed);
            RoundIndexDocument document = sidecars.TryRead(path) ?? throw new InvalidOperationException("no index");
            RoundIndexRun run = document.Rounds.SelectMany(r => r.Runs).First(r => r.Ct.Length > 0 && !r.Ct.Contains('?'));
            int slot = 0;
            List<QueryToken> tokens = [];
            foreach ((string place, int count) in PlaceCountToken.Decode(run.Ct))
            {
                for (int i = 0; i < count; i++)
                {
                    tokens.Add(new QueryToken(QuerySide.Ct, slot++, 0, 0, 0, place));
                }
            }

            using WatchedSituationsService service = new(root, index, cache, now: () => 1000);
            WatchedSituation watch = service.Watch("", document.Map, tokens, SituationTolerance.Exact, SearchFilterValues.None);
            evaluator.RebuildAll();
            evaluator.Evaluate(path, parsed);
            await Assert.That(service.NewCountOf(watch.Id)).IsGreaterThanOrEqualTo(1);

            using WatchedSituationsService restarted = new(root, index, cache, now: () => 1000);
            await Assert.That(restarted.NewCountOf(watch.Id)).IsEqualTo(service.NewCountOf(watch.Id));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
