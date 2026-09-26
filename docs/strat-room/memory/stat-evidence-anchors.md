---
name: stat-evidence-anchors
description: Why per-stat evidence clips (preaim etc.) need an engine change, and which stats do not
metadata:
  node_type: memory
  type: project
---

Deferred to the release after v0.8.1 (agreed 2026-09-14): letting a reviewer jump from a stat cell to
the ticks that produced it, rendered as a short lead/trail clip or screenshot. Not highlights; these
are evidence, and they must not enter the curated reel.

**The finding that decides the design, and it is not visible from the call sites.** Contact-anchored
stats cannot be re-derived app-side:

- `RuleChainTimeline` records ONLY logic-node rising edges (`StateGraphEvaluator` ~line 1515). Stat
  folds (`sum:`/`count:`) visit their rows and keep the total, emitting no timeline entry, so "which
  ticks made this number" is discarded by construction.
- `EnemySpottedEvent` is synthesized inside `EntityChangeScanner`, consumed by
  `SpottedEnrichmentEdge`, and dies there. It never reaches `demo.AllGameEvents` or `AnalysisRun`.

So preaim, XPlace, TTS, TTD, AimRx, TTK and SAcc% need CS2DemoKit to surface the anchor, which means
an engine release before the app half can be built (the 0.11.0 cycle shape). Spray, counter-strafe,
first-bullet and flick do NOT: they are derivable from `AllGameEvents`, and `SpraySampler` already
does exactly that, returning `SprayRun(StartTick, ...)` off an already-parsed demo with no engine
involvement. Clone that for those four.

**Proposed shape:** a `marks:` block as a sibling of `highlights:`, never an extension of it, or the
reel fills with unremarkable samples. A mark is a stat that retains its witnesses instead of only its
total, so it reuses the same anchoring machinery `HighlightFired(FrameIndex, Tick, PlayerSlot, ...)`
already provides. Opt-in per stat and cap witnesses per player, sampled across rounds rather than the
first N, or every strip shows round 1.

**Rendering splits too.** 2D is in reach now (`RenderCommand`/`ExportCommand` plus the clip-window
code behind `HighlightReelDialogViewModel` / `IReelJobService`), deterministic and golden-testable.
Real in-game footage means driving the CS2 client out of process (`playdemo`, `demo_gototick`), which
is platform-bound and not CI-testable: a separate capability, not a variant. The anchor data is the
same either way.

See [[assetbaker-run-windows]] for the asset side and [[sight-columns-no-data]] for what these columns
need in order to exist at all.
