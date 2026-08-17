# Parser Architecture — Follow-up Backlog

Tasks and architectural explorations identified during the parser-architecture review
(2026-05). Each entry should remain until it lands, gets rejected with a noted reason,
or is split into more specific issues.

Cross-reference:
- `docs/parser-architecture.md` — the architecture doc this review is anchored on
- `KNOWN-AND-SUSPECTED-ISSUES.md` — load-bearing bug list. The entity-decode bit
  misalignment on POV/MM demos gated several items below when this review was written;
  it was since fixed and probe-verified 2026-08-12, so treat those items as unblocked.
- The GameEvents SDK gap asks were delivered upstream and adopted as
  `CS2OpenDev.Sdk.GameEvents` 4.0.1; that doc was retired 2026-08-16 (git history).
  The demofile-net replacement-feasibility study is likewise in git history.

---

## Surface unknown message IDs

**Status:** landed
**Effort:** S (under a day)
**Blockers:** none

**Resolution:** `OnUnknownMessageType` event added to `DemoParser`, fired from
the `_ => null` arm of `ParseNetMessage` via a `HandleUnknown` helper. Threading
contract documented on both `OnUnknownMessageType` and (newly) `ParseFailed`:
raised from Pass 2 parallel parse threads, handlers MUST be thread-safe. New
test `ParseDemo_OnUnknownMessageType_FiresAndDoesNotThrow` pins the contract.
91/91 parser tests pass.

`DemoParser.ParseNetMessage` (at `src/Parser/DemoViewer.NET.Parser/DemoParser.cs:740`)
ends with `_ => null` for unmapped type IDs. The caller at `:722` does
`if (msg is null) continue;` — silently skipping the message. `ParseFailed` is only
raised for parser exceptions, not for unmapped type IDs, so a CS2 protocol addition
that we haven't wired up disappears with no signal.

**Plan:**
- Add a public event `OnUnknownMessageType(int typeId, string typeName)` to
  `DemoParser` (matching the existing `ParseFailed` pattern — event-based, no
  configuration flag).
- Fire it from the `_ => null` arm of `ParseNetMessage` before returning.
- No throwing default; consumers decide what to do (log, ignore, fail-fast).
- Add a test that exercises the new event by feeding a frame with an unknown type ID.

**Why event over `WarnOnUnknownMessage` / `FailOnUnknownMessage` flag:** matches
the parser's existing convention (events for diagnostic surfaces; no global flags
on the static `DemoParser`). Easier to compose — a CLI tool can subscribe and
exit non-zero; a UI can collect into a panel; tests can assert specific IDs.

---

## Move entity-tracking to its own project

**Status:** landed
**Effort:** M (1-3 days)
**Blockers:** none structural; coordinated with the decode-misalignment work to avoid
merge churn

**Resolution:** Created `src/Parser/DemoViewer.NET.Parser.EntityTracking/`
containing the 13 moved files (EntityTracker, EntityState, EntitySet,
EntityUpdateInfo, FieldDecoder, FieldDecoderFactory, FieldEncodingInfo,
FieldPath, FieldPathEncoding, FieldPathEncodingOp, FieldPathReader,
HuffmanNode, NodePriority) under new namespace `DemoViewer.NET.Parser.EntityTracking`.
RuntimeSchema/Serializer/Field STAY in parser at namespace
`DemoViewer.NET.Parser.Entities`. `InternalsVisibleTo("DemoViewer.NET.Parser.EntityTracking")`
added to parser's ParsedDemo.cs so the moved EntityTracker can still construct
RuntimeField via its internal ctor. New project has `InternalsVisibleTo("DemoViewer.NET.Parser.Tests")`
so FieldPathTests can still reach the internal FieldPath struct. Seven
downstream csprojs gained the new ProjectReference (Analysis.Abstractions,
App, Parser.Tests, AnalysisBench, EntityDecodeProbe, EntityFieldDiff,
Parser.Entities project). 20 consumer .cs files gained
`using DemoViewer.NET.Parser.EntityTracking;` alongside their existing
`using DemoViewer.NET.Parser.Entities;` (the parser's RuntimeSchema namespace).
Solution build clean, 91/91 parser tests pass.

**Boundary principle (from review):** the parser owns "read raw input bytes and
convert to convenient typed shapes." Anything that REPLAYS those types to
reconstruct game state is downstream of that boundary.

**Stays in `DemoViewer.NET.Parser`:**
- `DemoParser`, `DemoFrame`, `NetMessage`, `GameEventMessage`, `ParsedDemo`
- `BitBuffer`, `LEB128Utils`, `FrameHeader`, `ProtoScanner`
- `StringTableProcessor`
- `Entities/RuntimeSchema.cs`, `Entities/RuntimeSerializer.cs`,
  `Entities/RuntimeField.cs` (wire-format schema interpretation, not replay)

**Moves to new `DemoViewer.NET.Parser.EntityTracking` (or similar):**
- `EntityTracker.cs`, `EntitySet.cs`, `EntityState.cs`, `EntityUpdateInfo.cs`
- `FieldDecoder.cs`, `FieldDecoderFactory.cs`, `FieldEncodingInfo.cs`
- `FieldPath.cs`, `FieldPathEncoding.cs`, `FieldPathEncodingOp.cs`,
  `FieldPathReader.cs`
- `HuffmanNode.cs`, `NodePriority.cs`

**Consumer updates:**
- `tools/EntityDecodeProbe`, `tools/DemoViewer.NET.EntityFieldDiff` —
  add project reference to the new entity-tracking project.
- `src/Analysis/DemoViewer.NET.Analysis.Abstractions/EntityStateLayer.cs` —
  same.
- `src/App/DemoViewer.NET` — same (`PlaybackViewModel`, `MainViewModel`).
- `src/Parser/DemoViewer.NET.Parser.Tests` — split entity-tracking tests out
  to a `DemoViewer.NET.Parser.EntityTracking.Tests` project.

**Naming options:** `DemoViewer.NET.Parser.EntityTracking`,
`DemoViewer.NET.EntityState`, `DemoViewer.NET.Entities`. The first preserves
"this is parser-adjacent" framing; the third is shortest. Pick before starting.

---

## DEM_FullPacket as authoritative-state corrective

**Status:** reframed — exploration found the original hypothesis is wrong on
two counts; rescoped to "decoder-quality diagnostic" rather than a fix for the
POV-demo decode misalignment
**Effort:** S (half-day for non-mutating diagnostic on Furia; L if a corrective
phase is ever wired)
**Blockers:** none for the rescoped diagnostic (Furia is the test bed; the
misalignment doesn't bite there). The *original* corrective hypothesis was
blocked by the misalignment, and the exploration found it is unlikely to help
with it anyway — see Exploration findings below.

**Premise:** the server emits `DEM_FullPacket` every ~3840 ticks (60s) carrying
a complete `CDemoStringTables` snapshot plus a `CDemoPacket` with full entity
state. This snapshot represents the server's authoritative view at that tick.
Today we skip FullPacket entity data during sequential playback (matching dfn's
behavior), on the assumption it's redundant with accumulated regular-packet state.

**Hypothesis:** even though FullPacket entity data is computed by the SAME server
that emits the deltas, applying it gives us a corrective signal. If our decoder
has drifted (missed an update, mis-typed a field), the snapshot diff would
surface it.

**Two-phase plan:**

1. **Diagnostic pass (always-on instrumentation):**
   - When a `DEM_FullPacket` arrives during sequential playback, decode its
     entity state into a TEMPORARY `EntitySet`.
   - Diff against `CurrentEntities`. Log `(entityIndex, fieldName, oursValue,
     theirsValue)` triples to a sink. Do NOT overwrite.
   - Surfaces drift without changing behavior — safe to land first.

2. **Corrective pass (opt-in):**
   - Same as above but ALSO patch `CurrentEntities` for fields where they differ.
   - Gate behind a flag (env var or `EntityTracker.UseFullPacketCorrection`).
   - Run benchmark suite before/after — measure cost (profile per-phase with `--profile`; see
     [`profiling.md`](profiling.md)) and any accuracy change.

**Risks to validate during exploration:**
- If both decode paths share a bug, diff shows no drift (false negative). Mitigate
  by adding a ground-truth comparison with demofile-net for working demos (Furia).
- Performance: FullPacket entity data is large. Decoding adds work proportional to
  active entity count × FullPacket frequency.
- Existing tests may rely on FullPacket being skipped (e.g., entity-count
  assertions); rerun all tests and adjust expected values intentionally.
- An earlier experiment (processing FullPacket's PacketEntities) made the
  delta-on-unknown count go *up* (87k → 94k). Until the misalignment's root
  cause is understood, this exploration risks the same outcome.

**Exploration findings (2026-05-26):**

A forensic pass found the original hypothesis is wrong on two counts:

1. **Why the prior experiment regressed (87k → 94k).** The cause is documented
   in code at
   [`EntityTracker.cs:577-581`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs):
   the prior attempt processed FullPacket's `CSVCMsg_PacketEntities` exactly
   like a delta packet, which re-ran ENTERPVS for entities that already had
   class identity. Re-ENTERPVS rewrote `_classIdToName` for known entities,
   and subsequent deltas then decoded against the wrong schema — generating
   *more* delta-on-unknown events downstream. Phase 1 here (non-mutating
   diff against a temp `EntitySet`) structurally avoids that failure mode.
   The risk note in the entry above conflates "non-mutating diagnostic"
   with "mutating re-processing"; only the latter caused the 87k → 94k
   regression.

2. **FullPacket cannot fix the POV-demo misalignment.** That failure mode is
   "delta references an entity the recording client never received as
   ENTERPVS." `DEM_FullPacket` in a POV demo is the *recording client's* full
   view at the checkpoint tick, not the server's authoritative view —
   entities outside the client's PVS at the FullPacket tick are missing from
   FullPacket too. So FullPacket carries no new information about the
   entities causing the failure. The right path remains finding a
   slot→class map from `CDemoFileHeader` / `CDemoSpawnGroups` /
   `CDemoStringTables` / `CSVCMsg_HltvStatus` at recording start.

**Rescoped value:** the non-mutating diagnostic phase still has standalone
value — it gives us a decoder-quality signal on demos where state *does*
fully build (Furia HLTV). Today our "decoder works" criterion is "didn't
throw"; FullPacket diff would surface silent drift (mis-decoded values that
don't fault). That's a different goal than fixing the POV-demo case, and it's
unblocked.

**Rescoped plan:**
- Implement phase 1 (non-mutating temp-EntitySet decode + diff + log) on
  a known-good demo (Furia HLTV).
- Treat output as a decoder-quality metric, not as a correction input.
- Decide based on what the diff reveals whether phase 2 (corrective) is
  worth the cost. If Furia shows ~0 drift, phase 2 adds no value; if it
  shows non-trivial drift on real fields, phase 2 becomes meaningful.

---

## Quantify the cost of decoding entity_data during parse

**Status:** scheduled — exploration
**Effort:** S (half-day to benchmark)
**Blockers:** was gated by the POV-demo decode misalignment (an always-on
decode would have crashed on every bench MM demo); unblocked since the fix

**Premise:** the parse pipeline today treats `svc_PacketEntities.entity_data`
as opaque bytes (`src/Parser/DemoViewer.NET.Parser/DemoParser.cs:20` —
"No entity state reconstruction is performed"). The opt-in `EntityTracker`
layer does that work separately. The cost rationale was: most consumers don't
need entity state, and entity decode is inherently sequential so it would erase
Pass 2's parallelism.

**What's worth quantifying:** the DECODE-into-partials step (parse the bitstream
into structured field-path + value records) is potentially parallel — each
PacketEntities message is independent up to that point. The STATE accumulation
step is the part that's sequential. Splitting those:

- **Stage A (parallel, Pass 2-compatible):** decode `entity_data` bits into a
  list of `(entityIndex, FieldPath, decodedValue)` triples per packet. No shared
  state.
- **Stage B (sequential):** apply those triples in order to build cumulative
  `CurrentEntities`. This is what `EntityTracker.Replay` does today.

If Stage A can be parallelized cheaply, Pass 2 produces frame-attached
"per-packet field-update lists" alongside the typed protos, and Stage B becomes
the only sequential cost.

**Benchmark plan:**
- Branch off current main. Implement Stage A in Pass 2 for one demo (Furia,
  which decodes cleanly). Measure total Pass 2 time before/after.
- Compare against: today's parse (no entity decode), today's parse + offline
  `EntityTracker.Replay`. Want to see the parallel-versus-sequential gain.
- Decide based on numbers whether to:
  - (a) Always decode in Pass 2, drop Stage B unless asked for (free).
  - (b) Make Stage A opt-in (`DemoParser.Parse(bytes, options: ParseOptions.DecodeEntityData)`).
  - (c) Leave as today (if cost exceeds value).

**Gating:** originally waited on the decode-misalignment root cause — any
always-on entity decode crashed on every bench MM demo. With that fixed, a
valid demo is guaranteed to decode and the benchmark is meaningful.

---

## `DemoViewer.NET.Parser.DownstreamUtilities` extraction

**Status:** landed
**Effort:** S (half-day)
**Blockers:** none

**Resolution:** New static class `DemoViewer.NET.Parser.DownstreamUtilities`
in `src/Parser/DemoViewer.NET.Parser/DownstreamUtilities.cs` holds the moved
APIs: `GetDecompressedPayload`, `ExtractInnerMessageBytes` (with private
`ExtractBitBufferMessages` helper), `Scan`, `TryGetPayloadRange`,
`TryReadFixed32Value`, `TryReadFixed64Value`, `TryReadVarintValue`, and the
`FieldSpan` record. Source `ProtoScanner.cs` was deleted entirely (its whole
class graduated to the new home). `DemoParser.FindBytesField` had to be
relaxed from `private` to `internal` so `DownstreamUtilities.ExtractInnerMessageBytes`
can keep calling it — both live in the same assembly. Unlike the core parser
files, `DownstreamUtilities.cs` is deliberately open to casual additions.
Consumer rewrites: `PayloadNodeBuilder.cs` (App),
`MainViewModel.cs` (App), `DemoParserTests.cs` (Parser.Tests). Stale comments
in `LEB128Utils.cs` and `LEB128UtilsTests.cs` updated to point at the new
home. Solution build clean, 91/91 parser tests pass.

**Premise:** Several APIs on `DemoParser` aren't used internally by the pipeline
and only exist for downstream consumers (UI, probes). They've accumulated in the
parser static class, which is deliberately kept minimal and stable. Mixing
"load-bearing internal" with "convenience API for consumers" makes that
boundary unclear.

**Methods/types to move:**
- `DemoParser.GetDecompressedPayload(DemoFrame)`
- `DemoParser.ExtractInnerMessageBytes(DemoFrame, byte[])`
- `ProtoScanner.Scan(byte[])` (deliberate single-level scanner used by the hex view)
- `FieldSpan` (output type of `ProtoScanner.Scan`)
- Any future helpers for byte-range identification / hex-view support

**New shape:**
- New static class `DemoViewer.NET.Parser.DownstreamUtilities` in a new file
  `src/Parser/DemoViewer.NET.Parser/DownstreamUtilities.cs`
- Document the contract at the file header: "Stable convenience API for consumers
  that need to extract / display the parser's intermediate bytes. Not used
  internally by the parse pipeline. Safe to add to; safe to evolve in minor
  versions."

**Consumer updates:** find every call site to the moved methods (parser-internal
or external), update to new namespace/class. Most call sites are in the app and
probes.

---

## Schema-driven typed entity records

**Status:** superseded — delivered a different way. The typed-static-entity-access goal shipped via
the project's **own** Schema Lens registry emit (`Entities/Generated/SchemaLens.Generated.cs`; the
wrapper emitters were retired in the SDK cutover, `.../SchemaLens/`; see `parser-architecture.md`
§5b and `docs/entity-stack.md`), which routed around the CS2OpenDev-SDK GPL-3.0 licence blocker by
generating our own wrappers instead of depending on the SDK. The multi-party SDK collaboration
described below is no longer on the critical path. _(Original entry preserved for context.)_
**Effort:** L (weeks); multi-party with CS2OpenDev maintainers
**Blockers:** CS2OpenDev-SDK licence (GPL-3.0 → permissive); design
  collaboration on the API shape

**Background:** the parser bridges between a runtime-defined schema (per-demo
`CSVCMsg_FlattenedSerializer`) and the downstream desire for static-typed access
with IDE autocomplete. Today, downstream consumers reach entity state through
`EntityState.Fields[string]` — dictionary access, no type safety, no
discoverability.

**Architectural direction:** use the CS2OpenDev.SDK entity classes (which already
exist as a compile-time codegen of the CS2 schema) as a **baseline**, with two
adaptations:

1. **Nullable properties.** A property represents "this CS2 version's schema MAY
   have shipped this field; the demo we're parsing has it iff non-null."
   Today's SDK classes don't model this; they'd need to be updated.

2. **String-name fallback accessor.** For fields newer than the SDK's compile-
   time snapshot, expose a `IDictionary<string, object?> UnknownFields` (or
   `T? Get<T>(string fieldName)`). This handles the case where Valve ships a new
   `m_SerializePoseRecipeAG2Dynamic` field that the SDK hasn't yet codegen'd.

**Pre-requisites:**

- **CS2OpenDev licence:** GPL-3.0 makes adoption a non-starter for DVN.
  Must move to MIT/Apache-2.0 first. (The licence ask was later resolved
  upstream — both repos went MIT — and the SDK packages were adopted.)
- **CS2OpenDev API shape collaboration:**
  - Nullable property generation in codegen
  - `UnknownFields` / typed-fallback accessor on each generated class
  - Documented forward-compat guarantee (adding fields is non-breaking)
  - Documented backward-compat behavior (old demos missing newer fields render
    those as `null`, not as exceptions)

**DVN-side work (after CS2OpenDev cooperation):**

- Replace `EntityState.Fields` consumers with typed accessors against SDK
  classes.
- Build a runtime adapter that maps `RuntimeSchema` field reads into either
  typed SDK property setters or the `UnknownFields` dictionary slot, depending
  on whether the field exists in the baseline.
- Migrate existing analysis edges (`HurtTeamEnrichmentEdge` et al) to use the
  typed surface.

**Open question:** does this pattern need a codegen pass on the DVN side that
generates the adapter, or can it be runtime-reflection-based? Reflection is
slower but simpler; codegen is harder but matches the rest of the pipeline's
performance posture.

---

## Closure: questions answered, no task

These questions from the architecture review were resolved without follow-up
work needed; recording the answers here so they aren't re-raised:

- **Is the entity-state layer "decode once, build from partials as needed"?**
  Yes. `EntityTracker.Replay` is exactly that, just labeled opt-in. No change
  needed; just documenting that the intuition matches the implementation.

- **Would always parsing entity_data improve enrichment?**
  In principle yes; was blocked by the POV-demo decode misalignment in
  practice. The work is covered by the cost-quantification entry above.

- **Pass 2 vs Pass 3 for GameEventMessage conversion.**
  Pass 3 is required because (a) `CMsgSource1LegacyGameEventList` ships in an
  early signon frame and the decoder needs it before any raw event can be
  decoded; (b) `CDemoFileHeader.ServerStartTick` is needed for synthetic
  timestamp fields. Pass 2 is parallel-per-frame with no ordering guarantee. A
  pre-scan before fan-out is possible but doesn't remove Pass 3 (which handles
  string tables, schema extraction, file info, the events index). Not worth
  the complexity unless Pass 3 becomes a dominant cost — it isn't today.

- **Should ProtoScanner recurse into nested messages?**
  No. It's deliberately single-level for the hex view's per-field byte-range
  highlighting. Caller re-invokes on sub-message bytes when the user expands a
  TreeView row. The move to `DownstreamUtilities` is covered by the extraction
  entry above.

- **7-slot FieldPath cap.**
  Valve protocol convention, not our limitation. demofile-net's `FieldPath.cs`
  has the same `_path0`..`_path6` layout. CS2 entity schemas don't nest deeper.
  Path-cap exceptions on POV demos are downstream of misaligned bit-stream
  decoding, not a cap-too-low issue.

- **Can FieldDecoder know the next type proactively?**
  Yes, and it does — decoder closures are created once per field at descriptor
  build time, keyed off `RuntimeField.TypeName`. The path-op stream tells us
  *which* field to read; we already have a typed decoder for that field. The
  misalignment problem is not "we don't know the type" — it's "we don't have
  a class identity for delta-on-unknown slots, so we don't have descriptors at
  all for those slots."

---

## Lazy / reference-gated per-player entity capture

**Status:** scheduled — perf refinement
**Effort:** M (1–2 days incl. re-measurement)
**Blockers:** none
**Layer:** Analysis (`EntityChangeScanner`, `ExpressionCompiler`) + the
  `IPerPlayerEntityValueProvider` contract — not parser-core, but tracked here
  with the rest of the entity-tracking work.

**Premise:** `EntityChangeScanner.CapturePreFrameSnapshot`
(`src/Analysis/DemoViewer.NET.Analysis/EntityChangeScanner.cs:309`) walks EVERY
registered per-player provider into a `(provider, slot) → value` dict on EVERY
frame — one `PawnLookup.ForEachLivePawn` pass per provider — before the layer
advances. Today's per-player providers: `PawnHealthProvider`,
`ActiveWeaponProvider`, `PawnEquipmentValueProvider`, `PawnArmorProvider`.

The two economy providers (equipment value, armor) feed stats sampled **only at
`round_freeze_end` (~24×/match)** but are captured **~90k× (every frame)**.
Measured (`--profile`, 90,603-frame demo): adding them moved the
pre-frame-snapshot sub-phase from **6.4 s → 13.5 s/demo (+7.1 s)**; allocation
stayed ~200 MiB (the earlier `.Fields`-elimination win holds — this is wasted
*walk repetition*, not allocation). This is the cost the per-frame snapshot
imposes on rarely-sampled fields.

**Why the eager snapshot exists (keep it for HP):** the pre-frame snapshot's
previous-frame semantics are REQUIRED for HP-based damage-capping —
`player.entity.pawn.health` feeds the `AvgHP→Dmg` rule at `player_hurt`, where
the HP decrement arrives in the SAME packet as the event, so reading HP *at* the
event tick yields post-damage HP (`EntityChangeScanner.cs:39-45`). Equipment and
armor have **no co-located mutation** at `round_freeze_end` (nobody buys at that
tick), so reading them at the event tick is correct and ~3700× cheaper.

**Plan — per-provider capture policy:**
1. Add a `CapturePolicy { Eager, Lazy }` (or `bool RequiresPreFrameSnapshot`) to
   `IPerPlayerEntityValueProvider`.
2. `CapturePreFrameSnapshot` skips `Lazy` providers — they never enter the
   per-frame walk.
3. `ExpressionCompiler.ResolvePlayerEntity` (`Building/ExpressionCompiler.cs:302`,
   the `GetPreFrameValue` emission at `:320`) emits a `GetCurrentValue(provider,
   slot)` call — a new thin `EntityChangeScanner` accessor that calls
   `provider.Read(Layer, slot)` against the layer's CURRENT tick — for `Lazy`
   providers; keeps `GetPreFrameValue` for `Eager`.
4. Mark equipment/armor `Lazy`, health `Eager`. The lazy `Read(layer, slot)` path
   already exists on every provider (e.g. `PawnEquipmentValueProvider.cs:44`) —
   it's just unused today because the compiler unconditionally emits
   `GetPreFrameValue`.

**Second lever — reference-gating (lower priority):** even `Eager` providers are
captured whether or not any *active* rule references them. The scanner already
lazy-activates as a whole (null `BuildResult.EntityScanner` ⇒ zero work), but not
per-provider. Capturing only providers a compiled rule actually references would
make the registry free to extend. Do this only if the policy split (above)
proves insufficient.

**Related minor cleanup (fold in):** `EntityChangeScanner.ResolveThrowerSlot`
(`:259`, `:265`) still reads via `.Fields.TryGetValue` instead of the
allocation-free indexer. It's cold-path (once per molotov throw) so not a perf
concern, but it's the last un-swept site from the `.Fields` elimination —
finish the sweep for consistency.

**Verify:** equipment/armor stats **byte-identical** pre/post (reading a
non-mutating field at `round_freeze_end` ≡ previous-frame value); `AvgHP→Dmg`
and all HP-fed stats untouched (health stays `Eager`); re-measure the
pre-frame-snapshot phase with `--profile` (expect a return toward
~6.4 s, recovering the +7.1 s); full suite green (Parser/Entities/Analysis).

---

## Entity-derived movement / positioning stats (blocked on velocity networking)

**Status:** blocked — needs entity-layer investigation
**Effort:** S (velocity probe) + L (distance stat, if position proves networked)
**Blockers:** velocity/position networking in GOTV is unconfirmed; no oracle for
  cumulative distance.
**Layer:** Analysis providers + entity-tracking decode.

**Premise:** a speed/movement suite (`AvgSpeedWhileFiring`, horizontal velocity
magnitude sampled per `weapon_fire`) was prototyped on `feature/entity-rules-
profiling` and **removed before merge**: `m_vecVelocity` read **uniformly 0 for
every player across hundreds of shots** — physically impossible if velocity were
live (run-and-gunners would show 20–80 u/s). The server pawn's `Velocity`
object-slot decodes to null; velocity appears to be **client-predicted, not
server-transmitted** in GOTV. `m_vecOrigin` (position) is untested but suspect
for the same reason.

**What's worth doing:**
1. **Velocity/position probe (S).** Determine whether velocity or position is
   recoverable at all in GOTV demos: check alternative fields
   (`m_vecAbsVelocity`, `m_vecBaseVelocity`), the controller/observer entities,
   and the `CUserCmd` sub-frame stream; verify any candidate against
   demofile-net ground truth on a known-moving frame (the oracle pattern already
   used elsewhere). Conclusion gates everything else here.
2. **Cumulative distance-traveled (L), only if position IS networked.** Needs a
   first-of-its-kind **stateful per-frame provider** (all current providers are
   stateless point-reads) with a teleport/respawn guard, and has **no Leetify
   oracle** to validate against → a deliberate refine-phase task, wrong fit for a
   trust-baseline quick-add. Do not build until (1) confirms position is live.

**Why not just retry:** burning eval time on per-frame position capture only pays
off if the field is real. (1) is cheap and decides it; don't skip to (2).

---

## Updating this doc

When a task lands or is rejected:
1. Update the `Status:` line. Preserve the original entry — don't delete.
2. Add a `Resolution:` block at the bottom of the entry with a one-line summary.
3. Cross-reference in `KNOWN-AND-SUSPECTED-ISSUES.md` if the resolution touches
   a known bug.

When a new architectural follow-up arises:
- Append at the bottom. Match the format of existing entries.
- Include `Blockers:` honestly. Stale blockers are how this doc rots.
