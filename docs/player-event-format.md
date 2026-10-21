# The `player_event` facet (format 1, schema 1)

Every kill and every death of every player in a demo, so the Player Overview can count what two accounts
did to each other across the library. It is the two tables of the core stamped ruleset
[`rules/player_event.rules.yaml`](../rules/player_event.rules.yaml), written by the facts pass on the
library's background visit. This document is for anyone who reads or writes them. Committed samples live at
[`tests/fixtures/player-event/player_kills.sample.facts.json`](../tests/fixtures/player-event/player_kills.sample.facts.json)
and [`player_deaths.sample.facts.json`](../tests/fixtures/player-event/player_deaths.sample.facts.json) (the
first two rounds of a benchmark demo, accounts and names replaced by slot) and are pinned by
`PlayerEventFacetTests.V1Tables_MatchTheCheckedInSamples`. Regenerate them with
`PLAYER_EVENT_SAMPLE_UPDATE=1` through `PlayerEventRealDemoTests` and a `DEMO_PATH`.

## Where it lives

| Condition | Location |
|---|---|
| Desktop | `<app config root>/cache/demos/<key>.player_event.facts.player_kills.json.gz` and `….player_deaths.json.gz`, beside the demo's record. |
| Browser build | The in-memory cache; nothing persists. The Player Overview is Desktop only. |

Both sidecars use the facts envelope every stamped ruleset's table uses: gzipped JSON, `format: 1`, with
`ruleset`, `output`, `fingerprint`, `schema`, `grain` (`player_event`), `dimensions`, `values`, `clocks`
(`tick` is on the frame clock) and `rows`, each row `{ "d": { dimension: cell }, "v": { column: cell } }`.
Read them through `IAnalysisFacts.TryGet(path, new FactKey("player_event", "player_kills"))` and
`"player_deaths"`; the tab never runs the engine.

## Stamp and invalidation

The record carries a `facts:player_event` stamp (mirrored on the index row) at schema `1` under the
ruleset's own fingerprint: `MergedRulesBuild.RulesetIdentity` over the ruleset's source and the engine
version, folded with `StampedFacts.Schema`. It has no `use:`, so only an edit to this file or an engine bump
moves it, and either re-walks only this facet's backlog. The `player_match` stamps stay current: the two
facets are separate stamped rulesets so a later table never invalidates the first one.

* **Indexed**: both sidecars are current.
* **Failed**: the build left the ruleset out, for example a user-tier copy with `enabled: false`.

`player_event` is app-owned like `player_match`. It is not in the CS2DemoKit package, so re-extracting
`rules/` leaves it alone; `PlayerEventRealDemoTests` should pass after every engine bump.

## Dimensions

Both tables carry the engine's per-player event dimensions, one row per firing of the table's trigger.

| Dimension | Meaning |
|---|---|
| `match_id` | Declared by the engine, never filled. Absent from the rows. |
| `map` | The map name. |
| `round_number` | The round the event fired in. |
| `tick` | The event's tick on the frame clock. A kill and its death carry the same tick. |
| `player_slot` | The row's subject: the killer in `player_kills`, the victim in `player_deaths`. |
| `player_name` | As in `player_match`: not reliable on a forward run. Take names from the roster. |
| `steam_id` | The subject's SteamID64 as a decimal string, by the final roster's account on the slot; null or `0` for a bot. |
| `team` | The subject's final roster team, as in `player_match`. |

## Columns

| Table | Column | Meaning |
|---|---|---|
| `player_kills` | `victim_slot` | The victim's slot (`player_death.userid`). |
| `player_kills` | `weapon` | The weapon as the event names it (`ak47`, `usp_silencer`, `hegrenade`). |
| `player_kills` | `headshot` | 1 for a headshot kill, else 0. |
| `player_deaths` | `attacker_slot` | The killer's slot (`player_death.attacker`). A suicide names the victim's own slot. |

A kill row exists for every kill a player made, team kills included: per account, the kill rows equal
`player_match`'s `TotalK` plus `TeamKills`. A death with no killer (the world, a fall, the bomb) has a death
row and no kill row.

## Naming both accounts

The rules language has no SteamID for the other side of an event, only its slot, so neither row names both
accounts. `PlayerEventFacet.Kills` pairs each kill row with the death row of the same `tick`, killer slot
(`player_slot` / `attacker_slot`) and victim slot (`victim_slot` / `player_slot`). Both rows come from the
one `player_death`, so the pair is exact even for a collateral, and the result is one `PlayerKill` per kill:
round, tick, killer and victim SteamID64, weapon and headshot. A bot victim, or a kill whose death row is
missing, keeps a null victim.

## Head-to-head

The Player Overview's head-to-head reads both tables of every demo the subject and the chosen opponent
ended on opposite sides (by the index row's side lists, so a demo either of them left early is not one of
them), and counts the kills each way. The match record comes from the subject's `player_match` rows, not
from this facet.
