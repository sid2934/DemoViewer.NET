# The Suggested Tags proposals file (schema v1)

Suggested Tags proposes tag instances from an app-side detector pass over a demo's occupancy and events
(rules, not a classifier: a coach can read a definition and change its numbers). This document is for
anyone who wants to read the file the detectors write. It does not cover the **verdicts** file (what a
person decided about each proposal), which is user truth and lives in, and is documented by, the Tag
Store; see [Suggested Tags verdicts](tags-format.md#suggested-tags-verdicts-verdictssha256verdictsjson)
in [`tags-format.md`](tags-format.md). A committed sample of the proposals shape lives under
[`tests/fixtures/suggested-tags/`](../tests/fixtures/suggested-tags/), one small fixture per measured
demo, pinned by `SuggestedTagsGoldenTests`.

The format is modelled on the [annotation sidecar](playback2d-v2/annotations-format.md), which settled
identity, clock and forward compatibility the same way.

## Where the file lives

| File | Desktop | Browser build |
|---|---|---|
| One document per demo the evaluator has built | `<app cache root>/cache/suggestions/<StableKey of the demo path>.json` | Nowhere. Kept in memory for the session; the queue's header says so. |

`StableKey` is a hash of the demo's **path**, not its content (`DemoCacheStore.StableKey`), the same key
every derived cache sidecar uses. The file is rebuilt wholesale whenever the detector-set fingerprint
changes (a profile edit, a learned site-region table changing, or a detector-code version bump); nothing
in it is user truth, and deleting it is always safe: the evaluator writes it again the next time the
demo is considered.

**A citation note.** The Strat Room overview's integrator correction 1 asks Suggested Tags to drop this
directory and write a sibling of the demo's own record instead
(`<cache>/demos/<StableKey>.suggestions.json`, through the same `DemoCacheStore.WriteSibling` seam Grenade
Walk and the Round Index use). The code that ships keeps the dedicated `suggestions/` directory this
document describes. Where the two disagree, this page follows the code; migrating the directory is future
work, not part of this document.

The demo cache's index row carries one mirror field, `SuggestionCount` (the file's pending count), the
way `HighlightCount` mirrors the highlight scan: a surface can show "12 pending" without opening the
file.

## Top level

```jsonc
{
  "schemaVersion": 1,
  "demo":  { "sha256": "...", "stableKey": "...", "fileName": "match.dem", "sizeBytes": 289436777 },
  "clock": { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 154869,
             "firstTick": 1, "lastTick": 132516 },
  "detectorSet": {
    "fingerprint": "...64 hex...",
    "profileId": "team-default",
    "computedAtTicks": 638821234560000000,
    "regions": "learned:4"
  },
  "occupancySource": "round-index",
  "proposals": [ /* … */ ]
}
```

* **`demo.sha256`**: lowercase hex SHA-256 of the `.dem` bytes, filled once Content Identity has hashed
  the demo; null before then. A reader that finds a hash different from what it expects should treat the
  file as built for other bytes and not offer its proposals. **`demo.stableKey`** is the path hash the
  file is named by (see "Where the file lives"); `fileName` and `sizeBytes` are for a person reading the
  file. None of the three besides `sha256` takes part in matching.
* **`clock`**: which parse the ticks were written against, the same `dv-frame-clock` block every
  DemoViewer sidecar carries (see the [annotation sidecar](playback2d-v2/annotations-format.md#top-level)
  and [`tags-format.md`](tags-format.md#top-level)). `firstTick` is the first frame's tick (1 on a Valve
  matchmaking demo) and `lastTick` is the last frame's (`TickCount`); an all-zero clock predates a real
  parse and matches anything.
* **`detectorSet`**: the stamp a rebuild is decided on.
  * **`fingerprint`** is SHA-256 of the parameter profile's JSON, the site-region table composed for the
    map (shipped, learned, or the user's overrides), and a detector-code version, in that order (see
    `SuggestionsFingerprint.Compose`). Any of the three changing marks the file stale.
  * **`profileId`** is the profile's own name (`"team-default"` shipped), for a person reading the file;
    it plays no part in matching.
  * **`computedAtTicks`** is `DateTime.UtcNow.Ticks` at build time, for a person, not a machine.
  * **`regions`** says where the site regions came from: `shipped`, `learned:<n>` (the demo count they
    were learned from), `site-only` (no table at all, the bombsite place alone), each optionally suffixed
    `+profile` when the user's overrides changed at least one site.
* **`occupancySource`**: `"round-index"` when the rounds came from a current Round Index sidecar, or
  `"walk"` when they came from a position walk over the parse (the fallback, and the browser's only
  source). Both produce the same shape; a proposal never says which fed it.

## A proposal

```jsonc
{
  "id": "exec|r12|T|BombsiteA|s=20",
  "detector": "execute",
  "code": "execute",
  "round": 12,
  "roundStartTick": 40960,
  "side": 2,
  "fromTick": 40960, "toTick": 41728,
  "triggerTick": 41344,
  "confidence": 0.78,
  "factors": { "count": 0.8, "touch": 1.0, "utility": 0.9 },
  "labels": { "site": "BombsiteA", "count": "4", "tempo": "rush", "plant": "true" },
  "evidence": [
    { "kind": "occupancy", "tick": 41344, "text": "4 T in BombsiteA+Mini+Hut+Squeaky" },
    { "kind": "event", "tick": 41100, "text": "smokegrenade_detonate by slot 6 -> Hut" }
  ]
}
```

* **`id`** is the identity key a verdict is remembered under, and the only field a re-detection must
  reproduce for a rejection to survive a parameter change: `<prefix>|r<round>|<side>[|<where>][|s=<second>]`,
  joined with `|`. `prefix` is the detector's short name (`exec`, `default`, `fake`, `opener`, `retake`);
  `side` is `T`, `CT` or `?`; `where` is the site, the region, or a fake-to-real pair, omitted when the
  detector has none; the trigger second is floored to a 5-second quantum so a re-run with slightly
  different sampling still lands on the same key. It is **not** stable across a re-parse that renumbers
  rounds: that is what the verdicts file's re-match by nearest trigger tick is for (see
  [`tags-format.md`](tags-format.md#suggested-tags-verdicts-verdictssha256verdictsjson)).
* **`detector`** and **`code`** are the same string today (one code per detector); a reader should treat
  them as independent fields in case a future detector proposes more than one code.
* **`round`** is `ClipRound.Number`, the same numbering every DemoViewer file uses, never
  `m_totalRoundsPlayed + 1`. **`roundStartTick`** is the round's `round_freeze_end`, carried so a UI can
  group proposals by round without re-opening the occupancy the evaluator built them from.
* **`side`**: `2` for T, `3` for CT, matching `player_team`'s wire values everywhere else in this
  project.
* **`fromTick`/`toTick`** are the claim window: where a band would be drawn on the timeline and what an
  acceptance's tag instance spans by default. **`triggerTick`** is the tick the rule actually fired on,
  inside the window, and what the identity key's trailing second is derived from.
* **`confidence`** is `[0.05, 0.99]`, a product (or, for the additive detectors, a sum) of the named
  **`factors`**, so a UI can show why a number is what it is without re-running the detector.
* **`labels`** are what an acceptance would write into the Tag Store's human namespace verbatim (plus a
  `side` label the acceptance adds itself when the proposal does not already carry one). Values are
  always strings, including booleans (`"true"`/`"false"`) and counts.
* **`evidence`** is an ordered, human-readable trail: `kind` is `occupancy`, `event` or `bomb`; `tick` is
  frame clock; `text` is prose for the queue panel, not a machine-parsed field.

## Forward compatibility

The root object, `demo`, `clock`, `detectorSet` and each proposal accept unknown fields, and DemoViewer
preserves them across a load, edit (a rebuild reads nothing from the old file) and save. Because
the whole file is rebuilt wholesale on a fingerprint change, "preserved" here means only "not lost by a
reader that round-trips the file between two builds of the same fingerprint," the way `Extra` bags work
throughout this project's stores. Readers should ignore fields they do not recognise rather than
rejecting the file. `schemaVersion` is advisory: a higher number is read for whatever this build
understands, not refused; a genuinely newer shape than a reader supports is signalled by throwing the
document away and asking the evaluator to rebuild it, never by guessing.

## The parameter profile (`<config>/suggested-tags/profile.json`)

Not one of the two files this page documents (proposals and verdicts), but the third input that decides
what a proposals file contains, so a reader of one usually wants the other close by. It is a plain JSON
document: `schemaVersion`, `id`, the detector run `order`, one object per detector with its tunable
numbers, and a `siteRegionOverrides` map. `DetectorProfile.ToJson()`/`.Parse()` is the shape (writer
comments in `suggested-tags.md` §3.7 show a worked example); a value the file does not name falls back to
the shipped default, so a profile naming only `execute.N` is a whole profile. The Settings app's tuning
section reads and writes this file directly; nothing else needs to.
