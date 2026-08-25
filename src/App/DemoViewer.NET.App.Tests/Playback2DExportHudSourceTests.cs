#region

using System.Globalization;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>Which frame the exported scoreboard is read off.</b>
///     <para>
///         The tab's export HUD used to close over the tab's own <c>_frame</c> — the LIVE viewport's
///         frame — so the round and the score in the video were whatever the user happened to be looking
///         at when they pressed Start, burnt onto every frame of the export. Worse, the closure was live:
///         if playback resumed while the export rendered, the burnt-in scoreboard drifted with the
///         viewport rather than with the video.
///     </para>
///     <para>
///         The kill feed was always right, because it is the whole demo's timeline windowed by tick
///         inside the data source. Only the clock half read a moment instead of a function — which is
///         why <c>Playback2DKillFeedTests</c>' snapshot case could pass with the bug in place: it
///         verified <c>ClockReading.From</c> and the VM's <c>GameInfo</c> separately, and never executed
///         the closure that joins them.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DExportHudSourceTests
{
    private const string NukeSample = "assets/tour/sample-de_nuke.dem";

    /// <summary>
    ///     The regression, stated as the contrast that makes it visible: a live viewport that knows
    ///     nothing (no game-rules entity has ever been pushed through the fake context, so its panel reads
    ///     "—" and 0:0) and an export source replayed to a real mid-match frame. The exported clock must
    ///     be the source's.
    /// </summary>
    [Test]
    public async Task TheExportedClock_ReadsTheExportsOwnFrame_NotTheLiveViewport()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(ResolveNuke());
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();

        // A live push with players but no game rules: this is the state the panel is in, and it is what
        // the old closure captured.
        ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f), (1, 3, 900f, -500f, 64f, 270f));
        await Assert.That(vm.GameInfo.RoundNumber).IsEqualTo("—")
            .Because("the live viewport has no round to show in this harness");
        await Assert.That(vm.GameInfo.TScore).IsEqualTo(0);

        using TrackerFrameSource source = MidMatchSource(demo);
        IHudDataSource hud = vm.BuildExportHud()(source);

        Scene2DFrame exported = source.FrameAt(0);
        HudSnapshot drawn = hud.At(exported.Time.Tick);

        Console.WriteLine($"[export-hud] live panel round={vm.GameInfo.RoundNumber} " +
                          $"{vm.GameInfo.TScore}:{vm.GameInfo.CtScore} — exported round={drawn.RoundNumber} " +
                          $"{drawn.TScore}:{drawn.CtScore} at tick {drawn.Tick}");

        // Fails on the old capture, which answered the live panel's "—" for every tick of the video.
        await Assert.That(drawn.RoundNumber).IsNotEqualTo(vm.GameInfo.RoundNumber);
        await Assert.That(drawn.RoundNumber).IsEqualTo(
            ClockReading.From(source.LastGameInfo).Round);
        await Assert.That(int.TryParse(drawn.RoundNumber, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int round)).IsTrue();
        await Assert.That(round).IsEqualTo(source.LastGameInfo.RoundNumber);
        await Assert.That(drawn.TScore).IsEqualTo(source.LastGameInfo.TScore);
        await Assert.That(drawn.CtScore).IsEqualTo(source.LastGameInfo.CtScore);
    }

    /// <summary>
    ///     And it keeps reading it. The export walks its range while the app carries on; the video's
    ///     scoreboard must follow the VIDEO. A capture — of either frame — is a frozen number, and this is
    ///     the case that says so out loud: the live viewport is pushed forward between samples and the
    ///     exported clock ignores it.
    /// </summary>
    [Test]
    public async Task TheExportedClock_TracksTheExport_WhileTheLiveViewportMovesUnderneathIt()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(ResolveNuke());
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Tab();
        ctx.PushMarkers((0, 2, -800f, 600f, 64f, 90f));

        using TrackerFrameSource source = MidMatchSource(demo);
        IHudDataSource hud = vm.BuildExportHud()(source);

        HashSet<string> rounds = [];
        int stride = Math.Max(1, source.FrameCount / 40);

        for (int i = 0; i < source.FrameCount; i += stride)
        {
            Scene2DFrame exported = source.FrameAt(i);
            HudSnapshot drawn = hud.At(exported.Time.Tick);

            // The user keeps watching while the export runs — the exact thing that used to move the
            // burnt-in scoreboard.
            ctx.PushMarkers((0, 2, -800f + i, 600f, 64f, 90f));

            await Assert.That(drawn.RoundNumber)
                .IsEqualTo(ClockReading.From(source.LastGameInfo).Round);
            await Assert.That(drawn.RoundNumber).IsNotEqualTo(vm.GameInfo.RoundNumber);
            rounds.Add(drawn.RoundNumber);
        }

        Console.WriteLine($"[export-hud] rounds across the exported range: {string.Join(",", rounds)}");
        await Assert.That(rounds.Count).IsGreaterThan(1)
            .Because("the range spans a round change, so a frozen clock cannot pass this");
    }

    private static (Playback2DTabViewModel, Playback2DFakeContext) Tab()
    {
        Playback2DTabViewModel vm = new();
        Playback2DFakeContext ctx = new();
        ctx.Roster.Add(new PlayerRosterEntry { Slot = 0, Name = "Neo", SteamId = 1 });
        ctx.Roster.Add(new PlayerRosterEntry { Slot = 1, Name = "Smith", SteamId = 2 });
        vm.OnActivated(ctx);
        return (vm, ctx);
    }

    private static TrackerFrameSource MidMatchSource(ParsedDemo demo)
    {
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int tickRate = demo.TickRate > 0 ? (int)Math.Round((double)demo.TickRate) : 64;

        TrackerFrameSource source = new(frames, new SceneFrameBuilder(),
            frames.Count / 2, frames.Count - 1, 60, 1.0, tickRate);
        source.Prepare(CancellationToken.None);
        return source;
    }

    private static string ResolveNuke()
    {
        string bundled = Path.Combine(RepoRoot(), NukeSample.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(bundled)
            ? bundled
            : DemoTestHelper.FindDemoPath()
              ?? throw new SkipTestException($"no demo (tried {NukeSample} and the staged locations)");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new SkipTestException("could not locate the repository root");
    }
}
