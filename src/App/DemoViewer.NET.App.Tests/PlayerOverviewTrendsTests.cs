#region

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.Sdk;
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
///     The Player Overview's trends and form over an in-memory store: one line per metric oldest first, only
///     matches with stats drawn, the form pooled over the last matches against the ones before, the window
///     narrowing both, and no form on a short history.
/// </summary>
public class PlayerOverviewTrendsTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;
    private static readonly string Stamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);

    // Demo i: Grim kills 10 + i over 20 rounds and deals 60 x kills damage, rated 1 + i / 10, so every metric climbs with i.
    private static FactTable Facet(int i)
    {
        long kills = 10 + i;
        return new FactTable(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match",
            ["player_slot", "steam_id", "team"], [], new Dictionary<string, string>(),
            [
                new FactRow(
                    new Dictionary<string, FactValue>
                    {
                        ["player_slot"] = FactValue.Of(2L),
                        ["steam_id"] = FactValue.Of(Grim.ToString(CultureInfo.InvariantCulture)),
                        ["team"] = FactValue.Of(3L)
                    },
                    new Dictionary<string, FactValue>
                    {
                        ["rounds"] = FactValue.Of(20L), ["TotalK"] = FactValue.Of(kills), ["TotalD"] = FactValue.Of(10L),
                        ["EnemyDmg"] = FactValue.Of(kills * 60), ["KAST%"] = FactValue.Of(70.0), ["HLTV"] = FactValue.Of(1 + (i / 10.0)),
                        ["CTW"] = FactValue.Of(13L), ["CTL"] = FactValue.Of(7L)
                    })
            ]);
    }

    internal sealed class Shell : IDisposable
    {
        public Shell(int demos, Func<int, bool>? stamped = null)
        {
            DemoCacheStore store = PlayerOverviewHistoryTests.Library(demos, stamped);
            PlayerDirectory directory = new(store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            Identity.SetAccounts([new IdentityAccount(Grim.ToString(CultureInfo.InvariantCulture), null, DateTime.UtcNow)]);
            PlayerRowCache cache = new(store, directory, path =>
                store.TryGetIndex(path)?.Stamp(Stamp) is { State: DemoAnalysisState.Indexed }
                    ? Facet(int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture))
                    : null);
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(directory, Identity, cache, _ => { }, _ => { }, (_, _) => { }, _ => { }));
            Tab.OnActivated(null!);
        }

        public IdentityService Identity { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public TrendsSectionViewModel Trends => Tab.Sections.OfType<TrendsSectionViewModel>().Single();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    private static TrendItem Item(TrendsSectionViewModel trends, string key) => trends.Trends.Single(t => t.Key == key);

    [Test]
    public async Task TrendsFollowTheHistory_AndSitBeforeRank()
    {
        using Shell shell = new(4);
        string[] ids = [.. shell.Tab.Sections.Select(s => s.Id)];
        await Assert.That(ids.ToList().IndexOf("trends")).IsGreaterThan(ids.ToList().IndexOf("history"));
        await Assert.That(ids.ToList().IndexOf("trends")).IsLessThan(ids.ToList().IndexOf("rank"));
    }

    [Test]
    public async Task EachMetricIsALine_OldestFirst_OverTheWindow()
    {
        using Shell shell = new(12);
        await shell.Tab.Loading;
        TrendsSectionViewModel trends = shell.Trends;

        using (Assert.Multiple())
        {
            await Assert.That(trends.IsVisible).IsTrue();
            await Assert.That(trends.Trends.Select(t => t.Key)).IsEquivalentTo(["HLTV", "ADR", "KD"], CollectionOrdering.Matching);
            await Assert.That(Item(trends, "HLTV").Values).IsEquivalentTo(Enumerable.Range(0, 12).Select(i => 1 + (i / 10.0)), CollectionOrdering.Matching)
                .Because("demo 0 is the oldest, so it is the leftmost point");
            await Assert.That(Item(trends, "KD").Values.Count).IsEqualTo(12);
            await Assert.That(Item(trends, "KD").PointTips.Count).IsEqualTo(12);
            await Assert.That(Item(trends, "ADR").LatestText).IsEqualTo((21 * 60 / 20.0).ToString("0.0", CultureInfo.InvariantCulture));
        }
    }

    [Test]
    public async Task Form_PoolsTheLastTenAgainstTheTenBefore()
    {
        using Shell shell = new(24);
        await shell.Tab.Loading;
        TrendsSectionViewModel trends = shell.Trends;
        TrendItem kd = Item(trends, "KD");

        // Newest ten are demos 14-23 (kills 24-33), the ten before are 4-13 (kills 14-23); deaths are 10 each.
        double recent = Enumerable.Range(14, 10).Sum(i => 10.0 + i) / 100;
        double previous = Enumerable.Range(4, 10).Sum(i => 10.0 + i) / 100;
        double change = (recent - previous) / previous * 100;
        using (Assert.Multiple())
        {
            await Assert.That(trends.HasForm).IsTrue();
            await Assert.That(trends.FormText).IsEqualTo("Last 10 against the 10 before");
            await Assert.That(kd.RecentText).IsEqualTo(recent.ToString("0.00", CultureInfo.InvariantCulture));
            await Assert.That(kd.PreviousText).IsEqualTo(previous.ToString("0.00", CultureInfo.InvariantCulture));
            await Assert.That(kd.ChangePercent).IsEqualTo(change).Within(1e-9);
            await Assert.That(kd.ChangeText).IsEqualTo("+" + change.ToString("0.0", CultureInfo.InvariantCulture) + "%");
            await Assert.That((kd.RisePercent, kd.FallPercent)).IsEqualTo((kd.ChangePercent, 0.0));
        }
    }

    [Test]
    public async Task AFallingForm_DrawsTheLosingArm()
    {
        using Shell shell = new(12);
        await shell.Tab.Loading;
        shell.Tab.Filter.Window = shell.Tab.Filter.Windows.Single(c => c.Label == "Last 10");
        TrendItem hltv = Item(shell.Trends, "HLTV");

        // Within a ten-match window the line is oldest first, so the latest value is the newest demo's.
        using (Assert.Multiple())
        {
            await Assert.That(hltv.Values.Count).IsEqualTo(10);
            await Assert.That(shell.Trends.FormText).IsEqualTo("Last 5 against the 5 before");
            await Assert.That(hltv.ChangePercent).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task AShortHistory_HasALineAndNoForm()
    {
        using Shell shell = new(5);
        await shell.Tab.Loading;
        TrendsSectionViewModel trends = shell.Trends;

        using (Assert.Multiple())
        {
            await Assert.That(trends.IsVisible).IsTrue();
            await Assert.That(trends.HasForm).IsFalse();
            await Assert.That(trends.Trends.Select(t => t.HasForm)).IsEquivalentTo([false, false, false]);
            await Assert.That(Item(trends, "HLTV").ChangeText).IsEqualTo("");
        }
    }

    [Test]
    public async Task OneMatchWithStats_HasNoSection()
    {
        using Shell shell = new(5, i => i == 3);
        await shell.Tab.Loading;
        await Assert.That(shell.Trends.IsVisible).IsFalse();
    }

    [Test]
    public async Task MatchesWithoutStats_AreSkipped_NotDrawnAsZero()
    {
        using Shell shell = new(8, i => i % 2 == 0);
        await shell.Tab.Loading;
        TrendItem hltv = Item(shell.Trends, "HLTV");

        await Assert.That(hltv.Values).IsEquivalentTo([1.0, 1.2, 1.4, 1.6], CollectionOrdering.Matching);
    }
}

/// <summary>The trends render a line and a form bar per metric.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewTrendsRenderTests
{
    [Test]
    public async Task EachMetric_RendersItsLineAndFormBar()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using PlayerOverviewTrendsTests.Shell shell = new(24);
            await shell.Tab.Loading;

            Window window = new() { Width = 1300, Height = 900, Content = new PlayerOverviewTabView { DataContext = shell.Tab } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            TrendsSectionView view = window.GetVisualDescendants().OfType<TrendsSectionView>().Single();
            Sparkline[] lines = [.. view.GetVisualDescendants().OfType<Sparkline>()];
            DivergingBar[] bars = [.. view.GetVisualDescendants().OfType<DivergingBar>()];
            using (Assert.Multiple())
            {
                await Assert.That(view.IsVisible).IsTrue();
                await Assert.That(lines.Length).IsEqualTo(3);
                await Assert.That(lines.All(l => l.Bounds.Width > 0)).IsTrue();
                await Assert.That(bars.Length).IsEqualTo(3);
                await Assert.That(bars.All(b => b.Positive > 0 && b.Extent == TrendsSectionViewModel.FormExtent)).IsTrue();
            }

            window.Close();
        });
    }
}
