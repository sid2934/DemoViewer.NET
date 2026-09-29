# UI-thread audit

Every Strat Book section, the Library (cards, Demos, Teams), Match Overview's library preview and
Settings, checked for work that blocks the UI thread on a large library. Branch
`feature/strat-book-ui-thread-audit`, 2026-09-28.

## How it was measured

`UiThreadAuditTests` (Integration) builds the real composition root over a temp copy of the folder
`DV_AUDIT_CONFIG` names. Logs and lineup clips are left out. Background processing and idle mode are
switched off in the copy, so nothing is parsed and no `.dem` file is read. It shows `MainView` at
1440x900 and prints:

- per section: the open time (twice; the second open is what a user sees on every switch);
- the longest dispatcher stall in the seconds after;
- a scroll pass and a jump through the whole extent;
- the heaviest lists by realized visuals;
- per store: the UI cost of a burst of 40 `Changed` events, handler by handler;
- section-specific steps: a Utility map switch, ten grenade walks landing (Utility open and hidden), a
  Dossier team selection and Team Identity changes, an empty-draft Situations search, the Teams tab's
  biggest team and a rename, 20 library selections, and a rescan that finds 100 demos gone.

Run it with:

    DV_AUDIT_CONFIG=<copy of ~/Library/Application Support/DemoViewer.NET> \
      dotnet run --project src/App/DemoViewer.NET.App.Tests -c Debug -- \
      --treenode-filter '/*/*/UiThreadAuditTests/*' --output Detailed

The owner's copy holds 381 library demos, 382 cache rows, 16 teams, 3 strats and a 12 MB lineup
store. Numbers are from a Debug build. The headless platform renders on the UI thread, so layout and
Skia raster count toward every stall here. In the app the compositor rasterizes on its own thread, so
these numbers are upper bounds. A "stall" is one pump of the dispatcher: every queued job plus one
layout and render pass.

## Findings

Sorted by cost before the fix. Paths are under `src/App/DemoViewer.NET/`.

| Area | Cause | Where | Before | After | Status |
|---|---|---|---|---|---|
| Dossier, select a team | Findings list was an `ItemsControl` on a `StackPanel`: 1,691 findings, 33,823 visuals realized for "My team" (33 demos), rebuilt as each section's worker result landed | `Views/Dossier/DossierTabView.axaml` (findings `ItemsControl`) | 7,024 to 9,114 ms; 48,338 visuals | 118 ms, then one 423 ms frame; 14,538 visuals. With the map sections closed by default: 125 ms, worst later frame 56 ms | fixed 6798e077, c4f172a9 |
| Dossier, any Team Identity change | `Refresh` rebuilt the team rows and re-selected, so `Project` re-ran on every `teams.Changed` and every activation: two record reads per team demo on the UI thread, four workers restarted, the page re-realized | `ViewModels/Dossier/DossierTabViewModel.cs` `Refresh` | 7,647 to 9,300 ms per change | 6 to 11 ms | fixed 6798e077 |
| Utility Book, a grenade walk lands (section open) | `Indexed` was posted to the dispatcher, so `GrenadeIndex.Merge` read the rows sibling on the UI thread; `Changed` then re-ran `Maps`, `LandingPlaces` and `Query` under the index lock on the UI thread | `Modules/UtilityBook/GrenadeIndexEvaluator.cs:251`, `App.axaml.cs` (evaluator registration), `ViewModels/UtilityBook/UtilityBookTabViewModel.cs` `Refresh` | 10 walks: worst stall 1,464 ms, 4,827 ms blocked | worst 7 ms, 0 ms blocked | fixed 70b9c138 |
| Utility Book, a grenade walk lands (section hidden) | Same, whether or not the section was showing | same | worst 563 ms, 3,073 ms blocked | worst 5 ms, 0 ms blocked | fixed 70b9c138 |
| Lineup store save | A query that minted an anchor or a flight gzipped and rewrote all of `grenade-lineups.json.gz` inside `_gate`, on whichever thread queried (the UI for the Utility Book) | `Modules/UtilityBook/GrenadeIndex.cs` `SaveLineupsLocked` | 1,090 to 1,168 ms per save, lock held | snapshot under the lock; the write is a keyed StoreSave queue item; bounded flush on shutdown, `Dispose` and the flight-harvest migration | fixed 70b9c138, a2b6028e |
| Situations, search with nothing placed | Result cards on a `WrapPanel`: every hit realized (1,124 cards on de_mirage) | `Views/Situations/ResultCardsView.axaml` | de_ancient 1,660 ms, de_mirage 1,336 ms; 20,953 visuals | 25 ms, 10 ms; 1,197 visuals; render identical; then the first 200 cards with Show more | fixed f898709d, e4866cea |
| Library, rescan finds demos gone | `Reconcile` removed entries one `RemoveAt` at a time; each re-ran the tab's filters, player list, provenance and sort | `Modules/Library/DemoLibraryService.cs` `Reconcile` | 100 of 381 gone: 377 ms sync, 448 ms to the next frame | 60 ms, 130 to 145 ms | fixed a9873ea4 |
| Utility Book, map switch | `Refresh` ran the index reads inline | `UtilityBookTabViewModel.cs` | 77 to 101 ms | 2 to 8 ms, worst later frame 25 ms | fixed 70b9c138 |
| Startup | `BuildServices` loads `index.json`, `library.json`, `teams.json` and `team-index.json` (about 1.4 MB) and computes team suggestions on the UI thread before the first frame | `App.axaml.cs` `BuildServices`; `DemoCacheStore`, `DemoLibraryService`, `TeamIdentityService` constructors | 710 to 755 ms build, 515 to 848 ms first show, one 315 to 366 ms stall | 652 ms build, 575 ms first show; teams, the Review queue and both indexes read as queue items | partly fixed f5f21be9; the cache index and library.json stay synchronous (owner call 3) |
| Library card grid scroll | Card template realization per row (already virtualized) plus headless raster of the radar images | `Views/Library/LibraryTabView.axaml` card grid | 300 px steps avg 40 to 45 ms, worst 62 to 102 ms; jump worst 102 to 138 ms | unchanged | measured only: raster runs on the compositor thread in the app. Recheck in a Release app before acting |
| Store `Changed` bursts while the Strats section is open | Intermittent waits on `GrenadeIndex._gate` while a pool thread (lineup clip planning) runs `Assign` for a map | `GrenadeIndex.cs` `EnsureAssignedLocked` | per event 4 to 20 ms, worst 60 to 299 ms | save no longer held under the lock; `Assign` still is | deferred: splitting `Assign` from the lock needs a copy-on-write assignment table |
| Library `Changed` | `OnLibraryChanged` re-runs map filters, player list, provenance and filter per change (every 12 tier-2 demos, each rescan phase) | `ViewModels/Library/LibraryTabViewModel.cs:630` | 6 to 17 ms per event | unchanged | within budget at 381 demos |
| Teams tab | `Refresh` is O(teams x demos) per `teams.Changed`; UI commands (Rename, SetUs, Merge, Split, overrides) recompute and write `teams.json` and `team-index.json` synchronously | `ViewModels/Teams/TeamsTabViewModel.cs:314`; `Services/Teams/TeamIdentityService.cs` `Recompute` | select 54 ms, `Changed` 44 to 63 ms, Rename 49 to 52 ms | commands are UserRequested queue items with a busy line | fixed 5028c985 |
| Strats editor | `StratStepRow.Load` and `DescribeLineup` group the whole map's lineups once per utility step on every `Session.Changed` | `ViewModels/StratBook/StratEditorViewModel.cs:666`, `StratBookTabViewModel.cs:897-905` | open 47 to 122 ms, `Session.Changed` 4 to 36 ms | unchanged | within budget with the owner's three strats; scales with steps x throws. Next step if it grows: cache options per map, invalidated on `GrenadeIndex.Changed` |
| Tag matrix, rows by demo | Nested `ItemsControl`s on `StackPanel`s, one button per cell | `Views/RoundTagger/TagMatrixTabView.axaml:174-179` | 7 ms (few tags in the owner's store) | unchanged | not measurable at this size; virtualizing a two-axis grid changes its layout |
| Library selection, Match Overview preview | Record read and page rebuild on each selection | `ViewModels/Shell/MainViewModel.cs:1932` | 20 selections: avg 1 ms, worst 8 ms | unchanged | within budget |
| Section opens | Strats, Detected, Situations, Tags, Utility, Review, Dossier, Teams, Match Overview, Stats, Reels, 2D Playback | per section | second open 0 to 95 ms; first open up to 218 ms (2D Playback) | unchanged | within budget |
| Settings | First open builds the whole settings page | `Views/Settings/SettingsView.axaml` | 328 ms first open | unchanged | within budget |

Also found by code reading and not measured here. The harness keeps the queue off and opens no
demo, so it cannot trigger these.

| Area | Cause | Where | Status |
|---|---|---|---|
| Team Identity lock | `SyncWithIndex` and `Recompute` hold `_gate` across `SaveTeams` and `SaveIndex`; UI readers (`GetAssignment`, `Teams`, `DemosOf`, `SidesOf`) wait on it during indexing | `Services/Teams/TeamIdentityService.cs` `Recompute` | deferred: the next fix to take (owner call 4) |
| Library folder add and remove | `PersistFolders` and `Save` write `library.json` and the cache index (about 780 KB) on the UI thread; `PruneStaleCacheRows` deletes one sidecar per stale row there | `Modules/Library/DemoLibraryService.cs:338, 750-808, 1652` | deferred: the 60 ms left in the rescan row above is mostly this |
| Processing queue progress | Two posts per 1% of progress; each snapshots every entry and refreshes the status chip | `Services/DemoProcessing/DemoProcessingQueue.cs:762, 1123-1197, 1326` | deferred: needs a live queue to measure |
| Per-upsert handlers | Highlight scan status copies the index twice per cache change; watched situations re-query every watch per upsert; the Situations status line is O(demos) per upsert | `ViewModels/Highlights/HighlightScanStatusViewModel.cs:182`, `Modules/Situations/WatchedSituationsService.cs:94-102`, `ViewModels/Situations/SituationsTabViewModel.cs:154` | measured together in the cache burst above: 1 to 6 ms per event at 381 demos |
| 2D Playback side panels | Annotation `Flush().GetAwaiter().GetResult()` on deactivation; `TagSession.Detach` writes the tag index synchronously; the suggestion queue reads its proposals file on the UI thread | `Modules/Playback2D/Annotations/AnnotationSessionController.cs:318`, `Services/Tags/TagSession.cs:286`, `Modules/SuggestedTags/SuggestionQueueViewModel.cs:278` | deferred: needs an open demo, which is a heavy parse |
| Detected strats, Add to book | `Promote` reads a record and rewrites the tag store per member on the UI thread | `ViewModels/StratBook/DetectedStratsViewModel.cs:209` | deferred: one click, 5 to 50 members |
| Review leftovers | `PackSummary` probes `File.Exists` once per distinct demo; the queue's `List` getter blocks if touched before its load finishes | `ViewModels/Review/ReviewQueueTabViewModel.cs:212`, `Services/Review/ReviewQueue.cs:87` | deferred: the queue holds 12 entries since the lineup clips moved out |
| GIF clip frames | Each frame is decoded on the UI thread from a timer | `Controls/GifView.cs:163` | deferred: one GIF at a time |

## What changed

First pass:

- **Utility Book (70b9c138).** The evaluator raises `Indexed` on the queue worker that walked the
  demo, so the merge runs inside that queue item. Only `Changed` is posted. The section reads the
  index off the UI thread and applies the newest result. A hidden section refreshes when it is shown.
  Lineup saves snapshot under the lock and write outside it.
- **Dossier (6798e077).** The findings panel virtualizes. `Refresh` keeps the rows and the selection
  when the team list is unchanged. It re-projects only when something the selected team's projection
  reads has changed.
- **Situations (f898709d).** The cards are chunked into rows on a `VirtualizingStackPanel`, the way
  the Library card grid is.
- **Library (a9873ea4).** A rescan that drops several demos replaces the list once.

Review fixes:

- **Saves (a2b6028e).** `CoalescedWriter` holds a store's pending snapshot and one scheduled drain. A
  write that throws anything is logged and never stops later saves. `Flush` is bounded and never
  throws. Shutdown flushes after the CSVG and export teardown, with each flush guarded.
- **Dossier key (c0de0d2f).** The projection key carries the grenade index's readiness and whether
  each of the team's demos is loaded, and the tab refreshes on `GrenadeIndex.Changed`.

## The queue model

The owner's rule (2026-09-28) has two parts:

- Background work runs as processing-queue items, so the user sees and controls it.
- Work that a user action triggers goes to the front and stops the running background item.

**Two lanes (8aeafe9e).**

- The heavy lane works as before. It holds parses, forward passes, clip renders, mining, migrations,
  pack export and library scans. It runs one job at a time and holds the heavy-job slot.
- The light lane runs `StoreSave`, `StoreLoad`, `SectionCompute` and `TeamsCommand` items beside
  the heavy lane, with no slot. A save or a section build never waits behind a parse, a reel or an
  export.
- Background light items run one at a time. Items a user is waiting on run beside them, up to four
  at once (62a86efe). A save cannot stop mid-write, and a Dossier team builds four sections, which
  ran together on the pool.
- `QueueWork.UserAction()` marks what a click submits, and what it awaits, as `UserRequested`. It
  covers a Utility filter or map, a Dossier team or activation, a Situations search and overlay, and
  a Tag matrix field. Store-change paths stay `Background`.
- Light items and `LibraryScan` run with the background switch off.
- Pause holds `Background` items in both lanes. `UserRequested` items still start, and still preempt
  (538e91ac). The chip reads "Background paused". Saves are `Background`, so a paused queue holds
  them until Resume or the shutdown flush, which does not depend on the queue.
- Team Identity's replays and the Teams tab's commands share one serial: one runs, the others wait.
- A same-key submit while that key is running queues one rerun; it never starts beside it.

**Preemption.**

- A `UserRequested` item stops the `Background` job or forward pass running in its lane, through the
  item's token.
- The stopped item goes back in the queue with the same id and an unfinished `Completion`, first
  within its priority. It runs right after the user's item and ahead of other background work.
- A job that keeps its own progress resumes where it stopped. A forward pass restarts.
- Light work stops at `QueueWork.ThrowIfStopped()` checkpoints: per demo in the Dossier builds, per
  card in the Situations fill and overlay, between the Utility reads, and before a Tag matrix build.
  Work that cannot stop part-way is submitted as not preemptible and is never marked stopped:
  saves, loads, Team Identity and the lineup plan.
- A job that returns normally despite the stop counts as finished.
- A retained parse cannot be stopped. The parser takes no token, and adding one means touching the
  protected parser. A user item that arrives during a parse runs next.
- Two existing jobs ignore their token and finish before yielding: the library read in
  `SuggestedInboxService`, and the strat-mining job that removes a deleted strat's runs. The other
  jobs throw on cancel.

**Keyed items.** A keyed submit replaces the queued item's work (`ReplacePending`). A section's newest
build is the one that runs, and a store has one pending save item.

**UI cost (2876503a).** One pending UI update covers every queue change before it runs. Over the
whole audit walk, 640 updates averaged 0.03 ms of reconcile and 0.003 ms of `Changed` handlers.

Time from click until the result is applied, over the owner's copy (`TimeToResult_PoolAgainstQueue`).
Each row gives two runs each way. "Pool" is the same code with `QueueWork.Bypass` set, which is how
this work ran before it moved into the queue:

| Click | Pool | Queue |
|---|---|---|
| Dossier: select a team, until all four sections have built | 471 to 611 ms | 457 to 460 ms |
| Utility Book: switch map, until the groups are applied | 66 to 83 ms | 91 to 107 ms |
| The same, while a 1.1 s lineup save holds the light lane | 41 to 62 ms | 44 ms |
| Situations: empty-draft search, until every card is filled | 3.5 s | 3.6 s |

Before 62a86efe, the Dossier row in the queue was 798 to 843 ms, because its four builds ran one
after another. The Situations fill renders a thumbnail for every one of the 1,256 cards, shown or
not; see proposal 1.

Synthetic probe: time from submit to done for a 5 ms section build in a bare queue
(`QueueLatency_AndItsUiCost`):

| Queue state | Time to result |
|---|---|
| Idle | 14 ms (the first item pays for starting the worker) |
| A background clip job running | 6 ms |
| A demo parse running | 6 ms |
| A background light item running, user build | 6 ms (the background item was stopped and requeued) |

A burst of 40 keyed builds finished in 30 ms, and no UI pump took longer than 0.5 ms.

In the app harness, the store bursts now cost 10 to 20 ms per event. Two things add to that:

- The queue chip's count changes with each light item, which forces a layout and render pass.
  Headless, that render runs on the UI thread.
- The Team Identity replays run sooner, so their UI updates land inside the measured window.

The queue's own bookkeeping is the 0.03 ms above.

### Workers converted

These go through `QueueWork` (c37437de, f5f21be9, 5028c985). It falls back to the pool only when
there is no queue: tests, the browser, or a disposed queue.

| Kind | Work | Where |
|---|---|---|
| StoreSave, keyed | grenade lineup store | `GrenadeIndex` via `CoalescedWriter` |
| StoreSave, keyed | Review queue, after its 300 ms debounce timer | `ReviewQueue` |
| StoreSave | tag round facts refresh | `TagFactsRefresher` |
| StoreSave | open demo's highlight harvest | `HighlightScanService.OnOpenDemoEvaluated` |
| StoreSave | old lineup clip sweep | `LineupClipService` |
| StoreLoad, UserRequested | teams.json and the team index; Review queue; situation index; grenade index | `App.BuildServices` `StartupLoad` |
| SectionCompute, keyed | Utility Book refresh | `UtilityBookTabViewModel` |
| SectionCompute, keyed | Dossier openings, post-plant, situational, heatmaps | `DossierTabViewModel` |
| SectionCompute, keyed | Situations fill and overlay | `ResultCardsViewModel` |
| SectionCompute, keyed | Tag matrix rebuild | `TagMatrixTabViewModel` |
| SectionCompute | strat record pane | `StratEvidenceService.ComputeAsync` |
| SectionCompute | Suggested re-read of one demo | `SuggestedInboxService` |
| SectionCompute, keyed | Reels staleness pass | `HighlightScanService.RefreshStaleness` |
| SectionCompute, keyed | lineup clip plan (the 500 ms debounce stays a timer) | `LineupClipService.PlanSoon` |
| SectionCompute, UserRequested | reading a round for Create strat | `CreateStratDialogViewModel` |
| TeamsCommand | Team Identity replays: startup sync, rebuild, per-demo sync | `TeamIdentityService` `run` |
| TeamsCommand, UserRequested | rename, set as us, merge, split, start roster, hide, move a demo, not a team | `TeamsTabViewModel` |
| LibraryScan | folder walk and copy detection; tier-1 header reads | `DemoLibraryService.RescanAsync` |

The workers below are not converted. The first group is the open-demo pipeline: converting it would
make opening a demo a queue item, which is an owner call. The rest are either not real work or already
run under their own job control.

| Work | Where | Why |
|---|---|---|
| Opening a demo: hash and parse, the analysis run, the post-open library score replay | `MainViewModel` (the load core and `FanOutParsed`), `AnalysisViewModel.cs:792` | It holds the interactive heavy slot, which already preempts background parses. Owner call |
| Entity tracking seeks, stats spray and visibility, Replay harvest cards, Rule Workbench evaluation, suggested-tags tuning | `EntityTrackingTabViewModel`, `StatsTabViewModel`, `ReplayTabViewModel`, `RuleWorkbenchTabViewModel`, `SuggestedTagsTuningService` | Work on the open demo; same call as above |
| Visibility engine build, 2D Playback engine load | `VisibilityEngineCache`, `Playback2DTabViewModel.cs:3030` | Built for the open demo's map on first use |
| 2D video export | `ExportJobService` | Has its own job service, running under an export session on the heavy-job gate |
| Tag session SHA-256 of a .dem | `TagSession.cs:198` | Part of attaching the open demo |
| A strat's working-copy save, a tag document's save | `StratSession`, `TagSession` | The idle commit, shutdown and Detach wait for these on the UI thread. As queue items, a paused queue would hang them. A few kilobytes each, under the small-work exemption (e48e7b6b) |
| Reels clip picker record read | `HighlightsTabViewModel.cs:729` | About 300 ms cold, one click. The next one to convert |
| Timers and clocks: playback, live-sync position, perf, idle, progress animation, GIF frames, mining quiet delay, log pump | various | Not work, or must stay real time |

## Owner-approved changes

| Change | Commit | Render | Numbers |
|---|---|---|---|
| C. Stores read at startup as queue items at the front, loading states until ready | f5f21be9 | `ux-teams-reading.png` | Teams (51 ms), the Review queue and both indexes now load off the UI thread. The cache index (29 ms) and library.json (6 ms) stay synchronous |
| D. Collapsible Dossier sections with counts, Expand all and Collapse all | c4f172a9 (with G) | `ux-dossier-sections.png`, `ux-dossier-section-closed.png`, `ui-audit-dossier.png` | "My team": select 125 ms, worst later frame 56 ms. That frame was 423 ms after the first pass, and selecting took 7 to 9 s before it. Expand all 744 ms |
| E. Situations shows 200 cards, then Show more | e4866cea | `ux-situations-show-more.png` | Whole-set actions and the J / K walk are unchanged |
| F. Teams commands as queue items with a busy line | 5028c985 | `ux-teams-busy.png` | Rename, merge and the rest return at once; the panel says what it is applying |
| G. One Dossier section per map, team-wide sections for the rest | c4f172a9 | as D | Dismiss, restore and the settled toggle are tested inside a map section |

D and G share a commit, because the collapsing works on the sections that G introduced.

C keeps two stores synchronous. Every surface reads the cache index as a join while it is being
built, so moving that read would mean giving every one of them an empty state.

`LoadOnce` guards each deferred store:

- Every mutator and every scheduled replay runs the read first.
- A caller that needs the data before the queue item has run does the read inline.
- Nothing is written over a file that was not read (`DeferredStoreLoadTests`).

The inline edit fix is also in c4f172a9. When a finding being edited scrolled out, its row was
recycled and the edit lost focus. The editor that shows the row again now takes the focus back
(`ux-dossier-inline-edit.png`).

Checked and clear:

- Nothing outside the Utility Book's own map host calls `FocusLanding`, `SelectThrow` or sets its map.
- The Situations view needs no realized container. The Dossier's only focus call is the inline-edit
  reclaim above.
- `GrenadeIndex` is the only reader of `grenade-lineups.json.gz`.

Deviations from the request:

- D and G share one commit.
- The cache index and library.json still load synchronously (owner call 3).
- The Dossier has no "reading teams" state while the teams load; only the Teams tab has one. Until
  the read lands, the Dossier's team list is empty.
- `DemoLibraryServiceTests` was not run. It symlinks a real demo into a temp folder, and demos are
  never linked.

## Proposals

1. **Fill only the shown Situations cards.** Send to Review reads each card's tick rate from the
   fill, so a fill limited to the shown page would need to fill the rest before a send. That would
   take "search to filled" from 3.6 s to about 0.6 s on de_ancient.

## Owner calls

1. **Stopping a running parse for a user item.** It needs a cancellation token in the protected parser.
2. **Opening a demo as a queue item.** This covers the whole open pipeline: parse, analysis, and the
   entity and stats computes. See the table of workers not converted.
3. **The cache index and library.json at startup.** Moving them saves 35 ms and needs an empty
   state everywhere they are read.
4. **Team Identity's lock across its file writes.** This is the next fix to take: snapshot under the
   lock and write outside it. It needs a call only if the writes should also be queue items.
