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
| Dossier, select a team | Findings list was an `ItemsControl` on a `StackPanel`: 1,691 findings, 33,823 visuals realized for "My team" (33 demos), rebuilt as each section's worker result landed | `Views/Dossier/DossierTabView.axaml` (findings `ItemsControl`) | 7,024 to 9,114 ms; 48,338 visuals | 118 ms, then one 423 ms frame; 14,538 visuals | fixed 6798e077 |
| Dossier, any Team Identity change | `Refresh` rebuilt the team rows and re-selected, so `Project` re-ran on every `teams.Changed` and every activation: two record reads per team demo on the UI thread, four workers restarted, the page re-realized | `ViewModels/Dossier/DossierTabViewModel.cs` `Refresh` | 7,647 to 9,300 ms per change | 6 to 11 ms | fixed 6798e077 |
| Utility Book, a grenade walk lands (section open) | `Indexed` was posted to the dispatcher, so `GrenadeIndex.Merge` read the rows sibling on the UI thread; `Changed` then re-ran `Maps`, `LandingPlaces` and `Query` under the index lock on the UI thread | `Modules/UtilityBook/GrenadeIndexEvaluator.cs:251`, `App.axaml.cs` (evaluator registration), `ViewModels/UtilityBook/UtilityBookTabViewModel.cs` `Refresh` | 10 walks: worst stall 1,464 ms, 4,827 ms blocked | worst 7 ms, 0 ms blocked | fixed 70b9c138 |
| Utility Book, a grenade walk lands (section hidden) | Same, whether or not the section was showing | same | worst 563 ms, 3,073 ms blocked | worst 5 ms, 0 ms blocked | fixed 70b9c138 |
| Lineup store save | A query that minted an anchor or a flight gzipped and rewrote all of `grenade-lineups.json.gz` inside `_gate`, on whichever thread queried (the UI for the Utility Book) | `Modules/UtilityBook/GrenadeIndex.cs` `SaveLineupsLocked` | 1,090 to 1,168 ms per save, lock held | snapshot under the lock, one writer thread; `FlushLineups` on shutdown, `Dispose` and the flight-harvest migration | fixed 70b9c138 |
| Situations, search with nothing placed | Result cards on a `WrapPanel`: every hit realized (1,124 cards on de_mirage) | `Views/Situations/ResultCardsView.axaml` | de_ancient 1,660 ms, de_mirage 1,336 ms; 20,953 visuals | 25 ms, 10 ms; 1,197 visuals; render identical | fixed f898709d |
| Library, rescan finds demos gone | `Reconcile` removed entries one `RemoveAt` at a time; each re-ran the tab's filters, player list, provenance and sort | `Modules/Library/DemoLibraryService.cs` `Reconcile` | 100 of 381 gone: 377 ms sync, 448 ms to the next frame | 60 ms, 130 to 145 ms | fixed a9873ea4 |
| Utility Book, map switch | `Refresh` ran the index reads inline | `UtilityBookTabViewModel.cs` | 77 to 101 ms | 2 to 8 ms, worst later frame 25 ms | fixed 70b9c138 |
| Startup | `BuildServices` loads `index.json`, `library.json`, `teams.json` and `team-index.json` (about 1.4 MB) and computes team suggestions on the UI thread before the first frame | `App.axaml.cs` `BuildServices`; `DemoCacheStore`, `DemoLibraryService`, `TeamIdentityService` constructors | 710 to 755 ms build, 515 to 848 ms first show, one 315 to 366 ms stall | unchanged | deferred: moving the loads needs every consumer to tolerate an empty store at first paint, a larger change than this pass |
| Library card grid scroll | Card template realization per row (already virtualized) plus headless raster of the radar images | `Views/Library/LibraryTabView.axaml` card grid | 300 px steps avg 40 to 45 ms, worst 62 to 102 ms; jump worst 102 to 138 ms | unchanged | measured only: raster runs on the compositor thread in the app. Recheck in a Release app before acting |
| Store `Changed` bursts while the Strats section is open | Intermittent waits on `GrenadeIndex._gate` while a pool thread (lineup clip planning) runs `Assign` for a map | `GrenadeIndex.cs` `EnsureAssignedLocked` | per event 4 to 20 ms, worst 60 to 299 ms | save no longer held under the lock; `Assign` still is | deferred: splitting `Assign` from the lock needs a copy-on-write assignment table |
| Library `Changed` | `OnLibraryChanged` re-runs map filters, player list, provenance and filter per change (every 12 tier-2 demos, each rescan phase) | `ViewModels/Library/LibraryTabViewModel.cs:630` | 6 to 17 ms per event | unchanged | within budget at 381 demos |
| Teams tab | `Refresh` is O(teams x demos) per `teams.Changed`; UI commands (Rename, SetUs, Merge, Split, overrides) recompute and write `teams.json` and `team-index.json` synchronously | `ViewModels/Teams/TeamsTabViewModel.cs:314`; `Services/Teams/TeamIdentityService.cs` `Recompute` | select 54 ms, `Changed` 44 to 63 ms, Rename 49 to 52 ms | unchanged | within budget; see proposal 3 |
| Strats editor | `StratStepRow.Load` and `DescribeLineup` group the whole map's lineups once per utility step on every `Session.Changed` | `ViewModels/StratBook/StratEditorViewModel.cs:666`, `StratBookTabViewModel.cs:897-905` | open 47 to 122 ms, `Session.Changed` 4 to 36 ms | unchanged | within budget with the owner's three strats; scales with steps x throws. Next step if it grows: cache options per map, invalidated on `GrenadeIndex.Changed` |
| Tag matrix, rows by demo | Nested `ItemsControl`s on `StackPanel`s, one button per cell | `Views/RoundTagger/TagMatrixTabView.axaml:174-179` | 7 ms (few tags in the owner's store) | unchanged | not measurable at this size; virtualizing a two-axis grid changes its layout |
| Library selection, Match Overview preview | Record read and page rebuild on each selection | `ViewModels/Shell/MainViewModel.cs:1932` | 20 selections: avg 1 ms, worst 8 ms | unchanged | within budget |
| Section opens | Strats, Detected, Situations, Tags, Utility, Review, Dossier, Teams, Match Overview, Stats, Reels, 2D Playback | per section | second open 0 to 95 ms; first open up to 218 ms (2D Playback) | unchanged | within budget |
| Settings | First open builds the whole settings page | `Views/Settings/SettingsView.axaml` | 328 ms first open | unchanged | within budget |

Also found by code reading and not measured here. The harness keeps the queue off and opens no
demo, so it cannot trigger these.

| Area | Cause | Where | Status |
|---|---|---|---|
| Team Identity lock | `SyncWithIndex` and `Recompute` hold `_gate` across `SaveTeams` and `SaveIndex`; UI readers (`GetAssignment`, `Teams`, `DemosOf`, `SidesOf`) wait on it during indexing | `Services/Teams/TeamIdentityService.cs:384-440, 1077-1103` | deferred: the next fix to take. Snapshot under the lock and write outside, as the lineup store now does |
| Library folder add and remove | `PersistFolders` and `Save` write `library.json` and the cache index (about 780 KB) on the UI thread; `PruneStaleCacheRows` deletes one sidecar per stale row there | `Modules/Library/DemoLibraryService.cs:338, 750-808, 1652` | deferred: the 60 ms left in the rescan row above is mostly this |
| Processing queue progress | Two posts per 1% of progress; each snapshots every entry and refreshes the status chip | `Services/DemoProcessing/DemoProcessingQueue.cs:762, 1123-1197, 1326` | deferred: needs a live queue to measure |
| Per-upsert handlers | Highlight scan status copies the index twice per cache change; watched situations re-query every watch per upsert; the Situations status line is O(demos) per upsert | `ViewModels/Highlights/HighlightScanStatusViewModel.cs:182`, `Modules/Situations/WatchedSituationsService.cs:94-102`, `ViewModels/Situations/SituationsTabViewModel.cs:154` | measured together in the cache burst above: 1 to 6 ms per event at 381 demos |
| 2D Playback side panels | Annotation `Flush().GetAwaiter().GetResult()` on deactivation; `TagSession.Detach` writes the tag index synchronously; the suggestion queue reads its proposals file on the UI thread | `Modules/Playback2D/Annotations/AnnotationSessionController.cs:318`, `Services/Tags/TagSession.cs:286`, `Modules/SuggestedTags/SuggestionQueueViewModel.cs:278` | deferred: needs an open demo, which is a heavy parse |
| Detected strats, Add to book | `Promote` reads a record and rewrites the tag store per member on the UI thread | `ViewModels/StratBook/DetectedStratsViewModel.cs:209` | deferred: one click, 5 to 50 members |
| Review leftovers | `PackSummary` probes `File.Exists` once per distinct demo; the queue's `List` getter blocks if touched before its load finishes | `ViewModels/Review/ReviewQueueTabViewModel.cs:212`, `Services/Review/ReviewQueue.cs:87` | deferred: the queue holds 12 entries since the lineup clips moved out |
| GIF clip frames | Each frame is decoded on the UI thread from a timer | `Controls/GifView.cs:163` | deferred: one GIF at a time |

## What changed

- **Utility Book (70b9c138).** The evaluator raises `Indexed` on the queue worker that walked the
  demo, so the merge runs inside that queue item. Only `Changed` is posted. The Utility Book reads the
  index through a background delegate (the pool) and applies the newest result on the UI thread. An
  older read that finishes later is dropped. A hidden section marks itself stale and refreshes when it
  is shown. Lineup saves take a snapshot under the lock and hand it to a single writer thread.
- **Dossier (6798e077).** The findings panel is a `VirtualizingStackPanel`, which virtualizes against
  the page's `ScrollViewer`. `Refresh` keeps the rows and the selection when the team list is
  unchanged. It re-projects only when the selected team, its sides and assignments, or its demos'
  cache stamps differ from what the projection was built from. A hidden tab catches up on activation.
- **Situations (f898709d).** The cards are chunked into rows of as many 192 px slots as fit, the way
  the Library card grid does it, on a `VirtualizingStackPanel`.
- **Library (a9873ea4).** A rescan that drops several demos replaces the list once.

What the user sees is unchanged. Two things now happen later than before:

- A Utility Book filter or map change applies one pool round-trip later.
- Lineup anchors minted while the Utility Book is hidden are minted at the next query, which is the
  lineup clip planner's or the section's own when it is shown. Before, the section minted them after
  every merge, but only once it had been opened in that session.

Checked and clear:

- Nothing outside the Utility Book's own map host calls `FocusLanding`, `SelectThrow` or sets its map,
  so no deep link can land on a map before its asynchronous refresh does.
- No code in the Situations or Dossier views or view models needs a realized container. There is no
  `BringIntoView`, `ContainerFromItem` or `Focus()`, so an off-screen card or finding without a
  container breaks nothing.
- `GrenadeIndex` is the only reader of `grenade-lineups.json.gz`.

Guards: `ResultCardTests.AThousandCards_InTheTabsScrollViewer_RealizeOnlyTheRowsOnScreen`
(Integration) fails on the old `WrapPanel`. The Dossier findings panel has no such guard yet; its
fixture needs a team with hundreds of findings.

## Owner call: the lineup writer

The lineup save now runs on a plain thread, one writer per index, like the Review queue's debounced
save. It is not a processing-queue item. It writes the whole 12 MB file, about a second in Debug.
While a write is pending, newly minted anchor ids can already sit in review entries and clip names.
A crash in that window would re-mint them on the next launch. Shutdown, `Dispose` and the harvest
migration all flush first. Should a library-wide file write like this be a queue item under the
2026-09-27 rule, or is the Review-queue precedent enough for a store's own save?

## Proposals (change what the user sees)

1. **Situations hit cap or paging.** An empty draft still builds 1,124 card view models and reads 1,124
   index rows. That costs 10 to 25 ms now, but it grows with the corpus. Options: show the first N
   with "show all", or group by demo.
2. **Dossier sections as collapsible cards.** The Openings, Post-Plant and Situational blocks realize
   about 13,000 visuals for a big team. Collapsing each by default, as the Review queue does for big
   sections, would halve the first frame after a selection (the 423 ms above).
3. **Teams commands off the UI thread.** Rename, Set as us, Merge and Split recompute every demo and
   write two files before the click returns. At 381 demos that takes 50 ms. Making the commands async
   means the list updates a moment after the click, with a busy state in between.
4. **Startup.** Load the cache index, library cache and teams off the UI thread, and paint the shell
   first with loading states. That saves about 0.7 s before the first frame on this library.
