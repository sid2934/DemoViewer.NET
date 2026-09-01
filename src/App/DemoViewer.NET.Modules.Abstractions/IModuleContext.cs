namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     The single runtime object a module is handed. READ-ONLY,
///     push/observable, render-frame-coalesced. It deliberately does NOT expose the live
///     <c>EntityTracker</c>, the raw byte buffer, the <c>DemoParser</c>, or any mutator. A module
///     simply has no API to corrupt state (the primary, real guardrail).
/// </summary>
public interface IModuleContext
{
    // ── Identity / lifecycle ──
    /// <summary>True once a demo is loaded.</summary>
    bool HasDemo { get; }

    /// <summary>Demo file path; null on WASM / not-yet-loaded.</summary>
    string? DemoPath { get; }

    /// <summary>
    ///     The map's logical name (e.g. <c>"de_nuke"</c>), host-derived once at load from the demo header
    ///     (<c>ParsedDemo.MapName</c>), the data-driven identity a module uses to select map assets
    ///     (radar, floors, transform), never a behavior branch (map/asset-independence principle). Null
    ///     until a demo is loaded, or for hosts/doubles that don't expose it (default).
    /// </summary>
    string? MapName => null;

    /// <summary>Server tick rate (ticks/second).</summary>
    int TickRate { get; }

    /// <summary>
    ///     The live-sync (CS2) HUD projection for the 2D Playback tab's in-context CS2 indicator
    ///     , or null when no live-sync host is attached (Browser /
    ///     tests / no desktop engine, or the <c>chrome.livesync</c> feature is unavailable). It is a
    ///     read-only, engine-free view (<see cref="ILiveSyncHudState" />) the shell pushes in; a module
    ///     never reaches the live-sync engine itself. Default null for hosts / doubles that don't sync.
    /// </summary>
    ILiveSyncHudState? LiveSyncHud => null;

    // ── Clock (read-only view of PlaybackController) ──
    /// <summary>Current 0-based frame index.</summary>
    int CurrentFrameIndex { get; }

    /// <summary>Current server tick.</summary>
    int CurrentTick { get; }

    /// <summary>Whether auto-play is running.</summary>
    bool IsPlaying { get; }

    /// <summary>Playback speed multiplier.</summary>
    double Speed { get; }

    // ── Pull access (for on-activation resync and lazy detail) ──
    /// <summary>Read-only entity view at the current tick.</summary>
    IReadOnlyEntityView Entities { get; }

    /// <summary>Stable per-player identity roster (slot / steamID / name); no team.</summary>
    IReadOnlyList<PlayerRosterEntry> Players { get; }

    /// <summary>
    ///     Host-joined per-tick player state at the CURRENT tick, for on-activation resync (the same
    ///     shape the <c>Advanced</c> snapshot carries). Transient. Copy what you need.
    /// </summary>
    IReadOnlyList<IPlayerState> CurrentPlayers { get; }

    // ── Semantic (game-event) forward-nav (Phase E) ──
    // A module drives "jump to the next event of its own type" through these. They delegate to the shell-owned
    // SemanticNavigator (the same demo-derived event index the global nav strip uses), so a module never
    // re-scans frames or owns a navigator. Default no-ops keep these optional for hosts/doubles that don't
    // expose navigation.

    /// <summary>
    ///     The demo-derived game-event names present in this demo (e.g. "player_death", "bomb_planted").
    ///     A module builds its own forward-nav filter from this set so it only offers jumps the demo can
    ///     actually satisfy (asset/demo-independent). Empty when no navigator is exposed.
    /// </summary>
    IReadOnlyCollection<string> AvailableEventNames => Array.Empty<string>();

    // ── Timeline / transport seams (Playback2D v2 A1) ──
    // All additive with default implementations, so every existing host and hand-rolled test double keeps
    // compiling untouched.

    /// <summary>Total frames in the loaded demo, 0 when none. The timeline's x-axis domain.</summary>
    int TotalFrames => 0;

    /// <summary>
    ///     True while playback speed is pinned by the host (a Live Sync session without the plugin's
    ///     timescale capability). A module surfaces the lock rather than fighting it.
    /// </summary>
    bool IsSpeedLocked => false;

    /// <summary>
    ///     The live feature-gate projection, or <c>null</c> for a host / test double that does not
    ///     gate. <b>Null fails OPEN.</b> The shell folds platform ANDs (desktop-only ids) in on its
    ///     side, so a module never re-derives them.
    /// </summary>
    IModuleFeatureGate? Features => null;

    /// <summary>
    ///     The corrected game-clock time (in seconds) at a server tick, the shared "game-seconds-now"
    ///     helper both the round timer and the bomb/defuse timers consume. The naive
    ///     <c>m_flGameStartTime + tick/tickRate</c> reading runs a constant offset (~5.4s on the verified
    ///     demo) ahead of the entity time base that <c>m_fRoundStartTime</c> / <c>m_flC4Blow</c> stamp
    ///     against; the host computes that offset ONCE at load (it has the frame history + the precomputed
    ///     <c>round_freeze_end</c> frames; a module on a seek has neither) and bakes it in here. A module
    ///     can then compute round remaining as <c>m_fRoundStartTime + m_iRoundTime − CurtimeSeconds(tick)</c>
    ///     and bomb detonation remaining as <c>m_flC4Blow − CurtimeSeconds(tick)</c>. Returns the naive
    ///     reading (offset 0) when no <c>round_freeze_end</c> exists to calibrate against (warmup-only /
    ///     truncated demos). Callers tolerate the small residual.
    /// </summary>
    double CurtimeSeconds(int tick);

    // ── Operations a module may REQUEST (it asks the clock; it never moves itself). ──
    // Granted only to modules holding the Playback.Control capability; a read-only visualizer
    // gets the getters but these no-op without the grant.
    /// <summary>Requests a discrete seek to a frame (capability-gated).</summary>
    void RequestSeekToFrame(int frameIndex);

    /// <summary>Requests a discrete seek to the first frame at/after a tick (capability-gated).</summary>
    void RequestSeekToTick(int tick);

    /// <summary>Requests auto-play start (capability-gated).</summary>
    void RequestPlay();

    /// <summary>Requests auto-play pause (capability-gated).</summary>
    void RequestPause();

    // ── Per-frame push (the hot path) ──
    /// <summary>
    ///     Fires on the UI thread, at most once per render frame, ONLY while the module's tab is
    ///     active. Carries a transient snapshot valid ONLY for the duration of the callback. Copy what
    ///     you need, do not retain it.
    /// </summary>
    event Action<IPlaybackSnapshot> Advanced;

    /// <summary>
    ///     Fires on the UI thread when a NEW demo has finished loading and the context's roster / map /
    ///     events / clock have been repopulated, the signal an ACTIVE module uses to FULLY resync (reload
    ///     its map asset, marker labels, grenade trails, kill timeline) to the new demo. It is needed
    ///     because <c>LoadDemo</c> resets the playback clock WITHOUT emitting an <see cref="Advanced" />
    ///     push, so a tab that stays active across a reload (Open-file button or library browser) would
    ///     otherwise keep the PREVIOUS demo's draw-state until the user manually seeks. An INACTIVE module
    ///     needs no subscription. It resyncs on its next <c>OnActivated</c> (already parity with a fresh
    ///     load). Default no-op for hosts / test doubles that never reload a demo.
    /// </summary>
    event Action DemoReset
    {
        add { }
        remove { }
    }

    /// <summary>
    ///     Informs the host the user chose a follow/spectate target in a module surface (the 2D
    ///     tab's Follow-Player pick; slot per the roster). Default no-op: hosts that mirror
    ///     spectating (live CS2 sync) consume it. Fire-and-forget
    ///     module-side; the host owns any downstream effect.
    /// </summary>
    void NotifySpectateTarget(int slot)
    {
    }

    /// <summary>
    ///     Seeks the shared playback clock to the next frame carrying a game event whose name is in
    ///     <paramref name="eventNames" /> (null/empty = any event). No-op if none lie ahead.
    /// </summary>
    void RequestNextEvent(IReadOnlyCollection<string>? eventNames)
    {
    }

    /// <summary>As <see cref="RequestNextEvent" /> but seeks to the previous matching frame.</summary>
    void RequestPrevEvent(IReadOnlyCollection<string>? eventNames)
    {
    }

    /// <summary>
    ///     The WHOLE demo's decoded events of the given name (e.g. "player_death"), each a
    ///     <see cref="GameEventView" /> with enriched <see cref="GameEventView.Fields" /> and its own
    ///     <see cref="GameEventView.Tick" />. Built once and cached by the host from the demo's pre-decoded
    ///     event list (no per-call decode). A module pre-builds its own timeline from this and renders by
    ///     filtering a tick WINDOW, decoupling display from the playback notification cadence (a kill on a
    ///     render-skipped frame is never lost). Order is NOT guaranteed (the parse is two-pass parallel).
    ///     Sort by <see cref="GameEventView.Tick" /> before display if order matters. Empty default for hosts
    ///     / test doubles that don't expose a demo.
    /// </summary>
    IReadOnlyList<GameEventView> GetEventTimeline(string eventName) => Array.Empty<GameEventView>();

    /// <summary>
    ///     First frame index at/after <paramref name="tick" />, or -1 when unknown / past the end.
    ///     Binary search on the host; the seam that lets a module place tick-stamped events on the
    ///     frame-index movement axis without re-scanning frames.
    /// </summary>
    int FrameIndexAtTick(int tick) => -1;

    /// <summary>
    ///     Sorted, de-duplicated FRAME indices carrying <paramref name="eventName" />, the module-facing
    ///     projection of the shell's SemanticNavigator index (the same array its Next/Prev use). Empty
    ///     when the demo lacks the event or the host exposes no navigator.
    /// </summary>
    IReadOnlyList<int> EventFrames(string eventName) => Array.Empty<int>();

    /// <summary>
    ///     Requests a playback-speed change (capability-gated; clamped host-side to [0.25, 8]).
    ///     No-op while <see cref="IsSpeedLocked" />.
    /// </summary>
    void RequestSpeed(double speed)
    {
    }
}
