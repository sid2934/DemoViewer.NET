#region

using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

internal static class QueueWaits
{
    /// <summary>
    ///     Waits for an idle moment of <paramref name="queue" /> at which <paramref name="and" /> holds. Fails
    ///     after <paramref name="timeoutMs" /> so a hang is a failure. <paramref name="and" /> must become
    ///     true through the queue's own work: it is only re-checked when the queue next goes idle.
    /// </summary>
    public static async Task IdleAsync(this DemoProcessingQueue queue, string what, Func<bool>? and = null, int timeoutMs = 5000)
    {
        try
        {
            await queue.WhenIdleAsync(and).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"timed out waiting for {what}");
        }
    }
}
