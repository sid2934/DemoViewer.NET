#region

using System.Diagnostics;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The release gates, automated.
///     <list type="bullet">
///         <item>
///             <b>Installed-build smoke</b> — publishes the Desktop app to a directory OUTSIDE the
///             repo and proves the shipped rules land next to the binary and strict-load from there.
///             This is the test that would have caught the original finding (analysis
///             dead-on-arrival in installed builds). Opt-in via <c>RELEASE_GATES=1</c> (it runs
///             <c>dotnet publish</c>, ~1–2 min).
///         </item>
///         <item>
///             <b>User-rule journey</b> — the customization story end-to-end on a real demo: a user
///             file wholesale-overrides a shipped chain, adds a new chain, and a broken user file is
///             contained; the overridden/new columns land in the scoreboard VM and the CSV export.
///             Demo-gated (skips without a demo).
///         </item>
///     </list>
/// </summary>
[NotInParallel] // publish + full parse/eval are heavy; env-var mutation is process-wide
public class ReleaseGateTests
{
    // ── Gate 1: installed-build smoke ─────────────────────────────────────────

    /// <summary>Published build outside the repo carries loadable shipped rules next to the binary.</summary>
    [Test]
    public async Task InstalledBuild_ShipsLoadableRules()
    {
        if (Environment.GetEnvironmentVariable("RELEASE_GATES") != "1")
        {
            throw new SkipTestException("release gate — set RELEASE_GATES=1 to run (publishes the app, ~1–2 min)");
        }

        string repoRoot = FindRepoRoot();
        string publishDir = Path.Combine(Path.GetTempPath(), "demoviewer-release-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "dotnet",
                Arguments =
                    $"publish \"{Path.Combine(repoRoot, "src", "App", "DemoViewer.NET.Desktop", "DemoViewer.NET.Desktop.csproj")}\" "
                    + $"-c Release -o \"{publishDir}\"",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process publish = Process.Start(psi)!;
            string stdout = await publish.StandardOutput.ReadToEndAsync();
            string stderr = await publish.StandardError.ReadToEndAsync();
            await publish.WaitForExitAsync();
            await Assert.That(publish.ExitCode).IsEqualTo(0).Because($"publish failed:\n{stdout}\n{stderr}");

            // The packaged layout: rules/ + schema next to the binary, exactly where
            // RuleSetLocator.ResolveShippedRulesDirectory looks first. Post Rulesets v2 cutover the
            // shipped stats ship as v2 rulesets (*.rules.yaml), the v1 *.yaml files were removed.
            string rulesDir = Path.Combine(publishDir, "rules");
            await Assert.That(File.Exists(Path.Combine(rulesDir, "player_stats.rules.yaml"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(rulesDir, "kast.rules.yaml"))).IsTrue();
            // The v2 schema is what EnsureUserRulesDirectory copies into new user dirs —
            // if it stopped shipping, new users would silently lose editor validation.
            await Assert.That(File.Exists(Path.Combine(rulesDir, "cs2demokit-rules.schema.json"))).IsTrue();

            // Strict-load from the PUBLISHED location — the exact failure mode of the original
            // finding (DirectoryNotFoundException → dead Analysis tab) can never silently return.
            // The shipped tier hard-fails on any error, so a clean load proves the v2 rulesets
            // resolve from the packaged dir; kast ships as a v2 ruleset (not a v1 chain).
            RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(rulesDir);
            await Assert.That(loaded.Success).IsTrue();
            await Assert.That(loaded.Rulesets.Count).IsGreaterThan(0);
            await Assert.That(loaded.Rulesets.Select(r => r.Id)).Contains("kast");

            // The bundled sample demo, on the same terms as the rules: present in a publish that
            // did NOT route through scripts/publish.sh. TourDemoLocator resolves the first
            // assets/tour/*.dem by walking up from the binary, so an installer missing it has a
            // dead "Try a sample match" CTA and a first-run walkthrough with nothing to open —
            // a silent failure, since the locator degrades to null rather than throwing. Asserted
            // against the PUBLISHED tree (outside the repo) so the repo's own copy can't satisfy
            // it; the filename is deliberately not pinned, matching the locator.
            string tourDir = Path.Combine(publishDir, "assets", "tour");
            await Assert.That(Directory.Exists(tourDir)).IsTrue()
                .Because("assets/tour must ship from dotnet publish alone, not only via publish.sh");
            await Assert.That(Directory.EnumerateFiles(tourDir, "*.dem").Any()).IsTrue()
                .Because("the walkthrough and the Library sample CTA both resolve a .dem from here");

            // …and nothing else doc-shaped rides along. Both shipping paths copy assets/ by
            // wildcard — the Content glob above is `assets\tour\**`, and scripts/publish.sh does a
            // wholesale `cp -R assets "$OUT/assets"` — so any maintainer note dropped next to an
            // asset silently lands in every installer. That is how assets/tour/README.md (repo
            // paths, a commit SHA, and the reasoning about the private matchmaking demo) shipped
            // to end users; it now lives at docs/tour-sample-demo.md. Asserted over the whole
            // published assets/ tree, not just tour/, because the next one won't be in tour/.
            string publishedAssets = Path.Combine(publishDir, "assets");
            string[] strayDocs = Directory
                .EnumerateFiles(publishedAssets, "*.md", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(publishedAssets, "*.txt", SearchOption.AllDirectories))
                .Select(p => Path.GetRelativePath(publishedAssets, p))
                .ToArray();
            await Assert.That(strayDocs).IsEmpty()
                .Because($"maintainer docs must not ship inside assets/ — found: {string.Join(", ", strayDocs)}");
        }
        finally
        {
            try
            {
                Directory.Delete(publishDir, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    // ── Gate 2: user-rule journey ─────────────────────────────────────────────

    /// <summary>
    ///     User adds two rulesets + leaves a retired-v1 file behind → the new columns reach the
    ///     scoreboard and the export; the v1 file is contained with a loud, legible
    ///     retired-format error.
    /// </summary>
    [Test]
    public async Task UserRuleJourney_AddAndRetiredV1File_EndToEnd()
    {
        string demoPath = DemoTestHelper.RequireDemo();
        string repoRoot = FindRepoRoot();

        string userDir = Path.Combine(Path.GetTempPath(), "demoviewer-journey-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        try
        {
            // The user tier: two additive v2 rulesets with constant compute stats (deterministic
            // assertions), plus a leftover retired-v1 `chains:` file (the containment case — it
            // must error loudly and legibly without breaking the rest of the tier).
            File.WriteAllText(Path.Combine(userDir, "journey-check.rules.yaml"), """
                                                                                 ruleset: journey_check_rs
                                                                                 for: each_player
                                                                                 stats:
                                                                                   journey_check:
                                                                                     compute: "777"
                                                                                     per: match
                                                                                     format: F0
                                                                                 show:
                                                                                   scoreboard:
                                                                                     - { stat: journey_check, label: JourneyCheck }
                                                                                 """);
            File.WriteAllText(Path.Combine(userDir, "journey-new.rules.yaml"), """
                                                                               ruleset: journey_new
                                                                               for: each_player
                                                                               stats:
                                                                                 journey_new_stat:
                                                                                   compute: "888"
                                                                                   per: match
                                                                                   format: F0
                                                                               show:
                                                                                 scoreboard:
                                                                                   - { stat: journey_new_stat, label: JourneyNew }
                                                                                 tables:
                                                                                   journey_output:
                                                                                     per: player_match
                                                                                     columns:
                                                                                       - { stat: journey_new_stat, label: JourneyOut }
                                                                               """);
            File.WriteAllText(Path.Combine(userDir, "broken.yaml"),
                "chains:\n  - id: journey_broken\n    scope: per_player\n    rules:\n      - id: r1\n        type: bool\n");

            // Resolution plumbing: with both env overrides set, the locator points at exactly
            // these directories (the app's BuildFromConfig path resolves the same way).
            string shippedDir = Path.Combine(repoRoot, "rules");
            Environment.SetEnvironmentVariable(RuleSetLocator.RulesDirEnvVar, shippedDir);
            Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, userDir);
            try
            {
                await Assert.That(RuleSetLocator.ResolveShippedRulesDirectory()).IsEqualTo(shippedDir);
                await Assert.That(RuleSetLocator.GetUserRulesDirectory()).IsEqualTo(userDir);
            }
            finally
            {
                Environment.SetEnvironmentVariable(RuleSetLocator.RulesDirEnvVar, null);
                Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, null);
            }

            // Overlay load: the retired-v1 file is contained with a loud, legible error; the new
            // rulesets are in; the shipped tier is intact.
            RuleConfigLoadResult rules = YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
            await Assert.That(rules.Success).IsFalse();
            await Assert.That(rules.Errors.Single().Message).Contains("retired Rulesets v1 format")
                .Because("a pre-existing v1 overlay file must fail loudly, never silently");
            await Assert.That(rules.FailedFiles.Single()).Contains("broken.yaml");
            await Assert.That(rules.Rulesets.Select(r => r.Id)).Contains("journey_new");
            await Assert.That(rules.Rulesets.Select(r => r.Id)).Contains("journey_check_rs");

            // Full engine run on a real demo with the merged rulesets — exactly as the app's
            // BuildFromConfig path now does.
            ParsedDemo parsed = DemoTestHelper.GetOrParse(demoPath);
            BuildResult build = DemoAnalysis.Build(parsed, rules.Rulesets);
            AnalysisRun run = DemoAnalysis.Evaluate(parsed, build);

            // The scoreboard VM via the full event path (UpdateFromRun): the new columns are
            // present with constants delivered per player, AND the extra-table set carries the
            // user-declared output plus the shipped keyed weapon breakdowns.
            StatsTabViewModel vm = new(null, () => demoPath);
            vm.UpdateFromRun(run, parsed);

            await Assert.That(vm.HasStats).IsTrue();
            // User-authored columns land in the 'Other' category chip — select it,
            // exercising the category rail as part of the journey.
            await Assert.That(vm.Categories.Select(c => c.Group)).Contains(StatGroup.Other);
            vm.SelectedCategory = StatGroup.Other;
            IReadOnlyList<string> labels = vm.Columns.Select(c => c.Label).ToList();
            await Assert.That(labels).Contains("JourneyCheck");
            await Assert.That(labels).Contains("JourneyNew");

            int checkIdx = vm.Columns.Single(c => c.Label == "JourneyCheck").Index;
            int newIdx = vm.Columns.Single(c => c.Label == "JourneyNew").Index;
            foreach (StatsRow row in vm.GameRows)
            {
                await Assert.That(row.Cells[checkIdx].Display).IsEqualTo("777");
                await Assert.That(row.Cells[newIdx].Display).IsEqualTo("888");
            }

            // Table picker: the declared output and the shipped keyed tables are selectable, and
            // the generic renderer produces aligned rows for the declared output.
            IReadOnlyList<string> extraNames = vm.ExtraTables.Select(t => t.Name).ToList();
            await Assert.That(extraNames).Contains("journey_output");
            await Assert.That(extraNames).Contains("player_kills_by_weapon");
            vm.SelectedExtraTable = vm.ExtraTables.Single(t => t.Name == "journey_output");
            await Assert.That(vm.IsExtraTableView).IsTrue();
            await Assert.That(vm.ExtraColumns).Contains("JourneyOut");
            await Assert.That(vm.ExtraRows.Count).IsEqualTo(vm.GameRows.Count);
            await Assert.That(vm.ExtraRows.All(r => r[^1].StartsWith("888", StringComparison.Ordinal))).IsTrue();

            // And the export carries the same columns/values (one data path),
            // including the extra tables.
            string exportDir = Path.Combine(userDir, "export");
            Directory.CreateDirectory(exportDir);
            string message = vm.ExportTo(exportDir, "csv");
            await Assert.That(message).Contains("Exported");
            string csv = await File.ReadAllTextAsync(Path.Combine(exportDir, "player_game_stats.csv"));
            await Assert.That(csv).Contains("JourneyCheck");
            await Assert.That(csv).Contains("777");
            string outputCsv = await File.ReadAllTextAsync(Path.Combine(exportDir, "journey_output.csv"));
            await Assert.That(outputCsv).Contains("JourneyOut");
            await Assert.That(File.Exists(Path.Combine(exportDir, "player_kills_by_weapon.csv"))).IsTrue();
        }
        finally
        {
            try
            {
                Directory.Delete(userDir, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
