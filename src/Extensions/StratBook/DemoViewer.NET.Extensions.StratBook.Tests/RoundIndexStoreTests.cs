#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The round index store over the per-demo data: a write stamps the index and its positions, the stamp
///     answers freshness without a file read, a failure and a rebuild move the stamp, a corrupt file reads as
///     absent, a demo that leaves the library loses its index, and the v1 shape round-trips through the
///     committed fixture byte for byte.
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
    public async Task Write_StampsTheIndexAndItsPositions_AndAReadNeedsNoFile()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            cache.Upsert(ParsedRecord(Demo, sha: "abcd"));
            RoundIndexStore store = new(cache.DiskData(root));
            RoundIndexDocument sample = Sample();
            store.Write(Demo, sample, new RoundPositionsDocument { Fingerprint = sample.Fingerprint }, sample.Fingerprint);

            using (Assert.Multiple())
            {
                await Assert.That(store.IsCurrent(Demo, sample.Fingerprint)).IsTrue();
                await Assert.That(store.Needs(Demo, sample.Fingerprint)).IsFalse();
                await Assert.That(store.Needs(Demo, "another")).IsTrue().Because("another fingerprint is another index");
                await Assert.That(store.Stamp(Demo)!.Count).IsEqualTo(sample.RowCount);
                await Assert.That(store.TryRead(Demo)!.Rounds.Single().Runs.Count).IsEqualTo(3);
                await Assert.That(store.TryReadPositions(Demo, sample.Fingerprint)).IsNotNull();
                await Assert.That(store.TryReadPositions(Demo, "another")).IsNull().Because("positions built under another fingerprint read as absent");
                await Assert.That(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories)).IsEmpty()
                    .Because("every file is replaced whole, never left half written");
            }

            // A fresh store over the same folder reads the stamp from the index file, not the demo files.
            RoundIndexStore reopened = new(cache.DiskData(root));
            await Assert.That(reopened.ComputedAtTicks(Demo)).IsEqualTo(store.ComputedAtTicks(Demo));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AFailure_LeavesTheBacklog_UntilCleared_AndARebuildKeepsTheOldRowsReadable()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(Demo));
        RoundIndexStore store = new(cache.Data());
        RoundIndexDocument sample = Sample();
        store.Write(Demo, sample, new RoundPositionsDocument(), sample.Fingerprint);

        store.MarkFailed(Demo);
        await Assert.That(store.Needs(Demo, "another")).IsFalse().Because("retrying a failure is the user's call");
        store.ClearFailed(Demo);
        await Assert.That(store.Needs(Demo, "another")).IsTrue();

        store.InvalidateAll();
        using (Assert.Multiple())
        {
            await Assert.That(store.IsCurrent(Demo, sample.Fingerprint)).IsFalse();
            await Assert.That(store.TryRead(Demo)).IsNotNull().Because("the old rows answer until the demo is rebuilt");
        }
    }

    [Test]
    public async Task ACorruptIndex_ReadsAsAbsent()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            cache.Upsert(ParsedRecord(Demo, sha: "abcd"));
            RoundIndexStore store = new(cache.DiskData(root));
            RoundIndexDocument sample = Sample();
            store.Write(Demo, sample, new RoundPositionsDocument(), sample.Fingerprint);
            string file = Directory.GetFiles(root, "abcd.json.gz", SearchOption.AllDirectories).Single();
            File.WriteAllBytes(file, [1, 2, 3]);

            await Assert.That(store.TryRead(Demo)).IsNull();
            await Assert.That(store.TryRead("/d/never.dem")).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ADemoRemovedFromTheLibrary_LosesItsIndex()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(Demo));
        cache.Upsert(ParsedRecord("/d/other.dem"));
        RoundIndexStore store = new(cache.Data());
        store.Write(Demo, Sample());
        store.Write("/d/other.dem", Sample());

        cache.Remove(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(store.TryRead(Demo)).IsNull();
            await Assert.That(store.TryRead("/d/other.dem")).IsNotNull();
        }

        // A re-upsert of a demo still in the library is not a removal.
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
            await Assert.That(loaded!.SchemaVersion).IsEqualTo(RoundIndexStore.Schema);
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
