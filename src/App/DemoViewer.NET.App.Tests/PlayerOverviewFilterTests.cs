#region

using System.Collections.Concurrent;
using System.Globalization;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview's filter bar and summary tiles over an in-memory store: every panel windows the same
///     rows, a filter change reads no file, the tiles equal <see cref="PlayerAggregate" /> over the window, the
///     cold read runs off the calling thread with progress, and a window with no stats shows dashes, never zeros.
/// </summary>
public class PlayerOverviewFilterTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;
    private static readonly string[] Maps = ["de_nuke", "de_inferno", "de_mirage"];

    // Demo i is on Maps[i % 3], competitive on even i and premier on odd; Grim ends on CT on even demos.
    private static DemoCacheStore Library(int demos, Func<int, bool>? stamped = null)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < demos; i++)
            {
                DemoCacheRecord record = PlayerOverviewHistoryTests.Demo(i, stamped?.Invoke(i) ?? true);
                record.Map = Maps[i % Maps.Length];
                record.MatchMeta!.GameMode = i % 2 == 0 ? "competitive" : "premier";
                store.Upsert(record);
            }
        }

        return store;
    }

    private sealed class Shell : IDisposable
    {
        public Shell(DemoCacheStore store, Func<string, FactTable?>? read = null)
        {
            Store = store;
            Directory = new PlayerDirectory(store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            Identity.SetAccounts([new IdentityAccount(Grim.ToString(CultureInfo.InvariantCulture), null, DateTime.UtcNow)]);
            Func<string, FactTable?> facet = read ?? (path =>
            {
                Reads.Enqueue(path);
                return PlayerOverviewHistoryTests.Facet(int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture));
            });
            Cache = new PlayerRowCache(store, Directory, facet);
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(Directory, Identity, Cache, _ => { }, _ => { }, (_, _) => { }, _ => { }));
            Tab.OnActivated(null!);
        }

        public DemoCacheStore Store { get; }
        public PlayerDirectory Directory { get; }
        public IdentityService Identity { get; }
        public PlayerRowCache Cache { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public ConcurrentQueue<string> Reads { get; } = new();
        public PlayerOverviewFilterViewModel Filter => Tab.Filter;
        public SummarySectionViewModel Summary => Tab.Sections.OfType<SummarySectionViewModel>().Single();
        public HistorySectionViewModel History => Tab.Sections.OfType<HistorySectionViewModel>().Single();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    private static PlayerFilterChoice Choice(IEnumerable<PlayerFilterChoice> choices, string label) => choices.Single(c => c.Label == label);

    private static string Tile(SummarySectionViewModel summary, string key) => summary.Tiles.Single(t => t.Key == key).Text;

    [Test]
    public async Task SummarySitsBetweenTheHeaderAndTheHistory()
    {
        using Shell shell = new(Library(3));
        await Assert.That(shell.Tab.Sections.Select(s => s.Id).Take(3)).IsEquivalentTo(["header", "summary", "history"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tiles_EqualTheAggregateOverTheSameRows_ForEveryFilter()
    {
        using Shell shell = new(Library(12));
        await shell.Tab.Loading;
        PlayerRows rows = shell.Cache.TryGet(Grim)!;

        List<Action> selections =
        [
            () => { },
            () => shell.Filter.Window = Choice(shell.Filter.Windows, "Last 10"),
            () => shell.Filter.Map = Choice(shell.Filter.Maps, "Inferno"),
            () => shell.Filter.Side = Choice(shell.Filter.Sides, "CT"),
            () => shell.Filter.Mode = Choice(shell.Filter.Modes, "Competitive")
        ];
        foreach (Action select in selections)
        {
            select();
            PlayerFilter filter = shell.Filter.Filter;
            PlayerAggregate expected = rows.Aggregate(filter);
            PlayerAggregate shown = shell.Summary.InWindow!;
            using (Assert.Multiple())
            {
                await Assert.That((shown.Matches, shown.WithStats, shown.Wins, shown.Losses, shown.Ties))
                    .IsEqualTo((expected.Matches, expected.WithStats, expected.Wins, expected.Losses, expected.Ties));
                await Assert.That(shown.Values).IsEquivalentTo(expected.Values);
                await Assert.That(Tile(shell.Summary, "HLTV")).IsEqualTo(expected["HLTV"]!.Value.ToString("0.00", CultureInfo.InvariantCulture));
                await Assert.That(Tile(shell.Summary, "ADR")).IsEqualTo(expected["ADR"]!.Value.ToString("0.0", CultureInfo.InvariantCulture));
                await Assert.That(Tile(shell.Summary, "KD")).IsEqualTo(expected["KD"]!.Value.ToString("0.00", CultureInfo.InvariantCulture));
                await Assert.That(Tile(shell.Summary, "Matches")).IsEqualTo(expected.Matches.ToString(CultureInfo.InvariantCulture));
                await Assert.That(shell.History.Rows.Count).IsEqualTo(expected.Matches).Because("the history windows the same rows");
                await Assert.That(shell.Summary.AllTime!.Values).IsEquivalentTo(rows.Aggregate().Values);
            }
        }

        await Assert.That(shell.Filter.Filter).IsEqualTo(new PlayerFilter { Last = 10, Map = "de_inferno", Side = 3, GameMode = "competitive" });
    }

    [Test]
    public async Task AFilterChange_ReadsNoFile()
    {
        using Shell shell = new(Library(9));
        await shell.Tab.Loading;
        int reads = shell.Reads.Count;
        await Assert.That(reads).IsEqualTo(9);

        shell.Filter.Map = Choice(shell.Filter.Maps, "Mirage");
        shell.Filter.Window = Choice(shell.Filter.Windows, "Last 30");
        shell.Filter.Side = Choice(shell.Filter.Sides, "T");
        using (Assert.Multiple())
        {
            await Assert.That(shell.Reads.Count).IsEqualTo(reads);
            await Assert.That(shell.Tab.Loading.IsCompleted).IsTrue();
            await Assert.That(shell.History.Rows.Select(r => r.Match.Row.Path)).IsEquivalentTo(["/lib/5.dem"]);
            await Assert.That(shell.History.CountText).IsEqualTo("1 of 9 matches");
        }
    }

    [Test]
    public async Task AFilteredTile_ShowsTheAllTimeValueBeside()
    {
        using Shell shell = new(Library(6));
        await shell.Tab.Loading;
        SummaryTileItem before = shell.Summary.Tiles.Single(t => t.Key == "Win%");
        await Assert.That((before.Text, before.Caption)).IsEqualTo(("50%", ""));

        shell.Filter.Side = Choice(shell.Filter.Sides, "CT");
        SummaryTileItem won = shell.Summary.Tiles.Single(t => t.Key == "Win%");
        SummaryTileItem matches = shell.Summary.Tiles.Single(t => t.Key == "Matches");
        using (Assert.Multiple())
        {
            await Assert.That((won.Text, won.Caption)).IsEqualTo(("100%", "all time 50%"));
            await Assert.That((matches.Text, matches.Caption)).IsEqualTo(("3", "all time 6"));
            await Assert.That(shell.Filter.IsFiltered).IsTrue();
        }

        shell.Filter.ResetCommand.Execute(null);
        await Assert.That(shell.Filter.IsFiltered).IsFalse();
        await Assert.That(shell.History.Rows.Count).IsEqualTo(6);
    }

    [Test]
    public async Task ADateRange_KeepsTheDaysInclusive()
    {
        using Shell shell = new(Library(6));
        await shell.Tab.Loading;
        shell.Filter.Window = Choice(shell.Filter.Windows, "Date range");
        await Assert.That(shell.Filter.IsDateRange).IsTrue();
        await Assert.That(shell.History.Rows.Count).IsEqualTo(6).Because("an open range keeps everything");

        // Demo i is played at midnight UTC of day i + 1.
        DateTime Day(int i) => new DateTime((i + 1) * TimeSpan.TicksPerDay, DateTimeKind.Utc).ToLocalTime().Date;
        shell.Filter.From = Day(2);
        shell.Filter.To = Day(4);
        await Assert.That(shell.History.Rows.Select(r => r.Match.Row.Path)).IsEquivalentTo(["/lib/4.dem", "/lib/3.dem", "/lib/2.dem"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ColdLoad_ReadsOffTheCallingThread_WithProgress()
    {
        using ManualResetEventSlim gate = new(false);
        ConcurrentBag<int> threads = [];
        int caller = Environment.CurrentManagedThreadId;
        using Shell shell = new(Library(4), path =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            gate.Wait(TimeSpan.FromSeconds(10));
            return PlayerOverviewHistoryTests.Facet(int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture));
        });

        using (Assert.Multiple())
        {
            await Assert.That(shell.Tab.IsReadingStats).IsTrue();
            await Assert.That(shell.Tab.ReadText).StartsWith("Reading stats");
            await Assert.That(shell.History.Rows.All(r => r.Stats == HistoryStats.Loading)).IsTrue();
            await Assert.That(Tile(shell.Summary, "HLTV")).IsEqualTo("–");
            await Assert.That(Tile(shell.Summary, "Matches")).IsEqualTo("4");
        }

        gate.Set();
        await shell.Tab.Loading;
        using (Assert.Multiple())
        {
            await Assert.That(threads).DoesNotContain(caller);
            await Assert.That(shell.Tab.IsReadingStats).IsFalse();
            await Assert.That(Tile(shell.Summary, "HLTV")).IsEqualTo("1.17");
            await Assert.That(shell.History.Rows.All(r => r.HasStats)).IsTrue();
        }
    }

    [Test]
    public async Task AWindowWithNoStats_ShowsDashes_NeverZero()
    {
        using Shell shell = new(Library(4, i => i != 1));
        await shell.Tab.Loading;
        shell.Filter.Map = Choice(shell.Filter.Maps, "Inferno");
        using (Assert.Multiple())
        {
            await Assert.That(shell.History.Rows.Count).IsEqualTo(1);
            foreach (SummaryTileItem tile in shell.Summary.Tiles.Where(t => t.Key != "Matches"))
            {
                await Assert.That(tile.Text).IsEqualTo("–").Because(tile.Key);
                await Assert.That(tile.Value).IsNull();
            }

            await Assert.That(shell.Summary.Note).IsEqualTo("1 match without stats is left out of the tiles.");
        }
    }

    [Test]
    public async Task ANewSubject_StartsUnfiltered_AndAReindexKeepsTheSelection()
    {
        DemoCacheStore store = Library(6);
        using Shell shell = new(store);
        await shell.Tab.Loading;
        shell.Filter.Map = Choice(shell.Filter.Maps, "Nuke");

        store.Upsert(PlayerOverviewHistoryTests.Demo(9));
        await shell.Tab.Loading;
        await Assert.That(shell.Filter.Map.Label).IsEqualTo("Nuke").Because("the same subject keeps its filter");
        await Assert.That(shell.History.Rows.Count).IsEqualTo(3);

        shell.Tab.OpenPlayer(76561190000000012);
        await shell.Tab.Loading;
        await Assert.That(shell.Filter.IsFiltered).IsFalse();
    }

    [Test]
    public async Task Tiles_UseTheCataloguesFixedBands()
    {
        using Shell shell = new(Library(3));
        await shell.Tab.Loading;
        using (Assert.Multiple())
        {
            await Assert.That(shell.Summary.Tiles.Single(t => t.Key == "HLTV").Scale).IsEqualTo(StatScale.Absolute(0.40, 1.80, 0.95, 1.05));
            await Assert.That(shell.Summary.Tiles.Single(t => t.Key == "ADR").Scale).IsEqualTo(StatScale.Absolute(40, 120, 70, 82));
            await Assert.That(shell.Summary.Tiles.Single(t => t.Key == "Matches").Scale).IsNull();
            await Assert.That(shell.Summary.Tiles.Select(t => t.Key)).IsEquivalentTo(
                ["Matches", "Win%", "KD", "ADR", "KAST%", "HS%", "HLTV", "KPR", "Duel%", "Clutch", "MultiKills"]);
        }

        PlayerAggregate thin = PlayerAggregate.Of([]);
        await Assert.That(SummarySectionViewModel.AbsoluteScale("Duel%", thin)).IsNull().Because("fewer than 8 opening duels is not judged");
    }
}
