# The `.dvtag.json` tag file and the tag index (schema v1)

The Round Tagger stores what a person (or an accepted detector proposal) said about stretches of a
demo: a **code** that names a span ("A execute", 1:12 to 1:31 of round 7) and **labels** attached to it
("outcome = won", "site = A"). This document is for anyone who wants to read or write that file. A
committed sample lives at
[`tests/fixtures/tags/schema-v1.sample.json`](../tests/fixtures/tags/schema-v1.sample.json) and is
round-trip-pinned, unknown fields included, by `TagSchemaSnapshotTests.V1Schema_MatchesCheckedInSample`.

The format is this repository's own and is published under its licence. It is modelled on the
[annotation sidecar](playback2d-v2/annotations-format.md), which settled identity, clock and forward
compatibility the same way.

## Where the files live

| File | Desktop | Browser build |
|---|---|---|
| One document per tagged demo | `<app config root>/tags/demos/<sha256 of the demo>.dvtag.json` | Nowhere. Tags are session-only and the tagger says so. |
| The index | `<app config root>/tags/index.json` | Nowhere. |

Unlike annotations, a tag file is **never** written beside the demo, even when that folder is
writable. The Matrix reads every tagged demo from one directory, and most demos sit in Steam's
read-only replays folder anyway. The file is named by the demo's content hash, so renaming or moving
the `.dem` does not orphan its tags.

A document is written whole and atomically (a temp file in `demos/`, then a replace). The app creates
`tags/` on its first write, not at startup: a user who never tags has no folder.

## Top level

```jsonc
{
  "schemaVersion": 1,
  "demo":  { "sha256": "...64 hex...", "fileName": "match730_sample.dem", "sizeBytes": 549715968 },
  "clock": { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 223114,
             "firstTick": 0, "lastTick": 0 },
  "palette": "cs2-default",                   // the palette id last used; advisory, need not exist
  "instances": [ /* … */ ]
}
```

* **`demo.sha256`**: lowercase hex SHA-256 of the `.dem` bytes. It is the only field that takes part in
  matching; `fileName` and `sizeBytes` are for a person reading the file. **A reader that finds a hash
  different from the file's name must ignore the file and must not overwrite it.** Because the file is
  named by its hash, that only happens through a hand-edit or a copy, and the document still belongs
  to the demo it names. DemoViewer refuses such a file (and one it cannot parse) for the session:
  it loads nothing for that hash and declines to write over it.
* **`clock`**: which parse the ticks were written against. `kind` is always `dv-frame-clock`: every tick
  in the file is a DemoViewer frame-clock tick, **not** a live CS2 engine tick, and the same value the
  annotation sidecar and the demo cache's round rows carry. A mismatch against the reader's own parse is
  a warning, never a reason to discard. An all-zero clock (the fields before the demo was parsed)
  matches anything.
* **Nothing in the file is a frame index.** Frame indices shift on a re-parse; ticks do not.

## An instance

```jsonc
{
  "id": "6f1c0d2e-3a4b-4c5d-8e9f-a0b1c2d3e4f5",   // GUID, stable across edits
  "code": "A execute",                          // free string; the palette need not exist
  "fromTick": 41216, "toTick": 42432,           // frame-clock ticks, inclusive, fromTick <= toTick
  "round": 7,                                   // derived; absent when no round contains fromTick
  "createdUtc": "2026-09-23T14:02:11Z",
  "modifiedUtc": "2026-09-23T14:05:11Z",
  "source": "human",                            // "human" | "suggested" | "import"
  "provenance": { /* free-form; accepted proposals only */ },
  "labels": [                                   // HUMAN namespace, ordered, groups may repeat
    { "group": "outcome", "value": "won" },
    { "group": "site",    "value": "A" },
    { "group": "site",    "value": "B" },
    { "group": "",        "value": "sloppy" }   // a bare label: no group
  ],
  "facts": [                                    // PARSER namespace, rewritten wholesale on refresh
    { "group": "winner",    "value": "T" },
    { "group": "plantSite", "value": "A" }
  ],
  "factsStamp": { "schema": 1, "computedUtc": "2026-09-23T14:02:11Z", "stale": false },
  "note": "smokes were 1s late",                // free text; absent when null
  "positions": [ /* see below */ ],
  "movements": [ /* see below */ ]
}
```

* **`round`** is the round containing `fromTick`, numbered as the demo cache's rounds are (the same
  `ClipRound.Number` every DemoViewer file uses; not `m_totalRoundsPlayed + 1`, which is a fact). It is
  derived, stored so a cross-demo query need not open the cache, and may be rewritten by a refresh.
* **`source`** says who made the instance. `human` is a person with the palette or by hand. `suggested`
  is a Suggested Tags proposal that a person accepted; rejected and pending proposals never reach this
  file. `import` is reserved for a future importer and nothing writes it today. The field is a string,
  so a value this build does not know survives a round trip.
* **`provenance`** is present only on `suggested` instances: whatever the detector recorded (detector
  name, proposal id, confidence and so on). Opaque to DemoViewer and preserved as written.
* **`labels` and `facts` are two namespaces with one shape.** `labels` is what a person said. `facts`
  is what the parser derived from the demo, and belongs to the refresh pass: a re-parse or a newer Round
  Facts schema rewrites the whole `facts` array without reading `labels`, and the tagging UI never
  edits it. A human label must not use one of the fact group names below (tagging palettes are
  validated against that list), so a group name means the same thing whichever array it came from.
* **Labels are an ordered list, not a dictionary.** A group may repeat (an execute that hit both sites
  carries two `site` labels), and `group` may be empty, which is a bare label. `value` is always a
  string: numbers as invariant-culture text, booleans as `"true"` and `"false"`.
* **`factsStamp`** records when and against which Round Facts schema the facts were computed. `stale`
  is true when `fromTick` fell in no round at the last refresh, so the facts are the older ones.

### The fact vocabulary

Fact groups carry no prefix (the `facts` array is the namespace). The list is owned by Round Facts,
all absolute per side, never relative to "us":

| Group | Value |
|---|---|
| `round`, `matchRound`, `half` | integers as text |
| `buy.ct`, `buy.t` | `pistol` \| `eco` \| `semi` \| `force` \| `full` \| `unknown` |
| `score.ct`, `score.t` | integers as text |
| `winner` | `T` \| `CT` \| `none` |
| `endReason`, `plantSite`, `plantTick`, `roundTime` | as Round Facts reports them |
| `phase` | the round phase at the instance's `fromTick` |
| `manCount.ct`, `manCount.t` | players alive at `fromTick` |

Relative groups (`side`, `buy.us`, `buy.them`, `opponent`) are not facts. A consumer derives them at
query time from Team Identity; a person or a detector writes `side` as a human label when an instance
is about one side. The committed sample's `facts` predate this list; a reader should treat fact groups
as opaque strings, which is what makes a vocabulary change safe.

### Reserved human groups

`strat`, `strat.rev`, `strat.result` and `strat.failure` belong to the Strat Model. They are ordinary
labels in the file, but a tagging palette may not offer them as its own groups, and the index lists
the distinct `strat` values per document.

### Positions and movements

```jsonc
"positions": [
  { "x": -1180.5, "y": 2044.25, "levelMinZ": -384, "tick": 41600,
    "place": "BombsiteA", "placeSource": "zones:1c2b3a49" }
],
"movements": [
  { "from": { "x": -220, "y": 1610, "levelMinZ": -384, "tick": 41300, "place": "Ramp", "placeSource": "pawn" },
    "to":   { "x": -1180.5, "y": 2044.25, "levelMinZ": -384, "tick": 41600 } }
]
```

A position is a world point on one floor: `x`, `y` in world units and `levelMinZ`, the floor's
**quantized** lower Z (`round(zMin / 64) * 64`), the annotation sidecar's anchor rule and never a floor
index. `tick` is when the click was made, absent when the click was not time-specific. A movement is a
`from` and a `to`. Both arrays are empty when the code press had no click.

Coordinates are what a person clicked and are never rewritten. `place` is derived, and `placeSource`
says how, so a later pass knows whether it can do better:

| `placeSource` | Meaning |
|---|---|
| `zones:<zonesVersion>` | Resolved against the map's baked zones (see [`zones-format.md`](zones-format.md)) at that `zonesVersion`. Authoritative; re-resolved when the map's `zonesVersion` changes. |
| `pawn` | No zone data: the place name of the nearest alive player on that floor at `tick`. Approximate. |
| absent | Unresolved. |

## `index.json`

```jsonc
{
  "version": 1,
  "entries": [
    { "sha256": "...", "fileName": "match730_sample.dem", "instanceCount": 41,
      "codes": ["A execute", "Default", "Retake B"],   // distinct, ordinal order
      "stratIds": ["a-split"],                          // distinct values of the `strat` label
      "modifiedUtc": "2026-09-23T14:02:11Z", "tickRate": 64 }
  ]
}
```

The index is **derived**: one small row per document so a cross-demo reader can skip files that
cannot match before opening them. DemoViewer rebuilds it from `demos/` when it is missing or corrupt
and reconciles it against the directory at startup, so deleting it is always safe. A third party that
writes a document need not touch the index.

## Palettes (`.tagpalette.json`)

The Tag Palette in the 2D Playback tab writes instances from a **palette**: the codes a team tags with,
the label panels each code leads to, how far before and after the playhead a code's span reaches, and
which label groups stay set between tags. A palette is data, so a team writes its own and shares the
file:

| File | Desktop | Browser build |
|---|---|---|
| The built-in `cs2-default` | inside the app | inside the app |
| One file per user palette | `<app config root>/palettes/<name>.tagpalette.json` | Nowhere. Only the built-in is offered. |

```jsonc
{
  "schemaVersion": 1,
  "id": "cs2-default", "name": "CS2 default",       // id is what settings and a tag file's "palette" name
  "panels": [
    { "id": "root", "buttons": [                       // "root", else the first panel, is where tagging starts
        { "code": "A execute", "hotkey": "1", "leadSeconds": 5, "lagSeconds": 10,
          "then": "outcome", "colorArgb": 4293467747 },
        { "code": "Default", "hotkey": "3", "leadSeconds": 0, "lagSeconds": 20 } ] },
    { "id": "outcome", "kind": "labels", "group": "outcome", "then": "site", "buttons": [
        { "value": "won", "hotkey": "W" }, { "value": "lost", "hotkey": "L" } ] },
    { "id": "site", "kind": "labels", "group": "site", "buttons": [
        { "value": "A", "hotkey": "A" }, { "value": "B", "hotkey": "B" } ] }
  ],
  "stickyGroups": ["opponent", "map"],
  "clampToRound": true                                 // the default when absent
}
```

* A code button makes an instance from `leadSeconds` before the playhead to `lagSeconds` after, in the
  demo's own ticks. With `clampToRound` the span stays inside the playhead's round (from its start to
  the tick before the next round starts).
* `then` names the **labels** panel shown after the press, and only that panel; a labels panel adds one
  label of its `group` and shows its own `then`. When the chain ends the instance is written, code and
  labels together, as one undo step. Esc part-way writes it with the labels it has.
* A value picked in one of the `stickyGroups` is kept for the rest of the session: it is added to every
  new instance, and a panel for that group is answered from it and skipped, until the sticky labels are
  cleared. The stored label is an ordinary label.
* A hotkey is one key, optionally with `Ctrl+`, `Shift+` or both. A bare digit is the top-row digit, and
  the number pad works too. Keys are live only while the palette has focus.

A file is refused, with a line in the diagnostics log and on the palette panel, when it cannot be
parsed; when a hotkey is an app-wide shortcut, a key the browser keeps for itself, or one of the
palette's own keys (Esc and the note and sticky-reset chords); when two buttons of one panel share a
hotkey; when a group is one of the fact names or the reserved strat groups above; or when a `then` names
no labels panel or loops. A hotkey that shadows a 2D Playback key while the palette has focus (`F`
follow, `Q`/`E` rounds, `Ctrl+Z`) loads with a warning naming the key. A user palette may not reuse a
built-in id. DemoViewer never writes palettes; edit the file and press the palette's reload button.

## Querying

DemoViewer reads the files through one query layer (`TagQuery`), which is also the clearest statement
of what the fields are for:

* **`Find(documents, slice)`** returns the instances in a slice, as `(sha256, id, code, fromTick, toTick,
  round)` refs in document order then instance order. A slice filters on demos (by hash), codes,
  rounds, a creation-time cursor (strictly after), the source, and label predicates. A predicate is a
  namespace (`labels`, `facts` or either), a group and a set of accepted values; several predicates must
  all hold. With no source named, every source but `import` is read.
* **`Pivot(documents, slice, rows, columns)`** is the Matrix: a table keyed by code, by one label group's
  values, by demo or by round. An instance with two values of a pivoted group appears under each (once
  per distinct value), and one with no value on an axis is in no cell. The table is sparse: an empty
  cell is absent.
* **`At(document, tick)`** returns the instances whose span contains a tick, bounds inclusive, whatever
  their source.

## Interchange with sports-analysis tools (deferred)

There is no XML export in v1. The Hudl Sportscode and kloppy XML shape is a vendor format with no
published specification, and nothing in this project consumes it yet. The data model keeps the three
conventions that make an exporter a small task later:

| Convention | Why it maps |
|---|---|
| An instance is a `(fromTick, toTick, code)` span | 1:1 onto `<instance><start><end><code>` |
| Labels are an ordered list of `{group, value}`, groups repeat, `group` may be empty | 1:1 onto repeated `<label><group><text>`; a bare label is the `<text>`-only shape |
| A clock header with `tickRate`, one document per demo | seconds are `(tick - origin) / tickRate`; the XML's times are per video, so one file per demo |

The mapping, recorded for whoever builds it:

| `.dvtag.json` | Sportscode / kloppy XML |
|---|---|
| `instances[i]` | `<instance>` under `<ALL_INSTANCES>`, in document order |
| document-order index, 1-based | `<ID>` (integers are what every importer has been seen to accept; string IDs are legal) |
| `fromTick`, `toTick` | `<start>`, `<end>` as `(tick - originTick) / clock.tickRate + offsetSeconds`, `"0.0##"` invariant |
| `code` | `<code>` |
| `labels[j].group`, `.value` | `<label><group/><text/></label>`; an empty group is `<label><text/></label>` |
| `facts[j]` | the same, under their plain group names (human and fact groups are kept disjoint) |
| `round` | a `round` label |
| `note` | a `note` label |
| `source: import` | an importer is this mapping run backwards, and is deferred with the exporter |
| several demos | one file per demo in a folder, never merged |
| `<ROWS>` colours, `<SESSION_INFO>` | unverified; omit until a genuine export from the target tool is in hand |

The trigger for building it is a user who wants their tags in Sportscode or Nacsport, or a video tool
that needs a coded timeline. The first step then is one genuine export from that tool, to verify the
optional blocks against. If a flat export is wanted sooner, it is CSV (one row per instance, one column
per label group) over the same `Find` the Matrix uses.

## Forward compatibility

Every object in the file (the root, the `demo` and `clock` headers, each instance, label, facts stamp,
position and movement) accepts unknown fields, and DemoViewer preserves them across a load, edit and
save. The committed sample carries one at root, instance, label and position level, and its round trip
is byte-identical. Readers should ignore fields they do not recognise rather than rejecting the file.

`schemaVersion` is advisory: a higher number is read for whatever this build understands, not refused.
