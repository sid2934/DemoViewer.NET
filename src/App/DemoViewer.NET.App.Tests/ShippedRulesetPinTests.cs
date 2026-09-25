#region

using CS2DemoKit.Analysis.Yaml;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Keeps the four shipped rulesets mirrored in <c>rules/</c> identical to the ones embedded in
///     CS2DemoKit.Analysis, as <see cref="ShippedSchemaDriftTests" /> does for the schema.
///     <para>
///         The rulesets move with the engine: 0.13 changed what <c>round_won</c> fires for, and the
///         package's <c>player_stats</c> changed with it to count <c>round_lost</c> and
///         <c>round_ended</c>. A repo copy left on the old text reads zero losses and counts only won
///         rounds as survived, and nothing else would say so. Fix a failure by re-extracting with
///         <c>YamlConfigLoader.ExtractShippedTo(dir)</c>, never by hand-editing.
///     </para>
/// </summary>
public class ShippedRulesetPinTests
{
    [Test]
    [Arguments("kast.rules.yaml")]
    [Arguments("player_stats.rules.yaml")]
    [Arguments("post_plant_double.rules.yaml")]
    [Arguments("weapon_stats.rules.yaml")]
    public async Task RepoRuleset_MatchesThePackagesEmbeddedCopy(string fileName)
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            throw new SkipTestException("not running from a repo checkout");
        }

        string repoFile = Path.Combine(repoRoot, "rules", fileName);
        await Assert.That(File.Exists(repoFile)).IsTrue()
            .Because($"rules/{fileName} mirrors a ruleset the package ships");

        string extractDir = Directory.CreateTempSubdirectory("ruleset-pin-").FullName;
        try
        {
            YamlConfigLoader.ExtractShippedTo(extractDir);
            string packaged = Path.Combine(extractDir, fileName);

            await Assert.That(File.Exists(packaged)).IsTrue()
                .Because($"the package no longer ships {fileName}; drop the mirror or this row");

            // The index stores these eol=lf, but an older Windows checkout can still hold CRLF in the
            // working tree. That is not drift, so it is normalised away rather than failed on.
            string committed = (await File.ReadAllTextAsync(repoFile)).Replace("\r\n", "\n");

            await Assert.That(committed)
                .IsEqualTo(await File.ReadAllTextAsync(packaged))
                .Because($"rules/{fileName} has drifted from the package; re-extract it, do not hand-edit");
        }
        finally
        {
            Directory.Delete(extractDir, true);
        }
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
