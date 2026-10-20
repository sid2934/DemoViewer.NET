#region

using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>player_match</c> facet a library visit writes, against the Stats tab of the same demo: one forward
///     read through the facts pass beside a retained run of the core rulesets projected the way the Stats tab
///     projects it. Every pooling column of one demo must come back as the Stats tab's value, the round count must
///     be the match's, and the multi-kill buckets must be <c>kast_game_totals</c>'. Point <c>DEMO_PATH</c> (with
///     <c>DEMO_PATH_PICK</c>) at a reconnect demo to cover ghost slots.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class PlayerMatchParityRealDemoTests
{
    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static readonly string[] Buckets = ["2K", "3K", "4K", "5K"];

    /// <summary>The app's stamped set over the shipped rules: round facts, the two player facets and the library tables.</summary>
    internal static MergedRulesBuild AppRules(string? user = null) => new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user),
        () =>
        [
            StampedRuleset.Core(RoundFactsFingerprint.RulesetId),
            StampedRuleset.Core(PlayerMatchFacet.RulesetId),
            StampedRuleset.Core(PlayerEventFacet.RulesetId),
            .. LibraryTeamTables.RulesetIds.Select(StampedRuleset.Core)
        ]);

    private static double Cell(object? value) => value is null ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    // The decimals the Stats tab rounds a scoreboard column to: its stat's format:, F1 when it has none.
    private static Dictionary<string, int> Decimals(IReadOnlyList<RulesetDoc> docs)
    {
        Dictionary<string, int> decimals = new(StringComparer.Ordinal);
        foreach (RulesetDoc doc in docs)
        {
            foreach (ScoreboardEntry entry in doc.Show?.Scoreboard ?? [])
            {
                string? format = doc.Stats.FirstOrDefault(s => string.Equals(s.Id, entry.Stat, StringComparison.Ordinal))?.Format;
                decimals[entry.Label ?? entry.Stat] = format is ['F', .. var digits] && int.TryParse(digits, out int d) ? d : 1;
            }
        }

        return decimals;
    }

    // One CT and one T row of a real facet, the accounts and names replaced: the format is the point, not the players.
    private static void WriteSample(string path, FactTable facet)
    {
        List<FactRow> rows = [];
        foreach (long team in (long[]) [3, 2])
        {
            FactRow real = facet.Rows.Where(r => r.Dimensions["team"].AsInteger() == team && r.Dimensions["steam_id"].AsText() is not null)
                .OrderBy(r => r.Dimensions["player_slot"].AsInteger()).First();
            Dictionary<string, FactValue> dimensions = new(real.Dimensions, StringComparer.Ordinal)
            {
                ["player_name"] = FactValue.Of(team == 3 ? "player_a" : "player_b"),
                ["steam_id"] = FactValue.Of(team == 3 ? "76561190000000001" : "76561190000000002")
            };
            rows.Add(new FactRow(dimensions, real.Values));
        }

        FactTable sample = facet with { Fingerprint = "sample", Rows = rows };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, PlayerMatchFacetTests.Indent(FactsCodec.Encode(sample)));
    }

    [Test]
    public async Task TheFacet_IsTheStatsTabsGameTable_RowForRow()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        MergedRulesBuild rules = AppRules();
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 },
            Players =
            [
                .. parsed.Players.Values.Where(p => !p.IsHltv && !string.IsNullOrWhiteSpace(p.Name)).Select(p => new CachedPlayerInfo
                {
                    Slot = p.Slot,
                    Name = p.Name,
                    SteamId64 = p.SteamId64.ToString(CultureInfo.InvariantCulture),
                    Team = p.Team,
                    IsBot = p.IsBot
                })
            ]
        });
        FactsEvaluator facts = new(cache, stamped);
        AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache));
        IReadOnlySet<string> cut = rules.StampedOutputs(id => facts.Records(path, id));
        using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
        {
            facts.EvaluateForward(path, ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, outputs: cut));
        }

        FactTable facet = library.TryGet(path, PlayerMatchFacet.Key) ?? throw new InvalidOperationException("the visit wrote no facet");
        DemoCacheIndexEntry row = cache.TryGetIndex(path)!;
        if (Environment.GetEnvironmentVariable(PlayerMatchFacetTests.SampleUpdateVar) == "1" && DemoTestHelper.FindRepoRoot() is { } repo)
        {
            WriteSample(PlayerMatchFacetTests.SamplePath(repo), facet);
        }

        AnalysisOptions options = new();
        AnalysisRun retained = DemoAnalysis.Evaluate(parsed, DemoAnalysis.Build(parsed, rules.CoreDocs, options), options);
        Dictionary<long, MetricRow> game = new PlayerGameStatsProjector().Project(retained.Snapshots!, parsed).Single().Rows
            .ToDictionary(r => Convert.ToInt64(r.Dimensions["player_slot"], CultureInfo.InvariantCulture));
        Dictionary<long, MetricRow> kastTotals = retained.ProjectConfiguredOutputs().Single(t => t.Name == "kast_game_totals").Rows
            .ToDictionary(r => Convert.ToInt64(r.Dimensions["player_slot"], CultureInfo.InvariantCulture));
        int matchRounds = new PlayerRoundStatsProjector().Project(retained.Snapshots!, parsed).Single().Rows
            .Select(r => Convert.ToInt32(r.Dimensions["round_number"], CultureInfo.InvariantCulture)).Distinct().Count();
        Dictionary<string, int> decimals = Decimals(rules.CoreDocs);

        // An account on one slot is pooled the way the Player Overview pools it, so one demo is the Stats tab's row.
        Dictionary<long, PlayerAggregate> aggregates = facet.Rows
            .Where(r => r.Dimensions["steam_id"].AsText() is { Length: > 0 } id && id != "0")
            .GroupBy(r => r.Dimensions["steam_id"].AsText()!)
            .Where(g => g.Count() == 1)
            .Select(g => (Slot: g.Single().Dimensions["player_slot"].AsInteger()!.Value,
                Stats: PlayerAggregate.Merge(facet.Rows, ulong.Parse(g.Key, CultureInfo.InvariantCulture))))
            .Where(a => a.Stats is not null)
            .ToDictionary(a => a.Slot, a => PlayerAggregate.Of([new PlayerDemo(new PlayerMatch(row, null, [], [], false), true, a.Stats)]));
        List<string> mismatches = [];
        int compared = 0;
        foreach (FactRow facetRow in facet.Rows)
        {
            long slot = facetRow.Dimensions["player_slot"].AsInteger()!.Value;
            Dictionary<string, double> cells = facetRow.Values.ToDictionary(c => c.Key, c => c.Value.AsNumber() ?? 0, StringComparer.Ordinal);
            if (cells[ColumnRules.Rounds] != matchRounds)
            {
                mismatches.Add($"slot {slot}: rounds {cells[ColumnRules.Rounds]}, the match has {matchRounds}");
            }

            double kastRounds = cells["KAST%"] * cells[ColumnRules.Rounds] / 100;
            if (Math.Abs(kastRounds - Math.Round(kastRounds)) > 1e-6)
            {
                mismatches.Add($"slot {slot}: KAST% {cells["KAST%"]} is no whole number of rounds of {cells[ColumnRules.Rounds]}");
            }

            if (kastTotals.TryGetValue(slot, out MetricRow? totals))
            {
                foreach (string bucket in Buckets.Where(b => cells[b] != Cell(totals.Values[b])))
                {
                    mismatches.Add($"slot {slot}: {bucket} {cells[bucket]}, kast_game_totals {totals.Values[bucket]}");
                }
            }

            if (!game.TryGetValue(slot, out MetricRow? stats))
            {
                mismatches.Add($"slot {slot}: no Stats tab row");
                continue;
            }

            foreach ((string key, ColumnRule rule) in ColumnRules.All)
            {
                if (rule is ColumnRule.NotAggregable || !stats.Values.TryGetValue(key, out object? value))
                {
                    continue;
                }

                compared++;
                double expected = Cell(value);
                double? pooled = aggregates.TryGetValue(slot, out PlayerAggregate? aggregate) ? aggregate[key] : rule.Pool([cells]);
                bool agrees = rule is ColumnRule.Sum or ColumnRule.DifferenceOfSums
                    ? pooled == expected
                    : double.IsFinite(expected)
                        ? pooled is { } p && Math.Abs(p - expected) <= (0.5 * Math.Pow(10, -decimals.GetValueOrDefault(key, 1))) + 1e-9
                        : pooled is null;
                if (!agrees)
                {
                    mismatches.Add($"slot {slot}: {key} facet {pooled?.ToString("R", CultureInfo.InvariantCulture) ?? "null"}, Stats tab {value}");
                }
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(library.Status(path, PlayerMatchFacet.Key)).IsEqualTo(FactStatus.Current);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId))?.State).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(facet.Values.Order(StringComparer.Ordinal)).IsEquivalentTo(ColumnRules.FacetColumns.Order(StringComparer.Ordinal));
            await Assert.That(facet.Values.Where(ColumnCatalogue.RequiresVisibility)).IsEmpty();
            await Assert.That(game.Keys.Where(s => facet.Rows.All(r => r.Dimensions["player_slot"].AsInteger() != s))).IsEmpty()
                .Because("every Stats tab row has a facet row");
            await Assert.That(compared).IsGreaterThan(facet.Rows.Count * 40);
            await Assert.That(aggregates.Count).IsGreaterThanOrEqualTo(8).Because("most accounts sit on one slot");
            await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches));
            await Assert.That(row.FacetPlayers).IsNotNull();
            await Assert.That((row.CtPlayers ?? []).Concat(row.TPlayers ?? []).Select(p => p.SteamId64).Except(row.FacetPlayers!.Select(p => p.SteamId64)))
                .IsEmpty().Because("every side player is in the facet");
            await Assert.That(row.FacetPlayers!.SelectMany(p => p.Names).Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^Player \d+$"))).IsEmpty();
        }
    }
}
