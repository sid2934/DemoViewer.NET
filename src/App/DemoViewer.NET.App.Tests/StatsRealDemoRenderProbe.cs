#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Real-demo render probe for the Stats tab: full shipped-rules evaluation → scoreboard and
///     Rounds captures. Exists because the synthetic fixtures can't reproduce column-set-dependent
///     layout defects (the reported Rounds misalignment only manifests with the real ~20
///     round columns). Demo-gated.
/// </summary>
[Category("Probe")]
[NotInParallel]
[Category("RealDemo")]
public class StatsRealDemoRenderProbe
{
    /// <summary>Render scoreboard + rounds views from a real evaluation and save captures.</summary>
    [Test]
    public async Task RealDemo_ScoreboardAndRounds_Captures()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        // Post Rulesets v2 cutover the shipped scoreboard stats are v2 rulesets (in .Rulesets),
        // so build through the v2 overload — otherwise the rendered scoreboard is empty.
        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(RuleSetLocator.ResolveShippedRulesDirectory());
        BuildResult build = DemoAnalysis.Build(demo, loaded.Rulesets);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        await HeadlessSession.RunOnUi(async () =>
        {
            StatsTabViewModel vm = new(null, () => path);
            vm.UpdateFromRun(run, demo);

            StatsTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1400,
                Height = 700,
                Content = view
            };
            window.Show();

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? board = window.CaptureRenderedFrame();
            board!.Save(Path.Combine(HeadlessSession.ArtifactDir, "real-scoreboard.png"));

            vm.IsRoundView = true;
            vm.SelectedRound = vm.Rounds.Count > 5 ? vm.Rounds[5] : vm.Rounds[0];
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? rounds = window.CaptureRenderedFrame();
            rounds!.Save(Path.Combine(HeadlessSession.ArtifactDir, "real-rounds.png"));

            // Wide-category alignment check: the Damage chip carries the most round columns.
            vm.SelectedCategory = StatGroup.Damage;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap? damage = window.CaptureRenderedFrame();
            damage!.Save(Path.Combine(HeadlessSession.ArtifactDir, "real-rounds-damage.png"));

            Console.WriteLine("[damage round cols] " + string.Join(" | ", vm.RoundColumns.Select(c => c.Label)));
            Console.WriteLine("[damage row0] " + string.Join(" | ",
                vm.RoundRows[0].Cells.Select((c, i) => $"{vm.RoundColumns.ElementAtOrDefault(i)?.Label}={c.Display}")));
            // Header and rows must agree on the column count in every category (the misalignment regression).
            await Assert.That(vm.RoundRows[0].Cells.Count).IsEqualTo(vm.RoundColumns.Count);

            // Player-details overlay: open the top scoreboard
            // player and capture the Overview dashboard + Rounds sub-section with real columns.
            vm.ShowMatchViewCommand.Execute(null);
            vm.OpenPlayerDetailsCommand.Execute(vm.GameRows[0]);
            await Assert.That(vm.IsPlayerDetailsOpen).IsTrue();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap? details = window.CaptureRenderedFrame();
            details!.Save(Path.Combine(HeadlessSession.ArtifactDir, "real-player-details.png"));
            Console.WriteLine(
                $"[capture] {HeadlessSession.ArtifactDir}/real-player-details.png " +
                $"player={vm.PlayerDetails!.PlayerName} tiles={vm.PlayerDetails.CoreTiles.Count} " +
                $"weapons={vm.PlayerDetails.Weapons.Bars.Count} ach={vm.PlayerDetails.Achievements.Count} " +
                $"rounds={vm.PlayerDetails.RoundTableRows.Count}");

            vm.PlayerDetails?.Section = DetailSection.Rounds;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap? detailRounds = window.CaptureRenderedFrame();
            detailRounds!.Save(Path.Combine(HeadlessSession.ArtifactDir, "real-player-details-rounds.png"));
            vm.ClosePlayerDetailsCommand.Execute(null);

            Console.WriteLine($"[capture] {HeadlessSession.ArtifactDir}/real-scoreboard.png cols={vm.Columns.Count}");
            Console.WriteLine($"[capture] {HeadlessSession.ArtifactDir}/real-rounds.png cols={vm.RoundColumns.Count} rows={vm.RoundRows.Count}");
            Console.WriteLine("[round cols] " + string.Join(" | ", vm.RoundColumns.Select(c => $"{c.Label}:{c.Meta.Width}")));
            if (vm.RoundRows.Count > 0)
            {
                Console.WriteLine("[row0 cells] " + string.Join(" | ",
                    vm.RoundRows[0].Cells.Select(c => $"{c.Display}:{c.Width}")));
            }

            await Assert.That(vm.RoundRows.Count).IsGreaterThan(0);
        });
    }
}
