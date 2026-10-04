# Grenade Walk: design

**Work item:** Grenade Walk (Phase 4, Utility Book) · **Kind:** research then design, review required if
upstreamed · **Depends on:** Inputs Per Demo Source; better with Delta User Commands (CS2DemoKit #53) ·
**Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0, CS2OpenDev.Protos 0.9.0 · **Date:** 2026-09-23 ·
**Status:** approved 2026-09-24. Nothing here is implemented.

> **Status: APPROVED 2026-09-24**, as recommended: D1 trajectories at stride 4; D2 walker
> in `Modules/UtilityBook/` in the App; D3 filed as CS2DemoKit #56; D4 background indexing off by
> default with the open demo walked opportunistically (the integrator's O-4 one-setting-shape ask still
> applies across evaluators; the defaults differ by design); D5 thresholds as named; D6 `setpos` from
> the release tick; D7 the Parser-level `ProjectileSampler` proposal, batched after Phase 1 (O-35).

This is the walk that turns every grenade in a demo into one row: who threw it, from where, looking
where, how (standing, running, jump-throw), the path it flew, where it landed or detonated, and the
`setpos`/`setang` pair that reproduces the throw. It is the data source for the Grenade Index, Lineup
Cards, Lineup Clip Render and Opening Tendencies. It reads only what CS2DemoKit 0.12.0 already decodes;
no engine change is required to ship it. Every number below was measured by a scratch probe over real
Valve matchmaking demos from the Steam replays folder (§2.4), never the tour sample.

---

## 1. Problem and scope

Finding F6 in `plan.md` says it: the analysis layer indexes smoke and molotov projectile slots for the
visibility solver only, `grenade_thrown` is bound in the HLTV profile and not the GOTV one, and there is
no "every grenade with origin, release angle, landing point" walk anywhere. The 2D playback draws
grenade trails live (§2.2), but that is a forward-play artifact keyed to the open demo; nothing produces
a persisted, per-grenade record across a library.

**In scope.** The facts (which entity classes, fields and events carry each part of a grenade's story,
per demo source, measured); the walk API and its internals; the per-grenade row; jump-throw and movement
classification, with the method recorded on the row; the `setpos`/`setang` string; the evaluator that
runs the walk under the processing queue; the sidecar it writes beside `DemoCacheStore`; the browser
behaviour; the shape of an upstream proposal to CS2DemoKit; and a bug report the probe turned up.

**Out of scope.** Clustering rows by landing point and the coarse spatial grid (Grenade Index),
the card layout (Lineup Cards), the clip render (Lineup Clip Render), landing place names (Zone Baking
supplies `PlaceResolver`; the row reserves the field), and the practice-server push (deferred, D4).
Decoding `CMsgServerUserCmd.delta_data` is CS2DemoKit #53 and is not designed here.

---

## 2. What exists today (cited)

### 2.1 The engine surface the walk stands on

`PositionSampler.Walk(demo, frameStride, maxFrames)` (CS2DemoKit `origin/main`
`src/CS2DemoKit.Parser/EntityTracking/PositionSampler.cs:66-73`, packaged in 0.12.0) is the model.
It is a lazy `IEnumerable<PositionSample>`: `Iterate` creates `EntityTrackerFactory.CreateCurated()`,
calls `tracker.AdvanceOneFrame(frame)` for every frame from 0, and on every `frameStride`-th frame
sweeps `PawnLookup.ForEachLivePawn` into a buffer and yields it (`:75-120`). Its doc comment fixes two
rules this design inherits: the stride subsamples the output only, since every frame must be decoded,
and there is deliberately no way to start late, because entity state is delta-encoded.

The pieces it assembles are all public in 0.12.0 and reusable here:

| Piece | Where | What it gives the walk |
|---|---|---|
| `PositionUtil.CellToWorld(EntityState)` | `PositionUtil.cs`; XML doc `CS2DemoKit.Parser.xml:4119` | world position from `CBodyComponent.m_cell{X,Y,Z}` + `m_vec{X,Y,Z}`, `(cell - 32) * 512 + offset`; null when any of the six leaves is unseen |
| `PawnLookup.ForEachLivePawn`, `ResolveHandle(tracker, uint)`, `TryReadHandle`, `IndexOf` | `PawnLookup.cs:42`, `:97`, `:152`, `:30` | slot per live pawn, handle to entity, handle read without boxing, handle to index with the invalid-handle sentinel |
| `EntityTracker.EntityCreated` (`Action<int, EntityState>`) | XML doc `:3002` | fires once per `FHDR_ENTERPVS`; the probe saw no duplicate firings at the 27 to 35 `DemFullPacket` frames per demo |
| `EntityState.TryGetValue(path, out object?)` | XML doc `:2664` | seen-gated read without building the `Fields` dictionary (the doc measures the alternative at ~62 MiB per demo for one handle per projectile per frame) |
| `EntityTracker.StoreClassFilter` | XML doc `:2884`; used at `DemoLibraryService.cs:54-58`, `:1418-1421` | decode-and-discard every class not in the set; the Library's final-score replay is the precedent |
| `GameEvent` (`Name`, `FrameNumber` = frame index, `ServerTick` absolute, `GameTick` frame clock, `Payload`) | XML doc `:4222-4300` | `demo.AllGameEvents` is the pre-built flat index in tick order |

Grenades already exist in the engine, but only for the visibility solver and one synthesized rules
event. `ProjectileSlotIndex` (`src/CS2DemoKit.Analysis/ProjectileSlotIndex.cs:38-77`) is `internal`,
subscribes to `EntityCreated`, seeds from `AllIndexed()` on first bind, prunes lazily once per frame,
and admits exactly two classes: `VisibilityAnalyzer.SmokeClass` (`"CSmokeGrenadeProjectile"`,
`VisibilityAnalyzer.cs:53`) and `MolotovClass` (`"CMolotovProjectile"`, `:41`). The pattern is the
right one and the walk copies it (§3.3); the class cannot be reused because it is internal and admits
the wrong set. `EntityDigestExtractor.ResolveThrowerSlot` (`EntityDigestExtractor.cs:189-207`) is the
validated chain `m_hThrower -> pawn -> m_hController -> slot` (`slot = controller index - 1`, through
`IndexOf` so a dead pawn's invalid handle yields -1); also internal, also copied. The engine's own note
at `EntityChangeScanner.cs:1037-1042` says `m_hThrower` "is not reliably networked on the frame the
projectile is first seen"; §2.4 measures exactly when it is not, and it is a narrower story than that.
`MolotovThrownEvent` (`Events/MolotovThrownEvent.cs`) is synthesized per `CMolotovProjectile` creation
for YAML rules only, with all three clocks carrying the frame tick.

Source profiles (`Abstractions/DemoSourceProfile.cs`): `GrenadeThrown` is `null` on the base
(`:119`) and bound to `grenade_thrown` only in `Cs2HltvProfile` (`Profiles/Cs2HltvProfile.cs:51-52`;
its doc says "emitted on HLTV but not GOTV", `:33`). `Cs2GotvProfile` binds `SmokeDetonate` to
`smokegrenade_detonate` (`:257`), `InfernoStart` to `inferno_startburn` (`:123`), and `HeGrenadeDetonate`,
`FlashbangDetonate`, `DecoyDetonate` to their events (`CS2DemoKit.Analysis.xml:13783-13900`).
`molotov_detonate` is never emitted (`MolotovThrownEvent.cs:11-13`), which §2.4 confirms on all three
demos.

### 2.2 The app surface

**The 2D playback already reads the five projectile classes.** `SceneFrameBuilder` names them
(`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/SceneFrameBuilder.cs:58-60`:
`CHEGrenadeProjectile`, `CFlashbangProjectile`, `CSmokeGrenadeProjectile`, `CMolotovProjectile`,
`CDecoyProjectile`), accumulates a `GrenadeTrail` per projectile `Serial` from `CBodyComponent` cell
reads (`:720-790`), maps class to `GrenadeKind` (`:803-810`; the enum is
`Playback2D.Core/GrenadeTrail.cs:4-12`), and reads billowing smokes through `m_nSmokeEffectTickBegin`
and `m_vSmokeDetonationPos`, and inferno cells through `CInferno.m_firePositions[i]` /
`m_bFireIsBurning[i]` (`:686-712`). That is a per-frame render read over the host's `IReadOnlyEntityView`,
not a walk, and it is deliberately incomplete on seek (`GrenadeTrail.cs` class doc). Its class list and
field names are reused verbatim.

**The Library's replay is the app-side precedent for a filtered walk.** `DemoLibraryService.ExtractFinalScore`
(`Modules/Library/DemoLibraryService.cs:1400-1470`) builds `new EntityTracker { StoreClassFilter = _scoreClasses }`
and `ReplayToIndex(frames.Count - 1, frames)` to read `CCSTeam` alone; the comment records ~1.2x faster
and 30 to 60% less allocation than storing everything, byte-identical, and that the replay starts at
frame 0 because deltas cannot be skipped.

**One parse, many evaluators.** `IDemoEvaluator` (`Services/DemoProcessing/IDemoEvaluator.cs:22-73`)
is the contract: `Id`, `Wants(path)` (cheap, no parse), `Evaluate(path, parsed)` (runs inside the
queue's gate slot with the `ParsedDemo` held, synchronous, failures isolated), `OnFailed`,
`PriorityFor`, `OrderHint`, and `OnParsedOpportunistically(path, parsed)` (`:60-73`: a free parse from
an interactive open or another evaluator's tier-2 pass, not gated on `Wants`). `DemoEvaluationCoordinator`
(`DemoEvaluationCoordinator.cs:9-29`) polls every registered evaluator and coalesces their submissions
onto one queue entry per path. The registered list is built at the composition root
(`App.axaml.cs:704-720`: `new DemoEvaluationCoordinator([library, highlights], ...)`).
`HighlightScanService` (`Modules/Highlights/HighlightScanService.cs:44`) is the model evaluator: a
derived backlog from the cache index, an opt-in for background work
(`HighlightsSettings.BackgroundScan`, `Configuration/AppSettings.cs:204-211`, default off), forced
per-demo requests at `DemoJobPriority.UserRequested`, `Evaluate` at `:193`, the opportunistic hook at
`:113`, and tier stamping through `DemoCacheStore.UpdateExisting` + `StampAnalysis` (`:568-578`).
`HeavyJobGate` (`Services/HeavyJobGate.cs:3-33`) is the one-heavy-parse-at-a-time invariant the queue
enforces; an evaluator never touches it directly.

**Persistence.** `DemoCacheStore` (`Services/DemoCache/DemoCacheStore.cs`) is index plus one lazily-read
sidecar per demo (`:15-16`), rebuildable and never a source of truth (`:24-26`), in-memory on the
browser host (`:29-31`, `_memoryRecords` at `:59`). Sidecars live at `demos/<StableKey(path)>.json`
(`SidecarPathFor` `:500-504`; `StableKey` is the first 12 bytes of SHA-256 of the lower-cased path,
`:507-511`), written atomically (`WriteAtomic` `:559-573`). `LoadOrCreate` discards every tier on
identity drift (size + mtime, `:263-280`). `DemoCacheRecord` carries per-tier `TierStamp`s
(`DemoCacheModels.cs:55`), `Sha256` (`:216`, null until something computes it), `Rounds` in the frame
clock (`:234`, `CachedRound` at `:84`), and projects `HighlightCount` onto the index row (`:419`) so the
cross-demo picker can decide which sidecars to open without opening them (`LoadRecords`, `:301-320`).
The tiers are cumulative and independently stamped (`DemoCacheTier`, `:16-33`).

**Identity and clock headers.** `DemoIdentity(Sha256, FileName, SizeBytes)` and
`ClockIdentity(Kind, TickRate, FrameCount, FirstTick, LastTick)` with `DvFrameClock = "dv-frame-clock"`
(`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationIdentity.cs:17`, `:33-43`)
are the headers `docs/playback2d-v2/annotations-format.md:22-36` specifies; the Tag Store design reuses
them as-is (`tag-store.md` §3.10 item 5) and this design does the same.

**Inputs.** `SubTickExtractor` (`src/CS2DemoKit.Parser/Models/SubTickExtractor.cs:22-60`) parses
`CSGOUserCmdPB.Parser.ParseFrom(serverCmd.Data)` and nothing else; `AimCapabilityProbe`
(`ViewModels/Stats/AimCapabilityProbe.cs:36`) already reports input availability as a density with named
thresholds. The button bits the extractor names (`:10-19`) are `IN_ATTACK = 1 << 0`, `IN_JUMP = 1 << 1`,
`IN_DUCK = 1 << 2`. `CMsgServerUserCmd` in Protos 0.9.0 carries `PlayerSlot`, `ServerTickExecuted`,
`ClientTick`, `Data` and `DeltaData` (reflected in §2.4).

**Browser.** `docs/playback2d-v2/wasm-matrix.md` records the processing queue as absent by design and
the demo cache as degraded (writes do not outlive the tab, nothing in the UI says so).

### 2.3 What the schema and the event list say (CS2OpenDev-Docs, build 25218825, 2026-09-09)

`cs2_schema.json` lists exactly five server projectile classes, all deriving from
`CBaseCSGrenadeProjectile : CBaseGrenade : CBaseAnimGraph`: `CSmokeGrenadeProjectile`,
`CMolotovProjectile` (with `m_bIsIncGrenade`: incendiary and molotov share the class),
`CHEGrenadeProjectile` (no fields of its own), `CFlashbangProjectile`, `CDecoyProjectile`. Burning
fire is a separate `CInferno : CBaseModelEntity`. The fields the walk reads:

| Field | Declared on | Meaning |
|---|---|---|
| `m_hThrower : CHandle<CCSPlayerPawn>`, `m_hOriginalThrower` | `CBaseGrenade` | thrower pawn |
| `m_vInitialPosition : VectorWS`, `m_vInitialVelocity : Vector` | `CBaseCSGrenadeProjectile` | spawn point and launch velocity |
| `m_nBounces : int32` | `CBaseCSGrenadeProjectile` | bounce counter |
| `m_nExplodeEffectTickBegin : int32`, `m_vecExplodeEffectOrigin : VectorWS` | `CBaseCSGrenadeProjectile` | HE detonation tick (server clock); the origin decodes as garbage, see §2.4 |
| `m_nSmokeEffectTickBegin : int32`, `m_vSmokeDetonationPos : VectorWS`, `m_bDidSmokeEffect` | `CSmokeGrenadeProjectile` | smoke pop tick and point |
| `m_bIsIncGrenade`, `m_bDetonated` | `CMolotovProjectile` | incendiary flag; `m_bDetonated` is never observed true (the entity is removed the same tick) |
| `CBodyComponent.m_cell{X,Y,Z}`, `m_vec{X,Y,Z}` | body component | position, via `CellToWorld` |
| `m_angEyeAngles : QAngle`, `m_fFlags : uint32`, `m_hGroundEntity`, `m_bIsWalking`, `m_pMovementServices.m_bDucked`, `m_iTeamNum`, `m_pWeaponServices.m_hActiveWeapon` | `CCSPlayerPawn` and bases | thrower state |
| `m_flThrowStrength : float32` | `CBaseCSGrenade` (the held weapon) | 1.0 left click, ~0.5 both, 0.0 right click |

The schema dump carries no `MNetwork*` metadata, so whether a field reaches a demo is decided by the
probe, not the dump. `m_vecVelocity` is declared on `CBaseEntity` but is never networked on the pawn
(the probe reads null on every throw), so thrower speed is derived from positions.

`gameevents.json`: `grenade_thrown(userid, weapon)`; `weapon_fire(userid, weapon, silenced)` whose
`weapon` is the lowercase class name; `smokegrenade_detonate`, `flashbang_detonate`,
`hegrenade_detonate`, `decoy_detonate`, `decoy_started`, `smokegrenade_expired` all carry
`(userid, entityid, x, y, z)`; `inferno_startburn` / `inferno_expire` carry `(entityid, x, y, z)` and no
thrower; `molotov_detonate(userid, x, y, z)` is declared and never fired; `grenade_bounce(userid)` is
declared and never fired on the three demos.

### 2.4 Measured (scratch project `GrenadeWalkProbe`, 2026-09-23)

Probe: `CS2DemoKit.Parser` 0.12.0 + `CS2OpenDev.Protos` 0.9.0, Release, this Windows 11 dev machine,
one run per figure unless a range is given. It parses the demo, subscribes `EntityCreated` on a curated
tracker, tracks every projectile by `(index, serial)` per frame, keeps a 64-frame ring of every live
pawn's tick, flags, position, eye angles, ducked flag and active-weapon throw strength, joins
`weapon_fire` and the detonation events, and decodes full-payload user commands for the thrower in a
48-tick window. Three untrimmed `GotvMatchmaking` demos from the Steam replays folder:

| Demo | Map | Build | Frames | Grenades (smoke / molotov+inc / HE / flash / decoy) | `CInferno` |
|---|---|---|---|---|---|
| `match730_003731893271710924851_1024675027_129.dem` (Jan 2025) | de_nuke | 10231 | 154,869 | 229 (57 / 62 / 54 / 54 / 2) | 61 |
| `match730_003844252717140672725_0377894676_389.dem` (Sep 2026) | de_dust2 | 10896 | 106,901 | 318 (80 / 52 / 67 / 102 / 17) | 51 |
| `match730_003844212550606520904_0896405452_408.dem` (Sep 2026) | de_ancient | 10896 | 100,962 | 244 (91 / 38 / 54 / 61 / 0) | 37 |

**Classes.** Only the five classes in §2.3 appear; `EntityCreated` fired once per grenade, never again
at a `DemFullPacket` frame (0 duplicates), `DecodeErrorRaised` never fired, `DeltaUnknownCount` stayed 0.
Projectile counts equal the `weapon_fire` counts per grenade weapon on all three demos (the one
exception is a smoke already billowing at frame 15 of the 2025 demo, before any `weapon_fire`).

**Thrower.** `m_hThrower` resolves to a slot on the creation frame for 100% of HE, flash, molotov and
decoy projectiles on all three demos, and never later than the creation frame. Smokes: 41/57, 67/80,
80/91 resolve on the creation frame; the rest read `0` (build 10231) or `12` (build 10896) for the
entity's whole life. See the defect note at the end of this section. `hegrenade_detonate`,
`flashbang_detonate`, `smokegrenade_detonate`, `decoy_*` events carry `UserId` equal to the slot
(`UserIdPawn` is the pawn handle), and `EntityId` equal to the projectile's entity index.

**Release.** `grenade_thrown` is absent on GOTV (0 on all three). `weapon_fire` with
`weapon_smokegrenade`, `weapon_flashbang`, `weapon_hegrenade`, `weapon_molotov`, `weapon_incgrenade`,
`weapon_decoy` is present, and the projectile is created 7 ticks after it (min 6, median 7, max 15) on
620 of 622 attributable throws. On the 2025 demo `weapon_fire.GameTick` is the attack-release command's
tick minus one (median; range -12 to 0). So the release tick is `weapon_fire`, the spawn tick is
`weapon_fire + 7`, and inputs are not needed for either.

**Spawn.** `m_vInitialPosition` and `m_vInitialVelocity` are present on the creation frame for every
projectile (the billowing pre-existing smoke excepted). `|m_vInitialPosition - thrower origin at spawn|`
is 66 to 69 units (median, all classes and demos), which is the eye height plus the forward offset, and
`|m_vInitialPosition - first cell read|` is 13 to 15 units for the four non-smoke classes. Between the
release tick and the spawn tick the thrower moves a median 20 to 27 units (max 72; 7 ticks at the
245 u/s grenade run speed is 27).

**Angles.** Yaw of `m_vInitialVelocity` against the pawn's `m_angEyeAngles.Y` at the release tick,
grounded throwers only: median 0.1 to 0.4 degrees, p90 1.0 to 4.9 (the wider p90 is flashes thrown
while moving). Against the command's `viewangles` on the 2025 demo: median 0.1 to 0.5. Pawn eye angles
at the release tick are therefore the `setang` pair; inputs add nothing.

**Detonation and end of life** (event joins, all three demos):

| Kind | Event | Tick relation | Point relation | Entity after |
|---|---|---|---|---|
| HE | `hegrenade_detonate` | `GameTick == m_nExplodeEffectTickBegin - ServerStartTick` on 174/174 | `xyz == last cell sample`, 0.0 units | lingers 319 ticks |
| Flash | `flashbang_detonate` | last sample tick + 1 on 217/217 | `xyz == last cell sample`, 0.0 | removed |
| Smoke | `smokegrenade_detonate` | `GameTick == m_nSmokeEffectTickBegin - ServerStartTick` on 188/188 | `xyz == m_vSmokeDetonationPos`, 0.0 | lingers ~1411 ticks; `smokegrenade_expired` ends it |
| Molotov, incendiary | `inferno_startburn` (no `userid`) | last sample tick + 1; nearest event within 4 ticks | median 4 to 7.5 units from the last sample, max 89 | removed; `CInferno` created with `EntityId` |
| Decoy | `decoy_started` | fires when the decoy comes to rest, 170 to 450 ticks after spawn | `xyz == last cell sample`, 0.0 | lives 620 to 1262 ticks more |

148 of 152 molotovs matched an `inferno_startburn`; the unmatched four are the design's "no
detonation" case (`EndKind = Removed`). Air time (spawn to the first frame the position stops changing):
HE 104 ticks (the fuse), flash 95 to 103, smoke median 171 to 238 (p90 351 to 450), decoy 185 to 447,
molotov 79 to 89 (removed on impact). Bounces: smoke median 5 to 6, decoy 6 to 7, HE 2, flash 0 to 2,
molotov 0 to 1.

**Jump-throw.** 2025 demo (`data` populated on 1,324,574 of 1,324,574 commands): a jump press
(sub-tick `button == 2, pressed`, or bit 1 rising) within 20 ticks before or 2 ticks after the attack
release found 20 jump-throws of 229 (10 flash, 8 smoke, 1 molotov, 1 decoy). The thrower's
`m_fFlags & 1` (`FL_ONGROUND`) at the **spawn** frame agreed on 20/20 jump-throws and 181/182 standing
throws (one flash airborne at spawn with no jump press in the window). The same flag at the **release**
tick disagreed on 6 of 20: the jump press landed 1 to 6 ticks after the attack release (a
`-attack; +jump` bind ordering), so the pawn was still grounded when `weapon_fire` fired and airborne
when the projectile spawned. The fallback must sample at spawn. 2026 demos (`delta_data` on 99.86% and
99.81% of commands): no window decoded, as F6 predicts; airborne at spawn: flash 35/102 and 33/61, smoke
14/67 and 31/80, HE 2/67 and 1/54, molotov 0, decoy 0.

**Movement and strength.** Horizontal speed over the 8 ticks before release: under 10 u/s on 18 to
29% of throws, 10 to 140 on ~20%, 140 or more on 45 to 55% (max 245, the grenade run speed).
`m_bIsWalking` was true on 0 to 5 throws per class; `m_bDucked` at release on 0 to 7. The held weapon's
`m_flThrowStrength` at the release tick read 1.00 on 87 to 100% per class, 0.00 (right click) on 2 to
7, and a few mid values (0.44 to 0.80).

**Cost** (Release; the bench drifts 27 to 31% between runs, so read these as "about"):

| Demo | Parse | Full walk, projectiles only | Full walk with pawn ring and weapon read | `PositionSampler.Walk` (baseline) | Walk with `StoreClassFilter` | Input scan (full-payload commands) |
|---|---|---|---|---|---|---|
| 2025 nuke, 154,869 frames | 0.6 s | 1.40 to 1.42 s, 108 MB alloc | 1.95 to 2.46 s, 212 to 248 MB | 2.05 to 2.34 s, 104 MB | 0.96 to 1.50 s, 97 MB | 1.23 s (1.32M commands) |
| 2026 dust2, 106,901 frames | 0.5 s | 1.49 s, 41 MB | 2.05 to 2.13 s, 118 to 149 MB | 1.83 s, 31 MB | 1.45 s, 36 MB | 0.60 s |
| 2026 ancient, 100,962 frames | 0.4 s | | 1.98 s, 114 MB | | | 0.71 s |

The walk costs about the same as `PositionSampler.Walk` on the same demo: the decode floor dominates
and the per-frame projectile work is a handful of slot reads. Polyline size at 30 bytes per `[t,x,y,z]`:
stride 1 = 904 KB / 1,198 KB per demo (30,876 / 40,900 moved samples), stride 4 = 228 / 301 KB, stride
8 = 115 / 151 KB.

**Defect: `CSmokeGrenadeProjectile` decodes wrong fields on its creation packet.** On every smoke, in
both the curated and the plain `new EntityTracker()`, the creation frame reads `CBodyComponent.m_cellY`
and `m_cellZ` as 0 while `m_cellX` and the three offsets are right, `m_nBounces` as -46, `m_nEntityId`
as a large negative, `m_iTeamNum` as 0; HE, flash, molotov and decoy projectiles in the same packets
decode correctly, and `m_vInitialPosition`, `m_vInitialVelocity` and `m_vSmokeDetonationPos` decode
correctly on the smoke itself. Later deltas fix the offsets, but a cell that never changes again is
never re-sent, so 10/57 (2025) and 74/80, 74/91 (2026) smokes have a first cell sample at
`(-16384, ...)`, and a smoke at rest can keep a garbage axis for its whole life. On 16/57, 13/80 and
11/91 smokes `m_hThrower` is also wrong (0 or 12) for the entity's life. No decode error is raised.
This is the same shape as the misalignment `tools/EntityDecodeProbe` was written to hunt, narrowed to one
class. §3.3 designs around it; §5.2 drafts the upstream report.

---

## 3. Proposed design

### 3.1 Placement

Two files under `src/App/DemoViewer.NET/Modules/UtilityBook/` (the folder the Utility Book's later
build items share, following `Modules/Highlights/`, F14), plus one settings class:

| File | Contents | Dependencies |
|---|---|---|
| `GrenadeWalker.cs` | `GrenadeWalker.Walk`, `GrenadeRow`, `GrenadeKind`, the pure helpers (§3.2 to §3.6) | `CS2DemoKit.Parser`, `CS2OpenDev.Protos` (input seam), `System.Numerics` |
| `GrenadeIndexEvaluator.cs` | the `IDemoEvaluator` and the sidecar writer (§3.8, §3.9) | the above, `DemoCacheStore`, `AppSettings` |
| `Configuration/AppSettings.cs` | `GrenadesSettings { bool BackgroundIndex; int TrajectoryStride = 4; }` | |

`GrenadeWalker.cs` references nothing in the App beyond its own namespace, so lifting it into a
library or into CS2DemoKit is a file move (§5.1). D2 in §8 offers the alternative of a separate
assembly from day one.

### 3.2 The walk API

```csharp
namespace DemoViewer.NET.Modules.UtilityBook;

public static class GrenadeWalker
{
    /// One row per grenade, yielded when the projectile ends (removed, detonated, or the
    /// walk reaches maxFrames). Lazy: nothing decodes until enumerated; a second enumeration
    /// restarts from frame 0. Every frame is decoded whatever the stride (delta encoding).
    public static IEnumerable<GrenadeRow> Walk(
        ParsedDemo demo,
        GrenadeWalkOptions? options = null);
}

public sealed record GrenadeWalkOptions(
    int TrajectoryStride = 4,          // output subsampling only; bounce vertices always kept
    int MaxFrames = int.MaxValue,
    IThrowerInputSource? Inputs = null, // §3.7; null = full-payload commands from the demo
    IReadOnlyList<ClipRound>? Rounds = null); // ClipRounds.Derive(demo) when null
```

The shape mirrors `PositionSampler.Walk` on purpose: same laziness, same "no late start" rule, same
frame-stride semantics, one static entry point, no tracker exposed. It differs in what it yields: a
projectile's row is complete only when its entity ends, so the walk yields rows in completion order and
holds at most the live projectiles (a handful) plus the pawn ring. Rows for projectiles still alive at
`MaxFrames` are yielded with `EndKind = DemoEnded`.

### 3.3 The walk internals

Per demo, once: build `firesBySlot` from `demo.AllGameEvents` (`weapon_fire` whose `Weapon` is one of
the six grenade names, sorted by `GameTick` per `UserId`), `detonationsByKind` (the five detonation
events of §2.4, sorted by `GameTick`), `grenadeThrownBySlot` (HLTV only, usually empty), and the round
list. Create `EntityTrackerFactory.CreateCurated()` and subscribe `EntityCreated`.

Per frame, in this order:

1. `tracker.AdvanceOneFrame(frame)`.
2. **Pawn ring.** `PawnLookup.ForEachLivePawn(tracker, state, static callback)` (the allocation-free
   overload, `PawnLookup.cs:54`) writes into a per-slot ring of 64 entries: tick, `m_fFlags`,
   `CellToWorld`, `m_angEyeAngles`, `m_bDucked`, `m_bIsWalking`, `m_iTeamNum`, and the held weapon's
   `m_flThrowStrength` (via `m_pWeaponServices.m_hActiveWeapon` -> `ResolveHandle`). 64 frames covers
   the 15-tick release-to-spawn maximum plus the 8-tick speed window with margin.
3. **Live projectiles.** For each tracked `(index, serial)`: if the slot is empty, holds another serial
   or another class, finalize (step 5). Else read `CellToWorld`, `m_nBounces`, and the per-class
   detonation fields. A cell sample is **rejected when any cell index is 0** (cell 0 covers
   -16384..-15872 on that axis, outside every shipped map); this is the zero-cost guard for the smoke
   defect and never triggers on a correct decode. The first trajectory point is `m_vInitialPosition`,
   never a cell read. A point is appended when the position moved by more than 0.1 units and either the
   stride divides the sample count or `m_nBounces` increased since the last point.
4. **Creation frame** (once per projectile): resolve the thrower by the `ResolveThrowerSlot` chain. If
   it fails, fall back to the **weapon_fire join**: the grenade `weapon_fire` of the matching weapon
   family in `[spawnTick - 15, spawnTick - 6]` whose slot has no other projectile spawned in that window;
   the row records `ThrowerSource = Entity | WeaponFire`. Then the **release lookup**: the latest
   matching `weapon_fire` for that slot within 40 ticks before spawn gives `ReleaseTick`; the ring entry
   at that tick gives release position, eye angles, ground flag, ducked flag, throw strength; the ring
   entry 8 ticks earlier gives the speed. Fallbacks in order: `grenade_thrown` (HLTV), then
   `spawnTick - 7` with the ring entry at that tick; `ReleaseSource` records which. Thrower state at the
   spawn frame is read directly from the pawn.
5. **Finalize.** Detonation per the §2.4 table: the joined event by `EntityId` (HE, flash, smoke, decoy)
   or by nearest `inferno_startburn` within 4 ticks of the last sample (molotov); entity-side fallbacks
   `m_nExplodeEffectTickBegin` and `m_nSmokeEffectTickBegin` (server clock, subtract
   `demo.ServerStartTick`) with `m_vSmokeDetonationPos`; last-sample fallback; `DetonationSource`
   records which. `AirTimeTicks` is spawn to the first frame the position stopped changing, or to
   detonation if earlier. Jump-throw and movement per §3.5. Round number is the last `ClipRound` whose
   `StartTickFrameClock <= SpawnTick`. Yield.

`StoreClassFilter` is not applied by default: the filtered walk saved 0 to 30% in §2.4 but the set must
include `CCSPlayerPawn`, the weapon classes (for `m_flThrowStrength`), the five projectiles and
`CCSPlayerController`, and an omission is silent. The option is left for the evaluator to enable once
the integration tests pin equality of the two outputs (§7).

### 3.4 The row

```csharp
public enum GrenadeKind { Smoke, Molotov, Incendiary, He, Flash, Decoy }
public enum ThrowerSource { Entity, WeaponFire, None }
public enum ReleaseSource { WeaponFire, GrenadeThrown, SpawnOffset }
public enum JumpThrowSource { Inputs, GroundFlag, None }
public enum MovementClass { Stationary, Walking, Running }
public enum ThrowStrengthClass { Full, Half, Underhand, Other, Unknown }
public enum DetonationSource { Event, Entity, LastSample, None }
public enum GrenadeEndKind { Detonated, Expired, Removed, DemoEnded }
```

| Field | Type | Source | Notes |
|---|---|---|---|
| `Id` | `string` | `g{index}-{serial}` | unique within a demo; the cross-demo key is `demo.sha256 + Id` |
| `Kind` | `GrenadeKind` | class; molotov vs incendiary from the joined `weapon_fire` name, else `m_bIsIncGrenade` | |
| `ThrowerSlot`, `ThrowerSteamId64`, `ThrowerTeam` | `int`, `string`, `int` | chain or join; `demo.Players`; pawn `m_iTeamNum` at spawn | team 2 = T, 3 = CT |
| `ThrowerSource` | enum | §3.3 step 4 | |
| `RoundNumber` | `int` | `ClipRounds` | 0 before the first `round_freeze_end` |
| `ReleaseTick`, `ReleaseSource` | `int`, enum | §3.3 step 4 | frame clock |
| `ReleasePosition` | `Vector3` | pawn origin at release tick | the `setpos` |
| `ReleaseEyePitch`, `ReleaseEyeYaw` | `float` | `m_angEyeAngles` at release tick | the `setang` |
| `ReleaseOnGround`, `ReleaseCrouched` | `bool?` | `m_fFlags & 1`, `m_bDucked` at release tick | null when the ring has no entry |
| `ThrowStrength`, `ThrowStrengthClass` | `float?`, enum | held weapon at release tick | Full >= 0.95, Half 0.35..0.65, Underhand <= 0.05 |
| `Movement`, `SpeedAtRelease` | enum, `float` | ring positions over 8 ticks | Stationary < 10 u/s, Walking < 140, else Running |
| `SpawnTick`, `SpawnPosition`, `SpawnVelocity` | `int`, `Vector3`, `Vector3` | creation frame, `m_vInitialPosition`, `m_vInitialVelocity` | |
| `ThrowerPositionAtSpawn`, `ThrowerOnGroundAtSpawn` | `Vector3?`, `bool?` | pawn at spawn frame | the fallback flag |
| `JumpThrow`, `JumpThrowSource`, `JumpPressTick` | `bool`, enum, `int?` | §3.5 | |
| `Trajectory` | `IReadOnlyList<TrajectoryPoint>` | `(Tick, X, Y, Z, Bounce)` | first = spawn, last = rest or detonation |
| `BounceCount` | `int` | max `m_nBounces` | |
| `AirTimeTicks` | `int` | §3.3 step 5 | |
| `DetonationTick`, `DetonationPosition`, `DetonationSource` | `int?`, `Vector3?`, enum | §3.3 step 5 | |
| `EndTick`, `EndKind` | `int`, enum | last frame seen | |
| `InfernoEntityIndex` | `int?` | `inferno_startburn.EntityId` | for a later fire-area join |
| `LandingPlace` | `string?` | reserved, null | filled by Zone Baking's `PlaceResolver` at index time |

Everything positional is in world units; every tick is the frame clock (F15). The row carries no
`setpos` string; `GrenadeConsole.Format(row)` builds it on demand (§3.6) so the format can change
without a schema bump.

### 3.5 Jump-throw and movement

Inputs are primary and the ground flag is the fallback, as F6 and D8 decided; the row always says which
produced the flag.

**Inputs.** `IThrowerInputSource.FindJumpPress(slot, fromTick, toTick)` (§3.7) returns the latest tick
in `[SpawnTick - 27, SpawnTick]` at which the thrower's `IN_JUMP` rose (bit 1 of `buttonstate1` going
0 to 1 between consecutive commands, or a sub-tick move with `button == 2 && pressed`). The window is
the measured 20 ticks before the attack release plus the 7-tick release-to-spawn gap; the +2 tolerance
of §2.4 falls inside it. If the source reports coverage for that window (at least one decoded command
in it), `JumpThrow = press found`, `JumpThrowSource = Inputs`.

**Ground flag.** Otherwise `JumpThrow = !ThrowerOnGroundAtSpawn`, `JumpThrowSource = GroundFlag`. This
is the spawn-frame flag, not the release-tick one, for the reason measured in §2.4 (6 of 20 binds press
jump after the release). It over-reports by the rare mid-air throw without a jump (1 of 182 on the 2025
demo).

**None.** No pawn at spawn (thrower dead before spawn, or the smoke defect with no join): `JumpThrow = false`,
`JumpThrowSource = None`.

**Movement.** `SpeedAtRelease` is the horizontal displacement between the ring entries at
`ReleaseTick - 8` and `ReleaseTick`, times 8. Thresholds are named constants (`StationaryMaxSpeed = 10`,
`WalkingMaxSpeed = 140`; CS2 walks a grenade at 127 u/s and runs it at 245). `Crouched` is `m_bDucked`
at release. These are the CS2UTIL-style movement words the card prints (Lineup Cards); the raw speed is
kept so the thresholds can move without a re-walk.

### 3.6 The `setpos`/`setang` string

```
setpos -2260.00 -1036.00 -414.00; setang -18.12 -15.30 0.00
```

`GrenadeConsole.Format(row)` prints `ReleasePosition` (the pawn origin, which is what `setpos` sets)
and `ReleaseEyePitch`, `ReleaseEyeYaw`, roll 0, two decimals, invariant culture. For a jump-throw the
release position is where the player stood when the bind fired, which is what a practice-server user
needs; for a running throw the string alone does not reproduce the throw, and the card's movement word
says so. D6 in §8 offers the spawn-tick position instead.

### 3.7 The input seam (CS2DemoKit #53)

```csharp
public interface IThrowerInputSource
{
    /// True when at least one command for slot decoded in [fromTick, toTick] (frame clock).
    bool HasCoverage(int slot, int fromTick, int toTick);
    /// The latest tick in the window at which IN_JUMP rose, or null.
    int? FindJumpPress(int slot, int fromTick, int toTick);
    /// Share of the demo's commands that decoded, for the sidecar header.
    double Coverage { get; }
}
```

`FullPayloadInputSource` (v1, in `GrenadeWalker.cs`) scans `frame.GetUserCmdsPayload(i)` for every
frame once per walk, parses `CSVCMsg_UserCommands`, keeps only commands with `Data` set, and stores per
slot a compact `(tick, buttonstate1, jumpPressSubtick)` list keyed by
`ServerTickExecuted - demo.ServerStartTick` (the probe measured that offset as exactly `ServerStartTick`
on all three demos). It costs 0.6 to 1.2 s per demo (§2.4) and yields 100% coverage on demos up to
mid-2026 and about 0.2% after. When #53 lands, `ReconstructedInputSource` wraps the engine's
reconstructor behind the same interface; the sidecar header's `inputDecoder` field (§3.9) is what tells
the Utility Book that a demo indexed under v1 can be re-indexed for better jump-throw flags.

### 3.8 The evaluator

`GrenadeIndexEvaluator : IDemoEvaluator`, `Id = "grenades"`, registered at the composition root
next to `[library, highlights]` (`App.axaml.cs:704-720`) and constructed the way `HighlightScanService`
is (`:697-703`: cache store, library paths delegate, opt-in delegate, UI poster).

| Member | Behaviour |
|---|---|
| `Wants(path)` | index row exists at tier 2 or above, `GrenadeSchema < GrenadeSidecar.CurrentSchema` or the walker version differs, and (`GrenadesSettings.BackgroundIndex` or the path is in the forced set) |
| `Evaluate(path, parsed)` | run `GrenadeWalker.Walk(parsed, options)` to a list, write the sidecars, stamp; on throw mark `GrenadeState = Failed` (retry is an explicit user action, the highlights rule) |
| `OnParsedOpportunistically` | same as `Evaluate` when the row is stale, regardless of the opt-in: an interactive open and a Library tier-2 pass hand over a parse the walk rides for 2 s |
| `PriorityFor` | `UserRequested` for a forced path (the Utility Book's "Index this demo"), else `Background` |
| `OrderHint` | file mtime ticks, newest first, like the Library |
| `PendingPaths()` | derived from the index (`GrenadeSchema`, `GrenadeState`), fed to the coordinator's candidate union |

The walk runs synchronously inside the queue's gate slot with the `ParsedDemo` held, which is the
one-heavy-parse invariant `HeavyJobGate` enforces (`HeavyJobGate.cs:3-33`, `AcquireBackgroundAsync`
`:225`): one demo at a time, background yields to an interactive open between demos, paused during a
reel or an export. Nothing new touches the gate. A 700-demo library at about 2.5 s per demo is 30 minutes
of background work, in the same queue and often on the same parse the Library's tier-2 pass already
holds, so the opt-in default is off like `HighlightsSettings.BackgroundScan` and the Utility Book tab
carries the switch and the per-demo button.

### 3.9 Storage

Two sibling sidecars per demo beside the record, keyed by the same `StableKey`, written through the
store so the atomic write and the in-memory browser path are shared:

| File | Contents | Size (§2.4) | Read by |
|---|---|---|---|
| `demos/<key>.grenades.json` | header + rows without `Trajectory` | ~120 KB at 300 grenades | Grenade Index (all demos), Lineup Cards |
| `demos/<key>.grenades.paths.json` | `Id -> TrajectoryPoint[]` at the configured stride | ~230 to 300 KB at stride 4 | the card's radar path, the clip render, one demo at a time |

Header of both files:

```json
{
  "schemaVersion": 1,
  "walker": { "version": "0.8.2", "engine": "CS2DemoKit.Parser 0.12.0", "inputDecoder": "full-payload" },
  "demo":   { "sha256": null, "fileName": "match730_....dem", "sizeBytes": 289436777 },
  "clock":  { "kind": "dv-frame-clock", "tickRate": 64, "frameCount": 154869, "firstTick": 0, "lastTick": 132515 },
  "source": { "kind": "GotvMatchmaking", "build": 10231, "inputCoverage": 1.0, "trajectoryStride": 4 },
  "grenades": [ ... ]
}
```

`demo` and `clock` are `DemoIdentity` and `ClockIdentity` reused as-is (`AnnotationIdentity.cs:17`,
`:33`). `sha256` is null until Content Identity populates `DemoCacheRecord.Sha256`; the evaluator
copies it when present and the Grenade Index treats a null hash as "path-keyed only". A reader that
finds a hash different from the record's ignores the file, the annotation rule.

`DemoCacheStore` gains three members: `WriteSibling(demoPath, suffix, json)` and
`TryReadSibling(demoPath, suffix)` (atomic on disk, dictionary in memory when `_cacheRoot` is null), and
sibling deletion inside `Remove` / `RemoveWhere` / `DeleteSidecar` (`:421-457`, `:536-557`) so identity
drift and removal cannot orphan the files. `DemoCacheRecord` gains `TierStamp Grenades`,
`DemoAnalysisState GrenadeState`, `int GrenadeCount`, `string? GrenadeWalker`, `double GrenadeInputCoverage`;
`DemoCacheIndexEntry` mirrors `GrenadeSchema`, `GrenadeState`, `GrenadeCount` (the `HighlightCount`
pattern, `DemoCacheModels.cs:419`) so the backlog and the cross-demo picker never open a sidecar to
decide. `GrenadeSchema` is not a `DemoCacheTier`: the tiers are cumulative (`:16-33`) and grenades
depend on the parse, not on the analysis run, so they are a stamp beside the tiers, invalidated by their
own schema constant and by identity drift, never by a rules fingerprint change.

D2 in `plan.md` §6 (JSON versus SQLite) was decided for JSON sidecars plus in-memory postings at
The Round Index review (2026-09-23, measured on a real hundred-demo corpus), so this layout is the
answer; the row converts to a table without a schema change should the revisit trigger ever fire.

### 3.10 Browser host

Recorded per `wasm-matrix.md` convention, to be added to its matrix when this ships:

| Capability | Browser | Mechanism |
|---|---|---|
| Grenade Walk, background index | absent by design | the processing queue is absent; the evaluator is not registered on the browser head |
| Grenade Walk, open demo | degraded, session only, and the Utility Book says so | `OnParsedOpportunistically` from the interactive open writes to the in-memory store; the tab shows the annotation panel's wording ("this browser tab forgets ... when it reloads") |
| Walk time on the interpreter | unmeasured | native is ~2 s per 100k frames; the parse row in the matrix suggests tens of seconds |

### 3.11 UI touchpoints

None in this item. The consumers are later build items: Grenade Index reads `.grenades.json` across
the library through `TryReadSibling` and clusters; Lineup Cards read one demo's rows and paths and call
`GrenadeConsole.Format`; Opening Tendencies joins `ReleaseTick` against Round Facts. The one surface this
item touches is Settings (the `BackgroundIndex` toggle, a row beside the highlights scan opt-in) and a
"Index grenades" action on the Match Overview completeness chip, which sets the forced path and submits
through the coordinator the way the highlights retry does.

---

## 4. Alternatives considered and why not

**4.1 Read grenades out of the analysis engine (rules or `EntityChangeScanner`).** The engine's projectile
reads are internal, admit smoke and molotov only, and produce a digest for the visibility solver, not
trajectories. A rules-v2 ruleset cannot express "the entity's position on every frame" (F11). App-side
first is the plan's own call (`plan.md` §3, Grenade Walk).

**4.2 Reuse `SceneFrameBuilder`'s trail accumulation.** It runs on the host's coalesced snapshot view,
keys by serial, and is incomplete on seek by design (`GrenadeTrail.cs` class doc). A walk needs frame 0
and every frame; the trail code is a render read.

**4.3 Extend `PositionSampler.Walk` to yield projectiles too.** It is an upstream type; changing its
sample shape breaks `rules/highlights_position.rules.yaml` consumers and the Round Index design that
depends on it. A sibling is the right shape (§5.1).

**4.4 Derive release from inputs (attack release) instead of `weapon_fire`.** Inputs are absent on
99.8% of commands on current demos (F6) and the event is on every GOTV demo and precedes spawn by a
fixed 7 ticks. Inputs are kept for the one thing events cannot give: the jump press.

**4.5 Sample the ground flag at the release tick.** Measured wrong on 6 of 20 jump-throws (§2.4).

**4.6 Store full-resolution trajectories.** 0.9 to 1.2 MB per demo, 1 GB per thousand demos, for
points 4 units apart that no consumer draws at that density. Stride 4 with bounce vertices keeps the
shape at a quarter of the size; the raw walk is 2 s away for anyone who needs more (D1).

**4.7 Rows only, re-walk for paths on demand.** Saves 230 KB per demo; costs a 2 s parse-and-walk every
time a card is opened, and the clip render then needs the parse anyway. Kept as D1's alternative.

**4.8 A new `DemoCacheTier.Grenades = 4`.** The tiers are cumulative and grenades do not depend on the
analysis run; a fourth tier would make a rules change invalidate grenade rows. A stamp beside the tiers
is the annotation sidecar's precedent.

**4.9 Wait for the smoke decode fix before shipping.** The join and the zero-cell guard recover every
smoke's thrower, spawn and rest point today; the fix improves the trajectory's first samples and removes
the fallback. Shipping does not depend on it.

---

## 5. External and engine changes required

**Required: none.** Everything in §3 reads CS2DemoKit 0.12.0 and CS2OpenDev.Protos 0.9.0 as packaged.
**AssetBaker bundle schema:** none (`LandingPlace` is reserved for Zone Baking). **CSVG game plugin:** none.

Two proposals follow, neither assumed to exist.

### 5.1 Proposal: `ProjectileSampler`, a sibling of `PositionSampler` in `CS2DemoKit.Parser`: **filed as CS2DemoKit #59** (2026-09-24, D7 as recommended)

Once the app-side walk has run over the library and its integration tests are green, propose upstream,
in `CS2DemoKit.Parser.EntityTracking`, the per-frame half of §3.3 with the same contract as
`PositionSampler`:

```csharp
public readonly record struct ProjectileSample(
    int FrameIndex, int Tick, int EntityIndex, int Serial, string ClassName,
    int ThrowerSlot,            // -1 when unresolved, the ResolveThrowerSlot contract
    Vector3? Position,          // CellToWorld, null when unseen or a cell index is 0
    Vector3? InitialPosition, Vector3? InitialVelocity,
    int Bounces, bool Created, bool Removed);

public static class ProjectileSampler
{
    public static IEnumerable<ProjectileSample> Walk(ParsedDemo demo, int frameStride = 1, int maxFrames = int.MaxValue);
}
```

It would make `ProjectileSlotIndex` admit all five classes (or take the set as a parameter) and make
`ResolveThrowerSlot` public, which is where the engine already keeps that knowledge. The row assembly
(§3.4 to §3.6), the event joins and the input seam stay in the consumer, or later move to
`CS2DemoKit.Analysis` beside `ClipRounds` as `GrenadeRows.Build(demo)`; the Parser-level sampler is the
smaller and less opinionated first ask. The measured cost (§2.4) and the parity test (§7) go in the
proposal as the evidence `PositionSampler`'s own doc comment carries.

### 5.2 Bug report, `CSmokeGrenadeProjectile` creation-packet decode: **filed as CS2DemoKit #56**

*Filed 2026-09-24 after confirming on a fourth demo (de_inferno, build 10896, plain
`EntityTracker`, 190,423 frames): 114 of 114 smokes with a garbage first cell (median 23,160 units
from `m_vInitialPosition`, against 13.9 for 140 HE grenades), 21 of 114 with no thrower ever
resolved, the bad axis persisting to end of life, zero decode errors raised. The draft below is what
was filed, with that run added to its table.*

> **Title:** CSmokeGrenadeProjectile decodes wrong values for several fields on its creation packet
> (cells, m_nBounces, m_nEntityId, sometimes m_hThrower); no decode error raised
>
> **Summary.** On every `CSmokeGrenadeProjectile` creation in three Valve matchmaking demos (builds
> 10231 and 10896), the entity's state after the creation frame reads `CBodyComponent.m_cellY = 0`,
> `m_cellZ = 0` (while `m_cellX` and `m_vecX/Y/Z` are right), `m_nBounces = -46`, `m_nEntityId` as a
> large negative and `m_iTeamNum = 0`. `m_vInitialPosition`, `m_vInitialVelocity` and later
> `m_vSmokeDetonationPos` decode correctly. On 16/57, 13/80 and 11/91 smokes `m_hThrower` reads 0
> (build 10231) or 12 (10896) for the entity's whole life. `CHEGrenadeProjectile`,
> `CFlashbangProjectile`, `CMolotovProjectile` and `CDecoyProjectile` created in the same demos decode
> correctly on their creation frame (`|m_vInitialPosition - CellToWorld|` = 13 to 15 units). Same result
> with `EntityTrackerFactory.CreateCurated()` and `new EntityTracker()`. `DecodeErrorRaised` never
> fires; `DeltaUnknownCount` is 0. Because a cell that never changes again is never re-sent, a smoke at
> rest can keep `-16384` on an axis for its life; `PositionUtil.CellToWorld` returns it as a position.
>
> **Repro.** Any `match730_*.dem` from `game/csgo/replays`. Subscribe `EntityCreated`, and on the
> creation frame of the first `CSmokeGrenadeProjectile` print `m_nBounces`, `CBodyComponent.m_cellY`,
> `m_nEntityId`, `m_hThrower`; compare with the first `CHEGrenadeProjectile`. Per-frame dump (frame +0,
> +1, +2) from the probe:
> `thrower=2523565 cell=(26,0,0) vec=(861.5,998.9,713.9) init=(-2224,-1045,-319) bounces=-46 entId=-37977780 fields=1430`.
>
> **Where I would look.** The class's serializer carries `m_VoxelFrameData : CNetworkUtlVectorBase<uint8>`
> and `m_nVoxelFrameDataSize`; the misread fields are the ones whose descriptors sit after or around it,
> and the dump reads that array as values up to 67,108,860, so the element type or the array's
> field-path handling looks mis-detected for this class. `tools/EntityDecodeProbe --schema` in
> DemoViewer.NET dumps the flattened serializer for a class list and was written for exactly this hunt.
>
> **Environment.** CS2DemoKit.Parser 0.12.0 (NuGet), `origin/main` at `e443196`; CS2OpenDev.Protos
> 0.9.0; .NET 10; Windows 11. Probe: `scratchpad/GrenadeWalkProbe`, outputs `out3-2025-*.txt`,
> `out3-2026.txt`.

Filed per D3 (2026-09-24). The design does not wait for it (§4.9); when it
lands, the `ThrowerSource = WeaponFire` fallback and the zero-cell guard stop triggering, and the
walker version bump re-indexes.

### 5.3 Asks on sibling designs (not external projects)

| From | Needed | Shape |
|---|---|---|
| Content Identity | `DemoCacheRecord.Sha256` populated at tier 2 | the header's `demo.sha256`; the walk starts with it null |
| Zone Baking | point to place | `PlaceResolver.Resolve(map, x, y, z) -> string?` fills `LandingPlace` at Grenade Index time, not at walk time |
| Round Facts | nothing | `ClipRounds.Derive` gives the round number; Opening Tendencies joins the rest |
| Delta User Commands (#53) | a reconstructed `CSGOUserCmdPB` per command | wrapped by `ReconstructedInputSource` behind `IThrowerInputSource` |
| Inputs Per Demo Source | the per-source coverage table | cited by the Utility Book's "jump-throw from inputs on N% of this library" line; this design's own numbers cover Valve matchmaking 2025 and 2026 only |

---

## 6. Risks and unknowns

| Risk | Likelihood | Effect | Mitigation |
|---|---|---|---|
| HLTV and FACEIT demos behave differently (no `weapon_fire`, different `EntityId` semantics, no `svc_UserCmds`) | unknown; no such demo survives locally (F6c) | release falls back to `grenade_thrown` then `spawn - 7`; jump-throw falls to the ground flag; `ReleaseSource` and `JumpThrowSource` make it visible | Inputs Per Demo Source probes one demo per source; the integration test is parameterised by source and skips with a reason |
| The smoke decode defect widens (a build where other classes misdecode) | low | garbage trajectories, wrong throwers | the zero-cell guard, the `weapon_fire` join, and the counts-versus-`weapon_fire` invariant in the tests catch it per demo; the header records the engine version |
| The 7-tick release-to-spawn gap changes with a game update | low | `SpawnOffset` fallback drifts | the constant is named and the test measures the gap on every real demo it runs |
| Jump-throw from the ground flag over-reports mid-air throws without a jump | measured 1 in 182 | a lineup labelled jump-throw that is a ledge drop | `JumpThrowSource = GroundFlag` is printed on the card |
| `m_flThrowStrength` read at the release tick reflects the previous weapon (fast switch) | low | wrong strength class | `ThrowStrengthClass.Unknown` when the held weapon is not a grenade class |
| Background indexing doubles the library's queue time when both highlights and grenades are opted in | certain if both are on | 30 more minutes on a 700-demo library | same parse when both are pending; opt-in default off; the queue's newest-first order |
| The pawn ring misses the release tick (a frame gap, a dead thrower) | rare | null release fields | the row keeps `ReleaseSource`, the card prints "release state unavailable" rather than zeros (the "no data" rule from the stats board) |
| Browser walk time on the interpreter | unmeasured | a stalled tab | the browser head walks only on demand, off the UI thread, with a cancel |

Unknowns to close during the build: the exact `m_fFlags` bit for ducking (the probe used
`m_bDucked`); whether `decoy_started` is the right end for a decoy's air time on every source; the
in-browser walk time.

---

## 7. Test and verification strategy

**Unit (App tests, no demo).** The pure helpers take scripted inputs: detonation resolution per kind
(event, entity, last-sample, none); jump-throw classification for the four `(inputs coverage, press,
ground flag)` combinations, including the press-after-release case; movement class at the thresholds;
`GrenadeConsole.Format` invariant culture and rounding; polyline subsampling keeps the first point, the
last point and every bounce vertex at any stride; the zero-cell guard rejects `(0, y, z)` and accepts
`(1, y, z)`; the sidecar round-trips (write, read, equal) and a header with a different `sha256` is
ignored.

**Integration (`[Category("Integration")]`, `DemoTestHelper.RequireDemo`, skips without a demo, never the
tour sample).** On the discovered demo: projectile count per kind equals the grenade `weapon_fire`
count per weapon within 1; `ThrowerSource != None` for 100% of rows; `ReleaseSource == WeaponFire` for
at least 99% on a GOTV demo; `SpawnTick - ReleaseTick` in `[6, 15]`; every smoke's `DetonationPosition`
equals the `smokegrenade_detonate` xyz within 0.5 units; every HE's `DetonationTick` equals the event
tick; yaw of `SpawnVelocity` against `ReleaseEyeYaw` p90 under 5 degrees over grounded rows; no
trajectory point outside `[-16000, 16000]` on any axis; on a demo whose `inputCoverage` is above 0.9,
`JumpThrowSource == Inputs` on every row with a live thrower and the ground flag agrees on at least 95%
(on a delta demo the test skips with "inputs are delta-encoded, CS2DemoKit #53"); walk wall time under
5 s per 100,000 frames in Release (loose, per measured bench variance). A second test pins the
filtered walk (`StoreClassFilter`) equal to the unfiltered one row for row before the evaluator may
enable it.

**Golden.** The first 20 rows of `DemoTestHelper.ReferenceDemoFileName` serialized with positions
rounded to 0.1, committed under the App tests' goldens, skipped when the reference demo is absent (the
capture tests' precedent). A schema bump re-records it deliberately.

**Evaluator.** `DemoEvaluationCoordinator` tests already exist (`DemoEvaluationCoordinatorTests.cs`); add
the grenade evaluator to the registered list in a fixture and assert: `Wants` false when the index row
is current; a forced path submits at `UserRequested`; `Evaluate` stamps and the index mirror updates;
`OnParsedOpportunistically` refreshes a stale row without the opt-in; `Remove` deletes both siblings.

**The probe.** `GrenadeWalkProbe` is promoted to `tools/GrenadeWalkProbe` if the per-demo report is
worth keeping (modes `walk`, `plain`, `filtered`, `sampler`; it is what produced §2.4 and
what the upstream report cites). Otherwise its measurements live in this document only.

---

## 8. Decisions

**D1. Trajectories in the sidecar at stride 4 (recommended), or rows only with a re-walk on demand.**
Stride 4 is 230 to 300 KB per demo and keeps the bounce vertices; rows only is 120 KB and costs 2 s per
card open.

**D2. Where the walker lives.** `Modules/UtilityBook/GrenadeWalker.cs` in the App (recommended: it
ships in the same PR as the evaluator) or a new `src/Analysis/DemoViewer.NET.GrenadeWalk` library with a
Parser-only dependency (cleaner for §5.1, one more csproj).

**D3. File the smoke decode report (§5.2) now.** *Done: CS2DemoKit #56, 2026-09-24.* The design
does not wait for it.

**D4. Background indexing opt-in.** Default off like highlights (recommended), and the open demo is
always walked opportunistically. Alternative: on by default, since the walk rides the Library's own
parse when both are pending.

**D5. Named thresholds.** Jump-press window 27 ticks before spawn; movement 10 and 140 u/s; strength
classes at 0.95 / 0.35..0.65 / 0.05. Accept or change; each is a constant with the measurement beside
it.

**D6. `setpos` position.** The pawn origin at the release tick (recommended; where the player stood
when the bind fired) or at the spawn tick (where the projectile physics started, 20 to 27 units on from
release on a running throw).

**D7. Upstream tier.** Propose the Parser-level `ProjectileSampler` (recommended, §5.1) or an
Analysis-level `GrenadeRows` beside `ClipRounds`.

---

## 9. Effort estimate and sequencing

| Step | Estimate | Depends on |
|---|---|---|
| `GrenadeWalker` with the pure helpers and the full-payload input source; unit tests | 2 days | nothing (Sha256 nullable, `LandingPlace` reserved) |
| Integration tests, golden, parity test for the filtered walk | 0.5 day | a real demo on the machine |
| `GrenadeIndexEvaluator`, settings, composition root, store siblings, index mirror, sibling deletion | 1 day | the walker |
| Match Overview action and Settings row | 0.25 day | the evaluator |
| wasm-matrix rows, this document's status line | 0.25 day | |
| Upstream: file §5.2; draft §5.1 with the measured table after the library has been walked | 0.5 day | D3, D7 |

About 4.5 days. Sequencing: Content Identity may land before or after (the header takes a null hash);
Delta User Commands is not blocking (the seam exists from day one); Zone Baking is not blocking. The
Grenade Index starts when the evaluator has walked the reference library once. Gate 4 to 5 in
`plan.md` §4 ("the Utility Book returns clustered lineups") is the Grenade Index's, not this item's.

---

## 10. Sources

- `plan.md` §0, §2 (F6, F9, F11, F14, F15, F16, F17), §3 Phase 4, §6 D2, D4, D8.
- `docs/strat-book/designs/tag-store.md` §3.10 (Content Identity ask, headers reused).
- `docs/playback2d-v2/annotations-format.md:22-36`; `docs/playback2d-v2/wasm-matrix.md`.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs`, `DemoCacheModels.cs`; `Services/HeavyJobGate.cs`;
  `Services/DemoProcessing/IDemoEvaluator.cs`, `DemoEvaluationCoordinator.cs`, `DemoProcessingTypes.cs`;
  `Modules/Highlights/HighlightScanService.cs`, `HighlightsModule.cs`; `Modules/Library/DemoLibraryService.cs`;
  `Configuration/AppSettings.cs`; `Features/FeatureCatalog.cs`; `App.axaml.cs:690-720`;
  `ViewModels/Stats/AimCapabilityProbe.cs`; `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/SceneFrameBuilder.cs`,
  `Annotations/AnnotationIdentity.cs`; `src/Playback2D/DemoViewer.NET.Playback2D.Core/GrenadeTrail.cs`;
  `src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs`; `tools/EntityDecodeProbe/Program.cs`.
- CS2DemoKit `origin/main` (`e443196`, 0.12.0): `src/CS2DemoKit.Parser/EntityTracking/PositionSampler.cs`,
  `PositionUtil.cs`, `PawnLookup.cs`; `src/CS2DemoKit.Parser/Models/SubTickExtractor.cs`;
  `src/CS2DemoKit.Analysis/ProjectileSlotIndex.cs`, `EntityDigestExtractor.cs`, `EntityChangeScanner.cs`,
  `Events/MolotovThrownEvent.cs`, `Abstractions/DemoSourceProfile.cs`, `Profiles/Cs2GotvProfile.cs`,
  `Profiles/Cs2HltvProfile.cs`, `Registry/EventRegistry.cs`, `Visibility/VisibilityAnalyzer.cs`.
- Packaged XML docs: `~/.nuget/packages/cs2demokit.parser/0.12.0/lib/net10.0/CS2DemoKit.Parser.xml`
  (`EntityTracker`, `EntityState`, `PawnLookup`, `PositionSampler`, `PositionUtil`, `GameEvent`, `ParsedDemo`,
  `DemoSourceKind`), `cs2demokit.analysis/0.12.0/.../CS2DemoKit.Analysis.xml` (`ProjectileSlotIndex`,
  `DemoSourceProfile`, profiles); reflection over the same DLLs and `CS2OpenDev.Protos` 0.9.0
  (`CMsgServerUserCmd`, `CSGOUserCmdPB`, `CBaseUserCmdPB`, `CSubtickMoveStep`, `CInButtonStatePB`).
- CS2OpenDev-Docs `cs2_schema.json` (build 25218825, 2026-09-09) and `gameevents.json`, fetched 2026-09-23.
- CS2DemoKit issue #53 (delta-encoded user commands).
- Earlier findings: CS2DemoKit tick clocks; CS2DemoKit local checkout; bench run variance; tour sample demo is
  invalid; sight columns and "no data".
- Scratch probe `GrenadeWalkProbe` (`Walk.cs`, modes `walk`, `plain`, `filtered`, `sampler`),
  outputs `out2-2025.txt`, `out2-2026.txt`, `out2-2026b.txt`, `out3-2025-walk.txt`, `out3-2025-plain.txt`,
  `out3-2026.txt`; and `UserCmdProbe` for the F6 input measurement.
