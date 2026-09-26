# Inputs Per Demo Source: research table

Plan item: `plan.md` §3 Phase 0, "Inputs Per Demo Source" (research, no review, depends on nothing).
Status: **in progress**. Valve matchmaking is measured on two eras; FACEIT, HLTV broadcast and POV
are outstanding because no such demo exists locally (F6c). This file collects what the designs
already measured so the outstanding half is the only work left, and so the Grenade Walk and Delta
User Commands items have one place to cite.

Nothing here was taken from `assets/tour/sample-de_nuke.dem`: the trimmer strips `svc_UserCmds` by
design, so the tour sample can never be evidence for this question.

## 1. The question

For every demo source the library will see, how much of the input stream can the engine read? Per
source: is `svc_UserCmds` present at all, what share of `CMsgServerUserCmd` messages carry a full
`data` payload versus `delta_data`, how many distinct player slots send commands, which
`buttonstate1` bits appear, and what the sub-tick yield is. Consumers: `SubTickExtractor`,
`AimCapabilityProbe`, the Grenade Walk's jump-throw flag (`designs/grenade-walk.md` §3.5 and §3.7),
and every later input feature.

## 2. Measured: Valve matchmaking (`GotvMatchmaking`), builds 10231 to 10896

Source: `plan.md` §2 F6 (probe `UserCmdProbe`, CS2DemoKit 0.12.0, first 30,000 frames per demo) and
`designs/grenade-walk.md` §2.4 (probe `GrenadeWalkProbe`, whole demo). Demos are untrimmed
recordings from the Steam `replays` folder.

| Demo (era, map) | Build | Commands (30k frames) | Full `data` | `delta_data` | Delta share | Decoded buttons and sub-tick |
|---|---|---|---|---|---|---|
| Jan 2025, de_nuke | 10231 | 300,030 | 300,030 | 0 | 0.0% | attack 7,377 · jump 1,534 · duck 7,816 · mouse 89,293 · sub-tick jump presses 138 |
| Aug 2025, de_ancient | 10499 | 300,020 | 300,020 | 0 | 0.0% | attack 7,804 · jump 1,401 · duck 4,859 · sub-tick moves 291,117 |
| Sep 2026, de_dust2 | 10896 | 300,000 | 505 | 299,495 | 99.8% | attack 2 · jump 23 (keyframes only) |
| Sep 2026, de_inferno | 10896 | 300,000 | 813 | 299,187 | 99.7% | attack 33 · jump 0 (keyframes only) |

Whole-demo figures from the Grenade Walk probe agree: the Jan 2025 nuke demo carried `data` on
1,324,574 of 1,324,574 commands; the two Sep 2026 demos (dust2, ancient) carried `delta_data` on
99.86% and 99.81% of commands. Scanning the full-payload commands costs 0.6 to 1.2 s per demo.

What follows for this source:

- Every one of the ten players' `CSGOUserCmdPB` is on the wire: `buttonstate1` bits for attack,
  jump, duck and movement, mouse deltas, `subtick_moves` with a `when` fraction, `input_history`.
- Since roughly July 2026 the server writes `delta_data` instead of `data`. CS2DemoKit 0.12.0 does
  not decode it (`CS2DemoKit.Parser` has no reference to `DeltaData`; the packaged
  `CS2OpenDev.Protos` 0.9.0 carries the field), so on any current demo the engine sees only the
  ~0.2% full keyframes. Filed upstream as CS2DemoKit #53 (Delta User Commands, "awaiting upstream"
  on the status board). demoinfocs-golang reconstructs a command from either payload, so a reference
  decoder exists.
- Jump-throw from inputs worked on the 2025 demo (20 of 229 grenades; the thrower's
  `FL_ONGROUND` at the spawn frame agreed 20/20 and 181/182) and found no window at all on the 2026
  demos, exactly as the delta share predicts. Release tick and release angles do not need inputs on
  this source: `weapon_fire` plus the pawn's eye angles reproduce them (grenade-walk §2.4).

The gap between builds 10499 (Aug 2025, all full) and 10896 (Sep 2026, all delta) has not been
narrowed with a demo from between those dates; the "roughly July 2026" date comes from demoparser
issue #340, which reports the same change on FACEIT.

## 3. Outstanding sources

| Source | Engine profile | What is unknown | Exactly which demo closes it | Owning item |
|---|---|---|---|---|
| FACEIT (128 tick) | classified by `DemoSourceClassifier` (GOTV / HLTV / POV / Unknown); which kind a FACEIT recording lands on is itself unmeasured | Whether `svc_UserCmds` is present; delta share (demoparser #340 says FACEIT switched to `delta_data` on the same July 2026 date); whether `weapon_fire` fires; `EntityId` semantics; how `cadenceTicks` behaves at 128 (round-index R1, its §6 unknowns) | One FACEIT match demo downloaded from the FACEIT match room, current era (post July 2026), any active-duty map, plus one from before July 2026 if one can be found, to bracket the change. None is named in `demos/CORPUS.md`; a FACEIT account with a played match is the only route. | Inputs Per Demo Source |
| HLTV broadcast | `HLTV` profile (`GrenadeThrown` is bound on this profile and not on GOTV, plan F6) | Whether HLTV recordings carry `svc_UserCmds` at all; if so, delta share; `weapon_fire` presence; whether `grenade_thrown` really fires; `EntityId` semantics for detonations; coach `m_iTeamNum` (team-identity TI-V4) | Re-download one of the three names in `demos/CORPUS.md` `pro/`: `furia-vs-vitality-m1-mirage.dem` (the reference demo for `docs/rule-graph/design.md`, preferred), or `-m3-nuke.dem`, or `-m4-overpass.dem`. Team Identity's 8 HLTV cache records were parsed from `\\BLACK-BOX\Demos\Pro Demos`; check whether the `.dem` files still exist there before re-downloading. Place it under `demos/pro/` and update `CORPUS.md`. | Inputs Per Demo Source (plan §3 names the re-download explicitly) |
| POV (client recording) | `POV` profile | Whether the recording player's commands are present at all (a POV demo carries the local client's `CSGOUserCmdPB`; other players' are not expected); full-vs-delta share; distinct slots (expected 1) | One `record`-ed POV demo from the owner's own client on any map, current build. No corpus entry exists; the owner records one in a match or a bot game. A bot game is acceptable for the presence question, not for yield numbers. | Inputs Per Demo Source |

Per source the probe records, in this order: `DemoSourceKind`, command count, full `data` count,
`delta_data` count, distinct slots, button bits seen, sub-tick move count, and (at no extra cost,
team-identity §6) the `player_team` event count per slot so Place Names From The Pawn and Team
Identity get their per-source roster column from the same run.

## 4. The fallback each design already carries, per source

| Design | Where | Fallback when inputs are absent or delta-only |
|---|---|---|
| Grenade Walk | §3.5, §3.7, §6 | `IThrowerInputSource.HasCoverage` false for the window: `JumpThrow = !ThrowerOnGroundAtSpawn`, `JumpThrowSource = GroundFlag` (spawn frame, not release tick; over-reports 1 in 182). Release tick from `weapon_fire`, then `grenade_thrown` on HLTV, then `spawn - 7`; `ReleaseSource` is printed on the card. The sidecar header's `inputDecoder` field lets a v1 index be re-run when #53 lands. The integration test is parameterised by source and skips with a reason when a source's demo is absent. |
| Delta User Commands (CS2DemoKit #53) | plan §3 | Until the engine decodes `delta_data`, every input consumer sees ~0.2% of commands on current demos; `AimCapabilityProbe` is asked to report the delta share so the loss is visible rather than silent. |
| The Round Index | §6 R1 | Uses no inputs; cadence is in seconds so 128 tick only changes `cadenceTicks`. |
| Suggested Tags | §6 | Uses no inputs; `player_team` cadence on HLTV/POV is the exposure, handled by the side resolver's `OldTeam` path. |
| Round Facts | §6 A5 | Uses no inputs; source-profile event differences (`round_end` present on HLTV) are owned by the engine's profiles; verification per source is a test row of this item. |
| Team Identity | §6, §11 TI-V4 | Uses no inputs; `player_team` coverage per source is unknown for FACEIT and POV (6 of 277 matchmaking demos, 2.2%, had no `player_team` at all and stay unassigned). Coach exclusion is verified on the HLTV demo this item re-acquires. |

Until the outstanding rows are measured, every design states its claims for Valve matchmaking only
and treats pro demos as input-less (plan F6c).

## 5. Done criterion and re-acquisition

Done when §3's three rows carry measured numbers in the same columns as §2, from one demo per
source, and the Grenade Walk (§3.7) and Delta User Commands designs cite this file. Re-acquisition
of the HLTV demo is this item's own work (plan §3: "re-download one of the three `demos/CORPUS.md`
names"); FACEIT and POV need the owner to supply a demo, which is the one part of this item no agent
can do alone. The corpus manifest `demos/CORPUS.md` is updated with every file added; `demos/` stays
gitignored and is reached through `DEMO_PATH`, never a link.

## 6. Sources

- `docs/strat-room/plan.md` §2 F6 (corrected 2026-09-23), §3 Inputs Per Demo Source and Delta User Commands, §5, §7 (2026-09-23 entries).
- `docs/strat-room/designs/grenade-walk.md` §2.4 (jump-throw and input scan cost), §3.5, §3.7, §6.
- `docs/strat-room/designs/round-index.md` §6 R1 and unknowns; `designs/suggested-tags.md` §6; `designs/round-facts.md` §6 A5; `designs/team-identity.md` §2.1, §2.5, §6, §10, §11.3 TI-V4.
- `docs/strat-room/designs/00-overview.md` §5 item 4, §6.1, §6.3.
- `demos/CORPUS.md` (the three HLTV names; the `DEMO_PATH` rule).
- demoparser issue #340 (FACEIT `delta_data` date), cited from plan F6.
