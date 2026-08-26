#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Analysis.PlayerStats;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Hud;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline;

/// <summary>
///     Turns one tick of host state (players + entities) into a <see cref="Scene2DFrame" />. Lifted
///     wholesale out of <c>Playback2DTabViewModel.BuildFrame</c> in B0 with no logic changes — field
///     paths, defaults, ordering and fallbacks are identical, because the App's Playback2D suite is the
///     behaviour-identity gate for this extraction.
///     <para>
///         <b>Double-buffered (decision D6).</b> Two <see cref="Scene2DFrame" /> instances, each wired
///         once to its own pooled backing lists, are refilled in place and published alternately, so a
///         steady-state <see cref="Build" /> allocates only the two clock strings. A returned frame is
///         valid until the second-next <see cref="Build" /> on this instance; the documented contract is
///         the stricter "until the next", so nothing may rely on the extra generation.
///     </para>
/// </summary>
public sealed class SceneFrameBuilder
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

    private const int MaxMinimapSections = 8; // engine array is fixed; scan a small bounded count.

    // How many pushes to keep re-reading m_MinimapVerticalSectionHeights before concluding the map does
    // not publish it. The array is networked in the first few ticks on a map that HAS one; a map that
    // does not (every single-floor map, i.e. most of them) would otherwise re-scan eight field paths on
    // every push, for the whole demo, forever — the retry was unbounded, and it is a per-frame
    // allocation on the common case (B0 review carry-forward (a), fixed in B1).
    private const int MaxSectionHeightAttempts = 256;

    // The field paths, built once. String interpolation inside the scan allocated eight strings per
    // attempt; on a map with no sections that was eight strings per FRAME.
    private static readonly string[] _sectionHeightPaths = BuildSectionHeightPaths();

    // The grenade-projectile classes whose flight paths get a trail (all derive from CBaseCSGrenadeProjectile,
    // all carry CBodyComponent cell coords). Built once (static) so the per-frame scan allocates no strings.
    private static readonly string[] _grenadeProjectileClasses =
    {
        "CHEGrenadeProjectile", "CFlashbangProjectile", "CSmokeGrenadeProjectile", "CMolotovProjectile",
        "CDecoyProjectile"
    };

    private readonly FrameSlot _slotA = new();
    private readonly FrameSlot _slotB = new();

    // Last-known world position per slot. When a pawn orphans on death (no live position) a gray marker
    // is held here until respawn. Cleared with the ring cache on backward seek / reset.
    private readonly Dictionary<int, (float X, float Y, float Z)> _lastKnownPos = new(16);

    private readonly RingStateTracker _ringTracker;

    // Grenade flight trails keyed by the projectile's network Serial (the entity index is reused on
    // detonation; Serial survives it). Cleared wholesale on a discontinuous frame jump so a polyline never
    // streaks from a pre-seek point to a post-seek point.
    private readonly Dictionary<int, GrenadeTrail> _trails = new(16);
    private readonly List<int> _trailsToPrune = new(8);

    // Weapon class name → short name, resolved once per class. See ActiveWeapon.
    private readonly Dictionary<string, string> _weaponNames = new(32, StringComparer.Ordinal);

    // m:ss formatting cache. FormatClock is the only per-frame allocation left in Build, and the value
    // changes at most once a second, so keying the cached string on the rounded second makes a steady-state
    // frame allocation-free.
    private int _clockCacheSeconds = int.MinValue;
    private string _clockCacheText = "0:00";

    // The round-level HUD state, PERSISTED between frames — see BuildGameInfo for why.
    private BombMarker? _bomb;
    private string _hudBombState = "—";
    private bool _hudBombTicking;
    private int _hudCtScore;
    private bool _hudDefuseInProgress;
    private string _hudDefuseKitNote = "—";
    private double _hudDefuseSeconds = double.NaN;
    private string _hudDefuseTime = "—";
    private string _hudPhase = "—";
    private int _hudRoundNumber;
    private int _hudRoundsPlayed = -1;
    private double _hudRoundSeconds = double.NaN;
    private string _hudRoundTime = "—";
    private int _hudTScore;

    private int _lastFrameIndex = -1;
    private SceneMapInfo _map = SceneMapInfo.Unknown;
    private WorldBounds? _networkedBounds;
    private WorldBounds _observed = WorldBounds.Default;
    private bool _observedSeeded;
    private IReadOnlyList<MapRadarImage> _radars = [];
    private double[]? _sectionHeights;
    private bool _sectionHeightsRead;
    private int _sectionHeightAttempts;
    private bool _useSlotB;

    /// <summary>Creates a builder.</summary>
    /// <param name="ringDecayFrames">Frames a shoot / take-damage ring flash stays lit before decaying.</param>
    public SceneFrameBuilder(int ringDecayFrames = 8) => _ringTracker = new RingStateTracker(ringDecayFrames);

    /// <summary>
    ///     The running extent of every marker position observed since the last <see cref="Reset" /> — the
    ///     Map-mode fallback when the map publishes no networked bounds. Only ever widened.
    /// </summary>
    public WorldBounds ObservedBounds => _observed;

    /// <summary>
    ///     The export HUD's player cards for the frame <see cref="Build" /> produced most recently, in slot
    ///     order (D3b, registry D0 §3.2). Empty before the first build.
    ///     <para>
    ///         <b>A sibling of the frame, not a member of it.</b> <c>Scene2DFrame</c> is B0's record and
    ///         adding to it is "a guaranteed merge conflict for no gain" in <c>IHudDataSource</c>'s own
    ///         words; the HUD is a function of tick queried through <c>IHudDataSource</c>, and this is the
    ///         property that function reads — exactly as <c>TrackerFrameSource.LastGameInfo</c> is for the
    ///         clock.
    ///     </para>
    ///     <para>
    ///         Borrowed on the frame's own terms (decision D6): the list is one of the two pooled slots and
    ///         is refilled in place, so it is valid until the next <see cref="Build" /> on this builder.
    ///         Never retain it.
    ///     </para>
    /// </summary>
    public IReadOnlyList<HudPlayerRow> LastRoster { get; private set; } = [];

    /// <summary>
    ///     Clears trails, ring history, last-known positions and the once-per-demo section-height read.
    ///     Call on demo reset / re-activation.
    ///     <para>
    ///         Deliberately does <b>not</b> clear the networked map bounds: the pre-v2 view-model's resync
    ///         did not either (<c>ReadMapBoundsOnce</c> latches on first success and its resync path reset
    ///         only the section heights), and B0 is a behaviour-identical extraction. Revisit in B3, which
    ///         owns map identity.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        _ringTracker.Reset();
        _lastKnownPos.Clear();
        _trails.Clear();
        _sectionHeights = null;
        _sectionHeightsRead = false;
        _sectionHeightAttempts = 0;
        _lastFrameIndex = -1;
        _observed = WorldBounds.Default;
        _observedSeeded = false;

        // The roster is a cache like every other one cleared above, and it was the one exception:
        // TrackerFrameSource.LastRoster is assigned straight from here, so after a demo reset it went
        // on pointing at the PREVIOUS demo's pooled list until the next Build — a HUD source reading it
        // in that window burns the old match's cards into the new one's frames (D6 finding 32).
        LastRoster = [];
    }

    /// <summary>
    ///     Builds the scene for one tick. The returned frame is valid until the next call on this
    ///     instance (decision D6) — never retain it.
    /// </summary>
    /// <param name="input">This tick's host state.</param>
    public Scene2DFrame Build(in SceneFrameInput input)
    {
        // Seek detection, lifted from the view-model's OnAdvanced guards. Backward motion invalidates the
        // health/shots delta cache (a stale prior sample would manufacture a false ring flash) and the
        // death-marker positions; any jump larger than a fast normal push invalidates the live-accumulated
        // trails (otherwise a forward seek into a grenade's flight streaks a line across the map).
        bool backward = _lastFrameIndex >= 0 && input.FrameIndex < _lastFrameIndex;
        bool jumped = _lastFrameIndex >= 0 && Math.Abs(input.FrameIndex - _lastFrameIndex) > TrailJumpThreshold;

        if (backward)
        {
            _ringTracker.Reset();
            _lastKnownPos.Clear();
        }

        if (jumped)
        {
            _trails.Clear();
        }

        _lastFrameIndex = input.FrameIndex;

        int tickRate = input.TickRate > 0 ? input.TickRate : 64;
        FrameSlot slot = _useSlotB ? _slotB : _slotA;
        _useSlotB = !_useSlotB;

        slot.Markers.Clear();
        slot.AreaEffects.Clear();
        slot.Trails.Clear();
        slot.Roster.Clear();

        SceneGameInfo gameInfo = BuildGameInfo(input);
        UpdateAreaEffects(input.Entities, slot.AreaEffects);
        UpdateTrajectories(input.Entities, input.Tick, tickRate, slot.Trails);
        BuildMarkers(input, slot.Markers, slot.Roster);
        LastRoster = slot.Roster;

        Scene2DFrame frame = slot.Frame;
        frame.TimeField = new SceneTime(
            input.Tick,
            input.FrameIndex,
            input.CurtimeSeconds,
            tickRate > 0 ? 1.0 / tickRate : 0,
            backward || jumped);
        frame.BombField = _bomb;
        frame.GameInfoField = gameInfo;
        frame.MapField = ResolveMap(input);
        frame.VisionField = input.Vision ?? SceneVision.Off;
        frame.KillFeedField = input.KillFeed ?? [];
        frame.FollowSlotField = input.FollowSlot;
        return frame;
    }

    // ── Markers ─────────────────────────────────────────────────────────────────────────────────────

    // Copies out the scalars the scene needs from the transient/pooled player states (lifetime rule: read
    // inside the callback, copy to value types, never retain the pooled instance). Per-player cost is
    // O(players) via the allocation-free indexer — never EntityState.Fields.
    //
    // The roster is built in this same pass rather than beside it: health is already read here for the
    // ring state and was thrown away afterwards, and a second sweep would re-walk the same pooled facades
    // for the same fields. D3b's whole point is that the export HUD reads the entities ONCE, from the one
    // place both the app and dv2d already go through.
    private void BuildMarkers(in SceneFrameInput input, List<PlayerMarker> markers, List<HudPlayerRow> roster)
    {
        Func<int, string> labelFor = input.LabelForSlot;
        Func<int, ulong>? steamIdFor = input.SteamIdForSlot;

        foreach (IPlayerState p in input.Players)
        {
            IReadOnlyEntity? pawn = p.Pawn;
            bool hasPawn = p.HasLivePawn && pawn is not null;

            // Ring-state inputs — all null/seen-tolerant.
            int health = ReadInt(pawn, "m_iHealth", hasPawn ? 100 : 0);
            int shotsFired = ReadInt(pawn, "m_iShotsFired", 0);
            float flash = ReadFloat(pawn, "m_flFlashDuration", 0);
            bool alive = hasPawn && IsAlive(pawn);
            ulong steamId = steamIdFor?.Invoke(p.Slot) ?? 0;

            AddRosterRow(roster, p, input.Entities, labelFor(p.Slot), health, alive);

            if (p.WorldPosition is { } pos)
            {
                // Live pawn: remember the spot, draw the marker (the ring goes gray when dead).
                _lastKnownPos[p.Slot] = (pos.X, pos.Y, pos.Z);
                Observe(pos.X, pos.Y);

                float yaw = 0, pitch = 0;
                if (pawn is not null && pawn.TryGet("m_angEyeAngles", out Vector3 eye))
                {
                    pitch = eye.X; // pitch = .X, yaw = .Y, roll = .Z
                    yaw = eye.Y;
                }

                float duck = ReadFloat(pawn, "m_pMovementServices.m_flDuckAmount", 0);

                (RingState ring, double ringAlpha) =
                    _ringTracker.Evaluate(p.Slot, input.FrameIndex, alive, flash, health, shotsFired);

                markers.Add(new PlayerMarker(
                    p.Slot,
                    p.Team,
                    pos.X,
                    pos.Y,
                    pos.Z,
                    yaw,
                    ring,
                    ringAlpha,
                    labelFor(p.Slot),
                    alive,
                    pitch,
                    duck,
                    steamId));
            }
            else if (!alive && _lastKnownPos.TryGetValue(p.Slot, out (float X, float Y, float Z) last))
            {
                // Dead pawn has orphaned (no live position this tick) — hold a gray marker at the death
                // spot with the correct roster label until the player respawns (standard death marker).
                markers.Add(new PlayerMarker(
                    p.Slot,
                    p.Team,
                    last.X,
                    last.Y,
                    last.Z,
                    0,
                    RingState.Dead,
                    1.0,
                    labelFor(p.Slot),
                    false,
                    0,
                    0,
                    steamId));
            }
        }
    }

    // One player card. The field paths are the ones Playback2DTabViewModel.UpdateAttributes reads for the
    // app's attributes panel, verbatim — pawn for condition, controller for the cumulative scoreboard —
    // because the panel and the burnt-in card disagreeing about a player's health is the same class of bug
    // B4 D5 removed from the kill feed.
    //
    // Emitted for EVERY roster row including the dead and the sideless: the layer decides who gets an edge
    // of the frame, and dropping a dead player here would make his card vanish the instant it matters most.
    private void AddRosterRow(List<HudPlayerRow> roster, IPlayerState p, IReadOnlyEntityView entities,
        string label, int health, bool alive)
    {
        IReadOnlyEntity? pawn = p.Pawn;
        IReadOnlyEntity? ctrl = p.Controller;

        roster.Add(new HudPlayerRow(
            p.Slot,
            p.Team,
            label,
            alive,
            alive ? health : 0,
            ReadIntOr(pawn, "m_ArmorValue", 0),
            ReadBool(pawn, "m_pItemServices.m_bHasHelmet"),
            ReadBool(pawn, "m_pItemServices.m_bHasDefuser"),
            ActiveWeapon(pawn, entities),
            ReadIntOr(ctrl, "m_pInGameMoneyServices.m_iAccount", 0),
            ReadIntOr(ctrl, "m_pActionTrackingServices.m_iKills", 0),
            ReadIntOr(ctrl, "m_pActionTrackingServices.m_iDeaths", 0),
            ReadIntOr(ctrl, "m_pActionTrackingServices.m_iAssists", 0)));
    }

    // One handle hop, and the class name read IMMEDIATELY (the clobber rule: ResolveHandle hands back a
    // SHARED pooled facade, so anything not read before the next resolve is read off the wrong entity).
    // The short name is memoised by class name because WeaponShortName allocates and the answer is fixed
    // per class — otherwise this is one string per player per frame, in the method §6's budget measures.
    private string ActiveWeapon(IReadOnlyEntity? pawn, IReadOnlyEntityView entities)
    {
        if (pawn is null || !pawn.TryGet("m_pWeaponServices.m_hActiveWeapon", out ulong handle) ||
            handle == 0)
        {
            return "—";
        }

        if (entities.ResolveHandle(handle) is not { } weapon)
        {
            return "—";
        }

        string cls = weapon.ClassName;
        if (_weaponNames.TryGetValue(cls, out string? cached))
        {
            return cached;
        }

        string name = PlayerSnapshotBuilder.WeaponShortName(cls);
        _weaponNames[cls] = name;
        return name;
    }

    // The one entry point into the observed extent, and therefore the one place a non-finite coordinate
    // can be stopped. It has to be stopped HERE because _observed is only ever WIDENED and never
    // re-seeded: WorldBounds.Extend is Math.Min/Math.Max, both of which propagate NaN, so a single bad
    // sample poisons the extent for the whole demo — and from there ViewportTransform.Fit hands the
    // camera a NaN centre and scale, IsSettledAt never settles, and the render loop spins at refresh
    // rate showing nothing, across seeks included (D6 finding 8). A position that is not a number is not
    // a position; dropping the sample costs one marker's contribution to the fallback rectangle.
    private void Observe(float worldX, float worldY)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY))
        {
            return;
        }

        if (_observedSeeded)
        {
            _observed = _observed.Extend(worldX, worldY);
            return;
        }

        _observed = new WorldBounds(worldX, worldY, worldX, worldY);
        _observedSeeded = true;
    }

    // ── Map facts ───────────────────────────────────────────────────────────────────────────────────

    // SceneMapInfo is rebuilt only when one of its inputs actually changed, so the steady-state frame
    // publishes the same instance every push and allocates nothing.
    private SceneMapInfo ResolveMap(in SceneFrameInput input)
    {
        string mapName = input.MapName ?? "";
        IReadOnlyList<MapRadarImage> radars = input.Radars ?? [];

        bool unchanged = string.Equals(_map.MapName, mapName, StringComparison.Ordinal)
                         && ReferenceEquals(_radars, radars)
                         && _map.NetworkedBounds.Equals(_networkedBounds)
                         && ReferenceEquals(_map.SectionHeights, _sectionHeights)
                         && _map.ObservedBounds.Equals(_observed);
        if (unchanged)
        {
            return _map;
        }

        _radars = radars;
        _map = new SceneMapInfo
        {
            MapName = mapName,
            NetworkedBounds = _networkedBounds,
            ObservedBounds = _observed,
            SectionHeights = _sectionHeights,
            Radars = radars
        };
        return _map;
    }

    // ── Game info, bomb, round clock ────────────────────────────────────────────────────────────────

    // Round-level game info, read ONCE per frame (NOT per-player). OfClass allocates a fresh facade per
    // element — acceptable for this once-per-frame read, never in the per-player hot loop. Paths verified
    // against a real demo: CCSGameRulesProxy.m_pGameRules.* and CCSTeam.m_iScore filtered by m_iTeamNum.
    //
    // The `_hud` fields PERSIST between frames on purpose. The pre-v2 view-model held this state on an
    // ObservableObject and mutated it in place, so a frame in which the rules entity is not decoded (a
    // seek can land there) left every round field at its previous value rather than blanking the panel.
    // Rebuilding the record from scratch each frame would have changed that, so the record is assembled
    // from persistent fields instead.
    private SceneGameInfo BuildGameInfo(in SceneFrameInput input)
    {
        IReadOnlyEntityView entities = input.Entities;

        IReadOnlyEntity? rules = null;
        foreach (IReadOnlyEntity e in entities.OfClass("CCSGameRulesProxy"))
        {
            rules = e; // there is exactly one CCSGameRulesProxy per match
            break;
        }

        if (rules is not null)
        {
            // Static per map, read once: the real Z-floor boundaries and the real world-space X/Y bounds.
            ReadSectionHeightsOnce(rules);
            ReadMapBoundsOnce(rules);

            bool warmup = ReadBool(rules, "m_pGameRules.m_bWarmupPeriod");
            bool freeze = ReadBool(rules, "m_pGameRules.m_bFreezePeriod");
            bool planted = ReadBool(rules, "m_pGameRules.m_bBombPlanted");
            bool defused = ReadBool(rules, "m_pGameRules.m_bBombDefused");
            bool dropped = ReadBool(rules, "m_pGameRules.m_bBombDropped");

            _hudPhase = warmup ? "Warmup" : freeze ? "Freeze" : "Live";
            _hudBombState = defused ? "Defused"
                : planted ? "Planted"
                : dropped ? "Dropped"
                : "—";

            _hudRoundsPlayed = ReadIntOr(rules, "m_pGameRules.m_totalRoundsPlayed", -1);
            _hudRoundNumber = _hudRoundsPlayed >= 0 ? _hudRoundsPlayed + 1 : 0;

            // Bomb/round main countdown. Priority: a LIVE ticking CPlantedC4 replaces the round clock with
            // the C4 detonation countdown; otherwise the freeze state; otherwise the round clock. The
            // detonation timer is driven off the ENTITY (m_bBombTicking / m_flC4Blow), not
            // m_pGameRules.m_bBombPlanted — the entity carries the absolute blow time.
            if (UpdateBombTimers(input))
            {
                // The detonation countdown owns the main timer this frame; the defuse second-timer was set
                // inside UpdateBombTimers.
            }
            else if (freeze)
            {
                _hudRoundSeconds = double.NaN;
                _hudRoundTime = "freeze";
            }
            else if (rules.TryGet("m_pGameRules.m_fRoundStartTime", out float roundStart))
            {
                // Round time remaining = m_fRoundStartTime + m_iRoundTime − correctedCurtime. The round
                // length is the NETWORKED m_iRoundTime, not an assumed convar.
                double roundLen = ReadIntOr(rules, "m_pGameRules.m_iRoundTime", 0);
                if (roundLen <= 0)
                {
                    roundLen = FallbackRoundSeconds;
                }

                double remaining = roundStart + roundLen - input.CurtimeSeconds;
                _hudRoundSeconds = remaining;
                _hudRoundTime = remaining > 0 ? FormatClock(remaining) : "0:00";
            }
        }

        // Team score: CCSTeam.m_iScore filtered by m_iTeamNum (2=T, 3=CT).
        foreach (IReadOnlyEntity team in entities.OfClass("CCSTeam"))
        {
            int num = ReadIntOr(team, "m_iTeamNum", -1);
            int score = ReadIntOr(team, "m_iScore", 0);
            if (num == 2)
            {
                _hudTScore = score;
            }
            else if (num == 3)
            {
                _hudCtScore = score;
            }
        }

        return new SceneGameInfo(_hudPhase, _hudBombState, _hudRoundNumber, _hudRoundsPlayed,
            _hudRoundSeconds, _hudRoundTime, _hudBombTicking, _hudDefuseInProgress, _hudDefuseKitNote,
            _hudDefuseSeconds, _hudDefuseTime, _hudTScore, _hudCtScore);
    }

    // Reads CCSGameRulesProxy.m_pGameRules.m_MinimapVerticalSectionHeights[0..N] ONCE — the map's real
    // Z-floor boundaries (e.g. Nuke [1.81, 51.54, 287.0, 376.0]). The engine array is fixed-size; scan a
    // bounded count and stop at the first sentinel (3.4e38 ≈ float.MaxValue) or non-ascending value (an
    // unused trailing 0 slot). A map without floor sections publishes ≤1 usable value → null.
    private void ReadSectionHeightsOnce(IReadOnlyEntity rules)
    {
        if (_sectionHeightsRead)
        {
            return;
        }

        // Bounded retry. The array resolves within the first few pushes on a map that publishes one; on
        // a map that does not, an unbounded retry re-scanned eight field paths on every frame for the
        // whole demo. Giving up after a few seconds of play is not a loss: a map that has not networked
        // its section heights by then does not have any.
        if (++_sectionHeightAttempts > MaxSectionHeightAttempts)
        {
            _sectionHeightsRead = true;
            return;
        }

        List<double> kept = new(MaxMinimapSections);
        for (int i = 0; i < MaxMinimapSections; i++)
        {
            if (!rules.TryGet(_sectionHeightPaths[i], out float h))
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

        // Two or more boundaries describe a real multi-floor map; fewer ⇒ leave null.
        _sectionHeights = kept.Count >= 2 ? kept.ToArray() : null;

        // Only latch "read" once the field actually resolved (≥1 value seen); otherwise the array has not
        // been networked yet this frame and we retry next frame.
        if (kept.Count >= 1)
        {
            _sectionHeightsRead = true;
        }
    }

    private static string[] BuildSectionHeightPaths()
    {
        string[] paths = new string[MaxMinimapSections];
        for (int i = 0; i < MaxMinimapSections; i++)
        {
            paths[i] = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"m_pGameRules.m_MinimapVerticalSectionHeights[{i}]");
        }

        return paths;
    }

    // Reads CCSGameRulesProxy.m_pGameRules.m_vMinimapMins / m_vMinimapMaxs (Vector3 world-space radar
    // bounding box) ONCE — the REAL playable-map X/Y extent. Static per map.
    private void ReadMapBoundsOnce(IReadOnlyEntity rules)
    {
        if (_networkedBounds is not null)
        {
            return;
        }

        if (rules.TryGet("m_pGameRules.m_vMinimapMins", out Vector3 mins) &&
            rules.TryGet("m_pGameRules.m_vMinimapMaxs", out Vector3 maxs) &&
            maxs.X > mins.X && maxs.Y > mins.Y)
        {
            _networkedBounds = new WorldBounds(mins.X, mins.Y, maxs.X, maxs.Y);
        }
    }

    // Bomb plant/defuse + C4 detonation timers. Finds a live ticking CPlantedC4 (entity-driven —
    // m_bBombTicking, NOT m_pGameRules.m_bBombPlanted, which lags the entity) and, when present, replaces
    // the main countdown with the detonation remaining (m_flC4Blow − correctedCurtime). During a
    // defuse-in-progress (m_bBeingDefused) the SECOND timer shows the defuse-completion remaining
    // (m_flDefuseCountDown − correctedCurtime), so the panel shows the defuse-vs-detonation race. The
    // defuse length (m_flDefuseLength) already encodes kit (5s) vs no-kit (10s); the defuser's
    // m_bHasDefuser only labels it. Returns true iff a ticking C4 owns the main timer this frame; clears
    // all bomb/defuse state and returns false otherwise (so the round clock / freeze branch runs).
    private bool UpdateBombTimers(in SceneFrameInput input)
    {
        IReadOnlyEntity? c4 = null;
        foreach (IReadOnlyEntity e in input.Entities.OfClass("CPlantedC4"))
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

        double now = input.CurtimeSeconds;

        // Main countdown → C4 detonation remaining.
        double detonation = blow - now;
        _hudBombTicking = true;
        _hudRoundSeconds = detonation;
        _hudRoundTime = detonation > 0 ? FormatClock(detonation) : "0:00";

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
            _hudDefuseInProgress = true;
            _hudDefuseSeconds = defuseRemain;
            _hudDefuseTime = defuseRemain > 0 ? FormatClock(defuseRemain) : "0:00";
            // m_flDefuseLength is 5 with a kit, 10 without — surface that as a label.
            _hudDefuseKitNote = defuseLen > 0 && defuseLen <= 6 ? "with kit" : "no kit";

            beingDefused = true;
            defuseFraction = defuseLen > 0 ? Math.Clamp(defuseRemain / defuseLen, 0, 1) : 0;
        }
        else
        {
            ClearDefuseTimer();
        }

        // Bomb ring draw-state — only when its world position reconstructs (CPlantedC4 cell coords, same
        // encoding as pawns). Null position → no ring (the game-info timer still shows).
        _bomb = ReconstructWorld(c4) is { } pos
            ? new BombMarker(pos.X, pos.Y, pos.Z, detonationFraction, beingDefused, defuseFraction)
            : null;

        return true;
    }

    private void ClearBombTimers()
    {
        _hudBombTicking = false;
        _bomb = null;
        ClearDefuseTimer();
    }

    private void ClearDefuseTimer()
    {
        _hudDefuseInProgress = false;
        _hudDefuseSeconds = double.NaN;
        _hudDefuseTime = "—";
        _hudDefuseKitNote = "—";
    }

    // ── Area effects ────────────────────────────────────────────────────────────────────────────────

    // Active smoke clouds + burning inferno cells. Once per frame (OfClass allocates a facade per element
    // — acceptable for a handful of live grenades, never the per-player hot loop). World positions are
    // networked directly: smoke centre = m_vSmokeDetonationPos (once m_nSmokeEffectTickBegin > 0, i.e.
    // detonated/billowing, not the still-flying projectile); fire cells = m_firePositions[i] for the
    // m_fireCount active cells where m_bFireIsBurning[i].
    private static void UpdateAreaEffects(IReadOnlyEntityView entities, List<AreaEffect> areaEffects)
    {
        foreach (IReadOnlyEntity smoke in entities.OfClass("CSmokeGrenadeProjectile"))
        {
            if (ReadIntOr(smoke, "m_nSmokeEffectTickBegin", 0) <= 0)
            {
                continue; // still a flying projectile, not yet a billowing cloud
            }

            if (smoke.TryGet("m_vSmokeDetonationPos", out Vector3 pos) && (pos.X != 0 || pos.Y != 0))
            {
                areaEffects.Add(new AreaEffect(AreaEffectKind.Smoke, pos.X, pos.Y, pos.Z, SmokeRadiusWorld));
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
                    areaEffects.Add(new AreaEffect(
                        AreaEffectKind.Fire, cell.X, cell.Y, cell.Z, FireCellRadiusWorld));
                }
            }
        }
    }

    // ── Trails ──────────────────────────────────────────────────────────────────────────────────────

    // LIVE-accumulate each in-flight projectile's reconstructed world position into its Serial-keyed trail,
    // then fade/prune trails whose projectile has stopped. Projectile positions are NOT host-joined (the
    // host only joins player positions), so they are reconstructed from CBodyComponent cells via the
    // oracle-pinned PositionUtil.Axis — the same path the planted-C4 ring uses. The discontinuous-jump
    // clear lives in Build (it owns the frame delta); this method only grows + ages trails monotonically.
    private void UpdateTrajectories(IReadOnlyEntityView entities, int tick, int tickRate,
        List<GrenadeTrail> trailViews)
    {
        // 1) Sample every in-flight grenade projectile into its trail. Append only when advancing past the
        //    last sample AND the projectile actually moved, so a paused/coalesced re-push (same tick) or a
        //    small backward micro-step does not pile points or kink the line backward.
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

                // LastTick tracks the last MOVE, not the last sighting — so a landed-but-still-alive smoke
                // or decoy fades instead of holding forever.
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

        // 2) Fade by time-since-last-MOVE: a trail still moving (or whose playhead stepped back to/before
        //    its last move) holds full opacity; one that has stopped fades over the window and is pruned.
        //    Rebuild the draw-state from the survivors (≥2 points to be a visible line).
        int fadeTicks = TrailFadeSeconds * Math.Max(1, tickRate);
        _trailsToPrune.Clear();

        foreach (KeyValuePair<int, GrenadeTrail> kv in _trails)
        {
            GrenadeTrail t = kv.Value;
            int age = tick - t.LastTick;

            if (age <= 0)
            {
                t.Alpha = 1.0; // moving this frame, or the playhead stepped back to/before its last move
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
                trailViews.Add(t);
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

    // Two samples are the "same" point (skip the append) when within half a world unit on each axis.
    private static bool SamePoint(GrenadeTrailPoint a, (float X, float Y, float Z) b) =>
        Math.Abs(a.X - b.X) < 0.5f && Math.Abs(a.Y - b.Y) < 0.5f && Math.Abs(a.Z - b.Z) < 0.5f;

    // ── Entity read helpers ─────────────────────────────────────────────────────────────────────────

    // Reconstructs a non-player entity's world position from its CBodyComponent cell coords, reusing the
    // oracle-pinned PositionUtil.Axis formula (the load-bearing constant stays in one place). The host only
    // joins PLAYER positions; the C4 and the projectiles have none.
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

    private string FormatClock(double seconds)
    {
        int s = (int)Math.Round(seconds);
        if (s == _clockCacheSeconds)
        {
            return _clockCacheText;
        }

        _clockCacheSeconds = s;
        _clockCacheText = string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:D2}");
        return _clockCacheText;
    }

    // Null-tolerant: the roster reads a CONTROLLER, which is absent for a slot the demo never seated, and
    // the correct answer there is the fallback rather than a guard at every call site.
    private static int ReadIntOr(IReadOnlyEntity? entity, string path, int fallback)
    {
        if (entity is null)
        {
            return fallback;
        }

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

    private static bool ReadBool(IReadOnlyEntity? entity, string path) =>
        // Bools arrive as Int32 (0/1) on the wire — compare to 0, never `is bool`.
        entity is not null && entity.TryGet(path, out int v) && v != 0;

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

    // One published frame plus the pooled lists wired into it. Both slots exist for the whole builder
    // lifetime; only their contents change.
    private sealed class FrameSlot
    {
        public FrameSlot() =>
            Frame = new Scene2DFrame
            {
                Markers = Markers,
                AreaEffects = AreaEffects,
                Trails = Trails
            };

        public Scene2DFrame Frame { get; }
        public List<PlayerMarker> Markers { get; } = new(16);
        public List<AreaEffect> AreaEffects { get; } = new(32);
        public List<GrenadeTrail> Trails { get; } = new(16);

        // Pooled with the rest, but NOT wired into Frame: the roster is published through
        // SceneFrameBuilder.LastRoster, because Scene2DFrame does not grow a member for it.
        public List<HudPlayerRow> Roster { get; } = new(16);
    }
}
