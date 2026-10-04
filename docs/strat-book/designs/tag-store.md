# Tag Store: design

**Work item:** Tag Store (Phase 2, Round Tagger) · **Kind:** design, review required · **Depends on:**
Content Identity · **Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0 · **Date:** 2026-09-23 ·
**Status:** approved 2026-09-24 (see the banner below). Nothing here is implemented.

This is the persisted schema and the service behind the Round Tagger: the file that holds what a
human (or a detector) said about a stretch of a demo, and the API that reads it back for the
timeline, the Matrix and Watched Situations. It is modelled
on `docs/playback2d-v2/annotations-format.md`, because that sidecar already solved identity, clock,
forward compatibility and the read-only-replay-folder problem, and every one of those problems is the
same here.

---

> **Status: APPROVED 2026-09-24**, as recommended: D1 (config root keyed by hash), D3
> (`clampToRound: true`), D4 (hash helper in `Playback2D.Pipeline`, Content Identity's call), D6 (keep
> rejected proposals so detectors do not re-propose; verdict location per integrator correction 2).
> D2 and D5 withdrawn with the XML exporter. Nothing here is implemented.
>
> **Revision note (2026-09-24, review).** The XML export in the Sportscode shape is **removed
> from v1**. "Sportscode XML" is a vendor format with no published specification and no licence text,
> not an open standard, and no user or workflow of this project consumes it today. The format of
> record is this design's own `.dvtag.json` (MIT-licensed with the repository, published like the
> annotation sidecar). The three conventions that make a future exporter a one-day task are kept in
> the data model, and §3.8 now records the field mapping and the trigger for building it. Decisions
> D2 and D5 are withdrawn; the integrator's action O-33 goes with them.

## 1. Problem and scope

The Round Tagger uses the two-object model every sports-analysis package converges on: a **code**
creates a **timeline instance** (a named span: "A execute", 1:12 to 1:31 of round 7), and **labels**
attach key/value metadata to an instance ("outcome = won", "site = A"). Hudl Sportscode, Nacsport and
the open kloppy serializer all use exactly this shape (§2.6), which is why the original roadmap asks for it
rather than for a bespoke bookmark list. The shape is adopted; the vendor file format is not (§3.8).

DemoViewer has no instance store. It has three per-demo stores, none of which fits:

| Store | Why it does not fit |
|---|---|
| `DemoCacheStore` | A rebuildable cache, "never a source of truth" (`DemoCacheStore.cs:24-26`); identity drift discards every tier (`DemoCacheStore.cs:266-270`); I/O failures are swallowed. Human work cannot live under those rules. |
| `.dvann.json` annotations | Right shape (identity, clock, unknown-field bag), wrong content: elements are strokes with geometry, not spans with metadata, and the document is per demo with no cross-demo reader. |
| `SessionState.json` bookmarks / `GraphBreakpoints.v2.json` | Keyed by path or by hash, one flat file for the whole library, rewritten on every change. |

**In scope.** The sidecar schema (instances, labels in two namespaces, notes, positions and
movements); the palette definition format; the store service (location, atomic writes, index,
in-memory on the browser host); the session model with undo; the query API the Matrix and Watched
Situations consume; the Tag Track as an `ITimelineTrack`; the interchange mapping kept for a future
exporter (§3.8); the exact ask on Content Identity.

**Out of scope.** The palette UI, hotkey routing, Label Mode, Click To Tag Position gestures, The
Matrix screen and Suggested Tags detectors are their own build items in `plan.md` §3. This document
gives them the data they store and the calls they make, nothing more. Importing or exporting
Sportscode XML is not in scope for v1 (§3.8 says why, and what would trigger it).

---

## 2. What exists today (cited)

### 2.1 The annotation sidecar is the template

`AnnotationStore` (`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationStore.cs`)
resolves a location in two steps: beside the demo when its directory passes a create-and-delete probe,
else `<config>/annotations/<sha256>.dvann.json`, else nowhere (`AnnotationStore.cs:120-146`,
`:367-395`). The probe is cached once per directory per session (`:367-390`). On load, a hash mismatch
means "someone else's demo at this path": the file is ignored and never overwritten
(`AnnotationStore.cs:196-215`). A clock mismatch is a warning, never a discard (`:217`). Unknown JSON
at the root and per element is kept in a bag and re-emitted on save (`AnnotationStore.cs:41-47`,
`:311-323`). Writes are temp-file plus `File.Move(temp, path, true)` (`:325-349`). The DTOs are
source-generated (`AnnotationJsonContext`, `:175`), which matters for the browser head (§2.7).

`DemoIdentity` and `ClockIdentity` (`AnnotationIdentity.cs:17`, `:33`) are the two headers. The App
builds the clock as `new(DvFrameClock, ctx.TickRate > 0 ? ctx.TickRate : 64, ctx.TotalFrames, 0, 0)`
(`src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs:1133-1134`), so `firstTick`
and `lastTick` are zero in every document DemoViewer writes today. `ClockIdentity.Matches` treats an
all-zero clock as unknown and never warns against it (`AnnotationIdentity.cs:43-65`).

The schema is pinned by `AnnotationSchemaSnapshotTests.V1Schema_MatchesCheckedInSample`
(`src/Playback2D/DemoViewer.NET.Playback2D.Tests/AnnotationSchemaSnapshotTests.cs:29-73`): load the
committed sample through the real store, save it, compare bytes, regenerate only under
`PB2D_GOLDEN_UPDATE=1`. The sample is `tests/fixtures/playback2d/annotations/schema-v1.sample.json`.

### 2.2 The cache has the storage shape but not the semantics

`DemoCacheStore` (`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs`) is
`<config>/cache/index.json` plus `<config>/cache/demos/<key>.json`, one sidecar per demo, index
loaded at start, sidecars read lazily (`:11-32`). Sidecars are named by a hash of the lower-cased
**path**, not by content, because the content hash is not known at identity tier
(`:491-511`). Writes are atomic through a `.dc-<guid>.tmp` and `File.Replace` (`:559-573`). The
whole store is in-memory when the root is null, which is the browser host (`:48-59`, `:513-524`).
Read-modify-write cycles are serialized by `_rmwGate` because two tier writers race on one record
(`:63-75`). `Changed` carries the demo path or null for a batch (`:157-170`). `LoadRecords` is the one
cross-demo reader and was measured at ~32 ms warm, ~297 ms cold over 348 demos, 3.0 MB
(`:283-299`).

`DemoCacheRecord.Sha256` exists and is "null until something has computed it" (`DemoCacheModels.cs:215-216`).
The library indexer computes it only for files that share a byte size with another file
(`src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs:860-863`, `GetOrComputeSha` at
`:912-929`, streaming SHA-256 at `:932-940`) and writes it to the legacy `DemoLibraryCacheEntry`, from
which `LegacyCacheMigration` copies it onto the record (`LegacyCacheMigration.cs:97`, `:205`). The
tier-2 dual write (`DemoLibraryService.cs:1302-1348`) never sets it. So today most records have no
hash. Finding F9 and the Content Identity item exist for exactly this reason.

`CachedRound` is `Number` plus `StartTickFrameClock` (`DemoCacheModels.cs:84-89`), and
`ToClipRounds` adapts it to `CS2DemoKit.Analysis.Clips.ClipRound` with no clock change (`:96-120`).
That is enough to put a round number on an instance.

### 2.3 Three hash helpers agree, and none is shared

`AnnotationStore.ComputeDemoKey` (streaming, `AnnotationStore.cs:97-115`),
`GraphBreakpointStore.ComputeDemoKey(ReadOnlySpan<byte>)`
(`src/App/DemoViewer.NET/Services/GraphBreakpointStore.cs:203-204`) and
`DemoLibraryService.HashFileStreaming` (`DemoLibraryService.cs:932-940`) all produce lowercase-hex
SHA-256 of the file bytes. `MainViewModel` already hashes the raw bytes of every demo it opens
(`src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs:2641`, again at `:3734`) to key graph
breakpoints, so the open-demo path has the key in hand and does not publish it to modules:
`IModuleContext` exposes `DemoPath`, `MapName`, `TickRate`, `TotalFrames`, `FrameIndexAtTick`
(`src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs:16-27`, `:82`, `:190`) and no hash.

### 2.4 The timeline contract

`ITimelineTrack` (`src/Playback2D/DemoViewer.NET.Playback2D.Core/Timeline/ITimelineTrack.cs:22-41`)
is `Id`, `DisplayName`, `IsAvailable(data)`, `BuildMarkers(data)` ("point markers, ascending by frame
index"), `BuildBands(data)` ("range bands, ascending and **non-overlapping**") and `MarkersChanged`.
`TimelineMarkerKind.Custom` exists (`:15`). `TimelineBand` is `(TrackId, StartFrameIndex,
EndFrameIndex, Label, Tooltip, Argb)` and `TimelineMarker` adds `Tick`, `Kind`, `Glyph`
(`TimelineMarker.cs:11-30`); ARGB 0 means "host default". Tracks live on the **frame-index axis** and
convert ticks once through `ITimelineData.FrameIndexAtTick`, dropping anything that resolves to -1
(`AnnotationTrack.cs:13-18`, `:101-105`). App-side tracks (`RoundTrack`, `KillTrack`, `BombTrack`) sit in
`src/App/DemoViewer.NET/Modules/Playback2D/Timeline/` and register through
`Playback2DTimelineViewModel.RegisterTrack` (`Playback2DTimelineViewModel.cs:148-172`), which also
subscribes `MarkersChanged` so a track whose content grows while the demo sits still is re-queried
(`:140-147`). A click raises `SeekRequested(frameIndex)` and the tab forwards it to
`IModuleContext.RequestSeekToFrame` (`:22-23`, `:314-324`). The round band seeks to its first frame.

### 2.5 Undo, autosave and the session controller

`AnnotationDocument` keeps an undo stack of gesture steps, each an invertible `DocDelta`
(`Add | Remove | Replace | Batch`, `DocDelta.cs:15-60`), capped at 200 entries
(`AnnotationDocument.cs:37-41`), and refuses to undo while a gesture is open (`:186-208`).
`AnnotationSessionController` (`src/App/DemoViewer.NET/Modules/Playback2D/Annotations/`) debounces
autosave off the UI thread, snapshots the element list and a version stamp before awaiting so a
slower writer stands down (`:653-672`), never writes an empty sidecar that did not already exist
(`:674-680`), and computes `DemoIdentity` lazily on the first save (`:682-687`). The browser status
line reads "session only: this browser tab forgets annotations when it reloads" (`:779`).

### 2.6 The sports-analysis interchange shape (researched)

The de-facto interchange file is "Sportscode XML". Verified from primary sources:

* kloppy's serializer (`kloppy/infra/serializers/code/sportscode.py`): root `<file>` containing
  `<ALL_INSTANCES>`; each `<instance>` carries, in order, `<ID>`, `<start>`, `<end>`, `<code>`, then
  zero or more `<label>` elements each with `<group>` and `<text>`. `start` and `end` are **seconds as
  floats** from the start of the video (`str(relative_period_start.total_seconds() +
  code.start_timestamp.total_seconds())`). IDs default to a 1-based counter. A list value writes one
  `<label>` per item, all with the same `<group>`. A boolean `True` writes a `<label>` with `<text>`
  only and no `<group>`.
* kloppy's fixture `kloppy/tests/files/code_xml.xml` shows the same, with string IDs (`P1`), e.g.
  `<instance><ID>P1</ID><start>3.6</start><end>9.7</end><code>PASS</code><label><group>Team</group><text>Henkie</text></label>…</instance>`.
* kloppy issue #616 records that Sportscode itself writes **several labels sharing one group** as
  repeated `<label>` elements, and that a reader collapsing them to a dictionary loses values. Our label
  model is a list for this reason (§3.2).
* Hudl's support article "Import or Export a Sportscode XML" and Nacsport's "Nacsport and Sportscode:
  using both together" both state XML is the format each imports from the other. The Nacsport Pro and
  Elite manuals list XML import/export and Sportscode `.TLcodes` import.

Not verified from a primary source: the optional `<ROWS><row><code/><R/><G/><B/></row></ROWS>` colour
block and a `<SESSION_INFO>` block that appear in real Sportscode exports.

**Standard status (added at review).** "Sportscode XML" is Hudl's own timeline export. There is no
published specification, no version number and no licence text of any kind; Hudl's support article
documents the import/export workflow, not the format, and every third party that reads or writes it
(Nacsport, Catapult, kloppy) reverse-engineered the shape. Writing files in that shape needs no
licence, since nobody licenses a file layout to writers, but it is a vendor format, not an open
standard, and no open standard for video-coding instance/label interchange exists in sports
analysis. kloppy is an open-source implementation of the shape, not a standard. This is why §3.8
defers the exporter rather than shipping it.

### 2.7 The browser host

`AppPaths.ConfigRoot` is null on the browser (`src/App/DemoViewer.NET/Services/AppPaths.cs:54-66`),
every store degrades to in-memory, and `docs/playback2d-v2/wasm-matrix.md` records that only
annotations and the keybinding editor tell the user so. It also records that `PublishTrimmed=false`
is the price of reflection-based `System.Text.Json` in eleven stores, and asks that new stores be
source-generated.

### 2.8 Themes are the precedent for data in the config dir

`AppPaths.ThemesDirectory` is `<config>/themes/`, created at startup, scanned for `*.json` in
filename order by `ThemeRegistry.LoadUserThemes` (`src/App/DemoViewer.NET/Theming/ThemeRegistry.cs:175-215`),
a bad file skipped rather than fatal. Palettes follow this exactly (§3.4).

### 2.9 Measured (scratch project `tag-store-scan`, 2026-09-23)

| What | Result |
|---|---|
| 1000 synthetic sidecars, 60 instances each (2 human labels, 5 fact labels, a note on every seventh), indented JSON | **49.9 KiB per demo, 48.7 MiB total** |
| Full scan and code × fact pivot over all 1000 (`System.Text.Json` reflection, Release) | **222 ms warm**; 3.7 s on the first pass (JIT plus fresh-file antivirus scan) |
| SHA-256, `match730_003774064632172380508_1522348072_129.dem`, 524.3 MiB, Valve matchmaking, Steam replays folder | **722 ms** (725 MiB/s, page-cache warm) |
| SHA-256, `match730_003766481163036655836_1191991072_392.dem`, 493.7 MiB | **642 ms** |

A thousand tagged demos is an upper bound for a single team's library; the warm scan is under the
one-second bar the Round Index sets for itself, and well inside what the Matrix needs at 100 demos.
D2 (JSON versus SQLite) therefore does not need to be decided for this store (§4.1).

---

## 3. Proposed design

### 3.1 Files and locations

```
<config>/tags/
  index.json                      the always-loaded projection (rebuildable from the sidecars)
  demos/<sha256>.dvtag.json       one document per demo, the source of truth
<config>/palettes/
  <name>.tagpalette.json          user palettes (drop-in, like themes)
<DemoViewer.NET.dll resource>     the built-in default palette
```

**Sidecars live under the config root, keyed by content hash, always.** Not beside the demo. This is
the one place this design departs from the annotation store, and §4.2 gives the reasoning; §8 D1
covers the decision. The writable-probe rule stays available as the alternative.

`index.json` follows `DemoCacheIndexFile` (`DemoCacheModels.cs:447-465`): a version, a list of small
rows. It is derived and rebuilt from `demos/` when missing or corrupt, so its loss costs a directory
scan and nothing else.

```jsonc
{
  "version": 1,
  "entries": [
    { "sha256": "…", "fileName": "match730_….dem", "instanceCount": 41,
      "codes": ["A execute", "Default", "Retake B"],
      "modifiedUtc": "2026-09-23T14:02:11Z", "tickRate": 64 }
  ]
}
```

On the browser host `tagsRoot` is null and the store is a dictionary, exactly as
`DemoCacheStore._memoryRecords` (`DemoCacheStore.cs:59`). The Tag Palette's status line says "session
only: this browser tab forgets tags when it reloads", the annotation wording with one word changed,
and `wasm-matrix.md` gets a "Tags" row under **Degraded** that says so.

### 3.2 The document (`.dvtag.json`, schema v1)

```jsonc
{
  "schemaVersion": 1,
  "demo":  { "sha256": "…64 hex…", "fileName": "match730_….dem", "sizeBytes": 549715968 },
  "clock": { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 223114,
             "firstTick": 0, "lastTick": 0 },
  "palette": "cs2-default",                    // palette id the session used last; advisory
  "instances": [
    {
      "id": "6f1c…-…",                         // GUID, stable across edits
      "code": "A execute",                     // free string; the palette is not required to exist
      "fromTick": 41216, "toTick": 42432,      // FRAME-CLOCK ticks, inclusive, fromTick <= toTick
      "round": 7,                              // derived (see 3.3); null when unresolvable
      "createdUtc": "…", "modifiedUtc": "…",
      "source": "human",                       // "human" | "suggested" | "import"
      "suggestion": null,                      // present only when source == "suggested" (3.3)
      "labels": [                              // HUMAN namespace, ordered, groups may repeat
        { "group": "outcome", "value": "won" },
        { "group": "site",    "value": "A" },
        { "group": "",        "value": "sloppy" }        // a bare label: no group
      ],
      "facts": [                               // PARSER namespace, rewritten wholesale on refresh
        { "group": "side", "value": "T" },
        { "group": "buy.us", "value": "full" },
        { "group": "buy.them", "value": "force" },
        { "group": "score", "value": "6-4" },
        { "group": "endReason", "value": "bomb" },
        { "group": "plantSite", "value": "A" }
      ],
      "factsStamp": { "schema": 1, "computedUtc": "…", "stale": false },
      "note": "smokes were 1s late; the B player rotated before the flash",
      "positions": [                           // 3.6; empty array when none
        { "x": -1180.5, "y": 2044.0, "levelMinZ": -384, "tick": 41600,
          "place": "BombsiteA", "placeSource": "zone-bake:1" }
      ],
      "movements": [
        { "from": { "x": -220, "y": 1610, "levelMinZ": -384, "tick": 41300, "place": "Ramp", "placeSource": "pawn" },
          "to":   { "x": -1180.5, "y": 2044.0, "levelMinZ": -384, "tick": 41600, "place": "BombsiteA", "placeSource": "zone-bake:1" } }
      ]
    }
  ]
}
```

**Rules, in the annotation sidecar's words where they apply.**

* `demo.sha256` is the only field that takes part in matching. `fileName` and `sizeBytes` are for a
  human. A document whose hash differs from the demo the reader is on is ignored and never
  overwritten. Because the file is named by its hash, this only happens through a hand-edit.
* `clock` is the frame clock (F15). `fromTick`/`toTick` are the same tick values the annotation
  sidecar, `CachedRound.StartTickFrameClock` and `CachedHighlightEvent.Tick` carry. A clock mismatch on
  load is a warning surfaced on the Tag Palette status line, never a discard. Nothing in the file is a
  frame index: frame indices shift on re-parse, ticks do not.
* **Two namespaces.** `labels` is human. `facts` is parser-derived and is the property of the refresh
  pass (§3.3): a re-parse or a Round Facts schema bump rewrites the whole `facts` array without reading
  `labels`, and the palette UI cannot edit `facts`. The two arrays have the same element shape so the
  query API treats them as one label set with a namespace bit.
* Labels are an **ordered list of `{group, value}`**, not a dictionary. A group may repeat (the
  Sportscode lesson, §2.6). `group` may be empty, which is a bare label. `value` is always a string;
  numbers are written as invariant-culture text, booleans as `"true"`/`"false"`.
* `round` is an int derived from `DemoCacheRecord.Rounds` at creation (`ClipWindows.RoundStartFor` over
  `ToClipRounds()`, the same call the reel uses at `AddClipsPickerViewModel.cs:602`) and re-derived on
  refresh. It is stored, not recomputed on read, so the Matrix can pivot on it without opening the cache.
* `note` is free text, nullable; absent when null (`WhenWritingNull`, the annotation `timing` rule).
* `source` says who made the instance. `import` is reserved for a future importer (§3.8).
* Every object (root, instance, label, position, movement, suggestion) carries a
  `[JsonExtensionData]` bag. Unknown fields survive load → edit → save. `schemaVersion` is advisory: a
  higher number is read for what this build understands.
* Serialization is through a source-generated `TagJsonContext` (§2.7), `WriteIndented = true`,
  `DefaultIgnoreCondition = WhenWritingNull`, property order fixed by declaration order so the golden
  test is byte-stable.

### 3.3 Derived fields and the refresh pass

Three things on an instance are computed from the demo rather than typed: `round`, `facts`, and
`positions[].place` / `movements[].*.place`. They share one rule: **a refresh may overwrite them, and
only them, and does so outside the undo history.**

`TagFactsRefresher` (App service) subscribes to `DemoCacheStore.Changed(path)`. When the changed
record's Round Facts tier is present and newer than the document's `factsStamp.computedUtc`, it maps
`path → sha256` through the cache index, opens the document through `TagStore.Update` (§3.5, which
routes to the live session if one holds that demo), and for every instance sets `round` from the
round containing `fromTick`, replaces `facts` with what Round Facts reports for that round and side,
and stamps `factsStamp`. An instance whose `fromTick` falls in no round keeps its old `facts` with
`stale: true`. The same pass fills `place` on any position whose `placeSource` is null or an older
zone-bake version than the current bundle (§3.6).

What Round Facts must expose for this to work is one call (§5, interface needed):
`IRoundFactsSource.FactsFor(string demoPath, int roundNumber) → IReadOnlyList<(string group, string value)>`
plus a `Schema` int. The group vocabulary (`side`, `buy.us`, `buy.them`, `score`, `endReason`,
`plantSite`, `plantTick`, …) is Round Facts' to define; this document only fixes that they are strings and
that they live in `facts`, never `labels`. "Us" and "them" in `buy.us`/`buy.them` are relative to the
side the instance's `side` names, so an instance is self-contained without Team Identity; Team
Identity adds an `opponent` fact when it lands.

Suggested Tags writes instances with `source: "suggested"` and

```jsonc
"suggestion": { "detector": "site-commit", "confidence": 0.82, "state": "proposed" }
```

`state` is `proposed | accepted | rejected`. Accepting flips `state` and leaves `source` as
`suggested` so the Matrix can still stratify by provenance. Rejected proposals are kept so the detector
does not re-propose them on the next run; the detector is expected to skip an existing
`(detector, fromTick ± tolerance)` match. This is the only reason a document can grow without a human
typing, so §6 carries it as a size risk.

### 3.4 Palettes as data

A palette is the tagging vocabulary: codes, the panel each code opens, per-button lead and lag, and
which label groups are sticky. It is `<config>/palettes/<name>.tagpalette.json`, loaded like themes
(§2.8): scanned in filename order, a file that fails validation is skipped with a diagnostics-log line,
and the built-in default is an embedded resource in `DemoViewer.NET.dll` that ships one CS2 vocabulary
so a first run can tag.

```jsonc
{
  "schemaVersion": 1,
  "id": "cs2-default", "name": "CS2 default",
  "panels": [
    { "id": "root", "buttons": [
        { "code": "A execute",  "hotkey": "1", "leadSeconds": 5, "lagSeconds": 10, "then": "outcome", "colorArgb": 4293467747 },
        { "code": "B execute",  "hotkey": "2", "leadSeconds": 5, "lagSeconds": 10, "then": "outcome" },
        { "code": "Default",    "hotkey": "3", "leadSeconds": 0, "lagSeconds": 20 },
        { "code": "Retake",     "hotkey": "4", "leadSeconds": 3, "lagSeconds": 15, "then": "site" }
    ]},
    { "id": "outcome", "kind": "labels", "group": "outcome", "buttons": [
        { "value": "won",  "hotkey": "W" }, { "value": "lost", "hotkey": "L" }
    ], "then": "site" },
    { "id": "site", "kind": "labels", "group": "site", "buttons": [
        { "value": "A", "hotkey": "A" }, { "value": "B", "hotkey": "B" }
    ]}
  ],
  "stickyGroups": ["opponent", "map"],
  "clampToRound": true
}
```

* A `code` button creates an instance `[playhead - leadSeconds × tickRate, playhead + lagSeconds ×
  tickRate]`, clamped to the round when `clampToRound` (round bounds from `DemoCacheRecord.Rounds`,
  the same source the reel clamps by). Seconds, not ticks, because the author does not know the tick
  rate and the document does.
* `then` names the panel revealed after the press (the "panel flow"). A `labels` panel adds one label to
  the instance just created (or the selected one, in Label Mode) and follows its own `then`.
* `stickyGroups` lists groups whose current value the session re-applies to every new instance until
  cleared. Stickiness is a session behaviour; the stored label is an ordinary label.
* Hotkeys are single keys or `Ctrl+`/`Shift+` chords. The validator refuses anything in
  `Playback2DKeymap.ShellReservedGestures` and `BrowserReservedGestures`
  (`Playback2DKeymap.cs:363-400`), and warns (does not refuse) on a collision with a `Default`
  binding (`Q`, `E`, `F`, `D`, `X`, `Space`, arrows, `Home`, `Esc`, `Ctrl+Z`, `:307-358`): palette keys
  are routed only while the Tag Palette has focus, under a new `Playback2DBindingScope` value, so a
  shadowed transport key is a palette author's choice and the validator says which key they shadowed.
* A human label group may not use a name Round Facts reserves for `facts` (the validator reads the
  vocabulary from `IRoundFactsSource`). This keeps the two namespaces unambiguous in the document
  and in any future export (§3.8) without a prefix.
* Unknown fields are preserved, as everywhere else. Palettes are never written by the app; the user
  edits the file, and a "Reload palettes" action re-scans like `ThemeRegistry.Reload`.

### 3.5 The service

All types live in `src/App/DemoViewer.NET/Services/Tags/`, a sibling of `Services/DemoCache/`:

| File | Holds |
|---|---|
| `TagModels.cs` | `TagDocument`, `TagInstance`, `TagLabel`, `TagPosition`, `TagMovement`, `TagSuggestion`, `TagIndexEntry`, `TagIndexFile`; `TagJsonContext` (source-generated) |
| `TagStore.cs` | The persisted store: index plus sidecars, atomic writes, in-memory on the browser |
| `TagSession.cs` | The live document for the open demo with undo/redo and debounced autosave |
| `TagFactsRefresher.cs` | §3.3 |
| `TagQuery.cs` | §3.7 |
| `TagPaletteStore.cs`, `TagPaletteDefinition.cs` | §3.4 |

```csharp
public sealed class TagStore
{
    public TagStore(string? tagsRoot, Action<Action>? post = null);   // null root = in-memory (browser, tests)
    public bool IsPersistent { get; }
    public IReadOnlyList<TagIndexEntry> Index { get; }                 // snapshot, safe off-lock
    public event Action<string?>? Changed;                             // sha256, or null for a batch
    public IDisposable BeginBatch();

    public TagDocument? TryLoad(string sha256);                        // null when absent or corrupt
    public TagDocument LoadOrCreate(DemoIdentity demo, ClockIdentity clock);
    public TagLoadResult Load(string sha256, ClockIdentity current);   // carries ClockMismatch, SchemaVersion, Path
    public bool Save(TagDocument document);                            // atomic; false on I/O failure, never throws
    public void Update(string sha256, Action<TagDocument> mutate);     // read-modify-write under one gate; routed to a checked-out session
    public bool Delete(string sha256);
    public List<TagDocument> LoadDocuments(Func<TagIndexEntry, bool>? where = null);  // the cross-demo reader; not on the UI thread
    public void SaveIndex();                                            // deferred, like DemoCacheStore.SaveIndex
    public void RebuildIndexFromDisk();                                 // when index.json is missing or corrupt

    public IDisposable CheckOut(string sha256, TagSession session);    // makes the session the single writer for that document
    public static string SidecarPathFor(string tagsRoot, string sha256);
}
```

**Writes.** `Save` serializes, writes `demos/.tag-<guid>.tmp`, then `File.Replace` or `File.Move`,
copied from `DemoCacheStore.WriteAtomic` (`:559-573`). Unlike the cache, a failed write is **not**
silent: `Save` returns false and the session shows "tags could not be saved" on its status line, the
annotation controller's wording. The index row is refreshed on every `Save`; `index.json` itself is
written on `SaveIndex`, called by the session on detach and by the shell at shutdown.

**Single writer.** A session that has a document open calls `CheckOut`. While checked out, `Update`
for that hash does not touch disk: it posts the mutation to the session (UI thread), which applies it
as a non-undoable change and schedules its autosave. Otherwise `Update` is a read-modify-write on the
file under an `_rmwGate`, the `DemoCacheStore` pattern (`:63-75`). This is what stops a facts refresh
from being overwritten by the next autosave.

**Identity.** `TagStore` never hashes a file. It takes `DemoIdentity` from the caller, who gets it from
Content Identity (§3.10). If the caller has no hash (a demo opened from outside the library on a build
where Content Identity has not run yet), the session hashes off-thread with the shared helper, exactly
as `AnnotationSessionController` does on first save (`:682-687`).

### 3.6 Positions and movements

A position is a world point on one floor of the map: `x`, `y`, `levelMinZ` (the floor's quantized lower
Z, the annotation anchor rule from `annotations-format.md` "Space anchors"; never a floor index), the
`tick` the click was made at (frame clock; null when the click was not time-specific), a resolved
`place` and a `placeSource`. A movement is two positions, `from` and `to`. Both arrays are on the
instance; a code press with no click leaves them empty.

`place` resolution, in order of preference, recorded in `placeSource` so a later pass knows whether to
improve it:

| `placeSource` | Meaning |
|---|---|
| `zone-bake:<n>` | Resolved by the Zone Baking `PlaceResolver` at bundle schema `n`. Authoritative. |
| `pawn` | No zone data: the `m_szLastPlaceName` of the nearest alive pawn on that level at `tick`, via `PositionSampler.Walk` (the `Place` token, `CS2DemoKit.Parser.xml`: "`m_szLastPlaceName`, e.g. `BombsiteA`. Null on maps with no named nav areas"). Approximate. |
| `null` | Unresolved (no zone data and no pawn within a threshold). |

The refresh pass (§3.3) upgrades `pawn` and `null` to `zone-bake:<n>` when a bundle with places
exists, and re-resolves `zone-bake:<m>` when `n > m`. Coordinates are never rewritten.

Search Filters read positions through `TagQuery` (§3.7) as `place` equality today and as point-in-
polygon once Zone Baking ships; the coordinates are what make the second possible without re-tagging.

### 3.7 The query API (The Matrix, Watched Situations, Search Filters)

`TagQuery` is a pure function layer over `TagStore.LoadDocuments`. It does not know teams, palettes or
the UI.

```csharp
public sealed record TagSlice(
    IReadOnlySet<string>? Demos,             // sha256 set; null = every demo in the tag index
    IReadOnlySet<string>? Codes,             // null = every code
    IReadOnlyList<LabelPredicate> Where,     // AND of predicates; each is (namespace, group, one-of values)
    IReadOnlySet<int>? Rounds,               // null = every round
    TagSource? Source,                       // null = human + accepted suggestions (the default the UI wants)
    DateTime? CreatedAfterUtc);              // Watched Situations' "N new" cursor

public enum LabelNamespace { Human, Fact, Any }
public sealed record LabelPredicate(LabelNamespace Namespace, string Group, IReadOnlySet<string> Values);

public abstract record PivotAxis
{
    public sealed record Code : PivotAxis;
    public sealed record Label(LabelNamespace Namespace, string Group) : PivotAxis;   // one row/column per distinct value; multi-valued instances count once per value
    public sealed record Demo : PivotAxis;
    public sealed record Round : PivotAxis;
}

public sealed record TagInstanceRef(string Sha256, Guid Id, string Code, int FromTick, int ToTick, int? Round);

public sealed record TagPivot(
    IReadOnlyList<string> RowKeys, IReadOnlyList<string> ColumnKeys,
    IReadOnlyDictionary<(string Row, string Column), IReadOnlyList<TagInstanceRef>> Cells);

public static class TagQuery
{
    public static IReadOnlyList<TagInstanceRef> Find(IEnumerable<TagDocument> docs, TagSlice slice);
    public static TagPivot Pivot(IEnumerable<TagDocument> docs, TagSlice slice, PivotAxis rows, PivotAxis columns);
    public static IReadOnlyList<TagInstanceRef> At(TagDocument doc, int tick);   // Label Mode: instances containing the playhead
}
```

* **The Matrix** is `Pivot(store.LoadDocuments(e => slice.Demos?.Contains(e.Sha256) ?? true), slice,
  rows, columns)`. Swapping axes is calling it again with the arguments exchanged; every cell already
  holds the refs that open its clips. The "dynamic mode restricted to a slice" is the same call with
  a narrower `TagSlice`. Multi-demo over one opponent is `Demos` = the set Team Identity resolves for
  that opponent; `TagQuery` is deliberately ignorant of how that set was built.
* **Watched Situations** persists a `TagSlice` beside its query and reads the badge as
  `Find(docs, slice with { CreatedAfterUtc = lastSeen }).Count`. The `TagIndexEntry.codes` and
  `instanceCount` columns exist so it can skip documents that cannot match before opening them, the
  `HighlightCount` idiom (`DemoCacheModels.cs:415-419`).
* **Search Filters** need only `Find` with a `Fact` predicate and, later, a position predicate; a
  `PositionPredicate(place | polygon)` is the one planned addition to `TagSlice`.
* Opening a cell's clips: a `TagInstanceRef` resolves to a demo path through the cache index
  (`DemoCacheIndexEntry.Sha256`, non-null after Content Identity); a missing row renders "demo not in
  library" rather than a dead link. In Phase 2 a click seeks the open demo (`RequestSeekToTick`) or
  opens the other demo and seeks. When the Review Queue lands (Phase 4) it takes
  `(demoPath, fromTick, toTick, note)` from the same ref; `StagedClipState`
  (`HighlightsModels.cs:201-217`) is keyed by ruleset and highlight id and is not reused.

Cost: `LoadDocuments` at 1000 demos is 222 ms warm (§2.9). The Matrix debounces its rebuild and runs
it off the UI thread, the `LoadRecords` rule (`DemoCacheStore.cs:295-298`).

### 3.8 Interchange: deferred, with the mapping kept

**Decision (2026-09-24).** No XML exporter in v1. The format of record is `.dvtag.json`
(§3.2), which is versioned, documented in `docs/tags-format.md` in the shape of
`annotations-format.md`, forward compatible through the unknown-field bag, and MIT-licensed with the
repository. That is the open format this project offers; anyone can read it with a JSON parser and
the published document.

**Why the exporter was proposed, and why that is not enough.** The original roadmap's argument was
that XML "is the lingua franca for tag exchange in sports analysis, and being the CS tool that
speaks it costs almost nothing". The cost is real but small (about a day); what is missing is a
consumer. No CS team or analyst in the research is documented using Sportscode or Nacsport on demo
data, the shape's optional blocks cannot be verified without a genuine export (§2.6), and the
format is a vendor's with no specification to conform to. A feature with no consumer and no
verifiable target is not a v1 feature.

**What is kept so that adding it later is a one-day task.** Three conventions in the data model,
none of which costs anything and each of which the design already has for its own reasons:

| Convention (§3.2) | Own reason | Interchange reason |
|---|---|---|
| An instance is a `(fromTick, toTick, code)` span | the timeline band and the Matrix row | maps 1:1 to `<instance><start><end><code>` |
| Labels are an **ordered list** of `{group, value}`, a group may repeat, `group` may be empty | the Matrix pivots on multi-valued groups; a bare label is a flag | maps 1:1 to repeated `<label><group><text>`; a bare label is the `<text>`-only shape (the kloppy #616 lesson) |
| The clock header (`tickRate`, frame clock) and one document per demo | the annotation sidecar's contract (F15) | seconds into a video are `(tick - origin) / tickRate`; Sportscode times are per video, so one file per demo |

**The mapping, recorded for whoever builds it:**

| `.dvtag.json` | Sportscode / kloppy XML |
|---|---|
| `instances[i]` | `<instance>` under `<ALL_INSTANCES>` in document order |
| document-order index, 1-based | `<ID>` (integers are what every importer has been seen to accept; string IDs are legal) |
| `fromTick`, `toTick` | `<start>`, `<end>` as `(tick - originTick) / clock.tickRate + offsetSeconds`, `"0.0##"` invariant |
| `code` | `<code>` |
| `labels[j].group`, `.value` | `<label><group/><text/></label>`; empty group → `<label><text/></label>` |
| `facts[j]` (parser namespace) | the same, under their plain group names (the palette validator keeps human and fact groups disjoint, §3.4) |
| `round` | a `round` label |
| `note` | a `note` label |
| `suggestion.state == rejected` | never exported; `proposed` only on request |
| several demos | one `<file>` per demo in a folder, never merged |
| `<ROWS>` colours, `<SESSION_INFO>` | unverified; omit until a genuine export is in hand |

**Trigger for building it.** A user with Sportscode or Nacsport asks for their CS tags in it, or a
Review Packs consumer needs to hand a coded timeline to a video tool. At that point obtain one
genuine export from the target tool first (the former action O-33), verify the optional
blocks against it, and add `TagXmlExporter` with the golden test that was in the first draft. An
importer (`source: import`) is the same mapping run backwards and is deferred with it.

**If any export is wanted sooner, it is CSV**, not XML: the research's own finding is that teams
live in spreadsheets (theses 04 and 07 of the original roadmap). A flat CSV of instances with one column per
label group is a half-day item against the same `TagQuery` the Matrix uses, and needs no external
format at all. It is not in this design's scope either; it is noted so the next request for
"export" starts from the evidence.

### 3.9 The session and undo

`TagSession` holds one `TagDocument` and mirrors `AnnotationDocument`'s history model:

```csharp
public abstract record TagDelta
{
    public sealed record Add(TagInstance Instance, int Index) : TagDelta;
    public sealed record Remove(Guid Id) : TagDelta;
    public sealed record Replace(Guid Id, TagInstance Instance) : TagDelta;   // labels, note, positions, span edits
    public sealed record Batch(IReadOnlyList<TagDelta> Items) : TagDelta;
}
```

* `Apply(TagDelta)` computes the inverse at apply time (a `Remove`'s inverse needs the removed
  instance, `DocDelta.cs:11-14`), pushes it on a 200-entry undo stack, clears redo, bumps `Version`.
* `Undo`/`Redo` are the `Playback2DAction.Undo`/`Redo` bindings (`Ctrl+Z`, `Ctrl+Shift+Z`) when the
  Tag Palette has focus; the annotation document keeps them when the canvas has focus. One history per
  document kind, resolved by focus, is simpler than a merged history and matches how the two panels are
  used.
* A code press plus its panel-flow labels is one `Batch`, so one `Ctrl+Z` removes the whole gesture.
* Refresh mutations (§3.3, §3.5) apply through `ApplyExternal(Action<TagDocument>)`: no history entry,
  `Version` bumped, autosave scheduled. Undo after a refresh restores the human fields of the entry it
  pops and leaves `facts` as refreshed.
* Autosave is debounced (500 ms, the annotation controller's constant) off the UI thread, snapshots
  the document and `Version` first, and a stale snapshot stands down (`AnnotationSessionController.cs:653-672`).
  An empty document that does not already exist on disk is not written. `FlushAsync` runs on demo swap,
  tab deactivate and shutdown.
* `TagSession.Attach(DemoIdentity demo, ClockIdentity clock, string demoPath)` checks the document out
  of the store, surfaces `ClockMismatch` on the status line, and derives `round` for any instance that
  lacks one.

### 3.10 What Content Identity must provide (exact ask)

Content Identity is the small build item this store depends on. It needs to deliver these five things
and nothing more:

1. **One hash helper.** `DemoContentHash.Compute(string path)` and `Compute(ReadOnlySpan<byte>)`,
   lowercase-hex SHA-256 of the file bytes, in a place all three current callers (§2.3) can reach
   (`DemoViewer.NET.Playback2D.Pipeline` is the lowest assembly that already has one; the App can
   forward to it). The three existing helpers become forwarders. Same output as today, so every
   `.dvann.json` and `GraphBreakpoints.v2.json` on disk keeps matching.
2. **Every index row has a hash.** `DemoCacheRecord.Sha256` and `DemoCacheIndexEntry.Sha256` are
   populated for every demo at tier 2 at the latest: the tier-2 pass already streams the whole file
   through the parser, and the hash costs 0.6 to 0.7 s per 500 MiB warm (§2.9), disk-bound cold. The
   tier-1 (header) pass may also compute it when the library is small enough; that is the item's call.
3. **A reverse lookup.** `DemoCacheStore.TryGetIndexBySha256(string sha256) → DemoCacheIndexEntry?`
   backed by a second dictionary maintained alongside `_index`. Two library entries with one hash
   (a copied demo) return the lexicographically-smallest path, the `DemoLibraryService` primary rule
   (`:885-891`).
4. **The open demo's hash on the module context.** `IModuleContext.DemoSha256 { get; }` (a
   default-interface member returning null, the additive pattern `MapName` used, `IModuleContext.cs:24`),
   published from the value `MainViewModel` already computes at `:2641` and `:3734`. Modules must not hash
   a second time.
5. **Frame clock header on new stores.** Nothing to build: `ClockIdentity` and `DemoIdentity` from
   `AnnotationIdentity.cs` are reused as-is. The item should, while there, make the tab's clock carry
   real `firstTick`/`lastTick` instead of `0, 0` (`Playback2DTabViewModel.cs:1133-1134`) so a mismatch
   can actually be detected; `ClockIdentity.Matches` already handles old all-zero documents.

Until 2 lands, `TagSession.Attach` falls back to hashing off-thread (§3.5, Identity), so the Tag Store
build can start before Content Identity finishes; it cannot ship to users before it.

### 3.11 UI touchpoints (for the build items that follow)

* **Tag Track** (`Modules/RoundTagger/Timeline/TagTrack.cs`, App side like `RoundTrack`): `Id =
  "tag"`, `DisplayName = "Tags"`. `IsAvailable` is "the session's document has at least one instance
  whose `fromTick` resolves to a frame". **Bands** are the union of all instance spans merged into
  non-overlapping runs (the interface contract, §2.4), labelled with the code when a run holds one
  instance and with the count otherwise, ARGB from the palette button colour when the run is a single
  instance, else 0. **Markers** are one `TimelineMarkerKind.Custom` per instance at `fromTick`, glyph
  `⚑`, tooltip `"{code} · {labels} · r{round}"`, so nothing is hidden by the merge. Ticks convert
  exactly once through `FrameIndexAtTick`, -1 dropped (`AnnotationTrack.cs:101-105`). `MarkersChanged`
  fires on every session `Version` bump, coalesced to the UI thread. Registered through
  `Playback2DTimelineViewModel.RegisterTrack` from the Round Tagger module's activation. A click seeks
  to the band's first frame, the round-band behaviour. Per-code lanes would need a `Lane` on
  `TimelineBand`, a Core change this design does not require (§4.6).
* **Module.** `RoundTaggerModule : IWorkspaceModule` in `Modules/RoundTagger/`, id
  `net.demoviewer.roundtagger`, one Main-strip tab `"tagger.matrix"` (The Matrix) and a dockable palette
  hosted by the 2D Playback tab; feature ids `tab.tagger` and `playback2d.tagger` in `FeatureCatalog`,
  following `HighlightsModule` (`HighlightsModule.cs:31-62`): delegate-injected VM factory, no shell
  reference, `ViewModelFactory` never `DataContext`.
* **Settings.** One row: the active palette id, in `Playback2DSettings` with a `WriteInMemory` entry so
  `SettingsWasmRoundTripTests` passes.

---

## 4. Alternatives considered and why not

### 4.1 SQLite for the tag store (D2)

One statement for the Matrix instead of a scan. Rejected for this store: the measured scan over a
thousand-demo synthetic corpus is 222 ms warm (§2.9), `Microsoft.Data.Sqlite` is not in
`Directory.Packages.props`, a native SQLite library complicates the WASM publish that already needs
`WasmBuildNative`, and the sidecar format is the interchange artefact a teammate can copy. The Round
Index design owns D2 for the derived stores; if it adopts SQLite, a projection of tag documents into
that database is an additive reader, not a schema change here.

### 4.2 Sidecar beside the demo with the writable probe

The annotation store's rule. Considered and not recommended, because (a) the Matrix and Watched
Situations need to enumerate every document, which is one directory under the config root and an
unbounded set of demo folders otherwise; (b) most library demos sit in the read-only Steam replays
folder and would land in the config root anyway, splitting one team's tags across two locations by
folder permission; (c) annotations are per-demo artefacts a user hands over with the demo, tags are a
library-level dataset. "Export tags beside the demo" as an explicit action covers the sharing case.
Decision D1.

### 4.3 A tier inside `DemoCacheRecord`

The storage shape is right and the semantics are wrong: the cache is rebuildable, identity drift
discards all tiers, writes swallow failures, and `LoadOrCreate` on a size/mtime change would delete an
afternoon's tagging when a demo is re-downloaded. Tags are a source of truth. Rejected.

### 4.4 One `tags.json` for the whole library

`GraphBreakpoints.v2.json` style. Rewrites a growing file on every keystroke-debounce, deserializes
everything at start, and one corrupt write loses every demo's tags. This is the exact cost the
index-plus-sidecars split was introduced to avoid (`DemoCacheStore.cs:15-21`). Rejected.

### 4.5 Labels as a dictionary `group → value`

Simpler to query, and what kloppy's reader did until issue #616 showed Sportscode writes repeated
groups. A list of pairs round-trips both directions and the query layer builds its own lookups.
Rejected.

### 4.6 Per-code timeline lanes

Nicer to read than a merged band with markers, but `ITimelineTrack.BuildBands` requires
non-overlapping bands and registering one track per code would put a toggle per code in the chrome.
The merged-band-plus-markers shape needs no Core change. A `Lane` field on `TimelineBand` is a
possible later Core extension; it is not required to deliver the Tag Track.

### 4.7 Facts as prefixed human labels (`dv.side`)

One namespace, no refresh logic. But a re-parse would then have to find and rewrite by prefix, a
human could edit a fact, and the prefix leaks into every exported group name. Two arrays with one
shape is the same query cost and none of those problems. Rejected.

### 4.8 Frame indices instead of ticks

Would skip the `FrameIndexAtTick` conversion on every timeline build. Frame indices are a property of
one parse and shift when the engine changes how frames are split (F15); ticks do not. Rejected, and
it is the rule the whole app already follows.

---

## 5. External and engine changes required

**CS2DemoKit:** none. `PositionSampler.Walk` and `PositionSample.Place` (0.12.0) supply the `pawn`
place fallback as they are.

**AssetBaker bundle schema:** none required by this design. Zone Baking (its own design) supplies the
`PlaceResolver` that upgrades `placeSource`; this document only reserves the `zone-bake:<n>` stamp
format, where `n` is the bundle schema version that Zone Baking defines.

**CSVG game plugin:** none.

**Interfaces this design needs from sibling designs** (not external projects, listed so the integrator
can check them):

| From | Needed | Shape |
|---|---|---|
| Content Identity | §3.10 items 1 to 4 | one hash helper; `Sha256` on every index row; `TryGetIndexBySha256`; `IModuleContext.DemoSha256` |
| Round Facts | the per-round label source | `IRoundFactsSource.FactsFor(demoPath, roundNumber) → IReadOnlyList<(group, value)>`, plus `int Schema` and the reserved group vocabulary |
| Zone Baking | point → place | `PlaceResolver.Resolve(map, x, y, levelMinZ) → string?` and a bundle schema version |
| Team Identity | opponent → demo set | any function producing `IReadOnlySet<string>` of `sha256`; `TagQuery` takes the set |

---

## 6. Risks and unknowns

| Risk | Consequence | Mitigation |
|---|---|---|
| A user asks for Sportscode or Nacsport interchange after v1 | An exporter has to be built against an unverified vendor format | The data model already carries the three conventions and §3.8 the field mapping; obtain a genuine export from the target tool first, then build the exporter in about a day |
| Round Facts vocabulary changes after tags exist | `facts` groups renamed under the Matrix's feet | `factsStamp.schema`; the refresher rewrites the whole array, so a rename is one refresh, and the golden test pins the array shape not the vocabulary |
| Content Identity hashing on a network share | Tier-2 pass slows by ~1 s per demo | Hash in the same streaming pass as the parse; the fallback hash in the session keeps tagging working before the index catches up |
| Suggested Tags floods a document with proposals | Sidecar grows past the 50 KiB measured per demo; Matrix scan slows | `source`/`state` let the query default exclude proposals; detectors must dedupe against existing proposals; cap proposals per detector per round (Suggested Tags design) |
| Two sessions on the same document (two 2D tabs, or a CLI) | Lost writes | `CheckOut` is exclusive per process; cross-process is last-writer-wins with atomic files, the annotation store's current guarantee |
| Palette hotkeys shadow transport keys | A tagging user cannot pause | Scope-limited routing plus validator warnings (§3.4); `Space` and `Esc` are refused outright |
| The browser host | Tags vanish on reload | Session-only with the status line saying so; recorded in `wasm-matrix.md` |
| Overlapping instances hidden by the merged band | A user misses a second instance under a band | One marker per instance is always drawn; the band label carries the count |

Unknown until Place Names From The Pawn reports: how often `pawn` place resolution returns null on
each map, which decides whether the fallback is worth showing in the UI or should stay a silent
best-effort.

---

## 7. Test and verification strategy

All tests are TUnit (`Directory.Packages.props:115`), in `src/App/DemoViewer.NET.App.Tests/` unless
noted, following `DemoCacheStoreTests` (temp root per test, `AppPaths.ConfigDirEnvVar` seam).

| Test | Pins |
|---|---|
| `TagSchemaSnapshotTests.V1Schema_MatchesCheckedInSample` | `tests/fixtures/tags/schema-v1.sample.json` round-trips byte-identical through `TagStore`, including an injected unknown field at root, instance, label and position level; regenerated only under `PB2D_GOLDEN_UPDATE=1` (the annotation test's switch, `AnnotationSchemaSnapshotTests.cs:33-36`) |
| `TagStoreTests` | index/sidecar split; lazy read; corrupt sidecar → null; corrupt index → rebuild from disk; atomic overwrite leaves no `.tmp`; `Save` returns false on a read-only root and does not throw; null root holds many documents (the `_memoryRecords` lesson, `DemoCacheStore.cs:48-57`); `Update` routes to a checked-out session and not to disk |
| `TagStoreIdentityTests` | hash mismatch in the file body → ignored, not overwritten; clock mismatch → loads with the flag; all-zero clock → no warning |
| `TagSessionTests` | a code press plus panel labels is one undo entry; undo after `ApplyExternal` keeps refreshed facts; autosave stale-snapshot stand-down; empty document not written; 200-entry cap |
| `TagFactsRefresherTests` | facts rewritten wholesale, labels untouched byte-for-byte; instance outside every round marked `stale`; `placeSource` upgraded from `pawn` to `zone-bake:1` and not downgraded |
| `TagQueryTests` | pivot cell counts on a fixture of three documents; multi-valued group counted once per value; `Any` namespace merges; `CreatedAfterUtc` cursor; `At(tick)` inclusive bounds |
| `TagPaletteStoreTests` | drop-in order; invalid file skipped with a log line; reserved gesture refused; fact-group name refused; default palette loads from the resource |
| `TagTrackTests` (App tests, with a stub `ITimelineData`) | bands non-overlapping and ascending; one marker per instance; a `fromTick` past the parse dropped; `MarkersChanged` on version bump |
| `SettingsWasmRoundTripTests` | extended by the new settings row automatically (it reflects over the type) |
| `Playback2DFeatureCatalogTests` | the two new feature ids present with the right scope and parent |

Manual: tag a real matchmaking demo from the Steam replays folder (read-only, so the config-root
location is exercised), re-index the library so the facts refresher runs, confirm `labels` bytes are
unchanged in the sidecar with a diff. Add the browser row to `wasm-matrix.md` and tick it on the next
per-release checklist run.

---

## 8. Decisions

* **D1. Sidecar location.** Config root keyed by hash, always (recommended, §3.1, §4.2), or the
  annotation store's beside-the-demo-when-writable rule. The store code is the same either way; the
  index and the Matrix's enumeration differ.
* **D2. Facts namespace in XML.** *Withdrawn 2026-09-24 with the exporter (§3.8).* The validator
  rule that keeps human and fact groups disjoint stays for the document's own sake (§3.4).
* **D3. Round clamping default.** `clampToRound: true` in the shipped palette (recommended: an
  instance that crosses a round boundary is almost always a mis-press), or off.
* **D4. Where the hash helper lives.** `Playback2D.Pipeline` (already has one, reachable by the CLI
  and the App) or a new tiny `DemoViewer.NET.Identity` assembly. Content Identity's call, flagged here
  because the Tag Store references it.
* **D5. A genuine Sportscode or Nacsport export file.** *Withdrawn 2026-09-24 with the exporter.*
  It becomes the first step of building the exporter if a user ever asks (§3.8); nothing to decide now.
* **D6. Suggested-tag retention.** Keep rejected proposals in the document (recommended, so
  detectors do not re-propose) or drop them and make detectors idempotent by other means.

---

## 9. Effort estimate and sequencing

| Step | Item | Estimate | Blocked by |
|---|---|---|---|
| 0 | Content Identity (§3.10, items 1 to 4) | 1 to 2 days | nothing |
| 1 | `TagModels`, `TagJsonContext`, `TagStore`, index, JSON golden | 3 days | nothing (fallback hash until step 0) |
| 2 | `TagSession` with undo, autosave, status line; `RoundTaggerModule` shell and feature ids | 2 days | step 1 |
| 3 | `TagTrack` registered on the 2D timeline | 1 day | step 2 |
| 4 | `TagPaletteStore`, default palette resource, validator | 2 days | step 1 |
| 5 | `TagQuery` (`Find`, `Pivot`, `At`) with fixtures | 2 days | step 1 |
| 6 | `TagFactsRefresher` and `IRoundFactsSource` adapter | 1 day | step 1, Round Facts build |
| 7 | `wasm-matrix.md` row, docs page `docs/tags-format.md` in the `annotations-format.md` shape, including the §3.8 mapping | 0.5 day | steps 1 to 6 |

About eleven working days for the store-level work, after which Tag Palette, Label Mode, Click To Tag
Position and The Matrix (the UI build items in `plan.md` §3) start against a finished contract. Steps
1, 4 and 5 are independent once the models exist and can run in parallel. Step 3 is what makes the
Phase 2 gate ("a real demo tagged end to end by hotkey; the Matrix opens clips") visible on the
timeline; step 5 is what makes the second half of the gate possible.

---

## 10. Sources

Repository, `main` at `d90ec9f`:

* `plan.md` §2 (F5, F9, F10, F14, F15, F17), §3 (Tag Store, Tag Track, Tag Palette,
  Content Identity, Round Facts), §6 (D2).
* `docs/playback2d-v2/annotations-format.md`; `docs/playback2d-v2/wasm-matrix.md`;
  `docs/playback2d-v2/design.md` §5.4, §5.6.
* `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationStore.cs`,
  `AnnotationIdentity.cs`; `src/Playback2D/DemoViewer.NET.Playback2D.Core/Annotations/AnnotationDocument.cs`,
  `DocDelta.cs`; `.../Core/Timeline/ITimelineTrack.cs`, `TimelineMarker.cs`, `ITimelineData.cs`,
  `AnnotationTrack.cs`; `src/Playback2D/DemoViewer.NET.Playback2D.Tests/AnnotationSchemaSnapshotTests.cs`;
  `tests/fixtures/playback2d/annotations/schema-v1.sample.json`.
* `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs`, `DemoCacheModels.cs`,
  `LegacyCacheMigration.cs`; `src/App/DemoViewer.NET/Services/AppPaths.cs`, `GraphBreakpointStore.cs`;
  `src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs`;
  `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs`, `Playback2DKeymap.cs`,
  `Timeline/RoundTrack.cs`, `Timeline/Playback2DTimelineViewModel.cs`,
  `Annotations/AnnotationSessionController.cs`; `src/App/DemoViewer.NET/Modules/Highlights/HighlightsModule.cs`;
  `src/App/DemoViewer.NET/ViewModels/Highlights/HighlightsModels.cs`, `AddClipsPickerViewModel.cs`;
  `src/App/DemoViewer.NET/Theming/ThemeRegistry.cs`; `src/App/DemoViewer.NET/Features/FeatureCatalog.cs`;
  `src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs`;
  `src/App/DemoViewer.NET.Modules.Abstractions.Ui/IWorkspaceModule.cs`;
  `src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs:2641`, `:3734`; `Directory.Packages.props`.

CS2DemoKit 0.12.0: `~/.nuget/packages/cs2demokit.parser/0.12.0/lib/net10.0/CS2DemoKit.Parser.xml`
(`PositionSampler.Walk`, `PositionSample.Place`).

Measurements: scratch project
`tag-store-scan`
(Release, .NET 10, 2026-09-23), against synthetic sidecars and two Valve matchmaking demos in the
Steam replays folder (read-only; the tour sample was not used).

Interchange format:

* kloppy, `kloppy/infra/serializers/code/sportscode.py` and `kloppy/tests/files/code_xml.xml`
  (https://github.com/PySport/kloppy), read 2026-09-23.
* kloppy issue #616, "SportsCode load drops repeated group values written by save"
  (https://github.com/PySport/kloppy/issues/616).
* kloppy user guide, "Sportscode XML" (https://kloppy.pysport.org/user-guide/exporting-data/sportscode/).
* Hudl Support, "Import or Export a Sportscode XML"
  (https://support.hudl.com/s/article/import-or-export-a-sportscode-xml-sportscode); page title and
  scope confirmed, body not machine-readable at the time of writing.
* Nacsport, "Nacsport and Sportscode: using both together"
  (https://www.nacsport.com/blog/en-us/Tips/nacsport-sportscode-both-together); Nacsport Pro and Elite
  user manuals v6.0 (https://www.nacsport.com/Manuals/pdf/Nacsport_Pro_Manual_EN.pdf).
* wal/sportscoder, `R/sportscoder.R` (https://rdrr.io/github/wal/sportscoder/src/R/sportscoder.R):
  reads `instance`, `ID`, `start`, `end`, `code`, `label[group]`, `label[not(group)]`.
