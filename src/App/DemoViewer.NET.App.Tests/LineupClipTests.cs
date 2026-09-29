#region

using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lineup Clip Render (plan.md §3, Phase 4): the job planning, which is the whole of what a unit test can
///     reach (the real render parses a demo and runs in the integration phase). Which lineups get a clip, the
///     tick range around the throw, the pair's paths, the GIF request the existing export session receives
///     (format, rate, size, the frames the range resolves to, the camera on the thrower), and the service that
///     produces the setpos + GIF pair per lineup and technique with no one pressing anything.
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

    // Three techniques from one spot: two thrown twice each, one thrown once.
    private static GrenadeLineup WithTechniques()
    {
        IndexedGrenade[] jump = [Throw("/d/a.dem", Row("j1")), Throw("/d/b.dem", Row("j2", 3000, 3100))];
        IndexedGrenade[] stand = [Throw("/d/c.dem", Row("s1", 5000, 5100)), Throw("/d/d.dem", Row("s2", 7000, 7100))];
        IndexedGrenade[] once = [Throw("/d/e.dem", Row("o1", 9000, 9100))];
        return new GrenadeLineup(jump[0].Origin, true, [.. jump, .. stand, .. once], IdOf("spot"))
        {
            AliasIds = [IdOf("spot"), IdOf("older")],
            Techniques =
            [
                new LineupTechnique("stand-jump-left", "jump-throw, left click", jump[0].Origin, true, jump),
                new LineupTechnique("stand-throw-left", "standing throw, left click", stand[0].Origin, false, stand),
                new LineupTechnique("run-throw-left", "running throw, left click", once[0].Origin, false, once)
            ]
        };
    }

    [Test]
    public async Task EveryTechniqueThrownTwice_GetsItsOwnClip_AndTheFirstAdoptsTheLineupsOldPair()
    {
        GrenadeLineup lineup = WithTechniques();
        IReadOnlyList<LineupClipJob> jobs = LineupClipPlanner.PlanEvery([Cluster(lineup)], Directory);
        string old = LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, IdOf("spot"));

        using (Assert.Multiple())
        {
            await Assert.That(jobs.Select(j => j.TechniqueKey ?? "")).IsEquivalentTo(["stand-jump-left", "stand-throw-left"])
                .Because("a technique other than the first needs two throws of its own");
            await Assert.That(jobs[0].Stem).IsEqualTo(old + "-stand-jump-left");
            await Assert.That(jobs[1].DemoPath).IsEqualTo("/d/c.dem").Because("each technique's clip shows its own throw");
            await Assert.That(jobs[0].FormerGifPaths.Select(f => Path.GetFileNameWithoutExtension(f) ?? "")).Contains(old);
            await Assert.That(jobs[0].FormerGifPaths.Select(f => Path.GetFileNameWithoutExtension(f) ?? ""))
                .Contains(LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, IdOf("older")));
            await Assert.That(jobs[1].FormerGifPaths.Select(f => Path.GetFileNameWithoutExtension(f) ?? "")).DoesNotContain(old);
        }

        string clips = TempClips();
        try
        {
            WritePair(clips, old, 100, DateTime.UtcNow);
            FileRenderer renderer = new();
            using LineupClipService service = new(() => [Cluster(lineup)], clips, () => true, renderer);

            await Assert.That(service.Plan()).IsEqualTo(1);
            await service.WorkerTask;
            IReadOnlyList<LineupClipJob> onDisk = LineupClipPlanner.PlanEvery([Cluster(lineup)], clips);
            using (Assert.Multiple())
            {
                await Assert.That(renderer.Calls.SelectMany(c => c.Jobs).Select(j => j.TechniqueKey ?? "")).IsEquivalentTo(["stand-throw-left"]);
                await Assert.That(HasPair(clips, onDisk[0].Stem)).IsTrue().Because("the old per-lineup pair was renamed");
                await Assert.That(HasPair(clips, old)).IsFalse();
                await Assert.That(LineupClipPlanner.FinishedGif(clips, Mirage, GrenadeKind.Smoke, IdOf("spot"), "stand-throw-left"))
                    .IsEqualTo(onDisk[1].GifPath);
            }
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task PlanSoon_CoalescesABurstOfIndexChanges_IntoOnePlanOffTheCallingThread()
    {
        int calls = 0;
        // A pool thread can serve both the caller and the plan, so thread ids prove nothing;
        // what matters is that no plan runs inside a PlanSoon call.
        bool inCall = false;
        bool ranInCall = false;
        FakeRenderer renderer = new();
        using LineupClipService service = new(() =>
            {
                Interlocked.Increment(ref calls);
                ranInCall |= Volatile.Read(ref inCall);
                return [TwoLineups()];
            }, Directory, () => true, renderer, _ => false, (_, _) => { }, planDebounce: TimeSpan.FromMilliseconds(50));

        Volatile.Write(ref inCall, true);
        Task first = service.PlanSoon();
        Task second = service.PlanSoon();
        Task third = service.PlanSoon();
        Volatile.Write(ref inCall, false);
        await Task.WhenAll(first, second, third);
        await service.WorkerTask;

        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(second, first)).IsTrue();
            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(ranInCall).IsFalse();
            await Assert.That(renderer.Calls.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task AnOldPairOfAnotherThrow_IsNotAdopted_ItRendersAgainAndTheOldPairGoes()
    {
        GrenadeLineup lineup = WithTechniques();
        string clips = TempClips();
        try
        {
            string old = LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, IdOf("spot"));
            WritePair(clips, old, 100, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(clips, old + LineupClipPlanner.SetposExtension),
                "setpos 1.00 2.00 3.00; setang 0.00 0.00 0.00");
            FileRenderer renderer = new();
            using LineupClipService service = new(() => [Cluster(lineup)], clips, () => true, renderer);

            await Assert.That(service.Plan()).IsEqualTo(2).Because("the old pair shows a different throw");
            await service.WorkerTask;

            using (Assert.Multiple())
            {
                await Assert.That(HasPair(clips, old + "-stand-jump-left")).IsTrue();
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(clips, old + "-stand-jump-left" + LineupClipPlanner.SetposExtension)))
                    .IsEqualTo(Setpos + Environment.NewLine);
                await Assert.That(HasPair(clips, old)).IsFalse().Because("the old pair goes once the new one exists");
            }
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task ALineupEvictedUnderItsOldName_StaysEvictedUnderItsTechniqueName()
    {
        GrenadeLineup lineup = WithTechniques();
        string clips = TempClips();
        try
        {
            string old = LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, IdOf("spot"));
            File.WriteAllText(Path.Combine(clips, LineupClipService.EvictedFileName), old);
            FileRenderer renderer = new();
            using LineupClipService service = new(() => [Cluster(lineup)], clips, () => true, renderer);

            await Assert.That(service.Plan()).IsEqualTo(1).Because("only the second technique renders");
            await service.WorkerTask;
            await Assert.That(service.Evicted).Contains(old + "-stand-jump-left");

            await Assert.That(service.Request(IdOf("older"), "stand-jump-left")).IsEqualTo(1);
            await service.WorkerTask;
            await Assert.That(HasPair(clips, old + "-stand-jump-left")).IsTrue();
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
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
    public async Task Service_ProducesThePairs_WithNoUserInput()
    {
        FakeRenderer renderer = new();
        Dictionary<string, string> written = [];
        using LineupClipService service = new(() => [TwoLineups()], Directory, () => true, renderer,
            _ => false, (path, text) => written[path] = text);

        int planned = service.Plan();
        await service.WorkerTask;

        await Assert.That(planned).IsEqualTo(2);

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
    public async Task Service_LeavesNoHalfPair_WhenAGifFails()
    {
        FakeRenderer renderer = new() { Fail = true };
        Dictionary<string, string> written = [];
        using LineupClipService service = new(() => [TwoLineups()], Directory, () => true, renderer,
            _ => false, (path, text) => written[path] = text);

        service.Plan();
        await service.WorkerTask;

        using (Assert.Multiple())
        {
            await Assert.That(renderer.Calls.Select(c => c.Demo)).IsEquivalentTo(["/d/a.dem", "/d/b.dem"]);
            await Assert.That(written.Count).IsEqualTo(0).Because("no sidecar without its GIF");
        }
    }

    [Test]
    public async Task Service_PlansNothing_WhenOff_OrWithNoDirectory()
    {
        FakeRenderer renderer = new();
        using LineupClipService off = new(() => [TwoLineups()], Directory, () => false, renderer, _ => false);
        using LineupClipService browser = new(() => [TwoLineups()], null, () => true, renderer, _ => false);

        await Assert.That(off.Plan()).IsEqualTo(0);
        await Assert.That(browser.Plan()).IsEqualTo(0);
        await Assert.That(renderer.Calls.Count).IsEqualTo(0);
    }

    // ── Keyed by lineup and technique, bounded on disk ────────────────────────

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
            FileRenderer renderer = new();
            GrenadeLineup current = before;
            using LineupClipService service = new(() => [Cluster(current)], clips, () => true, renderer);

            await Assert.That(service.Plan()).IsEqualTo(1);
            await service.WorkerTask;
            current = after;

            using (Assert.Multiple())
            {
                await Assert.That(service.Plan()).IsEqualTo(0).Because("the pair on disk is the lineup's, whoever represents it");
                await Assert.That(renderer.Calls.Count).IsEqualTo(1);
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
            using LineupClipService service = new(() => [Cluster(lineup)], clips, () => true, renderer);

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
            using (LineupClipService loading = new(() => [cluster], clips, () => true, new FileRenderer(),
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
            using LineupClipService patient = new(() => [cluster], clips, () => true, new FileRenderer());
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
            using (LineupClipService service = new(() => [cluster], clips, () => true, renderer,
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
            using LineupClipService next = new(() => [cluster], clips, () => true, later);
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
    public async Task ALineupGoneForOnePlan_KeepsItsEviction_UntilTheGraceRunsOut()
    {
        GrenadeCluster cluster = TwoLineups();
        string clips = TempClips();
        try
        {
            IReadOnlyList<LineupClipJob> jobs = LineupClipPlanner.PlanEvery([cluster], clips);
            WritePair(clips, jobs[0].Stem, 100, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(clips, LineupClipService.EvictedFileName), jobs[1].Stem);

            // A re-index drops the demo's rows for a moment, then puts them back.
            bool present = true;
            using (LineupClipService service = new(() => present ? [cluster] : [], clips, () => true, new FileRenderer()))
            {
                present = false;
                service.Plan();
                present = true;
                await Assert.That(service.Plan()).IsEqualTo(0);
                await Assert.That(service.Evicted).IsEquivalentTo([jobs[1].Stem]);
            }

            using LineupClipService impatient = new(() => present ? [cluster] : [], clips, () => true, new FileRenderer(),
                orphanGrace: TimeSpan.Zero);
            present = false;
            impatient.Plan();
            await Assert.That(impatient.Evicted).IsEmpty();
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task ARequestDuringAnEvictionPass_WaitsForIt_AndItsClipComesBack()
    {
        GrenadeCluster cluster = TwoLineups();
        string clips = TempClips();
        try
        {
            IReadOnlyList<LineupClipJob> jobs = LineupClipPlanner.PlanEvery([cluster], clips);

            // The first lineup's pair is the oldest on disk; the second fits the room left, is rendered larger
            // than the pairs on disk, and pushes it over the cap.
            WritePair(clips, jobs[0].Stem, 600, DateTime.UtcNow.AddHours(-1));
            FileRenderer renderer = new() { Bytes = 1000 };
            using LineupClipService service = new(() => [cluster], clips, () => true, renderer,
                maxBytes: () => 1500);

            Task<int>? request = null;
            bool waited = false;
            service.BeforeEvictionDeletes = () =>
            {
                if (request is not null)
                {
                    return;
                }

                request = Task.Run(() => service.Request(jobs[0].LineupId));
                waited = !request.Wait(TimeSpan.FromMilliseconds(300));
            };

            await Assert.That(service.Plan()).IsEqualTo(1);
            await service.WorkerTask;
            await request!;
            while (!service.WorkerTask.IsCompleted || service.Pending.Count > 0)
            {
                await service.WorkerTask;
            }

            using (Assert.Multiple())
            {
                await Assert.That(waited).IsTrue().Because("the request waits for the pass it arrived in");
                await Assert.That(renderer.Calls.SelectMany(c => c.Jobs).Count(j => j.LineupId == jobs[0].LineupId)).IsEqualTo(1)
                    .Because("the pass evicted it, and the request put it back");
                await Assert.That(HasPair(clips, jobs[0].Stem)).IsTrue();
                await Assert.That(service.Evicted).DoesNotContain(jobs[0].Stem);
            }
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task TheDefaultParse_MapsOnlyASettledFile()
    {
        DateTimeOffset now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        TimeProvider clock = new FixedClock(now);
        FileStat old = new(100, now.AddMinutes(-5));
        FileStat fresh = new(100, now.AddSeconds(-10));
        int calls = 0;
        FileStat Growing(string _) => new(100 + calls++, now.AddMinutes(-5));

        using (Assert.Multiple())
        {
            await Assert.That(LineupClipRenderer.MapsFile("x", clock, _ => old)).IsEqualTo(!OperatingSystem.IsBrowser());
            await Assert.That(LineupClipRenderer.MapsFile("x", clock, _ => fresh)).IsFalse().Because("it may still be copying");
            await Assert.That(LineupClipRenderer.MapsFile("x", clock, Growing)).IsFalse().Because("it changed between the two stats");
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

    // ── Rank and cap: most thrown first, and a full cap stops rather than trades ──

    // A lineup thrown `times` times, all in one demo.
    private static GrenadeLineup Thrown(string demo, string id, int times) =>
        Lineup([.. Enumerable.Range(0, times).Select(i => Throw(demo, Row(i == 0 ? id : $"{id}-{i}", 1000 + (i * 500), 1100 + (i * 500))))]);

    [Test]
    public async Task ThePlan_RendersTheMostThrownLineupsFirst()
    {
        FakeRenderer renderer = new();
        GrenadeCluster cluster = Cluster(Thrown("/d/two.dem", "t", 2), Thrown("/d/five.dem", "f", 5), Thrown("/d/three.dem", "h", 3));
        using LineupClipService service = new(() => [cluster], Directory, () => true, renderer, _ => false, (_, _) => { });

        await Assert.That(service.Plan()).IsEqualTo(3);
        await service.WorkerTask;

        await Assert.That(string.Join(",", renderer.Calls.Select(c => c.Demo)))
            .IsEqualTo("/d/five.dem,/d/three.dem,/d/two.dem");
    }

    [Test]
    public async Task AFullCap_StopsScheduling_UnlessALineupOutranksTheLowestKeptPair()
    {
        string clips = TempClips();
        try
        {
            GrenadeLineup keptOld = Thrown("/d/a.dem", "a", 3), keptNew = Thrown("/d/b.dem", "b", 3);
            GrenadeLineup lower = Thrown("/d/c.dem", "c", 2), equal = Thrown("/d/d.dem", "d", 3);
            GrenadeLineup higher = Thrown("/d/e.dem", "e", 5);
            List<GrenadeLineup> lineups = [keptOld, keptNew, lower, equal];
            GrenadeCluster Current() => Cluster([.. lineups]);

            Dictionary<Guid, string> stems = LineupClipPlanner.PlanEvery([Cluster(keptOld, keptNew, lower, equal, higher)], clips)
                .ToDictionary(j => j.LineupId, j => j.Stem);
            DateTime now = DateTime.UtcNow;
            WritePair(clips, stems[keptOld.Id], 1000, now.AddHours(-2));
            WritePair(clips, stems[keptNew.Id], 1000, now.AddHours(-1));
            long cap = new System.IO.DirectoryInfo(clips).EnumerateFiles().Sum(f => f.Length) + 100; // less than one more pair

            FileRenderer renderer = new() { Bytes = 1000 };
            using LineupClipService service = new(() => [Current()], clips, () => true, renderer,
                maxBytes: () => cap);

            await Assert.That(service.Plan()).IsEqualTo(0).Because("the cap is full and nothing outranks a kept pair");
            await Assert.That(renderer.Calls).IsEmpty();

            lineups.Add(higher);
            await Assert.That(service.Plan()).IsEqualTo(1);
            await service.WorkerTask;

            using (Assert.Multiple())
            {
                await Assert.That(renderer.Calls.Single().Jobs.Single().LineupId).IsEqualTo(higher.Id);
                await Assert.That(HasPair(clips, stems[higher.Id])).IsTrue();
                await Assert.That(HasPair(clips, stems[keptOld.Id])).IsFalse().Because("the lowest rank, then the oldest, goes");
                await Assert.That(HasPair(clips, stems[keptNew.Id])).IsTrue();
                await Assert.That(service.Evicted).IsEquivalentTo([stems[keptOld.Id]]);
            }

            // Still full: the equal and the lower lineup never displace a kept pair, so nothing churns.
            await Assert.That(service.Plan()).IsEqualTo(0);
            await Assert.That(renderer.Calls.Count).IsEqualTo(1);
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
        }
    }

    [Test]
    public async Task WithAProcessingQueue_EachDemosBatch_IsALineupClipsItem_OneAtATime()
    {
        using DemoViewer.NET.Services.HeavyJobGate gate = new();
        using DemoProcessingQueue processing = new(gate, a => a(), _ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), () => Task.CompletedTask);
        processing.Pause();
        FakeRenderer renderer = new();
        GrenadeCluster cluster = Cluster(Thrown("/d/two.dem", "t", 2), Thrown("/d/five.dem", "f", 5));
        using LineupClipService service = new(() => [cluster], Directory, () => true, renderer,
            _ => false, (_, _) => { }, processing: processing);

        await Assert.That(service.Plan()).IsEqualTo(2);
        DemoQueueItemSnapshot first = processing.Snapshot().Single(i => i.Kind == QueueJobKind.LineupClips);
        await Assert.That(first.Kind).IsEqualTo(QueueJobKind.LineupClips);
        await Assert.That(first.DisplayName).IsEqualTo("Lineup clips: de_mirage, 1 clip from five.dem");
        await Assert.That(first.State).IsEqualTo(DemoQueueItemState.Queued).Because("a paused queue starts nothing");
        await Assert.That(renderer.Calls).IsEmpty();

        processing.Resume();
        await service.WorkerTask.WaitAsync(TimeSpan.FromSeconds(10));

        List<DemoQueueItemSnapshot> items = processing.Snapshot().Where(i => i.Kind == QueueJobKind.LineupClips).ToList();
        await Assert.That(items.Count).IsEqualTo(2);
        await Assert.That(items.All(i => i.State == DemoQueueItemState.Completed)).IsTrue();
        await Assert.That(string.Join(",", renderer.Calls.Select(c => c.Demo))).IsEqualTo("/d/five.dem,/d/two.dem");
    }

    [Test]
    public async Task CancellingABatchInTheQueue_DropsThatDemosClips_AndTheNextDemoGoesOn()
    {
        using DemoViewer.NET.Services.HeavyJobGate gate = new();
        using DemoProcessingQueue processing = new(gate, a => a(), _ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), () => Task.CompletedTask);
        processing.Pause();
        FakeRenderer renderer = new();
        GrenadeCluster cluster = Cluster(Thrown("/d/two.dem", "t", 2), Thrown("/d/five.dem", "f", 5));
        using LineupClipService service = new(() => [cluster], Directory, () => true, renderer,
            _ => false, (_, _) => { }, processing: processing);

        service.Plan();
        processing.RemoveByUser(processing.Snapshot().Single(i => i.Kind == QueueJobKind.LineupClips).Id);
        processing.Resume();
        await service.WorkerTask.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(string.Join(",", renderer.Calls.Select(c => c.Demo))).IsEqualTo("/d/two.dem");
        await Assert.That(service.Pending).IsEmpty();
    }

    [Test]
    public async Task TheRenderer_WritesAside_AndRenamesOnlyAFinishedGif()
    {
        string clips = TempClips();
        string source = Path.Combine(clips, "source.bin"); // RenderAsync only checks that the demo exists
        File.WriteAllBytes(source, [0]);
        try
        {
            LineupClipJob job = LineupClipPlanner.PlanEvery([TwoLineups()], clips)[0];
            string partial = LineupClipRenderer.PartialPathFor(job.GifPath);
            ParsedDemo demo = SyntheticParsedDemo.Create(Frames(0, 2000));
            string? seenOutput = null;

            LineupClipRenderer Renderer(Func<CancellationToken, Task> after) => new(_ => demo, _ => null,
                render: async (request, _, ct) =>
                {
                    seenOutput = request.OutputPath;
                    await File.WriteAllBytesAsync(request.OutputPath, new byte[64], CancellationToken.None);
                    await after(ct);
                });

            using CancellationTokenSource cts = new();
            bool cancelled = false;
            try
            {
                await Renderer(_ =>
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }).RenderAsync(source, [job], cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            using (Assert.Multiple())
            {
                await Assert.That(cancelled).IsTrue();
                await Assert.That(seenOutput).IsEqualTo(partial).Because("the encoder never writes the real name");
                await Assert.That(File.Exists(job.GifPath)).IsFalse().Because("a cut-short GIF must not look rendered");
                await Assert.That(File.Exists(partial)).IsFalse();
            }

            IReadOnlyList<LineupClipJob> failed = await Renderer(_ => throw new IOException("disk full"))
                .RenderAsync(source, [job], CancellationToken.None);
            await Assert.That(failed).IsEmpty();
            await Assert.That(File.Exists(job.GifPath)).IsFalse();
            await Assert.That(File.Exists(partial)).IsFalse();

            IReadOnlyList<LineupClipJob> done = await Renderer(_ => Task.CompletedTask)
                .RenderAsync(source, [job], CancellationToken.None);
            await Assert.That(done.Single()).IsEqualTo(job);
            await Assert.That(new FileInfo(job.GifPath).Length).IsEqualTo(64);
            await Assert.That(File.Exists(partial)).IsFalse();
            await Assert.That(LineupClipPlanner.StemOf(partial)).IsEqualTo(job.Stem);
            await Assert.That(System.IO.Directory.EnumerateFiles(clips).Any(f => f == partial)).IsFalse()
                .Because("the clip directory's own listing does not see the partial");
        }
        finally
        {
            System.IO.Directory.Delete(clips, true);
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
