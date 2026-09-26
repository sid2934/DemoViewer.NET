#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Export.Pack;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d pack</c>'s own <see cref="IPackClipRenderer" />: parses a clip's demo, loads its map
///     bundle and its saved ink, and renders it through the same Pipeline primitives
///     <c>dv2d export</c> already assembles by hand (<see cref="TrackerFrameSource" />,
///     <c>SceneLayerCatalog.CreateSceneStack</c>, <see cref="SceneExportSession" />) — never through the
///     App's own <c>PackClipRenderer</c>, which needs the heavy-job gate and the app-managed ffmpeg
///     directory a headless tool does not have. <see cref="PackPlanner" /> and <see cref="PackExporter" />
///     are the one shared plan-then-stitch policy either host runs; only this glue is per-host, exactly
///     the split <c>ExportCommand</c> already draws against the App's <c>SceneExportRunner</c>.
///     <para>
///         <b>One demo held at a time,</b> matching the App's renderer: a queue mostly keeps a demo's
///         clips together, consecutive clips from one demo share the parse, and a parse is hundreds of
///         megabytes.
///     </para>
/// </summary>
internal sealed class HeadlessPackClipRenderer : IPackClipRenderer, IDisposable
{
    private readonly AssetsRoot _assets;
    private Loaded? _current;

    /// <param name="assets">The resolved map-asset root; a clip renders with no radar art when disabled or absent.</param>
    public HeadlessPackClipRenderer(AssetsRoot assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _current?.Asset?.Dispose();
        _current = null;
    }

    /// <inheritdoc />
    public async Task RenderAsync(PackClip clip, PackSettings settings, IFrameSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sink);

        Loaded demo;
        Scene2DExportRequest request;
        try
        {
            demo = await LoadAsync(clip.Entry.DemoPath, ct).ConfigureAwait(false);
            request = PackPlanner.BuildClipRequest(clip, settings, demo.Parsed.Frames, demo.TickRate)
                      ?? throw new ExportValidationException("the clip's range lies outside its demo");
            SceneExportSession.Validate(request.Core);
        }
        catch
        {
            // The session never took the sink, so it is still this method's to close.
            await sink.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        using TrackerFrameSource source = new(demo.Parsed.Frames, new SceneFrameBuilder(),
            request.DemoStartFrame, request.DemoEndFrame, request.Core.Fps, 1.0, demo.TickRate)
        {
            MapName = demo.Parsed.MapName,
            Radars = demo.Asset is null ? null : MapAssetPipeline.DescribeRadars(demo.Asset)
        };

        // Re-stamped from the source itself, same as SceneExportRunner.RenderSceneAsync: the plan sized
        // the range with an estimate, and the built source's own frame count is the one actually rendered.
        int last = Math.Max(0, source.FrameCount - 1);
        if (clip.MaxFrames is int cap)
        {
            last = Math.Min(last, Math.Max(0, cap - 1));
        }

        ExportRequest core = request.Core with { StartFrame = 0, EndFrame = last };

        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(
            [.. core.LayerIds], null, null, null, demo.Ink);
        using IRenderSurfaceProvider surfaces = RenderSurfaceProviderFactory.CreateCpu();

        SceneExportSession session = new(compositor)
        {
            Palette = request.Palette ?? ScenePalette.Dark,
            AuthoritativeFloors = demo.Asset?.Floors,
            RadarBinder = demo.Asset is null ? null : new MapRadarBinder(demo.Asset)
        };

        await session.RunAsync(core, source, sink, surfaces, null, ct).ConfigureAwait(false);
    }

    private async Task<Loaded> LoadAsync(string path, CancellationToken ct)
    {
        if (_current is { } held && string.Equals(held.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return held;
        }

        Dispose();
        ParsedDemo parsed = DemoInput.Load(path, out _);
        int tickRate = DemoInput.TickRate(parsed);

        LoadedMapAsset? asset = _assets is { Source: not AssetsRootSource.Disabled, Path: { } root }
            ? MapAssetPipeline.TryLoad(root, parsed.MapName)
            : null;

        AnnotationSession? ink = await LoadInkAsync(path, tickRate, parsed.Frames.Count, ct).ConfigureAwait(false);
        _current = new Loaded(path, parsed, tickRate, asset, ink);
        return _current;
    }

    // The demo's own '.dvann.json', through the same store `export --annotations` reads: beside the
    // demo, or nothing (Pipeline must not reference the App's config root). A missing, foreign or
    // demo-mismatched sidecar is not an error; it just leaves the clip with no ink.
    private static async Task<AnnotationSession?> LoadInkAsync(string demoPath, int tickRate, int frameCount,
        CancellationToken ct)
    {
        ClockIdentity clock = new(ClockIdentity.DvFrameClock, tickRate > 0 ? tickRate : 64, frameCount, 0, 0);
        AnnotationLoadResult loaded = await new AnnotationStore(null).LoadAsync(demoPath, clock, ct)
            .ConfigureAwait(false);
        if (loaded.DemoMismatch || loaded.Elements.Count == 0)
        {
            return null;
        }

        AnnotationDocument document = new();
        document.Reset(loaded.Elements);
        return new AnnotationSession(document) { TicksPerSecond = clock.TickRate };
    }

    private sealed record Loaded(string Path, ParsedDemo Parsed, int TickRate, LoadedMapAsset? Asset,
        AnnotationSession? Ink);
}

/// <summary>
///     <c>dv2d pack</c>'s own <see cref="IPackEncoder" />: the same ffmpeg ladder <c>dv2d export</c>
///     walks (<see cref="EncoderSelector" />, PATH-only <see cref="FfmpegLocator" />, no app-managed
///     download), resolved once per pack so every clip and title card goes through one rung.
/// </summary>
internal sealed class HeadlessPackEncoder : IPackEncoder
{
    private readonly bool _ffmpegLog;
    private FfmpegLocation? _ffmpeg;
    private EncoderSelection? _selection;

    /// <param name="ffmpegLog">Echo ffmpeg's stderr through <see cref="ConsoleOut.Info" />.</param>
    public HeadlessPackEncoder(bool ffmpegLog)
    {
        _ffmpegLog = ffmpegLog;
    }

    /// <inheritdoc />
    public void Prepare(PackSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Pure argument validation before the ffmpeg gate, same ordering as ExportCommand: a wrong
        // --encoder must be refused with the ladder's choices even on a machine with no ffmpeg at all.
        // (GIF's --encoder is documented as ignored; ValidateRequest already passes it through.)
        EncoderSelector.ValidateRequest(settings.FormatId, settings.EncoderOverride);

        FfmpegLocation ffmpeg = FfmpegLocator.Locate(null);
        if (!ffmpeg.Found && !settings.IsGif)
        {
            throw new BackendUnavailableException(
                "no ffmpeg was found on PATH, so a pack can only be produced here as --format gif. " +
                "Install ffmpeg, or export gif.");
        }

        _selection = settings.IsGif && !ffmpeg.Found
            ? null
            : new EncoderSelector(EncoderProbeCache.Shared).Select(settings.FormatId, settings.EncoderOverride,
                ExportQualities.ParseOrDefault(settings.Quality), ffmpeg.Directory, ct);
        _ffmpeg = ffmpeg;
    }

    /// <inheritdoc />
    public IFrameSink Open(string outputPath, PackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(settings);
        if (_ffmpeg is not { } ffmpeg)
        {
            throw new InvalidOperationException("Prepare the encoder before opening a pack file.");
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return settings.IsGif && !ffmpeg.Found
            ? new ManagedGifSink(outputPath, settings.Fps)
            : new FfmpegFrameSink(new FfmpegSinkOptions(outputPath, settings.FormatId, settings.Side, settings.Side,
                settings.Fps, ffmpeg.Directory, _selection, Log: _ffmpegLog ? ConsoleOut.Info : null));
    }
}
