# The local demo corpus

`demos/**/*.dem` is gitignored, so nothing here is recoverable from this repository. This file
records WHICH demos the corpus held, because on 2026-09-20 it was deleted and the filenames
survived only in a chat transcript. A manifest is cheap; reconstructing a list of match IDs from
memory is not.

Keep this current when the corpus changes.

## What the suites expect

| consumer | reads |
|---|---|
| `AnalysisBench --suite` | every `.dem` in `demos/benchmarks/` |
| `scripts/perf-sweep.sh` | `demos/benchmarks/` and `demos/pro/` |
| `[Category("RealDemo")]` tests | `DemoTestHelper`: `DEMO_PATH`, then `TestData/`, then `demos/benchmarks/`, then `demos/` |

Absent demos are a SKIP, not a failure: with an empty corpus the App suite still passes and simply
runs 84 fewer tests. CI has never had the corpus, which is why CI is green regardless.

## benchmarks/ (matchmaking demos)

| file | size |
|---|---|
| `match730_003769462952671838367_0003107139_392.dem` | 265 MB |
| `match730_003770081941211054251_0235605701_129.dem` | 256 MB |
| `match730_003776747382562095193_1164284481_129.dem` | 220 MB |
| `match730_003777311051922538654_1847680867_392.dem` | 251 MB |
| `match730_003777334669947699355_1809437145_392.dem` | 252 MB |

## pro/ (HLTV downloads)

| file | map | size |
|---|---|---|
| `furia-vs-vitality-m1-mirage.dem` | de_mirage | 706 MB |
| `furia-vs-vitality-m3-nuke.dem` | de_nuke | 318 MB |
| `furia-vs-vitality-m4-overpass.dem` | de_overpass | 576 MB |

`furia-vs-vitality-m1-mirage.dem` is the reference demo for the rule-graph measurements in
`docs/rule-graph/design.md` (§0.3's 434 nodes / 474 edges, and the `ShippedScale` capture).

## Do not stage `match730_..._410.dem` here without reading this

A demo matching `demos/match730_003826256877184877003_0981591541_410.dem.info` exists in the Steam
replays folder. It is NOT part of the corpus above and staging it changes test behaviour:
`Nuke_TwoFloors_MatchesGolden` lists it as its second demo candidate, AHEAD of the committed
`assets/tour/sample-de_nuke.dem` that its golden was actually captured against. Staging it
silently re-points that capture and the test fails on a 14.5% pixel difference.

## How the corpus was lost, so it does not happen twice

An agent made `demos/` reachable from a git worktree with a Windows directory junction, then ran
`git worktree remove --force`. The removal followed the junction and deleted the TARGET's
contents rather than the link.

`.claude/hooks/guard_demos.py` now asks for confirmation before any worktree removal, any
destructive command naming `demos`, and any attempt to link `demos/` into another tree.

**The supported way to reach the corpus from elsewhere is the `DEMO_PATH` environment variable,
which `DemoTestHelper` honours ahead of every other location.** Never link it.
