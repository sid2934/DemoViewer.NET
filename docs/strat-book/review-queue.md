# Review queue at thousands of entries

The Strat Book's Review section (`review.queue`) froze the app once the queue passed a few thousand
entries. This note records what was measured, what was fixed on `feature/strat-book-review-queue`,
and the product questions that are still open.

## What is in the owner's queue

Measured on a read-only copy of `~/Library/Application Support/DemoViewer.NET/review-queue.json`,
taken 2026-09-28.

| | |
|---|---|
| File | 2,681,529 bytes, schema 1 |
| Entries | 4,811: 4,799 clips, 12 title cards |
| Clips by source | lineup 4,792, dossier 7 |
| Demos referenced | 372 |
| Questions written | 0 of 4,799 |

Clips per section:

| Section | Clips |
|---|---|
| Lineup clips, de_ancient | 869 |
| Lineup clips, de_nuke | 841 |
| Lineup clips, de_dust2 | 799 |
| Lineup clips, de_mirage | 788 |
| Lineup clips, de_inferno | 638 |
| Lineup clips, de_overpass | 314 |
| Lineup clips, de_train | 184 |
| Lineup clips, de_cache | 161 |
| Lineup clips, de_anubis | 153 |
| Lineup clips, de_vertigo | 44 |
| Dossier · Spirit · de_dust2 CT · opponent plants | 7 |
| Lineup clips, cs_office | 1 |

99.9% of the queue is written by `LineupClipService`, one clip per Grenade Index lineup. Nobody has
reviewed any of it. L3 in `docs/perf/memory-and-storage-v1.md` (one section per plan, never
superseded) is fixed: there is one section per map and one entry per lineup. The queue is now bounded
by the lineup count, which still grows with the corpus.

## Where the time went

`ReviewQueuePerfTests` (Integration) opens the real `ReviewQueueTabView` over a copy of the queue in a
1280x900 headless window. `DV_REVIEW_QUEUE` points it at the copy; without it, it builds a synthetic
5,000-clip queue with the same shape. Timings are from a Debug build.

| Step | Before | After |
|---|---|---|
| Read and deserialize the file | 41 ms, on the UI thread | 44 ms, started on a worker (the rail badge reads the clip count at registration, so startup may still wait for it) |
| Build the view model | 15 ms | 1 ms |
| Show the section | 24,294 ms | 76 ms (reopen with every section expanded: 87 ms) |
| Text boxes realized | 19,245 | 28 |
| Scroll, one wheel-sized step (300 px) | not measurable (everything was already realized) | 15 ms average, 31 ms worst |
| Scroll, 40 jumps across the whole list (thumb drag) | 251 ms | Debug about 2.5 s (60 to 100 ms a jump); Release 1,479 ms (37 ms a jump, 102 ms worst) |
| Ten-map lineup batch (`Plan` after an index change) | 58,618 ms | 51 ms (108 ms without `Defer`) |

The Before open was measured cold. After is measured with the controls warmed up the way the shell
has them. A cold After open measured 380 to 1,750 ms. Wheel scrolling is smooth. A thumb jump
realizes a whole new screen of rows and costs 37 ms on average in Release, which is noticeable but
not a freeze. If it matters, the next step is a template per row kind: each container still carries
both the title-card and the clip layout. A batch that lands while the reviewer reads an expanded
section keeps the top row in place (the harness asserts this).

The Before render was checked by eye and then overwritten by the After run. It showed seven clip
rows per screen under an expanded "Lineup clips, de_mirage" card. The After renders are
`review-queue-owner-open.png`, `-scrolled.png` and `-collapsed.png` under
`$TMPDIR/demoviewer-uitests/`.

Causes, largest first:

1. **No virtualization.** The rows sat in an `ItemsControl` with the default `StackPanel`, inside an
   outer `ScrollViewer` that measured it at infinite height. Every one of the 4,811 rows was realized:
   four text boxes and five buttons each.
2. **Collection-changed storm.** Any structural change ran `Rows.Clear()` and then one `Add` for each
   of 4,811 rows. That meant 4,811 container creations per change, and `Plan` made one change per map.
3. **A save per mutation, on the UI thread.** Each `Merge` serialized the whole queue, 2.7 MB indented,
   and wrote it synchronously. `Plan` did this once per map, and the prune added more writes.
4. **Quadratic duplicate checks.** `Add` and `Merge` scanned the queue for each incoming clip, and
   `Merge` scanned it again to look for a clip to supersede. On a first run (every lineup planned)
   that is about 23 million comparisons.
5. **Pack summary.** `PackSummary` is bound, and every queue change recomputed it with one
   `File.Exists` per clip: 4,799 probes for 372 distinct demos.

No row in the section shows a GIF or a preview, so there was nothing to lazy-load. If previews are
added, they get it for free: only realized rows exist.

## What changed

- `ReviewQueueTabView`: the list is an `ItemsControl` over a `VirtualizingStackPanel` inside its own
  `ScrollViewer`. The filter row has a source picker, a search box, and Expand all / Collapse all.
  Each title card has Show/Hide. A collapsed card hides ▲ and ▼: moving it one entry would move a
  single hidden clip into the neighbouring section. The search box waits 150 ms after typing stops.
- `ReviewQueueTabViewModel`: row view models exist only for shown rows. A section with more than 50
  clips starts collapsed. Collapse state is per session and never written to the file. The source
  filter and the search narrow the list, and a search opens the sections it matches. A structural
  change swaps the visible list in with one `Reset`. An in-place edit keeps its row and its
  container, so focus and caret survive. Positions count every clip, shown or not. While the tab is
  deactivated, a queue change only marks the rows stale; `TabSectionHost` activates the section
  every time it is shown. `PackSummary` is recomputed only when the queue changes, not when a
  filter, a toggle or the search changes.
- `ReviewQueue`: the file is read on a worker, and the first member access waits for it. Saves are
  debounced (300 ms) and written on a worker. `Flush()` writes what is pending, and the shutdown
  handler calls it. `Defer()` holds `Changed` and the save until the scope ends. `Add` and `Merge`
  use a hash set. `MergeByKey` finds superseded clips by key. The file format is unchanged.
- `LineupClipService.Plan`: runs inside one `Defer` and merges with `MergeByKey` on `LineupId`. What
  it plans and renders is unchanged.

## Product shape: what a reviewer needs

The owner decided these on 2026-09-28; what was built is under "Decisions, built" below. The
options are kept as they were proposed.

Built, since none of it needs a decision: sections with counts, collapsed big sections, a source
filter, search across notes, questions, demo names and section titles, and Expand all / Collapse all.

Open questions, with a recommendation for each. **(owner)** marks the ones that need the owner's call.

1. **Should auto-generated lineup clips be in the Review queue at all? (owner)**
   The numbers say no. There are 4,792 of them and none has a question. The Utility Book card already
   lists every throw with its GIF and setpos, which is where a lineup gets looked at. The queue is the
   list a reviewer walks, and a clip nobody chose buries the seven Dossier clips someone did send.
   - **Option A (recommended): take lineup clips out of the queue.** `LineupClipService` keeps its own
     render list, keyed by `LineupId`. Today it keys pending renders by queue entry id, and removing
     an entry cancels its render (`OnQueueChanged`). That coupling goes, and the renders still go
     through `DemoProcessingQueue`. A "send to Review" action on the Lineup Card queues one clip on
     purpose. On first launch, a migration drops the `source: lineup` entries and their "Lineup
     clips, …" cards: the old file is backed up, the new one is verified, then the old one is
     replaced.
   - **Option B: keep them, as now, collapsed per map.** This is what the branch ships. It is usable,
     but the header still reads "4,799 clips" and the badge still shows 4,799.
   - **Option C: keep them out of the badge and the header count only.** Cheap, but it leaves two
     meanings of "queued" in one list.
2. **Reviewed state. (owner)** Mark a clip reviewed, "mark section reviewed", and a
   reviewed/unreviewed filter. This needs a `reviewed` field on `ReviewEntry`. The field is additive:
   older builds keep it through `JsonExtensionData`, so there is no schema bump. The owner needs to
   decide what reviewed does: hide the clip, sink it, or count it, and whether writing an answer to
   the question counts as reviewing. Recommendation: a per-clip flag, hidden by default behind the
   filter, with "mark section reviewed" on the title card.
3. **Map and team filters. (owner, for team)** Entries carry no map or team field. A map filter can
   come from the demo cache by `Sha256`, with no change to the file; it can be built next without a
   decision. A team filter needs the sending surface (Dossier, Situations) to write the team onto the
   clip. That is a new field and a question of which team: the clip's subject or the demo's
   opponent. It follows the owner's team-identity rule, which has no automatic team tracking.
4. **Remove a section with its clips. (owner, tied to 1)** Today ✕ on a title card removes only the
   card. Removing a lineup section would also cancel its pending renders, through the coupling
   described under option A. After option A this is a plain delete and needs no decision.

Recommendation, in order: option A, then the reviewed flag, then the map filter. With lineups out,
the queue returns to tens of deliberate clips, the size it was designed for. The virtualization and
batching above stay as the guard for when it grows again.

## Decisions, built (2026-09-28)

**Rows under background sends.** Queue changes now reach the list as ranged removes and adds
(`BulkObservableCollection.SyncTo`), so a row whose Note or Question is being typed in keeps its
container, its focus and its uncommitted text. A Reset is left only for a reorder the user made.

**A. Lineup clips leave the queue.** Lineup Clip Render plans one clip per lineup and technique. The
lineup's first technique (its most thrown) gets a clip under the old rule, a lineup thrown at least
twice. Every other technique needs two throws of its own. No lineup loses the clip it had: the first
technique's job adopts the old per-lineup pair (the lineup id, its aliases, the legacy hash names) by
renaming it, and a stem evicted under the old name stays evicted under the new one. Clips render
automatically as `LineupClips` items of the processing queue, and the Utility Book's position card
plays the clip for the lineup and technique that was clicked. The card reads the GIF only while it is
open, decodes one frame at a time, and says "clip not rendered yet" or "no clip" when there is none.

The one-time migration runs when the queue loads. It drops a clip only if it sits under a "Lineup
clips, <map>" card (a clip the user moved, or whose card they renamed, is kept) and is a `lineup` clip with
no question, not marked reviewed, and a note in the generated shape (`<title>. setpos x y z; setang p y
r`), then drops "Lineup clips, <map>" cards left empty. The new file is written beside the old one,
read back and compared by id, but only after the old file is copied to `review-queue.lineups.bak`;
without that copy nothing migrates. If the rewrite fails after the copy, the migrated set stays in
memory and the next ordinary save writes it, so the unmigrated set is never saved over a migrated file. A refused file (newer schema, unreadable) is left alone. On the owner's copy: 4,792
dropped, 0 kept, 11 cards dropped. The 7 Dossier clips and their card stay.

An old pair is adopted only when its setpos line is the new job's, since the old per-lineup clip
showed the lineup's representative, which is not always the first technique's. Otherwise the clip
renders again and the old pair is deleted once the new one exists. Planning runs on the pool: an index
change calls `PlanSoon`, which waits 500 ms for more changes and runs one plan at a time. Measured over
a copy of the cache with the owner's clip folder mirrored (GIFs empty, setpos lines copied): 12,899
lineups plan 14,027 jobs, the index change costs its thread 0.08 ms, the first plan takes 2.1 s off
it and adopts 309 pairs, and a steady plan takes 1.8 s off it. The byte cap, already full, decides
how many of the new clips render.

**B. Reviewed.** Each clip has Mark reviewed, and each title card marks its section reviewed (or
unreviewed again once all of it is); with a filter or a search on, only the clips it shows. Reviewed clips are hidden unless "show reviewed" is on.
Cards read "4 of 12 clips unreviewed", and the rail badge counts unreviewed clips. `reviewed` is
written only when true: files without it read as before, and an older build keeps the field through
`Extra`.

**C. Map and team filters.** Map comes from the demo cache by hash, then by path, once per demo;
unknown demos are "no map". `teamId` is written by the Dossier sections (the dossier's team) and by
Situations (the Opponent filter's team, when it names one, read when the results load). Clips without
one are "no team". A team Team Identity no longer has, and that was not merged into another, is
"unknown team". No team is inferred for an older clip.

Renders, all headless from copies: Review before `review-queue-owner-*.png` and after
`review-after-owner-open.png` / `review-after-synthetic-scrolled.png`; Utility Book card before
`utility-card-before.png` and after `utility-card-after.png` (the Smoke into SnipersNest
jump-throw clip, 194 frames, playing).
