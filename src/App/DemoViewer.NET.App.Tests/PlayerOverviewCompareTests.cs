#region

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;
using DemoViewer.NET.Views.PlayerOverview;
using DemoViewer.NET.Views.PlayerOverview.Sections;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview's compare section over the head-to-head library: the second player comes from the
///     search's candidates, never the subject, and both players' tiles and categories pool their own demos that
///     pass the same filter. Grim (slot 2) is in demos 0-4, Rival (slot 7) in 0-3; Grim kills 20 and dies 15 every
///     demo, Rival kills 10 and dies 20.
/// </summary>
public class PlayerOverviewCompareTests
{
    internal const ulong Grim = HeadToHeadTests.Grim;
    internal const ulong Rival = 76561190000000007;

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    private static FactRow Row(long slot, ulong steamId, long team, long kills, long deaths, long openingKills, long openingDeaths, bool won) =>
        new(new Dictionary<string, FactValue>
            {
                ["player_slot"] = FactValue.Of(slot), ["steam_id"] = FactValue.Of(Id(steamId)), ["team"] = FactValue.Of(team)
            },
            new Dictionary<string, FactValue>
            {
                ["rounds"] = FactValue.Of(22L), ["TotalK"] = FactValue.Of(kills), ["TotalD"] = FactValue.Of(deaths),
                ["CTW"] = FactValue.Of(won ? 13L : 9L), ["CTL"] = FactValue.Of(won ? 9L : 13L),
                ["TotalFK"] = FactValue.Of(openingKills), ["TotalFD"] = FactValue.Of(openingDeaths),
                ["CTFK"] = FactValue.Of(openingKills), ["TrdK"] = FactValue.Of(2L)
            });

    // Grim wins even demos; Rival, on the other side, wins odd ones.
    internal static FactTable Facet(int i) => new(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match",
        ["player_slot", "steam_id", "team"], [], new Dictionary<string, string>(),
        [
            Row(2, Grim, 3, 20, 15, 4, 2, i % 2 == 0),
            Row(7, i == 4 ? 76561190000000020 : Rival, 2, 10, 20, 1, 5, i % 2 == 1)
        ]);

    internal static PlayerRowCache Cache(DemoCacheStore store, PlayerDirectory directory) =>
        new(store, directory, path => Facet(HeadToHeadTests.IndexOf(path)));

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            DemoCacheStore store = HeadToHeadTests.Library();
            Directory = new PlayerDirectory(store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            Rows = Cache(store, Directory);
            Services = new PlayerOverviewServices(Directory, Identity, Rows, _ => { }, _ => { }, (_, _) => { }, _ => { })
            {
                OpenPlayer = id => Opened.Add(id)
            };
            Section = new CompareSectionViewModel(Services);
        }

        public PlayerDirectory Directory { get; }
        public IdentityService Identity { get; }
        public PlayerRowCache Rows { get; }
        public PlayerOverviewServices Services { get; }
        public CompareSectionViewModel Section { get; }
        public List<ulong> Opened { get; } = [];

        public async Task<PlayerOverviewContext> Context(ulong subject, PlayerFilter? filter = null)
        {
            PlayerRows rows = await Rows.LoadAsync(subject);
            filter ??= PlayerFilter.All;
            IReadOnlyList<PlayerMatch> matches = Directory.Matches(subject);
            return new PlayerOverviewContext(subject, Directory.Get(subject), matches, matches.Count, false, Services, rows.Demos, filter,
                PlayerAggregate.Window(rows.Demos, filter), false, null, false);
        }

        public void Dispose() => Identity.Dispose();
    }

    private static CompareStatItem Tile(CompareSectionViewModel section, string key) => section.Tiles.Single(t => t.Key == key);

    private static CompareStatItem Stat(CompareSectionViewModel section, string key) =>
        section.Categories.SelectMany(c => c.Stats).Single(s => s.Key == key);

    [Test]
    public async Task WithNobodyPicked_TheSectionAsksForAPlayer_AndShowsNoColumns()
    {
        using Rig rig = new();
        rig.Section.Update(await rig.Context(Grim));

        using (Assert.Multiple())
        {
            await Assert.That(rig.Section.IsVisible).IsTrue();
            await Assert.That(rig.Section.HasOther).IsFalse();
            await Assert.That(rig.Section.Tiles).IsEmpty();
            await Assert.That(rig.Section.Categories).IsEmpty();
            await Assert.That(rig.Section.NoteText).IsEqualTo("Search a player to compare them with this one over the same filter.");
        }
    }

    [Test]
    public async Task TheCandidates_LeaveOutTheSubject_AndPickingOneComparesTheirTiles()
    {
        using Rig rig = new();
        rig.Section.Update(await rig.Context(Grim));

        rig.Section.SearchText = "r";
        ulong[] candidates = [.. rig.Section.Candidates.Select(c => c.SteamId64)];
        rig.Section.PickCandidateCommand.Execute(rig.Section.Candidates.Single(c => c.SteamId64 == Rival));
        await rig.Section.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(candidates).Contains(Rival);
            await Assert.That(candidates).DoesNotContain(Grim).Because("the subject is never compared with themselves");
            await Assert.That(rig.Section.SearchText).IsEqualTo("");
            await Assert.That(rig.Section.HasCandidates).IsFalse();
            await Assert.That((rig.Section.SubjectName, rig.Section.OtherName)).IsEqualTo(("grim", "rival"));
            await Assert.That((rig.Section.SubjectScope, rig.Section.OtherScope)).IsEqualTo(("5 matches", "4 matches"));
            await Assert.That(rig.Section.Tiles.Select(t => t.Key)).IsEquivalentTo(
                SummarySectionViewModel.Build(PlayerAggregate.Of([]), null).Select(t => t.Key), CollectionOrdering.Matching);
            await Assert.That((Tile(rig.Section, "KD").Subject.Text, Tile(rig.Section, "KD").Other.Text)).IsEqualTo(("1.33", "0.50"));
            await Assert.That(Tile(rig.Section, "KD").SubjectLeads).IsTrue();
            await Assert.That((Tile(rig.Section, "Win%").Subject.Text, Tile(rig.Section, "Win%").Other.Text)).IsEqualTo(("60%", "50%"));
            await Assert.That(Tile(rig.Section, "Matches").Lead).IsEqualTo(0).Because("a count over two sample sizes is not judged");
            await Assert.That(rig.Section.NoteText).IsEqualTo("");
        }
    }

    [Test]
    public async Task BothPlayers_PoolOnlyTheirDemosThatPassTheSameFilter()
    {
        using Rig rig = new();
        rig.Section.Update(await rig.Context(Grim));
        rig.Section.Pick(Rival);
        await rig.Section.Loading;

        // Odd demos are de_inferno: Grim lost both, Rival won both.
        rig.Section.Update(await rig.Context(Grim, new PlayerFilter { Map = "de_inferno" }));
        await rig.Section.Loading;

        using (Assert.Multiple())
        {
            await Assert.That((rig.Section.SubjectScope, rig.Section.OtherScope)).IsEqualTo(("2 matches", "2 matches"));
            await Assert.That((Tile(rig.Section, "Win%").Subject.Text, Tile(rig.Section, "Win%").Other.Text)).IsEqualTo(("0%", "100%"));
            await Assert.That(Tile(rig.Section, "Win%").OtherLeads).IsTrue();
        }

        rig.Section.Update(await rig.Context(Grim, new PlayerFilter { Map = "de_dust2" }));
        await Assert.That(rig.Section.NoteText).IsEqualTo("None of their matches passes the filter.");
    }

    [Test]
    public async Task ACategoryCount_IsJudgedByItsRate_AndOneWithoutARateOrBandIsNot()
    {
        using Rig rig = new();
        rig.Section.Update(await rig.Context(Grim));
        rig.Section.Pick(Rival);
        await rig.Section.Loading;

        CompareStatItem openingKills = Stat(rig.Section, "TotalFK");
        CompareStatItem openingDeaths = Stat(rig.Section, "TotalFD");
        using (Assert.Multiple())
        {
            await Assert.That(rig.Section.Categories.Select(c => c.Title)).IsEquivalentTo(CategoriesSectionViewModel.All.Select(c => c.Title),
                CollectionOrdering.Matching);
            await Assert.That((openingKills.Subject.Text, openingKills.Other.Text)).IsEqualTo(("20", "4"));
            await Assert.That((openingKills.Subject.Detail, openingKills.Other.Detail)).IsEqualTo(("0.18 per round", "0.05 per round"));
            await Assert.That(openingKills.SubjectLeads).IsTrue();
            await Assert.That(openingDeaths.SubjectLeads).IsTrue().Because("fewer opening deaths per round is the better");
            await Assert.That(Stat(rig.Section, "CTFK").Lead).IsEqualTo(0);
            await Assert.That(Stat(rig.Section, "TrdK").Lead).IsEqualTo(0).Because("the catalogue gives trade kills no direction");
        }
    }

    [Test]
    public async Task TypingTheSubjectsId_PicksNobody_AndOpeningTheSubjectYouCompareClearsThePick()
    {
        using Rig rig = new();
        rig.Section.Update(await rig.Context(Grim));
        rig.Section.SearchText = Id(Grim);
        bool pickedSelf = rig.Section.HasOther;

        rig.Section.SearchText = Id(Rival);
        await rig.Section.Loading;
        bool pickedById = rig.Section.OtherSteamId64 == Rival;
        rig.Section.OpenOtherCommand.Execute(null);

        rig.Section.Update(await rig.Context(Rival));

        using (Assert.Multiple())
        {
            await Assert.That(pickedSelf).IsFalse();
            await Assert.That(pickedById).IsTrue().Because("a SteamID picks at once, as the tab's own search opens one");
            await Assert.That(rig.Opened).IsEquivalentTo([Rival], CollectionOrdering.Matching);
            await Assert.That(rig.Section.HasOther).IsFalse();
            await Assert.That(rig.Section.Tiles).IsEmpty();
        }
    }
}

/// <summary>The compare section renders in the tab with both players' columns once a second player is picked.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewCompareRenderTests
{
    [Test]
    public async Task TheSection_RendersBothColumns()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = HeadToHeadTests.Library();
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRowCache rows = PlayerOverviewCompareTests.Cache(store, directory);
            await rows.LoadAsync(PlayerOverviewCompareTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, rows, _ => { }, _ => { }, (_, _) => { }, _ => { }));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewCompareTests.Grim);
            await vm.Loading;
            CompareSectionViewModel section = vm.Reveal<CompareSectionViewModel>();
            section.Pick(PlayerOverviewCompareTests.Rival);
            await section.Loading;

            vm.ShowPage(PlayerOverviewPageKind.Stats);

            Window window = new() { Width = 1300, Height = 6000, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            CompareSectionView view = window.GetVisualDescendants().OfType<CompareSectionView>().Single();
            ItemsControl tiles = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "CompareTiles");
            string[] texts = [.. view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(tiles.ItemCount).IsEqualTo(section.Tiles.Count);
                await Assert.That(texts).Contains("rival");
                await Assert.That(texts).Contains("4 matches");
            }

            window.Close();
        });
    }
}
