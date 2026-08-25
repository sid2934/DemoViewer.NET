#region

using DemoViewer.NET.Playback2D.Core;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.PlayerStats;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Services;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The 2D Playback tab's view-model. Subscribes to <c>IModuleContext.Advanced</c> on
///     activation and unsubscribes on deactivation, so it does ZERO per-tick work while inactive.
///     In the <c>Advanced</c> handler it copies out scalars for each live player (position / team / health
///     / weapons) INSIDE the callback — the <see cref="IPlayerState" />, the snapshot, and any resolved
///     <see cref="IReadOnlyEntity" /> are transient/pooled and invalid the instant the callback
///     returns. The viewport redraw is coalesced to the render frame, driven by
///     <see cref="FrameUpdated" />.
/// </summary>
public sealed partial class Playback2DTabViewModel : ObservableObject, IWorkspaceTabViewModel
{
    private const float SmokeRadiusWorld = 144; // the standard CS2 smoke radius (fixed)
    private const float FireCellRadiusWorld = 28; // per inferno fire cell; the cells cluster into the shape
    private const int MaxInfernoCells = 64; // m_firePositions / m_bFireIsBurning are [64] arrays
    private const int TrailFadeSeconds = 2; // a trail fades over this long (game time) after its nade lands
    private const int TrailJumpThreshold = 64; // |frameΔ| beyond this = a seek (clear trails); a normal push ≪ this
    private const int MaxTrailPoints = 256; // defensive per-trail cap (a flight is ~1-3s ≈ 60-180 samples)

    // Fallback round length (mp_roundtime 1:55) used only when m_iRoundTime is absent. Normally the
    // networked m_pGameRules.m_iRoundTime is read directly (#4).
    private const double FallbackRoundSeconds = 115;

    // Default C4 timer (mp_c4timer) used for the detonation-ring fraction when m_flTimerLength is absent.
    private const float DefaultC4Timer = 40;

    // The inventory array slots scanned per player: the dotted bracket-indexed paths are built ONCE
    // here, not per-frame, so the per-tick grenade loop allocates no path strings.
    private const int MyWeaponsSlots = 64;
    private const int MaxMinimapSections = 8; // engine array is fixed; scan a small bounded count.
    private const int KillFeedWindowSeconds = 8; // a kill stays visible this long (game time) after it happens
    private const int MaxKillFeedRows = 6;

    // The module's own "events of interest" for forward-nav (Phase E): a 2D combat viewer scrubs between
    // kills. The filter is matched against the host's demo-derived event set, so the buttons only show when
    // the demo actually carries player_death (asset/demo-independent — no hardcoded assumption it exists).
    private const string KillEventName = "player_death";

    // The grenade-projectile classes whose flight paths get a trail (all derive from CBaseCSGrenadeProjectile,
    // all carry CBodyComponent cell coords). Built once (static) so the per-frame scan allocates no strings.
    private static readonly string[] _grenadeProjectileClasses =
    {
        "CHEGrenadeProjectile", "CFlashbangProjectile", "CSmokeGrenadeProjectile", "CMolotovProjectile", "CDecoyProjectile"
    };

    private static readonly string[] _myWeaponsPaths = BuildMyWeaponsPaths();
    private static readonly Comparison<KillFeedEntry> _byTick = (a, b) => a.Tick.CompareTo(b.Tick);

    private static readonly string[] _killEventFilter =
    {
        KillEventName
    };

    // CS2 rounds OPEN at round_freeze_end (not round_start) — the same fact RoundTrack's band layout rests
    // on, so Q/E round nav and the timeline's bands can never disagree about where a round starts.
    private static readonly string[] _roundEventFilter =
    {
        RoundTrack.FreezeEndEvent
    };

    // The NavStrip speed ComboBox's ladder. ↑/↓ step within it rather than multiplying, so the keys and the
    // ComboBox always offer the same set of speeds.
    private static readonly double[] _speedPresets =
    {
        0.25, 0.5, 1, 2, 4, 8
    };

    // The WHOLE demo's kills, pre-built ONCE at load from IModuleContext.GetEventTimeline("player_death")
    // (decoupling display from the push cadence — no kill lost to a render-skipped frame). Rebuilt if the
    // roster arrives after activation (#2, names depend on it). _killWindow is reusable render scratch.
    private readonly List<KillFeedEntry> _allKills = new();

    // Grenade area effects (A4): active smoke clouds + burning inferno cells, rebuilt each push. Read by the
    // custom-drawn viewport. World positions are networked directly (no cell reconstruction).
    private readonly List<AreaEffect> _areaEffects = new(32);

    // Attributes panel: one row per roster slot, updated in place each push (no list rebuild).
    private readonly Dictionary<int, PlayerAttributes> _attrsBySlot = new();
    private readonly List<KillFeedEntry> _killWindow = new(16);

    // Last-known world position per slot, updated each frame a player has a live pawn. When a pawn
    // orphans on death (no live position) we hold a gray marker here until respawn (standard death
    // marker). Cleared with the ring cache on backward seek / re-activation.
    private readonly Dictionary<int, (float X, float Y, float Z)> _lastKnownPos = new(16);

    // Marker draw-state, rebuilt each push from copied-out scalars. The viewport reads this list; it is
    // never the pooled PlayerState (which is invalid after the callback returns).
    private readonly List<PlayerMarker> _markers = new(16);

    // Slot → display name from the stable roster. Rebuilt on activation.
    private readonly Dictionary<int, string> _nameBySlot = new();

    // Event-driven ring state machine + per-slot delta cache. Reset on backward seek.
    private readonly RingStateTracker _ringTracker = new();

    // Grenade flight trails (A4): per-projectile flight paths, LIVE-accumulated keyed by the projectile's
    // Serial (entity index gets reused on detonation — Serial survives it; the facade exposes no index/handle
    // anyway). Serial is a per-index reuse counter so two simultaneously-live projectiles could in theory
    // share one — but a probe over the whole reference demo (87k frames, up to 8 grenades aloft at once,
    // 25k multi-grenade frames) found ZERO collisions: CS2 hands out serials from a rising pool, so live
    // projectiles never share one. Tick-stamped, faded out after the projectile stops moving, then pruned;
    // CLEARED wholesale on a discontinuous frame jump (OnAdvanced) so a polyline
    // never streaks from a pre-seek point to a post-seek point. A FORWARD-PLAY artifact: a trail seeked-into
    // shows the arc from the seek point forward, which is incomplete, not wrong (unlike the kill feed, where a
    // mis-timed discrete event WAS a bug) — so render-skip loss here is purely cosmetic. _trailViews is the
    // reusable draw-state the viewport reads (the dict's live trails by reference, ≥2 points, not yet faded).
    private readonly Dictionary<int, GrenadeTrail> _trails = new(16);
    private readonly List<int> _trailsToPrune = new(8);
    private readonly List<GrenadeTrail> _trailViews = new(16);
    private IModuleContext? _context;

    // The shell's feature projection, captured at activation so deactivation unsubscribes the SAME
    // instance. Null (no host projection) fails OPEN — every gated surface stays on.
    private IModuleFeatureGate? _features;

    // Re-entrancy guard: the follow funnel assigns SelectedPlayer, whose generated setter loops back into
    // the funnel. Without it a card pick would raise FollowSlotChanged twice.
    private bool _inFollowFunnel;

    // The ITimelineData adapter over _context. Nulled on deactivation so the context isn't retained.
    private ModuleTimelineData? _timelineData;

    /// <summary>The followed roster slot, -1 = none. Set only through the follow funnel.</summary>
    [ObservableProperty]
    private int _followedSlot = -1;

    /// <summary>"following {name} · requested" — spectate has no readback, so never "confirmed".</summary>
    [ObservableProperty]
    private string _followStatus = "";

    /// <summary>Two-way bound to the player-card ListBox; setting it follows that player.</summary>
    [ObservableProperty]
    private PlayerAttributes? _selectedPlayer;

    /// <summary>One-shot footer hint set when a speed key is refused because Live Sync pins the speed.</summary>
    [ObservableProperty]
    private string _speedLockNote = "";

    /// <summary>True when the demo carries kill events, gating the kill forward-nav buttons (Phase E).</summary>
    [ObservableProperty]
    private bool _hasKillEvents;

    [ObservableProperty]
    private bool _isLiveSyncHudDegraded;

    [ObservableProperty]
    private bool _isLiveSyncHudError;

    // Class-driving dot-bucket projections (bound to Classes.x on the walled-off Ellipse.pb2dDot).
    [ObservableProperty]
    private bool _isLiveSyncHudGood;

    [ObservableProperty]
    private bool _isLiveSyncHudWorking;

    private int _lastFrameIndex = -1;

    // Identity of the last-published visible slice (count + boundary ticks) so an unchanged window doesn't
    // churn the ObservableCollection every push (the slice changes only when the playhead crosses a kill's
    // tick or its expiry — rare relative to ~60 pushes/sec).
    private int _lastKillCount = -1;
    private int _lastKillFirstTick;
    private int _lastKillLastTick;

    // ── Live Sync (CS2) in-context HUD indicator ─────────────────────────────────────────────────────────
    // A display-only chip on the HUD overlay band, driven by the shell-pushed ILiveSyncHudState projection
    // (engine-free; read via IModuleContext.LiveSyncHud). Captured at activation so deactivation unsubscribes
    // the SAME instance. Non-interactive (IsHitTestVisible=False in the view) — the shell status chip is the
    // control centre; see the design-system decision on the display-only call.
    private ILiveSyncHudState? _liveSyncHud;

    /// <summary>Hollow-ring flag — the inferred-pause treatment.</summary>
    [ObservableProperty]
    private bool _liveSyncHudHollow;

    /// <summary>The compact indicator text, e.g. "CS2 · Following" (the accessible carrier of state).</summary>
    [ObservableProperty]
    private string _liveSyncHudLabel = "";

    /// <summary>Dot-pulse flag (Following / working) — an opacity animation, not a colour change.</summary>
    [ObservableProperty]
    private bool _liveSyncHudPulsing;

    // The baked map-asset bundle for the current map (nav floors + radar + transform), loaded VRF-free from
    // the dev cs2-assets/baked cache when available. Null when no bundle exists → the
    // viewport falls back to its grid + Z-histogram floors. Reloaded when IModuleContext.MapName changes.

    // The map's REAL networked world-space X/Y bounds (the radar bounding box), read ONCE from the game-rules
    // entity: CCSGameRulesProxy.m_pGameRules.m_vMinimapMins / m_vMinimapMaxs (Vector3). Lets Map mode frame
    // the ACTUAL playable-map extent instead of the observed-positions approximation. Null until read.

    // Rounds completed so far (m_totalRoundsPlayed), cached ONCE per frame by UpdateGameInfo so the per-player
    // ADR computation in UpdateAttributes (O(players), no rules OfClass walk) reuses it. -1 = unknown.
    private int _roundsPlayed = -1;

    // The map's REAL networked Z-floor boundaries (#1 bonus), read ONCE from the game-rules entity:
    // CCSGameRulesProxy.m_pGameRules.m_MinimapVerticalSectionHeights[0..N]. Null until read; once populated
    // the viewport's FloorSplitter uses these EXACT thresholds instead of the histogram heuristic. Sentinel
    // (3.4e38) / unused-0 trailing slots are dropped here so the splitter receives only real boundaries.
    private double[]? _sectionHeights;
    private bool _sectionHeightsRead;

    // Count of roster entries last seeded into the display state (#2). -1 = never seeded. BuildFrame re-seeds
    // when the live roster count differs (empty→populated), so a roster set after activation still shows.
    private int _seededRosterCount = -1;

    [ObservableProperty]
    private bool _showAreaEffects = true;

    [ObservableProperty]
    private bool _showBombRing = true;

    [ObservableProperty]
    private bool _showKillFeed = true;

    /// <summary>Whether the 2D CS2 indicator is shown (gate on AND session non-Disconnected).</summary>
    [ObservableProperty]
    private bool _showLiveSyncHud;

    [ObservableProperty]
    private bool _showRadar = true; // baked radar background (A1); off → grid fallback

    // Overlay visibility toggles (A4 — "each sub-overlay toggleable"). Default ON. The three viewport-drawn
    // overlays (trails / area effects / bomb ring) are gated in the viewport's DrawSection and need a repaint
    // when toggled — hence the FrameUpdated nudge below (a toggle isn't a playback push). The kill feed is a
    // bound panel in the view, so its toggle drives IsVisible directly (the nudge is a harmless no-op for it).
    [ObservableProperty]
    private bool _showTrails = true;

    // 3D line-of-sight ("Vision") overlay. OFF by default — it lazily builds a collision BVH (~0.5s,
    // off-thread) the first time it's enabled on a map that has baked collision. Draws could-see sightlines.
    [ObservableProperty]
    private bool _showVision;

    [ObservableProperty]
    private string _status = "2D Playback — inactive";

    private int _tickRate = 64;

    // 3D line-of-sight engine for the current map (BVH over baked collision), lazily built off-thread the
    // first time the Vision overlay is enabled. Null until ready / when the map has no baked collision.
    private bool _visionEngineLoading;
    private string? _visionEngineMap;

    /// <summary>
    ///     The map's networked Z-floor section heights (#1 bonus), or null when absent. The 2D viewport reads
    ///     this to split floors EXACTLY on maps that publish them (Nuke / Vertigo), falling back to a histogram
    ///     heuristic otherwise. Read once per demo; cleared on backward seek only if it had never resolved.
    /// </summary>
    public IReadOnlyList<double>? SectionHeights => _sectionHeights;

    /// <summary>
    ///     The map's networked world-space X/Y bounds (radar bounding box), or null until read / absent. The
    ///     2D viewport's Map mode frames these EXACT playable-map bounds when present, falling back to the
    ///     all-demo observed-extent approximation otherwise. Static per map, so read once.
    /// </summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? MapBounds { get; private set; }

    /// <summary>
    ///     The bundle's nav-derived floor bands for the current map, or null when no baked bundle is available
    ///     (→ viewport uses its Z-histogram heuristic). The viewport adopts these as the authoritative floor
    ///     split. Validated to correctly classify real players (ZFloorValidationProbe).
    /// </summary>
    public IReadOnlyList<FloorSlice>? AuthoritativeFloors => MapAsset?.Floors;

    /// <summary>The loaded map-asset bundle (radar bitmaps + transform + layers) for the current map, or null.</summary>
    public LoadedMapAsset? MapAsset { get; private set; }

    /// <summary>
    ///     Test seam: the map name the viewport last (re)loaded assets for — set unconditionally by
    ///     <see cref="EnsureMapAsset" /> whether or not a baked bundle exists, so a headless test can assert
    ///     the map-reload path ran on a demo reload without the bundle files being present.
    /// </summary>
    internal string? LoadedMapNameForTest { get; private set; }

    /// <summary>The current map's line-of-sight engine, or null while loading / when unavailable. Read by the viewport.</summary>
    public VisibilityEngine? VisionEngine { get; private set; }

    /// <summary>Per-player current-state rows for the attributes panel.</summary>
    public ObservableCollection<PlayerAttributes> Attributes { get; } = new();

    /// <summary>
    ///     The kill-feed rows currently in the display window (ordered oldest→newest). A tick-window
    ///     filter over the pre-built timeline, refreshed each push — NOT an accumulator.
    /// </summary>
    public ObservableCollection<KillFeedEntry> KillFeed { get; } = new();

    /// <summary>Round-level game-info panel state.</summary>
    public GameInfo GameInfo { get; } = new();

    /// <summary>The current frame's marker draw-state. Read by the custom-drawn viewport.</summary>
    public IReadOnlyList<PlayerMarker> Markers => _markers;

    /// <summary>Active smoke clouds + burning inferno cells (A4), drawn under the markers by the viewport.</summary>
    public IReadOnlyList<AreaEffect> AreaEffects => _areaEffects;

    /// <summary>Grenade flight trails (A4), drawn as fading comet lines beneath the markers by the viewport.</summary>
    public IReadOnlyList<GrenadeTrail> GrenadeTrails => _trailViews;

    /// <summary>
    ///     The planted-C4 timer-ring draw-state (A4), or null when no live ticking bomb. Read by the
    ///     custom-drawn viewport.
    /// </summary>
    public BombMarker? Bomb { get; private set; }

    /// <summary>
    ///     The in-match players the Follow-Player camera mode can track (#2), ordered by team then slot.
    ///     Built on demand (when the mode menu opens) from the current attribute rows so the picker reflects
    ///     the live roster. Spectators / coaches / GOTV (non-T/CT) are excluded.
    /// </summary>
    public IReadOnlyList<FollowablePlayer> FollowablePlayers =>
        Attributes.Where(a => a.InMatch)
            .OrderBy(a => a.Team)
            .ThenBy(a => a.Slot)
            .Select(a => new FollowablePlayer(a.Slot, a.Name, a.Team))
            .ToList();

    /// <summary>Number of <c>Advanced</c> pushes received while active (read by tests).</summary>
    public int PushCount { get; private set; }

    /// <summary>
    ///     Builds the tab with its timeline and the three A1 tracks registered. Parameterless by contract:
    ///     gates arrive through <see cref="IModuleContext.Features" />, never through the constructor, so
    ///     the module descriptor's <c>ViewModelFactory</c> stays a bare <c>new()</c>.
    /// </summary>
    public Playback2DTabViewModel()
    {
        Timeline.RegisterTrack(new RoundTrack());
        Timeline.RegisterTrack(new KillTrack());
        Timeline.RegisterTrack(new BombTrack());

        // The timeline never moves the clock: it asks, and the shared clock decides (so LiveSync's
        // SyncStateObserver keeps seeing every seek).
        Timeline.SeekRequested += OnTimelineSeekRequested;
    }

    /// <summary>The scrub / rounds / markers chrome docked under the viewport.</summary>
    public Playback2DTimelineViewModel Timeline { get; } = new();

    /// <summary>
    ///     Whether the <c>playback2d.timeline</c> feature is on. Fails OPEN on a null projection (matching the
    ///     shell's own null-gate behaviour) and re-resolves live on the gate's Changed — a one-shot read would
    ///     leave the surface wrong until the tab was rebuilt.
    /// </summary>
    public bool IsTimelineEnabled => _features?.IsEnabled("playback2d.timeline") ?? true;

    /// <summary>Whether the <c>playback2d.follow</c> feature is on. Fail-open, live — see <see cref="IsTimelineEnabled" />.</summary>
    public bool IsFollowEnabled => _features?.IsEnabled("playback2d.follow") ?? true;

    /// <summary>Asks the view to re-fit the camera (the VM never touches the control).</summary>
    public event Action? FitRequested;

    public void OnActivated(IModuleContext context)
    {
        _context = context;

        _features = context.Features;
        if (_features is not null)
        {
            _features.Changed += OnFeaturesChanged;
        }

        RefreshGates();

        context.Advanced += OnAdvanced;
        // Stay in sync across demo reloads WHILE active: LoadDemo resets the playback clock without
        // an Advanced push, so without this an active tab would keep the PREVIOUS demo's map / markers /
        // trails after the user opens a new demo (via the Open-file button or the library browser).
        context.DemoReset += OnDemoReset;

        // Live Sync (CS2) in-context indicator: capture the shell's read-only projection (null on
        // Browser / no engine → the indicator stays absent) and track it while active. Captured so
        // deactivation unsubscribes the exact same instance (the seam is stable but we never re-read it late).
        _liveSyncHud = context.LiveSyncHud;
        if (_liveSyncHud is not null)
        {
            _liveSyncHud.Changed += OnLiveSyncHudChanged;
        }

        RefreshLiveSyncHud();

        // On-activation resync so a tab activated mid-playback is correct immediately: rebuild the
        // roster-derived display + map asset and the marker draw-state from the current host player-join
        // before the next push arrives.
        ResyncToCurrentDemo();
        Status = $"2D Playback — active · {context.CurrentPlayers.Count} players · 0 pushes";
    }

    public void OnDeactivated()
    {
        // Unsubscribe the CS2 indicator projection from the SAME instance captured at activation, before the
        // context is dropped (the seam is stable, but re-reading _context.LiveSyncHud late is not guaranteed
        // identical). After this the indicator does no work while inactive.
        if (_liveSyncHud is not null)
        {
            _liveSyncHud.Changed -= OnLiveSyncHudChanged;
            _liveSyncHud = null;
        }

        ShowLiveSyncHud = false;

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
            _features = null;
        }

        if (_context is not null)
        {
            _context.Advanced -= OnAdvanced;
            _context.DemoReset -= OnDemoReset;
            _context = null;
        }

        // The adapter holds no subscriptions, but it holds the context — drop it so an inactive tab
        // retains nothing.
        _timelineData = null;

        // After this returns the module does ZERO per-tick work.
        Status = $"2D Playback — inactive · {PushCount} pushes received";
    }

    private static string[] BuildMyWeaponsPaths()
    {
        string[] paths = new string[MyWeaponsSlots];
        for (int i = 0; i < MyWeaponsSlots; i++)
        {
            paths[i] = $"m_pWeaponServices.m_hMyWeapons[{i}]";
        }

        return paths;
    }

    /// <summary>
    ///     Raised once per coalesced <c>Advanced</c> push (after draw-state is updated) so the custom-drawn
    ///     viewport can <c>InvalidateVisual()</c>. One VM event per push → one invalidate is the whole
    ///     render-frame-coalescing story (the host already coalesces <c>Advanced</c> to ≤1/render-frame).
    /// </summary>
    public event Action? FrameUpdated;

    partial void OnShowRadarChanged(bool value) => FrameUpdated?.Invoke();
    partial void OnShowTrailsChanged(bool value) => FrameUpdated?.Invoke();
    partial void OnShowAreaEffectsChanged(bool value) => FrameUpdated?.Invoke();
    partial void OnShowBombRingChanged(bool value) => FrameUpdated?.Invoke();
    partial void OnShowKillFeedChanged(bool value) => FrameUpdated?.Invoke();

    partial void OnShowVisionChanged(bool value)
    {
        EnsureVisionEngine();
        FrameUpdated?.Invoke();
    }

    private void OnLiveSyncHudChanged(object? sender, EventArgs e) => RefreshLiveSyncHud();

    // Pulls the shell's read-only projection into the bound display state. Called at activation and on every
    // engine transition / gate flip (via the projection's Changed event).
    private void RefreshLiveSyncHud()
    {
        ILiveSyncHudState? hud = _liveSyncHud;
        if (hud is null)
        {
            ShowLiveSyncHud = false;
            return;
        }

        ShowLiveSyncHud = hud.IsActive;
        LiveSyncHudLabel = hud.Label;
        LiveSyncHudPulsing = hud.IsPulsing;
        LiveSyncHudHollow = hud.IsHollow;
        IsLiveSyncHudGood = hud.Dot == LiveSyncHudDot.Good;
        IsLiveSyncHudWorking = hud.Dot == LiveSyncHudDot.Working;
        IsLiveSyncHudDegraded = hud.Dot == LiveSyncHudDot.Degraded;
        IsLiveSyncHudError = hud.Dot == LiveSyncHudDot.Error;
    }

    /// <summary>Seek the shared clock to the next kill (player_death) — module-local forward-nav.</summary>
    [RelayCommand]
    private void NextKill() => _context?.RequestNextEvent(_killEventFilter);

    /// <summary>Seek the shared clock to the previous kill (player_death).</summary>
    [RelayCommand]
    private void PrevKill() => _context?.RequestPrevEvent(_killEventFilter);

    // Recompute whether the kill-nav buttons should show. Cheap (a membership test on the demo's event-name
    // set); called on activation and whenever the roster (re-)seeds, i.e. the demo-loaded signal (#2).
    private void RefreshEventNav() =>
        HasKillEvents = _context?.AvailableEventNames.Contains(KillEventName) ?? false;

    /// <summary>
    ///     Raised when the user picks a Follow-Player target in the viewport (csvg-integration
    ///     the DV→CS2 spectate seam). Payload = roster slot. The pick is also
    ///     relayed to the host via <see cref="IModuleContext.NotifySpectateTarget" />.
    /// </summary>
    public event Action<int>? FollowSlotChanged;

    /// <summary>
    ///     THE follow funnel. Every path that changes the followed player — a card click, the camera-mode
    ///     SplitButton submenu, the F / Shift+F keys, Esc — lands here, so there is exactly one place that
    ///     updates <see cref="FollowedSlot" />, the per-row followed flag, the selection, and the LiveSync
    ///     spectate chain. Passing -1 clears the follow and deliberately does NOT push a spectate change
    ///     (there is no "spectate nobody"; the CS2 session simply keeps its last target).
    /// </summary>
    internal void NotifyFollowSlotChanged(int slot)
    {
        _inFollowFunnel = true;
        try
        {
            FollowedSlot = slot;

            PlayerAttributes? followed = null;
            foreach (PlayerAttributes row in Attributes)
            {
                row.IsFollowed = row.Slot == slot;
                if (row.IsFollowed)
                {
                    followed = row;
                }
            }

            SelectedPlayer = followed;

            // "requested", never "confirmed": the spectate hook is fire-and-forget with no readback.
            FollowStatus = followed is not null ? $"following {followed.Name} · requested" : "";
            Timeline.FollowStatus = FollowStatus;
        }
        finally
        {
            _inFollowFunnel = false;
        }

        FollowSlotChanged?.Invoke(slot);

        if (slot >= 0)
        {
            _context?.NotifySpectateTarget(slot);
        }
    }

    /// <summary>Follows a roster slot (the camera-mode submenu and the card list both come through here).</summary>
    [RelayCommand]
    public void FollowPlayer(int slot)
    {
        if (IsFollowEnabled)
        {
            NotifyFollowSlotChanged(slot);
        }
    }

    /// <summary>Clears the follow target and asks the view to re-fit the camera.</summary>
    [RelayCommand]
    public void ClearFollow()
    {
        NotifyFollowSlotChanged(-1);
        FitRequested?.Invoke();
    }

    /// <summary>Steps the follow target through <see cref="FollowablePlayers" />; +1 next, -1 previous.</summary>
    public void CycleFollow(int direction)
    {
        if (!IsFollowEnabled)
        {
            return;
        }

        IReadOnlyList<FollowablePlayer> players = FollowablePlayers;
        if (players.Count == 0)
        {
            return;
        }

        int current = -1;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Slot == FollowedSlot)
            {
                current = i;
                break;
            }
        }

        int step = direction >= 0 ? 1 : -1;
        int next = current < 0
            ? step > 0 ? 0 : players.Count - 1
            : (current + step + players.Count) % players.Count;

        NotifyFollowSlotChanged(players[next].Slot);
    }

    /// <summary>
    ///     Dispatches a keymap action. Returns false when the action cannot be serviced right now (no
    ///     context, no demo, feature gated off, nothing to follow) — the view then leaves the key unhandled
    ///     so it can still reach whatever else wants it.
    ///     <para>
    ///         Every playback mutation below routes through <c>IModuleContext.Request*</c>, which is what
    ///         LiveSync's <c>SyncStateObserver</c> observes; a direct write to the controller would silently
    ///         bypass a Synced session.
    ///     </para>
    /// </summary>
    public bool ExecuteAction(Playback2DAction action)
    {
        if (_context is not { } ctx)
        {
            return false;
        }

        switch (action)
        {
            case Playback2DAction.TogglePlay:
                if (ctx.IsPlaying)
                {
                    ctx.RequestPause();
                }
                else
                {
                    ctx.RequestPlay();
                }

                return true;

            case Playback2DAction.StepBack:
                if (ctx.CurrentFrameIndex <= 0)
                {
                    return false;
                }

                ctx.RequestSeekToFrame(ctx.CurrentFrameIndex - 1);
                return true;

            case Playback2DAction.StepForward:
                int next = ctx.CurrentFrameIndex + 1;
                if (next <= 0 || (ctx.TotalFrames > 0 && next >= ctx.TotalFrames))
                {
                    return false;
                }

                ctx.RequestSeekToFrame(next);
                return true;

            case Playback2DAction.SpeedUp:
                return StepSpeed(ctx, 1);

            case Playback2DAction.SpeedDown:
                return StepSpeed(ctx, -1);

            case Playback2DAction.PrevRound:
                ctx.RequestPrevEvent(_roundEventFilter);
                return true;

            case Playback2DAction.NextRound:
                ctx.RequestNextEvent(_roundEventFilter);
                return true;

            case Playback2DAction.PrevKill:
                PrevKill();
                return true;

            case Playback2DAction.NextKill:
                NextKill();
                return true;

            case Playback2DAction.CycleFollowNext:
            case Playback2DAction.CycleFollowPrev:
                if (!IsFollowEnabled || FollowablePlayers.Count == 0)
                {
                    return false;
                }

                CycleFollow(action == Playback2DAction.CycleFollowNext ? 1 : -1);
                return true;

            case Playback2DAction.ClearFollow:
                ClearFollow();
                return true;

            case Playback2DAction.FitCamera:
                FitRequested?.Invoke();
                return true;

            default:
                return false;
        }
    }

    // Steps within the NavStrip's speed ladder from the nearest current value. A Live Sync session without
    // the plugin's timescale capability pins the speed: the key is consumed (so it never reaches the card
    // list underneath) but nothing is requested, and the footer says why.
    private bool StepSpeed(IModuleContext ctx, int direction)
    {
        if (ctx.IsSpeedLocked)
        {
            SpeedLockNote = "speed pinned by Live Sync";
            return true;
        }

        SpeedLockNote = "";

        int index = 0;
        double best = double.MaxValue;
        for (int i = 0; i < _speedPresets.Length; i++)
        {
            double distance = Math.Abs(_speedPresets[i] - ctx.Speed);
            if (distance < best)
            {
                best = distance;
                index = i;
            }
        }

        int target = Math.Clamp(index + direction, 0, _speedPresets.Length - 1);
        if (target != index)
        {
            ctx.RequestSpeed(_speedPresets[target]);
        }

        return true;
    }

    // The ListBox two-way binding lands here. Guarded against the funnel's own SelectedPlayer assignment.
    partial void OnSelectedPlayerChanged(PlayerAttributes? value)
    {
        if (_inFollowFunnel)
        {
            return;
        }

        if (value is null)
        {
            // A ListBox that is re-templating writes a transient null back through the two-way binding
            // before it re-reads the VM — and the view IS rebuilt on every tab activation. Dropping the
            // retained follow (and re-fitting the camera) on that would lose VM state to a view lifecycle
            // event, so a null only clears once the followed row has actually gone from the roster.
            if (FollowedSlot >= 0 && RowForSlot(FollowedSlot) is { } stillPresent)
            {
                _inFollowFunnel = true;
                try
                {
                    SelectedPlayer = stillPresent;
                }
                finally
                {
                    _inFollowFunnel = false;
                }

                return;
            }

            ClearFollow();
            return;
        }

        FollowPlayer(value.Slot);
    }

    private PlayerAttributes? RowForSlot(int slot)
    {
        foreach (PlayerAttributes row in Attributes)
        {
            if (row.Slot == slot)
            {
                return row;
            }
        }

        return null;
    }

    private void OnFeaturesChanged() => RefreshGates();

    private void RefreshGates()
    {
        OnPropertyChanged(nameof(IsTimelineEnabled));
        OnPropertyChanged(nameof(IsFollowEnabled));
        Timeline.IsVisible = IsTimelineEnabled && (_context?.HasDemo ?? false);

        if (!IsFollowEnabled && FollowedSlot >= 0)
        {
            NotifyFollowSlotChanged(-1);
        }
    }

    private void OnTimelineSeekRequested(int frameIndex) => _context?.RequestSeekToFrame(frameIndex);

    // The floating overlay's status readout moved into the timeline footer; mirror it so the one Status
    // string still drives it.
    partial void OnStatusChanged(string value) => Timeline.StatusText = value;

    // Same mirror for the speed-lock hint: a refused ↑/↓ is CONSUMED (so it never falls through to the card
    // list), which leaves the user with a dead key and no reason unless the footer says one.
    partial void OnSpeedLockNoteChanged(string value) => Timeline.SpeedLockNote = value;

    // A NEW demo was loaded while this tab is active — the roster / map / entities changed under us with no
    // Advanced push (LoadDemo resets the clock silently). Full resync so the map image, marker labels,
    // trails, ring state, and kill feed all reflect the new demo, exactly as a fresh activation would. This
    // is the state-restoration parity the Open-file button and the library browser must share.
    private void OnDemoReset() => ResyncToCurrentDemo();

    // Rebuilds ALL per-demo draw-state from the CURRENT context — shared by on-activation and by the
    // demo-reset signal. Re-seeds the roster display + map asset (SeedRosterDisplay → EnsureMapAsset), drops
    // every per-demo cache so nothing glides in from a prior demo/position, then builds the current
    // frame + kill window immediately.
    private void ResyncToCurrentDemo()
    {
        if (_context is null)
        {
            return;
        }

        // Cache the stable identity roster (slot → name) for marker labels + seed one attributes row per
        // slot; also (re)loads the baked map asset for the demo's map. Re-runnable: if the roster is set
        // AFTER activation (host order), BuildFrame re-seeds on the empty→populated transition too (#2).
        SeedRosterDisplay();

        // Reset the ring delta cache so a resync never flashes off a stale prior sample.
        _ringTracker.Reset();
        _lastKnownPos.Clear();
        _trails.Clear(); // fresh trails on (re)sync — never glide a line in from a prior demo/position
        _sectionHeights = null;
        _sectionHeightsRead = false;
        _lastFrameIndex = _context.CurrentFrameIndex;
        _tickRate = _context.TickRate > 0 ? _context.TickRate : 64;
        BuildFrame(_context.CurrentPlayers, _context.Entities, _context.CurrentFrameIndex, _context.CurrentTick);
        UpdateKillFeedWindow(_context.CurrentTick); // show the kills around the resync position immediately

        // Activation and DemoReset are exactly the two moments the demo's event set can change, so the
        // timeline is rebuilt here and nowhere else. A fresh adapter drops the previous demo's per-name cache.
        _timelineData = new ModuleTimelineData(_context);
        Timeline.Rebuild(_timelineData);
        Timeline.UpdatePlayhead(_context.CurrentFrameIndex, _context.CurrentTick);
        RefreshGates();

        FrameUpdated?.Invoke();
    }

    // Seeds the roster-DERIVED display state (slot→name labels + one attributes row per slot). Re-runnable:
    // if the roster arrives AFTER activation (host sets it post-load), BuildFrame re-invokes this on the
    // empty→populated transition so cards/initials appear without a tab re-activation (#2). Touches ONLY
    // display state — never the ring / last-known gameplay caches (slot-keyed; a display re-seed must not
    // wipe ring-flash / death-marker history).
    private void SeedRosterDisplay()
    {
        _nameBySlot.Clear();
        _attrsBySlot.Clear();
        Attributes.Clear();

        if (_context is null)
        {
            _seededRosterCount = 0;
            ReplaceMapAsset(null);
            LoadedMapNameForTest = null;
            return;
        }

        foreach (PlayerRosterEntry entry in _context.Players.OrderBy(p => p.Slot))
        {
            _nameBySlot[entry.Slot] = entry.Name;
            PlayerAttributes attrs = new(entry.Slot)
            {
                Name = entry.Name
            };
            _attrsBySlot[entry.Slot] = attrs;
            Attributes.Add(attrs);
        }

        _seededRosterCount = _context.Players.Count;

        // The roster is populated only once the demo has loaded, so this is also the right moment to (re)check
        // which semantic events the demo carries and show/hide the kill forward-nav accordingly (#2 / Phase E),
        // and to (re)build the kill timeline now that slot→name resolution is available.
        RefreshEventNav();
        BuildKillTimeline();

        // IModuleContext.MapName arrives with the roster (same host load step), so this is also the moment the
        // baked map-asset bundle (nav floors + radar) becomes selectable — load it here (and on the late
        // roster-arrival re-seed) so the viewport gets authoritative floors without a tab re-activation.
        EnsureMapAsset();
    }

    // (Re)loads the baked bundle when the map identity changes; cheap string compare when unchanged. Null map
    // name (or no bundle) clears it → the viewport falls back to its Z-histogram floors + grid (never throws).
    private void EnsureMapAsset()
    {
        string? name = _context?.MapName;
        if (string.Equals(name, LoadedMapNameForTest, StringComparison.Ordinal))
        {
            return;
        }

        ReplaceMapAsset(MapAssetLoader.TryLoad(name));
        LoadedMapNameForTest = name;

        // Map changed → the old collision engine no longer applies. Drop it and (re)load if Vision is on.
        VisionEngine = null;
        _visionEngineMap = null;
        EnsureVisionEngine();
    }

    /// <summary>
    ///     Swaps in a new map asset and DISPOSES the one it replaces. The radar bitmaps are Skia-backed, so
    ///     their pixel buffers are unmanaged (~4 MB each) — simply dropping the reference leaks them until a
    ///     finalizer happens to run, which is why a map swap used to grow native memory every time.
    ///     <para>
    ///         The old asset is disposed at Background priority rather than inline: the compositor may still
    ///         be holding it for the frame currently being rendered, and this hands the release to a point
    ///         where that frame has been submitted. Avalonia ref-counts the underlying bitmap impl, so this
    ///         is belt-and-braces rather than load-bearing — but a torn frame is a nasty thing to debug.
    ///     </para>
    /// </summary>
    private void ReplaceMapAsset(LoadedMapAsset? next)
    {
        LoadedMapAsset? previous = MapAsset;
        MapAsset = next;
        if (previous is null || ReferenceEquals(previous, next))
        {
            return;
        }

        Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
    }

    // Builds the current map's line-of-sight BVH off the UI thread (build is ~0.5s), the first time it's
    // needed. No-op unless the Vision overlay is on and the map has baked collision. Applies only if the map
    // is still current when the build finishes (the user may have switched maps meanwhile).
    private void EnsureVisionEngine()
    {
        if (!ShowVision || _visionEngineLoading)
        {
            return;
        }

        string? trisPath = MapAsset?.CollisionTrisPath;
        string? map = LoadedMapNameForTest;
        if (trisPath is null || map is null || string.Equals(_visionEngineMap, map, StringComparison.Ordinal))
        {
            return; // no collision for this map, or already loaded/loading for it
        }

        _visionEngineMap = map;
        _visionEngineLoading = true;
        Task.Run(() =>
        {
            VisibilityEngine? engine = null;
            try
            {
                engine = VisibilityEngine.Load(trisPath);
            }
            catch
            {
                // ignore — overlay silently stays off if collision can't be loaded/built
            }

            Dispatcher.UIThread.Post(() =>
            {
                _visionEngineLoading = false;
                if (string.Equals(LoadedMapNameForTest, map, StringComparison.Ordinal))
                {
                    VisionEngine = engine;
                    FrameUpdated?.Invoke();
                }
            });
        });
    }

    /// <summary>
    ///     Test seam: builds the vision engine synchronously on the calling thread (bypassing the off-thread
    ///     async load, which is fragile to pump under headless Avalonia). Production uses <see cref="EnsureVisionEngine" />.
    /// </summary>
    internal void LoadVisionEngineSyncForTest()
    {
        string? trisPath = MapAsset?.CollisionTrisPath;
        if (trisPath is null || LoadedMapNameForTest is null)
        {
            return;
        }

        _visionEngineMap = LoadedMapNameForTest;
        try
        {
            VisionEngine = VisibilityEngine.Load(trisPath);
        }
        catch
        {
            VisionEngine = null;
        }
    }

    private void OnAdvanced(IPlaybackSnapshot snapshot)
    {
        PushCount++;

        // Backward seek (or round-reset jump) → clear the delta cache so health/shots deltas don't
        // false-flash off a stale prior sample. The kill feed needs NO special handling — its
        // tick-window filter (below) naturally shows the right kills for the new position.
        if (snapshot.FrameIndex < _lastFrameIndex)
        {
            _ringTracker.Reset();
            _lastKnownPos.Clear();
        }

        // A4 grenade trails: clear on ANY discontinuous frame jump (forward OR backward beyond a normal
        // push) — the live-accumulate teleport guard, mirroring marker-snap. Without it a forward seek into a
        // grenade's flight draws a polyline streaking from the pre-seek point to the post-seek point. Normal
        // playback advances ≪ TrailJumpThreshold frames per push even at high speed, so this fires only on a
        // real seek. UpdateTrajectories re-seeds the (now empty) trail with the post-jump position next.
        if (_lastFrameIndex >= 0 && Math.Abs(snapshot.FrameIndex - _lastFrameIndex) > TrailJumpThreshold)
        {
            _trails.Clear();
        }

        _lastFrameIndex = snapshot.FrameIndex;
        BuildFrame(snapshot.Players, snapshot.Entities, snapshot.FrameIndex, snapshot.Tick);
        UpdateKillFeedWindow(snapshot.Tick);
        Status = $"2D Playback — active · frame {snapshot.FrameIndex} · " +
                 $"{_markers.Count} players · {PushCount} pushes";

        // The playhead follows the shared clock's push — never a private timer — so it tracks play, step,
        // NavStrip nav, palette jumps and LiveSync-driven seeks alike. A binary search and two sets.
        Timeline.UpdatePlayhead(snapshot.FrameIndex, snapshot.Tick);

        // Mark the viewport dirty; the View coalesces this to one InvalidateVisual on the render frame.
        FrameUpdated?.Invoke();
    }

    // Kill feed (A4): PRE-BUILD the whole demo's kills ONCE from the host's player_death timeline, resolving
    // slots → roster names and reading the typed modifiers the event factory enriched. Display is a tick
    // WINDOW filter over this (UpdateKillFeedWindow) — so nothing is lost to a render-skipped frame and a
    // seek shows the right kills. Rebuilt when the roster (re)seeds, since names depend on it (#2).
    private void BuildKillTimeline()
    {
        _allKills.Clear();
        if (_context is null)
        {
            return;
        }

        foreach (GameEventView ev in _context.GetEventTimeline("player_death"))
        {
            // Field names are the SDK payload record's properties (PlayerDeathEvent). The retired
            // generator's semantic-role names — KillerSlot / VictimSlot / AssisterSlot / IsHeadshot /
            // PenetratedObjects / ThroughSmoke — no longer exist as keys, and every read of one returned
            // the miss default: "world" killed "world", never a headshot. Check the embedded catalog (CatalogResource.Load()) before
            // adding a key here; it is generated by reflecting over these same records.
            int assister = ReadSlot(ev, "Assister");
            _allKills.Add(new KillFeedEntry(
                ev.Tick,
                NameForSlot(ReadSlot(ev, "Attacker")),
                assister >= 0 && _nameBySlot.TryGetValue(assister, out string? an) ? an : null,
                NameForSlot(ReadSlot(ev, "UserId")),
                ReadString(ev, "Weapon"),
                ReadBool(ev, "Headshot"),
                ReadInt(ev, "Penetrated") > 0,
                ReadBool(ev, "NoScope"),
                ReadBool(ev, "ThruSmoke"),
                ReadBool(ev, "AttackerBlind"),
                ReadBool(ev, "AttackerInAir"),
                ReadBool(ev, "AssistedFlash")));
        }

        // Force the next UpdateKillFeedWindow to publish (the underlying set just changed).
        _lastKillCount = -1;
    }

    // Refreshes the visible rows to the kills whose tick is in (now − window, now] — inclusive upper bound is
    // load-bearing: a kill AHEAD of the playhead must not appear when paused/seeking. Linear filter over a
    // few-hundred-element list (tens of µs), then sort the small visible window by tick (AllGameEvents order
    // is not guaranteed) and keep the most recent N. Skips the ObservableCollection update when the visible
    // slice is unchanged (it changes only when the playhead crosses a kill's tick or its expiry).
    private void UpdateKillFeedWindow(int nowTick)
    {
        int lowTick = nowTick - KillFeedWindowSeconds * Math.Max(1, _tickRate);

        _killWindow.Clear();
        foreach (KillFeedEntry k in _allKills)
        {
            if (k.Tick > lowTick && k.Tick <= nowTick)
            {
                _killWindow.Add(k);
            }
        }

        _killWindow.Sort(_byTick);
        int start = Math.Max(0, _killWindow.Count - MaxKillFeedRows);
        int count = _killWindow.Count - start;
        int firstTick = count > 0 ? _killWindow[start].Tick : 0;
        int lastTick = count > 0 ? _killWindow[^1].Tick : 0;

        if (count == _lastKillCount && firstTick == _lastKillFirstTick && lastTick == _lastKillLastTick)
        {
            return; // unchanged visible slice — don't churn the UI
        }

        KillFeed.Clear();
        for (int i = start; i < _killWindow.Count; i++)
        {
            KillFeed.Add(_killWindow[i]);
        }

        _lastKillCount = count;
        _lastKillFirstTick = firstTick;
        _lastKillLastTick = lastTick;
    }

    private string NameForSlot(int slot) =>
        slot >= 0 && _nameBySlot.TryGetValue(slot, out string? name) ? name : "world";

    private static int ReadSlot(GameEventView ev, string key) =>
        ev.Fields.TryGetValue(key, out object? v) && v is int i ? i : -1;

    private static int ReadInt(GameEventView ev, string key) =>
        ev.Fields.TryGetValue(key, out object? v) && v is int i ? i : 0;

    private static string ReadString(GameEventView ev, string key) =>
        ev.Fields.TryGetValue(key, out object? v) && v is string s ? s : "";

    private static bool ReadBool(GameEventView ev, string key) =>
        ev.Fields.TryGetValue(key, out object? v) && v is bool b && b;

    // Copies out the scalars the viewport + attributes panel need from the transient/pooled PlayerState
    // list (lifetime rule: read inside the callback, copy to value types, never retain the pooled
    // instance). Per-player cost is O(players) via the allocation-free indexer — never
    // EntityState.Fields. The one-hop weapon resolves obey the clobber rule: read each resolved entity's scalar
    // BEFORE the next ResolveHandle.
    private void BuildFrame(IReadOnlyList<IPlayerState> players, IReadOnlyEntityView entities, int frameIndex,
        int tick)
    {
        // #2: if the roster appeared after activation (host order), seed the display rows/labels now so the
        // cards + marker initials show without needing a tab re-activation. Count-change trigger → seed
        // once on the empty→populated transition, not every push (no per-frame ObservableCollection churn).
        if (_context is not null && _context.Players.Count != _seededRosterCount)
        {
            SeedRosterDisplay();
        }

        _markers.Clear();

        // Game-info: read ONCE per frame from CCSGameRulesProxy + CCSTeam (NOT per-player).
        UpdateGameInfo(entities, tick);

        // Grenade area effects (A4): active smokes + burning inferno cells (once per frame, not per-player).
        UpdateAreaEffects(entities);

        // Grenade flight trails (A4): live-accumulate each in-flight projectile's path (once per frame).
        UpdateTrajectories(entities, tick);

        // Mark all attribute rows not-live; live players below flip themselves back. A player who left
        // (disconnect / pre-spawn) thus resets to placeholders instead of showing stale state.
        foreach (PlayerAttributes row in Attributes)
        {
            row.HasLivePawn = false;
        }

        foreach (IPlayerState p in players)
        {
            IReadOnlyEntity? pawn = p.Pawn;
            bool hasPawn = p.HasLivePawn && pawn is not null;

            // Copy out the ring-state inputs — all null/seen-tolerant.
            int health = ReadInt(pawn, "m_iHealth", hasPawn ? 100 : 0);
            int shotsFired = ReadInt(pawn, "m_iShotsFired", 0);
            float flash = ReadFloat(pawn, "m_flFlashDuration", 0);
            bool alive = hasPawn && IsAlive(pawn);

            // Attributes for EVERY roster player — a dead/orphaned player keeps its controller-sourced
            // stats (K/D/A, cash, score) and grays out, instead of vanishing from the panel.
            UpdateAttributes(p, entities, health, alive);

            if (p.WorldPosition is { } pos)
            {
                // Live pawn: remember the spot, draw the marker (the ring goes gray when dead).
                _lastKnownPos[p.Slot] = (pos.X, pos.Y, pos.Z);

                float yaw = 0, pitch = 0;
                if (pawn is not null && pawn.TryGet("m_angEyeAngles", out Vector3 eye))
                {
                    pitch = eye.X; // pitch = .X, yaw = .Y, roll = .Z
                    yaw = eye.Y;
                }

                float duck = ReadFloat(pawn, "m_pMovementServices.m_flDuckAmount", 0);

                (RingState ring, double ringAlpha) =
                    _ringTracker.Evaluate(p.Slot, frameIndex, alive, flash, health, shotsFired);

                _markers.Add(new PlayerMarker(
                    p.Slot,
                    p.Team,
                    pos.X,
                    pos.Y,
                    pos.Z,
                    yaw,
                    ring,
                    ringAlpha,
                    LabelFor(p.Slot),
                    alive,
                    pitch,
                    duck));
            }
            else if (!alive && _lastKnownPos.TryGetValue(p.Slot, out (float X, float Y, float Z) last))
            {
                // Dead pawn has orphaned (no live position this tick) — hold a gray marker at the death
                // spot with the correct roster label until the player respawns (standard death marker).
                _markers.Add(new PlayerMarker(
                    p.Slot,
                    p.Team,
                    last.X,
                    last.Y,
                    last.Z,
                    0,
                    RingState.Dead,
                    1.0,
                    LabelFor(p.Slot),
                    false));
            }
        }
    }

    // Round-level game info, read ONCE per frame (NOT per-player). OfClass allocates a fresh facade
    // per element — acceptable for this once-per-frame read, never in the per-player hot loop.
    // Paths verified against a real demo (GameInfoFieldProbeTests): CCSGameRulesProxy.m_pGameRules.* and
    // CCSTeam.m_iScore filtered by m_iTeamNum (2=T, 3=CT).
    private void UpdateGameInfo(IReadOnlyEntityView entities, int tick)
    {
        IReadOnlyEntity? rules = null;
        foreach (IReadOnlyEntity e in entities.OfClass("CCSGameRulesProxy"))
        {
            rules = e; // there is exactly one CCSGameRulesProxy per match (FreezePeriodProvider)
            break;
        }

        if (rules is not null)
        {
            // #1 bonus: read the map's real Z-floor boundaries ONCE (they're static per map). Additive — does
            // not touch the round/bomb timer logic below.
            ReadSectionHeightsOnce(rules);

            // #2: read the map's REAL world-space X/Y bounds ONCE so Map mode frames the actual playable map
            // (radar bounding box) instead of the observed-positions approximation.
            ReadMapBoundsOnce(rules);

            bool warmup = ReadBool(rules, "m_pGameRules.m_bWarmupPeriod");
            bool freeze = ReadBool(rules, "m_pGameRules.m_bFreezePeriod");
            bool planted = ReadBool(rules, "m_pGameRules.m_bBombPlanted");
            bool defused = ReadBool(rules, "m_pGameRules.m_bBombDefused");
            bool dropped = ReadBool(rules, "m_pGameRules.m_bBombDropped");

            GameInfo.Phase = warmup ? "Warmup" : freeze ? "Freeze" : "Live";

            GameInfo.BombState = defused ? "Defused"
                : planted ? "Planted"
                : dropped ? "Dropped"
                : "—";

            int rounds = ReadIntOr(rules, "m_pGameRules.m_totalRoundsPlayed", -1);
            _roundsPlayed = rounds; // cached for per-player ADR (UpdateAttributes); -1 = unknown
            GameInfo.RoundNumber = rounds >= 0
                ? (rounds + 1).ToString(CultureInfo.InvariantCulture)
                : "—";

            // Bomb/round main countdown. Priority (#5 over #4): a LIVE ticking CPlantedC4 replaces the
            // round clock with the C4 detonation countdown; otherwise the freeze state, otherwise the
            // round clock. The detonation timer is driven off the ENTITY (m_bBombTicking / m_flC4Blow),
            // not m_pGameRules.m_bBombPlanted — the entity carries the absolute blow time.
            if (UpdateBombTimers(entities, tick))
            {
                // Detonation countdown owns the main timer this frame; defuse second-timer set inside.
            }
            else if (freeze)
            {
                GameInfo.RoundSeconds = double.NaN;
                GameInfo.RoundTime = "freeze";
            }
            else if (rules.TryGet("m_pGameRules.m_fRoundStartTime", out float roundStart))
            {
                // Round time remaining = m_fRoundStartTime + m_iRoundTime − correctedCurtime(tick) (#4).
                // The round length is the NETWORKED m_iRoundTime (115 on the verified demo), not an
                // assumed convar; correctedCurtime is the host's offset-corrected game clock
                // (IModuleContext.CurtimeSeconds) aligning the demo curtime to the round-start time base.
                double roundLen = ReadIntOr(rules, "m_pGameRules.m_iRoundTime", 0);
                if (roundLen <= 0)
                {
                    roundLen = FallbackRoundSeconds;
                }

                double remaining = roundStart + roundLen - CorrectedCurtime(tick);
                GameInfo.RoundSeconds = remaining;
                GameInfo.RoundTime = remaining > 0 ? FormatClock(remaining) : "0:00";
            }
        }

        // Team score: CCSTeam.m_iScore filtered by m_iTeamNum (2=T, 3=CT).
        foreach (IReadOnlyEntity team in entities.OfClass("CCSTeam"))
        {
            int num = ReadIntOr(team, "m_iTeamNum", -1);
            int score = ReadIntOr(team, "m_iScore", 0);
            if (num == 2)
            {
                GameInfo.TScore = score;
            }
            else if (num == 3)
            {
                GameInfo.CtScore = score;
            }
        }
    }

    // #1 bonus: reads CCSGameRulesProxy.m_pGameRules.m_MinimapVerticalSectionHeights[0..N] ONCE — the map's
    // real Z-floor boundaries (e.g. Nuke [1.81, 51.54, 287.0, 376.0]). The engine array is fixed-size; we
    // scan a bounded count and stop at the first sentinel (3.4e38 ≈ float.MaxValue) or non-ascending value
    // (an unused trailing 0 slot). A map without floor sections publishes ≤1 usable value → null (the
    // viewport then uses its histogram heuristic). Static per map, so reading once is correct.
    // #2: reads CCSGameRulesProxy.m_pGameRules.m_vMinimapMins / m_vMinimapMaxs (Vector3 world-space radar
    // bounding box) ONCE — the REAL playable-map X/Y extent (verified on the pro demo: X[-2573..2043]
    // Y[-1497..3358], with players comfortably inside). Lets Map mode frame the actual map. Static per map.
    private void ReadMapBoundsOnce(IReadOnlyEntity rules)
    {
        if (MapBounds is not null)
        {
            return;
        }

        if (rules.TryGet("m_pGameRules.m_vMinimapMins", out Vector3 mins) &&
            rules.TryGet("m_pGameRules.m_vMinimapMaxs", out Vector3 maxs) &&
            maxs.X > mins.X && maxs.Y > mins.Y)
        {
            MapBounds = (mins.X, mins.Y, maxs.X, maxs.Y);
        }
    }

    private void ReadSectionHeightsOnce(IReadOnlyEntity rules)
    {
        if (_sectionHeightsRead)
        {
            return;
        }

        List<double> kept = new(MaxMinimapSections);
        for (int i = 0; i < MaxMinimapSections; i++)
        {
            if (!rules.TryGet($"m_pGameRules.m_MinimapVerticalSectionHeights[{i}]", out float h))
            {
                break; // field unseen at this index — end of the published array for this map.
            }

            if (h >= 3.0e38f) // engine "unused section" sentinel
            {
                break;
            }

            if (kept.Count > 0 && h <= kept[^1])
            {
                break; // not strictly ascending → trailing unused slot.
            }

            kept.Add(h);
        }

        // Two or more boundaries describe a real multi-floor map; fewer ⇒ leave null and let the histogram run.
        _sectionHeights = kept.Count >= 2 ? kept.ToArray() : null;

        // Only latch "read" once the field actually resolved (≥1 value seen); otherwise the array hasn't been
        // networked yet this frame and we retry next frame (the entity may not yet be fully decoded on seek).
        if (kept.Count >= 1)
        {
            _sectionHeightsRead = true;
        }
    }

    // #5: bomb plant/defuse + C4 detonation timers. Finds a live ticking CPlantedC4 (entity-driven —
    // m_bBombTicking, NOT m_pGameRules.m_bBombPlanted, which lags the entity) and, when present, replaces
    // the main countdown with the detonation remaining (m_flC4Blow − correctedCurtime). During a
    // defuse-in-progress (m_bBeingDefused) the SECOND timer shows the defuse-completion remaining
    // (m_flDefuseCountDown − correctedCurtime), so the panel shows the defuse-vs-detonation race. The
    // defuse length (m_flDefuseLength) already encodes kit (5s) vs no-kit (10s); the defuser's
    // m_bHasDefuser only labels it. Returns true iff a ticking C4 owns the main timer this frame; clears
    // all bomb/defuse state and returns false otherwise (so the round clock / freeze branch runs).
    // A4 grenade area effects: active smoke clouds + burning inferno cells. Once per frame (OfClass allocates
    // a facade per element — acceptable for a handful of live grenades, never the per-player hot loop). World
    // positions are networked directly: smoke centre = m_vSmokeDetonationPos (once m_nSmokeEffectTickBegin>0,
    // i.e. detonated/billowing, not the still-flying projectile); fire cells = m_firePositions[i] for the
    // m_fireCount active cells where m_bFireIsBurning[i].
    private void UpdateAreaEffects(IReadOnlyEntityView entities)
    {
        _areaEffects.Clear();

        foreach (IReadOnlyEntity smoke in entities.OfClass("CSmokeGrenadeProjectile"))
        {
            if (ReadIntOr(smoke, "m_nSmokeEffectTickBegin", 0) <= 0)
            {
                continue; // still a flying projectile, not yet a billowing cloud
            }

            if (smoke.TryGet("m_vSmokeDetonationPos", out Vector3 pos) && (pos.X != 0 || pos.Y != 0))
            {
                _areaEffects.Add(new AreaEffect(AreaEffectKind.Smoke, pos.X, pos.Y, pos.Z, SmokeRadiusWorld));
            }
        }

        foreach (IReadOnlyEntity inferno in entities.OfClass("CInferno"))
        {
            int count = Math.Min(ReadIntOr(inferno, "m_fireCount", 0), MaxInfernoCells);
            for (int i = 0; i < count; i++)
            {
                if (!ReadBool(inferno, $"m_bFireIsBurning[{i}]"))
                {
                    continue;
                }

                if (inferno.TryGet($"m_firePositions[{i}]", out Vector3 cell) && (cell.X != 0 || cell.Y != 0))
                {
                    _areaEffects.Add(new AreaEffect(
                        AreaEffectKind.Fire, cell.X, cell.Y, cell.Z, FireCellRadiusWorld));
                }
            }
        }
    }

    // Grenade flight trails: LIVE-accumulate each in-flight projectile's reconstructed world position into
    // its Serial-keyed trail, then fade/prune trails whose projectile has detonated. Once per frame
    // (OfClass allocates a facade per element — a handful of grenades in flight, acceptable once-per-frame,
    // never the per-player hot loop). Projectile positions are NOT host-joined (the host only joins player
    // positions), so they're reconstructed from CBodyComponent cells via the oracle-pinned ReconstructWorld —
    // the same path the planted-C4 ring uses. The discontinuous-jump clear lives in OnAdvanced (it owns the
    // frame delta); this method only grows + ages trails monotonically with the forward playhead.
    private void UpdateTrajectories(IReadOnlyEntityView entities, int tick)
    {
        // 1) Sample every in-flight grenade projectile into its trail. Append only when advancing past the
        //    last sample AND the projectile actually moved, so a paused/coalesced re-push (same tick) or a
        //    small backward micro-step doesn't pile points or kink the line backward.
        foreach (string cls in _grenadeProjectileClasses)
        {
            foreach (IReadOnlyEntity proj in entities.OfClass(cls))
            {
                if (ReconstructWorld(proj) is not { } pos)
                {
                    continue; // cells not yet networked — skip until the projectile is positioned
                }

                if (!_trails.TryGetValue(proj.Serial, out GrenadeTrail? trail))
                {
                    trail = new GrenadeTrail
                    {
                        Kind = KindForClass(cls)
                    };
                    _trails[proj.Serial] = trail;
                }

                // Append only when the projectile actually MOVED (guards a stationary/landed projectile or a
                // duplicate same-tick push from piling points) AND we're advancing past its last move (so a
                // backward micro-step doesn't kink the line backward). LastTick tracks the last MOVE, not the
                // last sighting — so a landed-but-still-alive smoke/decoy fades instead of holding forever.
                bool moved = trail.Points.Count == 0 || !SamePoint(trail.Points[^1], pos);
                bool advancing = trail.Points.Count == 0 || tick > trail.LastTick;
                if (moved && advancing)
                {
                    if (trail.Points.Count >= MaxTrailPoints)
                    {
                        trail.Points.RemoveAt(0);
                    }

                    trail.Points.Add(new GrenadeTrailPoint(pos.X, pos.Y, pos.Z));
                    trail.LastTick = tick;
                }
            }
        }

        // 2) Fade by time-since-last-MOVE: a trail still moving (or whose playhead stepped back to/before its
        //    last move) holds full opacity; one that has stopped (detonated, despawned, or just landed and
        //    sitting there) fades over the window and is pruned. Rebuild the draw-state from the survivors
        //    (≥2 points to be a visible line).
        int fadeTicks = TrailFadeSeconds * Math.Max(1, _tickRate);
        _trailViews.Clear();
        _trailsToPrune.Clear();

        foreach (KeyValuePair<int, GrenadeTrail> kv in _trails)
        {
            GrenadeTrail t = kv.Value;
            int age = tick - t.LastTick;

            if (age <= 0)
            {
                t.Alpha = 1.0; // moving this frame, or the playhead stepped back to/before its last move → hold
            }
            else
            {
                t.Alpha = fadeTicks > 0 ? Math.Clamp(1.0 - age / (double)fadeTicks, 0, 1) : 0;
                if (t.Alpha <= 0.0)
                {
                    _trailsToPrune.Add(kv.Key); // faded out — pruned (a persistent projectile re-seeds cleanly)
                    continue;
                }
            }

            if (t.Points.Count >= 2)
            {
                _trailViews.Add(t);
            }
        }

        foreach (int key in _trailsToPrune)
        {
            _trails.Remove(key);
        }
    }

    private static GrenadeKind KindForClass(string cls) => cls switch
    {
        "CHEGrenadeProjectile" => GrenadeKind.He,
        "CFlashbangProjectile" => GrenadeKind.Flash,
        "CSmokeGrenadeProjectile" => GrenadeKind.Smoke,
        "CMolotovProjectile" => GrenadeKind.Molotov,
        "CDecoyProjectile" => GrenadeKind.Decoy,
        _ => GrenadeKind.He
    };

    // Two samples are the "same" point (skip the append) when within half a world unit on each axis — guards
    // a stationary/landed projectile or a duplicate same-tick push from piling coincident points.
    private static bool SamePoint(GrenadeTrailPoint a, (float X, float Y, float Z) b) =>
        Math.Abs(a.X - b.X) < 0.5f && Math.Abs(a.Y - b.Y) < 0.5f && Math.Abs(a.Z - b.Z) < 0.5f;

    private bool UpdateBombTimers(IReadOnlyEntityView entities, int tick)
    {
        IReadOnlyEntity? c4 = null;
        foreach (IReadOnlyEntity e in entities.OfClass("CPlantedC4"))
        {
            if (ReadBool(e, "m_bBombTicking") && !ReadBool(e, "m_bBombDefused"))
            {
                c4 = e;
                break;
            }
        }

        if (c4 is null || !c4.TryGet("m_flC4Blow", out float blow) || blow <= 0)
        {
            ClearBombTimers();
            return false;
        }

        double now = CorrectedCurtime(tick);

        // Main countdown → C4 detonation remaining.
        double detonation = blow - now;
        GameInfo.BombTicking = true;
        GameInfo.RoundSeconds = detonation;
        GameInfo.RoundTime = detonation > 0 ? FormatClock(detonation) : "0:00";

        // Detonation ring fraction (remaining / total bomb timer; m_flTimerLength is mp_c4timer, ~40s).
        float timerLength = ReadFloat(c4, "m_flTimerLength", DefaultC4Timer);
        double detonationFraction = Math.Clamp(detonation / Math.Max(1.0, timerLength), 0, 1);
        bool beingDefused = false;
        double defuseFraction = 0;

        // Second timer → defuse-in-progress (the defuse-vs-detonation race).
        if (ReadBool(c4, "m_bBeingDefused")
            && c4.TryGet("m_flDefuseCountDown", out float defuseCd) && defuseCd > 0)
        {
            double defuseRemain = defuseCd - now;
            float defuseLen = ReadFloat(c4, "m_flDefuseLength", 0);
            GameInfo.DefuseInProgress = true;
            GameInfo.DefuseSeconds = defuseRemain;
            GameInfo.DefuseTime = defuseRemain > 0 ? FormatClock(defuseRemain) : "0:00";
            // m_flDefuseLength is 5 with a kit, 10 without — surface that as a label.
            GameInfo.DefuseKitNote = defuseLen > 0 && defuseLen <= 6 ? "with kit" : "no kit";

            beingDefused = true;
            defuseFraction = defuseLen > 0 ? Math.Clamp(defuseRemain / defuseLen, 0, 1) : 0;
        }
        else
        {
            ClearDefuseTimer();
        }

        // Bomb ring draw-state — only when its world position reconstructs (CPlantedC4 cell coords, same
        // encoding as pawns). Null position → no ring (game-info timer still shows).
        Bomb = ReconstructWorld(c4) is { } pos
            ? new BombMarker(pos.X, pos.Y, pos.Z, detonationFraction, beingDefused, defuseFraction)
            : null;

        return true;
    }

    // Reconstructs a non-player entity's world position from its CBodyComponent cell coords, reusing the
    // oracle-pinned PositionUtil.Axis formula (the load-bearing constant stays in one place). The module
    // reads the fields off the read-only facade (the host only joins PLAYER positions; the C4 has none).
    private static (float X, float Y, float Z)? ReconstructWorld(IReadOnlyEntity e)
    {
        if (TryCell(e["CBodyComponent.m_cellX"], out int cx) &&
            TryCell(e["CBodyComponent.m_cellY"], out int cy) &&
            TryCell(e["CBodyComponent.m_cellZ"], out int cz) &&
            TryOffset(e["CBodyComponent.m_vecX"], out float ox) &&
            TryOffset(e["CBodyComponent.m_vecY"], out float oy) &&
            TryOffset(e["CBodyComponent.m_vecZ"], out float oz))
        {
            return (PositionUtil.Axis(cx, ox), PositionUtil.Axis(cy, oy), PositionUtil.Axis(cz, oz));
        }

        return null;
    }

    private static bool TryCell(object? v, out int cell)
    {
        switch (v)
        {
            case ushort u:
                cell = u;
                return true;
            case short s:
                cell = s;
                return true;
            case int i:
                cell = i;
                return true;
            case uint u:
                cell = (int)u;
                return true;
            case long l:
                cell = (int)l;
                return true;
            case byte b:
                cell = b;
                return true;
            default:
                cell = 0;
                return false;
        }
    }

    private static bool TryOffset(object? v, out float offset)
    {
        switch (v)
        {
            case float f:
                offset = f;
                return true;
            case double d:
                offset = (float)d;
                return true;
            case int i:
                offset = i;
                return true;
            default:
                offset = 0;
                return false;
        }
    }

    private void ClearBombTimers()
    {
        GameInfo.BombTicking = false;
        Bomb = null;
        ClearDefuseTimer();
    }

    private void ClearDefuseTimer()
    {
        GameInfo.DefuseInProgress = false;
        GameInfo.DefuseSeconds = double.NaN;
        GameInfo.DefuseTime = "—";
        GameInfo.DefuseKitNote = "—";
    }

    // The host's offset-corrected game clock (#4): aligns the demo curtime to the entity time base that
    // m_fRoundStartTime / m_flC4Blow stamp against. The host computes the offset once at load (it owns the
    // frame history a seeking module lacks). Falls back to the naive reading if the context is absent.
    private double CorrectedCurtime(int tick) =>
        _context?.CurtimeSeconds(tick) ?? tick / (double)(_tickRate > 0 ? _tickRate : 64);

    private static string FormatClock(double seconds)
    {
        int s = (int)Math.Round(seconds);
        return $"{s / 60}:{s % 60:D2}";
    }

    private static int ReadIntOr(IReadOnlyEntity entity, string path, int fallback)
    {
        if (entity.TryGet(path, out int i))
        {
            return i;
        }

        if (entity.TryGet(path, out uint u))
        {
            return (int)u;
        }

        return fallback;
    }

    // Updates one player's attributes row in place. The weapon resolves walk the clobber
    // hazard — ResolveHandle returns a SHARED pooled facade, so each resolved entity's class is read
    // IMMEDIATELY, before the next resolve.
    private void UpdateAttributes(IPlayerState p, IReadOnlyEntityView entities, int health, bool alive)
    {
        if (!_attrsBySlot.TryGetValue(p.Slot, out PlayerAttributes? a))
        {
            return;
        }

        IReadOnlyEntity? pawn = p.Pawn;
        IReadOnlyEntity? ctrl = p.Controller;

        a.HasLivePawn = p.HasLivePawn;
        a.IsAlive = alive;
        a.Team = p.Team;
        // Only T(2)/CT(3) participants are shown; coaches / GOTV / spectator roster entries (other teams)
        // stay hidden rather than appearing as empty grayed cards.
        a.InMatch = p.Team is 2 or 3;

        a.Health = alive ? health.ToString(CultureInfo.InvariantCulture) : "0";
        a.Armor = FormatInt(pawn, "m_ArmorValue");
        a.HasHelmet = ReadBool(pawn, "m_pItemServices.m_bHasHelmet");
        a.HasDefuser = ReadBool(pawn, "m_pItemServices.m_bHasDefuser");

        a.Cash = FormatInt(ctrl, "m_pInGameMoneyServices.m_iAccount");
        a.RoundKills = FormatInt(ctrl, "m_pActionTrackingServices.m_iNumRoundKills");

        // Match-total K/D/A — cumulative scoreboard stats, networked directly under the
        // action-tracking service (verified flattened paths; m_matchStats aggregates flatten up). Totals
        // are the headline stat the panel shows; round-kills stays available on the row VM.
        a.Kda = ctrl is null
            ? "—"
            : $"{ReadIntOr(ctrl, "m_pActionTrackingServices.m_iKills", 0)}/" +
              $"{ReadIntOr(ctrl, "m_pActionTrackingServices.m_iDeaths", 0)}/" +
              $"{ReadIntOr(ctrl, "m_pActionTrackingServices.m_iAssists", 0)}";

        // Match-total damage + ADR (average damage per round). m_iDamage is networked alongside K/D/A under
        // the action-tracking service. ADR = total damage / rounds played, denominator = m_totalRoundsPlayed
        // (cached once per frame by UpdateGameInfo). Floored at 1 round so the opening round (0
        // completed) shows damage/1 instead of dividing by zero; reduces to total-damage/total-rounds at game
        // end. Mid-round it reads slightly high (the live round's damage over a not-yet-incremented
        // denominator) then normalizes at round end — the standard live-scoreboard ADR behaviour.
        if (ctrl is null)
        {
            a.Damage = "—";
            a.Adr = "—";
        }
        else
        {
            int damage = ReadIntOr(ctrl, "m_pActionTrackingServices.m_iDamage", 0);
            int denom = Math.Max(1, _roundsPlayed);
            a.Damage = damage.ToString(CultureInfo.InvariantCulture);
            a.Adr = ((int)Math.Round((double)damage / denom)).ToString(CultureInfo.InvariantCulture);
        }

        a.Score = FormatInt(ctrl, "m_iScore");
        a.EquipmentValue = FormatInt(pawn, "m_unCurrentEquipmentValue");

        // Active weapon (one-hop): resolve the handle, read the class name IMMEDIATELY (clobber rule).
        a.ActiveWeapon = "—";
        if (pawn is not null && pawn.TryGet("m_pWeaponServices.m_hActiveWeapon", out ulong activeHandle)
                             && activeHandle != 0)
        {
            IReadOnlyEntity? weapon = entities.ResolveHandle(activeHandle);
            if (weapon is not null)
            {
                a.ActiveWeapon = PlayerSnapshotBuilder.WeaponShortName(weapon.ClassName);
            }
        }

        a.Grenades = pawn is not null ? CountGrenades(pawn, entities) : "—";
    }

    // Iterates m_pWeaponServices.m_hMyWeapons[N] (bracket-indexed array; the verified convention), resolving
    // each handle and classifying it by class name BEFORE the next resolve (clobber rule). Returns a
    // compact grenade summary (e.g. "HE Smoke Flash×2").
    private static string CountGrenades(IReadOnlyEntity pawn, IReadOnlyEntityView entities)
    {
        int he = 0, smoke = 0, flash = 0, molotov = 0, decoy = 0;

        for (int i = 0; i < MyWeaponsSlots; i++)
        {
            if (!pawn.TryGet(_myWeaponsPaths[i], out ulong handle) || handle == 0)
            {
                continue;
            }

            IReadOnlyEntity? w = entities.ResolveHandle(handle);
            if (w is null)
            {
                continue;
            }

            // Read the class name immediately (clobber rule) — classify before the next resolve.
            switch (Classify(w.ClassName))
            {
                case NadeKind.Flash: flash++; break;
                case NadeKind.Smoke: smoke++; break;
                case NadeKind.He: he++; break;
                case NadeKind.Molotov: molotov++; break;
                case NadeKind.Decoy: decoy++; break;
            }
        }

        List<string> parts = new(5);
        AddNade(parts, "HE", he);
        AddNade(parts, "Smoke", smoke);
        AddNade(parts, "Flash", flash);
        AddNade(parts, "Molly", molotov);
        AddNade(parts, "Decoy", decoy);
        return parts.Count == 0 ? "—" : string.Join("  ", parts);
    }

    private static NadeKind Classify(string cls)
    {
        if (cls.Contains("Flashbang", StringComparison.OrdinalIgnoreCase))
        {
            return NadeKind.Flash;
        }

        if (cls.Contains("Smoke", StringComparison.OrdinalIgnoreCase))
        {
            return NadeKind.Smoke;
        }

        if (cls.Contains("HEGrenade", StringComparison.OrdinalIgnoreCase))
        {
            return NadeKind.He;
        }

        if (cls.Contains("Molotov", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("Incendiary", StringComparison.OrdinalIgnoreCase))
        {
            return NadeKind.Molotov;
        }

        if (cls.Contains("Decoy", StringComparison.OrdinalIgnoreCase))
        {
            return NadeKind.Decoy;
        }

        return NadeKind.None;
    }

    private static void AddNade(List<string> parts, string label, int count)
    {
        if (count == 1)
        {
            parts.Add(label);
        }
        else if (count > 1)
        {
            parts.Add($"{label}×{count}");
        }
    }

    private static bool ReadBool(IReadOnlyEntity? entity, string path) =>
        // Bools arrive as Int32 (0/1) on the wire (project_cs2_wire_encoding) — compare to 0, never `is bool`.
        entity is not null && entity.TryGet(path, out int v) && v != 0;

    private static string FormatInt(IReadOnlyEntity? entity, string path)
    {
        if (entity is null)
        {
            return "—";
        }

        if (entity.TryGet(path, out int i))
        {
            return i.ToString(CultureInfo.InvariantCulture);
        }

        if (entity.TryGet(path, out uint u))
        {
            return u.ToString(CultureInfo.InvariantCulture);
        }

        return "—";
    }

    private static int ReadInt(IReadOnlyEntity? entity, string path, int fallback) =>
        entity is not null && entity.TryGet(path, out int v) ? v : fallback;

    private static float ReadFloat(IReadOnlyEntity? entity, string path, float fallback) =>
        entity is not null && entity.TryGet(path, out float v) ? v : fallback;

    // dead = m_lifeState != 0 OR m_iHealth <= 0. Reads are null/seen-tolerant.
    private static bool IsAlive(IReadOnlyEntity? pawn)
    {
        if (pawn is null)
        {
            return false;
        }

        if (pawn.TryGet("m_lifeState", out int lifeState) && lifeState != 0)
        {
            return false;
        }

        if (pawn.TryGet("m_iHealth", out int health) && health <= 0)
        {
            return false;
        }

        return true;
    }

    // Short marker label: player number (slot+1) if no name, else two-initials from the roster name.
    private string LabelFor(int slot)
    {
        if (_nameBySlot.TryGetValue(slot, out string? name) && !string.IsNullOrWhiteSpace(name))
        {
            string trimmed = name.Trim();
            return trimmed.Length <= 2 ? trimmed : trimmed[..2].ToUpperInvariant();
        }

        return (slot + 1).ToString(CultureInfo.InvariantCulture);
    }

    private enum NadeKind
    {
        None,
        He,
        Smoke,
        Flash,
        Molotov,
        Decoy
    }
}
