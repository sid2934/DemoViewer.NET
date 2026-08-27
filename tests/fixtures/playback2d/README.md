# Playback2D v2 fixture corpus

A **fixture** is a serialized `Scene2DFrame` plus the camera, size and clock to render it at
(`DemoViewer.NET.Playback2D.Pipeline.SceneFixture`). Fixtures are the design-iteration loop *and*
the golden-test corpus — deliberately the same artifact, so a scene you tweak by hand is a scene the
regression suite covers.

## Layout

```
scenes/<name>.scene.json          the fixture
goldens/cpu/<name>@<w>x<h>.png    the CPU-provider golden for it
goldens/gpu/<name>@<w>x<h>.png    the GPU parity image (C2)
annotations/<name>.dvann.json     the annotation sidecar (B2)
manifest.json                     the corpus index (C1 owns this file)
```

This is the canonical layout for every phase. There is no `tests/goldens/`, no `…/golden/`, and no
`…/goldens/export/`.

## `manifest.json` — the index

Every fixture the tooling knows about is listed here. `dv2d golden`, `dv2d bench` and `dv2d fixture`
all read it, and so do the direct-execution suites, so "which fixtures exist" has exactly one answer.

```jsonc
{
  "schema_version": 1,
  "default_budget": { "render_p99_ms": 8.0, "advance_p99_ms": 2.0, "bytes_per_frame": 0 },
  "entries": [{
    "name": "duel-mirage-b",
    "scene": "scenes/duel-mirage-b.scene.json",   // corpus-relative
    "size": { "width": 640, "height": 360 },      // the golden is named <name>@<w>x<h>.png
    "map": "de_mirage",
    "map_version": "1efb9403",                    // the bundle.json CRC this was captured against
    "tolerance": "perceptual",                    // or "byte-exact"
    "layers": null,                               // null = every registered layer
    "budget": { "render_p99_ms": 8.0, "advance_p99_ms": 2.0, "bytes_per_frame": 0 },
    "pending": false,
    "notes": "…"
  }]
}
```

**`pending: true` means "`dv2d golden verify` cannot judge this entry."** It **skips** such an entry
rather than failing it, and an entry may be listed with no scene file at all while it is pending —
which is what lets a later phase register the fixture it is going to author.

A stale flag therefore hides: pending is skipped, never failed, so nothing goes red when the phase it
was waiting on ships. Four of ten entries were pending at one point, three of them naming owners (B1,
B2, dv2d) that had all landed — including `annotated-mirage-b`, whose scene file did not exist, so
**no golden anywhere covered burned-in annotations**. All three are cleared. The three that remain
pending carry a reason in the note that is not "waiting on a phase":

| Entry | Why it cannot be verified by `dv2d` |
|---|---|
| `nuke-multilevel` | its golden is the **pre-v2 control's** own capture, under the LIGHT palette headless Avalonia resolves, gated by `GoldenParityTests` against a delta *distribution*. `dv2d` renders Dark and compares perceptually; it can express neither. |
| `nuke-multilevel-upper` | `SingleLayout` with the top floor active. `dv2d` refuses `--layout single` because it has no way to name a level id. Written by `LevelGoldenTests`. |
| `nuke-multilevel-noradar` | nav floors bound, radar art not. `dv2d --no-radar` disables the whole asset root, floors included, so it derives a different level set. There is no flag for "floors yes, art no". Written by `LevelGoldenTests`. |

Every one of those still has a live gate — it is simply not this one. A pending note that does not say
"PENDING" and why fails `GoldenCorpusTests.EveryPendingEntry_ExplainsItselfInItsNote`.

`map_version` is load-bearing: `golden verify` **refuses** (exit 4, `stale-assets`) when the bundle on
disk reports a different CRC, rather than diffing a render against re-baked radar art.

## Two families

**Synthetic** — hand-authored JSON, no demo required. These drive the direct-execution smoke tests
and run everywhere, CI included. They are edited like any other source file, and each has a committed
`goldens/cpu/<name>@640x360.png`.

Those PNGs have **two readers**: `SceneGoldenTests` in the Playback2D suite and `dv2d golden verify`
in the CLI lane. Both render through `SceneLayerCatalog.CreateSceneStack` + `HeadlessSceneRenderer`
with the camera pinned — statement for statement the same path — so the two cannot disagree about what
the file should contain. They previously agreed by accident: `SceneGoldenTests` drew a single
`DebugGridLayer`, and the catalog registered that same grid and nothing else. They are **not** the
pre-v2 parity corpus.

| Fixture | What it is for |
|---|---|
| `synthetic-empty.scene.json` | The degenerate case: no markers, no utility, nothing planted. |
| `synthetic-tenplayers.scene.json` | Ten markers spread over ±2000u, both teams, one `Dead` and one `Blinded` ring. The exit-criterion render test uses this one. |
| `synthetic-utility.scene.json` | Two smokes, six fire cells, two 40-point trails, and a bomb at 0.42 detonation with a defuse in progress. |

**Map-anchored, hand-authored** — same idea, but placed on a real map's coordinates and keyed to that
map's baked bundle, so the radar layers and the `map_version` staleness check are exercised. No demo
required; these are edited by hand.

| Fixture | What it is for |
|---|---|
| `fitmap-mirage-eco.scene.json` | Markers only, both full teams, framed on the whole map — the fit-map camera baseline. |
| `duel-mirage-b.scene.json` | The layer matrix: four ring states, a smoke, two trails, a kill row, two vision cones and a sightline. **The CI budget gate benches this one.** |
| `bomb-planted-inferno.scene.json` | A ticking bomb mid-defuse with fire cells and a populated kill feed — the bomb-ring and clock-HUD fixture. |

**Demo-derived** — a fixture and its golden PNG captured from a real demo, so the JSON and the image
describe the same world state.

| Fixture | What it is for |
|---|---|
| `nuke-single-upper.scene.json` | Captured by `dv2d fixture capture` from the committed `assets/tour/sample-de_nuke.dem` at tick 9694. Despite the name it is a **stacked** render — `dv2d` cannot select a floor — so the true single-floor picture is `nuke-multilevel-upper`, below. |
| `nuke-multilevel.scene.json` | **Pending, permanently as things stand.** The pre-v2 parity pair: its golden came from the control's own `DrawingContext` under the Light palette, and `GoldenParityTests` judges it on a delta distribution. Two further goldens are rendered *from this one scene* by `LevelGoldenTests` and listed under their own names: `nuke-multilevel-upper` (SingleLayout, top floor) and `nuke-multilevel-noradar` (floors bound, no radar art). |

**Pre-v2 captures** — `prev2-*`, written by `Playback2DGoldenCaptureTests` from a real demo through
headless Avalonia. They exist only on a machine with the relevant demo staged and are **not** listed
in the manifest, so `GoldenCorpusTests` exempts the prefix. The prefix exists because two of these
captures used to be named `duel-mirage-b` and `fitmap-mirage-eco` — the names of two hand-authored
640x360 fixtures — and overwrote both scene files on any machine that had the demos.

`annotations/<name>.dvann.json` is picked up **by convention**, not by a manifest field: if a sidecar
exists beside the corpus under that name, `golden` and `bench` load it through the production
`AnnotationStore` and register `playback2d.annotations` (the entry must also name the id in its
`layers` array). One entry uses it — `annotated-mirage-b` — and it is the only golden anywhere that
covers burned-in ink. `dv2d render --ink <path>` is the same thing for a one-off.

Note on tolerance. Entries default to `GoldenTolerance.DefaultPerceptual`: CPU rasterisation of
anti-aliased edges can differ by a least-significant bit between SIMD paths, and headless Skia text
metrics differ across operating systems (design risk 1 — text differences are reviewed, not
auto-failed). An entry can opt into `"tolerance": "byte-exact"` in the manifest once B1's embedded
typeface makes that safe.

## Regenerating

Goldens are regenerated only on a **deliberate** visual change, and the new images must be eyeballed
before they are committed — a golden that is silently rewritten is a test that no longer tests.

`PB2D_GOLDEN_UPDATE=1` rewrites an **existing** golden as well as filling in a missing one. It used to
do only the latter, which left `scripts/update-playback2d-goldens.sh` unable to re-baseline anything —
a deliberate visual change needed an undocumented `rm` first. `dv2d golden update` always overwrote;
the three direct-execution suites now agree with it. The same variable is what gates the *scene*
writes in `Playback2DGoldenCaptureTests`, which is new: that harness rewrote its fixtures on every
run, and the tour demo ships in every checkout, so any App-suite run re-authored
`scenes/nuke-multilevel.scene.json` — the input to `GoldenParityTests` and `LevelGoldenTests`.

```bash
scripts/dv2d.sh golden update                 # every entry, through the dv2d render path
scripts/dv2d.sh golden update --name duel-mirage-b
scripts/dv2d.sh golden verify                 # exit 4 on any mismatch, diffs in artifacts/
```

The B0/B1 pairs captured from a demo through the App's own harness have their own script:

```bash
scripts/update-playback2d-goldens.sh          # captures the demo-derived pairs
```

which is a thin wrapper over:

```bash
PB2D_GOLDEN_UPDATE=1 dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release \
  -- --treenode-filter "/*/*/Playback2DGoldenCaptureTests/*"
```

Without `PB2D_GOLDEN_UPDATE=1` a missing golden is a **failure**, not a silent write.

## Authoring a new fixture from a demo

```bash
scripts/dv2d.sh fixture capture --demo assets/tour/sample-de_nuke.dem --tick 9694 \
  --name my-fixture --size 640x360 --camera fit-alive
scripts/dv2d.sh golden update --name my-fixture
```

`capture` writes the scene and registers a manifest entry with a default budget and a generated note.
Edit those by hand — `manifest.json` is a reviewed file, not a generated one.

## Tolerant reader

`SceneFixtureSerializer` preserves JSON members it does not recognise and re-emits them on write, so
a fixture written by a newer build survives a round trip through an older one. Adding a field to
`Scene2DFrame` without serializing it fails `SceneFixtureTests.RoundTrip_PreservesEveryFrameField`,
which walks the type by reflection.
