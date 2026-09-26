# Pre-existing test failures: triage

Read-only triage of the four failures the Phase 0 verifiers reported as pre-existing. Nothing
under `src/`, `tests/`, `rules/` or `assets/` was changed. Every run below was made on this
machine (Windows 11 26200, .NET 10.0.12, TUnit 0.25.21) from detached worktrees under the session
scratchpad, against the committed sources at each commit; the replays were opened read-only.

| Test | Branch `f9b2b23` | Main `d90ec9f` | Verdict |
|---|---|---|---|
| `Playback2DBombTimerTests.BombTimer_IsC4TimerLength_AtPlant_AndDecreases` | fails | fails identically | replay-specific: the first plant's round ends 7 s later |
| `SprayControlOracleRealDemoTests.ShippedSprayColumn_IsWired_AndReportedBesideTheOracle` | fails | fails identically | replay-specific: build 10231 does not network the ballistics keys |
| `BackendParityTests.GpuMatchesTheCommittedCpuGoldens` | fails | fails identically | main regression by PR #19 (SkiaSharp 3), known, decision left open |
| `BackendParityTests.GpuMatchesLiveCpuRender_WithinPerceptualTolerance(synthetic-utility)` | fails | fails identically | same cause as the row above |

None of the four is attributable to the strat-book workstream. The two App failures are properties
of the replay the folder `DEMO_PATH` resolves to by default; the two Playback2D failures are a
consequence of a merged main commit on this class of GPU, documented in that commit's own PR.

## Commits, replay, machine

- Branch: `feature/strat-book` at `f9b2b23` ("content identity: one demo content hash ...").
- Main: `d90ec9f` ("revert the node editor and the vendored nodify fork out of main (#35)"), which
  is also the merge base of the strat-book branch.
- Pre-#19 control: `10702d9` ("refresh the rule-graph plan against the tree it now describes (#18)"),
  the last main commit before `2072b9d` "Avalonia 12.1.2 and SkiaSharp 3.119.4 (#19)".
- Replay: `match730_003731893271710924851_1024675027_129.dem`, de_nuke, GOTV matchmaking,
  build 10231, patch 14062, 154,869 frames, file dated 2025-01-25. It is the ordinal-first `.dem`
  in the Steam replays folder, so `DEMO_PATH=<replays folder>` resolves to it unless
  `DEMO_PATH_PICK` names another (the reference demo `003816248937665266002_0544286934.dem` is not
  in that folder). That is why every full-tier verify hit the same two failures.
- Control replay: `match730_003844252717140672725_0377894676_389.dem`, de_dust2, build 10896,
  patch 14181, dated 2026-09-22. Both App tests pass on it.
- GPU: NVIDIA GeForce RTX 4070 Ti SUPER, driver 32.0.16.1714 (2026-09-16), the same hardware the
  parity test's tolerance comment was measured on. An AMD Radeon iGPU is also present. ANGLE pin
  at `d90ec9f`: `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419; at `10702d9`: 2.1.25547.20250602.

## (a1) `BombTimer_IsC4TimerLength_AtPlant_AndDecreases`

**What it asserts.** Calibrates `clockBase` from the first `round_freeze_end`, takes the FIRST
`bomb_planted` frame plus 8, activates a real `Playback2DTabViewModel` there and reads
`GameInfo`: `BombTicking` must be true and `RoundSeconds` (the detonation countdown,
`m_flC4Blow` minus corrected curtime) must be in (38.5, 40.5]. It then reads the same thing at the
first frame whose `ServerTick` is at least 10 s later and asserts the countdown dropped by
8.5 to 11.5 s.

**How it fails here.** Both worktrees print the same line and the same assertion:

```
[bomb-timer] match730_003731893271710924851_1024675027_129.dem clockBase=-31.062
  plantFrame=41238 gateFrame=41246 ticking=True detonation=39.91s later=NaNs
Expected delta to be greater than 8.5 but it was NaN
```

The plant-side half passes (39.91 s). The "later" half reads NaN.

**Why.** The first plant in this replay does not survive ten seconds. From the probe:

```
frame 41013 tick 35395: bomb_beginplant
frame 41238 tick 35595: bomb_planted
frame 41250 tick 35605: player_death          (10 ticks after the plant)
frame 41779 tick 36053: round_officially_ended, round_prestart, round_poststart
frame 42005 tick 36241: the test's "later" frame (gate tick 35601 + 640)
```

At frame 41246 the C4 entity reads `ticking=1 defused=0 blow=627.23 timerLength=40` and the rules
proxy reads `freeze=0 planted=1 roundsPlayed=5`. At frame 42005 there is no `CPlantedC4` entity at
all and the rules proxy reads `freeze=1 planted=0 roundsPlayed=6 roundStart=614.39`: the next
round's freeze period. The death at tick 35605 ended the round (a terrorist win by elimination
with the bomb down), `round_officially_ended` followed 448 ticks later (7.0 s, the restart delay),
and the entity was torn down with the round. (This demo decodes zero `round_end` events in the
whole file, so the end is evidenced by the rules proxy and `round_officially_ended`, not by the
event.)

`SceneFrameBuilder.UpdateBombTimers` finds no live ticking `CPlantedC4`, returns false, and the
freeze branch sets `_hudRoundSeconds = double.NaN` with `RoundTime = "freeze"`
(`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/SceneFrameBuilder.cs`, the branch after
`UpdateBombTimers(input)`). That is the correct HUD state for a freeze period. The test's premise,
"the first bomb is still ticking ten seconds after it was planted", is what fails, not the timer.

**Main.** `d90ec9f` fails with the identical line and assertion; `f9b2b23` likewise. The dust2
control replay passes both tests in the class (39.88 s at the plant, 29.88 s ten seconds later).
`DefuseTimer_IsSane_DuringDefuseInProgress` passes on both replays and both commits. The file was
last touched by PR #10 (2026-09-01); nothing on main since changes the assertion or the countdown.

**Verdict.** Replay-specific. Not a regression, not a product defect.

**Smallest honest fix.** In the test, stop assuming the first plant: iterate the `bomb_planted`
frames in order and use the first one whose ten-second window stays in the same round, which the
existing helpers can decide by checking that `ReadGameInfoAt(..., laterFrame).BombTicking` is
still true (or, cheaper, that no `round_officially_ended` falls between `gateFrame` and
`laterFrame`); skip with a reason when no plant qualifies. That keeps every assertion as written
(the 40 s at plant, the 1 s per second decay) and only changes which plant is measured. Do not
widen the window or accept NaN. Alternatively leave the test alone and name a replay via
`DEMO_PATH_PICK`; the pick is the reason this surfaced, and the ordinal-first choice of the oldest
replay in the folder will keep surfacing it.

## (a2) `ShippedSprayColumn_IsWired_AndReportedBesideTheOracle`

**What it asserts.** Folds the demo's `bullet_damage` stream into oracle shots (skipping only when
there are none), derives the shipped aim columns through `LiveAimStats.Derive`, prints the two
side by side, and asserts three things: at least one player was compared, no shipped Spray is
outside [0, 180] degrees, and at least one player is "wired" (shipped `spray_residual_shots` > 0
AND `spray_error_deg` > 0). The third is the one that fails: `Expected wired to be greater than 0
... but it was 0`.

**How it fails here.** On both commits the table is byte-identical: every player has a populated
SprayN (18 to 53) and a Spray of 0.00, and the oracle's own columns are 0.00 for every player as
well (pitch, yaw and angle), over 683 landed shots. The sibling test
`LandedShotFold_HoldsTheSprayInvariants_AndReportsThePopulation` passes, vacuously.

**Why.** The replay's own `GE_Source1LegacyGameEventList` declares eleven keys on `bullet_damage`:
`victim`, `victim_pawn`, `attacker`, `attacker_pawn`, `distance`, `damage_dir_x/y/z`,
`num_penetrations`, `no_scope`, `in_air`. It does not declare `shoot_ang_x/y/z`,
`aim_punch_x/y/z`, `recoil_index`, `attack_tick_count/frac`, `render_tick_count/frac` or the
`inaccuracy_*` keys. CS2DemoKit 0.12.0's typed `BulletDamageEvent` fills the absent keys with
zero, `ParsedDemo.Warnings` is empty and `Health` is `Clean`, so nothing flags it. Every one of the
683 events has `ShootAngX = ShootAngY = 0`, `AimPunchX = AimPunchY = 0`, `RecoilIndex = 0`;
`Distance` and `DamageDir` are populated, which is why the fold sees shots at all.

With every shot direction equal to (0, 0), each landed bullet's angle from its run's anchor is
exactly zero. The engine's `spray_residual_measured` flag still latches (so SprayN counts), but
`spray_error_sum` sums zeros and `spray_error_deg = spray_error_sum / max(spray_residual_shots, 1)`
is 0.00. The residual itself is computed inside `CS2DemoKit.Analysis`, not in this repo. The
oracle reads the same fields through the event and reaches the same zero, which is the proof that
the input is empty rather than the engine miswired.

The keys arrive with a later game build. Probed across the replays folder:

| replay date | build | `bullet_damage` keys | declares `shoot_ang_x` |
|---|---|---|---|
| 2025-01-25 | 10231 | 11 | no (the failing replay) |
| 2025-03-20 | 10329 | 11 | no |
| 2025-07-24 | 10477 | 26 | yes |
| 2025-08-27 | 10521 | 26 | yes |
| 2026-01-06 | 10603 | 26 | yes |
| 2026-09-22 | 10896 | 26 | yes (the passing control) |

So every replay in the folder dated before mid-2025 fails this test the same way, and the
ordinal-first pick lands on the oldest one. The test's class summary states the premise it relies
on ("`bullet_damage`, which carries the shooter's shot direction, aim punch and recoil index on
the wire") as if it held for every demo carrying the event; it holds from roughly build 10477 on.

**Main.** `d90ec9f` fails identically (the two logs differ only in the worktree path);
`f9b2b23` likewise. The file was added by PR #13 (2026-09-14) and untouched since.

**Verdict.** Replay-specific, by game build. Not a regression.

**Smallest honest fix.** Extend the existing skip guard: after `LandedShots(demo)`, skip when no
shot carries a non-zero shot direction or recoil index, with a message naming the demo's
`BuildNumber` and the fact that its event list does not declare the ballistics keys. That is a
three-line change in the test that mirrors its existing "this demo carries no bullet_damage" skip,
and the same guard belongs in the sibling invariants test, which currently passes on zeros. The
cleaner version reads the descriptor (`GE_Source1LegacyGameEventList` -> `bullet_damage` ->
`keys` contains `shoot_ang_x`), but that needs a helper the test project does not have yet; the
all-zero check is sufficient because a real ballistics stream cannot be all zeros over hundreds of
shots. Leave the assertion itself alone: on a demo that declares the keys, "SprayN > 0 and
Spray = 0 on every player" is still the wiring defect it was written to catch.

## (b) `BackendParityTests` on this machine

**What they assert.** Each of three fixtures (`synthetic-empty`, `synthetic-tenplayers`,
`synthetic-utility`) is rendered through the production layer stack twice, on the CPU raster
backend and on the ANGLE/D3D11 GPU backend, and compared with
`GoldenImageComparer.Compare(..., _provisionalCrossBackend)`. The live test compares GPU against
a fresh CPU render at 1280x720 and 1920x1080; the goldens test compares GPU against the committed
CPU golden at the fixture's own 640x360. `_provisionalCrossBackend` is
`GoldenTolerance.CrossBackend` (= `DefaultPerceptual`: perceptual mode, channel delta 8, at most
0.5 % mismatched pixels, mean SSIM at least 0.995) with `OutlierChannelDelta` raised 32 -> 48 and
`MinWindowSsim` lowered 0.95 -> 0.85, scoped to that file. The comment records the measurement
those numbers came from: RTX 4070 Ti SUPER, worst single-channel delta 46, hence the 48 ceiling.

**How they fail here.** Identical on `f9b2b23` and `d90ec9f`:

```
synthetic-empty        (all sizes)      match    maxDelta=0
synthetic-tenplayers   (all sizes)      match    maxDelta=43  outliers 0.026 to 0.237 %
synthetic-utility vs golden @640x360    MISMATCH maxDelta=88  outliers=0.5946 %  aboveCeiling=0.0139 %
                                                 ssim=0.99896 minWindowSsim=0.89516 alphaDelta=0
synthetic-utility live @1280x720        MISMATCH maxDelta=88  outliers=0.1446 %  aboveCeiling=0.0035 %
                                                 ssim=0.99963 minWindowSsim=0.92948 alphaDelta=0
```

Two limits are exceeded on the 640x360 golden comparison (peak delta 88 > 48, mismatched
fraction 0.595 % > 0.5 %) and one on the 1280x720 live comparison (peak delta 88). SSIM, window
SSIM and alpha all pass. The diff image shows the grid lines, the two smoke discs (rim and
interior), the two trail strokes, the ring geometry and the glyph ink, all as thin antialiasing
differences; nothing is displaced or missing.

**Why, and the control run.** The tolerance was measured under SkiaSharp 2.88.9. Main commit
`2072b9d` (PR #19, merged 2026-09-20) moved SkiaSharp to 3.119.4 (with Avalonia 12.1.2) and
re-authored twelve of the thirteen CPU goldens, `synthetic-utility@640x360.png` among them. On
this machine, today, the four parity cases at `10702d9` (the commit before #19) all pass with
`maxDelta=46` on `synthetic-utility` at every size; at `d90ec9f` the same fixture reads 88. So the
change is the Skia major, not the driver, the ANGLE build or anything on the strat-book branch.

PR #19's description records exactly this: a table with 46 (ceiling 48) on main against 88 on the
branch, the mismatched fraction 0.211 % against 0.595 %, ANGLE ruled out by pinning the old native
back and reproducing 88, and the diff characterised as antialiasing on vector edges. The author
left `_provisionalCrossBackend` untouched on purpose ("doubling a peak-delta ceiling reduces what
that gate can catch") and, after CI, recorded that the pair passes on the `render-backends
(windows-latest, angle-warp)` lane, which made the failure discrete-GPU-specific and "weakens the
case for touching `_provisionalCrossBackend` at all". The real-GPU lane (`render-backends-gpu`,
self-hosted, `DV2D_RENDER_BACKEND=force-gpu`) runs only on a `gpu-lane` label or a manual
dispatch and is deliberately outside the required checks, so CI does not see this failure.

**Verdict.** A regression on main in the strict sense: a merged commit moved the GPU-versus-CPU
delta on discrete GPUs past a ceiling that was measured for them. It is known, documented in the
merging PR, and the decision not to re-measure was taken there. It is not machine flakiness (the
numbers reproduce exactly across three worktrees) and it is not the branch.

**Smallest honest fix, or the reason to leave it.** Leave it in this workstream: it is an owner
decision already recorded on PR #19, and the local rule against loosening a failing test applies.
If the owner wants the local run green, the test's own comment invites the change: re-measure at
file scope only, `OutlierChannelDelta` 48 -> 96 and a file-scoped `MaxMismatchedFraction` of
0.75 % (measured 88 and 0.595 %), and rewrite the comment's paragraph of numbers for Skia 3
(46 -> 88, 0.211 % -> 0.595 %). Whatever is decided, the comment is stale in-tree today: it
describes a measurement the current pin cannot reproduce, and it cites ANGLE "2.1.27952", which
matches neither the pre-#19 pin (2.1.25547) nor the current one (2.1.27548). Rewriting that
paragraph is the one edit that is correct under either decision.

## Reproduction

Each run used a detached worktree under the session scratchpad (`triage/wt-<name>`), a Release
build of the one test project, and `dotnet run --no-build ... -- --treenode-filter <filter>`:

```
DEMO_PATH="<replays>/match730_003731893271710924851_1024675027_129.dem" \
  dotnet run --no-build --project src/App/DemoViewer.NET.App.Tests -c Release -- \
  --treenode-filter "/*/*/Playback2DBombTimerTests/*"
DEMO_PATH=... --treenode-filter "/*/*/SprayControlOracleRealDemoTests/*"
dotnet run --no-build --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release -- \
  --treenode-filter "/*/*/BackendParityTests/*"
```

Logs: `scratchpad/triage/wt-{branch-f9b2b23,main-d90ec9f}.{bomb,spray,parity}.log`,
`wt-pre19-10702d9.parity.log`, `dust2-10896.{bomb,spray}.log`, and the probe outputs
`probe/nuke-probe*.log`, `probe/builds-probe.log`. The parity images (cpu, gpu, diff) are under
each worktree's `artifacts/bin/DemoViewer.NET.Playback2D.Tests/release/artifacts/backend-parity/`.

Housekeeping: the three scratchpad worktrees (`wt-branch-f9b2b23`, `wt-main-d90ec9f`,
`wt-pre19-10702d9`) are registered in the repository's `.git/worktrees`. They were left in place
because removing them is a state change this triage was not to make; `git worktree remove <path>`
for each, then `git worktree prune`, clears them.
