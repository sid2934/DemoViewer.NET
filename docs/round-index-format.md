# The `.dvri.json` round index sidecar (schema v1)

DemoViewer's Situation Search runs over a derived store: one row per (demo, live round, sampled
second) carrying the place-count token per side. The rows for one demo live in a JSON sidecar beside
the demo cache. This document is for anyone who wants to read or write that file. A committed sample
lives at [`tests/fixtures/round-index/schema-v1.sample.dvri.json`](../tests/fixtures/round-index/schema-v1.sample.dvri.json)
and is round-trip-pinned by `RoundIndexStoreTests`.

## Where the file lives

| Condition | Location |
|---|---|
| Desktop | `<app config root>/cache/round-index/<StableKey>.dvri.json`, where `StableKey` is the same path hash the cache's `demos/<StableKey>.json` record uses |
| Browser build | Nowhere. The index is session-only and the Situations tab says so. |

The file is a cache: it is rebuilt from the demo whenever its fingerprint no longer matches, and it is
never a source of truth. The stamp that says whether it is current (`RoundIndex`, `RoundIndexState`,
`RoundIndexFingerprint`, `RoundIndexRowCount`) lives on the demo's cache record, not in this file, so
a crash between the sidecar write and the stamp leaves "not indexed" rather than a stamp with no file.

## Top level

```jsonc
{
  "schemaVersion": 1,
  "fingerprint": "ri1;cadence=1;token=1;rf=1;src=pawn",
  "demo":  { "sha256": null, "stableKey": "3f9c…", "fileName": "match730_….dem", "sizeBytes": 289436777 },
  "clock": { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 154869, "firstTick": 1, "lastTick": 132516 },
  "map": "de_nuke",
  "cadenceTicks": 64,
  "rounds": [ /* … */ ],
  "places": { /* … */ },
  "transitions": [ /* … */ ]
}
```

* **`fingerprint`**: what the rows mean. `ri<schema>;cadence=<seconds>;token=<token grammar
  version>;rf=<round facts schema>;src=<pawn|zones>[;zv=<zone set version>]`. A reader whose own
  fingerprint differs must rebuild, never reinterpret: the same file under a different cadence would
  mean a different second.
* **`demo.sha256`**: lowercase hex SHA-256 of the `.dem` bytes, or `null` until the cache has hashed
  the demo. When both sides have a hash it is the only field that takes part in matching; **a reader
  that finds a different hash must ignore the file.** `stableKey` is the join key until the hash
  exists and stays as the file name afterwards.
* **`clock`**: the annotation sidecar's block verbatim ([annotations-format.md](playback2d-v2/annotations-format.md)).
  Every tick in this file is DemoViewer's frame clock, not a live CS2 engine tick. A `tickRate`
  mismatch on read is a rebuild trigger, never a reinterpretation.
* **`cadenceTicks`**: `round(cadenceSeconds * clock.tickRate)`. Rows are stored as step offsets from
  each round's freeze end, never as absolute ticks: `tick = freezeEndTick + step * cadenceTicks`.

## A round

```jsonc
{
  "number": 3,                 // == the Round Facts round number: the foreign key
  "freezeEndTick": 10746,      // frame clock; step 0 is sampled here
  "endTick": 17138,            // frame clock; the first tick NOT sampled
  "runs": [
    [0, 4,   "CTSpawn:5",              "TSpawn:5"],
    [5, 9,   "BombsiteA:2|Outside:3",  "Lobby:3|Ramp:2"],
    [10, 10, "BombsiteA:2|Outside:2",  "Lobby:3|Ramp:1"]
  ]
}
```

Only live rounds are written. Freeze time is not sampled (positions are spawns) and the post-round
win panel is not sampled (nothing after `endTick` is a situation anyone searches for).

**`runs`** tile the sampled steps: each entry is `[fromStep, toStep, ct, t]`, a (CT, T) token pair
held over consecutive samples, so run *i* ends at `toStep` and run *i+1* starts at `toStep + 1`. A
gap between two runs is a step the walk did not sample. Measured, runs are 55 to 65 percent of the
row count. A reader that wants one row per second expands each run.

### The token

Per side, per sampled tick, the **alive** players on that side grouped by place:

```text
token := ""                                   an empty side: nobody alive
       | pair ( "|" pair )*
pair  := place ":" count                      count is always written, including 1
place := any characters except ":" and "|"    the raw pawn string, case preserved
                                              "?" is reserved for a null place
order := pairs sorted by place, ordinal comparison, ascending
```

Examples: `BombsiteA:2|Outside:3`, `?:1|Ramp:4`, `CTSpawn:5`, `` (empty).

* Alive is decided from Round Facts `Kills`: a slot is dead from the first kill at or before the
  sampled tick whose victim it is, until the round ends. The position walk yields every pawn with a
  controller, dead or alive, and a quarter of one-second rows carry a dead one, so counting them would
  make every such token wrong.
* Side is decided from Round Facts `Slots` at freeze end; a slot on neither side (a spectator) is dropped.
* `src=pawn` (the default) names places by the pawn's `m_szLastPlaceName`, Valve's own vocabulary.
  `src=zones` names them through the effective zone set (baked plus the user's overlay) resolved over
  the sample position, so a team that authored its own zones searches by its own names. The two never
  mix in one file: the source is in the fingerprint, per map.
* The encoder is `PlaceCountToken.Encode` and its decoder is the exact inverse; the same function
  encodes the current tick's positions for Find Rounds Like This, so the token a query builds is byte
  for byte a token this file can hold.

## `places`

```jsonc
"places": {
  "Outside": { "n": 2396, "z": [ [-448, 1180, 61234.5, -812900.2], [-384, 1216, 60110.0, -800012.7] ] }
}
```

Alive samples with a non-null place, per place: `n` is the total and each `z` entry is
`[zBucket, n, sumX, sumY]` for one 64-unit bucket of the **sample's** Z (`MapSpace.QuantizeZ` of the
sample Z). It is never a level key: level keys are band lower bounds a floor rebuild can move, so a
consumer folds buckets into whatever bands it has. Sums rather than centroids so that folding across
demos is plain addition; the Query Canvas divides once per query to snap a dropped token to the
nearest place on the clicked pane's band.

## `transitions`

```jsonc
"transitions": [ ["CTSpawn", "Outside", 115], ["Admin", "Ramp", 62] ]
```

Unordered place pairs (`a` sorts before `b` by ordinal comparison) with the count of one-second place
changes by one alive player between consecutive rows within a round. Folded across the library and
thresholded at three observations, they recover the callout adjacency graph the Tolerance Slider's
middle stops use until Zone Baking's graph is available for the map.

## Forward compatibility

The root object accepts unknown fields and a reader should ignore fields it does not recognise. The
`schemaVersion` is advisory on read: a higher number is read for whatever this build understands.
The fingerprint, not the schema number, is what decides a rebuild.

## The positions sibling: `.dvrp.json.gz` (schema v1)

Beside every sidecar sits `<StableKey>.dvrp.json.gz`, the same rows seen the other way round: per live
round, per sampled step, the alive players' positions the tokens were encoded from. A Result Card
thumbnail is one step of this file rendered through the headless scene path, and Overlay View reads
every step, so neither ever opens a demo (measured, a seek from the demo is 0.6 to 3.3 s and half a
gigabyte of heap per hit; ten tuples render in half a millisecond). Written gzipped because the
compact JSON is 170 to 210 KB per demo and 56 to 60 KB zipped, about the size of the sidecar. A
committed uncompressed sample lives at
[`tests/fixtures/round-index/schema-v1.sample.dvrp.json`](../tests/fixtures/round-index/schema-v1.sample.dvrp.json)
and is round-trip-pinned by `RoundPositionsTests`.

```jsonc
{
  "schemaVersion": 1,
  "fingerprint": "ri1;cadence=1;token=1;rf=1;src=pawn;pos=1",   // identical to the .dvri.json it belongs to
  "demo": { "stableKey": "3f9c…", "sha256": null },
  "cadenceTicks": 64,
  "places": ["Outside", "Lobby", "Ramp", "?"],                   // the place table a tuple indexes; "?" is the null place
  "rounds": [
    { "number": 3, "freezeEndTick": 10746, "ct": [0, 2, 5, 7, 9],   // slots on CT this round, from Round Facts Slots
      "pos": [                                                     // indexed by step; tick = freezeEndTick + step * cadenceTicks
        [[0, -448, 1180, -416, 0], [1, 1320, -900, -700, 2], [2, -129, -1848, -416, 1]],
        [[0, -401, 1130, -416, 0], [2, -129, -1848, -416, 1], [4, 2600, 900, -416, 3]]
      ] }
  ]
}
```

* Each tuple is `[slot, x, y, z, placeId]`: **alive** slots only (the sidecar's own alive rule, so a
  picture always agrees with its token), world units rounded to integers (a marker at card size is
  about thirty units wide), Z kept for the level pick, slots ascending. A step the walk did not
  sample is an empty list, so a tuple's tick is always its index times the cadence.
* `pos=<n>` in the fingerprint is what a tuple means (`pos=1`: the above). It is part of the
  index fingerprint, so a change re-indexes rather than reinterprets, and a sidecar written before
  the positions file existed carries no `pos=` and is stale. "Never indexed" and "indexed before
  positions existed" are therefore one state, which is what the cards' placeholder relies on.
* A reader with the current fingerprint and the record's hash ignores a file under another
  fingerprint or naming another hash, exactly as the sidecar rule says; the card then shows a note
  instead of a picture and "Rebuild index" is the remedy.
* The evaluator writes this file first, the sidecar second and the stamp last, so a crash leaves
  "not indexed" or a positions file nothing reads, never an index whose cards have nothing to draw.
  Deletion and the orphan sweep take both files.
* Thumbnails themselves are never written to disk: a per-session memory cache keyed by
  `(stableKey, round, tick, fingerprint)` holds the rendered PNGs, dropped on a rebuild.
