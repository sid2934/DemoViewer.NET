#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.ViewModels.Playback;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     The concrete <see cref="IModuleContext" />. Lives in the App project (which references the
///     parser) because it performs the per-tick HOST PLAYER-JOIN: it reverse-resolves
///     pawn↔slot via <see cref="PawnLookup" /> (the correct <c>m_hController</c> path, never the stale
///     <c>controller.m_hPawn</c>) and reconstructs world position via <see cref="PositionUtil" />, then
///     hands every module a pawn-joined, position-reconstructed <see cref="IPlayerState" /> list. The
///     abstractions assembly stays clean of every parser type, which is what lets a module be written
///     against <see cref="IPlayerState" /> without taking a dependency on the demo parser at all.
///     <para>
///         <b>Read-only by construction</b>: the only handle a module gets. No mutators, no raw
///         tracker, no bytes, no parser (the primary guardrail). The <c>Request*</c> operations route
///         to the controller (capability-gated by the host).
///     </para>
///     <para>
///         <b>Allocation: POOLED.</b> The entity view, per-entity facade, and the ~10
///         <see cref="PooledPlayerState" /> instances are reused and re-aimed each push: the framework
///         allocates nothing per tick on the hot path.
///     </para>
/// </summary>
public sealed class ModuleContext : IModuleContext, ICurrentDemoSource
{
    private readonly PlaybackController _controller;
    private readonly Func<string?> _demoPath;

    // Pooled transient facades / states, re-aimed each push, never reallocated.
    private readonly ReadOnlyEntityView _entityView = new(EmptyEntitySet);

    // The shell-owned semantic navigator (Phase E forward-nav). Optional: null in test harnesses that
    // construct the context without navigation; the AvailableEventNames / Request*Event members no-op then.
    private readonly SemanticNavigator? _navigator;
    private readonly Dictionary<int, EntityState> _pawnBySlot = new(16);
    private readonly List<PooledPlayerState> _playerPool = new(16);
    private readonly List<IPlayerState> _playersView = new(16);
    private readonly PlaybackSnapshot _snapshot;
    private readonly Dictionary<string, IReadOnlyList<GameEventView>> _timelineCache = new();

    // Explicit registration wins over the DI container. Checked fresh on every call, never cached,
    // so a live pack toggle is always reflected.
    private readonly Dictionary<Type, Func<object?>> _serviceLookups = new();
    private IServiceProvider? _services;
    private ExtensionFaults? _faults;
    private readonly MulticastGuard<Action<IPlaybackSnapshot>> _advancedGuard = new("playback frame handler", FaultKind.Recurring);
    private readonly MulticastGuard<Action> _demoResetGuard = new("demo reset handler");

    // The demo's pre-decoded flat event list (set once at load, mirroring SetRoster) + a per-name cache of
    // the projected GameEventView timeline. A module pre-builds its own windowed view from a timeline; the
    // host materializes a given name's views ONCE on first request (so high-volume names it never asks for,
    // e.g. weapon_fire, are never built). Reset on unload.
    private IReadOnlyList<GameEvent> _allGameEvents = Array.Empty<GameEvent>();

    // Shared game-clock calibration (set once per demo at load by the shell, mirroring SetRoster). When
    // not calibrated CurtimeSeconds returns the naive reading (clockBase 0 → tick/tickRate).
    private double _clockBase;

    // The currently-loaded parsed demo (set once at load, mirroring SetRoster). Exposed to first-party
    // modules via ICurrentDemoSource so the Rulesets v2 Workbench can evaluate against it: the Parser
    // type stays OFF the minimal IModuleContext abstraction.

    // The live-sync HUD projection, set ONCE by the shell in AttachLiveSync (never cleared:
    // the projection folds the chrome.livesync gate into its own IsActive). Null on Browser / tests / no
    // desktop engine → the 2D indicator is simply absent.

    // The map's logical name, set once at load (mirrors SetRoster). Null until a demo is loaded.

    // Stable identity roster (slot / steamID / name, NO team).
    private List<PlayerRosterEntry> _roster = new();

    // The host's speed-lock predicate (a Live Sync session without the plugin's timescale capability).
    // Wired once by the shell via SetSpeedLock, exactly like SetLiveSyncHud. Null → never locked.
    private Func<bool>? _speedLocked;

    public ModuleContext(
        PlaybackController controller,
        Func<string?> demoPath,
        SemanticNavigator? navigator = null)
    {
        _controller = controller;
        _demoPath = demoPath;
        _navigator = navigator;
        _snapshot = new PlaybackSnapshot(this);

        // Re-raise the controller's coalesced PlaybackFrame as the module-facing IPlaybackSnapshot,
        // ONLY while a module is subscribed (active tab). Two deliberate layers.
        _controller.Advanced += OnControllerAdvanced;

        // ExportHost is set later, by SetExportHost; the closure reads it live rather than snapshotting
        // the (currently null) property.
        RegisterService<Playback2DExportHost>(() => ExportHost);
    }

    private static EntitySet EmptyEntitySet { get; } = new();

    /// <summary>
    ///     The 2D tab's video-export host, or null when this build has none (browser, tests, designer),
    ///     in which case the tab's Export affordance stays hidden rather than half-wired.
    ///     <para>
    ///         Deliberately NOT on <see cref="IModuleContext" />: that interface exposes no tracker, no
    ///         raw buffer and no parser, and an export needs the frame list. It is a first-party
    ///         capability the shell hands one tab explicitly, exactly like <see cref="SetLiveSyncHud" />.
    ///     </para>
    /// </summary>
    public Playback2DExportHost? ExportHost { get; private set; }

    /// <inheritdoc />
    public ParsedDemo? CurrentDemo { get; private set; }

    public bool HasDemo => _controller.HasDemo;
    public string? DemoPath => _demoPath();
    public string? MapName { get; private set; }
    public string? DemoSha256 { get; private set; }

    public ILiveSyncHudState? LiveSyncHud { get; private set; }

    public int TickRate => _controller.TickRate;
    public int CurrentFrameIndex => _controller.CurrentFrameIndex;
    public int CurrentTick => _controller.CurrentTick;
    public bool IsPlaying => _controller.IsPlaying;
    public double Speed => _controller.Speed;

    // CurtimeSeconds(tick) = tick/tickRate − clockBase (m_flGameStartTime cancels out of the consume
    // path; see GameClock). One subtraction per call; no entity reads. clockBase 0 = naive fallback.
    public double CurtimeSeconds(int tick)
    {
        int rate = TickRate > 0 ? TickRate : 64;
        return tick / (double)rate - _clockBase;
    }

    public void RequestSeekToFrame(int frameIndex) => _controller.SeekToFrame(frameIndex);
    public void RequestSeekToTick(int tick) => _controller.SeekToTick(tick);
    public void RequestPlay() => _controller.Play();
    public void RequestPause() => _controller.Pause();

    // ── Timeline / transport seams (Playback2D v2 A1) ──
    public int TotalFrames => _controller.TotalFrames;

    // Frame clock, read off the frame list the controller already holds rather than ParsedDemo.TickCount:
    // the two agree on every measured demo, and the frame list is what the playhead actually walks.
    public int FirstTick => _controller.Frames is { Count: > 0 } frames ? frames[0].ServerTick : 0;
    public int LastTick => _controller.Frames is { Count: > 0 } frames ? frames[^1].ServerTick : 0;
    public int FrameIndexAtTick(int tick) => _controller.FrameIndexAtTick(tick);

    public IReadOnlyList<int> EventFrames(string eventName) =>
        _navigator is not null && eventName is not null
                               && _navigator.EventBoundaryFramesByName.TryGetValue(eventName, out int[]? frames)
            ? frames
            : Array.Empty<int>();

    public bool IsSpeedLocked => _speedLocked?.Invoke() ?? false;

    // Clamp-free: the controller clamps in OnSpeedChanged. A locked session refuses outright rather than
    // letting a module keystroke desync a Synced game.
    public void RequestSpeed(double speed)
    {
        if (!IsSpeedLocked)
        {
            _controller.Speed = speed;
        }
    }

    /// <inheritdoc />
    public IModuleFeatureGate? Features { get; private set; }

    // Phase E forward-nav: delegate to the shell-owned navigator (the seek lands on the shared clock and
    // re-publishes to every module). null navigator (test harness) → empty set / no-op.
    public IReadOnlyCollection<string> AvailableEventNames =>
        _navigator is null
            ? Array.Empty<string>()
            : _navigator.EventBoundaryFramesByName.Keys.ToArray();

    public void RequestNextEvent(IReadOnlyCollection<string>? eventNames) => _navigator?.NextEvent(eventNames);
    public void RequestPrevEvent(IReadOnlyCollection<string>? eventNames) => _navigator?.PrevEvent(eventNames);

    public event Action<IPlaybackSnapshot>? Advanced;

    // Implicitly implements IModuleContext.DemoReset, overriding its default no-op (same field-like /
    // interface-abstract split as Advanced above). Raised by the host via RaiseDemoReset after a reload.
    public event Action? DemoReset;

    /// <inheritdoc cref="IModuleContext.NotifySpectateTarget" />
    public void NotifySpectateTarget(int slot) => SpectateTargetChanged?.Invoke(slot);

    public IReadOnlyEntityView Entities
    {
        get
        {
            AimEntityView();
            return _entityView;
        }
    }

    public IReadOnlyList<PlayerRosterEntry> Players => _roster;

    public IReadOnlyList<IPlayerState> CurrentPlayers
    {
        get
        {
            RebuildPlayerJoin();
            return _playersView;
        }
    }

    public IReadOnlyList<GameEventView> GetEventTimeline(string eventName)
    {
        if (_timelineCache.TryGetValue(eventName, out IReadOnlyList<GameEventView>? cached))
        {
            return cached;
        }

        List<GameEventView> views = new();
        foreach (GameEvent e in _allGameEvents)
        {
            if (e.Name == eventName)
            {
                views.Add(GameEventViewFactory.FromEvent(e));
            }
        }

        _timelineCache[eventName] = views;
        return views;
    }

    /// <summary>
    ///     Signals every ACTIVE module that a new demo has been loaded and this context's roster / map /
    ///     events / clock are now repopulated: call it AFTER the <c>Set*</c> methods on a (re)load. A module
    ///     can only OBSERVE this via <see cref="IModuleContext.DemoReset" /> (read-only guardrail); only
    ///     the host raises it. Inactive modules aren't subscribed and resync on their next activation instead.
    /// </summary>
    public void RaiseDemoReset()
    {
        if (_faults is null)
        {
            DemoReset?.Invoke();
        }
        else
        {
            _demoResetGuard.Invoke(_faults, DemoReset, 0, static (h, _) => h());
        }
    }

    /// <summary>
    ///     Contains extension handlers on <see cref="Advanced" /> and <see cref="DemoReset" />: a handler whose
    ///     method is an extension's is run as the extension's, so a throw neither stops the frame nor skips the
    ///     subscribers after it. Host handlers still throw.
    /// </summary>
    public void SetFaults(ExtensionFaults? faults) => _faults = faults;

    /// <summary>
    ///     Host-side relay for <see cref="IModuleContext.NotifySpectateTarget" /> (csvg-integration
    ///     the spectate seam): module surfaces report the user's follow pick; the live-sync engine
    ///     subscribes here (the module-owned tab VMs are lazily built, so this context, which
    ///     always exists, is the reachable seam).
    /// </summary>
    public event Action<int>? SpectateTargetChanged;


    /// <summary>
    ///     Sets the stable identity roster on demo load. Identity only: team is volatile and
    ///     comes from the per-tick join. Reset to empty on unload.
    /// </summary>
    public void SetRoster(IEnumerable<PlayerRosterEntry> roster) => _roster = roster.ToList();

    /// <summary>
    ///     Sets the map's logical name on demo load (mirrors <see cref="SetRoster" />): the host reads it
    ///     from <c>ParsedDemo.MapName</c>. Pass null on unload. Lets a module select map assets by identity.
    /// </summary>
    public void SetMapName(string? mapName) => MapName = mapName;

    /// <summary>
    ///     Sets the loaded demo's content hash on load (mirrors <see cref="SetMapName" />): the shell hashes
    ///     the raw bytes once, for its own breakpoint key, and hands the same value here. Pass null on unload.
    /// </summary>
    public void SetDemoSha256(string? sha256) => DemoSha256 = sha256;

    /// <summary>Sets the loaded demo on load / clears it on unload (mirrors <see cref="SetRoster" />).</summary>
    public void SetDemo(ParsedDemo? demo) => CurrentDemo = demo;

    /// <summary>
    ///     Wires the live-sync HUD projection the 2D indicator reads. Called once by the shell
    ///     when the desktop live-sync engine attaches; the projection itself carries the <c>chrome.livesync</c>
    ///     gate + session state through <see cref="ILiveSyncHudState.IsActive" />, so this is never cleared.
    /// </summary>
    public void SetLiveSyncHud(ILiveSyncHudState? hud) => LiveSyncHud = hud;

    /// <summary>
    ///     Wires the host's speed-lock predicate (mirrors <see cref="SetLiveSyncHud" />). A module's speed
    ///     keys must honour the SAME lock the NavStrip speed ComboBox binds its <c>IsEnabled</c> to: a
    ///     parallel path would let a keypress desync a Synced session.
    /// </summary>
    public void SetSpeedLock(Func<bool>? isLocked) => _speedLocked = isLocked;

    /// <summary>Sets the shell's feature projection once at composition (mirrors <see cref="SetLiveSyncHud" />).</summary>
    public void SetFeatures(IModuleFeatureGate? features) => Features = features;

    /// <summary>Wires the export host once at composition (mirrors <see cref="SetLiveSyncHud" />).</summary>
    /// <param name="host">The host, or null for a build with no export.</param>
    public void SetExportHost(Playback2DExportHost? host) => ExportHost = host;

    /// <summary>
    ///     Wires the app's DI container as <see cref="GetService{T}" />'s fallback for anything not
    ///     explicitly registered through <see cref="RegisterService{T}" />: a pack's capture or export
    ///     host, resolved by type. Null (tests, designer) leaves every unregistered lookup at null.
    /// </summary>
    public void SetServices(IServiceProvider? services) => _services = services;

    /// <summary>
    ///     Registers a lazy, typed lookup for a first-party host this context does not otherwise expose
    ///     (mirrors <see cref="SetExportHost" />'s reason, generalized): resolved fresh on every
    ///     <see cref="GetService{T}" /> call, so a host wired or swapped after this call is still the
    ///     current one. Last registration for a type wins.
    /// </summary>
    public void RegisterService<T>(Func<T?> resolve) where T : class => _serviceLookups[typeof(T)] = () => resolve();

    /// <inheritdoc />
    public T? GetService<T>() where T : class
    {
        if (_serviceLookups.TryGetValue(typeof(T), out Func<object?>? resolve))
        {
            return (T?)resolve();
        }

        // The container answers only for an extension's own types while that extension is on. A host type is
        // never reachable this way: the host's services are not a module's to resolve.
        if (_services is null || _faults?.Owner(typeof(T).Assembly) is not { } owner)
        {
            return null;
        }

        try
        {
            return _services.GetService<IFeatureGate>()?.IsEnabled(owner.FeatureId) == false ? null : _services.GetService<T>();
        }
        catch (ObjectDisposedException)
        {
            // A host torn down before this context (a headless test, shutdown) has nothing to resolve.
            return null;
        }
    }

    /// <summary>
    ///     Sets the shared game-clock calibration on demo load (mirrors <see cref="SetRoster" />). The
    ///     host computes <c>clockBase</c> once via <see cref="GameClock.ComputeClockBase" />: it owns
    ///     the frame history + the precomputed <c>round_freeze_end</c> frames a seeking module lacks.
    ///     Reset to 0 (naive fallback) on unload.
    /// </summary>
    public void SetGameClock(double clockBase) => _clockBase = clockBase;

    /// <summary>
    ///     Hands the context the demo's pre-decoded flat event list at load (mirrors <see cref="SetRoster" />).
    ///     Used to lazily build per-name <see cref="GetEventTimeline" /> views. Resets the per-name cache;
    ///     pass an empty list on unload.
    /// </summary>
    public void SetGameEvents(IReadOnlyList<GameEvent> events)
    {
        _allGameEvents = events;
        _timelineCache.Clear();
    }

    private void OnControllerAdvanced(PlaybackFrame _)
    {
        if (Advanced is null)
        {
            return;
        }

        RebuildPlayerJoin();
        AimEntityView();
        if (_faults is null)
        {
            Advanced.Invoke(_snapshot);
        }
        else
        {
            _advancedGuard.Invoke(_faults, Advanced, _snapshot, static (h, snapshot) => h(snapshot));
        }
    }

    // Re-aim the pooled entity view at the current authoritative entity set.
    private void AimEntityView() =>
        _entityView.Aim(_controller.AuthoritativeTracker?.CurrentEntities ?? EmptyEntitySet);

    // ── The host player-join ──────────────────────────────────────────────────
    // Once per push: walk live pawns, map pawn→slot via the reverse m_hController lookup, read the
    // volatile team, resolve the controller, reconstruct world position. Reuses pooled PlayerState
    // instances (no per-tick alloc on the framework hot path).
    private void RebuildPlayerJoin()
    {
        _playersView.Clear();

        if (_controller.AuthoritativeTracker is not { } tracker)
        {
            return;
        }

        // Map live, validly-controllered pawns by slot (ForEachLivePawn skips orphaned/dead-orphaned
        // pawns whose m_hController no longer resolves, see PawnLookup guard).
        _pawnBySlot.Clear();
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) => _pawnBySlot[slot] = pawn);

        // Controller-anchored emission: one PlayerState per stable roster slot, EVERY frame. Identity
        // comes from the persistent controller, not the fragile pawn. A dead player whose pawn has
        // orphaned still gets a row (HasLivePawn=false, pawn/position null) with their controller
        // resolved, so K/D/A / cash keep updating and the module can gray the card + hold the last-known
        // marker instead of the player vanishing. Falls back to a pawn-only sweep when no roster is set
        // (e.g. a context built directly in a test).
        int used = 0;
        if (_roster.Count > 0)
        {
            foreach (PlayerRosterEntry entry in _roster)
            {
                _pawnBySlot.TryGetValue(entry.Slot, out EntityState? pawn);
                EmitPlayer(ref used, tracker, entry.Slot, pawn);
            }
        }
        else
        {
            foreach (KeyValuePair<int, EntityState> kv in _pawnBySlot)
            {
                EmitPlayer(ref used, tracker, kv.Key, kv.Value);
            }
        }
    }

    // Resolves the persistent controller for a slot (entity index slot+1, verified to be a player
    // controller) and emits a pooled PlayerState. A null pawn is a dead/orphaned/pre-spawn player.
    private void EmitPlayer(ref int used, EntityTracker tracker, int slot, EntityState? pawn)
    {
        EntityState? ctrl = tracker.CurrentEntities[slot + 1];
        if (ctrl is null || !ctrl.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase))
        {
            ctrl = null;
        }

        int team = pawn is not null ? CoerceInt(pawn["m_iTeamNum"]) : CoerceInt(ctrl?["m_iTeamNum"]);
        // CellToWorld returns Vector3? since CS2DemoKit 0.10.0; IPlayerState.WorldPosition is the
        // add-on-facing contract and stays a tuple, so the shape converts here at the boundary.
        Vector3? v = pawn is not null ? PositionUtil.CellToWorld(pawn) : null;
        (float X, float Y, float Z)? world = v is { } p ? (p.X, p.Y, p.Z) : null;

        PooledPlayerState ps = RentPlayerState(used++);
        ps.Set(slot, team, pawn, ctrl, world);
        _playersView.Add(ps);
    }

    // Team arrives boxed (Int32 on the wire per project_cs2_wire_encoding); coerce defensively rather
    // than hard-cast (mirrors PawnLookup.TryUnboxHandle).
    private static int CoerceInt(object? value) => value switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort u => u,
        long l => (int)l,
        ulong u => (int)u,
        byte b => b,
        sbyte s => s,
        _ => 0
    };

    private PooledPlayerState RentPlayerState(int index)
    {
        while (_playerPool.Count <= index)
        {
            _playerPool.Add(new PooledPlayerState());
        }

        return _playerPool[index];
    }

    /// <summary>Detaches from the controller's push (shell teardown).</summary>
    public void Dispose() => _controller.Advanced -= OnControllerAdvanced;

    // ── Nested facade types ───────────────────────────────────────────────────

    /// <summary>The IPlaybackSnapshot the context hands to the Advanced callback (re-aimed each push).</summary>
    private sealed class PlaybackSnapshot(ModuleContext owner) : IPlaybackSnapshot
    {
        public int FrameIndex => owner.CurrentFrameIndex;
        public int Tick => owner.CurrentTick;
        public IReadOnlyEntityView Entities => owner._entityView;
        public IReadOnlyList<IPlayerState> Players => owner._playersView;
    }
}
