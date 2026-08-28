#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.Analysis;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Work item 0.2 (rule-authoring plan): fire-count badges + the never-fired lint,
///     surfacing 0.1's always-on counters in the rule-diagnostics panel. A deliberately dead
///     user rule (its trigger event occurs, its condition never matches) must produce the
///     "fired 0 times" warning and a 0× badge, while shipped rules show nonzero badges.
///     The contract is pinned on the engine thread via the pure
///     <see cref="AnalysisViewModel.ComputeRuleDiagnostics" /> (no UI machinery — immune to the
///     suite-order UI-session wedge, see the quarantined shell test below).
/// </summary>
/// <remarks>
///     [NotInParallel]: full parse + evaluation of a real demo, plus process-wide env-var
///     mutation for the user-rules overlay (same rationale as ReleaseGateTests).
/// </remarks>
[NotInParallel]
[Category("Integration")]
public class RuleFireDiagnosticsTests
{
    private const string DeadRuleYaml =
        """
        ruleset: dead_rule_probe
        for: each_player
        stats:
          never_fires_probe:
            count: kill
            # Slots are 0..63, so this never matches — the stat compiles, dispatches, and
            # fires zero times (the never-fired lint's exact target).
            where: "event.Attacker == 99"
            per: match
        show:
          scoreboard:
            - { stat: never_fires_probe, label: NeverFiresProbe }
        """;

    /// <summary>
    ///     The work item's required assertions, driven entirely on the test thread:
    ///     load the shipped rules + a dead user rule, run the engine, and compute the
    ///     diagnostics/badges exactly as RunAsync does. player_death occurs in every real demo
    ///     (so the pre-existing absent-event warning stays silent — this pins the NEW lint),
    ///     while slot 99 never matches (so the rule fires zero times). Note 0.4a is
    ///     load-bearing: pre-fix, game-event conditions were honored but net-message ones were
    ///     not — the probe uses a game-event condition.
    /// </summary>
    [Test]
    public async Task DeadRule_ProducesNeverFiredLint_AndBadges_EngineThread()
    {
        string demoPath = DemoTestHelper.RequireDemo();
        string repoRoot = FindRepoRoot();
        string userDir = Path.Combine(Path.GetTempPath(),
            "demoviewer-firelint-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(userDir, "dead-rule.rules.yaml"), DeadRuleYaml);

            string shippedDir = Path.Combine(repoRoot, "rules");
            RuleConfigLoadResult rules = YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
            await Assert.That(rules.Success).IsTrue();

            ParsedDemo parsed = DemoTestHelper.GetOrParse(demoPath);
            BuildResult build = DemoAnalysis.Build(parsed, rules.Rulesets);
            AnalysisRun run = DemoAnalysis.Evaluate(parsed, build);
            await Assert.That(run.Snapshots).IsNotNull();

            (List<RuleDiagnostic> diags, List<RuleFireStat> fireStats) =
                AnalysisViewModel.ComputeRuleDiagnostics(rules, parsed, run.Build, run.Snapshots!);

            // The never-fired lint row — exactly one row for the probe rule (the absent-event
            // warning must NOT also fire: player_death is present in the demo).
            List<RuleDiagnostic> probeRows = diags.Where(d => d.RuleId == "never_fires_probe").ToList();
            await Assert.That(probeRows.Count).IsEqualTo(1);
            await Assert.That(probeRows[0].Severity).IsEqualTo("warning");
            await Assert.That(probeRows[0].ChainId).IsEqualTo("dead_rule_probe");
            await Assert.That(probeRows[0].Message).Contains("0 times");

            // Badges: the dead rule reads 0× and flags NeverFired; at least one shipped rule
            // fired on a real demo (nonzero badge proves the counter plumbing end to end).
            RuleFireStat dead = fireStats.Single(s => s.RuleId == "never_fires_probe");
            await Assert.That(dead.FireCount).IsEqualTo(0);
            await Assert.That(dead.NeverFired).IsTrue();
            await Assert.That(dead.CountLabel).IsEqualTo("0×");
            await Assert.That(fireStats.Any(s => s.FireCount > 0)).IsTrue();
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

    /// <summary>
    ///     Full-shell variant with the Skia render (house convention): drives
    ///     MainViewModel.LoadDemoFromPathAsync and captures the open diagnostics panel.
    ///     Verified green in isolation (capture: rule-fire-lint.png, 78 badges).
    /// </summary>
    [Test]
    public async Task DeadRule_FullShell_RendersPanel()
    {
        string demoPath = DemoTestHelper.RequireDemo();
        string repoRoot = FindRepoRoot();
        string userDir = Path.Combine(Path.GetTempPath(),
            "demoviewer-firelint-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(userDir, "dead-rule.rules.yaml"), DeadRuleYaml);
            Environment.SetEnvironmentVariable(RuleSetLocator.RulesDirEnvVar,
                Path.Combine(repoRoot, "rules"));
            Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, userDir);

            await HeadlessSession.RunOnUi(async () =>
            {
                MainViewModel vm = new(null, new ModuleRegistry(),
                    new DemoLibraryService(null,
                        Path.Combine(Path.GetTempPath(),
                            "dvlib_test_" + Guid.NewGuid().ToString("N") + ".json")));
                try
                {
                    // Wedge forensics (see the Skip reason): the last StatusText localizes the
                    // stuck stage if this ever hangs again after unskipping.
                    vm.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(MainViewModel.StatusText))
                        {
                            Console.WriteLine($"[deadrule] shell: {vm.StatusText}");
                        }
                    };
                    await vm.LoadDemoFromPathAsync(demoPath);

                    AnalysisViewModel analysis = vm.Analysis;
                    await Assert.That(analysis.StatusText).DoesNotContain("Analysis failed");
                    await Assert.That(analysis.RuleFireStats.Any(s => s.RuleId == "never_fires_probe")).IsTrue();
                    await Assert.That(analysis.HasDiagnosticsPanelContent).IsTrue();

                    analysis.IsDiagnosticsOpen = true;
                    AnalysisTabView view = new()
                    {
                        DataContext = vm
                    };
                    Window window = new()
                    {
                        Width = 1400,
                        Height = 800,
                        Content = view
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();

                    WriteableBitmap? frame = window.CaptureRenderedFrame();
                    await Assert.That(frame).IsNotNull();
                    string capturePath = Path.Combine(HeadlessSession.ArtifactDir, "rule-fire-lint.png");
                    frame!.Save(capturePath);
                    Console.WriteLine($"[capture] {capturePath}  badges={analysis.RuleFireStats.Count}");
                    await Assert.That(CountDistinctColors(frame)).IsGreaterThan(8);

                    window.Close();
                }
                finally
                {
                    vm.Analysis.Dispose(); // cancels the desktop entity-cache pre-warm replay
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuleSetLocator.RulesDirEnvVar, null);
            Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, null);
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

    private static int CountDistinctColors(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4]; // BGRA8888
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        HashSet<int> colors = new();
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            colors.Add(buffer[i] | buffer[i + 1] << 8 | buffer[i + 2] << 16);
            if (colors.Count > 64)
            {
                break; // enough evidence; no need to scan the whole frame
            }
        }

        return colors.Count;
    }
}
