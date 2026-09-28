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
| Read and deserialize the file | 41 ms, on the UI thread | 44 ms, on a worker |
| Build the view model | 15 ms | 1 ms |
| Show the section | 24,294 ms | 76 ms (reopen with every section expanded: 87 ms) |
| Text boxes realized | 19,245 | 28 |
| Scroll, one wheel-sized step (300 px) | not measurable (everything was already realized) | 15 ms average, 31 ms worst |
| Scroll, 40 jumps across the whole list | 251 ms | about 2.5 s (60 to 100 ms a jump) |
| Ten-map lineup batch (`Plan` after an index change) | 58,618 ms | 51 ms (108 ms without `Defer`) |

The Before open was measured cold. After is measured with the controls warmed up the way the shell
has them. A cold After open measured 380 to 1,750 ms.

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
  Each title card has Show/Hide.
- `ReviewQueueTabViewModel`: row view models exist only for shown rows. A section with more than 50
  clips starts collapsed. Collapse state is per session and never written to the file. The source
  filter and the search narrow the list, and a search opens the sections it matches. A structural
  change swaps the visible list in with one `Reset`. An in-place edit keeps its row and its
  container, so focus and caret survive. Positions count every clip, shown or not. While the tab is
  deactivated, a queue change only marks the rows stale.
- `ReviewQueue`: the file is read on a worker, and the first member access waits for it. Saves are
  debounced (300 ms) and written on a worker. `Flush()` writes what is pending, and the shutdown
  handler calls it. `Defer()` holds `Changed` and the save until the scope ends. `Add` and `Merge`
  use a hash set. `MergeByKey` finds superseded clips by key. The file format is unchanged.
- `LineupClipService.Plan`: runs inside one `Defer` and merges with `MergeByKey` on `LineupId`. What
  it plans and renders is unchanged.

## Product shape: what a reviewer needs

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
