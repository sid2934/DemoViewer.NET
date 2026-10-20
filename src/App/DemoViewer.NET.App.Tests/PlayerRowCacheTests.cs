#region

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The row cache reads each demo's facet once per stamp: a cold fill reports its progress, a filter change
///     reads nothing, a facts write re-reads only its demo, and a demo with no facet is pending, not zero. The
///     store is in memory and the reader is a counting fake.
/// </summary>
public class PlayerRowCacheTests
{
    private const ulong Grim = 76561190000000003;
    private static readonly string Stamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);
    private static readonly string[] Maps = ["de_nuke", "de_inferno", "de_mirage", "de_ancient"];

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    private static DemoCacheRecord Demo(int i, bool stamped = true, long writtenAt = 1)
    {
        DemoCacheRecord record = new()
        {
            Path = $"/lib/{i}.dem",
            Size = 100,
            ModifiedTicks = 1,
            Sha256 = $"sha{i}",
            Map = Maps[i % Maps.Length],
            Players = [new CachedPlayerInfo { Slot = 2, Name = "grim", SteamId64 = Id(Grim), Team = i % 2 == 0 ? 3 : 2 }],
            MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = (i + 1) * TimeSpan.TicksPerDay, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        if (stamped)
        {
            record.PackStamps.Add(new PackStamp(Stamp, StampedFacts.Schema, "fp") { ComputedAtTicks = writtenAt });
        }

        return record;
    }

    // One active row for the account: kills grow with the demo so pools differ by window.
    private static FactTable Facet(int i, int kills) => new(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match",
        ["player_slot", "steam_id", "team"], ["rounds", "TotalK", "TotalD", "EnemyDmg", "CTW", "CTL"], new Dictionary<string, string>(),
        [
            new FactRow(
                new Dictionary<string, FactValue> { ["player_slot"] = FactValue.Of(2L), ["steam_id"] = FactValue.Of(Id(Grim)), ["team"] = FactValue.Of(3L) },
                new Dictionary<string, FactValue>
                {
                    ["rounds"] = FactValue.Of(20L), ["TotalK"] = FactValue.Of((long)kills), ["TotalD"] = FactValue.Of(10L),
                    ["EnemyDmg"] = FactValue.Of(1600L + i), ["CTW"] = FactValue.Of(i % 3 == 0 ? 13L : 7L), ["CTL"] = FactValue.Of(10L)
                })
        ]);

    private static (DemoCacheStore Store, PlayerRowCache Cache, ConcurrentQueue<string> Reads) Library(int demos, Func<int, bool>? stamped = null)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < demos; i++)
            {
                store.Upsert(Demo(i, stamped?.Invoke(i) ?? true));
            }
        }

        ConcurrentQueue<string> reads = new();
        PlayerRowCache cache = new(store, new PlayerDirectory(store), path =>
        {
            reads.Enqueue(path);
            int i = int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);
            return store.TryGetIndex(path)?.Stamp(Stamp) is { ComputedAtTicks: var at } ? Facet(i, i % 7 + (int)at - 1) : null;
        });
        return (store, cache, reads);
    }

    private static PlayerFilter[] Filters() =>
    [
        PlayerFilter.All, new() { Last = 10 }, new() { Last = 100 }, new() { Map = "de_nuke" }, new() { Side = 3 },
        new() { FromTicks = 200 * TimeSpan.TicksPerDay, BeforeTicks = 600 * TimeSpan.TicksPerDay }, new() { Map = "de_mirage", Side = 2, Last = 30 }
    ];

    private sealed class Recorder : IProgress<PlayerRowsProgress>
    {
        public List<PlayerRowsProgress> Reports { get; } = [];

        public void Report(PlayerRowsProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    [Test]
    public async Task AColdFill_ReportsProgress_AndAFilterChangeReadsNothing()
    {
        (_, PlayerRowCache cache, ConcurrentQueue<string> reads) = Library(1000);
        Recorder progress = new();

        await Assert.That(cache.TryGet(Grim)).IsNull();
        PlayerRows rows = await cache.LoadAsync(Grim, progress);
        int cold = reads.Count;
        List<PlayerAggregate> pools = [.. Filters().Select(f => rows.Aggregate(f))];
        PlayerRows? again = cache.TryGet(Grim);
        PlayerRows reloaded = await cache.LoadAsync(Grim);

        using (Assert.Multiple())
        {
            await Assert.That(cold).IsEqualTo(1000);
            await Assert.That(progress.Reports.First()).IsEqualTo(new PlayerRowsProgress(0, 1000));
            await Assert.That(progress.Reports.Last()).IsEqualTo(new PlayerRowsProgress(1000, 1000));
            await Assert.That(progress.Reports.Count).IsEqualTo(1001);
            await Assert.That(rows.Demos.Count).IsEqualTo(1000);
            await Assert.That(rows.Demos[0].Match.Row.Path).IsEqualTo("/lib/999.dem").Because("newest match first");
            await Assert.That(reads.Count).IsEqualTo(cold).Because("a filter change and a current load read no sidecar");
            await Assert.That(again).IsSameReferenceAs(rows);
            await Assert.That(reloaded).IsSameReferenceAs(rows);
            await Assert.That(pools[0].Matches).IsEqualTo(1000);
            await Assert.That(pools[1].Matches).IsEqualTo(10);
            await Assert.That(pools[3].Matches).IsEqualTo(250);
            await Assert.That(pools[4].Matches).IsEqualTo(500);
            await Assert.That(pools[0]["TotalK"]).IsEqualTo(rows.Demos.Sum(d => d.Stats!["TotalK"]));
            await Assert.That(pools[1]["TotalK"]).IsEqualTo(rows.Demos.Take(10).Sum(d => d.Stats!["TotalK"]));
            await Assert.That(pools[0].Wins + pools[0].Losses).IsEqualTo(1000);
        }
    }

    [Test]
    public async Task AFactsWrite_ReReadsOnlyItsDemo()
    {
        (DemoCacheStore store, PlayerRowCache cache, ConcurrentQueue<string> reads) = Library(20);
        PlayerRows before = await cache.LoadAsync(Grim);
        double kills = before.Demos.Single(d => d.Match.Row.Path == "/lib/5.dem").Stats!["TotalK"];
        reads.Clear();

        store.Upsert(Demo(5, writtenAt: 2));
        PlayerRows? stale = cache.TryGet(Grim);
        PlayerRows after = await cache.LoadAsync(Grim);

        using (Assert.Multiple())
        {
            await Assert.That(stale).IsNull().Because("the index moved");
            await Assert.That(reads).IsEquivalentTo(["/lib/5.dem"]);
            await Assert.That(after.Demos.Single(d => d.Match.Row.Path == "/lib/5.dem").Stats!["TotalK"]).IsEqualTo(kills + 1);
            await Assert.That(after.Demos.Single(d => d.Match.Row.Path == "/lib/6.dem").Stats)
                .IsSameReferenceAs(before.Demos.Single(d => d.Match.Row.Path == "/lib/6.dem").Stats);
        }
    }

    [Test]
    public async Task ADemoWithNoFacet_IsPending_AndIsNotRead()
    {
        (DemoCacheStore store, PlayerRowCache cache, ConcurrentQueue<string> reads) = Library(6, i => i != 2);
        DemoCacheRecord failed = Demo(4, false);
        failed.PackStamps.Add(new PackStamp(Stamp, StampedFacts.Schema, "fp") { State = DemoAnalysisState.Failed });
        store.Upsert(failed);

        PlayerRows rows = await cache.LoadAsync(Grim);
        PlayerDemo pending = rows.Demos.Single(d => d.Match.Row.Path == "/lib/2.dem");
        PlayerAggregate pool = rows.Aggregate();

        using (Assert.Multiple())
        {
            await Assert.That(reads.Count).IsEqualTo(4);
            await Assert.That(pending.HasFacet).IsFalse();
            await Assert.That(pending.Stats).IsNull();
            await Assert.That(pending.Outcome).IsNull();
            await Assert.That(rows.Demos.Single(d => d.Match.Row.Path == "/lib/4.dem").HasFacet).IsFalse();
            await Assert.That((pool.Matches, pool.WithStats)).IsEqualTo((6, 4));
        }
    }

    [Test]
    public async Task ACancelledFill_KeepsWhatItRead()
    {
        (_, PlayerRowCache cache, ConcurrentQueue<string> reads) = Library(50);
        using CancellationTokenSource cancel = new();
        Recorder progress = new();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.LoadAsync(Grim, new Stopper(cancel, 10), cancel.Token));
        int first = reads.Count;
        await cache.LoadAsync(Grim, progress);

        using (Assert.Multiple())
        {
            await Assert.That(first).IsEqualTo(10);
            await Assert.That(reads.Count).IsEqualTo(50);
            await Assert.That(progress.Reports.First()).IsEqualTo(new PlayerRowsProgress(0, 40));
        }
    }

    private sealed class Stopper(CancellationTokenSource cancel, int after) : IProgress<PlayerRowsProgress>
    {
        public void Report(PlayerRowsProgress value)
        {
            if (value.Done == after)
            {
                cancel.Cancel();
            }
        }
    }

    [Test]
    [Category("Budget")]
    public async Task AFilterChange_OverAThousandDemos_TakesUnder50Ms()
    {
        (_, PlayerRowCache cache, ConcurrentQueue<string> reads) = Library(1000);
        PlayerRows rows = await cache.LoadAsync(Grim);
        int cold = reads.Count;
        PlayerFilter[] filters = Filters();
        foreach (PlayerFilter filter in filters)
        {
            rows.Aggregate(filter);
        }

        // The median of five: a collection landing inside one run is the test host's heap, not the pool.
        List<double> runs = [];
        for (int run = 0; run < 5; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            PlayerRows current = cache.TryGet(Grim)!;
            current.Aggregate(filters[run % filters.Length]);
            runs.Add(watch.Elapsed.TotalMilliseconds);
        }

        double median = runs.Order().ElementAt(2);
        Console.WriteLine($"1k demos: median {median:F2} ms of [{string.Join(", ", runs.Select(r => r.ToString("F2", CultureInfo.InvariantCulture)))}]");
        using (Assert.Multiple())
        {
            await Assert.That(median).IsLessThan(50);
            await Assert.That(reads.Count).IsEqualTo(cold);
        }
    }
}
