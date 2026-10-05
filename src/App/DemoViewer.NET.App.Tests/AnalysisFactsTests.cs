#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library's facts store without a demo: the sidecar format keeps every cell's kind, the read cut names
///     only the tables of rulesets whose facts need writing, and a ruleset whose owner is off reads as absent.
/// </summary>
[NotInParallel]
public class AnalysisFactsTests
{
    internal const string FactsRuleset = "fixture_facts";
    internal const string FactsTable = "fixture_kills";

    internal const string FactsYaml = """
                                      ruleset: fixture_facts
                                      for: each_player
                                      stats:
                                        kills:
                                          count: kill
                                          per: match
                                          label: FixtureKills
                                      show:
                                        scoreboard:
                                          - { stat: kills, label: FixtureKills, group: game }
                                        tables:
                                          fixture_kills:
                                            per: player_match
                                            columns:
                                              - { stat: kills, label: fixture_kills_count }
                                      """;

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();
    private static readonly long?[] OneTwo = [1, 2];
    private static readonly int[] OneTwoCells = [1, 2];

    internal static RuleConfigLoadResult WithFixture(string directory) => YamlConfigLoader.LoadWithOverlay(Shipped, directory);

    internal static string WriteFixture(params (string Id, string Yaml)[] rulesets)
    {
        string directory = Directory.CreateTempSubdirectory("dv-facts-fixture-").FullName;
        foreach ((string id, string yaml) in rulesets)
        {
            File.WriteAllText(Path.Combine(directory, id + ".rules.yaml"), yaml);
        }

        return directory;
    }

    [Test]
    public async Task TheSidecar_KeepsEveryCellsKind()
    {
        FactKey key = new("r", "t");
        FactTable table = new(key, "fp", 1, "player_match", ["slot"], ["a", "b", "c", "d", "e", "f"],
            new Dictionary<string, string> { ["d"] = "frame" },
            [
                new FactRow(new Dictionary<string, FactValue> { ["slot"] = FactValue.Of(3L) },
                    new Dictionary<string, FactValue>
                    {
                        ["a"] = FactValue.Of(2L),
                        ["b"] = FactValue.Of(2.0),
                        ["c"] = FactValue.Of(0.25),
                        ["d"] = FactValue.Of(true),
                        ["e"] = FactValue.Of([FactValue.Of(1L), FactValue.Of("x"), FactValue.Null]),
                        ["f"] = FactValue.Null
                    })
            ]);

        FactTable? read = FactsCodec.Decode(FactsCodec.Encode(table));

        await Assert.That(read).IsNotNull();
        FactRow row = read!.Rows.Single();
        using (Assert.Multiple())
        {
            await Assert.That(read.Key).IsEqualTo(key);
            await Assert.That(read.Grain).IsEqualTo("player_match");
            await Assert.That(read.ColumnClocks["d"]).IsEqualTo("frame");
            await Assert.That(row.Dimensions["slot"]).IsEqualTo(FactValue.Of(3L));
            await Assert.That(row.Values["a"].Kind).IsEqualTo(FactValueKind.Whole);
            await Assert.That(row.Values["b"].Kind).IsEqualTo(FactValueKind.Number).Because("2.0 is not the whole number 2");
            await Assert.That(row.Values["c"].AsNumber()).IsEqualTo(0.25);
            await Assert.That(row.Values["d"].AsBoolean()).IsEqualTo(true);
            await Assert.That(row.Values["e"]).IsEqualTo(FactValue.Of([FactValue.Of(1L), FactValue.Of("x"), FactValue.Null]));
            await Assert.That(row.Values["f"]).IsEqualTo(FactValue.Null);
        }
    }

    [Test]
    public async Task EngineCells_NormalizeToTheSdksKinds()
    {
        MetricTable metric = new("t", ["slot"], ["n"],
        [
            new MetricRow(new Dictionary<string, object?> { ["slot"] = 4 }, new Dictionary<string, object?> { ["n"] = 1.5f }),
            new MetricRow(new Dictionary<string, object?> { ["slot"] = 5 }, new Dictionary<string, object?> { ["n"] = OneTwoCells })
        ]);

        FactTable table = FactsCodec.FromMetric(metric, new FactKey("r", "t"), "fp", 1, "player_match");

        using (Assert.Multiple())
        {
            await Assert.That(table.Rows[0].Dimensions["slot"].AsInteger()).IsEqualTo(4L);
            await Assert.That(table.Rows[0].Values["n"].AsNumber()).IsEqualTo(1.5);
            await Assert.That(table.Rows[1].Values["n"].Items.Select(i => i.AsInteger())).IsEquivalentTo(OneTwo);
        }
    }

    [Test]
    public async Task TheReadCut_NamesTheTablesOfStaleRulesetsOnly()
    {
        string fixture = WriteFixture((FactsRuleset, FactsYaml));
        try
        {
            bool on = true;
            MergedRulesBuild rules = new(() => WithFixture(fixture),
                () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(FactsRuleset, "dev.example.x", () => on)]);

            IReadOnlySet<string> all = rules.StampedOutputs();
            IReadOnlySet<string> onlyFacts = rules.StampedOutputs(id => id == FactsRuleset);
            on = false;
            IReadOnlySet<string> off = rules.StampedOutputs();

            using (Assert.Multiple())
            {
                await Assert.That(all).IsEquivalentTo(new[] { RoundFactsFingerprint.RulesetId, FactsTable });
                await Assert.That(onlyFacts).IsEquivalentTo(new[] { FactsTable });
                await Assert.That(off).IsEquivalentTo(new[] { RoundFactsFingerprint.RulesetId }).Because("an owner that is off records nothing");
                await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64)
                    .Because("a stamped ruleset never moves the highlights stamp");
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Test]
    public async Task AnOwnerThatIsOff_HidesItsFacts_AndTheStoredOnesStay()
    {
        string fixture = WriteFixture((FactsRuleset, FactsYaml));
        try
        {
            bool on = true;
            MergedRulesBuild rules = new(() => WithFixture(fixture),
                () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(FactsRuleset, "dev.example.x", () => on)]);
            StampedFacts stamped = new(rules);
            DemoCacheStore cache = new(null);
            const string path = "/demos/a.dem";
            cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
            string fingerprint = stamped.Fingerprint(FactsRuleset, 64)!;
            FactKey key = new(FactsRuleset, FactsTable);
            cache.WriteSiblingBytes(path, StampedFacts.Suffix(key), FactsCodec.Encode(
                new FactTable(key, fingerprint, StampedFacts.Schema, "player_match", ["slot"], ["k"], new Dictionary<string, string>(), [])));
            cache.UpdateExisting(path, r => r.SetStamp(new PackStamp(StampedFacts.StampId(FactsRuleset), StampedFacts.Schema, fingerprint)));
            AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
            FactsEvaluator evaluator = new(cache, stamped);

            FactStatus whileOn = facts.Status(path, key);
            FactTable? readOn = facts.TryGet(path, key);
            bool wantedOn = evaluator.Wants(path);
            on = false;

            using (Assert.Multiple())
            {
                await Assert.That(whileOn).IsEqualTo(FactStatus.Current);
                await Assert.That(readOn).IsNotNull();
                await Assert.That(wantedOn).IsFalse().Because("current facts need no read");
                await Assert.That(facts.Status(path, new FactKey(FactsRuleset, FactKey.ScoreboardOutput))).IsEqualTo(FactStatus.Absent)
                    .Because("off looks off, the scoreboard included");
                await Assert.That(facts.Declared).IsEmpty();
                await Assert.That(facts.Status(path, key)).IsEqualTo(FactStatus.Absent);
                await Assert.That(facts.TryGet(path, key)).IsNull();
                await Assert.That(cache.TryReadSiblingBytes(path, StampedFacts.Suffix(key))).IsNotNull().Because("the facts wait for the owner");
            }

            on = true;
            await Assert.That(facts.Status(path, new FactKey(FactsRuleset, FactKey.ScoreboardOutput))).IsEqualTo(FactStatus.NeedsFullAnalysis);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }
}
