#region

using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Work item 0.3 (rule-authoring plan): the loader has always captured line/col for
///     YAML-syntax and unknown-key errors, but the App-side mapping into
///     <see cref="RuleDiagnostic" /> dropped both fields. Diagnostics rows showed the file
///     with no position, and click-to-open landed at line 1. These pin the restored thread:
///     <c>RuleConfigError → RuleDiagnostic.FromError → Location</c> rendering. Pure unit
///     tests: no Avalonia session, no demo file.
/// </summary>
[Category("Unit")]
public class RuleDiagnosticMappingTests
{
    /// <summary>The shared factory preserves every attribution field, position included.</summary>
    [Test]
    public async Task FromError_PreservesPosition()
    {
        RuleConfigError err = new("/tmp/x.yaml", "boom", "c1", "r1", 12, 3);

        RuleDiagnostic d = RuleDiagnostic.FromError(err);

        await Assert.That(d.Severity).IsEqualTo("error");
        await Assert.That(d.Message).IsEqualTo("boom");
        await Assert.That(d.FilePath).IsEqualTo("/tmp/x.yaml");
        await Assert.That(d.ChainId).IsEqualTo("c1");
        await Assert.That(d.RuleId).IsEqualTo("r1");
        await Assert.That(d.Line).IsEqualTo(12);
        await Assert.That(d.Column).IsEqualTo(3);
        await Assert.That(d.CanOpen).IsTrue();
    }

    /// <summary>Position renders inside the locator line, mirroring RuleConfigError's format.</summary>
    [Test]
    public async Task Location_IncludesPosition()
    {
        RuleDiagnostic d = new("error", "m", "/tmp/rules/x.yaml", "c1", null, 12, 3);

        await Assert.That(d.Location).IsEqualTo("x.yaml(12,3) · chain 'c1'");
    }

    /// <summary>
    ///     Positionless rows (semantic warnings, the info/warning lints) render exactly the
    ///     legacy form: no "(0,0)" noise.
    /// </summary>
    [Test]
    public async Task Location_OmitsPosition_WhenLineNull()
    {
        RuleDiagnostic d = new("warning", "m", "/tmp/rules/x.yaml", "c1");

        await Assert.That(d.Location).IsEqualTo("x.yaml · chain 'c1'");
    }

    /// <summary>Line without column renders a zero column, mirroring RuleConfigError.ToString().</summary>
    [Test]
    public async Task Location_ColumnDefaultsToZero_WhenOnlyLine()
    {
        RuleDiagnostic d = new("error", "m", "/tmp/x.yaml", null, null, 5);

        await Assert.That(d.Location).IsEqualTo("x.yaml(5,0)");
    }

    /// <summary>
    ///     The work item's required end-to-end assertion: a malformed fixture's position
    ///     surfaces through loader → RuleConfigLoadResult → diagnostic row, without booting
    ///     the UI.
    /// </summary>
    [Test]
    public async Task MalformedFixture_PositionSurfacesInDiagnosticRow()
    {
        // A v2 ruleset with a typo'd stat key: the mapper attributes the unknown key to its
        // YAML node, so the diagnostic carries a position.
        const string BadYaml = """
                               ruleset: c1
                               for: each_player
                               stats:
                                 kills:
                                   count: kill
                                   per: round
                                   trigers: nope
                               """;

        string dir = Directory.CreateTempSubdirectory("demoviewer-diag-test-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.yaml"), BadYaml);
            RuleConfigLoadResult result = YamlConfigLoader.TryLoadDirectory(dir);

            await Assert.That(result.Errors.Count).IsGreaterThanOrEqualTo(1);
            RuleDiagnostic row = RuleDiagnostic.FromError(result.Errors[0]);

            await Assert.That(row.Line).IsNotNull();
            await Assert.That(row.Location).Contains("bad.yaml(")
                .Because("the diagnostics panel must show where in the file the error sits");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
