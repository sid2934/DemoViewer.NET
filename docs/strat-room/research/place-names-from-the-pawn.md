# Place Names From The Pawn: research table

Plan item: `plan.md` §3 Phase 0, "Place Names From The Pawn" (research, no review, depends on
nothing). Status: **in progress**. The Valve matchmaking half is answered, on eight demos across
two builds nineteen months apart and on a real hundred-demo corpus; FACEIT, HLTV broadcast and POV
are outstanding because no such demo exists locally (F6c). Gate 0 → 1 can pass on the Valve go with
the other sources as a stated risk, as `designs/00-overview.md` §5 recommends, because every
consumer already names its fallback (§4 below).

## 1. The question

Is `CCSPlayerPawn.m_szLastPlaceName` populated on every source the library sees, on every shipped
map, and how often is it unset (spawn, before the field is first networked, maps with no named
areas)? And what is the distinct place vocabulary per map, since that list is the canonical
vocabulary for Callout Aliases, The Round Index's tokens, Suggested Tags' detectors and the Strat
Model's step positions.

## 2. Measured: Valve matchmaking, builds 10231 to 10896

All figures from `PositionSampler.Walk` or the equivalent tracker replay over untrimmed
`GotvMatchmaking` demos in the Steam `replays` folder; the tour sample was never used.

### 2.1 Unset rate

| Demo | Map | Build | Samples | Null | Empty string | Source |
|---|---|---|---|---|---|---|
| `match730_003731893271710924851_1024675027_129.dem` | de_nuke | 10231 | alive samples, 8-tick base | 0 | (not split) | round-index §2.6 |
| same | de_nuke | 10231 | 54,223 (stride 16) | 0 | 17 (0.03%) | strat-model §2.4 |
| `match730_003844252717140672725_0377894676_389.dem` | de_dust2 | 10896 | alive samples | 0 | (not split) | round-index §2.6 |
| `match730_003842233788306292960_0260929275_408.dem` | de_mirage | 10896 | alive samples | 0 | (not split) | round-index §2.6 |
| de_mirage (Sep 2026) | de_mirage | 10896 | 55,511 (stride 16) | 0 | 21 (0.04%) | strat-model §2.4 |
| `match730_003842442070597828830_0022157567_392.dem` | de_inferno | 10896 | 118,887 alive | 45 (0.04%) | (not split) | round-index §2.6 |
| four demos, builds 10231, 10329 (x2), 10477 | nuke, inferno, ancient, dust2 | 10231 to 10477 | 144k, 126k, 200k, 152k | 0, 0, 0, 0 | 260, 234, 1,500, 280 (0.18 to 0.75%) | suggested-tags §2.1 |
| six demos (nuke x2, mirage x2, dust2, inferno) | five maps | 10231 and 10896 | every 32nd frame | populated on 99.7 to 100% of samples | (counted together) | zone-baking §7.1 |
| 100 newest replays (dust2 26, mirage 20, nuke 18, ancient 15, inferno 10, anubis 6, cache 4, overpass 1) | eight maps | mixed | 162,234 one-second rows | 0 of 100 demos failed to build; 22,219 distinct CT tokens | (not split) | round-index §2.7 |

Two facts every consumer has to know:

- **The wire delivers the empty string, not null.** The engine's XML promises null before the field
  is first networked; measured, `Place == null` is 0 on every demo and `Place == ""` is 0.03 to
  0.75%. Treat both as unplaced. Filed upstream as part of CS2DemoKit #58 (normalise or document).
- **The field is sticky.** A pawn on an unnamed nav area keeps its previous place name
  (round-index R4). That is what the game itself displays, so the token is what a human would call
  the spot; it is also why the miss rate is near zero rather than the 1 to 3% of nav areas no
  `env_cs_place` volume covers (zone-baking §2.5).

Also measured on the same demos: every alive pawn had `m_iTeamNum` 2 or 3; dead pawns keep
producing samples for the rest of the round (166 of 169 dead slots on nuke), so "live pawn" means
the entity, not the player, and every consumer filters by `player_death` (suggested-tags §2.1, #58).
`m_szLastPlaceName` on the planter's pawn read `BombsiteA` / `BombsiteB` on every plant on both
Round Facts demos (round-facts §2.7), which is what `enrich.round.plant_site` builds on.

### 2.2 Vocabulary per map

Distinct non-empty names seen from the pawn, against the `env_cs_place` volumes in the map's entity
lump (zone-baking §2.5, VRF over the installed maps):

| Map | From the pawn (demo) | `env_cs_place` distinct places | Volumes | Notes |
|---|---|---|---|---|
| de_nuke | 29 (round-index, suggested-tags, strat-model) | 29 | 58 | `Ramp` spans 7 64-unit Z buckets, `Silo` 6: a place is not a floor |
| de_dust2 | 24 (round-index, suggested-tags) | 24 | 43 | |
| de_mirage | 23 (round-index, strat-model) | 23 | 23 | identical name set to the volumes |
| de_inferno | 23 (round-index, build 10896), 24 (suggested-tags, build 10329) | 23 | 46 | one name differs between the two demos; which one is unresolved |
| de_ancient | 18 (suggested-tags, build 10477) | 18 | 18 | |
| de_anubis | (not walked) | 28 | 62 | 6 demos in the hundred-demo corpus |
| de_overpass | (not walked) | 25 | 26 | 1 demo in the hundred-demo corpus |
| de_vertigo | (not walked) | 23 | 50 | |
| de_train | (not walked) | 16 | 26 | |
| de_cache | (not walked) | 48 | 66 | 4 demos in the hundred-demo corpus |

Where both were counted, the pawn vocabulary equals the volume vocabulary, so the entity lump is
the authoritative list per map and the demo confirms it. Every map carries `BombsiteA`, `BombsiteB`,
`CTSpawn`, `TSpawn`, which `rules/highlights_position.rules.yaml` already relies on. The pawn field
is the only route to a place in a demo: `CCSPlace` derives from `CServerOnlyModelEntity` and is
never networked (zone-baking §2.5).

Zone Baking's cascade resolver reproduces the pawn's own label on 92.4 to 99.6% of placed samples
(zone-baking §7.1), so a resolver-derived place is a fallback, not a replacement, for the pawn field.

### 2.3 The Valve verdict on F1

**Go.** Null or empty is 0.03 to 0.75% of samples on builds 10231 to 10896, no demo of the hundred
failed, every active-duty map seen carries a full vocabulary that matches its entity lump. The
`m_szLastPlaceName` token is safe as the primary key of The Round Index, Suggested Tags, Callout
Aliases and Strat Model step positions for Valve matchmaking demos.

## 3. Outstanding sources

| Source | What is unknown | Exactly which demo closes it | Owning item |
|---|---|---|---|
| FACEIT (128 tick) | Whether the pawn field is networked at the same cadence; `player_team` cadence (Team Identity §6 unknown); whether 128 tick changes the sample stride math (`cadenceTicks`, round-index unknowns) | One current-era FACEIT match demo from the FACEIT match room, any active-duty map. Same demo as the Inputs Per Demo Source FACEIT row; one download serves both. | re-acquisition by Inputs Per Demo Source; the measurement by this item |
| HLTV broadcast | Whether the field is populated for all ten pawns in a broadcast recording (the recorder is a spectator proxy); coach pawns (`m_iCoachingTeam`, TI-V4); `round_end` present where GOTV lacks it | Re-download one of `demos/CORPUS.md` `pro/`: `furia-vs-vitality-m1-mirage.dem` (preferred: mirage's 23-name vocabulary is already measured on Valve, so a diff is direct), else `-m3-nuke.dem` or `-m4-overpass.dem`. Check `\\BLACK-BOX\Demos\Pro Demos` first (Team Identity's 8 HLTV records came from there). | re-acquisition by Inputs Per Demo Source (plan §3 names the re-download); the walk by this item |
| POV (client recording) | Whether other players' pawns carry the field outside the recording client's PVS; the null rate is expected to be far higher for non-local pawns | One `record`-ed POV demo from the owner's own client. Same demo as the Inputs Per Demo Source POV row. | re-acquisition by the owner, requested through Inputs Per Demo Source; the walk by this item |

Per (source, map) the walk records: samples, null, empty, distinct names, and the `player_team`
event count per slot (team-identity §6 asks for a roster column at no cost). Method is unchanged
from the Valve runs: `PositionSampler.Walk` over the demo via `DEMO_PATH`, never a link into
`demos/`.

## 4. The fallback each design already carries, per source

| Design | Where | Fallback if a source's place field is sparse or absent |
|---|---|---|
| The Round Index | §3.1, §6 R1, R3, R4 | The `?` token preserves the man-count when a pawn is unplaced; raw names are the contract so a rename becomes an alias; the opt-in `TokenSource.Zones` mode (Z-1) tokenises through the Zone Baking resolver from position alone, which needs no pawn field at all. |
| Suggested Tags | §6 | The side resolver's `OldTeam` path for `player_team` cadence; detectors go silent rather than wrong when occupancy has no places; test on one HLTV demo when one is re-downloaded. |
| Zone Baking | §3.4, §6 | `PlaceResolver` cascade (volumes, then nearest nav area) yields a place from a position on any source; the stated miss rate against the pawn is 0.4% (dust2) to 7.6% (mirage). The design says the other sources carrying the field "is an assumption until Place Names From The Pawn" runs. |
| Callout Aliases | plan §3 | Ships an empty alias table per map plus the Valve names; the vocabulary comes from the entity lump, which does not depend on the demo source. |
| Strat Model | §2.4, §3.9 | `""` is treated as unresolved; step positions carry `levelMinZ` because a place is not a floor; positions are stored, places are labels. |
| Round Facts | §6 A5 | `enrich.round.plant_site` reads the planter's place; the engine's source profiles own event differences; claims are stated for Valve matchmaking until this item's per-source row runs. |
| Team Identity | §2.1, §6 | Not a place consumer, but it shares the `player_team` coverage question: 6 of 277 matchmaking demos (2.2%) had no `player_team` and stay unassigned; FACEIT and POV coverage is the open unknown. |

## 5. Done criterion and re-acquisition

Done when §3's three rows carry a null rate and a vocabulary per (source, map) from one demo each,
and the F1 go/no-go in §2.3 is restated per source. The demos are the same three the Inputs Per
Demo Source item acquires (HLTV by re-download of a `demos/CORPUS.md` name, FACEIT and POV supplied
by the owner); this item adds no download of its own. `demos/CORPUS.md` is updated with every file
added.

## 6. Sources

- `docs/strat-room/plan.md` §2 F1, F6c; §3 Place Names From The Pawn, Callout Aliases; §5.
- `docs/strat-room/designs/round-index.md` §2.6 (place field, vocabulary, dead pawns), §2.7 (hundred-demo corpus), §6 R1, R3, R4.
- `docs/strat-room/designs/suggested-tags.md` §2.1 (four demos, builds 10231 to 10477), §5.3 (CS2DemoKit #58), §6.
- `docs/strat-room/designs/zone-baking.md` §2.5 (`env_cs_place` counts per map, `CCSPlace` never networked), §6, §7.1 (cascade against the pawn on six demos).
- `docs/strat-room/designs/strat-model.md` §2.4 (empty-string rate, one place spans floors).
- `docs/strat-room/designs/round-facts.md` §2.7 (planter's place at the plant frame), §6 A5.
- `docs/strat-room/designs/team-identity.md` §2.1 (`player_team` coverage), §6, §10, §11.3 TI-V4.
- `docs/strat-room/designs/00-overview.md` §5 item 4, §6.3.
- `demos/CORPUS.md`.
