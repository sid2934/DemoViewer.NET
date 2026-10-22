#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The merged build over the shipped rules plus two synthetic stamped rulesets, each declaring
///     highlights so that leaving the fingerprint is a real decision: one gated by its extension, one
///     always on as core. A stamped ruleset rides the merged run while it is on, never enters the core set
///     or the highlights fingerprint, and is identified on its own. A real rule change in a highlight
///     ruleset still moves the fingerprint.
/// </summary>
[NotInParallel]
public class MergedRulesBuildTests
{
    /// <summary>
    ///     The highlights fingerprint of the shipped rules at 64 and 128 tick: what every indexed demo is
    ///     stamped with. A value that moves re-scans every demo's highlights, so only a deliberate edit to a
    ///     shipped highlight may change it.
    /// </summary>
    public const string ShippedFingerprint64 = "b54450c95ee039d26958d9a6da7c757303e87306a182d2b46065471c4309fed7";

    /// <inheritdoc cref="ShippedFingerprint64" />
    public const string ShippedFingerprint128 = "0f8078d804fae1a14a584fa4f98a9ed84581ddb255d036798b6929507f7fb346";

    private const string RoundFacts = "round_facts";
    private const string Gated = "fixture_gated";
    private const string Always = "fixture_always";
    private const string Extension = "net.example.fixture";

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    // The columns DemoCacheAnalysisProjector persists into the tier-3 scoreboard.
    private static string? _fixture;

    private static bool ReadsEnemySpotted(string yaml) => yaml.Split('\n')
        .Any(line => line.Split('#')[0].Contains("enemy_spotted", StringComparison.Ordinal));

    private static string RulesetIdOf(string yaml) => yaml.Split('\n')
        .First(line => line.StartsWith("ruleset:", StringComparison.Ordinal))["ruleset:".Length..].Trim();

    [Before(Class)]
    public static void WriteFixture()
    {
        string multikill = File.ReadAllText(Path.Combine(Shipped, "highlights_multikill.rules.yaml"));
        _fixture = Directory.CreateTempSubdirectory("dv-mrb-fixture-").FullName;
        foreach (string id in new[] { Gated, Always })
        {
            File.WriteAllText(Path.Combine(_fixture, id + ".rules.yaml"),
                multikill.Replace("ruleset: highlights_multikill", "ruleset: " + id, StringComparison.Ordinal)
                    .Replace("group: multikill", "group: " + id, StringComparison.Ordinal));
        }
    }

    [After(Class)]
    public static void DeleteFixture()
    {
        if (_fixture is not null && Directory.Exists(_fixture))
        {
            Directory.Delete(_fixture, true);
        }
    }

    private static RuleConfigLoadResult ShippedRules() => YamlConfigLoader.LoadWithOverlay(Shipped, null);

    private static RuleConfigLoadResult FixtureRules() => YamlConfigLoader.LoadWithOverlay(Shipped, _fixture);

    private static StampedRuleset GatedBy(Func<bool> on) => new(Gated, Extension, on);

    private static IReadOnlyList<string> Ids(IEnumerable<RulesetDoc> docs) => [.. docs.Select(d => d.Id)];

    // Order matters: the engine composes in read order, so the merged set must keep it.
    private static string Joined(IEnumerable<string> ids) => string.Join(",", ids);

    internal static ParsedDemo TwoFrameDemo() => SyntheticParsedDemo.Create(
        [
            new DemoFrame { CommandKind = EDemoCommands.DemPacket, FrameNumber = 0, ServerTick = 1, HeaderLength = 0, RawLength = 0, RawStart = 0, IsCompressed = false },
            new DemoFrame { CommandKind = EDemoCommands.DemPacket, FrameNumber = 1, ServerTick = 500, HeaderLength = 0, RawLength = 0, RawStart = 0, IsCompressed = false }
        ],
        [TestGameEvents.RoundFreezeEnd(frameNumber: 1, serverTick: 500, gameTick: 500)],
        tickCount: 500);

    private static string Compute(IEnumerable<RulesetDoc> docs, int tickRate = 64) =>
        HighlightConfigFingerprint.Compute([.. docs], tickRate, RulesHighlightHarvester.GotvProfileId).Fingerprint;

    [Test]
    public async Task TheFixture_DeclaresHighlights_SoLeavingTheFingerprintMeansSomething()
    {
        IReadOnlyList<RulesetDoc> raw = FixtureRules().Rulesets;

        using (Assert.Multiple())
        {
            await Assert.That(Ids(raw)).Contains(Gated);
            await Assert.That(Ids(raw)).Contains(Always);
            await Assert.That(Compute(raw.Where(r => r.Id != Always))).IsNotEqualTo(Compute(raw.Where(r => r.Id is not (Always or Gated))))
                .Because("the gated fixture's highlights move the fingerprint when counted");
            await Assert.That(Compute(raw.Where(r => r.Id != Gated))).IsNotEqualTo(Compute(raw.Where(r => r.Id is not (Always or Gated))))
                .Because("so do the always-on fixture's");
        }
    }

    [Test]
    public async Task GatedOn_TheMergedSetIsTheWholeRead_InReadOrder()
    {
        IReadOnlyList<RulesetDoc> raw = FixtureRules().Rulesets;
        MergedRulesBuild build = new(FixtureRules, () => [StampedRuleset.Core(Always), GatedBy(() => true)]);

        using (Assert.Multiple())
        {
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(Ids(raw))).Because("on: byte-identical to the read");
            await Assert.That(build.EnabledDoc(Gated)).IsNotNull();
            await Assert.That(Joined(Ids(build.CoreDocs))).IsEqualTo(Joined(Ids(raw.Where(r => r.Id is not (Gated or Always)))));
        }
    }

    [Test]
    public async Task GatedOff_TheRulesetLeavesTheMergedSet_AndNothingElseMoves()
    {
        IReadOnlyList<RulesetDoc> raw = FixtureRules().Rulesets;
        MergedRulesBuild build = new(FixtureRules, () => [StampedRuleset.Core(Always), GatedBy(() => false)]);

        using (Assert.Multiple())
        {
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(Ids(raw.Where(r => r.Id != Gated))))
                .Because("the forward pass does strictly less work with the extension off");
            await Assert.That(build.EnabledDoc(Gated)).IsNull();
            await Assert.That(build.EnabledDoc(Always)).IsNotNull();
            await Assert.That(Joined(Ids(build.CoreDocs))).IsEqualTo(Joined(Ids(raw.Where(r => r.Id is not (Gated or Always)))));
        }
    }

    [Test]
    public async Task AnAlwaysOnStampedRuleset_RidesTheMergedRun_ButStaysOutOfTheCoreSetAndTheFingerprint()
    {
        IReadOnlyList<RulesetDoc> raw = FixtureRules().Rulesets;
        MergedRulesBuild build = new(FixtureRules, () => [StampedRuleset.Core(Always)]);

        using (Assert.Multiple())
        {
            await Assert.That(Ids(build.Docs)).Contains(Always);
            await Assert.That(Ids(build.CoreDocs)).DoesNotContain(Always);
            await Assert.That(build.Fingerprint(64).Fingerprint).IsEqualTo(Compute(raw.Where(r => r.Id != Always)));
            await Assert.That(build.Fingerprint(64).Fingerprint).IsNotEqualTo(Compute(raw))
                .Because("counted, its highlights would have moved the stamp");
            await Assert.That(build.WithoutStampedRulesets(raw).Select(r => r.Id)).DoesNotContain(Always)
                .Because("the open demo's Stats run leaves every stamped ruleset out");
        }
    }

    [Test]
    public async Task TheHighlightsFingerprint_IsTheUnstampedSets_WhateverTheGateSays()
    {
        IReadOnlyList<RulesetDoc> raw = FixtureRules().Rulesets;
        MergedRulesBuild on = new(FixtureRules, () => [StampedRuleset.Core(Always), GatedBy(() => true)]);
        MergedRulesBuild off = new(FixtureRules, () => [StampedRuleset.Core(Always), GatedBy(() => false)]);
        string core = Compute(raw.Where(r => r.Id is not (Gated or Always)));

        using (Assert.Multiple())
        {
            await Assert.That(on.Fingerprint(64).Fingerprint).IsEqualTo(core);
            await Assert.That(off.Fingerprint(64).Fingerprint).IsEqualTo(core).Because("a toggle marks no highlights stale");
            await Assert.That(on.Fingerprint(128).Fingerprint).IsEqualTo(off.Fingerprint(128).Fingerprint);
        }
    }

    [Test]
    public async Task TheHighlightsFingerprint_IsPinned_WhicheverWayRoundFactsIsOwned()
    {
        MergedRulesBuild none = new(ShippedRules);
        MergedRulesBuild core = new(ShippedRules, () => [StampedRuleset.Core(RoundFacts)]);
        MergedRulesBuild on = new(ShippedRules, () => [new StampedRuleset(RoundFacts, Extension, () => true)]);
        MergedRulesBuild off = new(ShippedRules, () => [new StampedRuleset(RoundFacts, Extension, () => false)]);

        using (Assert.Multiple())
        {
            foreach (MergedRulesBuild build in new[] { none, core, on, off })
            {
                await Assert.That(build.Fingerprint(64).Fingerprint).IsEqualTo(ShippedFingerprint64);
                await Assert.That(build.Fingerprint(128).Fingerprint).IsEqualTo(ShippedFingerprint128);
            }
        }
    }

    // round_facts composes on its own for its identity; the highlights fingerprint never composes it. A user
    // override that does not compose blanks Round Facts and leaves the highlights backlog running.
    [Test]
    public async Task ABrokenRoundFactsOverride_LeavesTheHighlightsFingerprintAsItWas()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-mrb-broken-");
        try
        {
            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "round_facts.rules.yaml"));
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "round_facts.rules.yaml"),
                yaml + "\nhighlights:\n  broken:\n    when: no_such_stat >= 1\n    per: round\n    title: broken\n");
            MergedRulesBuild build = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName),
                () => [StampedRuleset.Core(RoundFacts)]);

            Exception? composed = null;
            try
            {
                _ = build.RulesetIdentity(RoundFacts, 64);
            }
            catch (Exception ex)
            {
                composed = ex;
            }

            AnalysisRun run = build.BareRun(TwoFrameDemo());

            using (Assert.Multiple())
            {
                await Assert.That(composed).IsNotNull().Because("the broken override does not compose on its own");
                await Assert.That(build.Fingerprint(64).Fingerprint).IsEqualTo(ShippedFingerprint64);
                await Assert.That(run.Build.ExcludedRulesets.Select(r => r.Id)).IsEquivalentTo([RoundFacts])
                    .Because("the merged run every highlights visit makes drops the broken ruleset alone");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }

    [Test]
    public async Task ALiveToggle_ReDerivesTheMergedSet_WithoutReReadingTheRules()
    {
        bool extensionOn = true;
        int reads = 0;
        MergedRulesBuild build = new(() =>
        {
            reads++;
            return FixtureRules();
        }, () => [GatedBy(() => extensionOn)]);

        string fingerprintOn = build.Fingerprint(64).Fingerprint;
        IReadOnlyList<string> docsOn = Ids(build.Docs);
        string identityOn = build.RulesetIdentity(Gated, 64);

        extensionOn = false;
        IReadOnlyList<string> docsOff = Ids(build.Docs);
        string fingerprintOff = build.Fingerprint(64).Fingerprint;

        extensionOn = true;
        using (Assert.Multiple())
        {
            await Assert.That(docsOn).Contains(Gated);
            await Assert.That(docsOff).DoesNotContain(Gated);
            await Assert.That(docsOff.Count).IsEqualTo(docsOn.Count - 1);
            await Assert.That(fingerprintOff).IsEqualTo(fingerprintOn);
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(docsOn)).Because("back on: the set is as it was");
            await Assert.That(build.RulesetIdentity(Gated, 64)).IsEqualTo(identityOn)
                .Because("the identity is the doc's, so turning back on re-runs only what was indexed while off");
            await Assert.That(reads).IsEqualTo(1).Because("the gate is read per access; the directories once");
        }
    }

    [Test]
    public async Task GatedOff_TheRulesetIdentity_HasNothingToAnswer()
    {
        MergedRulesBuild build = new(FixtureRules, () => [GatedBy(() => false)]);

        Assert.Throws<InvalidOperationException>(() => build.RulesetIdentity(Gated, 64));
        await Assert.That(build.WithoutStampedRulesets(FixtureRules().Rulesets).Select(r => r.Id)).DoesNotContain(Gated)
            .Because("the open demo's Stats run excludes a stamped ruleset whether or not it is on");
    }

    [Test]
    public async Task ARuleChangeInAHighlightRuleset_StillMovesTheFingerprint_WithTheGateOnOrOff()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-mrb-override-");
        try
        {
            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "highlights_multikill.rules.yaml"));
            await Assert.That(yaml).Contains("enemy_kills_round >= 5");
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "highlights_multikill.rules.yaml"),
                yaml.Replace("enemy_kills_round >= 5", "enemy_kills_round >= 6", StringComparison.Ordinal));

            MergedRulesBuild before = new(ShippedRules, () => [StampedRuleset.Core(RoundFacts)]);
            MergedRulesBuild afterOn = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName),
                () => [new StampedRuleset(RoundFacts, Extension, () => true)]);
            MergedRulesBuild afterOff = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName),
                () => [new StampedRuleset(RoundFacts, Extension, () => false)]);

            using (Assert.Multiple())
            {
                await Assert.That(afterOn.Fingerprint(64).Fingerprint).IsNotEqualTo(before.Fingerprint(64).Fingerprint);
                await Assert.That(afterOff.Fingerprint(64).Fingerprint).IsEqualTo(afterOn.Fingerprint(64).Fingerprint);
                await Assert.That(afterOn.RulesetIdentity(RoundFacts, 64)).IsEqualTo(before.RulesetIdentity(RoundFacts, 64))
                    .Because("a highlight edit never re-runs round facts");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }

    // The identity is cached per doc: every scheduler plan asks for it, and composing it costs about a
    // millisecond. A reload that edits the ruleset must not be answered from the cache.
    [Test]
    public async Task TheRulesetIdentity_IsCached_UntilTheRulesReload()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-mrb-identity-");
        try
        {
            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "round_facts.rules.yaml"));
            MergedRulesBuild build = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName),
                () => [StampedRuleset.Core(RoundFacts)]);
            string first = build.RulesetIdentity(RoundFacts, 64);
            await Assert.That(build.RulesetIdentity(RoundFacts, 64)).IsEqualTo(first);

            await File.WriteAllTextAsync(Path.Combine(user.FullName, "round_facts.rules.yaml"), yaml + "\n# edited\n");
            await Assert.That(build.RulesetIdentity(RoundFacts, 64)).IsEqualTo(first).Because("no reload yet");
            build.Invalidate();
            await Assert.That(build.RulesetIdentity(RoundFacts, 64)).IsNotEqualTo(first).Because("the source moved");
        }
        finally
        {
            user.Delete(true);
        }
    }

    // The bare run is cached per held parse. A run from before a toggle is not the merged set's run: kept,
    // a stamped ruleset's reader would find nothing of its own in it and pin the demo as "no rows".
    [Test]
    public async Task ABareRunCachedUnderOneGate_IsNotServedUnderAnother()
    {
        bool extensionOn = false;
        MergedRulesBuild build = new(FixtureRules, () => [StampedRuleset.Core(RoundFacts), GatedBy(() => extensionOn)]);
        ParsedDemo parsed = TwoFrameDemo();

        AnalysisRun off = build.BareRun(parsed);
        AnalysisRun offAgain = build.BareRun(parsed);
        extensionOn = true;
        AnalysisRun on = build.BareRun(parsed);
        AnalysisRun onAgain = build.BareRun(parsed);

        using (Assert.Multiple())
        {
            await Assert.That(offAgain).IsSameReferenceAs(off).Because("same gate: the cached run");
            await Assert.That(on).IsNotSameReferenceAs(off).Because("the gate moved: a fresh run over the merged set");
            await Assert.That(onAgain).IsSameReferenceAs(on);
            await Assert.That(off.Build.Outputs?.Select(o => o.Id) ?? []).Contains(RoundFacts)
                .Because("round_facts is core and rides every run");
            await Assert.That(Recorded(on)).IsEquivalentTo(WithLibrary(RoundFacts))
                .Because("the bare build keeps round_facts and the library tables, and only those");
        }
    }

    private static IReadOnlyList<string> Recorded(AnalysisRun run) => [.. (run.Build.Outputs ?? []).Select(o => o.Id).Order(StringComparer.Ordinal)];

    // Every merged run records the library tables beside whatever its cut names.
    private static string[] WithLibrary(params string[] tables) => [.. tables.Concat(LibraryTeamTables.Tables).Order(StringComparer.Ordinal)];

    // A cached run is served only when it recorded every table the caller's cut names.
    [Test]
    public async Task ABareRunCutToFewerTables_IsNotServedToACallerNeedingMore()
    {
        string fixture = AnalysisFactsTests.WriteFixture((AnalysisFactsTests.FactsRuleset, AnalysisFactsTests.FactsYaml));
        try
        {
            bool factsStale = false;
            MergedRulesBuild build = new(() => AnalysisFactsTests.WithFixture(fixture),
                () => [StampedRuleset.Core(RoundFacts), StampedRuleset.Core(AnalysisFactsTests.FactsRuleset)]);
            build.OutputsFor = _ => build.StampedOutputs(id => id != AnalysisFactsTests.FactsRuleset || factsStale);
            ParsedDemo parsed = TwoFrameDemo();

            AnalysisRun narrow = build.BareRun(parsed, "/demos/a.dem");
            factsStale = true;
            AnalysisRun wide = build.BareRun(parsed, "/demos/a.dem");
            AnalysisRun wideAgain = build.BareRun(parsed, "/demos/a.dem");

            using (Assert.Multiple())
            {
                await Assert.That(Recorded(narrow)).IsEquivalentTo(WithLibrary(RoundFacts)).Because("the facts are current, so only round_facts and the library tables are recorded");
                await Assert.That(wide).IsNotSameReferenceAs(narrow).Because("the narrow run lacks a table the caller needs");
                await Assert.That(Recorded(wide)).IsEquivalentTo(WithLibrary(AnalysisFactsTests.FactsTable, RoundFacts));
                await Assert.That(wideAgain).IsSameReferenceAs(wide);
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    // A collector writes only on the run its options carried, so a run made for a visit without it is not that visit's run.
    [Test]
    public async Task ABareRunWithoutACollector_IsNotServedToACallerWhoseVisitNeedsIt()
    {
        CollectorSet visit = CollectorSet.Empty;
        MergedRulesBuild build = new(ShippedRules, () => [StampedRuleset.Core(RoundFacts)]) { CollectorsFor = _ => visit };
        ParsedDemo parsed = TwoFrameDemo();

        AnalysisRun bare = build.BareRun(parsed, "/demos/a.dem");
        visit = CollectorSet.Of([CollectorKit.Register(CollectorKit.Counting("count.a"))]);
        AnalysisRun collected = build.BareRun(parsed, "/demos/a.dem");
        AnalysisRun again = build.BareRun(parsed, "/demos/a.dem");

        using (Assert.Multiple())
        {
            await Assert.That(collected).IsNotSameReferenceAs(bare).Because("the cached run never ran the collector");
            await Assert.That(again).IsSameReferenceAs(collected);
            await Assert.That(build.Collected(parsed, "/demos/a.dem").Get<CollectorKit.Count>("count.a")?.Frames).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ABareRunWithASupersetOfCollectors_IsServed()
    {
        RegisteredCollector a = CollectorKit.Register(CollectorKit.Counting("count.a"));
        RegisteredCollector b = CollectorKit.Register(CollectorKit.Counting("count.b"));
        CollectorSet visit = CollectorSet.Of([a, b]);
        MergedRulesBuild build = new(ShippedRules, () => [StampedRuleset.Core(RoundFacts)]) { CollectorsFor = _ => visit };
        ParsedDemo parsed = TwoFrameDemo();

        AnalysisRun both = build.BareRun(parsed, "/demos/a.dem");
        visit = CollectorSet.Of([a]);
        AnalysisRun one = build.BareRun(parsed, "/demos/a.dem");
        visit = CollectorSet.Empty;
        AnalysisRun none = build.BareRun(parsed, "/demos/a.dem");

        using (Assert.Multiple())
        {
            await Assert.That(one).IsSameReferenceAs(both).Because("the cached run carried every collector this visit needs");
            await Assert.That(none).IsSameReferenceAs(both);
            await Assert.That(build.Collected(parsed, "/demos/a.dem").Get<CollectorKit.Count>("count.b")?.Frames).IsEqualTo(2);
        }
    }

    // Pass order shrinks the cut: the first writer's stamps make the next caller's cut a subset.
    [Test]
    public async Task ABareRunRecordingASuperset_IsServed()
    {
        string fixture = AnalysisFactsTests.WriteFixture((AnalysisFactsTests.FactsRuleset, AnalysisFactsTests.FactsYaml));
        try
        {
            bool factsStale = true;
            MergedRulesBuild build = new(() => AnalysisFactsTests.WithFixture(fixture),
                () => [StampedRuleset.Core(RoundFacts), StampedRuleset.Core(AnalysisFactsTests.FactsRuleset)]);
            build.OutputsFor = _ => build.StampedOutputs(id => id != AnalysisFactsTests.FactsRuleset || factsStale);
            ParsedDemo parsed = TwoFrameDemo();

            AnalysisRun wide = build.BareRun(parsed, "/demos/a.dem");
            factsStale = false;
            AnalysisRun narrow = build.BareRun(parsed, "/demos/a.dem");
            AnalysisRun everything = build.BareRun(parsed);

            using (Assert.Multiple())
            {
                await Assert.That(Recorded(wide)).IsEquivalentTo(WithLibrary(AnalysisFactsTests.FactsTable, RoundFacts));
                await Assert.That(narrow).IsSameReferenceAs(wide).Because("the cached run recorded a superset of the cut");
                await Assert.That(everything).IsSameReferenceAs(wide).Because("the cached run recorded every stamped output");
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Test]
    public async Task MoreThanSixtyFourStampedRulesets_AreGatedOneByOne()
    {
        bool fixtureOn = true;
        List<StampedRuleset> stamped = [.. Enumerable.Range(0, 70).Select(i => new StampedRuleset($"absent_{i}", Extension, () => i % 2 == 0))];
        stamped.Add(GatedBy(() => fixtureOn));
        MergedRulesBuild build = new(FixtureRules, () => stamped);

        IReadOnlyList<string> on = Ids(build.Docs);
        fixtureOn = false;
        IReadOnlyList<string> off = Ids(build.Docs);

        using (Assert.Multiple())
        {
            await Assert.That(build.StampedRulesets.Count).IsEqualTo(71);
            await Assert.That(on).Contains(Gated);
            await Assert.That(off).DoesNotContain(Gated).Because("the 71st gate is read like the first");
        }
    }

    [Test]
    public async Task TheFirstRegistrationOfAnId_Wins()
    {
        MergedRulesBuild build = new(FixtureRules, () => [StampedRuleset.Core(Gated), GatedBy(() => false)]);

        using (Assert.Multiple())
        {
            await Assert.That(build.StampedRulesets.Select(r => r.Owner)).IsEquivalentTo([StampedRuleset.CoreOwner]);
            await Assert.That(Ids(build.Docs)).Contains(Gated).Because("core registers first, so an extension cannot switch its ruleset off");
        }
    }

    [Test]
    public async Task ABuildWithNoStampedRulesets_RunsEverythingItRead()
    {
        IReadOnlyList<RulesetDoc> raw = ShippedRules().Rulesets;
        MergedRulesBuild build = new(ShippedRules);

        using (Assert.Multiple())
        {
            await Assert.That(build.StampedRulesets).IsEmpty();
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(Ids(raw)));
            await Assert.That(Joined(Ids(build.CoreDocs))).IsEqualTo(Joined(Ids(raw)));
        }
    }

    // A background visit runs without the visibility bake and the open runs with it. That is sound only while
    // nothing persisted reads enemy_spotted, or what is persisted carries the visibility term: player_match's one
    // table stamps HasVisibility off its Spots column. When this fails, the same change loads the bake in the
    // background run or adds a visibility term to the new reader's stamp.
    [Test]
    public async Task NoStampedRulesetReferencesEnemySpotted()
    {
        string root = DemoTestHelper.RequireRepoRoot();
        string[] files =
        [
            .. Directory.EnumerateFiles(Shipped, "*.rules.yaml"),
            .. Directory.EnumerateFiles(Path.Combine(root, "samples"), "*.rules.yaml", SearchOption.AllDirectories)
        ];
        string[] readers =
        [
            .. files.Select(File.ReadAllText).Where(ReadsEnemySpotted)
                .Select(RulesetIdOf).Order(StringComparer.Ordinal)
        ];
        RulesetDoc aim = ShippedRules().Rulesets.Single(r => r.Id == "aim_rating");
        RulesetDoc match = ShippedRules().Rulesets.Single(r => r.Id == PlayerMatchFacet.RulesetId);

        using (Assert.Multiple())
        {
            await Assert.That(files.Length).IsGreaterThan(1);
            await Assert.That(Joined(readers)).IsEqualTo(Joined(["aim_rating", PlayerMatchFacet.RulesetId]))
                .Because("a new enemy_spotted reader needs the bake in the background run or a visibility stamp term");
            await Assert.That(aim.Highlights ?? []).IsEmpty().Because("highlights are persisted from the bake-free runs");
            await Assert.That(aim.Show?.Tables ?? []).IsEmpty().Because("stamped tables are persisted from the bake-free runs");
            await Assert.That(aim.Exports ?? []).IsEmpty().Because("an export could feed a persisted column");
            await Assert.That((match.Show?.Tables ?? []).Select(t => t.Name)).IsEquivalentTo([PlayerMatchFacet.Table])
                .Because("HasVisibility speaks for the facet table alone");
            await Assert.That(match.Show!.Tables.Single().Columns.Select(c => c.Label ?? c.Stat)).Contains(ColumnCatalogue.VisibilityAnchorColumn)
                .Because("the stamp's HasVisibility is read off this column");
            await Assert.That(match.Highlights ?? []).IsEmpty().Because("highlights are persisted from the bake-free runs");
            await Assert.That(match.Exports ?? []).IsEmpty().Because("an export could feed a persisted column");
        }
    }

    private const string OpenPath = "/demos/a.dem";

    // A build over the shipped rules plus the facts fixture, both stamped, with the facts table stale when
    // the caller says so. round_facts is stale whenever anything is.
    private static MergedRulesBuild FactsBuild(string fixture, Func<bool> factsStale, Func<bool>? gate = null)
    {
        MergedRulesBuild build = new(() => AnalysisFactsTests.WithFixture(fixture),
            () => [StampedRuleset.Core(RoundFacts), new StampedRuleset(AnalysisFactsTests.FactsRuleset, Extension, gate ?? (() => true))]);
        build.OutputsFor = _ => build.StampedOutputs(id => id != AnalysisFactsTests.FactsRuleset || factsStale());
        return build;
    }

    // What the shell does: plan from its own read, build, evaluate with snapshots.
    private static (OpenRunPlan Plan, AnalysisRun Run) OpenRun(MergedRulesBuild build, string fixture, ParsedDemo parsed)
    {
        OpenRunPlan plan = build.PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, OpenPath);
        return (plan, DemoAnalysis.Evaluate(parsed, plan.Cuts(DemoAnalysis.Build(parsed, plan.Docs))));
    }

    private static async Task WithFactsFixture(Func<string, Task> body)
    {
        string fixture = AnalysisFactsTests.WriteFixture((AnalysisFactsTests.FactsRuleset, AnalysisFactsTests.FactsYaml));
        try
        {
            await body(fixture);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    // A pass's own table rides the run while the pass wants the demo, reads back by name, and is never a facet.
    [Test]
    public async Task APassReadTable_IsRecordedWhileItsPassWantsTheDemo_AndOnlyItsReaderReadsIt() => await WithFactsFixture(async fixture =>
    {
        bool wanted = false;
        MergedRulesBuild build = new(() => AnalysisFactsTests.WithFixture(fixture),
            () => [StampedRuleset.Core(RoundFacts), new StampedRuleset(AnalysisFactsTests.FactsRuleset, Extension, () => true) { ReadBy = "ext.reader" }]);
        build.OutputsFor = _ => build.StampedOutputs(id => id != AnalysisFactsTests.FactsRuleset || wanted);
        ParsedDemo parsed = TwoFrameDemo();

        AnalysisRun without = build.BareRun(parsed, OpenPath);
        wanted = true;
        AnalysisRun with = build.BareRun(parsed, OpenPath);
        IReadOnlyList<FactRow> rows = build.Table(with, AnalysisFactsTests.FactsTable, [AnalysisFactsTests.FactsRuleset]);
        int projected = with.ProjectConfiguredOutputs().Single(t => t.Name == AnalysisFactsTests.FactsTable).Rows.Count;

        using (Assert.Multiple())
        {
            await Assert.That(Recorded(without)).IsEquivalentTo(WithLibrary(RoundFacts));
            await Assert.That(Recorded(with)).IsEquivalentTo(WithLibrary(AnalysisFactsTests.FactsTable, RoundFacts));
            await Assert.That(rows.Count).IsEqualTo(projected);
            await Assert.That(build.ReaderOf(AnalysisFactsTests.FactsRuleset)).IsEqualTo("ext.reader");
            await Assert.That(new StampedFacts(build).Facets()).IsEmpty().Because("a pass's table is its input, not a fact");
        }

        Assert.Throws<InvalidOperationException>(() => build.Table(with, AnalysisFactsTests.FactsTable, ["another_ruleset"]));
        Assert.Throws<InvalidOperationException>(() => build.Table(without, AnalysisFactsTests.FactsTable, [AnalysisFactsTests.FactsRuleset]));
        Assert.Throws<InvalidOperationException>(() => build.Table(with, RoundFacts, [AnalysisFactsTests.FactsRuleset]));
    });

    // The open plans its cut from what it was handed, so the passes it asks are asked off the UI thread.
    [Test]
    public async Task AnOpenPlannedFromATakenCut_UsesThatCut() => await WithFactsFixture(async fixture =>
    {
        MergedRulesBuild build = FactsBuild(fixture, () => false);
        HashSet<string> taken = new(StringComparer.Ordinal) { AnalysisFactsTests.FactsTable };

        OpenRunPlan plan = build.PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, OpenPath, taken);

        await Assert.That(plan.Cut).Contains(AnalysisFactsTests.FactsTable);
    });

    [Test]
    public async Task ASeededRun_IsServedToTheVisit() => await WithFactsFixture(async fixture =>
    {
        MergedRulesBuild build = FactsBuild(fixture, () => true);
        ParsedDemo parsed = TwoFrameDemo();
        (OpenRunPlan plan, AnalysisRun run) = OpenRun(build, fixture, parsed);

        bool seeded = build.Seed(parsed, OpenPath, plan, run);

        using (Assert.Multiple())
        {
            await Assert.That(plan.Cut).IsNotEmpty();
            await Assert.That(Joined(Ids(plan.Docs))).IsEqualTo(Joined(Ids(build.Docs))).Because("the open builds the merged set");
            await Assert.That(seeded).IsTrue();
            await Assert.That(build.BareRun(parsed, OpenPath)).IsSameReferenceAs(run).Because("the visit reads the open's run");
            await Assert.That(build.BareRun(parsed)).IsSameReferenceAs(run).Because("it recorded every stamped output");
        }
    });

    // The open's run is made before its visit is planned, so it declares every registered collector.
    [Test]
    public async Task ASeededRunCarryingTheOpenCollectors_IsServedToAVisitThatNeedsOne() => await WithFactsFixture(async fixture =>
    {
        CollectorSet registered = CollectorSet.Of([CollectorKit.Register(CollectorKit.Counting("count.a")),
            CollectorKit.Register(CollectorKit.Counting("count.b"))]);
        CollectorSet visit = CollectorSet.Of([registered.All[0]]);
        MergedRulesBuild build = FactsBuild(fixture, () => true);
        build.OpenCollectors = () => registered;
        build.CollectorsFor = _ => visit;
        ParsedDemo parsed = TwoFrameDemo();

        OpenRunPlan plan = build.PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, OpenPath);
        BuildResult built = plan.Cuts(DemoAnalysis.Build(parsed, plan.Docs, plan.Collectors.DeclareReads(new AnalysisOptions())));
        (AnalysisOptions options, CollectorObserver? observer) = plan.Collectors.Declare(new AnalysisOptions());
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, built, options);
        bool seeded = build.Seed(parsed, OpenPath, plan, run, observer?.Results);

        using (Assert.Multiple())
        {
            await Assert.That(plan.Collectors.Key).IsEqualTo(registered.Key);
            await Assert.That(seeded).IsTrue();
            await Assert.That(build.BareRun(parsed, OpenPath)).IsSameReferenceAs(run).Because("the open's run carried the visit's collector");
            await Assert.That(build.Collected(parsed, OpenPath).Get<CollectorKit.Count>("count.a")?.Frames).IsEqualTo(2);
        }
    });

    [Test]
    public async Task ASeedWithoutCollectorResults_IsNotServedToAVisitThatNeedsACollector() => await WithFactsFixture(async fixture =>
    {
        CollectorSet visit = CollectorSet.Of([CollectorKit.Register(CollectorKit.Counting("count.a"))]);
        MergedRulesBuild build = FactsBuild(fixture, () => true);
        build.CollectorsFor = _ => visit;
        ParsedDemo parsed = TwoFrameDemo();
        (OpenRunPlan plan, AnalysisRun run) = OpenRun(build, fixture, parsed);

        bool seeded = build.Seed(parsed, OpenPath, plan, run);

        using (Assert.Multiple())
        {
            await Assert.That(plan.Collectors.IsEmpty).IsTrue().Because("nothing registered a collector");
            await Assert.That(seeded).IsTrue();
            await Assert.That(build.BareRun(parsed, OpenPath)).IsNotSameReferenceAs(run).Because("the open's run never ran count.a");
            await Assert.That(build.Collected(parsed, OpenPath).Get<CollectorKit.Count>("count.a")?.Frames).IsEqualTo(2);
        }
    });

    [Test]
    public async Task NothingStale_TheOpenRunsTheCoreRulesetsAlone() => await WithFactsFixture(async fixture =>
    {
        MergedRulesBuild build = new(() => AnalysisFactsTests.WithFixture(fixture),
            () => [StampedRuleset.Core(RoundFacts), StampedRuleset.Core(AnalysisFactsTests.FactsRuleset)]);
        build.OutputsFor = _ => new HashSet<string>();
        ParsedDemo parsed = TwoFrameDemo();

        (OpenRunPlan plan, AnalysisRun run) = OpenRun(build, fixture, parsed);
        OpenRunPlan noPath = FactsBuild(fixture, () => true).PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, null);

        using (Assert.Multiple())
        {
            await Assert.That(plan.Cut).IsEmpty();
            await Assert.That(Joined(Ids(plan.Docs))).IsEqualTo(Joined(Ids(build.CoreDocs)));
            await Assert.That(Recorded(run)).IsEquivalentTo(Recorded(DemoAnalysis.Run(parsed, build.CoreDocs)));
            await Assert.That(build.Seed(parsed, OpenPath, plan, run)).IsFalse();
            await Assert.That(noPath.Cut).IsEmpty().Because("a demo with no file has no visit to seed");
        }
    });

    [Test]
    public async Task NothingStale_TheOpenRunStillCarriesTheVisitsCollectors() => await WithFactsFixture(async fixture =>
    {
        CollectorSet registered = CollectorSet.Of([CollectorKit.Register(CollectorKit.Counting("count.a"))]);
        MergedRulesBuild build = new(() => AnalysisFactsTests.WithFixture(fixture),
            () => [StampedRuleset.Core(RoundFacts), StampedRuleset.Core(AnalysisFactsTests.FactsRuleset)]);
        build.OutputsFor = _ => new HashSet<string>();
        build.OpenCollectors = () => registered;
        build.CollectorsFor = _ => registered;
        ParsedDemo parsed = TwoFrameDemo();

        OpenRunPlan plan = build.PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, OpenPath);
        BuildResult built = plan.Cuts(DemoAnalysis.Build(parsed, plan.Docs, plan.Collectors.DeclareReads(new AnalysisOptions())));
        (AnalysisOptions options, CollectorObserver? observer) = plan.Collectors.Declare(new AnalysisOptions());
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, built, options);
        bool seeded = build.Seed(parsed, OpenPath, plan, run, observer?.Results);
        CollectorResults collected = build.Collected(parsed, OpenPath);

        using (Assert.Multiple())
        {
            await Assert.That(plan.Cut).IsEmpty();
            await Assert.That(Joined(Ids(plan.Docs))).IsEqualTo(Joined(Ids(build.CoreDocs))).Because("the open still runs the core alone");
            await Assert.That(seeded).IsTrue();
            await Assert.That(collected.Get<CollectorKit.Count>("count.a")?.Frames).IsEqualTo(2);
            await Assert.That(collected).IsSameReferenceAs(observer!.Results).Because("the visit read the open's collectors");
            await Assert.That(build.HoldsRun(parsed)).IsFalse().Because("no rules reader can be served the open's run");
            await Assert.That(build.BareRun(parsed, OpenPath)).IsNotSameReferenceAs(run)
                .Because("the open's run recorded no round_facts for a reader of the rules");
        }
    });

    // An open that cannot seed shows the core graph whether its stamps are current or it has no file.
    [Test]
    public async Task AnOpenThatCannotSeed_ShowsTheCoreGraph() => await WithFactsFixture(async fixture =>
    {
        ParsedDemo parsed = TwoFrameDemo();
        MergedRulesBuild current = FactsBuild(fixture, () => false);
        current.OutputsFor = _ => new HashSet<string>();
        (OpenRunPlan currentPlan, AnalysisRun fresh) = OpenRun(current, fixture, parsed);
        MergedRulesBuild stale = FactsBuild(fixture, () => true);
        OpenRunPlan noPathPlan = stale.PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, null);
        AnalysisRun noPath = DemoAnalysis.Evaluate(parsed, noPathPlan.Cuts(DemoAnalysis.Build(parsed, noPathPlan.Docs)));
        AnalysisRun core = DemoAnalysis.Run(parsed, current.CoreDocs);

        static string Graph(AnalysisRun run) => string.Join('\n',
            run.FinalNodes.Select(n => n.Name)
                .Concat(run.Build.Edges.Select(e => $"{e.Source.Name}>{e.Destination.Name}:{e.Label}"))
                .Concat(run.Build.RulesetDiagnostics.Select(d => d.ToString()))
                .Concat(run.Timeline.Events.Select(e => e.ChainName)));

        using (Assert.Multiple())
        {
            await Assert.That(currentPlan.Cut).IsEmpty();
            await Assert.That(noPathPlan.Cut).IsEmpty();
            await Assert.That(Graph(fresh)).IsEqualTo(Graph(core));
            await Assert.That(Graph(noPath)).IsEqualTo(Graph(core));
            await Assert.That(Recorded(currentPlan.Shown(fresh))).IsEquivalentTo(Recorded(core));
        }
    });

    [Test]
    public async Task ASeedWithAnotherGateSet_IsDropped() => await WithFactsFixture(async fixture =>
    {
        bool on = true;
        MergedRulesBuild build = FactsBuild(fixture, () => true, () => on);
        ParsedDemo parsed = TwoFrameDemo();
        (OpenRunPlan plan, AnalysisRun run) = OpenRun(build, fixture, parsed);

        on = false;
        bool seeded = build.Seed(parsed, OpenPath, plan, run);

        using (Assert.Multiple())
        {
            await Assert.That(seeded).IsFalse();
            await Assert.That(build.BareRun(parsed, OpenPath)).IsNotSameReferenceAs(run);
        }
    });

    [Test]
    public async Task ASeedWithASmallerCut_IsDropped() => await WithFactsFixture(async fixture =>
    {
        bool factsStale = false;
        MergedRulesBuild build = FactsBuild(fixture, () => factsStale);
        ParsedDemo parsed = TwoFrameDemo();
        (OpenRunPlan plan, AnalysisRun run) = OpenRun(build, fixture, parsed);

        factsStale = true;
        bool seeded = build.Seed(parsed, OpenPath, plan, run);
        AnalysisRun visit = build.BareRun(parsed, OpenPath);

        using (Assert.Multiple())
        {
            await Assert.That(plan.Cut!.Order(StringComparer.Ordinal)).IsEquivalentTo(WithLibrary(RoundFacts));
            await Assert.That(seeded).IsFalse();
            await Assert.That(visit).IsNotSameReferenceAs(run);
            await Assert.That(Recorded(visit)).IsEquivalentTo(WithLibrary(AnalysisFactsTests.FactsTable, RoundFacts));
        }
    });

    [Test]
    public async Task ForgetReleasesTheSeed() => await WithFactsFixture(async fixture =>
    {
        MergedRulesBuild build = FactsBuild(fixture, () => true);
        ParsedDemo parsed = TwoFrameDemo();
        (OpenRunPlan plan, AnalysisRun run) = OpenRun(build, fixture, parsed);
        build.Seed(parsed, OpenPath, plan, run);

        build.Forget(parsed);

        await Assert.That(build.BareRun(parsed, OpenPath)).IsNotSameReferenceAs(run);
    });

    // The background read is cached for the session; the shell reads the rules on every run.
    [Test]
    public async Task ASeedAfterARulesEdit_IsNotPlanned() => await WithFactsFixture(async fixture =>
    {
        MergedRulesBuild build = FactsBuild(fixture, () => true);
        _ = build.Docs;
        string kast = Path.Combine(fixture, "kast.rules.yaml");
        File.WriteAllText(kast, File.ReadAllText(Path.Combine(Shipped, "kast.rules.yaml"))
            .Replace("title: ", "title: Edited ", StringComparison.Ordinal));

        OpenRunPlan plan = build.PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, OpenPath);

        using (Assert.Multiple())
        {
            await Assert.That(plan.Cut).IsEmpty();
            await Assert.That(plan.Docs.Single(d => d.Id == "kast").Title).StartsWith("Edited ")
                .Because("the shell runs what it read");
        }
    });

    // The run carries the stamped tables; the Stats tab does not see them.
    [Test]
    public async Task TheOpenRun_ShowsTheCoreOutputsOnly() => await WithFactsFixture(async fixture =>
    {
        MergedRulesBuild build = FactsBuild(fixture, () => true);
        ParsedDemo parsed = TwoFrameDemo();
        (OpenRunPlan plan, AnalysisRun run) = OpenRun(build, fixture, parsed);
        IReadOnlyList<string> core = Recorded(DemoAnalysis.Run(parsed, build.CoreDocs));

        using (Assert.Multiple())
        {
            await Assert.That(Recorded(run)).IsEquivalentTo([.. core.Append(AnalysisFactsTests.FactsTable).Append(RoundFacts).Order(StringComparer.Ordinal)]);
            await Assert.That(Recorded(plan.Shown(run))).IsEquivalentTo(core);
            await Assert.That(core).IsNotEmpty().Because("the shipped core rulesets declare tables");
        }
    });
}
