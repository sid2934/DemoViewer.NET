# Grenades v2: why common lineups vanish, and the next grenade system

Branch `feature/strat-book-grenades-v2`, from `feature/strat-book` at `4cf6a6b1`. Research and prototypes.
Two changes touch live paths: the landing group key fix (slice 0), and `GrenadeWalker.ReadPass` now takes
its frames as a sequence so the retained and forward walks share it. The refactored retained walk and the
forward walk give byte-identical rows on three benchmark demos, and the real-demo walk tests (filtered
equals unfiltered, the measured invariants) pass. It has not been compared with the walk before the
refactor, because the Grenade Walk golden was never captured; slice 2 captures it first.

Data: a copy of the cache taken 2026-09-28 (381 demos walked by walker 2, 95,583 grenades, 94,845
with an origin and a landing, so indexable). 56 of the demos are de_mirage (4,523 smokes). No demo is
counted twice: 64 have no hash yet, and no two share a file name and size.

## 1. Diagnosis

### 1.1 The missing Window smoke is thrown, walked and grouped. The map drops it.

Mirage's nav place `SnipersNest` is the Window room (x -1328 to -1057, y -1216 to -458). Smokes released
in T spawn that detonate within 160 units of (-1176, -630): **513 throws, all T side, from 54 demos**.
500 of them detonate on the mid floor in front of Window (z about -166), and the zone resolver places 491
in `SnipersNest`. Detonation is the event position, not a bounce or a ledge, and every one has a
thrower, a release and a landing. The rows are fine.

`GrenadeIndex.Cluster` puts them in one landing group of 559 throws from 25 positions (lineups of 146,
124, 57, 35, 29, 29 ...). That is the biggest smoke group on the map.

The Utility Book never shows it. `UtilityBookTabViewModel.BuildGroups` stores groups in a dictionary keyed
by `LandingGroup.Id`, which was `"{Kind}:{Cell}"`, with `Cell` the 256-unit grid cell of the group's seed.
Landing groups are merged at 96 units, so one 256-unit cell can seed several groups. They share a key, and
since clusters arrive most thrown first, the smallest group in the cell is written last and replaces the
bigger ones. The icon at Window was a 9-throw group that happened to seed in the same cell.

Measured through the real C# path (`GrenadesV2Probe`, de_mirage smokes, default filters):

| | Landing groups | Lineup throws shown |
|---|---|---|
| Clusters with a lineup of 2 or more | 74 | 2,420 |
| Shown on the map before the fix | 53 | 659 (27%) |
| Largest group lost | Smoke into SnipersNest | 559 |

It is not a filter. The defaults are Smoke, any place, either side, lineups of 2 or more, and none of them
removes these throws. It is not the landing grid or the aim tolerance either: those split the Window throws
into 16 positions, but all 16 sit in the one group that the dictionary dropped.

### 1.2 How widespread

The same dictionary on every map (Python port of `Cluster`, `tools/GrenadesV2Analysis/collide.py`; it is
within 1.5% of the C# numbers on Mirage):

| Map | Smoke groups with a lineup | Shown | Lineup throws | Shown |
|---|---|---|---|---|
| de_mirage | 76 | 54 | 2,417 | 650 (27%) |
| de_inferno | 71 | 46 | 1,159 | 396 (34%) |
| de_dust2 | 74 | 51 | 2,086 | 434 (21%) |
| de_nuke | 91 | 58 | 2,177 | 577 (27%) |
| de_ancient | 103 | 72 | 2,252 | 739 (33%) |
| de_anubis | 30 | 25 | 238 | 147 (62%) |
| de_overpass | 58 | 48 | 554 | 392 (71%) |
| de_train | 41 | 34 | 312 | 179 (57%) |

All kinds and maps: 1,504 of 1,944 groups shown, 10,540 of 25,084 lineup throws (42%). The busiest spots
are the ones most likely to lose, because a busy cell seeds more groups.

### 1.3 Reported examples and other well-known lineups

Each row is the throws from the named area landing within 160 units of the spot (`known.py`). "Shown
today" counts throws in a lineup of 2 or more whose group survived the dictionary. The last two columns
are landing groups the throws fall in / lineups of 2 or more among them / largest lineup.

| Lineup | Throws | Shown today | Grid (after key fix) | Density prototype |
|---|---|---|---|---|
| Mirage Window from T spawn | 513 | 7 (1%) | 4 / 16 / 146 | 3 / 12 / 164 |
| Mirage Top-mid from T spawn | 139 | 100 (72%) | 2 / 13 / 50 | 2 / 10 / 51 |
| Mirage Stairs | 140 | 99 (71%) | 6 / 14 / 30 | 5 / 14 / 30 |
| Mirage CT from T roof / Palace alley | 139 | 14 (10%) | 4 / 13 / 38 | 1 / 7 / 62 |
| Mirage Jungle | 37 | 4 (11%) | 5 / 8 / 4 | 4 / 11 / 4 |
| Inferno B CT from Banana | 344 | 2 (1%) | 8 / 44 / 18 | 7 / 35 / 48 |
| Inferno Mid from T spawn | 67 | 3 (4%) | 6 / 12 / 17 | 5 / 4 / 62 |
| Dust2 Xbox from T spawn | 211 | 0 (0%) | 5 / 7 / 168 | 4 / 6 / 174 |
| Dust2 CT mid / mid doors | 297 | 7 (2%) | 6 / 18 / 168 | 4 / 22 / 66 |
| Dust2 A cross from Long doors | 225 | 2 (1%) | 4 / 31 / 36 | 3 / 16 / 84 |
| Nuke Outside, garage from T | 165 | 2 (1%) | 8 / 6 / 138 | 7 / 6 / 122 |
| Nuke Outside, mid wall from T | 234 | 2 (1%) | 9 / 16 / 88 | 5 / 14 / 80 |
| Ancient Mid from T spawn | 397 | 33 (8%) | 5 / 37 / 96 | 4 / 22 / 96 |
| Anubis Canal from T | 22 | 0 (0%) | 2 / 3 / 11 | 1 / 3 / 11 |

The key fix alone brings every one of these back. The density grouping then gathers the fragments.

### 1.4 The second problem: fragmentation

With the key fixed, 46% of Mirage smokes are still hidden as "thrown from only once" (2,103 positions).
Some of that is real, since matchmaking throws a lot of one-off smokes. Some of it is the grid:

- **Jump-throws release in the air.** The release position is the pawn origin at the `weapon_fire` tick,
  up to about 55 units above the floor. The Window lineup at (1382, 71) has 11 throws at z -136 split off
  the 146 at z -158, because the origin merge allows 24 units of height.
- **A fixed 16-unit origin grid and 2 degrees of aim.** Observed: in the 146-throw Window lineup, pitch at
  release ranges over 12 degrees while every throw lands in the same spot. That is either real spread,
  a release read on the wrong frame for some jump-throws, or two lineups from one spot. Not resolved here.
  Related: the card's console line comes from `Throws[0]` (oldest demo first), which is arbitrary; the
  representative throw should be the lineup's medoid (slice 3).
- **Running throws** release anywhere along the run, so a 16-unit spot splits them into singles.
- **The lineup id is not stable today either.** It is the grid id of whichever grid position seeded the
  merge, so a new demo that makes another grid position the most thrown changes the id. `AliasIds` only
  covers positions merged in the same run.

## 2. Proposed grouping

Prototype (research branch): `GrenadeDensityGrouping`, since replaced by `GrenadeLineups` (§9), and
`tools/GrenadesV2Analysis/dens.py`, which gives the same numbers.

**Lineups first, then landing groups.** A lineup is one spot, one technique, one outcome.

1. Technique key: jump-throw or not, strength (left, both, right click from `ThrowStrengthClass`, Unknown
   and Other counted as left), running or not (`Movement`). Throws with different keys never share a lineup.
2. Within a kind and technique, leader clustering on the release origin in density order. The most crowded
   throw seeds first. Every other throw joins the nearest seed within 24 units in the plane (64 when
   running) and 64 units in height (covers the jump arc), and only when its landing is within 192 units in
   the plane and 96 in height of the seed's landing. A throw compares with seeds only, never with other
   members, so nothing chains. Aim is not a criterion: same spot, same technique and same landing is the
   lineup. Aim spread becomes a card statistic.
3. Landing groups: the same leader clustering over the lineups' mean landings, weighted by throw count,
   with a radius of 128 units for smokes and HE, 96 for fires, 160 for flashes, and 96 units of height so
   a ledge and the floor under it part. Re-centred groups within half a radius of a bigger one fold into it.

Why these numbers: a CS2 smoke is about 144 units in radius, so two detonations within 128 cover the same
ground. 24 units is two feet. 64 in height is the jump apex plus a crouch. 192 on the landing lets bounces
scatter without letting two lineups from one spot to different places merge.

Results on the library, smokes:

| Map | Grid lineups ≥2 | Throws covered | Largest | Density lineups ≥2 | Throws covered | Largest |
|---|---|---|---|---|---|---|
| de_mirage | 401 | 2,417 (53%) | 146 | 441 | 3,089 (68%) | 164 |
| de_inferno | 335 | 1,159 (37%) | 19 | 429 | 1,897 (60%) | 62 |
| de_dust2 | 362 | 2,086 (45%) | 168 | 495 | 2,981 (64%) | 174 |
| de_nuke | 441 | 2,177 (47%) | 138 | 538 | 3,052 (65%) | 122 |
| de_ancient | 483 | 2,252 (46%) | 96 | 567 | 3,220 (65%) | 99 |
| de_anubis | 78 | 238 (33%) | 11 | 100 | 369 (50%) | 20 |
| de_overpass | 131 | 554 (26%) | 157 | 253 | 1,044 (48%) | 160 |
| de_train | 90 | 312 (28%) | 36 | 129 | 543 (48%) | 61 |

C# on Mirage smokes: grid 74 landing spots, 402 lineups, 2,420 throws; density 77 spots, 440 lineups,
3,093 throws, Window 607 throws from 30 positions (165, 126, 59, 35, 29, 29 ...). One all-kinds query on
Mirage takes 126 ms on the grid and 173 ms dense (14,757 grenades), so it can still run per refresh.
Leader clustering cannot chain, but lineups do grow: the largest goes from 19 to 62 on Inferno, 11 to 20
on Anubis and 36 to 61 on Train, mostly from the 64-unit running radius (Inferno mid from T spawn is a
run-jump throw). The farthest throw in a landing group sits 231 to 362 units from its centre, from lineups
whose own landings may spread 192. Both are over-merge risks to spot-check on the cards before slice 3
ships, not something the prototype has settled.

**Stable identity.** A density lineup is only stable if its identity is stored, not recomputed. Proposal:
a per-map lineup store holds anchors (id, kind, technique, anchor origin, anchor landing, created at). A new
demo's throws join the nearest anchor that the rules above accept; only leftovers are clustered, and a
new cluster of 2 or more mints a new anchor with a random `Guid`. Anchors never move. A re-cluster (a
parameter change) re-mints under a new store version and records old id to new id in an alias table.

**Migration of stored ids.** Strat steps (`utility.lineupId`, user data), lineup clip stems
(`{map}-{kind}-{lineupId:N}`), and review queue entries store today's ids. The mining cache
(`detected.json`) is rebuilt on every mine and needs nothing. The prototype fills `AliasIds` with every member's grid id,
which is not enough: the density grouping splits by strength and running where the grid did not, and on
the library 2,866 of 7,905 grid lineups (13,469 throws) spread over more than one density lineup
(`aliases.py`). With aliases on every piece, `DescribeLineup` would return whichever piece comes first.
Rule for the persisted alias table: each old id maps to exactly one anchor, the one that received the most
of that grid position's throws, ties broken by the anchor's throw count and then its id. Clip stems follow
the same map, so one former GIF is adopted by one lineup. `DescribeLineup`, the
Strat Book pickers, `RoleSheet` and the clip planner all go through `Answers`, so none of them changes. Clip
files are adopted under the new stem through the planner's existing former-path rename, with no re-render.

## 3. Storage

Today, per demo, a rows sibling (every field of every grenade) and a paths sibling (every trajectory):

| | Gzipped on disk | Raw |
|---|---|---|
| Rows, 95,583 grenades | 11.8 MB | 85.3 MB |
| Paths | 39.3 MB | 103.7 MB |
| Index in memory after load | | ~93 MB (about 1 KB per grenade) |

**The paths sibling has no production reader.** `GrenadeIndex` loads rows only and `Trajectory` is
`[JsonIgnore]`, so a row read from disk has no trajectory. The flight line on the map falls back to a
straight origin-to-landing line for every demo not walked in the current session. 77% of the grenade
bytes on disk are written and never read.

What needs individual throws (consumer survey):

- Utility Book instance list and "open in 2D Playback": demo, round, release tick, thrower.
- Opening Tendencies: first throw per round and side (round, team, release tick, detonation tick, kind, place).
- Strat Mining signatures and `CachedRoundCapture`: every throw in a round window (release tick, slot,
  kind, landing, place, origin, lineup id).
- Lineup clips: one representative throw per lineup (ticks, eye angles, SteamID, demo).

Everything else (cards, console line, thumbnails, Strat Book pickers, role sheets) reads one representative
throw or lineup aggregates. Suggested Tags reads the parse, not the rows.

**Proposal: a throw log plus a lineup store.**

- Throw log, per demo: one 43-byte record per grenade (row id, round, release and detonation tick, kind,
  flags for jump, crouch and ground, slot, origin, landing and spawn on a 2-unit grid, pitch and yaw in
  hundredths of a degree, SteamID index, strength and movement, team). Everything Opening Tendencies,
  mining and the instance list read. Measured: **4.1 MB raw, 2.9 MB gzipped** for the whole library.
- Lineup store, per map: anchors, alias table, counts, demo count, aim and landing spread, first and last
  seen, the representative throw at full float precision (the card's `setpos`/`setang` line needs it; the
  throw log's 2-unit grid is only for mining and Opening Tendencies), and **one** trajectory per lineup. 10,647 lineups of 2 or
  more across all kinds hold 52,921 throws; with one path each that is about **12.7 MB raw, 4.8 MB
  gzipped**.
- Single throws keep their throw log record and no trajectory. The instance list and 2D Playback need
  nothing more.

Total about 7.7 MB gzipped against 51.1 MB today (-85%), and the index would hold structs of about 43 bytes
per throw instead of ~1 KB objects. A cap is then cheap to add: for example, drop throw log records of
single throws from demos older than N months, or keep only the newest K members per lineup with counts
carried in the aggregate. Whether to cap at all is an open decision (§7).

Existing rows convert without a re-walk: the rows sibling has every field the throw log needs, and the
paths sibling has the representative trajectories. The conversion runs once as a queue item, like the
sidecar-format-v2 conversion.

## 4. Two-pass walk

The walk needs the retained parse for two reasons: random frame access and user commands. User commands
feed only the jump-throw flag (`ReconstructedInputSource` keeps commands inside each throw's 16-tick jump
window), and they are most of the parse's LOH (§7 of `docs/perf/memory-and-storage-v1.md`). The pawn reads
(release position, eye angles, flags, throw strength) come from entities.

Prototype: `GrenadeWalker.WalkForward` (`Modules/UtilityBook/GrenadeWalkerForward.cs`), on the research
branch `feature/strat-book-grenades-v2` only (commit `0b702929`). It was deferred; the build branch
does not carry it.

- **Pass 1**, `DemoReader` with Header, StringTables, Schema, Entities and GameEvents. A tap records every
  frame's tick and every decoded game event while `ProjectileSampler.Walk(IDemoFrameSource)` (present in
  the pinned 0.13.0-beta0001, checked with ilspycmd against the package DLL) samples the projectiles. From
  that alone: tracks, event index, `Plan`, round starts (`ClipRounds.Derive(events)`), players and tick
  rate from `Enrichment.Snapshot()`.
- **Pass 2**, a fresh reader with Header, StringTables, Schema, Entities and UserCmds and no events, fed
  to the existing `ReadPass`, which now takes the frames as a sequence. It stops at the last frame a plan
  needs. The reconstructor sees every command up to there, which it needs because most commands are deltas
  against the previous one, so a targeted window cannot start mid-demo.

Frame indexes line up across the two readers and with the retained parse: rows are **byte-identical,
trajectories included**, on all three demos. `AnalysisBench grenade-walk`, Workstation GC, mapped file,
two runs each (ranges):

| Demo | Rows | Retained: time / peak heap / peak RSS | Forward: time / peak heap / peak RSS |
|---|---|---|---|
| 0034 (181 MB) | 175 | 4.4-4.6 s / 495 MB / 572 MB | 4.9-5.1 s / 32-33 MB / 275 MB |
| 1066 (255 MB) | 247 | 5.7-7.0 s / 656-698 MB / 769 MB | 6.5-6.6 s / 41 MB / 351 MB |
| 0126 (292 MB) | 354 | 6.0-6.8 s / 739-778 MB / 885 MB | 6.5 s / 38 MB / 384 MB |

Peak managed heap drops by 93-95%; RSS roughly halves, and what is left is mostly mapped file pages the OS
can drop. Time is about the same (-0.5 to +0.8 s), with 4-5% more allocation, since both passes decode
entities.

In the app the walk rides the shared retained parse, so the gain is larger than the table: once grenades
run forward, no background consumer reads user commands, and the shared parse can drop them (§7 item 2 of
the perf doc: -29% to -44% footprint, LOH -80%). With Round Index and Suggested Tags forward as well, no
background job would retain frames at all.

**Upstream APIs** (none filed, none needed for the two-pass version):

- One pass instead of two needs the sampler's tracker. `ProjectileSampler.Slots` and `Slots.Step` are
  `internal`. An overload such as `ProjectileSampler.Walk(IDemoFrameSource, EntityTracker)`, or a public
  `Slots.Step`, would let the walk read pawns and feed the reconstructor on the same frames, keeping a
  16-tick ring per slot. It saves one of the two entity decodes (not measured).
- A reconstructor that can start from a FullPacket (a command baseline in the snapshot) would allow
  targeted command windows. The data may not carry that. Not needed.

## 5. Renders

`GrenadesV2Probe` (App tests, categories Probe, Render, Integration; skips unless `GV2_CACHE` names a cache
copy) renders de_mirage smokes with the default filters, then focuses the Window group. Copies are in
`docs/utility-book/grenades-v2/`:

- `mirage-smokes-today.png`: before the key fix. 53 spots, 659 throws. Nothing at Window except the
  9-throw survivor.
- `mirage-smokes-keyfix.png`, `mirage-window-keyfix-focus.png`: grid grouping, key fixed. 74 spots, 2,420
  throws. Window: 559 throws from 25 positions, all in T spawn.
- `mirage-smokes-density.png`, `mirage-window-density-focus.png`: density grouping. 77 spots, 3,093
  throws. Window: 607 throws from 30 positions.

What the images also show: mid and Window are crowded with overlapping icons at this zoom, and the T spawn
positions stack under count badges. Neither hides data, but the map needs a declutter pass (§6, slice 5).

## 6. Build plan, ranked

0. **Landing group key** (done on this branch, `UtilityBookTabViewModel`): key a group by its most thrown
   lineup's id. `GrenadeIndexTests.TwoLandingGroupsSeededInOneCell_BothReachTheMap` fails without it (the
   map keeps the 2-throw group and drops the 4-throw one). Fixes the reported issue on its own. Hours.
1. **Stop writing the paths sibling** for new walks; keep trajectories in memory for the current session.
   Keep the existing files until slice 4 converts them into one path per lineup, or accept straight flight
   lines for those demos (which is what the map shows for them today). Nothing reads the files now. Small.
2. **Grenade walk on forward reads.** Make grenades a forward owner in the queue (`ForwardFor` non-null),
   drop `UserCmds` from the shared background parse, capture the Grenade Walk golden on
   `demos/benchmarks` first and keep the parity check. Walker version stays 2 (rows are identical).
   Medium.
3. **Density grouping with stored anchors.** Lineup store per map with anchors and the alias table,
   incremental assignment, `Answers` over aliases, clip stem adoption. Replace the grid tests' fixtures
   (their 16-unit edge cases change meaning). Medium to large.
4. **Throw log and lineup store.** New sidecar schema, index loads compact records, one representative
   trajectory per lineup, one-off conversion of existing rows. Consumers keep their current reads through
   `IndexedGrenade` built from the log. Large.
5. **Map and card polish.** Declutter overlapping icons (merge icons that overlap on screen at the current
   zoom), aim spread and technique on the card, optionally count single throws in an icon's size. Small to
   medium.

## 7. Open decisions

1. Slice 0 now, ahead of everything else? It is one line and a test.
2. Technique split: should running, jump, and left/right/both-click throws from one spot always be separate
   lineups? The prototype says yes. The alternative is one lineup with technique counts on the card.
3. Minimum count: keep "2 or more" for the map, and should a landing icon's size count the single throws
   that land in it?
4. Single throws: keep a throw log record for every grenade forever (needed by Opening Tendencies and
   mining as they work today), or cap by demo age or by count per lineup?
5. Trajectories: one per lineup, and none for single throws? Today the app stores them all and reads none.
6. Lineup ids change once with the density store. Old ids resolve through the alias table: forever, or for
   one release?
7. Walk on forward reads: accept up to +0.8 s per demo in the walk for about 0.5-0.7 GB less peak per
   background job, and the shared parse dropping user commands?

## 8. Reproduce

- Copy the cache (`tools/GrenadesV2Analysis/load.py` header), then `python3 load.py` and any of
  `collide.py`, `compare.py`, `known.py`, `storage.py`, `top.py <map>`.
- Renders: `GV2_CACHE=<copy> dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- --treenode-filter "/*/*/GrenadesV2Probe/*"`.
  The store writes the copy's index, so never point it at the live cache. Output lands in
  `$TMPDIR/demoviewer-uitests/gv2-*`.
- Walk bench (research branch only): `DOTNET_gcServer=0 artifacts/bin/AnalysisBench/release/AnalysisBench grenade-walk --mode=retained|forward --out=rows.json <demo>`,
  one mode per process, then `cmp` the two row files.

## 9. Build (`feature/strat-book-grenades-v2-build`)

Decisions of 2026-09-28: one lineup per spot and landing with its techniques as positions under
one icon and card; full detail for every throw with no cap, stored compactly; throws keyed by player;
old ids resolve forever through a one-to-one alias map; the two-pass walk not now.

What landed, one commit per slice:

1. **Paths files off.** A walk writes no paths sibling and removes one an earlier walk left.
2. **Lineups by spot and landing** (`GrenadeLineups`, `GrenadeLineupStore`). A throw joins the nearest
   stored anchor that accepts it: release within 24 units in the plane (64 for a running throw) and 64 in
   height, landing within 192 and 96. Leftovers cluster in density order; a cluster of two or more mints
   an anchor once the library has loaded, its id a hash of the map, kind and seed throw. Anchors never
   move. Each v1 grid id maps to exactly one lineup, the one that took most of its throws, built once per
   map and then frozen. Techniques (standing or running, jump-throw or not, left, both or right click) are
   positions under the lineup: the map draws one disc per technique, the card lists the techniques with
   counts and the clicked position's throws, and the console line and clip use the technique's medoid.
   Everything lives in `<cache>/grenade-lineups.json.gz`.
3. **Throw log and lineup store.** Rows become `.grenades.log.gz`: binary, every field, positions in
   hundredths (what the JSON kept), angles as floats, one string table. Rows gain `ThrowerName`. The
   store keeps one flight per lineup technique; a demo walked in the session hands its flights to the
   index. `GrenadeStoreMigration` does the one-off conversion.
4. **Player identity.** Mined throws carry SteamID64 and name; strat capture finds the thrower's pawn by
   player before slot; Opening Tendencies takes a side's first throw from the team's own SteamIDs;
   Suggested Tags detonations carry the player, and the evidence line names them; the Utility card
   lists who threw each instance. The slot stays where a round's side or position join needs it.
5. **Map declutter.** A landing icon whose centre sits inside an icon at least as big that draws after it
   is folded into that icon's count badge at the current zoom. The hit test is unchanged.

**First launch.** 30 s after start, with the sidecar pass, the queue gets "Grenades: compact stored
throws". It waits for the grenade index to load, converts each current demo's JSON rows to a throw log
with names from the record, and deletes the JSON only when the log decodes to the same rows. Then the
index reloads, the lineup store takes one flight per technique from the old paths files (the three
throws nearest each technique's mean are tried), and the paths files are deleted only after the store
reads back with them. A pass with no failures writes `grenades-v3.done`; otherwise the next launch
retries what is left. Anchors and the alias map are built when the index first loads, before the
migration runs.

On a copy of the cache (381 demos, 95,583 throws):

| | Before | After |
|---|---|---|
| Rows | 11.8 MB gzipped JSON | 8.3 MB throw logs |
| Paths | 39.3 MB, never read | 0 |
| Lineup store | none | 12.5 MB (21,385 flights, all maps) |
| Total | 51.1 MB | 20.8 MB (-59%) |
| Migration | | 381 converted, 0 failed, 6.8 s |

The first pass on that copy failed three demos: rows with a coordinate of negative zero came back as
0, and the check kept their JSON. Negative zeros now keep their sign. Mirage after the migration:
2,168 anchors, 11,709 aliases, 3,304 flights, and every throw named.

Mirage smokes, default filters: before 74 landing spots, 402 lineups, 2,420 throws, 2,103 single
positions hidden; after 90 spots, 543 lineups, 3,419 throws, 1,104 hidden. Window: before 559 throws
from 25 positions, top 146; after 629 throws from 40 lineups, top 165 (31 demos). Renders in
`grenades-v2/`: `build-before-map.png`, `build-before-window-card.png` (feature/strat-book at
`3c26fa07` on the unmigrated copy), `build-after-map.png`, `build-after-window-card.png`.

Not done here: the flights are stored as JSON arrays and could be a third of the size in a binary
form; the store is read on first use and then held.
