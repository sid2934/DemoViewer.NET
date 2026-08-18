# Rulesets v2 — Phase 3: Authoring Workbench + Trace (implementation plan)

Implemented 2026-07-15: milestones M0–M7 shipped, plus the seven owner-review refinements
(data-browser tree, Evaluate scoping, real scoreboard/tables, syntax highlighting, ruleset graph,
read-only shipped rulesets + DeveloperMode) and follow-ups (demo-less focused authoring graph,
single-flight async Evaluate, DSL-aware highlighting + type-aware completion). Owner decisions
are recorded below (§6). Remaining items are tracked in `FEATURE-REQUESTS-AND-GAPS.md`
(context-aware completion, the graph view, predicate CSE), plus the live paper-test
to discharge the provisional freeze. Originally a design/scoping pass, 2026-07-14.

**Scope source:** `docs/rules-v2/rule-authoring-ux-review.md` §3.2, the workbench + trace wave.
**Predecessor:** the v2 core is implementation-complete through the tutorial; the shape is
provisionally frozen (`docs/rules-v2/rules-v2-spec.md`). This phase builds the in-app authoring
surface on top of that frozen core.

**Owner decisions (2026-07-14) that shape this plan:**
- **A new dedicated "Authoring" tab** (not an evolution of the Analysis tab).
- **Adopt v2 in the *main* analysis path now and drop v1 entirely on this feature branch.**
  This folds the previously owner-gated "ship cutover" into this phase (the cutover track,
  §1b), and makes the app v2-native before the Workbench is built. "Drop v1" = drop the v1
  **authoring/config surface** (the shipped `chains:` YAML files + the main-path v1 load) —
  **not** the shared evaluation engine, which the v2 planner is built on (19 reuse sites in
  `RuleChainBuilder.RulesetsV2.cs`: `BuildPerPlayerRuleNode` / `CompileEventCondition` /
  `KeyedCounterEdge` / `BuildSingletonRule` …). The v1 config-*build* path may go dead for
  production; physically deleting it is a deferred cleanup, not part of the cutover.
- **Initial trace scope is delegated** to whoever implements the trace milestone (M6).
- **The AvaloniaEdit pin (Avalonia 11.3.12-compatible) is approved.**

This is a plan, not a spec — it names the milestones, the real file seams they touch, the
protected-file impact (spoiler: none), the platform story, the test strategy, and the open
decisions. Each milestone is independently shippable.

---

## 0. Scope (what Phase 3 is)

From the plan, all in `src/App/DemoViewer.NET`:

- **AvaloniaEdit editor** — a new pure-managed dependency into `Directory.Packages.props`.
- **In-process completion** from the semantic core (no LSP hop) — views/facets/contexts/stat refs.
- **FileSystemWatcher → auto re-run** on edit (desktop-only).
- **Data browser** — live demo values with drag-to-insert into the editor.
- **Catalog-version stamping** into rulesets on Workbench save.
- **`2MUCH` display rendering** — the scoreboard/tables output the ruleset declares.
- **Trace panel** — clause-level verdict capture on a **cloned, instrumented subgraph** of the
  selected ruleset (instrumented nodes never enter the deduped shared graph).
- **Platform scoping** — file watcher, `code --goto`, and the Workbench itself are desktop-only;
  WASM/Browser gets a read-only view + diagnostics.
- **Reference-only:** the unmerged `feature/graph-breakpoints` branch (stale base — read, do not
  merge).

---

## 1. The load-bearing finding: v2 has no in-app consumer yet

The recon established that **nothing in the app evaluates Rulesets v2 today**:

- `AnalysisViewModel.BuildFromConfig` (`src/App/DemoViewer.NET/ViewModels/AnalysisViewModel.cs:1088`)
  loads rules via `YamlConfigLoader.LoadWithOverlay` and calls `DemoAnalysis.Build(demo, rules.Config)`
  — passing only the **v1** `Config` and **dropping `rules.Rulesets`** (the v2 side, which
  `RuleConfigLoadResult.Rulesets` does carry, `RuleConfigLoadResult.cs:70`).
- `RuleChainBuilder.Build(config, rulesets = null, …)` (`Building/RuleChainBuilder.cs:123`) then
  leaves the v2 `rulesets` argument null.
- v2 composition + checking (`RulesetComposition.Compose` / `ComposeDraft`, `CheckedRulesetDraft`)
  is invoked **only** from `tools/AnalysisBench/RulesCheckCommand.cs` and the test project — zero
  callers under `src/App`.

**Consequence for the plan.** The Workbench's foundation milestone is *wiring the v2
composition/checker/evaluator seam into the app for the first time* — the editor sits on top of
that. Fortunately the seam is clean: `RulesetComposition` and `CheckedRulesetDraft` live in
`src/Analysis/DemoViewer.NET.Analysis` (**no Avalonia dependency**, already referenced by the App),
so they import in-process directly. The reference implementations to mirror are:
`RulesCheckCommand.cs:200` (demo-less `ComposeDraft` → authoring diagnostics) and
`RulesCheckCommand.cs:462-476` (compose → build → evaluate for the demo-backed path).

Per the cutover decision, this phase does the opposite of "confine to a preview": it makes the
**main** analysis path v2-native and removes v1 (the cutover track below), *then* builds the
Workbench on the now-v2 app.

---

## 1b. The cutover track — production v2 adoption + v1 drop (do first)

The corpus port already produced **v2==v1 EXACT** equivalents for **all four** shipped v1 files,
proven by demo goldens:

| Shipped v1 (`rules/`) | v2 replacement (`rules/examples/`) | Golden |
|---|---|---|
| `kast.yaml` | `kast.rules.yaml` | `KastPilotTests` (98/105/105/33/1633) |
| `player-stats.yaml` | `player_stats.rules.yaml` (`use: [kast]` cross-ruleset read) | `PlayerStatsPilotTests` |
| `weapon-stats.yaml` | `weapon_stats.rules.yaml` | `WeaponStatsPilotTests` |
| `achievement-post-plant-double.yaml` | `post_plant_double.rules.yaml` | `PostPlantDoublePilotTests` |

The kast "duplication" flagged during the port is already resolved: `player_stats.rules.yaml` reads
`kast.kast_pct` via `use:`/`exports:` cross-ruleset composition, and the goldens evaluate through
that path — so no fold/duplication to reconcile.

**Cutover steps (goldens are the safety net at each step):**

1. **Thread compose→build into the app.** Add a rulesets-carrying build entry
   (`DemoAnalysis.Build(demo, config, rulesets, options)` overload, or call `RuleChainBuilder.Build`
   directly) that runs `RulesetComposition.Compose(load.Rulesets, adapter, tickRate, profile)` and
   passes the composed `CheckedRuleset[]` — mirroring `RulesCheckCommand.cs:462-476`. Today
   `DemoAnalysis.Build` takes only `RuleChainConfig` and `Compose` is called only by the CLI.
2. **Switch the main path.** `AnalysisViewModel.BuildFromConfig` (`:1088-1096`) stops passing
   only `rules.Config` and passes the composed v2 `rules.Rulesets` through step 1's entry. Keep the
   `OperatingSystem.IsBrowser()` overlay guard.
3. **Promote the rulesets.** Move the four `rules/examples/*.rules.yaml` (+ their shared deps)
   into `rules/` as the shipped production content; keep `catalog.json` + `cs2demokit-rules.schema.json`.
4. **Remove the v1 surface.** Delete the four shipped v1 `chains:` files and the v1-only
   `analysis-rules.schema.json`. Port or retire `kast.test.yaml` (v1 `.test.yaml`; the v2 `.test`
   runner was deferred at the CLI stage — retire for now, tracked). §8 cross-version collision
   becomes moot (no v1 files left to collide with the v2 ids).
5. **Verify the Stats/Analysis tab renders v2 output.** The v2 `show:` projection
   (`ConfiguredOutputProjector`, scoreboard + `OutputScope.PerMatch`) flows through `EvaluationResult`;
   `StatsTabViewModel.UpdateFromRun` consumes it. The goldens prove the *projection*; this step
   confirms the *tab VM* surfaces it (headless-Skia). Adapt the VM if a v1-shaped assumption leaks.
6. **Re-baseline.** Run the accuracy bench (`AnalysisBench --suite`) and the App/Analysis
   suites; re-baseline goldens per the goldens policy (v2==v1 means the accuracy table should be
   identical — any diff is a finding to investigate, not rubber-stamp).

**Risk register for the cutover:** (a) the Stats-tab VM may hold a v1-shaped assumption (step 5 —
headless capture catches it); (b) user-rules overlay now overlays v2 rulesets — confirm
`LoadWithOverlay` merges the v2 tier as expected; (c) the accuracy bench baseline is keyed on the
shipped rules and must be re-run (should be identical). All are goldens- or capture-gated.
Protected parser files: untouched. `RuleChainBuilder.cs` (not protected) may gain the additive v2
entry from step 1 but its v1 build path stays byte-identical where retained.

The cutover is do-first: once it lands, the app already composes + evaluates v2, so the Workbench's
`M1` (below) is largely satisfied and the editor sits on a v2-native app.

---

## 2. How it grafts onto the app (confirmed seams)

- **New tab = new module.** `IWorkspaceModule` → one `WorkspaceTabDescriptor` (use
  `ViewModelFactory` + `ViewFactory` for lazy/retained lifecycle) → register in
  `App.axaml.cs BuildRegistry()`. Copy-from template end-to-end: `Modules/Playback2D/Playback2DModule.cs`
  (+ its VM + `Views/Playback2D/Playback2DView.axaml`). Minimal footprint: 1 module + 1 VM + 1 View +
  1 `Register()` line. The Workbench is a `Placement.Main` tab (e.g. `Order` after Analysis).
- **Read-only runtime context.** `IModuleContext` (clock, `Advanced` per-frame push, `DemoReset`,
  `Entities`, `Players`, `GetEventTimeline`) is the module's whole world — no mutators. The data
  browser (M4) reads demo values through it.
- **Results flow to reuse.** `AnalysisViewModel` fires
  `event Action<AnalysisRun, ParsedDemo> EvaluationCompleted` carrying `EvaluationResult` snapshots;
  `StatsTabViewModel` (`ViewModels/Stats/StatsTabViewModel.cs:79`) is the template consumer
  (projects `MetricTable`s). The Workbench results panel (M5) copies this shape but drives it from
  its *own* Workbench-scoped evaluation, not the main run.
- **Trace substrate already exists.** Per-message node-state snapshots, `EvaluationResult`
  `AppliedMessagesByEdge`, `GraphBreakpoints`, and per-message step nav
  (`AnalysisViewModel.NextMessage/PreviousMessage`) are on main; the evaluator records applied fires
  via the `RuleChainTimeline` / `CaptureAfterMessage` mechanism (`StateGraphEvaluator.cs` ~ln
  311/429). M6 adds *clause-level* verdicts on top by instrumenting a cloned subgraph.
- **External-editor + folder reuse.** `Controls/OpenExternal.OpenLocalFile(path,line,col)`
  (`code --goto`) and the `📁 Rules` button's `OpenUserRulesFolder` already exist — the Workbench's
  "open in VS Code" and "reveal folder" reuse them verbatim.

---

## 3. Milestones (each independently shippable)

Ordering is dependency-driven: the checker seam and a minimal editor come before completion, data
browser, evaluation, and trace.

### M0 — Dependency + empty desktop-gated tab (S)
Add `AvaloniaEdit` `PackageVersion` (Avalonia **11.3.12**-compatible release) to
`Directory.Packages.props`; scaffold `RuleWorkbenchModule` + `RuleWorkbenchTabViewModel` +
`RuleWorkbenchView` (copying Playback2D), register it, desktop-gate its registration/visibility via
`OperatingSystem.IsBrowser()`. Ships an empty "Authoring" tab that loads. **Gate:** headless-Skia
smoke test the tab renders (desktop) and the WASM build still compiles.

### M1 — In-process v2 checker seam + diagnostics list (M) ← largely satisfied by the cutover
> With the cutover landed the app already composes + evaluates v2, so M1 shrinks to surfacing the
> *demo-less* authoring diagnostics (`ComposeDraft`) in the Workbench UI; the compose/build/evaluate
> plumbing is done.
Wire `RulesetComposition.ComposeDraft` in-app against the user rules dir
(`RuleSetLocator.EnsureUserRulesDirectory`); surface `CheckedRulesetDraft` diagnostics as a list
with `file(line,col)` rows (reuse the `RuleDiagnostic` row shape + `OpenExternal` click-to-open).
No editor yet — a "check this folder" button. This is the first time the app touches v2; it's
independently useful as an in-app `rules check`. **Gate:** a unit test that a known-bad ruleset dir
yields the expected diagnostic rows (no UI needed); headless-Skia the list renders.

### M2 — AvaloniaEdit editor pane + inline diagnostics + auto-rerun (M)
The editor: open/edit a `.rules.yaml`, YAML syntax highlighting, inline error markers driven by the
M1 checker's `file(line,col)`. Add a **FileSystemWatcher** (desktop-only, `IsBrowser()`-gated) that
re-runs the check on external edits; in-editor edits debounce-trigger the same. Save stamps
`catalog_version` (see M7 hook). **Gate:** headless-Skia capture of an error marker at the right
line; a watcher test (desktop) that an on-disk change fires a re-check.

### M3 — In-process completion from the semantic core (M)
Catalog-driven completion in the editor: view names, facet keys/values, context roots
(`player.*`/`round.*`/`match.*`/entity reads), sibling stat refs, and function names — sourced from
the generated catalog (`rules/catalog.json`) + the checker's scope environments, **in-process, no
LSP**. **Gate:** unit-test the completion provider returns the expected candidate set for a cursor
context; headless-Skia the completion popup.

### M4 — Data browser with live values + drag-to-insert (M)
A panel that, for the loaded demo at the current frame (`IModuleContext` entities/players/events),
shows readable values (a player's health/equipment, a kill event's fields) and lets the author
**drag a value's path into the editor** (inserts e.g. `player.entity.pawn.health`). Bridges the
`Advanced` per-frame push to a live-updating tree. **Gate:** headless-Skia the browser renders demo
values; unit-test the drag payload → inserted text mapping.

### M5 — Evaluate-on-demo + results panel (`2MUCH` rendering) (M)
Run the edited ruleset against the loaded demo in-process (mirror `RulesCheckCommand.cs:462-476`:
compose → `RuleChainBuilder.Build(config, rulesets)` → `DemoAnalysis.Evaluate`), render the
ruleset's declared `show:` (scoreboard/tables) — the `2MUCH` display. Reuse `EvaluationResult` +
the `StatsTabViewModel` projection shape, scoped to the Workbench. **Gate:** an evaluation
equivalence test (Workbench-run result == `rules check --demo` result for a pinned ruleset+demo);
headless-Skia the results table.

### M6 — Trace panel: clause-level verdicts on a cloned instrumented subgraph (L) ← hardest
For the selected stat/highlight, build a **cloned, instrumented** subgraph (instrumented nodes must
**never** enter the deduped shared graph — identity-hash discipline) and capture per-clause verdicts
per fire, layered on the existing `RuleChainTimeline`/`AppliedMessagesByEdge` recording + per-message
step nav. Surface "why did/didn't this fire at tick T" down to the failing clause. **Reference:**
`feature/graph-breakpoints` (read-only, stale base — do not merge). **Gate:** a determinism test
(instrumented subgraph verdicts reconcile with the uninstrumented run's applied fires); headless-Skia
the trace view. *This milestone is the M–L weight of the phase and can ship after M0–M5 deliver a
usable editor.*

### M7 — Catalog-version stamping + polish (S)
On save, stamp/refresh `catalog_version` (+ `min_app_version`) — the schema fields exist (frozen
with the v2 core). Wire the `📁 Rules` / open-in-VS-Code affordances. Final platform-scoping audit.

---

## 4. Platform scoping (desktop vs WASM)

The App is a **single shared assembly** compiled per host (Desktop `net10.0` / Browser
`net10.0-browser`) — so **never** a compile-time TFM split; gate at runtime with
`OperatingSystem.IsBrowser()` (the pattern already at `AnalysisViewModel.cs:1090`) or via the
`IWindowService` seam.

- **Desktop-only:** the editor + save, FileSystemWatcher, `code --goto`, evaluation-on-demo.
- **WASM/Browser:** read-only ruleset view + the diagnostics list (M1) — no watcher, no `Process`,
  no file writes. `OpenExternal`'s `Process.Start` calls are already try/catch-guarded and no-op on
  WASM, so they compile; the watcher and file-write paths must be `IsBrowser()`-gated to not run.

---

## 5. Protected-file impact & test strategy

- **Protected parser files: none touched.** Everything lives in `src/App/DemoViewer.NET`, a new
  package reference, and reuse of already-referenced `DemoViewer.NET.Analysis` library types. No
  `DemoParser.cs` / `DemoFrame.cs` / `LEB128Utils.cs` / `BitBuffer.cs`. No protected-file approval
  needed for this phase.
- **Tests:** every milestone verified by headless TUnit + Skia frame capture in
  `src/App/DemoViewer.NET.App.Tests` (template: `Playback2DKillFeedRenderTests` +
  `HeadlessSession.RunOnUi` + `CaptureRenderedFrame()`), plus pure unit tests for the non-UI logic
  (checker seam, completion provider, drag-payload mapping, evaluation equivalence). `[NotInParallel]`
  on UI classes (shared headless UI thread). The M5 evaluation-equivalence and M6 determinism gates
  are the correctness anchors.

---

## 6. Decisions (owner-resolved 2026-07-14)

- **New dedicated "Authoring" tab** (own module; Analysis tab untouched).
- **Adopt v2 in the main path now, drop v1 on the branch.** Implemented as the cutover track
  (§1b), sequenced first. Supersedes the earlier "confine to Workbench preview" assumption.
- **Initial trace scope delegated to the M6 implementer.** Guidance (non-binding):
  "applied-fire + per-clause verdict for the selected stat" is the natural first slice, deferring
  full multi-stat time-travel — but the implementer decides.
- **AvaloniaEdit pin approved** (the Avalonia 11.3.12-compatible `AvaloniaEdit`
  11.3.x release; exact patch confirmed at M0).

---

## 7. Effort & sequencing

| Milestone | Effort | Delivers |
|---|---|---|
| M0 dependency + empty tab | S | The tab exists; deps in place |
| M1 checker seam + diagnostics | M | First in-app v2 consumer; in-app `rules check` |
| M2 editor + inline diags + watcher | M | A usable editor with live errors |
| M3 completion | M | Catalog-driven authoring assist |
| M4 data browser | M | Live values + drag-to-insert |
| M5 evaluate + results | M | See your ruleset's numbers on a demo |
| M6 trace | L | Clause-level "why did it fire" |
| M7 stamping + polish | S | Provenance + affordances |

**Recommended first PR:** M0 + M1 together — they establish the tab and the v2 seam (the genuinely
new architecture) with minimal UI, and are independently valuable as an in-app checker. Editor
(M2) follows once the seam is proven.

Efforts follow the plan's convention (S = days, M = 1–3 weeks, L = 1–2 months). The phase as a
whole is M–L, consistent with §3.2.
