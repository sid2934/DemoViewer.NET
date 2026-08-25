#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
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
///     Design §6's "WASM frame-budget smoke test (relaxed budget, CPU path)", as B5 decision D5 shapes it:
///     a <b>browser-shaped proxy on the desktop CPU path</b>, not an in-browser run. This repo has no WASM
///     test host and CI has no browser runner, so claiming automated browser coverage would be false; what
///     is real is the code path (the same <c>CpuSurfaceProvider</c> the browser head uses offscreen), the
///     browser's viewport (1280×720, not 1080p), and the budget a single-threaded runtime deserves.
///     <para>
///         The automatic half of the WASM story is completed by the <c>wasm-build</c> CI job (the head
///         still compiles and links its natives) and the manual per-release checklist in
///         <c>docs/playback2d-v2/wasm-matrix.md</c>.
///     </para>
///     <para>
///         Relaxed timings — advance p99 ≤ 4 ms, render p99 ≤ 24 ms, combined ≤ 32 ms — but the
///         <b>allocation gate stays at zero</b>: WASM is single-threaded, so a gen-0 pause is worse there,
///         not more forgivable.
///     </para>
/// </summary>
[NotInParallel]
[Category("Budget")]
public class Playback2DWasmBudgetTests
{
    // The browser's viewport, not the desktop's. A WASM canvas at 1080p is not the case worth gating.
    private static readonly SKSizeI _size = new(1280, 720);

    // B5's relaxed numbers. Deliberately NOT scaled by DV2D_BUDGET_SCALE: these are already the loose
    // lane, and scaling a loose lane produces a gate that cannot fail.
    private static readonly BudgetPolicy _wasmBudget = new(4.0, 24.0, 0);

    private const double CombinedP99CeilingMs = 32.0;

    /// <summary>
    ///     The full scene — ten players, trails, area effects, vision, two levels, plus B2's ink layer —
    ///     at a browser viewport through the CPU provider.
    /// </summary>
    [Test]
    public async Task CpuProvider_MeetsRelaxedBudget_AtBrowserViewport()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        AnnotationDocument doc = InkedDocument();
        AnnotationSession session = new(doc);
        using AnnotationLayer ink = new(session);

        using SceneStage stage = new(_size, extra: ink);
        using CpuSurfaceProvider provider = new();

        await Assert.That(provider.Backend).IsEqualTo(RenderBackend.CpuRaster)
            .Because("the browser head's only offscreen path is the CPU rasteriser (design §8)");

        ScenePipelineBenchmark benchmark = new(stage.Compositor, provider, new StackedLayout(),
            ScenePalette.Dark)
        {
            Id = "wasm-" + SyntheticScenes.FullSceneBudgetName,
            AuthoritativeFloors = SyntheticScenes.BudgetFloors
        };

        BenchmarkReport report = benchmark.Run(new FixtureFrameSource(fixture),
            new BenchmarkRequest(512, _size));

        double combined = report.Advance.P99Ms + report.Render.P99Ms;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[wasm-budget] {_size.Width}x{_size.Height} CpuRaster: advance p50={report.Advance.P50Ms:F3} " +
            $"p99={report.Advance.P99Ms:F3} ms (≤{_wasmBudget.AdvanceP99Ms:F1}); " +
            $"render p50={report.Render.P50Ms:F3} p99={report.Render.P99Ms:F3} ms " +
            $"(≤{_wasmBudget.RenderP99Ms:F1}); combined p99={combined:F3} ms " +
            $"(≤{CombinedP99CeilingMs:F1}); alloc {report.AllocatedBytesPerFrame} B/frame"));

        IReadOnlyList<string> violations = _wasmBudget.Violations(report);
        foreach (string violation in violations)
        {
            Console.WriteLine($"[wasm-budget] VIOLATION {violation}");
        }

        await Assert.That(violations).IsEmpty();
        await Assert.That(combined).IsLessThanOrEqualTo(CombinedP99CeilingMs);
    }

    /// <summary>
    ///     Zero steady-state allocation, measured the same way the desktop lane measures it: two identical
    ///     windows, the SECOND asserted on, so the runtime tiering the loop body on its first pass is not
    ///     charged to the scene.
    /// </summary>
    [Test]
    public async Task SteadyState_AllocatesZeroBytes()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        AnnotationDocument doc = InkedDocument();
        AnnotationSession session = new(doc);
        using AnnotationLayer ink = new(session);

        using SceneStage stage = new(_size, extra: ink);
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

        long warm = MeasureWindow(stage, fixture, time);
        long steady = MeasureWindow(stage, fixture, time);

        Console.WriteLine($"[wasm-budget] 256 frames at {_size.Width}x{_size.Height}: " +
                          $"warm window {warm} B, steady window {steady} B");

        await Assert.That(steady).IsEqualTo(0)
            .Because("WASM is single-threaded — a gen-0 pause there is worse, not more forgivable");
    }

    /// <summary>
    ///     The browser short-circuit, driven through the factory's injectable platform seam: the CPU
    ///     answer is reached BEFORE any GPU attempt, so a browser never pays for a probe that cannot
    ///     succeed and never touches an EGL entry point that does not exist there.
    /// </summary>
    [Test]
    public async Task ProviderFactory_ReturnsCpu_WhenBrowser_WithoutProbingTheGpu()
    {
        int gpuAttempts = 0;

        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.ProbeCore(
            ProbeHostPlatform.Browser,
            RenderBackendPreference.Auto,
            _ =>
            {
                gpuAttempts++;
                return new GpuProbeResult(true, RenderBackend.Angle, "angle-d3d11", "fake", "fake", "fake");
            },
            TimeProvider.System);

        await Assert.That(probe.Backend).IsEqualTo(RenderBackend.CpuRaster);
        await Assert.That(probe.GpuAvailable).IsFalse();
        await Assert.That(probe.Reason).IsEqualTo("browser");
        await Assert.That(gpuAttempts).IsEqualTo(0)
            .Because("the short-circuit must come first — a browser has no EGL to bind to");
    }

    private static long MeasureWindow(SceneStage stage, SceneFixture fixture, SceneTime time)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            SceneTime frameTime = time with
            {
                DeltaSeconds = 1.0 / 64 + i % 7 * 1e-6
            };
            stage.Renderer.Advance(fixture.Frame, in frameTime);
            stage.Renderer.Render();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Dry ink on both of the budget fixture's Z bands, so the layer's level filter and its dry picture
    // are both doing real work rather than early-outing on an empty document.
    private static AnnotationDocument InkedDocument()
    {
        AnnotationDocument doc = new();
        for (int i = 0; i < 8; i++)
        {
            float zMin = i % 2 == 0 ? -700f : -100f;
            doc.Apply(new DocDelta.Add(
                AnnotationFakes.Stroke(new SpaceRef.World(zMin), default, -900f + i * 200f, -400f + i * 90f),
                i));
        }

        return doc;
    }
}
