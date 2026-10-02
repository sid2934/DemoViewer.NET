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
* **`origin`** is set when Create Strat From Round made the strat, or when a mined pattern was added to
  the book (then it names the pattern's most typical round): the demo it came from, keyed by content hash,
  never by path, plus the round number (`ClipRound.Number`, never `m_totalRoundsPlayed + 1`).
* **A mined strat** (Strat Mining's Detected inbox) carries the tag `mined` and an `extra.mined` object:
  `Key` (the pattern's identity, stable across re-mines), `Support` (rounds), `Spread` (the largest
  distance between two of them), `UtilityCompared` (false when some of its demos had no grenade rows) and
  `Members` (each round's `Sha256`, `FileName`, `Round` and `Won`). Each member round with a hash also gets
  a tag instance with source `suggested` and the label `strat: <id>`, so it counts as a run.
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
  `move | push | rotate | hold | peek | lurk | throw | fake | plant | defuse | wait | call | other`; outside
  it the validator **refuses** the document, because Role View and the call sheet phrase a line by verb.
  `push` (an aggressive advance onto a place) and `lurk` came later than the rest: a build older than them
  refuses a strat that uses either.
* **`from`**/**`to`** are **locations** (see "Locations" below): a canonical **place** (a nav place name,
  e.g. `TRamp`), never a team's callout word for one, and optionally the world point it was picked at;
  `""` (what the pawn reports before its place is first networked) is treated as unresolved, the same as
  null.
* **`via`** and **`viaPoints`** (optional) say where a travel goes on its way to `to`; see "Via" below.
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

  A stored id resolves through the lineup's alias ids (`GrenadeLineup.AliasIds`) everywhere it is read,
  so an id minted by an older grouping keeps naming the lineup that absorbed it.

  **`technique`** (optional) names which way the lineup is thrown: a `GrenadeLineups.TechniqueKey`,
  `<stand|run>-<throw|jump>-<left|right|both>`, e.g. `stand-jump-left`. Missing, or a key the lineup has no
  throws for, means the lineup's most thrown technique. A key outside those twelve is a validator warning,
  never a refusal.

  **Throw origins on the canvas.** A `throw` step whose `lineupId` resolves and whose `actor` is one slot
  puts that slot's token at the technique's mean release point at the step's `atSeconds`. The projection
  does this; nothing is written to `positions[]`, and the lineup origin wins over an authored position for
  that slot on that step. The canvas, the preview and an export all sample the same projection. A step
  whose actor is `all` names no one to stand there, so it is not projected and the validator warns.
* **`positions[]`**, **`strokes[]`**, **`holdSeconds`** and **`interpolation`** are Step Authoring's:
  this schema reserves their shape (a `{ slot, x, y, levelMinZ, yawDegrees?, carried? }` per token, and the
  `.dvann.json` element shape with the time fields left off, for `strokes[]`) so a document written before
  Step Authoring ships still opens once it does. `interpolation` is `linear | hold`, or null for "Step
  Authoring's default"; `path` is **reserved**, not yet a legal value, and a validator that meets it
  warns and treats it as `linear` rather than refusing the document. `positions[].slot` additionally
  admits the opponent tokens `O1`..`O5`, which a strat's own `slots[]` never does.

  **A new blank strat** (the tab's New Strat, not one created from a round or mined) is written with one
  round-start step: `atSeconds` equal to `clock.roundSeconds`, actor `all`, verb `hold`, and a position for
  each of `A`..`E` in its own side's spawn and `O1`..`O5` in the other side's. The spawn is the team's
  largest buy zone in the map's baked `zones.json`; the five tokens take five of its nav areas, spread
  apart, at the level of the area they stand on. A map with no zones gets the strat without the step.

### Locations

```jsonc
{ "place": "BombsiteA", "x": -300.5, "y": -2100, "levelMinZ": -256 }   // picked inside a place
{ "x": 1234.4, "y": -560.6, "levelMinZ": -256 }                         // picked outside every place
{ "place": "TRamp" }                                                     // typed, or written before points
```

`from`, `to`, a line's `to` and `utility.landing` share one shape. `x`, `y` and `levelMinZ` are optional
and written only when set, so a file without points loads and saves byte for byte as before. `levelMinZ`
follows the rule for `positions[].levelMinZ`: the level's quantized lower Z, never a floor index.

* **A map click** stores the place under the point and the point, or the point alone when it is in no
  place. With no zones for the map (none baked, or the read failed) there is no answer about the place, so
  the click sets the point and keeps whatever place was stored.
* **Typing a coordinate** (`(1234, -560)`, or the two numbers with a comma or spaces) sets a point without a place.
  It keeps the stored point's `levelMinZ`, else takes the strat's `canvas.defaultLevelMinZ`, else has none. Numbers
  alone are never stored as a place.
* **Typing a place** keeps the stored point only when it names the same place; any other place drops it,
  so a token never faces a point under another place's name. A landing is the exception it always was:
  typing "lands at" writes only `landing/place`, so a captured landing point survives; clearing it drops the place
  first and a second clear removes the point.
* **Printing.** Every reader (the call sheet, the role sheets and LAN print, the history, the Detected
  preview, the step track and the canvas's status line) prints the place's callout when there is a place.
  A point alone prints as its world coordinate rounded to whole units, `(1234, -561)`. Not "near
  <place>": the exports are pure over the model and the owner's callouts and run with or without the map's
  zones in memory, and a nearest-place name would change once they load and could name a place across a
  wall. `StratLocations.Text` is the one formatter.
* **The projection** uses the point when there is one and the place's centre otherwise
  (`StratSceneProjection.Where`), for facing and for motion: a `to` (the step's or a line's) and a lurk's first
  area move the token there (see "Motion on the canvas").
* **The validator** accepts a point without a place. A `move` (or a move line) warns when its `to` has
  neither. An unknown place still warns; a point is never checked against the zones.

`tests/fixtures/strats/schema-v1.locations.dvstrat.json` pins the shapes (a place with a point, point-only
`from`, `to` and landing, a watched point), through `StratLocationFieldTests`.

### Lines: several players in one step (`assignments`)

```jsonc
{
  "atSeconds": 65.0, "actor": "all", "verb": "push", "from": { "place": "TRamp" },
  "assignments": [
    { "slot": "B", "to": { "place": "PalaceInterior" }, "watch": { "places": ["BombsiteA", "CTSpawn"] } },
    { "slot": "C", "to": { "place": "Connector" }, "watch": { "places": ["Stairs"] } },
    { "slot": "D", "to": { "place": "Stairs" }, "watch": { "places": ["TRamp"], "yawDegrees": 135 } }
  ],
  "positions": [ /* … */ ]
}
```

A step keeps one time and one verb and may send several slots to different places. `assignments` is
optional and is not written when absent, so a file without it loads and saves byte for byte as before.

* **A line** is `slot` (`A` to `E`), `to` (a place, the same shape as the step's) and `watch`. A slot has
  at most one line per step; an unknown or repeated slot is refused.
* **`watch`** is `places` (canonical place names, never callouts, first one first; written only when there
  is one, so a watch kept for its angle or points has no `places`, and a missing one reads as none), an optional
  `points` (watched map points outside every place, `{ x, y, levelMinZ }` each, written only when there is
  one) and an optional `yawDegrees` (world yaw, 0 = +X and 90 = +Y, as `positions[].yawDegrees`) that
  overrides facing. The watched entries read places first, then points, and the token faces the first
  entry. A map pick on the watching field adds the place under the click, or the point when the click is in
  no place, and a place already watched is not added twice.

  `points` is a sibling of `places` rather than a list that mixes names and objects, because an older build
  must still load a newer file: its `places` is a list of strings and would refuse an object in it, while an
  unknown `points` lands in its extension data and round-trips. The cost is that a point always reads after
  the places; to face a point first, clear the places or drag the cone.
* **A line's position** is the step's `positions[]` entry for that slot. Lines add no position of their own.
* **`from`, `utility`, `lurk` and `note`** stay on the step and are shared by its lines.
* **Which verbs watch** is the editor's table (`StratStepFields`): push, hold, peek, fake and lurk do; move and
  rotate do not, since a moving player watches their path and the facing that matters comes with the hold or
  push that follows; throw, plant, defuse, wait and call do not. A file may still hold a watch on any verb and it
  is read the same way. The editor's verb change clears a watch the new verb does not use, in the same entry.

The rule that ties this to `actor` and `to`:

* **No `assignments`, or an empty list:** the step reads exactly as it always has. A single-slot actor is
  one implicit line with the step's `to` and no watch; `all` has no lines and means every slot.
* **One or more lines:** the lines alone say who takes part and where each goes. `actor` is written as the
  line's slot when there is one line and `all` when there are more, so a build that predates lines still
  reads something true; this build does not read it on such a step, and the validator warns at
  `/steps/i/actor` when it disagrees. The step's own `to` is not used and is not written; the validator warns
  at `/steps/i/to` when one is present.
* **Watching lives only on a line.** A single-slot step that gets a watch becomes a one-line step: `actor`
  stays the slot and its `to` moves into the line. An older build then shows that step's actor and verb
  without the place. A step with no watch and one player keeps the plain shape.

Create Strat From Round and Strat Mining still write one actor per step; lines come from the step
templates and the editor.

**Several players doing one thing** needs no shape of its own. The editor's Who picks several players and writes a
line for each (`StratLinePatches.SetWho`, one entry): a new player copies the place and watch the lines share, if
they all agree, and all five bare lines to one place fold back to `all` as below. Lines that agree apart from
their slot are what the editor shows as one "who" with one place and one watching; nothing in the file marks it.

**A line belongs to its player.** A change of who writes a `remove` for each player dropped and an `add` at the
end for each player added; the other lines keep their index and their bytes, unknown fields included, so no
edit moves one player's `to` or `watch` onto another. Only removing a player drops their line. A line's slot
change moves the whole line to the new slot, and a slot another line already holds swaps the two: both are
`replace` ops on `slot`, and undo restores the exact bytes. The player's position on that step goes with the line:
the step's `positions[]` entries for the old and the new slot swap slots (a `replace` on each entry's `slot`, so
`observed`, yaw and unknown fields go with it), and their marked carried entries are dropped, since a carried copy
says where a token stood rather than where its player was put. Other slots, opponents included, and other steps are
not touched. A slot change that leaves the lines as they were writes nothing, positions included, so wheeling the
slot combo through slots and back leaves no entry.

`StratStepLines` is the one reader of both shapes (`Of`, `Involves`, `ToFor`, `ActorOf`). Every consumer
goes through it except the Create Strat From Round preview, which only ever shows captured one-actor steps.
`StratLinePatches` is the one writer: the editor's lines, Set on map and the canvas's cone drag change a copy
of the lines and it writes the stored shape (no lines is a step for everyone, one line with no watch is a
plain step, five bare lines to one place fold back into a step for everyone). The validator's line rules, with pointers the editor's inline checks read:

| Rule | Severity | Pointer |
|---|---|---|
| slot not `A` to `E`, or a second line for a slot | refusal | `/steps/i/assignments/j/slot` |
| a `move` line whose `to` has no place and no point | warning | `/steps/i/assignments/j/to` |
| a `to` place the map lacks | warning | `/steps/i/assignments/j/to/place` |
| a watched place the map lacks | warning | `/steps/i/assignments/j/watch/places/k` |
| a via place the map lacks | warning | `/steps/i/assignments/j/via/k` |
| `actor` is not the lines' single slot or `all` | warning | `/steps/i/actor` |
| a step-level `to` beside lines | warning | `/steps/i/to` |
| a step-level `via` or `viaPoints` beside lines | warning | `/steps/i/via` (or `/steps/i/viaPoints`) |

With lines, the step-level "a move has no destination place" warning is not raised; each move line is
checked instead. A lineup throw by a step whose lines name more than one slot warns as `all` does.

### Via (`via`)

```jsonc
{ "atSeconds": 100.0, "actor": "B", "verb": "move", "to": { "place": "LongDoors" }, "via": ["OutsideLong"] }
{ "slot": "C", "to": { "place": "BombsiteB" }, "via": ["Middle"], "viaPoints": [{ "x": 120, "y": -40, "levelMinZ": -99968 }] }
```

A travel can name places to go through on its way, instead of the shortest route: "B moves to Long Doors via Outside
Long". Both members are optional and not written when absent, so a file without them loads and saves byte for byte as
before.

* **`via`** is canonical place names, first first. **`viaPoints`** (optional, written only when there is one) holds
  points picked outside every place, `{ x, y, levelMinZ }` each, after the places in reading order: a sibling of `via`
  for the reason `watch.points` and `lurk.areaPoints` are siblings. The cost is the same too: a point always reads after
  the places, so a route goes through every via place before any via point.
* **Where it lives** is where `to` lives. A step without lines keeps its own; a step with lines keeps one per line and
  the step's own is not used (the validator warns at `/steps/i/via`). The line writer (`StratLinePatches`) moves a
  via with its `to`: onto the line when a watch makes a one-player step a line, back onto the step when the lines fold
  to a plain step or to `all`. Five bare lines fold to `all` only when their vias agree too.
* **Which verbs** (`StratStepFields`, `StratStepField.Via`): the travel verbs, move, push, rotate, `other` and lurk (its
  walk to its first area). The editor's verb change clears a via the new verb does not use, in the same entry. The
  editor shows a multi place field labelled `via`: one on the compact row, which writes every line, and one per line
  when the row is split. Its map pick adds the place under the click, or the point outside every place
  (`StratLocationKind.Via`).
* **Printing.** The call sheet, the role sheets, LAN print and the history print it after the destination:
  `B moves T Spawn → Long Doors via Outside Long` (`StratStepPhrasing`), `C → Bombsite B via Middle` for a line, and
  `B moves to Long Doors via Outside Long at 1:45` in the history, whose edits read `via set to Outside Long, Middle`
  and `via cleared`.
* **On the canvas** a run goes through each via in order, routing each leg from the last stop (a via place is its
  arrival on the floor the token is on, a via point is itself), and arrives after the whole length at its speed. With
  routing off, or no zones for the map, a via is ignored and the run is the straight line it always was.

| Rule | Severity | Pointer |
|---|---|---|
| a via place the map lacks | warning | `/steps/i/via/k` |

### A lurk (`lurk`)

```jsonc
{
  "atSeconds": 70.0, "actor": "E", "verb": "lurk",
  "lurk": {
    "areas": ["PalaceInterior", "Connector"],
    "rotate": { "atSeconds": 40, "when": "on the call", "to": { "place": "BombsiteB" } }
  }
}
```

A lurk says where the player works and when and where they rotate. It is optional and not written when absent,
so a file without it loads and saves byte for byte as before. It lives on the step, not on a line: it is shared
by every player the step names, like `from` and `utility`, and two lurkers who rotate differently are two steps.

* **`areas`** are canonical places the lurk takes control of or works towards, first first. **`areaPoints`**
  (optional, written only when there is one) holds areas picked on the map outside every place, `{ x, y, levelMinZ }`
  each, after the places in reading order: a sibling of `areas` for the same reason `watch.points` is one.
* **`rotate`** is when the lurk rotates and where to. `atSeconds` is round clock remaining (later in the round,
  so lower than the step's own); `when` is free text ("on the call", "on contact", "bomb planted", "after first
  kill" are the editor's suggestions, not a vocabulary); `to` is a location (a place, a point, or both). A rotate
  with a time and a condition means whichever comes first. On the canvas a rotating token walks to the point when
  `to` has one, else to the place.
* `StratLurkPatches` is the one writer: it drops a `rotate` with no time, condition or place and a `lurk` with no
  area and no rotate, so a step never carries an empty object. The editor's verb change removes a lurk from a verb
  other than `lurk`, in the same entry.

| Rule | Severity | Pointer |
|---|---|---|
| an area the map lacks | warning | `/steps/i/lurk/areas/k` |
| a rotate-to place the map lacks | warning | `/steps/i/lurk/rotate/to/place` |
| a rotate time not later than the step (`atSeconds` not lower) | warning | `/steps/i/lurk/rotate/atSeconds` |

The place rules wait for the map's zones, as the other place warnings do.

**On the canvas**, a lurk step first walks its players to its first area from the step's time (see "Motion on the
canvas"). A rotate with a time and a place then moves every token the step names: from the rotate time the
token walks from where it stands to the centre of the rotate-to place (`StratPlaceCentres.Arrival`: on the token's
floor when the place has areas there, else on the floor holding most of the place, and the token arrives on that
floor), at
115 units a second (`StratSceneProjection.WalkUnitsPerSecond`, a rifle's shift-walk), and stays there. Nothing about a
lurk runs. A later
keyframe for the slot (an authored position, a lineup origin) still wins: if it comes before the token could
arrive, the token heads to it from the rotate time instead. A rotate that is not later than its step, or whose
place has no centre (or the zones are not in yet), moves nothing. "Later" is compared in strat ticks, the same
test the validator uses (`StratLurkPatches.IsLater`). The transport and an export run to the last arrival when it
comes after the last step (`StratSceneProjection.ContentEndTick`), so a rotate after the last step still plays. A
step added at or after a rotate whose place resolves does not carry the lurker's old position, so the rotate is not
undone by the next step; one added before the rotate carries the lurker where it stands.

**Facing on the canvas.** A token's yaw at a step, in order: a throw's lineup origin (position and yaw; a
throw with a lineup and one named slot still pins that slot); else the slot's line `watch.yawDegrees`;
else towards the first watched entry: a place's centre, on the token's level when the place has nav areas
there, else over all its floors, or a watched point itself; else `positions[].yawDegrees`; else, for a token a
destination moves, the direction it travels; else the yaw it had. A token running to a destination faces the way
it runs and turns to its watch on arrival, faced from the arrival point; one already there turns at the step's
time. A watching line on a
step with no position for its slot adds a keyframe where the token already stands, so no move is re-timed.
Place centres are the area-weighted centroids of the map's baked zones (a custom zone with no areas uses
the middle of its box), built once per map when the zones load through the processing queue; until they
land, and on a map with no zones, only an explicit `yawDegrees` turns a token. A new step carries each
token's facing as the projection shows it, from the canvas's Add step and the editor's alike.

### Motion on the canvas

A step's destination moves its tokens. The canvas, the Detected preview and an export all sample the one projection
(`StratSceneProjection`), so they agree.

* **Who and where.** Every player the step names: a single actor, each line, or all five for `all`. `all` is the
  strat's own five; the opponent tokens `O1`..`O5` never move for a destination or a rotate. The target is
  the location's point when it has one, else the place's arrival (`StratPlaceCentres.Arrival`: its centre on the
  token's floor when the place has areas there, else on the floor holding most of it, and the token arrives on that
  floor). A place the map lacks, or zones not loaded yet, moves nothing.
* **Routes.** Tokens walk the map's nav round walls (docs/strat-book/token-pathing.md). The graph is the baked nav areas
  and links in the map's `zones.json` (`ZoneSet.Areas`, `ZoneSet.AreaLinks`), built with the zones when the canvas reads
  them through the processing queue, and searched with A* plus a funnel (`NavPathfinder`). A link through a shared
  edge is walked both ways; a link with no shared edge (a drop, a jump, a gap) is climbed only when the far area's mean
  Z is at most 64 units higher, which stands in for the nav's one-way drops the bake does not keep. Ladders are not in
  the bake, so a route takes the long way where a ladder is the short one. Routing is the `stratbook.routing` feature,
  on by default; off, or with no zones for the map, every move is the straight line and the timing it was before. What
  routing changes:
  * A run (a travel, a lurk's walk, a rotate) follows its route at its speed, so it arrives after the route's length,
    not the straight distance: the transport's end and an export grow with it. Each bend is a keyframe at the tick
    its share of the length puts it, so the speed is one over the whole run, and the token faces the leg it is on.
    A run with `hold` interpolation still jumps, at the routed arrival.
  * A segment whose ticks the steps fixed (two placed entries, a position verb's walk, a run cut short by a later
    entry) bends along its route and keeps both ticks: the token arrives when the author put it there, faster or
    slower than a run. A position verb that cuts a run short heads for its place from the last bend the run reached.
  * The floor changes where the route crosses into the other floor, not half way along the leg before it.
  * A fanned spot must stand on the nav as well as in the place; with none, the ring spot is snapped onto the
    place's nav, and with no snap the token goes to the arrival.
  * A point off the nav snaps to the nearest area within 256 units and the route goes straight from there to the
    point; a point that does not snap, or two ends no corridor joins, gets the straight line.
  * Thrown utility is not routed: a projectile flies as before.
  * The canvas, the Detected preview and an export draw a moving token's way ahead as a faint line in its side's
    colour (`Pb2dCanvasRouteT`, `Pb2dCanvasRouteCt`), from where it is to where the move ends; it is gone on
    arrival.
* **By verb** (`StratSceneProjection.MotionOf`):

  | Verbs | Motion |
  |---|---|
  | move, push, rotate, `other`, and any verb outside the vocabulary | travel: leaves where it stands at the step's time and runs to the target |
  | hold, peek, fake, plant, defuse | position: at the target at the step's time, walking from its previous keyframe |
  | lurk | travel: walks to its first area (`areas`, then `areaPoints`) from the step's time; its rotate walks too |
  | throw, wait, call | none: a throw's landing is where the grenade goes, not the player |

  A run is at 215 units a second (`RunUnitsPerSecond`, a rifle's run), along its route when routing is on. A lurker
  walks at 115 (`WalkUnitsPerSecond`, a rifle's shift-walk): to its area and on its rotate, each leg routed from where
  the token stands. A later step on the lurk's tick that sends it elsewhere wins, at
  that step's pace. Both go through the same code, and the transport's end (`ContentEndTick`) covers the walk's arrival.
  A later keyframe for the slot that comes before the token could arrive wins: it heads there from the step's time
  instead. A later destination or rotate cuts a run that has not arrived: a run turns from where the token is at
  that moment, and a position verb heads for its place from the cut run's start, as an authored entry would. A
  position verb with no time to walk, because the slot's last keyframe is at the same tick (a setup's first step on
  the round-start seed), runs instead, so the move still plays. A token with no keyframe before the step starts at
  its destination at the step's time.

  The step's `holdSeconds` and `interpolation` shape a destination as they shape an authored entry: a hold delays
  the run's start (and its arrival by as much), and `hold` interpolation jumps at the end of the move instead of
  gliding.
* **Several tokens, one place.** When more than one token is sent to the same place (or the same point) and they
  are there at the same time, whichever steps sent them, each stands on a ray fixed by its slot letter (A at 90
  degrees, then every 72), at 160, 120, 80 or 48 units from the arrival, the widest still inside the place on that
  floor (`IZonePlaceResolver.ResolveOnFloor`); with no spot inside, 96 units out. Nearer spots overlap once the map
  is fitted to the pane. A token is there from its step's time until its next destination or placed entry. Only destinations that win by precedence count, so a slot whose drag
  beats its `to` does not push another token off the centre. The same slot takes the same spot in any group; a token
  there alone goes to the centre.
* **Precedence for a slot on a step:** a throw's lineup origin; then an authored position (a drag, or a set
  position); then the destination; then a carried position; then where the token already is. An authored position
  and a destination conflict only on a position verb, where the position is the exact spot and the destination is
  not used. On a travel verb (move, push, rotate, `other`, lurk) they are two halves of one move: the position is
  where the token is at the step's time and the destination is where it goes from there.
* **Observed positions.** Create Strat From Round and Strat Mining write `"observed": true` on every position they
  capture, since each is where a player was seen. An observed position is the spot on every verb, so a captured move
  does not run on to its majority `to`. The field is written only when true. A drag writes the entry without it, Add
  step's copies are carried and never observed, and Duplicate copies the mark as it is. Steps a person adds to a
  captured strat follow the travel rule above.

  A captured or mined strat (`origin` set, or the mined tag) written before the mark has no `observed` key anywhere.
  In such a file only, every position reads as observed (`StratSceneProjection.IsLegacyObserved`), which is how it
  played before. Any capture made from now on is marked per position, so the whole-file rule never applies to it.
* **Several steps on one tick.** When steps share a tick and tell one slot different things, the step later in path
  order wins, whatever the kinds: a destination, a position verb's spot, a lurk's first area, a lineup origin or a travel
  step's departure position. Positions on a tick settle before its destinations.
  * A later step's destination replaces an earlier one's: the token leaves once, for the later target. A lurk's first
    area is a destination like any other, so a lurk after a move on its tick sends the lurker to its area, at a walk.
  * A later exact spot cancels the tick's earlier destinations for the slot: the token is there and stays. An exact
    spot is a lineup origin, or the entry (placed or observed) of a step that names the slot and does not travel: a
    position verb, or a wait, call or throw.
  * A later destination beats an earlier exact spot: the token leaves for the later target from the later step's own
    position when it has one, else from where it stands on the tick, which is the earlier spot. So a setup's first step
    on the round-start seed leaves from spawn, and a move after a lineup throw on one tick leaves from the lineup (the
    grenade still flies from the lineup either way).
  * A position on a later step that is not an exact spot (a travel verb's departure, or an entry for a slot the step
    does not name) is where the token leaves from. It does not cancel an earlier destination, and a carried copy
    (marked, or legacy) is ignored as usual.

  An observed entry keeps its meaning: it is the spot on its own step, and a later step on its tick moves the token
  from there rather than discarding it. Create Strat From Round puts two steps on one tick only when two stops share a
  demo tick (two throws, or a throw and the plant; a sweep is never within 3 seconds of another stop). Captures and
  mining write only hold, throw, plant and move, never a lurk. One captured shape plays differently: a plant by a
  player who is not ours reads as `all` sending our five to the planter's place, and a throw of ours at the same demo
  tick after it now keeps the thrower at the spot the capture saw instead of running to that place. Every other
  captured shape plays as before.

  This is what a round-start seed turned into a move, with a lurk on the same tick, needs: the lurk's copies of the
  spawn positions must not hold the five in spawn, whether or not the file still reads unmarked copies as carried.
* **Carried positions.** Add step copies every token where the projection has it at the step before and writes
  `"carried": true` on each copy. A drag rewrites the entry without it. The field is written only when true, so a
  file without it loads and saves byte for byte as before. A carried position holds the token (so a later drag on an
  earlier step does not move it) until a destination has moved that slot since its last origin or authored entry;
  from then on the copy is stale and ignored, and the token stays where the destination left it. Add step does not
  carry such a token at all, nor a lurker whose rotate has fired.

  A file saved before the mark holds carried copies with no mark. In such a file only, an unmarked position whose
  `x`, `y` and `levelMinZ` are each within 0.02 of the slot's last placement as the file places it (an earlier step's
  `positions[]` entry, or its lineup origin) counts as carried (`StratSceneProjection.IsLegacyCarry`). A file is
  such a file when no position anywhere in it has a `carried` key and it has no `origin` and no mined tag: a
  captured or mined strat never had Add step's copies, and its identical positions are real (a planter who did not
  move). The first write that adds a mark (an Add step) turns the rule off for the file for good; from then on only
  marked entries are carried. Duplicate copies each position's mark as it is, so a duplicated drag stays authored.
  In a file the rule still reads, a duplicate of an authored position reads as carried, since it looks exactly like
  one of the old copies.
* **Setting a destination** through the location fields or the line writer (`StratLocationPatches`,
  `StratLinePatches`) removes the marked carried positions of the slots whose destination it sets on that step, in
  the same undo entry, highest index first; a new first lurk area on a lurk does the same for the lurk's players.
  Only when the verb moves for it (`StratStepFields.MovesToTo`): a `to` on a throw, wait, call or lurk removes
  nothing. An authored
  position is never removed. The writers have no map, so they remove only marked entries; the projection alone
  applies the unmarked rule. The history reads the removal as `B's carried position dropped`.
* **The clock.** The transport, the step row's last band and an export run to the last arrival
  (`ContentEndTick`), so a run after the last step still plays.

### Utility on the canvas

Every step with a `utility` throws it (`StratThrows`, the projection's utility pass): a projectile leaves the
thrower at the step's time, flies to the landing and goes off there. The canvas, the Detected preview and an export
draw it from the same projection.

* **Thrower.** With a lineup that resolves, the technique's mean release point (the same origin the token stands
  on) and one grenade, however many lines the step has. Otherwise each line's slot, or the single actor, from where
  its token is at the step's time. A throw with no one to throw it goes off at the step's time with no flight.
* **Landing.** A resolved lineup's landing group detonation point (`GrenadeCluster.Landing`). Otherwise
  `utility.landing`'s point, else its place's arrival centre. With none of these the step draws nothing.
* **Flight.** A lineup flies for its technique's median air time (release to rest) and goes off at its median
  detonation time, never earlier. When the Grenade Index stores a flight for the technique, the preview follows its
  shape, bent so it starts on the origin and ends on the landing; otherwise a straight line. A throw without a
  lineup, or a lineup with no recorded air time, flies at 800 units a second, between 0.5 and 3.5 s, and goes off
  on landing. The line fades over 2 s after the projectile stops.
* **Effect by kind** (`StratFrameSource.EffectTicks`): a smoke for 18 s, blooming from 35% of its radius over the
  first second; a molotov's fire for 7 s; a flash's pop for 0.5 s; an HE's burst for 0.6 s; a decoy's marker for
  15 s. No smoke occlusion.
* **Colour.** The flight is the thrower's side colour. The effects keep the demo colours.
* **The clock.** `ContentEndTick` covers the last effect and the last line's fade, so the transport and an export
  run until the smoke clears.
* **Lineup data** comes from `LineupOriginSource`, which groups each map once off the UI thread: per technique the
  landing, the air and detonation times, and the stored flight. A lineup not grouped yet draws as if it had none
  until the map is ready.

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
words a person reads ("molotov moved from 1:22 to 1:16", "step added: A peeks Connector at 1:05", "E's lurk:
rotate time 0:40 → 0:35", "E's lurk: lurk areas set to Palace, Connector").

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

## Templates

New Strat can start a strat from a template in `Services/Strats/StratTemplates.cs`, and Apply template
fills an open strat that has no steps beyond the spawn seed. A template writes nothing a hand-written strat
cannot: `type`, `targetSite`, the `role` of any slot that has none, and ordinary steps after the seed.
The file does not record that a template was used, and revision 1 (or the one undo entry of Apply template)
already holds the steps.

| Template | Side | Site | Shape |
|---|---|---|---|
| Execute | T | A or B | utility from 1:02, entry and trade at 0:53, plant at 0:45 |
| Rush | T | A or B | utility on the run from 1:50, everyone onto the site, plant at 1:35 |
| Split | T | A or B | two groups set up (one step, lines A and D), utility from both sides, entries together at 0:55 (lines A and D) |
| Fake | T | hit A or B | fake verbs with utility at the other site from 1:25, then an execute |
| Default | T | none | map control, a lurk that rotates on the call, a call at 1:15, regroup at 1:00 |
| Anti-eco | either | none | hold, call, wait |
| Setup | CT | none | a 2-1-2 hold from round start (one step, a line per slot), early utility, rotation call |
| Retake | CT | A or B | after the plant (negative `atSeconds`): call, group, utility, retake, defuse by -0:25 |

The rules the templates keep, pinned by `StratTemplatesTests`:

- Every template validates with no refusal and no warning on every shipped map and every side it is for.
- The only places they name are `BombsiteA` and `BombsiteB`, the two on every shipped map. Any other place is
  left empty and described in the note, and a step that needs a place it cannot name is a `hold` or `wait`.
- A step carries only the members its verb uses (`StratStepFields`). A throw names a slot and a kind, never a
  lineup. No step carries positions: the tokens carry forward from the seed.
- A step where players take different places uses lines; it names no step-level `to` and no watch.
  Apply template is not offered on a strat whose seed step has lines.

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
the owner's callouts when given, else the canonical name split into words), a lurk's areas and rotate
(`E lurks Palace, Connector; rotate to B site at 0:40 or on the call`), and, only when it differs
from `to`, the utility's landing place in parentheses. A location with only a point prints as its
coordinate, `(1234, -561)`, and a point-only landing prints without the extra parentheses. A step with lines is headed by its slots (`All`
when the lines name all five) and its shared `from` (`B, C, D move from T Ramp`), and each line follows as an indented bullet:
`B → Palace, watching A site, CT`, or `B at Palace` for a verb whose place is where it stands (hold, peek,
fake, plant, defuse). The step track's tooltip and the Detected preview, which have one line per step,
join the lines after a colon. The step's own note is left out of this one-liner,
the same way a history summary leaves prose out; the strat's own `notes` field prints once, at the end.

### The role sheets (HTML, LAN Print)

`RoleSheet.Derive(doc, slot, callouts, roster, lookup)` builds one slot's sheet: the strat's masthead,
every step the slot owns (it has a line, or with no lines `actor` is the slot or `all`) plus, greyed,
another slot's step that feeds one of the slot's own moves (a `to` place of it, a line's included, matches
the slot's `from` or `to`, at the same or earlier real time, which is a **larger** `atSeconds` since the
round clock counts down), the branches that follow one of the slot's own steps, and the slot's tracked positions as an ordered polyline once Step
Authoring has written any. On a step with lines the slot's own entry is only its line, phrased as a
single-slot step with `, watching …` appended. `RoleSheetHtmlWriter.Html` renders a list of sheets as one self-contained HTML
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
