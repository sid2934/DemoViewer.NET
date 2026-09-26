---
name: assetbaker-run-windows
description: How to run the macOS-pinned AssetBaker on Windows, stage cs2-assets, and what "baked" does not include
metadata: 
  node_type: memory
  type: project
  originSessionId: 7146d909-a634-4bb8-b9a3-e8facfa747bf
  modified: 2026-09-12T04:03:14.678Z
---

The AssetBaker (`tools/DemoViewer.NET.AssetBaker`) reads the gitignored `cs2-assets/` dev cache and writes the **committed, shipped** `assets/<map>/{bundle.json, <map>.png(+_lower), collision.tris}` at the repo root. `scripts/publish.sh` copies `assets/` next to the exe; the app probes it via `MapAssetLoader` / `CollisionAssetLocator`. `cs2-assets/` is the INPUT, `assets/` is the OUTPUT.

**Running on Windows** (the csproj hardcodes `osx-arm64` + `SkiaSharp.NativeAssets.macOS` for the Mac author): override the RID at the CLI, no csproj edit needed:
`dotnet run -c Release -r win-x64 -- de_dust2 de_nuke [--diag]`

**Staging `cs2-assets/`** (needed only for the full 2D bake; baker reads `radar/overviews/<map>.txt`, `radar/<map>_radar_psd.vtex_c` (+`_lower`), `maps/<map>.vpk`):
- `cs2-assets/maps` → NTFS **junction** to `<CS2>/game/csgo/maps` (New-Item -ItemType Junction, no admin, no 250MB/map copy). Map vpk holds `maps/<map>.nav` + `maps/<map>/world_physics.vmdl_c`.
- Radar/overview live inside `<CS2>/game/csgo/pak01_dir.vpk`: overview at `resource/overviews/<map>.txt`, radar image at **`panorama/images/overheadmaps/<map>_radar_psd.vtex_c`** (NOT under resource/overviews). Extract loose with ValvePak (`Package.Read` → `FindEntry` → `ReadEntry`).
- CS2 default install: `C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive`.

**A bundle.json does NOT imply a collision.tris, and the reason is not uniform.** Until 2026-09-11 four of the nine shipped maps had no line-of-sight geometry, and the tidy explanation ("the 0.1 baker had no collision step") is WRONG: the 0.1 baker did have one, and six of the nine bundles it wrote carry a `collisionMesh`. It was skipped on mirage, inferno and anubis specifically. de_cache kept a reference to a soup whose file was never committed, and the map changed under that reference (1,622,923 triangles recorded vs 1,632,062 today). de_train had never been baked at all and was not in the baker's default map list.

Consequences worth remembering:
- **Two consumers resolve a soup differently, and they can disagree.** Stats and the analysis run use `CollisionAssetLocator`, which globs `assets/<map>/collision.tris`. The Playback2D vision overlay uses `MapAssetPipeline.CollisionTrisPath`, which reads `Bundle.CollisionMesh` and returns null when it is absent. Dropping a soup file in without regenerating the bundle fixes Stats and leaves the overlay dark.
- A missing soup is silent: it zeroes 17 stat columns. See [[sight-columns-no-data]].
- Checking coverage: `ls assets/*/collision.tris` AND each bundle's `collisionMesh.triangleCount` against the file, not the presence of `assets/<map>/`.
- **Re-baking changes `mapVersion`** (collision bytes enter the CRC), and `dv2d golden verify` refuses to diff a golden whose manifest `map_version` disagrees with the bundle, reporting `stale-assets`. The guard fires BEFORE the update branch, so `golden update` cannot clear it by design. Restamp `tests/fixtures/playback2d/manifest.json` and the scene fixtures by string replacement (a JSON round-trip escapes the en dashes and churns the diff), then re-run verify so it actually compares pixels.
- **Still uncovered and unfixable here:** de_dogtown is gone from the game (only econ map tokens remain in pak01), and six maps the install ships (de_boulder, de_debris, de_eldorado, de_fachwerk, de_poseidon, cs_shelter) have no overview or radar in pak01 or in their own vpk, so they cannot be baked from an install at all. cs_italy and cs_office are bakeable but deliberately excluded as casual maps (italy alone is 45 MiB of soup).

**Baking soups only** needs just the per-map vpk, no `cs2-assets/` staging: the `--collision` mode reads the CS2 install directly the way `--icons` does (`SteamLibrary.FindCs2MapsDir`). Note as of 2026-09-11 that mode sits uncommitted in the working tree because `Program.cs` there also carries the in-flight icon workstream, which `SteamLibrary.cs` belongs to. It also does NOT update bundle.json, so prefer a full bake when the bundle must be correct, which is whenever Playback2D matters.

**Staging is cheap now.** `cs2-assets/maps` as a junction plus a few loose files pulled from pak01 with ValvePak (`resource/overviews/<map>.txt`, `panorama/images/overheadmaps/<map>_radar_psd.vtex_c` and its `_lower` variant) is all a full bake needs. A re-bake of an unchanged map reproduces the radar PNG and the soup byte-identically, so a full re-bake is safe to reach for.

**NavFloors footprint-overlap gate** (added 2026-07-17): the Z-histogram clusterer split de_ancient into 2 floors spuriously — its low terrain (z≈-190) and main floor (z≈+30) are bimodal with the SAME 224u peak separation and valley depth as nuke's real upper/lower split, so peak-separation/valley-depth gates can't distinguish them. Fix: only accept a boundary if the bands above/below share XY footprint (rasterize area bboxes to a 64u grid; require ∩/smaller ≥ 0.25). Measured: ancient 0.05 → drop; vertigo 0.59, nuke 0.97 → keep. A stacked storey overlaps horizontally; a sloped single floor doesn't. Only nuke+vertigo are 2-floor (they also get `_lower` radars). See [[cs2-docs]].
