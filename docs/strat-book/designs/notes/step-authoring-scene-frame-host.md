# Step Authoring note: the `ISceneFrameHost` seam over `Scene2DHost`

Build note for `designs/step-authoring.md` §3.10, written because `00-overview.md` §6.2 flagged the seam
as the riskiest App change in the set. It is not a new design: it pins what the host binds today, the
smallest change that lets a demo-free `StratCanvasViewModel` drive the same canvas, what that costs in
Core versus App, what `dv2d` needs, the tests, and the risks. Line numbers are against `d90ec9f`.

## 1. What the host binds today

`Scene2DHost : Control, IPlayback2DSurface, ILevelSurface, IAnnotationSurface`
(`src/App/DemoViewer.NET/Modules/Playback2D/Scene2DHost.cs:47-48`). It owns the seven-layer compositor,
the panes, the level model and the tool router; the only thing it borrows is the view-model, and it
borrows it as the concrete class. Every read of that class:

| Site | Line | What is read | Why the host needs it |
|---|---|---|---|
| field | `:90` | `Playback2DTabViewModel? _vm` | the one binding |
| `CurrentSceneFrame` | `:117` | `_vm?.CurrentFrame` | tool services: `CurrentTick`, entity-anchor hit tests (`Annotations/SceneHostToolServices.cs:23`) |
| `BuildScene` | `:348` | `() => _vm?.VisionEngine` | closure handed to `VisibilityEngineSolver` (`Pipeline/Vision/VisibilityEngineSolver.cs:43`); read at solve time, so it survives a re-bind |
| `OnDataContextChanged` | `:444-447` | `DataContext as Playback2DTabViewModel` | discovery |
| `OnAttachedToVisualTree` | `:451-464` | same cast, again | re-attach after a release |
| `AdvanceAndSubmit` | `:749-751` | `_vm?.CurrentFrame` | the frame that is advanced and submitted |
| one-shot fit | `:800-804`, `:1172` | `CurrentFrame.Map.ObservedBounds` | `CurrentExtent()`; note it is `ObservedBounds`, not `NetworkedBounds` |
| `AttachVm` | `:913-949` | `FrameUpdated` subscribe/unsubscribe | the only push signal; also clears smoother, crossings, levels, panes, gesture |
| `SyncFromViewModel` | `:962-986` | `ShowRadar`, `ShowTrails`, `ShowAreaEffects`, `ShowVision`, `ShowBombRing` (`:969-973`); `AnnotationSession` (`:975`); `IsAnnotationsEnabled` (`:976`); `MapAsset` (`:978-984`) | overlay toggles (D5), ink layer registration, authoritative floors and radar binding |
| `RebaseAnnotationAnchors` | `:1125-1163` | `_vm.ApplyAnnotationLevelRebuild(moved)` (`:1162`) | remaps `ZMin`-keyed ink when the level set moves |

On the view-model side those are `CurrentFrame` (`Playback2DTabViewModel.cs:478`), `FrameUpdated`
(`:1156`, raised by every toggle setter at `:1158-1162` and by every push), `MapAsset` (`:371`),
`VisionEngine` (`:381`), `AnnotationSession` (`:499`, null when annotations are off),
`IsAnnotationsEnabled` (`:511`), the toggle fields (`:259-284`) and `ApplyAnnotationLevelRebuild`
(`:1117`). Nothing else on the 2,574-line class is touched by the host.

Everything Avalonia-specific in the host is independent of the binding: the RAF loop and its clamped
wall-clock `dt` (`:1034-1058`), `RenderScaling` (`:832`), the theme palette (`:902`), the CPU fallback
bitmap and the custom draw op (`Render`, `:720-745`). The seam does not touch any of it.

The surrounding view does four more wirings the strat view must repeat, none through the host's binding:
`SetSurfaceCapabilities(_toolSurface is not null)` (`Views/Playback2D/Playback2DView.axaml.cs:121`),
`Annotations.ToolSelected` to `IAnnotationSurface.SetActiveTool` (`:126-127`),
`LevelStrip.Bind(_levelSurface)` (`:131`) and the keymap's `HoldPan`/`CancelGesture` (`:206-269`).

## 2. The smallest seam

An App-internal interface with exactly the members in the table, discovered the way the class is
discovered today. Nothing moves, nothing is extracted, and no behaviour on the 2D tab changes.

```csharp
// src/App/DemoViewer.NET/Modules/Playback2D/ISceneFrameHost.cs  (internal, like IAnnotationSurface)
internal interface ISceneFrameHost
{
    Scene2DFrame CurrentFrame { get; }            // stable reference until the next FrameUpdated
    event Action? FrameUpdated;                   // raised on every push AND every toggle change
    LoadedMapAsset? MapAsset { get; }             // floors + radar binding; null = grid fallback
    VisibilityEngine? VisionEngine { get; }       // null = no cones
    AnnotationSession? AnnotationSession { get; } // null = no ink layer mounted
    bool IsAnnotationsEnabled { get; }
    bool ShowRadar { get; }  bool ShowTrails { get; }  bool ShowAreaEffects { get; }
    bool ShowVision { get; } bool ShowBombRing { get; }
    ITokenEditor? TokenEditor { get; }            // Core interface from Step Authoring item 2; null on the 2D tab
    void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double, double> zMinMap);
}
```

Correction to §3.10: its member list omits `MapAsset` and `IsAnnotationsEnabled`, both read at
`Scene2DHost.cs:976-978`. Thirteen members, not nine. `event` and `get`-only: the host never writes.

Host edits, all mechanical:

- `:90` becomes `private ISceneFrameHost? _vm;` and `AttachVm(ISceneFrameHost?)` (`:913`).
- `:447` and `:464` become `DataContext as ISceneFrameHost`, through one private helper so there is one
  cast site. Discovery stays on `DataContext`: `Playback2DTimelineHarness.Show` sets it on the window
  (`App.Tests/Playback2DTimelineHeadlessSupport.cs:59-66`) and the re-attach test relies on
  `OnAttachedToVisualTree` re-reading it (`Scene2DHostTests.cs:354`). An explicit `Bind()` would be a
  second path for the same state and would break neither test while quietly bypassing both.
- `internal ISceneFrameHost? FrameHost => _vm;` so `SceneHostToolServices.Tokens` can return
  `host.FrameHost?.TokenEditor` when `IToolServices.Tokens` lands (§3.7).
- `:348`, `:751`, `:975-984`, `:1162`, `:1172` compile unchanged because the member names match.

View-model edits: `Playback2DTabViewModel : ..., ISceneFrameHost` with one new member,
`public ITokenEditor? TokenEditor => null;` (the interface is internal, the class public: legal, and
`App.Tests` already sees internals through `AssemblyInfo.cs:10`).

What `StratCanvasViewModel : ISceneFrameHost, ITokenEditor` must supply for the host to behave:

- `CurrentFrame.Map.ObservedBounds` = the bundle's world rectangle, and `NetworkedBounds` = the same.
  The host fits to `ObservedBounds` (`:1172`); `HeadlessSceneRenderer.Advance` prefers `NetworkedBounds`
  (`Pipeline/Headless/HeadlessSceneRenderer.cs:211-288`). Fill both or the live canvas and the export
  frame the map differently.
- `Time.FrameIndex = Tick`, `IsDiscontinuity = true` on seek, step jump and path switch, false while the
  transport plays. The host consumes a discontinuity once per `(FrameIndex, Tick)` identity
  (`:753-757`), so a repeated identity with the flag still up is ignored, as intended.
- A published frame is never mutated. The 2D tab double-buffers through `SceneFrameBuilder`
  (`Core/Scene2DFrame.cs` header: "refills the off-screen one in place") because the render thread
  replays the submitted frame. Ten markers per tick: allocate a fresh `Scene2DFrame` per publish and
  keep the pooling for `StratFrameSource`, where the per-frame budget applies.
- `FrameUpdated` from every toggle setter and every transport tick, on the UI thread
  (`DispatcherTimer`, §3.10). `SyncFromViewModel` runs only from `AttachVm` and `OnFrameUpdated`
  (`:946-957`), so a toggle that does not raise the event never reaches the compositor.
- `ShowVision = false`, `VisionEngine = null`; `IsAnnotationsEnabled = true` unconditionally: the ink is
  the strat's content, not an overlay, so the `playback2d.annotations` gate that
  `AnnotationSessionController.IsEnabled` reads must not apply. `AnnotationsPanelViewModel.IsEnabled` is
  `controller.IsEnabled && IsSurfaceCapable` (`ViewModels/Playback2D/AnnotationsPanelViewModel.cs:236`),
  so the strat view calls `SetSurfaceCapability(true)` (`:423`) exactly as the 2D view does at `:121`.
- `MapAsset` from `MapAssetLoader` (`Modules/Playback2D/MapAssetLoader.cs:27`), the same bundle.

The Strat Book view sets `host.DataContext = vm.Canvas` explicitly rather than relying on inheritance,
because the tab's own DataContext is the Strat Book tab view-model, not the canvas.

## 3. Core versus App

| Layer | For the seam | Adjacent Step Authoring items (not the seam) |
|---|---|---|
| `Playback2D.Core` | nothing | `ITokenEditor`, `IToolServices.Tokens`, `ToolKind.Token`, `TokenTool`, `IsDrawingToolActive` widening (§3.7, item 2). Pure interfaces and one tool; `ArchitectureTests` and `BannedApiTests` unaffected |
| `Playback2D.Pipeline` | nothing | `StratFrameSource` (§3.6, O-14) is export-side and never touches the host |
| App | one new file (`ISceneFrameHost.cs`), three edits (`Scene2DHost.cs`, `Playback2DTabViewModel.cs`, `SceneHostToolServices.cs`) | `StratCanvasViewModel`, the Strat Book view's four wirings, `StratTransport`, `StratTimelineData` |
| `Playback2DView` | unchanged | |
| `FakeToolServices` (Core tests) | unchanged | gains a settable `Tokens` with item 2 |

The layer stack in `BuildScene` (`:343-370`) stays the seven catalog layers plus the opt-in ink layer.
Do not add a strat-only layer to the host (step labels, opponent tint): `SceneLayerListParityTests`
pins the host to exactly the catalog's seven non-opt-in ids plus at most `playback2d.annotations`
(`App.Tests/SceneLayerListParityTests.cs`), and `dv2d` could not render such a layer into a golden.
Step numbers belong on the timeline track and in XAML; opponents are markers with `Team` set.

## 4. What `dv2d` needs

For the seam: nothing. `dv2d` never loads the host; it builds its stack through
`SceneLayerCatalog.CreateSceneStack` (`Pipeline/Headless/SceneLayerCatalog.cs:138-211`) and drives
`HeadlessSceneRenderer`, and both stay as they are.

For the strat goldens §7 describes, one correction. The plan registers the three entries with `layers`
naming the seven scene layers, `playback2d.annotations` and `hud.clock`. `dv2d render`, `golden` and
`bench` refuse `hud.*` for a fixture: `SceneRenderPlan.RequireFeedableOptIns`
(`tools/DemoViewer.NET.Playback2D.Cli/SceneRenderPlan.cs:233-268`) throws
"a HUD needs a demo's clock, scoreboard and kill timeline ... Only 'dv2d export --hud' can feed it",
and two manifest notes (`tests/fixtures/playback2d/manifest.json:97`, `:115`) and
`ExportLayerParityTests` document that rule. So either:

- (a) leave `hud.clock` out of the strat entries' `layers` and pin the countdown through
  `StratFrameSourceTests` plus an `ExportHudClockTests`-style test over `StratHudDataSource`; or
- (b) teach `SceneRenderPlan` a `FixtureHudDataSource` over `Scene2DFrame.GameInfo` (`RoundSeconds`,
  `RoundTime` are already in the frame) for `hud.clock` only, keeping roster and kill feed refused.

Recommend (a) for the build; (b) reopens a rule three places pin and is a `dv2d` change of its own.

The entries themselves need no CLI change: `scenes/<name>.scene.json` plus
`annotations/<name>.dvann.json` by convention, `map: de_mirage`, the bundle's current `map_version` CRC
(`golden verify` refuses a mismatch, `docs/playback2d-v2/dv2d.md:431`), `tolerance: perceptual`,
`pending: true` until Shape Tools lands. That is the `annotated-mirage-b` shape
(`manifest.json`, the entry with eight `layers`).

`SceneGoldenTests.SyntheticFixture_MatchesCommittedGolden` lists its entries with `[Arguments]`
(`Playback2D.Tests/SceneGoldenTests.cs:42-45`); add the strat names there or they are covered only by
the CLI-side `GoldenCorpusTests`, which reads the manifest.

## 5. Tests that pin it

Existing, unchanged, the regression gate for the 2D tab (all mount through `Playback2DTabViewModel`):
`Scene2DHostTests` (eight tests, `:36` to `:354`, including the non-blank render at `:104`, the gate
stress at `:291` and detach/re-attach at `:354`), `SceneLayerListParityTests`,
`Playback2DSurfaceCapabilityTests`, `Playback2DAnnotationHostTests`, `Playback2DMirrorLiveViewTests`,
`Playback2DLevelStripTests`, `Playback2DKeybindRoutingTests`.

New, App suite, `Scene2DHostFrameHostTests`, every one over a `FakeSceneFrameHost` record and no
`IModuleContext`:

| Test | Pins |
|---|---|
| `FakeHost_MountsAndRendersMarkers_WithoutATabViewModel` | `DataContext` = fake with ten markers and a `de_mirage` bundle (or null); one `Render` through the CPU fallback (`ForceLeaseUnavailableForTest`) is non-blank with team colours; mirror of `Scene2DHostTests.cs:104` |
| `FrameUpdated_Invalidates_AndADiscontinuityIsConsumedOnce` | raise with `IsDiscontinuity`, render twice, `SmoothedMarkerPosition` snaps on the first frame and glides on the second (`:753-757`) |
| `ObservedBounds_DrivesTheFirstFit` | `PrimaryCameraTransform` equals a fit of the fake's `ObservedBounds`, guarding the `ObservedBounds`/`NetworkedBounds` asymmetry of §2 |
| `TokenEditor_ReachesTheToolServices` | recording `ITokenEditor` on the fake, `ToolKind.Token` active, a press over a marker reaches `BeginDrag`; same headless pointer path as `Drag_PansOnlyThePaneUnderTheCursor` (`:141`) |
| `SwappingHosts_CancelsTheGesture_AndClearsSmoothing` | `DataContext` from the tab VM to the fake mid-stroke: `Router.IsGestureOpen` false, smoother empty, `PaneCountForTest` rebuilt (`:913-949`) |
| `LevelRebuild_ForwardsToTheHost` | two-floor frames whose band moves; the fake records the `zMinMap` (`:1125-1163`) |
| `SceneLayerListParityTests` gains a fake-host row | the seven-plus-ink rule holds when the host is not on the 2D tab |

Core and Pipeline: `ArchitectureTests.Core_TransitiveClosure_ContainsNoAvalonia` and
`Pipeline_TransitiveClosure_ContainsNoAvalonia` (`Playback2D.Tests/ArchitectureTests.cs:43-48`) and the
CLI's `NoAvaloniaArchitectureTests` (`Cli.Tests/NoAvaloniaArchitectureTests.cs:25-49`, deps.json and a
real subprocess) stay green by construction: the seam adds no reference below the App. The way to break
them is to put `ISceneFrameHost` in Pipeline "to share it": it names `LoadedMapAsset` and
`VisibilityEngine` (Pipeline) and `AnnotationSession` (Core), so it would compile there, but nothing
headless consumes it and the next step would be a Pipeline reference to `AnnotationsPanelViewModel`.
Keep it App-internal. `BannedApiTests` (`:58-67`) covers `TokenTool` against clocks and `Random`.

Determinism: `SceneDeterminismTests` (`:31`, `:58`, `:76`, `:99`) hash a fixture by name through
`SceneStage`; add `strat-mirage-exec-mid` to its inputs. `StratFrameSourceTests` runs
`HashingFrameSink` twice (§7, the `ExportCameraAndDeterminismTests` pattern).
`RenderDeterminismTests` (`Cli.Tests/RenderDeterminismTests.cs:20-35`) covers any corpus entry through
`dv2d` in-process and in a fresh process. None of these render the live host: its `dt` is wall clock
(`:1047-1058`), so a golden over the interactive canvas is not a test that can pass twice.

Goldens: the three entries plus `strat-mirage-exec-mid@1280x720`, captured by
`StratGoldenCaptureTests` under `PB2D_GOLDEN_UPDATE=1` like `Playback2DGoldenCaptureTests`
(`App.Tests/Playback2DGoldenCaptureTests.cs`), verified by `dv2d golden verify --cpu` and by
`SceneGoldenTests` reading the same PNGs.

## 6. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| `DataContext` inheritance binds nothing: the host under the Strat Book tab inherits the tab VM, the cast yields null, the canvas renders `Scene2DFrame.Empty` silently | Certain if left to inheritance | The strat view assigns `host.DataContext = vm.Canvas`; the first new test mounts with no window-level DataContext |
| `ObservedBounds` versus `NetworkedBounds`: live canvas fits to the default placeholder while the export fits to the map | High if only one is filled | `StratCanvasViewModel` and `StratFrameSource` fill both; `ObservedBounds_DrivesTheFirstFit` and a `StratFrameSourceTests` equality assertion |
| Published frame mutated in place while the render thread replays it | Medium | fresh frame per publish on the canvas; pooling only in the export source |
| A toggle that forgets `FrameUpdated` never reaches the compositor | Medium | the tab's pattern (`:1158-1162`); assert `SyncFromViewModel` ran by flipping `ShowRadar` and reading `UseRadarImage` |
| The `playback2d.annotations` feature gate hides the strat's ink | Medium | `IsAnnotationsEnabled => true` on the canvas host; `SetSurfaceCapability(true)` from the strat view |
| `AnnotationSessionController` built with a null store still tries a sidecar load on activation (`AttachAnnotationsToCurrentDemo`, `Playback2DTabViewModel.cs:1126`) | Low | that call is the tab VM's, not the controller's; the canvas never calls it |
| A strat-only layer added to `BuildScene` | Low | parity test fails immediately; §3 says where step chrome goes |
| `IPlayback2DSurface.FollowSlot` and `CameraMode.FollowPlayer` are meaningless on a strat | Certain, harmless | the strat view exposes Fit and Map only; the host tolerates a follow slot with no matching marker |
| Detach releases the compositor and the ink layer (`:468-476`, `:994-1023`); the strat session must outlive the view | None new | the session lives on `StratCanvasViewModel`, re-bound by the next `SyncFromViewModel`, exactly the tab's lifecycle |
| Browser head | None new | same control, same Skia lease, same CPU fallback; export gating is `stratbook.export` (§3.10) |

## 7. Sources

`Scene2DHost.cs`, `Playback2DTabViewModel.cs`, `Playback2DRenderer.cs:29-95`,
`Annotations/SceneHostToolServices.cs`, `Views/Playback2D/Playback2DView.axaml.cs:51-135`;
`Core/Compositing/ISceneLayer.cs`, `SceneCompositor.cs:141-289`, `SceneSubmission.cs`,
`Core/Export/ISceneFrameSource.cs`, `Core/Input/IPointerTool.cs:145`, `Core/Hud/IHudDataSource.cs`;
`Pipeline/Headless/SceneLayerCatalog.cs`, `HeadlessSceneRenderer.cs`;
`docs/playback2d-v2/design.md` §5.1, §5.2, §5.5, §5.7, §5.8, §7.1, §7.5; `docs/playback2d-v2/dv2d.md`;
`tests/fixtures/playback2d/README.md`, `manifest.json`; `designs/step-authoring.md` §2.2, §2.3, §3.6,
§3.7, §3.10, §7; `designs/00-overview.md` §3.5, §6.2; `designs/strat-model.md` §3.11.
