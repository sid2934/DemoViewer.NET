#region

using System.Threading.Channels;
using DemoViewer.NET.Services.LiveSync;
using static Cs2VideoGenerator.Core.Proto.DemoPlaybackStatusChange.Types;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>Timing knobs for <see cref="SyncEngine" />, shrunk by tests; production uses the defaults.</summary>
/// <param name="Settle">The edge-triggered reconcile debounce window (~140 ms).</param>
/// <param name="SeekConfirmGrace">
///     v1.0 provisional-confirm grace: the plugin pre-echoes the TARGET tick before actually
///     seeking, so an on-target tick only confirms after this window passes without a
///     contradicting far tick.
/// </param>
/// <param name="SeekTimeout">Unconfirmed-seek expiry → Degraded.</param>
/// <param name="PlayPauseTimeout">Missing play/pause status echo → Degraded.</param>
public sealed record SyncTimings(
    TimeSpan Settle,
    TimeSpan SeekConfirmGrace,
    TimeSpan SeekTimeout,
    TimeSpan PlayPauseTimeout)
{
    public static SyncTimings Default { get; } = new(
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5));
}

/// <summary>
///     The outbound sync core (one-way): desired-state vs believed-state
///     reconciliation, echo-suppression ledger, single-slot latest-wins seek pipeline, and the
///     serial command pump. Deliberately Avalonia-free: the UI-coupled
///     <see cref="SyncStateObserver" /> feeds desired state in, CSVG events feed believed state
///     in via <see cref="NotifyTick" /> / <see cref="NotifyPlaybackStatus" />, and the computed
///     <see cref="LiveSyncState" /> flows out through <see cref="StatusChanged" /> (raised on
///     arbitrary threads, the service marshals).
///     <para>
///         Control plane: DV is the single command authority. While both sides play, CS2's ticks
///         are a drift REFERENCE only (the servo consumes them); outbound sync never pushes
///         position while playing: discrete seeks only, which DV's controller always issues from
///         a paused state (<c>SeekToFrame</c> stops the play loop first).
///     </para>
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    /// <summary>±tick window for believed-position "close enough" and v1.0 seek confirmation.</summary>
    public const int SeekConfirmTolerance = 32;

    /// <summary>A tick this far from a provisional seek target revokes the provisional confirm.</summary>
    public const int SeekContradictionDistance = 128;

    private readonly LiveSyncCapabilities _capabilities;

    private readonly ISyncClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly Task _pumpTask;

    private readonly Channel<SyncCommand> _queue =
        Channel.CreateUnbounded<SyncCommand>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

    private readonly Timer _seekGraceTimer;
    private readonly Timer _seekTimeoutTimer;
    private readonly Timer _settleTimer;
    private readonly SyncTimings _timings;
    private readonly Timer _toggleTimeoutTimer;

    // ── Believed (CS2, from echoes + tick stream) ─────────────────────────────
    private string? _believedDemo;
    private bool _believedPlaying;
    private string? _believedSpectator;

    private long? _believedTick;

    // Last SENT values: the protocol has no readback. Null = unknown: a failed send rolls back
    // here so the next reconcile (incl. Re-sync, which never re-touches these) resends.
    private double? _believedTimescale = 1.0;
    private string? _degradedReason;
    private bool _demoPathUnavailable;

    // ── Desired (DV intent) ───────────────────────────────────────────────────
    private string? _desiredDemo;
    private bool _desiredPlaying;
    private string? _desiredSpectator;
    private long? _desiredTick;
    private double _desiredTimescale = 1.0;

    // Inbound flags for state that's inferred, not confirmed: inferred pause (v1.0
    // tick-silence watchdog) and the CS2-side demo change offer (v1.1 demo-identity).
    private bool _inferredPause;

    private LiveSyncState? _lastPublished;

    // ── In-flight ledger ──────────────────────────────────────────────────────
    private bool _loadInFlight;
    private bool _pendingSeekProvisional;
    private long? _pendingSeekTarget;
    private bool? _pendingToggleTarget;
    private string? _remoteDemoPath;
    private bool _seekMarkerQueued;
    private long _seekSlot;
    private bool _seekSlotArmed;

    // While a verification range playback owns CS2, the reconciler and the
    // inbound pump both stand down. The range's seeks/plays/pauses are neither drift nor
    // user intent, and pushing DV state at CS2 mid-range would fight the playback.
    private volatile bool _verificationInFlight;

    public SyncEngine(ISyncClient client, SyncTimings timings, LiveSyncCapabilities capabilities)
    {
        _client = client;
        _timings = timings;
        _capabilities = capabilities;
        _settleTimer = new Timer(_ => Reconcile(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _seekGraceTimer = new Timer(_ => OnSeekGraceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _seekTimeoutTimer = new Timer(_ => OnSeekTimedOut(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _toggleTimeoutTimer = new Timer(_ => OnToggleTimedOut(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pumpTask = Task.Run(PumpAsync);
    }

    // v1.1 fast paths: acked seeks replace the pre-echo/grace/timeout ledger machinery
    // (the client owns the ack deadline), and engine-truth pause replaces status-echo confirmation.
    private bool AckedSeeks => _capabilities.SeekAck && _capabilities.CommandAck;

    /// <summary>VerificationPending: reconciliation pauses; the pump reads this too.</summary>
    public bool VerificationInFlight => _verificationInFlight;

    /// <summary>
    ///     True while a seek is unresolved (in the slot or awaiting confirmation): the
    ///     inbound tick-jump inference must not misread our own seek as a CS2-side one.
    /// </summary>
    internal bool HasPendingSeek
    {
        get
        {
            lock (_gate)
            {
                return _pendingSeekTarget is not null || _seekSlotArmed || _seekMarkerQueued;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _queue.Writer.TryComplete();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch
        {
            // Pump exit is best-effort during teardown.
        }

        _settleTimer.Dispose();
        _seekGraceTimer.Dispose();
        _seekTimeoutTimer.Dispose();
        _toggleTimeoutTimer.Dispose();
        _cts.Dispose();
    }

    /// <summary>Raised (on arbitrary threads) whenever the computed engine status changes.</summary>
    public event Action<LiveSyncState>? StatusChanged;

    // ── Desired-state API (observer / UI thread) ──────────────────────────────

    /// <summary>
    ///     New demo intent (or none). Resets the tick/playing intent alongside, and re-arms a
    ///     Degraded engine: a fresh full-intent push (demo load, DemoReset, Re-sync) supersedes
    ///     any stale unconfirmed-command reason.
    /// </summary>
    public void SetDesiredDemo(string? demoPath, long? tick, bool playing)
    {
        lock (_gate)
        {
            _desiredDemo = demoPath;
            _desiredTick = tick;
            _desiredPlaying = playing;
            _demoPathUnavailable = false;
            _degradedReason = null;
            _remoteDemoPath = null;
            _inferredPause = false;
        }

        Kick();
    }

    /// <summary>
    ///     The loaded DV demo has no rooted local path CSVG could open. Sync
    ///     disengages and surfaces the Degraded reason.
    /// </summary>
    public void NoteDemoPathUnavailable()
    {
        lock (_gate)
        {
            _desiredDemo = null;
            _desiredTick = null;
            _desiredPlaying = false;
            _demoPathUnavailable = true;
        }

        Kick();
    }

    /// <summary>Discrete-seek intent (frame clock already mapped to a CS2 demo tick).</summary>
    public void SetDesiredTick(long tick)
    {
        lock (_gate)
        {
            _desiredTick = tick;
        }

        Kick();
    }

    /// <summary>Play/pause intent.</summary>
    public void SetDesiredPlaying(bool playing)
    {
        lock (_gate)
        {
            _desiredPlaying = playing;
        }

        Kick();
    }

    /// <summary>
    ///     Playback-speed intent. Mirrored to CS2 only under the "timescale-set"
    ///     capability, no-op otherwise, so the observer can push unconditionally. Send-only:
    ///     believed == last sent (the protocol has no engine readback, and in-game console
    ///     changes are invisible).
    /// </summary>
    public void SetDesiredTimescale(double timescale)
    {
        if (!_capabilities.TimescaleSet)
        {
            return;
        }

        lock (_gate)
        {
            _desiredTimescale = Math.Clamp(timescale, 0.25, 8.0);
        }

        Kick();
    }

    /// <summary>
    ///     Spectate intent: the exact in-demo player name to follow in CS2. Send-only,
    ///     dedup on change (a v1.0 command, no capability gate; readback awaits
    ///     "spectator-report"). Known limitation: exact-name targeting breaks on mid-match
    ///     renames until A-P9 steamid spectating validates.
    /// </summary>
    public void SetDesiredSpectator(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return;
        }

        lock (_gate)
        {
            _desiredSpectator = playerName;
        }

        Kick();
    }

    /// <summary>Enters verification mode (suspend the follower).</summary>
    public void BeginVerification()
    {
        _verificationInFlight = true;
        PublishStatus();
    }

    /// <summary>
    ///     Leaves verification mode and re-kicks reconciliation: the realign push (CS2 sits
    ///     paused at end-of-range; DV was remote-applied to the trigger) flows normally.
    /// </summary>
    public void EndVerification()
    {
        _verificationInFlight = false;
        Kick();
    }

    /// <summary>
    ///     v1.0 inference (fallback): tick silence while believed playing. CS2 is PROBABLY
    ///     paused, labeled as inferred (the hollow-dot state). Cleared by any subsequent
    ///     evidence (tick, status echo, fresh intent push).
    /// </summary>
    public void NoteInferredPause()
    {
        lock (_gate)
        {
            if (_believedDemo is null || _inferredPause)
            {
                return;
            }

            _believedPlaying = false;
            _inferredPause = true;
        }

        PublishStatus();
    }

    /// <summary>
    ///     v1.0 inference: the tick stream restarted near 0 (or otherwise became unexplainable).
    ///     CS2's demo state is genuinely unknown. Degraded until Re-sync/evidence.
    /// </summary>
    public void NoteRemoteDemoStateUnknown()
    {
        lock (_gate)
        {
            _degradedReason = "CS2's demo state is unknown. Re-sync to re-push this demo and position.";
        }

        PublishStatus();
    }

    /// <summary>
    ///     v1.1 demo-identity: CS2 reports a DIFFERENT loaded demo (user changed it
    ///     in-game). Never silently auto-load: surface Degraded with the path so the flyout
    ///     offers "Open in DV" / Re-sync. Believed demo adopts CS2's truth so the reconciler does
    ///     not fight the user by re-pushing DV's demo uninvited.
    /// </summary>
    public void NoteRemoteDemoChanged(string cs2DemoPath)
    {
        lock (_gate)
        {
            _believedDemo = cs2DemoPath;
            _believedTick = null;
            _remoteDemoPath = cs2DemoPath;
            _degradedReason =
                $"CS2 is now playing a different demo ({Path.GetFileName(cs2DemoPath)}). "
                + "Open it in DemoViewer, or Re-sync to re-push this demo.";
            AbandonSeekLocked();
        }

        PublishStatus();
    }

    // ── Believed-state notifications (CSVG event threads) ─────────────────────

    /// <summary>
    ///     Tick-stream update. Cheap (one lock); safe on the synchronous hot path.
    ///     <paramref name="isPaused" /> is the v1.1 per-tick pause flag, consumed as believed
    ///     truth only when the plugin advertises engine-pause-detection (it also confirms a
    ///     matching pending play/pause without waiting for the status echo).
    /// </summary>
    public void NotifyTick(long tick, bool? isPaused = null)
    {
        lock (_gate)
        {
            _believedTick = tick;

            // v1.0 inference exit: a tick is evidence CS2 is ticking again. An inferred pause
            // was wrong (or ended); on that path ticks only flow while the demo plays.
            if (_inferredPause && !_capabilities.EnginePauseDetection)
            {
                _inferredPause = false;
                _believedPlaying = true;
            }

            if (isPaused is bool paused && _capabilities.EnginePauseDetection)
            {
                _believedPlaying = !paused;
                if (_pendingToggleTarget is bool toggleTarget && toggleTarget == !paused)
                {
                    ConfirmToggleLocked();
                }
            }

            if (_pendingSeekTarget is long target)
            {
                long distance = Math.Abs(tick - target);
                if (distance <= SeekConfirmTolerance)
                {
                    if (!_pendingSeekProvisional)
                    {
                        // v1.0 pre-echo defence: an on-target tick is only PROVISIONAL. The
                        // plugin echoes the target before seeking. Confirm after the grace window
                        // unless a far tick contradicts it.
                        _pendingSeekProvisional = true;
                        ChangeSafe(_seekGraceTimer, _timings.SeekConfirmGrace);
                    }
                }
                else if (_pendingSeekProvisional && distance > SeekContradictionDistance)
                {
                    _pendingSeekProvisional = false;
                    ChangeSafe(_seekGraceTimer, Timeout.InfiniteTimeSpan);
                }
            }
        }

        PublishStatus();
    }

    /// <summary>Demo playback status echo (v1.0 confirmation path for play/pause).</summary>
    public void NotifyPlaybackStatus(DemoPlaybackStatus status)
    {
        lock (_gate)
        {
            _inferredPause = false; // any status echo is confirmed evidence
            switch (status)
            {
                case DemoPlaybackStatus.Playing:
                    _believedPlaying = true;
                    if (_pendingToggleTarget == true)
                    {
                        ConfirmToggleLocked();
                    }

                    break;

                case DemoPlaybackStatus.Paused:
                    _believedPlaying = false;
                    if (_pendingToggleTarget == false)
                    {
                        ConfirmToggleLocked();
                    }

                    break;

                case DemoPlaybackStatus.Stopped:
                case DemoPlaybackStatus.Stopping:
                    _believedPlaying = false;
                    break;

                case DemoPlaybackStatus.DemoFileNotFound:
                case DemoPlaybackStatus.DemoFileUnplayable:
                    // Usually surfaces as a LoadDemoAsync failure too; this covers event-only paths.
                    _degradedReason = "CS2 could not load the demo file.";
                    break;
            }
        }

        Kick();
    }

    // ── Reconciler (edge-triggered, settle-debounced) ─────────────────────────

    /// <summary>Restarts the settle window; the reconcile runs once input goes quiet (~140 ms).</summary>
    private void Kick()
    {
        ChangeSafe(_settleTimer, _timings.Settle);
        PublishStatus();
    }

    /// <summary>
    ///     <see cref="Timer.Change(TimeSpan, TimeSpan)" /> guarded against the teardown race:
    ///     <see cref="DisposeAsync" /> does not quiesce in-flight timer/pump callbacks, so any of
    ///     them can reach a sibling timer after it is disposed: swallow, since nothing is left to
    ///     schedule.
    /// </summary>
    private static void ChangeSafe(Timer timer, TimeSpan dueTime)
    {
        try
        {
            timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposal race: nothing left to reconcile.
        }
    }

    private void Reconcile()
    {
        List<SyncCommand> commands = [];
        lock (_gate)
        {
            if (_cts.IsCancellationRequested || _loadInFlight || _remoteDemoPath is not null
                || _verificationInFlight)
            {
                // Load completion re-kicks. A pending CS2-side demo-change offer PAUSES
                // reconciliation entirely: re-pushing DV's demo uninvited would fight
                // the user's in-game choice. Open-in-DV or Re-sync resolves it. Verification
                // pauses it too: the range playback owns CS2 until EndVerification.
            }
            else if (!string.Equals(_desiredDemo, _believedDemo, StringComparison.Ordinal))
            {
                // Demo change: abandon any in-flight seek FIRST (the plugin's seek slot
                // must never interleave with a close/load), close the old demo, then load.
                AbandonSeekLocked();
                if (_believedDemo is not null)
                {
                    commands.Add(SyncCommand.Close);
                }

                if (_desiredDemo is not null)
                {
                    _loadInFlight = true;
                    commands.Add(new SyncCommand.Load(_desiredDemo));
                }
            }
            else if (_desiredDemo is not null)
            {
                // Position: discrete-seek intent only, and never while BOTH sides play: there,
                // position drift is the servo's job, never a seek storm. While CS2 is
                // believed paused, a playing intent still needs its position delivered FIRST
                // (enable-sync-mid-play / Re-sync-while-playing land here with believed
                // {tick 0, paused}): seek, then the Play toggle below. Otherwise CS2 plays from
                // 0 and the servo hard-resyncs DV back to the demo start.
                if ((!_desiredPlaying || !_believedPlaying) && _desiredTick is long target)
                {
                    bool closeEnough = _believedTick is long believed
                                       && Math.Abs(believed - target) <= SeekConfirmTolerance;
                    bool alreadyInFlight = _pendingSeekTarget == target || _seekSlotArmed && _seekSlot == target;
                    if (!closeEnough && !alreadyInFlight)
                    {
                        _seekSlot = target;
                        _seekSlotArmed = true;
                        if (!_seekMarkerQueued)
                        {
                            _seekMarkerQueued = true;
                            commands.Add(SyncCommand.SeekMarker);
                        }
                    }
                }

                if (_pendingToggleTarget is null && _desiredPlaying != _believedPlaying)
                {
                    _pendingToggleTarget = _desiredPlaying;
                    ChangeSafe(_toggleTimeoutTimer, _timings.PlayPauseTimeout);
                    commands.Add(_desiredPlaying ? SyncCommand.Play : SyncCommand.Pause);
                }

                // Speed mirror (capability-gated at intake): send-only, believed = last sent.
                if (_believedTimescale is not { } believedTimescale
                    || Math.Abs(_desiredTimescale - believedTimescale) > 0.001)
                {
                    commands.Add(new SyncCommand.Timescale(_desiredTimescale));
                    _believedTimescale = _desiredTimescale;
                }

                // Spectate mirror: send-only, believed = last sent name.
                if (_desiredSpectator is { } spectator
                    && !string.Equals(spectator, _believedSpectator, StringComparison.Ordinal))
                {
                    commands.Add(new SyncCommand.Spectate(spectator));
                    _believedSpectator = spectator;
                }
            }
        }

        foreach (SyncCommand command in commands)
        {
            _queue.Writer.TryWrite(command);
        }

        PublishStatus();
    }

    // ── Command pump (single consumer; ALL client calls off the UI thread) ────

    private async Task PumpAsync()
    {
        try
        {
            await foreach (SyncCommand command in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await ExecuteAsync(command).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnCommandFailed(command, ex);
                }

                PublishStatus();
            }
        }
        catch (OperationCanceledException)
        {
            // Engine disposed.
        }
    }

    private async Task ExecuteAsync(SyncCommand command)
    {
        switch (command)
        {
            case SyncCommand.Load load:
                // Request the interactive in-game demo UI whenever the plugin can honor it.
                // Without the flag the CS2→DV direction cannot exist even on a v1.1 plugin.
                await _client.LoadDemoAsync(load.Path, _capabilities.UserDemoUi, _cts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _believedDemo = load.Path;
                    // The client's load contract: completes with the demo LOADED and PAUSED at
                    // tick 0 (verified against the mock wire: the load sequence itself pauses
                    // and goes to 0). The post-load reconcile pushes only the position fixup.
                    _believedTick = 0;
                    _believedPlaying = false;
                    _loadInFlight = false;
                    _degradedReason = null;
                }

                Kick();
                break;

            case SyncCommand.CloseCommand:
                await _client.CloseDemoAsync(_cts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _believedDemo = null;
                    _believedTick = null;
                    _believedPlaying = false;
                }

                break;

            case SyncCommand.SeekMarkerCommand:
                long target;
                bool? pauseAfterSeek;
                lock (_gate)
                {
                    _seekMarkerQueued = false;
                    if (!_seekSlotArmed)
                    {
                        return;
                    }

                    target = _seekSlot;
                    _seekSlotArmed = false;
                    _pendingSeekTarget = target;
                    _pendingSeekProvisional = false;
                    pauseAfterSeek = _desiredPlaying ? null : true;
                    if (!AckedSeeks)
                    {
                        // v1.0 only: the acked path's deadline lives in the client, and an
                        // engine 5 s expiry would mis-degrade a legitimately-slow far seek.
                        ChangeSafe(_seekTimeoutTimer, _timings.SeekTimeout);
                    }
                }

                if (AckedSeeks)
                {
                    // v1.1: arrival-verified ack; pause-after-seek folds the pause half of the
                    // seek+pause pair into the command (the Paused echo/tick still confirms the
                    // standalone toggle if one is pending).
                    bool confirmed = await _client
                        .SetDemoTickAckedAsync(checked((int)target), pauseAfterSeek, _cts.Token)
                        .ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_pendingSeekTarget == target)
                        {
                            ClearPendingSeekLocked();
                            if (confirmed)
                            {
                                _believedTick = target;
                                if (pauseAfterSeek == true)
                                {
                                    _believedPlaying = false;
                                    // The arrival-verified ack IS the pause confirmation: a
                                    // standalone Pause toggle pending for the same state must
                                    // not sit waiting for an echo and expire.
                                    if (_pendingToggleTarget == false)
                                    {
                                        ConfirmToggleLocked();
                                    }
                                }

                                _degradedReason = null;
                            }
                            else
                            {
                                _degradedReason = "Seek unconfirmed — CS2 did not confirm the seek.";
                            }
                        }
                    }

                    Kick(); // post-seek fixups
                }
                else
                {
                    await _client.SetDemoTickAsync(checked((int)target), _cts.Token).ConfigureAwait(false);
                }

                // Latest-wins: a target that arrived while this send was in flight re-arms the
                // slot; make sure a marker exists for it.
                lock (_gate)
                {
                    if (_seekSlotArmed && !_seekMarkerQueued)
                    {
                        _seekMarkerQueued = true;
                        _queue.Writer.TryWrite(SyncCommand.SeekMarker);
                    }
                }

                break;

            case SyncCommand.PlayCommand:
                await _client.ResumeDemoAsync(_cts.Token).ConfigureAwait(false);
                break;

            case SyncCommand.PauseCommand:
                await _client.PauseDemoAsync(_cts.Token).ConfigureAwait(false);
                break;

            case SyncCommand.Timescale timescale:
                await _client.SetTimescaleAsync((float)timescale.Value, _cts.Token).ConfigureAwait(false);
                break;

            case SyncCommand.Spectate spectate:
                await _client.SetSpectatorAsync(spectate.PlayerName, _cts.Token).ConfigureAwait(false);
                break;
        }
    }

    private void OnCommandFailed(SyncCommand command, Exception ex)
    {
        lock (_gate)
        {
            switch (command)
            {
                case SyncCommand.Load:
                    _loadInFlight = false;
                    // Disengage rather than retry-loop: the user re-arms by re-loading the demo
                    // (or, later, Re-sync). CS2 keeps whatever it had.
                    _desiredDemo = null;
                    _degradedReason = $"CS2 could not load the demo — {ex.Message}";
                    break;

                case SyncCommand.CloseCommand:
                    _believedDemo = null;
                    _believedTick = null;
                    _believedPlaying = false;
                    _degradedReason = $"Closing the CS2 demo failed — {ex.Message}";
                    break;

                case SyncCommand.SeekMarkerCommand:
                    ClearPendingSeekLocked();
                    _degradedReason = $"The seek command failed — {ex.Message}";
                    break;

                case SyncCommand.PlayCommand:
                case SyncCommand.PauseCommand:
                    ClearToggleLocked();
                    _degradedReason = $"The play/pause command failed — {ex.Message}";
                    break;

                case SyncCommand.Timescale:
                    _believedTimescale = null; // roll back the enqueue-time optimism → resend
                    _degradedReason = $"The speed command failed — {ex.Message}";
                    break;

                case SyncCommand.Spectate:
                    _believedSpectator = null; // roll back the enqueue-time optimism → resend
                    _degradedReason = $"The spectate command failed — {ex.Message}";
                    break;
            }
        }
    }

    // ── Ledger timers ─────────────────────────────────────────────────────────

    private void OnSeekGraceElapsed()
    {
        bool confirmed = false;
        lock (_gate)
        {
            if (_pendingSeekTarget is not null && _pendingSeekProvisional)
            {
                ClearPendingSeekLocked();
                _degradedReason = null;
                confirmed = true;
            }
        }

        if (confirmed)
        {
            Kick(); // post-seek fixups (e.g. the pause half of seek+pause)
        }
    }

    private void OnSeekTimedOut()
    {
        lock (_gate)
        {
            if (_pendingSeekTarget is null)
            {
                return;
            }

            // Expiry: adopt CS2-reported truth (believed tick stays stream-fed) and say so.
            ClearPendingSeekLocked();
            _degradedReason = "Seek unconfirmed — showing CS2's reported position.";
        }

        PublishStatus();
    }

    private void OnToggleTimedOut()
    {
        lock (_gate)
        {
            if (_pendingToggleTarget is null)
            {
                return;
            }

            ClearToggleLocked();
            _degradedReason = "CS2 did not confirm the last play/pause command.";
        }

        PublishStatus();
    }

    private void ConfirmToggleLocked()
    {
        ClearToggleLocked();
        _degradedReason = null;
    }

    private void ClearToggleLocked()
    {
        _pendingToggleTarget = null;
        ChangeSafe(_toggleTimeoutTimer, Timeout.InfiniteTimeSpan);
    }

    private void ClearPendingSeekLocked()
    {
        _pendingSeekTarget = null;
        _pendingSeekProvisional = false;
        ChangeSafe(_seekGraceTimer, Timeout.InfiniteTimeSpan);
        ChangeSafe(_seekTimeoutTimer, Timeout.InfiniteTimeSpan);
    }

    private void AbandonSeekLocked()
    {
        _seekSlotArmed = false;
        ClearPendingSeekLocked();
    }

    // ── Status projection ─────────────────────────────────────────────────────

    private void PublishStatus()
    {
        // Delivery is serialized WITH the dedup decision: computing under _gate but invoking
        // outside any lock would let two racing publishers deliver in inverted order, and the
        // _lastPublished dedup then pins subscribers on the stale status until the next genuine
        // transition. Holding _publishGate across the invoke is safe: the only subscriber
        // (LiveSyncService.OnEngineStatus → SetState) posts to the dispatcher and returns.
        // Ordering is _publishGate → _gate; nothing takes _gate first.
        lock (_publishGate)
        {
            LiveSyncState status;
            lock (_gate)
            {
                status = ComputeStatusLocked();
                if (status == _lastPublished)
                {
                    return;
                }

                _lastPublished = status;
            }

            StatusChanged?.Invoke(status);
        }
    }

    private LiveSyncState ComputeStatusLocked()
    {
        if (_verificationInFlight)
        {
            // The chip reads "Seeking…" while the verification range plays out.
            return new LiveSyncState(LiveSyncStateKind.SyncedSeekPending);
        }

        if (_demoPathUnavailable)
        {
            return new LiveSyncState(LiveSyncStateKind.Degraded, "Requires a demo with a local file path.");
        }

        if (_degradedReason is not null)
        {
            return new LiveSyncState(LiveSyncStateKind.Degraded, _degradedReason,
                RemoteDemoPath: _remoteDemoPath);
        }

        if (_loadInFlight)
        {
            return new LiveSyncState(LiveSyncStateKind.LoadingDemo);
        }

        if (_desiredDemo is null || _believedDemo is null)
        {
            return new LiveSyncState(LiveSyncStateKind.ConnectedIdle);
        }

        if (_pendingSeekTarget is not null || _seekSlotArmed)
        {
            return new LiveSyncState(LiveSyncStateKind.SyncedSeekPending);
        }

        return _believedPlaying
            ? new LiveSyncState(LiveSyncStateKind.SyncedFollowing)
            : new LiveSyncState(LiveSyncStateKind.SyncedHolding, IsInferred: _inferredPause);
    }

    /// <summary>The pump's command alphabet (singletons where payload-free).</summary>
    private abstract record SyncCommand
    {
        public static readonly SyncCommand Close = new CloseCommand();
        public static readonly SyncCommand SeekMarker = new SeekMarkerCommand();
        public static readonly SyncCommand Play = new PlayCommand();
        public static readonly SyncCommand Pause = new PauseCommand();

        public sealed record Load(string Path) : SyncCommand;

        public sealed record Timescale(double Value) : SyncCommand;

        public sealed record Spectate(string PlayerName) : SyncCommand;

        public sealed record CloseCommand : SyncCommand;

        public sealed record SeekMarkerCommand : SyncCommand;

        public sealed record PlayCommand : SyncCommand;

        public sealed record PauseCommand : SyncCommand;
    }
}
