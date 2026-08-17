# Rulesets v2 — Pre-Freeze Paper Test Kit

**Purpose.** Before the v2 authoring *shape* is frozen for good, we validate
it with real non-programmers. Each participant hand-writes v2 YAML answers to ten realistic
Leetify/Scope.gg-style stat requests. The shape is freeze-ready when **every request is expressible
with no new engine work — or is explicitly deferred with a named gap.** Confusion, dead-ends, or
"I reached for X and it wasn't there" are the signal we want *before* the freeze locks the surface.

**How to run (owner).**
1. Recruit 2–3 people who are comfortable with a config file but are *not* programmers.
2. Give each of them: this document's **Requests** (§2) and **Cheat-sheet** (§3) — but **not** the
   reference answers (§5).
3. Ask them to write a v2 YAML `ruleset:` for each request, thinking aloud. Time-box ~15 min/request.
4. Fill in the **Observation rubric** (§4) per request as they work.
5. Exit criterion met when, across the group, every request was either expressed (compare against
   §5) or you recorded a concrete named gap.

The reference answers in §5 are the *implementer-side* exit check: each is a real v2 ruleset that
passes `rules check` on the current build, proving the request is expressible today. Use them to
score attempts and to see the intended idiom — they are **not** the only correct answer.

---

## 2. The ten requests

Give these to participants verbatim. Each is phrased the way an analyst would ask.

1. **Ninja defuses** — For each player, how many rounds did they defuse the bomb while at least one
   enemy was still alive?
2. **Biggest clutch** — For each player, the largest clutch they *won* this match — i.e. the most
   enemies still alive at the moment they won a round as the last player alive on their team.
3. **Save rounds** — For each player, how many rounds did their team *lose* but they personally
   *survived* (kept themselves, and their gun, alive)?
4. **Eco frags** — For each player, how many kills did they get in rounds where their own team was
   on a low buy (team equipment value clearly below the enemies')?
5. **Fast trades** — For each player, how many of their kills *traded* a just-killed teammate within
   3 seconds?
6. **Per-weapon headshot rate** — For each player, their headshot percentage *broken down by
   weapon* (headshot kills ÷ kills, per weapon).
7. **Opening-duel win rate** — For each player, what fraction of the round's *first* duel they were
   in did they win (first kills ÷ (first kills + first deaths))?
8. **Flash effectiveness** — For each player, the average number of seconds they blinded enemies,
   per flash that actually blinded someone.
9. **Utility damage per round** — For each player, average grenade (HE/molotov) damage dealt to
   enemies per round played.
10. **Multi-kill rounds** — For each player, a breakdown of how many 2K / 3K / 4K / 5K rounds they
    had.

---

## 3. Cheat-sheet (give to participants)

A **ruleset** computes stats for either every player or the whole match:

```yaml
ruleset: my_stats
for: each_player          # or: match
stats:
  <stat_name>:
    <kind>: <source>      # exactly ONE kind per stat (below)
    per: round            # or: match  — the reset/aggregation window
    # optional gates:
    match: { enemy: true }         # filter the trigger by a facet
    where: "event.Weapon == \"awp\""   # or a free-form condition
highlights:                 # a per-round "did it happen" flag; its .count is a match total
  <name>:
    when: <condition-over-your-stats>
    per: round
    title: "…"
show:
  scoreboard:               # columns in the per-player table
    - { stat: <stat_or_highlight.count>, label: "…", group: game }
```

**Stat kinds** (pick one):
- `count: <view>` — +1 per matching event (e.g. `count: kill`).
- `sum: <expr>` — add up a value per event (e.g. `sum: event.DmgHealth`).
- `capture: <expr>` — record value(s); `keep: first | last | list`.
- `compute: "<expr>"` — a formula over your other stats, computed at round end (append `live: true`
  to recompute continuously instead).
- `tally:` — bucket a source value into thresholds (the 2K/3K/… idiom).
- `streak:` — a windowed streak of events.
- `bucket: <view>` with `key:` — one sub-count per key (e.g. per weapon); `key:` may be a **list**
  for a composite key; add `value:`+`reduce: sum|min|max|last|first` to reduce a value per key.

**Views** (author-facing verbs you trigger `on:` / `count:`): `kill`, `death`, `assist`, `damage_dealt`,
`shot`, `bomb_planted`, `bomb_defused`, `he_grenade`, `flash_grenade`, `smoke_grenade`, `molotov`,
`round_won`, `round_lost`, … Each carries **facets** you can `match:` on — e.g. the `kill` view has
`enemy`, `headshot`, `no_scope`, `through_smoke`, `trade`, `weapon`, `flash_assisted`.

**Contexts** (read in `when:`/`where:`/`compute:`):
- `player.survived` / `player.traded` / `player.alive` — the subject this round.
- `round.number`, `round.won` — round facts.
- `round.team.alive` / `round.enemies.alive` / `round.team.players` / `round.enemies.players` —
  live team counts; `round.team.equipment` / `round.enemies.equipment` — team economy at freeze-end;
  `round.alive.in_clutch` — is the subject in a clutch.

**Cross-ruleset:** a stat may read `otherRuleset.stat` if this file declares `use: [otherRuleset]`
and that ruleset `exports:` it.

**Functions in expressions:** `min`, `max`, `abs`, `floor`, `contains`, `startswith`. Durations:
`10s`, `500ms`, or `"1:30"`.

---

## 4. Observation rubric (owner fills per request, per participant)

| Field | What to record |
|---|---|
| Expressed? | yes / partial / no |
| Time to first working line | minutes |
| Kind chosen | which stat kind they reached for (and was it the intended one?) |
| Where they stalled | the first point of confusion or dead-end |
| Reached-for-but-absent | anything they expected to exist that doesn't (the freeze signal) |
| Vocabulary miss | a view/facet/context they looked for under the wrong name |
| Gap? | if not expressible: the concrete named gap (→ fix before freeze, or defer with a name) |

Roll-up after the session: list every **reached-for-but-absent** and **gap** — those are the
pre-freeze action items.

---

## 5. Reference answer key (implementer-side; validated by `rules check`)

Runnable reference files live under `rules/examples/paper-test/` and pass `rules check`.
*They prove expressibility; they are not the only correct phrasing.* **Caveat:** `rules check`
validates *static resolution*, not runtime *correctness* — a few requests resolve but read the
wrong value or can't express the exact ask; those are the named gaps below.

### Implementer-side exit check (10/10)

| # | Request | Status | Reference / gap |
|---|---|---|---|
| 1 | Ninja defuses | yes (gap since closed) | `count: bomb_defused` + `where: round.enemies.alive > 0` — a team aggregate in a `where:` now binds the **subject's** per-slot value (see below) |
| 2 | Biggest clutch | yes (gap since closed) | scalar min/max capture now exists (`keep: min` / `keep: max` — `KeepMode.Min`/`Max`, `StatDef.cs`) |
| 3 | Save rounds | yes | `save_rounds.rules.yaml` |
| 4 | Eco frags | yes (gap since closed) | same aggregate-in-`where:` path as #1, now subject-bound |
| 5 | Fast trades | partial | `fast_trades.rules.yaml` (`match: {trade: true}`) — trades are expressible, but the **"3-second" window is baked** into the enrichment, not authorable |
| 6 | Per-weapon HS rate | yes (gap since closed) | `weapon_hs.rules.yaml` — per-weapon counts, and a per-key **rate** (hs÷kills per weapon) via the `rate` kind (`KeyedRatioNode`, `RateKindModelTests`) |
| 7 | Opening-duel win rate | yes | the corpus `player_stats` opening pattern (opening_kill/opening_death flags → a `compute:` ratio) |
| 8 | Flash effectiveness | yes | `flash_effectiveness.rules.yaml` |
| 9 | Utility damage / round | yes | `utility_damage.rules.yaml` |
| 10 | Multi-kill rounds | yes | `multikill.rules.yaml` |

**Result: 9/10 cleanly expressible, 1 partial — the parameterized-trade-window minor (#5).** The
three gaps the first run named have all since closed: #1/#4 (subject-bound aggregate reads),
#2 (scalar min/max capture), #6 (per-key rate).

### Named gaps (the freeze decision list — fix before freeze, or freeze-and-defer with these names)

- **Event-gated per-player aggregate reads (a team-aggregate read inside a `where:`) — fixed
  2026-07-13.** Empirically the read did not merely bind the *global* lookup — it **threw at graph
  materialization** (`Unknown player member: survived` / `Unknown identifier: round`): the `where:`
  string was written verbatim and `ExpressionCompiler` had no branch/lookup for these names. The fix
  is additive and v2-only: `V1ExpressionWriter` lowers a `where:` context/aggregate path to its v1
  rule id (`player.survived` → `survived`, `round.enemies.alive` → `round_enemies_alive`), and a
  per-slot `ConditionNodes` overlay exposes the subject slot's context/aggregate nodes under those
  ids so the read binds the **subject**. The v1 path is byte-identical (overlay unset → the shared
  enrichment lookup; no remap). Proven by `G1WhereContextSubjectBindingTests` (per-player
  `player.alive` and aggregate `round.team.alive` / `round.enemies.alive` reads in a `where:` bind
  the subject slot on the reference demo). *(Known adjacent limitation, out of this fix's scope: a
  `while:` gate or `flag: when:` over an aggregate pull-node — e.g. `round.alive.in_clutch` — does
  not gate; the `where:` read is now the working event-gated path for a team aggregate.)*
- **Scalar min/max capture — fixed.** `keep: min` / `keep: max` now exist as scalar
  capture modes (`KeepMode.Min`/`Max`, `StatDef.cs`), so "biggest clutch won" (#2) is expressible.
- **Per-key bucket compute — fixed.** The `rate` kind now divides
  per key (`KeyedRatioNode`, `RateKindModelTests`), so a per-weapon HS *rate* (#6) is expressible.
- **Parameterized trade window (minor)** — the `trade` facet uses the enrichment's fixed
  window; a specific "within N seconds" isn't authorable. (Weakens #5.)

None is a hard blocker: each request is either expressible or has a crisply named gap — the
plan's exit criterion. The first three have since closed; only the parameterized-trade-window
minor (#5) remains as a named, additive, deferred lift (it doesn't reshape existing shapes). This is
the decision the owner's live paper-test should confirm against real participants.
