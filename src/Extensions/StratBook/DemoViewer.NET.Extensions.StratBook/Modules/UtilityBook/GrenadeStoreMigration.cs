#region

using System.Text.Json;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>One migration pass's tally.</summary>
/// <param name="Demos">Demos with current grenade rows.</param>
/// <param name="Converted">Rows files converted to throw logs.</param>
/// <param name="Failed">Rows files kept because the log did not check out, plus a store that did not read back.</param>
/// <param name="PathsStored">Flights the store took from paths files.</param>
/// <param name="PathFilesDeleted">Paths files deleted.</param>
/// <param name="Completed">True when the marker was written.</param>
/// <param name="PathFilesKept">Paths files kept for a retry: unreadable, or holding a flight the store lacks.</param>
/// <param name="PathFilesGivenUp">Paths files kept for good after <see cref="GrenadeStoreMigration.MaxAttempts" /> passes.</param>
public sealed record GrenadeStoreMigrationResult(int Demos, int Converted, int Failed, int PathsStored, int PathFilesDeleted,
    bool Completed, int PathFilesKept = 0, int PathFilesGivenUp = 0);

/// <summary>
///     The one-off move to the throw log and the lineup store. No parse. Per demo, the
///     JSON rows become a throw log with the thrower names from the record, and the JSON goes only once the
///     log decodes to the same rows. Then the lineup store takes one flight per lineup technique from the old
///     paths siblings, and those are deleted only after the store reads back with them. Marker-gated once a
///     pass finishes with no failures; a rerun redoes only what is left.
/// </summary>
public static class GrenadeStoreMigration
{
    /// <summary>Written into the cache root when a pass converted everything it found.</summary>
    public const string MarkerFileName = "grenades-v3.done";

    /// <summary>Per demo, how many passes kept its paths file. In the cache root.</summary>
    public const string AttemptsFileName = "grenades-v3.attempts.json";

    /// <summary>Passes a paths file may block the marker; after that it is kept and no longer retried.</summary>
    public const int MaxAttempts = 3;

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
        (int stored, HashSet<string> readable) = await Task.Run(
                () => index.HarvestStoredPaths(paths, path => GrenadeSidecar.TryReadPaths(cache, path)), cancellationToken)
            .ConfigureAwait(false);

        int deleted = 0, kept = 0, givenUp = 0;
        if (index.LineupStore.ReadsBack())
        {
            Dictionary<string, int> attempts = ReadAttempts(root);
            foreach (string path in paths)
            {
                // Only a file that read, and whose every position already has a flight, goes.
                bool keep = !readable.Contains(path) || !index.FlightsCovered(path);
                lock (cache.StripeFor(path))
                {
                    if (cache.TryReadSiblingBytes(path, GrenadeSidecar.PathsSuffix) is null
                        && cache.TryReadSiblingBytes(path, GrenadeSidecar.LegacyPathsSuffix) is null)
                    {
                        continue;
                    }

                    if (!keep)
                    {
                        cache.DeleteSibling(path, GrenadeSidecar.PathsSuffix);
                        cache.DeleteSibling(path, GrenadeSidecar.LegacyPathsSuffix);
                        attempts.Remove(path);
                        deleted++;
                    }
                }

                if (keep)
                {
                    int tries = attempts.GetValueOrDefault(path) + 1;
                    attempts[path] = tries;
                    if (tries >= MaxAttempts)
                    {
                        givenUp++;
                        string fileName = Path.GetFileName(path);
                        GrenadeStoreLog.PathsGivenUp(Log, fileName, tries);
                    }
                    else
                    {
                        kept++;
                    }
                }
            }

            WriteAttempts(root, attempts);
        }
        else
        {
            failed++;
            GrenadeStoreLog.StoreDidNotReadBack(Log);
        }

        bool completed = failed == 0 && kept == 0;
        if (completed)
        {
            File.WriteAllText(Path.Combine(root, MarkerFileName), DateTime.UtcNow.ToString("O"));
        }

        GrenadeStoreLog.PassFinished(Log, demos, converted, failed, stored, deleted);
        return new GrenadeStoreMigrationResult(demos, converted, failed, stored, deleted, completed, kept, givenUp);
    }

    private static Dictionary<string, int> ReadAttempts(string root)
    {
        try
        {
            string file = Path.Combine(root, AttemptsFileName);
            return File.Exists(file)
                ? new Dictionary<string, int>(
                    JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file)) ?? [], StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteAttempts(string root, Dictionary<string, int> attempts)
    {
        string file = Path.Combine(root, AttemptsFileName);
        if (attempts.Count == 0)
        {
            File.Delete(file);
            return;
        }

        DemoCacheStore.WriteAtomic(file, JsonSerializer.Serialize(attempts));
    }
}

internal static partial class GrenadeStoreLog
{
    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "{fileName}: grenade rows not converted to a throw log")]
    public static partial void ConversionFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "lineup store did not read back; paths files kept")]
    public static partial void StoreDidNotReadBack(ILogger logger);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
        Message = "{fileName}: paths file kept after {attempts} passes (unreadable, or a flight the store lacks); no longer retried")]
    public static partial void PathsGivenUp(ILogger logger, string fileName, int attempts);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information,
        Message = "grenade store pass: {demos} demos, {converted} converted, {failed} kept, {stored} flights stored, {deleted} paths files deleted")]
    public static partial void PassFinished(ILogger logger, int demos, int converted, int failed, int stored, int deleted);
}
