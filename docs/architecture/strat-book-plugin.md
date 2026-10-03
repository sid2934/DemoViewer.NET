# Strat Book as a plugin: investigation and design

Status: investigated 2026-10-02 on `spike/strat-book-plugin` (off `feature/strat-book` at `8275d5a6`);
owner decisions recorded 2026-10-02 (section 10). Line numbers are against that commit and will drift.

Owner request: make the Strat Book features a plugin that a user can fully disable, extend the plugin
framework where the Strat Book needs seams it does not have, and grow the existing feature hiding into a
modular application.

**Naming.** `docs/plugins/plugin-system-design.md` already reserves "plugin" for the CSVG game plugin that
live sync installs into CS2, and uses "add-on" for third-party code. This doc says **feature pack** for a
first-party, compiled-in unit that can be switched off as a whole, and "the Strat Book pack" for this one.
Users see it as an **extension** ("Strat Book extension"), decided 2026-10-02 (section 10).

---

## 1. Summary

- **Hiding is not off.** Every Strat Book surface can be hidden today, live, through `tab.*` feature ids.
  None of those gates stops any background work. Round Facts runs on every library index with no switch at
  all, Round Index runs by default, two indexes load at startup and stay resident, and Team Identity
  rebuilds at startup and constructs the strat, dossier and veto stores as it does. The first deliverable
  is making "off" mean off. It is also the cheapest.
- **The module framework already carries most of the UI.** All eight Strat Book modules are ordinary
  `IWorkspaceModule`s, and the branch already added the hosted-section idea (`TabPlacement.StratBook` and
  `.Library`, `TabSectionHost`). What it lacks is a pack level (an umbrella id, contribution lists the shell
  enumerates instead of hardcoding) and seams inside 2D Playback, where the Strat Book, Round Tagger,
  Suggested Tags and Situations are wired directly into `Playback2DTabViewModel`.
- **Recommendation: option (a), in-process packs behind an expanded contribution model, laid out so the
  later move to a separate assembly (b) is mechanical.** Option (c), runtime-loaded plugins, is ruled out
  for the Strat Book by the add-on design's own rule: 33 Strat Book files reference CS2DemoKit, and add-ons
  must never do so.
- **Structure (decision 5, 2026-10-02):** every extension lives in its own directory now and becomes its
  own csproj as soon as the core-to-extension edges are cut, so an extension can ship on its own cadence.
  Section 13 gives the layout, phases 5 and 6 the project split and the independent release.
- **Effort:** 25 agent-sized items for (a) across five phases (0 to 4), then 8 for the project split
  (Phase 5, now required) and about 6 for the independent release (Phase 6). Phase 0 (contracts and the pack
  skeleton) unblocks parallel work; Phase 1 (real "off", in-session release, first-run prompt) ships value
  on its own. Items in section 6, the parallel schedule in section 11.

---

## 2. What exists today

### 2.1 Module and tab framework

- `IWorkspaceModule` (`Modules.Abstractions.Ui/IWorkspaceModule.cs`): `Id`, `DisplayName`,
  `ContractVersion` (written, never read), `CreateTabs(IModuleHost)`.
- `WorkspaceTabDescriptor`: `TabId`, `Header`, `Badge` (added on this branch), `Order`, `Placement`, a lazy
  retained `ViewModelFactory`, a per-activation `ViewFactory`, and `PendingRestoreState` for lazy session
  restore. The view is dropped on deactivation; the VM is kept.
- `TabPlacement` gained `StratBook` and `Library` on this branch: a descriptor with those placements is a
  section hosted inside another tab, not a strip tab. `TabSectionHost` (`ViewModels/Shell/TabSectionHost.cs`)
  reconciles sections by identity on every gate change, so a section VM survives a flip.
- `ModuleRegistry` is a plain list. `App.BuildRegistry` (`App.axaml.cs:1511-1576`) news up every module by
  hand: Playback2D, RuleWorkbench, Highlights, then the eight Strat Book modules. `BuiltInTabsModule` is
  registered by the shell.
- `IModuleContext` is the read-only runtime surface. The branch added `DemoSha256`, `FirstTick` and
  `LastTick` as default members. The concrete `ModuleContext` also carries first-party hosts that are
  deliberately off the interface: `ExportHost`, `StratExportHost`, `StratCaptureHost`. Consumers reach them
  by downcasting (`_context is ModuleContext { StratCaptureHost: ... }`, five sites).
- `ModuleHost` capabilities are cosmetic (no enforcement; see the add-on doc §1.1). Unchanged here.

### 2.2 Feature gating

- `FeatureCatalog` is a static list of `FeatureDescriptor(Id, Scope, Label, Description, ParentId, GroupId,
  Required, Defaults)` with scopes `Tab`, `SubFeature`, `Chrome` and per-category defaults (consumer, power,
  developer). `FeatureGate` resolves Required, then override, then category default, then group leader,
  then parent cascade. It reads `IOptionsMonitor<AppSettings>` live and raises `Changed` on the UI thread.
  An id not in the catalog fails open.
- `MainViewModel._tabFeatureIds` (`MainViewModel.cs:94-115`) is a hardcoded map from tab id to feature id.
  A tab not in the map is always shown.
- `ShellModuleFeatureGate` projects the gate to modules as `IModuleFeatureGate` and ANDs in desktop-only ids
  (`stratbook.export` is one).
- Strat Book ids: tabs `tab.stratbook`, `tab.situations`, `tab.tagger`, `tab.utilitybook`, `tab.review`,
  `tab.dossier`, `tab.suggested`, `tab.teams`; sub-features `stratbook.export`, `stratbook.routing`,
  `playback2d.tagger`, `playback2d.suggestedtags`. All default on for every category. **There is no umbrella
  id.** The hub tab `stratbook.hub` is shown when any section resolves on.
- Gating is live for UI. It stops work in exactly one place: `SuggestedTagsService` takes
  `() => features.IsEnabled("playback2d.suggestedtags")` and its `Wants()` returns false when off. That is
  the precedent for section 7.

### 2.3 Plugins and extension

There is no loader: no `AssemblyLoadContext`, no assembly scanning, no MEF. `docs/plugins/plugin-system-design.md`
(rev. 2, 2026-08-20) is the design of record for third-party add-ons and recommends in-process,
full-trust, desktop-only, loader-last. Its findings apply here unchanged and are not repeated: collectible
ALC unload is best effort and likely defeated by `AvaloniaProperty.Register`; add-ons live under the config
root because Velopack replaces `current/`; add-ons must not reference CS2DemoKit; WASM cannot load
assemblies. `CS2DemoKit.Analysis.Plugins` is a namespace, not a mechanism.

### 2.4 Distribution

- Desktop publish is untrimmed, non-AOT, non-single-file, by recorded decision. Velopack per RID.
- The Browser head (`DemoViewer.NET.Browser`) references the whole app assembly and runs the same
  `App.BuildRegistry`; it is untrimmed too (`PublishTrimmed=false`, with the reasons in its csproj) and is
  now built by the `wasm-build` CI job. Every Strat Book module is registered on both hosts and degrades to
  session-only state in the browser.
- Consequence: nothing in the build blocks any of the three options on desktop. Trimming would only matter
  if it were turned on later, and then a pack in a separate assembly would need to be rooted explicitly.

---

## 3. Touchpoint inventory

**Rings.** Each touchpoint is placed in one of three rings, because the answer to "can it be a plugin
contribution" depends on which ring it is in.

- **P (pack-owned):** exists only for the Strat Book; disappears with it.
- **S (shared):** added by the Strat Book work but now consumed by core features. Disabling the pack must
  not remove it, or must replace it with a degraded path. Where the boundary sits is question 1.
- **G (generic):** a reusable capability the work added to core (lanes, shape tools, `MapSceneHost`,
  `QueueWork`, zones). Stays in core regardless.

"Expressible" means the current framework can carry it as a contribution with no new seam.

### 3.1 Tabs, sections, shell

| Touchpoint | Ring | Attaches today | Expressible? | Seam needed |
|---|---|---|---|---|
| Strat Book hub tab `stratbook.hub` | P | Shell synthesizes the descriptor when any `Placement=StratBook` descriptor exists; `new StratBookHubView()` and `StratBookHubViewModel` referenced from `MainViewModel` (:532, :2409-2435) | Partly | Hosts as contributions: a pack declares a host tab, sections target it by host id instead of an enum member |
| Sections: Strats, Situations, Tags, Utility, Review, Dossier, Suggested | P | One `IWorkspaceModule` each, `Placement=StratBook`, hardcoded `Register` in `BuildRegistry` | Yes, mostly | Pack registration instead of the hardcoded list; feature ids declared by the module instead of `_tabFeatureIds` |
| Teams under Library | S | `TeamsModule`, `Placement=Library`; `LibraryTabViewModel.Sections` | Yes (section) | Same as above. The Library's team filter and provenance chip are not sections (next row) |
| Library team filter, provenance chip | S | `MainViewModel` and `LibraryTabViewModel` ctor take `TeamIdentityService`, `IDemoProvenanceSource`; XAML in `LibraryTabView.axaml` | No | Library filter and card-badge contributions |
| `StratBookLayout` (collapsed rail and list) | P | DI singleton passed to `BuildShell`, shared by hub and Strats VM | No | Pack session state (3.6) |
| Strat export status chip | P | `MainViewModel.AttachStratExportStatus` (:2275-2339), its own chip slot | No | Status-chip contribution |
| Cross-pack navigation (`TrySelectTab(StratBookModule.BrowserTabId)` then `OpenStrat(id)`) | P | Lambda in `App.WireStratCapture` (:492-509) | No | Navigation by URI-like route (`stratbook/strat/{id}`) owned by the pack |
| Shutdown flushes | P/S | `App` :312-316: `StratBookModule.Shutdown`, `TagStore.SaveIndex`, `ReviewQueue.Flush`, `GrenadeIndex.FlushLineups` | No | Pack lifecycle `OnShutdown` |
| Feature ids | P | Static `FeatureCatalog`, hardcoded `_tabFeatureIds` | No | Pack-declared descriptors, a pack scope, umbrella id |

### 3.2 Library, indexing, processing queue

| Touchpoint | Ring | Attaches today | Runtime cost when "hidden" | Expressible? | Seam needed |
|---|---|---|---|---|---|
| Evaluator list | P/S | Hardcoded `[library, highlights, roundFacts, roundIndex, suggestedTags, grenades]` and a hardcoded pending-path union (`App.axaml.cs:1313-1340`); order pinned by `AppCompositionRootTests` | n/a | No | Ordered evaluator registry with `After` constraints; each evaluator gated |
| Round Facts | **S** | `RoundFactsEvaluator`; the `round_facts` ruleset is merged into `MergedRulesBuild`, so it runs inside every Library and Highlights forward pass; `AnalysisViewModel` excludes it; `RoundTrack` uses it for winner tints | Runs on every index and every open. No switch | No | Decide ring (Q1). If pack: ruleset contribution to `MergedRulesBuild`, `RoundTrack` tint via an optional source |
| Round Index and `SituationIndex` | P | `RoundIndexEvaluator.Wants` gated on `Situations.BackgroundIndex` (default **true**); `SituationIndex.Load` at startup (:1376), resident, subscribes to `DemoCacheStore.Changed` | Background parse work per demo; resident index | No | Gated evaluator, pack startup load |
| Grenade Index (Utility Book) | P | `GrenadeIndexEvaluator` (`Grenades.BackgroundIndex`, default false, but the open demo is always walked); `GrenadeIndex.Load` at startup (:1379), resident; `LineupClipService` force-resolved (:1382); `GrenadeStoreMigration` queued at 30 s (:1402); Match Overview "Index grenades" wired at :206-211 | Walk on every open, resident index, migration | No | Gated evaluator, pack startup, pack migrations, Match Overview action contribution |
| Suggested Tags | P | `SuggestedTagsService` (gated on `playback2d.suggestedtags` and `SuggestedTagsBackground`, default false); `SuggestedInboxModule` badge recomputes on every `cache.Changed` | Off when its gate is off; badge sum on every cache change while registered | Partly (the precedent) | Same pattern for the rest |
| Strat Mining | P | `StratMiningService` built lazily with the Strats VM; once mined, re-mines after 30 s of cache quiet | Only after first use | Mostly | Pack-gated, and unsubscribe on disable |
| Team Identity, Provenance | **S** | `TeamIdentityService.StartAsync()` at startup (:1385); its factory resolves `StratStore`, `DossierNotesStore`, `VetoHistoryStore`; Provenance overrides live in `teams.json` | Startup rebuild or diff; three pack stores constructed eagerly | No | Split "referenced teams" reporting into a contribution so Teams does not pull pack stores |
| `TagFactsRefresher` | P | Force-resolved at startup (:1387), saves on every Round Facts update | A `StoreSave` job per update | No | Pack startup |
| Queue job kinds | G/P | `QueueJobKind` enum gained `StratMining`, `StratPreview`, `LineupClips`, `SuggestionsInbox`, `SectionCompute`, `TeamsCommand`, `StoreSave`, `StoreLoad`, `PackExport`; `KindRank`, `IsLight` and `DemoQueueRowViewModel.KindLabel` switch on them | None | No | Job kind descriptors (label, rank, light) registered, enum kept for core kinds |
| Demo cache schema | **S** | `DemoCacheModels.cs` imports `Services.RoundFacts` and `.RoundIndex` and embeds about 13 Round Facts, Round Index, Suggestions and Grenade fields, mirrored on the index entry | ~50 B per index row | No | Opaque per-pack payloads keyed by pack id (3.6). No schema bump needed to drop fields: the store ignores unknown members |
| SHA-256 per demo at tier 2 | S | `DemoLibraryService.GetOrComputeSha` for the tag and annotation stores | A second full read per indexed demo | No | Compute on demand, or keep (annotations need it too) |
| Highlights to Review Queue | S | `HighlightsTabViewModel` takes `ReviewQueue` (:816) | None | No | Optional "send to review" contribution on the Reels tray |

### 3.3 2D Playback

| Touchpoint | Ring | Attaches today | Expressible? | Seam needed |
|---|---|---|---|---|
| Create Strat From Round (round-band right-click) | P | Hardcoded `MenuItem` in `TimelineControl.OnBandPressed` (:97-131); `CanCreateStrat`, `RequestCreateStrat`, `CreateStratRequested` on the timeline VM; tab VM downcasts to `ModuleContext` for `StratCaptureHost` (:570) and builds `CreateStratDialogViewModel` (:1040-1105) | No | Band context-menu contribution plus a side-pane contribution (section 5.4) |
| Create Strat review pane | P | `Playback2DView.axaml:525-540` binds `CreateStratDialog` | No | Side-pane contribution |
| `StratCaptureHost`, `StratExportHost` on `ModuleContext` | P | Concrete properties set by `App`; `ModuleContext.cs` imports `Modules.StratBook` | No | Typed service lookup on the context (`IModuleContext.GetService<T>`) or pack-owned host |
| Review mode, tag lane, proposal lane | P | Tab ctor registers `TagTrack` and `ProposalTrack` via `Timeline.RegisterTrack` (a real list seam), but band clicks dispatch on `TrackId` in the tab, and `LaneMenu` is a single `Func` slot | Lanes yes, behaviour no | Lane contribution that carries its own press and menu handlers |
| Tag Palette, Tag Editor, Suggestion Queue, Review panel | P | Tab VM constructs `TagPaletteViewModel`, `SuggestionQueueViewModel`, `ReviewPanelViewModel`; services by `App.Services` service locator (`TryResolve<T>`); views embedded in `Playback2DView.axaml` right column (:551-760) | No | Right-column panel contribution with order, gate and focus scope |
| Click To Tag Position | P | `Scene2DHost.OnPointerPressed` calls `ISceneFrameHost.TryTagPositionAt` before the tool router | No | Pointer pre-handler contribution, or a tool |
| Keymap actions | P/G | Closed `Playback2DAction` enum and static default table gained Tag*, Suggestion*, ToggleReviewMode, FindRoundsLikeThis, situation result nav, ToolToken, step add/duplicate/delete/nav; scopes `WhenPaletteFocused`, `WhenSuggestionSelected` | No | String-keyed command ids with defaults, registered by the pack (section 5.9) |
| "Rounds like this" button and menu | P | `Playback2DView.axaml:294, :322`; `IFindRoundsLikeThis` | No | Toolbar and menu contribution |
| Lineup picker | P | Lives in the Strat Book (`LineupPickerView`), hosts `UtilityMapHost : MapSceneHost` | Yes (inside the pack) | None, if `MapSceneHost` is reachable |
| Token editor, guides layer | P in G code | Core `ITokenEditor`, `TokenTool`, `SceneGuides`, `GuideLayer`; `Scene2DHost` always registers `TokenTool` and binds guides when the frame host has an editor; router hardcodes a Token fallback | Inert without a strat host | Tool and layer registration on the host (5.5); not urgent, it costs nothing at runtime |
| Keyframes, routes, route palette | P in G code | Core `Keyframes/`, `TokenRouteLine`, `Scene2DFrame.Routes`, `ScenePalette.Route*`; Pipeline `StratFrameSource`, `StratSceneSpec`, `StratHudDataSource` | Inert | None for (a). For (b), move the strat-only types out of Core and Pipeline into the pack |
| Zones, shape and text tools, `MapSceneHost`, `ISceneFrameHost`, `RegisterTrack` | G | Core and app | n/a | Stay core. `ISceneFrameHost` should lose `TokenEditor`, `Guides`, `TryTagPositionAt` into optional interfaces |
| Export dialog reuse (`ExportDialogScene`) | G | Strat Book reuses `Playback2DExportDialogViewModel` | Yes | None |

### 3.4 Situations, Dossier

Both are sections (3.1) with their own services. Situations adds three things outside its section:
`IFindRoundsLikeThis` and `ISituationResultWalk` used by 2D Playback (3.3), `WatchedSituationsService`
resolved eagerly by `BuildRegistry` (for the badge), and the startup `SituationIndex` load (3.2). Dossier
news up its analysis services inside its lazy VM factory and costs nothing until opened, apart from
`DossierNotesStore` and `VetoHistoryStore`, which Team Identity's factory builds at startup.

### 3.5 Settings, theme, UiCapture, CLI

| Touchpoint | Ring | Attaches today | Expressible? | Seam needed |
|---|---|---|---|---|
| Settings sections | P | `SettingsView.axaml` hardcoded XAML: Suggested Tags tuning section (~:314-400), grenade toggles (~:888-920); `SettingsViewModel` ctor takes `SuggestedTagsTuningViewModel`; hardcoded search keywords | No | Settings page contribution |
| `AppSettings` sections | P | `Situations`, `Grenades`, three `Playback2D` keys; `SettingsService` flattening at :504-517 lists keys by hand; `AppSettings` imports `RoundIndexTokenSource` | No | Pack settings section bound by key (`Packs:StratBook:*`) or kept where it is (cheap, harmless) |
| Theme tokens | P | `ModalScrim`, `Pb2dCanvasRoute{T,Ct,GhostT,GhostCt}`, `Pb2dCanvasDropTarget` in `DarkPalette.axaml` (both dictionaries), three in `01-high-contrast.json`; `Button.pane-toggle` in `Primitives.axaml` | Effectively yes | None. Tokens cost nothing when unused. Keep them in core; a pack token manifest is only needed for (c). Doc drift: `theme-token-catalog.md` lacks `RouteGhost*` and `DropTarget` |
| UiCapture variants | P | 30 `strat-*` keys in the hardcoded dictionary in `Variants.cs` (:232-262), implementations in partial files | No | Variant registration per pack (a static `IUiCaptureVariantSource` list), needed only for (b) |
| CLI | G | `Playback2D.Cli` gained `pack` (Review Queue packs) and `--zones-overlay`, `--query`, `--overlay`. No strat command. `AnalysisBench bg-run` compile-links `ForwardDemoPass.cs` and names `round_facts` | n/a | None for (a). Under (b), `pack` moves with the Review code |
| Embedded resources | P | `Services/Tags/Palettes/*.tagpalette.json`, `Services/Strats/Callouts/*.places.json` in the app csproj | n/a | Move with the assembly under (b) |
| `PlaceField` control | P | `Controls/PlaceField*` depends on `CalloutResolver` | n/a | Move to the pack |

### 3.6 Session, stores, caches

| Touchpoint | Ring | Attaches today | Seam needed |
|---|---|---|---|
| `SessionPayload.StratBook` (`StratBookLayoutState(RailCollapsed, ListCollapsed)`) | P | Positional field on the shell's session record, restored at `MainViewModel.cs:4470`, saved at :4576 | Per-pack session blob keyed by pack id (the per-tab `RestoreState` path already exists for tab VMs) |
| Active tab id persisted as a section id (`stratbook.browser`) | P | `PersistedActiveTabId` | `TrySelectTab` already returns false for a section whose host is gone (`MainViewModel.cs:3644-3655`); restore must then land on Library |
| Config-root stores | P/S | `strats/`, `tags/`, `palettes/`, `suggested-tags/`, `lineup-clips/`, `teams.json`, `review-queue.json`, `watched-situations.json`, `veto-history.json`, `dossier-notes.json`, `strat-mining.json`, `grenade-lineups.json.gz`, `grenades-v3.attempts.json` | None to keep. Pack store registration only matters for "delete my data" (section 7) |
| Cache-root data | P/S | `cache/round-index/*.dvri.json`, `.dvrp.json.gz`; `cache/suggestions/`; `cache/strat-mining/{detected.json, signatures.json.gz}`; `cache/team-index.json`; demo sidecars `.grenades*.json.gz`; fields inside the demo cache record | Opaque pack payloads in the cache record (P4) |

### 3.7 DI

About 50 singleton factory registrations in `App.BuildServices` (:690-1290) for Strat Book types, all
inline in one method, plus the explicit startup force-resolves listed in 3.2. Note that the comment above
`BuildServiceProvider` says `ValidateOnBuild` constructs every singleton. MS.DI validates call sites and
does not invoke factories, so the startup cost is the explicit list at :1373-1402, not every registration.
That is good news: moving the registrations into a `StratBookPack.Register(services)` costs nothing at
runtime, and gating the explicit list is what removes the startup cost.

---

## 4. Dependency map

### 4.1 Core to Strat Book (the edges to cut)

```
App.axaml.cs ............ everything (composition root; expected, becomes Pack.Register calls)
MainViewModel ........... StratBookHubViewModel, StratBookHubView, StratBookLayout(State),
                          TeamIdentityService, IDemoProvenanceSource, strat export chip
ModuleContext ........... StratCaptureHost, StratExportHost   (imports Modules.StratBook)
Playback2DTabViewModel .. ~25 types: StratBook (7), RoundTagger (~10), SuggestedTags (4),
                          Situations (2), Teams (3), RoundFacts; via downcast + App.Services locator
Playback2DView.axaml .... RoundTagger and SuggestedTags views embedded
TimelineControl ......... "Create strat from this round" menu item
Playback2DKeymap ........ strat, tag, suggestion, situation actions in a closed enum
RoundTrack .............. RoundFacts (winner tints)
LibraryTabViewModel ..... TeamIdentityService, IDemoProvenanceSource, TeamFilterItem
HighlightsTabViewModel .. ReviewQueue
AnalysisViewModel ....... RoundFacts, RoundFactsFingerprint
MergedRulesBuild ........ round_facts ruleset, RoundFactsFingerprint
RuleWorkbenchTabVM ...... ZoneOverlayDiagnostics          (Zones: generic, fine)
DemoCacheModels ......... RoundFacts*, RoundIndexState, RoundIndexFingerprint
AppSettings ............. RoundIndexTokenSource
SettingsViewModel/View .. SuggestedTagsTuningViewModel, grenade toggles
UiCapture ............... Strat* variants
Playback2D.Pipeline ..... declares namespace DemoViewer.NET.Services.Review (ReviewQueueModels)
```

The heavy knot is `Playback2DTabViewModel`, and most of it is the Round Tagger and Suggested Tags, not the
Strats section. Only the Create Strat path, `ModuleContext`, `MainViewModel`'s hub and the keymap carry
Strats-specific types.

### 4.2 Strat Book to core (what a pack contract must offer)

Occurrences across the Strat Book folders: `DemoCacheStore` 144, `QueueWork` 28,
`WorkspaceTabDescriptor` 26, `IModuleContext` 17, `QueueJobKind` 14, `IDemoProcessingQueue` 8,
`IDemoEvaluator` 7, `MergedRulesBuild` 6, `FrameClock` 5, `Scene2DHost` 4, `MapSceneHost` 4,
`ModuleContext` (concrete) 3, `IFeatureGate` 3, `DemoEvaluationCoordinator` 3, `AppPaths` 3, `App.Services`
3 (all in `StratCanvasViewModel`), `ISceneFrameHost` 2. Plus Playback2D Core and Pipeline wholesale, and
CS2DemoKit in 33 files (Round Facts, Round Index, Utility Book, capture, Suggested Tags). No Strat Book
code references `MainViewModel` or `DemoLibraryService`; it reaches the shell only through lambdas.

### 4.3 What this means

The reverse edges (4.2) are broad but healthy for options (a) and (b): they are app services a pack in the
same process can be handed. The forward edges (4.1) are the work. They are finite, about fifteen files, and
cluster in four places: the composition root, the shell, 2D Playback, and the demo cache record.

---

## 5. Options

### (a) In-process feature packs, compiled in, expanded contribution model

Every Strat Book module stays in `DemoViewer.NET.dll`. A `StratBookPack` class owns its DI registrations,
modules, evaluators, startup work, settings pages and 2D Playback contributions, and the shell enumerates
packs instead of hardcoding them. One umbrella feature id, with the existing per-section ids under it.

- **Effort:** 24 agent items (section 6). Phases 0 and 1 (real "off", with in-session memory release) are 10.
- **Risk:** low. No new loading, no type-identity problems, no XAML resource resolution across assemblies.
  The main risk is regression in 2D Playback while inverting its dependencies, which has good headless
  coverage (`Playback2D*Tests`, `SceneLayerListParityTests`, follow-card render tests). Owner-facing risk:
  none, the feature works the same when on.
- **Performance and memory:** when on, identical. When off: no Round Index or Grenade walks, no
  `SituationIndex` or `GrenadeIndex` resident, no Team Identity rebuild, no tag-facts saves, no mining. If
  Round Facts goes into the pack (Q1), the `round_facts` rules also leave every forward pass. Code pages
  for the pack stay mapped (it is in the same DLL), which is a few MB of IL and JIT only for what runs; JIT
  cost is zero for code never called. No measurement exists yet for the resident indexes; see section 9.
- **UX when disabled:** the Strat Book tab, Teams view, Library team filter and provenance chip, 2D
  Playback tag palette, suggestion queue, review mode, tag and proposal lanes, Click To Tag, "Rounds like
  this", Create Strat, strat keybinds in the settings list, the Suggested Tags tuning settings, and the
  grenade index toggles all disappear. Data stays on disk. Queue shows no Strat Book job kinds.
- **Testing:** a new matrix axis, pack on and off. One composition-root test per state (what resolves,
  which evaluators register, which startup loads run), plus a "pack off" headless render of 2D Playback.
  Existing tests keep running with the pack on.
- **WASM:** unchanged; the browser gets the same pack registrations and the same switch.

### (b) Separate assembly, loaded at startup, shipped together

`DemoViewer.NET.StratBook.dll` referenced by the heads (or by the app with a project reference reversed via
an interface assembly), registered through the same pack contract. Still one installer, one version.

- **Effort:** (a) plus about 8 items. The blockers are concrete: `ISceneFrameHost`, `Scene2DHost`,
  `MapSceneHost`, `QueueWork`, `DemoCacheStore`, `QueueJobKind`, `MergedRulesBuild` and friends are app
  types the pack would consume, so either they move into a shared `DemoViewer.NET.Core` assembly or the
  pack keeps referencing the app assembly (which only works if the app does not reference the pack, so
  heads compose both). Five downcasts to `ModuleContext` and three `App.Services` uses must go first.
  Avalonia XAML in a second assembly is fine (compiled bindings, `avares://` URIs per assembly); the
  reflection `ViewLocator` needs to search the pack assembly. Strat-only types in Playback2D Core and
  Pipeline (`Keyframes/`, `StratFrameSource`, `StratSceneSpec`, `StratHudDataSource`, route palette,
  guides, token tool) should move out with it or stay as neutral core.
- **Risk:** medium. Mechanical, but wide; touches 144 `DemoCacheStore` call sites indirectly through the
  namespace move, and the UiCapture and test projects need new references.
- **Performance and memory:** same as (a) when off, plus the pack assembly is never loaded if the heads
  register it conditionally. That saves the mapped image and type loads, a few MB at most. Startup is
  unchanged.
- **UX:** same as (a). The only user-visible difference would be a smaller download if a build without
  the pack were ever published, which nothing asks for.
- **Testing:** the boundary becomes compile-enforced: core cannot accidentally take a new dependency on
  the pack. That is the real value of (b). Decision 5 makes (b) the target: (a) is the path to it.

### (c) True runtime plugins (AssemblyLoadContext, third-party capable)

- **Ruled out for the Strat Book.** The add-on design requires add-ons never to reference CS2DemoKit,
  because the family has no cross-version compatibility contract. 33 Strat Book files reference it
  (Round Facts, Round Index, Utility Book grenade walking, Create Strat capture, Suggested Tags). Hiding
  that behind `IModuleContext` would mean promoting a large slice of parsed-demo access into the public
  contract, which is the opposite of that doc's direction.
- Everything else from the add-on doc applies: collectible ALCs are best effort and probably pinned by
  `AvaloniaProperty.Register`, so runtime unload is not a dependable "off"; Windows DLL locking forces
  stage-and-restart; Velopack means add-ons live under the config root; full trust means consent UX and
  signing; WASM cannot load assemblies at all, so the browser would lose the Strat Book.
- **Memory when off:** a not-loaded ALC costs nothing, which is the only advantage over (b), and it is
  available from (b) too by not registering the assembly.
- **Effort:** (b) plus the add-on doc's Phases 1 to 3, several times (b). Value for this request: none
  that (b) does not already give.

### Comparison

| | (a) packs in-process | (b) separate assembly | (c) runtime plugin |
|---|---|---|---|
| Real "off" (no UI, jobs, indexing) | Yes | Yes | Yes |
| Memory when off | Indexes and services gone; IL mapped | Same, assembly not loaded | Same |
| Live toggle without restart | Yes, with in-session release (item 8) | Same | Unload unreliable; restart |
| Boundary enforced by compiler | No (analyzer or test can approximate) | Yes | Yes |
| WASM | Works | Works | Lost |
| Effort (agent items) | 24 | ~32 | far more, blocked on CS2DemoKit rule |
| Risk | Low | Medium | High |

---

## 6. Recommended path

Do (a), and keep every new seam shaped so that (b) is a move rather than a redesign. Each item below is
sized for one agent and one commit set. Item ids are stable; section 11 schedules them. Phase 0 exists so
that the rest can run in parallel: once the contracts and the pack class are in, later items edit the pack
and small, separate seams instead of all queueing on `App.axaml.cs`.

### Phase 0: contracts, the pack skeleton and the directory (2 items, plus the baseline measurement)

0. **Contracts and `StratBookPack`.** `IFeaturePack`, `IPackLifecycle`, `IPackContributions` (section 7;
   only `Module`, `Evaluator` and `JobKind` need bodies now, the rest can be added by the item that first
   uses them), `FeatureScope.Pack`, the catalog accepting pack-contributed descriptors (static core plus
   pack lists, immutable after composition), the rule that a `pack.*` id never fails open, and the rule
   that a `Tab` may have a `Pack` parent. `StratBookPack.Register` moves the ~50 registrations out of
   `App.BuildServices` unchanged; `StratBookPack.Features` lists every Strat Book id (3.1, 3.2) with
   `ParentId = "pack.stratbook"`; `BuildRegistry` asks each pack for its modules. Behaviour is identical
   when on. Test: the composition root resolves the same services and modules as before, and
   `IsEnabled("pack.stratbook")` false cascades every section off and hides the hub.
   0b. **One directory per extension.** Move every Strat Book file under
   `src/App/DemoViewer.NET/Extensions/StratBook/` (section 13), tests under
   `DemoViewer.NET.App.Tests/Extensions/StratBook/`, UiCapture variants under
   `DemoViewer.NET.UiCapture/Extensions/StratBook/`. Pure `git mv` plus csproj globs; namespaces and
   behaviour unchanged, so the diff is renames only and `git log --follow` keeps every file's history.
   Runs alone after item 0, before wave 1, because it touches every file the later items edit. The
   csproj split (Phase 5) then moves that one directory up to `src/Extensions/`.
   M0. **Baseline measurement.** On a copy of the owner's library (never the live config dir): resident set
   after startup, and library index time, at the current head with everything on. Record the numbers in
   section 12 of this doc. Runs alongside item 0; it is the only heavy parse at the time.

### Phase 1: "off" means off (9 items)

1. **Gate the evaluators.** Thread `Func<bool> enabled` into `RoundIndexEvaluator`,
   `GrenadeIndexEvaluator` and `StratMiningService`, the `SuggestedTagsService` pattern; reparent the
   Suggested Tags id. `Wants()` and `PendingPaths()` return nothing when off; opportunistic hooks (the
   grenade walk on open, mining after cache quiet, the inbox badge) return early and unsubscribe.
2. **Round Facts into the pack (decision 1).** Exclude `round_facts` from the highlights fingerprint
   (`MergedRulesBuild.Fingerprint`; it already has `RoundFactsIdentity`), make `MergedRulesBuild` take the
   ruleset as a pack contribution so it leaves every forward pass when off, gate `RoundFactsEvaluator`,
   and make `RoundTrack` draw without winner tints and `AnalysisViewModel` not assume the ruleset. Bench
   A/B with `AnalysisBench --retained`, interleaved, on Library and Highlights passes; the goldens must
   not move with the pack on.
3. **Startup and shutdown through the lifecycle.** `StratBookPack`'s `IPackLifecycle.OnEnabledAsync` runs
   the loads that `App.axaml.cs` lists today (`SituationIndex`, `GrenadeIndex`, `LineupClipService`,
   `TeamIdentityService.StartAsync`, `TagFactsRefresher`, the sidecar and grenade migrations) and
   `OnShutdown` does the flushes with a "was built" check, so shutdown never constructs a store. The app
   calls the lifecycle only when the pack resolves on. `BuildRegistry` stops resolving
   `WatchedSituationsService` and `ReviewQueue` eagerly (the badge subscribes lazily).
4. **Shared surfaces under the pack id.** The Library team filter, provenance chip and Teams section
   (decision 1), the strat export status chip, and the hub all go when `pack.stratbook` is off; session
   restore lands on Library when the persisted active tab is a pack section. Visibility only; the
   contribution seams come in items 12 and 22.
5. **Settings.** One master switch labelled "Strat Book extension" (decision 4) with the per-section and
   sub-feature switches beneath it; the Suggested Tags tuning and grenade sections sit under it; search
   keywords; and the "N demos will be re-indexed in the background" notice when turning on (section 8).
   Note: `FeatureGate.HiddenCount` iterates every catalog row, the `Pack` row included, so a user who
   turns the extension off counts it as one hidden feature plus its tabs.
6. **First-run prompt (decision 2).** `FirstRunWizardViewModel` asks whether to turn the Strat Book
   extension on and writes the master switch; an upgrade from a settings file without the key keeps it on.
7. **Guards.** A test that scans core namespaces for pack namespace imports (section 9), a
   composition-root test for the pack-off state (no pack store resolved, no startup load registered, no
   pack job kind in the queue), and the `theme-token-catalog.md` drift (`RouteGhost*`, `DropTarget`).
8. **Live toggle, both directions.** On `Changed` to on: `OnEnabledAsync` once, then nudge the coordinator
   (`CapacityAvailable`) so pending paths are reconsidered, and attach to any open 2D Playback tab. On
   `Changed` to off: evaluators stop by predicate; a new `CancelOwned(ownerTag)` cancels every queued pack
   job; `OnDisabled` releases every resident index, cache, service and store the pack built
   (`SituationIndex`, `GrenadeIndex`, `SignatureCache`, `TeamIdentityService`, `LineupClipService`,
   `TagFactsRefresher`, the pack stores and cached VMs; the first four already implement `IDisposable` or
   can) and unsubscribes from `DemoCacheStore.Changed` (decision 3: no "reclaimed on restart"). Test:
   turning it off releases what turning it on built, measured with `GC.GetTotalMemory` after a full
   collect, not assumed.
9. **Measure after.** The same numbers as M0 with the pack off at startup, and after an on-to-off toggle
   in session; record them in section 12. The v1.0.0 memory claim rests on M0 versus item 9.

### Phase 2: registries instead of hardcoded lists (5 items)

10. **Module-declared feature ids.** Descriptors carry `FeatureId`; `_tabFeatureIds` shrinks to the
    built-ins; the pack's descriptors come from `StratBookPack.Features` (item 0).
11. **Evaluator registry.** Ordered by declared `After` ids instead of array position; the pending-path
    union is built from the registry. `AppCompositionRootTests` pins the resolved order, not the literal.
12. **Host-tab contributions.** Replace `TabPlacement.StratBook` and the shell's hub synthesis with a
    pack-declared host tab (`HostId = "stratbook.hub"`) and sections that name their host; `MainViewModel`
    loses its `StratBookHubViewModel` field. Library keeps `TabPlacement.Library` or moves to the same
    mechanism.
13. **Job-kind descriptors.** Label, rank, light and owner registered per kind; the enum keeps the core
    kinds; `KindRank`, `IsLight` and `KindLabel` read the registry. (Small.)
14. **Settings-page and status-chip contributions.** The settings section list is driven by a collection;
    the strat export chip becomes a contributed chip.

### Phase 3: 2D Playback inversions (6 items)

15. **Typed services on the context.** `IModuleContext.GetService<T>()` (first-party only) replaces the
    five `ModuleContext` downcasts and the `App.Services` locator in `Playback2DTabViewModel` and
    `StratCanvasViewModel`; `ModuleContext` stops importing `Modules.StratBook`.
16. **Band menus and the side pane.** `Playback2DTimelineViewModel` gets band-menu contributors in place of
    `CanCreateStrat` and the single `LaneMenu` slot, and the Create Strat review pane becomes a side-pane
    contribution. One design, two commits (the PoC finding below).
17. **Right-column panels.** Tag Palette, Tag Editor, Suggestion Queue and Review panel become panel
    contributions with placement, order, gate and focus scope. The follow-card render test locates cards
    by position, so capture before and after.
18. **Lane contributions with behaviour.** Tag and proposal lanes register through the pack with their own
    press, drag and label handlers; the tab stops special-casing `TagTrack.TrackId` and
    `ProposalTrack.TrackId`; review mode becomes a pack-owned toggle the timeline exposes.
19. **Command ids for keybinds.** String-keyed command ids with default chords and scopes, registered by
    packs; the closed `Playback2DAction` enum keeps core actions; the keybind settings list reads the
    registry. Persisted overrides are `"Action=Gesture"` rows keyed by the action name
    (`AppSettings.KeybindOverrides`), so ids that reuse those names keep every override.
20. **Pointer pre-handler and toolbar item.** Click To Tag as a pointer pre-handler contribution; "Rounds
    like this" as a toolbar item.

### Phase 4: data seams (4 items)

21. **Opaque pack payloads in the demo cache record.** `DemoCacheModels` stops importing pack types: the
    Round Facts, Round Index, Suggestions and Grenade fields move into a `Packs` dictionary of
    `JsonElement` keyed by pack id, with the fingerprints the coordinator needs promoted to a neutral
    `PackStamp(Id, Schema, Fingerprint)`. Old records are read by mapping the old fields once.
22. **Library contributions.** The team filter and provenance chip become `ILibraryContribution`s;
    `MainViewModel` and `LibraryTabViewModel` stop taking `TeamIdentityService` and
    `IDemoProvenanceSource`.
23. **Pack session state.** `SessionPayload.StratBook` becomes a per-pack blob through the existing
    `RestoreState` path.
24. **Delete Strat Book data.** `StoreDescriptor`s for every pack store and cache path, and a confirmed
    settings action that names the user-work stores (section 8).

### Phase 5: own csproj, option (b) (8 items, required by decision 5)

Possible only once Phases 2 to 4 have cut every core-to-extension edge in section 4.1, because the app
assembly cannot reference the extension. The extension references the app assembly; the heads (Desktop,
Browser) reference both and hand the pack to `App.BuildServices`. No `Core` assembly is needed for this
step.

25. `src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook.csproj`: move the Phase 0b directory up,
    reference the app project, keep namespaces. 26. Move strat-only types out of Playback2D Core and
    Pipeline (`Keyframes/`, `StratFrameSource`, `StratSceneSpec`, `StratHudDataSource`, route palette,
    guides, token tool) into the extension, or leave the neutral ones in core with a note. 27. ViewLocator
    and `avares://` resources across assemblies; embedded palettes and callouts move with the project.
    28. `DemoViewer.NET.Extensions.StratBook.Tests` and the UiCapture variants reference the extension.
    29. Heads register the pack conditionally. 30. A build-time check that the app project does not
    reference the extension (the compiler now enforces it; the test from item 7 is retired). 31. WASM
    publish check: the Browser head compile-links the extension. 32. `scripts/test.sh` gains the extension
    test project and tier.

### Phase 6: independent release cadence (about 6 items)

The extension ships and updates separately from the app. Bounded by one hard fact: the extension uses
CS2DemoKit types directly (33 files), so an extension build is compatible only with app builds on the
same CS2DemoKit version and app contract. The loader enforces that and disables, never crashes.

33. **Contract version.** The app exposes `ExtensionHostVersion` (the pack contracts) and its CS2DemoKit
    version; the extension carries a manifest (`extension.json`: id, version, `requiresHost` range,
    `requiresCs2DemoKit`). A mismatch disables the extension with a settings-page message.
34. **Loader.** At startup the Desktop head loads first-party extension assemblies from
    `<config root>/extensions/<id>/<version>/` when present, else the copy shipped in the installer. Loads
    into the default context (no unload: "off" is the Phase 1 switch, not an unload). Browser head
    unchanged: it compile-links the version it was built with.
35. **Signing and trust.** Only assemblies signed with the project's key load from the config root;
    anything else is ignored with a log line. First-party only; the add-on design's consent UX is not
    pulled in.
36. **Update feed.** Velopack owns `current/` and cannot carry a second package, so the extension has its
    own feed (a GitHub release per extension version); the app's Update service checks it, downloads,
    verifies, stages under the config root and applies on next start. Settings shows the installed and
    available versions under "Extensions".
37. **CI and packaging.** A release workflow per extension producing the signed zip and feed entry; the
    app installer still bundles the extension version current at app release time.
38. **Compatibility matrix test.** A test that builds the extension against the app and asserts the
    manifest's ranges match the referenced versions, so a release cannot ship an unloadable pair.

Other first-party modules (Highlights, Rule Workbench, Library sections) can take the same layout later;
nothing here depends on it.

### Proof of concept

Not built on this spike. The cheapest seam to prototype, item 16's menu, does not stand alone: the menu
item's action opens the Create Strat review pane at `Playback2DView.axaml:525-540`, which is bound to a
property on the tab VM. A menu contribution without a pane contribution would still need the tab VM to
know about `CreateStratDialogViewModel`, which proves nothing about the boundary. So the finding the PoC
would have produced is recorded instead: **a 2D Playback contribution is a pair (an entry point plus the
surface it opens), and the contribution API must carry both** (section 7.3, `IPlaybackContribution`).

---

## 7. Proposed extension API

Sketches, not code. Names follow the existing contracts. Everything here is first-party and lives in the
app (option a) or in `DemoViewer.NET.Core` (option b); none of it goes into the public
`Modules.Abstractions` package until the add-on work wants it.

### 7.1 The pack

```csharp
public interface IFeaturePack
{
    string Id { get; }                       // "net.demoviewer.pack.stratbook"; persisted key, distinct from the module id
    string FeatureId { get; }                // "pack.stratbook"; the umbrella gate
    IEnumerable<FeatureDescriptor> Features { get; }   // parented to FeatureId
    void Register(IServiceCollection services);        // all DI, unconditional (factories are lazy)
    void Contribute(IPackContributions to, IServiceProvider sp);
}

public interface IPackLifecycle            // optional, resolved from the pack's own registrations
{
    Task OnEnabledAsync(PackStartReason reason, CancellationToken ct);  // startup loads, subscriptions
    Task OnDisabledAsync();                  // unsubscribe, cancel owned jobs, release resident indexes (completes when released)
    void OnShutdown(TimeSpan budget);        // flushes
}

public interface IPackResident              // a pack-built singleton whose state can be dropped and rebuilt (item 8)
{
    void Attach();                           // subscribe to the sources that keep it current; loads nothing
    void Release();                          // unsubscribe, flush what is pending, drop the state
}
```

`Register` is unconditional on purpose: registrations are free, and conditional DI makes "turn on without
restart" impossible. What the gate controls is `OnEnabledAsync`, the evaluators' predicates, and the
contributions' visibility.

### 7.2 Contributions

```csharp
public interface IPackContributions
{
    void Module(IWorkspaceModule module);                    // tabs and sections
    void HostTab(HostTabContribution host);                  // a tab that hosts sections (the hub)
    void Evaluator(Func<IDemoEvaluator> factory, params string[] after);
    void JobKind(JobKindDescriptor kind);                    // label, rank, light, owner
    void SettingsPage(SettingsPageContribution page);        // header, order, VM factory, view factory, keywords
    void StatusChip(StatusChipContribution chip);
    void Playback(IPlaybackContribution contribution);       // see 7.3
    void Library(ILibraryContribution contribution);         // filters, card badges, sections
    void Commands(IEnumerable<CommandDescriptor> commands);  // keybinds and palette
    void Session(string key, ISessionParticipant participant);
    void Store(StoreDescriptor store);                       // paths, for "delete pack data"
    void Migration(string id, Func<IDemoProcessingQueue, Task> submit, TimeSpan delay);
    void Route(string prefix, Func<string, bool> navigate);  // "stratbook/strat/{id}"
}
```

Every contribution carries the pack's `FeatureId` implicitly (the pack registered it), and may carry a
narrower sub-feature id. The shell shows a contribution only when both resolve on.

### 7.3 2D Playback

```csharp
public interface IPlaybackContribution
{
    void Attach(IPlaybackSurface surface, IModuleContext context);  // per tab VM instance
    void Detach();
}

public interface IPlaybackSurface
{
    void AddBandMenu(Func<TimelineBandViewModel, IEnumerable<MenuEntry>> items);
    void AddLane(ITimelineTrack track, TimelineBandRow row, ILaneBehaviour? behaviour = null);
    IPaneHandle AddPane(PanePlacement where, int order, Func<object> viewModel);  // side pane, right column
    void AddToolbarItem(ToolbarItem item);
    void AddPointerPreHandler(Func<ScenePointer, bool> handler);  // Click To Tag
    void AddLayer(string layerId, Func<ISceneLayer> layer);        // later; guides
    void AddTool(IPointerTool tool);                               // later; token
    IDisposable OnDemoChanged(Action handler);
}
```

The Create Strat contribution would add a band-menu entry for round bands whose action opens a pane it
added (`AddPane(PanePlacement.Side, ...)`), using `IStratCapture` resolved from `context.GetService<T>()`.
That pairing is the PoC finding in section 6 (item 16).

`AddLayer` and `AddTool` exist for completeness and for (b). For (a), the token tool and guides layer can
stay core-registered: they are inert without a strat frame host and cost nothing. Code keeps the word
"pack" for the type names; user-facing copy says "extension" (decision 4).

### 7.4 Library, settings, session, stores

```csharp
public interface ILibraryContribution
{
    IEnumerable<LibraryFilter> Filters { get; }      // Team
    LibraryBadge? BadgeFor(DemoLibraryEntry entry);  // provenance chip; must be O(1), no I/O
}

public interface ISessionParticipant
{
    JsonElement? Snapshot();
    void Restore(JsonElement state);                 // must tolerate an old shape
}

public sealed record StoreDescriptor(string Id, string Label, StoreRoot Root, IReadOnlyList<string> Paths);
```

Theme tokens are not a contribution in (a) or (b): they stay in the core dictionaries, which cost nothing
when unused. A pack token manifest only matters for third-party add-ons.

### 7.5 Commands and keybindings

```csharp
public sealed record CommandDescriptor(
    string Id,                // "stratbook.step.add"; persisted override key
    string Label,
    string Scope,             // "playback2d", "playback2d.palette", "stratbook.canvas"
    KeyGesture? DefaultChord,
    Func<CommandContext, bool> Run,
    Func<CommandContext, bool>? CanRun = null);
```

`Run` returns `bool`, not `void`: the existing dispatch convention (`Playback2DTabViewModel.ExecuteAction`
returning false means unhandled, so the key falls through to whatever else wants it) has to survive
through a command, or a resolved key that does nothing would read as handled anyway.

Core `Playback2DAction` values map to command ids one to one, so persisted keybind overrides keep working.
Item 19 landed ids equal to the action's own enum name (not the `stratbook.step.add` style sketched
above), since that is what keeps a persisted `KeybindOverrides` row readable unchanged; item 19's own
report is the place to check before copying the dotted style for a future pack. A command palette, if one
is ever built, reads the same registry.

### 7.6 How the gate folds in

- `pack.stratbook` is a `FeatureScope.Pack` descriptor with per-category defaults. Every Strat Book tab,
  section and sub-feature id gets `ParentId = "pack.stratbook"` (directly, or through its tab).
- The existing resolver order stays: Required, override, category default, group leader, then cascade up
  the parent chain. The only rule change is that a `Tab` may now have a parent, and only a `Pack`.
- "Plugin enabled" is exactly `IsEnabled("pack.stratbook")`. The category model keeps working: if Q2 says
  consumers should not see the Strat Book by default, that is `Defaults(false, true, true)` on one id.
- Per-section overrides stay meaningful under an enabled pack (a user can hide Dossier and keep Strats).
- The gate becomes the one authority for background work too, not just visibility: evaluators and pack
  lifecycle read the same answer. That requires one new rule in the gate's contract: **a pack id never
  fails open.** An unknown `pack.*` id resolves off, so a typo cannot silently enable background work.

---

## 8. Disable semantics

"Off" means, per resource:

| Resource | When turned off at runtime | After restart with it off |
|---|---|---|
| UI (tabs, sections, panes, lanes, menus, keybinds, settings pages, chips) | Gone immediately (gate is already live; sections reconcile by identity) | Never built |
| Background jobs (evaluators, mining, inbox, lineup clips, migrations) | Evaluators stop at the next `Wants()` poll; queued jobs owned by the pack are cancelled by owner tag; a job already running finishes its current unit | Never queued |
| Indexing passes (Round Index, Grenade walk, Suggested Tags, Round Facts if pack-owned) | Stop at the next demo; nothing new written | Not run. Library indexing does strictly less work |
| Resident memory (`SituationIndex`, `GrenadeIndex`, `SignatureCache`, cached VMs) | Released in session (owner decision 3; Phase 1 item 4) | Not allocated |
| Startup cost (index loads, Team Identity rebuild, store construction) | n/a | None |
| Data on disk (stores in 3.6, cache sidecars, record fields) | Kept, untouched | Kept, untouched |

**Existing data.** Nothing is deleted when the pack is turned off. The settings page can offer "Delete
Strat Book data" as a separate, confirmed action that removes the paths in the pack's `StoreDescriptor`s;
cache sidecars are regenerable, `strats/`, `tags/`, `teams.json`, `review-queue.json` and the dossier
stores are user work and must be called out by name in the confirmation.

**Round Facts while off (item 2).** Both the writer and the reader are gated: `RoundFactsEvaluator` writes
nothing and `RoundFactsSource` answers "no rows" and forwards no `Updated`, so winner tints, situation joins
and tag labels go with the pack rather than showing rows written while it was on. The rows stay in the cache
records and come back with the pack; a bare run cached under one gate state is not served under another.

**Stale cache while off.** Library keeps indexing new demos without pack passes. The pack fields of those
records are simply absent (or, after Phase 4, the `Packs` entry is missing). Fields of records indexed
before the switch stay as they were.

**Re-enabling.** The pack's evaluators report every demo whose pack fingerprint is missing or stale through
`PendingPaths()`, which is the existing mechanism, so re-enabling backfills automatically. The cost is a
re-index of everything indexed while off, which on a large library is the same order as a first index.
If Round Facts is in the pack, the highlights fingerprint must be split first (section 9), or each toggle also
re-scans every demo's highlights. The settings page should say so ("N demos will be re-indexed in the background") and the backfill should
be visible and pausable in the queue, per the standing rule that all background work goes through it.

**Live toggle (item 8, as built).** Live in both directions, and turning off releases the pack's memory in
session (decision 3). A core `PackSwitch` subscribes to the gate's `Changed` once per pack and acts on real
transitions only: the resolved `IsEnabled(pack.FeatureId)` against the state the lifecycle was last put in,
so a settings write that leaves the pack where it was does nothing. `App.StartPacks` is its startup pass.

- *Off to on:* `OnEnabledAsync(EnabledInSession, ct)`, the same code path as startup: one attach item
  first ("Strat Book: attach services", which attaches the lineup clips, Team Identity (its file read and
  `StartAsync` are queued from inside it, so the Library team filter is not held behind the index loads),
  the facts refresher, the zone graphs and every resident built lazily before a release), then the two
  index loads, all queue items at user priority on the pack's serial; then a user-priority `SectionCompute`
  item on the same serial, owned by the pack id ("Strat Book extension: find demos to re-index"), re-polls
  the library through the coordinator, so the pack's evaluators submit every demo whose pack fields are
  missing or stale and the backfill is visible and pausable in the queue. Open 2D Playback tabs and the
  shell react through the gate's `Changed` as before.
- *On to off:* the enable's token is cancelled, every queued pack item goes by owner tag through
  `IDemoProcessingQueue.CancelOwned(ownerTag)` (a running one finishes its unit; a parse the library co-owns
  stays), and the release itself is one `SectionCompute` item at user priority, owner the pack id, on the
  same serial, so a large index never leaves memory on the UI thread and never beside a load still running.
  `OnDisabledAsync` returns that item's completion.
- *Ordering.* Every item of the pack's own (loads, attach, release) shares one serial, so nothing of the
  pack's state changes outside a totally ordered item, and each carries the lifecycle epoch it was queued
  under: every enable and disable bumps it, an enable also drops a release still queued (by owner), and an
  item that runs under an older epoch does nothing. So a fast off-on leaves everything attached and the
  live view intact (shutdown's lineup flush still writes), and a fast on-off leaves nothing loaded.
- *What release means.* The residents are container singletons that core surfaces hold references to
  (Team Identity for the Library filter, the situation index for the shell), so the object cannot be
  replaced; its state can. Each implements `IPackResident`: `Release` unsubscribes from the sources that
  would refill it (`DemoCacheStore.Changed`, the evaluators' `Written`/`Indexed`, the index's `Changed`),
  writes anything pending (lineups, the signature cache) and drops the loaded data; `Attach` subscribes
  again and the next load rebuilds. Released: `SituationIndex`, `GrenadeIndex` (and its lineup document),
  `TeamIdentityService` (side keys, assignments, joins, both files), `LineupClipService` (plan, ranks,
  requests), `TagFactsRefresher`, `WatchedSituationsService` (the new-hit groups; the saved watches stay),
  `StratMiningService` (the quiet timer, the patterns, the `SignatureCache`), and the zone graphs in
  `AssetZonePlaceResolverSource` (only pack code reads them). `StratBookPackInstances` keeps the built
  residents across a release and nulls its typed view, so shutdown flushes nothing that was dropped and a
  re-enable restores and re-attaches everything built lazily before the release. A mine or a lineup render
  still running in the heavy lane when the release runs (their items set no serial) skips its next step
  once detached and writes nothing back; the release lets it end (bounded by one batch) before dropping the
  signature cache, which is not thread-safe.
- *Not released:* the pack's tab view-models (container singletons the modules hand to the shell; their
  result lists are bounded by the last query and their map assets by the last map shown) and the pack's
  small user-truth stores (strats, tags, dossier notes, veto history, proposals, profile, site regions),
  which are the user's files and cheap. The hub shows no "stopping" state: its tab is gone the instant the
  gate flips; the release item is visible in the queue instead.
- *First run.* On a fresh desktop install `StartPacks` waits while `SettingsService.NeedsFirstRun` is true;
  the wizard's Finish or Skip writes settings, which is a gate change like any other, and the pack starts
  if its answer resolves on (accept, or Skip with the default) and stays unbuilt if it resolves off. The
  browser never shows the wizard and never waits. Upgrades with the flag set start as today. Team
  Identity, which the shell builds for the Library filter before the wizard has asked, is built detached
  and unread whatever the gate says at container build; only the attach item reads its files, and a read
  that reaches a detached service (a queued one that lost the race with a release) reads and writes nothing.
- *Measured* (`StratBookLiveToggleTests`, `[Category("Budget")]`, 160 synthetic demos with 24 rounds and
  60 grenades each, a mine and a watch seeded): an enable builds about 21 MB on the GC heap; the release
  leaves 0.5 MB after the first off-on-off cycle and 0.0 MB after the second, so nothing grows per toggle.

**Session restore.** If the persisted active tab id belongs to a disabled pack, restore lands on Library.
Pack session blobs of a disabled pack are preserved in the session file untouched, so re-enabling restores
the collapsed rail.

**WASM.** Same switch, same semantics; there is less to turn off because the browser already keeps strat
data for the session only.

---

## 9. Risks

- **Boundary drift.** Under (a) nothing stops a new core-to-pack reference. Mitigate with a test that scans
  the core namespaces' `using`s for pack namespaces (cheap), until (b) makes it a compile error.
- **Round Facts placement.** If it moves into the pack, `MergedRulesBuild` must accept ruleset contributions
  and `RoundTrack` must tolerate no tints. The highlights fingerprint is computed over every effective
  ruleset, `round_facts` included (`MergedRulesBuild.Fingerprint`, :72-84), so dropping the ruleset from the
  merged set on toggle would mark every demo's highlights stale and force a library-wide Reels re-scan in
  both directions. Either exclude `round_facts` from that fingerprint first (it already has its own,
  `RoundFactsIdentity`) or keep the ruleset in the merged set and gate only the evaluator's writes; Library
  and Highlights forward passes change shape and need a bench A/B (`AnalysisBench` with `--retained`,
  interleaved per the bench-variance note). Decision 1 puts it in the pack, so the split is item 2.
- **Team Identity in the Library.** `Services/Teams`, the Library team filter and the provenance chip are
  all new on this branch (nothing under `Services/Teams` at the merge base), so putting Teams in the pack
  takes away nothing main's users have today. Keeping it core instead means its factory must stop
  constructing pack stores.
- **2D Playback regressions.** Phase 3 rewires the busiest tab VM (3,560 lines). The follow-card render
  test locates cards by list position and will break on any panel reordering. Land Phase 3 one item at a
  time with UiCapture before and after.
- **Concurrent work.** The editor's start-block and clock work landed on 2026-10-02. Phase 3 items 15 and
  19 touch `StratCanvasViewModel` and the keymap; any further editor item should avoid those two files
  while they are in flight (section 11 lists hot files per item).
- **Evidence gaps.** No measurement exists of what the pack costs in RAM or index time when on. M0 and
  item 9 close that; section 12 holds the numbers. Also unverified: what session restore does after
  `TrySelectTab` returns false (item 4 covers it).
- **Scope creep toward (c).** The add-on doc's security and distribution work is a separate decision. None
  of this plan depends on it, and none of it should be pulled forward on the Strat Book's account.

---

## 10. Owner decisions (2026-10-02)

1. **Boundary:** the whole Strat Room is in the pack, and so are Round Facts, Teams and Provenance, so "off"
   removes their indexing cost. The Review Queue stays core, since Reels uses it.
2. **Default:** first-run setup asks whether to turn it on; the answer sets the master switch. Upgrades keep
   it on.
3. **Toggling:** turning it off is live and must also release its memory in session. No "reclaimed on
   restart" fallback.
4. **Name:** users see it as an **extension** ("Strat Book extension"). The per-section switches stay under
   the master switch.
5. **Structure:** each extension lives in its own directory from the start (Phase 0b) and becomes its own
   csproj once the edges are cut (Phase 5), so extensions can be released on a different cadence from the
   app (Phase 6).


---

## 11. Parallel execution plan

The items in section 6 are the units. This section says who builds each, what each may touch, and which
can run at the same time. The rules come from how the rest of this branch was built: one worktree and
one branch per item, a read-only review before every merge, and the standard tier after every merge.

### 11.1 Rules

- **Concurrency cap: three agents building at once** on this 16 GB machine (each builds the solution in
  its worktree), plus read-only reviewers. Environmental items (M0, 2, 9) parse demos or run the bench
  and never overlap each other; at most one of them runs at a time.
- **Hot-file ownership.** Two items in flight never share a hot file. The hot files are `App.axaml.cs`,
  `MainViewModel.cs`, `LibraryTabViewModel.cs`, `SettingsView.axaml` and `SettingsViewModel.cs`,
  `FeatureCatalog.cs`, `Playback2DTabViewModel.cs` and `Playback2DView.axaml`, `Playback2DKeymap.cs`,
  `DemoProcessingQueue.cs`, `MergedRulesBuild.cs`, `DemoCacheModels.cs`, `StratCanvasViewModel.cs`, and
  `StratBookPack.cs` once it exists. The table names each item's hot files; the schedule below is derived
  from those names and the dependencies.
- **Branches:** `feature/strat-book-ext-<item>-<slug>`, cut from `feature/strat-book` at the time the
  item starts. An agent whose base has moved merges `origin/feature/strat-book` before reporting.
- **Models.** Opus-class (the session model) for anything that defines a contract, inverts a dependency,
  or releases memory: items 0, 2, 8, 12, 16, 17, 18, 21. Sonnet for mechanical but wide work with clear
  acceptance: 1, 3, 4, 5, 6, 10, 11, 14, 15, 19, 20, 22, 23, 24, M0, 9. Haiku for small, fully specified
  edits with a test: 7, 13. Reviews: Sonnet first pass for every item; an Opus re-verify for 0, 8, 16
  to 18 and 21 before merge.
- **Per item:** the builder commits in small commits with tests; a Sonnet reviewer reads the diff and
  reports blockers, should-fixes and nits; the builder fixes on the same branch; the orchestrator merges
  `--no-ff` in wave order, runs the standard tier, `AppCompositionRootTests` and the Strat window
  classes, pushes, records the item in `plan.md`, and removes the worktree. The WASM head builds in
  Release after each wave.
- **Testing without duplication.** Three roles, three budgets. A builder runs only the test classes its
  item touches or adds, plus `AppCompositionRootTests`, and never the standard tier, the bench or the
  WASM build. A reviewer reads the diff and runs nothing, except the item's new test class when a claim
  needs checking. The orchestrator owns the one **heavy lane**: the standard tier once per wave on the
  merged head (per merge, only the affected classes and `AppCompositionRootTests`), the WASM Release
  build once per wave, and the Environmental items (M0, 2, 9) one at a time. Nothing in the heavy lane
  ever runs concurrently with another heavy-lane process. If a wave's tier fails, the orchestrator
  bisects by running the failing class on each merge commit of the wave rather than re-running the tier.
- **Done means:** the standard tier passes apart from the known `ThePinnedRounds_ExerciseEveryDetector`;
  the pack-on behaviour is unchanged (goldens and window tests); the pack-off composition-root test
  (item 7) passes from item 7 onward; and no core namespace imports a pack namespace (item 7's guard).

### 11.2 Items

| Item | Phase | Model | Size | Hot files | Depends on | Review |
|---|---|---|---|---|---|---|
| 0 contracts and pack skeleton | 0 | Opus | L | App.axaml.cs, FeatureCatalog.cs, FeatureDescriptor.cs, BuildRegistry, new StratBookPack.cs, Features/ | none | Sonnet, then Opus |
| 0b one directory per extension | 0 | Sonnet | M (renames only) | every Strat Book file; the three csproj globs | 0 | Sonnet (renames only, no behaviour) |
| M0 baseline measurement | 0 | Sonnet (Environmental) | S | none (a bench script and section 12) | none | none |
| 1 gate evaluators | 1 | Sonnet | M | RoundIndexEvaluator, GrenadeIndexEvaluator, StratMiningService, SuggestedInboxModule | 0 | Sonnet |
| 2 Round Facts into the pack | 1 | Opus (Environmental) | L | MergedRulesBuild.cs, RoundFactsEvaluator, RoundTrack, AnalysisViewModel, StratBookPack.cs (one contribution line) | 0 | Sonnet |
| 3 lifecycle: startup and shutdown | 1 | Sonnet | M | StratBookPack.cs, App.axaml.cs (the startup block and shutdown flushes only), BuildRegistry | 0 | Sonnet |
| 4 shared surfaces under the pack id | 1 | Sonnet | M | MainViewModel.cs, LibraryTabViewModel.cs, LibraryTabView.axaml | 0 | Sonnet |
| 5 settings master switch | 1 | Sonnet | M | SettingsView.axaml, SettingsViewModel.cs | 0 | Sonnet |
| 6 first-run prompt | 1 | Sonnet | S | FirstRunWizardViewModel, its view, SettingsService (the upgrade default) | 0 | Sonnet |
| 7 guards and doc drift | 1 | Haiku | S | new tests only; theme-token-catalog.md | 0 | Sonnet |
| 8 live toggle, release on disable | 1 | Opus | L | StratBookPack.cs, DemoProcessingQueue.cs (CancelOwned by owner), SituationIndex, GrenadeIndex, SignatureCache, TeamIdentityService, LineupClipService, TagFactsRefresher | 1, 3 | Sonnet, then Opus |
| 9 measure after | 1 | Sonnet (Environmental) | S | section 12 | 8 | none |
| 10 module-declared feature ids | 2 | Sonnet | M | MainViewModel.cs (`_tabFeatureIds`), WorkspaceTabDescriptor, the eight Strat Book modules | 0, 4 | Sonnet |
| 11 evaluator registry | 2 | Sonnet | M | App.axaml.cs (evaluator list), DemoEvaluationCoordinator, AppCompositionRootTests | 1, 2 | Sonnet |
| 12 host-tab contributions | 2 | Opus | L | MainViewModel.cs, TabSectionHost, TabPlacement, StratBookHub* | 10 | Sonnet, then Opus |
| 13 job-kind descriptors | 2 | Haiku | S | DemoProcessingQueue.cs (KindRank, IsLight), DemoQueueRowViewModel | 8 | Sonnet |
| 14 settings-page and chip contributions | 2 | Sonnet | M | SettingsView.axaml, SettingsViewModel.cs, MainViewModel.cs (chip slot) | 5, 12 | Sonnet |
| 15 typed services on the context | 3 | Sonnet | M | ModuleContext.cs, IModuleContext, Playback2DTabViewModel.cs (downcasts), StratCanvasViewModel.cs (App.Services) | 0 | Sonnet |
| 16 band menus and side pane | 3 | Opus | L | Playback2DTimelineViewModel, TimelineControl, Playback2DTabViewModel.cs, Playback2DView.axaml | 15 | Sonnet, then Opus |
| 17 right-column panels | 3 | Opus | L | Playback2DTabViewModel.cs, Playback2DView.axaml | 16 | Sonnet, then Opus |
| 18 lane contributions | 3 | Opus | L | Playback2DTabViewModel.cs, Playback2DTimelineViewModel, TagTrack, ProposalTrack | 17 | Sonnet, then Opus |
| 19 command ids for keybinds | 3 | Sonnet | M | Playback2DKeymap.cs, keybind settings list, AppSettings.KeybindOverrides | 0 | Sonnet |
| 20 pointer pre-handler and toolbar item | 3 | Sonnet | S | Scene2DHost, Playback2DView.axaml (toolbar) | 16 | Sonnet |
| 21 opaque pack payloads in the cache record | 4 | Opus | L | DemoCacheModels.cs, DemoCacheStore, the four evaluators' writes | 2, 11 | Sonnet, then Opus |
| 22 Library contributions | 4 | Sonnet | M | LibraryTabViewModel.cs, LibraryTabView.axaml, MainViewModel.cs | 12 | Sonnet |
| 23 pack session state | 4 | Sonnet | S | MainViewModel.cs (session restore and save), SessionPayload | 12 | Sonnet |
| 24 delete Strat Book data | 4 | Sonnet | S | SettingsView.axaml, SettingsViewModel.cs, StratBookPack.cs (store descriptors) | 14 | Sonnet |
| 25 extension csproj | 5 | Opus | L | the whole extension directory, the three csprojs, the heads | 21, 22, 23, 24 (every Phase 4 item) | Sonnet, then Opus |
| 26 strat-only types out of Playback2D | 5 | Sonnet | M | Playback2D Core and Pipeline strat types | 25 | Sonnet |
| 27 ViewLocator and avares | 5 | Sonnet | S | ViewLocator, resource URIs | 25 | Sonnet |
| 28 extension test project and UiCapture | 5 | Sonnet | M | test and UiCapture csprojs | 25 | Sonnet |
| 29 heads register the pack | 5 | Haiku | S | Desktop and Browser Program.cs | 25 | Sonnet |
| 30 compile-enforced boundary | 5 | Haiku | S | the app csproj; retire the item 7 scan | 25 | Sonnet |
| 31 WASM publish check | 5 | Haiku | S | wasm-build workflow | 29 | Sonnet |
| 32 test.sh tier | 5 | Haiku | S | scripts/test.sh | 28 | Sonnet |
| 33 contract version and manifest | 6 | Opus | M | new Extensions host contract, extension.json | 25 | Sonnet, then Opus |
| 34 loader | 6 | Opus | L | Desktop head, AppPaths | 33 | Sonnet, then Opus |
| 35 signing and trust | 6 | Sonnet | M | loader, CI key | 34 | Sonnet |
| 36 update feed | 6 | Opus | L | Services/Update, Settings "Extensions" page | 34, 35 | Sonnet, then Opus |
| 37 CI and packaging | 6 | Sonnet | M | release workflows | 35 | Sonnet |
| 38 compatibility matrix test | 6 | Haiku | S | new test | 33 | Sonnet |

### 11.3 Schedule

Waves are groups whose items share no hot file and whose dependencies are all merged. Within a wave,
items run in parallel up to the cap of three; the order inside a wave puts the items that unblock the
most first.

| Wave | Items in parallel | Notes |
|---|---|---|
| 0 | 0; M0 | M0 is the only heavy parse; it edits no code |
| 0b | 0b | alone: renames every Strat Book file; nothing else may be in flight |
| 1a | 1, 3, 4 | all three unblock later items; disjoint files after item 0 |
| 1b | 2, 5, 6 | 2 is Environmental (bench), so M0 must have finished |
| 1c | 7, 15, 19 | 15 and 19 are Phase 3 items with no Phase 1 dependency; they touch files nothing in Phase 1 touches |
| 1d | 8 | alone: it edits the queue and every resident service, and it is the item the memory claim rests on |
| 1e | 9, 10, 11 | 9 is Environmental; 10 and 11 start Phase 2 |
| 2a | 12, 13 | 13 after 8 because both edit the queue |
| 2b | 14, 16 | 16 starts the 2D Playback chain |
| 3a | 17, 21, 22 | 21 and 22 share nothing with 17 |
| 3b | 18, 23, 24 | |
| 3c | 20 | last of the Playback chain |
| 5a | 25 | alone: the project split |
| 5b | 26, 27, 28 | |
| 5c | 29, 30, 31, 32 | four Haiku items; run three, then one |
| 6a | 33 | contract first |
| 6b | 34, 38 | |
| 6c | 35, 37 | |
| 6d | 36 | |

Phase 1 is complete after wave 1d plus item 9; that is the point to show the owner the settings switch,
the first-run prompt and the measured numbers. Phases 2 to 4 are the edge cuts that Phase 5 needs, so
with decision 5 they are not optional; the checkpoint decides pacing, not whether.

### 11.4 What the orchestrator does between waves

- Merge each item as its review clears, in the order the table's dependencies allow, not in wave order
  when an earlier item is still in review.
- After each merge: standard tier, `AppCompositionRootTests`, the Strat window classes
  (`StratEditorRoomTests`, `StratCanvasViewTests`, `DetectedStratsTests`, `StratNewStratButtonsTests`,
  `StratStartRowWindowTests`, `StratDragWindowTests`), and from item 7 onward the pack-off test.
- After each wave: build the Browser head in Release, update `plan.md`, remove the worktrees.
- Before starting an item whose hot files an in-flight item also names: wait.

## 12. Measurements

Filled in by M0 and item 9. Resident set after startup and library index time, on a copy of the owner's
library, with the pack on (M0), off at startup (9), and after an on-to-off toggle in session (9).

| State | Resident set after startup | Library index time | Notes |
|---|---|---|---|
| Pack on (M0, head f48f6695) | GC heap 368.7 MB immediately after settling (median of 5); drops to 323.5 MB after 90s more idle with no queue activity to explain it (median of 5), likely a .NET buffer-pool trim rather than app or pack behavior, since the isolated probe drops by the same amount with no app, no queue and no Avalonia at all | full 6-evaluator pass 14.5 s versus library+highlights-only 9.3 s, pooled median of 3 runs x 4 demos each (about 57% slower, a floor: see §12.1 on library's own asymmetry; run medians ranged 44 to 87%) | macOS arm64, Release, head f48f6695, 382-demo copy. `PrivateMemorySize64` reads 0 on this OS; see §12.1. |
| Pack on, rerun (9, head 396495e1) | GC heap 369.9 MB immediately after settling (median of 5); after 90s more idle the drop M0 saw recurred in only 2 of 5 trials (down to ~324.6 MB, the same ~45 MB M0 measured), while the other 3 rose instead (to ~374.8 MB), so the late median (374.8 MB) sits above the early one | full 6-evaluator pass 13.5 s versus library+highlights-only-with-the-pack-still-on 7.7 s, one run x 4 demos each (about 76% slower). Not the pack-off comparison; see the pack-off row and §12.3 for that | macOS arm64, Release, head 396495e1 (code-identical to fa77bad5), fresh copy of the same 382-demo library |
| Pack off at startup (9) | GC heap 29.6 MB immediately after settling (median of 5); 29.7 MB after 90s more idle (median of 5), essentially flat: no drift recurs when nothing of the pack ever decodes a sidecar. Working set 203.8 MB early / 202.4 MB late (median), but individual trials ranged 116 to 206 MB late, noise with no counterpart in the GC heap | library + highlights only, which is what `Wants()` resolves the full evaluator list to with the pack off: two runs over the same 8 demos, 9.2 s and 9.4 s median; `round_facts` confirmed excluded from the merged ruleset (`MergedRulesBuild.EnabledDoc("round_facts") is null`) | macOS arm64, Release, head 396495e1, fresh copy of the same library with `Features.Overrides["pack.stratbook"] = false` in settings.json |
| On, then off in session (9) | GC heap: on 369.9 MB, then off 84.1 MB immediately after the release, then off 31.2 MB after 90s more idle (matching the pack-off-at-startup row's 29.6 MB within 1.6 MB), then on again 369.4 MB (medians of 3, real `IDemoProcessingQueue`). The 84.1 MB immediate figure is transient (gone after 90s idle; likely a pool trim), not retained pack state: once it settles, re-enabling costs about the same as the first enable, not more | not measured in session; the library+highlights-only figure is the pack-off row above | macOS arm64, Release, head 396495e1, same 382-demo copy as the pack-on rerun, pack on at boot, off and on again through `SettingsService.Write` + `PackSwitch` |

### 12.1 M0 method and numbers

Reproduced by `tools/strat-book-baseline/run.sh` (also usable directly: the three `StratBookPackBaselineTests`
probes in `DemoViewer.NET.App.Tests`, each `[Category("Environmental")]` and skipped unless its env var is
set, so the standard tier never runs them). Machine: a 16 GB macOS arm64 dev machine. Build: Release. Head:
`f48f6695`. The copy: the owner's live config dir (`~/Library/Application Support/DemoViewer.NET/`), minus
`lineup-clips/` (1.0 GB of rendered GIFs, no measurement reads them), `logs/`, `crash.log` and `*.bak`; 79 MB
copied, 382 demos indexed. In the copy's `settings.json`: `Highlights.BackgroundScan`,
`ProcessingQueue.BackgroundProcessingEnabled`, `Situations.BackgroundIndex`, `Grenades.BackgroundIndex` and
`Grenades.RenderLineupClips` all forced to `false`, so the only work at boot is the fixed startup-load list
`App.axaml.cs` runs today, nothing opportunistic layered on top.

**Bugs found and fixed while measuring.** Two passes before this one produced numbers that looked plausible
and were wrong, both recorded here because item 9 is about to point the same harness at a gated-off build
and would hit them again blind.

1. The resident-set wait loop checked `queue.QueuedCount == 0 && queue.RunningCount == 0` as part of
   "settled". With `ProcessingQueue.BackgroundProcessingEnabled` off, any ordinary library demo still owed a
   tier-2 pass sits `Queued` forever (Background-priority `DemoProcessing` jobs never start; that is what
   the setting means), so the loop always ran its full 90s deadline, silently, on backlog that has nothing
   to do with the pack. The pack's own readiness flags (`SituationIndex.IsReady`, `GrenadeIndex.IsReady`,
   `TeamIdentityService.IsLoaded`, and `TeamIdentityService.StartAsync`'s own `QueueJobKind.TeamsCommand`
   job, which the method had not been checking at all) were actually true within a few seconds. Fixed by
   waiting on the specific readiness flags plus `ActiveCount(QueueJobKind.TeamsCommand)` and
   `ActiveCount(QueueJobKind.StoreLoad)`, and by throwing instead of silently continuing past a deadline.
   This is NOT the whole story; see the resident-set entry below, which the fix exposed rather than closed.
2. The indexing-time probe called `Evaluate`/`EvaluateForward` directly on already-indexed demos, bypassing
   `Wants()`. Round Facts, Round Index, Suggested Tags and Highlights each separately check a fingerprint or
   state field against the cache record first and return immediately when it already matches, which it does
   for every demo in an indexed library: the second pass's "full" timing was still measuring mostly Grenade
   Index's walk (the one evaluator with no such check) plus the retained parse itself, not the combined cost
   of the four pack evaluators plus Highlights. Fixed by clearing the three fingerprints and the analysis
   state on each timed demo's cache record immediately before timing it, in BOTH modes
   (`DemoCacheStore.UpdateExisting`), forcing a genuine recompute either way, the scenario "library index
   time" actually needs to answer (what re-indexing a demo costs, not what re-checking a current one costs).
   Library's own gate (membership in a private, un-forceable "pending" dictionary, populated only by its
   own `RescanAsync` reconcile, which nothing calls here) is still not forced in either mode, and that is
   NOT symmetric the way the paragraph above first claimed. In the full arm, `library.Evaluate` returns at
   the membership miss before doing anything, so the retained parse never pays for library's final-score
   entity-tracker replay. In the reduced arm, that replay still runs: it is driven by the forward pass's own
   `ForwardNeeds.FinalState` flag (requested because `library.ForwardFor` always asks for it), not by
   whether `library.EvaluateForward` goes on to use the result, so the `Tap` inside `ForwardPassRunner.Run`
   does the replay regardless. The reduced arm is therefore carrying a real cost (library's own replay) that
   the full arm skips, which UNDERSTATES the full-versus-reduced delta below by some amount. Forcing library
   symmetrically would need reflection into its private membership dictionary (no public per-path force
   exists, unlike the other three) plus reading what `IndexTier2Core`'s fan-out argument does to the other
   evaluators first, to avoid double-counting; not attempted at M0's size. Treat the delta below as a floor,
   not a ceiling: the true cost of turning the four pack evaluators (plus Grenade Index's walk, which the
   full arm alone pays) off is at least what is measured here, and probably somewhat more.

**Resident set after startup** (`ResidentSetAfterStartup`, one process per trial, since a second boot in the
same process carries the first one's JIT and GC committed high-water): boots the real composition root
(`App.BuildServices`) against the copy, waits for the situation index, grenade index and Team Identity to
report ready and Team Identity's own update job to finish (bug 1 above), forces three full
blocking/compacting collections, then reads `Process.WorkingSet64`, `Process.PrivateMemorySize64` and
`GC.GetTotalMemory(true)`. Settling now takes 3 to 4 seconds, not ~190. An "early" snapshot is taken there;
the probe then subscribes to the processing queue's `Changed` event from right after `App.BuildServices`
returns (so a job that starts and finishes between two polls, invisible to a periodic `Items` snapshot, is
still logged) and keeps that subscription through 90 more seconds, then takes a "late" snapshot. Five
trials of each:

| Trial | Early working set | Early GC heap | Late working set | Late GC heap | Committed (same both) |
|---|---|---|---|---|---|
| 1 | 416.4 MB | 369.0 MB | 277.0 MB | 323.8 MB | 227.7 MB |
| 2 | 374.4 MB | 368.7 MB | 275.1 MB | 322.9 MB | 227.6 MB |
| 3 | 415.9 MB | 368.6 MB | 267.5 MB | 322.9 MB | 227.5 MB |
| 4 | 376.7 MB | 369.0 MB | 269.2 MB | 323.8 MB | 227.7 MB |
| 5 | 411.1 MB | 368.7 MB | 359.2 MB | 323.5 MB | 227.6 MB |
| **median** | **411.1 MB** | **368.7 MB** | **275.1 MB** | **323.5 MB** | **227.6 MB** |

Within "early" and within "late" the GC heap is tight (each within 0.4 MB across trials); working set is
noisier in both (trial 5's late figure is an outlier at 359.2 MB against the other four clustered 267 to
277 MB), matching a scar already on record for this machine for a different tool (`bench-run-variance.md`:
AnalysisBench drifts 27 to 31% between sessions). `PrivateMemorySize64` is 0 in every trial on this OS, not
a measurement error; committed bytes are identical between early and late in every trial, to five figures,
so the GC is not returning segments to the OS.

**The ~90s drop is real and not queue work, confirmed two ways now.** The GC heap drops about 45 MB between
the two snapshots in every trial. The `Changed` log (stronger evidence than the periodic `Items` poll this
doc originally relied on, which cannot see a job that starts and finishes between polls) shows exactly what
runs: the situation and grenade index `StoreLoad`s and Team Identity's `TeamsCommand` complete within about
2.6s of boot; a `SectionCompute` titled "Lineup clips: plan" (from `GrenadeIndex`'s `Changed` subscription in
`LineupClipService`) fires once around t=3.1s and is gone the very next event, right at the early snapshot;
the 9 demos `RoundFactsEvaluator` still wants sit `Queued` the whole time (confirming §1's "Round Facts runs
on every library index with no switch at all" in practice, and incidentally answering who: it is Round
Facts, not Library, holding them). After that single lineup-clips blip, the log is SILENT for the full ~90s
to the late snapshot in all five trials: no further `Changed` event at all. A plan-only `SectionCompute` is
far too small and far too quick (one event, not a sustained run) to account for 45 MB.

To find out whether the drop is an app-layer effect or something generic, the isolated probe below (no DI
container, no Avalonia, no processing queue, nothing event-driven) got the same 90s-idle-then-resnapshot
treatment. It drops too, by almost exactly the same amount (see below). That rules out anything specific to
this app's queue, Avalonia's dispatcher, or `App.BuildServices`' wiring: whatever is releasing memory does
it to a bare `DemoCacheStore`/`SituationIndex`/`GrenadeIndex`/`TeamIdentityService` graph sitting untouched
in a console-style test host just as readily as to the full app. The likeliest mechanism, not independently
confirmed (that needs a memory profiler, past M0's size): .NET's shared buffer pools
(`System.Buffers.ArrayPool<T>.Shared`, which gzip decompression and JSON deserialization both rent large
scratch arrays from) trim their buckets on an internal timer when a bucket has gone unused for a while,
independent of any GC.Collect call and independent of anything the app's own code does. Both probes decode
gzipped JSON sidecars for 382 demos right before the drop, which fits. Use the EARLY figure as "Pack on"
above: it is the one taken the same way (immediately after settling, no idle period) as the isolated probe's
own first figure, so the two are the comparable pair. The late figure is reported too because it is a real,
reproducible number and item 9 should capture both: if the ~90s drop is this same runtime-level effect
regardless of the pack, it should show up (and by about the same amount) whether the pack is on or off.

**Pack resident cost, isolated** (`PackResidentCostDirect`, no DI container, no `App.BuildServices`): there
is no seam yet to boot with the startup loads skipped, since `App.axaml.cs` runs them unconditionally and
owns that file this wave (item 0). As a narrower, exact substitute, `SituationIndex`, `GrenadeIndex` and
`TeamIdentityService` are constructed directly over a fresh `DemoCacheStore` on the copy (the same
construction `StratMiningCalibration` already uses), each measured by a `GC.GetTotalMemory(true)` delta
around its own load, immediately (no idle period, matching the full-boot probe's "early"); the probe then
takes the same 90s-idle-then-resnapshot second reading, a bare total only (not split per index):

| Trial | SituationIndex | GrenadeIndex | TeamIdentityService | Total (immediate) | Total (after 90s idle) |
|---|---|---|---|---|---|
| 1 | 86.1 MB | 251.8 MB | 4.8 MB | 342.6 MB | 291.3 MB |
| 2 | 86.1 MB | 251.8 MB | 4.8 MB | 342.6 MB | n/a |
| 3 | 86.1 MB | 251.8 MB | 4.8 MB | 342.6 MB | n/a |
| 4 | 86.1 MB | 251.8 MB | 5.0 MB | 342.8 MB | 291.5 MB |
| **median** | **86.1 MB** | **251.8 MB** | **4.8 MB** | **342.6 MB** | **291.4 MB** |

(Trials 1 to 3 predate the late-idle reading, added after the second review round below; trial 4 is the
first run with it. Two late readings is thin for a median, but the two agree to 0.2 MB.) Grenade Index
dominates: `cache/grenade-lineups.json.gz` is 12 MB gzipped, so the live structure runs well over 20x its
compressed size, worth a look if memory becomes a line item later. Immediate-to-immediate, this total is
properly a subset of the full boot's early figure (342.6 MB versus 368.7 MB). After 90s idle, it is ALSO
properly a subset of the full boot's late figure (291.4 MB versus 323.5 MB): the inversion an earlier pass
of this doc reported (342.6 MB "isolated" versus 315.9 MB "full boot", backwards) was comparing an immediate
reading against a reading that had already idled; comparing like with like, in either timing, resolves it.
It still excludes Team Identity's three sibling stores (Strats, Dossier, Veto History; wired by the DI
factory that builds `TeamIdentityService`, not the service itself) and Tag Facts and the Lineup Clip service
(event-driven / render work, not a bulk load), so treat it as a lower bound on the pack's resident cost, not
the whole of it, and prefer the full-boot delta once item 9 can actually toggle the pack off.

**Library index time** (`IndexingTimePerDemo`, 8 of the copy's demos: a discarded warm-up plus 4 timed under
each mode): one retained parse evaluated by all six registered evaluators (library, highlights, round facts,
round index, suggested tags, grenades, the roster `AppCompositionRootTests` pins), each demo's cache record
cleared of its Round Facts, Round Index and Suggested Tags fingerprints and its Highlights analysis state
before timing (bug 2 above, applied in BOTH modes), against one forward parse evaluated by library and
highlights alone, which is the shape the real coordinator already produces (round index, suggested tags and
grenades have no `ForwardFor`, so their presence is what forces the retained parse). Each of the 8 demos
runs under exactly one mode, never both: an early attempt ran both modes on every demo, back to back, and
whichever mode ran second on a given demo came out 5x to 7x faster purely because the OS page cache was
already warm for that file, nothing to do with the evaluator list, so the two modes never share a demo here.
Demos are read in place from the library, never copied or moved; all 8 are within the `match730_*` cluster
at the library's size median (283 to 284 MB). `df ~/Demos` reports an SMB mount (`192.168.1.7:/mnt/user/Demos`,
96% full, 2.1 TB free), not local disk, which is the likely source of most of the per-demo cold-read
variance below: a network share's read latency varies far more than a local SSD's. Three independent
runs on the same copy (each full pass re-forces and rewrites its demos' round index, round facts and
suggested-tags sidecars, so by run 3 those demos are on their third genuine recompute, not a progressively
staler one):

| Run | Full (6 evaluators) median | Reduced (library+highlights) median | Full's overhead over reduced |
|---|---|---|---|
| 1 | 14.38 s | 9.96 s | 4.41 s (44%) |
| 2 | 14.98 s | 8.45 s | 6.53 s (77%) |
| 3 | 16.81 s | 8.97 s | 7.84 s (87%) |
| **pooled median (12 demos/mode)** | **14.53 s** | **9.26 s** | **5.27 s (57%)** |

Per-demo figures, run 1: full 15.35 s, 14.69 s, 14.07 s, 13.32 s; reduced 10.37 s, 9.56 s, 11.21 s, 9.26 s.
Read the pooled figures as "full costs roughly half again as much wall-clock time per demo as reduced", not
as a precise multiplier: the per-run spread (44 to 87%) is wide, dominated by a few individual demos taking
12 to 15s longer than their siblings in the same run and mode, consistent with ordinary cold-read variance
on this machine rather than anything systematic; more runs would narrow it but M0's budget does not cover
that. The reduced pass still evaluates the `round_facts` ruleset internally (`MergedRulesBuild` has not
split it out yet, item 2's job), it just never writes the result, so part of Round Facts' own compute cost
is already inside this "reduced" baseline; once item 2 lands, the reduced number should drop a little for a
reason this measurement did not isolate, and the true overhead of turning the pack off is likely a bit
higher than the 57% here.

### 12.2 Item 2: the merged pass with and without `round_facts`

Item 2 makes `round_facts` a pack contribution, so with the pack off the ruleset leaves the merged set the
Library and Highlights passes run. This measures that one pass, A/B, on the reference demo
(`003816248937665266002_0544286934.dem`, 172 MB, read in place from `demos/benchmarks`), with
`AnalysisBench --bare --no-golden` against the shipped `rules/` directory (A, pack on) and against a copy of
it without `round_facts.rules.yaml` (B, pack off). Three rounds interleaved A B A B A B per the
bench-variance note; medians of 3. macOS arm64, Release, head `a1f6b2d2`, 1-minute load average 5 at start
(an earlier attempt under a load average of 30 to 38 spread 4.6 to 15.3 s across rounds and was discarded).

| Path | Phase | Pack on (A) | Pack off (B) | B vs A |
|---|---|---|---|---|
| `--retained` | Parse | 792.5 ms | 756.5 ms | noise |
| `--retained` | Build | 166.5 ms | 137.9 ms | -17% (every round: 163 to 172 vs 137 to 138) |
| `--retained` | Eval | 3591.7 ms | 3471.3 ms | -3.4% |
| `--retained` | Total (parse+build+eval) | 4560.7 ms | 4366.0 ms | -4.3% |
| forward (default) | Run (open+build+decode+eval) | 3159.8 ms | 3107.2 ms | -1.7% |

Read it as: dropping `round_facts` saves a steady ~30 ms of graph build per demo and a few percent of
evaluation, both inside the round-to-round spread the bench-variance note warns about, so the eval and run
deltas are directional, not quotable. The build saving is the one figure that held in every round. The
rule-chain event table is identical between A and B apart from the `round_facts` stat nodes (B lacks
`money_reliable`), which is the goldens-do-not-move check at the bench level; the test-level check is
`ForwardPassRealDemoTests` and `RoundFactsRealDemoTests` on the same corpus (9 passed, 2 skipped by design).

### 12.3 Item 9: pack off at startup, and the in-session toggle

Reproduced by `tools/strat-book-baseline/run.sh --state on|off|toggle` (new flags; `--state on` with no other
change is M0's own invocation, still against a pack-on copy). The two new `StratBookPackBaselineTests`
probes, `ResidentSetAfterStartup_PackOff` and `IndexingTimePerDemo_PackOff`, reuse M0's boot and snapshot
code but settle on the gate instead of on the pack's readiness flags, which never go true when it is off; a
third probe, `OnThenOffThenOn_InSession`, flips `Features.Overrides["pack.stratbook"]` through the real
`SettingsService.Write` and `PackSwitch`, on the real `IDemoProcessingQueue` (not the Budget test's fake
one), so awaiting `PackSwitch.Pending` waits for the actual "Strat Book: release memory" and startup-load
items to run, not just for them to be submitted. Machine, build and library: same as M0 (macOS arm64,
Release, 382 demos), head `396495e1` (code-identical to `fa77bad5`, the item 8/10 merge point; only
`docs/strat-room/plan.md` sits between them). Fresh config copies, made the same way M0's was (the live
config dir minus `lineup-clips/`, `logs/`, `crash.log`, `*.bak`, `.DS_Store`; the same five background
flags forced off): one with no `pack.stratbook` override (resolves on, used for the "pack on, rerun" row's
resident-set and index-time trials) and one with the override set to `false` (for the "pack off" row). A
third, also with no override, was made after the toggle probe gained its `off-late` step (below) to rerun
the toggle trials against the fixed code. Every copy and demo-list file was deleted after the run it was
made for; nothing under `.scratch/` is tracked.

**Resident set, pack off at startup**, 5 trials, same early/late shape as M0's probe:

| Trial | Early working set | Early GC heap | Late working set | Late GC heap |
|---|---|---|---|---|
| 1 | 206.3 MB | 29.60 MB | 116.6 MB | 29.74 MB |
| 2 | 204.7 MB | 29.58 MB | 205.7 MB | 29.72 MB |
| 3 | 201.7 MB | 29.60 MB | 132.7 MB | 29.74 MB |
| 4 | 201.5 MB | 29.62 MB | 202.4 MB | 29.75 MB |
| 5 | 203.8 MB | 29.59 MB | 204.6 MB | 29.73 MB |
| **median** | **203.8 MB** | **29.60 MB** | **202.4 MB** | **29.74 MB** |

Committed bytes read 24.84 MB in every trial, early and late alike (the same "GC is not returning segments"
pattern M0 saw, just at a tenth of the size). The GC heap barely moves at all (29.58 to 29.75 MB across all
ten readings): M0's ~45 MB drop does not recur here, which fits M0's own buffer-pool theory, since a pack
that never loads an index never decodes a gzipped sidecar in the first place, so there is nothing for
`ArrayPool` to trim. The working set is a different story: trial 1 and 3 drop by 90 and 69 MB respectively,
trials 2, 4 and 5 barely move. Trial 1's `@M0_QUEUE_OFF_T+Ns` log (the same 15s poll M0's probe writes)
is empty throughout the 90s, so the drop there is not queue work; trials 2 to 5's logs were not inspected,
only their early/late snapshot lines (`run_off_trials.sh` greps those). Working-set noise with no GC-heap
counterpart matches the pre-existing bench-variance scar for this machine and is reported as noise, not a
finding confirmed on every trial.

**Resident set, pack on, rerun at this head**, 5 trials:

| Trial | Early working set | Early GC heap | Late working set | Late GC heap |
|---|---|---|---|---|
| 1 | 417.1 MB | 369.87 MB | 422.2 MB | 374.84 MB |
| 2 | 417.7 MB | 369.90 MB | 330.4 MB | 324.60 MB |
| 3 | 420.5 MB | 369.91 MB | 423.1 MB | 374.86 MB |
| 4 | 417.0 MB | 369.93 MB | 420.6 MB | 374.89 MB |
| 5 | 415.4 MB | 369.90 MB | 353.5 MB | 324.57 MB |
| **median** | **417.1 MB** | **369.90 MB** | **420.6 MB** | **374.84 MB** |

The early GC heap (369.90 MB) matches M0's 368.7 MB closely, the small difference plausibly just the code
that landed between M0 and here (items 1 to 8, 10, 19). The late figure is the surprise: M0 saw the ~45 MB
drop in five of five trials; here it recurs in exactly two (trials 2 and 5, down to 324.6 MB, the same
magnitude M0 measured), while the other three rise slightly instead (to ~374.8 to 374.9 MB). The late
median therefore sits above the early median, the opposite of M0's table. Nothing about the probe changed
between M0 and this rerun (same code path, same copy shape); the likeliest read is that the drop is a
runtime-level buffer-pool trim gated by a timer or an unused-bucket threshold that this 90s window sometimes
catches and sometimes does not, which is consistent with it being independent of the app (M0's own isolated
probe) rather than deterministic. Treat M0's "always drops" framing as itself a small-sample artifact (5 of
5 trials is not enough to rule out exactly this split happening the other way) rather than conclude the
mechanism changed. One fact cuts the other way, though: the toggle probe's `off-late` reading below
trimmed in all 3 of 3 trials, not 2 of 5. If both readings are catching the same timer, something kept on
by the pack while it is running (nothing of the pack is attached in the pure pack-off boot, everything is
attached in the pack-on boot this split happened under) may be what makes the trim land inconsistently
here but reliably once the pack is off, rather than the trim itself being a coin flip.

**Library index time, pack off**, 8 demos (library + highlights only; this is now the real evaluator list
with the pack off, not a probe choosing a mode): 9453.8, 8936.9, 10233.9, 9030.8, 9401.9, 9613.2, 9147.7,
9991.0 ms; median 9427.9 ms. `MergedRulesBuild.EnabledDoc("round_facts")` is null for the whole run,
confirming item 2's split actually holds under the real gate, not just under `AnalysisBench`'s A/B in §12.2.

**Library index time, pack on, rerun**, 4 demos full / 4 reduced (the same split M0 used, pack on
throughout, so the reduced arm here still carries `round_facts`'s own compute cost per M0's original
caveat): full median 13539.2 ms, reduced median 7688.8 ms (76% slower full, inside M0's 44 to 87% spread).

**The pack-passes-add-X% figure, with a real after-state, and a noise caveat.** M0 could only compare
"full" against a "reduced" arm that still ran `round_facts` internally (item 2 had not landed), so its 57%
figure was always going to undercount. Comparing the pack-on rerun's full arm (13539.2 ms) against the
pack-off row's second run (9427.9 ms, measured on a different 8 demos from the same size-median cluster,
not the same ones, see "method deltas" below) gives 43.6% overhead, i.e. the pack's four evaluators plus
Grenade Index's walk cost about 4111 ms per demo of this size on this machine, inside M0's spread. Treat it
as a point estimate, not a tight bound: the pack-off row's two runs over the identical 8 demos landed at
9162.8 ms and 9427.9 ms, 2.9% apart, but the pack-on rerun's reduced arm (still running `round_facts`
internally, just not writing it) came in at 7688.8 ms, 1.47 to 1.74 s FASTER than the pack-off row despite
doing strictly more work. Per §12.2 `round_facts` itself costs only a few percent, nowhere near 1.7 s, so
demo-to-demo and SMB-read noise on this machine is at least as wide as the gap between 43.6%, M0's 57% and
the in-run 44 to 87% spread; the 43.6% figure is a real improvement over M0's (a true pack-off denominator
instead of one still carrying `round_facts`), but it is not more precise than M0's, just less biased.

**On, then off, then on again, in one process**, 3 trials, real queue. The first pass through this probe
(before it had an `off-late` step) measured on-before/off/on-again only and got a confusing result: off
landed at 84.1 MB (54.5 MB above the pack-off-at-startup row's 29.6 MB) and on-again landed at a median of
422.1 MB, 52.2 MB above on-before in two of three trials. Both looked like real pack costs. Adding a 90s
idle wait and a second collect between "off" and the re-enable, the same window `ResidentSetAfterStartup`
uses, resolves both:

| Trial | on-before GC heap | off GC heap (immediate) | off GC heap (after 90s idle) | on-again GC heap |
|---|---|---|---|---|
| 1 | 370.17 MB | 84.41 MB | 31.47 MB | 369.73 MB |
| 2 | 369.88 MB | 84.10 MB | 31.19 MB | 369.32 MB |
| 3 | 369.86 MB | 84.10 MB | 31.21 MB | 369.44 MB |
| **median** | **369.88 MB** | **84.10 MB** | **31.21 MB** | **369.44 MB** |

**The 84.1 MB immediate figure is transient, not retained pack state.** After 90s of idle (no
further gate changes, nothing else running) the same process's heap drops to 31.2 MB, 1.6 MB above the
pack-off-at-startup row's 29.6 MB, close enough to call it the same floor; committed bytes drop with it, to
a median of 24.7 MB, matching the pack-off-at-startup row's 24.8 MB almost exactly. This is the same ~45 to
55 MB scale M0 attributed to a buffer-pool trim on decode, and the timing fits that reading (the release
itself does the real work: `SituationIndex`, `GrenadeIndex` and Team Identity all report not ready/not
loaded the instant the release item completes, before any idle wait), but the probe only shows that
whatever lingers is gone within 90s, not what specifically holds it until then. Decision 3 ("no reclaimed
on restart") holds either way: the heap does go back to where it was, just not within the first few
seconds.

**Re-enabling from a settled state costs about the same as the first enable, not more.** on-again (369.44
MB median) sits 0.44 MB below on-before (369.88 MB), well inside the trial noise the pack-on rerun's own
early/late split already showed (±45 to 50 MB with nothing but idle time passing). The earlier 3-trial
run's "52.2 MB more to re-enable" is consistent with flipping the pack back on before the pool had
trimmed, so the reload's own allocations landed on top of memory that was about to be freed anyway, but
three trials against three cannot cleanly separate that from the same noise, only make it the likelier
reading. `StratBookLiveToggleTests.OnThenOff_ReturnsTheHeap_ToWhereItWasBeforeTheEnable`
(`[Category("Budget")]`, synthetic 160-demo fixture, fake `InlineQueue`, no idle wait between cycles)
reports a sub-1-MB residual after its first off-on-off cycle; one plausible mechanism, not confirmed here,
is a GC-triggered buffer-pool trim keyed to how long a rented buffer has sat unused, in which case three
collects taken immediately would see only recently-rented buffers and free little, while a collect after
90s would see the same buffers past whatever age threshold frees them, and the synthetic fixture rents
little either way so its residual is small regardless of timing.
Working set in the second pass (medians): on-before 415.0 MB, off 383.7 MB, off-late 171.4 MB, on-again
379.7 MB. Off-late's working set is noisy in exactly the way the pack-off-at-startup row's was (this run's
three trials: 218.3, 140.2 and 171.4 MB), the same GC-heap-flat-but-working-set-noisy pattern repeating.

Library index time was not measured inside the toggle probe: the pack-off row above already answers "what
does indexing cost with the pack off", and parsing demos inside the same process that the three resident
readings share would pollute their JIT and GC history for no new number.

**Method deltas from M0, and two things worth naming.** The index-time demo lists for the on-rerun and
off rows are disjoint from EACH OTHER (no filename appears in both), both from the same size-median
`match730_*` cluster M0 used (not M0's exact 8: the library has 381 to 382 entries depending on exactly
when it is counted, and M0's doc did not record filenames, only the selection rule).

1. A "Stale NFS file handle" on the `192.168.1.7` mount failed the on-rerun probe three times running,
   each time on a different file (`...1162819269_392.dem` twice, then `...1246116093_405.dem`), neither
   reproducing a `dd` read taken seconds apart on the same file; several demos already used successfully
   in the pack-off run were also momentarily unreadable by `dd` during the same session, then readable
   again, so this is believed transient, not a change anyone made. The fourth attempt, rebuilding the
   whole list from a fresh `dd`-verified readable set, succeeded and is what the doc reports.
2. That rebuild excluded every filename already in the off-row's list, but not the three earlier (failed)
   versions of its own list, so 4 of its 9 lines came back around, each read 1 to 3 times before this
   final pass: `...0380373016_406` (the discarded warm-up in all three failed attempts, scored "reduced"
   here, 7295.2 ms) and `...1857131197_392` (scored "reduced" three times before, scored "reduced" again
   here, 7676.5 ms) are the two FASTEST of the four reduced times, both faster than the two genuinely
   fresh reduced demos (7701.2, 8949.0 ms), which does look like a real warm-cache effect and means the
   reduced median (7688.8 ms) used above is probably a little low. `...0400436477_410` (scored "full"
   three times before, "full" again here, 15357.5 ms) is the SLOWEST of the four full times, and
   `...0155597527_407` (scored "reduced" once before, "full" here, 13340.7 ms) sits near the middle, so
   the full arm shows no comparable speedup. Net effect: the 76% "full slower than reduced" figure for
   the on-rerun likely overstates the gap a little (a fresh reduced arm would probably land above 7688.8
   ms), which is a second, independent reason the 43.6% pack-passes figure above is a point estimate, not
   a tight bound, on top of the demo-to-demo noise already named there.

The off row's list, by contrast, was read twice on purpose (not a mistake): `IndexingTimePerDemo_PackOff`
ran clean the first time too, giving two independent passes over the identical 9 lines, 9162.8 ms and
9427.9 ms median, 2.9% apart. That agreement is a real same-list repeat-read comparison; the on-rerun's
internal consistency cannot be read the same way; see the "pack-passes-add-X%" note above for why the two
are then still compared across each other for the headline percentage. One run per state used for the
reported numbers, two for the off row (not M0's three throughout): the time budget for this item did not
stretch to three full index-time passes on top of ten resident-set trials, six toggle trials and a code
change mid-session.

## 13. Repository layout (decision 5)

After Phase 0b, inside the app project, with namespaces unchanged:

```
src/App/DemoViewer.NET/Extensions/StratBook/
  StratBookPack.cs                      the IFeaturePack
  Modules/     StratBook, UtilityBook, RoundTagger, SuggestedTags, Situations, Dossier, Teams, Review
  Services/    Strats, RoundIndex, RoundFacts, Tags, Teams, Provenance
  ViewModels/  StratBook, UtilityBook, Situations, Dossier, RoundTagger, SuggestedTags, Teams, Review
  Views/       StratBook, UtilityBook, Situations, Dossier, RoundTagger, SuggestedTags, Teams, Review
  Controls/    PlaceField.axaml(.cs), PlaceFieldModel.cs
  Assets/      Palettes (tag-store.md §3.4), Callouts (strat-model.md §3.7)
src/App/DemoViewer.NET.App.Tests/Extensions/StratBook/
src/App/DemoViewer.NET.UiCapture/Extensions/StratBook/
```

Item 0b's actual move list differs from the draft above it replaced. `Services/Provenance` moved whole:
decision 1 names it explicitly, even though `MainViewModel` and `LibraryTabViewModel` keep forward
references into it, the same shape as the Teams and Round Facts edges the decision also accepts as
pre-existing and leaves for items 2, 4 and 22 to cut. `Services/Review` did not move at all: `ReviewQueue`
and `ReviewQueueMigration` are both core (Highlights is a consumer), so nothing in that folder is
pack-owned; the pack side of Review is `Modules/Review`, `ViewModels/Review` and `Views/Review` (the hub
section and its tab), which did move. The ViewModels and Views lists grew from the draft's partial set to
the full eight sections, matching Modules. `Services/Zones` stays core: core code outside the Strat Book
set consumes it (`RuleWorkbenchTabViewModel`, `Playback2DTabViewModel`, and `App.axaml.cs`'s composition
root), not only the two ring-G viewmodels a narrower grep would suggest.
`ViewModels/Settings/SuggestedTagsTuningViewModel.cs` also stays where it is for now, even though it is
pack-owned by the ring table: `SettingsViewModel`'s constructor takes it, and that seam is item 14's to
cut, not a rename item's.

After Phase 5, the same tree moved up:

```
src/Extensions/StratBook/
  DemoViewer.NET.Extensions.StratBook/            the csproj; references src/App/DemoViewer.NET
  DemoViewer.NET.Extensions.StratBook.Tests/
  extension.json                                   Phase 6 manifest
```

What stays in the app: everything ring G in section 3 (lanes, shape tools, `MapSceneHost`, zones,
`QueueWork`, the processing queue) and the Review Queue (decision 1). Ring S items move with the
extension once their contribution seams exist (items 2, 22). Which `Services/` folders are pack-only
versus shared is settled by item 0b's move list, which the orchestrator reviews before the move.
