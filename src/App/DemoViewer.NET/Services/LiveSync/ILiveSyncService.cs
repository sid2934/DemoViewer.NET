namespace DemoViewer.NET.Services.LiveSync;

/// <summary>
///     App-facing contract for the live CS2 playback-sync engine (docs/csvg-integration/
///     implementation-plan.md). The implementation lives in the desktop-only
///     <c>DemoViewer.NET.LiveSync</c> project (CSVG + ASP.NET Core dependencies — WASM poison,
///     so nothing CSVG-typed appears here) and reaches the App through
///     <see cref="DemoViewer.NET.Services.AppHostHooks.LiveSyncFactory" />. Null service = no
///     live-sync host (Browser, tests, non-desktop lifetimes).
///     <para>
///         Threading: <see cref="StateChanged" /> is always raised on the Avalonia UI thread.
///         All members are safe to call from the UI thread; the engine does its own gRPC work
///         off-thread. The surface grows with the phase-2/3 work items — members are added when
///         their behaviour lands, never as stubs.
///     </para>
/// </summary>
public interface ILiveSyncService : IAsyncDisposable
{
    /// <summary>The current engine state (chip + flyout render from this).</summary>
    LiveSyncState State { get; }

    /// <summary>
    ///     The most recent demo tick reported by CS2's tick stream, or null before the first
    ///     update of the current session. Display-only (one-way sync).
    /// </summary>
    long? LastCs2DemoTick { get; }

    /// <summary>Plugin/game version pair from the session handshake; null until reported.</summary>
    LiveSyncVersionInfo? Versions { get; }

    /// <summary>
    ///     The connected plugin's capability projection (degradation matrix), latched when
    ///     the session connects; null while no session is up. <see cref="LiveSyncCapabilities.None" />
    ///     means a v1.0-era plugin — the engine runs the fully-degraded baseline and the UI says so.
    /// </summary>
    LiveSyncCapabilities? Capabilities { get; }

    /// <summary>Raised on the UI thread after every state transition.</summary>
    event EventHandler<LiveSyncStateChangedEventArgs>? StateChanged;

    /// <summary>
    ///     Starts a live session: gRPC host → CS2 (or mock) launch → plugin connection
    ///     (real CS2: up to ~2 min). Valid from <see cref="LiveSyncStateKind.Disconnected" /> and
    ///     <see cref="LiveSyncStateKind.Faulted" /> (a faulted session is stopped first — CSVG's
    ///     recovery contract). Completes when the session reaches
    ///     <see cref="LiveSyncStateKind.ConnectedIdle" />; failures transition to
    ///     <see cref="LiveSyncStateKind.Faulted" /> and surface there rather than throwing,
    ///     except for cancellation.
    /// </summary>
    Task EnableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stops the session (kills CS2, restores the install) and the gRPC host; returns the
    ///     engine to <see cref="LiveSyncStateKind.Disconnected" />. Safe to call in any state.
    /// </summary>
    Task DisableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-pushes DV's full current intent (demo, position, play state) through the sync
    ///     pipeline — the flyout's "Re-sync" action, and the honest recovery from
    ///     <see cref="LiveSyncStateKind.Degraded" />. No-op without an active session.
    /// </summary>
    Task ResyncAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     F2 verify-in-CS2: suspends the sync follower, optionally spectates
    ///     <paramref name="spectateName" /> (exact in-demo roster name), plays the pre/post-roll
    ///     range around <paramref name="frameClockTick" /> live in CS2 (deterministic paused
    ///     arrival), then remote-applies DV's playhead to the trigger frame and resumes the
    ///     follower. <paramref name="frameClockTick" /> is FRAME-CLOCK (<c>RuleChainEvent.Tick</c>
    ///     / <c>GameEvent.GameTick</c> as-is — never <c>−ServerStartTick</c>). Returns false —
    ///     never throws for playback failures — when not currently synced or when CS2 could not
    ///     complete the range; the UI surfaces the enable prompt / failure state.
    /// </summary>
    Task<bool> VerifyMomentAsync(int frameClockTick, int preRollTicks = 192, int postRollTicks = 64,
        string? spectateName = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Crash recovery: probes the real CS2 install for leftover CSVG modifications
    ///     from a crashed prior session (patched <c>gameinfo.gi</c> / surviving plugin files).
    ///     Always false in mock mode or when no CS2 install is found. Runs off-thread.
    /// </summary>
    Task<bool> HasLeftoverInstallModificationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Crash recovery: restores the CS2 install — un-patches <c>gameinfo.gi</c> from
    ///     CSVG's own backup and removes the plugin files. Also the permanent-disable uninstall
    ///     path. Throws with manual-fallback copy (<c>csvg restore</c>) when the install remains
    ///     modified afterwards; callers surface the message.
    /// </summary>
    Task RestoreInstallAsync(CancellationToken cancellationToken = default);
}

/// <summary>The CSVG version handshake pair, as reported by the plugin (either may be unknown).</summary>
/// <param name="PluginVersion">CSVG plugin version (e.g. "1.1.0"), null if not reported.</param>
/// <param name="GameVersion">Running CS2 build number, null if not reported (e.g. mock server).</param>
public sealed record LiveSyncVersionInfo(string? PluginVersion, string? GameVersion);

/// <summary>Payload for <see cref="ILiveSyncService.StateChanged" />.</summary>
public sealed class LiveSyncStateChangedEventArgs(LiveSyncState previous, LiveSyncState current) : EventArgs
{
    /// <summary>The state before the transition.</summary>
    public LiveSyncState Previous { get; } = previous;

    /// <summary>The state after the transition (== <see cref="ILiveSyncService.State" /> at raise time).</summary>
    public LiveSyncState Current { get; } = current;
}
