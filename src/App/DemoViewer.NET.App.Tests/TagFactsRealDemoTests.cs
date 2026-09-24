#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.TestSupport;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Free Labels From Round Facts on a real Valve matchmaking demo: a tag made in every live round carries
///     that round's facts. It needs rows, and rows need CS2DemoKit #54 (see
///     <see cref="RoundFactsRealDemoTests" />), so it is skipped with that reason until the pin bumps; the
///     body runs the production evaluator and the production refresher against the parse.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class TagFactsRealDemoTests
{
    private const string WaitingOnEngine = "waiting on CS2DemoKit #54";

    [Test]
    [Skip(WaitingOnEngine)]
    public async Task ATagInEveryLiveRound_CarriesThatRoundsFacts()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);

        DemoCacheStore cache = new(null);
        RoundFactsEvaluator evaluator = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        RoundFactsSource facts = new(cache, evaluator);
        TagStore tags = new(null);
        TagDocument document = Document(Sha,
            [.. rounds.Select(r => Instance("execute", r.StartTickFrameClock + 64, r.StartTickFrameClock + 640, ("outcome", "won")))]);
        tags.Save(document);
        using TagFactsRefresher refresher = new(tags, facts, _ => Sha, action => action());

        // The evaluator's write raises Updated, which is the refresh.
        evaluator.OnParsedOpportunistically(path, parsed);

        RoundFactsRows rows = facts.TryGet(path) ?? throw new InvalidOperationException("the evaluator wrote no rows");
        TagDocument refreshed = tags.TryLoad(Sha)!;
        using (Assert.Multiple())
        {
            foreach (TagInstance instance in refreshed.Instances)
            {
                RoundFacts round = RoundFactsSource.FindRound(rows.Rounds, instance.FromTick)!;
                await Assert.That(instance.Round).IsEqualTo(round.Number);
                await Assert.That(instance.Facts.Single(f => f.Group == "buy.ct").Value)
                    .IsEqualTo(RoundFactsValues.LowerCamel(round.Ct.BuyType));
                await Assert.That(instance.Facts.Single(f => f.Group == "buy.t").Value)
                    .IsEqualTo(RoundFactsValues.LowerCamel(round.T.BuyType));
                await Assert.That(instance.Facts.Single(f => f.Group == "winner").Value).IsEqualTo(round.WinnerLabel);
                await Assert.That(instance.Labels.Single().Value).IsEqualTo("won");
                await Assert.That(instance.FactsStamp!.Stale).IsFalse();
            }
        }
    }
}
