# tests/fixtures/: reference data for parity tests

Per-demo subdirectories named after the demo's filename (without `.dem`),
plus a couple of top-level fixture files. Every JSON here is reference data
that one or more tests assert against.

## Layout

```
tests/fixtures/
├── <demo-id>/
│   ├── ours.golden.json               Stat snapshot produced by AnalysisBench
│   ├── leetify.golden.json            Stat snapshot converted from Leetify API JSON
│   ├── expected.golden.json           Curated reference (see "Reliability posture" below)
│   ├── aim.expected.golden.json       Aim-board pin (see "The aim board" below)
│   └── entity-fields.ours.golden.json Per-tick entity-field snapshot (FuriaMirage only)
```

## Reliability posture: what each file means

The three stat-side providers (`ours`, `leetify`, `expected`) are NOT
equally trustworthy. Tests in `StatParityTests` treat them differently:

| Provider | Source | Trust level today |
|---|---|---|
| `ours` | `AnalysisBench --suite` reads the demo through our parser/analyzer | Reflects what our code currently produces. NOT a reference; it's the thing being measured. |
| `leetify` | Leetify's public `?include=playerStats` API response, converted via `LeetifyGoldenStatsConverter` | **The current gold standard.** When ours and Leetify disagree on a stat, the working assumption is that ours is wrong until proven otherwise. |
| `expected` | Hand-curated values | **Not yet reliable.** Today's files were seeded from ours+leetify agreement, NOT from a human watching the demo. Function: parser-regression tripwire only. |

## Why `expected` exists if it's not yet hand-verified

The intent is for `expected.golden.json` to become the load-bearing ground
truth that unblocks the oracle sunset (dropping the live Leetify API
dependency from CI). That requires actual hand-verification.

Today's seed files were written from values where `ours` and `leetify`
agreed exactly on a chosen demo. They serve two interim purposes:

1. **Parser regression detection:** if ours produces a different value
   for a stat the seed has, the test fails. That catches our parser
   drifting from its own past output, even without a human in the loop.
2. **Infrastructure proof:** the schema, the loader, the parity-test
   shape all exist and work. Replacing seeded values with hand-verified
   values is a content swap, no code change required.

When hand-verification work happens, the file's `provider_version` field
will move from `null` to something like `"hand-verified-2026-XX-XX-by-NAME"`,
and the oracle-sunset clock starts.

## The aim board

`aim.expected.golden.json` pins the columns produced by `rules/aim_rating.rules.yaml`
(accuracy, the counter-strafing triple, spray control, crosshair placement).
It sits in the same posture as `expected.golden.json`: only the pin is
committed, `ours` is derived by running the ruleset over the demo every time,
and the comparison is at zero tolerance. The harness is
`src/App/DemoViewer.NET.App.Tests/AimParity/`.

Three things about it are specific to aim.

**Leetify is a calibration reference here, not an assertion.** Their payload in
`demos/benchmarks/<demo-id>.leetify.json` carries their own values for most of
these columns, and `AimStatParityTests.OursVsLeetify_AimStatDivergenceReport`
prints ours against theirs per player. It fires only on failures no
definitional difference can excuse (a column reading zero against a reference
of hundreds, a join that failed wholesale, counts that break their own
nesting). Fitting a tolerance is the work that pin unblocks, not something the
harness assumes.

**The pin records a machine capability, not just a demo.** Its
`provider_version` carries `visibility=on` or `visibility=off`. Tier 3 columns
(preaim, spotted accuracy, spray accuracy) need a baked `collision.tris` for
the map and read 0.0 rather than blank without one, so comparing a run without
geometry against a pin taken with it would produce a wall of divergences on
columns that are simply not being measured. The test skips on a mismatch
rather than reporting it as drift. Of the five benchmark maps, `assets/` ships
bakes for nuke, dust2 and ancient; mirage and inferno have none.

**Spray control has no external reference at all.** Leetify declares
`recoilShots` and `recoilShotsHit` and leaves both `null` in every row of all
five demos (`shotsHitFoeHead` is present and zero everywhere, which for a
head-hit numerator is the same thing).
`AimStatParityTests.LeetifyReference_DeclaresAimFieldsItNeverPopulates`
asserts that, so a payload which starts carrying them is a red test rather
than a discovery nobody makes. Until then the `Spray` column has no
reference at all, and what `SprayControlOracle` gives it is narrower than
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
| `leetify.golden.json` | Same; bench writes both as a side-effect. |
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
