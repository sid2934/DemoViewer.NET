#region

using CS2DemoKit.Analysis.Yaml;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Keeps the editor schema in <c>rules/</c> identical to the one embedded in
///     CS2DemoKit.Analysis.
///     <para>
///         The package is the authority: it generates the schema from its rules catalog and
///         extracts it into a user's rules directory. The copy in this repo exists so that editing
///         <c>rules/*.rules.yaml</c> in a normal editor gets validation from the sibling file the
///         <c>yaml-language-server</c> modeline points at. Two copies of a generated file drift,
///         and the drift is invisible — the editor simply validates against yesterday's rules.
///     </para>
///     <para>
///         To fix a failure, re-extract rather than hand-edit:
///         <c>YamlConfigLoader.ExtractShippedTo(dir)</c> writes the package's copy, which is the one
///         that is right by construction.
///     </para>
/// </summary>
public class ShippedSchemaDriftTests
{
    private const string SchemaFileName = "cs2demokit-rules.schema.json";

    /// <summary>The repo copy matches the package's byte for byte.</summary>
    [Test]
    public async Task RepoSchema_MatchesThePackagesEmbeddedCopy()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            throw new SkipTestException("not running from a repo checkout");
        }

        string repoSchema = Path.Combine(repoRoot, "rules", SchemaFileName);
        await Assert.That(File.Exists(repoSchema)).IsTrue()
            .Because($"{SchemaFileName} backs editor validation for the rulesets in rules/");

        string extractDir = Directory.CreateTempSubdirectory("schema-drift-").FullName;
        try
        {
            YamlConfigLoader.ExtractShippedTo(extractDir);
            string packaged = Path.Combine(extractDir, SchemaFileName);

            await Assert.That(File.Exists(packaged)).IsTrue()
                .Because("the package must still ship the schema it tells consumers to validate against");

            string committed = await File.ReadAllTextAsync(repoSchema);

            // Checked BEFORE the exact compare, which is against a file the package writes with LF:
            // on a CRLF checkout that compare fails with nothing having drifted, and its message
            // would send a contributor to re-extract an already-correct file. Separating the two
            // says which of the two actually happened.
            await Assert.That(committed).DoesNotContain("\r")
                .Because($"rules/{SchemaFileName} is stored eol=lf (see .gitattributes) — a CRLF "
                         + "checkout is not drift: re-clone, or run git add --renormalize .");

            await Assert.That(committed)
                .IsEqualTo(await File.ReadAllTextAsync(packaged))
                .Because($"rules/{SchemaFileName} has drifted from the package — re-extract it, do not hand-edit");
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
