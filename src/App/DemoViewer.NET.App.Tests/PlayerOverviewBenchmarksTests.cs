#region

using System.Collections.Concurrent;
using System.Globalization;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library benchmarks over an in-memory library of ten accounts: the library fill reads each facet once
///     for every account in it, the population leaves out the subject and anyone under the minimum and follows
///     the subject's filter, a percentile counts a tie as half, the rank band narrows the population to the
///     subject's Premier tier, and the section always names who it compared with.
/// </summary>
public class PlayerOverviewBenchmarksTests
{
    private const ulong First = 76561190000000100;
    private const int Accounts = 10;
    private const int Demos = 10;
    private static readonly int[] Others = [0, 1, 2, 4, 5, 6, 7, 8];
    private static readonly string FacetStamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);

    // Account k has K/D k+1; the last account is in the first two demos only.
    private static ulong Account(int k) => First + (ulong) k;

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<int> In(int demo) => Enumerable.Range(0, demo < 2 ? Accounts : Accounts - 1);

    // Accounts 0-6 rate in Premier's 15,000 tier, the rest in the 5,000 one.
    private static int Rating(int k) => k < 7 ? 15000 + (k * 100) : 8000;

    private static DemoCacheRecord Demo(int i)
    {
        DemoCacheRecord record = new()
        {
            Path = $"/lib/{i}.dem",
            Size = 100,
            ModifiedTicks = 1,
            Sha256 = $"sha{i}",
            Map = i % 2 == 0 ? "de_nuke" : "de_inferno",
            Players = [.. In(i).Select(k => new CachedPlayerInfo { Slot = k, Name = $"p{k}", SteamId64 = Id(Account(k)), Team = k % 2 == 0 ? 3 : 2 })],
            MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = (i + 1) * TimeSpan.TicksPerDay, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        record.PackStamps.Add(new PackStamp(FacetStamp, StampedFacts.Schema, "fp") { ComputedAtTicks = 1 });
        record.SetStamp(new PackStamp(MatchMetaEvaluator.StampId, DemoMatchMeta.CurrentSchema, MatchMetaEvaluator.Version) { ComputedAtTicks = 1 });
        return record;
    }

    private static FactTable Facet(int i) => new(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match",
        ["player_slot", "steam_id", "team"], ["rounds", "TotalK", "TotalD", "CTW", "CTL"], new Dictionary<string, string>(),
        [
            .. In(i).Select(k => new FactRow(
                new Dictionary<string, FactValue>
                {
                    ["player_slot"] = FactValue.Of((long) k), ["steam_id"] = FactValue.Of(Id(Account(k))), ["team"] = FactValue.Of(k % 2 == 0 ? 3L : 2L)
                },
                new Dictionary<string, FactValue>
                {
                    ["rounds"] = FactValue.Of(20L), ["TotalK"] = FactValue.Of(10L * (k + 1)), ["TotalD"] = FactValue.Of(10L),
                    ["CTW"] = FactValue.Of(k % 2 == 0 ? 13L : 7L), ["CTL"] = FactValue.Of(k % 2 == 0 ? 7L : 13L)
                }))
        ]);

    private static DemoMatchMeta Meta(int i) => new()
    {
        Players = [.. In(i).Select(k => new MatchRankRow { SteamId64 = Id(Account(k)), RankType = 11, RankOld = Rating(k), RankNew = Rating(k) })]
    };

    private static int Index(string path) => int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);

    private sealed record Fixture(
        DemoCacheStore Store,
        PlayerDirectory Directory,
        PlayerRowCache Rows,
        PlayerRankCache Ranks,
        ConcurrentQueue<string> FacetReads,
        ConcurrentQueue<string> RecordReads);

    private static Fixture Library()
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < Demos; i++)
            {
                store.Upsert(Demo(i));
            }
        }

        PlayerDirectory directory = new(store, _ => true);
        ConcurrentQueue<string> facets = new(), records = new();
        PlayerRowCache rows = new(store, directory, path =>
        {
            facets.Enqueue(path);
            return Facet(Index(path));
        });
        PlayerRankCache ranks = new(store, directory, path =>
        {
            records.Enqueue(path);
            return Meta(Index(path));
        });
        return new Fixture(store, directory, rows, ranks, facets, records);
    }

    [Test]
    public async Task TheLibraryFill_ReadsEachFacetOnce_ForEveryAccountInIt()
    {
        Fixture library = Library();
        LibraryRows rows = await library.Rows.LoadLibraryAsync();
        LibraryRows again = await library.Rows.LoadLibraryAsync();
        PlayerRows subject = await library.Rows.LoadAsync(Account(3));

        using (Assert.Multiple())
        {
            await Assert.That(library.FacetReads.Count).IsEqualTo(Demos).Because("the subject's fill reuses what the library fill read");
            await Assert.That(again).IsSameReferenceAs(rows);
            await Assert.That(rows.Accounts.Count).IsEqualTo(Accounts);
            await Assert.That(rows.Accounts[Account(9)].Count).IsEqualTo(2);
            await Assert.That(rows.Accounts[Account(3)].Select(d => d.Stats!["TotalK"]))
                .IsEquivalentTo(subject.Demos.Select(d => d.Stats!["TotalK"]), CollectionOrdering.Matching);
        }

        DemoCacheRecord rewritten = Demo(4);
        rewritten.PackStamps.RemoveAll(s => s.Id == FacetStamp);
        rewritten.PackStamps.Add(new PackStamp(FacetStamp, StampedFacts.Schema, "fp") { ComputedAtTicks = 2 });
        library.Store.Upsert(rewritten);
        library.FacetReads.Clear();
        await library.Rows.LoadLibraryAsync();
        await Assert.That(library.FacetReads).IsEquivalentTo(["/lib/4.dem"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ThePopulation_LeavesOutTheSubject_AndThoseUnderTheMinimum_AndFollowsTheFilter()
    {
        LibraryRows rows = await Library().Rows.LoadLibraryAsync();

        IReadOnlyList<(ulong SteamId64, PlayerAggregate Pool)> five = PlayerBenchmark.PopulationOf(rows, Account(3), PlayerFilter.All, 5);
        IReadOnlyList<(ulong SteamId64, PlayerAggregate Pool)> one = PlayerBenchmark.PopulationOf(rows, Account(3), PlayerFilter.All, 1);
        IReadOnlyList<(ulong SteamId64, PlayerAggregate Pool)> nuke = PlayerBenchmark.PopulationOf(rows, Account(3), new PlayerFilter { Map = "de_nuke" }, 5);
        IReadOnlyList<(ulong SteamId64, PlayerAggregate Pool)> last = PlayerBenchmark.PopulationOf(rows, Account(3), new PlayerFilter { Last = 3 }, 5);

        using (Assert.Multiple())
        {
            await Assert.That(five.Select(p => p.SteamId64)).IsEquivalentTo([.. Others.Select(Account)], CollectionOrdering.Matching);
            await Assert.That(one.Count).IsEqualTo(9);
            await Assert.That(nuke.Count).IsEqualTo(8);
            await Assert.That(nuke.All(p => p.Pool.Matches == 5)).IsTrue().Because("each player's own demos pass the subject's filter");
            await Assert.That(last).IsEmpty().Because("three demos each is under the minimum of five");
        }
    }

    [Test]
    public async Task APercentile_CountsBelowAndATieAsHalf()
    {
        using (Assert.Multiple())
        {
            await Assert.That(PlayerBenchmark.Percentile(5, [1, 5, 5, 9])).IsEqualTo(50);
            await Assert.That(PlayerBenchmark.Percentile(10, [1, 5, 5, 9])).IsEqualTo(100);
            await Assert.That(PlayerBenchmark.Percentile(0, [1, 5, 5, 9])).IsEqualTo(0);
            await Assert.That(PlayerBenchmark.Percentile(9, [1, 5, 5, 9])).IsEqualTo(87.5);
            await Assert.That(BenchmarkRowItem.Ordinal(1) + BenchmarkRowItem.Ordinal(12) + BenchmarkRowItem.Ordinal(23) + BenchmarkRowItem.Ordinal(50))
                .IsEqualTo("1st12th23rd50th");
        }
    }

    [Test]
    public async Task ARankBand_IsAPremierTier()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RankBand.For(14999)).IsEqualTo(new RankBand(10000, 14999));
            await Assert.That(RankBand.For(15000).Label).IsEqualTo("Premier 15,000-19,999");
            await Assert.That(RankBand.For(31250)).IsEqualTo(new RankBand(30000, null));
            await Assert.That(RankBand.For(31250).Label).IsEqualTo("Premier 30,000+");
            await Assert.That(RankBand.For(0).Contains(4999)).IsTrue();
            await Assert.That(RankBand.For(0).Contains(5000)).IsFalse();
        }
    }

    [Test]
    public async Task TheSection_NamesItsPopulation_AndTheRankBandNarrowsIt()
    {
        Fixture library = Library();
        using IdentityService identity = new(new SettingsService((string?) null));
        PlayerOverviewServices services = new(library.Directory, identity, library.Rows, _ => { }, _ => { }, (_, _) => { }, _ => { }, Ranks: library.Ranks);
        BenchmarksSectionViewModel section = new(services);
        ulong subject = Account(3);
        PlayerEntry player = library.Directory.Get(subject)!;
        PlayerRows rows = await library.Rows.LoadAsync(subject);
        PlayerRanks ranks = await library.Ranks.LoadAsync(subject);
        PlayerOverviewContext context = new(subject, player, [.. rows.Demos.Select(d => d.Match)], Demos, false, services, rows.Demos,
            PlayerFilter.All, rows.Demos, false, ranks, false);

        section.Update(context);
        await section.Loading;
        BenchmarkRowItem kd = section.Rows.Single(r => r.Stat.Key == "KD");

        using (Assert.Multiple())
        {
            await Assert.That(section.IsVisible).IsTrue();
            await Assert.That(section.PopulationText).IsEqualTo("vs 8 players in your library · 5+ matches each");
            await Assert.That(section.CanUseRankBand).IsTrue();
            await Assert.That(section.RankBandText).IsEqualTo("Only Premier 15,000-19,999");
            await Assert.That(kd.ValueText).IsEqualTo("4.00");
            await Assert.That(kd.Stat.Percentile).IsEqualTo(37.5).Because("three of the eight others are below 4.00");
            await Assert.That(kd.MedianText).IsEqualTo("median 5.50");
            await Assert.That(section.Rows.Select(r => r.Stat.Key)).Contains(PlayerBenchmark.WinRate);
        }

        section.UseRankBand = true;
        await section.Loading;
        kd = section.Rows.Single(r => r.Stat.Key == "KD");

        using (Assert.Multiple())
        {
            await Assert.That(section.PopulationText).IsEqualTo("vs 6 players in your library · 5+ matches each · Premier 15,000-19,999");
            await Assert.That(kd.Stat.Percentile).IsEqualTo(50);
            await Assert.That(library.RecordReads.Count).IsLessThanOrEqualTo(Demos * 2);
        }

        section.SelectedMinimum = 20;
        await section.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(section.PopulationText).IsEqualTo("vs 0 players in your library · 20+ matches each · Premier 15,000-19,999");
            await Assert.That(section.Rows).IsEmpty();
            await Assert.That(section.HasNote).IsTrue();
        }

        section.Update(context with { SteamId64 = null, Player = null });
        await Assert.That(section.IsVisible).IsFalse();
    }
}
