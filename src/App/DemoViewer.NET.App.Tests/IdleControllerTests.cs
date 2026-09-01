#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.Idle;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pure-logic coverage of <see cref="IdleController" />'s decision core (<c>TryEnterIdle</c>): the idle
///     state machine, driven with an explicit wall-clock so there is no timer / real-time dependency. Covers:
///     the timeout boundary, the master enable, the playback-blocked reset, fire-once semantics, and that
///     <c>ClearIdle</c> (Resume) re-arms the countdown. No Avalonia / headless session → runs in parallel.
/// </summary>
public class IdleControllerTests
{
    private static IdleController Build(AppSettings settings, Func<bool> isBlocked, Action? onIdle = null) =>
        new(new StubMonitor(settings), isBlocked, onIdle ?? (() => { }));

    [Test]
    public async Task DoesNotEnterIdle_BeforeTimeout()
    {
        IdleController c = Build(WithIdle(true, TimeSpan.FromMinutes(15)), () => false);
        c.NotifyActivity(); // stamps ~now (real UtcNow); we drive TryEnterIdle from T0-relative below

        // 14 minutes after the last activity is still under the 15-minute wait.
        await Assert.That(c.TryEnterIdle(NowAfter(c, TimeSpan.FromMinutes(14)))).IsFalse();
    }

    [Test]
    public async Task EntersIdle_AtTimeout()
    {
        IdleController c = Build(WithIdle(true, TimeSpan.FromMinutes(15)), () => false);
        c.NotifyActivity();

        await Assert.That(c.TryEnterIdle(NowAfter(c, TimeSpan.FromMinutes(15)))).IsTrue();
    }

    [Test]
    public async Task Disabled_NeverEntersIdle()
    {
        IdleController c = Build(WithIdle(false, TimeSpan.FromMinutes(1)), () => false);
        c.NotifyActivity();

        await Assert.That(c.TryEnterIdle(NowAfter(c, TimeSpan.FromHours(1)))).IsFalse();
    }

    [Test]
    public async Task Playback_BlocksIdle_AndResetsCountdown()
    {
        bool playing = true;
        IdleController c = Build(WithIdle(true, TimeSpan.FromMinutes(15)), () => playing);
        c.NotifyActivity();

        // While playing, a tick well past the timeout does NOT go idle, and it re-stamps activity, so the
        // countdown starts fresh from that moment. Simulate the pause happening at +30m.
        DateTime pausedAt = NowAfter(c, TimeSpan.FromMinutes(30));
        await Assert.That(c.TryEnterIdle(pausedAt)).IsFalse().Because("active playback blocks idle");

        // Playback stops. 14 minutes after the pause is still under the wait (countdown restarted at pause).
        playing = false;
        await Assert.That(c.TryEnterIdle(pausedAt + TimeSpan.FromMinutes(14))).IsFalse();
        // 15 minutes after the pause: now idle-eligible.
        await Assert.That(c.TryEnterIdle(pausedAt + TimeSpan.FromMinutes(15))).IsTrue();
    }

    [Test]
    public async Task FiresOnce_ThenReArmsAfterClearIdle()
    {
        IdleController c = Build(WithIdle(true, TimeSpan.FromMinutes(15)), () => false);
        c.NotifyActivity();

        DateTime entered = NowAfter(c, TimeSpan.FromMinutes(20));
        await Assert.That(c.TryEnterIdle(entered)).IsTrue();
        // Already idle: a later tick does not re-fire.
        await Assert.That(c.TryEnterIdle(entered + TimeSpan.FromMinutes(20))).IsFalse();

        // Resume re-arms; NotifyActivity restarts the countdown from the resume moment.
        c.ClearIdle();
        c.NotifyActivity();
        await Assert.That(c.TryEnterIdle(NowAfter(c, TimeSpan.FromMinutes(14)))).IsFalse().Because("countdown reset");
        // 15 minutes after resuming, it is idle-eligible again.
        await Assert.That(c.TryEnterIdle(NowAfter(c, TimeSpan.FromMinutes(15)))).IsTrue();
    }

    // The controller stamps _lastActivityUtc with real DateTime.UtcNow on NotifyActivity; drive TryEnterIdle
    // relative to "now" so the tiny real-time delta between NotifyActivity and the assert is irrelevant.
    private static DateTime NowAfter(IdleController _, TimeSpan delta) => DateTime.UtcNow + delta;

    private static AppSettings WithIdle(bool enabled, TimeSpan wait) =>
        new()
        {
            Idle = new IdleSettings
            {
                Enabled = enabled,
                IdleTimeoutWait = wait
            }
        };

    private sealed class StubMonitor(AppSettings value) : IOptionsMonitor<AppSettings>
    {
        public AppSettings CurrentValue { get; } = value;
        public AppSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }
}
