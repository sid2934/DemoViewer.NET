#region

using System.Numerics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The throw log, the lineup store's flights and the one-off migration on temp caches:
///     every field survives the log, the JSON and paths files go only after what replaces them reads back,
///     the names come from the record, and a demo walked this session gives its lineup a flight.
/// </summary>
[NotInParallel]
public class GrenadeStoreTests
{
    private const string Map = "de_mirage";

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-grenade-store-{Guid.NewGuid():N}");

    private static GrenadeRow Full(string id) => new()
    {
        Id = id,
        Kind = GrenadeKind.Molotov,
        ThrowerSlot = 4,
        ThrowerSteamId64 = "76561198000000004",
        ThrowerName = "sid",
        ThrowerTeam = 2,
        ThrowerSource = ThrowerSource.WeaponFire,
        RoundNumber = 7,
        ReleaseTick = 12345,
        ReleaseSource = ReleaseSource.GrenadeThrown,
        ReleasePosition = new WorldPoint(1293.41f, -352.27f, -145.2f),
        ReleaseEyePitch = -42.44123f,
        ReleaseEyeYaw = -173.9712f,
        ReleaseOnGround = false,
        ReleaseCrouched = true,
        ThrowStrength = 0.5f,
        ThrowStrengthClass = ThrowStrengthClass.Half,
        Movement = MovementClass.Walking,
        SpeedAtRelease = 18.3f,
        SpawnTick = 12352,
        SpawnPosition = new WorldPoint(1, 2, 3),
        SpawnVelocity = new WorldPoint(-621.41f, -13.22f, 263.24f),
        ThrowerPositionAtSpawn = new WorldPoint(4, 5, 6),
        ThrowerOnGroundAtSpawn = true,
        JumpThrow = true,
        JumpThrowSource = JumpThrowSource.Inputs,
        JumpPressTick = 12340,
        BounceCount = 6,
        AirTimeTicks = 167,
        DetonationTick = 12520,
        DetonationPosition = new WorldPoint(-1217.7f, -621.8f, -166f),
        DetonationSource = DetonationSource.Entity,
        EndTick = 13000,
        EndKind = GrenadeEndKind.Detonated,
        InfernoEntityIndex = 321,
        LandingPlace = "SnipersNest"
    };

    [Test]
    public async Task TheLog_KeepsEveryField_NullsIncluded_AndIsSmallerThanTheJson()
    {
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = "abc", StableKey = "k", FileName = "a.dem", SizeBytes = 9 },
            Grenades = [Full("g1-2"), new GrenadeRow { Id = "g3-4" }, .. Enumerable.Range(0, 200).Select(i => Full($"g{i}-1"))]
        };

        byte[] log = GrenadeThrowLog.Encode(document);
        GrenadeDocument back = GrenadeThrowLog.TryDecode(log)!;
        byte[] json = SidecarJson.SerializeGzip(document, GrenadeSidecar.JsonOptions);
        using (Assert.Multiple())
        {
            await Assert.That(GrenadeThrowLog.SameRows(document.Grenades, back.Grenades)).IsTrue();
            await Assert.That(back.Grenades[0].ReleaseEyePitch).IsEqualTo(-42.44123f).Because("floats keep full precision");
            await Assert.That(back.Grenades[1].ReleasePosition).IsNull();
            await Assert.That(back.Grenades[1].ReleaseOnGround).IsNull();
            await Assert.That(back.Demo.Sha256).IsEqualTo("abc");
            await Assert.That(log.Length).IsLessThan(json.Length);
            await Assert.That(GrenadeThrowLog.TryDecode([1, 2, 3])).IsNull();
        }
    }

    private static GrenadeRow Smoke(string id, float x, int slot, string steam) => new()
    {
        Id = id,
        Kind = GrenadeKind.Smoke,
        ThrowerSlot = slot,
        ThrowerSteamId64 = steam,
        ThrowerTeam = 2,
        ReleaseTick = 1000,
        ReleasePosition = new WorldPoint(x, 0, -160),
        DetonationPosition = new WorldPoint(-1200, -630, -166),
        EndKind = GrenadeEndKind.Detonated,
        Trajectory = [new TrajectoryPoint(10, x, 0, -100, 0), new TrajectoryPoint(20, -1200, -630, -166, 1)]
    };

    // An old-format demo: gzipped JSON rows and paths, the record listing the players, stamped current.
    private static void OldFormat(DemoCacheStore cache, string path, string sha, params GrenadeRow[] rows)
    {
        DemoCacheRecord record = RoundIndexTestData.ParsedRecord(path, Map, sha);
        record.Players = [new CachedPlayerInfo { Slot = 3, Name = "window guy", SteamId64 = "76561198000000003", Team = 2 }];
        record.StampGrenades();
        cache.Upsert(record);
        GrenadeDemoHeader header = new() { Sha256 = sha, StableKey = DemoCacheStore.StableKey(path) };
        GrenadeDocument document = new() { Demo = header, Grenades = [.. rows] };
        GrenadePathsDocument paths = new() { Demo = header };
        foreach (GrenadeRow row in rows)
        {
            paths.Paths[row.Id] = row.Trajectory;
        }

        cache.WriteSiblingBytes(path, GrenadeSidecar.Suffix, SidecarJson.SerializeGzip(document, GrenadeSidecar.JsonOptions));
        cache.WriteSiblingBytes(path, GrenadeSidecar.PathsSuffix, SidecarJson.SerializeGzip(paths, GrenadeSidecar.JsonOptions));
    }

    [Test]
    public async Task TheMigration_ConvertsRowsToLogs_StoresOneFlightPerPosition_ThenDeletesTheOldFiles()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            OldFormat(cache, "/d/a.dem", "sha-a", Smoke("g1-1", 1300, 3, "76561198000000003"));
            OldFormat(cache, "/d/b.dem", "sha-b", Smoke("g1-1", 1305, 3, "76561198000000003"));
            OldFormat(cache, "/d/c.dem", "sha-c", Smoke("g1-1", 1310, 5, "76561198000000009"));
            using GrenadeIndex index = new(cache);
            index.Load();
            Guid before = index.Query(new GrenadeQuery(Map))[0].Lineups[0].Id;

            GrenadeStoreMigrationResult result = await GrenadeStoreMigration.RunAsync(cache, index);
            GrenadeStoreMigrationResult again = await GrenadeStoreMigration.RunAsync(cache, index);

            GrenadeLineup lineup = index.Query(new GrenadeQuery(Map))[0].Lineups[0];
            GrenadeLineupStore reread = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(result.Converted).IsEqualTo(3);
                await Assert.That(result.PathsStored).IsEqualTo(1).Because("one lineup, one technique");
                await Assert.That(result.PathFilesDeleted).IsEqualTo(3);
                await Assert.That(again.Demos).IsEqualTo(0).Because("the marker ends it");
                foreach (string path in new[] { "/d/a.dem", "/d/b.dem", "/d/c.dem" })
                {
                    await Assert.That(cache.TryReadSiblingBytes(path, GrenadeSidecar.Suffix)).IsNull();
                    await Assert.That(cache.TryReadSiblingBytes(path, GrenadeSidecar.PathsSuffix)).IsNull();
                    await Assert.That(GrenadeSidecar.ReadLog(cache, path)).IsNotNull();
                }

                await Assert.That(lineup.Id).IsEqualTo(before).Because("the conversion does not move a lineup");
                await Assert.That(lineup.Throws.Select(t => t.Row.ThrowerName))
                    .IsEquivalentTo(new string?[] { "window guy", "window guy", null })
                    .Because("names come from the record by SteamID; the third thrower is not in it");
                await Assert.That(index.PathFor(lineup, null)!.Count).IsEqualTo(2);
                await Assert.That(reread.For(Map).Paths.Count).IsEqualTo(1).Because("the flight is on disk");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ACorruptPathsFile_IsKept_AndWithholdsTheMarker_UntilItIsGivenUp()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            OldFormat(cache, "/d/a.dem", "sha-a", Smoke("g1-1", 1300, 3, "76561198000000003"));
            OldFormat(cache, "/d/b.dem", "sha-b", Smoke("g1-1", 1305, 3, "76561198000000003"));
            cache.WriteSiblingBytes("/d/b.dem", GrenadeSidecar.PathsSuffix, [0x1F, 0x8B, 1, 2, 3]);
            using GrenadeIndex index = new(cache);
            index.Load();

            GrenadeStoreMigrationResult first = await GrenadeStoreMigration.RunAsync(cache, index);
            using (Assert.Multiple())
            {
                await Assert.That(first.Converted).IsEqualTo(2).Because("the rows are good");
                await Assert.That(first.Completed).IsFalse();
                await Assert.That(first.PathFilesKept).IsEqualTo(1);
                await Assert.That(cache.TryReadSiblingBytes("/d/b.dem", GrenadeSidecar.PathsSuffix)).IsNotNull();
                await Assert.That(cache.TryReadSiblingBytes("/d/a.dem", GrenadeSidecar.PathsSuffix)).IsNull()
                    .Because("a's file read and the lineup's flight came from it");
                await Assert.That(File.Exists(Path.Combine(root, GrenadeStoreMigration.MarkerFileName))).IsFalse();
            }

            GrenadeStoreMigrationResult last = first;
            for (int pass = 2; pass <= GrenadeStoreMigration.MaxAttempts; pass++)
            {
                last = await GrenadeStoreMigration.RunAsync(cache, index);
            }

            using (Assert.Multiple())
            {
                await Assert.That(last.PathFilesGivenUp).IsEqualTo(1);
                await Assert.That(last.Completed).IsTrue().Because("a file that never reads stops blocking the marker");
                await Assert.That(cache.TryReadSiblingBytes("/d/b.dem", GrenadeSidecar.PathsSuffix)).IsNotNull().Because("it is kept, not deleted");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task APathsFileWhoseLineupsGotTheirFlightElsewhere_IsDeleted()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            OldFormat(cache, "/d/a.dem", "sha-a", Smoke("g1-1", 1300, 3, "76561198000000003"));
            OldFormat(cache, "/d/b.dem", "sha-b", Smoke("g1-1", 1305, 3, "76561198000000003"));

            // b's file reads but holds no flight for its throw: the lineup's flight can only come from a.
            GrenadePathsDocument empty = new() { Demo = new GrenadeDemoHeader { Sha256 = "sha-b", StableKey = DemoCacheStore.StableKey("/d/b.dem") } };
            cache.WriteSiblingBytes("/d/b.dem", GrenadeSidecar.PathsSuffix, SidecarJson.SerializeGzip(empty, GrenadeSidecar.JsonOptions));
            using GrenadeIndex index = new(cache);
            index.Load();

            GrenadeStoreMigrationResult result = await GrenadeStoreMigration.RunAsync(cache, index);
            GrenadeLineup lineup = index.Query(new GrenadeQuery(Map))[0].Lineups[0];
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsTrue();
                await Assert.That(result.PathFilesDeleted).IsEqualTo(2);
                await Assert.That(index.LineupStore.For(Map).Paths.Values.Single().Throw).StartsWith("sha-a");
                await Assert.That(lineup.Throws.Count).IsEqualTo(2);
                await Assert.That(cache.TryReadSiblingBytes("/d/b.dem", GrenadeSidecar.PathsSuffix)).IsNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ARowsFileThatDoesNotRead_IsKept_AndNoMarkerIsWritten()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore cache = new(root);
            OldFormat(cache, "/d/a.dem", "sha-a", Smoke("g1-1", 1300, 3, "76561198000000003"));
            OldFormat(cache, "/d/bad.dem", "sha-b", Smoke("g1-1", 1305, 3, "76561198000000003"));
            GrenadeDocument other = new() { Demo = new GrenadeDemoHeader { Sha256 = "someone-else" }, Grenades = [] };
            cache.WriteSiblingBytes("/d/bad.dem", GrenadeSidecar.Suffix, SidecarJson.SerializeGzip(other, GrenadeSidecar.JsonOptions));
            using GrenadeIndex index = new(cache);
            index.Load();

            GrenadeStoreMigrationResult result = await GrenadeStoreMigration.RunAsync(cache, index);
            using (Assert.Multiple())
            {
                await Assert.That(result.Completed).IsFalse();
                await Assert.That(result.Failed).IsEqualTo(1);
                await Assert.That(cache.TryReadSiblingBytes("/d/bad.dem", GrenadeSidecar.Suffix)).IsNotNull().Because("it is kept");
                await Assert.That(GrenadeSidecar.ReadLog(cache, "/d/a.dem")).IsNotNull();
                await Assert.That(File.Exists(Path.Combine(root, GrenadeStoreMigration.MarkerFileName))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ADemoWalkedThisSession_GivesItsLineupAFlight_AndNoPathsFile()
    {
        DemoCacheStore cache = new(null);
        int n = 0;
        GrenadeIndexEvaluator evaluator = new(cache, () => true, () => null,
            walk: _ => new GrenadeWalk([Smoke("g1-1", 1300 + 5 * n++, 3, "76561198000000003")], 1, ReconstructedInputSource.DecoderName, 4));
        using GrenadeIndex index = new(cache, evaluator: evaluator);
        index.Load();
        foreach (string path in new[] { "/d/a.dem", "/d/b.dem" })
        {
            DemoCacheRecord record = RoundIndexTestData.ParsedRecord(path, Map, "sha" + path);
            record.Players = [new CachedPlayerInfo { Slot = 3, Name = "window guy", SteamId64 = "76561198000000003", Team = 2 }];
            cache.Upsert(record);
            evaluator.Evaluate(path, RoundIndexTestData.Demo(lastTick: 5000));
        }

        GrenadeLineup lineup = index.Query(new GrenadeQuery(Map))[0].Lineups[0];
        using (Assert.Multiple())
        {
            await Assert.That(lineup.Throws.Count).IsEqualTo(2);
            await Assert.That(index.PathFor(lineup, null)!.Count).IsEqualTo(2);
            await Assert.That(cache.TryReadSiblingBytes("/d/a.dem", GrenadeSidecar.PathsSuffix)).IsNull();
            await Assert.That(evaluator.TakeFlights("/d/a.dem")).IsNull().Because("the index took them");
        }
    }
}
