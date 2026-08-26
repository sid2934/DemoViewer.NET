# Playback2D v2 — Architecture & Feature Design

**Status:** **Implemented** — A1, B0–B5, C1, C2 Stage 0, the P and D tracks, and the D6 audit's three
fix rounds have landed on `feature/playback2d-v2`. Five exit criteria are **still open**; they are
listed in [§0](#0-status--what-is-still-open) rather than in a plan appendix, because the next person
to work on this needs to find them.
· **Branch:** `feature/playback2d-v2` · **Design date:** 2026-08-24 · **Status updated:** 2026-08-26
(D6 round 3)

This document is the design for the 2D playback window rework: drawable annotations (static and
time-anchored), video export of the 2D playback, a proper multi-level map model, follow-player,
keybinds, and a scrubbable timeline — on an architecture built to keep absorbing features for years.

It was produced from a structured review: five codebase reconnaissance passes, five external
research passes (competitor tools, annotation data models, Avalonia rendering options, video-export
tech, replay-viewer prior art), three independent architecture proposals (evolutionary /
clean-core compositor / plugin-first), and a two-judge scoring panel. Section 3 records the
decision; the rest is the synthesized design.

**Revision 2** deepens the host-independent core: an explicit performance budget pinned to the
64-tick rate (§6), CPU/GPU render-surface providers usable with no UI window (§5.8), a parser-side
`Pipeline` assembly and a `dv2d` CLI tool for headless rendering/export/benchmarking (§4), and a
test/iteration story that executes directly against the core (§11).

---

## 0. Status — what is still open

*Last re-checked against the tree on 2026-08-26 (D6 round 3), which is the point of a re-stamp: this
section was carrying "updated 2026-08-25 (B5)" while the whole D track and a five-lens audit had landed
since, and two of its five rows had gone stale under it.*

The nine phases are merged. The design's per-phase exit criteria are met **except** the five below.
None of them blocks the release; all five are things a later contributor will otherwise rediscover
from scratch, so they live here, with their owner and the measurement that closes each one. **O6 below
is closed** and is recorded anyway, because it is the one the audit says nobody would have found from
this section.

| # | Criterion | State | Owner / what closes it |
|---|---|---|---|
| **O1** | **1080p60 CPU export ≥ realtime** (§9, B4's row) | **Open — and the default moved instead.** Measured on `assets/tour/sample-de_nuke.dem`, shipped layer set, WebM/VP9: **109.8 fps at 1280×720** (1.83× realtime at 60 fps) and **58.4 fps at 1920×1080** (0.97×). B4 took the third of its R3 levers and made **720p the default export size**; 1080p is one click away and perfectly usable, it simply cannot promise "faster than watching it". | Closed either by CPU work on the layer stack, or by O2 — a GPU provider makes the 1080p number moot. Do not quietly re-default to 1080p without re-measuring. |
| **O2** | **GPU export ≥ 2× realtime at 1080p** (§9, C2's row; transferred to B4 by coordinator decision 2) | **Open.** C2 shipped Stage 0 only: `GpuSurfaceProvider` exists and its ANGLE/D3D11 path is proven on real hardware, with cross-backend parity measured (worst ΔSSIM 0.98352, all divergence on anti-aliased marker rims). Stages 1–2 — flush/readback tuning, threshold calibration, throughput — are deferred to a scheduled spike. Today `SceneExportSession` **refuses** a non-CPU provider, because it awaits its sink between frames and `GpuSurfaceProvider` is thread-affine; making that work is a redesign of `RunAsync` and is the same work Stage 1 needs. | **C2 Stages 1–2.** The baseline to beat is already measured: `dv2d export --no-encode` renders **62.6 fps at 1080p** on `CpuRaster`. **This criterion is also the delete-condition for two other records:** `00-overview.md` §3.10 pins a `RenderBackend` settings key that D6 round 3 deliberately did **not** build — nothing in the app can consume one while this stays open, and a preference whose every value behaves identically except `gpu`, which fails validation, would be the audit's own defect class one layer in — and `Playback2DSettingsConsumptionTests`' allow-list carries the matching entry. Build the key when Stage 1 lands, not before. |
| **O3** | **A de_mirage pre-v2 parity capture** (§11's corpus) | **Open — blocked on an asset, not on code**, but narrower than this row used to claim. It named *`duel-mirage-b` and `fitmap-mirage-eco`*, which are **hand-authored 640×360 dv2d fixtures**: both have existed since C1, both are `pending: false`, both have committed CPU goldens, and `dv2d golden verify` gates them on every PR. Nothing about them was ever demo-blocked; the two names collided with the capture harness's outputs, which is D6 G-8 and is fixed — the captures now own the `prev2-` namespace. What is genuinely still missing is a **pre-v2 control capture on a Mirage demo** (`prev2-mirage-roundstart`), which skips cleanly because the only demo in the tree is `assets/tour/sample-de_nuke.dem`. `mirage-single-level` is absent for the same reason, so B3's "strip hidden on a single-level map" case is covered synthetically instead. | Whoever stages a de_mirage demo: run `Playback2DGoldenCaptureTests` with `PB2D_GOLDEN_UPDATE=1` and commit the pair. The `nuke-multilevel` pair carries the parity gate meanwhile — **not** "byte-exact", as this row said: `GoldenParityTests` compares a delta *distribution* (≥99 % of pixels within ±8, ≥99.5 % within ±32) precisely because two rasterisers cannot agree pixel for pixel, and the manifest declares that entry `perceptual` and `pending` for dv2d. Byte-exactness is `SceneDeterminismTests`, v2 against itself. |
| **O4** | **R2 scrub-latency measurement** (A1's risk register) | **Open.** A1's plan called for measuring perceived latency while dragging the scrub bar on a long demo, to decide whether A2 needs a coalescing/preview seek. Never taken — it needed a staged demo at the time, and the reference demo has since landed. | Anyone, in an hour: drag-scrub `assets/tour/sample-de_nuke.dem` (19 237 frames) end to end and record the seek-to-paint latency. The A2 decision it feeds stays open until then. |
| **O5** | **Envelope drag handles on `AnnotationTrack`** (§12 Q3, B3's T9) | **Open — a feature, not residue.** B2 ships the annotation markers, and the toolbar authors envelopes (Always / Fade / Custom, "pin to now"); dragging a marker's ends to re-time it on the timeline is not built. It was blocked on B2's `DocDelta.Replace`, which now exists — so it is unblocked, small, and simply unscheduled. B5 did not take it because B5 ships no new user-facing behaviour. **The parenthetical above was false when written**: D0 §2.4 found `Custom` was a synonym for `Always` — `EnvelopeMode.Custom` resolved to `TimeEnvelope.Static`, constant opacity 1 — so the toolbar offered *two* behaviours under three names and picking *Custom* changed one persisted string. It is accurate only because **D2 built the `Custom` envelope editor**. Nothing about O5 itself changed; the sentence describing its premise did. | **B3's owner**, as a follow-up. B3's plan carries the design sketch (`EnvelopeHitTest`, `EnvelopeDragSession`, `AnnotationTrackInteraction`) and `TickAxis` is already shipped for the drag math. |

### O6 — the pixel gate that was comparing grids — **FOUND AND FIXED (D6 rounds 2–3)**

**Recorded here as a closed criterion because it was the single largest defect the audit found, and §0
did not mention it at all** — it existed only in a `ci.yml` comment and in two closed phases' plans, so
the one place a reader is told what is still wrong about this module was the one place that never said
the pixel and bench gates were measuring nothing.

`SceneLayerCatalog` held **two** tables. `CreateSceneStack` — the real eleven — was reached only by
`export`. `Create()` served `dv2d render`, `golden` and `bench` from a second list holding exactly one
entry, B0's `playback2d.debuggrid`. The split was deliberate and temporary (growing the default set
re-baselines every committed golden, and B1 was to fold the tables together in the PR that re-captured
the corpus). B1 did not, and nothing failed, because **the goldens had been captured through the same
one-layer stack**. So for four phases:

- CI's only pixel-regression gate on a PR re-rendered every corpus entry as a debug grid and compared
  it, successfully, against a committed picture of a debug grid — `ssim: 1, max_channel_delta: 0` on
  every entry, forever, for any change to any layer;
- `bench --gate` measured that grid — p99 **0.094 ms** against a 16 ms budget, ~170× headroom — and its
  allocation figure was the grid's own three `SKPaint`s, which is why CI carried
  `--budget-bytes-per-frame 4096` and `BenchAllocationTests` sat permanently red behind a category
  exclusion (G-4);
- `dv2d render`, documented as the design-iteration loop for "a marker style, a cone fill, an ink
  outline", answered `--layers markers` with *"unknown layer id(s)"*.

**Fixed:** one table, one entry point. `SceneStackIds` is now the only list and every command builds
through `CreateSceneStack`; the six corpus goldens were re-captured; `--budget-bytes-per-frame` is gone
and the gate reads the manifest's 0 B/frame; `SceneGoldenTests` was retargeted so the two writers of
the same PNGs share one render path; `SceneLayerListParityTests` asserts every other hand-written layer
array against the catalog. Round 3 closed the last hole the fold exposed — `playback2d.vision` was in
the default stack and drew nothing, because the layer read an `IVisionSolver` and ignored the
pre-solved `SceneVision` a fixture actually carries — and added the CLI's `Category=Budget` cases to
the budget lane, which is where `BenchAllocationTests` finally runs.

**What it cost to measure the fix:** `duel-mirage-b`'s render p99 went 0.094 ms → **4.9 ms**, of which
~4.7 ms is resampling the baked `de_mirage` radar at 640×360. Headroom against the scaled CI budget is
now ~3× rather than ~170×, which is the difference between a gate and a decoration.

**Not open, recorded so nobody re-opens them:** the SkiaSharp-on-WASM question (B0's spike passed;
the offscreen CPU provider works on the browser head, and B5 verified the whole app path there — see
[`wasm-matrix.md`](wasm-matrix.md)); §12 Q1 (the seek core is a package type, nothing to extract);
§12 Q3 (B2 ships the markers, B3 the drag handles).

**Scheduled, not open:** deleting the pre-v2 control. §9 keeps it one release behind the toggle; the
plan for removing it is [`old-control-removal.md`](old-control-removal.md), with its trigger
conditions stated.

---

## 1. Goals and market position

Target features:

1. **Annotations** — mouse draw/erase over the playback surface, undo/redo, color picker; both
   *static* drawings (always visible) and *dynamic* drawings (appear/disappear in real time,
   anchored to the demo clock).
2. **Video export** — render 2D playback (with annotations) to gif/webm/mp4 for sharing; the same
   pipeline later powers highlight generation.
3. **Multi-level maps** — a first-class layered view-model (manual level selection + automatic
   switching) replacing the stacked-band heuristic.
4. **Follow-player** — selecting a player's overview card follows them in the 2D view and, when
   LiveSync is active, in-engine.
5. **Polished controls & keybinds** — pause/play, seek, speed, tool shortcuts.
6. **Scrubbable timeline** — bottom-of-window timeline with round bands and event markers.

Plus three cross-cutting requirements:

- **Per-feature enable/disable** and **long-term extensibility** with bounded maintenance cost.
- **Performance floor: sustained 64 scene-frames per second** (matching CS2's 64-tick rate at 1×)
  with headroom above it, so the render pipeline is never the reason a LiveSync-synced session
  drops or coalesces visual frames (§6).
- **Headless operation:** the rendering core must run with no UI window — from CLI tools, CI, and
  tests — with GPU acceleration when available and a CPU path always (§5.8).

The Browser/WASM target must degrade gracefully and explicitly.

### 1.1 Competitive landscape (researched 2026-08)

| Feature | CS:DM | Leetify | Scope.gg | Noesis | Refrag | csstats | Allstar | CS2/CSTV |
|---|---|---|---|---|---|---|---|---|
| 2D scrubbing replay | ✔ free | ✔ paid | ✔ ~paid | ✔ paid | ✔ paid | ✖/~ | ✖ | ✖ (3D) |
| Multi-level map | ~ (opacity overlay) | ✖ | ✖ | ✖ | ✖ | ✖ | n/a | n/a |
| Drawing in replay | ✔ pen/eraser | ✖ | ✖ (separate board) | ✖ | ✔ best-in-class | ✖ | ✖ | ✖ |
| Time-anchored drawings | ✖ | ✖ | ✖ | ✖ | ~ (per-round notes) | ✖ | ✖ | ✖ |
| **2D-view video export** | ✖ | ✖ | ✖ | ✖ | ✖ | ✖ | ✖ | ✖ |
| Round/event timeline | ✔ | ✔ | ✔ | ✔ + filters | ✔ + markers | ~ | n/a | ✔ slider |
| Follow-player cam (2D) | ✖ | ✖ | ✖ | ✖ | ✖ | ✖ | n/a | ✔ (3D) |
| Fully free | ✔ | ✖ | ~ | ✖ | ✖ | ✔ | ~ | ✔ |

Three wide-open differentiators nobody in the market has:

- **2D playback video export.** Every competitor's video path is 3D (HLAE/server-side, quota-gated).
  One-click local gif/webm export of an annotated 2D round is unique.
- **Time-anchored, entity-tracked, persistent annotations.** CS:DM's drawings are session-static;
  Refrag saves per-round drawings but nothing animates on the demo clock. Drawings that live on the
  timeline — and can be *attached to a player* and track them exactly (no computer vision needed,
  unlike Hudl) — exist nowhere.
- **Follow-player camera in 2D**, which combined with export enables "POV radar clips."

Table stakes we must match: Refrag's draggable marker timeline, CS:DM's drawing keybinds
(`D`, Esc, Ctrl+Z / Ctrl+Shift+Z / Ctrl+X, hold-Space-to-pan), CS2's native transport keys.
Positioning: *"Refrag-grade annotation + unique 2D export, free and local."*

Backlog ideas worth stealing later (out of scope here): CS:DM's voice-chat extraction + audio-synced
playback, Refrag's utility timers/molly spread, Noesis's multi-round overlay comparison, heatmaps
with Z-filtering.

---

## 2. Current architecture (what the recon established)

- `Playback2DViewport` is a single 1,438-line `Control`: all drawing is immediate-mode
  `Control.Render(DrawingContext)` (no Skia interop, no bitmaps), redrawn from VM state per paint.
  Invalidation is demand-driven: VM `FrameUpdated` pushes plus a **self-terminating
  `RequestAnimationFrame` loop** that runs only while camera lerps / marker smoothing are settling.
- Multi-floor is **horizontal band-splitting inside `Render`** (clip + translate per band), one
  `SliceCamera` per slice, floors detected by `FloorSplitter` (baked nav floors > Z-histogram with
  sticky hysteresis — the level set **keeps learning during a session**). Radar images are matched
  to bands by a positional count-match heuristic (`ResolveRadarImage`) that silently degrades.
- `ViewportTransform` and `SliceCamera` are pure, dependency-free, unit-tested value types
  (zoom-to-cursor, Y-invert, exponential settle) — the best code in the module.
- The playback clock is a UI-thread `DispatcherTimer` in `PlaybackController` (fractional-frame
  accumulator; every frame decoded, notifications coalesced to ≤1 per render frame). Discrete seeks
  run a 150 ms-debounced checkpoint-replay off-thread; `SeekToTick` is a linear scan.
- **Follow already half-exists:** `CameraMode.FollowPlayer` + `FollowSlot`, and the complete
  LiveSync spectate chain `NotifyFollowSlotChanged` → `IModuleContext.NotifySpectateTarget` →
  `SyncStateObserver` → `SetSpectatorTargetAsync(name)` (send-only; no readback; name-based until
  the `SpectateBySteamId` capability is consumed upstream). The right-hand player cards are a
  selection-less `ItemsControl`.
- LiveSync servo-bends the DV playhead (0.75–1.5× speed, hard resyncs) while synced — anything
  time-anchored must key off **DV frame-clock ticks**, never wall clock, never CS2 ticks.
- Feature flags (`FeatureCatalog`/`FeatureGate` with `SubFeature`/`ParentId` cascade), consolidated
  `AppSettings` persistence, and the module/tab framework all exist and fit this work. WASM:
  single-threaded, Skia-on-WebGL2, no filesystem (settings partially in-memory), no LiveSync/ffmpeg.
- Offscreen prior art exists: headless Skia capture in tests (`ZRadarRenderTests`,
  `HeadlessSession`), `RenderTargetBitmap` via `GraphScreenshot`, a located `FfmpegDependency`, and
  the in-engine reel pipeline (`ReelJobService`, Windows-only, occupies the CS2 session).
- Known hot-path debt: per-frame `FormattedText`/`Pen`/`StreamGeometry` allocations and a per-band
  LINQ in radar resolution — tolerable at UI rate, hostile at export rate and at a 64 fps floor.

---

## 3. Decision: a renderer-agnostic Skia scene core

Three architectures were proposed and judged (engineering lens + product lens):

| | Evolutionary (extract layers, stay on `DrawingContext`) | **Clean-core compositor (Skia)** | Plugin-first (packs/registry/SDK) |
|---|---|---|---|
| Engineering judge | **49** (winner) | 47 | 40 |
| Product judge | 47 | **47** (winner on tie-break) | 43 |

- The **evolutionary** plan had the lowest migration risk and the most verified claims, but both
  judges found its export path the weakest (software `RenderTargetBitmap` raster with unresolved
  thread affinity, ~15 fps encode), its annotation schema freehand-only, and its ceiling closed:
  layers stay welded to Avalonia, so a headless render core (highlight service) never materializes.
- The **plugin-first** plan contributed excellent ideas (`ITimelineTrack`, `LayerCacheHint`,
  declarative keybinds, persistence fallback) but its pack/registry framework is heavy machinery for
  a single consumer, it rewrites proven camera code, and its schedule was judged not credible.
- The **compositor** plan wins on the two features that define the release — annotations and export
  — and is the only architecture that serves the roadmap's ceiling (headless/cloud highlight
  generation reuses the export session wholesale). Its risks are migration-shaped and mitigable.

**Decision: build the clean-core Skia compositor, executed with the evolutionary plan's de-risking
discipline, grafting the plugin-first plan's timeline/caching/persistence ideas.** Concretely:

- All scene painting targets raw `SKCanvas` behind a new Avalonia-free core project. On screen it
  runs inside one `ICustomDrawOperation` with `ISkiaSharpApiLeaseFeature`; offscreen it draws into
  an `SKSurface`. One draw path → pixel-identical screen, export, and test output, background-thread
  geometry building (Avalonia geometry is UI-thread-bound; Skia isn't), `SKPicture` caching, and it
  runs under the compositor on every platform including browser.
- **The core is a complete headless runtime, not just a drawing library.** Every consumer — the
  Avalonia host, the export service, CLI tools, CI, and tests — drives the same compositor through
  the same surface-provider seam (§5.8). No consumer requires an Avalonia platform, a window, or a
  dispatcher; GPU acceleration is an opportunistic upgrade behind the seam, never a requirement.
- `CompositionCustomVisual` is rejected: it bypasses hit-testing, needs cross-thread messaging, has
  an open Decorator-hosting bug (#19436), and its off-UI-thread benefit evaporates on
  single-threaded WASM. The layer contract (pure `Draw(SKCanvas)`) keeps it available as a future
  desktop-only promotion if UI-thread jank ever demands it.
- A CPU `SKSurface` path is always built (the contract baseline and the golden-test path, so it
  can't rot); on screen it feeds a `WriteableBitmap` when the Skia lease is unavailable.
- The proven value types survive: `ViewportTransform` and `SliceCamera` move to the core
  **verbatim** (including per-slice `ManualOverride` semantics); `FloorSplitter` survives as the
  floor-detection authority; the VM data pipeline, XAML HUD chrome, module registration, and all
  LiveSync seams are untouched.

---

## 4. Component map

Two new library projects and one CLI tool, layered so nothing above Core is required to render:

- **`DemoViewer.NET.Playback2D.Core`** — references SkiaSharp only. No Avalonia, no parser, no
  Modules.Abstractions. Enforced by an architecture test (§11).
- **`DemoViewer.NET.Playback2D.Pipeline`** — references Core + the parser packages (CS2DemoKit).
  Still no Avalonia. Owns the demo-domain adaptation: frame building, private checkpoint-replay
  frame sources, map/radar asset decode to `SKImage`, annotation sidecar I/O, and the encoder sinks.
- **`tools/DemoViewer.NET.Playback2D.Cli`** (`dv2d`) — references Pipeline. Headless rendering,
  export, and benchmarking from the command line; no UI window ever.

```
┌────────────────────── DemoViewer.NET.Playback2D.Core ──────────────────────┐
│ Scene2DFrame            immutable per-frame world state                    │
│ SceneTime               injected time; no wall clock anywhere in Core      │
│ ViewportTransform, SliceCamera        (moved verbatim)                     │
│ MapSpace / MapLevel / ILevelLayoutPolicy / LevelPane                       │
│ SceneCompositor ── ISceneLayer[]:  RadarLayer, TrailLayer, AreaEffectLayer,│
│   VisionLayer, MarkerLayer, BombLayer, AnnotationLayer,                    │
│   ClockLayer + KillFeedLayer (export HUD)                                  │
│ ICameraRig:  FitMapRig, FitAliveRig, FollowPlayerRig, ManualRig            │
│ AnnotationDocument (elements, deltas, gesture marks, sidecar DTO)          │
│ ITimelineTrack / TimelineMarker                                            │
│ IRenderSurfaceProvider ── CpuSurfaceProvider (always) | GPU providers      │
│ SceneExportSession ── ISceneFrameSource, IFrameSink, CameraScript          │
└────────────────────────────────────────────────────────────────────────────┘
              ▲
┌───────────────────── DemoViewer.NET.Playback2D.Pipeline ───────────────────┐
│ SceneFrameBuilder       (extracted from the VM's BuildFrame)               │
│ TrackerFrameSource      (private checkpoint replay → Scene2DFrame)         │
│ MapAssetPipeline        (radar decode → SKImage; MapSpace factory)         │
│ AnnotationStore         (sidecar / app-data persistence)                   │
│ FfmpegFrameSink / ManagedGifSink / (later) WebCodecsSink                   │
│ SceneFixture            (JSON scene + annotation fixtures for tests/CLI)   │
└────────────────────────────────────────────────────────────────────────────┘
        ▲                                   ▲
┌──── App (Modules/Playback2D) ────┐   ┌── tools/…Playback2D.Cli (dv2d) ──┐
│ Playback2DTabViewModel (kept)    │   │ dv2d render   single frame → png │
│ Scene2DHost : Control (~300 loc) │   │ dv2d export   range → webm/mp4/  │
│   ICustomDrawOperation + lease,  │   │               gif (same sinks)   │
│   RAF loop, InputToolRouter      │   │ dv2d bench    frame-time p50/p95/│
│ TimelineControl : XAML           │   │               p99 vs budget      │
│ Export dialog; Playback2DKeymap; │   │ --gpu | --cpu backend override   │
│ selectable cards; XAML HUD (kept)│   └──────────────────────────────────┘
└──────────────────────────────────┘
```

Dependency direction is strictly downward: Pipeline adapts `IPlaybackSnapshot`/tracker state into
`Scene2DFrame`; Core turns `Scene2DFrame` into pixels. The on-screen control, the video exporter,
the CLI, and future highlight generation are four thin consumers of the same core. The CLI is also
the foundation for batch/cloud highlight rendering later — it is the export session with argument
parsing, nothing more.

---

## 5. Core contracts

### 5.1 Time and determinism — the load-bearing rule

```csharp
public readonly record struct SceneTime(
    int Tick, int FrameIndex, double DemoSeconds,   // DemoSeconds = ServerTick / tickRate − clockBase
    double DeltaSeconds,                            // injected: real dt interactive, 1/fps on export
    bool IsDiscontinuity);                          // seek/jump — layers reset smoothing/trails

public enum RenderPurpose { Interactive, Export, Thumbnail }
```

All motion (marker smoothing, camera lerps, ink fades, trail decay) consumes `DeltaSeconds`/`Tick`,
so the interactive RAF loop and a fixed-timestep export produce identical motion. `Tick` is the
**DV frame clock** (`DemoFrame.ServerTick`), never CS2 ticks — the LiveSync servo bends the playhead
and `TickMapper` conversion is a per-demo affair; annotations and timelines never touch it.
No `DateTime`/`Stopwatch`/`Random` in Core — enforced by test, not convention.

### 5.2 Layers

```csharp
public enum LayerSlot { Underlay, World, Overlay, Hud }        // coarse z-band
public enum LayerCacheHint { Static, PerCamera, Dynamic }      // declared, auditable caching

public interface ISceneLayer : IDisposable
{
    string Id { get; }                    // stable key: feature gates, settings, layer panel
    LayerSlot Slot { get; }
    int Order { get; }                    // sort key within slot
    LayerCacheHint Cache { get; }         // Static/PerCamera → recorded into SKPicture
    bool IsEnabled { get; set; }
    // UI-thread pre-render step; true = keep the self-terminating RAF loop armed.
    bool Advance(in SceneTime time, Scene2DFrame frame);
    // Pure draw: reads caches built in Advance, must not mutate. Called once per pane.
    void Render(SKCanvas canvas, SceneRenderContext ctx);
}
```

The **Advance/Render purity split** fixes a live defect class: today `AdvanceCameras`/
`AdvanceMarkers` mutate state *inside* `Control.Render`. Here, `Advance` runs on the UI thread
before submission; the draw op consumes only the immutable `Scene2DFrame` + camera snapshot captured
at submission, serialized by a per-host render gate. Porting is mechanical — every existing
`DrawSection` helper already has the shape `(context, transform, sliceIndex)`; the geometry math
transfers, `DrawingContext` calls become `SKCanvas` calls. Layer z-order is one interleaved
`(Slot, Order)` list — annotations sit above actors and below HUD regardless of who registered them.

Text goes through a keyed `SKTextBlob`/`SKFont` cache (no per-frame `FormattedText`); marker/weapon
glyphs become GPU-resident `SKImage` sprites; dry annotation ink and radar are `SKPicture`-cached
per `LayerCacheHint`; the per-frame path is allocation-free (§6 makes this a hard budget, not a
guideline).

### 5.3 Cameras, levels, panes

```csharp
public interface ICameraRig { ViewportTransform? ComputeTarget(LevelPane pane, Scene2DFrame frame); }
// FitMapRig, FitAliveRig, ManualRig, FollowPlayerRig(slot) — the follow rig adds a deadzone window
// (Keren) on top of today's 900u half-extent box so small strafes don't shimmy the camera.

public sealed class LevelPane
{
    public MapLevel Level { get; set; }
    public SliceCamera Camera;            // kept struct: StepToward / IsSettledAt / ManualOverride
    public ICameraRig Rig { get; set; }
    public SKRect ViewportRect { get; set; }
}

public sealed class MapSpace
{
    public IReadOnlyList<MapLevel> Levels { get; }   // { Id, Name, ZMin, ZMax, SKImage? Radar }
    public MapLevel LevelFor(double worldZ);          // hysteresis band at boundaries
    public event Action? LevelSetChanged;             // FloorSplitter keeps learning — see below
}

public interface ILevelLayoutPolicy { IReadOnlyList<LevelPane> Arrange(MapSpace space, LevelDisplayMode mode, SKSize host); }
// StackedLayout (today's bands) | SingleLayout (manual pick / auto-switch) | future SideBySide
```

**Level identity is `ZMin`-keyed, and `MapSpace` is rebuildable.** The recon verified that
`FloorSplitter`'s Z-histogram keeps learning during a session (sticky-count hysteresis exists
precisely because the level set shifts), so levels must not be resolved "once at load": `MapSpace`
re-derives on `FloorSplitter` change with stable quantized-`ZMin` level ids, remapping panes and
annotations on rebuild. Radar binding is explicit per level, resolved at (re)build with a visible
"no radar for this level" state — the silent per-frame count-match LINQ dies. Trail and smoothing
buffers reset when an entity crosses levels (boltobserv's documented streak-across-the-map pitfall).

`FloorSplitter`'s precedence chain (baked nav floors > histogram; networked section heights
deliberately not adopted) survives intact inside the `MapSpace` factory.

### 5.4 Annotation document

Following the Kinovea / tldraw / Excalidraw / perfect-freehand consensus from research: elements in
**world space**, raw input points persisted (outlines derived at render), delta-stack undo with
gesture marks, stroke-level erase only.

```csharp
public sealed record AnnotationElement(
    Guid Id, AnnotationKind Kind,                  // Freehand, Line, Arrow, Rect, Ellipse, Text
    AnnotationStyle Style,                         // ARGB color, width (world units), opacity
    SpaceRef Space, TimeEnvelope Time,
    IReadOnlyList<InkPoint> Points,                // world x,y (+pressure) — never screen space
    string? Text);

public abstract record SpaceRef
{
    public sealed record World(double LevelMinZ) : SpaceRef;            // default; ZMin-keyed, remapped on rebuild
    public sealed record Entity(ulong SteamId, float Dx, float Dy) : SpaceRef;  // tracked telestration
    // Screen(normalized) reserved for future HUD-style notes.
}

public readonly record struct TimeEnvelope(int? FromTick, int? UntilTick, int FadeInTicks, int FadeOutTicks)
{
    public static readonly TimeEnvelope Static = default;   // null bounds = always visible
    public double OpacityAt(int tick);                      // Kinovea trapezoid; pure fn → scrub-safe
}

public sealed class AnnotationDocument
{
    public IReadOnlyList<AnnotationElement> Elements { get; }
    public int Version { get; }                     // ink layer re-records SKPicture on change
    public IDisposable BeginGesture(string name);   // mark; dispose = one undo entry; Bail() on Esc
    public void Apply(DocDelta delta);              // invertible add/remove/replace
    public bool Undo(); public bool Redo();         // camera/playback/selection excluded by contract
    public event Action? Changed;
}
```

Notes locked in by the review:

- **Entity anchors use SteamId, not slot** — slots recycle and rosters reseed mid-demo
  (`Playback2DRosterReseedTests` exists for exactly this). Resolution SteamId → slot happens per
  frame; while the target is unresolvable or dead, the element hides (clamped by its envelope).
- **Freehand rendering** uses a small C# port of perfect-freehand (MIT; Rust/Dart ports prove
  tractability, ~300 loc): raw points + pressure in, closed outline polygon out. The partial-stroke
  rendering it supports gives "draw-on reveal" animation for dynamic strokes nearly free.
- **Wet/dry split:** the in-progress stroke draws incrementally from
  `PointerEventArgs.GetIntermediatePoints` (coalesced samples + pressure); committed elements are
  `SKPicture`-cached and re-recorded only on `Version` change.
- **Eraser is stroke-level** (hit-test derived outline → remove-delta → undoable). Pixel erase is an
  open feature request even in tldraw/Excalidraw; explicitly deferred.
- **Time-editing UX** (so dynamic annotations aren't write-only): a "pin to now" action stamps
  `FromTick = CurrentTick`; anchored elements appear as markers on the timeline with drag handles to
  edit their envelope; per-element visibility mode (Always / Default fade / Custom), Kinovea-style
  style-stickiness (last used style becomes the default).
- **Persistence** (Pipeline's `AnnotationStore`): JSON sidecar `<demo>.dvann.json` when the demo's
  directory is writable, else app-data keyed by demo hash. Versioned schema, tolerant reader,
  unknown fields preserved. The file records **demo identity (hash) and clock identity (DV frame
  clock)** so a re-parse with different frame segmentation can detect rather than silently corrupt
  anchored strokes. Tool prefs (color, width, last tool) go in a new binder-safe
  `Playback2DSettings` on `AppSettings` — and must be added to `SettingsService.WriteInMemory` or
  WASM writes silently vanish.

### 5.5 Input tools

```csharp
public interface IPointerTool
{
    ToolKind Kind { get; }                       // PanZoom, Draw, Erase
    bool OnPressed(in ToolPointerEvent e, IToolServices s);   // world+screen pos, pane, pressure
    void OnMoved(in ToolPointerEvent e, IToolServices s);
    void OnReleased(in ToolPointerEvent e, IToolServices s);
    void OnCancelled(IToolServices s);           // Esc mid-gesture → BailToMark, no undo entry
}
```

`InputToolRouter` owns exactly one active tool; `PanZoomTool` (today's drag/wheel code, moved) is
the permanent fallback; **hold-Space temporarily reverts to pan while drawing** (CS:DM's proven UX).
Tools reach the world only through `IToolServices` (`ScreenToWorld`, `PaneAt`, annotation session,
`CurrentTick`) — never the control. This unfuses the hardwired pan-drag in `OnPointerPressed`.

### 5.6 Timeline tracks

```csharp
public interface ITimelineTrack
{
    string Id { get; }
    IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data);  // tick, glyph, tooltip, seek target
    event Action? MarkersChanged;
}
```

`TimelineControl` itself is XAML chrome (DataTemplates, tooltips, accessibility beat canvas for
linear UI): a rounds band + intra-round scrub bar absorbing the current bottom status bar. Tracks:
`RoundTrack` (via `SemanticNavigator` — remembering CS2 rounds open with `round_freeze_end`, not
`round_start`), `KillTrack` and `BombTrack` (via `GetEventTimeline`), `AnnotationTrack` (every
anchored element is a clickable marker — Frame.io's annotation-as-bookmark pattern), and optionally
a CS2 ghost cursor from `LastCs2DemoTick` through `TickMapper`. Future features (highlights) land as
new tracks.

Scrubbing emits `RequestSeekToFrame` (frame index is the movement contract). The existing 150 ms
debounce + latest-wins coalescing absorb drag bursts, and LiveSync's 140 ms settle + single-slot
seek pipeline absorb them downstream — raw pushes are safe. `SeekToTick`'s linear scan becomes a
binary search. **Expectation set by the review:** on long demos, drag-scrub over the debounced
checkpoint replay will feel coarse; a checkpoint-density/near-playhead cache improvement should be
expected to pull forward into the timeline phase rather than deferred (§10 risk 4).

### 5.7 Export pipeline

```csharp
public interface ISceneFrameSource
{
    int FrameCount { get; }
    SceneTime TimeAt(int frameIndex);
    Scene2DFrame FrameAt(int frameIndex);
}

public interface IFrameSink : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct);
}

public sealed record ExportRequest(int StartFrame, int EndFrame, int Fps, SKSizeI Size, double Speed,
    string FormatId /* webm | mp4 | gif */, IReadOnlySet<string> LayerIds, CameraScript Camera);

public sealed record CameraScript;   // Fixed(transform) | FollowPlayer(steamId) | MirrorLiveView
                                     // — the future highlight generator emits these

public sealed class SceneExportSession
{
    public Task RunAsync(ExportRequest req, ISceneFrameSource src, IFrameSink sink,
        IRenderSurfaceProvider surfaces,            // CPU or GPU — §5.8
        IProgress<ExportProgress> progress, CancellationToken ct);
}
```

Rules locked in by the review:

- **Export never touches the shared app clock.** `RequestSeekToFrame` moves every module and any
  LiveSync session. `TrackerFrameSource` (Pipeline) owns a **private** tracker replay on a
  background thread, reusing the shared checkpoint-replay seek core that already backs discrete
  seeks (extract it from `MainViewModel`'s wiring rather than paying a from-zero replay to reach
  `StartFrame`; exact type boundary to be confirmed at implementation — the review flagged that one
  proposal misnamed it).
  **Correction (B4):** there is nothing to extract. The seek core is the package type
  `CS2DemoKit.Parser.EntityTracking.EntitySeekService`; `MainViewModel` owns only an *instance* of it,
  and `MainViewModel.CreateTracker` is UI/debugger-specific and must never back an export.
  `TrackerFrameSource` builds its own service over `() => new EntityTracker()` and accepts one
  from-zero replay to reach `StartFrame` — no checkpoint cache exists to reuse. See
  `plans/B4-export.md` D1/D2.
- Fixed timestep `dt = 1/fps` through the same layer stack (`RenderPurpose.Export`), rendering to an
  `SKSurface` from the provider, `ReadPixels` → sink. Determinism is guaranteed by §5.1, verified by
  golden tests.
- Export enables two Core HUD layers (`ClockLayer`, `KillFeedLayer` — cheap `SKTextBlob` draws fed
  by the same pre-built kill timeline as the XAML HUD) so clips look complete without capturing
  XAML. Snapshot tests pin export-HUD rows to the same VM data to prevent dual-HUD drift.
- **Interlock:** in-app export runs under `HeavyJobGate` and **refuses to start** while a LiveSync
  session or reel job is active (SyncEngine has no pause semantics; refusal is specifiable today).
  The CLI has no such constraint — it owns its whole process.
- **Sinks and formats:** `FfmpegFrameSink` via FFMpegCore (MIT) piping rawvideo RGBA over stdin to
  an ffmpeg **subprocess** — the FSF "separate programs" posture keeps GPL clean. Defaults:
  **WebM/VP9** (`-c:v libvpx-vp9 -pix_fmt yuv420p -an`, present even in LGPL builds), MP4/H.264
  (`libx264 -crf`, needs GPL build) for compatibility, GIF via two-pass `palettegen`/`paletteuse`.
  Presets 720p/1080p/map-native × 30/60/64 fps (64 = native tick rate, 1:1 frame-per-tick clips).
  Progress is frames-done based, cancel kills ffmpeg.
- **ffmpeg acquisition ladder:** `FfmpegDependency.Locate()` (existing) → download-on-demand pinned
  BtbN build with license text shown and source link → `ManagedGifSink` (ImageSharp, Apache-2.0 for
  OSS) as the no-ffmpeg floor. Never Xabe.FFmpeg (CC BY-NC-SA). Settings mirror `HighlightsSettings`.
- **WASM:** export is feature-gated off in v1. The `IFrameSink` seam is where a
  WebCodecs + webm-muxer backend plugs in later (same WebM/VP9 output on both targets by design);
  a chunked ImageSharp GIF encode is the browser stretch goal after desktop ships.

### 5.8 Render surface providers — headless and GPU-accelerated paths

The seam that makes Core a runtime instead of a library. Every offscreen consumer (export, CLI,
tests, thumbnails; on-screen fallback when the lease is absent) obtains surfaces through it:

```csharp
public enum RenderBackend { CpuRaster, OpenGl, Angle, Vulkan }

public interface IRenderSurfaceProvider : IDisposable
{
    RenderBackend Backend { get; }
    SKSurface CreateSurface(SKSizeI size);          // RGBA8888, premul
    void Flush(SKSurface surface);                  // GPU: GRContext.Flush + submit; CPU: no-op
}
```

- **`CpuSurfaceProvider`** — `SKSurface.Create(SKImageInfo)` raster. Always available, zero native
  dependencies beyond SkiaSharp, runs anywhere including CI containers and WASM. It is the
  **contract baseline**: golden images are authored on it, and every feature must be correct
  (not necessarily fastest) on it.
- **`GpuSurfaceProvider`** — a windowless `GRContext`-backed provider for desktop/CLI/CI-with-GPU.
  Probe order (first success wins, chosen once per process, logged):
  1. **Windows:** ANGLE over D3D11 via EGL pbuffer/surfaceless (ships with predictable behavior
     across driver zoos; Avalonia itself uses ANGLE on Windows, so the native bits are familiar
     territory), falling back to a hidden-context WGL path.
  2. **Linux:** EGL surfaceless / GBM context (works on headless boxes with a GPU and in
     GPU-enabled containers).
  3. **macOS:** CPU initially; a Metal-backed `GRContext` is a later, isolated addition.
  4. **Anywhere probing fails:** `CpuSurfaceProvider`, with the reason logged once.
  The probe result is overridable everywhere it matters: `dv2d --gpu | --cpu`, an export-dialog
  advanced option, and an env var for CI. The exact windowless-context stack (ANGLE binaries vs a
  thin Silk.NET/OpenTK EGL binding vs SkiaSharp's Vulkan backend) is a **time-boxed spike at the
  start of phase C2** — the provider interface is the commitment; the winning backend is not.
- **Backend equivalence policy:** CPU goldens are authoritative. The GPU path is validated by
  perceptual diff (per-channel tolerance + SSIM-style threshold), not byte equality — AA and
  rounding legitimately differ between raster and GPU. A CI job with GPU runners runs the
  perceptual suite; the byte-exact suite runs everywhere on CPU.
- **On screen** the interactive path keeps using Avalonia's Skia lease (already GPU-composited);
  providers are for surfaces *we* own. The two paths share all layer code, so a bug is visible in
  both or in neither.

**Why this matters beyond export:** `dv2d render` gives a sub-second edit-render-look loop for
visual design work (render one tick of a fixture scene to PNG, no app launch); `dv2d bench` gives
CI-enforceable frame-time numbers on both backends; and a future cloud highlight service is
`SceneExportSession` + `GpuSurfaceProvider` on a Linux box — all three fall out of this seam.

---

## 6. Performance targets and budget

The floor is set by the demo, not by the display: CS2 demos tick at 64 Hz, and a LiveSync-synced
session must play back in real time without the renderer forcing dropped or coalesced frames.

**Targets (baseline hardware: mid-tier laptop, integrated GPU, 1080p viewport):**

| Metric | Floor | Target |
|---|---|---|
| Sustained scene-frame rate at 1× | **64 fps** (15.6 ms period) | 120+ fps headroom |
| `Advance` (all layers, UI thread) | ≤ 2 ms | ≤ 1 ms |
| `Render` (all panes, full scene: 10 players, trails, vision cones, area effects, annotations) | ≤ 8 ms | ≤ 4 ms |
| Steady-state allocations per frame | **0 bytes** (post-warmup) | 0 bytes |
| Export throughput, 1080p CPU | ≥ realtime (64 fps) | — |
| Export throughput, 1080p GPU | — | ≥ 2× realtime |

Clarifications so the numbers mean one thing:

- **"No skipping" means the render pipeline is never the bottleneck.** At 1× on a ≥64 Hz display,
  every tick renders. On a 60 Hz display, the compositor's refresh — not our frame time — is the
  only coalescing that occurs. During LiveSync the servo can bend playback to 1.5× (≈96 decoded
  ticks/sec); decode is sub-millisecond per frame (`AdvanceOneFrame`), and display coalescing to
  refresh at >1× speeds is by design — the budget guarantees the *renderer* always keeps up.
- **The budget is why the allocation discipline is a contract, not advice.** The known hot-path debt
  (per-frame `FormattedText`/`Pen`/`StreamGeometry`, per-band LINQ) is eliminated during the port,
  and the GC must be silent during playback — a gen-0 pause is a dropped frame at 64 fps.
- **Vision overlay is the budget's biggest single consumer** (26 raycasts × alive players + pairwise
  sightlines per frame). It ports as-is first; if it threatens the floor on baseline hardware, its
  `Advance` moves off the UI thread (compute into the next frame's snapshot — the layer contract
  already permits this) before any visual degradation is considered.

**Enforcement (all in CI, all runnable locally):**

- `dv2d bench --demo <fixture> --frames 2000 [--gpu|--cpu]` — plays a standard fixture demo through
  the full compositor headlessly and reports frame-time p50/p95/p99 and allocated bytes/frame.
  CI gates on p99 ≤ budget (CPU runner always; GPU runner where available).
- BenchmarkDotNet micro-benchmarks for the hottest layers (markers, vision, annotation replay) —
  the repo's `bench-reports/` + `tools/EntityMicroBench` precedent extends to render layers.
- Allocation assertion: `GC.GetAllocatedBytesForCurrentThread()` measured across a 512-frame
  headless run after warmup must be zero (test fails on any steady-state allocation).
- A WASM frame-budget smoke test (relaxed budget, CPU path) keeps the browser target honest.

---

## 7. Feature designs

### 7.1 Annotations
`Draw`/`Erase` tools + `AnnotationDocument` + wet/dry `AnnotationLayer`; color/width picker as XAML
chrome writing `AnnotationStyle` defaults into `Playback2DSettings`. Static = `TimeEnvelope.Static`;
dynamic = stamped at `CurrentTick` with configurable envelope, editable on the timeline. Shape kit
from v1 schema (Freehand first; Line/Arrow/Rect/Ellipse/Text follow without schema breaks because
`Kind` exists from day one). Entity-anchored elements give tracked telestration no video tool can
match. Undo/redo: gesture marks, squash-to-mark, `BailToMark` on Esc.

### 7.2 Video export
Export dialog builds `ExportRequest` (range from timeline selection or current round; camera
Fixed/Follow/Mirror; layer toggles — annotations/vision/HUD on or off per export). Runs per §5.7 on
a provider from §5.8 (GPU when probed, CPU otherwise). The same request shape is scriptable from the
CLI: `dv2d export`. Later highlight generation = a `CameraScript` emitter over `GetEventTimeline`
ranges + the same session; zero new rendering work.

### 7.3 Multi-level maps
`MapSpace` + `ILevelLayoutPolicy`. `StackedLayout` preserves today's all-floors view; `SingleLayout`
adds a level strip (manual pick) and **AutoFollow** — switch to the followed player's level via
`LevelFor(z)` with hysteresis. Explicit per-level radar binding with a visible no-radar state.

### 7.4 Follow-player
The Attributes `ItemsControl` becomes selectable; selection sets `FollowedSlot` on the VM, which
(a) drives `FollowPlayerRig` (deadzone + exponential settle) and auto level switching, and
(b) calls the existing `NotifyFollowSlotChanged` → `NotifySpectateTarget` → LiveSync
`SetDesiredSpectator` chain. UI shows **"requested," not "confirmed"** (spectate has no readback);
name-based targeting keeps its known rename limitation until `SpectateBySteamId` is consumed
upstream — surface SteamId through the chain when that capability reports true.

### 7.5 Keybinds
Declarative `Playback2DKeymap` (action → gesture table, conflict-checked at registration,
future-rebindable), bound on the focusable host: Space play/pause, ←/→ step, ↑/↓ speed, Q/E round
nav, F follow-cycle, D draw, E erase, Esc exit/bail, Ctrl+Z / Ctrl+Shift+Z / Ctrl+X (CS:DM parity).
All playback mutations route through `PlaybackController` commands / capability-gated
`IModuleContext.Request*` — the exact surfaces `SyncStateObserver` observes; a parallel path would
silently bypass LiveSync. Must not collide with shell bindings (Ctrl+1..9, Ctrl+P/O/B/W).

### 7.6 Timeline
Per §5.6. Feature-gated chrome; markers only for events the demo has (`AvailableEventNames`).

### 7.7 Feature gates
New `FeatureCatalog` entries, all `SubFeature` with `ParentId "tab.playback2d"` (cascade off with
the tab, per-category defaults, user-overridable): `playback2d.annotations`, `playback2d.timeline`,
`playback2d.levels.auto`, `playback2d.follow`, `playback2d.export` (additionally AND
`!OperatingSystem.IsBrowser()`, like `chrome.livesync`). A gated-off feature's layers are skipped by
the compositor and its tools/chrome hidden. **Ids are persisted keys — chosen once, never renamed.**
`TabId "playback2d.viewport"` and `"tab.playback2d"` stay stable; bump `Playback2DModule.
ContractVersion` for any additive context consumption. Feature gates govern the app; the CLI takes
explicit flags instead (a headless tool shouldn't read UI feature state).

---

## 8. WASM statement (explicit)

Works fully in browser: core rendering (custom op under the browser compositor, WebGL2→WebGL1→
software chain), annotations **in-session only**, levels, 2D follow, keybinds, timeline. Degraded or
absent: annotation persistence (no filesystem — stated in UI as session-only), video export (gated
off; WebCodecs sink later, chunked GIF as stretch), LiveSync/in-engine follow (hooks unset, already
null-tolerated), `GpuSurfaceProvider` (browser surfaces belong to Avalonia's compositor; the CPU
provider is the only offscreen path there). The allocation-free render path is what keeps the single
thread honest. Any new persisted settings keys must be added to `SettingsService.WriteInMemory`.

**Verified, and the record is [`wasm-matrix.md`](wasm-matrix.md).** B5 published the head and ran it
in a real browser with a real demo: the paragraph above holds, and three things it does not mention
came out of the pass — the head needs `WasmBuildNative=true` and `PublishTrimmed=false` to boot at
all, baked radar art is a further degradation (there is no directory to load it from, so every level
falls back to the grid), and `SettingsService.WriteInMemory` now has a reflection-driven test making
the "any new persisted key" rule mechanical rather than remembered.

---

## 9. Migration plan

**Kept as-is:** `ViewportTransform`, `SliceCamera`, `FloorSplitter` internals, `VisibilityAnalyzer`
/ vision BVH, `MapAssetLoader` decode logic (retargeted `Bitmap`→`SKImage`, moved to Pipeline), the
entire VM data pipeline (`OnAdvanced` copy-out, kill timeline, bomb/clock derivation, ring tracker,
map-asset lifecycle), XAML HUD, module registration/lifecycle, all of LiveSync, `PlaybackController`.
**Ported mechanically:** every `DrawSection` helper's math into `ISceneLayer.Render`.
**Extracted:** `BuildFrame` → `SceneFrameBuilder` (Pipeline); the checkpoint-replay seek core out of
`MainViewModel` wiring. **Rewritten:** the viewport control shell (input, band arithmetic,
invalidation) → `Scene2DHost` + compositor; `ResolveRadarImage` → `MapSpace`.
**Deleted:** nothing user-visible until parity is proven — the old control is retained one release
behind an internal toggle.

Three tracks. A ships user-visible wins immediately on the current control; B is the core port;
C builds the headless/CLI/GPU surface on top of B and can run in parallel with B2–B4.

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **A (UX, on the *current* control — renderer-independent)** | A1 | `TimelineControl` + round/kill/bomb tracks; keymap + keybinds; selectable player cards → follow + spectate; binary-search `SeekToTick` | Scrub + keys + follow-by-card shipped | 1.5 wk |
| **B (core)** | B0 | Core + Pipeline projects; move structs; `SceneFrameBuilder` + `CpuSurfaceProvider` + scene fixtures; golden-image tests pinning current output | Frames render to PNG with **zero Avalonia dependencies**; goldens green | 2 wk |
| | B1 | Port 7 layers to `SKCanvas`; `Scene2DHost` + draw op + CPU fallback; `MapSpace`/panes replicating stacked bands; deterministic-time plumbing; allocation cleanup; `dv2d bench` harness + budget gates in CI | Pixel-parity (± reviewed text metrics) vs B0 goldens; p99 ≤ budget on CPU baseline; old control behind toggle | 3 wk |
| | B2 | Annotations: document + deltas + undo, Draw/Erase tools, wet/dry layer, color picker, envelopes + timeline markers, sidecar + app-data persistence | Draw/erase/undo survive seek, zoom, level switch, tab deactivate | 2.5 wk |
| | B3 | Levels: `SingleLayout`, level strip, AutoFollow + hysteresis, buffer resets; `AnnotationTrack` time-edit handles | Levels shipped | 1 wk |
| | B4 | Export: seek-core extraction, `SceneExportSession`, ffmpeg sink + GIF floor, dialog, settings, gates, HUD layers + snapshot tests | 1080p round export ≥ realtime on CPU; cancel-safe; refuses under LiveSync | 2 wk |
| | B5 | Polish: WASM verification pass, feature-flag audit, keybind conflict audit, docs, old-control removal (next release) | Release | 1 wk |
| **C (headless/CLI/GPU — parallel with B2–B4)** | C1 | `dv2d` tool: `render` (single frame → PNG from demo or fixture), `export` (CLI front-end to the session), `bench` promoted from harness to command; fixture library for design iteration | A designer/dev renders any tick to PNG in <1 s without launching the app; CI uses `dv2d` for goldens + budgets | 1 wk |
| | C2 | `GpuSurfaceProvider`: time-boxed backend spike (ANGLE/EGL vs native GL vs Vulkan), probe + override flags, perceptual-diff validation vs CPU goldens, GPU lane in CI where runners allow | GPU export ≥ 2× realtime at 1080p on a baseline dGPU/iGPU; CPU parity within perceptual tolerance | 1.5 wk |

**Honest total: ~15.5 person-weeks** (~12.5 on the A+B critical path; C overlaps B2–B4). Track A
lands first and independently; every B phase ships behind its gate.

**As built:** every row above has landed except C2's Stages 1–2. The two exit criteria in this table
that are not met — B4's "1080p ≥ realtime on CPU" and C2's "GPU ≥ 2× realtime" — are
[§0](#0-status--what-is-still-open) O1 and O2, with their measurements. The per-phase
`Implementation notes (deviations)` sections in `plans/*.md` are the record of what each phase
actually shipped and where it departed from its plan.

---

## 10. Risk register

| # | Risk | L | I | Mitigation |
|---|---|---|---|---|
| 1 | Port regresses visuals (esp. text metrics vs `FormattedText`) | H | M | B0 goldens gate B1 on the CPU provider; old control retained one release behind toggle; text differences reviewed, not auto-failed |
| 2 | Render-thread races against layer caches | M | H | Advance/Render purity split; render gate; ops consume immutable snapshots; Core has no VM references; stress test in CI |
| 3 | Export nondeterminism (wall-clock leak) | L | H | Banned-API architecture test in Core; all motion consumes injected `SceneTime`; golden export frames |
| 4 | Scrub feel limited by debounced checkpoint re-seek on long demos | M | M | Ships with debounce + latest-wins; checkpoint-density/near-playhead cache expected to pull into Track A follow-up — treated as likely, not optional |
| 5 | Level set shifts under floor-tagged annotations (`FloorSplitter` keeps learning) | H | L | `ZMin`-keyed level identity; `MapSpace` rebuild event remaps panes + annotations; never store slice index |
| 6 | 64 fps floor missed on baseline hardware (vision overlay dominant) | M | M | Budget gates in CI from B1 (`dv2d bench` p99); allocation-zero contract; vision `Advance` moves off UI thread before any visual degradation; GPU path adds headroom |
| 7 | Windowless GPU context flaky across drivers/CI | M | M | CPU provider is the contract baseline — GPU is opportunistic; ANGLE-first probe order; `--cpu` override everywhere; perceptual-diff (not byte) parity policy; C2 spike is time-boxed |
| 8 | Dual HUD drift (XAML live vs Skia export) | M | L | Export HUD minimal, fed by identical VM data; snapshot tests |
| 9 | ffmpeg licensing/distribution missteps | L | M | Subprocess-only (FSF separate-programs); WebM/LGPL default; license text + source link on download; never Xabe |
| 10 | Export while LiveSync active corrupts sync | L | H | Private tracker + `HeavyJobGate` + hard refusal while a sync session or reel job is active (in-app; CLI owns its process) |
| 11 | Entity-anchored strokes break on roster reseed / slot recycling | M | M | Anchor by SteamId, resolve per frame; hide while unresolvable; `Playback2DRosterReseedTests` pattern extended |
| 12 | WASM perf (single thread) | M | M | Allocation-free path, `SKPicture` caches, frame-budget smoke test; export absent |
| 13 | Undo scope creep | L | M | History lives only in `AnnotationDocument`; camera/playback/selection excluded by contract |

---

## 11. Enforcement, testing, and the iteration loop

- **Architecture tests:** Core references only SkiaSharp; Pipeline references Core + parser but no
  Avalonia; no `DateTime.Now`/`UtcNow`/`Stopwatch`/`Random` in Core (banned-API scan).
- **Direct-execution tests (no Avalonia platform at all):** scene-level unit/integration tests
  construct `Scene2DFrame`s (or load `SceneFixture` JSON), run the compositor against the
  `CpuSurfaceProvider`, and assert on pixels or geometry — no `HeadlessSession`, no window, no
  dispatcher. This is strictly faster and less flaky than the current headless-Avalonia harness,
  which remains only for tests that genuinely exercise the Avalonia host (`Scene2DHost` input,
  lease path, XAML HUD).
- **Golden-image tests:** CPU-provider goldens pin B0 output; B1 must match; export frames get
  their own goldens. GPU output is validated against the same goldens by perceptual diff (§5.8).
- **Determinism test:** two export runs of the same request produce byte-identical frame hashes
  (per backend).
- **Performance gates:** `dv2d bench` p50/p95/p99 vs the §6 budget on the CPU lane in every CI run,
  GPU lane where runners allow; zero-allocation assertion over a 512-frame run; BenchmarkDotNet
  micro-benches for hot layers reported to `bench-reports/`.
- **The design-iteration loop:** `SceneFixture` files (a serialized `Scene2DFrame` + optional
  annotation document + camera) live under `tests/fixtures/playback2d/`. `dv2d render --fixture
  duel-mirage-b.json --out /tmp/f.png` re-renders in well under a second — tweaking a marker style,
  a cone fill, or an ink outline becomes edit → render → look, with no app launch and no demo
  parse. The same fixtures are the golden-test corpus, so iteration and regression coverage are the
  same artifact.
- **Behavioral tests carried forward:** interpolation snap/glide/prune, roster reseed, seek-push
  coalescing, floor validation probes — all keep passing against the new host.
- **Snapshot tests:** export HUD rows vs XAML HUD data; annotation schema round-trip with unknown
  fields preserved.

## 12. Open questions

1. Exact extraction boundary of the checkpoint-replay seek core out of `MainViewModel` (the review
   found proposals disagreed on its current shape/name — confirm at implementation start).
   **Resolved (B4):** the core is `CS2DemoKit.Parser.EntityTracking.EntitySeekService` — a package
   type, already standalone. `MainViewModel` owns only an instance, and was not modified by B4.
   See `plans/B4-export.md` D1.
2. Windowless GPU backend choice (ANGLE/EGL vs native GL vs SkiaSharp Vulkan) — resolved by the
   time-boxed C2 spike; macOS Metal support is a separate later decision.
3. ~~Whether `AnnotationTrack` envelope drag-editing lands in B2 or B3 (UX dependency on timeline).~~
   **Resolved in B2** (plan decision D8): B2 ships `AnnotationTrack`'s markers plus envelope authoring
   through the toolbar (Always / Fade / Custom, and "pin to now"); **B3 adds drag-to-edit** on the
   timeline, using the `DocDelta.Replace` API B2 exports. *The question is resolved; the drag-to-edit
   half is not built — see [§0](#0-status--what-is-still-open) O5.*
4. WebCodecs sink priority for WASM export — after desktop ships, gauge demand.
5. Voice-audio sync (CS:DM's beloved feature) — natural future track/layer; out of scope here.
