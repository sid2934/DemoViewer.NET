#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A stamped ruleset with <c>use:</c> composes with the rulesets it reads, so its identity covers their
///     sources too. <c>player_stats</c> (<c>use: [ kast ]</c>) is the shipped case. A ruleset without
///     <c>use:</c> hashes exactly as before the closure, so every existing round facts stamp stays current.
/// </summary>
public class RulesetIdentityTests
{
    private const string PlayerStats = "player_stats";
    private const string Kast = "kast";

    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    [Test]
    public async Task ClosureMovesWithADependency()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-ri-closure-");
        try
        {
            MergedRulesBuild before = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName));
            string playerStats = before.RulesetIdentity(PlayerStats, 64);
            string kast = before.RulesetIdentity(Kast, 64);
            string roundFacts = before.RulesetIdentity(RoundFactsFingerprint.RulesetId, 64);

            string yaml = await File.ReadAllTextAsync(Path.Combine(Shipped, "kast.rules.yaml"));
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "kast.rules.yaml"), yaml + "\n# edited\n");
            before.Invalidate();

            using (Assert.Multiple())
            {
                await Assert.That(before.RulesetIdentity(PlayerStats, 64)).IsNotEqualTo(playerStats)
                    .Because("player_stats reads kast, so an edit to kast's source re-runs it");
                await Assert.That(before.RulesetIdentity(Kast, 64)).IsNotEqualTo(kast);
                await Assert.That(before.RulesetIdentity(RoundFactsFingerprint.RulesetId, 64)).IsEqualTo(roundFacts)
                    .Because("round_facts reads nothing of kast's");
            }
        }
        finally
        {
            user.Delete(true);
        }
    }

    [Test]
    public async Task ClosureWithADisabledDependency_HasNothingToAnswer()
    {
        DirectoryInfo user = Directory.CreateTempSubdirectory("dv-ri-disabled-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(user.FullName, "kast.rules.yaml"), "ruleset: kast\nenabled: false\n");
            MergedRulesBuild build = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, user.FullName));
            await Assert.That(build.EnabledDoc(Kast)).IsNull();

            Assert.Throws<InvalidOperationException>(() => build.RulesetIdentity(PlayerStats, 64));
        }
        finally
        {
            user.Delete(true);
        }
    }

    [Test]
    public async Task NoUseKeepsIdentity()
    {
        MergedRulesBuild build = new(() => YamlConfigLoader.LoadWithOverlay(Shipped, null));
        RulesetDoc doc = build.EnabledDoc(RoundFactsFingerprint.RulesetId)!;
        await Assert.That(doc.Use).IsEmpty();

        using (Assert.Multiple())
        {
            await Assert.That(build.RulesetIdentity(RoundFactsFingerprint.RulesetId, 64)).IsEqualTo(Alone(doc, 64));
            await Assert.That(build.RulesetIdentity(RoundFactsFingerprint.RulesetId, 128)).IsEqualTo(Alone(doc, 128));
        }
    }

    // The formula before the closure: the doc composed alone, its file's hash, the engine version.
    private static string Alone(RulesetDoc doc, int tickRate)
    {
        string composed = HighlightConfigFingerprint.Compute([doc], tickRate, RulesHighlightHarvester.GotvProfileId).Fingerprint;
        string source = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(doc.Position.File!)));
        string engine = typeof(DemoAnalysis).Assembly.GetName().Version?.ToString() ?? "";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{composed}\n{source}\n{engine}")));
    }
}
