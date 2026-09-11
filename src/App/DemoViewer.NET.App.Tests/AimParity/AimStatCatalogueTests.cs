#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     The catalogue names rule ids as strings, and <see cref="LiveAimStats" /> reads a missing one
///     as <c>null</c>, which every consumer then folds to 0 with <c>?? 0</c>. So a catalogue row that
///     points at a stat the ruleset no longer declares does not fail anything: it prints a plausible
///     zero under a plausible header, which is exactly how the spray side-by-side read a renamed stat
///     for a while. This pins every produced rule id against the ruleset on disk, so a rename in
///     <c>rules/aim_rating.rules.yaml</c> is a red test rather than a column of zeros.
/// </summary>
public class AimStatCatalogueTests
{
    /// <summary>Every rule id the catalogue claims the engine produces is a stat the ruleset declares.</summary>
    [Test]
    public async Task EveryProducedRuleId_NamesAStatTheRulesetDeclares()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        string path = Path.Combine(root, LiveAimStats.RulesetRelativePath);
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments([(LiveAimStats.RulesetRelativePath, File.ReadAllText(path))]);
        await Assert.That(loaded.Success).IsTrue()
            .Because(string.Join("\n", loaded.Errors.Select(e => e.ToString())));

        RulesetDoc doc = loaded.Rulesets.Single();
        await Assert.That(doc.Id).IsEqualTo(AimStatCatalogue.RulesetId);

        HashSet<string> declared = doc.Stats
            .Select(stat => doc.Id + "." + stat.Id)
            .ToHashSet(StringComparer.Ordinal);

        string[] missing = AimStatCatalogue.Produced()
            .Where(stat => !declared.Contains(stat.RuleId))
            .Select(stat => $"{stat.Canonical} -> {stat.RuleId}")
            .ToArray();

        await Assert.That(missing).IsEmpty()
            .Because("LiveAimStats reads each of these as null and every consumer folds that to 0:\n"
                     + string.Join("\n", missing));
    }
}
