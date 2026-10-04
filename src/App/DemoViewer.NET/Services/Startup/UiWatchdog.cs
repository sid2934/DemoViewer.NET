#region

using Avalonia.Threading;

#endregion

namespace DemoViewer.NET.Services.Startup;

/// <summary>
///     Pings the UI thread from a timer thread and tells the <see cref="LaunchGuard" /> when it stops answering
///     and when it answers again. A freeze the user ends by killing the app then reads as one at the next launch.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private readonly Action<bool> _report;
    private readonly Action<Action> _post;
    private readonly TimeSpan _limit;
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private long _lastAnswerTicks;
    private int _pending;
    private int _hung;

    /// <summary>Starts watching.</summary>
    /// <param name="report">Told true when the UI thread went quiet past <paramref name="limit" />, false when it answers again.</param>
    /// <param name="post">Runs an action on the UI thread; null for the dispatcher.</param>
    /// <param name="limit">How long the UI thread may be quiet; null for ten seconds.</param>
    /// <param name="clock">The clock; null for the system's.</param>
    public UiWatchdog(Action<bool> report, Action<Action>? post = null, TimeSpan? limit = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
        // Input priority: the ping measures whether the UI answers, not whether it is idle. Playback keeps the
        // dispatcher busy without freezing it, and a Background ping would starve there.
        _post = post ?? (action => Dispatcher.UIThread.Post(action, DispatcherPriority.Input));
        _limit = limit ?? TimeSpan.FromSeconds(10);
        _clock = clock ?? TimeProvider.System;
        _lastAnswerTicks = _clock.GetTimestamp();
        TimeSpan period = TimeSpan.FromTicks(Math.Max(_limit.Ticks / 5, TimeSpan.FromMilliseconds(100).Ticks));
        _timer = _clock.CreateTimer(_ => Tick(), null, period, period);
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    private void Tick()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _post(Answer);
        }

        bool quiet = _clock.GetElapsedTime(Interlocked.Read(ref _lastAnswerTicks)) > _limit;
        if (quiet && Interlocked.CompareExchange(ref _hung, 1, 0) == 0)
        {
            _report(true);
        }
    }

    private void Answer()
    {
        Interlocked.Exchange(ref _lastAnswerTicks, _clock.GetTimestamp());
        Interlocked.Exchange(ref _pending, 0);
        if (Interlocked.CompareExchange(ref _hung, 0, 1) == 1)
        {
            _report(false);
        }
    }
}
