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
              [--cpu | --gpu | --backend <auto|cpu|gpu|angle|gl|force-gpu>]
              [--strict-backend]
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
              [--corpus <dir>] [--name <fixture>]
              [--cpu | --gpu | --backend <name>] [--strict-backend]
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
              [--cpu | --gpu | --backend <name>] [--strict-backend]
              [--gate] [--budget-scale X] [--budget-p99-ms X]
              [--budget-advance-p99-ms X] [--budget-bytes-per-frame N]
              [--report-dir <dir>] [--perf] [--json]
```

Times `Advance` and `Render` separately (Core is banned from wall-clock APIs, design §5.1 — the
harness measures from outside), reports p50/p95/p99/max/mean and allocated bytes per frame, and with
`--gate` exits **4** listing every violation.

Budgets come from the manifest per entry, seeded from design §6 (render p99 ≤ 8 ms, advance p99 ≤
2 ms, 0 bytes/frame). `--budget-scale` (env `DV2D_BUDGET_SCALE`) multiplies the **time** budgets so a
slow shared runner can gate without rewriting the design's numbers; **the allocation budget is never
scaled** — 0 is 0 everywhere.

Percentiles are nearest-rank on the sorted samples, so a reported p99 is always a real observed frame.

`--perf` adds the per-layer / per-stage breakdown — see [Performance capture](#performance-capture-perf).

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

### `dv2d probe`

```
dv2d probe    [--json] [--require-gpu] [--require-hardware] [--quiet]
```

Reports which render-surface backend this machine can provide, and why:

```
$ dv2d probe
[render] backend=Angle gpuAvailable=True reason=angle-d3d11 \
  renderer='ANGLE (NVIDIA, NVIDIA GeForce RTX 4070 Ti SUPER Direct3D11 vs_5_0 ps_5_0)' \
  vendor='Google Inc. (NVIDIA)' version='OpenGL ES 3.0 (ANGLE 2.1.27952)' probe=187ms
```

A CPU answer is **not** an error — the CPU provider is the contract baseline and the GPU is
opportunistic (design §10 risk 7) — so the command exits 0 either way. Two flags turn it into a gate:

- `--require-gpu` exits **6** when no GPU backend can be stood up.
- `--require-hardware` additionally exits 6 when the backend is a known software rasterizer (WARP,
  llvmpipe, SwiftShader). That is the distinction a throughput lane needs and a correctness lane must
  not make: a WARP run genuinely exercises the GPU code path, it just measures nothing.

`probe` asks `RenderSurfaceProviderFactory.Probe()`, **not** `Create` — it reports what the machine
can do, unfiltered by preference. Under `DV2D_RENDER_BACKEND=cpu` the payload therefore says
`"reason": "forced-cpu"` with `"forced_cpu": true`, which is the fact somebody debugging a slow CI
lane actually needs.

### `dv2d export`

A range of a demo to a video file. Argument parsing and nothing more: the flags become an
`ExportRequest`, `TrackerFrameSource` replays a **private** tracker over the demo, and
`SceneExportSession` does the rest. The full user-facing story — formats, frame rates, the ffmpeg
ladder, the GIF caps, measured speed — is in [`export.md`](export.md).

```bash
dv2d export --demo match.dem --from t12000 --to t20000 \
            --format webm --fps 60 --size 1920x1080 --out round-7.webm
```

| Flag | Default | Meaning |
|---|---|---|
| `--demo <path>` | required | The `.dem` to export |
| `--from` / `--to` | whole demo | A frame index, or a tick with a `t` prefix (`--from t12000`) |
| `--format` | `webm` | `webm` · `mp4` · `gif` |
| `--fps` | 60 (20 for gif) | Must be one the format supports — GIF is 10/20/25/50 |
| `--speed` | `1` | Playback-rate multiplier; fixes the timestep at `speed / fps` |
| `--size` | `1920x1080` | Even in both axes for `webm`/`mp4` |
| `--encoder` | `auto` | `auto` · `software` · a ladder rung's ffmpeg name — see [Encoder ladder](#encoder-ladder---encoder---quality) |
| `--quality` | `standard` | `draft` · `standard` · `best` |
| `--layers` | the seven scene layers | Bare or prefixed ids; the HUD is opt-in |
| `--hud` | off | Adds `hud.clock` and `hud.killfeed`. The clock is the exported frame's own round, score and countdown; the kill feed draws no rows here — see limitations |
| `--out` | `dv2d-export.<format>` | Output path |
| `--no-encode` | off | Render and read back every frame, encode nothing |
| `--ffmpeg-log` | off | Echo ffmpeg's stderr |
| `--perf` | off | Per-stage / per-layer breakdown — see [Performance capture](#performance-capture-perf) |

`export` has no `--camera`: it frames the map. Each pane is fitted once, on the first frame that
carries a world extent, to the map's networked bounds (falling back to the observed extent) — the
offscreen twin of the window's one-shot fit. It happens once rather than per frame, so the framing
does not creep as players wander, and a caller that supplies a camera script through the API still
overrides it.

Two mechanisms produce that one framing, and which of them does the work depends on where the range
starts. A range that starts mid-match is already past the point where `CCSGameRulesProxy` published
`m_vMinimapMins/Maxs`, so output frame 0 already carries the networked extent and every pane is
**born** fitted to it during pane reconciliation — the whole video, first frame included, is framed
correctly. A range that starts at frame 0 begins in the ticks before that entity exists: those frames
have no extent to fit, so they are drawn on the ±3000 placeholder and the panes **snap** onto the map
on the first frame that does carry one, normally frame 1. The snap happens at most once per export —
there is no way to frame a map the demo has not described yet — but it is why the very first frame of
a whole-demo export is composed differently from the rest.

`--no-encode` is the diagnostic that separates "the renderer is slow" from "libvpx is slow", and it
is what a GPU backend should be compared against — a GPU cannot make an encoder quicker.

#### Encoder ladder — `--encoder` / `--quality`

Each video format has an ordered list of encoders, best first, and `auto` takes the first rung this
machine can **actually run**:

| Format | Ladder |
|---|---|
| `webm` | `av1_nvenc` → `av1_qsv` → `av1_amf` → `libvpx-vp9` |
| `mp4` | `h264_nvenc` → `h264_qsv` → `h264_amf` → `libx264` |
| `gif` | none — the palettegen/paletteuse chain *is* the encoder |

AV1 on the WebM rungs rather than HEVC because HEVC cannot go in a WebM at all and AV1 can: a
hardware WebM export is still a `.webm`, with the same extension and the same saved defaults.

**"Actually run" means a two-frame test encode, not a listing.** `ffmpeg -encoders` describes the
*build*; whether the machine has the silicon and a working driver is a different question, and the
answers disagree constantly. On the box this was developed on, `av1_qsv`, `h264_qsv` and `av1_amf`
are all listed and all fail — `av1_amf` on the same GPU where `h264_amf` works, because that Radeon
has an H.264 encode block and no AV1 one. The probe costs one short ffmpeg per hardware rung, once
per process, and is skipped for software rungs (a listed `libvpx-vp9` is a working one).

- `--encoder auto` — walk the ladder. **Never fails for an environment reason**: a machine with no
  working hardware encoder lands on tuned software, which is a completely normal export.
- `--encoder software` — skip the hardware rungs and probe nothing. The machine-independent answer,
  for a bisect or a like-for-like comparison.
- `--encoder <name>` — taken literally. If it does not verify the export is **refused** (exit 6)
  with ffmpeg's own explanation, rather than quietly encoded with something else.

`--quality` is an intent, mapped per encoder onto that encoder's own rate and speed controls, so
`standard` means the same thing on NVENC and on libvpx even though the flags share no spelling.

The chosen rung, the reason, the exact arguments and every rejected rung are in `--json`
(`video_encoder`, `video_encoder_kind`, `video_codec`, `encoder_reason`, `encoder_arguments`,
`quality`, `encoder_probe_ms`, `encoder_attempts`) and on the human output. That matters because a
**hardware encoder is not bit-reproducible** — two runs, or two driver versions, can differ — so the
file's bytes are a function of the machine and the machine's answer is written down. The determinism
gate is unaffected: it hashes pre-encode RGBA frames, which no encoder touches.

`--perf` is what turns the single `realtime_ratio` this command reports into the five costs that
produce it — the tracker decode, the advance, the raster, the read-back, and how long the loop sat
blocked on the encoder's bounded frame channel. Pair it with `--no-encode` to see the renderer alone.

ffmpeg comes from `PATH` only here; the in-app managed download is not offered to a headless tool.
Without it, `--format gif` still works through the ImageSharp floor and the other two exit **2**.

Ctrl+C cancels: the token reaches the session, which disposes the sink, which kills ffmpeg and
deletes the partial output.

---

## Performance capture (`--perf`)

`bench` and `export` accept **`--perf`** (alias `--profile`). It decomposes the frame into stages and
per-layer rows, so "the export runs at 1.1× realtime" becomes a list of named costs. Off by default,
and free when off: the seam is one nullable field on `SceneCompositor`, which costs a predicted
branch per layer per phase and allocates nothing.

The switch it extends is the repo's existing one. `CS2DemoKit.Parser.Profiling.Enabled` —
`CS2DEMOKIT_PROFILE=1`, or the `DEMOVIEWER_PROFILE=1` spelling `docs/profiling.md` still carries — is
the single process-wide runtime profiling gate; when it is on, `dv2d` captures without being asked.
The implication is one-way: `--perf` does **not** turn that switch on, because the tracker decode is
one of the stages being timed and its own instrumentation would perturb it.

| Stage | What it measures | Where |
|---|---|---|
| `source` | `ISceneFrameSource.FrameAt` — the entity tracker's decode plus `SceneFrameBuilder` | both |
| `advance` | Level derivation, pane reconciliation, cameras, every layer's `Advance` | both |
| `render` | Clear, every layer's `Render` over every pane, surface flush | both |
| `readback` | `SKSurface.ReadPixels` into the staging buffer | export |
| `encode` | `IFrameSink.WriteAsync` — time **blocked** on the sink's capacity-4 bounded channel, i.e. how far the encoder is behind | export |

Stages partition the frame and their shares sum to 100 %. **Layer rows are nested inside `advance`
and `render`**, never additional to them, and carry the picture-cache verdict per layer: `replayed`
(hit), `recorded` (miss), `uncached` (a `Dynamic` layer, where there is no cache in the path at all —
counted apart so it does not read as a permanent cache failure).

`max_render_fps` is the uncapped render-only ceiling, `1000 / p50(render)`. `bench` never encodes and
`export --no-encode` encodes nothing, so both report it for the stack they are drawing.

> **Which command to ask about layers.** `bench` (and `render`) build through
> `SceneLayerCatalog.Create`, which in this build still knows only B0's `playback2d.debuggrid` — the
> seam C1 deviation 14 left open. `export` builds through `CreateSceneStack` and gets the real nine.
> So today **`export --no-encode --perf` is the per-layer authority**, and `bench --perf` reports a
> correct table of whatever stack it managed to build. See
> [`plans/P1-perf-instrumentation.md`](plans/P1-perf-instrumentation.md) §8.

Measured — `export --from 72000 --to 79680 --size 1280x720 --fps 60 --hud --perf` on a de_inferno
MM demo, CPU raster, libvpx-vp9 (this is the real output, not an illustration):

```
  perf 7201 frames  frame p50=14.960 p99=20.005 ms  max 66.8 fps  render-only 241.3 fps
  stage                             p50      p99   total ms   share
  source                          0.405    0.601     3220.7    3.0%
  advance                         0.014    0.037      135.1    0.1%
  render                          4.143    5.356    30277.5   27.9%
  readback                        2.151    3.739    16209.5   14.9%
  encode                          8.067   13.117    58628.9   54.0%
  layer (nested in stage)           p50      p99   total ms   share cache
  playback2d.radar (render)       3.265    4.483    24083.3   22.2% 100.0% hit (7200/7201)
  playback2d.markers (render)     0.232    0.349     1531.1    1.4% uncached
  …
  slowest: encode 54.0%, render 27.9%, playback2d.radar (render) 22.2%, readback 14.9%, source 3.0%
```

Read that way round it is unambiguous: the frame is libvpx plus one radar `DrawImage` plus a
read-back, and the entity decode everyone suspects (`source`) is 3 % of it. The full analysis,
including the ablation that checks the per-layer column against reality, is in
[`plans/P1-perf-instrumentation.md`](plans/P1-perf-instrumentation.md) §7.

Capture itself allocates nothing per frame in steady state — the ring buffers are filled during the
warmup — which is asserted by `ScenePerfRecorderTests` alongside the 0 B assertion for the detached
default path. Design and rationale: [`plans/P1-perf-instrumentation.md`](plans/P1-perf-instrumentation.md).

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
| 6 | the requested environment is unavailable (`--gpu` under `--strict-backend`, `probe --require-gpu` with no GPU, `--layout single` before B3) |

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

Precedence (design §5.8, plans/C2-gpu-provider.md §2.5):

1. `--cpu` / `--gpu` / `--backend <auto|cpu|gpu|angle|gl|force-gpu>` — mutually exclusive; `angle`
   and `gl` are accepted aliases for `gpu` (which GL stack gets used is the probe's decision).
2. `DV2D_RENDER_BACKEND`, same grammar. Unlike the library — which treats an unrecognised value as
   "unset" — **`dv2d` rejects a typo with exit 1**: a lane that set `DV2D_RENDER_BACKEND=gpuu` would
   otherwise measure the CPU path and report a green budget.
3. auto-probe.

`AppSettings.Playback2D.RenderBackend` is deliberately **not** consulted: a headless tool reads no UI
state (design §7.7). An explicit flag outranks the environment in both directions — `--backend
force-gpu` reaches the hardware even inside a `DV2D_RENDER_BACKEND=cpu` shell.

`--gpu` degrades to CPU **with a printed reason** when no GPU backend exists. `--strict-backend`
upgrades that request to `force-gpu`, so it fails with exit 6 instead — which is what stops a CI lane
going green having quietly measured software rendering. Every `--json` payload echoes both the
resolved `backend` and the requested `backend_requested`.

Windows gets ANGLE over D3D11 (`av_libglesv2.dll`, shipped by `Avalonia.Angle.Windows.Natives` —
`dv2d` references it directly, since it has no Avalonia to inherit it from); Linux gets EGL,
surfaceless first so containers work; macOS is deferred. Run `dv2d probe` to see which.

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

// probe
{"schema_version":1,"command":"probe","ok":true,"backend":"Angle","gpu_available":true,
 "reason":"angle-d3d11","renderer":"ANGLE (NVIDIA, … Direct3D11 vs_5_0 ps_5_0)",
 "vendor":"Google Inc. (NVIDIA)","version":"OpenGL ES 3.0 (ANGLE 2.1.27952)",
 "software_renderer":false,"forced_cpu":false,"duration_ms":174.777}

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

// bench / export with --perf — ONE additive "perf" key; absent without the flag
{"…":"…","perf":{
  "frames":7201,
  "frame_ms":{"p50":14.9599,"p95":17.6,"p99":20.0052,"max":61.2,"mean":15.1},
  "frame_total_ms":108471.7,"max_render_fps":241.3477,"max_frame_fps":66.8454,
  "stages":[{"name":"source","p50":0.405,"p95":0.517,"p99":0.601,"max":…,"mean":…,
             "samples":7201,"total_ms":3220.7,"share_pct":3.0}, …],
  "layers":[{"name":"playback2d.radar","phase":"render","p50":3.265,…,
             "samples":7201,"total_ms":24083.3,"share_pct":22.2,
             "cache":{"replayed":7200,"recorded":1,"uncached":0,"hit_rate":0.9999}}, …],
  "slowest":[{"name":"encode","kind":"stage","total_ms":58628.9,"share_pct":54.0}, …]}}

// export — "encoder" says WHICH PROGRAM encodes; the video_* keys say which codec inside it, and why
{"schema_version":1,"command":"export","ok":true,"out":"round-7.webm","format":"webm",
 "width":1280,"height":720,"fps":60,"speed":1,"frames":7201,
 "frames_per_second":184.6,"demo_seconds":120.0,"realtime_ratio":2.95,
 "backend":"CpuRaster","encoder":"ffmpeg","ffmpeg_origin":"SystemPath",
 "video_encoder":"av1_nvenc","video_encoder_kind":"nvenc","video_codec":"av1",
 "encoder_reason":"the best rung verified first time",
 "encoder_arguments":"-preset p4 -rc vbr -cq 34 -b:v 0 -bf 3 -rc-lookahead 8",
 "quality":"standard","encoder_probe_ms":1302.6,
 "encoder_attempts":[{"encoder":"av1_nvenc","works":true,"detail":"verified"}],
 "layers":[…],"parse_ms":510.1,"elapsed_ms":40921.3}

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

`--budget-bytes-per-frame 4096` is a temporary ceiling: the stack `dv2d` builds is still B0's smoke
layer, which constructs its three `SKPaint`s inside `Render` (~2.8 KB/frame measured on
`duel-mirage-b`). B1's seven layers are themselves allocation-clean — the core suite's
`full-scene-budget` run reports 0 B/frame — so **the PR that registers them in `SceneLayerCatalog`
drops that flag** (returning the gate to the manifest's 0) and drops the `Category!=Budget` filter
from the test step, which enables `BenchAllocationTests`.

The runner needs `libfontconfig1` (`SkiaSharp.NativeAssets.Linux` links fontconfig); the job already
installs it.

---

## Current limitations

These are phase boundaries, not bugs. Each is an honest failure rather than a silent degradation.

| Flag / feature | State | Owner |
|---|---|---|
| `--layout single`, `--level` | exit 6 — `MapSpace`/`StackedLayout` landed with B1, so `--layout stacked` is a real multi-pane render; the single-level policy is still B3's | B3 |
| `--gpu` on macOS | always degrades to CPU (`macos-deferred`); ANGLE/EGL ships for Windows and Linux only | C2 Stage 1 |
| `export --gpu` | exit 6 — `SceneExportSession` awaits its sink between frames, so the loop resumes on whatever pool thread the continuation lands on, while `GpuSurfaceProvider` is bound to the thread that created its EGL context. `export`'s backend chain therefore ends at `force-cpu` rather than `auto`, exactly as `golden` does, so the default is never a refusal. Pinning the loop to one thread is the work, and it is the same work the ≥2× throughput number needs | C2 Stage 1 |
| The `render`/`golden`/`bench` layer set | one smoke layer (`playback2d.debuggrid`); `SceneLayerCatalog.Create` is the single place the real seven register. `export` already draws them, through `SceneLayerCatalog.CreateSceneStack` — a second entry point on purpose, because growing `Create`'s default set moves every committed golden | B1 |
| A scene with no players and no map bundle | derives no floor band, so it gets no pane and renders background only (`synthetic-empty`, marked `pending` in the manifest) | B1, B3 |
| Byte-exact goldens | the corpus defaults to `perceptual`; CPU rasterisation of anti-aliased edges can differ by a least-significant bit between SIMD paths | B1 (embedded typeface), C2 (SSIM) |
| `export --hud`'s kill feed | draws **no rows**. The clock half is real — the round, score and countdown come off the frame the export is drawing, through `TrackerFrameSource.LastGameInfo` — but kill rows come from a parsed `player_death` timeline the app builds off `AllGameEvents`, and `dv2d` has no equivalent. An empty feed beats invented rows | B4 |
