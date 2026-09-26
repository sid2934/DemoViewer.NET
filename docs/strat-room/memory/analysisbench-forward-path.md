---
name: analysisbench-forward-path
description: Since CS2DemoKit 0.12.0 AnalysisBench defaults to the streaming path; perf-sweep.sh needs --retained, and forward peak heap is demo-size-independent
metadata:
  type: project
---

**AnalysisBench's default run is the forward (streaming) path** as of the 0.12.0 adoption
(PR #17, 2026-09-18). The old parse-then-evaluate path is `--retained`, and it is still the
one the app's playback uses and the only one the frame-walking debug modes have.

**Why it matters when reading bench output:** the two paths print different labels.
Retained prints `Read:` / `Parse:` / `Eval:` / `Total (parse+build+eval)`; forward prints
`Hash:` / `Run:` / `Total (run)` and no parse phase at all. Any script grepping the old
labels silently produces empty cells. `scripts/perf-sweep.sh` is pinned to `--retained`
for exactly this reason: its columns (parse_ms, the three pass timings, frames,
compressed) only exist on that path.

**Measured, 8 demos 220-706 MB, interleaved 3 rounds** (method: [[bench-run-variance]]):
- 0.11.0 to 0.12.0 on the *same* retained path: analysis -6.3% to -20.8%, peak heap
  -15.5% to -26.8%, allocation -36.3% to -44.1%. Free win from the package.
- retained to forward on 0.12.0: analysis +5.1% to +44.4% SLOWER, peak heap -73% to -89%.
- **The forward path's peak managed heap does not scale with demo size**: 222-289 MiB
  across the whole corpus. Retained runs about 3-4 MiB of heap per MB of file, so a 706 MB
  demo reaches 2220 MiB.

**How to apply:** comparing the two paths, quote `analysis` (retained parse+build+eval vs
forward run), NOT the bench's end-to-end line. Both paths SHA-256 the demo, but retained
hashes the buffer it already holds and leaves it untimed while forward reads the file
again and reports it as `Hash`. Same work, counted on one side only.

**Also:** `docs/benchmarks.md` and the whole `docs/perf/` tree were deleted in PR #7 when
the parser and engine moved to CS2DemoKit. There is no benchmark doc in this repo now;
`docs/profiling.md` is the surviving bench documentation. Do not go looking for it again.
