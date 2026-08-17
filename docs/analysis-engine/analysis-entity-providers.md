# Entity-Value Providers

**Audience:** engine developers adding entity-state signals to the analysis engine.
**Status:** as-built, 2026-07-08.

Entity-value providers expose networked entity fields (decoded by `EntityTracker` /
`EntityStateLayer`) to the rule engine as named context values — the bridge between
`CSVCMsg_PacketEntities` state and declarative YAML rules. There are two provider families with
fundamentally different consumer patterns, plus one orchestrator (`EntityChangeScanner`) that
owns all per-frame entity work for an evaluation.

Key files:

| File | Role |
|---|---|
| `src/Analysis/DemoViewer.NET.Analysis/Plugins/IEntityValueProvider.cs` | Singleton (push) provider contract + `ChangeDirection` |
| `src/Analysis/DemoViewer.NET.Analysis/Plugins/IPerPlayerEntityValueProvider.cs` | Per-player (pull) provider contract |
| `src/Analysis/DemoViewer.NET.Analysis/Plugins/EntityValueProviderRegistry.cs` | Singleton registry + `CreateDefault()` |
| `src/Analysis/DemoViewer.NET.Analysis/Plugins/PerPlayerEntityValueProviderRegistry.cs` | Per-player registry + `CreateDefault()` |
| `src/Analysis/DemoViewer.NET.Analysis/EntityChangeScanner.cs` | Per-evaluator orchestrator (polling, snapshots, synthesis) |
| `src/Analysis/DemoViewer.NET.Analysis/Plugins/PawnLookup.cs` | Shared slot↔pawn resolution + handle decoding |
| `src/Analysis/DemoViewer.NET.Analysis/Plugins/Markers/` | Marker types for synthesized change events |

---

## The two provider families

### `IEntityValueProvider` — singleton entity, push model

Targets one-of-a-kind entities (e.g. `CCSGameRulesProxy`). The scanner **polls** the provider
once per frame, compares against the previous value, and pushes two outputs:

1. **A value node** keyed by `ContextName` (e.g. `entity.game.freeze_period`), readable from any
   rule expression through the `ExpressionCompiler` `entity.*` resolution path. Written on
   *every* observed change.
2. **A synthesized change event** — `EntityValueChangedEvent<TMarker>` carrying `Tick`,
   `OldValue`, `NewValue` — dispatched only when the transition matches `EmitOn`
   (`RisingOnly` / `FallingOnly` / `Both`). Rules subscribe with `on: <ContextName>`.

**Marker types:** each provider declares a unique empty marker class (`MarkerType`, e.g.
`CCSGameRulesFreezePeriodMarker` in `Plugins/Markers/`). The closed generic
`typeof(EntityValueChangedEvent<TMarker>)` is the dispatch key, so each (entity class, field)
pair gets its own slot in the evaluator's dispatch index — no shared dispatch list.

Contract surface: `ContextName`, `EntityClass`, `FieldName` (dotted wire path), `ValueType`,
`DefaultValue` (seeds the node pre-spawn), `EmitOn`, `MarkerType`, `Read(EntityStateLayer)`.
`Read` returns `null` when the entity doesn't exist yet; the scanner leaves cached state
untouched for null reads.

Shipped provider: **`FreezePeriodProvider`** — reads
`CCSGameRulesProxy.m_pGameRules.m_bFreezePeriod` as `entity.game.freeze_period`
(`RisingOnly`). It backs the `gameplay_phase` FreezeTime transition on HLTV demos where
`round_prestart` never fires; the falling edge is deliberately left to the
`round_freeze_end` event trigger to avoid a same-tick race.

### `IPerPlayerEntityValueProvider` — per-player entity, pull model

Targets per-player entities (typically `CCSPlayerPawn`), parameterised by player slot. **No
synthesized events** — consumers *read* the value at the moment of a player-scoped event
(`player_hurt`, `round_freeze_end`, …), addressing the right pawn via the event's slot fields.

The scanner maintains a **pre-frame snapshot**: at the start of each frame's advance, the
*previous* frame's per-(provider, slot) values are folded into a persistent snapshot
(`GetPreFrameValue`). This is deliberately one frame behind — entity state *at* an event's tick
has already been updated by the wire-co-located `PacketEntities`, so the pre-frame value is the
correct "state before the event" read. (`HurtTeamEnrichmentEdge`'s pre-hit HP and every
`player.entity.*` expression read rely on this timing.) Stale entries are retained for slots
absent in a frame; the snapshot is empty during frame 0.

Contract surface: `Name` (the YAML-visible identifier, e.g. `entity.pawn.health`),
`EntityClass`, `FieldName`, `ValueType`, and three read methods:

| Method | Used by |
|---|---|
| `Read(layer, slot)` | Ad-hoc reads at the layer's current tick (POST-event values — use the snapshot for pre-event) |
| `ReadForPawn(tracker, pawn)` | The per-pawn unit of work; the scanner walks the entity set **once** per frame and dispatches to every provider, so the set isn't re-swept per provider. **The emit gate is part of the contract:** return `null` to skip the slot (e.g. `PawnHealthProvider` treats hp ≤ 0 as absent), return a value — including a legitimate 0, as the equipment/armor providers do — to snapshot it. |
| `CaptureAllSlots(layer, emit)` | Batch capture for the breakpoint `EntityValueCache` pre-warm path |

**Parameterless-constructor requirement.** `EntityChangeScanner.PrecomputeParallelDigests`
(the load-perf parallel-decode work) decodes the whole demo's entity stream in parallel, and each worker gets its
**own provider instances** because providers cache mutable state (e.g. `FreezePeriodProvider`'s
cached entity index). Cloning is `Activator.CreateInstance(p.GetType())` — see
`EntityChangeScanner.CloneProvider` — so **every provider (both families) must have a
parameterless constructor**. If a future provider needs constructor state, add a clone hook
instead (noted in the code).

Shipped per-player providers (`PerPlayerEntityValueProviderRegistry.CreateDefault()`):

| Name | Reads | ValueType | Notes |
|---|---|---|---|
| `entity.pawn.health` | `CCSPlayerPawn.m_iHealth` (typed wrapper `CSPlayerPawn.Health`) | int | hp ≤ 0 → null (absent); feeds `HurtTeamEnrichmentEdge` pre-hit HP |
| `entity.pawn.active_weapon_class` | pawn `ActiveWeaponHandle` → weapon entity `ClassName` | string | two-hop handle resolution via `PawnLookup.ResolveHandle`; class name, not item def index |
| `entity.pawn.equipment_value` | `CCSPlayerPawn.m_unCurrentEquipmentValue` | int | 0 is a real observation (eco round) — always emitted |
| `entity.pawn.armor` | `CCSPlayerPawn.m_ArmorValue` | int | 0 is a real observation — always emitted |

A movement/speed provider was prototyped and removed: `m_vecVelocity` is not usably networked on
the server pawn in GOTV demos (firing speed read uniformly 0). Don't rebuild velocity stats on
this surface.

---

## Registration

Two parallel registries, both defaulted by the `DemoAnalysis` facade (`AnalysisOptions.EntityProviders`
/ `.PerPlayerEntityProviders` — passing `null` means "use the defaults", not "none"):

```csharp
// singletons
EntityValueProviderRegistry.CreateDefault()      // FreezePeriodProvider
// per-player
PerPlayerEntityValueProviderRegistry.CreateDefault()
// PawnHealthProvider, ActiveWeaponProvider, PawnEquipmentValueProvider, PawnArmorProvider
```

To add a provider: implement the interface, register it in the appropriate `CreateDefault()`
(or supply a custom registry via `AnalysisOptions`). Singletons key by `ContextName`; per-player
providers key by `Name`. Registries are separate types from `PluginRegistry` because the
lifecycles differ — plugins contribute graph structure at build time; providers read entity
state at evaluation time.

---

## Lazy activation

The scanner is expensive (it owns an `EntityStateLayer` and drives per-frame entity decode), so
`RuleChainBuilder.Build()` constructs it only when needed:

- **Singleton providers** activate individually: the builder substring-matches each registered
  `ContextName` against every rule's `on:`, `condition:`, `value:`, and parent `when:` fields
  (config + built-in contexts). Matched providers get a value node
  (`GenericBoolNode`/`GenericValueNode<T>`) registered in the node lookup and are staged for the
  scanner. Unreferenced providers contribute nothing. (Substring matching is a known coarseness;
  token-level matching is the planned fix.)
- **Per-player providers** activate the scanner **unconditionally when registered** — they are
  read by C# edges and compiled expressions rather than named in `on:` fields, so reference
  scanning doesn't apply. Lazy activation therefore degrades to "scanner skipped only when zero
  providers (singleton or per-player) are registered"; callers wanting full lazy semantics pass
  `null` for both registries when constructing `RuleChainBuilder` directly.
- The synthesized **`molotov_thrown`** event also forces scanner construction when referenced
  (it is produced by the scanner itself from `CMolotovProjectile` creation, attributed via the
  `m_hThrower` handle chain, deduped by entity index+serial).

When nothing activates, `BuildResult.EntityScanner` is `null` and the evaluator's per-frame
entity hook short-circuits — **zero per-frame cost**. A trigger `on:` a *registered but
unactivated* provider context builds no edge (inert by design, same graceful degradation as
`requires:`); an `entity.*` name with no registered provider at all is inert only when no
provider registry was supplied — otherwise unknown names are loud build errors.

---

## The EntityChangeScanner bridge

One scanner per evaluator (single-threaded; parallel chain evaluators each get their own layer +
scanner). Per frame, `AdvanceAndPoll(tick)` / `AdvanceAndPollAt(frameIndex, tick)`:

1. Seek the layer to the tick (or consume a **precomputed digest** — see below).
2. Build the frame's `EntityFrameDigest` via the shared `EntityDigestExtractor` (the only step
   that touches the entity set: one walk serves all per-player providers via `ReadForPawn`,
   plus singleton reads and molotov detection).
3. **Consume** sequentially: fold the *previous* digest into the pre-frame snapshot; run
   singleton change detection (write value nodes, synthesize `EntityChangeMessage`s per
   `EmitOn`); synthesize `molotov_thrown` events for newly-seen projectiles.
4. Return the synthesized `NetMessage` list; the evaluator dispatches them like wire messages.

`PrecomputeParallelDigests(frames)` moves step 1–2 for the whole
demo into an up-front parallel phase, chunked at `DEM_FullPacket` boundaries by
`ParallelDigestProducer`. Workers receive **cloned provider instances** (the parameterless-ctor
requirement above); the digests are proven element-wise identical to sequential ones
(`ParallelDigestEquivalenceTests`), so golden output is preserved.

---

## Cost model

Every registered per-player provider is captured into the pre-frame snapshot **every frame**,
whether or not any rule samples it that frame. Measured when the economy providers landed
(2026-06-08, pre-parallel-decode): **each eager per-player provider adds roughly 3.5 s per demo
eval**. Two mitigations exist today:

- The per-frame entity-set walk is shared — adding a provider adds one `ReadForPawn` call per
  live pawn per frame, not a second entity-set sweep.
- `PrecomputeParallelDigests` moves the capture into the parallel up-front decode, so the
  sequential eval loop only consumes digests.

Providers sampled rarely by rules (equipment value / armor are read only at `round_freeze_end`)
are the standing candidates for a lazy-read refinement — noted in
`PerPlayerEntityValueProviderRegistry.CreateDefault()`.

Singleton providers are cheap by comparison (one entity read per frame, cached entity index),
but the same principle applies: only referenced providers are polled at all.

---

## How rules consume providers

**Singleton — trigger on a change event** (`on:` = `ContextName`; Phase-1 edge support is
`activate`/`deactivate` on bool targets and `set` with a **literal** value):

```yaml
# BuiltinContexts gameplay_phase — entity-state backup for HLTV FreezeTime
- on: entity.game.freeze_period
  action: set
  value: '"FreezeTime"'
  condition: "entity.game.freeze_period == true"
```

**Singleton — read the value node** anywhere an expression runs
(`entity.game.freeze_period == true` above resolves via the `entity.*` path in
`ExpressionCompiler`).

**Per-player — expression reads** in per-player chains: `player.entity.<provider-name>`
compiles to a fixed-slot call into `EntityChangeScanner.GetPreFrameValue` (the slot is the
chain's compile-time constant — no runtime dispatch). A null snapshot degrades to the provider's
default (0 for value types, `""` for string). Referencing `player.entity.*` without a scanner /
per-player registry / slot bound is a **compile-time error**.

```yaml
# rules/player-stats.yaml — average HP while dealing enemy damage
- id: HealthWhileDamagingSum
  type: counter
  parents: [enrich.hurt.was_enemy_damage]
  triggers:
    - on: player_hurt
      condition: "event.AttackerSlot == player.slot"
      action: set
      value: "rule.value + player.entity.pawn.health"

# economy sampling at buy-settled time
- id: RoundsWithArmor
  type: counter
  parents: [context.player.alive]
  triggers:
    - on: round_freeze_end
      condition: "player.entity.pawn.armor > 0"
      action: increment
```

**Synthesized events** — `on: molotov_thrown` with `event.PlayerSlot`, exactly like a parsed
game event.

**Breakpoint substrate** (developer tooling, not YAML rules): edge/node breakpoint conditions may
read providers per-fire via `<SlotField>.entity.<provider>` (e.g.
`event.VictimSlot.entity.pawn.health`) and `player.entity.<provider>` against the selected
player, backed by `IEntityValueAt` + the host's `EntityValueCache` positioned at each fire's
pre-frame state.
