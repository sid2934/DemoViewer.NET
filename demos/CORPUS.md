# The local demo corpus

`demos/**/*.dem` is gitignored, so nothing here is recoverable from this repository. This file
records WHICH demos the corpus held, because on 2026-09-20 it was deleted and the filenames were
not recoverable from the repository. A manifest is cheap; reconstructing a list of match IDs after
the fact is not.

Keep this current when the corpus changes.

## What the suites expect

| consumer | reads |
|---|---|
| `AnalysisBench --suite` | every `.dem` in `<repo-root>/demos/benchmarks/`. It finds the repo root itself and does not honour `DEMO_CORPUS` |
| `[Category("RealDemo")]` tests | `DemoTestHelper`: `DEMO_PATH` (a `.dem`, or a folder of them), then the reference demo `003816248937665266002_0544286934.dem` if present, then `TestData/`, then `<corpus-root>/benchmarks/`, then `<corpus-root>/` |

## Configuration

Every variable is optional. With none of them set, resolution looks only inside the repo and never
reaches a network, which is what this repo did before the fetch layer existed.

| variable | what it does |
|---|---|
| `DEMO_PATH` | A `.dem` to use, or a folder to pick one from. Wins over everything else. |
| `DEMO_PATH_PICK` | Which file to take when `DEMO_PATH` is a folder. |
| `DEMO_CORPUS` | The corpus root, replacing `<repo-root>/demos`. |
| `DEMO_SOURCES` | `;`-separated places to fetch an absent manifest demo from, tried in order. A directory, a `file://` URI, or a FileBrowser instance as `https://host/SourceName`. Unset means no fetching. |
| `DEMO_SOURCES_TOKEN` | The API token for the `https://` entries in `DEMO_SOURCES`, sent as a bearer header. |
| `DEMO_CORPUS_CACHE` | Where a fetched copy lands. Defaults to a per-user cache directory. |

`<corpus-root>` is `DEMO_CORPUS` when set, else `<repo-root>/demos`. A `DEMO_CORPUS` that is set but
is not a directory resolves to NOTHING and never falls back, so a misconfigured run skips loudly
instead of reporting as though it had the corpus.

The separator in `DEMO_SOURCES` is `;` and not the platform path separator, which is `:` on Unix and
would split a `file://` URI in half. Anything after the source name in an HTTP entry prefixes every
path that source probes, so `https://host/SourceName/TestingDemos` partitions a test corpus off from
the rest of a source shared with other content. An entry whose scheme is neither a path, `file://`
nor `http(s)://` fails the run at startup rather than being skipped: whoever configured it meant
those tests to run.

An HTTP entry is `https://` unless the host is loopback, and its token comes from
`DEMO_SOURCES_TOKEN`, never from the URI. A URI carrying user info or a query string is refused, and
construction errors report only scheme, host and path. All of that is because a source's description
is printed into skip messages verbatim, and a URI also reaches proxy logs. The token needs no more
than read and download on the one source.

An HTTP source probes two paths per demo and never lists a directory: `<prefix>/<dir>/<file>` then
`<prefix>/<file>`, the same pair a directory source tries. A listing could report a present demo as
absent when an index is stale, which is the one thing a source may never do. So the demos have to
sit on the instance under the manifest's own names.

A credential or configuration problem FAILS the run instead of skipping it. Only a 404 on both
candidates counts as "this source does not hold it", and a source that answers anything else, or
that cannot be reached at all, stops the run and says why. A green lane full of skips is the outcome
this layer exists to prevent.

`DEMO_CORPUS_CACHE` defaults to a per-user cache directory rather than anywhere under the repo, so
every checkout on a machine shares one copy and no write path of the layer points into `demos/`. A
cached demo lives at `<cache-root>/<sha256>/<file>`, so the cache is content addressed and two demos
cannot collide. The cache is checked before any source, which is why a warm machine never refetches.
Nothing evicts: reclaiming space is `rm -rf` on the cache root by hand.

The manifest is `corpus.manifest.json`, embedded in `DemoViewer.NET.TestSupport` and keyed by SHA-256
of each file's bytes. It is the machine-readable half of this file, and the two have to be edited
together. A fetched demo is checked against it on length and then hash, and a mismatch fails the run
rather than being cached.

A folder `DEMO_PATH` (the Steam replays directory, read in place) resolves to ONE demo for the whole
run: `DEMO_PATH_PICK=<filename>` when set, else the reference demo when the folder holds it, else the
`.dem` whose name sorts first by ordinal comparison, top level only. A `DEMO_PATH_PICK` that is not in
the folder resolves to nothing rather than to the first file, and the skip reason says so. The
pinned-name lookups (`RequireDemo("furia-vs-vitality-m1-mirage.dem")` and the candidate chains that
fall back through several names) do not look inside a folder `DEMO_PATH`, for the reason in the
`_410.dem` section below.

Absent demos are a SKIP, not a failure: with an empty corpus the App suite still passes and simply
runs about 84 fewer tests (the count when this file was written; it drifts with the suite). CI has
never had the corpus, which is why CI is green regardless.

Layout as of 2026-10-22: 21 `.dem` files (6 + 5 + 7 + 3 across the sections below) and 5 `.bz2`
archives, about 6.8 GB of `.dem` in total.

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

## `match730_..._410.dem`: staged here, and deliberately absent from the manifest

`match730_003826256877184877003_0981591541_410.dem` is the sixth loose demo in `demos/`, and its
`.dem.info` is the only tracked file under `demos/` besides this one. Four App tests resolve the
demo by name (`LibraryScoreTests`, `EntityStoreFilterEquivalenceTests`, `VisibilityAnalyzerTests`,
`ZFloorValidationProbe`) and `MatchInfoReaderTests` reads the committed `.info` beside it, so it is
a working part of the local corpus rather than something to keep out.

It has no `corpus.manifest.json` entry, so a configured `DEMO_SOURCES` fetch never stages it into a
checkout that lacks it, and `DemoCorpusTests` asserts that absence. The reason recorded for the
exclusion was `Nuke_TwoFloors_MatchesGolden`, a de_nuke golden that listed this demo as a candidate
ahead of the committed `assets/tour/sample-de_nuke.dem` its capture was taken against, and failed on
a 14.5% pixel difference when this one won. That test is gone and no surviving test resolves a demo
by name to compare it against a committed golden, so both the exclusion and the rule below rest on a
capture that no longer exists.

The rule below is that `DemoTestHelper.FindDemoPath(name)` never looks inside a folder-shaped
`DEMO_PATH`: the Steam replays folder holds demos whose names match a pinned lookup, and letting that
folder answer one would silently swap which demo a named test parsed. It was introduced for the same
deleted capture, and it is cited from `DemoTestHelper`'s own summary.

## How the corpus was lost, so it does not happen twice

`demos/` was made reachable from a git worktree with a Windows directory junction, and
`git worktree remove --force` was then run. The removal followed the junction and deleted the
TARGET's contents rather than the link.

**The supported way to point a second checkout at an existing corpus is the `DEMO_CORPUS`
environment variable**, which replaces `<repo-root>/demos` everywhere `DemoTestHelper` looks.
`DEMO_PATH` is a different lever: one `.dem`, or a folder that resolves to one demo for the whole
run. Never link `demos/`.
