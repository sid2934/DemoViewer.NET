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

    private static GrenadeLineup Lineup(params IndexedGrenade[] throws) => new(throws[0].Origin, false, throws);

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
