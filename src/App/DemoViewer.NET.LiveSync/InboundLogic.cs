#region

using Cs2VideoGenerator.Core.Models;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The pure decision half of the CS2→DV mirroring, extracted from the UI-coupled
///     <see cref="InboundSync" /> so the mapping is unit-testable and the injection-channel
///     integration tests can assert decisions against REAL wire events
///     without an Avalonia loop. <see cref="InboundSync.OnDemoState" /> applies the decision.
/// </summary>
public static class InboundLogic
{
    /// <summary>What one observed tick-stream change means (the pump's per-tick decision).</summary>
    public enum TickSignal
    {
        /// <summary>Normal advance (or no previous context / our own seek in flight): nothing to mirror.</summary>
        None,

        /// <summary>A CS2-side user seek: remote-apply DV's playhead to the new tick.</summary>
        UserSeek,

        /// <summary>
        ///     v1.0 only: a restart-like backward jump to near 0, where pause, end, and reload
        ///     are indistinguishable, resolves to Degraded rather than a guess.
        /// </summary>
        DemoStateUnknown
    }

    /// <summary>A CS2-side tick this far from DV's mapped position reads as a user seek.</summary>
    public const int RemoteSeekDistance = 128;

    /// <summary>A CS2-side tick jump beyond this (with no seek of ours in flight) is a user seek.</summary>
    public const int RemoteSeekJump = 128;

    /// <summary>Ticks at or below this reached by a large backward jump read as a demo restart (v1.0).</summary>
    public const int RestartTickCeiling = 64;

    /// <summary>v1.0 inference: tick silence this long while believed playing ⇒ inferred pause.</summary>
    public static readonly TimeSpan TickSilenceWindow = TimeSpan.FromMilliseconds(750);

    /// <summary>
    ///     Classifies one tick-stream change. User seeks surface ONLY as tick-stream jumps on
    ///     EVERY protocol version (they never emit a DemoStateEvent, since those fire only on
    ///     start/stop/pause-flip/path-change transitions), so the jump branch is
    ///     version-independent. The restart branch instead is v1.0-only: a v1.1 restart emits
    ///     real stop/start DemoStateEvents, so its near-zero jumps fall through as user seeks.
    ///     Both branches require an actual observed jump from <paramref name="previousTick" />.
    ///     A bare low tick (a fresh demo ticking 1, 2, … after a demo change or after our own
    ///     seek back to the start) is a normal advance, never a restart.
    /// </summary>
    /// <param name="cs2Tick">The newly observed CS2 demo tick.</param>
    /// <param name="previousTick">The pump's previously observed tick (null = no context yet).</param>
    /// <param name="ownSeekInFlight">True while our own seek is unresolved. Its jump is not a signal.</param>
    /// <param name="demoStateEvents">Whether the plugin advertises "demo-state-events" (v1.1).</param>
    public static TickSignal ClassifyTickAdvance(
        long cs2Tick, long? previousTick, bool ownSeekInFlight, bool demoStateEvents)
    {
        if (previousTick is not long previous || previous == cs2Tick || ownSeekInFlight)
        {
            return TickSignal.None;
        }

        if (!demoStateEvents && cs2Tick <= RestartTickCeiling && previous - cs2Tick > RemoteSeekJump)
        {
            return TickSignal.DemoStateUnknown;
        }

        return Math.Abs(cs2Tick - previous) > RemoteSeekJump ? TickSignal.UserSeek : TickSignal.None;
    }

    /// <summary>
    ///     The v1.0 inferred-pause watchdog decision (fallback): tick silence while believed
    ///     playing ⇒ CS2 is PROBABLY paused. Pure: the pump feeds the elapsed clock.
    /// </summary>
    public static bool ShouldInferPause(bool enginePauseDetection, bool believedFollowing,
        TimeSpan sinceLastTickChange) =>
        !enginePauseDetection && believedFollowing && sinceLastTickChange > TickSilenceWindow;

    /// <summary>
    ///     Maps one <see cref="DemoState" /> to a mirroring decision.
    /// </summary>
    /// <param name="state">The wire event (any origin, but non-User returns None).</param>
    /// <param name="dvPlaying">DV's current play state.</param>
    /// <param name="dvTickError">
    ///     |CS2-reported demo tick mapped to DV clock − DV's current tick|, or null when the
    ///     event carries no tick / no mapper exists.
    /// </param>
    /// <param name="demoIdentity">Whether the plugin advertises "demo-identity" (path trustable).</param>
    /// <param name="dvDemoPath">DV's loaded demo path (basename identity, plugin convention).</param>
    /// <param name="enginePauseDetection">
    ///     Whether the plugin advertises "engine-pause-detection": the same gate
    ///     <c>SyncEngine.NotifyTick</c> applies to this wire fact: <see cref="DemoState.IsPaused" />
    ///     is engine truth only under the token (it reads an unvalidated vtable slot otherwise);
    ///     without it the pause/play mirror stands down and the tick-silence inference carries.
    /// </param>
    public static Decision Decide(DemoState state, bool dvPlaying, long? dvTickError, bool demoIdentity,
        string? dvDemoPath, bool enginePauseDetection = true)
    {
        if (state.Origin != DemoStateOrigin.User)
        {
            // Host-command echoes are the outbound ledger's business.
            return Decision.None;
        }

        if (demoIdentity && !string.IsNullOrEmpty(state.DemoFilePath)
                         && !BasenamesMatch(state.DemoFilePath, dvDemoPath))
        {
            return new Decision(state.DemoFilePath, null, null);
        }

        if (state.IsPlayingDemo == false)
        {
            // Demo ended/closed in CS2: mirror as a pause of DV's playhead.
            return new Decision(null, null, false);
        }

        long? seekTo = state.DemoTick is int tick && dvTickError is > RemoteSeekDistance ? tick : null;

        bool? isPaused = enginePauseDetection ? state.IsPaused : null;
        bool? setPlaying = isPaused switch
        {
            true when dvPlaying => false,
            false when !dvPlaying => true,
            _ => null
        };

        return new Decision(null, seekTo, setPlaying);
    }

    /// <summary>
    ///     CS2 may report an install-relative or differently-rooted path for the same file.
    ///     Basename identity is the same standard the plugin's own load-completion check uses.
    /// </summary>
    public static bool BasenamesMatch(string cs2Path, string? dvPath) =>
        !string.IsNullOrEmpty(dvPath)
        && string.Equals(Path.GetFileName(cs2Path), Path.GetFileName(dvPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     What a user-originated demo-state change means for DV. At most one of
    ///     <paramref name="DemoChangedPath" /> / the position-play pair is set: a demo change
    ///     preempts everything else (the tick/pause refer to the demo CS2 switched TO).
    /// </summary>
    /// <param name="DemoChangedPath">CS2 now plays a different demo: offer it (never auto-load, D7).</param>
    /// <param name="SeekToTick">Remote-apply DV's playhead to this CS2 demo tick.</param>
    /// <param name="SetPlaying">Remote-apply DV's play state (null = leave as is).</param>
    public sealed record Decision(string? DemoChangedPath, long? SeekToTick, bool? SetPlaying)
    {
        public static readonly Decision None = new(null, null, null);

        public bool IsNone => DemoChangedPath is null && SeekToTick is null && SetPlaying is null;
    }
}
