#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
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
    private readonly Func<FfmpegDownloadOffer, string, CancellationToken, Task<bool>>? _consent;
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
    /// <param name="consent">
    ///     Shows the download offer and the licence text read out of the archive. Null means the download
    ///     rung is skipped entirely, which is the correct behaviour for a headless caller.
    /// </param>
    /// <param name="log">Optional line sink; ffmpeg's stderr flows through it.</param>
    public SceneExportRunner(
        Func<Scene2DExportRequest, ExportSceneSetup?> setup,
        Func<IRenderSurfaceProvider>? surfaces = null,
        Func<string?>? managedFfmpegDirectory = null,
        Func<FfmpegDownloadOffer, string, CancellationToken, Task<bool>>? consent = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _surfaces = surfaces ?? (static () => new CpuSurfaceProvider());
        _managedFfmpegDirectory = managedFfmpegDirectory ?? (static () => FfmpegDependency.ManagedDirectory);
        _consent = consent;
        _log = log;
    }

    /// <summary>The message shown when a video format was asked for and no ffmpeg exists.</summary>
    public const string NoFfmpegRefusal =
        "No ffmpeg was found, so only GIF can be exported. Install ffmpeg (or let DemoViewer download the " +
        "LGPL build), or switch the format to GIF.";

    /// <inheritdoc />
    public async Task RunAsync(Scene2DExportRequest request, IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExportSceneSetup setup = _setup(request)
                                 ?? throw new ExportRefusedException(
                                     "There is no loaded demo to export.");

        FfmpegLocation ffmpeg = await ResolveFfmpegAsync(request, ct).ConfigureAwait(false);
        bool gif = string.Equals(request.Core.FormatId, ExportFormats.Gif, StringComparison.Ordinal);

        if (!ffmpeg.Found && !gif)
        {
            throw new ExportRefusedException(NoFfmpegRefusal);
        }

        using TrackerFrameSource source = BuildSource(request, setup);
        ExportRequest core = request.Core with
        {
            // Re-stamped from the source itself: the dialog sized the range with
            // TrackerFrameSource.OutputFrameCount, and this is the assertion that the two agree.
            StartFrame = 0,
            EndFrame = Math.Max(0, source.FrameCount - 1)
        };

        using SceneCompositor compositor = BuildCompositor(core, setup);
        using IRenderSurfaceProvider surfaces = _surfaces();

        SceneExportSession session = new(compositor)
        {
            Palette = setup.Palette,
            DisplayMode = setup.DisplayMode,
            AuthoritativeFloors = setup.MapAssets?.Floors,
            RadarBinder = setup.MapAssets is null ? null : new MapRadarBinder(setup.MapAssets)
        };

        IFrameSink sink = BuildSink(request, core, ffmpeg);
        await session.RunAsync(core, source, sink, surfaces, progress, ct).ConfigureAwait(false);
    }

    private async Task<FfmpegLocation> ResolveFfmpegAsync(Scene2DExportRequest request, CancellationToken ct)
    {
        string? managed = _managedFfmpegDirectory();
        FfmpegLocation located = FfmpegLocator.Locate(managed);

        if (located.Found || !request.AllowFfmpegDownload || _consent is null || managed is null)
        {
            return located;
        }

        if (FfmpegAcquisition.Offer(managed) is not { } offer)
        {
            // No pinned build for this OS/architecture. Not an error — the caller falls through to the
            // GIF floor, and the dialog shows install instructions instead of a Download button.
            return located;
        }

        try
        {
            return await FfmpegAcquisition.AcquireAsync(offer, _consent, null, null, ct).ConfigureAwait(false);
        }
        catch (FfmpegAcquisitionException ex)
        {
            // A 404 on the pin, a checksum mismatch, a broken network: degrade, never crash.
            _log?.Invoke($"ffmpeg download failed: {ex.Message}");
            return FfmpegLocation.NotFound;
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
            Radars = setup.MapAssets is null ? null : MapAssetPipeline.DescribeRadars(setup.MapAssets)
        };

        return source;
    }

    private static SceneCompositor BuildCompositor(ExportRequest core, ExportSceneSetup setup)
    {
        // Empty LayerIds means "the scene, no HUD" — CreateSceneStack's own null-include behaviour.
        IReadOnlyList<string>? include = core.LayerIds.Count == 0 ? null : [.. core.LayerIds];
        return SceneLayerCatalog.CreateSceneStack(include, null, setup.Vision, setup.Hud);
    }

    private IFrameSink BuildSink(Scene2DExportRequest request, ExportRequest core, FfmpegLocation ffmpeg)
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
            Log: _log));
    }
}
