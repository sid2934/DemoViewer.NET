# Round Facts: design

> **Status: APPROVED 2026-09-23**, after revision. The route is **rules-driven**: the
> record is the output of a shipped ruleset, `round_facts`, evaluated by CS2DemoKit on the forward
> path, so that users can edit the classification and extend the record without a code change. The
> engine pieces that make this possible are requested upstream as
> **[CS2DemoKit #54](https://github.com/CS2OpenDev/CS2DemoKit/issues/54)**; this design assumes they
> land and the pin bumps before the build starts. The answers to every decision are recorded
> in §8. There are no open questions: every earlier unknown is now a stated assumption with its
> fallback, or a verification task assigned to a named work item.

**Work item:** Round Facts (plan §3, Phase 0; finding F5; decision D3). · **Tree:** `main` at
`d90ec9f` (0.8.1), CS2DemoKit 0.12.0 measured; design targets the release that closes #54. ·
**Written:** 2026-09-23, revised the same day at review.

This document answers the two research questions the plan asks (can a shipped YAML ruleset emit a
team-scoped per-round table; which buy-type thresholds to adopt) and then designs the per-round fact
record: what it holds, how it is computed, where it is stored, how users customise it, and how the
consumers read it. Everything measured below was measured on real Valve matchmaking demos with a
scratch project; the tour sample was not used for anything.

**The short version.** On 0.12.0 a ruleset cannot emit a team-scoped per-round table (§2, §3.1).
Rather than work around that in the app, the decision (D3) was to make the engine able to: six additions
(a team scope on the forward path, game-rules providers, a real round end on GOTV profiles,
frame-clock ticks, team money, plant site) are filed as #54. With them, Round Facts is one shipped
ruleset with the classifier thresholds as `params:`, HLTV's bands by default, editable by dropping a
same-id ruleset in the user rules directory. The app evaluates that ruleset at library index time on
the held tier-2 parse, caches its rows as an analysis output keyed by the ruleset's resolved
identity, and exposes them through `IRoundFactsSource`. The record is per round with two per-side
sub-records, keyed by `ClipRound.Number`, on the frame clock.

---

## 1. Problem and scope

Finding F5: `CachedRound` is `Number` plus `StartTickFrameClock` and nothing else
(`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs:84-89`). The original roadmap's claim that
"the parser already knows most of the labels" is true of the demo and false of anything the app
caches or exposes. Four downstream items block on this:

| Consumer | What it needs per round |
|---|---|
| Search Filters And Live Count | side, each side's buy type, phase at a tick, score, man-count at a tick |
| Result Cards And Walking | both buys, end reason, score, clock |
| Free Labels From Round Facts | every fact as a parser-namespace label on a tag instance |
| Opponent Dossier (Setup Heatmaps By Buy, Post-Plant And Retake, Situational Behaviour) | buy type, plant site and tick, retake window, man-advantage states, pistol rounds |

Also a foreign key for The Round Index and a seed for Create Strat From Round.

**In scope.** The per-round, per-side fact record; the shipped ruleset that produces it; the
classifier as ruleset parameters; the evaluator that runs it at index time; its cache; its read API;
user customisation and extension; its alignment with the frame clock and `ClipRound`.

**Out of scope.** Team identity ("us" and "them" are Team Identity's job; this record carries sides
and slots so the join is trivial, and stays team-free per integrator correction 9). Per-player
per-round stats (the engine's `player_round_stats` already exists). Grenade facts (Grenade Walk).
Position tokens (The Round Index). Any UI tab: Round Facts is a store and a service, not a surface.
The engine work itself: that is #54, designed here only as far as naming what the ruleset reads.

---

## 2. What exists today

### 2.1 The cache

- `DemoCacheRecord` has three stamped tiers, Header, Parse, Analysis, each with its own schema
  constant (`DemoCacheModels.cs:203-206`) and `TierStamp` (`:55`). Tier 2 holds `TickRate`,
  `TickCount`, `ServerStartTick` (`:229-231`) and `Rounds` (`:234`). The Analysis tier carries
  `ConfigFingerprint` (`:262`), the hash of the effective ruleset configuration, and
  `IsAnalysisCurrent(fingerprint)` / `NeedsAnalysis(fingerprint)` (`:326`, `:435`) decide whether
  the analysis outputs are stale.
- The index row `DemoCacheIndexEntry` mirrors tier presence (`:378-419`) so backlogs derive without
  opening sidecars.
- `DemoCacheStore` writes one sidecar per demo under `demos/` named by `StableKey(path)`
  (`DemoCacheStore.cs:500`), atomically (`:559`), keeps records in memory when there is no cache
  root (`:59`, the browser host), and exposes `TryLoadRecord` (`:200`), `LoadRecords` (`:301`, the
  cross-demo bulk read, off the UI thread), `UpdateExisting` (`:340`) and `Upsert` (`:367`).

### 2.2 The round authority

`CS2DemoKit.Analysis.Clips.ClipRounds.Derive(ParsedDemo)` is the single deriver of round boundaries
in the frame clock: one round per `round_freeze_end`, numbered from 1, `StartTickFrameClock` =
`GameEvent.GameTick` (XML doc on `ClipRound` and `ClipRounds`). Both tier-2 writers use it:
`DemoLibraryService.ProjectTier2` (`src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs:1393`)
and `HighlightScanService.WriteHarvest` (`src/App/DemoViewer.NET/Modules/Highlights/HighlightScanService.cs:609`).
CS2 does not emit `round_start`; the round opens at `round_freeze_end`. Integrator correction 12:
round numbering is `ClipRound.Number` everywhere.

### 2.3 The tier-2 parse pass

`DemoLibraryService.IndexTier2Core` (`DemoLibraryService.cs:1138`) holds the `ParsedDemo`, posts the
cheap metadata, then calls `ExtractFinalScore` (`:1176`, body at `:1403`), which replays every frame
through an `EntityTracker` with `StoreClassFilter = { "CCSTeam" }` (`:56-59`, `:1418-1421`) to read
`m_iScore` and the clan names. `ProjectTier2` (`:1362`) builds the roster and rounds and
`WriteTier2ToDemoCache` (`:1302`) writes the tier. The parse is fanned out to other evaluators
through `IDemoEvaluator` ("one parse, many evaluators",
`src/App/DemoViewer.NET/Services/DemoProcessing/IDemoEvaluator.cs:22`), so a new consumer of the held
parse costs no second parse.

### 2.4 The engine's per-round surface (0.12.0, from the packaged XML docs)

- `Output.PlayerRoundStatsProjector`: one `MetricRow` per (player, live round), sampled at the last
  snapshot of each round; dimensions `match_id, map, round_number, player_slot, player_name, team`.
  `Output.ConfiguredOutputProjector` projects `show: tables` at `OutputScope.PerPlayerPerRound`,
  `PerPlayerPerGame`, `PerEvent` or `PerMatch`. There is no per-round scope without a player.
- `Edges.PlayerEconomyFreezeEndEdge` writes `round.team.equipment` and `round.enemies.equipment`
  once per player at `round_freeze_end` from the digest-sampled team sums; both are subject-relative.
- `Edges.RoundEndEnrichmentEdge` "derives the winning side from bomb state and alive counts" on the
  profile's `$round_end`. It has to: a Valve matchmaking demo carries no `round_end` (§2.7).
  `Cs2GotvProfile` binds `$round_end` to `round_officially_ended`; `Cs2GotvPreRestartProfile` to
  `cs_pre_restart`. The enrichment outputs are `enrich.round.has_winner`, `winner_side`,
  `winner_team`. There is no reason output.
- The only game-rules provider is `Plugins.FreezePeriodProvider` (`entity.game.freeze_period`).
  Nothing reads `m_iRoundWinStatus` or `m_eRoundWinReason`.
- `ClipRound`, `ClipRounds`, `ClipWindows` and the cache are frame clock. `event.tick` in the rules
  language is `GameEvent.ServerTick`, the server-boot clock (confirmed in §2.7).
- Ruleset resolution: `RuleSetLocator` reads the shipped `rules/` set then `<config>/rules/`; a
  user ruleset with the same id **replaces** the shipped one (`YamlConfigLoader.MergeById`), and
  user-tier errors are collected rather than thrown (`docs/plugins/plugin-system-design.md` §1.2).
  Four shipped rulesets are kept byte-identical to the package's copies (`README.md` "Rules").
- `params:` blocks with typed defaults, minimums and maximums are part of the language
  (`rules/post_plant_double.rules.yaml`, `min_kills`).

### 2.5 What the rules language allows

- `for:` is `match` or `each_player` (`rules/cs2demokit-rules.schema.json:24-31`).
- `per:` is `round` or `match` (`:638-644`).
- `show.tables.<name>.per` is `player_round`, `player_match` or `match` (`:1620-1630`), "the closed
  table registry".
- `docs/rules-v2/rules-v2-spec.md` §4, "Scope: `for:` × `per:`": a `for: match` ruleset has no
  subject, rejects `round.team.*` / `round.enemies.*`, and `show: tables: per: match` "projects a
  single match-level row".
- There is no `for: each_team`, no `per: team`, no team dimension anywhere.

### 2.6 How the app reads ruleset output

`StatsTabViewModel.Update` runs the three built-in projectors
(`src/App/DemoViewer.NET/ViewModels/Stats/StatsTabViewModel.cs:704-715`) and `UpdateFromRun` adds
configured outputs and keyed tables (`:473-490`). The round view is `RoundTable.Rows` filtered by
`round_number` (`:1243`). Nothing in the app consumes a per-round table outside the Stats tab, and
the Stats tab needs a snapshot-mode run ("Stats tab requires snapshot-mode evaluation", `:477`).

`RoundTrack.ApplyWinnerTints` (`src/App/DemoViewer.NET/Modules/Playback2D/Timeline/RoundTrack.cs:99-140`)
reads `round_end` for the winner. §2.7 shows that event never occurs on a Valve matchmaking demo, so
the timeline's round bands are never tinted there today. This item fixes it (§3.6, decision 6).

### 2.7 What the demo carries (measured)

Scratch project `round-facts` (parser and analysis 0.12.0), two untrimmed Valve
matchmaking demos from the Steam replays folder:

| Demo | Map | Build | Frames | Events | Rounds |
|---|---|---|---|---|---|
| `match730_003731893271710924851_1024675027_129.dem` | de_nuke | 10231 (Jan 2025) | 154,869 | 12,850 | 23 |
| `match730_003844252717140672725_0377894676_389.dem` | de_dust2 | 10896 (Sep 2026) | 106,901 | 14,258 | 19 |

**Events.** Present on both: `round_freeze_end`, `round_officially_ended`, `round_prestart`,
`round_poststart`, `cs_pre_restart`, `buytime_ended`, `bomb_planted` / `bomb_defused` (fields `C4`,
`Site`, `UserId`, `UserIdPawn`), `player_death`, `player_hurt`, `player_team`, `announce_phase_end`,
`cs_win_panel_match`. Absent on both: `round_start`, `round_end`, `cs_win_panel_round`,
`bomb_exploded` (no bomb detonated in either match; the event is in the catalog). `Site` is the
bomb-target entity index (173 / 236 on Nuke, 168 / 169 on Dust2), not a letter. `ClipRounds.Derive`
returned 23 and 19, equal to the `round_freeze_end` count and to the real round count.

**Entity fields**, all in the parser's generated lens (upstream
`src/CS2DemoKit.Parser/Entities/Generated/SchemaLens.Generated.cs` at `origin/main`, lines 173-203
for `CCSGameRules` and its `m_pGameRules.` proxy form, 224-237 for `CCSPlayerController`, 243-286
for `CCSPlayerPawn`, 301-302 for `CCSTeam`):

| Class | Field | Observed |
|---|---|---|
| `CCSGameRulesProxy` | `m_pGameRules.m_bFreezePeriod` | 1→0 at exactly the `round_freeze_end` GameTick in every round (1761 on both demos for round 1); 0→1 at exactly the `round_officially_ended` GameTick |
| | `m_pGameRules.m_iRoundWinStatus` | 0→2 (T) or 0→3 (CT) at the instant the round is decided; back to 0 at `round_officially_ended`, 448 ticks (7.0 s) later in every round of both demos |
| | `m_pGameRules.m_eRoundWinReason` | set with the win status; values seen 7, 8, 9, 12 |
| | `m_pGameRules.m_totalRoundsPlayed` | increments with the win status; 0 before round 1 |
| | `m_pGameRules.m_gamePhase` | 2 during the first half, 3 during the second; overtime not present in either match |
| | `m_pGameRules.m_bBombPlanted` | 0→1 at the `bomb_planted` GameTick; cleared at `round_officially_ended` |
| | `m_pGameRules.m_iRoundTime` | 115 |
| `CCSPlayerPawn` | `m_unFreezetimeEndEquipmentValue` | per-side sums identical when sampled at the freeze-end frame and one second later, every round, both demos |
| | `m_unCurrentEquipmentValue` | drifts after freeze end (pickups, drops); not the classifier input |
| | `m_szLastPlaceName` | `BombsiteA` / `BombsiteB` on the planter's pawn at the plant frame, every plant, both demos |
| `CCSPlayerController` | `m_iTeamNum`, `m_hPlayerPawn`, `m_pInGameMoneyServices.m_iAccount` | side and pawn handle reliable; money sane on build 10896 ($0-$300 per player after the pistol buy) but implausible on build 10231 in round 1 only ($3,400-$4,300 per player at freeze end plus one second; rounds 2 onward sane) |
| `CCSTeam` | `m_iScore`, `m_iTeamNum` | per side; the score swaps sides at halftime with the teams (before round 13 on Nuke: CT 4, T 8, after a 7-4 / 8-4 first half) |

**Ticks.** `DemoFrame.ServerTick` and `GameEvent.GameTick` are the same frame clock;
`GameEvent.ServerTick` is offset by `ParsedDemo.ServerStartTick` (1988 and 1570). The ruleset
probe's `event.tick` captures came out on the server clock (plant at 6083 where the event's
GameTick is 4513).

**Cost of the replay a walk would need** (kept for the budget in §7, not as a design input):

| Demo | CCSTeam only (warm) | Four classes (warm) | Marginal |
|---|---|---|---|
| de_nuke 10231, 154,869 frames | 366 ms | 439 ms | +73 ms |
| de_dust2 10896, 106,901 frames | 677 ms | 985 ms | +308 ms |

Parse itself: 620 ms and 460 ms.

**Ruleset probe.** A `for: each_player` ruleset with a `player_round` table of `compute:
round.team.equipment`, `compute: round.enemies.equipment`, `compute: round.team.alive`, captures on
`bomb_planted`, `bomb_defused`, `raw.round_officially_ended`, `raw.player_death`, and `capture:
player.place on: bomb_planted where: event.UserId == player.slot` validated and ran (754 ms, snapshot
mode, on de_dust2). It produced 190 rows (10 per round). Observed: the `team` dimension is the side
the player finished on, not the side in that round; `count: round_won` and `count: round_lost` both
read 1 on every row (the view's team binding does not filter without an explicit `where:`);
`PlantSite` is the entity index; `PlanterPlace` is `BombsiteA`/`B` on the planter's row only; every
tick column is server clock. A `for: match` ruleset with `show: tables: per: match` produced one row;
the same ruleset asking for `per: player_round` validated and produced zero rows silently.

---

## 3. Proposed design

### 3.1 Research question one, and the route

**On 0.12.0, a ruleset cannot emit a team-scoped per-round table.** The output dimensions are
player-scoped or match-scoped (§2.5); a match-scoped ruleset has no per-round output and cannot read
the team namespaces. The folded `player_round` workaround loses the end reason, puts ticks on the
server clock, mislabels the side, needs a snapshot-mode run at tier 3, and yields ten rows per round.

**Decided (D3, §8): make the engine able to, and drive Round Facts from a ruleset.**
The reasons are the ones the original roadmap and the plan already give for rules over code: the knowledge
lives in YAML a user can read and edit; the classification thresholds become `params:`; a team can
extend the record with columns of its own without a build; and the shipped ruleset is customised the
way every shipped ruleset already is, by a same-id override in the user rules directory. The
app-side walk that the first draft recommended is retired: it would have duplicated engine concepts
in C# and been invisible to users.

What the engine must provide is filed as **CS2DemoKit #54**, six items, each measured in §2.7:

| # | Engine addition (#54) | What Round Facts reads through it |
|---|---|---|
| 1 | A team subject on the forward path: `for: each_team`, rows per (round, side), with `round.team.*` / `round.enemies.*` resolving for the subject and a `round.team.slots` list | the row itself; side; slots at freeze end; alive counts |
| 2 | Game-rules providers under `entity.game.*`: `round_win_status`, `round_win_reason`, `total_rounds_played`, `game_phase`, `bomb_planted`, `round_time` | end tick, winner, reason, match round number, half, round time |
| 3 | A real `$round_end` on the GOTV profiles fired at the `m_iRoundWinStatus` transition, with winner and reason | the exact end tick and the reason as an event, so `enrich.round.*` stops deriving from alive counts |
| 4 | Frame-clock ticks in outputs: `event.frame_tick` | every tick column, on the clock every consumer uses (F15) |
| 5 | `round.team.money` / `round.enemies.money` at the freeze-end sample | the force-buy input |
| 6 | `enrich.round.plant_site` (the planter's place) plus the raw site entity index | plant site as a letter, with the index kept for Zone Baking to cross-check |

The design below names those surfaces as if they exist. Where #54 delivers a surface under a
different name, the ruleset changes, the record does not. If #54 lands partially, the ruleset emits
null for the columns it cannot compute and the record says so per column (`Source` fields below);
there is no app-side fallback walk by decision.

### 3.2 Research question two: buy-type thresholds, as ruleset parameters

What the three references do:

| Source | Unit | Pistol | Eco | Middle | Full | Force |
|---|---|---|---|---|---|---|
| HLTV economy page (legend as quoted in search results; the page itself returns 403 to fetch) | team start equipment | not a class | "full eco" 0-5k | "semi-eco" 5-10k, "semi-buy" 10-20k | 20k+ | not a class |
| Leetify (rating update post) | per player equipment, "1 of 4 economic groups", CT and T thresholds differ | tracked as a round type in recaps | group | group | "both teams having over 20k" (4k per player) | listed in recaps; thresholds not published |
| CS Demo Manager (`akiver/cs-demo-analyzer`, `pkg/api/economy.go`) | per player, or team sum scaled by valid player count | first round of each half, no overtime | ≤ $1,000 per player | "semi" | ≥ $4,500 per player CT, ≥ $4,000 T | previous round lost and money ≤ $400 per player after buying |

**Decisions 2, 3 and 4 (§8):** five classes (`Pistol`, `Eco`, `Semi`, `Force`, `Full`);
`Force` keyed on money after a lost round with the reliability guard; **HLTV's bands as the
defaults**, scaled per player on the side so a 4v5 classifies sensibly; every threshold a parameter
a user can change. HLTV has one full-buy band for both sides, so the CT/T split CS Demo Manager uses
is available as two parameters that default to the same value.

```yaml
params:
  eco_max_per_player:        { type: int, default: 1000, min: 0,    max: 16000 }   # HLTV "full eco" 0-5k at five players
  full_min_per_player_ct:    { type: int, default: 4000, min: 0,    max: 16000 }   # HLTV "full buy" 20k+ at five players
  full_min_per_player_t:     { type: int, default: 4000, min: 0,    max: 16000 }   # set 4500 / 4000 for CS Demo Manager's split
  force_money_max_per_player:{ type: int, default: 400,  min: 0,    max: 16000 }   # CS Demo Manager's force rule
  money_sane_max:            { type: int, default: 16000, min: 0,   max: 65535 }   # the reliability guard (§2.7, build 10231)
  regulation_rounds:         { type: int, default: 24,   min: 2,    max: 60 }      # MR12; set 30 for an MR15 league
```

```text
n = players on the side at freeze end (round.team.players)
E = round.team.equipment            (sum of m_unFreezetimeEndEquipmentValue at freeze end)
M = round.team.money                (sum of m_iAccount at the freeze-end sample; #54 item 5)
reliable = M is present and every account is in [0, money_sane_max]

Pistol : match round is 1 or regulation_rounds / 2 + 1; never when match round > regulation_rounds
Eco    : E <= eco_max_per_player * n
Full   : E >= full_min_per_player_{side} * n
Force  : otherwise, and the side lost the previous live round, and reliable, and M < force_money_max_per_player * n
Semi   : otherwise
```

Why these defaults: at five players `Eco` is HLTV's "full eco" band exactly (≤ $5,000) and `Full` is
HLTV's "full buy" band exactly (≥ $20,000); HLTV's "semi-eco" and "semi-buy" collapse into `Semi`,
which stays as one class. `Force` cannot be told from `Semi` by equipment alone,
which is why it reads money (decision 3) and why the guard exists: on build 10231 the round-1
money read is implausible (§2.7), so a side whose money is not reliable classifies as `Semi` and the
row says `MoneyReliable = false`. `Pistol`, `Eco` and `Full` never depend on money. Overtime starts
at $10,000 in CS2, so its first rounds classify by equipment like any other; they are never `Pistol`.

The record stores `E`, `M`, `n` and every parameter value beside the class, so any consumer can
re-bucket for display parity (HLTV's four bands, CS Demo Manager's split) without re-evaluation,
and a row always says which thresholds produced it.

Worked check against §2.7 with the defaults (Dust2, build 10896, round 2, CT lost round 1): CT `E` =
4,700, `n` = 5 → `Eco`; T `E` = 13,550 → `Semi`. Round 3: CT 19,350 → `Semi`; T 21,350 → `Full`.
Round 5: T 5,000, exactly the eco ceiling → `Eco`. Nuke round 8: CT 4,000 → `Eco`; T 22,550 →
`Full`. With `full_min_per_player_ct: 4500` (CS Demo Manager) Dust2 round 3 CT stays `Semi` either
way; the two rules differ only for CT sides between $20,000 and $22,500, which the stored `E` lets a
user inspect.

### 3.3 The shipped ruleset

`rules/round_facts.rules.yaml`, `ruleset: round_facts`, kept byte-identical to the copy the engine
ships (§3.5 says why the engine is the authoritative home). Sketched against the #54 surfaces:

```yaml
# yaml-language-server: $schema=./cs2demokit-rules.schema.json
ruleset: round_facts
title: Round Facts
summary: One row per round per side; the vocabulary every Strat Room feature filters on.
for: each_team                       # #54 item 1
params: { ...as in §3.2... }

define:
  opening_kill:  { on: player_death, match: { enemy: true } }         # attacker and victim on different sides
  first_contact: { on: player_hurt,  match: { enemy: true } }

stats:
  freeze_end_tick:   { capture: event.frame_tick, on: round_freeze_end, per: round }   # #54 item 4
  end_tick:          { capture: event.frame_tick, on: $round_end,       per: round }   # #54 item 3
  end_reason:        { capture: entity.game.round_win_reason, on: $round_end, per: round }   # #54 item 2
  winner_side:       { capture: entity.game.round_win_status, on: $round_end, per: round }
  match_round:       { capture: entity.game.total_rounds_played, on: round_freeze_end, per: round }
  game_phase:        { capture: entity.game.game_phase, on: round_freeze_end, per: round }
  round_time:        { capture: entity.game.round_time, on: round_freeze_end, per: round }
  plant_tick:        { capture: event.frame_tick, on: bomb_planted, per: round }
  plant_site:        { capture: enrich.round.plant_site, on: bomb_planted, per: round }     # #54 item 6
  plant_site_entity: { capture: event.Site, on: bomb_planted, per: round }
  defuse_tick:       { capture: event.frame_tick, on: bomb_defused, per: round }
  explode_tick:      { capture: event.frame_tick, on: bomb_exploded, per: round }
  first_contact_tick:{ capture: event.frame_tick, on: first_contact, per: round, keep: first }
  opening_kill_tick: { capture: event.frame_tick, on: opening_kill,  per: round, keep: first }
  slots:             { capture: round.team.slots, on: round_freeze_end, per: round }        # #54 item 1
  players:           { capture: round.team.players, on: round_freeze_end, per: round }
  score_before:      { capture: round.team.score, on: round_freeze_end, per: round }
  equipment:         { capture: round.team.equipment, on: round_freeze_end, per: round }
  money:             { capture: round.team.money, on: round_freeze_end, per: round }        # #54 item 5
  kill_ticks:        { capture: event.frame_tick, on: player_death, per: round, keep: list }
  kill_victim_side:  { capture: victim.team,      on: player_death, per: round, keep: list }
  kill_team_alive:   { capture: round.team.alive, on: player_death, per: round, keep: list }
  kill_enemy_alive:  { capture: round.enemies.alive, on: player_death, per: round, keep: list }
  buy_type:          { compute: "..." }            # the §3.2 rule over equipment, money, players, score history

show:
  tables:
    round_facts:
      per: team_round                # #54 item 1
      columns: [ ...every stat above, plus the parameter values... ]
```

Two things the sketch relies on that the engine already has: `keep: list` per-round captures
(`post_plant_double.rules.yaml`), and `params:` with typed bounds. `buy_type` is a `compute:` over
the closed function set (`min`, `max`, comparisons, `and`/`or`); the previous-round-lost input is
a per-round capture of the previous row's `winner_side`, which the team scope makes a plain read.
If #54 lands `buy_type` as a built-in enrichment instead, the ruleset uses it and the parameters
move to that enrichment's inputs; the record is unchanged.

**Columns beyond the schema are the extension point.** A user's override may add stats and columns.
The projection (§3.4) maps the known columns onto the typed record and carries every other column in
`Extra` (name → scalar), which Free Labels From Round Facts exposes as `parser.<column>` labels and
Search Filters exposes as equality filters. That is how a team captures "new unique things" without
touching the app.

### 3.4 Computation: `RoundFactsEvaluator`

An `IDemoEvaluator` (`Services/RoundFacts/RoundFactsEvaluator.cs`, id `roundfacts`) on the tier-2
fan-out (§2.3), so it runs on the held parse with no second parse. It evaluates **only** the
`round_facts` ruleset (the effective one, user override included) through `DemoAnalysis` on the
forward path, reads the `round_facts` table, and projects rows onto the record:

1. `rounds = ClipRounds.Derive(parsed)`; the table's `round_number` is `ClipRound.Number`
   (correction 12), and `freeze_end_tick == StartTickFrameClock` is asserted per row.
2. Pair the two rows of each round by side into `RoundFacts` with two `SideFacts`. Round-level
   columns must agree between the pair; a disagreement is a projection error recorded on the round
   (`ProjectionWarnings`) and the row still lands.
3. Build `Kills` from the four `keep: list` columns (same length, tick order); `CtAlive`/`TAlive`
   after each kill from the side-relative counts and the side.
4. Known columns → typed fields; unknown columns → `Extra`. Parameter values → `Thresholds`.
5. Per-column `Source`: a column that is null because the engine did not provide the surface (a
   partial #54) is marked `Unavailable`, distinct from `null` meaning the demo did not say (no plant).

Source precedence is entity first (via the providers), event second, and the ruleset encodes it:
`end_tick` captures on `$round_end` (the win-status transition, #54 item 3) and a second stat
captures `round_officially_ended` so the projection can report `EndSource = OfficiallyEndedEvent`
on a source whose profile lacks the real end.

`ExtractFinalScore` is unchanged: it stays the Library's own CCSTeam replay. Round Facts' score is
`round.team.score` at freeze end from the engine; the final score in the record header comes from
the Library as today. A test asserts the two agree on the last round.

### 3.5 Storage and ownership (decision 5)

Which layer should own this is decided here. The answer follows from the route:

- **CS2DemoKit owns the semantics.** The rules language, the providers, the `$round_end` binding and
  the shipped `round_facts.rules.yaml` are engine artefacts. The ruleset should ship **from
  `CS2DemoKit.Analysis`** and be mirrored into `rules/` byte-identically, exactly as `kast`,
  `player_stats`, `weapon_stats` and `post_plant_double` are today (`README.md` "Rules"), so a fix
  to the classifier reaches every consumer of the package and the app's copy is a pin, not a fork.
  The proposal to ship it upstream goes with #54's implementation, not before it.
- **The app owns the cache and the read API.** Rows are an analysis output, so they live in the
  **Analysis tier** of `DemoCacheRecord`, not in a new Parse-level tier: `RoundFactsRows`
  (`List<RoundFacts>` plus the clock header) with its own fingerprint `RoundFactsFingerprint`, the
  resolved-identity hash of the effective `round_facts` ruleset (the hash the engine already
  computes for `ConfigFingerprint`, restricted to this one ruleset) plus `RoundFactsSchema`.
  Invalidation therefore follows the thing that changes the answer: a user editing a threshold
  changes the fingerprint and re-runs only this evaluator (one small ruleset on the forward path),
  never the full highlight scan and never the roster parse. `DemoCacheIndexEntry` mirrors the
  fingerprint so `NeedsRoundFacts(currentFingerprint)` derives from the index like `NeedsAnalysis`.
- The clock header is the annotation sidecar's (`docs/playback2d-v2/annotations-format.md:24-25`):
  `{ "kind": "dv-frame-clock", "tickRate", "frameCount", "firstTick", "lastTick" }`, plus
  `DemoSha256` (null until Content Identity fills `DemoCacheRecord.Sha256`; the join key is
  `StableKey` until then, and the rows are rebuilt from the parse regardless).
- Size: 24 rounds × (two sides plus ~7 kills) ≈ 15-25 KB indented JSON per sidecar; a 719-demo
  library adds about 15 MB on disk. The integrator's rule (correction 1) puts a payload this size
  inside the record rather than in a sibling sidecar; Match Overview reads it.
- Browser host: the store is in-memory when `AppPaths.ConfigRoot` is null; round facts follow the
  "Demo library / cache / bookmarks: degraded, session only" row of
  `docs/playback2d-v2/wasm-matrix.md:119`, and a row naming Round Facts is added when it lands. The
  evaluator runs wherever a parse runs; no feature gate.
- D2 (JSON versus SQLite) is untouched here. If The Round Index chooses SQLite at its review, that
  design carries round facts into its file as a table built from these rows.

### 3.6 The record

One `RoundFacts` per `ClipRound`, two `SideFacts` inside it, a kill timeline shared by both sides,
and no per-player rows. All ticks are **frame clock**. Null means "the demo did not say";
`Unavailable` in a `Source` means "the engine did not provide it".

```csharp
public sealed class RoundFacts
{
    public int Number;                 // == ClipRound.Number; the join key with CachedRound
    public int? MatchRoundNumber;      // total_rounds_played at freeze end + 1
    public RoundHalf Half;             // First, Second, Overtime; from game_phase and MatchRoundNumber > regulation_rounds
    public bool IsLive;                // false for a freeze-end with no end and no next freeze-end (truncated or warmup)

    public int FreezeEndTick;          // == CachedRound.StartTickFrameClock (asserted)
    public int? FirstContactTick;      // first enemy player_hurt, or the opening kill if earlier
    public int? OpeningKillTick;       // first enemy player_death
    public int? PlantTick;
    public BombSite PlantSite;         // A, B, Unknown (place null on this map or before first networking)
    public int? PlantSiteEntity;       // raw Site, for Zone Baking to cross-check
    public int? PlanterSlot;
    public int? DefuseTick;
    public int? ExplodeTick;
    public int? EndTick;               // $round_end (win-status transition); else round_officially_ended
    public RoundEndSource EndSource;   // WinStatus, OfficiallyEndedEvent, None
    public RoundEndReason EndReason;   // the m_eRoundWinReason enum; Unknown when only the late event was available
    public int WinnerSide;             // 2, 3 or 0
    public int? RoundTimeSeconds;
    public SideFacts Ct;
    public SideFacts T;
    public List<KillStep> Kills;       // tick order, live window only
    public Dictionary<string, object?> Extra;   // user-added columns (§3.3)
    public IReadOnlyDictionary<string, string> Sources;   // per column: Engine | Unavailable
    public List<string> ProjectionWarnings;
}

public sealed class SideFacts
{
    public int Side;                   // 2 = T, 3 = CT
    public int[] Slots;                // controllers on this side at freeze end; the Team Identity join (correction 9)
    public int ScoreBefore;
    public int PlayersAtFreezeEnd;     // n
    public int EquipmentFreezeEnd;     // E
    public int? MoneyAtFreezeEnd;      // M; null when not reliable
    public bool MoneyReliable;
    public BuyType BuyType;            // Pistol, Eco, Semi, Force, Full, Unknown
    public bool WonPreviousRound;
    public BuyThresholds Thresholds;   // the parameter values that produced BuyType
}

public sealed class KillStep
{
    public int Tick;
    public int VictimSlot;
    public int AttackerSlot;           // -1 when absent (fall, bomb, world)
    public int VictimSide;
    public int CtAlive;                // after this kill
    public int TAlive;
}
```

`RoundEndReason` is the `m_eRoundWinReason` value with the names every parser uses
(demoinfocs-golang `RoundEndReason`): 1 TargetBombed, 7 BombDefused, 8 CTWin, 9 TerroristsWin,
10 Draw, 12 TargetSaved, 16 GameStart, 17 TerroristsSurrender, 18 CTSurrender, plus the hostage and
VIP values, which never occur in defusal. Observed and cross-checked: 7 on a defused round, 8 and 9
on elimination rounds, 12 on a time-out CT win.

**`Half` is defined, not observed.** `First` when `MatchRoundNumber <= regulation_rounds / 2`,
`Second` when `<= regulation_rounds`, `Overtime` otherwise; `game_phase` is stored raw beside it
for anyone who wants the engine's own value. This replaces the first draft's "Unknown in overtime":
the definition needs no overtime demo to be correct, and the parameter makes MR15 leagues a
one-line override.

**Phase boundaries are derived, not stored.** A static `RoundPhases.At(RoundFacts, int tick)`
returns one of:

| Phase | Interval (frame clock, half-open) | Note |
|---|---|---|
| `Freeze` | `[previous EndTick or round_officially_ended, FreezeEndTick)` | buy time |
| `Opening` | `[FreezeEndTick, OpeningKillTick)` | no kill yet; `FirstContactTick` is stored separately because damage precedes the kill in every measured round |
| `MidRound` | `[OpeningKillTick, PlantTick ?? EndTick)` | after the opening kill, before a plant |
| `PostPlant` | `[PlantTick, EndTick)` | from the T side's point of view |
| `Retake` | the same interval as `PostPlant` | from the CT side; a consumer asks with a side, or gets `PostPlant` when it does not |
| `PostRound` | `[EndTick, next FreezeEndTick)` | the 7 s win panel; kills here are real and land in `Kills` with this phase |
| `Warmup` / `None` | before the first freeze end, or a round with `IsLive == false` | |

A round with no opening kill runs `Opening` to `PlantTick` or `EndTick`. A round with no
`EndTick` (truncated demo) runs its last phase to the last frame. Integrator correction 11: every
other design takes round bounds and alive state from here.

### 3.7 Exposure

`IRoundFactsSource` (in `DemoViewer.NET.dll`, `Services/RoundFacts/`), delegate-injected the way
`HighlightsModule` receives the cache store (`src/App/DemoViewer.NET/Modules/Highlights/HighlightsModule.cs`):

```csharp
public interface IRoundFactsSource
{
    RoundFactsRows? TryGet(string demoPath);                          // one sidecar read, cached by the store's capacity-1 record cache
    RoundFacts? RoundAt(string demoPath, int frameClockTick);         // by ClipRound window
    IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter);   // cross-demo; off the UI thread
    IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null);   // the parser-namespace projection (correction 10)
    event Action<string>? Updated;                                    // a demo's rows were (re)written
}
```

**The fact vocabulary is this design's, absolute per side, one list** (integrator correction 10).
`FactsFor` returns, per round, with values from the record:

| Group | Labels |
|---|---|
| `side` | `ct.slots`, `t.slots` |
| `buy` | `buy.ct`, `buy.t` (`Pistol` … `Full`), `equipment.ct`, `equipment.t`, `money.ct`, `money.t`, `moneyReliable.ct`, `moneyReliable.t` |
| `score` | `score.ct`, `score.t` (before the round), `matchRound`, `half` |
| `end` | `end.reason`, `end.winnerSide`, `end.tick`, `roundTime` |
| `plant` | `plant.site`, `plant.tick`, `plant.slot`, `defuse.tick`, `explode.tick` |
| `contact` | `firstContact.tick`, `openingKill.tick` |
| `phase` | `phase` at `atTick` (via `RoundPhases.At`), `manCount.ct`, `manCount.t` at `atTick` |
| `extra` | every `Extra` column as `<column>` |

Values are absolute (`ct`/`t`, never `us`/`them`); Team Identity turns them relative for display.
The Strat Model's economy enum takes these five buy values (correction 10).

| Consumer | What it reads | Where |
|---|---|---|
| Search Filters And Live Count | `RoundFactsFilter { Sides, BuyTypeBySide, Phase, ClockBand, ManCount (CtAlive, TAlive at a tick), ScoreBefore, EndReason, PlantSite, Extra equalities }`; the live count is `Query(filter).Count` intersected with the index token hits | `Query` |
| Result Cards And Walking | `ScoreBefore` both sides, `BuyType` both sides, `EndReason` icon, `EndTick - FreezeEndTick` for the clock | `TryGet` |
| Free Labels From Round Facts | `FactsFor(demo, round, instance.fromTick)` into the tag's `facts` namespace | `FactsFor` |
| The Round Index | foreign key `(StableKey or DemoSha256, RoundFacts.Number)`; `Slots` for side; `Kills` for alive; `RoundPhases.At` per sample; `RoundFactsFingerprint` in its own fingerprint | `TryGet` at index-build time |
| Opponent Dossier | stratification keys: `BuyType` per side, `PlantSite`, `[PlantTick, EndTick)` for retakes, `Kills` for man-advantage states, `Half`, pistol rounds | `Query` |
| Create Strat From Round | `Slots` per side, `BuyType`, `PlantSite`, `FreezeEndTick` as the step clock's zero | `RoundAt` |
| Strat Model evidence | `winner`, `roundTime`, `plantTick` groups | `FactsFor` |
| `RoundTrack` (decision 6) | `WinnerSide` per round for the band tint, replacing the `round_end` read that never fires on Valve demos | `TryGet` + `Updated` |

Team Identity joins on `SideFacts.Slots` against the record's `Players` (slot → SteamID);
`SideAtRound` lives on `TeamIdentityService` (correction 9). Nothing in Round Facts names a team.

### 3.8 Alignment with `ClipRound.StartTickFrameClock`

`RoundFacts.Number == ClipRound.Number` and `RoundFacts.FreezeEndTick ==
ClipRound.StartTickFrameClock` by construction: both come from the same `round_freeze_end` event's
`GameTick`, the first through `ClipRounds.Derive`, the second through `event.frame_tick` (#54 item
4). The measured `m_bFreezePeriod` 1→0 transition lands on that same tick, so an entity-derived
value would agree too. A test pins the equality on a real demo (§7).

### 3.9 User customisation and extension

- **Change a threshold:** copy `round_facts.rules.yaml` to `<config>/rules/`, edit a `params:`
  default, save. `RuleSetLocator` replaces the shipped ruleset by id; the fingerprint changes; the
  Library re-runs `roundfacts` on affected demos in the background; every consumer sees the new
  classes on its next read via `Updated`.
- **Add a fact:** add a `stats:` entry and a column to the table in the same override. It appears
  in `Extra`, as a `parser.<column>` label on every tag instance, and as an equality filter in
  Situation Search. No app change.
- **Guard rails:** user-tier errors are collected and shown in the Rule Workbench, not thrown; the
  Workbench validates against the schema as it does today. A malformed override leaves the shipped
  ruleset's last cached rows in place (fingerprint unchanged because the override failed to load)
  and the Workbench says why.

---

## 4. Alternatives considered

- **App-side walk in the tier-2 pass** (the first draft's recommendation). Rejected:
  it duplicates engine knowledge in C#, is invisible to users, and cannot be extended without a
  build. Its measured cost (73 to 308 ms marginal) is kept in §2.7 as the budget the ruleset
  evaluation is held to.
- **Engine API (`RoundFacts.Derive`) instead of a ruleset.** Subsumed: the ruleset gets the same
  facts through general providers that also serve every other ruleset, and stays editable.
- **Folded `player_round` table on 0.12.0 without #54.** Rejected (§3.1): no end reason,
  server-clock ticks, final-side `team` dimension, snapshot-mode run, ten rows per round.
- **Per-player rows instead of per-side.** Rejected: the engine already has `player_round_stats`,
  and the consumers all ask per side. `Slots` gives Team Identity what it needs.
- **A separate Parse-level tier.** Rejected (§3.5): the rows are an analysis output and their
  invalidation key is the ruleset's identity, which the Analysis tier already models.
- **Key rounds by `m_totalRoundsPlayed` instead of `ClipRound.Number`.** Rejected (correction 12).
- **Thresholds as app settings instead of ruleset parameters.** Rejected: one customisation surface
  (the user rules directory) rather than two, and the parameters travel with the ruleset a team
  shares.

---

## 5. External and engine changes required

**Required: CS2DemoKit #54** (filed 2026-09-23, one issue for the work stream, decisions 1 and
7), six items: a team subject on the forward path with `round.team.slots`; game-rules providers
(`round_win_status`, `round_win_reason`, `total_rounds_played`, `game_phase`, `bomb_planted`,
`round_time`); a real `$round_end` on the GOTV profiles from the win-status transition;
`event.frame_tick`; `round.team.money`; `enrich.round.plant_site`. The issue also notes the
`round_won` / `round_lost` binding bug. The build of this item starts when a CS2DemoKit release
closes #54 and `Directory.Packages.props` pins it; Gate 0 → 1 asks for this design approved, not
built.

**Proposed with #54's implementation, not before:** ship `round_facts.rules.yaml` from
`CS2DemoKit.Analysis` and mirror it in `rules/` byte-identically (§3.5).

**Not required:** the AssetBaker bundle schema, the CSVG plugin, CS2DemoKit #53 (delta user
commands; unrelated to this record).

---

## 6. Risks, and what is assumed

Every item is a stated assumption with its fallback or a verification task owned by a named work
item. None is an open question of this design.

| # | Assumption or risk | How it is handled |
|---|---|---|
| A1 | #54 lands as six surfaces, possibly under other names. | The ruleset is the adapter; the record does not change. A partial delivery yields null columns marked `Unavailable` (§3.4); nothing falls back to a walk. |
| A2 | The forward-path evaluation of one small ruleset costs no more than the walk it replaces. | Budget: under 1.5 × the Library's CCSTeam replay on the §2.7 demos, asserted loosely (§7). If the engine misses it, the finding goes to #54 as a follow-up; the design does not change. |
| A3 | Money reads can be implausible (build 10231, round 1). | `money_sane_max` guard; `Force` degrades to `Semi` with `MoneyReliable = false`; the three equipment classes never touch money. |
| A4 | Overtime and half boundaries. | Defined from `MatchRoundNumber` and `regulation_rounds` (§3.6); never `Pistol` in overtime; `game_phase` stored raw. MR15 leagues set the parameter. |
| A5 | HLTV, FACEIT and POV demos differ in events (`round_end` present), tick rate (128) and place coverage. | The engine's source profiles own event differences; thresholds are in dollars; ticks scale through the clock header. Verification per source is a test row of **Inputs Per Demo Source** (plan §3), which re-acquires one demo per source; until it runs, every claim here is stated for Valve matchmaking. |
| A6 | Demos with warmup freeze-ends, knife rounds or restarts number rounds differently from the scoreboard. | `IsLive` and `MatchRoundNumber` are facts; consumers filter on `IsLive` and display `MatchRoundNumber`. |
| A7 | A truncated last round has no end. | `EndTick` null, `EndSource = None`; the last phase runs to the last frame; Result Cards render "unfinished". |
| A8 | Mid-round disconnects and 4v5 starts. | `n` is per side at freeze end; `Slots` is what was there; thresholds scale by `n`. |
| A9 | `m_szLastPlaceName` null on some maps or before first networking. | `PlantSite = Unknown`, `PlantSiteEntity` kept; Zone Baking resolves the entity index later. Coverage is **Place Names From The Pawn**'s table. |
| A10 | Content Identity is not merged when this builds. | `DemoSha256` nullable in the header; the join key is `StableKey` until then; rows rebuild from the parse. |
| A11 | A user override breaks the ruleset. | User-tier errors are contained; the last good rows stay; the Workbench reports (§3.9). |
| A12 | Sidecar growth (about +20 KB per demo). | Measured budget; inside the record per correction 1. |

---

## 7. Test and verification strategy

**Unit, synthetic (App test suite, no demo).**
- `BuyTypeClassifierTests` over the projection: a table of `(side, n, E, M, reliable, lostPrevious,
  matchRound, params)` → class, covering every boundary (`eco_max`, `full_min` per side,
  `force_money_max`), n from 1 to 5, both pistol rounds at `regulation_rounds` 24 and 30, overtime
  never pistol, unreliable money never `Force`.
- `RoundPhasesTests`: every interval in §3.6 with and without plant, kill, end.
- `RoundFactsProjectionTests`: a synthetic `round_facts` table (two rows per round) → record;
  disagreeing round-level columns → `ProjectionWarnings`; a missing column → `Unavailable`; an
  unknown column → `Extra`; mismatched `keep: list` lengths → warning, not throw.
- `RoundFactsCacheTests`: a record with Analysis and no rows reports `NeedsRoundFacts`; a changed
  fingerprint reports it again; an old sidecar without the field deserializes; the index mirror
  agrees with the sidecar (the `DemoCacheTier2Tests` shape).
- `ShippedRulesetPinTests`: `rules/round_facts.rules.yaml` is byte-identical to the package copy,
  the way the four existing mirrored rulesets are pinned.
- `UserOverrideTests`: an override in a temp rules directory changing `eco_max_per_player` changes
  the fingerprint and the class of a known row; a malformed override leaves the fingerprint and rows
  unchanged and produces a diagnostic.

**Real demo, `[Category("RealDemo")]`, gated by `DemoTestHelper.RequireDemo()`
(`src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs:208`, skips without `DEMO_PATH`).**
- `FreezeEndTick == ClipRounds.Derive(...)[i].StartTickFrameClock` for every round, and the counts
  agree.
- Every live round has `EndTick`, `WinnerSide in {2,3}`, `EndReason != Unknown`, and
  `EndTick + 7 s == round_officially_ended` tick within one frame.
- Every planted round has `PlantSite != Unknown` and `PlantTick == bomb_planted GameTick`.
- `Kills`: alive counts are monotonic non-increasing per side within a round and start at
  `PlayersAtFreezeEnd`.
- `ScoreBefore` of round `i+1` equals round `i`'s score plus the winner's increment, accounting for
  the halftime swap; the last round's score plus increment equals the Library's `ExtractFinalScore`.
- The §3.2 worked checks (Dust2 rounds 2, 3, 5; Nuke round 8) hold with the default parameters.
- Budget: evaluation time on the test demo under 1.5 × the CCSTeam-only replay, asserted loosely
  (bench variance: quote plain mode, do not gate CI on a tight number).

**Manual.** Open a matchmaking demo, confirm the timeline's round bands tint after the `RoundTrack`
change, that Match Overview still renders from cache alone, and that editing `eco_max_per_player`
in the user rules directory re-tints nothing but re-classifies the Stats view's round rows.

---

## 8. Decisions, and the answers (2026-09-23)

| # | Decision | Answer | Where it lands |
|---|---|---|---|
| 1 | D3, the route | **Rules-driven.** File one CS2DemoKit issue for the engine pieces; design as if they land. | §3.1, §5; CS2DemoKit #54 |
| 2 | `Semi` as a class | **Keep it.** | §3.2 |
| 3 | `Force` on money after a loss | **Keep, with the reliability guard.** | §3.2, A3 |
| 4 | Full-buy threshold | **HLTV's bands by default**; users edit the ruleset to change the classification. | §3.2 (`params:`), §3.9 |
| 5 | Which layer owns the tier | **Reviewed and chosen:** CS2DemoKit owns the semantics and ships the ruleset; the app owns the cache (Analysis tier, keyed by the ruleset's resolved identity) and the read API. | §3.5 |
| 6 | `RoundTrack` tint | **Fix it here.** | §3.7 |
| 7 | Upstream filings | **One issue covering the whole work stream**, filed now. | §5; CS2DemoKit #54 |

No decisions remain open on this design.

---

## 9. Effort estimate and sequencing

Waits on a CS2DemoKit release that closes #54 and a pin bump. After that:

| Step | Effort | Depends on |
|---|---|---|
| Author `round_facts.rules.yaml` against the delivered surfaces; propose it upstream; pin the mirror | 1 day | #54 released |
| `RoundFactsEvaluator`, projection, `BuyThresholds`, `RoundPhases`, unit tests | 1.5-2 days | ruleset |
| Analysis-tier rows, fingerprint, index mirror, backfill via `Wants` | 1 day | evaluator |
| `IRoundFactsSource` incl. `FactsFor`, composition-root wiring, `RoundTrack` tint | 1 day | rows |
| Real-demo tests, override tests, wasm-matrix row | 0.5-1 day | all above |

About one working week after the engine lands. It does not block on Content Identity (the hash
slot is nullable) and does not block on Team Identity (the record carries slots, not teams). It
should land before The Round Index is built (that design reads `RoundFacts.Number`, `Slots`,
`Kills`, `RoundPhases.At` and carries `RoundFactsFingerprint`); The Round Index's "call the
extractor when the tier is absent" fallback (integrator §2, row "The Round Index (build)") is
**withdrawn** by this revision: there is no extractor, and the index requires the rows.

While #54 is open: nothing here is blocked for the other designs, which build on the record shape
(§3.6) and the label list (§3.7), both settled by this revision.

---

## 10. Sources

**Upstream.** CS2DemoKit issue #54, "Rules: six pieces needed to derive per-round, per-side round
facts from a shipped ruleset" (2026-09-23), which carries the §2.7 measurements.

**Tree, `main` at `d90ec9f`.**
- `plan.md` §2 (F5, F9, F14, F15, F17), §3 Round Facts, §6 D2, D3.
- `docs/strat-book/designs/00-overview.md` corrections 1, 9, 10, 11, 12.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs` (`:14-18`, `:55`, `:84-89`,
  `:203-206`, `:216`, `:226-234`, `:262`, `:326`, `:337-366`, `:378-419`, `:435`).
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs` (`:59`, `:200`, `:301`, `:340`,
  `:367`, `:407`, `:500`, `:559`).
- `src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs` (`:47`, `:56-59`, `:1138`,
  `:1176`, `:1244`, `:1274`, `:1302`, `:1362`, `:1393`, `:1403-1445`).
- `src/App/DemoViewer.NET/Modules/Highlights/HighlightScanService.cs` (`:609` onward).
- `src/App/DemoViewer.NET/Services/DemoProcessing/IDemoEvaluator.cs` (`:22`).
- `src/App/DemoViewer.NET/ViewModels/Stats/StatsTabViewModel.cs` (`:473-490`, `:704-715`, `:1243`).
- `src/App/DemoViewer.NET/Modules/Playback2D/Timeline/RoundTrack.cs` (`:23`, `:99-140`).
- `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs` (`:1049`).
- `src/App/DemoViewer.NET/Modules/Highlights/HighlightsModule.cs` (delegate injection precedent).
- `src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs` (`:208`).
- `rules/cs2demokit-rules.schema.json` (`:24-31`, `:638-644`, `:1594-1630`).
- `docs/rules-v2/rules-v2-spec.md` §4 (namespace tree, `for:` × `per:`).
- `docs/plugins/plugin-system-design.md` §1.2 (`RuleSetLocator`, `MergeById`, user-tier errors).
- `README.md` "Rules" (the four rulesets mirrored byte-identically from the package).
- `docs/playback2d-v2/annotations-format.md` (`:24-36`, the `clock` block).
- `docs/playback2d-v2/wasm-matrix.md` (`:119`).
- `rules/player_stats.rules.yaml` (`round_won` guarded by `where:`), `rules/post_plant_double.rules.yaml`
  (`params:`, `capture: event.tick`, `keep: list`).

**CS2DemoKit 0.12.0 packaged XML docs** (`~/.nuget/packages/cs2demokit.analysis/0.12.0/lib/net10.0/CS2DemoKit.Analysis.xml`,
`cs2demokit.parser/0.12.0/.../CS2DemoKit.Parser.xml`): `ClipRound`, `ClipRounds`, `ClipWindows`,
`PlayerRoundStatsProjector`, `ConfiguredOutputProjector`, `OutputScope`, `PlayerEconomyFreezeEndEdge`,
`RoundEndEnrichmentEdge`, `Cs2GotvProfile`, `Cs2GotvPreRestartProfile`, `FreezePeriodProvider`,
`DemoAnalysis`, `AnalysisOptions.CaptureSnapshots`, `PositionSample`, `GameEvent`, `DemoFrame.GameTick`.
Catalog dumped with `CatalogResource.Load()`: events `bomb_planted` (`C4, Site, UserId, UserIdPawn`),
`round_end` (`Legacy, Message, NoMusic, PlayerCount, Reason, Winner`), views `round_won` / `round_lost`
(event `round_end`, binding team, facets `has_winner, winner_side, winner_team`), enrichments
`enrich.round.*`, contexts `round.team.*`, `round.enemies.*`, `round.bomb.was_planted`, `match.phase`.

**CS2DemoKit source at `origin/main`** (read with `git -C C:\dev\CS2DemoKit show origin/main:<path>`):
`src/CS2DemoKit.Parser/Entities/Generated/SchemaLens.Generated.cs` lines 173-203, 224-237, 243-286,
301-302 (the lensed fields); `src/CS2DemoKit.Analysis/Plugins/PawnEquipmentValueProvider.cs:19`
("an equipment value of 0 (eco / save round) is a legitimate observation").

**Measurements.** Scratch project
`round-facts`
(`Program.cs` modes `events`, `walk`, `replaycost`, `catalog`, `rules`; `round_facts_probe.rules.yaml`,
`match_scope_probe.rules.yaml`, `match_scope_round_table_probe.rules.yaml`), run 2026-09-23 against
`C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\replays\match730_003731893271710924851_1024675027_129.dem`
(de_nuke, build 10231) and `...\match730_003844252717140672725_0377894676_389.dem` (de_dust2, build
10896). Release build, warm figures quoted.

**Buy-type references.**
- HLTV economy legend: `https://www.hltv.org/stats/matches/economy/mapstatsid/203904/supernova-comets-vs-full-house`
  (returns 403 to a direct fetch; the bands "full eco [0-5k], semi-eco [5-10k], semi-buy [10-20k],
  full buy [20k+]" are as quoted by search results for that page). These are the defaults.
- CS Demo Manager classifier: `github.com/akiver/cs-demo-analyzer`, `pkg/api/economy.go`
  (`computePlayerEconomyType`, `computeTeamEconomyType`) and `pkg/api/constants/economy.go`
  (`pistol`, `eco`, `semi`, `force-buy`, `full`), read 2026-09-23 through the GitHub API. The
  `Force` rule and the optional CT/T split come from here.
- Leetify: `https://leetify.com/blog/leetify-rating-update/` ("1 of 4 economic groups" by
  per-player equipment value, CT and T differ; a full-buy round is "both teams having over 20k in
  equipment value") and `https://leetify.com/blog/understanding-csgo-economy/` (definitions without
  thresholds).
- `RoundEndReason` names: `github.com/markus-wa/demoinfocs-golang`, `pkg/demoinfocs/events/events.go`
  lines 55-74.
