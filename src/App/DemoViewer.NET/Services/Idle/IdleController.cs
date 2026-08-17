#region

using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.Services.Idle;

/// <summary>
///     Watches for user inactivity and fires a single "enter idle" callback when the configured
///     <see cref="IdleSettings.IdleTimeoutWait" /> elapses with no interaction and nothing blocking (active
///     playback). All logic is App-layer and UI-thread; it touches no parser/entity state.
///     <para>
///         <b>Activity detection is the whole performance story.</b> The view attaches ONE set of tunneling
///         input handlers on the window's <c>TopLevel</c> (see <c>MainView</c> code-behind) that call
///         <see cref="NotifyActivity" /> — tunneling fires from the root before any control handles the
///         event, so it catches EVERY pointer / key / wheel interaction regardless of which control is the
///         target (clicking a message card, switching tabs, scrolling the hex view, typing in a filter) with
///         no per-control wiring (which would be fragile: every new control becomes a place to forget). The
///         per-event handler does exactly one thing: stamp <see cref="DateTime.UtcNow" /> into a field — a
///         single write, no allocation. The actual elapsed-time compare runs on a coarse poll timer, not per
///         input event.
///     </para>
///     <para>
///         <b>Wall-clock, not ticks.</b> Idle is measured with <see cref="DateTime" /> — "tick" in this
///         codebase means a CS2/demo discrete-time unit and is reserved for that. UTC is used so a DST shift
///         can't make an elapsed span jump or go negative.
///     </para>
/// </summary>
public sealed class IdleController : IDisposable
{
    // Coarse: idle timeouts are minutes, so a few seconds' granularity is imperceptible and the tick body is
    // near-free (one subtraction + two bool reads). Kept well below any realistic timeout.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<bool> _isBlocked;
    private readonly Action _onIdle;
    private readonly IOptionsMonitor<AppSettings> _options;

    private bool _isIdle;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _started;
    private DispatcherTimer? _timer;

    /// <summary>
    ///     Builds the controller. It does not observe anything until <see cref="Start" /> is called (the
    ///     desktop host wires that after the shell exists), so constructing it on a headless / WASM path is
    ///     inert.
    /// </summary>
    /// <param name="options">Live idle configuration (enable + timeout + background behaviour).</param>
    /// <param name="isBlocked">
    ///     Returns true when idle must NOT engage — the single "playback is running" signal
    ///     (<c>PlaybackController.IsPlaying</c>). Paused / ended playback returns false, so both correctly
    ///     become idle-eligible.
    /// </param>
    /// <param name="onIdle">
    ///     Invoked once, on the UI thread, when the app transitions into idle. The shell captures resume
    ///     state and closes the demo here. Not re-fired until <see cref="ClearIdle" /> is called.
    /// </param>
    public IdleController(IOptionsMonitor<AppSettings> options, Func<bool> isBlocked, Action onIdle)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isBlocked);
        ArgumentNullException.ThrowIfNull(onIdle);

        _options = options;
        _isBlocked = isBlocked;
        _onIdle = onIdle;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    /// <summary>
    ///     Begins watching: starts the poll timer (the view supplies interactions via
    ///     <see cref="NotifyActivity" />). Idempotent. Called by the desktop composition root only — the
    ///     browser host never starts idle mode. Creates the <see cref="DispatcherTimer" /> here (not in the
    ///     ctor) so constructing the controller needs no dispatcher.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _lastActivityUtc = DateTime.UtcNow;
        _timer = new DispatcherTimer
        {
            Interval = PollInterval
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    ///     Records that the user just interacted — resets the idle countdown. Public so a code path that is
    ///     not raw input (a programmatic action the app wants to count as activity) can also reset it; the
    ///     input hook already covers every real user interaction.
    /// </summary>
    public void NotifyActivity() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>
    ///     Leaves the idle state and restarts the countdown fresh. Called by the shell after the user chooses
    ///     Resume — until then, ongoing input is recorded but does NOT auto-dismiss idle (resume is explicit).
    /// </summary>
    public void ClearIdle()
    {
        _isIdle = false;
        _lastActivityUtc = DateTime.UtcNow;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (TryEnterIdle(DateTime.UtcNow))
        {
            _onIdle();
        }
    }

    // Testable decision core. When the app should transition into idle at <paramref name="nowUtc" />, it
    // flips the idle flag and returns true (the caller then invokes the enter-idle callback). For the
    // not-eligible cases (already idle / disabled / blocked) it refreshes the activity clock so the countdown
    // only ever measures a genuinely idle, enabled state — stamping while playback is running is what makes
    // the countdown start fresh at the moment playback pauses or ends. Isolated from the timer + callback so a
    // test can drive it with an explicit clock.
    internal bool TryEnterIdle(DateTime nowUtc)
    {
        // Already idle — wait for an explicit Resume. Input during idle is stamped but does not auto-resume.
        if (_isIdle)
        {
            return false;
        }

        IdleSettings s = _options.CurrentValue.Idle;
        if (!s.Enabled || s.IdleTimeoutWait <= TimeSpan.Zero || _isBlocked())
        {
            _lastActivityUtc = nowUtc;
            return false;
        }

        if (nowUtc - _lastActivityUtc < s.IdleTimeoutWait)
        {
            return false;
        }

        _isIdle = true;
        return true;
    }
}
