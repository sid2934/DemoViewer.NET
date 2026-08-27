# The `.dvann.json` annotation sidecar (schema v1)

DemoViewer's 2D playback surface stores hand-drawn annotations in a JSON sidecar next to the demo. This
document is for third parties who want to read or write that file. A committed sample lives at
[`tests/fixtures/playback2d/annotations/schema-v1.sample.json`](../../tests/fixtures/playback2d/annotations/schema-v1.sample.json)
and is round-trip-pinned by `AnnotationSchemaSnapshotTests`.

## Where the file lives

| Condition | Location |
|---|---|
| The demo's directory is writable | `<full path to the demo>.dvann.json` — e.g. `match.dem.dvann.json` |
| It is not (a read-only replay folder) | `<app config root>/annotations/<sha256 of the demo>.dvann.json` |
| Neither is available (browser build) | Nowhere. Annotations are session-only and the UI says so. |

The writable check is a create-and-delete probe, run once per directory per session.

## Top level

```jsonc
{
  "schemaVersion": 1,
  "demo":  { "sha256": "...", "fileName": "match.dem", "sizeBytes": 123456789 },
  "clock": { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 12345,
             "firstTick": 128, "lastTick": 49500 },
  "elements": [ /* … */ ]
}
```

* **`demo.sha256`** — lowercase hex SHA-256 of the `.dem` bytes. It is the only field that takes part in
  matching; `fileName` and `sizeBytes` are for a human reading the file. **A reader that finds a
  different hash must ignore the file and must not overwrite it** — the sidecar belongs to a different
  demo that happens to share this path.
* **`clock`** — which parse the tick anchors were authored against. `kind` is always `dv-frame-clock`:
  the ticks below are DemoViewer's own frame-clock ticks, **not** live CS2 engine ticks. A mismatch is a
  warning, never a reason to discard: annotations with no time anchor do not depend on the clock at all.

## An element

```jsonc
{
  "id": "11111111-1111-4111-8111-111111111111",  // GUID, stable across edits
  "kind": "Freehand",                            // only Freehand is written today
  "colorArgb": 4294951175,                       // packed 0xAARRGGBB as an unsigned integer
  "widthWorld": 6,                               // stroke width in WORLD units, not pixels
  "opacity": 1,                                  // 0..1, multiplied onto the envelope's opacity
  "revealOnFadeIn": false,                       // draw-on reveal during the lead-in ramp

  "space": "world",                              // "world" | "entity"
  "levelMinZ": -384,                             // world: the level's QUANTIZED lower Z (multiples of 64)
  "steamId": 0,                                  // entity: the anchored player's 64-bit SteamId
  "dx": 0, "dy": 0,                              // entity: offset from the player to the first sample

  "fromTick": null, "untilTick": null,           // visibility window; null = unbounded
  "fadeInTicks": 0, "fadeOutTicks": 0,           // ramps OUTSIDE the window (see below)

  "points": [ -120.5, 240.25, 0.5, -60, 260, 0.62 ],  // flat [x, y, pressure] triples, world space
  "text": null,                                  // label content, for the (unimplemented) Text kind

  "timing": {                                    // OPTIONAL; absent on all but real-time strokes
    "runs": [ 0, 0, 41, 96, 41, 160, 78, 214 ],  // flat [sampleIndex, tickOffset] pairs
    "durationTicks": 214
  }
}
```

### Space anchors

* **`world`** pins the stroke to one floor of the map, keyed by that floor's *quantized* lower Z
  (`round(zMin / 64) * 64`). Quantizing is what lets an anchor written before a floor-split rebuild still
  find its own level. It is never a floor *index* — inserting a basement shifts every index.
* **`entity`** makes the stroke follow a player. Keyed by SteamId because roster slots recycle within a
  demo. Rendering translates the whole stroke so its **first** sample sits at `player + (dx, dy)`, which
  makes the offset exactly zero at the moment it was drawn. A stroke whose player is absent or dead on
  the current frame is hidden, never drawn at a guessed position.

### The visibility envelope

Opacity over time is a trapezoid, and **the ramps sit outside the window**:

```
        1.0 ─          ┌────────────────────┐
                      /                      \
        0.0 ─────────┘                        └──────────
             fromTick-fadeIn   fromTick   untilTick   untilTick+fadeOut
```

Full opacity across `[fromTick, untilTick]`; a 0→1 lead-in over the `fadeInTicks` before `fromTick`; a
1→0 lead-out over the `fadeOutTicks` after `untilTick`; zero elsewhere. A null bound is ±∞, so an element
with both bounds null and both ramps zero is simply always visible — which is what makes "all fields
absent" the correct default.

### Stroke geometry

`points` are raw input samples, not the drawn outline. The outline is derived at render time by a port of
[perfect-freehand](https://github.com/steveruizok/perfect-freehand) v1.2.2 with `size = widthWorld`,
`thinning = 0.5`, `smoothing = 0.5`, `streamline = 0.5`, `simulatePressure = true` and round caps. A
reader that wants pixel-identical strokes should use the same library with those options; a reader that
only wants approximate geometry can stroke the polyline at `widthWorld`.

Pressure is `0..1`; devices that report none write `0.5`.

### The authoring cadence (`timing`) — optional

A stroke authored in DemoViewer's **real-time** mode replays at the speed it was drawn at, pauses
included. `timing` is what records that, and **it is absent from every element that does not have one** —
which is every element any other visibility mode produces.

```jsonc
"timing": {
  "runs": [ 0, 0, 41, 96, 41, 160, 78, 214 ],   // [sampleIndex, tickOffset] pairs, sample-ordered
  "durationTicks": 214                          // first sample to last; 0 for an instant stroke
}
```

* **`runs`** is a flat array of pairs — the same flattening `points` uses — read two at a time as
  `sampleIndex` (an index into `points`, counted in *samples*, not in the flat triples) and `tickOffset`
  (ticks elapsed since the stroke's **first** sample, not an absolute tick). It is a **sparse** table: a
  pair is emitted only where the authoring speed changed, so a stroke drawn in one continuous motion
  carries two pairs and one that paused three times carries eight. Offsets between two consecutive pairs
  are linear, so a pair-to-pair span is a constant-speed run and a *repeated* `sampleIndex` — `41, 96`
  followed by `41, 160` above — is a pause: 64 ticks in which the hand did not move.
* **`durationTicks`** is carried separately rather than inferred from the last pair, because it is what
  says the stroke is finished; a reader can use it to skip the table entirely once the elapsed time is
  past it.
* Every offset here is in **`clock.tickRate` ticks**, not in a fixed 64. A reader turning one back into
  seconds divides by the rate the header declares. (DemoViewer wrote these at a hard-coded 64 before
  D8; on a 64-tick parse — which is every one they were written on in practice — the two spellings agree
  exactly, so no existing document is affected.)
* Offsets are **elapsed authoring wall-clock, re-based onto the frame clock at the tick the element
  opens** — deliberately *not* the tick each sample was drawn at, which is frozen while the demo is
  paused. The consequence is intended: a stroke drawn during three seconds of paused thinking replays
  over three seconds of *demo* time, and the replay is a pure function of tick, so it is identical in a
  video export at any frame rate.
* An odd number of values in `runs` is a truncated pair: DemoViewer drops the orphan and keeps the rest.
  It does **not** re-order the table, so a hand-edit that breaks the sample ordering replays oddly rather
  than being silently rewritten.

`timing` is an **additive** field and `schemaVersion` is deliberately still `1`. A document without a
real-time stroke is byte-identical to what earlier builds wrote, and one with a real-time stroke differs
from it only by this object — so bumping the version would announce a break to every reader of every
document, for a field they are already required to ignore.

## Forward compatibility

Both the root object and each element accept unknown fields, and DemoViewer preserves them across a
load → edit → save cycle. A newer build's extra fields therefore survive being opened by an older one.
Readers should ignore fields they do not recognise rather than rejecting the file.

That is not a promise about hypothetical fields — it is what `timing` relies on. A build that predates it
parses a real-time stroke as an ordinary one, keeps `timing` in its unknown-field bag, and writes it back
out on the next save: the user edits a colour in the old build, saves, reopens in the new one, and the
cadence is still there. A third-party reader that round-trips a document is asked to do the same.

`schemaVersion` is advisory: a higher number is read for whatever this build understands, not refused.
