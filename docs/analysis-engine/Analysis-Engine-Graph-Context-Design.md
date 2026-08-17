# Analysis Engine: Graph Context & Enrichment Node Design

**Status:** Implemented
**Date:** 2026-04-28

## Overview

The analysis engine uses a graph-based enrichment system to make event-derived context (team classification, health tracking, trade detection) available as first-class nodes. YAML rule chains consume this context through parent references and expression nodes, eliminating the need for C# plugins in most cases.

Of the original 14 built-in plugins, 10 had been migrated to YAML at this doc's writing.
*(Update 2026-07-08: the migration is complete — `PluginRegistry.CreateDefault()` now ships
**empty**. The missing primitives landed as YAML rule types (`windowed_streak`,
`threshold_tally`) and enrichment edges (`ClutchEnrichmentEdge`/`ClutchResolutionEnrichmentEdge`,
`BlindEnrichmentEdge` for enemies-flashed); the "Plugin" rows in the migration table below are
historical.)*

## Architecture

### Three-Tier Node Scoping

**Event scope (transient):** Populated by enrichment edges when an event fires. Reset before the next event of the same type. Excluded from snapshot capture.

- `enrich.kill.was_enemy_kill` / `was_team_kill` / `was_self_kill` (bool)
- `enrich.kill.was_trade_kill` (bool) + `traded_player_slot` (int)
- `enrich.hurt.was_enemy_damage` / `was_team_damage` / `was_self_damage` (bool)
- `enrich.hurt.victim_health_before` (int) + `capped_damage` (int)

**Round scope:** Reset on `round_freeze_end`. Uses existing `IRoundScopedNode` interface. Examples: `context.player.alive`, round-scoped kill counters.

**Game scope:** Persistent for the demo. Examples: `context.player.team` (baked at build time), `context.match.live`.

### Enrichment via Topological Sort

Enrichment edges write to transient nodes. Rule edges source from those transient nodes. The existing topological sort in `TopologicalSortEdges()` ensures enrichment fires before rules within the same dispatch slot. No separate "enrichment phase" was needed.

```
graph.Root --[source]--> KillTeamEnrichmentEdge --[writes]--> enrich.kill.was_enemy_kill --[source]--> per-player rule edge --[writes]--> TotalEnemyKills
```

The evaluator resets transient nodes at the start of each dispatch via `_transientNodesPerKey`. Multi-write edges declare `AdditionalWrittenNodes` so the topo sort orders all downstream readers correctly.

### Player Context

`PlayerContextIndex` maps player slots to context (team, health, death history). Populated during player materialization in `StateGraphEvaluator`. Queried by enrichment edges for:

- Team classification (which team is the attacker/victim on)
- Health tracking (pre-hit HP for overkill capping)
- Trade detection (was the victim recently killed by a teammate within 320 ticks)

Health resets on `round_freeze_end` via `HealthResetEdge`.

### Expression Nodes

YAML `expression` rule type compiles formulas referencing sibling nodes:

```yaml
- id: ADR
  type: expression
  value: "TotalEnemyDmg / round_number"
  format: "F1"
```

Expression compilation is deferred to a second pass (after all counter/bool rules are built) so expressions can reference rules from any chain. Division is safe (returns 0 on divide-by-zero).

The `ExpressionCompiler` resolves:
- `event.<field>` — event payload properties
- `player.slot` / `player.team` — baked at build time
- `enrich.<path>` — transient enrichment node values
- Plain identifiers — sibling node values (for expression rules)
- `node.value` / `rule.value` — current node value (for triggered rules)

### Enrichment Edges

Two enrichment edges fire on every relevant event:

**`KillTeamEnrichmentEdge`** (on `PlayerDeathEvent`):
- Classifies kill as enemy/team/self
- Detects trade kills (victim's killer was killed by teammate within 320 ticks)
- Records death in `PlayerContextIndex` for future trade lookups

**`HurtTeamEnrichmentEdge`** (on `PlayerHurtEvent`):
- Classifies damage as enemy/team/self
- Reads victim's pre-hit HP, computes overkill-capped damage
- Updates victim's health in `PlayerContextIndex`

Both source from `graph.Root` (always active) and write to shared transient nodes.

## Plugin Migration Status

| Plugin | Status | Replaced By |
|--------|--------|-------------|
| `team_aware_stats` | YAML | Enrichment counters (TotalEnemyKills, TeamDmg, etc.) |
| `computed_stats` | YAML | Expression rules (ADR, HS%, KPR, KD, Surv%, HLTV) |
| `player_health_tracker` | YAML | Enrichment health tracking |
| `kast_percentage` | YAML | Counter + expression |
| `round_wins` | YAML | Conditional counters with `player.team` |
| `trade_stats` | YAML | Enrichment counter |
| `death_trade_window` | YAML | Enrichment death tracking |
| `check_trade` | YAML | Enrichment `context.player.traded` |
| `opening_duel_stats` | YAML | Per-side counters + expressions |
| `current_game_tick` | Plugin | Trivial, low priority |
| `clutch_detection` | Plugin | Needs cross-player aggregation |
| `rapid_kill_sequences` | Plugin | Needs windowed counter with tick decay |
| `enemies_flashed` | Plugin | Needs dictionary-based event correlation |
| `multi_kill_rounds` | Plugin | Needs threshold tally at round end |

## Performance

Bare eval (nuke 16r demo, Release build):
- Baseline (plugins only): 159ms
- With enrichment + YAML rules: 200ms (+41ms)
- Zero GC in bare mode (1 Gen0 collection)

The overhead comes from enrichment edges evaluating on every kill/hurt event and the additional YAML rule edges. The 4 remaining plugins contribute their share. Snapshot capture (92% of full eval time) is unaffected since transient nodes are excluded.

## Key Files

| File | Purpose |
|------|---------|
| `src/Analysis/DemoViewer.NET.Analysis.Abstractions/ITransientNode.cs` | Marker interface for event-scoped nodes |
| `src/Analysis/DemoViewer.NET.Analysis/Nodes/TransientBoolNode.cs` | Transient boolean node |
| `src/Analysis/DemoViewer.NET.Analysis/Nodes/TransientValueNode.cs` | Transient typed value node |
| `src/Analysis/DemoViewer.NET.Analysis/Building/PlayerContextIndex.cs` | Per-player context (team, health, deaths) |
| `src/Analysis/DemoViewer.NET.Analysis/Edges/KillTeamEnrichmentEdge.cs` | Kill team classification + trade detection |
| `src/Analysis/DemoViewer.NET.Analysis/Edges/HurtTeamEnrichmentEdge.cs` | Damage classification + health tracking |
| `src/Analysis/DemoViewer.NET.Analysis/Edges/HealthResetEdge.cs` | Round-start health reset |
| `src/Analysis/DemoViewer.NET.Analysis/Building/BuiltinContexts.cs` | Enrichment infrastructure creation |
| `src/Analysis/DemoViewer.NET.Analysis/Building/ExpressionCompiler.cs` | Expression compilation with enrichment support |
| `src/Analysis/DemoViewer.NET.Analysis/Building/RuleChainBuilder.cs` | Graph builder with enrichment wiring |
| `src/Analysis/DemoViewer.NET.Analysis/StateGraphEvaluator.cs` | Transient reset, snapshot exclusion |

## Future Work

### Remaining Plugin Primitives

To migrate the last 4 plugins, the graph needs:

- **Cross-player aggregation:** "count of players where team == myTeam and alive" for clutch detection
- **Windowed counter with tick decay:** Track kill streaks within a time window for rapid kill sequences
- **Dictionary-based event correlation:** Flash thrower → blinded victims mapping for enemies_flashed
- **Threshold tally:** Conditional increment at round end based on value ranges for multi-kill rounds

### Accuracy (updated 2026-07-08)

The "~55-58% vs Leetify" figure from this doc's original date is long obsolete, and accuracy is
no longer reported as a single overall percentage — per project convention it is tracked as
**per-stat match counts** with explicit gates:

- **`OursVsExpected_StatParity`** (in `StatParityTests`) enforces **zero divergence** against
  hand-verified `expected.golden.json` fixtures — the strict ground-truth gate.
- **Ours-vs-Leetify parity** (`StatParityTests._tolerances`) enforces **per-stat tolerances**
  across the 5-demo bench suite: a strict group pinned at 0.0 where exact parity has been
  achieved (kills, deaths, rounds survived, CT/T rounds won, 2K–5K multi-kill rounds, …), a
  small float headroom for provider display rounding (e.g. K/D), and documented-residual
  ceilings set at the currently observed max |Δ| so any future drift fails the test
  (closure rationale in `/KNOWN-AND-SUSPECTED-ISSUES.md`).

The systematic issues originally listed here (round wins reading 0, warmup gating, KAST%
rounding) were fixed in the intervening correctness passes; remaining known gaps are the
documented-residual tolerances in `StatParityTests`.
