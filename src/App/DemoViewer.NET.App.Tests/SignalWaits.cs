#region

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Signal-driven waits for the view-model landings the app suite used to sample for. Three rules
///     are load-bearing: subscribe BEFORE the first condition check, because a landing that already
///     happened raises nothing; treat the signal as a wake-up only and re-read the condition, because
///     none of these carries the value being waited on; and keep every budget well under
///     <c>HeadlessSession</c>'s four-minute dispatch budget, which a body that outlasts it wedges for
///     every later UI test.
/// </summary>
internal static class SignalWaits
{
    /// <summary>
    ///     Waits for <paramref name="condition" /> to hold, woken by <paramref name="source" />'s change
    ///     notifications rather than by sampling.
    /// </summary>
    public static async Task UntilAsync(
        this INotifyPropertyChanged source,
        Func<bool> condition,
        int timeoutMs = 30_000,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        TaskCompletionSource landed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (condition())
            {
                landed.TrySetResult();
            }
        }

        source.PropertyChanged += OnChanged;
        try
        {
            await Settle(landed, condition, timeoutMs, $"`{expression}`");
        }
        finally
        {
            source.PropertyChanged -= OnChanged;
        }
    }

    /// <summary>
    ///     Waits for the authoritative tracker to land on <paramref name="frameIndex" />. A discrete seek
    ///     publishes its freshly-built tracker and then posts <c>Advanced</c>, so that push is the only
    ///     signal that the TRACKER has moved. The controller's own position moves earlier and under its
    ///     own notification, which is why this cannot wait on <c>CurrentFrameIndex</c>.
    /// </summary>
    public static async Task TrackerAtFrameAsync(
        this PlaybackController playback,
        int frameIndex,
        int timeoutMs = 30_000)
    {
        bool Landed() => playback.AuthoritativeTracker?.CurrentFrameIndex == frameIndex;

        TaskCompletionSource landed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnAdvanced(PlaybackFrame frame)
        {
            if (Landed())
            {
                landed.TrySetResult();
            }
        }

        playback.Advanced += OnAdvanced;
        try
        {
            await Settle(landed, Landed, timeoutMs, $"the authoritative tracker to reach frame {frameIndex}");
        }
        finally
        {
            playback.Advanced -= OnAdvanced;
        }
    }

    private static async Task Settle(
        TaskCompletionSource landed,
        Func<bool> condition,
        int timeoutMs,
        string what)
    {
        if (condition())
        {
            Drain();
            return;
        }

        try
        {
            await landed.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"timed out waiting for {what} after {timeoutMs}ms");
        }

        Drain();
    }

    // The signal continuation can resume ahead of lower-priority work the landing also queued, and
    // callers read view-model state on the next line. Off the UI thread there is nothing to drain.
    private static void Drain()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }
}
