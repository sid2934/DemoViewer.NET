#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>One migration pass's tally. <paramref name="Completed" /> is true when the marker was written.</summary>
public sealed record GrenadeStoreMigrationResult(int Demos, int Converted, int Failed, int PathsStored, int PathFilesDeleted, bool Completed);

/// <summary>
///     The one-off move to the throw log and the lineup store (grenades-v2.md §3). No parse. Per demo, the
///     JSON rows become a throw log with the thrower names from the record, and the JSON goes only once the
///     log decodes to the same rows. Then the lineup store takes one flight per lineup technique from the old
///     paths siblings, and those are deleted only after the store reads back with them. Marker-gated once a
///     pass finishes with no failures; a rerun redoes only what is left.
/// </summary>
public static class GrenadeStoreMigration
{
    /// <summary>Written into the cache root when a pass converted everything it found.</summary>
    public const string MarkerFileName = "grenades-v3.done";

    private static ILogger? _diagLog;
    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(GrenadeIndexLog.Category);

    /// <summary>Queues the pass as a processing queue item, unless the marker says it is done. Null when nothing was queued.</summary>
    public static IDemoQueueHandle? Submit(IDemoProcessingQueue queue, DemoCacheStore cache, GrenadeIndex index)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(index);
        if (cache.CacheRoot is not { } root || File.Exists(Path.Combine(root, MarkerFileName)))
        {
            return null;
        }

        return queue.SubmitJob(new QueueJobRequest(QueueJobKind.SidecarMigration, "Grenades: compact stored throws",
            "demo-cache", DemoJobPriority.Background,
            async job =>
            {
                await RunAsync(cache, index, job.StepAsideAsync,
                    (done, total) => job.Report(done, total, $"{done} of {total} demos"), cancellationToken: job.CancellationToken)
                    .ConfigureAwait(false);
                job.CancellationToken.ThrowIfCancellationRequested();
            },
            Key: "grenade-store"));
    }

    /// <summary>Runs the pass. Waits for the index's first load, since the harvest needs its lineups.</summary>
    public static async Task<GrenadeStoreMigrationResult> RunAsync(
        DemoCacheStore cache,
        GrenadeIndex index,
        Func<Task>? betweenBatches = null,
        Action<int, int>? progress = null,
        int batchSize = 32,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(index);
        if (cache.CacheRoot is not { } root || File.Exists(Path.Combine(root, MarkerFileName)))
        {
            return new GrenadeStoreMigrationResult(0, 0, 0, 0, 0, false);
        }

        await index.WhenLoaded.WaitAsync(cancellationToken).ConfigureAwait(false);
        int converted = 0, failed = 0, demos = 0;
        List<string> paths = [.. cache.Index.Where(e => e.IsGrenadesCurrent(GrenadeWalker.Version)).Select(e => e.Path)];
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
                try
                {
                    switch (GrenadeSidecar.ConvertToLog(cache, path))
                    {
                        case SidecarConversion.Converted:
                            converted++;
                            break;
                        case SidecarConversion.Failed:
                            failed++;
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    string fileName = Path.GetFileName(path);
                    GrenadeStoreLog.ConversionFailed(Log, fileName, ex);
                }
            }
        }

        progress?.Invoke(paths.Count, paths.Count);

        // The rows now carry names: reload so the index serves them, then harvest the flights.
        await Task.Run(index.Load, cancellationToken).ConfigureAwait(false);
        int stored = await Task.Run(() => index.HarvestStoredPaths(path => GrenadeSidecar.TryReadPaths(cache, path)), cancellationToken)
            .ConfigureAwait(false);

        int deleted = 0;
        if (index.LineupStore.ReadsBack())
        {
            foreach (DemoCacheIndexEntry entry in cache.Index)
            {
                if (cache.TryReadSiblingBytes(entry.Path, GrenadeSidecar.PathsSuffix) is not null
                    || cache.TryReadSiblingBytes(entry.Path, GrenadeSidecar.LegacyPathsSuffix) is not null)
                {
                    cache.DeleteSibling(entry.Path, GrenadeSidecar.PathsSuffix);
                    cache.DeleteSibling(entry.Path, GrenadeSidecar.LegacyPathsSuffix);
                    deleted++;
                }
            }
        }
        else
        {
            failed++;
            GrenadeStoreLog.StoreDidNotReadBack(Log);
        }

        bool completed = failed == 0;
        if (completed)
        {
            File.WriteAllText(Path.Combine(root, MarkerFileName), DateTime.UtcNow.ToString("O"));
        }

        GrenadeStoreLog.PassFinished(Log, demos, converted, failed, stored, deleted);
        return new GrenadeStoreMigrationResult(demos, converted, failed, stored, deleted, completed);
    }
}

internal static partial class GrenadeStoreLog
{
    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "{fileName}: grenade rows not converted to a throw log")]
    public static partial void ConversionFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "lineup store did not read back; paths files kept")]
    public static partial void StoreDidNotReadBack(ILogger logger);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information,
        Message = "grenade store pass: {demos} demos, {converted} converted, {failed} kept, {stored} flights stored, {deleted} paths files deleted")]
    public static partial void PassFinished(ILogger logger, int demos, int converted, int failed, int stored, int deleted);
}
