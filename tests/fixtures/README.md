# tests/fixtures/: reference data for parity tests

Per-demo subdirectories named after the demo's filename (without `.dem`),
plus a couple of top-level fixture files. Every JSON here is reference data
that one or more tests assert against.

## Layout

```
tests/fixtures/
├── <demo-id>/
│   ├── ours.golden.json               Stat snapshot produced by AnalysisBench
│   ├── expected.golden.json           Curated reference (see "Reliability posture" below)
│   ├── aim.expected.golden.json       Aim-board pin (see "The aim board" below)
│   └── entity-fields.ours.golden.json Per-tick entity-field snapshot (FuriaMirage only)
```

## Reliability posture: what each file means

The two stat-side providers (`ours`, `expected`) are NOT equally
trustworthy. Tests in `StatParityTests` treat them differently:

| Provider | Source | Trust level today |
|---|---|---|
| `ours` | `AnalysisBench --suite` reads the demo through our parser/analyzer | Reflects what our code currently produces. NOT a reference; it's the thing being measured. |
| `expected` | Hand-curated values | **Not yet reliable.** Today's files were seeded from a run that was cross-checked against a second source, NOT from a human watching the demo. Function: parser-regression tripwire only. |

## Why `expected` exists if it's not yet hand-verified

The intent is for `expected.golden.json` to become the load-bearing ground
truth. That requires actual hand-verification.

Today's seed files carry only the stats a cross-check agreed on, which is a
narrow set of objective per-match counts (kills, deaths, assists, multi-kills,
rounds survived, trade kills, round wins). They serve two interim purposes:

1. **Parser regression detection:** if ours produces a different value
   for a stat the seed has, the test fails. That catches our parser
   drifting from its own past output, even without a human in the loop.
2. **Infrastructure proof:** the schema, the loader, the parity-test
   shape all exist and work. Replacing seeded values with hand-verified
   values is a content swap, no code change required.

When hand-verification work happens, the file's `provider_version` field
will move from `null` to something like `"hand-verified-2026-XX-XX-by-NAME"`.

## The aim board

`aim.expected.golden.json` pins the columns produced by `rules/aim_rating.rules.yaml`
(accuracy, the counter-strafing triple, spray control, crosshair placement).
It sits in the same posture as `expected.golden.json`: only the pin is
committed, `ours` is derived by running the ruleset over the demo every time,
and the comparison is at zero tolerance. The harness is
`src/App/DemoViewer.NET.App.Tests/AimParity/`.

Two things about it are specific to aim.

**The pin records a machine capability, not just a demo.** Its
`provider_version` carries `visibility=on` or `visibility=off`. Tier 3 columns
(preaim, spotted accuracy, spray accuracy) need a baked `collision.tris` for
the map and read 0.0 rather than blank without one, so comparing a run without
geometry against a pin taken with it would produce a wall of divergences on
columns that are simply not being measured. The test skips on a mismatch
rather than reporting it as drift. Of the five benchmark maps, `assets/` ships
bakes for nuke, dust2 and ancient; mirage and inferno have none.

**Spray control has no external reference at all.** No public tool publishes a
spray-residual column, so the `Spray` column has nothing to be compared
against, and what `SprayControlOracle` gives it is narrower than
validation. The oracle is an independent hand-written fold of the spray
SEGMENTATION rule (`SprayControlOracleTests` pins every branch of it), and
of two residuals over that segmentation: the fired-arm rule (view angle plus
recoil scale times punch, as pitch and yaw components) and the landed-arm
rule the shipped column uses (the 3D angle between a bullet's raw `ShootAng`
and the run's first landed bullet, `MeanAngleError`).
`SprayControlOracleRealDemoTests` asserts the oracle's invariants and that
the shipped column is WIRED (a measured `SprayN` with a non-zero `Spray` on a
demo that carries `bullet_damage`), and prints the oracle's angle beside the
shipped one. It does NOT assert the shipped mean against the oracle's: the
engine segments runs on the fired stream and the oracle on the landed one,
so the two populations differ wherever a miss sits inside a spray.

## Refresh procedures

| File | Refresh command |
|---|---|
| `ours.golden.json` | `dotnet run -c Release --project tools/AnalysisBench -- --suite` |
| `expected.golden.json` | **Not auto-refreshable.** Manual edit when hand-verifying. |
| `aim.expected.golden.json` | `PIN_EXPECTED_AIM=1` with the demo present, running `AimStatParityTests`. Deliberate, reviewed re-pin only. |
| `entity-fields.ours.golden.json` | `dotnet run --project tools/DemoViewer.NET.EntityFieldDiff -- <demo> --write-snapshot` (requires the gitignored EntityFieldDiff tool + sibling demofile-net repo). |

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
- **Per-stat tolerances.** Lives in `StatParityTests.Tolerances`.
- **Cross-provider mappings.** Each provider's converter (in
  `src/Analysis/.../GoldenStats/`) owns its own mapping from raw input
  to the canonical schema.
