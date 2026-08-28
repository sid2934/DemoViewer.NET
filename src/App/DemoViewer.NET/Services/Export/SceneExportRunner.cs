#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Services.Dependencies;

#endregion

namespace DemoViewer.NET.Services.Export;

/// <summary>
///     The production <see cref="IExportRunner" />: resolves an encoder, builds the export's private
///     scene, and hands both to <c>SceneExportSession</c>.
///     <para>
///         Everything reusable is in Pipeline — the session, the sinks, the ffmpeg ladder, the frame
///         source. What is here is the App's own three decisions: which rung of the ladder to take, what
///         the export's layer stack contains, and where its map art comes from.
///     </para>
///     <para>
///         <b>Private everything.</b> A private <c>EntityTracker</c> (via <c>TrackerFrameSource</c>), a
///         private compositor, a private surface. The app's playing clock, its tracker and its window's
///         layer stack are never touched, which is what lets a user keep watching the demo while it
///         renders.
///     </para>
/// </summary>
public sealed class SceneExportRunner : IExportRunner
{
    /// <summary>
    ///     The message shown when a video format was asked for and no ffmpeg exists.
    ///     <para>
    ///         It used to end "…or let DemoViewer download the LGPL build", advertising in a refusal the
    ///         exact rung that had just silently not happened: acquiring ffmpeg was an optional constructor
    ///         parameter the one production caller omitted. Acquisition is now a foreground action in the
    ///         export pane — <c>FfmpegAcquisition</c> asks for consent only after the transfer, so it can
    ///         never be something a background job does on a user's behalf. The refusal now points at the
    ///         pane instead of promising to act.
    ///     </para>
    /// </summary>
    public const string NoFfmpegRefusal =
        "No ffmpeg was found, so only GIF can be exported. Install ffmpeg — or use the export pane's " +
        "Download button where one is offered — then press Re-check, or switch the format to GIF.";

    private readonly EncoderSelector _encoders;
    private readonly Action<string>? _log;
    private readonly Func<string?> _managedFfmpegDirectory;
    private readonly Func<Scene2DExportRequest, ExportSceneSetup?> _setup;
    private readonly Func<IRenderSurfaceProvider> _surfaces;

    /// <summary>Creates the runner.</summary>
    /// <param name="setup">Captures the live tab's state for one export. Null means "no demo loaded".</param>
    /// <param name="surfaces">Builds the render surface provider. Defaults to the CPU rasteriser.</param>
    /// <param name="managedFfmpegDirectory">
    ///     Where an app-managed ffmpeg lives. Defaults to <see cref="FfmpegDependency.ManagedDirectory" />,
    ///     which is also where <c>CsvgWebHost</c> looks — one download serves reels and exports alike.
    /// </param>
    /// <param name="log">Optional line sink; the chosen encoder and ffmpeg's stderr flow through it.</param>
    /// <param name="encoderProbe">
    ///     How <c>EncoderLadder</c> rungs are verified (plan P2 D1). Defaults to
    ///     <c>EncoderProbeCache.Shared</c>, so an app session pays for one two-frame test encode per
    ///     encoder rather than one per export. The seam is here so a test can drive the fallback path
    ///     without a GPU, a driver or a subprocess.
    /// </param>
    public SceneExportRunner(
        Func<Scene2DExportRequest, ExportSceneSetup?> setup,
        Func<IRenderSurfaceProvider>? surfaces = null,
        Func<string?>? managedFfmpegDirectory = null,
        Action<string>? log = null,
        IEncoderProbe? encoderProbe = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _surfaces = surfaces ?? (static () => new CpuSurfaceProvider());
        _managedFfmpegDirectory = managedFfmpegDirectory ?? (static () => FfmpegDependency.ManagedDirectory);
        _log = log;
        _encoders = new EncoderSelector(encoderProbe);
    }

    /// <inheritdoc />
    public async Task RunAsync(Scene2DExportRequest request, IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExportSceneSetup setup = _setup(request)
                                 ?? throw new ExportRefusedException(
                                     "There is no loaded demo to export.");

        FfmpegLocation ffmpeg = FfmpegLocator.Locate(_managedFfmpegDirectory());
        bool gif = string.Equals(request.Core.FormatId, ExportFormats.Gif, StringComparison.Ordinal);

        if (!ffmpeg.Found && !gif)
        {
            throw new ExportRefusedException(NoFfmpegRefusal);
        }

        // BEFORE the replay: the ladder walk spawns one short ffmpeg per hardware rung, and a refusal
        // ("you asked for h264_nvenc and this driver cannot run it") has to arrive before the export
        // spends a minute seeking rather than after it spends ten encoding into a pipe (plan P2 D1).
        EncoderSelection? encoder = gif && !ffmpeg.Found
            ? null
            : _encoders.Select(request.Core.FormatId, request.EncoderOverride,
                ExportQualities.ParseOrDefault(request.Quality), ffmpeg.Directory, ct);

        if (encoder is not null)
        {
            _log?.Invoke("video encoder: " + encoder.Describe());
        }

        using TrackerFrameSource source = BuildSource(request, setup);
        ExportRequest core = request.Core with
        {
            // Re-stamped from the source itself: the dialog sized the range with
            // TrackerFrameSource.OutputFrameCount, and this is the assertion that the two agree.
            StartFrame = 0,
            EndFrame = Math.Max(0, source.FrameCount - 1)
        };

        // After BuildSource, because the HUD's clock reads the source's own last-built frame — the whole
        // point of the factory (see ExportSceneSetup.Hud).
        IHudDataSource? hud = setup.Hud?.Invoke(source);

        using SceneCompositor compositor = BuildCompositor(core, setup, hud);
        using IRenderSurfaceProvider surfaces = _surfaces();

        SceneExportSession session = new(compositor)
        {
            Palette = setup.Palette,
            DisplayMode = setup.DisplayMode,
            AuthoritativeFloors = setup.MapAssets?.Floors,
            RadarBinder = setup.MapAssets is null ? null : new MapRadarBinder(setup.MapAssets)
        };

        IFrameSink sink = BuildSink(request, core, ffmpeg, encoder);
        await session.RunAsync(core, source, sink, surfaces, progress, ct).ConfigureAwait(false);
    }

    private static TrackerFrameSource BuildSource(Scene2DExportRequest request, ExportSceneSetup setup)
    {
        SceneFrameBuilder builder = new();
        TrackerFrameSource source = new(
            setup.Frames,
            builder,
            request.DemoStartFrame,
            request.DemoEndFrame,
            request.Core.Fps,
            request.Core.Speed,
            setup.TickRate)
        {
            MapName = setup.MapName,
            Radars = setup.MapAssets is null ? null : MapAssetPipeline.DescribeRadars(setup.MapAssets)
        };

        return source;
    }

    private static SceneCompositor BuildCompositor(ExportRequest core, ExportSceneSetup setup,
        IHudDataSource? hud)
    {
        // Empty LayerIds means "the scene, nothing opt-in" — CreateSceneStack's own null-include behaviour.
        IReadOnlyList<string>? include = core.LayerIds.Count == 0 ? null : [.. core.LayerIds];
        return SceneLayerCatalog.CreateSceneStack(include, null, setup.Vision, hud, setup.Annotations);
    }

    private IFrameSink BuildSink(Scene2DExportRequest request, ExportRequest core, FfmpegLocation ffmpeg,
        EncoderSelection? encoder)
    {
        if (!ffmpeg.Found)
        {
            // The floor. Reached only for GIF — RunAsync refused the video formats above.
            return new ManagedGifSink(request.OutputPath, core.Fps);
        }

        return new FfmpegFrameSink(new FfmpegSinkOptions(
            request.OutputPath,
            core.FormatId,
            core.Size.Width,
            core.Size.Height,
            core.Fps,
            ffmpeg.Directory,
            encoder,
            Log: _log));
    }
}
