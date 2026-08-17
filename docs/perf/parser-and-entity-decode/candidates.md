# Parser & entity-decode performance — candidate changes

Branch `perf/parser-and-entity-decode`, written 2026-06-19 before any code changed. A shortlist of
correctness-neutral changes to the parser and entity decoder, with before/after sketches and rough
gain estimates, for review and selection. The numbers in the candidate entries are estimates from
existing measurements (the retired load-perf plan, in git history); none were re-measured for this
doc — that is what the baseline sweep below is for.

## Conventions

The core parse files (`DemoParser.cs`, `BitBuffer.cs`, `LEB128Utils.cs`, `DemoFrame.cs`) are
normally off-limits to casual edits. They are protected to stop *unexpected* feature drift from
silently degrading performance or correctness — not to forbid deliberate perf work. For this effort
they are in play: a diff that touches one of them gets a careful code-quality review before merge,
but that review is about quality, not feasibility, and planning assumes a sound change proceeds.
Plan the change on its merits.

Every change is correctness-gated (see the baseline sweep): golden byte-identical
(`ours.golden.json`) + StatParity vs the Leetify oracle + `ParallelDigestEquivalenceTests` + the
decode tripwire. A change that passes these is behaviour-preserving by definition.

## Where the time is

What was already known going in:

- Warm load ~6–8 s. Parse ~4–5 s (~52 %) in `DemoParser.cs`; Eval ~3.5 s = parallel entity-decode
  precompute ~2.1–2.4 s + sequential eval loop ~1.3 s.
- Entity decode (now run in parallel across ~6–7 effective cores): field-value reads ~1.9 s +
  field-path Huffman ~1.3 s in sequential-equivalent terms.
- All load-time allocation was already cut (12.2 GiB → 1.2 GiB). What remains is genuine CPU, not GC.

Two structural facts shape the estimates:

1. Entity-decode wins are diluted by parallelism. A 10 % per-op decode speedup shrinks each worker,
   so it takes ~10 % off the *2.1 s precompute*, not off the whole load. Real but bounded.
2. Parse is the bigger raw slice. Pass 2 (decompress + proto-parse) is the bulk and uses an existing
   `Parallel.For`; Pass 3 (enrich) is fully sequential. Every parse lever lands in `DemoParser.cs` /
   `BitBuffer.cs` (reviewed diffs; see Conventions).

---

## The baseline sweep (required before any candidate)

A fixed, repeatable measurement sweep across the full demo corpus on the then-current main tip,
recorded per demo as the reference every candidate is compared against. No code change. Not
optional — it is the apples-to-apples yardstick for both performance and correctness deltas.

### Inputs — the full demo corpus (hold constant for the baseline and every candidate re-run)

Test all 12 local demos. Breadth across source, map, and game revision is the point: a change
that's clean on one source/map can regress another. Run the whole corpus for both performance and
correctness.

| Set | Count / demos | Source class | Maps | Correctness oracle |
|---|---|---|---|---|
| `demos/benchmarks/` | 5 · `0038…_…​.dem` | GOTV / Valve MM | mixed | Leetify `*.leetify.json` (StatParity) + golden |
| `demos/pro-demos/` | 7 · furia-vs-vitality m1–m4, vitality-vs-fut m1–m3 | Pro / HLTV | mirage, inferno, nuke, overpass, dust2 | golden byte-identical only (no Leetify oracle) |

- Performance: all 12, recorded per-demo and aggregated. Sources and maps differ — don't average
  away a per-source regression.
- Correctness, golden byte-identical (our output unchanged): all 12. This is the primary "did the
  change alter output" gate and needs no external oracle — snapshot each demo's output during the
  baseline, then diff byte-for-byte per demo on every candidate.
- Correctness, accuracy parity vs Leetify: the 5 benchmarks (the only demos with oracles).
- Coverage gap, flagged honestly: the corpus covers Valve-MM GOTV and pro HLTV (ESL / BLAST
  events — see the baseline notes in results.md); FaceIt is not represented. If that source
  matters, add demos from it — it can have different tick rates / message mixes.
- Build `-c Release`. GC: Server GC (the established Desktop+bench config). Same machine, quiesced;
  discard the cold run, report the median of N ≥ 3 warm runs *per demo*. Headline wall-clock
  without `--profile` (instrumentation adds per-seam cost); per-pass breakdown with `--profile`;
  deep CPU attribution via `dotnet-trace` on a representative subset (≥1 Pro/HLTV + ≥1 GOTV/MM) —
  that tool is for *finding* where time goes, not a per-demo gate, so it needn't run on all 12.
- Practical tip: iterate the inner dev loop against one small benchmark. The full 12-demo corpus is
  the accept/reject gate and the source of the isolation/combined profiles — not the inner loop.

### Measurements (tools + what each yields)

1. **Headline wall-clock — every demo.** Benchmarks via the suite (also does golden + Leetify): `dotnet run --project
   tools/AnalysisBench -c Release -- --suite --no-golden`. Pro-demos (not covered by `--suite`) via a per-demo loop:
   ```sh
   for f in demos/pro-demos/*.dem; do dotnet run --project tools/AnalysisBench -c Release -- "$f" --no-golden; done
   ```
   → Read / Parse / Build / Eval / Total per demo. (`--no-golden` so iterating never clobbers the oracle.)
2. **Per-pass Parse split — every demo.** Add `--profile` to both of the above → `ParseProfiler` → Pass1 / Pass2 /
   Pass3 ticks + compressed-frame count, plus the entity sub-phase profile (precompute, eval loop). Already
   emitted; just never recorded.
3. **Intra-Pass-2 / intra-decode split — representative subset.** One `dotnet-trace` CPU sample over a `--profile` run
   per representative demo (`dotnet-trace collect --output baseline-<demo>.nettrace -- dotnet run -c Release --project
   tools/AnalysisBench -- "<demo>" --profile --no-golden`), viewed in speedscope/PerfView. Yields the
   decompress-vs-proto-parse fraction inside Pass 2 and the trace-vs-Huffman-`ReadOp`-vs-decoders-vs-`BitBuffer`
   fractions inside decode — the numbers that size the inner-message-copy and decompress-pool candidates on the parse
   side, and the trace-gate and Huffman-table candidates on the decode side. Run it on ≥1 Pro/HLTV + ≥1 GOTV/MM demo
   (the message-mix differs by source); sources ship in the binary, no special build.

### Correctness baseline (capture green, byte-for-byte, across the corpus)

- Golden byte-identical, all 12 demos. Snapshot each demo's output as the reference: `--suite` materialises
  `ours.golden.json` for the 5 benchmarks; for the 7 pro-demos, capture each per-demo report
  (`bench-reports/<demo>_…json`) from its baseline run. Treat all 12 snapshots as the byte-identical reference, then
  stop re-baselining (use `--no-golden` thereafter). A perf change must reproduce every snapshot exactly.
- StatParity vs Leetify, the 5 benchmarks: `… --treenode-filter "/*/*/StatParityTests/*"` → 0 divergences.
- Structural gates — cycle `DEMO_PATH` across the corpus (at minimum the representative subset, ideally all 12):
  `ParallelDigestEquivalenceTests` → 0 mismatches; `EntityIntegrationTests` incl. the decode tripwire
  `EntityDecode_IsHealthy_NoMisalignmentAndPawnsResolve`; `FullPacketCheckpointSpikeTests` (Parser.Tests) → green.

### Output of the baseline

A recorded table — one row per demo plus an aggregate: Parse {Pass1/Pass2/Pass3, decompress-vs-parse},
decode {trace/ReadOp/decoders/BitBuffer}, precompute, eval loop, Total (median warm) — plus all correctness gates
green per demo. Every candidate compares against *this* table, per demo.

### Results storage

`scripts/perf-sweep.sh <run-id>` writes each sweep's raw output — `results.tsv` (structured
per-demo numbers) plus per-demo `*.profile.txt` (full `--profile` output) — into the local
`runs/<run-id>/` directory, which is untracked scratch. Keep the correctness-gate output alongside
it. Runs worth keeping get summarized into the sibling `results.md`: a run-index row, a column in
the master per-demo comparison table (Δ vs baseline), and any key-insight bullets. Update it after
every run — the summary tables there are the durable record; the raw sweep output is not committed.

---

## Verification protocol — run for every candidate

After implementing a candidate, re-run the exact baseline sweep — all 12 demos, same `-c Release` +
Server GC, same N ≥ 3 warm runs (median per demo), same `--profile` / `dotnet-trace` tooling — and
record the per-demo + aggregate delta vs the baseline table. A candidate is accepted only if both
hold on every demo:

1. Performance: the targeted metric improves (or at minimum holds) and Total doesn't regress on any
   demo/source. A per-source regression must not hide inside a better average.
2. Correctness: all gates stay green — output byte-identical to the baseline snapshot for every
   demo, StatParity 0 divergences (benchmarks), `ParallelDigestEquivalenceTests` 0 mismatches,
   decode tripwire + spike green.

Each candidate below names the specific metric its re-run should focus on, so the comparison is
targeted. Summarize every run into `results.md` (see Results storage above).

---

## Branching & isolation strategy

One candidate per sub-feature branch off this integration branch, so each lands an isolated,
independently-measured delta — then stack them to get the realistic combined profile.

```
perf/parser-and-entity-decode      ← integration branch (this doc + the recorded baseline table)
├── perf/pae/enrich-stringtable-gate
├── perf/pae/trace-gate
├── perf/pae/huffman-table
└── …                              one branch per accepted candidate
```
> Sub-branches use the `perf/pae/<candidate>` prefix (not `perf/parser-and-entity-decode/<candidate>` — git can't nest a
> branch under an existing branch ref of the same name).

1. Isolate — branch per candidate off the integration branch; implement only that one change; run
   the full verification protocol (all 12 demos) → its *isolated* perf delta + corpus-wide
   correctness. Record the row; reject in place if it regresses any demo or breaks a gate.
2. Combine — merge accepted candidates into the integration branch one at a time, re-running the
   full protocol after each merge → the stacked profile. This catches interactions the isolated
   runs can't (e.g. the trace gate and the Huffman table both shrink decode and both ride the same
   parallelism dilution, so their combined wall-clock ≠ the sum).
3. Attribute — keep both numbers per candidate: *isolated* (clean attribution) and
   *marginal-in-stack* (its real contribution once the others are in). Record the stacking order
   with the results — it changes the marginal numbers.

---

## Baseline outcome → revised priorities (2026-06-19)

The 12-demo baseline (`results.md`) reorders these candidates. Headline: Pass-3 enrich is the
dominant and wildly variable parser cost — `furia m2-inferno` (ESL) spends 15.9 s of its 21 s load
in enrich, non-linearly (the *bigger* `furia m1-mirage`, more frames + events, enriches in 3.1 s).
Entity decode (`Eval`) is a stable 2.8–3.9 s floor; Pass-2 (decode+decompress) is modest,
0.44–1.7 s; Pass-1 is trivial (14–34 ms).

Revised priority (the pre-baseline ordering was trace gate → Huffman table → parser):

1. Parallelize Pass-3 enrich — was "gain unknown," now the biggest single lever, plus a likely
   non-linear bug on inferno. There is a concrete frame-slicing design (below). Highest value.
2. The decode-trace gate — cheap, certain, non-protected; its approach was settled by the
   observability assessment (below).
3. The decoder-dispatch collapse — implement + measure to decide keep/drop (promoted from
   "mention only").
4. The table-driven Huffman decode — targets the stable decode floor; needs MaxCodeLen sizing first.
5. The Pass-2 levers (bulk ReadBytes, decompress pool, copy elision) — Pass-2 is modest, so lower
   priority; explore only if a `dotnet-trace` shows the copy/decompress is actually hot. The
   decompress pool is a near-free GC win if pursued.

Notes from the 2026-06-19 review are folded into each candidate below.

---

## Entity-decode candidates

### Candidate: runtime opt-in gate on the decode trace — approach decided

The trace in question is the entity-decode bit-misalignment diagnostic.
**Files:** `EntityTracker.cs` (non-protected; coordinate with the Schema-Lens work) · **Risk:** low ·
**Correctness:** golden-neutral *by construction* (the trace feeds only console + the App-only `DecodeError` event, never decoded values/stats).
Decision basis: `d1-trace-observability-assessment.md` (2026-06-20); auto-capture in a default run was ruled not required.

Today the decode-trace is armed for every packet (`EntityTracker.cs:1944`), so every op builds a `DecodeTraceEntry`
and `_trace.Add`s it — ~7 M times/load — even though the trace is only ever *read* on a decode error (`DumpTrace`,
`_errorLogged`-gated, ≈never on a healthy demo). The cost is per-op CPU (gate branch + `readonly struct` construct +
`List.Add`); it is not allocation (the `List` is cleared/reused each packet — no GC; the lazy `Path` already removed
the one allocator), so a ring-buffer/alloc redesign buys nothing.

Decided approach — a runtime opt-in gate (the repo's own `Profiling.Enabled` doctrine), *not* re-decode. The
originally sketched "gate off + re-decode the failing packet" is rejected: it is fidelity-unsound under parallel
decode. A re-decode runs against already-mutated state and the tracker can't re-prime its own checkpoint (that lives
in `ParallelDigestProducer`), so it would fail to reproduce exactly the parallel-priming bugs the trace exists to
catch.

```csharp
// Healthy path pays one predicted branch (same shape as the existing per-op profiling gate, EntityTracker.cs:2012);
// the per-op DecodeTraceEntry construct + List.Add happen ONLY when tracing is explicitly enabled.
if (Tracing.Enabled) { _trace.Clear(); _traceContextActive = true; }   // arm only when opted in
try { ProcessPacketEntitiesCore(msg); }
catch (Exception ex)
{
    // Zero-healthy-cost breadcrumb: read EXISTING state at the catch (no per-op work) so a DEFAULT run still
    // reports "decode error at packet N, class X, last path P". Full bit-trace requires a re-run with the flag —
    // decode is deterministic in the demo bytes, so any failure (incl. parallel-priming) reproduces faithfully.
    LogDecodeErrorBreadcrumb(ex);
    if (_traceContextActive) DumpTraceSerialized();   // serialize the dump (parallel workers' Console writes interleave)
}
finally { if (Tracing.Enabled) _traceContextActive = false; }
// + the unconditional construction sites (BeginEntity ~2314, PathOp ~2349/2364) must check the gate so nothing is built when off.
```

Also fix, found by the assessment: the auto-dump is per-tracker (`_errorLogged` at `:144`) and concurrent
`DumpTrace` console writes interleave under parallel decode — serialize the dump.
**Gain (est.):** removes ~7 M per-op constructs + `List.Add`s from the default path → ~0.1–0.3 s off the
parallel-decode precompute (per-op CPU, diluted by parallelism).
**Verify:** re-run the sweep; focus on the precompute / decode slice (expect −0.1–0.3 s) and confirm the per-op
trace work is absent when the flag is off; correctness gates green (neutral by construction). Add a test that enabling
the flag still produces a faithful trace.

Decision (2026-06-20): auto full-trace capture on the first error in a *default* run is not required, so the opt-in
gate + breadcrumb wins outright — no producer-level auto-capture machinery needed. The assessment doc has the full
rationale, including why the re-decode sketch was unsound.

### Candidate: table-driven Huffman field-path decode

**Files:** `FieldPathEncoding.cs` (non-protected) + likely a peek/skip primitive in `BitBuffer.cs` (reviewed) ·
**Risk:** medium · **Correctness:** provably identical if the table is generated from the existing canonical tree (golden-gated).

`ReadOp` chases heap `HuffmanNode` record pointers one bit at a time for every field-path op (~3.5 M+/load),
cache-unfriendly:

```csharp
// BEFORE — FieldPathEncoding.cs:273 — one ReadOneBit + pointer-deref per code bit
public static FieldPathEncodingOp ReadOp(ref BitBuffer buffer) {
    var node = HuffmanRoot;
    for (;;) {
        var next = (buffer.ReadOneBit() ? node.Right : node.Left) ?? throw new InvalidDataException(...);
        if (next.Symbol is { } op) return op;
        node = next;
    }
}
```

```csharp
// AFTER — peek MaxCodeLen bits once, single flat-array lookup, advance by the real code length
static readonly (FieldPathEncodingOp Op, byte Len)[] _table; // size 1<<MaxCodeLen, built FROM HuffmanRoot
public static FieldPathEncodingOp ReadOp(ref BitBuffer buffer) {
    uint peek = buffer.PeekBits(MaxCodeLen);   // no advance  ← needs a peek primitive
    var (op, len) = _table[peek];
    buffer.SkipBits(len);
    return op;
}
```

**Gain (est.):** replaces the per-bit chase with one lookup → ~0.1–0.3 s wall (a fraction of the ~1.3 s field-path
loop, diluted by parallelism). **Caveat:** needs `PeekBits`/`SkipBits` on `BitBuffer` (reviewed) unless built
from existing primitives. Requires a table-vs-tree equivalence test (a mis-built table silently mis-decodes every
packet). Also verify the max code length first: the flat table is `1<<MaxCodeLen` entries — fine at ~8–12 bits, but
skewed op frequencies can push it to 15–25+ bits (multi-MB → infeasible), forcing a capped / two-level table. Size it
before committing.
**Verify:** re-run the sweep; focus on the `dotnet-trace` `ReadOp` / `HuffmanNode` inclusive-time fraction (expect a
clear drop) and the field-path slice; golden + StatParity + the new table-vs-tree equivalence test green.

Does the table fully replace the Huffman tree? *On the runtime hot path, yes:* `ReadOp`
would no longer touch `HuffmanNode` / `HuffmanRoot` at all — just one flat-array lookup + advance. *At startup, the tree
stays as a one-time generator:* `FieldPathEncoding`'s static ctor builds `HuffmanRoot` from the 39 ops' frequencies
(`HuffmanNode.Build`, `HuffmanNode.cs:8`); the table is generated by walking that tree once to emit `(op, codeLen)` per
leaf, so the tree remains the single source of truth for the canonical codes — never traversed during decode. We *could*
drop the tree entirely by hardcoding/serializing the table, but keeping it as the build-time generator avoids a
hand-maintained table silently drifting from the canonical codes (recommended). Net: replaces the tree at runtime;
retains it (cheaply, once) at init. Sizing gate first: 39 symbols with heavily skewed frequencies (PlusOne 36,271 …
rarest 35) → compute the actual MaxCodeLen before sizing `1<<MaxCodeLen`; if it's large (≳17 bits), use a capped /
two-level table rather than one giant flat array.

### Candidate: collapse per-field decoder dispatch — implement + measure to decide

Each leaf value read is an indirect delegate call through a per-field `IntDecoder`/`FloatDecoder`/`FieldDecoder`
closure. An enum-tag `switch` could remove the indirection (`FieldDecoderFactory`/`ReadAndTrace` rewrite). Non-protected,
medium risk.

Promoted (2026-06-19) from a mention to an experiment: implement the indirection removal on its own sub-branch and
run the full verification protocol to compare, then decide keep/drop on the *measured* number — don't pre-judge it
as "modest/unproven." It's a clean experiment: reversible and golden-gated.
**Verify:** re-run the sweep (all 12 demos); focus on the field-value / decoder-delegate slice and Total; golden +
StatParity + decode tripwire green. Keep only if the measured delta justifies the rewrite.

> **Not feasible — selective decode.** Fields are variable-width and data-dependent with no length prefixes, so you
> must fully decode field N to find N+1, and the path loop must read every op before the values begin. "Decode only the
> fields analysis needs" does **not** work. The only skippable work is bookkeeping (the trace gate above).

---

## Parser candidates (in `DemoParser.cs` / `BitBuffer.cs` — reviewed diffs; planning assumes they proceed when sound)

### Candidate: faster `BitBuffer.ReadBytes` for the inner-message copy (conditional — mind the alignment trap)

**File:** `BitBuffer.cs` (reviewed) · **Risk:** med · **Correctness:** identical bytes.

`ReadBytes` copies one byte per slot (each `ReadByte` shifts across the buffered word) and is called ~1.81 M times
in Pass 2 to extract every inner message (`DemoParser.cs:798`).

> **The obvious "block-copy when byte-aligned" fast path almost never fires here.** The inner-message header is read
> at *bit* granularity — `typeId = ReadUBitVar()` (6/10/14/34 bits) then `size = ReadUVarInt32()` (multiples of 8) — so
> the payload start is misaligned by 2 or 6 bits on essentially every message (`DemoParser.cs:772-773` says exactly
> this). A simple `if (aligned) blockcopy` buys ~nothing for the dominant use case.

The real lever — *only if* the baseline shows this copy is hot — is a misaligned bulk read: shift 32/64-bit words out
of the source span instead of one `ReadByte` per byte. That's a more involved change inside the buffered-word reader
(`_buf` / `_bitsAvail` / `_original` / `TellBits`), with unproven gain.

```csharp
// BEFORE — BitBuffer.cs:202 — one ReadByte (word-shift) per byte
public void ReadBytes(scoped Span<byte> output) {
    for (int i = 0; i < output.Length; ++i) output[i] = ReadByte();
}
// AFTER (illustrative — exact form depends on the buffered-word internals _buf/_bitsAvail/_original/TellBits):
//   if ((TellBits & 7) == 0)  → copy _original[TellBits>>3 ..] directly   (RARE for inner messages)
//   else                      → shift 32/64-bit words from _original into `output`   (the misaligned bulk read = the real win)
```

**Gain (est.):** unknown — sized by the baseline confirming the per-byte copy is a real Pass-2 slice. The *simple
aligned* version is a phantom gain on this (bit-misaligned) stream; only the misaligned bulk read is worth building,
and only once the baseline shows the copy is hot.
**Verify:** re-run the sweep; focus on Pass-2 wall-clock and the `dotnet-trace` `ReadBytes`/`ReadByte` fraction;
golden byte-identical (it's a pure copy).

Interesting concept, but explore only if the baseline shows reason to (2026-06-19). And the baseline read: Pass-2 is
modest (0.44–1.7 s) across the corpus, and the inner-message copy is only a *fraction* of that — so the bar to
justify a `BitBuffer.cs` change here is high. Need the `dotnet-trace` Pass-2 split to confirm the copy is genuinely
hot before building the (only viable) misaligned bulk read. Lower priority than the enrich and decode work.

### Candidate: pool / reuse the Snappy decompress output buffer

**File:** `DemoParser.cs:180` (reviewed) · **Risk:** low · **Correctness:** identical bytes.
`Snappy.DecompressToArray` allocates a fresh `byte[]` per compressed frame. A per-worker reused buffer (the payload is
fully consumed inside `ParseFrame` before the next iteration) removes that alloc. **Gain:** mostly GC pressure; modest
CPU — only worth it if the baseline shows decompress is a large Pass-2 fraction.
**Verify:** re-run the sweep; focus on Pass-2 wall-clock + Gen0 GC count / allocated bytes; golden byte-identical.

Does a reused buffer risk consumers holding stale references? *Verified against the code:
no aliasing risk.* The returned `DemoFrame` / `NetMessage` retains no reference into the decompressed buffer —
`NetMessage.Payload` is a parsed protobuf `IMessage` (Google.Protobuf copies all bytes out during `ParseFrom`, per
the existing comment at `DemoParser.cs:792`), and byte positions are stored as integer offsets
(`NetMessage.DecompressedStart`/`DecompressedLength`; `DemoFrame.RawStart`/`RawLength`/`HeaderLength`) — there is no
`ReadOnlyMemory`/`byte[]` payload field on `DemoFrame`. The RAW/hex view re-derives bytes on demand from the *original*
file, it doesn't hold the decompressed array. So once `ParseFrame` returns, nothing points into `framePayload`.
The value-vs-reference instinct is right to check: `byte[]` *is* a reference type, so if anything stored
`framePayload` directly, reuse *would* corrupt it — the safety here comes from "everything copies out / keeps only int
offsets," not from value-copy semantics.
Caveat — the real constraint: the reused buffer must be thread-local / partition-local (Pass 2 is a
`Parallel.For`; one shared array would be stomped by concurrent workers), sized to the largest decompressed payload
(grow-on-demand) via a `Snappy.Decompress(src, destSpan)`-style call instead of `DecompressToArray`. Gate by
specifically exercising the RAW/hex view (the one consumer that touches raw bytes) plus golden.

### Candidate: parallelize Pass 3 (enrich) by slicing at full-snapshot checkpoints — the top lever post-baseline

**File:** `DemoParser.cs:203` / `Enrich` (`DemoParser.cs:361`) (reviewed) · **Risk:** med–high ·
**Correctness:** ordering-sensitive — gate hard.

The baseline made this the #1 finding. Pass-3 enrich is the dominant, wildly variable parser cost:
`furia m2-inferno` (ESL) = 15.9 s of enrich (76 % of its 21 s load), and it's *not* size-driven — the *bigger*
`furia m1-mirage` (more frames + events) enriches in 3.1 s. That non-linearity smells like a quadratic or a bug, so:

> **Do first: investigate the inferno enrich non-linearity** (read-only + a `dotnet-trace` of that demo's
> Pass-3). If it's an accidental O(n²) (e.g. a per-message scan that grows with accumulated state), fixing it could turn
> that demo's 21 s → ~5 s *without* any parallelization — a bigger, lower-risk win than the parallel rewrite. Size the
> parallelization against the *post-fix* Pass-3, not the 15.9 s outlier.

The design (2026-06-19): parallelize via full-snapshot slicing. Pass-3 enrich was sequential because the
entities used to enrich messages are logically sequential. But the parallel entity-decode work proved we can
split the frame stream at the full-snapshot messages (`DEM_FullPacket`): slice into n + 1 sections (n = number
of full snapshots; +1 for the frames before the first snapshot). Each slice is self-contained — it starts from a
*complete* entity snapshot, so it needs no state from prior slices → slices enrich in parallel; within a slice,
frames replay sequentially. Invariant (must hold exactly): when enriching message M, the entity state in use =
the most recent full snapshot + every incremental delta since that snapshot up to and including M's frame, and
no delta from any frame after M. (The first slice `[0, firstSnapshot)` replays from scratch — same as the parallel
decode's chunk-0 handling.)

Why the foundation already exists: `FullPacketCheckpointSpikeTests` already proved a worker starting at a
`DEM_FullPacket` checkpoint reconstructs the analysis-relevant entity state faithfully (string tables + instance
baselines + serializer self-contained at the checkpoint), and `ParallelDigestProducer` already chunks at exactly these
boundaries. This applies that proven pattern to the *parser's* enrich pass.

Honest risk — enrich has sequential state beyond entities. Pass-3 `Enrich` also builds `RuntimeSchema` once,
accumulates string tables, decodes game events, and fills players/teams + `GameTick`. The invariant above covers
*entity* state; each of these *other* sequential dependencies must likewise be made self-contained at the slice
boundary (or computed in a cheap sequential pre-pass and handed to the parallel workers). This is the real correctness
surface — map every piece of `Enrich`'s carried state before parallelizing. (`DEM_FullPacket` bundles a full string-table
snapshot, which helps; schema is built-once and can be shared read-only.)

**Gain (est.):** large but unquantified until the investigation. If enrich is ~3–4 s on normal demos and parallelizes
~6× like the entity decode, that's a multi-second cut on the pro-demos; the inferno bug-fix alone could be ~16 s on
that one demo.
**Verify:** re-run the sweep (all 12 demos); focus on Pass-3 ticks per demo (esp. inferno) + Total; golden
byte-identical + StatParity 0-divergence are the hard gate — reordering/merge bugs in game-event or player/team
extraction would surface there. Own sub-branch; this is the most involved candidate.

### Candidate (higher risk): eliminate the per-inner-message copy entirely

Skip the `ArrayPool` rent + `ReadBytes` round-trip and parse `ProtoFrom` directly off the bitstream span where
byte-aligned (`DemoParser.cs:794`). Potentially larger than the bulk-read candidate, but the bit-alignment
correctness is exactly the zone where a mistake cascades through every later message. Only after the bulk-read work
and the baseline evidence; treat as a separate, carefully-gated effort.

One instinct (2026-06-19) was that if byte-alignment is rare (~1 in 8), a per-call alignment check would eat the
gain on the hit path. Stronger, from the code: the inner-message payload is misaligned by 2/6 bits on *essentially
every* message (`DemoParser.cs:772-773`) — aligned hits aren't ~1 in 8, they're ~never — so any "align→fast path,
else→slow path" branch is net-negative. The only viable form (here and in the bulk-read candidate) is an
unconditional misaligned bulk word-shift read with no per-call alignment branch. This reinforces *not* doing a simple
aligned fast path.
**Verify:** re-run the sweep; focus on Pass-2 wall-clock; golden byte-identical + decode tripwire green (alignment
bugs surface here).

---

## Recommended order (post-baseline, risk-adjusted)

0. Baseline — done; recorded across all 12 demos (`results.md`).
1. Investigate the inferno Pass-3 enrich non-linearity (read-only + `dotnet-trace`). Potentially the
   biggest, lowest-risk win (a quadratic fix could shave ~16 s off that one demo) *and* it sizes the
   parallel-enrich rewrite.
2. The decode-trace gate + error breadcrumb (approach decided; assessment done). Cheap, certain,
   non-protected, golden-neutral — ready to implement. (Re-decode-on-error was rejected as
   fidelity-unsound under parallel decode; serialize the dump while here.)
3. The decoder-dispatch collapse — implement + measure on its own sub-branch; keep/drop on the
   measured number.
4. The parallel-enrich rewrite — full-snapshot-sliced, sized against the *post-investigation*
   Pass-3. Highest value, most involved.
5. The Huffman table (compute the actual MaxCodeLen first; targets the stable decode floor).
6. The Pass-2 levers — Pass-2 is modest, so lowest priority. The decompress pool is a near-free GC
   win if pursued; bulk ReadBytes / copy elision only if a `dotnet-trace` shows the inner-message
   copy is genuinely hot, and only as the unconditional misaligned bulk read.

Each candidate lands on its own sub-feature branch (see Branching & isolation strategy) —
implemented and verified in isolation across the full corpus, then stacked on the integration
branch for the combined profile.

## Summary (post-baseline)

| Candidate | Files | Reviewed (protected)? | Correctness | Priority / est. gain |
|---|---|---|---|---|
| baseline sweep | — | no | n/a | done — baseline recorded |
| enrich slicing at snapshots | DemoParser | yes | ordering-sensitive | top — inferno 15.9 s outlier; the bug-fix alone maybe ~16 s, parallelize ~6× |
| decode-trace gate (opt-in + breadcrumb) | EntityTracker | no | neutral by construction | ~0.1–0.3 s — approach decided |
| dispatch collapse | FieldDecoderFactory | no | gated | implement + measure to decide |
| Huffman table | FieldPathEncoding (+ BitBuffer peek) | partly | provably-equal table | ~0.1–0.3 s (size MaxCodeLen first) |
| bulk ReadBytes (misaligned) | BitBuffer | yes | identical bytes | low — Pass-2 modest; aligned fast-path ~never fires |
| decompress pool | DemoParser | yes | identical bytes | low — near-free GC win |
| copy elision | DemoParser/BitBuffer | yes | alignment-risk | low — larger/riskier; misaligned bulk only |

Bottom line: the baseline reordered the work. Pass-3 enrich is the headline lever — the
`furia m2-inferno` 15.9 s outlier is non-linear (likely a bug), so investigate that anomaly first
(possibly ~16 s on one demo with no parallel rewrite), then the snapshot-sliced parallel enrich.
The decode-trace gate is decided and ready: a runtime opt-in on the trace plus a zero-cost error
breadcrumb (re-decode rejected as fidelity-unsound). The remaining decode levers target the stable
~3 s floor; the Pass-2 levers are modest and gated on a trace. Every candidate is golden-gated, one
per sub-branch, verified across all 12 demos; the protected parse files are expected to change
(code-quality review, not a feasibility gate).
