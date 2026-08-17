# Risk review — gating the parser's string-table decode to `userinfo`

2026-06-20, written for the go/no-go on the enrich string-table gate. The concern: gating the
parser's `StringTableProcessor` to the `userinfo` table could break other enrichment or
Analysis-engine features — now or ones we'll want to add later.

## Short answer

Current risk: effectively zero. Nothing outside `Enrich` reads the parser's non-`userinfo` string
tables, and the Analysis engine builds the table it needs (`instancebaseline`) independently. The
fix is in fact net risk-*reducing*: the skipped work wasn't merely wasteful, it was a decode
runaway (685 M garbage entries / 32 GiB from a 12 KB input — a latent OOM hazard). Future risk is
low and the right mitigation is an allowlist + docs. Verdict: safe to keep; one small refactor
recommended.

## 1. Current consumers — does anything read non-`userinfo` parser tables?

- `StringTableProcessor` is `internal sealed`, instantiated once (`DemoParser.cs:364`, inside `Enrich`), and its only
  public member is `Players` (`StringTableProcessor.cs:71`). The tables (`_byId`/`_byName`/`Entries`) are private.
- `ParsedDemo` exposes no string tables at all — its surface is `Players`, `AllGameEvents`, `Frames`, `Schema`, and
  map/server/tick metadata (`ParsedDemo.cs`). The source even notes exposing internal tables "would widen the public
  surface for everyone" (`:13`). So non-`userinfo` tables never leave the parser.
- Only read of the processor's output: `new(stringTables.Players)` (`DemoParser.cs:507`). Nothing else.
- ⇒ No current consumer of any non-`userinfo` parser table exists — it's structurally impossible.
- Tests corroborate the structure: the full suite is green (Parser 95/0, Analysis 396/0) — if any existing feature
  read these tables, gating them empty would plausibly have broken a test. Grep + types + tests all agree.

## 2. The Analysis engine is independent (the core of the concern)

The entity decoder does not use the parser's `StringTableProcessor`. It reconstructs `instancebaseline` itself, from
the wire, in the Analysis/EntityTracking layer:
- `EntityTracker.cs:82-84` — its own `instancebaseline` reconstruction (`CSVCMsg_CreateStringTable name="instancebaseline"`
  → `CSVCMsg_UpdateStringTable`), and `:481-508` + `EntityStateLayer.cs:169-171` — the parallel-decode checkpoint
  baseline seeding.
- ⇒ The fix touches `src/Parser/.../StringTableProcessor.cs` only; the Analysis engine's string-table handling is a
  separate code path and is unaffected. (Proven empirically too: `ParallelDigestEquivalenceTests` green, StatParity
  byte-identical, entity decode unchanged.)

## 3. UI is display-only

The two App references are not parser-table reads: `HarvestFrameRowViewModel.cs:84` maps the frame-type label
`"DEM_StringTables" => "STBL"`; `PayloadNodeBuilder` decodes message payloads for the tree/hex view from the raw
messages, not from `ParsedDemo`. No UI feature reads the parser's string tables.

## 4. What is actually being skipped — and the decode-runaway finding

String-table inventory (inferno; `bytes` = create-message `StringData`):

| Table | entries | input bytes | decoded after fix? | baseline create alloc |
|---|--:|--:|---|--:|
| **userinfo** | 64 | 605 | yes (feeds Players) | 0 MiB |
| instancebaseline | 58 | 12,632 | skipped | **32,768 MiB / 684,871,049 entries** |
| ServerAvatarOverrides | 11 | 28,304 | skipped | **1,534 MiB / 6,424 entries** |
| EntityNames | 31 | 357 | skipped | 0 MiB |
| lightstyles | 64 | 519 | skipped | 0 MiB |
| AnimAssetData / AnimTaskTypes / EffectDispatch / genericprecache / server_query_info | 1–16 | tiny | skipped | 0 MiB |
| InfoPanel / Scenes / VguiScreen | 0 | 0 | skipped (empty anyway) | — |

The 34 GiB was a decode bug, not useful work. Established fact (measured): decoding `instancebaseline` via the
generic decoder produces ~685 million garbage entries / 32 GiB from a 12 KB input (`ServerAvatarOverrides` similarly:
6,424 entries / 1.5 GiB) — the misread `entryIndex` drives the `while (Entries.Count <= entryIndex)` growth loop
(`StringTableProcessor.cs:218`) to run away. Likely cause: `instancebaseline` entries aren't in the generic
string-table bitstream layout this decoder assumes (probably class-id-keyed), so the generic `DecodeEntries` mis-reads
them. The garbage was never read → silently harmless, but it churned 32 GiB and was a latent OOM hazard on
large/memory-constrained runs.

Project history corroborates: the *correct* `instancebaseline` decoder was deliberately rebuilt in the entity
layer as a faithful demofile-net `StringTable` port, after three bit-layout bugs (confirmed in current code at
`EntityTracker.cs:82-84/481-508`). So the parser's generic `DecodeEntries` was never the real `instancebaseline`
decoder — which is exactly why its broken output never mattered.
⇒ The fix removes a latent crash/OOM risk and a decode runaway, not just allocation.

## 5. Future risk + mitigations

The real question: what if a future feature wants one of these tables *from the parser*?

- Reassurance: for the two non-trivial tables, the parser's generic decoder doesn't produce usable data anyway
  (`instancebaseline` → garbage; `ServerAvatarOverrides` → abnormal). So the fix doesn't disable *working* functionality —
  it disables broken/wasteful decoding. `instancebaseline` already has a correct, independent decoder in the Analysis
  layer; player avatars (`ServerAvatarOverrides`) would need a *bespoke* decoder regardless — re-enabling the generic
  path wouldn't give correct data.
- Genuinely losable (small, currently-correct) tables: `EntityNames` (31 entries) and `lightstyles` are decoded
  cheaply today and *might* interest a future feature. After the fix they're empty in the parser. This is the only real
  "future feature" gap — and it's recoverable in one line.

Recommended mitigations (small):

1. **Allowlist instead of a hardcoded `userinfo` check** — `static readonly HashSet<string> TablesToMaterialize = { "userinfo" }`,
   gated in all three methods. Same perf; makes "which tables the parser materializes" explicit, discoverable, and a
   one-line change to extend. (Caveat to document: adding `instancebaseline`/avatar tables needs a *bespoke* decoder, not
   just an allowlist entry — the generic decoder mis-reads them.)
2. **Document the deliberate scope** in the `StringTableProcessor` class summary (only `userinfo` is materialized; why;
   how to extend; the generic-decoder caveat).
3. **Cap the `while (Entries.Count <= entryIndex)` growth** (reject `entryIndex` > the 4096 table max). This is *not* just
   a future-belt: `userinfo` is the one table still decoded on the live path, and it runs the same unbounded loop. The
   parser ingests arbitrary user-supplied `.dem` files, so a malformed/corrupt `userinfo` could trigger the identical
   685M-entry runaway → a malformed-input OOM on the shipping path. A one-line cap hardens the live path. Recommend
   folding into this fix (we're already in the file), not a separate effort.

## 6. Verdict & options

- Keep the fix. Current risk ≈ zero (proven, structural + empirical); it removes a latent OOM/decode-runaway; output
  is byte-identical across all 3 sources (StatParity + roster diff).
- Recommended: fold in mitigations #1 (allowlist) + #2 (docs) + #3 (growth cap) before merge — #1/#2 turn the
  future-risk worry into a one-line, documented extension point; #3 hardens the live `userinfo` path against
  malformed-input OOM (not just a future belt). All three are small and in the same file.
- Alternative if maximum caution preferred: gate *out* only the proven-pathological tables (`instancebaseline`,
  `ServerAvatarOverrides`) instead of gating *in* `userinfo` — keeps the small correct tables (`EntityNames`, etc.)
  decoded. Costs a little (those decodes are ~0 MiB though) and is slightly less clean, but preserves the most future
  flexibility. (Not recommended: it keeps decoding tables nothing reads, for hypothetical future use.)
