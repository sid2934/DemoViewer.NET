#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.Views.Highlights;
using DemoViewer.NET.Views.Library;
using DemoViewer.NET.Views.MatchOverview;
using DemoViewer.NET.Views.Playback2D;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview entry on screen at the sites after Stats: Match Overview's roster and scoreboard menus, the
///     Add-clips picker row menu and the Library's player filter button. Each is greyed out with its reason for a row
///     with no SteamID and opens the row's account otherwise.
/// </summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewEntryPointRenderTests
{
    private const string Entry = "Open player overview";

    private static Window Show(Control view, double width = 1200, double height = 900)
    {
        Window window = new()
        {
            Width = width,
            Height = height,
            Content = view
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

    // The overview entry of a row's opened context menu.
    private static MenuItem Opened(Control row)
    {
        ContextMenu menu = row.ContextMenu!;
        menu.Open(row);
        Pump();
        MenuItem item = menu.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, Entry));
        menu.Close();
        return item;
    }

    private static async Task AssertEntry(MenuItem item, bool live)
    {
        await Assert.That(item.IsVisible).IsTrue();
        await Assert.That(item.IsEnabled).IsEqualTo(live);
        await Assert.That(ToolTip.GetTip(item)).IsEqualTo(live ? null : PlayerOverviewLink.NoSteamIdTip);
    }

    [Test]
    public async Task MatchOverview_RosterAndScoreboardMenus_GreyOutAPlayerWithNoSteamId()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            List<(ulong, string?)> opened = [];
            MatchOverviewTabViewModel vm = PlayerOverviewEntryPointTests.Overview(opened);
            Window window = Show(new MatchOverviewTabView
            {
                DataContext = vm
            });
            try
            {
                Control[] rows = [.. window.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.ContextMenu is not null && c.DataContext is OverviewPlayer or OverviewStatRow)];
                Control Row(string name) => rows.First(c => c.DataContext switch
                {
                    OverviewPlayer p => p.Name == name,
                    OverviewStatRow r => r.Name == name,
                    _ => false
                });
                Control[] Both(string name) => [.. rows.Where(c => c.DataContext switch
                {
                    OverviewPlayer p => p.Name == name,
                    OverviewStatRow r => r.Name == name,
                    _ => false
                })];

                await Assert.That(Both("Trin").Length).IsEqualTo(2).Because("a roster row and a scoreboard row");
                foreach (Control row in Both("Bob"))
                {
                    await AssertEntry(Opened(row), false);
                }

                foreach (Control row in Both("Trin"))
                {
                    await AssertEntry(Opened(row), true);
                }

                MenuItem trin = Opened(Row("Trin"));
                trin.Command!.Execute(null);
                await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (PlayerOverviewEntryPointTests.Trin, "Trin") }, CollectionOrdering.Matching);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Test]
    public async Task AddClipsPicker_RowMenu_OpensTheHighlightsPlayer()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            List<(ulong, string?)> opened = [];
            AddClipsPickerViewModel picker = PlayerOverviewEntryPointTests.Picker(opened);
            Window window = Show(new AddClipsPickerView
            {
                DataContext = picker
            });
            try
            {
                Border Row(string player) => window.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("pickRow") && b.DataContext is AddClipsRowViewModel r && r.PlayerName == player);

                await AssertEntry(Opened(Row("BOT Kirk")), false);
                MenuItem neo = Opened(Row("Neo"));
                await AssertEntry(neo, true);
                neo.Command!.Execute(null);
                await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (PlayerOverviewEntryPointTests.Neo, "Neo") }, CollectionOrdering.Matching);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Test]
    public async Task LibraryPlayerFilter_ShowsTheButton_OnlyForOneAccountAndOpensIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using DemoLibraryService svc = PlayerOverviewEntryPointTests.NewService();
            List<(ulong, string?)> opened = [];
            LibraryTabViewModel vm = PlayerOverviewEntryPointTests.Library(svc, opened);
            Window window = Show(new LibraryTabView
            {
                DataContext = vm
            });
            try
            {
                Button? Find() => window.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => Equals(b.Content, "Player overview"));

                await Assert.That(Find()?.IsVisible ?? false).IsFalse().Because("All players stands for nobody");

                vm.SelectedPlayer = "Smith";
                Pump();
                Button ambiguous = Find()!;
                await Assert.That(ambiguous.IsVisible).IsTrue();
                await Assert.That(ambiguous.IsEnabled).IsFalse();
                await Assert.That(ToolTip.GetTip(ambiguous)).IsEqualTo("2 accounts played under this name; open one from a demo");

                vm.SelectedPlayer = "Neo";
                Pump();
                Button neo = Find()!;
                await Assert.That(neo.IsEnabled).IsTrue();
                neo.Command!.Execute(null);
                await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (PlayerOverviewEntryPointTests.Neo, "Neo") }, CollectionOrdering.Matching);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Test]
    public async Task Playback2D_CardMenuAndFollowSubmenu_OpenTheRosterPlayer()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            List<(ulong, string?)> opened = [];
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab(
                configure: c => c.Roster[1] = new PlayerRosterEntry { Slot = 1, Name = "Bravo", SteamId = 0 },
                playerOverview: (id, name) => opened.Add((id, name)));
            ctx.Push(1, 2);
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            try
            {
                Control Card(string name) => view.GetVisualDescendants().OfType<Panel>()
                    .First(p => p.ContextMenu is not null && p.DataContext is PlayerAttributes a && a.Name == name);

                await AssertEntry(Opened(Card("Bravo")), false);
                MenuItem alpha = Opened(Card("Alpha"));
                await AssertEntry(alpha, true);
                alpha.Command!.Execute(null);

                view.FindControl<SplitButton>("ModeButton")!.Flyout!.ShowAt(view);
                Playback2DTimelineHarness.Pump();
                MenuItem submenu = view.FindControl<MenuItem>("PlayerOverviewMenuItem")!;
                MenuItem[] items = [.. ((IEnumerable<MenuItem>)submenu.ItemsSource!)];
                await Assert.That(submenu.IsVisible).IsTrue();
                await Assert.That(items.Select(i => i.IsEnabled)).IsEquivalentTo((bool[])[true, false, true], CollectionOrdering.Matching);
                await Assert.That(ToolTip.GetTip(items[1])).IsEqualTo(PlayerOverviewLink.NoSteamIdTip);
                items[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (1UL, "Alpha"), (3UL, "Charlie") }, CollectionOrdering.Matching);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
