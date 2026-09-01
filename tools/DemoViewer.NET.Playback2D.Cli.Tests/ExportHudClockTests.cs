#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using DemoViewer.NET.TestSupport;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <b>What <c>dv2d export --hud</c> actually writes on the scoreboard.</b>
///     <para>
///         <c>SceneFrameBuilder</c> has always read the round off <c>CCSGameRulesProxy</c> and the two
///         scores off <c>CCSTeam.m_iScore</c>, and <c>ClockLayer</c> has always drawn whatever its data
///         source said, but the CLI's data source was <c>static _ =&gt; ClockReading.Unknown</c>, a
///         constant. Every frame of every CLI export, at any point in any match, read
///         <c>Round —  T 0 : 0 CT</c>.
///     </para>
///     <para>
///         These cases execute <see cref="ExportCommand.BuildHud" /> itself rather than a look-alike
///         delegate. A test that rebuilt an equivalent closure would have passed against the constant too,
///         so the bug survived a suite that already covered the kill-feed window, the
///         <c>ClockReading</c> projection and the layer's ink separately.
///     </para>
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ExportHudClockTests
{
    /// <summary>
    ///     The regression. At a mid-match frame the exported clock must carry the round and the scores the
    ///     frame builder read for THAT frame, not the placeholder.
    /// </summary>
    [Test]
    public async Task TheExportedClock_CarriesTheRoundAndScore_OfTheFrameBeingDrawn()
    {
        using Replay replay = Replay.MidMatch();

        Scene2DFrame frame = replay.Source.FrameAt(0);
        HudSnapshot drawn = replay.Hud.At(frame.Time.Tick);

        Console.WriteLine($"[hud-clock] round={drawn.RoundNumber} T={drawn.TScore} CT={drawn.CtScore} " +
                          $"countdown={drawn.CountdownSeconds:F1}s at tick {drawn.Tick}");

        // "—" is what ClockReading.Unknown renders, and it is what every CLI export used to show.
        await Assert.That(drawn.RoundNumber).IsNotEqualTo("—")
            .Because("the CLI's clock was a constant ClockReading.Unknown");
        await Assert.That(int.TryParse(drawn.RoundNumber, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int round)).IsTrue();
        await Assert.That(round).IsGreaterThanOrEqualTo(1);

        // And it is the SOURCE's reading, field for field: the frame the export is about to draw.
        SceneGameInfo info = replay.Source.LastGameInfo;
        await Assert.That(drawn.RoundNumber).IsEqualTo(ClockReading.From(info).Round);
        await Assert.That(drawn.TScore).IsEqualTo(info.TScore);
        await Assert.That(drawn.CtScore).IsEqualTo(info.CtScore);
        await Assert.That(round).IsEqualTo(info.RoundNumber);
    }

    /// <summary>
    ///     Not a capture. The clock is a function of the frame being drawn, so it has to keep moving as the
    ///     export walks the range, which is the half of this bug the App shipped: a value captured when
    ///     Start was pressed, burnt into every frame of the video.
    /// </summary>
    [Test]
    public async Task TheClock_FollowsTheExportAcrossTheRange_RatherThanFreezing()
    {
        using Replay replay = Replay.MidMatch();

        List<double> countdowns = [];
        HashSet<string> rounds = [];

        int stride = Math.Max(1, replay.Source.FrameCount / 60);
        for (int i = 0; i < replay.Source.FrameCount; i += stride)
        {
            Scene2DFrame frame = replay.Source.FrameAt(i);
            HudSnapshot drawn = replay.Hud.At(frame.Time.Tick);

            // Read AFTER the frame it describes was built, on every single sample: the ordering
            // SceneExportSession guarantees (TimeAt → FrameAt → Advance → Render).
            await Assert.That(drawn.RoundNumber).IsEqualTo(ClockReading.From(replay.Source.LastGameInfo).Round);
            await Assert.That(drawn.Tick).IsEqualTo(frame.Time.Tick);

            countdowns.Add(drawn.CountdownSeconds);
            rounds.Add(drawn.RoundNumber);
        }

        Console.WriteLine($"[hud-clock] rounds seen: {string.Join(",", rounds)}; " +
                          $"countdown {countdowns[0]:F1}s → {countdowns[^1]:F1}s");

        // Something on the clock has to have moved across the sampled span, or the reading is a constant
        // wearing a demo's clothes. NaN-aware: "no countdown running" → "0:43 left" is a change, and
        // `NaN != 43` is false in IEEE arithmetic.
        bool moved = false;
        for (int i = 1; i < countdowns.Count; i++)
        {
            moved |= Differs(countdowns[i], countdowns[0]);
        }

        await Assert.That(moved || rounds.Count > 1).IsTrue();
    }

    private static bool Differs(double a, double b) =>
        double.IsNaN(a) != double.IsNaN(b) ||
        !double.IsNaN(a) && Math.Abs(a - b) > 1e-6;

    /// <summary>
    ///     The pixels, not just the value. <c>ClockLayer</c> fed the real source draws something different
    ///     from <c>ClockLayer</c> fed <c>ClockReading.Unknown</c>, which is the difference a viewer sees
    ///     between "Round 8  T 4 : 3 CT" and "Round —  T 0 : 0 CT".
    /// </summary>
    [Test]
    public async Task TheDrawnClock_DiffersFromThePlaceholderItUsedToBe()
    {
        using Replay replay = Replay.MidMatch();

        Scene2DFrame frame = replay.Source.FrameAt(0);
        int tick = frame.Time.Tick;

        TimelineHudDataSource placeholder = new([], replay.TickRate, static _ => ClockReading.Unknown);

        string real = Ink(replay.Hud, tick);
        string blank = Ink(placeholder, tick);

        Console.WriteLine($"[hud-clock] drawn={real[..16]} placeholder={blank[..16]}");
        await Assert.That(real).IsNotEqualTo(blank);
    }

    /// <summary>
    ///     End to end, through <c>Main</c>: a real range of a real demo exports with the HUD on and the
    ///     clock layer in the stack. <c>--no-encode</c> keeps it off ffmpeg. This is the wiring assertion
    ///     (the HUD is now built after the frame source), not an encoder test.
    /// </summary>
    [Test]
    public async Task ExportWithHud_RunsEndToEnd_AndDeclaresTheClockLayer()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
        int start = demo.Frames.Count / 2;
        int end = Math.Min(demo.Frames.Count - 1, start + 120);

        CliRun run = Dv2d.InProcess("export", "--demo", Dv2d.RequireDemo(),
            "--from", start.ToString(CultureInfo.InvariantCulture),
            "--to", end.ToString(CultureInfo.InvariantCulture),
            "--hud", "--no-encode", "--size", "320x180", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.StdErr);

        JsonObject json = run.Json();
        await Assert.That(json["ok"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json["frames"]!.GetValue<int>()).IsGreaterThan(0);

        string layers = json["layers"]!.ToJsonString();
        await Assert.That(layers).Contains(SceneLayerIds.HudClock);
    }

    private static string Ink(IHudDataSource data, int tick)
    {
        using CpuSurfaceProvider surfaces = new();
        using SKSurface surface = surfaces.CreateSurface(new SKSizeI(400, 120));
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        SceneTime time = new(tick, 0, 0, 1 / 60.0, true);
        using ClockLayer layer = new(data);
        layer.Advance(in time, Scene2DFrame.Empty);
        layer.Render(surface.Canvas, Context());

        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToHexString(SHA256.HashData(png.ToArray()));
    }

    private static SceneRenderContext Context() =>
        new(Scene2DFrame.Empty, default, ViewportTransform.Fit(400, 120, -100, -100, 100, 100),
            new SKRect(0, 0, 400, 120), 0, 0, 0,
            RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(default, 0,
                new MapLevel
                {
                    Id = default,
                    Name = "l",
                    ZMin = 0,
                    ZMax = 100
                },
                ViewportTransform.Fit(400, 120, -100, -100, 100, 100), default, 0)
        };

    /// <summary>
    ///     A prepared export over a mid-match range of the committed demo, with the CLI's OWN hud data
    ///     source built over it by <see cref="ExportCommand.BuildHud" />.
    /// </summary>
    private sealed class Replay : IDisposable
    {
        private Replay(TrackerFrameSource source, TimelineHudDataSource hud, int tickRate)
        {
            Source = source;
            Hud = hud;
            TickRate = tickRate;
        }

        public TrackerFrameSource Source { get; }
        public TimelineHudDataSource Hud { get; }
        public int TickRate { get; }

        public void Dispose() => Source.Dispose();

        public static Replay MidMatch()
        {
            ParsedDemo demo = DemoTestHelper.GetOrParse(Dv2d.RequireDemo());
            IReadOnlyList<DemoFrame> frames = demo.Frames;
            int tickRate = demo.TickRate > 0 ? (int)Math.Round((double)demo.TickRate) : 64;

            // Half way in: far enough that the game-rules entity and both team entities exist and the
            // scoreboard is no longer 0:0 by coincidence.
            int start = frames.Count / 2;
            int end = frames.Count - 1;

            TrackerFrameSource source = new(frames, new SceneFrameBuilder(), start, end, 60, 1.0, tickRate);
            source.Prepare(CancellationToken.None);

            return new Replay(source, ExportCommand.BuildHud(source, tickRate), tickRate);
        }
    }
}
