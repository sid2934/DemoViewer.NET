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
