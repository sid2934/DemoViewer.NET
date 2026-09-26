---
name: strat-book-workstream-rules
description: "The owner's standing rules for the Strat Book implementation workstream (branch layout, one-item-at-a-time builds, verify-merge-push, no planning markdown commits, no .dem deletion, no PRs until live verification, 15-minute updates, park upstream-blocked work)"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c601929d-ef26-465e-8cbd-da10642df0f3
  modified: 2026-09-24T08:12:11.050Z
---

Set by the owner on 2026-09-24 when the Strat Room design phase closed and implementation began.

- Integration branch is `feature/strat-book` (from `main` at `d90ec9f`). Never work on `main`.
- Each phase/stage/sub-feature gets its own `feature/strat-book/<slug>` branch, merged locally
  (`--no-ff`) into `feature/strat-book` only after it builds and its tests pass; then push
  `feature/strat-book` to origin for safe-keeping. **No PRs** until the whole workstream is done and
  the owner has verified it in a live application run.
- **Implementation and testing: one item at a time.** Planning, research and design-doc updates may
  run in parallel (they write only under `docs/strat-room/`).
- **Never commit planning or temporary markdown** (`docs/strat-room/` stays untracked in the working
  tree; agents stage by explicit path, never `git add -A`). Code docs the designs ask for are fine.
- **Never delete, move, copy or link a `.dem` file.** Real-demo tests read the Steam replays folder
  through `DEMO_PATH`.
- Items blocked on CS2DemoKit (#53, #54, #56, #58, #59): make all the progress that does not need the
  engine, park the rest behind a seam with a diagnostic, and pick it up when a release lands.
- Keep the review board artifact (https://claude.ai/artifact/7wAUkyXT6BQJRNQtjXWqWw, which the owner
  calls "The Strat Book" taskboard) updated as items complete, and give a terse foreground progress
  update at least every 15 minutes (ScheduleWakeup loop).
- Commit messages: repo style (lowercase, blunt), no trailers, no attribution.

**Why:** the owner wants an unattended but supervised build with a clean history, safe demos, and a
single reviewable integration branch. **How to apply:** every orchestration workflow's agent prompts
carry these rules verbatim as a guard block; the verifier agent is the only one that merges/pushes.
Related: [[strat-room-plan]], [[never-link-the-demo-corpus]], [[attribution-suppression]].

**Update 2026-09-26:** the owner reversed the no-planning-commit rule for `docs/strat-room/`. It is now committed on feature/strat-book (fb5debe) with a root CLAUDE.md saying DO NOT MERGE. Both must be deleted before the squash merge to main. Keep plan.md committed and current on the branch.
