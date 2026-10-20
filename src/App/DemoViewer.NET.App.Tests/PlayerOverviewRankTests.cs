#region

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;
using DemoViewer.NET.Views.PlayerOverview;
using DemoViewer.NET.Views.PlayerOverview.Sections;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview's rank history and the history's rank column over an in-memory store: one track per
///     rank system, Competitive per map, the line oldest first, the latest badge, placement read as unranked,
///     and a record read again only when its <c>matchrank</c> stamp moves.
/// </summary>
public class PlayerOverviewRankTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    // Demo i of PlayerOverviewHistoryTests on the given map, with Grim's rank row stamped as the evaluator writes it.
    internal static DemoCacheRecord Ranked(int i, string map, MatchRankRow? rank, long computedAt = 1)
    {
        DemoCacheRecord record = PlayerOverviewHistoryTests.Demo(i);
        record.Map = map;
        record.MatchMeta!.GameMode = rank?.RankType == 11 ? "premier" : "competitive";
        if (rank is not null)
        {
            rank.SteamId64 = Id(Grim);
            record.MatchMeta.Players = [rank, new MatchRankRow { SteamId64 = "76561190000000012", RankType = rank.RankType, RankOld = 3 }];
        }

        record.SetStamp(new PackStamp(MatchMetaEvaluator.StampId, DemoMatchMeta.CurrentSchema, MatchMetaEvaluator.Version) { ComputedAtTicks = computedAt });
        return record;
    }

    private static MatchRankRow Premier(int old, int change) =>
        new() { RankType = 11, RankOld = old, RankNew = old + change, RankChange = change, Wins = 50, RankSource = RankSource.ServerRankUpdate };

    private static MatchRankRow Competitive(int old, int change) =>
        new() { RankType = 12, RankOld = old, RankNew = old + change, RankChange = change, Wins = 30, RankSource = RankSource.ServerRankUpdate };

    // Oldest first: premier 14000 → 14189 → 14100 → 14350, and competitive on Nuke and Mirage in between.
    internal static DemoCacheStore Library()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Ranked(0, "de_nuke", Premier(14000, 189)));
        store.Upsert(Ranked(1, "de_nuke", Competitive(9, 1)));
        store.Upsert(Ranked(2, "de_mirage", Premier(14189, -89)));
        store.Upsert(Ranked(3, "de_mirage", Competitive(12, -1)));
        store.Upsert(Ranked(4, "de_nuke", Competitive(10, 0)));
        store.Upsert(Ranked(5, "de_inferno", Premier(14100, 250)));
        store.Upsert(Ranked(6, "de_inferno", null));
        return store;
    }

    private sealed class Shell : IDisposable
    {
        public Shell(DemoCacheStore store, bool held = false)
        {
            if (held)
            {
                Gate.Reset();
            }

            Store = store;
            PlayerDirectory directory = new(store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            Identity.SetAccounts([new IdentityAccount(Id(Grim), null, DateTime.UtcNow)]);
            Ranks = new PlayerRankCache(store, directory, path =>
            {
                Gate.Wait();
                Reads.Enqueue(path);
                return store.TryLoadRecord(path, false)?.MatchMeta;
            });
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(directory, Identity, null,
                _ => { }, _ => { }, (_, _) => { }, _ => { }, Ranks: Ranks));
            Tab.OnActivated(null!);
        }

        public DemoCacheStore Store { get; }
        public IdentityService Identity { get; }
        public PlayerRankCache Ranks { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public ConcurrentQueue<string> Reads { get; } = new();

        // Holds every record read until set, for a test that acts while the read is running.
        public ManualResetEventSlim Gate { get; } = new(true);

        public RankSectionViewModel Rank => Tab.Sections.OfType<RankSectionViewModel>().Single();
        public HistorySectionViewModel History => Tab.Sections.OfType<HistorySectionViewModel>().Single();
        public MapsSectionViewModel Maps => Tab.Sections.OfType<MapsSectionViewModel>().Single();
        public HeaderSectionViewModel Header => Tab.Sections.OfType<HeaderSectionViewModel>().Single();

        public Task Settled() => Tab.Loading;

        public void Dispose()
        {
            Gate.Set();
            Tab.Dispose();
            Identity.Dispose();
            Gate.Dispose();
        }
    }

    [Test]
    public async Task Charts_OnePerRankSystem_PremierFirst()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        RankSectionViewModel rank = shell.Rank;

        using (Assert.Multiple())
        {
            await Assert.That(rank.IsVisible).IsTrue();
            await Assert.That(rank.Slot).IsEqualTo(SectionSlot.Rail);
            await Assert.That(rank.Charts.Select(c => c.Title)).IsEquivalentTo(["Premier", "Competitive"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task PremierChart_IsABarPerMatch_OldestFirst_OnAFloorUnderTheLowest()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        RankChartItem premier = shell.Rank.Charts[0];

        using (Assert.Multiple())
        {
            await Assert.That(premier.Values).IsEquivalentTo([14189.0, 14100.0, 14350.0], CollectionOrdering.Matching);
            await Assert.That((premier.Minimum, premier.Maximum)).IsEqualTo((13000.0, (double?) null));
            await Assert.That(premier.SummaryText).IsEqualTo("3 matches · latest 14,350 · peak 14,350 · +161");
            await Assert.That(premier.PointTips[0]).EndsWith("Nuke · Premier 14,000 → 14,189 (+189)");
        }
    }

    [Test]
    public async Task CompetitiveChart_SpansEveryMap_OnTheSkillGroupScale()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        RankChartItem competitive = shell.Rank.Charts[1];

        using (Assert.Multiple())
        {
            await Assert.That(competitive.Values).IsEquivalentTo([10.0, 11.0, 10.0], CollectionOrdering.Matching);
            await Assert.That((competitive.Minimum, competitive.Maximum)).IsEqualTo((0.0, (double?) 18));
            await Assert.That(competitive.PointTips[1]).Contains("Mirage");
            await Assert.That(competitive.SummaryText).IsEqualTo(
                $"3 matches · latest {GameIcons.IconCatalogue.RankName(10)} · peak {GameIcons.IconCatalogue.RankName(11)}");
        }
    }

    [Test]
    public async Task NameCard_ShowsTheLatestPremierAndCompetitive()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        IReadOnlyList<RankCardItem> ranks = shell.Header.Ranks;

        using (Assert.Multiple())
        {
            await Assert.That(ranks.Select(r => r.Title)).IsEquivalentTo(["PREMIER", "COMPETITIVE"], CollectionOrdering.Matching);
            await Assert.That((ranks[0].ValueText, ranks[0].ChangeText, ranks[0].IsUp)).IsEqualTo(("14,350", "+250", true));
            await Assert.That(ranks[0].BadgeKey).IsEqualTo(GameIcons.IconCatalogue.PremierEmblem(14350)!.Key);
            await Assert.That(ranks[0].Tooltip).StartsWith("Premier 14,100 → 14,350 (+250) · ");
            await Assert.That(ranks[0].Tooltip).EndsWith("Inferno");
            await Assert.That((ranks[1].ValueText, ranks[1].BadgeKey)).IsEqualTo((GameIcons.IconCatalogue.RankName(10), "rank/competitive/10"))
                .Because("the latest Competitive match was on Nuke, whatever Mirage's was");
        }
    }

    [Test]
    public async Task MapRows_CarryTheLatestCompetitiveRankOnEachMap()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        IReadOnlyList<MapRowItem> rows = shell.Maps.Rows;

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single(r => r.Map == "de_nuke").Rank!.BadgeKey).IsEqualTo("rank/competitive/10");
            await Assert.That(rows.Single(r => r.Map == "de_mirage").Rank!.BadgeKey).IsEqualTo("rank/competitive/11");
            await Assert.That(rows.Single(r => r.Map == "de_inferno").HasRank).IsFalse().Because("Inferno was only played in Premier");
            await Assert.That(rows.Single(r => r.Map == "de_nuke").Rank!.Tooltip).StartsWith(
                $"Latest Competitive rank on the map: {GameIcons.IconCatalogue.RankName(10)}");
        }
    }

    [Test]
    public async Task APlacementAccount_ReadsAsUnranked_NeverZero()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Ranked(0, "de_nuke", new MatchRankRow { RankType = 12, Wins = 4, RankSource = RankSource.ServerRankUpdate }));
        store.Upsert(Ranked(1, "de_mirage", new MatchRankRow { RankType = 11, RankSource = RankSource.Controller }));
        using Shell shell = new(store);
        await shell.Settled();
        RankCardItem premier = shell.Header.Ranks.Single(c => c.Title == "PREMIER");
        RankCardItem competitive = shell.Header.Ranks.Single(c => c.Title == "COMPETITIVE");
        RankChartItem chart = shell.Rank.Charts.Single(c => c.Title == "Competitive");

        using (Assert.Multiple())
        {
            await Assert.That((competitive.ValueText, competitive.BadgeKey)).IsEqualTo(("Unranked", "rank/competitive/needwins"));
            await Assert.That((premier.ValueText, premier.BadgeKey)).IsEqualTo(("Unranked", "premier/unranked"));
            await Assert.That((chart.HasBars, chart.SummaryText)).IsEqualTo((false, "1 match · unranked"));
            await Assert.That(shell.Maps.Rows.Single(r => r.Map == "de_nuke").Rank!.BadgeKey).IsEqualTo("rank/competitive/needwins");
            HistoryRowItem row = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/0.dem");
            await Assert.That(row.RankChangeText).IsEqualTo("–");
            await Assert.That(row.RankTip).IsEqualTo("Competitive unranked");
        }
    }

    [Test]
    public async Task ASubjectWithNoRankedDemo_HasNoSection()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Ranked(0, "de_nuke", null));
        store.Upsert(PlayerOverviewHistoryTests.Demo(1));
        using Shell shell = new(store);
        await shell.Settled();

        await Assert.That(shell.Rank.IsVisible).IsFalse();
        await Assert.That(shell.Rank.Charts).IsEmpty();
        await Assert.That(shell.Header.HasRanks).IsFalse();
        await Assert.That(shell.Reads.Count).IsEqualTo(1).Because("a demo without a matchrank stamp has nothing to read");
    }

    [Test]
    public async Task HistoryRows_CarryTheRankChange()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        HistoryRowItem up = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/5.dem");
        HistoryRowItem down = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/2.dem");
        HistoryRowItem none = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/6.dem");

        using (Assert.Multiple())
        {
            await Assert.That((up.RankChangeText, up.IsRankUp, up.IsRankDown)).IsEqualTo(("+250", true, false));
            await Assert.That(up.RankTip).IsEqualTo("Premier 14,100 → 14,350 (+250)");
            await Assert.That((down.RankChangeText, down.IsRankUp, down.IsRankDown)).IsEqualTo(("-89", false, true));
            await Assert.That((none.RankChangeText, none.RankTip)).IsEqualTo(("–", (string?) null));
        }
    }

    [Test]
    public async Task AFilterSetWhileRanksAreRead_NarrowsTheChartsNotTheNameCard_AndAClearReadsNothing()
    {
        using Shell shell = new(Library(), held: true);
        await Assert.That(shell.Rank.IsLoading).IsTrue();
        PlayerOverviewFilterViewModel filter = shell.Tab.Filter;
        filter.Map = filter.Maps.Single(c => c.Label == "Mirage");
        shell.Gate.Set();
        await shell.Settled();

        using (Assert.Multiple())
        {
            await Assert.That(shell.Rank.IsLoading).IsFalse();
            await Assert.That(shell.Rank.Charts.Select(c => c.Values.Single())).IsEquivalentTo([14100.0, 11.0], CollectionOrdering.Matching);
            await Assert.That(shell.Header.Ranks[0].ValueText).IsEqualTo("14,350").Because("a standing reads every demo, whatever the filter");
            await Assert.That(shell.History.Rows.Select(r => r.RankChangeText)).IsEquivalentTo(["-1", "-89"], CollectionOrdering.Matching);
        }

        filter.Map = filter.Maps[0];
        using (Assert.Multiple())
        {
            await Assert.That(shell.Rank.Charts.Sum(c => c.Values.Count)).IsEqualTo(6);
            await Assert.That(shell.History.Rows.Count).IsEqualTo(7);
            await Assert.That(shell.Reads.Count).IsEqualTo(7).Because("a filter change recomputes from the rows already read");
        }
    }

    [Test]
    public async Task ARecord_IsReadAgain_OnlyWhenItsRankStampMoves()
    {
        DemoCacheStore store = Library();
        using Shell shell = new(store);
        await shell.Settled();
        await Assert.That(shell.Reads.Count).IsEqualTo(7);

        // An unrelated write moves the index, not Grim's stamps.
        store.Upsert(PlayerOverviewHistoryTests.Demo(9));
        await shell.Settled();
        await Assert.That(shell.Reads.Count).IsEqualTo(7);

        store.Upsert(Ranked(5, "de_inferno", Premier(14100, 300), computedAt: 2));
        await shell.Settled();
        using (Assert.Multiple())
        {
            await Assert.That(shell.Reads.Count).IsEqualTo(8);
            await Assert.That(shell.Reads.Last()).IsEqualTo("/lib/5.dem");
            await Assert.That(shell.Header.Ranks[0].ValueText).IsEqualTo("14,400");
            await Assert.That(shell.Rank.Charts[0].Values[^1]).IsEqualTo(14400.0);
        }
    }
}

/// <summary>The rank history draws a bar chart per system, and the name card a badge per latest standing.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewRankRenderTests
{
    [Test]
    public async Task TheChartsAndTheNameCardBadges_Render()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = PlayerOverviewRankTests.Library();
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRankCache ranks = new(store, directory);
            await ranks.LoadAsync(PlayerOverviewHistoryTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, null,
                _ => { }, _ => { }, (_, _) => { }, _ => { }, Ranks: ranks));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewHistoryTests.Grim);

            Window window = new() { Width = 1300, Height = 900, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            RankSectionView view = window.GetVisualDescendants().OfType<RankSectionView>().Single();
            Sparkline[] bars = [.. view.GetVisualDescendants().OfType<Sparkline>().Where(l => l.IsVisible)];
            HeaderSectionView header = window.GetVisualDescendants().OfType<HeaderSectionView>().Single();
            GameIcon[] badges = [.. header.GetVisualDescendants().OfType<GameIcon>()];
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(bars.Length).IsEqualTo(2);
                await Assert.That(bars.All(b => b.Mode == SparklineMode.Bars && b.Bounds.Width > 0)).IsTrue();
                await Assert.That(badges.Select(b => b.Key)).IsEquivalentTo(
                    vm.Sections.OfType<HeaderSectionViewModel>().Single().Ranks.Select(r => r.BadgeKey), CollectionOrdering.Matching);
                await Assert.That(badges.All(b => b.Bounds.Width > 0)).IsTrue();
                await Assert.That(header.FindControl<Border>("AvatarPlaceholder")!.Bounds.Width).IsEqualTo(64.0);
            }

            window.Close();
        });
    }

    [Test]
    public async Task HistoryPage_PutsMapsBesideTheHistory_ScrollingSidewaysUntilBothFit()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = PlayerOverviewRankTests.Library();
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRankCache ranks = new(store, directory);
            await ranks.LoadAsync(PlayerOverviewHistoryTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, null,
                _ => { }, _ => { }, (_, _) => { }, _ => { }, Ranks: ranks));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewHistoryTests.Grim);
            vm.ShowPage(PlayerOverviewPageKind.History);

            Window window = new() { Width = 1600, Height = 900, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Settle();
            HistorySectionView history = window.GetVisualDescendants().OfType<HistorySectionView>().Single();
            MapsSectionView maps = window.GetVisualDescendants().OfType<MapsSectionView>().Single();
            ScrollViewer mapsScroll = maps.FindControl<ScrollViewer>("MapsScroll")!;
            ScrollViewer historyScroll = history.FindControl<ScrollViewer>("HistoryScroll")!;
            double mapsLeft = maps.TranslatePoint(new Point(0, 0), window)!.Value.X;
            double historyRight = history.TranslatePoint(new Point(history.Bounds.Width, 0), window)!.Value.X;
            double tops = Math.Abs(maps.TranslatePoint(new Point(0, 0), window)!.Value.Y - history.TranslatePoint(new Point(0, 0), window)!.Value.Y);
            using (Assert.Multiple())
            {
                await Assert.That(mapsLeft).IsGreaterThan(historyRight).Because("maps sits to the right of the history");
                await Assert.That(tops).IsLessThan(1.0).Because("both start at the top of the page");
                await Assert.That(mapsScroll.Extent.Width).IsGreaterThan(mapsScroll.Viewport.Width).Because("at 1,600 px the maps table scrolls");
            }

            window.Width = 2560;
            Settle();
            using (Assert.Multiple())
            {
                await Assert.That(mapsScroll.Extent.Width).IsLessThanOrEqualTo(mapsScroll.Viewport.Width + 0.5);
                await Assert.That(historyScroll.Extent.Width).IsLessThanOrEqualTo(historyScroll.Viewport.Width + 0.5);
                await Assert.That(historyScroll.Viewport.Width).IsGreaterThan(940.0).Because("with room, the table stretches rather than sitting at its minimum");
            }

            window.Width = 1200;
            Settle();
            await Assert.That(maps.TranslatePoint(new Point(0, 0), window)!.Value.Y)
                .IsGreaterThan(history.TranslatePoint(new Point(0, history.Bounds.Height), window)!.Value.Y - 0.5)
                .Because("narrow, maps stacks under the history");

            window.Close();

            static void Settle()
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    [Test]
    public async Task MapRankBadges_LineUpInTheirOwnColumn()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = PlayerOverviewRankTests.Library();
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRankCache ranks = new(store, directory);
            await ranks.LoadAsync(PlayerOverviewHistoryTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, null,
                _ => { }, _ => { }, (_, _) => { }, _ => { }, Ranks: ranks));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewHistoryTests.Grim);
            vm.ShowPage(PlayerOverviewPageKind.History);

            Window window = new() { Width = 1300, Height = 900, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            MapsSectionView view = window.GetVisualDescendants().OfType<MapsSectionView>().Single();
            double[] lefts = [.. view.GetVisualDescendants().OfType<GameIcon>().Where(i => i.Name == "MapRankBadge" && i.IsVisible)
                .Select(i => i.TranslatePoint(new Point(0, 0), view)!.Value.X)];
            using (Assert.Multiple())
            {
                await Assert.That(lefts.Length).IsEqualTo(2).Because("Nuke and Mirage have a Competitive rank; Inferno does not");
                await Assert.That(lefts.Distinct().Count()).IsEqualTo(1).Because("the badges sit in one column whatever the map name's width");
            }

            window.Close();
        });
    }
}
