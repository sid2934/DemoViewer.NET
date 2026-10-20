#region

using System.Collections.Concurrent;
using System.Globalization;
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
    public async Task Tracks_SplitByRankSystem_CompetitivePerMap_PremierFirst()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        RankSectionViewModel rank = shell.Rank;

        using (Assert.Multiple())
        {
            await Assert.That(rank.IsVisible).IsTrue();
            await Assert.That(rank.Column).IsEqualTo(SectionColumn.Side);
            await Assert.That(rank.Tracks.Select(t => t.Title))
                .IsEquivalentTo(["Premier", "Competitive · Nuke", "Competitive · Mirage"], CollectionOrdering.Matching)
                .Because("Premier first, then the per-map tracks played most recently first");
        }
    }

    [Test]
    public async Task PremierTrack_IsOldestFirst_WithTheLatestBadgeAndChange()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        RankTrackItem premier = shell.Rank.Tracks[0];

        using (Assert.Multiple())
        {
            await Assert.That(premier.Values).IsEquivalentTo([14189.0, 14100.0, 14350.0], CollectionOrdering.Matching);
            await Assert.That(premier.HasLine).IsTrue();
            await Assert.That(premier.LatestText).IsEqualTo("14,350");
            await Assert.That((premier.LatestChangeText, premier.IsUp, premier.IsDown)).IsEqualTo(("+250", true, false));
            await Assert.That(premier.BadgeKey).IsEqualTo(GameIcons.IconCatalogue.PremierEmblem(14350)!.Key);
            await Assert.That(premier.SummaryText).IsEqualTo("3 matches · peak 14,350");
            await Assert.That(premier.PointTips[0]).EndsWith("Nuke · Premier 14,000 → 14,189 (+189)");
        }
    }

    [Test]
    public async Task CompetitiveTrack_NamesTheSkillGroup_AndShowsItsBadge()
    {
        using Shell shell = new(Library());
        await shell.Settled();
        RankTrackItem nuke = shell.Rank.Tracks.Single(t => t.Title == "Competitive · Nuke");

        using (Assert.Multiple())
        {
            await Assert.That(nuke.Values).IsEquivalentTo([10.0, 10.0], CollectionOrdering.Matching);
            await Assert.That(nuke.LatestText).IsEqualTo(GameIcons.IconCatalogue.RankName(10));
            await Assert.That(nuke.BadgeKey).IsEqualTo("rank/competitive/10");
            await Assert.That((nuke.LatestChangeText, nuke.IsUp, nuke.IsDown)).IsEqualTo(("0", false, false));
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
        RankTrackItem competitive = shell.Rank.Tracks.Single(t => t.RankType == 12);
        RankTrackItem premier = shell.Rank.Tracks.Single(t => t.RankType == 11);

        using (Assert.Multiple())
        {
            await Assert.That((competitive.LatestText, competitive.BadgeKey)).IsEqualTo(("Unranked", "rank/competitive/needwins"));
            await Assert.That(competitive.Values).IsEmpty();
            await Assert.That(competitive.HasLine).IsFalse();
            await Assert.That((premier.LatestText, premier.BadgeKey)).IsEqualTo(("Unranked", "premier/unranked"));
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
        await Assert.That(shell.Rank.Tracks).IsEmpty();
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
    public async Task AFilterSetWhileRanksAreRead_ShapesWhatTheyLandOn_AndAClearReadsNothing()
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
            await Assert.That(shell.Rank.Tracks.Select(t => t.Title))
                .IsEquivalentTo(["Premier", "Competitive · Mirage"], CollectionOrdering.Matching);
            await Assert.That(shell.Rank.Tracks[0].Values).IsEquivalentTo([14100.0], CollectionOrdering.Matching);
            await Assert.That(shell.History.Rows.Select(r => r.RankChangeText)).IsEquivalentTo(["-1", "-89"], CollectionOrdering.Matching);
        }

        filter.Map = filter.Maps[0];
        using (Assert.Multiple())
        {
            await Assert.That(shell.Rank.Tracks.Count).IsEqualTo(3);
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
            await Assert.That(shell.Rank.Tracks[0].LatestText).IsEqualTo("14,400");
        }
    }
}

/// <summary>The rank history renders a badge and a line per track.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewRankRenderTests
{
    [Test]
    public async Task EachTrack_RendersItsBadgeAndLine()
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
            string?[] badges = [.. view.GetVisualDescendants().OfType<GameIcon>().Select(i => i.Key)];
            Sparkline[] lines = [.. view.GetVisualDescendants().OfType<Sparkline>().Where(l => l.IsVisible)];
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(badges).IsEquivalentTo(vm.Sections.OfType<RankSectionViewModel>().Single().Tracks.Select(t => t.BadgeKey),
                    CollectionOrdering.Matching);
                await Assert.That(lines.Length).IsEqualTo(2).Because("Mirage has one ranked match, no line to draw");
                await Assert.That(lines.All(l => l.Bounds.Width > 0)).IsTrue();
            }

            window.Close();
        });
    }
}
