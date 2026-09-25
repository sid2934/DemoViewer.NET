#region

using System.Globalization;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     The open strat as it stood when Export was pressed, taken on the UI thread by
///     <see cref="StratCanvasViewModel.CaptureForExport" />. Everything here is either immutable or a copy the
///     export owns: the canvas keeps editing its own track set and ink while the GIF renders.
/// </summary>
/// <param name="Projection">The path's projection: schedule, labels, utility, round length. Immutable.</param>
/// <param name="Tracks">A copy of the canvas's track set, so a drag during the render moves nothing in it.</param>
/// <param name="Ink">A private session over a <c>Reset</c> copy of the projected document, never the live one.</param>
/// <param name="MapName">The strat's map; the export loads its own bundle for it.</param>
/// <param name="Title">The strat's name, for the default file name.</param>
/// <param name="FallbackBounds">The world rectangle to frame when the map has no bundle.</param>
public sealed record StratExportCapture(
    StratSceneProjection Projection,
    TokenTrackSet Tracks,
    AnnotationSession Ink,
    string MapName,
    string Title,
    WorldBounds FallbackBounds);

/// <summary>
///     Strat Export (step-authoring.md §3.6): the strat rendered to GIF or video through
///     <see cref="SceneExportSession" /> with a <see cref="StratFrameSource" /> and no demo behind it.
///     <para>
///         <b>The 2D export's job with a different source.</b> This is the <see cref="IExportRunner" /> an
///         <see cref="ExportJobService" /> runs, so the refusals, the heavy-job gate, single-flight, the status
///         chip and cancel are that service's; the encoder ladder, the managed GIF floor and the ffmpeg sink are
///         <see cref="ExportEncoding" />'s. What is here is the strat's half: the spec, the layer stack and the map.
///     </para>
///     <para>
///         <b>The capture rides the request.</b> The dialog only knows how to freeze an ink session at Start, so
///         the capture is keyed by that session and read back by reference on the pool thread; two Starts cannot
///         trade strats, for the reason <c>Scene2DExportRequest.Ink</c> gives.
///     </para>
///     <para>
///         <b>Private everything</b>, as the 2D export: its own map bundle, compositor and surface. The canvas's
///         bundle is disposed when the user opens a strat on another map, which can happen mid-render.
///     </para>
/// </summary>
public sealed class StratExportJob : IExportRunner
{
    /// <summary>Default frame rate (overview O-29): the GIF rate that divides 100 and keeps a round under the cap.</summary>
    public const int DefaultFps = 20;

    /// <summary>Default width (O-29). Square, so a radar fills it and the text arithmetic in §6 holds.</summary>
    public const int DefaultWidth = 640;

    /// <summary>Default height: the square's other side.</summary>
    public const int DefaultHeight = 640;

    /// <summary>How long the default range runs past the last step: 2 s on the strat clock.</summary>
    public const int TailTicks = 2 * StepSchedule.TicksPerSecond;

    /// <summary>The refusal when the request carries no capture this job made.</summary>
    public const string NoStratRefusal = "There is no open strat to export.";

    private static readonly ConditionalWeakTable<AnnotationSession, StratExportCapture> _captures = new();

    private readonly ExportEncoding _encoding;
    private readonly Func<string?, LoadedMapAsset?> _mapLoader;
    private readonly Func<IRenderSurfaceProvider> _surfaces;

    /// <summary>Creates the job.</summary>
    /// <param name="mapLoader">Loads the strat's map bundle by name; null draws the grid.</param>
    /// <param name="surfaces">Builds the render surface provider; the CPU rasteriser when omitted.</param>
    /// <param name="managedFfmpegDirectory">Where an app-managed ffmpeg lives; the reels' directory when omitted.</param>
    /// <param name="log">Line sink for the chosen encoder and ffmpeg's stderr.</param>
    /// <param name="encoderProbe">How ladder rungs are verified; the process-wide cache when omitted.</param>
    /// <param name="locateFfmpeg">
    ///     Finds ffmpeg given the managed directory; <c>FfmpegLocator.Locate</c> when omitted. The seam is what
    ///     lets a test take the managed GIF floor on a machine that has ffmpeg on PATH.
    /// </param>
    public StratExportJob(
        Func<string?, LoadedMapAsset?> mapLoader,
        Func<IRenderSurfaceProvider>? surfaces = null,
        Func<string?>? managedFfmpegDirectory = null,
        Action<string>? log = null,
        IEncoderProbe? encoderProbe = null,
        Func<string?, FfmpegLocation>? locateFfmpeg = null)
    {
        ArgumentNullException.ThrowIfNull(mapLoader);
        _mapLoader = mapLoader;
        _surfaces = surfaces ?? (static () => new CpuSurfaceProvider());
        _encoding = new ExportEncoding(managedFfmpegDirectory ?? (static () => FfmpegDependency.ManagedDirectory),
            locateFfmpeg ?? FfmpegLocator.Locate, log, new EncoderSelector(encoderProbe));
    }

    /// <summary>
    ///     The layers a strat export draws: the seven scene layers, then the ink and the round clock when asked
    ///     for. Named explicitly (§3.6) rather than left to the null-include default, which draws no opt-in layer.
    /// </summary>
    /// <param name="ink">Whether the strat's strokes are burned in.</param>
    /// <param name="clock">Whether <c>hud.clock</c> counts the round down.</param>
    public static HashSet<string> LayerIds(bool ink, bool clock)
    {
        HashSet<string> ids = new(StringComparer.Ordinal)
        {
            SceneLayerIds.Radar,
            SceneLayerIds.Trails,
            SceneLayerIds.AreaEffects,
            SceneLayerIds.Vision,
            SceneLayerIds.Markers,
            SceneLayerIds.Bomb,
            SceneLayerIds.FloorLabel
        };

        if (ink)
        {
            ids.Add(SceneLayerIds.Annotations);
        }

        if (clock)
        {
            ids.Add(SceneLayerIds.HudClock);
        }

        return ids;
    }

    /// <summary>
    ///     The ranges the dialog offers, in strat ticks, the default first: the first step to the last plus
    ///     <see cref="TailTicks" /> (O-29), then the round from its start when the first step is later than that.
    /// </summary>
    /// <param name="projection">The path being exported.</param>
    public static IReadOnlyList<ExportRangeOption> Ranges(StratSceneProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        int first = projection.Ticks.Count > 0 ? projection.Ticks[0] : 0;
        int end = projection.LastTick + TailTicks;
        List<ExportRangeOption> ranges =
        [
            new(Describe("First step to last + 2 s", first, end, projection.RoundSeconds), first, end)
        ];

        if (first > 0)
        {
            ranges.Add(new ExportRangeOption(Describe("From the round's start", 0, end, projection.RoundSeconds), 0, end));
        }

        return ranges;
    }

    /// <summary>
    ///     Freezes a capture onto its ink session, the object the dialog puts on the request. Returns that session,
    ///     which is what the dialog's ink capture hands back.
    /// </summary>
    /// <param name="capture">The strat as it stands at Start.</param>
    public static AnnotationSession Register(StratExportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _captures.AddOrUpdate(capture.Ink, capture);
        return capture.Ink;
    }

    /// <summary>The spec a capture exports as over one range, at one rate and speed.</summary>
    /// <param name="capture">The frozen strat.</param>
    /// <param name="asset">The map bundle, or null to frame <see cref="StratExportCapture.FallbackBounds" /> on the grid.</param>
    /// <param name="startTick">First strat tick rendered.</param>
    /// <param name="endTick">Last strat tick, inclusive.</param>
    /// <param name="fps">Output frame rate.</param>
    /// <param name="speed">Playback-rate multiplier.</param>
    public static StratSceneSpec BuildSpec(StratExportCapture capture, LoadedMapAsset? asset, int startTick,
        int endTick, int fps, double speed)
    {
        ArgumentNullException.ThrowIfNull(capture);
        StratSceneProjection projection = capture.Projection;

        // SectionHeights null and the bundle's bounds, the canvas's own spec: the canvas and the file draw the
        // same frames, and the bundle's floors reach the export through AuthoritativeFloors instead.
        return new StratSceneSpec(capture.Tracks, projection.Schedule, capture.Ink, projection.Labels,
            capture.MapName, asset is null ? [] : MapAssetPipeline.DescribeRadars(asset),
            asset is null ? capture.FallbackBounds : MapAssetPipeline.RadarBounds(asset), null,
            projection.Utility, projection.RoundSeconds, startTick, endTick, fps, speed);
    }

    /// <inheritdoc />
    public async Task RunAsync(Scene2DExportRequest request, IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Ink is not { } ink || !_captures.TryGetValue(ink, out StratExportCapture? capture))
        {
            throw new ExportRefusedException(NoStratRefusal);
        }

        (FfmpegLocation ffmpeg, EncoderSelection? encoder) = _encoding.Resolve(request, ct);

        using LoadedMapAsset? asset = SafeLoad(capture.MapName);

        // The dialog's range is in strat ticks: DemoStartFrame and DemoEndFrame carry them, and the dialog sized
        // the request with StratFrameSource.OutputFrameCount, the source's own arithmetic.
        StratSceneSpec spec = BuildSpec(capture, asset, request.DemoStartFrame, request.DemoEndFrame,
            request.Core.Fps, request.Core.Speed);
        StratFrameSource source = new(spec);
        ExportRequest core = request.Core with
        {
            StartFrame = 0,
            EndFrame = Math.Max(0, source.FrameCount - 1)
        };

        // No vision solver: the layer is named with the other six and draws nothing without one.
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack([.. core.LayerIds], null, null,
            new StratHudDataSource(spec.RoundSeconds), spec.Ink);
        using IRenderSurfaceProvider surfaces = _surfaces();

        SceneExportSession session = new(compositor)
        {
            Palette = request.Palette ?? ScenePalette.Dark,
            DisplayMode = LevelDisplayMode.Stacked,
            AuthoritativeFloors = asset?.Floors,
            RadarBinder = asset is null ? null : new MapRadarBinder(asset)
        };

        IFrameSink sink = _encoding.BuildSink(request, core, ffmpeg, encoder);
        await session.RunAsync(core, source, sink, surfaces, progress, ct).ConfigureAwait(false);
    }

    private LoadedMapAsset? SafeLoad(string map)
    {
        try
        {
            return _mapLoader(map);
        }
        catch (Exception)
        {
            return null; // the grid, as on the canvas: a strat without a bundle still exports
        }
    }

    // "First step to last + 2 s  (1:50 to 1:08)": the round clock the coach wrote the steps on.
    private static string Describe(string label, int fromTick, int toTick, int roundSeconds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{label}  ({Clock(roundSeconds, fromTick)} to {Clock(roundSeconds, toTick)})");

    private static string Clock(int roundSeconds, int tick)
    {
        double seconds = StepSchedule.AtSecondsFor(tick, roundSeconds);
        string sign = seconds < 0 ? "-" : "";
        int whole = (int)Math.Round(Math.Abs(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{whole / 60}:{whole % 60:00}");
    }
}
