# Strat Model: design

> **Status: APPROVED 2026-09-24** by the owner, as recommended (§8): books keyed by Team Identity team
> plus one `me` book; one history entry per commit with the 30 s idle rule and same-path merging; evidence
> linkage by the reserved human label groups `strat`, `strat.rev`, `strat.result`, `strat.failure`; Round
> Facts `winner` first, human `outcome` as fallback; alias table per owner per map beside the strats (and
> Zone Baking §3.8's custom zones supply place names of their own, so a team that defines zones rarely
> needs aliases for them); type applicability warns; the seven-value failure vocabulary as the shipped set;
> LAN Print as self-contained HTML in the system browser; the `plant` clock reserved only in v1; delete to
> `.trash/`. No engine change. Nothing here is implemented.

**Work item:** Strat Model (plan.md §3, Phase 3) · **Kind:** design, review required · **Status:** approved 2026-09-24 (banner above)
**Depends on:** Team Identity (design, `designs/team-identity.md`), Content Identity (build) · **Builds on:** Tag Store (design, `designs/tag-store.md`)
**Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0 · **Written:** 2026-09-23
**Unblocks:** Create Strat From Round, Step Authoring, Strat Export, Strat Record Panel, Strat Version History, Role View And LAN Print, Lineup On A Strat Step, Callout Aliases (the file it reads).

Nothing here is implemented. Every code reference describes the tree at `d90ec9f`. Measurements were taken over two untrimmed Valve matchmaking replays from the Steam `replays` folder (build 10896); the tour sample was not used.

---

## 1. Problem and scope

The Strat Book is built on one object: a **strat**, which is a diagram plus the set of tagged rounds in which it was run. Nothing in the tree is that object. The annotation sidecar holds strokes on one demo's clock; the Tag Store (once built) holds spans on one demo's clock; the cache holds facts about one demo. A strat is the opposite: it is authored on the **round clock** ("at 1:15"), it names **slots** instead of players, it belongs to a **team** rather than a demo, and it is *linked to* demos through tag instances rather than owning any tick anchor of its own.

This design fixes:

- the **schema** of a strat: identity and metadata, slots, steps on the round clock, utility references, branches, lifecycle status, provenance;
- how a **slot** resolves to a real player through Team Identity, and back;
- how a step's round-clock time is mapped to a **frame-clock tick** on a given demo and round, in both directions;
- the **evidence** rule: which Tag Store instances count as "this strat was run", and how the Strat Record Panel computes run / won / aborted, split by Demo Provenance Labels and by version;
- **Callout Aliases**: the alias table's schema, where it lives, and the resolution rule;
- **version history** as an append-only diff log, with the record split either side of a change;
- the **file format and location** (per owner, per map, under the config root), validation, the store service, the browser-host behaviour;
- what **Role View And LAN Print** derive, what **Step Authoring** must serialize (fields reserved here, semantics defined there), and the **export shapes**.

Out of scope, each its own item in `plan.md` §3: the canvas gestures and interpolation engine (Step Authoring), the render-to-GIF path (Strat Export), the record panel's UI (Strat Record Panel), the right-click population from a demo (Create Strat From Round), the lineup object (the Utility Book's Lineup Cards), the practice-server push (deferred, D4).

---

## 2. What exists today (cited)

### 2.1 Persistence conventions

`DemoCacheStore` (`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs`) is `<config>/cache/index.json` plus one lazily-read sidecar per demo, atomic writes through a temp file and `File.Replace` (`:559-573`), in-memory when the root is null, which is the browser host (`:48-59`), and a `Changed(string? path)` event posted to the UI thread (`:157-170`). Its class doc says the cache "is rebuildable and is never a source of truth" (`:24-26`) and identity drift discards every tier (`:263-281`). A strat is user truth and cannot live under those rules; the Tag Store design reached the same conclusion for tags (`tag-store.md` §4.3) and this document follows it.

User-truth files live one level up under `AppPaths.ConfigRoot` (`src/App/DemoViewer.NET/Services/AppPaths.cs:54-66`, null on the browser): `settings.json` (`:74`), `SessionState.json` (`:81`), `GraphBreakpoints.v2.json` (`:87`). Team Identity adds `<config>/teams.json`; the Tag Store adds `<config>/tags/` and `<config>/palettes/`. Themes are the drop-in precedent for data folders under the config root (`:137-145`).

### 2.2 The two headers every positional store carries

`DemoIdentity(Sha256, FileName, SizeBytes)` and `ClockIdentity(Kind, TickRate, FrameCount, FirstTick, LastTick)` (`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationIdentity.cs:17`, `:33`), `Kind` always `dv-frame-clock` (`:36`). `docs/playback2d-v2/annotations-format.md` states the rules: the hash is the only matching field, a clock mismatch is a warning, unknown fields round-trip. A strat carries **no demo tick anchors at all** (§3.4), so it has no `demo` header; it carries a `clock` block of a different kind, declared as such, and every place it touches a demo it does so through a Tag Store instance that carries the frame-clock header.

### 2.3 Rounds and the round clock

- `CachedRound` is `Number` plus `StartTickFrameClock` (`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs:84-89`), adapted from `CS2DemoKit.Analysis.Clips.ClipRounds.Derive`, "the round authority for clip work: derives round boundaries in the frame clock" (CS2DemoKit.Analysis.xml, `ClipRounds`). A round opens at `round_freeze_end`; CS2 emits no `round_start` (`Modules/Playback2D/Timeline/RoundTrack.cs:11-20`, `Modules/Library/DemoLibraryService.cs:1385-1391`).
- The playback HUD reads the round length from the networked `m_iRoundTime` and offset-corrects the clock (`Modules/Playback2D/GameInfo.cs:74-81`); `IModuleContext.CurtimeSeconds` documents round-remaining as `m_fRoundStartTime + m_iRoundTime − CurtimeSeconds(tick)` (`src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs:97-109`).
- `Playback2DTabViewModel.ResolveRoundWindow` walks consecutive freeze-ends on the tick axis and returns `[freeze-end, next freeze-end − 1]`, with warmup and the last round's open end handled explicitly (`Modules/Playback2D/Playback2DTabViewModel.cs:1026-1071`).

**Measured** (scratch project `scratchpad/strat-model`, `clock` mode, 2026-09-23):

| Demo (Steam `replays`, build 10896, `GotvMatchmaking`) | Rounds | `m_iRoundTime` at every freeze-end | `freeze GameTick == StartTickFrameClock` | `ServerTick − GameTick` | Plant offsets from freeze-end |
|---|---|---|---|---|---|
| `match730_003842233788306292960_0260929275_408.dem`, de_mirage | 21 | 115 on 21 of 21 | 21 of 21 | 1303 (= `ServerStartTick`) | 7 plants, 26.2 s to 100.2 s |
| `match730_003842182368957825245_0056633905_389.dem`, de_nuke | 22 | 115 on 22 of 22 | 22 of 22 | 1993 (= `ServerStartTick`) | 13 plants, 17.5 s to 100.3 s |

So the frame-clock round start is the freeze-end `GameTick` exactly (the tick-clocks memory: `GameTick` *is* the frame clock), the competitive round length is 115 s on every round of both demos, and a step at "1:15" is 40 s after the round start tick. `round_end` was absent as a game event in both parses (`roundEnds=0`); round outcome is Round Facts' problem, and §3.6 asks it for a `winner` fact rather than deriving one here.

### 2.4 Places: the canonical callout vocabulary

`PositionSample.Place` is "`m_szLastPlaceName`, e.g. `BombsiteA`. Null on maps with no named nav areas, and before the field is first networked for the pawn" (CS2DemoKit.Parser.xml, `PositionSample`). `rules/highlights_position.rules.yaml:3-5` already consumes it as `player.place`. Zone Baking's probe found the same names as `env_cs_place` volumes in the map's entity lump (`scratchpad/zone-probe/out_de_mirage.txt`: 23 volumes on de_mirage, plus two `func_bomb_target`).

**Measured** (`places` mode, `PositionSampler.Walk(demo, 16, frames)`):

| Demo | Samples | Distinct non-empty places | Empty-string place | Walk time |
|---|---|---|---|---|
| de_mirage above | 55,511 | 23 (identical to the 23 `env_cs_place` names) | 21 samples (0.04 %) | 1.42 s |
| de_nuke above | 54,223 | 29 | 17 samples (0.03 %) | 1.44 s |

Two facts for the alias table: the pawn's place is an **empty string**, not null, before it is first networked (both demos), so `""` must be treated as unresolved; and on a multi-level map one place spans floors (de_nuke `Ramp` samples span Z −640 to −310, `BombsiteB` −772 to −547, `BombsiteA` −482 to −225), so a place name alone does not pick a floor. Step positions therefore carry `levelMinZ` (§3.9), the annotation anchor rule (`annotations-format.md` "Space anchors", `MapSpace.QuantizeZ` at `src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapSpace.cs:81`).

### 2.5 The annotation element, undo, and the export loop

- `AnnotationKind` declares Freehand, Line, Arrow, Rect, Ellipse, Text (`src/Playback2D/DemoViewer.NET.Playback2D.Core/Annotations/AnnotationElement.cs:14-33`); `SpaceRef` is `World(LevelMinZ)` or `Entity(SteamId, Dx, Dy)` (`:219-246`); `TimeEnvelope` is the trapezoid on the frame clock (`:267`). The sidecar DTO carries `[JsonExtensionData]` on root and element and is source-generated (`Pipeline/Annotations/AnnotationDocumentDto.cs:15-30`, `:128-133`). Only Freehand is written today (finding F7); Shape Tools makes the rest real.
- `DocDelta` is `Add | Remove | Replace | Batch` with the inverse computed at apply time (`Core/Annotations/DocDelta.cs:15-41`); `AnnotationDocument` caps history at 200 entries and makes one gesture one undo entry (`AnnotationDocument.cs`).
- `ISceneFrameSource` is `FrameCount`, `TimeAt(i)`, `FrameAt(i)` (`Core/Export/ISceneFrameSource.cs:16`); `SceneExportSession.RunAsync` draws whatever source it is handed through the window's layer stack, refusing GPU providers and enforcing `GifMaxFrames = 1800` and GIF fps of 10, 20, 25 or 50 (`Pipeline/Export/SceneExportSession.cs:36-45`). A strat is a scene without a demo, which is what §3.11 relies on. `PlayerMarker` (`Core/PlayerMarker.cs:28-45`) is the token: slot, team, world X/Y/Z, yaw, ring, label, alive, SteamId. `SceneFixture` already serializes one `Scene2DFrame` for design iteration (`Pipeline/SceneFixture.cs:17-60`).

### 2.6 Modules

`HighlightsModule` is the module shape: one Main-strip tab, `ViewModelFactory` never `DataContext`, delegate-injected VM, no shell reference (`src/App/DemoViewer.NET/Modules/Highlights/HighlightsModule.cs:31-62`), registered in `App.BuildRegistry` (`App.axaml.cs:844-869`), feature id in `FeatureCatalog` with the persisted-key warning (`Features/FeatureCatalog.cs:59-63`). `IModuleContext` exposes `DemoPath` (`IModuleContext.cs:16`), `MapName` (`:24`), `RequestSeekToTick` (`:118`); Content Identity adds `DemoSha256` (`tag-store.md` §3.10 item 4).

### 2.7 What the upstream designs provide

| From | Used here as |
|---|---|
| Team Identity | `Team.Id` (GUID, stable across rebuilds) is the strat book owner; `Epochs[]` with `core` SteamIDs and the per-epoch members table (last-seen names) resolve slots to people; `Us` picks the default book; `GetAssignment(demoPath).OurSide` tells which five are "us" when a strat is created from a round. `teams.json` §3.2, API §3.7. |
| Tag Store | An instance is `{code, fromTick, toTick, round, labels[], facts[], ...}` on the frame clock, keyed by `demo.sha256`; human labels are an ordered `{group, value}` list where a group may repeat; `TagQuery.Find(docs, TagSlice)` with `LabelPredicate(Human, group, values)`; `TagIndexEntry` carries `codes[]` and `instanceCount` so documents can be skipped before opening. `tag-store.md` §3.2, §3.7. |
| Round Facts (design in flight) | The parser namespace on an instance: `side`, `buy.us`, `buy.them`, `score`, `endReason`, `plantSite`, `plantTick`. This document asks for three more groups (§5). |
| Content Identity | `sha256` on every index row and `DemoCacheStore.TryGetIndexBySha256` so an evidence ref resolves to a path. |

---

## 3. Proposed design

### 3.1 Vocabulary

| Term | Meaning |
|---|---|
| **Strat** | One document: metadata, five slots, an ordered step list on the round clock, branches, and a revision number. Diagram content (token positions, strokes) lives on its steps. |
| **Book** | Every strat of one **owner** (a Team Identity team, or the user alone), across maps. One folder. |
| **Owner** | `team:<GUID>` or `me`. `me` exists because a matchmaking library has "our side" without a stable roster (Team Identity §3.5) and must still get a book. |
| **Slot** | `A` to `E`. A role in the strat, never a person. Mapped to a SteamID per epoch (§3.5). |
| **Step** | One instruction: at a round-clock time, an actor slot does a verb from a place to a place, optionally with a utility reference. Carries the diagram state for that moment. |
| **Branch** | A first-class object: after a step, if a condition holds, continue at another strat or at a step chain. |
| **Evidence** | Every Tag Store instance carrying this strat's id in a `strat` label. Derived, never stored in the strat. |
| **Revision** | A monotonically increasing integer on the strat; every save appends one entry to the history log. |
| **Place** | A canonical nav place name (`PalaceInterior`). **Callout** is the team's word for it (`palace`), resolved through the alias table. Steps store places, never callouts. |

### 3.2 Files and locations

```
<config>/strats/
  index.json                                  rebuildable projection: one row per strat
  team-3f2a…/                                 owner folder: "team-<GUID>" or "me"
    book.json                                 owner-level: default slot map per epoch, notes
    de_mirage/
      callouts.json                           the Callout Aliases table for this owner on this map
      6f1c….dvstrat.json                      a strat: current state, schema v1
      6f1c….history.jsonl                     its append-only diff log, one JSON object per line
```

- **Per owner, per map**, as the brief asks. A book folder is self-contained: copying it copies the strats, their history and the team's callouts, which is the sharing unit (§3.13).
- `index.json` follows `DemoCacheIndexFile` (`DemoCacheModels.cs:447-465`): a version and a list of small rows (`id, owner, map, side, type, targetSite, status, name, revision, modifiedUtc, stepCount`). Rebuilt from the folders when missing, corrupt or behind. Evidence counts are **not** in it; they belong to the Tag Store and change without any strat being saved.
- Writes are atomic through the `WriteAtomic` idiom (`DemoCacheStore.cs:559-573`). Unlike the cache, a failed write is reported (§3.12).
- On the browser host the root is null and the store is a dictionary, the `_memoryRecords` shape (`DemoCacheStore.cs:59`). The Strat Book tab shows "session only: this browser tab forgets strats when it reloads", and `docs/playback2d-v2/wasm-matrix.md` gets a "Strat Book" row under Degraded. History is kept in memory per strat for the session.

**Measured size** (`size` mode): a strat with 12 steps, five slot positions per step, one branch and 20 history entries serializes to **20,034 bytes** indented; 200 such strats are **3.8 MiB**. Startup reads only `index.json`.

### 3.3 The strat document (`.dvstrat.json`, schema v1)

```jsonc
{
  "schemaVersion": 1,
  "id": "6f1c…",                                  // GUID, stable for the life of the strat, survives export/import
  "owner": { "kind": "team", "teamId": "3f2a…" }, // or { "kind": "me", "teamId": null }
  "name": "A exec, double smoke",
  "map": "de_mirage",                             // ParsedDemo.MapName spelling, lower case
  "side": "T",                                    // "T" | "CT"
  "type": "execute",                              // 3.3.1
  "targetSite": "A",                              // "A" | "B" | null
  "economy": "full",                              // "pistol" | "eco" | "force" | "full" | "any"   (Round Facts' buy vocabulary)
  "tempo": "slow",                                // "slow" | "mid" | "fast"
  "trigger": { "text": "on call at 1:15", "kind": "time", "atSeconds": 75 },   // kind: "time" | "contact" | "utility" | "call" | null
  "status": "Active",                             // "Theory" | "InProgress" | "Active" | "Archived"
  "revision": 4,
  "createdUtc": "2026-09-23T14:02:11Z", "modifiedUtc": "2026-09-23T15:40:03Z",
  "origin": { "demoSha256": "ab12…", "round": 7, "fileName": "match730_….dem" },   // null unless Create Strat From Round made it
  "tags": ["default-break", "vs-aggressive-ct"],  // free strings, for filtering only
  "notes": "Stairs smoke first, CT smoke second, then jungle molly.",

  "clock": { "kind": "round", "roundSeconds": 115 },   // 3.4: the STRAT clock, not a demo clock

  "slots": [                                      // exactly five, A to E, in order
    { "slot": "A", "role": "entry",   "steamId": null },
    { "slot": "B", "role": "support", "steamId": null },
    { "slot": "C", "role": "lurk",    "steamId": "76561198…" },   // optional pin; book.json holds the defaults
    { "slot": "D", "role": "awp",     "steamId": null },
    { "slot": "E", "role": "igl",     "steamId": null }
  ],

  "steps": [                                      // ordered; atSeconds non-increasing (the clock counts down)
    {
      "id": "9a0b…",
      "atSeconds": 90.0,                          // round clock REMAINING: 1:30
      "actor": "B",                               // "A".."E" | "all"
      "verb": "throw",                            // 3.3.2
      "from": { "place": "TRamp" },               // canonical place, or null
      "to":   { "place": "BombsiteA" },           // canonical place, or null
      "utility": { "kind": "smoke", "lineupId": null, "landing": { "place": "Stairs" } },   // 3.3.3; null when none
      "note": "throw on the 1:30 call, not before",
      "positions": [],                            // 3.9, reserved for Step Authoring
      "strokes": [],                              // 3.9, reserved for Step Authoring
      "holdSeconds": null,                        // 3.9, reserved
      "interpolation": null                       // 3.9, reserved
    }
  ],

  "branches": [                                   // 3.3.4
    {
      "id": "c2d3…",
      "afterStepId": "9a0b…",
      "condition": { "text": "contact at Connector before 1:05", "kind": null },
      "target": { "stratId": "e4f5…", "stepId": null },   // another strat, or a step in this one (stratId == id)
      "note": ""
    }
  ]
}
```

Rules, in the sidecar's words where they apply:

- Every object carries a `[JsonExtensionData]` bag; unknown fields survive load, edit, save. `schemaVersion` is advisory: a higher number is read for what this build understands. Serialization is through a source-generated `StratJsonContext`, `WriteIndented`, `WhenWritingNull`, declaration order, so the golden test is byte-stable (`tag-store.md` §3.2 rules, `wasm-matrix.md` on source generation).
- Ids are GUIDs and are the only cross-references. Names are display. `map` is the parser's spelling because that is what `IModuleContext.MapName` and the asset bundle use.
- `steps[]` order is authoring order and is the order Role View prints; `atSeconds` must be non-increasing along it (the validator refuses otherwise, §3.10). Two steps may share a time.
- The strat file never stores a player name, a demo path, or a frame-clock tick.

#### 3.3.1 Type vocabulary and applicability

The pro tactics-directory vocabulary, verbatim from the brief. Applicability is a **warning** in the validator, not a refusal, because a team may name a CT "fake" or a T "setup".

| `type` | Usual side | `targetSite` |
|---|---|---|
| `execute` | T | required |
| `rush` | T | required |
| `explode` | T | required (the site exploded onto) |
| `split` | T | required |
| `wrap` | T | required |
| `fake` | T | the site faked; `notes` says the real one |
| `default` | T | null |
| `setup` | CT | null (a whole-map setup) or the site it favours |
| `retake` | CT | required |
| `anti-eco` | either | optional |
| `save` | either | null |

#### 3.3.2 Verb vocabulary

`move | hold | throw | plant | defuse | peek | fake | rotate | wait | call | other`. Closed for v1 so Role View can phrase a line per verb ("B throws smoke TRamp → BombsiteA at 1:30"); `other` uses `note` as the phrase. Adding a verb is an additive schema change.

#### 3.3.3 Utility reference

`utility` is `{ kind, lineupId, landing }`. `kind` is `smoke | molotov | he | flash | decoy`. `lineupId` is a **typed reference to a Lineup id that the Utility Book defines later** (Lineup Cards, Phase 4): a GUID, nullable, and this design says nothing about what it resolves to beyond "a lineup on this map". `landing.place` lets a step say where the grenade should land before any lineup exists. Lineup On A Strat Step fills `lineupId`; nothing here dereferences it.

#### 3.3.4 Branches

A branch is a first-class object with its own id so it can be referenced by evidence later ("we took the B branch"). `afterStepId` names the step after which the condition is evaluated. `condition.text` is the human rule; `condition.kind` is reserved for a structured trigger (Suggested Tags' detector vocabulary, if that route is chosen) and is null in v1. `target` points at a strat (`stratId`) and optionally a step inside it (`stepId`); when `stratId == id` the branch continues at a later step of this strat, which is how a step chain is expressed without a second object kind. The validator refuses a branch whose `afterStepId` or in-document `stepId` is missing and warns on a `stratId` that is not in the same owner's book on the same map (§3.10).

### 3.4 The strat clock and the round-clock mapping

A strat has **no demo tick anchors**. Its `clock` block says so explicitly and is required by F15's rule that every store declares which clock it is on:

```jsonc
"clock": { "kind": "round", "roundSeconds": 115 }
```

`kind` is `round` in v1. `roundSeconds` is the authored round length, defaulting to 115 (measured on 43 of 43 rounds, §2.3). `plant` is reserved as a second kind for retake and post-plant strats whose steps count from `bomb_planted`; it is not defined in v1 (§8, decision 9).

Mapping between a step and a demo is a pure function in `StratClock` (App side, `Services/Strats/StratClock.cs`), used by Create Strat From Round, the Record Panel's "jump to this step in that round", and Strat Export's synthetic timeline:

```
tick(step, round, tickRate, roundSeconds)  = round.StartTickFrameClock + round((roundSeconds − step.atSeconds) × tickRate)
atSeconds(tick, round, tickRate, roundSeconds) = roundSeconds − (tick − round.StartTickFrameClock) / tickRate
```

- `round` is a `CachedRound` (frame clock). `tickRate` is the demo's. Both directions are on the frame clock only, the same `StartTickFrameClock` that `ClipWindows.RoundStartFor` floors against (`DemoCacheModels.cs:91-104`).
- `roundSeconds` for a *demo* round comes from Round Facts' `roundTime` fact when present (§5), else the strat's own `clock.roundSeconds`. On the two measured demos they agree at 115.
- A negative `atSeconds` means "after the round timer expired" (a plant stopped the clock); it is legal in the mapping and shown as `+m:ss` in the UI. A tick before the round's freeze-end has no round-clock value and maps to null, the `ResolveRoundWindow` rule for warmup (`Playback2DTabViewModel.cs:1035-1041`).
- Display is `m:ss` with an optional tenth (`1:15`, `1:15.5`). `atSeconds` is a double so a step captured from a demo (Create Strat From Round) keeps its precision; authored steps are whole seconds.

### 3.5 Slots and the roster

Slots are `A` to `E`, always five, always in order. Three levels resolve a slot to a person, in this order:

| Level | Where | Meaning |
|---|---|---|
| Strat pin | `strat.slots[].steamId` | This strat is built around one player in this slot (the team's AWPer in `D`). Optional. |
| Book default | `book.json` `slotDefaults[epochId][slot]` | The owner's usual line-up per Team Identity epoch. Written by the user in the Strat Book tab, or seeded by Create Strat From Round's first mapping. |
| Unassigned | | The UI shows "Slot A". Role View prints the slot letter. |

```jsonc
// book.json
{
  "schemaVersion": 1,
  "owner": { "kind": "team", "teamId": "3f2a…" },
  "slotDefaults": {
    "e1": { "A": "7656…", "B": "7656…", "C": "7656…", "D": "7656…", "E": "7656…" }   // epoch id from teams.json
  },
  "notes": ""
}
```

- Names are never stored. A display name is `TeamIdentityService`'s members table (`lastName` per SteamID per epoch, `team-identity.md` §3.2) at render time, sanitised at the render boundary like every player name. A rename never touches a strat.
- The epoch used for a display is the team's latest epoch unless the surface is looking at a specific demo, in which case it is the epoch of that demo's assignment (`TeamAssignment.EpochId`). For `me`, there are no epochs: `slotDefaults` has one key, `"me"`, and the `me.steamIds` list is the candidate set.
- **Demo to slot** (what Create Strat From Round needs): given a demo, a round and `GetAssignment(demoPath).OurSide`, the five pawns on our side are matched to slots by strat pin first, then book default for the demo's epoch, then remaining pawns to remaining slots in controller-slot order. The mapping is returned as `IReadOnlyDictionary<char, ulong>` and shown to the user before anything is written; it is not persisted on the strat.
- A strat whose owner is a team with `IsUs` false is legal (an opponent's strat, for the Dossier later); the "our side" logic simply uses that team's side in the demo.

### 3.6 Evidence and the Strat Record Panel

**The evidence list is derived, never stored.** A Tag Store instance is evidence for strat `S` when its human labels contain `{ group: "strat", value: "<S.id>" }`. The Strat Book reserves four human label groups; the Tag Palette validator (`tag-store.md` §3.4) treats them as reserved the same way it treats Round Facts' fact groups, so a palette cannot redefine them:

| Group | Values | Written by | Meaning |
|---|---|---|---|
| `strat` | a strat id | the Strat Book's "tag this round as a run of…" action, or a palette label panel the Strat Book populates from the index | This round ran this strat. Several strats on one instance are legal (a default that flowed into an execute). |
| `strat.rev` | integer | the same action, at tagging time | The strat's revision when the run was tagged. The version split keys on it (§3.8). |
| `strat.result` | `completed` \| `aborted` | human | Whether the strat was carried through. Absent means `completed`. |
| `strat.failure` | `utility-late` \| `entry-lost` \| `early-contact` \| `rotation-early` \| `info-lost` \| `economy` \| `other` | human, repeatable | Why it failed, for the failure breakdown. Only meaningful on lost or aborted runs. |

`StratEvidenceService` (App side) computes a `StratRecord` on demand, off the UI thread:

```csharp
public sealed record StratRecord(
    Guid StratId, int Revision,
    IReadOnlyList<StratRun> Runs,                            // one per evidence instance
    RecordSplit Total, IReadOnlyDictionary<string, RecordSplit> ByProvenance,   // key: Demo Provenance Labels value
    IReadOnlyDictionary<int, RecordSplit> ByRevision,        // key: strat.rev
    IReadOnlyDictionary<string, int> FailureBreakdown,       // key: strat.failure value
    bool SmallSample);                                       // Runs.Count < 8

public sealed record RecordSplit(int Run, int Won, int Lost, int Aborted, int Unknown);
public sealed record StratRun(TagInstanceRef Ref, string Sha256, int Round, int Revision, string? Provenance,
                              RunOutcome Outcome, IReadOnlyList<string> Failures);
public enum RunOutcome { Won, Lost, Aborted, Unknown }
```

- **Run** is every instance whose `strat` label matches and whose `source` is `human` or an accepted suggestion (the `TagQuery` default slice).
- **Aborted** is `strat.result == aborted`.
- **Won / Lost** comes from the parser namespace: the instance's `side` fact against Round Facts' `winner` fact (§5). When `winner` is absent (Round Facts not yet run on that demo) the human `outcome` label from the shipped palette (`won | lost`, `tag-store.md` §3.4) is used; when both are absent the run is `Unknown` and the panel says how many are unknown rather than folding them into lost.
- **ByProvenance** keys on Demo Provenance Labels' value for the instance's demo (`official | scrim | our scrim | matchmaking`, pending Team Identity §8 decision 4). The lookup is one call, `IDemoProvenanceSource.LabelFor(sha256)`, defined by that item.
- **SmallSample** is the plan's "caution under eight".
- **Every number is a clip**: each `RecordSplit` cell is backed by the `StratRun` list that produced it, and a `TagInstanceRef` resolves to a path through `DemoCacheStore.TryGetIndexBySha256` (Content Identity) and seeks with `RequestSeekToTick`; the Review Queue takes the same ref in Phase 4 (`tag-store.md` §3.7).

Cost: the Tag Store's `LoadDocuments` scans 1000 sidecars in 222 ms warm (`tag-store.md` §2.9). To skip documents without opening them, this design asks the Tag Store for one additive index column, `TagIndexEntry.stratIds[]` (§5); until it exists the panel scans every document with instances and debounces.

### 3.7 Callout Aliases

The canonical vocabulary is the nav place name (§2.4). A team defines its own words over it; the UI shows the team's words; the file stores the canonical name. The table lives **per owner, per map**, beside the strats it names:

```jsonc
// <config>/strats/<owner>/<map>/callouts.json
{
  "schemaVersion": 1,
  "map": "de_mirage",
  "canonical": { "source": "pawn", "names": ["Apartments", "BackAlley", "BombsiteA", "…"] },   // snapshot for validation; optional
  "aliases": [
    { "alias": "palace",   "place": "PalaceInterior", "primary": true },
    { "alias": "A ramp",   "place": "TRamp",          "primary": true },
    { "alias": "ramp",     "place": "TRamp" },
    { "alias": "window",   "place": "SnipersNest",    "primary": true },
    { "alias": "con",      "place": "Connector" }
  ]
}
```

- **Resolution** (`CalloutResolver`, pure): fold case, trim, collapse whitespace and hyphens; exact canonical name first, then alias; else null. `""` is unresolved (§2.4). Several aliases may map to one place; one alias maps to one place (the validator refuses a duplicate alias).
- **Display**: the `primary` alias for the owner if one exists, else the canonical name split on case boundaries (`PalaceInterior` → `Palace Interior`, `TopofMid` → `Top of Mid` needs a stop-word rule; the split is a display helper with a short override list, not data).
- **Canonical source**: the per-map name list from Place Names From The Pawn ships as an embedded resource (`Resources/callouts/<map>.places.json`), one file per shipped map, so a fresh install has the Valve names with no aliases. When Zone Baking's bundle carries a `zones` block the bundle's place list supersedes the resource for that map (the bundle is per map version; the resource is not). `canonical.names` in the file is a snapshot at the time the user last edited aliases and is used only to warn when a name vanished.
- **Unknown places warn, never refuse**: a map update can add or rename an area, and a strat written against the old name must still open.
- The Callout Aliases build item owns the editing UI and the "copy aliases from another owner" action; this design owns the file and the resolver.

### 3.8 Version history: the append-only diff log

Every `StratStore.Save` bumps `revision` and appends **one line** to `<id>.history.jsonl`:

```jsonc
{ "revision": 5, "atUtc": "2026-09-23T15:40:03Z", "summary": "molotov moved from 1:22 to 1:16",
  "ops": [ { "op": "replace", "path": "/steps/3/atSeconds", "from": 82, "value": 76 } ] }
```

- `ops` is a subset of RFC 6902 JSON Patch: `add`, `remove`, `replace`, with `path` a JSON Pointer into the strat document. `from` carries the displaced value on `replace` and `remove` (not in RFC 6902) so a diff can be rendered and an entry inverted **without replaying**. Revision 1 is a single `add` at `""` (the whole document).
- **Append-only.** The file is opened for append and never rewritten. The strat file is the materialized state. Write order is history line first, strat file second (atomic); on load, if `strat.revision` is behind the last history line, the missing ops are re-applied and the strat file rewritten, so a crash between the two writes loses nothing.
- **Materialize(id, revision)** applies ops 1..n to an empty document; `HistoryEntry.Inverse()` swaps `value` and `from`. Both are pure over the log.
- **Summary** is generated from the ops by `StratDiffPhrasing` using the callout display names ("molotov moved from 1:22 to 1:16", "step added: C peeks Connector at 1:05", "branch removed", "status Active → Archived"). Free text; the ops are the truth.
- **Granularity.** The Strat Book session (§3.12) holds an undo stack of the same ops. A history entry is written per **commit**, where a commit is an explicit Save, a tab deactivate, a demo swap, shutdown, or 30 s of idle after the last edit. Consecutive ops on the same `path` inside one commit are merged (a drag that fired 40 replaces becomes one). This keeps the log readable; it is decision 2 in §8.
- **The record split** (Strat Version History, Strat Record Panel) keys on the instance's `strat.rev` label: runs tagged at revision `< n` sit on one side of history entry `n` and runs at `>= n` on the other. A coach who retags old demos after a change tags them at the current revision, which is what "we tried the new timing on old footage" should mean; the panel also offers a by-demo-date split (Team Identity's `orderTicks`) for the other reading.

### 3.9 What Step Authoring must serialize (fields reserved here)

Step Authoring (its own design) adds the interpolated keyframe model over the canvas. This document reserves the fields on a step so that its documents are forward-compatible with the strats written before it, and defines their shape but not their motion semantics:

| Field | Shape | Reserved meaning |
|---|---|---|
| `positions[]` | `{ slot: "A", x, y, levelMinZ, yawDegrees? }`, at most one per slot, world units, `levelMinZ` the level's quantized lower Z (never a floor index) | Where each token is at `atSeconds`. A step without a slot's entry inherits the previous step's. |
| `strokes[]` | Annotation elements in the `.dvann.json` element shape (`annotations-format.md` "An element") with `fromTick`/`untilTick`/fade fields **omitted**, and `space` restricted to `world` | Per-step drawings (arrows, text, freehand), visible for the step's window. Reusing the element schema is what lets Shape Tools' writer and the export layer draw them unchanged. |
| `holdSeconds` | double or null | How long the state holds before the next step's motion begins. |
| `interpolation` | string or null | The motion kind between this step and the next; Step Authoring defines the vocabulary (`linear`, `hold`, `path` are expected). Null means "Step Authoring's default". |
| `path[]` on a position | reserved, absent in v1 | Waypoints for a non-straight move. Step Authoring may add it additively. |

The strat's own timeline for playback and export is `[max(atSeconds) .. min(atSeconds)]` on the round clock, in `clock.roundSeconds − atSeconds` seconds from the round start. A `StratFrameSource : ISceneFrameSource` (Strat Export) yields one `Scene2DFrame` per output frame with `PlayerMarker`s built from the interpolated positions (`Team` from `side`, `Label` from the slot's resolved name or letter, `SteamId` 0) and strokes through the annotation layer; `SceneExportSession` needs no change. At 20 fps the GIF ceiling of 1800 frames is 90 s of strat time, so Strat Export will offer a timescale; that is its concern.

### 3.10 Validation

`StratValidator.Validate(doc, callouts, book, index) → IReadOnlyList<StratIssue(Severity, JsonPointer, Message)>`. Run on load (issues shown, document still opens) and on save (refusals block the save).

| Rule | Severity |
|---|---|
| `schemaVersion` missing or below 1; `id` not a GUID; `map` empty; `side` not `T`/`CT`; `status` outside the four values | refuse |
| `slots` not exactly `A..E` in order | refuse |
| `steps[].atSeconds` increasing anywhere along the list; `actor` not a slot or `all`; `verb` outside the vocabulary; `id` duplicated | refuse |
| A branch whose `afterStepId` is not a step, or whose `target.stratId == id` with a `stepId` that is not a step, or whose target step is not later than `afterStepId` | refuse |
| Duplicate alias in `callouts.json`; alias mapping to an empty place | refuse (callouts file) |
| `type` outside the vocabulary; `type`/`side`/`targetSite` outside the applicability table | warn |
| `economy` outside Round Facts' vocabulary | warn |
| A `from`/`to`/`landing` place not in the canonical list for the map | warn |
| `atSeconds > clock.roundSeconds`, or `< −60` | warn |
| Branch `target.stratId` not in this owner's book on this map | warn |
| `utility.lineupId` set but no Utility Book index to check it against | info |
| Unknown top-level or step fields (the extension bag is non-empty) | info, listed by name |

### 3.11 The service and the module

All types live in `src/App/DemoViewer.NET/Services/Strats/`, a sibling of `Services/DemoCache/` and `Services/Tags/`:

| File | Holds |
|---|---|
| `StratModels.cs` | `StratDocument`, `StratOwner`, `StratSlot`, `StratStep`, `PlaceRef`, `UtilityRef`, `StratBranch`, `StratClockInfo`, `StratIndexEntry`, `StratIndexFile`, `StratBook`, `CalloutTable`, `HistoryEntry`, `PatchOp`; `StratJsonContext` (source-generated) |
| `StratStore.cs` | Index plus per-owner folders, atomic writes, the history append, in-memory on the browser |
| `StratSession.cs` | The live document in the Strat Book tab: undo/redo over `PatchOp`, commit rule, autosave |
| `StratClock.cs` | §3.4 |
| `StratValidator.cs` | §3.10 |
| `CalloutResolver.cs` | §3.7 |
| `StratEvidenceService.cs` | §3.6, over `TagStore.LoadDocuments` and `TagQuery.Find` |
| `StratHistory.cs` | `Materialize`, `Inverse`, `StratDiffPhrasing` |
| `RoleSheet.cs` | §3.14 |
| `StratTextExporter.cs` | §3.13 |

```csharp
public sealed class StratStore
{
    public StratStore(string? stratsRoot, Action<Action>? post = null);    // null root = in-memory (browser, tests)
    public bool IsPersistent { get; }
    public IReadOnlyList<StratIndexEntry> Index { get; }                    // snapshot, safe off-lock
    public event Action<Guid?>? Changed;                                    // strat id, or null for a batch / rebuild

    public StratDocument? TryLoad(Guid id);                                 // null when absent or corrupt; issues via Validate
    public StratDocument Create(StratOwner owner, string map, string side, string type, string name);
    public StratSaveResult Save(StratDocument doc, IReadOnlyList<PatchOp> ops, string? summary);   // bumps revision, appends history, atomic
    public bool SetStatus(Guid id, StratStatus status);                     // a Save with one op
    public bool Delete(Guid id);                                            // moves both files to <owner>/<map>/.trash/; never a hard delete
    public IReadOnlyList<HistoryEntry> History(Guid id);
    public StratDocument Materialize(Guid id, int revision);

    public StratBook LoadBook(StratOwner owner);       public bool SaveBook(StratBook book);
    public CalloutTable LoadCallouts(StratOwner owner, string map);   public bool SaveCallouts(CalloutTable table);
    public IReadOnlyList<StratIndexEntry> Query(StratOwner? owner, string? map, string? side, StratStatus? status);

    public void SaveIndex();                                                // deferred, like DemoCacheStore.SaveIndex
    public void RebuildIndexFromDisk();
    public static string FolderFor(string stratsRoot, StratOwner owner, string map);
}
```

- **Single writer.** The Strat Book tab checks a strat out into a `StratSession`; `Save` from anywhere else (Create Strat From Round, an import) routes through the session while it is checked out, the Tag Store's `CheckOut` rule (`tag-store.md` §3.5). Cross-process is last-writer-wins on atomic files, the annotation store's guarantee.
- **Failures are reported.** `Save` returns a result with a reason; the tab shows "strat could not be saved" on its status line, the annotation wording.
- **Module.** `StratBookModule : IWorkspaceModule` in `Modules/StratBook/`, id `net.demoviewer.stratbook`, one Main-strip tab `stratbook.browser` ("Strat Book"), feature id `tab.stratbook` in `FeatureCatalog` following the `tab.highlights` entry and its persisted-key warning (`FeatureCatalog.cs:59-63`), delegate-injected VM, registered in `App.BuildRegistry` beside `HighlightsModule` (`App.axaml.cs:869`). The tab holds: the book selector (owner, defaulting to `Us` or `me`), the map and side filters, the strat list from the index, the strat editor (metadata, slots, the step table, branches), the record pane (Strat Record Panel), the history pane (Strat Version History), and the callouts editor (Callout Aliases). The canvas for Step Authoring is hosted in this tab, not in 2D Playback, because a strat has no demo.
- **Other touchpoints.** 2D Playback's context menu gains "Create strat from this round" (that item). The Tag Palette gains a `strat` label panel whose buttons are the active book's strats for the open demo's map (the Tag Palette item reads `StratStore.Query`). The Library gains nothing.

### 3.12 Sessions, undo and autosave

`StratSession` mirrors `TagSession`: `Apply(PatchOp)` computes the inverse from the document (the `from` value), pushes it on a 200-entry undo stack, clears redo, bumps `Version`; `Undo`/`Redo` bind to `Ctrl+Z` / `Ctrl+Shift+Z` while the Strat Book tab has focus. A drag on the canvas is one gesture (Step Authoring's concern); a metadata edit is one op. Autosave writes the **strat file** debounced (500 ms, off-thread, stale snapshot stands down, the annotation controller's rules) so a crash loses at most half a second, and the **history entry** is written at commit (§3.8), which is when `revision` moves. Between commits the file on disk carries the pending edits with the previous revision number and a `pending: true` marker in the extension bag; a load that finds `pending` re-opens the session with those edits uncommitted.

### 3.13 Export shapes

| Shape | What | Notes |
|---|---|---|
| `.dvstrat.json` | One strat, as stored | Copy the file. Import into another book keeps the id, rewrites `owner`, and appends a history entry "imported from <book>". An id collision offers "replace (keep newer revision)" or "duplicate with a new id". |
| Book folder (`.dvbook.zip`) | The owner folder zipped: `book.json`, every map's `callouts.json`, strats and histories | The sharing unit for a team. Import as above per strat; callouts merge by alias with the importing side winning on conflict. |
| Call sheet (Markdown) | One strat as text, callouts in the owner's words, one line per step, branches as "if … → …" | For Discord and for a text diff. The line format is `**1:30** B throws smoke A ramp → A site (Stairs)`; `StratTextExporter`. |
| Role sheet (HTML) | §3.14, one page per slot, print stylesheet | Role View And LAN Print. |
| GIF / MP4 | The strat played through `SceneExportSession` via `StratFrameSource` | Strat Export, after Step Authoring. |

Nothing is exported to Sportscode XML from here: the evidence instances already export through the Tag Store's exporter, and the `strat` label goes with them as an ordinary group.

### 3.14 What Role View And LAN Print derive

`RoleSheet.Derive(doc, slot, callouts, roster) → RoleSheet` is pure over the model:

- **Header**: strat name, map, side, type, target site, economy, tempo, trigger text, status, revision, the slot letter and its resolved name (or blank line to write on), the slot's `role`.
- **Lines**: every step whose `actor` is this slot or `all`, in step order, phrased by verb with callout display names, the utility kind and landing, and the step `note`. Steps for other slots that this slot's steps depend on (the smoke thrown one second before this slot's push) are included as greyed context lines when their `to` place equals this slot's `from` or `to` place at the same or earlier time. That rule is simple and wrong in some cases; the sheet has a "show all steps" toggle.
- **Branches**: every branch whose `afterStepId` is one of this slot's lines, as "if <condition> → <target strat name>, step <n>".
- **Mini-map**: the slot's `positions` across steps as a polyline on the radar, when Step Authoring has written them; omitted otherwise.
- **LAN Print** writes the sheets as one self-contained HTML file with `@media print` page breaks per slot and opens it in the system browser. Avalonia has no printing API; HTML print is the route with the least new code, and the file is also what a coach emails. Decision 8 in §8.

---

## 4. Alternatives considered and why not

| Alternative | Why not |
|---|---|
| **A strat as an annotation document** (`.dvann.json` with a `strat` block). | Annotations are per demo, keyed by the demo hash, on the frame clock, and the store looks for them beside the demo. A strat has no demo. Reusing the *element* schema for strokes (§3.9) takes what fits and leaves the rest. |
| **Store the evidence list on the strat** (a `runs[]` array of `{sha256, instanceId}`). | Two sources of truth for one fact: tagging a round in the palette would have to write the strat, and deleting the instance would orphan the ref. The label on the instance is the single record, and `TagQuery` already answers "every instance with this label". |
| **Player names or SteamIDs as actors** instead of slots. | The brief and the artifact say slots, and for the right reason: rosters change and stand-ins are routine (Team Identity §3.3 measured a single stand-in breaking `k = 4`). The slot map is one indirection and lives where rosters live. |
| **Steps on demo ticks** (a strat authored against one demo). | Only Create Strat From Round has a demo. Every later use (record across demos, role sheet, export without a demo) needs the round clock. The mapping in §3.4 is two lines; the other direction would need it too. |
| **One `strats.json` per owner.** | The `GraphBreakpoints.v2.json` shape: a growing file rewritten on every autosave, one corrupt write loses every strat. Rejected for the same reason the Tag Store and the cache reject it. |
| **History inside the strat file.** | The file would grow on every commit and the history could never be append-only; a partial write would risk the current state. A sibling `.jsonl` is append-only by construction and the strat file stays a snapshot. |
| **Full snapshots per revision** instead of ops. | 20 KB per revision, and a diff view would have to diff snapshots. Ops are the diff. `Materialize` exists for the rare full-state need. |
| **A global alias table per map** shared by all owners. | Two teams in one library (a coach with two rosters) use different words for the same place. Per owner, with "copy from", covers both. |
| **SQLite** for strats and evidence. | Tens of documents, read once, edited by hand; JSON keeps the sharing story (copy a folder) and the current pattern. D2 belongs to the Round Index; the evidence query is the Tag Store's and is measured fast enough. |
| **A dedicated `stratId` field on `TagInstance`.** | A Tag Store schema change for one consumer, and it would make "this round ran two strats" impossible. A repeatable label group is exactly the Sportscode lesson the Tag Store learned (`tag-store.md` §2.6). |
| **Automatic won/lost from the human `outcome` label only.** | The parser knows who won; a human label is for what the parser cannot know. Facts first, human as the fallback before Round Facts exists. |

---

## 5. External and engine changes required

**CS2DemoKit:** none. `ClipRounds.Derive` and `PositionSampler.Walk` (0.12.0) supply the round start ticks and the place vocabulary as they are.

**AssetBaker bundle schema:** none required. When Zone Baking adds a `zones` block, its place list supersedes the embedded canonical list per map (§3.7); this document only says it will read it, and defines nothing about its shape.

**CSVG game plugin:** none. Walk It In The Server remains deferred (D4).

**Interfaces this design needs from sibling designs** (not external projects, listed so the integrator can check them):

| From | Needed | Shape |
|---|---|---|
| Round Facts | three fact groups on every instance | `winner` (`T` \| `CT` \| `none`), `roundTime` (seconds, from `m_iRoundTime`), and `plantTick` (already listed in `tag-store.md` §3.3) for the reserved `plant` clock |
| Tag Store | reserved human label groups; one additive index column | `strat`, `strat.rev`, `strat.result`, `strat.failure` refused as palette groups; `TagIndexEntry.stratIds[]` (distinct `strat` label values in the document) so the record panel can skip documents |
| Team Identity | as designed | `Team.Id`, `Epochs[]`, the members table for names, `Us`, `MyAccounts`, `GetAssignment(demoPath)` |
| Demo Provenance Labels | one lookup | `IDemoProvenanceSource.LabelFor(sha256) → string?` with the vocabulary decided in Team Identity §8 decision 4 |
| Content Identity | as designed | `TryGetIndexBySha256`, `IModuleContext.DemoSha256` |
| Place Names From The Pawn | the per-map canonical list | `Resources/callouts/<map>.places.json`, one per shipped map, generated from its table |
| Utility Book (Lineup Cards) | an id | `Lineup.Id` is a GUID; nothing else is assumed |

---

## 6. Risks and unknowns

| Risk | Likelihood | Mitigation |
|---|---|---|
| Round Facts chooses a different buy-type vocabulary from `pistol \| eco \| force \| full` | Medium | `economy` is validated as a warning against whatever Round Facts exports; the strat model adopts Round Facts' names at build time and a golden pins them. Recorded in `openQuestions`. |
| Round Facts ships without a `winner` fact | Low | The `outcome` human label fallback (§3.6) and an `Unknown` bucket the panel shows honestly. |
| `strat.rev` is wrong for retagged old footage | Medium | Documented meaning (§3.8) plus the by-date split as the second view. |
| A place spans two floors (de_nuke `Ramp`, −640 to −310) so a step "to Ramp" with no `positions` has no floor | Certain on multi-level maps | The role sheet and the call sheet do not need a floor; the canvas does, and Step Authoring writes `levelMinZ` per position. Create Strat From Round has the pawn's Z. |
| The pawn's empty-string place lands in a step through Create Strat From Round | Low | `CalloutResolver` maps `""` to null; the validator warns on a null `to` on a `move`. |
| A map update renames a nav place | Low, real over years | Warn, never refuse; `canonical.names` snapshot shows what changed; aliases are re-pointed by the user. |
| History and strat file diverge after a crash | Low | History first, strat second, reconcile on load (§3.8); a test kills between the two writes. |
| Evidence scan cost without `stratIds[]` | Low | 222 ms per 1000 tagged demos measured; debounced, off-thread; the column is asked for. |
| Two books, one strat id (a shared `.dvstrat.json`) | Medium for teams | Ids are preserved on import by design so records follow the strat; the collision dialog (§3.13) is explicit. |
| Team Identity's `Us` changes or a team is merged | Low | The owner folder is keyed by team GUID, which merge tombstones. The store offers "move book to team X" (a folder rename plus `owner` rewrite in every file, one history entry each). |
| Type applicability is only a warning, so lists can carry a CT "execute" | Certain, harmless | The Strat Book filters by `type` and `side` independently. |
| The browser host | Certain | Session-only with the status line; `wasm-matrix.md` row. |

Unknown until Step Authoring is designed: whether per-step `strokes` reuse the annotation element DTO verbatim (this design assumes yes, minus the time fields) or need a strat-specific projection. Unknown until Round Facts is designed: whether a `plant` clock is worth defining in v1 (§8, decision 9).

---

## 7. Test and verification strategy

All tests are TUnit, in `src/App/DemoViewer.NET.App.Tests/` following `DemoCacheStoreTests` (temp root per test, `AppPaths.ConfigDirEnvVar`).

| Test | Pins |
|---|---|
| `StratSchemaSnapshotTests.V1Schema_MatchesCheckedInSample` | `tests/fixtures/strats/schema-v1.sample.dvstrat.json` round-trips byte-identical through `StratStore`, with an injected unknown field at root, step, branch and position level; regenerated only under `PB2D_GOLDEN_UPDATE=1` |
| `StratSchemaSnapshotTests.V1History_MatchesCheckedInSample` | `schema-v1.sample.history.jsonl`: `Materialize(n)` for every `n` equals the committed snapshots; `Inverse` of every entry applied in reverse returns to revision 1 |
| `StratClockTests` | `tick(90 s, round starting 1761, 64, 115) == 1761 + 1600`; `atSeconds(plant at 1761 + 26.2 × 64) == 88.8`; a tick before freeze-end maps to null; negative `atSeconds` round-trips; the two measured demos' freeze-end ticks and `115` are the fixture values |
| `StratStoreTests` | folder layout; index rebuild from disk; corrupt strat → null with an issue; corrupt index → rebuild; atomic overwrite leaves no `.tmp`; `Save` returns a failure on a read-only root and does not throw; null root holds many strats; history is appended, never rewritten (file length only grows); crash between history and strat write is reconciled on load; `Delete` moves to `.trash/` |
| `StratValidatorTests` | every refuse row and every warn row in §3.10, one case each; unknown fields listed as info |
| `StratSessionTests` | one op per undo entry; a merged drag is one history op; `pending` marker round-trips; 200-entry cap; commit on idle |
| `CalloutResolverTests` | canonical before alias; case and whitespace folding; `""` → null; duplicate alias refused; display prefers primary; `TopofMid` display split |
| `StratEvidenceTests` | over a fixture of three tag documents: run/won/lost/aborted counts; `winner` fact beats `outcome` label; `Unknown` when both absent; `ByProvenance` and `ByRevision` splits; failure breakdown counts repeated groups once per value; `SmallSample` at 7 and not at 8 |
| `RoleSheetTests` | lines for `actor == slot` and `all`; context lines by the §3.14 rule; branches attached to the right lines; unassigned slot prints the letter |
| `StratTextExporterTests` | the call sheet for the sample strat is a committed `.md` golden |
| `Playback2DFeatureCatalogTests` | `tab.stratbook` present with the right scope |
| `SettingsWasmRoundTripTests` | unchanged: this design adds no settings row |

Manual verification at build time: create a book for the owner's trio team (Team Identity), write a de_mirage A execute with six steps, tag three rounds of a matchmaking replay as runs through the palette's `strat` panel, confirm the record pane shows 3 runs split by provenance, move one step's time, confirm the history line and the by-revision split, export the call sheet and the role sheet, and check the browser host shows the session-only line. Add the `wasm-matrix.md` row.

---

## 8. Decisions for the owner

1. **Owner model.** Books keyed by Team Identity team GUID plus a single `me` book (recommended, §3.2), or books only for teams (a matchmaking-only user would have no book until they name a team).
2. **History granularity.** One history entry per commit with the 30 s idle rule and same-path merging (recommended, §3.8), or one entry per explicit Save only (cleaner log, more lost detail), or one per autosave (noisy).
3. **Evidence linkage.** Reserved human label groups `strat`, `strat.rev`, `strat.result`, `strat.failure` on Tag Store instances (recommended, §3.6), or a dedicated field on `TagInstance` (a Tag Store schema change, one strat per instance).
4. **Won/lost precedence.** Round Facts `winner` first, human `outcome` as fallback (recommended), or human first.
5. **Alias table location.** Per owner per map beside the strats (recommended, §3.7), or one table per map shared by every owner.
6. **Type applicability.** Warn (recommended, §3.3.1) or refuse.
7. **Failure vocabulary.** The seven values in §3.6 as the shipped set, editable per palette, or free text only.
8. **LAN Print route.** Self-contained HTML opened in the system browser (recommended, §3.14), or an Avalonia-rendered bitmap per sheet.
9. **`plant` clock in v1.** Reserve only (recommended) or define now, which needs Round Facts' `plantTick` on every instance and a second mapping in `StratClock`.
10. **Delete semantics.** Move to `.trash/` (recommended, §3.11) or hard delete with the history file.

---

## 9. Effort estimate and sequencing

| Step | Item | Estimate | Blocked by |
|---|---|---|---|
| 1 | `StratModels`, `StratJsonContext`, `StratStore` (folders, index, history append, reconcile), JSON and history goldens | 3 days | nothing |
| 2 | `StratClock`, `StratValidator`, `CalloutResolver`, embedded canonical lists, tests | 1.5 days | step 1; Place Names From The Pawn for the resource files (a stub list per map until then) |
| 3 | `StratSession` (undo, commit rule, autosave, `pending`), `StratBookModule` shell, feature id, list and editor views (metadata, slots, step table, branches) | 3 days | step 1, Team Identity build (for the owner selector; `me` works without it) |
| 4 | `StratEvidenceService`, record pane data (the Strat Record Panel UI is its own item) | 1 day | step 1, Tag Store build |
| 5 | `StratHistory` (`Materialize`, `Inverse`, phrasing), history pane data | 1 day | step 1 |
| 6 | `RoleSheet`, `StratTextExporter`, HTML print writer | 1 day | step 1 |
| 7 | `wasm-matrix.md` row, `docs/strat-room/strat-format.md` in the `annotations-format.md` shape | 0.5 day | steps 1 to 6 |

About eleven working days for the model, store and tab shell, after which Create Strat From Round, Step Authoring, Strat Export, Strat Record Panel, Strat Version History, Role View And LAN Print and Lineup On A Strat Step start against a finished contract. Steps 2, 4, 5 and 6 are independent once the models exist. Nothing touches the engine, the baker or the CSVG plugin.

---

## 10. Sources

Repository, `main` at `d90ec9f`:

- `docs/strat-room/plan.md` §2 (F7, F9, F14, F15, F17), §3 (Strat Model and every Phase 3 item, Callout Aliases, Lineup On A Strat Step), §6 (D2, D4).
- `docs/strat-room/designs/team-identity.md` §3.2, §3.5, §3.7, §3.10, §8; `docs/strat-room/designs/tag-store.md` §2.9, §3.2, §3.4, §3.5, §3.7, §3.10, §4.3.
- `docs/playback2d-v2/annotations-format.md`; `docs/playback2d-v2/wasm-matrix.md`; `docs/playback2d-v2/design.md`; `docs/plugins/plugin-system-design.md` §1.1.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs:24-31, 48-59, 157-170, 263-318, 559-573`; `DemoCacheModels.cs:84-120, 215-216, 447-465`.
- `src/App/DemoViewer.NET/Services/AppPaths.cs:54-66, 74-87, 128-145`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationIdentity.cs:17, 33-66`; `AnnotationDocumentDto.cs:15-30, 128-133`; `src/Playback2D/DemoViewer.NET.Playback2D.Core/Annotations/AnnotationElement.cs:14-33, 219-246, 267-331, 351-359`; `DocDelta.cs:15-41`; `AnnotationDocument.cs`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Export/ISceneFrameSource.cs:16`; `Pipeline/Export/SceneExportSession.cs:36-45, 130-170`; `Core/PlayerMarker.cs:28-45`; `Pipeline/SceneFixture.cs:17-60`; `Core/Levels/MapSpace.cs:81`.
- `src/App/DemoViewer.NET/Modules/Playback2D/GameInfo.cs:74-81`; `Playback2DTabViewModel.cs:1026-1071`; `Timeline/RoundTrack.cs:11-20`; `Modules/GameClock.cs`; `Modules/Library/DemoLibraryService.cs:1385-1391`; `Modules/Highlights/HighlightsModule.cs:31-62`; `App.axaml.cs:844-869`; `Features/FeatureCatalog.cs:59-63`; `src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs:16, 24, 97-118`.
- `rules/highlights_position.rules.yaml:3-5, 36-53`; `assets/de_mirage/bundle.json` (schema v1 shape).
- CS2DemoKit 0.12.0 XML docs: `CS2DemoKit.Parser.xml` (`PositionSample`, `PositionSampler.Walk`, `DemoParser.Parse`), `CS2DemoKit.Analysis.xml` (`ClipRounds`, `ClipRound`, `ClipWindows.RoundStartFor`).
- Memory: `cs2demokit-tick-clocks.md` (`GameTick` is the frame clock).

Measurements, 2026-09-23, scratch project `<session scratchpad>\strat-model` (Release, .NET 10, CS2DemoKit.Parser and CS2DemoKit.Analysis 0.12.0; outputs `out_clock_mirage.txt`, `out_places_mirage.txt`, `out_clock_nuke.txt`, `out_places_nuke.txt`):

- `match730_003842233788306292960_0260929275_408.dem` (de_mirage, build 10896, `GotvMatchmaking`, 118,309 frames, `ServerStartTick` 1303, parse 0.55 to 0.76 s).
- `match730_003842182368957825245_0056633905_389.dem` (de_nuke, build 10896, `GotvMatchmaking`, 123,589 frames, `ServerStartTick` 1993, parse 0.53 s).
- Both read-only from the Steam `replays` folder. The tour sample was not used.
- Sibling probe cited for the `env_cs_place` list: `scratchpad/zone-probe/out_de_mirage.txt` (Zone Baking, same date).

RFC 6902 (JSON Patch) and RFC 6901 (JSON Pointer) for the `ops` subset in §3.8; the `from` field on `replace`/`remove` is this design's addition and is named as such.
