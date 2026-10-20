#region

using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The scoreboard Match Overview projects from a library visit's <c>player_match</c> facet, against the Stats
///     tab's game table of the same demo: the nine fields row for row, and the side wins the facts pass stores on
///     the record. Before the facet these came from the open's table, so a cached render must not move a number.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class MatchOverviewFacetScoreboardRealDemoTests
{
    private static int Whole(object? value) => Convert.ToInt32(Convert.ToDouble(value, CultureInfo.InvariantCulture));

    private static double Number(object? value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    [Test]
    public async Task TheFacetScoreboard_IsTheStatsTabsScoreboard()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules();
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
        });
        FactsEvaluator facts = new(cache, stamped);
        AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache));
        IReadOnlySet<string> cut = rules.StampedOutputs(id => facts.Records(path, id));
        using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
        {
            facts.EvaluateForward(path, ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, outputs: cut));
        }

        FactTable facet = library.TryGet(path, PlayerMatchFacet.Key) ?? throw new InvalidOperationException("the visit wrote no facet");
        List<CachedStatRow> projected = DemoCacheAnalysisProjector.ProjectScoreboard(facet);
        DemoCacheRecord record = cache.TryLoadRecord(path)!;

        AnalysisOptions options = new();
        AnalysisRun retained = DemoAnalysis.Evaluate(parsed, DemoAnalysis.Build(parsed, rules.CoreDocs, options), options);
        MetricTable game = new PlayerGameStatsProjector().Project(retained.Snapshots!, parsed).Single();
        List<CachedStatRow> statsTab =
        [
            .. game.Rows.Where(r => r.Dimensions.GetValueOrDefault("player_slot") is not null).Select(r => new CachedStatRow
            {
                Slot = Whole(r.Dimensions["player_slot"]),
                Team = Whole(r.Dimensions.GetValueOrDefault("team")),
                Kills = Whole(r.Values["TotalK"]),
                Deaths = Whole(r.Values["TotalD"]),
                Assists = Whole(r.Values["TotalA"]),
                Adr = Number(r.Values["ADR"]),
                Rating = Number(r.Values["HLTV"]),
                CtWins = Whole(r.Values["CTW"]),
                TWins = Whole(r.Values["TW"])
            })
        ];

        Dictionary<int, CachedStatRow> bySlot = projected.ToDictionary(r => r.Slot);
        List<string> mismatches = [];
        foreach (CachedStatRow expected in statsTab)
        {
            if (!bySlot.TryGetValue(expected.Slot, out CachedStatRow? actual))
            {
                mismatches.Add($"slot {expected.Slot}: no facet row");
                continue;
            }

            if ((actual.Team, actual.Kills, actual.Deaths, actual.Assists, actual.CtWins, actual.TWins)
                != (expected.Team, expected.Kills, expected.Deaths, expected.Assists, expected.CtWins, expected.TWins)
                || Math.Abs(actual.Adr - expected.Adr) > 1e-9
                || Math.Abs(actual.Rating - expected.Rating) > 1e-9)
            {
                mismatches.Add($"slot {expected.Slot}: facet {actual.Team} {actual.Kills}/{actual.Deaths}/{actual.Assists} {actual.Adr} {actual.Rating} {actual.CtWins}:{actual.TWins}, "
                               + $"Stats tab {expected.Team} {expected.Kills}/{expected.Deaths}/{expected.Assists} {expected.Adr} {expected.Rating} {expected.CtWins}:{expected.TWins}");
            }
        }

        (int? ct, int? t) = DemoCacheAnalysisProjector.ComputeSideWins(statsTab);
        Console.WriteLine($"{Path.GetFileName(path)}: {statsTab.Count} Stats tab rows, {projected.Count} facet rows, sides {ct}:{t}");

        using (Assert.Multiple())
        {
            await Assert.That(statsTab.Count).IsGreaterThanOrEqualTo(10);
            await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches));
            await Assert.That(record.CtSideWins).IsEqualTo(ct).Because("the facts pass stores the side wins the open used to");
            await Assert.That(record.TSideWins).IsEqualTo(t);
            await Assert.That(cache.TryGetIndex(path)!.CtSideWins).IsEqualTo(ct).Because("the index row carries them to Strat Book");
            await Assert.That(DemoCacheAnalysisProjector.Rounds(facet)).IsGreaterThan(0);
        }
    }
}
