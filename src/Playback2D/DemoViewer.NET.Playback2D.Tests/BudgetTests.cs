#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
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
