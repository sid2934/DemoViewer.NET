#region

using System.Globalization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview entry on every site after Stats: Match Overview's roster and scoreboard, the Stats podium,
///     the Library's player filter and the Add-clips picker row. Each opens the row's own account, and a player with no
///     SteamID, a bot or a coach offers no action.
/// </summary>
[NotInParallel]
public class PlayerOverviewEntryPointTests
{
    internal const ulong Neo = 76561198000000011;
    internal const ulong Trin = 76561198000000012;
    internal const ulong Coach = 76561198000000013;
    internal const string Demo = "/demos/entry_de_nuke.dem";

    [Test]
    public async Task Entry_OpensItsAccount_AndANoAccountEntryDoesNothing()
    {
        List<(ulong, string?)> opened = [];
        PlayerOverviewEntry live = new(Neo, "Neo", (id, name) => opened.Add((id, name)));
        PlayerOverviewEntry none = new(0, "Bob", (id, name) => opened.Add((id, name)));

        live.Open();
        none.Open();
        none.OpenCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (Neo, "Neo") });
            await Assert.That(live.Tip).IsNull();
            await Assert.That(none.Tip).IsEqualTo(PlayerOverviewLink.NoSteamIdTip);
            await Assert.That(none.CanOpen).IsFalse();
            await Assert.That(PlayerOverviewEntry.None.Offers).IsFalse();
            await Assert.That(new PlayerOverviewEntry(Neo, "Neo", null).Offers).IsFalse();
        }
    }

    // Neo and Bob (no SteamID) on CT, a bot, a coach and Trin on T; the stored scoreboard has rows for four of them.
    internal static DemoCacheRecord Cached()
    {
        DemoCacheRecord r = new()
        {
            Path = Demo,
            Size = 10,
            ModifiedTicks = 20,
            Map = "de_nuke",
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 },
            Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 },
            AnalysisState = DemoAnalysisState.Indexed,
            CtScore = 13,
            TScore = 9
        };
        r.Players.Add(new CachedPlayerInfo { Slot = 0, Name = "Neo", SteamId64 = Neo.ToString(CultureInfo.InvariantCulture), Team = 3 });
        r.Players.Add(new CachedPlayerInfo { Slot = 1, Name = "Bob", SteamId64 = "0", Team = 3 });
        r.Players.Add(new CachedPlayerInfo { Slot = 2, Name = "BOT Kirk", SteamId64 = "0", Team = 2, IsBot = true });
        r.Players.Add(new CachedPlayerInfo { Slot = 3, Name = "Coach", SteamId64 = Coach.ToString(CultureInfo.InvariantCulture), Team = 2, IsCoach = true });
        r.Players.Add(new CachedPlayerInfo { Slot = 4, Name = "Trin", SteamId64 = Trin.ToString(CultureInfo.InvariantCulture), Team = 2 });
        r.Scoreboard =
        [
            new CachedStatRow { Slot = 0, Team = 3, Kills = 20, Adr = 90, Rating = 1.2 },
            new CachedStatRow { Slot = 1, Team = 3, Kills = 10, Adr = 70, Rating = 0.9 },
            new CachedStatRow { Slot = 3, Team = 2, Kills = 0, Adr = 0, Rating = 0 },
            new CachedStatRow { Slot = 4, Team = 2, Kills = 15, Adr = 80, Rating = 1.1 }
        ];
        return r;
    }

    internal static MatchOverviewTabViewModel Overview(List<(ulong, string?)> opened)
    {
        MatchOverviewTabViewModel vm = new()
        {
            PlayerOverviewOpener = (id, name) => opened.Add((id, name))
        };
        vm.SetCachedRecord(Cached());
        return vm;
    }

    [Test]
    public async Task MatchOverview_Roster_OffersTheAccount_OnlyForAHumanWithASteamId()
    {
        List<(ulong, string?)> opened = [];
        MatchOverviewTabViewModel vm = Overview(opened);

        OverviewPlayer[] roster = [.. vm.CounterTerrorists, .. vm.Terrorists];
        foreach (OverviewPlayer p in roster.Where(p => p.Name != "Neo" && p.Name != "Trin"))
        {
            await Assert.That(p.PlayerOverview.CanOpen).IsFalse().Because(p.Name);
            await Assert.That(p.PlayerOverview.Tip).IsEqualTo(PlayerOverviewLink.NoSteamIdTip).Because(p.Name);
            await Assert.That(p.PlayerOverview.Offers).IsTrue().Because(p.Name);
        }

        roster.Single(p => p.Name == "Trin").PlayerOverview.Open();
        await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (Trin, "Trin") });
    }

    [Test]
    public async Task MatchOverview_Scoreboard_RowsOpenTheirOwnAccount()
    {
        List<(ulong, string?)> opened = [];
        MatchOverviewTabViewModel vm = Overview(opened);

        foreach (OverviewStatRow row in vm.PlayerStats)
        {
            row.PlayerOverview.Open();
        }

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerStats.Count).IsEqualTo(4);
            await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (Neo, "Neo"), (Trin, "Trin") })
                .Because("Bob has no SteamID and the coach is no player");
            await Assert.That(vm.PlayerStats.Single(r => r.Name == "Coach").PlayerOverview.CanOpen).IsFalse();
        }
    }

    [Test]
    public async Task MatchOverview_WithoutAnOpener_HidesTheEntry()
    {
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(Cached());

        await Assert.That(vm.CounterTerrorists.Concat(vm.Terrorists).Any(p => p.PlayerOverview.Offers)).IsFalse();
        await Assert.That(vm.PlayerStats.Any(r => r.PlayerOverview.Offers)).IsFalse();
    }

    [Test]
    public async Task MatchOverview_LiveScoreboard_JoinsTheParsedRosterBySlot()
    {
        List<(ulong, string?)> opened = [];
        StatsTabViewModel stats = FocusPlayerTests.Updated();
        (_, ParsedDemo demo, _) = FocusPlayerTests.BuildScenario();
        MatchOverviewTabViewModel vm = new()
        {
            PlayerOverviewOpener = (id, name) => opened.Add((id, name))
        };
        vm.BeginOpening("entry_de_nuke.dem", "Nuke", "Server", Demo);
        vm.SetSummary(Demo, demo);
        vm.SetAnalysis(Demo, stats.GameTable, null, 13, stats.AccountsBySlot);

        foreach (OverviewStatRow row in vm.PlayerStats)
        {
            row.PlayerOverview.Open();
        }

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerStats.Count).IsGreaterThan(0);
            await Assert.That(opened.Select(o => o.Item1).Distinct()).IsEquivalentTo(new[] { FocusPlayerTests.Alice });
            await Assert.That(vm.PlayerStats.Single(r => r.Name == "Bob").PlayerOverview.CanOpen).IsFalse();
            await Assert.That(vm.CounterTerrorists.Concat(vm.Terrorists).Single(p => p.Name == "Coach").PlayerOverview.CanOpen).IsFalse()
                .Because("the analysis names the coach after the roster was filled");
        }
    }

    [Test]
    public async Task StatsPodium_CarriesTheAccount_OfEachPlacedPlayer()
    {
        List<(ulong, string?)> opened = [];
        StatsTabViewModel stats = FocusPlayerTests.Updated(opened);

        foreach (PodiumEntry entry in stats.Podium)
        {
            entry.PlayerOverview.Open();
        }

        using (Assert.Multiple())
        {
            await Assert.That(stats.Podium.Count).IsEqualTo(3);
            await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (FocusPlayerTests.Alice, "Alice") });
            await Assert.That(stats.Podium.Single(p => p.Name == "Bob").PlayerOverview.Tip).IsEqualTo(PlayerOverviewLink.NoSteamIdTip);
        }
    }

    internal static AddClipsPickerViewModel Picker(List<(ulong, string?)> opened)
    {
        DemoCacheRecord record = Cached();
        record.Highlights =
        [
            new CachedHighlightEvent { RulesetId = "r", HighlightId = "ace", PlayerSlot = 0, RoundNumber = 3, Tick = 1000 },
            new CachedHighlightEvent { RulesetId = "r", HighlightId = "ace", PlayerSlot = 2, RoundNumber = 4, Tick = 2000 }
        ];
        return new AddClipsPickerViewModel([record], 1, _ => false, _ => { }, _ => { }, () => { },
            playerOverviewOpener: (id, name) => opened.Add((id, name)));
    }

    [Test]
    public async Task AddClipsRow_OpensTheHighlightsPlayer_AndABotRowOffersNothing()
    {
        List<(ulong, string?)> opened = [];
        AddClipsPickerViewModel picker = Picker(opened);

        foreach (AddClipsRowViewModel row in picker.Rows)
        {
            row.PlayerOverview.Open();
        }

        using (Assert.Multiple())
        {
            await Assert.That(picker.Rows.Count).IsEqualTo(2);
            await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (Neo, "Neo") });
            await Assert.That(picker.Rows.Single(r => r.PlayerName == "BOT Kirk").PlayerOverview.CanOpen).IsFalse();
        }
    }

    internal static DemoEntry LibraryEntry(string file, params string[] players) => new()
    {
        FilePath = "/demos/" + file,
        FileName = file,
        Directory = "/demos",
        FileSizeBytes = 100_000_000,
        Modified = new DateTime(2026, 7, 1, 12, 0, 0),
        MapName = "de_nuke",
        ServerName = "srv",
        Players = [.. players],
        DurationSeconds = 3000,
        State = DemoIndexState.Indexed
    };

    internal static LibraryDemo Indexed(DemoEntry e, params LibrarySidePlayer[] ct) =>
        new(e.FilePath, e.FileName, e.MapName, e.Modified, e.FileSizeBytes) { CtPlayers = ct };

    // A library of four demos: Neo is one account throughout, Smith is two (Coach's slot account and Trin), and the
    // fourth demo has no side lists in the index.
    internal static LibraryTabViewModel Library(DemoLibraryService svc, List<(ulong, string?)> opened)
    {
        DemoEntry a = LibraryEntry("a.dem", "Neo", "Trin");
        DemoEntry b = LibraryEntry("b.dem", "Neo", "Smith");
        DemoEntry c = LibraryEntry("c.dem", "Smith");
        DemoEntry d = LibraryEntry("d.dem", "Smith");
        Dictionary<string, LibraryDemo> index = new()
        {
            [a.FilePath] = Indexed(a, new LibrarySidePlayer(Neo, "Neo"), new LibrarySidePlayer(Trin, "Trin")),
            [b.FilePath] = Indexed(b, new LibrarySidePlayer(Neo, "Neo"), new LibrarySidePlayer(Coach, "Smith")),
            [c.FilePath] = Indexed(c, new LibrarySidePlayer(Trin, "Smith")),
            [d.FilePath] = new LibraryDemo(d.FilePath, d.FileName, d.MapName, d.Modified, d.FileSizeBytes)
        };
        LibraryTabViewModel vm = new(svc, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]),
            findDemo: path => index.GetValueOrDefault(path))
        {
            PlayerOverviewOpener = (id, name) => opened.Add((id, name))
        };
        foreach (DemoEntry e in (DemoEntry[])[a, b, c, d])
        {
            svc.Entries.Add(e);
        }

        return vm;
    }

    internal static DemoLibraryService NewService() =>
        new(a => a(), Path.Combine(Path.GetTempPath(), "dvpoentry_" + Guid.NewGuid().ToString("N") + ".json"));

    [Test]
    public async Task Library_PlayerFilter_OpensTheAccountTheIndexJoinsToTheName()
    {
        using DemoLibraryService svc = NewService();
        List<(ulong, string?)> opened = [];
        LibraryTabViewModel vm = Library(svc, opened);

        await Assert.That(vm.SelectedPlayerOverview.Offers).IsFalse().Because("All players stands for nobody");

        vm.SelectedPlayer = "Neo";
        vm.SelectedPlayerOverview.Open();
        vm.SelectedPlayer = "Smith";
        PlayerOverviewEntry ambiguous = vm.SelectedPlayerOverview;
        ambiguous.Open();
        vm.SelectedPlayer = "Trin";
        vm.SelectedPlayerOverview.Open();

        using (Assert.Multiple())
        {
            await Assert.That(opened).IsEquivalentTo(new (ulong, string?)[] { (Neo, "Neo"), (Trin, "Trin") });
            await Assert.That(ambiguous.CanOpen).IsFalse();
            await Assert.That(ambiguous.Tip).Contains("2 accounts");
        }
    }
}
