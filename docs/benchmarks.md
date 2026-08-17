# Benchmarks

Living performance record for the DemoViewer.NET parser and analysis engine. All timings are Release builds (`dotnet run -c Release`), tracing disabled.

---

## Machines

Reference table for hardware used in benchmark runs.

| ID | CPU | Cores (P/L) | RAM | OS | .NET |
|----|-----|-------------|-----|-----|------|
| M1 | Apple M2 Pro | 10 / 10 | 16 GB | macOS 26.3 (arm64) | .NET 10.0.0 |

---

## Demos

| ID | File | Map | Rounds | Size (MB) | Frames | Messages | Leetify |
|----|------|-----|--------|-----------|--------|----------|---------|
| D1 | `...0544286934.dem` | de_nuke | 16 | 172.3 | 90,603 | 1,089,570 | yes |
| D2 | `...0003771537.dem` | de_mirage | 22 | 273.4 | 138,419 | 1,667,018 | yes |
| D3 | `...1066991563.dem` | de_inferno | 19 | 243.5 | 117,833 | 1,415,583 | yes |
| D4 | `...0126308730.dem` | de_ancient | 24 | 278.8 | 137,103 | 1,657,417 | yes |
| D5 | `...1168926414.dem` | de_dust2 | 21 | 248.7 | 124,495 | 1,496,966 | yes |

All demos are GOTV matchmaking recordings at 64 tick with 10 real players.

---

## Results

<!-- Append new rows at the bottom. One row per demo per version per machine. -->

| Version | Demo | Map | Parse (ms) | Build (ms) | Eval (ms) | Total (ms) | Machine |
|---------|------|-----|------------|------------|-----------|------------|---------|
| v0.0.1 | D1 | de_nuke | 1,355 | 98 | 1,790 | 3,243 | M1 |
| v0.0.1 | D2 | de_mirage | 2,134 | 16 | 2,704 | 4,853 | M1 |
| v0.0.1 | D3 | de_inferno | 1,112 | 31 | 1,272 | 2,414 | M1 |
| v0.0.1 | D4 | de_ancient | 1,411 | 18 | 2,198 | 3,627 | M1 |
| v0.0.1 | D5 | de_dust2 | 1,080 | 11 | 1,996 | 3,086 | M1 |
| v0.0.2-enrichment | D1 | de_nuke | 1,447 | 105 | 1,787 | 3,339 | M1 |
| v0.0.2-enrichment | D2 | de_mirage | 1,807 | 40 | 2,691 | 4,538 | M1 |
| v0.0.2-enrichment | D3 | de_inferno | 1,153 | 40 | 1,425 | 2,618 | M1 |
| v0.0.2-enrichment | D4 | de_ancient | 1,396 | 16 | 2,287 | 3,699 | M1 |
| v0.0.2-enrichment | D5 | de_dust2 | 1,092 | 11 | 2,041 | 3,145 | M1 |
| v0.0.3-full-yaml | D1 | de_nuke | 1,708 | 111 | 256 | 2,075 | M1 |
| v0.0.3-full-yaml | D2 | de_mirage | 1,992 | 44 | 337 | 2,373 | M1 |
| v0.0.3-full-yaml | D3 | de_inferno | 1,098 | 49 | 268 | 1,414 | M1 |
| v0.0.3-full-yaml | D4 | de_ancient | 1,299 | 16 | 310 | 1,626 | M1 |
| v0.0.3-full-yaml | D5 | de_dust2 | 1,105 | 34 | 257 | 1,396 | M1 |
| pre-rules-v2 | D1 | de_nuke | 708 | 132 | 2,918 | 3,758 | M1 |
| pre-rules-v2 | D2 | de_mirage | 437 | 37 | 1,846 | 2,320 | M1 |
| pre-rules-v2 | D3 | de_inferno | 278 | 42 | 1,391 | 1,711 | M1 |
| pre-rules-v2 | D4 | de_ancient | 586 | 19 | 1,020 | 1,625 | M1 |
| pre-rules-v2 | D5 | de_dust2 | 384 | 22 | 868 | 1,274 | M1 |
| rules-v2 | D1 | de_nuke | 644 | 159 | 2,888 | 3,691 | M1 |
| rules-v2 | D2 | de_mirage | 438 | 29 | 2,060 | 2,527 | M1 |
| rules-v2 | D3 | de_inferno | 303 | 34 | 1,544 | 1,881 | M1 |
| rules-v2 | D4 | de_ancient | 530 | 16 | 1,172 | 1,718 | M1 |
| rules-v2 | D5 | de_dust2 | 351 | 19 | 1,086 | 1,456 | M1 |
| v0.5.1 | D1 | de_nuke | 486 | 140 | 2,150 | 2,743 | M1 |
| v0.5.1 | D2 | de_mirage | 244 | 27 | 948 | 1,230 | M1 |
| v0.5.1 | D3 | de_inferno | 215 | 25 | 925 | 1,166 | M1 |
| v0.5.1 | D4 | de_ancient | 262 | 14 | 595 | 867 | M1 |
| v0.5.1 | D5 | de_dust2 | 220 | 14 | 558 | 805 | M1 |
| sdk-cutover | D1 | de_nuke | 650 | 145 | 2,518 | 3,349 | M1 |
| sdk-cutover | D2 | de_mirage | 240 | 41 | 1,434 | 1,725 | M1 |
| sdk-cutover | D3 | de_inferno | 196 | 38 | 1,081 | 1,309 | M1 |
| sdk-cutover | D4 | de_ancient | 237 | 21 | 894 | 1,165 | M1 |
| sdk-cutover | D5 | de_dust2 | 203 | 23 | 894 | 1,118 | M1 |
| snapshot-cow | D1 | de_nuke | 524 | 150 | 2,350 | 2,974 | M1 |
| snapshot-cow | D2 | de_mirage | 234 | 41 | 1,254 | 1,532 | M1 |
| snapshot-cow | D3 | de_inferno | 207 | 39 | 1,005 | 1,251 | M1 |
| snapshot-cow | D4 | de_ancient | 243 | 21 | 855 | 1,119 | M1 |
| snapshot-cow | D5 | de_dust2 | 203 | 21 | 844 | 1,062 | M1 |
| wrap-cache | D1 | de_nuke | 667 | 163 | 2,694 | 3,587 | M1 |
| wrap-cache | D2 | de_mirage | 283 | 44 | 1,468 | 1,786 | M1 |
| wrap-cache | D3 | de_inferno | 246 | 42 | 1,135 | 1,423 | M1 |
| wrap-cache | D4 | de_ancient | 282 | 23 | 938 | 1,242 | M1 |
| wrap-cache | D5 | de_dust2 | 232 | 24 | 926 | 1,180 | M1 |

_The wrap-cache batch ran on a visibly hotter machine than the same-day snapshot-cow batch —
Parse is +10–16% on code untouched between the two (e.g. D4 243 → 282 ms), so read its timing
columns as batch drift, not regression; its meaningful signal is the deterministic allocation
drop recorded in the version note._

_v0.5.1, sdk-cutover, snapshot-cow, and wrap-cache rows are the **median of 3 back-to-back suite runs** (the doc's documented
eval noise is ±0.5–0.8 s; medians tame it). Each phase column takes its own median, so a row's phases
may not sum exactly to its Total. D1 always runs first and carries the process's cold-JIT cost, same
as every prior version's D1 — compare like-for-like across versions, and read D5 (fully warm) as the
cleanest single-demo signal._

---

## Version Notes

### snapshot-cow -- 2026-08-15

The fix for the regression the sdk-cutover note bisected to the v0.5.4 rich-highlights commit.
The evaluator's per-message node snapshots cloned the full tracked-node row whenever any column
was dirty; dirty rows are rare (5,766 of 1.66M messages on D4 — measured with a throwaway
instrumented build) but highlights widened the row from 1,634 to 3,014 columns (~82 KB per clone),
making full-row cloning the dominant eval allocation (405 MiB of `NodeSnapshot[]` in the D4
allocation trace). `EvaluationResult.MessageSnapshots` is now a chunked copy-on-write
`SnapshotTable`: 64-column chunks, unchanged rows share the previous row's chunk array, dirty rows
clone only the chunks holding dirty columns, and the end-of-run padding pass is replaced by the
reader serving late-materialized columns from their at-materialization defaults.

**Value-identical:** golden regen A/B shows metadata-only fixture diffs; accuracy stays 811/139.
Gates: Analysis 997/0/114, App suite 697/0, Parser untouched.

**Eval allocation (median of 3, MiB) — now below even v0.5.1 on every demo:**

| Demo | v0.5.1 | sdk-cutover | snapshot-cow |
|------|--------|-------------|--------------|
| D1 de_nuke | 202 | 379 | **191** |
| D2 de_mirage | 281 | 531 | **248** |
| D3 de_inferno | 252 | 472 | **226** |
| D4 de_ancient | 312 | 625 | **256** |
| D5 de_dust2 | 273 | 528 | **234** |

Eval time recovers part of the v0.5.4 cost (D2 −180 ms, D1 −168 ms, D3 −76 ms, D4/D5 −40/−50 ms
vs the sdk-cutover row); the rest is the highlights feature's genuine evaluation work. Whole-run
allocation drops ~330–370 MiB per demo.

A follow-up cleanup round (**wrap-cache** — its own Results rows above) shaved a
further slice, value-identical again: a per-`EntityState` wrapper cache in `SdkEntityWorlds.Wrap`
(one SDK wrapper per live entity instead of ~540k reader+wrapper binds per demo) and typed
`Func<GameEvent, bool>` conditions in the three per-fire highlight edges, replacing per-event
`DynamicInvoke`. Median-of-3 allocation deltas vs snapshot-cow (deterministic, unlike that
batch's drift-polluted timings): eval alloc D1 191→182 · D2 248→235 · D3 226→215 · D4 256→242 ·
D5 234→222 MiB; whole-run total alloc down 50–75 MiB per demo (D4 2,157→2,081).

### sdk-cutover -- 2026-08-15

First benchmark since v0.5.1 (2026-07-24), so the row-delta spans all of v0.5.2 + v0.5.3 + the
EnemyDmg entity-HP-override fix + the GameEvents SDK migration + the CS2OpenDev.Sdk.Entities
cutover (typed entity reads now run through SDK wrappers via the SdkAbstractions seam; local
wrapper layer deleted; position cell leaves on typed lanes).

**Accuracy: 811 matched / 139 mismatched of 950 — best on record.** Per-demo: D1 164/26 · D2 166/24
· D3 159/31 · D4 156/34 · D5 166/24. The +3 vs v0.5.1 (808/142) is exactly the EnemyDmg fix
(landed 2026-08-12 with its golden re-baseline): D2 +1, D3 +2, everything else unchanged. Every
cutover stage was additionally gated on golden regen A/B (byte-identical values).

**Timing: slower than v0.5.1 across the board (Eval +300–500 ms per demo) — bisected to one
commit.** A same-day staged sweep (v0.5.1 → just before the GameEvents migration → just before the
cutover → HEAD, suite median-of-3 at each point) plus a git bisect on the deterministic
eval-allocation signal pinned effectively all of the growth on the v0.5.4 rich play-based
highlights commit (2026-08-03): at that single commit D4 eval steps 594 → 863 ms and eval
allocation 309 → 624 MiB, and the graph grows 37 nodes / 40 edges → 44 / 43. The later segments
are flat: GameEvents migration + EnemyDmg fix ≈ ±batch drift with allocation unchanged (+2 MiB —
the EnemyDmg entity-HP override itself predates v0.5.1, landed 2026-06-08, so the fix added only
same-frame capping logic, measured free), and the SDK cutover ≈ +5 ms / +1 MiB. An earlier
suspicion that the EnemyDmg fix or the seam explained the growth was wrong — this section
supersedes it. Environmental drift is also ruled out: v0.5.1's own code re-measured today within
noise of its July row (macOS 26.3 → 26.5.2 changed nothing).

**Memory (median of 3; suite = all 5 demos in ONE process, so later demos ride the accumulated
committed heap — D1 is the cleanest single-demo footprint):**

| Demo | Eval alloc (MiB) | Total alloc (MiB) | Peak managed heap (MiB) | Peak process RSS (MiB) |
|------|------------------|-------------------|--------------------------|------------------------|
| D1 de_nuke | 379 | 1,606 | 1,704 | 1,395 |
| D2 de_mirage | 531 | 2,386 | 2,558 | 1,859 |
| D3 de_inferno | 472 | 2,129 | 2,802 | 1,994 |
| D4 de_ancient | 625 | 2,526 | 2,687 | 2,141 |
| D5 de_dust2 | 528 | 2,201 | 2,949 | 2,224 |

Allocation figures are near-deterministic across the 3 runs (±0.5 MiB); peak heap/RSS vary with GC
timing (±100–300 MiB run-to-run). Eval triggers **zero Gen0/Gen1 collections** on every demo.

**Suite wall-clock: ~14.5 s** for a full 5-demo pass (14.2 / 14.5 / 14.9 s; ~68–72 s user CPU,
~4.8–5.1 cores utilized).

**Isolated SDK-adoption delta (measured 2026-08-15): time-neutral.** Cold single-process A/B of
the commit just before the cutover began (local wrapper layer still in production, EnemyDmg fix and
today's rule graph already in) vs post-cutover HEAD; 5 interleaved runs per side on D4 and
D2, medians. Single-demo cold runs carry the full JIT cost the suite amortizes across demos —
compare within this table only, never against the suite rows above.

| | D4 Eval (ms) | D4 total alloc (MiB) | D2 Eval (ms) | D2 total alloc (MiB) |
|---|---|---|---|---|
| local wrappers (pre-cutover) | 3,306 | 2,510 | 2,699 | 2,367 |
| SDK wrappers (post-cutover) | 3,290 | 2,543 | 2,851 | 2,398 |

The timing deltas (−16 ms / +152 ms) sit inside the documented ±0.5–0.8 s eval noise with
overlapping distributions. The deterministic signal is allocation: eval-phase +~1 MiB (+0.2%),
whole-run +~32 MiB (+1.3%), on both demos. The eval growth vs v0.5.1 therefore belongs to the
pre-cutover span — bisected to the v0.5.4 rich-highlights feature (see the timing paragraph
above) — not the SDK adoption.

Micro-level corroboration, now a **standing perf gate** — `EmittedWrappersPerfTests`, the SDK
battery's perf stage in the Parser suite (runs on every `CS2OpenDev.Sdk.Entities` pin bump
alongside the stage-2/3 correctness battery; tripwires on read-cost ratios and allocations):
wrapper typed read 17.6 ns vs 8.3 ns direct lane read (2.1×), seen-aware nullable read 2.5×, all
read lanes **0 B/op**; wrapper bind 69 ns / 64 B; companion resolve 145 ns / 64 B (Release, M1).
At the ~1M wrapper reads of a demo eval that is ~10 ms — consistent with the macro null result.

### v0.5.1 -- 2026-07-24

First benchmark since `rules-v2` (2026-07-15), so the row-delta vs `rules-v2` spans all of v0.5.0 +
v0.5.1. v0.5.1 itself is a Workstation-GC-motivated allocation/footprint sweep on the parser and
entity-tracking layers (`docs/perf/parser-and-entity-decode/results.md` is the living detail hub).

**Accuracy unchanged — byte-identical.** Per-demo Leetify match counts are exactly the `rules-v2`
figures (D1 164/26 · D2 164/26 · D3 154/36 · D4 155/35 · D5 166/24), and AnalysisBench stat output is
byte-identical across all 5 demos. Every perf change in this version was gated on that.

**What shipped (each byte-identical, each its own commit):**
- **Lazy field descriptors** — array-element decoders built on first use: −51% descriptor allocation.
- **`EntityState.TryGetValue`** — stop materialising the whole `Fields` dict to read one handle: −184 MiB alloc.
- **Small-int box cache** — share boxes for the 99.9%-of-writes `[0,256)` range on the fallback decode path: boxed `Int32` 403 → 73 MiB.
- **`svc_UserCmds` (subtick) deferral** — `DeferredMessage : IMessage` keeps raw wire bytes, materialises on demand for the only two consumers (Replay tab, Parser inspector): retained-while-open **919 → 705 MiB** on D4.
- **Opt-in memory-mapped demo source** (part 1 of 2) — deterministic off-heap buffer release; not yet wired into the app.

**Isolated v0.5.1 delta** (cold single-process A/B, the merge-base with main vs HEAD, D4, Server GC
both sides, so it isolates the code from the Desktop app's separate Server→Workstation switch):
**Eval −22%, Total −17%, eval-phase allocation −48% (601 → 313 MiB), peak process RSS −58%
(4222 → 1775 MB), GC collections 5/3/1 → 2/1/1.** The RSS drop exceeds the allocation drop because
Server GC's committed heap responds non-linearly to allocation pressure — the same property that
made the Desktop head's Workstation switch worthwhile, so in the shipped app the two compound.

### rules-v2 -- 2026-07-15

Rulesets v2 merged to main (the v2 DSL/engine + Authoring Workbench + the production cutover that makes v2 the main analysis path, v1 rule files dropped).

**Performance: neutral — v2 is an authoring/composition change, not a runtime one.** Measured as a same-machine A/B: `pre-rules-v2` (the commit just before the cutover, still building the v1 rules) vs `rules-v2` (then-current main). Both rows above.

- **Structurally identical graph.** Every demo builds **37 nodes / 40 edges / 0 chains** under *both* v1 and v2 — the eval workload is the same graph, so there is no runtime cost difference by construction.
- **Byte-identical accuracy.** Per-demo Leetify match counts are unchanged between v1 and v2 (D1 164/26 · D2 164/26 · D3 154/36 · D4 155/35 · D5 166/24), confirming exact v2==v1 parity on the accuracy suite (in addition to the pinned goldens).
  > **Superseded as the current numbers (2026-07-26).** The v0.5.1 ship-gate run reads
  > **D1 164/26 · D2 165/25 · D3 157/33 · D4 156/34 · D5 166/24 = 808 matched / 142 mismatched
  > of 950.** Two stats moved and nothing else: `totalAssists` −5 (its known defect was fixed) and
  > `EnemyDmg` +3 (a regression — the EnemyDmg entity-HP-override issue, tracked in
  > KNOWN-AND-SUSPECTED-ISSUES.md, present since ~2026-06-18 and already shipped in v0.5.0). The
  > row above stays as the v1-vs-v2 A/B record it was written to be.
  > Note the `tests/fixtures/*/ours.golden.json` files still hold the 2026-05-22 values —
  > deliberately, pending the EnemyDmg fix (see KNOWN-AND-SUSPECTED-ISSUES.md).
- **Timing deltas are noise.** The Eval phase varies 300–800 ms **run-to-run on the identical v2 binary** (e.g. D2 mirage: 1,252 → 2,060 ms across two back-to-back runs) — larger than any v1↔v2 gap. The v2-independent Parse/Read phases swing just as much (D4 Parse 586 → 426 ms), confirming the machine (background load / thermal / GC timing), not v2, drives the differences. The rows record one representative `rules-v2` run; treat ±0.5–0.8 s on Eval as the measurement floor here.
- **Build (composition) is cheap.** ~15–40 ms after first-demo warmup (~130–160 ms on D1 for JIT + catalog load); v2's `RulesetComposition` is not measurably more expensive than v1's config load.

> **Do not cross-compare Eval with the pre-`v0.0.3` rows.** Those ancient rows (Eval ~256 ms) predate the analysis-engine release (entity integration, per-player snapshots, entity-state scanning) — that accumulated work, *not* v2, is why Eval is now seconds. The A/B above isolates v2 by holding the modern engine fixed on both sides.

### v0.0.3-full-yaml -- 2026-04-28

All 14 plugins migrated to YAML. Removed `current_game_tick` plugin which eliminated 123K+ CNETMsg_Tick edge evaluations per demo.

**Eval (with snapshots):** 256-337ms — **~85% improvement** over v0.0.1 (1,272-2,704ms). The CNETMsg_Tick edge removal is the primary driver.

**Bare eval:** 198ms (D1 nuke). Up from 159ms baseline due to enrichment, but the full eval path is dramatically faster.

**Leetify accuracy:** 54-58%. Same systematic issues as v0.0.2 (round wins, warmup shots, KAST rounding).

**New primitives:** `threshold_tally` (multi-kill rounds), `windowed_streak` (rapid kills). Blind + clutch enrichment edges added.

### v0.0.2-enrichment -- 2026-04-28

Graph context enrichment system. 10 of 14 plugins migrated to YAML. Transient event-scoped nodes for team classification, health-capped damage, and trade detection. Expression rule type for computed stats (ADR, HS%, HLTV). Leetify comparison added to AnalysisBench (19 stats, ~56% accuracy).

**Eval (with snapshots):** Similar to v0.0.1 — enrichment adds ~10ms bare eval overhead but removes redundant plugin edges. Net impact is within noise for most demos.

**Bare eval:** 200ms (D1 nuke, was 159ms in v0.0.1). The +41ms is the enrichment system processing every kill/hurt event. Expected to improve as remaining plugins are migrated.

**Leetify accuracy:** 55-58% across all demos. Known issues: round wins returning 0 (event field handling), shots/hits counting warmup events, KAST% rounding. Core stats (K/D/A, ADR, HLTV) match within rounding.

**Dead code removed:** 9 plugin files, 10 edge files, 4 node files (1,144 lines deleted).

### v0.0.1 -- 2026-04-28

First baseline. Rule chain YAML engine with 6 chains (kast, player_totals, opening kills/deaths, deagle HS, traded deaths).

**Parse** (1.1--2.1s): 128--224 MB/s depending on demo content complexity (entity density, compression ratio).

**Build** (11--98ms): Negligible. First demo in a suite shows ~100ms from JIT warmup; subsequent runs settle to 11--31ms.

**Eval with snapshots** (1.3--2.7s): 600K--1.2M messages/sec. Processes every message, fires edges, materializes per-player sub-graphs, captures a full node snapshot at every message boundary.

**Eval bare** (no snapshots): 156--198ms. Snapshot materialization accounts for ~92% of eval time -- primary optimization target.

| Demo | Eval bare (ms) | Eval full (ms) | Snapshot overhead |
|------|----------------|----------------|-------------------|
| D1 de_nuke | 156 | 1,790 | 91% |
| D2 de_mirage | 197 | 2,704 | 93% |
| D4 de_ancient | 198 | 2,198 | 91% |

**Evaluator internals** (from `--trace`, overhead excluded from results above):

| Metric | D1 de_nuke | D2 de_mirage |
|--------|------------|--------------|
| Edges evaluated | 1,096,680 | 1,672,099 |
| Edges fired | 98,372 | 149,974 |
| Edge hit rate | 9.0% | 9.0% |
| Edges registered | 601 | 601 |
| Slowest message | CNETMsg_Tick (12.5ms) | CNETMsg_Tick (14.5ms) |

**GC pressure** (eval with snapshots):

| Demo | Eval Gen0 | Eval Gen1 |
|------|-----------|-----------|
| D1 de_nuke | 165 | 87 |
| D2 de_mirage | 252 | 133 |
| D3 de_inferno | 215 | 114 |
| D4 de_ancient | 252 | 133 |
| D5 de_dust2 | 228 | 121 |

Bare mode triggers zero GC -- all eval GC pressure comes from snapshot allocation.

**Known issues:**
- Last round has no `round_officially_ended` event (delta of 1 on every demo). Evaluator synthesizes one at demo-end.
- Snapshot overhead (92%) is acceptable for the desktop UI scrubber but batch/export paths should use bare mode.

---

## Reproducing

The suite discovers every `.dem` under `demos/benchmarks/` (a matching `<id>.leetify.json` enables the
accuracy comparison). List, then run the full suite:

```sh
dotnet run -c Release --project tools/AnalysisBench -- --list-suite
# Full suite — per-demo accuracy + phase timings, JSON reports under bench-reports/:
dotnet run -c Release --project tools/AnalysisBench -- --suite --report-dir=bench-reports --no-golden
```

`--no-golden` keeps the run from re-baselining the committed `tests/fixtures/*/*.golden.json` oracle —
omit it only for a deliberate, reviewed golden re-baseline. JSON reports in `bench-reports/` carry full
machine info, per-player stats, and metadata for automated comparison.

### With profiling (opt-in instrumentation)

All profiling is OFF by default. These add it to the suite run; each prints per demo. Full menu and
provider details: [`docs/profiling.md`](./profiling.md).

```sh
# Compile-gated per-phase breakdown — prints the Parse-Pipeline + Entity-Tracking profile trees per demo:
dotnet run -c Release --project tools/AnalysisBench -- --suite --profile --no-golden

# Runtime listeners now compose with --suite (no profile build needed) — one report per demo:
dotnet run -c Release --project tools/AnalysisBench -- --suite --no-golden --timeline   # phase timeline
dotnet run -c Release --project tools/AnalysisBench -- --suite --no-golden --counters   # evaluator counters
dotnet run -c Release --project tools/AnalysisBench -- --suite --no-golden --trace      # EventSource events

# One-switch combined report (timeline + counters) per demo:
DEMOVIEWER_PROFILE=1 dotnet run -c Release --project tools/AnalysisBench -- --suite --no-golden
```

Single-demo runs take a path instead of `--suite` (e.g. `… -- demos/benchmarks/<demo>.dem --timeline`).
`--bare` (single-demo only) runs eval without snapshots to isolate pure eval cost.
