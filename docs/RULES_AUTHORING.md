# Authoring Rulesets — a hands-on guide (Rulesets v2)

This is the **learning path** for writing your own stats. You describe *what* you want to measure
in a small YAML file; the engine reads every round of a demo and produces the numbers. You never
write code, and you can't crash the parser — the worst that happens is a clear error telling you
what to fix.

This guide teaches you to author, step by step. The one-page vocabulary reference is the
cheat-sheet in `docs/rules-v2/rules-v2-paper-test-kit.md` §3, and the exact contract, for when
you need the fine print, is the spec (`docs/rules-v2/rules-v2-spec.md`). Working examples live
under `rules/examples/` — read them; they all pass `rules check`.

**Where your files go.** Your rules live in the per-user rules folder (the **📁 Rules** button on
the Analysis tab opens it, and the Rule Workbench edits it directly). Name files
`<name>.rules.yaml`. The folder is provisioned with a copy of the v2 JSON schema — start every
file with this line to get editor validation and autocompletion:

```yaml
# yaml-language-server: $schema=./cs2demokit-rules.schema.json
```

**Check your work as you go.** After writing a file, run:

```sh
dotnet run --project tools/AnalysisBench -- rules check path/to/your-rules-dir
```

It reports every problem with a `file(line,col):` you can click, and a "did you mean…" for typos.
A clean run means your ruleset is valid.

---

## 1. The mental model

A **ruleset** is a named bundle of **stats**. Each stat is one measurement. You choose:

- **`for:`** — do you want this *per player* (`each_player`) or for the *whole match* (`match`)?
- **the kind** — *how* to measure (count events, sum a value, keep a max, compute a formula, …).
- **the source** — *what* to measure (a "view" like `kill`, or another stat).
- **`per:`** — the window it resets over: `round` or `match`.

That's the whole idea. Everything else is refinement — filtering, formulas, and how the result is
displayed.

---

## 2. Your first ruleset

Count each player's kills.

```yaml
ruleset: my_first
for: each_player
stats:
  kills:
    count: kill
    per: match
show:
  scoreboard:
    - { stat: kills, label: Kills, group: game }
```

- `count: kill` — add 1 every time this player gets a kill. `kill` is a **view** (below).
- `per: match` — accumulate across the whole match (use `per: round` to reset each round).
- `show: scoreboard:` — put a `Kills` column on the per-player scoreboard.

Run `rules check` on it, and you have a working stat.

---

## 3. Views and facets — the vocabulary

You don't reference raw wire events; you trigger on **views** — author-friendly verbs that already
know the CS2 conventions. Common views:

`kill` · `death` · `assist` · `damage_dealt` · `shot` · `blinded_enemy` ·
`bomb_planted` · `bomb_defused` · `he_grenade` · `flash_grenade` · `smoke_grenade` · `molotov` ·
`round_won` · `round_lost`

A view carries **facets** — typed attributes you filter on with `match:`. The `kill` view has:
`enemy`, `headshot`, `no_scope`, `through_smoke`, `trade`, `flash_assisted`, `weapon`.

```yaml
stats:
  headshot_kills:
    count: kill
    match: { enemy: true, headshot: true }   # only enemy headshot kills
    per: round
```

For `for: each_player`, a view automatically binds to *this* player (the `kill` view counts *this
player's* kills). At `for: match`, there's no subject, so `count: kill` counts *everyone's* kills
(a match total).

If you need a raw event with no view, use `raw.<event>`; net messages are `net.<Message>`. Views
are almost always what you want.

---

## 4. The stat kinds

Pick exactly one kind per stat.

### `count:` — +1 per event
```yaml
deaths: { count: death, per: round }
```

### `sum:` — add up a value per event
```yaml
damage: { sum: event.DmgHealth, on: damage_dealt, match: { enemy: true }, per: round }
```
`sum:` takes the value to add; `on:` names the view whose events drive it.

### `capture:` — remember value(s)
`keep:` chooses what to keep: `first`, `last`, `list`, or the extremes `min` / `max`.
```yaml
best_multi:                              # the most kills this player got in any single round
  capture: round_kills                   # a numeric value…
  keep: max                              # …keep the maximum over the match
  per: match
```

### `compute:` — a formula over your other stats
Evaluated at round end. Reads your sibling stats and contexts. Add `live: true` to recompute
continuously instead of only at round end.
```yaml
adr: { compute: "damage / round.number" }        # average damage per round
kd:  { compute: "kills / deaths" }
```
Expressions support `+ - * /`, comparisons, `and`/`or`/`not`, the functions
`min max abs floor contains startswith`, and duration literals `10s` / `500ms` / `"1:30"`.

### `tally:` — bucket a value into thresholds
The 2K/3K/4K/5K idiom. Each threshold's `target` is a counter it feeds.
```yaml
multi_kills:
  tally: round_kills
  thresholds:
    - { min: 5, target: rounds_5k }
    - { min: 4, target: rounds_4k }
    - { min: 3, target: rounds_3k }
    - { min: 2, target: rounds_2k }
```
`min:` can also be a `params.<name>` reference if you parameterize your ruleset.

### `streak:` — a windowed streak of events
```yaml
rapid_kills: { streak: kill, window: "10s", min_streak: 2 }
```

### `bucket:` — one sub-count per key
Breaks a stat down by a key (per weapon, per site, …). `key:` may be a **list** for a composite
(tuple) key. Add `value:` + `reduce:` to reduce a value per key instead of counting.
```yaml
kills_by_weapon:
  bucket: kill
  key: event.Weapon
  match: { enemy: true }
damage_by_weapon:
  bucket: kill
  key: event.Weapon
  value: enrich.hurt.capped_damage
  reduce: sum                            # sum | count | min | max | last | first
```

### `rate:` — a per-key ratio
Divides two same-keyed buckets into a per-key ratio (e.g. per-weapon headshot %). Both buckets must
use the same `key:`. Iterates the denominator's keys; a key with 0 denominator is skipped.
```yaml
hs_by_weapon:  { bucket: kill, key: event.Weapon, match: { enemy: true, headshot: true } }
weapon_hs_rate: { rate: { of: hs_by_weapon, per: kills_by_weapon } }
```

### `flag:` — a per-round boolean
True/false for the round, driven either by an event (`on:` + `activate`) or by a condition over
your other stats (`when:`). Its most common use is inside a **highlight** (next section).

---

## 5. Gating — filtering when a stat measures

Four ways to narrow what a stat counts, from coarsest to finest:

- **`match:`** — filter by a view's typed facets: `match: { enemy: true, headshot: true }`.
- **`where:`** — a free-form condition over the event's fields, enrichments, contexts, and
  entity state: `where: 'event.Weapon == "awp"'`.
- **`while:`** — only fire while a per-player condition holds: `while: player.alive`.
- **`when:`** — (on `flag:`/`highlight:`) a condition over your *sibling stats*: `when: kills >= 2`.

`match:` and `where:` filter each event; `while:` gates on the player's live state; `when:` composes
your stats. You can combine them.

```yaml
eco_kills:
  count: kill
  match: { enemy: true }
  where: "round.team.equipment < round.enemies.equipment"   # your team was out-bought
  per: round
```

`when:` may be a single expression or a **list**, which reads as "all of these" (AND):
```yaml
when: [enemy_kills > 0, player.survived]     # same as "enemy_kills > 0 and player.survived"
```

---

## 6. Highlights — per-round achievements and their totals

A **highlight** is a per-round "did it happen" flag. Its match-scoped **`.count`** is how many
rounds it fired — the idiomatic way to turn "this round I did X" into a match total.

```yaml
stats:
  round_kills: { count: kill, match: { enemy: true }, per: round }
highlights:
  multi_kill_round:
    when: round_kills >= 2
    per: round
    title: "Multi-kill round"
show:
  scoreboard:
    - { stat: multi_kill_round.count, label: MultiRounds, group: game }
```

Highlights also surface on the timeline (each firing is a clip-able moment).

---

## 7. Contexts — reading the game state

Inside `when:` / `where:` / `compute:` you can read live game state:

- **Per-player (this player):** `player.survived`, `player.traded`, `player.alive`.
- **Round facts:** `round.number`, `round.won`.
- **Team aggregates (subject-relative):** `round.team.alive` / `round.enemies.alive`,
  `round.team.players` / `round.enemies.players`, `round.team.equipment` /
  `round.enemies.equipment`, `round.alive.in_clutch`.
- **Entity state:** `player.entity.pawn.health` / `.armor` / `.equipment_value` /
  `.active_weapon_clip` / `.place` — the player's live pawn state. (`active_weapon_clip` is the
  magazine count of the currently held weapon — under the pre-frame timing below, at a kill
  event it is the clip BEFORE the killing shot, so "last bullet" reads `== 1`; no-magazine
  weapons like knives read `-1`. `place` is the human-readable nav-mesh place name the pawn
  last occupied — `"BombsiteA"`, `"TSpawn"`, `"Ramp"`, … — a string; names come from the map's
  nav mesh, so gate on the standard ones (`BombsiteA`/`BombsiteB`/`CTSpawn`/`TSpawn`) for
  map-portable rules. See `rules/highlights_position.rules.yaml`.)

**A timing note on entity reads.** In an event-gated site (`where:`, a `sum:`/`capture:` value,
`while:`), an entity read is the value *at the moment of the event* (e.g. the victim's HP at the
kill). In a node-logic site (`compute:`, `flag: when:`), it's the value at *round end / evaluation
time*. Both are useful — pick the site that matches the question. (Under an event view you can also
read a role's entity state: `victim.entity.pawn.health` in a `kill`-view `where:`.)

Contexts are per-player, so they're only available in a `for: each_player` ruleset. A `for: match`
ruleset has no subject and cannot read `player.*` or the team aggregates.

---

## 8. Displaying results — `show:`

- **`scoreboard:`** — per-player columns. `{ stat: <name or highlight.count>, label:, group: }`.
  `group:` is usually `round` (per-round columns) or `game` (match totals).
- **`tables:`** — richer per-round or per-match tables, written as a **named map**
  (`tables: { <table-name>: { per:, columns: [...] } }`). Use `per: match` on a table (in a
  `for: match` ruleset) for a single match-level row.
- **`as: ticks | seconds | time`** on a column reformats a tick-valued stat (raw ticks, seconds, or
  `m:ss`).

`scoreboard:` is inherently per-player; in a `for: match` ruleset use `tables:` instead.

---

## 9. Bringing it together — a worked example

"Multi-kill rounds," end to end (this is `rules/examples/paper-test/multikill.rules.yaml`):

```yaml
ruleset: multikill
for: each_player
stats:
  round_kills:
    count: kill
    match: { enemy: true }
    per: round
  multi_kill_tally:
    tally: round_kills
    thresholds:
      - { min: 5, target: rounds_5k }
      - { min: 4, target: rounds_4k }
      - { min: 3, target: rounds_3k }
      - { min: 2, target: rounds_2k }
show:
  scoreboard:
    - { stat: rounds_2k, label: "2K", group: game }
    - { stat: rounds_3k, label: "3K", group: game }
    - { stat: rounds_4k, label: "4K", group: game }
    - { stat: rounds_5k, label: "5K", group: game }
```

For more complete, real examples read `rules/examples/`: `weapon_stats` (buckets), `kast`
(counters + tally + a highlight + a compute), and `player_stats` (the big one — computes,
entity reads, cross-ruleset composition).

---

## 10. Two bigger tools

### Match-wide stats — `for: match`
When a stat isn't per-player (total kills, total rounds, a match-level table), use `for: match`.
Views count everyone; `player.*`/team contexts aren't available (there's no subject). Display with
`tables: (per: match)`, not `scoreboard:`.
```yaml
ruleset: match_totals
for: match
stats:
  total_kills:  { count: kill, per: match }
  total_rounds: { count: round_won, per: match }
show:
  tables:
    match_summary:                         # tables: is a named map: <table-name>: { per, columns }
      per: match
      columns:
        - { stat: total_kills, label: TotalK }
        - { stat: total_rounds, label: Rounds }
```

### Reusing another ruleset — `use:` / `exports:`
A stat can read `otherRuleset.stat` if your file declares `use: [otherRuleset]` and that ruleset
`exports:` it. This is how one file builds on another without copy-pasting.
```yaml
ruleset: kast
for: each_player
exports: [kast_pct]
stats: { ... }
---
ruleset: ratings
for: each_player
use: [kast]
stats:
  hltv: { compute: "0.73 * (kast.kast_pct / 100) + ..." }   # reads kast's exported stat
```
The engine catches every mistake: unknown ruleset, unknown stat, not-exported, not-in-`use:`, and
reference cycles — each with a clear message.

---

## 11. Reference maps of parameters & provenance

- **`params:`** let a ruleset take values (e.g. a threshold) that a reader can adjust; read them as
  `params.<name>` (currently in `tally:` thresholds and expressions).
- **`define:`** names a reusable list, trigger, or a **lookup map** read as `ref[key]`.
- **`catalog_version:` / `min_app_version:`** stamp which catalog/app a ruleset was written against.

---

## 12. When something's wrong

- `rules check` names the exact `file(line,col)` and suggests fixes for typos.
- "unknown name X — available roots: …" means you referenced something not in scope (a common one:
  reading `player.*` in a `for: match` ruleset — there's no subject there).
- A stat can declare only **one** kind; two kinds on one stat is an error.
- Not sure a facet/view exists? The error lists the valid options.

Author small, check often, and grow the file a stat at a time. Every example under `rules/examples/`
is a working reference you can copy from.
