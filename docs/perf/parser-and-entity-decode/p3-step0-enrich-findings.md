# Pass-3 enrich non-linearity — root cause

Branch `perf/parser-and-entity-decode`, 2026-06-20. Read-only investigation of the baseline finding
that `furia m2-inferno` spends 15.9 s of its 21 s load in Pass-3 enrich, non-linearly. Diagnosed
here; the recommended fix shipped as the enrich string-table gate (see `results.md`).

## The differentiator: Pass-3 allocation, not frames/events

From the baseline sweep:

| Demo (ESL, same build) | frames | events | **Pass-3 alloc** | Pass-3 time |
|---|--:|--:|--:|--:|
| furia m1-mirage | 249,912 (more) | 29,161 (more) | **8.2 GiB** | 3.1 s |
| **furia m2-inferno** | 213,250 | 28,183 | **34.3 GiB** | **15.9 s** |

Inferno has *fewer* frames and events yet allocates 4.2× more and runs 5.2× longer. So the cost
scales with something other than message count — and it's allocation-bound (time tracks alloc).

## Root cause (measured): `StringTableProcessor.ProcessCreate → DecodeEntries` on non-`userinfo` tables

Temporary GC-delta instrumentation (per-method `GC.GetAllocatedBytesForCurrentThread`, since
`dotnet-trace` wasn't installed; reverted after the run) split inferno's 34.3 GiB Pass-3 allocation
decisively:

```
[STP-DBG] snapshot=1 MiB / 53 calls   update=8 MiB / 9 calls   create=34,303 MiB / 13 calls
```

- `ProcessCreate` is ~100 % of it (34,303 MiB ≈ the full Pass-3 alloc, confirmed to the MiB). `ProcessSnapshot`
  (1 MiB) and `ProcessUpdate` (8 MiB) are negligible — the first guess (snapshot deep-copy) *and* the arithmetic
  guess (update) were both wrong; the measurement settled it. Lesson: instrument before attributing.
- `ProcessCreate` (`StringTableProcessor.cs:79`) decodes each `svc_CreateStringTable`'s initial entries via
  `DecodeEntries` (`:95` → `:197`), allocating per entry (`new byte[]` user-data `:274/:284`, plus `Entries` growth
  `:218`). The churn is concentrated in the large non-`userinfo` table(s) — almost certainly `instancebaseline`
  (entity baselines). (`GC.GetAllocatedBytesForCurrentThread` measures total allocation *throughput*, so 34 GiB is
  decode churn over the create calls, not live size.)
- But Enrich only ever reads `stringTables.Players` (`DemoParser.cs:507`; the processor is a local at `:364`,
  discarded after), and `Players` derives solely from `userinfo` (`ExtractPlayers`/`ExtractPlayersFromState`,
  `:297/:307`). `userinfo` is tiny (~15 players × ~318 B), so essentially all 34 GiB is decoding tables enrich never
  reads. (The entity decoder builds its own `instancebaseline` from the wire in the Analysis layer — it does not
  use the parser's `StringTableProcessor`.)

Why inferno ≫ m1-mirage: inferno is more entity-dense → a larger `instancebaseline` to decode on create → more
churn, even with fewer frames.

## Recommended fix (targeted, non-protected, expected golden-neutral)

`StringTableProcessor.cs` is not a protected file. Gate the per-entry decode/copy to the `userinfo` table only —
everywhere it happens, so the fix holds regardless of which method dominates:

- `ProcessCreate` (the measured hot path): still register every table (`GetOrCreateByName`, set the flags,
  `_byId.Add` — preserves `table_id` alignment for `ProcessUpdate`), but skip `DecodeEntries` unless the table is
  `userinfo`. This removes ~all 34 GiB.
- `ProcessUpdate`: skip `DecodeEntries` for non-`userinfo` (it already only extracts players for `userinfo`) —
  negligible alloc, but consistent and future-proof.
- `ProcessSnapshot`: skip the `ToByteArray()` copy for non-`userinfo` tables — negligible (1 MiB), for consistency.

Correctness-neutral for `Players` (the only output): tables are independent, `DecodeEntries`' substring history is local
to each call (`:201`), and non-`userinfo` content is never read — so skipping it cannot affect `Players`. Keep the
merge-only player logic (`:127-133`) intact. Hard gate: golden byte-identical + StatParity 0-divergence (player
names feed game-event rows).

## Impact on the parallelization plan

If this fix lands the predicted win, Pass-3 stops being the dominant cost on every demo (inferno ~21 s → ~5 s with
*no* parallel rewrite), which may make the full snapshot-sliced parallel enrich unnecessary or much smaller — exactly
the "bigger, lower-risk win than the parallel rewrite" this investigation was meant to surface. Re-measure (full
corpus sweep) after the fix; re-decide whether the parallelization is still worth it.

## Verify (when implemented)

Own sub-branch; full verification protocol (all 12 demos): golden byte-identical + StatParity 0-divergence (the hard
gate — `Players`/names feed game-event rows), and record the per-demo Pass-3 alloc + time delta (esp. inferno) vs the
baseline.
