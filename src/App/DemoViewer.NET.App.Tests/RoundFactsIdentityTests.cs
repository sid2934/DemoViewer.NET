#region

using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A user override of one <c>round_facts</c> parameter must change the fingerprint the rows are stored
///     under. The formula before the merge could not see it: the engine's fingerprint hashes highlight
///     definitions, and <c>round_facts</c> declares none.
/// </summary>
public class RoundFactsIdentityTests
{
    [Test]
    public async Task AnOverriddenThreshold_ChangesTheFingerprint_TheOldFormulaDidNot()
    {
        string shipped = RuleSetLocator.ResolveShippedRulesDirectory();
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-rf-override-");
        try
        {
            string yaml = await File.ReadAllTextAsync(Path.Combine(shipped, "round_facts.rules.yaml"));
            await Assert.That(yaml).Contains("default: 1000,  min");
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "round_facts.rules.yaml"),
                yaml.Replace("default: 1000,  min", "default: 1500,  min", StringComparison.Ordinal));

            MergedRulesBuild before = new(() => YamlConfigLoader.LoadWithOverlay(shipped, null));
            MergedRulesBuild after = new(() => YamlConfigLoader.LoadWithOverlay(shipped, user.FullName));
            RulesetDoc beforeDoc = before.EnabledDoc(RoundFactsFingerprint.RulesetId)!;
            RulesetDoc afterDoc = after.EnabledDoc(RoundFactsFingerprint.RulesetId)!;

            using (Assert.Multiple())
            {
                await Assert.That(Convert.ToInt32(EngineRoundFactsRowSource.ParametersOf(afterDoc)["eco_max_per_player"], System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1500);
                await Assert.That(new RulesRoundFactsRulesetIdentity(after).Fingerprint(64))
                    .IsNotEqualTo(new RulesRoundFactsRulesetIdentity(before).Fingerprint(64));
                await Assert.That(new RulesRoundFactsRulesetIdentity(before).Fingerprint(64)).IsNotNull();
                await Assert.That(Old(afterDoc)).IsEqualTo(Old(beforeDoc));
                await Assert.That(after.Fingerprint(64).Fingerprint).IsEqualTo(before.Fingerprint(64).Fingerprint)
                    .Because("highlights do not re-scan when only round_facts changes");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }

    private static string Old(RulesetDoc doc) => RoundFactsFingerprint.Combine(RoundFactsRecords.Schema,
        HighlightConfigFingerprint.Compute([doc], 64, RulesHighlightHarvester.GotvProfileId).Fingerprint);
}
