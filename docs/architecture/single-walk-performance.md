# Single-walk performance at release

The single walk takes the sweep from 6 entity decodes per demo to 1 and the open from 5 to 1, and is
25.9% to 48.2% faster than `main` on the sweep and 21.2% to 38.2% faster on the open. Against M9,
the last milestone before the swaps and shell fixes, the final stack is slower and uses more memory:
open wall +11.6% to +23.0%, sweep wall +6.4% to +8.9%, sweep peak heap +15.1% to +19.5%. Every Gate G
perf miss was accepted for release by the owner, 2026-10-17. This page records the numbers, what
probably causes the M9 regression, and where to look first when it is worked on.

## What the harness measures

`tools/single-walk-bench/run.sh` runs `SingleWalkVisitProbe` once per arm, demo, visit and trial,
each in its own process with a fresh config directory, and reports `wallMs`, `peakWsMb`,
`peakHeapMb`, entity decodes and per-pass time.

- **sweep**: import with every background pass on and every stamp absent.
- **open**: import with the sweeps off, then open through the shell. The whole open is measured:
  read, parse, the shell's rules run and scoreboard write, then the open's visit.
- **recheck**: import with the sweeps on, then re-enlist the demo for the library's score repair.
  Only the re-check visit is measured, with the library row present and every stamp current.

## Arms and conditions

| arm | commit | engine |
|---|---|---|
| main | `27c8c7fdd` | CS2DemoKit 0.13.0-beta0001 |
| base (M2) | `a3189b7c9` | 0.13.0-beta0001 |
| M9 | `55d27a54e` (`sw/strat-observers-grenades` on `sw/local-engine`) | 0.14.0-alpha0001 |
| final | `08eaab2ce` (`sw/landing-fixes+le`) | 0.14.0-alpha0001 |

M9 and final ran byte-identical engine DLLs. Five demos from `demos/benchmarks` (172 to 279 MB),
`--trials 5` after one discarded trial, Release. "Disjoint" means the two arms' 5-trial min..max
ranges do not touch.

Every leg ran with Rider open under the owner's 2026-10-16 waiver. load1 was 2.70 to 6.89 for final
vs main and 3.59 to 6.83 for final vs M9; the runbook's "load1 above 2 voids the round" rule was off
and no round was dropped. These are not quiet-machine numbers. A/A bands on the same machine were
roughly -8% to +7% on wall.

## Final vs main

| visit | wallMs | peakHeapMb | peakWsMb | decodes per demo |
|---|---|---|---|---|
| sweep | -48.2% to -25.9%, disjoint 5 of 5 | +0.8% to +27.0%, disjoint above 4 of 5 | +5.4% to +9.6%, disjoint 5 of 5 | 6 to 1 |
| open | -38.2% to -21.2%, disjoint 5 of 5 | -2.2% to +8.5%, mixed | +3.0% to +9.3%, mixed | 5 to 1 |

The decode counts come from different counters. main reports the host hooks (`counterSource=hooks`),
which do not see shell replays; final reports the engine's decode meter. main's 6 and 5 are a floor.

## Final vs M2 (base)

Judged against a pooled base band of 20 samples per demo. Wall is below the band on all 5 demos:
sweep -20.3% to -43.7%, open -14.5% to -32.6%. Sweep peakWs is above on 5 of 5 (+5.5% to +10.3%) and
sweep peakHeap above on 4 of 5 (+13.5% to +25.9%; the smallest demo, `0544286934`, holds at -1.1%).
Open memory is mixed in sign.

## Final vs M9

| visit | wallMs | peakHeapMb | peakWsMb |
|---|---|---|---|
| sweep | +6.4% to +8.9%, disjoint 5 of 5 | +15.1% to +19.5% on 4 of 5, disjoint (`0544286934` +1.0%, overlaps) | +4.2% to +9.3%, disjoint 4 of 5 |
| open | +11.6% to +23.0%, disjoint 5 of 5 | -7.2% to +9.0%, mixed | +1.3% to +6.3%, mixed |
| recheck | -3.9% to +4.2%, overlaps on all 5 | -1.3% to +3.7% | -0.1% to +1.1% |

The same regression was first recorded on `43ec83a41` (open +14.2% to +22.8%) and is unchanged by
the landing fixes. Recheck sits inside its A/A noise.

Open, per pass, median final minus M9 over the 5 demos:

| pass | delta | relative |
|---|---|---|
| roundindex | +171 to +209 ms | +107% to +140% (about 2x) |
| grenades | +72 to +89 ms | +68% to +96% |
| shell-rules | +108 to +778 ms | +2.1% to +15.0% |
| suggestedtags | -0.1 to -11.1 ms | flat |
| remainder (wall minus the four passes) | +416 to +570 ms | +28% to +48% |

Each row is its own median, so the rows do not sum to the wall delta. The harness does not time
anything inside the remainder.

## M9 residual peak heap

M9 against the pooled M2 band (n=20), the Gate G item carried from M9:

| check | separated above | delta | verdict |
|---|---|---|---|
| sweep peakHeap | `0003771537`, `0126308730` | +4.9%, +7.1% | miss |
| open peakHeap | `1168926414` | +3.4% | miss |
| sweep and open peakWs | none | -1.4% to +2.3% | holds |

Same demos and direction as the M9 measurement (+5.4%, +5.5%, +4.3%).

## Likely causes

Nothing below has been isolated by a run. The per-pass table above is measured. Each mechanism is
read from the code between `55d27a54e` and the tip, and is labelled as likely.

**The open no longer runs the two extra decodes, so they are not a cause.** `753b6d305` takes the
game clock from the open's own rules run (`GameClockSource` wraps the freeze-period poll) instead of
replaying a tracker to the first `round_freeze_end`, and owes the landing entity seek while the
selected tab reads no entity state. `c62179c45` settles that clock past a tick's trailing frames.
Both remove work. The wrapper and the fresh `EntityValueProviderRegistry` it builds per open are not
measured.

**roundindex and grenades passes, about 2x (likely: row materialization).** At M9 these passes folded
what their collectors handed them. Now they read tables off the visit's rules run, and each row is
built three times. `MergedRulesBuild.Table` projects the table through `ProjectConfiguredOutputs`,
then copies every row into a `FactRow` of two dictionaries through `FactsCodec.FromEngine`.
`RoundIndexTable.Samples(IEnumerable<FactRow>)` and `GrenadeTables.Read(Func<string, ...>)` then turn
each `FactRow` back into two object dictionaries (`Cells`), and the round index sorts the result.
The grenade pass does this for 8 tables, with one projection call per table. The round index table
is every player once a second, which is tens of thousands of rows per demo.

**shell-rules, +108 to +778 ms (likely: rules cost more than the collectors they replaced).** M9's open
run carried the round index and grenade collectors (`OpenCollectorSet()`). Since `3ec34aa79` the open
reads `MergedRulesBuild.ReadRules()`, so the same run carries four Strat Book rulesets in their place:
`roundindex_positions` (a one-second cadence per player), and `grenade_projectiles`,
`grenade_releases` and `grenade_events`. Three engine features they use do per-frame work in place
of what the collectors did:

- `for: each_projectile` builds an instance per projectile. CS2DemoKit's own R6 bench puts that at
  +6.6% `eval_ms`, measured.
- The `@-7`, `@-8` and `@-15` look-backs make `RuleChainBuilder` call `KeepHistory` for about ten
  per-player columns. `EntityChangeScanner.RecordHistory` then walks every history column for every
  changed row on every frame. These histories replace the M9 collector's pawn rings.
- `thrower.cmd.last_jump_tick` and `thrower.cmd.coverage` set `TrackInputs` on the scanner's
  `UserCmdReconstructor`, which turns off the on-demand queue (`queue = OnDemand && !TrackInputs`)
  and applies every command of every slot as it arrives. M9 decoded every command too: its
  `GrenadeCollector` declared `ReadsUserCommands` and read every slot's command each frame into its
  command rings.

No run has compared these paths against the collectors' rings, so their share of the delta is not
known.

The spread across demos (+108 ms to +778 ms) is wider than the per-row costs alone explain, and no
run has split it by ruleset.

`library_teams` and `library_coaches` ride the open run on both arms (`LibraryTeamTables.Tables` is
in M9's open cut). Between M9 and the tip `library_teams` changed only from flow to block style, so
neither ruleset is part of the delta.

**remainder, +416 to +570 ms (unmeasured).** The harness has no timer inside it. Code that is new in
that span: the `StaleOutputs` query to the passes before every open (`AnalysisViewModel.RunOpenAsync`,
off the UI thread), the extension's rulesets joining the per-open ruleset parse (`ReadRules`),
and two more `each_player` rulesets whose per-player nodes `BuildGraphViewModels` and
`BuildPlayerTables` now walk. Extra GC from the higher heap would also land here. None of these is
measured.

**Sweep peak heap, +15% to +20% on 4 of 5 (likely: rows held to the end of the run, then copied).**
The rise grows with demo length and is flat on the smallest demo. That fits the recorded table rows
staying alive for the whole run, with the projection, the `FactRow` copies and the `Cells` copies
all alive at once while a pass reads them. The M9 collectors produced `PositionSample` and grenade
records directly.

## Candidate improvements

- **Read a pass's tables once, without the `FactRow` round trip.** Give `IPassContext` a reader that
  projects all of a pass's tables in one `ProjectConfiguredOutputs` call and hands over `MetricTable`
  rows. `RoundIndexTable.Samples(ParsedDemo)` and `GrenadeTables.Read(AnalysisRun, int)` already read
  `MetricTable` rows directly, so the fold code exists. This takes out two dictionary copies per row
  and seven extra projections in the grenade pass.
- **Read the round index samples into a typed buffer.** `RoundIndexTable.Samples` builds a keyed list
  of every row and sorts all of it. If the projection already yields rows in tick order, sorting by
  entity index within each tick is enough.
- **Keep the on-demand command queue for `coverage` and `last_jump_tick`.** In CS2DemoKit,
  `UserCmdReconstructor` could track the jump edge and the coverage window over queued frames,
  instead of `TrackInputs` forcing every command to be applied. This is engine work, in
  `UserCmdReconstructor.AdvanceOneFrame`.
- **Shrink the look-back set.** `grenade_projectiles` reads ten columns at `@-7` and two at `@-15`;
  `grenade_releases` reads three at `@-8`. Fewer history columns means less work in `RecordHistory`.
  Check that the grenade digests stay byte-identical.
- **Release the run's table rows once the pass has read them.** For the sweep peak, the recorded rows
  could be dropped after the last reader, or the reader could stream rows instead of materializing
  the projection.
- **Time the open's remainder before changing it.** Add stage timers in
  `SingleWalkSession` around the parse, `StaleOutputs`, `BuildFromConfig`, the graph and table
  view-model builds, the scoreboard write and the visit hand-off, and report them as passes.

## Re-measuring

Run under the machine's heavy-job lock, one demo per call (a sweep and open call of 5 trials took
about 6 to 8 minutes). Build each arm once in Release, then pass `--no-build`:

```
tools/single-walk-bench/run.sh --a <m9-checkout> --b <final-checkout> \
  --demos <demo-list.txt> --visits sweep,open --trials 5 -c Release \
  --tmp <scratch> --out <out.jsonl> --no-build

tools/single-walk-bench/run.sh --a <m9-checkout> --b <final-checkout> \
  --demos <demo-list.txt> --visits recheck --trials 5 -c Release \
  --tmp <scratch> --out <out-rc.jsonl> --no-build
```

Omit `--b` for the A/A run that sets the session's noise band. The per-pass breakdown comes from the
`passMs` field of each row in the jsonl. `run.sh` warns when rows mix `counterSource` values; do not
compare decode counts across those rows.
