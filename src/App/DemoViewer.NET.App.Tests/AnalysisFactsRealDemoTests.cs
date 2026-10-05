#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A stamped ruleset's table off one forward read of a real demo, beside Round Facts and a ruleset that does
///     not compose: the read records only what is stale, the facts pass stores the table, the library reads it
///     back, and the broken ruleset costs nothing but its own facts. Read in place from the <c>DEMO_PATH</c> folder.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class AnalysisFactsRealDemoTests
{
    private const string BrokenRuleset = "fixture_broken";

    private const string BrokenYaml = """
                                      ruleset: fixture_broken
                                      for: each_player
                                      stats:
                                        nonsense:
                                          count: no_such_view_anywhere
                                          per: match
                                          label: Nonsense
                                      show:
                                        tables:
                                          fixture_broken_table:
                                            per: player_match
                                            columns:
                                              - { stat: nonsense, label: nonsense }
                                      """;

    [Test]
    public async Task OneRead_StoresTheStaleTables_AndTheLibraryReadsThemBack()
    {
        string path = BackgroundPlanRealDemoTests.SmallestDemo(0);
        string fixture = AnalysisFactsTests.WriteFixture((AnalysisFactsTests.FactsRuleset, AnalysisFactsTests.FactsYaml),
            (BrokenRuleset, BrokenYaml));
        try
        {
            MergedRulesBuild rules = new(() => AnalysisFactsTests.WithFixture(fixture),
            () =>
            [
                StampedRuleset.Core(RoundFactsFingerprint.RulesetId),
                new StampedRuleset(AnalysisFactsTests.FactsRuleset, "dev.example.x", static () => true),
                new StampedRuleset(BrokenRuleset, "dev.example.x", static () => true)
            ]);
            DemoCacheStore cache = new(null);
            cache.Upsert(new DemoCacheRecord
            {
                Path = path,
                Size = new FileInfo(path).Length,
                Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
            });
            RulesRoundFactsRulesetIdentity identity = new(rules);
            RoundFactsEvaluator roundFacts = new(cache, new EngineRoundFactsRowSource(identity), identity);
            StampedFacts stamped = new(rules);
            FactsEvaluator facts = new(cache, stamped);
            AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache, roundFacts));
            FactKey key = new(AnalysisFactsTests.FactsRuleset, AnalysisFactsTests.FactsTable);

            IReadOnlySet<string> Cut() => rules.StampedOutputs(id => id == RoundFactsFingerprint.RulesetId
                ? roundFacts.Records(path)
                : facts.Records(path, id));

            IReadOnlySet<string> first = Cut();
            bool wanted = facts.Wants(path);
            using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
            {
                ForwardDemoResult pass = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, outputs: first);
                roundFacts.EvaluateForward(path, pass);
                facts.EvaluateForward(path, pass);
            }

            FactTable? table = library.TryGet(path, key);
            using (Assert.Multiple())
            {
                await Assert.That(wanted).IsTrue();
                await Assert.That(first).IsEquivalentTo(new[] { RoundFactsFingerprint.RulesetId, AnalysisFactsTests.FactsTable })
                    .Because("the broken ruleset has no fingerprint, so nothing reads for it");
                await Assert.That(library.Declared).Contains(key);
                await Assert.That(library.Declared).Contains(new FactKey(AnalysisFactsTests.FactsRuleset, FactKey.ScoreboardOutput));
                await Assert.That(library.Status(path, key)).IsEqualTo(FactStatus.Current);
                await Assert.That(library.Status(path, new FactKey(AnalysisFactsTests.FactsRuleset, FactKey.ScoreboardOutput)))
                    .IsEqualTo(FactStatus.NeedsFullAnalysis);
                await Assert.That(library.Status(path, new FactKey(BrokenRuleset, "fixture_broken_table"))).IsEqualTo(FactStatus.Absent);
                await Assert.That(table).IsNotNull();
                await Assert.That(table!.Grain).IsEqualTo("player_match");
                await Assert.That(table.Rows.Count).IsGreaterThanOrEqualTo(10);
                await Assert.That(table.Rows.Sum(r => r.Values["fixture_kills_count"].AsNumber() ?? 0)).IsGreaterThan(0);
                await Assert.That(library.RoundFacts.TryGet(path)?.Rounds.Count ?? 0).IsGreaterThan(0)
                    .Because("the broken ruleset rode the same build and Round Facts still wrote");
                await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64);
                await Assert.That(facts.Wants(path)).IsFalse();
                await Assert.That(Cut()).IsEmpty().Because("nothing is stale, so the next read records no table");
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }
}
