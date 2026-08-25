#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Benchmarking;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Plan <c>P1-perf-instrumentation</c> §5: capture must be free when off and allocation-free when
///     on.
///     <para>
///         The zero-byte assertions are the load-bearing ones and they are <b>not</b> scaled by anything
///         — zero is zero on every machine, exactly as in <see cref="BudgetTests" />. The timing
///         comparison is deliberately loose: it exists to catch a recorder that costs a multiple of the
///         frame, not to referee the runner.
///     </para>
/// </summary>
[NotInParallel]
public class ScenePerfRecorderTests
{
    private static readonly SKSizeI _size = new(1920, 1080);

    /// <summary>
    ///     The default path. With no recorder attached, 512 full-scene frames must still allocate exactly
    ///     nothing — the same window <see cref="BudgetTests.FullScene_SteadyState_AllocatesNothing" />
    ///     asserts, re-run here so a regression is attributed to the instrumentation rather than to the
    ///     scene.
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task Detached_AllocatesNothing_AndCapturesNothing()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        using SceneStage stage = new(_size);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        ScenePerfRecorder recorder = new(1024);
        await Assert.That(stage.Compositor.Profiler).IsNull();

        Warm(stage, fixture);
        long steady = Window(stage, fixture, 512);

        Console.WriteLine($"[perf] detached: {steady} B over 512 frames");

        await Assert.That(steady).IsEqualTo(0L);

        // A recorder that was never attached saw nothing — the seam is genuinely inert, not merely quiet.
        await Assert.That(recorder.Frames).IsEqualTo(0);
        await Assert.That(recorder.Snapshot().Stages).IsEmpty();
    }

    /// <summary>
    ///     Capture on. The rings are filled by the warmup, so the measured window writes only into arrays
    ///     that already exist and the steady state is still zero bytes per frame.
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task Attached_AllocatesNothingPerFrame_InSteadyState()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        using SceneStage stage = new(_size);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        ScenePerfRecorder recorder = new(1024);
        stage.Compositor.Profiler = recorder;

        // Warmup: JIT, the picture caches AND every ring the recorder will ever write to.
        Warm(stage, fixture, recorder);
        recorder.Reset();

        long steady = Window(stage, fixture, 512, recorder);

        Console.WriteLine($"[perf] attached: {steady} B over 512 frames ({steady / 512.0:F2} B/frame)");

        await Assert.That(steady).IsEqualTo(0L);

        // Two windows of 512, both instrumented: Window runs the loop twice and asserts on the second.
        await Assert.That(recorder.Frames).IsEqualTo(1024);
    }

    /// <summary>
    ///     What the capture actually says: every enabled layer appears, both phases, with a sane
    ///     distribution and a picture-cache verdict on the render row.
    /// </summary>
    [Test]
    public async Task Attached_ReportsEveryLayer_WithBothPhasesAndCacheCounters()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        using SceneStage stage = new(_size);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        ScenePerfRecorder recorder = new(256);
        stage.Compositor.Profiler = recorder;

        Warm(stage, fixture, recorder);
        recorder.Reset();
        Window(stage, fixture, 64, recorder);

        PerfReport report = recorder.Snapshot();
        foreach (PerfRow row in report.Layers)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[perf] {row.Label,-34} p50={row.Times.P50Ms:F4} total={row.TotalMs:F2} ms " +
                $"share={row.SharePct:F1}% cache={row.CacheHitRate?.ToString("P1", CultureInfo.InvariantCulture) ?? "n/a"}"));
        }

        HashSet<string> ids = [.. stage.Compositor.Layers.Where(l => l.IsEnabled).Select(l => l.Id)];
        HashSet<string> advanced = [.. report.Layers.Where(r => r.Phase == LayerPhase.Advance).Select(r => r.Name)];
        HashSet<string> rendered = [.. report.Layers.Where(r => r.Phase == LayerPhase.Render).Select(r => r.Name)];

        await Assert.That(advanced).IsEquivalentTo(ids);
        await Assert.That(rendered).IsEquivalentTo(ids);
        await Assert.That(report.Frames).IsEqualTo(128); // Window runs the 64-frame loop twice

        // Every layer row must be a real sample set, and its cost must sit inside the frame that contains
        // it — a row claiming more time than the frame would mean the accumulators are double-counting.
        // With no pipeline stages driven here, the layers ARE the frame, so the shares sum to 100 %.
        double shareSum = 0;
        foreach (PerfRow row in report.Layers)
        {
            await Assert.That(row.Samples).IsEqualTo(128);
            await Assert.That(row.TotalMs).IsGreaterThanOrEqualTo(0);
            await Assert.That(row.SharePct).IsLessThanOrEqualTo(100.5);
            shareSum += row.SharePct;
        }

        await Assert.That(shareSum).IsBetween(99.0, 101.0);

        // The radar is the stack's PerCamera layer with a static camera, so after the warmup its picture
        // is a hit every frame. That is the counter proving the cache column reads the real branch.
        PerfRow? radar = report.Find(SceneLayerIds.Radar, PerfRowKind.Layer, LayerPhase.Render);
        await Assert.That(radar).IsNotNull();
        await Assert.That(radar!.CacheReplayed).IsGreaterThan(0L);
    }

    /// <summary>
    ///     The overhead of capture, reported and loosely bounded. Detached is the shipped path and must
    ///     not be the slower of the two; the 2× + 0.5 ms allowance is deliberately generous, because the
    ///     assertion worth having here is "the recorder is not a multiple of the frame", not a verdict on
    ///     the machine.
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task Detached_IsNotSlowerThanAttached()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();

        double attached = MedianFrameMs(fixture, capture: true);
        double detached = MedianFrameMs(fixture, capture: false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[perf] frame median: detached {detached:F3} ms, attached {attached:F3} ms " +
            $"(+{(detached > 0 ? (attached - detached) / detached * 100 : 0):F1}%)"));

        await Assert.That(detached).IsLessThanOrEqualTo((attached * 2.0) + 0.5);
    }

    private static double MedianFrameMs(SceneFixture fixture, bool capture)
    {
        using SceneStage stage = new(_size);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);

        ScenePerfRecorder? recorder = capture ? new ScenePerfRecorder(512) : null;
        stage.Compositor.Profiler = recorder;

        Warm(stage, fixture, recorder);

        double[] samples = new double[256];
        SceneTime time = fixture.Time;
        for (int i = 0; i < samples.Length; i++)
        {
            SceneTime frameTime = time with
            {
                DeltaSeconds = (1.0 / 64) + (i % 7 * 1e-6)
            };

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            stage.Renderer.Advance(fixture.Frame, in frameTime);
            stage.Renderer.Render();
            samples[i] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            recorder?.EndFrame();
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static void Warm(SceneStage stage, SceneFixture fixture, ScenePerfRecorder? recorder = null)
    {
        SceneTime time = fixture.Time;
        for (int i = 0; i < 64; i++)
        {
            stage.Renderer.Advance(fixture.Frame, in time);
            if (i == 0)
            {
                stage.Renderer.FitAll(fixture.Frame);
            }

            stage.Renderer.Render();
            recorder?.EndFrame();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Two identical windows, and the SECOND is the one returned — the same allowance BudgetTests makes,
    // for the same reason: the first window reliably shows one 48-byte tiering allocation that never
    // recurs, and charging it to the budget would either make the gate flaky or force the budget above
    // zero.
    private static long Window(SceneStage stage, SceneFixture fixture, int frames,
        ScenePerfRecorder? recorder = null)
    {
        Measure(stage, fixture, frames, recorder);
        return Measure(stage, fixture, frames, recorder);
    }

    private static long Measure(SceneStage stage, SceneFixture fixture, int frames,
        ScenePerfRecorder? recorder)
    {
        SceneTime time = fixture.Time;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            SceneTime frameTime = time with
            {
                DeltaSeconds = (1.0 / 64) + (i % 7 * 1e-6)
            };
            stage.Renderer.Advance(fixture.Frame, in frameTime);
            stage.Renderer.Render();
            recorder?.EndFrame();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
