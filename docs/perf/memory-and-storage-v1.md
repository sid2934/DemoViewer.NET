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

## 7. Measurements, 2026-09-27

### Method

- Branch `feature/strat-book-perf-measure` off `feature/strat-book` at e7ea8c09. Release build of
  `tools/AnalysisBench`, new verb `bg-run --list=<file>` (`BackgroundRunCommand.cs`, `BackgroundPlans.cs`).
  It parses each listed demo, optionally runs the highlights harvester's snapshot-free rules evaluation
  (`--eval`, all shipped rulesets including round_facts), drops it, and reports committed heap, LOH size
  and LOH free after every job. `--compact=end` runs the app's `HeapCompactor` sequence once after the
  last demo (a queue drain) and reports the state before and after it; `--compact=each` runs it after
  every job. `--read=mmap|bytes|forward`, `--plan=<preset>`.
- One process per run, GC chosen by `DOTNET_*` env vars (`GCSettings.IsServerGC` confirmed per run).
  Workstation runs use `DOTNET_gcServer=0 DOTNET_gcConcurrent=1`, the Desktop app's config.
- Peak memory from `/usr/bin/time -l`. "Peak memory footprint" (fp) excludes clean file-backed pages, so
  it is the right number for mapped parses; max RSS counts resident mapped pages and makes the two read
  paths look the same. Peak heap from a 20 ms sampler.
- Configurations interleaved, order reversed between repetitions. The owner's app was not running
  during any measurement.
- **The library is on NFS** (`/Users/austingray/Demos` is `192.168.1.7:/mnt/user/Demos`). A cold
  ~270 MB demo takes 6-8 s to parse against ~1 s warm, so cold wall time is network-bound. The GC
  comparison uses eval seconds (job time minus parse time) as its cost figure for that reason.
- Open-demo peak: the existing `gc-sweep` probe, Workstation concurrent config only (`ReadAllBytes`,
  parse, build, snapshot evaluation, then the close compaction), 2 reps per demo.

Demos: MM is a 276 MB matchmaking demo, Pro a 380 MB BLAST demo, Big the largest in the library (750 MB,
PGL). The sequential run is 14 demos (11 MM, 3 pro, 188-392 MB, 3.9 GB total, fixed seed).

### Q9: mapped parse vs `ReadAllBytes` (parse only, Workstation)

Warm cache, reps 2-5 (rep 1 of each mapped run was the first read of the file):

| Demo | Read | Parse s | Peak footprint | Peak heap | Committed after parse | LOH after parse |
|---|---|---|---|---|---|---|
| MM 276 MB | mmap | 0.8-1.0 | 557 MB | 733 MB | 524 MB | 276 MB |
| | bytes | 0.8-0.9 | 831 MB | 1,005 MB | 797 MB | 554 MB |
| Pro 380 MB | mmap | 1.1-1.2 | 766 MB | 786 MB | 729 MB | 397 MB |
| | bytes | 0.9-1.4 | 1,148 MB | 1,167 MB | 1,111 MB | 776 MB |
| Big 750 MB | mmap | 1.5-1.7 | 1,558 MB | 1,572 MB | 1,543 MB | 979 MB |
| | bytes | 1.6-1.9 | 2,306 MB | 2,332 MB | 2,290 MB | 1,729 MB |

Cold over NFS, 10 different unread MM demos of 251-299 MB, alternating: mmap 6.4 / 6.6 / 7.1 / 7.3 / 7.7 s,
bytes 7.3 / 7.2 / 7.2 / 7.2 / 6.2 s; footprint mmap 507-583 MB, bytes 766-836 MB. Three warm runs hit
6.8-7.2 s with no pattern by mode (likely NFS revalidation; not isolated).

Max RSS is identical for both modes (856 / 1,168 / 2,330 MB): the mapped pages are resident, but clean
and evictable.

### Q1 and S7: GC configurations over a 14-demo background run

Mapped parse plus bare rules evaluation per demo, back to back. Two reps each, shown as rep 1 / rep 2 or
as a range. "During" is over the 14 per-job samples; "before drain" is after the last job, "after drain"
after `HeapCompactor`. MB unless marked. The per-job sample follows the last GC, which in the
"compact after every job" row is the forced compaction, so that row's "during" columns are the floor each
job leaves for the next, not the level inside a job; compare it on peak footprint.

| Config | Eval s | Peak fp | Committed during, max / median | LOH free during, max / median | Before drain: committed / LOH / LOH free | After drain committed | gen2 | GC pause |
|---|---|---|---|---|---|---|---|---|
| Workstation (today) | 16.3 / 16.2 | 1,573 / 1,613 | 1,469-1,508 / 1,069-1,082 | 357-427 / 253-270 | 896-1,168 / 504-532 / 276-311 | 99 / 148 | 30 / 28 | 5.6 / 5.5 s |
| + ConserveMemory 5 | 16.0 / 16.1 | 1,481 / 1,468 | 1,312-1,326 / 840-943 | 247-341 / 97-132 | 522-1,061 / 220-467 / 0-247 | 83 / 21 | 31 / 32 | 5.5 / 5.7 s |
| + ConserveMemory 7 | 16.7 / 17.0 | 1,464 / 1,453 | 1,066-1,070 / 629-648 | 128-134 / 0 | 525-566 / 219-220 / 0 | 22 / 49 | 38 / 37 | 6.3 / 6.3 s |
| + compact after every job | 16.3 / 15.5 | 1,004 / 995 | 228-229 / 73-81 | 0 / 0 | 50 / 2 / 0 | 50 / 50 | 68 / 71 | 5.7 / 5.6 s |
| Server + DATAS | 12.7 / 12.7 | 2,671 / 2,700 | 2,467-2,477 / 1,928-1,960 | 616-632 / 288-335 | 2,140-2,148 / 606-607 / 288-369 | **1,538 / 1,545** | 13 / 14 | 0.4 / 0.4 s |

All configs produced the same 3,848 highlights and allocated 10.8 GB. Wall time was 102-135 s, of which
86-119 s was parse. The parse sum tracked the config in both orders (compact every job 86-88 s, CM7
94-95 s, CM5 108-109 s, Workstation 114-119 s, DATAS 113-119 s). The cause is not isolated; plausibly
page-cache pressure on the NFS-backed mapped pages. It is not GC cost.

Reading:

- The drain compaction the queue does today works at the drain: 896-1,168 MB down to 99-148 MB. It does
  nothing during the run, which is where the live app sat at 3.5-4.9 GB. Committed heap peaks near
  1.5 GB mid-run even in this bench, where nothing else is resident.
- ConserveMemory 7 cuts mid-run committed by 28% at the peak and 40% at the median and keeps LOH free at
  0 for most jobs, for +4% eval and +14% pause. ConserveMemory 5 barely moves it.
- Compacting after every job cuts peak footprint 1,573-1,613 to 995-1,004 MB (-37%) at no measurable
  eval cost here, and leaves ~50-80 MB committed for the next job instead of 0.9-1.5 GB of mostly
  fragmented heap. That carried-over fragmentation is what grew to 3.5-4.9 GB in the live app. The bench's live heap between jobs is ~50 MB; in the app it is the
  library indexes plus UI (a few hundred MB), so each compaction costs more there, likely 100-300 ms of
  blocking gen2 per 5-10 s job (estimated, not measured).
- Server + DATAS evaluates 22% faster but commits 2.5 GB mid-run and keeps **1.5 GB committed after the
  compaction**: it does not give memory back. Rejected.

### Open-demo peak under Workstation GC

`gc-sweep` probe, Workstation concurrent, `ReadAllBytes` then parse, build, snapshot evaluation:

| Demo | Total s (warm) | Peak heap | Peak RSS | Live while open | After close: committed / RSS |
|---|---|---|---|---|---|
| MM 276 MB | 3.2 (rep 2 read cold: 9.5) | 1,044-1,108 MB | 1,046-1,090 MB | 795 MB | 106-124 / 207-227 MB |
| Pro 380 MB | 3.7-3.9 | 1,492 MB | 1,481-1,491 MB | 1,095 MB | 23-25 / 130-131 MB |
| Big 750 MB | 4.5-4.6 | 2,716 MB | 2,687 MB | 2,208 MB | 18 / 128 MB |

The "5-7 GB, Server GC" note is stale: the analysis path peaks at 1.1-2.7 GB and close returns to
~130-230 MB. Not covered: the open fan-out (`MainViewModel.cs:3968`: Round Facts, Round Index, Suggested
Tags and Grenades on the same parse), the Playback2D tracker and the UI. Those need the app itself on a
scratch config.

### S1: narrower background parses

Consumer survey against the pinned 0.13.0-beta0001 API (read with ilspycmd). The queue parses once and
fans the `ParsedDemo` out to `[library, highlights, roundFacts, roundIndex, suggestedTags, grenades]`
(`App.axaml.cs:1248-1249`, `DemoEvaluationCoordinator.cs:165`), so a narrower shared plan has to cover the
union. `ParsedDemo` has no entity layer: every entity consumer replays `Frames` through its own tracker.

| Consumer | Reads | Fits |
|---|---|---|
| Library tier 2 (`DemoLibraryService.cs:1140-1169`) | roster, header, final-state entity replay (CCSTeam, CCSPlayerController) | forward reader + `EntityReplay` with `player_team` events |
| Highlights, bare (`RulesHighlightHarvester.cs:108-119`) | rules run, no snapshots | forward `DemoAnalysis.Run` / `Evaluate(IDemoFrameSource)`, which applies `PlanDecode` itself |
| Round Facts (`EngineRoundFactsRowSource.cs:71-86`) | round_facts rules run, `ClipRounds.Derive(events)` | same as highlights; a second pass unless merged into one build |
| Round Index (`RoundIndexEvaluator.cs:289`) | `PositionSampler.Walk(ParsedDemo)`, every frame | forward only with a reader-side sampler; `PositionSampler` has no `IDemoFrameSource` overload |
| Suggested Tags (`SuggestedTagsService.cs:678-697`) | positions walk, `ProjectileSampler.Walk`, all events | forward; `ProjectileSampler.Walk(IDemoFrameSource)` exists |
| Grenade walk (`GrenadeWalker.cs:124-138`) | projectiles, events, random frame access, user commands | full parse or a two-pass redesign; the only reader of `svc_UserCmds`; background sweep off by default (`Grenades.BackgroundIndex`) |
| Lineup clips (`LineupClipService.cs:975-1008`, `PackClipRenderer.cs:58`) | own parse; entity frames with FullPacket seek, no events, no user commands | `ParseFile(path, new ParseOptions { Plan = DecodePlan.EntityReplay })`; the reader cannot seek |

Parse only, mapped, Workstation, warm, 2 reps (within 1% of each other). fp / committed / LOH in MB:

| Plan | MM | Pro | Big | Parse s (MM / Pro / Big) |
|---|---|---|---|---|
| `Everything` (today) | 558 / 525 / 277 | 767 / 730 / 396 | 1,556 / 1,541 / 979 | 0.9 / 1.3 / 1.5 |
| All minus `UserCmds` | 336 / 298 / 57 | 453 / 414 / 78 | 742 / 701 / 135 | 0.6-0.8 / 1.0 / 1.2-1.3 |
| `EntityReplay` | 261 / 233 / 58 | 399 / 369 / 78 | 584 / 550 / 135 | 0.6 / 0.8-0.9 / 1.1-1.2 |
| `GameEventsOnly` | 138 / 101 / 57 | 190 / 150 / 78 | 280 / 239 / 135 | 0.3 / 0.5 / 0.7-0.9 |

User commands are most of the parse's LOH: 220-845 MB of it.

Parse plus bare rules evaluation (the highlights path), same demos:

| Path | MM fp / s | Pro fp / s | Big fp / s | Highlights |
|---|---|---|---|---|
| Retained, `Everything` | 720-728 MB / 3.0 | 1,029-1,035 MB / 3.5 | 1,835-1,845 MB / 4.5 | 292 / 330 / 345 |
| Retained, minus `UserCmds` | 502-510 MB / 2.8-3.0 | 714-720 MB / 3.4-3.5 | 1,031-1,037 MB / 4.2-4.6 | same |
| Forward reader (`DemoAnalysis.Run(path)`) | 204 MB / 4.2 | 199-205 MB / 3.9 | 206-223 MB / 4.3 | same |

### After the owner decisions (branch `feature/strat-book-perf-narrow`)

Built: a compaction queued after every background parse (a demo processing entry, or a lineup clip item),
ahead of every other item; the 30 s throttle and drain rule stay for non-parse jobs. The shared background
parse drops `UserCmds` when no evaluator on the entry reads them (only the grenade walk does; it is attached
only while `Grenades.BackgroundIndex` is on and its sidecar is stale). Lineup and pack clips parse with
`DecodePlan.EntityReplay`. The foreground open stays full.

Output checks, the three smallest demos in `demos/benchmarks` (003816248937665266002, 003816809596253634708,
003816798820180689112), RealDemo tests that skip without `DEMO_PATH`:

- `BackgroundPlanRealDemoTests`: Round Facts rows, Round Index sidecar and positions, Suggested Tags proposals,
  bare highlights and library tier-2 fields serialize identically from the full and the no-`UserCmds` parse.
- `ClipReplayPlanRealDemoTests`: three lineup GIFs per demo byte-identical, one pack clip per demo
  (86-120 frames) pixel-identical, same frame clock identity.

Same method as above, same 14-demo list, mapped, Workstation concurrent, two reps in reversed order. The
bench runs the policy (`--compact`, `--plan`), not the app's queue. MB unless marked.

Background run (parse plus bare rules evaluation):

| Config | Eval s | Peak fp | Committed during, max / median | LOH free during, max | After last job committed | gen2 | GC pause | Alloc |
|---|---|---|---|---|---|---|---|---|
| Before: `Everything`, compact at drain | 17.9 / 17.4 | 2,027 / 1,526 | 1,315-1,447 / 1,088-1,121 | 340-550 | 1,121-1,279 (153-164 after drain) | 27 / 29 | 6.2 / 6.2 s | 10.8 GB |
| After, sweep on: `Everything`, compact each | 17.4 / 18.0 | 1,001 / 999 | 225-231 / 95-96 | 0 | 48 | 71 / 71 | 6.4 / 6.5 s | 10.8 GB |
| After, sweep off or current: no `UserCmds`, compact each | 18.1 / 17.9 | 663 / 661 | 223-224 / 93-99 | 0 | 50-53 | 57 / 57 | 6.7 / 6.5 s | 7.6 GB |

All three produced the same 3,848 highlights. Peak footprint -51% (sweep on) and -63% (sweep off) against
this session's baseline; the baseline's own two reps spread 1,526-2,027. Parse seconds swung 39-106 s with
NFS caching and are left out.

Lineup clip parses over the same list (parse only, no render):

| Config | Peak fp | Committed during, max / median | After last job | GC pause | Alloc |
|---|---|---|---|---|---|
| Before: `Everything`, compact at end | 1,746 / 1,266 | 1,221-1,717 / 1,098-1,137 | 1,061-1,074 before the compaction | 3.3 / 3.3 s | 7.1 GB |
| After: `EntityReplay`, compact each | 392 / 391 | 165-168 / 17 | 16-22 | 2.5 / 2.6 s | 3.1 GB |

Not measured: the app itself under `dotnet-counters` during a queue run, and the cost of each compaction with
the app's live indexes resident (estimated 100-300 ms above).

### Recommendations

**GC (Q1, S7).** Keep Workstation concurrent. Do not ship Server + DATAS: it keeps 1.5 GB committed even
after an explicit compaction. Compact after each background parse job instead of only on drain (the
queue's existing `HeapCompaction` item, with the 30 s throttle dropped or shortened when the previous job
was a parse): peak footprint -37% in the bench, and each job starts from ~50-80 MB committed instead of
0.9-1.5 GB. If a blocking gen2 per job is
unwelcome, `System.GC.ConserveMemory=7` in the Desktop csproj is the config-only fallback (-28% peak,
-40% median committed, +4% eval). Keep the drain compaction either way. Owner call: which of the two.
Re-measure in the app afterwards (`dotnet-counters` on committed and LOH free during a queue run).

**Q9.** Keep the mapped parse. It takes exactly the file's size off committed heap and footprint
(276 / 382 / 748 MB) at no time cost, warm or cold over NFS.

**S1, ranked.** None of these needs a new `ParseOptions` or `DecodePlan` member, so none touches the
protected `DemoParser.cs`.

The owner's settings have `Grenades.BackgroundIndex: true` (and `Situations.BackgroundIndex: true`), so
Grenade walk is on the shared fan-out today and reads user commands. That sets the order.

1. **Lineup and pack clips on `EntityReplay`.** Parse footprint 558 to 261 MB (MM), 767 to 399 (Pro),
   1,556 to 584 (Big): -47% to -62%, and faster. Two call sites, no contract change, unconditional.
   Measured on the parse only; the renderer's tracker replay and GIF output need a byte or visual A/B
   before shipping. No owner call beyond approval.
2. **Drop user commands from the shared background parse when no consumer needs them:**
   `Plan = DecodePlan.Everything with { Categories = MessageCategories.All & ~MessageCategories.UserCmds }`
   when `Grenades.BackgroundIndex` is off or the demo's grenade sidecar is already current (the case for
   every re-processed demo once the walk has caught up). Parse+eval footprint -29% (MM), -31% (Pro),
   -44% (Big), LOH -80%, parse 10-20% faster, identical highlights. Owner call: the per-job condition
   (the queue has to know before parsing whether Grenade walk will run). Check that the other five
   consumers' sidecars come out byte-identical before shipping. The open-demo parse stays full.
3. **Library tier 2, Highlights (bare) and Round Facts on the forward reader.** Peak ~200-220 MB whatever
   the demo size, against 0.72-1.84 GB retained (-72% to -89%), identical highlights; cost +1.2 s on the
   276 MB demo, +0.4 s Pro, none on Big. Large: the queue's handler contract changes from `ParsedDemo` to
   a path or reader. Owner calls: drop the fan-out for these consumers; two rules passes or one merged
   build (changes fingerprint and exclusion semantics). Forced (snapshot) highlights keep the full parse.
4. **Round Index and Suggested Tags positions on the same forward pass.** Needs a reader-side position
   sampler: an app shim from public parts (`EntityTrackerFactory.CreateCurated`, `PawnLookup`,
   `PositionUtil`), or an upstream ask for `PositionSampler.Walk(IDemoFrameSource)` in the CS2DemoKit
   parser package (not `DemoParser.cs`). Owner call: shim or upstream. With 3 and 4 done no background job
   retains frames, so the ~200 MB of item 3 is the whole job (estimated).
5. **Grenade walk last.** Random frame access and user commands: a two-pass redesign, or keep the full
   parse when the owner enables the background sweep.

Commands: `AnalysisBench bg-run --list=<file> [--read=mmap|bytes|forward] [--eval]
[--compact=none|end|each] [--plan=everything|no-usercmds|replay|events|structure]`, GC from the
environment, peak from `/usr/bin/time -l`.
