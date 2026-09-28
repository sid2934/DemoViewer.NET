# Memory and storage at library scale (v1.0.0 input)

Measured 2026-09-27 on the 16 GB M-series Mac, branch `feature/strat-book` at 6b6d370f, while the owner's
app (Debug build, pid 27970) was walking grenades across the library in the background. Nothing here
changes product code. The measurement harness is an uncommitted test class in this worktree:
`src/App/DemoViewer.NET.App.Tests/MemoryFootprintProbe.cs` (tests `Measure`, `StreamingReads`,
`OrphanClips`, all `Environmental`, all gated on `MEM_PROBE_CONFIG`).

## 0. Headline

The library indexes are not where the gigabytes go. At 366 demos every library-wide structure together
retains about 140 MB, growing linearly at roughly 0.35 MB per demo. The live app meanwhile sat at a
physical footprint of 4.8 to 5.5 GB, with 3.5 to 4.9 GB of GC heap committed, and **1.0 to 3.4 GB of that
committed heap was free fragmentation, mostly in the large object heap**. No code path requests LOH
compaction during background processing. The second problem is disk: lineup clip GIFs already take 747 MB for 106 walked
demos, grow faster than linearly, are never evicted, and 19% of them are already orphans.

## 1. Leaks and unbounded structures (read these first)

| # | What | Evidence | Bound |
|---|------|----------|-------|
| L1 | **LOH fragmentation is never reclaimed during background work.** The only compacting collect in the app is `CloseDemoAsync` (`ViewModels/Shell/MainViewModel.cs:3508-3514`), and Workstation GC with `ConserveMemory` unset does not compact the LOH on its own. A likely contributor, not yet isolated from the parser's own large arrays: every background parse reads the whole `.dem` into one `byte[]` on the LOH (`Services/DemoProcessing/DemoProcessingQueue.cs:87`, `Modules/UtilityBook/LineupClipService.cs:298`), and demos differ in size, so freed blocks rarely fit the next one. | Live counters, 6 samples over 18 min: LOH 2.6-3.5 GB with 0.9-2.6 GB of it free; gen2 0.8-2.0 GB with 0.03-1.2 GB free; committed 3.5-4.9 GB. `footprint`: 4.8-5.5 GB, 5.15 GB of it untagged VM_ALLOCATE (GC regions). | Grows with the spread of demo sizes and job count until a demo is closed. |
| L2 | **Lineup clips: unbounded disk, orphans, one full parse per render batch.** Nothing deletes from `lineup-clips/`. The stem hashes the representative throw's key (`Modules/UtilityBook/LineupClipPlanner.cs:235-241`); the representative is the first throw ordered by demo path (`GrenadeIndex.cs:458-460`), so a new demo that sorts earlier re-keys the lineup, renders a new GIF with a new parse, and orphans the old pair. | At 106 walked demos: 2,288 lineups planned, 1,177 GIFs on disk (747 MB, median 605 KB), **228 orphans (144 MB)**, 1,339 still to render. Lineups planned: 404 at 25 walked demos, 920 at 48, 2,288 at 106 (superlinear). | None. |
| L3 | **ReviewQueue grows without bound.** Every `Plan` that finds new jobs appends a new section header per map (`LineupClipService.cs:154`) and entries are never superseded (`Services/Review/ReviewQueue.cs:39`); `LineupClipService._planned` only grows (`:67`). | `review-queue.json`: 2,057 entries (61 sections) at 01:55, 3,386 entries (113 sections) at 03:57, 1.0 MB to 1.6 MB. | None. |
| L4 | **Queue history can root whole parsed demos.** Terminal entries keep `ForegroundWaiters` (`DemoProcessingQueue.cs:760`), whose results are the `ParsedDemo`; the list is copied at `:539` but never cleared, and 30 terminal entries are kept (`:42`). A foreground open that coalesced onto a background parse keeps its multi-GB demo rooted until 30 more jobs finish. In a Debug build the `parsed` local (`:469`) is also hoisted into the async state machine and stays rooted while the worker waits for the next slot. The owner runs Debug. | Code reading; not measured (needs a parse). | 30 entries, but each can be GBs. |
| L5 | **UtilityBook reloads map art on every index change and never disposes the old one.** The singleton VM subscribes to `GrenadeIndex.Changed` (`ViewModels/UtilityBook/UtilityBookTabViewModel.cs:127`); `Refresh` calls `RebindMap` (`:225`), which calls `MapAssetPipeline.TryLoad` (`:327-340`); that returns a new `LoadedMapAsset` with new `SKImage`s each time (`Playback2D.Pipeline/Assets/MapAssetPipeline.cs:243-282`), so the `ReferenceEquals` early-out never fires and the old asset waits for its finalizer. Also re-binds the scene. Only once the tab VM exists. | Code reading. | Native churn per walked demo, released only by finalization. |
| L6 | Dossier heatmap `Bitmap`s are cleared without dispose (`ViewModels/Dossier/DossierTabViewModel.cs:501`), and a rebuild runs on every `TeamIdentityService.Changed` while a team is selected. `SuggestedTagsTuningService._cache` (`Modules/SuggestedTags/SuggestedTagsTuningService.cs:34`) holds per-demo detection inputs with no cap (Settings tuning preview only). | Code reading. | Per rebuild / per previewed demo. |
| L7 | SituationIndex per-map token table never shrinks when demos leave (`Services/RoundIndex/SituationIndex.cs:607-612`, `Remove` at `:684-740`). | Code reading. | Distinct tokens per map; small. |

Not a leak: Strat Mining. Patterns retained are about 0.5 MB and the previous list is released on
publish. It is a CPU and allocation churn problem (section 3, F4).

## 2. Baseline

### Method

- Library: a copy of the live config (`rsync`, no logs, no lineup clips) into scratch. 366 demos in the
  cache index, 355 with round-index sidecars, 53 with grenade sidecars (a second copy an hour later had
  106). No `cache/strat-mining/` yet, so auto re-mining is not armed in the owner's app.
- Subsets: 90 and 180 demos, random sample with a fixed seed, by filtering `cache/index.json`.
- Host: the App.Tests TUnit process, Debug build, forced to the app's GC config
  (`DOTNET_gcServer=0`, concurrent on, `DOTNET_GCConserveMemory=0` to override the test project's 5).
- Retained = managed heap minus fragmentation (`GC.GetGCMemoryInfo(GCKind.FullBlocking)`) after an
  aggressive compacting full GC, before and after constructing and loading each service, with the
  result kept alive. Alloc = `GC.GetTotalAllocatedBytes(true)` delta. Peak = 10 ms sampler on
  `Environment.WorkingSet` and heap size.
- Cross-check: a `dotnet-gcdump` of the probe at 366 demos reported 155 MB of reachable heap for the
  whole process including the test host, which agrees with the per-phase sum below. The first metric I
  tried, `GC.GetTotalMemory`, overstated by up to 2x and was not monotone across sizes; I discarded it.
- The probe's END line reports a heap about 2x the per-phase sum (92 / 152 / 284 MB against ~45 / 76 /
  141 MB). The gcdump shows the collections the probe loads and drops (records, grenade documents) at zero
  and one copy of each retained structure, so the gap is not held by the services and the per-phase
  deltas stand.
- Timings are Debug-build and indicative only.

### Retained memory per subsystem

| Subsystem (steady state, singleton) | 90 demos | 180 demos | 366 demos | Marginal per demo |
|---|---|---|---|---|
| `SituationIndex` (postings + retained documents) | 34.1 MB | 62.1 MB | 119.7 MB | ~330 KB |
| of which: full `RoundIndexDocument` kept per demo | 16.1 MB | 31.4 MB | 64.6 MB | ~180 KB |
| `GrenadeIndex` (15 / 24 / 53 walked demos) | 8.7 MB | 10.7 MB | 16.6 MB | ~200 KB per walked demo, plus ~5 MB of zone resolvers |
| `DemoCacheStore` index | 0.9 MB | 1.6 MB | 3.0 MB | ~8 KB |
| `TeamIdentityService` | 0.7 MB | 0.9 MB | 1.5 MB | ~4 KB |
| `StratMiningService` patterns | 0.3 MB | 0.4 MB | 0.5 MB | small |
| Tags, proposals, Round Facts, Dossier services | ~0 | ~0 | ~0 | read per call, no cache |
| **Total** | **~45 MB** | **~76 MB** | **~141 MB** | **~0.35-0.55 MB** |

Projection, linear: with every demo grenade-walked, 366 demos is about 200 MB; 1,000 demos about
550 MB; 2,000 demos about 1.1 GB. Not the problem today, a real one at 2,000.

What SituationIndex holds (gcdump counts at 366): 289k `RoundIndexRun` objects with 578k separate token
strings (avg 32 chars, never interned), 289k `DecodedRun` records, 436k `Posting` records (class, one heap
object each), 159k `List<Posting>` (one per token per side, most nearly empty), 79k `TokenInfo`.

### Transient cost per pass (366 demos, nothing retained afterwards)

| Operation | Allocated | Peak heap | Time (Debug) | Scaling 90 / 180 / 366 |
|---|---|---|---|---|
| `StratMiningService.MineAsync` (one full mine) | 2.39 GB | ~520 MB | 10.3 s | 462 MB, 1.1 s / 935 MB, 2.8 s / 2.39 GB, 10.3 s |
| of which `RoundSignatureBuilder.Build` | 1.96 GB | ~510 MB | 3.3 s | ~5 MB per demo, linear |
| of which `StratMiner.Mine` (largest group n=748) | 430 MB | small | 6.9 s | 27 MB, 0.44 s / 100 MB, 1.7 s / 430 MB, 6.9 s: about n^2.4 |
| `RoundIndexStore.TryReadPositions` over the library | 507 MB | +140 MB WS | 0.9-1.2 s | 1.43 MB per demo read |
| `DemoCacheStore.TryLoadRecord` over the library | 266 MB | +20-50 MB WS | 0.2-0.5 s | 0.73 MB per record read |
| `DemoCacheStore.LoadRecords` (all held at once) | 269 MB | 56 MB retained while held | 0.45 s | 153 KB per record in memory |
| Dossier: SetupHeatmap / Openings / PostPlant | ~11 MB per team demo each | small | ~25 ms per demo | linear in team demos |
| Dossier: SituationalBehaviour | 17.7 MB per team demo | small | 33 ms per demo | see F5 |
| `GrenadeIndex` Rows+Cluster, every map | 49 MB | small | 150 ms | linear in grenades |

Projection for a mine at 1,000 demos (largest group about 2,000): roughly 70 s of `StratMiner` CPU and
3 GB for the miner alone, 5 GB for signatures; at 2,000 demos minutes per mine and a 64 MB `float[n,n]`
on the LOH per group. Extrapolated from the three points above, not measured.

### Disk

Copy of the live config at 366 demos (53 grenade-walked); lineup clips from the live directory at 106
walked demos.

| Kind | Files | Total | Per demo | Compact JSON | gzip of compact |
|---|---|---|---|---|---|
| `lineup-clips/*.gif` + `.setpos.txt` | 1,177 + 1,154 | **747 MB** | ~7 MB per walked demo so far, rising | n/a | n/a |
| `cache/demos/<key>.json` (record, indented) | 366 | 48.0 MB | 131 KB | 29.1 MB (-39%) | 3.5 MB (-93%) |
| `cache/round-index/*.dvri.json` | 355 | 21.4 MB | 60 KB | already compact | 3.3 MB (-84%) |
| `cache/round-index/*.dvrp.json.gz` | 355 | 20.8 MB | 58 KB | 68.5 MB raw | already gzip |
| `cache/demos/*.grenades.paths.json` | 53 | 18.7 MB | 353 KB | already compact | 6.8 MB (-63%) |
| `cache/demos/*.grenades.json` | 53 | 14.7 MB | 278 KB | already compact | 2.0 MB (-86%) |
| `highlights.json` (legacy, read once by migration) | 1 | 1.4 MB | | | |
| `review-queue.json` | 1 | 1.0-1.6 MB | grows, see L3 | | |
| `cache/index.json`, `team-index.json`, `library.json` | 3 | 1.5 MB | ~4 KB | rewritten in full on most writes | |

Per demo, the cache is about 880 KB once grenade-walked (631 KB of it grenade sidecars). Record
contents: RoundFacts 61%, Highlights 30%, HighlightHashes 6%. Inside RoundFacts rows, the per-round
`Sources` provenance map is 36%, Kills 29%, the two SideFacts 32%.

## 3. Top findings (what grows with library size, and why)

**F1. Committed heap is mostly fragmentation from whole-file demo reads, not library state.** See L1.
The library indexes are ~140 MB of a 3.5-4.9 GB committed heap. Background parsing (the queue, and a
second full parse per lineup render batch) puts a 100-400 MB `byte[]` plus the parser's own large arrays
on the LOH per job, and the LOH is only compacted when the user closes a demo. The csproj comment's
"112 MB resident after close" is true only because close runs an aggressive compacting collect.
(`DemoViewer.NET.Desktop.csproj:9-12`, `MainViewModel.cs:3508-3514`.)

**F2. Lineup clips are the largest and fastest-growing disk consumer, and they re-parse demos.** See L2.
At full coverage the planned lineup count extrapolates to several thousand GIFs, i.e. multiple GB, each
render batch a full `DemoParser.Parse` outside the processing queue's single parse
(`LineupClipService.cs:298-314`).

**F3. SituationIndex keeps every demo's whole `.dvri.json` object graph.** `LoadedDemo.Document`
(`SituationIndex.cs:582`) is read back only for `Places`, `Transitions` (removal, `:694`, `:722`) and
`Clock.TickRate` (`:480`), yet it holds every run with two uninterned token strings. That is 54% of the
index (64.6 of 119.7 MB at 366). The rest is object overhead: `Posting` and `DecodedRun` are record
classes (`:551`, `:601`).

**F4. Every strat mine re-reads and re-deserializes the whole library.** `RoundSignatureBuilder.Build`
loads every record and every positions file (`RoundSignatureBuilder.cs:116-122`) to produce signatures that
total only 4.8 MB, then throws them away. `StratMiner` allocates a `double[,]`, a `bool[]` and a closure
per pair cost (`StratMiner.cs:116-160`), n^2/2 times, and is about n^2.4 in time. Once `detected.json`
exists, any 30 s gap in cache events triggers a full mine (`StratMiningService.cs:399-411`), outside the
HeavyJobGate, so during a long background run it lands on top of a live parse.

**F5. Read paths double or triple the bytes and re-read the same record many times.** Every sidecar
read is whole-file to string, then reflection STJ (`DemoCacheStore.cs:274-284`,
`RoundPositionsModels.cs:128-141`, `GrenadeSidecar.cs:230-240`); the positions path holds compressed
bytes, a UTF-16 string of the inflated JSON, then the objects. `TeamIdentityService.SideAtRound`
(`TeamIdentityService.cs:568-593`) loads the record twice per call and is called per round, so the
Situational Dossier deserializes about 49 records per demo (17.7 MB per demo measured). The capacity-1
`_lastRecordJson` cache (`DemoCacheStore.cs:99-100`) still deserializes on every hit.

## 4. Options, ranked

Savings at 366 demos unless stated. "Measured" means a prototype in the probe; "estimated" gives the
basis.

### Quick wins (small, low risk, no format change)

| Rank | Option | RAM saving | Disk saving | Perf cost | Size | Risk | Owner decision |
|---|---|---|---|---|---|---|---|
| Q1 | **Compact the LOH after background jobs.** Either set `System.GC.ConserveMemory` to 5-7 in the Desktop csproj (any non-zero value makes the GC compact the LOH when it is too fragmented), or run the same `CompactOnce` + blocking gen2 that `CloseDemoAsync` runs when the processing queue drains or every N jobs. | Estimated 1-3 GB of committed heap: the live free-LOH figure was 0.9-2.6 GB. | 0 | ConserveMemory: more gen2 GCs during a parse, unmeasured for this workload. Explicit compact: one blocking full GC per drain, about what close already pays. | 1-10 lines | Low | Yes: choose knob vs explicit collect; wants an `AnalysisBench gc-sweep` run over sequential background parses (section 5). |
| Q2 | **Stop the lineup-clip bleed:** key the stem by lineup id (`GrenadeIndex.LineupId`), not the representative throw; delete a lineup's previous pair when it re-plans; sweep GIFs no current lineup plans. | Removes repeat parses. | 144 MB today (19%), and every future re-key. | None | ~50 lines | Low | No |
| Q3 | **Cap and dedupe the ReviewQueue:** one section per map, replace a lineup's entry instead of appending. | Small | 0.6 MB so far, unbounded | None | ~30 lines | Low | No |
| Q4 | **Clear `ForegroundWaiters` after signalling** (`DemoProcessingQueue.cs:539-563`), and null `parsed` before the next await. | Up to one or more whole `ParsedDemo`s (GBs) in the coalesced-open case. | 0 | None | 3 lines | Very low | No |
| Q5 | **Stream reads into UTF-8** instead of `ReadAllText`/`ReadToEnd`: `JsonSerializer.Deserialize(FileStream)` for records; inflate positions into a reused buffer and deserialize the span. | Measured: record reads 266 → 68 MB allocated (-75%), time 230 → 180-200 ms; positions 507 → 207 MB (-59%), time 860 → 630 ms (-25%) with the reused buffer (a plain streamed `GZipStream` was 20% slower). Removes a 260-400 KB LOH string per read. Mining and every Dossier build benefit. | 0 | Faster | ~40 lines, 4 sites | Low | No |
| Q6 | **Fix the record re-reads:** pass the loaded rows into `SideAtRound` (or memo per build); drop `_lastRecordJson` or cache the object. | SituationalBehaviour 17.7 → ~0.7 MB per team demo (estimated from 49 deserializations per demo to 1). | 0 | Faster | ~20 lines | Low | No |
| Q7 | **UtilityBook: skip `RebindMap` when the map did not change, dispose the replaced asset.** Same for Dossier heatmap bitmaps. | Native churn per walked demo | 0 | Faster | ~10 lines | Low | No |
| Q8 | **Write the record compact** (`WriteIndented = false`, `DemoCacheStore.cs:35-38`). | Small | Measured 48.0 → 29.1 MB (-39%) | Slightly faster writes | 1 line | Low (readers accept both) | No |

Q9. **Parse background demos from a memory map.** The pinned `CS2DemoKit.Parser` 0.13.0-beta0001 already
ships `MemoryMappedDemoSource.ParseFile(path)` (map, parse, unmap; the mapping never escapes). Swapping the
two `DemoParser.Parse(File.ReadAllBytes(path))` defaults (`DemoProcessingQueue.cs:87`,
`LineupClipService.cs:298`) takes the 100-400 MB input array off the LOH per job, into file-backed pages the
kernel can evict. RAM saving: the size of each demo, per job, plus whatever share of L1 it accounts for
(section 5, item 1). Perf: unmeasured; page faults on sequential first touch. Size: 2 lines. Risk: low.
No change to the parser source, so no protected-file approval is needed; owner call only because it
changes the parse path.

### Structural changes

| Rank | Option | RAM saving | Disk saving | Perf cost | Size | Risk | Owner decision |
|---|---|---|---|---|---|---|---|
| S1 | **Parse less in the background.** The pinned parser has `ParseOptions.Plan` (`DecodePlan` presets that never materialise unplanned payloads) and a forward-only `DemoReader.OpenFile` that keeps only the current frame. The background evaluators and the lineup renderer (which needs a few seconds per clip) all take a full `ParsedDemo` today. Move them to a narrower plan or the reader. | Unmeasured. Live gen2 during processing was 0.8-2.0 GB and the retained parse is most of it; this is likely the largest RAM lever not yet measured. | 0 | Faster parse with a narrower plan | Large: every evaluator's input contract | Medium-high | Yes. Package API only as it stands; if a new parser option is needed it lands in CS2DemoKit's DemoParser.cs, which is protected and needs explicit approval. |
| S2 | **Slim SituationIndex:** drop `LoadedDemo.Document` (keep a small Places/Transitions summary and the tick rate), make `Posting`/`DecodedRun` structs, one flat postings array per token, intern token strings per map. | Estimated 75-85 MB of 120 (-65%): 64.6 MB measured for the documents alone, plus ~20 MB of per-object headers from the gcdump counts. At 2,000 demos about 400 MB. | 0 | Load slightly faster (fewer objects); queries unchanged | ~200 lines in one file | Low-medium: removal path must stay exact | No |
| S3 | **Cache mining signatures per demo** (keyed by sha, positions fingerprint, grenade walker version) and rebuild only changed demos; pool the pair-cost buffers in `StratMiner`; suppress auto re-mine while the processing queue is non-empty and take a HeavyJobGate background slot. | Measured basis: signatures are 4.8 MB retained at 366; rebuilding them costs 1.96 GB and 3.3 s per mine. Re-mine drops to ~430 MB (miner only), and pooling removes most of that. | 0 | Much faster re-mines | Medium | Medium: invalidation keys | Yes: whether auto re-mine may run during background processing at all. |
| S4 | **Lineup clip policy:** render on demand (when a card is opened) or cap by count/bytes with LRU eviction; smaller GIFs (lower fps or 360 px). | Removes background full parses | Estimated several GB at full coverage (2,288 lineups at 106 demos, x ~605 KB) | Clip appears later for unseen cards | Medium | Low | Yes: product call on always-on clips. |
| S5 | **Compress or split the record:** gzip the record like `.dvrp`, or move Highlights (36% incl. hashes) and RoundFacts into siblings so readers load only what they use; store `Sources` once per demo instead of per round. | Per-read alloc drops with the smaller graph | Measured: gzip of compact JSON 48.0 → 3.5 MB (-93%); whole cache ~124 MB → ~37 MB at current coverage | gzip inflate is cheap next to STJ | Medium, touches many readers | Medium: format version bump, migration | Yes (format change) |
| S6 | **Grenade rows:** keep only what `IndexedGrenade` queries need, SteamID as `ulong`, share `PlaceSource`, no empty `Trajectory` list per row, gzip the two grenade sidecars. | Estimated 20-30% of GrenadeIndex (~15-25 MB at full coverage). | Measured gzip: 33.4 → 8.8 MB at 53 demos (-74%) | Neutral | Small-medium | Low | No |
| S7 | **Server GC with DATAS** to win back the ~32% parse speed that Workstation cost. | Negative unless paired with Q1; DATAS alone did not beat Workstation in the earlier sweep. | 0 | Faster parse | Config only | Medium | Yes; rerun gc-sweep first. |

Rejected: `GCHeapHardLimit` (a single parse peaks near 2.9 GB and a limit below that throws OOM instead of
paging), `RetainVM` (holds freed memory), `Half` positions (±2-4 unit error at map scale; `RoundPosition`
is already quantised ints).

## 5. Not measured, and how to measure later

The earlier "one open demo = 5-7 GB (eager analysis + Server GC)" characterisation is half stale: the app
ships Workstation GC now (`src/App/DemoViewer.NET.Desktop/DemoViewer.NET.Desktop.csproj:9-12`, and both
built runtimeconfigs say `System.GC.Server: false`). Two comments still describe Server GC as shipped and
should be corrected: `MainViewModel.cs:3453` and `tools/AnalysisBench/GcSweepCommand.cs:47`. The
open-demo peak under Workstation was not re-measured here (item 7).

The owner's background run ruled out any parse here. Still open:

1. **Q1 and S1 effect on committed heap during background processing.** Extend `AnalysisBench gc-sweep`
   with a scenario that parses 10-20 demos of varying size back to back without closing, reporting
   `GCMemoryInfo` committed and fragmented bytes after each, for: Workstation (today), Workstation +
   ConserveMemory 5 and 7, Workstation + explicit CompactOnce per job, Server + DATAS. Run it when the
   machine is otherwise idle.
2. **Attribution of the live app's 0.8-2.0 GB gen2.** Once the background run is finished and the app is
   idle: `dotnet-gcdump collect -p <pid>` then `dotnet-gcdump report`. It forces a full blocking GC and
   walks the heap, so not during processing. Compare a Release build to separate the Debug-only
   `parsed` hoisting (L4).
3. **Native memory**: `footprint <pid>` showed 230 MB MALLOC_SMALL and ~110 MB of graphics; a
   `vmmap --summary` after a long UtilityBook session would show whether L5's undisposed `SKImage`s
   accumulate.
4. **Re-mine churn in practice**: the owner's app has not mined yet. After the first mine, count
   "service mine" log lines per background run.
5. **Grenade index at full coverage**: re-run `Measure` on a copy after the walk finishes (projected
   ~80 MB at 366 walked demos), and `OrphanClips` to confirm the lineup and orphan growth curve.
6. **Release-build timings** for the options table.
7. **One open demo under Workstation GC**: with the machine idle, open one large demo, wait for analysis
   to finish, then `footprint <pid>` and `dotnet-gcdump collect -p <pid>`; close it and repeat to confirm
   the close-time compaction still returns to ~100 MB.
8. **Q9 and S1 parse-time cost**: time `MemoryMappedDemoSource.ParseFile` against the current
   `ReadAllBytes` path, and a narrow `DecodePlan` against the full one, in AnalysisBench.

Commands (from `src/App/DemoViewer.NET.App.Tests`, after `dotnet build -c Debug`):

    MEM_PROBE_CONFIG=<config copy> MEM_PROBE_OUT=<file> DOTNET_gcServer=0 DOTNET_GCConserveMemory=0 \
      dotnet run -c Debug --no-build -- --treenode-filter "/*/*/MemoryFootprintProbe/Measure"
    # add MEM_PROBE_WAIT=<sentinel file> to pause at the end for dotnet-gcdump
    ... --treenode-filter "/*/*/MemoryFootprintProbe/StreamingReads"
    CLIPS_DIR=<lineup-clips dir> ... --treenode-filter "/*/*/MemoryFootprintProbe/OrphanClips"

Never point `MEM_PROBE_CONFIG` at the live config: `Measure` sweeps orphan sidecars and TeamIdentity
rewrites its files.

## 6. Status (2026-09-27)

Built and merged into `feature/strat-book`, each on its own branch with a review pass:

- `feature/strat-book-perf-queue`: L4/Q4 (history no longer roots parsed demos), Q9 (queue parses settled
  files through a memory map, `ReadAllBytes` for anything written in the last 60 s), L1/Q1 as an explicit
  compaction when the queue drains, throttled to once per 30 s with a deferred run, inside a background
  gate slot. No GC knob changed.
- `feature/strat-book-perf-clips`: L2/Q2 (clip stems by lineup id, renames instead of re-renders, orphan
  sweep after a 10 min grace), L3/Q3 (one review section per map, one entry per lineup), S4 as a byte cap
  (`Grenades.LineupClipsMaxMegabytes`, default 1024) with oldest-first eviction, the renderer's mapped parse
  behind the same settled-file check, L5/Q7 and L6.
- `feature/strat-book-perf-sidecars`: Q5, Q6, Q8, S5 (gzipped records), S6 (gzipped grenade sidecars,
  slimmer rows), with verify-before-delete and a one-off background conversion of existing files.
- `feature/strat-book-perf-index`: S2 and L7 (SituationIndex 136 to 66 MB retained on 366 synthetic demos),
  S3 (per-demo signature cache: a second mine 10 s / 2.3 GB allocated before, 5.9 s / 66 MB after; the gate
  is taken per 16-demo batch; no quiet re-mine while the processing queue has work).

Still open, because each needs a real parse to measure and the owner's library was being processed:
S1 (narrower background parse plans), S7 (Server GC with DATAS), and the gc-sweep that would say whether
`ConserveMemory` beats the drain compaction. Nothing shows the lineup clips in the app today, so rendering
them on demand instead of in the background would remove their parses at no visible cost; that is an owner
call.

Follow-up, same day: after the owner's library finished processing, the queue list was empty while
`LineupClipService` kept rendering on its own worker (92% CPU, 71 GIFs in three minutes, a 1 GB cap full
with 4,361 clips evicted). Owner rule: all background work is reported and managed by the one queue.
`feature/strat-book-bg-queue` makes demo processing, lineup clips (one item per demo), strat mining, the
sidecar migration, pack export and heap compaction queue items with a title, state and progress; pause,
cancel and priority apply to all of them, and non-parse jobs run alone at any concurrency. Clips still render
in the background, most-thrown lineups first, and stop at the cap unless a lineup outranks the lowest kept
clip; a clip is written to a temp file and renamed only when it finishes.
