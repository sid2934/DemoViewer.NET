# tests/fixtures/: reference data for parity tests

Per-demo subdirectories named after the demo's filename (without `.dem`),
plus a couple of top-level fixture files. Most of these files are reference
data a test asserts against. The exception is `expected.golden.json`, which is
read only for its presence, as the next section explains.

## Layout

```
tests/fixtures/
├── <demo-id>/
│   ├── expected.golden.json           Selects this demo as a benchmark (see below)
│   └── aim.expected.golden.json       Aim-board pin (see "The aim board" below)
```

## `expected.golden.json` is a selector, and its contents are unread

Nothing parses this file. One line reads it, and only to ask whether it
exists: `LiveAimStats.BenchmarkDemoIds` enumerates the fixture directories
that carry one, and that list is the case list for `AimStatParityTests` and
`CounterStrafeAdmissionFoldTests`. Five directories carry one, so each of
those classes has five per-demo cases. Deleting a copy silently drops a demo
from both classes rather than failing anything, so treat these files as the
benchmark set's definition.

The values inside are a 2026-05 seed of objective per-match counts (kills,
deaths, assists, multi-kills, rounds survived, trade kills, round wins),
cross-checked against a second source rather than hand-verified. They were
meant to become the ground truth for a stat-side parity test. That test was
never written, and the comparer concept behind it is gone: see "What's not in
here".

The fixture directory, not the demo, defines the set, because the demos
themselves are gitignored and enumerating whatever is on the current machine
would make the case list vary by checkout.

## The aim board

`aim.expected.golden.json` pins the columns produced by `rules/aim_rating.rules.yaml`
(accuracy, the counter-strafing triple, spray control, crosshair placement).
Unlike `expected.golden.json` this one is read for its contents: only the pin
is committed, the live numbers are derived by running the ruleset over the
demo every time, and the comparison is at zero tolerance. The harness is
`src/App/DemoViewer.NET.App.Tests/AimParity/`.

Two things about it are specific to aim.

**The pin records a machine capability, not just a demo.** Its
`provider_version` carries `aim-v1;visibility=on` or `...=off`. Tier 3 columns
(preaim, spotted accuracy, spray accuracy) need the map's baked collision soup
and read 0.0 rather than blank without one, so comparing a run without geometry
against a pin taken with it would produce a wall of divergences on columns that
are simply not being measured. The test skips on a mismatch rather than
reporting it as drift. All ten maps under `assets/` now ship a soup, so the flag
reads `on` across the benchmark set; `assets/` carries it as `collision.tris.gz`
and only `DemoViewer.NET.Services.CollisionSoup` resolves that name, which is
what the aim harness calls. A map outside the shipped ten still has none.

**Spray control has no external reference at all.** No public tool publishes a
spray-residual column, so the `Spray` column has nothing to be compared
against, and what `SprayControlOracle` gives it is narrower than
validation. The oracle is an independent hand-written fold of the spray
SEGMENTATION rule (`SprayControlOracleTests` pins every branch of it), and
of one residual over that segmentation, reported three ways: the pitch and
yaw components, and the 3D angle the shipped column uses
(`MeanAngleError`). All three measure `bullet_damage.ShootAng` against the
run's first landed bullet and none of them touches the aim punch, because
`ShootAng` is `eyeAngle + AimPunch` already; the punch reaches the fold
only as the gate deciding whether a shot is measurable at all.
Reconstructing an effective aim from a raw view angle and the punch column
is the FIRED arm's job and happens engine-side.
`SprayControlOracleRealDemoTests` asserts the oracle's invariants and that
the shipped column is WIRED (a measured `SprayN` with a non-zero `Spray` on a
demo that carries `bullet_damage`), and prints the oracle's angle beside the
shipped one. It does NOT assert the shipped mean against the oracle's: the
engine segments runs on the fired stream and the oracle on the landed one,
so the two populations differ wherever a miss sits inside a spray.

## Refresh procedures

| File | Refresh command |
|---|---|
| `expected.golden.json` | **Not auto-refreshable.** Manual edit when hand-verifying. Adding or removing one changes the benchmark demo set. |
| `aim.expected.golden.json` | `PIN_EXPECTED_AIM=1` with the demo present, running `AimStatParityTests`. Deliberate, reviewed re-pin only. |

## Schema versioning

Every JSON file has a `schema_version` field. Today schemas are at v1.
Breaking changes to a schema (new required field, removed field,
renamed key) should bump the version and update the loader. The current
loaders don't enforce version compatibility yet; that's a follow-up
when a v2 actually exists.

## What's not in here

- **The demo files themselves.** `.dem` files are 200–300 MB each and
  gitignored. Provisioning them is deferred work; until then, fixture
  refreshes are a maintainer activity (the maintainer has the demos
  locally).
- **Per-stat tolerances.** There are none to keep: the aim board compares at
  zero tolerance, and there is no stat-side comparer.
- **`ours.golden.json`.** AnalysisBench used to write one per fixture on every
  run and no test ever read it, so the committed copies were two months stale
  by the time they went. Writing it is now opt-in behind `--golden`, and what
  it writes is untracked.
- **Cross-provider mappings.** The converters that map raw input to the
  canonical schema moved out with the analysis engine and now live in the
  `CS2DemoKit.Analysis` package, under `CS2DemoKit.Analysis.GoldenStats`.
