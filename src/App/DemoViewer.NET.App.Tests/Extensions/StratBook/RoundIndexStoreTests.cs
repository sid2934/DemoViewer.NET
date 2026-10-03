#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The sidecar store: the file lands beside the cache under the stable key, a write is atomic
///     (no temp file survives), a corrupt sidecar reads as absent, the null-root store keeps documents
///     in memory, the orphan sweep and the <c>Changed</c> subscriber delete what the index forgot,
///     and the v1 shape round-trips through the committed fixture byte for byte.
/// </summary>
[NotInParallel]
public class RoundIndexStoreTests
{
    private const string Demo = "/d/match.dem";
    private const string SampleName = "schema-v1.sample.dvri.json";

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-roundindex-{Guid.NewGuid():N}");

    private static RoundIndexDocument Sample() => Document("de_nuke", "ri1;cadence=1;token=1;rf=1;src=pawn",
        (3, 10746, 17138,
        [
            new RoundIndexRun(0, 4, "CTSpawn:5", "TSpawn:5"),
            new RoundIndexRun(5, 9, "BombsiteA:2|Outside:3", "Lobby:3|Ramp:2"),
            new RoundIndexRun(10, 10, "BombsiteA:2|Outside:2", "Lobby:3|Ramp:1")
        ]));

    private static RoundIndexDocument FixtureDocument()
    {
        RoundIndexDocument document = Sample();
        document.Demo = new RoundIndexDemo
        {
            Sha256 = null,
            StableKey = "3f9c0a1b2c3d4e5f60718293",
            FileName = "match730_003731893271710924851_1024675027_129.dem",
            SizeBytes = 289436777
        };
        document.Clock.FrameCount = 154869;
        document.Clock.LastTick = 132516;
        document.Places["Outside"] = new PlaceSampleSummary
        {
            Count = 2396,
            Buckets =
            [
                new PlaceZBucketSum(-448, 1180, 61234.5, -812900.2),
                new PlaceZBucketSum(-384, 1216, 60110.0, -800012.7)
            ]
        };
        document.Transitions = [new PlaceTransition("CTSpawn", "Outside", 115), new PlaceTransition("Admin", "Ramp", 62)];
        return document;
    }

    [Test]
    public async Task Write_LandsUnderTheStableKey_Atomically_AndReadsBack()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            using RoundIndexStore store = new(root, cache);
            store.Write(Demo, Sample());

            string expected = Path.Combine(root, "round-index", DemoCacheStore.StableKey(Demo) + ".dvri.json");
            RoundIndexDocument? read = store.TryRead(Demo);

            using (Assert.Multiple())
            {
                await Assert.That(store.PathFor(Demo)).IsEqualTo(expected);
                await Assert.That(File.Exists(expected)).IsTrue();
                await Assert.That(Directory.GetFiles(Path.Combine(root, "round-index"), "*.tmp")).IsEmpty()
                    .Because("the temp file is replaced, never left beside the sidecar");
                await Assert.That(read).IsNotNull();
                await Assert.That(read!.Rounds.Single().Runs.Count).IsEqualTo(3);
                await Assert.That(read.Rounds[0].Runs[1].Ct).IsEqualTo("BombsiteA:2|Outside:3");
                await Assert.That(File.ReadAllText(expected)).DoesNotContain("\n").Because("compact, not indented");
            }

            // Overwrite through the same path: the replace branch.
            RoundIndexDocument second = Sample();
            second.Rounds[0].Number = 4;
            store.Write(Demo, second);
            await Assert.That(store.TryRead(Demo)!.Rounds[0].Number).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ACorruptSidecar_ReadsAsAbsent()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            using RoundIndexStore store = new(root, cache);
            store.Write(Demo, Sample());
            File.WriteAllText(store.PathFor(Demo)!, "{ not json");

            await Assert.That(store.TryRead(Demo)).IsNull();
            await Assert.That(store.TryRead("/d/never.dem")).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ANullRoot_KeepsDocumentsInMemory()
    {
        DemoCacheStore cache = new(null);
        using RoundIndexStore store = new(null, cache);
        store.Write(Demo, Sample());
        store.Write("/d/other.dem", Sample());

        using (Assert.Multiple())
        {
            await Assert.That(store.Root).IsNull();
            await Assert.That(store.PathFor(Demo)).IsNull();
            await Assert.That(store.TryRead(Demo)).IsNotNull();
            await Assert.That(store.TryRead("/d/other.dem")).IsNotNull().Because("more than one demo, unlike a capacity-1 cache");
        }

        store.Delete(Demo);
        await Assert.That(store.TryRead(Demo)).IsNull();
    }

    [Test]
    public async Task TheOrphanSweep_DeletesSidecarsTheIndexDoesNotCarry()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            cache.Upsert(ParsedRecord(Demo));
            using RoundIndexStore store = new(root, cache);
            store.Write(Demo, Sample());
            store.Write("/d/gone.dem", Sample());

            int removed = store.SweepOrphans();

            using (Assert.Multiple())
            {
                await Assert.That(removed).IsEqualTo(1);
                await Assert.That(store.TryRead(Demo)).IsNotNull();
                await Assert.That(store.TryRead("/d/gone.dem")).IsNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ADemoRemovedFromTheIndex_LosesItsSidecarOnChanged()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(Demo));
        cache.Upsert(ParsedRecord("/d/other.dem"));
        using RoundIndexStore store = new(null, cache);
        store.Write(Demo, Sample());
        store.Write("/d/other.dem", Sample());

        cache.Remove(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(store.TryRead(Demo)).IsNull().Because("the Changed subscriber deleted it");
            await Assert.That(store.TryRead("/d/other.dem")).IsNotNull();
        }

        // A re-upsert of a demo still in the index is not a removal.
        cache.Upsert(ParsedRecord("/d/other.dem"));
        await Assert.That(store.TryRead("/d/other.dem")).IsNotNull();
    }

    /// <summary>
    ///     The v1 sidecar is a published format (docs/round-index-format.md); a round trip through this
    ///     build must be byte-identical to the committed sample. Regenerate deliberately with
    ///     <c>RI_GOLDEN_UPDATE=1</c> and look at the diff before committing it.
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
        RoundIndexDocument? loaded = RoundIndexDocument.TryDeserialize(original);

        using (Assert.Multiple())
        {
            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.SchemaVersion).IsEqualTo(DemoCacheRecord.RoundIndexSchema);
            await Assert.That(loaded.Clock.Kind).IsEqualTo("dv-frame-clock");
            await Assert.That(loaded.Demo.Sha256).IsNull();
            await Assert.That(loaded.RowCount).IsEqualTo(11);
            await Assert.That(loaded.Places["Outside"].Buckets.Count).IsEqualTo(2);
            await Assert.That(loaded.Transitions[0]).IsEqualTo(new PlaceTransition("CTSpawn", "Outside", 115));
            await Assert.That(loaded.Serialize() + "\n").IsEqualTo(original.Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("the v1 sidecar is a published format; a round trip must be field-identical");
            await Assert.That(FixtureDocument().Serialize() + "\n").IsEqualTo(original.Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("this build still writes the committed shape");
        }
    }
}
