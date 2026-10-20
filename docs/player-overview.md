# Player Overview

The Player Overview tab reads one player across every demo in the library. Its numbers come from the
cached `player_match` facet of each demo ([`player-match-format.md`](player-match-format.md)); the tab
never runs the engine.

## Aggregation rules

Every Stats tab column (`ColumnCatalogue`) has exactly one rule in `ColumnRules`, and
`ColumnRuleSweepTests` fails for a column without one. The facet stores exactly the columns the rules read.

| Rule | Pooled value over a player's demos |
|---|---|
| `Sum(c)` | Σ c |
| `DifferenceOfSums(a, b)` | Σ a − Σ b |
| `RatioOfSums(n…, d…, scale)` | (Σ n) / (Σ d) × scale, where each side may add several columns |
| `WeightedMean(v, w)` | Σ (v × w) / Σ w |
| `NotAggregable` | none |

A ratio is never averaged across demos: the components are summed and divided once, so a 10-round demo
counts a third as much as a 30-round one. A zero denominator reads 0, as the engine's division does. The
pool of a single demo is that demo's Stats tab value (`PlayerMatchParityRealDemoTests`).

`rounds` is the match's round count, the engine's divisor for every per-round rate. A player's own rounds
played is never a denominator: the engine does not use one, and no source measures it reliably.

| Column | Rule | Facet inputs |
|---|---|---|
| ADR | RatioOfSums | EnemyDmg / rounds |
| KPR | RatioOfSums | TotalK / rounds |
| Surv% | RatioOfSums ×100 | Survived / rounds |
| KD | RatioOfSums | TotalK / TotalD |
| HS% | RatioOfSums ×100 | TotalHS / (TotalK + TeamKills) |
| KAST% | WeightedMean | KAST% weighted by rounds: Σ KAST rounds / Σ rounds |
| HLTV | WeightedMean | HLTV weighted by rounds (HLTV 2.0 is linear in its per-round terms) |
| Acc% | RatioOfSums ×100 | HitFoe / Shots |
| HSAcc% | RatioOfSums ×100 | head_hits / HitFoe |
| HSDmg% | RatioOfSums ×100 | head_damage / EnemyDmg |
| CS% | RatioOfSums ×100 | cs_clean / CSAtt |
| CSAll% | RatioOfSums ×100 | cs_clean / Shots |
| Linear% | RatioOfSums ×100 | moving_shots / Shots |
| Spray | RatioOfSums | spray_error_sum / SprayN |
| AvgHP→Dmg | RatioOfSums | health_at_damage_sum / damage_instances |
| AvgBlind | RatioOfSums | blind_duration_sum / EFlash |
| Equip | RatioOfSums | equipment_sum / equipment_rounds |
| Duel% | RatioOfSums ×100 | TotalFK / (TotalFK + TotalFD) |
| FK± | DifferenceOfSums | TotalFK − TotalFD |
| FB% | RatioOfSums ×100 | first_bullet_hits / first_bullet_shots (sight) |
| SAcc% | RatioOfSums ×100 | contact_hits / contact_shots (sight) |
| SprayAcc% | RatioOfSums ×100 | spray_hits / spray_shots (sight) |
| Preaim | RatioOfSums | preaim_angle_sum / Spots (sight) |
| XPlace, FlickErr | RatioOfSums | travel_sum / XShots, flick_error_sum / XShots (sight) |
| TTS, TTD, AimRx, TTK | RatioOfSums ×15.625 | tts_ticks / TTSn, ttd_ticks / TTDn, aimrx_ticks / AimRxn, ttk_ticks / TTKn: ticks to ms (sight) |
| Spots, XShots, TTSn, TTDn, AimRxn, TTKn | Sum | the column itself (sight) |
| TotalK, TotalD, TotalA, TotalHS, FlashAst, TrdK, TradedD, Clutch, RapidKills | Sum | the column itself |
| CSAtt, SprayN | Sum | the column itself |
| EnemyDmg, TeamDmg, SelfDmg, HitFoe, HitTeam, Shots | Sum | the column itself |
| TotalFK, TotalFD, CTFK, CTFD, TFK, TFD | Sum | the column itself |
| AWP, Pistol, Rifle, SMG, Knife, DeagleHSRnds | Sum | the column itself |
| NoScope, WB, Smoke, Blind, Revenge, FlashK | Sum | the column itself |
| HE, Flash, Smokes, Molly, EFlash, Plants, Defuses, Armor | Sum | the column itself |
| 2K, 3K, 4K, 5K, CTW, CTL, TW, TL, Survived | Sum | the column itself |
| Kills, Deaths, Assists, Damage, UtilDmg, Traded, HasKAST, HSKills, EKills, Flashed, FK, FD, DeagleHS, Spot | NotAggregable | round-board cells, not match totals |

Peer scales are never used on a cross-demo board; a column keeps its absolute colour band only.

### Line-of-sight columns

The columns marked (sight) are the Stats tab's `sight: true` columns. They exist only when the run had the
map's collision geometry, so they pool over a narrower set of demos: those whose `player_match` stamp has
`HasVisibility` ([`player-match-format.md`](player-match-format.md#line-of-sight)). A demo without it is left
out of them, not counted as 0, and when no demo in the window has it the column has no value at all and the
tab says "no data". `PlayerAggregate.WithSight` is the count of demos they pool.

The background visit that writes the facet has no geometry; an interactive open of a demo whose map has a
bake upgrades that demo's facet. Handing the background run the geometry was measured and rejected: on the
five benchmark demos, interleaved over three rounds, the forward visit with a `VisibilityEngine` took 14 to
23% longer per demo (median about 17%), allocated 27% more and peaked 280 MB higher in working set.

## Which demos count

* One row per demo content: a demo found under two paths counts once, because the cache keys a demo by its
  content hash and lists every path on that one row.
* A deleted demo drops out of the aggregates when its row is orphaned, and comes back if a file with the
  same bytes returns within the cache's grace period.
* Within a demo, an account's rows are merged by `steam_id` before pooling, ghost slots dropped
  ([`player-match-format.md`](player-match-format.md#dimensions)).
* Bots, coaches and SteamID 0 are never subjects.

## Stat categories

The categories section lists the catalogue's groups over the filter's window: opening duels, trades, clutches,
utility, weapons, multi-kills and economy. Each value is the column's pool by the rules above, labelled and
banded as on the Stats tab (absolute bands only). Beside a count sits a rate that reads across a career: per
round over the summed `rounds`, per match over the demos with stats, or a share (weapon kills of all kills,
traded deaths of all deaths, armor rounds of all rounds). Aim quality is not a category: it has a section of
its own, which the categories' footer points to.

## Aim quality

The aim quality section lists reaction (TTS, TTD, AimRx, TTK), crosshair placement (Preaim, XPlace, FlickErr),
the after-contact hit rates (FB%, SAcc%, SprayAcc%) and the accuracy columns every demo carries (Acc%,
HSAcc%, HSDmg%, CS%, CSAll%, Linear%, Spray), each with the population behind it from the catalogue's
denominator ("after 412 contacts"). A line-of-sight column reads "no data" when no demo in the window has
sight, and the note under the blocks says how many matches were left out and that opening one on a map with a
collision bake adds them. The summary carries two of them as tiles, time to damage and preaim, with the same
"no data" rule.

## Teammates

The teammates section lists everyone who ended one of the subject's demos on the same side, over the filter's
window, the most shared first. A demo either of them left early is not shared: sides come from the index's
side lists. The record and win rate together are the subject's outcomes in those demos, and each teammate's
HLTV is their own `player_match` rows in the shared demos pooled by the HLTV rule above. The subject's facet
read keeps every other account's rating inputs, so a teammate costs no extra read. A shared demo whose facet
is not written counts as a match together and adds nothing to the win rate or the rating.

## Head-to-head

The head-to-head section pits the subject against one opponent, picked from everyone who ended one of the
subject's demos on the other side, the most often met first. It covers the demos the two ended on opposite
sides: the record is the subject's wins, losses and ties there from the `player_match` facet, and the kills
each way, the K/D between them and the headshot share of the subject's kills come from each demo's
`player_event` facet ([`player-event-format.md`](player-event-format.md)). A demo whose `player_event` facet
is not written yet is listed as pending and adds nothing to the counts.

## Library benchmarks

The benchmarks section places the subject's win rate, rating, K/D, ADR, KAST, KPR, HS% and opening-duel win
rate among the other players in the library. The population is every account other than the subject with at
least the chosen number of demos with stats (5 by default) among the demos that pass the subject's filter;
each account is pooled over those demos by the rules above. With "only my Premier tier" ticked, the
population keeps only accounts whose latest CS Rating in the library is in the subject's 5,000-wide Premier
tier; the option shows only when the subject has a rated Premier demo. Competitive and Wingman Skill Groups
are per map and are not used for bands.

A percentile is the share of the population below the subject, a tie counting half; it is not flipped for a
lower-is-better column, only its colour is. A key whose catalogue gate leaves an account too few attempts
(opening duels) counts only the accounts that pass it. Fewer than 5 players give no percentiles. The line
above the rows always names the population: "vs 214 players in your library · 5+ matches each", with the
tier and "same filter" added when they apply.

Every account's rows come from one library-wide fill of `PlayerRowCache` that reads each demo's
`player_match` facet once, merges every account in it, and shares that read with the subject's own fill; the
ratings come from `PlayerRankCache`'s library read of each demo's record. Both run off the UI thread, and so
does pooling the population.
