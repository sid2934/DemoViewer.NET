namespace DemoViewer.NET.AppTests;

/// <summary>
///     Wakes a test when something it waits on may have changed, so it re-checks its condition on the change
///     instead of on a timer.
/// </summary>
internal sealed class ChangeSignal
{
    // A genuine hang fails after this rather than stalling the suite. It is never the normal path.
    internal static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private TaskCompletionSource _next = New();

    public void Raise() => Interlocked.Exchange(ref _next, New()).TrySetResult();

    /// <summary>Returns once <paramref name="until" /> holds, re-checking after each <see cref="Raise" />.</summary>
    public async Task WaitUntilAsync(Func<bool> until, string what, DateTime? deadline = null)
    {
        DateTime limit = deadline ?? DateTime.UtcNow + Backstop;
        while (true)
        {
            // Taken before the check, so a raise between the check and the wait is not lost.
            Task changed = Volatile.Read(ref _next).Task;
            if (until())
            {
                return;
            }

            TimeSpan left = limit - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            try
            {
                await changed.WaitAsync(left);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }
        }
    }

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
