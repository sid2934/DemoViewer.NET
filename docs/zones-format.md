# The `zones.json` map zones file and the `.zones.json` user overlay (schema v1)

DemoViewer resolves a world position to a named place (a callout) through two files: the baker's
`assets/<map>/zones.json`, which carries Valve's own place volumes joined to the nav mesh, and an
optional user overlay, `<config>/zones/<map>.zones.json`, which defines a team's own zones over that
default. This document is for anyone who wants to read the baked file or write an overlay. A committed
overlay lives at
[`tests/fixtures/playback2d/zones/zones-nuke-overlay.zones.json`](../tests/fixtures/playback2d/zones/zones-nuke-overlay.zones.json)
and is rendered by the `zones-nuke-overlay` golden.

## Where the files live

| File | Location | Written by |
|---|---|---|
| The baked set | `assets/<map>/zones.json` (or `zones.json.gz`), a sibling of `bundle.json` | `AssetBaker --zones` and the full bake. Never edited by hand; a bake overwrites it. |
| The user overlay | `<app config root>/zones/<map>.zones.json`, beside `rules/` and `themes/` | You. The app creates the `zones/` folder at startup and never writes into it. |
| Browser build | Nowhere. There is no asset directory and no config root, so no zones exist there. | |

`bundle.json` is not involved: the app locates `zones.json` by path, so a `--zones` top-up on an older
bundle needs no bundle change. `bundleMapVersion` inside the file says which bundle it was baked beside.

## The baked file

```jsonc
{
  "schemaVersion": 1,
  "mapName": "de_nuke",
  "bundleMapVersion": "075a27b3",        // the bundle.json mapVersion this was baked beside
  "zonesVersion": "29eb299a",            // CRC32 over the entity lump, every volume model and the nav bytes
  "bakerVersion": "0.4+vrf19.2.6339",
  "floorQuantum": 64,                    // the level quantum the floor keys were minted with
  "floors": [                            // the bundle's floor bands, low to high, with the key each mints
    { "key": -99968, "minZ": -100000, "maxZ": -528 },
    { "key": -512,   "minZ": -528,    "maxZ": 100000 }
  ],
  "places": [                            // index = place id; sorted by name, stable across re-bakes
    { "id": 0, "name": "Admin" },
    { "id": 1, "name": "BombsiteA" }
  ],
  "volumes": [                           // convex hulls, world space, entity origin already applied
    { "kind": "place",    "place": 1, "entity": "2:61817:49",
      "min": [502, -940, -416], "max": [874, -500, -320],
      "hulls": [ { "planes": [ [nx, ny, nz, d], ... ] } ] },
    { "kind": "bombsite", "site": "A", "entity": "2:47639", "min": [...], "max": [...], "hulls": [...] },
    { "kind": "buyzone",  "team": "CT", "entity": "...", "min": [...], "max": [...], "hulls": [...] }
  ],
  "areas": [                             // one per nav area
    { "id": 1234, "place": 1, "floor": -512, "seed": true,
      "z": -416.0, "xy": [x0, y0, x1, y1, x2, y2, x3, y3] }
  ],
  "areaLinks": [ [1234, 1235], ... ],    // undirected nav connections, deduplicated
  "adjacency": [ [0, 12], [1, 7], ... ], // undirected place pairs that share a nav connection
  "parameters": { "bombRadius": 650 }
}
```

* **`floors[].key`** is `MapSpace.QuantizeZ(minZ)`: `floor(minZ / 64 + 0.5) * 64`, half-up. It is the
  same value a `world` annotation anchor's `levelMinZ` carries (see
  [`playback2d-v2/annotations-format.md`](playback2d-v2/annotations-format.md)), never a floor *index*.
  An overlay names floors by this key.
* **`places[]`** are the raw `env_cs_place` names (`BombsiteA`, `TopofMid`, `HutRoof`). Nothing is
  renamed; display names are a separate concern (callout aliases).
* **`volumes[]`** store half-spaces, not vertices. A point is inside a hull when `n·p - d <= 0` for
  every plane; `min`/`max` is the world bounding box, the pre-reject. `kind` is `place`, `bombsite`
  (`site` is `A` or `B`, from `bomb_site_designation` 0 and 1), `buyzone` (`team` is `T` or `CT`) or
  `hostage_rescue`. Volume order is entity-lump order, and when two place volumes overlap the first wins.
* **`areas[]`**: `place` is `-1` for a nav island no volume reaches. `seed` says a volume assigned the
  area directly; the rest inherited the place of their nearest seeded neighbour by hop count over
  `areaLinks`. `z` is the mean corner Z; `floor` is the key of the band containing it.
* Coordinates are rounded to 0.1 units.

### How a point resolves

The app's `PlaceResolver` runs one cascade: the place volumes at the point and 32 units above it
(the game's own rule, exact); then the nearest nav area on the point's floor within 128 units, by
XY distance to the area polygon; then nothing. A click on a 2D pane has no Z, so `ResolveOnFloor`
probes only the volumes whose Z range meets that floor's band and only that floor's areas. The
bombsite test is the volume test alone and never snaps.

## The user overlay

An overlay is not a replacement: it says what differs from the baked set. Everything it does not
mention stays exactly as baked, and every entry it gets wrong is skipped on its own, with a report,
while the rest applies.

```jsonc
{
  "schemaVersion": 1,
  "mapName": "de_mirage",
  "basedOn": "9f1c02aa",                 // the baked zonesVersion you looked at; a mismatch warns, never rejects
  "defaults": { "floorQuantum": 64 },
  "zones": [
    {                                     // a new zone: a polygon on one floor, optionally a Z band
      "name": "E-box",                    // becomes a place; the name is the callout
      "floor": -528,                      // a floor key from the baked file's floors[]
      "polygon": [ -1560, -420,  -1380, -420,  -1380, -190,  -1560, -190 ],   // world XY, flat, a simple polygon
      "minZ": -528, "maxZ": -300,         // optional; default is the floor band
      "replaces": false                   // see below
    },
    { "name": "Default", "floor": -528, "polygon": [ ... ] }
  ],
  "merges": [                             // several baked places become one custom place
    { "into": "Site", "from": ["BombsiteA", "Stairs", "Firebox"] }
  ],
  "hidden": ["Scaffolding"]               // baked places removed from the vocabulary
}
```

### How it applies, in order

1. **`hidden`**: each named baked place loses its volumes and its areas; the areas re-flood from their
   neighbours over `areaLinks`, the same rule the bake uses, so nothing is left unnamed that the bake
   could name.
2. **`merges`**: the `from` places become one custom place called `into`, with the union of their
   areas and volumes. `into` may be one of the `from` names (keep the name, absorb the rest); it may
   not be the name of a place that is not a source.
3. **`zones`**, in order: each becomes a custom place with one vertical prism volume,
   `polygon × [minZ, maxZ]`, stored as planes exactly like a baked hull. Every nav area whose centroid
   lies in the prism is reassigned to it. With `replaces: true`, a baked place whose *every* area was
   taken is removed, and a baked place with the *same name* is hidden first so the name has one owner.
   With `replaces: false` (the default) the zone only claims points inside it and a baked namesake is
   an error.
4. **Precedence**: user volumes come first in the cascade, then the baked volumes, then the nearest
   area. A custom zone always wins inside its own polygon and never affects anything outside it.
5. **The effective set**: the vocabulary is the baked places minus hidden and merged ones, plus the
   custom ones, each carrying `Origin: Baked | Custom`; adjacency is rebuilt over the new assignment;
   and the version consumers store becomes `EffectiveVersion = CRC32(zonesVersion ‖ overlay bytes)`.
   Any byte of the overlay changes it, so a consumer that cached a place under the old version
   re-resolves from the stored world point on its next read. Without an overlay file the effective
   version is the baked `zonesVersion` itself.

### What is a diagnostic, and where it shows

| Entry | Code | What happens |
|---|---|---|
| The file is not JSON of this shape | `zones.overlay.malformed` | the baked set is used, the overlay is ignored entirely |
| The file cannot be read | `zones.overlay.unreadable` | same |
| `mapName` names another map | `zones.overlay.map-mismatch` | applied anyway; a warning |
| `basedOn` is not the loaded bake's `zonesVersion` | `zones.overlay.based-on` | applied anyway; polygons are world geometry and still hold |
| a zone with no `name`, or a merge with no `into` | `zones.overlay.empty-name` | the entry is skipped |
| `floor` is not one of the baked floor keys | `zones.overlay.unknown-floor` | the zone is skipped |
| `polygon` is not simple (fewer than three distinct corners, an odd count, a crossing edge, zero area) | `zones.overlay.bad-polygon` | the zone is skipped |
| `minZ` is not below `maxZ` | `zones.overlay.bad-band` | the zone is skipped |
| a zone or merge takes the name of a live place it may not | `zones.overlay.name-collision` | the entry is skipped |
| `hidden` or `from` names a place the bake does not have | `zones.overlay.unknown-place` | the name (or the whole merge) is skipped |

The rows appear in the **Rule Workbench**'s diagnostics list, under the ruleset problems, as soon as
the zones load: the same place a user-tier ruleset error is shown. Nothing is ever refused silently,
and a failed overlay never shadows the baked set.

### Authoring and reloading

There is no zone editor yet. Read world coordinates off the entity panel or a playback marker, write
the polygon, save the file, and press **Reload zones** on the 2D Playback tab (beside the **Zones**
toggle) to see the outline without reloading the map; the file is also re-read on the next map load.
Custom zones draw in a distinct dashed style so you can check the polygon against the radar. Headless:
`dv2d render --fixture <scene> --assets assets --layers radar,zones --zones-overlay <file>`
([`playback2d-v2/dv2d.md`](playback2d-v2/dv2d.md)).

Sharing is copying the file: it is plain JSON under your config root, like a ruleset or a theme. It is
never written into `assets/`.

## What an overlay cannot change

The pawn's own `m_szLastPlaceName`, which is Valve's volume set, is what the round index tokenises by
default. Outlines, clicks, grenade landing places and everything else that goes through the resolver
speak the effective vocabulary; a search over the pawn token still speaks Valve's names.

## Forward compatibility

Both files accept unknown fields, and a higher `schemaVersion` is read for whatever this build
understands rather than refused. A reader should ignore fields it does not recognise; a writer that
round-trips an overlay is asked to keep them.
