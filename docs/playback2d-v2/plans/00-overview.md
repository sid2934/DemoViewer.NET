# Playback2D v2 plan set overview (integration record)

**Design authority:** [`../design.md`](../design.md) · **Branch:** `feature/playback2d-v2`
**Status:** nine phase plans reconciled. Each plan now opens with an
`## Integrator corrections (BINDING)` block; where a plan body and its corrections block disagree,
**the corrections block wins**, and this document is the registry both point at.

---

## 1. Master index

| Plan | Track | Owns | Exit criterion | Effort |
|---|---|---|---|---|
| [`A1-timeline-keybinds-follow.md`](A1-timeline-keybinds-follow.md) | A (UX on the current control) | `TimelineControl` + round/kill/bomb tracks, `ITimelineTrack` contract, `Playback2DKeymap`, selectable player cards → follow + spectate, binary-search `SeekToTick`, the module feature-gate seam | Scrub + keys + follow-by-card shipped | 1.5 wk |
| [`B0-core-pipeline-foundations.md`](B0-core-pipeline-foundations.md) | B (core) | Core + Pipeline projects, `Modules.Abstractions` Avalonia split, moved value types, `Scene2DFrame`/`SceneTime`, `SceneFrameBuilder`, `CpuSurfaceProvider`, `SceneFixture`, golden corpus + comparator, architecture tests | Frames render to PNG with zero Avalonia dependencies; goldens green | 2 wk |
| [`B1-compositor-port.md`](B1-compositor-port.md) | B | Seven layers → `SKCanvas`, `SceneCompositor` + picture caches, `Scene2DHost` + draw op + CPU fallback, `MapSpace`/`PaneSet`/`StackedLayout`, camera rigs, vision solver seam, allocation cleanup, bench harness | Pixel-parity vs B0 goldens; p99 ≤ budget; old control behind toggle | 3 wk |
| [`B2-annotations.md`](B2-annotations.md) | B | `AnnotationDocument` + deltas + undo, perfect-freehand port, `IPointerTool`/`InputToolRouter`/`PanZoom`/`Draw`/`Erase`, wet/dry `AnnotationLayer`, `AnnotationTrack`, `AnnotationStore` sidecar | Draw/erase/undo survive seek, zoom, level switch, tab deactivate | 2.5 wk |
| [`B3-levels.md`](B3-levels.md) | B | Level identity + rebuild/remap, hysteresis + AutoFollow, `SingleLayout` + level strip, per-level radar binding, buffer resets on crossing, envelope drag handles | Levels shipped | 1 wk |
| [`B4-export.md`](B4-export.md) | B | `SceneExportSession`, `TrackerSceneSnapshot`, ffmpeg + GIF sinks, ffmpeg ladder, HUD layers, `HeavyJobGate` export session, export dialog | 1080p round export ≥ realtime on CPU; cancel-safe; refuses under LiveSync | 2 wk |
| [`B5-polish-wasm.md`](B5-polish-wasm.md) | B | Feature-flag + keybind audits, `Playback2DSettings` + `WriteInMemory` flattening, WASM verification + `wasm-build` CI job, docs, old-control removal plan | Release | 1 wk |
| [`C1-cli.md`](C1-cli.md) | C (parallel with B2–B4) | `dv2d` tool (`render`/`export`/`bench`/`golden`/`fixture`), `TrackerFrameSource`, `HeadlessSceneRenderer`, golden corpus manifest, CI golden + budget gates | Any tick → PNG in <1 s with no app launch; CI uses `dv2d` | 1 wk |
| [`C2-gpu-provider.md`](C2-gpu-provider.md) | C | `GpuSurfaceProvider` (ANGLE/EGL), probe + override precedence, SSIM/perceptual parity, `render-backends` CI matrix, ANGLE packaging + notices | GPU export ≥ 2× realtime at 1080p; CPU parity within perceptual tolerance | 1.5 wk |
| [`P1-perf-instrumentation.md`](P1-perf-instrumentation.md) | P (post-C, measurement only) | `ISceneProfiler` seam on `SceneCompositor`, `ScenePerfRecorder` + `PerfReport`, `--perf` on `dv2d bench`/`export`, additive `perf` JSON block | Per-layer / per-stage breakdown of an export; §6 gates unchanged with the flag off | 0.5 wk |
| [`P2-export-throughput.md`](P2-export-throughput.md) | P (post-P1, spends its answer) | `VideoEncoder`/`EncoderLadder`/`EncoderSelector` + the verifying `FfmpegEncoderProbe`, `ExportQuality` presets per encoder, `--encoder`/`--quality`, additive export JSON keys, the export-node seams (design only) | An export picks a verified hardware encoder where one exists, degrades honestly to tuned software where none does, and the determinism gate is unchanged | 0.5 wk |
| [`P3-test-tiers.md`](P3-test-tiers.md) | P (workflow only, no product code) | `fast`/`standard`/`full` tiers over `[Category]`, the cost-tag vocabulary (`Budget` `Environmental` `Gpu` `Integration` `RealDemo` `Render`), `scripts/test.sh`, the linked `TestTiers` + `TestTierContractTests` guard, and the TUnit 0.25.21 / MTP 1.7.1 filter-grammar findings every other plan's `--treenode-filter` lines depend on | In-flight default runs in 54 s where running everything took 205 s; PR and `main` lanes provably unchanged, by count | 0.5 wk |

Total ≈ 16 person-weeks, ~12.5 on the A+B critical path (design §9).

---

## 2. Dependency graph

```
A1 ──────────────────────────────────────────────┐  (ships first, on the CURRENT control)
  │ ITimelineTrack/TimelineMarker/ITimelineData   │
  │ Playback2DKeymap · IModuleContext.Features    │
  │ FrameIndexAtTick · FollowedSlot               │
  ▼                                               │
B0 ──┬── B1 ──┬── B2 ──┬── B3                     │
     │        │        │     ▲                    │
     │        │        └─────┘ (B3 needs B2's doc)│
     │        │                                   │
     │        └── B4 ◄── C1 (TrackerFrameSource)  │
     │        │                                   │
     │        └── C1 ──┬── C2                     │
     │                 │                          │
     └── C1, C2, B4 (IRenderSurfaceProvider, fixtures, comparator)
                                                  │
B5 ◄── everything ───────────────────────────────┘
```

**Hard edges**

| Blocked phase | Blocked on | What it needs |
|---|---|---|
| B1 | B0 | `Scene2DFrame`, `SceneTime`, `ISceneLayer`, `SceneRenderContext`, `SceneCompositor`, `IRenderSurfaceProvider`, `SceneFrameBuilder`, `SceneFixture`, goldens |
| B1 | A1 | the timeline types it moves to Core (namespace rewrite only) |
| B2 | B0, B1 | `LevelPane`/`MapSpace`, `Scene2DHost` + `PanZoomGesture`, layer contract. **Groups 1 and 4 (document model, freehand port, persistence) need neither and can start the day B0 lands.** |
| B3 | B1, B2 | `MapSpace`/`PaneSet`/layers; `AnnotationDocument` + `ApplyMigration`; A1's timeline for T9 |
| B4 | B0, B1, C1 | layer stack + `TrackerFrameSource`. If C1 slips, B4 writes `TrackerFrameSource` to the §3 signature instead. |
| C1 | B0 (+B1 for `bench`, B4 for `export`) | fixtures, compositor, comparator, benchmark types |
| C2 | B0 (+C1 for flags, B4 to *close* the ≥2× criterion) | provider seam, corpus, comparator |
| B5 | B1–B4 + A1 | audits everything; **B5's settings/gate contracts are consumed by B2–B4, so its `Playback2DSettings` shape must be agreed before B2 starts** (it is, in §3) |

**Parallelism.** A1 is independent of all of B/C and ships first. After B0: B1 is the long pole;
C1 can start alongside B1 (fixture-only paths need no compositor port); C2's Stage 0 is entirely
no-GPU work that can run alongside B2–B4. B2's Groups 1 and 4 parallelise with B1.
**No cycles**: the only apparent one, B4 ↔ C1, is resolved by assigning `TrackerFrameSource` to C1.

---

## 3. Canonical shared-API registry

Anything referenced by two or more plans. **One signature, one owner.** Where design §5 pinned a
shape it wins; otherwise the better of the two proposals was chosen and both plans were edited.

### 3.1 Assemblies and namespaces

| Assembly | Path | Namespaces |
|---|---|---|
| `DemoViewer.NET.Playback2D.Core` | `src/Playback2D/DemoViewer.NET.Playback2D.Core` | `…Core` (frame + value types), `…Core.Compositing`, `…Core.Rendering`, `…Core.Levels`, `…Core.Cameras`, `…Core.Vision`, `…Core.Layers`, `…Core.Annotations`, `…Core.Ink`, `…Core.Input`, `…Core.Timeline`, `…Core.Export`, `…Core.Hud` |
| `DemoViewer.NET.Playback2D.Pipeline` | `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline` | `…Pipeline`, `…Pipeline.Assets`, `…Pipeline.Vision`, `…Pipeline.Annotations`, `…Pipeline.Frames`, `…Pipeline.Export`, `…Pipeline.Ffmpeg`, `…Pipeline.Hud`, `…Pipeline.Goldens`, `…Pipeline.Headless`, `…Pipeline.Benchmarking` |
| `DemoViewer.NET.Playback2D.Tests` | `src/Playback2D/DemoViewer.NET.Playback2D.Tests` | one project for Core **and** Pipeline; root ns `DemoViewer.NET.Playback2DTests`. `…Core.Tests` / `…Pipeline.Tests` **do not exist**. |
| `dv2d` | `tools/DemoViewer.NET.Playback2D.Cli` (+ `.Tests`) | `DemoViewer.NET.Playback2D.Cli` |
| `DemoViewer.NET.Modules.Abstractions.Ui` | `src/App/DemoViewer.NET.Modules.Abstractions.Ui` | keeps ns `DemoViewer.NET.Modules.Abstractions` (no call-site churn) |

### 3.2 Frame and time (B0)

`Scene2DFrame` is a **sealed class**, double-buffered by `SceneFrameBuilder`, valid until that
builder's next `Build`. Members: `Time` (`SceneTime`), `Markers`, `AreaEffects`, **`Trails`**,
`Bomb`, `KillFeed`, `GameInfo`, **`Map`** (`SceneMapInfo`), `Vision`, `FollowSlot`, `static Empty`.
**No `Toggles`** (overlay visibility is `ISceneLayer.IsEnabled`) and **no `Levels`** (levels are
derived from `Map.SectionHeights` + authoritative floors by `MapSpaceFactory`).
Bounds are `WorldBounds`, not `SKRect`.

```csharp
public readonly record struct SceneTime(int Tick, int FrameIndex, double DemoSeconds,
    double DeltaSeconds, bool IsDiscontinuity);           // design §5.1 verbatim
public enum RenderPurpose { Interactive, Export, Thumbnail }

public readonly record struct PlayerMarker(int Slot, int Team, float WorldX, float WorldY,
    float WorldZ, float YawDegrees, RingState Ring, double RingAlpha, string Label, bool IsAlive,
    float PitchDegrees = 0, float DuckAmount = 0, ulong SteamId = 0);   // SteamId added for B2/B4

public readonly record struct KillFeedRow(int Tick, string Attacker, string? Assister,
    string Victim, string Weapon, bool Headshot, bool Penetrated, bool NoScope, bool ThroughSmoke,
    bool AttackerBlind, bool AttackerInAir, bool AssistedFlash);        // CORE: B4 must not redeclare
```

`IsDiscontinuity` is authored by `SceneFrameBuilder`; `Scene2DHost`'s frame-index-delta fallback is
a safety net only.

### 3.3 Layers and compositor (B0 declares, B1 extends)

```csharp
public interface ISceneLayer : IDisposable      // …Core.Compositing
{
    string Id { get; } LayerSlot Slot { get; } int Order { get; }
    LayerCacheHint Cache { get; } bool IsEnabled { get; set; }
    int ContentVersion { get; }                                   // declared in B0, used by B1/B2
    bool Advance(in SceneTime time, Scene2DFrame frame);          // UI thread; true = keep RAF armed
    void Render(SKCanvas canvas, SceneRenderContext ctx);         // pure
}
```

`SceneRenderContext` is one type, extended in place: B0 declares `Frame`, `Time`, `Transform`,
`PaneBounds`, `LevelIndex`, `LevelMinZ`, `LevelMaxZ`, `Purpose`, `Palette`, `RenderScaling`,
`IsSingleLevel`, `BelongsHere(double)`; **B1 adds** `Pane` (`LevelPaneSnapshot`) + `LevelIndexFor`;
**B3 adds** `Levels` (`MapSpace`) + `LevelCrossings`.
`SceneCompositor` members are `Add` / `Remove` / `Find` / `SetEnabled` / `Advance` /
`Render(SKCanvas, in SceneRenderContext)` (B0, single pane) / `Render(SKCanvas, in SceneSubmission)`
(B1 overload, multi-pane) / `InvalidateCaches` / `Dispose`. Sort key `(Slot, Order, Id)`.

**Layer ids** (persisted keys). All **eleven**: `playback2d.radar`, `playback2d.trails`,
`playback2d.areaeffects`, `playback2d.vision`, `playback2d.markers`, `playback2d.bomb`,
`playback2d.floorlabel` (B1), `playback2d.annotations` (B2), `hud.roster` (D3b), `hud.clock`,
`hud.killfeed` (B4). `hud.roster` was missing from this list until D6 round 3. The registry is what a
reader checks a hand-written layer array against, so an id absent from it is an id that can be absent
from four other places without anyone noticing (G-3).

This is registration order, not draw order: the compositor sorts on `(Slot, Order, Id)`, which puts
`playback2d.annotations` (Overlay/100) ahead of `playback2d.floorlabel` (Hud/60). The one table these
ids actually live in is `SceneLayerCatalog.SceneStackIds`; `SceneLayerListParityTests` asserts every
other list in the repository against it, so this paragraph is prose about a fact a test owns.

### 3.4 Levels (B1 declares, B3 fills in)

```csharp
public readonly record struct MapLevelId(int Key);                       // never a level INDEX
public sealed class MapLevel { MapLevelId Id; string Name; double ZMin, ZMax;
    SKImage? Radar; string? RadarImageName; bool HasRadar; double Span, MidZ; bool Contains(double); }
public enum LevelDisplayMode { Stacked, Single, SideBySide }             // SideBySide reserved
public sealed class MapSpace {
    const double LevelQuantum = 64.0;  static double QuantizeZ(double);
    IReadOnlyList<MapLevel> Levels; RadarBindingQuality RadarBinding; event Action? LevelSetChanged;
    MapLevel LevelFor(double worldZ);  int LevelIndexFor(double worldZ);
    MapLevel LevelFor(double worldZ, MapLevelId? previous);              // sticky (B3 fills the band)
    LevelSetChange Rebuild(IReadOnlyList<FloorSlice> bands,
        IReadOnlyList<SKImage?>? radarByLevel = null,
        RadarBindingQuality quality = RadarBindingQuality.None);
    LevelSetChange LastChange; MapLevel? ById(MapLevelId); int IndexOf(MapLevelId); }
```

`LevelPane.Camera` is a **public field** (B2 mutates it in place) with a justified `CA1051`
suppression. **`PaneSet` is the only pane-lifetime owner**; B3's "`LevelPaneStore`" is added
behaviour on it (`RetainUnarranged`, `ResetAll`, `TryGetCamera`), not a second class.
`ILevelLayoutPolicy.Arrange(MapSpace, LevelDisplayMode, SKSize)` keeps the design's exact signature.
`FloorSplitter`/`FloorSlice` move into `…Core.Levels` in B1 T1.

### 3.5 Timeline (A1 declares, B1 moves to `…Core.Timeline`)

`ITimelineTrack` has **six** members: `Id`, `DisplayName`, `bool IsAvailable(ITimelineData)`,
`BuildMarkers`, `BuildBands`, `event Action? MarkersChanged`. Every implementer ships all six:
`RoundTrack`, `KillTrack`, `BombTrack` (A1) and `AnnotationTrack` (B2).
**The timeline x-axis domain is FRAME INDEX** (design §5.6). `TimelineMarker` carries both
`FrameIndex` and `Tick`; tick-stamped events convert once via `ITimelineData.FrameIndexAtTick` and
markers resolving to `-1` are dropped. Track ids are bare words: `round`, `kill`, `bomb`,
`annotation`. B3's `TickAxis` is drag-math only and must not lay out A1's control.

### 3.6 Input tools (B2 owns; B1 ships only `PanZoomGesture`)

`IPointerTool` is design §5.5 verbatim (four methods, **no wheel member**; wheel is
`InputToolRouter.OnWheel`). `IToolServices`, `ToolPointerEvent`, `InputToolRouter`, `PanZoomTool`,
`DrawTool`, `EraseTool` live in `…Core.Input`. B1 must not ship a competing tool abstraction.

### 3.7 Render surfaces (B0 declares, C2 extends): `…Core.Rendering`

```csharp
public enum RenderBackend { CpuRaster, OpenGl, Angle, Vulkan }   // Vulkan declared, unreachable in v1
public interface IRenderSurfaceProvider : IDisposable            // design §5.8 verbatim
{ RenderBackend Backend { get; } SKSurface CreateSurface(SKSizeI size); void Flush(SKSurface s); }
```
`CpuSurfaceProvider` (B0) · `SceneRenderer` (B0) · `HeadlessSceneRenderer` (`…Pipeline.Headless`),
**one** headless entry point, **not** a second render path; **owned by B1 as of correction 24**, which
is the multi-pane form (`SceneCompositor` + `IRenderSurfaceProvider` + `ILevelLayoutPolicy` +
`ScenePalette`, deriving levels and drawing a `SceneSubmission`). C1's single-pane facade over
`SceneRenderer` is withdrawn; `dv2d`'s convenience members live on B1's class · `RenderBackendPreference`, `RenderSurfaceProbe`,
`RenderBackendPreferenceParser`, `RenderSurfaceProviderFactory`, `GpuSurfaceProvider` (C2).
Precedence: explicit argument → CLI flag → `DV2D_RENDER_BACKEND` → `AppSettings.Playback2D
.RenderBackend` → auto-probe. B1's on-screen CPU fallback draws into a locked `WriteableBitmap`
framebuffer and deliberately does **not** use `CpuSurfaceProvider`.

### 3.8 Export (B4 owns; design §5.7 verbatim except where noted)

`ISceneFrameSource`, `IFrameSink`, `ExportRequest`, `SceneExportSession.RunAsync(...)` exactly as
§5.7. `CameraScript.Fixed` takes `IReadOnlyDictionary<MapLevelId, ViewportTransform>`;
`PaneCameraSnapshot` is `(MapLevelId LevelId, ViewportTransform Transform, bool ManualOverride)`.
`IHudDataSource` **and `HudSnapshot`** are `…Core.Hud`; `KillFeedTimeline`,
`TimelineHudDataSource`, the sinks and the ffmpeg ladder are Pipeline.

**`TrackerFrameSource` is C1's** (`…Pipeline.Frames`), consumed by B4. One canonical signature:

```csharp
public TrackerFrameSource(IReadOnlyList<DemoFrame> frames, SceneFrameBuilder builder,
    int startFrame, int endFrame, int fps, double speed, int tickRate,
    Func<EntityTracker>? createTracker = null, bool throwOnNonSequentialAccess = false);
public void Prepare(CancellationToken ct);
public SceneTime TimeAt(int i); public Scene2DFrame FrameAt(int i); public int DemoFrameIndexOf(int i);
public static int FrameIndexForTick(IReadOnlyList<DemoFrame> frames, int serverTick);
```

**Design §12 Q1 is answered:** there is nothing to extract from `MainViewModel`. The seek core is
the package type `CS2DemoKit.Parser.EntityTracking.EntitySeekService`; `TrackerFrameSource` builds
its own over `() => new EntityTracker()` and never uses `MainViewModel.CreateTracker`.
`SceneFrameBuilder.Build(in SceneFrameInput)` is **unchanged**; B4 adds a Pipeline-side
`TrackerSceneSnapshot` adapter (`PawnLookup`) that presents an `EntityTracker` as
`IReadOnlyList<IPlayerState>` + `IReadOnlyEntityView`.

### 3.9 Benchmarks and goldens

**Bench (B1 owns, C1 wraps):** `ScenePipelineBenchmark`, `BenchmarkRequest`, `BenchmarkReport`,
`FrameTimeStats`, `BudgetPolicy` (`…Pipeline.Benchmarking`). C1's `SceneBenchHarness`/
`SceneBenchRequest`/`SceneBenchResult` are withdrawn. Budgets scale by `DV2D_BUDGET_SCALE`
(CI 2.0); the zero-allocation assertion is never scaled.

**Perf capture (P1 owns):** `ISceneProfiler` + `LayerPhase` + `PictureCacheOutcome` in
`…Core.Compositing` (clock-free by construction; Core is banned from `Stopwatch`), attached through
`SceneCompositor.Profiler`; `ScenePerfRecorder`, `PerfStage`, `PerfReport`, `PerfRow`, `PerfRowKind`
in `…Pipeline.Benchmarking`, consumed by `ScenePipelineBenchmark.Perf` and `SceneExportSession.Perf`.
Surfaced as `dv2d bench|export --perf` and an additive `perf` key on the `schema_version: 1` payload.
Null everywhere by default; the §6 gates run with it detached.

**Encoder selection (P2 owns):** `ExportQuality` (`Draft|Standard|Best`) + `ExportQualities`,
`VideoEncoder` + `EncoderAcceleration`, `EncoderLadder`, `EncoderSelection` + `EncoderSelector`,
`IEncoderProbe` + `EncoderProbeResult` + `FfmpegEncoderProbe` + `EncoderProbeCache`. **All in
`…Pipeline.Ffmpeg`**, none in Core. `FfmpegSinkOptions` carries `Encoder` + `Quality` in place of
`Crf` + `H264Preset`. The selection is a **per-session value**, never process-global; the probe cache
is shared and concurrent because it holds machine facts only. `ExportRequest`, `IFrameSink` and
`SceneExportSession` are unchanged, so §3.8 and design §5.7 stand.

**Goldens (B0 owns the comparator, C1 the corpus):** `GoldenImageComparer`, `GoldenTolerance`
(`ByteExact` / `DefaultPerceptual` ≡ `CrossBackend`), `GoldenComparison`, `CreateDiffPng` in
`…Pipeline.Goldens`; C2 implements SSIM **inside** it. `GoldenCorpus`, `GoldenCorpusEntry`,
`GoldenBudget` and `manifest.json` are C1's. **`TestSupport` gains no imaging API.**

**Corpus layout (canonical for every phase):**
```
tests/fixtures/playback2d/{README.md, manifest.json}
tests/fixtures/playback2d/scenes/<name>.scene.json
tests/fixtures/playback2d/annotations/<name>.dvann.json
tests/fixtures/playback2d/goldens/{cpu,gpu}/<name>@<w>x<h>.png
```
No `tests/goldens/`, no `…/golden/`, no `…/goldens/export/`. Canonical entry names:
`synthetic-empty`, `synthetic-tenplayers`, `synthetic-utility`, `fitmap-mirage-eco`,
`duel-mirage-b`, `mirage-single-level`, `nuke-multilevel`, `nuke-multilevel-noradar`,
`nuke-single-upper`, `bomb-planted-inferno`, `annotated-mirage-b`, `full-scene-budget`.
`SceneFixture` = `SchemaVersion, Frame, SceneTime Time, ViewportTransform Camera, SKSizeI Size,
string? MapName, string? MapVersion, JsonElement? Annotations, SourceDemoId, Notes` +
`Load`/`Save`.

### 3.10 App-side contracts

**Feature ids** (persisted keys, never renamed), one contiguous `_catalog` block after
`analysis.breakpoints` and before `// ---- CHROME`, in this order; each phase inserts its own row:
`playback2d.annotations` (B2) · `playback2d.timeline` (A1) · `playback2d.levels.auto` (B3) ·
`playback2d.follow` (A1) · `playback2d.export` (B4). All `SubFeature`, `ParentId
"tab.playback2d"`, `GroupId null`, `Defaults(true, true, true)`.

**Gate seam (A1 ships it, everyone uses it):** `IModuleFeatureGate` in Abstractions,
`IModuleContext.Features` (additive, default `null`, **fails open**), `ModuleContext.SetFeatures`,
`ShellModuleFeatureGate` with `DesktopOnlyIds` = the single `!OperatingSystem.IsBrowser()` site.
**No phase injects `IFeatureGate` into a tab view-model.** Core/Pipeline/`dv2d` read no gates at all
(design §7.7).

**`IModuleContext` additive members (A1):** `TotalFrames`, `FrameIndexAtTick`, `EventFrames`,
`IsSpeedLocked`, `RequestSpeed`, `Features`. **`Playback2DModule.ContractVersion` = `1.2.0`, bumped
once for the whole release by A1**, audited by B5.

**Keymap (A1 owns):** `Playback2DKeymap.Default` / `.Active` / `.Reserved` (there is no `.All`),
`Playback2DBinding` with a `Playback2DBindingScope { Always, WhenToolActive }`. Resolved
collisions: `Q`/`E` = round nav, **`X` = erase**, `Ctrl+X` = clear all; `Space` = play/pause except
while a drawing tool is active (then hold-to-pan); `Esc` = gesture bail when a tool is active, else
clear follow. Text-input suppression is A1's single global rule, not a per-binding flag.

> **`RenderBackend` is listed below but has no property.** The key is still the intended name; nothing
> in the app can consume it yet, and the reasons are in the `Playback2DSettings` class doc in
> `AppSettings.cs`. `Playback2DSettingsConsumptionTests` carries a matching allow-list entry. Delete both
> in the commit that gives the key a consumer (design §0 **O2** / C2 Stage 1).

> **Editing the paragraph below.** `Playback2DSettingsConsumptionTests.RegistryKeys` parses it (from the
> `AppSettings.Playback2D` marker to the next `---`) and treats **every backticked PascalCase token in
> that span as a persisted key**. Prose mentioning a type name in backticks there becomes a key the guard
> then demands on the class. Put commentary above this line, as these two blocks are.

**`AppSettings.Playback2D`: one section, one class, every property flattened into
`SettingsService.WriteInMemory`** (B5 D3; B4's "exclude export keys" is overridden):
`LastTool`, `AnnotationColorArgb` (uint), `AnnotationWidth`, `AnnotationOpacity`,
`AnnotationDefaultVisibility` (`Always|Fade|Custom`), `AnnotationFadeInTicks`,
`AnnotationFadeOutTicks`, `AnnotationHoldTicks`, `AnnotationAnchorToEntities`,
`AnnotationAutoSave`, `AnnotationRecentColors` (`string[]`, indexed keys) · `LevelDisplayMode`,
`AutoLevelFollow` · `TimelineShowKills`, `TimelineShowBomb`, `TimelineShowAnnotations` ·
`ExportFormatId`, `ExportFps`, `ExportWidth`, `ExportHeight`, `ExportOutputDirectory`,
`ExportIncludeHud`, `ExportIncludeAnnotations`, **`ExportEncoder`** (`auto|software|<rung>`, P2),
**`ExportQuality`** (`draft|standard|best`, P2) · `RenderBackend` (`auto|cpu|gpu`),
**pinned here, deliberately NOT on the class** · `LegacyViewport`. First lander creates the class;
everyone else adds properties.

---

## 4. Canonical build-wiring delta

### 4.1 New projects (all in `DemoViewer.NET.slnx`)

| Project | Folder | Created by |
|---|---|---|
| `src/App/DemoViewer.NET.Modules.Abstractions.Ui` | `/src/App/` | B0 |
| `src/Playback2D/DemoViewer.NET.Playback2D.Core` | `/src/Playback2D/` | B0 |
| `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline` | `/src/Playback2D/` | B0 |
| `src/Playback2D/DemoViewer.NET.Playback2D.Tests` | `/src/Playback2D/` | B0 |
| `tools/DemoViewer.NET.Playback2D.Cli` (asm `dv2d`) | `/tools/` | C1 |
| `tools/DemoViewer.NET.Playback2D.Cli.Tests` | `/tools/` | C1 |

Reference edges: App → Core, Pipeline, Abstractions.Ui, `Avalonia.Skia`. Pipeline → Core,
Modules.Abstractions, `CS2DemoKit.Parser`, `CS2DemoKit.Analysis`. Core → **SkiaSharp only**.
CLI → Pipeline (**never** `src/App/*`).

### 4.2 `Directory.Packages.props`: the complete delta

| Package | Version | Added by | Why / policy |
|---|---|---|---|
| `SkiaSharp` | `2.88.9` | **B0** | Coherence-pinned to what `Avalonia.Skia 11.3.12` resolves: the Skia lease hands the draw op *Avalonia's* `SKCanvas`, and two `libSkiaSharp` natives in one process is a crash. Bump only with Avalonia. |
| `SkiaSharp.NativeAssets.Linux` | `2.88.9` | **B0** | CI/test runner native. **Not** `…NoDependencies`: B1 draws text and C1's goldens contain text. CI installs `libfontconfig1`. |
| `SkiaSharp.NativeAssets.Win32` | `2.88.9` | **C1** | `dv2d` has no Avalonia to bring natives. |
| `SkiaSharp.NativeAssets.macOS` | `2.88.9` | **C1** | ditto. |
| `Avalonia.Angle.Windows.Natives` | `2.1.25547.20250602` | **C2** | Already transitive via `Avalonia.Win32`; explicit so `dv2d` + tests get `av_libglesv2.dll`. Coherence-pinned to Avalonia. BSD-3-Clause → notices §d. |
| `FFMpegCore` | `5.2.0` (confirm latest 5.x at implementation) | **B4** | MIT; builds args + pipes to an ffmpeg **subprocess**. Never Xabe.FFmpeg. |
| `SixLabors.ImageSharp` | `3.1.12` (confirm latest 3.1.x) | **B4** | The no-ffmpeg GIF floor only. |

Central Package Management: `PackageReference` items carry **no** `Version=`. Avalonia sub-packages
move in lockstep; `SkiaSharp` and `Avalonia.Angle.*` are *derived* pins, and a lone dependabot bump of
either is a defect.

### 4.3 `.github/workflows/ci.yml`: the complete delta

| Job | Added by | Contents |
|---|---|---|
| `build` (existing) | none | untouched: `dotnet build src/App/DemoViewer.NET.Desktop -c Release` |
| `playback2d-tests` | **B0** | `apt-get install -y libfontconfig1`; run `src/Playback2D/DemoViewer.NET.Playback2D.Tests` (`Category!=Budget`). **C1 appends** its CLI test step, `dv2d golden verify --cpu`, and `dv2d bench --gate` (`DV2D_BUDGET_SCALE=2.0`) + artifact upload. **B2/B3/B4 add nothing**; their tests are in that project. |
| `playback2d-budget` | **B1** | `Category=Budget` lane, `DV2D_BUDGET_SCALE=2.0`, uploads `bench-reports/`. |
| `render-backends` (matrix ubuntu/windows) | **C2** | probe + parity, `SkipTestException` when no backend. Never required-checks for the GPU throughput number. |
| `render-backends-gpu` | **C2** | opt-in (`gpu-lane` label / `workflow_dispatch`), self-hosted. |
| `wasm-build` | **B5** | `wasm-tools` workload + `dotnet build src/App/DemoViewer.NET.Browser -c Release`. |

The App UI suite stays out of CI (`scripts/test-app-suite.sh`, OOM-prone).
New scripts: `scripts/update-playback2d-goldens.sh` (B0), `scripts/dv2d.sh` (C1).
`THIRD-PARTY-NOTICES.md`: §d perfect-freehand (B2), §e FFMpegCore + ImageSharp + "ffmpeg (not
redistributed)" (B4), §f ANGLE (C2). **Append in landing order and renumber then, not now.**

---

## 5. Corrections log

What was changed, where, and why. All edits are `Integrator corrections` blocks plus the inline
signature fixes listed.

| # | File(s) | Change | Why |
|---|---|---|---|
| 1 | B0, B1, B2, B3, C1, C2 | Project path fixed to `src/Playback2D/…`; **one** test project `…Playback2D.Tests` | B2/B4 assumed `…Core.Tests`+`…Pipeline.Tests`, B3/C1 assumed `src/Visualization/`. Four different trees for two projects. |
| 2 | B1 §2.1 (rewritten inline) | `Scene2DFrame`'s "minimum shape" replaced with B0's actual members | B1 coded against `Tick`/`FrameIndex`/`IsDiscontinuity`/`GrenadeTrails`/`MapBounds`/`ObservedExtent`/`Levels`/`Toggles`; B0 ships `Time`/`Trails`/`Map`/no-toggles/no-levels. Would not compile. |
| 3 | B0 (inline) | `SceneRenderContext` renamed to B1's members (`PaneBounds`, `RenderScaling`, `BelongsHere`, `IsSingleLevel`) + `Frame`/`Time`; `ISceneLayer` gains `ContentVersion`; `SceneCompositor` uses `Add`/`Remove`/`Find`/`SetEnabled` | Two incompatible declarations of both types (B0 vs B1); B2/B3/B4 consume them. B1's names won (richer, and what the layer ports need). |
| 4 | B0 (inline), B2 | `PlayerMarker` gains `ulong SteamId`; `SceneFrameInput` gains `SteamIdForSlot` | Design §5.4 mandates SteamId anchoring; B2's C1 conflict and B4's `FollowPlayer(steamId)` both blocked on it. |
| 5 | B4 (inline) | Pipeline `KillFeedRow` deleted → B0's Core record; `HudSnapshot` moved to `…Core.Hud` | Two records with the same name and different members; and `IHudDataSource` (Core) returning a Pipeline type cannot compile. |
| 6 | B1, B3 (inline) | Level model unified on B3's `MapLevelId` + `MapLevel` class + `LevelDisplayMode {Stacked,Single,SideBySide}` + `LevelFor(z, prev)` + `Rebuild(IReadOnlyList<FloorSlice>, …)`; `LevelPaneStore` folded into `PaneSet` | B1 and B3 declared incompatible `MapLevel`/`LevelDisplayMode`/`Rebuild`/pane stores. B3's typed id is what prevents design risk 5 (id vs index). |
| 7 | B4, C1 (inline) | `TrackerFrameSource` assigned to **C1**, one merged signature; B4 consumes | Both plans created it, with different constructors. C1 needs it a phase earlier. |
| 8 | B1, C1 | Bench harness unified on B1's `ScenePipelineBenchmark`/`BenchmarkRequest`/`BenchmarkReport`/`BudgetPolicy`; C1's `SceneBench*` withdrawn | Two harnesses for one CI gate. |
| 9 | B0, C1, C2 (inline) | One image comparator: `GoldenImageComparer`/`GoldenTolerance`/`GoldenComparison` in Pipeline, owned by B0, SSIM added by C2; C2's `ImageComparison`/`ImageDiffOptions` in TestSupport withdrawn | Three comparison implementations (B0 inline, C1, C2) for one golden policy. |
| 10 | B0, B1, B2, B3, B4, C1, C2 | Corpus layout + entry names canonicalised under `tests/fixtures/playback2d/{scenes,goldens/cpu,goldens/gpu,annotations}` | Four different golden roots; the same Nuke two-level scene authored under three names. |
| 11 | B0 (inline), C1 | `SceneFixture` gains `Time`/`Size`/`MapName`/`MapVersion` + `Load`/`Save`; `Camera` stays `ViewportTransform`, `Annotations` stays `JsonElement?` | C1 required fields B0 did not define and types (`CameraScript`, `AnnotationDocument`) that would invert the dependency direction. |
| 12 | A1 (inline), B2, B3, B4, B5 | Feature-gate seam = `IModuleContext.Features`, shipped by **A1**; no `IFeatureGate` ctor injection anywhere | A1 injected a gate into the module/VM; B5 designed a different seam; B2/B3/B4 each assumed one. Four mechanisms → one. |
| 13 | A1, B2, B3, B4, B5 | Feature ids form one contiguous block after `analysis.breakpoints`, each phase inserting its own row; B5-1 becomes an audit | B5 added all five and A1/B2/B3/B4 each added theirs, guaranteed duplicate entries. |
| 14 | B1, B2, B3, B4, B5, C2 | `Playback2DSettings`: one flat class, canonical property names (B5's), `LegacyViewport` not `UseLegacyViewport`, `RenderBackend` not `ExportBackendOverride`, no nested `Export` class, whole section flattened into `WriteInMemory` | Five phases declared overlapping/conflicting shapes; B4 and B5 disagreed on whether export keys are flattened. |
| 15 | A1, B2 (inline) | `AnnotationTrack` implements all six `ITimelineTrack` members and places markers on the frame-index axis via `FrameIndexAtTick`; track id `annotation` | B2's three-member sketch does not satisfy A1's interface; and a tick-keyed marker on a frame-index axis is a silent mis-placement. |
| 16 | B2 (inline), B3 | `AnnotationDocument.ApplyMigration(DocDelta)` added to B2 | B3 needed a non-undoable mutation entry point and both plans offered to add it. |
| 17 | B0 (inline) | `SkiaSharp.NativeAssets.Linux.NoDependencies` → `SkiaSharp.NativeAssets.Linux` + `libfontconfig1` in CI | B0 chose NoDependencies, B1/C1 need fonts; swapping later silently re-baselines every golden. |
| 18 | B0, B1, B2, B4, B5, C1, C2 | CI jobs consolidated: `playback2d-tests` (B0, extended by C1), `playback2d-budget` (B1), `render-backends` (C2), `wasm-build` (B5) | Five plans each appended their own Core/Pipeline test step. |
| 19 | B4 (inline), C1 | `SceneFrameBuilder` keeps `Build(in SceneFrameInput)`; B4 adds a Pipeline `TrackerSceneSnapshot` (`PawnLookup`) adapter | B4 demanded a re-shaped builder; B0's `Modules.Abstractions.Ui` split already makes the existing shape legal headlessly. |
| 20 | B4 (inline) | `PaneCameraSnapshot`/`CameraScript.Fixed` keyed by `MapLevelId`, not `string` | Level identity is a typed id everywhere else. |
| 21 | A1, B2, B4, B5 | `ContractVersion 1.2.0` bumped once, by A1 | A1, B2 and B5 each bumped it to the same value. |
| 22 | B3 (inline) | `TickAxis` documented as drag-math only; timeline layout stays on A1's frame-index mapping | B3's tick axis would have silently disagreed with A1's control geometry. |
| 23 | C2 (inline) | Namespace `…Core.Rendering` confirmed for the provider family; B5's `Core/Surfaces/` path corrected | Two homes for `RenderSurfaceProviderFactory`. |
| 24 | §3.7 (inline), B1, C1 | `HeadlessSceneRenderer` reassigned **C1 → B1**: one class, B1's multi-pane body, with C1's CLI conveniences (`Camera`, `RenderInto`, `RenderPng`, `Backend`, `Compositor`) as wrappers on it | Both phases shipped the type; B1's is what C1's own deviation (1) said B1 would produce, and it is the only one that knows about levels. Resolved at the C1 merge; see C1-cli.md deviation 21. |
| 25 | §3.9 (inline), B1, C1 | `MapAssetPipeline`/`LoadedMapAsset` confirmed **B1's** (the App consumes it); C1's `LoadedMapAssets` withdrawn, its explicit-root `TryLoad(assetsRoot, mapName)` / `TryReadMapVersion` / `TryLocateAssetsRoot` added to B1's class | Add/add collision at the C1 merge; a headless tool told where the art lives must not walk up from `AppContext.BaseDirectory`. C1-cli.md deviation 22. |
| 26 | B1, C1 | Bench harness unification (correction 8) **executed**: `BenchCommand.Measure` and the CLI's `FrameTimeStats`/`BenchResult` deleted; `dv2d bench` calls `ScenePipelineBenchmark.Run`. B1's harness gains a settable `Camera` and GC collection counts on `BenchmarkReport` (both defaulted) | The seam C1 deviation (7) left for the merge. C1-cli.md deviation 23. |

**Design-doc follow-ups** (one-line edits, owned by the phase that lands first; no plan is blocked
on them): §12 Q1 → resolved (B4 D1); §12 Q2 → C2's decision doc; §12 Q3 → resolved as B3 (drag
handles) with B2 shipping the markers; §5.7's "extract it from `MainViewModel`'s wiring" needs the
correction note; §5.2's placement of `VisionLayer` wholly in Core needs B1's compute/draw split;
§5.6's "`RoundTrack` via `SemanticNavigator`" is projected as `IModuleContext.EventFrames`.

---

## 6. Recommended kickoff order

1. **A1 now, alone.** It touches only the current control, ships user-visible value first, and its
   outputs (timeline contract, keymap, gate seam, `FrameIndexAtTick`) are inputs to four later
   phases. Nothing else should start before its `IModuleContext` additions are merged, or they will
   be written twice.
2. **B0 next, with two pull-forwards.** (a) The **SkiaSharp-on-WASM spike** (B5 risk 1): the Browser
   head must render one `CpuSurfaceProvider` frame, or the WASM story is discovered broken in the
   last phase; 1 day, and it can change the packaging. (b) The **`Modules.Abstractions` Avalonia
   split** (D1) is B0's first task and unblocks B4 and C1; do it before anything depends on it.
3. **B1 and C1 in parallel** once B0's contracts are merged. C1's fixture-only paths (`render`,
   `golden`, `fixture`) need no compositor; its `bench` command lands when B1's harness does. C1
   early also means `TrackerFrameSource` exists before B4 needs it.
4. **B2 Groups 1 + 4 in parallel with B1** (document model, freehand port, persistence; no B1
   dependency), then Groups 2/3/5 when `Scene2DHost` lands.
5. **C2 Stage 0 in parallel** with B2: the whole interface/probe/override/CI surface is no-GPU
   work. Its spike (Stages 1–2) needs a machine with a GPU; schedule that separately.
6. **B3 after B2** (it needs `AnnotationDocument.ApplyMigration` and the level model B1 declared).
7. **B4 after B1**, consuming C1's `TrackerFrameSource`. Its R3 (1080p CPU ≥ realtime) needs
   measuring **mid-phase**, not at the end; the three levers are ordered in its plan.
8. **B5 last**, as the audit phase it is. Its settings/gate/keymap contracts are already agreed
   above, so B2–B4 can consume them from day one without waiting.

**Two things the coordinator must decide, not the implementers**

- **The WASM Skia spike (item 2a).** If it fails, Playback2D v2 on browser keeps the Avalonia Skia
  lease and the offscreen CPU provider becomes desktop-only, a documented degradation, not a
  blocker, but it changes B5's WASM matrix and should be known before B1 designs the host.
- **C2's ≥2× realtime exit criterion cannot be closed until B4 lands.** Either accept C2 shipping
  "GPU parity verified, throughput measured against a stub loop" and close the criterion in B4's
  week, or schedule C2's Stage 2 after B4. Everything else in C2 is independent.

## Coordinator decisions (2026-08-24)

Both open items above are decided:

1. **The SkiaSharp-on-WASM spike moves into B0** (1 day, before B1 designs the host). B0's
   implementer runs it as their first task after project creation: add the SkiaSharp pin +
   `SkiaSharp.NativeAssets.WebAssembly` to the Browser head behind a throwaway branch commit,
   confirm the app boots and a trivial `SKSurface` draw works in-browser, and record the outcome
   in B0's plan under Decisions. If it fails: browser keeps the Avalonia lease only, the offscreen
   CPU provider is desktop-only, and B5's WASM matrix is updated accordingly: documented
   degradation, not a blocker.
2. **C2 ships with "GPU parity verified, throughput on a stub loop."** The ≥2× realtime exit
   criterion transfers to B4's acceptance checklist (measured with the real `SceneExportSession`
   on the GPU provider during B4's week). C2 is not serialized behind B4.
