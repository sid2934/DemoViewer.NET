#region

using System.Diagnostics;
using System.Text.Json.Nodes;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Benchmarking;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d export</c>: the headless front end to <c>SceneExportSession</c>.
///     <para>
///         Argument parsing and nothing more. The range, the format, the size and the camera become an
///         <c>ExportRequest</c>, the frames come from <c>TrackerFrameSource</c>, the sink is chosen by
///         <c>--format</c> plus what <c>FfmpegLocator</c> found, and the session does the rest. Every
///         rule it appears to enforce is <c>SceneExportSession.Validate</c>'s, the same validator the
///         in-app dialog calls. A CLI that could produce a file the app refuses would be a second
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

        // Palette and ink are the two options the dialog had and the CLI did not. Both have to come off
        // the request or "a request the dialog accepts is a request the CLI accepts" holds for the
        // VALIDATOR and not for the picture: same request, two materially different files.
        ScenePalette palette = ResolvePalette(args.String("palette"));
        bool annotations = args.Flag("annotations");
        if (args.String("annotations") is { Length: > 0 } annotationValue)
        {
            throw new CliUsageException(
                $"--annotations is a flag and takes no value (got '{annotationValue}'); it burns in " +
                $"the demo's own '{AnnotationStore.SidecarExtension}' sidecar.");
        }

        // Which rung of the encoder ladder, and how much to spend per frame. `auto` walks the ladder;
        // `software` skips the hardware rungs (the machine-independent answer a bisect wants); a rung's
        // own name is taken literally and refused if it does not verify.
        string encoderRequest = args.String("encoder") ?? EncoderLadder.Auto;
        string? qualityRequest = args.String("quality");
        if (qualityRequest is not null && !ExportQualities.TryParse(qualityRequest, out _))
        {
            throw new CliUsageException(
                $"--quality expects one of: {string.Join(", ", ExportQualities.All)}, got '{qualityRequest}'.");
        }

        ExportQuality quality = ExportQualities.ParseOrDefault(qualityRequest);

        // Renders and reads back every frame but encodes nothing: the only way to answer "is the
        // RENDERER fast enough" separately from "is libvpx fast enough". A GPU provider's speed is
        // measured against this baseline, since a GPU cannot make an encoder quicker.
        bool noEncode = args.Flag("no-encode");
        bool perf = PerfOutput.Requested(args);
        IReadOnlyList<string>? layers = args.List("layers");
        AssetsRoot assets = AssetsRootResolver.Resolve(args);
        // ForceCpu as the bottom rung, same as the golden lane. An auto-probe that finds ANGLE would
        // hand SceneExportSession a thread-affine provider, which the session refuses; auto-probing into
        // a guaranteed refusal is no default. An explicit --gpu still reaches that refusal, and says so.
        ResolvedBackend backend = BackendResolver.Resolve(args, RenderBackendPreference.ForceCpu);
        args.ThrowIfUnconsumed();

        // SceneExportSession refuses this too, because the app can reach it without going through here.
        // But its refusal is an ExportValidationException, which lands on exit 3. "This build cannot
        // export on a GPU" is an environment answer, exit 6: the same code `--layout single` gives, and
        // for the same reason, a real feature that is not in this build yet.
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

        // The ink FIRST, because whether the annotation id belongs in the layer set is "was one loaded",
        // not "was one asked for": a --annotations run against a demo with no sidecar must not name a
        // layer the render then starves.
        AnnotationSession? ink = annotations
            ? await LoadInkAsync(demoPath, tickRate, frames.Count, ct).ConfigureAwait(false)
            : null;

        ExportRequest request = new(
            0,
            0, // re-stamped below from the source's own frame count
            fps,
            size,
            speed,
            format,
            BuildLayerIds(layers, hud, ink is not null),
            new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>()));

        using TrackerFrameSource source = new(frames, new SceneFrameBuilder(), startFrame, endFrame,
            fps, speed, tickRate)
        {
            MapName = demo.MapName,
            Radars = mapAssets is null ? null : MapAssetPipeline.DescribeRadars(mapAssets)
        };

        request = request with
        {
            EndFrame = Math.Max(0, source.FrameCount - 1)
        };

        // AFTER the source exists, because the clock reads the source. See BuildHud.
        IHudDataSource? hudData = hud ? BuildHud(source, tickRate) : null;

        // Pure argument validation BEFORE the ffmpeg gate: a wrong --encoder must be refused with the
        // ladder's choices even on a machine with no ffmpeg at all (the GPU-less CI runner hits exactly
        // this ordering). --no-encode ignores --encoder by documented decision, so it skips too.
        if (!noEncode)
        {
            EncoderSelector.ValidateRequest(format, encoderRequest);
        }

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

        // The ladder walk, BEFORE the range is rendered: one test encode per hardware rung, cached for
        // the life of this process. A refusal (`--encoder h264_nvenc` on a machine whose driver cannot
        // run it) must arrive now, not after two minutes of frames have gone into a pipe. Skipped
        // entirely when nothing will be encoded.
        EncoderSelection? encoder;
        long probeStarted = Stopwatch.GetTimestamp();
        try
        {
            encoder = noEncode || gif && !ffmpeg.Found
                ? null
                : new EncoderSelector(new EncoderProbeCache())
                    .Select(format, encoderRequest, quality, ffmpeg.Directory, ct);
        }
        catch
        {
            backend.Provider.Dispose();
            mapAssets?.Dispose();
            throw;
        }

        // Timed HERE and not inside the probe: Pipeline is banned from Stopwatch outside two namespaces
        // (BannedApiTests), and one number for the whole walk is the number a user cares about anyway.
        double probeMs = encoder is null ? 0 : Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds;

        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(
            [.. request.LayerIds], null, null, hudData, ink);

        SceneExportSession session = new(compositor)
        {
            Palette = palette,
            AuthoritativeFloors = mapAssets?.Floors,
            RadarBinder = mapAssets is null ? null : new MapRadarBinder(mapAssets),

            // Sized to the range, so a two-minute capture is the whole two minutes rather than its tail.
            Perf = perf ? new ScenePerfRecorder(Math.Max(1, source.FrameCount)) : null
        };

        // ffmpeg's stderr is its normal banner plus a per-second progress line, so it is echoed only on
        // request. A real encoder failure still surfaces: FFMpegCore throws with the stderr tail.
        IFrameSink sink = noEncode
            ? new HashingFrameSink()
            : gif && !ffmpeg.Found
                ? new ManagedGifSink(outPath, fps)
                : new FfmpegFrameSink(new FfmpegSinkOptions(outPath, format, size.Width, size.Height, fps,
                    ffmpeg.Directory, encoder, Log: ffmpegLog ? ConsoleOut.Info : null));

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
        PerfReport? perfReport = session.Perf?.Snapshot();

        if (ConsoleOut.IsJson)
        {
            JsonObject payload = new()
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

                // Additive: `encoder` above keeps its old meaning (WHICH PROGRAM encodes), and these say
                // which codec inside it, chosen how. A hardware encoder is not bit-reproducible, so the
                // file's bytes are a function of this machine. Record the machine's answer or two files
                // cannot be compared later.
                ["video_encoder"] = encoder?.Encoder.Name,
                ["video_encoder_kind"] = encoder is null
                    ? null
                    : encoder.Encoder.Acceleration.ToString().ToLowerInvariant(),
                ["video_codec"] = encoder?.Encoder.Codec,
                ["encoder_reason"] = encoder?.Reason,
                ["encoder_arguments"] = encoder?.Arguments,
                ["quality"] = encoder is null ? null : ExportQualities.ToId(encoder.Quality),
                ["encoder_probe_ms"] = RenderCommand.Round(probeMs),
                ["encoder_attempts"] = ToAttempts(encoder),
                ["layers"] = RenderCommand.ToArray(request.LayerIds),
                ["parse_ms"] = RenderCommand.Round(parseMs),
                ["elapsed_ms"] = RenderCommand.Round(elapsedMs)
            };

            // Additive: one new key on the documented schema_version 1 shape, absent without the flag.
            // Decomposes realtime_ratio above into the five costs that produce it.
            if (perfReport is not null)
            {
                payload["perf"] = PerfOutput.ToJson(perfReport);
            }

            ConsoleOut.Json(payload);
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"wrote {outPath}  {size.Width}x{size.Height}  {format}  {last.FramesTotal} frames @ {fps} fps"));
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"elapsed {elapsedMs:F0} ms (parse {parseMs:F0} ms)  render {last.FramesPerSecond:F1} fps  " +
                $"{realtimeRatio:F2}x realtime  encoder={(noEncode ? "none" : ffmpeg.Found ? "ffmpeg" : "imagesharp-gif")}"));

            if (encoder is not null)
            {
                // The chosen rung AND why, on one line: "it picked the fast one" and "it fell back
                // because your driver said no" are different facts and a user needs to be able to tell
                // them apart without re-running with --json.
                ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                    $"video encoder: {encoder.Describe()}  (probe {probeMs:F0} ms)"));
                foreach (EncoderProbeResult attempt in encoder.Attempts)
                {
                    if (!attempt.Works)
                    {
                        ConsoleOut.Info("  rejected " + attempt.Describe());
                    }
                }
            }

            if (perfReport is not null)
            {
                PerfOutput.WriteHuman(perfReport);
            }
        }

        return ExitCode.Success;
    }

    // Every rung the ladder walked, in the order it walked them, losers included. A report showing only
    // the winner could not distinguish "there was no GPU" from "the GPU said no", and those two send a
    // user to different places.
    private static JsonArray? ToAttempts(EncoderSelection? encoder)
    {
        if (encoder is null)
        {
            return null;
        }

        JsonArray array = [];
        foreach (EncoderProbeResult attempt in encoder.Attempts)
        {
            array.Add(new JsonObject
            {
                ["encoder"] = attempt.Encoder,
                ["works"] = attempt.Works,
                ["detail"] = attempt.Detail
            });
        }

        return array;
    }

    // 20 for GIF (divides the 100-centisecond frame delay evenly, see SceneExportSession's _gifFps);
    // 60 for video. SceneExportSession.SupportedFps is the authority and refuses anything else.
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

    /// <summary>
    ///     The default id set, plus whatever the flags opted into. The CLI counterpart of
    ///     <c>Playback2DExportDialogViewModel.BuildLayerIds</c>: both project
    ///     <see cref="SceneLayerCatalog.SceneStackIds" /> rather than listing ids, so one request cannot
    ///     mean two different videos.
    /// </summary>
    /// <param name="layers">An explicit <c>--layers</c> list, or null for the default set.</param>
    /// <param name="hud">Whether <c>--hud</c> was given.</param>
    /// <param name="hasInk">Whether a sidecar was actually loaded. See the call site.</param>
    internal static HashSet<string> BuildLayerIds(IReadOnlyList<string>? layers, bool hud, bool hasInk)
    {
        // The opt-in set (SceneLayerIds.OptIn), NOT a "hud." prefix test. The prefix spelling was blind
        // to every non-HUD opt-in layer: once playback2d.annotations became a SceneStackId, an export
        // with no --layers named the ink in its default set and in the sidecar manifest's `layers`.
        //
        // Vision comes out too. It is NOT opt-in in the catalog, so a default export named it while the
        // app's dialog ships it OFF (the solve is the frame's biggest per-frame cost): one request, two
        // different videos. The CLI has no visibility engine to hand VisionLayer either. `--layers` can
        // still name it explicitly.
        //
        // That last part is EXPORT only. VisionLayer falls back to a frame's pre-solved SceneVision, so
        // `render`/`golden`/`bench` do draw cones from a fixture. An export's frames come off
        // SceneFrameBuilder, whose Vision input nothing fills, so `dv2d export --layers
        // …,playback2d.vision` is still an empty layer. Feeding it needs a VisibilityEngine for the
        // demo's map; nobody has needed it yet.
        HashSet<string> ids = layers is null
            ?
            [
                .. SceneLayerCatalog.SceneStackIds.Where(id =>
                    !SceneLayerIds.OptIn.Contains(id) &&
                    !string.Equals(id, SceneLayerIds.Vision, StringComparison.Ordinal))
            ]
            : [.. layers.Select(SceneLayerCatalog.Normalize)];

        if (hud)
        {
            ids.Add(SceneLayerIds.HudClock);
            ids.Add(SceneLayerIds.HudKillFeed);
            ids.Add(SceneLayerIds.HudRoster);
        }

        if (hasInk)
        {
            ids.Add(SceneLayerIds.Annotations);
        }

        return ids;
    }

    // dark (the shipping default) or light. Hard-coding Dark here inverts every export of a request the
    // app would have drawn in Light.
    private static ScenePalette ResolvePalette(string? requested) => requested?.ToLowerInvariant() switch
    {
        null or "dark" => ScenePalette.Dark,
        "light" => ScenePalette.Light,
        _ => throw new CliUsageException($"--palette expects dark or light, got '{requested}'.")
    };

    // The demo's own '.dvann.json', through the same store the app writes it with. A missing, truncated
    // or foreign sidecar is not an error (AnnotationStore returns an empty result for all three), but an
    // EMPTY one returns null here, so the caller can keep the annotation id out of the layer set rather
    // than naming a layer with nothing behind it.
    private static async Task<AnnotationSession?> LoadInkAsync(string demoPath, int tickRate,
        int frameCount, CancellationToken ct)
    {
        // The same identity the 2D tab builds (Playback2DTabViewModel.AttachAnnotationsToCurrentDemo),
        // so a sidecar written by the app is recognised as belonging to this parse rather than reported
        // as a clock mismatch.
        ClockIdentity clock = new(ClockIdentity.DvFrameClock, tickRate > 0 ? tickRate : 64,
            frameCount, 0, 0);

        AnnotationLoadResult loaded = await new AnnotationStore(null)
            .LoadAsync(demoPath, clock, ct).ConfigureAwait(false);

        if (loaded.Elements.Count == 0)
        {
            ConsoleOut.Info($"--annotations: no ink found for {Path.GetFileName(demoPath)}");
            return null;
        }

        AnnotationDocument document = new();
        document.Reset(loaded.Elements);
        return new AnnotationSession(document);
    }

    // The clock reads the SOURCE's own game info (the round and score SceneFrameBuilder reads off
    // CCSGameRulesProxy and the two CCSTeam entities for the frame being drawn) rather than a captured
    // SceneGameInfo value, which would freeze the scoreboard at frame 0.
    //
    // Reading it through the source keeps the clock a pure function of the frame: SceneExportSession
    // calls FrameAt immediately before Advance, and ClockLayer asks during Advance, so LastGameInfo is
    // always the drawn frame's.
    //
    // The kill feed stays empty: kill rows come from a parsed event timeline the app builds from
    // AllGameEvents, and the CLI has no equivalent. The roster is not in that gap, it comes off the
    // same frame the clock does, so `--hud` draws real cards over an empty feed. Internal rather than
    // private so a test can call this exact closure.
    internal static TimelineHudDataSource BuildHud(TrackerFrameSource source, int tickRate) =>
        new([], tickRate, _ => ClockReading.From(source.LastGameInfo),
            rosterAt: _ => source.LastRoster);
}
