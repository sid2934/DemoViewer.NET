#region

using Avalonia.Threading;
using Cs2DemoKit.Parser;
using Cs2VideoGenerator.Core.Models;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The CS2→DV inbound half: a ~30 Hz UI-thread
///     <see cref="DispatcherTimer" /> (<c>Cs2EventPump</c>) that drains the latest-value tick
///     slot and drives the drift servo, the v1.0 inference watchdog, and the remote-apply of
///     v1.1 user-originated demo-state changes. Every DV playback mutation happens under the
///     <c>applyingRemote</c> flag AND writes the same values into the engine's desired state —
///     the observer sees no diff, no echo command (the loop breaker).
///     <para>
///         UI-thread resident: construct, feed (<see cref="OnDemoState" /> is posted by the
///         service), and dispose on the UI thread.
///     </para>
/// </summary>
internal sealed class InboundSync : IDisposable
{
    private readonly LiveSyncCapabilities _capabilities;
    private readonly SyncEngine _engine;
    private readonly Func<long?> _latestCs2Tick;
    private readonly Func<TickMapper?> _mapper;
    private readonly DispatcherTimer _pump;
    private readonly Func<LiveSyncState> _serviceState;
    private readonly Action<bool> _setApplyingRemote;
    private readonly MainViewModel _shell;

    private long? _lastSeenTick;
    private DateTime _lastTickChangeAt = DateTime.UtcNow;
    private bool _servoEngaged;

    public InboundSync(
        MainViewModel shell,
        SyncEngine engine,
        LiveSyncCapabilities capabilities,
        Func<long?> latestCs2Tick,
        Func<TickMapper?> mapper,
        Func<LiveSyncState> serviceState,
        Action<bool> setApplyingRemote)
    {
        _shell = shell;
        _engine = engine;
        _capabilities = capabilities;
        _latestCs2Tick = latestCs2Tick;
        _mapper = mapper;
        _serviceState = serviceState;
        _setApplyingRemote = setApplyingRemote;

        _pump = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _pump.Tick += (_, _) => PumpTick();
        _pump.Start();
    }

    public void Dispose() => _pump.Stop();

    // ── The 30 Hz pump ────────────────────────────────────────────────────────

    private void PumpTick()
    {
        if (_engine.VerificationInFlight)
        {
            // The range playback owns CS2 — its seeks are neither drift nor user intent.
            // Reset jump tracking so the range's moves never read as user seeks afterwards.
            _lastSeenTick = null;
            _servoEngaged = false;
            return;
        }

        LiveSyncState state = _serviceState();
        if (!state.IsSynced && state.Kind != LiveSyncStateKind.Degraded)
        {
            _lastSeenTick = null;
            _servoEngaged = false;
            return;
        }

        long? tick = _latestCs2Tick();
        if (tick is not long cs2Tick)
        {
            return;
        }

        bool advanced = _lastSeenTick != cs2Tick;
        if (advanced)
        {
            // A big unexplained move is a CS2-side user seek; a v1.0 backward jump to near 0 is
            // restart-like Degraded. The disambiguation itself is InboundLogic.ClassifyTickAdvance
            // (pure, tested) — this applies it.
            switch (InboundLogic.ClassifyTickAdvance(
                        cs2Tick, _lastSeenTick, _engine.HasPendingSeek, _capabilities.DemoStateEvents))
            {
                case InboundLogic.TickSignal.DemoStateUnknown:
                    _engine.NoteRemoteDemoStateUnknown();
                    break;

                case InboundLogic.TickSignal.UserSeek:
                    RemoteApplySeek(cs2Tick);
                    break;
            }

            _lastSeenTick = cs2Tick;
            _lastTickChangeAt = DateTime.UtcNow;
        }
        else if (InboundLogic.ShouldInferPause(
                     _capabilities.EnginePauseDetection,
                     state.Kind == LiveSyncStateKind.SyncedFollowing,
                     DateTime.UtcNow - _lastTickChangeAt))
        {
            // v1.0 inference: silence while believed playing — inferred pause (hollow dot). The
            // engine mirrors DV: pause the local playhead too, as remote intent.
            _engine.NoteInferredPause();
            RemoteApplyPause();
            return;
        }

        RunServo(state, cs2Tick);
    }

    private void RunServo(LiveSyncState state, long cs2Tick)
    {
        if (state.Kind != LiveSyncStateKind.SyncedFollowing || !_shell.Playback.IsPlaying)
        {
            RestoreServoSpeedIfEngaged();
            return;
        }

        if (_mapper() is not { } mapper)
        {
            return;
        }

        long error = mapper.DvTick(cs2Tick) - _shell.Playback.CurrentTick;
        (ServoLogic.Correction kind, double speed) = ServoLogic.Decide(error, _servoEngaged);
        switch (kind)
        {
            case ServoLogic.Correction.RestoreSpeed:
                ApplyRemote(() => _shell.Playback.Speed = 1.0);
                _servoEngaged = false;
                break;

            case ServoLogic.Correction.AdjustSpeed:
                ApplyRemote(() => _shell.Playback.Speed = speed);
                _servoEngaged = true;
                break;

            case ServoLogic.Correction.HardResync:
                // Only a large divergence pays the discrete-seek cost; play resumes so
                // following continues.
                ApplyRemote(() =>
                {
                    _shell.Playback.SeekToFrame(mapper.FrameIndexOf(checked((int)cs2Tick)));
                    _shell.Playback.Speed = 1.0;
                    _shell.Playback.PlayCommand.Execute(null);
                    _engine.SetDesiredTick(cs2Tick);
                    _engine.SetDesiredPlaying(true);
                });
                _servoEngaged = false;
                break;
        }
    }

    private void RestoreServoSpeedIfEngaged()
    {
        if (_servoEngaged)
        {
            ApplyRemote(() => _shell.Playback.Speed = 1.0);
            _servoEngaged = false;
        }
    }

    // ── v1.1 demo-state mirroring (primary path) ──────────────────────────────

    /// <summary>
    ///     UI thread (posted by the service). Mirrors USER-originated CS2 changes into DV —
    ///     the decision itself is <see cref="InboundLogic.Decide" /> (pure, tested); this applies it.
    /// </summary>
    public void OnDemoState(DemoState state)
    {
        if (_engine.VerificationInFlight)
        {
            return;
        }

        long? tickError = state.DemoTick is int remoteTick && _mapper() is { } errorMapper
            ? Math.Abs(errorMapper.DvTick(remoteTick) - _shell.Playback.CurrentTick)
            : null;

        InboundLogic.Decision decision = InboundLogic.Decide(
            state, _shell.Playback.IsPlaying, tickError,
            _capabilities.DemoIdentity, _shell.ModuleContext?.DemoPath,
            _capabilities.EnginePauseDetection);

        if (decision.DemoChangedPath is { } changedPath)
        {
            _engine.NoteRemoteDemoChanged(changedPath);
            return;
        }

        if (decision.SeekToTick is { } seekTo)
        {
            RemoteApplySeek(seekTo);
        }

        switch (decision.SetPlaying)
        {
            case false:
                RemoteApplyPause();
                break;
            case true:
                RemoteApplyPlay();
                break;
        }
    }

    // ── Remote-apply helpers (the loop breaker in action) ─────────────────────

    private void RemoteApplyPause() => ApplyRemote(() =>
    {
        _shell.Playback.PauseCommand.Execute(null);
        _engine.SetDesiredPlaying(false);
        if (_mapper() is { } mapper)
        {
            // Desired tick is CS2-tick space: the fallback maps DV's CurrentTick through the
            // mapper (offset applied once, pre-game negative sentinel clamped) — the raw frame
            // clock would seed a bogus seek target.
            _engine.SetDesiredTick(_latestCs2Tick() ?? mapper.Cs2TickFromDvTick(_shell.Playback.CurrentTick));
        }
    });

    private void RemoteApplyPlay() => ApplyRemote(() =>
    {
        _shell.Playback.PlayCommand.Execute(null);
        _engine.SetDesiredPlaying(true);
    });

    private void RemoteApplySeek(long cs2Tick) => ApplyRemote(() =>
    {
        if (_mapper() is { } mapper)
        {
            _shell.Playback.SeekToFrame(mapper.FrameIndexOf(checked((int)cs2Tick)));
        }

        _engine.SetDesiredTick(cs2Tick);
        _engine.SetDesiredPlaying(_shell.Playback.IsPlaying);
    });

    private void ApplyRemote(Action mutate)
    {
        _setApplyingRemote(true);
        try
        {
            mutate();
        }
        finally
        {
            _setApplyingRemote(false);
        }
    }
}
