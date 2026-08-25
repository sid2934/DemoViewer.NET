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
///         banned-API test forbids both — Core must contain nothing that makes a render depend on when
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
    ///         corpus entry was captured at — otherwise the two commands measure and verify different
    ///         framings of one scene, and a "bench is slower" report could just be a wider camera.
    ///     </para>
    /// </summary>
    public ViewportTransform? Camera { get; set; }

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

        // Warmup: JIT, the picture caches, the text blobs and the marker smoothing all settle here.
        // Measuring through them would report a first-frame cost as a steady-state one.
        for (int i = 0; i < request.WarmupFrames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Step(renderer, source, i, dt, fitFirstFrame: Camera is null);
        }

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
            Scene2DFrame frame = source.FrameAt(index);
            SceneTime time = source.TimeAt(index) with
            {
                DeltaSeconds = dt
            };

            clock.Restart();
            renderer.Advance(frame, in time);
            clock.Stop();
            double advance = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            renderer.Render();
            clock.Stop();
            double render = clock.Elapsed.TotalMilliseconds;

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

    private static void Step(HeadlessSceneRenderer renderer, ISceneFrameSource source, int i, double dt,
        bool fitFirstFrame)
    {
        int index = i % source.FrameCount;
        Scene2DFrame frame = source.FrameAt(index);
        SceneTime time = source.TimeAt(index) with
        {
            DeltaSeconds = dt
        };

        renderer.Advance(frame, in time);
        if (i == 0 && fitFirstFrame)
        {
            renderer.FitAll(frame);
        }

        renderer.Render();
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
