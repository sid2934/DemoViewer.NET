#region

using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The gzipped record and grenade sidecars: what is written, that the pre-gzip files still read and are
///     replaced only by a successful write, and that bad files read as absent.
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
                await Assert.That(back!.RoundFacts!.Rounds.Count).IsEqualTo(24);
                await Assert.That(back.Highlights.Count).IsEqualTo(200);
                await Assert.That(back.Players[0].SteamId64).IsEqualTo("76561198000000001");
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
                await Assert.That(read!.RoundFacts!.Rounds.Count).IsEqualTo(24);
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
    public async Task BadRecordFiles_ReadAsAbsent_AndTheNewFileWinsOverTheLegacyOne()
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

            // A valid legacy file does not rescue a corrupt new one: the new one is the later write.
            File.WriteAllText(legacy, JsonSerializer.Serialize(BigRecord(Demo)));
            await Assert.That(new DemoCacheStore(root).TryLoadRecord(Demo)).IsNull();

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
            using RoundIndexStore store = new(root, cache);
            RoundPositionsDocument document = new() { Fingerprint = "fp" };
            store.WritePositions(Demo, document);
            string file = store.PositionsPathFor(Demo)!;
            await Assert.That(store.TryReadPositions(Demo, "fp")).IsNotNull();

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

    private static void Stamp(DemoCacheStore cache)
    {
        DemoCacheRecord record = RoundIndexTestData.ParsedRecord(Demo, "de_nuke", "sha-g");
        DemoCacheStore.StampGrenades(record);
        record.GrenadeState = DemoAnalysisState.Indexed;
        record.GrenadeWalker = GrenadeWalker.Version;
        cache.Upsert(record);
    }

    [Test]
    public async Task Grenades_LegacySiblingsRead_NewOnesWinAndReplaceThem()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            Stamp(cache);
            (GrenadeDocument oldRows, GrenadePathsDocument oldPaths) = Documents(Row("g1", "76561198000000001"));
            cache.WriteSibling(Demo, GrenadeSidecar.LegacySuffix, GrenadeSidecar.Serialize(oldRows));
            cache.WriteSibling(Demo, GrenadeSidecar.LegacyPathsSuffix, GrenadeSidecar.Serialize(oldPaths));

            GrenadeDocument? legacyRows = GrenadeSidecar.TryReadRows(cache, Demo);
            GrenadePathsDocument? legacyPaths = GrenadeSidecar.TryReadPaths(cache, Demo);
            using (Assert.Multiple())
            {
                await Assert.That(legacyRows!.Grenades.Single().Id).IsEqualTo("g1");
                await Assert.That(legacyPaths!.Paths["g1"].Count).IsEqualTo(2);
            }

            (GrenadeDocument rows, GrenadePathsDocument paths) = Documents(Row("g2", null), Row("g3", "76561198000000003"));
            GrenadeSidecar.Write(cache, Demo, rows, paths);
            Stamp(cache);
            GrenadeSidecar.DeleteLegacy(cache, Demo);

            GrenadeDocument? newRows = GrenadeSidecar.TryReadRows(cache, Demo);
            GrenadePathsDocument? newPaths = GrenadeSidecar.TryReadPaths(cache, Demo);
            using (Assert.Multiple())
            {
                await Assert.That(IsGzipFile(cache.SiblingPathFor(Demo, GrenadeSidecar.Suffix)!)).IsTrue();
                await Assert.That(IsGzipFile(cache.SiblingPathFor(Demo, GrenadeSidecar.PathsSuffix)!)).IsTrue();
                await Assert.That(File.Exists(cache.SiblingPathFor(Demo, GrenadeSidecar.LegacySuffix)!)).IsFalse();
                await Assert.That(File.Exists(cache.SiblingPathFor(Demo, GrenadeSidecar.LegacyPathsSuffix)!)).IsFalse();
                await Assert.That(newRows!.Grenades.Select(g => g.Id)).IsEquivalentTo(["g2", "g3"]);
                await Assert.That(newRows.Grenades[0].ThrowerSteamId64).IsNull();
                await Assert.That(newRows.Grenades[1].ThrowerSteamId64).IsEqualTo("76561198000000003");
                await Assert.That(newRows.Grenades[1].ThrowerSteamId).IsEqualTo(76561198000000003UL);
                await Assert.That(newRows.Grenades[1].Trajectory).IsEmpty().Because("the rows file carries no paths");
                await Assert.That(newPaths!.Paths["g3"].Count).IsEqualTo(2);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Grenades_CorruptNewSibling_DoesNotFallBackToTheLegacyOne()
    {
        DemoCacheStore cache = new(null);
        Stamp(cache);
        (GrenadeDocument rows, _) = Documents(Row("g1", "1"));
        cache.WriteSibling(Demo, GrenadeSidecar.LegacySuffix, GrenadeSidecar.Serialize(rows));
        cache.WriteSiblingBytes(Demo, GrenadeSidecar.Suffix, [0x1F, 0x8B, 9, 9, 9]);

        await Assert.That(GrenadeSidecar.TryReadRows(cache, Demo)).IsNull();
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
