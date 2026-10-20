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
///     The Player Overview's head-to-head over an in-memory store: who the subject met, the demos they met in on
///     opposite sides, the record and the kills each way, a demo whose <c>player_event</c> facet is not written
///     shown as pending, and another opponent over the same demos read from memory. The facet readers are fakes.
/// </summary>
public class HeadToHeadTests
{
    internal const ulong Grim = 76561190000000003;
    private const ulong Rival = 76561190000000007;
    private const ulong Mate = 76561190000000012;
    private const ulong Stranger = 76561190000000020;
    private static readonly string MatchStamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);
    private static readonly string EventStamp = StampedFacts.StampId(PlayerEventFacet.RulesetId);

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    // Demo i on day i + 1. Grim (slot 2) and Mate (slot 3) are CT, Rival (slot 7) T; in demo 3 Rival is on Grim's side,
    // and demo 4 has Stranger in Rival's place.
    private static DemoCacheRecord Demo(int i, bool events = true)
    {
        DemoCacheRecord record = new()
        {
            Path = $"/lib/{i}.dem",
            Size = 100,
            ModifiedTicks = 1,
            Sha256 = $"sha{i}",
            Map = i % 2 == 0 ? "de_nuke" : "de_inferno",
            Players =
            [
                new CachedPlayerInfo { Slot = 2, Name = "grim", SteamId64 = Id(Grim), Team = 3 },
                new CachedPlayerInfo { Slot = 3, Name = "mate", SteamId64 = Id(Mate), Team = 3 },
                i == 4
                    ? new CachedPlayerInfo { Slot = 7, Name = "stranger", SteamId64 = Id(Stranger), Team = 2 }
                    : new CachedPlayerInfo { Slot = 7, Name = "rival", SteamId64 = Id(Rival), Team = i == 3 ? 3 : 2 }
            ],
            MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = (i + 1) * TimeSpan.TicksPerDay, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        record.PackStamps.Add(new PackStamp(MatchStamp, StampedFacts.Schema, "fp") { ComputedAtTicks = 1 });
        if (events)
        {
            record.PackStamps.Add(new PackStamp(EventStamp, StampedFacts.Schema, "fp-events") { ComputedAtTicks = 1 });
        }

        return record;
    }

    // Grim wins even demos 13-9 and loses odd ones.
    internal static FactTable Facet(int i) => new(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match",
        ["player_slot", "steam_id", "team"], [], new Dictionary<string, string>(),
        [
            new FactRow(
                new Dictionary<string, FactValue> { ["player_slot"] = FactValue.Of(2L), ["steam_id"] = FactValue.Of(Id(Grim)), ["team"] = FactValue.Of(3L) },
                new Dictionary<string, FactValue>
                {
                    ["rounds"] = FactValue.Of(22L), ["TotalK"] = FactValue.Of(20L), ["TotalD"] = FactValue.Of(15L), ["Shots"] = FactValue.Of(300L),
                    ["CTW"] = FactValue.Of(i % 2 == 0 ? 13L : 9L), ["CTL"] = FactValue.Of(i % 2 == 0 ? 9L : 13L)
                })
        ]);

    private static FactRow Row(long tick, long slot, ulong steamId, string column, long other, bool headshot = false)
    {
        Dictionary<string, FactValue> values = new() { [column] = FactValue.Of(other) };
        if (column == "victim_slot")
        {
            values["weapon"] = FactValue.Of("ak47");
            values["headshot"] = FactValue.Of(headshot ? 1L : 0L);
        }

        return new FactRow(
            new Dictionary<string, FactValue>
            {
                ["round_number"] = FactValue.Of(1L),
                ["tick"] = FactValue.Of(tick),
                ["player_slot"] = FactValue.Of(slot),
                ["steam_id"] = FactValue.Of(Id(steamId))
            },
            values);
    }

    // In demo i Grim kills slot 7 twice (one headshot) and slot 7 kills Grim once; Mate kills slot 7 once.
    internal static FactTable Events(int i, FactKey key)
    {
        ulong seven = i == 4 ? Stranger : Rival;
        (long Tick, long Killer, ulong KillerId, long Victim, ulong VictimId, bool Headshot)[] kills =
        [
            (100, 2, Grim, 7, seven, true),
            (200, 7, seven, 2, Grim, false),
            (300, 2, Grim, 7, seven, false),
            (400, 3, Mate, 7, seven, false)
        ];
        List<FactRow> rows =
        [
            .. kills.Select(k => key == PlayerEventFacet.KillsKey
                ? Row(k.Tick, k.Killer, k.KillerId, "victim_slot", k.Victim, k.Headshot)
                : Row(k.Tick, k.Victim, k.VictimId, "attacker_slot", k.Killer))
        ];
        return new FactTable(key, "fp-events", StampedFacts.Schema, "player_event", ["round_number", "tick", "player_slot", "steam_id"], [],
            new Dictionary<string, string>(), rows);
    }

    internal static DemoCacheStore Library(Func<int, bool>? events = null)
    {
        DemoCacheStore store = new(null);
        for (int i = 0; i < 5; i++)
        {
            store.Upsert(Demo(i, events?.Invoke(i) ?? true));
        }

        return store;
    }

    internal static int IndexOf(string path) => int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);

    private sealed class Rig : IDisposable
    {
        public Rig(DemoCacheStore store)
        {
            Store = store;
            Directory = new PlayerDirectory(store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            PlayerRowCache rows = new(store, Directory, path => Facet(IndexOf(path)));
            Services = new PlayerOverviewServices(Directory, Identity, rows, _ => { }, _ => { }, (_, _) => { }, _ => { })
            {
                Events = new PlayerEventCache((path, key) =>
                {
                    Reads.Enqueue(path);
                    return store.TryGetIndex(path)?.Stamp(EventStamp) is { State: DemoAnalysisState.Indexed } ? Events(IndexOf(path), key) : null;
                })
            };
            Section = new HeadToHeadSectionViewModel(Services);
        }

        public DemoCacheStore Store { get; }
        public PlayerDirectory Directory { get; }
        public IdentityService Identity { get; }
        public PlayerOverviewServices Services { get; }
        public HeadToHeadSectionViewModel Section { get; }
        public ConcurrentQueue<string> Reads { get; } = new();

        public PlayerOverviewContext Context(ulong subject)
        {
            IReadOnlyList<PlayerMatch> matches = Directory.Matches(subject);
            PlayerDemo[] demos = [.. matches.Select(m => new PlayerDemo(m, false, null))];
            return new PlayerOverviewContext(subject, Directory.Get(subject), matches, 0, false, Services, demos, PlayerFilter.All, demos,
                false, null, false);
        }

        public void Dispose() => Identity.Dispose();
    }

    [Test]
    public async Task Opponents_AreTheOtherSide_MostOftenMetFirst()
    {
        PlayerDirectory directory = new(Library(), _ => true);
        IReadOnlyList<PlayerOpponent> opponents = HeadToHead.Opponents(Grim, directory.Matches(Grim));

        using (Assert.Multiple())
        {
            await Assert.That(opponents.Select(o => o.SteamId64)).IsEquivalentTo([Rival, Stranger], CollectionOrdering.Matching)
                .Because("a teammate is never an opponent, and Rival was on Grim's side in one demo");
            await Assert.That(opponents[0].Matches).IsEqualTo(3);
            await Assert.That(opponents[0].Name).IsEqualTo("rival");
            await Assert.That(opponents[1].Matches).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Against_IsTheDemosOnOppositeSides_NewestFirst()
    {
        PlayerDirectory directory = new(Library(), _ => true);
        IReadOnlyList<PlayerMatch> against = HeadToHead.Against(directory.Matches(Grim), directory.Matches(Rival));

        await Assert.That(against.Select(m => m.Row.Path)).IsEquivalentTo(["/lib/2.dem", "/lib/1.dem", "/lib/0.dem"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Of_CountsTheKillsEachWay_AndTheRecord()
    {
        PlayerDirectory directory = new(Library(), _ => true);
        IReadOnlyList<PlayerMatch> against = HeadToHead.Against(directory.Matches(Grim), directory.Matches(Rival));
        Dictionary<string, IReadOnlyList<PlayerKill>> kills = new(StringComparer.Ordinal)
        {
            ["sha0"] = PlayerEventFacet.Kills(Events(0, PlayerEventFacet.KillsKey).Rows, Events(0, PlayerEventFacet.DeathsKey).Rows),
            ["sha1"] = PlayerEventFacet.Kills(Events(1, PlayerEventFacet.KillsKey).Rows, Events(1, PlayerEventFacet.DeathsKey).Rows)
        };

        HeadToHead result = HeadToHead.Of(Grim, Rival, against, m => IndexOf(m.Row.Path) % 2 == 0 ? MatchOutcome.Win : MatchOutcome.Loss, kills);

        using (Assert.Multiple())
        {
            await Assert.That((result.Wins, result.Losses, result.Ties, result.WithOutcome)).IsEqualTo((2, 1, 0, 3));
            await Assert.That(result.WithKills).IsEqualTo(2).Because("demo 2 has no kills read");
            await Assert.That((result.Kills, result.Deaths, result.HeadshotKills)).IsEqualTo((4, 2, 2))
                .Because("Mate's kill on Rival is not Grim's");
            await Assert.That(result.Matches.Single(m => m.Match.Row.Path == "/lib/2.dem").HasKills).IsFalse();
        }
    }

    [Test]
    public async Task TheSection_PicksTheMostMetOpponent_AndShowsTheirRecord()
    {
        using Rig rig = new(Library(i => i != 2));
        rig.Section.Update(rig.Context(Grim));
        await rig.Section.Loading;

        HeadToHeadRowItem pending = rig.Section.Rows.Single(r => r.Match.Match.Row.Path == "/lib/2.dem");
        HeadToHeadRowItem won = rig.Section.Rows.Single(r => r.Match.Match.Row.Path == "/lib/0.dem");
        using (Assert.Multiple())
        {
            await Assert.That(rig.Section.IsVisible).IsTrue();
            await Assert.That(rig.Section.Opponents.Select(o => o.Title)).IsEquivalentTo(["rival (3 matches)", "stranger (1 match)"],
                CollectionOrdering.Matching);
            await Assert.That(rig.Section.SelectedOpponent?.SteamId64).IsEqualTo(Rival);
            await Assert.That(rig.Section.CountText).IsEqualTo("3 matches against");
            await Assert.That(rig.Section.RecordText).IsEqualTo("2-1");
            await Assert.That((rig.Section.KillsText, rig.Section.DeathsText, rig.Section.KdText, rig.Section.HsText))
                .IsEqualTo(("4", "2", "2.00", "50%"));
            await Assert.That(rig.Section.NoteText).IsEqualTo("Kills from 2 of 3 matches; the library is computing the rest");
            await Assert.That((pending.HasKills, pending.KillsNote)).IsEqualTo((false, "pending"));
            await Assert.That((won.HasKills, won.KillsText, won.ResultText, won.IsWin)).IsEqualTo((true, "2-1", "W", true));
            await Assert.That(rig.Reads.Distinct().Order()).IsEquivalentTo(["/lib/0.dem", "/lib/1.dem"], CollectionOrdering.Matching)
                .Because("a demo whose facet is not written is never read");
        }
    }

    [Test]
    public async Task AnotherOpponent_OverDemosAlreadyRead_ReadsNothingAgain()
    {
        using Rig rig = new(Library());
        rig.Section.Update(rig.Context(Grim));
        await rig.Section.Loading;
        int first = rig.Reads.Count;

        rig.Section.SelectedOpponent = rig.Section.Opponents.Single(o => o.SteamId64 == Stranger);
        await rig.Section.Loading;
        string stranger = rig.Section.KillsText;
        int afterStranger = rig.Reads.Count;

        rig.Section.SelectedOpponent = rig.Section.Opponents.Single(o => o.SteamId64 == Rival);
        await rig.Section.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(first).IsEqualTo(6).Because("two tables of each of the three demos met in");
            await Assert.That(stranger).IsEqualTo("2");
            await Assert.That(afterStranger).IsEqualTo(8).Because("demo 4 is new to the cache");
            await Assert.That(rig.Reads.Count).IsEqualTo(8).Because("Rival's demos are held");
            await Assert.That(rig.Section.KillsText).IsEqualTo("6");
        }
    }

    [Test]
    public async Task ASubjectWithNoOpponent_HidesTheSection()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord solo = Demo(0);
        solo.Players.RemoveAll(p => p.SteamId64 == Id(Rival));
        store.Upsert(solo);
        using Rig rig = new(store);
        rig.Section.Update(rig.Context(Grim));

        using (Assert.Multiple())
        {
            await Assert.That(rig.Section.IsVisible).IsFalse();
            await Assert.That(rig.Section.Opponents).IsEmpty();
            await Assert.That(rig.Reads).IsEmpty();
        }
    }
}

/// <summary>The head-to-head renders in the tab with its picker and one line per demo met in.</summary>
[NotInParallel]
[Category("Render")]
public class HeadToHeadRenderTests
{
    [Test]
    public async Task TheSection_RendersThePickerAndTheDemosMetIn()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = HeadToHeadTests.Library();
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRowCache rows = new(store, directory, path => HeadToHeadTests.Facet(HeadToHeadTests.IndexOf(path)));
            await rows.LoadAsync(HeadToHeadTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, rows, _ => { }, _ => { }, (_, _) => { }, _ => { })
            {
                Events = new PlayerEventCache((path, key) => HeadToHeadTests.Events(HeadToHeadTests.IndexOf(path), key))
            });
            vm.OnActivated(null!);
            vm.OpenPlayer(HeadToHeadTests.Grim);
            HeadToHeadSectionViewModel section = vm.Sections.OfType<HeadToHeadSectionViewModel>().Single();
            await section.Loading;

            vm.ShowPage(PlayerOverviewPageKind.History);

            Window window = new() { Width = 1300, Height = 4000, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            HeadToHeadSectionView view = window.GetVisualDescendants().OfType<HeadToHeadSectionView>().Single();
            ComboBox picker = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "OpponentPicker");
            ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "HeadToHeadList");
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(picker.ItemCount).IsEqualTo(2);
                await Assert.That((picker.SelectedItem as OpponentItem)?.SteamId64).IsEqualTo(76561190000000007UL);
                await Assert.That(list.GetVisualDescendants().OfType<ListBoxItem>().Count()).IsEqualTo(3);
                await Assert.That(view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")).Contains("2-1");
            }

            window.Close();
        });
    }
}
