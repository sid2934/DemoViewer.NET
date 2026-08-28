#region

using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Benchmarking;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     The export loop — design §5.7. A fixed timestep through the same layer stack the window draws,
///     into an <see cref="IRenderSurfaceProvider" />'s surface, out through an <see cref="IFrameSink" />.
///     <para>
///         <b>It draws through <c>HeadlessSceneRenderer</c>, never a private loop</b>: a two-floor Nuke
///         export shows two bands instead of one flattened pane, the same picture the window draws.
///         Every type this session takes and every type it throws is Core's.
///     </para>
///     <para>
///         <b>Zero steady-state allocation</b> (design §6): one surface, one pooled RGBA staging buffer,
///         one pinned handle per frame, and a progress struct — no per-frame bitmap, no per-frame array,
///         no LINQ. <b>The session disposes the sink</b>, exactly once, in a <c>finally</c>, on success,
///         cancellation and failure alike: that is what kills an ffmpeg subprocess on cancel, so a caller
///         must not wrap the sink in its own <c>await using</c>.
///     </para>
/// </summary>
public sealed class SceneExportSession
{
    /// <summary>The frame ceiling for a GIF. Above it, palettegen and ImageSharp both OOM.</summary>
    public const int GifMaxFrames = 1800;

    /// <summary>The width ceiling for a GIF. Wider is technically legal and practically unusable.</summary>
    public const int GifMaxWidth = 1920;

    /// <summary>fps values every video format accepts.</summary>
    private static readonly int[] _videoFps = [24, 25, 30, 50, 60, 64];

    /// <summary>
    ///     fps values GIF can express <b>exactly</b>. A GIF frame delay is an integer number of
    ///     centiseconds, so only divisors of 100 land on the requested rate; 30 and 60 would silently
    ///     become 33.3 and 50.
    /// </summary>
    private static readonly int[] _gifFps = [10, 20, 25, 50];

    private readonly SceneCompositor _compositor;

    /// <summary>Creates a session over a layer stack.</summary>
    /// <param name="compositor">The layers to draw. Owned by the caller; never disposed here.</param>
    public SceneExportSession(SceneCompositor compositor)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        _compositor = compositor;
    }

    /// <summary>Resolved colours for the export. Defaults to the dark palette.</summary>
    public ScenePalette Palette { get; set; } = ScenePalette.Dark;

    /// <summary>How levels are laid out. Defaults to the stacked bands the window shows.</summary>
    public LevelDisplayMode DisplayMode { get; set; } = LevelDisplayMode.Stacked;

    /// <summary>
    ///     The map bundle's nav-derived floor bands, when there is one. Bound exactly as
    ///     <c>Scene2DHost</c> and <c>dv2d</c> bind them: without it the export derives its levels from the
    ///     Z histogram alone, and a two-floor Nuke video would not have the bands the window shows.
    /// </summary>
    public IReadOnlyList<FloorSlice>? AuthoritativeFloors { get; set; }

    /// <summary>The binder that gives each floor band its radar image, or null with no bundle.</summary>
    public ILevelRadarBinder? RadarBinder { get; set; }

    /// <summary>
    ///     Whether the radar image is resampled once and blitted thereafter, rather than re-resampled per
    ///     frame. On for an export, and the single biggest thing between this loop and its ≥ realtime
    ///     budget — see <c>RadarLayer.CacheScaledImage</c> for the measurement and for what it costs
    ///     (a sub-pixel difference against the pre-v2 parity reference, which is why the flag is off
    ///     everywhere else). Turn it off to render an export through exactly the window's draw path.
    /// </summary>
    public bool CacheRadarResample { get; set; } = true;

    /// <summary>
    ///     Optional per-stage / per-layer capture. Null, the default, leaves the loop byte-for-byte what
    ///     it was: the compositor's profiler seam stays unattached and each stage costs one predicted
    ///     null branch.
    ///     <para>
    ///         With it set the loop is decomposed into <see cref="PerfStage.Source" /> (the tracker decode
    ///         and scene build), <see cref="PerfStage.Advance" />, <see cref="PerfStage.Render" />,
    ///         <see cref="PerfStage.Readback" /> and <see cref="PerfStage.Encode" /> — the last of which
    ///         is the time the render loop sits blocked on the sink's bounded channel, i.e. how far the
    ///         encoder is behind. That decomposition is what turns one <c>realtime_ratio</c> into an
    ///         answer.
    ///     </para>
    /// </summary>
    public ScenePerfRecorder? Perf { get; set; }

    /// <summary>
    ///     The layers that are off unless <see cref="ExportRequest.LayerIds" /> names them explicitly.
    ///     <para>
    ///         An alias, not a second list: this and <c>SceneLayerCatalog.CreateSceneStack</c> were two
    ///         hand-written pairs, and an opt-in id that reached only one of them was force-enabled here
    ///         on every export.
    ///     </para>
    /// </summary>
    public static IReadOnlySet<string> OptInLayerIds => SceneLayerIds.OptIn;

    /// <summary>
    ///     Renders <c>[StartFrame, EndFrame]</c> into <paramref name="sink" />.
    ///     <para>
    ///         Blocking and CPU-bound between awaits; call it from a background thread. It touches no
    ///         dispatcher and no shared playback clock.
    ///     </para>
    /// </summary>
    /// <param name="req">What to render. Validated first; an invalid request renders nothing.</param>
    /// <param name="src">Where frames come from.</param>
    /// <param name="sink">Where frames go. <b>Disposed by this method</b>, exactly once.</param>
    /// <param name="surfaces">Where the render surface comes from. Owned by the caller.</param>
    /// <param name="progress">Progress reports, including the terminal one. May be null.</param>
    /// <param name="ct">Cancels the run; the sink is still disposed and the partial output removed.</param>
    /// <exception cref="ExportValidationException">The request is refused before rendering.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task RunAsync(ExportRequest req, ISceneFrameSource src, IFrameSink sink,
        IRenderSurfaceProvider surfaces, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(surfaces);

        Validate(req);

        // GPU export is not supported yet; refusing it here up front is better than failing mid-export.
        //
        // The render loop awaits the sink between frames, so after the first await it resumes on
        // whatever pool thread the continuation lands on, while GpuSurfaceProvider is thread-affine and
        // throws the moment its EGL context is touched from a second thread. An unguarded run with a GPU
        // surface dies mid-export with "GpuSurfaceProvider is thread-affine: it was created on thread 2
        // and was used from thread 33" — true, but it arrives after the replay and tells the user nothing
        // they can act on.
        //
        // Supporting it needs the loop pinned to one thread, which is a redesign of this method. Until
        // then this is a refusal, up front, in the caller's own vocabulary. CLI callers default to
        // CpuRaster so it is never reached by accident; only an explicit --gpu / --backend angle gets
        // here.
        if (surfaces.Backend != RenderBackend.CpuRaster)
        {
            throw new ExportValidationException(
                $"Export cannot run on the {surfaces.Backend} surface provider yet: the render loop " +
                "crosses threads between frames and the GPU provider is bound to the thread that " +
                "created it (C2 Stage 1). Export on the CPU provider.");
        }

        if (req.EndFrame >= src.FrameCount)
        {
            throw new ExportValidationException(
                $"The request ends at source frame {req.EndFrame}, but the source has only {src.FrameCount}.");
        }

        int total = req.FrameCount;
        int width = req.Size.Width;
        int height = req.Size.Height;
        long stride = (long)width * 4;
        int bytes = checked((int)(stride * height));

        ExportClock clock = ExportClock.Start();
        int done = 0;
        Exception? failure = null;

        LayerEnabledScope layers = new(_compositor, req.LayerIds, CacheRadarResample);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bytes);
        SKSurface? surface = null;

        ScenePerfRecorder? perf = Perf;
        _compositor.Profiler = perf;

        try
        {
            Report(progress, ExportPhase.Preparing, 0, total, clock, null);

            if (src is IPreparableFrameSource preparable && preparable.NeedsPreparation)
            {
                // Plan D2: one from-zero replay to reach StartFrame, surfaced as its own phase because on
                // a full demo it is seconds long and a frozen 0/1800 would look like a hang.
                Report(progress, ExportPhase.Seeking, 0, total, clock, "replaying to the first frame");
                preparable.Prepare(ct);
            }

            ct.ThrowIfCancellationRequested();

            CameraScriptResolver resolver = new(req.Camera);
            using HeadlessSceneRenderer renderer = new(surfaces, _compositor)
            {
                Size = req.Size,
                Palette = Palette,
                DisplayMode = DisplayMode,
                Purpose = RenderPurpose.Export,
                CameraPolicy = resolver,

                // The offscreen twin of the host's one-shot fit. An export's panes are born fitted to
                // WorldBounds.Default (±3000) because Reconcile runs before any frame has been read, and
                // with AdvanceCameras off and, in both front ends, an empty default camera script,
                // NOTHING re-framed them afterwards. Every export was framed by a placeholder.
                //
                // The policy still has the last word: a user who pinned a camera or asked for "mirror
                // the live view" gets theirs applied after this, on the same frame.
                AutoFitOnFirstMapBounds = true,

                // The script owns the cameras outright; letting the rigs step them as well would be two
                // hands on the same wheel.
                AdvanceCameras = false
            };

            renderer.Levels.SetAuthoritativeFloors(AuthoritativeFloors);
            renderer.Levels.RadarBinder = RadarBinder;

            surface = surfaces.CreateSurface(req.Size);
            SKImageInfo readInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

            Report(progress, ExportPhase.Rendering, 0, total, clock, null);

            for (int i = req.StartFrame; i <= req.EndFrame; i++)
            {
                ct.ThrowIfCancellationRequested();

                // FrameAt is the tracker's decode plus SceneFrameBuilder — on a busy demo the single
                // largest thing in this loop, and invisible in the aggregate fps this method reports.
                perf?.BeginStage(PerfStage.Source);
                SceneTime time = src.TimeAt(i);
                Scene2DFrame frame = src.FrameAt(i);
                perf?.EndStage(PerfStage.Source);

                perf?.BeginStage(PerfStage.Advance);
                renderer.Advance(frame, in time);
                perf?.EndStage(PerfStage.Advance);

                perf?.BeginStage(PerfStage.Render);
                renderer.Render(surface);
                surfaces.Flush(surface);
                perf?.EndStage(PerfStage.Render);

                perf?.BeginStage(PerfStage.Readback);
                ReadInto(surface, readInfo, buffer, (int)stride);
                perf?.EndStage(PerfStage.Readback);

                // The backpressure number. The sink's channel is bounded at four with FullMode.Wait, so
                // whatever this await costs beyond a memcpy is the encoder being behind the renderer.
                perf?.BeginStage(PerfStage.Encode);
                await sink.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, bytes), width, height, ct)
                    .ConfigureAwait(false);
                perf?.EndStage(PerfStage.Encode);

                perf?.EndFrame();

                done++;
                Report(progress, ExportPhase.Rendering, done, total, clock, null);
            }

            Report(progress, ExportPhase.Finalizing, done, total, clock, null);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            surface?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
            layers.Dispose();

            // Same reason the layer scope is restored: the compositor belongs to the caller, and in the
            // app it is the live window's stack.
            _compositor.Profiler = null;
        }

        // Closing the sink is its own step rather than part of the finally above, for two reasons.
        //
        // It is where ffmpeg is drained (or killed) and where a GIF is written — so a failure HERE is an
        // export failure even when every frame rendered. Muxing happens on close: "all frames written"
        // is not "a file exists that plays", and reporting Completed would point a user at a file that
        // does not decode.
        //
        // And a throw out of a finally would escape before the terminal report was made, leaving a
        // caller that drives a progress bar off Phase with Rendering as the last thing it ever saw.
        // Exactly one terminal report, on every path, is the contract.
        try
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        ExportPhase terminal = failure switch
        {
            null => ExportPhase.Completed,
            OperationCanceledException => ExportPhase.Cancelled,
            _ => ExportPhase.Failed
        };
        Report(progress, terminal, done, total, clock, failure?.Message);

        if (failure is not null)
        {
            // Rethrown with its original stack: the caller's catch is what turns this into a Failed job
            // status, and a repackaged exception would lose where the encode actually died.
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    ///     Throws <see cref="ExportValidationException" /> when the request cannot produce a file. Called
    ///     by <see cref="RunAsync" />, by the export dialog's <c>CanStart</c>, and by <c>dv2d export</c>
    ///     — one rule set, three callers, so the CLI cannot drift from the UI.
    /// </summary>
    /// <param name="req">The request to check.</param>
    public static void Validate(ExportRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (req.StartFrame < 0)
        {
            throw new ExportValidationException("The first frame cannot be negative.");
        }

        if (req.EndFrame < req.StartFrame)
        {
            throw new ExportValidationException(
                $"The range is empty: frame {req.StartFrame} to {req.EndFrame}.");
        }

        if (req.Speed <= 0)
        {
            throw new ExportValidationException("Playback speed must be greater than zero.");
        }

        if (req.Size.Width < 2 || req.Size.Height < 2)
        {
            throw new ExportValidationException(
                $"The output size {req.Size.Width}×{req.Size.Height} is too small to encode.");
        }

        IReadOnlyList<int> allowed = SupportedFps(req.FormatId);
        if (!Contains(allowed, req.Fps))
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{req.Fps} fps is not available for {req.FormatId}. Supported: {Join(allowed)}."));
        }

        if (ExportFormats.RequiresEvenDimensions(req.FormatId) &&
            ((req.Size.Width & 1) != 0 || (req.Size.Height & 1) != 0))
        {
            // libvpx-vp9 and libx264 with -pix_fmt yuv420p subsample chroma 2×2; an odd axis is rejected
            // by ffmpeg itself, several seconds into an encode. Refuse it here instead.
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{req.FormatId} needs even width and height; {req.Size.Width}×{req.Size.Height} is odd."));
        }

        if (!string.Equals(req.FormatId, ExportFormats.Gif, StringComparison.Ordinal))
        {
            return;
        }

        if (req.FrameCount > GifMaxFrames)
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"A GIF is capped at {GifMaxFrames} frames; this range is {req.FrameCount}. " +
                $"Shorten the range, lower the frame rate, or export WebM."));
        }

        if (req.Size.Width > GifMaxWidth)
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"A GIF is capped at {GifMaxWidth} px wide; this export is {req.Size.Width}. Export WebM instead."));
        }
    }

    /// <summary>
    ///     The frame rates a format supports. GIF gets its own list (<see cref="_gifFps" />); an unknown format id gets
    ///     the video list.
    /// </summary>
    /// <param name="formatId">One of <see cref="ExportFormats" />.</param>
    public static IReadOnlyList<int> SupportedFps(string formatId) =>
        string.Equals(formatId, ExportFormats.Gif, StringComparison.Ordinal) ? _gifFps : _videoFps;

    // GCHandle rather than an unsafe block: it pins without turning AllowUnsafeBlocks on for the whole
    // assembly, and it costs no managed allocation, which the §6 budget is measured against.
    private static void ReadInto(SKSurface surface, SKImageInfo info, byte[] destination, int rowBytes)
    {
        GCHandle handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            surface.ReadPixels(info, handle.AddrOfPinnedObject(), rowBytes, 0, 0);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Report(IProgress<ExportProgress>? progress, ExportPhase phase, int done, int total,
        ExportClock clock, string? detail)
    {
        if (progress is null)
        {
            return;
        }

        TimeSpan elapsed = clock.Elapsed;
        double fps = done > 0 && elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0;

        // ETA needs two frames: one frame's throughput is dominated by JIT and the first surface touch,
        // and an ETA computed from it is a number that immediately halves.
        TimeSpan? eta = done >= 2 && fps > 0 && total > done
            ? TimeSpan.FromSeconds((total - done) / fps)
            : null;

        progress.Report(new ExportProgress(phase, done, total, fps, elapsed, eta, detail));
    }

    private static bool Contains(IReadOnlyList<int> values, int candidate)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static string Join(IReadOnlyList<int> values) =>
        string.Join(", ", values);

    /// <summary>
    ///     Applies <see cref="ExportRequest.LayerIds" /> to the shared compositor for the duration of one
    ///     run and puts every layer back afterwards.
    ///     <para>
    ///         The compositor belongs to the caller (in the app it is the live window's stack), so an
    ///         export that left the vision layer switched off would be a visible bug in the UI after the
    ///         file finished writing.
    ///     </para>
    /// </summary>
    private readonly struct LayerEnabledScope : IDisposable
    {
        private readonly SceneCompositor _compositor;
        private readonly bool[] _previous;
        private readonly RadarLayer? _radar;
        private readonly bool _radarCacheWas;

        public LayerEnabledScope(SceneCompositor compositor, IReadOnlySet<string> requested,
            bool cacheRadarResample)
        {
            _compositor = compositor;
            IReadOnlyList<ISceneLayer> layers = compositor.Layers;
            _previous = new bool[layers.Count];

            bool explicitSet = requested is { Count: > 0 };
            for (int i = 0; i < layers.Count; i++)
            {
                ISceneLayer layer = layers[i];
                _previous[i] = layer.IsEnabled;
                layer.IsEnabled = explicitSet
                    ? requested.Contains(layer.Id)
                    : layer.IsEnabled && !OptInLayerIds.Contains(layer.Id);
            }

            // Measured on assets/tour/sample-de_nuke.dem at 1920x1080: 21.4 -> 58.3 exported frames per
            // second. The radar is ONE DrawImage, but at SKFilterQuality.High of a ~2000 px bundle layer,
            // and LayerCacheHint.PerCamera caches the picture rather than its pixels — so the bicubic
            // resample was re-run for every frame of the video. Restored on dispose because the flag
            // costs pre-v2 parity (see RadarLayer.CacheScaledImage) and the caller's compositor may be
            // the window's.
            _radar = compositor.Find(SceneLayerIds.Radar) as RadarLayer;
            _radarCacheWas = _radar?.CacheScaledImage ?? false;
            if (_radar is not null)
            {
                _radar.CacheScaledImage = cacheRadarResample;
            }
        }

        public void Dispose()
        {
            IReadOnlyList<ISceneLayer> layers = _compositor.Layers;
            for (int i = 0; i < layers.Count && i < _previous.Length; i++)
            {
                layers[i].IsEnabled = _previous[i];
            }

            if (_radar is not null)
            {
                _radar.CacheScaledImage = _radarCacheWas;
            }
        }
    }
}
