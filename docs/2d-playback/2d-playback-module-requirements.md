# 2D Playback Module — Requirements & Design

Shipped / as-built — the 2D Playback module is implemented on main; §11 is the as-built binding the
module code cites.
**Owner module:** new UI tab — "2D Playback"
**Depends on:** the modular tab framework (`docs/ui/modular-ui-design.md`, authored separately).

> **Contract reconciled — read `docs/ui/modular-ui-design.md` §11 (Wave-1.5 addendum) first.**
> After cross-reading both docs, the shared module↔framework contract was settled. Three things in
> this doc are now **superseded** (kept below for the rationale, but bind to the framework instead):
> (1) the module does **not** own an `EntityTracker` or call `AdvanceToIndex` — it reads a host-joined
> `IPlaybackSnapshot.Players` (pawn + reconstructed world position + team) and resolves weapon handles
> via `IReadOnlyEntityView.ResolveHandle(...)` inside the `Advanced` callback; (2) **position** comes
> from `snapshot.Players[i].WorldPosition` (the host owns the cell→world reconstruction + the verified
> `WORLD_HALF_EXTENT` constant — §4.1's analysis is what the host implements); (3) **pawn↔slot** is the
> host's job (the §8 risk #6 `PawnLookup` concern is dissolved for this module). Wave 2B binds the §4/§10
> API references to the as-built framework signatures.
Field-availability claims in §4 were verified against a real demo
(`demos/pro-demos/vitality-vs-fut-m2-dust2.dem`) using `tools/EntityDecodeProbe`
(descriptor + schema dumps), the live entity-read APIs in
`src/Parser/DemoViewer.NET.Parser.EntityTracking/`, the generated typed wrappers under
`src/Parser/DemoViewer.NET.Entities/Generated/`, and the CS2 OpenDevDocs server/client schemas.

---

## 0. Scope and one-paragraph summary

A new tab whose centrepiece is a **2D top-down viewport** that animates every player's
position as the framework's playback clock advances. Players render as round markers
(team-coloured ring → Steam avatar later) whose **outline colour signals game events**
(shooting / taking damage / blinded / dead). Side panels show **per-player current state**
(weapon, grenades, HP/armour, cash, K/D/A) and **round-level game info** (round time,
score). Iteration 1 ships on a **plain grid background** with **auto-fit** zoom derived from
observed player positions and **histogram-based Z floor-splitting** — no map metadata, no
radar art, no avatars, no per-map constants. Those are clean pluggable swaps deferred to
later iterations.

This document is the requirements + design contract. **It writes no code and touches no
protected file** (`DemoParser.cs`, `DemoFrame.cs`, `BitBuffer.cs`, `LEB128Utils.cs`). One
finding (§4.1) implies new read-side derivation code in a *non-protected* location; that is
flagged, not actioned here.

---

## 1. Prior-art survey

Several open-source / web tools already do 2D CS demo playback. The most useful lessons:

### 1.1 boltobserv (`boltgolt/boltobserv`) — Python/Electron live observer radar
- **Auto-zoom / auto-pan:** the view "automatically pans and zooms according to where the
  players are located, and smoothly follows the action." This is exactly our iteration-1
  auto-fit requirement (FR-3), and validates doing it from live positions rather than a
  fixed map rectangle.
- **6 distinct player "dot states":** an observer can read "exactly what a player is doing"
  from the dot alone. This is the strongest prior-art justification for our **event-driven
  ring colours** (FR-9) — encode state in the marker, not in a side table.
- **Custom radar images** made with SimpleRadar/readtldr, with per-state overlays (e.g.
  buyzones only during buytime). Confirms radar art is a *later* asset-swap concern, separable
  from the position engine.
- **Runs in a browser / OBS browser source.** Reinforces a clean separation between the
  position model and the renderer.
- **Take:** emulate auto-zoom and the small-vocabulary dot-state language. Avoid coupling to
  bespoke radar art up front.

### 1.2 csgo-2d-demo-viewer (`sparkoo/...`) and splouisliu/csgo-demo-viewer — web 2D players
- Top-down map with **all player locations, grenade trajectories, kills**, plus playback
  controls (pause/rewind/speed/round-skip).
- **Take:** the standard feature envelope (positions + events + round navigation). Grenade
  trajectories and kill markers are good iteration-2+ overlays; out of scope for iteration 1.

### 1.3 awpy (`pnxenopoulos/awpy`) — Python parse + 2D plotting
- Plots a fixed per-player field set: **`X, Y, Z, health, armor, pitch, yaw, side, name`**.
  Players are rendered as **points** with `point_settings` carrying `hp`, `armor`,
  `direction=(pitch,yaw)`, and `label` (name); team colour comes from `side` (ct/t).
- Reads **map metadata (`pos_x`, `pos_y`, `scale`)** from a per-map overview `.txt` to map
  world→image (see §1.6).
- Z is captured but awpy does **not** auto-split floors — it relies on per-map knowledge.
- **Take:** awpy's field set is almost exactly our attributes panel (FR-10) and confirms
  **view angle (yaw)** is the supported heading source — *not velocity*. Its lack of
  floor-splitting is the gap we explicitly fill with a heuristic (FR-7).

### 1.4 healeycodes "Rendering CS Demos in the Browser"
- Reads "a slice of player data every ~200ms" (not every tick) and renders **SVG markers over
  a decompiled radar image**, React-driven, scrubbable slider.
- Each marker carries **team colour, a direction arrow, current weapon, bomb-carrier flag**;
  **when flashed the circle is shaded white and fades**; HP shown in non-map UI.
- **Take:** (a) decoupling sample rate from tick rate is a legitimate perf lever (informs
  NFR-1); (b) the **flash fade** animation is a concrete model for our `blinded` ring;
  (c) marker carries event/identity, panels carry numbers — matches our split.

### 1.5 CS Demo Manager 2D viewer / SimpleRadar / Leetify-style replays
- Polished commercial-grade 2D replays: smooth interpolation between sampled positions,
  per-round timeline, kill feed, utility overlays, multi-floor radars (Nuke/Vertigo ship a
  *lower* radar image keyed off a Z threshold).
- **SimpleRadar** is the de-facto community radar art set; the eventual map-asset model should
  be compatible with its image + overview-txt convention.
- **Take:** smooth interpolation and per-round timeline are desirable but **deferred**
  (iteration 1 snaps to tick). Multi-floor-via-Z-threshold is the *map-metadata* version of
  our heuristic floor-split — design the heuristic now, accept metadata later.

### 1.6 The standard CS radar coordinate convention (eventual map-metadata model)
Every CS map ships an overview `.txt` (`resource/overviews/<map>.txt`) with three numbers used
by awpy, demoinfocs-golang, CS Demo Manager, and SimpleRadar alike:

```
world_to_radar_pixel:
    px = (world_x - pos_x) / scale
    py = (pos_y - world_y) / scale      # note: Y is inverted (image Y grows downward)
```

- `pos_x`, `pos_y` = world coordinate of the radar image's **top-left** corner.
- `scale` = world units per radar pixel.
- Multi-floor maps add a `verticalsections` block (e.g. `default` / `lower`) each with its own
  image + a Z range. This is precisely the per-map metadata we defer (§7) — but adopting this
  exact triple as the future metadata schema means radar art drops in with no model change.

**Prior-art takeaways (top 3):**
1. **boltobserv's auto-zoom + small dot-state vocabulary** — both are iteration-1 requirements
   and proven to work from live positions alone.
2. **awpy/healeycodes confirm the data model**: position + yaw-heading + hp/armor/weapon/team +
   flash-state per marker; *velocity is never the heading source*.
3. **The `pos_x/pos_y/scale` overview convention** is the universal world→image map; adopt it
   verbatim as the deferred map-metadata schema so radar art is a drop-in swap.

---

## 2. Glossary
- **Pawn** — the in-world player entity (`CCSPlayerPawn`); carries position, health, weapons.
- **Controller** — the persistent player entity (`CCSPlayerController`); carries name, SteamID,
  cash, score, K/D/A. One controller ↔ one pawn at a time; the binding changes across deaths.
- **Player slot** — stable 0-based player index. `controllerIndex = slot + 1`.
- **Tick / frame index** — the playback clock surface; supplied by the framework (§6).
- **Ring** — the coloured outline around a player marker; its colour is the event channel.

---

## 3. Functional requirements

### Viewport, pan, zoom, auto-fit
- **FR-1 (2D viewport):** Render a top-down 2D viewport that plots one marker per live player
  at the current tick, refreshed as the framework playback clock advances.
- **FR-2 (pan & zoom):** Support mouse pan (drag) and zoom (wheel / pinch) with a sensible
  min/max zoom clamp; pan/zoom never desync world↔screen mapping (§5.1).
- **FR-3 (auto-fit):** On load, and on an explicit "Fit" action, auto-fit zoom+pan so the full
  extent of all observed player positions is visible with margin. Until positions are known,
  fall back to a **default fixed world rectangle** (configurable `DefaultWorldExtent`, e.g.
  `[-3000,3000]²`) so the grid renders immediately.
- **FR-4 (extent source):** Auto-fit extent is computed from **observed min/max player world
  positions** (a running bound over decoded ticks), *not* from any map constant — there is no
  map metadata (§4 constraints).

### Background
- **FR-5 (grid background):** Iteration 1 draws a plain scaled grid (major/minor lines) under
  the markers, labelled with world coordinates at gridline intersections (debug aid).
- **FR-6 (pluggable background):** The background is an injectable `IViewportBackground`
  abstraction. Iteration 1 provides `GridBackground`; a future `RadarImageBackground`
  (image + `pos_x/pos_y/scale`, §1.6) swaps in without touching marker/viewport code.

### Multi-floor (Z) handling
- **FR-7 (Z floor-split, heuristic):** For maps spanning multiple Z layers (Nuke, Vertigo),
  split observed Z values into floor "slices" using a **histogram / gap heuristic** over the
  Z values seen so far (§5.2) — *not* hardcoded per-map Z thresholds. Each detected floor
  renders as a **separate 2D section** within the same viewport (stacked vertically or as
  tabs/toggles), and a player is drawn in the slice its current Z falls into.
- **FR-8 (single-floor default):** When the histogram finds one cluster (the common case),
  render exactly one section — no empty/spurious floors.

### Player markers + event-driven ring colours
- **FR-9 (markers & ring colours):** Each live player renders as a round marker positioned from
  its **pawn** location. Iteration 1: a filled disc with **team-coloured fill** and a number
  /initials label. The **ring (outline) colour** encodes the player's current event state with
  this precedence (highest first):

  | State | Ring colour | Detection (per §4.2) |
  |---|---|---|
  | dead | grey / hollow | `m_lifeState != 0` or `m_iHealth <= 0` |
  | blinded | white (fading) | `m_flFlashDuration > 0` (alpha ∝ remaining) |
  | taking damage | red flash | `m_iHealth` decreased vs previous decoded tick |
  | shooting | yellow flash | `m_iShotsFired` increased vs previous decoded tick |
  | (default) | team colour | none of the above |

  Precedence resolves simultaneous states; "flash" states (shooting / taking-damage) decay over
  a short fixed window so a single-tick event remains visible across a render frame or two.
- **FR-9a (avatar swap):** The marker fill is an injectable `IPlayerMarkerVisual`. Iteration 1
  = team-colour disc + label. A future `AvatarMarkerVisual` (Steam profile picture inside the
  ring) swaps in with no change to positioning or ring logic. **Avatars are deferred** (§4.3).

### Attributes panel
- **FR-10 (per-player attributes):** A side panel lists each player's *current* state at the
  current tick: name, team, **HP, armour, helmet/defuser, active weapon, grenades/inventory,
  cash in reserve, K/D/A, current/round equipment value**. Sourced per §4. Updates with the
  clock; absent fields render as "—" (never crash on a missing field).

### Game-info panel
- **FR-11 (game info):** A panel shows round-level info: **round time remaining** (derived,
  §4.2), **team score** (per §4.4), round phase (freeze/live/over from `CCSGameRules`),
  bomb state, and current round number.

### Lifecycle / robustness
- **FR-12 (tick-driven only):** All marker/panel updates are driven by the framework clock's
  current-tick changes (§6). The module never advances time itself.
- **FR-13 (graceful empties):** Before a pawn exists, on spectator/unassigned slots, or when a
  field is unavailable, the module renders a placeholder and continues — never throws.

---

## 4. Feature → data-source availability table (the heart of the doc)

**Tiers:** **Live** = directly readable per tick from the EntityTracker entity snapshot.
**Derived** = computed from events and/or cross-tick field deltas (no single raw field).
**Deferred** = needs out-of-band work (network/assets/per-map constants); out of scope for
iteration 1, specced with a fallback.

How the module reads live state each tick (verified APIs):
- Seek: `EntityTracker.AdvanceToIndex(frameIndex, frames)` (frame-accurate; ticks repeat) or
  `AdvanceTo(targetTick, frames)`; read-only inspection without mutating live state via
  `PeekEntityUpdates(CSVCMsg_PacketEntities)`.
- Enumerate players: iterate `tracker.CurrentEntities.AllIndexed()` (or `.OfClass(...)`),
  filter pawns, map pawn→slot by decoding `m_hController` — **reuse
  `Analysis.Plugins.PawnLookup.ForEachLivePawn` / `ResolvePawn`** (the established, correct
  reverse-lookup; the forward `controller.m_hPawn` path is stale across deaths).
- Read a field: `entityState["<dotted.path>"]` (raw boxed, allocation-free), or
  `entityState.TryGet<T>(path)`, or the typed wrapper via `tracker.Get<CSPlayerPawn>(slot)`.
- Follow a handle: `PawnLookup.ResolveHandle(tracker, handleValue)` or
  `tracker.ResolveHandle<T>(handle)` (masks low 14 bits = entity index).
- **Wire-encoding gotchas (verified):** handles arrive as
  `UInt64`; bools as `Int32` (0/1); sub-services flatten under dotted parents
  (`m_pWeaponServices.m_hActiveWeapon`); arrays use `[N]` not `.NNN`. Always coerce handles via
  `PawnLookup.TryUnboxHandle`; never `is uint` / `is bool`.

### 4.1 Position — Derived, not Live (verified; the biggest correction in this doc)

**Empirical result.** Dumping the real `CCSPlayerPawn` descriptor tree
(`EntityDecodeProbe --descriptors`) shows **there is NO `m_vecOrigin` leaf on the pawn**. The
top-level pawn has 214 fields; field `[9] CBodyComponent` (43 children) holds position as
**cell coordinates + an in-cell offset**:

| Storage key (dotted path in `EntityState.Fields`) | Wire type | Meaning |
|---|---|---|
| `CBodyComponent.m_cellX` / `m_cellY` / `m_cellZ` | `uint16` | cell index per axis |
| `CBodyComponent.m_vecX` / `m_vecY` / `m_vecZ` | `CNetworkedQuantizedFloat` | in-cell offset, **range `[0, 1024]`**, 15-bit (per schema `CNetworkOriginCellCoordQuantizedVector`) |

So world position must be **reconstructed**:
```
world_axis = (m_cell* - 32) * CELL_WIDTH + m_vec*
```
- **`CELL_WIDTH = 512` world units** (corrected Wave 2B from the demofile-net oracle; the original
  schema-range inference was wrong — the cell multiplier is **512, not 1024**). The `[0,1024]`
  in-cell offset range is correct, but it does **not** imply the cell width: a 512-unit cell width
  coexists with a `[0,1024]` offset because the engine's cells overlap (see `PositionUtil` notes).
- `WORLD_HALF_EXTENT` centres the cell grid on the world origin and is now **pinned at
  `16384 = 32 * 512 = 1<<14`** — lifted+verified from the oracle's literal `(cell − 32) * 512` form
  (it independently matches the non-cell `CNetworkOriginQuantizedVector` range `[-16384, 16384]`).
  No back-solving needed; the constant is settled.

**Pitfall flagged:** the SchemaLens genesis maps `CCSPlayerPawn["m_vecOrigin"] → "Origin"`
(ObjectLane, slot 8) and the generated `CSPlayerPawn.Origin` getter reads that object slot.
**That slot is never written** for a real pawn (no `m_vecOrigin` field exists on it), so
`CSPlayerPawn.Origin` returns `null` at runtime. The 2D module must **not** rely on
`.Origin`; it must read the six `CBodyComponent.m_cell*/m_vec*` keys and reconstruct.

**Implementation note (RESOLVED Wave 2B — host-owned):** the cell→world reconstruction now lives
in the host, not the module. `src/App/DemoViewer.NET/Services/PositionUtil.cs`
(`PositionUtil.CellToWorld(pawn)`) does the math once per tick inside the host player-join; the
module **just reads `snapshot.Players[i].WorldPosition`** and never re-rolls the cell math (§11
binding). No protected parser file changes. (A nice-to-have future fix is a working typed
`Position` wrapper, an Entities/codegen change, still out of scope.)

| Feature | Source | Tier |
|---|---|---|
| Player X/Y (map plot) | reconstruct from `CBodyComponent.m_cellX,m_vecX` / `m_cellY,m_vecY` | **Derived** |
| Player Z (floor-split) | reconstruct from `CBodyComponent.m_cellZ,m_vecZ` | **Derived** |
| Heading / facing | `m_angEyeAngles` (pawn, `QAngle`) **yaw** — *not* velocity | **Live** |

### 4.2 Ring-colour states — DERIVED (event/delta), each source named

| Ring state | Concrete source | Per-tick detection |
|---|---|---|
| **dead** | pawn `m_lifeState` (`uint8`, top-level leaf, verified) and/or `m_iHealth` (`int32`) | `m_lifeState != 0` (0 == ALIVE) **or** `m_iHealth <= 0`. `m_lifeState` is seen-aware so 0 is a real "alive" value, not "absent". |
| **blinded** | pawn `m_flFlashDuration` (`CCSPlayerPawnBase`, FloatLane; verified in `SchemaLens.Generated.cs`) | `m_flFlashDuration > 0`; ring alpha ∝ remaining flash (mirrors healeycodes/boltobserv fade). Optionally also `m_flFlashMaxAlpha`. |
| **taking damage** | pawn `m_iHealth` cross-tick delta | `health(tick) < health(prevDecodedTick)` → red flash for a short decay window. Robust without an event subscription. |
| **shooting** | pawn `m_iShotsFired` (`int32`, top-level leaf, verified) cross-tick delta | `shotsFired(tick) > shotsFired(prev)` → yellow flash for a short decay window. **Preferred over the `weapon_fire` game event** — no event plumbing, reads from the same per-tick snapshot. |

- **Game-event alternative (optional, not required):** the parser also surfaces game events
  (`weapon_fire`, `player_hurt`, `player_death`) through the parser/analysis layer. The
  **entity-delta approach above is preferred** because it reads from the same per-tick entity
  snapshot the module already holds — no separate event-stream subscription, no event↔tick
  alignment. If a future iteration wants exact sub-tick event timing (e.g. precise shot
  origin), the game-event API is the upgrade path; tier those as Derived-via-events.
- **"Previous decoded tick" caveat:** deltas need the prior sample. The module keeps a tiny
  per-player ring-buffer of `(health, shotsFired)` from the last sampled tick. On a backward
  seek the deltas are reset (no false flashes) — see NFR-2.

### 4.3 Player identity & marker — mixed

| Feature | Source | Tier |
|---|---|---|
| Player name | controller `m_iszPlayerName` (verified) / `m_sSanitizedPlayerName` | **Live** |
| Team | pawn `m_iTeamNum` (verified) or controller `m_iTeamNum` | **Live** |
| SteamID (for avatar) | controller `m_steamID` (`uint64`, verified) | **Live** (the *id*) |
| **Steam avatar image** | Steam Web API lookup by SteamID + image fetch + cache + API-key mgmt | **Deferred** (§7). Iteration-1 marker = team-colour disc + number/initials (`IPlayerMarkerVisual`, FR-9a). |

### 4.4 Attributes panel — mostly Live (some one-hop)

| Feature | Source (verified path) | Tier |
|---|---|---|
| Health | pawn `m_iHealth` | **Live** |
| Armour | pawn `m_ArmorValue` | **Live** |
| Helmet / defuser | pawn `m_pItemServices.m_bHasHelmet` / `m_bHasDefuser` (bools as Int32) | **Live** |
| Cash in reserve | controller `m_pInGameMoneyServices.m_iAccount` (verified) | **Live** |
| Kills / deaths / assists | controller — round kills `m_pActionTrackingServices.m_iNumRoundKills`; **match-total K/D/A is Derived** (see note) | **Live (round) / Derived (match totals)** |
| Equipment value | pawn `m_unCurrentEquipmentValue` / `m_unRoundStartEquipmentValue` (verified) | **Live** |
| Score (per-player) | controller `m_iScore` (verified) | **Live** |
| **Active weapon (display name)** | pawn `m_pWeaponServices.m_hActiveWeapon` (handle, UInt64) → `ResolveHandle` → weapon entity class/item-def → display name | **Live, one-hop** (resolve handle → look up class/`m_iItemDefinitionIndex` → name) |
| **Grenade/weapon inventory** | pawn `m_pWeaponServices.m_hMyWeapons[N]` (handle array, `[N]` indexing) → resolve each → class/item-def | **Live, one-hop** (iterate `[0..N]`, resolve, classify nade vs gun) |

- **K/D/A note:** the controller exposes *round* kills/HS directly; full **match** K/D/A is not
  a single networked field. Use `m_iScore` and round-kill fields for a live-enough display, or
  derive cumulative K/D/A from the analysis layer's existing per-player stats if the framework
  exposes them. Tier the match-total flavour as Derived to avoid over-promising a raw field.

### 4.5 Game-info panel — mixed

| Feature | Source | Tier |
|---|---|---|
| Round phase (freeze/live) | `CCSGameRules` `m_bFreezePeriod` / `m_bWarmupPeriod` / `m_bBombPlanted` (verified in genesis) | **Live** |
| Round start time | `CCSGameRules` `m_fRoundStartTime` (FloatLane, verified) | **Live** |
| **Round time remaining** | `m_fRoundStartTime` + round-length − now | **Derived.** Round length is a **convar, not networked** (assume default 115s, or read `mp_roundtime*` if the demo carries it). Document the assumption; if a planted bomb, switch to bomb timer. |
| **Team score (T/CT round wins)** | NOT controller `m_iScore` (that's per-player). Likely `CCSTeam.m_iScore` / score on the team entity | **Live (verify path).** Must confirm the team-entity field before relying on it (see §8 risk). |
| Bomb state | `CCSGameRules` `m_bBombPlanted` / `m_bBombDefused` / `m_bBombDropped` (verified) | **Live** |

### 4.6 Map metadata (radar art, scale, floor thresholds) — DEFERRED

| Feature | Source | Tier |
|---|---|---|
| Radar image | per-map asset (SimpleRadar/decompiled) | **Deferred** → grid fallback (FR-5/6) |
| world→pixel `pos_x/pos_y/scale` | per-map overview `.txt` (§1.6) | **Deferred** → auto-fit from observed extent (FR-3/4) |
| Per-map floor Z thresholds | per-map metadata | **Deferred** → Z histogram heuristic (FR-7) |

---

## 5. Rendering & interaction design

### 5.1 World→screen mapping (auto-fit math)
A single affine transform, recomputed only on fit / pan / zoom (not per tick):
```
scale  = min(viewW / (worldMaxX - worldMinX),
             viewH / (worldMaxY - worldMinY)) * (1 - margin)    # uniform; preserves aspect
screenX = (worldX - worldCenterX) * scale + viewW/2 + panX
screenY = (worldCenterY - worldY) * scale + viewH/2 + panY      # Y inverted: world up = screen up
```
- `worldMin/Max` come from the running observed-extent bound (FR-4), or `DefaultWorldExtent`
  until populated.
- Zoom multiplies `scale` about the cursor; pan adds `panX/panY`. Inverse transform
  (screen→world) supports hit-testing a marker.
- When the radar background lands later, the same transform composes with the
  `pos_x/pos_y/scale` triple — the marker layer is unchanged.

### 5.2 Z floor-split heuristic (FR-7)
1. Accumulate observed player Z values (cheap running histogram, fixed bucket width e.g. 64u).
2. Find clusters separated by an empty-bucket **gap ≥ G** (e.g. ≥ ~180u, a player can't span
   floors within one tick). Each cluster = one floor slice, ordered by Z (low→high).
3. Assign each player to the slice containing its current Z; render slices as stacked sections
   (or toggled layers) sharing the X/Y transform.
4. **Single cluster ⇒ one section** (FR-8). Hysteresis on slice boundaries prevents a player on
   a ramp from flickering between floors.
5. This is intentionally metadata-free; when per-map Z thresholds arrive they simply **replace**
   the heuristic boundaries (same slice abstraction).

### 5.3 Marker / ring visual language
```
        ╭─────╮        ring  = event state (FR-9 precedence): dead·blind·hurt·shoot·team
       (   5   )       fill  = team colour (T=amber, CT=blue); label = number/initials
        ╰──┬──╯        stub  = heading from m_angEyeAngles yaw (NOT velocity)
           │  (heading)
```
- Iteration-1 fill = flat team colour; later swap to avatar (`IPlayerMarkerVisual`).
- Flash states (shoot/hurt) decay over ~200–300 ms so a 1-tick event survives a render frame.
- Dead markers go hollow/grey and optionally drop to a "deaths" gutter.

### 5.4 Panel layout (ASCII mock)
```
┌──────────────────────────────────────────────┬───────────────────────────┐
│  2D VIEWPORT (FR-1)                           │  GAME INFO (FR-11)        │
│  ┌── floor: upper (FR-7) ──────────────────┐  │  Round 14   CT 8 : 5 T    │
│  │   grid (FR-5)   • markers (FR-9)         │  │  ⏱ 1:12   ● bomb planted  │
│  │        (3)        (1)                    │  ├───────────────────────────┤
│  │              (5)→        (2)             │  │  ATTRIBUTES (FR-10)       │
│  └─────────────────────────────────────────┘  │  ┌─ player 5 ───────────┐ │
│  ┌── floor: lower (FR-7) ──────────────────┐  │  │ HP 78  AR 100  ◈helmet│ │
│  │              (4)                         │  │  │ AK-47  $2400          │ │
│  └─────────────────────────────────────────┘  │  │ HE,Smoke  K/D/A 12/7/3│ │
│  [Fit] [+]/[–] zoom   pan: drag               │  └───────────────────────┘ │
└──────────────────────────────────────────────┴───────────────────────────┘
(playback transport — play/pause/seek — is the FRAMEWORK's, shared across tabs; §6)
```

### 5.5 Render-frame coalescing
Tick changes mark the module "dirty"; an actual redraw happens once per render frame (or on a
throttled timer), coalescing multiple tick updates between frames (NFR-1). The world transform
is cached and only recomputed on fit/pan/zoom.

---

## 6. Playback dependency (framework-owned)

**The module does not implement playback.** It consumes the framework's playback clock /
current-tick surface defined in `docs/ui/modular-ui-design.md`:
- Play / pause / seek / speed are **framework controls**, shared across tabs.
- The module **subscribes** to "current tick/frame changed" and reacts by:
  (a) seeking its `EntityTracker` to that frame (`AdvanceToIndex`), (b) reading live state,
  (c) marking the viewport dirty.
- **Backward seeks** invalidate the per-player delta history (§4.2) to avoid false flash rings.
- **Open dependency:** whether each tab owns its own `EntityTracker` or shares one tracker
  instance is a **framework decision** — this module is written against whatever
  current-tick + entity-snapshot surface the framework exposes. We **defer the mechanics**
  (tab registration, context injection, the clock interface shape) to the framework doc and
  design only against its contract. If the framework hands the active tab an already-seeked
  tracker/snapshot, the module skips its own `AdvanceToIndex`.
- **NFR alignment:** the framework's "only the active tab does per-tick work; updates coalesced
  to render frame" model (NFR-1) is assumed; the module honours it by being inert when not the
  active tab.

---

## 7. Iteration plan

### Iteration 1 — "mostly working" (this spec's target)
- Grid background (FR-5/6) + pan/zoom + auto-fit from observed extent (FR-1..4).
- Position **reconstructed from cell+offset** (§4.1) — the load-bearing first task; confirm the
  `WORLD_HALF_EXTENT` constant against a decoded value before trusting the plot.
- Markers = team-colour disc + number + heading stub (FR-9), event-driven ring via
  `m_lifeState` / `m_flFlashDuration` / `m_iHealth`-delta / `m_iShotsFired`-delta (§4.2).
- Z floor-split via histogram heuristic (FR-7/8).
- Attributes panel: HP/armour/helmet/defuser/weapon(one-hop)/cash/round-kills/equip (§4.4).
- Game-info panel: round phase, round time (derived w/ assumed length), bomb state, score
  (after verifying team-score path) (§4.5).
- Snaps to tick (no interpolation); reacts to framework clock (§6).

### Deferred (later iterations, all designed as clean swaps)
- **Steam avatars** (`IPlayerMarkerVisual` → Steam Web API + image cache + key mgmt) — §4.3.
- **Real radar art** (`IViewportBackground` → image + `pos_x/pos_y/scale`) — §1.6 / §4.6.
- **Per-map Z thresholds & vertical sections** replacing the histogram heuristic — §5.2.
- **Smooth position interpolation** between sampled ticks (sub-tick easing) — §1.5.
- **Overlays:** grenade trajectories, kill feed, smoke/molotov areas, bomb timer ring.
- **Exact game-event timing** via `weapon_fire`/`player_hurt`/`player_death` if sub-tick
  precision is needed (upgrade from the delta approach) — §4.2.
- **Working typed `Position` wrapper** (Entities/codegen change) so consumers stop hand-rolling
  the cell reconstruction.

---

## 8. Open questions / risks

1. **[RESOLVED Wave 2B] World-extent constant for position reconstruction (§4.1).** The earlier
   claim "`CELL_WIDTH=1024` confirmed from the schema" was **wrong** — the `[0,1024]` offset range
   does not pin the cell width. The demofile-net oracle settles it deterministically:
   `world = (cell − 32) * 512 + offset`, so **`CELL_WIDTH = 512`** and
   **`WORLD_HALF_EXTENT = 32*512 = 16384 = 1<<14`**. Both are now oracle-pinned in
   `PositionUtil` and gate-tested against off-centre decoded pawns; no back-solving needed and
   positions are no longer suspect. (Risk closed.)
2. **`m_vecOrigin`/`.Origin` is a phantom (§4.1).** The genesis lens rule + generated
   `CSPlayerPawn.Origin` getter exist but read an unwritten slot → `null`. Anyone (including a
   future contributor) who reaches for `.Origin` will get a silently-broken plot. Documented
   here; consider a follow-up to either fix the wrapper or remove the misleading rule.
3. **Team score field path (§4.5).** Controller `m_iScore` is **per-player**, not the T/CT round
   score the game-info panel needs. The team score likely lives on a `CCSTeam` entity
   (`m_iScore`) — **verify with the probe before wiring** the scoreboard.
4. **Round length is a convar, not networked (§4.5).** Round-time-remaining assumes a default
   (115s) unless `mp_roundtime`/bomb timer is available. Acceptable for v1; note the assumption
   in the UI (e.g. tooltip).
5. **Delta-state correctness across seeks (§4.2).** Health/shots deltas depend on a prior
   sample. Forward play is fine; **backward seeks and round resets must clear the delta cache**
   or markers will flash spuriously.
6. **Pawn↔slot binding churn.** Across deaths the pawn entity is recreated and the
   controller↔pawn handle rebinds. Use `PawnLookup`'s reverse `m_hController` lookup (not
   `controller.m_hPawn`) — already the project's verified pattern; flagged so the module reuses
   it rather than re-deriving.
7. **Per-tab vs shared `EntityTracker` (§6).** Cost and correctness of seeking depend on whether
   the framework shares a tracker. A per-tab tracker re-seek on every clock tick could be
   expensive on big demos; needs the framework's clock/snapshot contract to resolve. Deferred to
   the framework doc.
8. **Heading without velocity.** `m_vecVelocity` is **not usably networked in GOTV** (reads 0) —
   confirmed constraint. Heading uses `m_angEyeAngles` yaw; if a future need wants movement
   direction (vs facing), derive it from **position delta between ticks**, never velocity.
9. **Spectators / coaches / unassigned slots.** Some controllers have no live pawn
   (`m_iTeamNum` spectator). The module must skip them (FR-13), not render phantom markers.

---

## 9. Non-functional requirements

- **NFR-1 (bounded per-tick cost):** Per-tick work is O(players) (~10), not O(entities)
  (~250) or O(16384 slots). Use `CurrentEntities.AllIndexed()` (walks ~250 live, not 16k) and
  read fields via the **allocation-free indexer** `entityState["path"]` — *never*
  `EntityState.Fields` in a per-player loop (it rebuilds a full dict per entity — profiling showed
  it as the dominant entity-tracking allocation). Redraws coalesce
  to the render frame (§5.5). Only the active tab does per-tick work (framework model).
- **NFR-2 (seek correctness):** Backward seeks/round resets reset delta-derived state (§4.2).
  The displayed frame always matches the framework clock's current tick.
- **NFR-3 (no crashes on missing data):** Every field read tolerates absence (seen-aware
  reads / `TryGet`); missing → placeholder, never an exception (FR-13).
- **NFR-4 (extensibility):** Background, marker visual, and floor-boundary source are injectable
  abstractions so radar art, avatars, and per-map Z metadata drop in without touching the
  viewport/marker core.
- **NFR-5 (no protected-file changes):** The module reads only through existing public APIs
  (`EntityTracker`, `EntityState`, `EntitySet`, typed wrappers, `PawnLookup`). The one new piece
  of logic (cell→world reconstruction, §4.1) lives in a **non-protected** helper. No change to
  `DemoParser.cs` / `DemoFrame.cs` / `BitBuffer.cs` / `LEB128Utils.cs`.
- **NFR-6 (determinism):** Given the same demo + tick, the viewport state is reproducible (pure
  function of the entity snapshot + delta cache); no wall-clock-dependent positioning.

---

## 10. Verified-API quick reference (for the implementer)

- Seek: `EntityTracker.AdvanceToIndex(frameIndex, frames)` / `AdvanceTo(tick, frames)`.
- Live read-only peek: `EntityTracker.PeekEntityUpdates(CSVCMsg_PacketEntities)`.
- Enumerate live entities: `tracker.CurrentEntities.AllIndexed()` / `.OfClass(name)` /
  `.AllInPvs()`.
- Per-pawn walk + slot mapping: `Analysis.Plugins.PawnLookup.ForEachLivePawn(tracker, (slot, pawn)=>…)`
  and `ResolvePawn(tracker, slot)`.
- Field read: `entityState["m_iHealth"]` (boxed), `entityState.TryGet<int>("m_iHealth")`,
  typed `tracker.Get<CSPlayerPawn>(slot)` / `tracker.Snapshot<CSPlayerPawn>(slot)`.
- Handle follow: `PawnLookup.ResolveHandle(tracker, handleValue)` /
  `tracker.ResolveHandle<T>(handle)`; unbox via `PawnLookup.TryUnboxHandle(value)`.
- Field metadata (wire type): `tracker.GetFieldMeta(className, path)?.TypeName`.
- **Position (Wave 2B: host-owned — DON'T reconstruct in the module):** read
  `snapshot.Players[i].WorldPosition` (`(float X, float Y, float Z)?`). The host's
  `PositionUtil.CellToWorld` does `world = (cell − 32) * 512 + offset` with
  **`CELL_WIDTH = 512`, `WORLD_HALF_EXTENT = 16384 = 1<<14`** (oracle-pinned; the prior
  "`CELL_WIDTH = 1024` confirmed" was **wrong** — corrected Wave 2B). Underlying keys (FYI only):
  `CBodyComponent.m_cellX/Y/Z` (uint16) + `CBodyComponent.m_vecX/Y/Z` (quantized float, in-cell
  offset `[0,1024]`).
- **Do NOT use** `CSPlayerPawn.Origin` / `m_vecOrigin` — phantom, returns null (§4.1).

---

## 11. As-built implementation binding (Wave 2B)

> **What this is.** A doc-only binding of this spec to the **as-built** modular framework on
> `feature/modular-ui-framework` (the reconciled contract of `docs/ui/modular-ui-design.md` §11, now
> implemented — see its §12 log). The signatures below are read off the shipped code, not the design
> draft; where they differ, the **as-built code wins**. No code is changed by this pass. The Wave-3
> implementer writes the 2D module against exactly this surface.
>
> **The three surfaces, by lifetime (memorise this split):**
> - **`IModuleContext` (stable, pull):** identity roster `Players` (slot/steamID/name, **no team**),
>   clock getters, `Request*` ops, `Entities` view, `CurrentPlayers` (on-activation resync),
>   `Analysis`. Handed once at `OnActivated`.
> - **`IPlaybackSnapshot` (transient, push):** delivered on each coalesced render frame via the
>   `Advanced` event while the tab is active. Carries `Players` (host-joined `PlayerState`),
>   `Entities`, `FrameEvents`, `FrameIndex`/`Tick`.
> - **`PlayerState` / `IReadOnlyEntity` / `IReadOnlyEntityView` are TRANSIENT and POOLED** — the host
>   re-aims the same ~10 `PlayerState` instances and the shared entity facades each push (the §12
>   POOLED decision). **Copy out the scalars you need inside the callback; never retain a
>   `PlayerState`, snapshot, or `IReadOnlyEntity` across pushes** — and (see §11.4) not even two
>   resolved entities within a single push.

### 11.1 Requirement → as-built API map

All per-player reads go through the **pooled** `PlayerState` (`snapshot.Players[i]` during a push, or
`context.CurrentPlayers[i]` on activation). `p.Pawn` / `p.Controller` are `IReadOnlyEntity?`; read
fields via the **allocation-free indexer** `p.Pawn?["m_iHealth"]` or `p.Pawn?.TryGet<T>(path, out v)`.
Identity (name/SteamID) comes from `context.Players` joined by `Slot`.

| Req / §4 row | As-built call (verify against the file) | Surface · lifetime |
|---|---|---|
| **FR-1 / FR-4 — position, extent** | `snapshot.Players[i].WorldPosition` → `(float X, float Y, float Z)?`. Host-reconstructed by `PositionUtil.CellToWorld`; **module never reads cell fields**. Running min/max over X/Y per push feeds auto-fit. | snapshot · transient |
| **FR-9 — heading / facing** | `p.Pawn?["m_angEyeAngles"]` returns a **boxed `System.Numerics.Vector3`** (pitch=`.X`, **yaw=`.Y`**, roll=`.Z`); or `p.Pawn.TryGet<Vector3>("m_angEyeAngles", out var a)` then `a.Y`. NOT velocity (`m_vecVelocity` reads 0 in GOTV). `Vector3` is a BCL type, so the abstractions assembly carries it with no Parser dependency. | snapshot · transient |
| **FR-9 ring: dead** | `p.Pawn?.TryGet<int>("m_lifeState", out var ls)` → `ls != 0`; or `m_iHealth <= 0` (`p.Pawn?["m_iHealth"]`). | snapshot · transient |
| **FR-9 ring: blinded** | `p.Pawn?.TryGet<float>("m_flFlashDuration", out var f)` → `f > 0`; alpha ∝ remaining. | snapshot · transient |
| **FR-9 ring: taking damage** | cross-tick delta of `m_iHealth` vs the module's own per-player last sample (decreased → red flash). | snapshot · transient (delta cache module-owned) |
| **FR-9 ring: shooting** | cross-tick delta of `m_iShotsFired` (`p.Pawn?["m_iShotsFired"]`, int) increased → yellow flash. | snapshot · transient |
| **FR-10 / §4.3 — name** | `context.Players.First(r => r.Slot == p.Slot).Name` (`PlayerRosterEntry.Name`). | context · stable |
| **FR-10 / §4.3 — team** | `p.Team` (volatile, on `PlayerState` — **not** on the roster). | snapshot · transient |
| **FR-10 — HP / armour** | `p.Pawn?["m_iHealth"]` / `p.Pawn?["m_ArmorValue"]`. | snapshot · transient |
| **FR-10 — helmet / defuser** | `p.Pawn?["m_pItemServices.m_bHasHelmet"]` / `m_bHasDefuser` (bools arrive as Int32 0/1 — read via `TryGet<int>` or compare to 0). | snapshot · transient |
| **FR-10 — cash in reserve** | `p.Controller?["m_pInGameMoneyServices.m_iAccount"]`. | snapshot · transient |
| **FR-10 — round kills / score** | `p.Controller?["m_pActionTrackingServices.m_iNumRoundKills"]`, `p.Controller?["m_iScore"]`. (Match-total K/D/A also networked — `m_pActionTrackingServices.m_iKills/m_iDeaths/m_iAssists`; see §11.5.) | snapshot · transient |
| **FR-10 — equipment value** | `p.Pawn?["m_unCurrentEquipmentValue"]` / `m_unRoundStartEquipmentValue`. | snapshot · transient |
| **FR-10 — active weapon (name)** | `p.Pawn?.TryGet<ulong>("m_pWeaponServices.m_hActiveWeapon", out var h)` → `snapshot.Entities.ResolveHandle(h)` → read `.ClassName` and/or `["m_iItemDefinitionIndex"]` **immediately** (see §11.4 clobber rule). | snapshot · transient (one-hop) |
| **FR-10 — grenade/weapon inventory** | iterate `p.Pawn?["m_pWeaponServices.m_hMyWeapons[N]"]` for `N = 0..`, `TryGet<ulong>` each → `ResolveHandle` → read class/item-def **before the next resolve** (clobber rule). | snapshot · transient (one-hop ×N) |
| **FR-3.3 / §4.3 — SteamID (avatar later)** | `context.Players.First(r => r.Slot == p.Slot).SteamId`. | context · stable |
| **FR-11 — round phase / freeze / warmup / bomb** | `snapshot.Entities.OfClass("CCSGameRulesProxy").FirstOrDefault()` then the **`m_pGameRules.`-prefixed** keys: `["m_pGameRules.m_bFreezePeriod"]` / `["m_pGameRules.m_bWarmupPeriod"]` / `["m_pGameRules.m_bBombPlanted"]` / `["m_pGameRules.m_bBombDefused"]` / `["m_pGameRules.m_bBombDropped"]`. **Live class is `CCSGameRulesProxy`, NOT `CCSGameRules`** — the rules object is networked as the `m_pGameRules` sub-object on the proxy entity (verified against `Analysis.Plugins.FreezePeriodProvider`). | snapshot · transient (once/frame, NOT per-player) |
| **FR-11 — round start time / round number** | same `CCSGameRulesProxy` entity: `["m_pGameRules.m_fRoundStartTime"]`, `["m_pGameRules.m_totalRoundsPlayed"]` (round number). Round-time-remaining is Derived (assumed `mp_roundtime` 115 s; see §4.5). | snapshot · transient (once/frame) |
| **FR-11 — team score (T/CT round wins)** | `snapshot.Entities.OfClass("CCSTeam")` (class name + score field — likely `m_iScore` on the team entity — are **impl-time verification** per §8 risk #3; not in the curated genesis lens, so confirm with the entity probe before wiring). **Not** a framework gap — `OfClass` + the indexer can reach whatever the live class/field turns out to be; only the exact strings need pinning. | snapshot · transient (once/frame) |
| **FR-12 — tick driver** | subscribe `context.Advanced`; react in the handler. Never advance time. | context event |
| **FR-13 — skip non-live slots** | `if (!p.HasLivePawn) continue;` (`p.Pawn`/`WorldPosition` are null for spectators/pre-spawn). | snapshot · transient |
| **FR-2 — clock control (optional)** | `context.RequestSeekToFrame/RequestSeekToTick/RequestPlay/RequestPause` — capability-gated `Playback.Control` (granted, §11.2). | context · stable |

**Lifetime rule restated:** every value above is read **inside** the `Advanced` callback (or the
`OnActivated` resync) and **copied to a scalar/VM field**. The `PlayerState`, the snapshot, and any
`IReadOnlyEntity` from `ResolveHandle`/`OfClass`/`Pawn` are invalid the instant the callback returns.

### 11.2 Module skeleton binding (the concrete shape Wave 3 writes)

Mirror the **VM-lifecycle** pattern from `PlaceholderTabViewModel`, but the **descriptor wiring**
from the `ViewModelFactory` branch (NOT `PlaceholderModule`'s `DataContext = vm` line — that branch
leaves `TabViewModel` null, so the descriptor's `Activate()` never calls `OnActivated`; the placeholder's
PM-1 test drives its VM directly). For the 2D module the `OnActivated`-subscribe / `OnDeactivated`-
unsubscribe lifecycle **must** run, so the VM must be the descriptor's `TabViewModel` — set
`ViewModelFactory` and leave `DataContext` null (`Activate()` then sets the View's DataContext to
`DataContext ?? TabViewModel` = the VM, and the inactive-unload `DataContext is null` branch applies).

```csharp
// 1) The module — contributes one Main-strip tab.
public sealed class Playback2DModule : IWorkspaceModule
{
    public string  Id => "net.demoviewer.playback2d";
    public string  DisplayName => "2D Playback";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        // host.HasCapability("Playback.Control") is true for first-party (FirstPartyCapabilities).
        yield return new WorkspaceTabDescriptor
        {
            TabId    = "playback2d.viewport",
            Header   = "2D Playback",
            Order    = 4,                         // after the four built-ins (0..3)
            Placement = TabPlacement.Main,
            // ViewModelFactory (LAZY + RETAINED) — NOT DataContext. This is what makes
            // Activate() build + drive the VM's OnActivated/OnDeactivated lifecycle.
            ViewModelFactory = () => new Playback2DTabViewModel(/* inject roster source if needed */),
            ViewFactory      = () => new Playback2DView(),   // DataContext defaults to TabViewModel
        };
    }
}

// 2) The per-tab VM — zero per-tick work while inactive (PM-1).
public sealed partial class Playback2DTabViewModel : ObservableObject, IWorkspaceTabViewModel
{
    private IModuleContext? _context;

    public void OnActivated(IModuleContext context)
    {
        _context = context;
        context.Advanced += OnAdvanced;              // subscribe HERE — only the active tab
        // On-activation resync so a tab activated mid-playback is correct immediately:
        foreach (PlayerState p in context.CurrentPlayers) { /* copy scalars into VM */ }
    }

    public void OnDeactivated()
    {
        if (_context is not null) { _context.Advanced -= OnAdvanced; _context = null; }
        // After this returns the module does ZERO per-tick work (PM-1).
    }

    private void OnAdvanced(IPlaybackSnapshot s)
    {
        // Copy out scalars for each live player; read game-info from OfClass("CCSGameRulesProxy") ONCE.
        // Mark the viewport dirty; redraw coalesces to the render frame (§5.5). Reset the
        // health/shots delta cache on a BACKWARD seek (s.FrameIndex < last) per NFR-2.
    }
}
```

- **Registration:** add `registry.Register(new Playback2DModule());` to `BuildRegistry()` in
  `src/App/DemoViewer.NET/App.axaml.cs` (today it returns a bare `ModuleRegistry`; `BuiltInTabsModule`
  is auto-registered by `MainViewModel.BuildWorkspaceTabs`, so only the 2D module is added here).
  Both desktop and browser hosts use this path.
- **Capability:** `Playback.Control` is already in `ModuleHost.FirstPartyCapabilities`, and
  `MainViewModel.HostCapabilitiesFor` grants all of them to every (first-party) module — so the
  optional clock-control `Request*` calls work with no extra wiring.
- **View/DataContext convention:** the realized `Playback2DView`'s DataContext is the descriptor's
  `TabViewModel` (because `DataContext` is left null) — so XAML binds `{Binding SomeVmProperty}`
  directly against the VM. The View is built on activation and dropped on deactivation
  (inactive-content-unload invariant); the VM is retained across activations for state.

### 11.3 Constant correction (CELL_WIDTH 1024 → 512)

The as-built `PositionUtil` (`src/App/DemoViewer.NET/Services/PositionUtil.cs`) uses
**`CellWidth = 512`** and **`WorldHalfExtent = 32 * 512 = 16384 = 1<<14`**, with
`world = (cell − 32) * 512 + offset` — lifted+verified from the demofile-net oracle
(`CNetworkOriginCellCoordQuantizedVector`). This document's earlier §4.1 / §8 risk #1 / §10 text
asserting "`CELL_WIDTH = 1024` confirmed" was **wrong** and has been corrected inline above
(the `[0,1024]` figure is the in-cell *offset* range, which is correct and left untouched — it does
not pin the cell width; the engine's cells overlap). Position reconstruction is **host-owned** now
(`PositionUtil` + the per-tick player-join in `ModuleContext.RebuildPlayerJoin`), so the module just
reads `snapshot.Players[i].WorldPosition` and re-rolls nothing.

### 11.4 Pooling / aliasing hazards the implementer must respect

- **`ResolveHandle` / `ByIndex` / `BySerial` all return the SAME shared pooled `_scratch` facade**
  (`ReadOnlyEntityView`). Two sequential resolves **clobber** the first. The FR-10 active-weapon +
  `m_hMyWeapons[N]` loop walks straight into this — read the scalar you need (`.ClassName`,
  `["m_iItemDefinitionIndex"]`) **immediately after each resolve, before the next resolve**. You
  cannot hold two resolved entities even within one callback.
- **`OfClass(...)` / `All()` allocate a fresh facade per element** (the §12 carryover). They're fine
  for once-per-frame game-info reads (`CCSGameRulesProxy`, `CCSTeam`) but **never** put them in the
  per-player hot loop — per-player state comes through the pooled `PlayerState.Pawn`/`Controller`
  and `ResolveHandle` (NFR-1: O(players), not O(entities); never `EntityState.Fields`).
- **No `PawnLookup` / `TryUnboxHandle` on the module surface** (the abstractions assembly can't
  reference Analysis). Read handles as `ulong` (`TryGet<ulong>(...)`) and pass straight to
  `Entities.ResolveHandle(...)`; the host already did the `m_hController` pawn↔slot join.

### 11.5 Residual-gap check (per FR)

| FR | As-built satisfies? | Notes |
|---|---|---|
| FR-1 viewport | Yes | `snapshot.Players[i].WorldPosition`. |
| FR-2 pan/zoom | Yes (module-internal) | No framework dependency; optional clock control via `Playback.Control` (granted). |
| FR-3 auto-fit | Yes | Running min/max over `WorldPosition` per push. |
| FR-4 extent source | Yes | Observed positions only; no map constant needed. |
| FR-5 / FR-6 background | Yes (module-internal) | Grid + injectable `IViewportBackground`; no framework dependency. |
| FR-7 / FR-8 Z floor-split | Yes | Z from `WorldPosition.Z`; histogram is module-side. |
| FR-9 markers + ring colours | Yes | dead/blinded/health-delta/shots-delta all readable off `Pawn`; heading = `m_angEyeAngles` boxed `Vector3` yaw (`.Y`). Delta cache + backward-seek reset is module-owned (NFR-2). |
| FR-9a avatar swap | Yes (deferred) | SteamID available now (`PlayerRosterEntry.SteamId`); image fetch is deferred per §4.3. |
| FR-10 attributes | Yes | HP/armour/helmet/defuser/cash/round-kills/score/equip/active-weapon/inventory all bind cleanly — and **match-total K/D/A binds too**: it turned out to be networked on the controller (`m_pActionTrackingServices.m_iKills/m_iDeaths/m_iAssists`), read directly in the shipped module. See below. |
| FR-11 game info | Yes | round phase/time/bomb/round-number via `OfClass("CCSGameRulesProxy")` + `m_pGameRules.`-prefixed keys (verified against `FreezePeriodProvider`); team score via `OfClass("CCSTeam")` (class name + score field are impl-time verification, §8 risk #3 — reachable, so not a framework gap). |
| FR-12 tick-driven | Yes | `context.Advanced`. |
| FR-13 graceful empties | Yes | `PlayerState.HasLivePawn` / null `Pawn` / null `WorldPosition`; every read is null-tolerant. |

**Former residual GAP — match-total K/D/A (FR-10) — CLOSED, resolved differently than predicted.**
This section originally assumed match totals were *not* a networked field and would need module-side
accumulation from `FrameEvents`. In the shipped module they are read **directly**: the controller's
`m_pActionTrackingServices` exposes `m_iKills` / `m_iDeaths` / `m_iAssists` (and `m_iDamage` for ADR)
alongside the round-level `m_iNumRoundKills` (`Playback2DTabViewModel.cs:1178`). No module-side
bookkeeping was needed; the framework contract was sufficient as-is.

No other gaps: team score, heading, position, weapons, identity, game-info, and the full lifecycle
are all met by the as-built surface exactly as mapped in §11.1.
