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
- **Effort:** about 20 agent-sized items for (a) across four phases, the first of which (real "off") is 4
  items and ships value on its own. Splitting into an assembly (b) is about 8 more items after that.
  Details in section 6.

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

- **Effort:** about 20 agent items (section 6). Phase 1 alone (real "off") is 5, with in-session memory release.
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
  the pack. That is the real value of (b), and the reason (a) should be laid out as if (b) were coming.

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
| Live toggle without restart | UI and jobs yes; resident memory on restart | Same | Unload unreliable; restart |
| Boundary enforced by compiler | No (analyzer or test can approximate) | Yes | Yes |
| WASM | Works | Works | Lost |
| Effort (agent items) | ~20 | ~28 | far more, blocked on CS2DemoKit rule |
| Risk | Low | Medium | High |

---

## 6. Recommended path

Do (a), in this order, and keep every new seam shaped so that (b) is a move rather than a redesign. Each
item below is sized for one agent and one commit set. Phase 1 has value on its own and should land before
anything else.

### Phase 1: "off" means off (5 items)

1. **Umbrella id and pack scope in the catalog.** Add `pack.stratbook` (scope `Pack`, defaults on, or per
   Q2) and set it as the parent of every Strat Book tab and sub-feature. The resolver already walks
   `ParentId` generically; the descriptor contract ("tabs have no parent") and the settings UI grouping are
   what change. Settings page shows one master switch with the sections beneath it.
2. **Gate every evaluator.** Thread `Func<bool> enabled` into `RoundIndexEvaluator`, `GrenadeIndexEvaluator`,
   `RoundFactsEvaluator` (if pack-owned, Q1) and `StratMiningService`, the `SuggestedTagsService` pattern.
   `Wants()` and `PendingPaths()` return nothing when off; opportunistic hooks return early.
3. **Gate startup and shutdown work.** The explicit list at `App.axaml.cs:1373-1402`: skip `SituationIndex`
   and `GrenadeIndex` loads, `LineupClipService`, `TagFactsRefresher`, the grenade migration and, if Teams
   is in the pack, `TeamIdentityService.StartAsync`. Stop `BuildRegistry` resolving `WatchedSituationsService`
   and `ReviewQueue` eagerly. Shutdown currently constructs unbuilt stores: `GetService<TagStore>()` and
   `GetService<ReviewQueue>()` at :314-315 build the singleton if nothing has. Move the flushes into the
   pack lifecycle with a "was built" check.
4. **Turning on and off mid-session.** On `Changed` to on: run the deferred startup loads once and nudge
   the coordinator (`CapacityAvailable`) so pending paths are reconsidered. On `Changed` to off: evaluators
   stop by predicate; queued pack jobs are cancelled by owner tag (the queue has `CancelOwned(ownerTag,
   path)` per path; a cancel-all-for-owner is a small addition); every resident index, cache and store the
   pack built is released in session (owner decision 3: no "reclaimed on restart"). That pulls the memory
   release work of item 15 into this phase. Test: composition root with the pack off resolves no pack store
   and registers no startup loads; turning it off releases what turning it on built (measured, not
   assumed).
5. **Measure first and after.** Before item 1, record RAM and indexing time on a copy of the owner's
   library with the pack on; after item 4, the same with it off and after an on-to-off toggle. The memory
   claim for v1.0.0 rests on these numbers.

### Phase 2: registries instead of hardcoded lists (5 items)

5. **`IFeaturePack` and pack registration.** `StratBookPack.Register(IServiceCollection)` moves the ~50
   registrations out of `App.BuildServices` unchanged. `BuildRegistry` asks each pack for its modules.
6. **Module-declared feature ids.** Descriptors carry `FeatureId`; `_tabFeatureIds` shrinks to the
   built-ins. Packs contribute their `FeatureDescriptor`s to the catalog at startup (catalog becomes
   static core plus pack lists, still immutable after composition).
7. **Evaluator registry.** Ordered by declared `After` ids instead of array position; pending-path union
   built from the registry. `AppCompositionRootTests` pins the resolved order instead of the literal.
8. **Host contributions.** Replace `TabPlacement.StratBook` and the shell's hub synthesis with a pack-
   declared host tab (`HostId = "stratbook.hub"`) and sections that name their host. `MainViewModel` loses
   its `StratBookHubViewModel` field. Library keeps `TabPlacement.Library` or moves to the same mechanism.
9. **Settings pages, queue job kinds, status chips.** Settings page contributions (section list in
   `SettingsView` driven by a collection), job kind descriptors (label, rank, light), the strat export
   chip as a contributed status chip.

### Phase 3: 2D Playback inversions (6 items)

10. **Band and lane menus.** `Playback2DTimelineViewModel` gets a list of band-menu contributors in place of
    `CanCreateStrat` and the single `LaneMenu` slot. Create Strat becomes the first contributor.
11. **Side-pane and right-column panels.** The Create Strat review pane, Tag Palette, Tag Editor, Suggestion
    Queue and Review panel become panel contributions with a placement, order, gate and focus scope. This
    is the largest item; it may split in two (side pane, then right column).
12. **Typed services on the context.** Replace the five `ModuleContext` downcasts and the `App.Services`
    locator in `Playback2DTabViewModel` and `StratCanvasViewModel` with `IModuleContext.GetService<T>()`
    (first-party only) so the tab VM depends on interfaces the pack supplies or does not.
13. **Lane contributions with behaviour.** Tag and proposal lanes register through the pack with their own
    press, drag and label handlers; the tab stops special-casing `TagTrack.TrackId` and
    `ProposalTrack.TrackId`. Review mode becomes a pack-owned toggle the timeline exposes.
14. **Command ids for keybinds.** String-keyed command ids with default chords and scopes, registered by
    packs; the closed `Playback2DAction` enum keeps core actions. Keybind settings list reads the registry.
    Persisted overrides are `"Action=Gesture"` rows keyed by the `Playback2DAction` name
    (`AppSettings.KeybindOverrides`), so command ids that reuse those names keep every override.
15. **Pointer pre-handlers, toolbar items, resident memory release.** Click To Tag as a pointer pre-handler
    contribution; "Rounds like this" as a toolbar item; `SituationIndex`, `GrenadeIndex` and
    `SignatureCache` implement release-on-disable so the live toggle also returns memory.

### Phase 4: data seams (2 items)

16. **Opaque pack payloads in the demo cache record.** `DemoCacheModels` stops importing pack types:
    Round Facts, Round Index, Suggestions and Grenade fields move into a `Packs` dictionary of
    `JsonElement` keyed by pack id, with the fingerprints the coordinator needs promoted to a small
    neutral `PackStamp(Id, Schema, Fingerprint)`. Read old records by mapping the old fields once.
17. **Pack session state.** `SessionPayload.StratBook` becomes a per-pack blob through the existing
    `RestoreState` path; session restore falls back to Library when the active tab id belongs to an
    off pack.

### Phase 5 (optional): separate assembly, option (b) (about 8 items)

18. Extract a `DemoViewer.NET.Core` (or `.Host`) assembly with the services packs consume (`DemoCacheStore`,
    queue, `QueueWork`, `MergedRulesBuild`, `MapSceneHost`, `Scene2DHost`, `ISceneFrameHost`, `FrameClock`,
    `AppPaths`). 19. Move the Strat Book folders into `DemoViewer.NET.StratBook`. 20. Move strat-only types
    out of Playback2D Core and Pipeline. 21. ViewLocator and `avares` resources across assemblies.
    22. UiCapture variants and tests reference the pack. 23. Heads register the pack. 24. A build-time
    check that core does not reference the pack. 25. WASM publish check.

Phases 1 to 4: about 17 items, which is the "about 20" in the summary once the inevitable split of
item 11 and a review pass are counted.

### Proof of concept

Not built on this spike. The cheapest seam to prototype, item 10, does not stand alone: the menu item's
action opens the Create Strat review pane at `Playback2DView.axaml:525-540`, which is bound to a property
on the tab VM. A menu contribution without a pane contribution would still need the tab VM to know about
`CreateStratDialogViewModel`, which proves nothing about the boundary. So the finding the PoC would have
produced is recorded instead: **a 2D Playback contribution is a pair (an entry point plus the surface it
opens), and the contribution API must carry both** (section 7.3, `IPlaybackContribution`). Items 10 and
11 should be one design even if they are two commits.

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
That pairing is the PoC finding in section 6.

`AddLayer` and `AddTool` exist for completeness and for (b). For (a), the token tool and guides layer can
stay core-registered: they are inert without a strat frame host and cost nothing.

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

**Live toggle versus restart.** Recommend live for UI and work in both directions, and accept that memory
is only reclaimed on restart until item 15 lands. Turning on mid-session is the harder direction: startup
loads run from `OnEnabledAsync`, and any 2D Playback tab already open attaches the pack's contributions on
the next demo change or immediately if `Attach` supports a live surface (it should; panes are already
dynamic). If live attach in 2D Playback proves fragile, the fallback is "takes effect on the next demo
open", not "restart".

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
  `RoundFactsIdentity`) or keep the ruleset in the merged set and gate only the evaluator's writes; Library and Highlights forward passes change shape and need a
  bench A/B (`AnalysisBench` with `--retained`, interleaved per the bench-variance note). If it stays core,
  the pack is not fully "no indexing" when off. Q1 decides.
- **Team Identity in the Library.** `Services/Teams`, the Library team filter and the provenance chip are
  all new on this branch (nothing under `Services/Teams` at the merge base), so putting Teams in the pack
  takes away nothing main's users have today. Keeping it core instead means its factory must stop
  constructing pack stores.
- **2D Playback regressions.** Phase 3 rewires the busiest tab VM (3,560 lines). The follow-card render
  test locates cards by list position and will break on any panel reordering. Land Phase 3 one item at a
  time with UiCapture before and after.
- **Concurrent work.** The Strat Book editor is under active development on this branch. Phases 1 and 2
  barely touch its files; Phase 3 items 12 and 14 touch `StratCanvasViewModel` and the keymap and should
  wait for the editor's start-block and clock work to land.
- **Evidence gaps.** No measurement exists of what the pack costs in RAM or index time when on. Before
  Phase 1 is sold as a v1.0.0 memory win, measure: resident set after startup on a large library with the
  pack on versus with the startup loads skipped, and library index time with and without the Round Index
  and Round Facts passes. Also unverified: what session restore does after `TrySelectTab` returns false.
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
