# SDK#6 — DVN adapter findings: implementing `CS2OpenDev.Sdk.Entities.Abstractions` 0.1.1

Adapter implemented and conformance-proven 2026-08-13. These findings were
[posted on SDK#6](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/6#issuecomment-5286434787)
as the contract's **second implementation** — the milestone the 0.x freeze was waiting on.
Everything below is written against the 0.1.1 package source and test suite; where our runtime
and the contract meet awkwardly it is stated precisely, because at 0.x those are candidate
contract bugs, not things to work around silently.

The code lives in `src/Parser/Cs2DemoKit.Parser/Entities/SdkAbstractions/` (namespace
`Cs2DemoKit.Parser.Entities.SdkAbstractions`): `LensBoundReader` (`IEntityFieldReader`),
`TrackerEntityWorld` (`IEntityWorld`), `LensBindingBuilder` (runtime `EntityClassBinding`
manifests), and `LensOrdinalMap` (the translation table). The conformance ports live in
`src/Parser/Cs2DemoKit.Parser.Tests/SdkAbstractions/`.

## Headline

**The contract is implementable over our runtime with adapters only.** No change to
`EntityState`, no change to `EntityTracker`, no new internal test hooks — the adapters-only
claim from §8 of the entity-abstraction proposal (posted on SDK#6; the full document is in git
history) held up in full. Every upstream conformance assertion is satisfiable except the
storage-inherent divergences listed under "Contract frictions", of which exactly one is a
semantic difference a consumer could observe (Vector3/QAngle cross-reads), and none blocks us.

## What the adapter actually needed

### The translation table (`LensOrdinalMap`)

The promised shape was `contract ordinal → Lens canonical path → (lane, slot)`. The real table
needed one refinement the proposal had already flagged (§8.1's wire-vs-canonical note): our
`ClassShape.PathToSlot` is keyed by the **wire spelling of the demo being replayed** (our
`Fields` projection must reproduce wire-name keys for existing consumers), while contract
ordinals are keyed by the **canonical** Lens spelling. So each ordinal resolves through a
*candidate list* — the canonical path first, then every historical spelling the binding's
`Aliases` table targets at it — and the first candidate the shape knows wins:

- current demo → the canonical spelling hits `PathToSlot`;
- pre-rename demo → the alias spelling hits;
- unmapped path / array element / no shape bound at all → `SlotAddr.Fallback`, and reads probe
  the candidates against the entity's fallback dictionary at read time.

One consequence worth stating for other implementers: because our storage is wire-keyed, our
`TryReadByEnginePath` bridges **both** alias directions — the reference reader only needs
alias→canonical (its dictionary is canonical-keyed), while we also serve a canonical-spelling
query against pre-rename storage. Both directions are covered by tests. The contract text
doesn't say whether "canonical query over old-demo data" must resolve; the reference
implementation implies yes (its storage is always canonical-keyed, so the question can't
arise there). We resolve it. **Suggested upstream doc clarification, not a code change.**

Presence semantics came for free, and this deserves emphasis upstream: the contract's
absent-vs-received-default asymmetry — the suite's own "case that matters most" — is served
directly by our per-lane `_seen[]` bitvectors and the seen-gated `EntityState.TryGetValue`.
A runtime without per-field seen-tracking could not implement this contract honestly, which
is exactly the design pressure the doc comment on `IEntityFieldReader` intends.

### Type-conversion decisions (each deliberate, each tested)

| Contract member | Our storage reality | Decision |
|---|---|---|
| `TryReadBool` | bool wires stored as int 0/1 on the int lane (our documented wire-encoding convention) | int-lane fast path compares against zero; boxed path mirrors the reference reader's exact acceptance set (`bool`/`int`/`long`/`uint`/`ulong`) |
| `TryReadEntityHandle` | CHandle wires decode via our uint64 raw path → boxed `ulong` on the object lane | **width-fold, not decode**: `unchecked((uint))` from whatever integral width is boxed; no mask, no index/serial split, no sentinel interpretation. The 32-bit packed value survives intact (the high 32 bits of the boxed ulong are zero on this wire). An `int -1` folds to `0xFFFFFFFF` — the invalid sentinel crosses raw, as it should; note this is more permissive than the reference's checked `Convert.ChangeType`, which throws on negatives, but a handle reader that *cannot return the invalid sentinel* would be wrong, so we consider our behavior correct and the reference's an untested corner (see frictions, F3) |
| `TryReadUInt64` | wide ints (`m_steamID`, `m_nButtons`) land boxed `ulong` on the object lane under our honour-the-wire rule, even where genesis declared `int` lane | boxed read + the reference's conversion rule; no truncation path exists |
| `TryReadQAngle` | our angle decoders produce `Vector3(pitch, yaw, roll)` — we have no QAngle storage type | component reinterpretation `Vector3 → QAngle(X, Y, Z)`; order verified against our decoder (`FieldDecoderFactory` angle paths) |
| `TryReadInt32` / `TryReadSingle` | int/float lanes | lane fast path (no boxing); anything else routes through the boxed read + the reference's rule: exact type match, else `Convert.ChangeType` invariant-culture, cast/format/overflow ⇒ absent |
| lane-declared vs actual lane | genesis lane declarations can disagree with the wire (that's our §5.4 lane-drift rule) | the table binds against `ClassShape.PathToSlot` — the **runtime's** truth — never the Lens-declared `WireType` |

### `TrackerEntityWorld`

`Resolve<T>` is one line: `tracker.ResolveHandle<T>(unchecked((int)rawHandle))`. The tracker
already owns the sentinel checks (`0`, `0xFFFFFFFF`), the 14-bit index mask, the slot lookup
and the factory dispatch; the adapter re-implements none of it, per the design's "mask policy
is the runtime's business". Wrapper registration composes into the tracker's existing
`RegisterEntityFactory` registry — an SDK factory registered for a class **replaces** any
local generated `EntityBase` factory for that class, which is the intended migration lever
(class-by-class cutover), but consumers should know the registry holds one factory per class.
Until the upstream emitter's `EntityWrapperRegistry` ships, `RegisterWrapper(binding, factory)`
takes hand-written factories.

The translation table is cached per class keyed on the `ClassShape` reference (shapes are
immutable and shared across all states of a class within a tracker), so the ordinal join runs
once per class, not once per read or per wrapper.

### `LensBindingBuilder`

Builds manifests at runtime from the loaded `LensState`:

- `CanonicalPaths`: the class's active Lens fields sorted `StringComparer.Ordinal` — the
  "ordinal-sorted canonical Lens paths" numbering from the thread. Deterministic, dense,
  duplicate-free by construction.
- `Aliases`: our `AliasMap` stores identity entries (canonical → canonical) as a lookup
  convenience; the builder excludes them because a contract alias whose key is a canonical
  path would shadow the live field (`BindingConformance` rightly rejects it). Non-identity
  entries pass through untouched — if Lens data ever grows a dangling alias, we want
  conformance to shout, not the builder to launder it. Today's genesis state has **zero**
  genuine renames, so the alias machinery is proven by synthetic fixtures.
- `HandleOrdinals`: ordinals of `LensTransform.HandleIndex` fields.
- `NetName`: derived by the codegen convention (strip one leading `C`). The authoritative
  hand-maintained table (`SchemaLensSlotsGenerator.EngineToNetName`, 58 entries — this report
  originally said 60; corrected in the emitter-seed handoff's errata) lives in our codegen
  tool where the library can't see it; every entry follows the rule and a test pins
  representative pairs (`CCSPlayerPawn→CSPlayerPawn`, `CAK47→AK47`, `CC4→C4`).

`BindingConformance.ThrowIfInvalid` passes over the **entire** built set — all 58 covered Lens
classes — as a unit test, no demo required.

## Contract frictions (candidate 0.x items, stated precisely)

**F1 — `EntityClassBinding` has no schema-pinning members.** Our `LensState` carries
`CanonicalHash` (the canonical-form sha256 the runtime cross-checks at startup), and the
attribute docs in the package say the Lens hash "belongs on the generated registry". Fine —
but until the generated registry exists, a runtime binding hand-built or runtime-built
manifests has no contract-visible place to assert "this binding set was derived from Lens
state X". We populate nothing because there is nothing to populate. Not a defect at 0.1.1,
but worth deciding deliberately before 1.0: either the registry carries it (as planned) and
the contract stays clean, or a future consumer will bolt hashes into `NetName` strings. We
recommend the former, stated in the registry's spec when it lands.

**F2 — Vector3/QAngle discrimination cannot live in our storage.** Our decoder produces
`System.Numerics.Vector3` for *both* position and angle wires; the boxed value carries no
angle-ness. The reference reader discriminates by boxed CLR type (`QAngle` in the dictionary
⇒ `TryReadQAngle` succeeds, `TryReadVector3` fails). We cannot: on our reader,
`TryReadVector3` on an angle field returns the raw component triple and `TryReadQAngle` on a
position reinterprets it. **The upstream suite never asserts cross-shape refusal between
these two** (its `WrongShape_ReadsAsAbsent` uses a string), so we pass the suite as written —
but the reference implementation *would* refuse, and a consumer could observe the difference.
Our position: the contract should say the discrimination is the **emitter's** (each property
calls the accessor matching the field's schema type) and cross-shape reads are
implementation-defined. If upstream instead wants refusal to be normative, we would need
per-ordinal schema-type metadata at bind time — obtainable from Lens `TargetProperty`
conventions or a new binding member, i.e. a contract change either way. A documenting test
(`AngleAndVectorShareOneStorageShape_SoCrossReadsSucceed`) pins our actual behavior.

**F3 — negative boxed integers in `TryReadEntityHandle`.** The reference's `TryConvert`
routes handles through checked `Convert.ChangeType`, so a fixture writing `-1` (int) for a
handle field reads as *absent*; our width-fold reads it as `0xFFFFFFFF` — the invalid
sentinel, present and raw. The suite only tests a positive packed value, so both pass. We
think the fold is the correct semantics (the sentinel must be able to cross the seam;
"present with the invalid value" and "absent" are different facts), and suggest the reference
adopt an unchecked fold for the handle accessor specifically. One line upstream.

**F4 — received-null for scalar-typed fields is unrepresentable on our typed lanes.** The
contract's received-null case (`TryReadObject` true+null, typed readers false) is fully
representable in our object lane and fallback dictionary, and our tests port it faithfully in
both. But an int-lane slot cannot hold null — and no CS2 integer wire ever delivers one, so
the state *cannot arise* for a lane-mapped scalar in practice. We note this as an
observation, not a gap: the contract's asymmetry holds everywhere the state is constructible.
No suite assertion is unsatisfiable.

**F5 — `EngineClassName` override.** The reference reader takes an explicit
`engineClassName` constructor override to model a subclass read through a base binding. Our
reader has no override and needs none: the `EntityState` knows its actual class, so the
subclass case is inherent (the test port covers it). No friction, just a mapping note: the
override parameter is a reference-implementation affordance, not a contract requirement —
`IEntityFieldReader.EngineClassName` only requires reporting the entity's engine class.

## What maps cleanly

Everything else. Specifically: absent/received-zero asymmetry (seen bitvectors);
out-of-range ordinals degrade to absent (stale-wrapper tolerance); boxed `TryReadObject` with
true+null; the by-path escape hatch reaching uncurated fields (straight onto our seen-gated
`TryGetValue`); `IEntityWorld.Resolve<T>`'s null-collapse of sentinel/empty/stale/wrong-type
(the tracker already behaved this way); `EntityWrapper` composition — our tracker's
`Get<T>`/`Snapshot<T>`/`ResolveHandle<T>` being constrained `where T : class` (not
`EntityBase`) means SDK wrapper types flow through the existing generic surface with zero
signature changes, exactly as §8.2 of our proposal predicted; and `BindingConformance` over
runtime-built manifests. The all-fallback mode (tracker before shape binding) also satisfies
the full read contract through the fallback dictionary, which matters for us because it is
the compatibility mode the Lens migration still supports.

## Runtime hooks added

None. The adapter lives inside `Cs2DemoKit.Parser`, so the `internal` lane accessors
(`TryGetIntSlot` et al.) and `ClassShape` were already reachable; the test fabrication uses
the same internal machinery through the pre-existing `InternalsVisibleTo` grant. `EntityState`
and `EntityTracker` are untouched by this change.

## Test results

- New conformance suite: **43 tests, 43 pass** —
  `LensBoundReaderContractTests` 24 (ReadContractTests port + DVN-specific coverage),
  `LensBindingBuilderConformanceTests` 11 (BindingConformanceTests semantics over the full
  58-class built set), `SdkWrapperCompositionTests` 7 (WrapperCompositionTests port over a
  real tracker), `SdkAdapterDemoSmokeTests` 1 (integration: one real-demo replay; every
  canonical `CCSPlayerPawn` ordinal read agrees with the `Fields` projection by presence and
  value, plus typed health and controller-handle spot checks).
- Full Parser suite: **268 tests: 266 passed, 2 skipped, 0 failed** on the implementation
  branch (baseline 225 + the 43 new; both skips are pre-existing environment-dependent guards
  outside the new suite). Re-run after merge with the reference demo present, so the
  environment guards ran too: **268 / 268 passed, 0 skipped, 0 failed**. The known flake in
  `EmptyOptions_ParsesIdenticallyToTheOptionsLessOverload` (a warning-count race) did not
  reproduce in either run.

## Suggested upstream follow-ups (in priority order)

1. Decide F2 (Vector3/QAngle cross-read semantics) explicitly — one paragraph in the
   `IEntityFieldReader` remarks either blessing implementation-defined cross-reads or making
   refusal normative. We implement the former today.
2. F3: unchecked width-fold in the reference `TryReadEntityHandle`, so the invalid sentinel
   round-trips through fixtures written with `int` literals.
3. F1: when the generated `EntityWrapperRegistry` spec lands, give the Lens hash its promised
   home there — we'll wire `LensState.CanonicalHash` into it the day it exists.
