---
name: workflow-model-routing
description: The owner wants Sonnet used for workflow agents where appropriate to cut token use; the routing rule applied to the Strat Book build lanes
metadata:
  type: feedback
---

Asked 2026-09-24 during the Strat Book workstream: "allow for Sonnet to be used [for] tasks when appropriate to reduce token usage."

Routing applied in the build-lane scripts (`modelOpts(opts, model)` helper):
- First-pass verifier (diff rules, build, test tiers, merge and push): **Sonnet**.
- Implementers of wiring, docs or otherwise mechanical items: **Sonnet** (per item `model: 'sonnet'`).
- Implementers of design-heavy items, the fix pass, and the re-verify after a failed first verdict: the session model (escalation).

**Why:** cost is turn count x context (see [[agent-token-cost-model]]); verifiers and light items are most of the turns but little of the judgment.
**How to apply:** carry the helper into every new workflow script (Phase 3 onward); when relaunching a running lane to pick it up, relaunch with `args.startAt` at an item boundary, not `resumeFromRunId` (changed opts invalidate the cache). Related: [[strat-book-workstream-rules]].
