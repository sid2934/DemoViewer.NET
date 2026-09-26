#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The grenade evaluator and its storage over an in-memory (and, where deletion is the point, an
///     on-disk) cache (grenade-walk.md §7, evaluator): wanted from the index row alone and only with the
///     opt-in or a forced request, the two siblings written before the stamp and the index mirroring it,
///     the open demo walked without the opt-in and no other, a throw stamping Failed until retried, the
///     siblings deleted with the demo, and the reader's hash rule.
/// </summary>
public class GrenadeIndexEvaluatorTests
{
    private const string Demo = "/d/match.dem";

    private static ParsedDemo Parse() => RoundIndexTestData.Demo(lastTick: 5000);

    private static GrenadeWalk OneSmoke(ParsedDemo _) => new(
    [
        new GrenadeRow
        {
            Id = "g120-7",
            Kind = GrenadeKind.Smoke,
            ThrowerSlot = 3,
            ReleaseTick = 93,
            ReleasePosition = new WorldPoint(-2260, -1036, -414),
            ReleaseEyePitch = -18.12f,
            ReleaseEyeYaw = -15.3f,
            SpawnTick = 100,
            JumpThrowSource = JumpThrowSource.GroundFlag,
            DetonationTick = 300,
            DetonationSource = DetonationSource.Event,
            EndKind = GrenadeEndKind.Detonated,
            Trajectory = [new TrajectoryPoint(100, 1, 2, 3, 0), new TrajectoryPoint(140, 10.26f, 20, 30, 1)]
        }
    ], 0.998, ReconstructedInputSource.DecoderName, 4);

    private static (DemoCacheStore Cache, GrenadeIndexEvaluator Evaluator) Wire(
        bool background = false, string? open = null, string? sha = "abc", Func<ParsedDemo, GrenadeWalk>? walk = null,
        string? root = null)
    {
        DemoCacheStore cache = new(root);
        cache.Upsert(RoundIndexTestData.ParsedRecord(Demo, sha: sha));
        GrenadeIndexEvaluator evaluator = new(cache, () => background, () => open, walk: walk ?? OneSmoke);
        return (cache, evaluator);
    }

    [Test]
    public async Task Wanted_OnlyWithTheOptIn_AndNeverWithoutAParse()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator off) = Wire();
        GrenadeIndexEvaluator on = new(cache, () => true, walk: OneSmoke);
        cache.Upsert(new DemoCacheRecord { Path = "/d/unparsed.dem", Size = 1, ModifiedTicks = 1 });

        using (Assert.Multiple())
        {
            await Assert.That(off.Wants(Demo)).IsFalse().Because("background indexing is off by default");
            await Assert.That(off.PendingPaths()).IsEmpty();
            await Assert.That(on.Wants(Demo)).IsTrue();
            await Assert.That(on.Wants("/d/unparsed.dem")).IsFalse();
            await Assert.That(on.PendingPaths()).IsEquivalentTo(new[] { Demo });
            await Assert.That(on.PriorityFor(Demo)).IsEqualTo(DemoJobPriority.Background);
        }
    }

    [Test]
    public async Task ARequest_IsWantedAtUserPriority_WhateverTheOptInSays()
    {
        (_, GrenadeIndexEvaluator evaluator) = Wire();

        evaluator.Request(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(DemoJobPriority.UserRequested);
            await Assert.That(evaluator.PendingPaths()).IsEquivalentTo(new[] { Demo });
        }
    }

    [Test]
    public async Task Evaluate_WritesBothSiblingsThenTheStamp_AndTheIndexMirrorsIt()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        List<string> indexed = [];
        evaluator.Indexed += indexed.Add;

        evaluator.Evaluate(Demo, Parse());

        DemoCacheIndexEntry entry = cache.TryGetIndex(Demo)!;
        DemoCacheRecord record = cache.TryLoadRecord(Demo)!;
        GrenadeDocument rows = GrenadeSidecar.TryReadRows(cache, Demo)!;
        GrenadePathsDocument paths = GrenadeSidecar.TryReadPaths(cache, Demo)!;
        using (Assert.Multiple())
        {
            await Assert.That(indexed).IsEquivalentTo(new[] { Demo });
            await Assert.That(entry.GrenadeSchema).IsEqualTo(DemoCacheRecord.GrenadeSchema);
            await Assert.That(entry.GrenadeState).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(entry.GrenadeCount).IsEqualTo(1);
            await Assert.That(entry.GrenadeWalker).IsEqualTo(GrenadeWalker.Version);
            await Assert.That(record.GrenadeInputCoverage).IsEqualTo(0.998);
            await Assert.That(record.Grenades.IsPresent).IsTrue();
            await Assert.That(evaluator.Wants(Demo)).IsFalse().Because("current under this walker");
            await Assert.That(evaluator.IsCurrent(Demo)).IsTrue();

            await Assert.That(rows.Demo.Sha256).IsEqualTo("abc");
            await Assert.That(rows.Demo.StableKey).IsEqualTo(DemoCacheStore.StableKey(Demo));
            await Assert.That(rows.Walker.Version).IsEqualTo(GrenadeWalker.Version);
            await Assert.That(rows.Walker.Engine).StartsWith("CS2DemoKit.Parser ");
            await Assert.That(rows.Walker.InputDecoder).IsEqualTo(ReconstructedInputSource.DecoderName);
            await Assert.That(rows.Clock.LastTick).IsEqualTo(5000);
            await Assert.That(rows.Source.TrajectoryStride).IsEqualTo(4);
            await Assert.That(rows.Grenades.Single().Trajectory).IsEmpty().Because("the rows file carries no paths");
            await Assert.That(rows.Grenades.Single().ReleaseEyeYaw).IsEqualTo(-15.3f);
            await Assert.That(paths.Paths["g120-7"].Count).IsEqualTo(2);
            await Assert.That(paths.Paths["g120-7"][1]).IsEqualTo(new TrajectoryPoint(140, 10.3f, 20, 30, 1))
                .Because("paths are written at one decimal");
        }
    }

    [Test]
    public async Task TheOpenDemo_IsWalkedOnItsOwnParseWithoutTheOptIn_AndNoOtherDemoIs()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(open: "/D/MATCH.dem");
        cache.Upsert(RoundIndexTestData.ParsedRecord("/d/other.dem"));

        evaluator.OnParsedOpportunistically("/d/other.dem", Parse());
        evaluator.OnParsedOpportunistically(Demo, Parse());

        using (Assert.Multiple())
        {
            await Assert.That(cache.TryGetIndex(Demo)!.GrenadeState).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(cache.TryGetIndex("/d/other.dem")!.GrenadeState).IsEqualTo(DemoAnalysisState.Pending)
                .Because("a Library tier-2 pass does not turn the sweep the user left off back on");
        }
    }

    [Test]
    public async Task AThrow_StampsFailed_AndIsNotWantedAgainUntilRequested()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true,
            walk: _ => throw new InvalidOperationException("boom"));

        evaluator.Evaluate(Demo, Parse());

        using (Assert.Multiple())
        {
            await Assert.That(cache.TryGetIndex(Demo)!.GrenadeState).IsEqualTo(DemoAnalysisState.Failed);
            await Assert.That(evaluator.Wants(Demo)).IsFalse();
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
            await Assert.That(GrenadeSidecar.TryReadRows(cache, Demo)).IsNull();
        }

        evaluator.Request(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(cache.TryGetIndex(Demo)!.GrenadeState).IsEqualTo(DemoAnalysisState.Pending);
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
        }
    }

    [Test]
    public async Task AnotherWalkerVersion_IsStale_AndItsRowsAreNotRead()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        evaluator.Evaluate(Demo, Parse());

        cache.UpdateExisting(Demo, r => r.GrenadeWalker = "0");

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(GrenadeSidecar.TryReadRows(cache, Demo)).IsNull();
        }
    }

    [Test]
    public async Task AFileNamingAnotherDemosHash_IsIgnored()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        evaluator.Evaluate(Demo, Parse());
        await Assert.That(GrenadeSidecar.TryReadRows(cache, Demo)).IsNotNull();

        cache.UpdateExisting(Demo, r => r.Sha256 = "def");

        using (Assert.Multiple())
        {
            await Assert.That(GrenadeSidecar.TryReadRows(cache, Demo)).IsNull();
            await Assert.That(GrenadeSidecar.TryReadPaths(cache, Demo)).IsNull();
            await Assert.That(GrenadeSidecar.SameDemo(null, "abc")).IsTrue().Because("a path-keyed row accepts any file");
            await Assert.That(GrenadeSidecar.SameDemo("abc", null)).IsTrue();
            await Assert.That(GrenadeSidecar.SameDemo("ABC", "abc")).IsTrue();
        }
    }

    [Test]
    public async Task TheRowsFile_RoundTrips()
    {
        ParsedDemo parsed = Parse();
        (GrenadeDocument rows, GrenadePathsDocument paths) = GrenadeSidecar.Build(Demo, null, parsed, OneSmoke(parsed));
        string json = GrenadeSidecar.Serialize(rows);
        string pathsJson = GrenadeSidecar.Serialize(paths);

        GrenadeDocument back = GrenadeSidecar.TryDeserializeRows(json)!;
        GrenadePathsDocument pathsBack = GrenadeSidecar.TryDeserializePaths(pathsJson)!;

        using (Assert.Multiple())
        {
            await Assert.That(GrenadeSidecar.Serialize(back)).IsEqualTo(json);
            await Assert.That(GrenadeSidecar.Serialize(pathsBack)).IsEqualTo(pathsJson);
            await Assert.That(json).Contains("\"kind\":\"Smoke\"").Because("enums are written by name");
            await Assert.That(json).Contains("\"releasePosition\":[-2260,-1036,-414]");
            await Assert.That(json).Contains("\"sha256\":null").Because("the hash is visibly null until Content Identity fills it");
            await Assert.That(GrenadeSidecar.TryDeserializeRows("{\"schemaVersion\":99}")).IsNull();
            await Assert.That(GrenadeSidecar.TryDeserializeRows("not json")).IsNull();
        }
    }

    [Test]
    public async Task RemovingTheDemo_DeletesBothSiblings_OnDiskAndInMemory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-grenades-{Guid.NewGuid():N}");
        try
        {
            (DemoCacheStore disk, GrenadeIndexEvaluator onDisk) = Wire(background: true, root: root);
            (DemoCacheStore memory, GrenadeIndexEvaluator inMemory) = Wire(background: true);
            onDisk.Evaluate(Demo, Parse());
            inMemory.Evaluate(Demo, Parse());
            string rowsFile = disk.SiblingPathFor(Demo, GrenadeSidecar.Suffix)!;
            string pathsFile = disk.SiblingPathFor(Demo, GrenadeSidecar.PathsSuffix)!;
            await Assert.That(File.Exists(rowsFile) && File.Exists(pathsFile)).IsTrue();

            // Another demo's files in the same folder must survive.
            disk.Upsert(RoundIndexTestData.ParsedRecord("/d/other.dem"));
            disk.WriteSibling("/d/other.dem", GrenadeSidecar.Suffix, "{}");

            disk.Remove(Demo);
            memory.Remove(Demo);

            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(rowsFile)).IsFalse();
                await Assert.That(File.Exists(pathsFile)).IsFalse();
                await Assert.That(disk.TryReadSibling("/d/other.dem", GrenadeSidecar.Suffix)).IsEqualTo("{}");
                await Assert.That(memory.TryReadSibling(Demo, GrenadeSidecar.Suffix)).IsNull();
                await Assert.That(memory.TryReadSibling(Demo, GrenadeSidecar.PathsSuffix)).IsNull();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task ASiblingSuffix_MustNotBeTheRecordsOwn()
    {
        DemoCacheStore cache = new(null);

        Assert.Throws<ArgumentException>(() => cache.WriteSibling(Demo, ".json", "{}"));
        Assert.Throws<ArgumentException>(() => cache.WriteSibling(Demo, "grenades.json", "{}"));
        await Assert.That(cache.TryReadSibling(Demo, ".grenades.json")).IsNull();
    }
}
