#region

using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>player_event</c> facet is a published format (<c>docs/player-event-format.md</c>): the shipped
///     ruleset still declares the committed samples' columns, the sidecars round-trip, a kill joins to its death
///     to name both accounts, and the ruleset is stamped on its own, so editing it never moves <c>player_match</c>.
/// </summary>
[NotInParallel]
public class PlayerEventFacetTests
{
    /// <summary>Set to 1 to rewrite the samples from a real demo through <see cref="PlayerEventRealDemoTests" />.</summary>
    internal const string SampleUpdateVar = "PLAYER_EVENT_SAMPLE_UPDATE";

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static readonly string[] Dimensions = ["match_id", "map", "round_number", "tick", "player_slot", "player_name", "steam_id", "team"];

    internal static string SamplePath(string repo, string table) => Path.Combine(repo, "tests", "fixtures", "player-event", table + ".sample.facts.json");

    private static FactRow Kill(long tick, long killer, string? killerId, long victim, string weapon = "ak47", bool headshot = false) => new(
        new Dictionary<string, FactValue>
        {
            ["round_number"] = FactValue.Of(3L),
            ["tick"] = FactValue.Of(tick),
            ["player_slot"] = FactValue.Of(killer),
            ["steam_id"] = FactValue.Of(killerId)
        },
        new Dictionary<string, FactValue>
        {
            ["victim_slot"] = FactValue.Of(victim),
            ["weapon"] = FactValue.Of(weapon),
            ["headshot"] = FactValue.Of(headshot ? 1L : 0L)
        });

    private static FactRow Death(long tick, long victim, string? victimId, long killer) => new(
        new Dictionary<string, FactValue>
        {
            ["round_number"] = FactValue.Of(3L),
            ["tick"] = FactValue.Of(tick),
            ["player_slot"] = FactValue.Of(victim),
            ["steam_id"] = FactValue.Of(victimId)
        },
        new Dictionary<string, FactValue> { ["attacker_slot"] = FactValue.Of(killer) });

    [Test]
    public async Task V1Tables_MatchTheCheckedInSamples()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, null));
        Dictionary<string, FactTable> tables = [];
        foreach (FactKey key in (FactKey[]) [PlayerEventFacet.KillsKey, PlayerEventFacet.DeathsKey])
        {
            string path = SamplePath(repo, key.Output);
            if (!File.Exists(path))
            {
                throw new SkipTestException($"missing the {key.Output} sample; regenerate with {SampleUpdateVar}=1 through PlayerEventRealDemoTests");
            }

            string committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            FactTable? table = FactsCodec.Decode(PlayerMatchFacetTests.Gzip(committed));
            IReadOnlyList<string> declared =
            [
                .. rules.EnabledDoc(PlayerEventFacet.RulesetId)!.Show!.Tables.Single(t => t.Name == key.Output).Columns
                    .Select(c => c.Label ?? c.Stat)
            ];

            await Assert.That(table).IsNotNull().Because("the sample is a format-1 facts sidecar");
            using (Assert.Multiple())
            {
                await Assert.That(table!.Key).IsEqualTo(key);
                await Assert.That(table.Schema).IsEqualTo(StampedFacts.Schema);
                await Assert.That(table.Grain).IsEqualTo("player_event");
                await Assert.That(string.Join(",", table.Values)).IsEqualTo(string.Join(",", declared))
                    .Because("the shipped ruleset declares the committed columns, in order; a change is a new facet version");
                await Assert.That(string.Join(",", table.Dimensions)).IsEqualTo(string.Join(",", Dimensions));
                await Assert.That(table.Rows.Count).IsGreaterThan(0);
                await Assert.That(table.Rows.All(r => declared.All(r.Values.ContainsKey))).IsTrue();
                await Assert.That(table.Rows.All(r => r.Dimensions["tick"].Kind == FactValueKind.Whole)).IsTrue();
                await Assert.That(PlayerMatchFacetTests.Indent(FactsCodec.Encode(table))).IsEqualTo(committed).Because("round trip");
            }

            tables[key.Output] = table;
        }

        List<PlayerKill> kills = PlayerEventFacet.Kills(tables[PlayerEventFacet.KillsTable].Rows, tables[PlayerEventFacet.DeathsTable].Rows);
        using (Assert.Multiple())
        {
            await Assert.That(kills.Count).IsEqualTo(tables[PlayerEventFacet.KillsTable].Rows.Count);
            await Assert.That(kills.All(k => k.Killer is not null && k.Victim is not null && k.Killer != k.Victim)).IsTrue()
                .Because("every sample kill has its death row, both accounts named");
            await Assert.That(kills.All(k => k.Weapon is not null)).IsTrue();
        }
    }

    [Test]
    public async Task Kills_PairEachKillWithItsDeath()
    {
        const string a = "76561190000000001", b = "76561190000000002", c = "76561190000000003", d = "76561190000000004";
        FactRow[] kills =
        [
            Kill(100, 1, a, 2, headshot: true),
            Kill(100, 1, a, 3, "awp"),
            Kill(200, 2, b, 4),
            Kill(300, 3, c, 5),
            Kill(400, 4, d, 1)
        ];
        FactRow[] deaths =
        [
            Death(100, 3, c, 1),
            Death(100, 2, b, 1),
            Death(200, 4, d, 2),
            Death(300, 5, "0", 3),
            Death(350, 2, b, 0)
        ];

        List<PlayerKill> joined = PlayerEventFacet.Kills(kills, deaths);

        using (Assert.Multiple())
        {
            await Assert.That(joined.Select(k => (k.Killer, k.Victim))).IsEquivalentTo(new (string?, string?)[]
            {
                (a, b), (a, c), (b, d), (c, null), (d, null)
            }, CollectionOrdering.Matching).Because("a collateral pairs by victim slot, a bot victim has no account, and a kill with no death row keeps no victim");
            await Assert.That(joined[0].Headshot).IsTrue();
            await Assert.That(joined[1].Headshot).IsFalse();
            await Assert.That(joined[1].Weapon).IsEqualTo("awp");
            await Assert.That(joined[0].Round).IsEqualTo(3);
            await Assert.That(joined[0].Tick).IsEqualTo(100L);
        }
    }

    [Test]
    public async Task AWrite_StampsBothTablesUnderItsOwnStamp()
    {
        MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules();
        StampedFacts stamped = new(rules);
        DemoCacheStore cache = new(null);
        const string path = "/demos/a.dem";
        cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
        FactsEvaluator evaluator = new(cache, stamped);
        AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
        bool wanted = evaluator.Records(path, PlayerEventFacet.RulesetId);

        evaluator.Evaluate(path, MergedRulesBuildTests.TwoFrameDemo());
        DemoCacheIndexEntry row = cache.TryGetIndex(path)!;

        using (Assert.Multiple())
        {
            await Assert.That(wanted).IsTrue().Because("a parsed demo with no facet is the backlog");
            await Assert.That(facts.Declared).Contains(PlayerEventFacet.KillsKey);
            await Assert.That(facts.Declared).Contains(PlayerEventFacet.DeathsKey);
            await Assert.That(facts.Status(path, PlayerEventFacet.KillsKey)).IsEqualTo(FactStatus.Current);
            await Assert.That(facts.Status(path, PlayerEventFacet.DeathsKey)).IsEqualTo(FactStatus.Current);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerEventFacet.RulesetId))?.State).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(row.Stamp(StampedFacts.StampId(PlayerEventFacet.RulesetId))?.Fingerprint)
                .IsNotEqualTo(row.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId))?.Fingerprint);
            await Assert.That(evaluator.Records(path, PlayerEventFacet.RulesetId)).IsFalse();
            await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64)
                .Because("the facet is stamped on its own, so the highlights stamp never moves");
        }
    }

    [Test]
    public async Task EditingIt_MovesOnlyItsOwnFingerprint()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-pe-edit-");
        try
        {
            MergedRulesBuild rules = PlayerMatchParityRealDemoTests.AppRules(user.FullName);
            StampedFacts stamped = new(rules);
            string before = stamped.Fingerprint(PlayerEventFacet.RulesetId, 64)!;
            string playerMatch = stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)!;
            string roundFacts = stamped.Fingerprint(RoundFactsFingerprint.RulesetId, 64)!;

            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "player_event.rules.yaml"));
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "player_event.rules.yaml"),
                yaml.Replace("capture: event.Weapon", "capture: event.Weapon\n    keep: last", StringComparison.Ordinal));
            rules.Invalidate();

            using (Assert.Multiple())
            {
                await Assert.That(before).IsNotNull();
                await Assert.That(stamped.Fingerprint(PlayerEventFacet.RulesetId, 64)).IsNotNull();
                await Assert.That(stamped.Fingerprint(PlayerEventFacet.RulesetId, 64)).IsNotEqualTo(before);
                await Assert.That(stamped.Fingerprint(PlayerMatchFacet.RulesetId, 64)).IsEqualTo(playerMatch)
                    .Because("a later table re-walks only its own backlog");
                await Assert.That(stamped.Fingerprint(RoundFactsFingerprint.RulesetId, 64)).IsEqualTo(roundFacts);
                await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64);
            }
        }
        finally
        {
            user.Delete(true);
        }
    }
}
