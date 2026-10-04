# Suggested Tags: design

> **Status: APPROVED 2026-09-24**, as recommended (§8): route (a) app-side detectors for the
> first release, route (b) recorded as an upstream proposal and **not filed** until a team asks to author
> detectors in YAML (plan D6); execute detector strict (`N=4, T=4 s`); occupancy from The Round Index's
> alive-only token (delivered, `round-index.md` §3.2); proposals and verdicts in `suggestions/` beside
> `demos/` with their own fingerprint (integrator correction 2); queue keys `J/K/Y/N/Enter/Ctrl+Y` claimed
> in `Playback2DKeymap` (Find Rounds Like This is `Ctrl+F`, plan D7); `provenance` as a free-form object on
> the instance; hand-tag validation after the Tag Palette ships; the §5.3 doc corrections filed upstream
> (see the plan log for the issue number). Nothing here is implemented.

**Work item:** Suggested Tags (plan §3, Phase 2). **Kind:** research then design. **Review:** required
before any code. **Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0. **Written:** 2026-09-23.
**Depends on:** The Round Index, Tag Store (both designs in flight in this same workflow; the contracts
this document needs from them are stated in §2.9 and offered back in §8).

This document proposes the engine that offers tag instances with a confidence and a one-click accept,
edit or reject (finding F11, decision D6). It recommends route (a), an app-side detector over sampled
positions and detonation events, for the first release, and writes route (b), a team-presence provider
in CS2DemoKit, up as an upstream proposal for a later phase. Every number in it was measured on real
Valve matchmaking demos with a scratch probe; the probe and its outputs are listed in §10.

---

## 1. Problem and scope

A team that tags rounds by hand spends most of its time on the same six labels: the execute, whether it
was a rush, the default, the fake before the rotate, the utility opener, and the retake. Each is a
mechanical pattern over where players were and when utility landed. The plan asks for these to be
proposed, not written: the tagger sees a queue of candidates on the timeline, each with a confidence,
and accepts, edits or rejects with one key. Accepted proposals become ordinary human tags with a note of
where they came from. Rejected ones are not offered again.

In scope:

- The detector set: **execute**, **fast or rush** (a label on an execute), **default**, **fake then
  rotate**, **coordinated opener**, **retake grouping**. For each, a definition, its parameters, how
  confidence is computed, and the lead and lag window the proposal claims.
- The input model the detectors run over, and where it comes from.
- The proposal store: what is persisted, where, keyed how, and how rejections are remembered.
- How a proposal is presented and acted on: a queue on the Tag Track, keyboard flow, provenance on
  acceptance.
- How a team tunes detector parameters, and how the result is validated against hand-tagged rounds.
- The choice between route (a) and route (b), with (b) specified as an upstream proposal.

Out of scope, by the original roadmap and the plan: trajectory clustering, strategy classifiers, anything
learned from labels beyond a parameter fit. The detectors here are rules with thresholds. That is the
point: a coach can read the definition, disagree with a number, and change it.

---

## 2. What exists today

### 2.1 Sampled positions with nav place

`CS2DemoKit.Parser.EntityTracking.PositionSampler.Walk(demo, frameStride, maxFrames)` streams one
`PositionSample(FrameIndex, Tick, PlayerSlot, Position, Place)` per live pawn per sampled frame
(packaged XML, `CS2DemoKit.Parser.xml` lines 3998-4076). The walk is lazy and must start at frame 0
because entity state is delta-encoded; the stride subsamples output, not decode. Nothing in the app
consumes it yet (`grep PositionSampler src/ tools/` is empty at `d90ec9f`); the rules layer reads the
same field as `player.place` (`rules/highlights_position.rules.yaml:36,45`).

Four facts about it were measured for this design, on four untrimmed `GotvMatchmaking` demos from the
Steam replays folder (never the tour sample). Builds 10231, 10329 (two demos) and 10477.

| Fact | Measured | Consequence |
|---|---|---|
| Which clock `PositionSample.Tick` is on | First sample tick 1, last sample tick equals `ParsedDemo.TickCount` on all four demos; `ServerStartTick` is 1587 to 3365 | It is the **frame clock** already, the same clock as `GameEvent.GameTick` and `ClipRound.StartTickFrameClock`. No subtraction. The XML doc says "the frame's server tick", which is misleading; see §5.3. |
| Unplaced samples | `Place == null`: 0 of 144k, 126k, 200k, 152k samples. `Place == ""`: 260, 234, 1500, 280 (0.18 to 0.75 %) | The XML promises null; the wire delivers the empty string. Treat both as unplaced. |
| Dead pawns | 166 of 169, 126 of 128, 214 of 215, 171 of 173 dead slots kept producing samples more than 2 s after their `player_death` | "Live pawn" means the entity is live, not the player. Every detector needs an alive filter from `player_death`. |
| Cost, 210,945-frame de_ancient demo | stride 1: 1.60 M samples, 2.4 s; stride 8: 200 k, 1.6 s; stride 64: 25 k, 1.6 s. Parse to `ParsedDemo`: 0.6 to 1.1 s on all four | The decode floor is about 1.5 s per demo. A detector pass costs roughly one parse plus one walk, under 4 s per demo. |

Place vocabularies seen: de_nuke 29 names, de_inferno 24, de_ancient 18, de_dust2 24. Every map carries
`BombsiteA`, `BombsiteB`, `CTSpawn`, `TSpawn`, which is what the position rules file already relies on.

### 2.2 Detonation and bomb events

`ParsedDemo.AllGameEvents` (`CS2DemoKit.Parser.xml:5074`) carries every decoded event in tick order with
`GameTick` in the frame clock (`:4310`). Fields as decoded by `GameEvent.GetDecodedFields()` on build
10231:

| Event | Fields | Note |
|---|---|---|
| `smokegrenade_detonate`, `flashbang_detonate`, `hegrenade_detonate` | `EntityId`, `UserId`, `UserIdPawn`, `X`, `Y`, `Z` | thrower slot and world position |
| `inferno_startburn` | `EntityId`, `X`, `Y`, `Z` | **no thrower** (27 % of detonations on de_nuke, 21 % on de_ancient); the analysis layer attributes it by projectile slot (`CS2DemoKit.Analysis.xml:8860`) but that is inside a rules run, not on the event |
| `decoy_started` | `EntityId`, `UserId` (pawn handle), `X`, `Y`, `Z` | rare (0 to 12 per match) |
| `bomb_planted` | `C4`, `Site`, `UserId`, `UserIdPawn` | `Site` is a **map-specific entity index** (173 and 236 on de_nuke, 172 and 264 on de_inferno, 233 and 234 on de_ancient), not A or B |
| `bomb_defused`, `bomb_exploded` | same shape | |

Resolving `Site` to a name works through the planter: the planter's `Place` at the plant second was
`BombsiteA` or `BombsiteB` for **58 of 58 plants** across the four demos. That is the resolver this design
uses until Zone Baking gives a volume lookup.

### 2.3 Rounds and sides

`ClipRounds.Derive(demo)` (`CS2DemoKit.Analysis.xml`, `T:CS2DemoKit.Analysis.Clips.ClipRounds`) opens a
round per `round_freeze_end` in the frame clock and is what the cache stores as `CachedRound`
(`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs:84-89`, written at
`Modules/Highlights/HighlightScanService.cs:609` and `Modules/Library/DemoLibraryService.cs:1393`).

None of the four demos carries `round_end`. Each carries `round_officially_ended`, one fewer than the
number of freeze-ends (the last round has none). The detectors therefore bound a round by
`[freeze_end, round_officially_ended or next freeze_end)`. Round Facts owns phase boundaries; this
document needs only that window.

GOTV emits `player_team` only for the halftime swap (10 events per regulation match, 20 with overtime,
measured; the same finding `Modules/Playback2D/Timeline/ModuleTimelineData.cs:209-233` records). Side per
slot per round is resolved the way that file does: the last change at or before the round's first
second, `OldTeam` when the first recorded change lies ahead, `PlayerInfo.Team` for slots with no change.

### 2.4 The timeline and the keymap

`ITimelineTrack` (`src/Playback2D/DemoViewer.NET.Playback2D.Core/Timeline/ITimelineTrack.cs:22`) yields
point markers and range bands; `TimelineMarkerKind.Custom` (`:15`) exists for tracks like this one.
`TimelineBand(TrackId, StartFrameIndex, EndFrameIndex, Label, Tooltip, Argb)` (`TimelineMarker.cs:24-30`)
is the band shape. Tracks register in `Playback2DTabViewModel` (`Modules/Playback2D/Playback2DTabViewModel.cs:328-331`)
through `Playback2DTimelineViewModel.RegisterTrack` (`Timeline/Playback2DTimelineViewModel.cs:148`); a
band press seeks to its start frame (`Views/Playback2D/TimelineControl.axaml.cs:95-104`). A track raises
`MarkersChanged` to be re-queried, which `AnnotationTrack` does on every document change.

`Playback2DKeymap` (`Modules/Playback2D/Playback2DKeymap.cs`) binds Space, the arrows, `Q`/`E` and
`Shift+Q`/`Shift+E`, `F`/`Shift+F`, `Esc`, `D`, `X`, `Ctrl+Z`, `Ctrl+Shift+Z`, `Ctrl+X`, and reserves
`Home` (`:307-358`). Shell accelerators are `Ctrl+P/O/W/,/B` and `Ctrl+1..9` (`:363-378`); the browser
list adds `Ctrl+T/N/W`, `F11`, `F12` and the dev-tools chords (`:388-400`). `J`, `K`, `Y`, `N`, `Enter`
and `A` are free. A side finding for decision D7: `F` is already `CycleFollowNext` (`:333`), so Find
Rounds Like This cannot take the original roadmap's `F` without rebinding follow.

### 2.5 Persistence

`DemoCacheStore` (`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs:11-31`) is `index.json`
plus one lazily-read sidecar per demo under `demos/`, named by `StableKey(path)` (`:507`, a path hash,
not a content hash), written atomically (`:559-572`), and fully in-memory when the cache root is null
(`:513-525`, the browser host). `DemoCacheRecord.Sha256` (`DemoCacheModels.cs:216`) is null until
something computes it; the annotation store hashes every demo it touches
(`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationStore.cs:109`) and the library
hashes only size collisions (`DemoLibraryService.cs:926-938`). Content Identity (Phase 0) closes that
gap and every new store joins on `demo.sha256` (F9, F15).

Background work runs through `IDemoEvaluator` (`Services/DemoProcessing/IDemoEvaluator.cs:22`:
`Id`, `Wants(path)`, `Evaluate(path, parsed)`, `OnParsedOpportunistically`) fed by
`DemoEvaluationCoordinator` (`Consider` at `:74`, `FanOutParsed` at `:158`), which parses once and fans
out to every evaluator. `HighlightScanService` is the model consumer (`Modules/Highlights/HighlightScanService.cs:44,152`).

### 2.6 Modules, features and settings

New surfaces are first-party `IWorkspaceModule`s (`Modules/Highlights/HighlightsModule.cs:31-61`,
`Modules/Playback2D/Playback2DModule.cs:23-51`), gated by ids in `Features/FeatureCatalog.cs`
(sub-features of `tab.playback2d` at `:147-169`). Persisted settings are sections of `AppSettings`
(`Configuration/AppSettings.cs:396`, `Playback2DSettings`), and data-only user registries live under the
config root (`Services/AppPaths.cs:143`, the themes directory).

### 2.7 What rules v2 can and cannot say

`docs/rules-v2/rules-v2-spec.md` §3 to §5 fix the language. Relevant limits, cited:

| Need | What the language offers | Gap |
|---|---|---|
| "N teammates in place X" at one instant | `round.team.alive` / `round.team.players` / `round.team.equipment` (`:338`, live per `:372`), all integers over the whole team; `player.place` is the **subject's** place only | no team aggregate is parameterised by a place, and no per-player read can reach a teammate's state |
| "within T seconds" | `duration`/`instant` types (§3.1), `event.tick`, `match.tick` | no window primitive over a team state; `while:` gates on a flag, it does not count arrivals |
| a set of places (a site region) | `define:` lists as the right operand of `in` (§3.4, `:221-238`) | usable for `player.place in site_a_region` on the subject, but only the subject |
| aggregating over teammates | `for: each_player` materialises one node per subject; `for: match` rejects `round.team.*` (`:409-413`) | there is no "for each teammate" axis and no list-valued team read |
| functions | the closed set `min max abs floor contains startswith` (§3.7, `:254-268`), deliberately closed | a place-count function would be a language change, not a ruleset |

So an execute is not expressible today, and adding it means an engine change (§5.2). That is finding F11
restated with line numbers.

### 2.8 The engine's provider and aggregate shapes (origin/main)

Route (b) would sit beside two existing things. `BuiltinProviderSpecs.PawnPlace`
(`src/CS2DemoKit.Analysis/Plugins/BuiltinProviderSpecs.cs:63-72`) declares `entity.pawn.place` as a
`ProviderSpec` over `m_szLastPlaceName`. `RoundTeamAggregateNode`
(`src/CS2DemoKit.Analysis/Nodes/RoundTeamAggregateNode.cs`) computes `round.team.alive` live from
`PlayerContextIndex.CountAlive(team)` (`Building/PlayerContextIndex.cs:68`), the single-writer alive
index; `B6RuleIds.Members` (`Building/B6Aggregates.cs:47-56`) is the table the catalog generator reads.
A team-presence aggregate is a third `AggregateKind` plus a place argument, which is exactly the part the
language cannot express (§5.2).

### 2.9 The two sibling designs this one leans on

Neither is written yet at the time of this document. What this design assumes from each, stated so the
integrator can reconcile:

- **The Round Index** stores one row per (demo, round, sampled second) with a place-count token per
  side. This design needs that token to be **decodable** (place to count, not only hashable) and to
  count **alive** players only (§2.1, dead pawns). If it does, the execute, default, fake and retake
  detectors run over the index with no second walk. If it stores per-slot rows instead, better still.
  If it stores neither, this design falls back to its own walk (§3.2), at about 2 s per demo.
- **Tag Store** holds instances keyed by `demo.sha256` and frame-clock ticks, with a human namespace and
  a parser namespace. This design writes accepted proposals into the **human** namespace and needs one
  extra field on an instance: `provenance` (§3.5). It does not need a third namespace.

---

## 3. Proposed design

### 3.1 Vocabulary

| Term | Meaning |
|---|---|
| **detector** | A pure function from a round's occupancy series and events to zero or more proposals. Has an id, a parameter set, and a code it proposes. |
| **proposal** | A candidate tag instance: code, side, frame-clock `from`/`to`, labels, confidence in 0..1, an evidence list, and an identity key. Never a tag until accepted. |
| **claim window** | The `[from, to]` a proposal asks for. Lead is what the detector adds before the trigger; lag is what it adds after. Both are parameters. |
| **occupancy** | Per round, per second since freeze-end, per side: a map from place to the number of alive players there. Plus, when available, per slot: the place each alive player was in. |
| **site region** | For a bombsite: the site place plus its approach places. Data, per map (§3.3). |
| **verdict** | accepted, edited (accepted with changes), or rejected. Remembered per proposal identity. |
| **parameter profile** | The tunable numbers for every detector, as one JSON document a team owns. |

### 3.2 Inputs

**Occupancy.** Preferred source: The Round Index rows for the demo, decoded to place counts per side
per second, alive-only. Fallback source: `PositionSampler.Walk(demo, 8, int.MaxValue)` folded into the
same shape by `RoundOccupancyBuilder`, with the alive filter from `player_death` and the side resolver
from §2.3. Stride 8 at 64 tick is one sample per eighth of a second; the fold keeps the first sample per
(slot, second). Measured cost 1.2 to 1.6 s per demo. Both sources produce the same `RoundOccupancy`
type, so detectors do not know which fed them.

The per-slot form is only needed by the lead/lag refinement (§3.4, "arrival ticks") and by the fake
detector's presence count. §3.4 shows the instantaneous per-side count is within one to three fires of
the per-slot window count on every demo, so the first release can run entirely on the per-side token if
that is all the index carries.

**Events.** From `ParsedDemo.AllGameEvents`: the four detonation events and `inferno_startburn`, the
three bomb events, `player_death`, `player_team`, `round_freeze_end`, `round_officially_ended`. All in
`GameTick`.

**Detonation place.** A detonation carries a world position and no place. Until Zone Baking lands, the
detector resolves it to the place of the **nearest sampled position** in a sparse cloud kept from the
walk (1 in 25 placed samples; 5,000 to 8,000 points per demo), within 400 units. Measured unresolved: 8
of 225, 6 of 235, 16 of 451, 25 of 295 (2.6 to 8.5 %). An unresolved detonation still counts for the
opener (which needs only time and side) and is skipped by the fake detector. When Zone Baking ships, a
`PlaceResolver` replaces the cloud behind the same call.

**Site regions.** Three sources, in precedence order:

1. A shipped table `assets/<map>/site-regions.json` where one exists (none ship in the first release;
   Zone Baking can generate them from nav adjacency later).
2. A **learned** table per map, built from the library: for each site, the places T players occupied in
   the 12 s before a plant at that site, kept when they appear before at least 40 % of plants **and**
   are not spawn-adjacent (occupied by two or more T players in seconds 0 to 10 of at most 30 % of
   rounds). Without the spawn filter the learned set pulled in `Outside`, `Mini` and `Lobby` on de_nuke
   and the execute detector fired in 12 of 14 non-plant rounds at a median of 3 s after freeze-end.
   With it, learned regions on the four demos were: de_nuke A `BombsiteA+Mini+Hut+Squeaky`; de_inferno A
   `BombsiteA+Middle+TopofMid+Balcony+Apartments+Pit+SecondMid`, B `BombsiteB+Ruins+Banana`; de_ancient A
   `BombsiteA+SideHall+Middle+MainHall`, B `BombsiteB+Ramp+TSideLower+Middle`; de_dust2 A
   `BombsiteA+ExtendedA+ARamp+LongA`, B `BombsiteB+MidDoors+BDoors+Hole+UpperTunnel`. A coach will
   recognise every one of these. `Middle` on de_inferno and de_ancient is a known over-reach and is
   why the table is editable.
3. The user's edits, in the parameter profile (§3.7), which override both.

The learned table is recomputed when the library gains demos on that map and is stored beside the
profile as `site-regions.<map>.json` with the demo count it was learned from. Fewer than four plants at
a site yields the site alone, as on de_nuke B above.

### 3.3 The detector set

Every detector runs per round and per side where a side applies. Parameters are the defaults in the
shipped profile. "Second" means seconds since `round_freeze_end`; ticks are frame clock. Confidence is
clamped to `[0.05, 0.99]` and is a product of named factors so the UI can show why.

#### Execute

| | |
|---|---|
| Definition | At some second `s ≥ 3`, at least `N` alive players of side T are inside site region `R(X)`, and within the next `T + touch` seconds at least one of them is inside `X` itself. The earliest such `s` per round is the execute at `X`. |
| Parameters | `N = 4`, `T = 4 s`, `touch = 10 s`, `minSecond = 3` |
| Confidence | `c = f_count · f_touch · f_utility` where `f_count = min(1, n_in_region / 5)` at the peak second inside `[s, s+T]`; `f_touch = 1` if a player is in `X` by `s+T`, `0.7` if only by `s+T+touch`; `f_utility = 0.6 + 0.1 · min(4, detonations by T resolved into R(X) in [s-8, s+T])`. |
| Claim window | from `s - lead` to `s + T + lag`, `lead = 6 s`, `lag = 8 s`, floored at the round start. When per-slot data exists, `from` is instead the **first arrival** of the `N` players into the region, minus `lead`. |
| Labels | `site = X`, `count = n`, `tempo = rush` when `s ≤ 25` (see next row), `plant = true` when a `bomb_planted` follows at `X` in the round |

Measured against plants as a weak truth (a plant at `X` is a committed execute; a fired execute in a
round with no plant is either a failed execute or a false positive, and only hand tagging tells which):

| Demo (build) | Plants in scored rounds | Fires | Fired at the planted site before the plant | Fired at the other site or after | Fires in rounds with no plant |
|---|---|---|---|---|---|
| de_nuke (10231) | 9 of 23 rounds | 3 | 3 | 0 | 0 of 14 |
| de_inferno (10329) | 16 of 18 | 10 | 10 | 0 | 0 of 2 |
| de_ancient (10477) | 19 of 29 | 16 | 12 | 1 | 3 of 10 |
| de_dust2 (10329) | 14 of 23 | 10 | 6 | 2 | 2 of 9 |

That is the `N=4, T=4 s, instant+touch` row of the parameter grid. Loosening to `N=3, T=6 s` raises the
plant hit rate to 4/9, 13/16, 18/19 and 10/14 and raises non-plant fires to 2, 0, 7 and 4. The default
sits at the strict end deliberately: a proposal the tagger rejects costs a keypress, but a queue that is
mostly noise gets ignored. Matchmaking teams on de_nuke rarely commit four players at once; the
low de_nuke recall is play style, not a detector fault, and the tuning view (§3.7) is where a team moves
`N` to 3 for itself.

The per-slot window variant ("N distinct players with any sample in the region inside a T-second
window") fired within one to three of the instantaneous count in every cell of the grid, so the
instantaneous count over the Round Index token is what ships.

#### Fast or rush

A label, not a detector. An execute whose `s ≤ rushSecond` (`rushSecond = 25`) is labelled `tempo =
rush`; one with `s ≥ slowSecond` (`slowSecond = 60`) is labelled `tempo = late`; otherwise `tempo =
mid`. The band and the code are the execute's. Confidence is the execute's. Measured share of rush
among fired executes: 3 of 3, 5 of 10, 14 of 16, 5 of 10; matchmaking plays fast.

#### Default

| | |
|---|---|
| Definition | No execute fired by second `defaultSecond`, and at second `spreadSecond` the T side occupies at least `spreadPlaces` distinct places that are not spawn-adjacent (the same baseline as §3.2 item 2). |
| Parameters | `defaultSecond = 40`, `spreadSecond = 30`, `spreadPlaces = 3` |
| Confidence | `c = 0.5 + 0.1 · (distinct places - spreadPlaces) + 0.1 · [a T detonation resolved into each of two different regions before defaultSecond]`, capped at 0.9. A default is a weaker claim than an execute and never reaches the execute's ceiling. |
| Claim window | from `round start` to the execute's start if one follows, else to `defaultSecond + lag`, `lag = 10 s`. |
| Labels | `places = [...]` at `spreadSecond`, `then = execute@X` when an execute follows |

Measured (at the execute default above): 2, 2, 2 and 4 rounds per demo. Under the looser execute
setting the count drops because more rounds resolve as executes; the two detectors are complementary by
construction (a round gets one or the other, or neither).

#### Fake then rotate

| | |
|---|---|
| Definition | An execute at `Y` preceded, in `[s - fakeWindow, s - 3]`, by either at least `fakePresence` alive T players inside `R(X)` (`X ≠ Y`) or at least `fakeUtility` T detonations resolved into `R(X)`. |
| Parameters | `fakeWindow = 25 s`, `fakePresence = 2`, `fakeUtility = 2` |
| Confidence | `c = c_execute · (0.6 + 0.1 · presence + 0.1 · utility)`, capped at 0.95. |
| Claim window | from the first fake evidence (presence or detonation) minus `lead = 4 s` to the execute's `to`. The execute proposal is kept as its own band; the fake band spans both. |
| Labels | `fake = X`, `real = Y`, `presence = n`, `utility = n` |

Measured: 0, 5, 3 and 0 per demo. de_inferno's five are all rounds where two or more T stood in Banana
or Ruins and then took A. The zero on de_nuke and de_dust2 reflects both the strict execute default and
matchmaking play. Requires the per-slot form or the per-side token's count in `R(X)`; both suffice.

#### Coordinated opener

| | |
|---|---|
| Definition | `K` detonations by one side inside `W` seconds. Any of smoke, flash, HE, inferno start (inferno without a thrower is attributed to no side and is excluded unless the analysis layer's projectile attribution is available). |
| Parameters | `K = 3`, `W = 2 s` |
| Confidence | `c = 0.5 + 0.1 · (detonations in window - K) + 0.2 · [all resolved detonations land in one site region] + 0.1 · [window starts before second 20]`, capped at 0.95. |
| Claim window | from the first detonation minus `lead = 5 s` (the throws) to the last plus `lag = 6 s`. |
| Labels | `side`, `count`, `region = X` when all resolved detonations share one region, `kinds = [...]` |

Measured: 3, 7, 12 and 5 rounds per demo fired; of those, 2, 2, 3 and 2 had all three resolved into one
site region. The one-region factor is what separates a set execute from three players throwing utility
in three directions, which is the difference the tagger cares about.

#### Retake grouping

| | |
|---|---|
| Definition | After `bomb_planted` at `X`, two or more CT players **alive at the plant** each first enter `R(X)` within `retakeGap` seconds of the previous entrant. The group is every entrant inside the run. |
| Parameters | `retakeGap = 6 s`, `minGroup = 2` |
| Confidence | `c = 0.5 + 0.15 · (group - 2) + 0.1 · [a CT detonation resolved into R(X) inside the group's window] + 0.1 · [bomb_defused follows]`, capped at 0.95. |
| Claim window | from the first entrant minus `lead = 5 s` to the last entrant plus `lag = 10 s`, capped at the round end. |
| Labels | `site = X`, `group = n`, `aliveCt = n at plant`, `outcome = defused / exploded / wiped` |

Measured: 4 of 9, 7 of 16, 8 of 19 and 4 of 14 planted rounds produced a group, sizes 2 to 4. Rounds
with one alive CT or none produce nothing, correctly. Needs per-slot data (entry times per player); with
only the per-side token it degrades to "CT count in `R(X)` rose from below 2 to at least 2 within
`retakeGap` seconds", which the first release ships as the fallback.

### 3.4 Data model

All ticks are frame clock. Every store carries the same clock header the annotation sidecar writes
(`docs/playback2d-v2/annotations-format.md`, the `clock` block).

```jsonc
// <cacheRoot>/suggestions/<StableKey(demoPath)>.json      (schema v1)
{
  "schemaVersion": 1,
  "demo":  { "sha256": "...", "fileName": "match.dem", "sizeBytes": 289436777 },
  "clock": { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 154869, "firstTick": 1, "lastTick": 132516 },
  "detectorSet": {
    "fingerprint": "sha256 of (profile json + site-region tables for this map + detector code version)",
    "profileId": "team-default", "computedAtTicks": 0
  },
  "occupancySource": "round-index" | "walk",
  "proposals": [
    {
      "id": "exec|r12|T|BombsiteA|s=21",          // identity key, see below
      "detector": "execute", "code": "execute",
      "round": 12, "side": 2,
      "fromTick": 40960, "toTick": 41728,
      "triggerTick": 41344,
      "confidence": 0.78,
      "factors": { "count": 0.8, "touch": 1.0, "utility": 0.9 },
      "labels": { "site": "BombsiteA", "count": "4", "tempo": "rush", "plant": "true" },
      "evidence": [ { "kind": "occupancy", "tick": 41344, "text": "4 T in BombsiteA+Mini+Hut+Squeaky" },
                    { "kind": "event", "tick": 41100, "text": "smokegrenade_detonate by slot 6 -> Hut" } ]
    }
  ]
}

// <cacheRoot>/suggestions/<StableKey(demoPath)>.verdicts.json      (schema v1)
{
  "schemaVersion": 1,
  "demo": { "sha256": "..." },
  "verdicts": {
    "exec|r12|T|BombsiteA|s=21": { "verdict": "accepted", "tagInstanceId": "…", "at": "2026-09-23T…" },
    "exec|r14|T|BombsiteB|s=47": { "verdict": "rejected", "at": "…" },
    "default|r3|T":               { "verdict": "edited",  "tagInstanceId": "…", "at": "…" }
  }
}
```

**Identity key.** `detector | round | side | site-or-region | trigger second quantised to 5 s`. It is
stable across a re-detection with changed parameters as long as the same event is found, which is what
lets a rejection survive a tuning pass. It is not stable across a re-parse that renumbers rounds; that
is why the verdicts file also keys on `demo.sha256`, and why a re-parse with a different `frameCount`
in the clock header triggers a re-match by nearest trigger tick (within 5 s) before anything is
re-proposed.

**Two files, not one.** Proposals are rebuilt wholesale when the detector set fingerprint changes.
Verdicts are append-only and never rebuilt. Keeping them apart means a tuning pass cannot lose a
rejection, and the proposals file can be deleted freely.

**Why not the record sidecar.** `DemoCacheRecord` is one tiered record with an index projection; a
proposals payload of a few hundred rows per demo would bloat the sidecar every surface reads, and its
staleness rule (`ConfigFingerprint`) is the rules fingerprint, not this one. A sibling directory under
the same cache root, named by the same `StableKey`, written by the same `WriteAtomic`, is the F9 shape
with its own stamp. The index row gains one integer, `SuggestionCount`, mirrored the way
`HighlightCount` is (`DemoCacheModels.cs:415-419`), so the queue can say "12 pending" without opening
the file.

**Accepted proposals** become Tag Store instances in the human namespace with:

```jsonc
"provenance": { "source": "suggested-tags", "detector": "execute", "proposalId": "exec|r12|T|BombsiteA|s=21",
                "confidence": 0.78, "detectorSetFingerprint": "…", "edited": false }
```

Nothing else about the instance differs from a hand-made one. The Matrix, the Strat Record Panel and
export treat it as human. `edited: true` records that the tagger moved a boundary or changed a label
before accepting, which the tuning view uses as signal (§3.7).

### 3.5 APIs

All in `DemoViewer.NET.dll` under `Modules/SuggestedTags/`, following the Highlights layout (F14).

```csharp
// The occupancy input. Built from the Round Index or from a walk; detectors never see the source.
public sealed class RoundOccupancy
{
    public int Round { get; }                       // 1-based, ClipRounds numbering
    public int StartTick { get; }                   // frame clock, round_freeze_end
    public int EndTick { get; }                     // frame clock, round_officially_ended or next start - 1
    public int Seconds { get; }
    public IReadOnlyDictionary<string,int> CountsAt(int side, int second);   // place -> alive count
    public bool HasSlots { get; }                   // per-slot form available
    public string? PlaceOf(int slot, int second);   // null when unplaced, dead, or !HasSlots
    public IReadOnlyList<int> Slots(int side);
}

public interface IProposalDetector
{
    string Id { get; }                              // "execute", "default", ...
    string Code { get; }                            // the tag code proposed
    IReadOnlyList<DetectorParameter> Parameters { get; }
    IEnumerable<TagProposal> Detect(DetectorContext ctx, RoundOccupancy round, DetectorProfile profile);
}

public sealed record DetectorContext(
    string Map, int TickRate, SiteRegions Regions, IReadOnlyList<PlacedEvent> Events,
    IReadOnlyList<TagProposal> EarlierProposalsThisRound);   // fake/default read the execute's result

public sealed record TagProposal(string Id, string Detector, string Code, int Round, int Side,
    int FromTick, int ToTick, int TriggerTick, double Confidence,
    IReadOnlyDictionary<string,double> Factors, IReadOnlyDictionary<string,string> Labels,
    IReadOnlyList<ProposalEvidence> Evidence);

// The service. One parse, many evaluators: it is an IDemoEvaluator like HighlightScanService.
public sealed class SuggestedTagsService : IDemoEvaluator
{
    public string Id => "suggested-tags";
    public bool Wants(string path);                 // suggestions file missing or fingerprint stale, and the feature on
    public void Evaluate(string path, ParsedDemo parsed);   // build occupancy, run detectors, write proposals
    public ProposalSet Load(string path);           // proposals + verdicts, merged
    public void Accept(string path, string proposalId, TagInstanceEdit? edit);  // writes Tag Store + verdict
    public void Reject(string path, string proposalId);
    public event Action<string>? Changed;
}
```

Detectors run in a fixed order per round: execute, then default, fake, opener, retake. Execute must
come first because default and fake read its result through `EarlierProposalsThisRound`. The order is
data in the profile so a future detector can slot in.

The five detectors are ordinary classes registered in a static list, like the timeline tracks. There is
no plug-in seam here and there should not be one: the plugin design document scopes third-party code
out, and a team that wants a new detector wants YAML, which is route (b).

### 3.6 UI touchpoints

**Proposal Track.** An `ITimelineTrack` with id `suggested`, display name "Suggested", bands only,
`TimelineMarkerKind.Custom`. One band per pending proposal, labelled `code@site` and tinted by
confidence (three steps: below 0.5, 0.5 to 0.75, above 0.75; the host maps the step to a theme token so
the track never names a colour). Accepted proposals leave this track and appear on the Tag Track as
instances; rejected ones disappear. A band press seeks to `fromTick` like any band. The track raises
`MarkersChanged` when the proposal set or a verdict changes.

**Proposal Queue.** A panel docked with the Tag Palette (a sibling build item) inside the 2D Playback
tab, listing pending proposals for the open demo in round order: code, side, site, second, confidence
with its factors on hover, and the evidence lines. Filter by detector and by minimum confidence. A
counter in the tab header reads "Suggested: 12" from `SuggestionCount`.

**Keyboard flow**, registered in `Playback2DKeymap` as new `Playback2DAction` members and
conflict-checked by its static constructor:

| Action | Key | Scope | Behaviour |
|---|---|---|---|
| `SuggestionNext` / `SuggestionPrev` | `K` / `J` | Always | Select the next or previous pending proposal and seek to its `fromTick`. Same keys the plan gives Result Cards for walking a set. |
| `SuggestionAccept` | `Y` | Always | Write the instance, record the verdict, advance to the next. |
| `SuggestionReject` | `N` | Always | Record the verdict, advance. |
| `SuggestionEdit` | `Enter` | Always | Open the instance editor pre-filled from the proposal; saving accepts with `edited: true`. Esc returns without a verdict. |
| `SuggestionAcceptAll` | `Ctrl+Y` | Always | Accept every pending proposal at or above the queue's confidence filter, after a confirm. |

`Y`, `N`, `J`, `K`, `Enter` and `Ctrl+Y` collide with nothing in the shipped table, the shell list or
the browser list (§2.4). They are inert when no proposal is selected. The Tag Palette's own hotkeys are
not designed yet; its design must check this table before claiming any of these six.

**Editing.** The editor is the Tag Palette's instance editor with the proposal's window and labels
filled in. Dragging a band end on the timeline is the envelope drag the Playback2D design lists as open
criterion O5; it is not required here, the editor's numeric fields suffice.

**Feature gate.** `playback2d.suggestedtags`, a sub-feature of `tab.playback2d` in `FeatureCatalog`,
default on for desktop and browser alike (the browser row below says what "on" means there). Off hides
the track, the queue and the evaluator.

### 3.7 Tuning by a team

The parameter profile is a JSON document in the user config directory, `suggested-tags/profile.json`,
next to `site-regions.<map>.json`. It lists every detector, every parameter with its value, and the
detector order. The shipped default is embedded and written out on first run, the way themes are
(`AppPaths.cs:143,200`). A team shares the folder like it shares a palette definition.

```jsonc
{ "schemaVersion": 1, "id": "team-default",
  "order": ["execute", "default", "fake", "opener", "retake"],
  "execute": { "N": 4, "T": 4, "touch": 10, "minSecond": 3, "lead": 6, "lag": 8, "rushSecond": 25, "slowSecond": 60 },
  "default": { "defaultSecond": 40, "spreadSecond": 30, "spreadPlaces": 3, "lag": 10 },
  "fake":    { "fakeWindow": 25, "fakePresence": 2, "fakeUtility": 2, "lead": 4 },
  "opener":  { "K": 3, "W": 2, "lead": 5, "lag": 6 },
  "retake":  { "retakeGap": 6, "minGroup": 2, "lead": 5, "lag": 10 },
  "siteRegionOverrides": { "de_inferno": { "BombsiteA": { "remove": ["Middle"] } } } }
```

Changing a value changes the detector-set fingerprint, which marks every demo's proposals stale;
`Wants` returns true and the coordinator rebuilds them in the background. Verdicts are untouched.

**The tuning view** is a Settings section, not a new tab. It shows, per detector, over the demos that
have verdicts: proposals made, accepted, edited, rejected; and over the demos that have hand tags of the
same code (§7.3), recall and precision at the current parameters. A parameter change re-runs the
detectors on the verdict demos in memory and updates the two numbers before it is saved, so a coach
sees "N=3 finds 4 more of your executes and adds 6 rejects" before committing. This is the same harness
as §7.3 with a user interface on it.

### 3.8 Browser and WASM

Per `docs/playback2d-v2/wasm-matrix.md`, recorded as a row in its Degraded table:

| Capability | Browser | What actually happens |
|---|---|---|
| Suggested tags | degraded | The evaluator runs on the open demo only (no library, no coordinator, no `DemoCacheDir`), from a walk. Proposals and verdicts live in the in-memory store the cache already uses when the root is null (`DemoCacheStore.cs:513-525`) and die with the tab. Accepting writes to the Tag Store's own session-only mode. The queue header says "session only", in the words the annotation panel uses. The profile is the embedded default; no tuning view. |

---

## 4. Alternatives considered and why not

**Route (b) for the first release: a team-presence provider in CS2DemoKit, detectors as YAML.** Needs
an engine release with a new aggregate family and a language change (a place argument, a time window,
or both; §5.2). Nothing upstream is in flight for it (F16). It also puts the detonation-to-place
resolution, the learned site regions and the confidence arithmetic into the rules layer, which has no
place for a per-map data table today. It is the right home for detectors a team wants to author
itself, and that is why §5.2 specifies it. It is the wrong first step for six detectors whose numbers
still need to be tuned against hand tags. Recommendation, matching D6: (a) first, (b) when palette
authors ask for their own detectors.

**YAML over `player.place` with per-player captures.** One could write, per subject, "I entered
BombsiteA at tick t" as a `keep: list` capture and try to count teammates at round end. The list is
per subject, comparison across subjects does not exist, and `compute:` is round-end or live per subject
(§2.7). It cannot be made to say "four of us". Rejected on the spec, not on taste.

**Per-slot window counting as the only mode.** More precise in principle (it catches a group that
arrives staggered over four seconds without ever being four at one instant). Measured within one to
three fires of the instantaneous count on every demo and every grid cell. The instantaneous count runs
on the Round Index token with no second walk. Ship the instantaneous count; keep the window variant as
an option in the profile for teams with per-slot data.

**Storing proposals as Tag Store instances in a third namespace.** Tempting because the Tag Track could
then render them for free. Rejected: proposals are rebuilt wholesale on every parameter change and
would churn the Tag Store's history; verdicts would need a fourth place to live; and every Tag Store
consumer (the Matrix, the record panel, export) would need to learn to skip a namespace. A proposal is
not a tag until someone says so.

**A classifier.** Out of scope per the original roadmap and the plan. Also unhelpful here: the value of a
suggestion is that the coach can read why it fired.

**Sampling at one second from the walk instead of stride 8.** The stride does not reduce decode time
(measured: 1.6 s at both 8 and 64), and the eighth-second samples are what the per-slot form and the
detonation cloud want. Keep stride 8 for the fallback walk; the Round Index chooses its own cadence.

---

## 5. External and engine changes required

**For the first release: none.** Route (a) runs on CS2DemoKit 0.12.0 as packaged, the current
AssetBaker bundle schema, and no CSVG change. Zone Baking improves it (exact detonation place, generated
site regions) but is not a dependency.

### 5.1 Contracts requested from sibling designs (this repo, not external)

- The Round Index: a decodable alive-only place-count token per side per sampled second, or per-slot
  rows. Stated in §2.9. If neither, this design's fallback walk applies.
- Tag Store: a `provenance` object on an instance, free-form JSON preserved on round trip, in the human
  namespace. Stated in §3.4.
- Content Identity: `demo.sha256` on every record. Already a Phase 0 item.

### 5.2 Upstream proposal for a later phase: team presence in rules v2 (route (b))

Filed against `CS2OpenDev/CS2DemoKit` when D6's second half opens. The proposal, in the
shape of §2.8:

1. **A per-team place index** beside the alive index in `PlayerContextIndex`: `CountInPlace(team,
   place)` and `CountInPlaces(team, ISet<string>)`, maintained by one writer, the `entity.pawn.place`
   provider's change edge, so the single-writer property the alive index documents is kept.
2. **Two new B6 members**, `round.team.in_place` and `round.enemies.in_place`, of `AggregateKind`
   `TeamInPlace` / `EnemyInPlace`, taking a **place argument**. The language has no function with a
   string argument on a context member today, so this is one of two spellings to decide between: (i) a subscript on the member, `round.team.in_place["BombsiteA"]`, mirroring the map-define
   subscript the vocabulary wave already un-reserved (§3.4 of the spec); or (ii) a closed function,
   `team_in(place_or_list)`, which extends §3.7's set by one entry. Either lowers to a
   `RoundTeamAggregateNode` with a bound place set. A `define:` list as the argument gives the site
   region for free, and a per-map define table gives the learned region a home in the ruleset.
3. **A window primitive** is the harder half. "N teammates entered the region within T seconds" needs
   arrival times, which a live aggregate does not keep. The cheaper spelling that covers the measured
   cases is not a window at all: `while: round.team.in_place[site_a] >= 4` as a gate and a `capture:`
   of `match.tick` at its rising edge. §3.3 shows the instantaneous count is within three fires of the
   window count, so the proposal asks for the aggregate only and leaves arrivals to a second issue if a
   team ever needs them.
4. **Catalog and hashing.** The new members join `B6RuleIds.Members`, the catalog generator emits
   them, and the place argument is identity-bearing in the `RuleHasher` preimage (a rule over
   `BombsiteA` and one over `BombsiteB` must hash apart).

What (b) does not give: detonation place (needs a world-to-place resolver the rules layer lacks),
confidence (a `compute:` can produce a number, but the evidence list is app-side), and the verdict
loop. Those stay in the app under either route. The realistic end state is (a) for the shipped six and
(b) for team-authored extras, both feeding the same proposal store through the `IProposalDetector`
seam with a `YamlRulesetDetector` adapter that reads highlight firings as proposals.

### 5.3 Small upstream corrections: **filed as CS2DemoKit #58** (2026-09-24, together with The Round Index's `PositionSample` proposal)

Neither blocks anything; both cost a reader time.

- `PositionSample.Tick` is documented as "the frame's server tick" (`CS2DemoKit.Parser.xml:4027`).
  Measured: it runs 1 to `TickCount` and equals `GameEvent.GameTick`'s clock, not `ServerTick`. The doc
  should say frame clock, and say it is the same clock as `GameEvent.GameTick`.
- `PositionSample.Place` is documented as null before the field is first networked. Measured on four
  demos: never null, empty string in 0.18 to 0.75 % of samples. Either normalise `""` to null in the
  sampler or document the empty string.
- `PositionSampler` is documented as "every live pawn". Measured: dead players' pawns keep producing
  samples for the rest of the round (166 of 169 dead slots on de_nuke). A `PositionSample.IsAlive`
  flag, or a sentence saying that consumers must filter by `player_death`, would save every consumer
  the discovery.

---

## 6. Risks and unknowns

| Risk | Likelihood | Effect | Mitigation |
|---|---|---|---|
| The Round Index token is not decodable or not alive-only | medium | the execute, default and fake detectors need the fallback walk, about 2 s per demo, and the index gives no saving | §3.2 fallback is designed in; the request is in §2.9 and §8 for the integrator |
| Learned site regions over-reach (`Middle` on two maps) | high, measured | executes fire on mid control | the spawn filter cuts the worst of it; the profile overrides the rest; Zone Baking replaces learning with adjacency |
| Non-plant fires are failed executes, not false positives, and the plant-based numbers understate precision | certain | the numbers in §3.3 are bounds, not truth | §7.3 hand tagging is the only fix and is scheduled before the parameters are called final |
| HLTV and POV demos differ (place field presence, `player_team` cadence) | unknown, F6(c) | detectors silent or wrong on pro demos | Place Names From The Pawn establishes the place field per source; the side resolver already has the `OldTeam` path; test on one HLTV demo when one is re-downloaded |
| Round renumbering on re-parse breaks proposal identity | low | a rejection is forgotten | the verdict re-match by nearest trigger tick in §3.4 |
| `inferno_startburn` has no thrower | certain | inferno detonations count for no side | opener ignores them unless attributed; when the analysis layer's projectile attribution is reachable outside a rules run, use it |
| A team tunes itself into a noisy queue | medium | the feature is ignored | the tuning view shows recall and precision before save; `Ctrl+Y` is gated on the confidence filter |
| Keymap collisions with the Tag Palette's future hotkeys | medium | a key does two things | the six keys are claimed in this document; the palette design must check `Playback2DKeymap` first, and the static constructor throws on a duplicate |
| Overtime and warmup rounds | low | proposals in rounds nobody tags | rounds with fewer than four players per side are skipped, which drops warmup; overtime is a real round and is kept |

Unknowns to close during build: the exact per-side token shape from The Round Index; whether the Tag
Store's editor is available to pre-fill from a proposal or needs a small adapter; the cost of the
in-memory re-run behind the tuning view on a fifty-demo verdict set (expected under 10 s from cached
occupancy, unmeasured).

---

## 7. Test and verification strategy

### 7.1 Unit tests on synthetic occupancy

Each detector gets a fixture builder that writes a `RoundOccupancy` by hand (five T slots, a place per
second) and asserts fires, windows, labels and the confidence factors. Cases: the four-at-once execute;
staggered arrivals that never reach four at one second (instantaneous mode must not fire, window mode
must); a rush at second 12; a default that becomes an execute at 55; a two-player fake at B before an A
execute; an opener of three flashes in 1.5 s and one of three across 3 s; a retake with one alive CT and
with three. Every parameter in the profile has a test that moves it and sees the change.

### 7.2 Golden fixtures from real demos

The probe's occupancy fold for one round of each of the four measured demos is committed as a JSON
fixture (a few kilobytes per round, no demo bytes), together with the proposals the shipped profile
produces. A snapshot test pins them. Recapture is a deliberate act with a diff, as for the Playback2D
goldens. `assets/tour/sample-de_nuke.dem` is not used for anything here.

### 7.3 Validation against hand-tagged rounds

The gate before the parameters are called final, and the data the tuning view reads.

1. Three demos, one map each from the replays folder (de_inferno 10329, de_ancient 10477, de_dust2
   10329), every scored round. Two taggers, independently, using the Tag Palette with the six codes and
   the site label, no suggestions shown. Roughly 70 rounds, about two hours per tagger.
2. Inter-tagger agreement per code (Cohen's kappa). A code under 0.6 has a definition problem, not a
   detector problem, and goes back to the vocabulary before the detector is tuned to it.
3. Against the agreed set: recall and precision per detector at the shipped profile, with a match
   counted when the code and site agree and the claim window overlaps the human window by at least
   half of the shorter one. Window offset statistics (proposal start minus human start) tune `lead`
   and `lag`.
4. Targets for the first release, per detector: recall at least 0.7 and precision at least 0.6 on the
   agreed set for execute, opener and retake; at least 0.5 and 0.5 for default and fake, which are
   softer labels. Miss the target, publish the number and the parameter that moves it, and ship with
   the number in the queue header's tooltip.
5. Repeat on one HLTV demo when one exists, as a smoke test only.

### 7.4 Round trip and provenance

Accept a proposal, reload, assert the Tag Store instance carries the provenance block and the verdict
file marks it accepted; change a parameter, rebuild, assert the rejected proposal is not re-offered and
the accepted instance is untouched; re-parse with a different `frameCount`, assert the verdict re-match
finds the same proposal within 5 s.

### 7.5 Keymap and browser

`Playback2DKeybindConflictTests` already asserts the shell list; adding the six bindings to the table
is covered by the static constructor's throw. The browser row in §3.8 is verified by hand on the
published head with the rest of the wasm-matrix checklist, and its automated proxy is a test that the
service, given a null cache root, keeps proposals and verdicts in memory across accept and reject.

---

## 8. Decisions

1. **Route.** Confirm route (a) for the first release and (b) as a filed upstream proposal (§5.2) with
   no engine work until a team asks to author detectors. This is D6's recommendation restated with
   measurements.
2. **Execute defaults.** Ship strict (`N=4, T=4 s`) as proposed, or loose (`N=3, T=6 s`) with more
   recall and more rejects. The tuning view lets a team move it, so this is only the first
   impression.
3. **Occupancy source.** Ask The Round Index design for a decodable alive-only per-side token (or
   per-slot rows), or accept the fallback walk as the permanent source and let the index stay opaque.
4. **Store location.** `suggestions/` beside `demos/` under the cache root with its own fingerprint,
   as proposed, or fold proposals into `DemoCacheRecord` as a fourth tier. The fourth-tier version
   ties staleness to the rules fingerprint and grows every record.
5. **Keys.** `J`/`K`/`Y`/`N`/`Enter`/`Ctrl+Y` for the queue, claimed now in `Playback2DKeymap` before
   the Tag Palette designs its own. And a note for D7: `F` is taken by follow.
6. **Provenance shape.** A `provenance` object on the Tag Store instance (proposed) versus a separate
   label in the parser namespace. The object keeps the human namespace clean and survives export.
7. **Validation before or after Tag Palette.** §7.3 needs the palette to tag by hand. Either the
   validation waits for the palette (recommended, it is the same phase) or a throwaway tagging form
   ships with the probe.
8. **File the three upstream doc corrections** in §5.3 now, or batch them with the route (b) issue.

---

## 9. Effort estimate and sequencing

| Step | Depends on | Estimate | Output |
|---|---|---|---|
| 1. `RoundOccupancy`, the walk-based builder, the side resolver, the alive filter, the detonation cloud | nothing (Round Index optional) | 2 days | the input layer, unit-tested on synthetic data and pinned on one real round |
| 2. Site regions: learned table with spawn filter, profile overrides, per-map file | step 1 | 1 day | `site-regions.<map>.json` |
| 3. The five detectors and the fast/rush label | steps 1, 2 | 2 days | `IProposalDetector` implementations, §7.1 tests, §7.2 goldens |
| 4. Proposal store, verdicts, identity re-match, `SuggestedTagsService` as an evaluator, index `SuggestionCount` | step 3, Content Identity | 2 days | `suggestions/*.json`, background rebuild on fingerprint change |
| 5. Proposal Track, queue panel, keymap actions, accept/edit/reject into the Tag Store with provenance | step 4, Tag Store, Tag Track | 3 days | the user-facing loop |
| 6. Profile file, tuning view in Settings, in-memory re-run | steps 4, 5 | 2 days | team tuning |
| 7. Validation (§7.3): tagging, agreement, recall and precision, parameter adjustment | step 5, Tag Palette | 2 days plus two taggers' time | the numbers, and the final shipped profile |
| 8. Browser row, wasm-matrix update, docs page in the style of `annotations-format.md` for the two files | step 5 | 1 day | documentation |

About three weeks of one person, with step 7 gated on the Tag Palette. Steps 1 to 4 can start as soon
as Content Identity has merged and do not need the Round Index; if the index lands with a decodable
token, step 1's builder gains a second source in half a day. Route (b) is not scheduled; its issue is
filed at step 8 and picked up when D6's second half opens.

Sequencing against the plan: this is a Phase 2 item after Tag Store and Tag Track. Steps 1 to 3 have
no UI and could run during Phase 1 as background work, which would let the validation data exist
before the palette ships.

---

## 10. Sources

Tree (`main` at `d90ec9f`):

- `plan.md`: §0, F1, F6, F9, F10, F11, F14, F15, F16, F17, §3 "Suggested Tags" (line 348), D6, D7.
- `docs/playback2d-v2/design.md` and `docs/plugins/plugin-system-design.md` (structure and style).
- `docs/playback2d-v2/annotations-format.md` (the `clock` block, forward compatibility).
- `docs/playback2d-v2/wasm-matrix.md` (capability matrix convention).
- `docs/rules-v2/rules-v2-spec.md` §3.1, §3.4 (line 221), §3.7 (254), §4 (270, 338, 372, 409), §5.
- `rules/highlights_position.rules.yaml:26-56`.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs:11-31, 367, 500-525, 559-572`.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs:84-89, 148-193, 200-216, 415-419`.
- `src/App/DemoViewer.NET/Services/DemoProcessing/IDemoEvaluator.cs:22`; `DemoEvaluationCoordinator.cs:74, 158`.
- `src/App/DemoViewer.NET/Modules/Highlights/HighlightsModule.cs:31-61`; `HighlightScanService.cs:44, 152, 609`; `RulesHighlightHarvester.cs:107-119`.
- `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DModule.cs:23-51`; `Playback2DKeymap.cs:14-39, 307-400`; `Playback2DTabViewModel.cs:328-331`.
- `src/App/DemoViewer.NET/Modules/Playback2D/Timeline/ModuleTimelineData.cs:209-233`; `Playback2DTimelineViewModel.cs:148, 317`; `BombTrack.cs`.
- `src/App/DemoViewer.NET/Views/Playback2D/TimelineControl.axaml.cs:95-104`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Timeline/ITimelineTrack.cs:7-41`; `TimelineMarker.cs:24-30`; `ITimelineData.cs`; `AnnotationTrack.cs`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationStore.cs:109`; `src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs:926-938`.
- `src/App/DemoViewer.NET/Features/FeatureCatalog.cs:147-169`; `Configuration/AppSettings.cs:396`; `Services/AppPaths.cs:143, 200`.

CS2DemoKit 0.12.0, packaged (`~/.nuget/packages/cs2demokit.*/0.12.0/lib/net10.0/`):

- `CS2DemoKit.Parser.xml`: `PositionSample` (3998-4040), `PositionSampler.Walk` (4042-4076), `GameEvent` (4222-4335), `ParsedDemo.AllGameEvents` (5074), `ParsedDemo.Players` (5125).
- `CS2DemoKit.Analysis.xml`: `ClipRound`, `ClipRounds.Derive`, `ClipWindows` (about 4950-5070); `DemoSourceProfile.*Detonate` (246-423); `inferno_startburn` thrower note (8860).

CS2DemoKit engine source, `origin/main` in `C:\dev\CS2DemoKit` (read with `git show`, the working
branch is stale):

- `src/CS2DemoKit.Analysis/Plugins/BuiltinProviderSpecs.cs:63-72` (`entity.pawn.place`).
- `src/CS2DemoKit.Analysis/Building/B6Aggregates.cs:14-56`; `Nodes/RoundTeamAggregateNode.cs`; `Building/PlayerContextIndex.cs:68-113, 181`.
- `src/CS2DemoKit.Parser/EntityTracking/PositionSampler.cs:95`.

Measurements (scratch project, not in the tree):

- `tagprobe\Program.cs` (v3), outputs `v2_*.txt`, `v3_*.txt` in the same folder.
- Demos, read-only, from `C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\replays`:
  `match730_003731893271710924851_1024675027_129.dem` (de_nuke, build 10231, 154,869 frames, 23 rounds);
  `match730_003752202995232669993_0995639494_129.dem` (de_inferno, build 10329, 131,613 frames, 18 rounds);
  `match730_003744626821098897655_0217256255_410.dem` (de_dust2, build 10329, 168,485 frames, 23 rounds);
  `match730_003765251335658668110_1533655444_405.dem` (de_ancient, build 10477, 210,945 frames, 29 rounds).
- Method: parse with `DemoParser.Parse`; rounds from `ClipRounds.Derive`, ended at `round_officially_ended`; sides from `player_team` with the `OldTeam` rule; occupancy at 1 Hz from `PositionSampler.Walk(demo, 8, int.MaxValue)`, first sample per (slot, second), alive-only; detonation place by nearest cloud point within 400 units; execute grid over `N in {3,4,5}`, `T in {4,6,8}`, window versus instantaneous, with and without the site-touch condition; plants as weak truth.
