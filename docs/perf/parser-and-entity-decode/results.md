# Parser & entity-decode — performance results (living)

Living results hub for the parser/entity-decode perf work. Update this file after every baseline
run and every candidate verification run — it's the scannable cross-run view for quick analysis.
The raw per-run data (structured `results.tsv` + per-demo `--profile` logs) is written by
`scripts/perf-sweep.sh` into the local `runs/` directory, which is untracked scratch; runs worth
keeping get summarized into the tables here.

Plan and verification protocol: `candidates.md` (sibling).

---

# Candidate testing results (2026-06-20)

Each candidate was implemented in isolation on its own sub-branch off the integration baseline (the
enrich string-table gate), verified (full 12-demo sweep + correctness gates), then the accepted
candidates were stacked and re-measured back-to-back in one quiet window (the `int-baseline` /
`+trace gate` / `+trace gate +buffer reuse` runs).

## Summary

Two real wins (the decode-trace gate and the decompress-buffer reuse), one clean drop (the Huffman
table), three deferrals (bulk ReadBytes, copy elision, dispatch collapse). Every accepted change is
byte-identical on all 12 demos across all 3 sources (Valve MM / ESL / BLAST). The combined accepted
stack, back-to-back: Σ Total 55,472 → 51,666 ms = −6.9 % raw (~−5–6 % net of residual drift) —
dominated by the trace gate, with the buffer reuse adding ~−1.7 % of total plus a GC-health
improvement.

## Per-candidate verdicts
| Candidate | Touches | Real effect (drift-controlled) | Correctness | Verdict |
|---|---|---|---|---|
| decode-trace gate | `Tracing.cs` (new), `EntityTracker.cs`, `DecodeTraceGateTests.cs` — all non-protected | `eval/parse` ratio −4.5 % (back-to-back) / −6.4 % (stale base); deterministic alloc −6.5 % → ~−5 % real decode | byte-identical (StatParity div=0; ParallelDigest identical; Parser 98/0 incl. 3 new gate tests) | keep |
| decompress-buffer reuse | `DemoParser.cs` (protected) | `pass2` −7.5 to −8 % → ~−1.7 % of total + GC health (per-frame decompress alloc eliminated, 90 k–250 k arrays/demo) | byte-identical (ParallelDigest identical; StatParity div=0) | keep |
| table-driven field-path decode | `FieldPathEncoding.cs`, `BitBuffer.cs` (protected), `FieldPathTests.cs` | `eval/parse` ratio −0.0 % (no gain; matches the 0.05 %-CPU dotnet-trace prior) | byte-identical (exhaustive table-vs-tree over all 1<<17 codes) | drop |
| misaligned bulk ReadBytes | (would touch `BitBuffer.cs`) | inner-msg copy 0.22 % of CPU (cold) — required gate failed | n/a | defer |
| copy elision | (would touch `DemoParser.cs`) | same trace evidence (cold); larger/riskier than the bulk read | n/a | defer |
| decoder-dispatch collapse | (would touch `FieldDecoder`/`FieldDecoderFactory`) | not implemented (measurement scaffolding only); field-value decode is 22–26 % of decode but *dispatch* is a sub-fraction → ceiling ≤ ~2.5 % of total | scaffolding correctness-neutral | defer |

## Integration — back-to-back stack (drift-minimized), Σ Total ms
| stack | Σ Total | Σ eval | Σ parse | Σ pass2 | Δ Total vs baseline |
|---|--:|--:|--:|--:|--:|
| `int-baseline` | 55,472 | 40,083 | 13,992 | 11,987 | — |
| `+trace gate` | 51,894 | 37,228 | 13,614 | 12,368 | **−6.5 %** |
| `+trace gate +buffer reuse` | 51,666 | 37,396 | 12,874 | 11,438 | **−6.9 %** |

- Trace gate isolated (`+trace gate` vs baseline): eval −7.1 %, but the *untouched* parse fell
  −2.7 % (residual drift even back-to-back) → the drift-cancelled `eval/parse` ratio is −4.5 %;
  deterministic alloc −6.5 %. The trace gate is the dominant lever.
- Buffer-reuse marginal (`+trace gate +buffer reuse` vs `+trace gate`): `pass2` −7.5 % (matches its
  isolated −8 %), but the *Total* marginal reads only −0.4 % because the untouched eval/precompute
  phases swung +0.5 %/+2.0 % of run-to-run noise that demo-Total can't separate. The reuse's real
  contribution is the −7.5 % Pass-2 (~−1.7 % of total) + the GC-health win.
- Honesty caveat: even back-to-back, ~2–3 % residual drift (the untouched-parse move) and ±1–2 %
  per-run parallel-phase noise remain. So −6.9 % is the raw back-to-back combined; the real combined
  is ~−5–6 %, almost all of it the trace gate. Per-demo Totals are noisy (−18 %…+4 %); trust the Σ
  and the within-run ratios, not any single demo.
- A reuse-alone stack was deliberately not measured (only baseline / +gate / +gate+reuse). The two
  changes touch disjoint phases (entity decode vs Pass-2 decompress), and the reuse's
  marginal-in-stack `pass2` (−7.5 %) already matches its isolated sweep (−8 %), so a reuse-alone
  back-to-back run would be redundant. The two survivors do not interact.
- The stack is essentially the trace gate. The buffer reuse's *total-level* effect (−0.4 % marginal)
  is inside this run's noise; its real justification is the −7.5 % Pass-2 + the deterministic GC
  win, not a visible Total move. Accepting it therefore trades a protected-file change
  (`DemoParser.cs`) for a small, noise-adjacent total — worth making that explicit so the sign-off
  is an informed call, not an assumed one.

## Key takeaways
1. The dominant decode cost is the actual field reads, not the scaffolding around them. The trace
   gate (stop building a never-read per-op trace) is real and cheap; the Huffman table and the
   dispatch indirection are tiny slivers — the dotnet-trace prior (field-path `ReadOp` 0.05 % of
   CPU) was correct, and the candidates that fought it lost.
2. Allocation removal is the highest-leverage, lowest-risk parser perf pattern here. Both real wins
   remove allocation — per-op trace objects and per-frame decompress arrays. The deterministic alloc
   metric is also the only drift-immune signal, which is why it settled the close calls.
3. Measurement hygiene was decisive. A stale baseline drifts ~5 % between recording and re-measure;
   the Huffman table's apparent −6 % was *entirely* drift (its untouched parse fell the same −6 %).
   The `eval/parse` ratio + deterministic alloc are the tools that separate signal from drift; raw
   timing deltas are only trustworthy back-to-back, and even then carry ±1–2 % parallel-phase noise.
   (Captured as a reusable doctrine in *Key insights* below.)

## Recommendations
- Keep the trace gate and the buffer reuse. Both real, byte-identical, low-risk. The buffer reuse
  touches the protected `DemoParser.cs`, so it gets the code-quality sign-off before merging to
  main; the trace gate touches no protected file. The Pass-2 reuse is reviewed and sound (the
  buffer never escapes onto a `DemoFrame`; verified against the code).
- Drop the Huffman table. A clean drop on the number (zero real gain), not a quality problem — it
  would add a protected-file change (`BitBuffer.cs` `PeekBits`) + a 256 KB table to maintain for
  nothing.
- Defer bulk ReadBytes, copy elision, and the dispatch collapse. The inner-message copy is cold
  (0.22 % CPU) and the only viable form is a high-risk `BitBuffer.cs` rewrite — not worth it. The
  dispatch collapse was never actually built; its bounded ceiling (≤ ~2.5 % of total) sits in the
  90 %-of-CPU core decode path, so it warrants a focused, deliberate effort with a profile in hand.
  That experiment left behind a reusable `field-value (Σ workers)` profiler and a
  `DEMOVIEWER_D3_DOP` DOP knob.

## Next steps
1. Review the two survivors; the buffer reuse needs the protected-file sign-off for
   `DemoParser.cs`, the trace gate doesn't.
2. Merge the trace gate, then the buffer reuse, to `perf/parser-and-entity-decode`, re-running the
   gates after each.
3. (Optional, low priority) If field-value decode ever warrants attention, start the dispatch work
   properly: read the current `FieldDecoder`/`FieldDecoderFactory` dispatch to confirm a real
   hot-path indirection exists *before* implementing, and use the left-behind profiler to size it.
4. Independent of these candidates: Parse (Pass-1/2/3 + decompress) is now a small slice; entity
   decode (eval + precompute) remains ~70 % of load — the next big lever is still inside entity
   decode, but needs a fresh, focused profile (the dispatch/field-value split that experiment began
   to measure is the natural starting point).

---

## Run index
| Run | Date | What changed | Σ Total 12-demo (ms) | Gates |
|---|---|---|--:|---|
| `baseline` | 2026-06-19 | — (baseline sweep on the integration branch, at the then-current main tip) | 75,196 | green |
| `enrich-gate` | 2026-06-20 | gate string-table decode to `userinfo` in enrich (the outcome of the enrich investigation) | 56,949 (**−24%**) | green¹ |
| `trace-gate` | 2026-06-20 | runtime opt-in decode-trace gate (`DEMOVIEWER_TRACE_DECODE`, default off) — stop building ~7M `DecodeTraceEntry`/load in the hot decode loop | 52,125 (raw −8.5%; **~−5% real**³) | green² |
| `huffman-table` | 2026-06-20 | table-driven field-path `ReadOp` (256 KB lookup) + `BitBuffer.PeekBits` (protected) | 53,552 raw → ~0 % real (**drop**) | byte-identical⁴ |
| `pass2-levers` | 2026-06-20 | reuse the Snappy decompress buffer in Pass-2 `DemoParser.cs` (protected); bulk ReadBytes / copy elision deferred — copy 0.22 % CPU, cold | 54,493 raw → −8 % Pass-2 real (**keep**) | byte-identical⁴ |
| `decoder-dispatch` | 2026-06-20 | measurement tooling only — the dispatch collapse was not implemented | — (no sweep) → **defer** | n/a (neutral) |
| `int-baseline` → `+trace gate` → `+trace gate +buffer reuse` | 2026-06-20 | back-to-back stack in one quiet window | 55,472 → 51,894 → **51,666 (−6.9% combined)** | byte-identical⁴ |

¹ Golden gate closed across all 3 sources: StatParity byte-identical (5 Valve); parser roster diff byte-identical
baseline-vs-fix on inferno/ESL (15/15) + vitality-m3-nuke/BLAST (12/12). Parser 95/0, Analysis 396/0 — the one
"failure" is the pre-existing `MolotovThrowScanner` `IsBetween(10,80)` bound vs 87 molotovs on the pro demo
(entity-derived, identical to baseline).

² Measured vs the `enrich-gate` run (the integration baseline, not the original `baseline`). StatParity divergences=0 on
every stat (5 Valve); `ParallelDigest` element-wise identical (incl. 706 MB demo); Parser 98/0 (incl. 3 new
`DecodeTraceGateTests`). Analysis 391✓/1✗/76skip — the 1 failure is a worktree artifact (`Cs2OpendocsSubmodule_PinnedToExpectedSha`:
cs2-opendocs is symlinked in a worktree, so its SHA-pin can't git-resolve HEAD; passes in the main checkout, resolves on merge).

⁴ Byte-identical = `ParallelDigest` element-wise identical (decode equivalence, incl. 706 MB demo) + StatParity
divergences=0 (5 Valve). The lone Analysis failure in every *worktree* run is the submodule-symlink artifact (see ²),
never the candidate. The combined gate+reuse stack also passed Parser.Tests 98/0.

³ Drift correction (and back-to-back refinement). The `enrich-gate` baseline was recorded earlier and the machine
had since drifted ~5 % faster (the *untouched* parse phase dropped −4.7 % in the isolated trace-gate run), so the raw
−8.5 % Total overstates the gate. Against that stale baseline the `eval/parse` ratio read −6.4 %; the back-to-back
integration run (both states measured in one quiet window — the more controlled measurement) refined it to −4.5 %
(ratio 2.865 → 2.735). The deterministic, drift-immune eval-alloc is −6.5 %. Honest trace-gate figure: ~−5 % real
decode — time-ratio −4.5 % (back-to-back), allocation −6.5 % (the gap is coherent: under Server GC, removing
allocation doesn't cut wall-clock proportionally). See the *Candidate testing results* section above for the
back-to-back numbers.

> Σ Total is the sum of the 12 per-demo median-warm totals — a coarse headline; `furia m2-inferno` alone is 21.0 s of it.
> Compare per demo (table below), not just the sum.

## Master comparison — Total per demo (median warm, ms)
`enrich-gate` Δ is vs `baseline`; `trace-gate` Δ is vs `enrich-gate` (its measurement base — the gate stacks on the enrich fix).

| Demo | Plat | `baseline` | `enrich-gate` (Δ vs base) | `trace-gate` (Δ vs enrich-gate) |
|---|---|--:|--:|--:|
| bench 0544286934 | Valve | 3,505 | 3,563 (+2%) | 3,382 (−5%) |
| bench 0003771537 | Valve | 4,231 | 4,137 (−2%) | 3,879 (−6%) |
| bench 1066991563 | Valve | 4,222 | 4,180 (−1%) | 4,057 (−3%) |
| bench 0126308730 | Valve | 4,016 | 4,150 (+3%) | 3,956 (−5%) |
| bench 1168926414 | Valve | 4,007 | 3,852 (−4%) | 3,454 (−10%) |
| furia m1-mirage | ESL | 8,770 | 5,820 (−34%) | 5,106 (−12%) |
| **furia m2-inferno** | ESL | **21,007** | **6,439 (−69%)** | **5,583 (−13%)** |
| furia m3-nuke | ESL | 5,432 | 4,514 (−17%) | 4,280 (−5%) |
| furia m4-overpass | ESL | 5,700 | 5,861 (+3%) | 5,271 (−10%) |
| vitality m1-mirage | BLAST | 4,709 | 4,389 (−7%) | 4,165 (−5%) |
| vitality m2-dust2 | BLAST | 4,831 | 5,017 (+4%) | 4,605 (−8%) |
| vitality m3-nuke | BLAST | 4,766 | 5,027 (+5%) | 4,387 (−13%) |
| **Σ Total** | | **75,196** | **56,949 (−24%)** | **52,125 (−8.5%)** |

(Per-phase breakdown — Parse/Pass1-3, Eval, precompute, alloc — is in each run's local sweep output.)
Trace gate, drift-corrected: raw shows every demo down (−3…−13%), but ~5 % of that is machine drift vs the stale
baseline (untouched parse fell −4.7 %). The real decode gain is ~−5 % — `eval/parse` ratio −4.5 % (back-to-back
integration, the controlled measurement; −6.4 % vs the stale base) corroborated by the deterministic eval-alloc −6.5 %
(drift-immune). See footnote ³ and the *Candidate testing results* section.
On the +2–5% demos: noise, not regression — the enrich gate edits only Pass-3, and Pass-3 fell on all 12; the
Total wobble is bidirectional jitter in the parallel Pass-2/Eval phases (untouched) and largest where the Pass-3 win is
smallest.

## Key insights (rolling — newest first)
- **[methodology — drift control]** The recorded baseline ages: between recording it and measuring a candidate, the
  machine drifts (≈5 % was observed today). Raw timing deltas vs a stale baseline therefore conflate the candidate with
  drift. Two drift-proof tools settle isolated keep/drop: (1) the `eval/parse` ratio — for a decode candidate that
  leaves parse untouched, uniform drift cancels because both are measured in the same warm runs; (2) allocation, which
  is deterministic and immune to timing drift (decisive when the candidate's *mechanism* reduces allocation). Reserve raw
  timing deltas for the integration phase, where a fresh baseline is measured back-to-back with the survivor stacks in
  one quiet lock window — the only place a clean absolute total is available.
- **[trace-gate]** The decode-trace was armed for every packet — ~7M `DecodeTraceEntry` builds + `List.Add`s per
  load — though it's only ever *read* on a decode error (≈never on a healthy demo). Gating it behind a runtime opt-in flag
  (`DEMOVIEWER_TRACE_DECODE`, default off) removes that per-op CPU from the hot decode loop. Real gain (drift-corrected):
  ~−5 % — `eval/parse` ratio −4.5 % (back-to-back integration) + deterministic alloc −6.5 % — byte-identical output.
  (Raw sweep read −8.5 % Total / −10.7 % eval vs the stale baseline, ~5 % of which is drift.) Targets the entity-decode
  path the dotnet-trace flagged as ~90 % of CPU. Accepted — the dominant accepted lever.
- **[enrich-gate]** Root cause of the inferno enrich blowup was `StringTableProcessor.ProcessCreate → DecodeEntries`
  decoding the large non-`userinfo` `instancebaseline` table (measured ~100 % of Pass-3 alloc) that enrich never reads.
  Gating decode to `userinfo` cut Pass-3 to ≤161 ms on every demo (inferno 15.9 s → 0.14 s), corpus −24 %,
  inferno total 21 s → 6.4 s, golden byte-identical. ⇒ Parallelizing enrich is no longer worth it — the investigation
  solved the problem the parallel rewrite was meant to.
- **[baseline]** Pass-3 enrich is the dominant, content-variable parser cost. `furia m2-inferno` (ESL): 15.9 s of
  enrich = 76 % of its 21 s load — non-linear, *not* size-driven (`m1-mirage` is bigger w/ more events but enriches in
  3.1 s). Likely a bug/quadratic; highest-value parser lever to investigate first.
- **[baseline]** Entity decode (`Eval` 2.8–3.9 s; `precompute` 1.8–3.1 s) is the stable corpus-wide floor — what
  the trace-gate and Huffman-table candidates target.
- **[baseline]** Pass-2 (decode+decompress) is modest (0.44–1.7 s); Pass-1 trivial (14–34 ms).
- **[baseline]** Sources span Valve MM / ESL / BLAST (no FaceIt); the format-detector labels all `GotvMatchmaking`
  (use the Server string for platform). Pass-3 cost is *content*-specific, not platform-specific.

## Workstation-GC allocation reduction (2026-07-23, `feature/v0.5.1`)

Context: the Desktop head moved Server GC → Workstation GC for footprint reasons, at a throughput
cost. These are the levers that buy that cost back by allocating less, which helps Workstation GC
disproportionately (one collector thread servicing many parallel decode workers).

Method: `dotnet-trace` GC allocation-tick sampling with call stacks, aggregated by type *and by stack*
(`dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1:5`). Correctness gate for each
step is AnalysisBench's own stat output, diffed with timing lines stripped — byte-identical, not
"still 81.6%". Verified across all 5 benchmark demos (445 lines, zero diff) for the two steps
combined, in addition to the per-step 3-demo check.

| Step | Change | Total alloc | Peak RSS |
|---|---|---|---|
| — | baseline (post lazy-descriptors) | 2889.2 MiB | 2417.2 MB |
| 1 | `EntityState.TryGetValue` — stop materialising `Fields` in `ResolveThrowerSlot` | 2705.0 MiB | — |
| 2 | shared boxes for small ints on the fallback decode path | **2376.6 MiB** | **2110.3 MB** |

Net: **−512.6 MiB allocated (−17.7%)**, **−307 MB peak RSS (−12.7%, n=3, spread <6 MB)**,
**−6.1% wall clock** (4.75 → 4.46 s). Parser 99/99, Analysis 967/967.

### Key insights
- **Boxing was the story, and it was in the decode, not the digest.** A code-read predicted the
  digest extractor's `object?[]`/provider boxing; stack-attributed tracing put 72% of the 441 MiB of
  boxed `Int32` in `EntityTracker.ReadAndTrace` — fields with no typed lane, stored through
  `SetFallback(string, object?)`. Do not skip stack attribution on this codebase.
- **Measure the distribution before designing the cache.** 14,303,518 fallback int writes, 99.9% in
  `[0, 256)` → a 1152-entry shared table covers essentially all of them. The same probe killed the
  competing design (reuse the box already at that path): only 1% of writes repeat the prior value.
- **`.Fields` is still a live footgun.** It rebuilds a merged dict of every field on the entity.
  Earlier profiling work already fixed one such site; `ResolveThrowerSlot` was another, left behind
  *with a comment explaining why the indexer could not replace it* — the indexer collapses absent and
  present-null. The fix was to add the missing API (`TryGetValue`), not to force the indexer.
- **The digest array is retained, not churned.** `ParallelDigestProducer.Produce` returns
  `EntityFrameDigest[frames.Count]` — all 137,103 digests live simultaneously. So the remaining
  digest-side allocation (`ValueTuple<int,object[]>[]` 51.6 MiB + `object[]` 57.5 MiB + digest/list
  objects ~10 MiB ≈ 118 MiB) is *peak memory*, and cannot be pooled or reused per frame without
  changing that architecture. Only the wrapper/closure churn (`CSPlayerPawn` 31.8 MiB +
  `Action<int,EntityState>` 7.3 MiB ≈ 39 MiB) is cheaply addressable.

### Remaining map (post-step-2, 2376.6 MiB total)
| Bucket | MiB | Owner | Notes |
|---|---|---|---|
| `System.Byte[]` | 1046.0 | parser pass 2 | decompression buffers; protected-file territory |
| `CMsgServerUserCmd` family | ~245 | parser | subtick / usercmd protobufs — the subtick-deferral target |
| `NodeSnapshot[]` + `[][]` | 181.1 | analysis | evaluator snapshots, not entity tracking |
| digest retained | ~118 | entity tracking | architectural (bound the digest buffer) |
| boxed `Int32` residual | 73.2 | entity tracking | out-of-range + non-fallback sites |
| digest churn | ~39 | entity tracking | wrapper + per-frame closure; cheap, low value |

## v0.5.1 end-to-end AnalysisBench comparison (2026-07-24)

Full before/after over the whole v0.5.1 code sweep — the merge-base with main (before any v0.5.1
work) vs the sweep's HEAD. 279 MB benchmark demo, 3 runs each, medians. AnalysisBench runs
Server GC at both endpoints (unchanged — the Server→Workstation switch was Desktop-app-only), so
this isolates the code improvements: lazy field descriptors, `EntityState.TryGetValue`, the
small-int box cache, and the `svc_UserCmds` deferral.

| metric | before | after | delta |
|---|---|---|---|
| Parse ms | 1030 | 973 | −5.5% |
| **Eval ms** (entity decode + scanner) | 3132 | 2439 | **−22.1%** |
| **Total ms** (parse+build+eval) | 4322 | 3570 | **−17.4%** |
| **Eval-phase allocation** | 601.4 MiB | 313.0 MiB | **−48.0%** |
| **Peak process RSS** (`time -l`) | 4222 MB | 1775 MB | **−58.0%** |
| GC collections (Gen0/1/2) | 5 / 3 / 1 | 2 / 1 / 1 | fewer |

- Eval allocation nearly halved (−288 MiB) — the direct target of TryGetValue + the box cache +
  lazy descriptors. Parse-phase allocation also fell (subtick deferral + descriptors) though parse
  *wall-clock* is flat (the deferred-message per-message `byte[]` copy offsets the object-build it
  replaces).
- Peak RSS fell 58% under Server GC — larger than the allocation delta alone because Server GC's
  committed heap responds non-linearly to allocation pressure (greedy per-core segments, delayed
  collection). This is exactly the property that made the Desktop app's separate Server→Workstation
  switch worthwhile; it also means the two wins compound in the shipped app.
- Retained-after-load (measured separately, held ParsedDemo): the `svc_UserCmds` deferral alone cut
  919 → 705 MiB on this demo; the full sweep's retained figure is lower still.
- Output byte-identical across all 5 benchmark demos (correctness gate below).

## Digest-buffer bounding — attempted, reverted (2026-07-24)

Goal: cut the ~118 MiB transient peak from `ParallelDigestProducer` materialising all 137k
`EntityFrameDigest`s before the scanner consumes them.

Approach tried: stream the parallel producer into the sequential consumer — `ProduceInto` publishes
each slot with `Volatile.Write`, `EntityChangeScanner.WaitForDigest` acquire-loads and spin-waits.
Deadlock-free by construction (producers never block on the consumer; worst case = today). Output
byte-identical across all 5 demos.

Why it was reverted — measured, decisive:
- Eval regressed ~12% (2.50 s → 2.76–2.87 s, `0126308730`, n=8; reducing producer DOP to cores−1
  did not recover it). Root cause: the sequential consumer now runs *concurrently* with the
  core-saturating parallel producers, so the spin-waiting consumer oversubscribes the cores. The
  overlap it buys is smaller than the contention it costs — the produce and consume phases were never
  on a spare core to begin with.
- Peak RSS unchanged (~1770 vs ~1775 MB). Under Server GC (which AnalysisBench uses) the committed
  heap high-water mark swamps a 118 MiB transient; the reduction is within noise and Server GC never
  decommits it anyway.

Verdict: the digest peak is not cheaply reclaimable. Reducing it requires interleaving, which
oversubscribes; the only real lever left would be shrinking each digest (de-boxing the provider
`object?[]` — a wide `IPerPlayerEntityValueProvider` contract change), out of proportion to a ~6.6%
transient. Not pursued. Left as-is.

## Correctness gate status (must stay green every run)
- StatParity vs Leetify (5 Valve benchmarks — documented residual, perf changes must reproduce it
  exactly): `0544286934` 86.3 % · `0003771537` 86.3 % · `1066991563` 81.1 % · `0126308730` 81.6 % · `1168926414` 87.4 %.
- ParallelDigestEquivalence — green on all 3 sources (incl. ESL furia-m1-mirage 706 MB). FullPacketCheckpointSpike — green.
- Golden byte-identical — each demo's bench output (captured per run) is the reference; a perf change must reproduce it.

## How to record a run
1. Perf: `scripts/perf-sweep.sh <run-id>` → writes `runs/<run-id>/results.tsv` + per-demo
   `*.profile.txt`. That directory is local, untracked scratch.
2. Correctness: run the gates (candidates doc → *Verification protocol*) and keep the output with
   the run's sweep files.
3. Summarize here: add a run-index row, a master-comparison column (with Δ vs baseline), and any
   key-insight bullets. This file is the durable record; the raw sweep output is not committed.
