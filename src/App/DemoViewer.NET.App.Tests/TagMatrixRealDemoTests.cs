#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.RoundTagger;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Matrix over a real Valve matchmaking demo: a tag in every live round, stamped with its round's
///     facts by the production refresher, pivoted code by the T side's buy, and a cell's clips queued
///     against the library path. The facts come from the shipped engine ruleset (see
///     <see cref="RoundFactsRealDemoTests" />).
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class TagMatrixRealDemoTests
{
    [Test]
    public async Task TheRealDemosTaggedRounds_PivotOnTheParsersBuy_AndACellQueuesItsClips()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);

        DemoCacheStore cache = new(null);
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = new FileInfo(path).Length,
            ModifiedTicks = File.GetLastWriteTimeUtc(path).Ticks,
            Sha256 = Sha,
            Map = parsed.MapName
        };
        DemoCacheStore.StampParse(record);
        cache.Upsert(record);

        RoundFactsEvaluator evaluator = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        RoundFactsSource facts = new(cache, evaluator);
        TagStore tags = new(null);
        tags.Save(Document(Sha,
            [.. rounds.Select(r => Instance("execute", r.StartTickFrameClock + 64, r.StartTickFrameClock + 640))]));
        using TagFactsRefresher refresher = new(tags, facts, _ => Sha, action => action());
        evaluator.OnParsedOpportunistically(path, parsed);
        RoundFactsRows rows = facts.TryGet(path) ?? throw new InvalidOperationException("the evaluator wrote no rows");

        ReviewQueue queue = new(null);
        using TagMatrixTabViewModel matrix = new(tags, queue, cache.TryGetIndexBySha256, debounce: TimeSpan.Zero, isBrowser: false);
        matrix.ColumnAxis = TagMatrixAxis.Label(LabelNamespace.Fact, "buy.t");
        await matrix.Pending;

        Dictionary<string, int> expected = tags.TryLoad(Sha)!.Instances
            .Select(i => RoundFactsValues.LowerCamel(RoundFactsSource.FindRound(rows.Rounds, i.FromTick)!.T.BuyType))
            .GroupBy(b => b, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        using (Assert.Multiple())
        {
            await Assert.That(matrix.Table!.RowKeys).IsEquivalentTo(["execute"]);
            foreach ((string buy, int count) in expected)
            {
                await Assert.That(matrix.Table.Count("execute", buy)).IsEqualTo(count).Because($"T buy {buy}");
            }

            await Assert.That(matrix.Table.Total).IsEqualTo(rounds.Count);
        }

        TagMatrixCellViewModel cell = matrix.Rows.Single().Cells.First(c => !c.IsEmpty);
        cell.OpenCommand.Execute(null);
        await Assert.That(queue.Clips.All(c => c.DemoPath == path)).IsTrue();
        await Assert.That(queue.ClipCount).IsEqualTo(cell.Count);
    }
}
