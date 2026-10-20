#region

using System.Globalization;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>player_round</c> facet is a published format (<c>docs/player-round-format.md</c>): the shipped ruleset
///     still declares the committed sample's columns, the sidecar round-trips, an account's rounds sum by side, and
///     the ruleset is stamped on its own, so adding or editing it never moves <c>player_match</c>.
/// </summary>
[NotInParallel]
public class PlayerRoundFacetTests
{
    /// <summary>Set to 1 to rewrite the sample from a real demo through <see cref="PlayerMatchParityRealDemoTests" />.</summary>
    internal const string SampleUpdateVar = "PLAYER_ROUND_SAMPLE_UPDATE";

    private const string A = "76561190000000001", B = "76561190000000002";

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static readonly string[] Dimensions = ["match_id", "map", "round_number", "player_slot", "player_name", "steam_id", "team"];

    internal static string SamplePath(string repo) => Path.Combine(repo, "tests", "fixtures", "player-round", "player_round.sample.facts.json");

    // The first rounds of a real demo, the accounts and names replaced by slot.
    internal static void WriteSample(string path, FactTable table)
    {
        List<FactRow> rows = [];
        foreach (FactRow real in table.Rows.Where(r => r.Dimensions["round_number"].AsInteger() <= 2))
        {
            long slot = real.Dimensions["player_slot"].AsInteger()!.Value;
            Dictionary<string, FactValue> dimensions = new(real.Dimensions, StringComparer.Ordinal)
            {
                ["player_name"] = FactValue.Of(string.Create(CultureInfo.InvariantCulture, $"player_{slot}")),
                ["steam_id"] = FactValue.Of(string.Create(CultureInfo.InvariantCulture, $"765611900000000{slot:00}"))
            };
            rows.Add(new FactRow(dimensions, real.Values));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, PlayerMatchFacetTests.Indent(FactsCodec.Encode(table with { Fingerprint = "sample", Rows = rows })));
    }

    private static FactRow Round(long round, long slot, string? steamId, long side, long kills = 0, long deaths = 0, long damage = 0, bool kast = false) => new(
        new Dictionary<string, FactValue>
        {
            ["round_number"] = FactValue.Of(round),
            ["player_slot"] = FactValue.Of(slot),
            ["steam_id"] = FactValue.Of(steamId)
        },
        new Dictionary<string, FactValue>
        {
            ["side"] = FactValue.Of(side),
            ["kills"] = FactValue.Of(kills),
            ["deaths"] = FactValue.Of(deaths),
            ["assists"] = FactValue.Of(0L),
            ["damage"] = FactValue.Of(damage),
            ["survived"] = FactValue.Of(deaths == 0 ? 1L : 0L),
            ["kast"] = FactValue.Of(kast ? 1L : 0L)
        });

    [Test]
    public async Task V1Columns_MatchTheCheckedInSample()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = SamplePath(repo);
        if (!File.Exists(path))
        {
            throw new SkipTestException($"missing the player_round sample; regenerate with {SampleUpdateVar}=1 through PlayerMatchParityRealDemoTests");
        }

        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, null));
        string committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        FactTable? table = FactsCodec.Decode(PlayerMatchFacetTests.Gzip(committed));
        IReadOnlyList<string> declared =
        [
            .. rules.EnabledDoc(PlayerRoundFacet.RulesetId)!.Show!.Tables.Single(t => t.Name == PlayerRoundFacet.Table).Columns.Select(c => c.Label ?? c.Stat)
        ];

        await Assert.That(table).IsNotNull().Because("the sample is a format-1 facts sidecar");
        using (Assert.Multiple())
        {
            await Assert.That(table!.Key).IsEqualTo(PlayerRoundFacet.Key);
            await Assert.That(table.Schema).IsEqualTo(StampedFacts.Schema);
            await Assert.That(table.Grain).IsEqualTo("player_round");
            await Assert.That(string.Join(",", table.Values)).IsEqualTo(string.Join(",", declared))
                .Because("the shipped ruleset declares the committed columns, in order; a change is a new facet version");
            await Assert.That(string.Join(",", table.Dimensions)).IsEqualTo(string.Join(",", Dimensions));
            await Assert.That(table.Rows.Count).IsGreaterThanOrEqualTo(20).Because("two rounds of ten players");
            await Assert.That(table.Rows.All(r => declared.All(r.Values.ContainsKey))).IsTrue();
            await Assert.That(table.Rows.Count(r => r.Values["side"].AsInteger() == PlayerRoundFacet.Ct))
                .IsEqualTo(table.Rows.Count(r => r.Values["side"].AsInteger() == PlayerRoundFacet.T));
            await Assert.That(PlayerMatchFacetTests.Indent(FactsCodec.Encode(table))).IsEqualTo(committed).Because("round trip");
        }
    }

    [Test]
    public async Task Sides_SumEachAccountsRoundsBySide()
    {
        FactRow[] rows =
        [
            Round(1, 1, A, 3, kills: 2, damage: 180, kast: true),
            Round(2, 1, A, 3, deaths: 1, damage: 40),
            Round(13, 1, A, 2, kills: 1, damage: 100, kast: true),
            // A reconnect: the old slot's ghost sits on no side, the new slot carries on.
            Round(14, 1, A, 0),
            Round(14, 6, A, 2, deaths: 1, damage: 12, kast: true),
            Round(1, 2, B, 2, deaths: 1),
            Round(1, 3, "0", 3, kills: 4),
            Round(1, 4, null, 2, kills: 4)
        ];

        Dictionary<ulong, Dictionary<string, double>> sides = PlayerRoundFacet.Sides(rows);
        Dictionary<string, double> a = sides[ulong.Parse(A, CultureInfo.InvariantCulture)];

        using (Assert.Multiple())
        {
            await Assert.That(sides.Keys.Count).IsEqualTo(2).Because("a bot and a slot with no account count for no one");
            await Assert.That(a["CT:rounds"]).IsEqualTo(2);
            await Assert.That(a["T:rounds"]).IsEqualTo(2).Because("a round on no side counts nowhere");
            await Assert.That(a["CT:kills"]).IsEqualTo(2);
            await Assert.That(a["CT:deaths"]).IsEqualTo(1);
            await Assert.That(a["CT:damage"]).IsEqualTo(220);
            await Assert.That(a["CT:kast"]).IsEqualTo(1);
            await Assert.That(a["T:kills"]).IsEqualTo(1);
            await Assert.That(a["T:deaths"]).IsEqualTo(1);
            await Assert.That(a["T:damage"]).IsEqualTo(112);
            await Assert.That(a["T:kast"]).IsEqualTo(2);
            await Assert.That(a["T:survived"]).IsEqualTo(1);
            await Assert.That(sides[ulong.Parse(B, CultureInfo.InvariantCulture)].ContainsKey("CT:rounds")).IsFalse();
        }
    }

    [Test]
    public async Task Sides_CountARoundOnce_WhenTwoSlotsPlayedIt()
    {
        FactRow[] rows = [Round(5, 1, A, 3, kills: 1), Round(5, 7, A, 3, kills: 1, kast: true)];

        Dictionary<string, double> a = PlayerRoundFacet.Sides(rows)[ulong.Parse(A, CultureInfo.InvariantCulture)];

        using (Assert.Multiple())
        {
            await Assert.That(a["CT:rounds"]).IsEqualTo(1);
            await Assert.That(a["CT:kills"]).IsEqualTo(2);
            await Assert.That(a["CT:kast"]).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AWrite_StampsUnderItsOwnStamp()
    {
        MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules();
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = new(null);
        const string path = "/demos/a.dem";
        cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
        FactsEvaluator evaluator = new(cache, stamped);
        AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
        bool wanted = evaluator.Records(path, PlayerRoundFacet.RulesetId);

        evaluator.Evaluate(path, MergedRulesBuildTests.TwoFrameDemo());
        DemoCacheIndexEntry row = cache.TryGetIndex(path)!;

        using (Assert.Multiple())
        {
            await Assert.That(wanted).IsTrue().Because("a parsed demo with no facet is the backlog");
            await Assert.That(facts.Declared).Contains(PlayerRoundFacet.Key);
            await Assert.That(facts.Status(path, PlayerRoundFacet.Key)).IsEqualTo(FactStatus.Current);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerRoundFacet.RulesetId))?.State).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerRoundFacet.RulesetId))?.Fingerprint)
                .IsNotEqualTo(row.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId))?.Fingerprint);
            await Assert.That(evaluator.Records(path, PlayerRoundFacet.RulesetId)).IsFalse();
            await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64)
                .Because("the facet is stamped on its own, so the highlights stamp never moves");
        }
    }

    [Test]
    public async Task EditingIt_MovesOnlyItsOwnFingerprint()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-pr-edit-");
        try
        {
            MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules(user.FullName);
            StampedFacts stamped = new(rules);
            string before = stamped.Fingerprint(PlayerRoundFacet.RulesetId, 64)!;
            string playerMatch = stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)!;
            string playerEvent = stamped.Fingerprint(PlayerEventFacet.RulesetId, 64)!;

            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "player_round.rules.yaml"));
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "player_round.rules.yaml"),
                yaml.Replace("capture: player.team", "capture: player.team\n    keep: last", StringComparison.Ordinal));
            rules.Invalidate();

            using (Assert.Multiple())
            {
                await Assert.That(before).IsNotNull();
                await Assert.That(stamped.Fingerprint(PlayerRoundFacet.RulesetId, 64)).IsNotNull();
                await Assert.That(stamped.Fingerprint(PlayerRoundFacet.RulesetId, 64)).IsNotEqualTo(before);
                await Assert.That(stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)).IsEqualTo(playerMatch)
                    .Because("a later table re-walks only its own backlog");
                await Assert.That(stamped.Fingerprint(PlayerEventFacet.RulesetId, 64)).IsEqualTo(playerEvent);
                await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64);
            }
        }
        finally
        {
            user.Delete(true);
        }
    }
}
