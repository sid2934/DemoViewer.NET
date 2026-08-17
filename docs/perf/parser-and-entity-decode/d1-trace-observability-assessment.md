# Decode-trace observability assessment

Branch `perf/parser-and-entity-decode`, 2026-06-20. Written before the decode-trace gate was
implemented, to settle its approach: can the trace stay always-on cheaply, and is the sketched
gate-off-plus-re-decode-on-error design sound? No code changed for this assessment, and it does not
re-derive cost from benchmarks (`dotnet-trace` was not installed on the machine; a prior run had
already characterized the cost) — every claim is grounded in file:line. Companion to the trace-gate
entry in `candidates.md`; this doc supplies what that entry deferred: ranked always-on options, an
end-user diagnostic-value analysis, and an explicit recommendation.

---

## 1. What the trace is

The trace is the entity-decode bit-misalignment diagnostic in `EntityTracker.cs`. When a
`CSVCMsg_PacketEntities` packet fails to decode (a wire-shape / schema mismatch desynchronizes the
bit reader), the trace is the chronological record of every field-path op and field read in that
packet — the artifact that root-caused the classid, baseline, and AnimGraph2 decode bugs.

Mechanism (confirmed by read):

| Element | Location | Behaviour |
|---|---|---|
| Buffer | `EntityTracker.cs:135` | `List<DecodeTraceEntry> _trace = new(4096)` — pre-sized, per tracker instance. |
| Entry | `EntityTracker.cs:2487-2581` | `readonly struct DecodeTraceEntry` (~13 fields; stack-constructed). Two ctors: a string-path one and a `FieldPath`-by-value one with **lazy** `Path` (`:2541` — `ToString()` deferred to dump-time). |
| Arm | `EntityTracker.cs:1944-1945` | `_trace.Clear(); _traceContextActive = true;` at the **start of every packet**. |
| Disarm | `EntityTracker.cs:1997` | `_traceContextActive = false` in `finally`. |
| Append | `EntityTracker.cs:564-570` | `AddTrace(in entry)` — adds only if `_traceContextActive`. |
| Dump | `EntityTracker.cs:1003-1069` | `DumpTrace()` — Console output + outlier ranking. |
| Dump trigger | `EntityTracker.cs:1963-1970` | first error only (`!_errorLogged` → set `_errorLogged`, `DumpTrace()`). |
| UI event | `EntityTracker.cs:1975-1993` | `DecodeErrorRaised?.Invoke(new DecodeError(...))` — App-only; reads `_trace[^1].Path` (`:1980`). |
| Construction sites | `:2055, 2071, 2125, 2137, 2144` (Prelude), `:2314` (BeginEntity), `:2349, 2364, 2377` (PathOp), `:2275` (FieldRead) | ~7 M entries/load on a real demo. |

---

## 2. Cost characterization — confirming the four prior findings

All four established findings are confirmed against the code, with citations:

**Finding #1 — `_trace.Clear()` runs every packet (`:1944`), so even the always-on design retains only the single most
recent packet.** The trace is not history. `DumpTrace()` is explicit about this — `"({_trace.Count} entries, last
packet)"` (`:1011`) and `"last packet"`. The dump fires on the *first* error (`!_errorLogged`, `:1963`), and at that
moment `_trace` holds exactly the failing packet (the clear ran at its top, `:1944`, before the throw). Therefore the
claim "always-on captures more history than a re-decode" is false: both designs yield the same single-packet trace.
The only thing always-on buys over a re-decode is *not having to reproduce* that one packet's decode.

**Finding #2 — cost is per-op CPU, not allocation.** The `List` is `new(4096)` (`:135`) and `Clear()`ed + reused each
packet (`:1944`) — `Clear()` does not release backing storage, so after warmup the backing array is stable and nothing
is produced for the GC to reclaim. `DecodeTraceEntry` is a `readonly struct` (`:2487`), so each entry lives inline in
the `List`'s array — no per-entry heap object. The one historical allocator (per-op `FieldPath.ToString()`) was already
removed by the lazy-`Path` design (`:2530-2541`). What remains per op is: (a) the `_traceContextActive` branch in
`AddTrace` (`:566`); (b) unconditional `DecodeTraceEntry` struct construction at the call sites whose argument is
built *before* the `AddTrace` gate (e.g. `:2314` BeginEntity, `:2349/2377` PathOp — the args are evaluated to pass them
in); (c) the `List.Add` copy of the struct. Net: a ring buffer / fixed-size struct ring / any alloc-saving redesign
buys nothing — there is no allocation to save. The only available win is *not doing the per-op work* on the healthy
path.

**Finding #3 — re-decode-on-error has a genuine fidelity risk under parallel decode.** Confirmed and made
precise below (§4). Decode now runs in parallel: each worker owns its own `EntityStateLayer` (hence its own
`EntityTracker`) — `ParallelDigestProducer.cs:114` — and chunks `k>0` are primed from a DEM_FullPacket checkpoint
via `layer.PrimeFromCheckpoint(chunk.CheckpointFrameIndex, schemaPrefixEnd)` (`:121-124`), then replay
`chunk.Start..chunk.End` (`:126-130`). The pre-packet state for a failing packet in chunk *k* is therefore a function of
the checkpoint prime + intra-chunk replay — state the tracker does not own and cannot reconstruct by itself.

**Finding #4 — `DumpTrace()` fires on any tracker at first error, gated only by `!_errorLogged` (`:1963-1970`);
`DecodeErrorRaised` is App-only (`:317`, `:1975`).** Confirmed. Under parallelism this is now a defect in its own right
(§4): `_errorLogged` is per tracker, so "first error" is per-worker, and concurrent `DumpTrace()` Console writes from
multiple workers interleave into unreadable output.

**Bottom line on cost:** the overhead is ~7 M × (one predicted branch + one ~64-byte struct construction + one `List.Add`
struct-copy) per load, entirely CPU, living inside the parallel-decode precompute. The candidates doc estimates
~0.1–0.3 s removable (design-derived; the precise `dotnet-trace` `AddTrace`/`..ctor` fraction was not captured —
`dotnet-trace` not installed). Label: design-derived, not measured.

---

## 3. Ranked "always-on cheap" options vs the re-decode sketch

The question was whether *any* always-on design can keep tracing on for all packets cheaply, beating the sketched
gate-off-plus-re-decode. Ranked by merit, with the honest verdict: given findings #1 and #2, no pure always-on design
wins — the only real lever is to stop doing per-op work on the healthy path, which is a gate, not a layout change.

| Rank | Option | Feasibility | Overhead (healthy path) | Observability / correctness tradeoff | Verdict |
|---|---|---|---|---|---|
| 1 | **Runtime opt-in gate** (the `Profiling.Enabled` doctrine applied to the trace) | High — pattern already in this file | Off: one predicted branch per op (or zero — see the compile-time gate). On: today's exact behaviour. | Best. When on, the trace is *faithful by construction* — no re-decode, no re-prime. Loses only *automatic* first-error capture in a default run (mitigated by a breadcrumb, §5). | **Winner (recommended)** |
| 2 | **Compile-time gate** (`#if`/`[Conditional]`) on the trace | High | Zero IL on the default path. | Same fidelity as the opt-in gate when compiled in, but needs a rebuild to trace a shipped binary — worse for *end-user* diagnosis (§4). Could layer *under* the runtime gate for the innermost sites. | Strong, but the runtime gate is more end-user-friendly. |
| 3 | **Ring buffer** (fixed `DecodeTraceEntry[N]`, overwrite) | High | Same as today — still builds + stores every entry. | Buys nothing: finding #1 (clear-per-packet ⇒ no extra reach already) + finding #2 (no alloc to save). Adds index bookkeeping. | Reject — solves a non-problem. |
| 4 | **Sampling** (trace 1-in-N packets/ops) | Medium | Lower, proportional to rate. | Defeats the purpose: the perpetrator op is a single specific read; sampling will miss it. Outlier ranking (`:1041-1069`) needs the *full* failing packet. | Reject — loses the signal. |
| 5 | **EventSource / EventPipe per op** | High to wire | Off: one predicted branch (≈ the opt-in gate). On: far worse — 7 M `WriteEvent` calls/load, arg boxing/serialization >> struct+List. | Live capture via `dotnet-trace`, but wrong granularity: this is a *post-mortem dump of one packet*, not a hot event stream. Cross-worker ordering still a problem. | Reject for the trace (good for *coarse* seams, not this). |
| 6 | **Tiered: cheap always-on breadcrumb + opt-in full trace** | High | Off (full trace): opt-in-gate cost. Breadcrumb: a few fields on the error path only (≈free). | Default run still emits "decode error at packet N, class X, last path P" with zero healthy-path cost (the breadcrumb reads existing state at the catch, not per-op). Full bit-trace on deliberate re-run. | **Recommended companion to the opt-in gate** (§5). |
| — | **Gate-off + re-decode-on-error** (the original sketch) | Medium — defective as sketched (§4) | Off (healthy): ~the opt-in gate. | Re-decode reproduces the trace only if pre-packet state is reproduced — which under parallel decode it is not, without producer-level re-prime (a two-file contract change). | Reject the sketch; the opt-in gate supersedes it. |

Conclusion on "always-on cheap": there is no free lunch in *layout*. The ring buffer and sampling both fail on
their own terms. The honest answer is that an always-on cheap design is not realistic for this
trace — the cost is intrinsic per-op work, not a layout artifact. The correct move is the established repo doctrine,
make it opt-in, which is cheaper-or-equal to the re-decode design on the healthy path *and* avoids its fidelity
gamble.

---

## 4. The defect in the re-decode sketch, made precise (the high-value finding)

The original sketch in the candidates doc proposed: on catch, `_trace.Clear(); _traceContextActive = true;
RedecodePacketForTrace(msg);`. This does not reproduce the failing trace under the current architecture, for two
independent reasons:

1. **State is already mutated.** The failed first decode of the packet partially mutated `CurrentEntities` /
   `EntityState` before it threw (each entity is applied in sequence inside `ProcessPacketEntitiesCore`,
   `:2008-2153`). Re-decoding the *same* `msg` against the *now-mutated* tracker starts from a different pre-state, so the
   bit stream is interpreted against different entities — the re-decode can diverge from, or even silently succeed
   where, the original failed. The sketch acknowledged this ("the re-decode must run on a fresh/snapshotted
   tracker") but did not do it.

2. **The tracker cannot re-prime itself.** Under parallel decode the failing packet's correct pre-state is
   `PrimeFromCheckpoint(chunk.CheckpointFrameIndex)` (`ParallelDigestProducer.cs:121-124`) + replay of
   `chunk.Start..N-1` (`:126-130`). The `EntityTracker` does not own `chunk.CheckpointFrameIndex`, the frame list, or
   the schema-prefix boundary — those live in `ParallelDigestProducer`. So a faithful re-decode requires one of:
   - a per-packet pre-state snapshot taken on the healthy path (deep-copy of all live `EntityState`s before each
     packet) — which is *more* expensive than the trace it replaces, defeating the redesign's entire purpose; or
   - producer-level re-prime orchestration: on a worker error, spin up a fresh layer →
     `PrimeFromCheckpoint(chunk.CheckpointFrameIndex)` → replay `chunk.Start..N-1` → trace-decode packet N. That is a
     contract change spanning `EntityTracker.cs` + `ParallelDigestProducer.cs`, far heavier than the one-method sketch
     implies, and it must thread the trace flag down through `StateGraphEvaluator.PrecomputeParallelDigests`
     (`:157`, `:320`).

3. **The sketch's lone advantage — automatic first-error capture — is weak under parallelism.** `_errorLogged` is per
   tracker (`:144`), so "first error" is *per worker*, not global; and concurrent `DumpTrace()` Console writes from
   multiple workers interleave (`:1018-1039` is plain `Console.WriteLine` with no synchronization). So the current
   always-on design *also* mis-serves the multi-worker case — the auto-dump it offers is already partly broken.

Net: the re-decode is either fidelity-unsound (sketch as written) or as-expensive-as-what-it-removes (snapshot) or a
multi-file contract change (producer re-prime). The opt-in gate sidesteps all three: when enabled it runs the current,
faithful decode with tracing on; when disabled it costs one branch.

---

## 5. End-user diagnostic-value analysis

**Will these traces help real users diagnose decode issues?** Yes — the trace is the *only* artifact that localizes a
bit-misalignment to a specific op/field (the outlier ranking at `:1041-1069` ranks reads by `|actual − expected|` bits,
which is precisely how the prior decode bugs were found). For an end user hitting a decode failure on a new demo
(a new game build, an unseen class, an AnimGraph-era change), this trace is the front-line tool.

**Does the failure reproduce on a deliberate re-run?** Yes — decode is deterministic in the demo bytes. The same
`.dem` file produces the same bit stream and the same checkpoint priming every run (the parallelism partitions
deterministically at DEM_FullPacket boundaries, `ParallelDigestProducer.cs:148-208`). So an end user who hits an error
can re-run with the trace flag enabled and get the full bit-trace of the exact same failure. This is the key fact
that makes the opt-in gate viable: *we don't need automatic capture on the first error, because the user can
deterministically reproduce it on demand.*

**What re-run-with-flag can reproduce:** every failure that is a function of the demo bytes + schema — i.e. all
known decode-bug classes (classid, baseline, AnimGraph2, wire-shape drift). These are deterministic.

**What a naive in-process re-decode-on-error cannot reproduce:** the parallel checkpoint-priming-specific
failure from finding #3 — a desync that only manifests when chunk *k* is primed from `PrimeFromCheckpoint` rather than
reached by from-scratch sequential replay (an incremental-string-table / baseline-priming edge, the exact class of bug
the parallel-decode work had to fix). For *that* failure, re-decoding `msg` against mutated
state (or against a from-scratch tracker) will not put the reader in the primed pre-state, so it may not reproduce —
or may reproduce a *different* fault. The opt-in gate, by contrast, traces the *actual* primed decode in situ, so it
captures priming-specific failures faithfully.

**What no design can fully reproduce post-hoc:** a genuinely non-deterministic failure (a data race in shared mutable
state across workers). Nothing in the trace is built for that — but the architecture already isolates per-worker state
(`ParallelDigestProducer.cs:114-119` gives each worker its own layer + providers), so this is out of scope, and an
in-situ opt-in trace is still the *best available* tool for it (it captures whatever actually happened, once).

---

## 6. Recommendation (decisive)

Gate the trace behind the existing runtime opt-in (`Profiling.Enabled` doctrine), and add a
zero-healthy-cost error breadcrumb. Reject gate-off-and-re-decode.

Concretely:

1. **Gate the whole trace context behind a runtime flag, snapshotted once per packet** — mirror the profiling pattern
   already in this file (`bool __prof = Profiling.Enabled` at `EntityTracker.cs:1190, 2012, 2319`; producer snapshot at
   `ParallelDigestProducer.cs:103`). Introduce a sibling flag (e.g. `Profiling.TraceEntityDecode`, or reuse a trace-tier
   of the existing switch) read once at the top of `ProcessPacketEntities` into a local; `_traceContextActive` is set
   true only if that flag is on. Then:
   - **Default (flag off):** `_traceContextActive` stays false → `AddTrace` no-ops at `:566`; *and* wrap the
     unconditional construction sites (`:2314` BeginEntity, `:2349/2364/2377` PathOp, the Prelude sites) in
     `if (_traceContextActive)` so the `DecodeTraceEntry` struct isn't even built. Healthy-path cost → one predicted
     branch per op (the same shape as the existing `__prof` checks), removing the ~7 M struct-construct + `List.Add`.
   - **Flag on:** today's exact, faithful behaviour — the trace records the real in-situ decode (including primed chunks),
     no re-decode, no fidelity gamble.

2. **Add a cheap always-on breadcrumb on the error path only:** the catch at `:1958-1993` already has
   `_curEntityIndex`, the live `ClassName`, and `ex.Message`. Emit one line — `"decode error at packet N, class X; re-run
   with <trace flag> for the full bit-trace"` — gated by `!_errorLogged` as today. This costs nothing on the healthy path
   (it runs only on an exception) and tells the user how to get the full trace deterministically (§5).

3. **Fix the parallel auto-dump defect while here** (finding #4): `DumpTrace()`'s `Console.WriteLine` loop
   (`:1018-1039`) interleaves across workers. When the trace flag is on, serialize the dump (lock, or buffer to a single
   string and write once) and tag it with the worker/chunk id so multi-worker output is readable. This is a correctness
   fix for the *enabled* path, not a healthy-path cost.

Why this is the right call:
- It is cheaper-or-equal to the re-decode design on the healthy path (same single-branch elision, same
  struct-construction removal) and it eliminates the fidelity problem (no re-decode, no re-prime, no per-packet
  snapshot, no two-file contract change).
- It is golden-neutral by construction, exactly as the candidates entry claims: the trace feeds only Console
  output + the App-only `DecodeError` event, never decoded values or stats (`:1962-1993`). Gating it cannot move
  `ours.golden.json`.
- It matches the repo's own established doctrine — runtime opt-in via `Profiling.Enabled` / `DEMOVIEWER_PROFILE=1`
  (`Profiling.cs:47`; docs/profiling.md), so there's one discoverable switch and no new mental model.
- It preserves and improves end-user diagnostic value: the user gets a breadcrumb by default and the *faithful*
  full trace (including parallel-priming-specific failures) on a deterministic re-run — the one thing the re-decode
  cannot guarantee.

Do not: ship a ring buffer (solves nothing), sampling (loses the perpetrator), or EventSource-per-op (wrong
granularity, worse when enabled), and do not implement the `RedecodePacketForTrace(msg)` sketch as written
(fidelity-unsound, §4).

The discriminating question: *is automatic trace capture on the first error in a default
(un-flagged) run a hard requirement?*
- No → the opt-in gate + breadcrumb wins outright (recommended). The breadcrumb + deterministic re-run covers it.
- Yes → keep an auto-capture path, but implement it at the producer level (fresh layer → `PrimeFromCheckpoint` →
  replay `chunk.Start..N-1` → traced decode of N), not the tracker-local `msg`-only re-decode — and
  accept the two-file contract change that entails.

Resolved 2026-06-20: not a hard requirement. The opt-in gate + breadcrumb shipped as
`DEMOVIEWER_TRACE_DECODE` (default off) — see `results.md` for the measured effect (~−5 % real decode).

---

## 7. Verification (when implemented; not part of this assessment)

- **Correctness:** golden byte-identical for every demo + StatParity + `ParallelDigestEquivalenceTests` + decode
  tripwire (the candidates doc's verification protocol). Neutral by construction — the trace never touches decoded state.
- **Performance:** re-run the sweep; the `dotnet-trace` `AddTrace` + `DecodeTraceEntry..ctor` fraction should collapse to
  ~0 on the default path (expect the ~0.1–0.3 s estimate, diluted by parallelism — design-derived, confirm by
  measurement).
- **Enabled-path smoke test:** set the trace flag, force a known decode error (a deliberately corrupted packet), confirm
  the full bit-trace + outlier ranking still print and are readable under parallel workers (finding #4 fix).

---

## 8. Summary of findings

| # | Finding | Evidence |
|---|---|---|
| 1 | Trace retains only the single most recent packet (clear-per-packet). "Always-on captures more history" is false. | `:1944`, `:1011` |
| 2 | Cost is per-op CPU (branch + struct-construct + `List.Add`), not allocation. Ring buffer / alloc redesign buys nothing. | `:135`, `:2487`, `:2530-2541`, `:564-570` |
| 3 | Re-decode-on-error is fidelity-unsound under parallel checkpoint-priming. | `ParallelDigestProducer.cs:114, 121-124, 126-130` |
| 4 | Auto-dump defect: `_errorLogged` is per-tracker; concurrent `DumpTrace` Console writes interleave. | `:144`, `:1963`, `:1018-1039` |
| — | Recommendation: opt-in runtime gate + error breadcrumb + serialize the dump. Reject the re-decode sketch. | §6 |
