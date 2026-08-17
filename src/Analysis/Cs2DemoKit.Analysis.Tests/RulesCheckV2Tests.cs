#region

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TUnit.Core.Exceptions;

#endregion

namespace Cs2DemoKit.Analysis.Tests;

/// <summary>
///     A black-box smoke of the <c>rules check</c> CLI over v2
///     <c>*.rules.yaml</c> documents. It runs the built <c>AnalysisBench</c> binary against temp
///     rule directories (demo-less — the fast tier), asserting a clean ruleset passes and a
///     bad-reference ruleset fails with a positioned <c>file(line,col)</c> diagnostic (the spec §8
///     contract). A build-order <c>ProjectReference</c> on AnalysisBench (in the .csproj,
///     <c>ReferenceOutputAssembly=false</c>) guarantees a FRESH binary before this runs — so it can
///     neither silently skip on a missing binary nor pass green against a stale one; the
///     <see cref="SkipTestException" /> below remains only as a defensive fallback.
/// </summary>
[Category("Integration")]
public class RulesCheckV2Tests
{
    /// <summary>The shipped pilot ruleset resolves clean through the CLI (0 errors, exit 0).</summary>
    [Test]
    public async Task RulesCheck_CleanV2Ruleset_Passes()
    {
        string pilot = File.ReadAllText(Path.Combine(FindRepoRoot(), "rules", "post_plant_double.rules.yaml"));
        (int exit, string output) = RunRulesCheck(("post_plant_double.rules.yaml", pilot));

        await Assert.That(exit).IsEqualTo(0).Because($"a clean v2 ruleset must pass rules check\n{output}");
        await Assert.That(output).Contains("0 error(s)");
    }

    /// <summary>A v2 ruleset with an unknown-name reference fails with a positioned §8 diagnostic (exit 1).</summary>
    [Test]
    public async Task RulesCheck_BadReferenceV2Ruleset_FailsWithPositionedDiagnostic()
    {
        const string Bad = """
                           ruleset: bad_ref
                           for: each_player
                           stats:
                             kills:
                               count: kill
                               per: round
                           highlights:
                             h:
                               when: nonexistent_stat >= 2
                               per: round
                               title: "x"
                           """;
        (int exit, string output) = RunRulesCheck(("bad.rules.yaml", Bad));

        await Assert.That(exit).IsEqualTo(1).Because($"a bad reference must fail rules check\n{output}");
        await Assert.That(output).Contains("nonexistent_stat")
            .Because("the diagnostic names what was written");
        await Assert.That(Regex.IsMatch(output, @"bad\.rules\.yaml\(\d+,\d+\):")).IsTrue()
            .Because($"the §8 diagnostic carries file(line,col)\n{output}");
    }

    /// <summary>A v2 ruleset whose when: slot is a non-bool expression fails with a typed §8 diagnostic.</summary>
    [Test]
    public async Task RulesCheck_WrongTypeV2Ruleset_FailsWithPositionedDiagnostic()
    {
        // `kills` is an int counter; a when: slot must be bool -> a type error at the highlight.
        const string BadType = """
                               ruleset: bad_type
                               for: each_player
                               stats:
                                 kills:
                                   count: kill
                                   per: round
                               highlights:
                                 h:
                                   when: kills
                                   per: round
                                   title: "x"
                               """;
        (int exit, string output) = RunRulesCheck(("bad_type.rules.yaml", BadType));

        await Assert.That(exit).IsEqualTo(1).Because($"a wrong-type when: must fail rules check\n{output}");
        await Assert.That(output).Contains("must be bool")
            .Because("the checker states the expected type in language terms");
        await Assert.That(Regex.IsMatch(output, @"bad_type\.rules\.yaml\(\d+,\d+\):")).IsTrue()
            .Because($"the §8 diagnostic carries file(line,col)\n{output}");
    }

    /// <summary>
    ///     Writes the files to a temp dir and runs <c>AnalysisBench rules check &lt;dir&gt;</c>, capturing (exit,
    ///     stdout+stderr).
    /// </summary>
    private static (int Exit, string Output) RunRulesCheck(params (string Name, string Content)[] files)
    {
        string dll = FindAnalysisBenchDll()
                     ?? throw new SkipTestException("AnalysisBench.dll not built — run: dotnet build tools/AnalysisBench");

        string dir = Path.Combine(Path.GetTempPath(), "rules_check_v2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach ((string name, string content) in files)
            {
                File.WriteAllText(Path.Combine(dir, name), content);
            }

            ProcessStartInfo psi = new("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("rules");
            psi.ArgumentList.Add("check");
            psi.ArgumentList.Add(dir);

            using Process process = Process.Start(psi)
                                    ?? throw new InvalidOperationException("failed to start dotnet");
            StringBuilder output = new();
            output.Append(process.StandardOutput.ReadToEnd());
            output.Append(process.StandardError.ReadToEnd());
            process.WaitForExit();
            return (process.ExitCode, output.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string? FindAnalysisBenchDll()
    {
        string root = Path.Combine(FindRepoRoot(), "artifacts", "bin", "AnalysisBench");
        if (!Directory.Exists(root))
        {
            return null;
        }

        // Prefer a build whose runtimeconfig.json sibling exists (the runnable output, not obj/ref copies).
        return Directory.EnumerateFiles(root, "AnalysisBench.dll", SearchOption.AllDirectories)
            .Where(p => File.Exists(Path.ChangeExtension(p, ".runtimeconfig.json")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string FindRepoRoot()
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

        throw new InvalidOperationException("repo root not found");
    }
}
