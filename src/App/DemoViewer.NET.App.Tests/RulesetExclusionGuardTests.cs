#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Highlights;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A contributed ruleset that validates but fails to lower is left out by the engine's build and reported
///     through its extension's guard, once for its content however many runs build it, while its sibling runs.
/// </summary>
[NotInParallel]
public class RulesetExclusionGuardTests
{
    private const string Owner = "net.example.fixture";
    private const string BadId = "net_example_fixture__bad";
    private const string HealthyId = "net_example_fixture__kills";

    // Validates (round is always present) but has no subject under for: match, which only lowering sees.
    private const string BadYaml = """
                                   ruleset: net_example_fixture__bad
                                   for: match
                                   stats:
                                     s:
                                       count: kill
                                       where: round.team.alive > 0
                                       per: match
                                   """;

    private const string HealthyYaml = """
                                       ruleset: net_example_fixture__kills
                                       for: each_player
                                       stats:
                                         kills:
                                           count: kill
                                           per: match
                                       show:
                                         tables:
                                           fixture_kills:
                                             per: player_match
                                             columns:
                                               - { stat: kills, label: kills }
                                       """;

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    [Test]
    public async Task AContributedRulesetThatFailsToLower_IsExcludedAndReportedOnce_WhileItsSiblingRuns()
    {
        ContributedRuleset[] contributed =
        [
            new(BadId, Owner, Owner, () => BadYaml),
            new(HealthyId, Owner, Owner, () => HealthyYaml)
        ];
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadWithOverlay(Shipped, null);
        HashSet<string> shippedIds = new(shipped.Rulesets.Select(r => r.Id), StringComparer.Ordinal);
        MergedRulesBuild rules = new(
            () => RuleLayers.WithExtensions(YamlConfigLoader.LoadWithOverlay(Shipped, null), shippedIds, new HashSet<string>(),
                contributed, NullLogger.Instance),
            () => [.. contributed.Select(c => new StampedRuleset(c.RulesetId, c.Owner, static () => true))]);

        PostQueue posts = new();
        ExtensionScope scope = new(Owner, Owner, "Fixture", typeof(RulesetExclusionGuardTests).Assembly);
        ExtensionFaults faults = new([scope], posts.Post, new ManualClock());
        RecordingSwitch suspension = new();
        faults.AttachSwitch(suspension);
        ExtensionGuard guard = faults.GuardFor(scope);
        List<string> asked = [];
        rules.GuardOf = owner =>
        {
            asked.Add(owner);
            return string.Equals(owner, Owner, StringComparison.Ordinal) ? guard : null;
        };

        AnalysisRun first = rules.BareRun(MergedRulesBuildTests.TwoFrameDemo());
        AnalysisRun second = rules.BareRun(MergedRulesBuildTests.TwoFrameDemo());
        AnalysisRun third = rules.BareRun(MergedRulesBuildTests.TwoFrameDemo());
        posts.Drain();

        ExcludedRuleset excluded = first.Build.ExcludedRulesets.Single();
        ExtensionFaultState state = faults.StateOf(Owner);
        using (Assert.Multiple())
        {
            await Assert.That(excluded.Id).IsEqualTo(BadId);
            await Assert.That(excluded.Diagnostics.Select(d => d.Code)).IsEquivalentTo([BuildDiagnosticCodes.LoweringFailed]);
            await Assert.That(first.Build.Outputs!.Select(o => o.Id)).Contains("fixture_kills")
                .Because("the sibling builds and its table rides the run");
            await Assert.That(second.Build.ExcludedRulesets.Select(r => r.Id)).IsEquivalentTo([BadId]);
            await Assert.That(third.Build.ExcludedRulesets.Select(r => r.Id)).IsEquivalentTo([BadId]);
            await Assert.That(state.Count).IsEqualTo(1).Because("one report per ruleset content, not one per build");
            await Assert.That(state.LastSite).IsEqualTo("ruleset " + BadId);
            await Assert.That(state.Suspended).IsFalse();
            await Assert.That(suspension.Suspensions).IsEmpty();
            await Assert.That(asked).IsEquivalentTo([Owner]);
        }
    }

    [Test]
    public async Task AUserOverrideThatFailsToLower_IsNotReportedAgainstTheExtension()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-exclusion-user-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(user.FullName, BadId + ".rules.yaml"), BadYaml);
            ContributedRuleset[] contributed = [new(BadId, Owner, Owner, () => BadYaml)];
            HashSet<string> shippedIds = new(YamlConfigLoader.LoadWithOverlay(Shipped, null).Rulesets.Select(r => r.Id),
                StringComparer.Ordinal);
            MergedRulesBuild rules = new(
                () => RuleLayers.WithExtensions(YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName), shippedIds,
                    new HashSet<string>(StringComparer.Ordinal) { BadId }, contributed, NullLogger.Instance),
                () => [.. contributed.Select(c => new StampedRuleset(c.RulesetId, c.Owner, static () => true))]);
            int asked = 0;
            rules.GuardOf = _ =>
            {
                asked++;
                return null;
            };

            AnalysisRun run = rules.BareRun(MergedRulesBuildTests.TwoFrameDemo());

            using (Assert.Multiple())
            {
                await Assert.That(run.Build.ExcludedRulesets.Select(r => r.Id)).IsEquivalentTo([BadId]);
                await Assert.That(asked).IsEqualTo(0).Because("the user's file is the user's, not the extension's");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }
}
