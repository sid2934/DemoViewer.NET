---
name: usercmd-delta-data
description: "Valve matchmaking GOTV demos DO carry every player's inputs (CSGOUserCmdPB); since ~July 2026 they are 99.8% CMsgServerUserCmd.delta_data, which CS2DemoKit 0.12.0 cannot decode, so SubTickExtractor and AimCapabilityProbe silently see ~1 command in 600 on current demos"
metadata:
  node_type: memory
  type: project
  originSessionId: c601929d-ef26-465e-8cbd-da10642df0f3
  modified: 2026-09-23T20:13:49.933Z
---

Measured 2026-09-23 with a scratch probe (scratchpad `UserCmdProbe`, CS2DemoKit.Parser 0.12.0 +
CS2OpenDev.Protos 0.9.0) on untrimmed `GotvMatchmaking` demos from the Steam replays folder:

- Jan 2025 (build 10231) and Aug 2025 (10499): ~300k commands per 30k frames, all `data`, ten
  players, attack/jump/duck/move bits, mouse deltas, sub-tick jump presses with `when`.
- Sep 2026 (build 10896): 299,495 of 300,000 commands are `delta_data`; only ~505 full keyframes
  decode. `CS2DemoKit.Parser.dll` has zero references to `DeltaData`.

**Why:** an earlier draft claimed "GOTV demos carry no input buttons"; the owner challenged it and
was right. The trimmed tour sample (`assets/tour/sample-de_nuke.dem`) has zero `svc_UserCmds`
BECAUSE the trimmer strips them, so it is evidence of nothing (see [[tour-sample-demo-invalid]]).

**How to apply:**
- Never cite the tour sample for input presence. Probe a real demo from the Steam replays folder.
- Filed upstream as CS2DemoKit issue #53 (2026-09-23) at the owner's direction: the parser keeps
  the bytes (UserCmdsWriter arena) but `SubTickExtractor` reads `Data` only; the owner wants the
  per-slot reconstruction state in EntityTracking. Reference impl: demoinfocs-golang
  `s2_usercmd_delta.go` (wire type 7 = reset marker, 150-slot ring by cmd_number). Plan assumes it
  lands upstream; do not design a delta decoder in DemoViewer.
- HLTV broadcast demos: still UNVERIFIED whether they carry `svc_UserCmds`; no untrimmed pro demo
  survives locally. Design input features with a ground-flag fallback until one is probed.
- demoparser issue #340 (LaihoE) reports the same switch for FACEIT demos from ~2026-07-10.
