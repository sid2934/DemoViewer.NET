# `dv2d` — the headless Playback2D tool

`dv2d` renders, benchmarks and gates the Playback2D v2 scene pipeline **without launching the app**.
It references `DemoViewer.NET.Playback2D.Pipeline` and nothing from `src/App/*`; no Avalonia assembly
is loaded at any point, and that is asserted by test (`NoAvaloniaArchitectureTests`) on every CI run.

Design authority: [`design.md`](design.md) §4, §5.7, §5.8, §6, §7.7, §9, §11.
Plan: [`plans/C1-cli.md`](plans/C1-cli.md).

```
dotnet build tools/DemoViewer.NET.Playback2D.Cli -c Release
scripts/dv2d.sh render --fixture tests/fixtures/playback2d/scenes/duel-mirage-b.scene.json --out /tmp/f.png
```

The built executable is `artifacts/bin/DemoViewer.NET.Playback2D.Cli/release/dv2d[.exe]`.

---

## The iteration loop

The point of the tool. Edit a layer, re-render a fixture, look — no app, no demo parse, no window:

```sh
scripts/dv2d.sh render --fixture tests/fixtures/playback2d/scenes/duel-mirage-b.scene.json --out /tmp/f.png
```

That is **well under a second** on a warm run and is asserted as such
(`RenderFixtureTests.WarmRender_IsUnderOneSecond`). `render --demo` is the other half — it renders any
tick of a real demo — but it pays a full parse first, so it is not the loop you sit in.

---

## Commands

### `dv2d render`

```
dv2d render   --fixture <path> | --demo <path> (--tick N | --frame N)
              [--out <png>]              default ./dv2d-render.png
              [--size WxH]               default: the fixture's size, else 1920x1080
              [--layers a,b] [--exclude-layers a,b]
              [--camera fit-map|fit-alive|follow:<steamId>|fixed:<x>,<y>,<zoom>]
              [--layout stacked|single] [--level <levelId>]
              [--assets <dir>] [--no-radar]
              [--cpu | --gpu] [--strict-backend]
              [--json] [--quiet] [--diag-assemblies]
```

- `--tick N` resolves by **binary search** over `ServerTick`. When several frames share a tick the
  **first** is chosen; when no frame carries the tick exactly, the last frame before it is. The
  resolved `frame_index` is always echoed in `--json`, so the mapping is never a guess.
- `--layers` takes stable `ISceneLayer.Id` values, bare (`markers`) or prefixed
  (`playback2d.markers`). **An unknown id is exit 1**, not a silent no-op — a typo in a CI invocation
  must fail loudly. `dv2d fixture list --json` and the error message both name the known set.
- `--camera` is a single-frame framing. Omit it and the fixture's own camera is used, re-fitted to the
  requested viewport (so `--size` reframes rather than crops).
- `--diag-assemblies` writes the process's loaded-assembly list to stderr after the render. It exists
  for the architecture test and for support triage, and is documented rather than hidden.

### `dv2d golden verify | update`

```
dv2d golden   verify | update
              [--corpus <dir>] [--name <fixture>] [--cpu | --gpu]
              [--tolerance byte-exact|perceptual] [--diff-dir <dir>] [--json]
```

Renders every corpus entry and compares it with its committed golden. `verify` exits **4** on any
mismatch or missing golden and writes `<name>.actual.png` plus `<name>.diff.png` into `--diff-dir`
(default `artifacts/playback2d-goldens/`) so a CI artifact upload carries the evidence.

`update` rewrites the PNGs. **Look at them before committing** — a golden that is silently rewritten
is a test that no longer tests.

`--size` is deliberately **not** accepted here: a golden is named for its size, so an override would
compare one image against a differently-named other.

Entries marked `"pending": true` in the manifest are **skipped**, not failed. That is what lets a
later phase register the fixture it will author before it can render it.

### `dv2d bench`

```
dv2d bench    (--fixture <path> | --name <corpusEntry> | --demo <path> [--from N])
              [--frames N]               default 2000
              [--warmup N]               default 128
              [--size WxH] [--layers ...] [--assets <dir>]
              [--cpu | --gpu]
              [--gate] [--budget-scale X] [--budget-p99-ms X]
              [--budget-advance-p99-ms X] [--budget-bytes-per-frame N]
              [--report-dir <dir>] [--json]
```

Times `Advance` and `Render` separately (Core is banned from wall-clock APIs, design §5.1 — the
harness measures from outside), reports p50/p95/p99/max/mean and allocated bytes per frame, and with
`--gate` exits **4** listing every violation.

Budgets come from the manifest per entry, seeded from design §6 (render p99 ≤ 8 ms, advance p99 ≤
2 ms, 0 bytes/frame). `--budget-scale` (env `DV2D_BUDGET_SCALE`) multiplies the **time** budgets so a
slow shared runner can gate without rewriting the design's numbers; **the allocation budget is never
scaled** — 0 is 0 everywhere.

Percentiles are nearest-rank on the sorted samples, so a reported p99 is always a real observed frame.

### `dv2d fixture capture | list | verify`

```
dv2d fixture  capture --demo <path> (--tick N | --frame N) --name <id>
                      [--corpus <dir>] [--size WxH] [--camera ...]
                      [--annotations <path>] [--layers ...] [--json]
              list   [--corpus <dir>] [--json]
              verify [--corpus <dir>] [--json]       # schema round-trip, no rendering
```

`capture` replays a **private** tracker to the requested tick, serializes the built scene into
`scenes/<name>.scene.json`, and registers it in `manifest.json`. After that the fixture is demo-free,
which is what lets the whole corpus run in CI. The manifest entry it writes carries a default budget
and a generated note — edit those by hand afterwards; the manifest is a reviewed file.

`verify` round-trips every scene through the serializer and fails (exit 4) on anything that does not
come back byte-identically — a fixture that reads but does not write back identically is a fixture
whose next `capture` would silently drop data.

### `dv2d export`

**Deferred to B4.** The verb exists and is documented, and exits **6** naming what is missing
(`SceneExportSession`, `FfmpegFrameSink`, `ManagedGifSink`). A private encoder path in the CLI was
explicitly rejected: it would be the thing B4 has to delete, and until then it would produce video
that does not match the app's export. `TrackerFrameSource` — the frame source that session consumes —
already ships, in `DemoViewer.NET.Playback2D.Pipeline.Frames`.

---

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | usage / argument error, including an unknown option or layer id |
| 2 | a required input is missing (demo, fixture, corpus, asset root, ffmpeg) |
| 3 | runtime failure (decode / render / encode threw) |
| **4** | **gate failure** — a golden mismatched or a budget was exceeded |
| 5 | cancelled (Ctrl+C) |
| 6 | the requested environment is unavailable (`--gpu` under `--strict-backend`, `--layout single` before B3, `export` before B4) |

**4 is the only code CI should read as "the change is bad."** Everything else means "the run is
broken", and conflating the two is how a golden regression becomes a green build.

---

## Asset resolution

Map art is the baked output of `tools/DemoViewer.NET.AssetBaker`: an `assets/` root holding one
subdirectory per map with `bundle.json` plus its radar PNGs. The ladder is

1. `--assets <dir>`
2. `DV2D_ASSETS`
3. a walk-up probe for `assets/` from the process base directory, then the working directory

and `--no-radar` short-circuits the whole thing. The winning rung is reported as `assets_source`
(`flag` / `env` / `probe` / `disabled` / `not-found`) in every `--json` payload, because a golden
failure caused by a different asset root has to be diagnosable from a CI log alone. An explicit
`--assets`/`DV2D_ASSETS` that does not exist is exit 2 — never a silent fall-through to the probe.

A fixture records the `mapVersion` CRC it was captured against. `golden verify` **refuses** (exit 4,
status `stale-assets`) when the bundle on disk reports a different one, rather than diffing a render
against re-baked radar art.

## Render backend

Precedence: explicit argument → `--cpu`/`--gpu` → `DV2D_RENDER_BACKEND` (`auto|cpu|gpu`) →
auto-probe. C2 owns `GpuSurfaceProvider`; until it lands, `--gpu` degrades to CPU **with a printed
reason**, or fails with exit 6 under `--strict-backend`.

## SkiaSharp version policy

`SkiaSharp` is pinned to exactly what `Avalonia.Skia` resolves (2.88.9 today), because the app loads
Core in the same process as Avalonia's Skia and two `libSkiaSharp` natives in one process is a hard
crash. `dv2d` additionally pulls `SkiaSharp.NativeAssets.{Win32,Linux,macOS}`, since it has no
Avalonia to bring the natives along. Verify before changing anything:

```sh
dotnet list src/App/DemoViewer.NET.Desktop package --include-transitive | grep -i skiasharp
```

Bump only in lockstep with the Avalonia block in `Directory.Packages.props`.

---

## JSON output (`--json`, `schema_version: 1`)

With `--json`, **stdout carries exactly one JSON object** and every human line moves to stderr, so
`dv2d … --json | jq` works without a filter and a CI log still shows the prose. Keys are
`snake_case`. Progress events (export) are newline-delimited JSON on stderr.

```jsonc
// render
{"schema_version":1,"command":"render","ok":true,"out":"f.png","width":1920,"height":1080,
 "backend":"CpuRaster","backend_requested":"auto","assets_root":"/repo/assets","assets_source":"probe",
 "source":{"kind":"fixture","name":"duel-mirage-b"},
 "map":"de_mirage","map_version":"1efb9403","tick":21120,"frame_index":21152,
 "layers":["playback2d.debuggrid"],"png_sha256":"…","png_bytes":4576,
 "parse_ms":0,"elapsed_ms":103.1}

// bench
{"schema_version":1,"command":"bench","ok":true,"backend":"CpuRaster",
 "source":{"kind":"fixture","name":"duel-mirage-b"},
 "frames":2000,"warmup":128,"size":{"width":640,"height":360},"layers":[…],
 "advance_ms":{"p50":0,"p95":0.0001,"p99":0.0001,"max":0.0002,"mean":0},
 "render_ms":{"p50":3.4,"p95":5.9,"p99":7.2,"max":11.8,"mean":4.1},
 "frame_ms":{…},
 "allocated_bytes_per_frame":0,"gc":{"gen0":0,"gen1":0,"gen2":0},
 "budget":{"scale":1.0,"render_p99_ms":8.0,"advance_p99_ms":2.0,"bytes_per_frame":0},
 "gate":{"enabled":true,"passed":true,"violations":[]},
 "metadata":{"timestamp":"…","git_commit":"…","machine":{…}}}

// golden verify
{"schema_version":1,"command":"golden","action":"verify","ok":false,"backend":"CpuRaster",
 "corpus":"/repo/tests/fixtures/playback2d","tolerance":{"mode":"per-entry"},
 "counts":{"total":10,"matched":6,"mismatched":1,"missing":0,"skipped":3,"updated":0},
 "results":[{"name":"duel-mirage-b","status":"mismatch","mismatched_fraction":0.013,
   "max_channel_delta":37,"ssim":0.981,"tolerance":"perceptual",
   "golden":"tests/fixtures/playback2d/goldens/cpu/duel-mirage-b@640x360.png",
   "actual":"artifacts/playback2d-goldens/duel-mirage-b.actual.png",
   "diff":"artifacts/playback2d-goldens/duel-mirage-b.diff.png"}]}

// fixture verify / list / capture — same envelope, "command":"fixture" plus an "action"
```

`render`'s `png_sha256` is the determinism handle: it is identical across two runs in one process and
across two processes, and `RenderDeterminismTests` asserts both.

---

## CI recipes

Both live in the `playback2d-tests` job of `.github/workflows/ci.yml` (there is deliberately no second
Playback2D test job).

```yaml
- name: Golden images
  run: >
    dotnet run -c Release --project tools/DemoViewer.NET.Playback2D.Cli --
    golden verify --cpu --json --diff-dir artifacts/playback2d-goldens

- name: Frame-budget gate
  env:
    DV2D_BUDGET_SCALE: '2.0'
  run: >
    dotnet run -c Release --project tools/DemoViewer.NET.Playback2D.Cli --
    bench --name duel-mirage-b --frames 512 --cpu --gate --json
    --budget-bytes-per-frame 4096
    --report-dir artifacts/bench-reports
```

`--budget-bytes-per-frame 4096` is a temporary ceiling: today's smoke layer allocates its `SKPaint`s
inside `Render`. **The PR that closes B1's allocation cleanup drops that flag** (returning the gate to
the manifest's 0) and drops the `Category!=Budget` filter from the test step, which enables
`BenchAllocationTests`.

The runner needs `libfontconfig1` (`SkiaSharp.NativeAssets.Linux` links fontconfig); the job already
installs it.

---

## Current limitations

These are phase boundaries, not bugs. Each is an honest failure rather than a silent degradation.

| Flag / feature | State | Owner |
|---|---|---|
| `--layout single`, `--level` | exit 6 — needs `MapSpace` / `ILevelLayoutPolicy` | B1, B3 |
| `--gpu` | degrades to CPU with a reason; exit 6 under `--strict-backend` | C2 |
| `export` | exit 6 | B4 |
| The layer set | one smoke layer (`playback2d.debuggrid`); `SceneLayerCatalog` is the single place the real seven register | B1 |
| Byte-exact goldens | the corpus defaults to `perceptual`; CPU rasterisation of anti-aliased edges can differ by a least-significant bit between SIMD paths | B1 (embedded typeface), C2 (SSIM) |
