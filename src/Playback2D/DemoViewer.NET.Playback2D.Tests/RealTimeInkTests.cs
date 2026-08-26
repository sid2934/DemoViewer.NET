#region

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Real-time ink replay (plan D7): a stroke carrying a captured cadence draws itself on at the speed
///     it was authored at — pauses included — and dissolves behind itself.
///     <para>
///         Every case is measured off a <b>rendered surface</b>, never off the run table. The table's own
///         arithmetic is <c>StrokeTiming</c>'s contract and is tested where it lives; what is unproven,
///         and what these cover, is that the layer turns it into the right picture. The fixture is a
///         left-to-right stroke on a 400×400 pane, so "how far has the head got" is a pixel column and
///         "how faded is the tail" is a brightness at a column — claims a whole-surface diff cannot make.
///     </para>
/// </summary>
public class RealTimeInkTests
{
    private static readonly SKSizeI _size = new(400, 400);

    // The surface §4's numbers were taken on. Only the costing case uses it.
    private static readonly SKSizeI _hd = new(1920, 1080);

    /// <summary>
    ///     The head advances with the tick, and what is drawn is a genuine PREFIX — the stroke still
    ///     starts where it was drawn, it does not slide across the map.
    /// </summary>
    [Test]
    public async Task RealTimeStroke_DrawsAPrefix_ThatGrowsWithTheTick()
    {
        using Fixture fixture = Fixture.With(RealTimeFakes.RealTime(
            RealTimeFakes.Steady(RealTimeFakes.SampleCount, 128)));

        int quarter = Head(fixture.Render(132));
        int half = Head(fixture.Render(164));
        int done = Head(fixture.Render(228));

        Console.WriteLine($"[realtime] head px quarter={quarter} half={half} done={done}");

        await Assert.That(quarter).IsGreaterThan(0);
        await Assert.That(quarter).IsLessThan(half);
        await Assert.That(half).IsLessThan(done);

        await Assert.That(Tail(fixture.Render(132))).IsEqualTo(Tail(fixture.Render(228)))
            .Because("the hold outlasts the draw here, so the tail has not moved — this is a prefix, " +
                     "not a stroke sliding along its own path");

        await Assert.That(Ink(fixture.Render(99))).IsEqualTo(0)
            .Because("nothing has been drawn before the tick the stroke was authored at");
    }

    /// <summary>
    ///     <b>The feature.</b> A pause in the run table is where the author stopped to think, and it is
    ///     what a viewer reads as "it is replaying me" (§2). The same ten-tick advance must reveal
    ///     strictly less across the pause than outside it — and, here, nothing at all.
    /// </summary>
    [Test]
    public async Task APauseInTheRunTable_StallsTheHead()
    {
        // 0→99 in 50 ticks, a 150-tick rest at sample 100, then 100→199 in 50 more.
        using Fixture fixture = Fixture.With(RealTimeFakes.RealTime(
            RealTimeFakes.WithPause(RealTimeFakes.SampleCount, 50, 150)));

        int movingFrom = Head(fixture.Render(110));
        int movingTo = Head(fixture.Render(120));
        int pausedFrom = Head(fixture.Render(160));
        int pausedTo = Head(fixture.Render(170));

        Console.WriteLine($"[realtime] 10 ticks while moving {movingFrom}→{movingTo} px, " +
                          $"10 ticks across the pause {pausedFrom}→{pausedTo} px");

        await Assert.That(movingTo - movingFrom).IsGreaterThan(0);
        await Assert.That(pausedTo - pausedFrom).IsEqualTo(0)
            .Because("the head stops exactly where the hand stopped; that is the whole feature");
        await Assert.That(pausedTo - pausedFrom).IsLessThan(movingTo - movingFrom);

        // …and it starts again afterwards, so the pause is a stall and not a truncation. The rest runs
        // from elapsed 50 to elapsed 200, i.e. ticks 150 to 300, so 320 is the first tick that proves it.
        await Assert.That(Head(fixture.Render(320))).IsGreaterThan(pausedTo);
    }

    /// <summary>
    ///     §3's per-section trapezoid: the tail is dimmer than the head at a tick past the tail's own
    ///     hold, while the head is still being drawn at full opacity.
    /// </summary>
    [Test]
    public async Task TheTailFades_WhileTheHeadIsStillAdvancing()
    {
        using Fixture fixture = Fixture.With(RealTimeFakes.RealTime(
            RealTimeFakes.Steady(RealTimeFakes.SampleCount, 128), from: 100, hold: 32, fadeOut: 32));

        // Tick 150: sample 0 is 18 ticks past its 32-tick hold, so it sits mid-lead-out; the head is at
        // roughly sample 78 and has not begun to age at all.
        SKColor[] pixels = fixture.Render(150);
        int tail = Brightness(pixels, RealTimeFakes.LeftWorld, RealTimeFakes.WorldXOf(6));
        int head = Brightness(pixels, RealTimeFakes.WorldXOf(50), RealTimeFakes.WorldXOf(70));
        int background = ScenePalette.Dark.Background.Red;

        Console.WriteLine($"[realtime] red at the tail={tail} at the head={head} background={background}");

        await Assert.That(tail).IsLessThan(head)
            .Because("sample 0 is dissolving while the head is still full-strength ink");
        await Assert.That(tail).IsGreaterThan(background + 8)
            .Because("mid-lead-out is dim, not gone — a section that vanished at UntilTick would have " +
                     "no ramp at all");
        await Assert.That(head).IsEqualTo(Brightness(fixture.Render(120),
            RealTimeFakes.WorldXOf(20), RealTimeFakes.WorldXOf(40)))
            .Because("the body is the element's plateau wherever it sits; it does not dim with the tail");
    }

    /// <summary>
    ///     §3's consequence, from ONE control. A hold that outlasts the draw shows the whole stroke and
    ///     then dissolves it from the start; a hold that does not makes the stroke chase its own tail.
    /// </summary>
    [Test]
    public async Task HoldTicks_ChoosesBetweenDissolvingFromTheStart_AndChasingItsOwnTail()
    {
        StrokeTiming cadence = RealTimeFakes.Steady(RealTimeFakes.SampleCount, 128);
        using Fixture whole = Fixture.With(RealTimeFakes.RealTime(cadence, hold: 512));
        using Fixture chasing = Fixture.With(RealTimeFakes.RealTime(cadence, hold: 8));

        // Tick 228 is the tick the last sample is drawn at, for both.
        SKColor[] wholePixels = whole.Render(228);
        SKColor[] chasingPixels = chasing.Render(228);

        Console.WriteLine($"[realtime] hold 512: tail={Tail(wholePixels)} head={Head(wholePixels)} " +
                          $"ink={Ink(wholePixels)} | hold 8: tail={Tail(chasingPixels)} " +
                          $"head={Head(chasingPixels)} ink={Ink(chasingPixels)}");

        await Assert.That(Head(wholePixels)).IsEqualTo(Head(chasingPixels))
            .Because("the head is the cadence's answer and the hold has no say in it");
        await Assert.That(Tail(chasingPixels)).IsGreaterThan(Tail(wholePixels) + 100)
            .Because("a hold shorter than the draw leaves only a moving window of ink");
        await Assert.That(Ink(wholePixels)).IsGreaterThan(Ink(chasingPixels) * 2);

        // The long-hold case at the end of its draw is the WHOLE stroke at full opacity — one section
        // over the whole point list, which is byte for byte the untimed element's single draw. That is
        // the guarantee "a non-RealTime element renders identically to today" rests on, asserted rather
        // than argued.
        using Fixture untimed = Fixture.With(RealTimeFakes.Untimed());
        await Assert.That(Hash(wholePixels)).IsEqualTo(Hash(untimed.Render(228)))
            .Because("a fully-drawn, fully-held real-time stroke IS the plain stroke");
    }

    /// <summary>
    ///     <b>The export determinism gate, at the layer.</b> §5: the reveal is <c>f(Tick)</c> with no
    ///     accumulated state, so arriving at a tick by stepping, by jumping, or by the 30 fps sampling
    ///     that SKIPS ticks (<c>ticksPerOutputFrame ≈ 2.13</c>) must all give the same pixels.
    ///     <para>
    ///         Both named ways to lose it fail here: an accumulated <c>DeltaSeconds</c> diverges between
    ///         the stepped and the jumped layer, and a one-tick pulse is missed entirely by the skipping
    ///         one.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reveal_IsAPureFunctionOfTheTick_HoweverTheTickWasReached()
    {
        StrokeTiming cadence = RealTimeFakes.WithPause(RealTimeFakes.SampleCount, 50, 60);
        AnnotationElement element = RealTimeFakes.RealTime(cadence, from: 100, hold: 40, fadeOut: 32);
        const int target = 196;

        using Fixture jumped = Fixture.With(element);
        string reference = Hash(jumped.Render(target));

        using Fixture stepped = Fixture.With(element);
        for (int tick = 100; tick < target; tick++)
        {
            stepped.Advance(tick);
        }

        using Fixture skipping = Fixture.With(element);
        // 64 tick / 30 fps: the export lands on 100, 102, 104, 106, 109, … and never sees most ticks.
        for (double t = 100; t < target; t += 64.0 / 30)
        {
            skipping.Advance((int)t);
        }

        Console.WriteLine($"[realtime] hash jumped={reference[..16]} " +
                          $"stepped={Hash(stepped.Render(target))[..16]} " +
                          $"skipped={Hash(skipping.Render(target))[..16]}");

        await Assert.That(Hash(stepped.Render(target))).IsEqualTo(reference)
            .Because("the layer holds no replay state, so 96 advances leave it exactly where one does");
        await Assert.That(Hash(skipping.Render(target))).IsEqualTo(reference)
            .Because("a 30 fps export skips ticks; RevealedCount is continuous in the tick for this");
    }

    /// <summary>Scrubbing backwards un-draws it, and lands on the same picture a cold layer would.</summary>
    [Test]
    public async Task ScrubbingBackwards_UnDrawsTheStroke()
    {
        AnnotationElement element = RealTimeFakes.RealTime(
            RealTimeFakes.Steady(RealTimeFakes.SampleCount, 128), from: 100, hold: 40, fadeOut: 32);

        using Fixture scrubbed = Fixture.With(element);
        using Fixture cold = Fixture.With(element);

        int atEnd = Head(scrubbed.Render(228));
        string back = Hash(scrubbed.Render(132));

        Console.WriteLine($"[realtime] head at the end={atEnd}, after scrubbing back={Head(scrubbed.Render(132))}");

        await Assert.That(Head(scrubbed.Render(132))).IsLessThan(atEnd);
        await Assert.That(back).IsEqualTo(Hash(cold.Render(132)))
            .Because("backwards is the same pure function as forwards — TimeEnvelope.OpacityAt and " +
                     "RevealedCount both read the tick and nothing else");
        await Assert.That(Ink(scrubbed.Render(50))).IsEqualTo(0);
    }

    /// <summary>
    ///     <see cref="AnnotationStyle.RevealOnFadeIn" /> is a DIFFERENT feature that shares the same seam
    ///     (§9): a linear sweep across the fade-in ramp, with no cadence anywhere. Generalising
    ///     <c>RevealCount</c> must leave it exactly where it was, so its sweep is pinned here at three
    ///     points rather than only asserted to be monotone.
    /// </summary>
    [Test]
    public async Task RevealOnFadeIn_StillSweepsLinearly_WithNoCadence()
    {
        AnnotationStyle style = AnnotationStyle.Default with
        {
            RevealOnFadeIn = true,
            WidthWorld = 20f
        };

        // A 100-tick lead-in before tick 200. No Timing: this element predates D7 and must not notice it.
        using Fixture fixture = Fixture.With(new AnnotationElement(Guid.NewGuid(), AnnotationKind.Freehand,
            style, new SpaceRef.World(0), new TimeEnvelope(200, 400, 100, 0), RealTimeFakes.Line(), null));

        int quarter = Head(fixture.Render(125));
        int half = Head(fixture.Render(150));
        int threeQuarters = Head(fixture.Render(175));

        Console.WriteLine($"[reveal-on-fade-in] head px 25%={quarter} 50%={half} 75%={threeQuarters}");

        await Assert.That(quarter).IsGreaterThan(0);
        await Assert.That(Math.Abs(half - quarter - (threeQuarters - half))).IsLessThanOrEqualTo(4)
            .Because("a LINEAR sweep across the ramp — evenly spaced ticks reveal evenly spaced samples");
        await Assert.That(Head(fixture.Render(260))).IsGreaterThan(threeQuarters)
            .Because("past FromTick the whole stroke is drawn, ramp over");
    }

    /// <summary>
    ///     §6's budget with the animated case live. A real-time stroke is the layer's worst content: it
    ///     is re-sectioned and re-outlined every frame and is cached by nothing, so if any of that
    ///     allocated it would show up here and nowhere else.
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task SteadyState_WithARealTimeStrokeLive_AllocatesNothing()
    {
        AnnotationDocument doc = new();
        AnnotationSession session = new(doc);
        using AnnotationLayer layer = new(session);

        StrokeTiming cadence = RealTimeFakes.WithPause(400, 200, 120);
        doc.Apply(new DocDelta.Add(
            RealTimeFakes.RealTime(cadence, from: 100, hold: 64, fadeOut: 48, count: 400), 0));
        doc.Apply(new DocDelta.Add(
            RealTimeFakes.RealTime(RealTimeFakes.Steady(400, 260), from: 100, hold: 64, fadeOut: 48,
                count: 400, y: 120), 1));
        doc.Apply(new DocDelta.Add(RealTimeFakes.Untimed(count: 400, y: -120), 2));

        // Tick 400 is mid-replay for both cadences: a live head, a full-alpha body and all eight tail
        // bands, which is every branch of BuildSections at once.
        SceneRenderContext ctx = Context(400);
        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(_size);

        long first = MeasureWindow(layer, surface.Canvas, in ctx);
        long second = MeasureWindow(layer, surface.Canvas, in ctx);

        Console.WriteLine($"[realtime] alloc window1={first} B window2={second} B " +
                          $"({second / 512.0:F2} B/frame), prepared={layer.PreparedCount}");

        await Assert.That(layer.PreparedCount).IsEqualTo(3)
            .Because("a fixture that culled its own ink would measure an empty layer");
        await Assert.That(second).IsEqualTo(0);
    }

    /// <summary>
    ///     §4's costing, kept honest. The plan priced the tail ramp at 117 µs (k=1) → 152 µs (k=8) for a
    ///     400-sample stroke on a 1080p CPU surface, at 0 B/frame for every k, and the whole "one
    ///     full-alpha body plus k short tail draws" shape rests on that number staying flat in the sample
    ///     count.
    ///     <para>
    ///         The microseconds are <b>reported, never gated</b>: a µs gate on a shared runner is a
    ///         referendum on the runner, and the gate that matters — the whole scene inside its draw
    ///         budget with real ink on it — is <c>BudgetTests.FullScene_WithRealStrokeInk…</c>. The bytes
    ///         are gated, because zero is zero on every machine.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task OneRealTimeStroke_CostsAboutWhatSection4Costed()
    {
        const int samples = 400;

        using Fixture plain = Fixture.With(RealTimeFakes.Untimed(count: samples, width: 24f));
        using Fixture live = Fixture.With(RealTimeFakes.RealTime(
            RealTimeFakes.Steady(samples, 260), from: 100, hold: 64, fadeOut: 48, count: samples,
            width: 24f));

        // Tick 228: the plain stroke, whole, in one draw. Tick 300: elapsed 200 of 260, so the real-time
        // stroke has a live head, a full-alpha body and all eight tail bands at once.
        (double plainUs, long plainBytes) = Cost(plain, 228);
        (double liveUs, long liveBytes) = Cost(live, 300);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[realtime] one {samples}-sample stroke at {_hd.Width}x{_hd.Height}: " +
            $"plain {plainUs:F0} µs/frame {plainBytes} B/frame → " +
            $"real-time, body + 8 tail bands {liveUs:F0} µs/frame {liveBytes} B/frame"));

        await Assert.That(plainBytes).IsEqualTo(0);
        await Assert.That(liveBytes).IsEqualTo(0)
            .Because("§4 measured 0 B/frame at k = 1, 8 and 64; a per-section list would land here");
    }

    // Median frame cost and steady-state allocation for one fixture at one tick. Median, not mean: a
    // single scheduler hiccup in 256 frames should not move the number a reader is asked to compare
    // against a plan.
    private static (double Micros, long Bytes) Cost(Fixture fixture, int tick)
    {
        SceneRenderContext ctx = HdContext(tick);
        using CpuSurfaceProvider provider = new();
        using SKSurface surface = provider.CreateSurface(_hd);

        for (int i = 0; i < 64; i++)
        {
            fixture.Frame(surface.Canvas, in ctx);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        double[] frames = new double[256];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            fixture.Frame(surface.Canvas, in ctx);
            frames[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(frames);
        return (frames[frames.Length / 2], bytes / frames.Length);
    }

    private static long MeasureWindow(AnnotationLayer layer, SKCanvas canvas, in SceneRenderContext ctx)
    {
        SceneTime time = ctx.Time;
        for (int i = 0; i < 32; i++)
        {
            layer.Advance(in time, Scene2DFrame.Empty);
            layer.Render(canvas, ctx);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            layer.Advance(in time, Scene2DFrame.Empty);
            layer.Render(canvas, ctx);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ── Surface probes. ─────────────────────────────────────────────────────────────────────────────

    private static SceneRenderContext Context(int tick)
    {
        MapLevel level = new()
        {
            Id = new MapLevelId(0),
            Name = "floor 0",
            ZMin = -1000,
            ZMax = 1000
        };

        ViewportTransform transform = ViewportTransform.Fit(_size.Width, _size.Height,
            -500, -500, 500, 500);

        return new SceneRenderContext(Scene2DFrame.Empty, Scene2DFrame.Empty.Time with
            {
                Tick = tick
            },
            transform, SKRect.Create(_size.Width, _size.Height), -1, level.ZMin, level.ZMax,
            RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(level.Id, 0, level, transform,
                SKRect.Create(_size.Width, _size.Height), 1)
        };
    }

    // The costing case's context: the same single-level pane on §4's 1080p surface, framed so a
    // 900-unit stroke covers about half the width — a stroke drawn at one pixel would be measuring the
    // outliner and nothing of the rasterizer.
    private static SceneRenderContext HdContext(int tick)
    {
        MapLevel level = new()
        {
            Id = new MapLevelId(0),
            Name = "floor 0",
            ZMin = -1000,
            ZMax = 1000
        };

        ViewportTransform transform = ViewportTransform.Fit(_hd.Width, _hd.Height,
            -1000, -1000, 1000, 1000);

        return new SceneRenderContext(Scene2DFrame.Empty, Scene2DFrame.Empty.Time with
            {
                Tick = tick
            },
            transform, SKRect.Create(_hd.Width, _hd.Height), -1, level.ZMin, level.ZMax,
            RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(level.Id, 0, level, transform,
                SKRect.Create(_hd.Width, _hd.Height), 1)
        };
    }

    // World X → pane column, the same fit Context builds.
    private static int Column(float worldX) =>
        (int)Math.Round((worldX + 500) * (_size.Width / 1000.0));

    // The rightmost column carrying ink. On a left-to-right stroke that is the replay head.
    private static int Head(SKColor[] pixels)
    {
        for (int x = _size.Width - 1; x >= 0; x--)
        {
            if (ColumnHasInk(pixels, x))
            {
                return x;
            }
        }

        return -1;
    }

    // The leftmost column carrying ink — the tail, which walks right as the stroke dissolves behind
    // itself.
    private static int Tail(SKColor[] pixels)
    {
        for (int x = 0; x < _size.Width; x++)
        {
            if (ColumnHasInk(pixels, x))
            {
                return x;
            }
        }

        return -1;
    }

    // Peak red between two world X positions. The ink is amber on a dark ground, so red over the
    // background tracks the section's alpha directly.
    private static int Brightness(SKColor[] pixels, float fromWorldX, float toWorldX)
    {
        int lo = Math.Clamp(Column(fromWorldX), 0, _size.Width - 1);
        int hi = Math.Clamp(Column(toWorldX), 0, _size.Width - 1);
        int best = 0;
        for (int y = 0; y < _size.Height; y++)
        {
            for (int x = lo; x <= hi; x++)
            {
                best = Math.Max(best, pixels[(y * _size.Width) + x].Red);
            }
        }

        return best;
    }

    private static bool ColumnHasInk(SKColor[] pixels, int x)
    {
        SKColor background = ScenePalette.Dark.Background;
        for (int y = 0; y < _size.Height; y++)
        {
            SKColor p = pixels[(y * _size.Width) + x];
            if (p.Red != background.Red || p.Green != background.Green || p.Blue != background.Blue)
            {
                return true;
            }
        }

        return false;
    }

    private static int Ink(SKColor[] pixels)
    {
        SKColor background = ScenePalette.Dark.Background;
        int count = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            SKColor p = pixels[i];
            if (p.Red != background.Red || p.Green != background.Green || p.Blue != background.Blue)
            {
                count++;
            }
        }

        return count;
    }

    private static string Hash(SKColor[] pixels)
    {
        byte[] bytes = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            SKColor p = pixels[i];
            bytes[i * 4] = p.Red;
            bytes[(i * 4) + 1] = p.Green;
            bytes[(i * 4) + 2] = p.Blue;
            bytes[(i * 4) + 3] = p.Alpha;
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>
    ///     A document, a session and a LIVE layer over one element. The layer is kept across calls on
    ///     purpose: a determinism claim about a stateless reveal is only worth making against an instance
    ///     that has had every chance to accumulate something.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly AnnotationLayer _layer;

        private Fixture(AnnotationElement element)
        {
            AnnotationDocument document = new();
            document.Apply(new DocDelta.Add(element, 0));
            _layer = new AnnotationLayer(new AnnotationSession(document));
        }

        public static Fixture With(AnnotationElement element) => new(element);

        public void Dispose() => _layer.Dispose();

        /// <summary>Advances to a tick without drawing — how a skipped export frame reaches the layer.</summary>
        /// <param name="tick">The DV frame-clock tick.</param>
        public void Advance(int tick)
        {
            SceneTime time = Context(tick).Time;
            _layer.Advance(in time, Scene2DFrame.Empty);
        }

        /// <summary>
        ///     One advance + draw onto a caller-owned canvas: the shape a timing or allocation window
        ///     needs, where creating a surface per frame would be most of what got measured.
        /// </summary>
        /// <param name="canvas">The destination canvas.</param>
        /// <param name="ctx">The pane to draw.</param>
        public void Frame(SKCanvas canvas, in SceneRenderContext ctx)
        {
            SceneTime time = ctx.Time;
            _layer.Advance(in time, Scene2DFrame.Empty);
            _layer.Render(canvas, ctx);
        }

        /// <summary>Advances to a tick and returns the pane's pixels.</summary>
        /// <param name="tick">The DV frame-clock tick.</param>
        public SKColor[] Render(int tick)
        {
            SceneRenderContext ctx = Context(tick);

            using CpuSurfaceProvider provider = new();
            using SKSurface surface = provider.CreateSurface(_size);
            surface.Canvas.Clear(ScenePalette.Dark.Background);

            SceneTime time = ctx.Time;
            _layer.Advance(in time, Scene2DFrame.Empty);
            _layer.Render(surface.Canvas, ctx);

            using SKImage image = surface.Snapshot();
            using SKBitmap bitmap = SKBitmap.FromImage(image);
            return bitmap.Pixels;
        }
    }
}
