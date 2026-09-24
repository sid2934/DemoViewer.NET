#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tolerance slider over a real Valve matchmaking demo's index: a query placed from a run the
///     demo stored counts at least that demo's round at Exact, and the live count never falls as the
///     slider loosens through the empirical graph the one demo folds. The index joins alive and side
///     against Round Facts rows, and the engine row source writes none until CS2DemoKit #54, so the
///     test is written against the record and skipped with that reason.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ToleranceSliderRealDemoTests
{
    private const string WaitingOnEngine =
        "waiting on CS2DemoKit #54: the index joins alive and side against Round Facts rows, and the engine row source writes none yet";

    [Test]
    [Skip(WaitingOnEngine)]
    public async Task ARunTheDemoStored_CountsMonotonically_AsTheSliderLoosens()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        DemoCacheStore cache = new(null);
        RoundFactsEvaluator facts = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.OnParsedOpportunistically(path, parsed);
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundIndexEvaluator evaluator = new(cache, sidecars, sources, () => true);
        evaluator.Evaluate(path, parsed);
        using SituationIndex index = new(cache, sidecars, sources, evaluator: evaluator);
        index.Load();

        RoundIndexDocument document = sidecars.TryRead(path) ?? throw new InvalidOperationException("no index");
        RoundIndexRun run = document.Rounds.SelectMany(r => r.Runs).First(r => r.Ct.Length > 0 && !r.Ct.Contains('?'));

        using QueryCanvasViewModel canvas = new(index, new QueryPlaceResolver(index), cache, _ => null,
            dispose => dispose(), post: action => action(), countDelay: TimeSpan.Zero);
        canvas.Map = document.Map;
        int slot = 0;
        foreach ((string place, int count) in PlaceCountToken.Decode(run.Ct))
        {
            for (int i = 0; i < count; i++)
            {
                canvas.Document.Place(new QueryToken(QuerySide.Ct, slot++, 0, 0, 0, place));
            }
        }

        await Assert.That(canvas.AdjacencySource).IsEqualTo("empirical");
        int previous = 0;
        for (int stop = 0; stop <= canvas.ToleranceMaximum; stop++)
        {
            canvas.ToleranceValue = stop;
            await canvas.Counter.Pending;
            int count = canvas.LiveCount ?? -1;
            await Assert.That(count).IsGreaterThanOrEqualTo(stop == 0 ? 1 : previous);
            previous = count;
        }
    }
}
