#region

using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>.dvrp.json.gz</c> positions file: the builder keeps the alive tuples of every row it
///     closes (integer world units, a place id, CT slots per round, a step the walk skipped as an
///     empty list), the fingerprint carries <c>pos=</c> so an older sidecar is stale, the store writes
///     it gzipped beside the sidecar and refuses a stale or foreign file, the evaluator writes it under
///     the sidecar's fingerprint, and the v1 shape round-trips through the committed sample.
/// </summary>
[NotInParallel]
public class RoundPositionsTests
{
    private const string Demo = "/d/match.dem";
    private const string SampleName = "schema-v1.sample.dvrp.json";

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-roundpositions-{Guid.NewGuid():N}");

    // Every seated slot at a distinct spot, CT on A and T on Ramp, with fractional coordinates so the
    // rounding is visible.
    private static IEnumerable<PositionSample> Placed(int tick)
    {
        foreach (int slot in CtSlots)
        {
            yield return Sample(tick, slot, "BombsiteA", 600.4f + slot, -400.6f, -416.2f);
        }

        foreach (int slot in TSlots)
        {
            yield return Sample(tick, slot, "Ramp", 1300.5f + slot, -1000f, -700f);
        }
    }

    [Test]
    public async Task TheWalk_KeepsTheAliveTuples_PerStep_InSlotOrder_AsIntegers()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1200, kills: Kill(1064, 3)));
        List<PositionSample> samples =
        [
            .. Placed(1000),
            .. Placed(1064),
            Sample(1128, 6, null, 10.4f, 20.6f, 30f)
        ];

        RoundIndexBuild build = RoundIndexBuilder.BuildWithPositions(RoundIndexTestData.Demo(), facts,
            RoundIndexOptions.Default, PawnPlaceSource.Instance, samples);
        RoundPositionsDocument positions = build.Positions;
        RoundPositionsRound round = positions.Rounds.Single();

        using (Assert.Multiple())
        {
            await Assert.That(positions.Fingerprint).IsEqualTo(build.Index.Fingerprint);
            await Assert.That(positions.CadenceTicks).IsEqualTo(64);
            await Assert.That(positions.Places).IsEquivalentTo(["BombsiteA", "Ramp", "?"]);
            await Assert.That(round.Number).IsEqualTo(1);
            await Assert.That(round.FreezeEndTick).IsEqualTo(1000);
            await Assert.That(round.Ct).IsEquivalentTo(CtSlots);
            await Assert.That(round.Pos.Count).IsEqualTo(3);
            await Assert.That(round.At(0).Count).IsEqualTo(10);
            await Assert.That(round.At(1).Count).IsEqualTo(9).Because("slot 3 died at the sampled tick");
            await Assert.That(round.At(1).Any(p => p.Slot == 3)).IsFalse();
            await Assert.That(round.At(0).Select(p => p.Slot)).IsEquivalentTo([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
            await Assert.That(round.At(0)[0]).IsEqualTo(new RoundPosition(1, 601, -401, -416, 0));
            await Assert.That(round.At(0)[5]).IsEqualTo(new RoundPosition(6, 1307, -1000, -700, 1));
            await Assert.That(round.At(2).Single()).IsEqualTo(new RoundPosition(6, 10, 21, 30, 2));
            await Assert.That(positions.PlaceOf(2)).IsNull().Because("? is the null place");
            await Assert.That(positions.PlaceOf(0)).IsEqualTo("BombsiteA");
            await Assert.That(positions.StepFor(round, 1064)).IsEqualTo(1);
            await Assert.That(positions.StepFor(round, 1100)).IsEqualTo(1).Because("a tick inside a step maps to it");
            await Assert.That(round.At(7)).IsEmpty();
        }

        // The token the index stored for a step is the token the tuples encode to: one function,
        // applied twice, the property the cards and the overlay rely on.
        foreach (RoundIndexRow row in build.Index.ExpandRows(build.Index.Rounds[0]))
        {
            HashSet<int> ct = [.. round.Ct];
            IReadOnlyList<RoundPosition> tuples = round.At(row.Step);
            await Assert.That(PlaceCountToken.EncodePlaces(tuples.Where(t => ct.Contains(t.Slot)).Select(t => positions.PlaceOf(t.PlaceId))))
                .IsEqualTo(row.Ct);
            await Assert.That(PlaceCountToken.EncodePlaces(tuples.Where(t => !ct.Contains(t.Slot)).Select(t => positions.PlaceOf(t.PlaceId))))
                .IsEqualTo(row.T);
        }
    }

    [Test]
    public async Task AStepTheWalkSkipped_IsAnEmptyList_SoTheIndexIsTheStep()
    {
        RoundFactsRows facts = Facts(Round(1, 1000, 1400));
        List<PositionSample> samples = [.. Placed(1000), .. Placed(1128)];

        RoundPositionsDocument positions = RoundIndexBuilder.BuildWithPositions(RoundIndexTestData.Demo(), facts,
            RoundIndexOptions.Default, PawnPlaceSource.Instance, samples).Positions;
        RoundPositionsRound round = positions.Rounds.Single();

        using (Assert.Multiple())
        {
            await Assert.That(round.Pos.Count).IsEqualTo(3);
            await Assert.That(round.At(0).Count).IsEqualTo(10);
            await Assert.That(round.At(1)).IsEmpty();
            await Assert.That(round.At(2).Count).IsEqualTo(10);
        }
    }

    [Test]
    public async Task TheFingerprint_CarriesPos_AndASidecarWithoutIt_IsStale()
    {
        string current = RoundIndexFingerprint.Compose(RoundIndexOptions.Default, PawnPlaceSource.Instance);
        DemoCacheRecord record = ParsedRecord(Demo);
        record.RoundIndex = new TierStamp
        {
            Schema = DemoCacheRecord.RoundIndexSchema,
            ComputedAtTicks = 1
        };
        record.RoundIndexState = RoundIndexState.Indexed;
        record.RoundIndexFingerprint = "ri1;cadence=1;token=1;rf=1;src=pawn";

        using (Assert.Multiple())
        {
            await Assert.That(current).IsEqualTo("ri1;cadence=1;token=1;rf=1;src=pawn;pos=1");
            await Assert.That(current).EndsWith($";pos={RoundPositionsDocument.PositionSchema}");
            await Assert.That(record.IsRoundIndexCurrent(current)).IsFalse();
            await Assert.That(record.NeedsRoundIndex(current)).IsTrue()
                .Because("indexed before positions existed and never indexed are one state");
        }
    }

    [Test]
    public async Task TheStore_WritesGzippedBesideTheSidecar_ReadsBack_AndRefusesStaleOrForeignFiles()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            using RoundIndexStore store = new(root, cache);
            RoundIndexBuild build = Build();
            store.WritePositions(Demo, build.Positions);
            store.Write(Demo, build.Index);

            string expected = Path.Combine(root, "round-index", DemoCacheStore.StableKey(Demo) + ".dvrp.json.gz");
            byte[] bytes = File.ReadAllBytes(expected);
            RoundPositionsDocument? read = store.TryReadPositions(Demo);

            using (Assert.Multiple())
            {
                await Assert.That(store.PositionsPathFor(Demo)).IsEqualTo(expected);
                await Assert.That(bytes.Length).IsGreaterThan(2);
                await Assert.That((bytes[0], bytes[1])).IsEqualTo(((byte)0x1f, (byte)0x8b)).Because("gzip magic");
                await Assert.That(Directory.GetFiles(Path.Combine(root, "round-index"), "*.tmp")).IsEmpty();
                await Assert.That(read).IsNotNull();
                await Assert.That(read!.Rounds.Single().At(0).Count).IsEqualTo(10);
                await Assert.That(read.Demo.Sha256).IsEqualTo("abc");
                await Assert.That(store.TryReadPositions(Demo, build.Positions.Fingerprint, "abc")).IsNotNull();
                await Assert.That(store.TryReadPositions(Demo, "ri1;cadence=2;token=1;rf=1;src=pawn;pos=1")).IsNull()
                    .Because("a file under another fingerprint is stale, so absent");
                await Assert.That(store.TryReadPositions(Demo, null, "def")).IsNull()
                    .Because("a file naming another demo's hash is ignored");
                await Assert.That(store.TryReadPositions("/d/never.dem")).IsNull();
            }

            // A corrupt file reads as absent, like the sidecar.
            File.WriteAllBytes(expected, [1, 2, 3]);
            await Assert.That(store.TryReadPositions(Demo)).IsNull();

            // Delete takes both files; the sweep takes an orphan positions file on its own.
            store.WritePositions(Demo, build.Positions);
            store.Delete(Demo);
            await Assert.That(File.Exists(expected)).IsFalse();
            await Assert.That(File.Exists(store.PathFor(Demo)!)).IsFalse();

            store.WritePositions("/d/orphan.dem", build.Positions);
            int swept = store.SweepOrphans();
            await Assert.That(swept).IsEqualTo(1);
            await Assert.That(File.Exists(store.PositionsPathFor("/d/orphan.dem")!)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TheInMemoryStore_HoldsPositionsToo()
    {
        DemoCacheStore cache = new(null);
        using RoundIndexStore store = new(null, cache);
        RoundIndexBuild build = Build();

        store.WritePositions(Demo, build.Positions);
        await Assert.That(store.PositionsPathFor(Demo)).IsNull();
        await Assert.That(store.TryReadPositions(Demo)!.Rounds.Count).IsEqualTo(1);

        store.Delete(Demo);
        await Assert.That(store.TryReadPositions(Demo)).IsNull();
    }

    [Test]
    public async Task TheEvaluator_WritesPositionsUnderTheSidecarsFingerprint()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(Demo, sha: "abc", facts: Facts(Round(1, 1000, 1200))));
        using RoundIndexStore store = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundIndexEvaluator evaluator = new(cache, store, sources, () => true, walk: _ => [.. Placed(1000), .. Placed(1064)]);

        evaluator.Evaluate(Demo, RoundIndexTestData.Demo(lastTick: 5000));

        RoundIndexDocument index = store.TryRead(Demo)!;
        RoundPositionsDocument? positions = store.TryReadPositions(Demo, sources.FingerprintFor("de_nuke"), "abc");
        DemoCacheRecord record = cache.TryLoadRecord(Demo)!;

        using (Assert.Multiple())
        {
            await Assert.That(positions).IsNotNull();
            await Assert.That(positions!.Fingerprint).IsEqualTo(index.Fingerprint);
            await Assert.That(positions.Demo.StableKey).IsEqualTo(index.Demo.StableKey);
            await Assert.That(positions.Demo.Sha256).IsEqualTo("abc");
            await Assert.That(positions.Rounds.Single().At(1).Count).IsEqualTo(10);
            await Assert.That(record.RoundIndexFingerprint).IsEqualTo(positions.Fingerprint);
            await Assert.That(record.IsRoundIndexCurrent(sources.FingerprintFor("de_nuke"))).IsTrue();
        }
    }

    /// <summary>
    ///     The v1 positions file is a published format: what this build writes for the fixture
    ///     document must be byte-identical to the committed sample (uncompressed, so the diff is
    ///     readable). Regenerate deliberately with <c>RI_GOLDEN_UPDATE=1</c> and look at the diff.
    /// </summary>
    [Test]
    public async Task V1Schema_MatchesTheCheckedInSample()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = Path.Combine(repo, "tests", "fixtures", "round-index", SampleName);
        if (Environment.GetEnvironmentVariable("RI_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, FixtureDocument().Serialize() + "\n");
        }

        if (!File.Exists(path))
        {
            throw new SkipTestException($"missing {path}; regenerate with RI_GOLDEN_UPDATE=1");
        }

        string original = File.ReadAllText(path);
        RoundPositionsDocument? loaded = RoundPositionsDocument.TryDeserialize(original);

        using (Assert.Multiple())
        {
            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.SchemaVersion).IsEqualTo(RoundPositionsDocument.CurrentSchemaVersion);
            await Assert.That(loaded.Fingerprint).EndsWith(";pos=1");
            await Assert.That(loaded.Demo.Sha256).IsNull();
            await Assert.That(loaded.Places).IsEquivalentTo(["Outside", "Lobby", "Ramp", "?"]);
            await Assert.That(loaded.Rounds.Single().Ct).IsEquivalentTo([0, 2, 5, 7, 9]);
            await Assert.That(loaded.Rounds[0].At(1)[1]).IsEqualTo(new RoundPosition(2, -129, -1848, -416, 1));
            await Assert.That(loaded.Serialize() + "\n").IsEqualTo(original.Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("the v1 positions file is a published format; a round trip must be field-identical");
            await Assert.That(FixtureDocument().Serialize() + "\n").IsEqualTo(original.Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("this build still writes the committed shape");
            await Assert.That(RoundPositionsDocument.TryDeserializeGzip(loaded.SerializeGzip())!.Serialize())
                .IsEqualTo(loaded.Serialize()).Because("the gzip round trip is the store's path");
        }
    }

    private static RoundIndexBuild Build()
    {
        RoundIndexBuild build = RoundIndexBuilder.BuildWithPositions(RoundIndexTestData.Demo(), Facts(Round(1, 1000, 1200)),
            RoundIndexOptions.Default, PawnPlaceSource.Instance, [.. Placed(1000), .. Placed(1064)]);
        build.Positions.Demo = new RoundPositionsDemo
        {
            StableKey = DemoCacheStore.StableKey(Demo),
            Sha256 = "abc"
        };
        return build;
    }

    // The round-index-format.md example: round 3 on nuke, two sampled steps, slots on both sides.
    private static RoundPositionsDocument FixtureDocument() => new()
    {
        Fingerprint = "ri1;cadence=1;token=1;rf=1;src=pawn;pos=1",
        Demo = new RoundPositionsDemo
        {
            StableKey = "3f9c0a1b2c3d4e5f60718293",
            Sha256 = null
        },
        CadenceTicks = 64,
        Places = ["Outside", "Lobby", "Ramp", "?"],
        Rounds =
        [
            new RoundPositionsRound
            {
                Number = 3,
                FreezeEndTick = 10746,
                Ct = [0, 2, 5, 7, 9],
                Pos =
                [
                    [
                        new RoundPosition(0, -448, 1180, -416, 0),
                        new RoundPosition(1, 1320, -900, -700, 2),
                        new RoundPosition(2, -129, -1848, -416, 1)
                    ],
                    [
                        new RoundPosition(0, -401, 1130, -416, 0),
                        new RoundPosition(2, -129, -1848, -416, 1),
                        new RoundPosition(4, 2600, 900, -416, 3)
                    ]
                ]
            }
        ]
    };
}
