#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services.Dependencies;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.Export.Pack;

/// <summary>
///     The production <see cref="IPackClipRenderer" />: parses a clip's demo, loads its map bundle and its
///     saved annotations, and renders the clip through <see cref="SceneExportRunner.RenderSceneAsync" />,
///     the 2D export's own session. Private everything, as every export.
///     <para>
///         <b>One demo held at a time.</b> A queue walks clips in the reviewer's order, which mostly keeps a
///         demo's clips together; consecutive clips from one demo share the parse, and the next demo
///         replaces it. A parse is hundreds of megabytes, so holding more than one is not worth it.
///     </para>
///     <para>
///         <b>The ink</b> is the demo's saved document, the one the 2D tab loads when it opens it, read
///         here rather than taken from the live tab: a pack spans demos the tab does not have open. A
///         sidecar that belongs to another demo is left out, as the tab leaves it out.
///     </para>
/// </summary>
public sealed class PackClipRenderer : IPackClipRenderer, IDisposable
{
    private readonly AnnotationStore? _annotations;
    private readonly HeavyJobGate? _gate;
    private readonly Func<string, LoadedMapAsset?> _loadMap;
    private readonly Action<string>? _log;
    private readonly Func<string, ParsedDemo> _parse;
    private readonly Func<IRenderSurfaceProvider> _surfaces;
    private Loaded? _current;

    /// <param name="gate">The heavy-job gate; each parse takes an interactive slot, since the user asked for this.</param>
    /// <param name="annotations">Where each demo's ink is read from, or null to render without ink.</param>
    /// <param name="parse">Reads and parses a demo; <c>DemoParser.Parse</c> over the file when null.</param>
    /// <param name="loadMap">Finds a map's baked bundle; the pipeline's loader when null.</param>
    /// <param name="surfaces">Builds the render surface; the CPU rasteriser when null.</param>
    /// <param name="log">Line sink for a missing bundle or an unreadable sidecar.</param>
    public PackClipRenderer(HeavyJobGate? gate, AnnotationStore? annotations, Func<string, ParsedDemo>? parse = null,
        Func<string, LoadedMapAsset?>? loadMap = null, Func<IRenderSurfaceProvider>? surfaces = null,
        Action<string>? log = null)
    {
        _gate = gate;
        _annotations = annotations;
        _parse = parse ?? (path => DemoParser.Parse(File.ReadAllBytes(path).AsMemory()));
        _loadMap = loadMap ?? (map => MapAssetPipeline.TryLoad(map));
        _surfaces = surfaces ?? RenderSurfaceProviderFactory.CreateCpu;
        _log = log;
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

        Scene2DExportRequest request;
        ExportSceneSetup setup;
        try
        {
            Loaded demo = await LoadAsync(clip.Entry.DemoPath, ct).ConfigureAwait(false);
            request = PackPlanner.BuildClipRequest(clip, settings, demo.Parsed.Frames, demo.Parsed.TickRate)
                      ?? throw new ExportValidationException("the clip's range lies outside its demo");
            setup = new ExportSceneSetup(demo.Parsed.Frames, demo.Parsed.TickRate, demo.Parsed.MapName,
                ScenePalette.Dark, LevelDisplayMode.Stacked, null, null, demo.Asset, demo.Ink);
            SceneExportSession.Validate(request.Core);
        }
        catch
        {
            // The session never took the sink, so it is still this method's to close.
            await sink.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        using IRenderSurfaceProvider surfaces = _surfaces();
        await SceneExportRunner.RenderSceneAsync(request, setup, sink, surfaces, null, ct, clip.MaxFrames)
            .ConfigureAwait(false);
    }

    private async Task<Loaded> LoadAsync(string path, CancellationToken ct)
    {
        if (_current is { } held && string.Equals(held.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return held;
        }

        Dispose();
        ParsedDemo parsed;
        using (IDisposable? slot = _gate is null ? null : await _gate.AcquireInteractiveAsync(ct).ConfigureAwait(false))
        {
            parsed = _parse(path);
        }

        LoadedMapAsset? asset = null;
        try
        {
            asset = string.IsNullOrEmpty(parsed.MapName) ? null : _loadMap(parsed.MapName);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"pack export: no map bundle for {parsed.MapName}: {ex.Message}");
        }

        AnnotationSession? ink = await LoadInkAsync(path, parsed, ct).ConfigureAwait(false);
        _current = new Loaded(path, parsed, asset, ink);
        return _current;
    }

    private async Task<AnnotationSession?> LoadInkAsync(string path, ParsedDemo parsed, CancellationToken ct)
    {
        if (_annotations is null)
        {
            return null;
        }

        ClockIdentity clock = FrameClock.IdentityFor(parsed);
        AnnotationLoadResult result = await _annotations.LoadAsync(path, clock, ct).ConfigureAwait(false);
        if (result.DemoMismatch || result.Elements.Count == 0)
        {
            return null;
        }

        AnnotationDocument document = new();
        document.Reset(result.Elements);
        return new AnnotationSession(document) { TicksPerSecond = clock.TickRate };
    }

    private sealed record Loaded(string Path, ParsedDemo Parsed, LoadedMapAsset? Asset, AnnotationSession? Ink);
}

/// <summary>
///     The production <see cref="IPackEncoder" />: the 2D export's own ffmpeg ladder and refusal through
///     <see cref="ExportEncoding" />, resolved once per pack so every clip and card goes through one rung.
/// </summary>
public sealed class PackEncoder : IPackEncoder
{
    private readonly ExportEncoding _encoding;
    private FfmpegLocation? _ffmpeg;
    private EncoderSelection? _selection;

    /// <param name="managedFfmpegDirectory">Where an app-managed ffmpeg lives; <see cref="FfmpegDependency.ManagedDirectory" /> when null.</param>
    /// <param name="log">Line sink for the chosen encoder and ffmpeg's stderr.</param>
    /// <param name="encoderProbe">How ladder rungs are verified; the shared cache when null.</param>
    public PackEncoder(Func<string?>? managedFfmpegDirectory = null, Action<string>? log = null,
        IEncoderProbe? encoderProbe = null)
    {
        _encoding = new ExportEncoding(managedFfmpegDirectory ?? (static () => FfmpegDependency.ManagedDirectory),
            FfmpegLocator.Locate, log, new EncoderSelector(encoderProbe ?? EncoderProbeCache.Shared));
    }

    /// <inheritdoc />
    public void Prepare(PackSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        (_ffmpeg, _selection) = _encoding.Resolve(Request(settings.OutputPath, settings), ct);
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

        Scene2DExportRequest request = Request(outputPath, settings);
        return _encoding.BuildSink(request, request.Core, ffmpeg, _selection);
    }

    private static Scene2DExportRequest Request(string outputPath, PackSettings settings) => new(
        new ExportRequest(0, 0, settings.Fps, new SkiaSharp.SKSizeI(settings.Side, settings.Side), 1.0,
            settings.FormatId, PackPlanner.LayerIds, new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>())),
        outputPath, string.Empty, EncoderOverride: settings.EncoderOverride, Quality: settings.Quality);
}

/// <summary>Source-generated log lines for Pack Export.</summary>
internal static partial class PackExportLog
{
    /// <summary>Category (the "App" source tag) for pack export lines.</summary>
    public const string Category = "App.PackExport";

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{line}")]
    public static partial void Line(ILogger logger, string line);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "pack encoder: {line}")]
    public static partial void Encoder(ILogger logger, string line);
}
