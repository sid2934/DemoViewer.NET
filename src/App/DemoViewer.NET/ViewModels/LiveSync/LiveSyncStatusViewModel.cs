#region

using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Playback;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.LiveSync;

/// <summary>
///     Maps the engine-side <see cref="ILiveSyncService" /> state onto the status-strip chip and its flyout
///     . It owns a single reusable
///     <see cref="StatusChipViewModel" /> (the chip the shell shows) whose <c>FlyoutContent</c> is this VM
///     itself: the app <c>ViewLocator</c> resolves <c>Views/LiveSync/LiveSyncStatusView</c> for the flyout
///     body, so there is no VM→View reference here.
///     <para>
///         Sync is outbound-only today: no inferred states occur yet (<see cref="LiveSyncState.IsInferred" />
///         is always false today), but the hollow-ring path is wired against the contract flag. The chip is
///         added to <c>MainViewModel.Chips</c> only while the <c>chrome.livesync</c> gate is on AND the host
///         provides an engine (desktop); a session never auto-starts.
///     </para>
/// </summary>
public sealed partial class LiveSyncStatusViewModel : ViewModelBase, IDisposable, ILiveSyncHudState
{
    // The chrome.livesync gate, folded into the 2D HUD projection's IsActive. Null (tests / capture)
    // ⇒ the gate is treated as enabled, so the HUD shows whenever the session state is non-Disconnected.
    private readonly Func<bool>? _isHudGateEnabled;
    private readonly ILiveSyncService _liveSync;
    private readonly IModuleContext? _moduleContext;
    private readonly Func<string, Task>? _openDemoInDv;
    private readonly Action _openSettings;
    private readonly PlaybackController _playback;

    [ObservableProperty]
    private bool _canOpenRemoteDemo;

    [ObservableProperty]
    private string _demoBindingText = "";

    // Diagnostics-pillar logger (v0.6.0, the restore-failure surface shows clean text, this
    // carries the real exception). Lazy: the ambient factory is wired after construction.
    private ILogger? _diagLog;

    private bool _disposed;

    [ObservableProperty]
    private string? _enableDisabledReason;

    private bool _hudActive;

    private EventHandler? _hudChanged;

    // ── 2D HUD projection (ILiveSyncHudState), recomputed in MapState, mirrored from the chip ──
    private LiveSyncHudDot _hudDot;

    [ObservableProperty]
    private bool _isConnectedIdle;

    [ObservableProperty]
    private bool _isDegraded;

    [ObservableProperty]
    private bool _isFaulted;

    // ── Flyout section visibility (mutually exclusive; one is true per state) ──
    [ObservableProperty]
    private bool _isOff;

    [ObservableProperty]
    private bool _isRestoringInstall;

    [ObservableProperty]
    private bool _isSuspended;

    [ObservableProperty]
    private bool _isSynced;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private string _positionText = "";

    // ~2 Hz refresh of the Following position readout ("live position while synced"). Runs ONLY while a
    // Synced sub-state is current (started/stopped from the state mapper); LastCs2DemoTick advances between
    // StateChanged transitions, so without this the flyout Position line would go stale. _positionTimerRunning
    // is the test-observable decision flag (the timer itself never ticks under the headless VM tests).
    private DispatcherTimer? _positionTimer;

    [ObservableProperty]
    private string _reasonText = "";

    // ── CS2-side demo change: "Open in DV" offer, never a silent auto-load ──
    [ObservableProperty]
    private string? _remoteDemoPath;

    [ObservableProperty]
    private string? _restoreFailureText;

    // ── Crash recovery: probed once at construction (host start) ──
    [ObservableProperty]
    private bool _showLeftoverRestoreOffer;

    // ── Demo-path guard ──
    [ObservableProperty]
    private bool _showNoPathWarning;

    [ObservableProperty]
    private bool _showUntestedVersionsNote;

    // Shown in the Synced/Degraded flyout sections when the connected plugin is a
    // v1.0-era build that advertised nothing (Capabilities.IsV10Baseline). Capabilities present-but-partial
    // shows NO note (the flyout stays lean, no matrix enumeration in v1).
    [ObservableProperty]
    private bool _showV10BaselineNote;

    // ── Flyout content (per-state prose + rows) ──
    [ObservableProperty]
    private string _stateHeadline = "";

    [ObservableProperty]
    private string _stepText = "";

    [ObservableProperty]
    private string _versionsText = "";

    /// <summary>
    ///     Constructs the mapper over the live engine. Seeds the chip from the CURRENT
    ///     <see cref="ILiveSyncService.State" /> (not the first transition, so the chip is never blank), then
    ///     tracks <see cref="ILiveSyncService.StateChanged" /> and the host's demo-reset signal.
    /// </summary>
    public LiveSyncStatusViewModel(
        ILiveSyncService liveSync, IModuleContext? moduleContext, PlaybackController playback, Action openSettings,
        Func<string, Task>? openDemoInDv = null, Func<bool>? isHudGateEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(liveSync);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(openSettings);
        _liveSync = liveSync;
        _moduleContext = moduleContext;
        _playback = playback;
        _openSettings = openSettings;
        _openDemoInDv = openDemoInDv;
        _isHudGateEnabled = isHudGateEnabled;

        Chip = new StatusChipViewModel
        {
            FlyoutContent = this
        };

        _liveSync.StateChanged += OnStateChanged;
        if (_moduleContext is not null)
        {
            _moduleContext.DemoReset += OnDemoReset;
        }

        RefreshDemoBinding();
        MapState(_liveSync.State);

        // Probe the CS2 install for a crashed prior session's leftovers exactly once, at
        // host start (this VM is constructed when the engine attaches): the offer surfaces in
        // the Off flyout BEFORE any session start. Detection never errors (null → no offer).
        _ = ProbeLeftoverModificationsAsync();
    }

    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger("App.LiveSync");

    /// <summary>The status-strip chip this VM drives (added to <c>MainViewModel.Chips</c> when gated on).</summary>
    public StatusChipViewModel Chip { get; }

    // Whether the current demo has a real, rooted, on-disk path: CSVG needs a host file path. A bare
    // filename (WASM / non-local picker) is not usable, so Enable / Re-sync are disabled with the path note.
    private bool HasRootedDemoPath
    {
        get
        {
            string? path = _moduleContext?.DemoPath;
            return !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && File.Exists(path);
        }
    }

    private bool HasDemoLoaded => !string.IsNullOrEmpty(_moduleContext?.DemoPath);

    /// <summary>Whether the informed-launch "Enable Live Sync…" action can fire.</summary>
    public bool CanEnable => HasDemoLoaded && HasRootedDemoPath;

    /// <summary>Whether "Re-sync" can fire: needs a rooted demo path and an active session.</summary>
    public bool CanResync => HasRootedDemoPath && _liveSync.State.IsSessionActive;

    /// <summary>
    ///     Whether DV playback speed is currently pinned to 1×. True only in a Synced sub-state
    ///     AND when the connected plugin can't mirror speed; a v1.1 plugin that advertises
    ///     <see cref="LiveSyncCapabilities.TimescaleSet" /> keeps Speed a user-controlled, mirrored
    ///     control-plane property. Same predicate as <c>MainViewModel.IsPlaybackSpeedLocked</c> and the
    ///     <see cref="OnStateChanged" /> speed-lock guard, so the flyout Speed row can never disagree with
    ///     the NavStrip's actual lock behaviour.
    /// </summary>
    public bool IsSpeedLocked =>
        _liveSync.State.IsSynced && !(_liveSync.Capabilities?.TimescaleSet ?? false);

    /// <summary>Test seam: whether the Synced-only ~2 Hz position refresh timer is currently running.</summary>
    internal bool IsPositionTimerRunning { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPositionTimer();
        _liveSync.StateChanged -= OnStateChanged;
        if (_moduleContext is not null)
        {
            _moduleContext.DemoReset -= OnDemoReset;
        }
    }

    // ── ILiveSyncHudState (the 2D indicator's read-only view) ────────────────────────────────────────
    bool ILiveSyncHudState.IsActive => _hudActive;
    LiveSyncHudDot ILiveSyncHudState.Dot => _hudDot;
    bool ILiveSyncHudState.IsPulsing => Chip.IsPulsing;
    bool ILiveSyncHudState.IsHollow => Chip.IsHollow;
    string ILiveSyncHudState.Label => Chip.Label;

    event EventHandler? ILiveSyncHudState.Changed
    {
        add => _hudChanged += value;
        remove => _hudChanged -= value;
    }

    private async Task ProbeLeftoverModificationsAsync()
    {
        try
        {
            bool leftovers = await _liveSync.HasLeftoverInstallModificationsAsync().ConfigureAwait(true);
            if (!_disposed)
            {
                ShowLeftoverRestoreOffer = leftovers;
            }
        }
        catch
        {
            // Detection is best-effort by contract; an error means no offer, never a surface.
        }
    }

    /// <summary>
    ///     Offered restore: un-patches the CS2 install a crashed session left modified.
    ///     Failure surfaces inline with the `csvg restore` manual-fallback copy from the engine.
    /// </summary>
    [RelayCommand]
    private async Task RestoreInstall()
    {
        IsRestoringInstall = true;
        RestoreFailureText = null;
        try
        {
            await _liveSync.RestoreInstallAsync().ConfigureAwait(true);
            ShowLeftoverRestoreOffer = false;
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "restore the CS2 install", ex);
            // Prefixed, user-phrased text (v0.6.0, was a bare ex.Message); the adjacent manual-
            // fallback copy still points at `csvg restore` for the by-hand path.
            RestoreFailureText = UserFacingError.Describe("restore the CS2 install", ex);
        }
        finally
        {
            IsRestoringInstall = false;
        }
    }

    // ── Commands (per-state action sets) ────────────────────────────────────────────────────────────

    /// <summary>Informed launch (Off state): the copy above the button is the consent; this starts a session.</summary>
    [RelayCommand(CanExecute = nameof(CanEnable))]
    private async Task Enable()
    {
        try
        {
            await _liveSync.EnableAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled via Disable, the state machine already reflects the teardown.
        }
    }

    /// <summary>Stops the session (kills CS2, restores install), the Disable / Cancel action.</summary>
    [RelayCommand]
    private Task Disable() => _liveSync.DisableAsync();

    /// <summary>Re-pushes DV's demo + position (Synced / Degraded recovery).</summary>
    [RelayCommand(CanExecute = nameof(CanResync))]
    private Task Resync() => _liveSync.ResyncAsync();

    /// <summary>Faulted recovery: full relaunch = Disable then Enable ("Reconnect (relaunch CS2)").</summary>
    [RelayCommand]
    private async Task Reconnect()
    {
        try
        {
            await _liveSync.DisableAsync().ConfigureAwait(true);
            await _liveSync.EnableAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-reconnect, the state machine reflects it.
        }
    }

    /// <summary>Opens Settings → Live Sync (the Off-state ".ghost" action / the non-dev opt-in path).</summary>
    [RelayCommand]
    private void OpenLiveSyncSettings() => _openSettings();

    /// <summary>
    ///     Opens the demo CS2 switched to in DemoViewer, the explicit,
    ///     never-silent adoption of a CS2-side demo change. Loading it fires DemoReset, which
    ///     re-pushes intent and clears the Degraded offer.
    /// </summary>
    [RelayCommand]
    private async Task OpenRemoteDemo()
    {
        if (_openDemoInDv is null || RemoteDemoPath is not { } path)
        {
            return;
        }

        try
        {
            await _openDemoInDv(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Surface the open failure inline in the Degraded flyout section (bound ReasonText).
            // Do NOT touch RestoreFailureText here. That's the unrelated crash-recovery surface.
            ReasonText = $"Could not open the demo — {ex.Message}";
        }
    }

    // ── Engine → UI mapping ─────────────────────────────────────────────────────────────────────────

    // StateChanged is raised on the UI thread (ILiveSyncService contract), so we touch bound state directly.
    private void OnStateChanged(object? sender, LiveSyncStateChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Entering any Synced sub-state locks DV playback to 1×, but only when the plugin
        // cannot mirror speed; with the v1.1 "timescale-set" capability Speed is a mirrored
        // control-plane property and stays user-controlled.
        if (e.Current.IsSynced && !e.Previous.IsSynced && !(_liveSync.Capabilities?.TimescaleSet ?? false))
        {
            _playback.Speed = 1.0;
        }

        MapState(e.Current);
    }

    private void OnDemoReset()
    {
        RefreshDemoBinding();
        MapState(_liveSync.State);
    }

    // The single state→chip + state→flyout map. Called at construction and on every transition.
    private void MapState(LiveSyncState state)
    {
        // The live position refresh runs ONLY while a Synced sub-state is current: stop it here and let
        // MapSynced re-start it for the synced kinds (idempotent; "live position while synced").
        StopPositionTimer();

        // Clear any stale open-remote-demo failure on a fresh mapping (the OpenRemoteDemo catch
        // sets ReasonText without re-mapping, so it persists until the next real state transition).
        ReasonText = "";

        // Chip dot state + label. Off is a SOLID dim dot; hollow ring is inferred-only.
        bool inferred = state.IsInferred;
        Chip.IsHollow = inferred;

        switch (state.Kind)
        {
            case LiveSyncStateKind.Disconnected:
                SetChip(StatusChipDotState.Off, false, "CS2 · Off", null);
                ShowSection(true);
                StateHeadline = "Off — not connected to CS2.";
                break;

            case LiveSyncStateKind.HostStarting:
            case LiveSyncStateKind.LaunchingCs2:
            case LiveSyncStateKind.Connecting:
            case LiveSyncStateKind.LoadingDemo:
                SetChip(StatusChipDotState.Working, true, "CS2 · " + WorkingLabel(state.Kind), null);
                ShowSection(working: true);
                StateHeadline = "Connecting…  (this can take up to ~2 min)";
                StepText = "Step: " + (state.Reason ?? WorkingStep(state.Kind));
                break;

            case LiveSyncStateKind.ConnectedIdle:
                // Session up, no demo in CS2: a solid AccentInteractive dot, not pulsing.
                SetChip(StatusChipDotState.Working, false, "CS2 · Connected (no demo)", null);
                ShowSection(connectedIdle: true);
                StateHeadline = "Connected — no demo loaded in CS2.";
                break;

            case LiveSyncStateKind.SyncedHolding:
            case LiveSyncStateKind.SyncedFollowing:
            case LiveSyncStateKind.SyncedSeekPending:
                MapSynced(state);
                break;

            case LiveSyncStateKind.Degraded:
                SetChip(StatusChipDotState.Degraded, false,
                    state.RemoteDemoPath is not null ? "CS2 · Demo changed" : "CS2 · Seek unconfirmed",
                    state.Reason);
                ShowSection(degraded: true);
                StateHeadline = state.Reason
                                ?? "Seek unconfirmed — CS2 did not confirm the last seek; showing CS2's reported position.";
                RefreshVersions();
                break;

            case LiveSyncStateKind.Faulted:
                SetChip(StatusChipDotState.Error, false, "CS2 · Disconnected", state.Reason);
                ShowSection(faulted: true);
                StateHeadline = state.Reason ?? "Disconnected — CS2 quit.";
                break;

            case LiveSyncStateKind.SuspendedForReel:
                SetChip(StatusChipDotState.Off, false, "CS2 · Paused for reel render", null);
                ShowSection(suspended: true);
                StateHeadline = "Paused for reel render.";
                break;
        }

        // Offer surface: present only while the engine carries a CS2-side demo change.
        RemoteDemoPath = state.RemoteDemoPath;
        CanOpenRemoteDemo = state.RemoteDemoPath is not null && _openDemoInDv is not null;

        RefreshGuards();
        OnPropertyChanged(nameof(IsSpeedLocked));
        UpdateHudProjection(state);
    }

    // Projects the just-mapped chip state onto the engine-free 2D HUD contract and notifies the 2D
    // tab. Label / pulse / hollow mirror the chip verbatim; only the dot bucket + IsActive are HUD-specific
    // (Off is overloaded on the chip between Disconnected and Suspended, so the bucket maps fresh from Kind).
    private void UpdateHudProjection(LiveSyncState state)
    {
        _hudActive = (_isHudGateEnabled?.Invoke() ?? true)
                     && state.Kind != LiveSyncStateKind.Disconnected;

        _hudDot = state.Kind switch
        {
            LiveSyncStateKind.Disconnected => LiveSyncHudDot.None,
            LiveSyncStateKind.SyncedHolding
                or LiveSyncStateKind.SyncedFollowing
                or LiveSyncStateKind.SyncedSeekPending => LiveSyncHudDot.Good,
            LiveSyncStateKind.Degraded => LiveSyncHudDot.Degraded,
            LiveSyncStateKind.Faulted => LiveSyncHudDot.Error,
            _ => LiveSyncHudDot.Working // HostStarting / LaunchingCs2 / Connecting / LoadingDemo / ConnectedIdle / SuspendedForReel
        };

        _hudChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Re-raises the 2D HUD <see cref="ILiveSyncHudState.Changed" /> when the <c>chrome.livesync</c> gate
    ///     flips (the shell calls this from its chip reconcile), so the 2D indicator shows/hides live without
    ///     a tab re-activation: <see cref="ILiveSyncHudState.IsActive" /> folds the gate in.
    /// </summary>
    public void NotifyHudGateChanged()
    {
        _hudActive = (_isHudGateEnabled?.Invoke() ?? true)
                     && _liveSync.State.Kind != LiveSyncStateKind.Disconnected;
        _hudChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MapSynced(LiveSyncState state)
    {
        // Following / SeekPending pulse; Holding is a steady solid green. Inferred pauses render hollow.
        bool seeking = state.Kind == LiveSyncStateKind.SyncedSeekPending;
        string label = state.IsInferred
            ? "CS2 · Paused (inferred)"
            : state.Kind switch
            {
                LiveSyncStateKind.SyncedFollowing => "CS2 · Following",
                LiveSyncStateKind.SyncedSeekPending => "CS2 · Seeking…",
                _ => "CS2 · Synced (paused)"
            };
        SetChip(StatusChipDotState.Good, seeking || state.Kind == LiveSyncStateKind.SyncedFollowing,
            label, null);

        ShowSection(synced: true);
        StateHeadline = state.IsInferred
            ? "Paused (inferred) — CS2 stopped sending ticks; it may be paused, at demo end, or unresponsive."
            : state.Kind switch
            {
                LiveSyncStateKind.SyncedFollowing =>
                    "Following — CS2 is playing; DemoViewer is following its tick.",
                LiveSyncStateKind.SyncedSeekPending =>
                    "Seeking… — waiting for CS2 to confirm the seek.",
                _ => "Synced (paused) — CS2 and DemoViewer are both paused at the same position."
            };

        RefreshPositionText();
        RefreshVersions();

        // Keep the Following position readout live: LastCs2DemoTick advances between StateChanged
        // transitions, so poll it at ~2 Hz for the duration of the Synced sub-state.
        StartPositionTimer();
    }

    // Reads the current CS2 demo tick into the flyout Position line. Shared by the state mapper and the
    // ~2 Hz refresh timer.
    private void RefreshPositionText()
    {
        long? tick = _liveSync.LastCs2DemoTick;
        PositionText = tick is { } t
            ? "tick " + t.ToString("N0", CultureInfo.CurrentCulture)
            : "—";
    }

    // Starts the ~2 Hz position refresh if not already running. The timer is lazily created so the pure-VM
    // tests (no dispatcher pump) never depend on it ticking: they assert _positionTimerRunning instead.
    private void StartPositionTimer()
    {
        if (IsPositionTimerRunning || _disposed)
        {
            return;
        }

        IsPositionTimerRunning = true;
        _positionTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnPositionTick);
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        IsPositionTimerRunning = false;
        _positionTimer?.Stop();
    }

    private void OnPositionTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RefreshPositionText();
    }

    // Recompute the demo-binding row + guards from the current demo path.
    private void RefreshDemoBinding()
    {
        string? path = _moduleContext?.DemoPath;
        string? map = _moduleContext?.MapName;
        if (string.IsNullOrEmpty(path))
        {
            DemoBindingText = "No demo loaded.";
        }
        else
        {
            string file = Path.GetFileName(path);
            DemoBindingText = string.IsNullOrEmpty(map) ? file : map + " · " + file;
        }

        RefreshGuards();
    }

    private void RefreshGuards()
    {
        // The ⚠ path note shows only when a demo IS loaded but its path is not rooted/on-disk.
        ShowNoPathWarning = HasDemoLoaded && !HasRootedDemoPath;
        EnableDisabledReason = !HasDemoLoaded
            ? "Open a demo first."
            : !HasRootedDemoPath
                ? "Requires a demo with a local file path."
                : null;

        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(CanResync));
        EnableCommand.NotifyCanExecuteChanged();
        ResyncCommand.NotifyCanExecuteChanged();
    }

    private void RefreshVersions()
    {
        // A v1.0-era plugin advertised NO capabilities (IsV10Baseline) → the "update CSVG
        // for exact pause sync" note. Present-but-partial capabilities show nothing (the flyout stays lean,
        // no matrix enumeration in v1). Null capabilities (no session yet) → no note.
        ShowV10BaselineNote = _liveSync.Capabilities?.IsV10Baseline ?? false;

        LiveSyncVersionInfo? v = _liveSync.Versions;
        if (v is null || v.PluginVersion is null && v.GameVersion is null)
        {
            VersionsText = "Plugin —   ·   Game —";
            ShowUntestedVersionsNote = true; // "untested plugin/game pair", a warning, never a block.
            return;
        }

        VersionsText = "Plugin " + (v.PluginVersion ?? "—") + "   ·   Game " + (v.GameVersion ?? "—");
        ShowUntestedVersionsNote = false;
    }

    private void SetChip(StatusChipDotState dot, bool pulsing, string label, string? tooltip)
    {
        Chip.DotState = dot;
        Chip.IsPulsing = pulsing;
        Chip.Label = label;
        Chip.Tooltip = tooltip;
    }

    private void ShowSection(
        bool off = false, bool working = false, bool connectedIdle = false, bool synced = false,
        bool degraded = false, bool faulted = false, bool suspended = false)
    {
        IsOff = off;
        IsWorking = working;
        IsConnectedIdle = connectedIdle;
        IsSynced = synced;
        IsDegraded = degraded;
        IsFaulted = faulted;
        IsSuspended = suspended;
    }

    private static string WorkingLabel(LiveSyncStateKind kind) => kind switch
    {
        LiveSyncStateKind.LaunchingCs2 => "Launching…",
        LiveSyncStateKind.LoadingDemo => "Loading demo…",
        _ => "Connecting…"
    };

    private static string WorkingStep(LiveSyncStateKind kind) => kind switch
    {
        LiveSyncStateKind.HostStarting => "Starting sync host",
        LiveSyncStateKind.LaunchingCs2 => "Launching CS2",
        LiveSyncStateKind.LoadingDemo => "Loading demo",
        _ => "Waiting for plugin"
    };
}
