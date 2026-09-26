# Zone Baking (issue #5): design

> **Status: APPROVED 2026-09-23** by the owner, as revised: D1 to D7 answered in §8, the Custom Zones
> overlay (§3.8) in scope, Z-1 and Z-2 accepted as recommended. Z-1 is carried into The Round Index
> review. Nothing here is implemented.

**Revision note (2026-09-23):** decisions D1 to D7 are answered in §8 (D1 was
left to this design's investigation and is decided there: no `bundle.json` change, no bump). The
owner asked for one addition, **user-defined zones and callouts with the baked set as the default**,
which is §3.8 and raises two new decisions, Z-1 and Z-2, with recommended answers. Nothing here is
implemented.
**Tree:** `main` at `d90ec9f` (0.8.1). CS2DemoKit 0.12.0. ValveResourceFormat 19.2.6339. ValvePak 4.0.0.142.
**Work item:** Zone Baking, `docs/strat-room/plan.md` §3 Phase 0 (plan.md:255). Findings F1, F2, F15, F17 apply.
**Date:** 2026-09-23.

---

## 1. Problem and scope

Issue #5 asks for "map trigger volumes / nav zones, baked into the map asset bundles, so a world
position can resolve to a named place or a bombsite", with the baker as the place the extraction
lands so the app stays VRF-free.

Finding F1 (plan.md:61) removes the round index from the list of consumers: `PositionSampler.Walk`
already yields each pawn's `m_szLastPlaceName`, and `rules/highlights_position.rules.yaml:36-53`
consumes it as `player.place`. What is left needs a lookup from an arbitrary world point, which no
demo field gives:

| Consumer (plan.md name) | Needs from this item |
|---|---|
| Query Canvas (plan.md:275) | a click on a pane at a given floor, to a place token identical to what the index stores |
| Grenade Index (plan.md:417) | a detonation or landing point, to a place |
| Tolerance Slider (plan.md:293) | a place-adjacency graph |
| Zone outlines on the canvas | per-floor outline geometry for each place, drawable by a scene layer |
| "inside bombsite" for arbitrary points (Grenade Index, Round Facts consumers) | the two `func_bomb_target` volumes as testable solids |
| Click To Tag Position (plan.md:331) | point to place |
| Callout Aliases (plan.md:369) | the canonical place vocabulary per map |
| A technical user (owner requirement, review) | a way to define their own zones and callouts over the baked default, because teams do not share callouts |

In scope: the baker extension, the file it writes, the app-side resolver, the outline layer's data
contract, the user overlay that defines custom zones over the baked default (§3.8), a validation
method with measured miss rates, and the browser story. Out of scope: the consumers above (each is
its own work item), a graphical zone editor (§3.8 says why a JSON file plus the outline layer is
the first release), and any engine-side place resolution (§5).

## 2. What exists today (cited)

### 2.1 The baker

`tools/DemoViewer.NET.AssetBaker` is a standalone console tool, not in the solution, that owns VRF
and ValvePak (`DemoViewer.NET.AssetBaker.csproj`, package block). It bakes out of the gitignored
`cs2-assets/` cache into the committed `assets/<map>/` directories (`Program.cs:166-173`).

- `Program.cs:12-13`: `SchemaVersion = 1`, `BakerVersion = "0.3+vrf19.2.6339"`.
- `Program.cs:159-163`: the ten shipped maps: nuke, dust2, mirage, inferno, anubis, ancient,
  overpass, vertigo, train, cache.
- `Program.cs:232-242`: step 3 of a full bake reads `maps/<map>.nav` from the per-map vpk
  (`NavFloors.ExtractNav`, `NavFloors.cs:48`) and clusters area Z into floor bands
  (`NavFloors.ComputeFloors`, `NavFloors.cs:59`). It iterates `NavMeshArea.Corners` only
  (`NavFloors.cs:70`).
- `Program.cs:253-291`: step 5b extracts world collision from `maps/<map>/world_physics.vmdl_c`
  through `CollisionMesh.Extract` (`CollisionMesh.cs:41`), which reads an embedded
  `PhysAggregateData` and walks `Part.Shape.Hulls` and `Part.Shape.Meshes` (`CollisionMesh.cs:81-120`).
- `Program.cs:293-295`: `mapVersion` is a CRC32 over radar vtex bytes, nav bytes, the uncompressed
  collision bytes and the overview txt.
- `Program.cs:79-145`: `--collision` is a top-up mode that writes `collision.tris.gz` into an already
  baked map directory and deliberately does not touch `bundle.json`, because the CRC needs the
  radar source bytes only a full bake has staged (`Program.cs:74-78`). The app finds the soup by
  path, not by the bundle field (`Program.cs:76-78`; `src/App/DemoViewer.NET/Services/CollisionSoup.cs:33,46,90`).
- `Bundle.cs:48-58`: `AssetBundle(SchemaVersion, MapName, MapVersion, BakerVersion, Transform,
  Bounds, Floors, RadarLayers, RadarImages, CollisionMesh?)`, camel-cased JSON, additive by design.

A `bundle.json` without a `collision.tris.gz` is normal (four of the nine shipped bundles were
written by baker 0.1 with no collision step; `Program.cs:69-72`). The same will be true of zones.

### 2.2 Who reads the bundle

`bundle.json` is parsed by the **engine**: `CS2DemoKit.Analysis.Visibility.MapAssetBundleReader`
(`TryRead`, `FindBundleDirectory`, `TryReadIdentity`), into `MapAssetBundle`, whose XML doc says
"additive by design: an older consumer ignores fields it does not know". The app wraps that in
`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Assets/MapAssetPipeline.cs:102,119,167`
(`LoadedMapAsset`, `MapAssetPipeline.cs:23`) and loads it from
`src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs:1975-1983` (`EnsureMapAsset`).

Measured on 0.12.0 with a scratch project (`scratchpad/zone-probe`, mode `bundle`): a bundle with
`"schemaVersion": 2` or `99` and an unknown `"zones": {...}` object reads back as a full
`MapAssetBundle` with the right identity. The reader gates on nothing. So a schema bump and an
additive field are both safe for every existing consumer, and the app cannot reach a new field
through the engine DTO. Zone data therefore lives in its own file with its own reader (§3.2).

### 2.3 Floors and the floor key

The app's level model quantizes a floor's lower Z to a 64-unit grid:
`src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapSpace.cs:35` (`LevelQuantum = 64.0`) and
`:81` (`QuantizeZ(z) = floor(z / 64 + 0.5) * 64`, half-up on purpose). The annotation sidecar keys
world anchors by exactly that value (`docs/playback2d-v2/annotations-format.md:69-71`;
`Core/Input/DrawTool.cs:187`). Floors come from the bundle's `floors` bands, low to high, with the
outer bands extended to plus and minus 100 000 (`NavFloors.cs:35`; `assets/de_nuke/bundle.json`
carries `[-100000, -528]` and `[-528, 100000]`).

### 2.4 Persistence, modules, browser

`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs:10-33`: `index.json` plus per-demo
sidecars, atomic writes, fully in-memory on the browser host. This item persists no per-demo state
(§3.6), so it adds no tier; it does hand its consumers a version stamp they must store.

New surfaces are first-party `IWorkspaceModule`s (`Modules/Playback2D/Playback2DModule.cs:23-50`,
`Modules/Highlights/HighlightsModule.cs:31,43,51`). This item adds no tab; it adds one scene layer
(§3.5), which is an `ISceneLayer`
(`src/Playback2D/DemoViewer.NET.Playback2D.Core/Compositing/ISceneLayer.cs:18`) registered in
`SceneLayerCatalog.SceneStackIds` (`Pipeline/Headless/SceneLayerCatalog.cs:52`) and named in
`Core/Layers/SceneLayerIds.cs:7`.

`docs/playback2d-v2/wasm-matrix.md:117`: baked map art is read from disk beside the executable and
the browser head has no such directory; radar is "degraded" there. Zones inherit that row (§3.7).

### 2.5 Established facts about the source data

The brief's two research questions, answered by reflection and by running VRF against the
installed maps (`scratchpad/zone-probe`, modes `reflect`, `nav`, `kv`, `ents`, `vol`):

**Q1. Does VRF 19.2.6339 expose per-area place names on `NavMeshArea`?** No. The public surface is
`AreaId`, `HullIndex`, `DynamicAttributeFlags`, `Corners`, `Connections`, `LaddersAbove`,
`LaddersBelow`. `NavMeshFile` adds `Version`, `SubVersion`, `Areas`, `Ladders`, `IsAnalyzed`,
`GenerationParams`, `CustomData`, `KV3Unknown1..3`. VRF `master` (fetched 2026-09-23) adds
`AttributeFlags` and `MovableMeshId`, still no place. Nor is a place table in the file: de_nuke's
`.nav` is version 36, 3040 areas, 7676 directed connections, one hull; its `CustomData` KV3 is
Steam Audio bake settings only, and a scan of the raw bytes finds no place-name strings. In CS2 the
nav file does not carry places. The CS:GO place table is gone.

**Q2. Does `EntityLump` from the per-map vpk yield `func_bomb_target` volumes?** Yes, and it yields
the place volumes too. `maps/<map>/entities/default_ents.vents_c` parses through
`ResourceTypes.EntityLump`; each `Entity` is a `ValveKeyValue.KVObject`
(`IReadOnlyDictionary<string, KVObject>`), so `classname`, `place_name`, `bomb_site_designation`,
`origin`, `angles` and `model` read as dictionary entries (19.2 has no `Properties` member; the
indexer is the API). Two classes matter:

| Class | Count on the ten maps | Properties used | Geometry |
|---|---|---|---|
| `env_cs_place` | 18 (ancient) to 66 (cache); 418 total | `place_name`, `origin`, `angles`, `model` | brush model `maps/<map>/entities/<name>.vmdl`, in the vpk, with an embedded `PhysAggregateData`; every one on the ten maps is convex hulls, zero meshes |
| `func_bomb_target` | exactly 2 per map | `bomb_site_designation` (`"0"` = A, `"1"` = B, confirmed by nuke's `targetname "[PR#]A"` / `"[PR#]B"`), `origin`, `model` | same; 1 to 6 hulls |

Also present and cheap to carry: `func_buyzone` (with `teamnum` 2 = T, 3 = CT) and
`info_map_parameters.bombradius` (650 on nuke). `func_hostage_rescue` does not occur on the ten maps.

`env_cs_place` is the CS2 authority on place names: the schema class `CCSPlace` derives from
`CServerOnlyModelEntity` (CS2OpenDev docs, build 25218825), so the volumes are never networked and
never appear in a demo. Only the pawn's `m_szLastPlaceName` string does. `CBombTarget` derives from
`CBaseTrigger`.

Hull geometry conventions, measured on all 418 + 20 hulls: vertices are in entity-local space and
the entity `origin` translates them (`angles` is zero on every place and bombsite volume on the ten
maps; buyzone `[PR#]buyzone.2v2` on nuke has a 90 degree yaw and is the only rotated volume seen);
`BindPose` is empty; and the inside test is `dot(plane.Normal, p) - plane.Offset <= 0` for every
plane, verified by the hull's own vertex centroid. The opposite sign convention gives the mirror
image and silently halves coverage, which is the first bug the scratch probe had.

**Coverage of the nav mesh by the volumes** (area centroid, or centroid lifted 8 or 32 units, inside
some place volume):

| Map | `env_cs_place` | distinct places | nav areas | seeded by a volume | after flood fill (§3.3) | unreachable | areas in 2+ volumes |
|---|---|---|---|---|---|---|---|
| de_nuke | 58 | 29 | 3040 | 2556 | 2968 | 72 | 0 |
| de_dust2 | 43 | 24 | 2242 | 2172 | 2242 | 0 | 10 |
| de_mirage | 23 | 23 | 2544 | 2295 | 2540 | 4 | 91 |
| de_inferno | 46 | 23 | 2738 | 2425 | 2730 | 8 | 0 |
| de_anubis | 62 | 28 | 2633 | 2431 | 2633 | 0 | 2 |
| de_ancient | 18 | 18 | 1969 | 1760 | 1969 | 0 | 2 |
| de_overpass | 26 | 25 | 3938 | 3452 | 3923 | 15 | 287 |
| de_vertigo | 50 | 23 | 2105 | 1589 | 2102 | 3 | 0 |
| de_train | 26 | 16 | 2154 | 1996 | 2154 | 0 | 2 |
| de_cache | 66 | 48 | 2209 | 2185 | 2208 | 1 | 23 |

The `cs2-assets/` cache is staged on this machine (`cs2-assets/maps` is a link to the Steam maps
directory; `cs2-assets/radar` holds five vtex files), so a `--diag` run of the existing baker is
possible. No run of this design wrote under `assets/`.

## 3. Proposed design

### 3.1 Principle

The game defines places by volumes and the nav mesh defines where players can stand. The bake
carries both, joined: every nav area gets a place, seeded by the volumes and completed by a flood
fill over nav connections. The app resolves a point by the volumes first (exact, the game's own
rule) and by the nearest same-floor nav area second (covers gaps and edges). Outlines and adjacency
fall out of the joined data with no polygon union.

### 3.2 The file: `assets/<map>/zones.json`

A sibling of `bundle.json`, like `collision.tris.gz`, with its own schema version and its own
reader, located by path. **`bundle.json` is not changed and its `schemaVersion` stays at 1**
(decision D1, §8): the first draft proposed an additive `zones` reference, but the reader ignores
such a reference by design (§3.4, because the `--zones` top-up mode cannot write one), so it would
have been a field with no reader, which is exactly what the unread `collisionMesh` field already is
(`Program.cs:76-78`). Drift between the two files is detected through `bundleMapVersion` below.

```jsonc
{
  "schemaVersion": 1,
  "mapName": "de_nuke",
  "bundleMapVersion": "075a27b3",        // the bundle.json mapVersion this was baked beside
  "zonesVersion": "9f1c02aa",            // CRC32 over vents_c + every volume model + nav bytes
  "bakerVersion": "0.4+vrf19.2.6339",
  "floorQuantum": 64,                    // MapSpace.LevelQuantum, restated so a reader without Core can key floors
  "floors": [                            // copied from bundle.json, with the key each band mints
    { "key": -99968, "minZ": -100000, "maxZ": -528 },
    { "key": -528,   "minZ": -528,    "maxZ": 100000 }
  ],
  "places": [                            // index = placeId; order is stable across re-bakes (sorted by name)
    { "id": 0, "name": "Admin" },
    { "id": 1, "name": "BombsiteA" }
  ],
  "volumes": [                           // convex hulls, world space (origin already applied)
    { "kind": "place",    "place": 1, "entity": "2:61817:49",
      "min": [502, -940, -416], "max": [874, -500, -320],
      "hulls": [ { "planes": [ [nx, ny, nz, d], ... ] } ] },   // inside: n.p - d <= 0 for every plane
    { "kind": "bombsite", "site": "A", "entity": "2:47639", "min": [...], "max": [...], "hulls": [...] },
    { "kind": "buyzone",  "team": "CT", "min": [...], "max": [...], "hulls": [...] }
  ],
  "areas": [                             // one per nav area
    { "id": 1234, "place": 1, "floor": -528, "seed": true,
      "z": -416.0, "xy": [x0, y0, x1, y1, x2, y2, x3, y3] }   // corners, flat, world XY; z = corner mean
  ],
  "areaLinks": [ [1234, 1235], ... ],    // undirected nav connections, deduplicated (for outlines and a finer tolerance)
  "adjacency": [ [0, 12], [1, 7], ... ], // undirected place pairs that share a nav connection
  "parameters": { "bombRadius": 650 }
}
```

Field rules:

- `floor` on an area is `MapSpace.QuantizeZ(band.MinZ)` for the bundle band whose `[minZ, maxZ)`
  contains the area's mean corner Z. It is the same value a `SpaceRef.World` anchor carries
  (`annotations-format.md:69-71`), never a floor index. A reader that has `MapSpace` may recompute
  it from `floors[].minZ` and assert equality; a reader that does not has the value.
- Volumes are stored as planes, not vertices, because the resolver needs the half-space test and
  the baker already has the planes from VRF. `min`/`max` is the world AABB, the pre-reject.
- Coordinates are rounded to 0.1 units. Estimated size for nuke: 3040 areas at roughly 120 bytes
  plus 80 volumes at roughly 400 bytes, about 400 KB plain, under 100 KB gzipped. Decision D3
  (§8) picks plain or `.gz`.
- Place names are the raw `place_name` strings (`BombsiteA`, `TopofMid`, `HutRoof`). They are the
  canonical vocabulary Callout Aliases builds on; the file does not rename anything.

`mapVersion` keeps its current inputs (radar, nav, collision, overview). Zone bytes are not folded
in, so a zones-only re-bake does not restamp `mapVersion` and does not trip the golden stale-assets
check. `zonesVersion` is the baked stamp; the stamp consumers store is the **effective** version,
which also covers the user overlay (§3.8).

### 3.3 The baker: `Zones.cs` and two modes

New file `tools/DemoViewer.NET.AssetBaker/Zones.cs`, modelled on `CollisionMesh.cs`:

1. `ExtractVolumes(Package, map)`: open every `*.vents_c` under `maps/<map>/entities/`; for each
   entity whose `classname` is `env_cs_place`, `func_bomb_target`, `func_buyzone` or
   `func_hostage_rescue`, read `model` (a resource reference; strip to the path and append `_c`),
   load it from the same vpk, take `Model.GetEmbeddedPhys()`, and emit one hull per
   `Part.Shape.Hulls` entry as world-space planes (`Hull.GetPlanes()`, translated by `origin`,
   rotated by `angles` when non-zero). Log and skip a volume whose model is missing or has meshes
   instead of hulls (none on the ten maps; the log line is the guard).
2. `AssignAreas(NavMeshFile, volumes)`: seed each area whose centroid (or centroid plus 8 or plus
   32 units) lies in a place volume; when several volumes hit, apply the tie rule from decision D4;
   then breadth-first flood over `Connections` so every reachable area inherits the place of its
   nearest seeded neighbour by hop count. Unreachable areas (0 to 72 per map, isolated islands)
   get no place.
3. `BuildAdjacency(nav, assignment)`: every connection between two areas of different places adds
   an undirected pair. Measured: 40 (train) to 122 (cache) directed pairs; nuke's graph reads as
   the real callout map (Ramp to Admin, BombsiteB, Control; Outside to nine neighbours).
4. `Write(zones.json)` with `zonesVersion` from a CRC32 over the vents_c bytes, every volume model's
   bytes and the nav bytes.

Modes:

- The full bake (`BakeMap`, `Program.cs:193`) runs `Zones` after the nav step and writes
  `zones.json`. It does not touch `bundle.json` for zones (D1).
- `--zones [maps...]` is a top-up mode like `--collision` (`Program.cs:79-145`): reads the per-map
  vpk straight from the CS2 install, needs no staged `cs2-assets/`, discovers maps by the presence
  of `bundle.json`, writes `zones.json` only and never edits `bundle.json`. This is how the nine
  committed 0.1 bundles get zones without a re-stage, and it is why the app must locate the file by
  path (§3.4). It reads `floors` from the existing `bundle.json` so the floor keys match what the
  app already shows.
- `--zones --diag` prints the per-map coverage table of §2.5 and the adjacency list.

`BakerVersion` moves to `0.4+vrf19.2.6339`. VRF stays at 19.2.6339; nothing here needs 20.0.

### 3.4 App side: `ZoneSet`, `PlaceResolver`, `ZoneAssetPipeline`

Home: `src/Playback2D/DemoViewer.NET.Playback2D.Core/Zones/` for the model and the resolver (Core
is host-independent and `dv2d` can then draw outlines headlessly), and
`src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Assets/ZoneAssetPipeline.cs` for the file read,
beside `MapAssetPipeline`. No CS2DemoKit type in any signature; `System.Text.Json` with the same
reflection-based options the pipeline uses today.

```csharp
namespace DemoViewer.NET.Playback2D.Core.Zones;

public sealed class ZoneSet                       // the parsed file, immutable
{
    string MapName; string ZonesVersion; string BundleMapVersion;
    IReadOnlyList<ZoneFloor> Floors;              // (Key, MinZ, MaxZ)
    IReadOnlyList<string> Places;                 // index = placeId
    IReadOnlyList<ZoneVolume> Volumes;            // Kind, PlaceId?, Site?, Team?, Min, Max, Hulls (planes)
    IReadOnlyList<ZoneArea> Areas;                // Id, PlaceId (-1 = none), FloorKey, Z, Xy[]
    IReadOnlyList<(int, int)> AreaLinks;
    IReadOnlyList<(int, int)> Adjacency;
}

public enum PlaceHitKind { Volume, NearestArea, None }
public readonly record struct PlaceHit(int PlaceId, string? Name, PlaceHitKind Kind, double SnapDistance);
public enum Bombsite { A, B }

public sealed class PlaceResolver
{
    public PlaceResolver(ZoneSet zones);                        // builds a 128-unit XY grid over Areas and Volumes once
    public ZoneSet Zones { get; }

    public PlaceHit Resolve(Vector3 world);                     // 1. volumes at world and world+32z; 2. nearest area whose
                                                                //    floor band contains world.Z, XY box distance <= MaxSnap (128u);
                                                                //    3. None
    public PlaceHit ResolveOnFloor(double x, double y, double floorKey);   // for a click on a 2D pane: areas of that floor only,
                                                                            // volumes whose Z range meets the band
    public Bombsite? BombsiteAt(Vector3 world);                 // volume test only; no snapping
    public bool IsInside(Vector3 world, Bombsite site);
    public double FloorKeyFor(double z);                        // MapSpace.QuantizeZ(band.MinZ) for the band containing z
    public IReadOnlyList<int> Adjacent(int placeId);            // from Adjacency
    public bool AreAdjacent(int a, int b);
    public int? PlaceId(string name);                           // ordinal match on the raw name
    public IReadOnlyList<PlaceOutline> OutlinesFor(double floorKey);   // cached per floor, see §3.5
}
```

`ZoneAssetPipeline.TryLoad(bundleDir, overlayDir)` returns `PlaceResolver?`. It probes `zones.json`
then `zones.json.gz` in the bundle directory, never throws, reads nothing from `bundle.json` (D1),
then applies the user overlay for that map from `overlayDir` if one exists (§3.8) and builds the
resolver over the **effective** `ZoneSet`. `LoadedMapAsset` gets a lazy `Zones` property backed by
that call so `EnsureMapAsset` (`Playback2DTabViewModel.cs:1975`) needs no new wiring; consumers
outside playback (the library indexer for Grenade Index) call
`ZoneAssetPipeline.TryLoad(MapAssetBundleReader.FindBundleDirectory(map), AppPaths.ZonesDirectory)`
directly. `ZoneSet.EffectiveVersion` is the version every consumer stores (§3.6).

Cost: the grid build is one pass over 2000 to 4000 areas; `Resolve` is a handful of AABB and plane
tests plus one grid cell scan. Measured in the scratch probe without a grid, a brute-force resolve
over 3040 areas ran 36 000 samples in well under a second; with the grid the per-call cost is
microseconds, which matters because the Grenade Index and Click To Tag Position call it in loops.

### 3.5 Zone outlines: `playback2d.zones`

A new `ISceneLayer`, id `playback2d.zones`, `LayerSlot.Overlay`, opt-in and off by default (the
same class as `playback2d.annotations`), `LayerCacheHint.Static` keyed on `(zonesVersion, floorKey)`.
It draws, per pane, the boundary edges of each place on that pane's floor and a label at the
place's area-weighted centroid. Boundary edges are area polygon edges with no `areaLinks` partner
of the same place, so no polygon union is computed anywhere. `PlaceOutline` is
`(PlaceId, IReadOnlyList<(Vector2 A, Vector2 B)> Edges, Vector2 LabelAt)`.

Registration touches `SceneLayerIds.cs`, `SceneLayerCatalog.SceneStackIds`, and every list
`SceneLayerListParityTests` asserts against. It renders through `dv2d render --layers zones` for
the golden it will need. Hover to highlight a place and click to select are Query Canvas
behaviours, not this layer's.

### 3.6 Persistence and versioning

This item stores nothing per demo. The rule for consumers that cache a resolved place (the Grenade
Index rows, Click To Tag Position instances, the Query Canvas's saved queries) is: store the raw
place name string, the **effective** zones version it was resolved under (`ZoneSet.EffectiveVersion`,
§3.8: the baked `zonesVersion` alone when no overlay exists), and the frame-clock `clock` header of
`annotations-format.md:24-25`. A consumer whose stored version differs from the loaded set
re-resolves from the stored world point; it never trusts the old name. A user editing their zone
overlay therefore re-labels every cached place on the next read, without a re-bake and without
touching the demo. The Round Index does not carry a zones version by default because its token
comes from the pawn (F1), not from this resolver; §3.8 and Z-1 say what changes if it opts in.

### 3.8 Custom zones and callouts: the user overlay

**Requirement (owner, review):** a technical user must be able to define their own zones and
callouts, because teams do not share callouts. The baked `zones.json` is the default for every
shipped map; overriding it is a supported feature, not a hack.

**Two layers, two items.** Names over the canonical places are **Callout Aliases** (Strat Model
design, per owner per map: "popdog" for `Ramp`): they change what the UI says, not where a place
is. This section is the other layer, **geometry**: a place that does not exist in Valve's volumes
(a team's "Default", "E-box", "Sandwich"), a split of a Valve place into two, or a merge of several
into one. Both layers can be used together, and a custom zone's name is itself a callout, so a
team that defines its own zones rarely needs aliases for them.

**Where it lives.** `<config>/zones/<map>.zones.json`, one file per map, in the user config root
beside `rules/` and `themes/`, loaded the way `ThemeRegistry.LoadUserThemes` loads themes
(`plugin-system-design.md` §1.3): tolerate a missing directory, skip a malformed file with a
diagnostic rather than failing, never let the overlay silently shadow the bundle when it fails to
load. `AppPaths` gains `ZonesDirectory` and `EnsureZonesDirectory()` on the pattern of
`EnsureThemesDirectory()`. Team scoping (a file per team rather than per install) is decision Z-2.

**The file.** An overlay, not a replacement: it says what differs from the baked set it was
written against.

```jsonc
{
  "schemaVersion": 1,
  "mapName": "de_mirage",
  "basedOn": "9f1c02aa",                 // the baked zonesVersion the author looked at; a mismatch warns, never rejects
  "defaults": { "floorQuantum": 64 },
  "zones": [
    {                                     // a new zone: a polygon on one floor, optionally a Z band
      "name": "E-box",                    // becomes a place; the name is the callout
      "floor": -528,                      // a bundle floor key (MapSpace.QuantizeZ of the band's minZ)
      "polygon": [ -1560, -420,  -1380, -420,  -1380, -190,  -1560, -190 ],   // world XY, flat, simple polygon
      "minZ": -528, "maxZ": -300,         // optional; default is the floor band
      "replaces": false                   // false: the zone only claims points inside it (default); true: also removes every baked place it fully covers
    },
    { "name": "Default", "floor": -528, "polygon": [ ... ] },
    {                                     // split: a new zone carved out of a baked place, by geometry
      "name": "Stairs Top", "floor": -528, "polygon": [ ... ] }
  ],
  "merges": [                             // several baked places become one custom place
    { "into": "Site", "from": ["BombsiteA", "Stairs", "Firebox"] }
  ],
  "hidden": ["Scaffolding"]               // baked places removed from the vocabulary; their areas are re-flooded from neighbours
}
```

**How it applies (at load, in `ZoneAssetPipeline`, in this order):**

1. Parse and validate: unknown floor key, non-simple polygon, name colliding with a baked place
   without `replaces: true`, or a merge naming an unknown place, are each a diagnostic; the entry
   is skipped, the rest applies. The diagnostics reach the Rule Workbench's diagnostics surface
   (the same place user-tier ruleset errors are shown) so the author sees what was ignored.
2. `hidden` and `merges` first: hidden places lose their areas (re-flooded from neighbours over
   `areaLinks`, the §3.3 rule) and their volumes; merged places become one place id with the
   union of their areas and volumes and the new name.
3. `zones` next: each becomes a place with one prism volume (`polygon` × `[minZ, maxZ]`), stored
   as planes exactly like a baked hull so the resolver has one code path; every nav area whose
   centroid lies in the prism is reassigned to it (2 000 to 4 000 point-in-polygon tests, once).
   With `replaces: true`, any baked place whose every area was reassigned is removed.
4. Precedence in `PlaceResolver.Resolve`: **user volumes first**, then baked volumes, then nearest
   area, so a custom zone always wins inside its own polygon and never affects anything outside it.
5. `EffectiveVersion = CRC32(zonesVersion ‖ overlay bytes)`; `Places` is the effective vocabulary
   (baked minus hidden and merged, plus custom), and every place carries `Origin: Baked | Custom`.

**What custom zones change, and what they cannot.** Every consumer of the resolver sees the
effective set: a click on the Query Canvas, a grenade landing point, Click To Tag Position, the
outline layer (custom zones draw in a distinct style so the author can check them against the
radar), Callout Aliases' vocabulary, and the Dossier's heatmap labels. What a custom zone cannot
change is the pawn's `m_szLastPlaceName`, which is Valve's own volume set and is what The Round
Index tokenises by default (F1). So a Situation Search over the pawn token still speaks Valve's
names even when the outlines show the team's. Closing that gap is The Round Index's decision, not
this item's: that design can offer an index mode that tokenises sampled positions through the
effective `PlaceResolver` instead of the pawn field, at the cost of carrying `EffectiveVersion` in
its fingerprint and re-indexing when the overlay changes. Z-1 records the recommendation.

**Authoring in the first release.** A JSON file and the outline layer. The author draws nothing in
the app: they read world coordinates off the entity panel or a playback marker, write the polygon,
save, and use "Reload zones" (a command on the Playback2D tab beside the layer toggle; the file is
also re-read on the next map load) to see the outline. A graphical zone editor is a natural
follow-on once Step Authoring ships its Shape Tools (a polygon tool over the same canvas is the
same code), and is recorded there, not here. `dv2d render --layers zones --zones-overlay <file>`
gives a headless check and the golden.

**Sharing.** The file is plain JSON under the user's config root, so a team shares it the way it
shares rulesets and themes: copy the file. It is never written into `assets/`.

### 3.7 Browser host

`zones.json` is read from the bundle directory beside the executable, which the browser head does
not have (`wasm-matrix.md:117`). `ZoneAssetPipeline.TryLoad` returns null there; `PlaceResolver` is
absent; every consumer takes its documented fallback: the Query Canvas snaps to the index's sampled
place centroids (plan.md:275-278), the Grenade Index leaves the landing place empty, the Tolerance
Slider offers exact match only, the outline layer is not registered. `wasm-matrix.md` gets a row:
"Zone data | absent | no asset directory on the browser host; every consumer degrades to its no-zones
path; shipping `zones.json` as a web asset is the same open option as the radar set." Fetching it
over HTTP is not part of this item.

## 4. Alternatives considered and why not

| Alternative | Why not |
|---|---|
| Take place names from the `.nav` file | CS2 nav v36 carries none (§2.5 Q1). Not a VRF gap; the data is not there. |
| Derive place polygons from demos: cluster `m_szLastPlaceName` samples by position | Needs demos for every map before any resolver exists; boundaries are wherever players happened to walk; nothing for a bombsite volume. The Query Canvas keeps this as its no-zones fallback (plan.md:277), which is the right place for it. |
| Volumes only, no nav join | 88.5 to 98.8 percent agreement alone (§7), and 6 to 24 percent of nav areas on nuke, vertigo and mirage are outside every volume. The nav join lifts those maps by 3 to 5 points and gives outlines and adjacency, which volumes cannot. |
| Nav flood only, no volume test | Volumes are the game's own rule and win on dust2 (98.8 vs 97.8). The cascade is one extra AABB pass. |
| Polygon union per place (Clipper2 in the baker) | Adds a package to produce what boundary-edge extraction produces for free from `areaLinks`. Revisit only if a consumer needs a filled region as one path. |
| Put the zone fields inside `bundle.json` | The engine owns that DTO; the app cannot read a new field through it without an engine release (§2.2). A sibling file keeps the app in control of its schema and lets `--zones` top up committed bundles without touching `mapVersion`. |
| Add a `zones` reference to `bundle.json` and bump its `schemaVersion` (the first draft) | Nothing would read it: the top-up mode cannot write it, so the reader locates by path regardless (§3.4). A field with no reader is the `collisionMesh` situation again. Dropped (D1). |
| Let users edit `assets/<map>/zones.json` directly | A committed, baker-owned file; an edit is lost on the next bake and cannot be shared without shipping. The overlay in the config root (§3.8) is the pattern every other user customisation already uses. |
| Bake custom zones with the baker (a `--zones --overlay` mode) | Puts a user's callouts through a tool that needs the CS2 install and VRF; the overlay applies at load in milliseconds and needs neither. |
| Add the resolver to CS2DemoKit so rules can say `pos_in_place(...)` | The plan's rule: engine changes go upstream against the DLLs (F16), and nothing upstream is in flight for nav. App-side first; §5 records the upstream option. |
| Read bombsite bounds from the demo (`CBombTarget` is a `CBaseTrigger`) | Whether the entity is networked into GOTV demos is unverified, `CCSPlace` is server-only regardless, and a per-demo source would give the Query Canvas nothing before a demo is open. |
| Upgrade VRF to 20.0.6980 for the bake | Nothing needed is missing in 19.2; a bump changes `BakerVersion` and every bundle's provenance for no gain. |

## 5. External and engine changes required

**None required.** The bake reads CS2 data VRF already decodes, the file is app-owned, and the
engine's bundle reader tolerates both the schema bump and the additive field (measured, §2.2).

Two optional proposals, recorded so they are not assumed:

- **Upstream proposal (CS2DemoKit, later):** a `MapAssetBundleReader.TryReadSidecar<T>` or a
  documented statement that `bundle.json` unknown fields are stable. Not needed for this item.
- **Upstream proposal (CS2DemoKit rules v2, only if D6 in plan.md §6 is decided towards YAML
  detectors):** a place-lookup provider so a rule can evaluate a grenade or kill position against
  places. It would consume this file format. Out of scope here.

No change to the CSVG game plugin. No change to VRF.

## 6. Risks and unknowns

1. **Overlapping place volumes.** Mirage has 91 nav areas inside two or more volumes (CTSpawn over
   SnipersNest and Jungle; BombsiteA over Stairs), overpass 287. The game's precedence rule is not
   known. Measured tie rules on the January 2025 mirage demo: first-in-lump 91.3 percent nav
   agreement, last-in-lump 88.9, smallest volume 91.3, largest 88.9; the combined cascade moves
   between 92.4 and 93.6. The confusions are exactly the overlaps (`CTSpawn -> SnipersNest` 508
   samples, `Stairs -> BombsiteA` 219). Mitigation: decision D4 picks a default; the validation test
   reports per-place agreement so a per-map override can be justified from data if needed.
2. **`m_szLastPlaceName` is sticky.** It is the last place the pawn was inside, so a pawn on an
   unassigned or boundary area keeps the previous name while the resolver reports the nearest area.
   Some of the residual 3 to 7 percent is this, not resolver error. The validation method (§7)
   reports it as a stated miss rate rather than pretending to separate the two.
3. **Map updates.** A CS2 update that moves a volume or regenerates the nav changes `zonesVersion`
   but not `mapVersion` unless the nav bytes also changed. Consumers that store `zonesVersion`
   (§3.6) re-resolve; the outline layer's cache key includes it.
4. **Rotated volumes.** Only one rotated volume was seen on the ten maps (a buyzone). The baker
   applies `angles` anyway and logs every non-zero rotation so a future map with a rotated place
   volume is visible in the diag output rather than wrong.
5. **Mesh-shaped volumes.** Every place and bombsite volume on the ten maps is convex hulls. A
   future map with a physics mesh volume is logged and skipped by the baker; the nav flood still
   covers it if any other volume touches the same place. A mesh-to-prism fallback is easy to add
   when a map needs it.
6. **Floor key drift.** If a floor rebuild ever changes a band's `MinZ` by 64 or more, the key in
   `zones.json` and the annotation anchors move together only if both are re-derived from the same
   bundle. The file carries `bundleMapVersion` so the pipeline can warn when the two disagree.
7. **File size in the publish.** About 0.3 to 0.5 MB per map plain JSON, ten maps; D3 decides gzip.
8. **Unverified sources.** Only Valve matchmaking demos were measured (builds 10231 and 10896).
   FACEIT, HLTV and POV demos carry the same pawn field, but that is an assumption until Place Names
   From The Pawn reports.
9. **Two vocabularies on screen.** With an overlay loaded, outlines and clicks speak the team's
   names while the pawn-token index speaks Valve's (§3.8). Mitigation: the effective set records
   `Origin` per place, the UI labels custom places distinctly, and Z-1 gives The Round Index the
   option to tokenise through the resolver so both agree.
10. **Overlay drift.** A CS2 update re-bakes `zones.json`; a polygon written against the old volumes
    still applies (it is world geometry), but a `merges` or `hidden` entry naming a place Valve
    renamed becomes a diagnostic. `basedOn` lets the loader say "written against an older bake".

## 7. Test and verification strategy

### 7.1 Baseline measurement (done, scratch probe)

Method: for every `PositionSample` with a non-empty `Place` on every 32nd frame of a demo
(`PositionSampler.Walk(demo, 32, demo.Frames.Count)`), resolve the sample position and compare with
the pawn's place. Demos are Valve matchmaking recordings from the Steam `replays` folder, untrimmed.

| Demo (replays folder) | Build | Map | Samples with a place | Volumes only | Nav flood only | Cascade (volumes, then nearest area) |
|---|---|---|---|---|---|---|
| `match730_003731893271710924851_1024675027_129.dem` (Jan 2025) | 10231 | de_nuke | 35 941 | 91.2 % | 95.7 % | **96.7 %** (319 unresolved) |
| `match730_003732533638449856642_0094712188_122.dem` (Jan 2025) | 10231 | de_mirage | 36 595 | 88.5 % | 91.3 % | **92.4 %** (0 unresolved) |
| `match730_003842182368957825245_0056633905_389.dem` (Sep 2026) | 10896 | de_nuke | 27 109 | 92.0 % | 96.3 % | **97.2 %** (88 unresolved) |
| `match730_003844252717140672725_0377894676_389.dem` (Sep 2026) | 10896 | de_dust2 | 25 446 | 98.8 % | 97.8 % | **99.6 %** (0 unresolved) |
| `match730_003842442070597828830_0022157567_392.dem` (Sep 2026) | 10896 | de_inferno | 45 517 | 91.4 % | 94.1 % | **97.2 %** (174 unresolved) |
| `match730_003842233788306292960_0260929275_408.dem` (Sep 2026) | 10896 | de_mirage | 27 747 | 89.9 % | 92.2 % | **93.2 %** (0 unresolved) |

The pawn field is populated on 99.7 to 100 percent of samples. The stated miss rate of the cascade
is therefore **0.4 percent (dust2) to 7.6 percent (mirage)** against the pawn's own label, before
any overlap tuning, on builds nineteen months apart. Nuke's residual is concentrated on Rafters,
Silo and Heaven (unresolved or neighbour confusion on the stacked upper storey); mirage's on the
overlaps in risk 1.

### 7.2 Tests to ship with the build

| Test | Where | What it pins |
|---|---|---|
| `ZonesBakeTests` | `tools/DemoViewer.NET.AssetBaker` (new test project, or `--diag` self-check) | plane sign convention via the centroid check; every place and bombsite volume has at least one hull; each map has exactly two bombsites with designations 0 and 1; coverage table matches §2.5 within a tolerance |
| `ZoneSetReaderTests` | `src/Playback2D/DemoViewer.NET.Playback2D.Tests` | synthetic `zones.json` round-trips; missing file, `.gz`, malformed file all return null; `floor` equals `MapSpace.QuantizeZ(minZ)` for every band |
| `PlaceResolverTests` | same | a point inside a hull resolves `Volume`; a point 100 units off any area on the right floor resolves `NearestArea`; 200 units off resolves `None`; `ResolveOnFloor` ignores areas of another floor; `BombsiteAt` never snaps; adjacency is symmetric |
| `ZoneOutlineLayerTests` and a `dv2d` golden | same, plus `assets/tour` fixture | boundary edges have no partner of the same place; the layer is deterministic under `SceneDeterminismTests` |
| `ZoneResolverAgreementTests` | `src/App/DemoViewer.NET.App.Tests`, skipped unless `DEMO_PATH` is set | re-runs §7.1 through the real reader and resolver over every demo under `DEMO_PATH` whose map has a `zones.json`, asserts cascade agreement at or above a per-map floor (nuke 96, mirage 92, dust2 99, inferno 96, others 90 until measured), and writes the per-place table to the test output |
| `SceneLayerListParityTests` | existing | the new id appears in every list |
| `ZoneOverlayTests` | `src/Playback2D/DemoViewer.NET.Playback2D.Tests` | a synthetic overlay over a synthetic baked set: a custom polygon wins inside itself and changes nothing outside; `hidden` re-floods areas from neighbours; `merges` yields one place with the union; `replaces: true` removes a fully covered baked place and `false` leaves it; a malformed entry is skipped with a diagnostic and the rest applies; `EffectiveVersion` changes on any byte of the overlay and equals `zonesVersion` without one; `Origin` is `Custom` for custom places |
| `ZoneOverlayLoadTests` | `src/App/DemoViewer.NET.App.Tests` | a missing `zones/` directory loads the baked set; an unreadable file loads the baked set and reports; `Reload zones` picks up an edited file without a map reload |
| overlay golden | `dv2d render --layers zones --zones-overlay` | custom zones render in the distinct style and the baked outline is unchanged outside them |

The tour sample (`assets/tour/sample-de_nuke.dem`) is used only as a rendering fixture for the
outline golden, never as evidence of agreement.

### 7.3 Manual check

Open a nuke demo, enable the zones layer, step to a player, read the pawn's place in the entity
panel, compare with the outline under the marker on both storeys.

## 8. Decisions for the owner, and the answers (2026-09-23)

| # | Decision | Answer | Where it lands |
|---|---|---|---|
| D1 | Bump `bundle.json` `schemaVersion` to 2 with a `zones` reference? | Owner: bump only if the bundle schema actually changes; left to this design to decide. **Decided: no change to `bundle.json`, no bump.** The reference had no reader (§3.4 ignores it because the top-up mode cannot write it), user overlays never touch the bundle (§3.8), and `bundleMapVersion` inside `zones.json` already detects drift. | §3.2, §3.3, §4 |
| D2 | Where the resolver lives | `Playback2D.Core/Zones` plus a Pipeline reader, as recommended. | §3.4 |
| D3 | Plain or gzipped | Plain, as recommended; the reader accepts both. | §3.2 |
| D4 | Overlap tie rule | First volume in entity-lump order, with a per-map override table if validation shows a map where another rule wins, as recommended. | §3.3, §6 |
| D5 | `func_buyzone` and `bombRadius` | Include, as recommended. | §3.2 |
| D6 | Ship `areaLinks` | Ship, as recommended. | §3.2 |
| D7 | Per-map agreement floors | Measured minus one point, as recommended. | §7.2 |

Two decisions the Custom Zones addition (§3.8) raises, with recommended answers:

| # | Decision | Recommendation |
|---|---|---|
| Z-1 | Should The Round Index be able to tokenise through the effective `PlaceResolver` instead of the pawn field, so Situation Search speaks the team's callouts when an overlay exists? | Yes, as an **opt-in index mode** designed in The Round Index (it owns the fingerprint): default stays the pawn token (F1, no re-index on overlay edits); the resolver mode carries `EffectiveVersion` in the index fingerprint and re-indexes when the overlay changes. Recorded here so The Round Index review sees it. |
| Z-2 | Overlay scope: one file per map per install (`<config>/zones/<map>.zones.json`), or per team (under the Strat Model's per-owner books)? | Per install for the first release; it is the pattern rules and themes use, and a team shares it by copying the file. Per-team scoping can layer on later by letting the Strat Model's owner book point at an overlay file, without changing the file format. |

## 9. Effort estimate and sequencing

| Step | Work | Estimate |
|---|---|---|
| 1 | `Zones.cs` in the baker, `--zones` mode, full-bake hook, `BakerVersion` bump, diag output | 1.5 days |
| 2 | Bake all ten maps with `--zones`; commit `zones.json` files; inspect coverage tables | 0.5 day |
| 3 | `ZoneSet`, `PlaceResolver` with grid index, `ZoneAssetPipeline`, `LoadedMapAsset.Zones` | 1.5 days |
| 4 | `ZoneResolverAgreementTests` over `DEMO_PATH`; tune D4 if mirage is under its floor | 0.5 day |
| 5 | `playback2d.zones` layer, catalog registration, parity tests, `dv2d` golden | 1 day |
| 6 | `wasm-matrix.md` row, this document's §2.5 tables promoted to the baker's docs | 0.5 day |
| 7 | Overlay: schema and validation, `AppPaths.ZonesDirectory`, apply-at-load in `ZoneAssetPipeline`, `Origin` and `EffectiveVersion`, "Reload zones" command, distinct outline style, `--zones-overlay` on `dv2d`, the two overlay test classes and the golden, a documented example file for one map | 1.5 days |

About seven days. Steps 1 and 2 unblock nothing until step 3 lands; steps 3 and 4 unblock
Query Canvas click-to-place, Grenade Index landing place, Tolerance Slider adjacency and Click To
Tag Position; step 5 is independent of 4. Zone Baking runs in parallel with Phase 1 per plan.md
§4 and none of it sits on Situation Search's critical path (F1).

## 10. Sources

- `docs/strat-room/plan.md` at d90ec9f: §0, F1 (line 61), F2 (72), F15 (180), F17 (191), the Zone
  Baking entry (255), Query Canvas (275), Tolerance Slider (293), Click To Tag Position (331),
  Callout Aliases (369), Grenade Index (417), §6 decisions.
- `tools/DemoViewer.NET.AssetBaker/Program.cs`, `NavFloors.cs`, `Bundle.cs`, `CollisionMesh.cs`,
  `DemoViewer.NET.AssetBaker.csproj`; `assets/de_nuke/bundle.json`, `assets/de_ancient/bundle.json`.
- `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Assets/MapAssetPipeline.cs`;
  `src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapSpace.cs`, `MapLevel.cs`,
  `FloorSplitter.cs`; `Core/Compositing/ISceneLayer.cs`; `Core/Layers/SceneLayerIds.cs`;
  `Pipeline/Headless/SceneLayerCatalog.cs`; `Core/Input/DrawTool.cs`.
- `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs`, `Playback2DModule.cs`,
  `Scene2DHost.cs`; `Modules/Highlights/HighlightsModule.cs`; `Services/CollisionSoup.cs`;
  `Services/DemoCache/DemoCacheStore.cs`, `DemoCacheModels.cs`;
  `src/App/DemoViewer.NET.App.Tests/FloorAssetConsumptionTests.cs`.
- `docs/playback2d-v2/annotations-format.md` (clock block lines 24-25, world anchor 69-71);
  `docs/playback2d-v2/wasm-matrix.md` (line 117); `docs/playback2d-v2/design.md` and
  `docs/plugins/plugin-system-design.md` for form.
- `rules/highlights_position.rules.yaml:36-53`.
- CS2DemoKit 0.12.0 XML docs (`~/.nuget/packages/cs2demokit.parser/0.12.0`,
  `cs2demokit.analysis/0.12.0`): `PositionSampler.Walk(demo, frameStride, maxFrames)`,
  `PositionSample.Place`, `MapAssetBundleReader`, `MapAssetBundle`, `CollisionAssetLocator`.
- ValveResourceFormat 19.2.6339 (`~/.nuget/packages/valveresourceformat/19.2.6339`), reflected:
  `NavMeshArea`, `NavMeshFile`, `EntityLump.Entity` (a `KVObject`), `Hull.GetPlanes`,
  `Model.GetEmbeddedPhys`; VRF `master` `NavMeshFile.cs` and `NavMeshArea.cs` (GitHub raw,
  2026-09-23); nuget.org lists 19.2.6339 and 20.0.6980.
- CS2OpenDev docs, build 25218825: `schemas/server/CCSPlace.md` (derives
  `CServerOnlyModelEntity`), `CBombTarget.md` (derives `CBaseTrigger`), `CBaseTrigger.md`.
- Scratch probe: `scratchpad/zone-probe` (`Program.cs`, `Zones.cs`; modes `nav`, `kv`, `ents`,
  `reflect`, `vol`, `bundle`, `validate [demo] [stride] [sweep]`), run against the installed CS2
  map vpks and the six replays named in §7.1. Not committed; re-creatable from this document.
