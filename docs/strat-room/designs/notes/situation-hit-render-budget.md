# SituationHit render budget: a build note for Result Cards And Walking and Overlay View

**Closes:** `00-overview.md` §6.2, the Round Index thin spot ("`SituationHit` carries ticks only; Overlay
View and Result Cards thumbnails must re-open each hit demo and seek"). · **Tree:** `main` at `d90ec9f`
(0.8.1), CS2DemoKit 0.12.0. · **Written:** 2026-09-24. · **Amends:** `round-index.md` §3.3, §3.4, §3.6
(one sibling file, one fingerprint component). Nothing here is implemented.

**The short version.** Measured, a hit costs 0.6 to 3.3 s to render from the demo (parse 0.44 to 1.2 s,
then a from-zero tracker replay that grows linearly with the tick, 0.14 to 2.1 s), holds 300 to 480 MB
of managed heap while it does, and the render itself is 0.5 ms. Forty cards are 24 to 130 s of serial
work, and a stacked overlay needs every matched step, which is a full walk per demo, not a seek. The
smallest fix is to keep the ten positions per row that `RoundIndexBuilder`'s walk already has in hand and
write them beside the sidecar (`.dvrp.json.gz`, 56 to 60 KB per demo), so a card thumbnail is a
`Scene2DFrame` built from ten tuples and rendered in under a millisecond, and the overlay opens no demo
at all. A background thumbnail queue is then unnecessary; a render budget is still stated (§4) for the
fallback path, which is the click-to-seek the plan already accepts.

---

## 1. What a consumer pays today

`SituationHit` is `(DemoStableKey, Map, RoundNumber, FreezeEndTick, FirstMatchTick, LastMatchTick,
MatchedSteps)` (`round-index.md` §3.7). To draw anything from it, a consumer takes the `dv2d render`
path the plan names for thumbnails: `DemoInput.Load` (`File.ReadAllBytes` + `DemoParser.Parse`), then
`TrackerFrameSource.Prepare`, which is `new EntitySeekService(() => new EntityTracker())
.SeekToFrameNoSnapshot(frameIndex, frames)` (`TrackerFrameSource.cs:241-252`). The packaged doc for
`EntitySeekService` says what that is: "Every call replays from frame 0 ... For a forward-only walk over
many ticks it is quadratic". The seek is therefore linear in where the hit sits in the demo, and there is
no checkpoint to land on.

## 2. Measured (scratch project `scratchpad/hit-render`, 2026-09-24)

Three untrimmed Valve matchmaking demos from the Steam `replays` folder, the same three the Round Index
measured, Release build, page cache warm (a cold read adds the disk time for 180 to 280 MB). Targets
are 30 s into round 2, the middle round and the last round: a hit's `FirstMatchTick - 10 s` lands
anywhere in that range. `new EntityTracker()` is what `TrackerFrameSource` uses; `CreateCurated()` is
what the index build uses. Both are shown because the curated store was not faster on a seek.

| Demo | Map | Frames | Read | Parse | Heap held by the parse | Seek to round 2 | Seek to the middle round | Seek to the last round |
|---|---|---|---|---|---|---|---|---|
| `…1024675027_129` | de_nuke | 154,869 | 47 to 50 ms | 774 to 1,202 ms | 484 MB (+276 MB file bytes) | 200 to 229 ms | 260 to 277 ms (745 first call) | 482 to 621 ms |
| `…0377894676_389` | de_dust2 | 106,901 | 25 to 56 ms | 537 to 555 ms | 297 MB (+178 MB) | 187 to 549 ms | 651 to 986 ms | 1,048 to 1,385 ms |
| `…0260929275_408` | de_mirage | 118,309 | 35 to 66 ms | 439 to 624 ms | 344 MB (+209 MB) | 138 to 535 ms | 849 to 1,060 ms | 1,491 to 2,068 ms |

Reading the ten `(slot, team, alive, place, x, y, z)` tuples off the seeded tracker
(`PawnLookup.ForEachLivePawn` + `PositionUtil.CellToWorld`) is 30 to 100 µs after the first call
(2 ms, JIT). The nuke demo's seek is cheaper per frame because build 10231 packs up to 39 frames per
tick (`round-index.md` §2.6); the two 10896 demos are the shape of the owner's corpus.

**Render, measured separately.** `dv2d render --demo <nuke> --tick 67760 --size 320x180 --cpu`:
1,456 to 1,583 ms elapsed, of which parse 554 to 617 ms; at 1920x1080 it is 1,602 ms. The same command
on a fixture (`duel-mirage-b.scene.json`, no demo) is 158 ms at 320x180 and 256 ms at 1080p, most of it
process start and asset load. In process, `dv2d bench --fixture duel-mirage-b --size 320x180 --cpu`
renders at p50 0.474 ms, p99 0.516 ms per frame. The renderer is not the cost; the demo is.

**The bar, against these numbers.** "Forty results walk in under two minutes of user time" is 3 s per
card. Serially, forty thumbnails from the demo are 40 × (0.6 to 3.3 s) = 24 to 130 s of background work
before the last card has a picture, and hits cluster by demo so the parse can be shared but the seek
cannot (each hit is its own from-zero replay). In parallel it is bounded by memory, not cores: the gate's
own comment says "two concurrent multi-GB parses OOM a 16 GB box" (`HeavyJobGate.cs:41`), and each
parse here holds 0.5 to 0.8 GB. Overlay View is worse: it needs positions at every matched step of every
hit (`MatchedSteps` is tens per round), and a from-zero seek per step is quadratic, so the honest cost is
a forward walk per demo, which is the index build's own 1.0 to 1.6 s on top of the parse: forty demos
are 60 to 110 s and forty parses of churn to draw one heatmap. Neither item is budgeted by the
design as it stands.

## 3. Proposal: the snapshot the walk already has

The builder (`round-index.md` §3.4 step 3) iterates `PositionSampler.Walk(demo, 4, int.MaxValue)`, and
each `PositionSample` carries `PlayerSlot`, `Position` and `Place` (packaged doc). When a row closes, the
last sample per alive slot at the sampled tick is exactly the tuple a thumbnail or a heatmap needs. Keep
it. Measured in the probe over the same walk: 1,282 to 1,570 rows per demo, 6.2 to 6.6 alive tuples
per row (the dead are dropped by the token's own alive rule, so a picture always agrees with its token).

**Shape.** A sibling file, `<config>/cache/round-index/<StableKey>.dvrp.json.gz` ("round positions"),
written by `RoundIndexEvaluator.Evaluate` in the same pass, before the `.dvri.json`, with the same
atomic idiom (a crash between them leaves "not indexed", never a stamp without both files):

```jsonc
{
  "schemaVersion": 1,
  "fingerprint": "ri1;cadence=1;token=1;rf=1;src=pawn;pos=1",   // identical to the .dvri.json it belongs to
  "demo": { "stableKey": "3f9c…", "sha256": null },
  "cadenceTicks": 64,
  "places": ["Outside", "Lobby", "Ramp", "?"],                 // per-document place table; "?" is the null place
  "rounds": [
    { "number": 3, "freezeEndTick": 10746, "ct": [0, 2, 5, 7, 9],   // slots on CT this round, from Round Facts Slots
      "pos": [                                                     // indexed by step; tick = freezeEndTick + step * cadenceTicks
        [[0, -448, 1180, -416, 0], [2, -129, -1848, -416, 0], /* … alive slots only */],
        [[0, -401, 1130, -416, 0], /* … */]
      ] }
  ]
}
```

Each tuple is `[slot, x, y, z, placeId]` with world units rounded to integers (a marker at 320x180 is
about 30 units wide; Z is kept for level selection, `MapSpace.QuantizeZ`, as the sidecar's `places`
block already does). Step indexing means no step number is written; a hit maps its ticks with
`step = (tick - FreezeEndTick) / cadenceTicks`, the inverse of §3.1.

**Size, measured on the three demos (compact JSON, the shape above):** 168 to 213 KB per demo, 134 to
140 bytes per row; **56 to 60 KB gzipped**. Storing only the first step of each token run (the row the
index keys) is 119 to 133 KB / 38 to 41 KB but useless to the overlay, since players move inside a
run; a binary form (int16 x, y, z, byte slot and place) is 65 to 77 KB uncompressed, which gzip already
beats, so the file is JSON like everything else in the cache. Written gzipped, the file is about the size
of the sidecar it sits beside: a 277-demo library adds 16 MB (57 MB unzipped); a thousand demos 58 MB.
The zone asset pipeline already reads `.json` and `.gz` by path (`da85df0`), so the reader is one line.

**Why a sibling file, not schema 2 of `.dvri.json`.** `SituationIndex` deserializes every sidecar at
startup (2.0 ms per demo measured on 171 KB indented files, `round-index.md` §2.7). Positions in the same
document would double or triple that read for a surface that never looks at them, pulling the §4 revisit
trigger (5 s of startup) forward from about 2,500 demos to under a thousand. A sibling is read only per
hit, by consumers, and costs the loader nothing. The price is one more file per demo for the orphan
sweep and the `Changed` subscriber to delete (§3.6, "Demo removed from the library"), one line each.

**Fingerprint.** `RoundIndexFingerprint.Current` gains `;pos={PositionSchema}` (`pos=1`: integer
world units, alive slots only, last sample at the sampled tick). Anything that changes what a tuple
means is in it; the thumbnail size, camera and palette are not. A `pos` bump re-enters every row into
the backlog exactly like a cadence change, and since one walk yields both files there is no cheaper
partial rebuild to design. Old sidecars carry no `pos=` and mismatch, so "never indexed" and "indexed
before positions existed" are the same state, which is what the fallback in §4 relies on. The record
stamp (`RoundIndexFingerprint`, `RoundIndexRowCount`) is unchanged.

**Build cost.** Reading tuples per row in the probe was 30 to 100 µs, so under 0.2 s on the longest demo
over a walk that already costs 1.0 to 1.6 s; the gzip of 200 KB is a few milliseconds. The index rate in
`round-index.md` §9 step 5 absorbs it.

**Reader.** `Services/RoundIndex/RoundPositionStore.cs`: `RoundPositions? TryRead(string stableKey,
string expectedFingerprint)`, returning null on a missing file, a fingerprint mismatch or a sha256 that
disagrees with the record (the `annotations-format.md` rule the sidecar follows). `ISituationIndex` does
not change. Consumers hold a small LRU keyed by stable key because hits cluster by demo: forty hits are
typically ten to twenty files, each 1 to 2 ms to read and inflate.

## 4. The budget

| Path | Per hit | Forty hits | Demo opens |
|---|---|---|---|
| Thumbnail from `.dvrp.json.gz`: build a `Scene2DFrame` from the round's tuples at `FirstMatchTick`'s step (`PlayerMarker` per alive slot, team from `ct`, no yaw, `ring: Team`), render through `Scene2DRenderer` at 320x180 | 1 to 2 ms read, amortised per demo; 0.5 ms render; PNG encode a few ms | well under 100 ms, on one worker, as the result set lands | 0 |
| Overlay from the same file: every step in `[FirstMatchTick, LastMatchTick]` of every hit, alive `(x, y)` per side into the heatmap layer | tens of steps × 6 to 7 tuples | about 20,000 points, milliseconds | 0 |
| Click to seek playback (the plan's own behaviour): parse + seek on the interactive path | 0.6 to 3.3 s, page cache warm | paid only for cards the user opens, one at a time | 1 per click |
| Fallback: a hit whose demo has no positions file (indexed before `pos=1`, or a stale fingerprint) | the card shows the token text and the round facts with a placeholder tile; no re-open | the strip's stale count already says how many, and "Rebuild index" is the remedy | 0 |

The rule that makes the bar hold: **a card never opens a demo to draw itself.** A picture comes from the
positions file or it is a placeholder, and the only demo open in the Situations tab is the one the user
clicked, on the playback path, with the progress ring the interactive open already has. The plan's
"cached thumbnails" become a memory cache of PNGs keyed by `(stableKey, round, step)`, dropped with the
result set; nothing is written to disk for thumbnails, and no background queue is needed, because forty
renders at 0.5 ms are cheaper than scheduling them. Thumbnails render on a worker off the UI thread in
one batch per result set, so walking with `J`/`K` never waits on a render.

A thumbnail drawn this way lacks what the tracker path has: yaw, ring state, weapon, grenade trails,
smokes. For a card that is the point: it shows the arrangement the token matched, nothing else, and it
is byte-for-byte consistent with the token because both come from the same row. The full scene is one
click away.

## 5. Alternatives considered

| Alternative | Why not |
|---|---|
| A background thumbnail queue over the demo path, cached PNGs on disk | 24 to 130 s of background work per result set, 0.5 to 0.8 GB held per parse, one or two at a time; the first page of cards is blank for a while on every new query; the overlay still needs a walk per demo. It fixes nothing for Overlay View. |
| Checkpointed trackers (engine change: periodic tracker snapshots so a seek is a short replay) | Real, and worth an upstream proposal for the interactive seek in its own right, but it still needs the parse (0.44 to 1.2 s and the heap) per demo, and the overlay's per-step reads are then many short replays instead of one walk. It does not remove the demo open, which is the cost. |
| Positions inside `.dvri.json` as schema 2 | Startup loader reads them for nothing (§3, "why a sibling file"). |
| Per-slot rows in the token index (`round-index.md` §4, "per-slot rows") | Five times the postings for an identity the search never queries; the positions file gives the consumers the per-slot data without touching the index. |
| Store positions only at run starts | 30 percent smaller; wrong for the overlay and for a card whose `FirstMatchTick` is mid-run. |
| Quantise x, y to 8 units, or drop Z | Saves under 10 percent after gzip; Z is what picks the level on nuke and vertigo. |

## 6. Tests

- `RoundPositionsSnapshotTests` (App): a committed fixture pins the serialized shape, the way
  `AnnotationSchemaSnapshotTests` pins `.dvann.json`.
- `RoundPositionsConsistencyTests` (App, real demo, the nuke replay above): for every round and step,
  `PlaceCountToken.Encode` over the positions file's tuples (grouped by side from `ct`, place from the
  table) equals the token stored in `.dvri.json` at that step. Same function applied twice, the property
  the Query Canvas round-trip already relies on.
- `RoundIndexEvaluatorTests`: a crash injected between the two writes leaves no stamp; an old sidecar
  without `pos=` is stale, not corrupt; deleting a library path removes both files.
- `ResultCardThumbnailTests` (App, synthetic): a card built from a hit with no positions file renders the
  placeholder and never constructs a `TrackerFrameSource` (assert through the seek factory, as the
  export session's strict source does).
- Corpus budget: forty hits from the owner's replays folder thumbnail in under 100 ms on the worker,
  measured and written into this note at Result Cards' step 5.

## 7. Sources

`docs/strat-room/designs/round-index.md` §2.6, §2.7, §3.1, §3.3, §3.4, §3.6, §3.7, §4, §9;
`00-overview.md` §6.2; `plan.md` Result Cards And Walking (`:307-311`), Overlay View (`:313-314`).
`tools/DemoViewer.NET.Playback2D.Cli/RenderCommand.cs`, `SceneProvider.cs` (`DemoSceneProvider.Open`),
`DemoInput.cs`; `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Frames/TrackerFrameSource.cs`
(`:236-252`); `src/Playback2D/DemoViewer.NET.Playback2D.Core/Scene2DFrame.cs`, `PlayerMarker.cs`;
`src/App/DemoViewer.NET/Services/HeavyJobGate.cs` (`:37-43`), `Services/DemoProcessing/IDemoEvaluator.cs`
(`:34-40`); `tests/fixtures/playback2d/scenes/duel-mirage-b.scene.json`. CS2DemoKit 0.12.0 packaged XML
docs: `EntitySeekService` (cost paragraph), `PositionSample`, `PositionSampler.Walk`.
Measurements: scratch project `<session scratchpad>\hit-render` (`Program.cs`, `seek.log`,
`out/dv2d-duel-mirage-b_20260924-113043.json`), Release, run 2026-09-24 against the three replays named
in `round-index.md` §10; `dv2d` from `artifacts/bin/DemoViewer.NET.Playback2D.Cli/release`. Not
committed; re-creatable from this note. The tour sample was not used.
