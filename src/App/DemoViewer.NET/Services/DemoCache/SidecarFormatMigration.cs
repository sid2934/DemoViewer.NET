#region

using CS2DemoKit.Analysis.Diagnostics;
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
    ///     Converts every indexed demo, a batch at a time inside a background slot so an interactive load
    ///     or a reel session preempts it between batches.
    /// </summary>
    /// <param name="cache">The demo cache.</param>
    /// <param name="converters">Per-demo converters, run in order.</param>
    /// <param name="acquireSlot">A background slot per batch (the heavy-job gate); null runs unthrottled.</param>
    /// <param name="startDelay">Waits this long first, so startup loads are not competing for the disk.</param>
    /// <param name="batchSize">Demos per slot.</param>
    /// <param name="cancellationToken">Stops between demos.</param>
    public static async Task<SidecarFormatResult> RunAsync(
        DemoCacheStore cache,
        IReadOnlyList<Func<string, SidecarConversion>> converters,
        Func<CancellationToken, Task<IDisposable>>? acquireSlot = null,
        TimeSpan startDelay = default,
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
            if (startDelay > TimeSpan.Zero)
            {
                await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);
            }

            List<string> paths = [.. cache.Index.Select(e => e.Path)];
            for (int start = 0; start < paths.Count; start += Math.Max(1, batchSize))
            {
                using IDisposable? slot = acquireSlot is null ? null : await acquireSlot(cancellationToken).ConfigureAwait(false);
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
