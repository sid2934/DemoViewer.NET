#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     Deletes the demos the cache has kept orphaned for longer than the grace period, with their files, as a
///     light processing queue item. Reads no demo. Safe to run at any time: a demo a path took back meanwhile
///     is left alone, and one cut short by a crash is finished by the next run.
/// </summary>
public static class OrphanSweep
{
    private static ILogger? _diagLog;

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(SidecarFormatLog.Category);

    /// <summary>Queues the sweep. Null when there is no cache on disk.</summary>
    /// <param name="queue">The processing queue.</param>
    /// <param name="cache">The demo cache.</param>
    /// <param name="grace">How long an orphaned demo is kept.</param>
    /// <param name="utcNow">The clock; null reads <see cref="DateTime.UtcNow" />.</param>
    public static IDemoQueueHandle? Submit(IDemoProcessingQueue queue, DemoCacheStore cache, TimeSpan grace,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.CacheRoot is null)
        {
            return null;
        }

        return queue.SubmitJob(new QueueJobRequest(QueueJobKind.CacheSweep, "Demo cache: drop demos gone past the grace period",
            "demo-cache", DemoJobPriority.Background,
            _ =>
            {
                Run(cache, grace, (utcNow ?? (() => DateTime.UtcNow))());
                return Task.CompletedTask;
            },
            Key: "orphan-sweep"));
    }

    /// <summary>Expires what is due and saves the index when anything went.</summary>
    /// <param name="cache">The demo cache.</param>
    /// <param name="grace">How long an orphaned demo is kept.</param>
    /// <param name="nowUtc">The current time, UTC.</param>
    /// <returns>How many demos went.</returns>
    internal static int Run(DemoCacheStore cache, TimeSpan grace, DateTime nowUtc)
    {
        int expired = cache.ExpireOrphans(nowUtc, grace < TimeSpan.Zero ? TimeSpan.Zero : grace);
        if (expired > 0)
        {
            cache.SaveIndex();
            OrphanSweepLog.Swept(Log, expired);
        }

        return expired;
    }
}

internal static partial class OrphanSweepLog
{
    [LoggerMessage(EventId = 13, Level = LogLevel.Information,
        Message = "demo cache: {count} demos no folder listed past the grace period were deleted")]
    public static partial void Swept(ILogger logger, int count);
}
