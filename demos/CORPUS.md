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
| `[Category("RealDemo")]` tests | `DemoTestHelper`: `DEMO_PATH` (a `.dem`, or a folder of them), then the reference demo `003816248937665266002_0544286934.dem` if present, then `TestData/`, then `demos/benchmarks/`, then `demos/` |

A folder `DEMO_PATH` (the Steam replays directory, read in place) resolves to ONE demo for the whole
run: `DEMO_PATH_PICK=<filename>` when set, else the reference demo when the folder holds it, else the
`.dem` whose name sorts first by ordinal comparison, top level only. A `DEMO_PATH_PICK` that is not in
the folder resolves to nothing rather than to the first file, and the skip reason says so. The
pinned-name lookups (`RequireDemo("furia-vs-vitality-m1-mirage.dem")` and the golden candidate
lists) do not look inside a folder `DEMO_PATH`, for the reason in the `_410.dem` section below.

Absent demos are a SKIP, not a failure: with an empty corpus the App suite still passes and simply
runs about 84 fewer tests (the count when this file was written; it drifts with the suite). CI has
never had the corpus, which is why CI is green regardless.

Layout as of 2026-10-22: 26 `.dem` files and 5 `.bz2` archives, about 6.8 GB of `.dem` in total.

## demos/ (loose, matchmaking)

| file | size |
|---|---|
| `003801777854962729156_0256036251.dem` | 371 MB |
| `003802582796258508821_1612401002.dem` | 467 MB |
| `003802730901763260580_1218921269.dem` | 289 MB |
| `003816306022075596881_1029495947.dem` | 309 MB |
| `003816313710067056698_1777110562.dem` | 451 MB |

These five are the decompressed copies of the archives in `bz2/`. The `_410.dem` and its `.info`
file also sit here; see the section below.

## benchmarks/ (matchmaking demos)

| file | size |
|---|---|
| `003816248937665266002_0544286934.dem` | 181 MB |
| `003816779297406845372_0003771537.dem` | 287 MB |
| `003816798820180689112_1066991563.dem` | 255 MB |
| `003816804281231606001_0126308730.dem` | 292 MB |
| `003816809596253634708_1168926414.dem` | 261 MB |

`003816248937665266002_0544286934.dem` is the reference demo that `DemoTestHelper` looks for by name.

## pro-demos/ (HLTV downloads)

| file | size |
|---|---|
| `furia-vs-vitality-m1-mirage.dem` | 741 MB |
| `furia-vs-vitality-m2-inferno.dem` | 605 MB |
| `furia-vs-vitality-m3-nuke.dem` | 334 MB |
| `furia-vs-vitality-m4-overpass.dem` | 605 MB |
| `vitality-vs-fut-m1-mirage.dem` | 330 MB |
| `vitality-vs-fut-m2-dust2.dem` | 481 MB |
| `vitality-vs-fut-m3-nuke.dem` | 391 MB |

`furia-vs-vitality-m1-mirage.dem` is the reference demo for the rule-graph measurements (434 nodes /
474 edges, and the `ShippedScale` capture).

## bz2/

The five loose matchmaking demos above, compressed: 186 to 306 MB each, same basenames plus `.bz2`.

## trimmed/

Three trimmer outputs, 11 MB each: `mm-nuke-v3c-no-usercmds-contiguous-3r.dem`,
`pro-nuke-v3c-no-usercmds-contiguous-3r.dem`, `pro-vit-nuke-v3c-no-usercmds-contiguous-3r.dem`.

## scratch/

`s7s8probe/`: a throwaway probe project and its text output, 104 KB. Not a demo location.

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

**The supported way to reach the corpus from elsewhere is the `DEMO_PATH` environment variable,
which `DemoTestHelper` honours ahead of every other location.** Never link it.
