# DemoViewer.NET — Analysis Engine Design

**Status:** As-built (refreshed 2026-07-08, branch `feature/analysis-release-p0`).
For the user-facing authoring guide see [`RULES_AUTHORING.md`](../RULES_AUTHORING.md) (Rulesets v2); for the entity-state
surface see [`analysis-entity-providers.md`](./analysis-entity-providers.md); for source profiles
see [`Demo-Profiles.md`](../Demo-Profiles.md).

## Mental Model

The analysis engine is a **state propagation graph** evaluated incrementally as a demo is replayed
message by message. Rule chains are the primary abstraction — users declare what they want to
compute, and the engine builds and optimizes the underlying graph automatically.

The graph has these runtime elements:

| Element | Description |
|---|---|
| **Rule (StateNode)** | A named stateful element — boolean, typed value, counter, computed expression, or streak/tally node |
| **Trigger (StateEdge)** | A directed, conditional link that fires on specific events |
| **Auto-activate (ConjunctionNode/DisjunctionNode)** | A rule that activates based on parent conditions, not events |
| **Enrichment edge** | Built-in C# edge (`Edges/`) that classifies raw events into transient `enrich.*` nodes before rules read them |
| **Entity provider** | Reads networked entity fields into the graph (`entity.*` contexts, `player.entity.*` expression reads) |
| **Plugin** | Legacy code-level escape hatch (`type: plugin`); the default registry ships **empty** |

---

## Rules

A rule represents a single atomic fact about the current game state.

### Rule Types

| Type | Description | Example |
|---|---|---|
| **bool** | Binary active/inactive state | `alive`, `has_kast` |
| **counter** | Integer value (defaults to 0) | `kills`, `TotalEnemyDmg` |
| **value** | Typed value (`int`, `string`, `float`/`double`, `bool`) | `map_name`, `gameplay_phase` |
| **expression** | Computed formula over sibling rule values, recomputed at `$round_end` | `ADR`, `KD`, `HLTV` |
| **threshold_tally** | At `$round_end`, increments the first matching bucket's target counter from a source rule's value | `multi_kill_tally` → `Rounds2K…Rounds5K` |
| **windowed_streak** | Counts event streaks within a sliding tick window | `rapid_kill_sequences` (window 640 ticks, min 2) |
| **keyed_counter** | Per-key bucket dictionary (`key:` expression → `double` bucket); triggers `increment`/`add` into the fired key's bucket. Per-player chains only (v1); **excluded from snapshots** (`ISnapshotExcludedNode`) — sampled per-game by `KeyedStatsProjector` reading the live node post-eval (the per-weapon dimension mechanism) | `kills_by_weapon`, `damage_by_weapon` (`weapon-stats.yaml`) |
| **plugin** | Materialized by a registered `IAnalysisPlugin` | none shipped — registry is empty |

**Properties:**
- Activation is **idempotent** — activating an already-active bool is a no-op
- Rules are either **game-scoped** (one instance) or **per-player** (one per player slot, lazily materialized)
- **`reset: round`** rules restore their default value when the evaluator sees `round_freeze_end`
  (the concrete event — fires in both GOTV and HLTV demos; the reset is evaluator-level, see
  `StateGraphEvaluator.ResetRoundScopedNodes`)
- **End-of-round finalization** (expression recompute, threshold tallies, round-end enrichment,
  `survived`) binds to the **`$round_end` logical event**, which the active profile expands to its
  concrete events (GOTV: `round_officially_ended` → `cs_win_panel_match`; HLTV: `cs_pre_restart` →
  `cs_win_panel_match`) with first-wins-per-round guards on non-idempotent actions. There is **no**
  hardcoded round-end event and no synthetic end-of-demo event.
- **`requires:`** lists logical events the rule strictly needs; if the active profile does not
  bind one, the rule is silently skipped at build time (graceful degradation)

---

## Triggers

A trigger is a directed, conditional link that fires when a specific event occurs:

```
[Parent rules: all must be ON] --{ event + condition }--> [This rule]
                                                              action: activate | deactivate | increment | set
```

### Trigger `on:` sources

| Form | Resolves to | Example |
|---|---|---|
| Concrete game event | `EventRegistry` game-event map | `on: player_death` |
| Net message | `EventRegistry` net-message map | `on: CDemoFileHeader` |
| `$logical` alias | Active profile's binding, expanded at build time (see `Demo-Profiles.md`) | `on: $round_end` |
| Entity context | Synthesized `EntityValueChangedEvent<TMarker>` from a registered `IEntityValueProvider` | `on: entity.game.freeze_period` |
| Synthesized event | Scanner-emitted event (currently only `molotov_thrown`) | `on: molotov_thrown` |

An `on:` name matching none of these is a **build error** with a did-you-mean suggestion (edit
distance over all registered names) — never a silently-inert rule.

### Trigger Actions

| Action | Description |
|---|---|
| **activate** | Sets a bool rule to active |
| **deactivate** | Sets a bool rule to inactive |
| **increment** | Shorthand for `node.value + 1` on a counter |
| **set** | Sets a value via a compiled expression |

### Parents

Parents gate a rule's triggers — parents must be active for any trigger to fire:

| Mode | Description |
|---|---|
| **all** (default) | All parents must be active and meet their conditions (AND) |
| **any** | Any parent being active and meeting its condition is sufficient (OR) |

Known limitation: for a rule with **both** triggers and multiple parents, only the first parent is
used as the edge source. Slated for hardening.

### Auto-Activation

Rules with parents but **no triggers** auto-activate when their parent conditions are met. This
is how conjunction/disjunction logic is expressed:

```yaml
# Activates when kills >= 3 (conjunction)
- id: has_3k
  type: bool
  reset: round
  parents:
    - rule: kills
      when: "value >= 3"

# Activates when ANY parent is active (disjunction)
- id: has_kast
  type: bool
  reset: round
  parents:
    mode: any
    rules:
      - rule: kills
        when: "value > 0"
      - context.player.survived
```

---

## Rule Chains

A rule chain is a self-contained set of rules that answers a question about a demo. Chains are
the primary unit of encapsulation in the YAML API.

### Chain Satisfaction

A chain is satisfied when **all of its bool rules are simultaneously active** (the builder feeds
only `type: bool` rules into the hidden `_chain_{id}` conjunction).

### on_satisfied

When a chain transitions from unsatisfied to satisfied (rising edge), the `on_satisfied` action
fires — typically incrementing a counter:

```yaml
on_satisfied:
  increment: kast_rounds
```

### Isolation

Each chain's satisfaction logic is independent. Shared rules (structurally deduplicated) are
deterministic functions of events — no chain can modify another chain's private state.

---

## Enrichment & Transient Node Scoping

Enrichment edges (`src/Analysis/DemoViewer.NET.Analysis/Edges/`) are built-in C# edges that fire
on raw events and write classified facts into **transient nodes** (`enrich.*`). Transient nodes
are event-scoped: reset before the next dispatch of the same message type, excluded from snapshot
capture. Rules read them via parents (`parents: [enrich.kill.was_enemy_kill]`) or expressions
(`enrich.hurt.capped_damage`). The topological edge sort guarantees enrichment writes land before
rule edges read within the same dispatch slot.

The full `enrich.*` node inventory (24 nodes: kill/trade/flash, hurt/damage-capping,
blind, clutch, round-winner, bullet-classification) is defined in
`Building/BuiltinContexts.cs::CreateEnrichment` and surfaced through the generated
authoring catalog (`rules/catalog.json`; see [`RULES_AUTHORING.md`](../RULES_AUTHORING.md)).
Design details:
[`Analysis-Engine-Graph-Context-Design.md`](./Analysis-Engine-Graph-Context-Design.md).

Round-end enrichment (`RoundEndEnrichmentEdge`, `ClutchResolutionEnrichmentEdge`) is instantiated
**once per concrete event** in the active profile's `$round_end` binding, so it fires correctly
across GOTV, HLTV, and end-of-demo.

---

## Built-in Contexts

`BuiltinContexts.GenerateContextRules()` injects two built-in chains into every build —
`_builtin_game_context` (game-scoped) and `_builtin_player_context` (per-player). Rules reference
them by `context.*` alias (resolved in `RuleChainBuilder.ResolveContextId`) or directly by rule id:

| Context Path | Rule id | Type | Scope | Description |
|---|---|---|---|---|
| `context.match.live` | `match_live` | bool | game | `$match_start` → `$match_end` |
| `context.round.active` | `round_active` | bool | game | true during live round (`$round_freeze_end` → `$round_end`) |
| `context.round.number` | `round_number` | counter | game | increments on `$round_freeze_end` while `match_live` |
| `context.round.gameplay_phase` | `gameplay_phase` | string value | game | `WarmUp` / `PreMatch` / `FreezeTime` / `ActiveWithBuy` / `ActivePostBuy` / `PostRound` / `PreRound` / `Halftime` / `Intermission` / `PostMatch` state machine (entity-state backup for HLTV FreezeTime via `entity.game.freeze_period`) |
| `context.round.bomb_status` | `bomb_status` | string value | game | `NotInPlay` / `Carried` / `Dropped` / `Planting` / `Planted` / `Defusing` / `Defused` / `Detonated`; round-reset |
| `context.round.no_deaths` | `no_deaths_yet` | bool | game | true until first death each round |
| `context.match.regulation_status` | `regulation_status` | string value | game | `Regulation` → `Overtime` on `cs_match_end_restart` (untested on a real OT demo) |
| `context.match.half_state` | `half_state` | string value | game | `FirstHalf` → `SecondHalf` at halftime (announce-gated) |
| `context.match.map` | `map_name` | string value | game | from `CDemoFileHeader.MapName` |
| `context.player.alive` | `alive` | bool | per_player | `$round_freeze_end` → `$player_death` |
| `context.player.survived` | `survived` | bool | per_player | activates at `$round_end` while `alive` |
| `context.player.traded` | `traded` | bool | per_player | activates when the player's death was traded (enrichment-driven) |
| (internal) | `rounds_after_half_announce` | counter | game | halftime-detection gate (see BuiltinContexts.cs comments) |

`context.match.tick` was **removed** with the retired `current_game_tick` plugin (the alias could
only resolve to a nonexistent node). Referencing it is an unknown-parent build error; rules that
need tick values read them from event fields instead.

---

## Logical Events & Capability Gating

Trigger names prefixed with `$` are **logical events** resolved against the demo's
`DemoSourceProfile` at build time (zero runtime cost). Multi-event bindings (today only
`$round_end`) expand to one edge per concrete event, with a round-scoped `__seen_*` guard
suppressing duplicate non-idempotent fires. An unguarded `$logical` that the profile does not
bind is a build error; declaring the name in the rule's `requires:` list turns that into a
silent skip instead. Full treatment: [`Demo-Profiles.md`](../Demo-Profiles.md).

---

## Entity-Value Providers

Two provider families expose networked entity state to rules without any per-rule entity code:

- **`IEntityValueProvider`** (singleton entities, push model) — polled per frame by
  `EntityChangeScanner`; value mirrored into an `entity.<name>` graph node and change transitions
  synthesized as dispatchable events (`on: entity.game.freeze_period`).
- **`IPerPlayerEntityValueProvider`** (per-player entities, pull model) — captured into a
  pre-frame snapshot; read from expressions as `player.entity.<name>`
  (e.g. `player.entity.pawn.health`).

Providers are **lazily activated**: the scanner is only constructed when the config references a
provider context (or per-player providers/`molotov_thrown` are registered), so unused providers
cost nothing. See [`analysis-entity-providers.md`](./analysis-entity-providers.md).

---

## Data Flow

```
rules/ (shipped, next to app)  +  user overlay dir
    → RuleSetLocator (resolve both tiers; DEMOVIEWER_RULES_DIR override)
    → YamlConfigLoader.LoadWithOverlay (strict parse; per-file containment; overlay merge)
    → RuleConfigLoadResult { RuleChainConfig, attributed Errors, Loaded/FailedFiles }
    → DemoAnalysis.Build(demo, config)        — RuleChainBuilder + EventRegistry + registries
    → BuildResult (StateGraph, Nodes, EdgeDescriptors, PlayerContextIndex, EntityScanner, …)
    → DemoAnalysis.Evaluate(demo, build)      — StateGraphEvaluator
    → AnalysisRun { Build, Timeline, Snapshots? }
    → Output projectors (MetricTable) → CSV / JSON / UI tables
```

### The `DemoAnalysis` facade

`DemoAnalysis` (`src/Analysis/DemoViewer.NET.Analysis/DemoAnalysis.cs`) is the **single documented
entry point**. It wraps the registry/builder/evaluator assembly every consumer previously
duplicated — including the correctness-critical threading of `BuildResult.PlayerContextIndex` and
`BuildResult.EntityScanner` into the evaluator, which silently produces wrong results when
forgotten.

```csharp
AnalysisRun run = DemoAnalysis.Run(demo, config);            // one-shot
BuildResult build = DemoAnalysis.Build(demo, config);        // two-phase: render skeleton…
AnalysisRun run = DemoAnalysis.Evaluate(demo, build);        // …then evaluate
```

`AnalysisOptions` covers snapshot mode (`CaptureSnapshots`, default true; false = the bench's
cheaper `--bare` path producing only the `RuleChainTimeline`), `Progress`, and overrides for the
four registries (`Events`, `Plugins`, `EntityProviders`, `PerPlayerEntityProviders` — `null`
means "use `CreateDefault()`", not "none"). Both the app (`AnalysisViewModel`) and AnalysisBench
consume the facade; neither constructs `RuleChainBuilder`/`StateGraphEvaluator` directly.

---

## Rules Packaging & User Overlay

Rules load from **two tiers** (`YamlConfigLoader.LoadWithOverlay`, paths from `RuleSetLocator`):

| Tier | Location | Semantics |
|---|---|---|
| **Shipped** | `AppContext.BaseDirectory/rules` (csproj copies `rules/` to output); `DEMOVIEWER_RULES_DIR` env var overrides; repo-walk fallback for test hosts | Read-only defaults. Load-bearing: any error **hard-fails** with `RuleConfigException` listing every attributed error. |
| **User overlay** | `~/Library/Application Support/DemoViewer.NET/rules` (macOS), `%APPDATA%\DemoViewer.NET\rules` (Windows), `$XDG_CONFIG_HOME/DemoViewer.NET/rules` (Linux) | A user chain with the **same id replaces the shipped chain wholesale**; new ids append; `enabled: false` stubs disable shipped chains. Errors are **contained** — the broken file is skipped, everything else still loads, errors surface in `RuleConfigLoadResult.Errors`. |

The user directory is auto-provisioned on first use (`RuleSetLocator.EnsureUserRulesDirectory`):
a README explaining the overlay semantics plus a copy of `analysis-rules.schema.json` so
`# yaml-language-server` editor validation works immediately. The Analysis tab's **📁 Rules**
button opens it. Chain ids must be unique within a tier (duplicate = load error); files load in
name order. Disabled chains are dropped after tier merge, before the graph builds.

Loading is **strict**: unknown YAML keys, unknown enum strings (`type`, `action`, `scope`,
`reset`), files without a `chains:` section, and duplicate chain ids are all errors — attributed
with file, chain/rule id, and line/column where the parser supplies one. A typo'd rule fails
loudly at load time; it never silently reads 0 at eval time.

---

## YAML Schema

```yaml
chains:
  - id: kast
    scope: per_player
    rules:
      - id: kills
        type: counter
        reset: round
        parents: [context.round.active]
        triggers:
          - on: player_death
            condition: "event.KillerSlot == player.slot"
            action: increment

      - id: has_kast
        type: bool
        reset: round
        parents:
          mode: any
          rules:
            - rule: kills
              when: "value > 0"
            - context.player.survived
            - context.player.traded

      - id: kast_rounds
        type: counter
        default: 0

      - id: kast_pct
        type: expression
        value: "kast_rounds / round_number * 100"
        format: "F2"

      - id: multi_kill_tally
        type: threshold_tally
        source: kills
        thresholds:
          - { min: 3, target: Rounds3K }
          - { min: 2, target: Rounds2K }

    on_satisfied:
      increment: kast_rounds

    columns:
      - { rule: kills, label: Kills, group: round }
      - { rule: kast_pct, label: "KAST%", group: game }
```

The authoritative schema is `rules/analysis-rules.schema.json`; the full key reference is in
[`RULES_AUTHORING.md`](../RULES_AUTHORING.md).

### Columns (Visualization Metadata)

Columns map rules to table cells. They are pure visualization metadata — the graph builder and
evaluator have no concept of tables. The stats table, the output projectors, and CSV/JSON export
all derive their value columns from the union of `columns:` assignments, so a user-authored
column flows to every surface with zero extra wiring.

---

## Structural Deduplication

Rules are deduplicated by **structural hash**, not by name. The builder hashes each rule's
input-side properties (type, parents, triggers, conditions) and groups identical rules. Two
chains defining a structurally identical `kills` counter share one graph node regardless of
what each chain names it.

**Input-only identity:** A rule's outbound edges and child rules have no impact on its identity.
Same inputs = same graph node, regardless of what reads from it.

---

## Expression Language

Conditions and value expressions are compiled to delegates at build time via `ExpressionCompiler`
(`Building/ExpressionCompiler.cs`) — a recursive-descent parser over `System.Linq.Expressions`.
Zero interpretation overhead at evaluation time.

| Pattern | Example | Context |
|---|---|---|
| Event field | `event.UserId`, `event.Weapon == "awp"` | trigger `condition:` / `value:` |
| Player binding | `player.slot`, `player.team` (live, halftime-aware), `player.name` | per-player chains |
| Own value | `node.value + 1` (YAML alias `rule.value` is rewritten to `node.value`) | `set`/`increment` |
| Parent value | `value >= 2` (`active` = bool shorthand) | parent `when:` |
| Enrichment | `enrich.hurt.capped_damage` | any expression |
| Entity context | `entity.game.freeze_period == true` | any expression (provider-backed) |
| Per-player entity | `player.entity.pawn.health` (pre-frame snapshot read) | per-player chains |
| Sibling rules | `TotalEnemyDmg / round_number` | `type: expression` rules |

Operators: `&&` `||` `!`, comparisons (`==` `!=` `>` `>=` `<` `<=`), arithmetic (`+ - * / %`),
parentheses, int/double/float coercion, `"string"` literals, `true`/`false`. Division is **safe**
(x / 0 = 0). Unknown identifiers are compile-time errors.

Event fields resolve against the **SDK payload record's** property names (`CS2OpenSchema.Events`),
case-insensitively. `rules/catalog.json` is generated by reflecting over those records, so it is the
authoritative spelling. Game-event expressions bind the `GameEvent` envelope as their parameter and
reach wire fields through `Payload`, which is what keeps per-fire transport (`event.tick`)
addressable — no payload record carries a tick. Net-message and entity-change expressions have no
envelope and keep their payload parameter.

The compiler also backs the graph-breakpoint substrates (edge conditions with bare `player`,
`<SlotField>.entity.<provider>` event-subject reads, and node `input.<event>.<field>` mixed
conditions) — a debugger-only surface, not part of the YAML rule schema.

The breakpoint substrates compile the same way: a game-event edge or node-input condition binds
the envelope (`EdgeBreakpointConditions` / `NodeBreakpointConditions` thread the parameter type
from the edge's registration), so `event.tick` — and `input.<event>.tick` on a node — resolve in
a breakpoint condition exactly as in a ruleset, alongside `ServerTick`/`GameTick`/`FrameNumber`.
Net-message conditions keep their payload parameter; they have no envelope, and `event.tick` is
rejected there rather than guessed at.

---

## Extending in C#

### Plugins (legacy — registry ships empty)

`PluginRegistry.CreateDefault()` registers **no plugins**. All 14 original built-ins
(`computed_stats`, `kast_percentage`, `clutch_detection`, `enemies_flashed`,
`rapid_kill_sequences`, `multi_kill_rounds`, `team_aware_stats`, trade/opening-duel/round-win
plugins, `current_game_tick`, …) have been replaced by YAML rule types (`expression`,
`threshold_tally`, `windowed_streak`), enrichment edges, and built-in contexts. The
`IAnalysisPlugin` interface (`Plugins/IAnalysisPlugin.cs`) remains for embedders who register
their own via `PluginRegistry.Register` + `type: plugin` rules, but nothing shipped uses it.

### Enrichment edges — the real C# extension path

New cross-event / cross-player logic that YAML can't express belongs in an **enrichment edge**
(`Edges/`): a `StateEdge` subscribed to an event type that writes transient `enrich.*` nodes,
wired in `BuiltinContexts.CreateEnrichment`. Existing examples: `KillTeamEnrichmentEdge` (team
classification + trade detection), `HurtTeamEnrichmentEdge` (damage capping via entity health),
`ClutchEnrichmentEdge`/`ClutchResolutionEnrichmentEdge`, `BlindEnrichmentEdge`,
`RoundEndEnrichmentEdge`, `WeaponFireEnrichmentEdge`/`HurtBulletEnrichmentEdge`.

### Entity providers

New entity-state signals are added as providers, not edges — see
[`analysis-entity-providers.md`](./analysis-entity-providers.md).

---

## Output API

`Output/` turns an `EvaluationResult` into dimensioned tabular data
(design: [`ANALYSIS_ENGINE_OUTPUT_DESIGN.md`](./ANALYSIS_ENGINE_OUTPUT_DESIGN.md)):

| Type | Role |
|---|---|
| `MetricRow` / `MetricTable` | Dimensions (`match_id`, `round_number`, `player_slot`, …) + values (one per `columns:` assignment), stable column order |
| `IOutputProjector` | Pure transform `EvaluationResult + ParsedDemo → MetricTable[]` |
| `PlayerRoundStatsProjector` | One row per (player, round); samples the last snapshot of each live `round_number` value — profile-independent (no hardcoded round-end event) |
| `PlayerGameStatsProjector` | One row per player from the final snapshot (end-of-match scoreboard) |
| `IOutputFormatter` → `CsvOutputFormatter`, `JsonOutputFormatter` | Serialize a `MetricTable`; same tables feed in-app grids and export |

---

## Evaluation Loop

```
(optional) EntityChangeScanner.PrecomputeParallelDigests — parallel entity decode up front
foreach DemoFrame in demo.Frames:
    advance entity scanner (consume frame digest; synthesize entity-change / molotov events)
    foreach NetMessage in frame (+ synthesized messages):
        materialize new players (game events naming unknown slots)
        reset round-scoped rules (on round_freeze_end)
        reset transient enrichment nodes for this dispatch key
        dispatch edges by message type (enrichment before rules — topo sort)
        recompute logic nodes (conjunction/disjunction, dirty-flagged)
        fire rising-edge actions (on_satisfied)
        capture snapshot (snapshot mode only)
```

### Edge Ordering

Within each dispatch slot (edges grouped by message type), edges are topologically sorted
based on their declared effect:
- **Activate/SetValue** edges fire BEFORE edges that read from their target
- **Deactivate** edges fire AFTER edges that read from their target

Note the sort orders by written nodes, not by `Condition` reads — the `rounds_after_half_announce`
counter gate in `BuiltinContexts.cs` exists precisely because a condition-read ordering cannot be
enforced.

---

## Project Structure

| Project | Role |
|---|---|
| `DemoViewer.NET.Parser` | Parse raw bytes → `ParsedDemo` |
| `DemoViewer.NET.Analysis.Abstractions` | `StateNode`, `StateEdge`, `EvaluationContext`, `RuleChainTimeline`, `DemoSourceProfile`, `LogicalEventBinding` |
| `DemoViewer.NET.Analysis` | `DemoAnalysis` facade, `StateGraph`, `StateGraphEvaluator`, `RuleChainBuilder`, `ExpressionCompiler`, `EventRegistry`, `EntityChangeScanner`, providers (`Plugins/`), enrichment edges (`Edges/`), output (`Output/`), profiles (`Profiles/`) |
| `DemoViewer.NET.Analysis.Yaml` | `YamlConfigLoader` (strict, overlay-aware), `RuleSetLocator`, `RuleConfigLoadResult` |
| `DemoViewer.NET.Visualization` | Graph rendering with pan/zoom |
| `rules/` | Shipped YAML rule chains (`kast.yaml`, `player-stats.yaml`) + JSON schema |
| `tools/AnalysisBench` | CLI benchmark / accuracy-suite tool (golden stats, Leetify parity, export) |

---

## Performance Optimizations

| Optimization | Description |
|---|---|
| **Dispatch filtering** | Only edges relevant to the current message type are evaluated |
| **Lazy materialization** | Per-player rules created on first player encounter |
| **Lazy provider activation** | Entity scanner built only when a rule references an entity context; null scanner = zero per-frame work |
| **Parallel digest precompute** | Whole-demo entity decode chunked at `DEM_FullPacket` boundaries and run in parallel up front; per-frame consume stays sequential (proven element-wise identical to sequential digests) |
| **Snapshot reference sharing** | Identical snapshots share array references (32x speedup) |
| **Struct EvaluationContext** | Zero-allocation evaluation context per message |
| **TryApplyDirect** | Pre-extracted payload eliminates redundant type checks |
| **Dirty-flag logic nodes** | Conjunction/disjunction nodes skip recomputation when inputs unchanged |
| **Topological edge sort** | Kahn's algorithm with effect-aware dependency ordering |
| **Structural deduplication** | Identical rules across chains share one graph node |

---

## Accuracy Discipline

Correctness is enforced by test gates, reported as **per-stat match counts** (never a single
overall percentage):

- `OursVsExpected_StatParity` — zero tolerance against hand-verified `expected.golden.json` fixtures
- Leetify-side `StatParityTests` — per-stat tolerances (0.0 where exact parity is achieved;
  documented residual ceilings elsewhere) acting as regression tripwires across the 5-demo bench suite
- `ParallelDigestEquivalenceTests` and golden-output gates guard the parallel-decode and eval paths
