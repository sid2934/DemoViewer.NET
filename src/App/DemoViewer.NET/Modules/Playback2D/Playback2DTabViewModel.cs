#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using CS2DemoKit.Analysis.PlayerStats;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser.EntityTracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Playback2D;
using Microsoft.Extensions.DependencyInjection;

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
public sealed partial class Playback2DTabViewModel : ObservableObject, IWorkspaceTabViewModel, IDisposable
{
    // The inventory array slots scanned per player: the dotted bracket-indexed paths are built ONCE
    // here, not per-frame, so the per-tick grenade loop allocates no path strings.
    private const int MyWeaponsSlots = 64;
    private const int KillFeedWindowSeconds = 8; // a kill stays visible this long (game time) after it happens
    private const int MaxKillFeedRows = 6;

    // The module's own "events of interest" for forward-nav (Phase E): a 2D combat viewer scrubs between
    // kills. The filter is matched against the host's demo-derived event set, so the buttons only show when
    // the demo actually carries player_death (asset/demo-independent — no hardcoded assumption it exists).
    private const string KillEventName = "player_death";

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

    // Attributes panel: one row per roster slot, updated in place each push (no list rebuild).
    private readonly Dictionary<int, PlayerAttributes> _attrsBySlot = new();
    private readonly List<KillFeedEntry> _killWindow = new(16);

    // The scene half of a push: every marker / area-effect / trail / bomb / game-info read moved into
    // SceneFrameBuilder in B0. The VM keeps only panel state and re-publishes the built frame's contents
    // through the properties the XAML and the viewport already bind to.
    private readonly SceneFrameBuilder _frameBuilder = new();

    // The kill window projected onto the Core row type, refreshed only when the visible slice changes,
    // so the per-push builder input costs no allocation.
    private readonly List<KillFeedRow> _killRows = new(MaxKillFeedRows);

    private Scene2DFrame _frame = Scene2DFrame.Empty;

    // Slot → display name from the stable roster. Rebuilt on activation.
    private readonly Dictionary<int, string> _nameBySlot = new();

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

    // The current map's radar layers, described once per map asset (see ReplaceMapAsset).
    private IReadOnlyList<MapRadarImage> _radars = [];

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
    public IReadOnlyList<double>? SectionHeights => _frame.Map.SectionHeights;

    /// <summary>
    ///     The map's networked world-space X/Y bounds (radar bounding box), or null until read / absent. The
    ///     2D viewport's Map mode frames these EXACT playable-map bounds when present, falling back to the
    ///     all-demo observed-extent approximation otherwise. Static per map, so read once.
    /// </summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? MapBounds =>
        _frame.Map.NetworkedBounds is { } b ? (b.MinX, b.MinY, b.MaxX, b.MaxY) : null;

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
    public IReadOnlyList<PlayerMarker> Markers => _frame.Markers;

    /// <summary>Active smoke clouds + burning inferno cells (A4), drawn under the markers by the viewport.</summary>
    public IReadOnlyList<AreaEffect> AreaEffects => _frame.AreaEffects;

    /// <summary>Grenade flight trails (A4), drawn as fading comet lines beneath the markers by the viewport.</summary>
    public IReadOnlyList<GrenadeTrail> GrenadeTrails => _frame.Trails;

    /// <summary>
    ///     The planted-C4 timer-ring draw-state (A4), or null when no live ticking bomb. Read by the
    ///     custom-drawn viewport.
    /// </summary>
    public BombMarker? Bomb => _frame.Bomb;

    /// <summary>
    ///     The scene state the last push produced. B1's <c>Scene2DHost</c> submits this, and B0's golden
    ///     capture pairs it with the captured PNG. Valid until the next push — see <see cref="Scene2DFrame" />.
    /// </summary>
    public Scene2DFrame CurrentFrame => _frame;

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
        // Every dependency is optional, resolved the way Playback2DRenderer resolves its setting: the
        // descriptor's ViewModelFactory is a bare new(), and a headless test builds this with no
        // container at all. No store and no settings means annotations still work — session only.
        _annotationController = new AnnotationSessionController(
            TryResolveAnnotationStore(), TryResolveSettings());
        _annotationController.LoadRecentColors();

        Annotations = new AnnotationsPanelViewModel(_annotationController,
            () => _context?.CurrentTick ?? _frame.Time.Tick);

        _annotationTrack = new AnnotationTrack(_annotationController.Document);

        Timeline.RegisterTrack(new RoundTrack());
        Timeline.RegisterTrack(new KillTrack());
        Timeline.RegisterTrack(new BombTrack());
        Timeline.RegisterTrack(_annotationTrack);

        // The timeline never moves the clock: it asks, and the shared clock decides (so LiveSync's
        // SyncStateObserver keeps seeing every seek).
        Timeline.SeekRequested += OnTimelineSeekRequested;
    }

    private readonly AnnotationSessionController _annotationController;
    private readonly AnnotationTrack _annotationTrack;

    /// <summary>The annotation toolbar's state. A nested VM, so this class does not grow another screen.</summary>
    public AnnotationsPanelViewModel Annotations { get; }

    /// <summary>The session the v2 host's ink layer and pointer tools share with the toolbar.</summary>
    public AnnotationSession? AnnotationSession => IsAnnotationsEnabled ? Annotations.Session : null;

    /// <summary>Whether the <c>playback2d.annotations</c> feature is on. Fail-open, live.</summary>
    public bool IsAnnotationsEnabled =>
        _features?.IsEnabled(AnnotationSessionController.FeatureId) ?? true;

    // The store needs an app-data root for its fallback location and the App's cached demo hash for its
    // key; both come from the container when there is one. Pipeline must not reference the App, so the
    // App is the side that knows AppPaths.
    private static AnnotationStore? TryResolveAnnotationStore()
    {
        try
        {
            return new AnnotationStore(AppPaths.ConfigRoot);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SettingsService? TryResolveSettings()
    {
        try
        {
            return App.Services?.GetService<SettingsService>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Rebases annotation world anchors after a level-set rebuild. B3's hysteresis/rebuild path calls
    ///     this; it consumes no undo slot (plan decision D6) and covers the wet stroke too.
    /// </summary>
    /// <param name="zMinMap">Old quantized level ZMin → new quantized level ZMin.</param>
    public void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double, double> zMinMap) =>
        _annotationController.ApplyLevelRebuild(zMinMap);

    /// <summary>
    ///     Shutdown: flushes any pending annotation autosave and drops the controller's subscriptions.
    ///     The flush is synchronous here on purpose — this is the last chance the document has.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _annotationController.FlushAsync().GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            // Best-effort, exactly as SettingsService.SaveSession is: a failed write at shutdown must
            // not turn into an unhandled exception on the way out.
        }
        catch (UnauthorizedAccessException)
        {
        }

        Annotations.Dispose();
        _annotationTrack.Dispose();
        _annotationController.Dispose();
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

        _annotationController.SetFeatures(_features);
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
        AttachAnnotationsToCurrentDemo(force: false);
        Status = $"2D Playback — active · {context.CurrentPlayers.Count} players · 0 pushes";
    }

    // Loads (or reloads) the annotation sidecar for whatever demo the context is on. Fire-and-forget by
    // design: an activation must not block on a disk read, and every failure inside is already reduced
    // to a status line by the controller.
    private void AttachAnnotationsToCurrentDemo(bool force)
    {
        if (_context is not { } ctx)
        {
            return;
        }

        ClockIdentity clock = new(ClockIdentity.DvFrameClock,
            ctx.TickRate > 0 ? ctx.TickRate : 64, ctx.TotalFrames, 0, 0);

        _ = _annotationController.AttachDemoAsync(ctx.DemoPath, clock, force)
            .ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    public void OnDeactivated()
    {
        // Annotations are flushed FIRST, while the context is still attached: a debounced autosave that
        // had not fired yet is the difference between a stroke surviving a tab switch and vanishing.
        _ = _annotationController.FlushAsync().ContinueWith(static _ => { }, TaskScheduler.Default);

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

            // ── Annotations (declared by A1, bound here). Gated off, the keys stay unhandled so they
            //    can still reach whatever else wants them.
            case Playback2DAction.ToolDraw:
                if (!IsAnnotationsEnabled)
                {
                    return false;
                }

                Annotations.SelectTool(Annotations.ActiveTool == ToolKind.Draw
                    ? ToolKind.PanZoom
                    : ToolKind.Draw);
                return true;

            case Playback2DAction.ToolErase:
                if (!IsAnnotationsEnabled)
                {
                    return false;
                }

                Annotations.SelectTool(Annotations.ActiveTool == ToolKind.Erase
                    ? ToolKind.PanZoom
                    : ToolKind.Erase);
                return true;

            case Playback2DAction.Undo:
                if (!IsAnnotationsEnabled || !Annotations.CanUndo)
                {
                    return false;
                }

                Annotations.UndoCommand.Execute(null);
                return true;

            case Playback2DAction.Redo:
                if (!IsAnnotationsEnabled || !Annotations.CanRedo)
                {
                    return false;
                }

                Annotations.RedoCommand.Execute(null);
                return true;

            case Playback2DAction.ClearAnnotations:
                if (!IsAnnotationsEnabled)
                {
                    return false;
                }

                Annotations.ClearAllCommand.Execute(null);
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
        OnPropertyChanged(nameof(IsAnnotationsEnabled));
        OnPropertyChanged(nameof(AnnotationSession));
        Timeline.IsVisible = IsTimelineEnabled && (_context?.HasDemo ?? false);

        if (!IsFollowEnabled && FollowedSlot >= 0)
        {
            NotifyFollowSlotChanged(-1);
        }

        // Gated off, the surface reverts to plain pan/zoom: leaving a drawing tool selected would let a
        // click still open a gesture on a document the user can no longer see.
        if (!IsAnnotationsEnabled)
        {
            Annotations.SelectTool(ToolKind.PanZoom);
        }

        // A gate flip changes which LAYERS the surface should carry, and the surface only re-reads that
        // on a frame push. Nudging it here is the same mechanism the overlay toggles use.
        FrameUpdated?.Invoke();
    }

    /// <summary>
    ///     Session state carried across restarts: the annotation TOOL state only — active tool, ink
    ///     style, envelope defaults.
    ///     <para>
    ///         Deliberately <b>not</b> the document (that is <c>AnnotationStore</c>'s job, keyed to the
    ///         demo) and deliberately not the camera, the playhead or the selection.
    ///     </para>
    /// </summary>
    public object? SnapshotState() => new Playback2DTabState(
        Annotations.ActiveTool.ToString(),
        Annotations.InkColorHex,
        Annotations.InkWidth,
        Annotations.InkOpacity,
        Annotations.Visibility.ToString(),
        Annotations.FadeInTicks,
        Annotations.FadeOutTicks,
        Annotations.HoldTicks,
        Annotations.AnchorToEntities);

    /// <summary>
    ///     Restores <see cref="SnapshotState" />. Session state is a convenience, never a source of
    ///     truth: a blob written by an older build degrades to "restore nothing" rather than throwing on
    ///     startup.
    /// </summary>
    /// <param name="state">The persisted blob as a <c>JsonElement</c>, or null.</param>
    public void RestoreState(object? state)
    {
        if (state is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Playback2DTabState? restored;
        try
        {
            restored = element.Deserialize<Playback2DTabState>();
        }
        catch (JsonException)
        {
            return;
        }

        if (restored is null)
        {
            return;
        }

        if (Enum.TryParse(restored.ActiveTool, ignoreCase: true, out ToolKind tool))
        {
            Annotations.SelectTool(tool);
        }

        if (TryParseArgb(restored.InkColorHex, out uint argb))
        {
            Annotations.InkColor = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
                (byte)(argb >> 8), (byte)argb);
        }

        if (restored.InkWidth > 0)
        {
            Annotations.InkWidth = restored.InkWidth;
        }

        if (restored.InkOpacity is > 0 and <= 1)
        {
            Annotations.InkOpacity = restored.InkOpacity;
        }

        if (Enum.TryParse(restored.Visibility, ignoreCase: true, out EnvelopeMode mode))
        {
            Annotations.Visibility = mode;
        }

        Annotations.FadeInTicks = Math.Max(0, restored.FadeInTicks);
        Annotations.FadeOutTicks = Math.Max(0, restored.FadeOutTicks);
        Annotations.HoldTicks = Math.Max(0, restored.HoldTicks);
        Annotations.AnchorToEntities = restored.AnchorToEntities;
    }

    private static bool TryParseArgb(string? hex, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        ReadOnlySpan<char> span = hex.AsSpan().TrimStart('#');
        return span.Length == 8
               && uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
    }

    /// <summary>The 2D tab's session blob. Tool state only — never the document, camera or playhead.</summary>
    /// <param name="ActiveTool">The <c>ToolKind</c> name.</param>
    /// <param name="InkColorHex"><c>#AARRGGBB</c>.</param>
    /// <param name="InkWidth">World units.</param>
    /// <param name="InkOpacity">0..1.</param>
    /// <param name="Visibility">The <c>EnvelopeMode</c> name.</param>
    /// <param name="FadeInTicks">Lead-in ticks.</param>
    /// <param name="FadeOutTicks">Lead-out ticks.</param>
    /// <param name="HoldTicks">Fully-opaque hold.</param>
    /// <param name="AnchorToEntities">Whether new strokes follow a nearby player.</param>
    public sealed record Playback2DTabState(
        string ActiveTool,
        string InkColorHex,
        double InkWidth,
        double InkOpacity,
        string Visibility,
        int FadeInTicks,
        int FadeOutTicks,
        int HoldTicks,
        bool AnchorToEntities);

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
    private void OnDemoReset()
    {
        ResyncToCurrentDemo();

        // A demo reload is the one moment the sidecar on disk really is the newer truth, so this one
        // forces — unlike a tab re-activation, which must keep the in-memory document.
        AttachAnnotationsToCurrentDemo(force: true);
    }

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

        // Drop every per-demo cache the builder holds — ring deltas, death-marker positions, trails and
        // the once-per-demo section-height read — so nothing glides in from a prior demo or position.
        _frameBuilder.Reset();
        _tickRate = _context.TickRate > 0 ? _context.TickRate : 64;
        UpdateKillFeedWindow(_context.CurrentTick); // show the kills around the resync position immediately
        BuildFrame(_context.CurrentPlayers, _context.Entities, _context.CurrentFrameIndex, _context.CurrentTick);

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

        ReplaceMapAsset(MapAssetPipeline.TryLoad(name));
        LoadedMapNameForTest = name;

        // Map changed → the old collision engine no longer applies. Drop it and (re)load if Vision is on.
        VisionEngine = null;
        _visionEngineMap = null;
        EnsureVisionEngine();
    }

    /// <summary>
    ///     Swaps in a new map asset and DISPOSES the one it replaces. The radar images are Skia-backed, so
    ///     their pixel buffers are unmanaged (~4 MB each) — simply dropping the reference leaks them until a
    ///     finalizer happens to run, which is why a map swap used to grow native memory every time.
    ///     <para>
    ///         The old asset is disposed at Background priority rather than inline: the render thread may
    ///         still be replaying a cached SKPicture that references one of these images for the frame in
    ///         flight. B1 made that genuinely load-bearing rather than belt-and-braces — the render gate
    ///         plus this one dispatcher hop is what covers the window.
    ///     </para>
    /// </summary>
    private void ReplaceMapAsset(LoadedMapAsset? next)
    {
        LoadedMapAsset? previous = MapAsset;
        MapAsset = next;

        // Described ONCE per map, not per push: the frame publishes the same list instance every frame
        // so SceneFrameBuilder's "map facts unchanged" short-circuit holds and the steady state stays
        // allocation-free.
        _radars = next is null ? [] : MapAssetPipeline.DescribeRadars(next);

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

        // The kill window is refreshed BEFORE the frame is built so the built frame carries this tick's
        // rows (B4's HUD layer reads Scene2DFrame.KillFeed). It is a pure filter over the pre-built
        // timeline, so the order is free of side effects.
        UpdateKillFeedWindow(snapshot.Tick);

        // Seek detection (backward → drop the ring delta + death-marker caches; any large jump → drop the
        // live-accumulated trails) now lives in SceneFrameBuilder, which owns the frame delta.
        BuildFrame(snapshot.Players, snapshot.Entities, snapshot.FrameIndex, snapshot.Tick);
        Status = $"2D Playback — active · frame {snapshot.FrameIndex} · " +
                 $"{_frame.Markers.Count} players · {PushCount} pushes";

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
        _killRows.Clear();
        for (int i = start; i < _killWindow.Count; i++)
        {
            KillFeedEntry k = _killWindow[i];
            KillFeed.Add(k);
            _killRows.Add(new KillFeedRow(k.Tick, k.KillerName, k.AssisterName, k.VictimName, k.Weapon,
                k.IsHeadshot, k.IsWallbang, k.IsNoScope, k.IsThroughSmoke, k.AttackerBlind, k.AttackerInAir,
                k.IsFlashAssist));
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

    // Builds one push: seed the roster display if it arrived late, run the ATTRIBUTES pass over the
    // players (panel state — stays here), then hand the same tick to SceneFrameBuilder for the SCENE
    // state and copy the results onto the bound surface.
    //
    // Two passes over `players` on purpose. Before B0 a single loop interleaved the panel read with
    // marker construction; splitting panel from scene is the whole point of the extraction, and
    // players.Count is ~10, so the second pass is free. Do not "optimise" them back together.
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

        // Mark all attribute rows not-live; live players below flip themselves back. A player who left
        // (disconnect / pre-spawn) thus resets to placeholders instead of showing stale state.
        foreach (PlayerAttributes row in Attributes)
        {
            row.HasLivePawn = false;
        }

        SceneFrameInput input = new()
        {
            Players = players,
            Entities = entities,
            FrameIndex = frameIndex,
            Tick = tick,
            TickRate = _tickRate,
            CurtimeSeconds = _context?.CurtimeSeconds(tick) ?? tick / (double)(_tickRate > 0 ? _tickRate : 64),
            LabelForSlot = LabelFor,
            MapName = _context?.MapName,
            KillFeed = _killRows,
            Radars = _radars,
            FollowSlot = FollowedSlot
        };

        _frame = _frameBuilder.Build(in input);
        PublishGameInfo(_frame.GameInfo);

        foreach (IPlayerState p in players)
        {
            IReadOnlyEntity? pawn = p.Pawn;
            bool hasPawn = p.HasLivePawn && pawn is not null;
            int health = ReadInt(pawn, "m_iHealth", hasPawn ? 100 : 0);
            bool alive = hasPawn && IsAlive(pawn);

            // Attributes for EVERY roster player — a dead/orphaned player keeps its controller-sourced
            // stats (K/D/A, cash, score) and grays out, instead of vanishing from the panel.
            UpdateAttributes(p, entities, health, alive, _frame.GameInfo.RoundsPlayed);
        }
    }

    // Copies the built frame's round state onto the bound ObservableObject. RoundNumber is the one
    // reshape: the scene carries it as an int (0 = unknown) and the panel shows a string.
    private void PublishGameInfo(SceneGameInfo info)
    {
        GameInfo.Phase = info.Phase;
        GameInfo.BombState = info.BombState;
        GameInfo.RoundNumber = info.RoundNumber > 0
            ? info.RoundNumber.ToString(CultureInfo.InvariantCulture)
            : "—";
        GameInfo.RoundSeconds = info.RoundSeconds;
        GameInfo.RoundTime = info.RoundTime;
        GameInfo.BombTicking = info.BombTicking;
        GameInfo.DefuseInProgress = info.DefuseInProgress;
        GameInfo.DefuseKitNote = info.DefuseKitNote;
        GameInfo.DefuseSeconds = info.DefuseSeconds;
        GameInfo.DefuseTime = info.DefuseTime;
        GameInfo.TScore = info.TScore;
        GameInfo.CtScore = info.CtScore;
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
    private void UpdateAttributes(IPlayerState p, IReadOnlyEntityView entities, int health, bool alive,
        int roundsPlayed)
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
        // (carried on the built frame as SceneGameInfo.RoundsPlayed). Floored at 1 round so the opening round (0
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
            int denom = Math.Max(1, roundsPlayed);
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
