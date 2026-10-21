#region

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
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.PlayerOverview;
using DemoViewer.NET.Views.PlayerOverview.Sections;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview's stat categories over an in-memory store: the seven groups in order, every
///     pooled column of a mirrored catalogue group listed, no line-of-sight column anywhere, each value the
///     <see cref="PlayerAggregate" /> of the filter's window and each rate over the window's pooled denominator.
/// </summary>
public class PlayerOverviewCategoriesTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;
    private static readonly string[] Maps = ["de_nuke", "de_inferno", "de_mirage"];

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    // Demo i is on Maps[i % 3]; its round count and every category column move with i, so no two demos pool alike.
    internal static FactTable Facet(int i)
    {
        Dictionary<string, FactValue> values = new()
        {
            ["rounds"] = FactValue.Of(20L + i), ["TotalK"] = FactValue.Of(18L + i), ["TotalD"] = FactValue.Of(14L), ["TotalA"] = FactValue.Of(4L),
            ["TotalHS"] = FactValue.Of(9L), ["EnemyDmg"] = FactValue.Of(1800L), ["KAST%"] = FactValue.Of(70.0), ["HLTV"] = FactValue.Of(1.1),
            ["CTW"] = FactValue.Of(13L), ["CTL"] = FactValue.Of(9L),
            ["TotalFK"] = FactValue.Of(3L + (i % 3)), ["TotalFD"] = FactValue.Of(2L), ["CTFK"] = FactValue.Of(2L), ["CTFD"] = FactValue.Of(1L),
            ["TFK"] = FactValue.Of(1L + (i % 3)), ["TFD"] = FactValue.Of(1L),
            ["TrdK"] = FactValue.Of(2L + i), ["TradedD"] = FactValue.Of(3L), ["Clutch"] = FactValue.Of((long)(i % 2)),
            ["Flash"] = FactValue.Of(10L), ["EFlash"] = FactValue.Of(7L + i), ["blind_duration_sum"] = FactValue.Of(15.5 + i), ["FlashAst"] = FactValue.Of(1L),
            ["HE"] = FactValue.Of(5L), ["Smokes"] = FactValue.Of(6L), ["Molly"] = FactValue.Of(3L),
            ["Rifle"] = FactValue.Of(10L + i), ["AWP"] = FactValue.Of(4L), ["Pistol"] = FactValue.Of(3L), ["SMG"] = FactValue.Of(1L), ["Knife"] = FactValue.Of(0L),
            ["DeagleHSRnds"] = FactValue.Of((long)(i % 2)),
            ["2K"] = FactValue.Of(3L), ["3K"] = FactValue.Of(1L), ["4K"] = FactValue.Of((long)(i % 2)), ["5K"] = FactValue.Of(0L),
            ["equipment_sum"] = FactValue.Of(80000.0 + (1000 * i)), ["equipment_rounds"] = FactValue.Of(20L + i), ["Armor"] = FactValue.Of(17L + i)
        };
        return new FactTable(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match", ["player_slot", "steam_id", "team"], [],
            new Dictionary<string, string>(),
            [new FactRow(new Dictionary<string, FactValue> { ["player_slot"] = FactValue.Of(2L), ["steam_id"] = FactValue.Of(Id(Grim)), ["team"] = FactValue.Of(3L) }, values)]);
    }

    internal static DemoCacheStore Library(int demos, Func<int, bool>? stamped = null)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < demos; i++)
            {
                DemoCacheRecord record = PlayerOverviewHistoryTests.Demo(i, stamped?.Invoke(i) ?? true);
                record.Map = Maps[i % Maps.Length];
                store.Upsert(record);
            }
        }

        return store;
    }

    internal static PlayerRowCache Cache(DemoCacheStore store, PlayerDirectory directory) =>
        new(store, directory, path => store.TryGetIndex(path)?.Stamp(StampedFacts.StampId(PlayerMatchFacet.RulesetId)) is { State: DemoAnalysisState.Indexed }
            ? Facet(int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture))
            : null);

    private sealed class Shell : IDisposable
    {
        public Shell(DemoCacheStore store)
        {
            PlayerDirectory directory = new(store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            Identity.SetAccounts([new IdentityAccount(Id(Grim), null, DateTime.UtcNow)]);
            Rows = Cache(store, directory);
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(directory, Identity, Rows, _ => { }, _ => { }, (_, _) => { }, _ => { }));
            Tab.OnActivated(null!);
        }

        public IdentityService Identity { get; }
        public PlayerRowCache Rows { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public CategoriesSectionViewModel Categories => Tab.Reveal<CategoriesSectionViewModel>();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    private static CategoryStatItem Stat(CategoriesSectionViewModel section, string key) =>
        section.Categories.SelectMany(c => c.Stats).Single(s => s.Key == key);

    [Test]
    public async Task Categories_AreThePlansSeven_InOrder()
    {
        using Shell shell = new(Library(3));
        await shell.Tab.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Categories.IsVisible).IsTrue();
            await Assert.That(shell.Categories.Page).IsEqualTo(PlayerOverviewPageKind.Stats);
            await Assert.That(shell.Categories.Categories.Select(c => c.Title))
                .IsEquivalentTo(["Opening duels", "Trades", "Clutches", "Utility", "Weapons", "Multi-kill", "Economy"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task NoCategory_ListsALineOfSightColumn_OrAnAimGroup()
    {
        string[] keys = [.. CategoriesSectionViewModel.All.SelectMany(c => c.Entries).Select(c => c.Key)];
        using (Assert.Multiple())
        {
            await Assert.That(keys.Where(ColumnCatalogue.RequiresVisibility)).IsEmpty();
            await Assert.That(keys.Where(k => ColumnCatalogue.Resolve(k).Group is StatGroup.AimQuality or StatGroup.Accuracy)).IsEmpty();
            await Assert.That(keys).IsEquivalentTo(keys.Distinct()).Because("a column sits in one category");
            await Assert.That(keys.Where(k => ColumnRules.For(k) is null or ColumnRule.NotAggregable)).IsEmpty()
                .Because("every listed column pools across demos");
        }
    }

    [Test]
    public async Task AMirroredGroup_ListsEveryPooledColumn()
    {
        foreach (CategoriesSectionViewModel.Category category in CategoriesSectionViewModel.All)
        {
            string[] expected =
            [
                .. ColumnCatalogue.All
                    .Where(m => category.Groups.Contains(m.Group) && !m.Hidden && !m.RequiresVisibility)
                    .Where(m => ColumnRules.For(m.Key) is not ColumnRule.NotAggregable)
                    .Select(m => m.Key)
            ];
            await Assert.That(category.Entries).IsNotEmpty();
            await Assert.That(expected.Except(category.Entries.Select(c => c.Key))).IsEmpty()
                .Because(category.Title + " mirrors its catalogue group");
        }
    }

    [Test]
    public async Task Values_EqualTheAggregateOverTheWindow_AndRatesUseItsDenominators()
    {
        using Shell shell = new(Library(9));
        await shell.Tab.Loading;

        foreach (Action select in (Action[]) [() => { }, () => shell.Tab.Filter.Map = shell.Tab.Filter.Maps.Single(c => c.Label == "Inferno")])
        {
            select();
            PlayerAggregate expected = shell.Rows.TryGet(Grim)!.Aggregate(shell.Tab.Filter.Filter);
            CategoriesSectionViewModel section = shell.Categories;
            int[] demos = [.. Enumerable.Range(0, 9).Where(i => shell.Tab.Filter.Filter.Map is null || Maps[i % 3] == shell.Tab.Filter.Filter.Map)];
            double rounds = demos.Sum(i => 20.0 + i);
            double kills = demos.Sum(i => 18.0 + i);

            using (Assert.Multiple())
            {
                foreach (CategoryStatItem stat in section.Categories.SelectMany(c => c.Stats))
                {
                    await Assert.That(stat.Value).IsEqualTo(expected[stat.Key]).Because(stat.Key);
                    await Assert.That(stat.Label).IsEqualTo(ColumnCatalogue.Resolve(stat.Key).Display);
                }

                await Assert.That(section.Aggregate!.Values).IsEquivalentTo(expected.Values);
                await Assert.That(Stat(section, "TotalFK").Detail)
                    .IsEqualTo((expected["TotalFK"]!.Value / rounds).ToString("0.00", CultureInfo.InvariantCulture) + " per round");
                await Assert.That(Stat(section, "AWP").Detail)
                    .IsEqualTo((expected["AWP"]!.Value / kills * 100).ToString("0", CultureInfo.InvariantCulture) + "% of kills");
                await Assert.That(Stat(section, "Clutch").Detail)
                    .IsEqualTo((expected["Clutch"]!.Value / demos.Length).ToString("0.00", CultureInfo.InvariantCulture) + " per match");
                await Assert.That(Stat(section, "Armor").Detail)
                    .IsEqualTo((expected["Armor"]!.Value / rounds * 100).ToString("0", CultureInfo.InvariantCulture) + "% of rounds");
                await Assert.That(Stat(section, "Equip").Text)
                    .IsEqualTo(expected["Equip"]!.Value.ToString("'$'#,0", CultureInfo.InvariantCulture));
                await Assert.That(section.ScopeText)
                    .IsEqualTo(string.Create(CultureInfo.InvariantCulture, $"{demos.Length} matches · {rounds:0} rounds"));
            }
        }
    }

    [Test]
    public async Task DuelRate_TakesItsAbsoluteBand_OnlyPastTheAttemptGate()
    {
        using Shell shell = new(Library(1));
        await shell.Tab.Loading;
        CategoryStatItem thin = Stat(shell.Categories, "Duel%");

        using Shell wide = new(Library(6));
        await wide.Tab.Loading;
        CategoryStatItem pooled = Stat(wide.Categories, "Duel%");

        using (Assert.Multiple())
        {
            await Assert.That(thin.Scale).IsNull().Because("five opening duels are under the catalogue's gate of eight");
            await Assert.That(pooled.Scale).IsNotNull();
            await Assert.That(Stat(wide.Categories, "TotalFK").Scale).IsNull().Because("a count has no fixed band");
            await Assert.That(Stat(wide.Categories, "AvgBlind").Scale).IsNotNull();
        }
    }

    [Test]
    public async Task ARateOverNothing_TakesNoColour()
    {
        DemoCacheStore store = Library(2);
        PlayerDirectory directory = new(store, _ => true);
        using IdentityService identity = new(new SettingsService((string?)null));
        identity.SetAccounts([new IdentityAccount(Id(Grim), null, DateTime.UtcNow)]);
        PlayerRowCache rows = new(store, directory, path =>
        {
            FactTable table = Facet(int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture));
            FactRow row = table.Rows[0];
            Dictionary<string, FactValue> values = new(row.Values) { ["EFlash"] = FactValue.Of(0L), ["blind_duration_sum"] = FactValue.Of(0.0) };
            return table with { Rows = [row with { Values = values }] };
        });
        using PlayerOverviewTabViewModel tab = new(new PlayerOverviewServices(directory, identity, rows, _ => { }, _ => { }, (_, _) => { }, _ => { }));
        tab.OnActivated(null!);
        await tab.Loading;
        CategoryStatItem blind = Stat(tab.Reveal<CategoriesSectionViewModel>(), "AvgBlind");

        using (Assert.Multiple())
        {
            await Assert.That(blind.Value).IsEqualTo(0);
            await Assert.That(blind.Scale).IsNull().Because("no enemy was flashed, so the average measured nothing");
        }
    }

    [Test]
    public async Task AWindowWithNoStats_HidesTheSection()
    {
        using Shell shell = new(Library(4, _ => false));
        await shell.Tab.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Categories.IsVisible).IsFalse();
            await Assert.That(shell.Categories.Categories).IsEmpty();
        }

        using Shell some = new(Library(4, i => i == 2));
        await some.Tab.Loading;
        await Assert.That(some.Categories.Aggregate!.WithStats).IsEqualTo(1);
        await Assert.That(some.Categories.ScopeText).IsEqualTo("1 match · 22 rounds");
    }
}

/// <summary>The stat categories render one block per category with the line-of-sight footer.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewCategoriesRenderTests
{
    [Test]
    public async Task EachCategory_RendersItsBlock()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = PlayerOverviewCategoriesTests.Library(3);
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRowCache rows = PlayerOverviewCategoriesTests.Cache(store, directory);
            await rows.LoadAsync(PlayerOverviewHistoryTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, rows,
                _ => { }, _ => { }, (_, _) => { }, _ => { }));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewHistoryTests.Grim);
            await vm.Loading;

            vm.ShowPage(PlayerOverviewPageKind.Stats);

            Window window = new() { Width = 1300, Height = 900, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            CategoriesSectionView view = window.GetVisualDescendants().OfType<CategoriesSectionView>().Single();
            string[] texts = [.. view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(view.Bounds.Width).IsGreaterThan(0);
                await Assert.That(texts).Contains("Opening duels");
                await Assert.That(texts).Contains("Economy");
                await Assert.That(texts).Contains("Clutches Won");
                await Assert.That(texts).Contains(CategoriesSectionViewModel.SightNote);
            }

            window.Close();
        });
    }
}
