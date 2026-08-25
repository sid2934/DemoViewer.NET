# P1 — per-layer / per-stage performance instrumentation

**Design authority:** [`../design.md`](../design.md) §5.1, §5.7, §6 · **Registry:** [`00-overview.md`](00-overview.md) §3
**Branch:** `feature/playback2d-v2` · **Status:** design fixed before implementation; kept true afterwards.

This is a **measurement** phase, not a feature phase. It adds no pixels and changes no output. Its
exit criterion is that a reader can point at a number and say *this* is why an export runs at 1.1×
realtime instead of 2.7×.

---

## 1. The question this exists to answer

`dv2d export` on a full de_inferno MM demo runs at **90.9 fps overall (1.5× realtime)** and **67.8 fps
(1.1×)** over a busy two-minute mid-match range at 720p60, against **161.5 fps (2.7×)** on the sparse
bundled nuke demo. Nothing currently on the branch can say whether the difference is:

- tracker decode + scene build (`ISceneFrameSource.FrameAt`),
- one expensive layer (vision at ten live players? area effects?),
- the raster itself,
- the surface read-back, or
- ffmpeg backpressure on the bounded frame channel.

`bench` reports only two aggregates (advance, render) over a **fixture**, which by construction has no
tracker and no encoder in it. `export --json` reports only `elapsed_ms`, `frames_per_second` and
`realtime_ratio` — one number with five causes inside it.

## 2. The flag (extended, not invented)

The repo already has exactly one perf-measurement switch: **`CS2DemoKit.Parser.Profiling.Enabled`**
(`docs/profiling.md`), the process-wide runtime gate for the parse, entity-decode and analysis
accumulators. `dv2d` had no flag surface of its own at all.

| Surface | Effect |
|---|---|
| `dv2d bench --perf` / `dv2d export --perf` | attaches the scene recorder for that run |
| `--profile` | accepted alias — the spelling `tools/AnalysisBench` uses |
| `CS2DEMOKIT_PROFILE=1` (env) | `Profiling.Enabled` resolves true → `dv2d` attaches the recorder **too**, and the parser/tracker accumulator trees populate on their own |
| `DEMOVIEWER_PROFILE=1` (env) | same, for the spelling `docs/profiling.md` and `RuntimeEnvInfo` still carry |

**`--perf` deliberately does NOT flip `Profiling.Enabled`.** The tracker decode is one of the things
being measured, and turning on its own per-call `Stopwatch` instrumentation would perturb the very
stage the flag exists to time. The relationship is one-way: the repo switch implies scene capture,
scene capture does not imply the repo switch. A caller who wants both asks for both.

> **Finding, not fixed here:** `docs/profiling.md` and `RuntimeEnvInfo` still name `DEMOVIEWER_PROFILE`,
> but the switch moved into the CS2DemoKit package, whose env var is `CS2DEMOKIT_PROFILE`. `dv2d`
> honours both. A one-line correction is added to `docs/profiling.md`; the app's own wiring is out of
> this phase's scope.

## 3. Where the seams go

### 3.1 Per layer — `SceneCompositor` (Core)

`SceneCompositor` is already the one place layers are iterated, in both entry points, for both
phases. It gains one nullable property:

```csharp
public ISceneProfiler? Profiler { get; set; }
```

`ISceneProfiler` lives in Core and is **three methods with no clock in them**:

```csharp
void BeginLayer(int index, string layerId, LayerPhase phase);   // phase ∈ { Advance, Render }
void EndLayer(int index, LayerPhase phase);
void RecordPicture(int index, PictureCacheOutcome outcome);     // Replayed | Recorded | Uncached
```

**Core never reads a clock.** `System.Diagnostics.Stopwatch` is banned outright in Core by
`BannedApiTests` — the whole type, not just `GetTimestamp` — because a render that can observe wall
time is a render that cannot be reproduced (design §5.1). The interface therefore carries only
*events*; the implementation that timestamps them lives in `Pipeline.Benchmarking`, inside the
namespace prefix that scan already exempts. This is the same argument that put the benchmark harness
in Pipeline in the first place (plan T16), applied one level deeper.

`RecordPicture` is free: `SceneCompositor.RenderLayer` already branches on `picture is null`, which is
exactly the hit/miss decision, and `LayerCacheHint.Dynamic`/`EnablePictureCaching == false` is the
`Uncached` arm. Nothing new is computed.

Cost with the flag off: one field read and one predicted branch per layer per phase, plus one at the
cache decision. No allocation, no clock, no virtual dispatch.

### 3.2 Per stage — `ScenePerfRecorder` (Pipeline)

`ScenePerfRecorder` implements `ISceneProfiler` and adds the stage API the two harnesses drive:

```
Source     ISceneFrameSource.TimeAt + FrameAt   (tracker decode + SceneFrameBuilder)
Advance    HeadlessSceneRenderer.Advance        (levels, panes, cameras, layer advance)
Render     HeadlessSceneRenderer.Render + Flush
Readback   SKSurface.ReadPixels into the staging buffer
Encode     IFrameSink.WriteAsync                (blocked on the capacity-4 bounded channel)
```

`Encode` is the backpressure number: `ChannelVideoFrameSource` is a bounded channel of 4 with
`FullMode.Wait`, so the time the render loop spends inside `WriteAsync` *is* the time the encoder is
behind. `--no-encode` swaps in `HashingFrameSink`, and the same stage then measures the read-back
consumer instead — which is what makes the two runs comparable.

The per-frame total is the sum of the captured stages. Layer rows are **nested inside** `Advance` and
`Render`, never additional to them; their share percentages are of the same frame denominator, so
they sum to slightly under the two stages that contain them.

### 3.3 Zero-allocation capture

- Preallocated `long[]` ring buffers, one per stage and one per (layer × phase). Capacity 4096 frames,
  wrapping; each ring is allocated on its **first push**, i.e. during warmup.
- Per-frame accumulation into flat `long[]` scratch, pushed once per `EndFrame()`. A layer drawn into
  three panes contributes three deltas to one frame sample — which is the honest per-frame cost.
- Raw `Stopwatch.GetTimestamp()` deltas stored as ticks; the conversion to milliseconds, the sort and
  the percentiles all happen in `Snapshot()`, after the measured window.
- Percentiles reuse `FrameTimeStats.From` — nearest-rank, the same p50/p95/p99/max/mean the budget
  gate already uses, so a perf row and a budget row cannot disagree about what p99 means.
- Not thread-safe. One run at a time. The export loop hands off between pool threads across `await`
  but never concurrently, which is the contract the recorder needs.

### 3.4 Harness wiring

`ScenePipelineBenchmark.Perf` and `SceneExportSession.Perf` are nullable `ScenePerfRecorder`
properties; each sets `compositor.Profiler` for the duration of the run and clears it in a `finally`.

The benchmark attaches the recorder **before** the warmup loop and calls `Reset()` after it: the rings
are therefore allocated by warmup frames and the measured window — the one the bytes/frame gate reads
— pushes into arrays that already exist. `Reset()` keeps the rings and zeroes the counters.

## 4. Reporting

Both commands print a human table to the usual stream and, under `--json`, add **one additive key**,
`"perf"`, to the existing `schema_version: 1` payload. Absent without the flag; nothing existing moves
or changes meaning.

```jsonc
"perf": {
  "frames": 900,
  "frame_ms": { "p50": …, "p95": …, "p99": …, "max": …, "mean": … },  // sum of captured stages
  "max_render_fps": …,     // 1000 / render-stage p50 — the uncapped render-only ceiling
  "max_frame_fps": …,      // 1000 / frame p50
  "stages": [ { "name": "source", "p50": …, "total_ms": …, "share_pct": … }, … ],
  "layers": [ { "name": "playback2d.vision", "phase": "render", "p50": …, "total_ms": …,
                "share_pct": …, "cache": { "replayed": …, "recorded": …, "uncached": …,
                                           "hit_rate": … } }, … ],
  "slowest": [ { "name": "source", "kind": "stage", "total_ms": …, "share_pct": … }, … ]
}
```

`max_render_fps` is requirement 3 in one number, and it is the same number under `bench` (which never
encodes) and under `export --no-encode` (which encodes nothing) — that equality is the cross-check
that the two harnesses are measuring the same renderer.

## 5. Proving the default path is untouched

| Gate | Expectation |
|---|---|
| `BudgetTests.FullScene_SteadyState_AllocatesNothing` | passes **unchanged** — 0 B, `Profiler` null |
| `BudgetTests.FullScene_FrameTimes_AreWithinBudget` | passes unchanged against `BudgetPolicy.Ci` |
| `TextBlobCacheTests` allocation gate | untouched |
| CLI Budget lane | unchanged (the one documented `SmallestDrawingFixture_AllocatesNothingPerFrame` failure stays exactly as documented) |
| new `ScenePerfRecorderTests` | recorder detached → 0 B over 512 frames **and** the recorder observed nothing; recorder attached → 0 B/frame steady-state after warmup |

## 6. Deviations from the surrounding plans

1. **`00-overview.md` §3 has no P-track row.** This phase adds one file to Core (`ISceneProfiler.cs`)
   and three to Pipeline; it owns no layer, no format and no golden. Registered here rather than
   re-cutting the master index.
2. **`BannedApiTests` exemption is unchanged.** `ScenePerfRecorder` sits under
   `DemoViewer.NET.Playback2D.Pipeline.Benchmarking.`, which is already exempt, and
   `SceneExportSession` under `…Pipeline.Export.`, likewise. No new exemption prefix was added, and
   `TheBenchmarkExemption_IsActuallyLoadBearing` still holds.
3. **`dv2d bench`'s `frame_ms` is unchanged** and still means advance + render. The `perf` block's own
   `frame_ms` means the sum of the captured stages, which under `export` also includes source,
   read-back and encode. Two names, two scopes, documented at both ends.
