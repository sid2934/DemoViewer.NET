# CS2 (Source 2) Map Assets — Research Reference

> **Status:** Background research / feasibility — *not* an implementation plan. Compiled 2026-06.
> **Goal:** Understand the format and packaging of Counter-Strike 2 (Source 2 engine) map assets so DemoViewer.NET can eventually (a) replace its grid background with a real top-down map, (b) draw FOV cones with occlusion, and (c) compute visibility-based statistics.
>
> **How to read this doc:** Every load-bearing factual claim carries an inline source URL. Claims that could not be pinned to a primary source are tagged **`[UNCERTAIN]`**; reasoned-but-uncited arguments are tagged **`[INFERENCE]`**. The strongest evidence throughout is the **ValveResourceFormat (VRF)** source code — it is an executable reverse-engineered parser, not prose. Valve Developer Community (VDC) pages sit behind an anti-bot wall and were read via search index / Wayback snapshots; those are flagged where they are the only source.

---

## Executive summary (read this first)

- **The single biggest enabler is ValveResourceFormat (VRF / Source 2 Viewer), and yes — VRF-from-.NET is viable.** It ships on **NuGet as `ValveResourceFormat`** (MIT, `net10.0`), is cross-platform and **headless** for the library + CLI (only the WinForms GUI is Windows-only), reads every map asset type we care about (vpk, `vmap_c`, `vwrld_c`/`vwnod_c`, `vmdl_c`, `vmat_c`, `vtex_c`, `vphys_c`, CS2 `.nav`, `vvis_c`), and exports geometry to **glTF** and textures to **PNG on CPU** (SkiaSharp). Our project is already `net10.0` and already pulls SkiaSharp via Avalonia, so the hard prerequisites are met. ([VRF NuGet](https://www.nuget.org/packages/ValveResourceFormat), [read-resource guide](https://s2v.app/ValveResourceFormat/guides/read-resource.html))
- **Cheapest first step that unblocks a real map background:** parse the shipped **radar overview `.txt`** (already in our `cs2-opendocs` mirror) for the `pos_x / pos_y / scale` affine transform, and load the shipped **radar image** (`materials/overviews/<map>*.vtex_c`). This is exactly what awpy / SimpleRadar / boltobserv do. The overview-txt's `verticalsections` (AltitudeMin/Max) maps **directly** onto the entity fields we already consume (`m_MinimapVerticalSectionHeights`, `m_vMinimapMins/Maxs`).
- **Map background:** *cheap* = shipped radar image + overview txt; *expensive* = orthographic top-down render of the extracted world mesh (full control, re-sliceable per floor — the "RadGen approach").
- **FOV cone + occlusion:** *cheap* = a naive angle+range wedge (universal prior art, no occlusion); *expensive* = an occlusion-clipped 2D visibility polygon (well-known geometry, but **essentially unclaimed in the CS-radar space — novel work**).
- **Visibility stats ("was B visible to A at tick T"):** *moderate-to-expensive*. **"Visible" is decided (Guiding Principle 2): player A's recomputed 3D line-of-sight, not the networked `spotted` bit.** There is **no usable baked PVS shortcut** in CS2 — you must raycast the player's eye against extracted **collision** triangles (`vphys`). This is proven prior art (awpy's `VisibilityChecker`: BVH + Möller-Trumbore). Doing it **3D and floor-correct** is essential (Guiding Principle 1); naive 2D top-down occlusion is wrong on multi-level maps (Nuke/Vertigo). The novel work for us is hitbox/eye-height anchoring, not the raycaster.
- **Top open questions:** (1) confirm SkiaSharp native assets resolve **on macOS** for VRF's headless PNG path (only Linux native-assets were observed); (2) confirm the exact in-vpk path of per-map geometry + `.nav` (per-map vpk vs `pak01_dir`) against a real CS2 install; (3) nail the world→radar Y-sign and `zoom`/`rotate`/`inset_*` handling for *our* renderer; (4) decide runtime-extraction (safe) vs shipping extracted assets (redistribution risk).

---

## Guiding principles (design constraints — apply to every feature in this doc)

These are binding on any map-asset / visibility / FOV work that follows. They constrain *how* features are built, independent of the format details below.

1. **3D-native analysis; 2D is only a projection.** All analysis — positions, geometry, FOV, occlusion, visibility — is computed in the game's **native 3D world-coordinate system**. The top-down "2D Playback" view is *purely a render-time projection*: apply the world→screen transform (and the per-floor Z slice) **only at draw time**, never to the underlying analysis. No calculation is performed "in 2D." This is *why* naive 2D top-down occlusion is wrong on multi-level maps (§5/§6) — and the principle generalizes that to **every** feature: a wall, a sightline, a player position, a cone are all reasoned about in 3D and flattened last. (The module keeps the name "2D Playback" as a label for its current view — the name does **not** imply 2D analysis.)

2. **Engine-fidelity — reproduce the player's real in-game experience.** Model each calculation the way the engine/server/client would; where the exact implementation is unknown, choose the option that most closely matches what the player actually experienced in-game. **Decided application — what "visible" means:** an enemy is "visible" to player A (for FOV cones, time-to-damage, spotting-quality stats) iff the enemy lay within A's actual **line of sight** — a **3D eye→hitbox ray clear of the map's collision geometry**, recomputed from the assets (§6). We do **not** use the demo's networked `spotted` bit / radar dorito: it lags 0–500 ms and is a known-unreliable proxy for "could see" — Leetify recomputes from geometry for exactly this reason (§6.4). The reference frame is the **player's perception (client line-of-sight)**, not an internal server summary.

3. **Map/asset-independence — no name/id special-casing.** Every tool/component must work for *any* map or asset of a given type, driven generically by the asset **data**. **No code branches on a map's or asset's name or id** to behave materially differently — no `if (map == "de_nuke")`, no per-map hardcoded extents / floor thresholds / radar offsets / occlusion tweaks. Reading per-map **data** — a map's `overview.txt`, its per-map `vpk`, its networked `m_vMinimapMins/Maxs` / `m_MinimapVerticalSectionHeights` — is the *correct* way to stay map-agnostic (data-driven ≠ name-keyed). Special-case handling is added **only** after a concrete, first-hand problem proves it necessary. *(Already in force: the floor split is a generic density-valley algorithm over observed player Z, not a per-map table; the radar world↔pixel transform comes from each map's `overview.txt`, not a code branch.)*

---

## 0. Quick orientation — what lives where (settled on disk)

Our repo already contains an **extracted `pak01_dir.vpk` tree** at `cs2-opendocs/data/game/csgo/pak01_dir/` (a curated subset — text/KV resources only; binary `_c` files and `.nav` are not mirrored). Two facts were settled directly against it:

- **The radar overview `.txt` lives in `pak01_dir.vpk`.** Confirmed: `cs2-opendocs/data/game/csgo/pak01_dir/resource/overviews/de_dust2.txt` (and ~20 maps) exist on disk. These descriptors are *shared* resources.
- **Map geometry does not live in `pak01_dir`.** No `*.vmap_c`, `*.vwrld_c`, `*.vphys_c`, `*.vvis_c`, or `*.nav` exists anywhere in the mirror; `pak01_dir/maps/` contains only metadata (`*_camera_nodes.kv3`, `*_retake.txt`). Per the tool docs, per-map compiled geometry + the `.nav` ship in a **per-map vpk** (`game/csgo/maps/<map>.vpk` for official maps, or `csgo_community_addons/<map>/<map>_dir.vpk` for workshop maps). ([cs-demo-manager — maps guide](https://cs-demo-manager.com/docs/guides/maps))

> **Reconciliation note (resolving a contradiction found during research):** Two sources both citing cs-demo-manager appeared to conflict — one said "official maps are *only* in `pak01_dir`," the other said ".nav is in the per-map vpk." The correct picture is **both, for different assets**: *shared* assets (radar overview `.txt`, radar material/texture, prefabs) are in `pak01_dir.vpk`; *per-map geometry* (`vmap_c`, `vwrld_c`, `vphys_c`, `vvis_c`, `.nav`) is in the per-map vpk. The on-disk mirror is consistent with this. **`[UNCERTAIN]`** — we did not enumerate a real `de_dust2.vpk` from a live CS2 install; the per-map-vpk claim rests on tool docs, not first-hand listing.

**Actionable for us:** to draw a *cheap* radar background, you only need `pak01_dir.vpk` (overview txt + radar texture). To extract *geometry* for rendering / occlusion / visibility, you must open the **per-map vpk**.

---

## 1. CS2 map asset formats & packaging

### 1.1 Where assets live on disk

- Install root is still named `Counter-Strike Global Offensive`, content under `game/csgo/` — a CS:GO legacy that survived the CS2 rebrand. ([cs-demo-manager](https://cs-demo-manager.com/docs/guides/maps))
- **Shared assets:** `<Steam>/steamapps/common/Counter-Strike Global Offensive/game/csgo/pak01_dir.vpk` + numbered data archives (`pak01_000.vpk`, …). Holds `resource/overviews/<map>.txt`, radar materials/textures, prefabs, scripts, panorama UI.
- **Per-map geometry:** official maps → `game/csgo/maps/<map>.vpk`; workshop/community maps → `game/csgo/csgo_community_addons/<map>/<map>_dir.vpk`. ([cs-demo-manager](https://cs-demo-manager.com/docs/guides/maps), corroborated [fpaezf/cs2-vpk-map-unpacker](https://github.com/fpaezf/cs2-vpk-map-unpacker))
- Inside a map vpk, dependency files sit under `maps/<mapname>/`: `vwrld`, `vwnod`, `vvis`, `vphys`, `vents`, `vtex`, `vmdl`, `vrman`. ([VDC: VMAP](https://developer.valvesoftware.com/wiki/VMAP) — search excerpt only, page behind anti-bot wall)

### 1.2 The VPK container format

Read by **ValvePak** (`SteamDatabase.ValvePak`, MIT). The container itself is **uncompressed** — payload compression is *inside* the `_c` files (§1.3). ([ValvePak](https://github.com/SteamDatabase/ValvePak))

- **Split architecture:** open the **directory file** `pak01_dir.vpk` (the index/tree); numbered `pak01_000.vpk`… are pure data blobs. ([ValvePak README](https://github.com/SteamDatabase/ValvePak))
- **Header** (`Package.Read.cs`): `MAGIC = 0x55AA1234` (u32); `Version` (1, 2, or `0x00030002` for Respawn/Apex — *not supported*); `TreeSize` (u32). Version 2 adds four u32s: `FileDataSectionSize`, `ArchiveMD5SectionSize`, `OtherMD5SectionSize`, `SignatureSectionSize`. ([Package.Read.cs](https://raw.githubusercontent.com/SteamDatabase/ValvePak/master/ValvePak/ValvePak/Package.Read.cs), [Package.cs](https://raw.githubusercontent.com/SteamDatabase/ValvePak/master/ValvePak/ValvePak/Package.cs))
- **Directory tree:** three nested null-terminated-UTF8 loops → **extension → directory → filename**. Per entry: `CRC32` (u32), preload bytes (u16), `ArchiveIndex` (u16), `Offset` (u32), `Length` (u32), `0xFFFF` terminator. `ArchiveIndex == 0x7FFF` means the data is embedded in the `_dir.vpk` itself. ([Package.Read.cs](https://raw.githubusercontent.com/SteamDatabase/ValvePak/master/ValvePak/ValvePak/Package.Read.cs), [PackageEntry.cs](https://raw.githubusercontent.com/SteamDatabase/ValvePak/master/ValvePak/ValvePak/PackageEntry.cs))
- Version-2 footer adds per-chunk MD5s, a whole-file checksum, and an optional RSA signature; per-file CRC32 is validated on read.

### 1.3 Source 2 compiled resource (`_c`) types

**Container model (critical):** every `_c` file is a generic Source 2 *Resource* = header + a list of typed **blocks**. Block FourCCs: `RERL` (external reference list — *how a resource points at others*), `REDI`/`RED2` (edit info / dependency metadata), `NTRO` (introspection manifest), `DATA` (primary payload), plus type-specific blocks `VBIB` (vertex/index buffers), `PHYS`, `VXVS` (voxel visibility), `MVTX/MIDX/MBUF/MDAT/MRPH` (mesh/morph), etc. Blocks are frequently **LZ4-compressed**. ([BlockType.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/Enums/BlockType.cs), [ResourceType.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/Enums/ResourceType.cs))

| Resource | Ext | What it is | Notes / source |
|---|---|---|---|
| **Map** | `vmap_c` | Compiled Hammer map **root / manifest** | VRF's `Map.Read()` is a no-op — *"Maps have no data."* Real content is reached via its **RERL** external-ref list (e.g. `dota.vmap_c` lists 72 references to vmdl/etc.). ([Map.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/Map.cs), [RERL fixture](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/Tests/Files/ValidOutput/dota.vmap_c/RERL.txt)) |
| **World** | `vwrld_c` | "World root file" — entity lumps, lighting, and the **`m_worldNodes`** list | ([World.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/World.cs)) |
| **WorldNode** | `vwnod_c` | **Scene graph / static geometry layout** — `m_sceneObjects`, **`m_aggregateSceneObjects`** (baked/merged world geometry), `m_clutterSceneObjects` | The Source 2 spatial-partition unit (replaces BSP leaves; see §2). ([WorldNode.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/WorldNode.cs)) |
| **Model** | `vmdl_c` | Compiled model (skeleton, hitboxes, attachments, mesh refs); geometry in VBIB/MVTX/MIDX | ([Model.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/Model.cs)) |
| **Material** | `vmat_c` | `ShaderName` + Int/Float/Vector/**Texture** param dicts (TextureParams → vtex paths) | ([Material.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/Material.cs)) |
| **Texture** | `vtex_c` | Compiled texture. Formats: DXT1/5, BC6H, BC7, RGBA8888, ATI1N/2N, ETC2…; mip data **LZ4**-packed | ([Texture.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/Texture.cs)) |
| **PhysicsCollisionMesh** | `vphys_c` | **Rubikon** collision. `m_parts` → shapes: **Hull** (convex), **Mesh** (triangles + BVH), Sphere, Capsule | The geometry to raycast for LOS. ([PhysAggregateData.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/PhysAggregateData.cs), [Shapes API](https://s2v.app/ValveResourceFormat/api/ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.html)) |
| **WorldVisibility** | `vvis_c` | **Voxel-cluster PVS** (block `VXVS`, schema `CVoxelVisibility`) — see §1.4 | ([VoxelVisibility.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/Blocks/VoxelVisibility.cs)) |
| **EntityLump** | `vents_c` | Map entities — including `env_cs_place` callouts | ([EntityLump.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/ResourceTypes/EntityLump.cs)) |
| **(Nav mesh)** | `.nav` | **NOT a `_c` resource** — legacy binary, magic `0xFEEDFACE`, CS2 version 30–36 | See §1.5. ([NavMeshFile API](https://s2v.app/ValveResourceFormat/api/ValveResourceFormat.NavMesh.NavMeshFile.html)) |

### 1.4 Does CS2 ship a PVS / visibility structure? — carefully verified

**Yes — but it is voxel-cluster-based, not a Source 1 BSP-leaf PVS, and it is not usable as a shortcut for precise "A sees B" stats.**

- Format: `[Extension("vvis")] WorldVisibility` — *"World visibility data stored in voxel clusters."* The data block `VXVS` ("Voxel Visibility") is parsed by VRF `VoxelVisibility.cs` against the **CS2-namespaced** schema `CVoxelVisibility`: `BaseClusterCount`, **`PVSBytesPerCluster`** (VRF literally calls it "PVS bytes per cluster"), an octree (`MinBounds`/`MaxBounds`/`GridSize`, node leaf/child offsets), `SkyVisibilityCluster`/`SunVisibilityCluster`. It is a **per-cluster PVS bitset over a voxel octree** — the modern replacement for Source 1's leaf-based PVS. ([VoxelVisibility.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/Blocks/VoxelVisibility.cs), [ResourceType.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/Enums/ResourceType.cs))
- **CS2 ships it** (not a Dota-only inherited format): a real CS2 internal path was found — `maps/prefabs/misc/terrorist_team_intro_variant2/world_visibility.vvis_c`. ([CounterStrikeSharp #1092](https://github.com/roflmuffin/CounterStrikeSharp/issues/1092) — search excerpt)
- **Why it can't shortcut the visibility-stats problem `[INFERENCE]`:** it is a *conservative culling* structure at voxel granularity (designed to over-include, to avoid render pop-in / wrongly-culled networked entities). It answers "could voxel X possibly see voxel Y," **not** "did eye-point A have an unobstructed line to hitbox-point B." VRF's exact `VoxelVisibility` bit-layout is reverse-engineered and partly incomplete (a "missing visibility region bit field" was recently noted) — treat its *precise contents* as **`[UNCERTAIN]`**, but the *architectural unsuitability for precise stats* holds regardless. It is, however, a sound **coarse pre-reject** to accelerate raycasting (§6). ([VRF](https://github.com/ValveResourceFormat/ValveResourceFormat))

### 1.5 The radar / minimap assets

**Two distinct radar assets exist — don't conflate them:**

1. **The in-game radar material + descriptor** (what awpy/SimpleRadar/boltobserv use). The descriptor `resource/overviews/<map>.txt` lives in `pak01_dir.vpk` (confirmed on disk). Its `material` key points at a texture under `materials/overviews/` (a compiled `vtex_c`). E.g. `de_dust2.txt` → `"material" "overviews/de_dust2_v2"`. The radar image is a plain `vtex_c` → extract to PNG via VRF's `Resource → Texture → GenerateBitmap → ToPngImage` path. ([on-disk `de_dust2.txt`]; VDC: [Mapname.txt](https://developer.valvesoftware.com/wiki/Mapname.txt))
2. **A Panorama overhead-UI radar** (`panorama/images/overheadmaps/<map>_radar.ctex_c`) reported by some sources. `.ctex_c` is **not** in VRF's `ResourceType` enum; the mapping to a `Texture`+`SpritesheetData` is **`[UNCERTAIN]`/[INFERENCE]`**. This path was **not present** in our mirror (no `overheadmaps/` dir). For our purposes the `materials/overviews/*.vtex_c` image (#1) is the canonical, confirmed radar source. ([cs-demo-manager](https://cs-demo-manager.com/docs/guides/maps))

> **Legacy-vs-CS2 caveat:** Older write-ups describe CS:GO's loose `resource/overviews/<map>_radar.dds`. In CS2 the descriptor `.txt` persists (in `pak01_dir`), but the image is a compiled `vtex_c` material, not a loose `.dds`. SVG (`.vsvg`) is used for *icons*, not the radar bitmap.

The full overview `.txt` format and the world→radar transform are detailed in **§4.2 / §4.3** (with on-disk data).

### 1.6 Compiled (`_c`) vs source (`.vmap`/`.vmdl`) — what ships, what's recoverable

- **Retail CS2 ships only compiled `_c` resources** + the `.nav`. Source `.vmap`/`.vmdl`/`.vmat`/`.vtex` (Hammer/ModelDoc authoring files) ship **only in the CS2 Workshop Tools SDK**, not the retail game.
- **Decompilation is possible but lossy.** VRF can decompile `vmap_c` → editable `.vmap` and export world/models to glTF, but it is reverse-engineered (no official docs) and not byte-perfect: documented losses include missing `func_viscluster`/`func_occluder`, tools-textures substituted, models merged by material, missing lightmap volumes. The compiler embeds source paths in REDI/RED2 `InputDependencies`, which VRF uses to reconstruct intended filenames. ([VDC: Decompiling Maps](https://developer.valvesoftware.com/wiki/Decompiling_Maps) — excerpt; [s2v.app exporting-maps](https://s2v.app/ValveResourceFormat/guides/exporting-maps.html); [Resource.cs](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/ValveResourceFormat/Resource/Resource.cs))

---

## 2. Disambiguating "wireframe" — the three geometry layers

CS2 maps carry **three distinct geometry layers**; "the map mesh" is ambiguous. Pick the right one per feature.

### 2.1 Confirmed: Source 2 geometry is MESH-based, not BSP brushes

Primary source, exact quote — the Source 2 Hammer docs have a section titled "Not Brushes":
> *"Hammer's geometry tools have undergone a significant overhaul. The usage of brushes is no longer the primary building block… instead it is all faces, edges, and vertices… Non-flat faces are valid and are **converted to triangles when compiled**."*
([VDC: Mesh Editing 1](https://developer.valvesoftware.com/wiki/Source_2/Docs/Level_Design/Basic_Construction/Mesh_Editing_1), via [Wayback snapshot](https://web.archive.org/web/20260122005903/https://developer.valvesoftware.com/wiki/Source_2/Docs/Level_Design/Basic_Construction/Mesh_Editing_1))

**Precise framing (don't overclaim):** what is gone is the *brush/CSG authoring model* and *BSP-leaf world geometry*. World geometry is now triangle meshes. Source 2 **still has a spatial-partition tree** for culling — the WorldNode (`vwnod`) format — so "no BSP" means "no brush planes / no CSG," **not** "no spatial tree."

**Implication for extraction:**
- *Source 1 / GoldSrc:* `.bsp` stores brushes as sets of cutting **planes**; you reconstruct polygons by CSG-intersecting half-spaces. An extractor must slice planes.
- *Source 2 / CS2:* you get **triangle meshes directly** from `vwrld`/`vmesh`/`vmdl` — "read vertex buffer + index buffer," exactly like a model. **No brush planes to slice.** This is materially simpler and is why a top-down render (§4b) and triangle raycasting (§6) are tractable. VRF exports these straight to glTF.

### 2.2 Render / visual mesh

The drawable geometry, in `vmdl`/`vmesh`/`vwrld`/`vwnod`/`vmap`. Most detailed of the three layers; carries materials/textures. All five formats are marked fully supported (👍) in VRF and export to glTF. Use this layer for **drawing** a faithful background. ([VRF README](https://raw.githubusercontent.com/ValveResourceFormat/ValveResourceFormat/master/README.md))

### 2.3 Physics / collision mesh (`vphys_c`)

Rubikon collision (`PhysAggregateData.Parts` → shapes). Two geometry flavors plus primitives:
- **`Hull`** = convex hull (`Face`/`HalfEdge`/`Plane`/`Region`) — simpler volumes, many props.
- **`Mesh`** = triangle mesh (`Triangle` + a `Node`/`NodeType` **BVH**) — full concave world surface.
- plus `Sphere`, `Capsule`.

This is the authoritative "where do bullets and bodies actually stop" surface — the correct layer for **occlusion / line-of-sight** and for a "walls" footprint (vs visual clutter). VRF can ray-trace against these hulls/meshes. ([PhysAggregateData API](https://s2v.app/ValveResourceFormat/api/ValveResourceFormat.ResourceTypes.PhysAggregateData.html), [Shapes API](https://s2v.app/ValveResourceFormat/api/ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.html))

### 2.4 Navigation mesh (`.nav`)

Walkable areas as flat convex polygons connected into a graph — the cleanest **2D footprint** (no ceilings, no decoration). CS2 uses the legacy `.nav` binary (magic `0xFEEDFACE`) but at **version 30–36** (CS2 maps observed at v35), a structural break from Source 1's ≤v16 (this version number is the key discriminator: old CS:GO parsers reject CS2 nav). VRF reads it natively via `NavMeshFile`. ([NavMeshFile API](https://s2v.app/ValveResourceFormat/api/ValveResourceFormat.NavMesh.NavMeshFile.html))

CS2 `.nav` structure (from VRF source + awpy's mirror):
- `Version` + new **`SubVersion`** (absent in Source 1); `Areas` (dict areaID→area), `Ladders`, `GenerationParams`, optional KV3 `CustomData`.
- **Corners are polygon-indexed (v31+):** a shared corner/vertex table + polygon table; each area stores a polygon index rather than 4 inline corners (Source 1 stored corners inline). ([NavMeshFile.cs], [NavMeshArea.cs])
- `NavArea`: `area_id`, `hull_index`, `dynamic_attribute_flags`, **`corners`** (polygon boundary), **`connections`** (per-corner links to adjacent areas), `ladders_above/below`. ([awpy nav docs](https://awpy.readthedocs.io/en/latest/modules/nav.html))
- **Callouts moved OUT of the nav in CS2** — place names are now `env_cs_place` entities in the entity lump, not embedded in the navmesh. ([hjb.dev](https://hjb.dev/posts/counter-strike-2-where-are-all-the-callouts-2))
- **`[UNCERTAIN]`** exact in-vpk path of the `.nav` (per-map vpk, under `maps/`), and the complete list of CS2 nav versions (v35 and v36 both attested; no authoritative changelog seen).

**CS2-capable nav readers:** VRF `NavMeshFile` (C#, authoritative), awpy (Python), `cs2-nav` (Rust). Do not use csgonavparse / gonav / sourcenav.lua — they parse Source 1 (≤v16) only and will reject CS2 nav.

---

## 3. Tooling landscape

### 3.1 ValveResourceFormat (VRF / Source 2 Viewer) — deep dive

> **Repo:** <https://github.com/ValveResourceFormat/ValveResourceFormat> · **License: MIT** (except `Tests/Files`, which excludes likely-Valve-origin fixtures) · **Very actively maintained** (push 2026-06-24; ~2.3k stars). The GitHub org was renamed from `SteamDatabase/ValveResourceFormat` (old URL redirects). Lead maintainer is xPaw/SteamDB by reputation; metadata shows only the org **`[UNCERTAIN]`** at the metadata level.

**As a .NET library — the key facts:**
- **NuGet `ValveResourceFormat`**, latest 19.2.6339 (2026-05-29), MIT. Description: "Valve's Source 2 resource file format parser, decompiler, and exporter." A separate **`ValveResourceFormat.Renderer`** package handles GPU rendering; the **core parse/decompile/export library does not need it**. VPK reading is the separate **`ValvePak`** package. ([VRF NuGet](https://www.nuget.org/packages/ValveResourceFormat), [ValvePak NuGet](https://www.nuget.org/packages/ValvePak))
- **Target framework: `net10.0` only** — a single TFM, no netstandard/net8 fallback. A consumer must be on `net10.0`. **Our project already is** — green light, but a hard floor. The csproj is also AOT-compatible (`IsAotCompatible`). ([VRF csproj](https://github.com/SteamDatabase/ValveResourceFormat/blob/master/ValveResourceFormat/ValveResourceFormat.csproj))
- **Cross-platform & headless:** the **library + CLI are fully cross-platform** (Win/macOS/Linux, x64+ARM64). Only the **WinForms GUI is Windows-only**. glTF export is pure-CPU (SharpGLTF, no render context). Texture→PNG is CPU via **SkiaSharp**; the csproj pulls `SkiaSharp.NativeAssets.Linux.NoDependencies`, confirming headless Linux. ([s2v.app](https://s2v.app/ValveResourceFormat/))
  - **`[UNCERTAIN]` / open question:** only the *Linux* SkiaSharp native-assets package was observed in the csproj. **Confirm SkiaSharp resolves on macOS** (our dev box is darwin). In practice Avalonia already brings `SkiaSharp` + macOS natives into our app, so this is likely satisfied — but verify in a spike.

**What it reads (per format, from the README support table — all 👍):** `vpk` (via ValvePak), `vmap`/`vmap_c`, `vmdl`/`vmdl_c`, `vmesh`, `vmat`/`vmat_c`, `vtex`/`vtex_c`, `vphys`/`vphys_c`, `vwrld`, `vwnod`, plus 60+ resource types. **CS2 `.nav`** is read by `ValveResourceFormat.NavMesh.NavMeshFile` (it's a non-resource file, so it bypasses the `Resource.Read` pipeline and isn't in the README table — absence ≠ unsupported). ([VRF README](https://github.com/SteamDatabase/ValveResourceFormat/blob/master/README.md), [NavMeshFile API](https://s2v.app/ValveResourceFormat/api/ValveResourceFormat.NavMesh.NavMeshFile.html))

**Export:**
- **glTF 2.0 (`gltf`/`glb`) only — there is no OBJ exporter.** Verified against the CLI source: it has zero `obj`/`ObjExporter` references; the only mesh exporter is `GltfModelExporter`, validated with `if (GltfExportFormat is not "gltf" and not "glb" and not null)`. (An old issue mused about `.obj`; it is not in the current API.) ([CLI/Decompiler.cs](https://raw.githubusercontent.com/SteamDatabase/ValveResourceFormat/master/CLI/Decompiler.cs))
- **`GltfModelExporter`** (`ValveResourceFormat.IO`): `Export(Resource, targetPath, CancellationToken)`; `CanExport` covers **Mesh, Model, EntityLump, PhysicsCollisionMesh, WorldNode, World, Map**, plus a standalone `Export(NavMeshFile, …)` overload (nav → glTF). Needs an **`IFileLoader`** to resolve referenced materials/textures. Config flags: `ExportMaterials`, `ExportAnimations`, `MeshFilter`, etc. GLB is capped at 2 GB — use `gltf` for large maps. ([GltfModelExporter.cs](https://raw.githubusercontent.com/SteamDatabase/ValveResourceFormat/master/ValveResourceFormat/IO/GltfModelExporter.cs))
- **Texture/radar → PNG, programmatically and headless** — the canonical path:
  ```csharp
  using var package = new Package();
  package.Read("pak01_dir.vpk");
  var entry = package.FindEntry("materials/overviews/de_dust2_v2.vtex_c");
  package.ReadEntry(entry, out var raw);
  using var resource = new Resource { FileName = entry.GetFullPath() };
  resource.Read(new MemoryStream(raw));
  var texture = (Texture)resource.DataBlock;
  using var bitmap = texture.GenerateBitmap();        // SkiaSharp, CPU
  File.WriteAllBytes("radar.png", TextureExtract.ToPngImage(bitmap));
  ```
  ([read-resource guide](https://s2v.app/ValveResourceFormat/guides/read-resource.html))

**Key namespaces/classes for us:** `SteamDatabase.ValvePak.Package` (`Read`/`FindEntry`/`ReadEntry`), `ValveResourceFormat.Resource` (`Read`/`DataBlock`/`ResourceType`), typed blocks `Texture`/`Model`/`Material`/`PhysAggregateData`, `ValveResourceFormat.IO.{GltfModelExporter, FileExtract}`, `TextureExtract`, `ValveResourceFormat.NavMesh.NavMeshFile`, `IFileLoader`.

### 3.2 The rest of the landscape

| Tool | Purpose | Lang / License | Maintenance | Notes |
|---|---|---|---|---|
| **SourceIO** | Blender import (VMDL/VMAP/VTEX/VMAT) | Python / MIT | Active (2026-06) | Reads `_c` containers + VPK. ([repo](https://github.com/REDxEYE/SourceIO)) |
| **RadGen** | CS2 radar generator (ortho top-down render) | closed freeware | active | "renderer is fully 3D… usually render top-down orthographic." Consumes the editable `.vmap` (mapper-side), not shipped `_c`. ([radargenerator.github.io](https://radargenerator.github.io/)) |
| **cs2-radar-extractor** | Extract radar from game files | Python / Unlicense | light (1.1.0) | Shells out to **VRF's Decompiler**; needs local CS2 install. ([repo](https://github.com/invakid404/cs2-radar-extractor)) |
| **awpy** | CS2 demo analysis (Python) | Python / MIT | Active (2026-06) | Doesn't bundle assets; `awpy get maps\|navs\|tris` downloads pre-extracted data from its own CDN. Parses CS2 `.nav` (v35) and has a 3D `VisibilityChecker`. Radar images = SimpleRadar art (used with permission). ([repo](https://github.com/pnxenopoulos/awpy), [nav](https://awpy.readthedocs.io/en/latest/modules/nav.html), [visibility](https://awpy.readthedocs.io/en/latest/examples/visibility.html)) |
| **SimpleRadar** | Hand-drawn replacement radars | proprietary/freemium | active | Original vector art redrawn from scratch — *not* extracted Valve assets. ([readtldr.gg](https://readtldr.gg/simpleradar)) |
| **boltobserv** | External observer radar | JS / GPL-3.0 | active (1.6.1) | Bundles SimpleRadar-derived overlays; doesn't extract from game files. ([repo](https://github.com/boltgolt/boltobserv)) |
| **cs2-phys-extractor** | `vphys` → triangle mesh in C# | C# (VRF+ValvePak) | — | Working .NET example emitting `.tri`/`.vphys`. ([repo](https://github.com/itzlaith/cs2-phys-extractor)) |
| **cs2-nav** | CS2 nav parser | Rust / MIT | active | pyo3 Python bindings; CS2-specific. ([crate](https://crates.io/crates/cs2-nav)) |
| **source2gen** / **cs2-dumper** | Schema dumpers (need game installed) | C++/Rust, Apache-2.0/MIT | active | For schema offsets, not map geometry. ([source2gen](https://github.com/neverlosecc/source2gen), [cs2-dumper](https://github.com/a2x/cs2-dumper)) |
| csgonavparse / gonav / sourcenav.lua | **Source 1 nav (≤v16) only** | various, AGPL/MIT | stale | Do not use for CS2. |

### 3.3 Canonical extraction path per asset type

| Asset | Canonical read | Canonical export | Tool |
|---|---|---|---|
| vpk | `Package.Read/FindEntry/ReadEntry` | extract loose | **ValvePak** |
| vmap_c / vwrld_c / vwnod_c | `Resource` → World/Map/WorldNode | glTF | **VRF** `GltfModelExporter` |
| vmdl_c / vmesh | `Resource` → Model/Mesh | glTF (no OBJ) | **VRF** |
| vmat_c | `Resource` → Material | resolved via `IFileLoader` during export | **VRF** |
| vtex_c (incl. radar) | `Resource` → Texture → `GenerateBitmap()` | **PNG** (`TextureExtract.ToPngImage`) | **VRF** |
| vphys_c | `Resource` → PhysAggregateData | glTF (or read triangles directly) | **VRF** |
| .nav | `NavMeshFile.Read` | structured Areas/Ladders, or glTF | **VRF** (C#); awpy/cs2-nav elsewhere |
| radar overview .txt | plain KeyValues text | — | parse ourselves (already mirrored) |

---

## 4. Auto-generating the 2D top-down map

### 4.1 Three approaches, compared

**(a) Use the shipped radar image + overview `.txt` — least work; what the ecosystem does.**
Two files: a radar **image** (`materials/overviews/<map>*.vtex_c`) + the **overview `.txt`** giving the affine transform (`pos_x`/`pos_y`/`scale`). The engine just blits the image and maps player coords onto it. awpy, SimpleRadar, and boltobserv all use this model (boltobserv/awpy actually substitute SimpleRadar's cleaner art). CS Demo Manager draws SVG markers over a decompiled radar image. *Tradeoff:* lowest effort, highest visual fidelity (hand-authored art), but you inherit the mapper's framing + only the floors they authored, and you can't re-slice at arbitrary Z. ([Mapname.txt](https://developer.valvesoftware.com/wiki/Mapname.txt), [boltobserv](https://github.com/boltgolt/boltobserv))

**(b) Orthographic top-down render of the extracted world mesh — full control.**
Extract `vwrld`/`vmesh`/`vmdl` → glTF via VRF, then render with an orthographic top-down camera. This is *how Valve/community radars are actually authored* — **RadGen** (CS2) and its predecessor **TAR** (CS:GO) do exactly this top-down ortho render. *Tradeoff:* maximal control, re-sliceable per floor, recolor by surface property — but most engineering (materials, flat-shading, slicing) is on us. **`[UNCERTAIN]`:** no turnkey open-source "shipped CS2 vpk → ortho radar PNG" pipeline exists (RadGen consumes the editable `.vmap`, not shipped `_c`); the VRF-glTF → render step would be custom. ([RadGen](https://radargenerator.github.io/), [TAR](https://developer.valvesoftware.com/wiki/TAR))

**(c) Project collision or nav geometry to a 2D footprint — cheap clean outline.**
- *Nav projection:* take `NavArea.corners`, drop Z, fill/outline → accurate walkable footprint, zero clutter. Read CS2 nav with VRF `NavMeshFile` (C#).
- *Collision projection:* `vphys` Hull/Mesh triangles → XY → "where are the walls/solids" silhouette.
*Tradeoff:* cheapest, inherently 2D-clean, but schematic (nav omits non-walkable-but-visible areas; collision includes prop clutter). Best as a programmatic base/overlay, not final art.

### 4.2 How CS2 itself generates the radar — and can we mirror it?

**Pre-rendered and authored by the mapper, not generated live.** The mapper runs a radar tool (RadGen / TAR) that does a top-down ortho render → the radar image (compiled to a `vtex_c`) + the overview metadata. The engine at runtime just blits + transforms coords. **We can mirror it two ways:** (1) reuse the shipped output (4a — easiest, what awpy/boltobserv do); (2) reproduce the process (4b — custom from shipped assets).

### 4.3 The overview `.txt` format + world→radar transform (grounded on-disk)

From `cs2-opendocs/data/game/csgo/pak01_dir/resource/overviews/de_dust2.txt` (verbatim on disk):
```
"de_dust2"
{
    "material"  "overviews/de_dust2_v2"   // texture file
    "pos_x"     "-2476"                    // upper left world coordinate
    "pos_y"     "3239"
    "scale"     "4.4"
    "rotate"    "1"
    "zoom"      "1.1"
    "inset_left/top/right/bottom" ...
    "CTSpawn_x/y" "TSpawn_x/y" "bombA_x/y" "bombB_x/y" ...  // fractional [0,1] loading-screen icons
}
```
- `material` = texture path under `materials/`. `pos_x`/`pos_y` = **upper-left world coordinate** (pos_y is top/max world-Y). `scale` = **world units per radar pixel**, referenced to the 1024×1024 image. `rotate`/`zoom`/`inset_*` optional.
- **World→radar transform** (resolved — the "two formulas" online differ only by output space):
  - **Pixel space [0,1024]:** `px = (x − pos_x) / scale` ; `py = (pos_y − y) / scale`
  - **Normalized [0,1]:** divide each by `scale × 1024`
  - Y is **inverted** (`pos_y − y`). **`[UNCERTAIN]`:** the Y-sign convention varies by tool — verify for *our* renderer. Some tools also apply `zoom`/`rotate`; our existing radar code already uses `pos_x/pos_y/scale`, so this slots in.
  ([Mapname.txt](https://developer.valvesoftware.com/wiki/Mapname.txt), [Valthrun #174](https://github.com/Valthrun/valthrun-cs2/issues/174), on-disk `.txt`)

### 4.4 Multi-floor / per-Z slicing — and how it maps onto OUR entity fields

From `de_nuke.txt` (verbatim on disk):
```
"verticalsections"
{
    "default" { "AltitudeMax" "10000"  "AltitudeMin" "-495"   }  // primary radar image
    "lower"   { "AltitudeMax" "-495"   "AltitudeMin" "-10000" }  // i.e. de_nuke_lower_radar.dds
}
```
- Each named section = a world-Z altitude band; the section name maps to a radar image (`"default"` → the `material`; `"lower"` → `<map>_lower_radar`). The game switches floors by the **observed player's world Z** falling within a band. Nuke splits at **Z = −495**; Vertigo at **Z = 11700**. Single-floor maps (dust2, mirage) omit `verticalsections`. ([on-disk `de_nuke.txt`/`de_vertigo.txt`])

> **Actionable for us:** these overview-txt `verticalsections` (AltitudeMin/Max) are the *authored radar* equivalent of the entity fields we already consume — **`m_MinimapVerticalSectionHeights`** (radar floor-switch sub-sections) and **`m_vMinimapMins/m_vMinimapMaxs`** (the world-space radar bounding box, ≈ `pos_x/pos_y` + extent). Both encode "which radar image / sub-section applies at this Z." For approach (4a) we can: read the overview txt for the image set + transform, and use the player's reconstructed Z (from cell coords) against the AltitudeMin/Max bands (cross-checked against `m_MinimapVerticalSectionHeights`) to pick the floor image. For a custom render (4b), render one ortho slice per band. **These sub-sections are radar floors, not real storeys** — consistent with our existing understanding.

---

## 5. FOV cone + occlusion (future feature)

### 5.1 What existing tools draw: a naive wedge (no occlusion)

Verified at source level — CS radar/replay tools draw a fixed-angle arc/triangle, **not** an occlusion-clipped polygon:
- **csgoverview** draws ±20/10/5° arcs via SDL2_gfx `ArcColor` — pure wedge, no wall clipping. ([draw.go](https://github.com/Linus4/csgoverview/blob/master/draw.go))
- **healeycodes' browser renderer** draws a direction triangle, explicitly "no occlusion handling, visibility polygons, or view cones." ([healeycodes](https://healeycodes.com/rendering-counter-strike-demos-in-the-browser))
- Hosted 2D viewers (cs2.cam, scope.gg, CS Demo Manager) show positions/trajectories — none advertise occlusion-clipped cones.

**Verdict: the occlusion-clipped FOV cone on a CS radar is essentially unclaimed — it would be novel work.** The naive wedge is universal prior art.

**Inputs (all available from CS2 demos):** eye position (pawn origin + view-offset) + view yaw + FOV half-angle + range. We already track `m_angEyeAngles`; eye-offset Z is networked via `m_vecViewOffset`. For a top-down cone, pitch is irrelevant — use (x,y) eye + yaw. CS2 default `fov` is 90 (locked without `sv_cheats`) → ≈106° horizontal on 16:9 via Hor+ scaling. **`[UNCERTAIN]`:** the 106° figure is community-derived (`H = 2·atan(tan(V/2)·aspect)`), not a Valve constant — a stylized ~90–106° is the defensible choice. ([demoinfocs player.go](https://github.com/markus-wa/demoinfocs-golang/blob/master/pkg/demoinfocs/common/player.go), [fov commands](https://totalcsgo.com/commands/categories/fov-and-viewmodel))

### 5.2 The occlusion-clipped cone (novel, but standard geometry)

Compute a 2D **visibility polygon** via angular/rotational sweep: cast rays at every wall-segment endpoint (±epsilon to catch corners), sort by polar angle, maintain a distance-ordered active set, emit polygon vertices when the nearest segment changes — **Θ(n log n)**. Restrict to the FOV wedge by clipping (Sutherland–Hodgman) or by seeding the sweep with the two cone-edge rays over `[yaw−h, yaw+h]`. ([Red Blob Games: 2D Visibility](https://www.redblobgames.com/articles/visibility/), [Sight & Light](https://ncase.me/sight-and-light/), [trylock/visibility](https://github.com/trylock/visibility), [CGAL Rotational_sweep_visibility_2](https://doc.cgal.org/latest/Visibility_2/classCGAL_1_1Rotational__sweep__visibility__2.html))

The 2D wall segments come from slicing the **collision** mesh at the player's eye-Z (per-floor). **This is a *picture*, not the *truth*** — see §5.3 for why the visibility-stats path is different.

### 5.3 The multi-floor / height problem — why naive 2D occlusion is wrong

`[INFERENCE]` (first-principles; external sources don't cover this for CS specifically): a single-Z 2D slice collapses all geometry onto one plane. On **multi-level maps (Nuke stacked sites, Vertigo, ramps)** this both **over-occludes** (a wall on floor 1, once collapsed, blocks a floor-2 sightline it doesn't physically block) and **under-occludes** (a floor/ceiling separating two levels — which *does* block vertical LOS — vanishes in a horizontal slice). Per-floor slicing only works if you can cleanly partition floors and assign each player to one, which is unreliable across ramps/overlaps.

**Resolution (this is Guiding Principle 1 in concrete form — 3D analysis, 2D only for the picture):** use 2D visibility polygons **only for the radar cone *visualization*** (§5.2). For correct visibility *statistics* (§6), raycast in **full 3D** against the collision triangles — a 3D eye→target segment is floor-correct by construction (a Nuke-roof triangle simply isn't on the segment between two same-floor players). Same map, two tools: 2D polygon for the picture, 3D raycast for the truth.

**No PVS shortcut:** the `vvis_c` voxel structure (§1.4) is conservative culling, not a per-point LOS oracle — you must raycast.

---

## 6. Visibility-based statistics ("was enemy B visible to player A at tick T")

### 6.1 The proven approach (awpy `VisibilityChecker`)

The canonical open-source CS2 implementation, verified from source ([awpy visibility.py](https://github.com/pnxenopoulos/awpy/blob/main/awpy/visibility.py)):
- **Algorithm:** segment-vs-triangle via **Möller-Trumbore**; if the eye→target segment hits no triangle, the two points are mutually visible.
- **Acceleration:** a **custom BVH** (binary tree, AABB nodes, split on largest centroid spread) — no external lib. `is_visible(start, end) -> bool`.
- **Geometry source:** CS2 collision triangles from `world_physics.vmdl_c` → `vphys`, extracted via VRF (awpy ships `.tri` files, ~20 MB across all maps; `awpy get tris`). **Implication for us:** VRF is .NET, so collision extraction is native — **no Python bridge needed**. A working C# example is `cs2-phys-extractor`. ([awpy visibility example](https://awpy.readthedocs.io/en/latest/examples/visibility.html), [cs2-phys-extractor](https://github.com/itzlaith/cs2-phys-extractor))
- **Crucially, awpy is point-to-point only** — no hitbox/eye-height handling; the caller supplies the two 3D points.

### 6.2 The novel work for us: hitbox / eye-height anchoring

Because the raycaster is point-to-point, *correctness lives in choosing the two points*:
- **Attacker eye:** pawn origin + networked view-offset (`m_vecViewOffset`; ≈ +64 standing / ~46 crouched if the offset isn't exposed). **`[UNCERTAIN]`** whether our parser currently surfaces the view-offset Z.
- **Target:** a single eye→center ray over/under-counts (head visible but center blocked, or vice versa). Robust approach: raycast eye→**multiple hitbox anchors** (head/chest/pelvis or a few bbox samples) and report visible **if any** ray is clear. This multi-anchor approximation is the concrete novel work — awpy doesn't provide it.

### 6.3 Performance shape

Using awpy's measured per-ray costs (~177 µs clear / ~65 µs blocked; one-time BVH build 0.7–9.6 s/map):
- **Ticks:** we consume **GOTV** demos (default `Cs2GotvProfile`), typically ~32 fps. A ~40-min match ≈ **~77k ticks** (~150k at 64-tick). **`[UNCERTAIN]`** exact per-demo rate.
- **Pairs:** directed attacker→enemy among living players, ≤ 5×5 = **25 ordered pairs**, shrinking as players die.
- **Brute force:** ~77k × 25 × ~0.1 ms ≈ **~190 s/match** (a few minutes), skewed cheap because most pairs are wall-blocked. Multi-anchor (×3–5 rays/pair) and 64-tick push this to tens of minutes → needs reduction.
- **Levers (mostly proven):** (1) compute only on/around significant events (shots, kills) — Leetify-style; (2) downsample ticks; (3) distance-cull + living-pairs-only; (4) use the coarse `vvis` voxel structure as a cheap conservative **pre-reject** before the triangle raycast; (5) the BVH (already in awpy) is the main per-ray accelerator.

### 6.4 Prior art

- **Leetify** explicitly **recomputes spotting from geometry rather than trusting the demo's `spotted` bit** (the radar bit lags 0–500 ms, requires specific body-part/angle visibility, and leaves a stale "shadow"). On a significant event they check whether the attacker can see "any part of the enemy's body or gun," feeding **Time to Damage / Spotted Accuracy / Crosshair Placement**. Internals (raycast backend, hitbox handling) are **closed-source `[UNCERTAIN]`**. *Validates our architecture: don't use the spotted-bit; recompute from geometry.* ([Leetify: enemy actually spotted](https://leetify.com/blog/enemy-actually-spotted/), [stats glossary](https://leetify.com/blog/leetify-stats-glossary/))
- **CS:GO / Source nav-mesh visibility precompute (primary source).** `CNavArea` carried **precomputed area→area visibility**: `enum VisibilityType { NOT_VISIBLE, POTENTIALLY_VISIBLE, COMPLETELY_VISIBLE }`; `m_potentiallyVisibleAreas`; `ComputeVisibilityToMesh()` ran offline LOS traces at compile time; `IsPotentiallyVisible()` was "very fast," `IsPartiallyVisible(eye)` did live, "CPU intensive" traces; storage shrunk via `m_inheritVisibilityFrom`. ([source-sdk-2013 nav_area.h](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/nav_area.h)) — **But this does NOT carry to CS2:** awpy's CS2 nav parser stores **no visibility data**. The CS:GO nav-area precompute is, however, a sound *acceleration pattern to re-implement*: precompute coarse area→area visibility once, raycast only for area-pairs flagged potentially-visible.
- **Smokes/flashes (out of scope, flagged):** dynamic occluders not in static collision geometry. A static-geometry raycast reports a smoked-off enemy as "visible." Correct handling needs per-tick smoke-volume tracking — a known correctness gap, deliberately deferred.

---

## 7. Legal / licensing

> Not legal advice — a research summary of the documents and community practice.

- **Steam Subscriber Agreement (governing doc; no separate CS2 EULA found).** §2.A grants a license "for your **personal, non-commercial use**" (content is licensed, not sold). §2.G prohibits, without Valve's prior written consent, reproducing/distributing/reverse-engineering/creating derivative works, and "transfer[ring] reproductions of the Content… to other parties." **Net:** personal/research use is licensed; **redistribution** of extracted assets and reverse engineering are restricted. The literal text also forbids the reverse engineering that the whole tool ecosystem (VRF, awpy) does — community practice diverges from the strict letter. ([SSA](https://store.steampowered.com/subscriber_agreement/))
- **VRF's stance:** MIT, "except `Tests/Files`… which contains files which have likely come from Valve's games" — it deliberately excludes Valve-origin files from its license grant, and behaviorally only **reads** the user's own VPKs (no bundled Valve assets beyond test fixtures). No explicit "extract from your own install" disclaimer was found — the stance is implicit. ([VRF README/LICENSE](https://github.com/ValveResourceFormat/ValveResourceFormat/blob/master/README.md))
- **awpy / SimpleRadar / boltobserv — distinct postures:**
  - **SimpleRadar** distributes its **own original redrawn art**, not extracted Valve assets, and frames itself as a config-style game-file mod. ([readtldr.gg](https://readtldr.gg/simpleradar))
  - **awpy** has *two* streams: radar images = **SimpleRadar art used with permission** (credited in the 1.x PyPI description); nav/map data = **extracted from CS2 by the maintainers and redistributed via their own CDN** (users don't need CS2 installed). The second stream **is** redistribution of Valve-origin extracted data — real-world precedent, but it carries the §2.G risk that runtime-extraction avoids. ([awpy 1.1.4 PyPI](https://pypi.org/project/awpy/1.1.4/), [awpy setup](https://deepwiki.com/pnxenopoulos/awpy/1.1-installation-and-setup))
  - **boltobserv** bundles SimpleRadar-derived art, doesn't extract from game files. ([repo](https://github.com/boltgolt/boltobserv))
- **The design decision for us:**
  - **Runtime-extract from the user's own CS2 install** (what VRF does) is the **conservative** posture — the user only touches files already licensed to them; the app redistributes nothing Valve-origin.
  - **Shipping extracted Valve assets** directly implicates §2.G's no-redistribution clause (awpy does this for nav/map data — popular, but riskier).
- **Enforcement precedent:** no documented takedown of VRF or any CS asset-extraction tool (open for years). The clearest Valve enforcement is **Classic Offensive** — a cease-and-desist over distributing "derivative content," shut down May 2025 — which targets a *derivative game*, not an analysis/extraction library, but shows Valve enforces against **redistribution of derivative content**. ([esports.gg](https://esports.gg/news/counter-strike-2/classic-offensive-project-shut-down-as-valve-issue-cease-and-desist/)) **`[UNCERTAIN]`** broader generalization from one case.
- **Recommendation:** design for **runtime extraction from the user's own install**; do not ship extracted Valve geometry/textures. If we ever bake a derived footprint (e.g. nav-projected outlines), treat its redistributability separately and conservatively.

---

## 8. Recommendation + phased feasibility

### 8.1 The most promising path (we're .NET)

**Use ValveResourceFormat as an in-process NuGet library** (`net10.0`, MIT, headless, AOT-friendly). It is the single biggest enabler and covers every asset type we need with one dependency stack (VRF + ValvePak), avoiding any Python/external bridge. Extract at **runtime from the user's own CS2 install** (legal-conservative).

### 8.2 Cheap vs expensive, per feature

| Feature | Cheap | Expensive |
|---|---|---|
| **Map background** | Shipped **radar `vtex_c` → PNG** + overview `.txt` transform (already mirrored). Floor switching via `verticalsections` AltitudeMin/Max ↔ our `m_MinimapVerticalSectionHeights`. | **Orthographic top-down render** of VRF-extracted world mesh — full control, custom per-floor slices (RadGen approach, but from shipped `_c` = custom). |
| **FOV / occlusion** | Naive **angle+range wedge** (universal prior art; uses data we already have). | **Occlusion-clipped 2D visibility polygon** from a per-floor collision slice (standard Θ(n log n) geometry, but novel in the CS-radar space). |
| **Visibility stats** | — (no genuinely cheap version; even the minimum needs collision geometry) | **3D eye→hitbox raycast** vs `vphys` triangles (BVH + Möller-Trumbore, awpy-proven); event-gated + voxel pre-reject for performance. Multi-anchor hitbox is our novel work. |

### 8.3 Smallest first step that unblocks a real map background

1. Parse the overview `.txt` (already at `cs2-opendocs/.../resource/overviews/<map>.txt`) → `pos_x/pos_y/scale` (+ `verticalsections`).
2. Add VRF (`ValveResourceFormat` + `ValvePak`) and extract the radar `vtex_c` (`materials/overviews/<map>*.vtex_c`) from the user's `pak01_dir.vpk` → PNG via `Texture.GenerateBitmap()` + `TextureExtract.ToPngImage`.
3. Swap the Avalonia grid for that PNG, transforming player world coords with `px=(x−pos_x)/scale`, `py=(pos_y−y)/scale`; pick the floor image via player-Z vs `verticalsections` (cross-check `m_MinimapVerticalSectionHeights`).

This needs **only `pak01_dir.vpk`** — no per-map geometry, no rendering — and reuses our existing radar-coordinate convention.

### 8.4 Top open questions for a follow-up spike

1. **macOS SkiaSharp natives** for VRF's headless `Texture.GenerateBitmap`/`ToPngImage` (only Linux native-assets were observed; Avalonia likely already satisfies this — verify on our darwin box). `[UNCERTAIN]`
2. **Exact in-vpk layout** of per-map geometry + `.nav` against a real CS2 install (per-map `maps/<map>.vpk`, internal paths) — the mirror omits them. `[UNCERTAIN]`
3. **World→radar specifics for our renderer:** Y-sign, and whether `zoom`/`rotate`/`inset_*` must be applied (tools vary). `[UNCERTAIN]`
4. **View-offset Z exposure** in our parser (`m_vecViewOffset`) for accurate eye position. `[UNCERTAIN]`
5. **CS2 `.nav` version coverage** in VRF (v35/v36) and whether we want nav at all (cheap footprint vs collision for occlusion). `[UNCERTAIN]`
6. **Multi-anchor hitbox model** for visibility stats (which anchors; how it compares to Leetify/awpy ground truth) — the actual novel work. `[UNCERTAIN]`

---

## Appendix: confidence & caveats

- **Highest confidence (first-hand code):** VRF source (`ResourceType.cs`, `BlockType.cs`, `VoxelVisibility.cs`, `World.cs`, `WorldNode.cs`, `Texture.cs`, `PhysAggregateData.cs`, `GltfModelExporter.cs`, `NavMeshFile.cs`, `CLI/Decompiler.cs`), ValvePak (`Package.Read.cs`), awpy (`visibility.py`, `vector.py`), source-sdk-2013 (`nav_area.h`), and the **on-disk overview `.txt` files** in our own mirror.
- **Lower confidence (search-index / single-source / behind anti-bot wall):** all `developer.valvesoftware.com` claims (VMAP/NAV/VTEX/Decompiling-Maps/Mesh-Editing — read via search excerpts or Wayback), the `.ctex_c` radar-texture mapping, per-map-vpk internal `.nav` path, CS2 nav full version list, the 106° horizontal-FOV figure, and Leetify's internals.
- **Explicitly carried-through `[UNCERTAIN]` items** (do not launder into confident prose): a *compiled* `vnav` resource for CS2 (evidence points to the legacy `.nav` binary, not a `_c`); the `.ctex_c` → `Texture`+spritesheet mapping; exact `vvis_c` bit-layout; CS2 nav version enumeration; the world→radar Y-sign per tool.
