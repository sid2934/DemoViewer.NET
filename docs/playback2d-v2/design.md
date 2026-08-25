# Playback2D v2 — Architecture & Feature Design

**Status:** Proposed (design review) · **Branch:** `feature/playback2d-v2` · **Date:** 2026-08-24

This document is the design for the 2D playback window rework: drawable annotations (static and
time-anchored), video export of the 2D playback, a proper multi-level map model, follow-player,
keybinds, and a scrubbable timeline — on an architecture built to keep absorbing features for years.

It was produced from a structured review: five codebase reconnaissance passes, five external
research passes (competitor tools, annotation data models, Avalonia rendering options, video-export
tech, replay-viewer prior art), three independent architecture proposals (evolutionary /
clean-core compositor / plugin-first), and a two-judge scoring panel. Section 3 records the
decision; the rest is the synthesized design.

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

Plus two cross-cutting requirements: **per-feature enable/disable** and **long-term extensibility**
with bounded maintenance cost. The Browser/WASM target must degrade gracefully and explicitly.

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
  LINQ in radar resolution — tolerable at UI rate, hostile at export rate.

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
- `CompositionCustomVisual` is rejected: it bypasses hit-testing, needs cross-thread messaging, has
  an open Decorator-hosting bug (#19436), and its off-UI-thread benefit evaporates on
  single-threaded WASM. The layer contract (pure `Draw(SKCanvas)`) keeps it available as a future
  desktop-only promotion if UI-thread jank ever demands it.
- A CPU `SKSurface` → `WriteableBitmap` fallback path is always built (used when the Skia lease is
  unavailable) and doubles as the headless golden-test path, so it can't rot.
- The proven value types survive: `ViewportTransform` and `SliceCamera` move to the core
  **verbatim** (including per-slice `ManualOverride` semantics); `FloorSplitter` survives as the
  floor-detection authority; the VM data pipeline, XAML HUD chrome, module registration, and all
  LiveSync seams are untouched.

---

## 4. Component map

New project **`DemoViewer.NET.Playback2D.Core`** — references SkiaSharp only. No Avalonia, no
parser, no Modules.Abstractions. Enforced by an architecture test (§10).

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
│ SceneExportSession ── ISceneFrameSource, IFrameSink, CameraScript          │
└────────────────────────────────────────────────────────────────────────────┘
              ▲ consumed by
┌──────────────────────── App (Modules/Playback2D + services) ───────────────┐
│ Playback2DTabViewModel (kept) ── SceneFrameBuilder (extracted BuildFrame)  │
│ Scene2DHost : Control (~300 loc)  ICustomDrawOperation + lease, RAF loop,  │
│                                   InputToolRouter (PanZoom | Draw | Erase) │
│ TimelineControl : XAML  (rounds band + scrub + tracks)                     │
│ TrackerFrameSource (private checkpoint replay → Scene2DFrame)              │
│ FfmpegFrameSink / ManagedGifSink / (later) WebCodecsSink; export dialog    │
│ Playback2DKeymap; selectable player cards; XAML HUD (kept)                 │
└────────────────────────────────────────────────────────────────────────────┘
```

Dependency direction is strictly downward: the module adapts `IPlaybackSnapshot`/tracker state into
`Scene2DFrame`; Core turns `Scene2DFrame` into pixels. The on-screen control, the video exporter,
and future highlight generation are three thin consumers of the same core.

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
per `LayerCacheHint`; the per-frame path is allocation-free.

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
- **Persistence:** JSON sidecar `<demo>.dvann.json` when the demo's directory is writable, else
  app-data keyed by demo hash. Versioned schema, tolerant reader, unknown fields preserved. The file
  records **demo identity (hash) and clock identity (DV frame clock)** so a re-parse with different
  frame segmentation can detect rather than silently corrupt anchored strokes. Tool prefs (color,
  width, last tool) go in a new binder-safe `Playback2DSettings` on `AppSettings` — and must be
  added to `SettingsService.WriteInMemory` or WASM writes silently vanish.

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
expected to pull forward into the timeline phase rather than deferred (§9 risk 4).

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
        IProgress<ExportProgress> progress, CancellationToken ct);
}
```

Rules locked in by the review:

- **Export never touches the shared app clock.** `RequestSeekToFrame` moves every module and any
  LiveSync session. `TrackerFrameSource` owns a **private** tracker replay on a background thread,
  reusing the shared checkpoint-replay seek core that already backs discrete seeks (extract it from
  `MainViewModel`'s wiring rather than paying a from-zero replay to reach `StartFrame`; exact type
  boundary to be confirmed at implementation — the review flagged that one proposal misnamed it).
- Fixed timestep `dt = 1/fps` through the same layer stack (`RenderPurpose.Export`), rendering to an
  `SKSurface`, `ReadPixels` → sink. Determinism is guaranteed by §5.1, verified by golden tests.
- Export enables two Core HUD layers (`ClockLayer`, `KillFeedLayer` — cheap `SKTextBlob` draws fed
  by the same pre-built kill timeline as the XAML HUD) so clips look complete without capturing
  XAML. Snapshot tests pin export-HUD rows to the same VM data to prevent dual-HUD drift.
- **Interlock:** runs under `HeavyJobGate` and **refuses to start** while a LiveSync session or reel
  job is active (SyncEngine has no pause semantics; refusal is specifiable today).
- **Sinks and formats:** `FfmpegFrameSink` via FFMpegCore (MIT) piping rawvideo RGBA over stdin to
  an ffmpeg **subprocess** — the FSF "separate programs" posture keeps GPL clean. Defaults:
  **WebM/VP9** (`-c:v libvpx-vp9 -pix_fmt yuv420p -an`, present even in LGPL builds), MP4/H.264
  (`libx264 -crf`, needs GPL build) for compatibility, GIF via two-pass `palettegen`/`paletteuse`.
  Presets 720p/1080p/map-native × 30/60 fps. Progress is frames-done based, cancel kills ffmpeg.
- **ffmpeg acquisition ladder:** `FfmpegDependency.Locate()` (existing) → download-on-demand pinned
  BtbN build with license text shown and source link → `ManagedGifSink` (ImageSharp, Apache-2.0 for
  OSS) as the no-ffmpeg floor. Never Xabe.FFmpeg (CC BY-NC-SA). Settings mirror `HighlightsSettings`.
- **WASM:** export is feature-gated off in v1. The `IFrameSink` seam is where a
  WebCodecs + webm-muxer backend plugs in later (same WebM/VP9 output on both targets by design);
  a chunked ImageSharp GIF encode is the browser stretch goal after desktop ships.

---

## 6. Feature designs

### 6.1 Annotations
`Draw`/`Erase` tools + `AnnotationDocument` + wet/dry `AnnotationLayer`; color/width picker as XAML
chrome writing `AnnotationStyle` defaults into `Playback2DSettings`. Static = `TimeEnvelope.Static`;
dynamic = stamped at `CurrentTick` with configurable envelope, editable on the timeline. Shape kit
from v1 schema (Freehand first; Line/Arrow/Rect/Ellipse/Text follow without schema breaks because
`Kind` exists from day one). Entity-anchored elements give tracked telestration no video tool can
match. Undo/redo: gesture marks, squash-to-mark, `BailToMark` on Esc.

### 6.2 Video export
Export dialog builds `ExportRequest` (range from timeline selection or current round; camera
Fixed/Follow/Mirror; layer toggles — annotations/vision/HUD on or off per export). Runs per §5.7.
Later highlight generation = a `CameraScript` emitter over `GetEventTimeline` ranges + the same
session; zero new rendering work.

### 6.3 Multi-level maps
`MapSpace` + `ILevelLayoutPolicy`. `StackedLayout` preserves today's all-floors view; `SingleLayout`
adds a level strip (manual pick) and **AutoFollow** — switch to the followed player's level via
`LevelFor(z)` with hysteresis. Explicit per-level radar binding with a visible no-radar state.

### 6.4 Follow-player
The Attributes `ItemsControl` becomes selectable; selection sets `FollowedSlot` on the VM, which
(a) drives `FollowPlayerRig` (deadzone + exponential settle) and auto level switching, and
(b) calls the existing `NotifyFollowSlotChanged` → `NotifySpectateTarget` → LiveSync
`SetDesiredSpectator` chain. UI shows **"requested," not "confirmed"** (spectate has no readback);
name-based targeting keeps its known rename limitation until `SpectateBySteamId` is consumed
upstream — surface SteamId through the chain when that capability reports true.

### 6.5 Keybinds
Declarative `Playback2DKeymap` (action → gesture table, conflict-checked at registration,
future-rebindable), bound on the focusable host: Space play/pause, ←/→ step, ↑/↓ speed, Q/E round
nav, F follow-cycle, D draw, E erase, Esc exit/bail, Ctrl+Z / Ctrl+Shift+Z / Ctrl+X (CS:DM parity).
All playback mutations route through `PlaybackController` commands / capability-gated
`IModuleContext.Request*` — the exact surfaces `SyncStateObserver` observes; a parallel path would
silently bypass LiveSync. Must not collide with shell bindings (Ctrl+1..9, Ctrl+P/O/B/W).

### 6.6 Timeline
Per §5.6. Feature-gated chrome; markers only for events the demo has (`AvailableEventNames`).

### 6.7 Feature gates
New `FeatureCatalog` entries, all `SubFeature` with `ParentId "tab.playback2d"` (cascade off with
the tab, per-category defaults, user-overridable): `playback2d.annotations`, `playback2d.timeline`,
`playback2d.levels.auto`, `playback2d.follow`, `playback2d.export` (additionally AND
`!OperatingSystem.IsBrowser()`, like `chrome.livesync`). A gated-off feature's layers are skipped by
the compositor and its tools/chrome hidden. **Ids are persisted keys — chosen once, never renamed.**
`TabId "playback2d.viewport"` and `"tab.playback2d"` stay stable; bump `Playback2DModule.
ContractVersion` for any additive context consumption.

---

## 7. WASM statement (explicit)

Works fully in browser: core rendering (custom op under the browser compositor, WebGL2→WebGL1→
software chain), annotations **in-session only**, levels, 2D follow, keybinds, timeline. Degraded or
absent: annotation persistence (no filesystem — stated in UI as session-only), video export (gated
off; WebCodecs sink later, chunked GIF as stretch), LiveSync/in-engine follow (hooks unset, already
null-tolerated). The allocation-free render path is what keeps the single thread honest. Any new
persisted settings keys must be added to `SettingsService.WriteInMemory`.

---

## 8. Migration plan

**Kept as-is:** `ViewportTransform`, `SliceCamera`, `FloorSplitter` internals, `VisibilityAnalyzer`
/ vision BVH, `MapAssetLoader` (decode retargeted `Bitmap`→`SKImage`), the entire VM data pipeline
(`OnAdvanced` copy-out, kill timeline, bomb/clock derivation, ring tracker, map-asset lifecycle),
XAML HUD, module registration/lifecycle, all of LiveSync, `PlaybackController`.
**Ported mechanically:** every `DrawSection` helper's math into `ISceneLayer.Render`.
**Extracted:** `BuildFrame` → `SceneFrameBuilder`; the checkpoint-replay seek core out of
`MainViewModel` wiring. **Rewritten:** the viewport control shell (input, band arithmetic,
invalidation) → `Scene2DHost` + compositor; `ResolveRadarImage` → `MapSpace`.
**Deleted:** nothing user-visible until parity is proven — the old control is retained one release
behind an internal toggle.

Two parallel tracks so the port is never a visible-progress desert:

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **A (UX, on the *current* control — renderer-independent)** | A1 | `TimelineControl` + round/kill/bomb tracks; keymap + keybinds; selectable player cards → follow + spectate; binary-search `SeekToTick` | Scrub + keys + follow-by-card shipped | 1.5 wk |
| **B (core)** | B0 | Core project; move structs; `SceneFrameBuilder` extraction; golden-image headless tests pinning current output | Frames buildable without VM; goldens green | 1.5 wk |
| | B1 | Port 7 layers to `SKCanvas`; `Scene2DHost` + draw op + CPU fallback; `MapSpace`/panes replicating stacked bands; deterministic-time plumbing | Pixel-parity (± reviewed text metrics) vs B0 goldens; old control behind toggle | 3 wk |
| | B2 | Annotations: document + deltas + undo, Draw/Erase tools, wet/dry layer, color picker, envelopes + timeline markers, sidecar + app-data persistence | Draw/erase/undo survive seek, zoom, level switch, tab deactivate | 2.5 wk |
| | B3 | Levels: `SingleLayout`, level strip, AutoFollow + hysteresis, buffer resets; `AnnotationTrack` time-edit handles | Levels shipped | 1 wk |
| | B4 | Export: seek-core extraction, `SceneExportSession`, ffmpeg sink + GIF floor, dialog, settings, gates, HUD layers + snapshot tests | 1080p round export ≥ realtime speed on desktop; cancel-safe; refuses under LiveSync | 2 wk |
| | B5 | Polish: WASM verification pass, feature-flag audit, keybind conflict audit, docs, old-control removal (next release) | Release | 1 wk |

**Honest total: ~12.5 person-weeks** (the review explicitly corrected more optimistic estimates).
Track A lands first and independently; every B phase ships behind its gate.

---

## 9. Risk register

| # | Risk | L | I | Mitigation |
|---|---|---|---|---|
| 1 | Port regresses visuals (esp. text metrics vs `FormattedText`) | H | M | B0 goldens gate B1 on the existing headless harness; old control retained one release behind toggle; text differences reviewed, not auto-failed |
| 2 | Render-thread races against layer caches | M | H | Advance/Render purity split; render gate; ops consume immutable snapshots; SDK has no VM references; stress test in CI |
| 3 | Export nondeterminism (wall-clock leak) | L | H | Banned-API architecture test in Core; all motion consumes injected `SceneTime`; golden export frames |
| 4 | Scrub feel limited by debounced checkpoint re-seek on long demos | M | M | Ships with debounce + latest-wins; checkpoint-density/near-playhead cache expected to pull into Track A follow-up — treated as likely, not optional |
| 5 | Level set shifts under floor-tagged annotations (`FloorSplitter` keeps learning) | H | L | `ZMin`-keyed level identity; `MapSpace` rebuild event remaps panes + annotations; never store slice index |
| 6 | Dual HUD drift (XAML live vs Skia export) | M | L | Export HUD minimal, fed by identical VM data; snapshot tests |
| 7 | ffmpeg licensing/distribution missteps | L | M | Subprocess-only (FSF separate-programs); WebM/LGPL default; license text + source link on download; never Xabe |
| 8 | Export while LiveSync active corrupts sync | L | H | Private tracker + `HeavyJobGate` + hard refusal while a sync session or reel job is active |
| 9 | Skia lease unavailable / API drift | M | M | CPU `SKSurface`→`WriteableBitmap` fallback always built = headless test path, can't rot |
| 10 | Entity-anchored strokes break on roster reseed / slot recycling | M | M | Anchor by SteamId, resolve per frame; hide while unresolvable; `Playback2DRosterReseedTests` pattern extended |
| 11 | WASM perf (single thread) | M | M | Allocation-free path, `SKPicture` caches, frame-budget test; export absent |
| 12 | Undo scope creep | L | M | History lives only in `AnnotationDocument`; camera/playback/selection excluded by contract |

---

## 10. Enforcement & testing

- **Architecture test:** Core assembly references no Avalonia/parser types; no
  `DateTime.Now`/`UtcNow`/`Stopwatch`/`Random` usage (banned-API scan).
- **Golden-image tests:** existing headless Skia harness (`HeadlessSession`/`ZRadarRenderTests`
  pattern) pins B0 output; B1 must match; export frames get their own goldens (they share the CPU
  fallback path, so they run in CI without a GPU).
- **Determinism test:** two export runs of the same request produce byte-identical frame hashes.
- **Behavioral tests carried forward:** interpolation snap/glide/prune, roster reseed, seek-push
  coalescing, floor validation probes — all keep passing against the new host.
- **Snapshot tests:** export HUD rows vs XAML HUD data; annotation schema round-trip with unknown
  fields preserved.

## 11. Open questions

1. Exact extraction boundary of the checkpoint-replay seek core out of `MainViewModel` (the review
   found proposals disagreed on its current shape/name — confirm at implementation start).
2. Whether `AnnotationTrack` envelope drag-editing lands in B2 or B3 (UX dependency on timeline).
3. WebCodecs sink priority for WASM export — after desktop ships, gauge demand.
4. Voice-audio sync (CS:DM's beloved feature) — natural future track/layer; out of scope here.
