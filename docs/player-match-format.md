# The `player_match` facet (format 1, schema 1)

One row per player slot per demo: the match totals and the components of every rate the Player Overview
pools across demos. It is the table of the core stamped ruleset [`rules/player_match.rules.yaml`](../rules/player_match.rules.yaml),
written by the facts pass on the library's background visit. This document is for anyone who reads or
writes it. A committed sample lives at [`tests/fixtures/player-match/player_match.sample.facts.json`](../tests/fixtures/player-match/player_match.sample.facts.json)
(two rows of a benchmark demo, accounts and names replaced) and is pinned by
`PlayerMatchFacetTests.V1Columns_MatchTheCheckedInSample`. Regenerate it with
`PLAYER_MATCH_SAMPLE_UPDATE=1` through `PlayerMatchParityRealDemoTests` and a `DEMO_PATH`.

How each column pools across demos is in [`player-overview.md`](player-overview.md#aggregation-rules).

## Where it lives

| Condition | Location |
|---|---|
| Desktop | `<app config root>/cache/demos/<key>.player_match.facts.player_match.json.gz`, beside the demo's record. |
| Browser build | The in-memory cache; nothing persists. The Player Overview is Desktop only. |

The sidecar is the facts envelope every stamped ruleset's table uses: gzipped JSON, `format: 1`, with
`ruleset`, `output`, `fingerprint`, `schema`, `grain` (`player_match`), `dimensions`, `values`, `clocks`
(empty here) and `rows`, each row `{ "d": { dimension: cell }, "v": { column: cell } }`. A number with a
fraction is written with a decimal point and a whole number without one, so the two read back as they were
written. Read it through `IAnalysisFacts.TryGet(path, new FactKey("player_match", "player_match"))`; the
tab never runs the engine.

## Stamp and invalidation

The record carries a `facts:player_match` stamp (mirrored on the index row) at schema `1` under the
ruleset's fingerprint: `MergedRulesBuild.RulesetIdentity` over the ruleset and its `use:` closure
(`player_stats`, `kast`), their sources and the engine version, folded with `StampedFacts.Schema`. Any edit
to those three rulesets, or an engine bump (which re-extracts `player_stats` and `kast` from the package),
makes every demo stale, and the background queue re-walks the library: about ten seconds a demo.

* **Indexed**: the sidecar is current.
* **Failed**: the build left the ruleset out. A user-tier file that disables `kast` or `player_stats` does
  this: the facet still has a fingerprint (`MissingDependencyIdentity`), every demo is visited once and
  stamped failed, and none is visited again until the dependency is back, which moves the fingerprint.

`player_match` is app-owned like the library rulesets. It is not in the CS2DemoKit package, so
re-extracting `rules/` leaves it alone; `PlayerMatchParityRealDemoTests` must pass after every engine bump.

## Dimensions

| Dimension | Meaning |
|---|---|
| `match_id` | Declared by the engine, never filled. Absent from the rows. |
| `map` | The map name. |
| `player_slot` | The controller slot. A row exists for every slot the run materialised, ghosts included. |
| `player_name` | Not reliable on a forward run: a slot can read an earlier occupant's name or the placeholder `Player N`. Take names from the record's roster by slot (see `FacetPlayers`). |
| `steam_id` | SteamID64 as a decimal string; null for bots and for slots the roster never named. |
| `team` | The final roster team, as `ParsedDemo.Players[slot].Team`: 2 = T, 3 = CT, 0 for an abandoned slot, 1 for a spectator. Side at the end of the match. |

A reconnecting account leaves a ghost row on its old slot: its real `steam_id`, zero kills, deaths, damage
and shots, and junk survival and KAST (a ghost is alive every round). Merge rows per `steam_id` only when
`team` is 2 or 3 or the row shows activity (`Shots`, `TotalD` or `EnemyDmg` above zero); drop the rest.
`PlayerAggregate.Merge` does this: counts and sums add, `rounds` is the same on every row, KAST rounds add
up to at most `rounds`, and HLTV adds up less HLTV 2.0's constant terms once per extra slot (every other
term is linear in a per-round count).

## Columns

Labels are the Stats tab's column keys wherever one exists. Every value is a whole number except the three
marked *double*.

| Column | Source |
|---|---|
| `rounds` | `round.number` at match end: the match's round count, the divisor of every per-round rate. |
| `KAST%` *double* | `kast.kast_pct`, unrounded. `KAST% * rounds / 100` is the whole number of KAST rounds behind it. |
| `HLTV` *double* | `player_stats.HLTV`, unrounded. |
| `TotalK`, `TeamKills`, `TotalD`, `TotalA`, `TotalHS`, `FlashAst`, `TrdK`, `Clutch`, `RapidKills` | `player_stats` kill, death and assist counts. `TeamKills` is the second half of the HS% denominator. |
| `NoScope`, `WB`, `Smoke`, `Blind`, `Revenge`, `FlashK`, `AWP`, `Pistol`, `Rifle`, `SMG`, `Knife` | `player_stats` special and weapon-class kills. |
| `EnemyDmg`, `TeamDmg`, `SelfDmg`, `HitFoe`, `HitTeam`, `Shots` | `player_stats` damage and shot counts. `Shots` is also aim_rating's bullet count. |
| `health_at_damage_sum`, `damage_instances` | The two halves of `AvgHP→Dmg`. |
| `Survived`, `Armor` | Rounds survived, rounds started with armour. |
| `equipment_sum`, `equipment_rounds` | The two halves of `Equip`. |
| `HE`, `Flash`, `Smokes`, `Molly`, `EFlash`, `Plants`, `Defuses` | Utility thrown, enemies flashed, bomb plants and defuses. |
| `blind_duration_sum` | Sum of whole seconds of enemy blindness: the numerator of `AvgBlind` over `EFlash`. |
| `CTW`, `CTL`, `TW`, `TL` | Rounds won and lost on each side. |
| `TotalFK`, `TotalFD` | Rounds with an opening kill, with an opening death. |
| `CTFK`, `CTFD`, `TFK`, `TFD` | Opening kills and deaths by side. |
| `TradedD` | Rounds in which the player's death was traded. |
| `DeagleHSRnds` | Rounds with two or more Desert Eagle headshot kills. |
| `2K`, `3K`, `4K`, `5K` | Rounds with exactly that many kills (5 or more for `5K`). |
| `head_hits`, `head_damage`, `cs_clean`, `moving_shots` | aim_rating numerators of `HSAcc%`, `HSDmg%`, `CS%`/`CSAll%` and `Linear%`. |
| `CSAtt`, `SprayN` | Counter-strafe attempts and spray-measured shots: the `CS%` and `Spray` denominators. |
| `spray_error_sum` *double* | Sum of the spray residual in degrees: the `Spray` numerator. |

How they are produced, and why that matters to a reader:

* **Counts are copies, not reads.** Each count is a word-for-word copy of its `player_stats` or `aim_rating`
  definition, which the engine resolves to the same node and its final value. A `per: match` compute that
  read it across would be evaluated at match end, before the last round's `round_won`/`round_lost`, and
  store one round win or loss fewer than the Stats tab shows.
* **The four highlight counts are re-authored.** `TotalFK`, `TotalFD`, `DeagleHSRnds` and `TradedD` are
  `.count`s of highlights in `player_stats`, which do not resolve across rulesets. They are per-round
  counts with a `tally:`, or a count of round ends while the player is traded. A highlight inside
  `player_match` would have worked too, but it would join the highlights fingerprint of any rules build that
  does not stamp the ruleset.
* **Rates are stored only where the engine computes them at match end** (KAST%, HLTV), with `format: R`.
  A compute's format rounds the stored value, and the default `F1` stores 1.6 for an HLTV of 1.56.
* **One demo equals its Stats tab.** `PlayerMatchParityRealDemoTests` rebuilds every pooling column from one
  row and compares it with the Stats tab's game table, row for row: counts exactly, rates to the column's
  display precision. It passes on the five benchmark demos and on a reconnect demo with ghost slots.

Not in the facet:

* **Sight columns.** The 17 `sight: true` columns need line-of-sight geometry; the background run has none
  and would store a permanent 0.
* **Rounds played.** A count of round ends since a slot appeared counts every round for a bot, a ghost and a
  spectator alike; it measures time connected, not time on a team, so nothing could use it.
* **Ratios.** ADR, KPR, HS% and the rest are divided at read time from the columns above.
* **Weapon buckets** (`weapon_stats`) have no projection off a forward run.

## The record and the index row

Writing the facet sets `FacetPlayers` on the demo's record in the same update as the stamp, and the index
row mirrors it (omitted when null), so the player directory finds a player who is in no side list, such as
one who ended the demo as a spectator, without opening a sidecar.

The same update sets the record's `CtSideWins` and `TSideWins` (rounds won on each side across the match, from
`CTW` and `TW` of one row per final team; null when a team's rows disagree), which the index row carries to
Strat Book.

## Match Overview

Match Overview's cached scoreboard is projected from this table: `TotalK`, `TotalD`, `TotalA`, ADR as
`EnemyDmg / rounds` at `F1`, `HLTV` at `F2`, and the side split and round count, so a demo the facts pass has
reached reads `FULL` once its highlights are scanned. A record's own `Scoreboard`, which earlier versions wrote
from an open or a forced scan, is read only for a demo with no facet; nothing writes it now.

```jsonc
"FacetPlayers": [
  { "SteamId64": "76561190000000003", "Names": [ "grim" ], "Slots": [ 3, 4 ] }
]
```

* One entry per `steam_id` in the table, sorted ordinally. Rows with no `steam_id`, or `0`, are left out,
  and so is an account the record's roster has only as a coach or a bot.
* **`Slots`**: every slot with a row for the account, ghosts included, ascending.
* **`Names`**: the record's roster name of each slot whose roster entry is the same account, distinct, in
  slot order. Only when the roster has none does the table's `player_name` stand, and a `Player N`
  placeholder never does. RAW names: sanitise at the render boundary.

A player whose roster entry the game cleared on disconnect has a row with a null `steam_id` and so is not
in `FacetPlayers`. The "left early" case the directory can show is a player who ended as a spectator or on
an abandoned slot that still carries the account.
