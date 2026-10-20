#region

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Controls.Stats;
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

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview's aim quality over an in-memory store: every sight column pools the demos whose
///     <c>player_match</c> stamp says it was written with line-of-sight geometry and reads "no data" when none was,
///     never 0; the accuracy columns pool every demo; the note says how many matches were left out.
/// </summary>
public class PlayerOverviewAimQualityTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;
    private static readonly string Stamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    // Demo i's sight components grow with i; a demo written without geometry carries the zeros a background visit stores.
    private static FactTable Facet(int i, bool sight)
    {
        Dictionary<string, FactValue> values = new(PlayerOverviewCategoriesTests.Facet(i).Rows.Single().Values)
        {
            ["Shots"] = FactValue.Of(300L + i), ["HitFoe"] = FactValue.Of(60L), ["head_hits"] = FactValue.Of(12L),
            ["head_damage"] = FactValue.Of(900L), ["CSAtt"] = FactValue.Of(80L), ["cs_clean"] = FactValue.Of(40L), ["moving_shots"] = FactValue.Of(30L),
            ["spray_error_sum"] = FactValue.Of(120.0), ["SprayN"] = FactValue.Of(50L)
        };
        long k = sight ? 1 : 0;
        foreach ((string column, long value) in (ReadOnlySpan<(string, long)>)
                 [
                     ("Spots", 40 + i), ("preaim_angle_sum", 400 + (10 * i)), ("contact_shots", 200), ("contact_hits", 50),
                     ("first_bullet_shots", 80), ("first_bullet_hits", 24), ("spray_shots", 120), ("spray_hits", 20),
                     ("XShots", 30), ("travel_sum", 240 + i), ("flick_error_sum", 30), ("TTSn", 25), ("tts_ticks", 500 + i),
                     ("TTDn", 20), ("ttd_ticks", 600 + i), ("AimRxn", 15), ("aimrx_ticks", 225), ("TTKn", 10), ("ttk_ticks", 900)
                 ])
        {
            values[column] = FactValue.Of(value * k);
        }

        return new FactTable(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match", ["player_slot", "steam_id", "team"], [],
            new Dictionary<string, string>(),
            [new FactRow(new Dictionary<string, FactValue> { ["player_slot"] = FactValue.Of(2L), ["steam_id"] = FactValue.Of(Id(Grim)), ["team"] = FactValue.Of(3L) }, values)]);
    }

    private static int Of(string path) => int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);

    internal static DemoCacheStore Library(int demos, Func<int, bool> sight, Func<int, bool>? stamped = null)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < demos; i++)
            {
                DemoCacheRecord record = PlayerOverviewHistoryTests.Demo(i, stamped?.Invoke(i) ?? true);
                if (record.Stamp(Stamp) is { } facet)
                {
                    record.SetStamp(facet with { HasVisibility = sight(i) });
                }

                store.Upsert(record);
            }
        }

        return store;
    }

    internal static PlayerRowCache Cache(DemoCacheStore store, PlayerDirectory directory) =>
        new(store, directory, path => store.TryGetIndex(path)?.Stamp(Stamp) is { State: DemoAnalysisState.Indexed } stamp
            ? Facet(Of(path), stamp.HasVisibility)
            : null);

    private static CategoryStatItem Stat(AimQualitySectionViewModel section, string key) =>
        section.Blocks.SelectMany(c => c.Stats).Single(s => s.Key == key);

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
        public AimQualitySectionViewModel Aim => Tab.Sections.OfType<AimQualitySectionViewModel>().Single();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    [Test]
    public async Task EveryCatalogueAimColumn_HasABlock_AndEverySightColumnIsCovered()
    {
        string[] keys = [.. AimQualitySectionViewModel.All.SelectMany(b => b.Entries).Select(e => e.Key)];
        string[] sight =
        [
            .. ColumnCatalogue.All.Where(m => m.RequiresVisibility && !m.Hidden && ColumnRules.For(m.Key) is not ColumnRule.NotAggregable).Select(m => m.Key)
        ];

        using (Assert.Multiple())
        {
            await Assert.That(sight.Except(keys)).IsEmpty().Because("every pooled sight column on the board is in the section");
            await Assert.That(ColumnCatalogue.All.Where(m => m.Group == StatGroup.AimQuality && !m.Hidden).Select(m => m.Key).Except(keys)).IsEmpty();
            await Assert.That(keys.Where(k => ColumnRules.For(k) is null or ColumnRule.NotAggregable)).IsEmpty();
            await Assert.That(keys).IsEquivalentTo(keys.Distinct());
        }
    }

    [Test]
    public async Task SightColumns_PoolOnlyTheDemosWithSight()
    {
        using Shell shell = new(Library(6, i => i % 2 == 0));
        await shell.Tab.Loading;
        AimQualitySectionViewModel aim = shell.Aim;
        PlayerAggregate expected = shell.Rows.TryGet(Grim)!.Aggregate();
        int[] sighted = [0, 2, 4];

        using (Assert.Multiple())
        {
            await Assert.That(aim.IsVisible).IsTrue();
            await Assert.That(aim.Aggregate!.WithSight).IsEqualTo(3);
            foreach (CategoryStatItem stat in aim.Blocks.SelectMany(b => b.Stats))
            {
                await Assert.That(stat.Value).IsEqualTo(expected[stat.Key]).Because(stat.Key);
                await Assert.That(stat.Text).IsNotEqualTo(AimQualitySectionViewModel.NoData).Because(stat.Key);
            }

            await Assert.That(Stat(aim, "Preaim").Value!.Value)
                .IsEqualTo(sighted.Sum(i => 400.0 + (10 * i)) / sighted.Sum(i => 40.0 + i)).Within(1e-9);
            await Assert.That(Stat(aim, "TTD").Value!.Value).IsEqualTo(sighted.Sum(i => 600.0 + i) / 60 * 15.625).Within(1e-9);
            await Assert.That(Stat(aim, "Preaim").Detail).IsEqualTo(string.Create(CultureInfo.InvariantCulture, $"over {sighted.Sum(i => 40 + i)} contacts"));
            await Assert.That(Stat(aim, "FB%").Detail).IsEqualTo(string.Create(CultureInfo.InvariantCulture, $"after {sighted.Sum(i => 40 + i)} contacts"));
            await Assert.That(Stat(aim, "Acc%").Value).IsEqualTo(60.0 * 6 / Enumerable.Range(0, 6).Sum(i => 300.0 + i) * 100)
                .Because("accuracy pools every demo");
            await Assert.That(aim.ScopeText).IsEqualTo("line of sight from 3 matches");
            await Assert.That(aim.Note).IsEqualTo("3 matches without line-of-sight data are left out of the reaction, crosshair and after-contact columns. "
                                                  + "Opening a match on a map with a collision bake adds it.");
        }
    }

    [Test]
    public async Task NoDemoWithSight_ReadsNoData_NeverZero()
    {
        using Shell shell = new(Library(3, _ => false));
        await shell.Tab.Loading;
        AimQualitySectionViewModel aim = shell.Aim;
        CategoryStatItem[] stats = [.. aim.Blocks.SelectMany(b => b.Stats)];

        using (Assert.Multiple())
        {
            await Assert.That(aim.IsVisible).IsTrue().Because("no data is said, not hidden");
            foreach (CategoryStatItem stat in stats.Where(s => ColumnCatalogue.RequiresVisibility(s.Key)))
            {
                await Assert.That(stat.Text).IsEqualTo(AimQualitySectionViewModel.NoData).Because(stat.Key);
                await Assert.That(stat.Value).IsNull().Because(stat.Key);
                await Assert.That(stat.Scale).IsNull().Because(stat.Key);
                await Assert.That(stat.HasDetail).IsFalse().Because(stat.Key);
            }

            await Assert.That(stats.Where(s => !ColumnCatalogue.RequiresVisibility(s.Key)).Select(s => s.Value)).DoesNotContain((double?)null);
            await Assert.That(aim.ScopeText).IsEqualTo("no line-of-sight data");
            await Assert.That(aim.Note).StartsWith("None of these matches has line-of-sight data");
            await Assert.That(shell.Tab.Sections.OfType<SummarySectionViewModel>().Single().Tiles.Single(t => t.Key == "Preaim").Text)
                .IsEqualTo(AimQualitySectionViewModel.NoData);
        }
    }

    [Test]
    public async Task EveryDemoWithSight_LeavesNoNote()
    {
        using Shell shell = new(Library(3, _ => true));
        await shell.Tab.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Aim.Aggregate!.WithSight).IsEqualTo(3);
            await Assert.That(shell.Aim.Note).IsEmpty();
            await Assert.That(shell.Aim.HasNote).IsFalse();
            await Assert.That(shell.Tab.Sections.OfType<SummarySectionViewModel>().Single().Tiles.Single(t => t.Key == "TTD").Value)
                .IsEqualTo(shell.Aim.Aggregate["TTD"]);
        }
    }

    [Test]
    public async Task AWindowWithNoStats_HidesTheSection()
    {
        using Shell shell = new(Library(3, _ => true, _ => false));
        await shell.Tab.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Aim.IsVisible).IsFalse();
            await Assert.That(shell.Aim.Blocks).IsEmpty();
        }
    }
}

/// <summary>The aim quality section renders its blocks, "no data" for the sight columns and the note.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewAimQualityRenderTests
{
    [Test]
    public async Task TheSection_RendersNoData_AndTheNote()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = PlayerOverviewAimQualityTests.Library(3, _ => false);
            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            PlayerRowCache rows = PlayerOverviewAimQualityTests.Cache(store, directory);
            await rows.LoadAsync(PlayerOverviewHistoryTests.Grim);
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, rows,
                _ => { }, _ => { }, (_, _) => { }, _ => { }));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewHistoryTests.Grim);
            await vm.Loading;

            // Tall enough to realise the last section under everything above it.
            Window window = new() { Width = 1300, Height = 6000, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            AimQualitySectionView view = window.GetVisualDescendants().OfType<AimQualitySectionView>().Single();
            string[] texts = [.. view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(view.Bounds.Width).IsGreaterThan(0);
                await Assert.That(texts).Contains("Aim quality");
                await Assert.That(texts).Contains("Reaction");
                await Assert.That(texts).Contains("Accuracy");
                await Assert.That(texts).Contains(ColumnCatalogue.Resolve("TTD").Display);
                await Assert.That(view.GetVisualDescendants().OfType<StatValue>().Count(v => v.Text == AimQualitySectionViewModel.NoData)).IsEqualTo(10);
                await Assert.That(texts.Any(t => t.StartsWith("None of these matches", StringComparison.Ordinal))).IsTrue();
            }

            window.Close();
        });
    }
}
