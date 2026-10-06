#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using Microsoft.Extensions.DependencyInjection;

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

    [Test]
    public async Task FactsWrittenThroughOnePath_ReadThroughAnother_AcrossTheRenameAndAReopen()
    {
        string fixture = WriteFixture((FactsRuleset, FactsYaml));
        string root = Path.Combine(Path.GetTempPath(), "dv-facts-copies-" + Guid.NewGuid().ToString("N"));
        try
        {
            MergedRulesBuild rules = new(() => WithFixture(fixture),
                () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(FactsRuleset, "dev.example.x", () => true)]);
            StampedFacts stamped = new(rules);
            const string nfs = "/nfs/a.dem";
            const string smb = "/smb/a.dem";
            string fingerprint = stamped.Fingerprint(FactsRuleset, 64)!;
            FactKey key = new(FactsRuleset, FactsTable);

            DemoCacheStore cache = new(root);
            cache.Upsert(new DemoCacheRecord
            {
                Path = nfs, Size = 1, Sha256 = "sha-a", Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
            });
            cache.WriteSiblingBytes(nfs, StampedFacts.Suffix(key), FactsCodec.Encode(
                new FactTable(key, fingerprint, StampedFacts.Schema, "player_match", ["slot"], ["k"], new Dictionary<string, string>(), [])));
            cache.UpdateExisting(nfs, r => r.SetStamp(new PackStamp(StampedFacts.StampId(FactsRuleset), StampedFacts.Schema, fingerprint)));
            cache.Update(smb, 1, 0, r => r.Sha256 = "sha-a");
            cache.SaveIndex();

            AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
            DemoCacheStore reopened = new(root);
            AnalysisFacts reread = new(reopened, stamped, new RoundFactsSource(reopened));

            using (Assert.Multiple())
            {
                await Assert.That(cache.TryGetIndex(smb)!.Locations).HasCount(2);
                await Assert.That(Directory.GetFiles(Path.Combine(root, "demos")).Select(Path.GetFileName).All(n => n!.StartsWith("sha-a.", StringComparison.Ordinal)))
                    .IsTrue().Because("the saved index named the hash, so the files moved to it");
                await Assert.That(facts.Status(smb, key)).IsEqualTo(FactStatus.Current);
                await Assert.That(facts.TryGet(smb, key)).IsNotNull();
                await Assert.That(new FactsEvaluator(cache, stamped).Wants(smb)).IsFalse()
                    .Because("the other path's facts are this demo's");
                await Assert.That(reread.TryGet(smb, key)).IsNotNull();
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task AnOwnerThatIsOff_TakesItsFactsOffTheLibraryRows_AndTheirQuery()
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
            string stampId = StampedFacts.StampId(FactsRuleset);
            cache.UpdateExisting(path, r => r.SetStamp(new PackStamp(stampId, StampedFacts.Schema, stamped.Fingerprint(FactsRuleset, 64)!)));
            AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
            HostLibrary library = HostLibrary.For(cache, null, () => facts);
            LibraryQuery hasFact = new() { HasFact = stampId };

            bool shownOn = library.Find(path)?.Fact(stampId) is { IsWritten: true };
            int matchedOn = library.Query(hasFact).Total;
            List<LibraryChange> changes = [];
            library.Changed += changes.Add;
            on = false;
            library.RecheckFacts();

            using (Assert.Multiple())
            {
                await Assert.That(shownOn).IsTrue();
                await Assert.That(matchedOn).IsEqualTo(1);
                await Assert.That(changes).IsEquivalentTo(new[] { new LibraryChange(null, LibraryChangeKind.Updated) });
                await Assert.That(library.Find(path)?.Fact(stampId)).IsNull().Because("off looks off on the row");
                await Assert.That(library.Demos.Single().Facts.Select(f => f.Id)).DoesNotContain(stampId);
                await Assert.That(library.Query(hasFact).Total).IsEqualTo(0);
                await Assert.That(cache.TryGetIndex(path)?.Stamp(stampId)).IsNotNull().Because("the stamp waits for the owner");
            }

            on = true;
            await Assert.That(library.Query(hasFact).Total).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Test]
    public async Task AnOwnerOffFromTheStart_NeverShowsItsStampOnARow()
    {
        string fixture = WriteFixture((FactsRuleset, FactsYaml));
        try
        {
            MergedRulesBuild rules = new(() => WithFixture(fixture),
                () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(FactsRuleset, "dev.example.x", static () => false)]);
            StampedFacts stamped = new(rules);
            DemoCacheStore cache = new(null);
            const string path = "/demos/a.dem";
            string stampId = StampedFacts.StampId(FactsRuleset);
            cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
            cache.UpdateExisting(path, r => r.SetStamp(new PackStamp(stampId, StampedFacts.Schema, "fp")));
            cache.UpdateExisting(path, r => r.SetStamp(new PackStamp("other", 1, "fp")));
            AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
            List<Action> posted = [];
            HostLibrary library = new(cache, null, static () => true, posted.Add, () => facts);

            using (Assert.Multiple())
            {
                await Assert.That(library.Find(path)?.Fact(stampId)).IsNull().Because("off looks off on the first read, with nothing to wait for");
                await Assert.That(library.Demos.Single().Facts.Select(f => f.Id)).IsEquivalentTo(["other"]);
                await Assert.That(library.Query(new LibraryQuery { HasFact = stampId }).Total).IsEqualTo(0);
                await Assert.That(posted).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Test]
    public async Task ARowReadWhileAPackContributes_ShowsEveryStamp_AndThePostedRecheckDropsTheOffOnes()
    {
        int reads = 0;
        string empty = WriteFixture();
        try
        {
            MergedRulesBuild rules = new(() => WithFixture(empty),
                () =>
                {
                    reads++;
                    return [new StampedRuleset(FactsRuleset, "dev.example.x", static () => false)];
                });
            DemoCacheStore cache = new(null);
            const string path = "/demos/a.dem";
            string stampId = StampedFacts.StampId(FactsRuleset);
            cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
            cache.UpdateExisting(path, r => r.SetStamp(new PackStamp(stampId, StampedFacts.Schema, "fp")));
            AnalysisFacts facts = new(cache, new StampedFacts(rules), new RoundFactsSource(cache));
            List<Action> posted = [];
            HostLibrary library = new(cache, null, static () => true, posted.Add, () => facts);
            List<LibraryChange> changes = [];
            library.Changed += changes.Add;

            LibraryFactState? during = null;
            int readsDuring = -1;
            ReadingPack pack = new(() =>
            {
                during = library.Find(path)?.Fact(stampId);
                readsDuring = reads;
            });
            _ = new PackContributionSet([pack], new ServiceCollection().BuildServiceProvider());
            int postedWhileCollecting = posted.Count;
            LibraryFactState? afterCollecting = library.Find(path)?.Fact(stampId);
            int readsAfterCollecting = reads;
            int postedAfterCollecting = posted.Count;
            foreach (Action recheck in posted)
            {
                recheck();
            }

            using (Assert.Multiple())
            {
                await Assert.That(pack.Contributed).IsTrue();
                await Assert.That(readsDuring).IsEqualTo(0).Because("reading the stamped rulesets would collect the contributions again");
                await Assert.That(during).IsNotNull().Because("a row read while the packs contribute shows every stamp");
                await Assert.That(postedWhileCollecting).IsEqualTo(1);
                await Assert.That(afterCollecting).IsNull().Because("the next read after the packs contributed resolves the rulesets");
                await Assert.That(readsAfterCollecting).IsEqualTo(1);
                await Assert.That(postedAfterCollecting).IsEqualTo(1).Because("one recheck covers every deferred read");
                await Assert.That(changes).IsEquivalentTo(new[] { new LibraryChange(null, LibraryChangeKind.Updated) })
                    .Because("the posted recheck tells readers that rows projected while the packs contributed moved");
                await Assert.That(library.Find(path)?.Fact(stampId)).IsNull();
                await Assert.That(reads).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    [Test]
    public async Task ARulesetTheEngineLeftOutOfTheBuild_IsStampedFailed_AndTriedAgainOnlyOnceItChanges()
    {
        string fixture = WriteFixture((FactsRuleset, FactsYaml));
        try
        {
            MergedRulesBuild rules = new(() => WithFixture(fixture),
                () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(FactsRuleset, "dev.example.x", static () => true)]);
            StampedFacts stamped = new(rules);
            DemoCacheStore cache = new(null);
            const string path = "/demos/a.dem";
            cache.Upsert(new DemoCacheRecord { Path = path, Size = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
            AnalysisFacts facts = new(cache, stamped, new RoundFactsSource(cache));
            FactsEvaluator evaluator = new(cache, stamped);
            List<string> updated = [];
            evaluator.Updated += updated.Add;
            FactKey key = new(FactsRuleset, FactsTable);
            string stampId = StampedFacts.StampId(FactsRuleset);
            string fingerprint = stamped.Fingerprint(FactsRuleset, 64)!;
            bool wantedBefore = evaluator.Wants(path);

            // The ruleset composes alone, so it has a fingerprint; this demo's build is one the engine dropped it from.
            ParsedDemo demo = MergedRulesBuildTests.TwoFrameDemo();
            BuildResult leftOut = rules.BareRun(demo).Build with
            {
                ExcludedRulesets =
                [
                    new ExcludedRuleset(FactsRuleset, FactsRuleset + ".rules.yaml",
                    [
                        new RulesetCompositionDiagnostic(FactsRuleset, DiagnosticSeverity.Error, ResolveDiagnosticCodes.CrossRefCycle,
                            "a cycle through another ruleset", new SourcePosition(FactsRuleset + ".rules.yaml", 1, 1))
                    ])
                ]
            };
            AnalysisRun run = DemoAnalysis.Evaluate(demo, leftOut, new AnalysisOptions { CaptureSnapshots = false });
            ForwardDemoResult pass = new() { Demo = run.Demo, Rounds = [], FrameCount = 2, FirstServerTick = 1, LastServerTick = 500, Run = run };

            evaluator.EvaluateForward(path, pass);
            PackStamp? stamp = cache.TryGetIndex(path)?.Stamp(stampId);
            FactStatus failed = facts.Status(path, key);
            bool wantedAfter = evaluator.Wants(path);
            evaluator.EvaluateForward(path, pass);

            File.WriteAllText(Path.Combine(fixture, FactsRuleset + ".rules.yaml"), FactsYaml.Replace("label: FixtureKills", "label: FixtureKillsChanged"));
            rules.Invalidate();

            using (Assert.Multiple())
            {
                await Assert.That(wantedBefore).IsTrue();
                await Assert.That(stamp?.State).IsEqualTo(DemoAnalysisState.Failed);
                await Assert.That(stamp?.Fingerprint).IsEqualTo(fingerprint);
                await Assert.That(failed).IsEqualTo(FactStatus.Failed);
                await Assert.That(facts.TryGet(path, key)).IsNull();
                await Assert.That(cache.TryReadSiblingBytes(path, StampedFacts.Suffix(key))).IsNull().Because("a ruleset left out writes no table");
                await Assert.That(wantedAfter).IsFalse().Because("a failed write under the same fingerprint is settled");
                await Assert.That(updated).IsEquivalentTo(new[] { path }).Because("the second visit records nothing");
                await Assert.That(facts.Status(path, key)).IsEqualTo(FactStatus.Stale).Because("a changed ruleset is tried again");
                await Assert.That(evaluator.Wants(path)).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private sealed class ReadingPack(Action onContribute) : IExtension
    {
        public bool Contributed { get; private set; }

        public string Id => "net.demoviewer.test.readingpack";

        public string FeatureId => "pack.readingpack";

        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
            onContribute();
            Contributed = true;
        }
    }
}
