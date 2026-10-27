#region

using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Hud;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
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
///         Everything reusable is in Pipeline: the session, the sinks, the ffmpeg ladder, the frame
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
    ///         export pane: <c>FfmpegAcquisition</c> asks for consent only after the transfer, so it can
    ///         never be something a background job does on a user's behalf. The refusal now points at the
    ///         pane instead of promising to act.
    ///     </para>
    /// </summary>
    public const string NoFfmpegRefusal =
        "No ffmpeg was found, so only GIF can be exported. Install ffmpeg — or use the export pane's " +
        "Download button where one is offered — then press Re-check, or switch the format to GIF.";

    private readonly ExportEncoding _encoding;
    private readonly string _noSetupRefusal;
    private readonly Func<Scene2DExportRequest, ExportSceneSetup?> _setup;
    private readonly Func<IRenderSurfaceProvider> _surfaces;

    /// <summary>Creates the runner.</summary>
    /// <param name="setup">Captures the live tab's state for one export. Null means "no demo loaded".</param>
    /// <param name="surfaces">Builds the render surface provider. Defaults to the CPU rasteriser.</param>
    /// <param name="managedFfmpegDirectory">
    ///     Where an app-managed ffmpeg lives. Defaults to <see cref="FfmpegDependency.ManagedDirectory" />,
    ///     which is also where <c>CsvgWebHost</c> looks: one download serves reels and exports alike.
    /// </param>
    /// <param name="log">Optional line sink; the chosen encoder and ffmpeg's stderr flow through it.</param>
    /// <param name="encoderProbe">
    ///     How <c>EncoderLadder</c> rungs are verified. Defaults to
    ///     <c>EncoderProbeCache.Shared</c>, so an app session pays for one two-frame test encode per
    ///     encoder rather than one per export. The seam is here so a test can drive the fallback path
    ///     without a GPU, a driver or a subprocess.
    /// </param>
    /// <param name="locateFfmpeg">
    ///     Finds ffmpeg given the managed directory; <c>FfmpegLocator.Locate</c> when omitted. The seam is
    ///     what lets a test take the managed GIF floor on a machine that has ffmpeg on PATH, and the floor
    ///     and the ffmpeg encoder do not produce the same bytes.
    /// </param>
    /// <param name="noSetupRefusal">
    ///     The refusal when <paramref name="setup" /> returns null. A strat export refuses with its own
    ///     copy: there is no demo in that sentence to be unloaded.
    /// </param>
    public SceneExportRunner(
        Func<Scene2DExportRequest, ExportSceneSetup?> setup,
        Func<IRenderSurfaceProvider>? surfaces = null,
        Func<string?>? managedFfmpegDirectory = null,
        Action<string>? log = null,
        IEncoderProbe? encoderProbe = null,
        Func<string?, FfmpegLocation>? locateFfmpeg = null,
        string? noSetupRefusal = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _surfaces = surfaces ?? (static () => new CpuSurfaceProvider());
        _noSetupRefusal = noSetupRefusal ?? "There is no loaded demo to export.";
        _encoding = new ExportEncoding(managedFfmpegDirectory ?? (static () => FfmpegDependency.ManagedDirectory),
            locateFfmpeg ?? FfmpegLocator.Locate, log, new EncoderSelector(encoderProbe));
    }

    /// <inheritdoc />
    public async Task RunAsync(Scene2DExportRequest request, IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExportSceneSetup setup = _setup(request) ?? throw new ExportRefusedException(_noSetupRefusal);

        // Setup may already hold the run's map art, so every exit from here on has to release it.
        try
        {
            // BEFORE the replay: the ladder walk spawns one short ffmpeg per hardware rung, and a refusal
            // ("you asked for h264_nvenc and this driver cannot run it") has to arrive before the export
            // spends a minute seeking rather than after it spends ten encoding into a pipe.
            (FfmpegLocation ffmpeg, EncoderSelection? encoder) = _encoding.Resolve(request, ct);

            IFrameSink sink = _encoding.BuildSink(request, request.Core, ffmpeg, encoder);
            using IRenderSurfaceProvider surfaces = _surfaces();
            await RenderSceneAsync(request, setup, sink, surfaces, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            setup.Owned?.Dispose();
        }
    }

    /// <summary>
    ///     Renders one demo range into a sink the caller built: the source, the HUD, the compositor and the
    ///     session, everything <see cref="RunAsync" /> does after the encoder is chosen. Pack Export calls it
    ///     once per clip with the pack's one sink behind a <c>PackSegmentSink</c>, so many clips from many
    ///     demos land in one encode.
    /// </summary>
    /// <param name="request">The range and the output settings; <c>Core</c>'s frame range is re-stamped here.</param>
    /// <param name="setup">The scene captured for this run.</param>
    /// <param name="sink">Where frames go. <b>Disposed by the session</b>, as every export sink is.</param>
    /// <param name="surfaces">The render surface provider. Owned by the caller.</param>
    /// <param name="progress">Progress reports, or null.</param>
    /// <param name="ct">Cancels the render.</param>
    /// <param name="maxFrames">Renders at most this many frames from the start of the range, or all of it when null.</param>
    internal static async Task RenderSceneAsync(Scene2DExportRequest request, ExportSceneSetup setup, IFrameSink sink,
        IRenderSurfaceProvider surfaces, IProgress<ExportProgress>? progress, CancellationToken ct,
        int? maxFrames = null)
    {
        ISceneFrameSource source;
        IHudDataSource? hud;
        if (setup.Source is { } frames)
        {
            source = new SceneFramesSource(frames());
            hud = setup.SourceHud;
        }
        else
        {
            // The HUD is built after the source because its clock reads the source's own last-built
            // frame: the whole point of the factory (see ExportSceneSetup.Hud).
            TrackerFrameSource tracker = BuildSource(request, setup);
            source = tracker;
            hud = setup.Hud?.Invoke(tracker);
        }

        try
        {
            int last = Math.Max(0, source.FrameCount - 1);
            if (maxFrames is int cap)
            {
                last = Math.Min(last, Math.Max(0, cap - 1));
            }

            ExportRequest core = request.Core with
            {
                // Re-stamped from the source itself: the dialog sized the range with the source's own
                // OutputFrameCount, and this is the assertion that the two agree.
                StartFrame = 0,
                EndFrame = last
            };

            using SceneCompositor compositor = BuildCompositor(core, setup, hud);
            setup.ConfigureLayers?.Invoke(compositor);

            SceneExportSession session = new(compositor)
            {
                Palette = setup.Palette,
                DisplayMode = setup.DisplayMode,
                AuthoritativeFloors = setup.MapAssets?.Floors,
                RadarBinder = setup.MapAssets?.CreateRadarBinder()
            };

            await session.RunAsync(core, source, sink, surfaces, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            // TrackerFrameSource holds a private EntityTracker; a caller-supplied source may hold nothing.
            (source as IDisposable)?.Dispose();
        }
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
            Radars = setup.MapAssets?.DescribeRadars()
        };

        return source;
    }

    private static SceneCompositor BuildCompositor(ExportRequest core, ExportSceneSetup setup,
        IHudDataSource? hud)
    {
        // Empty LayerIds means "the scene, nothing opt-in": CreateSceneStack's own null-include behaviour.
        IReadOnlyList<string>? include = core.LayerIds.Count == 0 ? null : [.. core.LayerIds];
        return SceneLayerCatalog.CreateSceneStack(include, null, setup.Vision, hud, setup.Annotations);
    }
}
