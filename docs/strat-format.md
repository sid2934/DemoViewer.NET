# The Strat Book files: `.dvstrat.json`, `book.json`, `callouts.json` and `.history.jsonl` (schema v1)

The Strat Book stores a **strat**: a diagram plus the set of tagged rounds in which it was run. A strat
is authored on the round clock ("at 1:15"), names **slots** rather than players, belongs to a **team**
rather than a demo, and carries no demo tick anchor of its own; everything that touches a demo goes
through a Tag Store instance or `StratClock`. This document is for anyone who wants to read or write the
files a book is made of, plus the two text exports (the call sheet and the LAN Print role sheets) that
read them. A committed sample strat and its scripted history live under
[`tests/fixtures/strats/`](../tests/fixtures/strats/), pinned by `StratSchemaSnapshotTests`; a committed
call sheet golden lives beside them, pinned by `StratTextExporterTests`.

**A location note.** `strat-model.md` names this page `docs/strat-room/strat-format.md`. That path is
under `docs/strat-room/`, this repository's untracked design-and-planning tree, and is never committed.
This page is the committed one and lives at `docs/strat-format.md` instead, the same top-level spot as
[`tags-format.md`](tags-format.md), [`team-identity-format.md`](team-identity-format.md) and
[`round-index-format.md`](round-index-format.md).

The format is modelled on the [annotation sidecar](playback2d-v2/annotations-format.md), which settled
identity, clock and forward compatibility the same way.

## Where the files live

| File | Desktop | Browser build |
|---|---|---|
| The rebuildable index | `<app config root>/strats/index.json` | Nowhere. |
| One owner's defaults | `<app config root>/strats/<owner>/book.json` | Nowhere. |
| One owner's words for one map | `<app config root>/strats/<owner>/<map>/callouts.json` | Nowhere. |
| One strat | `<app config root>/strats/<owner>/<map>/<id>.dvstrat.json` | Nowhere. |
| Its append-only diff log | `<app config root>/strats/<owner>/<map>/<id>.history.jsonl` | Nowhere. |

`<owner>` is a folder name, never a display name: `team-<GUID>` for a Team Identity team, or the literal
`me` for the user's own book. Every one of the five kinds above is user truth (never rebuilt from a
demo the way a cache sidecar is), so all of it sits under the config root rather than the cache root,
the same rule the Tag Store's files follow. On the browser build there is no config root, so `StratStore`
keeps every book, strat and history log in a dictionary for the session; the Strat Book tab says so
(`StratBookTabViewModel.BrowserNote`: *"session only: this browser tab forgets strats when it
reloads"*), and `docs/playback2d-v2/wasm-matrix.md` carries the row.

A book folder is self-contained: copying `<owner>/` copies every strat, its history and the team's
callouts for every map, which is the sharing unit (see "Exports" below). Deleting a strat moves both its
files to `<owner>/<map>/.trash/` rather than removing them.

## Top level (`<id>.dvstrat.json`)

```jsonc
{
  "schemaVersion": 1,
  "id": "6f1c0d2e-3a4b-4c5d-8e9f-a0b1c2d3e4f5",
  "owner": { "kind": "team", "teamId": "3f2a9c1e-5b7d-4e2f-8a61-0c9d4b3e2f10" },
  "name": "A exec, double smoke",
  "map": "de_mirage",
  "side": "T",
  "type": "execute",
  "targetSite": "A",
  "economy": "full",
  "tempo": "slow",
  "trigger": { "text": "on call at 1:15", "kind": "time", "atSeconds": 75 },
  "status": "Active",
  "revision": 4,
  "createdUtc": "2026-09-23T14:02:11Z", "modifiedUtc": "2026-09-23T15:40:03Z",
  "origin": { "demoSha256": "...64 hex...", "round": 7, "fileName": "match730_sample.dem" },
  "tags": ["default-break", "vs-aggressive-ct"],
  "notes": "Stairs smoke first, CT smoke second, then jungle molly.",
  "clock": { "kind": "round", "roundSeconds": 115 },
  "canvas": { "fadeInTicks": 8, "fadeOutTicks": 16, "showOpponents": true, "defaultLevelMinZ": null },
  "slots": [ /* five, A to E, in order */ ],
  "steps": [ /* … */ ],
  "branches": [ /* … */ ]
}
```

* **`id`** is a GUID, stable across export and import; it is the only thing a branch or a `strat` tag
  label may reference. **`owner`** is `{ "kind": "team", "teamId": "..." }` or `{ "kind": "me" }`; it
  also names the strat's folder (`FolderName` in code).
* **`map`** is the parser's spelling, lower case: the same string `IModuleContext.MapName` and the asset
  bundles use. **`side`** is `T` or `CT`.
* **`type`** is one of `execute | rush | explode | split | wrap | fake | default | setup | retake |
  anti-eco | save`, the pro tactics-directory vocabulary. It is validated against `side` and
  `targetSite` for the usual pairing (an `execute` wants a site; a `save` wants neither), but a mismatch
  only **warns**: a team may legitimately call a CT play "fake". **`targetSite`** is `A`, `B` or null.
* **`economy`** is one of Round Facts' buy types, lower case, plus `any`: `pistol | eco | semi | force |
  full | unknown | any`. **`tempo`** is `slow | mid | fast`.
* **`trigger`** is the human call that starts the strat: `text` always, `kind` one of `time | contact |
  utility | call` or null, `atSeconds` (round clock remaining) only meaningful for `kind: "time"`.
* **`status`** is one of `Theory | InProgress | Active | Archived`. **`revision`** moves only on a
  commit (see "The history log" below), together with `modifiedUtc`.
* **`origin`** is set only when Create Strat From Round made the strat: the demo it came from, keyed by
  content hash, never by path, plus the round number (`ClipRound.Number`, never
  `m_totalRoundsPlayed + 1`).
* **`clock`**: the STRAT clock, not a demo clock. `kind` is `round` in schema v1 (`plant`, for a
  post-plant strat counted from the bomb going down, is reserved and not defined yet). `roundSeconds` is
  the authored round length, defaulting to 115 (the competitive round length, measured on 43 of 43
  rounds across two Valve matchmaking replays). A step's `atSeconds` is round clock **remaining**, so it counts down along
  `steps[]`; a negative value means "after the round timer stopped for a plant". Mapping a step to a real
  demo tick, in either direction, is `StratClock.TickFor`/`AtSecondsFor`, and always uses
  `CachedRound.StartTickFrameClock` (the round's freeze-end tick, which the frame clock's `GameTick`
  already is) plus Round Facts' `roundTime` fact when the demo round has one, else this block's own
  `roundSeconds`.
* **`canvas`** is Step Authoring's per-strat defaults for the token canvas: fade-in/out in ticks,
  whether opponent tokens show, and a default level. Present from schema v1 even though nothing writes a
  non-default value until Step Authoring does.

### Slots

```jsonc
{ "slot": "A", "role": "entry", "steamId": null }
```

Exactly five entries, letters `A` to `E`, in that order; the validator refuses anything else. `role` is
free text ("entry", "awp", "igl"). `steamId` pins one player to the slot for this strat specifically; when
null, a display resolves the slot through the owner's `book.json` default for the demo's Team Identity
epoch, and falls back to just the letter when neither is set. Names are never stored on a strat or a
book: a display name is looked up at render time and a roster rename never touches either file.

### A step

```jsonc
{
  "id": "9a0b0000-...",
  "atSeconds": 90.0,
  "actor": "B",
  "verb": "throw",
  "from": { "place": "TRamp" },
  "to": { "place": "BombsiteA" },
  "utility": { "kind": "smoke", "lineupId": null, "landing": { "place": "Stairs" } },
  "note": "throw on the 1:30 call, not before",
  "positions": [],
  "strokes": [],
  "holdSeconds": null,
  "interpolation": null
}
```

* **`actor`** is a slot letter, or `all` for every slot at once. **`verb`** is one of the closed list
  `move | hold | throw | plant | defuse | peek | fake | rotate | wait | call | other`; outside it the
  validator **refuses** the document, because Role View and the call sheet phrase a line by verb.
* **`from`**/**`to`** are canonical **places** (a nav place name, e.g. `TRamp`), never a team's callout
  word for one; `""` (what the pawn reports before its place is first networked) is treated as
  unresolved, the same as null.
* **`utility`** is present only on a step that throws something: `kind` is `smoke | molotov | he |
  flash | decoy`; `lineupId` is a GUID naming one Utility Book lineup (Lineup On A Strat Step); `landing`
  names where the grenade should land, as a place and (once Step Authoring writes it) a world point.

  **`lineupId` (Lineup On A Strat Step).** This model does not dereference the id itself, only stores and
  round-trips it; the value and the lookup are the Utility Book's. It is not a row a "Lineup Cards" table
  minted (there is no such table): it is `GrenadeLineup.Id`, computed deterministically from the strat's
  map, the utility `kind`, the throw's landing cell and its rounded origin, so the same throw position
  gets the same id from every process and every reindex with nothing persisted. `GrenadeIndex.DescribeLineup(map,
  id)` resolves it back to a card title, a throw count and the representative throw's console line; a
  step whose id no longer resolves (every throw at that position left the corpus) still opens and still
  shows the raw id rather than losing the reference, the same fallback a branch's unresolved target uses.
  The Strat Editor's step row shows the resolved title as its lineup combo's selection; the call sheet and
  the role sheets (LAN Print) both append `[lineup: <title>]` after a step's phrased line through the same
  `StratStepPhrasing.Phrase` the two surfaces already share.
* **`positions[]`**, **`strokes[]`**, **`holdSeconds`** and **`interpolation`** are Step Authoring's:
  this schema reserves their shape (a `{ slot, x, y, levelMinZ, yawDegrees? }` per token, and the
  `.dvann.json` element shape with the time fields left off, for `strokes[]`) so a document written before
  Step Authoring ships still opens once it does. `interpolation` is `linear | hold`, or null for "Step
  Authoring's default"; `path` is **reserved**, not yet a legal value, and a validator that meets it
  warns and treats it as `linear` rather than refusing the document. `positions[].slot` additionally
  admits the opponent tokens `O1`..`O5`, which a strat's own `slots[]` never does.

`steps[]` is authoring order, which is also Role View's print order, and `atSeconds` must never increase
along it; two steps may share a time.

### A branch

```jsonc
{
  "id": "c2d3e4f5-...",
  "afterStepId": "9a0b0000-...",
  "condition": { "text": "contact at Connector before 1:05", "kind": null },
  "target": { "stratId": "6f1c0d2e-...", "stepId": "..." },
  "note": ""
}
```

After the named step, if the human `condition.text` holds, play continues at `target`: another strat
(`target.stratId`), or, when `target.stratId` equals this document's own `id`, a later step of the *same*
strat, which is how a step chain is written without a second object kind. `target.stepId` is null to mean
"the target strat from its start". `condition.kind` is reserved for a structured trigger and is always
null in schema v1.

## `book.json`

```jsonc
{
  "schemaVersion": 1,
  "owner": { "kind": "team", "teamId": "3f2a9c1e-..." },
  "slotDefaults": { "e1": { "A": "76561198...", "B": "76561198...", "C": "...", "D": "...", "E": "..." } },
  "notes": ""
}
```

One per owner, beside every map's strats. `slotDefaults` maps a Team Identity epoch id to a slot letter
to a SteamID64 string: the owner's usual line-up for that roster era. `me` (no epochs) uses the single
key `"me"`.

## `callouts.json`

```jsonc
{
  "schemaVersion": 1,
  "map": "de_mirage",
  "canonical": { "source": "pawn", "names": ["Apartments", "BackAlley", "BombsiteA", "..."] },
  "aliases": [
    { "alias": "palace", "place": "PalaceInterior", "primary": true },
    { "alias": "A ramp", "place": "TRamp", "primary": true },
    { "alias": "ramp", "place": "TRamp" }
  ]
}
```

One per owner per map, beside that map's strats. Steps always store the canonical **place**; a team's
own word for one is an **alias**, resolved by `CalloutResolver`: fold case, whitespace and hyphens; an
exact canonical name wins over an alias with the same spelling; several aliases may name one place, but
one alias may name only one (the validator refuses a duplicate). `primary` marks the alias a display
prefers; without one, a canonical name is split on its case boundaries (`PalaceInterior` to
"Palace Interior"). `canonical.names` is a snapshot taken the last time the aliases were edited, used
only to warn when a name has since vanished; the live canonical list actually resolved against comes
from the map's baked zones when they are loaded (a custom zone's place list included), else from an
embedded per-map resource seeded from Place Names From The Pawn.

## `<id>.history.jsonl`

```jsonc
{ "revision": 5, "atUtc": "2026-09-23T15:40:03Z", "summary": "molotov moved from 1:22 to 1:16",
  "ops": [ { "op": "replace", "path": "/steps/3/atSeconds", "from": 82, "value": 76 } ] }
```

One line per commit, appended and never rewritten; the strat file is always a plain snapshot of the log
replayed to its own `revision`. `ops` is a subset of RFC 6902 JSON Patch (`add`, `remove`, `replace`,
with an RFC 6901 `path`); `from` is this format's own addition to `remove`/`replace`, carrying the value
the op displaced, so a line can be phrased and inverted without replaying the whole log. Revision 1 is
always a single `add` at `""` (the whole document). `StratHistory.Materialize(log, revision)` rebuilds
the document as of any revision from the log alone; `StratDiffPhrasing` turns an entry's ops into the
words a person reads ("molotov moved from 1:22 to 1:16", "step added: A peeks Connector at 1:05").

A commit happens at an explicit Save, a tab deactivate, a demo swap, shutdown, or 30 seconds of no
further edit; consecutive ops on the same path inside one commit merge into one, so a canvas drag that
fired forty replaces becomes a single history line.

## `index.json`

The rebuildable projection every strat leaves in `strats/index.json`: one small row per strat (`id`,
`owner`, `map`, `side`, `type`, `targetSite`, `status`, `name`, `revision`, `modifiedUtc`, `stepCount`).
Rebuilt from the folders on disk whenever it is missing, corrupt, or behind. It deliberately carries no
evidence count: how many rounds ran a strat is the Tag Store's question, answered by
`StratEvidenceService` from the `strat`, `strat.rev`, `strat.result` and `strat.failure` reserved human
label groups, never stored on the strat itself.

## Exports

Two text shapes are built from a strat and read no file of their own; both are pure over the model in
`Services/Strats/RoleSheet.cs` and `Services/Strats/StratTextExporter.cs`.

### The call sheet (Markdown)

`StratTextExporter.CallSheet` renders one strat as Markdown for Discord or a text diff: a title and
metadata line, one bullet per step with its round-clock time bolded, and a branch as an indented
`if … → …` line under the step it follows:

```markdown
- **1:30** B throws smoke A ramp → A site (Stairs)
- **1:16** C throws molotov A ramp → A site (Jungle)
  - if contact at Connector before 1:05 → A exec, double smoke, step 4
```

A step's line names its actor, its verb, an optional utility kind, its `from` and `to` places (through
the owner's callouts when given, else the canonical name split into words), and, only when it differs
from `to`, the utility's landing place in parentheses. The step's own note is left out of this one-liner,
the same way a history summary leaves prose out; the strat's own `notes` field prints once, at the end.

### The role sheets (HTML, LAN Print)

`RoleSheet.Derive(doc, slot, callouts, roster, lookup)` builds one slot's sheet: the strat's masthead,
every step the slot owns (`actor` equal to the slot or `all`) plus, greyed, another slot's step that
feeds one of the slot's own moves (its `to` place matches the slot's `from` or `to`, at the same or
earlier real time — a **larger** `atSeconds`, since the round clock counts down), the branches that
follow one of the slot's own steps, and the slot's tracked positions as an ordered polyline once Step
Authoring has written any. `RoleSheetHtmlWriter.Html` renders a list of sheets as one self-contained HTML
page, one `<section>` per sheet with a print page break between them and no external reference of any
kind (font, stylesheet, image): the mini-map is drawn as a bare SVG polyline rather than an overlay on
the baked radar art, precisely so the file stays self-contained. `LanPrint.WriteAndOpen` writes that page
to a temp file and hands it to the OS's default `.html` handler, the system browser on every desktop
this app ships to, which is also how a coach prints or emails it (Avalonia has no printing API of its
own). On the browser build the write still lands in the runtime's ephemeral filesystem, but the open
call has no process to launch and fails silently, caught the same way `OpenExternal.OpenUri` already
catches a missing handler.

## Forward compatibility

Every object in `.dvstrat.json`, `book.json` and `callouts.json` carries a `[JsonExtensionData]` bag:
a field written by a newer build round-trips through an older one, load, edit and save. `schemaVersion`
is advisory, read for whatever this build understands rather than refused outright. `.history.jsonl` is
append-only by construction, so forward compatibility there is simply that an older reader stops
understanding new op shapes it meets rather than losing the ones it already wrote. Every vocabulary
field (`type`, `verb`, `economy`, …) is a plain string for the same reason a value this build does not
recognise is a validator warning, or in the closed-vocabulary case of `verb` a refusal, never a load
failure.
