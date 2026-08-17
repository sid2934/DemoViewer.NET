# Demo trimmer — proof-of-concept results

**Status:** POC complete. Tool + tests landed on `spike/demo-trimmer-poc`; 24 candidate files generated.
**Date:** 2026-07-25.
**Motivating study:** the trimmed-tour-demo feasibility research (retired 2026-08-16; full text
in git history). §3.3 below restates its estimates and replaces them with measurements. This
document reports what the trimmer actually produced and what was verified.
**Tool:** `tools/DemoViewer.NET.DemoTrimmer/` · **Tests:** `tools/DemoViewer.NET.DemoTrimmer.Tests/`
**Candidates:** `demos/trimmed/` (gitignored).

> **What is not verified:** CS2 playability. No candidate has been loaded in the game. Everything below
> is parser-side verification plus a manual protocol (§6) for settling the real question in-game.

---

## 1. The short version

1. **The verbatim trim works and is fully verified.** V0 (contiguous) at 3 rounds is **36.56 MiB**
   (matchmaking) / **43.26 MiB** (pro) — matching the study's estimate almost exactly.
2. **Stripping `svc_UserCmds` works, and is completely transparent to entity decode.** The 3-round file
   drops to **10.71 MiB** (matchmaking) / **10.17 MiB** (pro) — a **71 %** / **76 %** reduction — and the
   replayed entity state is *bit-identical* to the untrimmed source at every sample tick, with zero
   unknown deltas. The bit-level re-encoder was proven bit-exact on **59 000+ real packets** before
   anything was dropped (§4.2).
3. **The study's `svc_UserCmds`-stripped estimate was ~19 % low.** Estimated ~9.0 MiB for the
   matchmaking 3-round trim; measured 10.68–10.71 MiB.
4. **Entering at a `DEM_FullPacket` checkpoint is a bad trade and should be dropped from the design.**
   On both reference demos it saves **0.03 MiB** — because their first checkpoint is at tick 1, so "the
   checkpoint before round 1" *is* the start of the demo — while making the file undecodable by any
   sequential reader, **including DemoViewer.NET's own demo-load path** (§5.1). The contiguous variants
   (`v0`, `v2c`, `v3c`) give the same size with none of the risk.
5. **Two container facts the parser hides** were discovered and had to be handled explicitly, or every
   emitted file would have been malformed (§5.2). One of them is a documentation defect in
   `DemoParser.cs`.

**Recommended candidate for the tour:** `*-v3c-no-usercmds-contiguous-3r.dem` — ~10.2–10.7 MiB, no
mid-stream entry, verified byte-faithful entity + game-event streams, readable by DemoViewer.NET as-is.
Its CS2 playability is the one open question, and it is the *least* likely of the ladder to survive.

---

## 2. The ladder

Six rungs, ordered by how much they remove. Each rung differs from a neighbour by exactly one thing, so
a CS2 failure isolates.

| Id | Entry | Whole frames dropped | Inner messages stripped |
|---|---|---|---|
| `v0-contiguous` | frame 0 | — | — |
| `v1-verbatim` | `DEM_FullPacket` before round 1 | — | — |
| `v2-no-anim` | `DEM_FullPacket` | `DEM_AnimationData`, `DEM_AnimationHeader` | — |
| `v3-no-usercmds` | `DEM_FullPacket` | `DEM_AnimationData`, `DEM_AnimationHeader` | `svc_UserCmds` (76) |
| `v2c-no-anim-contiguous` | frame 0 | `DEM_AnimationData`, `DEM_AnimationHeader` | — |
| `v3c-no-usercmds-contiguous` | frame 0 | `DEM_AnimationData`, `DEM_AnimationHeader` | `svc_UserCmds` (76) |

`v2c` / `v3c` were added after measurement: the checkpoint-entry axis turned out to cost readability and
buy nothing (§5.1), so the message-removal rungs needed contiguous counterparts to be usable at all.

Rounds are delimited by `round_freeze_end`, matching the study. `round_start` / `round_end` /
`round_officially_ended` are unusable here: the matchmaking demo emits **0** `round_start` events and the
pro demo emits **0** `round_officially_ended`. Neither demo fires `round_freeze_end` during warmup — in
both, the first `round_freeze_end` coincides with `round_announce_match_start` — so no warmup skipping is
needed (`--skip-boundaries 0`).

---

## 3. Measured sizes

### 3.1 Matchmaking reference demo

`003816248937665266002_0544286934.dem` · de_nuke · **172.25 MiB** · 90 603 frames · 90 568 ticks · 64 Hz.
1-round window = ticks 0–7 510 (117 s, 8 kills) · 3-round window = ticks 0–19 344 (302 s, 22 kills).

| Variant | 1 round | 3 rounds | 3r vs source |
|---|---:|---:|---:|
| `v0-contiguous` | 14.00 MiB | **36.56 MiB** | 21.2 % |
| `v1-verbatim` | 13.97 MiB | 36.54 MiB | 21.2 % |
| `v2-no-anim` | 13.97 MiB | 36.54 MiB | 21.2 % |
| `v3-no-usercmds` | 4.13 MiB | **10.68 MiB** | 6.2 % |
| `v2c-no-anim-contiguous` | 14.00 MiB | 36.56 MiB | 21.2 % |
| `v3c-no-usercmds-contiguous` | 4.16 MiB | **10.71 MiB** | 6.2 % |

This demo contains no `DEM_AnimationData`, so `v2*` is identical to its verbatim sibling.

### 3.2 Pro demo

`furia-vs-vitality-m3-nuke.dem` · de_nuke · **318.33 MiB** · 126 125 frames · 118 049 ticks.
1-round window = ticks 0–8 140 (7 kills) · 3-round window = ticks 0–15 360 (18 kills).

| Variant | 1 round | 3 rounds | 3r vs source |
|---|---:|---:|---:|
| `v0-contiguous` | 22.33 MiB | **43.26 MiB** | 13.6 % |
| `v1-verbatim` | 22.29 MiB | 43.23 MiB | 13.6 % |
| `v2-no-anim` | 19.68 MiB | 37.68 MiB | 11.8 % |
| `v3-no-usercmds` | 5.00 MiB | **10.14 MiB** | 3.2 % |
| `v2c-no-anim-contiguous` | 19.71 MiB | 37.71 MiB | 11.8 % |
| `v3c-no-usercmds-contiguous` | 5.03 MiB | **10.17 MiB** | 3.2 % |

### 3.3 Measured vs the study's estimates

| Claim | Study (§3.2 / §3.4) | Measured | Verdict |
|---|---:|---:|---|
| 3-round verbatim, matchmaking | 36.5 MiB | 36.56 MiB | exact |
| 3-round verbatim, pro | 43.3 MiB | 43.26 MiB | exact |
| 3-round `svc_UserCmds` stripped, matchmaking | ~9.0 MiB | **10.71 MiB** | estimate **19 % low** |
| 3-round `svc_UserCmds` stripped, pro | ~13.6 MiB | 10.17 MiB (not like-for-like) | see below |

The verbatim rows were exact because nothing is estimated there. The stripped rows came from scaling each
frame's compressed size by its surviving share of decompressed bytes; that approximation is optimistic
because `svc_UserCmds` compresses better than what remains, so removing it removes less on-disk weight
than its decompressed share suggests.

The pro-demo stripped figure is **not** directly comparable: the shipped `v3` also drops
`DEM_AnimationData`, which the study's estimate kept. Subtracting the measured animation saving
(43.23 → 37.68 MiB) from the same file puts a UserCmds-only pro trim at roughly **15.7 MiB** — again
above the study's ~13.6 MiB estimate, in the same direction and of the same order as the matchmaking gap.

### 3.4 Where the removed bytes came from

Over the 3-round window, by decompressed inner-message bytes (confirms the study's §3.3 exactly):

| Message | Matchmaking | Pro |
|---|---:|---:|
| `svc_UserCmds` | 75.5 % (32.52 MiB) | 71.4 % (39.57 MiB) |
| `svc_PacketEntities` | 22.7 % | 17.7 % |
| `DEM_AnimationData` | — | 10.0 % |
| everything else | ~1.8 % | ~0.9 % |

193 430 (matchmaking) / 199 667 (pro) `svc_UserCmds` messages were removed from the 3-round windows.

---

## 4. What was verified

All 24 emitted candidates pass. `dotnet run --project tools/DemoViewer.NET.DemoTrimmer -- trim …` exits
non-zero if any candidate fails, so the checks below are enforced, not just reported.

### 4.1 Every candidate

* **Parses.** `DemoParser.Parse` on the emitted file, no exception.
* **Container is well-formed** — the part `DemoParser.Parse` structurally cannot see, since it starts at
  byte 16 and stops at `DEM_Stop`. Re-reads the emitted file's raw bytes and asserts that the file
  header's two offsets each resolve to a frame *of the expected command*, that the `DEM_Stop` terminator
  is present, and that the rewritten `DEM_FileInfo` re-parses with `playback_ticks` equal to the window
  end and no out-of-window `round_start_ticks`. Without this every candidate could carry dangling
  offsets and no tail and still pass every other check here — which is precisely the shape most likely
  to be rejected outright by CS2 (§5.2, §6.4).
* **Metadata identical** to the source: map, tick interval, server/client name, game directory, build
  number, patch version, demo version name + GUID, server start tick, addons, schema presence (12 fields).
* **Game-event stream identical.** Every event the retained *source* frames produced, matched
  one-for-one against the trimmed file's events by name, game tick, server tick, event id and every
  decoded field value. For the 3-round matchmaking window that is a 1 575-event sequence compared
  element-wise.
* **Tour content present.** ≥ 1 `player_death` and ≥ N `round_freeze_end` in the window — a trim whose
  "rounds" are warmup would parse perfectly and still be useless for the Stats / 2D-playback tour steps.
* **Entity stream identical** — the real test. See below.

### 4.2 Entity verification (three-way)

Comparing a from-frame-0 source replay directly against a checkpoint-entry trim would fail for a
*correct* trim, so the comparison is three-way:

| | Replay |
|---|---|
| **D0** | source, from frame 0. Informational baseline. |
| **D1** | source frames in exactly the retained order. **The reference.** |
| **D2** | the emitted file, re-parsed, replayed from its own frame 0. |

**`D2 == D1` is the assertion.** Digests are FNV-1a 64 over every live entity's index, class name, serial,
PVS flag and every received field (keys ordered; floats canonicalized through
`BitConverter.SingleToInt32Bits`, never through formatting — `System.HashCode` is process-randomized and
useless as an oracle). Sampling happens at **every round boundary plus the window end**, not only the
end, so a mid-window desync that a later `DEM_FullPacket` would heal cannot hide.

Result: **identical hashes at every sample tick for all six variants on both demos**, with
`DeltaUnknownCount = 0` and no decode error. Concretely, for the matchmaking 3-round window all six
variants produce `5e1a916b62a8a1de` / `4a31465845fe35f2` / `1a9ca9c0f7c4d0a3` / `3e103e29b4a29695` at
ticks 1761 / 7511 / 13842 / 19344 — the same values the untrimmed source produces.

**Stripping `svc_UserCmds` changes nothing about the reconstructed world.** That is now measured, not
assumed.

For the checkpoint-entry variants a fourth replay runs — the same file read *naively* (§5.1) — and the
`EntityTracker` prints a decode-error stack trace to stderr from it. **That trace belongs to the
deliberately-broken naive read, not to D1 or D2**: `LastEntityError` is sticky, and every D1/D2 digest
reports no error and `DeltaUnknownCount = 0`. A decode stack trace under a `VERIFY: PASS` line is
expected for `v1` / `v2` / `v3`, and is itself the evidence for §5.1.

### 4.3 The encoder-identity gate

Before dropping anything, every packet in the window is re-encoded through the new bit writer with an
**empty** drop set and compared to the original — bit-exactly over the bits produced (the recorder's
`CDemoPacket.data` length is byte-rounded, so trailing padding is not ours to reproduce).

| | Packets checked | Bit-exact | Mismatch |
|---|---:|---:|---:|
| Matchmaking, 3 rounds | 19 348 | 19 348 | **0** |
| Pro, 3 rounds | 15 362 | 15 362 | **0** |
| Both demos, all rewriting variants | 59 000+ | all | **0** |

Without this gate a V3 failure would be uninterpretable — "the bit writer is broken" and "CS2 needs
UserCmds" look the same from outside. It is now ruled out.

### 4.4 Tests

`tools/DemoViewer.NET.DemoTrimmer.Tests/` — TUnit, `[NotInParallel]`, one heavy parse per process,
`SkipTestException` when no demo is present (12 tests; 7 skip cleanly with no demo available).

* `BitStreamWriterTests` — no demo needed. `WriteUBitVar` / `WriteUVarInt32` / unaligned `WriteBytes`
  round-trip through the parser's own `BitBuffer`; empty-drop-set identity; selective drop.
* `DemoTrimRoundTripTests` — V0 / V1 / V2 / V3 / V3C end-to-end trim → re-parse → full verification;
  encoder identity sampled across the whole window on real packets; monotonic size shrink down the
  ladder. The two contiguous variants (V0 and the recommended V3C) additionally assert the from-frame-0
  baseline `D1 == D0` as a hard failure — for a contiguous trim the retained frames *are* source frames
  0..EndIndex, so any difference is a bookkeeping bug rather than a property of checkpoint entry.

---

## 5. Findings

### 5.1 Checkpoint entry costs readability and buys nothing

`EntityTracker.ProcessFrame` **deliberately skips** a `DEM_FullPacket`'s `svc_PacketEntities`
(`EntityTracker.cs:1777-1789`): during sequential playback that snapshot is redundant with the delta
stream that already built the state, and re-applying it double-delivers ENTERPVS and cascades into
bit misalignment a few packets later. A trim that *enters* at a checkpoint has no such prior stream, so a
sequential reader silently decodes garbage — measured at **23 826 unknown deltas** on the 1-round
matchmaking `v1` file, ending in a hard `FieldPath is full` decode failure.

Reading such a file requires `ResetEntitiesKeepSchema()` + `LoadInstanceBaselineSnapshot()` +
`ProcessFullPacketCheckpoint()` on the entry frame. **DemoViewer.NET's own demo-load path does not do
this** — it is a plain sequential replay. So `v1` / `v2` / `v3` are *not* loadable by the app they were
built for without a code change.

And the payoff is nil: both reference demos place their first `DEM_FullPacket` at tick 1 and their first
`round_freeze_end` at tick 1761 / 1275, with checkpoints every 3 840 ticks (60 s). The last checkpoint at
or before round 1 is therefore the demo's *own start*, and checkpoint entry saves exactly one frame —
**0.03 MiB**. The trimmer still emits these variants (they are the specified ladder, and they are the only
way to test the mid-stream-entry dimension in CS2), but the design should not depend on them. Use
`--skip-boundaries N` to force a genuine mid-match entry if that dimension ever needs testing.

Secondary constraint found while making checkpoint entry work at all: the full-packet string-table dump
is **incremental**, so a checkpoint may carry no `instancebaseline` snapshot. Neither demo's entry
checkpoint does; the retained setup prefix supplies the baselines instead. A genuinely mid-match entry
would have to walk back to the most recent checkpoint that carried the table.

### 5.2 Container facts the parser hides — and a doc defect

**`DemoParser.Parse` stops *at* `DEM_Stop`**, so `ParsedDemo.Frames` never contains the last three frames
of a real CS2 demo. Measured layout of both demos:

```
… DEM_Packet@last · DEM_Stop@last · DEM_SpawnGroups@last · DEM_FileInfo@last · EOF
```

A trimmer that re-emits only `ParsedDemo.Frames` therefore drops `DEM_Stop`, `DEM_SpawnGroups` and
`DEM_FileInfo` and leaves both file-header offsets zero. That is very likely fatal to the real CS2 client
and would have been invisible to any parser-side check. `DemoTail` reads them straight out of the raw
bytes; the trimmer re-emits all three, re-headered at the trim's last tick.

Two consequences worth recording:

* `ParsedDemo.TickCount`'s documented "sourced from `CDemoFileInfo.PlaybackTicks` when available" path
  **never fires on either reference demo** — the frame is past `DEM_Stop`, so it is not in the array the
  enrichment pass walks, and `TickCount` falls back to the highest observed frame tick. Whether that
  generalises to every CS2 demo was not tested (n = 2), but the mechanism is structural rather than
  demo-specific.
* **`DemoParser.cs:86-88` has the two file-header offsets backwards.** Measured on both demos:

  | Bytes | Comment says | Actually is |
  |---|---|---|
  | 8-11 | spawngroups stream offset | **`DEM_FileInfo` frame offset** |
  | 12-15 | reserved / `CDemoFileInfo` offset | **`DEM_SpawnGroups` frame offset** |

  `DemoParser.cs` is a protected file and was **not** modified. This is load-bearing for anyone writing a
  demo *writer*, which is exactly what this POC is.

### 5.3 Rewritten `DEM_FileInfo`

`CDemoFileInfo` is re-emitted with the source message *cloned* — field shape preserved, only values
changed — so a demo with no `game_info` does not gain one:

* `playback_ticks` → the last retained frame's absolute tick
* `playback_frames` → count of emitted `DEM_Packet` + `DEM_FullPacket` frames
* `playback_time` → `playback_ticks × tick_interval`
* `CGameInfo.cs.round_start_ticks` → filtered to `≤ playback_ticks` (neither reference demo populates it)

Example (matchmaking, 3 rounds): `ticks=90568 frames=90564 time=1415.12s` →
`ticks=19344 frames=19349 time=302.25s`. If CS2 rejects *every* candidate, this hand-built frame is the
first suspect — the exact before/after is printed by the tool for every run.

### 5.4 The rewriting variants mix compressed and uncompressed frames

A rewritten frame is re-compressed only when Snappy actually makes it smaller. After `svc_UserCmds` is
removed many packets are down to a `net_Tick` and little else, and compressing a dozen bytes grows them.
Measured on the 3-round files:

| | Frames rewritten | Left uncompressed |
|---|---:|---:|
| Matchmaking `v3` / `v3c` | 19 348 / 19 349 | **8 459** (44 %) |
| Pro `v3` / `v3c` | 15 362 / 15 363 | **672** (4 %) |

Per-frame compression is a wire-format flag, and the source demos already mix (their whole tail is
uncompressed), so this is legal. It is recorded here because it is a real difference between `v3*` and
every other variant, and therefore a candidate explanation if `v3*` alone misbehaves in CS2. Forcing
compression on every rewritten frame is a one-line change in `DemoTrimWriter.WritePayloadFrame` and
would cost ~17 KB on the matchmaking 3-round file.

### 5.5 Ticks are not rebased

Frame header ticks keep their source values. Rebasing only the headers would contradict the tick values
embedded inside the payloads (`net_Tick`, game events, entity data). For the contiguous variants this is
a non-issue — they start at the demo's own tick 0. For the checkpoint-entry variants the file starts at
tick 1, which is also fine. It would matter for a genuine mid-match trim (the file would appear to start
at e.g. tick 40 000), and is called out in the CS2 protocol below.

---

## 6. Protocol: testing a candidate in CS2

All commands verified against the `cs2-opendocs` submodule's `docs/commands.md` / `docs/convars.md`.

### 6.1 Setup

1. Copy the candidate into the CS2 game directory:
   `.../Steam/steamapps/common/Counter-Strike Global Offensive/game/csgo/`
   (`playdemo` resolves relative to `csgo/`; a subfolder works as `playdemo trimmed/<file>`).
2. Launch CS2 with the console enabled (`-console`), and add `-dev` if you want `demo_debug` available —
   it is flagged `developmentonly` and is not settable in a plain release launch.
3. In console, before loading anything:
   ```
   developer 1
   demo_pause_at_end 1
   ```
   `demo_pause_at_end 1` stops CS2 quitting to the main menu at the end of the file, which otherwise
   looks identical to a crash-on-truncation.

### 6.2 Per candidate

```
listdemo trimmed/<file>.dem       // reads the container WITHOUT starting playback
playdemo trimmed/<file>.dem
demo_info                         // map, tick count, playback length as CS2 understands them
demoui                            // scrubber — check its total length and that the handle moves
demo_gototick 3000                // seek forward
demo_gototick 500                 // seek backward
demo_timescale 4                  // fast-forward through the whole window
```

Then watch playback through to the end of the retained window.

### 6.3 What "pass" looks like

* `listdemo` prints the header without error.
* `demo_info` reports the trimmed map and a tick count matching the file's window (§5.3), not the
  original match's ~90 000.
* Players are visible, animated, moving, shooting; weapons in hand; the HUD scoreboard populates.
* Kills appear in the kill feed at the right moments.
* Both seeks land and resume cleanly.
* Playback reaches the end of the window and pauses (because of `demo_pause_at_end 1`).

### 6.4 What failure looks like — and what each mode implicates

| Symptom | Most likely cause |
|---|---|
| Refuses to load / "corrupt demo file" at `listdemo` | file header offsets or the `DEM_Stop`/`DEM_SpawnGroups`/`DEM_FileInfo` tail (§5.2) |
| Loads, then hangs on the loading screen | missing setup frames, or a `DEM_SpawnGroups` payload CS2 cannot reconcile |
| Loads, world renders, **players frozen / T-posing / sliding** | **the `svc_UserCmds` removal** — the expected failure mode for `v3` / `v3c` |
| Crash at the first tick past the entry point | mid-stream entry (`v1` / `v2` / `v3`) — retest the contiguous sibling |
| Scrubber length wrong, seeks land in the wrong place | the rewritten `DEM_FileInfo` (§5.3) or unrebased ticks (§5.5) |
| Console spam about unknown/missing messages under `developer 1` | note the message name — it identifies what the strip removed that CS2 wanted |

### 6.5 Suggested order

Bisect rather than testing all 24. Use the 3-round matchmaking files:

1. `mm-nuke-v0-contiguous-3r.dem` — if this fails, the *container writing* is wrong and nothing else
   matters. It is a byte-verbatim copy of the source's first 19 364 frames plus a rebuilt tail, so a
   failure here points squarely at §5.2 / §5.3.
2. `mm-nuke-v3c-no-usercmds-contiguous-3r.dem` — the candidate we actually want to ship. If this plays,
   stop: 10.71 MiB and done.
3. If (2) fails but (1) passed → `svc_UserCmds` is required by CS2. Confirm on the pro demo
   (`pro-nuke-v3c-…`), then fall back to `pro-nuke-v2c-no-anim-contiguous-3r.dem` (37.71 MiB) to check
   whether animation frames alone are droppable.
4. `mm-nuke-v1-verbatim-3r.dem` — only if the mid-stream-entry question matters independently. It is not
   needed for the tour (§5.1).

---

## 7. Ranking by likelihood of surviving CS2 playback

| Rank | Candidate | 3-round size (mm / pro) | Why |
|---|---|---|---|
| 1 | `v0-contiguous` | 36.56 / 43.26 MiB | byte-verbatim frame copy from frame 0; only the rebuilt 3-frame tail and file header are new |
| 2 | `v2c-no-anim-contiguous` | 36.56 / 37.71 MiB | adds only whole-frame removal of client-side animation data; no payload is rewritten |
| 3 | `v1-verbatim` | 36.54 / 43.23 MiB | payloads verbatim, but enters mid-stream — a dimension CS2 may or may not tolerate, and one that breaks every sequential reader we control |
| 4 | `v2-no-anim` | 36.54 / 37.68 MiB | rank 2's removal on top of rank 3's entry risk |
| 5 | **`v3c-no-usercmds-contiguous`** | **10.71 / 10.17 MiB** | the one we want; every packet payload is decoded, edited, re-serialized and re-compressed, and `svc_UserCmds` is gone. Going in, the expectation is this is the one CS2 rejects |
| 6 | `v3-no-usercmds` | 10.68 / 10.14 MiB | rank 5's rewrite *and* rank 3's entry risk — strictly the most exposed |

Reading it as a decision: **ranks 1-2 are near-certain to work and too big to ship. Rank 5 is the
artifact worth having and the one under genuine doubt.** The 1-round files (4.16 / 5.03 MiB for `v3c`)
exist for the same test at a smaller size.

Note that `v0` and `v2c` on the matchmaking demo are the same size — that demo carries no
`DEM_AnimationData` — so ranks 1 and 2 only diverge on the pro demo.

---

## 8. Bearing on the tour decision

The study's recommendation was: treat CS2 playability as nice-to-have, since the tour only needs the demo
to drive DemoViewer's *own* Stats and 2D-playback surfaces. That recommendation survives, and is now
better supported:

* **DemoViewer.NET-parsable at ~10.2-10.7 MiB is achieved and verified.** `v3c` decodes to a
  bit-identical entity stream and an element-wise-identical game-event stream over its window.
* The remaining open questions from the study's §5 are unchanged — installer budget, and whose demo gets
  redistributed. The POC does not touch either. A purpose-recorded local demo would still be smaller and
  would sidestep the consent question entirely; the trimmer works on any `.dem`, so it composes with that
  option rather than competing with it.
* If CS2 playability *does* become a requirement (verify-in-CS2 on the sample demo), §6 settles it in
  about ten minutes of manual testing.

---

## 9. Using the tool

```sh
# What is in a demo: byte breakdown, round-boundary ladder, container tail
dotnet run --project tools/DemoViewer.NET.DemoTrimmer -c Release -- inspect <demo.dem>

# Emit the full ladder at 1 and 3 rounds, with verification (exit code 2 if any candidate fails)
dotnet run --project tools/DemoViewer.NET.DemoTrimmer -c Release -- \
    trim <demo.dem> --out demos/trimmed --rounds 1,3 --prefix <name>

# Options: --variants v0,v3c   --boundary round_freeze_end   --skip-boundaries N
#          --no-verify   --no-baseline   --no-identity-check

# Tests
DEMO_PATH=<demo.dem> dotnet run --project tools/DemoViewer.NET.DemoTrimmer.Tests
```

One demo at a time — a 170-450 MB source plus its `ParsedDemo` plus two `EntityTracker` replays is
already most of a 16 GB machine's headroom. A full 12-candidate run takes ~25-30 s per demo.
