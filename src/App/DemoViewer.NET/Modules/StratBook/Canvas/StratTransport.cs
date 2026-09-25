#region

using System.Diagnostics;
using Avalonia.Threading;
using DemoViewer.NET.Playback2D.Core.Keyframes;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>A repeating callback with the seconds since the previous one: the transport's clock source.</summary>
public interface IStratTicker
{
    /// <summary>Starts calling <paramref name="onTick" />; disposing the result stops it.</summary>
    /// <param name="onTick">Called on the UI thread with the elapsed seconds.</param>
    IDisposable Start(Action<double> onTick);
}

/// <summary>
///     The strat canvas's private clock (step-authoring.md §3.10): play, pause, step, speed and seek over the
///     strat frame clock at <see cref="StepSchedule.TicksPerSecond" />. It is deliberately not
///     <c>IModuleContext.RequestSeekToFrame</c>: a strat has no demo, and a seek here must never move the
///     shared playback clock or LiveSync.
///     <para>
///         The position is fractional so a slow speed still advances; <see cref="Tick" /> is what is shown.
///         Playing stops at <see cref="EndTick" />, and play from the end starts over at
///         <see cref="StartTick" />, the way a clip plays.
///     </para>
/// </summary>
public sealed class StratTransport : IDisposable
{
    /// <summary>The speeds Up and Down step through.</summary>
    public static readonly IReadOnlyList<double> Speeds = [0.25, 0.5, 1, 2, 4];

    private readonly IStratTicker _ticker;
    private double _position;
    private IDisposable? _running;

    /// <param name="ticker">The clock source; a dispatcher timer at the display rate when omitted.</param>
    public StratTransport(IStratTicker? ticker = null) => _ticker = ticker ?? new DispatcherTicker();

    /// <summary>The shown tick.</summary>
    public int Tick => (int)Math.Floor(_position);

    /// <summary>Where playback starts over.</summary>
    public int StartTick { get; private set; }

    /// <summary>Where playback stops: the last step's tick.</summary>
    public int EndTick { get; private set; }

    public bool IsPlaying => _running is not null;

    /// <summary>Playback rate; 1 is realtime.</summary>
    public double Speed { get; private set; } = 1;

    /// <summary>The seconds the last advance covered: the frame's delta, 0 after a seek.</summary>
    public double LastDeltaSeconds { get; private set; }

    /// <inheritdoc />
    public void Dispose() => Pause();

    /// <summary>
    ///     Raised when the shown tick or the play state changes. The flag is true for a jump (seek, step,
    ///     range change) and false while playing, which is what the scene's marker smoothing keys off.
    /// </summary>
    public event Action<bool>? Changed;

    /// <summary>Sets the playable range, keeping the position inside it.</summary>
    /// <param name="startTick">The first tick.</param>
    /// <param name="endTick">The last tick.</param>
    public void SetRange(int startTick, int endTick)
    {
        StartTick = Math.Max(0, startTick);
        EndTick = Math.Max(StartTick, endTick);
        double clamped = Math.Clamp(_position, 0, EndTick);
        if (!clamped.Equals(_position))
        {
            _position = clamped;
            LastDeltaSeconds = 0;
            Changed?.Invoke(true);
        }
    }

    /// <summary>Moves to a tick, clamped to <c>[0, EndTick]</c>. Keeps playing if it was.</summary>
    /// <param name="tick">The tick.</param>
    public void Seek(int tick)
    {
        _position = Math.Clamp(tick, 0, EndTick);
        LastDeltaSeconds = 0;
        Changed?.Invoke(true);
    }

    /// <summary>One tick forward or back.</summary>
    /// <param name="delta">+1 or -1.</param>
    public bool Step(int delta)
    {
        int target = Math.Clamp(Tick + delta, 0, EndTick);
        if (target == Tick)
        {
            return false;
        }

        Pause();
        Seek(target);
        return true;
    }

    public void Play()
    {
        if (IsPlaying)
        {
            return;
        }

        if (Tick >= EndTick)
        {
            _position = StartTick;
        }

        _running = _ticker.Start(Advance);
        Changed?.Invoke(true);
    }

    public void Pause()
    {
        if (_running is null)
        {
            return;
        }

        _running.Dispose();
        _running = null;
        Changed?.Invoke(false);
    }

    public void TogglePlay()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Steps through <see cref="Speeds" />; false at either end.</summary>
    /// <param name="direction">+1 faster, -1 slower.</param>
    public bool StepSpeed(int direction)
    {
        int index = Speeds.ToList().IndexOf(Speed);
        int next = Math.Clamp((index < 0 ? 2 : index) + direction, 0, Speeds.Count - 1);
        if (Speeds[next].Equals(Speed))
        {
            return false;
        }

        Speed = Speeds[next];
        Changed?.Invoke(false);
        return true;
    }

    /// <summary>Moves the clock on by wall seconds at the current speed; stops at the end. The ticker calls this.</summary>
    /// <param name="seconds">Elapsed wall time.</param>
    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return;
        }

        _position = Math.Min(EndTick, _position + seconds * Speed * StepSchedule.TicksPerSecond);
        LastDeltaSeconds = seconds;
        bool ended = _position >= EndTick;
        Changed?.Invoke(false);
        if (ended)
        {
            Pause();
        }
    }

    // A DispatcherTimer at roughly the display rate, with the real elapsed time measured rather than the
    // interval assumed: a timer that slips must not slow the strat down.
    private sealed class DispatcherTicker : IStratTicker
    {
        public IDisposable Start(Action<double> onTick)
        {
            Stopwatch clock = Stopwatch.StartNew();
            double last = 0;
            DispatcherTimer timer = new(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                double now = clock.Elapsed.TotalSeconds;
                double elapsed = Math.Min(0.25, now - last);
                last = now;
                onTick(elapsed);
            });
            timer.Start();
            return new Stopper(timer);
        }

        private sealed class Stopper(DispatcherTimer timer) : IDisposable
        {
            public void Dispose() => timer.Stop();
        }
    }
}
