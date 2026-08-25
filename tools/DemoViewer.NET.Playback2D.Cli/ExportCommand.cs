#region

using System.Diagnostics;
using System.Text.Json.Nodes;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Hud;
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
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d export</c> — the headless front end to B4's <c>SceneExportSession</c>.
///     <para>
///         It is argument parsing and nothing more: the range, the format, the size and the camera become
///         an <c>ExportRequest</c>, the frames come from <c>TrackerFrameSource</c>, the sink is chosen by
///         <c>--format</c> plus what <c>FfmpegLocator</c> found, and the session does the rest. Every rule
///         it appears to enforce is <c>SceneExportSession.Validate</c>'s, which is the same validator the
///         in-app dialog calls — a CLI that could produce a file the app refuses would be a second
///         encoder policy in disguise.
///     </para>
///     <para>
///         Ctrl+C cancels: the token reaches the session, which disposes the sink, which kills ffmpeg and
///         removes the partial output.
///     </para>
/// </summary>
internal static class ExportCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="ct">Cancels the export.</param>
    public static async Task<ExitCode> RunAsync(CliArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();

        string demoPath = args.String("demo")
                          ?? throw new CliUsageException("export requires --demo <path.dem>.");

        long started = Stopwatch.GetTimestamp();
        ParsedDemo demo = DemoInput.Load(demoPath, out double parseMs);
        int tickRate = DemoInput.TickRate(demo);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        (int startFrame, int endFrame) = ResolveRange(args, frames);
        string format = (args.String("format") ?? ExportFormats.WebM).ToLowerInvariant();
        int fps = args.Int("fps", DefaultFps(format));
        double speed = args.Double("speed", 1.0);
        SKSizeI size = args.Size("size", new SKSizeI(1920, 1080));
        string outPath = args.String("out") ?? $"dv2d-export.{format}";
        bool hud = args.Flag("hud");
        bool ffmpegLog = args.Flag("ffmpeg-log");

        // Renders and reads back every frame but encodes nothing. The only way to answer "is the RENDERER
        // fast enough" separately from "is libvpx fast enough" — and the measurement C2 compares a GPU
        // provider against, since a GPU cannot make an encoder quicker.
        bool noEncode = args.Flag("no-encode");
        IReadOnlyList<string>? layers = args.List("layers");
        AssetsRoot assets = AssetsRootResolver.Resolve(args);
        // ForceCpu as the bottom rung, like the golden lane above it and for the same kind of reason: an
        // auto-probe that finds ANGLE would hand SceneExportSession a thread-affine provider, and the
        // session refuses it (C2 Stage 1 owns making it work). Auto-probing into a guaranteed refusal is
        // not a default. An explicit --gpu still reaches that refusal, and says so.
        ResolvedBackend backend = BackendResolver.Resolve(args, RenderBackendPreference.ForceCpu);
        args.ThrowIfUnconsumed();

        // SceneExportSession refuses this too — it has to, because the app can reach it without going
        // through here — but the refusal it throws is an ExportValidationException, which lands on exit
        // 3. "This build cannot export on a GPU" is an environment answer, exit 6, the same code
        // `--layout single` gives for the same kind of reason: a real feature, not in this build yet.
        if (backend.Backend != RenderBackend.CpuRaster)
        {
            backend.Provider.Dispose();
            throw new BackendUnavailableException(
                $"export cannot run on the {backend.Backend} surface provider yet: the render loop " +
                "crosses threads between frames and the GPU provider is bound to the thread that " +
                "created it (C2 Stage 1). Drop --gpu, or pass --cpu.");
        }

        LoadedMapAsset? mapAssets = null;
        if (assets is { Source: not AssetsRootSource.Disabled, Path: { } root })
        {
            mapAssets = MapAssetPipeline.TryLoad(root, demo.MapName);
        }

        ExportRequest request = new(
            0,
            0, // re-stamped below from the source's own frame count
            fps,
            size,
            speed,
            format,
            BuildLayerIds(layers, hud),
            new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>()));

        using TrackerFrameSource source = new(frames, new SceneFrameBuilder(), startFrame, endFrame,
            fps, speed, tickRate)
        {
            MapName = demo.MapName,
            Radars = mapAssets is null ? null : MapAssetPipeline.DescribeRadars(mapAssets)
        };

        request = request with { EndFrame = Math.Max(0, source.FrameCount - 1) };

        // AFTER the source exists, because the clock reads the source. See BuildHud.
        IHudDataSource? hudData = hud ? BuildHud(source, tickRate) : null;

        FfmpegLocation ffmpeg = FfmpegLocator.Locate(null);
        bool gif = string.Equals(format, ExportFormats.Gif, StringComparison.Ordinal);
        if (!ffmpeg.Found && !gif && !noEncode)
        {
            backend.Provider.Dispose();
            mapAssets?.Dispose();
            throw new BackendUnavailableException(
                "no ffmpeg was found on PATH, so only --format gif can be produced here. " +
                "Install ffmpeg, or export GIF.");
        }

        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(
            [.. request.LayerIds], null, null, hudData);

        SceneExportSession session = new(compositor)
        {
            Palette = ScenePalette.Dark,
            AuthoritativeFloors = mapAssets?.Floors,
            RadarBinder = mapAssets is null ? null : new MapRadarBinder(mapAssets)
        };

        // ffmpeg's stderr is its normal banner plus a per-second progress line, so it is echoed only on
        // request. A real encoder failure still surfaces: FFMpegCore throws with the stderr tail.
        IFrameSink sink = noEncode
            ? new HashingFrameSink()
            : gif && !ffmpeg.Found
                ? new ManagedGifSink(outPath, fps)
                : new FfmpegFrameSink(new FfmpegSinkOptions(outPath, format, size.Width, size.Height, fps,
                    ffmpeg.Directory, Log: ffmpegLog ? ConsoleOut.Info : null));

        ExportProgress last = default;
        Progress<ExportProgress> progress = new(p => last = p);

        try
        {
            await session.RunAsync(request, source, sink, backend.Provider, progress, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            backend.Provider.Dispose();
            mapAssets?.Dispose();
        }

        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double demoSeconds = (frames[endFrame].ServerTick - frames[startFrame].ServerTick) / (double)tickRate;
        double realtimeRatio = elapsedMs > 0 ? demoSeconds / (elapsedMs / 1000.0) : 0;

        if (ConsoleOut.IsJson)
        {
            ConsoleOut.Json(new JsonObject
            {
                ["schema_version"] = 1,
                ["command"] = "export",
                ["ok"] = true,
                ["out"] = outPath,
                ["format"] = format,
                ["width"] = size.Width,
                ["height"] = size.Height,
                ["fps"] = fps,
                ["speed"] = speed,
                ["frames"] = last.FramesTotal,
                ["frames_per_second"] = RenderCommand.Round(last.FramesPerSecond),
                ["demo_seconds"] = RenderCommand.Round(demoSeconds),
                ["realtime_ratio"] = RenderCommand.Round(realtimeRatio),
                ["backend"] = backend.Backend.ToString(),
                ["encoder"] = noEncode ? "none" : ffmpeg.Found ? "ffmpeg" : "imagesharp-gif",
                ["ffmpeg_origin"] = ffmpeg.Origin.ToString(),
                ["layers"] = RenderCommand.ToArray(request.LayerIds),
                ["parse_ms"] = RenderCommand.Round(parseMs),
                ["elapsed_ms"] = RenderCommand.Round(elapsedMs)
            });
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"wrote {outPath}  {size.Width}x{size.Height}  {format}  {last.FramesTotal} frames @ {fps} fps"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"elapsed {elapsedMs:F0} ms (parse {parseMs:F0} ms)  render {last.FramesPerSecond:F1} fps  " +
                $"{realtimeRatio:F2}x realtime  encoder={(noEncode ? "none" : ffmpeg.Found ? "ffmpeg" : "imagesharp-gif")}"));
        }

        return ExitCode.Success;
    }

    // 20 for GIF because a GIF frame delay is a whole number of centiseconds and 20 divides 100; 60 for
    // video. SceneExportSession.SupportedFps is the authority, and it refuses anything else.
    private static int DefaultFps(string format) =>
        string.Equals(format, ExportFormats.Gif, StringComparison.Ordinal) ? 20 : 60;

    private static (int Start, int End) ResolveRange(CliArgs args, IReadOnlyList<DemoFrame> frames)
    {
        int last = frames.Count - 1;
        int start = Resolve(args, "from", 0, frames);
        int end = Resolve(args, "to", last, frames);

        if (end < start)
        {
            throw new CliUsageException($"--from {start} is after --to {end}.");
        }

        return (start, end);
    }

    // --from/--to accept a frame index by default, or a tick with a "t" prefix (--from t12000), so a
    // caller who thinks in demo ticks does not have to convert by hand.
    private static int Resolve(CliArgs args, string option, int fallback, IReadOnlyList<DemoFrame> frames)
    {
        if (args.String(option) is not { } raw)
        {
            return fallback;
        }

        bool isTick = raw.StartsWith('t') || raw.StartsWith('T');
        string digits = isTick ? raw[1..] : raw;

        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new CliUsageException($"--{option} expects a frame index or a tick (t12000), got '{raw}'.");
        }

        if (isTick)
        {
            int index = TrackerFrameSource.FrameIndexForTick(frames, value);
            return index >= 0
                ? index
                : throw new CliUsageException($"--{option} t{value} is outside the demo.");
        }

        return value >= 0 && value < frames.Count
            ? value
            : throw new CliUsageException(
                $"--{option} {value} is outside the demo (frames 0..{frames.Count - 1}).");
    }

    private static HashSet<string> BuildLayerIds(IReadOnlyList<string>? layers, bool hud)
    {
        HashSet<string> ids = layers is null
            ? [.. SceneLayerCatalog.SceneStackIds.Where(id => !id.StartsWith("hud.", StringComparison.Ordinal))]
            : [.. layers.Select(SceneLayerCatalog.Normalize)];

        if (hud)
        {
            ids.Add(SceneLayerIds.HudClock);
            ids.Add(SceneLayerIds.HudKillFeed);
        }

        return ids;
    }

    // The clock is the SOURCE's own game info — the round and the score SceneFrameBuilder read off
    // CCSGameRulesProxy and the two CCSTeam entities for the frame being drawn. It used to be a constant
    // ClockReading.Unknown, which renders "Round —  T 0 : 0 CT" over every frame of every CLI export,
    // however far into the match the range was.
    //
    // Reading it through the source is what keeps the clock a pure function of the frame:
    // SceneExportSession calls FrameAt immediately before Advance, and ClockLayer asks during Advance,
    // so LastGameInfo is the drawn frame's. Capturing a SceneGameInfo VALUE here instead would freeze
    // the scoreboard at frame 0 — which is the app-side half of this same bug.
    //
    // The kill feed is still empty, and that is the CLI's remaining gap: kill rows come from a parsed
    // event timeline the app builds from AllGameEvents, and the CLI has no equivalent. --hud on the CLI
    // draws a true clock over an empty feed; inventing rows would be worse than the absence.
    // Internal, not private: the closure IS the bug. A test that rebuilt an equivalent delegate would
    // have passed against the broken constant too.
    internal static TimelineHudDataSource BuildHud(TrackerFrameSource source, int tickRate) =>
        new([], tickRate, _ => ClockReading.From(source.LastGameInfo));
}
