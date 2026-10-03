#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The in-memory index over hand-built documents: every tolerance level, monotonicity across
///     levels and across added pairs (property-style over random tokens), two-sided overlap, the
///     count equalling the query count in every case, the demo, watermark and Round Facts filters,
///     ordering, readiness, the incremental merge and drop, the stale count, the zone graph winning
///     over the empirical one, the place summaries, and a sidecar with another demo's hash ignored.
/// </summary>
public class SituationQueryTests
{
    private const string Fingerprint = "ri1;cadence=1;token=1;rf=1;src=pawn;pos=2";
    private const string DemoA = "/d/a.dem";
    private const string DemoB = "/d/b.dem";
    private const string DemoC = "/d/c.dem";

    private static RoundIndexDocument DocA()
    {
        RoundIndexDocument document = Document("de_nuke", Fingerprint,
            (1, 10746, 17138,
            [
                new RoundIndexRun(0, 4, "CTSpawn:5", "TSpawn:5"),
                new RoundIndexRun(5, 9, "BombsiteA:2|Outside:3", "Lobby:3|Ramp:2"),
                new RoundIndexRun(10, 12, "BombsiteA:2|Outside:2", "Lobby:3|Ramp:1")
            ]),
            (2, 20000, 24000, [new RoundIndexRun(0, 3, "BombsiteA:3|Hell:2", "Ramp:5")]));
        document.Transitions = [new PlaceTransition("BombsiteA", "Hell", 4), new PlaceTransition("Hell", "Ramp", 3), new PlaceTransition("Outside", "Ramp", 1)];
        document.Places["BombsiteA"] = new PlaceSampleSummary
        {
            Count = 4,
            Buckets = [new PlaceZBucketSum(0, 4, 400, 800)]
        };
        return document;
    }

    private static RoundIndexDocument DocB()
    {
        RoundIndexDocument document = Document("de_nuke", Fingerprint,
            (1, 5000, 9000, [new RoundIndexRun(0, 2, "BombsiteA:4|Outside:1", "Lobby:3|Ramp:2")]));
        document.Transitions = [new PlaceTransition("Outside", "Ramp", 2)];
        document.Places["BombsiteA"] = new PlaceSampleSummary
        {
            Count = 2,
            Buckets = [new PlaceZBucketSum(0, 2, 100, 200), new PlaceZBucketSum(-512, 0, 0, 0)]
        };
        return document;
    }

    private static RoundIndexDocument DocC() =>
        Document("de_dust2", Fingerprint, (1, 3000, 6000, [new RoundIndexRun(0, 1, "BombsiteA:2", "Ramp:3")]));

    private static Harness Load(IZonePlaceResolverSource? zones = null, IRoundFactsSource? facts = null)
    {
        Harness h = new(zones, facts);
        Indexed(h.Cache, h.Sidecars, DemoA, DocA(), computedAt: 100, modifiedTicks: 30);
        Indexed(h.Cache, h.Sidecars, DemoB, DocB(), computedAt: 200, modifiedTicks: 20);
        Indexed(h.Cache, h.Sidecars, DemoC, DocC(), computedAt: 300, modifiedTicks: 10);
        h.Index.Load();
        return h;
    }

    private static SituationQuery Q(string map, PlaceQuery[]? ct = null, PlaceQuery[]? t = null,
        SituationTolerance tolerance = SituationTolerance.Exact) =>
        new(map, ct ?? [], t ?? [], tolerance);

    private static async Task<int> CountAndCheck(SituationIndex index, SituationQuery query)
    {
        IReadOnlyList<SituationHit> hits = index.Query(query);
        await Assert.That(index.Count(query)).IsEqualTo(hits.Count).Because("Count is Query with the materialisation skipped");
        return hits.Count;
    }

    [Test]
    public async Task Exact_MeansExactlyN_AndBoundsTheMatchedSteps()
    {
        using Harness h = Load();
        SituationQuery query = Q("de_nuke", ct: [new PlaceQuery("BombsiteA", 2)]);
        IReadOnlyList<SituationHit> hits = h.Index.Query(query);

        using (Assert.Multiple())
        {
            await Assert.That(h.Index.IsReady).IsTrue();
            await Assert.That(await CountAndCheck(h.Index, query)).IsEqualTo(1).Because("B has four in A and C is another map");
            await Assert.That(hits[0].DemoPath).IsEqualTo(DemoA);
            await Assert.That(hits[0].DemoStableKey).IsEqualTo(DemoCacheStore.StableKey(DemoA));
            await Assert.That(hits[0].RoundNumber).IsEqualTo(1);
            await Assert.That(hits[0].FreezeEndTick).IsEqualTo(10746);
            await Assert.That(hits[0].FirstMatchTick).IsEqualTo(10746 + 5 * 64);
            await Assert.That(hits[0].LastMatchTick).IsEqualTo(10746 + 12 * 64);
            await Assert.That(hits[0].MatchedSteps).IsEqualTo(8);
            await Assert.That(hits[0].Map).IsEqualTo("de_nuke");
        }
    }

    [Test]
    public async Task TwoSides_MustMatchAtTheSameStep()
    {
        using Harness h = Load();
        SituationQuery overlap = Q("de_nuke", ct: [new PlaceQuery("BombsiteA", 2)], t: [new PlaceQuery("Ramp", 1)]);
        SituationQuery disjoint = Q("de_nuke", ct: [new PlaceQuery("BombsiteA", 2)], t: [new PlaceQuery("Ramp", 5)]);
        IReadOnlyList<SituationHit> hits = h.Index.Query(overlap);

        using (Assert.Multiple())
        {
            await Assert.That(await CountAndCheck(h.Index, overlap)).IsEqualTo(1);
            await Assert.That(hits[0].MatchedSteps).IsEqualTo(3).Because("only steps 10 to 12 hold both tokens");
            await Assert.That(hits[0].FirstMatchTick).IsEqualTo(10746 + 10 * 64);
            await Assert.That(await CountAndCheck(h.Index, disjoint)).IsEqualTo(0)
                .Because("Ramp:5 is in round 2, where the CTs are BombsiteA:3; the sides never coincide");
        }
    }

    [Test]
    public async Task TheCountNeverFalls_AsTheSliderLoosens()
    {
        using Harness h = Load();
        PlaceQuery[] three = [new PlaceQuery("BombsiteA", 3)];
        int exact = await CountAndCheck(h.Index, Q("de_nuke", three));
        int adjacent = await CountAndCheck(h.Index, Q("de_nuke", three, tolerance: SituationTolerance.Adjacent));
        int twoHops = await CountAndCheck(h.Index, Q("de_nuke", three, tolerance: SituationTolerance.TwoHops));
        int any = await CountAndCheck(h.Index, Q("de_nuke", three, tolerance: SituationTolerance.AnyPlace));

        using (Assert.Multiple())
        {
            await Assert.That(exact).IsEqualTo(1).Because("only A round 2 has exactly three in A");
            await Assert.That(adjacent).IsEqualTo(2).Because("A round 2 sums five over A and Hell; B round 1 has four in A");
            await Assert.That(twoHops).IsEqualTo(2).Because("Ramp joins the neighbourhood but no CT stands there");
            await Assert.That(any).IsEqualTo(3).Because("every nuke round has at least three CTs alive");
            await Assert.That(h.Index.Adjacency("de_nuke")!.Source).IsEqualTo("index:2");
            await Assert.That(h.Index.Adjacency("de_nuke")!.Neighbours("Outside")).Contains("Ramp")
                .Because("one transition in A and two in B fold to the threshold");
        }
    }

    [Test]
    public async Task WithoutAGraph_TheMiddleStopsCollapseToExact()
    {
        using Harness h = Load();
        // dust2 has one demo with no transitions: an empirical graph with no edges, so Adjacent equals Exact.
        PlaceQuery[] two = [new PlaceQuery("BombsiteA", 2)];
        int exact = await CountAndCheck(h.Index, Q("de_dust2", two));
        int adjacent = await CountAndCheck(h.Index, Q("de_dust2", two, tolerance: SituationTolerance.Adjacent));

        await Assert.That(exact).IsEqualTo(1);
        await Assert.That(adjacent).IsEqualTo(1);
        await Assert.That(h.Index.Adjacency("de_train")).IsNull().Because("a map with no indexed demo has no graph");
    }

    [Test]
    public async Task Monotonicity_HoldsOverRandomTokens_AcrossLevelsAndAddedPairs()
    {
        Random random = new(20260924);
        string[] places = ["A", "B", "C", "D", "E", "F"];
        Harness h = new(null, null);
        for (int d = 0; d < 6; d++)
        {
            List<(int, int, int, RoundIndexRun[])> rounds = [];
            for (int r = 1; r <= 5; r++)
            {
                List<RoundIndexRun> runs = [];
                int step = 0;
                while (step < 20)
                {
                    int length = random.Next(1, 6);
                    runs.Add(new RoundIndexRun(step, step + length - 1, RandomToken(random, places), RandomToken(random, places)));
                    step += length;
                }

                rounds.Add((r, 1000 * r, 1000 * r + 900, [.. runs]));
            }

            RoundIndexDocument document = Document("de_rand", Fingerprint, [.. rounds]);
            for (int i = 0; i < places.Length - 1; i++)
            {
                document.Transitions.Add(new PlaceTransition(places[i], places[i + 1], 3));
            }

            Indexed(h.Cache, h.Sidecars, $"/d/rand{d}.dem", document, modifiedTicks: d);
        }

        h.Index.Load();
        using (h)
        {
            for (int trial = 0; trial < 60; trial++)
            {
                PlaceQuery[] ct = RandomPairs(random, places);
                PlaceQuery[] t = random.Next(2) == 0 ? [] : RandomPairs(random, places);
                int exact = await CountAndCheck(h.Index, Q("de_rand", ct, t));
                int adjacent = await CountAndCheck(h.Index, Q("de_rand", ct, t, SituationTolerance.Adjacent));
                int twoHops = await CountAndCheck(h.Index, Q("de_rand", ct, t, SituationTolerance.TwoHops));
                int any = await CountAndCheck(h.Index, Q("de_rand", ct, t, SituationTolerance.AnyPlace));

                await Assert.That(exact).IsLessThanOrEqualTo(adjacent).Because($"trial {trial}: exact implies adjacent");
                await Assert.That(adjacent).IsLessThanOrEqualTo(twoHops).Because($"trial {trial}: adjacent implies two hops");
                await Assert.That(twoHops).IsLessThanOrEqualTo(any).Because($"trial {trial}: two hops implies any place");

                PlaceQuery[] more = [.. ct, new PlaceQuery(places[random.Next(places.Length)], random.Next(1, 3))];
                foreach (SituationTolerance level in Enum.GetValues<SituationTolerance>())
                {
                    int before = await CountAndCheck(h.Index, Q("de_rand", ct, t, level));
                    int after = await CountAndCheck(h.Index, Q("de_rand", more, t, level));
                    await Assert.That(after).IsLessThanOrEqualTo(before).Because($"trial {trial} at {level}: a pair added never widens the set");
                }
            }
        }
    }

    [Test]
    public async Task TheDemoAndWatermarkFilters_NarrowTheSameHitSet()
    {
        using Harness h = Load();
        PlaceQuery[] anyA = [new PlaceQuery("BombsiteA", 1)];
        SituationQuery all = Q("de_nuke", anyA, tolerance: SituationTolerance.AnyPlace);
        SituationQuery onlyB = all with
        {
            Demos = new HashSet<string>([DemoCacheStore.StableKey(DemoB)], StringComparer.Ordinal)
        };
        SituationQuery after150 = all with
        {
            IndexedAfterTicks = 150
        };

        using (Assert.Multiple())
        {
            await Assert.That(await CountAndCheck(h.Index, all)).IsEqualTo(3);
            await Assert.That(await CountAndCheck(h.Index, onlyB)).IsEqualTo(1);
            await Assert.That(h.Index.Query(onlyB)[0].DemoPath).IsEqualTo(DemoB);
            await Assert.That(await CountAndCheck(h.Index, after150)).IsEqualTo(1).Because("only B was stamped after the watermark");
            await Assert.That(h.Index.Query(after150)[0].DemoPath).IsEqualTo(DemoB);
        }
    }

    [Test]
    public async Task ARoundFactsFilter_JoinsByPathAndRoundNumber()
    {
        FakeFacts facts = new();
        facts.Rows[DemoA] = Facts(Round(1, 10746, 17138), Round(2, 20000, 24000));
        facts.Rows[DemoA].Rounds[1].WinnerSide = 2;
        using Harness h = Load(facts: facts);
        SituationQuery tWins = Q("de_nuke", tolerance: SituationTolerance.AnyPlace) with
        {
            Facts = new RoundFactsFilter
            {
                WinnerSide = 2
            }
        };
        SituationQuery anyFacts = Q("de_nuke", tolerance: SituationTolerance.AnyPlace) with
        {
            Facts = new RoundFactsFilter()
        };

        using (Assert.Multiple())
        {
            await Assert.That(await CountAndCheck(h.Index, Q("de_nuke"))).IsEqualTo(3).Because("both sides free matches every round");
            await Assert.That(await CountAndCheck(h.Index, anyFacts)).IsEqualTo(2).Because("B has no rows, so a facts filter excludes it");
            await Assert.That(await CountAndCheck(h.Index, tWins)).IsEqualTo(1);
            await Assert.That(h.Index.Query(tWins)[0].RoundNumber).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Hits_AreNewestDemoFirst_ThenRoundNumber()
    {
        using Harness h = Load();
        IReadOnlyList<SituationHit> hits = h.Index.Query(Q("de_nuke"));

        using (Assert.Multiple())
        {
            await Assert.That(hits.Select(x => (x.DemoPath, x.RoundNumber)))
                .IsEquivalentTo([(DemoA, 1), (DemoA, 2), (DemoB, 1)]);
            await Assert.That(hits[0].DemoPath).IsEqualTo(DemoA).Because("A was modified last");
            await Assert.That(hits[2].DemoPath).IsEqualTo(DemoB);
        }
    }

    [Test]
    public async Task BeforeTheLoad_QueriesAreEmpty_AndTheIndexIsNotReady()
    {
        Harness h = new(null, null);
        Indexed(h.Cache, h.Sidecars, DemoA, DocA());
        using (h)
        {
            await Assert.That(h.Index.IsReady).IsFalse();
            await Assert.That(h.Index.Query(Q("de_nuke"))).IsEmpty();
            await Assert.That(h.Index.IndexedDemoCount).IsEqualTo(0);

            int changed = 0;
            h.Index.Changed += () => changed++;
            h.Index.Load();

            await Assert.That(h.Index.IsReady).IsTrue();
            await Assert.That(changed).IsEqualTo(1);
            await Assert.That(h.Index.IndexedDemoCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheEvaluatorsWrite_MergesIncrementally_AndARemovalDrops()
    {
        Harness h = new(null, null);
        Indexed(h.Cache, h.Sidecars, DemoA, DocA());
        h.Index.Load();
        RoundIndexEvaluator evaluator = new(h.Cache, h.Sidecars, h.Sources, () => true, walk: _ => []);
        using SituationIndex index = new(h.Cache, h.Sidecars, h.Sources, evaluator: evaluator);
        index.Load();
        List<RoundIndexedEvent> merged = [];
        index.Indexed += merged.Add;

        // A parsed record with rows; the synthetic parse walks no samples, so the document has rounds
        // and no runs, which is enough to be merged.
        h.Cache.Upsert(ParsedRecord(DemoB, facts: Facts(Round(1, 1000, 2000))));
        evaluator.Evaluate(DemoB, Demo());

        using (Assert.Multiple())
        {
            await Assert.That(index.IndexedDemoCount).IsEqualTo(2);
            await Assert.That(merged.Count).IsEqualTo(1);
            await Assert.That(merged[0].DemoPath).IsEqualTo(DemoB);
            await Assert.That(index.Count(Q("de_nuke"))).IsEqualTo(2).Because("A's two rounds; B's round has no sampled step");
        }

        h.Cache.Remove(DemoA);
        using (Assert.Multiple())
        {
            await Assert.That(index.IndexedDemoCount).IsEqualTo(1);
            await Assert.That(index.Count(Q("de_nuke"))).IsEqualTo(0);
            await Assert.That(index.Places("de_nuke")).IsEmpty().Because("A's samples were subtracted");
        }

        // A replaced file: identity drift drops every tier, so the row loses its stamp and the index drops it.
        h.Cache.Upsert(ParsedRecord(DemoB));
        await Assert.That(index.IndexedDemoCount).IsEqualTo(0);
        h.Dispose();
    }

    [Test]
    public async Task AStaleFingerprint_StillLoads_AndIsCounted()
    {
        Harness h = new(null, null);
        RoundIndexDocument stale = DocA();
        stale.Fingerprint = "ri1;cadence=2;token=1;rf=1;src=pawn;pos=1";
        Indexed(h.Cache, h.Sidecars, DemoA, stale);
        Indexed(h.Cache, h.Sidecars, DemoB, DocB());
        h.Index.Load();

        using (h)
        {
            await Assert.That(h.Index.IndexedDemoCount).IsEqualTo(2);
            await Assert.That(h.Index.StaleDemoCount).IsEqualTo(1);
            await Assert.That(h.Index.Count(Q("de_nuke", [new PlaceQuery("BombsiteA", 2)]))).IsEqualTo(1)
                .Because("the stale rows keep answering until replaced");
        }
    }

    [Test]
    public async Task TheZoneGraph_WinsWhenTheMapHasZones()
    {
        RoundIndexBuilderTests.FakeZoneResolver nuke = new("zv-1", _ => null,
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["BombsiteA"] = ["Heaven"],
                ["Heaven"] = ["BombsiteA"]
            });
        using Harness h = Load(new RoundIndexEvaluatorTests.MapZones(("de_nuke", nuke)));

        IPlaceAdjacency? nukeGraph = h.Index.Adjacency("de_nuke");
        IPlaceAdjacency? dustGraph = h.Index.Adjacency("de_dust2");

        using (Assert.Multiple())
        {
            await Assert.That(nukeGraph).IsTypeOf<ZonePlaceAdjacency>();
            await Assert.That(nukeGraph!.Source).IsEqualTo("zones:zv-1");
            await Assert.That(nukeGraph.Neighbours("BombsiteA")).Contains("Heaven");
            await Assert.That(nukeGraph.Neighbours("BombsiteA")).DoesNotContain("Hell").Because("the empirical edge is not consulted");
            await Assert.That(dustGraph).IsTypeOf<EmpiricalPlaceAdjacency>();
            await Assert.That(dustGraph!.Source).IsEqualTo("index:1");
            // Under the zone graph, three in A widens over Heaven only: A round 2 (3 in A) and B (4 in A).
            await Assert.That(await CountAndCheck(h.Index, Q("de_nuke", [new PlaceQuery("BombsiteA", 3)], tolerance: SituationTolerance.Adjacent)))
                .IsEqualTo(2);
        }
    }

    [Test]
    public async Task Places_FoldAcrossDemos_PerBucket()
    {
        using Harness h = Load();
        IReadOnlyList<PlaceSummary> places = h.Index.Places("de_nuke");
        PlaceSummary bombsite = places.Single(p => p.Place == "BombsiteA");

        using (Assert.Multiple())
        {
            await Assert.That(bombsite.SampleCount).IsEqualTo(6);
            await Assert.That(bombsite.Buckets.Count).IsEqualTo(2);
            PlaceZBucket ground = bombsite.Buckets.Single(b => b.ZBucket == 0);
            await Assert.That(ground.Count).IsEqualTo(6);
            await Assert.That(ground.CentroidX).IsEqualTo(500.0 / 6).Within(1e-9);
            await Assert.That(ground.CentroidY).IsEqualTo(1000.0 / 6).Within(1e-9);
            await Assert.That(h.Index.Places("de_train")).IsEmpty();
        }
    }

    [Test]
    public async Task ASidecarWithAnotherDemosHash_IsIgnored()
    {
        Harness h = new(null, null);
        RoundIndexDocument other = DocA();
        other.Demo.Sha256 = "def";
        Indexed(h.Cache, h.Sidecars, DemoA, other, sha: "abc");
        Indexed(h.Cache, h.Sidecars, DemoB, DocB(), sha: "same");
        RoundIndexDocument matching = DocC();
        matching.Demo.Sha256 = "same";
        Indexed(h.Cache, h.Sidecars, DemoC, matching, sha: "same");
        h.Index.Load();

        using (h)
        {
            await Assert.That(h.Index.IndexedDemoCount).IsEqualTo(2);
            await Assert.That(h.Index.Count(Q("de_nuke"))).IsEqualTo(1).Because("B loads (no hash in the file); A is another demo's file");
        }
    }

    private static string RandomToken(Random random, string[] places)
    {
        int alive = random.Next(0, 6);
        return PlaceCountToken.EncodePlaces(Enumerable.Range(0, alive).Select(_ => places[random.Next(places.Length)]));
    }

    private static PlaceQuery[] RandomPairs(Random random, string[] places)
    {
        int count = random.Next(1, 3);
        return [.. Enumerable.Range(0, count).Select(_ => new PlaceQuery(places[random.Next(places.Length)], random.Next(1, 4)))];
    }

    private sealed class Harness : IDisposable
    {
        public Harness(IZonePlaceResolverSource? zones, IRoundFactsSource? facts)
        {
            Cache = new DemoCacheStore(null);
            Sidecars = new RoundIndexStore(null, Cache);
            Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
            Index = new SituationIndex(Cache, Sidecars, Sources, facts, zones);
        }

        public DemoCacheStore Cache { get; }

        public RoundIndexStore Sidecars { get; }

        public RoundIndexPlaceSources Sources { get; }

        public SituationIndex Index { get; }

        public void Dispose()
        {
            Index.Dispose();
            Sidecars.Dispose();
        }
    }

    private sealed class FakeFacts : IRoundFactsSource
    {
        public Dictionary<string, RoundFactsRows> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Schema => DemoCacheRecord.RoundFactsSchema;

        public event Action<string>? Updated
        {
            add { }
            remove { }
        }

        public RoundFactsRows? TryGet(string demoPath) => Rows.GetValueOrDefault(demoPath);

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
            TryGet(demoPath) is { } rows ? RoundFactsSource.FindRound(rows.Rounds, frameClockTick) : null;

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];
    }
}
