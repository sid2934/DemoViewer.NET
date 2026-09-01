namespace DemoViewer.NET.Services.LiveSync;

/// <summary>
///     The live-sync engine's lifecycle position.
///     Kinds map 1:1 onto the status-chip rows in the design notes in git history. The UI
///     derives dot colour/shape and label from this plus <see cref="LiveSyncState.IsInferred" />.
/// </summary>
public enum LiveSyncStateKind
{
    /// <summary>No session. The chip shows "Off"; enabling starts a session.</summary>
    Disconnected,

    /// <summary>The in-process gRPC host (Kestrel :50051) is starting.</summary>
    HostStarting,

    /// <summary>CS2 (or the mock server) is being launched.</summary>
    LaunchingCs2,

    /// <summary>Waiting for the CS2 plugin to dial back in (real CS2: up to ~2 min).</summary>
    Connecting,

    /// <summary>Session up, no demo loaded in CS2. A DV demo load transitions to <see cref="LoadingDemo" />.</summary>
    ConnectedIdle,

    /// <summary>A LoadDemo command is in flight (real CS2: tens of seconds).</summary>
    LoadingDemo,

    /// <summary>Synced; both sides paused at the same position.</summary>
    SyncedHolding,

    /// <summary>Synced; CS2 is playing and DV follows its tick (CS2 is the clock master).</summary>
    SyncedFollowing,

    /// <summary>Synced; a seek is in flight awaiting confirmation.</summary>
    SyncedSeekPending,

    /// <summary>
    ///     Sync is up but a fact is genuinely uncertain (unconfirmed seek, unknown CS2 demo state,
    ///     demo without a local path). <see cref="LiveSyncState.Reason" /> carries the copy describing what's uncertain.
    /// </summary>
    Degraded,

    /// <summary>
    ///     Session lost or failed (CS2 quit, port in use, launch failure). Reconnect = full CS2
    ///     relaunch. <see cref="LiveSyncState.Reason" /> carries the failure copy.
    /// </summary>
    Faulted,

    /// <summary>
    ///     The reel render job owns the CS2 instance. Sync actions are disabled;
    ///     when the reel finishes the engine returns to <see cref="Disconnected" /> with a
    ///     reconnect prompt (never an auto-relaunch).
    /// </summary>
    SuspendedForReel
}

/// <summary>
///     An immutable snapshot of the live-sync engine's state.
/// </summary>
/// <param name="Kind">The lifecycle position.</param>
/// <param name="Reason">
///     Human-readable detail for <see cref="LiveSyncStateKind.Degraded" /> /
///     <see cref="LiveSyncStateKind.Faulted" /> (and optional step text for the working states,
///     e.g. "Waiting for plugin"). Null when the kind speaks for itself.
/// </param>
/// <param name="IsInferred">
///     True when the state is believed-good but not engine-confirmed (v1.0 plugin without
///     demo-state events, e.g. pause inferred from tick silence). The UI renders the hollow-ring
///     dot + "(inferred)" suffix for exactly this flag; confirmed states never
///     set it.
/// </param>
/// <param name="RemoteDemoPath">
///     Set (with <see cref="LiveSyncStateKind.Degraded" />) when CS2 reports it is now playing a
///     DIFFERENT demo than DV (v1.1 demo-identity): the path CS2 reported. The flyout
///     offers "Open in DV" for it: never a silent auto-load (decision D7). Null otherwise.
/// </param>
public sealed record LiveSyncState(
    LiveSyncStateKind Kind,
    string? Reason = null,
    bool IsInferred = false,
    string? RemoteDemoPath = null)
{
    /// <summary>The canonical idle state.</summary>
    public static LiveSyncState Disconnected { get; } = new(LiveSyncStateKind.Disconnected);

    /// <summary>True in any of the three Synced sub-states.</summary>
    public bool IsSynced => Kind is LiveSyncStateKind.SyncedHolding
        or LiveSyncStateKind.SyncedFollowing
        or LiveSyncStateKind.SyncedSeekPending;

    /// <summary>True while a session is being brought up or a demo pushed (pulsing-dot states).</summary>
    public bool IsWorking => Kind is LiveSyncStateKind.HostStarting
        or LiveSyncStateKind.LaunchingCs2
        or LiveSyncStateKind.Connecting
        or LiveSyncStateKind.LoadingDemo;

    /// <summary>
    ///     True whenever a session (and thus a CS2/mock process and a patched install in real mode)
    ///     may exist: everything except Disconnected and Faulted. NOTE: Faulted is session-inactive
    ///     but NOT resource-free: the engine keeps the gRPC host (port 50051) alive across Faulted
    ///     for fast retry; resource ownership is the engine's own probe, not a state-kind question.
    /// </summary>
    public bool IsSessionActive => Kind is not (LiveSyncStateKind.Disconnected or LiveSyncStateKind.Faulted);
}
