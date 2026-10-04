# The Round Index: design

> **Status: APPROVED 2026-09-23**, on the design's recommended answers (§8), with two
> conditions from earlier decisions discharged in this revision: **D2** re-measured over a real
> hundred-demo corpus (§2.7; the synthetic numbers in §2.6 stay for comparison) and **Z-1** from the
> Zone Baking review folded in as the opt-in resolver-token mode (§3.14). Also folded in: Round Facts
> is rules-driven and has no extractor, so the fallback in §3.4 is withdrawn; Find Rounds Like This
> takes `Ctrl+F` (plan D7). No code.

**Work item:** The Round Index (plan §3, Phase 1;
findings F1, F9, F15, F17; decision D2). · **Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0. ·
**Written:** 2026-09-23. · **Builds on:** `designs/round-facts.md` (the foreign key, `RoundPhases.At`,
`SideFacts.Slots`, `KillStep`), and names its seams to `designs/zone-baking.md`,
`designs/team-identity.md`, `designs/suggested-tags.md` and `designs/tag-store.md`.

This document designs the derived store behind Situation Search: one row per (demo, round, sampled
tick) carrying the place-count token per side, plus the key into Round Facts. Everything measured
below was measured on untrimmed Valve matchmaking demos from the Steam `replays` folder with a scratch
project (§10). The tour sample was not used for anything.

**The short version.** Sample every live round once per second on the frame clock, encode each
side's alive players as a sorted `Place:Count` string, and write one compact JSON sidecar per demo
beside the cache (55 to 90 KB, about 1,300 to 2,300 rows). The indexing job is a third
`IDemoEvaluator` next to Library and Highlights, riding the same held parse under the same gate at
1.0 to 3.1 s per demo. Queries do not scan sidecars: a per-session in-memory index built once at
startup (1.5 s for a thousand-demo synthetic corpus, about 40 MB) answers an exact or tolerant
match in microseconds, which is what the live count needs. D2 is therefore decided for JSON, with
the measured point at which SQLite would start to pay for itself stated in §4. Two findings changed
the shape from the brief: `PositionSampler.Walk` yields dead pawns (24 to 29 percent of one-second
rows carry one), so the token is filtered through Round Facts' kill timeline; and the index itself
yields a usable place-adjacency graph and place centroids, so the Tolerance Slider and the Query
Canvas snap both work before Zone Baking lands.

---

## 1. Problem and scope

Finding F1 (`plan.md:61-70`): the round-index token needs no Zone Baking because
`PositionSampler.Walk` streams every pawn's `m_szLastPlaceName`. The plan's entry
(`plan.md:266-273`) asks for one row per (demo, round, sampled tick), a per-side place-count token
that matches exactly, a foreign key into Round Facts, a cadence decision, a storage decision (D2,
`plan.md:586-591`), size per demo and rebuild triggers, with the done bar "a corpus indexes at a
stated rate, and a token lookup over it returns in under a second".

| Consumer (plan name) | What it needs from this store |
|---|---|
| Query Canvas (`plan.md:275-280`) | a token identical to what the index stores; without Zone Baking, a place centroid per map to snap a dropped token to |
| Find Rounds Like This | the same encoder applied to the current tick's positions |
| Search Filters And Live Count (`plan.md:287-291`) | a count that equals the result set, updated as tokens move, intersected with `RoundFactsFilter` |
| Tolerance Slider (`plan.md:293-296`) | adjacent-area matching over a place-adjacency graph; exact only when no graph exists |
| Result Cards And Walking | hits as (demo, round, matched tick) so the card seeks to the tick minus ten seconds |
| Overlay View, Setup Heatmaps By Buy, Post-Plant And Retake | the matching rows with their ticks, to render positions from the demo |
| Watched Situations (`plan.md:308-310`) | a hook when a new demo indexes, and "new since" after a restart |
| Suggested Tags (`designs/suggested-tags.md` §2.9, §5.1) | a decodable, alive-only per-side token per sampled second |

**In scope.** The token, the sampling rule, the sidecar, the in-memory index and its query API, the
evaluator and its triggers, the adjacency and centroid summaries, the live-count contract, the
Watched Situations hook, browser behaviour, tests.

**Out of scope.** The Situation Search tab itself (Query Canvas, Result Cards, Overlay View are their
own build items; §3.10 names the module they plug into). Team identity joins ("their setup" is
Team Identity's `DemosAgainst`). Place resolution from an arbitrary world point (Zone Baking).
Per-player position rows (the demo stays the source; the index says where to look, not what it looked
like).

---

## 2. What exists today (cited)

### 2.1 The engine's position walk

- `PositionSampler.Walk(demo, frameStride, maxFrames)` (CS2DemoKit.Parser 0.12.0 XML docs, and
  `src/CS2DemoKit.Parser/EntityTracking/PositionSampler.cs` at `origin/main`) yields
  `PositionSample(FrameIndex, Tick, PlayerSlot, Position, Place)` for every pawn on every
  `frameStride`-th frame. `Tick` is `DemoFrame.ServerTick`, the frame clock. The stride subsamples
  output only; every frame is decoded. The doc warns it is a frame stride, not a tick stride.
- It enumerates through `PawnLookup.ForEachLivePawn` (`PawnLookup.cs:42-87` at `origin/main`), and
  "live" there means "has a valid controller handle", not "alive": the sweep filters on class name
  and controller identity and never reads `m_lifeState` or `m_iHealth`. **Measured (§2.6): dead
  pawns are present in 24 to 29 percent of one-second samples on all four demos.** `PositionSample`
  carries no alive flag and no team.
- The app reads alive state as `m_lifeState != 0 || m_iHealth <= 0`
  (`src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs:2505-2513`).

### 2.2 The cache and the library evaluator

- `DemoCacheStore` (`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs`): `index.json`
  plus one sidecar per demo under `demos/` named by `StableKey(path)` (`:500-511`), atomic writes
  (`:559-573`), in-memory records when there is no cache root (`:48-59`), `Changed(path?)` per
  mutation or once per batch (`:157-170`), `TryLoadRecord` cached as JSON text at capacity 1
  (`:80-90`, `:200-257`), `LoadRecords` as the one bulk reader and not for the UI thread
  (`:283-318`), `LoadOrCreate` discarding every tier on identity drift (`:263-281`).
- `DemoCacheRecord` has three stamped tiers with schema constants (`DemoCacheModels.cs:203-206`),
  a `TierStamp` with `ComputedAtTicks` (`:55-65`), `NeedsAnalysis` derived from a fingerprint
  rather than stored (`:309-327`, the reasoning at `:311-323`), and `DemoAnalysisState.Failed`
  excluded from re-queue on purpose (`:320-323`). `DemoCacheIndexEntry` mirrors the stamps and the
  fingerprint so backlogs derive without opening sidecars (`:401-419`, `:431-440`).
- Round Facts (`designs/round-facts.md` §3.5) adds a fourth stamped tier `RoundFacts` with
  `RoundFactsSchema = 1`, a `dv-frame-clock` header and `ClassifierVersion`, and the index mirror
  `NeedsRoundFacts()`. Its record (§3.3) carries per round `FreezeEndTick == ClipRound.StartTickFrameClock`,
  `EndTick`, `IsLive`, `SideFacts.Slots` per side, and `Kills` as `KillStep(Tick, VictimSlot, ...)`.
- `DemoLibraryService : IDemoEvaluator` (`src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs:47`,
  members `:188-270`): `Wants` is backlog membership (`:199-205`), `Evaluate` runs with the parse
  held (`:215`), `IndexTier2Core` (`:1138`) posts metadata, replays `CCSTeam` (`:1403-1460`) and fans
  the parse to the other evaluators (`:1190-1192`), `WriteTier2ToDemoCache` (`:1302-1354`) writes the
  tier. `HighlightScanService` is the second evaluator (`Modules/Highlights/HighlightScanService.cs:152-215`):
  `Wants` gated on a per-row staleness check plus an opt-in (`:159-169`), `OrderHint` from
  `ModifiedTicks` (`:190`), `PendingPaths()` as its candidate snapshot (`:498`).
- `DemoEvaluationCoordinator` (`Services/DemoProcessing/DemoEvaluationCoordinator.cs`): `Consider`
  polls every evaluator's `Wants` and submits one request per interested evaluator, coalesced by path
  (`:73-108`); `FanOutParsed` hands an already-held parse to every evaluator's
  `OnParsedOpportunistically`, not gated on `Wants` (`:140-175`). Both are registered at
  `App.axaml.cs:705-725` with the candidate universe as the union of each evaluator's pending
  snapshot. `HeavyJobGate` (`Services/HeavyJobGate.cs:37`) keeps one heavy parse at a time.
- `IDemoEvaluator` (`Services/DemoProcessing/IDemoEvaluator.cs:22-73`): `Evaluate` must finish
  synchronously inside the gate slot; failures are isolated by the queue.

### 2.3 Clocks and levels

- Everything positional is frame clock (F15, `plan.md:180-184`). `ClipRound.StartTickFrameClock`
  is `GameEvent.GameTick` (CS2DemoKit.Analysis XML, `ClipRound`). `PositionSample.Tick` is
  `DemoFrame.ServerTick`, the same clock; the scratch probe aligned the two directly (§2.6).
- The annotation sidecar's `clock` block (`docs/playback2d-v2/annotations-format.md:24-36`) is the
  house header: `{ kind: "dv-frame-clock", tickRate, frameCount, firstTick, lastTick }`, plus
  `demo.sha256` as the only matching field.
- `MapSpace.LevelQuantum = 64` and `QuantizeZ` (`src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapSpace.cs:35`, `:81`)
  define the Z grid the level model and the annotation anchors use. Level keys are quantized band
  lower bounds, never a player's Z (`:81-100`).

### 2.4 Modules, keymap, features, browser

- New surfaces are first-party `IWorkspaceModule`s: `HighlightsModule` (`Modules/Highlights/HighlightsModule.cs:31-62`),
  delegate-injected VM, `ViewModelFactory` never `DataContext`; feature ids in
  `Features/FeatureCatalog.cs` (`tab.highlights` at `:62-66`); desktop-only ids in one place,
  `ShellModuleFeatureGate.DesktopOnlyIds` (`Features/ShellModuleFeatureGate.cs:59-64`).
- `Playback2DKeymap.cs:333-335` binds `F` and `Shift+F` to follow cycling, so D7's `F` is taken
  (also noted by `designs/suggested-tags.md` §8).
- `docs/playback2d-v2/wasm-matrix.md`: the processing queue is absent on the browser (`:128`), the
  demo library and cache are session-only and nothing in the UI says so (`:119`), radar art is absent
  (`:117`). `AppPaths.ConfigRoot` is null there (`Services/AppPaths.cs:54-66`).
- `HighlightsSettings.BackgroundScan` (`Configuration/AppSettings.cs:206-211`) is the precedent for
  an opt-in library sweep ("a 200-demo library is ~30 min of churn").

### 2.5 Sibling designs this one must agree with

- `designs/round-facts.md` §3.7: the foreign key is `(StableKey or DemoSha256, RoundFacts.Number)`;
  `RoundPhases.At(facts, tick)` gives the phase per sampled tick; `IRoundFactsSource.Query(RoundFactsFilter)`
  is the cross-demo read Search Filters And Live Count intersects with.
- `designs/zone-baking.md` §3.2 and §3.4: `zones.json` carries `adjacency` (undirected place pairs
  sharing a nav connection) and `PlaceResolver.Adjacent(placeId)` / `AreAdjacent`; §3.6 says the
  Round Index does not carry `zonesVersion` because its token comes from the pawn; §3.7 says without
  zones the Query Canvas snaps to the index's sampled place centroids.
- `designs/team-identity.md` §3.7: Watched Situations reads `DemosAgainst(opponent)` and
  intersects with "the Round Index's newly indexed set".
- `designs/suggested-tags.md` §2.9 and §5.1: the token must be decodable and alive-only, per sampled
  second; if so the execute, default, fake and retake detectors run over the index with no second walk.
- `designs/tag-store.md` §2.9 and §4.1: a thousand-sidecar scan measured at 222 ms warm; D2 is left
  to this design; if this design adopts SQLite, tags project into it additively.

### 2.6 Measured (scratch project `round-index`, 2026-09-23)

Four untrimmed Valve matchmaking demos from the Steam `replays` folder, Release build, warm figures.
The probe replays every frame through `EntityTrackerFactory.CreateCurated()` and
`PawnLookup.ForEachLivePawn`, reading `m_lifeState`, `m_iHealth`, `m_iTeamNum`, `m_szLastPlaceName`
and `PositionUtil.CellToWorld`, and samples every 8 ticks from each `round_freeze_end` to that
round's `round_officially_ended`.

| Demo | Map | Build | Frames | Rounds | Parse | Walk (8-tick base) | Frames sharing a tick | Max frames per tick |
|---|---|---|---|---|---|---|---|---|
| `match730_003731893271710924851_1024675027_129.dem` | de_nuke | 10231 | 154,869 | 23 | 670 ms | 1,401 to 1,510 ms | 22,367 | 39 |
| `match730_003844252717140672725_0377894676_389.dem` | de_dust2 | 10896 | 106,901 | 19 | 517 ms | 1,324 ms | 43 | 16 |
| `match730_003842233788306292960_0260929275_408.dem` | de_mirage | 10896 | 118,309 | 21 | 514 ms | 1,453 ms | 46 | 16 |
| `match730_003842442070597828830_0022157567_392.dem` | de_inferno | 10896 | 190,423 | 29 | 792 ms | 3,066 to 3,145 ms | 65 | 16 |

**Cadence** (rows per demo, sampled from freeze end to officially-ended; "rle" is rows after
collapsing consecutive identical (CT, T) pairs within a round; "distinct pair" is distinct (CT, T)
tokens in the demo; token length is with `:1` omitted):

| Cadence | Nuke rows / rle / distinct pair | Dust2 | Mirage | Inferno | Rows per round (range) |
|---|---|---|---|---|---|
| 0.125 s | 12,471 / 1,419 / 1,150 | 10,215 / 1,428 / 1,256 | 10,691 / 1,220 / 1,097 | 17,889 / 2,014 / 1,715 | 509 to 617 |
| 0.25 s | 6,241 / 1,307 / 1,087 | 5,111 / 1,318 / 1,177 | 5,352 / 1,161 / 1,041 | 8,952 / 1,872 / 1,611 | 255 to 309 |
| 0.5 s | 3,126 / 1,118 / 971 | 2,560 / 1,119 / 1,025 | 2,681 / 1,015 / 914 | 4,486 / 1,636 / 1,425 | 128 to 155 |
| **1 s** | **1,570 / 864 / 774** | **1,282 / 831 / 786** | **1,343 / 796 / 721** | **2,250 / 1,267 / 1,131** | **64 to 78** |
| 2 s | 791 / 594 / 541 | 645 / 546 / 515 | 676 / 551 / 499 | 1,134 / 853 / 773 | 32 to 39 |
| 4 s | 402 / 361 / 318 | 328 / 308 / 288 | 345 / 317 / 285 | 575 / 523 / 464 | 16 to 20 |

Average token length per side: 31 (nuke), 43 to 47 (mirage, dust2, inferno) characters.

**Dead pawns.** At 1 s, rows with at least one dead pawn present: 382 of 1,570 (nuke), 313 of
1,282 (dust2), 382 of 1,343 (mirage), 447 of 2,250 (inferno). In every such row the token changes
when dead pawns are counted. A `player_death`-derived alive set (victim slot dead from the event's
`GameTick` on) disagrees with `m_lifeState` on 3 of 1,570 rows (nuke) and 2 of 2,250 (inferno), all
the same way: the event fired at the sampled tick and the entity had not yet flipped. No row was
dead by state and alive by events.

**Place field.** `m_szLastPlaceName` was null on 0 alive samples on nuke, dust2 and mirage, and on
45 of 118,887 (0.04 percent) on inferno. Every alive pawn had `m_iTeamNum` 2 or 3. Vocabulary: 29
places on nuke, 24 dust2, 23 mirage, 23 inferno. Every place spans more than one 64-unit Z bucket on
at least one map (nuke `Ramp` spans 7, `Silo` 6), so a place is not a floor.

**Empirical adjacency.** Counting, per alive player, place changes between consecutive one-second
rows within a round, nuke yields 45 distinct unordered pairs, 36 seen three or more times, headed by
`CTSpawn-Outside` 115, `Outside-TSpawn` 115, `Hell-Outside` 86, `Lobby-Outside` 82,
`Heaven-Rafters` 76, `Admin-Hell` 75, `Admin-Ramp` 62; the four singletons are skip-throughs
(`Crane-Rafters`, `Mini-Silo`, `Hut-HutRoof`, `Crane-HutRoof`). Inferno: 41 pairs, 38 seen three or
more times, headed by `BombsiteB-Ruins`, `LowerMid-TSpawn`, `LowerMid-TRamp`, `Banana-BombsiteB`.

**`PositionSampler.Walk` cost** on the dust2 demo, folding to one row per second: stride 1,
1,414 to 1,761 ms over 814,344 samples; stride 4, 1,075 to 1,109 ms over 203,589 samples, keeping
every one-second row; stride 8, 1,021 ms, dropping 2 of 1,649 rows; stride 16, 991 ms.

**Sidecar size at 1 s**, proposed shape, `:1` omitted (add roughly 15 percent for explicit counts,
§3.2): rows as `[offset, ct, t]`, indented 149 to 246 KB, compact 70 to 118 KB, gzip 8.5 to 13 KB;
runs as `[from, to, ct, t]`, compact 46 to 79 KB, gzip 6.7 to 10 KB.

**Thousand-demo synthetic corpus** (24 rounds, 70 rows per round, tokens drawn from a 28-place
Zipf vocabulary with a 35 percent per-second change rate, indented JSON, 1,680,000 rows, 164 MiB,
167 MB on disk):

| Method | Build | Exact lookup on one side token | Notes |
|---|---|---|---|
| Scan every sidecar per query (`System.Text.Json`, reflection) | none | 987 to 1,209 ms warm, 2,064 ms first pass | fails the one-second bar as soon as the disk is cold or the library is bigger |
| In-memory inverted index built from the sidecars once | 1,509 ms (one pass) | 11 µs (1,000 lookups in 11.3 ms) | 1,172,818 postings, about 21 MiB estimated; 149 MiB managed heap at the peak of the build because the probe deserialized every document whole |
| SQLite (`Microsoft.Data.Sqlite` 10.0.0, WAL, three indexes) | 3,542 ms | 2.04 ms cold open, 0.08 to 0.12 ms warm | 179.5 MiB file; deleting one demo's rows 14 ms; the restore pulled `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 with an open NU1903 advisory (GHSA-2m69-gcr7-jv3q) |

The same probe at 100 rows per round (2.4 M rows, 234 MiB) scanned in 1,306 to 2,116 ms, built the
in-memory index in 2,064 ms and the SQLite file (257 MiB) in 4,980 ms.

---

### 2.7 Measured on a real hundred-demo corpus (2026-09-23, the D2 condition)

D2 made the storage choice conditional on a scan measured over a **real**
hundred-demo corpus rather than the synthetic thousand in §2.6. The probe's `cadence <demo> <outDir>`
mode wrote one real one-second sidecar (rows shape, indented, `:1` omitted) for each of the 100
newest demos in the Steam replays folder, and a new `scanreal <jsonDir>` mode ran the same three
measurements over those files. Release build, warm figures; 0 of 100 demos failed.

| Corpus | Demos | Maps | Rows | Distinct CT tokens | Indented JSON on disk | Build per demo (parse plus walk, 8-tick base) |
|---|---|---|---|---|---|---|
| Real | 100 | dust2 26, mirage 20, nuke 18, ancient 15, inferno 10, anubis 6, cache 4, overpass 1 | 162,234 | 22,219 | 17.1 MiB (171 KB per demo) | 3,484 ms mean, 356 s total |

Query token: a median-frequency real CT token, `Alley|BombsiteA|BombsiteB:2|SideHall` (17 rows,
9 rounds, an anubis setup).

| Method | Build | Exact lookup on one side token | Size | Notes |
|---|---|---|---|---|
| Scan every sidecar per query | none | **141 to 267 ms** at 100 demos (rep 0 to 3) | the sidecars | Linear in the library: at the synthetic thousand it was 1.0 to 2.1 s; the live count runs a query per token drag, so this fails the one-second bar somewhere between 300 and 700 demos |
| In-memory inverted index built once | **204 ms** (2.0 ms per demo) | **3.7 µs** (1,000 lookups in 3.69 ms) | 118,365 postings, about 3.7 MiB; 38 MiB managed heap at the build's peak in the probe | Chosen. Startup load at the measured 2.0 ms per demo: the local 282-demo folder in about 0.6 s, a thousand demos in 2 s |
| SQLite (`Microsoft.Data.Sqlite` 10.0.0, WAL, three indexes) | 619 ms | 1.72 ms cold open, 0.04 to 0.10 ms warm | **17.9 MiB** file (more than the indented JSON it would replace; about three times the compact-runs sidecars) | Delete one demo 5.8 ms. Same package and native-library caveats as §2.6 |

Two things the real corpus says that the synthetic one could not. First, the token space is far
sparser than the Zipf generator assumed: 22,219 distinct CT tokens across 100 demos, so an exact
whole-side match is rare (a median token hits 9 rounds in 100 demos), which is why "exact" is a
pair-level predicate in §3.7 and the Tolerance Slider exists. Second, the per-demo build cost on
real demos is dominated by the walk, not the sidecar (3.5 s per demo in the probe's 8-tick base
mode; the production stride-4 fold measured 1.0 to 1.1 s in §2.6), so the background index of the
local library is a one-off of roughly 15 minutes and stays off the interactive path.

**D2 is therefore decided on real data: JSON sidecars plus the per-session in-memory index.** The
revisit trigger in §4 is restated with the measured 2.0 ms per demo.

---

## 3. Proposed design

### 3.1 The row

One row per (demo, live round, sampled tick). A sampled tick is `FreezeEndTick + k * cadenceTicks`
for `k = 0, 1, 2, ...` while the tick is below the round's end. Both bounds come from Round Facts
(`FreezeEndTick`, which equals `ClipRound.StartTickFrameClock`; `EndTick`, falling back to the next
round's `FreezeBeginTick`, then the last frame). Freeze time is not sampled (positions are spawns)
and the post-round win panel is not sampled (the seven seconds after `EndTick` carry no situation
anyone searches for). Rounds with `IsLive == false` are skipped.

`cadenceTicks = round(cadenceSeconds * clock.tickRate)`, and `cadenceSeconds = 1`. The cadence is
stated in seconds so a 128-tick FACEIT demo samples at 128 ticks and rows mean the same thing across
sources. Rows are stored as offsets in cadence steps from the freeze end, never as absolute ticks:
`tick = FreezeEndTick + step * cadenceTicks`. Phase per row is not stored; a consumer asks
`RoundPhases.At(facts, tick)`.

**Why one second.** At 1 s a demo has 64 to 78 rows per round and 720 to 1,130 distinct (CT, T)
pairs; halving the cadence doubles the rows and adds only 25 to 30 percent more distinct pairs,
because a five-versus-five arrangement changes on the order of seconds, not fractions of one. At 2 s
the distinct pairs drop by 30 percent: setups held for under two seconds vanish. Suggested Tags'
detectors are specified in seconds (`N players within T = 4 s`) and ask for per-second occupancy.
One second is the finest cadence that costs nothing visible and the coarsest that loses nothing the
consumers ask for. It is a fingerprinted parameter (§3.6), not a constant.

### 3.2 The token: `PlaceCountToken`

Per side, per sampled tick, the alive players on that side grouped by `m_szLastPlaceName`:

```text
token := ""                                   an empty side: nobody alive
       | pair ( "|" pair )*
pair  := place ":" count                      count is always written, including 1
place := any characters except ":" and "|"    the raw pawn string, case preserved
                                              "?" is reserved for a null place
order := pairs sorted by place, ordinal comparison, ascending
```

Examples: `BombsiteA:2|Outside:3`, `?:1|Ramp:4`, `CTSpawn:5`, `` (empty).

Rules, each measured or reasoned in §2.6:

| Rule | Reason |
|---|---|
| Alive players only | 24 to 29 percent of one-second rows carry a dead pawn, and every one of those tokens is wrong otherwise. Suggested Tags asks for alive-only. |
| Alive is decided from Round Facts `Kills`: a slot is dead from the first `KillStep.Tick <= sampledTick` whose `VictimSlot` is that slot until the round ends | Agrees with `m_lifeState` on 99.8 to 99.9 percent of rows; the residue is the death tick itself. Keeps the walk engine-owned (`PositionSampler.Walk`) instead of a second tracker in the app. §5 proposes the engine flag that would remove the join. |
| Side is decided from Round Facts `SideFacts.Slots` at freeze end | A pawn's `m_iTeamNum` is not on `PositionSample`; slots do not change side within a round; spectators (in neither list) are dropped. |
| Null place counts under `?` | Preserves the alive man-count, which Suggested Tags reads from the token. Measured null rate 0 to 0.04 percent on Valve demos; Place Names From The Pawn reports the other sources. A query never targets `?`. |
| Explicit `:1` | One decoder with no special case. Costs about 15 percent of token bytes over the measured lengths. |
| Ordinal sort, raw names | The pawn's string is the canonical vocabulary (Zone Baking §3.2, Callout Aliases). No normalisation, so a token round-trips byte for byte. |
| Two tokens per row, never concatenated | A one-side query touches one side's postings; the Query Canvas rail is five per side. |

`PlaceCountToken.Encode(IEnumerable<(string Place, int Count)>)`, `Decode(string)` returning the
pairs in canonical order, and `TokenVersion = 1` live in `Services/RoundIndex/PlaceCountToken.cs`,
pure and shared by the builder, the query service and Find Rounds Like This (which encodes the
current tick's positions from the 2D scene's alive players by the same function, so the round-trip
in Query Canvas' done bar is one function applied twice).

### 3.3 The sidecar: `.dvri.json` (schema 1)

One file per demo, `<config>/cache/round-index/<StableKey(path)>.dvri.json`, written atomically
with `DemoCacheStore.WriteAtomic`'s temp-and-replace idiom, compact (not indented: 2 to 3 times
smaller and nobody reads it by hand). The stamp lives on `DemoCacheRecord` (§3.6); the payload lives
here, not inside the record, because `TryLoadRecord` deserializes the whole record on every Match
Overview property touch and caches it as text (`DemoCacheStore.cs:80-90`), and 55 to 90 KB of rows
per record would make every Library arrow-key a ten-times heavier read for a surface that never
looks at them.

```jsonc
{
  "schemaVersion": 1,
  "fingerprint": "ri1;cadence=1;token=1;rf=1",      // §3.6; a mismatch means "rebuild", never "reinterpret"
  "demo":  { "sha256": null, "stableKey": "3f9c…", "fileName": "match730_….dem", "sizeBytes": 289436777 },
  "clock": { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 154869, "firstTick": 1, "lastTick": 132516 },
  "map": "de_nuke",
  "cadenceTicks": 64,
  "rounds": [
    { "number": 3, "freezeEndTick": 10746, "endTick": 17138,
      "runs": [
        [0, 4,  "CTSpawn:5",              "TSpawn:5"],          // [fromStep, toStep, ct, t]; tick = freezeEndTick + step * cadenceTicks
        [5, 9,  "BombsiteA:2|Outside:3",  "Lobby:3|Ramp:2"],
        [10, 10, "BombsiteA:2|Outside:2", "Lobby:3|Ramp:1"]
      ] }
  ],
  "places": {                                            // alive samples, for the Query Canvas snap (§3.9)
    "Outside": { "n": 2396, "z": [ [-448, 1180, 61234.5, -812900.2], [-384, 1216, 60110.0, -800012.7] ] }
                                                         // per 64-unit Z bucket (MapSpace.QuantizeZ of the sample Z): [bucket, n, sumX, sumY]
  },
  "transitions": [ ["CTSpawn", "Outside", 115], ["Admin", "Ramp", 62] ]   // unordered pairs, count of one-second place changes by one alive player (§3.8)
}
```

- `runs` tile the sampled steps of the round: run *i* ends at `toStep` and run *i+1* starts at
  `toStep + 1`; a run is one (CT, T) pair held over consecutive samples. Measured, runs are 55 to
  65 percent of the row count. `RoundIndexDocument.ExpandRows(round)` yields one row per step for
  consumers that want per-second occupancy (Suggested Tags).
- `demo.sha256` is null until Content Identity fills `DemoCacheRecord.Sha256`; `stableKey` is the
  join key until then and stays as the file name afterwards (the cache keys by path, F9). A reader
  that has a hash compares it and ignores a mismatching file, as `annotations-format.md` requires.
- `clock` is the annotation sidecar's block verbatim. A `tickRate` mismatch on read is a warning and
  a rebuild trigger, never a reinterpretation (the steps would mean a different second).
- `places` and `transitions` are per-demo partial sums; the per-map fold is exact addition (§3.8,
  §3.9). Z buckets are `MapSpace.QuantizeZ(sample.Z)`, never a level key: level keys are band lower
  bounds that a floor rebuild can move (`MapSpace.cs:81-100`), so the consumer folds buckets into
  whatever bands it has.
- Size: 55 to 90 KB compact per demo at the measured row counts with explicit counts (about 3 KB of
  it the two summaries). A 277-demo library is about 20 MB; a thousand demos about 75 MB.

The document class is `RoundIndexDocument` in `Services/RoundIndex/RoundIndexModels.cs`; the
serialized shape is pinned by a committed fixture and a snapshot test (§7), the way
`AnnotationSchemaSnapshotTests` pins `.dvann.json`.

### 3.4 The builder: `RoundIndexBuilder`

`Services/RoundIndex/RoundIndexBuilder.cs`, `static RoundIndexDocument Build(ParsedDemo demo,
RoundFactsTier facts, RoundIndexOptions options)`. Pure: no I/O, no cache, no UI. Steps:

1. `cadenceTicks = round(options.CadenceSeconds * demo.TickRate)`. Rounds: `facts.Rounds.Where(r => r.IsLive)`,
   each with `[FreezeEndTick, EndTick ?? nextFreezeBeginTick ?? lastFrameTick)`.
2. Per round, build `sideBySlot` from `Ct.Slots` and `T.Slots`, and `deathTickBySlot` from `Kills`.
3. `foreach sample in PositionSampler.Walk(demo, options.FrameStride /* 4 */, int.MaxValue)`:
   skip samples outside every round window; when `sample.Tick >= nextDueTick` for the current round,
   open a new row at `step = (sample.Tick - FreezeEndTick) / cadenceTicks` and set
   `nextDueTick = FreezeEndTick + (step + 1) * cadenceTicks`; add the sample to the open row when its
   slot has a side and `deathTickBySlot[slot] > sample.Tick`. Samples from later frames of the same
   tick belong to the same row (several frames can share a tick, §2.6: up to 39 on build 10231).
   Stride 4 keeps every one-second row on the measured demo and costs the decode floor (1.1 s);
   stride 8 dropped two rows.
4. Close a row: encode both tokens; append to the current run or open a new one. Accumulate
   `places` (alive samples with a non-null place) and `transitions` (per slot, the previous row's
   place versus this row's place, both alive, both non-null, different).
5. Emit the document with the clock header from `demo` and the fingerprint from `options`.

**The index requires the Round Facts rows.** Round Facts is rules-driven (`designs/round-facts.md`
as approved: a shipped `round_facts` ruleset evaluated by the engine, cached in the Analysis tier);
there is no app-side extractor to call, so the first draft's "call `RoundFactsExtractor` when the
tier is absent" fallback is withdrawn. Ordering is by registration: the `roundfacts` evaluator is
registered before `roundindex` in the coordinator list (`App.axaml.cs:716-717`), both ride the same
held parse, and `Wants(path)` for the index is false while the record carries no
`RoundFactsFingerprint`, so the index follows Round Facts by one pass and never races it.
The fingerprint carries `RoundFactsSchema` so a change to `Slots` or `Kills` semantics rebuilds the
index; a change to buy-type thresholds (a `params:` edit) changes Round Facts' own fingerprint but
not the index's, because the index reads only `Slots`, `Kills` and the round bounds.

Cost per demo: the walk at 1.0 to 3.1 s on 107k to 190k frames, on top of the parse the queue
already pays. A 277-demo library indexes in roughly 15 minutes of background time at one demo at a
time, including the Library's own replay and Round Facts.

### 3.5 The evaluator: `RoundIndexEvaluator`

`Services/RoundIndex/RoundIndexEvaluator.cs`, `IDemoEvaluator` with `Id = "roundindex"`, registered
third in the coordinator list at `App.axaml.cs:716-717` and unioned into the candidate universe through
`PendingPaths()` like Highlights.

| Member | Behaviour |
|---|---|
| `Wants(path)` | `entry = cache.TryGetIndex(path)`; true when `entry` exists, `entry.RoundIndexState != Failed`, and `entry.RoundIndexSchema == 0 || entry.RoundIndexFingerprint != RoundIndexFingerprint.Current`, and the background setting is on or the path was forced (§3.10). No sidecar read: the index row carries everything. |
| `Evaluate(path, parsed)` | Load the record (`TryLoadRecord`, a capacity-1 hit right after Library's upsert), take `RoundFactsData` or run the extractor, `Build`, write the sidecar, then `UpdateExisting(path, r => stamp)` so the identity fields are not restated in a different unit (`DemoCacheStore.cs:320-339`). On a throw: stamp `RoundIndexState = Failed` and clear the forced flag; never re-queue automatically (the `DemoAnalysisState.Failed` rule, `DemoCacheModels.cs:320-323`). Then raise `Indexed(path)` (§3.7). |
| `OnParsedOpportunistically(path, parsed)` | Same as `Evaluate` when `Wants(path)`; otherwise a no-op. An interactive open therefore indexes the opened demo on that parse, which is what Find Rounds Like This needs on a demo the library has not reached yet. |
| `OnFailed(path)` | Clear the forced flag; leave the stamp alone (the parse failed, the row is not the index's to mark). |
| `PriorityFor` / `OrderHint` | `UserRequested` for forced paths, else `Background`; `ModifiedTicks` newest first, as Highlights. |

Evaluate runs inside the gate slot with the parse held and finishes synchronously, per
`IDemoEvaluator.cs:34-40`. The whole per-demo write is one sidecar plus one record upsert; the
`index.json` save rides the Library's existing checkpoints (`DemoLibraryService.cs:1276-1282`,
`:1634-1641`).

### 3.6 Stamps, fingerprint, rebuild triggers

`DemoCacheRecord` gains:

```csharp
public const int RoundIndexSchema = 1;
public TierStamp RoundIndex { get; set; } = new();           // Schema, ComputedAtTicks
public RoundIndexState RoundIndexState { get; set; }         // Pending, Indexed, Failed (the DemoAnalysisState shape)
public string? RoundIndexFingerprint { get; set; }
public int RoundIndexRowCount { get; set; }                  // for the status strip; 0 when absent
public bool NeedsRoundIndex(string current) =>
    RoundIndexState != RoundIndexState.Failed
    && !(RoundIndex.IsPresent && RoundIndexState == RoundIndexState.Indexed
         && string.Equals(RoundIndexFingerprint, current, StringComparison.Ordinal));
```

`DemoCacheIndexEntry` mirrors `RoundIndexSchema`, `RoundIndexState`, `RoundIndexFingerprint` and
`RoundIndexRowCount` (about 50 bytes on a ~780-byte row) with the same `NeedsRoundIndex`, so the
backlog derives from the index like `NeedsAnalysis` (`DemoCacheModels.cs:431-440`). `ToIndexEntry`
copies them. Old sidecars without the fields deserialize to "never written".

`RoundIndexFingerprint.Current` is the string
`ri{RoundIndexSchema};cadence={CadenceSeconds};token={TokenVersion};rf={RoundFactsSchema};src={pawn|zones}[;zv={EffectiveVersion}]`,
computed once per (map, settings). Anything that changes what a row means is in it; anything joined
at query time (buy-type thresholds, team identity) is not. `src` is the token source of §3.14: in the
default `pawn` mode zones are joined at query time and are not in the fingerprint; in the opt-in
`zones` mode the token is minted through the effective `PlaceResolver`, so the map's
`ZoneSet.EffectiveVersion` (Zone Baking §3.8) is part of what a row means and is in it, per map.

| Trigger | Mechanism |
|---|---|
| Never indexed | `RoundIndexSchema == 0` in the index row; `Wants` true. |
| Cadence, token grammar, schema, or Round Facts schema changed | `Current` differs from the stored fingerprint; every row re-enters the backlog on the next `ConsiderAll`; the stale sidecar keeps serving queries until replaced (the derived-backlog rule, `DemoCacheModels.cs:311-318`), and the status strip says how many are stale. |
| Demo replaced at the same path | `LoadOrCreate` discards every tier on size or mtime drift; the record loses the stamp; the orphan sidecar is overwritten on rebuild. |
| Demo removed from the library | `DemoCacheStore.Changed(path)` for a path no longer in the index: the store subscriber deletes the sidecar. A startup sweep deletes any `.dvri.json` whose key is not in the index (orphans are harmless but cost disk). |
| Failed | Excluded from `Wants` until the user presses "Retry" on the status strip, which adds the path to the forced set. |
| User asks for a rebuild | The strip's "Rebuild index" clears every `RoundIndexFingerprint` through a batch `UpdateExisting` and calls `Coordinator.ConsiderAll()`. |

### 3.7 The query service: `SituationIndex` and `ISituationIndex`

`Services/RoundIndex/SituationIndex.cs`, a DI singleton like `DemoCacheStore`, delegate-injected into
the Situations module. It owns the in-memory index and is the only reader of the sidecars at query
time.

**Load.** At startup, off the UI thread, read every sidecar whose index row is `Indexed` at the
current fingerprint (the row filter avoids opening stale or missing files), decode each token once
into `(placeId, count)[]` against a per-map place table, and build per map:

- `tokens`: `Dictionary<string, TokenId>` and `decoded[TokenId]`;
- `postings[side][TokenId]`: `List<Posting(DemoId, RoundNumber, FromStep, ToStep)>`;
- `demos[DemoId]`: stable key, sha256, map, `ComputedAtTicks`, and the round table
  (`freezeEndTick`, `endTick`, the runs) so a two-sided query can verify overlap without re-reading;
- `places` and `transitions` folded by addition.

Measured on the thousand-demo synthetic corpus: 1.5 s, 1.17 M postings, on the order of 40 MB
retained. `IsReady` flips when the load ends; `Changed` fires once. Queries before that return an
empty result with `IsReady == false`, and the UI says "indexing".

**Incremental.** Subscribe to `DemoCacheStore.Changed`. For a path whose row is `Indexed` at the
current fingerprint and whose `ComputedAtTicks` is newer than the loaded one: read that one sidecar,
drop the demo's old postings (each `DemoId` keeps the list of token ids it contributed to, so removal
is a filter over those lists only), merge, raise `Indexed(RoundIndexedEvent)`. For a null path (a
batch) diff the index rows against the loaded set. For a path gone from the index, drop and delete.

**Query.**

```csharp
public sealed record PlaceQuery(string Place, int Count);
public enum SituationTolerance { Exact, Adjacent, TwoHops, AnyPlace }

public sealed record SituationQuery(
    string Map,
    IReadOnlyList<PlaceQuery> Ct,                 // empty = unconstrained side
    IReadOnlyList<PlaceQuery> T,
    SituationTolerance Tolerance = SituationTolerance.Exact,
    RoundFactsFilter? Facts = null,               // round-facts.md §3.6; joined by (StableKey, RoundNumber)
    IReadOnlySet<string>? Demos = null,           // StableKeys, from Team Identity or the Library filter
    long? IndexedAfterTicks = null);              // Watched Situations: only demos stamped after this

public sealed record SituationHit(
    string DemoStableKey, string? DemoSha256, string Map, int RoundNumber,
    int FreezeEndTick, int FirstMatchTick, int LastMatchTick, int MatchedSteps);   // frame clock

public interface ISituationIndex
{
    bool IsReady { get; }
    int IndexedDemoCount { get; }
    int StaleDemoCount { get; }
    IReadOnlyList<SituationHit> Query(SituationQuery query);          // one hit per (demo, round); off the UI thread
    int Count(SituationQuery query);                                  // == Query(query).Count, by construction (§3.11)
    IReadOnlyList<PlaceSummary> Places(string map);                   // §3.9
    IPlaceAdjacency? Adjacency(string map);                           // §3.8; null when no source exists
    event Action? Changed;                                            // load finished, a demo merged or dropped
    event Action<RoundIndexedEvent>? Indexed;                         // §3.12
}
```

Matching is one predicate over decoded tokens, applied per side:

| Tolerance | A queried pair `(P, N)` matches a token when | Needs |
|---|---|---|
| `Exact` | the token holds `P` with count exactly `N` | nothing |
| `Adjacent` | the sum of counts over `P` and every place adjacent to `P` is at least `N` | an `IPlaceAdjacency` |
| `TwoHops` | the same over `P`, its neighbours and their neighbours | an `IPlaceAdjacency` |
| `AnyPlace` | the side's alive total is at least the sum of all queried counts | nothing |

A side matches when every queried pair matches. A round hits when some sampled step matches on both
constrained sides at the same step; `FirstMatchTick` and `LastMatchTick` bound the matching steps
and `MatchedSteps` counts them, so Result Cards seek to `FirstMatchTick - 10 s` and Overlay View can
weight by duration. Monotonicity holds by construction: an `Exact` match implies the `Adjacent` sum
is at least `N`, which implies the `TwoHops` sum is, which implies the total is; so the count never
falls as the slider loosens, which is the Tolerance Slider's done bar. `Exact` is "exactly N" and the
wider levels are "at least N" on purpose: "three in A" at exact must not match four in A, and a
neighbourhood sum cannot be exact without becoming non-monotone. Decision 8 records the
alternative.

Execution: for the more selective side (fewer decoded tokens pass the predicate; both are evaluated
over that map's token table, 70 k tokens at a thousand demos, a few milliseconds), union the
postings; for each candidate (demo, round), verify the other side's runs overlap step for step
against the demo's round table; then apply `Demos`, `IndexedAfterTicks`, and `Facts` through
`IRoundFactsSource.TryGet(demo).Rounds[number]` (phase filters use `RoundPhases.At` on the matched
ticks). Results are ordered by demo `ModifiedTicks` descending then round number. Without Zone
Baking or an empirical graph, `Adjacent` and `TwoHops` fall back to `Exact` and the slider shows two
stops, exact and any place, which is the plan's stated degraded form.

### 3.8 Adjacency: the seam, and the graph the index already has

```csharp
public interface IPlaceAdjacency
{
    string Source { get; }                                   // "zones:<zonesVersion>" or "index:<demoCount>"
    IReadOnlySet<string> Neighbours(string place);
}
```

Two implementations. `ZonePlaceAdjacency` wraps `PlaceResolver.Adjacent` from Zone Baking §3.4 when
`ZoneAssetPipeline.TryLoad` returns a resolver for the map; it is authoritative when present.
`EmpiricalPlaceAdjacency` folds the sidecars' `transitions`: two places are adjacent when their
one-second transition count over the library is at least three. Measured on nuke, that threshold
keeps 36 pairs headed by the real callout neighbours and drops the four skip-throughs (§2.6); on
inferno it keeps 38 of 41. It is wrong in two known ways: a fast player crossing a small place in
under a second links its two neighbours, and a place nobody walks through in the library has no
edges. Both err towards a looser match, which is the direction the slider is loosening in anyway.
`SituationIndex.Adjacency(map)` returns the zone graph when loaded, else the empirical one when the
map has at least one indexed demo, else null. The Tolerance Slider names the source in its tooltip.

### 3.9 Place centroids: what the Query Canvas needs without Zone Baking

```csharp
public sealed record PlaceSummary(string Place, int SampleCount, IReadOnlyList<PlaceZBucket> Buckets);
public sealed record PlaceZBucket(int ZBucket, int Count, double CentroidX, double CentroidY);   // ZBucket = MapSpace.QuantizeZ(z)

public static class PlaceSnap
{
    // Folds the buckets that fall inside [minZ, maxZ) and returns the nearest place centroid on that band,
    // or null when nothing is within maxDistance (default 512 world units) or the band has no samples.
    public static (string Place, double Distance)? Nearest(IReadOnlyList<PlaceSummary> places,
        double x, double y, double minZ, double maxZ, double maxDistance = 512);
}
```

The canvas knows which pane was clicked and that pane's band; it folds the buckets in the band and
takes the nearest centroid. Nuke's `Ramp` spans seven Z buckets across both storeys and gets a
different centroid per storey, which is the point of bucketing by sample Z rather than by place.
With Zone Baking present the canvas calls `PlaceResolver.ResolveOnFloor` instead and this method is
its fallback, as `designs/zone-baking.md` §3.7 already states. Both produce a raw place name, so the
token the canvas builds is the same either way.

### 3.10 UI touchpoints and settings

- **Module.** `Modules/Situations/SituationsModule.cs`, id `net.demoviewer.situations`, one Main
  tab `"situations.search"` with header "Situations", feature id `tab.situations` in
  `FeatureCatalog`, `ViewModelFactory` never `DataContext`, delegate-injected `ISituationIndex`,
  `IRoundFactsSource` and the Team Identity service (`HighlightsModule.cs:31-62`). Query Canvas,
  Result Cards And Walking, Overlay View and the Tolerance Slider fill this tab; this design ships
  the tab with the status strip only.
- **Status strip** (in the tab): "Indexed 240 of 277 demos · 12 stale · indexing match730_…",
  "Rebuild index", "Retry failed". Counts come from the index rows, not from sidecars.
- **Library card**: no new badge. A demo that is parsed but not yet indexed is the normal state for
  a while, and the card already pulses for the demo being worked on.
- **Settings.** `SituationsSettings.BackgroundIndex` (default on: the flagship needs coverage, and
  at 1 to 3 s per demo the sweep is a third of the Highlights scan the opt-in guards) and
  `CadenceSeconds` is not a setting (it is a fingerprint input; changing it re-indexes the library).
  One `WriteInMemory` row so `SettingsWasmRoundTripTests` passes.
- **Keymap.** Nothing here. Find Rounds Like This registers its key: `F` is taken (§2.4), and the
  choice is **`Ctrl+F`** (plan D7; verified unbound in `Playback2DKeymap` on 2026-09-23), user-
  rebindable through the existing `Playback2DKeymapProfile.Rebind` / `ApplyKeymapOverrides` layer.
- **Token source.** `SituationsSettings.TokenSource`, `Pawn` (default) or `Zones` (§3.14). Changing
  it changes the fingerprint and re-indexes the library; the status strip says so before it applies.

### 3.14 Z-1: the opt-in resolver-token mode

Carried in from the Zone Baking review (Z-1). A user overlay (Zone Baking §3.8) can define a team's
own zones and callouts, but it cannot change the pawn's `m_szLastPlaceName`, which is what the
default token is built from (F1). With the default source, outlines and clicks speak the team's
names while search results speak Valve's. The opt-in mode closes that:

| | `TokenSource.Pawn` (default) | `TokenSource.Zones` |
|---|---|---|
| Place per alive sample | `sample.Place` (the pawn field) | `PlaceResolver.Resolve(sample.Position).Name` over the effective `ZoneSet` (baked plus overlay); `?` when `None` |
| Fingerprint | `src=pawn` | `src=zones;zv=<EffectiveVersion>` per map |
| Re-index on an overlay edit | never | yes, for that map only (the fingerprint is per map, so other maps' rows stay current) |
| Needs `zones.json` | no | yes; a map without one falls back to `Pawn` for that map and the strip says so |
| Vocabulary in Query Canvas, results, Watched Situations | Valve's | the team's (custom places carry `Origin: Custom`) |
| Agreement with the pawn field | exact | the Zone Baking cascade's measured 92 to 99.6 percent on Valve's own places (§7.1 there); custom places have no pawn counterpart by definition |

The builder takes an `IPlaceSource` (`PawnPlaceSource` or `ZonePlaceSource(PlaceResolver)`) so the
walk, the row shape, the sidecar and the query service are identical in both modes; only the string
minted per sample differs. `PlaceSnap` (§3.9) and the empirical adjacency (§3.8) are built from
whichever names the rows carry, so the Query Canvas snap and the Tolerance Slider follow the mode
without a special case. Find Rounds Like This encodes the current tick through the same
`IPlaceSource`, so the round-trip in Query Canvas' done bar still holds in both modes.

Default stays `Pawn`: it needs no asset, never re-indexes on an overlay edit, and matches what the
game itself displays. A team that has authored an overlay flips the setting once.

### 3.11 The live-count contract (Search Filters And Live Count)

- `Count(q)` returns the number of (demo, round) hits `Query(q)` would return for the same query,
  computed by the same code path with the materialisation skipped; a test asserts equality on a
  fixture and on a real demo (§7).
- Both run off the UI thread; the caller debounces token drags (about 100 ms) and posts the result;
  a stale result is discarded by sequence number, never shown.
- The count is monotone non-increasing as pairs are added to a side and monotone non-decreasing as
  the tolerance loosens, both by construction (§3.7).
- The count covers indexed demos only. The strip states the coverage ("over 240 of 277") beside the
  count so a small number is never mistaken for a rare situation.
- A `RoundFactsFilter` narrows the same hit set; the count after filtering equals the filtered
  result count. Team filters narrow through `Demos` (a set from `DemosAgainst` or `OurDemos`).

### 3.12 The Watched Situations hook

`RoundIndexedEvent(string DemoStableKey, string? DemoSha256, string Map, long ComputedAtTicks)` is
raised on the post thread after a demo's sidecar has been merged into the in-memory index. A watched
situation stores its `SituationQuery` and a `WatermarkTicks`; on the event it runs
`Query(q with Demos = { that demo })` and increments its badge on a hit. After a restart it computes
"new" as `Query(q with IndexedAfterTicks = WatermarkTicks)`, which reads the `RoundIndex.ComputedAtTicks`
stamp per demo, so the badge survives without the event. Clearing the badge moves the watermark to
now. The per-demo stamp is the only state this design adds for it.

### 3.13 Browser host

`AppPaths.ConfigRoot` is null, the processing queue is absent (`wasm-matrix.md:128`) and no
evaluator runs, so no library index exists on the browser. The `SituationIndex` runs fully in
memory: a demo opened in the tab reaches the evaluator only if the coordinator's `FanOutParsed`
fires on that head (unverified, listed in §6), in which case that one demo is searchable for the
session; otherwise the tab shows the annotations-style line "session only: no library index in the
browser" in the status strip. `tab.situations` is not added to `DesktopOnlyIds`: the tab renders
and says what it cannot do, the `ExportUnavailableNote` shape (`Playback2DView.axaml:146-147`).
`wasm-matrix.md` gets a Degraded row: "Situation Search index | degraded | no queue and no
filesystem; at most the demo open in the tab, for the session; the strip says so."

---

## 4. Alternatives considered and why not

| Alternative | Why not |
|---|---|
| **SQLite for the store (D2).** | Measured at a thousand demos: 3.5 s to build, a 180 MB file (larger than the JSON it replaces), 0.1 to 2 ms per lookup against 11 µs in memory. It adds `Microsoft.Data.Sqlite` plus a native `SQLitePCLRaw` library that is not in `Directory.Packages.props`, restored with an open NU1903 advisory on 2026-09-23, and has no place in the WASM publish. The in-memory index gives the "one statement" property D2 wants (cross-demo queries in microseconds) without the package. Confirmed on the real hundred-demo corpus (§2.7): 204 ms to build, 3.7 µs per lookup, against a 17.9 MiB SQLite file. **Revisit trigger:** when the startup load passes 5 s (about 2,500 demos at the measured 2.0 ms per demo on real sidecars), persist the postings as one compact file beside the sidecars first; SQLite only if incremental maintenance of that file becomes the problem. The `ISituationIndex` API does not change either way, and Tag Store §4.1's projection stays additive. |
| **Scan sidecars per query, no in-memory index.** | 1.0 to 2.1 s per query at a thousand demos; the live count runs a query per token drag. Fails the done bar. |
| **Rows inside `DemoCacheRecord` as a tier.** | 55 to 90 KB per record on a surface that re-reads the record per property touch (§3.3). The stamp is on the record; the payload is not. |
| **Cadence 0.5 s.** | Double the rows for 25 to 30 percent more distinct pairs (§2.6); nothing downstream asks for it. Available by changing one fingerprinted number. |
| **Cadence 2 s.** | Loses 30 percent of the distinct (CT, T) pairs; Suggested Tags' four-second execute window would see two samples. |
| **Sample every tick and compress.** | The walk's decode floor is the cost either way (1.0 s at stride 16 versus 1.4 to 1.8 s at stride 1), but rows would be 64 times more numerous for no consumer. |
| **Count dead players in the token.** | Wrong on a quarter of rows (§2.6); Suggested Tags asks for alive-only; man-count is a Round Facts fact. |
| **A second app-side tracker walk reading `m_lifeState` and `m_iTeamNum`.** | Works (it is what the probe did) but duplicates `PositionSampler`'s reconstruction and costs the same. The Kills join agrees on 99.8 percent of rows and keeps the walk engine-owned; §5 proposes the engine flag. |
| **Omit `:1`.** | Saves about 15 percent of token bytes for a decoder with a special case. Rejected; the sizes are small. |
| **Whole-situation exact string match only (the plan's literal wording).** | A ten-player arrangement recurring exactly is rare (720 to 1,130 distinct pairs per demo); the plan also says partial queries are the normal case. Pair-level matching is what "exact" means here, and the full-string case is the same predicate with ten pairs. |
| **Store the phase per row.** | Derivable in constant time from Round Facts; storing it couples the sidecar to Round Facts' phase table. |
| **Per-slot rows (place per player).** | Would let Suggested Tags refine arrival ticks, at five times the rows and a per-player identity the search never queries. Its design says the per-side token suffices for the first release. Kept as the obvious schema 2 if a consumer needs it. |
| **Adjacency only from Zone Baking.** | Leaves the Tolerance Slider at two stops until issue #5 ships; the transition counts are free at index time and measured to recover the callout graph. The seam makes the zone graph win when present. |
| **One `round-index.json` for the whole library.** | The store's own rule (`DemoCacheStore.cs:15-21`): a per-demo backfill would rewrite a growing file per demo. |

---

## 5. External and engine changes required

**Required: none of this design's own.** The builder reads `PositionSampler.Walk` as packaged in
CS2DemoKit 0.12.0, no field of `assets/<map>/bundle.json` is read or written, and the CSVG game
plugin is untouched. It does **inherit** Round Facts' dependency: the rows it reads come from the
rules-driven Round Facts, which waits on CS2DemoKit #54, so the index build cannot start before
Round Facts' does (§9).

**Proposals A and B below were filed together as CS2DemoKit #58 on 2026-09-24**, with Suggested
Tags' §5.3 doc corrections, as decided.

**Proposal A (CS2DemoKit).** `PositionSample` gains `bool IsAlive` (from
`m_lifeState == 0 && m_iHealth > 0`) and `int Team` (`m_iTeamNum`), or `PositionSampler` gains a
`WalkDetailed` overload yielding them. Value: the index and Suggested Tags drop the Kills and Slots
joins; the 0.1 to 0.2 percent death-tick residue disappears. Cost: an upstream PR and a pin bump;
the app-side join stays as the fallback so nothing here waits for it.

**Proposal B (CS2DemoKit, a documentation note or a filtered overload).** `PawnLookup.ForEachLivePawn`
and the `PositionSampler` doc say "live pawn"; measured, 24 to 29 percent of one-second samples on
four Valve demos include a dead pawn. Either the doc says "every pawn with a controller, dead or
alive", or an `alive: true` parameter filters on `m_lifeState`. Worth filing with the measurement
so the next consumer does not rediscover it.

**Zone Baking (this repo, sibling design, not assumed).** When `zones.json` ships, `ZonePlaceAdjacency`
wraps `PlaceResolver.Adjacent`, and the Query Canvas prefers `ResolveOnFloor` to `PlaceSnap.Nearest`.
Nothing in this design changes; the seam is `IPlaceAdjacency` and the fallback stays.

---

## 6. Risks and unknowns

| # | Risk | Mitigation |
|---|---|---|
| R1 | Only Valve matchmaking demos (builds 10231 and 10896) were measured. FACEIT (128 tick), HLTV and POV may differ in place coverage or kill events. | Cadence in seconds; `?` preserves the man-count; Place Names From The Pawn and Inputs Per Demo Source report per source before Phase 1 builds. |
| R2 | Round Facts is approved but not built, and waits on CS2DemoKit #54; its `Slots` and `Kills` shapes are fixed by its §3.6 but may move at build. | The fingerprint carries `RoundFactsSchema`; the builder takes the rows as a parameter; the index follows Round Facts by one evaluator pass and does not run without it (§3.4). |
| R3 | Place vocabulary drifts across game builds (Valve renames or splits a place). Tokens from different eras stop matching. | Raw names are the contract (Zone Baking, Callout Aliases); the empirical `places` table shows the vocabulary per demo; a rename becomes an alias, not a rebuild. |
| R4 | `m_szLastPlaceName` is sticky: a player on an unnamed nav area keeps the previous name. | Measured null rate is near zero; the sticky name is what the game itself displays; the token is what a human would call the spot. |
| R5 | The empirical adjacency links places a fast player crosses in under a second. | Threshold of three observations; the graph is a fallback; the slider names its source; Zone Baking replaces it. |
| R6 | Startup load grows linearly with the library (1.5 ms per demo measured; 5,000 demos about 7 s). | Off the UI thread with `IsReady`; the revisit trigger in §4 names the next step; the sidecar loader reads typed rows, not `JsonElement`, which the probe's slower path did. |
| R7 | Memory at large libraries (about 40 MB per thousand demos). | Bounded and measured; per-map tables so a single-map library pays for one map. |
| R8 | Two writers per demo per pass (the sidecar and the record stamp) and a sibling directory the store does not know about. | One subscriber deletes on `Changed`; a startup orphan sweep; both writes are atomic and the stamp is written last, so a crash between them leaves "not indexed", never a stamp without a file. |
| R9 | `FanOutParsed` on the browser head is unverified, so "the open demo is searchable in the session" may be false there. | The strip says "no library index in the browser" either way; a wasm-matrix row records which it is once seen. |
| R10 | Frames sharing a tick (up to 39 on build 10231) could put two frames' samples into one row. | Rows key on the sampled tick, and samples from later frames of the same tick overwrite by slot; a test pins one row per (round, step). |
| R11 | Background indexing on by default is 15 minutes of churn on a 277-demo library and scales with the library. | Runs under the same gate as everything else, yields to interactive loads and reel sessions (`HeavyJobGate`), one setting turns it off; the strip shows progress. |
| R12 | The `Exact` versus "at least" semantics may surprise a user who places three tokens and expects a 4-in-A round to count. | Decision 8; the slider's first loosening stop already returns it. |

Unknowns to close during build: whether `KillStep.VictimSlot` and `PositionSample.PlayerSlot` are
the same controller slot numbering (both are documented as controller-derived; a real-demo test
asserts the alive agreement of §2.6); whether overtime rounds carry `IsLive` correctly (Round Facts
R1); the exact FACEIT tick rate handling of `cadenceTicks` at 128.

---

## 7. Test and verification strategy

**Unit, synthetic (App test suite, no demo file).**

- `PlaceCountTokenTests`: encode is canonical (order, explicit counts, `?`, empty); decode is the
  inverse; a place containing `:` or `|` is rejected with a message; ordinal sort places `Z` before
  `a`.
- `RoundIndexBuilderTests` over `SyntheticParsedDemo` (`src/App/DemoViewer.NET.App.Tests/SyntheticParsedDemo.cs:18`)
  with a synthetic `RoundFactsTier`: rows only inside live windows; step arithmetic at 64 and 128
  ticks; a death at the sampled tick removes the slot from that row on; runs tile the steps; two
  frames on one tick produce one row; spectator slots are dropped; `places` and `transitions` sums.
- `SituationQueryTests`: every tolerance level against a hand-built token table; monotonicity across
  levels and across added pairs, property-style over random tokens; two-sided overlap verification;
  `Count == Query.Count` for every case; `Demos`, `IndexedAfterTicks` and a `RoundFactsFilter` stub.
- `EmpiricalPlaceAdjacencyTests`: threshold, symmetry, fold across demos; `ZonePlaceAdjacency`
  preferred when present.
- `PlaceSnapTests`: nearest centroid within a band; a place with samples on two bands yields two
  centroids; `maxDistance` returns null.
- `RoundIndexStoreTests`: atomic write, corrupt sidecar treated as absent, in-memory on a null
  cache root, orphan sweep, deletion on `Changed`; the serialized fixture
  `tests/fixtures/round-index/schema-v1.sample.dvri.json` round-trips byte for byte.
- `DemoCacheRoundIndexStampTests` (the `DemoCacheTier2Tests` shape): `NeedsRoundIndex` on missing,
  stale-fingerprint, `Failed`; stamping leaves the other tiers alone; the index mirror agrees with the
  record; an old sidecar without the fields deserializes.
- `RoundIndexEvaluatorTests`: `Wants` from index rows only; `Evaluate` with and without a Round
  Facts tier; a throw stamps `Failed` and is not re-wanted; forced paths get `UserRequested`.

**Real demo, `[Category("RealDemo")]`, gated by `DemoTestHelper.RequireDemo()`
(`src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs:208`).**

- Rows per live round between 20 and 160 at 1 s; every side count sum at most the side's
  `PlayersAtFreezeEnd`.
- Alive agreement: the builder's alive set versus a `m_lifeState` walk of the same demo differs on
  at most 0.5 percent of rows, and only where a death tick equals the sampled tick.
- `FreezeEndTick + 0 * cadence` rows exist for every live round and equal
  `ClipRounds.Derive(...)[i].StartTickFrameClock`.
- Find Rounds Like This round-trip: encode the 2D scene's alive players at a sampled tick and assert
  the index holds that token for that (round, step).
- On a nuke demo the empirical adjacency contains `Admin-Ramp`, `Heaven-Rafters`, `CTSpawn-Outside`
  and not `Crane-Rafters`.
- Budget, loosely: build time under 3 times the parse time (bench variance: quote, do not
  gate tightly).

**Corpus, synthetic, `[Category("Budget")]`.** Regenerate the §2.6 thousand-demo corpus in a temp
directory; assert load under 5 s and a query under 50 ms on the CI runner, both with headroom.

**Manual.** Index the local replays folder, watch the strip reach the library size, drop three
tokens on the canvas once Query Canvas exists, and check the count does not fall as the slider
loosens.

---

## 8. Decisions, and the answers (2026-09-23)

The design was approved on its recommended answers. Each row records the recommendation as
the decision.

| # | Decision | Answer |
|---|---|---|
| 1 | D2, storage | JSON sidecars plus a per-session in-memory index, **conditional on the real-corpus measurement requested**, which §2.7 now carries; §4 states the revisit trigger. |
| 2 | Cadence | One second. |
| 3 | Rows or runs in the sidecar | Runs. |
| 4 | Explicit `:1` | Explicit. |
| 5 | Alive source | Round Facts `Kills`. |
| 6 | Sidecar location | `cache/round-index/<StableKey>.dvri.json` beside `demos/`. |
| 7 | Background indexing | On by default (the integrator's O-4 asks for one setting shape across evaluators; this design's default stands for `roundindex`). |
| 8 | `Exact` semantics | Exactly N at `Exact`; at least N at wider tolerances. |
| 9 | Adjacency | Ship the empirical graph at threshold three; Zone Baking's graph wins when present. |
| 10 | Post-round window | Not indexed. |
| 11 | Module naming | `net.demoviewer.situations`, tab `situations.search`, feature `tab.situations`. |
| 12 | Upstream proposals | Batched after Phase 1 (integrator O-35). |
| Z-1 | Resolver-token mode (from the Zone Baking review) | Opt-in `TokenSource.Zones` (§3.14); default `Pawn`. |
| D7 | Find Rounds Like This key | `Ctrl+F`, user-rebindable (§3.10). |

No decisions remain open on this design.

---

## 9. Effort estimate and sequencing

| Step | Work | Estimate | Depends on |
|---|---|---|---|
| 1 | `PlaceCountToken`, `RoundIndexModels`, `RoundIndexBuilder`, fixture, unit tests | 2 days | Round Facts design approved (the `RoundFactsTier` shape); not its build |
| 2 | Stamps on record and index row, `RoundIndexStore`, `RoundIndexEvaluator`, composition-root wiring, settings rows (`BackgroundIndex`, `TokenSource`) | 2 days | step 1; the Round Facts build (its rows are required; there is no extractor) |
| 2b | `IPlaceSource` with `PawnPlaceSource` and `ZonePlaceSource`, the per-map `zv` fingerprint, the strip's re-index notice (§3.14) | 0.5 day | step 2; Zone Baking's `PlaceResolver` for the zones mode (the pawn mode needs nothing) |
| 3 | `SituationIndex`: load, incremental merge, query with tolerances, `Count`, adjacency seam, `PlaceSnap`, `Indexed` event | 2.5 days | step 2 |
| 4 | `SituationsModule` with the status strip, feature id, wasm-matrix row | 1 day | step 3 |
| 5 | Real-demo tests, corpus budget test, a full index of the local replays folder with the rate written into this document | 1 day | steps 1 to 4 |

About 9 days. Steps 1 and 3 can proceed in parallel after the models exist. The plan's done bar
("a corpus indexes at a stated rate, and a token lookup over it returns in under a second") is met
at step 5 with numbers to replace the synthetic ones in §2.6. Query Canvas, Find Rounds Like This,
Result Cards And Walking, Search Filters And Live Count, the Tolerance Slider and Watched Situations
all start after step 3; none needs Zone Baking to start.

---

## 10. Sources

**Tree, `main` at `d90ec9f`.**
- `plan.md` §2 (F1 `:61-70`, F9 `:148-153`, F14 `:175-178`, F15 `:180-184`,
  F17 `:191-193`), §3 (The Round Index `:266-273`, Query Canvas `:275-280`, Find Rounds Like This
  `:282-285`, Search Filters And Live Count `:287-291`, Tolerance Slider `:293-296`, Result Cards
  And Walking `:298-302`, Watched Situations `:308-310`), §6 D2 (`:586-591`), D7 (`:613-614`).
- `docs/strat-book/designs/round-facts.md` §2.7, §3.3, §3.4, §3.5, §3.6, §3.7, §9;
  `designs/zone-baking.md` §3.2, §3.4, §3.6, §3.7, §7.1; `designs/team-identity.md` §3.7;
  `designs/suggested-tags.md` §2.9, §3.2, §5.1, §8; `designs/tag-store.md` §2.9, §4.1.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs` (`:15-21`, `:48-59`, `:80-90`,
  `:157-170`, `:200-257`, `:263-281`, `:283-318`, `:320-339`, `:500-511`, `:559-573`).
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs` (`:55-65`, `:84-89`, `:203-206`,
  `:216`, `:309-327`, `:337-370`, `:401-419`, `:431-440`).
- `src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs` (`:47`, `:188-270`, `:571-600`,
  `:1138-1192`, `:1276-1282`, `:1302-1354`, `:1393`, `:1403-1460`, `:1634-1641`).
- `src/App/DemoViewer.NET/Modules/Highlights/HighlightScanService.cs` (`:152-215`, `:498`);
  `Modules/Highlights/HighlightsModule.cs` (`:31-62`).
- `src/App/DemoViewer.NET/Services/DemoProcessing/IDemoEvaluator.cs` (`:22-73`),
  `DemoEvaluationCoordinator.cs` (`:73-108`, `:140-197`), `DemoProcessingQueue.cs` (`:285`, `:315`);
  `Services/HeavyJobGate.cs` (`:37`); `Services/AppPaths.cs` (`:54-66`); `App.axaml.cs` (`:705-725`).
- `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DKeymap.cs` (`:333-335`);
  `Modules/Playback2D/Playback2DTabViewModel.cs` (`:2505-2513`).
- `src/App/DemoViewer.NET/Features/FeatureCatalog.cs` (`:59-66`), `Features/ShellModuleFeatureGate.cs`
  (`:59-64`); `Views/Playback2D/Playback2DView.axaml` (`:146-147`); `Configuration/AppSettings.cs`
  (`:206-211`).
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapSpace.cs` (`:35`, `:81-100`).
- `src/App/DemoViewer.NET.App.Tests/SyntheticParsedDemo.cs` (`:18`), `DemoCacheTier2Tests.cs`,
  `EntityStoreFilterEquivalenceTests.cs`; `src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs` (`:208`).
- `docs/playback2d-v2/annotations-format.md` (`:24-36`, `:69-71`); `docs/playback2d-v2/wasm-matrix.md`
  (`:117`, `:119`, `:128`); `docs/playback2d-v2/design.md` and `docs/plugins/plugin-system-design.md`
  for form.

**CS2DemoKit 0.12.0 packaged XML docs** (`~/.nuget/packages/cs2demokit.parser/0.12.0/lib/net10.0/CS2DemoKit.Parser.xml`,
`cs2demokit.analysis/0.12.0/.../CS2DemoKit.Analysis.xml`): `PositionSampler`, `PositionSampler.Walk`,
`PositionSample`, `PawnLookup.ForEachLivePawn`, `PositionUtil.CellToWorld`, `EntityTrackerFactory.CreateCurated`,
`EntityTracker.StoreClassFilter`, `DemoFrame.ServerTick`, `ClipRound`, `ClipRounds`.

**CS2DemoKit source at `origin/main`** (`git -C C:\dev\CS2DemoKit show origin/main:<path>`):
`src/CS2DemoKit.Parser/EntityTracking/PositionSampler.cs` (the `Iterate` loop and `Collect`),
`src/CS2DemoKit.Parser/EntityTracking/PawnLookup.cs:42-87` (no alive check in the sweep).

**Measurements.** Scratch project
`round-index`
(`Program.cs` modes `cadence`, `sampler`, `scan`; CS2DemoKit.Parser and Analysis 0.12.0,
CS2OpenDev.Protos 0.9.0, Microsoft.Data.Sqlite 10.0.0; logs `cadence.log`, `cadence2.log`,
`scan.log`, `scan70.log`), run 2026-09-23, Release, against
`C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\replays\`
`match730_003731893271710924851_1024675027_129.dem` (de_nuke, build 10231),
`match730_003844252717140672725_0377894676_389.dem` (de_dust2, 10896),
`match730_003842233788306292960_0260929275_408.dem` (de_mirage, 10896),
`match730_003842442070597828830_0022157567_392.dem` (de_inferno, 10896). Synthetic corpora under
`round-index/corpus` (100 rows per round) and `corpus70` (70 rows per round). Not
committed; re-creatable from this document.
