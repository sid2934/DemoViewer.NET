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
///     The Player Overview's teammates over an in-memory store: who ended the subject's demos on their side, the
///     record together, each teammate's HLTV pooled over the shared demos from the rows the subject's facet read
///     already carries, a demo whose facet is not written counted without stats, and a teammate opening their own
///     overview. The facet reader is a fake.
/// </summary>
public class TeammatesTests
{
    internal const ulong Grim = 76561190000000003;
    internal const ulong Mate = 76561190000000012;
    private const ulong Rival = 76561190000000007;
    private static readonly string MatchStamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    // Demo i on day i + 1. Grim (slot 2) and Mate (slot 3) are CT, Rival (slot 7) T; in demo 3 Rival is on Grim's
    // side, on Inferno. Demo 2's facet is not written.
    private static DemoCacheRecord Demo(int i)
    {
        DemoCacheRecord record = new()
        {
            Path = $"/lib/{i}.dem",
            Size = 100,
            ModifiedTicks = 1,
            Sha256 = $"sha{i}",
            Map = i == 3 ? "de_inferno" : "de_nuke",
            Players =
            [
                new CachedPlayerInfo { Slot = 2, Name = "grim", SteamId64 = Id(Grim), Team = 3 },
                new CachedPlayerInfo { Slot = 3, Name = i == 3 ? "mate2" : "mate", SteamId64 = Id(Mate), Team = 3 },
                new CachedPlayerInfo { Slot = 7, Name = "rival", SteamId64 = Id(Rival), Team = i == 3 ? 3 : 2 }
            ],
            MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = (i + 1) * TimeSpan.TicksPerDay, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        if (i != 2)
        {
            record.PackStamps.Add(new PackStamp(MatchStamp, StampedFacts.Schema, "fp") { ComputedAtTicks = 1 });
        }

        return record;
    }

    private static FactRow Row(long slot, ulong steamId, long team, long rounds, double hltv, long won, long lost) =>
        new(new Dictionary<string, FactValue> { ["player_slot"] = FactValue.Of(slot), ["steam_id"] = FactValue.Of(Id(steamId)), ["team"] = FactValue.Of(team) },
            new Dictionary<string, FactValue>
            {
                ["rounds"] = FactValue.Of(rounds), ["HLTV"] = FactValue.Of(hltv), ["TotalK"] = FactValue.Of(15L), ["TotalD"] = FactValue.Of(15L),
                ["Shots"] = FactValue.Of(200L), ["CTW"] = FactValue.Of(won), ["CTL"] = FactValue.Of(lost)
            });

    // Grim wins demo 0 and loses 1 and 3. Mate rates 1.0 + 0.1 i over 20 + i rounds; Rival 0.8.
    internal static FactTable Facet(int i)
    {
        long rounds = 20 + i;
        (long won, long lost) = i == 0 ? (13L, rounds - 13) : (rounds - 13, 13L);
        return new FactTable(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match", ["player_slot", "steam_id", "team"], [],
            new Dictionary<string, string>(),
            [
                Row(2, Grim, 3, rounds, 1.2, won, lost),
                Row(3, Mate, 3, rounds, 1.0 + (0.1 * i), won, lost),
                Row(7, Rival, i == 3 ? 3 : 2, rounds, 0.8, lost, won)
            ]);
    }

    internal static DemoCacheStore Library()
    {
        DemoCacheStore store = new(null);
        for (int i = 0; i < 4; i++)
        {
            store.Upsert(Demo(i));
        }

        return store;
    }

    internal static int IndexOf(string path) => int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Store = Library();
            Directory = new PlayerDirectory(Store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            Cache = new PlayerRowCache(Store, Directory, path =>
            {
                Reads.Enqueue(path);
                return Facet(IndexOf(path));
            });
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(Directory, Identity, Cache, _ => { }, _ => { }, (_, _) => { }, _ => { }));
            Tab.OnActivated(null!);
        }

        public DemoCacheStore Store { get; }
        public PlayerDirectory Directory { get; }
        public IdentityService Identity { get; }
        public PlayerRowCache Cache { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public TeammatesSectionViewModel Section => Tab.Sections.OfType<TeammatesSectionViewModel>().Single();
        public ConcurrentQueue<string> Reads { get; } = new();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    [Test]
    public async Task TheRowCache_KeepsEveryOtherAccountsRating_FromTheSameRead()
    {
        DemoCacheStore store = Library();
        PlayerDirectory directory = new(store, _ => true);
        ConcurrentQueue<string> reads = new();
        PlayerRowCache cache = new(store, directory, path =>
        {
            reads.Enqueue(path);
            return Facet(IndexOf(path));
        });

        PlayerRows rows = await cache.LoadAsync(Grim);
        PlayerDemo first = rows.Demos.Single(d => d.Match.Row.Path == "/lib/0.dem");
        PlayerDemo pending = rows.Demos.Single(d => d.Match.Row.Path == "/lib/2.dem");

        using (Assert.Multiple())
        {
            await Assert.That(first.Others!.Keys.Order()).IsEquivalentTo([Rival, Mate], CollectionOrdering.Matching)
                .Because("the subject is not among the others");
            await Assert.That(first.Others![Mate].Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(["HLTV", "rounds"], CollectionOrdering.Matching)
                .Because("only the rating's inputs are kept");
            await Assert.That(first.Others![Mate]["HLTV"]).IsEqualTo(1.0);
            await Assert.That(pending.Others).IsNull();
            await Assert.That(reads.Count).IsEqualTo(3).Because("one read per written facet serves the subject and everyone in it");
        }
    }

    [Test]
    public async Task Of_IsTheSameSide_WithTheRecordAndTheRoundsWeightedRating()
    {
        DemoCacheStore store = Library();
        PlayerDirectory directory = new(store, _ => true);
        PlayerRows rows = await new PlayerRowCache(store, directory, path => Facet(IndexOf(path))).LoadAsync(Grim);

        IReadOnlyList<PlayerTeammate> mates = Teammates.Of(Grim, rows.Demos);

        PlayerTeammate mate = mates[0];
        PlayerTeammate rival = mates[1];
        using (Assert.Multiple())
        {
            await Assert.That(mates.Select(m => m.SteamId64)).IsEquivalentTo([Mate, Rival], CollectionOrdering.Matching)
                .Because("Rival shared a side once, in demo 3");
            await Assert.That((mate.Matches, mate.Wins, mate.Losses, mate.Ties, mate.Rated)).IsEqualTo((4, 1, 2, 0, 3))
                .Because("demo 2 counts as shared but has no stats");
            await Assert.That(mate.Name).IsEqualTo("mate2").Because("the newest demo's name");
            await Assert.That(mate.Hltv!.Value).IsEqualTo(((1.0 * 20) + (1.1 * 21) + (1.3 * 23)) / 64).Within(1e-9);
            await Assert.That(mate.WinRate!.Value).IsEqualTo(100.0 / 3).Within(1e-9);
            await Assert.That((rival.Matches, rival.Wins, rival.Losses, rival.Hltv)).IsEqualTo((1, 0, 1, (double?) 0.8));
        }
    }

    [Test]
    public async Task Of_CountsADemoOnce_AndLeavesOutADemoWithoutASide()
    {
        DemoCacheStore store = Library();
        PlayerDirectory directory = new(store, _ => true);
        PlayerMatch match = directory.Matches(Grim)[0];
        PlayerDemo demo = new(match, false, null);
        PlayerDemo spectator = new(match with { Side = null, LeftEarly = true }, false, null);

        IReadOnlyList<PlayerTeammate> mates = Teammates.Of(Grim, [demo, demo]);
        IReadOnlyList<PlayerTeammate> none = Teammates.Of(Grim, [spectator]);

        using (Assert.Multiple())
        {
            await Assert.That(mates.Single(m => m.SteamId64 == Mate).Matches).IsEqualTo(1);
            await Assert.That(mates.Single(m => m.SteamId64 == Mate).WinRate).IsNull();
            await Assert.That(none).IsEmpty();
        }
    }

    [Test]
    public async Task TheSection_FollowsTheWindow_AndOpensATeammate()
    {
        using Rig rig = new();
        rig.Tab.OpenPlayer(Grim);
        await rig.Tab.Loading;

        TeammateRowItem mate = rig.Section.Rows[0];
        string allNote = rig.Section.NoteText;
        int reads = rig.Reads.Count;
        rig.Tab.Filter.Map = rig.Tab.Filter.Maps.Single(c => c.Label == "Inferno");
        IReadOnlyList<TeammateRowItem> windowed = rig.Section.Rows;

        using (Assert.Multiple())
        {
            await Assert.That(rig.Section.IsVisible).IsTrue();
            await Assert.That(rig.Section.CountText).IsEqualTo("2 players");
            await Assert.That((mate.Name, mate.MatchesText, mate.RecordText, mate.WinRateText, mate.HltvText))
                .IsEqualTo(("mate2", "4", "1-2", "33%", "1.14"));
            await Assert.That(allNote).IsEqualTo("Win rates and ratings from the 3 of 4 matches with stats; the library is computing the rest");
            await Assert.That(windowed.Select(r => (r.SteamId64, r.MatchesText, r.HltvText)))
                .IsEquivalentTo([(Mate, "1", "1.30"), (Rival, "1", "0.80")], CollectionOrdering.Matching);
            await Assert.That(rig.Reads.Count).IsEqualTo(reads).Because("a filter change reads no file");
        }

        rig.Section.OpenTeammateCommand.Execute(mate);
        await Assert.That(rig.Tab.SubjectSteamId64).IsEqualTo(Mate);
    }

    [Test]
    public async Task ASubjectWithNoTeammate_HidesTheSection()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord solo = Demo(0);
        solo.Players.RemoveAll(p => p.SteamId64 == Id(Mate));
        store.Upsert(solo);
        PlayerDirectory directory = new(store, _ => true);
        using IdentityService identity = new(new SettingsService((string?) null));
        PlayerOverviewServices services = new(directory, identity, null, _ => { }, _ => { }, (_, _) => { }, _ => { });
        TeammatesSectionViewModel section = new(services);
        IReadOnlyList<PlayerMatch> matches = directory.Matches(Grim);
        PlayerDemo[] demos = [.. matches.Select(m => new PlayerDemo(m, false, null))];

        section.Update(new PlayerOverviewContext(Grim, directory.Get(Grim), matches, 0, false, services, demos, PlayerFilter.All, demos, false, null,
            false));

        using (Assert.Multiple())
        {
            await Assert.That(section.IsVisible).IsFalse();
            await Assert.That(section.Rows).IsEmpty();
        }
    }
}

/// <summary>The teammates render in the tab, one line per teammate with an open action.</summary>
[NotInParallel]
[Category("Render")]
public class TeammatesRenderTests
{
    [Test]
    public async Task TheSection_RendersOneLinePerTeammate()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = TeammatesTests.Library();
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?) null));
            PlayerRowCache rows = new(store, directory, path => TeammatesTests.Facet(TeammatesTests.IndexOf(path)));
            await rows.LoadAsync(TeammatesTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, rows, _ => { }, _ => { }, (_, _) => { }, _ => { }));
            vm.OnActivated(null!);
            vm.OpenPlayer(TeammatesTests.Grim);
            await vm.Loading;

            Window window = new() { Width = 1300, Height = 4000, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            TeammatesSectionView view = window.GetVisualDescendants().OfType<TeammatesSectionView>().Single();
            ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TeammatesList");
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(list.GetVisualDescendants().OfType<ListBoxItem>().Count()).IsEqualTo(2);
                await Assert.That(view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")).Contains("1.14");
                await Assert.That(list.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "Open")).IsEqualTo(2);
            }

            window.Close();
        });
    }
}
