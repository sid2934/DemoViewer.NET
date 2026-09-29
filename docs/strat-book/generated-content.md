# Generated content: one inbox rule

Plan row: UX Consistency, generated content. Machine output lands in its own inbox or card, never in a
curated list, and reviewed, dismissed and hidden mean the same thing in every feature.

The precedent is the Review queue (`review-queue.md`): 4,792 auto lineup clips buried the 7 clips someone
sent on purpose. They moved to the Utility Book card. This note surveys every other producer, proposes
one rule, lists what each producer would change, and ranks the calls the owner has to make.

## Survey

Counts are from a copy of `~/Library/Application Support/DemoViewer.NET/` taken 2026-09-28 (logs, lineup
clips, per-demo cache records and positions files left out). The live directory was only read.

| Producer | What it makes (owner's copy) | Where it lands | Curated list? | Review / accept | Dismiss | Restore | State persisted |
|---|---|---|---|---|---|---|---|
| Suggested Tags | 3,795 proposals over 149 demos; 10 accepted, 0 rejected, so about 3,785 pending | 2D Playback Review mode, Suggested tab, one open demo at a time. The Library index carries a per-demo pending count | No for proposals. Yes once accepted: an accept writes a `source: suggested` tag into the demo's tag document, listed in the Labels tab beside hand tags (10 such tags vs 3 human) | Y accepts, Enter edits then accepts, Ctrl+Y accept-all with a confirm and the current filters | N rejects | None. A rejection is permanent and there is no view of rejected proposals | Yes, `tags/verdicts/<sha>.verdicts.json`, append-only. An unreadable file offers nothing and is never overwritten |
| Strat Mining | 303 patterns (100 executes, 203 setups), all utility-compared; 0 dismissed, 2 promoted | Strats section, Detected toggle with its own list and detail pane | No for patterns. Promotion writes a strat into the book (its purpose) and a `suggested` run tag into every member demo: 6 such tags, shown in the Labels tab | Preview, then Add to book | Dismiss | Restore, behind a "dismissed" checkbox | Yes, `strat-mining.json` under the config root; carried across re-mines by the half-the-rounds rule. Was not refused when unreadable (fixed, below) |
| Team Identity suggestions | Squad, roster change and merge-by-clan-tag suggestions. Copy: 16 teams (15 named from a clan tag, 1 by the user), no duplicate tags and no core lineups, so 0 merge and 0 roster suggestions; at most the one squad suggestion | Teams tab inbox | No | Accept applies it (sets the squad, starts a roster, merges) | Dismiss | None, and no count of dismissed ones | Yes, `teams.json` `dismissedSuggestions`; refused when unreadable. Squad and roster ids embed the player set, so a one-player change makes a new id and a dismissed suggestion returns |
| Team Identity "is this you?" | One account suggestion when me is empty and one account is in more than half of 20+ demos | Teams tab | No | Confirm | None: it shows until confirmed or me is set by hand | n/a | Not stored; recomputed |
| Team Identity teams | 15 of 16 teams were made by the clusterer and named from a clan tag | Teams list | Yes: the list of teams is the curated list, and machine-made teams sit in it | Rename, merge, mark us | Hide | "show hidden" toggle, Show | Yes, `hidden` per team in `teams.json` |
| Dossier findings | Every section's numbers per team, as findings | Dossier editor | Yes by design: the editor is where findings become the dossier | Star | Leave out | "Restore left out" brings back all of them, not one | Yes, dossier notes `hidden` keys; refused when unreadable |
| Lineup clips | 12,899 lineups plan 14,027 clip jobs (from `review-queue.md`) | Utility Book position card | No, since the 2026-09-28 migration (4,792 dropped from Review, backup kept) | Watched on the card | None | n/a | No state; the byte cap evicts |
| Highlights | 21 demos scanned, 4,110 surfaced moments | Highlights dashboard | No; the reel tray is the curated list and the user adds to it | Add to tray, send to Review | None per moment. "Hidden" is a ruleset authoring flag, not a user action | n/a | Scan results in `highlights.json` |
| Bulk sends to Review (Situations, Setup Heatmap, Opening Tendencies, Highlights) | Review holds 7 Dossier clips, 1 card, 0 reviewed | Review queue, one card per send | Yes, but the user asked for each send. "Send N to Review" writes every result at once with no cap | Mark reviewed, per clip or per card | Delete | "show reviewed" | Yes, `review-queue.json` `reviewed`; refused when unreadable |

Also found:

- The plan log's calibration said 25 of 227 patterns compared utility. The owner's detected file now
  has 303, every one utility-compared. The Grenade Index has grown since; the numbers above are what
  the file holds today, not a re-mine.
- A promoted pattern whose strat is later deleted stays "in book" forever, and its member-demo run tags
  stay behind. The copy's two promoted strats both exist.
- A promoted pattern can also be dismissed. The list hides it (dismissed wins) while its summary line
  says "in book" (promoted wins). Harmless, but it is the kind of split the shared state removes.

Four verbs mean "not this one": reject (Suggested Tags), dismiss (Strat Mining, Team Identity), hide
(teams) and leave out (Dossier). Three toggles bring things back: "dismissed", "show hidden", "show
reviewed". Suggested Tags and Team Identity suggestions have none.

## The rule

One state per generated item, `GeneratedState` in `Services/Generated/GeneratedState.cs`:

| State | Meaning | Shown by default | Badge |
|---|---|---|---|
| New | Nothing done yet | Yes | Counted |
| Reviewed | Looked at and kept; nothing written elsewhere | No | No |
| Dismissed | Not wanted. Never offered again, across re-derivations, until restored | No | No |
| Accepted | Became user truth (a tag, a strat, a team change); the item links to what it wrote | No (see decision 2) | No |

1. **Machine output has an inbox of its own.** It never lands in a curated list (the book, Review, the
   Labels tab, the teams list) until the user accepts it, one item or one previewed batch at a time.
2. **The badge counts New only**, everywhere: the Detected toggle, the Suggested tab header, the Teams
   inbox, the Review rail.
3. **One toggle per inbox, "Show settled (n)"**, where n is `GeneratedCounts.Settled`. Settled items
   show greyed with their state as a word.
4. **Every dismissed item has Restore**, one at a time, not only "restore all". An accepted item links
   to what it wrote; undoing that write is the preview-before-commit row's job.
5. **State is user truth.** It lives under the config root, keyed by an id that survives
   re-derivation. When an id can move (patterns, squads, rosters), the state moves to the successor
   holding at least half of its members, Strat Mining's rule. Every state file refuses to load when
   unreadable or newer and is then never overwritten.
6. **Bulk accepts and bulk sends preview first.** This is the preview-before-commit row. It is noted
   here and not built.

`GeneratedFilter` says which settled states an inbox shows; `GeneratedCounts` gives the badge and the
toggle's count.

## Per-producer changes

| Producer | To meet the rule | Needs a decision |
|---|---|---|
| Suggested Tags | Restore for rejected proposals behind the settled toggle; a library-wide inbox (per demo counts exist in the Library index); accepted tags either keep `source: suggested` and show apart in the Labels tab, or become ordinary tags | 3, 4, 6 |
| Strat Mining | Already closest. Switch the checkbox to the shared toggle; stale promotions (strat deleted) go back to New; run tags stop landing in the Labels tab as if hand-made | 1, 5, 6 |
| Team Identity suggestions | Restore plus the settled toggle; carry a dismissal to the successor of a squad or roster suggestion; a Dismiss for the "is this you?" suggestion | 1, 7, 8 |
| Teams list | Clustered teams are the identity layer, not suggestions, so no change beyond the hide vocabulary | 1 |
| Dossier findings | Per-finding Restore beside "Restore left out" | 1 |
| Review queue | Reviewed already matches the rule; decide whether bulk sends above a size ask first | 9 |
| Lineup clips, Highlights | No state today and none needed while they live on their own card and dashboard | 10 |

## Built on this branch (no owner decision)

- `GeneratedState`, `GeneratedFilter`, `GeneratedCounts`, with tests. `DetectedPattern.State` feeds the
  Detected list and its count; what it shows is unchanged.
- `strat-mining.json` is refused when unreadable or at a newer schema, like `teams.json` and the dossier
  notes. Before, a bad file loaded as empty and the next Dismiss saved an empty set over every
  dismissal and promotion. While refused, changes stay in memory and Promote is refused, since a
  promotion the file cannot record would come back and be promoted twice. The file gains an additive
  `SchemaVersion`.
- A Suggested Tags accept whose verdict cannot be written takes its tag back. Before, the proposal
  stayed pending with the tag already written, and accepting it again wrote a second copy.

## Decisions, ranked

1. **One vocabulary and one toggle.** Rename reject, hide and leave out to Dismiss / Restore and use
   one "Show settled (n)" toggle in every inbox. *Recommend yes.* Keep "Hide" only for the teams list,
   which is a curated list and not an inbox.
2. **Does an accepted item stay in its inbox?** Detected keeps promoted patterns visible with "in book";
   the Suggested queue drops accepted proposals. *Recommend: accepted leaves the default view and shows
   under the settled toggle with a link to what it wrote*, so the inbox reads as work left to do.
3. **Restore for rejected Suggested Tags.** Verdicts are append-only today and tuning counts them.
   *Recommend restore as a new `restored` verdict appended over the rejection*, so tuning keeps the
   history and can choose to ignore restored rejections.
4. **A library-wide Suggested Tags inbox.** 3,785 pending proposals are reachable one open demo at a
   time. *Recommend a Suggested section in the Strat Book listing demos by pending count*, opening each
   in Review mode; no cross-demo accept-all until preview-before-commit lands.
5. **A deleted promoted strat.** *Recommend the pattern returns to New and the strat's run tags are
   removed with the strat*, the second half through the preview-before-commit row's undo.
6. **Machine-written labels in the Labels tab.** Accepted suggestions and mining runs both use `source:
   suggested`. *Recommend the Labels tab groups them under "from suggestions" with the detector name*, and
   mining runs never show as hand tags.
7. **Team suggestion dismissals across churn.** A one-player change resurfaces a dismissed squad or
   roster suggestion. *Recommend Strat Mining's successor rule* (the dismissal moves to a suggestion
   sharing at least half its players), keyed by SteamID.
8. **Dismiss for "is this you?".** *Recommend yes*, stored in `teams.json` beside the other dismissals.
9. **Bulk sends to Review.** Situations sends every result with no cap. *Recommend a confirm above 50
   clips* that names the count, the same threshold at which a Review card starts collapsed.
10. **Reviewed beyond Review clips.** *Recommend not yet.* Reviewed only means something where looking is
   the work (clips); for proposals, patterns and suggestions the user decides, and Dismiss or Accept
   covers it.

## Overlap with preview before commit

Accept-all, squad and roster accepts and Add to book all write into curated places in one step. Rule 6
depends on that row; decisions 4 and 5 lean on its undo. Nothing here builds a preview.
