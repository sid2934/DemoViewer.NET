#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.TestSupport;

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
    private static string? _fixture;

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

    private static ParsedDemo TwoFrameDemo() => SyntheticParsedDemo.Create(
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
            await Assert.That(on.Build.Outputs?.Select(o => o.Id) ?? []).IsEquivalentTo([RoundFacts])
                .Because("the bare build keeps the round_facts output, and only that one");
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
}
