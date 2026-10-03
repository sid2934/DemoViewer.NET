# Strat Book as a plugin: investigation and design

Status: investigated 2026-10-02 on `spike/strat-book-plugin` (off `feature/strat-book` at `8275d5a6`);
owner decisions recorded 2026-10-02 (section 10). Line numbers are against that commit and will drift.

Goal: make the Strat Book features a plugin that a user can fully disable, extend the plugin
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
- **Effort:** 25 items for (a) across five phases (0 to 4), then 8 for the project split
  (Phase 5, now required) and about 6 for the independent release (Phase 6). Phase 0 (contracts and the pack
  skeleton) unblocks parallel work; Phase 1 (real "off", in-session release, first-run prompt) ships value
  on its own. Items in section 6, the build order in section 11.

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

- **Effort:** 24 items (section 6). Phases 0 and 1 (real "off", with in-session memory release) are 10.
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
| Effort (items) | 24 | ~32 | far more, blocked on CS2DemoKit rule |
| Risk | Low | Medium | High |

---

## 6. Recommended path

Do (a), and keep every new seam shaped so that (b) is a move rather than a redesign. Each item below is
sized for one branch and one commit set. Item ids are stable; section 11 gives the build order. Phase 0 exists so
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
    string Id { get; }                       // "net.demoviewer.stratbook"; persisted key
    string FeatureId { get; }                // "pack.stratbook"; the umbrella gate
    IEnumerable<FeatureDescriptor> Features { get; }   // parented to FeatureId
    void Register(IServiceCollection services);        // all DI, unconditional (factories are lazy)
    void Contribute(IPackContributions to, IServiceProvider sp);
}

public interface IPackLifecycle            // optional, resolved from the pack's own registrations
{
    Task OnEnabledAsync(PackStartReason reason, CancellationToken ct);  // startup loads, subscriptions
    void OnDisabled();                       // unsubscribe, cancel owned jobs, release resident indexes
    void OnShutdown(TimeSpan budget);        // flushes
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
    Action<CommandContext> Run,
    Func<CommandContext, bool>? CanRun = null);
```

Core `Playback2DAction` values map to command ids one to one, so persisted keybind overrides keep working.
A command palette, if one is ever built, reads the same registry.

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

**Stale cache while off.** Library keeps indexing new demos without pack passes. The pack fields of those
records are simply absent (or, after Phase 4, the `Packs` entry is missing). Fields of records indexed
before the switch stay as they were.

**Re-enabling.** The pack's evaluators report every demo whose pack fingerprint is missing or stale through
`PendingPaths()`, which is the existing mechanism, so re-enabling backfills automatically. The cost is a
re-index of everything indexed while off, which on a large library is the same order as a first index.
If Round Facts is in the pack, the highlights fingerprint must be split first (section 9), or each toggle also
re-scans every demo's highlights. The settings page should say so ("N demos will be re-indexed in the background") and the backfill should
be visible and pausable in the queue, per the standing rule that all background work goes through it.

**Live toggle.** Live in both directions, and turning off releases the pack's memory in session (decision 3,
item 8). Turning on mid-session is the harder direction: startup loads run from `OnEnabledAsync`, and any
2D Playback tab already open attaches the pack's contributions on the next demo change or immediately if
`Attach` supports a live surface (it should; panes are already dynamic). If live attach in 2D Playback
proves fragile, the fallback is "takes effect on the next demo open", not "restart".

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
  while they are in flight (section 11 lists the hot files).
- **Evidence gaps.** No measurement exists of what the pack costs in RAM or index time when on. M0 and
  item 9 close that; section 12 holds the numbers. Also unverified: what session restore does after
  `TrySelectTab` returns false (item 4 covers it).
- **Scope creep toward (c).** The add-on doc's security and distribution work is a separate decision. None
  of this plan depends on it, and none of it should be pulled forward on the Strat Book's account.

---

## 10. Decisions (2026-10-02)

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

## 11. Build order

The items in section 6 are the units. Each was built on its own branch
(`feature/strat-book-ext-<item>-<slug>`, cut from `feature/strat-book` when the item started), reviewed
before its merge, and followed by the standard tier; the Browser head was built in Release after each
group of merges. Items were merged in the order their dependencies allow.

Two items in flight never shared a hot file. The hot files are `App.axaml.cs`, `MainViewModel.cs`, `LibraryTabViewModel.cs`, `SettingsView.axaml` and `SettingsViewModel.cs`, `FeatureCatalog.cs`, `Playback2DTabViewModel.cs` and `Playback2DView.axaml`, `Playback2DKeymap.cs`, `DemoProcessingQueue.cs`, `MergedRulesBuild.cs`, `DemoCacheModels.cs`, `StratCanvasViewModel.cs`, and `StratBookPack.cs` once it exists. The environmental items (M0, 2,
9) parse demos or run the bench and never overlapped each other.

## 12. Measurements

Filled in by M0 and item 9. Resident set after startup and library index time, on a copy of the owner's
library, with the pack on (M0), off at startup (9), and after an on-to-off toggle in session (9).

| State | Resident set after startup | Library index time | Notes |
|---|---|---|---|
| Pack on (M0, head at the time) | GC heap 315.9 MB (median of 6, tight); working set 293.1 MB (median of 6, noisy: 277 to 370 MB) | full 6-evaluator pass about 20 to 31% slower than library+highlights-only across 3 runs (11.4 to 14.1 s versus 9.3 to 10.8 s) | macOS arm64, Release, head f48f6695, 382-demo copy. `PrivateMemorySize64` reads 0 on this OS; see §12.1. |
| Pack off at startup (9) | | | |
| On, then off in session (9) | | | |

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

**Resident set after startup** (`ResidentSetAfterStartup`, one process per trial, since a second boot in the
same process carries the first one's JIT and GC committed high-water): boots the real composition root
(`App.BuildServices`) against the copy, waits for the situation index, grenade index and Team Identity to
report ready, the processing queue to drain, and T+36 s past boot (the sidecar/grenade migrations are
scheduled at T+30 s), forces three full blocking/compacting collections, then reads
`Process.WorkingSet64`, `Process.PrivateMemorySize64` and `GC.GetTotalMemory(true)`. Six trials across two
separate sessions (three run directly, three more run later through `run.sh` as its own end-to-end check):

| Trial | Working set | Private | GC heap | Committed |
|---|---|---|---|---|
| 1 | 286.5 MB | 0 | 316.2 MB | 177.5 MB |
| 2 | 287.8 MB | 0 | 315.9 MB | 177.4 MB |
| 3 | 298.3 MB | 0 | 315.9 MB | 177.4 MB |
| 4 | 277.4 MB | 0 | 315.9 MB | 177.3 MB |
| 5 | 368.5 MB | 0 | 315.9 MB | 177.4 MB |
| 6 | 370.0 MB | 0 | 315.9 MB | 177.3 MB |
| **median** | **293.1 MB** | **0** | **315.9 MB** | **177.4 MB** |

The GC heap and the committed figure are tight across all six independent process launches, each within
0.3 MB of the others. The working set is not: 277 to 370 MB, a swing of about a third, with no code or cache
difference between the trials to explain it (trials 1 to 3 and 4 to 6 used the identical unmodified copy).
This matches a scar already on record for this machine for a different tool
(`bench-run-variance.md`: AnalysisBench drifts 27 to 31% between sessions), so it reads as this dev machine's
own memory-pressure and paging noise rather than anything the app or the pack does. Treat the GC heap as the
number to compare against in item 9; treat the working set range, not a single median, as the honest answer
for "resident set" until a machine with less background variance is available. `PrivateMemorySize64` is 0 in
every trial: it does not read on this OS (.NET on macOS does not populate it), not a measurement error.
`GC.GetGCMemoryInfo().TotalCommittedBytes` is reported alongside as a second stable figure, though note it
reads LOWER than `GC.GetTotalMemory` in every trial, the reverse of what the two normally imply (committed
should upper-bound live bytes); not chased further at M0 size, flagged for item 9 if it recurs there.

**Pack resident cost, isolated** (`PackResidentCostDirect`, no DI container, no `App.BuildServices`): there is
no seam yet to boot with the startup loads skipped, since `App.axaml.cs` runs them unconditionally and owns
that file this wave (item 0). As a narrower, exact substitute, `SituationIndex`, `GrenadeIndex` and
`TeamIdentityService` are constructed directly over a fresh `DemoCacheStore` on the copy (the same
construction `StratMiningCalibration` already uses), each measured by a `GC.GetTotalMemory(true)` delta
around its own load:

| Trial | SituationIndex | GrenadeIndex | TeamIdentityService | Total |
|---|---|---|---|---|
| 1 | 86.1 MB | 251.8 MB | 4.8 MB | 342.6 MB |
| 2 | 86.1 MB | 251.8 MB | 4.7 MB | 342.6 MB |
| 3 | 86.1 MB | 251.8 MB | 4.8 MB | 342.6 MB |
| **median** | **86.1 MB** | **251.8 MB** | **4.8 MB** | **342.6 MB** |

Grenade Index dominates: `cache/grenade-lineups.json.gz` is 12 MB gzipped, so the live structure runs well
over 20x its compressed size, worth a look if memory becomes a line item later. This total (342.6 MB) is
narrower than the full resident set above, not a subset of it: it excludes Team Identity's three sibling
stores (Strats, Dossier, Veto History; wired by the DI factory that builds `TeamIdentityService`, not the
service itself) and Tag Facts and the Lineup Clip service (event-driven / render work, not a bulk load). It
also, oddly, comes out HIGHER than the whole process's GC heap in the boot above (342.6 MB versus 315.9 MB)
even though it is a strict subset of what that boot constructs; most likely a GC-compaction difference
between a process that has run for two-plus minutes with ordinary background activity and one that runs for
a few seconds and forces its collections immediately, but this was not run down further at M0 size. Item 9
should treat the isolated number as a lower bound, not ground truth, and prefer the full-boot delta once the
pack can actually be turned off.

**Library index time** (`IndexingTimePerDemo`, 8 of the copy's demos: a discarded warm-up plus 4 timed under
each mode): one retained parse evaluated by all six registered evaluators (library, highlights, round facts,
round index, suggested tags, grenades, the roster `AppCompositionRootTests` pins) against one forward parse
evaluated by library and highlights alone, which is the shape the real coordinator already produces (round
index, suggested tags and grenades have no `ForwardFor`, so their presence is what forces the retained
parse). Each of the 8 demos runs under exactly one mode, never both: a first attempt ran both modes on every
demo, back to back, and whichever mode ran second on a given demo came out 5x to 7x faster purely because the
OS page cache was already warm for that file, nothing to do with the evaluator list, so the two modes never
share a demo here. Demos are read in place from the library, never copied or moved; all 8 are within the
`match730_*` cluster at the library's size median (283 to 284 MB). Three independent runs (the third run
through `run.sh`'s own end-to-end check, on the same demo list, by then its third pass over the same eight
files):

| Run | Full (6 evaluators) median | Reduced (library+highlights) median | Full's overhead over reduced |
|---|---|---|---|
| 1 | 11.51 s | 9.33 s | 2.18 s (23%) |
| 2 | 11.44 s | 9.53 s | 1.91 s (20%) |
| 3 | 14.12 s | 10.82 s | 3.30 s (31%) |

Per-demo figures, run 2: full 10.83 s, 11.30 s, 11.58 s, 12.63 s; reduced 9.67 s, 9.38 s, 11.77 s, 6.92 s.
Run 3 is slower in BOTH modes, not just the full one (reduced alone moved from about 9.4 s to 10.8 s, and
reduced never touches round index, suggested tags or grenades), so the extra time is not the evaluators
accumulating cost across repeated passes over the same files; it lines up with the same per-machine variance
noted for the resident-set trials above, not with anything either mode is doing. Read the overhead as "full
costs roughly a fifth to a third more wall-clock time per demo than reduced", not as a single precise number.
The gap is smaller than the resident-memory picture suggests the pack costs: most of each pass is the disk
read and the header/string-table/game-event decode both modes pay, and the extra entity/schema decode for
the final score plus the four extra evaluators' own compute is a real but secondary addition on top of that,
not a multiple of it.

## 13. Repository layout (decision 5)

After Phase 0b, inside the app project, with namespaces unchanged:

```
src/App/DemoViewer.NET/Extensions/StratBook/
  StratBookPack.cs                      the IFeaturePack
  Modules/   StratBook, UtilityBook, RoundTagger, SuggestedTags, Situations, Dossier, Teams, Review
  Services/  Strats, RoundIndex, RoundFacts, Tags, Teams, Review (pack side), Zones (if pack-only)
  ViewModels/ StratBook, UtilityBook, Situations
  Views/     StratBook, UtilityBook
  Controls/  PlaceField and the other pack-only controls
  Assets/    palettes, callouts
src/App/DemoViewer.NET.App.Tests/Extensions/StratBook/
src/App/DemoViewer.NET.UiCapture/Extensions/StratBook/
```

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
versus shared is settled by item 0b's move list, reviewed before the move.
