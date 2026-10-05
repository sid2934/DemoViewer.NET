#region

using DemoViewer.NET.Extensions.StratBook;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The gzipped record sidecars: what is written, that the pre-gzip files still read and are replaced only by
///     a successful write, and that bad files read as absent. Also the grenade rows' text round trip and a cut
///     positions file reading as absent.
/// </summary>
public class SidecarFormatTests
{
    private const string Demo = "/d/format.dem";

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-sidecar-{Guid.NewGuid():N}");

    private static DemoCacheRecord BigRecord(string path)
    {
        List<RoundFacts> rounds = [];
        for (int n = 1; n <= 24; n++)
        {
            rounds.Add(RoundIndexTestData.Round(n, n * 10_000, n * 10_000 + 6_000,
                kills: [RoundIndexTestData.Kill(n * 10_000 + 500, 6), RoundIndexTestData.Kill(n * 10_000 + 900, 2)]));
        }

        DemoCacheRecord record = RoundIndexTestData.ParsedRecord(path, "de_nuke", "sha-1", RoundIndexTestData.Facts([.. rounds]));
        for (int slot = 1; slot <= 10; slot++)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot, Name = $"player {slot}", SteamId64 = $"7656119800000{slot:D4}", Team = slot <= 5 ? 3 : 2 });
        }

        for (int i = 0; i < 200; i++)
        {
            record.Highlights.Add(new CachedHighlightEvent
            {
                RulesetId = "builtin", HighlightId = $"h{i % 7}", Tick = i * 300, RoundNumber = 1 + i / 9,
                PlayerSlot = 1 + i % 10, RenderedTitle = $"a highlight title number {i}"
            });
        }

        return record;
    }

    private static bool IsGzipFile(string file)
    {
        byte[] head = File.ReadAllBytes(file);
        return head.Length > 2 && head[0] == 0x1F && head[1] == 0x8B;
    }

    [Test]
    public async Task Upsert_WritesGzippedCompactJson_AndReadsItBack()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(BigRecord(Demo));
            string file = store.SidecarPathFor(Demo)!;

            await using GZipStream gzip = new(File.OpenRead(file), CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);
            string json = await reader.ReadToEndAsync();

            DemoCacheRecord? back = new DemoCacheStore(root).TryLoadRecord(Demo);
            using (Assert.Multiple())
            {
                await Assert.That(file).EndsWith(".json.gz");
                await Assert.That(IsGzipFile(file)).IsTrue();
                await Assert.That(json).DoesNotContain("\n").Because("the record is written compact");
                await Assert.That(back).IsNotNull();
                await Assert.That(back!.RoundFacts()!.Rounds.Count).IsEqualTo(24);
                await Assert.That(back!.Highlights.Count).IsEqualTo(200);
                await Assert.That(back!.Players[0].SteamId64).IsEqualTo("76561198000000001");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task LegacyRecord_Reads_AndIsReplacedOnlyByTheNextWrite()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore seed = new(root);
            seed.Upsert(BigRecord(Demo));
            seed.SaveIndex();
            string file = seed.SidecarPathFor(Demo)!;
            string legacy = seed.LegacySidecarPathFor(Demo)!;

            // Turn it into what the previous build left: an indented .json and no .json.gz.
            DemoCacheRecord original = seed.TryLoadRecord(Demo)!;
            File.WriteAllText(legacy, JsonSerializer.Serialize(original, _indented));
            File.Delete(file);
            string otherLegacy = seed.LegacySidecarPathFor("/d/untouched.dem")!;
            File.WriteAllText(otherLegacy, "{}");

            DemoCacheStore store = new(root);
            DemoCacheRecord? read = store.TryLoadRecord(Demo);
            using (Assert.Multiple())
            {
                await Assert.That(read).IsNotNull();
                await Assert.That(read!.RoundFacts()!.Rounds.Count).IsEqualTo(24);
                await Assert.That(File.Exists(legacy)).IsTrue().Because("a read never migrates");
                await Assert.That(File.Exists(file)).IsFalse();
            }

            store.UpdateExisting(Demo, r => r.Map = "de_inferno");

            DemoCacheRecord? after = new DemoCacheStore(root).TryLoadRecord(Demo);
            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(file)).IsTrue();
                await Assert.That(IsGzipFile(file)).IsTrue();
                await Assert.That(File.Exists(legacy)).IsFalse().Because("the write replaced it");
                await Assert.That(File.Exists(otherLegacy)).IsTrue().Because("only the written demo migrates");
                await Assert.That(after!.Map).IsEqualTo("de_inferno");
                await Assert.That(after.Highlights.Count).IsEqualTo(200);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task BadRecordFiles_ReadAsAbsent_UnlessALegacyFileSitsBesideThem()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore seed = new(root);
            seed.Upsert(BigRecord(Demo));
            string file = seed.SidecarPathFor(Demo)!;
            string legacy = seed.LegacySidecarPathFor(Demo)!;
            byte[] good = File.ReadAllBytes(file);

            // Truncated gzip.
            File.WriteAllBytes(file, good[..(good.Length / 2)]);
            await Assert.That(new DemoCacheStore(root).TryLoadRecord(Demo)).IsNull();

            // Gzip magic then garbage.
            File.WriteAllBytes(file, [0x1F, 0x8B, 1, 2, 3, 4, 5]);
            await Assert.That(new DemoCacheStore(root).TryLoadRecord(Demo)).IsNull();

            // A valid legacy file beside a corrupt new one is the last good write, and is read.
            File.WriteAllText(legacy, JsonSerializer.Serialize(BigRecord(Demo)));
            await Assert.That(new DemoCacheStore(root).TryLoadRecord(Demo)).IsNotNull();

            // Corrupt legacy alone.
            File.Delete(file);
            File.WriteAllText(legacy, "{ not json");
            await Assert.That(new DemoCacheStore(root).TryLoadRecord(Demo)).IsNull();

            // A plain-JSON file under the new name still reads: readers sniff, not trust the name.
            File.Delete(legacy);
            File.WriteAllText(file, JsonSerializer.Serialize(BigRecord(Demo)));
            await Assert.That(new DemoCacheStore(root).TryLoadRecord(Demo)).IsNotNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Remove_TakesBothRecordNames()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(BigRecord(Demo));
            string legacy = store.LegacySidecarPathFor(Demo)!;
            File.WriteAllText(legacy, "{}");

            store.Remove(Demo);
            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(store.SidecarPathFor(Demo)!)).IsFalse();
                await Assert.That(File.Exists(legacy)).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void SiblingSuffixes_CannotShadowTheRecord()
    {
        DemoCacheStore cache = new(null);
        Assert.Throws<ArgumentException>(() => cache.WriteSibling(Demo, ".json.gz", "{}"));
        Assert.Throws<ArgumentException>(() => cache.WriteSibling(Demo, ".json", "{}"));
    }

    [Test]
    public async Task Gzip_SizeHintTooSmall_StillInflatesWhole()
    {
        // Two gzip members: the trailer names only the last one's size, so the pooled buffer has to grow.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(BigRecord(Demo));
        int split = json.Length - 8;
        byte[] gz = [.. SidecarJson.Gzip(json.AsSpan(0, split)), .. SidecarJson.Gzip(json.AsSpan(split))];

        DemoCacheRecord? back = SidecarJson.Deserialize<DemoCacheRecord>(gz, null);
        await Assert.That(back!.Highlights.Count).IsEqualTo(200);
    }

    [Test]
    public async Task Positions_TruncatedGzip_ReadsAsAbsent()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            cache.Upsert(RoundIndexTestData.ParsedRecord(Demo, "de_nuke", "shapos"));
            RoundIndexStore store = new(cache.DiskData(root));
            store.Write(Demo, new RoundIndexDocument { Map = "de_nuke" }, new RoundPositionsDocument { Fingerprint = "fp" }, "fp");
            await Assert.That(store.TryReadPositions(Demo, "fp")).IsNotNull();

            string file = Directory.GetFiles(root, "*.positions.json.gz", SearchOption.AllDirectories).Single();
            byte[] good = File.ReadAllBytes(file);
            File.WriteAllBytes(file, good[..(good.Length / 2)]);
            await Assert.That(store.TryReadPositions(Demo)).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── Grenades ────────────────────────────────────────────────────────────────────────────────

    private static GrenadeRow Row(string id, string? steamId) => new()
    {
        Id = id,
        Kind = GrenadeKind.Smoke,
        ThrowerSteamId64 = steamId,
        ThrowerTeam = 3,
        ReleasePosition = WorldPoint.From(new Vector3(1, 2, 3)),
        DetonationPosition = WorldPoint.From(new Vector3(100, 200, 3)),
        Trajectory = [new TrajectoryPoint(10, 1, 2, 3, 0), new TrajectoryPoint(20, 50, 100, 3, 0)]
    };

    private static (GrenadeDocument Rows, GrenadePathsDocument Paths) Documents(params GrenadeRow[] rows)
    {
        GrenadeDemoHeader header = new() { Sha256 = "sha-g", StableKey = DemoCacheStore.StableKey(Demo) };
        GrenadeDocument document = new() { Demo = header, Grenades = [.. rows] };
        GrenadePathsDocument paths = new() { Demo = header };
        foreach (GrenadeRow row in rows)
        {
            paths.Paths[row.Id] = row.Trajectory;
        }

        return (document, paths);
    }

    // ── One-off conversion pass ─────────────────────────────────────────────────────────────────

    private static Task<SidecarFormatResult> Pass(DemoCacheStore cache) =>
        SidecarFormatMigration.RunAsync(cache, [cache.ConvertLegacyRecord], batchSize: 2);

    [Test]
    public async Task ConversionPass_RunsAsAQueueItem_AndIsNotQueuedOnceDone()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore seed = new(root);
            foreach (string path in (string[])["/d/a.dem", "/d/b.dem", "/d/c.dem"])
            {
                seed.Upsert(BigRecord(path));
            }

            seed.SaveIndex();
            DemoCacheRecord aRecord = seed.TryLoadRecord("/d/a.dem")!;
            File.WriteAllText(seed.LegacySidecarPathFor("/d/a.dem")!, JsonSerializer.Serialize(aRecord, _indented));
            File.Delete(seed.SidecarPathFor("/d/a.dem")!);

            DemoCacheStore cache = new(root);
            using HeavyJobGate gate = new();
            using DemoProcessingQueue queue = new(gate, a => a(), _ => throw new NotSupportedException(),
                _ => throw new NotSupportedException(), () => Task.CompletedTask);
            IReadOnlyList<Func<string, SidecarConversion>> converters = [cache.ConvertLegacyRecord];

            IDemoQueueHandle? handle = SidecarFormatMigration.Submit(queue, cache, converters, batchSize: 1);
            await Assert.That(handle).IsNotNull();
            await handle!.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            DemoQueueItemSnapshot item = queue.Snapshot().Single(s => s.Id == handle.Id);
            await Assert.That(item.Kind).IsEqualTo(QueueJobKind.SidecarMigration);
            await Assert.That(item.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(File.Exists(seed.LegacySidecarPathFor("/d/a.dem")!)).IsFalse();
            await Assert.That(File.Exists(Path.Combine(root, SidecarFormatMigration.MarkerFileName))).IsTrue();
            await Assert.That(SidecarFormatMigration.Submit(queue, cache, converters)).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ConversionPass_ConvertsMixedFiles_KeepsBadOnes_AndMarksDoneOnceClean()
    {
        string root = TempRoot();
        try
        {
            const string a = "/d/a.dem", b = "/d/b.dem", c = "/d/c.dem", d = "/d/d.dem";
            DemoCacheStore seed = new(root);
            foreach (string path in (string[])[a, b, c, d])
            {
                seed.Upsert(BigRecord(path));
            }

            seed.SaveIndex();

            // a: legacy only, carrying a field no model knows. c: corrupt legacy only. d: new plus a stale legacy.
            DemoCacheRecord aRecord = seed.TryLoadRecord(a)!;
            string aJson = JsonSerializer.Serialize(aRecord, _indented).Insert(1, "\n  \"FutureField\": 7,");
            File.WriteAllText(seed.LegacySidecarPathFor(a)!, aJson);
            File.Delete(seed.SidecarPathFor(a)!);
            File.WriteAllText(seed.LegacySidecarPathFor(c)!, "{ not json");
            File.Delete(seed.SidecarPathFor(c)!);
            File.WriteAllText(seed.LegacySidecarPathFor(d)!, "{}");

            DemoCacheStore cache = new(root);
            SidecarFormatResult first = await Pass(cache);
            string marker = Path.Combine(root, SidecarFormatMigration.MarkerFileName);

            await using (GZipStream gzip = new(File.OpenRead(cache.SidecarPathFor(a)!), CompressionMode.Decompress))
            using (StreamReader reader = new(gzip, Encoding.UTF8))
            {
                string converted = await reader.ReadToEndAsync();
                await Assert.That(converted).Contains("\"FutureField\":7");
            }

            using (Assert.Multiple())
            {
                await Assert.That(first.Demos).IsEqualTo(4);
                await Assert.That(first.Failed).IsEqualTo(1).Because("c's record does not read");
                await Assert.That(first.Completed).IsFalse();
                await Assert.That(File.Exists(marker)).IsFalse();
                await Assert.That(File.Exists(cache.LegacySidecarPathFor(a)!)).IsFalse();
                await Assert.That(cache.TryLoadRecord(a)!.Highlights.Count).IsEqualTo(200);
                await Assert.That(File.Exists(cache.LegacySidecarPathFor(c)!)).IsTrue();
                await Assert.That(File.Exists(cache.SidecarPathFor(c)!)).IsFalse();
                await Assert.That(File.Exists(cache.LegacySidecarPathFor(d)!)).IsFalse();
                await Assert.That(cache.TryLoadRecord(d)).IsNotNull();
            }

            // Resumable: the converted demos have nothing left, the bad files are tried again.
            SidecarFormatResult second = await Pass(cache);
            using (Assert.Multiple())
            {
                await Assert.That(second.Converted).IsEqualTo(0);
                await Assert.That(second.Failed).IsEqualTo(1);
            }

            File.Delete(cache.LegacySidecarPathFor(c)!);
            SidecarFormatResult third = await Pass(cache);
            SidecarFormatResult fourth = await Pass(cache);
            using (Assert.Multiple())
            {
                await Assert.That(third.Completed).IsTrue();
                await Assert.That(File.Exists(marker)).IsTrue();
                await Assert.That(fourth.Demos).IsEqualTo(0).Because("the marker ends the pass for good");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task GrenadeRow_SteamIdText_RoundTripsExactly()
    {
        (GrenadeDocument rows, _) = Documents(Row("a", "76561198000000001"), Row("b", null), Row("c", "007"), Row("d", "bot"), Row("e", "0"));
        string json = GrenadeSidecar.Serialize(rows);
        GrenadeDocument back = GrenadeSidecar.TryDeserializeRows(json)!;
        using (Assert.Multiple())
        {
            await Assert.That(GrenadeSidecar.Serialize(back)).IsEqualTo(json);
            await Assert.That(back.Grenades.Select(g => g.ThrowerSteamId64)).IsEquivalentTo(
                new string?[] { "76561198000000001", null, "007", "bot", "0" });
            await Assert.That(back.Grenades[2].ThrowerSteamId).IsNull().Because("text that would not print back is kept as text");
            await Assert.That(back.Grenades[4].ThrowerSteamId).IsEqualTo(0UL);
        }
    }

    // Prints the before/after allocation of one record read. Not an assertion: the numbers depend on
    // the record's shape, and this one is synthetic.
    [Test]
    [Category("Budget")]
    public async Task Measure_RecordReadAllocation()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            DemoCacheRecord record = BigRecord(Demo);
            store.Upsert(record);
            string legacy = Path.Combine(root, "legacy.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(record, _indented));

            const int reads = 50;
            JsonSerializer.Deserialize<DemoCacheRecord>(File.ReadAllText(legacy));
            new DemoCacheStore(root).TryLoadRecord(Demo);

            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < reads; i++)
            {
                JsonSerializer.Deserialize<DemoCacheRecord>(File.ReadAllText(legacy));
            }

            long before = (GC.GetAllocatedBytesForCurrentThread() - start) / reads;

            DemoCacheStore[] fresh = [.. Enumerable.Range(0, reads).Select(_ => new DemoCacheStore(root))];
            start = GC.GetAllocatedBytesForCurrentThread();
            foreach (DemoCacheStore s in fresh)
            {
                s.TryLoadRecord(Demo);
            }

            long after = (GC.GetAllocatedBytesForCurrentThread() - start) / reads;
            Console.WriteLine(
                $"[record read] indented text {new FileInfo(legacy).Length} B, gzip {new FileInfo(store.SidecarPathFor(Demo)!).Length} B; " +
                $"alloc per read: before {before} B, after {after} B");
            await Assert.That(after).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
