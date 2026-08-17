# Deferring the `svc_UserCmds` (subtick) parse

Shipped on `feature/v0.5.1` as part of the Workstation-GC footprint-reduction sweep (see
`results.md`), in the hold-raw-bytes form, as a generic `DeferredMessage`. The proposal is kept as
written; "As built" at the bottom records what actually landed.

## The problem, measured

Every demo parse eagerly deserializes all `svc_UserCmds` net messages into
`CSVCMsg_UserCommands` protobufs and keeps them in `frame.InnerMessages` for the entire life of
the `ParsedDemo`. On the 279 MB benchmark demo (`0126308730`):

| | |
|---|---|
| `svc_UserCmds` messages | **1,368,069** (one `CMsgServerUserCmd` each) |
| Retained by ParsedDemo (whole demo) | **919 MiB** |
| — of which usercmds (gcdump, objects + raw bytes) | **~530 MiB → 58%** |
| Parse-time allocation attributed to the family | ~243 MiB (sampled) |

The ~530 MiB breaks down (gcdump `report`, counts × managed sizes):

```
CMsgServerUserCmd[] backing   96 MiB   ← RepeatedField growth (the 94 MiB alloc hot spot)
CMsgServerUserCmd             61 MiB
ByteString wrappers           45 MiB
CSVCMsg_UserCommands          35 MiB
RepeatedField<…>              35 MiB
raw wire bytes (the payload) 258 MiB   ← the only part a consumer actually reads
```

So usercmds are the single largest retained consumer of a loaded demo — bigger than entity
state — and ~270 MiB of that is pure protobuf object overhead wrapping 258 MiB of wire bytes.

## Who actually reads it

Exactly two consumers, both lazy UI features:

1. `SubTickExtractor.Extract` — the Replay tab (`ReplayTabViewModel.cs:454`). Iterates
   `frame.InnerMessages`, casts `Payload is CSVCMsg_UserCommands`, then re-parses each
   `serverCmd.Data` into `CSGOUserCmdPB` to read `SubtickMoves`.
2. The Parser-tab inspector (`PayloadNodeBuilder.cs:38,44`) — per-frame, only when the user clicks
   into a usercmd frame.

Nothing on the shared parse/analysis path touches them — not AnalysisBench, not the Analysis
engine, not Library tier-2 background parse, not highlight backfill. Every one of those pays ~530
MiB retained + ~243 MiB churn for data it never reads.

## Proposed change: defer the parse

Stop eagerly deserializing `svc_UserCmds`. Emit a lightweight placeholder that materialises the
real `CSVCMsg_UserCommands` on demand, only when the Replay tab or inspector asks.

### The one protected-file touch

`DemoParser.cs:919`, the single dispatch arm:

```csharp
// before
(int)SVC_Messages.SvcUserCmds => Try(CSVCMsg_UserCommands.Parser, seq, typeName),
// after (shape)
(int)SVC_Messages.SvcUserCmds => DeferredUserCommands.Create(data, decompressedStart),
```

`DeferredUserCommands` is a new non-protected type implementing `IMessage` (so it drops into
`NetMessage.Payload`'s existing `required IMessage` contract with no change to `NetMessage.cs` or
`DemoFrame.cs`, and `InnerMessages` stays populated so the Parser tab still lists the frame).

This is the only change inside a protected file: one switch arm. No pipeline, threading, or slicing
logic is touched.

### Two variants — the real decision

| | **Hold raw bytes** | **Hold nothing** |
|---|---|---|
| Placeholder stores | a copied `byte[]` of the message (~size bytes) | just the byte range (`start,len`) |
| Retained after parse | **258 MiB** (raw wire only; −270 MiB overhead) | **~0 MiB** (−530 MiB) |
| Materialise reads from | its own held bytes | reconstructs via `DownstreamUtilities.ExtractInnerMessageBytesAligned` (the exact path the hex view already uses) |
| Consumer change | none — `SubTickExtractor` calls `.Materialize()` | `SubTickExtractor` must receive the demo bytes (signature change) |
| Depends on | nothing new | demo `byte[]` still alive when the Replay tab opens (App holds `_openBytes` for hex views already) |
| Risk | lower — self-contained, no reconstruction | higher — byte-range reconstruction must be exact |

Recommendation: hold nothing, for the full ~530 MiB win, with hold-raw-bytes as the safe fallback
if reconstruction proves fiddly. Both defer the ~243 MiB of parse churn out of the shared path
either way.

## Correctness gate (this is decode-adjacent)

- `SubTickExtractor` output must be byte-identical before/after on a demo with subtick data
  (capture the Replay tab's event list, diff). The current re-parse of `serverCmd.Data` is unchanged;
  only *when* the outer message is parsed moves.
- Parser-tab inspector must still render a usercmd frame (drill-in must trigger materialisation).
- Parser 99/99 + analysis 967/967 stay green.

## Not proposed

- Eager compact extraction (parse → keep `SubTickEvent` structs → discard protobuf): pays the
  extraction cost on every parse even when unused, and moves work into the hot path. Rejected.
- Touching the entity-side retained digest (separate ~118 MiB item, architectural).

## As built (hold raw bytes, generic `DeferredMessage`)

Shipped `DeferredMessage : IMessage` (not usercmds-specific) holding the raw wire bytes + a lazy
materialized cache. The four `IMessage` members delegate to `Materialize()`; the win is that the hot
path only does `is CConcrete` type tests, which never invoke a member and see a deferred payload as
not-the-concrete-type. Contract change to `NetMessage`/`DemoFrame` was not needed and was
deliberately avoided — see the design discussion in `DeferredMessage.cs`.

Measured (279 MB demo): ParsedDemo retained **919.3 → 704.6 MiB (−214.7, −23%)**, plus ~243 MiB
of parse-time churn deferred on every headless load. (Retained drop is less than the ~270 MiB the
proposal projected because holding raw bytes keeps 258 MiB of wire data + ~1.37M per-message
`byte[]`/wrapper headers — the price of the lower-risk variant vs zero-retention reconstruction.)

Correctness: `SubTickExtractor` output byte-identical eager-vs-deferred (deterministic FNV over
the canonical event set, 3 demos — note `System.HashCode` is per-process randomized and unusable for
this). AnalysisBench byte-identical across all 5 demos. Parser 108/108, analysis 967/967, Desktop +
Browser clean. One predicted test seam fixed: `InnerMessageAlignmentTests.RoundTrips` materializes a
deferred payload before its `.Equals`.

When to revisit the hold-nothing variant: if the remaining ~346 MiB retained (258 MiB raw + wrapper
overhead) becomes worth reclaiming, reconstructing bytes from the frame on demand via
`ExtractInnerMessageBytesAligned` retains ~0 — at the cost of a `SubTickExtractor` signature
change (needs the demo bytes) and byte-range-reconstruction risk. `DeferredMessage` is the seam that
would carry it.


## Closeout (2026-07-24)

The two remaining items from the v0.5.1 sweep were driven to a decision:

- **Retained digest bounding — not landed.** Attempted (interleaved streaming producer/consumer,
  byte-identical, deadlock-free) and reverted: eval +12% from consumer/producer core oversubscription,
  peak RSS unchanged (Server GC masks the ~118 MiB transient). See `results.md`. Not viable.
- **mmap App-side wiring (part 2) — deferred by decision.** The deterministic-close lifetime fix
  (v0.5.1: await the library fan-out before the reclaim collect) already delivered the lifecycle-
  control goal — a close now returns the whole ParsedDemo (~705 MiB: frames, entity state, and raw
  bytes) deterministically, not just the buffer. With that in place, wiring `MemoryMappedDemoSource`
  into the App shrank to a marginal off-heap refinement (raw bytes off the managed heap + a precise
  `Dispose()` point) whose cost — a wide byte-source refactor shipping an uncatchable crash mode — was
  judged out of proportion to the marginal gain. `MemoryMappedDemoSource` stays as the merged, opt-in,
  carefully reviewed primitive (part 1) for a future off-heap need; the App path stays on `byte[]`.
