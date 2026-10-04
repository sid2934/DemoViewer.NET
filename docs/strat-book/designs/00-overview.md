# Strat Room design set overview (integration record)

**Plan authority:** `plan.md` (§0, §2, §4, §6) · **Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0 · **Written:** 2026-09-23
**Status:** nine designs read in full and reconciled. None of the nine files has been edited. Every
cross-design disagreement below carries a numbered **Integrator correction**; where a design body and a
correction disagree, **the correction wins**, and this document is the registry both point at, the way
`docs/playback2d-v2/plans/00-overview.md` is for the Playback2D plan set. When a design is revised for
review, the author folds the corrections that name it into an `## Integrator corrections (BINDING)`
block at the top of that file, the house shape.

Every design indexed here has since been built; at the time of writing nothing was. Every claim about the tree cites a file; every measured number is the
design's own and is cited by section.

---

## 1. Master index

| Design | File | Owns | External or engine changes | Effort | Decisions raised |
|---|---|---|---|---|---|
| Team Identity | [`team-identity.md`](team-identity.md) | `Team` as data (GUID, name, roster epochs, core of 7), side-key clustering at `k = min(3, side)`, the single `IsUs` team, the `me` account list, opponent derivation, merge/split/tombstones, `<config>/teams.json` (truth) + `<config>/cache/team-index.json` (derived), `TeamIdentityService`, Library team filter, Teams tab `net.demoviewer.teams` | none | 7.5 d | 7 (§8) |
| Round Facts | [`round-facts.md`](round-facts.md) | `RoundFactsExtractor.Extract(ParsedDemo)` (pure), the per-round record with two `SideFacts`, `KillStep` timeline, `RoundPhases.At`, the CS Demo Manager buy-type classifier, the fourth stamped tier `RoundFacts` on `DemoCacheRecord`, `IRoundFactsSource`; answers plan research questions one and two | none required; two optional upstream proposals (§5) | ~5 d | 7 (§8) |
| Zone Baking (#5) | [`zone-baking.md`](zone-baking.md) | Baker `Zones.cs` + `--zones` top-up, app-owned `assets/<map>/zones.json` (places, hull-plane volumes, nav areas with flood-filled places, `areaLinks`, `adjacency`), `bundle.json` schemaVersion 2 with an additive `zones` reference, `ZoneSet` + `PlaceResolver` in `Playback2D.Core/Zones`, `ZoneAssetPipeline`, the `playback2d.zones` outline layer | none (engine reader tolerates the bump, measured §2.2) | 5.5 d | 7 (§8) |
| The Round Index | [`round-index.md`](round-index.md) | `PlaceCountToken`, `RoundIndexBuilder` (1 s cadence, alive-only via Round Facts `Kills`, side via `Slots`), `.dvri.json` sidecar with runs, `places` centroids and `transitions`, `RoundIndexEvaluator` (`roundindex`), stamps on record and index row, `SituationIndex`/`ISituationIndex` in-memory postings with four tolerance levels, `IPlaceAdjacency` seam + empirical graph, `PlaceSnap`, the live-count contract, the `Indexed` hook, `net.demoviewer.situations` | none required; two optional upstream proposals (§5) | 8.5 d | 12 (§8) |
| Tag Store | [`tag-store.md`](tag-store.md) | `.dvtag.json` per demo under `<config>/tags/demos/<sha256>` + `index.json`, instances with `labels` (human) and `facts` (parser) namespaces, positions/movements with `placeSource`, `TagStore` (atomic, single-writer `CheckOut`), `TagSession` undo/autosave, `TagFactsRefresher`, palettes as drop-in JSON, `TagQuery` (`Find`/`Pivot`/`At`), Tag Track, Sportscode-subset XML exporter, the exact Content Identity ask (§3.10) | none | 12 d (+1 to 2 d Content Identity) | 6 (§8) |
| Grenade Walk | [`grenade-walk.md`](grenade-walk.md) | `GrenadeWalker.Walk(demo, options)` mirroring `PositionSampler.Walk`, `GrenadeRow` (thrower, release/spawn/detonation, trajectory stride 4 with bounces, jump-throw with source, movement, `setpos`/`setang`), `IThrowerInputSource` seam for CS2DemoKit #53, `GrenadeIndexEvaluator` (`grenades`), two sibling sidecars, the smoke creation-packet decode bug report (§5.2) | CS2DemoKit bug report (drafted, not filed); optional `ProjectileSampler` proposal (§5.1); #53 assumed, not designed | 4.5 d | 7 (§8) |
| Suggested Tags | [`suggested-tags.md`](suggested-tags.md) | Route (a): six rule detectors over 1 Hz alive-only occupancy (Round Index token, walk fallback), learned site regions with a spawn filter, proposals with confidence factors and a stable identity key, `suggestions/` proposals + append-only verdicts, Proposal Track and queue with `J/K/Y/N/Enter/Ctrl+Y`, team parameter profile, tuning view; route (b) written as the upstream proposal (§5.2) | none for the first release; route (b) later; three doc corrections (§5.3) | ~15 d | 8 (§8) |
| Strat Model | [`strat-model.md`](strat-model.md) | `.dvstrat.json` per owner per map under `<config>/strats/`, `book.json` slot defaults per epoch, `callouts.json` per owner per map, append-only `.history.jsonl` of RFC 6902-style ops with `from`, the `round` clock block and `StratClock`, slots A to E, closed verb and type vocabularies, first-class branches, evidence by reserved `strat*` human labels, `StratEvidenceService`/`StratRecord`, `StratStore`, `StratSession`, validator, `RoleSheet`, exports, `net.demoviewer.stratbook` | none | 11 d | 10 (§8) |
| Step Authoring | [`step-authoring.md`](step-authoring.md) | Core keyframe model (`TokenKeyframe`, `TokenTrack`, `TokenTrackSet`, `StepSchedule`) on a 64-tick strat frame clock, stationary rule, midpoint level snap, ten tokens (`O1..O5` additive), per-step strokes projected into one `AnnotationDocument` by `TimeEnvelope`, `TokenTool` + `ITokenEditor` on `IToolServices`, `StratFrameSource` in Pipeline, one undo stack via `PatchOp`, Create Strat From Round pre-population, the exact Shape Tools contract (§3.2), keymap rows, goldens through `dv2d` | none | 18 d (4 of them Shape Tools) | 10 (§8) |

Total about **87 working days** of one person for the nine reviewed items, before the roughly 35
no-review build items in `plan.md` §3. Shape Tools (4 d, no review) is counted inside Step Authoring
because that design is its contract.

---

## 2. Dependency graph

```
Phase 0   Content Identity (build; spec = tag-store.md §3.10) ──┬─► Tag Store ─► Suggested Tags (accept/verdicts)
          Round Facts ──────────────────────────────────────────┼─► The Round Index ─► Suggested Tags (occupancy)
            │  (tier written in tier 2; Kills, Slots, winner)    │        │
            ├─► Tag Store (facts refresh)                        │        ├─► Query Canvas ─► Find Rounds Like This
            ├─► Strat Model (winner, roundTime, plantTick)       │        ├─► Result Cards ─► Overlay View
            └─► Search Filters, Result Cards, Dossier            │        └─► Watched Situations ◄── Team Identity
          Team Identity ──► Search Filters · Watched Situations · The Matrix · Strat Model (owner) · Provenance Labels
          Zone Baking ∥ ──► Tag Store placeSource upgrade · Round Index adjacency (preferred, optional)
                             · Grenade Index landing place · Suggested Tags detonation place · Query Canvas click
                             · Strat Model canonical place list
          Inputs Per Demo Source ─► CS2DemoKit #53 (upstream) ─► Grenade Walk (soft, via IThrowerInputSource)

Phase 2   Tag Store ─► Tag Track ─► Tag Palette ─► Suggested Tags validation (§7.3)
Phase 3   Shape Tools (independent) ─► Step Authoring ◄── Strat Model ◄── Team Identity, Tag Store
                                             └─► Strat Export · Create Strat From Round (◄ Round Facts, Team Identity)
Phase 4   Grenade Walk ─► Grenade Index (◄ Zone Baking) ─► Lineup Cards ─► Lineup On A Strat Step (◄ Strat Model lineupId)
```

**Hard edges** (a design that cannot be built to its own text without the other having landed):

| Blocked | Blocked on | What it needs |
|---|---|---|
| The Round Index (build) | Round Facts (design approved; build optional) | `RoundFactsTier` shape: `FreezeEndTick`, `EndTick`, `IsLive`, `SideFacts.Slots`, `KillStep`; the evaluator calls `RoundFactsExtractor.Extract` itself when the tier is absent (`round-index.md` §3.4) |
| Tag Store (ship) | Content Identity | the five items of `tag-store.md` §3.10; the build can start on the session-side fallback hash |
| Suggested Tags (steps 4 to 7) | Tag Store, Tag Track, Tag Palette | the instance write with provenance; the hand-tag validation |
| Strat Model (evidence, owner) | Tag Store, Team Identity | reserved label groups, `TagIndexEntry.stratIds[]`; `Team.Id`, epochs, `GetAssignment` |
| Step Authoring | Strat Model (build), Shape Tools (build) | `StratSession`, `StratStore`; the §3.2 tool set |
| Grenade Index | Grenade Walk | `.grenades.json` rows |
| Watched Situations | Team Identity, The Round Index | `DemosAgainst`, `Indexed` event, `ComputedAtTicks` |

**Soft edges** (a documented fallback exists): Zone Baking for every consumer (each names its no-zones
path); Team Identity for Tag Store `buy.us`/`buy.them` and Strat Model `me` books; CS2DemoKit #53 for
jump-throw flags; Round Facts' build for the Round Index (extractor call).

**Parallelism.** Zone Baking, Grenade Walk (walker and evaluator), Shape Tools and Suggested Tags steps
1 to 3 have no hard edge into Phase 0 or 1 and can run whenever a person is free. **No cycles**: the one
apparent cycle, Team Identity ↔ Round Facts over `SideAtRound`, is resolved by correction 9.

---

## 3. Cross-design conflicts and Integrator corrections (BINDING)

Anything two designs define differently, or one assumes and another contradicts. Each correction names
the files it binds. The resolution is the shape implementers code against.

### 3.1 Storage and identity

**Correction 1. One derived-sidecar convention under the cache root.** Three designs invented three
layouts for large per-demo derived data: Grenade Walk writes `demos/<StableKey>.grenades.json` through
a new `DemoCacheStore.WriteSibling`/`TryReadSibling` pair with deletion inside `Remove`/`DeleteSidecar`
(`grenade-walk.md` §3.9); The Round Index writes `cache/round-index/<StableKey>.dvri.json` through its
own `RoundIndexStore` with a `Changed` subscriber and a startup orphan sweep (`round-index.md` §3.3,
§3.6); Suggested Tags writes `cache/suggestions/<StableKey>.json` (`suggested-tags.md` §3.4). None of
these APIs exists at `d90ec9f` (`DemoCacheStore.cs:500-540` has only `SidecarPathFor`, `StableKey`,
`DeleteSidecar`). **Binding:** `DemoCacheStore` gains one seam, Grenade Walk's, generalised:
`WriteSibling(demoPath, string suffix, string json)`, `TryReadSibling(demoPath, suffix)`,
`SiblingPathFor(demoPath, suffix)`, and deletion of every `<StableKey>.*` sibling inside
`Remove`/`RemoveWhere`/`DeleteSidecar`, atomic on disk, dictionary in memory when `_cacheRoot` is null.
Files: `demos/<StableKey>.grenades.json`, `.grenades.paths.json`, `.dvri.json`, `.suggestions.json`.
The Round Index drops its own directory, store class and orphan sweep; Suggested Tags drops its
directory. Identity drift (`LoadOrCreate`) and library removal then cannot orphan a sibling for any
store, once. The rule for choosing tier versus sibling is Round Facts' and the Round Index's, stated
once: **inside the record when it is small and Match Overview reads it (Round Facts, about 20 KB);
a sibling when it is large or read only cross-demo (grenades, index rows, proposals).**

**Correction 2. Verdicts are user truth and leave the cache.** Suggested Tags keeps verdicts in
`<cacheRoot>/suggestions/<StableKey>.verdicts.json` as "append-only and never rebuilt"
(`suggested-tags.md` §3.4), under a root whose class doc says it "is rebuildable and is never a source
of truth" (`DemoCacheStore.cs:24-26`) and whose `LoadOrCreate` discards on drift; the Tag Store, Team
Identity and Strat Model all refuse to put user truth there for exactly that reason (`tag-store.md`
§4.3, `team-identity.md` §3.2, `strat-model.md` §2.1). Meanwhile the Tag Store models proposals as
instances with `source: "suggested"` and `suggestion { detector, confidence, state }`, keeps rejected
ones in the document, and makes retention its D6 (`tag-store.md` §3.3, §8); Suggested Tags rejects
storing proposals as instances at all (§4, "a proposal is not a tag until someone says so").
**Binding:** Suggested Tags' split stands (proposals are derived and rebuilt wholesale; verdicts are
append-only), but the verdict file moves under the config root keyed by content hash:
`<config>/tags/verdicts/<sha256>.verdicts.json`, written by `TagStore` with its atomic write and
in-memory mode, and listed in `wasm-matrix.md` with tags. The Tag Store's per-instance `suggestion`
block is withdrawn; only **accepted** proposals reach the document, as instances with
`source: "suggested"` (kept, so the Matrix can stratify by provenance) and Suggested Tags' free-form
`provenance` object (`suggested-tags.md` §3.4). `TagQuery`'s default slice becomes "every source but
`import`" and the `TagSource` filter stays. Tag Store D6 and Suggested Tags decision 4 are closed by
this correction.

**Correction 3. One join-key rule and one `DemoRef`.** Derived stores key by `StableKey(path)` and carry
`sha256` when known (Round Index, Grenade Walk, Suggested Tags, Team Identity assignments); user-truth
stores key by `sha256` (Tag Store, Strat Model evidence, Team Identity overrides, verdicts after
correction 2). Consumers cross the boundary in both directions: Tag Store §3.7 wants an opponent's
demos as a `sha256` set, Round Index `SituationQuery.Demos` wants StableKeys "from Team Identity", and
Team Identity's `DemosOf`/`DemosAgainst` return an undefined `DemoRef` (`team-identity.md` §3.7).
**Binding:** `DemoRef(string Path, string StableKey, string? Sha256)`, defined once in
`Services/DemoCache/DemoCacheModels.cs`, returned by Team Identity and accepted by every consumer; the
two bridges are `DemoCacheStore.TryGetIndex(path)` and Content Identity's `TryGetIndexBySha256`
(`tag-store.md` §3.10 item 3). No design converts keys on its own.

**Correction 4. Content Identity is built to `tag-store.md` §3.10.** Six designs assume
`DemoCacheRecord.Sha256` is populated (Team Identity overrides upgrade, Grenade Walk header, Round Index
`demo.sha256`, Suggested Tags verdict key, Strat Model `origin` and evidence, Tag Store everything); only
the Tag Store states what the build item must deliver. **Binding:** the five items in `tag-store.md`
§3.10 are Content Identity's specification, plus Team Identity's `demoStableKey → demoSha256` override
upgrade (`team-identity.md` §3.2). The hash helper lives in `Playback2D.Pipeline` (Tag Store D4,
recommended there; the CLI and the App both reach it). The clock header is filled with real
`firstTick`/`lastTick` by the same item (item 5), which is what correction 6 needs.

**Correction 5. Team Identity's split stays; the cache directory is not exempt for it.** Team Identity
decision 7 asks whether both files could live under `cache/`. Correction 2's rule answers it:
`teams.json` under the config root, `team-index.json` under `cache/`, as designed. Closed.

### 3.2 Clocks and keys

**Correction 6. `firstTick`/`lastTick` have one definition.** The Tag Store notes the app writes
`0, 0` today (`tag-store.md` §2.1, `Playback2DTabViewModel.cs:1133-1134`); Grenade Walk's example
header shows `firstTick: 0, lastTick: 132515` (§3.9); the Round Index and Suggested Tags show
`firstTick: 1, lastTick: 132516` (§3.3, §3.4), and the Round Index measured the first sample at tick 1
and the last at `TickCount` (§2.6, and `suggested-tags.md` §2.1). **Binding:** `firstTick` is the first
frame's `ServerTick` (the frame clock, measured 1 on Valve demos) and `lastTick` is the last frame's
(`TickCount`); Content Identity fills them through one helper that every new store calls; the examples
are illustrative. `ClockIdentity.Matches` already treats all-zero as unknown, so old documents keep
loading.

**Correction 7. The strat frame clock constant lives in Core.** Step Authoring references
`StratClock.TicksPerSecond = 64` as the strat frame rate (`step-authoring.md` §3.3) while the Strat
Model places `StratClock` in the App (`strat-model.md` §3.11, `Services/Strats/StratClock.cs`), and
Step Authoring's `StepSchedule` is a Core type. **Binding:** the constant is
`StepSchedule.TicksPerSecond = 64` in `Playback2D.Core.Keyframes`; the App's `StratClock` (demo mapping
in seconds, `strat-model.md` §3.4) reads it. The two never meet a demo clock; the projected document's
header is `ClockIdentity("dv-strat-clock", 64, lastTick + 1, 0, lastTick)` as Step Authoring §3.5 says.

**Correction 8. Two quantized Z values, two names, never confused.** Zone Baking areas carry
`floor = MapSpace.QuantizeZ(band.MinZ)` (a level key, `zone-baking.md` §3.2); Tag Store positions and
Strat Model/Step Authoring positions carry `levelMinZ = MapSpace.QuantizeZ(level.ZMin)` (the same level
key, the annotation anchor rule); the Round Index's `places.z` buckets are `MapSpace.QuantizeZ(sample.Z)`
and its text says "never a level key" (`round-index.md` §3.3). All three are consistent in intent.
**Binding:** the word **`levelMinZ`** (or `floorKey`) always means a band lower bound; the word
**`zBucket`** always means a quantized sample Z; `PlaceSnap.Nearest` takes a `[minZ, maxZ)` band and
folds buckets into it; `PlaceResolver.ResolveOnFloor` takes a `floorKey`. No store may carry one under
the other's name.

### 3.3 Round Facts as the shared vocabulary

**Correction 9. `SideAtRound` belongs to Team Identity.** Team Identity says the per-round side API
"will grow `SideAtRound(demoPath, teamId, round)` there [Round Facts], not here" (`team-identity.md`
§3.7); Round Facts says "Nothing in Round Facts names a team" and that Team Identity joins on
`SideFacts.Slots` (`round-facts.md` §3.6). Each points at the other. **Binding:** `TeamIdentityService`
gains `int? SideAtRound(string demoPath, Guid teamId, int roundNumber)`, implemented by joining
`IRoundFactsSource.TryGet(demoPath).Rounds[n].Ct.Slots / T.Slots` against the record's `Players` (slot
to SteamID) and the team's side key. Round Facts stays team-free.

**Correction 10. The parser-namespace fact vocabulary is Round Facts', absolute per side, and one list.**
Four spellings exist: Tag Store's example `side, buy.us, buy.them, score, endReason, plantSite`
(`tag-store.md` §3.2); Round Facts' consumer row `parser.side, parser.buy.ct, parser.buy.t,
parser.score.ct, parser.score.t, parser.end.reason, parser.plant.site, parser.phase, parser.mancount`
(`round-facts.md` §3.6); Strat Model's ask for `winner`, `roundTime`, `plantTick` (`strat-model.md`
§5); and Strat Model's `economy` enum `pistol | eco | force | full | any`, which lacks Round Facts'
`Semi` (`round-facts.md` §3.2). `side` and `buy.us`/`buy.them` are relative to something Round Facts
cannot know: an instance has no side unless a human or Team Identity says so. **Binding:** the `facts`
array is the namespace, so groups carry no `parser.` prefix; the list, owned by Round Facts and
exposed through `IRoundFactsSource.FactsFor(demoPath, roundNumber)` plus `int Schema` (the Tag Store's
ask, `tag-store.md` §3.3): `round`, `matchRound`, `half`, `buy.ct`, `buy.t`, `score.ct`, `score.t`,
`winner` (`T | CT | none`), `endReason`, `plantSite`, `plantTick`, `roundTime`, `phase` (at the
instance's `fromTick`), `manCount.ct`, `manCount.t` (at `fromTick`). Buy-type values are Round Facts'
five, lower-cased: `pistol | eco | semi | force | full | unknown`; the Strat Model's `economy` adopts
the same five plus `any`. Relative facts (`side`, `buy.us`, `buy.them`, `opponent`) are **not** written
by the refresher; a consumer derives them at query time from Team Identity's `GetAssignment(...).OurSide`
(or `SideAtRound`, correction 9), and a detector or palette writes `side` as a human label when the
instance is about one side (Suggested Tags proposals already carry `side`). The Strat Model's won/lost
rule (`strat-model.md` §3.6) therefore uses the strat's own `side` against the round's `winner` fact
and needs no side fact on the instance; the human `outcome` label stays the fallback.

**Correction 11. Round bounds and alive state come from Round Facts everywhere.** Suggested Tags bounds
a round by `[freeze_end, round_officially_ended or next freeze_end)` and derives alive from raw
`player_death` events (`suggested-tags.md` §2.3, §3.2); the Round Index uses Round Facts'
`[FreezeEndTick, EndTick)` where `EndTick` is the win-status transition, seven seconds before
`round_officially_ended` (`round-index.md` §3.1, `round-facts.md` §2.7), and alive from Round Facts
`Kills`. The two occupancy sources would disagree in the post-round window, and the retake and opener
detectors could fire on win-panel positions. **Binding:** `RoundOccupancyBuilder` (the walk fallback)
takes the same `RoundFactsTier` the index does: windows from `FreezeEndTick`/`EndTick`, alive from
`Kills`, sides from `Slots`, and `PlantSite` from the fact rather than re-deriving it from the planter's
place. Both sources then produce identical `RoundOccupancy` rows for the same demo, which is the property
Suggested Tags' §3.2 claims.

**Correction 12. Round numbering is `ClipRound.Number`, everywhere.** Round Facts (`Number ==
ClipRound.Number`), the Round Index (`RoundNumber`), the Tag Store (`round` derived from
`DemoCacheRecord.Rounds`), Suggested Tags proposals (`round`), Strat Model `origin.round`, Grenade Walk
`RoundNumber` (last `ClipRound` at or before `SpawnTick`, 0 before the first) all agree. Recorded so
nobody keys on `MatchRoundNumber` (`m_totalRoundsPlayed + 1`), which is a fact, not a key
(`round-facts.md` §4).

### 3.4 Zones and places

**Correction 13. The place-resolution stamp is `zonesVersion`, not the bundle schema version.** The Tag
Store reserves `placeSource: "zone-bake:<n>"` where `n` is "the bundle schema version that Zone Baking
defines" (`tag-store.md` §3.6, §5); Zone Baking says consumers must store the per-map `zonesVersion`
CRC and re-resolve when it differs, and that a zones-only re-bake changes `zonesVersion` without
touching `mapVersion` or the schema number (`zone-baking.md` §3.2, §3.6, risk 3). A single global
schema number would never trigger a re-resolve when one map's volumes move. **Binding:**
`placeSource: "zones:<zonesVersion>"`, the same spelling the Round Index uses for
`IPlaceAdjacency.Source` (`round-index.md` §3.8); `pawn` and `null` stay. The Grenade Index (build item)
stores `zonesVersion` beside `LandingPlace` for the same reason; the Grenade Walk row itself keeps
`LandingPlace` null (`grenade-walk.md` §3.4). Zone Baking's bundle `schemaVersion: 2` is orthogonal and
stands (Zone Baking D1).

**Correction 14. `PlaceResolver` has one API and it is Zone Baking's.** The Tag Store expects
`PlaceResolver.Resolve(map, x, y, levelMinZ) → string?` (§5); Grenade Walk expects
`PlaceResolver.Resolve(map, x, y, z) -> string?` (§5.3); Zone Baking defines a per-map
`PlaceResolver(ZoneSet)` with `Resolve(Vector3) → PlaceHit`, `ResolveOnFloor(x, y, floorKey)`,
`BombsiteAt`, `Adjacent` (`zone-baking.md` §3.4), obtained through
`ZoneAssetPipeline.TryLoad(bundleDir)`. **Binding:** Zone Baking's signatures. A consumer with a world
Z (Grenade Index, Create Strat From Round) calls `Resolve(Vector3)`; a consumer with a floor key (Click
To Tag Position, Query Canvas) calls `ResolveOnFloor`; both read `PlaceHit.Name` and `PlaceHit.Kind`.
`LoadedMapAsset.Zones` is the lazy accessor inside playback; everyone else calls the pipeline directly.

**Correction 15. The canonical place list per map comes from `zones.json`, not `bundle.json`.** The Strat
Model says the bundle's place list supersedes the embedded resource "when Zone Baking's bundle carries a
`zones` block" (`strat-model.md` §3.7, §5); Zone Baking's `bundle.json` carries only
`{ file, zonesVersion, placeCount }`, and the names live in `zones.json` `places[]`, which the engine's
bundle DTO cannot expose (`zone-baking.md` §2.2, §3.2). **Binding:** `CalloutResolver` takes its
canonical list from `ZoneSet.Places` via `ZoneAssetPipeline.TryLoad` when present, else the embedded
`Resources/callouts/<map>.places.json` from Place Names From The Pawn; the Round Index's `places` table
is an observed vocabulary and is not a source of canonical names.

**Correction 16. Detonation-to-place resolution has one precedence.** Suggested Tags keeps a private
sparse sample cloud (1 in 25 placed samples) for no-zones detonation placement (`suggested-tags.md`
§3.2); the Round Index already stores per-place, per-Z-bucket centroids and `PlaceSnap.Nearest`
(`round-index.md` §3.9). **Binding:** behind Suggested Tags' one resolver call the order is
`PlaceResolver.Resolve` (zones) → `PlaceSnap.Nearest` over `ISituationIndex.Places(map)` (index) →
the private cloud, which exists only when the walk fallback is in use (browser, or a demo the index has
not reached). The 400-unit threshold Suggested Tags measured is passed as `maxDistance`.

### 3.5 Strat Model and Step Authoring

**Correction 17. The Strat Model adopts Step Authoring's four additive items.** Step Authoring lists
them itself (`step-authoring.md` §5, §6): opponent slots `O1..O5` in `positions[].slot` (the Strat
Model's slot rule refuses anything but `A..E` in `slots[]`, `strat-model.md` §3.10, but `positions[]`
is a different field and must admit ten values); `utility.landing { x, y, levelMinZ }` beside `place`;
the root `canvas { fadeInTicks: 8, fadeOutTicks: 16, showOpponents: true, defaultLevelMinZ: null }`
block; and `StratFrameSource` in `Playback2D.Pipeline/Frames` rather than beside `StratStore`
(Strat Model §3.9 names the type without a home). **Binding:** all four are defined fields of
`.dvstrat.json` schema 1, the validator admits them, the `schema-v1.sample.dvstrat.json` fixture
carries positions and strokes on three steps, and `StratFrameSource` is Pipeline's (Step Authoring
decision 6, recommended). The `interpolation` vocabulary is `linear | hold`; `path` is **reserved**:
the validator warns on it and treats it as `linear` (the Strat Model's "unknown places warn, never
refuse" spirit), rather than Step Authoring's "refused as unknown" (§3.11), so a document written by a
later build still opens.

**Correction 18. One undo stack, and the Strat Model owns it.** Both designs agree (`strat-model.md`
§3.12, `step-authoring.md` §3.8): `StratSession.Apply(PatchOp)` is the only user-visible history;
annotation gestures use `DocDelta` inside a gesture only; undo reaches the projections through
`AnnotationDocument.ApplyMigration` and `TokenTrackSet.Replace`. Recorded here because `StratSession`
must accept ops produced outside the metadata editor from day one (Step Authoring §5), which the
Strat Model's step 3 (`strat-model.md` §9) must build in, not retrofit.

### 3.6 Evaluators, background policy and keys

**Correction 19. Evaluator registration order is fixed and tested.** Round Facts is written by the
Library's tier-2 pass; the Round Index prefers the tier and falls back to the extractor; Suggested Tags
prefers the freshly written `.dvri.json` and falls back to a 2 s walk; `DemoEvaluationCoordinator.FanOutParsed`
iterates `_evaluators` in registration order (`DemoEvaluationCoordinator.cs:158-160`), and each design
says only "registered next to" the others. **Binding:** the list at `App.axaml.cs:704-725` is
`[library, highlights, roundindex, grenades, suggestedtags]` in that order, and a coordinator test pins
that each evaluator's `Evaluate` and `OnParsedOpportunistically` run after the previous one has written
its sidecar, so Suggested Tags reads the index in the same pass and the walk fallback is the exception,
not the rule.

**Correction 20. One background-indexing policy, decided once.** Grenade Walk defaults its opt-in
**off** like the Highlights scan (D4); the Round Index defaults **on** (decision 7, "the flagship needs
coverage"); Suggested Tags' feature gate is **on** and its `Wants` follows it (§3.6); Round Facts always
runs (it rides tier 2). Three settings with three defaults for one queue. **Binding (open decision
§4, O-4):** one `LibrarySettings.BackgroundEvaluators` policy with per-evaluator switches,
recommended defaults on for `roundindex` (1 to 3 s per demo, the flagship) and off for `grenades` and
`suggestedtags` until their consumers ship; the open demo is always evaluated opportunistically by all
three. Whatever is decided applies to all three rows in `AppSettings`.

**Correction 21. The `N` key is claimed twice.** Suggested Tags binds `N` to `SuggestionReject` and
Step Authoring binds `N` to `AddStep`, both `Playback2DBindingScope.Always` in the shared table
(`suggested-tags.md` §3.6, `step-authoring.md` §3.7); the keymap's static constructor throws on a
duplicate, so the second to land breaks the build. Neither key is bound today
(`Playback2DKeymap.cs:307-359` binds Space, arrows, `Q`, `E`, `F`, `Esc`, `D`, `X`, `Ctrl+Z`,
`Ctrl+Shift+Z`, `Ctrl+X`, `Home`; `Ctrl+N` is browser-reserved at `:392`). **Binding:** Suggested Tags
keeps `J/K/Y/N/Enter/Ctrl+Y` (a coherent accept/reject set); Step Authoring's `AddStep` moves to
`Shift+N`, and its `DuplicateStep` stays `Ctrl+D`. Scope precedence for the rest: a palette-focused
scope (the Tag Store's new `Playback2DBindingScope` value, `tag-store.md` §3.4) wins over
`WhenToolActive`, which wins over `Always`, so the default palette's `A`/`L`/`W` labels do not select
the Arrow and Line tools while the palette has focus, and the validator's warning names the shadowed
tool key.

**Correction 22. D7: `F` is taken; Find Rounds Like This takes `S`.** Both the Round Index and Suggested
Tags found `F` bound to `CycleFollowNext` (`Playback2DKeymap.cs:333`). `S` is unbound in the default,
shell and browser lists and reads as "situation". Recorded as open decision O-7; until it is
taken, no design promises a key for it.

### 3.7 Tag Store contracts other designs extend

**Correction 23. The Tag Store adopts the Strat Model's two asks.** Reserved human label groups
`strat`, `strat.rev`, `strat.result`, `strat.failure`, refused by the palette validator like Round
Facts' fact groups; and `TagIndexEntry.stratIds[]` (distinct `strat` values per document) beside
`codes[]` (`strat-model.md` §5). Both additive; the JSON golden gains them.

**Correction 24. The provenance vocabulary gains `matchmaking`.** Team Identity §3.10 shows the label
vocabulary has no value for a `me`-resolved, tagless demo and asks for `matchmaking` (decision 4); the
Strat Model already keys `ByProvenance` on `official | scrim | our scrim | matchmaking`
(`strat-model.md` §3.6). **Binding:** Demo Provenance Labels (build item, no design) ships the four
values, takes Team Identity §3.10 as its default table and Strat Model §5's
`IDemoProvenanceSource.LabelFor(sha256)` as its read API; its override store is user truth, so it lives
under the config root keyed by `sha256` (a `provenance` section inside `teams.json` is the smallest
home, since Team Identity already owns the default and both files are read at startup). Recorded as
decision O-11.

### 3.8 Shared-contract registry (one signature, one owner)

| Contract | Owner | Consumers | Shape |
|---|---|---|---|
| `DemoIdentity`, `ClockIdentity` (`dv-frame-clock`) | Playback2D annotations (exists) | every new store | reused as-is; fill rule per correction 6 |
| `DemoRef` | Team Identity (correction 3) | Round Index, Tag Store, Strat Model, Watched Situations | `(Path, StableKey, Sha256?)` |
| `DemoCacheStore.WriteSibling/TryReadSibling` | Grenade Walk (correction 1) | Round Index, Suggested Tags | suffix-keyed siblings under `demos/` |
| `RoundFactsTier`, `IRoundFactsSource` incl. `FactsFor` | Round Facts (correction 10) | Round Index, Tag Store, Strat Model, Suggested Tags, Search Filters | §3.3, §3.6 of `round-facts.md` plus the fact list above |
| `PlaceCountToken` | Round Index | Query Canvas, Find Rounds Like This, Suggested Tags | `Encode`/`Decode`, `TokenVersion = 1` |
| `RoundOccupancy` | Suggested Tags | detectors only | built from the index or the walk (correction 11) |
| `ZoneSet`, `PlaceResolver`, `ZoneAssetPipeline` | Zone Baking (correction 14) | Tag Store, Grenade Index, Round Index adjacency, Query Canvas, Strat Model callouts | `zone-baking.md` §3.4 |
| `IPlaceAdjacency`, `PlaceSnap` | Round Index | Tolerance Slider, Query Canvas, Suggested Tags (correction 16) | `round-index.md` §3.8, §3.9 |
| `TagDocument`, `TagQuery`, `TagSlice`, `TagInstanceRef` | Tag Store (corrections 2, 23) | Matrix, Watched Situations, Strat Model evidence, Suggested Tags accept | `tag-store.md` §3.2, §3.7 |
| `TeamIdentityService` incl. `SideAtRound` | Team Identity (correction 9) | Search Filters, Watched Situations, Matrix, Strat Model, Dossier | `team-identity.md` §3.7 |
| `StratDocument`, `StratSession.Apply(PatchOp)` | Strat Model (corrections 17, 18) | Step Authoring, Strat Export, Create Strat From Round | `strat-model.md` §3.3, §3.12 |
| `TokenTrackSet`, `StepSchedule`, `StratFrameSource` | Step Authoring (correction 7) | Strat Export, goldens | `step-authoring.md` §3.3, §3.6 |
| `GrenadeRow`, `IThrowerInputSource` | Grenade Walk | Grenade Index, Lineup Cards, Opening Tendencies | `grenade-walk.md` §3.4, §3.7 |
| Module ids | each design | `FeatureCatalog` | `net.demoviewer.teams`/`tab.teams`; `net.demoviewer.situations`/`situations.search`/`tab.situations`; `net.demoviewer.roundtagger`/`tagger.matrix`/`tab.tagger` + `playback2d.tagger`; `net.demoviewer.stratbook`/`stratbook.browser`/`tab.stratbook` + `stratbook.export` (desktop-only); `playback2d.suggestedtags`; layer `playback2d.zones`; tracks `tag`, `suggested`, `step` |

---

## 4. Consolidated decisions

De-duplicated across the nine `§8` lists (76 raw items). Where designs agree on a recommendation it is
stated as the default so it can be approved in bulk; the rows marked **choose** are the ones where
designs disagree or no recommendation exists. Plan references are to `plan.md` §6.

### 4.1 The plan's D1 to D7

| Plan | Decision | Where raised | Recommended default | Status |
|---|---|---|---|---|
| **D1** | Team Identity ship order | TI 1 | Approve in Phase 0; build service, both stores and the Library filter in Phase 1 after Result Cards And Walking, before Search Filters And Live Count; Teams tab one step later | approve |
| **D2** | Storage for derived stores | RI 1, TS §4.1, GW §3.9, TI §4, SM §4 | JSON sidecars plus the Round Index's per-session in-memory postings (measured: 1.5 s build, 11 µs lookup at 1,000 demos versus 1 to 2 s per scan and a 180 MB SQLite file with a native package under advisory); revisit when startup load passes 5 s (about 3,000 to 4,000 demos). One decision closes D2 for all five stores | approve |
| **D3** | Round Facts route | RF 1 | App-side walk in tier 2 (73 to 308 ms marginal, exact end tick and reason, frame clock); ruleset route kept as a parity oracle; engine API as a later proposal | approve |
| **D4** | Practice-server door | untouched by any design | still deferred past Phase 4 | no change |
| **D5** | Veto import | untouched | manual entry, Phase 5 | no change |
| **D6** | Suggested Tags route | ST 1 | Route (a) app-side detectors; route (b) filed as an upstream proposal with no engine work until a team asks for YAML detectors | approve |
| **D7** | Find Rounds Like This key | RI §2.4, ST §2.4, correction 22 | `F` is taken by follow; `S` (unbound) | **choose** |
| D8 | resolved 2026-09-23 (CS2DemoKit #53) | GW §3.7 keeps the seam | | closed |

### 4.2 New decisions, grouped

**Storage, identity and files**

| # | Decision | Raised by | Default |
|---|---|---|---|
| O-1 | Tag sidecars always under the config root keyed by hash (not the annotation store's beside-the-demo probe) | TS D1 | as recommended |
| O-2 | Hash helper home: `Playback2D.Pipeline` versus a new tiny assembly | TS D4, correction 4 | Pipeline |
| O-3 | Grenade trajectories in the sidecar at stride 4 (230 to 300 KB per demo) versus rows only plus a 2 s re-walk | GW D1 | stride 4 |
| O-4 | **Background evaluator policy** (correction 20): one setting shape; defaults on for `roundindex`, off for `grenades` and `suggestedtags` | GW D4, RI 7, ST §3.6 | **choose**; designs disagree |
| O-5 | Round Index sidecar shape: runs (40 percent smaller) and explicit `:1` | RI 3, RI 4 | runs, explicit |
| O-6 | Round Facts as its own stamped tier versus folded into Parse | RF 5 | own tier |
| O-7 | Strat delete semantics: `.trash/` versus hard delete | SM 10 | `.trash/` |
| O-8 | Strat history granularity: per commit (explicit save, deactivate, swap, shutdown, 30 s idle) with same-path merging | SM 2 | per commit |
| O-9 | Alias table per owner per map versus one per map | SM 5 | per owner |
| O-10 | Owner model: team GUID books plus one `me` book | SM 1 | as recommended |
| O-11 | Provenance vocabulary gains `matchmaking`; override store inside `teams.json` (correction 24) | TI 4, SM §3.6 | `matchmaking`; **choose** the store |
| O-12 | Zone file plain versus gzipped; ship `areaLinks` (doubles size); include buyzones and `bombRadius`; bump `bundle.json` to schemaVersion 2 | ZB D3, D6, D5, D1 | plain, ship, include, bump |
| O-13 | Resolver in `Playback2D.Core/Zones` (headless outlines) versus `src/App` | ZB D2 | Core |
| O-14 | `StratFrameSource` in Pipeline; defer moving the strat JSON reader into Pipeline for `dv2d strat` | SA 6, SA 7 | Pipeline; defer |
| O-15 | Walker in `Modules/UtilityBook` versus a separate Parser-only library | GW D2 | in the App |
| O-16 | Teams panel as its own Main tab versus a Library pane; module naming for Situations (`net.demoviewer.situations`, `situations.search`, `tab.situations`) | TI 5, RI 11 | own tab; names as listed |

**Thresholds, rules and vocabularies** (each a named constant with its measurement beside it)

| # | Decision | Raised by | Default |
|---|---|---|---|
| O-17 | Clustering rule `k = min(3, side)`, core cap 7, as a constant; auto teams visible from 2 assigned sides | TI 2, TI 6 | as recommended |
| O-18 | `me` account list in `teams.json`, seeded by the >50 percent share suggestion, written only on confirmation | TI 3 | as recommended |
| O-19 | Buy types: keep `Semi`; `Force` on money after a loss with the reliability guard; CT $4,500 versus T $4,000 per player (correction 10 spreads the five values to every consumer) | RF 2, 3, 4 | as recommended |
| O-20 | Fix `RoundTrack`'s winner tint from Round Facts in this item (it never fires on Valve demos today) | RF 6 | yes |
| O-21 | Round Index cadence 1 s; `Exact` means exactly N with wider levels at least N; ship the empirical adjacency at threshold 3; do not index the post-round window; alive from Round Facts `Kills` | RI 2, 8, 9, 10, 5 | as recommended |
| O-22 | Execute detector strict (`N=4, T=4 s`) versus loose (`N=3, T=6 s`); the tuning view moves it later | ST 2 | strict |
| O-23 | Grenade thresholds (jump window 27 ticks, movement 10 and 140 u/s, strength 0.95 / 0.35 to 0.65 / 0.05); `setpos` from the release tick | GW D5, D6 | as recommended |
| O-24 | Overlap tie rule first-in-lump with a per-map override table; per-map agreement floors at measured minus one point versus holding for mirage at 95 | ZB D4, D7 | first-in-lump; **choose** the floor policy |
| O-25 | Stationary rule for absent entries; midpoint level snap; text at `6 × WidthWorld`; Create Strat From Round cadence (utility + plant + 10 s sweep at 200 units); ten tokens with `O1..O5` | SA 1, 8, 9, 10, 2 | as recommended |
| O-26 | Strat type applicability warns (not refuses); failure vocabulary of seven values as the default set; `plant` clock reserved only in v1; LAN Print as self-contained HTML in the system browser | SM 6, 7, 9, 8 | as recommended |
| O-27 | Evidence linkage by reserved human labels (not a `TagInstance` field); won/lost from Round Facts `winner` first | SM 3, 4 | as recommended (correction 10 fixes the side source) |
| O-28 | Export facts under plain group names with the palette validator keeping human groups disjoint; `clampToRound: true` in the shipped palette | TS D2, D3 | as recommended |
| O-29 | Default Strat Export GIF, 20 fps, 640 wide, first step to last plus 2 s | SA 5 | as recommended |

**Keys and UI**

| # | Decision | Raised by | Default |
|---|---|---|---|
| O-30 | Suggested Tags claims `J/K/Y/N/Enter/Ctrl+Y` now; Step Authoring's rows (`V A T L R O`, `Shift+N`, `Ctrl+D`, `Ctrl+Delete`, `[ ]`) in the shared keymap; palette scope precedence (correction 21) | ST 5, SA 4 | as corrected |
| O-31 | Provenance on an accepted instance as a free-form object (not a parser-namespace label) | ST 6, correction 2 | object |
| O-32 | Run the hand-tag validation after the Tag Palette ships versus a throwaway tagging form earlier | ST 7 | after the palette (same phase) |
| O-33 | Obtain one genuine Sportscode or Nacsport XML export to verify `ROWS`/`SESSION_INFO`; otherwise ship the verified subset and say so | TS D5 | **action required** |

**Upstream filings** (one decision instead of five)

| # | Decision | Raised by | Default |
|---|---|---|---|
| O-34 | File the `CSmokeGrenadeProjectile` creation-packet decode bug now (drafted, `grenade-walk.md` §5.2); the design does not wait for it | GW D3 | file now |
| O-35 | Batch the optional proposals and doc corrections into themed CS2DemoKit issues after Phase 1: Round Facts A and B (`RoundFacts.Derive`, win-status provider + synthesized `$round_end`, the `round_won` note); Round Index A and B and Suggested Tags §5.3 (`PositionSample.IsAlive`/`Team`, the "live pawn" naming, `Tick` clock doc, empty-string `Place`); Grenade Walk §5.1 (`ProjectileSampler`, Parser tier per GW D7); Suggested Tags route (b) when a team asks | RF 7, RI 12, ST 8, GW D7 | after Phase 1, batched |

---

## 5. Merged sequencing recommendation

Respects `plan.md` §4's gates and its "research never more than one phase ahead" rule. Durations are
the designs' own; one person unless stated.

**Now (Phase 0, before Gate 0 → 1).**
1. **Approve** Round Facts, Team Identity and Zone Baking (the three Phase 0 designs). Gate 0 → 1 asks
   for the first two approved, not built.
2. **Build Content Identity** to `tag-store.md` §3.10 (correction 4), 1 to 2 days. Every later store
   assumes it.
3. **Build Round Facts** (about 5 days) inside Phase 0. It is small, blocks nothing on the engine, and
   the Round Index build, the Tag Store refresher and the Strat Model's `winner` all read its tier;
   building it here removes the extractor-call fallback from the Round Index's critical path.
4. **Place Names From The Pawn**: the Valve matchmaking half is already answered inside four designs
   (null rate 0 to 0.04 percent, 23 to 29 names per map: `round-index.md` §2.6, `suggested-tags.md`
   §2.1, `strat-model.md` §2.4, `zone-baking.md` §7.1). What remains is FACEIT, HLTV and POV, which
   needs one demo per source (`plan.md` §3, Inputs Per Demo Source). Record the Valve table now, mark
   the rest outstanding, and let Gate 0 → 1 pass on the Valve go with the other sources as a stated
   risk (every design already names its fallback).
5. **File** the smoke decode bug (O-34).

**Phase 1 (Situation Search).**
6. **The Round Index** (8.5 d) → Query Canvas → Find Rounds Like This (with the D7 key) → Result Cards
   And Walking.
7. **Team Identity build** (7.5 d) immediately after Result Cards, per D1 → Search Filters And Live
   Count → Watched Situations → Overlay View. Demo Provenance Labels (small) lands with it
   (correction 24).
8. **Zone Baking** (5.5 d) in parallel, by a second person if there is one; it must land before the
   Tolerance Slider's polish and before Click To Tag Position, but the Round Index's empirical graph
   means the slider ships in a degraded form without it.
9. **Suggested Tags steps 1 to 3** (the input layer, site regions, detectors; 5 d, no UI) can run as
   Phase 1 background work so validation data exists before the palette ships
   (`suggested-tags.md` §9). Gate 1 → 2: the thirty-second clip.

**Phase 2 (Round Tagger).**
10. **Tag Store** (12 d) → Tag Track → Tag Palette → Label Mode → Free Labels From Round Facts → The
    Matrix (Team Identity already built).
11. **Suggested Tags steps 4 to 8** after the Tag Store and Tag Track; the hand-tag validation (§7.3)
    after the Tag Palette (O-32). Gate 2 → 3: a demo tagged end to end; the Matrix opens clips.

**Phase 3 (Strat Book).**
12. **Shape Tools** (4 d) has no dependency and should be pulled forward into any idle slot in Phase 1
    or 2; it is also what Pack Export needs.
13. **Strat Model** (11 d) → **Step Authoring** (14 d after Shape Tools) → Strat Export → Create Strat
    From Round → Record Panel · Version History · Role View. Gate 3 → 4: a strat created from a round
    exports as a GIF and reports a record.

**Phase 4 (Utility Book and Review Packs).**
14. **Grenade Walk** (4.5 d) has only a soft edge into Content Identity. Recommendation: build the
    walker and evaluator during Phase 2 or 3 idle time and let the library index in the background
    (30 minutes per 700 demos, `grenade-walk.md` §3.8), so that when Phase 4 opens the Grenade Index
    has rows to cluster and Opening Tendencies has data. The plan's phase assignment stands; only the
    walker is pulled forward.
15. Grenade Index → Lineup Cards → Lineup Clip Render · Lineup On A Strat Step; Review Queue → Pack
    Export → Headless Packs. Gate 4 → 5 as in the plan.

**Phase 5** unchanged.

Critical path: Content Identity → Round Facts → The Round Index → (Query Canvas, Result Cards) → Team
Identity → Watched Situations, then Tag Store → Tag Palette → Strat Model → Step Authoring. About
55 working days of reviewed items sit on it; Zone Baking, Grenade Walk, Shape Tools and Suggested Tags
steps 1 to 3 (about 20 days) are off it.

---

## 6. Completeness check

### 6.1 Plan work items marked review-required, against the designs

| Work item (plan §3) | Review | Design | State |
|---|---|---|---|
| Team Identity | yes | `team-identity.md` | complete; §3.7 needs `SideAtRound` (correction 9) |
| Round Facts | if engine | `round-facts.md` | complete; no engine change chosen, so review is for the record and the tier schema |
| Delta User Commands | yes (upstream) | none | resolved as CS2DemoKit #53 (D8); no design needed |
| Zone Baking | yes | `zone-baking.md` | complete |
| The Round Index | yes | `round-index.md` | complete |
| Tag Store | yes | `tag-store.md` | complete; corrections 2, 10, 13, 23 change §3.2, §3.3, §3.6 |
| Suggested Tags | if engine | `suggested-tags.md` | complete for route (a); route (b) is a proposal, not a design, and stays that way per D6 |
| Strat Model | yes | `strat-model.md` | complete; correction 17 adds four fields |
| Step Authoring | yes | `step-authoring.md` | complete, including the Shape Tools contract |
| Grenade Walk | if upstreamed | `grenade-walk.md` | complete; the upstream half is a proposal (O-35) |
| Walk It In The Server | yes, blocked on D4 | **none** | deferred by the plan; no design expected until D4 opens |
| Push Lineup To Server | yes, blocked on D4 | **none** | same |

Every review-required item that is not deferred has a design. Two build items with no review
requirement are nonetheless specified only by other designs and should be read as having one:
**Content Identity** (`tag-store.md` §3.10 plus correction 4) and **Demo Provenance Labels**
(`team-identity.md` §3.10, `strat-model.md` §5, correction 24). Two research items are still open and
gate Phase 1 only softly: **Place Names From The Pawn** (Valve answered inside the designs; other
sources not) and **Inputs Per Demo Source** (Valve 2025 and 2026 measured in `plan.md` F6 and
`grenade-walk.md` §2.4; FACEIT, HLTV, POV outstanding).

### 6.2 Thin sections a reviewer should press on

| Design | Section | What is thin |
|---|---|---|
| Round Facts | §3.6 exposure | `IRoundFactsSource` lacks the `FactsFor(demoPath, round)` label projection the Tag Store asks for and the `winner`/`roundTime`/`plantTick` groups the Strat Model asks for; correction 10 supplies the list, but the design should own it. Overtime (`m_gamePhase`), HLTV, FACEIT and POV are designed but unmeasured (R1, R3). |
| Team Identity | §3.7, §3.9 | `DemoRef` is undefined (correction 3); `SideAtRound` is pointed at the other design (correction 9); merge/split UI has no mock, and the Teams tab is the largest UI in the set (2.5 d) with the least description. The `.info` sibling date is noted and deferred, so `orderTicks` stays download time. |
| Zone Baking | §3.5, §7.2 | The outline layer's label placement and hover are one paragraph; the baker has no test project (tests are "a new test project, or `--diag` self-check"), and the tool is not in the solution, so nothing in CI runs it. The per-map override table for D4 has no format. |
| The Round Index | §3.7, consumers | `SituationHit` carries ticks only; Overlay View and Result Cards thumbnails must re-open each hit demo and seek (a tracker replay from frame 0, 1 to 3 s per demo), so "forty results walk in under two minutes" and a stacked overlay over forty demos are unbudgeted. The design excludes per-player rows on purpose (§4); the consumer items need a cached per-hit position snapshot or a render budget. `FanOutParsed` on the browser head is unverified (R9). |
| Tag Store | §3.3, §3.6 | The `suggestion` block and rejected-proposal retention are superseded (correction 2); `placeSource` stamp corrected (13); the XML `ROWS`/`SESSION_INFO` blocks are unverified (O-33). Multi-instance selection in Label Mode is not described. |
| Grenade Walk | §3.9, §6 | No `zonesVersion` beside `LandingPlace` (correction 13, pushed to the Grenade Index); HLTV and FACEIT entirely unmeasured, including whether `weapon_fire` and `EntityId` semantics hold; the `m_fFlags` duck bit is an open unknown. |
| Suggested Tags | §3.4, §3.7 | Verdict location (correction 2); occupancy bounds and alive source (correction 11); the tuning view's in-memory re-run cost over fifty verdict demos is "expected under 10 s, unmeasured"; the plant-based precision numbers are bounds until §7.3 runs, which is gated on the Tag Palette. |
| Strat Model | §3.7, §3.13 | The canonical list source is corrected (15); import collision handling is two sentences; `Lineup.Id` is an opaque GUID with nothing to validate against until Phase 4; the `plant` clock is reserved with no sketch of the second mapping. |
| Step Authoring | §3.10, §6 | The `ISceneFrameHost` seam over `Scene2DHost` is the riskiest App change in the set (the host binds a concrete VM at `Scene2DHost.cs:90`) and gets one paragraph; text legibility at 640 px is flagged high-likelihood with a preview as the only mitigation; the perceptual tolerance for text-heavy goldens is unknown until the first strat golden exists. |

### 6.3 Shared risks no single design owns

- **Non-Valve sources.** Every measurement in the set is on Valve matchmaking demos (builds 10231 to
  10896) plus 8 HLTV records for Team Identity. Place coverage, `player_team` cadence, `weapon_fire`,
  `round_end` presence, 128-tick cadence and `svc_UserCmds` on FACEIT, HLTV and POV are assumptions with
  named fallbacks. Inputs Per Demo Source and Place Names From The Pawn are the two items that close
  this, and both need one demo per source that does not exist locally (`plan.md` F6c).
- **Round Facts is code nobody has written** and four designs build on its tier shape (`Slots`, `Kills`,
  `EndTick`, `winner`). Its fingerprint (`RoundFactsSchema`) is carried by the Round Index, and the
  Tag Store's `factsStamp.schema` by the refresher, so a change rebuilds rather than corrupts; the cost
  is a library re-index, which is why it should be built first (§5 step 3).
- **Four additive field groups on `DemoCacheRecord` and `DemoCacheIndexEntry`** (Round Facts tier stamp,
  Round Index stamp and state, grenade stamp and counts, `SuggestionCount`) land in four PRs. Each must
  extend `ToIndexEntry` and the `DemoCacheTier2Tests`-shaped mirror test, and old sidecars must
  deserialize to "never written" in every case.
- **Browser host.** Every design adds a `wasm-matrix.md` Degraded row and a "session only" status line;
  none is verified on the published head. One checklist pass at the end of each phase covers all of them.
