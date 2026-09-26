---
name: playback2d-v2-design
description: "Playback2D v2 rework — PR #10, still OPEN as of 2026-08-31; Skia clean-core compositor, annotations, export, levels, timeline, dv2d CLI; open criteria in design.md §0"
metadata: 
  node_type: memory
  type: project
  originSessionId: f4d150c9-b460-45a3-937c-81711633d423
  modified: 2026-08-31T17:03:50.601Z
---

The 2D playback rework (branch `feature/playback2d-v2`) is **PR #10**
(https://github.com/sid2934/DemoViewer.NET/pull/10), 9 phases. Feature work landed 2026-08-25, but
**the PR is still OPEN as of 2026-08-31** — do not assume it merged. 112 commits above `efbae01`
(v0.7.2) after the 2026-08-31 branch scrub; the branch was force-pushed then, so any clone from
before that date is on dead history.
Design: `docs/playback2d-v2/design.md` (rev 2) — **§0 is the open-criteria ledger**; plans + per-phase
deviation logs in `docs/playback2d-v2/plans/`. Status board artifact:
https://claude.ai/code/artifact/ca6be4f6-7189-4a00-8440-80adf9c8cbf9

Architecture as shipped:
- `src/Playback2D/DemoViewer.NET.Playback2D.Core` (SkiaSharp-only) → `…Pipeline` (Core + CS2DemoKit) →
  App + `tools/DemoViewer.NET.Playback2D.Cli` (`dv2d render/export/bench/probe/golden`).
- CPU raster provider is the contract baseline + golden authority; GPU provider is a Stage-0 seam only.
- Pre-v2 control retained one release behind a toggle; removal plan in `docs/playback2d-v2/old-control-removal.md`.
- Export replays a private tracker (EntitySeekService), never the shared clock; ffmpeg ladder
  PATH → BtbN download → ImageSharp GIF floor.

Key shipped numbers: parity 99.68%±8 vs pre-v2 golden; render p99 2.5 ms @1080p (8 ms budget);
0 B/frame steady state; export 2.70× realtime @720p60 (shipped default).

Open items (design.md §0, O1–O5): 1080p60 CPU export 0.97× realtime; GPU ≥2× (needs C2 Stages 1–2 on a
GPU spike session); mirage goldens `duel-mirage-b`/`fitmap-mirage-eco` await a de_mirage demo; R2
scrub-latency measurement; envelope drag handles. Also: browser head ships untrimmed (16.3 MB brotli),
baked radar art absent on browser (`docs/playback2d-v2/wasm-matrix.md`), the `wasm-build` CI job's
first run needed babysitting, one CLI Budget bench fails by documented expectation until
`SceneLayerCatalog.Create` registers B1's layers.

Test asset: `assets/tour/sample-de_nuke.dem` (trimmed 3-round two-floor pro demo) — resolved
repo-relative by DemoTestHelper; the `nuke-multilevel` golden pins pre-v2 behavior.
