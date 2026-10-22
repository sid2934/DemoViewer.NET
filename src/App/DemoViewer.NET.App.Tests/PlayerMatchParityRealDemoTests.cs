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
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using TUnit.Core.Exceptions;

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

    /// <summary>The app's stamped set over the shipped rules: round facts, the player facets and the library tables.</summary>
    internal static MergedRulesBuild AppRules(string? user = null) => new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user),
        () =>
        [
            StampedRuleset.Core(RoundFactsFingerprint.RulesetId),
            StampedRuleset.Core(PlayerMatchFacet.RulesetId),
            StampedRuleset.Core(PlayerEventFacet.RulesetId),
            StampedRuleset.Core(PlayerRoundFacet.RulesetId),
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

    // One CT and one T row of a real facet, the accounts and names replaced.
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

    // A library row for the demo with its roster, as the library's visit leaves it before the facts pass.
    private static DemoCacheStore Library(string path, ParsedDemo parsed)
    {
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
        return cache;
    }

    // A library visit of the demo through the facts pass, over a cache that holds only its parse and roster.
    private static (DemoCacheStore Cache, AnalysisFacts Library, ForwardDemoResult Pass) Visit(string path, ParsedDemo parsed, MergedRulesBuild rules)
    {
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = Library(path, parsed);
        FactsEvaluator facts = new(cache, stamped);
        AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache));
        IReadOnlySet<string> cut = rules.StampedOutputs(id => facts.Records(path, id));
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult pass = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, outputs: cut);
        facts.EvaluateForward(path, pass);
        return (cache, library, pass);
    }

    [Test]
    public async Task TheFacet_IsTheStatsTabsGameTable_RowForRow()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        MergedRulesBuild rules = AppRules();
        (DemoCacheStore cache, AnalysisFacts library, _) = Visit(path, parsed, rules);
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
                // The background visit has no geometry: its sight columns are no data, and the open's test compares them.
                if (rule is ColumnRule.NotAggregable || ColumnCatalogue.RequiresVisibility(key) || !stats.Values.TryGetValue(key, out object? value))
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
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId))?.HasVisibility).IsFalse()
                .Because("a forward visit has no line-of-sight geometry");
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

    [Test]
    public async Task TheRoundFacet_IsTheRetainedRoundTable_AndAddsUpToTheMatch()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        MergedRulesBuild rules = AppRules();
        (DemoCacheStore cache, AnalysisFacts library, ForwardDemoResult pass) = Visit(path, parsed, rules);
        FactTable rounds = library.TryGet(path, PlayerRoundFacet.Key) ?? throw new InvalidOperationException("the visit wrote no player_round");
        FactTable facet = library.TryGet(path, PlayerMatchFacet.Key) ?? throw new InvalidOperationException("the visit wrote no player_match");
        DemoCacheIndexEntry row = cache.TryGetIndex(path)!;
        if (Environment.GetEnvironmentVariable(PlayerRoundFacetTests.SampleUpdateVar) == "1" && DemoTestHelper.FindRepoRoot() is { } repo)
        {
            PlayerRoundFacetTests.WriteSample(PlayerRoundFacetTests.SamplePath(repo), rounds);
        }

        AnalysisOptions options = new();
        AnalysisRun retained = DemoAnalysis.Evaluate(parsed, DemoAnalysis.Build(parsed, rules.CoreDocs, options), options);
        Dictionary<(long Round, long Slot), MetricRow> retainedRounds = new PlayerRoundStatsProjector().Project(retained.Snapshots!, parsed).Single().Rows
            .ToDictionary(r => (Convert.ToInt64(r.Dimensions["round_number"], CultureInfo.InvariantCulture),
                Convert.ToInt64(r.Dimensions["player_slot"], CultureInfo.InvariantCulture)));

        // The side of each slot at each freeze end, by the round_facts rows the same visit recorded.
        Dictionary<(long Round, long Slot), long> sideOf = [];
        foreach (MetricRow side in pass.Run!.ProjectConfiguredOutputs().Single(t => t.Name == RoundFactsFingerprint.RulesetId).Rows)
        {
            long round = Convert.ToInt64(side.Dimensions["round_number"], CultureInfo.InvariantCulture);
            foreach (int slot in RoundFactsValues.ToIntList(side.Dimensions["slots"]) ?? [])
            {
                sideOf[(round, slot)] = Convert.ToInt64(side.Dimensions["side"], CultureInfo.InvariantCulture);
            }
        }

        List<string> mismatches = [];
        foreach (FactRow line in rounds.Rows)
        {
            (long Round, long Slot) key = (line.Dimensions["round_number"].AsInteger()!.Value, line.Dimensions["player_slot"].AsInteger()!.Value);
            long side = line.Values["side"].AsInteger() ?? 0;
            // round_facts lists the slots with a player at freeze end; a slot it leaves out (a player gone for the round) still has its team.
            if (sideOf.TryGetValue(key, out long expected) && side != expected)
            {
                mismatches.Add($"round {key.Round} slot {key.Slot}: side {side}, round_facts {expected}");
            }

            if (retainedRounds.TryGetValue(key, out MetricRow? stats)
                && Convert.ToBoolean(stats.Values["HasKAST"], CultureInfo.InvariantCulture) != (line.Values["kast"].AsBoolean() == true))
            {
                mismatches.Add($"round {key.Round} slot {key.Slot}: kast {line.Values["kast"].AsBoolean()}, HasKAST {stats.Values["HasKAST"]}");
            }
        }

        // Each account's sides add up to its match: every count, the rounds it played a side and the rounds it won and lost there.
        (string Round, string Match)[] totals = [("kills", "TotalK"), ("deaths", "TotalD"), ("assists", "TotalA"), ("damage", "EnemyDmg"), ("survived", "Survived")];
        Dictionary<ulong, Dictionary<string, double>> sides = PlayerRoundFacet.Sides(rounds.Rows);
        long lastRound = rounds.Rows.Max(r => r.Dimensions["round_number"].AsInteger() ?? 0);
        int reconciled = 0, whole = 0;
        foreach ((ulong id, Dictionary<string, double> split) in sides)
        {
            if (PlayerAggregate.Merge(facet.Rows, id) is not { } match)
            {
                continue;
            }

            reconciled++;
            double Both(string count) => split.GetValueOrDefault(PlayerRoundFacet.Column(PlayerRoundFacet.Ct, count))
                                         + split.GetValueOrDefault(PlayerRoundFacet.Column(PlayerRoundFacet.T, count));
            foreach ((string round, string column) in totals.Where(t => Both(t.Round) != match.GetValueOrDefault(t.Match)))
            {
                mismatches.Add($"{id}: {round} {Both(round)} by side, player_match {column} {match.GetValueOrDefault(column)}");
            }

            // KAST% is a match-end compute, and the last round's survival lands after it: the sides may hold that round's KAST beside it.
            double kastRounds = Math.Round(match.GetValueOrDefault("KAST%") * match.GetValueOrDefault(ColumnRules.Rounds) / 100);
            double lastKast = rounds.Rows.Any(r => r.Dimensions["round_number"].AsInteger() == lastRound
                                                   && r.Dimensions["steam_id"].AsText() == id.ToString(CultureInfo.InvariantCulture)
                                                   && r.Values["kast"].AsBoolean() == true) ? 1 : 0;
            if (Both("kast") != kastRounds && Both("kast") - lastKast != kastRounds)
            {
                mismatches.Add($"{id}: kast {Both("kast")} by side ({lastKast} in the last round), KASTRounds {kastRounds}");
            }

            if (Both("rounds") > match.GetValueOrDefault(ColumnRules.Rounds))
            {
                mismatches.Add($"{id}: {Both("rounds")} rounds by side, the match has {match.GetValueOrDefault(ColumnRules.Rounds)}");
            }

            whole += Both("rounds") == match.GetValueOrDefault(ColumnRules.Rounds) ? 1 : 0;
            foreach ((int side, string won, string lost) in (ReadOnlySpan<(int, string, string)>) [(PlayerRoundFacet.Ct, "CTW", "CTL"), (PlayerRoundFacet.T, "TW", "TL")])
            {
                double played = split.GetValueOrDefault(PlayerRoundFacet.Column(side, "rounds"));
                if (played != match.GetValueOrDefault(won) + match.GetValueOrDefault(lost))
                {
                    mismatches.Add($"{id}: {played} rounds on {won[..^1]}, player_match {won}+{lost} {match.GetValueOrDefault(won) + match.GetValueOrDefault(lost)}");
                }
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(library.Status(path, PlayerRoundFacet.Key)).IsEqualTo(FactStatus.Current);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerRoundFacet.RulesetId))?.State).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId))?.State).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(rounds.Grain).IsEqualTo("player_round");
            await Assert.That(rounds.Rows.Count).IsGreaterThanOrEqualTo(retainedRounds.Count).Because("a row per player per round, as the retained run has");
            await Assert.That(retainedRounds.Keys.Where(k => rounds.Rows.All(r => r.Dimensions["round_number"].AsInteger() != k.Round
                                                                                 || r.Dimensions["player_slot"].AsInteger() != k.Slot))).IsEmpty();
            await Assert.That(reconciled).IsGreaterThanOrEqualTo(10);
            await Assert.That(whole).IsGreaterThanOrEqualTo(8).Because("a player there all match played every round on a side");
            await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(40)));
        }
    }

    [Test]
    public async Task AnOpenWithGeometry_GivesTheFacetTheStatsTabsSightColumns()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        if (CollisionSoup.Find(parsed.MapName) is not { } tris)
        {
            throw new SkipTestException($"no collision bake for {parsed.MapName}");
        }

        MergedRulesBuild rules = AppRules();
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = Library(path, parsed);
        FactsEvaluator facts = new(cache, stamped);
        AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache));
        string stampId = StampedFacts.StampId(PlayerMatchFacet.RulesetId);
        // As the composition root wires it, less round facts, which this test does not store.
        rules.OutputsFor = p => rules.StampedOutputs(id => !StampedFacts.IsHostRead(id) && facts.Records(p, id));
        using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
        {
            facts.EvaluateForward(path, ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, outputs: rules.StaleOutputs(path)));
        }

        PackStamp background = cache.TryGetIndex(path)!.Stamp(stampId)!;

        // The open, as AnalysisViewModel runs it: geometry in the build, the stale tables cut in, the run seeded.
        facts.OpenWithSight(path);
        OpenRunPlan plan = rules.PlanOpen(rules.ReadRules().Rulesets, path, rules.StaleOutputs(path));
        AnalysisOptions options = new() { VisibilityEngine = CollisionSoup.Load(tris) };
        AnalysisRun open = DemoAnalysis.Evaluate(parsed, plan.Cuts(DemoAnalysis.Build(parsed, plan.Docs, options)), options);
        bool seeded = rules.Seed(parsed, path, plan, open);
        bool wanted = facts.Wants(path);
        facts.Evaluate(path, parsed);
        rules.Forget(parsed);

        DemoCacheIndexEntry row = cache.TryGetIndex(path)!;
        PackStamp upgraded = row.Stamp(stampId)!;
        FactTable facet = library.TryGet(path, PlayerMatchFacet.Key) ?? throw new InvalidOperationException("the open wrote no facet");
        Dictionary<long, MetricRow> game = new PlayerGameStatsProjector().Project(open.Snapshots!, parsed).Single().Rows
            .ToDictionary(r => Convert.ToInt64(r.Dimensions["player_slot"], CultureInfo.InvariantCulture));
        Dictionary<string, int> decimals = Decimals(rules.CoreDocs);
        string[] sight = [.. ColumnRules.All.Where(r => r.Value is not ColumnRule.NotAggregable && ColumnCatalogue.RequiresVisibility(r.Key)).Select(r => r.Key)];

        List<string> mismatches = [];
        int compared = 0;
        foreach (IGrouping<string, FactRow> account in facet.Rows
                     .Where(r => r.Dimensions["steam_id"].AsText() is { Length: > 0 } id && id != "0")
                     .GroupBy(r => r.Dimensions["steam_id"].AsText()!)
                     .Where(g => g.Count() == 1))
        {
            long slot = account.Single().Dimensions["player_slot"].AsInteger()!.Value;
            if (PlayerAggregate.Merge(facet.Rows, ulong.Parse(account.Key, CultureInfo.InvariantCulture)) is not { } merged
                || !game.TryGetValue(slot, out MetricRow? stats))
            {
                continue;
            }

            PlayerAggregate pooled = PlayerAggregate.Of([new PlayerDemo(new PlayerMatch(row, null, [], [], false), true, merged) { HasSight = upgraded.HasVisibility }]);
            foreach (string key in sight.Where(stats.Values.ContainsKey))
            {
                compared++;
                double expected = Cell(stats.Values[key]);
                bool agrees = ColumnRules.For(key) is ColumnRule.Sum
                    ? pooled[key] == expected
                    : pooled[key] is { } p && Math.Abs(p - expected) <= (0.5 * Math.Pow(10, -decimals.GetValueOrDefault(key, 1))) + 1e-9;
                if (!agrees)
                {
                    mismatches.Add($"slot {slot}: {key} facet {pooled[key]?.ToString("R", CultureInfo.InvariantCulture) ?? "null"}, Stats tab {stats.Values[key]}");
                }
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(background.HasVisibility).IsFalse();
            await Assert.That(seeded).IsTrue().Because("the open's run recorded the facet, so the visit reads it");
            await Assert.That(wanted).IsTrue();
            await Assert.That(upgraded.HasVisibility).IsTrue();
            await Assert.That(upgraded.Fingerprint).IsEqualTo(background.Fingerprint).Because("sight is not a new ruleset; nothing else re-walks");
            await Assert.That(upgraded.ComputedAtTicks).IsGreaterThan(background.ComputedAtTicks);
            await Assert.That(library.Status(path, PlayerMatchFacet.Key)).IsEqualTo(FactStatus.Current);
            await Assert.That(facts.Wants(path)).IsFalse();
            await Assert.That(compared).IsGreaterThan(8 * 10);
            await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches));
        }
    }
}
