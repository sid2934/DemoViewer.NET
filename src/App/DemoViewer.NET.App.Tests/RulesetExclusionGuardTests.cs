#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Highlights;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Enums;

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

    // Fails COMPOSITION, not lowering: show: names a stat this ruleset never defines, which composition
    // itself refuses. Lowering never gets a look in.
    private const string UncomposableId = "net_example_fixture__uncomposable";

    private const string UncomposableYaml = """
                                             ruleset: net_example_fixture__uncomposable
                                             for: each_player
                                             stats:
                                               kills:
                                                 count: kill
                                                 per: match
                                             show:
                                               tables:
                                                 fixture_uncomposable:
                                                   per: player_match
                                                   columns:
                                                     - { stat: no_such_stat_anywhere, label: nope }
                                             """;

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static MergedRulesBuild Build(string? userDir, ContributedRuleset[] contributed, params string[] stamped)
    {
        HashSet<string> shippedIds = new(YamlConfigLoader.LoadWithOverlay(Shipped, null).Rulesets.Select(r => r.Id),
            StringComparer.Ordinal);
        HashSet<string> overridden = new(stamped, StringComparer.Ordinal);
        return new MergedRulesBuild(
            () => RuleLayers.WithExtensions(YamlConfigLoader.LoadWithOverlay(Shipped, userDir), shippedIds,
                overridden, contributed, NullLogger.Instance),
            () => [.. contributed.Select(c => new StampedRuleset(c.RulesetId, c.Owner, static () => true))]);
    }

    /// <summary>
    ///     The premise behind reporting exclusions from the record alone, settled empirically rather than by
    ///     reading the chain. A composition exclusion in a NON-stamped ruleset makes the highlights
    ///     fingerprint throw, because <c>HighlightConfigFingerprint.Compute</c> refuses any composition that
    ///     produced a diagnostic and every exclusion carries one. So the write that persists the set never
    ///     runs in exactly the case the set exists to explain.
    ///     <para>
    ///         And the same break in a STAMPED ruleset does NOT throw: <c>CoreDocs</c> leaves stamped
    ///         rulesets out so a broken copy of one cannot stop the fingerprint composing. The two halves
    ///         decide how much a report can learn from the record alone.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ACompositionExclusion_BreaksTheFingerprint_UnlessTheRulesetIsStamped()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-exclusion-compose-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(user.FullName, UncomposableId + ".rules.yaml"),
                UncomposableYaml);

            // The user's own file: not stamped, so it is in CoreDocs.
            MergedRulesBuild userRules = Build(user.FullName, []);
            Exception? userThrew = null;
            try
            {
                userRules.Fingerprint(64);
            }
            catch (Exception ex)
            {
                userThrew = ex;
            }

            IReadOnlyList<string> userExcluded =
                [.. userRules.BareRun(MergedRulesBuildTests.TwoFrameDemo()).Build.ExcludedRulesets.Select(r => r.Id)];

            // The same yaml contributed by an extension, and stamped.
            ContributedRuleset[] contributed = [new(UncomposableId, Owner, Owner, () => UncomposableYaml)];
            MergedRulesBuild stampedRules = Build(null, contributed);
            stampedRules.GuardOf = _ => null;
            Exception? stampedThrew = null;
            try
            {
                stampedRules.Fingerprint(64);
            }
            catch (Exception ex)
            {
                stampedThrew = ex;
            }

            using (Assert.Multiple())
            {
                await Assert.That(userExcluded).Contains(UncomposableId)
                    .Because("the ruleset really is dropped, or this test proves nothing");
                await Assert.That(userThrew).IsNotNull()
                    .Because("a composition exclusion in CoreDocs makes the fingerprint refuse to compute");
                await Assert.That(userThrew?.Message ?? "").Contains("failed composition")
                    .Because("and for THAT reason, not some unrelated throw on the way");
                await Assert.That(userThrew?.Message ?? "").Contains(UncomposableId)
                    .Because("the message names the ruleset, which is the information the report wants");
                await Assert.That(stampedThrew).IsNull()
                    .Because("CoreDocs leaves stamped rulesets out so a broken one cannot block the fingerprint");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }


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
            await Assert.That(excluded.Diagnostics.Select(d => d.Code)).IsEquivalentTo([BuildDiagnosticCodes.LoweringFailed], CollectionOrdering.Matching);
            await Assert.That(first.Build.Outputs!.Select(o => o.Id)).Contains("fixture_kills")
                .Because("the sibling builds and its table rides the run");
            await Assert.That(second.Build.ExcludedRulesets.Select(r => r.Id)).IsEquivalentTo([BadId], CollectionOrdering.Matching);
            await Assert.That(third.Build.ExcludedRulesets.Select(r => r.Id)).IsEquivalentTo([BadId], CollectionOrdering.Matching);
            await Assert.That(state.Count).IsEqualTo(1).Because("one report per ruleset content, not one per build");
            await Assert.That(state.LastSite).IsEqualTo("ruleset " + BadId);
            await Assert.That(state.Suspended).IsFalse();
            await Assert.That(suspension.Suspensions).IsEmpty();
            await Assert.That(asked).IsEquivalentTo([Owner], CollectionOrdering.Matching);
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
                await Assert.That(run.Build.ExcludedRulesets.Select(r => r.Id)).IsEquivalentTo([BadId], CollectionOrdering.Matching);
                await Assert.That(asked).IsEqualTo(0).Because("the user's file is the user's, not the extension's");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }
}
