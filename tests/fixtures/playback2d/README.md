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

**`pending: true` means "this entry's inputs have not all landed yet."** `dv2d golden verify` and
`dv2d fixture verify` **skip** such an entry rather than failing it, and an entry may be listed with
no scene file at all while it is pending. That is what lets a later phase register the fixture it is
going to author. Three entries are pending today: `nuke-multilevel` (B1's parity pair, which needs the
pane/level model), `annotated-mirage-b` (B2's annotation document) and `full-scene-budget` (B1's
worst-case 1080p bench scene). Clearing the flag is the phase's job.

`map_version` is load-bearing: `golden verify` **refuses** (exit 4, `stale-assets`) when the bundle on
disk reports a different CRC, rather than diffing a render against re-baked radar art.

## Two families

**Synthetic** — hand-authored JSON, no demo required. These drive the direct-execution smoke tests
and run everywhere, CI included. They are edited like any other source file, and each has a committed
`goldens/cpu/<name>@640x360.png` produced by `SceneGoldenTests` — a CPU-provider render of B0's own
loop (palette, transform, compositor, fixture format). Those are **not** the B1 parity corpus.

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
| `nuke-single-upper.scene.json` | Captured by `dv2d fixture capture` from the committed `assets/tour/sample-de_nuke.dem` at tick 9694. Re-capture with `--level` once B3 ships the level pick. |
| `nuke-multilevel.scene.json` | **Pending.** B1's parity pair: its golden came from the pre-v2 control's two-pane stacked layout (camera viewport 900×450 inside a 900×900 image), which needs B1's `PaneSet` and B3's `MapSpace` to reproduce. |

Note on tolerance. Entries default to `GoldenTolerance.DefaultPerceptual`: CPU rasterisation of
anti-aliased edges can differ by a least-significant bit between SIMD paths, and headless Skia text
metrics differ across operating systems (design risk 1 — text differences are reviewed, not
auto-failed). An entry can opt into `"tolerance": "byte-exact"` in the manifest once B1's embedded
typeface makes that safe.

## Regenerating

Goldens are regenerated only on a **deliberate** visual change, and the new images must be eyeballed
before they are committed — a golden that is silently rewritten is a test that no longer tests.

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
