#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Benchmarking;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The design §6 budget: a 64 fps floor at 1× means 15.6 ms per frame, split ≤2 ms of advance and
///     ≤8 ms of draw at 1080p, with <b>zero</b> steady-state allocation.
///     <para>
///         Both cases run the worst-case <c>full-scene-budget</c> fixture: every layer carrying real
///         work at once, over two levels, at 1080p. Timing gates on p99 against
///         <see cref="BudgetPolicy.Ci" /> (baseline × <c>DV2D_BUDGET_SCALE</c>, default 2.0) because a
///         hosted runner is not the design's mid-tier laptop and a gate that fires on runner noise gets
///         muted within a week. The allocation gate is <b>not</b> scaled: zero is zero on every machine,
///         and it is the assertion that carries most of the regression-catching value.
///     </para>
/// </summary>
[NotInParallel]
[Category("Budget")]
public class BudgetTests
{
    private static readonly SKSizeI _size = new(1920, 1080);

    /// <summary>
    ///     512 frames after a 64-frame warmup must allocate exactly nothing. Every item on the plan's
    ///     T15 list is an allocation this would catch: a <c>FormattedText</c> per marker, a
    ///     <c>Pen</c> per trail, a <c>StreamGeometry</c> per cone, a run list per pane, a closure per
    ///     call, a LINQ chain per band.
    /// </summary>
    [Test]
    public async Task FullScene_SteadyState_AllocatesNothing()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        using SceneStage stage = new(_size);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);
        stage.Renderer.AdvanceCameras = true;

        SceneTime time = fixture.Time;
        for (int i = 0; i < 64; i++)
        {
            stage.Renderer.Advance(fixture.Frame, in time);
            if (i == 0)
            {
                stage.Renderer.FitAll(fixture.Frame);
            }

            stage.Renderer.Render();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // TWO identical windows, and the SECOND is the one asserted on. The first reliably shows a
        // single 48-byte allocation at a varying iteration somewhere past ~150 — it appears whatever
        // the layer stack draws, vanishes entirely when nothing draws, happens with no gen-0 collection
        // in sight, and never recurs. That is the runtime tiering the loop body, not the scene
        // allocating: charging it to the budget would either make the gate flaky or force the budget
        // above zero, and zero is the assertion worth having.
        long first = MeasureWindow(stage, fixture, time);
        long steady = MeasureWindow(stage, fixture, time);

        Console.WriteLine($"[budget] 512 full-scene frames at {_size.Width}x{_size.Height}: " +
                          $"warm window {first} B, steady window {steady} B " +
                          $"({steady / 512.0:F2} B/frame)");

        if (steady != 0)
        {
            Console.WriteLine("[budget] per-layer breakdown:");
            foreach ((string id, long bytes) in PerLayerAllocation(fixture))
            {
                Console.WriteLine($"    {id,-28} {bytes,8} B over 128 frames");
            }
        }

        await Assert.That(steady).IsEqualTo(0);
    }

    /// <summary>
    ///     The same 512 frames over the same fixture, with <b>no baked floor bands</b> — the branch every
    ///     user without a map asset is on, and the one this gate has never measured.
    ///     <para>
    ///         <see cref="FullScene_SteadyState_AllocatesNothing" /> calls
    ///         <c>SetAuthoritativeFloors</c> first, which makes <c>FloorSplitter.Slices</c> hand back the
    ///         bundle's own list and short-circuit the histogram entirely. Everything §6's zero was
    ///         proving was therefore proved on the short-circuit: on the histogram path each observed
    ///         marker marked the split dirty and the next read rebuilt it in full, at a measured
    ///         552 B/frame, for the whole demo (D6 finding 24). A one-line difference from the case
    ///         above, because that is exactly how much of the gate was missing.
    ///     </para>
    /// </summary>
    [Test]
    public async Task FullScene_HistogramFloors_SteadyState_AllocatesNothing()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        using SceneStage stage = new(_size);
        stage.Renderer.AdvanceCameras = true;

        SceneTime time = fixture.Time;
        for (int i = 0; i < 64; i++)
        {
            stage.Renderer.Advance(fixture.Frame, in time);
            if (i == 0)
            {
                stage.Renderer.FitAll(fixture.Frame);
            }

            stage.Renderer.Render();
        }

        // The fixture's markers sit on two Z bands 500 units apart, so the density-valley heuristic finds
        // the same two floors the bundle would have declared. If it ever found one, this would be
        // measuring a single-pane scene against a two-pane budget.
        await Assert.That(stage.Renderer.Levels.Space.Levels.Count).IsEqualTo(2);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long first = MeasureWindow(stage, fixture, time);
        long steady = MeasureWindow(stage, fixture, time);

        Console.WriteLine($"[budget] 512 full-scene frames on the HISTOGRAM path at " +
                          $"{_size.Width}x{_size.Height}: warm window {first} B, steady window " +
                          $"{steady} B ({steady / 512.0:F2} B/frame)");

        await Assert.That(steady).IsEqualTo(0);
    }

    private static long MeasureWindow(SceneStage stage, SceneFixture fixture, SceneTime time)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            // The animation-frame timestamp jitters in reality, so a varying dt is the honest steady
            // state — and it also stops MarkerSmoother.AdvanceOnce from de-duplicating the loop away.
            SceneTime frameTime = time with
            {
                DeltaSeconds = 1.0 / 64 + i % 7 * 1e-6
            };
            stage.Renderer.Advance(fixture.Frame, in frameTime);
            stage.Renderer.Render();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task FullScene_FrameTimes_AreWithinBudget()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        using SceneStage stage = new(_size);
        using CpuSurfaceProvider provider = new();

        ScenePipelineBenchmark benchmark = new(stage.Compositor, provider, new StackedLayout(),
            ScenePalette.Dark)
        {
            Id = SyntheticScenes.FullSceneBudgetName,
            AuthoritativeFloors = SyntheticScenes.BudgetFloors
        };

        BenchmarkReport report = benchmark.Run(new FixtureFrameSource(fixture),
            new BenchmarkRequest(256, _size));

        BudgetPolicy policy = BudgetPolicy.Ci;
        IReadOnlyList<string> violations = policy.Violations(report);

        // The strict design §6 numbers are reported alongside, never gated on here: this suite runs on
        // whatever machine happens to have it, and the CI scale exists precisely so the gate is not a
        // referendum on the runner. A local run that breaks BASELINE while staying inside CI is the
        // signal to go looking before it becomes a CI failure.
        foreach (string strict in BudgetPolicy.Baseline.Violations(report))
        {
            Console.WriteLine($"[budget] over the design baseline (not gated): {strict}");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[budget] advance p50={report.Advance.P50Ms:F3} p99={report.Advance.P99Ms:F3} ms " +
            $"(budget {policy.AdvanceP99Ms:F3})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[budget] render  p50={report.Render.P50Ms:F3} p99={report.Render.P99Ms:F3} ms " +
            $"(budget {policy.RenderP99Ms:F3})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[budget] total   p99={report.Total.P99Ms:F3} ms, frame floor 15.625 ms at 64 fps"));
        Console.WriteLine($"[budget] allocation {report.AllocatedBytesPerFrame} B/frame");

        string written = report.WriteToBenchReports(BenchReportDirectory());
        Console.WriteLine($"[budget] report {written}");

        foreach (string violation in violations)
        {
            Console.WriteLine($"[budget] VIOLATION {violation}");
        }

        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     <b>The 8 ms render budget had never seen ink</b> (plan D7 §7).
    ///     <para>
    ///         Every other case here builds <see cref="SceneStage" /> with no <c>extra</c> layers, and the
    ///         stage's fixed seven cannot include the annotation layer because it takes a session — so it
    ///         only ever arrives through <c>extra</c>, and no timing gate passed it one.
    ///         <c>AnnotationLayerTests.SteadyState_ZeroAllocations</c> measures allocation only, and did
    ///         it on THREE-sample strokes. The result was that the whole ink subsystem sat outside the
    ///         frame-time budget while looking covered.
    ///     </para>
    ///     <para>
    ///         The document below is the shape a real telestration session produces: hundreds of samples
    ///         per stroke, cached ink on both floors, a fade and a tracked callout, and two
    ///         <b>mid-replay real-time strokes</b> — the worst content this layer has, because a real-time
    ///         stroke is re-sectioned and re-outlined on every frame and is cached by nothing. The run is
    ///         reported twice, ink off and ink on, because "we are inside budget" is much less useful
    ///         than "the ink costs this much of it".
    ///     </para>
    /// </summary>
    [Test]
    public async Task FullScene_WithRealStrokeInk_FrameTimesAreWithinBudget()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        AnnotationDocument document = new();
        InkFixture(document, fixture.Time.Tick);

        AnnotationLayer ink = new(new AnnotationSession(document));
        using SceneStage stage = new(_size, extra: ink);
        using CpuSurfaceProvider provider = new();

        ScenePipelineBenchmark benchmark = new(stage.Compositor, provider, new StackedLayout(),
            ScenePalette.Dark)
        {
            Id = SyntheticScenes.FullSceneBudgetName + "-ink",
            AuthoritativeFloors = SyntheticScenes.BudgetFloors
        };

        FixtureFrameSource source = new(fixture);
        BenchmarkRequest request = new(256, _size);

        ink.IsEnabled = false;
        BenchmarkReport without = benchmark.Run(source, request);
        ink.IsEnabled = true;
        BenchmarkReport with = benchmark.Run(source, request);

        // A fixture whose ink was culled — wrong floor anchor, closed envelope, dead player — would
        // report a delta of zero and pass for entirely the wrong reason.
        await Assert.That(ink.DryPictureCount).IsEqualTo(2)
            .Because("cached ink on both floors, or the dry half of the layer is not being measured");
        await Assert.That(ink.PreparedCount).IsEqualTo(6)
            .Because("three fades, one tracked callout and two real-time strokes have to be LIVE");

        BudgetPolicy policy = BudgetPolicy.Ci;
        IReadOnlyList<string> violations = policy.Violations(with);

        foreach (string strict in BudgetPolicy.Baseline.Violations(with))
        {
            Console.WriteLine($"[budget] over the design baseline (not gated): {strict}");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[budget-ink] render  p50={with.Render.P50Ms:F3} p99={with.Render.P99Ms:F3} ms " +
            $"(baseline {BudgetPolicy.Baseline.RenderP99Ms:F3}, ci {policy.RenderP99Ms:F3})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[budget-ink] no ink  p50={without.Render.P50Ms:F3} p99={without.Render.P99Ms:F3} ms " +
            $"→ the ink costs p50 {(with.Render.P50Ms - without.Render.P50Ms) * 1000:F0} µs, " +
            $"p99 {(with.Render.P99Ms - without.Render.P99Ms) * 1000:F0} µs per frame"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[budget-ink] advance p50={with.Advance.P50Ms:F3} p99={with.Advance.P99Ms:F3} ms " +
            $"(budget {policy.AdvanceP99Ms:F3})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[budget-ink] total   p99={with.Total.P99Ms:F3} ms, frame floor 15.625 ms at 64 fps"));
        Console.WriteLine($"[budget-ink] allocation {with.AllocatedBytesPerFrame} B/frame " +
                          $"({without.AllocatedBytesPerFrame} B/frame with the ink layer off)");

        Console.WriteLine($"[budget-ink] report {with.WriteToBenchReports(BenchReportDirectory())}");

        foreach (string violation in violations)
        {
            Console.WriteLine($"[budget-ink] VIOLATION {violation}");
        }

        await Assert.That(violations).IsEmpty();
    }

    // The ink a real session leaves behind, at the budget fixture's tick. Sample counts are in the
    // hundreds deliberately: the outliner is O(n) in them and a three-point stub measures the call
    // overhead rather than the work.
    private static void InkFixture(AnnotationDocument document, int tick)
    {
        double lower = MapSpace.QuantizeZ(SyntheticScenes.BudgetFloors[0].MinZ);
        double upper = MapSpace.QuantizeZ(SyntheticScenes.BudgetFloors[1].MinZ);

        // Six always-on strokes, three per floor: the cached WORLD pictures, recorded once and replayed
        // under each pane's camera.
        for (int i = 0; i < 6; i++)
        {
            document.Apply(new DocDelta.Add(
                new AnnotationElement(Guid.NewGuid(), AnnotationKind.Freehand, InkStyle(6f),
                    new SpaceRef.World(i % 2 == 0 ? lower : upper), TimeEnvelope.Static,
                    Squiggle(320, -1700f + i * 120f, -900f + i * 240f), null),
                document.Elements.Count));
        }

        // Three mid-fade strokes: outlined every frame, but at one alpha and one path.
        for (int i = 0; i < 3; i++)
        {
            document.Apply(new DocDelta.Add(
                new AnnotationElement(Guid.NewGuid(), AnnotationKind.Freehand, InkStyle(8f),
                    new SpaceRef.World(i % 2 == 0 ? lower : upper),
                    new TimeEnvelope(tick - 200, tick + 200, 32, 32),
                    Squiggle(280, -1500f + i * 500f, 200f + i * 180f), null),
                document.Elements.Count));
        }

        // One tracked callout on a live player — slot 3's SteamId, on the upper band.
        document.Apply(new DocDelta.Add(
            new AnnotationElement(Guid.NewGuid(), AnnotationKind.Freehand, InkStyle(8f),
                new SpaceRef.Entity(76561190000000003, 40f, 40f), TimeEnvelope.Static,
                Squiggle(240, 0f, 0f), null),
            document.Elements.Count));

        // Two real-time strokes, both 200 ticks into a 260-tick replay with a 64-tick hold: a live head,
        // a full-alpha body and every one of the eight tail bands, on every frame, cached by nothing.
        for (int i = 0; i < 2; i++)
        {
            StrokeTiming cadence = i == 0
                ? RealTimeFakes.Steady(400, 260)
                : RealTimeFakes.WithPause(400, 100, 60);

            document.Apply(new DocDelta.Add(
                new AnnotationElement(Guid.NewGuid(), AnnotationKind.Freehand, InkStyle(10f),
                    new SpaceRef.World(i == 0 ? lower : upper),
                    new TimeEnvelope(tick - 200, tick - 136, 0, 48),
                    Squiggle(400, -1800f, -400f + i * 700f), null, cadence),
                document.Elements.Count));
        }
    }

    private static AnnotationStyle InkStyle(float width) =>
        AnnotationStyle.Default with
        {
            WidthWorld = width
        };

    // A doubling-back arc across the map, which is the case a gradient shader gets wrong and the case a
    // straight line would let through: the outliner emits its 13-point corner sweeps here.
    private static InkPoint[] Squiggle(int count, float x, float y)
    {
        InkPoint[] points = new InkPoint[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1);
            points[i] = new InkPoint(
                x + (float)(t * 2400.0 + Math.Sin(t * Math.PI * 6) * 90.0),
                y + (float)(Math.Sin(t * Math.PI * 2.5) * 260.0),
                0.5f);
        }

        return points;
    }

    // Which layer allocates, when the zero assertion fails. Printed rather than asserted: the failure
    // has already happened and what the reader needs next is the culprit's name, not another red line.
    private static List<(string Id, long Bytes)> PerLayerAllocation(SceneFixture fixture)
    {
        List<(string Id, long Bytes)> results = [];
        using SceneStage probe = new(_size);
        probe.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        IReadOnlyList<ISceneLayer> layers = probe.Compositor.Layers;
        string[] ids = [.. layers.Select(l => l.Id)];

        foreach (string id in ids)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                layers[i].IsEnabled = string.Equals(layers[i].Id, id, StringComparison.Ordinal);
            }

            SceneTime time = fixture.Time;
            for (int i = 0; i < 32; i++)
            {
                probe.Renderer.Advance(fixture.Frame, in time);
                probe.Renderer.Render();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++)
            {
                SceneTime frameTime = time with
                {
                    DeltaSeconds = 1.0 / 64 + i % 7 * 1e-6
                };
                probe.Renderer.Advance(fixture.Frame, in frameTime);
                probe.Renderer.Render();
            }

            results.Add((id, GC.GetAllocatedBytesForCurrentThread() - before));
        }

        return results;
    }

    // bench-reports/ at the repo root, next to the existing convention. Falls back to the test output
    // directory when the repo is not on disk (a packaged run).
    private static string BenchReportDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return Path.Combine(dir.FullName, "bench-reports");
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "bench-reports");
    }
}
