#region

using System.Numerics;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     <see cref="IPackResident" /> on each resident, outside any container: Release empties and
///     unsubscribes, a source change after it changes nothing, Attach plus the load bring the state back, and
///     both calls are idempotent.
/// </summary>
public class PackResidentTests
{
    [Test]
    public async Task SituationIndex_ReleaseEmptiesAndUnsubscribes_LoadBringsItBack()
    {
        DemoCacheStore cache = new(null);
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundIndexEvaluator evaluator = new(cache, sidecars, sources, () => true, walk: _ => []);
        using SituationIndex index = new(cache, sidecars, sources, evaluator: evaluator);
        Indexed(cache, sidecars, "/d/a.dem", Document("de_nuke", sources.FingerprintFor("de_nuke"),
            (1, 1000, 1200, [new RoundIndexRun(0, 1, "BombsiteA:5", "Ramp:5")])));
        Indexed(cache, sidecars, "/d/b.dem", Document("de_nuke", sources.FingerprintFor("de_nuke"),
            (1, 1000, 1200, [new RoundIndexRun(0, 1, "Heaven:5", "Ramp:5")])));
        int changed = 0;
        index.Changed += () => changed++;

        index.Load();
        await Assert.That(index.IndexedDemoCount).IsEqualTo(2);
        await Assert.That(index.IsReady).IsTrue();

        index.Release();
        index.Release();
        using (Assert.Multiple())
        {
            await Assert.That(index.IndexedDemoCount).IsEqualTo(0);
            await Assert.That(index.IsReady).IsFalse();
            await Assert.That(index.Query(new SituationQuery("de_nuke", [], []))).IsEmpty();
            await Assert.That(changed).IsEqualTo(2).Because("one Changed for the load, one for the release, none for the second release");
        }

        // Unsubscribed: a removal the attached index would act on is not even looked at.
        cache.Remove("/d/a.dem");
        await Assert.That(changed).IsEqualTo(2);

        index.Load();
        using (Assert.Multiple())
        {
            await Assert.That(index.IsReady).IsTrue();
            await Assert.That(index.IndexedDemoCount).IsEqualTo(1).Because("the reload reads the cache as it is now");
        }

        // Subscribed again: the removal is seen this time.
        cache.Remove("/d/b.dem");
        await Assert.That(index.IndexedDemoCount).IsEqualTo(0);
    }

    [Test]
    public async Task GrenadeIndex_ReleaseFlushesThenEmpties_WhenLoadedIsPendingAgain_LoadBringsItBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvgrenaderelease_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DemoCacheStore cache = new(null);
            IndexOneGrenade(cache, "/d/a.dem", "de_dust2");
            using GrenadeIndex grenades = new(cache, lineups: new GrenadeLineupStore(root), scheduleSave: _ => Task.CompletedTask);
            grenades.Load();
            _ = grenades.Query(new GrenadeQuery("de_dust2")); // mints an anchor: the lineup store is dirty
            Task loaded = grenades.WhenLoaded;
            await Assert.That(loaded.IsCompleted).IsTrue();
            string lineups = Path.Combine(root, GrenadeLineupStore.FileName);
            await Assert.That(File.Exists(lineups)).IsFalse();

            grenades.Release();
            grenades.Release();
            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(lineups)).IsTrue().Because("the pending lineup save was written before the drop");
                await Assert.That(grenades.IsReady).IsFalse();
                await Assert.That(grenades.DemoCount).IsEqualTo(0);
                await Assert.That(grenades.Maps()).IsEmpty();
                await Assert.That(grenades.WhenLoaded.IsCompleted).IsFalse().Because("the next load completes a new WhenLoaded");
            }

            grenades.Load();
            using (Assert.Multiple())
            {
                await Assert.That(grenades.IsReady).IsTrue();
                await Assert.That(grenades.DemoCount).IsEqualTo(1);
                await Assert.That(grenades.WhenLoaded.IsCompleted).IsTrue();
                await Assert.That(grenades.Query(new GrenadeQuery("de_dust2")).Count).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TeamIdentity_LoadAtStartFalse_StaysUnreadUntilAttach_ReleaseDropsTheIndex_AttachReadsAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvteamsrelease_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        try
        {
            DemoCacheStore cache = new(null);
            int scheduled = 0;
            using TeamIdentityService teams = new(root, cache, run: work =>
            {
                work();
                return Task.CompletedTask;
            }, scheduleLoad: load =>
            {
                scheduled++;
                load();
                return Task.CompletedTask;
            }, loadAtStart: false);

            await Assert.That(teams.IsLoaded).IsFalse().Because("the pack is off: nothing read");
            await Assert.That(scheduled).IsEqualTo(0);

            teams.Attach();
            teams.Attach();
            await Assert.That(scheduled).IsEqualTo(1).Because("Attach schedules the read once");
            await Assert.That(teams.IsLoaded).IsTrue();
            await teams.StartAsync();
            teams.SetSquad(["76561198000000001", "76561198000000002"], "us");
            await Assert.That(teams.AllTeams.Count).IsEqualTo(1);
            await Assert.That(File.Exists(Path.Combine(root, "teams.json"))).IsTrue();

            teams.Release();
            teams.Release();
            using (Assert.Multiple())
            {
                await Assert.That(teams.IsLoaded).IsFalse();
                await Assert.That(teams.AllTeams).IsEmpty().Because("the file stays on disk; the memory is gone");
            }

            teams.Attach();
            using (Assert.Multiple())
            {
                await Assert.That(scheduled).IsEqualTo(2);
                await Assert.That(teams.IsLoaded).IsTrue();
                await Assert.That(teams.AllTeams.Count).IsEqualTo(1).Because("the re-attach read teams.json back");
                await Assert.That(teams.AllTeams[0].Name).IsEqualTo("us");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TeamIdentity_AReadReachingADetachedService_ReadsNothing_WritesNothing_AndAttachReadsTheFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvteamsdetachedread_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        try
        {
            DemoCacheStore cache = new(null);
            Func<Action, Task> inline = work =>
            {
                work();
                return Task.CompletedTask;
            };

            // An attached service writes the user's file, then goes away.
            using (TeamIdentityService writer = new(root, cache, run: inline, scheduleLoad: inline))
            {
                writer.SetSquad(["76561198000000001", "76561198000000002"], "us");
            }

            string teamsFile = Path.Combine(root, "teams.json");
            string indexFile = Path.Combine(root, "cache", "team-index.json");
            string onDisk = await File.ReadAllTextAsync(teamsFile);
            string indexOnDisk = await File.ReadAllTextAsync(indexFile);

            // Detached from the start, the way the shell builds it before the wizard has asked. A read that
            // reaches it anyway (a mutator's own Ensure stands in for a stale queued "Load: teams" here)
            // reads nothing, and what the mutator then computes over nothing reaches no file.
            using TeamIdentityService teams = new(root, cache, run: inline, scheduleLoad: inline, loadAtStart: false);
            teams.SetSquad(["76561198000000009", "76561198000000010"], "not us");
            using (Assert.Multiple())
            {
                await Assert.That(teams.IsLoaded).IsFalse().Because("the skipped read leaves a fresh load behind");
                await Assert.That(await File.ReadAllTextAsync(teamsFile)).IsEqualTo(onDisk).Because("detached, nothing is written");
                await Assert.That(await File.ReadAllTextAsync(indexFile)).IsEqualTo(indexOnDisk).Because("nor is the derived index");
            }

            teams.Attach();
            using (Assert.Multiple())
            {
                await Assert.That(teams.IsLoaded).IsTrue();
                await Assert.That(teams.AllTeams.Select(t => t.Name)).IsEquivalentTo(["us"])
                    .Because("Attach read the file as the attached service left it; the detached mutation is gone");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task WatchedSituations_ReleaseDropsTheNewHits_AndStopsFollowingTheIndex_AttachReevaluates()
    {
        DemoCacheStore cache = new(null);
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        using SituationIndex index = new(cache, sidecars, sources);
        Indexed(cache, sidecars, "/d/a.dem", Document("de_nuke", sources.FingerprintFor("de_nuke"),
            (1, 1000, 1200, [new RoundIndexRun(0, 1, "BombsiteA:5", "Ramp:5")])), computedAt: 100);
        index.Load();
        using WatchedSituationsService watched = new(null, index, cache, now: () => 50);
        watched.Watch("A hold", "de_nuke", [.. Enumerable.Range(0, 5).Select(i => new QueryToken(QuerySide.Ct, i, 600 + i, -400, -416, "BombsiteA"))],
            SituationTolerance.Exact, SearchFilterValues.None);
        index.Load(); // the index's Changed is what makes a watch count
        await Assert.That(watched.NewCount).IsEqualTo(1);

        watched.Release();
        watched.Release();
        await Assert.That(watched.NewCount).IsEqualTo(0);
        await Assert.That(watched.Watches.Count).IsEqualTo(1).Because("the saved query is the user's; only the hits go");

        // Not following the index: a reload that would recount changes nothing until Attach.
        index.Release();
        index.Load();
        await Assert.That(watched.NewCount).IsEqualTo(0);

        watched.Attach();
        await Assert.That(watched.NewCount).IsEqualTo(1);
    }

    [Test]
    public async Task LineupClips_ReleasedPlansNothing_AttachPlansAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvlineuprelease_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DemoCacheStore cache = new(null);
            IndexOneGrenade(cache, "/d/a.dem", "de_dust2");
            IndexOneGrenade(cache, "/d/b.dem", "de_dust2");
            using GrenadeIndex grenades = new(cache, lineups: new GrenadeLineupStore(null), scheduleSave: _ => Task.CompletedTask);
            grenades.Load();
            using LineupClipService clips = new(() => [.. grenades.Maps().SelectMany(m => grenades.Query(new GrenadeQuery(m)))], root,
                () => true, new NoRenderer(), fileExists: _ => false, planDebounce: TimeSpan.Zero);

            await Assert.That(clips.Plan()).IsGreaterThan(0);
            clips.Release();
            using (Assert.Multiple())
            {
                await Assert.That(clips.Pending).IsEmpty();
                await Assert.That(clips.Plan()).IsEqualTo(0).Because("released: a plan is a no-op until Attach");
                await Assert.That(clips.PlanSoon().IsCompleted).IsTrue();
            }

            clips.Attach();
            await Assert.That(clips.Plan()).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // One grenade row, no zones, so EnsureAssignedLocked mints a fresh anchor on the first query.
    private static void IndexOneGrenade(DemoCacheStore cache, string path, string map)
    {
        GrenadeRow row = new()
        {
            Id = "a",
            Kind = GrenadeKind.Smoke,
            ThrowerTeam = 3,
            ReleaseTick = 1000,
            ReleasePosition = WorldPoint.From(new Vector3(512, 288, -160)),
            ReleaseEyePitch = -10.5f,
            ReleaseEyeYaw = 45.25f,
            DetonationPosition = WorldPoint.From(new Vector3(-1400, -1400, -170)),
            EndKind = GrenadeEndKind.Detonated
        };
        DemoCacheRecord record = ParsedRecord(path, map);
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = null, StableKey = DemoCacheStore.StableKey(path) },
            Grenades = [row]
        };
        cache.WriteSibling(path, GrenadeSidecar.Suffix, GrenadeSidecar.Serialize(document));
        record.StampGrenades(document.Grenades.Count);
        cache.Upsert(record);
    }

    private sealed class NoRenderer : ILineupClipRenderer
    {
        public Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, IReadOnlyList<LineupClipJob> jobs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LineupClipJob>>([]);
    }
}
