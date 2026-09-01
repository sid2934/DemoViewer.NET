#region

using System.Globalization;
using Avalonia.Threading;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Proto;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The desktop live CS2 sync engine: the
///     <see cref="ILiveSyncService" /> the Desktop host injects through
///     <c>AppHostHooks.LiveSyncFactory</c>. This class owns the session lifecycle: the private
///     CSVG gRPC host (<see cref="CsvgWebHost" />), CS2/mock launch, state surface, and teardown.
///     The outbound sync pipeline (observer → reconciler → ledger → command pump) attaches
///     when a session comes up.
///     <para>
///         Threading: public members are UI-thread-first (commands/flyout call them);
///         <see cref="State" /> transitions are marshaled to the UI thread. CSVG's synchronous
///         <c>TickUpdated</c> hot path only writes a latest-value slot (inbound threading
///         rule: exception-free, no per-subscriber isolation upstream); its async events are
///         posted to the UI thread and never awaited inline on the gRPC read loop.
///     </para>
/// </summary>
public sealed class LiveSyncService : ILiveSyncService
{
    private const long NoTick = long.MinValue;
    private readonly object _disposeStart = new();

    // Serializes Enable/Disable/Dispose: the flyout can only issue one at a time, but the
    // shutdown path may race a user action.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly IOptionsMonitor<AppSettings>? _settings;
    private readonly IDisposable? _settingsChangeSub;

    private readonly MainViewModel _shell;
    private volatile bool _captureFrameworkLogs;
    private Task? _disposeTask;
    private bool _disposed;

    // The in-flight EnableAsync's cancellation: a CS2 launch holds the lifecycle gate for up
    // to ~2 min, so Disable/Dispose/reel-suspend cancel it rather than queueing behind it.
    private CancellationTokenSource? _enableCts;

    // Outbound pipeline: alive only while a session is up.
    private SyncEngine? _engine;

    private CsvgWebHost? _host;

    // Inbound pipeline: the 30 Hz Cs2EventPump + servo + mirroring.
    private InboundSync? _inbound;
    private long _lastTickSlot = NoTick;

    // Live-read log gate: the CSVG host's bridge reads these on every record (hot under a gRPC
    // stream), so they are cached fields updated once per settings change via OnChange, not a
    // per-record IOptionsMonitor.CurrentValue lookup. The OnChange subscription is disposed on
    // teardown; it outlives per-reconnect bridges (a fresh host+bridge is built each Enable).
    private volatile LogLevel _logMinLevel = LogLevel.Information;
    private SyncStateObserver? _observer;
    private Func<CsvgSessionState, CsvgSessionState, Task>? _onClientStateChanged;
    private Func<string, DemoState, Task>? _onDemoStateChanged;
    private Func<string, DemoPlaybackStatusChange, Task>? _onPlaybackStatusChanged;
    private Func<string, Cs2ProcessStatus, Task>? _onProcessStatusChanged;
    private Action<string, long>? _onTickUpdated;

    private CsvgVideoSession? _subscribedSession;
    private bool _tearingDown;

    /// <summary>
    ///     Creates the engine against the shell. Settings resolve from the app's composition root
    ///     when not passed explicitly (tests pass their own monitor).
    /// </summary>
    public LiveSyncService(MainViewModel shell, IOptionsMonitor<AppSettings>? settings = null)
    {
        _shell = shell;
        _settings = settings ?? App.Services?.GetService<IOptionsMonitor<AppSettings>>();

        ApplyLogGate((_settings?.CurrentValue ?? new AppSettings()).LiveSync);
        _settingsChangeSub = _settings?.OnChange(s => ApplyLogGate(s.LiveSync));
    }

    // Loop breaker: set around engine-driven PlaybackController mutations so the
    // observer sees no new intent.
    private bool ApplyingRemote { get; set; }

    /// <summary>
    ///     True while this service still owns session resources, most importantly the gRPC host
    ///     and its exclusive port 50051. Broader than <see cref="LiveSyncState.IsSessionActive" />:
    ///     the host is deliberately kept alive across <see cref="LiveSyncStateKind.Faulted" /> for
    ///     fast retry, so a reel job must suspend on THIS, not on the state kind. Otherwise its
    ///     own host start hits the port and fails blaming "another program".
    /// </summary>
    internal bool OwnsSessionResources => _host is not null;

    /// <inheritdoc />
    public LiveSyncState State { get; private set; } = LiveSyncState.Disconnected;

    /// <inheritdoc />
    public event EventHandler<LiveSyncStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public long? LastCs2DemoTick
    {
        get
        {
            long value = Volatile.Read(ref _lastTickSlot);
            return value == NoTick ? null : value;
        }
    }

    /// <inheritdoc />
    public LiveSyncVersionInfo? Versions { get; private set; }

    /// <inheritdoc />
    public LiveSyncCapabilities? Capabilities { get; private set; }

    /// <inheritdoc />
    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource enableCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _enableCts = enableCts;
        try
        {
            await _lifecycleGate.WaitAsync(enableCts.Token);
        }
        catch
        {
            _enableCts = null;
            enableCts.Dispose();
            throw;
        }

        try
        {
            if (_disposed)
            {
                // Disposed while queued behind the gate: the teardown already ran; starting a
                // session now would leak a host nothing ever disposes.
                return;
            }

            CancellationToken ct = enableCts.Token;
            if (State.IsSessionActive || State.Kind == LiveSyncStateKind.SuspendedForReel)
            {
                // Interlock: while a reel owns CS2, sync Enable/Reconnect are excluded.
                return;
            }

            LiveSyncSettings settings = CurrentSettings();

            SetState(new LiveSyncState(LiveSyncStateKind.HostStarting));
            try
            {
                _host ??= await CsvgWebHost.StartAsync(settings, CreateLogBridge(), ct);
            }
            catch (OperationCanceledException)
            {
                SetState(LiveSyncState.Disconnected);
                throw;
            }
            catch (LiveSyncPortInUseException ex)
            {
                SetState(new LiveSyncState(LiveSyncStateKind.Faulted, ex.Message));
                return;
            }
            catch (Exception ex)
            {
                SetState(new LiveSyncState(LiveSyncStateKind.Faulted,
                    $"The sync host failed to start — {ex.Message}"));
                return;
            }

            CsvgVideoSession session = _host.Session;

            // CSVG recovery contract: from Faulted, StopAsync first (stops any leftover CS2 process,
            // restores backups, returns the session to Disconnected). This runs exactly when CSVG is
            // cleaning up a dead CS2. Failures land in Faulted per the ILiveSyncService contract,
            // never escape into the flyout command.
            if (session.State == CsvgSessionState.Faulted)
            {
                try
                {
                    await session.StopAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    SetState(new LiveSyncState(LiveSyncStateKind.Faulted,
                        $"Could not clean up the previous session — {ex.Message}"));
                    return;
                }
            }

            Subscribe(session);
            ResetSessionSurface();

            SetState(new LiveSyncState(LiveSyncStateKind.LaunchingCs2,
                settings.MockMode || !string.IsNullOrWhiteSpace(settings.ExternalMockServerPath)
                    ? "Launching mock CS2"
                    : "Launching CS2 (up to ~2 min)"));
            try
            {
                await session.StartWatchAsync(
                    new EngineSessionOptions
                    {
                        Width = settings.GameWindowWidth > 0 ? settings.GameWindowWidth : null,
                        Height = settings.GameWindowHeight > 0 ? settings.GameWindowHeight : null,
                        Fullscreen = settings.GameFullscreen
                    },
                    ct);
            }
            catch (OperationCanceledException)
            {
                await StopSessionCoreAsync();
                SetState(LiveSyncState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                SetState(new LiveSyncState(LiveSyncStateKind.Faulted,
                    $"CS2 failed to start — {ex.Message}"));
                return;
            }

            SetState(new LiveSyncState(LiveSyncStateKind.ConnectedIdle));

            // Latch the plugin's advertised capabilities for the session (capability matrix); the
            // engine's v1.0/v1.1 behavior switches ride on it. Capabilities are per-run engine
            // truth, so read them off session.Engine (the current run's ICs2EngineSession).
            Capabilities = MapCapabilities(session.Engine.PluginCapabilities);

            // Session is up: attach the outbound pipeline. The engine is Avalonia-free;
            // the observer subscribes shell events and must therefore attach on the UI thread.
            SyncEngine engine = new(new CsvgSyncClientAdapter(session), SyncTimings.Default, Capabilities);
            engine.StatusChanged += OnEngineStatus;
            _engine = engine;
            int tickOffset = settings.TickOffset;
            if (Dispatcher.UIThread.CheckAccess())
            {
                AttachObserver(engine, tickOffset);
            }
            else
            {
                Dispatcher.UIThread.Post(() => AttachObserver(engine, tickOffset));
            }
        }
        finally
        {
            _lifecycleGate.Release();
            _enableCts = null;
            enableCts.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelInFlightEnable();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopSessionCoreAsync(true);
            SetState(LiveSyncState.Disconnected);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyMomentAsync(int frameClockTick,
        int preRollTicks = VerificationRunner.DefaultPreRollTicks,
        int postRollTicks = VerificationRunner.DefaultPostRollTicks,
        string? spectateName = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is not { } engine || _subscribedSession is not { } session || !State.IsSynced)
        {
            // Not synced for the current demo: the UI owns the enable/sync prompt.
            return false;
        }

        if (_observer?.Mapper is not { } mapper)
        {
            return false;
        }

        engine.BeginVerification();
        try
        {
            VerificationRunner.Outcome outcome = await VerificationRunner.RunAsync(
                session, mapper, frameClockTick, preRollTicks, postRollTicks, spectateName, cancellationToken);
            if (outcome.Success)
            {
                // Remote-apply DV's playhead to the trigger frame under the loop
                // breaker, desired state aligned so the observer sees no diff.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyingRemote = true;
                    try
                    {
                        _shell.Playback.SeekToFrame(outcome.TargetFrameIndex);
                        engine.SetDesiredTick(outcome.TargetCs2Tick);
                        engine.SetDesiredPlaying(false);
                    }
                    finally
                    {
                        ApplyingRemote = false;
                    }
                });
            }

            return outcome.Success;
        }
        finally
        {
            engine.EndVerification();
        }
    }

    /// <inheritdoc />
    public Task<bool> HasLeftoverInstallModificationsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallRecovery.Detect(CurrentSettings())?.Any == true, cancellationToken);

    /// <inheritdoc />
    public Task RestoreInstallAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallRecovery.Restore(
            CurrentSettings(),
            line => Dispatcher.UIThread.Post(() =>
            {
                OutputChannelViewModel channel = _shell.Output.GetOrAddChannel("Live Sync", OutputSeverity.Live);
                channel.Append(new OutputRow(-1, "CSVG", "INFO", line));
            })), cancellationToken);

    /// <inheritdoc />
    public Task ResyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_observer is null)
        {
            return Task.CompletedTask;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            _observer.Republish();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => _observer?.Republish()).GetTask();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Join, never skip: a second disposer (e.g. a repeated ShutdownRequested) must WAIT for
        // the in-flight teardown: returning early would let the process exit while the CS2 kill
        // / install restore is still mid-flight.
        lock (_disposeStart)
        {
            _disposeTask ??= DisposeCoreAsync();
        }

        return new ValueTask(_disposeTask);
    }

    private void ApplyLogGate(LiveSyncSettings s)
    {
        _logMinLevel = ToMelLevel(s.MinimumLogLevel);
        _captureFrameworkLogs = s.CaptureFrameworkLogs;
    }

    /// <summary>Maps the UI-head log-level mirror onto the framework <see cref="LogLevel" /> (1:1).</summary>
    internal static LogLevel ToMelLevel(LiveSyncLogLevel level) => level switch
    {
        LiveSyncLogLevel.Trace => LogLevel.Trace,
        LiveSyncLogLevel.Debug => LogLevel.Debug,
        LiveSyncLogLevel.Information => LogLevel.Information,
        LiveSyncLogLevel.Warning => LogLevel.Warning,
        LiveSyncLogLevel.Error => LogLevel.Error,
        LiveSyncLogLevel.Critical => LogLevel.Critical,
        LiveSyncLogLevel.None => LogLevel.None,
        _ => LogLevel.Information
    };

    /// <summary>Projects CSVG capability tokens onto the engine-feature matrix.</summary>
    public static LiveSyncCapabilities MapCapabilities(IReadOnlySet<string> tokens) => new(
        tokens.Contains(CsvgCapabilities.DemoStateEvents),
        tokens.Contains(CsvgCapabilities.CommandAck),
        tokens.Contains(CsvgCapabilities.SeekAck),
        tokens.Contains(CsvgCapabilities.TimescaleSet),
        tokens.Contains(CsvgCapabilities.DemoIdentity),
        tokens.Contains(CsvgCapabilities.EnginePauseDetection),
        tokens.Contains(CsvgCapabilities.LoadFailureDetection),
        tokens.Contains(CsvgCapabilities.SpectateBySteamId),
        tokens.Contains(CsvgCapabilities.UserDemoUi));

    /// <summary>
    ///     Cancels an in-flight EnableAsync (CS2 launch) so lifecycle intent never queues
    ///     behind it for minutes. Safe to call from any thread.
    /// </summary>
    private void CancelInFlightEnable()
    {
        try
        {
            _enableCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The enable finished (and disposed its CTS) between the read and the cancel.
        }
    }

    /// <summary>UI thread. No-ops when the session tore down before the post arrived.</summary>
    private void AttachObserver(SyncEngine engine, int tickOffset)
    {
        if (!ReferenceEquals(_engine, engine) || _disposed)
        {
            return;
        }

        _observer = new SyncStateObserver(_shell, engine, tickOffset, () => ApplyingRemote);
        _inbound = new InboundSync(
            _shell, engine, Capabilities ?? LiveSyncCapabilities.None,
            () => LastCs2DemoTick,
            () => _observer?.Mapper,
            () => State,
            applying => ApplyingRemote = applying);
    }

    /// <summary>Engine status → service state (any thread; SetState marshals + dedups).</summary>
    private void OnEngineStatus(LiveSyncState status)
    {
        if (_tearingDown || _disposed || State.Kind == LiveSyncStateKind.Faulted)
        {
            // A dead session's terminal copy (CS2 quit / launch failure) outranks pipeline chatter.
            return;
        }

        SetState(status);
    }

    private LiveSyncSettings CurrentSettings() => (_settings?.CurrentValue ?? new AppSettings()).LiveSync;

    private async Task DisposeCoreAsync()
    {
        CancelInFlightEnable();
        await _lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            _disposed = true;
            _settingsChangeSub?.Dispose();
            await StopSessionCoreAsync();
            SetState(LiveSyncState.Disconnected);
        }
        finally
        {
            // The gate is deliberately NOT disposed: waiters queued behind this teardown (a late
            // flyout click racing shutdown) still wake, hit the _disposed re-check, and release.
            // Disposing it would turn their finally-Release into an ObjectDisposedException
            // inside an async-void command. SemaphoreSlim holds no unmanaged state.
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    ///     Single-CS2 interlock: a reel needs a capture session. The sync
    ///     session cannot coexist. Stops session + host and parks the chip at
    ///     "Paused for reel render"; sync actions stay excluded until
    ///     <see cref="EndReelSuspension" />.
    /// </summary>
    internal async Task SuspendForReelAsync()
    {
        if (_disposed)
        {
            return;
        }

        CancelInFlightEnable();
        await _lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopSessionCoreAsync();
            // COMMIT the state on the UI thread before returning: the reel job's fast-fail
            // paths call EndReelSuspension right after this returns, and a merely-POSTED
            // SuspendedForReel would land after that check, parking the chip at "Paused for
            // reel render" forever with Enable refused.
            await Dispatcher.UIThread.InvokeAsync(() =>
                SetState(new LiveSyncState(LiveSyncStateKind.SuspendedForReel)));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    ///     Ends the reel suspension: back to Disconnected. The Off flyout's informed Enable IS
    ///     the reconnect prompt (never an auto-relaunch). Any thread: the check+set is
    ///     marshaled so it reads the committed state, not a stale cross-thread snapshot.
    /// </summary>
    internal void EndReelSuspension()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            EndReelSuspensionCore();
        }
        else
        {
            Dispatcher.UIThread.Post(EndReelSuspensionCore);
        }
    }

    private void EndReelSuspensionCore()
    {
        if (!_disposed && State.Kind == LiveSyncStateKind.SuspendedForReel)
        {
            SetState(LiveSyncState.Disconnected);
        }
    }

    /// <summary>
    ///     Stops the CSVG session (kills CS2/mock, restores the install) and tears down the gRPC
    ///     host. Never throws. Teardown must always complete. Callers hold the lifecycle gate.
    ///     <paramref name="clearOutput" /> drops the app-lifetime "Live Sync" log channel too, set
    ///     only on Disable, so reel-suspend and enable-cancel keep the accumulated diagnostics.
    /// </summary>
    private async Task StopSessionCoreAsync(bool clearOutput = false)
    {
        _tearingDown = true;
        try
        {
            // Both UI-coupled pipeline halves dispose on the UI thread (their documented
            // residency: they unsubscribe shell events); the reel-suspend path runs this on a
            // threadpool thread.
            if (_observer is { } observer)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    observer.Dispose();
                }
                else
                {
                    Dispatcher.UIThread.Post(observer.Dispose);
                }

                _observer = null;
            }

            if (_inbound is { } inbound)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    inbound.Dispose();
                }
                else
                {
                    Dispatcher.UIThread.Post(inbound.Dispose);
                }

                _inbound = null;
            }

            // Fields are cleared BEFORE the awaits and every dispose is best-effort: the doc
            // contract is "never throws; teardown must always complete", and a throwing dispose
            // must not leave a half-disposed host behind for the next EnableAsync's `??=` to reuse.
            if (_engine is { } engine)
            {
                engine.StatusChanged -= OnEngineStatus;
                _engine = null;
                try
                {
                    await engine.DisposeAsync();
                }
                catch
                {
                    // Best-effort.
                }
            }

            if (_host is { } host)
            {
                _host = null;
                try
                {
                    await host.Session.StopAsync(CancellationToken.None);
                }
                catch
                {
                    // Best-effort: CSVG's StopAsync is itself the recovery path; a failure
                    // here leaves `csvg restore` / doctor as the manual fallback (the crash-recovery offer surfaces it).
                }

                Unsubscribe();
                try
                {
                    await host.DisposeAsync();
                }
                catch
                {
                    // Best-effort: a failed container dispose must not wedge the service.
                }
            }

            if (clearOutput)
            {
                // Drop the session's accumulated CSVG log rows so they don't survive Disable (the
                // channel is app-lifetime; only file-load otherwise clears it). Marshaled like the
                // pipeline disposes above: this path runs on the UI thread on Disable and on a
                // threadpool thread on the reel-suspend path (which passes clearOutput:false).
                if (Dispatcher.UIThread.CheckAccess())
                {
                    _shell.Output.ClearChannel("Live Sync");
                }
                else
                {
                    Dispatcher.UIThread.Post(() => _shell.Output.ClearChannel("Live Sync"));
                }
            }

            ResetSessionSurface();
        }
        finally
        {
            _tearingDown = false;
        }
    }

    private void ResetSessionSurface()
    {
        Volatile.Write(ref _lastTickSlot, NoTick);
        Versions = null;
        Capabilities = null;
    }

    private void Subscribe(CsvgVideoSession session)
    {
        if (ReferenceEquals(_subscribedSession, session))
        {
            return;
        }

        Unsubscribe();

        // Inbound threading: the synchronous tick hot path writes a slot and feeds the
        // engine's believed state (one lock, no UI). It MUST be exception-free (no
        // per-subscriber isolation on CSVG's sync event path).
        _onTickUpdated = (_, tick) =>
        {
            Volatile.Write(ref _lastTickSlot, tick);
            try
            {
                // LastTickIsPaused rides the same wire event, so it is current here; it is per-run
                // engine truth (session.Engine), and the engine only consumes it under the
                // engine-pause-detection capability. Guarded by the surrounding catch against a
                // transient null Engine during teardown.
                _engine?.NotifyTick(tick, session.Engine.LastTickIsPaused);
            }
            catch
            {
                // Never let the sync event path see a throw.
            }
        };

        // Async events are awaited serially on the gRPC read loop: post-and-return, never
        // await UI work inline.
        _onClientStateChanged = (_, next) =>
        {
            Dispatcher.UIThread.Post(() => OnClientStateChanged(next));
            return Task.CompletedTask;
        };
        _onProcessStatusChanged = (_, status) =>
        {
            string? plugin = status.HasPluginVersion ? status.PluginVersion : null;
            string? game = status.HasGameVersion ? status.GameVersion : null;
            if (plugin is not null || game is not null)
            {
                Dispatcher.UIThread.Post(() => Versions = new LiveSyncVersionInfo(plugin, game));
            }

            return Task.CompletedTask;
        };
        _onPlaybackStatusChanged = (_, change) =>
        {
            // Ledger echo path (v1.0 confirmation): lock-cheap, no UI work; run inline rather
            // than bouncing through the dispatcher so confirmations aren't delayed behind renders.
            _engine?.NotifyPlaybackStatus(change.NewStatus);
            return Task.CompletedTask;
        };
        _onDemoStateChanged = (_, demoState) =>
        {
            // v1.1 mirroring: post-and-return (async events are awaited serially on the
            // gRPC read loop; UI work must never run inline there).
            Dispatcher.UIThread.Post(() => _inbound?.OnDemoState(demoState));
            return Task.CompletedTask;
        };

        // Subscribe on the video session, not session.Engine: CsvgVideoSession forwards from
        // whichever engine session is current, so these handlers survive a Start→Stop→Start cycle
        // (CSVG's migrating-to-2.0.md, Events section).
        session.TickUpdated += _onTickUpdated;
        session.StateChanged += _onClientStateChanged;
        session.ProcessStatusChanged += _onProcessStatusChanged;
        session.DemoPlaybackStatusChanged += _onPlaybackStatusChanged;
        session.DemoStateChanged += _onDemoStateChanged;
        _subscribedSession = session;
    }

    private void Unsubscribe()
    {
        if (_subscribedSession is not { } session)
        {
            return;
        }

        session.TickUpdated -= _onTickUpdated;
        session.StateChanged -= _onClientStateChanged;
        session.ProcessStatusChanged -= _onProcessStatusChanged;
        session.DemoPlaybackStatusChanged -= _onPlaybackStatusChanged;
        session.DemoStateChanged -= _onDemoStateChanged;
        _subscribedSession = null;
    }

    /// <summary>UI thread. Maps CSVG client transitions onto the engine state machine.</summary>
    private void OnClientStateChanged(CsvgSessionState next)
    {
        if (_tearingDown || _disposed)
        {
            return;
        }

        switch (next)
        {
            case CsvgSessionState.Connecting when State.Kind == LiveSyncStateKind.LaunchingCs2:
                SetState(new LiveSyncState(LiveSyncStateKind.Connecting,
                    "Waiting for the CS2 plugin (up to ~2 min)"));
                break;

            case CsvgSessionState.Faulted when State.IsSessionActive:
                // Reconnect copy must say a relaunch is coming.
                SetState(new LiveSyncState(LiveSyncStateKind.Faulted,
                    "CS2 quit. Reconnecting relaunches CS2 from scratch (up to ~2 min)."));
                break;
        }
    }

    private void SetState(LiveSyncState next)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetState(next));
            return;
        }

        LiveSyncState previous = State;
        if (previous == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, new LiveSyncStateChangedEventArgs(previous, next));
    }

    /// <summary>
    ///     CSVG host logs → the Output panel's lazily-created "Live Sync" channel. The channel is
    ///     created on first log line, so users who never enable live sync never see it.
    /// </summary>
    private OutputLogBridge CreateLogBridge() =>
        new((level, category, message) => Dispatcher.UIThread.Post(() =>
            {
                string label = LevelLabel(level);

                // Feed the unified diagnostics hub, tagged "CSVG" (telemetry P2). Only plain rows cross the
                // seam. No ASP.NET/gRPC types reach the App head. We're already on the UI thread (this sink
                // is invoked inside Dispatcher.Post), so append directly; the MEL LogLevel passes straight
                // through: the hub row keys on LogLevel now.
                _shell.Telemetry.AppendOnUiThread(new TelemetryLogRow(
                    "CSVG", level, label, category, message,
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));

                // …and the lazily-created bottom Output-drawer channel (unchanged existing surface).
                OutputChannelViewModel channel = _shell.Output.GetOrAddChannel("Live Sync", OutputSeverity.Live);
                channel.Append(new OutputRow(-1, "CSVG", label, $"{category}: {message}"));
            }),
            () => _logMinLevel,
            () => _captureFrameworkLogs);

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => "LOG"
    };
}
