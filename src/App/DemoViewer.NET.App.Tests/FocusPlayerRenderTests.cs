#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Stats tab's "Open player overview" entry on screen: the row menu and the details header greyed out with
///     their reason for a player with no SteamID, and the shell opening the tab on the row's account.
/// </summary>
[NotInParallel]
[Category("Render")]
public class FocusPlayerRenderTests
{
    private const string Entry = "Open player overview";

    private static Window Show(StatsTabViewModel vm)
    {
        Window window = new()
        {
            Width = 1200,
            Height = 700,
            Content = new StatsTabView
            {
                DataContext = vm
            }
        };
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // The overview entry of the named player's scoreboard row, read off its opened context menu.
    private static MenuItem RowEntry(Window window, string player)
    {
        Border row = window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("statsRow") && b.DataContext is StatsRow { IsTotals: false } r && r.PlayerName == player);
        ContextMenu menu = row.ContextMenu!;
        menu.Open(row);
        Pump();
        MenuItem item = menu.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, Entry));
        menu.Close();
        return item;
    }

    [Test]
    public async Task RowMenu_GreysTheEntryOut_WithItsReason_ForANoSteamIdRow()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatsTabViewModel vm = FocusPlayerTests.Updated();
            Window window = Show(vm);
            try
            {
                MenuItem bob = RowEntry(window, "Bob");
                await Assert.That(bob.IsVisible).IsTrue();
                await Assert.That(bob.IsEnabled).IsFalse();
                await Assert.That(ToolTip.GetTip(bob)).IsEqualTo(PlayerOverviewLink.NoSteamIdTip);

                MenuItem alice = RowEntry(window, "Alice");
                await Assert.That(alice.IsEnabled).IsTrue();
                await Assert.That(ToolTip.GetTip(alice)).IsNull();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Test]
    public async Task DetailsHeader_OffersTheOverview_OnlyForAPlayerWithASteamId()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            List<(ulong, string?)> opened = [];
            StatsTabViewModel vm = FocusPlayerTests.Updated(opened);
            Window window = Show(vm);
            try
            {
                vm.OpenPlayerDetailsCommand.Execute(vm.GameRows.Single(r => r.PlayerName == "Bob"));
                Pump();
                Button button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Player overview"));
                await Assert.That(button.IsVisible).IsTrue();
                await Assert.That(button.IsEnabled).IsFalse();
                await Assert.That(ToolTip.GetTip(button)).IsEqualTo(PlayerOverviewLink.NoSteamIdTip);

                vm.FocusPlayer(FocusPlayerTests.Alice);
                Pump();
                button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Player overview"));
                await Assert.That(button.IsEnabled).IsTrue();
                button.Command!.Execute(button.CommandParameter);
                await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (FocusPlayerTests.Alice, "Alice") });
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Test]
    public async Task RowEntry_OpensTheOverviewTab_OnTheRowsAccount()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = new(new SettingsService((string?)null));
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(),
                players: new PlayerDirectory(new DemoCacheStore(null)), identity: identity);
            try
            {
                (EvaluationResult result, ParsedDemo demo, MetricTable coaches) = FocusPlayerTests.BuildScenario();
                vm.StatsTab.Update(result, demo, [coaches]);
                vm.StatsTab.OpenPlayerOverviewCommand.Execute(vm.StatsTab.GameRows.Single(r => r.PlayerName == "Bob"));
                await Assert.That(vm.PlayerOverviewTab!.SubjectSteamId64).IsNull();

                vm.StatsTab.OpenPlayerOverviewCommand.Execute(vm.StatsTab.GameRows.First(r => r.PlayerName == "Alice"));
                await Assert.That(vm.PlayerOverviewTab.SubjectSteamId64).IsEqualTo(FocusPlayerTests.Alice);
                await Assert.That(vm.PlayerOverviewTab.IsPinned).IsTrue();
                await Assert.That(vm.SelectedTab?.TabId).IsEqualTo(PlayerOverviewTabViewModel.TabId);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task ShellWithoutTheTab_HidesTheEntry()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty());
            try
            {
                (EvaluationResult result, ParsedDemo demo, MetricTable coaches) = FocusPlayerTests.BuildScenario();
                vm.StatsTab.Update(result, demo, [coaches]);
                await Assert.That(vm.StatsTab.GameRows.Any(r => r.ShowsPlayerOverview)).IsFalse();
                await Assert.That(vm.OpenPlayerOverview(FocusPlayerTests.Alice, "Alice")).IsFalse();
            }
            finally
            {
                vm.Dispose();
            }
        });
    }
}
