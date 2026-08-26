# Removing the pre-v2 2D control — the next release's cleanup

**Status: WRITTEN, NOT EXECUTED.** Design §9 keeps the old control for one release behind an internal
toggle; B5 writes this plan, the release *after* v2 ships executes it. Verified against the tree at
B5 (`0994bf8`) — every path, line number and reference count below was grepped, not remembered.

The thing being deleted is `Playback2DViewport` (1 447 loc), the `DrawingContext`-based control the v2
compositor replaced. It is reachable today only through the escape hatch:
`AppSettings.Playback2D.LegacyViewport`, or `DV_PLAYBACK2D_RENDERER=legacy`, both resolved once per
process by `Playback2DRenderer.Selected`.

---

## Removal trigger — all four must hold

1. **v2 default-on has shipped in one tagged release.** Not merged: shipped, with users on it.
2. **No open bug whose only workaround is `LegacyViewport=true`.** The hatch exists to be an A/B for
   exactly this; if it is load-bearing for anyone, it is not time.
3. **The six retargeted test classes below are green in their new form**, in the same PR.
4. **`Playback2DLegacyToggleTests` is deleted in the same commit.** It asserts the old control mounts;
   leaving it is a red suite.

If (2) fails, the honest move is to fix the bug and re-start the clock — not to delete the fallback
while it is still somebody's answer.

---

## Files to delete or edit

| Path | Action |
|---|---|
| `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DViewport.cs` | **delete** (1 447 loc) |
| `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DRenderer.cs` | **delete `Playback2DRendererKind`, `Playback2DRenderer` and `ResetForTest`.** Keep `IPlayback2DSurface` and `ILevelSurface` — the View still talks to the host through them, and `ILevelSurface` is the level strip's whole contract. With one surface left, `IPlayback2DSurface` could be folded into `Scene2DHost`; that is a judgement call for the person doing it, not a requirement. |
| `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml.cs` | the constructor's surface *choice* (`:39-41`) collapses to `new Scene2DHost()`; `_surface` can become `Scene2DHost` and the five `is Scene2DHost` casts (`:125`, `:143`, `:220`, `:229`, `:245`) go with it; `_levelSurface` stops being a `surface as ILevelSurface`; the `LiveCameraSource` wiring loses its null branch. Update the class doc (`:15-21`), which still names the old control. |
| `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml` | the comment above `ViewportHost` (`:101-103`) describes the two-surface hatch — reword. The `ViewportHost` `ContentControl` **stays**: it is how the surface is mounted, not part of the hatch. |
| `src/App/DemoViewer.NET/Configuration/AppSettings.cs` | delete `Playback2DSettings.LegacyViewport` (`:402`) and its doc block. |
| `src/App/DemoViewer.NET/Configuration/SettingsService.cs` | delete the `Playback2D:LegacyViewport` `WriteInMemory` row. `SettingsWasmRoundTripTests` is reflection-driven and needs no edit. |
| `src/App/DemoViewer.NET/Modules/Playback2D/MapAssetLoader.cs` | its Avalonia-`Bitmap` half exists **for the legacy control** (`:19`, `:69`). `MapRadarConverter` still uses `TryLoadRadarThumbnail` for library cards, so the file stays — delete only the radar-bitmap path the viewport consumed, and re-check with `grep` before cutting. |
| `src/App/DemoViewer.NET.UiCapture/Variants.cs` | retarget `["playback2d-canvas"]` (`:153`), `Pb2DLiveSyncHud`'s `new Playback2DViewport` (`:1158`) and the `root.Children.Add` at `:3378` to `Scene2DHost`; update the doc at `:3290`. |
| `src/App/DemoViewer.NET/Views/Tutorial/TutorialView.axaml.cs` | doc-comment reference only (`:19`) — reword. |
| `src/App/DemoViewer.NET/Styles/DarkPalette.axaml` | three comments (`:235`, `:326`, `:564`) say the Skia renderer resolves these tokens *from `Playback2DViewport.cs`*. The tokens themselves are still resolved — by `ScenePaletteFactory` now. **Reword, do not delete tokens** without checking each for zero remaining references; the `Pb2d*` ramp is the v2 HUD's palette too. |

**Core / Pipeline mention `Playback2DViewport` in comments only** (`WorldBounds`, `TrailGeometry`,
`ScenePalette`, `SceneDefaults`, `MarkerSmoother`, `VisionSolution`, `MapRadarBinder`, and two test
files) — they record where a behaviour was ported *from*. Those are provenance, and provenance
survives the code it describes. Leave them, or reword to "the pre-v2 control"; do not treat them as
references to fix.

---

## Tests that retarget

Reference counts are `grep -c Playback2DViewport` at B5.

| Test class | Refs | Retarget to |
|---|:-:|---|
| `GrenadeTrailFloorSplitTests` | 7 | direct execution against `FloorSplitter` / `TrailGeometry` in Core — drop the Avalonia host entirely. |
| `Playback2DInterpolationTests` | 5 | direct execution against `MarkerSmoother` in Core. |
| `Playback2DCameraModeTests` | 4 | direct execution against `ICameraRig` / `SliceCamera` / `PaneSet`. `Scene2DHostTests` already covers the host-level equivalents (`PrimaryCameraTransform`, `PrimaryCameraManual`). |
| `ZTrajectoryRenderTests` | 2 | CPU-provider golden in the Playback2D test project. |
| `ZRadarRenderTests` | 1 | ditto. |
| `ZVisionOverlayRenderTests` | 1 | ditto. |

**Deleted outright:** `Playback2DLegacyToggleTests` (4 refs) — its entire subject is the hatch.

**Retargeted by editing one helper:** `Playback2DTimelineHeadlessSupport.Viewport(view)` (`:101`) is the
accessor four suites reach the old control through (`Playback2DFollowCardRenderTests`,
`Playback2DTimelineRenderTests`, `Playback2DKeyRoutingTests`, and the harness's own `Show(renderer:)`
parameter). Delete `Viewport`, delete `Show`'s `renderer` parameter and its `Playback2DRenderer
.ResetForTest` call, and those four suites become `SceneHost(view)` calls — they are testing the
timeline, the follow funnel and key routing, not the surface. `Scene2DHostTests` loses its
`Default_MountsTheSceneHost_AndLegacyIsStillReachable`,
`EnvironmentVariable_SelectsTheSurface_AndOutranksTheSetting` and
`BothSurfaces_SatisfyThePlayback2DSurfaceContract` cases for the same reason.

`Playback2DGoldenCaptureTests` (2 refs) is the interesting one: it captures the corpus goldens **from
the pre-v2 control on purpose** — that is what makes them a parity baseline rather than a snapshot of
v2's own output. It must be retargeted to `Scene2DHost` **in a commit of its own, with the goldens
re-captured and the diff inspected**, not folded into the deletion. Until then the committed
`nuke-multilevel` pair stays exactly as it is: it is the only real gate B1's parity claim rests on.

It is **not byte-exact**, as this paragraph used to say (corrected D6 round 3). `GoldenParityTests`
compares a delta *distribution* — ≥99 % of pixels within ±8 and ≥99.5 % within ±32 — because the
pre-v2 control draws through Avalonia's `DrawingContext` and v2 through Skia, and two rasterisers
cannot agree pixel for pixel on an anti-aliased edge. That is the point of the gate, not a weakness in
it, and the distinction matters to the retarget above: **after** retargeting, both sides are Skia and
the tolerance should be tightened rather than inherited. Byte-exactness in this repo means
`SceneDeterminismTests` / `SceneRendererTests.Render_Twice_ProducesByteIdenticalPixels` — v2 against
itself on one machine — and never a cross-rasteriser comparison.

---

## Order of work

1. Retarget the six classes; land them green **while the old control still exists**, so a failure is
   attributable to the retarget and not to the deletion.
2. Retarget `Playback2DGoldenCaptureTests` and re-capture, separately, with the diff reviewed.
3. Delete the control, the renderer switch, the setting, the `WriteInMemory` row, and
   `Playback2DLegacyToggleTests` — one commit.
4. Reword the comments and retarget `UiCapture`.
5. Update `docs/ui/design-system.md` (the `Pb2d*` token block's provenance notes) and delete this file.

## What must NOT be deleted with it

- `IPlayback2DSurface` / `ILevelSurface` — the View's contract with the surface that remains.
- The `Pb2d*` token ramp — the v2 HUD's palette.
- `MapAssetLoader` — `MapRadarConverter` still uses it for library cards.
- The `nuke-multilevel` golden and its `.scene.json` — until step 2 says otherwise.
