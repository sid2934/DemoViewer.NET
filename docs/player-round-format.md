# The `player_round` facet (format 1, schema 1)

One row per player per round of a demo, with the side the player was on, so the Player Overview can split
K/D, ADR and KAST between CT and T across the library. It is the one table of the core stamped ruleset
[`rules/player_round.rules.yaml`](../rules/player_round.rules.yaml), written by the facts pass on the
library's background visit. This document is for anyone who reads or writes it. A committed sample lives at
[`tests/fixtures/player-round/player_round.sample.facts.json`](../tests/fixtures/player-round/player_round.sample.facts.json)
(the first two rounds of a benchmark demo, accounts and names replaced by slot) and is pinned by
`PlayerRoundFacetTests.V1Columns_MatchTheCheckedInSample`. Regenerate it with `PLAYER_ROUND_SAMPLE_UPDATE=1`
through `PlayerMatchParityRealDemoTests` and a `DEMO_PATH`.

## Where it lives

| Condition | Location |
|---|---|
| Desktop | `<app config root>/cache/demos/<key>.player_round.facts.player_round.json.gz`, beside the demo's record. |
| Browser build | The in-memory cache; nothing persists. The Player Overview is Desktop only. |

The sidecar uses the facts envelope every stamped ruleset's table uses: gzipped JSON, `format: 1`, with
`ruleset`, `output`, `fingerprint`, `schema`, `grain` (`player_round`), `dimensions`, `values`, `clocks` and
`rows`, each row `{ "d": { dimension: cell }, "v": { column: cell } }`. Read it through
`IAnalysisFacts.TryGet(path, new FactKey("player_round", "player_round"))`; the tab never runs the engine.
A 24-round demo holds about 240 rows, a few KB gzipped.

## Stamp and invalidation

The record carries a `facts:player_round` stamp (mirrored on the index row) at schema `1` under the
ruleset's own fingerprint: `MergedRulesBuild.RulesetIdentity` over the ruleset's source and the engine
version, folded with `StampedFacts.Schema`. It has no `use:`, so only an edit to this file or an engine bump
moves it, and either re-walks only this facet's backlog. The `player_match` and `player_event` stamps stay
current.

* **Indexed**: the sidecar is current.
* **Failed**: the build left the ruleset out, for example a user-tier copy with `enabled: false`.

`player_round` is app-owned like `player_match`. It is not in the CS2DemoKit package, so re-extracting
`rules/` leaves it alone; `PlayerMatchParityRealDemoTests` should pass after every engine bump.

## Dimensions

| Dimension | Meaning |
|---|---|
| `match_id` | Declared by the engine, never filled. Absent from the rows. |
| `map` | The map name. |
| `round_number` | The round, from 1. |
| `player_slot` | The player's slot. |
| `player_name` | As in `player_match`: not reliable on a forward run. Take names from the roster. |
| `steam_id` | The player's SteamID64 as a decimal string, by the final roster's account on the slot; null or `0` for a bot. |
| `team` | The final roster team, the same on every round. Use `side` for the round's side. |

## Columns

| Column | Meaning | Adds up to (`player_match`) |
|---|---|---|
| `side` | The slot's team at the round's freeze end: 3 CT, 2 T, 0 when the slot was on no side. | `CTW + CTL` rounds on 3, `TW + TL` on 2 |
| `kills` | Enemy kills. | `TotalK` |
| `deaths` | Deaths while the match is live. | `TotalD` |
| `assists` | Enemy assists, flash assists excluded. | `TotalA` |
| `damage` | Enemy damage, capped at the victim's health. | `EnemyDmg` |
| `survived` | 1 when the player was alive at the round's end. | `Survived` |
| `kast` | `true` when the round counts for KAST: a kill, an assist on an enemy kill, survival or a trade. | `KASTRounds`, or one more |

Every count is its `player_match` column's definition at `per: round`, so an account's rows add up to the
match column exactly. `kast` is the `kast` ruleset's `has_kast` round for round. `KAST%` is a match-end
compute and the last round's survival lands after it, so the rounds `kast` holds can be one more than
`KASTRounds` when the last round counted on survival alone; the Stats tab shows the smaller number.

`side` is read off the player, not joined from `round_facts`. On every slot `round_facts` lists at a freeze
end the two agree; a player gone for a round (disconnected, slot still on a team) is on no `round_facts`
side but keeps the team's `side` here, so each account's sides cover the rounds `CTW/CTL/TW/TL` count.
`PlayerMatchParityRealDemoTests` pins both, the sums above and `kast` against the retained run's per-round
`HasKAST` (`PlayerRoundStatsProjector`).

## Splits by side

`PlayerRoundFacet.Sides` sums an account's rows by side into `CT:rounds`, `CT:kills`, … `T:kast`. A row on
no side counts nowhere. A reconnect splits an account over two slots: their rows are summed, a round counts
once per side, and `kast` holds when either slot had it. The Player Overview divides the sums: ADR is damage
over rounds on the side, KAST% the `kast` rounds over rounds on the side, K/D kills over deaths. A demo whose
`player_round` facet is not written yet adds nothing to either side.
