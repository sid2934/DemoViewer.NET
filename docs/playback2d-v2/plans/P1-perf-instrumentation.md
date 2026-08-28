# P1: per-layer / per-stage performance instrumentation

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
`realtime_ratio`: one number with five causes inside it.

## 2. The flag (extended, not invented)

The repo already has exactly one perf-measurement switch: **`CS2DemoKit.Parser.Profiling.Enabled`**
(`docs/profiling.md`), the process-wide runtime gate for the parse, entity-decode and analysis
accumulators. `dv2d` had no flag surface of its own at all.

| Surface | Effect |
|---|---|
| `dv2d bench --perf` / `dv2d export --perf` | attaches the scene recorder for that run |
| `--profile` | accepted alias, the spelling `tools/AnalysisBench` uses |
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

### 3.1 Per layer: `SceneCompositor` (Core)

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
`BannedApiTests` (the whole type, not just `GetTimestamp`), because a render that can observe wall
time is a render that cannot be reproduced (design §5.1). The interface therefore carries only
*events*; the implementation that timestamps them lives in `Pipeline.Benchmarking`, inside the
namespace prefix that scan already exempts. This is the same argument that put the benchmark harness
in Pipeline in the first place (plan T16), applied one level deeper.

`RecordPicture` is free: `SceneCompositor.RenderLayer` already branches on `picture is null`, which is
exactly the hit/miss decision, and `LayerCacheHint.Dynamic`/`EnablePictureCaching == false` is the
`Uncached` arm. Nothing new is computed.

Cost with the flag off: one field read and one predicted branch per layer per phase, plus one at the
cache decision. No allocation, no clock, no virtual dispatch.

### 3.2 Per stage: `ScenePerfRecorder` (Pipeline)

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
consumer instead, which is what makes the two runs comparable.

The per-frame total is the sum of the captured stages. Layer rows are **nested inside** `Advance` and
`Render`, never additional to them; their share percentages are of the same frame denominator, so
they sum to slightly under the two stages that contain them.

### 3.3 Zero-allocation capture

- Preallocated `long[]` ring buffers, one per stage and one per (layer × phase). Capacity 4096 frames,
  wrapping; each ring is allocated on its **first push**, i.e. during warmup.
- Per-frame accumulation into flat `long[]` scratch, pushed once per `EndFrame()`. A layer drawn into
  three panes contributes three deltas to one frame sample, which is the honest per-frame cost.
- Raw `Stopwatch.GetTimestamp()` deltas stored as ticks; the conversion to milliseconds, the sort and
  the percentiles all happen in `Snapshot()`, after the measured window.
- Percentiles reuse `FrameTimeStats.From`: nearest-rank, the same p50/p95/p99/max/mean the budget
  gate already uses, so a perf row and a budget row cannot disagree about what p99 means.
- Not thread-safe. One run at a time. The export loop hands off between pool threads across `await`
  but never concurrently, which is the contract the recorder needs.

### 3.4 Harness wiring

`ScenePipelineBenchmark.Perf` and `SceneExportSession.Perf` are nullable `ScenePerfRecorder`
properties; each sets `compositor.Profiler` for the duration of the run and clears it in a `finally`.

The benchmark attaches the recorder **before** the warmup loop and calls `Reset()` after it: the rings
are therefore allocated by warmup frames and the measured window (the one the bytes/frame gate reads)
pushes into arrays that already exist. `Reset()` keeps the rings and zeroes the counters.

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

`max_render_fps` is requirement 3 in one number: `bench` never encodes and `export --no-encode`
encodes nothing, so both report the uncapped render-only ceiling for the stack they drew. §8.1 is why
those two stacks are not yet the same one.

## 5. Proving the default path is untouched

| Gate | Expectation |
|---|---|
| `BudgetTests.FullScene_SteadyState_AllocatesNothing` | passes **unchanged**: 0 B, `Profiler` null |
| `BudgetTests.FullScene_FrameTimes_AreWithinBudget` | passes unchanged against `BudgetPolicy.Ci` |
| `TextBlobCacheTests` allocation gate | untouched |
| CLI Budget lane | unchanged (the one documented `SmallestDrawingFixture_AllocatesNothingPerFrame` failure stays exactly as documented) |
| new `ScenePerfRecorderTests` | recorder detached → 0 B over 512 frames **and** the recorder observed nothing; recorder attached → 0 B/frame steady-state after warmup; the ring evicts onto its newest frames when it wraps (§9.2); `Reset()` retires rows nothing touched afterwards (§9.1) |

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

---

## 7. What it measured (first run, 2026-08-25)

Windows 11, RTX 4070 Ti SUPER box, **CPU raster**, 1280×720 @ 60 fps, `--hud`, ffmpeg on PATH
(libvpx-vp9, CRF 30, `-row-mt 1`). Inferno = the two-minute mid-match range `--from 72000 --to 79680`
of `match730_003837017413086347571_2138351068_117.dem`; nuke = the whole bundled
`assets/tour/sample-de_nuke.dem`.

| Run | out fps | realtime | frame p50 | source | advance | render | readback | sink |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| inferno, vp9 | 65.0 | 1.1× | 14.96 | 0.405 | 0.014 | 4.143 | 2.151 | **8.067** |
| inferno, `--no-encode` | 153.4 | 2.5× | 6.06 | 0.223 | 0.010 | 2.785 | 1.486 | 1.530 |
| inferno, `--no-encode --no-radar` | 196.8 | 3.2× | 4.75 | 0.215 | 0.012 | **1.311** | 1.678 | 1.526 |
| nuke, vp9 | 73.9 | 1.2× | 12.92 | 0.272 | 0.020 | 4.202 | 2.087 | **6.241** |
| nuke, `--no-encode` | 150.3 | 2.5× | 6.53 | 0.138 | 0.010 | 3.163 | 1.678 | 1.524 |

Stage columns are p50 ms. `sink` under `--no-encode` is `HashingFrameSink` SHA-256ing a 3.5 MB frame:
not free, and the reason "no encoder" is not the same as "renderer alone".

**Share of the inferno vp9 frame:** encode 54.0 %, render 27.9 %, readback 14.9 %, source 3.0 %,
advance 0.1 %. Inside render: `playback2d.radar` 22.2 % of the whole frame (four fifths of the
raster), then `playback2d.markers` 1.4 %, `hud.clock` 0.4 %, everything else under 0.3 % combined.

**The attribution is checked, not asserted.** Turning the radar art off drops the render stage p50 by
1.474 ms and the radar layer row by 1.499 ms. The two agree within 2 %, which is what makes the
per-layer column trustworthy rather than decorative.

**Answering the motivating question.** It is not tracker decode, not vision, not area effects at ten
players. `source` (`FrameAt`, i.e. the entity decode plus `SceneFrameBuilder`) is 3.0 % of the
frame; inferno's is 49 % dearer than nuke's, which moves the total by ~1.5 %. Vision and area effects
together are under 0.3 %. The frame is **libvpx (54 %) + one radar `DrawImage` (22 %) + a read-back
(15 %)**, and content barely enters it.

Nor does the 1.1× / 2.7× gap reproduce as a content difference: at these settings inferno and nuke
are 65.0 and 73.9 fps, 14 % apart, not 2.4×. What does reproduce is **2.5× realtime with the encoder
out of the loop, for both demos**, within 8 % of the 2.7 × nuke reference. The reference pair was
almost certainly measured with libvpx in one path and not the other.

Two second-order facts the stage table makes visible:

- **ffmpeg steals from the renderer.** The same inferno frames raster at 2.785 ms p50 with no encoder
  and 4.143 ms with libvpx running beside them (+49 %), and `max_render_fps` reads 359 vs 241. The
  encoder is not just serialised after the raster; it competes with it for cores and memory bandwidth.
- **Read-back is a real line item**, 15 % of the encoding frame and 25–36 % once the encoder is gone.
  A GPU provider (C2 Stage 1) has to beat a 1.5–2.2 ms `ReadPixels`, not only the raster.

Where the wins are, in order: the encoder (preset/codec/threads, or a GPU encoder), the radar blit
(cache the *pixels* at pane resolution, not just the picture; `RadarLayer.CacheScaledImage` already
caches the resample but the blit itself is still per frame), and the read-back.

## 8. Findings this surfaced (not fixed here)

1. **`dv2d bench` cannot see the shipping layer stack.** `SceneRenderPlan` builds through
   `SceneLayerCatalog.Create`, whose `KnownLayerIds` is still B0's single `playback2d.debuggrid`,
   the seam C1 deviation 14 / risk R6 left open. `export` goes through `CreateSceneStack` and gets the
   real nine. So `bench --perf` reports a correct per-layer table *of a debug grid*, and the CI budget
   gate is gating on it. Closing it means registering the nine layers in the catalog, which changes
   what a default `dv2d render` draws and therefore every golden captured through the tool: B1/C1's
   change to make, not this phase's. Until then, **`export --no-encode --perf` is the per-layer
   authority** and `bench --perf` is the max-render-rate one.
2. **`docs/profiling.md` had drifted.** The single profiling switch moved into the CS2DemoKit package
   and its env var is `CS2DEMOKIT_PROFILE`; the doc still told readers to set `DEMOVIEWER_PROFILE`,
   which no longer flips anything on its own. Corrected in that doc; `dv2d` honours both spellings.
   `RuntimeEnvInfo` and the Desktop/AnalysisBench comments still carry the old name: app-side, out of
   scope here.

---

## 9. Review fixes (2026-08-25)

Independent review of the two P1 commits against `a556ec1`. The default path, the enabled-path
arithmetic and the §7 bottleneck analysis all held up under re-measurement (below); two
defects in `ScenePerfRecorder` did not.

### 9.1 `Reset()` did not retire the rows it zeroed

`Reset()` cleared every sample, counter and head but left `_layerTouched` / `_stageTouched` set. Those
flags are what decide whether a row exists at all, so a slot that only the **warmup** ever exercised
survived the reset. Because `EndFrame()` pushes every touched accumulator whether or not it moved, it
went on pushing a zero into its ring for every measured frame. The report then carried a row of zeros
for something that was never measured, which reads as "measured and free" when the truth is "not
measured". That is the inverse of the rule the stage rows already follow, and which
`PerfFlagTests.Bench_Perf_BreaksTheFrameDownByStageAndLayer` states outright: *a stage nobody measured
would read as "free" rather than "absent"*.

Not reachable from either CLI command today (both drive the same stack across warmup and measurement),
but `Reset()` is public API and its own summary promised to zero "every counter". Both flag arrays are
now cleared with the rest.

### 9.2 The ring's wraparound arm was never executed

`Stats` picks the live window with `start = count == _capacity ? head : 0`. The arithmetic is right, but
nothing reached the wrapped arm: `bench` sizes the ring to `--frames`, `export` sizes it to the range,
and every test sized it above the frames it drove. So the one branch that only a capture outliving its
own history can take, which is what a long `export --perf` becomes past `DefaultCapacity`, shipped
unexecuted. `RingWrapsOntoTheNewestFrames_NotTheOldest` now drives four slow frames then eight fast ones
through a ring of four and asserts the slow samples are gone: 4 samples of 12 frames, max 0.000 ms.

### 9.3 What re-measurement confirmed

**Default path unpaid.** Budget lane A/B on one box, base worktree vs `HEAD`, alternating:

| | `a556ec1` | with P1 |
|---|---|---|
| render p50 | 2.132, 2.151 | 2.191, 2.286, 2.195 |
| render p99 | 3.610, 3.828 | 3.712, 4.310, 3.748 |
| advance p99 | 0.018, 0.016 | 0.015, 0.015, 0.016 |
| allocation | 0 B/frame | 0 B/frame |

The +2.6 % on render p50 sits inside `HEAD`'s own 4.3 % run-to-run spread, against 7× budget headroom.
Allocation is unchanged at exactly zero, warm and steady, flag off **and on**.

**Enabled-path arithmetic.** Independent `export --no-encode --perf` over the §7 inferno range: stage
p50s sum to 6.287 ms against a captured frame p50 of 6.297, a residual of **0.16 %**. Stage shares sum
to 100.0 %. Render-phase layer totals leave 7.4 % of the render stage unattributed, which is the clear,
the pane setup and the flush: outside the layers by construction, and correctly not charged to one.

**§7 reproduces.** vp9: encode 56.3 / render 27.4 / readback 13.5 / source 2.6 / advance 0.1 % against
the recorded 54.0 / 27.9 / 14.9 / 3.0 / 0.1: same ranking, within ~2 points. The radar ablation
cross-check reproduces harder than recorded: render stage p50 −1.809 ms against the radar layer row's
−1.783 ms, agreeing to **1.5 %**. §8.1 reproduces exactly: `bench --perf` on a demo reports one layer,
`playback2d.debuggrid`.

One footnote on the ablation: `--no-radar` does not remove the radar layer, it removes the *art*. The
layer still draws (0.725 ms p50) and its draw count rises from 7 201 to 11 643 over the same 7 201
frames; it is being drawn into a second pane on some frames. That does not disturb the §7 conclusion,
which rests on the stage-vs-layer delta agreeing, but it means "no radar" is not "no radar layer".
