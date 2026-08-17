# Analysis Engine Output Design

> **Status:** shipped / as-built. The full implementation sequence is now in the tree, including
> the two steps this doc's table historically flagged as pending: Step 5
> (`Output/RuleChainEventProjector.cs`) and Step 8 (the YAML `outputs:` schema — `Config/OutputDef.cs`,
> `Output/ConfiguredOutputProjector.cs`, wired through `DemoAnalysis` and the `outputs` key in
> `rules/analysis-rules.schema.json`). Retained as the design rationale for the shipped Output API
> — the live as-built surface is documented in the "Output API" section of
> `Analysis-Engine-Design.md`.

## Problem

The analysis engine evaluates YAML-declared rule chains against parsed CS2 demos and produces rich
per-message state snapshots. Today this data is consumed exclusively by the UI for step-through
visualization. There is no structured output — no CSV, no JSON, no API surface for downstream
consumers.

The visualization is valuable for debugging and monitoring rules, but the primary goal of
DemoViewer.NET is a fast, extensible parser/analyzer that produces **detailed, structured outputs**
based on declared rule chains. Users analyzing demos want tabular data they can feed into Excel,
pandas, R, or custom tooling.

---

## Design Principles

### Metrics with Dimensions

Inspired by OpenTelemetry's metric model: each output row is a set of **values** (the stats)
enriched with **dimensions** (context that describes what the values apply to).

```
Row = Dimensions + Values

Dimensions: { matchId, map, roundNumber, playerSlot, playerName, team, roundWinner }
Values:     { kills: 3, deaths: 1, damage: 287, headshots: 2, enemiesFlashed: 4 }
```

Dimensions are the "group by" axes. Values are the measurements. This maps naturally to CSV
columns, JSON objects, and database rows.

### Declarative Output Definitions

Users already declare rule chains in YAML. Output definitions should live alongside them — the
user specifies which nodes to sample, at what scope (per-round, per-game, per-event), and with
what labels. The engine handles the sampling mechanics.

### Same Data, Multiple Consumers

The same output feeds both export (CSV/JSON files) and in-app visualization (DataGrid tables,
summary panels). No separate rendering path — the application consumes the same `MetricTable`
structure that gets serialized to disk.

---

## Concrete Examples

The existing and new rule chains provide concrete test cases for the output API:

### What expresses cleanly in YAML (self-serve)

| Rule | YAML Pattern | Output |
|------|-------------|--------|
| Kills per round | Counter + `player_death` + condition | Per-round integer |
| Damage dealt | Counter + `player_hurt` + `node.value + event.DmgHealth` | Per-round integer |
| Utility damage | Same as damage but with weapon string `\|\|` condition | Per-round integer |
| Headshot kills | Counter + `event.IsHeadshot == true` condition | Per-round integer |
| Deagle HS rounds | Round-scoped counter + conjunction (>= 2) + rising edge → game counter | Per-game integer |
| KAST % | Disjunction chain + rising edge → accumulator / round_number | Per-game percentage |

These rules produce scalar values per player per round (or per game). The output is a natural
table: one row per (player, round), columns for each stat.

### What needs plugins (code required)

| Rule | Why Plugin | Output |
|------|-----------|--------|
| Enemies flashed | Team lookup requires `Demo.Players` — not available in expressions | Per-round integer |
| Rapid kill sequences | Stateful: tracks timestamps across kills within a round | Per-game integer |
| Trade detection | Cross-player temporal window with team validation | Per-round boolean |

Plugin-produced nodes output the same scalar values — they just need code to compute them. From
the output API's perspective, they're identical to YAML-produced nodes.

### What's not yet expressible — status update (2026-07-08)

Entity state **is** now integrated into the evaluator via entity-value providers
(`EntityChangeScanner` + `IEntityValueProvider`/`IPerPlayerEntityValueProvider` — see
`docs/analysis-engine/analysis-entity-providers.md`), which resolved most of this table:

| Rule | Original Gap | Status |
|------|-----|-------------|
| Money spent | No `item_purchase` event in parser | **Partially shipped** — equipment-value economy stats exist via `player.entity.pawn.equipment_value` / `player.entity.pawn.armor` (`AvgEquipmentValue`, `RoundsWithArmor` in `rules/player-stats.yaml`); a money-account provider (`m_iAccount`) would follow the same pattern |
| Per-weapon damage breakdown | Would need dynamic node creation per weapon string | Still open (per-weapon dimension axes are an explicit post-release item) |
| Positional data (player positions, grenade trajectories) | Requires entity state | **Shipped elsewhere** — position reconstruction and playback live in the 2D Playback module (cell-coord `PositionUtil.CellToWorld`), not as stat output rows |
| Economy tracking (money over time) | Requires `CCSPlayerController_InGameMoneyServices.m_iAccount` | Provider surface exists; time-series output scope still open (see item 7 below) |

---

## Output API

### Core Types

```csharp
// A single row of dimensioned metric values
public sealed record MetricRow(
    IReadOnlyDictionary<string, object> Dimensions,
    IReadOnlyDictionary<string, object> Values);

// A named, columnar collection of rows
public sealed record MetricTable(
    string Name,
    IReadOnlyList<string> DimensionColumns,
    IReadOnlyList<string> ValueColumns,
    IReadOnlyList<MetricRow> Rows);
```

### Output Projectors

A projector extracts metric tables from an `EvaluationResult` at semantically meaningful
boundaries (round-end, game-end, per-event).

```csharp
public interface IOutputProjector
{
    IReadOnlyList<MetricTable> Project(EvaluationResult result, ParsedDemo demo);
}
```

### Built-in Projectors

**`PlayerRoundStatsProjector`** — one row per (player, round):

Samples node values from `MessageSnapshots` at the message index where
`RoundOfficiallyEndedEvent` fires. Each materialized player's column nodes are read from the
snapshot at that index.

| Dimension | Source |
|-----------|--------|
| `matchId` | `CDemoFileHeader` or demo filename |
| `map` | `map_name` node value |
| `roundNumber` | `round_number` node value from snapshot |
| `playerSlot` | `MaterializedPlayer.PlayerSlot` |
| `playerName` | `MaterializedPlayer.PlayerName` |
| `team` | `Demo.Players[slot].Team` |

Values: each column assignment in the player's template → one value column.

**`PlayerGameStatsProjector`** — one row per player:

Reads from the final snapshot (last message index). For game-scoped nodes, this is the
accumulated total. For round-scoped nodes, this is the last round's value only (less useful —
the per-round projector is more informative).

**`RuleChainEventProjector`** — one row per rising-edge event:

| Column | Source |
|--------|--------|
| `matchId` | Demo header |
| `chainName` | `RuleChainEvent.ChainName` |
| `roundNumber` | Derived from `round_number` snapshot at event's frame index |
| `frameIndex` | `RuleChainEvent.FrameIndex` |
| `tick` | `RuleChainEvent.Tick` |

This produces the event log — useful for achievement tracking, temporal analysis, and debugging.

### Round Boundary Sampling Algorithm

The key technical challenge is finding the snapshot index where each round ends:

```
1. Scan EvaluationResult.Messages for GameEventMessage carrying RoundOfficiallyEndedEvent
2. For each match at index i:
   a. Read MessageSnapshots[i] — full node state vector after round end
   b. Read round_number node value from snapshot (tracked node index)
   c. For each MaterializedPlayer:
      - Read column node values from snapshot using NodeTrackedIndex
      - Build MetricRow with dimension tags + stat values
```

The `round_number` node is a singleton (from `round-lifecycle.yaml`), so its tracked index is
known from `FinalTrackedNodes`. Per-player column nodes have their tracked indices stored in
`PerPlayerColumnAssignment`.

---

## YAML Output Definitions

Extend the rule YAML schema with an `outputs` section that lets users control what gets exported:

```yaml
outputs:
  - id: player_round_stats
    scope: per_player_per_round
    metrics:
      - { node_id: kills, label: Kills }
      - { node_id: deaths, label: Deaths }
      - { node_id: assists, label: Assists }
      - { node_id: damage_dealt, label: Damage }
      - { node_id: utility_damage, label: UtilDmg }
      - { node_id: headshot_kills, label: HSKills }
      - { node_id: enemies_flashed, label: EnemiesFlashed }
    dimensions:
      - { source: match_id }
      - { source: map_name }
      - { source: round_number }
      - { source: player_slot }
      - { source: player_name }
      - { source: team }

  - id: achievements
    scope: per_event
    chains:
      - { chain_id: deagle_hs_round, label: "Deagle HS Round" }
```

### Output Scopes

| Scope | Sampling Point | Rows |
|-------|---------------|------|
| `per_player_per_round` | `round_officially_ended` message | players × rounds |
| `per_player_per_game` | Final snapshot | players × 1 |
| `per_event` | Each rising-edge event | 1 per chain satisfaction |

### Dimension Sources

| Source | Value |
|--------|-------|
| `match_id` | From `CDemoFileHeader` or demo file hash |
| `map_name` | `map_name` node value |
| `round_number` | `round_number` node value at sampling point |
| `player_slot` | Materialized player slot |
| `player_name` | Materialized player name |
| `team` | From `Demo.Players` |
| `round_winner` | From `RoundEndEvent.Winner` at preceding `round_end` |
| `chain_name` | From `RuleChainEvent.ChainName` (per_event scope only) |
| `tick` | Server tick at sampling point |

---

## Output Types Assessment

### Obviously Needed

1. **Per-player per-round stats table** — the CS2 community's standard output format. Every
   analysis tool produces this. Required for parity with existing tools.

2. **Per-player per-game summary** — aggregate stats across all rounds. End-of-match scoreboard.

3. **Rule chain event log** — timeline of rising-edge events. Required for achievement tracking
   and temporal analysis.

### Most Valuable to Add

4. **Enriched per-round rows** — dimensions beyond basic stats: round winner, round type
   (pistol/eco/full buy), side (T/CT), half number. These enable slicing in downstream tools
   ("show me CT-side stats on pistol rounds"). *Update: the underlying round-phase context rules
   shipped — `gameplay_phase` (WarmUp…PostMatch state machine), `bomb_status`, `half_state`, and
   `regulation_status` are built-in game-context rules (`Building/BuiltinContexts.cs`) any row
   can sample.*

5. **Achievement/event detail log** — for complex rules like "deagle HS round" or "rapid kill
   sequence", log the specific round, tick, and contributing events. This goes beyond the basic
   chain event log by including the node values that triggered the achievement.

6. **Multi-demo aggregation support** — consistent dimension schema (matchId, map) that allows
   concatenating output from multiple demos into a single dataset for cross-match analysis.

### Challenging to Add

7. **Time-series node values** — sampling node states at regular tick intervals (e.g. every 64
   ticks). Produces massive datasets (30-minute demo × 64 ticks/sec × 10 players × N nodes).
   Valuable for heatmaps and temporal visualization but needs careful scoping.

8. **Positional/spatial data** — player positions, grenade trajectories, kill locations. Requires
   entity state integration (`CSVCMsg_PacketEntities` → `EntityTracker`) into the evaluator loop.
   This is a major feature that unlocks a new class of analysis (2D minimaps, spray patterns,
   movement analysis) but is architecturally significant.

9. **Economy tracking** — money spent, equipment value, buy type classification. Requires either
   `item_purchase` game events (availability unconfirmed in full match demos) or entity state
   tracking of `CCSPlayerController_InGameMoneyServices.m_iAccount`. The former is a parser
   addition; the latter is the same entity state integration needed for positional data.

---

## Application Visualization

The same `MetricTable` that feeds CSV export feeds in-app UI components. No separate rendering
path.

### Current State

The analysis tab has:
- **Graph visualization** — MSAGL-powered node/edge rendering with per-message stepping
- **Player table** (`PlayerTableViewModel`) — per-player columns showing live node values
- **Chain summary strip** — chain names with satisfaction counts

### Extensions Using Output API

**Round-by-round breakdown panel:**
A DataGrid bound to `PlayerRoundStatsProjector` output. Each row = one player in one round.
Columns = the metrics defined in `outputs`. Users can sort, filter, and compare rounds.

**Game summary panel:**
A compact DataGrid bound to `PlayerGameStatsProjector` output. One row per player. Shows
totals, averages, and achievement counts.

**Achievement highlights:**
A list bound to `RuleChainEventProjector` output, filtered to "achievement" chains. Shows
which player triggered which achievement in which round. Clicking navigates to the
corresponding message in the step-through view.

**Export button:**
Triggers `IOutputFormatter.Format(table)` for all output tables. Save dialog with format
selection (CSV, JSON). Files named by table ID (e.g. `player_round_stats.csv`).

---

## Export Formats and Integrations

### Phase 1 — Implement First

**CSV (`CsvOutputFormatter`)**
- Standard RFC 4180 CSV with header row
- One file per `MetricTable`
- Universal compatibility: Excel, Google Sheets, pandas, R, SQL import
- Dimension columns first, then value columns
- No external dependencies — `StringBuilder` + proper quoting

**JSON (`JsonOutputFormatter`)**
- Array of row objects with `System.Text.Json`
- Each row = `{ dimensions: {...}, values: {...} }` or flat merged object
- Structured format for web tools, APIs, and programmatic consumption
- Optional: JSON Lines (`.jsonl`) for streaming/append-friendly output

**In-app DataGrid**
- Same `MetricTable` data bound to Avalonia `DataGrid` controls
- No serialization — direct ViewModel binding

### Phase 2 — Future

**SQLite (`SqliteOutputFormatter`)**
- One table per `MetricTable`, auto-created schema from column names/types
- Users can query with SQL: `SELECT playerName, AVG(damage) FROM player_round_stats GROUP BY playerName`
- Enables cross-demo databases (append mode)

**Parquet**
- Columnar format optimized for analytical queries
- Native support in pandas, Spark, DuckDB, Polars
- Efficient compression for large datasets
- Requires a Parquet library (e.g. Parquet.Net)

**Headless CLI Mode**
- `dotnet run --project DemoViewer.NET.CLI -- analyze demo.dem --output csv --rules rules/`
- No UI dependency — batch processing for pipelines
- Separate project referencing Analysis + Analysis.Yaml but not Avalonia

**REST API**
- HTTP endpoint exposing `MetricTable` as JSON
- Enables integration with dashboards (Grafana, custom web UIs)
- Would use ASP.NET Minimal APIs

---

## Implementation Sequence

| Step | What | Dependencies | Status (2026-07-08) |
|------|------|-------------|---------------------|
| 1 | `MetricRow`, `MetricTable` types | None | Done |
| 2 | `IOutputProjector` interface | Step 1 | Done |
| 3 | `PlayerRoundStatsProjector` | Steps 1-2, existing `EvaluationResult` | Done — note: samples the last snapshot of each live `round_number` value (profile-independent), not the `RoundOfficiallyEndedEvent` index described above |
| 4 | `PlayerGameStatsProjector` | Steps 1-2 | Done |
| 5 | `RuleChainEventProjector` | Steps 1-2 | Shipped (`Output/RuleChainEventProjector.cs`) |
| 6 | `CsvOutputFormatter` | Step 1 | Done |
| 7 | `JsonOutputFormatter` | Step 1 | Done |
| 8 | YAML `outputs` schema extension | Steps 1-2 | Shipped (`Config/OutputDef.cs`, `Output/ConfiguredOutputProjector.cs`) |
| 9 | In-app DataGrid binding | Steps 3-4 | In progress — the Stats module |
| 10 | Export UI (button + save dialog) | Steps 6-7, 9 | In progress — in-app export; CLI export via AnalysisBench exists |

Steps 1-7 are pure library code with no UI dependency. Step 8 extends the YAML schema. Steps 9-10
are UI integration.

---

## Open Questions

| Question | Status |
|----------|--------|
| Does `item_purchase` exist as a game event in full CS2 match demos? | Unconfirmed — small test demos showed no evidence, but full match demos (100-300MB) may differ |
| Should per-round output include the round's buy type (pistol/eco/force/full)? | Requires economy data (blocked by above) or heuristic classification |
| Should output support user-defined computed columns (e.g. `kills / deaths` ratio)? | Defer — keep v1 simple with raw node values |
| Where should output types live — Analysis project or separate Analysis.Output project? | Analysis project for now; extract if dependencies grow |
| Should the headless CLI be a separate project or a mode flag on Desktop? | Separate project — clean dependency boundary, no Avalonia |
