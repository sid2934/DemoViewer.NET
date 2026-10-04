#region

using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.Export.Pack;

/// <summary>Runs a Pack Export as a user-requested item of the processing queue.</summary>
internal static class PackExportQueue
{
    /// <summary>
    ///     Queues <paramref name="export" /> and completes with its result. Cancelling <paramref name="ct" /> cancels
    ///     the queue item, queued or running; cancelling the item from the queue list cancels the export.
    /// </summary>
    /// <param name="queue">The processing queue.</param>
    /// <param name="title">The queue list's line.</param>
    /// <param name="outputPath">The file being written, for the row tooltip.</param>
    /// <param name="export">The export. It takes its own gate slots (an export session, an interactive slot per parse).</param>
    /// <param name="progress">The caller's progress sink.</param>
    /// <param name="ct">The caller's cancel.</param>
    public static Task<T> RunAsync<T>(IDemoProcessingQueue queue, string title, string? outputPath,
        Func<IProgress<PackProgress>, CancellationToken, Task<T>> export, IProgress<PackProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(export);
        TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDemoQueueHandle handle = queue.SubmitJob(new QueueJobRequest(QueueJobKind.PackExport, title, "review",
            DemoJobPriority.UserRequested, async job =>
            {
                // Holding the queue's slot would block the export's own interactive slot at one-at-a-time.
                job.ReleaseSlot();
                try
                {
                    T value = await export(new Relay(progress, job), job.CancellationToken).ConfigureAwait(false);
                    result.TrySetResult(value);
                }
                catch (OperationCanceledException)
                {
                    result.TrySetCanceled(job.CancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    result.TrySetException(ex);
                    throw;
                }
            }, Target: outputPath));

        CancellationTokenRegistration registration = ct.Register(handle.Cancel);
        _ = handle.Completion.ContinueWith(_ =>
        {
            registration.Dispose();
            result.TrySetCanceled(); // cancelled or refused before it ran
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return result.Task;
    }

    private sealed class Relay(IProgress<PackProgress>? inner, IQueueJobContext job) : IProgress<PackProgress>
    {
        public void Report(PackProgress value)
        {
            inner?.Report(value);
            job.Report(value.Segment, value.SegmentCount, value.Label);
        }
    }
}
