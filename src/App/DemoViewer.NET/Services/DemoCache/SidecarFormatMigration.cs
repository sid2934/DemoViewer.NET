#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>What one legacy conversion did for one demo.</summary>
internal enum SidecarConversion
{
    /// <summary>Nothing to convert.</summary>
    None,

    /// <summary>The new file reads back and the legacy one is gone.</summary>
    Converted,

    /// <summary>The legacy file was kept: it did not read, or the new file did not verify.</summary>
    Failed
}

/// <summary>
///     The one-off re-encode of pre-gzip sidecars (records and grenade siblings) into their gzipped
///     names. No parse. Resumable, since a converted demo has nothing left to convert, and marker-gated
///     once a pass finishes with no failures.
/// </summary>
internal static class SidecarFormatMigration
{
    /// <summary>Written into the cache root when a pass converted everything it found.</summary>
    public const string MarkerFileName = "sidecar-format-v2.done";

    private static ILogger? _diagLog;

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(SidecarFormatLog.Category);

    /// <summary>
    ///     Queues the pass as a processing queue item, unless the marker says it is done. Null when nothing was
    ///     queued.
    /// </summary>
    /// <param name="queue">The processing queue.</param>
    /// <param name="cache">The demo cache.</param>
    /// <param name="converters">Per-demo converters, run in order.</param>
    /// <param name="batchSize">Demos between steps aside.</param>
    public static IDemoQueueHandle? Submit(IDemoProcessingQueue queue, DemoCacheStore cache,
        IReadOnlyList<Func<string, SidecarConversion>> converters, int batchSize = 32)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.CacheRoot is not { } root || File.Exists(Path.Combine(root, MarkerFileName)))
        {
            return null;
        }

        return queue.SubmitJob(new QueueJobRequest(QueueJobKind.SidecarMigration, "Sidecar format: compress cached files",
            "demo-cache", DemoJobPriority.Background,
            async job =>
            {
                await RunAsync(cache, converters, job.StepAsideAsync,
                    (done, total) => job.Report(done, total, $"{done} of {total} demos"), batchSize,
                    job.CancellationToken).ConfigureAwait(false);
                job.CancellationToken.ThrowIfCancellationRequested();
            },
            Key: "sidecar-format"));
    }

    /// <summary>Converts every indexed demo, stepping aside between batches for a demo open.</summary>
    /// <param name="cache">The demo cache.</param>
    /// <param name="converters">Per-demo converters, run in order.</param>
    /// <param name="betweenBatches">Runs before every batch after the first; null runs straight through.</param>
    /// <param name="progress">Demos done of the total, before each batch.</param>
    /// <param name="batchSize">Demos per batch.</param>
    /// <param name="cancellationToken">Stops between demos.</param>
    public static async Task<SidecarFormatResult> RunAsync(
        DemoCacheStore cache,
        IReadOnlyList<Func<string, SidecarConversion>> converters,
        Func<Task>? betweenBatches = null,
        Action<int, int>? progress = null,
        int batchSize = 32,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(converters);
        if (cache.CacheRoot is not { } root || File.Exists(Path.Combine(root, MarkerFileName)))
        {
            return new SidecarFormatResult(0, 0, 0, false);
        }

        int converted = 0, failed = 0, demos = 0;
        try
        {
            List<string> paths = [.. cache.Index.Select(e => e.Path)];
            for (int start = 0; start < paths.Count; start += Math.Max(1, batchSize))
            {
                progress?.Invoke(start, paths.Count);
                if (start > 0 && betweenBatches is not null)
                {
                    await betweenBatches().ConfigureAwait(false);
                }

                foreach (string path in paths.Skip(start).Take(Math.Max(1, batchSize)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    demos++;
                    foreach (Func<string, SidecarConversion> convert in converters)
                    {
                        switch (convert(path))
                        {
                            case SidecarConversion.Converted:
                                converted++;
                                break;
                            case SidecarConversion.Failed:
                                failed++;
                                break;
                        }
                    }
                }
            }

            if (failed == 0)
            {
                File.WriteAllText(Path.Combine(root, MarkerFileName), DateTime.UtcNow.ToString("O"));
            }

            SidecarFormatLog.PassFinished(Log, demos, converted, failed);
            return new SidecarFormatResult(demos, converted, failed, failed == 0);
        }
        catch (OperationCanceledException)
        {
            return new SidecarFormatResult(demos, converted, failed, false);
        }
        catch (Exception ex)
        {
            SidecarFormatLog.PassFailed(Log, ex);
            return new SidecarFormatResult(demos, converted, failed, false);
        }
    }
}

/// <summary>One pass's tally. <paramref name="Completed" /> is true when the marker was written.</summary>
internal sealed record SidecarFormatResult(int Demos, int Converted, int Failed, bool Completed);

internal static partial class SidecarFormatLog
{
    /// <summary>Category (the "App" source tag) for sidecar format lines.</summary>
    public const string Category = "App.DemoCache";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "{fileName}: the new sidecar did not read back; the pre-gzip file is kept")]
    public static partial void KeptLegacy(ILogger logger, string fileName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{fileName}: pre-gzip sidecar does not read; kept")]
    public static partial void LegacyUnreadable(ILogger logger, string fileName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "{fileName}: sidecar conversion failed")]
    public static partial void ConversionFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "sidecar format pass: {demos} demos, {converted} files converted, {failed} kept")]
    public static partial void PassFinished(ILogger logger, int demos, int converted, int failed);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "sidecar format pass stopped")]
    public static partial void PassFailed(ILogger logger, Exception exception);
}
