#region

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services.Export.Pack;
using DemoViewer.NET.Services.Review;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d pack</c>: Headless Packs (plan.md §3, Phase 4). A review-queue.json file becomes one
///     video, scheduled rather than clicked: "every scrim from last night, tagged rounds only, rendered
///     by morning" is this command in a cron line. It is <see cref="PackPlanner" /> and
///     <see cref="PackExporter" /> — the exact plan-then-stitch policy the app's Export pack row runs —
///     handed a headless <see cref="IPackClipRenderer" />/<see cref="IPackEncoder" /> pair instead of the
///     App's own (<see cref="HeadlessPackClipRenderer" />, <see cref="HeadlessPackEncoder" />).
///     <para>
///         Which rounds are "tagged" is decided before the queue file exists: the CLI renders whatever
///         <c>review-queue.json</c> shape it is handed, in order, sections and all. It filters nothing of
///         its own, the same posture <c>export</c> takes toward its range.
///     </para>
/// </summary>
internal static class PackCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="ct">Cancels the pack; the half-written file does not survive.</param>
    public static async Task<ExitCode> RunAsync(CliArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();

        long started = Stopwatch.GetTimestamp();

        string queuePath = args.String("queue")
                           ?? throw new CliUsageException("pack requires --queue <review-queue.json>.");
        ReviewQueueFile queue = LoadQueue(queuePath);

        string format = (args.String("format") ?? ExportFormats.Mp4).ToLowerInvariant();
        string outPath = args.String("out") ?? $"dv2d-pack.{format}";
        PackSettings defaults = PackSettings.For(outPath, format);

        SKSizeI size = args.Size("size", new SKSizeI(defaults.Side, defaults.Side));
        if (size.Width != size.Height)
        {
            throw new CliUsageException(
                $"--size must be square for a pack (the radar frame is square), got {size.Width}x{size.Height}.");
        }

        int fps = args.Int("fps", defaults.Fps);
        double titleSeconds = args.Double("title-seconds", PackPlanner.DefaultTitleSeconds);
        string? encoderRequest = args.String("encoder");
        string? qualityRequest = args.String("quality");
        if (qualityRequest is not null && !ExportQualities.TryParse(qualityRequest, out _))
        {
            throw new CliUsageException(
                $"--quality expects one of: {string.Join(", ", ExportQualities.All)}, got '{qualityRequest}'.");
        }

        bool ffmpegLog = args.Flag("ffmpeg-log");
        AssetsRoot assets = AssetsRootResolver.Resolve(args);
        args.ThrowIfUnconsumed();

        PackSettings settings = new(outPath, format, size.Width, fps, titleSeconds, encoderRequest, qualityRequest);
        if (PackPlanner.Validate(settings) is { } invalid)
        {
            throw new CliUsageException(invalid);
        }

        PackPlan plan = PackPlanner.Plan(queue.Entries, settings, File.Exists);
        foreach (PackSkip skip in plan.Skipped)
        {
            ConsoleOut.Warn($"left out before rendering: {Describe(skip)}");
        }

        using HeadlessPackClipRenderer clips = new(assets);
        HeadlessPackEncoder encoder = new(ffmpegLog);
        Progress<PackProgress> progress = new(p =>
            ConsoleOut.Info($"[{p.Segment}/{p.SegmentCount}] {p.Label}"));

        PackExporter exporter = new(clips, encoder, log: ConsoleOut.Warn);
        PackResult result = await exporter.ExportAsync(plan, progress, ct).ConfigureAwait(false);

        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (ConsoleOut.IsJson)
        {
            JsonObject payload = new()
            {
                ["schema_version"] = 1,
                ["command"] = "pack",
                ["ok"] = true,
                ["queue"] = queuePath,
                ["out"] = RenderCommand.ToArray(result.Outputs),
                ["format"] = format,
                ["width"] = size.Width,
                ["height"] = size.Height,
                ["fps"] = fps,
                ["demos"] = plan.DemoCount,
                ["clips_planned"] = plan.Clips.Count,
                ["clips_rendered"] = result.ClipsRendered,
                ["clips_left_out_before_render"] = plan.Skipped.Count,
                ["clips_failed_to_render"] = result.Failed.Count,
                ["estimated_seconds"] = RenderCommand.Round(plan.Seconds),
                ["left_out"] = ToJsonArray(plan.Skipped),
                ["failed"] = ToJsonArray(result.Failed),
                ["elapsed_ms"] = RenderCommand.Round(elapsedMs)
            };

            ConsoleOut.Json(payload);
        }
        else
        {
            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture,
                $"wrote {string.Join(", ", result.Outputs)}  " +
                $"{result.ClipsRendered}/{plan.Clips.Count} clips  {plan.DemoCount} demos"));

            foreach (PackSkip failure in result.Failed)
            {
                ConsoleOut.Warn($"failed to render: {Describe(failure)}");
            }

            ConsoleOut.Info(string.Create(CultureInfo.InvariantCulture, $"elapsed {elapsedMs:F0} ms"));
        }

        return ExitCode.Success;
    }

    // review-queue.json, the same shape and the same schema-too-new refusal as the app's own
    // ReviewQueue.Load; the CLI has no ReviewQueue (no config root, no mutation, no Changed event), just
    // the file.
    private static ReviewQueueFile LoadQueue(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"queue file not found: {path}", path);
        }

        ReviewQueueFile? file = JsonSerializer.Deserialize<ReviewQueueFile>(File.ReadAllText(path),
            ReviewQueueFile.JsonOptions);

        if (file is null)
        {
            throw new InvalidDataException($"{path} did not parse to a queue.");
        }

        if (file.SchemaVersion > ReviewQueueFile.CurrentSchema)
        {
            throw new InvalidDataException(
                $"{path} is at schema {file.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, " +
                "newer than this build reads.");
        }

        return file;
    }

    // "/d/b.dem: clip 2: smokes: <why>" for a render failure (PackExporter.Label already folded the
    // clip's number and note into Reason); "/d/missing.dem: demo not found" for one left out before a
    // file was ever opened.
    private static string Describe(PackSkip skip) => $"{skip.Entry.DemoPath}: {skip.Reason}";

    private static JsonArray ToJsonArray(IReadOnlyList<PackSkip> skips)
    {
        JsonArray array = [];
        foreach (PackSkip skip in skips)
        {
            array.Add(new JsonObject
            {
                ["demo"] = skip.Entry.DemoPath,
                ["note"] = skip.Entry.Note,
                ["reason"] = skip.Reason
            });
        }

        return array;
    }
}
