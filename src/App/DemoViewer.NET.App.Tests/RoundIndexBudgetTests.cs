#region

using System.Diagnostics;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The corpus budget: a synthetic library in the design's §2.6 shape (24 rounds of 70 rows per
///     demo, tokens from a 28-place vocabulary with a 35 percent per-second change rate) written to a
///     temp cache root, loaded in under five seconds and queried in under fifty milliseconds, both with
///     headroom on the CI runner. The measured figures were 204 ms for a hundred real demos and 1.5 s
///     for a thousand synthetic ones; the corpus here is three hundred demos.
/// </summary>
[NotInParallel]
[Category("Budget")]
public class RoundIndexBudgetTests
{
    private const int Demos = 300;
    private const int Rounds = 24;
    private const int RowsPerRound = 70;

    private static readonly string[] _places =
    [
        "Admin", "BombsiteA", "BombsiteB", "CTSpawn", "Catwalk", "Control", "Crane", "Decon", "Garage", "Heaven",
        "Hell", "Hut", "HutRoof", "Lobby", "LockerRoom", "Mini", "Observation", "Outside", "Rafters", "Ramp", "Roof",
        "Secret", "Silo", "Squeaky", "TSpawn", "Trophy", "Tunnels", "Vending"
    ];

    [Test]
    public async Task ThreeHundredDemos_LoadInUnderFiveSeconds_AndQueryInUnderFiftyMilliseconds()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-roundindex-corpus-{Guid.NewGuid():N}");
        try
        {
            Random random = new(2026);
            DemoCacheStore cache = new(root);
            using (RoundIndexStore writer = new(root, cache))
            using (cache.BeginBatch())
            {
                for (int d = 0; d < Demos; d++)
                {
                    Indexed(cache, writer, $"/corpus/match{d:D4}.dem", Generate(random), modifiedTicks: d);
                }
            }

            cache.SaveIndex();

            // A fresh store over the same root: the startup path, sidecars read from disk.
            DemoCacheStore reopened = new(root);
            using RoundIndexStore sidecars = new(root, reopened);
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            using SituationIndex index = new(reopened, sidecars, sources);

            Stopwatch load = Stopwatch.StartNew();
            index.Load();
            load.Stop();

            SituationQuery query = new("de_nuke", [new PlaceQuery("BombsiteA", 2), new PlaceQuery("Outside", 1)],
                [new PlaceQuery("Ramp", 2)], SituationTolerance.Adjacent);
            index.Count(query); // warm
            Stopwatch lookup = Stopwatch.StartNew();
            int count = index.Count(query);
            lookup.Stop();

            Console.WriteLine($"corpus: {index.IndexedDemoCount} demos loaded in {load.ElapsedMilliseconds} ms " +
                              $"({load.Elapsed.TotalMilliseconds / Demos:F2} ms per demo); " +
                              $"tolerant query {count} hits in {lookup.Elapsed.TotalMilliseconds:F3} ms");

            using (Assert.Multiple())
            {
                await Assert.That(index.IndexedDemoCount).IsEqualTo(Demos);
                await Assert.That(load.ElapsedMilliseconds).IsLessThan(5000);
                await Assert.That(lookup.ElapsedMilliseconds).IsLessThan(50);
                await Assert.That(index.Count(query)).IsEqualTo(index.Query(query).Count);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Tokens drawn from a Zipf-shaped vocabulary: place i is picked with weight 1 / (i + 1).
    private static RoundIndexDocument Generate(Random random)
    {
        List<(int, int, int, RoundIndexRun[])> rounds = [];
        for (int r = 1; r <= Rounds; r++)
        {
            List<RoundIndexRun> runs = [];
            string ct = Token(random);
            string t = Token(random);
            int from = 0;
            for (int step = 1; step <= RowsPerRound; step++)
            {
                bool change = step == RowsPerRound || random.NextDouble() < 0.35;
                if (!change)
                {
                    continue;
                }

                runs.Add(new RoundIndexRun(from, step - 1, ct, t));
                from = step;
                ct = Token(random);
                t = Token(random);
            }

            int freezeEnd = 2000 + r * 8000;
            rounds.Add((r, freezeEnd, freezeEnd + RowsPerRound * 64, [.. runs]));
        }

        RoundIndexDocument document = Document("de_nuke", "ri1;cadence=1;token=1;rf=1;src=pawn", [.. rounds]);
        for (int i = 0; i < _places.Length - 1; i++)
        {
            document.Transitions.Add(new PlaceTransition(_places[i], _places[i + 1], random.Next(1, 40)));
        }

        return document;
    }

    private static string Token(Random random)
    {
        int alive = random.Next(1, 6);
        return PlaceCountToken.EncodePlaces(Enumerable.Range(0, alive).Select(_ => _places[Zipf(random)]));
    }

    private static int Zipf(Random random)
    {
        double total = 0;
        for (int i = 0; i < _places.Length; i++)
        {
            total += 1.0 / (i + 1);
        }

        double pick = random.NextDouble() * total;
        for (int i = 0; i < _places.Length; i++)
        {
            pick -= 1.0 / (i + 1);
            if (pick <= 0)
            {
                return i;
            }
        }

        return _places.Length - 1;
    }
}
