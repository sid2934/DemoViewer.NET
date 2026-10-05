#region

using DemoViewer.NET.Extensions.StratBook;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The grenade evaluator and its storage over an in-memory (and, where deletion is the point, an
///     on-disk) cache: wanted from the index row alone and only with the
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
        string? root = null, Func<bool>? enabled = null)
    {
        DemoCacheStore cache = new(root);
        cache.Upsert(RoundIndexTestData.ParsedRecord(Demo, sha: sha));
        GrenadeIndexEvaluator evaluator = new(cache.Library(), new GrenadeStore(root is null ? cache.Data() : cache.DiskData(root)), () => background,
            () => open, walk: walk ?? OneSmoke, enabled: enabled);
        return (cache, evaluator);
    }

    [Test]
    public async Task TheClipPass_FollowsTheWalk_RunsOnceOnItsVisit_AndThenWantsNothing()
    {
        (_, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        using LineupClipService clips = new(() => [], "/clips", () => true, new NoRender());
        using LineupClipPass pass = new(evaluator, clips);
        ParsedDemo parsed = Parse();

        DemoInterest beforeWalk = pass.Interest(Demo);
        evaluator.Evaluate(Demo, parsed);
        DemoInterest afterWalk = pass.Interest(Demo);
        pass.Run(new Context(Demo, parsed));
        DemoInterest afterRun = pass.Interest(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(beforeWalk).IsEqualTo(DemoInterest.AfterUpstream).Because("it joins the visit the walk is on");
            await Assert.That(afterWalk).IsEqualTo(DemoInterest.Yes);
            await Assert.That(afterRun).IsEqualTo(DemoInterest.No)
                .Because("a re-check after the visit must not read the demo again for its clips");
        }
    }

    [Test]
    public async Task TheClipPass_WithClipsOff_IsNeverOnAVisit()
    {
        (_, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        using LineupClipService clips = new(() => [], "/clips", () => false, new NoRender());
        using LineupClipPass pass = new(evaluator, clips);

        evaluator.Evaluate(Demo, Parse());

        await Assert.That(pass.Interest(Demo)).IsEqualTo(DemoInterest.No);
    }

    private sealed class NoRender : ILineupClipRenderer
    {
        public Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, ParsedDemo? demo,
            IReadOnlyList<LineupClipJob> jobs, CancellationToken ct) => Task.FromResult(jobs);
    }

    private sealed class Context(string path, ParsedDemo parsed) : IPassContext
    {
        public string DemoPath => path;

        public ParsedDemo Parsed => parsed;

        public CancellationToken CancellationToken => CancellationToken.None;
    }

    [Test]
    public async Task Wanted_OnlyWithTheOptIn_AndNeverWithoutAParse()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator off) = Wire();
        GrenadeIndexEvaluator on = new(cache.Library(), cache.Grenades(), () => true, walk: OneSmoke);
        cache.Upsert(new DemoCacheRecord { Path = "/d/unparsed.dem", Size = 1, ModifiedTicks = 1 });

        using (Assert.Multiple())
        {
            await Assert.That(off.Wants(Demo)).IsFalse().Because("background indexing is off by default");
            await Assert.That(off.PendingPaths()).IsEmpty();
            await Assert.That(on.Wants(Demo)).IsTrue();
            await Assert.That(on.Wants("/d/unparsed.dem")).IsFalse();
            await Assert.That(on.WantsAfterUpstream("/d/unparsed.dem")).IsTrue().Because("the walk follows the Library's parse on the same visit");
            await Assert.That(on.WantsAfterUpstream(Demo)).IsFalse().Because("a parsed demo is Wants' call");
            await Assert.That(off.WantsAfterUpstream("/d/unparsed.dem")).IsFalse();
            await Assert.That(on.PendingPaths()).IsEquivalentTo(new[] { Demo });
            await Assert.That(on.PriorityFor(Demo)).IsEqualTo(JobPriority.Background);
        }
    }

    [Test]
    public async Task WithThePackOff_NothingIsWanted_AndTheOpenDemoIsNotWalkedEither()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(RoundIndexTestData.ParsedRecord(Demo, sha: "abc"));
        GrenadeIndexEvaluator evaluator = new(cache.Library(), cache.Grenades(), () => true, () => Demo, walk: OneSmoke, enabled: () => false);

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsFalse()
                .Because("the open demo's walk rides its Wants, which the pack gate forces off");
            await Assert.That(evaluator.WantsAfterUpstream(Demo)).IsFalse();
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
            await Assert.That(evaluator.Store.TryReadRows(Demo, "abc")).IsNull();
        }
    }

    // Match Overview's "Index grenades" chip is core and the view model hides it when the pack is off
    // (MatchOverviewGrenadeActionTests), but the evaluator must refuse the request on its own too: a forced
    // path left behind here would walk unasked the moment the pack came back on.
    [Test]
    public async Task WithThePackOff_Request_IsANoOp_AndLeavesNoForcedPathBehind()
    {
        (_, GrenadeIndexEvaluator evaluator) = Wire(enabled: () => false);

        evaluator.Request(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsFalse();
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
            await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(JobPriority.Background)
                .Because("Request must not have added a forced path");
        }
    }

    [Test]
    public async Task WithThePackOff_Request_DoesNotLiftAFailedRowBackToPending()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(enabled: () => false);
        evaluator.Store.MarkFailed(Demo);

        evaluator.Request(Demo);

        await Assert.That(cache.GrenadeState(Demo)).IsEqualTo(DemoDataState.Failed)
            .Because("a no-op Request clears nothing, not even a stamp a retry would normally lift");
    }

    // The coordinator's exact scenario: a click while the pack is off, then the pack comes back on
    // without a restart. Background is off by default (Wire), so only a leftover forced path could
    // make Wants true here.
    [Test]
    public async Task WithThePackOff_Request_LeavesNothingForWhenThePackComesBackOn()
    {
        bool on = false;
        (_, GrenadeIndexEvaluator evaluator) = Wire(enabled: () => on);

        evaluator.Request(Demo);
        on = true;

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsFalse()
                .Because("the demo must not be walked unasked now that the pack is back on");
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
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
            await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(JobPriority.UserRequested);
            await Assert.That(evaluator.PendingPaths()).IsEquivalentTo(new[] { Demo });
        }
    }

    [Test]
    public async Task Evaluate_WritesTheRowsThenTheStamp_NoPaths_AndTheIndexMirrorsIt()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        List<string> indexed = [];
        evaluator.Indexed += indexed.Add;

        evaluator.Evaluate(Demo, Parse());

        DemoDataStamp stamp = evaluator.Store.Stamp(Demo)!;
        GrenadeDocument rows = evaluator.Store.TryReadRows(Demo, "abc")!;
        using (Assert.Multiple())
        {
            await Assert.That(indexed).IsEquivalentTo(new[] { Demo });
            await Assert.That(stamp.Schema).IsEqualTo(GrenadeStore.Schema);
            await Assert.That(stamp.State).IsEqualTo(DemoDataState.Written);
            await Assert.That(stamp.Count).IsEqualTo(1);
            await Assert.That(stamp.Fingerprint).IsEqualTo(GrenadeWalker.Version);
            await Assert.That(stamp.WrittenAtTicks).IsGreaterThan(0);
            await Assert.That(rows.Source.InputCoverage).IsEqualTo(0.998);
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
        }
    }

    [Test]
    public async Task AParseWithoutUserCommands_IsNotWalked_AndTheDemoStaysWanted()
    {
        int walks = 0;
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true, walk: d =>
        {
            walks++;
            return OneSmoke(d);
        });
        ParsedDemo narrowed = SyntheticParsedDemo.Create(tickCount: 5000, plan: DemoProcessingQueue.WithoutUserCommands);

        evaluator.Evaluate(Demo, narrowed);

        using (Assert.Multiple())
        {
            await Assert.That(walks).IsEqualTo(0);
            await Assert.That(evaluator.Store.TryReadRows(Demo, "abc")).IsNull();
            await Assert.That(cache.GrenadeState(Demo)).IsNotEqualTo(DemoDataState.Failed);
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(((IExtensionPass)evaluator).ReadsUserCommands).IsTrue();
        }
    }

    [Test]
    public async Task AForcedRequest_OnAParseWithoutUserCommands_StaysForcedAndWanted()
    {
        int walks = 0;
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(walk: d =>
        {
            walks++;
            return OneSmoke(d);
        });
        evaluator.Request(Demo);

        evaluator.Evaluate(Demo, SyntheticParsedDemo.Create(tickCount: 5000, plan: DemoProcessingQueue.WithoutUserCommands));

        using (Assert.Multiple())
        {
            await Assert.That(walks).IsEqualTo(0);
            await Assert.That(evaluator.Store.TryReadRows(Demo, "abc")).IsNull();
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(JobPriority.UserRequested);
        }

        evaluator.Evaluate(Demo, Parse());
        await Assert.That(walks).IsEqualTo(1);
        await Assert.That(evaluator.PriorityFor(Demo)).IsEqualTo(JobPriority.Background);
    }

    [Test]
    public async Task TheOpenDemo_IsWantedWithoutTheOptIn_AndNoOtherDemoIs()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(open: "/D/MATCH.dem");
        cache.Upsert(RoundIndexTestData.ParsedRecord("/d/other.dem"));

        await Assert.That(evaluator.Wants("/d/other.dem")).IsFalse()
            .Because("another demo's visit does not turn the sweep the user left off back on");
        await Assert.That(evaluator.Wants(Demo)).IsTrue().Because("the open demo is walked on the parse its open paid for");
        evaluator.Evaluate(Demo, Parse());

        using (Assert.Multiple())
        {
            await Assert.That(cache.GrenadeState(Demo)).IsEqualTo(DemoDataState.Written);
            await Assert.That(cache.GrenadeState("/d/other.dem")).IsEqualTo(DemoDataState.Pending);
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
            await Assert.That(cache.GrenadeState(Demo)).IsEqualTo(DemoDataState.Failed);
            await Assert.That(evaluator.Wants(Demo)).IsFalse();
            await Assert.That(evaluator.PendingPaths()).IsEmpty();
            await Assert.That(evaluator.Store.TryReadRows(Demo, "abc")).IsNull();
        }

        evaluator.Request(Demo);

        using (Assert.Multiple())
        {
            await Assert.That(cache.GrenadeState(Demo)).IsEqualTo(DemoDataState.Pending);
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
        }
    }

    [Test]
    public async Task AnotherWalkerVersion_IsStale_AndItsRowsAreNotRead()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        evaluator.Evaluate(Demo, Parse());

        cache.Data().Invalidate(GrenadeStore.Facet, Demo);

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Wants(Demo)).IsTrue();
            await Assert.That(evaluator.Store.TryReadRows(Demo, "abc")).IsNull();
        }
    }

    [Test]
    public async Task AFileNamingAnotherDemosHash_IsIgnored()
    {
        (DemoCacheStore cache, GrenadeIndexEvaluator evaluator) = Wire(background: true);
        evaluator.Evaluate(Demo, Parse());
        await Assert.That(evaluator.Store.TryReadRows(Demo, "abc")).IsNotNull();


        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Store.TryReadRows(Demo, "def")).IsNull();
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
    public async Task RemovingTheDemo_DeletesItsRows_OnDiskAndInMemory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-grenades-{Guid.NewGuid():N}");
        try
        {
            (DemoCacheStore disk, GrenadeIndexEvaluator onDisk) = Wire(background: true, root: root);
            (DemoCacheStore memory, GrenadeIndexEvaluator inMemory) = Wire(background: true);
            onDisk.Evaluate(Demo, Parse());
            inMemory.Evaluate(Demo, Parse());
            string rowsFile = Directory.GetFiles(root, "abc.json.gz", SearchOption.AllDirectories).Single();

            // Another demo's rows must survive.
            disk.Upsert(RoundIndexTestData.ParsedRecord("/d/other.dem", sha: "fff"));
            onDisk.Evaluate("/d/other.dem", Parse());

            disk.Remove(Demo);
            memory.Remove(Demo);

            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(rowsFile)).IsFalse();
                await Assert.That(onDisk.Store.Stamp("/d/other.dem")).IsNotNull();
                await Assert.That(inMemory.Store.Stamp(Demo)).IsNull();
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
