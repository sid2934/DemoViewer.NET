# Phase B5 — Polish: WASM verification, feature-flag audit, keybind audit, docs, removal plan

**Branch:** `feature/playback2d-v2` · **Design:** `docs/playback2d-v2/design.md` (authoritative; §5.8, §6,
§7.5, §7.7, §8, §9, §11) · **Effort:** 1 wk · **Depends on:** B1–B4 landed (see [Dependencies](#dependencies))

This plan is self-contained. You do not need to have read the design doc to execute it; every rule it
relies on is quoted or restated here, with file paths and current symbols verified against the tree at
commit `305c5ac`.

> ## Integrator corrections (BINDING — supersede anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry. Risks 1, 6 and 7
> and the "project paths are B0/C1's to fix" note are resolved here.
>
> 1. **B5-1 is an AUDIT, not the insertion.** Each phase adds its own catalog row when it ships:
>    A1 creates the `// ---- 2D PLAYBACK v2 SUB-FEATURES ----` block (at the position B5-1 specifies,
>    after `analysis.breakpoints`, before `// ---- CHROME`) with `playback2d.timeline` and
>    `playback2d.follow`; B2 inserts `playback2d.annotations`, B3 `playback2d.levels.auto`, B4
>    `playback2d.export`. Final order: annotations · timeline · levels.auto · follow · export.
>    B5-1 verifies all five rows match the descriptors below and adds any that are missing.
> 2. **Risk 7 resolved — the gate seam lands in A1, not B5.** A1 already modifies `IModuleContext`,
>    `ModuleContext` and `App.axaml.cs`, so it ships `IModuleFeatureGate`,
>    `IModuleContext.Features`, `ModuleContext.SetFeatures` and `ShellModuleFeatureGate` to B5-2's
>    exact signatures. B5-2 becomes the audit that no second seam exists (no `IFeatureGate` injected
>    into a tab VM anywhere) and that `DesktopOnlyIds` is the single `!IsBrowser()` site.
> 3. **Risk 6 resolved — the legacy toggle is `Playback2DSettings.LegacyViewport`** (B1 uses that
>    name). No rename needed.
> 4. **`Playback2DSettings` — three amendments to the "binding" list below.** (a) Add B2's
>    annotation properties that the list omits: `LastTool`, `AnnotationHoldTicks`,
>    `AnnotationAnchorToEntities`, `AnnotationAutoSave`, `AnnotationRecentColors` (`string[]`,
>    flattened as indexed keys — `SettingsWasmRoundTripTests` must special-case arrays since it
>    reflects over scalars). (b) `AnnotationDefaultVisibility`'s values are `Always | Fade | Custom`
>    (B2's three envelope modes), not `Always | Envelope`. (c) **Drop `ExportBackendOverride`** —
>    C2 owns the one backend key, `RenderBackend` (`"auto" | "cpu" | "gpu"`), used by the export
>    dialog *and* `dv2d`. B3's key is `AutoLevelFollow` (this list's spelling wins over B3's
>    `AutoFollowLevel`).
> 5. **`Playback2DKeymap.All` does not exist — A1's enumerable is `Playback2DKeymap.Default`**
>    (with `Active` and `Reserved` subsets). Retarget `Playback2DKeymapConflictTests`. A1 also has
>    no per-binding "suppress in text input" flag: suppression is A1 D12's single global rule (the
>    tunnelling handler bails when the focused element is a text input), so
>    `SingleLetterBindings_AreSuppressedInTextInput` asserts *that handler's* behaviour, not a flag.
>    A1 already resolved `E` vs `X` the same way B5-5 does — B5 confirms, it does not change A1.
> 6. **Project paths (resolving the "B0/C1's to fix" note):** Core/Pipeline are
>    `src/Playback2D/DemoViewer.NET.Playback2D.{Core,Pipeline}` and there is **one** test project,
>    `src/Playback2D/DemoViewer.NET.Playback2D.Tests`. Every `…Core.Tests` / `…Pipeline.Tests`
>    invocation in this plan collapses to that one project, and `Playback2DWasmBudgetTests` lands
>    there. B5's `core-tests` CI job is therefore **already B0's `playback2d-tests` job** — B5 adds
>    only `wasm-build`.
> 7. **B5-4's provider factory is `…Core.Rendering.RenderSurfaceProviderFactory`** (C2's type, path
>    `Core/Rendering/RenderSurfaceProviderFactory.cs`, not `Core/Surfaces/`). C2 already specifies
>    the browser short-circuit and an injectable platform flag for testing it; B5 verifies rather
>    than adds. If C2 has not landed, B5-4 becomes a one-line guard inside `CpuSurfaceProvider`'s
>    call site and the test moves with the factory.
> 8. **Risk 1 (SkiaSharp on WASM) is accepted as a B0 pull-forward.** It is recorded in
>    `00-overview.md` §6 as the one open item the coordinator must schedule; B5 keeps the fallback
>    (documented degradation in `wasm-matrix.md`), and B0 owns the 1-day spike.
> 9. **`ContractVersion` — A1 already bumps it to `1.2.0`.** B5-9 is the audit that the comment
>    lists every additive `IModuleContext` member actually consumed (A1's five plus `Features`), and
>    that no phase bumped it a second time.

---

## Scope & exit criterion

The design's phase table (§9) row, quoted verbatim:

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **B (core)** | B5 | Polish: WASM verification pass, feature-flag audit, keybind conflict audit, docs, old-control removal (next release) | Release | 1 wk |

"Release" is not a testable statement on its own, so B5's operational exit criterion is the
[Acceptance checklist](#acceptance-checklist) at the bottom of this document. B5 ships **no new user-facing
feature**. It ships: enforcement tests, the gate/settings wiring that B2–B4 consume, three doc updates, and
one written removal plan for the next release.

**Explicitly out of scope for B5:** deleting the old control (that is *next* release — B5 only writes the
plan), the GPU provider backend (C2), any new layer, any new tool.

---

## Ordered work breakdown

Tasks are ≤ half a day each. **Ordering constraints are stated per task.** Tasks B5-1 … B5-4 are the
"contracts other phases consume" block and should land first even if B2–B4 are still in flight — they are
additive and independently testable.

### B5-1 · Feature-catalog block for Playback2D (0.5 d)

*No dependencies. Land first — B2/B3/B4 read these ids.*

**Modify** `src/App/DemoViewer.NET/Features/FeatureCatalog.cs`.

Insert the five descriptors below as one contiguous block in the `_catalog` array, **after the
`analysis.breakpoints` entry (currently lines 135–139) and before the `// ---------------- CHROME` comment
at line 141**. Position matters only for one reason, and it is safe here: the file's header comment (lines
31–34) warns that "a group's LEADER is its FIRST member in `All`". All five new rows have `GroupId: null`,
so inserting them cannot change `parserDeepDive`'s leader (`parser.hex`) or `graphDebug`'s leader
(`analysis.breakpoints`) — exactly the reasoning already recorded for `highlights.encoding` (line 124) and
`chrome.livesync` (line 160). A test in B5-6 pins this.

```csharp
        // ---------------- 2D PLAYBACK v2 SUB-FEATURES ----------------
        // All five: ParentId "tab.playback2d" (cascade off with the tab), no GroupId (so the
        // parserDeepDive / graphDebug leader-lock ordering above is undisturbed), default-ON for every
        // category. These are the headline consumer features of the 2D rework — gating them to
        // power-users would hide the payoff from the audience most excited by it (the same reasoning
        // recorded for tab.highlights). IDS ARE PERSISTED KEYS (settings write Features:Overrides:{id})
        // and must never be renamed. playback2d.export additionally ANDs !OperatingSystem.IsBrowser()
        // — folded in ONE place, ShellModuleFeatureGate.DesktopOnlyIds, never re-derived per call site.
        new(
            "playback2d.annotations", FeatureScope.SubFeature, "Annotations",
            "Draw over the 2D playback — pen and eraser with undo/redo, plus time-anchored drawings "
            + "that appear and disappear on the demo clock.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.timeline", FeatureScope.SubFeature, "Timeline",
            "Scrubbable timeline under the 2D playback with round bands and kill / bomb / annotation "
            + "markers.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.levels.auto", FeatureScope.SubFeature, "Automatic level switching",
            "Switch the visible map level automatically to follow the action. Manual level picking "
            + "stays available with this off.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.follow", FeatureScope.SubFeature, "Follow player",
            "Selecting a player card follows them in the 2D view — and in-engine when Live Sync is "
            + "active.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.export", FeatureScope.SubFeature, "Video export",
            "Render the 2D playback, annotations included, to webm / mp4 / gif. Desktop only — it "
            + "needs a filesystem and an ffmpeg binary.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
```

No other change to this file. `FeatureCatalog.Children("tab.playback2d")` then returns exactly these five,
which is what the Settings FEATURES list (`SettingsViewModel` / `FeatureToggleRow`) renders indented under
the "2D Playback" tab row automatically — no Settings-side code change is required.

### B5-2 · The module-facing feature-gate seam (0.5 d)

*Depends on B5-1. Blocks B2/B3/B4 gate consumption.*

Problem this solves: `Playback2DModule.CreateTabs` builds the tab VM with
`ViewModelFactory = () => new Playback2DTabViewModel()`
(`src/App/DemoViewer.NET/Modules/Playback2D/Playback2DModule.cs:44`) — no DI, no `IFeatureGate`. The module
assembly (`DemoViewer.NET.Modules.Abstractions`) must not reference `DemoViewer.NET.Features`. The
established precedent for handing a shell-owned, gate-folded projection to a module is
`IModuleContext.LiveSyncHud` (`ModuleContext.cs:98`, setter `SetLiveSyncHud` at `:215`, whose doc at `:213`
records that it "folds the gate + session state through `ILiveSyncHudState.IsActive`"). Mirror it.

1. **Create** `src/App/DemoViewer.NET.Modules.Abstractions/IModuleFeatureGate.cs` — the interface in
   [Public API contracts](#public-api-contracts).
2. **Modify** `src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs` — add the default-implemented
   `Features` property (default `null` ⇒ fail-open) next to the other additive members
   (`MapName` at `:24`, `LiveSyncHud`, `GetEventTimeline` at `:163`). Default-implemented so **no existing
   `IModuleContext` test double breaks** — there are 10+ of them in `App.Tests`
   (`Playback2DAdrTests.FakeCtx`, `Playback2DKillFeedTests.FakeCtx`, `Playback2DEventNavTests.RecordingCtx`,
   `Playback2DCameraModeTests.ModeFakeContext`, `Playback2DHeadlessSmokeTests.FakeContext`,
   `Playback2DDeadMarkerTests.FakeCtx`, `Playback2DAreaEffectsTests.FakeCtx`,
   `Playback2DKillFeedRenderTests.Kctx`, `Playback2DReloadResyncTests.ReloadCtx`, …). Adding a
   non-defaulted member would break every one of them.
3. **Create** `src/App/DemoViewer.NET/Features/ShellModuleFeatureGate.cs` — the adapter wrapping the
   singleton `IFeatureGate`, owning the single `!OperatingSystem.IsBrowser()` AND for desktop-only ids.
4. **Modify** `src/App/DemoViewer.NET/Modules/ModuleContext.cs` — add
   `public IModuleFeatureGate? Features { get; private set; }` + `public void SetFeatures(IModuleFeatureGate?)`,
   copying the shape and doc-comment style of `LiveSyncHud`/`SetLiveSyncHud` (`:98`, `:215`).
5. **Modify** `src/App/DemoViewer.NET/App.axaml.cs` — where the `ModuleContext` is composed, call
   `ctx.SetFeatures(new ShellModuleFeatureGate(sp.GetRequiredService<IFeatureGate>()))`. `IFeatureGate` is
   already a registered singleton (`App.axaml.cs:487`). Place the call next to the existing
   `SetLiveSyncHud` wiring so the two shell-projection hookups stay together.

**Reconciliation note for the integrator:** if B2 already introduced a gate seam under a different name,
B5 does **not** add a second one — it renames/reshapes B2's to match the signature below (these signatures
are binding for B3/B4) and keeps the tests.

### B5-3 · `Playback2DSettings` container + `WriteInMemory` flattening (0.5 d)

*Depends on nothing. Blocks B2 (tool prefs), B3 (level mode), B4 (export prefs).*

1. **Modify** `src/App/DemoViewer.NET/Configuration/AppSettings.cs` — add a `Playback2D` property to
   `AppSettings` (after `Idle` at `:64`) and the `Playback2DSettings` class (full shape in
   [Public API contracts](#public-api-contracts)). Binder-safe: every property is a settable scalar with a
   non-null default, matching `HighlightsSettings` (`:201`).
2. **Modify** `src/App/DemoViewer.NET/Configuration/SettingsService.cs` — flatten **every**
   `Playback2DSettings` key into `WriteInMemory` (`:419`). Add the rows after the `ProcessingQueue:*` block
   at `:428–433`, following the exact existing conventions:
   - `bool` → `x ? "true" : "false"`
   - `int` / `double` → `.ToString(CultureInfo.InvariantCulture)`
   - `enum`-shaped values are modelled as `string` here, so they write directly
   - a `string?` that is `null` → **omit the key entirely** (the rebuild-from-scratch `ReplaceAll` at `:447`
     then leaves the bound default in place; writing an empty string would materialise `""`, which is not
     the same value)
3. **Modify** the `WriteInMemory` header comment (`:411–418`). It currently says
   "DELIBERATELY PARTIAL: only the WASM-reachable subset is flattened. The LiveSync and Highlights sections
   are EXCLUDED". Extend it to record the B5 decision: **`Playback2D` is flattened in full, including the
   export keys, even though export is gated off on browser** — the section is small, the Settings screen
   renders it on both hosts, and the "nothing browser-reachable writes it" argument is too fragile for a
   section a user can edit. See [Decisions made](#decisions-made) D3.

### B5-4 · WASM provider guard + legacy-toggle plumbing (0.5 d)

*Depends on B1 (`IRenderSurfaceProvider`, `CpuSurfaceProvider`) and B5-3.*

1. **Modify** the provider factory introduced by B1/C2 (expected
   `src/Playback2D/DemoViewer.NET.Playback2D.Core/Surfaces/RenderSurfaceProviderFactory.cs`; confirm the
   actual path at implementation time). Add, as the **first** statement of the probe, an unconditional
   browser short-circuit returning `CpuSurfaceProvider`, before any GPU probe runs. Design §8: "the CPU
   provider is the only offscreen path there". Note that `Core` must not reference
   `OperatingSystem.IsBrowser()`-adjacent Avalonia types — `System.OperatingSystem` is BCL, so this is
   allowed under the "Core references only SkiaSharp" architecture rule (§11) and the architecture test in
   B5-6 must not flag it.
2. **Modify** `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml` + `.axaml.cs` — verify the
   legacy-toggle branch (from B1) reads `AppSettings.Playback2D.LegacyViewport` and that with the toggle
   **off** (the shipping default) no `Playback2DViewport` instance is constructed at all. Currently
   `Playback2DView.axaml.cs:31` does `this.FindControl<Playback2DViewport>("Viewport")`; after B1 there are
   two candidate hosts and only one may be instantiated.
3. Confirm no `Playback2D` code path calls `FfmpegDependency.Locate()` on browser — it already
   self-guards (`src/App/DemoViewer.NET/Services/Dependencies/FfmpegDependency.cs:30`), but the export
   dialog must not even be reachable: it is behind `playback2d.export`, which B5-2's adapter forces false
   on browser.

### B5-5 · Keybind conflict audit + keymap reconciliation (0.5 d)

*Depends on A1 (`Playback2DKeymap`) and B2 (Draw/Erase tools).*

The shell's app-wide accelerators are declared in `src/App/DemoViewer.NET/Views/MainView.axaml:22–37`:
`Ctrl+P`, `Ctrl+O`, `Ctrl+W`, `Ctrl+OemComma`, `Ctrl+B`, `Ctrl+D1`…`Ctrl+D9`. The only other
`KeyBinding` in the app is `Escape` scoped to the Stats player-details overlay
(`Views/Stats/StatsTabView.axaml:443`), and two code-behind handlers (`HighlightsTabView.axaml.cs:74`
Escape; `RuleWorkbenchView.axaml.cs:192` `Ctrl+Space`).

Audit the design's §7.5 keymap against that set and against itself. **Findings and their resolutions
(binding — the keymap must ship this way):**

| Gesture | Action | Finding | Resolution |
|---|---|---|---|
| `E` | design assigns it to **both** "Q/E round nav" and "erase" | **self-conflict in the design** | Keep `Q`/`E` = prev/next round (nav parity with the rest of the market). **Erase moves to `X`.** `Ctrl+X` stays "clear all drawings" (CS:DM parity), so `X` / `Ctrl+X` read coherently. |
| `Space` | play/pause **and** hold-to-pan-while-drawing (§5.5) | mode-dependent | `Space` is play/pause **unless** a drawing tool (`Draw`/`Erase`) is active; then hold-`Space` is temporary pan and a tap does **not** toggle playback. |
| `←` `→` `↑` `↓` | step / speed | collide with `ItemsControl` arrow navigation once the player cards become **selectable** (A1, design §7.4) | Handle arrows on the focusable `Scene2DHost` and set `KeyboardNavigation.TabNavigation`/arrow handling on the cards list so the list does not swallow them while the host has focus. Verified by test. |
| `Ctrl+X` | clear drawings | collides with the standard **Cut** gesture inside any focused `TextBox` (annotation Text tool, export dialog filename) | Keymap handlers must no-op while `FocusManager.GetFocusedElement()` is a text input. Applies to every single-letter binding. |
| `Ctrl+Z` / `Ctrl+Shift+Z` | undo / redo | no shell conflict | ship as designed; same focused-text-input suppression |
| `D`, `F`, `Q`, `E`, `X` | tools / follow / rounds | no shell conflict (shell is all `Ctrl+`-prefixed) | ship |
| `Esc` | exit tool / bail gesture | conflicts with the Stats overlay `Escape` only when that overlay is open on a different tab | no action — different visual tree, never simultaneously focused |

**Deliverable:** the resolutions above encoded in `Playback2DKeymap` (A1's type) plus the conflict test in
B5-6. If A1 shipped `E` as erase, B5 changes it to `X` and updates A1's tests.

### B5-6 · Enforcement test suite (1 d)

*Depends on B5-1 … B5-5. The bulk of B5's deliverable.*

Six new test classes, detailed in [Test plan](#test-plan). Written to
`src/App/DemoViewer.NET.App.Tests/` except the budget test, which is a direct-execution test in the
Core test project.

### B5-7 · Docs updates (0.5 d)

*Depends on B5-1, B5-5. No code dependency.*

1. **Modify** `README.md` — line 51 currently reads "…breakpoints; a 2D playback view; and a Diagnostics
   tab with live logs and counters." Replace "a 2D playback view" with a phrase naming the v2 capabilities
   (scrubbable 2D playback with drawable annotations and one-click video export). Add a short
   **"Browser build"** note under `## What it does` pointing at `docs/playback2d-v2/wasm-matrix.md`.
2. **Modify** `docs/ui/design-system.md`:
   - §5 category-visibility matrix (starts line 1194): add five rows, one per new feature id, in the same
     column format (`Feature / surface | Scope | Consumer | Power | Dev | Notes`), all `● ● ●`. The
     `playback2d.export` Notes cell must state **"Desktop only (ANDs `!IsBrowser()`)"**, matching the
     existing `chrome.livesync` and `chrome.processingQueue` rows.
   - §2 component contracts (starts line 352): add a `### Playback2D keymap (v2)` subsection carrying the
     full gesture table from B5-5, flagged as **the single source of truth the conflict test reads**.
3. **Create** `docs/playback2d-v2/wasm-matrix.md` — the per-capability browser support matrix (works /
   degraded / absent + the mechanism), filled from the verification pass. Content skeleton in
   [Test plan](#test-plan) → *WASM verification pass*.
4. **Create** `docs/playback2d-v2/old-control-removal.md` — the next-release removal plan (B5-8).

### B5-8 · Old-control removal plan, written not executed (0.5 d)

*Depends on nothing; do it last so the file list is accurate.*

Write `docs/playback2d-v2/old-control-removal.md` containing exactly this, verified against the tree:

**Deleted next release** (design §9: "the old control is retained one release behind an internal toggle"):

| Path | Action |
|---|---|
| `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DViewport.cs` | delete (1,438 loc) |
| `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml` | delete the legacy `<Playback2DViewport x:Name="Viewport"/>` branch |
| `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml.cs` | delete field `_viewport` (`:26`) + `FindControl<Playback2DViewport>` (`:31`); update the class doc (`:15`) |
| `src/App/DemoViewer.NET/Configuration/AppSettings.cs` | delete `Playback2DSettings.LegacyViewport` |
| `src/App/DemoViewer.NET/Configuration/SettingsService.cs` | delete its `WriteInMemory` row |
| `src/App/DemoViewer.NET.UiCapture/Variants.cs` | retarget `["playback2d-canvas"]` (`:153`) and `Pb2DLiveSyncHud`'s `new Playback2DViewport` (`:1158`) to `Scene2DHost`; update the doc at `:3290` |
| `src/App/DemoViewer.NET/Views/Tutorial/TutorialView.axaml.cs:19` | doc-comment reference only — reword |
| `src/App/DemoViewer.NET/Styles/DarkPalette.axaml` | audit the `Pb2d*` token block (design-system.md §248) and delete only tokens with zero remaining references |

**Tests that retarget** (grep-verified reference counts):

| Test class | Refs | Retarget to |
|---|:-:|---|
| `Playback2DInterpolationTests` | 5 | direct-execution against the Core/Pipeline marker interpolation — drop the Avalonia host |
| `Playback2DCameraModeTests` | 5 | direct-execution against `ICameraRig` + `SliceCamera` in Core |
| `GrenadeTrailFloorSplitTests` | 7 | direct-execution against `MapSpace` / `FloorSplitter` in Pipeline |
| `ZRadarRenderTests` | 1 | CPU-provider golden test in the Core test project |
| `ZTrajectoryRenderTests` | 2 | CPU-provider golden test in the Core test project |
| `ZVisionOverlayRenderTests` | 1 | CPU-provider golden test in the Core test project |

**Removal trigger (all must hold):** v2 default-on has shipped in one tagged release; no open bug whose
only workaround is `LegacyViewport=true`; the six classes above are green in their retargeted form; the
`Playback2DLegacyToggleTests` class is deleted in the same commit.

### B5-9 · ContractVersion bump audit (0.25 d)

*Do after B2–B4 have landed; it enumerates what they actually consumed.*

**Modify** `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DModule.cs:30`.
`ContractVersion` is currently `new(1, 1, 0)` with the comment "1.1: consumes `IModuleContext.MapName`
(additive)". Nothing in the codebase *enforces* it (`grep ContractVersion` finds only the five module
declarations and the interface at `IWorkspaceModule.cs:21`) — it is a documented claim, so the audit is a
human read plus a pinning test.

Bump to `new(1, 2, 0)` and replace the comment with the full list of additive `IModuleContext` members the
v2 module now consumes. Verify each by grep before listing it; the expected set is:

- `Features` (B5-2, new)
- `GetEventTimeline(string)` — timeline `KillTrack`/`BombTrack` (§5.6)
- `AvailableEventNames` — "markers only for events the demo has" (§7.6)
- `NotifySpectateTarget(int)` — follow → LiveSync (§7.4)
- `MapName` — already claimed by 1.1

**One bump per release, minor**, listing every newly consumed additive member. Do not bump per phase — B2,
B3 and B4 all land inside this release.

---

## Public API contracts

Binding for other phases. Signatures obey the repo style (`.editorconfig`: file-scoped namespaces,
Allman braces, explicit types over `var`, 120-col, 4-space indent, `#region`-wrapped usings).

### `IModuleFeatureGate` — new file `src/App/DemoViewer.NET.Modules.Abstractions/IModuleFeatureGate.cs`

```csharp
namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     The read-only, shell-owned feature-gate projection handed to a module through
///     <see cref="IModuleContext.Features" />. Deliberately a projection, not the app's IFeatureGate: the
///     abstractions assembly must stay free of the Features namespace, and the shell folds
///     platform ANDs (e.g. desktop-only ids) in on this side of the seam so a module never re-derives them.
/// </summary>
public interface IModuleFeatureGate
{
    /// <summary>
    ///     Whether <paramref name="featureId" /> is live RIGHT NOW. Re-query on <see cref="Changed" />;
    ///     never cache the answer for the lifetime of a tab. An id the host does not know fails OPEN
    ///     (returns <c>true</c>), matching IFeatureGate's own contract.
    /// </summary>
    bool IsEnabled(string featureId);

    /// <summary>Raised on the UI thread when any gate answer may have changed.</summary>
    event Action? Changed;
}
```

### `IModuleContext` — additive member (`src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs`)

```csharp
    /// <summary>
    ///     The live feature-gate projection, or <c>null</c> for a host / test double that does not gate.
    ///     <b>Null fails OPEN</b> — a module with no gate shows everything, exactly as it did before gating
    ///     existed. Default-implemented so every existing IModuleContext implementation keeps compiling.
    /// </summary>
    IModuleFeatureGate? Features => null;
```

### `ShellModuleFeatureGate` — new file `src/App/DemoViewer.NET/Features/ShellModuleFeatureGate.cs`

```csharp
public sealed class ShellModuleFeatureGate : IModuleFeatureGate, IDisposable
{
    /// <summary>
    ///     Feature ids that additionally require a desktop host. The ONE place the
    ///     <c>!OperatingSystem.IsBrowser()</c> AND for module features lives (design §7.7: playback2d.export
    ///     "additionally AND !OperatingSystem.IsBrowser(), like chrome.livesync").
    /// </summary>
    public static IReadOnlySet<string> DesktopOnlyIds { get; }   // { "playback2d.export" }

    public ShellModuleFeatureGate(IFeatureGate gate);

    public bool IsEnabled(string featureId);
    public event Action? Changed;
    public void Dispose();
}
```

### `ModuleContext` — additive members (`src/App/DemoViewer.NET/Modules/ModuleContext.cs`)

```csharp
    /// <inheritdoc />
    public IModuleFeatureGate? Features { get; private set; }

    /// <summary>
    ///     Sets the shell's feature projection ONCE at composition. Mirrors <see cref="SetLiveSyncHud" />:
    ///     never cleared, because the projection itself reports the live answer.
    /// </summary>
    public void SetFeatures(IModuleFeatureGate? features) => Features = features;
```

### `Playback2DSettings` — new class in `src/App/DemoViewer.NET/Configuration/AppSettings.cs`

Property names are binding — they are persisted config keys (`Playback2D:AnnotationColorArgb`, …) and the
`WriteInMemory` flattening and its enforcement test key off them. Other phases **add** properties to this
class; they do not create sibling sections.

```csharp
public sealed class AppSettings
{
    // … existing properties …

    /// <summary>2D playback (v2) preferences — annotation tool defaults, level display, timeline, export.</summary>
    public Playback2DSettings Playback2D { get; set; } = new();
}

/// <summary>
///     2D-playback v2 preferences. Binder-safe (every property a settable scalar with a non-null default).
///     EVERY property here is flattened by <c>SettingsService.WriteInMemory</c> — a property added without
///     a matching flatten row is silently discarded on the WASM head, which is what
///     <c>SettingsWasmRoundTripTests</c> exists to prevent.
/// </summary>
public sealed class Playback2DSettings
{
    // ── Annotations (B2) ──
    /// <summary>Default ink colour as packed ARGB. Default 0xFFFFC107 (AccentAmber).</summary>
    public uint AnnotationColorArgb { get; set; } = 0xFFFFC107;

    /// <summary>Default stroke width in WORLD units (annotations live in world space).</summary>
    public double AnnotationWidth { get; set; } = 8;

    /// <summary>Default ink opacity, 0..1.</summary>
    public double AnnotationOpacity { get; set; } = 1;

    /// <summary>Last active tool — "PanZoom" | "Draw" | "Erase". String, not an enum, so an unknown
    /// value from a newer build binds harmlessly instead of throwing.</summary>
    public string LastTool { get; set; } = "PanZoom";

    /// <summary>Default TimeEnvelope fade-in, in DV frame-clock ticks.</summary>
    public int AnnotationFadeInTicks { get; set; } = 8;

    /// <summary>Default TimeEnvelope fade-out, in DV frame-clock ticks.</summary>
    public int AnnotationFadeOutTicks { get; set; } = 16;

    /// <summary>Default visibility for a NEW element — "Always" | "Fade" | "Custom" (B2's
    /// EnvelopeMode names; correction 4b).</summary>
    public string AnnotationDefaultVisibility { get; set; } = "Always";

    // ── Annotations (B2), completing the list (correction 4a) ──
    /// <summary>Last active tool — "PanZoom" | "Draw" | "Erase". String so an unknown value from a
    /// newer build binds harmlessly.</summary>
    public string LastTool { get; set; } = "PanZoom";
    /// <summary>Hold duration for a "pin to now" element, in DV frame-clock ticks (5 s at 64 tick).</summary>
    public int AnnotationHoldTicks { get; set; } = 320;
    /// <summary>Draw new strokes anchored to the nearest player (SteamId) rather than to the level.</summary>
    public bool AnnotationAnchorToEntities { get; set; }
    /// <summary>Autosave the annotation sidecar on a debounce.</summary>
    public bool AnnotationAutoSave { get; set; } = true;
    /// <summary>Recent ink colours, most-recent-first. Flattened as Playback2D:AnnotationRecentColors:{i}.</summary>
    public string[] AnnotationRecentColors { get; set; } = [];

    // ── Levels (B3) ──
    /// <summary>Level layout — "Stacked" (today's bands) | "Single".</summary>
    public string LevelDisplayMode { get; set; } = "Stacked";

    /// <summary>Auto-switch the visible level to the followed player's. Also needs playback2d.levels.auto.</summary>
    public bool AutoLevelFollow { get; set; } = true;

    // ── Timeline (A1 / B3) ──
    public bool TimelineShowKills { get; set; } = true;
    public bool TimelineShowBomb { get; set; } = true;
    public bool TimelineShowAnnotations { get; set; } = true;

    // ── Export (B4) ── mirrors HighlightsSettings' reel knobs
    /// <summary>Container / codec preset id — "webm" | "mp4" | "gif".</summary>
    public string ExportFormatId { get; set; } = "webm";

    public int ExportFps { get; set; } = 30;
    public int ExportWidth { get; set; } = 1920;
    public int ExportHeight { get; set; } = 1080;

    /// <summary>Output directory; null = prompt in the dialog (same contract as ReelOutputDirectory).</summary>
    public string? ExportOutputDirectory { get; set; }

    public bool ExportIncludeHud { get; set; } = true;
    public bool ExportIncludeAnnotations { get; set; } = true;

    // ── Render backend (C2) ── correction 4c: ONE key, not an export-only override. Parsed by
    // RenderBackendPreferenceParser; an unknown value falls back to "auto" without throwing.
    /// <summary>"auto" | "cpu" | "gpu". Mirrors dv2d --cpu/--gpu/--backend (design §5.8).</summary>
    public string RenderBackend { get; set; } = "auto";

    // ── Migration (B1; DELETED next release — see docs/playback2d-v2/old-control-removal.md) ──
    /// <summary>Mounts the pre-v2 Playback2DViewport instead of Scene2DHost. Temporary escape hatch.</summary>
    public bool LegacyViewport { get; set; }
}
```

### `SettingsService.WriteInMemory` — added rows

```csharp
            new KeyValuePair<string, string?>("Playback2D:AnnotationColorArgb",
                settings.Playback2D.AnnotationColorArgb.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:AnnotationWidth",
                settings.Playback2D.AnnotationWidth.ToString(CultureInfo.InvariantCulture)),
            // … one row per property; string? properties are appended conditionally (omit when null) …
```

### Feature ids (string constants, persisted keys — never renamed)

`playback2d.annotations` · `playback2d.timeline` · `playback2d.levels.auto` · `playback2d.follow` ·
`playback2d.export` — all `FeatureScope.SubFeature`, `ParentId "tab.playback2d"`, `GroupId null`,
`Required false`, `Defaults(true, true, true)`.

Unchanged and pinned by test: `TabId "playback2d.viewport"`, tab feature id `"tab.playback2d"`
(`MainViewModel.cs:98` maps between them), module `Id "net.demoviewer.playback2d"`.

### `Playback2DModule.ContractVersion`

`new(1, 2, 0)` at release.

---

## Test plan

All tests are TUnit (`[Test]`, `await Assert.That(x).IsEqualTo(y)`), class-level `[NotInParallel]` where
they touch shared state, and live in `src/App/DemoViewer.NET.App.Tests/` unless stated. Test method names
use underscores (the csproj already carries `NoWarn=$(NoWarn);CA1707`).

### 1. `Playback2DFeatureCatalogTests` — direct execution, no Avalonia

Follows `FeatureGateTests` (which constructs `new FeatureGate(monitor, marshalChangedToUiThread: false)`
via the internal test ctor at `FeatureGate.cs:45` — no dispatcher needed).

| Case | Asserts |
|---|---|
| `AllFiveIds_Present_AsSubFeaturesOfPlayback2dTab` | `FeatureCatalog.Children("tab.playback2d")` ids == the exact five, in the specified order |
| `Playback2dIds_HaveNoGroupId` | none of the five sets `GroupId` (the leader-lock precondition) |
| `GroupLeaders_Unchanged_AfterInsert` | `GroupLeader("parserDeepDive").Id == "parser.hex"`, `GroupLeader("graphDebug").Id == "analysis.breakpoints"` — duplicates `FeatureGateTests.GroupLeaders_AreStable` deliberately, as an insert-position regression net |
| `Defaults_OnForEveryCategory` | Consumer/PowerUser/Developer all resolve true with empty overrides |
| `Cascade_TabOff_ForcesAllFiveOff` | `Overrides["tab.playback2d"]=false` ⇒ all five `IsEnabled` false |
| `Override_TurnsOneOff_WithoutTouchingSiblings` | `Overrides["playback2d.export"]=false` leaves the other four on |

### 2. `Playback2DFeatureWiringTests` — direct execution

Guards "declared but never consumed" — the failure mode a catalog entry has when its phase forgot to gate.

| Case | Asserts |
|---|---|
| `EveryPlayback2dId_IsReferencedInAppSources` | for each of the five ids, a literal-string scan of `src/App/DemoViewer.NET/**/*.cs` + `**/*.axaml` (excluding `FeatureCatalog.cs` itself) finds ≥1 hit |
| `ExportId_IsInDesktopOnlySet` | `ShellModuleFeatureGate.DesktopOnlyIds.Contains("playback2d.export")` and the other four are absent |
| `CoreAndPipeline_NeverReferenceFeatureGating` | source scan of the Core / Pipeline / Cli project dirs finds zero `IFeatureGate`, `FeatureCatalog`, `playback2d.` id literals — design §7.7: "the CLI takes explicit flags instead" |

Repo-root resolution: walk up for `DemoViewer.NET.slnx`, the same technique
`DemoTestHelper` (`src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs`) already uses.

### 3. `ShellModuleFeatureGateTests` — direct execution

| Case | Asserts |
|---|---|
| `Delegates_ToUnderlyingGate` | fake `IFeatureGate` answers flow through |
| `DesktopOnlyId_IsFalse_OnBrowser_TrueOtherwise` | drive the browser branch through an injectable `Func<bool> isBrowser` test seam (do **not** try to fake `OperatingSystem.IsBrowser()`); on desktop the answer is the gate's |
| `Changed_ReRaised_FromUnderlyingGate` | subscribing to the adapter sees the gate's `Changed` |
| `NullFeatures_FailOpen` | an `IModuleContext` with `Features == null` (the default impl) — the consuming VM shows everything |

### 4. `SettingsWasmRoundTripTests` — direct execution

The mechanical guarantee that no persisted key can be added without a `WriteInMemory` row. Black-box: no
change to `SettingsService`'s private surface. Uses the fileless ctor `new SettingsService(null)` — the
same WASM path `SettingsServiceTests.WriteInMemory_ShrinkAndRemove_DropStaleKeys` (`:259`) already exercises.

| Case | Asserts |
|---|---|
| `EveryPlayback2dProperty_SurvivesAFilelessWrite` | reflect over `typeof(Playback2DSettings)` public settable props; for each, `Write(s => set a non-default value)` then assert `svc.Current.Playback2D.<prop>` equals it. Reflection-driven ⇒ **covers properties other phases add later** |
| `NullStringProperty_OmitsKey_AndBindsDefault` | `ExportOutputDirectory = null` round-trips as null, not `""` |
| `Shrink_DropsStaleKeys` | set then unset; the `ReplaceAll` rebuild leaves no stale key |
| `RootAndProcessingQueue_StillRoundTrip` | regression net over the pre-existing flattened set |

### 5. `Playback2DKeymapConflictTests` — direct execution

| Case | Asserts |
|---|---|
| `NoDuplicateGesture_WithinTheKeymap` | `Playback2DKeymap.Default` (correction 5 — there is no `All`) has no repeated gesture *within a scope* — the test that would have caught the design's `E` double-assignment. A1's static ctor already throws on a non-empty `FindConflicts`; this is the second, independent net |
| `NoCollisionWithShellAccelerators` | parses `src/App/DemoViewer.NET/Views/MainView.axaml` at test time, extracts every `<KeyBinding Gesture="…">`, and asserts the intersection with the keymap is empty. **Parse the file, don't mirror the list** — a mirrored constant drifts the moment someone adds a shell accelerator |
| `EraseIsX_NotE` | pins the B5-5 resolution so a later edit re-introducing the clash fails loudly |
| `SingleLetterBindings_AreSuppressedInTextInput` | the keymap marks single-letter and `Ctrl+X`/`Ctrl+Z` actions as text-input-suppressed |

### 6. `Playback2DWasmBudgetTests` — **direct execution, Core test project**

Design §6: "A WASM frame-budget smoke test (relaxed budget, CPU path) keeps the browser target honest."
A real browser run is not achievable in this repo's CI (no wasm test host, no GPU-less browser runner), so
this is a **browser-shaped** proxy on the desktop CPU path, plus a build job (see Build & wiring) and a
manual checklist (below). Recorded as decision D5.

| Case | Asserts |
|---|---|
| `CpuProvider_MeetsRelaxedBudget_AtBrowserViewport` | 512 frames of a full `SceneFixture` scene (10 players, trails, vision, annotations) at 1280×720 through `CpuSurfaceProvider`: `Advance` p99 ≤ 4 ms, `Render` p99 ≤ 24 ms, combined p99 ≤ 32 ms |
| `SteadyState_AllocatesZeroBytes` | `GC.GetAllocatedBytesForCurrentThread()` delta across the last 256 frames == 0 after warmup (same hard rule as desktop — single-threaded WASM makes a gen-0 pause worse, not more forgivable) |
| `ProviderFactory_ReturnsCpu_WhenBrowser` | drive the factory's injectable `isBrowser` seam ⇒ `RenderBackend.CpuRaster`, and no GPU probe ran |

**Manual WASM verification checklist** (run once per release on a real browser build; the output fills
`docs/playback2d-v2/wasm-matrix.md`):

```
dotnet workload install wasm-tools
dotnet run --project src/App/DemoViewer.NET.Browser -c Release
```
then confirm, per design §8: core rendering works · annotations draw and undo **in session** · a reload
loses them and the UI *says so* · levels switch · 2D follow works · keybinds work · timeline scrubs ·
**Video export is absent from the UI entirely** · Settings shows the five feature rows and toggling one
takes effect live · no console exception mentioning ffmpeg, GRContext, or a filesystem path.

### 7. `Playback2DLegacyToggleTests` — **headless Avalonia** (`HeadlessSession`)

The only B5 class that needs the Avalonia host. Follows `Playback2DHeadlessSmokeTests` (frame capture to
`HeadlessSession.ArtifactDir`).

| Case | Asserts |
|---|---|
| `ToggleOff_MountsScene2DHost_AndRendersAFrame` | default settings ⇒ `Scene2DHost` in the visual tree, **no** `Playback2DViewport` instance; PNG written |
| `ToggleOn_MountsLegacyViewport_AndRendersAFrame` | `LegacyViewport=true` ⇒ the old control mounts; PNG written |

Deleted wholesale by the removal commit next release.

### 8. `Playback2DContractVersionTests` — direct execution

| Case | Asserts |
|---|---|
| `ContractVersion_IsPinned` | `new Playback2DModule().ContractVersion == new Version(1, 2, 0)` — forces a conscious edit + audit next time |
| `TabAndFeatureIds_AreStable` | `TabId "playback2d.viewport"`, module `Id "net.demoviewer.playback2d"`, and `MainViewModel`'s map entry `playback2d.viewport → tab.playback2d` |

### Commands

```bash
# One class (TUnit tree-node filter, the form scripts/test-app-suite.sh uses):
dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- \
  --treenode-filter "/*/*/Playback2DFeatureCatalogTests/*"

# All B5 App-side classes:
dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- --treenode-filter \
 "/*/*/(Playback2DFeatureCatalogTests|Playback2DFeatureWiringTests|ShellModuleFeatureGateTests|SettingsWasmRoundTripTests|Playback2DKeymapConflictTests|Playback2DLegacyToggleTests|Playback2DContractVersionTests)/*"

# The whole App suite (batched — a single process OOMs; see the script header):
scripts/test-app-suite.sh -c Release

# The Core direct-execution budget test (fast, no Avalonia):
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release -- \
  --treenode-filter "/*/*/Playback2DWasmBudgetTests/*"

# Browser head compiles (the WASM regression net):
dotnet workload install wasm-tools && dotnet build src/App/DemoViewer.NET.Browser -c Release
```

---

## Build & wiring

**No new projects.** B5 adds files to existing projects only:
`DemoViewer.NET`, `DemoViewer.NET.Modules.Abstractions`, `DemoViewer.NET.App.Tests`, and one test class in
B0's `DemoViewer.NET.Playback2D.Core.Tests`.

**`DemoViewer.NET.slnx`:** no change (the new-format XML solution needs a `<Project Path=…/>` line only for
new projects; B0/C1 add theirs).

**`Directory.Packages.props`:** no new package ids. B5 introduces no dependency. *(Version policy, for the
record: this repo uses Central Package Management — `PackageReference` items carry **no** `Version`
attribute; every version is pinned exactly once in `Directory.Packages.props`, Avalonia sub-packages are
kept in lockstep at 11.3.12, and the three `CS2DemoKit.*` packages bump together in one commit.)*

**`.github/workflows/ci.yml` — two additions.** The current file has a single `build` job on
`ubuntu-latest` that runs `dotnet build src/App/DemoViewer.NET.Desktop -c Release`, and its header comment
explicitly records that it "does NOT build the Browser/WASM head (needs the wasm-tools workload)". B5's
WASM verification pass is worthless if nothing ever compiles that head, so:

```yaml
  wasm-build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0          # Nerdbank.GitVersioning needs full history
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      # ~2-4 min. A SEPARATE job so it never slows the desktop build's feedback.
      - name: Install wasm-tools
        run: dotnet workload install wasm-tools
      # The WASM regression net for Playback2D.Core/Pipeline: proves the new Skia-facing
      # projects still compile for net10.0-browser. See docs/playback2d-v2/wasm-matrix.md.
      - name: Build Browser head (Release)
        run: dotnet build src/App/DemoViewer.NET.Browser -c Release

  core-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      # The Playback2D direct-execution suites are fast and NOT OOM-prone (no Avalonia
      # platform, no ParsedDemo cache) — unlike the App UI suite, which still needs
      # scripts/test-app-suite.sh and stays out of CI.
      - name: Core + Pipeline tests
        run: |
          dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
          dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
```

Also update `ci.yml`'s header comment: the "does NOT build the Browser/WASM head" sentence is no longer
true once `wasm-build` exists.

**Project paths above are B0/C1's to fix.** Confirm the actual Core/Pipeline test project paths at
implementation time and correct the two `dotnet run` lines; everything else in this plan is
path-independent.

**Style rules that will otherwise fail the build:** `Directory.Build.props` sets
`TreatWarningsAsErrors=true` + `EnforceCodeStyleInBuild=true` + `AnalysisMode=Recommended` +
`GenerateDocumentationFile=true`. Every new public member needs an XML doc comment; every new file needs
the `#region`/usings/`#endregion` header; file-scoped namespace; Allman braces; explicit types (no `var`);
LF endings; 120-column soft limit.

---

## Dependencies

### Consumed from other phases

| Phase | API | Used by B5 for |
|---|---|---|
| A1 | `Playback2DKeymap` — the declarative action→gesture table, with an enumerable `All` of gestures and a per-action "suppress in text input" flag | B5-5 audit + `Playback2DKeymapConflictTests` |
| B0 | `SceneFixture` (JSON scene fixtures under `tests/fixtures/playback2d/`), the `DemoViewer.NET.Playback2D.Core.Tests` project | `Playback2DWasmBudgetTests` |
| B0/B1 | `IRenderSurfaceProvider` / `CpuSurfaceProvider` / `RenderBackend` (design §5.8) and the provider factory | browser short-circuit (B5-4) + budget test |
| B1 | `Scene2DHost : Control`; the legacy-viewport toggle branch in `Playback2DView.axaml` | `Playback2DLegacyToggleTests`, removal plan |
| B1 | `SceneCompositor` / `ISceneLayer.IsEnabled` (a gated-off feature's layers are skipped) | wiring audit |
| B2 | annotation tool prefs written into `AppSettings.Playback2D` | `SettingsWasmRoundTripTests` (reflection-driven, so it picks them up automatically) |
| B4 | `SceneExportSession`, export dialog, ffmpeg sink | `playback2d.export` gate + desktop-only AND |

### Exported by B5 (who consumes them)

| API | Consumers |
|---|---|
| The five `FeatureCatalog` ids + their exact descriptor rows | B2 (`playback2d.annotations`), B3 (`playback2d.levels.auto`), B4 (`playback2d.export`), A1 (`playback2d.timeline`, `playback2d.follow`) |
| `IModuleFeatureGate` + `IModuleContext.Features` + `ShellModuleFeatureGate` | every B-phase that gates a layer, tool, or chrome element |
| `Playback2DSettings` (the container + naming convention) | B2, B3, B4 add properties to it |
| The `WriteInMemory` flattening convention + `SettingsWasmRoundTripTests` | any future phase adding a persisted key |
| The reconciled keymap (`X` = erase; `Space` mode rule; arrow-key ownership) | A1, B2 |
| `docs/playback2d-v2/old-control-removal.md` | the next release's cleanup commit |

---

## Risks & spikes

| # | Risk | Impact | Mitigation / time-box |
|---|---|---|---|
| 1 | **SkiaSharp on the browser head.** `Directory.Packages.props` has **no** `SkiaSharp` entry today — only `Avalonia.Skia 11.3.12`. B0's Core project takes a direct `SkiaSharp` reference, and running managed Skia under `net10.0-browser` typically also needs `SkiaSharp.NativeAssets.WebAssembly` + `<WasmBuildNative>true</WasmBuildNative>` in `DemoViewer.NET.Browser.csproj`, at a version that agrees with the one Avalonia.Browser resolves. A mismatch is a runtime `DllNotFoundException`, not a build error. | High — the whole WASM story | **Spike: 1 day, and PULL IT FORWARD to B0** rather than discovering it in B5. Deliverable: the Browser head runs one `CpuSurfaceProvider` render. If it cannot be made to work, the fallback is that Playback2D v2 on browser keeps using the Avalonia Skia lease only and the offscreen CPU provider is desktop-only — a documented degradation in `wasm-matrix.md`, not a blocker. |
| 2 | Arrow keys stolen by the now-selectable player-card `ItemsControl` (A1) | Medium — transport keys silently dead | ½ day inside B5-5; covered by a keymap test + the manual checklist |
| 3 | `Ctrl+X` / `Ctrl+Z` shadowing Cut/Undo in a focused `TextBox` (annotation Text tool, export filename) | Medium — data loss feel | focused-element suppression, tested; ½ day |
| 4 | A phase adds a persisted key and forgets `WriteInMemory` (the exact failure design §8 warns about) | Medium — silent WASM data loss | the reflection-driven round-trip test makes it mechanically impossible for `Playback2DSettings`; no time-box (it is B5-3/B5-6) |
| 5 | `wasm-tools` workload install adds 2–4 min to CI | Low | separate job; desktop feedback unaffected |
| 6 | Legacy-toggle name collision — B1 may have shipped a different name/location than `Playback2DSettings.LegacyViewport` | Low | B5 adopts B1's actual name and updates this plan + the removal doc; ≤1 h |
| 7 | B2/B3/B4 each invented their own gate seam before B5-2 landed | Medium — three seams to reconcile | B5-2 is written to be landed FIRST; if not, reconcile to the single signature above, ½ day |
| 8 | Catalog insert position accidentally re-parents a group leader | Low — but corrupts every user's grouped overrides | `GroupLeaders_Unchanged_AfterInsert` test; already covered |

---

## Decisions made

Recorded here because the design left them open or self-contradicted.

- **D1 — `E` is round-nav, `X` is erase.** Design §7.5 assigns `E` to both "Q/E round nav" and "erase".
  The keybind audit is the phase that must resolve it: rounds keep `Q`/`E`; erase becomes `X`, which pairs
  coherently with `Ctrl+X` = clear all drawings (CS:DM parity retained).
- **D2 — the module feature seam is `IModuleContext.Features`, an additive default-`null` projection.**
  Not an `IModuleHost` addition (creation-time only; gates change live), not a DI injection into the tab VM
  (the module factory has no container). It mirrors the existing `LiveSyncHud` projection, which already
  folds a feature gate on the shell side.
- **D3 — the whole `Playback2D` settings section is flattened into `WriteInMemory`, export keys
  included**, breaking with the "deliberately partial" precedent that excludes `LiveSync` and `Highlights`.
  Those exclusions rest on "no browser code path writes them"; this section is user-editable in a Settings
  screen that renders on both hosts, so the argument does not hold and the cost is a handful of string rows.
- **D4 — the `!IsBrowser()` AND lives in exactly one place**, `ShellModuleFeatureGate.DesktopOnlyIds`, not
  inline at each call site the way `MainViewModel.IsLiveSyncEnabled` (`:1111`) and
  `IsProcessingQueueEnabled` (`:1121`) do it. Consolidating those two pre-existing call sites is
  deliberately **out of B5's scope** — a follow-up, not a polish-phase refactor.
- **D5 — the "WASM frame-budget smoke test" is a browser-shaped CPU-path proxy plus a build job plus a
  manual checklist**, not an in-browser automated run. The repo has no wasm test host and CI has no browser
  runner; claiming automated browser coverage would be false. The build job is the part that actually
  catches regressions automatically.
- **D6 — new feature ids default ON for all three categories.** They are the release's headline consumer
  features; the `tab.highlights` precedent in the design-system matrix explicitly rejects gating a headline
  payoff away from the audience most excited by it.
- **D7 — ContractVersion bumps once per release** (1.1.0 → 1.2.0), listing every newly consumed additive
  member, rather than once per phase.
- **D8 — the legacy escape hatch is a settings scalar, not a feature id.** Feature ids are permanent
  persisted keys ("chosen once, never renamed"); a toggle scheduled for deletion in one release must not
  enter that namespace.

---

## Acceptance checklist

Maps to the design exit criterion ("Release") item by item; the first block is the assignment's own scope
list, the second is B5's additions.

**WASM verification pass**
- [x] `playback2d.export` resolves **false** on the browser head, via `ShellModuleFeatureGate.DesktopOnlyIds` — one place, tested
- [x] The other four ids resolve identically on both hosts (no accidental platform AND)
- [x] Every `Playback2DSettings` property has a `WriteInMemory` row; `SettingsWasmRoundTripTests` green, reflection-driven so it covers later additions
- [x] The render-surface provider factory returns `CpuSurfaceProvider` on browser **before** any GPU probe runs
- [x] `Playback2DWasmBudgetTests` green: relaxed budget (Advance p99 ≤ 4 ms, Render p99 ≤ 24 ms, combined p99 ≤ 32 ms @ 1280×720) and zero steady-state allocation
- [x] `wasm-build` job added — and it **publishes** rather than builds, because `dotnet build` of
      this head is green in states where the app cannot boot. Verified locally (`dotnet publish` green,
      payload asserted); the job itself has not yet run on a GitHub runner.
- [x] The manual browser checklist has been run once and `docs/playback2d-v2/wasm-matrix.md` reflects the result — including "annotations are session-only, and the UI says so"

**Feature-flag audit**
- [x] All five ids present in `FeatureCatalog`, `FeatureScope.SubFeature`, `ParentId "tab.playback2d"`, no `GroupId`, `Defaults(true, true, true)`
- [x] Group leaders unchanged (`parser.hex`, `analysis.breakpoints`) — pinned by test
- [x] Every id is consumed by at least one non-catalog source file — pinned by test
- [x] Turning a tab off cascades all five off; turning one sub-feature off leaves the others alone
- [x] Core / Pipeline / `dv2d` reference **zero** feature-gating types or id literals (design §7.7)
- [x] The five rows appear in Settings under "2D Playback" and toggling one takes effect live (no restart)

**Keybind conflict audit**
- [x] No duplicate gesture inside `Playback2DKeymap` (`E` double-assignment resolved: erase = `X`)
- [x] No intersection with the shell accelerators, asserted by **parsing `MainView.axaml`**, not by a mirrored list
- [x] Single-letter and `Ctrl+X`/`Ctrl+Z`/`Ctrl+Shift+Z` actions are suppressed while a text input has focus
- [x] Arrow keys reach the transport, not the selectable player-card list
- [x] `Space` is play/pause except while a drawing tool is active (then hold-to-pan, no playback toggle)

**Docs**
- [x] `README.md` names the v2 capabilities and links the browser-support note
- [x] `docs/ui/design-system.md` §5 carries five new matrix rows, with the export row marked desktop-only
- [x] `docs/ui/design-system.md` §2 carries the `### Playback2D keymap (v2)` table — the source of truth the conflict test mirrors
- [x] `docs/playback2d-v2/wasm-matrix.md` exists and is filled in
- [x] `docs/playback2d-v2/old-control-removal.md` exists, lists all 8 file actions and all 6 retargeting test classes, and states the removal trigger

**ContractVersion**
- [x] `Playback2DModule.ContractVersion == new Version(1, 2, 0)`, comment lists every newly consumed additive `IModuleContext` member, each grep-verified
- [x] `Playback2DContractVersionTests` pins the version and the stable ids (`playback2d.viewport`, `tab.playback2d`, `net.demoviewer.playback2d`)

**Release gate**
- [x] `scripts/test-app-suite.sh -c Release` runs (under **bash**, after B5 fixed its 1-indexed
      partition) and the audit passes: 895 run ≥ 869 listed. **6 failures, all the known
      environmental set** (A1 deviation 21 / B3 deviation 8) — `DiagnosticsFileLogTests` ×3,
      `Scan_DeduplicatesSameFile_AcrossSymlinkedFolders`,
      `SettingsBacked_AddRemoveFolder_WritesThroughToSettingsJson`,
      `QueuePath_PersistsCache_SoSecondLaunchDoesNotReparse`. None in a subsystem B5 touches.
- [x] `dotnet build src/App/DemoViewer.NET.Desktop -c Release` green with `TreatWarningsAsErrors`
- [x] No new package added to `Directory.Packages.props` by B5
- [x] The legacy toggle is **off** by default and the old control is not constructed when it is off

---

## Implementation notes (deviations)

Written at implementation time. Everything not listed here was done as the plan body and the
`Integrator corrections` block specify.

B5 arrived last, so most of its "build this" tasks were already built: **B5-1** (the five catalog
rows), **B5-2** (the gate seam, shipped by A1 per correction 2), **B5-3**'s container, **B5-4**'s
browser short-circuit (C2's `ProbeCore`) and legacy-toggle branch, **B5-5**'s `X`-is-erase
resolution, and **B5-9**'s `ContractVersion` bump. Each was verified against the plan's stated shape
before being ticked, and the verification is now a test rather than a reading. What follows is what
differed, and what the verification found.

### The audits found four things, not zero

1. **Three registry §3.10 settings keys did not exist:** `TimelineShowKills`, `TimelineShowBomb`,
   `TimelineShowAnnotations`. A1 shipped the timeline's footer check-boxes as **session** state, so a
   user who turned kill markers off got them back on the next launch. Added, flattened, and wired
   through the same load/save seam the level strip already uses — with a `RestoreTrackEnabled` that
   does **not** echo back out as a change to save, because writing settings from a constructor turns a
   read-only config directory into a swallowed exception on every tab open. `TrackVisibilityChanged`
   is deliberately not raised for an availability change: "this demo has no bomb" is a property of the
   demo, and persisting it would carry to the next one.

2. **`RenderBackend` (registry §3.10, C2.8) was NOT added, deliberately.** It is the one canonical
   property still missing from `Playback2DSettings`, and adding it in B5 would have been worse than
   leaving it out: its only App-side consumer would be the export path, and `SceneExportSession`
   currently **refuses** any provider whose backend is not `CpuRaster` (B4 deviation 26 — the session
   awaits its sink between frames and `GpuSurfaceProvider` is thread-affine). A persisted
   `RenderBackend=gpu` would therefore be a setting whose only effect is to make exports fail. It
   belongs to C2 Stage 1, in the commit that makes a GPU export work. `SettingsWasmRoundTripTests` is
   reflection-driven, so it will cover the property the day it appears, with no test edit.

3. **`playback2d.annotations` is both a feature id (§3.10) and a layer id (§3.3).** The plan's
   `CoreAndPipeline_NeverReferenceFeatureGating` scan flagged `SceneLayerIds.cs` and
   `AnnotationTrack.cs` on its first run. The collision is intentional and documented in
   `AnnotationTrack`'s own comment; the scan now bans the gating TYPES everywhere and the four ids
   that are *not* also layer ids, and a separate case pins the collision so the exemption has a reason
   under test rather than a comment.

4. **The keybind audit's class already existed under another name.** A1 shipped
   `Playback2DKeybindConflictTests` (which parses `MainView.axaml`, as the plan requires); the plan
   names it `Playback2DKeymapConflictTests`. B5's four extra pins — `NoDuplicateGesture_WithinTheKeymap`,
   `EraseIsX_NotE`, `Space_IsPlayPause_UnlessADrawingToolIsActive`, `ArrowKeys_AreBoundToTheTransport`
   — joined the existing class rather than arriving as a near-duplicate of it. Text-input suppression
   is asserted where the rule lives (`Playback2DKeyRoutingTests.TextBoxFocused_KeysAreNotIntercepted`),
   because correction 5 makes it one global handler behaviour and not a per-binding flag.

### Two test seams the plan asks for and the code did not have

5. **`ShellModuleFeatureGate` gained an internal `Func<bool> isBrowser` ctor**, and
   `AnnotationSessionController` gained the same. `OperatingSystem.IsBrowser()` is a JIT-folded
   intrinsic, so a browser branch cannot be faked from outside — and both branches are exactly the
   kind that ships broken because nobody can run them. The public constructors are unchanged.

6. **`SettingsWasmRoundTripTests` covers `AnnotationRecentColors` as its own case**, not through the
   reflection loop: correction 4a predicted arrays would need special-casing. `ExportOutputDirectory`
   is `string` with an `""` default rather than the plan's `string?`/null (B4's shape), so the plan's
   `NullStringProperty_OmitsKey_AndBindsDefault` became
   `EmptyStringProperty_RoundTripsAsEmpty_NotAsTheDefault` — the property that actually needs proving
   for the shape that shipped.

### The WASM verification pass found three defects, all in the app, none in Playback2D

7. **The published browser head did not boot.** `MainViewModel` initialised
   `Process.GetCurrentProcess()` in a **field initializer** for the window-title CPU/RAM readout;
   that throws `PlatformNotSupportedException` on WASM before the constructor body runs, so the whole
   app came up black with one console line. Now null on browser, with the perf ticker not started.
   This is the other half of B0 D11 finding 3 — the `JsonSerializerIsReflectionDisabled` it recorded
   was real too, and is item 8.

8. **`dotnet publish` of the head failed outright**, on ~30 `IL2026` sites plus `IL2104` for
   `CS2DemoKit.Parser`, `CS2DemoKit.Analysis` and `FFMpegCore`. Not incidental call sites: reflection
   `System.Text.Json` in eleven stores, `ConfigurationBinder.Get<AppSettings>()` (which *is* the
   settings layer), and Avalonia's reflection `ViewLocator`. `PublishTrimmed=false`, stated in the
   csproj with its revisit trigger; `WasmBuildNative=true` beside it, no longer relying on an SDK
   default. Cost: 16.3 MB brotli.

9. **The annotation panel lied on the browser.** With a demo attached it read
   `saving to /sample-de_nuke.dem.dvann.json` — true, in that the WASM in-memory VFS accepted the
   write; false, in that the next reload discards it. Design §8 asks for the opposite in as many
   words. Now: *"session only — this browser tab forgets annotations when it reloads."*

**The pass itself was run on the published head in a real browser**, not simulated: `sample-de_nuke.dem`
(19 237 frames) parsed in-browser, the 2D tab rendered markers / kill feed / round HUD / floor labels,
the timeline scrubbed, the level strip appeared on the two-floor map, Settings listed all five
sub-features and toggling `playback2d.timeline` removed it live (hidden-count 9 → 10, viewport
reclaimed the row), and **Video export was absent from the UI entirely**. Full record in
`docs/playback2d-v2/wasm-matrix.md`, including the two remaining degradations (no baked radar art on
this head; nothing survives a reload) and the one cosmetic (the export row's Settings toggle shows the
stored preference, because the platform AND is folded one layer further out — D4 keeps it in one place
on purpose).

### Carry-forwards closed on other phases' behalf

10. **B4 deviation 20 — `CameraScript.MirrorLiveView` captured an empty script.** `Scene2DHost` owns
    its `PaneSet` privately and exposed no snapshot, so "mirror the live view" exported every pane on
    the fit its own level was born with. `Scene2DHost.CaptureCameraScript()` now freezes each pane into
    a `PaneCameraSnapshot` keyed by `MapLevelId`, and the View hands the delegate to the tab on bind
    (null under the legacy hatch, which has no pane cameras). Four tests, including that the capture
    does not move when the live camera does afterwards — which is the whole of D12.

11. **B3's T8 was one wire short.** B2 landed the document-side remap and the tab's entry point, but
    `Scene2DHost.OnLevelSetChanged` never built the zMin map, so the chain existed and only a test ever
    ran it — meanwhile the histogram moves the floor boundary all demo long, and a stroke whose anchor
    stops matching any pane does not move, it vanishes. `RebaseAnnotationAnchors` closes it, keyed on
    the **quantized** ZMin (a raw-Z key matches nothing, since `DrawTool` stamps
    `MapSpace.QuantizeZ(pane.Level.ZMin)`). B3's checklist item is ticked with the evidence; T9's
    annotation half is **not** built and is now recorded as an open FEATURE (design §0 O5) rather than
    as blocked residue — `DocDelta.Replace` exists, so nothing blocks it but scheduling.

12. **The golden harness rewrote its own fixture with CRLF on every Windows run.**
    `JsonWriterOptions.NewLine` defaults to `Environment.NewLine`, against a corpus `.gitattributes`
    pins to LF. Staging normalised it back, so nothing ever reached a commit and nothing ever stopped
    happening. Fixed in `SceneFixtureSerializer`, with a test that asserts the bytes.

13. **`scripts/test-app-suite.sh` could not run under bash.** Its partition indexed `CLASSES` from 1
    (a zsh convention): under bash it skipped the first class and then aborted the last batch on
    `unbound variable`, before the partition audit that exists to catch exactly that could run. Now
    iterates the array with a 0-based counter — identical in both shells — and the shebang follows the
    last zsh-ism out. Verified: three batches, audit **895 ran >= 869 listed**.

### Not done

14. **`Playback2DWasmBudgetTests` is `[Category("Budget")]`**, so CI's `playback2d-tests` lane
    (`Category!=Budget`) excludes it and the `playback2d-budget` lane runs it. That matches every other
    allocation gate in the repo; the plan did not say either way.

15. **The two pre-existing inline `!IsBrowser()` call sites** (`MainViewModel.IsLiveSyncEnabled`,
    `IsProcessingQueueEnabled`) were not consolidated into `ShellModuleFeatureGate`. D4 explicitly
    scopes that out as a follow-up, and B5 ships no refactor it does not need.

16. **`AppSettings.Playback2D.RenderBackend`** — see item 2.

17. **`docs/playback2d-v2/old-control-removal.md` lists 10 file actions, not the plan's 8.** The plan's
    table predates B1/B3/B4; grepping the tree found `Playback2DRenderer` (the toggle's own resolver)
    and `MapAssetLoader`'s legacy `Bitmap` half, and corrected several line numbers and the reference
    counts. It also splits `Playback2DGoldenCaptureTests` out into its own step: those goldens are
    captured **from the pre-v2 control on purpose**, which is what makes them a parity baseline rather
    than a snapshot of v2's own output, so retargeting them is a deliberate re-capture and not part of
    a deletion commit.

18. **The `wasm-build` CI job has not run on a GitHub runner**, only locally — it is added in the same
    commit as the fixes it would have caught, so its first real execution is the next push.
