#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using Library = DemoViewer.NET.AppTests.StratMiningServiceTests.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The per-demo signature cache: after every kind of change a cached build prints exactly what an uncached one
///     does, and reads files only for the demos whose inputs moved. Also the mine's gate slot and the quiet
///     re-mine held back while the processing queue has work.
/// </summary>
[NotInParallel]
public class StratMiningCacheTests
{
    private const string Map = "de_mirage";

    private static string Text(IEnumerable<RoundSignature> signatures)
    {
        StringBuilder text = new();
        foreach (RoundSignature s in signatures)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{s.Key} {s.Sha256} {s.Map} {s.Site} {s.Buy} {s.Won} {s.TeamId} {s.TickRate} {s.FreezeEndTick} {s.AnchorTick}");
            foreach (IReadOnlyList<MinedPawn> anchor in s.Anchors)
            {
                text.Append(" [").Append(string.Join(";", anchor.Select(p => $"{p.Slot},{p.X:R},{p.Y:R},{p.Z:R},{p.Place}"))).Append(']');
            }

            text.Append(s.Throws is null ? " no-rows" : " throws " + string.Join(";", s.Throws.Select(t =>
                string.Create(CultureInfo.InvariantCulture, $"{t.Kind},{t.Seconds:R},{t.Landing.X},{t.LineupId}"))));
            text.Append('\n');
        }

        return text.ToString();
    }

    private static MiningGrenade Grenade(int demo, Guid lineup) =>
        new(new IndexedGrenade(new global::DemoViewer.NET.Extensions.StratBook.DemoRef($"/d/m{demo}.dem", global::DemoViewer.NET.Extensions.StratBook.DemoKeys.StableKey($"/d/m{demo}.dem"), null), Map,
            new GrenadeRow
            {
                Id = "g1",
                Kind = GrenadeKind.Smoke,
                ThrowerSlot = 2,
                ThrowerTeam = 2,
                RoundNumber = 1,
                ReleaseTick = 1000 + 5 * 64,
                ReleasePosition = new WorldPoint(100, 50, 0),
                DetonationTick = 1000 + 7 * 64,
                DetonationPosition = new WorldPoint(1200, 400, 0)
            }, new WorldPoint(100, 50, 0), new WorldPoint(1200, 400, 0), "BombsiteA", null), lineup);

    private sealed class Rig
    {
        public string Fingerprint { get; set; } = StratMiningServiceTests._sources.FingerprintFor(Map);

        public Guid Lineup { get; set; } = Guid.NewGuid();

        public TeamIdentityService? Teams { get; set; }

        public RoundSignatureBuilder Builder(Library library, SignatureCache? cache) =>
            new(library.Cache.Library(), library.Cache.RoundFacts(), library.Positions, _ => Fingerprint,
                map => [Grenade(1, Lineup), Grenade(2, Lineup)], Teams, cache);
    }

    private sealed class RecordFacts(DemoCacheStore cache) : IRoundFactsSource
    {
        public int Schema => RoundFactsRecords.Schema;

        public event Action<string>? Updated
        {
            add { }
            remove { }
        }

        public RoundFactsRows? TryGet(string demoPath) => cache.TryLoadRecord(demoPath)?.RoundFacts;

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) => null;

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];
    }

    [Test]
    public async Task ATeamChange_ReachesTheCachedSignatures()
    {
        using Library library = Library.Create();
        // One T five across every demo, so Team Identity forms a team on the T side.
        for (int n = 1; n <= 4; n++)
        {
            DemoCacheRecord record = library.Cache.TryLoadRecord($"/d/m{n}.dem")!;
            foreach (CachedPlayerInfo player in record.Players.Where(p => p.Slot < 5))
            {
                player.SteamId64 = $"76561190000000{player.Slot:D3}";
            }

            library.Cache.Upsert(record);
        }

        TeamIdentityService teams = new(null, library.Cache.Library(), new RecordFacts(library.Cache), run: a =>
        {
            a();
            return Task.CompletedTask;
        });
        await teams.StartAsync();
        await teams.Idle;
        Rig rig = new() { Teams = teams };
        SignatureCache cache = new(null);
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((0, 4));
        await Assert.That(rig.Builder(library, cache).Build().Any(s => s.TeamId is not null)).IsTrue().Because("the rig has a team");

        teams.Override("/d/m2.dem", 2, null);
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((3, 1)).Because("m2's T side left the team");

        teams.ClearOverride("/d/m2.dem", 2);
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((3, 1)).Because("and came back");
        teams.Dispose();
    }

    private static async Task<(int Reused, int Built)> BuildsAsUncached(Rig rig, Library library, SignatureCache cache)
    {
        RoundSignatureBuilder cached = rig.Builder(library, cache);
        string text = Text(cached.Build());
        await Assert.That(text).IsEqualTo(Text(rig.Builder(library, null).Build()));
        return cached.LastBuild;
    }

    [Test]
    public async Task ACachedBuild_MatchesAnUncachedOne_AndRebuildsOnlyWhatChanged()
    {
        using Library library = Library.Create();
        Rig rig = new();
        SignatureCache cache = new(null);

        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((0, 4)).Because("nothing cached yet");
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((4, 0)).Because("nothing changed");

        DemoCacheRecord record = library.Cache.TryLoadRecord("/d/m2.dem")!;
        record.ModifiedTicks += 1;
        library.Cache.Upsert(record);
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((3, 1)).Because("m2's record changed");

        rig.Lineup = Guid.NewGuid();
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((4, 0))
            .Because("a re-clustered lineup reaches the throws without a rebuild: throws are attached on every build");

        library.Cache.Remove("/d/m3.dem");
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((3, 0));
        await Assert.That(cache.Count).IsEqualTo(3).Because("a demo that left the library leaves the cache");

        rig.Fingerprint = "positions-v-next";
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((0, 3))
            .Because("a new positions fingerprint rebuilds every demo of the map");
        await Assert.That(await BuildsAsUncached(rig, library, cache)).IsEqualTo((0, 3))
            .Because("a demo whose positions are stale under the new fingerprint is not cached, so it is tried again");
    }

    [Test]
    public async Task TheFile_SurvivesARestart_AndACorruptOneRebuilds()
    {
        using Library library = Library.Create();
        Rig rig = new();
        string file = Path.Combine(library.Root, "strat-mining", "signatures.json.gz");

        await Assert.That(await BuildsAsUncached(rig, library, new SignatureCache(file))).IsEqualTo((0, 4));
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(await BuildsAsUncached(rig, library, new SignatureCache(file))).IsEqualTo((4, 0)).Because("read back from disk");

        await File.WriteAllTextAsync(file, "not gzip");
        await Assert.That(await BuildsAsUncached(rig, library, new SignatureCache(file))).IsEqualTo((0, 4)).Because("unreadable is empty");
        await Assert.That(await BuildsAsUncached(rig, library, new SignatureCache(file))).IsEqualTo((4, 0)).Because("and rewritten");
    }

    [Test]
    public async Task TheServiceMine_UsesTheCacheUnderItsCacheRoot()
    {
        using Library library = Library.Create();
        using StratMiningService service = library.Service();
        await service.MineAsync();
        IReadOnlyList<string> first = [.. service.Patterns.Select(p => p.Pattern.Key)];
        await Assert.That(service.Signatures.LastBuild).IsEqualTo((0, 4));

        using StratMiningService restarted = library.Service();
        await restarted.MineAsync();
        using (Assert.Multiple())
        {
            await Assert.That(restarted.Signatures.LastBuild).IsEqualTo((4, 0));
            await Assert.That(restarted.Patterns.Select(p => p.Pattern.Key)).IsEquivalentTo(first);
            await Assert.That(File.Exists(Path.Combine(library.Root, "cache", "strat-mining", "signatures.json.gz"))).IsTrue();
        }
    }

    [Test]
    public async Task TheQuietRemine_WaitsForThePacksPassesToSettle_AndAUserMineDoesNot()
    {
        using Library library = Library.Create();
        InlineJobs jobs = new();
        FakePasses passes = new() { Busy = true };
        using StratMiningService service = new(library.Cache.Library(), library.Cache.RoundFacts(), library.Positions, StratMiningServiceTests._sources.FingerprintFor, null, null,
            library.Strats, library.Tags, null, null, run: a =>
            {
                a();
                return Task.CompletedTask;
            }, jobs: jobs, passes: passes) { QuietDelay = Timeout.InfiniteTimeSpan };

        int Mines() => jobs.Submitted.Count(j => j.Title == "Strat mining: library");

        await service.MineAsync();
        await Assert.That(Mines()).IsEqualTo(1).Because("a user mine is submitted while the passes are busy");

        service.OnQuiet();
        await Assert.That(Mines()).IsEqualTo(1).Because("the quiet re-mine holds while a pass it reads has a demo in flight");

        service.QuietDelay = TimeSpan.FromMilliseconds(20);
        passes.Busy = false;
        passes.Raise();
        for (int i = 0; i < 200 && Mines() < 2; i++)
        {
            await Task.Delay(25);
        }

        await Assert.That(Mines()).IsEqualTo(2).Because("the passes settling re-arms the quiet timer and it fires");

        passes.Raise();
        await Task.Delay(200);
        await Assert.That(Mines()).IsEqualTo(2).Because("settling with nothing held back mines nothing");
    }

    private static ExtensionJobs Jobs(DemoProcessingQueue queue) =>
        new(StratBookPack.PackId, () => queue, () => JobKindRegistry.Build([new StratBookPack()]));

    // The drain compaction item may still hold the slot for a moment after the mine completes.
    private static async Task Idle(DemoProcessingQueue queue)
    {
        for (int i = 0; i < 200 && queue.ActiveWorkerCount > 0; i++)
        {
            await Task.Delay(10);
        }
    }

    private static DemoProcessingQueue RealQueue(HeavyJobGate gate) =>
        new(gate, a => a(), _ => throw new NotSupportedException(), _ => throw new NotSupportedException(),
            () => Task.CompletedTask);

    [Test]
    public async Task AMine_IsAQueueItem_ThatWaitsForTheSlotInUse()
    {
        using Library library = Library.Create();
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = RealQueue(gate);
        using StratMiningService service = new(library.Cache.Library(), library.Cache.RoundFacts(), library.Positions, StratMiningServiceTests._sources.FingerprintFor, null, null,
            library.Strats, library.Tags, null, null, jobs: Jobs(queue)) { QuietDelay = Timeout.InfiniteTimeSpan };

        Task mine;
        using (await gate.AcquireBackgroundAsync())
        {
            mine = service.MineAsync();
            await Task.Delay(300);
            await Assert.That(mine.IsCompleted).IsFalse().Because("a parse holds the only slot");
            await Assert.That(service.IsMining).IsTrue();
            DemoQueueItemSnapshot item = queue.Snapshot().Single();
            await Assert.That(item.ExtensionKind).IsEqualTo(StratBookJobKinds.Mining);
            await Assert.That(item.Priority).IsEqualTo(DemoJobPriority.UserRequested);
            await Assert.That(item.State).IsEqualTo(DemoQueueItemState.Queued);
        }

        await mine.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(service.MinedUtc).IsNotNull();
        await Assert.That(service.IsMining).IsFalse();
        await Assert.That(queue.Snapshot().Single(s => s.ExtensionKind == StratBookJobKinds.Mining).State)
            .IsEqualTo(DemoQueueItemState.Completed);
        await Idle(queue);
        await Assert.That(gate.InFlight).IsEqualTo(0).Because("the slot is released after the mine");
    }

    [Test]
    public async Task APreview_IsItsOwnQueueItem_AndLeavesMiningAlone()
    {
        using Library library = Library.Create();
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = RealQueue(gate);
        using StratMiningService service = new(library.Cache.Library(), library.Cache.RoundFacts(), library.Positions, StratMiningServiceTests._sources.FingerprintFor, null, null,
            library.Strats, library.Tags, null, null, jobs: Jobs(queue)) { QuietDelay = Timeout.InfiniteTimeSpan };
        await service.MineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        MinedPattern execute = service.Patterns.Select(p => p.Pattern).Single(p => p.Kind == PatternKind.Execute);

        Task<StratDocument?> preview;
        using (await gate.AcquireBackgroundAsync())
        {
            preview = service.PreviewAsync(execute, StratOwner.Me(), DateTime.UtcNow);
            await Task.Delay(300);
            await Assert.That(preview.IsCompleted).IsFalse().Because("a parse holds the only slot");
            DemoQueueItemSnapshot item = queue.Snapshot().Single(s => s.ExtensionKind == StratBookJobKinds.Preview);
            await Assert.That(item.Priority).IsEqualTo(DemoJobPriority.UserRequested);
            await Assert.That(service.IsMining).IsFalse().Because("a preview is not a mine");
        }

        StratDocument? doc = await preview.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.Multiple())
        {
            await Assert.That(doc?.Type).IsEqualTo("execute");
            await Assert.That(library.Strats.Index).IsEmpty();
            await Assert.That(service.IsMining).IsFalse();
        }

        await Idle(queue);
    }

    [Test]
    public async Task ADemoOpen_GetsTheGateBetweenBatches_BeforeTheMineFinishes()
    {
        using Library library = Library.Create();
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = RealQueue(gate);
        TaskCompletionSource inBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int steps = 0;
        using StratMiningService service = new(library.Cache.Library(), library.Cache.RoundFacts(), library.Positions, StratMiningServiceTests._sources.FingerprintFor, null, null,
            library.Strats, library.Tags, null, null, jobs: Jobs(queue), run: a => Task.Run(async () =>
            {
                a();
                // Step 1 is Begin, step 2 the first batch, run while the mine holds the slot.
                if (Interlocked.Increment(ref steps) == 2)
                {
                    inBatch.SetResult();
                    await proceed.Task;
                }
            })) { QuietDelay = Timeout.InfiniteTimeSpan, BatchSize = 1 };

        foreach (bool user in (bool[]) [false, true])
        {
            steps = 0;
            inBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task mine = service.MineAsync(user);
            await inBatch.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<IDisposable> open = gate.AcquireInteractiveAsync();
            await Task.Delay(150);
            await Assert.That(open.IsCompleted).IsFalse().Because("the batch in flight holds the slot");
            proceed.SetResult();

            using (await open.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                await Task.Delay(300);
                await Assert.That(mine.IsCompleted).IsFalse().Because($"the open took the gate between batches (user={user})");
            }

            await mine.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(service.Signatures.LastBuild.Reused + service.Signatures.LastBuild.Built).IsEqualTo(4);
            await Idle(queue);
            await Assert.That(gate.InFlight).IsEqualTo(0);
        }
    }

    private sealed class FakePasses : IExtensionPasses
    {
        public bool Busy { get; set; }

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();

        public void Request(string demoPath)
        {
        }

        public void RecheckAll()
        {
        }

        public bool IsBusy(string passId) => Busy;
    }
}
