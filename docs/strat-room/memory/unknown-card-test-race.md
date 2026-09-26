---
name: unknown-card-test-race
description: The App suite has several cross-test races; WHICH ones fail depends on how the suite is partitioned into processes, and CI sees none of them
metadata:
  type: project
---

The App suite has at least three tests that fail only under cross-test interference, and **the
partitioning decides which**. Measured on merged main at `15fbec2`, same tree, same tier:

| how it is run | failures |
|---|---|
| `scripts/test-app-suite.sh -t full` (3 processes, **what CI runs**) | `SelectingFrameWithUnknowns_BuildsWireDecodedUnknownCards`, `RenderCapture_UnknownCardInCardList` |
| `scripts/test.sh -t full -p app` (1 process) | `TheChip_JoinsTheStrip_WhileRunning_AndLeavesOnDismiss` |

All three pass in isolation. Two agents reported different "the failing test" for this reason, and
both were right.

**Why CI is green anyway:** the batch runner is what `ci.yml` invokes, and the unknown-card pair is
`[Category("RealDemo")]`, so with no demo corpus on the runner they skip. Reproducing any of this
needs `demos/` present locally.

**The two mechanisms, both real:**
- The unknown-card pair: `DemoParser.OnUnknownMessageType` is a STATIC event that both
  `MainViewModel` and the test's own `DemoCensus` subscribe to, so a concurrent parse elsewhere in
  the batch pollutes the census the assertion is measured against.
- `TheChip_JoinsTheStrip...`: constructs `MainViewModel` off the UI thread, which builds
  `LibraryTabView` and throws out of `ItemCollection.Add`. Avalonia 12 enforces thread affinity
  that 11 did not, the same class of failure as [[wsl-linux-ci-repro]]'s notes on the What's New
  gate tests. Its own file already uses `HeadlessSession.RunOnUi` six times, so the fix is to wrap
  it like its siblings.

**How to apply:** do not treat any single run as authoritative, and say which runner produced a
result. A "fixed" cross-test race may just have moved to a different victim. None of these are
regressions from the change under test, so check main with the SAME runner before attributing one.
