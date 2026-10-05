#region

using System.Numerics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The throw log, the grenade store and the lineup store: every field survives the log, the store keeps
///     the rows under the walker version and refuses another demo's, the lineups are copied once from where an
///     older build kept them, and a demo walked this session gives its lineup a flight.
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
        byte[] json = Gzip(System.Text.Encoding.UTF8.GetBytes(GrenadeSidecar.Serialize(document)));
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

    [Test]
    public async Task TheStore_RoundTripsTheRows_UnderTheWalkerVersion_AndRefusesAnotherDemosRows()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(RoundIndexTestData.ParsedRecord("/d/a.dem", Map, "abc"));
        GrenadeStore store = cache.Grenades();
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = "abc", StableKey = "k" },
            Grenades = [Full("g1-1"), Full("g2-1")]
        };

        store.Write("/d/a.dem", document);

        using (Assert.Multiple())
        {
            await Assert.That(store.IsCurrent("/d/a.dem")).IsTrue();
            await Assert.That(store.Stamp("/d/a.dem")!.Count).IsEqualTo(2);
            await Assert.That(store.TryReadRows("/d/a.dem", "abc")!.Grenades.Count).IsEqualTo(2);
            await Assert.That(store.TryReadRows("/d/a.dem", "def")).IsNull().Because("rows naming another demo's hash are ignored");
        }

        store.MarkFailed("/d/a.dem");
        await Assert.That(store.Needs("/d/a.dem")).IsFalse().Because("retrying a failure is the user's call");
        store.ClearFailed("/d/a.dem");
        await Assert.That(store.Needs("/d/a.dem")).IsTrue();
    }

    [Test]
    public async Task TheLineups_AreCopiedOnce_FromWhereAnOlderBuildKeptThem()
    {
        string root = TempRoot();
        try
        {
            string legacy = Path.Combine(root, "cache", GrenadeLineupStore.FileName);
            GrenadeLineupStore old = new(Path.Combine(root, "cache"));
            LineupAnchor anchor = new(Guid.NewGuid(), GrenadeKind.Smoke, new WorldPoint(1, 2, 3), new WorldPoint(4, 5, 6), "seed");
            old.For(Map).Anchors.Add(anchor);
            old.Save();

            DemoViewer.NET.Extensions.ExtensionContext.StorageView storage = new("dev.example.lineups", Path.Combine(root, "config"),
                Path.Combine(root, "cache"));
            GrenadeLineupStore copied = GrenadeLineupStore.In(storage, legacy);
            using (Assert.Multiple())
            {
                await Assert.That(copied.For(Map).Anchors.Single()).IsEqualTo(anchor);
                await Assert.That(await storage.ReadAsync(DemoViewer.NET.Extensions.Sdk.StoreRoot.Cache, GrenadeLineupStore.FileName)).IsNotNull()
                    .Because("the copy lands in the extension's own cache folder");
            }

            // The copy is the store now: a change there is what the next start reads, not the old file.
            copied.For(Map).Anchors.Clear();
            copied.Save();
            GrenadeLineupStore reopened = GrenadeLineupStore.In(storage, legacy);
            await Assert.That(reopened.For(Map).Anchors).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ADemoWalkedThisSession_GivesItsLineupAFlight()
    {
        DemoCacheStore cache = new(null);
        int n = 0;
        GrenadeIndexEvaluator evaluator = new(cache.Library(), cache.Grenades(), () => true, () => null,
            walk: _ => new GrenadeWalk([Smoke("g1-1", 1300 + 5 * n++, 3, "76561198000000003")], 1, ReconstructedInputSource.DecoderName, 4));
        using GrenadeIndex index = new(cache.Library(), evaluator: evaluator);
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
            await Assert.That(evaluator.TakeFlights("/d/a.dem")).IsNull().Because("the index took them");
        }
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using MemoryStream buffer = new();
        using (System.IO.Compression.GZipStream gzip = new(buffer, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            gzip.Write(bytes);
        }

        return buffer.ToArray();
    }
}
