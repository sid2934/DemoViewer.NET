#region

using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.Facts;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An extension's ruleset as the middle rule layer: it joins after the shipped rules, a user file of the same
///     id overrides or disables it, and one that does not load, claims another id or reuses another ruleset's
///     table is left out with nothing else moving. Its identity is its YAML's, stable across reads.
/// </summary>
[NotInParallel]
public class RuleLayersTests
{
    private const string Owner = "dev.example.layers";

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static string Id(string local) => RulesetContribution.QualifiedId(Owner, local);

    private static string Yaml(string id, string table, string label = "LayerKills") => $$"""
        ruleset: {{id}}
        for: each_player
        stats:
          kills:
            count: kill
            per: match
            label: {{label}}
        show:
          tables:
            {{table}}:
              per: player_match
              columns:
                - { stat: kills, label: {{table}}_count }
        """;

    private static ContributedRuleset Contributed(string id, string? yaml) => new(id, Owner, "pack." + Owner, () => yaml);

    private static RuleConfigLoadResult Load(string? userDir, params ContributedRuleset[] extensions) =>
        RuleLayers.Load(Shipped, userDir, extensions, NullLogger.Instance);

    private static IReadOnlyList<string> Ids(RuleConfigLoadResult result) => [.. result.Rulesets.Select(r => r.Id)];

    [Test]
    public async Task QualifiedIds_PrefixTheExtension_AndRefuseNamesThatAreNotRulesetIds()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RulesetContribution.QualifiedId("dev.example.Hello-World", "kills")).IsEqualTo("dev_example_hello_world__kills");
            await Assert.That(RulesetContribution.IsValidId("Kills")).IsFalse();
            await Assert.That(RulesetContribution.IsValidId("2kills")).IsFalse();
            await Assert.That(RulesetContribution.IsValidId("my.kills")).IsFalse();
            await Assert.That(RulesetContribution.IsValidId("my_kills2")).IsTrue();
        }

        Assert.Throws<ArgumentException>(() => RulesetContribution.QualifiedId(Owner, "a.b"));
    }

    [Test]
    public async Task AnExtensionRuleset_JoinsAfterTheShippedRules()
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadWithOverlay(Shipped, null);
        RuleConfigLoadResult layered = Load(null, Contributed(Id("kills"), Yaml(Id("kills"), "layer_kills")));

        await Assert.That(Ids(layered)).IsEquivalentTo([.. Ids(shipped), Id("kills")]);
        await Assert.That(Ids(layered)[^1]).IsEqualTo(Id("kills"));
    }

    [Test]
    public async Task AUserFileOfTheSameId_OverridesIt_OrSwitchesItOff()
    {
        string user = Directory.CreateTempSubdirectory("dv-layers-user-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(user, "override.rules.yaml"), Yaml(Id("kills"), "layer_kills", "UserKills"));
            RuleConfigLoadResult overridden = Load(user, Contributed(Id("kills"), Yaml(Id("kills"), "layer_kills")));
            RulesetDoc doc = overridden.Rulesets.Single(r => r.Id == Id("kills"));

            File.WriteAllText(Path.Combine(user, "override.rules.yaml"), $"ruleset: {Id("kills")}\nenabled: false\n");
            RuleConfigLoadResult disabled = Load(user, Contributed(Id("kills"), Yaml(Id("kills"), "layer_kills")));

            using (Assert.Multiple())
            {
                await Assert.That(doc.Stats.Single().Label).IsEqualTo("UserKills").Because("the user's copy, not the extension's");
                await Assert.That(RuleLayers.SourceOf(doc)).IsNull().Because("it was read from the user's file");
                await Assert.That(Ids(disabled)).DoesNotContain(Id("kills"));
            }
        }
        finally
        {
            Directory.Delete(user, true);
        }
    }

    [Test]
    public async Task ARulesetThatDoesNotFit_IsLeftOut_AndNothingElseMoves()
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadWithOverlay(Shipped, null);
        RuleConfigLoadResult layered = Load(null,
            Contributed(Id("broken"), "ruleset: [this is not a ruleset"),
            Contributed(Id("renamed"), Yaml("someone_else", "layer_renamed")),
            Contributed(Id("clash"), Yaml(Id("clash"), "round_facts")),
            Contributed(Id("missing"), null),
            Contributed(Id("good"), Yaml(Id("good"), "layer_good")));

        await Assert.That(Ids(layered)).IsEquivalentTo([.. Ids(shipped), Id("good")]);
    }

    [Test]
    public async Task ItsIdentity_IsItsYaml_StableAcrossReads_AndTheHighlightsStampNeverMoves()
    {
        ContributedRuleset kills = Contributed(Id("kills"), Yaml(Id("kills"), "layer_kills"));
        MergedRulesBuild first = new(() => Load(null, kills),
            () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(kills.RulesetId, Owner, static () => true)]);
        MergedRulesBuild second = new(() => Load(null, kills),
            () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(kills.RulesetId, Owner, static () => true)]);
        MergedRulesBuild edited = new(() => Load(null, Contributed(Id("kills"), Yaml(Id("kills"), "layer_kills", "Edited"))),
            () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), new StampedRuleset(kills.RulesetId, Owner, static () => true)]);

        using (Assert.Multiple())
        {
            await Assert.That(first.RulesetIdentity(kills.RulesetId, 64)).IsEqualTo(second.RulesetIdentity(kills.RulesetId, 64));
            await Assert.That(edited.RulesetIdentity(kills.RulesetId, 64)).IsNotEqualTo(first.RulesetIdentity(kills.RulesetId, 64));
            await Assert.That(first.Fingerprint(64).Fingerprint).IsEqualTo(MergedRulesBuildTests.ShippedFingerprint64);
            await Assert.That(first.StampedOutputs()).Contains("layer_kills");
        }
    }
}
