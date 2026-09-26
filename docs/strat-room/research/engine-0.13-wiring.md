# CS2DemoKit 0.13.0-beta0001: wiring map for Phases 0 to 3

Untracked planning note (do not stage). Sources: the 0.13 release notes, `migrating-to-0.13.md`,
the `releasing.md` 0.13.0 compatibility notes and `RULES_AUTHORING.md` (copies in the session scratchpad
under `cs2demokit-0.13/`), the 0.13.0-beta0001 nuspecs and XML docs from nuget.org, and the engine
repo at tag `v0.13.0-beta0001` (the rules files and the JSON schema). No demo was opened.

Order: **engine-upgrade-0-13** first (compile and re-mirror), then **round-facts-from-the-engine**,
then **alive-and-team-from-samples**, **grenade-projectiles-wiring** and **player-input-refresh**
(independent of each other), and **real-demo-unskip-sweep** last, because most skips need both real
rows and the sample-side alive/team.

## 0. Package pins (Directory.Packages.props)

| Package | Now | 0.13.0-beta0001 needs | Change |
|---|---|---|---|
| CS2DemoKit.Parser | 0.12.0 | 0.13.0-beta0001 | bump |
| CS2DemoKit.Analysis | 0.12.0 | 0.13.0-beta0001 (pins Parser and Rules exactly: `[0.13.0-beta0001]`) | bump |
| CS2DemoKit.Analysis.Rules | 0.12.0 | 0.13.0-beta0001 (zero deps) | bump |
| CS2OpenDev.Protos | 0.9.0 | 0.9.0 | none |
| CS2OpenDev.Sdk | 0.9.0 | 0.9.0 | none |
| CS2OpenDev.Sdk.GameEvents | 0.9.0 | 0.9.0 | none |
| CS2OpenDev.Sdk.Entities.Abstractions | 1.0.3 | 1.0.3 | none |
| CS2OpenDev.Sdk.Entities | 1.1.3 | 1.1.3 | none |
| Google.Protobuf | 3.29.5 | 3.29.5 | none |
| Snappier | 1.3.1 | 1.3.1 | none |
| YamlDotNet | 16.3.0 | 16.3.0 | none |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | 10.0.0 | none |

All three CS2DemoKit lines move in one commit (the exact intra-family pin fails restore otherwise).
Add a 0.13 paragraph to the comment block above the pins and keep the 0.12 paragraph. The prerelease
is on nuget.org, so no feed change is needed.

## 1. engine-upgrade-0-13

Goal: the build and tests pass on 0.13, with no new feature wired yet. This repo has only a few compile breaks.

| File / symbol | Change | 0.13 shape |
|---|---|---|
| `Directory.Packages.props` | the three pins, comment | see §0 |
| `tools/AnalysisBench/Program.cs:501, 629, 707` (`run.Build.Chains.Count`, `build.Chains.Count`) | remove the chains figure (always 0) or print `RuleGraph.FromBuild(build).Edges.Count` | `BuildResult(Graph, Nodes, Edges, RelevantMessageTypes, PlayerContextIndex, EntityScanner, EdgeBacking, GameNodesByRuleId, Outputs, RulesetCoverage)` |
| `src/App/DemoViewer.NET/Modules/Situations/SituationSnapshot.cs:102` `new PositionSample(...)` | pass the two new members from the marker | `new(frame, tick, slot, pos, place, marker.Team, marker.IsAlive)` |
| `src/App/DemoViewer.NET.App.Tests/RoundIndexTestData.cs:104-106` `Sample(...)` | add `int team`, `bool alive = true`; the only direct test construction, every other sample helper (`Everyone`, `FindRoundsLikeThisTests.Samples`, `ResultCardTests.Placed`, `RoundPositionsTests.Placed`, `SuggestedTagsOccupancyTests`, `WatchedSituationsTests`, `SuggestedTagsReviewData.Walk`) routes through it | `(int frame, int tick, int slot, Vector3 pos, string? place, int team, bool alive)` |
| `rules/player_stats.rules.yaml` | re-mirror from the package: `rounds_survived` and `clutch_won` count `round_ended`, `CTLosses` / `TLosses` count `round_lost`. Left as is, the app's copy reads 0 losses and counts only won rounds as survived | `YamlConfigLoader.ExtractShippedTo(dir)`, copy with LF |
| `rules/kast.rules.yaml`, `weapon_stats`, `post_plant_double` | match the tag except for line endings; re-extract anyway so all four stay byte-identical | same |
| `rules/cs2demokit-rules.schema.json` | re-extract. 0.13 adds `each_team`, `team_round` and `team_match`, and the repo copy has none of them. `ShippedSchemaDriftTests` fails until then | same |
| Comments naming 0.12.0 as current: `ShippedScaleGraph.cs:23`, `EntityMicroBench/Program.cs:189`, `designs/grenade-walk.md` sidecar example `"engine": "CS2DemoKit.Parser 0.12.0"` | wording | |

These have no call sites, so nothing breaks: `NodeGroupHint`, `BuildResult.GroupHints` / `NodeChains`
(only comments in `AnalysisViewModel.cs:1761, 3214, 3290` and `RuleGraphSkeleton.cs:85`, which
can now say "removed in 0.13"), `RoundEndEnrichmentEdge`, `MetricRef`, `EntityProviderReference`,
`EntityChangeScanner.AdvanceAndPollAt` / `PostFrameMessages`, and any `switch` on `RulesetScope` /
`OutputScope` / `ScopeAxis`. `CheckedStat` is only enumerated (`tools/AnalysisBench/RulesCheckCommand.cs:177`)
and never deconstructed. No type in the repo collides with the new public names (`RuleGraph*`,
`ProjectileSample*`, `GrenadeProjectileClasses`, `UserCmdReconstructor` etc.).
`AnalysisTabView.axaml:599` `Filter.Chains` is app-side, not `BuildResult`.

Tests: `ShippedSchemaDriftTests` and the full App suite must pass. Add `ShippedRulesetPinTests`,
which round-facts design §6 names: each of the four mirrored rulesets must equal the `ExtractShippedTo`
copy byte for byte, as the schema test already checks for the schema.

Risks (numbers that move):
- `tests/fixtures/<demo-id>/ours.golden.json` (5 dirs): `CTLosses`, `TLosses`, `rounds_survived` and
  `clutch_won` move with the re-mirrored `player_stats`. Wins and losses also move by one on a surrender
  round (in the engine's own fixtures: a round-13 CT surrender, five players). Regenerate with
  `AnalysisBench --suite` and diff. `expected.golden.json` is a tripwire, so any expected value that
  moves needs a stated reason in the commit.
- `highlights_ammo.rules.yaml` already assumes `m_iClip1 == -1` for knives and `== 1` for the last
  bullet. The 0.12 zigzag decode made those reads wrong, so last-bullet highlights now change (they
  become correct), and any highlight golden or cached highlight list differs. Check whether the
  highlight cache key includes the engine version, or whether old caches survive the bump.
- Every build gains 4 nodes (the round-decided providers) and matching snapshot columns. Any test
  that pins `Nodes.Count` or a snapshot column count may move, for example
  `Visualization.Tests/GraphProjectionTests.cs` and the Workbench graph tests. Per-pawn digest columns
  after `entity.controller.money` shift by one, so index them by provider name.
- Smoke baseline decode (#56): on build-10896 demos, `VisibilityAnalyzer` numbers and the Playback2D
  area-effect goldens move, because flying smokes no longer count as clouds.
  `SceneFrameBuilder.UpdateAreaEffects` (the `m_nSmokeEffectTickBegin > 0` gate, line 694) needs no
  change. `SceneGoldenTests` / `Playback2DGoldenCaptureTests` may need a recapture; inspect the diff
  before accepting it.
- Evaluate allocates 3.5 to 5% more, and wall time stays within noise (the engine's measurement). If
  you use AnalysisBench, quote plain mode and interleave builds, since runs drift 27 to 31% between sessions.
- A rules file with `---` separators now loads every document in it. Check the user-overlay test
  fixtures for multi-document files.

Model: Sonnet for the mechanical part; golden triage wants Opus or a human.

## 2. round-facts-from-the-engine (#54)

Goal: `EngineRoundFactsRowSource` evaluates the `round_facts` ruleset and returns real rows. Nothing
above the seam changes (`IRoundFactsRowSource` -> `RoundFactsProjection` -> `RoundFactsEvaluator` ->
`RoundFactsSource`).

| File / symbol | Change |
|---|---|
| `docs/strat-room/parked/round_facts.rules.yaml` -> `rules/round_facts.rules.yaml` | Port to the 0.13 vocabulary (table below) and move it into `rules/`. The engine's `examples/round_facts.rules.yaml` does not drop in. Its stat names and labels differ, and it has fewer columns: no slots list, players, score, round_time, game_phase, planter or first contact. It is also an example, so `ExtractShippedTo` does not ship it. The "byte-identical to the package copy" contract in design §3.5 therefore cannot hold. Record that as a design correction, and keep the ruleset as app content like `highlights_*`, with column labels equal to `RoundFactsColumns` |
| `Services/RoundFacts/EngineRoundFactsRowSource.cs` | Replace the stub. Load the effective `round_facts` doc through the same overlay as `RulesRoundFactsRulesetIdentity`, then call `DemoAnalysis.Build(parsed, [doc], options)`, `DemoAnalysis.Evaluate(parsed, build, ...)` with snapshots off, and `run.ProjectConfiguredOutputs(parsed)` (0.13 projects without snapshots). Take the `round_facts` table and turn each `MetricRow` into a column dictionary, including the `side` and `slots` dimensions. `slots` is a comma-joined string, so `RoundFactsValues.ToIntList` must accept that. Fill `Parameters` from the doc's `params:`. Set `UnavailableColumns` to the known columns the ported ruleset does not emit. Keep the parameterless constructor (13 test files use it), and delete `WaitingDiagnostic` |
| `Services/RoundFacts/RoundFactsEvaluator.cs:26, 128` | drop the #54 wording. Keep the once-only "ruleset absent" log for a user who disables the ruleset |
| `Services/RoundFacts/RoundFactsFingerprint.cs:55` | comment only. The identity already hashes the effective doc through `HighlightConfigFingerprint.Compute`. Bump `DemoCacheRecord.RoundFactsSchema` if the row shape changes |
| `Services/RoundFacts/IRoundFactsRowSource.cs:27`, `RoundFactsModels.cs:263` | comment only ("a column the ruleset does not emit") |
| `App.axaml.cs:785` | comment only; registrations unchanged |
| `ViewModels/StratBook/CreateStratDialogViewModel.cs:25` | comment only |
| `RulesHighlightHarvester` (`Build(demo, rules.Rulesets)` at 110, 125), `AnalysisViewModel.cs:1270`, `RuleWorkbenchTabViewModel.cs:377` | Once `round_facts` is in `rules/`, every whole-directory load runs it too. That costs time on the highlight scan and Stats evaluation, and a `team_round` table shows up in the Stats extra tables. Either exclude it by id in those hosts, or ship it `enabled: false` and have the identity and row source read it regardless. The second option keeps the other hosts clean. Decide in this item |

Parked surface -> 0.13 surface:

| Parked (0.12 draft) | 0.13 |
|---|---|
| `entity.game.round_win_reason` on `$round_end` | `event.Reason` on `round_decided`. Status and reason reset at `round_officially_ended`, 448 ticks later |
| `entity.game.round_win_status` on `$round_end` | `event.Winner` on `round_decided` |
| `end_tick` = `event.frame_tick` on `$round_end` | `event.frame_tick` on `round_decided` (the decision); `officially_ended_tick` on `round_ended` |
| `entity.game.total_rounds_played` / `game_phase` / `round_time` | `match.total_rounds_played` / `match.game_phase` / `match.round_time` (singleton reads build now) |
| `enrich.round.plant_site` | `round.bomb.site` (`"A"`/`"B"`, null with no plant), or `round.bomb.site_entity` (`-1` with no plant). Capture on `round_ended` as the engine example does, or on `bomb_planted` |
| `round.team.slots` stat | drop it: every `team_round` row has `slots` as a dimension |
| `round.team.money` at freeze end | same name, now real; use `on: raw.round_freeze_end` as the guide does |
| `event.frame_tick` | same, now real |

The 0.13 authoring guide does not document the surfaces below. Before relying on them, check each
with `AnalysisBench rules-check` against the 0.13 package, and drop to the app-side fallback for any
that fail:
- `round.team.score`
- `round.team.money_min` / `money_max` (feeds `money_reliable`)
- `round.team.previous_winner_side` (feeds `lost_previous`; the projection already derives this from the previous row)
- `victim.team`, `victim.slot` and `attacker.slot` on `player_death`
- `event.UserId` / `event.Site` on `bomb_planted`
- `define:` views with `match: { victim_enemy: true }`
- `{ param: ... }` table columns

`buy_type` has an app-side twin (`BuyTypeClassifier`), so dropping its `compute:` loses nothing.

Tests:
- Rewrite `RoundFactsEvaluatorTests.TheParkedEngineSource_ReportsEverythingUnavailable_AndWritesNoRows`
  (line 114) as a synthetic test through `FakeIdentity`, and move the real-demo assertion to `RoundFactsRealDemoTests`.
- New: the shipped `round_facts` loads and validates with zero errors (no
  `resolve.team-scope.unsupported` and no `resolve.show.table-scope-mismatch`), and every label it
  emits is a `RoundFactsColumns` name.
- New: `EngineRoundFactsRowSource` maps a hand-built `MetricTable` (side, slots, lists) to the column
  dictionary, including a null plant site and a `0` tick for something that did not happen.
- Un-skip `RoundFactsRealDemoTests` (6 skips) here; item 3 covers the rest.

Risks:
- A round decided in freeze time (8 of 282 MM demos) now gets a synthesized `round_freeze_end` and
  its own row. On those demos `rows.Rounds.Count` can exceed `ClipRounds.Derive(parsed).Count`, and
  `FreezeEndTicks_AgreeWithTheRoundAuthority_ForEveryRound` fails. Decide which authority wins and record the decision.
- The winner on a surrender is now the server's, so `WinnerSide` differs from the kill-derived guess
  on those rounds. Strats and tags filtered on "won" move there.
- Cost: every tier-2 parse gains one Build + Evaluate. Measure on a current full demo. The engine
  quotes about 5 ms more for the build; the evaluation is where the time goes.
- Every cached record has a null `RoundFactsFingerprint`, so on first run the backlog re-evaluates
  the whole library.
- The suggested-tags goldens (`tests/fixtures/suggested-tags/*.json`) were captured from rows made by
  `SuggestedTagsRealDemoRows.Synthesize` (`SuggestedTagsGolden.cs:45, 214`). They move when switched
  to real rows. Recapture with `ST_GOLDEN_UPDATE=1` and review the diff.

Model: Opus (the ruleset port and the round-authority decision).

## 3. real-demo-unskip-sweep

Every skip that cites #54, all in `src/App/DemoViewer.NET.App.Tests/`. Most are reason constants;
one is a `SkipTestException`:

| File | Skips | Needs |
|---|---|---|
| `RoundFactsRealDemoTests.cs:27` | 6 | item 2 |
| `RoundIndexRealDemoTests.cs:30` | 4 | item 2, 4 |
| `SuggestedTagsRealDemoTests.cs:30` | 2 | item 2, 4 |
| `CreateStratFromRoundRealDemoTests.cs:26` (and the comment at 129) | 1 | item 2 |
| `FindRoundsLikeThisRealDemoTests.cs:30` | 1 | item 2, 4 |
| `OverlayViewTests.cs:29` | 1 | item 2 |
| `QueryCanvasRealDemoTests.cs:26` | 1 | item 2, 4 |
| `RoundPositionsRealDemoTests.cs:32` | 1 | item 2 |
| `SearchFiltersRealDemoTests.cs:27` | 1 | item 2 |
| `StratEvidenceRealDemoTests.cs:27` | 1 | item 2 |
| `SuggestedTagsReviewRealDemoTests.cs:26` | 1 | item 2 |
| `TagFactsRealDemoTests.cs:25` | 1 | item 2 |
| `TagMatrixRealDemoTests.cs:27` | 1 | item 2 |
| `TeamIdentityRealDemoTests.cs:25` (`SideAtRound_OnTheRealRows`, `throw new SkipTestException` at 134) | 1 | item 2 |
| `ToleranceSliderRealDemoTests.cs:28` | 1 | item 2, 4 |
| `WatchedSituationsRealDemoTests.cs:27` | 1 | item 2, 4 |

These comments mention #54 but skip nothing; rewrite them: `DemoProvenanceRealDemoTests.cs:18`,
`SearchFiltersTests.cs:23`, `StratEvidenceTests.cs:19`, `SuggestedTagsReviewData.cs:18`,
`TagFactsRefresherTests.cs:18`, `TagMatrixTests.cs:20`, `SuggestedTagsGolden.cs:45, 214`,
`SuggestedTagsRealDemoTests.cs:20`.

Procedure: remove the attribute and its reason constant. Run each class with `DEMO_PATH` set to the
real replays folder: never the tour sample, and never a junction to the corpus. Fix the assertion or
record the measured value. If the real rows disprove a test's premise, rewrite the test instead of
skipping it again. Skips that fire when a named replay is missing from the folder stay.

Risks: this is the first time the Phase 1 to 3 features run on real rows, so expect real bugs as
well as value drift. The App suite has at least 3 tests that fail only when run alongside others, and
which ones depends on partitioning. Run the un-skipped classes on their own before blaming the change.

Model: Sonnet per class. Escalate any failure that is not value drift.

## 4. alive-and-team-from-samples (#58)

Goal: the walk-based builders read life state and side from the sample instead of the Round Facts
kill list and slot lists. Round Facts still supplies the round windows.

| File / symbol | Now | Change |
|---|---|---|
| `Services/RoundIndex/RoundIndexBuilder.cs`: `Windows` (side from `round.Ct.Slots` / `round.T.Slots`, death from `round.Kills`) and `CloseRow` (`SideBySlot`, `DeathTickBySlot`, around lines 186-260) | joins Round Facts | skip a sample when `!sample.IsAlive`; take side from `sample.Team` and skip anything but 2 or 3. Keep the facts' slot sets as a cross-check that logs disagreement, not as the gate |
| `Services/RoundIndex/RoundIndexBuilder.cs:274` `if (place is null) continue;` | `""` counts as a place | treat `""` as unplaced (`string.IsNullOrEmpty`) |
| `Services/RoundIndex/IPlaceSource.cs:42` `PawnPlaceSource.PlaceFor => sample.Place` | can return `""` | return null for `""`, so every consumer sees a single "unplaced" value |
| `Modules/SuggestedTags/RoundOccupancyBuilder.cs:140-142` (`window.Sides`, `window.Deaths`), and the comment at 155 ("the wire delivers \"\" where the engine doc promises null") | joins Round Facts | same change as the index; reword the comment, since `""` is now the documented contract |
| `RoundIndexBuilder.cs:79`, `RoundOccupancyBuilder.cs:119` `lastFrameTick = demo.Frames[^1].ServerTick` | compares the server clock with `sample.Tick`, which is the frame clock | use the frame clock (the last frame's `GameTick`) for the fallback window end |
| `Services/Strats/RoundCapture.cs:386` private `IsAlive(IReadOnlyEntity)` | its own copy of the rule | use `PawnLookup.IsAlive(EntityState)` where an `EntityState` is at hand; otherwise leave it with a comment pointing at the engine rule |
| `Modules/Playback2D/Playback2DTabViewModel.cs:3219`, `Playback2D.Pipeline/SceneFrameBuilder.cs:937` | same rule, on facades | leave (Playback2D holds no `EntityState`); add a comment reference only |

0.13 shape: `foreach (var s in PositionSampler.Walk(demo, stride)) if (s.IsAlive && s.Team is 2 or 3) ...`

Tests: extend `RoundIndexBuilderTests`, `SuggestedTagsOccupancyTests` and `RoundPositionsTests` with
a dead sample (`alive: false`) that must not appear, and a `Team` that disagrees with the facts' slot
list. Add a sample with a `""` place that both builders must count as unplaced.

Risks:
- The `tests/fixtures/round-index/*` and `tests/fixtures/suggested-tags/*.json` goldens move in two
  cases: where the kill tick and the pawn's life state disagree by a frame, and where the index used
  to count a `""` place as a real place.
- `.dvri.json` sidecars carry a schema version; bump it so cached sidecars rebuild.
- Deaths now come from the pawn's state at the sampled frame (stride 4), not from the kill tick.

Model: Sonnet.

## 5. grenade-projectiles-wiring (#56, #59)

Grenade Walk and Grenade Index are Phase 4 and not built yet. For Phases 0 to 3, the wiring covers
the places that currently resolve a thrower or a smoke by hand.

| File / symbol | Now | Change |
|---|---|---|
| `Services/Strats/RoundCapture.cs:288, 318-322`: inferno thrower from `m_hOwnerEntity` on the `CInferno`, decoy from a pawn handle | resolved at the stop; fails when the owner handle is stale | Run one `ProjectileSampler.Walk` per capture window. Match each `inferno_startburn` / `decoy_started` to the `Removed` sample of the nearest `CMolotovProjectile` / `CDecoyProjectile`, within a few frames and a small radius. That sample's `ThrowerSlot` is held from creation, so it survives the thrower's death. Keep `m_hOwnerEntity` as the fallback |
| `Services/Strats/RoundCapture.cs:336-337`: a dead thrower's team from the controller | works | unchanged |
| `Modules/SuggestedTags/DetonationPlacement.cs:37-55`: inferno and decoy get `ThrowerSlot = -1` (21 to 27% of detonations) | those have no side | the same projectile match fills `ThrowerSlot`, so `ProposalDetection.cs:89` can put them on a side |
| `Playback2D.Pipeline/SceneFrameBuilder.cs:60`: its own list of the five projectile classes | duplicate | could read `GrenadeProjectileClasses` instead; optional |
| `SceneFrameBuilder.UpdateAreaEffects` (694) | smoke gate relies on `m_nSmokeEffectTickBegin` | no change; the gate is now correct on 10896 demos (#56) |
| `designs/grenade-walk.md` §3, §5.1 and the sidecar example | an app-side `GrenadeWalker`, plus the proposal that became #59 | note that `ProjectileSampler` shipped, so the Phase 4 walker becomes a consumer of it (D7 closed) |

0.13 shape: `foreach (ProjectileSample s in ProjectileSampler.Walk(demo, frameStride: 8)) if (s.Removed) ...`
(Created and Removed samples always land on their own frame, whatever the stride.)

Tests: synthetic matching tests. A projectile's Removed sample near an inferno start names the slot,
and no projectile nearby leaves -1. On a real demo, count inferno and decoy detonations with a
resolved thrower and record the figure against the 21 to 27% unresolved baseline.

Risks:
- Suggested-tags goldens and proposal counts move, because more fires get a side.
- A strat captured before and after the change can name a different actor for a fire.
- A whole-demo projectile walk costs a full replay pass. Reuse the capture's existing tracker pass,
  or limit the walk to the round window.

Model: Sonnet for the matching; Opus to set the match tolerances.

## 6. player-input-refresh (#53)

| File / symbol | Now | Change |
|---|---|---|
| `ViewModels/Replay/ReplayTabViewModel.cs:454` `SubTickExtractor.Extract(value.Frames)` over one tick group | on a current demo, a tick group's frames have no delta baseline, so the rebuilt events stay near 0 (MissingBaseline) | Keep a `UserCmdReconstructor` per loaded demo. For a tick group, call `Reset()`, start at the nearest preceding `DEM_FullPacket`, and feed frames up to the group's end through `Extract(frames, reconstructor)` or `AdvanceOneFrame`, keeping only the group's events. Cache per demo if the seek cost shows |
| `ViewModels/Stats/AimCapabilityProbe.cs:288-330` `MeasureSubtick`, which samples 512 strided carrier frames | the strided sample has no baseline on delta demos, so the yield under-reads and `SubtickAimTiming` reads Degraded even on a fully instrumented demo | Walk contiguous runs from a `DEM_FullPacket`, or the whole demo (2 to 2.5 s). Report `UserCmdReconstructionStats` (`Full`, `Delta`, `MissingBaseline`, `DecodeFailed`) in `SubtickObservation`, and add the delta share to `AimCapabilityReport` |
| `ViewModels/Stats/AimCapabilityReport.cs:145-160` docs (0.026 vs 0.88 yield) | 0.12 numbers | re-measure and rewrite |
| `Models/PayloadNodeBuilder.cs:38`: embeds `CMsgServerUserCmd.data` as `CSGOUserCmdPB` | `delta_data` shows as raw bytes | optional: show the rebuilt `CSGOUserCmdPB` for the selected message (needs the per-demo reconstructor); otherwise leave the raw bytes |
| `docs/strat-room/research/inputs-per-demo-source.md` §2 | build-10896 rows are keyframes only | add the 0.13 rebuilt figures (the engine reports 15 -> 60,503 sub-tick events on a 10896 MM demo) |

0.13 shape: `var input = new UserCmdReconstructor(); foreach (var f in frames) foreach (var cmd in input.AdvanceOneFrame(f)) Use(cmd.Command);`
(The decode plan must include `MessageCategories.UserCmds`. Commands inside a `DEM_FullPacket` prime the reconstructor and are not returned.)

Tests:
- Keep `AimCapabilityProbeRealDemoTests.cs:106` as is. The trimmer strips `svc_UserCmds`, so the
  tour sample still reads 0 and Unsupported.
- Add a real-demo test on a 10896 demo: `MissingBaseline` near 0 and a yield above `MinSubtickYield`.
- Add a replay-tab test: a tick group in the middle of a delta demo yields events.

Risks:
- On a current demo, every input count jumps by orders of magnitude: the tick view, the aim probe
  verdicts, and the probe's `MinSubtickYield = 0.10` calibration.
- `DEM_FullPacket` snapshots no longer double count on current demos. Pre-10896 numbers stay the same.
- Walking the whole demo in the probe costs 2 to 2.5 s per full demo, so keep it off the UI thread.

Model: Opus (the reconstructor's seek semantics).

## 7. Out of scope for Phases 0 to 3

`RuleGraph.FromRun(run).CollapsePlayers()` could replace `ViewModels/RuleGraphSkeleton.cs` and the
graph edge enumeration in `AnalysisViewModel.cs:858, 1479, 1740-1866`; game-scope descriptors went
from 43 rows to 134. It is not a Strat Room item, so leave it for a later pass. The clip fix (#49)
needs no app change beyond the golden moves listed in §1.
