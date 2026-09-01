#region

using System.Diagnostics;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Headless;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Benchmarking;

/// <summary>
///     Times the scene pipeline against the design §6 budget: advance, render, and steady-state
///     allocation, over a frame source.
///     <para>
///         <b>It lives in Pipeline, not Core, deliberately.</b> The report stamps
///         <c>DateTimeOffset.UtcNow</c> and the loop uses a <see cref="Stopwatch" />, and Core's
///         banned-API test forbids both: Core must contain nothing that makes a render depend on when
///         it happened. Measuring from outside is not a workaround; it is the contract.
///     </para>
///     <para>
///         Rendering goes through <see cref="HeadlessSceneRenderer" />, the same entry point the goldens
///         use, so a benchmark cannot accidentally measure a cheaper loop than the one being shipped.
///     </para>
/// </summary>
public sealed class ScenePipelineBenchmark
{
    private readonly SceneCompositor _compositor;
    private readonly ILevelLayoutPolicy _layout;
    private readonly ScenePalette _palette;
    private readonly IRenderSurfaceProvider _surfaces;

    /// <summary>Creates a benchmark over a layer stack.</summary>
    /// <param name="compositor">The layers under test. Not owned.</param>
    /// <param name="surfaces">Surface backend. Not owned.</param>
    /// <param name="layout">Pane layout policy.</param>
    /// <param name="palette">Resolved colours.</param>
    public ScenePipelineBenchmark(SceneCompositor compositor, IRenderSurfaceProvider surfaces,
        ILevelLayoutPolicy layout, ScenePalette palette)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(layout);

        _compositor = compositor;
        _surfaces = surfaces;
        _layout = layout;
        _palette = palette;
    }

    /// <summary>Run identity, stamped into the report and its file name.</summary>
    public string Id { get; set; } = "scene";

    /// <summary>The binder that gives levels their radar images, when the run should draw them.</summary>
    public ILevelRadarBinder? RadarBinder { get; set; }

    /// <summary>Authoritative floor bands for the run's map, when it has them.</summary>
    public IReadOnlyList<FloorSlice>? AuthoritativeFloors { get; set; }

    /// <summary>
    ///     A camera to pin every pane to, instead of fitting the first frame's observed extent.
    ///     <para>
    ///         <c>dv2d bench</c> sets it so the benchmark draws the <b>same picture</b> the golden for that
    ///         corpus entry was captured at. Otherwise the two commands measure and verify different
    ///         framings of one scene, and a "bench is slower" report could just be a wider camera.
    ///     </para>
    /// </summary>
    public ViewportTransform? Camera { get; set; }

    /// <summary>
    ///     Optional per-layer / per-stage capture (plan <c>P1-perf-instrumentation</c>). Null (the
    ///     default) leaves the compositor's profiler seam unattached and the run byte-for-byte the same
    ///     as before this existed.
    ///     <para>
    ///         When set, the recorder is attached <b>before</b> the warmup and
    ///         <see cref="ScenePerfRecorder.Reset" /> afterwards, so its rings are allocated by warmup
    ///         frames and the measured window (the one <c>AllocatedBytesPerFrame</c> reads) writes only
    ///         into arrays that already exist. The §6 zero stays zero with capture on.
    ///     </para>
    /// </summary>
    public ScenePerfRecorder? Perf { get; set; }

    /// <summary>Runs the benchmark.</summary>
    /// <param name="source">Frames to replay; wrapped modulo its length when the request wants more.</param>
    /// <param name="request">What to measure.</param>
    /// <param name="cancellationToken">Cancels mid-run.</param>
    public BenchmarkReport Run(ISceneFrameSource source, BenchmarkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        if (source.FrameCount == 0)
        {
            throw new ArgumentException("the frame source is empty", nameof(source));
        }

        ApplyLayerFilter(request.LayerIds);

        using HeadlessSceneRenderer renderer = new(_compositor, _surfaces, _layout, _palette)
        {
            Size = request.Size,
            Purpose = RenderPurpose.Export,
            // A pinned camera is data, not a target: advancing rigs on top of it would drift the framing
            // across the measured window and make the render times depend on where the lerp got to.
            AdvanceCameras = Camera is null,
            Camera = Camera
        };
        renderer.Levels.SetAuthoritativeFloors(AuthoritativeFloors);
        renderer.Levels.RadarBinder = RadarBinder;

        double dt = 1.0 / 64 * Math.Max(0.01, request.Speed);

        ScenePerfRecorder? perf = Perf;
        _compositor.Profiler = perf;

        try
        {
            return RunCore(renderer, source, request, dt, perf, cancellationToken);
        }
        finally
        {
            // The compositor belongs to the caller (in the app it is the live window's stack), so a run
            // that left a recorder attached would go on charging every subsequent frame to a report
            // nobody is going to read.
            _compositor.Profiler = null;
        }
    }

    private BenchmarkReport RunCore(HeadlessSceneRenderer renderer, ISceneFrameSource source,
        BenchmarkRequest request, double dt, ScenePerfRecorder? perf, CancellationToken cancellationToken)
    {
        // Warmup: JIT, the picture caches, the text blobs and the marker smoothing all settle here. With
        // capture on, the recorder's rings are allocated here too. Measuring through any of it would
        // report a first-frame cost as a steady-state one.
        for (int i = 0; i < request.WarmupFrames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Step(renderer, source, i, dt, Camera is null, perf);
        }

        // Warmup samples are not the run. The rings and their allocation survive; the counters do not.
        perf?.Reset();

        double[] advanceMs = new double[request.Frames];
        double[] renderMs = new double[request.Frames];
        double[] totalMs = new double[request.Frames];

        // Collect before the measured window so a warmup-era collection is not charged to it, then
        // sample the allocation counter around the whole window.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = request.MeasureAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        Stopwatch clock = new();

        for (int i = 0; i < request.Frames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int index = (request.WarmupFrames + i) % source.FrameCount;

            // Source is its own stage, not part of advance: on a --demo run this is the entity tracker's
            // decode plus SceneFrameBuilder, and folding it into advance is exactly the conflation the
            // whole capture exists to undo.
            perf?.BeginStage(PerfStage.Source);
            Scene2DFrame frame = source.FrameAt(index);
            SceneTime time = source.TimeAt(index) with
            {
                DeltaSeconds = dt
            };
            perf?.EndStage(PerfStage.Source);

            clock.Restart();
            perf?.BeginStage(PerfStage.Advance);
            renderer.Advance(frame, in time);
            perf?.EndStage(PerfStage.Advance);
            clock.Stop();
            double advance = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            perf?.BeginStage(PerfStage.Render);
            renderer.Render();
            perf?.EndStage(PerfStage.Render);
            clock.Stop();
            double render = clock.Elapsed.TotalMilliseconds;

            perf?.EndFrame();

            advanceMs[i] = advance;
            renderMs[i] = render;
            totalMs[i] = advance + render;
        }

        long allocated = request.MeasureAllocations
            ? GC.GetAllocatedBytesForCurrentThread() - allocatedBefore
            : 0;

        return new BenchmarkReport(
            Id,
            request.Frames,
            request.Size,
            _surfaces.Backend,
            FrameTimeStats.From(advanceMs),
            FrameTimeStats.From(renderMs),
            FrameTimeStats.From(totalMs),
            request.Frames > 0 ? allocated / request.Frames : 0,
            allocated,
            DateTimeOffset.UtcNow,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before);
    }

    // The warmup runs through the SAME instrumented shape as the measured loop, deliberately: that is
    // what allocates the recorder's rings before the bytes/frame window opens.
    private static void Step(HeadlessSceneRenderer renderer, ISceneFrameSource source, int i, double dt,
        bool fitFirstFrame, ScenePerfRecorder? perf)
    {
        int index = i % source.FrameCount;

        perf?.BeginStage(PerfStage.Source);
        Scene2DFrame frame = source.FrameAt(index);
        SceneTime time = source.TimeAt(index) with
        {
            DeltaSeconds = dt
        };
        perf?.EndStage(PerfStage.Source);

        perf?.BeginStage(PerfStage.Advance);
        renderer.Advance(frame, in time);
        perf?.EndStage(PerfStage.Advance);

        if (i == 0 && fitFirstFrame)
        {
            renderer.FitAll(frame);
        }

        perf?.BeginStage(PerfStage.Render);
        renderer.Render();
        perf?.EndStage(PerfStage.Render);
        perf?.EndFrame();
    }

    private void ApplyLayerFilter(IReadOnlySet<string>? layerIds)
    {
        if (layerIds is null)
        {
            return;
        }

        IReadOnlyList<ISceneLayer> layers = _compositor.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            layers[i].IsEnabled = layerIds.Contains(layers[i].Id);
        }
    }
}
