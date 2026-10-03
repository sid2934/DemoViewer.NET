#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The merged build with a pack-owned ruleset (<c>round_facts</c>, the Strat Book's): with the pack on
///     the merged set is the whole read, with it off the ruleset leaves the set, and the highlights
///     fingerprint is the core set's either way, so a pack toggle re-scans no demo's highlights. A real
///     rule change in a core ruleset still moves it.
/// </summary>
public class MergedRulesBuildTests
{
    private const string RoundFacts = "round_facts";

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static RuleConfigLoadResult ShippedRules() => YamlConfigLoader.LoadWithOverlay(Shipped, null);

    private static IReadOnlyList<string> Ids(IEnumerable<RulesetDoc> docs) => [.. docs.Select(d => d.Id)];

    // Order matters: the engine composes in read order, so the merged set must keep it.
    private static string Joined(IEnumerable<string> ids) => string.Join(",", ids);

    [Test]
    public async Task PackOn_TheMergedSetIsTheWholeRead_InReadOrder()
    {
        IReadOnlyList<RulesetDoc> raw = ShippedRules().Rulesets;
        MergedRulesBuild build = new(ShippedRules, () => [new GatedRuleset(RoundFacts, () => true)]);

        using (Assert.Multiple())
        {
            await Assert.That(Ids(raw)).Contains(RoundFacts).Because("the fixture must carry the ruleset for the test to mean anything");
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(Ids(raw))).Because("pack on: byte-identical to the read");
            await Assert.That(build.EnabledDoc(RoundFacts)).IsNotNull();
            await Assert.That(Joined(Ids(build.CoreDocs))).IsEqualTo(Joined(Ids(raw.Where(r => r.Id != RoundFacts))));
        }
    }

    [Test]
    public async Task PackOff_TheRulesetLeavesTheMergedSet_AndNothingElseMoves()
    {
        IReadOnlyList<RulesetDoc> raw = ShippedRules().Rulesets;
        MergedRulesBuild build = new(ShippedRules, () => [new GatedRuleset(RoundFacts, () => false)]);

        using (Assert.Multiple())
        {
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(Ids(raw.Where(r => r.Id != RoundFacts))))
                .Because("the forward pass does strictly less work with the pack off");
            await Assert.That(build.EnabledDoc(RoundFacts)).IsNull();
            await Assert.That(Joined(Ids(build.CoreDocs))).IsEqualTo(Joined(Ids(raw.Where(r => r.Id != RoundFacts))));
        }
    }

    [Test]
    public async Task TheHighlightsFingerprint_IsTheCoreSets_WhateverTheGateSays()
    {
        MergedRulesBuild none = new(ShippedRules);
        MergedRulesBuild on = new(ShippedRules, () => [new GatedRuleset(RoundFacts, () => true)]);
        MergedRulesBuild off = new(ShippedRules, () => [new GatedRuleset(RoundFacts, () => false)]);
        string core = HighlightConfigFingerprint.Compute(
            [.. ShippedRules().Rulesets.Where(r => r.Id != RoundFacts)], 64, RulesHighlightHarvester.GotvProfileId).Fingerprint;

        using (Assert.Multiple())
        {
            await Assert.That(on.Fingerprint(64).Fingerprint).IsEqualTo(core);
            await Assert.That(off.Fingerprint(64).Fingerprint).IsEqualTo(core).Because("a toggle marks no highlights stale");
            await Assert.That(none.Fingerprint(64).Fingerprint).IsEqualTo(core)
                .Because("the ruleset declares no highlights, so the build before the split stamped the same value");
            await Assert.That(on.Fingerprint(128).Fingerprint).IsEqualTo(off.Fingerprint(128).Fingerprint);
        }
    }

    [Test]
    public async Task ALiveToggle_ReDerivesTheMergedSet_WithoutReReadingTheRules()
    {
        bool packOn = true;
        int reads = 0;
        MergedRulesBuild build = new(() =>
        {
            reads++;
            return ShippedRules();
        }, () => [new GatedRuleset(RoundFacts, () => packOn)]);

        string fingerprintOn = build.Fingerprint(64).Fingerprint;
        IReadOnlyList<string> docsOn = Ids(build.Docs);
        string identityOn = build.RulesetIdentity(RoundFacts, 64);

        packOn = false;
        IReadOnlyList<string> docsOff = Ids(build.Docs);
        string fingerprintOff = build.Fingerprint(64).Fingerprint;

        packOn = true;
        using (Assert.Multiple())
        {
            await Assert.That(docsOn).Contains(RoundFacts);
            await Assert.That(docsOff).DoesNotContain(RoundFacts);
            await Assert.That(docsOff.Count).IsEqualTo(docsOn.Count - 1);
            await Assert.That(fingerprintOff).IsEqualTo(fingerprintOn);
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(docsOn)).Because("back on: the set is as it was");
            await Assert.That(build.RulesetIdentity(RoundFacts, 64)).IsEqualTo(identityOn)
                .Because("the pack's own identity is the doc's, so re-enabling re-runs only what was indexed while off");
            await Assert.That(reads).IsEqualTo(1).Because("the gate is read per access; the directories once");
        }
    }

    [Test]
    public async Task PackOff_TheRulesetIdentity_HasNothingToAnswer()
    {
        MergedRulesBuild build = new(ShippedRules, () => [new GatedRuleset(RoundFacts, () => false)]);

        Assert.Throws<InvalidOperationException>(() => build.RulesetIdentity(RoundFacts, 64));
        await Assert.That(build.WithoutPackRulesets(ShippedRules().Rulesets).Select(r => r.Id)).DoesNotContain(RoundFacts)
            .Because("the open demo's Stats run excludes a pack's ruleset whether or not the pack is on");
    }

    [Test]
    public async Task ARuleChangeInACoreRuleset_StillMovesTheFingerprint_WithThePackOnOrOff()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-mrb-override-");
        try
        {
            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "highlights_multikill.rules.yaml"));
            await Assert.That(yaml).Contains("enemy_kills_round >= 5");
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "highlights_multikill.rules.yaml"),
                yaml.Replace("enemy_kills_round >= 5", "enemy_kills_round >= 6", StringComparison.Ordinal));

            MergedRulesBuild before = new(ShippedRules, () => [new GatedRuleset(RoundFacts, () => true)]);
            MergedRulesBuild afterOn = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName),
                () => [new GatedRuleset(RoundFacts, () => true)]);
            MergedRulesBuild afterOff = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName),
                () => [new GatedRuleset(RoundFacts, () => false)]);

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

    // The bare run is cached per held parse. A run from before a pack toggle is not the merged set's run:
    // kept, the row source would read an empty table off it and pin the demo as "no rows" for the session.
    [Test]
    public async Task ABareRunCachedUnderOneGate_IsNotServedUnderAnother()
    {
        bool packOn = false;
        MergedRulesBuild build = new(ShippedRules, () => [new GatedRuleset(RoundFacts, () => packOn)]);
        ParsedDemo parsed = SyntheticParsedDemo.Create(
            [
                new DemoFrame { CommandKind = EDemoCommands.DemPacket, FrameNumber = 0, ServerTick = 1, HeaderLength = 0, RawLength = 0, RawStart = 0, IsCompressed = false },
                new DemoFrame { CommandKind = EDemoCommands.DemPacket, FrameNumber = 1, ServerTick = 500, HeaderLength = 0, RawLength = 0, RawStart = 0, IsCompressed = false }
            ],
            [TestGameEvents.RoundFreezeEnd(frameNumber: 1, serverTick: 500, gameTick: 500)],
            tickCount: 500);

        AnalysisRun off = build.BareRun(parsed);
        AnalysisRun offAgain = build.BareRun(parsed);
        packOn = true;
        AnalysisRun on = build.BareRun(parsed);
        AnalysisRun onAgain = build.BareRun(parsed);

        using (Assert.Multiple())
        {
            await Assert.That(offAgain).IsSameReferenceAs(off).Because("same gate: the cached run");
            await Assert.That(on).IsNotSameReferenceAs(off).Because("the gate moved: a fresh run over the merged set");
            await Assert.That(onAgain).IsSameReferenceAs(on);
            await Assert.That(off.Build.Outputs?.Select(o => o.Id) ?? []).DoesNotContain(RoundFacts);
            await Assert.That(on.Build.Outputs?.Select(o => o.Id) ?? []).Contains(RoundFacts)
                .Because("the bare build keeps the round_facts output, and only that one");
        }
    }

    [Test]
    public async Task AnUnclaimedBuild_RunsEverythingItRead()
    {
        IReadOnlyList<RulesetDoc> raw = ShippedRules().Rulesets;
        MergedRulesBuild build = new(ShippedRules);

        using (Assert.Multiple())
        {
            await Assert.That(build.PackRulesets).IsEmpty();
            await Assert.That(Joined(Ids(build.Docs))).IsEqualTo(Joined(Ids(raw)));
            await Assert.That(Joined(Ids(build.CoreDocs))).IsEqualTo(Joined(Ids(raw)));
        }
    }
}
