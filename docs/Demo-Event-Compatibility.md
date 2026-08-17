# CS2 Demo Event Compatibility

## Overview

CS2 demos come from different sources with significantly different event sets. This document
catalogs the differences discovered through analysis and their impact on the analysis engine.

**Demos analyzed:**
- **Matchmaking (MM):** `003802730901763260580_1218921269.dem` (276 MB, de_mirage, 18 rounds)
- **HLTV/Pro:** `furia-vs-vitality-m1-mirage.dem` (706 MB, de_mirage, 24 rounds)

---

## Event Presence Comparison

### Round Lifecycle Events

| Event | MM Demo | HLTV Demo | Impact |
|-------|---------|-----------|--------|
| `round_freeze_end` | 18 | 24 | Round start signal — present in both |
| `round_officially_ended` | **17** | **0** | Round end signal — **MM only** |
| `round_end` | **0** | **0** | Not present in either demo type |
| `round_start` | **0** | **0** | Not present in either demo type |
| `round_prestart` | 18 | 0 | MM only |
| `round_poststart` | 18 | 0 | MM only |
| `cs_round_start_beep` | 54 | 72 | Present in both (3 beeps per round) |
| `cs_round_final_beep` | 18 | 24 | Present in both |
| `buytime_ended` | 18 | 0 | MM only |
| `begin_new_match` | 1 | 1 | Present in both |
| `cs_pre_restart` | 18 | 24 | Present in both |
| `announce_phase_end` | 1 | 2 | Present in both |
| `cs_win_panel_match` | 1 | 1 | Present in both |
| `halftime` | 0 | 0 | Not observed |
| `cs_intermission` | 0 | 1 | HLTV only |

**Critical finding:** `round_end` does not appear in EITHER demo type. `round_officially_ended`
appears only in matchmaking demos. HLTV demos have no explicit round-end game event.

### Player Events

| Event | MM Demo | HLTV Demo | Notes |
|-------|---------|-----------|-------|
| `player_death` | 130 | 173 | Both — primary kill tracking |
| `player_hurt` | 483 | 657 | Both — damage tracking |
| `player_blind` | 96 | 0 | **MM only** — flash tracking unavailable on HLTV |
| `player_spawn` | 180 | 250 | Both |
| `player_team` | 10 | 30 | Both |
| `player_connect` | 0 | 0 | Not observed (may use player_team for discovery) |
| `player_disconnect` | 5 | 1 | Both |
| `player_footstep` | 652 | 0 | MM only |
| `player_sound` | 0 | 20,079 | **HLTV only** — replaces footsteps |
| `player_avenged_teammate` | 0 | 0 | Not observed |

**Critical finding:** `player_blind` is **MM only**. HLTV demos do not emit flash blind events.
This means enemies-flashed and flash-assist tracking is unavailable for HLTV demos.

### Weapon/Item Events

| Event | MM Demo | HLTV Demo | Notes |
|-------|---------|-----------|-------|
| `weapon_fire` | 2,693 | 3,591 | Both |
| `weapon_reload` | 99 | 0 | MM only |
| `weapon_zoom` | 267 | 0 | MM only |
| `item_pickup` | 1,018 | 1,683 | Both |
| `item_equip` | 3,530 | 0 | **MM only** |
| `bullet_damage` | 442 | 0 | MM only |
| `grenade_thrown` | 0 | 534 | **HLTV only** |

### Bomb Events

| Event | MM Demo | HLTV Demo | Notes |
|-------|---------|-----------|-------|
| `bomb_planted` | 10 | 11 | Both |
| `bomb_defused` | 3 | 7 | Both |
| `bomb_exploded` | 1 | 2 | Both |
| `bomb_pickup` | 42 | 52 | Both |
| `bomb_dropped` | 30 | 46 | Both |
| `bomb_beginplant` | 15 | 0 | MM only |
| `bomb_begindefuse` | 3 | 0 | MM only |

### HLTV-Specific Events

| Event | Count | Notes |
|-------|-------|-------|
| `hltv_chase` | 501 | Camera direction changes |
| `hltv_fixed` | 147 | Fixed camera positions |
| `CSVCMsg_HLTVStatus` | 569 | HLTV status net messages |
| `CDemoRecovery` | 16 | Demo recovery points |
| `entity_killed` | 281 | Alternative death tracking? |
| `player_ping` / `player_ping_stop` | 58/48 | Player communication pings |
| `switch_team` | 20 | Team switches (halftime) |
| `vote_cast` | 24 | Tactical timeout votes |

### Net Message Types (Both Demos)

| Message Type | MM Demo | HLTV Demo | Notes |
|--------------|---------|-----------|-------|
| `CSVCMsg_UserCommands` | 1,228,942 | 2,698,191 | Dominant message type |
| `CNETMsg_Tick` | 123,219 | 207,637 | Tick updates |
| `CSVCMsg_PacketEntities` | 123,218 | 207,636 | Entity state updates |
| `CDemoAnimationData` | 6,420 | 42,201 | Animation data |

---

## Impact on Analysis Engine Rules

### Stats That Work on Both Demo Types

| Stat | Source Event | Notes |
|------|-------------|-------|
| Kills / Deaths / Assists | `player_death` | Fully supported |
| Damage Dealt | `player_hurt` | Fully supported |
| Headshot Kills | `player_death.IsHeadshot` | Fully supported |
| Weapon-specific kills | `player_death.Weapon` | Fully supported |
| Bomb plants / defuses | `bomb_planted` / `bomb_defused` | Fully supported |
| Shots fired | `weapon_fire` | Fully supported |
| KAST % | Derived from kills/assists/survived/traded | Fully supported |
| Multi-kill rounds | Derived from kill counter | Fully supported |
| Opening kill / death | Derived from `player_death` ordering | Fully supported |
| Trade detection | `player_death` timing + team lookup | Fully supported |
| Rapid kill sequences | `player_death` timing | Fully supported |

### Stats That Require Matchmaking Demos

| Stat | Source Event | Alternative for HLTV |
|------|-------------|---------------------|
| Enemies Flashed | `player_blind` | None — event not emitted |
| Flash Assists | `player_death.AssistedFlash` | **May still work** — field is on death event |
| Flash Blind Duration | `player_blind.Duration` | None |
| Round-scoped reset timing | `round_officially_ended` | Use `cs_pre_restart` or `cs_round_final_beep` |
| Clutch resolution (round winner) | `round_officially_ended` | Need alternative signal |
| Item equip tracking | `item_equip` | None |
| Bullet damage detail | `bullet_damage` | None |
| Weapon reload / zoom | `weapon_reload` / `weapon_zoom` | None |

### Stats Blocked on Both Demo Types (historical — both since unblocked)

| Stat | Original blocker | Status |
|------|-----|--------|
| Economy / Money | No `item_purchase` event; needs entity state | Equipment-value/armor stats shipped via per-player entity providers (`player.entity.pawn.*` — see `docs/analysis-engine/analysis-entity-providers.md`) |
| Round winner | No `round_end` event in either type | Shipped via `RoundEndEnrichmentEdge` on the `$round_end` binding (see Migration Plan below) |

---

## Round Boundary Strategy

### Current Approach
- Round start: `round_freeze_end` (works on both)
- Round end: `round_officially_ended` (MM only)

### Recommended Approach
Use `cs_pre_restart` as the universal round-end signal:
- Present in **both** demo types (18 in MM, 24 in HLTV)
- Fires after the round has concluded
- Can be used for round-scoped node reset and clutch resolution

Alternatively, use `cs_round_final_beep` (18 in MM, 24 in HLTV) — fires at the very end of
each round as the final audio cue.

### Migration plan — executed

Implemented as the **`$round_end` logical-event binding** on `DemoSourceProfile` (see
[`Demo-Profiles.md`](./Demo-Profiles.md)) rather than hardcoded dual edges:

1. `cs_pre_restart` is a typed game event (`CsPreRestartEvent`, registered in `EventRegistry`)
2. Round-end handling binds to `$round_end`, a multi-event `FirstWins` binding expanded per
   profile — GOTV: `round_officially_ended` → `cs_win_panel_match`; HLTV: `cs_pre_restart` →
   `cs_win_panel_match` — with per-round first-wins guards on non-idempotent actions
3. Clutch resolution (`ClutchResolutionEnrichmentEdge`) and the other round-end infrastructure
   edges (`RoundEndEnrichmentEdge`, `ThresholdTallyEdge`, `ComputeOnRoundEndEdge`) subscribe to
   every concrete event in the active profile's `$round_end` binding
4. Round winner comes from `RoundEndEnrichmentEdge` (`enrich.round.has_winner` /
   `winner_team` / `winner_side`), driving the shipped CTW/CTL/TW/TL round-win counters

---

## Weapon String Reference

Confirmed weapon strings from both demo types (used in `player_death.Weapon`
and `player_hurt.Weapon`):

**Rifles:** `ak47`, `m4a1`, `m4a1_silencer`, `aug`, `sg556`, `famas`, `galilar`, `ssg08`
**AWP:** `awp`
**SMGs:** `mp7`, `mp9`, `mac10`, `p90`
**Shotguns:** `xm1014`, `negev`
**Pistols:** `glock`, `usp_silencer`, `hkp2000`, `p250`, `deagle`, `fiveseven`, `tec9`, `elite`, `revolver`
**Knives:** `knife_kukri`
**Utility:** `hegrenade`, `inferno`
**Other:** `planted_c4` (bomb kill), `world` (fall/environment damage)
