#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     The one-off rename of a hashed demo's cache files from the path's key to the content id, for an index
///     written before rows were keyed by content. The index is re-keyed in memory on load; until this pass
///     has moved a row's files, reads find them under the old names. No parse, no demo read.
///     <para>
///         Resumable: the re-keyed index, naming where each row's files still are, is saved before the first
///         file moves, a move whose source is gone is already done, and the marker
///         (<see cref="DemoCacheIndexFile.ContentKeyMigrationVersion" />) is set only by a pass that left
///         nothing under an old name.
///     </para>
/// </summary>
public static class ContentKeyMigration
{
    /// <summary>Revision of this pass. A cache at this revision has no file under a path key for a hashed row.</summary>
    public const int CurrentVersion = 1;

    private static ILogger? _diagLog;

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(SidecarFormatLog.Category);

    /// <summary>
    ///     Queues the pass as a processing queue item, unless the cache is already at <see cref="CurrentVersion" />.
    ///     Null when nothing was queued.
    /// </summary>
    /// <param name="queue">The processing queue.</param>
    /// <param name="cache">The demo cache.</param>
    /// <param name="batchSize">Rows between steps aside.</param>
    public static IDemoQueueHandle? Submit(IDemoProcessingQueue queue, DemoCacheStore cache, int batchSize = 64)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.CacheRoot is null || cache.ContentKeyMigrationVersion >= CurrentVersion)
        {
            return null;
        }

        return queue.SubmitJob(new QueueJobRequest(QueueJobKind.SidecarMigration, "Demo cache: name files by content",
            "demo-cache", DemoJobPriority.Background,
            async job =>
            {
                await RunAsync(cache, job.StepAsideAsync,
                    (done, total) => job.Report(done, total, $"{done} of {total} demos"), batchSize,
                    job.CancellationToken).ConfigureAwait(false);
                job.CancellationToken.ThrowIfCancellationRequested();
            },
            Key: "content-key"));
    }

    /// <summary>Moves every unsettled row's files, stepping aside between batches for a demo open.</summary>
    /// <param name="cache">The demo cache.</param>
    /// <param name="betweenBatches">Runs before every batch after the first; null runs straight through.</param>
    /// <param name="progress">Rows done of the total, before each batch.</param>
    /// <param name="batchSize">Rows per batch.</param>
    /// <param name="cancellationToken">Stops between rows.</param>
    internal static async Task<ContentKeyMigrationResult> RunAsync(
        DemoCacheStore cache,
        Func<Task>? betweenBatches = null,
        Action<int, int>? progress = null,
        int batchSize = 64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.CacheRoot is null || cache.ContentKeyMigrationVersion >= CurrentVersion)
        {
            return new ContentKeyMigrationResult(0, 0, false);
        }

        int settled = 0, failed = 0;
        try
        {
            // Nothing moves until the index that says where every row's files are is on disk.
            if (!cache.TrySaveIndex())
            {
                return new ContentKeyMigrationResult(0, 0, false);
            }

            List<string> rows = cache.UnsettledRows();
            for (int start = 0; start < rows.Count; start += Math.Max(1, batchSize))
            {
                progress?.Invoke(start, rows.Count);
                if (start > 0 && betweenBatches is not null)
                {
                    await betweenBatches().ConfigureAwait(false);
                }

                foreach (string row in rows.Skip(start).Take(Math.Max(1, batchSize)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cache.SettleRow(row))
                    {
                        settled++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            bool completed = failed == 0;
            if (completed)
            {
                cache.ContentKeyMigrationVersion = CurrentVersion;
            }

            completed &= cache.TrySaveIndex();
            ContentKeyLog.PassFinished(Log, settled, failed);
            return new ContentKeyMigrationResult(settled, failed, completed);
        }
        catch (OperationCanceledException)
        {
            return new ContentKeyMigrationResult(settled, failed, false);
        }
        catch (Exception ex)
        {
            ContentKeyLog.PassFailed(Log, ex);
            return new ContentKeyMigrationResult(settled, failed, false);
        }
    }
}

/// <summary>One pass's tally. <paramref name="Completed" /> is true when the marker was saved.</summary>
internal sealed record ContentKeyMigrationResult(int Settled, int Failed, bool Completed);

internal static partial class ContentKeyLog
{
    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "content key pass: {settled} demos renamed, {failed} left under the old name")]
    public static partial void PassFinished(ILogger logger, int settled, int failed);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "content key pass stopped")]
    public static partial void PassFailed(ILogger logger, Exception exception);
}
