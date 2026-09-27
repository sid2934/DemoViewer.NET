#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The index over a seeded synthetic library of several maps: a fixed query battery must print the same text
///     as the index did before its storage was slimmed (the digests), a removal must leave the index answering
///     exactly as a fresh index over the remaining demos, and a remove-then-re-add must answer as the first load.
/// </summary>
public class SituationIndexEquivalenceTests
{
    private const string Fingerprint = "ri1;cadence=1;token=1;rf=1;src=pawn;pos=2";

    private static readonly string[] Maps = ["de_nuke", "de_mirage", "de_inferno"];

    private static readonly string[] Places =
    [
        "CTSpawn", "TSpawn", "BombsiteA", "BombsiteB", "Outside", "Ramp", "Lobby", "Hell", "Secret", "Vents", "Heaven",
        "Silo", "Garage", "Mini", "Squeaky", "Hut", "Trophy", "Radio", "Upper", "Lower"
    ];

    // Captured from the index before the slimming; any change here is a behaviour change.
    private const string FullDigest = "005f28b12246ce8f8b7edc4a863e52225b5357756409bb45feea4dc41c4e1ca3";
    private const string RemovedDigest = "23ef32a851a9d8fb11f4ce6208ded8726fe51dcc395dcee646769f0c3b6a85bf";

    private static string DemoPath(int n) => $"/lib/{n:D4}.dem";

    private static string Token(Random random, int alive, int spread) =>
        PlaceCountToken.EncodePlaces(Enumerable.Range(0, alive).Select(_ => Places[random.Next(spread)]));

    internal static RoundIndexDocument SyntheticDocument(Random random, string map, int rounds, int runsPerRound, int spread)
    {
        List<(int, int, int, RoundIndexRun[])> list = [];
        for (int r = 1; r <= rounds; r++)
        {
            int freezeEnd = 1000 + r * 8000 + random.Next(100);
            List<RoundIndexRun> runs = [];
            int step = 0;
            for (int k = 0; k < runsPerRound; k++)
            {
                int length = random.Next(1, 6);
                runs.Add(new RoundIndexRun(step, step + length - 1, Token(random, random.Next(0, 6), spread),
                    Token(random, random.Next(0, 6), spread)));
                step += length;
            }

            list.Add((r, freezeEnd, freezeEnd + step * 64 + 64, [.. runs]));
        }

        RoundIndexDocument document = Document(map, Fingerprint, [.. list]);
        for (int p = 0; p < 6; p++)
        {
            string place = Places[random.Next(spread)];
            document.Places[place] = new PlaceSampleSummary
            {
                Count = random.Next(1, 40),
                Buckets =
                [
                    new PlaceZBucketSum(random.Next(-2, 3) * 64, random.Next(1, 20), random.Next(-3000, 3000), random.Next(-3000, 3000)),
                    new PlaceZBucketSum(random.Next(-2, 3) * 64, random.Next(1, 20), random.Next(-3000, 3000), random.Next(-3000, 3000))
                ]
            };
        }

        for (int t = 0; t < 8; t++)
        {
            document.Transitions.Add(new PlaceTransition(Places[random.Next(spread)], Places[random.Next(spread)], random.Next(1, 5)));
        }

        return document;
    }

    private static List<(string Path, RoundIndexDocument Document, long Computed, long Modified)> Library(int demos, int seed)
    {
        Random random = new(seed);
        return
        [
            .. Enumerable.Range(0, demos).Select(n => (DemoPath(n),
                SyntheticDocument(random, Maps[n % Maps.Length], random.Next(3, 10), random.Next(2, 7), 12),
                (long)(100 + n * 7 % 50), (long)(1000 + n)))
        ];
    }

    private static SituationIndex Build(DemoCacheStore cache, RoundIndexStore sidecars,
        IEnumerable<(string Path, RoundIndexDocument Document, long Computed, long Modified)> demos)
    {
        foreach ((string path, RoundIndexDocument document, long computed, long modified) in demos)
        {
            Indexed(cache, sidecars, path, document, computed, modified);
        }

        SituationIndex index = new(cache, sidecars, new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn));
        index.Load();
        return index;
    }

    // Every tolerance, one side, both sides, neither, and the demo and watermark filters; then the place
    // summaries and the empirical graph. Hits print whole so a moved window shows, not just a count.
    private static string Battery(SituationIndex index)
    {
        Random random = new(99);
        StringBuilder text = new();
        HashSet<string> someDemos = [.. Enumerable.Range(0, 400).Where(n => n % 3 == 0).Select(n => DemoCacheStore.StableKey(DemoPath(n)))];
        foreach (string map in Maps)
        {
            for (int q = 0; q < 60; q++)
            {
                PlaceQuery[] Pairs() =>
                [
                    .. Enumerable.Range(0, random.Next(1, 3)).Select(_ => new PlaceQuery(Places[random.Next(14)], random.Next(1, 4)))
                ];

                int shape = q % 4;
                PlaceQuery[] ct = shape is 0 or 2 ? Pairs() : [];
                PlaceQuery[] t = shape is 1 or 2 ? Pairs() : [];
                SituationTolerance tolerance = (SituationTolerance)(q / 4 % 4);
                SituationQuery query = new(map, ct, t, tolerance,
                    Demos: q % 7 == 3 ? someDemos : null,
                    IndexedAfterTicks: q % 11 == 5 ? 120 : null);
                IReadOnlyList<SituationHit> hits = index.Query(query);
                text.Append(CultureInfo.InvariantCulture, $"{map} q{q} {tolerance} n={hits.Count} c={index.Count(query)}\n");
                foreach (SituationHit h in hits)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $"  {h.DemoPath} {h.DemoStableKey} {h.Map} r{h.RoundNumber} {h.FreezeEndTick} {h.FirstMatchTick} {h.LastMatchTick} {h.MatchedSteps}\n");
                }
            }

            foreach (PlaceSummary summary in index.Places(map))
            {
                text.Append(CultureInfo.InvariantCulture, $"{map} place {summary.Place} {summary.SampleCount}:");
                foreach (PlaceZBucket b in summary.Buckets)
                {
                    text.Append(CultureInfo.InvariantCulture, $" {b.ZBucket}/{b.Count}/{b.CentroidX:R}/{b.CentroidY:R}");
                }

                text.Append('\n');
            }

            if (index.Adjacency(map) is { } adjacency)
            {
                text.Append(CultureInfo.InvariantCulture, $"{map} graph {adjacency.Source}\n");
                foreach (string place in Places)
                {
                    text.Append(CultureInfo.InvariantCulture, $"  {place}: {string.Join(",", adjacency.Neighbours(place).Order(StringComparer.Ordinal))}\n");
                }
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"demos={index.IndexedDemoCount}\n");
        return text.ToString();
    }

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Test]
    public async Task TheBattery_AnswersAsBefore_AndARemovalLeavesExactlyTheRemainingDemos()
    {
        List<(string Path, RoundIndexDocument Document, long Computed, long Modified)> library = Library(90, 7);
        DemoCacheStore cache = new(null);
        using RoundIndexStore sidecars = new(null, cache);
        using SituationIndex index = Build(cache, sidecars, library);
        string full = Battery(index);

        HashSet<int> removed = [.. Enumerable.Range(0, 90).Where(n => n % 3 == 1 || n % 10 == 0)];
        foreach (int n in removed)
        {
            cache.Remove(DemoPath(n));
        }

        string afterRemoval = Battery(index);

        DemoCacheStore freshCache = new(null);
        using RoundIndexStore freshSidecars = new(null, freshCache);
        using SituationIndex fresh = Build(freshCache, freshSidecars, library.Where((_, n) => !removed.Contains(n)));
        string remaining = Battery(fresh);

        // Load again: every demo leaves and comes back, the removed ones included, so freed token slots are reused.
        foreach (int n in removed)
        {
            Indexed(cache, sidecars, library[n].Path, library[n].Document, library[n].Computed, library[n].Modified);
        }

        index.Load();
        string reloaded = Battery(index);

        Console.WriteLine($"[situation] full={Digest(full)} removed={Digest(afterRemoval)}");
        using (Assert.Multiple())
        {
            await Assert.That(afterRemoval).IsEqualTo(remaining).Because("a removal subtracts exactly what the demo added");
            await Assert.That(reloaded).IsEqualTo(full).Because("a re-added demo answers as it did on the first load");
            if (FullDigest.Length > 0)
            {
                await Assert.That(Digest(full)).IsEqualTo(FullDigest);
                await Assert.That(Digest(afterRemoval)).IsEqualTo(RemovedDigest);
            }
        }
    }

    [Test]
    [Category("Environmental")]
    public async Task RetainedBytes_OverAFewHundredDemos()
    {
        if (Environment.GetEnvironmentVariable("SITUATION_MEM_PROBE") is not "1")
        {
            throw new SkipTestException("SITUATION_MEM_PROBE not set");
        }

        const int demos = 366;
        Random random = new(11);
        DemoCacheStore cache = new(null);
        using RoundIndexStore sidecars = new(null, cache);
        for (int n = 0; n < demos; n++)
        {
            Indexed(cache, sidecars, DemoPath(n), SyntheticDocument(random, Maps[n % Maps.Length], 24, 14, Places.Length), 100, 1000 + n);
        }

        using SituationIndex index = new(cache, sidecars, new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn));
        long before = GC.GetTotalMemory(true);
        index.Load();
        long after = GC.GetTotalMemory(true);
        Console.WriteLine($"[situation] {demos} demos retained {(after - before) / 1024.0 / 1024.0:0.00} MB");
        await Assert.That(index.IndexedDemoCount).IsEqualTo(demos);
        GC.KeepAlive(index);
    }
}
