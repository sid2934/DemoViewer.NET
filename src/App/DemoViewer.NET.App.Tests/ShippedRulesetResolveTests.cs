#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The shipped rulesets in <c>rules/</c> load and then resolve clean, which is tiers 1 and 2 of
///     <c>AnalysisBench rules check</c> with no demo, no Avalonia and no bake.
///     <para>
///         <b>This class is deliberately UNCATEGORISED</b>, which is what puts it in the in-flight
///         tier. The same assertion already existed in <c>RuleWorkbenchModuleTests</c>, but that
///         class is <c>Category("Integration")</c> and the in-flight tier excludes it. The gap is
///         not academic: a CS2DemoKit facet rename left six unknown-facet errors in
///         <c>aim_rating.rules.yaml</c>, and the standard tier reported 2010 passed, 0 failed with
///         every one of them present.
///     </para>
///     <para>
///         A resolve diagnostic is never a partial failure, which is why this is worth a tier of its
///         own. <c>RulesetResolve</c> returns nothing once it holds ANY diagnostic, so a single bad
///         facet discards every stat in the file that carries it. The board those stats feed then
///         renders EMPTY rather than wrong, and an empty board is the failure mode no assertion
///         about a value can see.
///     </para>
/// </summary>
public class ShippedRulesetResolveTests
{
    /// <summary>Tier 1 load, then tier 2 whole-set resolve, both with nothing to report.</summary>
    [Test]
    public async Task ShippedRulesets_LoadAndResolveWithNoDiagnostics()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(Path.Combine(root, "rules"));
        await Assert.That(loaded.Success).IsTrue()
            .Because("tier 1, the shipped rulesets parse: "
                     + string.Join("; ", loaded.Errors.Select(e => e.ToString())));
        await Assert.That(loaded.Rulesets.Count).IsGreaterThan(0)
            .Because("a resolve over no rulesets asserts nothing");

        // The whole set at once, not file by file: a qualified cross-ruleset read resolves against
        // the other loaded rulesets' exports, and a per-doc resolve would reject it falsely.
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets(loaded.Rulesets);
        await Assert.That(validated.Diagnostics.Count).IsEqualTo(0)
            .Because("tier 2, the shipped rulesets resolve demo-less: "
                     + string.Join("; ", validated.Diagnostics.Select(d => d.ToString())));
    }
}
