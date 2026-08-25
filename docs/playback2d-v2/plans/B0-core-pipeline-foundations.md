# Phase B0 — Core + Pipeline foundations

**Plan for:** `docs/playback2d-v2/design.md` track B, phase B0
**Branch:** `feature/playback2d-v2` · **Budget:** 2 weeks (~7 person-days of work below + slack)
**Audience:** a coding agent that has NOT read the design doc. Everything needed is restated here.

> ## Integrator corrections (BINDING — supersede anything below that disagrees)
>
> Cross-phase reconciliation, `plans/00-overview.md` §3 is the canonical registry.
>
> 1. **Namespace map.** Core is *not* flat. B0's types land in
>    `DemoViewer.NET.Playback2D.Core` (`SceneTime`, `RenderPurpose`, `Scene2DFrame`, `SceneMapInfo`,
>    `SceneGameInfo`, `SceneVision`, `KillFeedRow`, `WorldBounds`, `MapRadarImage`, `ScenePalette`,
>    `ViewportTransform`, `SliceCamera`, the draw-state value types),
>    `…Core.Compositing` (`LayerSlot`, `LayerCacheHint`, `ISceneLayer`, `SceneRenderContext`,
>    `SceneCompositor`) and **`…Core.Rendering`** (`RenderBackend`, `IRenderSurfaceProvider`,
>    `CpuSurfaceProvider`, `SceneRenderer`). C2 binds `RenderSurfaceProviderFactory`/
>    `GpuSurfaceProvider` into `…Core.Rendering`; B1/B2/B3/B4 add `…Core.{Levels,Cameras,Vision,
>    Layers,Annotations,Ink,Input,Timeline,Export,Hud}`.
> 2. **`SceneRenderContext` and `ISceneLayer` are shared with B1/B2/B3/B4.** The shapes below have
>    been amended to B1's member names (`PaneBounds`, `RenderScaling`, `BelongsHere`,
>    `IsSingleLevel`, `Frame`, `Time`, `ContentVersion`). B1 extends the context with `Pane`
>    (`LevelPaneSnapshot`) and `LevelIndexFor`; B3 adds `Levels`/`LevelCrossings`. Do not fork it.
> 3. **`SceneCompositor` members are `Add`/`Remove`/`Find`/`SetEnabled`** (B1's names), not
>    `AddLayer`/`RemoveLayer`. B0 ships `Render(SKCanvas, in SceneRenderContext)` (single pane);
>    B1 **adds** `Render(SKCanvas, in SceneSubmission)` as an overload.
> 4. **`PlayerMarker` gains `ulong SteamId`** (design §5.4 requires SteamId anchoring; B2's
>    entity-anchored annotations and B4's `CameraScript.FollowPlayer(steamId)` both need it).
>    `SceneFrameInput` gains `Func<int, ulong>? SteamIdForSlot` to fill it. This is the one
>    non-verbatim change to a moved value type; note it in the move commit.
> 5. **`KillFeedRow` is a Core type, defined here, and B4 must not redefine it in Pipeline.** B4's
>    `KillFeedTimeline.Window` operates on *this* record; B4's `HudSnapshot` moves to `Core.Hud`.
> 6. **Corpus layout is C1's** (`plans/C1-cli.md` §"Fixture corpus layout"), and B0 authors into it:
>    scenes → `tests/fixtures/playback2d/scenes/<name>.scene.json`, goldens →
>    `tests/fixtures/playback2d/goldens/cpu/<name>@<w>x<h>.png`. **There is no `tests/goldens/`.**
>    C1 owns `manifest.json`; B0 seeds the entries it authors.
> 7. **`SceneFixture` gains `SceneTime Time`, `string? MapName`, `string? MapVersion`,
>    `SKSizeI Size`** (C1's `render --fixture`/`golden`/`bench` need them) plus
>    `static SceneFixture Load(string)` / `void Save(string)` conveniences delegating to
>    `SceneFixtureSerializer`. `Camera` stays `ViewportTransform`; `Annotations` stays `JsonElement?`.
> 8. **B0 also owns the golden comparator** — `GoldenImageComparer`, `GoldenTolerance`,
>    `GoldenComparison` in `Pipeline/Goldens/` (signatures in `plans/C1-cli.md` §"Public API
>    contracts", extended by C2 with SSIM). B1's `GoldenParityTests`, B0's own capture test, C1's
>    `golden` command and C2's parity lane all call it — do not inline a second comparison.
> 9. **Native Skia on Linux is `SkiaSharp.NativeAssets.Linux` (not `…NoDependencies`)** plus an
>    `apt-get install -y libfontconfig1` step in the CI job: B1 draws text, C1's goldens contain
>    text, and switching the package later would silently re-baseline every golden.
> 10. **One test project, `src/Playback2D/DemoViewer.NET.Playback2D.Tests`,** covers Core *and*
>     Pipeline. B2/B3/B4/B5 reference `…Core.Tests` / `…Pipeline.Tests` — those do not exist; read
>     them as this one project.
> 11. **The `Modules.Abstractions.Ui` split (D1) is load-bearing for B4 and C1** — it is what lets
>     Pipeline consume `IPlaybackSnapshot`/`IPlayerState`/`IReadOnlyEntityView` and therefore what
>     lets `SceneFrameBuilder` keep its `SceneFrameInput` signature. B4 adds a Pipeline-side
>     `TrackerSceneSnapshot` adapter over `EntityTracker`; **`SceneFrameBuilder.Build` is not
>     re-shaped to take an `EntityTracker`.**

---

## Scope & exit criterion (quoted from the design)

design.md §9, the B0 row of the migration table, verbatim:

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **B (core)** | B0 | Core + Pipeline projects; move structs; `SceneFrameBuilder` + `CpuSurfaceProvider` + scene fixtures; golden-image tests pinning current output | Frames render to PNG with **zero Avalonia dependencies**; goldens green | 2 wk |

Supporting design constraints B0 must satisfy:

- §4: `DemoViewer.NET.Playback2D.Core` references **SkiaSharp only** — no Avalonia, no parser, no
  Modules.Abstractions. `DemoViewer.NET.Playback2D.Pipeline` references **Core + the CS2DemoKit parser
  packages** — still no Avalonia.
- §3 / §9: `ViewportTransform` and `SliceCamera` move to the core **verbatim** (including per-slice
  `ManualOverride` semantics).
- §9 "Extracted": `BuildFrame` → `SceneFrameBuilder` (Pipeline).
- §5.1: no `DateTime`/`Stopwatch`/`Random` in Core — enforced by test, not convention.
- §5.8: `CpuSurfaceProvider` is the **contract baseline**; golden images are authored on it.
- §11: architecture tests; direct-execution tests (no Avalonia platform at all); golden-image tests
  pinning B0 output as the corpus B1 must match; `SceneFixture` files under `tests/fixtures/playback2d/`.

**What B0 is not:** no layer ports (B1), no `Scene2DHost`, no `MapSpace`/`LevelPane`, no annotations
(B2), no export (B4), no `dv2d` CLI (C1), no GPU provider (C2). B0 stands up the assemblies, the value
types, the frame contract, the CPU render loop, the fixtures and the test gates — and proves the loop
with one trivial smoke layer.

---

## Repo facts the implementer must know before starting

Verified against the working tree at commit `305c5ac`.

- **Solution file is `DemoViewer.NET.slnx`** (new XML format), not a `.sln`. Projects are added as
  `<Project Path="..."/>` inside a `<Folder Name="/area/">`.
- `Directory.Build.props` sets, for every project: `TargetFramework=net10.0`, `Nullable=enable`,
  `ImplicitUsings=enable`, `EnableNETAnalyzers=true`, `EnforceCodeStyleInBuild=true`,
  **`TreatWarningsAsErrors=true`**, `AnalysisMode=Recommended`, **`GenerateDocumentationFile=true`**
  (`NoWarn=CS1591` is set, so undocumented members do not fail — but analyzer and style violations do).
- `Directory.Packages.props` is **Central Package Management**: `PackageReference` items carry **no
  `Version=` attribute**; versions live centrally.
- **Test framework is TUnit 0.25.21** (not xunit/nunit): `[Test]`, `await Assert.That(x).IsEqualTo(y)`,
  `[Before(HookType.Assembly)]`, `[NotInParallel]`, `[Category("Integration")]`,
  `throw new SkipTestException("…")` to skip. Test projects are `OutputType=Exe` with a distinct
  `RootNamespace` and `NoWarn=$(NoWarn);CA1707`.
- **Code style** (`.editorconfig`, enforced at build): 4-space indent, LF endings, max 120 cols,
  **file-scoped namespaces** (`namespace Foo;`), **Allman braces**, **braces always** (even one-liners),
  **explicit types — `var` is disabled everywhere**. Every file in this repo opens with a
  `#region` / `using …` / `#endregion` block above the namespace; match it.
- CI (`.github/workflows/ci.yml`) is a single ubuntu job that only runs
  `dotnet build src/App/DemoViewer.NET.Desktop -c Release`. **No tests run in CI today.** B0 adds the
  first test step (see Build & wiring).
- Demo fixtures resolve through `DemoViewer.NET.TestSupport.DemoTestHelper` (`DEMO_PATH` env →
  `TestData/` → `<repo>/demos/benchmarks/` → `<repo>/demos/`), and `FindDemoPath` returns null when
  absent so the caller throws `SkipTestException`.
- Headless Avalonia lives in `src/App/DemoViewer.NET.App.Tests/HeadlessSession.cs`:
  `HeadlessSession.RunOnUi(async () => …)`, artifacts to `HeadlessSession.ArtifactDir`. The reference
  pattern for a render-and-capture test is `ZRadarRenderTests.cs`.

---

## Decisions made

The design left these open or did not anticipate them. Each is a call I am making now; they are binding
for later phases unless the integrator overrides them.

**D1 — `DemoViewer.NET.Modules.Abstractions` must become Avalonia-free, by splitting out one file.**
The design (§4) says Pipeline "adapts `IPlaybackSnapshot`/tracker state into `Scene2DFrame`" *and* that
Pipeline has no Avalonia. Those two statements are in conflict today: `IPlaybackSnapshot`,
`IPlayerState`, `IReadOnlyEntity`, `IReadOnlyEntityView` live in `DemoViewer.NET.Modules.Abstractions`,
which carries `<PackageReference Include="Avalonia"/>` — used by **exactly one file**,
`WorkspaceTabDescriptor.cs` (`Func<Control> ViewFactory`, `Control? ActiveContent`), plus
`IWorkspaceModule.cs` which returns descriptors.
Resolution: move `WorkspaceTabDescriptor.cs` and `IWorkspaceModule.cs` into a new sibling project
`DemoViewer.NET.Modules.Abstractions.Ui` **keeping the same namespace**
(`DemoViewer.NET.Modules.Abstractions`), so no call site's `using` changes; drop the Avalonia
`PackageReference` from the base project. Rejected alternatives: changing `ViewFactory` to
`Func<object>` (pushes `DataContext` assignment out of `Activate`, a module-contract break);
duplicating the entity read surface in Pipeline (per-entity adapter allocation, hostile to the §6
zero-allocation budget).

**D2 — Draw-state value types live in Core, not Pipeline.** `PlayerMarker`, `RingState`, `AreaEffect`,
`AreaEffectKind`, `GrenadeTrail`, `GrenadeTrailPoint`, `GrenadeKind`, `BombMarker` are what the seven
B1 layers draw, and Core cannot reference Pipeline. They move to Core.

**D3 — `FloorSplitter` / `FloorSlice` do NOT move in B0.** The design assigns them to the `MapSpace`
factory, which is B1 work. They stay in `DemoViewer.NET.Modules.Playback2D` for B0 and move in B1.
`Scene2DFrame` therefore carries floor *inputs* (`SectionHeights`, world bounds) rather than resolved
levels.

**D4 — Vision geometry is computed in Pipeline and carried on the frame; Core's `VisionLayer` only
draws it.** `VisibilityEngine` / `VisibilityAnalyzer` live in `CS2DemoKit.Analysis.Visibility`, which
Core may not reference. So `Scene2DFrame.Vision` carries already-clipped world-space cone fans and
sightline segments. This also pre-satisfies the §6 mitigation "vision `Advance` moves off the UI
thread" — the computation is already outside the layer.

**D5 — Overlay visibility toggles are compositor state, not frame state.** `ShowRadar`, `ShowTrails`,
`ShowAreaEffects`, `ShowBombRing`, `ShowVision`, `ShowKillFeed` stay on `Playback2DTabViewModel` in B0
and map onto `ISceneLayer.IsEnabled` in B1. They are deliberately absent from `Scene2DFrame`.

**D6 — `Scene2DFrame` is a sealed class, published by reference, double-buffered by the builder.**
§6 demands zero steady-state allocation; §5.2 demands the draw op consume an immutable snapshot. Both
hold if `SceneFrameBuilder` owns two `Scene2DFrame` instances with pooled `List<T>` backing stores,
refills the off-screen one in place and publishes it. The contract, documented on the type: **a frame
is valid until the next `SceneFrameBuilder.Build` call on the same builder; consumers must not retain
it across pushes.** Export (B4) drives its own builder instance, so it is unaffected.

**D7 — `SceneFixture` carries an annotation slot that is null until B2.** B0 defines
`SceneFixture { Scene2DFrame Frame; ViewportTransform Camera; JsonElement? Annotations; string SchemaVersion; }`.
B2 gives `Annotations` a real DTO; the reader preserves unknown fields from day one (§5.4's tolerant-reader
rule), so no schema break occurs.

**D8 — Two fixture families.** *Synthetic* fixtures (hand-authored JSON, no demo required) drive the
direct-execution smoke tests and run everywhere including CI. *Demo-derived* fixture/golden **pairs**
are captured in one headless push: the same `SceneFrameBuilder` call that feeds the current VM writes
the `.scene.json`, while the current `Playback2DViewport` render writes the `.png`. That guarantees the
JSON and the PNG describe the same world state, which is the whole point of the B1 parity gate. Pair
capture requires a demo and therefore **skips in CI**.

**D9 — B0's `SceneRenderer` renders one pane.** Multi-pane rendering needs `LevelPane`/`MapSpace`
(B1). B0 ships a single-pane `Render` overload; B1 adds a pane-list overload rather than changing this
signature.

**D10 — SkiaSharp is pinned to exactly `2.88.9`.** That is what `Avalonia.Skia 11.3.12` resolves today
(verified in `artifacts/obj/DemoViewer.NET.App.Tests/project.assets.json:1025`). In B1 the on-screen
path takes Avalonia's `ISkiaSharpApiLeaseFeature`, which hands back *Avalonia's* `SKCanvas` — a
different SkiaSharp major (3.x is present in the local nuget cache) would make that a different type
identity and break the lease path outright. Bump only in lockstep with Avalonia.

---

## Ordered work breakdown

Each task is ≤ ~half a day unless noted. Dependencies are stated per task; within a task the file list
is exhaustive.

### T1 — Make `Modules.Abstractions` Avalonia-free (½ d) · *blocks T7*

**Create** `src/App/DemoViewer.NET.Modules.Abstractions.Ui/DemoViewer.NET.Modules.Abstractions.Ui.csproj`
(contents in Build & wiring).
**Move (git mv, no content change beyond the namespace staying identical):**
- `src/App/DemoViewer.NET.Modules.Abstractions/WorkspaceTabDescriptor.cs` → the new project.
- `src/App/DemoViewer.NET.Modules.Abstractions/IWorkspaceModule.cs` → the new project (it returns
  `WorkspaceTabDescriptor`).

**Modify** `src/App/DemoViewer.NET.Modules.Abstractions/DemoViewer.NET.Modules.Abstractions.csproj`:
remove `<PackageReference Include="Avalonia"/>`; update the leading comment (it currently claims the
Avalonia reference is deliberate) to state the new split.
**Modify** `src/App/DemoViewer.NET/DemoViewer.NET.csproj`: add a `ProjectReference` to
`…Modules.Abstractions.Ui`.
**Modify** `DemoViewer.NET.slnx`: add the new project under `/src/App/`.

No `using` statements change anywhere — the moved types keep namespace
`DemoViewer.NET.Modules.Abstractions`. Verify with `dotnet build src/App/DemoViewer.NET.Desktop`.
Call sites that must keep compiling untouched: `BuiltInTabsModule.cs:66,79,89,98,112,122,132`,
`HighlightsModule.cs:59`, `PlaceholderModule.cs:43`, `Playback2D/Playback2DModule.cs:45`,
`RuleWorkbench/RuleWorkbenchModule.cs:48`, `Views/MainView.axaml:253`, and the four App.Tests files
that touch `ActiveContent`/`ViewFactory`.

### T2 — Create the Core project; move the two proven structs (½ d) · *blocks T3–T6*

**Create** `src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj`.
**Add** `<PackageVersion Include="SkiaSharp" Version="2.88.9"/>` to `Directory.Packages.props`
(see the version-policy note in Build & wiring).
**Move verbatim** (change *only* the namespace line; the design says verbatim and it means it):
- `src/App/DemoViewer.NET/Modules/Playback2D/ViewportTransform.cs` →
  `src/Playback2D/DemoViewer.NET.Playback2D.Core/ViewportTransform.cs`, namespace
  `DemoViewer.NET.Playback2D.Core`. All eight properties, `EffectiveScale`, `WorldToScreen`,
  `ScreenToWorld`, `Fit`, `WithViewport`, `WithPanDelta`, `ZoomAbout` unchanged, including the
  `minZoom = 0.05, maxZoom = 40.0` defaults and the `margin = 0.08` default.
- `src/App/DemoViewer.NET/Modules/Playback2D/SliceCamera.cs` →
  `src/Playback2D/DemoViewer.NET.Playback2D.Core/SliceCamera.cs`, same namespace change. `Current`,
  `ManualOverride`, `StepToward`, `IsSettledAt(target, epsilonPixels = 0.75)` and the private `Lerp`
  unchanged.

**Modify** `src/App/DemoViewer.NET/DemoViewer.NET.csproj`: `ProjectReference` to Core.
**Modify** the three App files that reference the structs — add
`using DemoViewer.NET.Playback2D.Core;` to their `#region` using block:
`src/App/DemoViewer.NET/Modules/Playback2D/Playback2DViewport.cs` (uses `ViewportTransform`,
`SliceCamera`, incl. `internal ViewportTransform PrimaryCameraTransform` at `:188`).
**Modify** `DemoViewer.NET.slnx`: new `<Folder Name="/src/Playback2D/">` with the Core project.

**No type-forwarding shims.** The old files are deleted, not left behind.

### T3 — Move the draw-state value types to Core (½ d) · *needs T2; blocks T7*

**Move** (namespace → `DemoViewer.NET.Playback2D.Core`, otherwise unchanged), from
`src/App/DemoViewer.NET/Modules/Playback2D/` to `src/Playback2D/DemoViewer.NET.Playback2D.Core/`:

| File | Types |
|---|---|
| `PlayerMarker.cs` | `RingState` enum, `PlayerMarker` record struct |
| `AreaEffect.cs` | `AreaEffectKind` enum, `AreaEffect` record struct |
| `GrenadeTrail.cs` | `GrenadeKind` enum, `GrenadeTrailPoint` record struct, `GrenadeTrail` class |
| `BombMarker.cs` | `BombMarker` record struct |

`PlayerMarker.cs` currently has `using DemoViewer.NET.Modules.Abstractions;` for an `IPlayerState`
`<see cref>` in its doc comment — replace that cref with plain `<c>IPlayerState</c>` text so Core keeps
zero non-Skia references.

**Modify — add `using DemoViewer.NET.Playback2D.Core;`** to every file that names these types. Exhaustive
list (find with `rg 'PlayerMarker|AreaEffect|GrenadeTrail|BombMarker|RingState|GrenadeKind'`):
- `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs`
- `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DViewport.cs`
- `src/App/DemoViewer.NET/Modules/Playback2D/RingStateTracker.cs` (moves in T7; add the using now)
- App.Tests: `Playback2DAreaEffectsTests.cs`, `Playback2DAreaEffectsRealDemoTests.cs`,
  `Playback2DTrajectoryTests.cs`, `Playback2DTrajectoryRealDemoTests.cs`, `GrenadeTrailFloorSplitTests.cs`,
  `Playback2DBombRingTests.cs`, `Playback2DBombTimerTests.cs`, `Playback2DDeadMarkerTests.cs`,
  `Playback2DInterpolationTests.cs`, `Playback2DRealDemoRenderTests.cs`, `ZRadarRenderTests.cs`,
  `RingStateTrackerTests.cs`, `Playback2DCameraModeTests.cs`, `ViewportTransformTests.cs`,
  `SliceCameraTests.cs`, `ZFloorValidationProbe.cs`
  (the last three move out entirely in T10 — add the using anyway so the tree builds between tasks).

### T4 — Define the frame contract (½ d) · *needs T3; blocks T7, T9*

**Create**, all in `src/Playback2D/DemoViewer.NET.Playback2D.Core/`:
`SceneTime.cs`, `RenderPurpose.cs`, `Scene2DFrame.cs`, `SceneMapInfo.cs`, `SceneGameInfo.cs`,
`SceneVision.cs`, `KillFeedRow.cs`, `WorldBounds.cs`, `MapRadarImage.cs`, `ScenePalette.cs`.
Signatures are in **Public API contracts** below. Field enumeration is justified there against the
current draw state consumed by `Playback2DViewport.DrawSection` (`Playback2DViewport.cs:863-930`) and
`RebuildSightlines` (`:932-`).

### T5 — Render surface providers + PNG out (½ d) · *needs T2; blocks T10*

**Create** `RenderBackend.cs`, `IRenderSurfaceProvider.cs`, `CpuSurfaceProvider.cs`, `SceneRenderer.cs`
in Core. `CpuSurfaceProvider` wraps `SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888,
SKAlphaType.Premul))`; `Flush` is a no-op; `Dispose` is a no-op (it owns no GPU context) but is
implemented so the interface contract holds. `SceneRenderer.WritePng` goes
`SKSurface.Snapshot()` → `SKImage.Encode(SKEncodedImageFormat.Png, 100)` → `SKData.SaveTo(stream)`.

### T6 — Layer seam + compositor + smoke layer (½ d) · *needs T4, T5; blocks T10*

**Create** `LayerSlot.cs`, `LayerCacheHint.cs`, `ISceneLayer.cs`, `SceneRenderContext.cs`,
`SceneCompositor.cs`, and `Layers/DebugGridLayer.cs` in Core.
`ISceneLayer` **must match design §5.2 exactly** — see contracts. `DebugGridLayer` is the "trivial
smoke layer" the exit criterion needs: it fills the pane with `palette.Background`, strokes the
world-space grid at 512u (`GridStepWorld`, matching `Playback2DViewport.cs:50`) through
`ctx.Transform`, and draws one filled 12px disc per `frame.Markers` entry in the team colour. No text,
no fonts — see the Linux/fontconfig note in Build & wiring. It is `internal` to Core with
`InternalsVisibleTo` for the test project, so it cannot become a production dependency.

### T7 — Create Pipeline; move `RingStateTracker`; extract `SceneFrameBuilder` (1 d) · *needs T1, T3, T4*

**Create** `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj`.
**Move** `src/App/DemoViewer.NET/Modules/Playback2D/RingStateTracker.cs` →
`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/RingStateTracker.cs`, namespace
`DemoViewer.NET.Playback2D.Pipeline`. Public surface is unchanged: `RingStateTracker(int decayFrames = 8)`,
`int DecayFrames`, `void Reset()`, `(RingState State, double Alpha) Evaluate(int slot, int frameIndex,
bool alive, float flashDuration, int health, int shotsFired)`.

**Create** `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/SceneFrameBuilder.cs` by lifting, in
order, from `Playback2DTabViewModel.cs`:

| Lifted from | Lines | Becomes |
|---|---|---|
| `BuildFrame` marker loop | `:759`, `:777-839` | `SceneFrameBuilder.Build` marker pass |
| `UpdateGameInfo` | `:846-929` | private `BuildGameInfo` → `SceneGameInfo` record |
| `ReadMapBoundsOnce` | `:939-` | private, caches into `SceneMapInfo` |
| `ReadSectionHeightsOnce` | `:954-` | private, caches into `SceneMapInfo` |
| `UpdateAreaEffects` | `:1006-` | private, fills the pooled `AreaEffect` list |
| `UpdateTrajectories` | `:1049-` | private, owns the `_trails` dictionary + prune list |
| `UpdateBombTimers` | `:1145-` | private, returns the `BombMarker?` + bomb clock fields |
| `CorrectedCurtime`, `FormatClock` | `:1289`, `:1292` | private helpers |
| `ReadInt`/`ReadFloat`/`ReadBool`/`ReadIntOr`/`IsAlive` entity helpers | scattered | private static helpers |
| the constants block | `:30-49` | `private const` on the builder (`SmokeRadiusWorld` 144, `FireCellRadiusWorld` 28, `MaxInfernoCells` 64, `TrailFadeSeconds` 2, `TrailJumpThreshold` 64, `MaxTrailPoints` 256, `FallbackRoundSeconds` 115, `DefaultC4Timer` 40, `MaxMinimapSections` 8) |
| `_grenadeProjectileClasses` | `:58-61` | `private static readonly string[]` on the builder |
| the trail-clear-on-jump guard | `:629-637` | `Build`'s discontinuity handling (`SceneTime.IsDiscontinuity`) |
| the ring-tracker/last-known-pos reset on backward seek | `:623-627` | `Build`'s discontinuity handling |

**Stays in the view-model** (panel state, not scene state): `UpdateAttributes` (`:1316`),
`SeedRosterDisplay` (`:469`), `LabelFor` (`:1522`), `BuildKillTimeline` (`:653`),
`UpdateKillFeedWindow` (`:693`), `EnsureMapAsset` (`:510`), `EnsureVisionEngine`, the `ObservableProperty`
fields, `FollowablePlayers`, and the `GameInfo`/`Attributes`/`KillFeed` observable collections.

**Labels:** the builder cannot call `LabelFor` (roster display is VM state). `SceneFrameBuilder.Build`
takes a `Func<int, string> labelForSlot` on the input struct; the VM passes its existing `LabelFor`.

**Kill feed:** the VM keeps ownership; it passes the current window (`_killWindow` slice) into the
builder as `IReadOnlyList<KillFeedRow>` so `Scene2DFrame.KillFeed` is populated for B4's HUD layer.
Add a mapping from `KillFeedEntry` (App) → `KillFeedRow` (Core) in the VM. `KillFeedEntry` itself does
**not** move in B0 (it is bound by XAML DataTemplates).

**Two passes, deliberately:** today `BuildFrame`'s single `foreach (IPlayerState p in players)` loop
interleaves `UpdateAttributes` (panel) with marker construction (scene). After the split the VM loops
once for attributes and the builder loops once for markers. `players.Count ≤ 10-ish`; the extra pass is
free and it is the price of the layering. Note it in the VM comment so a future reader does not
"optimise" it back together.

**Behaviour-identity rule:** no logic changes in this task. Field paths, defaults, ordering, the
`ReadInt(pawn, "m_iHealth", hasPawn ? 100 : 0)` fallback, the dead-marker branch (`:823-838`), the
`m_angEyeAngles` pitch=`.X`/yaw=`.Y` convention (`:800-801`) — all identical.

### T8 — Wire the view-model to the builder (½ d) · *needs T7*

**Modify** `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs`:
- Add `private readonly SceneFrameBuilder _frameBuilder = new();` and
  `private Scene2DFrame _frame = Scene2DFrame.Empty;`.
- `BuildFrame` (`:748`) becomes: seed roster if needed → attributes pass → `_frame =
  _frameBuilder.Build(in input)` → copy the scene results onto the existing public surface.
- The existing public properties must keep their exact shapes so the ~20 App tests and the XAML keep
  working: `Markers` → `_frame.Markers`, `AreaEffects` → `_frame.AreaEffects`, `GrenadeTrails` →
  `_frame.Trails`, `Bomb` → `_frame.Bomb`, `SectionHeights` → `_frame.Map.SectionHeights`, `MapBounds`
  → `_frame.Map.NetworkedBounds` re-shaped to the existing
  `(double MinX, double MinY, double MaxX, double MaxY)?` tuple.
- `GameInfo` (the `ObservableObject`) is updated by copying the fields off `_frame.GameInfo` —
  `Phase`, `BombState`, `RoundNumber`, `RoundSeconds`, `RoundTime`, `BombTicking`, `DefuseInProgress`,
  `DefuseKitNote`, `DefuseSeconds`, `DefuseTime`, `TScore`, `CtScore`. `RoundTimeNote` keeps its
  constant default.
- Add `public Scene2DFrame CurrentFrame => _frame;` — B1's `Scene2DHost` and the B0 golden capture read
  it.
- Delete the now-empty private methods listed as "lifted" in T7, and the `_markers`, `_areaEffects`,
  `_trails`, `_trailsToPrune`, `_trailViews`, `_lastKnownPos`, `_ringTracker`, `_sectionHeights`,
  `_sectionHeightsRead`, `_roundsPlayed` fields — **except** `_roundsPlayed`, which
  `UpdateAttributes` reads for ADR; it now comes from `_frame.GameInfo.RoundsPlayed`.

**Gate:** every existing `Playback2D*Tests` in App.Tests passes unmodified except for the added `using`
from T3. Run them before moving on (command below).

### T9 — `SceneFixture` (½ d) · *needs T4, T7*

**Create** `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/SceneFixture.cs` and
`SceneFixtureSerializer.cs`. `System.Text.Json`, source-generated context
(`[JsonSerializable(typeof(SceneFixtureDto))]`) so it is trimming/WASM-safe. `SKImage` is not
serialized: `SceneMapInfo.Radars` round-trips as `{ Name, Bounds, MinZ, MaxZ }` and the image is
re-attached by `MapAssetPipeline` at load (B1) or left null. Unknown JSON members are captured into a
`Dictionary<string, JsonElement>` and re-emitted on write (§5.4's tolerant reader, enforced by a test).

**Create** `tests/fixtures/playback2d/README.md` describing the two families (D8) and the regeneration
command. Directory layout is C1's (correction 6): `scenes/`, `goldens/cpu/`, `annotations/`,
`manifest.json`.
**Create** three synthetic fixtures by hand under `tests/fixtures/playback2d/scenes/`:
`synthetic-empty.scene.json`, `synthetic-tenplayers.scene.json` (10 markers spread over ±2000u, mixed
teams, one `RingState.Dead`, one `Blinded`), `synthetic-utility.scene.json` (2 smokes, 6 fire cells,
2 trails of 40 points, a planted bomb at 0.42 detonation fraction with a defuse in progress).

### T10 — Test project; move the struct tests; direct-execution suite (½ d) · *needs T5, T6, T9*

**Create** `src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Tests.csproj`.
**Move** from `src/App/DemoViewer.NET.App.Tests/` (they are pure math, no Avalonia — this proves Core is
directly testable per §11):
- `ViewportTransformTests.cs` → namespace `DemoViewer.NET.Playback2D.Tests`
- `SliceCameraTests.cs` → same
- `RingStateTrackerTests.cs` → same (RingStateTracker moved to Pipeline in T7)

**Create** the new test classes listed in the Test plan.

### T11 — Architecture + banned-API tests (½ d) · *needs T10*

**Create** `src/Playback2D/DemoViewer.NET.Playback2D.Tests/ArchitectureTests.cs` and
`BannedApiTests.cs`. Implementation notes are in the Test plan.

### T12 — Golden capture harness (1 d) · *needs T8*

**Create** `src/App/DemoViewer.NET.App.Tests/Playback2DGoldenCaptureTests.cs` — pattern-matched on
`ZRadarRenderTests.cs`. **Create** `tests/fixtures/playback2d/goldens/cpu/` (correction 6 — *not*
`tests/goldens/`) and commit the captured PNGs + their paired `scenes/*.scene.json`. **Create**
`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Goldens/{GoldenImageComparer,GoldenTolerance,
GoldenComparison}.cs` (correction 8 — the one comparator B1/C1/C2 all call; signatures in C1's plan)
and `scripts/update-playback2d-goldens.sh`.

### T13 — CI, docs, scripts (½ d) · *needs T11, T12*

**Modify** `.github/workflows/ci.yml`, `DemoViewer.NET.slnx`, `scripts/test-app-suite.sh` (append the
new project), and add `docs/playback2d-v2/plans/B0-notes.md` only if implementation deviates from this
plan (do **not** write a summary/report file otherwise).

---

## Public API contracts

**BINDING for B1–B5 and C1–C2.** Where the design §5 gives a sketch, it is reproduced exactly.
All types are `public` unless marked. All XML docs omitted here for brevity — write them (the repo sets
`GenerateDocumentationFile=true`).

### Core — `namespace DemoViewer.NET.Playback2D.Core`

#### Time and purpose (design §5.1, verbatim)

```csharp
public readonly record struct SceneTime(
    int Tick, int FrameIndex, double DemoSeconds,   // DemoSeconds = ServerTick / tickRate − clockBase
    double DeltaSeconds,                            // injected: real dt interactive, 1/fps on export
    bool IsDiscontinuity);                          // seek/jump — layers reset smoothing/trails

public enum RenderPurpose { Interactive, Export, Thumbnail }
```

#### Moved value types (verbatim from `Modules.Playback2D`)

```csharp
public readonly struct ViewportTransform            // members unchanged — see T2
public struct SliceCamera                           // members unchanged — see T2
public enum RingState { Team, Shooting, TakingDamage, Blinded, Dead }
// SteamId is the ONE non-verbatim addition (correction 4): design §5.4 anchors annotations by
// SteamId because slots recycle, and B4's CameraScript.FollowPlayer(steamId) needs the same join.
// 0 = unresolved; SceneFrameInput.SteamIdForSlot fills it.
public readonly record struct PlayerMarker(
    int Slot, int Team, float WorldX, float WorldY, float WorldZ, float YawDegrees,
    RingState Ring, double RingAlpha, string Label, bool IsAlive,
    float PitchDegrees = 0, float DuckAmount = 0, ulong SteamId = 0);
public enum AreaEffectKind { Smoke, Fire }
public readonly record struct AreaEffect(AreaEffectKind Kind,
    float WorldX, float WorldY, float WorldZ, float WorldRadius);
public enum GrenadeKind { He, Flash, Smoke, Molotov, Decoy }
public readonly record struct GrenadeTrailPoint(float X, float Y, float Z);
public sealed class GrenadeTrail
{
    public GrenadeKind Kind { get; init; }
    public List<GrenadeTrailPoint> Points { get; }
    public int LastTick { get; set; }
    public double Alpha { get; set; }
    public float CurrentZ { get; }
}
public readonly record struct BombMarker(float WorldX, float WorldY, float WorldZ,
    double DetonationFraction, bool BeingDefused, double DefuseFraction);
```

#### New frame contract

```csharp
public readonly record struct WorldBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width { get; }
    public double Height { get; }
    public static WorldBounds Union(WorldBounds a, WorldBounds b);
    public static readonly WorldBounds Default;   // ±3000 on both axes (Playback2DViewport.cs:49)
}

public sealed class MapRadarImage
{
    public required string Name { get; init; }        // bundle image file name
    public SKImage? Image { get; init; }              // null when undecoded / unavailable
    public required WorldBounds Bounds { get; init; } // bundle world bounds the image spans
    public double MinZ { get; init; }
    public double MaxZ { get; init; }
}

public sealed class SceneMapInfo
{
    public string MapName { get; init; } = "";
    public WorldBounds? NetworkedBounds { get; init; }        // m_vMinimapMins / m_vMinimapMaxs
    public WorldBounds ObservedBounds { get; init; } = WorldBounds.Default;
    public IReadOnlyList<double>? SectionHeights { get; init; } // m_MinimapVerticalSectionHeights
    public IReadOnlyList<MapRadarImage> Radars { get; init; } = [];
    public static readonly SceneMapInfo Unknown;
}

public readonly record struct SceneGameInfo(
    string Phase,            // "Warmup" | "Freeze" | "Live" | "—"
    string BombState,        // "Defused" | "Planted" | "Dropped" | "—"
    int RoundNumber,         // 1-based; 0 = unknown
    int RoundsPlayed,        // m_totalRoundsPlayed; -1 = unknown (UpdateAttributes reads this for ADR)
    double RoundSeconds,     // NaN when no countdown
    string RoundTime,
    bool BombTicking,
    bool DefuseInProgress,
    string DefuseKitNote,
    double DefuseSeconds,    // NaN when not defusing
    string DefuseTime,
    int TScore,
    int CtScore)
{
    public static readonly SceneGameInfo Empty;
}

public readonly record struct KillFeedRow(
    int Tick, string Attacker, string? Assister, string Victim, string Weapon,
    bool Headshot, bool Penetrated, bool NoScope, bool ThroughSmoke,
    bool AttackerBlind, bool AttackerInAir, bool AssistedFlash);

public readonly record struct ConePoint(float X, float Y);

public sealed class VisionCone
{
    public int Slot { get; init; }
    public int Team { get; init; }
    public float ApexX { get; init; }
    public float ApexY { get; init; }
    public float ApexZ { get; init; }
    public IReadOnlyList<ConePoint> Fan { get; init; } = [];   // clipped ray ends, ordered by angle
}

public readonly record struct Sightline(
    int ViewerSlot, int ViewerTeam,
    float X0, float Y0, float Z0,
    float X1, float Y1, float Z1);

public sealed class SceneVision
{
    public bool IsAvailable { get; init; }                     // engine loaded for this map
    public IReadOnlyList<VisionCone> Cones { get; init; } = [];
    public IReadOnlyList<Sightline> Sightlines { get; init; } = [];
    public static readonly SceneVision Off;
}

/// Valid until the next Build() on the producing SceneFrameBuilder — see decision D6.
public sealed class Scene2DFrame
{
    public SceneTime Time { get; init; }
    public IReadOnlyList<PlayerMarker> Markers { get; init; } = [];
    public IReadOnlyList<AreaEffect> AreaEffects { get; init; } = [];
    public IReadOnlyList<GrenadeTrail> Trails { get; init; } = [];
    public BombMarker? Bomb { get; init; }
    public IReadOnlyList<KillFeedRow> KillFeed { get; init; } = [];
    public SceneGameInfo GameInfo { get; init; } = SceneGameInfo.Empty;
    public SceneMapInfo Map { get; init; } = SceneMapInfo.Unknown;
    public SceneVision Vision { get; init; } = SceneVision.Off;
    public int FollowSlot { get; init; } = -1;
    public static readonly Scene2DFrame Empty;
}
```

**Field justification (assignment constraint (c) — the seven B1 layers against the current draw state):**

| B1 layer | Reads | Current source it replaces |
|---|---|---|
| `RadarLayer` | `Map.Radars`, `Map.NetworkedBounds` | `TryDrawRadar` / `ResolveRadarImage` (`Playback2DViewport.cs`) |
| `TrailLayer` | `Trails`, `Time.Tick`, `Time.IsDiscontinuity` | `_vm.GrenadeTrails` → `DrawTrajectory` (`:878-884`) |
| `AreaEffectLayer` | `AreaEffects` | `_vm.AreaEffects` → `DrawAreaEffect` (`:887-896`) |
| `VisionLayer` | `Vision.Cones`, `Vision.Sightlines` | `DrawViewCones` / `DrawSightlines` (`:900-904`), `RebuildSightlines` (`:932`) |
| `MarkerLayer` | `Markers` (all 12 members; `PitchDegrees`/`DuckAmount` feed nothing in B1 but stay for vision recompute), `FollowSlot` | `_vm.Markers` → `DrawMarker` (`:906-914`) |
| `BombLayer` | `Bomb` | `_vm.Bomb` → `DrawBomb` (`:917-921`) |
| `AnnotationLayer` (B2) | `Time.Tick` only; the document is layer-owned state | n/a |
| `ClockLayer` (B4) | `GameInfo` | XAML HUD binding to `vm.GameInfo` |
| `KillFeedLayer` (B4) | `KillFeed`, `Time.Tick` | XAML HUD binding to `vm.KillFeed` |
| camera rigs (B1) | `Map.NetworkedBounds`, `Map.ObservedBounds`, `Markers`, `FollowSlot` | `TryComputeTarget`/`TryFitAlive`/`TryFollow` (`:706-817`) |
| `MapSpace` factory (B1) | `Map.SectionHeights`, marker Zs | `_floors.SetSectionHeights` / `SetAuthoritativeFloors` (`:335-340`) |

Deliberately absent: overlay toggles (D5), floor slices (D3), the theme palette (compositor state, see
`ScenePalette` below), `LoadedMapAsset` (Avalonia `Bitmap`, replaced by `MapRadarImage.Image`).

#### Palette

```csharp
public readonly record struct ScenePalette(
    SKColor Background, SKColor MinorGrid, SKColor MajorGrid, SKColor Label,
    SKColor TeamT, SKColor TeamCt, SKColor Neutral,
    SKColor SightlineT, SKColor SightlineCt,
    SKColor ConeT, SKColor ConeCt, SKColor ConeNeutral,
    SKColor RingShooting, SKColor RingDamage, SKColor RingBlinded, SKColor RingDead,
    SKColor Bomb, SKColor BombTrack, SKColor BombDetonation, SKColor BombDefuse,
    SKColor Smoke, SKColor SmokeStroke, SKColor Fire,
    SKColor TrailHe, SKColor TrailFlash, SKColor TrailSmoke, SKColor TrailMolotov, SKColor TrailDecoy,
    SKColor MarkerRingT, SKColor MarkerRingCt, SKColor MarkerRingNeutral,
    SceneStrokeWidths Strokes)
{
    /// The Dark-variant fallbacks currently hard-coded in Playback2DViewport.BuildPalette (:229-260).
    public static readonly ScenePalette Dark;
}

public readonly record struct SceneStrokeWidths(
    float MinorGrid = 1f, float MajorGrid = 1f, float Sightline = 1f,
    float BombTrack = 2f, float BombDetonation = 3f, float BombDefuse = 3f, float SmokeStroke = 1f);
```

`ScenePalette.Dark` must reproduce, colour for colour, the fallback hexes at
`Playback2DViewport.cs:229-260` (`#15181C`, `#22272E`, `#2E3742`, `#9AA4AF`, `#E0A030`, `#4A90D9`,
`#888888`, `#70E0A030`, `#704A90D9`, `#3CE0A030`, `#3C4A90D9`, `#2C888888`, `#FFD400`, `#F44336`,
`#FFFFFFFF`, `#555B62`, `#F03A2E`, `#40FFFFFF`, `#FF5040`, `#40C4FF`, `#66AEB6BD`, `#88C8CED4`,
`#78FF6A1A`, `#FF5252`, `#FFE082`, `#B0BEC5`, `#FF7043`, `#81C784`, `#C8881F`, `#357ABD`, `#666666`).
In B1 the App builds a `ScenePalette` from its theme tokens; `CanvasPalette` then becomes a factory.

#### Layers and compositor (design §5.2 — `ISceneLayer` verbatim)

```csharp
public enum LayerSlot { Underlay, World, Overlay, Hud }        // coarse z-band
public enum LayerCacheHint { Static, PerCamera, Dynamic }      // declared, auditable caching

// namespace DemoViewer.NET.Playback2D.Core.Compositing (correction 1)
public interface ISceneLayer : IDisposable
{
    string Id { get; }                    // stable key: feature gates, settings, layer panel
    LayerSlot Slot { get; }
    int Order { get; }                    // sort key within slot
    LayerCacheHint Cache { get; }         // Static/PerCamera → recorded into SKPicture
    bool IsEnabled { get; set; }
    // Bumped by the layer when its cacheable content changes; ignored when Cache is Dynamic.
    // Declared here (not added by B1) so there is one interface shape for the whole track.
    int ContentVersion { get; }
    // UI-thread pre-render step; true = keep the self-terminating RAF loop armed.
    bool Advance(in SceneTime time, Scene2DFrame frame);
    // Pure draw: reads caches built in Advance, must not mutate. Called once per pane.
    void Render(SKCanvas canvas, SceneRenderContext ctx);
}

// Member names are B1's (correction 2). B1 EXTENDS this with `LevelPaneSnapshot Pane` and
// `int LevelIndexFor(double worldZ)`; B3 adds `MapSpace Levels` + `LevelCrossingTracker
// LevelCrossings`. Extending means adding members to THIS type, never declaring a second one.
public readonly record struct SceneRenderContext(
    Scene2DFrame Frame,
    SceneTime Time,
    ViewportTransform Transform,   // world → pane-local screen
    SKRect PaneBounds,             // pane-local bounds; origin is always (0,0)
    int LevelIndex,                // -1 = single pane / all levels (matches today's sliceIndex < 0)
    double LevelMinZ,
    double LevelMaxZ,
    RenderPurpose Purpose,
    ScenePalette Palette,
    float RenderScaling)           // device px per DIP; 1.0 offscreen
{
    public bool IsSingleLevel => LevelIndex < 0;
    public bool BelongsHere(double worldZ);   // LevelIndex < 0 → always true
}

public sealed class SceneCompositor : IDisposable
{
    public IReadOnlyList<ISceneLayer> Layers { get; }        // sorted by (Slot, Order, Id)
    public void Add(ISceneLayer layer);                      // throws on duplicate Id
    public bool Remove(string layerId);
    public ISceneLayer? Find(string layerId);
    public void SetEnabled(string layerId, bool enabled);
    public bool Advance(in SceneTime time, Scene2DFrame frame);   // OR of enabled layers' Advance
    public void Render(SKCanvas canvas, in SceneRenderContext ctx); // enabled layers, in order
    public void Dispose();
}
```

#### Render surfaces (design §5.8, verbatim interface)

```csharp
public enum RenderBackend { CpuRaster, OpenGl, Angle, Vulkan }

public interface IRenderSurfaceProvider : IDisposable
{
    RenderBackend Backend { get; }
    SKSurface CreateSurface(SKSizeI size);          // RGBA8888, premul
    void Flush(SKSurface surface);                  // GPU: GRContext.Flush + submit; CPU: no-op
}

public sealed class CpuSurfaceProvider : IRenderSurfaceProvider
{
    public RenderBackend Backend => RenderBackend.CpuRaster;
    public SKSurface CreateSurface(SKSizeI size);
    public void Flush(SKSurface surface);
    public void Dispose();
}

public sealed class SceneRenderer
{
    public SceneRenderer(IRenderSurfaceProvider surfaces);
    /// Single-pane render — B1 adds a pane-list overload rather than changing this (decision D9).
    public SKImage Render(SceneCompositor compositor, Scene2DFrame frame, in SceneTime time,
        in SceneRenderContext ctx, SKSizeI size);
    public static void WritePng(SKImage image, Stream destination);
    public static void WritePng(SKImage image, string path);
}
```

### Pipeline — `namespace DemoViewer.NET.Playback2D.Pipeline`

```csharp
public sealed class RingStateTracker    // moved verbatim; surface unchanged (see T7)

public readonly ref struct SceneFrameInput
{
    public required IReadOnlyList<IPlayerState> Players { get; init; }
    public required IReadOnlyEntityView Entities { get; init; }
    public required int FrameIndex { get; init; }
    public required int Tick { get; init; }
    public required int TickRate { get; init; }
    public required double CurtimeSeconds { get; init; }        // IModuleContext.CurtimeSeconds
    public required Func<int, string> LabelForSlot { get; init; }
    public string MapName { get; init; }
    public IReadOnlyList<KillFeedRow> KillFeed { get; init; }
    public IReadOnlyList<MapRadarImage> Radars { get; init; }
    public SceneVision Vision { get; init; }                    // B1 fills this; SceneVision.Off in B0
    public int FollowSlot { get; init; }
}

public sealed class SceneFrameBuilder
{
    public SceneFrameBuilder(int ringDecayFrames = 8);
    /// Returns a frame valid until the next Build on this instance (decision D6).
    public Scene2DFrame Build(in SceneFrameInput input);
    /// Clears trails, ring history and last-known positions — call on demo reset / backward seek.
    public void Reset();
    /// Running observed world extent (Map-mode fallback). Only ever widened; cleared by Reset.
    public WorldBounds ObservedBounds { get; }
}

public sealed record SceneFixture
{
    public string SchemaVersion { get; init; }     // "playback2d-scene/1"
    public required Scene2DFrame Frame { get; init; }
    public SceneTime Time { get; init; }           // correction 7 — C1 renders at a stated time
    public ViewportTransform Camera { get; init; }
    public SKSizeI Size { get; init; }             // correction 7 — default render/golden size
    public string? MapName { get; init; }          // correction 7 — MapAssetPipeline lookup key
    public string? MapVersion { get; init; }       // correction 7 — bundle.json mapVersion CRC
    public JsonElement? Annotations { get; init; } // null until B2 (decision D7)
    public string? SourceDemoId { get; init; }
    public string? Notes { get; init; }

    public static SceneFixture Load(string path);  // == SceneFixtureSerializer.ReadFile
    public void Save(string path);                 // == SceneFixtureSerializer.WriteFile
}

public static class SceneFixtureSerializer
{
    public static SceneFixture Read(Stream source);
    public static SceneFixture ReadFile(string path);
    public static void Write(SceneFixture fixture, Stream destination);
    public static void WriteFile(SceneFixture fixture, string path);
}
```

### App — changed surface

```csharp
// src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs
public Scene2DFrame CurrentFrame { get; }   // NEW — B1's Scene2DHost + B0's golden capture read it
```
Everything else on the VM keeps its current shape (`Markers`, `AreaEffects`, `GrenadeTrails`, `Bomb`,
`GameInfo`, `SectionHeights`, `MapBounds`, `AuthoritativeFloors`, `MapAsset`, `VisionEngine`,
`Attributes`, `KillFeed`, `FollowablePlayers`, `PushCount`, `FrameUpdated`, `FollowSlotChanged`, and
all `[ObservableProperty]` fields).

---

## Test plan

Two suites. **Direct-execution** (`DemoViewer.NET.Playback2D.Tests`) has no Avalonia at all and runs on
any machine including CI. **Headless-Avalonia** (`DemoViewer.NET.App.Tests`) keeps the existing
`HeadlessSession` and is the only place the current control is exercised.

### Suite 1 — direct execution, `src/Playback2D/DemoViewer.NET.Playback2D.Tests`

| Class | Cases | Fixtures |
|---|---|---|
| `ViewportTransformTests` | *moved unchanged from App.Tests* — round-trip, fit centring, aspect preservation, zoom-about-cursor invariance | none |
| `SliceCameraTests` | *moved unchanged* — half-factor lerp, factor-1 landing, settle detection, manual-override carry | none |
| `RingStateTrackerTests` | *moved unchanged* — first-observation baseline, damage flash + decay, reset-on-backward-seek | none |
| `CpuSurfaceProviderTests` | `CreateSurface_ReturnsRgba8888Premul_OfRequestedSize`; `Clear_ThenReadPixels_ReturnsClearColor`; `Flush_IsNoOp_AndDoesNotThrow`; `Dispose_IsIdempotent` | none |
| `SceneRendererTests` | `Render_EmptyCompositor_ProducesBackgroundOnlyImage`; `WritePng_ProducesDecodablePng_OfRequestedSize`; `Render_Twice_ProducesByteIdenticalPixels` (the §11 determinism gate, CPU backend) | none |
| `SceneCompositorTests` | `Layers_SortBy_SlotThenOrderThenInsertion`; `AddLayer_DuplicateId_Throws`; `Advance_ReturnsTrue_WhenAnyEnabledLayerReturnsTrue`; `Render_SkipsDisabledLayers`; `Dispose_DisposesAllLayers` | none |
| `SceneSmokeRenderTests` | **the exit-criterion test.** `DebugGridLayer_RendersFixtureToPng_WithZeroAvaloniaLoaded` — load `synthetic-tenplayers.scene.json`, `SceneRenderer.Render` at 640×360 on `CpuSurfaceProvider`, write PNG to the test output dir, assert non-background pixel count > 2000 **and** that `AppDomain.CurrentDomain.GetAssemblies()` contains no assembly whose name starts with `Avalonia` | `synthetic-tenplayers.scene.json` |
| `SceneFixtureTests` | `RoundTrip_PreservesEveryFrameField` (reflection-walk every `Scene2DFrame` member, assert equality); `Read_UnknownMember_IsPreservedOnWrite`; `Read_MissingOptionalMember_UsesDefault`; `ReadFile_EachCommittedFixture_Parses` (data-driven over `tests/fixtures/playback2d/*.scene.json`) | all three synthetic |
| `SceneFrameBuilderTests` | `Markers_MatchPlayerState_ForAliveAndDead` (dead-marker branch holds last-known position); `AreaEffects_DetonatedSmokesAndBurningCellsOnly` (port the `FakeCtx`/`View`/`Ent` fakes from `Playback2DAreaEffectsTests.cs:20-60`); `Trails_AccumulateThenFadeThenPrune`; `Trails_ClearOnDiscontinuity`; `Bomb_DetonationFraction_TracksC4Blow`; `GameInfo_RoundClock_UsesNetworkedRoundTime`; `Build_TwiceInARow_AllocatesZeroBytes` (`GC.GetAllocatedBytesForCurrentThread()` delta after 64 warm-up builds must be 0) | none (in-memory fakes) |
| `ArchitectureTests` | `Core_ReferencesOnlySkiaSharpAndBcl`; `Core_TransitiveClosure_ContainsNoAvalonia`; `Pipeline_TransitiveClosure_ContainsNoAvalonia`; `ModulesAbstractions_TransitiveClosure_ContainsNoAvalonia` (D1's guard) | none |
| `BannedApiTests` | `Core_ContainsNo_DateTimeNow_Stopwatch_Or_Random`; `Pipeline_ContainsNo_DateTimeNow_Stopwatch_Or_Random` | none |

**`ArchitectureTests` implementation:** walk `Assembly.GetReferencedAssemblies()` recursively from
`typeof(Scene2DFrame).Assembly` / `typeof(SceneFrameBuilder).Assembly` with a visited set, resolving
each via `Assembly.Load` and skipping names that fail to resolve. Assert no resolved name starts with
`"Avalonia"`. For Core additionally assert the direct reference set ⊆ `{ SkiaSharp, System.*,
netstandard, mscorlib }`.

**`BannedApiTests` implementation:** open the assembly file with
`System.Reflection.PortableExecutable.PEReader` + `System.Reflection.Metadata.MetadataReader` (both BCL,
no package). Enumerate `MemberReferences`; for each, resolve the parent `TypeReference`'s
namespace + name and flag: `System.DateTime::get_Now|get_UtcNow|get_Today`,
`System.DateTimeOffset::get_Now|get_UtcNow`, any member of `System.Diagnostics.Stopwatch`, any member of
`System.Random`, `System.Environment::get_TickCount|get_TickCount64`, and
`System.Threading.Thread::Sleep`. Also scan `TypeReferences` for `Stopwatch`/`Random` to catch field
declarations. Fail with the offending member's full name so the diagnosis is one line.

**Run:** `dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release`
(TUnit test projects are `Exe`; `dotnet run --project …` also works).

### Suite 2 — headless Avalonia, `src/App/DemoViewer.NET.App.Tests`

**Existing tests that must keep passing unmodified** (beyond the T3 `using` additions) — this is the
"VM behaviour identical" gate for T7/T8: `Playback2DAreaEffectsTests`,
`Playback2DAreaEffectsRealDemoTests`, `Playback2DTrajectoryTests`, `Playback2DTrajectoryRealDemoTests`,
`GrenadeTrailFloorSplitTests`, `Playback2DBombRingTests`, `Playback2DBombTimerTests`,
`Playback2DRoundTimerTests`, `Playback2DDeadMarkerTests`, `Playback2DInterpolationTests`,
`Playback2DKillFeedTests`, `Playback2DKillFeedRenderTests`, `Playback2DMapBoundsTests`,
`Playback2DRosterReseedTests`, `Playback2DReloadResyncTests`, `Playback2DAdrTests`,
`Playback2DEventNavTests`, `Playback2DModuleLifecycleTests`, `Playback2DCameraModeTests`,
`Playback2DHeadlessSmokeTests`, `Playback2DRealDemoRenderTests`, `ZRadarRenderTests`,
`FloorAssetConsumptionTests`, `Playback2DFloorThresholdProbeTests`, `ZFloorValidationProbe`,
plus `ModuleFrameworkPhase3Tests`, `ModuleTabPersistenceTests`, `TabFeatureGatingTests` (T1's gate).

**New:** `Playback2DGoldenCaptureTests` — `[NotInParallel]`, `[Category("Integration")]`, modelled on
`ZRadarRenderTests.RenderMapCapture` (`ZRadarRenderTests.cs:106-177`).

Per fixture case: `DemoTestHelper.FindDemoPath(<name>)` → `SkipTestException` if null →
`DemoTestHelper.GetOrParse` → `EntityTracker.ReplayToIndex(target, frames)` → `PlaybackController`
+ `ModuleContext` (roster + map name) → `HeadlessSession.RunOnUi`:
1. construct `Playback2DViewport { DataContext = vm }` in a `Window { Width = 900, Height = 900 }`,
   `window.Show()`, `Dispatcher.UIThread.RunJobs()`;
2. `vm.OnActivated(context)`; force `viewport.Mode = CameraMode.Fit` (the **only** deterministic mode —
   `AdvanceCameras` skips Fit entirely, `Playback2DViewport.cs:616`);
3. `Dispatcher.UIThread.RunJobs()`, `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`, `RunJobs()`;
4. `window.CaptureRenderedFrame()` → save PNG;
5. `SceneFixtureSerializer.WriteFile(new SceneFixture { Frame = vm.CurrentFrame, Camera =
   viewport.PrimaryCameraTransform, SourceDemoId = … }, …)`.

**Determinism:** marker interpolation seeds *on* the player at first appearance
(`Playback2DViewport.cs:659-662`) so the first captured frame has no glide, and `CameraMode.Fit` never
lerps. `AdvanceMarkers` is `internal` and App.Tests already sees internals — assert
`viewport.SmoothedMarkerPosition(slot)` equals the raw marker position for every marker before
capturing, and fail the capture if not.

**Comparison:** via `GoldenImageComparer.Compare(..., GoldenTolerance.DefaultPerceptual)` (correction
8). If `tests/fixtures/playback2d/goldens/cpu/<name>@<w>x<h>.png` exists, compare and fail on
>0.1% differing pixels, writing the actual + `GoldenImageComparer.CreateDiffPng(...)` to
`HeadlessSession.ArtifactDir`. If it does not exist **and** `PB2D_GOLDEN_UPDATE=1`, write it. If it does
not exist and the env var is unset, fail with the regeneration command in the message.

**Fixture cases** (skip individually when the demo is absent): `mirage-duel` (use the mirage demos under
`tests/fixtures/furia-vs-vitality-m1-mirage` / `vitality-vs-fut-m1-mirage`), `nuke-twofloor`
(`003816306022075596881_1029495947.dem`, the Nuke demo `ZRadarRenderTests` uses), `dust2-roundstart`
(`003801777854962729156_0256036251.dem`). Round-start frame selection reuses
`ZRadarRenderTests.FindRoundStartFrame` (`:180-194`) — lift it into a shared internal helper rather
than copying.

**Run:**
```bash
# fast, no demo, no Avalonia — this is the CI gate
dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

# the App suite must be batched — it is single-process and OOM-prone
./scripts/test-app-suite.sh

# just the 2D playback + golden members
dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- --treenode-filter "/*/*/Playback2D*/*"

# regenerate goldens after a deliberate visual change
PB2D_GOLDEN_UPDATE=1 dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release \
  -- --treenode-filter "/*/*/Playback2DGoldenCaptureTests/*"
```

---

## Build & wiring

### `Directory.Packages.props` additions

```xml
<!-- SkiaSharp — the Playback2D v2 render core (docs/playback2d-v2/design.md §3).
     PINNED to the version Avalonia.Skia 11.3.12 resolves. In B1 the on-screen path takes
     Avalonia's ISkiaSharpApiLeaseFeature, which hands back Avalonia's OWN SKCanvas: a different
     SkiaSharp major would make that a different type identity and break the lease outright.
     Bump ONLY in lockstep with the Avalonia version above. -->
<PackageVersion Include="SkiaSharp" Version="2.88.9"/>
<!-- Native Skia for Linux test/CI runners. Correction 9: use the FULL package, not
     …NoDependencies — B1 draws SKTextBlob text and C1's golden corpus contains text layers, and
     swapping the native later silently re-baselines every golden. The CI job installs its one
     system dependency: `sudo apt-get install -y libfontconfig1`. -->
<PackageVersion Include="SkiaSharp.NativeAssets.Linux" Version="2.88.9"/>
```

**Version policy:** SkiaSharp's version is a *derived* pin, not an independent choice — it is whatever
`Avalonia.Skia` resolves. When Avalonia is bumped, re-read the resolved SkiaSharp version from
`artifacts/obj/DemoViewer.NET.App.Tests/project.assets.json` and update both entries together in the
same commit. `TreatWarningsAsErrors` already promotes `NU1608`/`NU1605`, so a skew fails the build
rather than surfacing at runtime.

### `src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!--
      The renderer-agnostic scene core (docs/playback2d-v2/design.md §4). References SkiaSharp and
      NOTHING else — no Avalonia, no parser, no Modules.Abstractions. That constraint is the whole
      value of this assembly (it is what lets export, the CLI and CI render without a window) and it
      is enforced by ArchitectureTests, not by convention.
    -->
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <RootNamespace>DemoViewer.NET.Playback2D.Core</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="SkiaSharp"/>
    </ItemGroup>
    <ItemGroup>
        <InternalsVisibleTo Include="DemoViewer.NET.Playback2D.Pipeline"/>
        <InternalsVisibleTo Include="DemoViewer.NET.Playback2D.Tests"/>
    </ItemGroup>
</Project>
```

### `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!--
      Demo-domain adaptation for the scene core: frame building, map-asset decode, fixtures, and
      (from B4) the private tracker replay and encoder sinks. References Core + the CS2DemoKit parser
      packages + the (now Avalonia-free) module abstractions. Still NO Avalonia — enforced by test.
    -->
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <RootNamespace>DemoViewer.NET.Playback2D.Pipeline</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="CS2DemoKit.Parser"/>
        <PackageReference Include="CS2DemoKit.Analysis"/>
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
        <ProjectReference Include="..\..\App\DemoViewer.NET.Modules.Abstractions\DemoViewer.NET.Modules.Abstractions.csproj"/>
    </ItemGroup>
    <ItemGroup>
        <InternalsVisibleTo Include="DemoViewer.NET.Playback2D.Tests"/>
    </ItemGroup>
</Project>
```

### `src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <RootNamespace>DemoViewer.NET.Playback2D.Tests</RootNamespace>
        <NoWarn>$(NoWarn);CA1707</NoWarn>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="TUnit"/>
        <PackageReference Include="SkiaSharp.NativeAssets.Linux"
                          Condition="'$([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform($([System.Runtime.InteropServices.OSPlatform]::Linux)))' == 'true'"/>
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Pipeline\DemoViewer.NET.Playback2D.Pipeline.csproj"/>
    </ItemGroup>
    <ItemGroup>
        <None Include="..\..\..\tests\fixtures\playback2d\**\*.json" LinkBase="fixtures"
              CopyToOutputDirectory="PreserveNewest"/>
    </ItemGroup>
</Project>
```
Note: this project deliberately does **not** reference `DemoViewer.NET.TestSupport` — that project
would drag the App graph (and Avalonia) into the direct-execution suite and defeat
`SceneSmokeRenderTests`' "no Avalonia assembly is loaded" assertion. If the Linux condition proves
awkward in practice, reference the package unconditionally; it is a no-op on Windows/macOS.

### `src/App/DemoViewer.NET.Modules.Abstractions.Ui/DemoViewer.NET.Modules.Abstractions.Ui.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!--
      The Avalonia-shaped half of the module contract: WorkspaceTabDescriptor (Func<Control>
      ViewFactory / Control? ActiveContent) and IWorkspaceModule, which returns descriptors. Split out
      of DemoViewer.NET.Modules.Abstractions in B0 so that project — which owns IPlaybackSnapshot /
      IPlayerState / IReadOnlyEntityView — can be referenced by the Avalonia-free Playback2D.Pipeline.
      The namespace is deliberately UNCHANGED (DemoViewer.NET.Modules.Abstractions) so no call site
      needed a using change.
    -->
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <RootNamespace>DemoViewer.NET.Modules.Abstractions</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="Avalonia"/>
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Modules.Abstractions\DemoViewer.NET.Modules.Abstractions.csproj"/>
    </ItemGroup>
</Project>
```

### `src/App/DemoViewer.NET/DemoViewer.NET.csproj` additions

```xml
<ProjectReference Include="..\DemoViewer.NET.Modules.Abstractions.Ui\DemoViewer.NET.Modules.Abstractions.Ui.csproj"/>
<ProjectReference Include="..\..\Playback2D\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
<ProjectReference Include="..\..\Playback2D\DemoViewer.NET.Playback2D.Pipeline\DemoViewer.NET.Playback2D.Pipeline.csproj"/>
```

### `DemoViewer.NET.slnx` additions

```xml
<Folder Name="/src/App/">
    <!-- … existing entries … -->
    <Project Path="src/App/DemoViewer.NET.Modules.Abstractions.Ui/DemoViewer.NET.Modules.Abstractions.Ui.csproj"/>
</Folder>
<Folder Name="/src/Playback2D/">
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Tests.csproj"/>
</Folder>
```

### CI — `.github/workflows/ci.yml`

Append after the existing Desktop build step (this is the first automated test execution in this repo's
CI; it is safe because the suite needs no demo, no GPU and no Avalonia):

```yaml
      # SkiaSharp.NativeAssets.Linux links fontconfig (correction 9).
      - name: Install native deps
        run: sudo apt-get update && sudo apt-get install -y libfontconfig1
      - name: Playback2D core + pipeline tests (direct execution, no Avalonia)
        run: dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
```

This is the `playback2d-tests` job the rest of the track extends: B1 adds a `playback2d-budget`
job beside it, C1 adds `golden verify` + `bench --gate` steps, C2 adds the `render-backends`
matrix, B5 adds `wasm-build`. Nobody adds a *second* Core/Pipeline test invocation.

Do **not** add the golden capture to CI — it requires demo files the runner does not have and would
report as skipped noise. Update the header comment in `ci.yml` to say so. `scripts/test-app-suite.sh`
gets the new project appended to its batch list.

### WASM note

`src/App/DemoViewer.NET.Browser` transitively picks up SkiaSharp 2.88.9 through Core.
`Avalonia.Browser 11.3.12` already brings `SkiaSharp.NativeAssets.WebAssembly 2.88.9` (verified in
`project.assets.json:194`), so no browser-specific package is needed. The Browser head is not built in
CI; verify locally once at the end of T13 with `dotnet build src/App/DemoViewer.NET.Browser`.

---

## Dependencies

### Consumed from other phases

**None.** B0 is the first phase of track B and depends on nothing from A or C. It consumes only
existing App/parser surface:

| From | Signature |
|---|---|
| `DemoViewer.NET.Modules.Abstractions` (existing) | `IPlaybackSnapshot { int FrameIndex; int Tick; IReadOnlyEntityView Entities; IReadOnlyList<IPlayerState> Players; }` |
| `DemoViewer.NET.Modules.Abstractions` (existing) | `IPlayerState { int Slot; int Team; bool HasLivePawn; IReadOnlyEntity? Pawn; IReadOnlyEntity? Controller; (float X, float Y, float Z)? WorldPosition; }` |
| `DemoViewer.NET.Modules.Abstractions` (existing) | `IReadOnlyEntityView { IEnumerable<IReadOnlyEntity> All(); IEnumerable<IReadOnlyEntity> OfClass(string); IReadOnlyEntity? BySerial(int); IReadOnlyEntity? ByIndex(int); IReadOnlyEntity? ResolveHandle(ulong); }` |
| `CS2DemoKit.Analysis.Visibility` (package 0.10.0) | `MapAssetBundleReader.FindBundleDirectory/TryRead`, `MapAssetBundle`, `WorldBoundsDto`, `RadarTransform` — Pipeline-side only |
| `DemoViewer.NET.TestSupport` (existing) | `DemoTestHelper.FindDemoPath/GetOrParse/RequireDemo` — App.Tests golden capture only |

### Exported to other phases

| API | Consumed by |
|---|---|
| `Scene2DFrame`, `SceneTime`, `RenderPurpose`, `SceneMapInfo`, `SceneGameInfo`, `SceneVision`, `KillFeedRow`, `WorldBounds`, `MapRadarImage` | B1 (all layers + camera rigs), B4 (`ISceneFrameSource`, HUD layers), C1 (`dv2d render/export`) |
| `ISceneLayer`, `LayerSlot`, `LayerCacheHint`, `SceneRenderContext`, `SceneCompositor` | B1 (7 layer ports + `Scene2DHost`), B2 (`AnnotationLayer`), B4 (`ClockLayer`, `KillFeedLayer`) |
| `ViewportTransform`, `SliceCamera` | B1 (`ICameraRig`, `LevelPane`), B3 (`SingleLayout`), B4 (`CameraScript.Fixed`) |
| `PlayerMarker`, `AreaEffect`, `GrenadeTrail`, `BombMarker`, `RingState`, `ScenePalette` | B1 layers |
| `IRenderSurfaceProvider`, `RenderBackend`, `CpuSurfaceProvider`, `SceneRenderer` | B4 (`SceneExportSession`), C1 (`dv2d render`), C2 (`GpuSurfaceProvider` implements the interface; perceptual diff compares against the CPU path) |
| `SceneFrameBuilder`, `SceneFrameInput`, `RingStateTracker` | B1 (VM continues to drive it), B4 (`TrackerFrameSource` drives its own instance) |
| `SceneFixture`, `SceneFixtureSerializer`, `tests/fixtures/playback2d/` | B1 (parity corpus), C1 (`dv2d render --fixture`), B2 (annotation payload slot) |
| `tests/goldens/playback2d/*.png` | **B1's exit criterion** — "Pixel-parity (± reviewed text metrics) vs B0 goldens" |
| `Playback2DTabViewModel.CurrentFrame` | B1 (`Scene2DHost` submission) |
| `DemoViewer.NET.Modules.Abstractions.Ui` split (D1) | B4 (export service in Pipeline), C1 (CLI links Pipeline without Avalonia) |

Note for B4: `TrackerFrameSource` should construct its **own** `SceneFrameBuilder` (never share the
VM's), because of the D6 double-buffer contract — a shared builder would have export overwrite the
frame the UI is rendering.

---

## Risks & spikes

| # | Risk | Mitigation | Time-box |
|---|---|---|---|
| R1 | The `Modules.Abstractions` split (D1) ripples further than the two files — e.g. an unnoticed Avalonia-typed member elsewhere in the project | Grep is already done: `using Avalonia` appears in exactly one file. If a second surfaces, move it too; if a *third* project turns out to need Avalonia types from Abstractions, fall back to letting Pipeline define narrow input interfaces and adapting in the App. | **½ day.** If not resolved, escalate to the integrator before starting T7. |
| R2 | `SceneFrameBuilder` extraction silently changes behaviour (field-read order matters — the VM's comment at `:743-747` warns that one-hop weapon resolves must read each resolved entity's scalar *before* the next `ResolveHandle`) | Extract mechanically, method by method, running the App Playback2D tests after each method moves. Do not "tidy" while moving. | **1 day** for T7; if the suite is still red after 1.5 days, revert and extract one method per commit. |
| R3 | Goldens are not deterministic (marker glide / camera lerp / theme resolution) | `CameraMode.Fit` + first-appearance marker seeding + asserting `SmoothedMarkerPosition == raw` before capture. If a capture still varies run-to-run, drop to a fixed synthetic VM state rather than a real demo for that case. | **½ day** spike inside T12. |
| R4 | Headless Skia text metrics differ across OS, making committed goldens machine-specific | B0's goldens capture the current control, which *does* draw a floor label with `FormattedText` (`Playback2DViewport.cs:924-929`). Capture only single-floor scenes for the strict-tolerance goldens; for the two-floor Nuke case, mask the label rect out of the comparison and record the mask in the fixture README. This is also design risk 1's "text differences reviewed, not auto-failed". | **½ day** inside T12. |
| R5 | `SkiaSharp.NativeAssets.Linux.NoDependencies` fails to load on the GitHub ubuntu image | Verified path: SkiaSharp 2.88.x NoDependencies ships a self-contained `libSkiaSharp.so`. If it fails, switch to `SkiaSharp.NativeAssets.Linux` + `sudo apt-get install -y libfontconfig1` in the CI job. | **2 hours**, inside T13. |
| R6 | `ScenePalette` locks the Dark fallback hexes into Core, forking from the App's theme tokens | B0 only pins the fallbacks (which the control already hard-codes as its own fallbacks); B1 makes the App build a `ScenePalette` from `ThemeColors.Get`. Add a B1 task note: `CanvasPalette` becomes a `ScenePalette` factory, and a test asserts the two agree for the Dark variant. | n/a (B1 carry-forward) |
| R7 | Zero-allocation assertion in `SceneFrameBuilderTests` is flaky on a shared thread | Use `GC.GetAllocatedBytesForCurrentThread()` with `[NotInParallel]`, 64 warm-up iterations, and assert the *delta over the measured window* is 0. If string formatting in `FormatClock` allocates (it does — `RoundTime`/`DefuseTime` are strings), exclude those two fields from the zero-alloc path by caching the last formatted value keyed on the integer second. | **½ day**; if it cannot be made 0, relax the B0 assertion to "< 256 bytes/frame" and log it as a B1 obligation (§6 makes 0 a hard budget from B1's `dv2d bench`). |

No architecture spike is needed in B0 — the design's only time-boxed spike is the C2 GPU backend.

---

## Acceptance checklist

The design's exit criterion is two clauses; items 1–4 map to "frames render to PNG with zero Avalonia
dependencies", items 5–7 map to "goldens green", 8–16 are this plan's own additions.

- [ ] **1.** `dotnet test src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release` is green, and
      `SceneSmokeRenderTests.DebugGridLayer_RendersFixtureToPng_WithZeroAvaloniaLoaded` writes a
      decodable PNG from a `SceneFixture` through `CpuSurfaceProvider`.
- [ ] **2.** That same test asserts no loaded assembly's name starts with `Avalonia`, and it passes.
- [ ] **3.** `ArchitectureTests` proves Core's direct references are `{ SkiaSharp } ∪ BCL` and that
      Core's, Pipeline's **and** `Modules.Abstractions`' transitive closures contain no Avalonia.
- [ ] **4.** `BannedApiTests` proves no `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`,
      `Stopwatch`, `Random`, `Environment.TickCount*` or `Thread.Sleep` reference exists in Core or
      Pipeline metadata.
- [ ] **5.** `tests/goldens/playback2d/` contains at least three committed PNGs, each with a paired
      `tests/fixtures/playback2d/<name>.scene.json` produced from the same push.
- [ ] **6.** `Playback2DGoldenCaptureTests` re-run twice on the same machine produces byte-identical
      captures (determinism), and passes against the committed goldens.
- [ ] **7.** Golden regeneration is documented and gated behind `PB2D_GOLDEN_UPDATE=1`;
      `scripts/update-playback2d-goldens.sh` exists and works.
- [ ] **8.** `ViewportTransform` and `SliceCamera` exist only in
      `DemoViewer.NET.Playback2D.Core`; no type-forwarding shim, no duplicate definition, and their
      member lists are byte-identical to the pre-move files apart from the namespace line.
- [ ] **9.** All ~26 existing `Playback2D*` / `Floor*` / `RingStateTracker` App tests pass; the only
      diffs to them are added `using` lines and the three files moved to the new test project.
- [ ] **10.** `Playback2DTabViewModel.BuildFrame` contains no scene-building logic — it seeds the
      roster, runs the attributes pass, calls `SceneFrameBuilder.Build`, and copies results out.
- [ ] **11.** `Scene2DFrame` carries every field enumerated in the layer-justification table, and a
      reflection-driven `SceneFixtureTests.RoundTrip_PreservesEveryFrameField` proves the serializer
      covers all of them (so adding a field without serializing it fails the build).
- [ ] **12.** `SceneFixtureSerializer` preserves unknown JSON members across a read/write round trip.
- [ ] **13.** `DemoViewer.NET.slnx` lists all four new projects; `dotnet build DemoViewer.NET.slnx -c
      Release` is clean with `TreatWarningsAsErrors=true`.
- [ ] **14.** `.github/workflows/ci.yml` runs the direct-execution suite, and the run is green on
      ubuntu-latest.
- [ ] **15.** `dotnet build src/App/DemoViewer.NET.Browser` still succeeds (WASM head unbroken by the
      SkiaSharp addition).
- [ ] **16.** `docs/playback2d-v2/design.md` §12 open question 1 is answered in this plan's Decisions
      section (D1 resolves the Avalonia boundary that blocks Pipeline; the seek-core boundary itself is
      confirmed as `CS2DemoKit.Parser.EntityTracking.EntitySeekService` — see the note below) and no
      other design section required a change.

**Seek-core note for B4 (recorded here because B0 confirmed it, not because B0 uses it):** the
"checkpoint-replay seek core" the design says to extract from `MainViewModel` is not repo code at all —
it is `CS2DemoKit.Parser.EntityTracking.EntitySeekService` from the `CS2DemoKit.Parser` 0.10.0 package
(`public sealed class EntitySeekService { EntitySeekService(Func<EntityTracker> createTracker);
SeekResult SeekToFrame(int, IReadOnlyList<DemoFrame>); SeekResult SeekToFrameNoSnapshot(int,
IReadOnlyList<DemoFrame>); SeekResult SeekToFrameWithSnapshotAt(int, int, bool,
IReadOnlyList<DemoFrame>); }`). `MainViewModel` merely owns one instance (`MainViewModel.cs:205-206`,
constructed at `:698-701`). There is nothing to extract: B4's `TrackerFrameSource` constructs its own
`new EntitySeekService(() => new EntityTracker())` — deliberately **not** `MainViewModel.CreateTracker`
(`:2736-2751`), which wires the interactive Tier-3 debugger — calls `SeekToFrameNoSnapshot(startFrame,
frames)`, keeps the returned tracker privately, and steps it with `AdvanceOneFrame(frames[i])`.
