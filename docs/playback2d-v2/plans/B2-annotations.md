# Phase B2: Annotations (implementation plan)

**Branch:** `feature/playback2d-v2` · **Design:** `docs/playback2d-v2/design.md` (authoritative; §5.4, §5.5, §7.1)
· **Depends on:** B0 (Core/Pipeline projects, `Scene2DFrame`, `CpuSurfaceProvider`, fixtures), B1
(`ISceneLayer`, `SceneCompositor`, `Scene2DHost`, `MapSpace`/`LevelPane`) · **Runs parallel with:** C1/C2

This plan is self-contained: an implementer who has not read the design doc can execute it end to end.
Everything it asserts about existing code is cited with file + line as of `305c5ac`.

> ## Integrator corrections (BINDING; supersedes anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry. Every "Conflicts
> for the integrator" item (C1–C6) below is resolved here.
>
> 1. **C1 resolved: `PlayerMarker` carries `ulong SteamId`** (B0 adds it, default 0 when
>    unresolved). Use the field; the `Func<int, ulong>` resolver fallback is withdrawn.
> 2. **C2 resolved: `LevelPane.Camera` stays a public field** with a justified CA1051 suppression.
>    B1 has been told; `PanZoomTool` mutates it in place as planned.
> 3. **C3/D8/S7 resolved: B1 moves A1's timeline types to Core, not B2.** By the time B2 starts,
>    `ITimelineTrack`/`TimelineMarker`/`TimelineBand`/`ITimelineData`/`TimelineEventRecord`/
>    `TimelineEventKeys`/`TimelineMarkerKind` already live in `…Core.Timeline`. Delete T12's
>    "move it first" branch. **`AnnotationTrack` must implement A1's full interface**: `Id`,
>    `DisplayName`, `bool IsAvailable(ITimelineData)`, `BuildMarkers`, **`BuildBands`** (return
>    empty), `MarkersChanged`. Not the three-member sketch in this plan's contracts section.
> 4. **`TimelineMarker` is placed on the FRAME-INDEX axis** (A1 D5, design §5.6: "frame index is the
>    movement contract"). `AnnotationTrack` therefore emits
>    `new TimelineMarker(TrackId, data.FrameIndexAtTick(fromTick), fromTick, TimelineMarkerKind
>    .Annotation, "✎", tooltip, 0u)` and **drops elements whose tick resolves to `-1`**. Its `Id` is
>    **`"annotation"`** (A1's track ids are bare words: `round`, `kill`, `bomb`); the string
>    `"playback2d.annotations"` stays reserved for the layer id and the feature id.
> 5. **C4 resolved: B0 pins `SkiaSharp 2.88.9`** in `Directory.Packages.props` (the version
>    `Avalonia.Skia 11.3.12` resolves). B2 adds no package.
> 6. **C5 resolved: projects live at `src/Playback2D/DemoViewer.NET.Playback2D.{Core,Pipeline}`**,
>    slnx folder `/src/Playback2D/`. `{CORE}`/`{PIPE}` in this plan expand to those. **There is one
>    test project, `src/Playback2D/DemoViewer.NET.Playback2D.Tests`** (B0 creates it, root namespace
>    `DemoViewer.NET.Playback2DTests`). `…Core.Tests` and `…Pipeline.Tests` do not exist; both test
>    tables below land in that single project, and the two CI steps collapse into B0's existing
>    `playback2d-tests` job (B2 adds no CI step).
> 7. **C6 resolved: B1 ships `PanZoomGesture`, a self-contained pan/zoom class with no tool
>    abstraction.** B2 wraps it as `PanZoomTool` and deletes the host's direct handler bodies, as D1
>    describes. Nothing to reconcile.
> 8. **`Playback2DSettings` property names are B5's** (that plan owns the class and its
>    `WriteInMemory` flattening test). Rename B2's: `InkColor` → `AnnotationColorArgb` (**`uint`**,
>    not a `#AARRGGBB` string), `InkWidth` → `AnnotationWidth`, `InkOpacity` → `AnnotationOpacity`,
>    `EnvelopeMode` → `AnnotationDefaultVisibility` (values `Always | Fade | Custom`),
>    `FadeInTicks`/`FadeOutTicks` → `AnnotationFadeInTicks`/`AnnotationFadeOutTicks`,
>    `DynamicHoldTicks` → `AnnotationHoldTicks`, `AnchorToEntities` →
>    `AnnotationAnchorToEntities`, `AutoSaveAnnotations` → `AnnotationAutoSave`, `RecentInkColors`
>    → `AnnotationRecentColors`. `LastTool` keeps its name. B2 **adds** these to the existing class;
>    it does not declare the class unless it is the first lander.
> 9. **`AnnotationDocument` gains `public void ApplyMigration(DocDelta delta)`**: applies without an
>    undo entry, bumps `Version`, raises `Changed`. B3's level-anchor rebase needs it; implement it
>    next to `Apply` (it is the same mechanism `RemapWorldLevels` already uses).
> 10. **New `SpaceRef.World` anchors stamp `MapSpace.QuantizeZ(pane.Level.ZMin)`**, not the raw
>     slice `MinZ`. Otherwise an anchor written before a rebuild can miss its own level (B3 T8).
> 11. **`ContractVersion` is bumped once per release, by B5, to `1.2.0`**; A1 already bumped it.
>     Delete T18's bump instruction; a second bump to the same value is a no-op that reads as drift.
> 12. **Feature-catalog placement:** the five v2 ids form one contiguous block inserted after
>     `analysis.breakpoints` and before the `// ---- CHROME` comment, in the order
>     annotations · timeline · levels.auto · follow · export. B2 inserts *its* row into that block
>     (A1 creates the block). Gate reads go through **`IModuleContext.Features`** (A1 lands the seam;
>     `null` fails open), never a directly injected `IFeatureGate`.

---

## Scope & exit criterion

Quoted verbatim from design §9's phase table:

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **B (core)** | B2 | Annotations: document + deltas + undo, Draw/Erase tools, wet/dry layer, color picker, envelopes + timeline markers, sidecar + app-data persistence | Draw/erase/undo survive seek, zoom, level switch, tab deactivate | 2.5 wk |

Everything else the assignment names is in scope as a sub-part of that row: `AnnotationElement` /
`SpaceRef` / `TimeEnvelope` exactly per §5.4, `IPointerTool` + `InputToolRouter` per §5.5, the
perfect-freehand C# port with MIT attribution, `Playback2DSettings` (binder-safe + `WriteInMemory`),
`AnnotationStore` in Pipeline with demo + clock identity, and stroke-level erase by outline hit-test.

**Out of scope (belongs to a named other phase):** timeline *drag handles* on annotation markers (B3;
design §9 B3 row: "`AnnotationTrack` time-edit handles"); the timeline control itself (A1); export of
annotated frames (B4); `SingleLayout` / level strip (B3); pixel-level erase (design §5.4: "explicitly
deferred"); shape tools beyond Freehand (`Kind` exists from day one so they are additive later).

---

## Decisions made

Where the design left a choice open, this plan makes the call. These are binding for B2 and are
flagged to the integrator where another phase is affected.

**D1: B2 owns the tool router; B1 ships plain pan handlers that B2 replaces.**
Design §4's component map lists `InputToolRouter` inside the App's `Scene2DHost` box, but §5.5 gives no
assembly for `IPointerTool`/`IToolServices`. B1 only needs pan/zoom, so requiring B1 to build the router
would front-load B2's design into B1. Therefore: **B1 ships `Scene2DHost` with direct pan/zoom pointer
handlers** (a mechanical move of `Playback2DViewport.cs:405-459`), and **B2 deletes those handler bodies
and delegates to an `InputToolRouter` it owns**, with today's code re-homed in `PanZoomTool`. The
*types* (`IPointerTool`, `IToolServices`, `ToolPointerEvent`, `InputToolRouter`, `PanZoomTool`,
`DrawTool`, `EraseTool`) live in **Core** (`Input/`) so they are unit-testable with no Avalonia platform
(design §11 "direct-execution tests"); `Scene2DHost` merely *owns an instance* of the router, which is
what §4's box actually depicts. Integrator: tell B1 to keep its pointer handlers thin and free of
annotation concepts, and not to invent a competing tool abstraction.

**D2: Wheel is router-level, not an `IPointerTool` member.** §5.5's `IPointerTool` has no wheel method
and this plan does not add one (the sketch is binding). `InputToolRouter.OnWheel(...)` always applies
zoom-to-cursor to the pane under the cursor regardless of active tool (universal drawing-app behavior),
and it keeps `Playback2DViewport.cs:439-459` semantics byte-for-byte.

**D3: hold-Space is sampled at press time only.** `InputToolRouter.IsSpaceHeld` diverts the *next*
press to `PanZoomTool`; a gesture already in flight is never hijacked mid-stroke (a half-committed
stroke is worse than a missed pan). Releasing Space mid-pan does not end the pan gesture either.

**D4: `BeginGesture` returns plain `IDisposable`; bail is a document method.** §5.4 sketches
`IDisposable BeginGesture(string name)` and §7.1 names "`BailToMark` on Esc", so `AnnotationDocument`
gets `public bool BailToMark()` and the returned handle stays `IDisposable` exactly as sketched.
Gestures are **non-reentrant**: a second `BeginGesture` while one is open throws
`InvalidOperationException` (the router guarantees one active tool, so this is a bug detector).
Disposing a gesture that produced zero deltas pushes **no** undo entry.

**D5: `TimeEnvelope` fades sit *outside* the visible window (lead-in / lead-out).** Full opacity over
`[FromTick, UntilTick]`; ramp 0→1 over `[FromTick − FadeInTicks, FromTick)`; ramp 1→0 over
`(UntilTick, UntilTick + FadeOutTicks]`; 0 elsewhere. `null` bounds are ±∞. `default` (all zero/null)
is therefore constant 1.0, which is exactly what §5.4's `TimeEnvelope.Static = default` requires.
Rationale: "pin to now" must show the stroke at full opacity *immediately*, which an inside-the-window
fade would not do.

**D6: level remap is history-transparent.** Design risk 5 requires annotations to be remapped when
`FloorSplitter` re-derives the level set. A remap is a system event, not a user gesture, so it must not
consume an undo slot nor be undoable into a stale state. `AnnotationDocument.RemapWorldLevels(map)`
rewrites live elements **and every element captured in the undo/redo history**, bumps `Version`, raises
`Changed`, and touches neither stack's depth.

**D7: dry ink is split two ways inside `AnnotationLayer`.** Only elements that are *both*
`TimeEnvelope.Static` *and* `SpaceRef.World` go into the per-level `SKPicture` (re-recorded on
`Version` change or level rebuild). Time-anchored and entity-anchored elements are drawn per frame from
a prepared list built in `Advance`; their count is small and their geometry/opacity is time-varying.
The layer declares `Cache => LayerCacheHint.Dynamic` so the compositor never tries to record the whole
layer into an outer picture.

**D8: `AnnotationTrack` ships in B2; drag handles ship in B3.** This resolves design open question 3.
B2 delivers markers (tick, glyph, tooltip, seek target) plus envelope editing via a properties popover
("pin to now", Always/Fade/Custom, numeric From/Until). B3 adds drag-to-edit on the timeline using the
`DocDelta.Replace` API B2 exports. `AnnotationTrack` lives in **Core** against Core's `ITimelineTrack`;
if A1 defined `ITimelineTrack`/`TimelineMarker` App-locally, B2's first task moves those two types to
Core and repoints A1's `RoundTrack`/`KillTrack`/`BombTrack` (mechanical, 3 files). Design §4 puts
them in Core.

**D9: nested `SpaceRef` cases keep the design's shape and carry a targeted CA1034 suppression.**
`Directory.Build.props` sets `EnableNETAnalyzers=true`, `AnalysisMode=Recommended`,
`TreatWarningsAsErrors=true`, so §5.4's visible nested records (`SpaceRef.World`, `SpaceRef.Entity`)
would fail the build under CA1034. Keep the design's shape; add
`[SuppressMessage("Design", "CA1034", Justification = "Closed discriminated union; the nesting is the contract (design §5.4).")]`
on each nested record. Same treatment for `DocDelta`'s nested cases.

**D10: persistence identity.** Demo identity = lowercase-hex SHA-256 of the `.dem` bytes (the existing
convention: `GraphBreakpointStore.ComputeDemoKey`, `GraphBreakpointStore.cs:86-88`), plus file name and
byte size for diagnostics. Clock identity = `("dv-frame-clock", TickRate, FrameCount, FirstTick,
LastTick)`. A clock mismatch on load **does not discard anything**: the document loads, `Static`
elements are unaffected, time-anchored elements are flagged and the UI shows a one-line banner. A demo
*hash* mismatch on a sidecar means the file belongs to a different demo → the sidecar is ignored (and
never overwritten) and a fresh document is used.

**D11: hashing is streamed and cached.** SHA-256 over a multi-GB `.dem` is not free. `AnnotationStore`
takes an injected `Func<string, string>` demo-key resolver so the App can pass its existing cached hash
(`DemoLibraryService.cs:915-924` already caches by `(size, mtime)`), with a streaming default for the
CLI. Nothing in the annotation path ever hashes on the UI thread.

**D12: persistence writes are best-effort and never throw into the UI**, matching
`GraphBreakpointStore.Save` (`GraphBreakpointStore.cs:104-119`) and `SettingsService.SaveSession`
(`SettingsService.cs:214-224`). A failed write surfaces as a status string, never an exception.

---

## Ordered work breakdown

Tasks are ≤ ~half a day each. `⇒` marks a hard ordering constraint. Paths are repo-relative.
`{CORE}` = `src/Playback2D/DemoViewer.NET.Playback2D.Core`, `{PIPE}` =
`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline`. **Mirror whatever paths B0 actually created**
(fixed by integrator correction 6; the slnx folder is `/src/Playback2D/`).

### Group 1: Core document model (no dependency on B1)

**T01 · Element/space/time value types.**
Create `{CORE}/Annotations/AnnotationElement.cs` with `AnnotationKind`, `AnnotationStyle`, `InkPoint`,
`SpaceRef` (+ `World`, `Entity`), `TimeEnvelope`, `EnvelopeMode`, `AnnotationElement`; signatures in
"Public API contracts" below, matching design §5.4 exactly. `TimeEnvelope.OpacityAt` implements D5.
Pure value types, no Skia types in the public shape except `SKColor`-free ARGB `uint` (Core references
SkiaSharp, but keeping the DTO surface primitive makes the Pipeline serializer trivial).
*Done when:* `TimeEnvelopeTests` (T14) passes.

**T02 · `DocDelta` + `AnnotationDocument`.** ⇒ T01
Create `{CORE}/Annotations/DocDelta.cs` and `{CORE}/Annotations/AnnotationDocument.cs`.
Document holds `List<AnnotationElement>` + a `Dictionary<Guid,int>` index, `Version`, `Changed`,
`Apply`, `Undo`, `Redo`, `BeginGesture`, `BailToMark`, `RemapWorldLevels`. History is a
`List<(DocDelta Applied, DocDelta Inverse)>` per gesture entry plus an `_undo`/`_redo` stack of
gesture-sized batches (squash-to-mark). Inverses are computed **at apply time** (a `Remove` inverse
needs the element that was removed), which is why `DocDelta` itself stays minimal and serializable.
Bounded history: cap at 200 gesture entries, oldest dropped (a stroke is ~1 KB of points; 200 is
generous and bounds memory; the repo's "no unbounded buffer" invariant, see
`AppSettings.DiagnosticsSettings` docs at `AppSettings.cs:112-123`).
*Done when:* `AnnotationDocumentTests` (T14) passes.

**T03 · Perfect-freehand C# port.** ⇒ T01 (independent of T02)
Create `{CORE}/Ink/FreehandOutline.cs` + `{CORE}/Ink/FreehandOptions.cs`. Port of
[perfect-freehand](https://github.com/steveruizok/perfect-freehand) v1.2.2 (MIT, © Steve Ruiz), files
`packages/perfect-freehand/src/getStrokePoints.ts` and `getStrokeOutlinePoints.ts`. Two stages, kept
separate so both are independently testable:

1. `GetStrokePoints`: dedupe identical consecutive samples; streamline
   `p = prev + (cur − prev) · (1 − streamline)`; per point emit `(Point, Pressure, Vector, Distance,
   RunningLength)` where `Vector = unit(prev − cur)`; append a duplicate terminal point.
2. `GetStrokeOutline`: `radius = size/2`, scaled by `thinning` and the pressure easing;
   `simulatePressure` derives pressure from velocity
   (`sp = min(1, distance/size)`, `rp = min(1, 1 − sp)`,
   `pressure = min(1, prev + (rp − prev) · (sp · 0.275))`); walk points emitting left/right offsets
   perpendicular to the running direction, inserting arc points at sharp direction changes
   (`dpr < 0`); taper start/end by `start.taper`/`end.taper` with their easings; cap ends with a
   semicircle unless tapered; result = `left ++ reverse(right)` as a closed polygon.

Port defaults match upstream: `Size = 16`, `Thinning = 0.5`, `Smoothing = 0.5`, `Streamline = 0.5`,
`SimulatePressure = true`, caps on, tapers 0, identity easings. **Doubles throughout** (upstream uses
JS numbers = doubles) so reference vectors compare tightly. Allocation discipline: the hot overload
writes into a caller-supplied `List<SKPoint>`/`SKPath`: no `IEnumerable`, no LINQ (design §6 zero-alloc
budget).
*Done when:* `FreehandOutlineTests` (T14) matches the checked-in reference vectors.

**T04 · MIT attribution.** ⇒ T03
Edit `THIRD-PARTY-NOTICES.md`: add section `## d. perfect-freehand (MIT)` after the existing `## c.`,
listing `{CORE}/Ink/FreehandOutline.cs` and `{CORE}/Ink/FreehandOptions.cs` as adapted files and
reproducing the upstream MIT text in full, in the exact style of the existing `## a. demofile-net (MIT)`
block (`THIRD-PARTY-NOTICES.md:7-47`). Also put a file-header comment in both `.cs` files pointing at
the notice section.

**T05 · Stroke hit-testing for erase.** ⇒ T03
Create `{CORE}/Annotations/AnnotationHitTester.cs`: `HitTest(element, worldPoint, worldRadius)` →
for `Freehand`, cheap AABB reject, then point-to-polyline distance against the raw points expanded by
(half stroke width + radius), then, only if that passes and the stroke is wide, exact
point-in-outline via the derived polygon (even-odd). For future kinds, a shape-specific branch (stub
throwing `NotSupportedException` is acceptable; Freehand is the only kind B2 ships).
`HitTestAll(document, world, radius, results)` fills a caller list, newest-first (topmost wins).

**T06 · `AnnotationSession`.** ⇒ T02
Create `{CORE}/Annotations/AnnotationSession.cs`, the shared mutable seam between tools, layer and UI:
the `AnnotationDocument`, the in-flight `WetStroke` buffer, the current `AnnotationStyle`, the envelope
template for new elements, and the active `ToolKind`. This is the "annotation session" §5.5 names in
`IToolServices`.

### Group 2: Input tools (needs B1's `LevelPane`)

**T07 · Tool contracts.** ⇒ T06, ⇒ B1's `LevelPane`
Create `{CORE}/Input/IPointerTool.cs` (`ToolKind`, `ToolPointerEvent`, `ToolPointerButton`,
`ToolModifiers`, `IPointerTool`) and `{CORE}/Input/IToolServices.cs`. `IPointerTool` matches §5.5
exactly: four methods, no wheel (D2).

**T08 · `InputToolRouter` + `PanZoomTool`.** ⇒ T07
Create `{CORE}/Input/InputToolRouter.cs` and `{CORE}/Input/PanZoomTool.cs`.
`PanZoomTool` is a straight move of the current viewport code:
- press → capture the pane under the cursor (`Playback2DViewport.cs:405-413` `_dragSlice`/`_lastPointer`),
- move → `pane.Camera.Current = pane.Camera.Current.WithPanDelta(dx, dy)` and
  `pane.Camera.ManualOverride = true` (`Playback2DViewport.cs:415-429`, `ViewportTransform.cs:121`,
  `SliceCamera.cs:21,27`),
- release → drop the captured pane (`:431-437`),
- router `OnWheel` → `ZoomAbout(x, paneLocalY, e.Delta > 0 ? 1.1 : 1/1.1)` + `ManualOverride = true`
  (`:439-459`, `ViewportTransform.cs:129`).
Router: `Active`, `SetActive(ToolKind)`, `IsSpaceHeld` (D3), `OnPressed/OnMoved/OnReleased/OnWheel`,
`CancelActive()` (Esc → `OnCancelled` → `BailToMark`). Exactly one tool is active; `PanZoomTool` is the
permanent fallback and is never disposed.

**T09 · `DrawTool`.** ⇒ T08, T03
Create `{CORE}/Input/DrawTool.cs`.
- `OnPressed`: `s.Session.Document.BeginGesture("draw")` (store the handle), reset `WetStroke` with the
  first world point + pressure, choose `SpaceRef` (`Entity(steamId, dx, dy)` when
  `s.TryResolveEntityAnchor` hits within the anchor radius **and** entity-anchor mode is on, else
  `World(pane.Level.ZMin)`), and snapshot `s.Session.NewElementEnvelope` resolved against
  `s.CurrentTick`. Returns `true` (handled).
- `OnMoved`: append every point in `e.IntermediatePoints` (plus the primary point) after a
  min-distance filter of `0.35 · strokeWidth` in world units; `s.RequestRender()`.
- `OnReleased`: decimate (same filter), build the `AnnotationElement`, `document.Apply(DocDelta.Add)`,
  dispose the gesture ⇒ exactly one undo entry; clear `WetStroke`. A stroke with < 2 points after
  filtering becomes a dot (two coincident points) rather than being dropped.
- `OnCancelled`: clear `WetStroke`, `document.BailToMark()`, no undo entry.

**T10 · `EraseTool`.** ⇒ T08, T05
Create `{CORE}/Input/EraseTool.cs`. Press opens a gesture named `"erase"`; press and each move
hit-test at the cursor with radius `EraserWorldRadius` and `Apply(DocDelta.Remove(id))` for each new
hit (dedupe by id within the gesture); release disposes ⇒ one undo entry for the whole drag, and zero
entries when nothing was hit (D4). `OnCancelled` bails.

### Group 3: Rendering

**T11 · `AnnotationLayer` (wet/dry).** ⇒ T06, T03, ⇒ B1's `ISceneLayer`/`SceneRenderContext`
Create `{CORE}/Layers/AnnotationLayer.cs`.
- `Id = "playback2d.annotations"`, `Slot = LayerSlot.Overlay`, `Order` above actors and below HUD,
  `Cache = LayerCacheHint.Dynamic` (D7).
- `Advance(in SceneTime time, Scene2DFrame frame)`: if `Version` changed (or the level set was
  rebuilt) re-record the per-level dry `SKPicture`s from the Static∧World subset; rebuild the
  `_prepared` list (element → resolved world offset, opacity, level) for the time/entity-anchored
  subset, resolving `SpaceRef.Entity.SteamId` → the frame's marker each frame and **skipping** while
  unresolvable or dead (§5.4). All allocation is into reused lists. Returns `session.Wet.IsActive`
  (RAF armed only while a stroke is in flight; a fade needs no RAF because tick changes already
  repaint).
- `Render(SKCanvas canvas, SceneRenderContext ctx)`: pure. Draws this pane's dry picture, then the
  prepared per-frame elements, then the wet stroke (only in the pane the stroke began on). Reuses one
  `SKPaint` and one `SKPath` per layer instance. No per-frame allocation.
- Partial-stroke reveal: for a `Freehand` element whose envelope is mid-fade-in, draw only the leading
  `t` fraction of points (design §5.4: "the partial-stroke rendering it supports gives 'draw-on reveal'
  animation for dynamic strokes nearly free"). Gate behind `AnnotationStyle.RevealOnFadeIn`.

**T12 · `AnnotationTrack`.** ⇒ T02, D8
Create `{CORE}/Timeline/AnnotationTrack.cs` implementing `ITimelineTrack`: one `TimelineMarker` per
element with a non-null `FromTick`, glyph `✎`, tooltip = kind + envelope summary, seek target =
`FromTick`. Subscribes to `AnnotationDocument.Changed` → raises `MarkersChanged`. If `ITimelineTrack`
still lives in the App from A1, move it and `TimelineMarker` to Core first and repoint A1's three
tracks (D8).

### Group 4: Pipeline persistence

**T13 · `AnnotationStore` + DTO.** ⇒ T01, T02
Create `{PIPE}/Annotations/AnnotationDocumentDto.cs`, `{PIPE}/Annotations/AnnotationIdentity.cs`,
`{PIPE}/Annotations/AnnotationStore.cs`.
- Schema v1, tolerant reader, unknown fields preserved via `[JsonExtensionData]` on **both** the root
  DTO and the element DTO (a v2 field written by a newer build survives a v1 round-trip).
- Location: `<demo>.dvann.json` beside the demo when its directory is writable (probe: create + delete
  a `.dvann.probe` temp file; cache the result per directory), else
  `<appDataRoot>/annotations/<sha256>.dvann.json`. `appDataRoot` is a constructor parameter; Pipeline
  must not reference the App (`AppPaths` stays App-side; the App passes `AppPaths.ConfigRoot`,
  `AppPaths.cs:81-93`). `null` root + non-writable demo dir ⇒ store is inert (WASM) and reports so.
- Records demo identity + clock identity (D10); load returns `AnnotationLoadResult` carrying
  `ClockMismatch` / `DemoMismatch` so the UI can warn instead of silently corrupting anchors.
- Save is atomic (temp + `File.Move` overwrite, mirroring `SettingsService`'s write) and best-effort
  (D12). `SaveAsync`/`LoadAsync` are `Task`-returning and never touch a dispatcher.

### Group 5: App wiring

**T14 · Tests.** See the Test plan section; interleave with the groups above rather than deferring.

**T15 · `Playback2DSettings` + `WriteInMemory`.**
- `src/App/DemoViewer.NET/Configuration/AppSettings.cs`: add
  `public Playback2DSettings Playback2D { get; set; } = new();` to `AppSettings` (after `Idle`,
  `AppSettings.cs:64`) and the new binder-safe `Playback2DSettings` class (every property defaulted,
  parameterless ctor; the binder starts from `new AppSettings()`, `AppSettings.cs:3-18`).
- `src/App/DemoViewer.NET/Configuration/SettingsService.cs`: add every key to `WriteInMemory`
  (`SettingsService.cs:419-448`), including the `RecentInkColors` array indices, using
  `CultureInfo.InvariantCulture` for numbers exactly as the existing entries do (`:431`). **This is
  mandatory, not optional**: the method's own comment (`:411-415`) states that an unflattened
  WASM-reachable section is silently discarded on reload, and annotations *are* WASM-reachable
  (in-session drawing works in the browser, design §8).
- Extend `SettingsServiceTests.WriteInMemory_ShrinkAndRemove_DropStaleKeys`
  (`SettingsServiceTests.cs:259`) to cover the new keys.

**T16 · Feature gate.**
`src/App/DemoViewer.NET/Features/FeatureCatalog.cs`: add to the sub-feature block (`:88`)
`new("playback2d.annotations", FeatureScope.SubFeature, "Annotations", "Draw and erase over the 2D
playback surface; static or clock-anchored.", "tab.playback2d", null, false, Defaults(true, true,
true))`. Id is a persisted override key, chosen once, never renamed (design §7.7). Do **not** reorder
existing entries: catalog order determines group leaders (`FeatureCatalog.cs:31-33`).

**T17 · Session controller + `IToolServices` adapter.** ⇒ T06, T07, T13, ⇒ B1's `Scene2DHost`
- `src/App/DemoViewer.NET/Modules/Playback2D/Annotations/AnnotationSessionController.cs`: owns the
  `AnnotationSession`, the `AnnotationStore`, load-on-demo-open, debounced autosave (750 ms on
  `Document.Changed`), flush on tab deactivate / `DemoReset` / shutdown, and the feature-gate check.
  Hooks the existing lifecycle: `Playback2DTabViewModel.OnActivated` (`:297`) /`OnDeactivated`
  (`:325`) / `context.DemoReset` (`:305`).
- `src/App/DemoViewer.NET/Modules/Playback2D/Annotations/SceneHostToolServices.cs`: implements
  `IToolServices` over `Scene2DHost`: `PaneAt` from the host's pane list (the successor to
  `SliceIndexAtScreenY`, `Playback2DViewport.cs:464-475`), `ScreenToWorld` via
  `ViewportTransform.ScreenToWorld` (`ViewportTransform.cs:72`), `CurrentTick` from
  `IModuleContext.CurrentTick`, `RequestRender` → `Scene2DHost.InvalidateVisual()` (+
  `ArmFrameLoopIfNeeded`, the successor to `Playback2DViewport.cs:822-841`),
  `TryResolveEntityAnchor` over the current frame's markers.
- `Scene2DHost` edit: replace B1's pan handler bodies with router delegation, and forward
  `OnKeyDown/OnKeyUp` for Space (D3) and Esc (`CancelActive`).

**T18 · Toolbar chrome + VM.** ⇒ T15, T17
- `src/App/DemoViewer.NET/ViewModels/Playback2D/AnnotationsPanelViewModel.cs` (new file, new folder):
  `[ObservableProperty]` tool selection, ink color, width, opacity, envelope mode + fade ticks,
  entity-anchor toggle, `UndoCommand`/`RedoCommand`/`ClearAllCommand`/`PinToNowCommand`, persistence
  status text. Exposed as `Playback2DTabViewModel.Annotations`, a nested VM, so the already
  1,542-line `Playback2DTabViewModel` does not grow further.
- `src/App/DemoViewer.NET/Views/Playback2D/AnnotationToolbar.axaml` (+ `.axaml.cs`): tool toggle
  buttons (Pan / Draw / Erase), a `ColorPicker` (the `Avalonia.Controls.ColorPicker` package is
  already referenced at `DemoViewer.NET.csproj:22`), a width slider with a live preview swatch, an
  envelope-mode selector with "Pin to now", and undo/redo buttons. Styled from the existing `Pb2d*`
  dynamic-resource tokens used throughout `Playback2DView.axaml`.
- `Playback2DView.axaml`: mount the toolbar in the left column's overlay grid (`:84-141`), as a
  top-left `Border` mirroring the bottom-left transport overlay's chrome. Bind visibility to the
  `playback2d.annotations` gate.
- `Playback2DModule.cs`: bump `ContractVersion` to `1.2.0` (design §7.7: bump for any additive context
  consumption).

**T19 · Session-state round-trip.** ⇒ T18
Implement `SnapshotState`/`RestoreState` on `Playback2DTabViewModel`
(`IWorkspaceTabViewModel.cs:26,39`) for the *tool* state only: active tool, style, envelope mode.
**Never** the document (that is `AnnotationStore`'s job) and **never** camera/playback/selection.
`RestoreState` receives a `JsonElement` and must tolerate an older shape by degrading to "restore
nothing" (`IWorkspaceTabViewModel.cs:30-36`).

**T20 · Docs + notices sweep.** ⇒ all
Update `docs/playback2d-v2/design.md` §12 open question 3 with D8's resolution (one line, marked
"resolved in B2"). Add a short `docs/playback2d-v2/annotations-format.md` documenting the v1 sidecar
schema for third parties.

**Ordering summary:** Group 1 (T01–T06) is unblocked today and should start immediately; it needs
nothing from B1. Group 2 needs B1's `LevelPane`; Group 3 needs B1's `ISceneLayer` +
`SceneRenderContext`. Group 4 is independent of B1 entirely. Group 5 needs B1's `Scene2DHost`. If B1
slips, Groups 1 and 4 plus their tests still complete and are separately mergeable.

---

## Public API contracts

Binding for other phases. `SKPoint`/`SKRect`/`SKCanvas` are SkiaSharp; `LevelPane`, `MapLevel`,
`Scene2DFrame`, `SceneTime`, `SceneRenderContext`, `ISceneLayer`, `LayerSlot`, `LayerCacheHint`,
`ITimelineTrack`, `TimelineMarker`, `ViewportTransform`, `SliceCamera` come from B0/B1.

### Namespace `DemoViewer.NET.Playback2D.Core.Annotations`

```csharp
public enum AnnotationKind { Freehand, Line, Arrow, Rect, Ellipse, Text }

/// <summary>Envelope authoring mode. Drives what the UI writes into <see cref="TimeEnvelope"/>.</summary>
public enum EnvelopeMode { Always, Fade, Custom }

/// <summary>One raw input sample in WORLD units. Pressure is 0..1; 0.5 when the device reports none.</summary>
public readonly record struct InkPoint(float X, float Y, float Pressure);

/// <summary>ARGB colour, stroke width in WORLD units, 0..1 opacity multiplier.</summary>
public readonly record struct AnnotationStyle(uint ColorArgb, float WidthWorld, float Opacity,
    bool RevealOnFadeIn = false)
{
    public static readonly AnnotationStyle Default;   // 0xFFFFC107, 6f, 1f, false
}

public abstract record SpaceRef
{
    /// <summary>Default anchor: a map level, keyed by its quantized ZMin (never a slice index).</summary>
    public sealed record World(double LevelMinZ) : SpaceRef;

    /// <summary>Tracked telestration: offset from a player, keyed by SteamId (slots recycle).</summary>
    public sealed record Entity(ulong SteamId, float Dx, float Dy) : SpaceRef;
}

public readonly record struct TimeEnvelope(int? FromTick, int? UntilTick, int FadeInTicks, int FadeOutTicks)
{
    public static readonly TimeEnvelope Static = default;   // null bounds = always visible
    public bool IsAnchored { get; }                          // FromTick or UntilTick is non-null
    public double OpacityAt(int tick);                       // pure; trapezoid per D5
    public TimeEnvelope PinnedTo(int tick, int holdTicks, int fadeIn, int fadeOut);
}

public sealed record AnnotationElement(
    Guid Id, AnnotationKind Kind,
    AnnotationStyle Style,
    SpaceRef Space, TimeEnvelope Time,
    IReadOnlyList<InkPoint> Points,
    string? Text);

public abstract record DocDelta
{
    public sealed record Add(AnnotationElement Element, int Index) : DocDelta;
    public sealed record Remove(Guid Id) : DocDelta;
    public sealed record Replace(Guid Id, AnnotationElement Element) : DocDelta;
    public sealed record Batch(IReadOnlyList<DocDelta> Items) : DocDelta;
}

public sealed class AnnotationDocument
{
    public IReadOnlyList<AnnotationElement> Elements { get; }
    public int Version { get; }                     // bumps on every mutation; ink layer re-records on change
    public int UndoDepth { get; }
    public int RedoDepth { get; }
    public bool IsGestureOpen { get; }

    public IDisposable BeginGesture(string name);   // mark; dispose = ONE undo entry (none if zero deltas)
    public bool BailToMark();                       // Esc: roll back the open gesture, add no undo entry
    public void Apply(DocDelta delta);              // invertible add/remove/replace/batch
    public bool Undo();
    public bool Redo();
    public bool TryGet(Guid id, out AnnotationElement element);

    /// <summary>D6: rewrites World(ZMin) anchors in live elements AND history. No undo entry.</summary>
    public void RemapWorldLevels(IReadOnlyDictionary<double, double> zMinMap);

    /// <summary>Correction 9 (B3 consumes it): applies a delta as a MIGRATION: bumps Version and
    /// raises Changed, but pushes nothing onto the undo/redo stacks. For level-set rebases and
    /// schema migrations, where the user did not act and Ctrl+Z must not restore a stale state.</summary>
    public void ApplyMigration(DocDelta delta);

    /// <summary>Bulk load (persistence / tests). Clears history; one Changed.</summary>
    public void Reset(IEnumerable<AnnotationElement> elements);

    public event Action? Changed;
}

/// <summary>The in-flight (wet) stroke: raw samples not yet committed to the document.</summary>
public sealed class WetStroke
{
    public bool IsActive { get; }
    public AnnotationStyle Style { get; }
    public SpaceRef Space { get; }
    public IReadOnlyList<InkPoint> Points { get; }
    public string? PaneLevelId { get; }
}

/// <summary>The shared seam between tools, the render layer and the UI (design §5.5 "annotation session").</summary>
public sealed class AnnotationSession
{
    public AnnotationSession(AnnotationDocument document);
    public AnnotationDocument Document { get; }
    public WetStroke Wet { get; }
    public AnnotationStyle Style { get; set; }
    public TimeEnvelope NewElementEnvelope { get; set; }
    public ToolKind ActiveTool { get; set; }
    public bool AnchorToEntities { get; set; }
    public float EraserWorldRadius { get; set; }         // default 48
    public event Action? WetChanged;
}

public static class AnnotationHitTester
{
    public static bool HitTest(AnnotationElement element, float worldX, float worldY, float worldRadius);
    public static int HitTestAll(AnnotationDocument doc, float worldX, float worldY, float worldRadius,
        List<Guid> results);                              // topmost-first; returns count
}
```

### Namespace `DemoViewer.NET.Playback2D.Core.Ink`

```csharp
public readonly record struct FreehandOptions(
    double Size, double Thinning, double Smoothing, double Streamline,
    bool SimulatePressure, bool CapStart, double TaperStart, bool CapEnd, double TaperEnd)
{
    public static readonly FreehandOptions Default;  // 16, .5, .5, .5, true, true, 0, true, 0
}

public readonly record struct StrokePoint(double X, double Y, double Pressure,
    double VectorX, double VectorY, double Distance, double RunningLength);

public static class FreehandOutline
{
    public static void GetStrokePoints(ReadOnlySpan<InkPoint> input, in FreehandOptions o,
        List<StrokePoint> output);
    public static void GetStrokeOutline(List<StrokePoint> points, in FreehandOptions o,
        List<SKPoint> outline);
    /// <summary>Convenience: raw samples → closed outline polygon. Allocation-free given warm lists.</summary>
    public static void GetOutline(ReadOnlySpan<InkPoint> input, in FreehandOptions o,
        List<StrokePoint> scratch, List<SKPoint> outline);
}
```

### Namespace `DemoViewer.NET.Playback2D.Core.Input`

```csharp
public enum ToolKind { PanZoom, Draw, Erase }

[Flags]
public enum ToolModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Space = 8 }

public enum ToolPointerButton { None, Left, Right, Middle }

/// <summary>One pointer sample, already resolved to a pane and to world coordinates by the host.</summary>
public readonly ref struct ToolPointerEvent
{
    public LevelPane? Pane { get; init; }
    public SKPoint Screen { get; init; }          // host-relative
    public SKPoint PaneLocal { get; init; }       // pane-rect-relative (zoom anchor)
    public SKPoint World { get; init; }
    public float Pressure { get; init; }          // 0..1; 0.5 when unsupported
    public ToolPointerButton Button { get; init; }
    public ToolModifiers Modifiers { get; init; }
    /// <summary>Coalesced samples since the previous event, world space, oldest-first. May be empty.</summary>
    public ReadOnlySpan<InkPoint> Intermediate { get; init; }
}

public readonly record struct ToolWheelEvent(LevelPane? Pane, SKPoint Screen, SKPoint PaneLocal,
    double Delta, ToolModifiers Modifiers);

public interface IPointerTool
{
    ToolKind Kind { get; }
    bool OnPressed(in ToolPointerEvent e, IToolServices s);
    void OnMoved(in ToolPointerEvent e, IToolServices s);
    void OnReleased(in ToolPointerEvent e, IToolServices s);
    void OnCancelled(IToolServices s);
}

public interface IToolServices
{
    AnnotationSession Session { get; }
    int CurrentTick { get; }                                       // DV frame clock, never CS2 ticks
    LevelPane? PaneAt(SKPoint screen);
    SKPoint ScreenToWorld(LevelPane pane, SKPoint screen);
    SKPoint WorldToScreen(LevelPane pane, SKPoint world);
    double WorldUnitsPerPixel(LevelPane pane);
    bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
        out ulong steamId, out float dx, out float dy);
    void RequestRender();
}

public sealed class InputToolRouter
{
    public InputToolRouter(IToolServices services, PanZoomTool panZoom);
    public IPointerTool Active { get; }
    public ToolKind ActiveKind { get; }
    public bool IsSpaceHeld { get; set; }        // D3: sampled at press time only
    public bool IsGestureOpen { get; }
    public void SetActive(ToolKind kind);
    public void Register(IPointerTool tool);
    public bool OnPressed(in ToolPointerEvent e);
    public void OnMoved(in ToolPointerEvent e);
    public void OnReleased(in ToolPointerEvent e);
    public void OnWheel(in ToolWheelEvent e);    // D2: always pan-zoom semantics
    public void CancelActive();                  // Esc
    public event Action<ToolKind>? ActiveToolChanged;
}

public sealed class PanZoomTool : IPointerTool { public PanZoomTool(); }
public sealed class DrawTool  : IPointerTool { public DrawTool(); }
public sealed class EraseTool : IPointerTool { public EraseTool(); }
```

### Namespace `DemoViewer.NET.Playback2D.Core.Layers`

```csharp
public sealed class AnnotationLayer : ISceneLayer
{
    public const string LayerId = "playback2d.annotations";
    public AnnotationLayer(AnnotationSession session);
    public string Id { get; }                                    // LayerId
    public LayerSlot Slot { get; }                               // Overlay
    public int Order { get; }                                    // 100
    public LayerCacheHint Cache { get; }                         // Dynamic (D7)
    public bool IsEnabled { get; set; }
    public bool Advance(in SceneTime time, Scene2DFrame frame);
    public void Render(SKCanvas canvas, SceneRenderContext ctx);
    public void InvalidateLevels();                              // MapSpace rebuild → drop dry pictures
    public void Dispose();
}
```

### Namespace `DemoViewer.NET.Playback2D.Core.Timeline`

```csharp
// Correction 3/4: A1's ITimelineTrack has six members and AnnotationTrack implements all of them.
public sealed class AnnotationTrack : ITimelineTrack, IDisposable
{
    public const string TrackId = "annotation";      // NOT "playback2d.annotations" (that is the
                                                     // layer id and the feature id)
    public AnnotationTrack(AnnotationDocument document);
    public string Id { get; }                        // TrackId
    public string DisplayName { get; }               // "Annotations"
    public bool IsAvailable(ITimelineData data);     // true once any element is anchored
    /// One marker per element with a non-null FromTick, on the FRAME-INDEX axis:
    /// FrameIndex = data.FrameIndexAtTick(FromTick); elements resolving to -1 are dropped.
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data);
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data);   // always empty
    public event Action? MarkersChanged;             // raised from AnnotationDocument.Changed
    public void Dispose();
}
```

### Namespace `DemoViewer.NET.Playback2D.Pipeline.Annotations`

```csharp
/// <summary>Which demo a sidecar belongs to. Sha256 is lowercase hex of the .dem bytes.</summary>
public sealed record DemoIdentity(string Sha256, string FileName, long SizeBytes);

/// <summary>Which parse the tick anchors were authored against: the DV frame clock, never CS2 ticks.</summary>
public sealed record ClockIdentity(string Kind, int TickRate, int FrameCount, int FirstTick, int LastTick)
{
    public const string DvFrameClock = "dv-frame-clock";
    public bool Matches(ClockIdentity other);
}

public enum AnnotationStoreLocation { None, DemoSidecar, AppData }

public sealed record AnnotationLoadResult(
    IReadOnlyList<AnnotationElement> Elements,
    AnnotationStoreLocation Location,
    string? Path,
    bool DemoMismatch,
    bool ClockMismatch,
    int SchemaVersion);

public sealed class AnnotationStore
{
    public const int SchemaVersion = 1;
    public const string SidecarExtension = ".dvann.json";

    /// <param name="appDataRoot">App-data root for the fallback location; null = no fallback (WASM).</param>
    /// <param name="demoKeyResolver">Demo path → SHA-256 hex. Injected so the App reuses its cache (D11).</param>
    public AnnotationStore(string? appDataRoot, Func<string, string>? demoKeyResolver = null);

    public bool IsPersistent { get; }
    public AnnotationStoreLocation ResolveLocation(string demoPath);
    public string? ResolvePath(string demoPath);
    public Task<AnnotationLoadResult> LoadAsync(string demoPath, ClockIdentity clock, CancellationToken ct = default);
    public Task<bool> SaveAsync(string demoPath, DemoIdentity demo, ClockIdentity clock,
        IReadOnlyList<AnnotationElement> elements, CancellationToken ct = default);
    public Task<bool> DeleteAsync(string demoPath, CancellationToken ct = default);
}
```

### App surface

```csharp
namespace DemoViewer.NET.Configuration;

/// <summary>2D playback preferences: ONE section for the whole module. B2 ADDS the properties
/// below to the existing class (B5 owns its full shape); it declares the class only if it is the
/// first lander. Binder-safe: parameterless ctor, every property defaulted. Every property must
/// have a SettingsService.WriteInMemory row.</summary>
public sealed class Playback2DSettings
{
    // ── Annotations (B2): names per correction 8 ──
    public string LastTool { get; set; } = "PanZoom";              // ToolKind name
    public uint AnnotationColorArgb { get; set; } = 0xFFFFC107;    // packed ARGB, not a string
    public double AnnotationWidth { get; set; } = 8;               // world units
    public double AnnotationOpacity { get; set; } = 1.0;
    public string AnnotationDefaultVisibility { get; set; } = "Always";  // Always | Fade | Custom
    public int AnnotationFadeInTicks { get; set; } = 8;
    public int AnnotationFadeOutTicks { get; set; } = 16;
    public int AnnotationHoldTicks { get; set; } = 320;            // 5 s at 64 tick
    public bool AnnotationAnchorToEntities { get; set; }
    public bool AnnotationAutoSave { get; set; } = true;
    public string[] AnnotationRecentColors { get; set; } = [];     // flattened as indexed keys
    // … Levels (B3), Timeline (A1/B3), Export (B4), RenderBackend (C2), LegacyViewport (B1) …
}
// AppSettings gains: public Playback2DSettings Playback2D { get; set; } = new();
```

```csharp
namespace DemoViewer.NET.Modules.Playback2D.Annotations;

public sealed class AnnotationSessionController : IDisposable
{
    public AnnotationSessionController(AnnotationStore store, IOptionsMonitor<AppSettings> settings,
        IFeatureGate gate);
    public AnnotationSession Session { get; }
    public bool IsEnabled { get; }                 // playback2d.annotations gate
    public string StatusText { get; }              // "saved to <demo>.dvann.json" / "session only" / error
    public bool ClockMismatch { get; }
    public Task AttachDemoAsync(string? demoPath, ClockIdentity clock);
    public Task FlushAsync();                      // deactivate / DemoReset / shutdown
    public void ApplyLevelRebuild(IReadOnlyDictionary<double, double> zMinMap);
    public event Action? StateChanged;
}
```

---

## Test plan

Two execution modes, per design §11:

* **Direct-execution** (no Avalonia platform, no window, no dispatcher): everything in Core and
  Pipeline. Fast, deterministic, runs in CI on every push.
* **Headless-Avalonia** (`HeadlessSession`, `src/App/DemoViewer.NET.App.Tests/HeadlessSession.cs`):
  only the two host-integration classes that genuinely need Avalonia input plumbing.

Framework is **TUnit** (`[Test]`, `await Assert.That(x).IsEqualTo(y)`, `SkipTestException` for missing
fixtures, `[NotInParallel]` for anything touching temp dirs or settings).

### Core tests: `{CORE}.Tests` (direct execution)

| Class | Cases |
|---|---|
| `TimeEnvelopeTests` | `Static_IsAlwaysFullyOpaque`; `Default_EqualsStatic`; `Trapezoid_RampsInBeforeFrom_AndOutAfterUntil` (D5); `NullFrom_IsNegativeInfinity`; `NullUntil_IsPositiveInfinity`; `ZeroLengthWindow_StillVisibleAtFromTick`; `OpacityAt_IsPure_SameAnswerRegardlessOfCallOrder` (scrub safety) |
| `AnnotationDocumentTests` | `Apply_Add_BumpsVersion_RaisesChanged`; `Gesture_ManyDeltas_SquashToOneUndoEntry`; `Gesture_ZeroDeltas_PushesNoUndoEntry`; `BailToMark_RollsBack_NoUndoEntry`; `Undo_Redo_RoundTripsElements`; `Apply_AfterUndo_ClearsRedo`; `NestedGesture_Throws`; `History_BoundedTo200Entries`; `RemapWorldLevels_RewritesLiveAndHistory_NoUndoEntry` (D6); `Undo_DoesNotTouchCameraOrPlayback`, asserted structurally: the document's public surface exposes no camera/playback type (design risk 13, "history lives only in `AnnotationDocument`") |
| `FreehandOutlineTests` | `Matches_ReferenceVector_StraightLine`; `Matches_ReferenceVector_PressureCurve`; `Matches_ReferenceVector_SharpCornerAndDuplicates`; `SinglePoint_ProducesClosedDot`; `Outline_IsClosed_And_NonSelfIntersecting_ForSmoothInput`; `Streamline_Zero_PreservesInputPositions`; `NoAllocation_OnWarmLists` (`GC.GetAllocatedBytesForCurrentThread()` delta == 0 over 1000 calls with pre-sized lists) |
| `AnnotationHitTestTests` | `HitsWithinHalfWidthPlusRadius`; `MissesOutsideOutline`; `TopmostWinsWhenOverlapping`; `EmptyStroke_NeverHits`; `WideStroke_InteriorPointHits` |
| `InputToolRouterTests` | `DefaultTool_IsPanZoom`; `PanZoom_Drag_PansCapturedPaneOnly` (two panes; the non-dragged pane's transform is unchanged, the invariant `Playback2DViewport.cs:409` encodes today); `PanZoom_Drag_SetsManualOverride`; `Wheel_ZoomsAboutCursor_UnderEveryTool` (D2); `SpaceHeld_DivertsNextPressToPan` (D3); `SpaceHeld_DoesNotHijackOpenGesture`; `Escape_CancelsGesture_AndBailsDocument`; `SetActive_MidGesture_CancelsFirst` |
| `DrawToolTests` | `Press_Move_Release_CommitsOneElement_OneUndoEntry`; `IntermediatePoints_AreAppended_InOrder`; `MinDistanceFilter_DecimatesJitter`; `TapWithNoMove_ProducesDot`; `AnchorMode_On_And_MarkerNearby_ProducesEntitySpaceRef`; `AnchorMode_On_And_NoMarker_FallsBackToWorldSpaceRef`; `NewElement_UsesSessionStyleAndEnvelope`; `Cancel_LeavesDocumentUnchanged` |
| `EraseToolTests` | `DragAcrossThreeStrokes_RemovesAll_InOneUndoEntry`; `NoHit_PushesNoUndoEntry`; `Undo_RestoresAllErased`; `RemovesSameStrokeOnce` |
| `AnnotationLayerTests` | `Advance_RerecordsDryPicture_OnlyOnVersionChange`; `WetStroke_DrawsOnlyInOriginPane`; `EntityAnchored_HiddenWhileUnresolvable`; `EntityAnchored_TracksMarkerAcrossFrames`; `WorldAnchored_DrawsOnlyInMatchingLevelPane`; `Advance_ReturnsTrue_OnlyWhileWetStrokeActive`; `SteadyState_ZeroAllocations` (512 Advance+Render frames after warmup, per §6) |
| `AnnotationLayerGoldenTests` | `Golden_StaticStrokes_Cpu`; `Golden_FadeInMidRamp_Cpu`; `Golden_EntityAnchored_Cpu`; `Golden_WetStroke_Cpu`: render a fixture scene through `SceneCompositor` on `CpuSurfaceProvider`, compare to a checked-in PNG byte-for-byte (CPU goldens are authoritative, §5.8) |
| `AnnotationTrackTests` | `BuildMarkers_OneMarkerPerAnchoredElement`; `StaticElements_ProduceNoMarkers`; `DocumentChanged_RaisesMarkersChanged`; `MarkerSeekTarget_IsFromTick` |

### Pipeline tests: `{PIPE}.Tests` (direct execution)

| Class | Cases |
|---|---|
| `AnnotationStoreTests` | `Save_WritableDemoDir_WritesSidecarBesideDemo`; `Save_ReadOnlyDemoDir_FallsBackToAppData`; `NoAppDataRoot_AndReadOnlyDir_IsNotPersistent`; `RoundTrip_PreservesElements_Exactly`; `RoundTrip_PreservesUnknownFields_RootAndElement`; `Load_UnknownSchemaVersion_LoadsTolerantly`; `Load_TruncatedJson_ReturnsEmpty_DoesNotThrow`; `Load_DemoHashMismatch_IgnoresSidecar_AndDoesNotOverwrite`; `Load_ClockMismatch_LoadsWithFlag_StaticElementsIntact` (D10); `Save_IsAtomic_NoPartialFileObserved`; `Save_OnIoFailure_ReturnsFalse_DoesNotThrow` (D12) |
| `AnnotationSchemaSnapshotTests` | `V1Schema_MatchesCheckedInSample`: a checked-in `tests/fixtures/playback2d/annotations/schema-v1.sample.json` deserialized and re-serialized must be field-identical (design §11 "annotation schema round-trip with unknown fields preserved") |

### App tests: `src/App/DemoViewer.NET.App.Tests` (headless Avalonia + direct)

| Class | Mode | Cases |
|---|---|---|
| `Playback2DAnnotationSettingsTests` | direct, `[NotInParallel]` | `Playback2D_Section_BindsFromEmptyFile_WithDefaults`; `Write_ThenRead_RoundTripsEveryKey`; `WriteInMemory_FlattensEveryPlayback2DKey` (drives a fileless `SettingsService(null)`, writes, reloads, asserts every property survived, the exact failure mode `SettingsService.cs:411-415` warns about); `WriteInMemory_ShrinkingRecentColors_DropsStaleIndices` |
| `Playback2DAnnotationHostTests` | **headless Avalonia**, `[NotInParallel]` | **The exit criterion, one case per clause:** `Draw_ThenSeek_StrokeSurvives`; `Draw_ThenZoomAndPan_StrokeStaysWorldAnchored` (screen position moves, world points identical); `Draw_ThenLevelRebuild_StrokeRemapsToSameLevel` (D6); `Draw_ThenDeactivateReactivateTab_StrokeSurvives`; `Erase_ThenUndo_StrokeReturns`; `Undo_AfterSeek_UndoesTheStroke_NotTheSeek` (undo-scope contract); `HoldSpace_DuringDrawTool_Pans`; `Escape_MidStroke_LeavesNoElement`. Captures a PNG per case to `HeadlessSession.ArtifactDir` for eyeball review, following `ZRadarRenderTests`. |
| `Playback2DAnnotationPersistenceTests` | direct, `[NotInParallel]` | `Controller_AutosavesAfterDebounce`; `Controller_FlushesOnDeactivate`; `Controller_LoadsOnDemoAttach`; `Controller_GateOff_NeverTouchesDisk` |

### Fixtures

* `tests/fixtures/playback2d/annotations/schema-v1.sample.json`: a v1 sidecar carrying one static
  freehand, one entity-anchored stroke, one unknown root field and one unknown element field.
* `tests/fixtures/playback2d/annotations/*.golden.png`: CPU goldens for `AnnotationLayerGoldenTests`.
* `tests/fixtures/playback2d/freehand/{straight,pressure-curve,sharp-corner}.json`: reference vectors,
  each `{ "options": {...}, "input": [[x,y,p],...], "outline": [[x,y],...] }`.
* A `SceneFixture` JSON with two levels and four markers (reuse B0's fixture; add
  `annotations-demo.json` if B0's corpus has no two-level scene).

**Generating the freehand reference vectors** (run once by a contributor, output checked in; CI never
runs node):

```bash
mkdir -p /tmp/pf && cd /tmp/pf && npm i perfect-freehand@1.2.2
node -e '
const {getStroke}=require("perfect-freehand");
const cases={
 straight:{o:{size:16,thinning:0,smoothing:0.5,streamline:0,simulatePressure:false},i:[[0,0,0.5],[100,0,0.5]]},
 "pressure-curve":{o:{size:16,thinning:0.5,smoothing:0.5,streamline:0.5,simulatePressure:false},
   i:[[0,0,0.1],[10,6,0.2],[22,14,0.35],[35,20,0.5],[50,22,0.65],[66,20,0.8],[80,12,0.9],[92,2,1.0]]},
 "sharp-corner":{o:{size:24,thinning:0.5,smoothing:0.5,streamline:0.5,simulatePressure:true},
   i:[[0,0,0.5],[0,0,0.5],[40,0,0.5],[40,40,0.5],[40,40,0.5]]}};
for(const [n,c] of Object.entries(cases))
  require("fs").writeFileSync(n+".json",
    JSON.stringify({options:c.o,input:c.i,outline:getStroke(c.i,c.o)},null,1));
'
```

Comparison tolerance: `1e-6` relative, `1e-4` absolute per coordinate (both sides are IEEE doubles;
this catches a mis-ported formula while tolerating last-bit ordering differences). The test asserts
point **count** exactly; a differing count means a structurally wrong port, never rounding.

### Commands

```bash
# Direct-execution suites: fast, no Avalonia, safe in CI
dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

# One class while iterating (TUnit filter)
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release -- \
  --treenode-filter "/*/*/FreehandOutlineTests/*"

# App suite: OOM-prone single process; use the batch runner (see scripts/test-app-suite.sh:1-12)
scripts/test-app-suite.sh -c Release -n 3

# Visual check of one golden without launching the app (needs C1)
dv2d render --fixture tests/fixtures/playback2d/annotations-demo.json --out /tmp/f.png
```

---

## Build & wiring

### New projects

B2 adds **no new production project**; Core and Pipeline come from B0. It adds test projects **only
if B0 did not**. If `{CORE}.Tests` is absent, create
`src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <!-- Distinct from the SUT namespace to avoid a type collision. -->
        <RootNamespace>DemoViewer.NET.Playback2D.CoreTests</RootNamespace>
        <!-- CA1707: test method names conventionally use underscores. -->
        <NoWarn>$(NoWarn);CA1707</NoWarn>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="TUnit"/>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
    </ItemGroup>

</Project>
```

`{PIPE}.Tests` is identical with `RootNamespace = DemoViewer.NET.Playback2D.PipelineTests` and a
`ProjectReference` to the Pipeline project (plus `DemoViewer.NET.TestSupport` only if a real demo is
ever needed; B2's Pipeline tests use temp dirs and synthetic bytes, so it is not).

### `DemoViewer.NET.slnx`

Add a `/src/Playback2D/` folder (or extend B0's) with the four project paths, following the
`/src/Playback2D/` block B0 creates (which holds Core, Pipeline and the single Tests project):

```xml
<Folder Name="/src/Playback2D/">
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Core.Tests.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Pipeline.Tests.csproj"/>
</Folder>
```

### `Directory.Packages.props`

**B2 adds no package.** Everything it needs is present or comes from B0:

* `SkiaSharp`: added by **B0** (no `PackageVersion` entry exists today; B0 owns pinning it). B2
  consumes it transitively through Core. If B0 has not landed it, B2 cannot start Group 3.
* `TUnit 0.25.21`: present.
* `Avalonia.Controls.ColorPicker 11.3.12`: present *and already referenced by the App*
  (`DemoViewer.NET.csproj:22`), so the color picker needs no csproj change.
* `System.Text.Json`: in-box on net10.0; no entry needed (`SettingsService`/`GraphBreakpointStore`
  already use it with no package reference).

**Version policy:** central package management (`ManagePackageVersionsCentrally=true`). Never write a
`Version=` attribute on a `PackageReference`; add or change the pin in `Directory.Packages.props` only.
Avalonia sub-packages move as one version (the file's own "keep version in sync!" note); a SkiaSharp
pin must be compatible with `Avalonia.Skia 11.3.12`'s own SkiaSharp dependency. B0 resolves this, and
B2 must not bump it independently.

### CI: `.github/workflows/ci.yml`

Today the workflow builds only `src/App/DemoViewer.NET.Desktop -c Release` and runs **no tests** (the
App suite is explicitly excluded as "single-process, OOM-prone"). The Core/Pipeline suites have none of
that problem (no Avalonia, no demo parse, no multi-GB transients), so B2 adds the first real test
execution to CI. Append after the existing build step (idempotent: skip if B0/B1 already added it):

```yaml
      - name: Test Playback2D Core
        run: dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release --no-restore
      - name: Test Playback2D Pipeline
        run: dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release --no-restore
```

Keep `fetch-depth: 0` (Nerdbank.GitVersioning) and the `10.0.x` `setup-dotnet` pin untouched. Golden
PNGs are compared on the Linux CPU provider, the same provider the goldens were authored on (§5.8);
do not add a GPU lane in B2 (that is C2).

### Style rules the generated code must satisfy

`Directory.Build.props` sets `TreatWarningsAsErrors=true` + `EnforceCodeStyleInBuild=true`, so
`.editorconfig` violations **fail the build**: 4-space indent, LF endings, 120-column max, file-scoped
namespaces (`namespace Foo;`), Allman braces on their own line, braces always (even one-liners),
**explicit types, never `var`**, and the repo-wide `#region` / `using …` / `#endregion` header block
before the namespace. `GenerateDocumentationFile=true` with `NoWarn=CS1591`, so XML docs are expected
by convention (every existing public member has one) but not compiler-enforced; write them anyway.
Analyzer traps this phase will hit: **CA1034** on nested `SpaceRef`/`DocDelta` cases (D9: suppress
with justification), **CA1051** if any public mutable field is introduced (use properties; the one
exception is B1's `LevelPane.Camera`, which B2 mutates; see Conflicts), and **CA1002** (do not expose
`List<T>`): all public collection returns are `IReadOnlyList<T>`, with `List<T>` only as a
caller-supplied output parameter.

---

## Dependencies

### Consumed from other phases

| From | API | Used by |
|---|---|---|
| **B0** | `Scene2DFrame`: must expose per-marker `ulong SteamId` (see Conflicts, C1) | `AnnotationLayer.Advance` entity resolution |
| **B0** | `SceneTime` (§5.1: `Tick`, `FrameIndex`, `DeltaSeconds`, `IsDiscontinuity`) | `AnnotationLayer.Advance` |
| **B0** | `CpuSurfaceProvider`, `SceneFixture` JSON loader | golden + layer tests |
| **B0** | `RenderPurpose` | (read-only; annotations render identically in all purposes) |
| **B1** | `ISceneLayer`, `LayerSlot`, `LayerCacheHint`, `SceneRenderContext` (must carry the pane, its camera transform and `SceneTime`), `SceneCompositor` registration | `AnnotationLayer` |
| **B1** | `LevelPane` (`Level`, `Camera`, `Rig`, `ViewportRect`), `MapLevel` (`Id`, `ZMin`, `ZMax`), `MapSpace.LevelFor(z)` + `LevelSetChanged` | `PanZoomTool`, `IToolServices`, level remap |
| **B1** | `Scene2DHost`: pointer/key events, pane hit-test, `InvalidateVisual`, RAF arming | `SceneHostToolServices`, router wiring |
| **B1** | `ViewportTransform`, `SliceCamera` (moved verbatim from `Modules/Playback2D/`) | `PanZoomTool` |
| **A1** | `ITimelineTrack`, `TimelineMarker`, `ITimelineData`; `TimelineControl` renders whatever tracks are registered | `AnnotationTrack` (D8) |
| **App (existing)** | `IModuleContext.CurrentTick` / `.CurrentFrameIndex` / `.DemoPath` / `.Players[].SteamId` (`IModuleContext.cs:16,40,43,56`; `PlayerRosterEntry.cs:17`); `IFeatureGate`; `IOptionsMonitor<AppSettings>`; `AppPaths.ConfigRoot` (`AppPaths.cs:81-93`) | `AnnotationSessionController`, `SceneHostToolServices` |

### Exported by B2 (and who consumes them)

| API | Consumer |
|---|---|
| `AnnotationDocument`, `DocDelta`, `AnnotationElement`, `SpaceRef`, `TimeEnvelope`, `AnnotationStyle`, `InkPoint` | **B3** (envelope drag handles apply `DocDelta.Replace`), **B4** (export renders the same document), **C1** (`dv2d render --annotations <file>`) |
| `AnnotationSession`, `AnnotationLayer` (`LayerId = "playback2d.annotations"`) | **B4** (`ExportRequest.LayerIds` toggles annotations per export; the id string is the contract), **B3** (level rebuild → `InvalidateLevels`) |
| `IPointerTool`, `IToolServices`, `ToolPointerEvent`, `InputToolRouter`, `PanZoomTool` | **B1** (replaces its interim pan handlers; see D1), **B3** (level strip may add a tool later), **B5** (keybind audit binds `D`/`E`/`Esc` to `SetActive`/`CancelActive`) |
| `AnnotationTrack` | **A1**/**B3** timeline (`TimelineControl` registers it) |
| `AnnotationStore`, `DemoIdentity`, `ClockIdentity`, `AnnotationLoadResult` | **C1** (`dv2d` loads a sidecar for headless render), **B4** (export loads the same document) |
| `FreehandOutline` | **B4** (export draws identical ink), **C1** |
| `Playback2DSettings` (+ its `WriteInMemory` keys) | **B3** (level prefs), **B4** (export prefs) extend the *same* section rather than adding new ones: one section per module |
| `playback2d.annotations` feature id | **B5** feature-flag audit |

### Conflicts for the integrator to resolve

**C1: `Scene2DFrame` markers need `SteamId`.** Today's `PlayerMarker` (`PlayerMarker.cs:31-45`) carries
`Slot` but **no** `SteamId`, and design §5.4 mandates SteamId anchoring because slots recycle. B0/B1
must add `ulong SteamId` to the scene marker record (the App already has slot → SteamId via
`IModuleContext.Players` / `PlayerRosterEntry.SteamId`, `PlayerRosterEntry.cs:17`). Fallback if B0 has
frozen the record: `AnnotationLayer` takes an optional `Func<int, ulong>` slot→SteamId resolver
supplied by the App. Inferior (it re-introduces slot as the join key inside the layer), so prefer the
field.

**C2: `LevelPane.Camera` is a public *field* in design §5.3**, which trips **CA1051** under
`TreatWarningsAsErrors=true` + `AnalysisMode=Recommended`. B2's `PanZoomTool` mutates it in place
(`pane.Camera.Current = …`, `pane.Camera.ManualOverride = true`), so a property returning a copy would
silently break panning. B1 must either keep the field with a justified CA1051 suppression or expose
`ref SliceCamera CameraRef()`. B2 assumes the field.

**C3: `ITimelineTrack` ownership.** Design §4 puts it in Core; A1 ships `TimelineControl` before Core
exists and may have defined it App-side. See D8/S7; B2 moves it to Core if needed.

**C4: `SkiaSharp` is not yet in `Directory.Packages.props`.** B0 owns adding and pinning it
(compatibly with `Avalonia.Skia 11.3.12`). B2 blocks on it for Group 3 only.

**C5: Project paths.** This plan assumes `src/Playback2D/DemoViewer.NET.Playback2D.{Core,Pipeline}`,
confirmed by integrator correction 6. B0 creates them; mirror those paths everywhere.

**C6: B1 must not build a tool abstraction.** Per D1, B1's `Scene2DHost` ships plain pan/zoom pointer
handlers that B2 deletes. If B1 has already introduced its own router/tool types, B2 adopts them only
if they match §5.5's `IPointerTool` exactly; otherwise B1's must be removed, not layered.

---

## Risks & spikes

| # | Risk | Mitigation | Time-box |
|---|---|---|---|
| **S1** | **Avalonia coalesced-pointer API shape.** `PointerEventArgs.GetIntermediatePoints(Visual?)` exists in 11.3.12 (verified in `Avalonia.Base.xml:24975`) and `PointerPointProperties.Pressure` / `RawPointerPoint.Pressure` exist (`:25075`, `:25208`), but the exact return element type (`PointerPoint` vs `RawPointerPoint`) and whether a mouse reports a usable pressure must be confirmed against the running build. | Spike: a throwaway headless test that logs the returned type, count and pressures for a synthetic drag. Fallback if pressure is absent/zero: constant `0.5` and `SimulatePressure = true` in `FreehandOptions` (velocity-derived width), which is upstream's own default. | **2 h**, first day |
| **S2** | **Perfect-freehand port fidelity.** ~300 loc of vector math with sharp-corner arc insertion and taper easing; a subtle sign error yields plausible-looking but wrong outlines. | Two-stage port (`GetStrokePoints` and `GetStrokeOutline` tested separately) + three checked-in reference vectors including a degenerate corner/duplicate case. Assert point count exactly. | **1 day** total incl. tests; if the corner case resists, ship with arc insertion disabled (visibly rounder corners, no correctness loss) and file a follow-up |
| **S3** | **`Version`-keyed dry cache thrash.** Any mutation bumps `Version`; a drag-erase across 30 strokes would re-record every level picture 30 times. | Deltas inside a gesture bump `Version` but the layer re-records at most once per `Advance` (it compares the version it last recorded, not the count of changes). Covered by `Advance_RerecordsDryPicture_OnlyOnVersionChange`. | none |
| **S4** | **Zero-allocation budget vs. the wet stroke.** §6 demands 0 bytes/frame steady state; a growing `List<InkPoint>` allocates as it doubles. | Pre-size wet buffers to 4096 samples at session construction and reuse; outline lists likewise. Steady state is measured *without* an active stroke (a stroke is a transient user gesture, not steady state), but `SteadyState_ZeroAllocations` also runs a second pass *with* a warm wet stroke and asserts zero after the first growth. | none |
| **S5** | **Level-set churn under live strokes.** `FloorSplitter` keeps learning (design §5.3 / risk 5); a rebuild mid-stroke could orphan the wet stroke's `SpaceRef.World(ZMin)`. | The wet stroke stores its origin level id *and* ZMin; `ApplyLevelRebuild` remaps the wet stroke too. Test: `Draw_ThenLevelRebuild_StrokeRemapsToSameLevel`. If a rebuild lands mid-gesture, the gesture is bailed (safer than committing to a level that no longer exists); a one-line status message explains it. | **2 h** to confirm B1's rebuild event ordering |
| **S6** | **Writable-directory probe cost / false negatives.** Probing per save on a network share is slow; a read-only Steam replay folder is common. | Probe once per directory per session, cached; probe is create+delete of a uniquely-named temp file, wrapped in try/catch. On any failure → app-data fallback (never an exception). | none |
| **S7** | **`ITimelineTrack` ownership (A1 vs Core).** If A1 shipped it App-side, `AnnotationTrack` cannot live in Core without moving it. | D8 makes moving the interface B2's first Group-3 task (3 mechanical files). Confirm with the integrator before starting T12. | **2 h** if the move is needed |
| **S8** | **Undo scope creep** (design risk 13). | Enforced structurally: `AnnotationDocument` has no reference to any camera/playback/selection type, and `Undo_DoesNotTouchCameraOrPlayback` asserts the public surface. Tools mutate cameras *directly through `LevelPane`*, never through the document. | none |

---

## Acceptance checklist

**Design exit criterion, "Draw/erase/undo survive seek, zoom, level switch, tab deactivate":**

- [ ] Draw a stroke, seek backward and forward → the stroke is still present, at the same world
      coordinates (`Draw_ThenSeek_StrokeSurvives`).
- [ ] Draw a stroke, zoom and pan → world coordinates unchanged, screen position transforms correctly
      (`Draw_ThenZoomAndPan_StrokeStaysWorldAnchored`).
- [ ] Draw a stroke, force a `MapSpace` rebuild → the stroke remaps to the same physical level, and no
      undo entry was consumed (`Draw_ThenLevelRebuild_StrokeRemapsToSameLevel`).
- [ ] Draw a stroke, deactivate the tab, reactivate → stroke present, document identical
      (`Draw_ThenDeactivateReactivateTab_StrokeSurvives`).
- [ ] Erase a stroke, undo → the stroke returns byte-identical (`Erase_ThenUndo_StrokeReturns`).
- [ ] Undo after a seek undoes the *stroke*, not the seek (`Undo_AfterSeek_UndoesTheStroke_NotTheSeek`).

**Additional B2 acceptance:**

- [ ] `AnnotationElement`, `SpaceRef` (World ZMin-keyed / Entity SteamId-keyed), `TimeEnvelope`,
      `AnnotationDocument` match design §5.4's sketches field-for-field.
- [ ] `IPointerTool` matches design §5.5's sketch method-for-method (no wheel member; D2).
- [ ] `PanZoomTool` reproduces today's pan/zoom behavior exactly, including per-pane capture and
      `ManualOverride` (`Playback2DViewport.cs:405-459` semantics preserved).
- [ ] Hold-Space reverts to pan; Esc mid-gesture leaves no element and no undo entry.
- [ ] One gesture = one undo entry; a zero-delta gesture = zero undo entries; drag-erase across N
      strokes = one undo entry.
- [ ] Undo history contains no camera, playback or selection state (structural assertion).
- [ ] Perfect-freehand port matches all three reference vectors within tolerance, with exact point
      counts.
- [ ] `THIRD-PARTY-NOTICES.md` carries a perfect-freehand MIT section naming the ported files, and both
      ported files carry a header pointing at it.
- [ ] Wet stroke renders incrementally from coalesced samples with pressure; committed ink is
      `SKPicture`-cached and re-records only on `Version` change.
- [ ] Erase is stroke-level via outline hit-test; no pixel erase exists.
- [ ] Entity-anchored strokes track their player by SteamId and hide while unresolvable or dead.
- [ ] `AnnotationTrack` puts a clickable marker on the timeline for every anchored element;
      "pin to now" stamps `FromTick = CurrentTick`.
- [ ] Sidecar `<demo>.dvann.json` is written when the demo directory is writable, app-data by demo hash
      otherwise, and nothing at all on WASM (with the session-only state stated in the UI).
- [ ] The sidecar records demo hash **and** clock identity; a clock mismatch warns and preserves static
      elements instead of corrupting anchors; a demo mismatch is ignored and never overwritten.
- [ ] Unknown JSON fields survive a load→save round trip at both root and element level.
- [ ] `Playback2DSettings` binds from an empty file with defaults **and** every key is flattened in
      `SettingsService.WriteInMemory` (asserted by a fileless round-trip test).
- [ ] `playback2d.annotations` is registered as a `SubFeature` of `tab.playback2d`; gated off, the
      toolbar is hidden, the layer is skipped, and nothing touches disk.
- [ ] CPU goldens for static / mid-fade / entity-anchored / wet ink are checked in and green.
- [ ] Steady-state allocation over 512 headless frames is 0 bytes with no active stroke.
- [ ] `dotnet build src/App/DemoViewer.NET.Desktop -c Release` is clean (warnings are errors), and both
      new CI test steps are green.

---

## Implementation notes (deviations)

Written at implementation time. Everything not listed here was built as the plan body and the
`Integrator corrections` block specify.

### Typed identity, where the plan body still said `string`

1. **`WetStroke.PaneLevelId` is `MapLevelId?`, not `string?`.** The contracts section spells it
   `string? PaneLevelId`, but B1 landed level identity as a typed `MapLevelId` struct precisely so the
   compiler rejects the id-vs-index (and now id-vs-name) mix-up design risk 5 is about. A string here
   would have been the one place in the level model that could be silently wrong. Same reasoning as B1's
   own deviation 1; B3 sees a typed id everywhere.

2. **`AnnotationLoadResult.Elements` stays `IReadOnlyList<AnnotationElement>`** as the contract says, and
   the unknown-JSON preservation is done by memoising the extension data per sidecar path inside
   `AnnotationStore` and re-emitting it on the next save. Returning DTOs would have leaked the
   persistence shape into every consumer for the sake of one round-trip guarantee.

### Additive API, agreed shapes unchanged

3. **`PanZoomGesture` gained `Press(LevelPane?, …)` / `Wheel(LevelPane?, …)` overloads.** Correction 7
   says B2 *wraps* B1's gesture, but the shipped signatures take a `PaneSet` and hit-test internally,
   and the router has already resolved the pane by the time a tool sees the event. Re-running the hit
   test inside the tool would be a second answer to a question the router already answered. The `PaneSet`
   overloads are unchanged and still delegate to the new ones.

4. **`AnnotationSession` carries the envelope authoring inputs** (`DefaultVisibility`, `FadeInTicks`,
   `FadeOutTicks`, `HoldTicks`, `AnchorWorldRadius`, `SampleSpacingFactor`) plus
   `EnvelopeForNewElement(currentTick)`. The contract lists only `NewElementEnvelope`, but T09 requires
   the tool to "snapshot `NewElementEnvelope` resolved against `s.CurrentTick`". Resolving needs the
   mode and the ramp lengths, and putting them anywhere else would mean the App recomputing the envelope
   on every settings change and pushing it in.

5. **`FreehandOptions` gained `WithSize` and `ForWidth`.** `ForWidth` is the single place an
   `AnnotationStyle.WidthWorld` becomes a `FreehandOptions.Size`, so the eraser's hit-test outline and
   the layer's drawn outline are provably the same polygon.

6. **`InputToolRouter` gained `IsDrawingToolActive` and `GestureTool`.** The first is what the view passes
   to `Playback2DKeymap.TryResolve` as its `toolActive` flag; without it the App would re-derive "is a
   drawing tool selected" from the session and the two answers could drift. The second is a test seam for
   "which tool actually took this gesture", which is the whole of decision D3.

7. **`AnnotationElement` has hand-written structural equality.** The synthesized record equality compares
   `Points` by REFERENCE, which makes an element that survived a save/load round trip unequal to the one
   that was written, the exact comparison persistence, export and the undo tests all need to make. Cost
   is O(n) in the sample count, paid only when something compares.

8. **`AnnotationSessionController` takes `SettingsService?`, not `IOptionsMonitor<AppSettings>`.** The
   controller must *write* the ink style back (colour, width, last tool), and `IOptionsMonitor` is
   read-only. `SettingsService` is the repo's single read+write seam for preferences and is what
   `Playback2DRenderer` already resolves. It also takes no `IFeatureGate`: correction 12 routes gates
   through `IModuleContext.Features`, so the gate arrives via `SetFeatures`.

9. **`AttachDemoAsync` gained a `force` parameter.** A tab RE-activation rebuilds the view but keeps the
   cached view-model, and reloading the sidecar there would throw away anything the debounce had not yet
   written. Activation passes `force: false` (skip when already attached to this demo); `DemoReset` passes
   `force: true`, which is the one moment the file on disk really is the newer truth.

10. **`Playback2DTabViewModel` now implements `IDisposable`**, following the existing
    `RuleWorkbenchTabViewModel` precedent. It owns the controller and the timeline track, and CA1001 is
    on for this assembly.

### Things found while building, that the plan did not anticipate

11. **`AnnotationDocument.Changed` is raised again when a gesture CLOSES.** Closing a gesture is the
    moment its deltas become an undo entry; until then they sit in the open batch and `UndoDepth` still
    reads zero. Without a notification there, the toolbar's undo button stayed greyed out after the first
    stroke of a session and only woke on some later unrelated mutation. `Version` is deliberately *not*
    bumped (no content changed, and the ink layer re-records every level's dry picture on a version
    change). Found by the headless exit-criterion suite; pinned by
    `AnnotationDocumentTests.Gesture_Close_AnnouncesTheNewUndoEntry` and `.Gesture_Close_DoesNotBumpVersion`.

12. **`RefreshGates` now raises `FrameUpdated`.** A feature-gate flip changes which LAYERS the surface
    should carry, and `Scene2DHost` only re-reads that on a frame push, the same mechanism the overlay
    toggles already use. Without it, switching `playback2d.annotations` off left the ink layer registered
    and drawing until the next playback push.

13. **`Scene2DHost` registers and removes the annotation layer under the render gate.** B1 review
    carry-forward 28 predicted exactly this: `RenderPane` walks the layer list by index on the render
    thread, and B2 is the first phase to add or remove a layer in response to a user action.

14. **The host owns ONE router over a swappable session.** `SceneHostToolServices.Session` is settable and
    the host re-points it when a view-model binds, rather than rebuilding the router. Rebuilding would
    drop a gesture that was in flight across a data-context change, and the router is also the thing that
    holds `IsSpaceHeld`.

### Test-plan deviations

15. **`FreehandOutlineTests` pins BOTH stages, not just the outline.** The generator script in the plan
    emits only `getStroke`'s output; the committed vectors also carry `getStrokePoints`, so a streamline
    or minimum-length error is attributed to stage 1 instead of showing up as a mysterious outline
    mismatch. All three vectors match upstream to 1e-6 relative with exact point counts.

16. **`SinglePoint_ProducesClosedDot` split in two.** Upstream's circular-dot branch fires when
    `getStrokePoints` collapses to ONE point, which happens for two coincident samples (what `DrawTool`
    commits for a tap), not for a lone sample, which upstream expands into a very short capped stroke.
    The circle assertion moved to `CoincidentPair_ProducesAClosedDot`; the single-sample case asserts a
    closed blob of about the stroke's width.

17. **`Save_IsAtomic_NoPartialFileObserved` is written as `Save_IsAtomic_NoTempFileLeftBehind`.**
    Observing a partial file requires racing the writer, which is a flaky test by construction. What is
    actually assertable (and what the atomic write exists to guarantee) is that a second save leaves no
    `.tmp` behind and the file still parses. `Save_OnIoFailure_ReturnsFalse_DoesNotThrow` covers the
    failure half by holding the destination open with no sharing.

18. **`AnnotationLayerGoldenTests` is not written as a separate checked-in-PNG suite.** Four annotation
    goldens would need a fixture corpus of their own plus a regeneration path, and what they would assert
    (static / mid-fade / entity-anchored / wet ink each land on the right pane with the right opacity)
    is asserted directly in `AnnotationLayerTests` by counting pixels, which localises a failure to one
    rule instead of to "the picture changed". `Playback2DAnnotationHostTests.DrawnStroke_RendersOnTheSurface`
    covers the end-to-end "ink reaches the real surface" claim with a captured PNG for eyeball review.

19. **Two A1 keymap tests were updated, not deleted.** `TryResolve_ReservedGesture_ReturnsFalseInA1`
    asserted that `X`, `D` and `Ctrl+Z` resolve to nothing; B2 binds all three. It is now
    `TryResolve_ReservedGesture_ReturnsFalse` (covering `Home`, still B3's) plus a new
    `TryResolve_AnnotationGestures_AreBoundByB2`. `TryResolve_ToolActive_PrefersToolScopedBinding`
    asserted the tool-scoped shadow resolves to *nothing*; it now asserts it resolves to `HoldPan` and
    `CancelGesture`.

20. **The `Playback2DAnnotationHostTests` fixture focuses the view before a key test.** Nothing focuses
    the 2D view until the user clicks the surface, so a key case that never clicks would silently assert
    nothing at all.

### Not built, and why

21. **`THIRD-PARTY-NOTICES.md` uses section `## e.`, not `## d.`**: B1 landed the Inter font as `d.`; the
    plan's instruction to "add `## d.`" predates it, and sections are appended in landing order.
    **Superseded at the merge; see 34.**

22. **`ContractVersion` was not bumped** (correction 11): A1 already set it to `1.2.0`.

23. **`Directory.Packages.props` and `.github/workflows/ci.yml` are untouched.** B2 adds no package, and
    its tests live in the two projects B0's `playback2d-tests` job already runs.

24. **`AnnotationHitTester` throws `NotSupportedException` for non-`Freehand` kinds** rather than
    answering "no hit". A silent miss for a shape kind nobody implemented is an eraser that mysteriously
    refuses to erase; the plan permitted the stub and this is the loud version of it.

25. **Attaching to a demo no longer writes an empty sidecar.** Loading calls `AnnotationDocument.Reset`,
    which raises `Changed`, which scheduled an autosave, so simply *opening* a demo dropped an empty
    `.dvann.json` beside it. Caught by a stray `assets/tour/sample-de_nuke.dem.dvann.json` appearing in
    the repo's own working tree after an App-suite run. Two guards, because either alone is incomplete:
    autosave is suppressed for the duration of a load, and a save of an EMPTY document is skipped unless
    a sidecar already exists; erasing the last stroke must still clear the file. Pinned by
    `Playback2DAnnotationPersistenceTests.Controller_AttachingToADemoWithNoAnnotations_WritesNothing`
    and `.Controller_ErasingTheLastStroke_StillClearsAnExistingSidecar`.

26. **`AnnotationNukeLevelTests` added, over the real two-floor Nuke frame.** The plan's fixture list asks
    for "a `SceneFixture` JSON with two levels"; B1's `nuke-multilevel` capture already is one, so the
    level-anchoring rule is proved against the bands `MapSpaceFactory` actually derives
    (`-1562:[-100000..-528]` and `-8:[-528..100000]`) rather than only against a synthetic band list.
    `SceneStage` gained a `params ISceneLayer[] extra` so the ink layer is exercised over the SAME seven
    layers the app ships. Ink is measured as a delta against a render with an empty document, inside each
    pane's real rectangle: Nuke's lower floor already carries red team discs, and an absolute red count
    would be measuring the markers.

27. **The deactivate flush is SYNCHRONOUS (`AnnotationSessionController.Flush`).** T17 lists "flush on
    tab deactivate / DemoReset / shutdown", and the shell's shutdown path is
    `MainViewModel.Dispose → SelectedTab.Deactivate() → OnDeactivated`, where a fire-and-forget write
    races the process exit and loses the stroke someone drew ten seconds before quitting. Every await
    inside the controller and the store is `ConfigureAwait(false)`, so blocking there cannot deadlock on
    the UI context, and the payload is a small JSON file: the same trade
    `SettingsService.SaveSession` already makes on this thread. `FlushAsync` is still there for callers
    that have somewhere to await.

### Review findings (independent reviewer, on top of `c82d516`)

Six defects found by independent review, each pinned by a regression test that fails without the fix.

28. **Coalesced pointer samples reached the ink BACKWARDS, with the oldest dropped and the primary one
    duplicated.** `Scene2DHost.Translate` walked `GetIntermediatePoints` from the end, on the belief that
    Avalonia returns it newest-first. It does not: verified against Avalonia 11.3.12 (a
    `PointerEventArgs` built with three known previous points), `GetIntermediatePoints` is literally
    *previous raw points ++ `GetCurrentPoint`*: **oldest-first, with THIS event's own point LAST**. The
    shipped loop therefore emitted `[current, p₍n−1₎ … p₁]` and dropped `p₀`, and `DrawTool` then appended
    the primary point again: every sub-frame batch zig-zagged and duplicated a sample. Invisible to the
    whole suite, because headless `MouseMove` carries no sub-frame history. But a real 1000 Hz digitiser,
    or a plain mouse on a 60 Hz surface, hits it on every fast drag. The plan's S1 spike is what this was
    meant to settle. Fixed by walking forwards and dropping the trailing entry; pinned by
    `Playback2DAnnotationHostTests.CoalescedSamples_ReachTheInk_OldestFirst_AndOnlyOnce`, which builds a
    real event with history through Avalonia's internal constructor so an upstream ordering flip fails
    here rather than shipping.

29. **The eraser hit-tested the WHOLE document at the RAW stored coordinates.** `EraseTool` called
    `AnnotationHitTester.HitTestAll`, which knows nothing about panes, levels or the clock. Three separate
    consequences, all silent data loss: on a stacked two-floor map (Nuke, the phase's own fixture) both
    panes show the same world XY, so erasing on the lower floor deleted the upper floor's callout from a
    band it was never drawn in; an entity-anchored stroke is DRAWN at `marker + offset` but was TESTED at
    its authoring coordinates, making it un-erasable where the user can see it and erasable at a phantom
    location; and a stroke its envelope had faded to nothing was still erasable. `IToolServices` gains
    **`TryResolveDrawOffset(LevelPane, AnnotationElement, out float, out float)`** (the same resolution
    `AnnotationLayer` performs per frame, returning false when the element does not render in this pane at
    all), and `EraseTool` now walks the elements topmost-first, skips anything the envelope has hidden,
    and tests at `world − offset`. Pinned by `EraseToolTests.EraserOnOneFloor_LeavesTheOtherFloorsInkAlone`,
    `.EntityAnchored_IsErasedWhereItIsDrawn_NotWhereItWasAuthored` and `.StrokeOutsideItsEnvelope_IsNotErased`.
    *Carry-forward:* the offset rule now exists twice (layer + services). B3 should hoist it into one Core
    helper before the level work makes them drift.

30. **`Undo` during an open gesture destroyed the previous stroke, unrecoverably.** Ctrl+Z is bound
    `Always` and the pointer being captured does not stop a key from arriving. Undoing mid-stroke popped
    the PREVIOUS entry onto the redo stack; the in-flight stroke's own `Apply` then cleared that redo
    stack on the next sample, leaving the earlier stroke deleted with no way back. `Undo`/`Redo` now
    return false while `IsGestureOpen`: the gesture is the user's current intent, and history editing
    waits for it to finish. Pinned by `AnnotationDocumentTests.Undo_DuringAnOpenGesture_IsRefused_AndLosesNothing`.

31. **Ctrl+X mid-stroke threw `InvalidOperationException` out of a key handler.** "Clear all" opens a
    gesture of its own, and gestures deliberately do not nest (D4), so the guard that makes nesting a bug
    detector became a crash on a bound keystroke. `ClearAll` and `PinToNow` now stand down while a gesture
    is in flight. Pinned by `Playback2DAnnotationHostTests.ClearAll_MidStroke_StandsDown_InsteadOfThrowing`.

32. **Entity-anchored ink could not work in the running app at all: `SceneFrameInput.SteamIdForSlot` was
    never supplied.** Correction 4 added the resolver to the builder for exactly this feature, but
    `Playback2DTabViewModel.BuildFrame` never passed one, so every real `PlayerMarker.SteamId` was 0, and
    both halves of the feature treat 0 as unresolvable, so the tool would not capture an anchor and the
    layer would not draw one. Every direct-execution test stayed green because they all inject their own
    markers. The roster already carries `PlayerRosterEntry.SteamId`; it is now cached slot-keyed in
    `SeedRosterDisplay` alongside the name and handed to the builder. Pinned by
    `Playback2DAnnotationHostTests.EntityAnchor_IsCapturedFromARealSceneFrame`, which asserts against the
    frame the app itself builds.

33. **Persistence: two threads, one dictionary, and one temp-file name.** `AnnotationStore`'s
    writable-directory probe cache and its unknown-JSON memo are plain `Dictionary` instances reached from
    both the UI thread (`ResolvePath`, for the panel's status line) and the thread pool (the debounced
    autosave, `LoadAsync`'s continuation); a read racing a write there can spin forever inside bucket
    traversal, not merely lose an entry. They are now behind one `Lock`, with the probe itself left outside
    it so a dead network share cannot stall the UI thread. Separately, cancelling the debounce does not
    stop a save already inside the store's write, so a flush-on-deactivate immediately after a stroke could
    run concurrently with it: both wrote the same `<path>.tmp` and raced to `File.Move` it, which can leave
    the OLDER snapshot on disk with nothing scheduled to correct it. `AnnotationSessionController` now
    serializes saves and stamps each snapshot with the document `Version`, so a slower writer stands down
    instead of overwriting a newer document.

**Verified, no change needed:** envelope opacity is frame-exact at both ramp edges and at zero-length
fades; strokes on the wrong level do not render (`AnnotationNukeLevelTests`, real two-floor Nuke bands
`-1562:[-100000..-528]` / `-8:[-528..100000]`); the ink layer allocates 0 B/frame steady-state when idle
(second window 0 B over 512 Advance+Render frames) and the B1 budget lane is unchanged at 0 B/frame;
`BailToMark` mid-gesture rolls back every delta with no undo entry, for both tools.

**Carry-forwards for later phases**

- **C1/B4:** entity anchoring is proved against synthetic frames and against ONE real Nuke frame; there is
  no multi-frame real-demo seek test, because B2 has no `TrackerFrameSource` yet. Add one when C1 lands.
- **B5:** the App builds `AnnotationStore` with the DEFAULT demo-key resolver, so the first autosave of a
  session streams a full SHA-256 of the `.dem`. It is off the UI thread, so D11's hard rule holds, but D11
  also asked the App to pass `DemoLibraryService`'s cached `(size, mtime)` hash and it does not.
- **B3:** see 29. One Core helper for the draw offset, consumed by both the layer and the tool services.

### Merge into `feature/playback2d-v2` (integrator, on top of `460e201`)

B2 branched from `f6ae1ab`; by the time it merged, C1 (`477cbd4`) and C2 Stage 0 + C2.7 (`2475f93`,
`460e201`) had already landed. `git merge feature/playback2d-v2-b2` produced **one** conflict.

34. **`THIRD-PARTY-NOTICES.md`: perfect-freehand landed as `## f.`, not `## e.`** The merge's only
    conflict was an add/add on `## e.`: C2 had taken it for ANGLE, B2 had taken it for perfect-freehand.
    Resolved by keeping **both**, in landing order (§4.3's rule): `d.` Inter font (B1), `e.` ANGLE (C2),
    `f.` perfect-freehand (B2). Nothing referenced the letter: `FreehandOutline.cs` and
    `FreehandOptions.cs` cite the section *by name* (`§ "perfect-freehand (MIT)"`), which is why the
    rename costs no code change. Deviation 21 is superseded by this one.

35. **`tests/fixtures/playback2d/scenes/nuke-multilevel.scene.json` is regenerated with real
    `steamId` values.** `Playback2DGoldenCaptureTests` rewrites the fixture unconditionally from the same
    push that produces the PNG, and finding 32's fix (the App now supplies
    `SceneFrameInput.SteamIdForSlot` from the roster) means every marker in that fixture now carries its
    real SteamId instead of `0`. The paired golden is **unaffected**: SteamId reaches no draw call, and
    `GoldenParityTests`/`Playback2DGoldenCaptureTests` both stay green at the same numbers. Committed as
    part of the merge so the capture is idempotent again and so C1/B4 fixtures carry usable ids. This
    also explains the CRLF churn the implementer and reviewer both reported on this file: it is rewritten
    on every Windows app-suite run, and `.gitattributes` normalises the line endings back to LF on
    staging, so only genuine content changes ever reach a commit.

**Verified at the merge (`460e201` + B2):** `dotnet build DemoViewer.NET.slnx` 0 errors / 0 warnings;
Playback2D suite **349/349**, 0 failed, 0 skipped (up from 282 on the B2 branch alone; C1/C2 added 67);
the 59 annotation-class tests pass with the ink allocation gate at **window1 19 704 B / window2 0 B**;
the B1 budget lane unchanged at **advance p99 0.007 ms, render p99 1.851 ms, 0 B/frame**; golden parity
on `nuke-multilevel` unchanged at **identical 63.72 %, ±8 99.68 %, ±32 99.74 %, max 204**; App suite
**812 total / 708 passed / 98 skipped / 6 failed**, all six the known environmental set.

**Not a regression, confirmed by re-running it at `460e201`:** the `dv2d` CLI suite is 107 total /
106 passed / 1 failed: `BenchAllocationTests.SmallestDrawingFixture_AllocatesNothingPerFrame`, 3 336 B,
**identical byte count before and after the merge**. It is `[Category("Budget")]` and its own doc comment
says it is expected to fail until `SceneLayerCatalog` registers B1's seven layers; CI excludes it
(`Category!=Budget`).

**Carry-forward from the merge: `SceneLayerCatalog` still registers only B0's `DebugGridLayer`.** B1's
seven layers were never added to it, so `playback2d.annotations` is not headless-selectable via
`dv2d --layers` either. Closing that seam (C1 risk R6 / C1 deviation 14) should add the annotation layer
in the same pass, together with a source for `SceneFixture.Annotations`.

### Post-merge fix: the two top-left toolbars (`fix/p2d-toolbar-conflict`, on top of `c393602`)

36. **T18's "mount the toolbar as a top-left `Border`" was wrong by the time it was executed: A4's
    overlay-toggle strip already owned that corner, and B2 mounted the annotation toolbar underneath it.**
    Both claimed `HorizontalAlignment=Left` + `VerticalAlignment=Top` + `Margin=10` in the SAME grid cell
    of `Playback2DView.axaml`'s left column, and every overlay in that cell is a plain sibling of every
    other, so the strip, being the LATER sibling, painted over the toolbar and won its hit tests.
    Measured headlessly at 1000×700 before the fix: toolbar `(10,10) 656×74`, strip `(10,10) 485×40`,
    intersection **485×40 px**, the entire tool row. `Window.InputHitTest` at the Pan tool's own centre
    `(37, 38.5)` returned a `Border` that is not an ancestor of the button, i.e. **Pan / Draw / Erase were
    unclickable**, and so were the colour picker, the width slider, the envelope box, "Pin to now",
    "Track player" and undo/redo. Only the trailing "Clear" and the status line stuck out below/right of
    the strip. The keymap's `D` / `X` still selected the tools, which is presumably why the suite (all
    view-model-level for the toolbar) never noticed.
    **Fix:** the two left-hand overlays now share ONE `StackPanel x:Name="TopLeftHud"` anchored top-left.
    The always-present toggle strip is first and the gated toolbar sits under it, so a
    `playback2d.annotations` flip cannot shove the strip around; both children are `HorizontalAlignment=Left`
    so the narrower strip does not inherit the toolbar's width as dead translucent chrome; the panel has no
    `Background`, so the 6 px gap and the ragged right edge still pass pan/draw gestures to the canvas. The
    gate moved onto the mounted element (`IsVisible="{Binding IsEnabled}"`, resolved against the element's
    own `Annotations` DataContext); the control's internal gate collapses only its inner `Border`, which
    would have left this element's top margin in the stack. `TransportBar`, `OverlayToggles` and
    `ExportButton` gained `x:Name`s purely so the regression test can address them.

37. **The toolbar's tool row was a fixed horizontal `StackPanel` ~775 px wide, in a viewport column that is
    narrower than that on any window under ~1100 px.** Nothing in the left cell clips, so at 820 px
    (column 496 px) the row ran the trailing controls out of the column and under the roster panel, a
    later sibling again, so it painted over them and took their clicks. `DesiredSize` cannot detect this
    (`Layoutable.MeasureCore` clamps it to the available width, so a clipped row still reports "I fit");
    the trailing control's arranged rect can, and does: `ClearAllButton` sat at x≈724 in a 496 px column.
    **Fix:** the row is a `WrapPanel` (`ItemSpacing=6`, `LineSpacing=4`): reflow instead of clip, the same
    call design-system D35 made for the Library filter toolbar. Measured after: 775 px on one line at
    1400 px, two lines at 1000 px, three at 820 px, `Clear` inside the column at all three.

**Considered and deliberately NOT changed.** Arrow keys and Space reaching the annotation toolbar's
`Slider` / `ComboBox` / `ToggleButton`s are eaten by the tab's tunnelling keymap handler, exactly as they
are for A4's overlay `CheckBox`es. That is the shipped, documented and pinned behaviour
(`Playback2DKeyRoutingTests.SpaceOverFocusedCheckbox_TogglesPlay_NotTheCheckbox`), not a toolbar conflict.
The two toolbars share no toggle, so there is nothing duplicated or contradictory between them, and they
are not modally exclusive: the overlay strip is layer visibility, the annotation toolbar is a tool palette.

**Regression test:** `src/App/DemoViewer.NET.App.Tests/Playback2DHudLayoutTests.cs`, 6 cases.
Geometry is the only honest assertion here; a "is it in the right container" test would have passed on the
broken tree. It pins (a) pairwise non-overlap of every INTERACTIVE viewport overlay
(`AnnotationToolbarHost`, `OverlayToggles`, `TransportBar`, `LevelStrip`, `ExportButton`; the kill-feed /
live-sync stack is excluded, being `IsHitTestVisible=False`), (b) each tool button being the topmost
`InputHitTest` result at its own centre, (c) the toggle strip not moving a pixel when the annotations gate
flips, and (d) the toolbar's trailing control staying inside the viewport column at 1400 / 1000 / 820 px.

**Verified:** `dotnet build DemoViewer.NET.slnx` 0 errors / 0 warnings. App `*Playback2D*` slice
**188 total / 169 passed / 19 skipped / 0 failed**; `*Timeline*` 42 / 38 / 4 / 0; `Scene2DHostTests`
10 / 10 / 0 / 0; `DemoViewer.NET.Playback2D.Tests` **481 / 481**. Full App suite **903 total / 799 passed /
99 skipped / 5 failed**: the same five (`DiagnosticsFileLogTests` ×3, `DemoLibraryServiceTests` ×2)
reproduced on an untouched `c393602`, so pre-existing and environmental.
