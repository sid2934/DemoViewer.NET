---
name: tour-sample-demo-invalid
description: assets/tour/sample-de_nuke.dem is incomplete - never use it for analytical results; schema probes and render smoke tests are the only valid uses
metadata: 
  node_type: memory
  type: project
  originSessionId: c90480f9-9c74-4d26-beb9-6f3ec4456d8a
  modified: 2026-09-14T07:50:05.447Z
---

`assets/tour/sample-de_nuke.dem` is an **incomplete demo**. Any analytical result derived
from it is invalid — stats, aim metrics, parity pins, oracles, goldens of computed values.

**Why:** the owner stated this directly (2026-09-14). It is the only `.dem` committed to the
tree (`demos/**/*.dem` is gitignored), which makes it a standing temptation: whenever a
demo-gated test skips, pointing it at the tour sample looks like the obvious fix and is
always the wrong one. A test that skips for want of a valid demo is behaving correctly.

**How to apply:** never route a test, fixture, parity pin, oracle, or benchmark that produces
numbers at this file, and never propose it as the way to un-skip a demo-gated test.
`DemoTestHelper` deliberately does not look in `assets/tour/` — leave that alone.

Two uses ARE legitimate, because they do not depend on the demo being complete:
- **Schema/wire-type probes** (`EntityDecodeProbe --schema`) — serializer classes, wire types
  and encoders come from `DEM_SendTables` at the head of the file.
- **Render/CLI smoke tests** (the dv2d `export` lane in CI) — exercising the code path, not
  measuring anything.

Related: [[cs2demokit-tick-clocks]], [[stat-evidence-anchors]]
