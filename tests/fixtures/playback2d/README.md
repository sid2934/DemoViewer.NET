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

**Demo-derived** — a fixture and its golden PNG captured in **one** headless push, so the JSON and
the image describe the same world state. That pairing is the whole point of the B1 parity gate: B1
must re-render the `.scene.json` and match the `.png` the pre-v2 control produced. Capture needs a
demo, so these cases skip when one is not staged (see `DemoTestHelper` for the search order) — and
they are **not committed yet** for exactly that reason. Stage a demo and run the script below before
B1 starts, or B1's exit criterion has nothing to compare against.

Note on tolerance. The demo-derived goldens are compared at `GoldenTolerance.DefaultPerceptual`
because the pre-v2 control draws a floor label with `FormattedText`, and headless Skia text metrics
differ across operating systems (design risk 1: text differences are reviewed, not auto-failed).

## Regenerating

Goldens are regenerated only on a **deliberate** visual change, and the new images must be eyeballed
before they are committed — a golden that is silently rewritten is a test that no longer tests.

```bash
scripts/update-playback2d-goldens.sh          # captures the demo-derived pairs
```

The script is a thin wrapper over:

```bash
PB2D_GOLDEN_UPDATE=1 dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release \
  -- --treenode-filter "/*/*/Playback2DGoldenCaptureTests/*"
```

Without `PB2D_GOLDEN_UPDATE=1` a missing golden is a **failure**, not a silent write.

## Tolerant reader

`SceneFixtureSerializer` preserves JSON members it does not recognise and re-emits them on write, so
a fixture written by a newer build survives a round trip through an older one. Adding a field to
`Scene2DFrame` without serializing it fails `SceneFixtureTests.RoundTrip_PreservesEveryFrameField`,
which walks the type by reflection.
