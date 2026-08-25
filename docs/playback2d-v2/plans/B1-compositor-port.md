# Phase B1 — Compositor Port

**Track B (core) · Playback2D v2 · Branch `feature/playback2d-v2`**
Design authority: [`docs/playback2d-v2/design.md`](../design.md). This plan is self-contained; you do
not need to read the design to execute it, but every contract below is traceable to a design section
and must not be redesigned.

> ## Integrator corrections (BINDING — supersede anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry.
>
> 1. **§2.1's "minimum shape" of `Scene2DFrame` is wrong — B0 owns the type and it landed
>    differently.** The real members are: `Time` (a `SceneTime` carrying `Tick`, `FrameIndex`,
>    `DemoSeconds`, `DeltaSeconds`, `IsDiscontinuity` — there are no top-level `Tick`/`FrameIndex`/
>    `IsDiscontinuity` properties), `Markers`, `AreaEffects`, **`Trails`** (not `GrenadeTrails`),
>    `Bomb`, `KillFeed`, `GameInfo`, **`Map`** (a `SceneMapInfo` carrying `MapName`,
>    `NetworkedBounds`/`ObservedBounds` as `WorldBounds?`/`WorldBounds` — not `SKRect`,
>    `SectionHeights`, `Radars`), `Vision` (Pipeline-computed cones/sightlines — B0 D4), `FollowSlot`.
>    There is **no `Toggles`** member (B0 D5: overlay toggles are compositor state → `ISceneLayer
>    .IsEnabled`) and **no `Levels`** member (B0 D3: the frame carries `Map.SectionHeights`, and B1's
>    `MapSpaceFactory` derives the level set from it). Adapt the layers at their call sites.
> 2. **`ISceneLayer.ContentVersion` and `SceneRenderContext` are declared by B0**, already carrying
>    B1's member names (`Frame`, `Time`, `PaneBounds`, `RenderScaling`, `IsSingleLevel`,
>    `BelongsHere`). B1 **adds** `Pane` (`LevelPaneSnapshot`) and `LevelIndexFor` to that same type,
>    and **adds** the `Render(SKCanvas, in SceneSubmission)` overload to B0's `SceneCompositor`
>    (whose members are `Add`/`Remove`/`Find`/`SetEnabled`). Never declare a second context or
>    compositor.
> 3. **`MapLevel` / level identity is B3's shape, adopted here up front** so B3 only adds members:
>    `MapLevel` is a **`sealed class`** with `MapLevelId Id` (a `readonly record struct
>    MapLevelId(int Key)`), `Name`, `ZMin`, `ZMax`, `SKImage? Radar`, `RadarImageName`, `HasRadar`,
>    `Span`, `MidZ`, `Contains(double)`. `MapLevel.IdFor(...)` is replaced by
>    `MapSpace.QuantizeZ(z)` + B3's overlap-carry minting. `LevelDisplayMode` is
>    `{ Stacked, Single, SideBySide }` (**not** `{ AllLevels, SingleLevel }`).
>    `MapSpace.LevelIndexForSticky(...)` is replaced by `MapLevel LevelFor(double worldZ,
>    MapLevelId? previous)`, and `MapSpace.Rebuild` is
>    `LevelSetChange Rebuild(IReadOnlyList<FloorSlice> bands, IReadOnlyList<SKImage?>? radarByLevel
>    = null, RadarBindingQuality quality = RadarBindingQuality.None)` — B1 moves `FloorSplitter`/
>    `FloorSlice` into Core (T1), so the `FloorSlice` parameter is legal.
> 4. **`PaneSet` is the single pane-lifetime owner.** B3's `LevelPaneStore` is not a second type —
>    B3 adds "retain state for levels that are not currently arranged" to *this* `PaneSet`.
>    `LevelPane.Camera` stays a **public field** with a justified `CA1051` suppression (B2's
>    `PanZoomTool` mutates it in place; a property returning a copy silently breaks panning).
> 5. **The settings property is `Playback2DSettings.LegacyViewport`**, not `UseLegacyViewport`
>    (B5 owns the class shape and its removal plan cites that name). The env var
>    `DV_PLAYBACK2D_RENDERER` is unchanged. B1 creates the class if it is the first lander; every
>    later phase **adds properties to it** rather than creating a sibling section.
> 6. **Text must come from an embedded typeface, never `SKTypeface.Default`** (C1 R1): the CI golden
>    lane runs on ubuntu and byte-exact text goldens are impossible with system font fallback. Ship
>    `TextBlobCache` over a font asset embedded in Core as an `EmbeddedResource`, and record the
>    choice in the T0 API notes.
> 7. **Fixtures and goldens use C1's corpus layout** — `tests/fixtures/playback2d/scenes/*.scene.json`
>    and `tests/fixtures/playback2d/goldens/cpu/<name>@<w>x<h>.png`. B1's three fixtures are the
>    canonical corpus names `nuke-multilevel`, `mirage-single-level`, `full-scene-budget` (drop
>    `stacked-2level-nuke` / `single-level-dust2`). Comparison goes through B0's
>    `GoldenImageComparer` + `GoldenTolerance` — `GoldenParityTests` supplies
>    `GoldenTolerance.ByteExact` for Tier A and `GoldenTolerance.DefaultPerceptual` for Tier B; it
>    does not implement its own pixel loop.
> 8. **The bench harness names in §5.7 are canonical** (`ScenePipelineBenchmark`,
>    `BenchmarkRequest`, `BenchmarkReport`, `FrameTimeStats`, `BudgetPolicy`). C1's `dv2d bench`
>    wraps exactly these; C1's `SceneBenchHarness`/`SceneBenchRequest`/`SceneBenchResult` names are
>    withdrawn. `ScenePipelineBenchmark` renders through `HeadlessSceneRenderer` (C1's Pipeline
>    facade over Core's `SceneRenderer`) so there is one headless render entry point.
> 9. **The test project is `src/Playback2D/DemoViewer.NET.Playback2D.Tests`, created by B0.** B1
>    adds only what is missing. Its `Directory.Packages.props` entries and the CI `playback2d-tests`
>    job also come from B0; B1 adds the `playback2d-budget` job only.
> 10. **A1's timeline types land in Core** (`…Core.Timeline`) — A1 ships them Core-clean under an
>     architecture test precisely so this move is a namespace rewrite. Move all seven declared
>     members of `ITimelineTrack`/`TimelineMarker`/`TimelineBand`/`ITimelineData`/
>     `TimelineEventRecord`/`TimelineEventKeys`/`TimelineMarkerKind` unchanged, and delete A1's
>     `TimelineCoreCleanTests`. R9's "land it in Pipeline instead" is a fallback only if A1 shipped
>     something the architecture test rejects.

---

## 1. Scope & exit criterion

The design's §9 phase table, row B1, verbatim:

> | | B1 | Port 7 layers to `SKCanvas`; `Scene2DHost` + draw op + CPU fallback; `MapSpace`/panes replicating stacked bands; deterministic-time plumbing; allocation cleanup; `dv2d bench` harness + budget gates in CI | Pixel-parity (± reviewed text metrics) vs B0 goldens; p99 ≤ budget on CPU baseline; old control behind toggle | 3 wk |

Everything B1 owns, expanded from the assignment:

1. Port the seven draw passes in `Playback2DViewport.DrawSection` to `ISceneLayer.Render(SKCanvas, …)`.
2. `SceneCompositor` with an interleaved `(Slot, Order)` layer list and `LayerCacheHint`
   (`Static`/`PerCamera` → `SKPicture`).
3. `Scene2DHost : Control` — one `ICustomDrawOperation` over `ISkiaSharpApiLeaseFeature`, plus the CPU
   `SKSurface` → `WriteableBitmap` fallback.
4. `MapSpace` / `MapLevel` / `LevelPane` / `ILevelLayoutPolicy`, with `StackedLayout` replicating today's
   horizontal bands byte-for-byte.
5. `ICameraRig`: `FitMapRig`, `FitAliveRig`, `ManualRig`, `FollowPlayerRig` (the last with a new deadzone).
6. The Advance/Render purity split, enforced by a per-host render gate.
7. Deterministic `SceneTime` plumbing (no wall clock reaches a layer).
8. Allocation cleanup to a **zero steady-state bytes/frame** contract.
9. The `dv2d bench` **harness** (a Pipeline-level benchmark runner class) + budget gates in CI. The `dv2d`
   CLI command that wraps it is **C1's**, not B1's.
10. The old `Playback2DViewport` retained, live, behind an internal toggle.

**Explicitly NOT in B1** (do not build these, do not stub them beyond the named seams):
annotations and `IPointerTool`/`InputToolRouter` (B2), `SingleLayout`/level strip/AutoFollow (B3),
`SceneExportSession`/sinks/HUD layers (B4), `dv2d` CLI (C1), `GpuSurfaceProvider` (C2), moving
`VisionLayer.Advance` off the UI thread (designed here, built only if §6 forces it).

---

## 2. Ground truth B1 inherits

### 2.1 From B0 (must exist before T3 starts)

| Symbol | Assembly | B1 uses it for |
|---|---|---|
| `Scene2DFrame` | Core | The immutable per-frame world state every layer reads |
| `SceneTime` | Core | Injected time (§5.1 record struct) |
| `RenderPurpose` | Core | `Interactive` in B1 |
| `ViewportTransform` (moved verbatim) | Core | World↔screen math, unchanged |
| `SliceCamera` (moved verbatim) | Core | Per-pane camera with `StepToward`/`IsSettledAt`/`ManualOverride` |
| `IRenderSurfaceProvider`, `CpuSurfaceProvider`, `RenderBackend` | Core | Offscreen surfaces for goldens + bench |
| `SceneFrameBuilder` | Pipeline | Builds `Scene2DFrame` from `IPlaybackSnapshot` |
| `SceneFixture` (JSON scene fixtures) | Pipeline | Golden + bench corpus |
| Golden images pinning current output | `tests/fixtures/playback2d/` | The B1 exit criterion |
| Architecture test (Core→SkiaSharp only; banned wall-clock APIs) | tests | B1 must keep it green |

**The shape of `Scene2DFrame` (B0 owns it; corrected per integrator note 1).** This is what B1's
layers actually read — the earlier draft of this section guessed wrong and has been replaced:

```csharp
SceneTime Time { get; }                            // Tick (DV frame clock, never CS2 ticks), FrameIndex,
                                                   // DemoSeconds, DeltaSeconds, IsDiscontinuity
IReadOnlyList<PlayerMarker> Markers { get; }       // incl. ulong SteamId
IReadOnlyList<AreaEffect> AreaEffects { get; }
IReadOnlyList<GrenadeTrail> Trails { get; }        // NOT "GrenadeTrails"
BombMarker? Bomb { get; }
IReadOnlyList<KillFeedRow> KillFeed { get; }
SceneGameInfo GameInfo { get; }
SceneMapInfo Map { get; }                          // MapName, WorldBounds? NetworkedBounds,
                                                   // WorldBounds ObservedBounds, SectionHeights, Radars
SceneVision Vision { get; }                        // Pipeline-computed cones + sightlines (B0 D4)
int FollowSlot { get; }
```

Two members B1 expected and must live without: there is **no `Toggles`** (overlay visibility is
compositor state — map it onto `ISceneLayer.IsEnabled`, B0 D5) and **no `Levels`** (B0 D3 keeps
`FloorSplitter` out of the frame; `MapSpaceFactory` derives levels from `Map.SectionHeights` plus the
authoritative floors, exactly as the viewport does today). `WorldBounds` is a Core record struct,
not `SKRect` — convert at the layer boundary.

`PlayerMarker`, `AreaEffect`, `GrenadeTrail`, `GrenadeTrailPoint`, `BombMarker`, `RingState`,
`GrenadeKind`, `AreaEffectKind` are the existing App records
(`src/App/DemoViewer.NET/Modules/Playback2D/*.cs`) and move to Core in B0 or in B1 T1 — they are pure
value types with no Avalonia dependency. `FloorSlice`/`FloorSplitter` move in B1 T1 (§4 T1).

### 2.2 From A1 (mechanical move here)

A1 defines `ITimelineTrack`, `TimelineMarker`, `ITimelineData` App-side. B1 **moves them to Core
unchanged** — signatures are frozen by A1; this is a namespace change and nothing else.

> **Integrator flag.** A1 has not landed as of writing (`grep -rn "ITimelineTrack" src/` → no hits), so
> the move target cannot be verified. If A1's `ITimelineData` transitively references CS2DemoKit or
> App types, the move lands in **Pipeline**, not Core — Core references SkiaSharp only and the
> architecture test will reject it. Decide at merge; do not weaken the architecture test.

### 2.3 Version constraints discovered in recon (hard, non-negotiable)

- Avalonia 11.3.12 resolves **SkiaSharp 2.88.9** (verified: `artifacts/obj/DemoViewer.NET.Desktop/project.assets.json:3427`).
  `ISkiaSharpApiLeaseFeature` hands you a `SkiaSharp 2.88.x` `SKCanvas`. Core **must** pin SkiaSharp
  `2.88.9` — a Core built against SkiaSharp 3.x produces a different `SKCanvas` type and the lease path
  will not compile, let alone run.
- `tools/DemoViewer.NET.AssetBaker` deliberately opts out of CPM and uses SkiaSharp 3.119.2. It is **not**
  in the solution and the app never references it. Do not "unify" the two.

---

## 3. The layer-by-layer port map

`Playback2DViewport.cs` is 1,438 lines. Every line below is accounted for.

### 3.1 Draw passes → layers

`DrawSection` (lines 863–930) is the pass-order authority. Its order becomes the compositor's
`(Slot, Order)` sort:

| Layer id (stable, persisted key) | Type | Slot | Order | Cache | Ported from (lines) |
|---|---|---|---|---|---|
| `playback2d.radar` | `RadarLayer` | `Underlay` | 0 | `PerCamera` | `TryDrawRadar` 1065–1090 + `DrawGrid` 1117–1152 (radar-else-grid, exactly line 867–870) |
| `playback2d.trails` | `TrailLayer` | `World` | 10 | `Dynamic` | `DrawTrajectory` 1236–1289, `FloorSegmentRuns` 1297–1332 |
| `playback2d.areaeffects` | `AreaEffectLayer` | `World` | 20 | `Dynamic` | `DrawAreaEffect` 1215–1229, level filter 890–895 |
| `playback2d.vision` | `VisionLayer` | `World` | 30 | `Dynamic` | `RebuildSightlines` 932–983, `DrawViewCones` 1007–1023, `DrawOneCone` 1025–1057, `DrawSightlines` 987–1005 |
| `playback2d.markers` | `MarkerLayer` | `World` | 40 | `Dynamic` | `DrawMarker` 1154–1204, `RingColorForTeam` 1206–1211, `AdvanceMarkers` 648–698, `SmoothedMarkerPosition` 701 |
| `playback2d.bomb` | `BombLayer` | `Overlay` | 50 | `Dynamic` | `DrawBomb` 1337–1365, `DrawArc` 1368–1389, `PointOnCircle` 1391–1395 |
| `playback2d.floorlabel` | `FloorLabelLayer` | `Hud` | 60 | `Dynamic` | label string 587, `context.DrawText` 924–929 |

Sort key is `(Slot, Order, Id)` — `Id` is the deterministic tiebreak so two layers registered at the same
`(Slot, Order)` never reorder between runs (determinism test 10 in §6).

The **band separator line** (596–599) is *not* a layer: it is drawn in host coordinates between panes and
belongs to `SceneCompositor`'s pane-divider chrome (§5.3, `SceneCompositor.Render` step 5).

**`Static` has no B1 consumer.** The radar's image draw is a single `DrawImage`; folding it into the same
`PerCamera` picture as the grid loop (up to 800 `DrawLine` calls, lines 1137–1151) is both simpler and
faster than splitting the layer. The `Static` mechanism is still built and unit-tested against a synthetic
layer, because B2's dry annotation ink is its real consumer.

### 3.2 Everything else in the file

| Current member | Lines | Destination |
|---|---|---|
| `enum CameraMode` | 23–36 | **Stays in App.** It is the view's mode-selector vocabulary; `CameraRigFactory.For(mode, followSlot)` maps it to rigs. |
| `DefaultWorldExtent`, `GridStepWorld`, `RadarOpacity` | 49–51 | Core `RadarLayer` consts / `SceneDefaults.WorldExtent` |
| `AlivePadding`, `FollowHalfWorld`, `LerpResponse` | 54–56 | Core `FitAliveRig`, `FollowPlayerRig`, `CameraAdvancer` |
| `MarkerLerpResponse`, `MarkerSnapDistanceSq`, `MarkerSettleEpsilonSq` | 63–65 | Core `MarkerLayer` |
| `ConeRays`, `ConeHalfFovDeg`, `ConeMaxRange` | 71–73 | Core `VisionOptions` (shared by layer + Pipeline solver) |
| `_floors` (`FloorSplitter`) | 76 | Core `MapSpaceFactory` owns the splitter |
| `_liveSlots`, `_pruneScratch`, `_smoothedPos` | 77, 78, 86 | Core `MarkerLayer` smoothing state |
| `_sightlines`, `_smokeScratch`, `_visionScratch` | 84, 85, 89 | Pipeline `VisibilityEngineSolver` scratch |
| `_typeface` | 88 | Core `TextBlobCache` (`SKFont` over `SKTypeface.FromFamilyName`) |
| `_cameras`, `_cameraSliceCount`, `_cameraViewW/H` | 94–96 | Core `PaneSet` / `LevelPane` |
| `_dragging`, `_dragSlice`, `_lastPointer` | 99, 100, 110 | App `Scene2DHost` + `PanZoomGesture` |
| `_followSlot` | 101 | App `Scene2DHost.FollowSlot` → `FollowPlayerRig` |
| `_frameLoopArmed`, `_havePrevFrameTime`, `_lastDt`, `_prevFrameTime` | 105, 107, 109, 122 | App `Scene2DHost` RAF loop → `SceneTime.DeltaSeconds` |
| `_hasObservedPositions`, `_initialFitApplied`, `_minX/_minY/_maxX/_maxY` | 106, 108, 111, 115 | B0 `Scene2DFrame.ObservedExtent` + Core `PaneSet.ApplyInitialFit` |
| `_palette`, `CanvasPalette` record, delegating props | 120, 134–161, 1406–1437 | Core `ScenePalette` + App `ScenePaletteFactory` |
| `BuildPalette` | 210–261 | App `ScenePaletteFactory.Build(ThemeVariant)` — same 31 `Pb2dCanvas*` keys, same hex fallbacks |
| `Mode` setter | 164–185 | App `Scene2DHost.Mode` → `PaneSet.SetRig` + `PaneSet.ClearManualOverrides` |
| `PrimaryCameraTransform`, `PrimaryCameraManual`, `SightlineCount`, `SmoothedMarkerPosition` | 188, 192, 206, 701 | Re-exposed on `Scene2DHost` under **the same names** so ported tests read identically |
| `OnDataContextChanged`/`OnAttached…`/`OnDetached…`/`OnThemeVariantChanged`/`RefreshCanvasPalette`/`AttachVm` | 263–316 | App `Scene2DHost`, same lifecycle + compositor dispose |
| `OnFrameUpdated` | 320–325 | App `Scene2DHost.OnFrameUpdated` → `Advance` + `InvalidateVisual` |
| `UpdateObservedExtent`, `Widen` | 327–387 | **B0** `SceneFrameBuilder` (extent widening + `FloorSplitter.Observe`) |
| `FitToExtent` | 394–401 | App `Scene2DHost.FitToExtent()` → `PaneSet.FitAll` |
| `OnPointerPressed/Moved/Released/WheelChanged` | 405–459 | App `Scene2DHost` + `PanZoomGesture` |
| `SliceIndexAtScreenY`, `ScreenSectionOffset` | 464–488 | Core `PaneSet.PaneAt(float, float)` / `LevelPane.ViewportRect` |
| `EnsureCameras`, `ApplyFitToAllSlices` | 492–532 | Core `PaneSet.Reconcile(…)`, `PaneSet.FitAll(…)` |
| `Render` | 534–601 | App `Scene2DHost.Render` (submission) + Core `SceneCompositor.Render` |
| `AdvanceCameras` | 606–641 | Core `CameraAdvancer.Advance(PaneSet, Scene2DFrame, in SceneTime)` |
| `TryComputeTarget` | 706–739 | Core rig dispatch — `pane.Rig.ComputeTarget(pane, frame)` |
| `TryFitAlive` | 743–784 | Core `FitAliveRig.ComputeTarget` |
| `TryFollow` | 789–817 | Core `FollowPlayerRig.ComputeTarget` (+ new deadzone) |
| `ArmFrameLoopIfNeeded`, `OnAnimationFrame` | 822–858 | App `Scene2DHost` RAF loop |
| `ResolveRadarImage` | 1096–1115 | **DELETED.** Replaced by Pipeline `MapRadarBinder`, evaluated once per `MapSpace` rebuild (§4 T5) |

### 3.3 Parity invariants that must survive the port

These are the subtle behaviours that make the goldens match. Read them before writing a layer.

1. **`sliceIndex < 0` is a sentinel, not an index.** In the single-floor case (line 576) `DrawSection` is
   called with `-1`, which makes every `_floors.SliceIndexFor(z) == sliceIndex` filter pass everything.
   `SceneRenderContext.LevelIndex` is `-1` when `IsSingleLevel` for exactly this reason. Do not
   "clean it up" to `0`.
2. **Highest level renders on top.** Pane for level index `i` (0 = lowest) occupies band
   `sectionIndex = count - 1 - i` (lines 583, 486).
3. **Markers draw at the smoothed position; level assignment uses the raw Z** (lines 908 vs 1158, and the
   comment at 1156–1157). Same for cones (1028–1030) and sightlines (998–999).
4. **A trail segment belongs to a level if *either* endpoint maps to it**, so the crossing segment is drawn
   on both bands (1309–1311). The head dot draws only on the tip's level (1280–1283).
5. **A sightline draws on a band if *either* endpoint is on it** (991–994).
6. **`AdvanceMarkers` runs in every camera mode, including Fit** (line 566 comment) and re-arms the loop.
7. **`AdvanceCameras` skips Fit and manual-override panes entirely** (616–619); a settled pane snaps to the
   target so the loop can stop (626–630).
8. **The bomb arc collapses below 0.5°** and is clamped to 359.99° (1372–1376).
9. **Radar `rotate`/`zoom` from the overview txt are deliberately NOT applied** (comment 1062–1064).
   Preserve. The dest rect is `WorldToScreen(MinX, MaxY)` → `WorldToScreen(MaxX, MinY)` (1080–1082).
10. **Grid bail-out at >400 lines per axis** (1129–1134), and the major line is the axis (`|w| < 1e-3`).

---

## 4. Ordered work breakdown

Each task is ≤ ~half a day unless marked. `→` denotes a hard ordering dependency.

### T0 — SkiaSharp API pinning spike (**time-box: 1 h**) → blocks T2

Write a throwaway console/test that exercises, against **SkiaSharp 2.88.9** specifically:
`SKPictureRecorder.BeginRecording/EndRecording`, `SKCanvas.DrawPicture`, `SKTextBlob.Create(string, SKFont)`,
`SKFont` metrics, `SKCanvas.DrawImage(SKImage, SKRect, …)` sampling overloads (`SKSamplingOptions` vs
`SKPaint.FilterQuality`), `SKPath.ArcTo` sweep semantics, `SKSurface.Create(SKImageInfo, IntPtr, int)`.
**Deliverable:** a short `docs/playback2d-v2/plans/B1-skia-api-notes.md` listing the exact overloads chosen.
Every later task uses those and nothing else. Do not discover API drift at T9.

### T1 — Move the pure value types into Core → blocks everything

Mechanical file moves, namespace change only, zero logic change.

- **Move to `DemoViewer.NET.Playback2D.Core/Levels/`:** `FloorSplitter.cs` (383 lines),
  `FloorSlice` (same file).
- **Move to `DemoViewer.NET.Playback2D.Core/Scene/`** (if B0 has not already): `PlayerMarker.cs`,
  `RingState`, `AreaEffect.cs`, `AreaEffectKind`, `GrenadeTrail.cs`, `GrenadeTrailPoint`, `GrenadeKind`,
  `BombMarker.cs`.
- Update usings in: `Playback2DTabViewModel.cs` (:174, :240), `MapAssetLoader.cs` (:18-19, :33-35),
  `Playback2DViewport.cs`, and the App test files `FloorAssetConsumptionTests.cs`,
  `FloorSplitterTests.cs`, `FloorSplitterMultiFloorTests.cs`, `GrenadeTrailFloorSplitTests.cs`.
- Add `global using` aliases in the App's `GlobalUsings.cs` only if the churn exceeds ~20 sites; otherwise
  update the usings directly (preferred — explicit is the repo style).

**Exit:** solution builds, all existing App tests green, no behaviour change.

### T2 — Core layer contracts + `SceneCompositor` (**~1 day**) → T0, T1

Create in `DemoViewer.NET.Playback2D.Core/Compositing/`:
`LayerSlot.cs`, `LayerCacheHint.cs`, `ISceneLayer.cs`, `SceneRenderContext.cs`, `SceneSubmission.cs`,
`SceneCompositor.cs`, `SceneCompositorStats.cs`, `LayerPictureCache.cs`.

`LayerPictureCache` semantics (this is the whole `LayerCacheHint` mechanism):

| Hint | Recording space | Cache key | Replay |
|---|---|---|---|
| `Static` | **World** space | `(paneLevelId, layerId, layer.ContentVersion)` | `canvas.Save(); canvas.Concat(ViewportMatrix.From(transform)); canvas.DrawPicture(p); canvas.Restore()` |
| `PerCamera` | **Pane-local screen** space | `(paneLevelId, layerId, layer.ContentVersion, cameraEpoch)` | `canvas.DrawPicture(p)` |
| `Dynamic` | — | — | `layer.Render(canvas, ctx)` directly |

`cameraEpoch` is an `int` bumped by `LevelPane` whenever `Camera.Current` changes materially
(`!IsSettledAt(previous)` **or** viewport rect resize). `ContentVersion` is `ISceneLayer.ContentVersion`,
an `int` the layer bumps when its cacheable content changes (radar image swap, later ink edits).
Pictures are disposed on eviction, on pane removal, and in `SceneCompositor.Dispose`.

**Exit:** `SceneCompositorOrderTests` + `LayerCachePictureTests` green (§6).

### T3 — `MapSpace` / `MapLevel` / `LevelPane` / `PaneSet` / `StackedLayout` (**~1 day**) → T1

Create in `Core/Levels/`: `MapLevel.cs`, `MapSpace.cs`, `MapSpaceFactory.cs`, `LevelPane.cs`,
`PaneSet.cs`, `ILevelLayoutPolicy.cs`, `StackedLayout.cs`, `LevelDisplayMode.cs`.

- `MapSpaceFactory` owns the `FloorSplitter` and its precedence chain (authoritative nav floors >
  histogram; section heights stored-not-adopted) — unchanged from `FloorSplitter.Slices`.
- **Level identity is quantized `ZMin`**: `MapLevel.Id = (int)Math.Round(ZMin / FloorSplitter.BucketWidth)`
  (bucket width 64). Never the array index — that is design risk 5.
- `MapSpace.LevelIndexFor(double z)` must be **behaviourally identical** to `FloorSplitter.SliceIndexFor`
  (contains-first, then nearest-by-`MidZ`). A parity test table pins it (§6 test 3).
- `PaneSet.Reconcile` replaces `EnsureCameras` (492–523) but keys pane reuse on `MapLevel.Id`, not array
  index: an existing pane keeps its `SliceCamera`, `ManualOverride`, and `Rig`, and is re-fitted to the new
  band rect via `WithViewport`; a newly appeared level is `ViewportTransform.Fit`-ed to the frame's extent;
  a vanished level's pane and its cached pictures are dropped.
- `StackedLayout.Arrange` reproduces lines 546–548 and 580–584 exactly:
  `bandH = host.Height / max(1, levels.Count)`, pane for level `i` gets
  `SKRect(0, (count-1-i)*bandH, host.Width, (count-i)*bandH)`.
- `PaneSet.PaneAt(float x, float y)` replaces `SliceIndexAtScreenY` (464–475) — same floor/clamp/invert.

### T4 — Camera rigs + `CameraAdvancer` → T3

Create in `Core/Cameras/`: `ICameraRig.cs`, `ManualRig.cs`, `FitMapRig.cs`, `FitAliveRig.cs`,
`FollowPlayerRig.cs`, `CameraAdvancer.cs`, `CameraRigFactory.cs`.

Mapping from today's `CameraMode` (note the naming collision — see Decision D-3):

| `CameraMode` | Rig | Ported from |
|---|---|---|
| `Fit` | `ManualRig` (returns `null` — hold; the one-shot fit is applied by `PaneSet.FitAll`) | 616–619, 554–558, 394–401 |
| `Map` | `FitMapRig` | 716–728 |
| `Alive` | `FitAliveRig` | 743–784 |
| `FollowPlayer` | `FollowPlayerRig(slot)` | 789–817 **+ new deadzone** |

`CameraAdvancer.Advance` is `AdvanceCameras` (606–641) verbatim: `t = 1 - exp(-LerpResponse * dt)`,
skip manual/null-target panes, snap when `IsSettledAt`, else `StepToward`, return `anyMoving`.

**Deadzone (the one deliberate behaviour change):** `FollowPlayerRig` holds its last committed target
while the followed marker stays inside an axis-aligned box of half-extent `DeadzoneHalfWorld` (default
**180 world units**, 20 % of the 900 u follow box) around the committed centre; outside it, it recentres.
`DeadzoneHalfWorld = 0` reproduces today exactly and is what the parity test uses.

### T5 — `MapAssetPipeline`: radar bitmaps → `SKImage`, explicit per-level binding (**~1 day**) → T3

Split `src/App/DemoViewer.NET/Modules/Playback2D/MapAssetLoader.cs` (155 lines):

- **Move to `Pipeline/Assets/MapAssetPipeline.cs`:** `LoadedMapAsset` (21–66), `TryLoad` (76–80),
  `TryLoadFromDirectory` (120–154). `RadarBitmaps : IReadOnlyDictionary<string, Bitmap>` becomes
  `RadarImages : IReadOnlyDictionary<string, SKImage>`, decoded with
  `SKImage.FromEncodedData(SKData.Create(path))` instead of `new Bitmap(path)` (line 140). Keep the
  best-effort per-image try/catch, keep `Dispose` idempotent (53–65) — `SKImage` is equally unmanaged.
- **Stays in App:** `MapAssetLoader.TryLoadRadarThumbnail` (88–117). It feeds an Avalonia `Bitmap` to a
  library card and has nothing to do with the scene. `Bitmap.DecodeToWidth` has no `SKImage` analogue and
  we are not adding one.
- **Fix the per-push allocation:** `LoadedMapAsset.Floors` (34–35) allocates a `List` on **every property
  read**, and `Playback2DViewport.UpdateObservedExtent` reads `_vm.AuthoritativeFloors` once per push
  (line 340). Cache it in a field.
- **New `Pipeline/Assets/MapRadarBinder.cs`** replaces `ResolveRadarImage` (1096–1115), evaluated **once
  per `MapSpace` rebuild** instead of per band per frame (killing the `OrderBy`/`ToList`/`First` LINQ at
  1109/1114). Rules, preserving today's decisions exactly:
  1. `RadarLayers.Count == 0` → every level gets `RadarImages[0]` (or none). *(1100–1101)*
  2. `RadarLayers.Count == levels.Count` → level `i` gets `layers.OrderBy(MinZ)[i].Image`. *(1107–1111)*
  3. otherwise → every level gets `layers.OrderByDescending(MinZ).First().Image`, and the binding is
     flagged `RadarBindingQuality.Degraded` with a per-level `HasRadar` bool so the UI can say
     "no radar for this level". *(1114)*
- The App's `Playback2DTabViewModel.MapAsset`/`AuthoritativeFloors`/`CollisionTrisPath` keep their shapes;
  only the namespace and the bitmap type change. The `ReplaceMapAsset` Background-priority dispose
  (`Playback2DTabViewModel.cs:538-548`) is unchanged and still correct.

### T6 — `RadarLayer` (radar + grid fallback) → T2, T3, T5

`Render` is `TryDrawRadar`-else-`DrawGrid`, exactly line 867–870. Radar draws with
`SKPaint { Color = new SKColor(255,255,255, (byte)(0.9*255)) }` to reproduce `PushOpacity(0.9)` (1084);
grid reproduces 1117–1152 including the 400-line bail-out. Everything recorded into the layer's
`PerCamera` picture.

### T7 — `TrailLayer` + `AreaEffectLayer` + `BombLayer` → T2, T3

Direct ports of 1236–1289, 1215–1229, 1337–1395. `StreamGeometry` → a layer-owned reused `SKPath`
(`path.Reset()` per use); `Pen`/`SolidColorBrush` → layer-owned `SKPaint`s mutated in place.
`DrawArc` → `SKPath.ArcTo` with the same clamp rules (1370–1376). `FloorSegmentRuns` moves to Core
`TrailGeometry` verbatim, **plus** a non-allocating overload filling a caller-owned list.

### T8 — `MarkerLayer` (Advance = smoothing, Render = discs) → T2, T3

- `Advance` = `AdvanceMarkers` (648–698) verbatim, **plus**: when `time.IsDiscontinuity`, snap every
  tracked slot to its raw position (superset of the existing distance-based teleport snap — the distance
  rule is what `Playback2DInterpolationTests` pins, so it stays).
- `Render` = `DrawMarker` (1154–1204). `FormattedText` → `TextBlobCache.Get(label, 10f)`.
  Label colour: black when alive, `palette.Label` when dead (1202). Centring uses the blob's measured
  bounds — this is the one place where "± reviewed text metrics" applies (§6 test 14).
- `SmoothedMarkerPosition(int slot)` stays `internal` with the same signature and is re-exposed by
  `Scene2DHost`.

### T9 — `FloorLabelLayer` → T2, T8 (`TextBlobCache`)

Label string is `$"floor {levelIndex}  z[{MinZ:F0}..{MaxZ:F0}]"` (line 587) drawn at pane-local `(8, 6)`
with the 11 px face (926–928). Renders only when `!ctx.IsSingleLevel` (line 924 — `label` is `null` in the
single-floor path, line 576).

### T10 — `VisionLayer` + the `IVisionSolver` seam (**~1 day**) → T2, T3

This is the one pass that cannot port mechanically, because `VisibilityEngine`/`VisibilityAnalyzer`/
`PlayerVantage` live in `CS2DemoKit.Analysis.Visibility` and **Core references SkiaSharp only**.

- **Core** `Vision/IVisionSolver.cs`, `Vision/VisionSolution.cs`, `Vision/VisionOptions.cs`,
  `Layers/VisionLayer.cs`. `VisionLayer.Advance` calls `_solver.Solve(frame, _solution)`; `Render` maps the
  solution's **world-space** cone polygons and sightline endpoints through `ctx.Transform` and fills/strokes
  them (the mapping half of 1032–1056 and 1000–1003). `VisionSolution` uses pooled, reused arrays.
- **Pipeline** `Vision/VisibilityEngineSolver.cs` — `RebuildSightlines` (932–983) **and the 26 raycasts**
  from `DrawOneCone` (1037–1045), verbatim math, into `VisionSolution`.
- **The raycasts move from Render to Advance.** Today they run inside `Control.Render` (1042), which
  violates the purity split and would call a `VisibilityEngine` from the render thread. Pixel-identical
  (same rays, same eye, same range), strictly cheaper (one solve per frame instead of one per pane), and
  required by the contract.
- **Off-thread escape hatch (designed, not built):** `IVisionSolver` is the seam. B1 ships one
  implementation, `VisibilityEngineSolver`, invoked synchronously in `Advance`. If §6 budgets are missed
  on baseline hardware, a `DeferredVisionSolver` wraps it to compute into the *next* frame's solution off
  the UI thread. Do not build it in B1.

### T11 — `ScenePalette` + `ScenePaletteFactory` → T2

`Core/ScenePalette.cs`: a record of `SKColor`s + stroke widths, one member per `CanvasPalette` field
(1406–1437), with a `ScenePalette.Dark` static built from the same hex fallbacks at 230–260 so
direct-execution tests and goldens need no Avalonia. `App/…/ScenePaletteFactory.cs` resolves the same 31
`Pb2dCanvas*`/`Pb2dTeam*` keys via `ThemeColors.Get(key, variant, fallbackHex)` and is called **once per
theme change**, never per frame (the existing discipline at 288–294).

### T12 — `Scene2DHost` + `ICustomDrawOperation` + render gate (**~1.5 days**) → T2–T11

`src/App/DemoViewer.NET/Modules/Playback2D/Scene2DHost.cs` (~300 loc target) +
`SceneDrawOperation.cs` + `SceneRenderGate.cs` + `PanZoomGesture.cs`.

Full threading contract in §5.4. Port order inside the file: lifecycle (263–316) → `OnFrameUpdated`
(320–325) → RAF loop (822–858) → pointer handlers (405–459, via `PanZoomGesture` + `PaneSet.PaneAt`) →
`Render` (534–601 split into `AdvanceAndSubmit` + the op) → the `Mode`/`FollowSlot`/`FitToExtent` surface
(164–203, 394–401) → the four test hooks.

### T13 — CPU `SKSurface` → `WriteableBitmap` fallback → T12

Probe-by-failure: the op sets `_leaseUnavailable = true` and posts an invalidate when
`context.TryGetFeature<ISkiaSharpApiLeaseFeature>()` returns null; from the next frame the host renders on
the UI thread into a cached `WriteableBitmap` and draws it with `DrawingContext.DrawImage`. The
`SKSurface` is created **directly over the locked framebuffer**
(`SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), fb.Address, fb.RowBytes)`)
— no `ReadPixels` copy. `CpuSurfaceProvider` is *not* used here; it is for offscreen consumers that own
their own memory (Decision D-7).

### T14 — Internal toggle + view swap point → T12

- `App/.../Playback2DRenderer.cs`: `enum Playback2DRendererKind { Scene, Legacy }` +
  `static Playback2DRendererKind Selected` resolved once (§5.6).
- `App/.../IPlayback2DSurface.cs`: `CameraMode Mode { get; set; }`, `int FollowSlot { set; }`,
  `void FitToExtent()`. `Playback2DViewport` gains `: IPlayback2DSurface` — its members already match
  (164, 195, 394), so this is a one-line change to the class declaration.
- `Views/Playback2D/Playback2DView.axaml:87`: `<p2d:Playback2DViewport x:Name="Viewport" />` →
  `<ContentControl x:Name="ViewportHost" />`.
- `Playback2DView.axaml.cs`: field `_viewport` becomes `IPlayback2DSurface? _surface`; the constructor
  (28–53) instantiates by `Playback2DRenderer.Selected` and assigns `ViewportHost.Content`. Call sites at
  :75, :81, :93, :155 are retyped; nothing else in the file changes.
- New `Configuration/Playback2DSettings.cs` + `AppSettings.Playback2D` property, **and it must be added to
  `SettingsService.WriteInMemory`** or WASM writes vanish silently (design §5.4, §8).

### T15 — Allocation cleanup pass (**~1 day**) → T6–T12

Concrete list, all verified in the current source:

1. `FormattedText` per marker per frame (1201) and per band (926) → `TextBlobCache`
   (bounded LRU, cap 512, keyed by `(text, size)`).
2. `Pen`/`SolidColorBrush` per marker (1178, 1196), per trail (1275, 1287) → layer-owned `SKPaint`s
   mutated in place.
3. `StreamGeometry` per cone (1033), per trail (1257), per bomb diamond (1343), per arc (1380) →
   layer-owned reused `SKPath` + `Reset()`.
4. `FloorSegmentRuns` allocates a `List<(int,int)>` per trail **per pane per frame** (1256, 1300) →
   non-allocating overload into a caller-owned buffer.
5. The `Func<double,int> floorOf` delegate passed at 1256 allocates a closure per call → cache one
   delegate field on the layer (or pass `MapSpace` + index).
6. `ResolveRadarImage`'s `OrderBy`/`ToList`/`First` per band per frame (1109, 1114) → dead (T5).
7. `LoadedMapAsset.Floors` `Select`/`ToList` per property read (34–35) → cached field (T5).
8. `foreach` over `IReadOnlyList<T>` boxes the enumerator (355, 654, 754, 797, 880, 888, 906, 941, 958,
   989, 1014) → indexed `for` loops.
9. `_smoothedPos.Keys` enumeration in the prune path (683) → only when counts differ (already guarded);
   keep the guard, use the pooled `_pruneScratch`.

**Exit:** `ZeroAllocationTests` green — `GC.GetAllocatedBytesForCurrentThread()` delta over 512 frames
after 64 warmup frames is **0**.

### T16 — `ScenePipelineBenchmark` harness → T12, T15

`Pipeline/Benchmarking/ScenePipelineBenchmark.cs` + `BudgetPolicy.cs` + `BenchmarkReport.cs`.
It lives in **Pipeline, not Core**, because the report stamps `DateTimeOffset.UtcNow` and Core's banned-API
test forbids that. Public API in §5.7. It also writes `bench-reports/dv2d-<id>-<timestamp>.json` matching
the existing `bench-reports/` convention.

### T17 — CI: test + budget lanes → T16

`.github/workflows/ci.yml` currently runs **zero tests**. Add two jobs (§7.4). Budget gating uses
`BudgetProfile.Ci` = baseline × `DV2D_BUDGET_SCALE` (default **2.0**) — a GitHub `ubuntu-latest` runner is
not the design's "mid-tier laptop", and a gate that fires on runner noise gets disabled within a week.

### T18 — Golden parity + review (**~1 day**) → T6–T15

Run `GoldenParityTests` against B0's corpus. Text-bearing goldens go through the perceptual tier; **every
text-metric difference is written up in `docs/playback2d-v2/plans/B1-text-metrics-review.md` and reviewed,
not auto-failed** (design risk 1). Diff PNGs are dumped to the artifact dir for eyeballing.

---

## 5. Public API contracts

**These are binding for other phases.** Namespaces: `DemoViewer.NET.Playback2D.Core.*` and
`DemoViewer.NET.Playback2D.Pipeline.*`. Design sketches from §5 are reproduced exactly where given; every
addition is marked `[fill-in]`.

### 5.1 Layers

```csharp
namespace DemoViewer.NET.Playback2D.Core.Compositing;

public enum LayerSlot { Underlay, World, Overlay, Hud }
public enum LayerCacheHint { Static, PerCamera, Dynamic }

public interface ISceneLayer : IDisposable
{
    string Id { get; }
    LayerSlot Slot { get; }
    int Order { get; }
    LayerCacheHint Cache { get; }
    bool IsEnabled { get; set; }

    /// [fill-in] Bumped by the layer when its cacheable content changes. Ignored when Cache is Dynamic.
    int ContentVersion { get; }

    /// UI-thread pre-render step; true = keep the self-terminating RAF loop armed.
    bool Advance(in SceneTime time, Scene2DFrame frame);

    /// Pure draw: reads caches built in Advance, must not mutate. Called once per pane.
    void Render(SKCanvas canvas, SceneRenderContext ctx);
}
```

```csharp
/// Immutable per-pane draw context. Value type; never retained past the Render call.
public readonly struct SceneRenderContext
{
    public Scene2DFrame Frame { get; }
    public SceneTime Time { get; }
    public ScenePalette Palette { get; }
    public RenderPurpose Purpose { get; }

    public LevelPaneSnapshot Pane { get; }
    public ViewportTransform Transform { get; }   // == Pane.Transform, hoisted for call-site brevity
    public SKRect PaneBounds { get; }             // pane-local: (0, 0, w, h)

    /// True when the scene has exactly one level. LevelIndex is then -1 (today's sliceIndex sentinel).
    public bool IsSingleLevel { get; }
    public int LevelIndex { get; }

    public float RenderScaling { get; }

    /// Level index for a world Z, or LevelIndex's sentinel semantics when IsSingleLevel.
    public int LevelIndexFor(double worldZ);

    /// True when content at worldZ belongs on this pane. Encodes the "-1 passes everything" rule once.
    public bool BelongsHere(double worldZ);
}

public readonly record struct LevelPaneSnapshot(
    int LevelId, int LevelIndex, MapLevel Level, ViewportTransform Transform, SKRect ViewportRect, int CameraEpoch);
```

### 5.2 Compositor

```csharp
public sealed class SceneCompositor : IDisposable
{
    public SceneCompositor(SceneCompositorOptions? options = null);

    public IReadOnlyList<ISceneLayer> Layers { get; }   // sorted by (Slot, Order, Id)
    public SceneCompositorStats Stats { get; }

    public void Add(ISceneLayer layer);                 // throws on duplicate Id
    public bool Remove(string layerId);
    public ISceneLayer? Find(string layerId);
    public void SetEnabled(string layerId, bool enabled);

    /// UI thread. Runs every enabled layer's Advance once. Returns true while any layer still needs frames.
    public bool Advance(in SceneTime time, Scene2DFrame frame);

    /// Render thread, inside the host's gate. Background fill → panes (clip+translate) → layers → dividers.
    public void Render(SKCanvas canvas, in SceneSubmission submission);

    public void InvalidateCaches();
    public void InvalidatePaneCaches(int levelId);
    public void Dispose();
}

public sealed record SceneCompositorOptions(bool EnablePictureCaching = true, int MaxCachedPictures = 64);

public readonly record struct SceneCompositorStats(
    int LayersRendered, int PicturesRecorded, int PicturesReplayed, int PanesRendered);

/// Everything the draw op is allowed to see. Fully immutable; captured on the UI thread at submission.
public readonly record struct SceneSubmission(
    long SubmissionId,
    Scene2DFrame Frame,
    SceneTime Time,
    IReadOnlyList<LevelPaneSnapshot> Panes,
    ScenePalette Palette,
    RenderPurpose Purpose,
    SKRect HostBounds,
    float RenderScaling);
```

### 5.3 Levels, panes, layout

```csharp
namespace DemoViewer.NET.Playback2D.Core.Levels;

// Correction 3: B3's shape, adopted up front. MapLevel is a CLASS (reference semantics; Radar is
// rebound in place at MapSpace rebuild) and its identity is a distinct struct so a level id can
// never be confused with a level index — the confusion design risk 5 is about.
public readonly record struct MapLevelId(int Key)
{
    public static MapLevelId None => new(int.MinValue);
    public bool IsNone => Key == int.MinValue;
}

public sealed class MapLevel
{
    public required MapLevelId Id { get; init; }
    public required string Name { get; init; }      // display only; may reorder across rebuilds
    public required double ZMin { get; init; }      // quantized to MapSpace.LevelQuantum (64)
    public required double ZMax { get; init; }
    public SKImage? Radar { get; internal set; }
    public string? RadarImageName { get; internal set; }
    public bool HasRadar => Radar is not null;
    public double Span => ZMax - ZMin;
    public double MidZ => (ZMin + ZMax) / 2;
    public bool Contains(double z) => z >= ZMin && z <= ZMax;
}

public sealed class MapSpace
{
    public const double LevelQuantum = 64.0;                       // == FloorSplitter.BucketWidth
    public static double QuantizeZ(double z);                      // B3 D1

    public IReadOnlyList<MapLevel> Levels { get; }
    public RadarBindingQuality RadarBinding { get; }
    public event Action? LevelSetChanged;

    public MapLevel LevelFor(double worldZ);
    public int LevelIndexFor(double worldZ);                       // parity with FloorSplitter.SliceIndexFor
    /// Sticky spatial band given the caller's previous answer. B3 fills in the hysteresis; B1 ships
    /// the overload returning the stateless answer so level assignment cannot regress here.
    public MapLevel LevelFor(double worldZ, MapLevelId? previous);
    public MapLevel? ById(MapLevelId id);
    public int IndexOf(MapLevelId id);                             // -1 when absent

    /// B1 mints ids from QuantizeZ and returns LevelSetChange.None-shaped data; B3 replaces the
    /// minting with overlap-carry matching. The SIGNATURE is fixed here so B3 changes a body, not
    /// a call site. Idempotent: an unchanged band list raises nothing.
    public LevelSetChange Rebuild(IReadOnlyList<FloorSlice> bands,
        IReadOnlyList<SKImage?>? radarByLevel = null,
        RadarBindingQuality quality = RadarBindingQuality.None);
    public LevelSetChange LastChange { get; }
}

public enum RadarBindingQuality { None, Exact, Degraded }

public sealed class LevelPane
{
    public MapLevel Level { get; set; }
    // Public FIELD by contract (design §5.3): B2's PanZoomTool mutates it in place, so a property
    // returning a copy would silently break panning. Carries a justified CA1051 suppression.
    public SliceCamera Camera;
    public ICameraRig Rig { get; set; }
    public SKRect ViewportRect { get; set; }

    public int LevelIndex { get; internal set; }   // [fill-in] 0 = lowest
    public int CameraEpoch { get; internal set; }  // [fill-in] bumped on material camera / rect change
    public LevelPaneSnapshot Snapshot();           // [fill-in]
}

// Correction 3 — B3's member names; SideBySide is reserved and no policy returns it.
public enum LevelDisplayMode { Stacked, Single, SideBySide }

public interface ILevelLayoutPolicy
{
    IReadOnlyList<LevelPane> Arrange(MapSpace space, LevelDisplayMode mode, SKSize host);
}

public sealed class StackedLayout : ILevelLayoutPolicy { /* today's bands */ }

/// [fill-in] Owns pane lifetime + camera identity across relayout. Replaces EnsureCameras/
/// ApplyFitToAllSlices — and is the ONLY such owner: B3's "LevelPaneStore" is additional behaviour
/// on this type (retain state for levels not currently arranged), not a second class.
public sealed class PaneSet
{
    public PaneSet(ILevelLayoutPolicy policy);
    public IReadOnlyList<LevelPane> Panes { get; }
    public ILevelLayoutPolicy Policy { get; set; }

    /// Re-arranges and reconciles by MapLevel.Id: existing panes keep Camera/ManualOverride/Rig
    /// (re-viewport'd); new levels are Fit to `extent`; removed levels are dropped.
    public bool Reconcile(MapSpace space, LevelDisplayMode mode, SKSize host, SKRect extent);

    public void FitAll(SKRect extent);              // ApplyFitToAllSlices (525-532)
    public void ClearManualOverrides();             // Mode setter (172-175)
    public void SetRig(Func<LevelPane, ICameraRig> factory);
    public LevelPane? PaneAt(float x, float y);     // SliceIndexAtScreenY (464-475)
    public void CopySnapshots(List<LevelPaneSnapshot> into);
}
```

### 5.4 Cameras

```csharp
namespace DemoViewer.NET.Playback2D.Core.Cameras;

public interface ICameraRig
{
    string Id { get; }                                                    // [fill-in]
    ViewportTransform? ComputeTarget(LevelPane pane, Scene2DFrame frame);
}

public sealed class ManualRig : ICameraRig { public static readonly ManualRig Instance; }
public sealed class FitMapRig : ICameraRig { }
public sealed class FitAliveRig : ICameraRig
{
    public FitAliveRig(double padding = 0.18, double minHalfWorld = 900);
}
public sealed class FollowPlayerRig : ICameraRig
{
    public FollowPlayerRig(int slot, double halfWorld = 900, double deadzoneHalfWorld = 180);
    public int Slot { get; set; }
    public double DeadzoneHalfWorld { get; set; }
    public void ResetDeadzone();                    // called on SceneTime.IsDiscontinuity
}

public static class CameraAdvancer
{
    public const double LerpResponse = 7.0;
    /// AdvanceCameras (606-641). Returns true while any pane is still settling.
    public static bool Advance(PaneSet panes, Scene2DFrame frame, in SceneTime time);
}
```

### 5.5 Vision seam

```csharp
namespace DemoViewer.NET.Playback2D.Core.Vision;

public sealed record VisionOptions(int ConeRays = 26, float ConeHalfFovDeg = 53f, float ConeMaxRange = 3200f,
    float SightlineHalfFovHDeg = 53f, float SightlineHalfFovVDeg = 37f);

/// World-space solve output. Buffers are pooled and reused; valid only until the next Solve.
public sealed class VisionSolution
{
    public IReadOnlyList<ConePolygon> Cones { get; }
    public IReadOnlyList<SightlineSegment> Sightlines { get; }
    public void Clear();
    public ConePolygon AddCone(int slot, int team, float apexX, float apexY, float apexZ, int rayCount);
    public void AddSightline(in SightlineSegment segment);
}

public sealed class ConePolygon
{
    public int Slot { get; }
    public int Team { get; }
    public float ApexX { get; }  public float ApexY { get; }  public float ApexZ { get; }
    public ReadOnlySpan<float> RayEndsXY { get; }   // 2 floats per ray, world space
    public Span<float> RayEndsWritable { get; }
}

public readonly record struct SightlineSegment(
    int ViewerSlot, int ViewerTeam, float ViewerZ, int TargetSlot, float TargetZ);
    // endpoints are resolved at Render from smoothed marker positions (parity with lines 998-999)

/// The escape hatch's seam. B1 ships exactly one implementation (Pipeline.VisibilityEngineSolver).
public interface IVisionSolver
{
    bool IsReady { get; }
    void Solve(Scene2DFrame frame, VisionSolution into);
}
```

### 5.6 App host + toggle

```csharp
namespace DemoViewer.NET.Modules.Playback2D;

public sealed class Scene2DHost : Control, IPlayback2DSurface, IDisposable
{
    public CameraMode Mode { get; set; }
    public int FollowSlot { get; set; }
    public void FitToExtent();
    public SceneCompositor Compositor { get; }
    public ILevelLayoutPolicy LayoutPolicy { get; set; }

    // Test hooks — SAME NAMES as Playback2DViewport's, so ported tests read identically.
    internal ViewportTransform PrimaryCameraTransform { get; }
    internal bool PrimaryCameraManual { get; }
    internal int SightlineCount { get; }
    internal (float X, float Y)? SmoothedMarkerPosition(int slot);
    internal bool AdvanceMarkers(IReadOnlyList<PlayerMarker> markers, double dt);
    internal bool LeaseUnavailable { get; }        // [fill-in] true once the CPU fallback engaged
}

public interface IPlayback2DSurface
{
    CameraMode Mode { get; set; }
    int FollowSlot { set; }
    void FitToExtent();
}

public enum Playback2DRendererKind { Scene, Legacy }

public static class Playback2DRenderer
{
    /// Resolution order, evaluated once per process:
    ///   1. env DV_PLAYBACK2D_RENDERER = "legacy" | "scene"     (CI / bisecting; absent on WASM)
    ///   2. AppSettings.Playback2D.UseLegacyViewport            (developer-mode Diagnostics toggle)
    ///   3. Scene (default)
    public static Playback2DRendererKind Selected { get; }
    internal static void ResetForTest(Playback2DRendererKind? forced);   // [fill-in]
}
```

```csharp
namespace DemoViewer.NET.Configuration;

/// Binder-safe. MUST be added to SettingsService.WriteInMemory (design §5.4, §8) or WASM writes
/// vanish. ONE section for the whole module: B2/B3/B4/C2 ADD properties here (full canonical
/// property list in plans/B5-polish-wasm.md §"Public API contracts"); nobody adds a sibling section.
public sealed class Playback2DSettings
{
    /// Internal parity escape hatch — retained one release, then deleted with the old control (B5).
    /// Named LegacyViewport (correction 5), which is the name B5's removal plan cites.
    public bool LegacyViewport { get; set; }
}
```

### 5.7 Benchmark harness (Pipeline)

```csharp
namespace DemoViewer.NET.Playback2D.Pipeline.Benchmarking;

public sealed record BenchmarkRequest(
    int Frames, SKSizeI Size, int WarmupFrames = 64,
    IReadOnlySet<string>? LayerIds = null, bool MeasureAllocations = true, double Speed = 1.0);

public readonly record struct FrameTimeStats(double P50Ms, double P95Ms, double P99Ms, double MaxMs, double MeanMs);

public sealed record BenchmarkReport(
    string Id, int Frames, SKSizeI Size, RenderBackend Backend,
    FrameTimeStats Advance, FrameTimeStats Render, FrameTimeStats Total,
    long AllocatedBytesPerFrame, long AllocatedBytesTotal, DateTimeOffset RunUtc)
{
    public string ToJson();
    public void WriteToBenchReports(string directory);   // bench-reports/dv2d-<Id>-<yyyyMMdd-HHmmss>.json
}

// Correction 8: these five names are canonical for the whole track — C1's `dv2d bench` is a thin
// wrapper over exactly this, and C1's SceneBenchHarness/SceneBenchRequest/SceneBenchResult names
// are withdrawn. The measurement loop times Advance and Render from OUTSIDE Core (Core bans
// Stopwatch, §5.1), rendering through C1's HeadlessSceneRenderer facade.
public sealed class ScenePipelineBenchmark
{
    public ScenePipelineBenchmark(SceneCompositor compositor, IRenderSurfaceProvider surfaces,
        ILevelLayoutPolicy layout, ScenePalette palette);
    public BenchmarkReport Run(ISceneFrameSource source, BenchmarkRequest request, CancellationToken ct = default);
}

public sealed record BudgetPolicy(double AdvanceP99Ms, double RenderP99Ms, long AllocatedBytesPerFrame)
{
    public static readonly BudgetPolicy Baseline = new(2.0, 8.0, 0);   // design §6
    /// Baseline scaled by env DV2D_BUDGET_SCALE (default 2.0) — CI runners are not the design's baseline laptop.
    public static BudgetPolicy Ci { get; }
    public IReadOnlyList<string> Violations(BenchmarkReport report);
}
```

`ISceneFrameSource` is the design §5.7 interface; B1 consumes it and supplies a
`FixtureFrameSource` (Pipeline) replaying `SceneFixture` frames. `TrackerFrameSource` is **B4's**.

### 5.8 The render-gate threading contract (**read this before writing T12**)

```csharp
/// One per Scene2DHost. Serializes the UI thread's Advance+submit against the render thread's draw op,
/// because SceneCompositor's picture caches and each layer's Advance-built buffers are shared mutable state.
public sealed class SceneRenderGate
{
    public IDisposable Enter();          // plain Monitor; no re-entrancy, no nested locks
    public bool IsHeld { get; }          // [fill-in] debug assert only
}
```

**Captured at submission** (UI thread, inside the gate, in `Scene2DHost.AdvanceAndSubmit`):

- the `Scene2DFrame` **reference** — immutable by contract, built by `SceneFrameBuilder` and never mutated
  after publication;
- the `SceneTime` **value**;
- one `LevelPaneSnapshot` **value** per pane (`ViewportTransform` and `SKRect` are value copies — the
  mutable `LevelPane`/`PaneSet` never crosses the thread boundary);
- the `ScenePalette` **reference** (immutable record, swapped wholesale on theme change);
- `RenderPurpose`, `HostBounds`, `RenderScaling`, and a monotonic `SubmissionId`;
- the shared `SceneCompositor` reference and the `SceneRenderGate` — **the only two mutable objects the op
  touches, and it touches them only inside the gate.**

**The op MAY touch:** the captured `SceneSubmission`, the `SceneCompositor` (inside the gate), and the
`SKCanvas` obtained from the lease.

**The op MUST NOT touch:** the `Playback2DTabViewModel` or any VM state; any Avalonia `Control`,
`Visual`, or property; `PaneSet`/`LevelPane`/`MapSpace`; `Dispatcher`; `VisibilityEngine`; any layer field
written outside the gate; `Bounds`/`ActualThemeVariant`. Enforce with a debug `Debug.Assert(gate.IsHeld)`
at the top of every compositor cache mutation, and with test 19 (§6).

**Ordering.** `AdvanceAndSubmit` runs entirely on the UI thread inside the gate: `MapSpace` rebuild →
`PaneSet.Reconcile` → `CameraAdvancer.Advance` → `compositor.Advance` → `CopySnapshots` → build
`SceneSubmission` → release gate → `context.Custom(op)`. The op re-enters the gate for the duration of
`compositor.Render` only. Neither side acquires any other lock while holding the gate, so the design is
deadlock-free by construction. Advance is budgeted at ≤2 ms (§6), so worst-case UI stall from contention
is one Advance.

**Lifetime.** `SKPicture`s live in the compositor and are disposed under the gate. `SKImage` radar handles
are owned by `LoadedMapAsset` and disposed at Background priority after a map swap
(`Playback2DTabViewModel.cs:538-548`) — unchanged, and now genuinely load-bearing because the render
thread may hold a picture referencing the image; the gate plus one dispatcher hop covers it.

**Deterministic `SceneTime`.** `DeltaSeconds` comes from the RAF timestamp, clamped `[1/240, 1/15]` exactly
as line 852, and is the **only** wall-clock reading in the whole pipeline — it happens in the App, and Core
receives it as data. `Tick`/`FrameIndex`/`DemoSeconds`/`IsDiscontinuity` come from `Scene2DFrame`. No layer
may call `DateTime`, `Stopwatch`, `Environment.TickCount`, or `Random`; the architecture test enforces it.

---

## 6. Test plan

Two execution modes, per design §11:

- **Direct-execution** — no Avalonia platform, no window, no dispatcher. Construct `Scene2DFrame`s (or load
  `SceneFixture` JSON), run the compositor against `CpuSurfaceProvider`, assert on pixels or geometry.
  Project: `src/Playback2D/DemoViewer.NET.Playback2D.Tests` (TUnit, `OutputType=Exe`).
- **Headless-Avalonia** — only for tests that genuinely exercise the Avalonia host. Project:
  `src/App/DemoViewer.NET.App.Tests` (existing `HeadlessSession`).

### 6.1 Direct-execution tests (`DemoViewer.NET.Playback2D.Tests`)

| # | Class | Cases |
|---|---|---|
| 1 | `SceneCompositorOrderTests` | interleaved `(Slot, Order)` sort; `Id` tiebreak is deterministic across two constructions; disabled layer is skipped in Advance **and** Render; each layer rendered exactly once per pane; duplicate-Id `Add` throws |
| 2 | `LayerCachePictureTests` | `Static` records once, replays under the camera matrix, re-records only on `ContentVersion`; `PerCamera` re-records on `CameraEpoch` bump, not otherwise; `Dynamic` never records; pane removal disposes its pictures; `MaxCachedPictures` evicts LRU |
| 3 | `MapSpaceTests` | level `Id` is quantized `ZMin`; rebuild preserves ids for unchanged bands and mints new ones for new bands; `LevelIndexFor` matches `FloorSplitter.SliceIndexFor` over a 200-value Z table spanning bands and gaps (**parity oracle**); `LevelSetChanged` fires once per rebuild; sticky overload holds inside hysteresis |
| 4 | `MapRadarBindingTests` | the three rules of §4 T5 against a decision table captured from `ResolveRadarImage` (1096–1115); `Degraded` flag + per-level `HasRadar`; zero LINQ allocation on the hot path |
| 5 | `StackedLayoutTests` | pane rects equal `height/count` bands, highest level top; `PaneAt(x, y)` matches `SliceIndexAtScreenY` for a Y table at 1/2/3/4 levels; single-level pane is the full host rect |
| 6 | `PaneSetReconcileTests` | camera + `ManualOverride` + `Rig` carried across a rebuild **keyed by level id, not index** (design risk 5): insert a *lower* level and assert the original pane keeps its pan/zoom; new level is Fit-ed; removed level's pane and pictures are dropped |
| 7 | `CameraRigTests` + `FollowPlayerRigDeadzoneTests` | `FitMapRig`/`FitAliveRig`/`ManualRig` reproduce values captured from `TryComputeTarget`/`TryFitAlive`/`TryFollow` on a fixture roster (numeric parity, 1e-9); alive-fit falls back correctly when a level has no alive players; follow holds when the slot has no marker; deadzone: no target change inside 180 u, recentre outside, `DeadzoneHalfWorld = 0` is byte-identical to today |
| 8 | `MarkerSmoothingTests` | port of `Playback2DInterpolationTests` against `MarkerLayer.Advance`: first-appearance snap, glide converges, teleport snap at ≥250 u, prune on leave, rejoin snaps; **plus** `IsDiscontinuity` snaps all |
| 9 | `TrailGeometryTests` | port of `GrenadeTrailFloorSplitTests` (both overloads); the non-allocating overload produces identical runs and allocates 0 bytes |
| 10 | `SceneDeterminismTests` | two runs of the same 128-frame fixture with the same `dt` produce identical per-frame SHA-256 of the CPU surface; layer registration order does not change output; `RenderPurpose.Export` at fixed `dt` matches `Interactive` fed the same `dt` |
| 11 | `CoreArchitectureTests` | Core's assembly references contain **only** SkiaSharp + BCL (no `Avalonia*`, no `CS2DemoKit*`); Pipeline has no `Avalonia*`; banned-API IL scan over Core for `DateTime`, `DateTimeOffset`, `Stopwatch`, `Random`, `Environment.TickCount*` (extends B0's test) |
| 12 | `ZeroAllocationTests` `[Category("Budget")]` | 512-frame full-scene run after 64 warmup frames: `GC.GetAllocatedBytesForCurrentThread()` delta **== 0**; a per-layer breakdown is printed on failure so the culprit is named |
| 13 | `FrameBudgetTests` `[Category("Budget")]` | `ScenePipelineBenchmark` over the standard fixture: `Advance` p99 ≤ policy, `Render` p99 ≤ policy, on `CpuSurfaceProvider`; report written to `bench-reports/` |
| 14 | `GoldenParityTests` | **the exit criterion.** For each B0 golden: render via the compositor on `CpuSurfaceProvider` and compare. Tier A (**byte-exact**): text layers disabled (`playback2d.floorlabel` off, `MarkerLayer.DrawLabels = false`) — any diff is a hard failure. Tier B (**perceptual**): everything on — per-channel ≤ 8/255 on ≥ 99.5 % of pixels, plus a label-bounding-box containment assert. Diff PNGs written to the artifact dir; `DV2D_UPDATE_GOLDENS=1` rewrites them (never set in CI) |

### 6.2 Headless-Avalonia tests (`DemoViewer.NET.App.Tests`)

| # | Class | Cases |
|---|---|---|
| 15 | `Scene2DHostRenderTests` `[NotInParallel]` `[Category("Integration")]` | mirrors `ZRadarRenderTests`: real demo + baked bundle → `Scene2DHost` in a headless window → Skia frame capture; assert non-background pixel counts (Nuke 2 levels > 400 000, dust2 1 level > 100 000) and save `scene2d-nuke.png` / `scene2d-dust2.png` to `HeadlessSession.ArtifactDir`; `SkipTestException` when demo/bundle absent |
| 16 | `Scene2DHostInputTests` | press+move on the top band pans **only** that pane and sets `ManualOverride`; wheel zooms about the **band-local** cursor Y (line 451); mode change clears every override (172–175); `FitToExtent` re-fits all. Ports `Playback2DCameraModeTests` assertions onto `PrimaryCameraTransform`/`PrimaryCameraManual` |
| 17 | `Scene2DHostLeaseFallbackTests` | force `LeaseUnavailable`; the `WriteableBitmap` path renders and is perceptually equal to the lease path; a resize reallocates the bitmap; no leak across 100 resizes |
| 18 | `Playback2DRendererToggleTests` | `DV_PLAYBACK2D_RENDERER=legacy` puts a `Playback2DViewport` in `ViewportHost`; `scene`/unset puts a `Scene2DHost`; `AppSettings.Playback2D.LegacyViewport` is honoured when the env var is absent; both satisfy `IPlayback2DSurface` and the mode menu drives both |
| 19 | `RenderGateStressTests` `[Category("Integration")]` | 5 s of `AdvanceAndSubmit` on the UI thread against a worker thread replaying the op: no exception, no torn picture (each rendered frame hashes to one of the submitted states, never a blend), `SubmissionId` monotonic. Design risk 2 |

### 6.3 Carried forward unchanged

`Playback2DInterpolationTests`, `GrenadeTrailFloorSplitTests`, `Playback2DCameraModeTests`,
`FloorSplitterTests`, `FloorSplitterMultiFloorTests`, `FloorAssetConsumptionTests`, `ZRadarRenderTests`,
`ZTrajectoryRenderTests`, `ZVisionOverlayRenderTests`, `Playback2DRosterReseedTests`,
`Playback2DModuleLifecycleTests` — they keep targeting the **legacy** control, which is still live behind
the toggle. They are the safety net that proves the toggle actually works.

### 6.4 Fixtures

- Scene fixtures + goldens: C1's corpus layout (correction 7) — `tests/fixtures/playback2d/scenes/`
  and `tests/fixtures/playback2d/goldens/cpu/<name>@<w>x<h>.png` (B0 creates the tree; C1 owns
  `manifest.json`). B1 adds the canonical entries `nuke-multilevel` (stacked, two levels, both radar
  images), `mirage-single-level`, and `full-scene-budget` — the last is the benchmark's standard
  fixture: 10 players, 4 trails, 12 area effects, vision on, bomb planted.
- Demos: resolved by the existing `DemoTestHelper` ladder (`DEMO_PATH` → `TestData/` → `demos/benchmarks/`
  → `demos/`), `SkipTestException` when absent.

### 6.5 Commands

```bash
# Direct-execution suite (fast, no Avalonia) — the everyday loop
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

# Budget lane only
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release -- \
  --treenode-filter "/*/*/*/*[Category=Budget]"

# One class
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release -- \
  --treenode-filter "/*/*/GoldenParityTests/*"

# Headless-Avalonia host tests (batched — the App suite is OOM-prone in one process)
bash scripts/test-app-suite.sh -c Release

# Force the legacy control for a bisect
DV_PLAYBACK2D_RENDERER=legacy dotnet run --project src/App/DemoViewer.NET.Desktop -c Debug
```

---

## 7. Build & wiring

### 7.1 `Directory.Packages.props` additions

```xml
<!-- The 2D scene core draws on raw Skia. The version is PINNED to whatever Avalonia 11.3.12 resolves
     (2.88.9, verified in project.assets.json) because ISkiaSharpApiLeaseFeature hands the custom draw
     op an SKCanvas from *Avalonia's* SkiaSharp: a Core built against SkiaSharp 3.x is a different type
     and will not bind. Bump this ONLY in lockstep with an Avalonia bump, and re-verify the resolved
     version afterwards. tools/DemoViewer.NET.AssetBaker deliberately opts out of CPM and stays on
     SkiaSharp 3.x — that is not a conflict, it is a file boundary. -->
<PackageVersion Include="SkiaSharp" Version="2.88.9"/>
<PackageVersion Include="SkiaSharp.NativeAssets.Linux" Version="2.88.9"/>
```

`SkiaSharp.NativeAssets.Linux` is needed by the direct-execution test project so `CpuSurfaceProvider`
works on the CI runner without Avalonia's head packages dragging it in. Windows/macOS native assets arrive
through Avalonia on the app heads.

**Version policy:** SkiaSharp is a *derived* pin, not an independent choice. It is slaved to Avalonia. Any
Avalonia bump must (a) re-read the resolved SkiaSharp version out of `project.assets.json` and (b) update
both lines above in the same commit. Never let CPM float it.

### 7.2 New projects

`src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj`
*(created by B0; B1 adds the `InternalsVisibleTo` if absent)*

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <RootNamespace>DemoViewer.NET.Playback2D.Core</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
        <!-- SkiaSharp ONLY. No Avalonia, no CS2DemoKit, no Modules.Abstractions.
             Enforced by CoreArchitectureTests — adding a reference here is a design violation. -->
        <PackageReference Include="SkiaSharp"/>
    </ItemGroup>
    <ItemGroup>
        <InternalsVisibleTo Include="DemoViewer.NET.Playback2D.Pipeline"/>
        <InternalsVisibleTo Include="DemoViewer.NET.Playback2D.Tests"/>
    </ItemGroup>
</Project>
```

`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <RootNamespace>DemoViewer.NET.Playback2D.Pipeline</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
        <!-- Core + the parser/analysis packages. Still NO Avalonia. -->
        <PackageReference Include="CS2DemoKit.Parser"/>
        <PackageReference Include="CS2DemoKit.Analysis"/>
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
    </ItemGroup>
    <ItemGroup>
        <InternalsVisibleTo Include="DemoViewer.NET.Playback2D.Tests"/>
    </ItemGroup>
</Project>
```

`src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Tests.csproj` **(B1 creates)**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <!-- Distinct from the tested assemblies to avoid a namespace/type collision. -->
        <RootNamespace>DemoViewer.NET.Playback2DTests</RootNamespace>
        <!-- CA1707: test method names conventionally use underscores. -->
        <NoWarn>$(NoWarn);CA1707</NoWarn>
        <!-- Deterministic allocation measurement: the zero-allocation assertion is meaningless
             if a background GC thread is churning under it. -->
        <ServerGarbageCollection>false</ServerGarbageCollection>
        <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="TUnit"/>
        <!-- Native raster backend for CpuSurfaceProvider on the Linux CI runner (no Avalonia head here). -->
        <PackageReference Include="SkiaSharp.NativeAssets.Linux" Condition="'$([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform($([System.Runtime.InteropServices.OSPlatform]::Linux)))' == 'true'"/>
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Pipeline\DemoViewer.NET.Playback2D.Pipeline.csproj"/>
        <ProjectReference Include="..\..\Testing\DemoViewer.NET.TestSupport\DemoViewer.NET.TestSupport.csproj"/>
    </ItemGroup>
</Project>
```

If the conditional `PackageReference` proves awkward, reference `SkiaSharp.NativeAssets.Linux`
unconditionally — it is a no-op payload on other platforms.

### 7.3 `DemoViewer.NET.slnx`

```xml
<Folder Name="/src/Playback2D/">
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Tests.csproj"/>
</Folder>
```

Insert after the `/src/App/` folder block. (B0 may have added the first two; add only what is missing.)

### 7.4 `src/App/DemoViewer.NET/DemoViewer.NET.csproj`

```xml
<ItemGroup>
    <!-- ISkiaSharpApiLeaseFeature for Scene2DHost's custom draw op. Present transitively via the
         Desktop/Browser heads, but the type needs a direct compile-time reference here. -->
    <PackageReference Include="Avalonia.Skia"/>
</ItemGroup>
<ItemGroup>
    <ProjectReference Include="..\..\Playback2D\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
    <ProjectReference Include="..\..\Playback2D\DemoViewer.NET.Playback2D.Pipeline\DemoViewer.NET.Playback2D.Pipeline.csproj"/>
</ItemGroup>
```

### 7.5 CI — `.github/workflows/ci.yml`

CI currently builds only the Desktop head and runs **no tests**. Add two jobs. Both run a
direct-execution project with no Avalonia platform, so neither inherits the App suite's OOM problem.

```yaml
  playback2d-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      # Direct-execution scene tests: no Avalonia platform, no window, no dispatcher — safe in CI
      # in a way the App UI suite is not. Goldens are authored on the CPU provider, which is the
      # contract baseline (design §5.8), so this lane is authoritative for correctness.
      - name: Playback2D scene suite
        run: dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
             -- --treenode-filter "/*/*/*/*[Category!=Budget]"

  playback2d-budget:
    runs-on: ubuntu-latest
    needs: playback2d-tests
    env:
      # A GitHub hosted runner is not the design §6 baseline laptop. Gate on 2x the baseline so the
      # lane catches real regressions (an O(n) blow-up, a re-introduced per-frame allocation) without
      # firing on runner noise. The strict §6 numbers are what local `dv2d bench` reports against.
      DV2D_BUDGET_SCALE: '2.0'
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - name: Frame-time + allocation budget
        run: dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
             -- --treenode-filter "/*/*/*/*[Category=Budget]"
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: bench-reports
          path: bench-reports/dv2d-*.json
```

The zero-allocation assertion is **not** scaled by `DV2D_BUDGET_SCALE` — zero is zero on every machine.

---

## 8. Dependencies

### 8.1 Consumed from other phases

| From | Symbol / signature | Used by |
|---|---|---|
| **B0** | `Scene2DFrame` (shape in §2.1) | every layer, `SceneRenderContext`, `SceneSubmission` |
| **B0** | `readonly record struct SceneTime(int Tick, int FrameIndex, double DemoSeconds, double DeltaSeconds, bool IsDiscontinuity)` | `ISceneLayer.Advance`, `CameraAdvancer`, `Scene2DHost` |
| **B0** | `enum RenderPurpose { Interactive, Export, Thumbnail }` | `SceneRenderContext` |
| **B0** | `ViewportTransform`, `SliceCamera` (moved verbatim) | `LevelPane`, all rigs, every layer |
| **B0** | `IRenderSurfaceProvider`, `CpuSurfaceProvider`, `enum RenderBackend` | `ScenePipelineBenchmark`, `GoldenParityTests` |
| **B0** | `SceneFrameBuilder` | `Playback2DTabViewModel` → `Scene2DHost` |
| **B0** | `SceneFixture` + golden corpus under `tests/fixtures/playback2d/` | tests 10, 13, 14 |
| **B0** | Core architecture / banned-API test | extended by test 11 |
| **A1** | `ITimelineTrack`, `TimelineMarker`, `ITimelineData` — **signatures frozen by A1** | moved to Core by B1 T1; no B1 consumer |
| existing | `CS2DemoKit.Analysis.Visibility.{VisibilityEngine, VisibilityAnalyzer, PlayerVantage}` | Pipeline `VisibilityEngineSolver` only |
| existing | `Playback2DTabViewModel` (`MapAsset`, `VisionEngine`, `FrameUpdated`, `NotifyFollowSlotChanged`) | `Scene2DHost` |
| existing | `ThemeColors.Get(string, ThemeVariant?, string)` | `ScenePaletteFactory` |
| existing | `IFeatureGate.IsEnabled(string)` | compositor layer enablement (§7.7 ids land in B2–B4; B1 wires the seam) |

### 8.2 Exported by B1

| Symbol | Consumed by |
|---|---|
| `ISceneLayer`, `LayerSlot`, `LayerCacheHint`, `SceneRenderContext` | **B2** (`AnnotationLayer`), **B4** (`ClockLayer`, `KillFeedLayer`) |
| `SceneCompositor`, `SceneSubmission`, `SceneCompositorStats` | **B2**, **B4**, **C1**, **C2** |
| `MapSpace`, `MapLevel`, `LevelPane`, `LevelPaneSnapshot`, `ILevelLayoutPolicy`, `PaneSet`, `LevelDisplayMode` | **B2** (`SpaceRef.World(LevelMinZ)` remapping), **B3** (`SingleLayout`, AutoFollow) |
| `ICameraRig`, `FollowPlayerRig`, `CameraAdvancer`, `CameraRigFactory` | **B3** (AutoFollow), **B4** (`CameraScript`) |
| `IVisionSolver`, `VisionSolution`, `VisionOptions` | the deferred off-thread solver (design §6), **C1** (`--layers` flags) |
| `ScenePalette`, `TextBlobCache` | **B2** (ink styling), **B4** (HUD layers) |
| `Scene2DHost`, `IPlayback2DSurface`, `PanZoomGesture` | **B2** (`InputToolRouter` wraps `PanZoomGesture` as `PanZoomTool`), **B3** (level strip) |
| `MapAssetPipeline`, `LoadedMapAsset` (`SKImage` radar), `MapRadarBinder`, `RadarBindingQuality` | **B3** (no-radar UI state), **C1** |
| `ScenePipelineBenchmark`, `BenchmarkRequest`, `BenchmarkReport`, `BudgetPolicy`, `FrameTimeStats` | **C1** (`dv2d bench` is a CLI wrapper over exactly this) |
| `Playback2DSettings`, `Playback2DRenderer` | **B5** (old-control removal deletes both) |

---

## 9. Decisions made

Where the design left a choice open, this is the call. Each is reversible cheaply except where noted.

- **D-1 — SkiaSharp is pinned to 2.88.9, slaved to Avalonia.** Not a preference: the lease hands the op
  Avalonia's `SKCanvas` type. This constrains the API surface (see T0) and is the single most likely
  source of a nasty surprise. *(Not cheaply reversible.)*
- **D-2 — `VisionLayer` splits across the assembly boundary.** The design lists `VisionLayer` in Core, but
  `VisibilityEngine` is a CS2DemoKit type and Core references SkiaSharp only. Core owns the layer and an
  `IVisionSolver` seam; Pipeline owns `VisibilityEngineSolver`. This is also exactly where the deferred
  off-thread solver plugs in. **Biggest structural fill-in in B1.**
- **D-3 — Rig naming vs. today's `CameraMode`.** `CameraMode.Fit` (a one-shot fit, then static) maps to
  `ManualRig`, and `CameraMode.Map` (a continuous fit) maps to `FitMapRig`. The names read backwards; the
  behaviours are what matter. `CameraMode` stays in the App as the menu's vocabulary.
- **D-4 — `ILevelLayoutPolicy.Arrange` keeps the design's exact signature**; pane reuse and camera
  identity live in a separate `PaneSet` that reconciles the policy's output by `MapLevel.Id`. This keeps
  the design contract intact *and* fixes risk 5 (never key panes by index).
- **D-5 — `Static` has no B1 consumer.** The radar's single `DrawImage` and the grid's ~800 `DrawLine`s
  share one `PerCamera` picture; splitting the layer to give `Static` a customer would be contortion. The
  `Static` mechanism is built and unit-tested against a synthetic layer for B2's dry ink.
- **D-6 — Picture recording space is hint-dependent:** `Static` records in world space and replays under
  the camera matrix; `PerCamera` records in pane-local screen space. This is why both hints exist.
- **D-7 — The on-screen CPU fallback does not use `CpuSurfaceProvider`.** It creates an `SKSurface`
  directly over the `WriteableBitmap`'s locked framebuffer, avoiding a full-frame `ReadPixels` copy every
  frame. The provider seam remains what offscreen consumers use.
- **D-8 — Dynamic layers draw in screen space, transforming points themselves**, exactly as today
  (`transform.WorldToScreen` per point). Setting a world→screen matrix on the canvas would scale stroke
  widths and marker radii, breaking pixel parity. Only the radar image and the `Static` replay path use a
  matrix.
- **D-9 — The internal toggle burns no `FeatureCatalog` id.** Catalog ids are permanent persisted keys
  (§7.7) and this toggle is deliberately temporary (deleted in B5). It is an env var
  (`DV_PLAYBACK2D_RENDERER`) over a developer-mode-only `AppSettings.Playback2D.LegacyViewport`, and
  both controls coexist behind `IPlayback2DSurface` inside a `ContentControl`.
- **D-10 — `IPointerTool`/`InputToolRouter` are B2's.** B1 ships pan/zoom as a self-contained
  `PanZoomGesture` class that B2 wraps as `PanZoomTool` without touching the host.
- **D-11 — `IsDiscontinuity` is authored by B0's `SceneFrameBuilder`** (it owns the frame-index history and
  already implements the trail-clear rule at `Playback2DTabViewModel.cs:~628`). If B0's frame lacks it,
  `Scene2DHost` computes it from `frame.FrameIndex` deltas as a fallback — same thresholds.
- **D-12 — Marker/weapon `SKImage` sprites are built but off by default.** §5.2 asks for sprites; the exit
  criterion asks for pixel parity, and a sprite blit does not AA identically to `DrawCircle`. `SpriteAtlas`
  and the sprite draw path ship behind `SceneRenderOptions.UseSprites = false`, with a bench comparison in
  the T18 write-up. Flip it only if §6 forces it, and re-baseline goldens if so.
- **D-13 — The vision raycasts move from Render to Advance.** Required by the purity split; pixel-identical
  and strictly cheaper. The only non-mechanical change to a ported pass.
- **D-14 — CI budgets are gated at 2× the design's numbers** via `DV2D_BUDGET_SCALE`; the allocation
  assertion is not scaled. The strict §6 numbers are what a local run reports against.
- **D-15 — `MapSpace.LevelIndexFor` is a parity clone of `FloorSplitter.SliceIndexFor`** (contains-first,
  nearest-`MidZ` fallback), pinned by a table oracle. The hysteresis overload the design mentions exists but
  is unused until B3, so B1 cannot regress level assignment.
- **D-16 — `MapAssetLoader` splits, it does not move wholesale.** `TryLoadRadarThumbnail` stays in the App
  (it feeds an Avalonia `Bitmap` to a library card and has no `SKImage` analogue with downscale-on-decode).
- **D-17 — Text parity is a review gate, not an assert.** Tier A goldens run with text disabled and are
  byte-exact; Tier B runs everything and is perceptual. Every text difference is written up and reviewed
  (design risk 1's "reviewed, not auto-failed").

---

## 10. Risks & spikes

| # | Risk | Likelihood / impact | Mitigation | Time-box |
|---|---|---|---|---|
| R1 | **SkiaSharp 2.88.9's API differs from what the plan assumes** (`SKSamplingOptions` overloads, `SKTextBlob` factories, `SKPath.ArcTo` sweep semantics) | M / M | **T0 spike** before any layer is written; write down the chosen overloads | **1 h** |
| R2 | **The lease canvas's matrix/clip state is not what we expect** (render scaling already applied? clip already set to the op's bounds?) | M / H | **Spike inside T12**: render a 1 px red border at the op's `Bounds` under the lease at 100 %/150 %/200 % scaling and inspect; adjust the submission's `RenderScaling` handling accordingly | **2 h** |
| R3 | **Text metrics differ from `FormattedText`** enough that marker labels shift by a pixel or two and every golden diffs | **H** / M | Two-tier goldens (test 14); labels centred on measured `SKTextBlob` bounds; differences reviewed in `B1-text-metrics-review.md`, not auto-failed. Design risk 1 | — |
| R4 | **Radar `DrawImage` under a world-space matrix samples differently** from today's screen-space dest rect | M / M | Pin `SKSamplingOptions` explicitly; if the Tier-A golden still diffs, fall back to computing the screen dest rect exactly as line 1080–1082 (the fallback is one line and costs nothing) | **2 h** if it fires |
| R5 | **Render-thread race against layer caches** (design risk 2) | M / **H** | Advance/Render purity split; `SceneRenderGate`; ops consume immutable snapshots; `Debug.Assert(gate.IsHeld)` on every cache mutation; `RenderGateStressTests` in CI | — |
| R6 | **Zero-allocation is missed by a boxed enumerator or a hidden closure** and the assertion becomes a nag | M / M | Test 12 prints a per-layer allocation breakdown on failure; T15 is a dedicated task, not a "while I'm here"; indexed `for` loops are a review checklist item | — |
| R7 | **The 64 fps floor is missed on baseline hardware, vision dominant** (design risk 6) | M / M | Budget gates from B1 (test 13); `IVisionSolver` seam already exists so the fix is a wrapper, not a refactor; **do not** degrade visuals first | **1 day** if it fires (build `DeferredVisionSolver`) |
| R8 | **B0's `Scene2DFrame` shape differs from §2.1** and half the layers need adapting | M / M | §2.1 states the minimum contract explicitly; adapt at call sites, never change semantics; raise to the integrator immediately rather than forking the frame type | — |
| R9 | **A1's `ITimelineTrack` cannot legally live in Core** (transitive App/parser refs) | M / L | Land it in Pipeline instead; do **not** weaken the architecture test. Integrator decision | — |
| R10 | **The new deadzone changes follow-camera feel** in a way a user notices as a regression | L / L | `DeadzoneHalfWorld` is a constructor arg; `0` is byte-identical to today; the parity test uses `0` and a separate test covers the deadzone | — |
| R11 | **CI budget lane is flaky on shared runners** and gets muted | M / M | `DV2D_BUDGET_SCALE=2.0`; p99 (not max); the allocation assertion — which is machine-independent — carries most of the regression-catching value | — |
| R12 | **`WriteableBitmap` fallback path rots** because it never runs in normal use | M / M | Test 17 exercises it explicitly on every run; `LeaseUnavailable` is an `internal` test hook so it can be forced | — |

---

## 11. Acceptance checklist

### Mapped 1:1 to the design's B1 exit criterion

- [ ] **Pixel-parity (± reviewed text metrics) vs B0 goldens.** `GoldenParityTests` Tier A (text disabled)
      is **byte-exact** on every B0 golden; Tier B (text enabled) is within the perceptual threshold; every
      text difference is documented and signed off in `docs/playback2d-v2/plans/B1-text-metrics-review.md`.
- [ ] **p99 ≤ budget on CPU baseline.** `FrameBudgetTests` green against `BudgetPolicy.Baseline` locally
      (`Advance` p99 ≤ 2 ms, `Render` p99 ≤ 8 ms) and against `BudgetPolicy.Ci` in CI.
- [ ] **Old control behind toggle.** `DV_PLAYBACK2D_RENDERER=legacy` and
      `AppSettings.Playback2D.UseLegacyViewport=true` both restore `Playback2DViewport`; the default is
      `Scene2DHost`; every carried-forward legacy test still passes.

### B1's own additions

- [ ] All seven passes render through `ISceneLayer.Render(SKCanvas, SceneRenderContext)`; `DrawSection`'s
      order is reproduced by the `(Slot, Order, Id)` sort.
- [ ] `SceneCompositor` implements all three `LayerCacheHint` modes; `Static` is covered by a synthetic-layer
      test even though no B1 layer declares it.
- [ ] `Scene2DHost` renders through one `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`, and the
      `WriteableBitmap` fallback is exercised by test 17 on every run.
- [ ] `MapSpace`/`MapLevel`/`LevelPane`/`StackedLayout` reproduce today's bands; `PaneAt` matches
      `SliceIndexAtScreenY`; `LevelIndexFor` matches `FloorSplitter.SliceIndexFor` on the table oracle.
- [ ] Pane identity survives a `MapSpace` rebuild that **inserts a lower level** (design risk 5) — the
      original pane keeps its camera and manual override.
- [ ] All four rigs implemented; `FollowPlayerRig` has a deadzone; `DeadzoneHalfWorld = 0` is byte-identical
      to `TryFollow`.
- [ ] `Advance` never draws and `Render` never mutates; `Debug.Assert(gate.IsHeld)` guards every compositor
      cache mutation; `RenderGateStressTests` green.
- [ ] The RAF loop still self-terminates: after cameras settle and markers stop gliding, no further frames
      are requested (assert via a frame counter over 2 s of idle).
- [ ] `SceneTime.DeltaSeconds` is the only wall-clock reading in the pipeline and it happens in the App;
      `CoreArchitectureTests` banned-API scan green.
- [ ] `SceneDeterminismTests` green: identical fixture + identical `dt` → identical per-frame hashes.
- [ ] **Zero steady-state allocations**: 512-frame run after warmup allocates 0 bytes; every item in the
      T15 list is struck off.
- [ ] `ResolveRadarImage` is deleted; radar binding is explicit per level, computed once per rebuild, and
      exposes a visible "no radar for this level" state.
- [ ] Radar decodes to `SKImage` via `MapAssetPipeline`; `LoadedMapAsset.Dispose` still releases them;
      `TryLoadRadarThumbnail` stays in the App.
- [ ] `ScenePipelineBenchmark` produces a `BenchmarkReport` and writes `bench-reports/dv2d-*.json`; both CI
      jobs are green on a PR.
- [ ] `A1`'s timeline types are moved (to Core, or to Pipeline per R9) with signatures unchanged.
- [ ] `Playback2DSettings` is added to `SettingsService.WriteInMemory` (WASM would silently drop it
      otherwise).
- [ ] `DemoViewer.NET.slnx` lists all three Playback2D projects; `dotnet build src/App/DemoViewer.NET.Desktop -c Release`
      is clean with `TreatWarningsAsErrors=true`.
- [ ] `Scene2DHostRenderTests` saves `scene2d-nuke.png` / `scene2d-dust2.png` to the artifact dir and a human
      has eyeballed world→radar alignment against the legacy captures.

---

## Implementation notes (deviations)

Written at implementation time. Everything not listed here was built as the plan and the
`Integrator corrections` block specify.

### Typed identity, where the plan body still said `int`

1. **`LevelPaneSnapshot.LevelId` and `SceneCompositor.InvalidatePaneCaches` take `MapLevelId`, not
   `int`.** §5.1 and §5.2 were written before integrator correction 3 made level identity a typed
   struct; keeping `int` there would have reintroduced exactly the id-vs-index confusion design risk 5
   is about, one layer below where correction 3 fixed it. Call sites read `snapshot.LevelId.Key` for
   the raw value. **B2/B3/B4 see a typed id everywhere.**

2. **`PaneSet.Reconcile` and `PaneSet.FitAll` take `WorldBounds`, not `SKRect`.** Registry §3.2 is
   explicit that world bounds are `WorldBounds` "not `SKRect`" because world Y is up and Skia's Y is
   down; §5.3's `SKRect extent` predates that. `SKSize host` is unchanged — that one really is a
   screen-space size.

### Things that could not be implemented where the plan put them

3. **`PanZoomGesture` lives in `…Core.Input`, not the App.** §4 T12 lists it among `Scene2DHost`'s
   files, but §3.6 of the registry puts B2's `PanZoomTool` in `…Core.Input` and says it *wraps*
   `PanZoomGesture`. A Core type cannot wrap an App type. The gesture is pure math over `PaneSet` with
   no Avalonia in it, so Core is where it belongs; the host translates pointer events into
   coordinates.

4. **`SceneRenderGate` lives in `…Core.Compositing`, not the App.** §4 T12 lists the file App-side, but
   §5.8 requires `Debug.Assert(gate.IsHeld)` "at the top of every compositor cache mutation" — and the
   compositor is Core's. `SceneCompositor.Gate` is nullable and left null by single-threaded consumers
   (export, the CLI, tests), which have nothing to serialize.

5. **`SceneRenderContext` gains `Levels` (a `MapSpace?`) in B1, which correction 2 assigned to B3.**
   `LevelIndexFor` — which correction 2 *does* assign to B1 — cannot be implemented without the level
   table: it has to reproduce `FloorSplitter.SliceIndexFor`'s nearest-band fallback, and a single Z
   band cannot answer "which band is nearest". Adding it under B3's eventual name means B3 adds only
   `LevelCrossings` rather than renaming a member.

6. **`ISceneFrameSource` is declared by B1, in `…Core.Export`.** Registry §3.8 assigns it to B4
   verbatim from design §5.7, but B1's benchmark harness consumes it and the harness is the CI budget
   gate. Declared once, unchanged from §5.7's three members, so B4 adds `IFrameSink`, `ExportRequest`
   and `SceneExportSession` alongside it.

7. **`HeadlessSceneRenderer` is written by B1, in `…Pipeline.Headless`.** Correction 8 says
   `ScenePipelineBenchmark` "renders through `HeadlessSceneRenderer` (C1's Pipeline facade)" — but C1
   has not landed, and the benchmark cannot wait for it. It is written to that name and namespace so
   C1 extends it rather than adding a second headless entry point. It is a facade over
   `SceneCompositor`, never a competing renderer, and the goldens go through it too.

### Additive API, agreed shapes unchanged

8. **`MapSpace.Rebuild` has a fourth optional parameter, `radarNamesByLevel`.** The frozen
   three-argument signature still compiles at every documented call site. It exists because correction
   3's `MapLevel` shape requires `RadarImageName`, and `Rebuild`'s three parameters carry no way to
   supply it.

9. **`ILevelRadarBinder` (Core) is the seam `MapRadarBinder` (Pipeline) implements.** §4 T5 specifies
   the binder's rules but not how Core reaches them; the binder reads the baked bundle, which Core may
   not. Same Core-declares/Pipeline-solves split as `IVisionSolver`, and it is what lets the binding be
   evaluated exactly once per level-set rebuild.

10. **`ScenePalette.Light` added.** The pre-v2 golden corpus was captured under the app's Light theme
    variant and B0 shipped only `ScenePalette.Dark`, so the first parity run reported 100 % of pixels
    differing on a picture that was otherwise pixel-for-pixel correct. See
    `B1-text-metrics-review.md` §3.1.

11. **`GoldenImageComparer.Analyze` + `GoldenDeltaProfile` added.** `Compare`'s verdict is unchanged.
    The distribution exists because `MaxChannelDelta` is the single worst pixel in the frame — and
    across two rasterisers one anti-aliased edge pixel always produces a full-amplitude difference, so
    the maximum says nothing. The parity gate needs the shape of the curve. C2's SSIM lane will want it
    too.

12. **`ConePolygon.ApexZ` carries the player's FEET Z, not the eye Z.** The eye height is an input to
    the raycast and never leaves the solver; `ApexZ` is what the level filter compares, and the pre-v2
    filter compared `m.WorldZ`. Documented on the member.

### Test-plan deviations

13. **`GoldenParityTests` gates on a delta distribution, not on `GoldenTolerance` tiers.** §6 test 14
    asks for Tier A byte-exact with text disabled. That is not reachable: the golden was captured from
    a different rasteriser, so even with text off the anti-aliased edges differ, and there is no
    text-disabled golden to compare against. The gate asserts ≥99 % of pixels within ±8 and ≥99.5 %
    within ±32 (measured: 99.45 % / 99.72 %), the text-off comparison is asserted to be no worse than
    the text-on one, and every number is written up in `B1-text-metrics-review.md`. The byte-exact half
    of the exit criterion is `SceneDeterminismTests`, which pins the v2 renderer against itself.

14. **The zero-allocation assertion measures the SECOND of two identical 512-frame windows.** The first
    reliably shows one 48-byte allocation at a varying iteration past ~150. It appears whatever the
    layers draw, vanishes when nothing draws, occurs with no gen-0 collection in the window, and never
    recurs — the runtime tiering the loop body, not the scene allocating. Charging it to the budget
    would either make the gate flaky or force the budget above zero, and zero is the assertion worth
    having.

15. **`BannedApiTests` was rewritten to attribute offenders to the calling type.** B0's assembly-wide
    member-reference scan cannot express "the benchmark harness may read a stopwatch" — which plan T16
    requires, since that is the harness's entire purpose and the reason it sits in Pipeline rather than
    Core. The scan now finds banned member references precisely (pass 1) and attributes them to methods
    by matching those exact tokens in IL (pass 2), with a namespace exemption for
    `…Pipeline.Benchmarking`. A third case asserts the exemption is load-bearing, so it cannot silently
    grow to cover something else.

16. **The corpus entries `mirage-single-level`, `duel-mirage-b` and `fitmap-mirage-eco` are not
    captured.** All three need a de_mirage demo and the only demo in the tree is
    `assets/tour/sample-de_nuke.dem`. The nuke demo cannot stand in: the names encode the map, and a
    nuke capture filed under a mirage name is a corpus that lies. All three skip cleanly.
    `full-scene-budget` is authored in code instead of captured, deliberately — a budget fixture must
    make every layer do its worst, and a captured frame that happens to be quiet would let a regression
    through.

### B0 review carry-forwards

17. **(a) `ReadSectionHeightsOnce` retried forever — fixed.** The read only latched once a value
    resolved, so on a map that publishes no section heights (every single-floor map, i.e. most of them)
    it re-scanned eight interpolated field paths on every push for the whole demo. Now bounded at 256
    attempts, with the paths built once into a static array.
    `SceneFrameBuilderTests.MapWithoutSectionHeights_StopsRetrying` pins it.

18. **(c) The fixture's `roundSeconds 434.45` / `"7:14"` was NOT a trimmed-demo clock quirk.** The
    capture harness builds a bare `ModuleContext` and never calls `SetGameClock`, which is the shell's
    job on load — so `CurtimeSeconds` was the naive `tick/tickRate` and the round clock was off by
    exactly `clockBase`. Calibrating it in the harness (`clockBase = -319.641` for this demo) gives
    **114.81 s / "1:55"**, which is `mp_roundtime` twelve frames past `round_freeze_end` — precisely
    what the capture aims at. The golden PNG is byte-identical either way: the round clock is XAML
    chrome, not canvas. Fixed in `Playback2DGoldenCaptureTests`; fixture regenerated.

19. **(b) `duel-mirage-b` and `fitmap-mirage-eco` remain uncaptured** — see 16.

### One bug worth naming

20. **`ICustomDrawOperation.HitTest` must return true inside `Bounds`.** The obvious implementation is
    `false` — the host is a plain `Control` with its own pointer handlers, so why would the draw
    operation claim hits? Because a control whose only content is a custom draw operation has no other
    hit-testable geometry: with `false`, the entire surface is transparent to the pointer, the scene
    renders perfectly, and pan and zoom silently do nothing. Caught by `Scene2DHostTests`' drag case.

### Not built, and why

21. **`SpriteAtlas` / `SceneRenderOptions.UseSprites` (decision D-12) is not built.** D-12 already says
    sprites ship off by default, because a sprite blit does not anti-alias identically to `DrawCircle`
    and the exit criterion is parity. Building an unused, off-by-default draw path — and a second
    marker renderer for B2 and B4 to keep in step — buys nothing until a measurement asks for it, and
    the measurement says otherwise: render p99 is 3.9 ms against an 8 ms budget. Flip the decision when
    a profile names marker drawing as the cost.

22. **`DeferredVisionSolver` is not built**, exactly as plan T10 instructs. `IVisionSolver` is the seam;
    the budget has ~11 ms of headroom per frame at 1080p, so the escape hatch stays a seam.

23. **The `Scene2DHostRenderTests` real-demo capture (§6 test 15) is not written.** Its assertion —
    "Nuke 2 levels > 400 000 non-background pixels" — is the same claim
    `Scene2DHostTests.SceneHost_RendersANonBlankFrame_WithTeamColouredMarkers` makes against a
    synthetic roster (699 972 non-background pixels, team colours present), and the real-demo half is
    covered end to end by `GoldenParityTests`, which re-renders an actual captured Nuke frame and
    compares it to the pre-v2 picture. A third harness replaying the demo through a window would add
    runtime, not coverage.
