---
name: strat-room-plan
description: "The Strat Room feature set (Situation Search, Round Tagger, Strat Book, Utility Book, Review Packs, Opponent Dossier) has a LOCAL, UNCOMMITTED plan + status board at docs/strat-room/plan.md, excluded via .git/info/exclude; work items are referred to by their human-readable names only"
metadata:
  node_type: memory
  type: project
  originSessionId: c601929d-ef26-465e-8cbd-da10642df0f3
  modified: 2026-09-24T07:48:33.307Z
---

The six-feature "Strat Room" roadmap (from the claude.ai artifact Ho9tuwBGY637hisGT6QbRL) is planned in
`C:\dev\DemoViewer.NET\docs\strat-room\plan.md`. Created 2026-09-23 against `main` at `d90ec9f`.

**Why:** it began as a local, uncommitted board (listed in `.git/info/exclude`). On 2026-09-24, with
all nine designs approved, the owner promoted `docs/strat-room/` into the tree: the exclude entry is
gone and the folder is staged/tracked like any other doc. It is the project's plan of record now.

**How to apply:**
- Read the plan before touching any of these features. It is the board: update the §5 row and add a
  dated line to §7 when something moves; do not rewrite the prose.
- Refer to work items by their exact names ("Place Names From The Pawn", "Team Identity", "Round
  Facts", "The Round Index", "Tag Store", "Strat Model", "Grenade Walk", "Review Queue", …). The owner
  asked for no abbreviations.
- Anything that adds a sizable new concept to CS2DemoKit, the asset baker, or the CSVG plugin is a
  `design` item that stops for human review before code.
- Key findings that overturn the artifact: the round-index token needs only `PositionSampler.Place`
  (not issue #5); LiveSync has no setpos path (walk-in-server is deferred, decision D4); "our team /
  opponent" needs a Team Identity design nobody had named; Valve matchmaking demos DO carry every
  player's inputs but current ones are delta-encoded and unreadable by CS2DemoKit 0.12.0 (see
  [[usercmd-delta-data]]; plan items Inputs Per Demo Source, Delta User Commands, decision D8).
- DESIGN PHASE CLOSED 2026-09-24: all nine designs approved (banners at the top of each file record
  the owner's answers). Upstream issues open on CS2DemoKit: #53 delta usercmds, #54 rules-driven
  round facts (Round Facts build waits on it), #56 smoke creation packet, #58 PositionSampler
  contract, #59 ProjectileSampler. Not filed by decision: Suggested Tags route (b). Build order:
  `designs/00-overview.md` §5 (Content Identity → Round Facts → The Round Index → ...). Nothing is
  implemented yet; every build item still starts from its approved design.
- Designs live in `docs/strat-room/designs/` (also uncommitted). Approved so far: Team Identity
  (Valve 3-of-5 roster rules bind, §11) and Round Facts (RULES-DRIVEN: a shipped `round_facts`
  ruleset, HLTV buy bands as params, engine pieces filed as CS2DemoKit #54; no app-side extractor).
  The owner prefers engine capability requests over app-side workarounds: CS2DemoKit "moves fast".
  Also approved: Zone Baking (no bundle.json bump; user zone overlays in <config>/zones/) and The
  Round Index (D2 = JSON sidecars + in-memory postings, measured on 100 REAL demos; Ctrl+F for Find
  Rounds Like This). Real-corpus probe: scratchpad/round-index `scanreal` mode over real100/json.
- Related: [[cs2demokit-tick-clocks]] (all new stores use the frame clock), [[cs2demokit-local-checkout]]
  (engine questions go to the DLLs), [[never-link-the-demo-corpus]] (research over `DEMO_PATH`).

**Status 2026-09-25 04:10:** Phases 0 to 3 built and merged on `feature/strat-book` (head `e0a3e90`, pushed; no PR). Paused at the owner's request for an early live review before Phase 4 (Utility Book and Review Packs). Build lanes live in workflows/scripts/strat-book-phase{1,2,3}.js; see [[workflow-model-routing]]. Real-demo Situation Search, Suggested Tags and Free Labels stay empty until CS2DemoKit #54 ships Round Facts rows.
