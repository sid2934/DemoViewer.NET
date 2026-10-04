# Step Authoring: design

> **Status: APPROVED 2026-09-24**, as recommended (§8): stationary rule for absent entries;
> ten tokens with `O1..O5` additive; one undo stack in `StratSession`; keymap rows shared through
> `Playback2DKeymap`; default export GIF at 20 fps, 640 wide, first step to last plus 2 s;
> `StratFrameSource` in Pipeline; the strat JSON reader stays in the App for now (`dv2d strat` deferred);
> midpoint level snap on a cross-floor move; text at `6 × WidthWorld`; Create Strat From Round cadence of
> utility events plus plant plus a 10 s sweep at 200 units. Shape Tools ships to the §3.2 contract
> verbatim. The `ISceneFrameHost` seam over `Scene2DHost` is the one the integrator flagged as the riskiest
> App change in the set; it gets a design note of its own at build time before the host is touched. No
> engine change. Nothing here is implemented.

**Work item:** Step Authoring (plan.md §3, Phase 3) · **Kind:** design, review required · **Status:** approved 2026-09-24 (banner above)
**Depends on:** Strat Model (design, `designs/strat-model.md`), Shape Tools (build, prerequisite; §3.2 says exactly what it must ship)
**Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0 · **Written:** 2026-09-23
**Unblocks:** Strat Export, Create Strat From Round (the pre-population half), Role View And LAN Print (the mini-map polyline).

Nothing here is implemented. Every code reference describes the tree at `d90ec9f`. Measurements were taken with `dv2d bench` on the committed corpus and with a scratch probe over two untrimmed Valve matchmaking replays from the Steam `replays` folder (build 10896); the tour sample was not used.

---

## 1. Problem and scope

A strat (Strat Model §3.3) is a list of steps on the round clock. Each step reserves `positions[]`, `strokes[]`, `holdSeconds` and `interpolation` "with semantics defined by Step Authoring" (Strat Model §3.9). This document defines those semantics and the tooling that writes them: a coach adds a step, drags the ten tokens to where they should be at that step, draws arrows and text that belong to that step, and the canvas plays the strat by interpolating token positions between steps while the scrubber moves. The result must export to GIF or MP4 with no demo behind it (Strat Export), and be pre-populated from a real round (Create Strat From Round).

What is new is a **keyframe model**: a token position is a function of time defined by a sparse list of keyframes, not a stroke with an envelope. The annotation model has no such thing (finding F7). Everything else reuses what exists: the Skia canvas and its pane model, the annotation document and its tools, the time envelope for per-step strokes, the timeline tracks, and the export session.

This design fixes:

- the **keyframe track** per slot, its interpolation, what a slot without an entry at a step does, and how a branch is authored and played (§3.3, §3.4);
- how **per-step strokes** reuse `TimeEnvelope` on a synthetic frame clock (§3.5);
- the **synthetic frame source** (`StratFrameSource`) and Strat Export through `SceneExportSession` unchanged (§3.6);
- the split between **Playback2D.Core, Playback2D.Pipeline and the App** (§3.1), keeping Core Avalonia-free;
- the **tools and keybinds**, in `Playback2DKeymap` (§3.7);
- **undo/redo** through `DocDelta` inside a gesture and through the Strat Model's `PatchOp` stack across gestures (§3.8);
- **Create Strat From Round** pre-population, measured on real demos (§3.9);
- **Shape Tools** as the prerequisite, and exactly what it ships (§3.2);
- **browser-host** behaviour (§3.10) and a **golden-image test plan** through `dv2d` (§7).

Out of scope: the Strat Book tab's non-canvas panes (Strat Model §3.11), the record and history panes, lineups (the Utility Book defines the Lineup object; a step's `utility.lineupId` stays an opaque id here), and any practice-server push (D4).

---

## 2. What exists today (cited)

### 2.1 The annotation document and its tools

- `AnnotationKind` declares Freehand, Line, Arrow, Rect, Ellipse, Text (`src/Playback2D/DemoViewer.NET.Playback2D.Core/Annotations/AnnotationElement.cs:14-33`). Only Freehand is written: `DrawTool.OnReleased` builds a Freehand element (`Core/Input/DrawTool.cs:136-146`), `AnnotationStore.ToElement` forces every other kind back to Freehand on load (`Pipeline/Annotations/AnnotationStore.cs:561-573`), `AnnotationHitTester.HitTest` throws `NotSupportedException` for anything but Freehand (`Core/Annotations/AnnotationHitTester.cs:50-55`), and `AnnotationLayer.BuildPath` runs every element through `FreehandOutline` regardless of kind (`Core/Layers/AnnotationLayer.cs:434-468`). Shape Tools has to touch all four.
- `AnnotationElement` is `(Id, Kind, Style, Space, Time, Points, Text, Timing)` (`AnnotationElement.cs:351-359`); `SpaceRef.World(LevelMinZ)` keys a floor by its quantized lower Z (`:229`); `TimeEnvelope(FromTick, UntilTick, FadeInTicks, FadeOutTicks)` is a pure trapezoid on the DV frame clock with `PinnedTo` (`:267-331`).
- `AnnotationDocument` is an ordered element list with a 200-entry delta-stack undo: `BeginGesture` (`Core/Annotations/AnnotationDocument.cs:87-98`), `Apply` (`:129-151`), `ApplyMigration` for system events that must not consume an undo slot (`:162-170`), `Undo`/`Redo` refused while a gesture is open (`:186-228`), `RemapWorldLevels` (`:260`), `Reset` (`:297-320`). `DocDelta` is `Add | Remove | Replace | Batch` with the inverse computed at apply time (`Core/Annotations/DocDelta.cs:15-42`).
- `AnnotationSession` holds the document, the wet stroke, the style and the envelope template. `DefaultVisibility` selects how `EnvelopeForNewElement(currentTick)` composes the envelope; `EnvelopeMode.Custom` returns `NewElementEnvelope`, which `SetCustomWindow(from, until)` composes with the session's ramps (`Core/Annotations/AnnotationSession.cs:401-510`). `TicksPerSecond` defaults to 64 (`:325`, `:354-358`).
- `IPointerTool` is four methods and no wheel (`Core/Input/IPointerTool.cs:115-138`); `IToolServices` is the whole host seam: session, `CurrentTick`, `NowMilliseconds`, pane and world transforms, entity-anchor and draw-offset resolution, `RequestRender` (`:145-221`). `InputToolRouter` routes one gesture to one tool, decided at press (`Core/Input/InputToolRouter.cs:125-159`); `IsDrawingToolActive` is `Draw or Erase` (`:86`) and is what the keymap's tool scope keys off. `ToolKind` is `PanZoom, Draw, Erase` and is persisted by name in `Playback2DSettings.LastTool` (`IPointerTool.cs:11-22`).
- `AnnotationLayer` splits elements into a cached dry picture per level (Static and World, `AnnotationLayer.cs:191-243`) and a per-frame prepared list for anything time- or entity-anchored (`:283-355`). It is `LayerSlot.Overlay`, order 100 (`:100-103`).
- `AnnotationTrack` puts one marker per time-anchored element on the frame-index axis and no bands (`Core/Timeline/AnnotationTrack.cs:86-116`). `ITimelineTrack` offers markers and bands, `TimelineMarkerKind.Custom` exists (`Core/Timeline/ITimelineTrack.cs:7-41`); `ITimelineData` is six primitives (`Core/Timeline/ITimelineData.cs:67-86`).

### 2.2 The scene, the export loop and the headless path

- `Scene2DFrame` is markers, area effects, trails, bomb, kill feed, game info, map info, vision, follow slot; every property is init-only and the builder refills pooled lists in place (`Core/Scene2DFrame.cs:24-109`). `PlayerMarker` is slot, team (2 = T, 3 = CT), world XYZ, yaw, ring, label, alive, pitch, duck, SteamId (`Core/PlayerMarker.cs:28-45`). `SceneTime` is `(Tick, FrameIndex, DemoSeconds, DeltaSeconds, IsDiscontinuity)` and the tick is the DV frame clock (`Core/SceneTime.cs:19-24`). `SceneGameInfo` carries `RoundSeconds` and `RoundTime` for the clock HUD (`Core/SceneGameInfo.cs:26-44`). `SceneMapInfo` carries `MapName`, `NetworkedBounds`, `ObservedBounds`, `SectionHeights`, `Radars` (`Core/SceneMapInfo.cs:12-40`); `MapRadarImage` carries world `Bounds` and a Z band (`Core/MapRadarImage.cs:14-29`). `AreaEffect` is `(Kind, WorldX, WorldY, WorldZ, WorldRadius)` with `Smoke | Fire` (`Core/AreaEffect.cs`).
- `MarkerLayer` draws a disc, yaw stub, ring and a label through `TextBlobCache` at `SceneDefaults.MarkerLabelSize`, positions taken from `MarkerSmoother` (`Core/Layers/MarkerLayer.cs:136-186`). `MarkerSmoother.Advance` glides toward the sampled position, snaps on `IsDiscontinuity`, on a level crossing, and beyond 250 units (`Core/MarkerSmoother.cs:105-173`).
- `ISceneFrameSource` is `FrameCount`, `TimeAt(i)`, `FrameAt(i)` (`Core/Export/ISceneFrameSource.cs:16-28`); `IPreparableFrameSource` is optional (`:40-48`). `FixtureFrameSource` replays committed scenes with no demo (`Pipeline/Frames/FixtureFrameSource.cs:24-57`). `TrackerFrameSource.TimeAt` derives `DeltaSeconds = speed / fps` and `IsDiscontinuity = frameIndex == 0` (`Pipeline/Frames/TrackerFrameSource.cs:198-208`); `OutputFrameCount` is `1 + floor(tickSpan / (speed × rate / fps))` (`:282-300`).
- `SceneExportSession.RunAsync` is `TimeAt` then `FrameAt` then `Advance` then `Render` then readback then sink, per frame (`Pipeline/Export/SceneExportSession.cs:229-264`), refusing GPU providers (`:153-159`), enforcing `GifMaxFrames = 1800`, `GifMaxWidth = 1920`, GIF fps in {10, 20, 25, 50} and video fps in {24, 25, 30, 50, 60, 64} (`:39-53`, `Validate` `:324-383`). It takes whatever source it is handed; nothing in it reads a demo. `LayerEnabledScope` applies `ExportRequest.LayerIds` and the opt-in rule (`:453-504`). `ExportRequest` is source-relative frames plus fps, size, speed, format, layer ids, camera (`Core/Export/ExportRequest.cs:30-45`).
- `SceneLayerCatalog.CreateSceneStack` is the one place a headless stack is built; the ink and the three HUD layers are opt-in by name and are registered only when fed (`Pipeline/Headless/SceneLayerCatalog.cs:138-211`). Its doc says an export must never be handed the live document (`:124-130`). `HeadlessSceneRenderer.Advance` derives levels, reconciles panes, applies the one-shot `AutoFitOnFirstMapBounds`, the pin and the policy, then advances the compositor (`Pipeline/Headless/HeadlessSceneRenderer.cs:211-288`).
- `SceneLayerIds.OptIn` is annotations plus the three HUD ids (`Core/Layers/SceneLayerIds.cs:60-67`). `dv2d export` picks `ManagedGifSink` when GIF is asked for and no ffmpeg is found, else `FfmpegFrameSink` (`tools/DemoViewer.NET.Playback2D.Cli/ExportCommand.cs:218-223`), and loads the demo's ink through the production `AnnotationStore` (`:462-482`).

**Measured** (`scripts/dv2d.sh bench --name synthetic-tenplayers --frames 600 --warmup 64 --cpu --json`, 2026-09-23, `d90ec9f`, CPU raster, 640×360, AMD 32-thread desktop): advance p50 0.0014 ms, render p50 **0.434 ms**, p95 0.458 ms, p99 0.582 ms, max 1.137 ms, **0 bytes per frame**. Ten markers over the grid fallback is the shape of a strat frame; a 1,200-frame strat GIF is under a second of drawing before encoding. Output: `step-authoring-bench.json`.

### 2.3 The host, the view-model seam and the keymap

- `Scene2DHost : Control, IPlayback2DSurface, ILevelSurface, IAnnotationSurface` owns the compositor, the panes and the `InputToolRouter` with `DrawTool` and `EraseTool` registered (`src/App/DemoViewer.NET/Modules/Playback2D/Scene2DHost.cs:47-48`, `:105-108`). It is bound to a concrete `Playback2DTabViewModel` (`:90`): `AttachVm` subscribes `FrameUpdated` (`:913-949`), `SyncFromViewModel` pulls the overlay toggles and `AnnotationSession` (`:959-975`), `CurrentSceneFrame` reads `_vm.CurrentFrame` (`:117`), the vision solver reads `_vm.VisionEngine` (`:348`), and a level rebuild calls `_vm.ApplyAnnotationLevelRebuild` (`:1125-1165`). The strat canvas needs this control and has no `Playback2DTabViewModel`.
- `SceneHostToolServices` is `IToolServices` over the host: `CurrentTick` is the shown frame's tick, `NowMilliseconds` a `Stopwatch` (`Modules/Playback2D/Annotations/SceneHostToolServices.cs:23-198`). `AnnotationSessionController` owns the session, the sidecar and the settings seed; its browser branch says "session only" in so many words (`Modules/Playback2D/Annotations/AnnotationSessionController.cs:777-780`).
- `Playback2DKeymap` is a declarative table conflict-checked in its static constructor against `ShellReservedGestures`; `Playback2DAction` declares every action (`Modules/Playback2D/Playback2DKeymap.cs:14-39`, `:73-91`, `BuildDefault` `:307-359`). `BrowserReservedGestures` refuses rebinds the browser eats (`:388-401`). `Playback2DKeymapProfile.FromOverrides` composes user rows over it and never throws (`Playback2DKeymapProfile.cs:96-169`). The view resolves a key through `vm.Keymap.TryResolve(e, toolActive, …)` and hands `HoldPan` and `CancelGesture` to the surface, everything else to `vm.ExecuteAction` (`Views/Playback2D/Playback2DView.axaml.cs:206-269`).
- `Playback2DTimelineViewModel` registers `ITimelineTrack`s, rebuilds on `ITimelineData`, and raises `SeekRequested` rather than moving any clock itself (`Modules/Playback2D/Timeline/Playback2DTimelineViewModel.cs:135-178`).
- `Playback2DExportHost` is the record the shell hands the tab for export: frames, the `HeavyJobGate`, busy predicates, settings, the status chip mount (`Modules/Playback2D/Playback2DExportHost.cs:54-62`). `playback2d.export` is desktop-only through `ShellModuleFeatureGate.DesktopOnlyIds` (`Features/ShellModuleFeatureGate.cs:12`, `FeatureCatalog.cs:164-169`).
- `ArchitectureTests` asserts Core references only SkiaSharp and the BCL, and that neither Core, Pipeline nor Modules.Abstractions reaches Avalonia transitively (`src/Playback2D/DemoViewer.NET.Playback2D.Tests/ArchitectureTests.cs:21-67`).

### 2.4 Goldens and fixtures

`SceneGoldenTests` renders a `SceneFixture` through `SceneLayerCatalog.CreateSceneStack` plus `HeadlessSceneRenderer` with the camera pinned, compares perceptually, and rewrites only under `PB2D_GOLDEN_UPDATE=1`; the same PNGs are what `dv2d golden verify` reads (`Playback2D.Tests/SceneGoldenTests.cs:40-101`, `:190-212`). `SceneFixture` carries an opaque `Annotations` element (`Pipeline/SceneFixture.cs:47`); the corpus picks up `annotations/<name>.dvann.json` by convention when the entry names `playback2d.annotations` in `layers` (`tests/fixtures/playback2d/README.md:112-116`, manifest entry `annotated-mirage-b`, `manifest.json:118-144`). A `pending` entry is skipped, never failed (`Pipeline/Goldens/GoldenCorpus.cs:40-44`).

### 2.5 What the Strat Model provides

Every step is `{ id, atSeconds, actor, verb, from, to, utility, note, positions[], strokes[], holdSeconds, interpolation }` (Strat Model §3.3), the strat's `clock` block is `{ kind: round, roundSeconds }` (§3.4), `StratClock` maps `atSeconds` to a demo round's frame-clock tick and back (§3.4), slots are `A..E` (§3.5), `StratSession` owns a `PatchOp` undo stack with a commit rule and autosave (§3.12), and the Strat Book tab hosts "the Step Authoring canvas" (§3.11). Strat Model §3.9 reserves the shapes this document gives meaning to and expects a `StratFrameSource : ISceneFrameSource`.

---

## 3. Proposed design

### 3.1 What goes where

| Project | Adds | Why there |
|---|---|---|
| **Playback2D.Core** (`Keyframes/`, `Input/`) | `TokenKeyframe`, `TokenTrack`, `TokenTrackSet`, `TokenInterpolation`, `StepWindow`, `StepSchedule`; `ITokenEditor`; `TokenTool : IPointerTool`; `ToolKind.Token`; `IToolServices.Tokens`; `InputToolRouter.IsDrawingToolActive` widened; the Shape Tools kinds (§3.2) | Pure sampling and gesture code with no demo, no file and no Avalonia. `ArchitectureTests` stays green because nothing here references anything new. |
| **Playback2D.Pipeline** (`Frames/`, `Hud/`) | `StratFrameSource : ISceneFrameSource`, `StratSceneSpec`, `StratHudDataSource : IHudDataSource` | The synthetic frame source sits beside `FixtureFrameSource` and `TrackerFrameSource`, so an export and a golden use the same source. It takes Core types only; it never sees a `.dvstrat.json`. |
| **App** (`Modules/StratBook/Canvas/`, `Services/Strats/`) | `ISceneFrameHost` seam and the `Scene2DHost` rebind; `StratCanvasViewModel`, `StratSceneProjection`, `StepAuthoringPatches`, `StratTransport`, `StratTimelineData`, `StepTrack`, `StratExportJob`, `StratCaptureHost`, `CreateStratFromRound`; keymap rows; the canvas views | Everything that reads a strat document, a demo, a setting or a dispatcher. |

Core stays Avalonia-free; Pipeline stays App-free; the `dv2d` architecture test (`ArchitectureTests.cs:43-67`) is the gate, and §7 adds no exception to it.

### 3.2 Shape Tools: the prerequisite, and exactly what it ships

Step Authoring needs Arrow and Text as real tools. Shape Tools is its own build item (plan §3, "no review"); this section is the contract Step Authoring builds on, so the two cannot drift.

| Layer | Change | Detail |
|---|---|---|
| Core `Input/ToolKind` | add `Line, Arrow, Rect, Ellipse, Text` | Persisted by name in `LastTool`; `Enum.TryParse` already round-trips new names (`AnnotationSessionController.cs:372-374`). |
| Core `Input/ShapeTool` | one class, constructed per kind | Press anchors the first point, moves rubber-band the second through the session's wet stroke (two samples, `WetStroke.Begin` then a single replaced sample), release commits an element whose `Points` is exactly `[first, last]`. Space and envelope resolve exactly as `DrawTool.ResolveSpace` and `EnvelopeForNewElement` do (`DrawTool.cs:44-57`, `:175-188`). Shift constrains a line or arrow to 45° steps and a rect or ellipse to a square. A tap (no movement) commits nothing. |
| Core `Input/TextTool` | press places the anchor | Commits an element with `Kind = Text`, one point, `Text = ""`, then calls a new `IToolServices.RequestTextEdit(Guid elementId)`; the host edits the text and applies `DocDelta.Replace`; an empty result removes the element in the same gesture. Core never sees a text box. |
| Core `Layers/AnnotationLayer.BuildPath` | per-kind geometry | Freehand keeps the outline. Line: a stroked segment, width `WidthWorld`, round caps. Arrow: the segment plus a filled triangular head of length `3 × WidthWorld` (clamped to half the segment) at the last point. Rect and Ellipse: stroked, corners from the two points. Text: `TextBlobCache` blob at the first point, size `6 × WidthWorld` world units, drawn under the same world matrix so it zooms with the map like ink. The dry and prepared paths both go through `BuildPath`, so every kind is cached when Static and animated when anchored, with no second code path. `RevealCount` and `TimingOf` stay Freehand-only (`AnnotationLayer.cs:478-503`, `:640-641`). |
| Core `Annotations/AnnotationHitTester` | per-kind branch instead of throw | Line and Arrow: segment distance (already there for the polyline). Rect: distance to the four edges. Ellipse: `abs(normalized radius − 1)` against the slop. Text: the blob's bounds inflated by the eraser radius; the bounds come from the same `TextBlobCache` measurement the layer draws with. |
| Pipeline `AnnotationStore.ToElement` | stop forcing Freehand | Keep the `Enum.IsDefined` fence (`AnnotationStore.cs:568-573`); accept the five kinds. The DTO does not change: `points` already carries two triples for a shape and one for text, `text` is already a field (`annotations-format.md`, "An element"). |
| App | toolbar buttons, keymap rows (§3.7), the inline text editor over the surface, `IToolServices.RequestTextEdit` on `SceneHostToolServices` | The text editor is an Avalonia `TextBox` positioned at `WorldToScreen(anchor)` and committed on Enter, cancelled on Esc. |
| Tests | `ShapeToolTests`, `TextToolTests` (over `FakeToolServices`), `AnnotationHitTestTests` per kind, `AnnotationSchemaSnapshotTests` sample gains one element per kind, a new golden `annotated-shapes-mirage-b` in the corpus with all six kinds, `dv2d render --ink` over it | Done means: all six kinds round-trip through `.dvann.json` and render in export, the plan's own criterion. |

Step Authoring reads Shape Tools' kinds and tools exactly as they land; nothing below re-implements a shape.

### 3.3 The token-position keyframe track

Core types, in `DemoViewer.NET.Playback2D.Core.Keyframes`:

```csharp
public readonly record struct TokenKeyframe(int Tick, float X, float Y, double LevelMinZ, float YawDegrees);

public enum TokenInterpolation { Linear, Hold }

/// One slot's position over the strat clock: keyframes sorted by tick, one interpolation per segment.
public sealed class TokenTrack
{
    public string Slot { get; }                                   // "A".."E", "O1".."O5"
    public IReadOnlyList<TokenKeyframe> Keyframes { get; }        // sorted, distinct ticks
    public IReadOnlyList<int> HoldTicks { get; }                  // per keyframe: how long it holds before moving
    public IReadOnlyList<TokenInterpolation> Segments { get; }    // per keyframe: motion toward the NEXT keyframe
    public bool TrySample(int tick, out TokenKeyframe sample);    // pure; false before the first keyframe
}

public sealed class TokenTrackSet
{
    public IReadOnlyList<TokenTrack> Tracks { get; }
    public int Version { get; }
    public void Sample(int tick, List<TokenSample> into);         // allocation-free fill, one entry per track that has a sample
}

public readonly record struct StepWindow(Guid StepId, int FromTick, int? UntilTick);   // UntilTick inclusive, null = open
public sealed class StepSchedule { IReadOnlyList<StepWindow> Windows; StepWindow? At(int tick); int LastTick; }
```

**The strat frame clock.** Tick 0 is the round start (freeze-end); the rate is a constant `StratClock.TicksPerSecond = 64`, the same fallback `AnnotationSession.DefaultTicksPerSecond` uses (`AnnotationSession.cs:325`). A step at `atSeconds` sits at `tick = round((roundSeconds − atSeconds) × 64)`; a negative `atSeconds` (after the timer expired, Strat Model §3.4) is a tick past `roundSeconds × 64` and is legal. This clock never meets a demo clock; `StratClock.tick(step, round, tickRate, roundSeconds)` (Strat Model §3.4) is the only bridge, and it works in seconds. Two steps sharing a time share a tick; the schedule keeps authoring order and gives the earlier one a zero-length window.

**Sampling rule** (`TokenTrack.TrySample`), for a tick `t` between keyframes `k` (tick `a`, hold `h`) and `k+1` (tick `b`):

| Case | Result |
|---|---|
| `t < first keyframe tick` | no sample: the token is not drawn (a slot with no keyframe at all is never drawn) |
| `t ≥ last keyframe tick` | the last keyframe, held forever |
| `a ≤ t < a + h`, or `Segments[k] == Hold` | keyframe `k` (stationary) |
| `a + h ≤ t < b`, `Linear` | `u = (t − a − h) / (b − a − h)`; `X, Y` lerped; yaw lerped along the shortest arc; `LevelMinZ` from `k` while `u < 0.5`, from `k+1` after |
| `h ≥ b − a` | degenerate hold: the token stays at `k` and jumps at `b` |

Linear in world space per slot, as the brief asks. It is pure in `t`, so scrubbing backwards equals scrubbing forwards and an export at 20 fps agrees with one at 50 fps, the same property `TimeEnvelope.OpacityAt` and `StrokeTiming.RevealedCount` were built for (`AnnotationElement.cs:146-182`). The Z the marker carries is `LevelMinZ + MapSpace.LevelQuantum / 2` (`Core/Levels/MapSpace.cs:35`), the middle of the quantum, so the pane's Z-band lookup lands on the intended floor rather than on its boundary. A level switch at the segment midpoint is a snap, and `MarkerSmoother` snaps with it through `LevelCrossings` (`MarkerSmoother.cs:120-131`), so no dot streaks across the wrong floor's radar. An author who wants the token to walk the stairs adds a step at the stairs; `path[]` waypoints stay reserved (Strat Model §3.9) and are the later fix.

**A slot that does not move between steps.** `step.positions[]` carries an entry only for slots the author placed at that step. Strat Model §3.9 says an absent entry "inherits the previous step's", and this design keeps that reading literally: the token is **stationary** through every step that has no entry for it, and moves only during the segment that ends at its next explicit entry. Adding a position for slot B at step 4 therefore changes B's motion between steps 3 and 4 and nothing else; it never re-times a move the author already saw. Motion that should start earlier is authored by dragging the token part-way at the earlier step. The track builder (`StratSceneProjection.BuildTracks`, App) walks the steps in order, appends a keyframe per explicit entry, and sets `HoldTicks[k] = holdSeconds × 64` and `Segments[k]` from the step that owns keyframe `k` (`interpolation: "linear" | "hold"`, null meaning linear). A step whose slot entry is present but unchanged is still a keyframe: it pins the token so a later edit of the previous step cannot move it.

**Ten tokens.** Slots `A..E` are the strat's own side (Strat Model §3.5). The other five are **opponent tokens** `O1..O5`, optional, labelled `1`..`5`, drawn with the other team's colour, carrying no identity and no role. They exist because a setup is meaningless without where the other side is expected, and because Create Strat From Round has their positions for free (§3.9). They are an additive extension of the reserved `positions[].slot` vocabulary; the Strat Model validator's slot rule (§3.10) has to admit them (§6, and `openQuestions`). The `canvas.showOpponents` flag (§3.5) hides them without deleting them.

### 3.4 Branches: authoring and playing

A branch is a first-class object (Strat Model §3.3.4): `afterStepId`, a condition, and a target `{ stratId, stepId }` that is either a later step of this strat or another strat. Step Authoring does not add a second step list per branch. It defines a **path**:

- `StratPath.MainLine(doc)` is the step list in authoring order.
- `StratPath.Through(doc, branchId)` is the steps up to and including `afterStepId`, then the target: for an in-document target, the steps from `stepId` onward in authoring order (the steps between are skipped); for another strat, that strat's steps from its `stepId` (or its first step), with the branch strat's `atSeconds` read as-is. Token tracks are built over the chosen path: a slot with no entry in the branch's first step inherits its position at the branch point, which is the stationary rule from §3.3 applied across the join.
- The canvas shows a fork glyph on the step track at every step that has a branch; clicking it switches the canvas to that path (a **view** state, not a document edit, so it takes no undo slot). Authoring a branch's positions and strokes means: switch to the path, scrub past the fork, add steps and drag tokens; the edits land on the branch target's steps, which may live in another strat document. The Strat Model's single-writer rule (§3.11) applies per document: the canvas checks the target strat out through its own `StratSession` while the branch path is active.
- Export offers the path choice (§3.6).

### 3.5 Per-step strokes on the annotation document

A step's `strokes[]` are annotation elements in the `.dvann.json` element shape minus `fromTick`, `untilTick`, `fadeInTicks`, `fadeOutTicks` and `timing`, restricted to `space: "world"` (Strat Model §3.9). The canvas does not draw them from the strat document. It **projects** the whole strat into one `AnnotationDocument` (`StratSceneProjection.BuildDocument`):

- each stroke of step `s` becomes an `AnnotationElement` with its stored `Id`, kind, style, points and text, `Space = World(levelMinZ)`, and `Time = new TimeEnvelope(window.FromTick, window.UntilTick, canvas.fadeInTicks, canvas.fadeOutTicks)`, where `window` is the step's `StepWindow` in the schedule: `FromTick` is the step's tick and `UntilTick` is the next step's tick minus one (the last step's `UntilTick` is null, "until the end", as `TimeEnvelope` already spells it). The fades sit outside the window, which is exactly the trapezoid the layer draws (`AnnotationElement.cs:249-262`).
- `AnnotationLayer` then renders per-step strokes with no change: they are time-anchored and world-anchored, so they take the prepared path (`AnnotationLayer.cs:283-355`); `AnnotationTrack` puts one marker per stroke on the scrubber for free.
- The projected document's clock header, for anything that serializes it (the golden captures in §7), is `ClockIdentity("dv-strat-clock", 64, lastTick + 1, 0, lastTick)`. The strat file itself carries only `clock { kind: round, roundSeconds }` (Strat Model §3.4), which is the header F15 asks every store to declare; strokes carry no tick because the tick is derived from `atSeconds`, so re-timing a step re-times its strokes with no rewrite.

Authoring uses the existing tools unchanged. When the active step changes, the canvas view-model sets `Session.DefaultVisibility = EnvelopeMode.Custom` and calls `Session.SetCustomWindow(window.FromTick, window.UntilTick ?? schedule.LastTick)` (`AnnotationSession.cs:470-472`), so `DrawTool`, `ShapeTool` and `TextTool` stamp every new element with the active step's window through `EnvelopeForNewElement` (`:482-510`). The element-to-step mapping on save is by `FromTick`: an element belongs to the step whose window opens at its `FromTick`. Dragging a stroke's marker on the timeline is not offered on the strat canvas; a stroke moves steps by cut and paste, which is a remove and an add and therefore two ordinary ops.

New root-level fields on the strat document, additive and defined here: `canvas { fadeInTicks: 8, fadeOutTicks: 16, showOpponents: true, defaultLevelMinZ: null }`. Absent means those defaults. The Strat Model preserves unknown fields at every level, so a strat written by this build opens in a build that predates it.

### 3.6 The synthetic frame source and Strat Export

`StratFrameSource : ISceneFrameSource` (Pipeline, `Frames/StratFrameSource.cs`) is built from a `StratSceneSpec`:

```csharp
public sealed record StratSceneSpec(
    TokenTrackSet Tracks, StepSchedule Schedule, AnnotationSession Ink,   // Ink is a private session over a Reset copy, never the live document
    IReadOnlyList<TokenLabel> Labels,                                     // (slot, label, team) for the marker layer
    string MapName, IReadOnlyList<MapRadarImage> Radars, WorldBounds MapBounds, IReadOnlyList<double>? SectionHeights,
    IReadOnlyList<UtilityCue> Utility,                                    // (tick, kind, x, y, z) from steps that carry a landing point
    int RoundSeconds, int StartTick, int EndTick, int Fps, double Speed);
```

- `FrameCount = 1 + floor((EndTick − StartTick) / (Speed × 64 / Fps))`, `TrackerFrameSource.OutputFrameCount`'s arithmetic (`TrackerFrameSource.cs:282-300`) with the rate fixed at 64, exposed as a static so the export dialog sizes its request the way the CLI does.
- `TimeAt(i) = new SceneTime(StartTick + round(i × ticksPerOutputFrame), i, (tick − StartTick) / 64.0, Speed / Fps, i == 0)`.
- `FrameAt(i)` samples `Tracks` at the tick into two pooled marker lists (the `FrameSlot` shape of `SceneFrameBuilder`, `Pipeline/SceneFrameBuilder.cs:951-969`): one `PlayerMarker` per sampled track with `Team` from the label, `Ring = Team`, `RingAlpha = 1`, `IsAlive = true`, `SteamId = 0`, `Label` the slot's resolved initials or its letter. `AreaEffects` carries a `Smoke` disc for 18 s and `Fire` cells for 7 s after each utility cue of that kind (the nominal CS2 durations; a flash or HE draws nothing). `Trails` and `KillFeed` are empty, `Bomb` null, `Vision` off. `Map` is `MapName`, `NetworkedBounds = MapBounds` (the bundle's world rectangle, so `AutoFitOnFirstMapBounds` frames the map on frame 0, `HeadlessSceneRenderer.cs:243-247`), `SectionHeights`, `Radars`. `GameInfo` is `Phase "Live"`, `RoundSeconds = RoundSeconds − tick / 64.0`, `RoundTime` formatted `m:ss`, scores 0. No `IPreparableFrameSource`: there is nothing to warm up.
- `StratHudDataSource : IHudDataSource` answers `At(tick)` with the same round clock, so `hud.clock` burns the countdown in. Ink is registered by naming `playback2d.annotations` and passing `Ink` to `CreateSceneStack`, which is the catalog's own opt-in rule (`SceneLayerCatalog.cs:181-191`).

**Strat Export** (App, `StratExportJob`): builds the spec from the checked-out strat and the chosen path, snapshots the projected document into a fresh `AnnotationDocument` through `Reset` (the catalog's "never the live document" rule, `SceneLayerCatalog.cs:124-130`), and runs `SceneExportSession.RunAsync` with an `ExportRequest` whose `LayerIds` names the seven scene layers, `playback2d.annotations` and `hud.clock` explicitly, `Camera = CameraScript.Fixed(fit to MapBounds)`, `Size` from the presets, `Speed` and `Fps` from the dialog. Sinks, encoder selection, the ffmpeg ladder, the `HeavyJobGate`, the status chip and the output path all reuse the export job's existing pieces; the tab receives them through a `StratExportHost` record shaped like `Playback2DExportHost` minus `Frames` (`Playback2DExportHost.cs:54-62`). Default: GIF, 20 fps, 640 wide, speed 1, range from the first step's tick to `LastTick + 2 s`. At 20 fps the 1,800-frame cap (`SceneExportSession.cs:40`) is 90 s of strat time; a 1:55 to 0:55 execute is 1,201 frames and fits; a whole 115 s round does not, and the dialog offers 10 fps (1,151 frames) or speed 1.5 before the validator refuses, using the same `Validate` the dialog and CLI share (`:324-383`). WebM and MP4 have no cap. `SceneExportSession` needs no change.

Determinism is the source's: `FrameAt` is a pure function of the tick and `IsDiscontinuity` is true only at frame 0, so two runs hash identically (test in §7).

### 3.7 Authoring tools and keybinds

Two new Core tools and the Shape Tools set, all routed by `InputToolRouter` exactly as Draw and Erase are, so hold-Space, middle-drag and Ctrl-drag still pan, Esc still cancels, and the right button still takes `SecondaryTool` (`InputToolRouter.cs:61`, `:125-159`).

**`TokenTool : IPointerTool`** (`ToolKind.Token`). Press: `s.Tokens?.TryHitToken(pane, world, radius, out slot, out grip)`; no hit or a null editor returns false so the press falls through unhandled. Hit: `Tokens.BeginDrag(slot)` and `RequestRender`. Move: `Tokens.MoveTo(slot, world, MapSpace.QuantizeZ(pane.Level.ZMin))`; the level comes from the pane the pointer is in, which is how a token is dragged onto another floor on a stacked map. Release: `Tokens.EndDrag()`. Cancel: `Tokens.CancelDrag()`. Shift held on release snaps yaw to the drag direction; a plain drag keeps the keyframe's yaw. `ITokenEditor` is a Core interface with those five members plus `int ActiveTick`; `IToolServices` gains `ITokenEditor? Tokens { get; }`, null on the 2D Playback tab (`SceneHostToolServices` returns the host's editor, which the 2D tab leaves null; `FakeToolServices` gets a settable one). The editor is implemented by `StratCanvasViewModel`: `BeginDrag` opens a strat gesture, `MoveTo` writes the keyframe for the active step (creating the entry if the step had none for that slot) and republishes the frame, `EndDrag` closes the gesture as one op (§3.8). The tool never sees a strat.

**Yaw grip.** With the token tool, a drag that starts on the marker's heading stub (the segment `MarkerLayer` draws at `radius + MarkerHeadingLength`, `MarkerLayer.cs:146-154`) rotates instead of moving: `TryHitToken` reports `grip == TokenGrip.Heading`. One tool, two grips.

`InputToolRouter.IsDrawingToolActive` becomes "any kind but PanZoom" (`InputToolRouter.cs:86`), so the keymap's tool scope shadows Space and Esc under every authoring tool.

**Keymap rows**, added to `Playback2DKeymap.BuildDefault` (`Playback2DKeymap.cs:307-359`) and to `Playback2DAction`. The table is shared by both tabs, which is what the brief asks; the strat canvas routes it through the same `Playback2DKeymapProfile` so a user's overrides apply once. Each row was checked against the shipped `Always` gestures (Space, arrows, Q, E, Shift+Q, Shift+E, F, Shift+F, Esc, D, X, Ctrl+Z, Ctrl+Shift+Z, Ctrl+X, Home), the shell list (`:363-379`) and the browser list (`:388-401`); none collides, and the static constructor would throw if one did.

| Action | Gesture | Scope | Meaning | On the 2D Playback tab |
|---|---|---|---|---|
| `ToolToken` | `V` | Always | Token tool (press again for pan) | unhandled (no token editor) |
| `ToolArrow` | `A` | Always | Arrow tool | works (Shape Tools) |
| `ToolText` | `T` | Always | Text tool | works |
| `ToolLine` | `L` | Always | Line tool | works |
| `ToolRect` | `R` | Always | Rect tool | works |
| `ToolEllipse` | `O` | Always | Ellipse tool | works |
| `AddStep` | `N` | Always | Insert a step after the active one at the playhead's round-clock time | unhandled |
| `DuplicateStep` | `Ctrl+D` | Always | Copy the active step's positions and strokes into a new step 5 s later | unhandled |
| `DeleteStep` | `Ctrl+Delete` | Always | Delete the active step | unhandled |
| `PrevStep` / `NextStep` | `[` / `]` (`OemOpenBrackets` / `OemCloseBrackets`) | Always | Seek to the previous / next step's tick and make it active | unhandled |

`D` (draw), `X` (erase), `Ctrl+Z`, `Ctrl+Shift+Z`, `Ctrl+X`, Space, arrows, `Home`, hold-Space and Esc keep their rows and their meaning on the strat canvas; `Ctrl+X` clears the active step's strokes rather than the whole strat, and `Ctrl+Z` routes to the strat session (§3.8). Round, kill and follow actions resolve and are ignored by the strat executor, the "leave it unhandled" rule the view already applies to reserved rows. `docs/playback2d-v2/design.md` §7.5's table and the Settings keybind list grow the same rows, and `Playback2DKeybindConflictTests` needs no change.

### 3.8 Undo and redo

Two mechanisms, one stack:

1. **Inside a gesture**, the annotation tools keep their contract: `BeginGesture`, `Apply(DocDelta)`, `BailToMark` on Esc (`AnnotationDocument.cs:87-151`). A token drag is the same shape on the editor side: `BeginDrag` opens a strat gesture, every `MoveTo` replaces the keyframe, `CancelDrag` rolls back to the mark.
2. **Across gestures**, the Strat Model's `StratSession` is the only undo stack the user sees (§3.12). When a gesture closes, `StepAuthoringPatches` translates it into `PatchOp`s against the strat document with the `from` value filled in: a committed stroke is `add /steps/{i}/strokes/-`; an erase is one `remove /steps/{i}/strokes/{k}` per element, with `from` the element; a token drag is one `replace /steps/{i}/positions/{j}` (or `add` when the slot had no entry) whose `from` is the pre-drag value, forty `MoveTo`s collapsed into one op exactly as §3.8 of the Strat Model wants a drag to reach the history log; step add, duplicate, delete and re-time are ops on `/steps`. `StratSession.Apply` pushes each as one undo entry and the autosave and commit rules already defined there take over.
3. **Undo and redo** pop the op from `StratSession`, apply the inverse to the strat document, and push the change into the projections **as a migration**: `AnnotationDocument.ApplyMigration(DocDelta)` (`AnnotationDocument.cs:162-170`) for a stroke, `TokenTrackSet.Replace(slot, keyframes)` for a position. `ApplyMigration` bumps `Version` and raises `Changed` without touching the Core undo stacks, which the strat canvas never calls; there is one history and it is the one the diff log is written from. `Ctrl+Z` on the strat canvas therefore undoes a stroke, a drag or a step edit in the order they happened, and a redo after a save is still a redo, because the session owns both.

The annotation document's own 200-entry stack still exists and still refuses to undo mid-gesture; it is never driven on this canvas. Reusing `DocDelta` inside a gesture is what lets Shape Tools' tools work on both tabs with no knowledge of which one they are on.

### 3.9 Create Strat From Round: pre-population

Right-click in 2D Playback on a round: "Create strat from this round". The build item is Create Strat From Round; this section defines what it writes into the keyframe model and what that costs.

**Inputs.** The parsed demo (the tab gets a `StratCaptureHost` record with `Func<ParsedDemo?>`, shaped like `Playback2DExportHost`), the round from `ResolveRoundWindow` (Strat Model §2.3), our side from Team Identity's `GetAssignment(demoPath).OurSide` or a picker when unknown, the demo-to-slot map from Strat Model §3.5, and the map's `MapSpace` (the host's `Levels`, `Scene2DHost.cs:195`) to quantize a pawn's Z to a level.

**Keyframes.** Steps are generated at, in tick order:

| Trigger | Step | Positions written |
|---|---|---|
| freeze-end | `atSeconds = roundSeconds`, verb `hold`, actor `all` | every live pawn on both sides (`A..E` and `O1..O5`) |
| each of our side's `smokegrenade_detonate`, `flashbang_detonate`, `hegrenade_detonate`, `inferno_startburn`, `decoy_started` | verb `throw`, `utility.kind` from the event, `utility.landing { place?, x, y, levelMinZ }` from the event's `x/y/z`, actor the thrower's slot from `Userid` | the thrower, plus any slot that moved more than 200 units since its last keyframe |
| `bomb_planted` | verb `plant` | the planter |
| every 10 s with no other step inside 3 s | verb `move` | every slot that moved more than 200 units since its last keyframe; a slot that did not move gets no entry, which is the stationary rule from §3.3 |

Positions come from the pawn's cell-reconstructed world position, `yawDegrees` from `m_angEyeAngles.Y`, `levelMinZ` from `MapSpace.LevelFor(z).ZMin` quantized, `from`/`to` places from `m_szLastPlaceName`, all read with a private `EntityTracker` walked once from frame 0 to the round's last sample frame off the UI thread; nothing touches the shared playback clock, the rule every export already keeps (design §5.7). `atSeconds` is `StratClock.atSeconds(tick, round, tickRate, roundSeconds)`. One generated arrow stroke per `throw` step from the thrower to the landing point is on by default ("draw throw arrows"), the one place Create Strat From Round writes ink. `origin` is filled (Strat Model §3.3). The user reviews the slot map and the step list before anything is saved.

**Measured** (scratch project `step-authoring`, Release, .NET 10, CS2DemoKit.Parser and CS2DemoKit.Analysis 0.12.0, 2026-09-23):

| Demo (Steam `replays`, build 10896, `GotvMatchmaking`) | Round | Length | Detonations with `x/y/z` | `inferno_startburn` without `Userid` | Tracker walk to the round's last sample frame | `PositionSampler.Walk` (stride 1) to the same frame | Parse |
|---|---|---|---|---|---|---|---|
| `match730_003842233788306292960_0260929275_408.dem`, de_mirage | 7 | 101.6 s | 16 of 16 | 2 | 623 ms (frame 34,155) | 831 ms | 557 ms |
| `match730_003842182368957825245_0056633905_389.dem`, de_nuke | 5 | 72.1 s | 15 of 15 | 3 | 520 ms (frame 26,842) | 699 ms | 588 ms |

Every detonation event carries world coordinates, so a landing point never needs the projectile entity; `inferno_startburn` carries no thrower, so a molotov step's actor is left for the user until the Grenade Walk supplies it. Yaw and place were present on all 130 sampled pawn rows across both rounds. The walk is O(frames before the round), because entity state is delta-encoded (the package's own note on `PositionSampler.Walk`); a late round in a 45-minute match costs a few seconds and gets a progress line.

**One finding that shapes the implementation.** On the de_nuke round, 3 of the 10 `CCSPlayerPawn` entities the tracker held at freeze-end were **orphans**: `m_lifeState != 0`, stale mid-map positions (Rafters, Squeaky, Lobby), and no controller pointing at them. `PositionSampler.Walk` did not emit them, because it enumerates through the controller's live pawn. Create Strat From Round must do the same: read positions through `PositionSampler` or through `IPlayerState.HasLivePawn` as `SceneFrameBuilder.BuildMarkers` does (`SceneFrameBuilder.cs:240-253`), never by enumerating the pawn class. The probe's raw enumeration is what found this; its output is `step-authoring/out_nuke_r5.txt`.

### 3.10 The canvas host, the timeline and the browser

**Host seam.** `Scene2DHost` binds a concrete `Playback2DTabViewModel` (`Scene2DHost.cs:90`, `:913-975`). This design introduces `ISceneFrameHost` (App-internal): `Scene2DFrame CurrentFrame`, `event Action? FrameUpdated`, `AnnotationSession? AnnotationSession`, `VisibilityEngine? VisionEngine`, the five overlay toggles, `void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double,double>)`, `ITokenEditor? TokenEditor`. `Playback2DTabViewModel` implements it with its existing members (`Playback2DTabViewModel.cs:478`, `:1117`, `:1156`, `:381`); `Scene2DHost.AttachVm` takes the interface; `SceneHostToolServices.Tokens` returns the host's `TokenEditor`. No behaviour changes on the 2D tab; the existing App-suite tests over the host are the regression gate.

**`StratCanvasViewModel : ISceneFrameHost, ITokenEditor`** owns the projection (`TokenTrackSet`, `StepSchedule`, `AnnotationSession` over the projected document), the transport, the active step and the path. It publishes a `Scene2DFrame` from a pooled marker list on every tick change and raises `FrameUpdated`; `Map` comes from the same `MapAssetLoader` bundle the 2D tab binds. The Strat Book tab mounts `Scene2DHost` beside the step table (Strat Model §3.11) and reuses `AnnotationsPanelViewModel` for the tool row and ink pickers (`ViewModels/Playback2D/AnnotationsPanelViewModel.cs:199`), which takes an `AnnotationSessionController` built with a null store: session-only by construction, since the strat file is the persistence.

**Transport and scrubber.** `StratTransport` is a private clock: a `DispatcherTimer` at the display rate advancing the tick by `speed × 64 × dt`, with play/pause, step, speed, seek. It is not `IModuleContext.RequestSeekToFrame`: a strat has no demo and must not move LiveSync. The scrubber is a second `Playback2DTimelineViewModel` instance over `StratTimelineData : ITimelineData` (`TotalFrames = LastTick + 1`, `TickRate = 64`, `FrameIndexAtTick(t) = t`, no events) with two tracks: `StepTrack` (a marker per step, kind `Custom`, glyph the step number, tooltip the call-sheet line from `StratTextExporter`, a fork glyph where a branch hangs; bands are the step windows) and the existing `AnnotationTrack` over the projected document. `SeekRequested` drives the transport.

**Browser host** (`docs/playback2d-v2/wasm-matrix.md` rows to add):

| Capability | Browser | Mechanism |
|---|---|---|
| Strat Book canvas: tokens, steps, strokes, scrub, undo | works, session only | Same Skia lease as the 2D tab; the strat store is in-memory there (Strat Model §3.2) and the tab's status line says so. |
| Strat Export | absent by design | `stratbook.export` joins `ShellModuleFeatureGate.DesktopOnlyIds` beside `playback2d.export` (`Features/ShellModuleFeatureGate.cs:12`); the button slot says "unavailable in the browser", the wording the export slot uses. |
| Create Strat From Round | works, session only | The parsed demo is in memory; the result lives as long as the tab. |

### 3.11 Field semantics summary (the contract on `.dvstrat.json`)

| Field | Defined here as |
|---|---|
| `step.positions[] { slot, x, y, levelMinZ, yawDegrees? }` | The token's keyframe at the step's tick. `slot` is `A..E` or `O1..O5`. `levelMinZ` is `MapSpace.QuantizeZ(level.ZMin)`. `yawDegrees` absent means "keep the previous keyframe's yaw", 0 when there is none. |
| `step.strokes[]` | `.dvann.json` elements minus the time fields and `timing`, `space` must be `world`; visible over the step's window with the strat's `canvas` fades. |
| `step.holdSeconds` | Seconds × 64 of hold before motion toward the next keyframe begins; null is 0. |
| `step.interpolation` | `"linear"` (default, null) or `"hold"` (the token jumps at the next step). `"path"` stays reserved and is refused by the validator as unknown for now. |
| `step.utility.landing.x / y / levelMinZ` | Additive to Strat Model §3.3.3's `landing { place }`: the point the smoke or fire is drawn at during playback and export. Absent draws nothing. |
| root `canvas { fadeInTicks, fadeOutTicks, showOpponents, defaultLevelMinZ }` | Additive root block; defaults 8, 16, true, null. |

---

## 4. Alternatives considered and why not

| Alternative | Why not |
|---|---|
| **Token positions as annotation elements** (a new `AnnotationKind.Token` with one point per step). | An element has one envelope; a token needs a value per step and interpolation between them. It would also put strat state into `.dvann.json`, which is per demo, and it abuses `Points` as a time series. The keyframe track is fifty lines and models the thing. |
| **Interpolate across steps that have no entry** for a slot (a move spanning steps 1 to 4 when only 1 and 4 carry an entry). | Adding a position at step 4 would silently re-time the token through steps 2 and 3, which the author may already have reviewed. The stationary rule keeps every edit local to one segment, and it is the reading Strat Model §3.9 already fixed. |
| **A second `Playback2DKeymap` for the strat canvas.** | Two tables mean two override sets and two Settings lists for the same physical keys, and the conflict checker would not see a key claimed by both. One table, unhandled where an action has no meaning, is what the shipped view already does for reserved rows. |
| **A separate undo stack per document** (annotation stack, keyframe stack, session stack). | Ctrl+Z would have to guess which stack the user means, and the history log needs every edit as an op anyway. One stack, gestures translated to ops at close, is the shorter path and it is the Strat Model's. |
| **Drive the strat canvas through `IModuleContext` seeks** so the 2D tab's timeline is reused as-is. | Seeks reach LiveSync and every module; a strat has no demo. A private transport is what export already is. |
| **`StratFrameSource` in the App** next to `StratStore`. | Then no golden and no `dv2d` path could render a strat, and the source would be untestable without the App's container. Pipeline takes Core types; the App projects the document into them. |
| **Store ticks on strokes** (write `fromTick`/`untilTick` into `strokes[]`). | Re-timing a step would have to rewrite every stroke, and two spellings of one time is the drift the Strat Model avoided by deriving everything from `atSeconds`. |
| **Z lerped through the whole segment** on a level change. | The marker would spend half its move on a floor pane whose radar it is not on, and the hysteresis band would flicker it. The midpoint snap is deterministic and reads as "took the stairs"; waypoints are the real fix and stay reserved. |
| **A GIF encoder in the browser** (ImageSharp in memory) for Strat Export. | There is no file to write to and no download affordance in the shell today; the export gate is desktop-only for the same reason (design §8). Revisit with the WebCodecs sink. |

---

## 5. External and engine changes required

**CS2DemoKit:** none. `EntityTracker`, `PositionSampler.Walk`, `ClipRounds.Derive` and the game-event payloads in 0.12.0 supply everything §3.9 reads. Delta-encoded user commands (issue #53) are not involved: no input is read here.

**AssetBaker bundle schema:** none. The canvas reads `bundle.json` as the 2D tab does (radars, floors). When Zone Baking adds `zones`, `utility.landing.place` can be resolved from `landing.x/y` at capture time; that is a consumer, not a schema change.

**CSVG game plugin:** none.

**Interfaces this design needs from sibling designs** (not external projects):

| From | Needed |
|---|---|
| Strat Model | admit `O1..O5` in `positions[].slot`; `landing.x/y/levelMinZ` and the root `canvas` block as defined fields rather than unknown ones; `interpolation` vocabulary `linear` and `hold`; the `schema-v1.sample.dvstrat.json` fixture to carry positions and strokes on three steps; `StratSession.Apply` accepting ops produced outside the metadata editor. |
| Team Identity | `GetAssignment(demoPath).OurSide` for Create Strat From Round. |
| Round Facts | `roundTime` when present, else 115 (Strat Model §3.4). |
| Shape Tools (build) | §3.2, verbatim. |

---

## 6. Risks and unknowns

| Risk | Likelihood | Mitigation |
|---|---|---|
| The Strat Model validator refuses `O1..O5` or the `canvas` block before it is updated | Certain until reconciled | Listed in §5 and `openQuestions`; both are additive, and unknown fields survive a load in either direction. |
| Text in world units is illegible at 640 px wide GIFs (a 6 × 6 = 36-unit glyph on a 4,000-unit map is 5 px) | High for defaults | The text size follows the pen width the author chose; the export preview shows the frame at output size before encoding; a per-strat `canvas.textScale` is the fallback if the preview proves insufficient. |
| Two views of one truth: the projected `AnnotationDocument` and the strat's `strokes[]` diverge after a bug in `StepAuthoringPatches` | Medium | A round-trip test: project, apply every gesture kind, un-project, compare to the ops applied directly; the projection is rebuilt from the document on every undo, so a divergence is corrected rather than accumulated. |
| A branch into another strat edits that document while the Strat Book tab has it checked out elsewhere | Low | Single-writer rule per document; the canvas refuses to enter a branch path whose target is checked out and says why. |
| `IsDrawingToolActive` widening changes when Space and Esc shadow transport on the 2D tab | Certain, intended | Only while a non-pan tool is selected, which is the meaning the keymap already documents; `InputToolRouterTests` gains the new kinds. |
| Midpoint level snap looks wrong on a long cross-floor move | Medium on de_nuke, de_vertigo | Author adds a stairs step; `path[]` waypoints are the reserved fix. Decision 8. |
| The 1,800-frame GIF cap makes a whole-round strat need 10 fps or 1.5× speed | Certain | The dialog states it and offers both; WebM has no cap. |
| `Ctrl+D` on the browser head is Chrome's bookmark shortcut | Low | It reaches the page and is cancellable, so it is deliberately not in `BrowserReservedGestures`; a user can rebind it. |
| Raw pawn enumeration in Create Strat From Round picks up orphaned pawns | Certain if done naively | Measured (§3.9); the implementation goes through the controller's live pawn. |
| `Playback2DKeymapProfile` rows in a user's `settings.json` name only the old actions | None | Additive rows; `FromOverrides` ignores what is not overridden. |

Unknown: whether the corpus's perceptual tolerance holds for a frame that is mostly text and arrows (the glyph budget is per marker label, `dv2d.md` "Where the glyph allowance comes from"); the first strat golden measures it. Unknown until Shape Tools lands: whether an arrow head in world units needs a screen-space minimum to stay visible when zoomed out.

Where this document deviates from `strat-model.md`: the opponent slots, the `landing` coordinates, the `canvas` block, and `StratFrameSource` living in Pipeline rather than beside the store. All four are additive; none changes a rule the Strat Model fixed.

---

## 7. Test and verification strategy

Core and Pipeline tests are TUnit in `src/Playback2D/DemoViewer.NET.Playback2D.Tests/`; App tests in `src/App/DemoViewer.NET.App.Tests/`.

| Test | Pins |
|---|---|
| `TokenTrackTests` | the sampling table in §3.3 row by row: before-first, after-last, inside hold, linear midpoint (`u = 0.5` lands on the arithmetic mean), degenerate hold, `Hold` segment jump, yaw shortest arc across 350° to 10°, level switch at the midpoint and not before; forward and reverse scrubs agree at every tick |
| `StepScheduleTests` | windows from `atSeconds` (non-increasing), shared ticks give a zero-length window in authoring order, last window open-ended, negative `atSeconds` past `roundSeconds × 64` |
| `TokenToolTests` (over `FakeToolServices` with a fake `ITokenEditor`) | press with no editor returns false; press away from every token returns false; drag writes `MoveTo` with the pane's quantized level; release closes the gesture; Esc calls `CancelDrag`; heading grip rotates; middle-drag and hold-Space still pan |
| `InputToolRouterTests` | `IsDrawingToolActive` true for every kind but PanZoom |
| `StratFrameSourceTests` | `FrameCount` equals `TrackerFrameSource.OutputFrameCount`'s arithmetic at 64; `TimeAt(0).IsDiscontinuity` only; markers per frame equal sampled tracks; smoke disc present for 18 s after a cue and gone after; `HashingFrameSink` over two runs of the same spec gives identical hashes at 20 fps, and at 50 fps the 20 fps frames are a subset by tick |
| `StratExportValidationTests` | 1,201 frames at 20 fps accepted, 2,301 refused with the cap message, WebM uncapped |
| `ShapeTool`, `TextTool`, hit-test and schema tests | §3.2, owned by Shape Tools |
| `StratSceneProjectionTests` (App) | strat to document and back is identity for the sample fixture; every stroke's envelope equals its step window with the `canvas` fades; `FromTick` partition assigns strokes to the right step; unknown fields on a stroke survive |
| `StepAuthoringPatchesTests` (App) | a drag of forty moves is one `replace` with the pre-drag `from`; erase of three elements is three `remove` ops with `from`; undo applies `ApplyMigration` and consumes no Core undo entry (`UndoDepth` stays 0 on the annotation document) |
| `CreateStratFromRoundTests` (App, skipped without `DEMO_PATH`) | on the two build-10896 replays: 16 and 15 utility steps, every landing has coordinates, no orphaned pawn reaches a keyframe, slot map matches the fixture, `atSeconds` round-trips through `StratClock` |
| `Playback2DKeymapTests` | the new rows resolve; the static constructor still passes; `Playback2DKeybindConflictTests` unchanged |
| `Playback2DFeatureCatalogTests`, `ShellModuleFeatureGateTests` | `stratbook.export` is desktop-only |
| `SettingsWasmRoundTripTests` | unchanged: no new settings row |

**Golden images through `dv2d`.** The strat reader lives in the App, so `dv2d` cannot open a `.dvstrat.json`; it does not need to. An App-suite capture, `StratGoldenCaptureTests`, gated by `PB2D_GOLDEN_UPDATE=1` exactly like `Playback2DGoldenCaptureTests`, projects `tests/fixtures/strats/schema-v1.sample.dvstrat.json` (extended with positions on three steps, two arrows, one text, one smoke landing, one opponent token) through `StratSceneProjection` and `StratFrameSource`, and writes three corpus entries:

| Entry | Frame | Covers |
|---|---|---|
| `strat-mirage-exec-step1` | the first step's tick | tokens at keyframes, step-1 strokes at full opacity, opponents drawn, `hud.clock` reading `1:55` |
| `strat-mirage-exec-mid` | midway between steps 2 and 3 | linear interpolation, a held slot, step-2 strokes fading out and step-3 strokes not yet in, the smoke disc |
| `strat-mirage-exec-branch` | first tick of a branch path | the path switch and the inherited positions |

Each entry is `scenes/<name>.scene.json` (the `Scene2DFrame` the source produced, serialized by `SceneFixtureSerializer`) plus `annotations/<name>.dvann.json` (the projected document with the `dv-strat-clock` header), registered in `manifest.json` with `layers` naming the seven scene layers, `playback2d.annotations` and `hud.clock`, `map: de_mirage` with the bundle's `map_version`. That is precisely the shape of `annotated-mirage-b` (`manifest.json:118-144`), so `dv2d golden verify` renders and compares them with no CLI change, `SceneGoldenTests`-style direct tests read the same PNGs, and the two readers cannot disagree. A fourth entry, `strat-mirage-exec-mid@1280x720`, pins the export preset size. Entries are committed `pending: true` with a note naming this document until Shape Tools lands, then cleared.

Manual verification at build time: create a five-step de_mirage A execute by hand, drag ten tokens, draw an arrow and a label on each step, scrub it, add a branch to a step, export a GIF at 20 fps and drop it into Discord (the plan's Strat Export criterion), run Create Strat From Round on round 7 of the mirage replay and confirm it plays without manual re-entry, and check the browser host shows the session-only line and no export button.

---

## 8. Decisions

1. **Stationary rule for absent entries** (recommended, §3.3): a slot without an entry holds its previous position and moves only in the segment ending at its next explicit entry; or interpolate across the gap.
2. **Ten tokens**: opponent slots `O1..O5` as additive positions (recommended), or five tokens only in v1.
3. **One undo stack** in `StratSession` with gestures translated to ops at close (recommended, §3.8), or keep the annotation document's own stack for strokes and accept two Ctrl+Z meanings.
4. **Keymap sharing**: new rows in `Playback2DKeymap` shared by both tabs (recommended, the brief's ask), or a separate table for the Strat Book tab.
5. **Default export**: GIF, 20 fps, 640 wide, first step to last step plus 2 s (recommended), or WebM by default as the 2D tab does.
6. **`StratFrameSource` in Pipeline** (recommended, §3.1) so goldens and a later `dv2d strat` subcommand can use it, or in the App beside `StratStore`.
7. **Moving the strat JSON reader into Pipeline later** so `dv2d strat export <file>` exists headlessly: defer (recommended) or include in the Strat Export build item.
8. **Level switch on a cross-floor move**: midpoint snap (recommended, §3.3) or switch at the arriving keyframe; waypoints deferred either way.
9. **Text size**: `6 × WidthWorld` world units following the pen (recommended, §3.2), or a separate world size setting.
10. **Create Strat From Round cadence**: utility events plus plant plus a 10 s move sweep with a 200-unit threshold (recommended, §3.9), or utility events only.

---

## 9. Effort estimate and sequencing

| Step | Item | Estimate | Blocked by |
|---|---|---|---|
| 1 | Shape Tools (§3.2): kinds, tools, layer geometry, hit-testing, store, toolbar, keymap rows, text editor, tests, golden | 4 days | nothing |
| 2 | Core keyframes: `TokenTrack`, `TokenTrackSet`, `StepSchedule`, `ITokenEditor`, `TokenTool`, router widening, tests | 2 days | nothing (parallel with 1) |
| 3 | `ISceneFrameHost` seam and the `Scene2DHost` rebind, `SceneHostToolServices.Tokens` | 1 day | 2 |
| 4 | `StratFrameSource`, `StratSceneSpec`, `StratHudDataSource`, determinism and count tests | 1.5 days | 2 |
| 5 | App canvas: `StratCanvasViewModel`, `StratSceneProjection`, `StepAuthoringPatches`, `StratTransport`, `StratTimelineData`, `StepTrack`, branch paths, the views in the Strat Book tab, keymap executor | 4 days | 1, 3, Strat Model build (`StratSession`, store) |
| 6 | Strat Export: `StratExportJob`, `StratExportHost`, dialog, `stratbook.export` gate, chip reuse | 2 days | 4, 5 |
| 7 | Create Strat From Round: `StratCaptureHost`, the tracker walk, slot map review dialog, throw arrows | 2 days | 5, Team Identity build (a side picker suffices without it) |
| 8 | Goldens (`StratGoldenCaptureTests`, four corpus entries), `wasm-matrix.md` rows, `design.md` §7.5 table, `dv2d.md` note | 1.5 days | 5, 6 |

About eighteen working days after the Strat Model's eleven, of which steps 1 and 2 can start today. The plan's "done" for Step Authoring, a five-step strat that plays back and scrubs, is reached at step 5; Strat Export's "a GIF drops into Discord" at step 6; Create Strat From Round's "no manual re-entry" at step 7. Nothing touches CS2DemoKit, the baker or the CSVG plugin.

---

## 10. Sources

Repository, `main` at `d90ec9f`:

- `plan.md` §2 (F7, F10, F14, F15, F17), §3 (Shape Tools, Strat Model, Create Strat From Round, Step Authoring, Strat Export), §6 (D4, D7).
- `docs/strat-book/designs/strat-model.md` §2.3, §2.5, §3.3, §3.4, §3.5, §3.8, §3.9, §3.10, §3.11, §3.12, §8.
- `docs/playback2d-v2/design.md` §5.1, §5.4, §5.5, §5.6, §5.7, §7.1, §7.5, §7.7, §8; `annotations-format.md`; `export.md` (formats, GIF caps); `dv2d.md` (`golden verify`, `fixture capture`, the glyph allowance); `wasm-matrix.md`; `tests/fixtures/playback2d/README.md`; `tests/fixtures/playback2d/manifest.json:118-144`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Annotations/AnnotationElement.cs:14-33, 39-77, 106-186, 205-213, 219-247, 249-331, 351-409`; `AnnotationDocument.cs:39, 87-151, 162-170, 186-228, 260, 297-320, 450-475`; `DocDelta.cs:15-42`; `AnnotationSession.cs:20, 307, 325, 354-358, 370, 401-410, 416, 470-510`; `AnnotationHitTester.cs:46-55`.
- `Core/Input/IPointerTool.cs:11-22, 66-95, 115-138, 145-221`; `InputToolRouter.cs:61, 86, 95, 106-159, 204`; `DrawTool.cs:34-61, 102-152, 175-188`; `EraseTool.cs:89-145`.
- `Core/Layers/AnnotationLayer.cs:35, 100-103, 191-243, 283-355, 434-468, 478-503, 640-641`; `MarkerLayer.cs:69, 95-99, 136-186`; `SceneLayerIds.cs:7-68`; `Core/MarkerSmoother.cs:36, 105-173`.
- `Core/Timeline/AnnotationTrack.cs:24-134`; `ITimelineTrack.cs:7-41`; `ITimelineData.cs:67-86`.
- `Core/Export/ISceneFrameSource.cs:16-48`; `ExportRequest.cs:30-70`; `Core/Scene2DFrame.cs:24-109`; `PlayerMarker.cs:28-45`; `SceneTime.cs:19-24`; `SceneGameInfo.cs:26-44`; `SceneMapInfo.cs:12-40`; `MapRadarImage.cs:14-29`; `AreaEffect.cs`; `Core/Levels/MapSpace.cs:35, 81, 85`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Export/SceneExportSession.cs:39-53, 130-159, 229-264, 324-383, 453-504`; `Frames/FixtureFrameSource.cs:24-57`; `Frames/TrackerFrameSource.cs:30, 198-208, 216-267, 282-300`; `Headless/HeadlessSceneRenderer.cs:33, 128, 211-288`; `Headless/SceneLayerCatalog.cs:52-65, 124-130, 138-211`; `SceneFixture.cs:17-67`; `SceneFrameBuilder.cs:235-307, 951-969`; `Annotations/AnnotationDocumentDto.cs:19-134`; `Annotations/AnnotationStore.cs:464-529, 554-594`; `Goldens/GoldenCorpus.cs:40-44`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Tests/ArchitectureTests.cs:21-67`; `SceneGoldenTests.cs:40-101, 190-212`; `AnnotationSchemaSnapshotTests.cs:28-40`; `FakeToolServices.cs:18-166`.
- `src/App/DemoViewer.NET/Modules/Playback2D/Scene2DHost.cs:47-48, 90, 93-117, 195, 348, 913-975, 1125-1165`; `Annotations/SceneHostToolServices.cs:23-198`; `Annotations/AnnotationSessionController.cs:32-35, 228-284, 372-374, 755-795`; `Playback2DKeymap.cs:14-58, 73-91, 159-180, 198-231, 307-401`; `Playback2DKeymapProfile.cs:25, 96-169`; `Playback2DExportHost.cs:54-62`; `Playback2DModule.cs:23-51`; `Playback2DRenderer.cs:29-105`; `Playback2DTabViewModel.cs:314-318, 381, 478, 543, 1117, 1156, 1656, 2266`; `Timeline/Playback2DTimelineViewModel.cs:29, 135-178, 301`; `Modules/Highlights/HighlightsModule.cs:31-62`; `Views/Playback2D/Playback2DView.axaml.cs:53-56, 206-269`; `ViewModels/Playback2D/AnnotationsPanelViewModel.cs:199, 362`; `Features/FeatureCatalog.cs:59-63, 147-149, 164-169`; `Features/ShellModuleFeatureGate.cs:12`; `App.axaml.cs:838-869`.
- `tools/DemoViewer.NET.Playback2D.Cli/ExportCommand.cs:200-235, 462-482`; `scripts/dv2d.sh`.
- CS2DemoKit 0.12.0 XML docs: `CS2DemoKit.Parser.xml` (`PositionSample`, `PositionSampler.Walk`, `GameEvent`), `CS2DemoKit.Analysis.xml` (`ClipRound`, `ClipRounds.Derive`).
- CS2DemoKit: `GameTick` is the frame clock.

Measurements, 2026-09-23:

- `scripts/dv2d.sh bench --name synthetic-tenplayers --frames 600 --warmup 64 --cpu --json`, output `step-authoring-bench.json` (`git_commit: d90ec9f`, Windows 10.0.26200, AMD, 32 logical cores, .NET 10).
- Scratch project `step-authoring` (Release, .NET 10, CS2DemoKit.Parser and CS2DemoKit.Analysis 0.12.0), outputs `out_mirage_r7.txt` and `out_nuke_r5.txt`: `match730_003842233788306292960_0260929275_408.dem` (de_mirage, build 10896, `GotvMatchmaking`, 118,309 frames, round 7) and `match730_003842182368957825245_0056633905_389.dem` (de_nuke, build 10896, `GotvMatchmaking`, 123,589 frames, round 5), both read-only from the Steam `replays` folder. The tour sample was not used.
