#region

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
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
///     The Player Overview's header and match history over an in-memory store: newest match first and stable
///     across a re-index, "stats pending" rather than zeros for a demo whose facet is not written, a deleted
///     demo gone until it is re-attached, the row actions and the header's account actions. The facet reader is
///     a counting fake; no demo file is touched.
/// </summary>
public class PlayerOverviewHistoryTests
{
    internal const ulong Grim = 76561190000000003;
    private const ulong Mate = 76561190000000012;
    private static readonly string Stamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);
    private static readonly DateTime Added = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    // Demo i is played on day i + 1, so a higher i is newer; Grim ends on CT on even demos and T on odd ones.
    internal static DemoCacheRecord Demo(int i, bool stamped = true, long? time = null, string? reservation = null)
    {
        DemoCacheRecord record = new()
        {
            Path = $"/lib/{i}.dem",
            Size = 100,
            ModifiedTicks = 1,
            Sha256 = $"sha{i}",
            Map = "de_nuke",
            Players =
            [
                new CachedPlayerInfo { Slot = 2, Name = i == 0 ? "Officer" : "grim", SteamId64 = Id(Grim), Team = i % 2 == 0 ? 3 : 2 },
                new CachedPlayerInfo { Slot = 3, Name = "mate", SteamId64 = Id(Mate), Team = 3 }
            ],
            MatchMeta = new DemoMatchMeta
            {
                MatchTimeUtcTicks = time ?? (i + 1) * TimeSpan.TicksPerDay,
                MatchTimeSource = MatchTimeSource.ValveMatchInfo,
                ReservationId = reservation
            }
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        if (stamped)
        {
            record.PackStamps.Add(new PackStamp(Stamp, StampedFacts.Schema, "fp") { ComputedAtTicks = 1 });
        }

        return record;
    }

    // Grim's one active row in demo i: 13-9 won on even demos, 9-13 lost on odd ones.
    internal static FactTable Facet(int i) => new(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match",
        ["player_slot", "steam_id", "team"], [], new Dictionary<string, string>(),
        [
            new FactRow(
                new Dictionary<string, FactValue> { ["player_slot"] = FactValue.Of(2L), ["steam_id"] = FactValue.Of(Id(Grim)), ["team"] = FactValue.Of(3L) },
                new Dictionary<string, FactValue>
                {
                    ["rounds"] = FactValue.Of(22L), ["TotalK"] = FactValue.Of(20L + i), ["TotalD"] = FactValue.Of(15L), ["TotalA"] = FactValue.Of(4L),
                    ["TotalHS"] = FactValue.Of(10L), ["TeamKills"] = FactValue.Of(0L), ["EnemyDmg"] = FactValue.Of(1870L),
                    ["KAST%"] = FactValue.Of(72.5), ["HLTV"] = FactValue.Of(1.17), ["Shots"] = FactValue.Of(300L),
                    ["CTW"] = FactValue.Of(i % 2 == 0 ? 13L : 9L), ["CTL"] = FactValue.Of(i % 2 == 0 ? 9L : 13L)
                })
        ]);

    internal static DemoCacheStore Library(int demos, Func<int, bool>? stamped = null)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < demos; i++)
            {
                store.Upsert(Demo(i, stamped?.Invoke(i) ?? true));
            }
        }

        return store;
    }

    internal static PlayerRowCache Cache(DemoCacheStore store, PlayerDirectory directory, ConcurrentQueue<string>? reads = null) =>
        new(store, directory, path =>
        {
            reads?.Enqueue(path);
            int i = int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);
            return store.TryGetIndex(path)?.Stamp(Stamp) is { State: DemoAnalysisState.Indexed } ? Facet(i) : null;
        });

    private sealed class Shell : IDisposable
    {
        public Shell(DemoCacheStore store, HashSet<string>? onDisk = null, params ulong[] me)
        {
            Store = store;
            Directory = new PlayerDirectory(store, onDisk is null ? _ => true : onDisk.Contains);
            Identity = new IdentityService(new SettingsService((string?)null));
            Identity.SetAccounts(me.Select(a => new IdentityAccount(Id(a), null, Added)));
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(Directory, Identity, Cache(store, Directory, Reads),
                Opened.Add, _ => { }, (path, id) => Focused.Add((path, id)), _ => { }));
            Tab.OnActivated(null!);
        }

        public DemoCacheStore Store { get; }
        public PlayerDirectory Directory { get; }
        public IdentityService Identity { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public ConcurrentQueue<string> Reads { get; } = new();
        public List<string> Opened { get; } = [];
        public List<(string Path, ulong Id)> Focused { get; } = [];

        public HeaderSectionViewModel Header => Tab.Reveal<HeaderSectionViewModel>();
        public HistorySectionViewModel History => Tab.Reveal<HistorySectionViewModel>();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    [Test]
    public async Task History_IsNewestMatchFirst_AndStableAcrossAReindex()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord[] demos =
        [
            Demo(0), Demo(1, time: 5 * TimeSpan.TicksPerDay, reservation: "100"), Demo(2, time: 5 * TimeSpan.TicksPerDay, reservation: "200"),
            Demo(3), Demo(4, time: 5 * TimeSpan.TicksPerDay)
        ];
        foreach (DemoCacheRecord demo in demos)
        {
            store.Upsert(demo);
        }

        using Shell shell = new(store, null, Grim);
        await shell.Tab.Loading;
        string[] order = [.. shell.History.Rows.Select(r => r.Match.Row.Path)];
        await Assert.That(order).IsEquivalentTo(["/lib/2.dem", "/lib/1.dem", "/lib/4.dem", "/lib/3.dem", "/lib/0.dem"], CollectionOrdering.Matching)
            .Because("one match time breaks by the higher reservation id, then the path");

        foreach (DemoCacheRecord demo in demos.Reverse())
        {
            store.Upsert(Demo(int.Parse(Path.GetFileNameWithoutExtension(demo.Path), CultureInfo.InvariantCulture),
                time: demo.MatchMeta!.MatchTimeUtcTicks, reservation: demo.MatchMeta.ReservationId));
        }

        await shell.Tab.Loading;
        await Assert.That(shell.History.Rows.Select(r => r.Match.Row.Path)).IsEquivalentTo(order, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARowWithNoFacet_ShowsStatsPending_NeverZero()
    {
        using Shell shell = new(Library(4, i => i != 1), null, Grim);
        await shell.Tab.Loading;
        HistoryRowItem pending = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/1.dem");
        HistoryRowItem won = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/2.dem");

        using (Assert.Multiple())
        {
            await Assert.That(shell.History.Rows.Count).IsEqualTo(4);
            await Assert.That(shell.History.CountText).IsEqualTo("4 matches");
            await Assert.That(pending.HasStats).IsFalse();
            await Assert.That(pending.StatsNote).IsEqualTo("stats pending");
            foreach (string cell in (string[]) [pending.ScoreText, pending.ResultText, pending.KdaText, pending.AdrText, pending.HsText, pending.KastText, pending.HltvText])
            {
                await Assert.That(cell).DoesNotContain("0").Because("a demo with no facet is pending, not zero");
            }

            await Assert.That(shell.Reads.Count).IsEqualTo(3).Because("only the written facets are read");
            await Assert.That(won.HasStats).IsTrue();
            await Assert.That((won.ScoreText, won.ResultText, won.IsWin, won.KdaText)).IsEqualTo(("13-9", "W", true, "22-15-4"));
            await Assert.That((won.AdrText, won.HsText, won.KastText, won.HltvText)).IsEqualTo(("85.0", "45%", "72.5%", "1.17"));
            await Assert.That(won.SideText).IsEqualTo("CT");
            await Assert.That(won.RankChangeText).IsEqualTo("–");
        }
    }

    [Test]
    public async Task OneRowsStats_EqualTheAggregateOfThatDemo()
    {
        using Shell shell = new(Library(3), null, Grim);
        await shell.Tab.Loading;
        PlayerRows rows = shell.Tab.Context!.Services.Rows!.TryGet(Grim)!;
        foreach (PlayerDemo demo in rows.Demos)
        {
            PlayerAggregate one = PlayerAggregate.Of([demo]);
            HistoryRowItem row = shell.History.Rows.Single(r => r.Match.Row.Path == demo.Match.Row.Path);
            await Assert.That(row.AdrText).IsEqualTo(one["ADR"]!.Value.ToString("0.0", CultureInfo.InvariantCulture));
            await Assert.That(row.HltvText).IsEqualTo(one["HLTV"]!.Value.ToString("0.00", CultureInfo.InvariantCulture));
        }
    }

    [Test]
    public async Task ADeletedDemo_LeavesTheHistory_AndReturnsOnReattach()
    {
        DemoCacheStore store = Library(3);
        using Shell shell = new(store, null, Grim);
        await shell.Tab.Loading;
        await Assert.That(shell.History.Rows.Count).IsEqualTo(3);

        store.Detach("/lib/1.dem");
        await shell.Tab.Loading;
        await Assert.That(shell.History.Rows.Select(r => r.Match.Row.Path)).DoesNotContain("/lib/1.dem");
        await Assert.That(shell.Header.DemoCountText).IsEqualTo("2 demos");

        await Assert.That(store.Reattach("/lib/1.dem", 100, 1)).IsTrue();
        await shell.Tab.Loading;
        await Assert.That(shell.History.Rows.Count).IsEqualTo(3);
        HistoryRowItem back = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/1.dem");
        await Assert.That(back.HasStats).IsTrue().Because("the demo's facet is still held under its content id");
    }

    [Test]
    public async Task RowActions_OpenTheFirstPathOnDisk_OrMarkTheFileMissing()
    {
        HashSet<string> onDisk = new(StringComparer.OrdinalIgnoreCase) { "/lib/0.dem" };
        using Shell shell = new(Library(2), onDisk, Grim);
        HistoryRowItem present = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/0.dem");
        HistoryRowItem missing = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/1.dem");

        shell.History.OpenDemoCommand.Execute(present);
        shell.History.FocusInStatsCommand.Execute(present);
        shell.History.OpenDemoCommand.Execute(missing);
        shell.History.FocusInStatsCommand.Execute(missing);

        using (Assert.Multiple())
        {
            await Assert.That(shell.Opened).IsEquivalentTo(["/lib/0.dem"]);
            await Assert.That(shell.Focused).IsEquivalentTo([("/lib/0.dem", Grim)]);
            await Assert.That(present.IsFileMissing).IsFalse();
            await Assert.That(missing.IsFileMissing).IsTrue();
        }
    }

    [Test]
    public async Task Chips_MarkAnApproximateTime_AndAPlayerWhoLeftEarly()
    {
        DemoCacheStore store = Library(1);
        DemoCacheRecord gone = Demo(1);
        gone.Players = [new CachedPlayerInfo { Slot = 3, Name = "mate", SteamId64 = Id(Mate), Team = 3 }];
        gone.FacetPlayers = [new FacetPlayer { SteamId64 = Id(Grim), Names = ["grim"], Slots = [2] }];
        gone.MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = 9 * TimeSpan.TicksPerDay, MatchTimeSource = MatchTimeSource.FileWriteTime };
        store.Upsert(gone);

        using Shell shell = new(store, null, Grim);
        HistoryRowItem exact = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/0.dem");
        HistoryRowItem left = shell.History.Rows.Single(r => r.Match.Row.Path == "/lib/1.dem");
        using (Assert.Multiple())
        {
            await Assert.That((exact.IsApproximateTime, exact.TimeChip, exact.LeftEarly, exact.SideText)).IsEqualTo((false, "exact", false, "CT"));
            await Assert.That((left.IsApproximateTime, left.TimeChip, left.LeftEarly, left.ShowSide)).IsEqualTo((true, "approx", true, false));
            await Assert.That(exact.DateText).IsEqualTo(new DateTime(TimeSpan.TicksPerDay, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }
    }

    [Test]
    public async Task Header_ShowsAliasesProfileAndSeen()
    {
        using Shell shell = new(Library(3), null, Grim);
        HeaderSectionViewModel header = shell.Header;
        using (Assert.Multiple())
        {
            await Assert.That(header.Name).IsEqualTo("grim");
            await Assert.That(header.Aliases).IsEqualTo("also Officer");
            await Assert.That(header.SteamId64).IsEqualTo(Id(Grim));
            await Assert.That(header.ProfileUrl).IsEqualTo("https://steamcommunity.com/profiles/" + Id(Grim));
            await Assert.That(header.DemoCountText).IsEqualTo("3 demos");
            await Assert.That(header.SeenText).IsEqualTo("first seen 0001-01-02 · last seen 0001-01-04");
            await Assert.That((header.IsMine, header.CanSetAsMe, header.CanSetActive)).IsEqualTo((true, false, false));
        }
    }

    [Test]
    public async Task Header_SetAsMe_AddsTheAccount_AndSetActive_MakesItActive()
    {
        using Shell shell = new(Library(2), null, Grim);
        shell.Tab.OpenPlayer(Mate);
        await Assert.That((shell.Header.IsMine, shell.Header.CanSetAsMe)).IsEqualTo((false, true));

        shell.Header.SetAsMeCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Identity.IsMine(Mate)).IsTrue();
            await Assert.That(shell.Identity.ActiveSteamId64).IsEqualTo(Id(Grim)).Because("the user already had an active account");
            await Assert.That(shell.Tab.SubjectSteamId64).IsEqualTo(Mate);
            await Assert.That((shell.Header.IsMine, shell.Header.CanSetAsMe, shell.Header.CanSetActive)).IsEqualTo((true, false, true));
        }

        shell.Header.SetActiveCommand.Execute(null);
        await Assert.That(shell.Identity.ActiveSteamId64).IsEqualTo(Id(Mate));
        await Assert.That(shell.Header.CanSetActive).IsFalse();
    }

    [Test]
    public async Task Header_SetAsMe_OnAnEmptyList_MakesTheAccountActive()
    {
        using Shell shell = new(Library(1), null);
        shell.Tab.OpenPlayer(Mate);
        shell.Header.SetAsMeCommand.Execute(null);
        await Assert.That(shell.Identity.ActiveSteamId64).IsEqualTo(Id(Mate));
    }
}

/// <summary>The history list realises only the rows in view, however long the history is.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewHistoryRenderTests
{
    [Test]
    public async Task AThousandRowHistory_RealisesOnlyTheViewport()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = PlayerOverviewHistoryTests.Library(1000, i => i % 3 != 0);
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRowCache cache = PlayerOverviewHistoryTests.Cache(store, directory);
            await cache.LoadAsync(PlayerOverviewHistoryTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, cache,
                _ => { }, _ => { }, (_, _) => { }, _ => { }));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewHistoryTests.Grim);
            HistorySectionViewModel history = vm.Reveal<HistorySectionViewModel>();
            await Assert.That(history.Rows.Count).IsEqualTo(1000);

            vm.ShowPage(PlayerOverviewPageKind.History);

            Window window = new() { Width = 1300, Height = 900, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            ListBox list = window.GetVisualDescendants().OfType<HistorySectionView>().Single()
                .GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "HistoryList");
            int realised = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            await Assert.That(realised).IsGreaterThan(0);
            await Assert.That(realised).IsLessThan(40).Because("a 520 px list of 26 px rows shows about twenty");

            list.ScrollIntoView(999);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            int afterScroll = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            await Assert.That(afterScroll).IsLessThan(40);
            await Assert.That(list.ContainerFromIndex(999)).IsNotNull();
            window.Close();
        });
    }
}
