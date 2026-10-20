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
///     The Player Overview's filter bar, summary tiles and per-map table over an in-memory store: every panel
///     windows the same rows, a filter change reads no file, the tiles equal <see cref="PlayerAggregate" /> over the
///     window, the map rows add up to the tiles, the cold read runs off the calling thread with progress, and a
///     window with no stats shows dashes, never zeros.
/// </summary>
public class PlayerOverviewFilterTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;
    private static readonly string[] Maps = ["de_nuke", "de_inferno", "de_mirage"];

    // Demo i is on Maps[i % 3], competitive on even i and premier on odd; Grim ends on CT on even demos. A demo
    // that passes rounds has its player_round facet written.
    private static DemoCacheStore Library(int demos, Func<int, bool>? stamped = null, Func<int, bool>? rounds = null)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < demos; i++)
            {
                DemoCacheRecord record = PlayerOverviewHistoryTests.Demo(i, stamped?.Invoke(i) ?? true);
                record.Map = Maps[i % Maps.Length];
                record.MatchMeta!.GameMode = i % 2 == 0 ? "competitive" : "premier";
                if (rounds?.Invoke(i) == true)
                {
                    record.PackStamps.Add(new PackStamp(StampedFacts.StampId(PlayerRoundFacet.RulesetId), StampedFacts.Schema, "fp") { ComputedAtTicks = 1 });
                }

                store.Upsert(record);
            }
        }

        return store;
    }

    // Facet(i) with T-side rounds and both sides' opening duels: T 3-2 on even demos and 3-3 on odd ones, CT opening
    // duels 2-1 everywhere, T opening duels 1-1 on even demos and 1-2 on odd ones. The outcome is Facet(i)'s.
    private static FactTable SidedFacet(string path)
    {
        int i = int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);
        FactTable facet = PlayerOverviewHistoryTests.Facet(i);
        FactRow row = facet.Rows[0];
        Dictionary<string, FactValue> values = new(row.Values)
        {
            ["TW"] = FactValue.Of(3L), ["TL"] = FactValue.Of(2L + (i % 2)),
            ["CTFK"] = FactValue.Of(2L), ["CTFD"] = FactValue.Of(1L), ["TFK"] = FactValue.Of(1L), ["TFD"] = FactValue.Of(1L + (i % 2))
        };
        return facet with { Rows = [new FactRow(row.Dimensions, values)] };
    }

    // Grim's rounds in demo i: two CT rounds with 2 kills, a death, 150 damage and KAST each; two T rounds with a
    // kill and 25 damage each, KAST in the first, a death in the first and, on odd demos, in the second.
    private static FactTable SidedRounds(string path)
    {
        long i = long.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);
        FactRow Round(long round, long side, long kills, long deaths, long damage, bool kast) => new(
            new Dictionary<string, FactValue>
            {
                ["round_number"] = FactValue.Of(round), ["player_slot"] = FactValue.Of(2L),
                ["steam_id"] = FactValue.Of(Grim.ToString(CultureInfo.InvariantCulture))
            },
            new Dictionary<string, FactValue>
            {
                ["side"] = FactValue.Of(side), ["kills"] = FactValue.Of(kills), ["deaths"] = FactValue.Of(deaths), ["assists"] = FactValue.Of(0L),
                ["damage"] = FactValue.Of(damage), ["survived"] = FactValue.Of(deaths == 0 ? 1L : 0L), ["kast"] = FactValue.Of(kast)
            });
        return new FactTable(PlayerRoundFacet.Key, "fp", StampedFacts.Schema, "player_round", ["round_number", "player_slot", "steam_id"],
            [.. PlayerRoundFacet.Counts.Prepend("side")], new Dictionary<string, string>(),
            [
                Round(1, PlayerRoundFacet.Ct, 2, 1, 150, true), Round(2, PlayerRoundFacet.Ct, 2, 1, 150, true),
                Round(3, PlayerRoundFacet.T, 1, 1, 25, true), Round(4, PlayerRoundFacet.T, 1, i % 2, 25, false)
            ]);
    }

    private sealed class Shell : IDisposable
    {
        public Shell(DemoCacheStore store, Func<string, FactTable?>? read = null, Func<string, FactTable?>? readRounds = null)
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
            Cache = new PlayerRowCache(store, Directory, facet, readRounds);
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
        public MapsSectionViewModel Maps => Tab.Sections.OfType<MapsSectionViewModel>().Single();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    private static PlayerFilterChoice Choice(IEnumerable<PlayerFilterChoice> choices, string label) => choices.Single(c => c.Label == label);

    private static string Tile(SummarySectionViewModel summary, string key) => summary.Tiles.Single(t => t.Key == key).Text;

    [Test]
    public async Task SummaryOpensTheOverview_UnderTheHeader()
    {
        using Shell shell = new(Library(3));
        await Assert.That(shell.Tab.Persistent.Select(s => s.Id)).IsEquivalentTo(["header"]);
        await Assert.That(shell.Tab.Pages[0].Top.Select(s => s.Id)).IsEquivalentTo(["summary"]);
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
        using Shell shell = new(Library(4), path =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            gate.Wait(TimeSpan.FromSeconds(10));
            return PlayerOverviewHistoryTests.Facet(int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture));
        });

        using (Assert.Multiple())
        {
            await Assert.That(shell.Tab.IsReadingStats).IsTrue();
            await Assert.That(shell.History.Rows.All(r => r.Stats == HistoryStats.Loading)).IsTrue();
            await Assert.That(Tile(shell.Summary, "HLTV")).IsEqualTo("–");
            await Assert.That(Tile(shell.Summary, "Matches")).IsEqualTo("4");
        }

        // The reader is blocked inside a read while this thread goes on, so the read is not on the calling path.
        await Assert.That(SpinWait.SpinUntil(() => !threads.IsEmpty, TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(shell.Tab.Loading.IsCompleted).IsFalse();

        gate.Set();
        await shell.Tab.Loading;
        using (Assert.Multiple())
        {
            await Assert.That(shell.Tab.IsReadingStats).IsFalse();
            await Assert.That(Tile(shell.Summary, "HLTV")).IsEqualTo("1.17");
            await Assert.That(shell.History.Rows.All(r => r.HasStats)).IsTrue();
        }
    }

    [Test]
    public async Task ADemoFinishedInTheBackground_RereadsBehindTheStatsOnShow()
    {
        using ManualResetEventSlim gate = new(true);
        using Shell shell = new(Library(4), path =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return PlayerOverviewHistoryTests.Facet(int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture));
        });
        await shell.Tab.Loading;
        string hltv = Tile(shell.Summary, "HLTV");

        // The queue finishing a demo drops the subject's rows; the re-read is held open to look at the page mid-read.
        gate.Reset();
        shell.Store.Upsert(PlayerOverviewHistoryTests.Demo(4, true));
        using (Assert.Multiple())
        {
            await Assert.That(shell.Tab.Loading.IsCompleted).IsFalse();
            await Assert.That(shell.Tab.IsReadingStats).IsFalse();
            await Assert.That(shell.History.Rows.Count(r => r.HasStats)).IsEqualTo(4).Because("the stats read before stay on show");
            await Assert.That(shell.History.Rows.Any(r => r.Stats == HistoryStats.Loading)).IsFalse();
            await Assert.That(Tile(shell.Summary, "HLTV")).IsEqualTo(hltv);
        }

        gate.Set();
        await shell.Tab.Loading;
        await Assert.That(shell.History.Rows.Count(r => r.HasStats)).IsEqualTo(5);
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
                ["Matches", "Win%", "KD", "ADR", "KAST%", "HS%", "HLTV", "KPR", "Duel%", "Clutch", "MultiKills", "TTD", "Preaim"]);
            await Assert.That(shell.Summary.Tiles.Single(t => t.Key == "TTD").Text).IsEqualTo(AimQualitySectionViewModel.NoData)
                .Because("no demo here was written with line-of-sight geometry");
        }

        PlayerAggregate thin = PlayerAggregate.Of([]);
        await Assert.That(SummarySectionViewModel.AbsoluteScale("Duel%", thin)).IsNull().Because("fewer than 8 opening duels is not judged");
    }

    [Test]
    public async Task MapRows_AddUpToTheSummary_ForEveryFilter()
    {
        using Shell shell = new(Library(12), SidedFacet);
        await shell.Tab.Loading;
        string[] sums = ["TotalK", "TotalD", "EnemyDmg", "CTW", "CTL", "TW", "TL", "CTFK", "CTFD", "TFK", "TFD"];

        List<Action> selections =
        [
            () => { },
            () => shell.Filter.Window = Choice(shell.Filter.Windows, "Last 10"),
            () => shell.Filter.Side = Choice(shell.Filter.Sides, "T"),
            () => shell.Filter.Mode = Choice(shell.Filter.Modes, "Premier"),
            () => shell.Filter.Map = Choice(shell.Filter.Maps, "Mirage")
        ];
        foreach (Action select in selections)
        {
            select();
            PlayerAggregate summary = shell.Summary.InWindow!;
            IReadOnlyList<MapRowItem> rows = shell.Maps.Rows;
            using (Assert.Multiple())
            {
                await Assert.That(rows.All(r => r.Pool.Matches > 0)).IsTrue().Because("a map with no demo in the window has no row");
                await Assert.That(rows.Select(r => r.Map).Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqualTo(rows.Count);
                await Assert.That((rows.Sum(r => r.Pool.Matches), rows.Sum(r => r.Pool.WithStats), rows.Sum(r => r.Pool.Wins),
                        rows.Sum(r => r.Pool.Losses), rows.Sum(r => r.Pool.Ties)))
                    .IsEqualTo((summary.Matches, summary.WithStats, summary.Wins, summary.Losses, summary.Ties));
                foreach (string column in sums)
                {
                    await Assert.That(rows.Sum(r => r.Pool[column] ?? 0)).IsEqualTo(summary[column]!.Value).Within(1e-9).Because(column);
                }
            }
        }

        await Assert.That(shell.Maps.Rows.Select(r => r.Map)).IsEquivalentTo(["de_mirage"]);
    }

    [Test]
    public async Task AMapWithNoDemoInTheWindow_HasNoRow()
    {
        using Shell shell = new(Library(2), SidedFacet);
        await shell.Tab.Loading;
        await Assert.That(shell.Maps.Rows.Select(r => r.MapText)).IsEquivalentTo(["Inferno", "Nuke"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
            .Because("Mirage is in no demo of the subject");

        shell.Filter.Map = Choice(shell.Filter.Maps, "Inferno");
        using (Assert.Multiple())
        {
            await Assert.That(shell.Maps.Rows.Select(r => r.MapText)).IsEquivalentTo(["Inferno"]);
            await Assert.That(shell.Maps.CountText).IsEqualTo("1 map");
        }

        shell.Filter.Map = Choice(shell.Filter.Maps, "Nuke");
        shell.Filter.Side = Choice(shell.Filter.Sides, "T");
        using (Assert.Multiple())
        {
            await Assert.That(shell.Maps.Rows).IsEmpty();
            await Assert.That(shell.Maps.IsVisible).IsFalse();
        }
    }

    [Test]
    public async Task MapRows_AndSideSplits_Format()
    {
        using Shell shell = new(Library(6), SidedFacet);
        await shell.Tab.Loading;
        MapRowItem nuke = shell.Maps.Rows.Single(r => r.Map == "de_nuke");
        using (Assert.Multiple())
        {
            await Assert.That(shell.Maps.Rows.Select(r => r.MapText)).IsEquivalentTo(["Inferno", "Mirage", "Nuke"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("equal counts sort by name");
            await Assert.That((nuke.MatchesText, nuke.RecordText, nuke.WinRateText)).IsEqualTo(("2", "1-1", "50%"));
            await Assert.That((nuke.CtText, nuke.TText)).IsEqualTo(("22-22 (50%)", "6-5 (55%)"));
            await Assert.That(nuke.ResultBar.Select(b => (b.Value, b.Slot))).IsEquivalentTo([(1.0, 0), (1.0, 1), (0.0, 2)],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(nuke.CtBar.Select(b => b.Value)).IsEquivalentTo([22.0, 22.0]);
            await Assert.That((nuke.CtShareText, nuke.TShareText, nuke.MatchesWord)).IsEqualTo(("50%", "55%", "matches"));
            await Assert.That(nuke.KdText).IsEqualTo(nuke.Pool["KD"]!.Value.ToString("0.00", CultureInfo.InvariantCulture));
            await Assert.That(nuke.AdrText).IsEqualTo("85.0");
            await Assert.That(nuke.HltvText).IsEqualTo("1.17");
            await Assert.That(shell.Maps.Sides.Select(s => (s.Side, s.RoundsText, s.OpeningText))).IsEquivalentTo(
                [("CT", "66-66 (50%)", "12-6 (67%)"), ("T", "18-15 (55%)", "6-9 (40%)")],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(shell.Maps.Sides.All(s => s is { KdText: "–", AdrText: "–", KastText: "–" })).IsTrue()
                .Because("no demo has its rounds by side yet");
            await Assert.That(shell.Maps.SideNote).IsEqualTo("K/D, ADR and KAST by side need per-round stats; the library is computing them.");
        }
    }

    [Test]
    public async Task SideRates_PoolTheRoundsBySide_OverTheDemosThatHaveThem()
    {
        using Shell shell = new(Library(4, rounds: i => i != 3), SidedFacet, SidedRounds);
        await shell.Tab.Loading;
        using (Assert.Multiple())
        {
            await Assert.That(shell.Maps.Sides.Select(s => (s.Side, s.KdText, s.AdrText, s.KastText))).IsEquivalentTo(
                [("CT", "2.00", "150.0", "100.0%"), ("T", "1.50", "25.0", "50.0%")],
                TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("demos 0-2: 6 rounds a side, 12-6 and 6-4 kills to deaths");
            await Assert.That(shell.Maps.SideNote).IsEqualTo("K/D, ADR and KAST by side cover 3 of 4 matches; the library is computing the rest.");
        }

        shell.Filter.Map = Choice(shell.Filter.Maps, "Nuke");
        using (Assert.Multiple())
        {
            await Assert.That(shell.Maps.Sides.Select(s => (s.Side, s.KdText))).IsEquivalentTo([("CT", "2.00"), ("T", "2.00")],
                TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("Nuke is demos 0 and 3, and only 0 has its rounds");
            await Assert.That(shell.Maps.SideNote).IsEqualTo("K/D, ADR and KAST by side cover 1 of 2 matches; the library is computing the rest.");
        }

        using Shell all = new(Library(4, rounds: _ => true), SidedFacet, SidedRounds);
        await all.Tab.Loading;
        using (Assert.Multiple())
        {
            await Assert.That(all.Maps.SideNote).IsEmpty();
            await Assert.That(all.Maps.Sides.Select(s => s.KdText)).IsEquivalentTo(["2.00", "1.33"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task AMapWithoutStats_KeepsItsMatchCount_AndShowsDashes()
    {
        using Shell shell = new(Library(4, i => i != 1), SidedFacet);
        await shell.Tab.Loading;
        shell.Filter.Map = Choice(shell.Filter.Maps, "Inferno");
        MapRowItem inferno = shell.Maps.Rows.Single();
        string[] cells = [inferno.RecordText, inferno.WinRateText, inferno.KdText, inferno.AdrText, inferno.HltvText, inferno.CtText, inferno.TText];
        using (Assert.Multiple())
        {
            await Assert.That(inferno.MatchesText).IsEqualTo("1");
            await Assert.That(cells.All(c => c == "–")).IsTrue();
            await Assert.That(shell.Maps.Sides.All(s => s.RoundsText == "–" && s.OpeningText == "–")).IsTrue();
        }
    }
}
