# D6 — the audit, and the record of what was done about it

**Design authority:** this document · **Registry:** [`D0-ux-pass-overview.md`](D0-ux-pass-overview.md) ·
**Branch:** `feature/playback2d-v2` · **Audited at:** `9a87305` ·
**Status: CLOSED — rounds 1, 2 and 3 landed. Dispositions verified against the tree on 2026-08-26.**

**This document is now a record, not a to-do list.** Every finding below carries a disposition in §0.1;
the finding text itself is left exactly as the audit wrote it, in the present tense, because a defect
report rewritten after the fix loses the thing that makes it useful — what it looked like from the
outside, before anyone knew where to look.

Five read-only audits swept the module after the D track landed: one per lens rather than one per
directory, so a defect that spans two directories could not fall between two reviewers.

| Audit | Lens |
|---|---|
| A | Built but never surfaced or consumed |
| B | Correctness of the D-track diff itself |
| C | Correctness of the established Core/Pipeline |
| D | The App-layer view-models and views |
| E | Cross-surface parity, docs vs reality, test-suite honesty |

Findings are deduplicated here (A1≡D3, A5≡D6). Every item is labelled **CONFIRMED** — traced end to
end, or reproduced with a throwaway probe — or **SUSPECTED**, with what would settle it.

---

## 0. Disposition

**32 product findings, 8 gate findings, 2 rounds of fixes and a third that closed the tail.** Read
across: `FIXED` means the defect is gone *and* something fails if it returns; `ROUTED` means it was
looked at, deliberately not built, and the reason is recorded somewhere a test can fail over; `OPEN`
means it is still there and nobody has claimed it.

### 0.1 Product findings

| # | One line | Disposition | Where the guard lives |
|---|---|---|---|
| 1 | Export dialog renders as its own type name | **FIXED** (R1) | `Playback2DExportSurfaceTests.TheExportPane_MountsItsRealView_NotItsTypeName` |
| 2 | Export refused when the output file exists | **FIXED** (R1) — overwrite is a `NoticeBanner`, not an error | `WhenTheOutputFileExists_ItStillStarts_AndSaysItWillOverwrite` |
| 3 | Export progress / failure / cancel rendered nowhere | **FIXED** (R1) — new `Playback2DExportStatusViewModel` + view | `AStatusChange_ReachesTheBoundFlyout`; guard 1 |
| 4 | In-app ffmpeg download can never run | **FIXED** (R1) — consent is a foreground command, not an omitted parameter | `TheLicence_IsShown_AndInstallingWaitsForTheUserToAcceptIt`; guard 4 |
| 5 | `AnnotationTrack.MarkersChanged` never subscribed | **FIXED** (R1) | `AnnotationTrack_MarkersAndAvailability_FollowTheDocument_WithNoRebuild`; guard 1 |
| 6 | A chorded release truncates the stroke | **FIXED** (R1) | `MiddleRelease_DuringAnOpenStroke_DoesNotTruncateIt` |
| 7 | ColorPicker drag rewrites `settings.json` per sample | **FIXED** (R1) — 250 ms debounce + self-write guard | `RapidStyleChanges_CoalesceIntoASingleSettingsWrite` |
| 8 | One `NaN` poisons the camera and pins the loop on | **FIXED** (R1) | `ANonFiniteMarker_NeitherPoisonsTheCamera_NorPinsTheRenderLoopOn` |
| 9 | Roster CT column and kill feed share a rectangle | **FIXED** (R1) — the feed reserves a band | `TheRosterAndTheKillFeed_DoNotOverlap_OnAShortPane` |
| 10 | `MapSpace.Mint`'s bump breaks annotation level identity | **FIXED** (R1) — new `IdForAnchor` | `IdForAnchor_FollowsTheCarriedIdentity_NotTheMintingRule` |
| 11 | Follow target survives a demo swap | **FIXED** (R3) | `Playback2DFollowFunnelTests` |
| 12 | Legacy renderer + draw tool kills Space and Escape | **FIXED** (R3) — the toolbar gates on the mounted surface | `Playback2DSurfaceCapabilityTests` |
| 13 | AUTO level-follow toggle never persists | **FIXED** (R2, by guard 2) | `Playback2DLevelStripTests` |
| 14 | Format change leaves the output extension stale | **FIXED** (R1) | `ChangingTheFormat_RewritesTheOutputExtension` |
| 15 | `_exportInk` shared with no run correlation | **FIXED** (R1) — ink rides the request | `EachStart_CarriesItsOwnInk` |
| 16 | Picture-cache key carries no palette | **FIXED** (R1) | `APaletteSwap_DropsThePictures_SoAPerCameraLayerRedraws` |
| 17 | Single-pane `Render` pins `PerCamera` to a default pane | **FIXED** (R1) — that overload bypasses the cache | `TheSinglePaneRender_DoesNotCacheAgainstAnUnframedPane` |
| 18 | `MapSpace.Reset()` publishes `LevelSetChange.None` | **FIXED** (R1) | `Reset_PublishesEveryLevelAsRemoved_SoPanesReconcile` |
| 19 | A reserved annotation kind crashes the eraser | **FIXED** (R1) — the store fences it, as `LevelLayouts.Parse` does | `Load_AReservedKind_BecomesFreehand_SoTheEraserSurvivesIt` |
| 20 | `Format` drops `KeyModifiers.Meta` | **FIXED** (R3) | `Playback2DKeybindConflictTests`, parametrized over Meta |
| 21 | `OnKeyUp` resolves `HoldPan` against the current profile | **FIXED** (R1) — the key is latched at key-down | `HoldPan_ReboundWhileHeld_StillReleases` |
| 22 | `RadarLayer.ScaledFor` leaves a disposed `SKImage` | **FIXED** (R1) — *and the SUSPECTED label was right*; a fault-injecting surface factory settled it | `AFailedResample_LeavesNoCacheEntry_RatherThanADisposedHandle` |
| 23 | `TextBlobCache` can dispose `SKTypeface.Default` | **FIXED** (R1) — ownership is tracked | `AMissingTypefaceResource_BorrowsTheFallback_AndNeverDisposesIt` |
| 24 | 552 B/frame on the histogram floor path | **FIXED** (R1) — and the budget gate widened to cover that branch | `BudgetTests.FullScene_HistogramFloors_SteadyState_AllocatesNothing` |
| 25 | `RenderBackend` — the key the registry pins — does not exist | **ROUTED (R3), deliberately not built** | allow-list entry in guard 3 carries the reason; `AppSettings.cs` and `00-overview.md` §3.10 both say so. The suite that pinned the absence was deleted in P3: it compared a constant to itself and regexed production source text. |
| 26 | `AnnotationAutoSave` read, never written, no UI | **FIXED** (R3) — writer + toolbar toggle | `Playback2DAnnotationPersistenceTests`; guard 3 |
| 27 | `IncludeVision` is the only export checkbox that does not persist | **FIXED** (R3) | `Playback2DExportDialogTests` |
| 28 | `RenderPurpose` threaded everywhere, read by nothing | **ROUTED (R3)** — kept as a reserved seam, and now *pinned* as inert rather than assumed live | `RenderPurposeTests.EveryPurpose_RendersTheSamePixels` |
| 29 | Export pane at 1.71:1 contrast across eleven labels | **FIXED** (R1) | `Playback2DExportSurfaceTests` |
| 30 | `Scene2DHost` never handles `PointerCaptureLost` | **FIXED** (R1) — *SUSPECTED and confirmed* | `LostCapture_MidStroke_AbandonsTheGesture` |
| 31 | HUD layout test skips a 0×0 control | **FIXED** (R2) — it now fails, and the probed floor is the real 28 | `Playback2DHudLayoutTests` + its templateless-control canary |
| 32 | `SceneFrameBuilder.Reset()` does not reset `LastRoster` | **FIXED** (R1) | `SceneFrameBuilder.Reset` clears it |

### 0.2 The gates (P0b)

| # | One line | Disposition |
|---|---|---|
| G-1 | `render`/`golden`/`bench` could only draw B0's debug grid | **FIXED (R2 + R3).** One table, one entry point; six goldens re-captured; `--budget-bytes-per-frame` dropped. Recorded in `design.md` §0 as **O6** — round 3 added it there, because §0 is where a reader looks and it was the one place this never appeared. Round 3 closed the last hole the fold exposed: `playback2d.vision` was in the default stack and drew nothing. |
| G-2 | `App.Tests` ran in no CI job | **FIXED (R2 + R3).** New `app-tests` job, standard blocking + full reporting. R3 fixed the six `Environmental` failures the reporting step was carrying, and extended the **budget** lane to `Playback2D.Cli.Tests` — the complementary `Category=Budget` / `Category!=Budget` pair had only ever been applied to one of the two projects, which is why G-4 could hide. |
| G-3 | Six hand-written layer lists, one derived | **FIXED (R2).** `SceneLayerListParityTests` asserts the one shipping list against the catalog. R3 added `hud.roster` to the §3.3 registry, which had listed ten of the eleven ids. P3 dropped the two assertions that only re-stated a production expression, or pinned a test fixture rather than the app. |
| G-4 | `BenchAllocationTests` permanently red where no lane runs it | **FIXED (R2 + R3).** A live gate at 0 B/frame since R2; R3 added the lane that runs it. |
| G-5 | Golden capture rewrites its own scene fixtures | **FIXED (R2).** The scene write is inside the `PB2D_GOLDEN_UPDATE` guard with the PNG. |
| G-6 | Assertions too loose to fail | **FIXED (R2).** Kill tints pinned exactly; the timeline metric rewritten over the timeline's own rect; `HeadlessSceneRendererTests` takes named ids rather than `KnownLayerIds[0]`. |
| G-7 | Four of ten corpus entries `pending` | **FIXED (R2).** The annotation golden and the 1080p budget scene both exist and are gated; three `pending` entries remain, all of them things `dv2d` structurally cannot render (palette + tolerance, or `--layout single`), and each note now says which. |
| G-8 | Corpus integrity | **FIXED (R2).** `nuke-multilevel-upper` renamed off the colliding name, `nuke-multilevel-noradar` given an entry, and `EveryCommittedGolden_IsNamedByAnEntry_AtTheDeclaredSize` makes the README's "exactly one answer" mechanical. |

### 0.3 Still open

| What | Why it is still here |
|---|---|
| **25 — `RenderBackend`** | Routed, not fixed. No consumer exists for the key — reasons in the `Playback2DSettings` class doc. Lands with C2 Stage 1 (design §0 **O2**). |
| **28 — `RenderPurpose`** | Routed as a reserved seam. `Export` and `Interactive` render identically and a test now says so; deleting it would cost the export/interactive distinction the pipeline may still want. |
| **§3 — `WetChanged`, `ActiveToolChanged`** | Still raised, still unsubscribed, both allow-listed in guard 1 with a deletion condition. |
| **§3 — `SceneRenderer`** | Still test-only, and **its doc still claims `HeadlessSceneRenderer` is "a facade over this — never a second render path"**, which is backwards: the facade is the real path and this is the dead one. Round 3 did not own that file. One doc comment. |
| **§3 — `LayerCacheHint.Static`** | Still produced by nothing but a test layer, so the world-space cull and matrix replay behind it are production-dead. |
| **`Playback2DRendererKind.Legacy`** | Its doc says the escape hatch was "removed in B5"; `old-control-removal.md` correctly says the release *after* v2 ships, and the hatch is still here. One doc comment, in a file round 3 did not own. |
| **`dv2d export --layers …,playback2d.vision`** | Still an empty layer. `render`/`golden`/`bench` draw a fixture's pre-solved vision; an export's frames come off `SceneFrameBuilder`, whose `Vision` input nothing fills. Feeding it means constructing a `VisibilityEngine` for the demo's map inside `ExportCommand`. Recorded at that call site. |

---

## 1. The shape of what was found

A suite of 1594 passing tests saw none of it. Four whole-graph gaps, each invisible to a unit test *by
construction*, because a unit test's job is to instantiate the thing directly:

| # | Gap | Instances |
|---|---|---|
| **G1** | **The optional constructor parameter.** `SceneExportRunner(setup, surfaces = null, ffmpegDir = null, consent = null, log = null, probe = null)` has one production caller passing **one** argument. Tests supply the rest, so the suite proves every branch while the shipped composition takes none of them. Nothing distinguishes "the test does not need this" from "production forgot it". | A2 (ffmpeg download), A4 (GPU backend), and the discarded export log |
| **G2** | **Producer wired, consumer never built.** An event is raised faithfully and nothing subscribes; a setting is read and nothing writes it. In several cases a doc comment describes the missing half as though it exists. | A1 (`StatusChanged`), A3 (`MarkersChanged`), A6 (`AnnotationAutoSave`), `WetChanged`, `ActiveToolChanged` |
| **G3** | **The XAML binds around the code path that does the extra work.** `IsChecked="{Binding IsAutoEnabled}"` reaches the property but skips the command that also persists. String-based binding makes "is this command used?" invisible to the compiler, the analyzer, and a C#-only grep — and the test drove the command the UI does not take. | A5 (AUTO toggle); two further commands are bound nowhere |
| **G4** | **The one mechanical settings guard tests transport, not consumption.** `SettingsWasmRoundTripTests` reflects over every `Playback2DSettings` property and proves each survives a fileless round trip. There is no equivalent for *something reads it*, *something writes it*, or *the user can reach it*. | A4 (key absent entirely), A6, A10 |

**Four architecture tests close all four gaps mechanically.** They are §4 of this plan.

A fifth, narrower pattern produced the worst single finding: **a test that asserts on the service
instead of through the surface**. `ExportJobServiceTests` asserts `service.Status.Phase` directly, so
no view had to read it for the suite to pass.

---

## 2. Findings, ranked

### P0 — a shipped feature does not work

| # | Defect | Where | Evidence |
|---|---|---|---|
| **1** | **The export dialog renders as its own type name.** `ViewLocator.Match` requires `ViewModelBase`; `Playback2DExportDialogViewModel : ObservableObject`. No `DataTemplate` maps it, and `Playback2DExportDialogView` is instantiated nowhere. The pane shows one line of text and a Close button — no range, format, size, path or Export button. **The export UI has never worked.** | `ViewLocator.cs:42`, `Playback2DExportDialogViewModel.cs:46`, `Playback2DView.axaml:443` | CONFIRMED — headless probe: mounted=False, first TextBlock = the fully-qualified VM type name |
| **2** | **Export is refused whenever the output file already exists.** `Validate()` returns the overwrite *warning* as `ErrorBanner`; `CanStart => ErrorBanner is null`. The default path is a constant, so the **second export ever attempted** is blocked with a dead button. | `Playback2DExportDialogViewModel.cs:550` | CONFIRMED — probe with `fileExists: _ => true` → `CanStart=False` |
| **3** | **Export progress, failure and cancellation are computed and rendered nowhere.** `ExportJobService` marshals phase/frames/fps/elapsed/error to the UI thread and raises `StatusChanged`; nothing subscribes. `CancelAsync` has zero production call sites. ffmpeg dying sets `Error` and discards it. `_exportJob` is never disposed, and shutdown cancels the reel job but not the export. | `Playback2DTabViewModel.cs:332`, `ExportJobService.cs:89` | CONFIRMED — `ExportStatus` appears in exactly two lines repo-wide; no `.axaml` binds it |
| **4** | **The in-app ffmpeg download can never run.** `consent` is an optional parameter the sole production caller omits, so the download rung short-circuits — while the dialog shows *"Allow DemoViewer to download the pinned LGPL build"* checked by default, and the refusal text advertises the capability that just silently did not happen. | `SceneExportRunner.cs:147` vs `Playback2DTabViewModel.cs:344` | CONFIRMED — `AcquireAsync` has one call site, gated behind the null consent |
| **5** | **`AnnotationTrack.MarkersChanged` is raised faithfully and never subscribed.** The interface documents it as "the host must re-query it". Annotation markers therefore never appear as the user draws, and the Annotations track toggle never becomes *available* — `IsAvailable` is evaluated only inside `Rebuild`, which runs on activation and demo-reset only. | `Playback2DTimelineViewModel.cs:102`, `AnnotationTrack.cs:118` | CONFIRMED — only subscribers repo-wide are two tests |
| **6** | **A chorded button *release* truncates the in-flight stroke.** D2 taught `OnPressed` that chording is not a gesture; `OnReleased` never learned it and closes whatever is open. Brushing the middle button mid-stroke and releasing it commits the stroke at that point and drops capture; the rest of the drag draws nothing. | `InputToolRouter.cs:173` | CONFIRMED — repro against the real router: element ends at the chord point |
| **7** | **Dragging the ink ColorPicker rewrites `settings.json` on every pointer sample.** Latent until D4 made the pickers real. Each write is a synchronous read-serialize-temp-write-move-reload, and each reload re-composes the keymap profile and re-reflects the Settings page. A one-second drag is a few hundred cycles on the UI thread. | `AnnotationsPanelViewModel.cs:440`, `SettingsService.cs:141` | CONFIRMED by trace — no debounce, no dirty check, no self-write guard |
| **8** | **One non-finite coordinate poisons the camera permanently and pins the render loop on.** `WorldBounds.Extend` propagates `NaN` through `Math.Min/Max`; `_observed` is only widened, never re-seeded; `Fit`'s guard `w <= double.Epsilon` is false for `NaN`. Nothing draws, and `IsSettledAt` never settles, so an idle tab burns a core at refresh rate and never recovers — including across a seek. | `SceneFrameBuilder.cs:366`, `ViewportTransform.cs:90` | CONFIRMED — probe: 2000 advances, one `NaN` marker → `keepArmed=True centerX=NaN` |
| **9** | **`hud.roster`'s CT column and `hud.killfeed` occupy the same rectangle** on any pane shorter than ~552 px — which includes a 720p two-level stacked export, the case both layers' own comments cite. The feed is Order 80 vs roster 65, so it paints over the cards. | `RosterLayer.cs:144`, `KillFeedLayer.cs:118` | CONFIRMED by arithmetic from both layers' constants; every roster test renders the layer alone |

### P1 — wrong behaviour, narrower trigger

| # | Defect | Where | Evidence |
|---|---|---|---|
| 10 | **`MapSpace.Mint`'s collision bump breaks the ZMin-keyed level identity annotations depend on.** After a bump `level.Id != IdForZMin(level.ZMin)`, but the annotation layer and hit-testers derive the id from the stored anchor. A floor lost and re-found across a rebuild makes world-anchored ink vanish — or draw on the wrong floor if a neighbour owns the old key. design §10 risk 5's stated mitigation is exactly this identity. | `MapSpace.cs:417` vs `AnnotationLayer.cs:184` | CONFIRMED — probe: `before=lv-5 after=lv-4`, `IdForZMin(-320)=lv-5` |
| 11 | **The follow target survives a demo swap** and silently re-points at whoever holds that slot in the new demo, while the footer still names the old player. | `Playback2DTabViewModel.cs:1610` | CONFIRMED — probe after reset: `followed=1 status='following bravo · requested'` |
| 12 | **Under the legacy renderer, picking a draw tool kills Space and Escape.** The toolbar is gated on the feature, not the mounted surface, so `ToolDraw` succeeds, `toolActive` goes true, and the keymap's tool-scoped `HoldPan`/`CancelGesture` win — then fall through the `is Scene2DHost` check without setting `e.Handled`. | `Playback2DView.axaml.cs:220` | CONFIRMED by trace across three files |
| 13 | **The AUTO level-follow toggle never persists** — the XAML binds `IsChecked` directly and skips `EnableAutoCommand`, the only path that raises `SettingsChanged`. That command is bound nowhere; its only consumer is a test. | `LevelStripViewModel.cs:222`, `Playback2DView.axaml:421` | CONFIRMED — probe: `SettingsChanged raised 0 times` |
| 14 | **Changing the export format leaves the output path's extension stale**, and ffmpeg infers the container from the extension — so picking MP4 can yield a WebM named `.webm`. | `Playback2DExportDialogViewModel.cs:413` | CONFIRMED — probe: format=mp4, path still `.webm` |
| 15 | **`_exportInk` is a shared tab-level field with no run correlation and no clearing.** `RunAsync` awaits the heavy-job gate *before* the setup closure reads it, so a second Start that is subsequently refused has already overwritten the ink the parked first export will burn in. | `Playback2DTabViewModel.cs:481` | CONFIRMED by call ordering; the gate wait makes it reachable without a race |
| 16 | **The picture-cache key carries no palette.** `HeadlessSceneRenderer.Palette`'s own doc claims swapping it invalidates caches; it is a plain auto-property that does nothing of the sort. | `SceneCompositor.cs:394` | CONFIRMED — probe: dark grid persists after a light swap |
| 17 | **The single-pane `Render` overload pins every `PerCamera` key to a default pane**, so a moving camera replays frame 1 forever. Latent only because `SceneRenderer` has no production caller. | `SceneCompositor.cs:394`, `SceneRenderer.cs:38` | CONFIRMED — probe: `renders=1` across two cameras |
| 18 | **`MapSpace.Reset()` publishes `LevelSetChange.None` while removing every level**, so a handler doing `RetainUnarranged(LastChange)` keeps a pane and camera for every level of the old demo. | `MapSpace.cs:378` | CONFIRMED by trace; no test covers Reset → PaneSet reconciliation |
| 19 | **A hand-edited sidecar with a non-`Freehand` kind crashes the eraser.** `AnnotationStore` parses any `AnnotationKind`; `AnnotationHitTester` throws for anything else and `EraseTool` has no catch, so it escapes into Avalonia's pointer pipeline. `LevelLayouts.Parse` fences its reserved member for exactly this reason; `AnnotationStore` does not. | `AnnotationStore.cs:523` | CONFIRMED by trace |
| 20 | **`Format` drops `KeyModifiers.Meta` while `Row` writes it**, so a macOS ⌘ binding persists correctly and renders everywhere as the bare key — indistinguishable from an unmodified binding. | `Playback2DKeymap.cs:212` vs `Playback2DKeymapProfile.cs:194` | CONFIRMED — both formatters read in full |
| 21 | **`OnKeyUp` resolves `HoldPan` against the *current* profile.** Rebind while the key is held and the release no longer matches; nothing else clears the pan flag, so the surface pans forever. | `Playback2DView.axaml.cs:249` | CONFIRMED by trace |
| 22 | **`RadarLayer.ScaledFor` leaves a dangling disposed `SKImage`** if the resample throws — the next frame at that size returns the disposed handle to `DrawImage`, which is an access violation, not an exception. | `RadarLayer.cs:241` | SUSPECTED — shape traced; needs a fault-injected surface factory |
| 23 | **`TextBlobCache` can dispose `SKTypeface.Default`** on the missing-resource fallback path, killing text rendering process-wide. | `TextBlobCache.cs:94` | CONFIRMED by trace |

### P0b — the gates that were supposed to catch all of this

These are separate from the product defects above because they are the *reason* the list above is
long. Two of the three gates a PR runs were measuring nothing.

| # | Defect | Where | Evidence |
|---|---|---|---|
| **G-1** | **`dv2d render` / `golden` / `bench` can only draw B0's debug grid.** `SceneLayerCatalog._registrations` holds exactly one entry, `playback2d.debuggrid`; only `export` uses `CreateSceneStack` and the real eleven. So **CI's only pixel-regression gate on a PR re-renders every corpus entry as a grid**, and `dv2d bench --gate` measures that grid against a 16 ms budget. `dv2d render`, documented as the design-iteration loop for "a marker style, a cone fill, an ink outline", can draw none of them. | `SceneLayerCatalog.cs:30`, `SceneRenderPlan.cs:124`, `ci.yml:149,163` | CONFIRMED four ways by running it: `--layers markers` → *"unknown layer id(s)"*; `golden verify --cpu` → six entries at **`ssim: 1, max_channel_delta: 0`**; `bench --gate` → `"layers":["playback2d.debuggrid"]`, p99 **0.094 ms** vs 16 ms; and the `duel-mirage-b` golden PNG *is* a grid with five discs |
| **G-2** | **`App.Tests` — 967 tests, 73 of the repo's 101 skip sites — runs in no CI job.** `ci.yml` invokes two test projects; `scripts/test.sh` knows six; `test-app-suite.sh` is invoked by no workflow. Every guard for this project's two named defect classes lives in the project CI never runs: `Playback2DFeatureWiringTests`, `Playback2DHudLayoutTests`, `Playback2DExportDialogTests`, `Playback2DKeyRoutingTests`, `SettingsWasmRoundTripTests`, `Playback2DGoldenCaptureTests`. | `.github/workflows/ci.yml` | CONFIRMED — read end to end |
| **G-3** | **Six independent hand-written layer lists; exactly one derives from the catalog.** A new scene layer must be added in four places by hand. `SceneStage`'s doc claims its stack is wired "exactly as `Scene2DHost` wires them… so none of them can quietly test a different layer stack from the one that ships" — it is a hand-copied array, so the guarantee is inverted. | `SceneLayerCatalog.cs:30,136`, `Scene2DHost.cs:173`, `Playback2DExportDialogViewModel.cs:346`, `ExportCommand.cs:385`, `SceneStage.cs:48` | CONFIRMED — every `compositor.Add` site grepped |
| **G-4** | **`BenchAllocationTests` is permanently red and parked where no lane runs it** — its own doc says "**Expected to fail** until `SceneLayerCatalog` registers B1's seven layers", tagged `[Category("Budget")]`, and the CLI lane excludes Budget while the budget lane runs only `Playback2D.Tests`. The category is doing the work of a `[Skip]` without saying so. | `BenchCommandTests.cs:112` | CONFIRMED — both CI steps read; merge `742b7ca` records it failing at 3336 B |
| **G-5** | **`Playback2DGoldenCaptureTests` rewrites its own scene fixtures unconditionally**, outside the `PB2D_GOLDEN_UPDATE` guard that protects the PNG. The reference demo ships in every checkout, so any App-suite run rewrites `nuke-multilevel.scene.json` — the *input* to `GoldenParityTests` and `LevelGoldenTests`. The class doc says "a golden that silently rewrites itself is a test that no longer tests". | `Playback2DGoldenCaptureTests.cs:158` | CONFIRMED — it happened in `742b7ca` |
| **G-6** | **Assertions too loose to fail.** Kill-marker colours are asserted only *non-zero, different and opaque*, so swapping T and CT keeps it green — D5's entire user-visible claim is unpinned. `Playback2DTimelineRenderTests` asserts `nonBg > 100` where the opaque panel fill alone passes on an empty timeline. `HeadlessSceneRendererTests` derives its input from `KnownLayerIds[0]`, so it passes identically whether the catalog holds one layer or eleven — it structurally cannot notice G-1. | `TimelineTrackTests.cs:151`, `Playback2DTimelineRenderTests.cs:53`, `HeadlessSceneRendererTests.cs:58` | CONFIRMED |
| **G-7** | **Four of ten corpus entries are `pending`** — skipped, never failed — including the 1080p budget scene and the only annotation golden, whose scene file does not exist. So **no golden anywhere covers burned-in annotations**, the feature D3a just fixed. Three notes name owners (B1, B2, dv2d) that have all shipped. | `manifest.json:24,150,168,186` | CONFIRMED via `dv2d fixture list` |
| **G-8** | **Corpus integrity:** `nuke-single-upper` has two goldens at two sizes with different meanings; `nuke-multilevel-noradar@900x900.png` has **no manifest entry at all**, contradicting the corpus README's "which fixtures exist has exactly one answer"; and `Playback2DGoldenCaptureTests` captures at 900×900 while the manifest declares `duel-mirage-b` at 640×360, so O3's goldens would land at a path the manifest never reads. | `tests/fixtures/playback2d/` | CONFIRMED by inspection |

### P2 — hygiene, performance, and unreachable capability

| # | Defect | Where |
|---|---|---|
| 24 | **`MapSpaceFactory.Update` allocates 552 B/frame on the histogram path** — the no-baked-bundle case every user without a map asset is on. `BudgetTests` calls `SetAuthoritativeFloors` first, so the "zero allocation" gate measures the branch those users never take. | `FloorSplitter.cs:100` |
| 25 | **`RenderBackend` — the settings key the registry pins — does not exist.** The GPU stack is built and tested but reachable only from `dv2d --backend`; the app hard-codes `CpuSurfaceProvider`. | `AppSettings.cs:391` |
| 26 | `AnnotationAutoSave` is read at runtime, has a `WriteInMemory` row, and has no writer and no UI. | `AppSettings.cs:445` |
| 27 | `IncludeVision` is the only export checkbox that does not persist. | `Playback2DExportDialogViewModel.cs:93` |
| 28 | `RenderPurpose` is threaded through the whole pipeline and read by nothing — `Export` and `Interactive` render identically. | `SceneCompositor.cs:311` |
| 29 | The export pane uses the app-chrome `TextDim` on a `Pb2dPanelBg` host — **1.71:1** contrast in Dark, across eleven labels. Crosses the D21 wall. | `Playback2DExportDialogView.axaml:28…` |
| 30 | `Scene2DHost` never handles `PointerCaptureLost`; an OS-cancelled contact leaves the stroke open. *(SUSPECTED)* | `Scene2DHost.cs:497` |
| 31 | `Playback2DHudLayoutTests` skips any control measuring 0×0 — exactly what a templateless control measures — so the ColorPicker fix shipped with no guard. Probed floor is 25 against an actual 28. | `Playback2DHudLayoutTests.cs:164` |
| 32 | `SceneFrameBuilder.Reset()` does not reset `LastRoster`; `TimelineHudDataSource`'s tick cache can hand a layer the previous frame's roster when two output frames share a tick. | `SceneFrameBuilder.cs:154` |

---

## 3. Dead public surface

Not bugs today; recorded so the next reader does not mistake them for load-bearing.

> **Round 3 re-check.** Four of these were followed up. `AnnotationSession.WetChanged` and
> `InputToolRouter.ActiveToolChanged` are **still dead and now allow-listed** in guard 1, each with the
> condition for deleting the entry, so the guard names them rather than staying quiet.
> `LayerCacheHint.Static` is **still produced by nothing but a test layer**. `SceneRenderer` is **still
> test-only**, and the false half of its doc — "`HeadlessSceneRenderer` is a facade over this — never a
> second render path" — **is still there**; the facade is the shipped path and this is the dead one.
> `HeadlessSceneRenderer`'s own converse claim was corrected in round 3.

`SceneRenderer` (every consumer is a test, and its doc's claim that `HeadlessSceneRenderer` is "a
facade over this — never a second render path" is false); `LayerCacheHint.Static` (never produced, so
the world-space cull and matrix replay are production-dead); `CameraScript.FollowPlayer` and
`CameraScriptResolver.ApplyFollow` (no shipping surface can produce a following export camera);
`AnnotationSession.WetChanged` (raised 4× per stroke, zero subscribers anywhere);
`IToolServices.WorldUnitsPerPixel`, `IVisionSolver.IsReady`, `AnnotationDocument.ApplyMigration`,
`AnnotationHitTester.HitTestAll` (documented topmost-first; `EraseTool` walks document order
instead, so production does not get the guarantee), `StackedLayout.BandRect` (`PaneSet.PaneAt`
re-derives the band math inline, so the invariant it exists to enforce is not enforced),
`SceneDefaults.WorldExtent`, `MapAssetPipeline.TryLocateAssetsRoot`, and write-only state on
`ExportRequest.DeltaSeconds`, `SceneSubmission.SubmissionId`, `SceneCompositorStats`,
`LevelSetChange.Retained`/`.Added`.

`FreehandOptions` is never non-default except `Size`: both production sites call `ForWidth`, and
`AnnotationStyle` exposes only `WidthWorld`, so the flat-cap and both taper branches are test-only.
`HudStyle` is still never constructed with non-default values.

---

## 4. The four guards — BUILT (round 2B)

Each is a whole-graph reachability assertion. Together they close G1–G4 and would have caught
findings 3, 4, 5, 13, 25, 26 and 27 the day each was written.

All four live in `src/App/DemoViewer.NET.App.Tests/`, are **untagged** (so every tier runs them), and
share one analyser: `Playback2DWholeGraph.cs`.

**Two lenses, deliberately.** Anything expressible in IL is read from IL — `System.Reflection.Metadata`
over the three production assemblies (App, Core, Pipeline), following `BannedApiTests`' two-pass
token scan. A *source* grep for an event name also matches the `<see cref>` in the doc comment that
**describes the missing half** — the shape several of these defects shipped in. Only the two questions
IL genuinely cannot answer fall back to source — an `.axaml` string binding, which compiles to nothing,
and whether a call site mentions an optional parameter — and that corpus has every `///` line blanked
before it is searched. The corpus is `src/**` + `tools/**`, excluding `bin`/`obj` and every `*.Tests` /
`*.TestSupport` / `*.UiCapture` directory: each of those constructs the module's types the way a test
does, with hand-supplied collaborators.

| # | Guard | Asserts | Proof it can fail |
|---|---|---|---|
| 1 | `Playback2DEventWiringTests` | Every public event contract in the module has ≥1 production raiser (a method that loads the backing field and is not the compiler's `add_`/`remove_`) and ≥1 production subscriber (a call to `add_X`). | Three canary events in the test assembly — wired, raised-never-subscribed, subscribed-never-raised — classified correctly. Plus: the allow-list must name exactly the events that fail without it. |
| 2 | `Playback2DCommandBindingTests` | Every `CommunityToolkit.Mvvm.Input` command property on a module type is named by an `.axaml` or by production C#. | A canary command that exists in no production source is reported; `ToggleDisplayModeCommand` (bound in `Playback2DView.axaml`) is not. The matcher is separately checked against a synthetic corpus for word boundaries and doc-comment blanking. |
| 3 | `Playback2DSettingsConsumptionTests` | Every `Playback2DSettings` property has a `get_X` call and a `set_X` call outside `DemoViewer.NET.Configuration` (which is exactly `AppSettings.cs` + `SettingsService.cs`). **Plus** every key registry §3.10 names exists on the class. | Three canary properties — read+written, read-only, written-only — classified correctly. The registry parse asserts it found >20 keys, so a moved heading fails rather than passing over nothing. |
| 4 | `Playback2DCompositionTests` | Every production `new T(...)` of an App-side module service **mentions** every null-defaulted optional parameter, positionally or by name. | The argument reader is checked against handcrafted call lists (lambdas, collection expressions, ternaries, comments between arguments, a `)` inside a string, an unbalanced list). IL separately answers *is it constructed at all*, so a call site the parser cannot read is **reported**, not assumed absent. |

**Guard 1 groups by contract, not by event.** The four `MarkersChanged` implementations are one
`ITimelineTrack.MarkersChanged`, and three of them (round, kill, bomb) legitimately never raise it —
their data is fixed for the whole demo. Asking each implementation in isolation would demand a raise
those three have nothing to raise. The grouping still catches the original defect, because at the time
of the audit **nothing in production subscribed to any of them**.

**Every allow-list entry carries a reason and is asserted load-bearing** — a companion test fails if an
entry names something that is no longer broken, and fails if any reason is under 40 characters.

### What they found

Fixed here:

- **Finding 13 — the AUTO toggle never persisted.** `LevelStripViewModel.OnIsAutoEnabledChanged` now
  raises `SettingsChanged`, which is the path the `ToggleButton`'s `IsChecked` binding actually takes;
  `EnableAuto` lost its `[RelayCommand]` (a command on a two-way toggle would fight the binding on
  un-check, so it could never have been the user's path). A `_inGesture` flag keeps a chip click to one
  save rather than two, and `OnIsAutoAvailableChanged` now clears under `_applying` so a **gate** going
  off can never overwrite the user's preference.

Routed, each allow-listed with its reason and the condition for deleting the entry:

| Where | What | Route |
|---|---|---|
| `SceneExportRunner.cs:57–61` vs `Playback2DTabViewModel.cs:361` | `surfaces`, `managedFfmpegDirectory`, `encoderProbe` omitted at the sole production composition | Round 3 with finding 25 — `surfaces` is the `RenderBackend` key's landing site |
| `AppSettings.cs:445` | `AnnotationAutoSave` read, never written, no UI | Round 3, finding 26 |
| `00-overview.md` §3.10 vs `AppSettings.cs` | `RenderBackend` pinned by the registry, absent from the class | Round 3, finding 25 |
| `Playback2DTabViewModel.cs:955`, `:965` | `FollowPlayerCommand` / `ClearFollowCommand` bound nowhere — both methods are called directly by the keymap and the follow funnel, so the generated wrappers are dead surface | Round 3: drop the attribute, keep the method |
| `AnnotationSession.cs:240` | `WetChanged` raised 4× per stroke, zero subscribers | §3 dead surface — delete, or explain the second repaint |
| `InputToolRouter.cs:101` | `ActiveToolChanged` raised, zero subscribers; the panel's own `ObservableProperty` is what the toolbar binds | §3 dead surface — subscribe or delete |

`LegacyViewport` is allow-listed as the only **by design** entry: plan decision D-9 makes it a
hand-edited escape hatch with no UI, so it has a reader and needs no writer.

### The fifth, cheaper guard — also built

`Playback2DHudLayoutTests` now **fails rather than skips** on a visible control measuring 0×0, and its
probed floor moved from 25 to the 28 actually present. A new canary,
`ATemplatelessControl_IsVisibleAndMeasuresZero`, proves the condition is reachable — a `Button` with a
null `Template` reports `IsEffectivelyVisible = true` and `Bounds = 0×0`, which is exactly the state the
themeless `ColorPicker` was in from B2 to D4 while passing this very case on every run.

### G-6's other two, and two flakes found on the way

| Where | Was | Is |
|---|---|---|
| `TimelineTrackTests.cs:151` | kill tints only *non-zero, different, opaque* — swapping `TintTeamT`/`TintTeamCt` stayed green | the exact mapping (`team 2 → 0xFFE0A030`, `team 3 → 0xFF4A90D9`), plus a new case pinning that `RoundTrack` and `KillTrack` use the same RGB per side at their two different alphas |
| `Playback2DTimelineRenderTests.cs:53` | `nonBg > 100` counting any non-zero channel, over the full window width — `Pb2dPanelBg` alone scored 53 404 | the timeline's own rect; the modal colour is taken as the fill and `ink` counts what is drawn *on* it (30 192 px, 939 colours). The old metric is still computed and asserted **trivially true**, so the reason this was rewritten cannot be lost by restoring it |
| `ExportHudAndLadderTests.cs` `ItReusesTheDestination_AndAllocatesNothingOnceWarm` | one allocation window after a warmup loop; failed once in round 1 and passed on retry | `BudgetTests`' two-window form — warm window measured and printed, steady window asserted |
| `TimelineLayoutTests.cs:135` `KillMarkerBrushes_DifferBySide_AndFallBackToTheKindDefault` | asserted the *fallback literal* `0xFFF44336`, which `Token` returns only when no `Application.Current` exists or the call is off the UI thread — a property of what else has run in the process. Observed failing ~1 run in 10 | the two tints exactly, plus "an uncolourable kill takes the same token a bomb explosion takes", compared between two view-models built in the same call. True in either environment. Outside the audit's file list, taken because it was about to sit in a required lane |

### G-2 — the CI wiring

`App.Tests` now runs, in a new `app-tests` job, via `scripts/test-app-suite.sh` (N sequential process
batches, with its partition audit). Two steps:

- **`-t standard` blocks.** 868 tests, ~25 s, and it holds every guard above. The tier excludes
  `Integration`, `RealDemo`, `Environmental` and `Budget` — everything that reads a demo, crosses a
  process boundary, depends on machine state, or reports a number instead of a verdict. Measured green
  14 consecutive times locally before landing.
- **`-t full` reports** (`continue-on-error`). 1042 tests, ~41 s, 6 failures, **all six
  `[Category("Environmental")]`** (file-lock semantics, symlink privilege, a per-user settings path).
  A required check that is red for reasons nobody can fix is the "gate that cries wolf" this file's own
  comments warn about. The step carries an explicit promotion criterion in `ci.yml` so it cannot become
  a step everyone learns to ignore.

`ci.yml`'s WASM payload check also gained `Avalonia.Controls.ColorPicker` and `AvaloniaEdit`, with the
rule stated: anything `App.axaml` `<StyleInclude>`s by `avares://<assembly>/…` is boot-critical, because
the XAML loader resolves that URI during `App.Initialize` — a package that stops shipping is not a
missing control theme, it is a black browser head.

**A new G-2-shaped finding, found by wiring it up.** `test-app-suite.sh` discovered test classes by
grepping `$PROJ` only — but `tests/shared` is compiled into every test assembly as **linked source**, so
`TestTierContractTests` (6 tests: the category-vocabulary guard, the script-text-vs-`TestTiers.cs`
check, the tier-nesting proof) was **in no batch at all**. The batched run executed 862 of the 868 the
suite holds, and the script's own partition audit could not see it: `--list-tests` counts a
parametrized test once while the run expands it, so "ran ≥ listed" was satisfied with room to spare.
The lane would have selected on tier filters while never running the guard over those filters.

Fixed in the same commit: the grep walks `tests/shared` too, and a new **discovery audit** lists the
tests under the class filter and requires it to equal the unfiltered listing exactly — both sides count
the same way, so any difference is a class the grep missed. Batched now runs 868, matching the
single-process count.

---

## 4b. Surface parity and browser honesty

`dv2d export` and the app produce **materially different videos from the same request**: vision is on
in the CLI and off in the app, the CLI's kill feed is always empty, its palette is hard-coded Dark,
and it has no way to burn in annotations at all. `export.md:7` claims *"a request the dialog accepts
is a request the CLI accepts"* and `:171` claims the two kill feeds *"cannot show different kills at
the same tick"* — one of them shows none, ever.

On the browser head the gate itself is **complete** — a sweep for process spawning, filesystem
writes, ffmpeg, native interop and platform attributes found no ungated desktop-only capability, and
the D track added none. The defects are all about **honesty**:

- **D1's keybinding overrides are memory-only in the browser and nothing says so.** A user rebinds
  twenty gestures, watches them apply live, and loses all of it on refresh. B5 fixed exactly this
  class for annotations (*"session only — this browser tab forgets…"*); D1 shipped a new surface that
  ignores the precedent. A grep of every Settings view for `session only|not saved|forgets|reload`
  returns zero hits.
- **`ShellReservedGestures` has no browser-reserved set**, while the Settings UI promises "keys
  already taken… are refused with a reason". A user can bind `Ctrl+T` / `Ctrl+N` / `F12`, which
  Chrome eats.
- **The Settings feature list binds the raw `IFeatureGate`, not `ShellModuleFeatureGate`**, so the
  browser shows a live, ON "Video export" toggle for a capability forced off one layer out. Recorded
  in `wasm-matrix.md` as a D4 follow-up; D4 shipped without it.
- **The export button vanishes with no explanation** — and the same binding hides it on desktop when
  no demo is open, so a browser user cannot tell "not available here" from "open a demo first". The
  codebase does this correctly elsewhere (`SettingsView.axaml:384`, "(unavailable in the browser)").
- **`ci.yml:99` asserts boot-critical WASM artefacts by name** and was not extended for
  `Avalonia.Controls.ColorPicker`, which `App.axaml:29` now hard-includes — if it stops shipping,
  `App.Initialize` throws and the head boots black.

`design.md` §0 is stale: O3 is self-contradicting (its goldens exist, are `pending:false`, and are
grids; it calls the `nuke-multilevel` pair "byte-exact" when the manifest says `perceptual`), O5's
parenthetical was **false when written** and is accidentally true now that D2 built the editor, and
**G-1 is missing from §0 entirely** — recorded only in a `ci.yml` comment and in two closed phases'
plans.

**Disposition (round 3).**

| Item | State |
|---|---|
| Browser keybinding overrides are memory-only | **FIXED.** The keybinding editor carries a session-only caution on the browser head. |
| `ShellReservedGestures` has no browser set | **FIXED.** `Playback2DKeymap.BrowserReservedGestures` refuses a rebind onto a gesture Chrome eats. |
| Settings shows a live Video-export toggle | **FIXED.** `FeatureToggleRow.IsPlatformUnavailable` makes the row non-interactive and labels it *"unavailable in the browser"*. |
| Export button vanishes unexplained | **FIXED.** The absence is explained rather than silent. |
| `ci.yml:99` artefact list | **FIXED.** Gained `Avalonia.Controls.ColorPicker` and `AvaloniaEdit`. |
| `dv2d`/app export parity, `export.md`'s false claims | Round 1's. |
| `design.md` §0 stale | **RE-STAMPED.** O3 rewritten (the two names it called open are hand-authored dv2d fixtures gated since C1; what is blocked is a *de_mirage pre-v2 capture*, and "byte-exact" is corrected to the delta distribution `GoldenParityTests` uses), O5's parenthetical annotated as false when written, **G-1 recorded as O6**. |
| `wasm-matrix.md` | **RE-MEASURED**, not re-ticked: payload 63.5 MiB / 16.4 MiB with the unit stated (the same number has been quoted as 66.5 in decimal MB), the `⚠️` legend no longer claims "and the UI says so" over two rows where it does not, and the manual checklist is **cleared to unticked** — it has not been run since B5. |

---

## 5. Fix order — and what each round actually did

The plan and the outcome, side by side.

**Round 1 — the product defects, in parallel (file-disjoint).** *Planned:*
- *Wave 1, export end to end:* 1, 2, 3, 4, 14, 15, 27, 29, plus the CLI/app parity gap and
  `export.md`'s false claims. One story: the dialog does not render, cannot start twice, reports
  nothing while it runs, and offers a download it cannot perform. Fixing any one alone leaves it
  unusable.
- *Wave 2, input and annotation:* 5, 6, 7, 19, 21, 30, plus 10 and 18 — level identity and pane
  reconciliation, which is where annotations actually get lost.
- *Wave 3, render and camera:* 8, 16, 17, 22, 23, 32, and 24's allocation with the budget gate
  widened to cover the histogram branch.

*Landed:* all of the above, and 21 came with it. **22 and 30 were both `SUSPECTED` and both real** —
the disposed-`SKImage` needed the fault-injecting surface factory the finding asked for, and it is a
seam in `RadarLayer` now. 27 slipped to round 3 with the other settings-key work.

**Round 2 — the gates, after Round 1.** *Planned:* G-1 through G-8, plus §4's four architecture guards
and G-2's CI wiring. Second on purpose: **registering the real layers re-baselines six goldens**, and a
golden that moved for that reason must not be confused with one that moved because Round 1 changed a
pixel. Round 1 agents were therefore forbidden from touching a golden PNG.

*Landed:* all eight gates and all four guards. The ordering rule held twice — once for the six goldens,
and again in round 3, where the vision layer moved exactly three more, each attributable to a single
named cause. The guards found 13 on their first run and routed six more with reasons; guard 4's rule
(**mention, not non-null**) turned finding 4's omitted `consent` into a decision someone had to write
down. G-2's wiring turned up a G-2-shaped finding of its own: `tests/shared` is linked source, so
`TestTierContractTests` was in no batch at all.

**Round 3 — the tail and the record.** *Planned:* 11, 12, 13, 20, 25, 26, 28, 31; the browser-honesty
items in §4b; and re-stamping `design.md` §0 (correcting O3, adding G-1 as O6), `export.md`,
`dv2d.md`, `wasm-matrix.md` and `00-overview.md`'s layer registry.

*Landed:* the tail as planned — with 25 and 28 **routed rather than built**, each with a test that
pins the absence and states why. Plus three things the plan did not have:

1. **`playback2d.vision` drew nothing** — found by round 2 while registering the real layers. The layer
   read an `IVisionSolver` and ignored the pre-solved `SceneVision` a frame carries, while
   `SceneVision`'s own doc said the layer draws exactly that. It was also the one layer in the stack
   whose `IsEnabled` defaulted `false`, so even a fed one stayed dark unless `Scene2DHost` — the only
   caller of `SetEnabled` for it — pushed the user's toggle. Both halves fixed; three goldens moved.
2. **The budget lane ran one of the two projects that carry `Category=Budget`**, which is the hole
   G-4 lived in. Extended, and the `--budget-bytes-per-frame 4096` override dropped now that the real
   stack measures 0 B/frame.
3. **The six `Environmental` App failures**, which the `app-tests` reporting step's promotion criterion
   turns on. Four of the six were plain bugs wearing the tag — a `:` in a path built from a TUnit test
   id, and a raw-path substring search inside escaped JSON — and both are **Windows-only**, so the
   Linux lane those figures were quoted for had never actually been read. One now skips with a reason
   where the host cannot create a symlink; one was a genuine race between a backlog draining and its
   best-effort save. Two lost the tag with the fix.

**The tag.** `Environmental` was doing a `[Skip]`'s job in four places, the same way
`[Category("Budget")]` was doing it for `BenchAllocationTests` (G-4). **A category that excludes a test
from every lane anyone runs is a skip that nothing makes you justify.**
