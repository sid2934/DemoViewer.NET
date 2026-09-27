#region

using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lineup Clip Render (plan.md §3, Phase 4): the job planning, which is the whole of what a unit test can
///     reach (the real render parses a demo and runs in the integration phase). Which lineups get a clip, the
///     tick range around the throw, the pair's paths, the GIF request the existing export session receives
///     (format, rate, size, the frames the range resolves to, the camera on the thrower), and the service that
///     queues the clips in the Review Queue and produces the setpos + GIF pair with no one pressing anything.
/// </summary>
public class LineupClipTests
{
    private const string Mirage = "de_mirage";
    private const string Directory = "/clips";
    private const string Setpos = "setpos -1200.00 300.00 64.00; setang -10.50 45.25 0.00";

    private static GrenadeRow Row(string id, int release = 1000, int? detonation = 1100, bool console = true,
        string? steamId = "76561198000000001") => new()
    {
        Id = id,
        Kind = GrenadeKind.Smoke,
        ThrowerTeam = 3,
        ThrowerSteamId64 = steamId,
        ReleaseTick = release,
        ReleasePosition = console ? new WorldPoint(-1200, 300, 64) : null,
        ReleaseEyePitch = console ? -10.5f : null,
        ReleaseEyeYaw = console ? 45.25f : null,
        DetonationTick = detonation,
        DetonationPosition = new WorldPoint(-1500, 800, 0),
        EndKind = GrenadeEndKind.Detonated
    };

    private static IndexedGrenade Throw(string demo, GrenadeRow row) =>
        new(new DemoRef(demo, DemoCacheStore.StableKey(demo), "sha-" + Path.GetFileNameWithoutExtension(demo)),
            Mirage, row, row.ReleasePosition ?? default, new WorldPoint(-1500, 800, 0), "CTSpawn", "zones:1");

    // The id is the position's, stable across calls, as GrenadeIndex.LineupId is; here keyed by the first row.
    private static GrenadeLineup Lineup(params IndexedGrenade[] throws) =>
        new(throws[0].Origin, false, throws, IdOf(throws[0].Row.Id));

    private static Guid IdOf(string seed) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));

    private static GrenadeCluster Cluster(params GrenadeLineup[] lineups) =>
        new(GrenadeKind.Smoke, (0, 0, 0), new WorldPoint(-1500, 800, 0), "CTSpawn", lineups);

    // Two positions thrown twice each, in two demos, and one thrown once.
    private static GrenadeCluster TwoLineups() => Cluster(
        Lineup(Throw("/d/a.dem", Row("a1")), Throw("/d/b.dem", Row("b1", 3000, 3100))),
        Lineup(Throw("/d/b.dem", Row("b2", 5000, 5080)), Throw("/d/a.dem", Row("a2", 7000, 7100))),
        Lineup(Throw("/d/a.dem", Row("a3", 9000, 9050))));

    private static DemoFrame[] Frames(int from, int to)
    {
        DemoFrame[] frames = new DemoFrame[to - from + 1];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = new DemoFrame
            {
                CommandKind = EDemoCommands.DemPacket,
                FrameNumber = i,
                ServerTick = from + i,
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false
            };
        }

        return frames;
    }

    [Test]
    public async Task Plan_ARepeatedPosition_IsOneJobOverItsRepresentative()
    {
        GrenadeLineup lineup = Lineup(Throw("/d/a.dem", Row("a1")), Throw("/d/b.dem", Row("b1", 3000, 3100)));

        LineupClipJob? job = LineupClipPlanner.Plan(lineup, "Smoke into CTSpawn", Directory);

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Key).IsEqualTo("sha-a/a1");
        await Assert.That(job.DemoPath).IsEqualTo("/d/a.dem");
        await Assert.That(job.Map).IsEqualTo(Mirage);
        await Assert.That(job.ConsoleText).IsEqualTo(Setpos);
        await Assert.That(job.ThrowerSteamId).IsEqualTo(76561198000000001UL);
        await Assert.That(job.FromTick).IsEqualTo(1000 - 64);
        await Assert.That(job.ToTick).IsEqualTo(1100 + 96);
        await Assert.That(job.TickRate).IsEqualTo(64);

        // The pair: one stem, the GIF and the sidecar beside it, stable for the same throw.
        await Assert.That(Path.GetDirectoryName(job.GifPath)).IsEqualTo(Path.GetDirectoryName(Path.Combine(Directory, "x")));
        await Assert.That(Path.GetFileName(job.GifPath)).StartsWith("de_mirage-smoke-");
        await Assert.That(job.GifPath).EndsWith(".gif");
        await Assert.That(job.SetposPath).IsEqualTo(job.GifPath[..^".gif".Length] + LineupClipPlanner.SetposExtension);
        await Assert.That(LineupClipPlanner.Plan(lineup, "Smoke into CTSpawn", Directory)!.GifPath).IsEqualTo(job.GifPath);
    }

    [Test]
    public async Task Plan_SkipsAOneOffThrow_AndAThrowWithNoConsoleLine()
    {
        await Assert.That(LineupClipPlanner.Plan(Lineup(Throw("/d/a.dem", Row("a1"))), "t", Directory)).IsNull();

        GrenadeLineup unread = Lineup(Throw("/d/a.dem", Row("a1", console: false)), Throw("/d/b.dem", Row("b1")));
        await Assert.That(LineupClipPlanner.Plan(unread, "t", Directory)).IsNull();

        // No thrower id: still a clip, framed on the whole map instead of following anyone.
        GrenadeLineup anonymous = Lineup(Throw("/d/a.dem", Row("a1", steamId: null)), Throw("/d/b.dem", Row("b1")));
        await Assert.That(LineupClipPlanner.Plan(anonymous, "t", Directory)!.ThrowerSteamId).IsNull();
    }

    [Test]
    public async Task Range_FallsBackToTheEnd_ThenTheAirTime_AndIsCapped()
    {
        await Assert.That(LineupClipPlanner.Range(Row("x", 1000, 1100), 64)).IsEqualTo((936, 1196));

        GrenadeRow ended = Row("x", 1000, null);
        ended.EndTick = 1200;
        await Assert.That(LineupClipPlanner.Range(ended, 64)).IsEqualTo((936, 1296));

        GrenadeRow airOnly = Row("x", 1000, null);
        airOnly.AirTimeTicks = 150;
        await Assert.That(LineupClipPlanner.Range(airOnly, 64)).IsEqualTo((936, 1246));

        GrenadeRow endless = Row("x", 1000, null);
        endless.AirTimeTicks = 100_000;
        await Assert.That(LineupClipPlanner.Range(endless, 64)).IsEqualTo((936, 936 + 768));

        // Near the demo's start the lead is clipped at tick 0, never negative.
        await Assert.That(LineupClipPlanner.Range(Row("x", 10, 40), 64).FromTick).IsEqualTo(0);
    }

    [Test]
    public async Task PlanAll_SkipsAFinishedPair_ButNotHalfOfOne()
    {
        GrenadeCluster cluster = TwoLineups();
        IReadOnlyList<LineupClipJob> all = LineupClipPlanner.PlanAll([cluster], Directory, _ => false);
        await Assert.That(all.Select(j => j.Key)).IsEquivalentTo(["sha-a/a1", "sha-b/b2"]);
        await Assert.That(all.All(j => j.Title == "Smoke into CTSpawn")).IsTrue();

        LineupClipJob first = all[0];
        HashSet<string> done = [first.GifPath, first.SetposPath];
        await Assert.That(LineupClipPlanner.PlanAll([cluster], Directory, done.Contains).Select(j => j.Key))
            .IsEquivalentTo(["sha-b/b2"]);

        HashSet<string> gifOnly = [first.GifPath];
        await Assert.That(LineupClipPlanner.PlanAll([cluster], Directory, gifOnly.Contains).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ToReviewEntry_IsALineupClipCarryingTheConsoleLine()
    {
        LineupClipJob job = LineupClipPlanner.PlanAll([TwoLineups()], Directory, _ => false)[0];

        ReviewEntry entry = LineupClipPlanner.ToReviewEntry(job);

        await Assert.That(entry.Kind).IsEqualTo(ReviewEntryKind.Clip);
        await Assert.That(entry.Source).IsEqualTo(ReviewSources.Lineup);
        await Assert.That(entry.DemoPath).IsEqualTo("/d/a.dem");
        await Assert.That(entry.Sha256).IsEqualTo("sha-a");
        await Assert.That(entry.FromTick).IsEqualTo(936);
        await Assert.That(entry.ToTick).IsEqualTo(1196);
        await Assert.That(entry.TickRate).IsEqualTo(64);
        await Assert.That(entry.Note).IsEqualTo("Smoke into CTSpawn. " + Setpos);
    }

    [Test]
    public async Task BuildRequest_IsAValidGif_OverTheResolvedFrames_FollowingTheThrower()
    {
        LineupClipJob job = LineupClipPlanner.PlanAll([TwoLineups()], Directory, _ => false)[0];
        DemoFrame[] frames = Frames(0, 2000);

        Scene2DExportRequest? request = LineupClipPlanner.BuildRequest(job, frames, 64);

        await Assert.That(request).IsNotNull();
        await Assert.That(request!.OutputPath).IsEqualTo(job.GifPath);
        await Assert.That(request.DemoPath).IsEqualTo(job.DemoPath);
        await Assert.That(request.DemoStartFrame).IsEqualTo(936);
        await Assert.That(request.DemoEndFrame).IsEqualTo(1196);
        await Assert.That(request.Core.FormatId).IsEqualTo(ExportFormats.Gif);
        await Assert.That(request.Core.Fps).IsEqualTo(LineupClipPlanner.Fps);
        await Assert.That(request.Core.Size.Width).IsEqualTo(LineupClipPlanner.Side);
        await Assert.That(request.Core.Size.Height).IsEqualTo(LineupClipPlanner.Side);

        // 260 ticks at 3.2 ticks per output frame: the source's own arithmetic, 82 frames.
        await Assert.That(request.Core.FrameCount).IsEqualTo(82);
        await Assert.That(request.Core.Camera).IsEqualTo(new CameraScript.FollowPlayer(76561198000000001UL));
        SceneExportSession.Validate(request.Core);
    }

    [Test]
    public async Task BuildRequest_ClampsToTheDemo_AndRefusesARangeOutsideIt()
    {
        LineupClipJob job = LineupClipPlanner.PlanAll([TwoLineups()], Directory, _ => false)[0];

        Scene2DExportRequest? clipped = LineupClipPlanner.BuildRequest(job, Frames(1000, 1150), 64);
        await Assert.That(clipped!.DemoStartFrame).IsEqualTo(0);
        await Assert.That(clipped.DemoEndFrame).IsEqualTo(150);

        await Assert.That(LineupClipPlanner.BuildRequest(job, Frames(5000, 6000), 64)).IsNull();
        await Assert.That(LineupClipPlanner.BuildRequest(job, [], 64)).IsNull();

        LineupClipJob anonymous = job with { ThrowerSteamId = null };
        await Assert.That(LineupClipPlanner.BuildRequest(anonymous, Frames(0, 2000), 64)!.Core.Camera)
            .IsTypeOf<CameraScript.Fixed>();
    }

    [Test]
    public async Task Service_QueuesTheClips_AndProducesThePairs_WithNoUserInput()
    {
        ReviewQueue queue = new(null);
        FakeRenderer renderer = new();
        Dictionary<string, string> written = [];
        using LineupClipService service = new(() => [TwoLineups()], queue, Directory, () => true, renderer,
            _ => false, (path, text) => written[path] = text);

        int planned = service.Plan();
        await service.WorkerTask;

        await Assert.That(planned).IsEqualTo(2);

        // In the Review Queue under one title card for the map, one clip per lineup.
        await Assert.That(queue.Entries.Count).IsEqualTo(3);
        await Assert.That(queue.Entries[0].Title).IsEqualTo("Lineup clips, de_mirage");
        await Assert.That(queue.Clips.All(c => c.Source == ReviewSources.Lineup)).IsTrue();

        // One render call per demo, each parsed once; every GIF has its setpos beside it.
        await Assert.That(renderer.Calls.Select(c => c.Demo)).IsEquivalentTo(["/d/a.dem", "/d/b.dem"]);
        foreach (LineupClipJob job in renderer.Calls.SelectMany(c => c.Jobs))
        {
            await Assert.That(written[job.SetposPath]).IsEqualTo(Setpos + Environment.NewLine);
        }

        await Assert.That(service.Pending.Count).IsEqualTo(0);

        // The same index again plans nothing twice.
        await Assert.That(service.Plan()).IsEqualTo(0);
    }

    [Test]
    public async Task Service_DoesNotRender_AClipTakenOutOfTheQueue_OrAFailedGif()
    {
        ReviewQueue queue = new(null);
        FakeRenderer renderer = new() { Hold = new TaskCompletionSource() };
        Dictionary<string, string> written = [];
        using LineupClipService service = new(() => [TwoLineups()], queue, Directory, () => true, renderer,
            _ => false, (path, text) => written[path] = text);

        service.Plan();

        // The first demo is rendering; the second demo's clip is taken out of the queue before its turn.
        ReviewEntry second = queue.Clips.Single(c => c.DemoPath == "/d/b.dem");
        queue.Remove([second.Id]);
        renderer.Fail = true;
        renderer.Hold.SetResult();
        await service.WorkerTask;

        await Assert.That(renderer.Calls.Select(c => c.Demo)).IsEquivalentTo(["/d/a.dem"]);

        // The first GIF failed: no sidecar, so no half pair on disk.
        await Assert.That(written.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Service_PlansNothing_WhenOff_OrWithNoDirectory()
    {
        ReviewQueue queue = new(null);
        FakeRenderer renderer = new();
        using LineupClipService off = new(() => [TwoLineups()], queue, Directory, () => false, renderer, _ => false);
        using LineupClipService browser = new(() => [TwoLineups()], queue, null, () => true, renderer, _ => false);

        await Assert.That(off.Plan()).IsEqualTo(0);
        await Assert.That(browser.Plan()).IsEqualTo(0);
        await Assert.That(queue.Entries.Count).IsEqualTo(0);
        await Assert.That(renderer.Calls.Count).IsEqualTo(0);
    }

    // ── Keyed by lineup, bounded on disk and in the queue ─────────────────────

    private static string TempClips() =>
        System.IO.Directory.CreateTempSubdirectory("dv-lineup-clips-").FullName;

    private static void WritePair(string directory, string stem, int gifBytes, DateTime written)
    {
        string gif = Path.Combine(directory, stem + LineupClipPlanner.GifExtension);
        File.WriteAllBytes(gif, new byte[gifBytes]);
        File.WriteAllText(LineupClipPlanner.SetposPathFor(gif), Setpos);
        File.SetLastWriteTimeUtc(gif, written);
        File.SetLastWriteTimeUtc(LineupClipPlanner.SetposPathFor(gif), written);
    }

    private static bool HasPair(string directory, string stem) =>
        File.Exists(Path.Combine(directory, stem + LineupClipPlanner.GifExtension))
        && File.Exists(Path.Combine(directory, stem + LineupClipPlanner.SetposExtension));

    [Test]
    public async Task TheStem_IsTheLineups_SoANewRepresentativeKeepsThePair()
    {
        GrenadeLineup before = new(default, false,
            [Throw("/d/b.dem", Row("b1", 3000, 3100)), Throw("/d/c.dem", Row("c1", 5000, 5100))], IdOf("spot"));

        // An older demo is indexed: it sorts first and becomes the representative, the position is the same.
        GrenadeLineup after = before with { Throws = [Throw("/d/a.dem", Row("a1")), .. before.Throws] };

        LineupClipJob first = LineupClipPlanner.Plan(before, "t", Directory)!;
        LineupClipJob second = LineupClipPlanner.Plan(after, "t", Directory)!;
        using (Assert.Multiple())
        {
            await Assert.That(second.Key).IsNotEqualTo(first.Key);
            await Assert.That(second.GifPath).IsEqualTo(first.GifPath);
            await Assert.That(second.SetposPath).IsEqualTo(first.SetposPath);
            await Assert.That(first.Stem).IsEqualTo(LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, IdOf("spot")));
        }

        string clips = TempClips();
        try
        {
            ReviewQueue queue = new(null);
            FileRenderer renderer = new();
            GrenadeLineup current = before;
            using LineupClipService service = new(() => [Cluster(current)], queue, clips, () => true, renderer);

            await Assert.That(service.Plan()).IsEqualTo(1);
            await service.WorkerTask;
            current = after;

            using (Assert.Multiple())
            {
                await Assert.That(service.Plan()).IsEqualTo(0).Because("the pair on disk is the lineup's, whoever represents it");
                await Assert.That(renderer.Calls.Count).IsEqualTo(1);
                await Assert.That(queue.ClipCount).IsEqualTo(1);
            }
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task APairUnderTheOldThrowKeyedName_IsRenamed_NotRenderedAgain()
    {
        GrenadeLineup lineup = Lineup(Throw("/d/a.dem", Row("a1")), Throw("/d/b.dem", Row("b1", 3000, 3100)));
        string legacy = LineupClipPlanner.LegacyFileStem(lineup.Throws[1]);
        string clips = TempClips();
        try
        {
            WritePair(clips, legacy, 100, DateTime.UtcNow);
            FileRenderer renderer = new();
            using LineupClipService service = new(() => [Cluster(lineup)], new ReviewQueue(null), clips, () => true, renderer);

            int planned = service.Plan();
            LineupClipJob job = LineupClipPlanner.Plan(lineup, "t", clips)!;
            using (Assert.Multiple())
            {
                await Assert.That(planned).IsEqualTo(0);
                await Assert.That(renderer.Calls).IsEmpty();
                await Assert.That(HasPair(clips, job.Stem)).IsTrue();
                await Assert.That(HasPair(clips, legacy)).IsFalse();
            }
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task TheSweep_DeletesPairsNoLineupPlans_OnlyOnceTheIndexIsComplete()
    {
        GrenadeCluster cluster = TwoLineups();
        string clips = TempClips();
        try
        {
            IReadOnlyList<LineupClipJob> jobs = LineupClipPlanner.PlanEvery([cluster], clips);
            foreach (LineupClipJob job in jobs)
            {
                WritePair(clips, job.Stem, 100, DateTime.UtcNow);
            }

            WritePair(clips, "de_mirage-smoke-0123456789ab", 100, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(clips, "notes.txt"), "not a clip");

            bool complete = false;
            using (LineupClipService loading = new(() => [cluster], new ReviewQueue(null), clips, () => true, new FileRenderer(),
                       complete: () => complete, orphanGrace: TimeSpan.Zero))
            {
                loading.Plan();
                await loading.SweepTask;
                await Assert.That(HasPair(clips, "de_mirage-smoke-0123456789ab")).IsTrue().Because("half an index is not the library");

                complete = true;
                loading.Plan();
                await loading.SweepTask;
            }

            using (Assert.Multiple())
            {
                await Assert.That(HasPair(clips, "de_mirage-smoke-0123456789ab")).IsFalse();
                await Assert.That(jobs.All(j => HasPair(clips, j.Stem))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(clips, "notes.txt"))).IsTrue();
            }

            // With a grace period, the first sighting only starts the clock.
            WritePair(clips, "de_mirage-smoke-ba9876543210", 100, DateTime.UtcNow);
            using LineupClipService patient = new(() => [cluster], new ReviewQueue(null), clips, () => true, new FileRenderer());
            patient.Plan();
            await patient.SweepTask;
            await Assert.That(HasPair(clips, "de_mirage-smoke-ba9876543210")).IsTrue();
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task TheQueue_HoldsOneSectionPerMap_AndOneEntryPerLineup()
    {
        GrenadeCluster cluster = TwoLineups();
        LineupClipJob first = LineupClipPlanner.PlanEvery([cluster], Directory)[0];
        ReviewQueue queue = new(null);

        // What earlier builds left: a card per plan, entries with no lineup id, one of them for a throw that
        // no longer represents anything, and a clip the user filed under the second card by hand.
        ReviewEntry legacy = LineupClipPlanner.ToReviewEntry(first) with { LineupId = null, Question = "which angle?" };
        queue.Add([legacy], LineupClipPlanner.SectionTitle(Mirage));
        queue.Add([ReviewEntry.Clip("/d/z.dem", 1, 2, "stale", ReviewSources.Lineup)], LineupClipPlanner.SectionTitle(Mirage));
        ReviewEntry manual = ReviewEntry.Clip("/d/m.dem", 5, 6, "mine", ReviewSources.Manual);
        queue.Add([manual]);

        using LineupClipService service = new(() => [cluster], queue, Directory, () => true, new FakeRenderer(), _ => false,
            (_, _) => { });
        await Assert.That(service.Plan()).IsEqualTo(2);
        await service.WorkerTask;

        List<ReviewEntry> lineups = [.. queue.Clips.Where(c => c.Source == ReviewSources.Lineup)];
        using (Assert.Multiple())
        {
            await Assert.That(queue.Entries.Count(e => e.Kind == ReviewEntryKind.Section)).IsEqualTo(1);
            await Assert.That(lineups.Count).IsEqualTo(2);
            await Assert.That(lineups.Select(c => c.LineupId)).IsEquivalentTo(cluster.Lineups.Take(2).Select(l => (Guid?)l.Id));
            await Assert.That(queue.Find(legacy.Id)!.Question).IsEqualTo("which angle?").Because("the old entry was kept, not re-added");
            await Assert.That(queue.Find(manual.Id)).IsNotNull();
        }

        // The first lineup's representative changes while its pair is still missing: its entry is replaced
        // where it stands, not appended.
        GrenadeCluster rekeyed = cluster with
        {
            Lineups = [cluster.Lineups[0] with { Throws = [Throw("/d/0.dem", Row("z1", 2000, 2100)), .. cluster.Lineups[0].Throws] }, .. cluster.Lineups.Skip(1)]
        };
        using LineupClipService again = new(() => [rekeyed], queue, Directory, () => true, new FakeRenderer(), _ => false,
            (_, _) => { });
        await Assert.That(again.Plan()).IsEqualTo(2);
        await again.WorkerTask;

        ReviewEntry replaced = queue.Find(legacy.Id)!;
        using (Assert.Multiple())
        {
            await Assert.That(queue.Clips.Count(c => c.Source == ReviewSources.Lineup)).IsEqualTo(2);
            await Assert.That(replaced.DemoPath).IsEqualTo("/d/0.dem");
            await Assert.That(replaced.Question).IsEqualTo("which angle?");
            await Assert.That(queue.Entries.Count(e => e.Kind == ReviewEntryKind.Section)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheCap_EvictsTheLeastRecentlyUsedPairs_AndAnEvictedClipComesBackOnlyOnRequest()
    {
        GrenadeCluster cluster = TwoLineups();
        string clips = TempClips();
        try
        {
            IReadOnlyList<LineupClipJob> jobs = LineupClipPlanner.PlanEvery([cluster], clips);
            DateTime now = DateTime.UtcNow;
            WritePair(clips, "de_mirage-smoke-000000000001", 1000, now.AddHours(-3));
            WritePair(clips, "de_mirage-smoke-000000000002", 1000, now.AddHours(-2));

            // Two old pairs and two new GIFs of 1000 bytes, four setpos lines: a 3,000-byte cap takes one old pair.
            long setpos = new FileInfo(Path.Combine(clips, "de_mirage-smoke-000000000001" + LineupClipPlanner.SetposExtension)).Length;
            long cap = 3000 + (4 * setpos);
            FileRenderer renderer = new() { Bytes = 1000 };
            using (LineupClipService service = new(() => [cluster], new ReviewQueue(null), clips, () => true, renderer,
                       complete: () => false, maxBytes: () => cap))
            {
                await Assert.That(service.Plan()).IsEqualTo(2);
                await service.WorkerTask;

                using (Assert.Multiple())
                {
                    await Assert.That(HasPair(clips, "de_mirage-smoke-000000000001")).IsFalse().Because("the oldest write goes first");
                    await Assert.That(HasPair(clips, "de_mirage-smoke-000000000002")).IsTrue();
                    await Assert.That(jobs.All(j => HasPair(clips, j.Stem))).IsTrue();
                    await Assert.That(service.Evicted).IsEquivalentTo(["de_mirage-smoke-000000000001"]);
                }
            }

            // An evicted current lineup stays evicted across sessions until it is asked for.
            File.Delete(jobs[0].GifPath);
            File.Delete(jobs[0].SetposPath);
            File.WriteAllText(Path.Combine(clips, LineupClipService.EvictedFileName), jobs[0].Stem);
            FileRenderer later = new();
            using LineupClipService next = new(() => [cluster], new ReviewQueue(null), clips, () => true, later);
            await Assert.That(next.Plan()).IsEqualTo(0);

            await Assert.That(next.Request(jobs[0].LineupId)).IsEqualTo(1);
            await next.WorkerTask;
            using (Assert.Multiple())
            {
                await Assert.That(later.Calls.Single().Jobs.Single().LineupId).IsEqualTo(jobs[0].LineupId);
                await Assert.That(HasPair(clips, jobs[0].Stem)).IsTrue();
                await Assert.That(next.Evicted).IsEmpty();
            }
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task ALineupGoneForOnePlan_KeepsItsEntryAndItsEviction_UntilTheGraceRunsOut()
    {
        GrenadeCluster cluster = TwoLineups();
        string clips = TempClips();
        try
        {
            IReadOnlyList<LineupClipJob> jobs = LineupClipPlanner.PlanEvery([cluster], clips);
            WritePair(clips, jobs[0].Stem, 100, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(clips, LineupClipService.EvictedFileName), jobs[1].Stem);
            ReviewQueue queue = new(null);
            queue.Add([LineupClipPlanner.ToReviewEntry(jobs[0]) with { Question = "which angle?" }], LineupClipPlanner.SectionTitle(Mirage));

            // A re-index drops the demo's rows for a moment, then puts them back.
            bool present = true;
            using (LineupClipService service = new(() => present ? [cluster] : [], queue, clips, () => true, new FileRenderer()))
            {
                present = false;
                service.Plan();
                present = true;
                await Assert.That(service.Plan()).IsEqualTo(0);
                using (Assert.Multiple())
                {
                    await Assert.That(queue.Clips.Single().Question).IsEqualTo("which angle?");
                    await Assert.That(service.Evicted).IsEquivalentTo([jobs[1].Stem]);
                }
            }

            using LineupClipService impatient = new(() => present ? [cluster] : [], queue, clips, () => true, new FileRenderer(),
                orphanGrace: TimeSpan.Zero);
            present = false;
            impatient.Plan();
            using (Assert.Multiple())
            {
                await Assert.That(queue.ClipCount).IsEqualTo(0);
                await Assert.That(queue.Entries).IsEmpty().Because("the emptied lineup card goes with its last clip");
                await Assert.That(impatient.Evicted).IsEmpty();
            }
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task Merge_FoldsSameTitledSections_AndReplacesInPlace()
    {
        ReviewQueue queue = new(null);
        ReviewEntry a = ReviewEntry.Clip("/d/a.dem", 1, 2, "a", ReviewSources.Lineup) with { LineupId = IdOf("a") };
        ReviewEntry b = ReviewEntry.Clip("/d/b.dem", 1, 2, "b", ReviewSources.Lineup) with { LineupId = IdOf("b") };
        queue.Add([a], "Lineup clips, de_mirage");
        queue.Add([ReviewEntry.Clip("/d/x.dem", 1, 2, "x", ReviewSources.Manual)], "Other");
        queue.Add([b], "Lineup clips, de_mirage");

        ReviewEntry a2 = ReviewEntry.Clip("/d/a.dem", 5, 9, "a2", ReviewSources.Lineup) with { LineupId = IdOf("a") };
        ReviewEntry c = ReviewEntry.Clip("/d/c.dem", 1, 2, "c", ReviewSources.Lineup) with { LineupId = IdOf("c") };
        int merged = queue.Merge([a2, c], "Lineup clips, de_mirage", (old, incoming) => old.LineupId == incoming.LineupId);

        using (Assert.Multiple())
        {
            await Assert.That(merged).IsEqualTo(2);
            await Assert.That(string.Join("|", queue.Entries.Select(e => e.Kind == ReviewEntryKind.Section ? e.Title : e.Note)))
                .IsEqualTo("Lineup clips, de_mirage|a2|b|c|Other|x");
            await Assert.That(queue.Clips[0].Id).IsEqualTo(a.Id);
        }
    }

    private sealed class FileRenderer : ILineupClipRenderer
    {
        public List<(string Demo, IReadOnlyList<LineupClipJob> Jobs)> Calls { get; } = [];

        public int Bytes { get; init; } = 100;

        public Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, IReadOnlyList<LineupClipJob> jobs,
            CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((demoPath, jobs));
            }

            foreach (LineupClipJob job in jobs)
            {
                File.WriteAllBytes(job.GifPath, new byte[Bytes]);
            }

            return Task.FromResult(jobs);
        }
    }

    private sealed class FakeRenderer : ILineupClipRenderer
    {
        public List<(string Demo, IReadOnlyList<LineupClipJob> Jobs)> Calls { get; } = [];

        public TaskCompletionSource? Hold { get; init; }

        public bool Fail { get; set; }

        public async Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, IReadOnlyList<LineupClipJob> jobs,
            CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((demoPath, jobs));
            }

            if (Hold is not null)
            {
                await Hold.Task.ConfigureAwait(false);
            }

            return Fail ? [] : jobs;
        }
    }
}
