# Known and Suspected Issues

Parity gaps between our parser/analysis output and Leetify's `playerStats` on the 5-demo
bench suite (`demos/benchmarks/`), plus a few parser-side robustness questions. Opened
2026-05-22 as the follow-up backlog from the May 2026 correctness pass.

Each entry records: what we measured, what we ruled out, what we suspect, and how to
resume the investigation. Closure of any of these moves the bench mismatch count down;
do not close-by-tuning-toward-Leetify without confirming the change matches the
documented CS-stat-engine convention. We will accept residual divergence rather than
encode a Leetify-specific quirk.

---

## Quick reference

Bench suite snapshot as of the v0.5.1 ship-gate run (2026-07-26): 142 mismatched
player×stat tuples / 950 compared across the 5 demos (per-demo 26 · 25 · 33 · 34 · 24).
The list below covers the stats with non-trivial residual.

Movement from the previous 2026-05-22 snapshot (144) was exactly two stats:
`totalAssists` −5 (the assist-facet fix below) and `EnemyDmg` +3 (the damage regression
below). Every other stat was tuple-for-tuple identical.

> Goldens re-baselined 2026-08-12 together with the damage-regression fix, ending the
> embargo that had held them at 2026-05-22: the regen moved exactly the 5 assist cells
> and nothing else. While the embargo held, live numbers came from
> `AnalysisBench --suite --no-golden` and the fixtures deliberately lagged.

| Stat | Pattern | Magnitude | Demos | Status |
|---|---|---|---|---|
| `shotsFired` (Shots) | +14/−1 overcount | +1 to +2 per affected player | 5/5 | [Deferred](#shots-fired-overcount-uniform-direction) |
| `totalDamage` (EnemyDmg) | 0/−9 undercount | −1 to −3 hp | 4/5 | [Open](#enemy-damage-undercount-uniform-direction); the 2026 overcount regression is [fixed](#enemy-damage-overcount-regression-fixed-2026-08-12) |
| `tradeKillsSucceeded` (TrdK) | 0/−7 undercount | −1 to −2 trades | 4/5 | [Deferred](#trade-kill-undercount-not-a-window-size-issue) |
| `kast` (KAST%) | 0/−5 undercount | each Δ ≈ 1 round | 3/5 | [Bound to the trade-kill gap](#kast-undercount-bound-to-the-trade-kill-gap) |
| `hltvRating` (HLTV) | ±0.4 mixed | small fractional | 5/5 | [Out of scope](#hltv-rating-divergence-formula-choice-not-a-parser-bug) |
| `dpr` (ADR), `kdRatio` (KD), `hsp` (HS%) | mixed | derived | 5/5 | [Derived](#derived-stats-adr-kd-hs) |
| `totalAssists` (TotalA) | +3/−2 mixed | ±1 per player | 3/5 | [Fixed 2026-07-26](#assist-counting-fixed-2026-07-26); ship-gate bench confirmed 0 mismatches, zero ripple |
| `shotsHitFoe` (HitFoe) | +11/−2 mixed | ±1 to ±4 | 5/5 | [Narrowed](#shots-hit-foe-mixed-sign); dedup + shotgun hypotheses disproven; +11 tied to the shots overcount, −2 unexplained |
| entity-state decoder bit misalignment | parser-side, not parity | decode clean on all bench demos | 0/5 (was 5/5 MM) | [Cured](#entity-decode-bit-misalignment-on-mm-demos-cured) by the 2026-06-08 decode fix; probe shows zero errors |
| malformed-demo policy (no fail-fast, unenforced Valve limits) | parser robustness | silent partial parse | any corrupt demo | [Open](#no-malformed-demo-policy--valve-declared-limits-are-not-enforced) |
| string-table entries index-addressed (dense list) | parser design | forced a domain cap on indices | n/a | [Fixed 2026-07-26](#string-table-storage-moved-to-a-keyed-map-fixed-2026-07-26) |
| `ParsedDemo.Warnings.Count` varies across identical parses | diagnostics only | ±1 warning entry | 1 observation | [Open](#parseddemowarningscount-is-nondeterministic-across-identical-parses) |

To reproduce the underlying numbers:

```sh
dotnet run -c Release --project tools/AnalysisBench -- --suite
# Reports land in bench-reports/<demo_id>_<timestamp>.json
# Per-stat mismatch summary printed at end of each demo's section
```

To dig into a specific player's events:

```sh
dotnet run -c Release --project tools/AnalysisBench -- \
  demos/benchmarks/<demo_id>.dem --shots-debug="<player name substring>"
```

`--shots-debug` prints every weapon_fire and player_hurt with phase tags
(LIVE/WARMUP, ROUND/FREEZE, BUY/COMBAT, ALIVE/DEAD!), every player_death for
the target player, plus a per-player damage summary (raw DmgHealth sum, capped
sum, overkill reduction). Added for this backlog in May 2026.

---

## Shots-fired overcount (uniform direction)

**Measured:** 15 affected players across all 5 demos. Sign distribution
+14/−1, mean Δ = +1.13, max |Δ| = 2. Examples (ours vs Leetify):

| Demo | Player | Ours | Leetify | Δ |
|---|---|---|---|---|
| dust2 | yehright | 332 | 330 | +2 |
| dust2 | Dark Jeb | 170 | 168 | +2 |
| ancient | d4y | 195 | 193 | +2 |
| nuke | fonzieguy | 309 | 307 | +2 |
| inferno | beyondproof | 233 | 232 | +1 |

Magnitude is 0.3–0.5% relative (1–2 over 200–400). The +1.13 mean over
15 players totals ≈ 17 extra weapon_fire events across the suite that
Leetify excludes.

**Ruled out** in the May 2026 investigation:

- Warmup shots. Every affected shot is tagged `LIVE` (after `BeginNewMatchEvent`).
- Same-tick duplicate weapon_fire events. Scripted scan over yehright's
  376 weapon_fires: zero same-(tick, weapon) duplicates, zero close-tick
  (<3-tick gap) repeats of the same weapon.
- Post-death shots. Probed all 5 affected players with the ALIVE/DEAD!
  tag added to `--shots-debug`; all five show `TOTAL post-death shots: 0`.
  CS2 demos *can* emit a weapon_fire on the input frame after death, but it
  isn't what's happening here.
- Weapon classification edge cases. None of the affected players used
  taser, zeus, shield, or other ambiguous weapons. Histograms are all
  standard rifles/pistols/SMGs/snipers.
- BUY-phase shots. They are counted by both us and Leetify (live and
  weapons-active during buy phase is normal CS2 gameplay).

**Suspected root cause:** Leetify applies a filter we can't see from our
side. Plausible candidates we cannot verify without their source:

- A first-shot-of-match exclusion (e.g., warmup-tail shot at server-side
  match-start that the client-side `BeginNewMatchEvent` arrives after).
- A weapon-state filter (e.g., "shot while reloading" or "shot during
  weapon-switch animation" suppressed).
- A specific tickrate-rounding off-by-one near round boundaries.

**Resume here when:** the trade-kill gap closes (a similar Leetify-side filter
may surface), or when we get external information about Leetify's algorithm.
Don't bisect the 0.5% residual without a discriminator hypothesis.

---

## Enemy-damage undercount (uniform direction)

**Measured:** 9 affected players across 4 demos. Sign distribution 0/−9
(uniform undercount), mean Δ = −1.67 hp, max |Δ| = 3. Examples:

| Demo | Player | Ours | Leetify | Δ |
|---|---|---|---|---|
| mirage | Plasmabal | 2343 | 2344 | −1 |
| ancient | Little MJ | 2668 | 2670 | −2 |
| ancient | yehright | 2105 | 2108 | −3 |
| nuke | Goonicus | 2325 | 2327 | −2 |
| inferno | ABSOLUTELY FABULOUS | 1307 | 1308 | −1 |

Magnitude is 0.04–0.4% relative — extremely small, but uniformly negative,
which suggests a systematic per-event suppression rather than noise.

(For most of summer 2026 this section did not describe live behaviour — a
regression flipped the profile to an overcount of up to +66 hp. That was fixed
2026-08-12, see the closed section below, and this uniform small undercount is
once more the live profile.)

**Ruled out:**

- The cap formula entirely. Removing the overkill cap and counting raw
  `event.DmgHealth` overshoots Leetify by +49 to +1780 hp per player
  (e.g., Goonicus: 2325 capped → 3160 raw vs Leetify's 2327). The cap is
  essential and approximately correct.
- Entity-state vs event-cache `m_iHealth` divergence. For Plasmabal,
  the event-cache-only probe summed to 2343 — identical to the bench's
  full entity-state-backed value of 2343. Both paths produce the same
  number on these demos.
- Shotgun pellet contamination. None of the 9 affected players used
  xm1014/nova/mag7/sawedoff. Pellet dedup isn't in play here.
- Self-damage / team-damage leakage. The `enrich.hurt.was_enemy_damage`
  parent gate filters these out before the counter increments.

**Suspected root cause:** per-tick `m_iHealth` interpolation difference
between our entity-state read and whatever Leetify samples. Specifically:

- Our preHitHp is read from a frame-start `m_iHealth` snapshot
  (`PawnHealthProvider`). On rare ticks where multiple hurts cluster, the
  snapshot reflects pre-frame state — we use the *same* preHitHp for every
  hurt in that frame. The cap formula short-circuits when `hurt.Health > 0`
  (no cap on non-kills), so this only matters on kill shots, and only when
  the preHitHp differs from the actual just-before-kill HP by 1–3 hp.
- Alternative: Leetify samples HP from a slightly different field
  (e.g., `m_iMaxHealth` minus accumulated damage from a different ledger),
  giving 1–3 hp more headroom on overkill caps.

**Diagnostic to try next:** dump per-hurt cap decisions for one −3 player
(e.g., yehright on ancient — magnitude is largest). For each
`event.Health == 0` event, log: GameTick, attacker, victim, our preHitHp
from both paths, our cap, raw `DmgHealth`. If 3 specific kills show
our preHitHp = 99 where the actual was 100, we've found it.

**Resume here when:** the entity-state HP source changes, or when we get a
clean way to query Leetify's preHitHp expectation.

**Verified still open (2026-07-18).** No targeted fix has landed since this
was written. The suspected mechanism is intact in `HurtTeamEnrichmentEdge.cs`
(frame-start `preHitHp` via `scanner.GetPreFrameValue(pawnHealthProvider, …)`,
then the lethal-hit cap that limits `DmgHealth` to `preHitHp`). The
`PawnHealthProvider` typed-wrapper migration reads the same `m_iHealth` slot,
so it was behavior-preserving and this stands. Re-confirming the exact
±1–3 hp magnitudes needs a fresh `--suite` run.

---

## Trade-kill undercount (NOT a window-size issue)

**Measured at 256-tick (4s) window:** 7 affected players across 4 demos.
Sign distribution 0/−7 (uniform undercount), each |Δ| ∈ {1, 2}. Examples:

| Demo | Player | Ours | Leetify | Δ |
|---|---|---|---|---|
| mirage | Wifferino | 2 | 4 | −2 |
| mirage | piesandcheese | 1 | 2 | −1 |
| inferno | Little MJ | 1 | 2 | −1 |
| ancient | d4y | 3 | 4 | −1 |
| ancient | yehright | 2 | 3 | −1 |

**Experiment performed and falsified:** bumped `windowTicks` from 256 (4s)
to 320 (5s) — the "industry standard" trade window.

| Window | Undercounts | Overcounts | Net abs error |
|---|---|---|---|
| 256 (4s) | 7 | 0 | 7 |
| 320 (5s) | 5 | 13 | 18 |

Of the original 7 undercounts, only 2 closed at 5s. The other 5 missing
trades happen beyond 5s entirely (Leetify is counting them via a
non-window mechanism). And the 13 new false positives at 5s mean the 4–5s
window contains many candidates Leetify *rejects*. So window-size is
emphatically not the right discriminator. 256 ticks remains the local
minimum.

**Ruled out:**

- Window-size tuning alone, per the experiment above.
- Same-team filter. We already require avenger and dead teammate on
  same side (`ctx.Team != killerTeam` check in `FindTradedPlayer`).
- Direct-kill-only (not assists). Matches Leetify's `tradeKillsSucceeded` spec.

**Suspected root cause:** Leetify's trade definition includes a
prerequisite we don't model. Plausible candidates:

- Engagement prerequisite. "Avenger must have dealt damage to the
  killer before the teammate died" — i.e., the kill is only a trade if it
  closes out an existing engagement, not if it's a clean wrap-around
  pickup. This would explain both the 4–5s false positives (no prior
  engagement) and the >5s real trades (long-duration engagements where the
  first damage was within the trade window even if the kill wasn't).
- First-damage-timestamp clock. Leetify might start the trade-window
  countdown from the avenger's first damaging shot on the killer, not from
  the avenger's killing shot. Long fights stretch the wall-clock window
  significantly.
- Line-of-sight or proximity check at the original death.

**Diagnostic to try next:** for each Leetify-counted trade that we miss
and for each false positive at 5s, look up: did the avenger damage the
killer before the original teammate-death? If the answer cleanly separates
the two populations, we've found it.

**Resume here when:** there's appetite for a multi-day reverse-engineering
project, or when external info on Leetify's algorithm surfaces. This is
the highest-leverage open gap — fixing it likely closes the KAST gap too.

---

## KAST undercount (bound to the trade-kill gap)

**Measured:** 5 affected players across 3 demos. Sign distribution 0/−5
(uniform undercount), each Δ is exactly one round's worth:

| Demo | Player | Ours | Leetify | Δ | Rounds |
|---|---|---|---|---|---|
| mirage | ÐúÇK ôÑ qùÄçk | 77.27 | 86.36 | −9.09 | 2/22 |
| ancient | 9 | 66.67 | 70.83 | −4.16 | 1/24 |
| ancient | Mr Chow | 83.33 | 87.50 | −4.17 | 1/24 |
| ancient | 𝙺 𝙰 𝙸 𝚉 𝙴 𝚁 | 66.67 | 70.83 | −4.16 | 1/24 |
| inferno | beyondproof | 84.21 | 89.47 | −5.26 | 1/19 |

The 1-round granularity is the cleanest signal in the whole matrix — every
Δ corresponds to exactly one round where Leetify credits KAST and we don't.

**Suspected root cause:** the missing T in KAST is the same trade-kill the
player is missing per the trade-kill section. Our `has_kast` rule activates
on any of:

- `enemy_kills_round > 0` (K)
- `team_assists_round > 0` (A)
- `context.player.survived` (S)
- `context.player.traded` (T)

The fourth, `context.player.traded`, only activates on `player_death` with
the `enrich.kill.traded_player_slot == player.slot` condition — i.e., when
trade detection fires. So every missing trade kill is a missing T credit
on the partner's KAST round. The 5 missing KAST rounds map cleanly to a
subset of the 7 missing trades.

**Resume here when:** the trade-kill gap closes. Don't fix this
independently — there's no separate signal to chase.

---

## HLTV-rating divergence (formula choice, NOT a parser bug)

**Measured:** 47 affected players, mean Δ = +0.06, max |Δ| = 0.38, mixed
direction (+29/−18). Affects all 5 demos.

This is the largest single contributor to bench mismatch count but is not
a correctness gap — it's a formula choice. Our `HLTV` expression uses the
documented HLTV 2.0 weights (`rules/player_stats.rules.yaml`):

```yaml
HLTV = 0.73 * (kast_pct / 100)
     + 0.3591 * KPR
     - 0.5329 * DPR
     + 0.2372 * (2.13 * KPR + 0.42 * APR - 0.41)
     + 0.0032 * ADR
     + 0.1587
```

Leetify's `hltvRating` uses their own HLTV-style weights that they don't
publish. The deltas are consistent with weight differences in the
high-impact terms (KPR, KAST, DPR).

**Status:** out of scope. Calibrating to Leetify's specific weights would
be a stat-engine-matching project, not a parser-correctness project. The
parser is producing the right inputs; the rating computation is a
downstream choice.

---

## Derived stats (ADR, KD, HS%)

| Stat | Formula | Driver |
|---|---|---|
| ADR | `TotalEnemyDmg / round_number` | enemy-damage undercount |
| KD | `TotalEnemyKills / total_deaths` | TotalK + Deaths (now mostly closed) |
| HS% | `total_headshot_kills / TotalK * 100` | TotalK + headshot count |

**Status:** don't fix independently. The May 2026 pass already saw KD
move closer to Leetify for two dust2 players after the `total_deaths`
fix. Each upstream closure will pull the derived stats along.

The ADR mean Δ across the suite is −0.01 (effectively zero), and the
max |Δ| is 0.16 — the residual is rounding-precision noise on the dpr
division. Not worth chasing.

---

## Assist counting (fixed 2026-07-26)

±1 assists on 5 players across 3 demos, both signs. Root cause: the assist
view's `enemy` facet resolved to `enrich.kill.was_enemy_kill` (killer-team vs
victim-team), but the stat's subject is the *assister* — one wrong-pair gate
produced both signs (team-damage assists counted; enemy assisters on teamkills
dropped; suicides-with-an-enemy-assister confirmed NOT counted by Leetify, so
the view's `KillerSlot != VictimSlot` filter stays). Fix: a new enrichment
`enrich.kill.was_enemy_assist` (assisterTeam vs victimTeam) in
`KillTeamEnrichmentEdge` + `BuiltinContexts`; the assist view's `enemy` facet
repointed; `rules/catalog.json` regenerated. Result: assists match Leetify
50/50 players across all 5 bench demos. Deliberate scope cut: KAST's
`team_assists_round` credit stays on the OLD killer-vs-victim gate (Leetify's
KAST credits assists its `totalAssists` does not — verified against the
oracle), so the KAST residual remains the trade-kill gap's problem. Full
investigation: git history.

---

## Shots-hit-foe mixed sign

**Measured:** 13 affected players across 5 demos. Sign distribution +11/−2,
mean Δ = +0.77, max |Δ| = 4.

The mostly-positive bias (+11/−2) hints at the same source as the
shots-fired overcount. The two negative outliers (−3 each) likely have a
separate cause:

| Demo | Player | Ours | Leetify | Δ |
|---|---|---|---|---|
| mirage | Wifferino | 43 | 46 | −3 |
| ancient | Barry Keoghan's Nose | 41 | 44 | −3 |

The dedup-over-suppression theory was disproven 2026-07-26 (full 5-demo
game-event probe): the `(attacker, victim, tick)` dedup in
`HurtBulletEnrichmentEdge` suppressed zero events on every demo (raw ==
post-dedup for all 50 players); it has been inert since shotguns were
excluded up-front.

The shotgun-inclusion hypothesis was also disproven the same day (it briefly
looked right because Wifferino's −3 numerically equals his 3 shotgun blast
groups — coincidence): every heavy shotgun user across the suite matches
Leetify exactly with shotgun hits excluded, including the in-demo control on
mirage — DaHydraKing (7 xm1014 blast groups) pins 31=31 in the same demo
where Wifferino is −3. Likewise ancient yehright (9 groups) 60=60, nuke
jeremyskills (5 nova groups) 13=13, inferno Toxsick/X3YellowPP (mag7 groups)
22=22/44=44, and 10+ more. Leetify's `shotsFired` also excludes shotgun fires
(yehright 407=407 with 11 shotgun fires excluded) — their per-stat scopes are
coherent and match ours. The up-front shotgun exclusion is re-confirmed, not
contradicted. No code change.

**Residual after the probes:** the +11 direction (each +1/+2, plus Canoga
Park +4 on dust2 with zero shotgun events in the demo) shares the shots-fired
overcount's closure — same unexplained extra-events shape. The two −3s
(Wifferino/mirage, Barry Keoghan's Nose/ancient) have no event-visible
mechanism: Barry has zero shotgun fires AND zero shotgun hurts; raw enemy
bullet `player_hurt` events equal our counts exactly (43 and 41); no
per-weapon category explains it, and no warmup-window or match-restart gap
exists (begin_new_match = 1 on all 5 demos). Whatever adds Leetify's 3 hits
is not derivable from `player_hurt` totals — likely their own
hit-reconstruction internals. Resume when a clean external signal on
Leetify's hit counting arrives (same gate as the shots-fired overcount); do
not tune toward the number.

---

## Entity-decode bit misalignment on MM demos (cured)

The May 2026 entry recorded `FieldPath is full` cascades on all 5 bench MM
demos and a deep-dive attributing them to POV delta-on-unknown skips
desynchronizing the bit cursor. The description no longer reproduces:
`tools/EntityDecodeProbe` reports `LastEntityError == null` with zero decode
errors on all 5 bench demos (90k–138k frames each). The cure was the
2026-06-08 instancebaseline string-table + AnimGraph2 field decode fix —
CUtlBinaryBlock/CGlobalSymbol had been misaligning the bitstream on
current-era demos, exactly this entry's symptom. Nobody re-ran the probe
after that fix, so the entry stayed open while the damage-regression
investigation built (and had falsified) a fix on top of the stale premise.
The lesson: re-verify an old issue's premise before building on it. The
formerly skip-gated `EntityIntegrationTests` now run on the bench demos;
`EntityTracker.DeltaUnknownCount` and the decode trace ring buffer remain in
code as diagnostics. Full investigation (two rounds, the POV-demo analysis,
four attempted fixes): git history.

---

## Workbench test flakes on one dev machine (closed)

Two Rules-Workbench App-suite tests failed on one machine in July 2026;
both passed by the v0.5.1 ship gate, verified individually in isolation.
The honest closure: the "user-config state" theory was never proven, and one
test's brittle hardcoded assertion (`vm.ExtraRows.Count == 10`) was replaced
by a derived one in the Rulesets v1 removal. Anyone hitting a similar
count-mismatch failure should suspect a hardcoded expectation before
suspecting the machine. Full notes: git history.

---

## No malformed-demo policy — Valve-declared limits are not enforced

**Status: Open (investigation).** Raised 2026-07-26 off the back of the
string-table OOM hardening (v0.5.1).

That fix bounded four attacker-controlled sizes in the string-table decoder,
but it fixed one decoder, not a policy. Two gaps remain:

**1. The protos declare limits we never read.** `netmessages.proto` carries
`option (maximum_size_bytes)` on messages (e.g. 49152 on
`CSVCMsg_CreateStringTable`, 262144 on the update message), and other messages
carry their own. Today these are treated as documentation: nothing in the
parser consults them. Worth investigating:
- Which limits are actually authoritative for CS2-era demos versus stale
  Source-1 carryover? (`cs2-opendocs` is the reference; a limit that real
  demos already exceed is not a limit.)
- Are they machine-readable from the generated descriptors (custom options are
  reachable via reflection) so a check could be systematic rather than
  hand-copied constant by constant?
- Which other decoders take an attacker-controlled length or count with no
  bound? The OOM review only swept `StringTableProcessor`. The entity
  decoder, the string-table *snapshot* path, and anything else reading a
  varint length are unexamined.

**2. We have no "stop, this demo is broken" concept.** Partially resolved in
v0.6.0: the structured parse-diagnostics channel now exists —
`ParseDiagnostics` (a per-parse-thread accumulator in the unprotected
`ParseDiagnostics.cs`, modeled on `ParseProfiler`) drains into the
`ParsedDemo.Warnings` list inside the `ParsedDemo` ctor (no `DemoParser.cs`
signature change), and the string-table swallow sites
(`ProcessCreate`/`ProcessUpdate`/snapshot truncation/unreadable-userinfo
player drops) all report through it. Match Overview surfaces a
"THIS DEMO MAY BE DAMAGED" banner when warnings are present — the
every-table-rejected demo is no longer a silent, plausible-looking
no-player parse.

**Still open from gap 2:** coverage inside `DemoParser.cs` itself —
truncated header/payload (the partial-download case), abandoned packet
remainders, `Try<T>` proto-parse failures, and a missing schema all still stop
or degrade silently (those edits were deliberately deferred in v0.6.0; each
is a one-line `ParseDiagnostics.Warn` inside an existing guard branch, and
`Try<T>`'s `context` parameter is already reserved for exactly this). The
abort-threshold design question also remains: everything today is
warn-and-continue.

**Resume here with:** the gap-1 unbounded-read audit; the ~6 one-line
`DemoParser.cs` warn sites; the abort threshold decision.

---

## String-table storage moved to a keyed map (fixed 2026-07-26)

Entries were positionally addressed (`List<Entry>` where position IS the
string-table index), which forced a domain cap on wire-supplied indices.
Fixed in v0.5.1: entries are now `Dictionary<int, Entry>`. The correction
worth keeping: the original entry claimed the index cap could "go away
entirely" once keyed — wrong. Keying decouples max-index from entry-count,
and the 3-bit sequential shorthand carries no entropy (Snappy-compresses to
almost nothing), so ~200 compressed bytes can reach the 16 MiB `string_data`
ceiling and declare ~44.7M distinct keys (~1.6 GiB live). The ceiling changed
what it measures rather than vanishing: `MaxEntriesPerTable` (4096) bounds
memory (entries present, per-table lifetime); `MaxPlayerSlot` (63) bounds
meaning, in `ExtractPlayersFromState`. Also fixed in passing:
`ProcessSnapshot` had no ceiling, and a huge snapshot would have poisoned
every later update on that table. Verified: bounds suite 8 → 12 cases,
Parser suite 120/120 with 0 skips on a real demo, `userinfo` peaks at
exactly 64 entries. Original reasoning + container measurements: git history.

---

## Enemy-damage overcount regression (fixed 2026-08-12)

Found 2026-07-26 by the v0.5.1 ship-gate bench run; it had already shipped in
v0.5.0 (+2…+66 hp on 9+ players, and ADR carried the excess). Mechanism: the
May–June 2026 window contains the instancebaseline/AnimGraph2 decode fix,
which cured entity decode on MM demos (see the bit-misalignment section) and
thereby brought `HurtTeamEnrichmentEdge`'s entity pre-frame HP override alive
for the first time — at golden time it was inert and the event cache did all
overkill capping. The pre-frame snapshot is frame-START health and GOTV
coalesces multiple server ticks per frame, so same-frame multi-hit bursts
(shotgun pellets, sprays) capped burst-ending kills at pre-burst HP. Fix: the
entity override engages only for the victim's first hurt of a frame; later
same-frame hits cap with the event-tracked cache; cross-frame the entity
value stays authoritative (heal-awareness kept). Verified twice by
independent full-suite regens: `enemy_damage` byte-identical to the
2026-05-22 goldens on all 5 demos; the re-baseline moved only the 5 assist
cells (exactly as predicted the day before); the rules-v2 cutover pins were
re-pinned in the same change. The uniform small undercount above is the live
profile again. The first investigation round was falsified by the bench —
its fix was built on the stale bit-misalignment premise and was a no-op on
real demos. The wider lesson is recorded as the parity-fixture gap in
FEATURE-REQUESTS-AND-GAPS.md: unit tests alone prove nothing here; re-verify
against the bench. Full record (per-player tables, the bench-report bisect,
the falsified round): git history.

## `ParsedDemo.Warnings.Count` is nondeterministic across identical parses

Observed 2026-08-13 during an SDK pin bump — which is exonerated: both
packages were same-schema-build regens with no parser-facing change, and the
failing comparison is between two parses *within one process*, neither
involving the new packages' surface.
`EmptyOptions_ParsesIdenticallyToTheOptionsLessOverload` failed once with the
options-less parse reporting 4 warnings and the empty-options parse 3, on
identical bytes through the same private core; two immediate reruns both
produced 3/3 and passed. The differing tuple element is
`ParsedDemo.Warnings.Count` alone — frame, event, player and tick counts were
identical in the failing run.

Suspected mechanism (not yet instrumented): warning emission racing in the
two-pass parallel parse. This demo's warnings include per-type
dropped-message coalescing (`net-message-dropped … count=`) and the
`userinfo` string-table skip; a thread-timing-sensitive coalesce or dedup
when per-worker warnings merge into the final list would produce exactly a
±1 entry count with an otherwise byte-identical parse. Root-causing means
walking `DemoParser.cs`'s parallel merge, so it stops at suspicion until it
earns priority.

Impact: cosmetic for users (diagnostics list length), but it makes any
equality assertion over `Warnings.Count` flaky — the ParseOptions parity
tuple in `ParseOptionsTests` is the only current consumer. If it recurs,
either instrument the warning merge or drop the warnings element from the
parity tuple with a pointer here.

Status: **OPEN** — one observation, reproducible only by chance so far.

For each open item above, document any new finding here. A reopen counts
as evidence that the gap is real and we're not satisfied with the local
minimum — record what changed your mind. A closure counts as a fix
landed; cross-reference the date and update the quick-reference table
above.

Do not close a gap by tuning toward Leetify's number without a
documented mechanism. The local-minimum trap is real (see the trade-kill
4s-vs-5s experiment). Better to leave the parser at the documented-correct
value and accept the residual.

---

## Tooling reference

| Tool / probe | What it gives you |
|---|---|
| `AnalysisBench --suite` | Per-demo accuracy %, per-stat mismatch breakdown |
| `AnalysisBench --shots-debug=<player>` | Per-event timeline + damage-cap instrumentation + post-death-shot count for one player |
| `AnalysisBench --round-debug` | Per-round event trace (one demo) |
| `tools/EntityDecodeProbe` | `dotnet run -- <demo.dem>` — replays a single demo through `EntityTracker` and dumps `LastEntityError`. Confirms whether the bit-misalignment failure fires on a given demo. |
| `bench-reports/*.json` | Persistent per-run snapshots — diff vs. older runs to catch regressions |
| `tests/fixtures/<demo>/ours.golden.json` | Frozen ours-side stats. They update only on a `--suite` run WITHOUT `--no-golden` — a deliberate re-baseline, not "each bench run". In practice they sat unchanged from 2026-05-22 to 2026-08-12, which is how the damage regression hid for six weeks. Nothing in the test suite re-derives them from live code. |
