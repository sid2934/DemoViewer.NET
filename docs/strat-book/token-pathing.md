# Token pathing: routing strat tokens around walls

Owner request, 2026-10-01: tokens in the strat preview move in straight lines between keyframes and pass
through walls. This note covers what map data we already have, the approaches we could take, a prototype
measured on three maps, and a recommendation.

**Status (2026-10-01): built** on `feature/strat-book-pathing` from the owner's decisions in section 7. The
production pathfinder, the projection, the wiring, authored-segment bending, the route line and `via` are in; the
baker change (item 5) is not. docs/strat-format.md ("Motion on the canvas", "Via") is the reference for what ships;
this note stays as the spike's record.

Spike branch: `spike/strat-book-pathing`. Prototype code:

- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Zones/NavPathfinder.cs`: A* over nav areas plus a funnel pass.
  Nothing in production calls it.
- `tools/NavPathSpike/`: the measurement harness. It is outside the `.slnx`. `tools/NavPathSpike/render.sh`
  reruns everything and renders the images.

## Summary

The nav mesh is already shipped. Every `assets/<map>/zones.json` carries Valve's nav areas as polygons with
their connections. That covers all ten maps, it is VRF-free, and the app already loads it into
`ZoneSet.Areas` and `ZoneSet.AreaLinks`. A* over that graph with a funnel pass gives good routes:

- Routed lines are about 1% off the mesh. Straight lines are 37 to 66% off it on dust2 and mirage.
- A query takes 7 to 74 us (p50) and 15 to 160 us (p99).
- Building the graph takes 7 to 10 ms per map, and it keeps about 0.4 MB.

**Recommendation: approach (a), nav-area A* with a funnel pass. The work is three core agent-sized items,
plus two optional ones: bending authored segments, and a baker change.**

## 1. What map data exists

| File per map | What it holds | Use for pathing |
|---|---|---|
| `bundle.json` | radar transform, floor bands, `collisionMesh` stats | floor bands only |
| `collision.tris.gz` | the physics triangle soup (dust2: 435,649 tris), used by the 3D LOS engine's BVH (`CollisionSoup`, `VisibilityEngineCache`, both in the app) | ray tests only; there is no walkable-surface information |
| `zones.json` | places, trigger volumes, **nav areas** (`areas[]`: id, place, floor key, mean Z, corner XY) and **nav links** (`areaLinks[]`, undirected and deduplicated) | the walk graph itself |

**All ten shipped maps have both `collision.tris.gz` and `zones.json`:** ancient, anubis, cache, dust2,
inferno, mirage, nuke, overpass, train and vertigo. The `.nav` file is never shipped. `AssetBaker --zones`
reads `maps/<map>.nav` out of the map vpk through VRF's `NavMeshFile` and writes `zones.json` from it, as
`tools/DemoViewer.NET.AssetBaker/Zones.cs` shows. So the baker can reach everything the nav has, and the app
sees only what `zones.json` carries.

Do the zones carry adjacency? Yes, at two levels. `areaLinks` is area-to-area, which is the graph we need, and
`adjacency` is place-to-place. Portals are not stored, but they can be derived from the geometry (the same
edge-overlap test `NavPathfinder.Build` runs):

| map | links | share a collinear edge (portal = overlap) | no shared edge (drop, jump, gap) | median XY gap of the rest |
|---|---|---|---|---|
| dust2 | 3225 | 2575 (80%) | 650 | 21 u |
| mirage | 3344 | 2781 (83%) | 563 | 21 u |
| nuke | 4004 | 3361 (84%) | 643 | 21 u |
| inferno | 3606 | 2990 (83%) | 616 | 20 u |
| vertigo | 2749 | 2257 (82%) | 492 | 20 u |

The links with no shared edge are mostly ledges. Their median vertical step is 46 to 69 u, and 139 to 327
per map exceed 64 u. The prototype gives each one a point portal halfway across the gap.

The shared-edge links are flat by comparison. Their mean-Z step has a p99 of 17 to 33 u and a maximum of 56 to
80 u, and only one of them, on vertigo, exceeds 72 u. Drops therefore sit almost entirely in the gap bucket,
which is the only bucket the climb rule below checks.

What `zones.json` loses, and VRF still has at bake time:

1. **Direction.** `BuildAreaLinks` folds the nav's directed connections into undirected pairs, so a one-way
   drop such as mirage window to mid also reads as a way up. The prototype works around this: a gap link
   may be climbed only if the far area's mean Z is at most 64 u higher. That rule drops 173 directed edges
   on dust2, 305 on mirage and 327 on nuke. It is a heuristic, because Z is one mean per area. At 72 u it
   let mirage Middle to Window climb into window over a link with a mean step of exactly 72. At 64 the
   route goes round through Connector and Jungle, and no other measured route breaks.
2. **The portal edge.** `NavMeshConnection.EdgeId` names the side the link leaves through. With it, no
   geometry matching is needed.
3. **Ladders.** `NavMeshArea.Ladders` and the ladder lists are never emitted. Nuke, train and vertigo route
   the long way round where a ladder is the short one.
4. **Per-corner Z.** Only the mean is kept, which is enough for floor keys but not for slopes or step
   heights.

Connectivity is fine as baked. One component covers every map except for 1 to 8 stray areas (nuke has an
8-area island). A start or end on an island returns no route, and the caller falls back to a straight line.

## 2. Approaches compared

| | (a) nav-area A* + funnel | (b) occupancy grid from collision | (c) corridors from demo traces |
|---|---|---|---|
| Data | `zones.json`, shipped, all 10 maps | `collision.tris.gz`, shipped, all 10 maps | demos in the library, parsed one at a time |
| What has to be built | graph + portals (done in the spike) | a walkability rasteriser: floor detection, step height, slope, headroom, player clips. In effect Recast, rebuilt | trace extraction, a density graph, then still a path search over it |
| Quality | Valve's own walkable mesh; routes follow corridors and doors | only as good as the rasteriser; thin walls, props and stairs are hard; diagonal grid artefacts without smoothing | realistic where players go, but gaps and wrong results where data is thin; biased by meta and by side |
| Floors | each area has a floor key, so floor changes come for free | one grid per floor band, plus stair connectors you have to find | per-sample Z, workable |
| Build cost | 7 to 10 ms, 0.4 MB per map | estimated 100s of ms to seconds over 0.4 to 2 M tris; 230 k cells per floor at 16 u on dust2 | seconds of parsing per demo, but coverage needs many demos per map and side, and only one heavy parse can run at a time on 16 GB |
| Query cost | 7 to 74 us p50, up to 160 us p99 (measured) | JPS on 230 k cells: roughly 0.1 to 1 ms (estimate, not measured) | same as (a) once the graph exists |
| Risk | lost link direction and ladders (see above) | correctness of walkability; large effort | coverage, staleness after map updates, heavy-parse cost |
| Effort | small: prototype already works | large | medium to large, and still needs (a) or (b) underneath |

(d) Other options considered:

- **Precomputed place-to-place routes cached on disk.** Not needed: live queries are already well inside a
  frame.
- **Weighting (a) with (c).** This is a sound later layer. Keep the nav graph and scale each area's cost by
  how often demo players stand in it, so routes prefer common lanes over hugging corners. It is not needed
  for "not through walls".

(a) is the clear choice. (b) re-derives what the nav already encodes, and does it worse. (c) is a refinement,
not a foundation.

## 3. Prototype measurements

Machine: the owner's Mac, Release build, .NET 10. Routes run place anchor to place anchor. An anchor is
the place's area-weighted centroid, snapped into one of the place's own areas. Off-mesh is the share of
points, sampled every 8 u, that lie more than 8 u from any nav area. A query is two `Locate` calls plus
`FindPath`, which is what the projection would do per run. Timings are taken after a 500 ms JIT warmup over
1000 runs. The off-mesh check accepts an area on any floor. On nuke, where floors overlap in XY, it
understates how much of a straight line crosses walls.

Graph build:

| map | build ms | allocated during build | retained | areas | directed links | gap links |
|---|---|---|---|---|---|---|
| dust2 | 9.7 | 2.9 MB | 369 KB | 2242 | 6277 | 650 |
| mirage | 7.5 | 2.6 MB | 379 KB | 2544 | 6383 | 563 |
| nuke | 6.8 | 3.0 MB | 448 KB | 3040 | 7681 | 643 |

Routes:

| route | straight u | routed u | ratio | off-mesh straight / routed | query p50 / p99 us | alloc per query |
|---|---|---|---|---|---|---|
| dust2 T spawn to Long Doors | 2337 | 3015 | 1.29 | 59% / 1% | 23 / 92 | 89 KB |
| dust2 T spawn to B via Upper Tunnel | 3203 | 4092 | 1.28 | 66% / 3% | 47 / 112 | 91 KB |
| dust2 T spawn to B, no via | 3203 | 4091 | 1.28 | 66% / 3% | 38 / 109 | 91 KB |
| dust2 CT spawn to Long A | 1416 | 1589 | 1.12 | 52% / 0% | 7 / 15 | 75 KB |
| mirage T spawn to Jungle | 2585 | 3595 | 1.39 | 53% / 1% | 16 / 79 | 98 KB |
| mirage Top of Mid to Underpass | 1424 | 1939 | 1.36 | 60% / 9% | 16 / 79 | 90 KB |
| mirage T spawn to B, no via | 3238 | 4435 | 1.37 | 47% / 1% | 27 / 94 | 102 KB |
| mirage Middle to Window, must not climb | 776 | 1703 | 2.19 | 37% / 0% | 13 / 75 | 90 KB |
| nuke Outside to B via Ramp (crosses floors) | 852 | 4048 | 4.75 | 14% / 1% | 74 / 160 | 114 KB |
| nuke T spawn to A | 2785 | 3473 | 1.25 | 15% / 0% | 21 / 92 | 114 KB |

What the numbers mean:

- **Timing.** Routed runs are 12 to 39% longer than straight ones, so arrivals land later by the same ratio,
  at 215 u/s run speed and 115 u/s lurk walk. Nuke Outside to B site is the extreme case: 4 s straight
  becomes 18.8 s routed, because B sits under Outside.
- **Shortest route.** On dust2, T spawn to B already goes through Upper Tunnel without being told to.
- **The 9% off-mesh on mirage Top of Mid to Underpass** comes from crossing gap portals and the drop into
  Underpass. It is not a wall crossing.
- **Allocation per query** is the A* scratch arrays, sized per area. Pooling them per thread makes queries
  allocation-free. That is not done in the spike.

Images, red for the straight line and green for the route, rendered with `dv2d render --ink` and read back:

- `docs/strat-book/token-pathing/de_dust2.png`: Long Doors, B via Upper Tunnel, CT to Long A.
- `docs/strat-book/token-pathing/de_dust2-long-doors.png`: zoomed in with zone outlines. The route goes
  through the doors and around the barrel by the corner.
- `docs/strat-book/token-pathing/de_mirage.png`: T to Jungle via A, T to B via Apartments, Top of Mid to
  Underpass, Middle to Window.
- `docs/strat-book/token-pathing/de_mirage-window.png`: zoomed in with zone outlines. Middle to Window goes
  through Connector and Jungle and up into window from behind, not up the wall from mid.
- `docs/strat-book/token-pathing/de_nuke.png`: stacked panes. Outside to B runs up the east side into Ramp on
  the upper pane, then continues down Ramp into B on the lower pane, with the floor key changing at the
  portal.

Two quality issues showed up:

- A route can bend round a small nav hole, such as a barrel, right before its end. The anchor sits next to a
  corner, and the route is still correct. Better anchors, which `StratPlaceCentres` already provides, make
  this rarer.
- Routes hug corners 16 u inside the portal ends, and long curved corridors produce many small corners: 30
  to 45 on a cross-map route. That is fine for keyframes, but see the easing point below.

## 4. Integration

**Graph lifetime.** Build one `NavPathfinder` per map, beside `StratPlaceCentres` in
`ZonePlaceResolverAdapter` (`Services/Zones/AssetZonePlaceResolverSource.cs`). The canvas gets that adapter
from `QueuedPlaces` in `StratCanvasViewModel`, which reads it as a `QueueWork.RunAsync` job ("Strat places:
<map>", `SectionCompute`, `UserRequested`). The 7 to 10 ms graph build therefore runs inside a job that is
already on the processing queue, and no new job is needed. Key the graph on `EffectiveVersion`, the same
key the adapter has. The graph copies `ZoneSet.Areas`, and those carry the overlay's effective place ids,
which `Locate`'s place filter reads.

**Into the projection.** Add a nullable `PathResolver` parameter to `StratSceneProjection.Build`, the same way
`PlaceArrivalResolver` and `PlaceContainsResolver` work. When it is null, today's straight lines stay, so
every existing test is unchanged and the feature has a flag for free. The resolver returns a waypoint list
with a floor key per waypoint, and the projection treats it as follows:

- **`Run`** uses the route length instead of `distance`, so `arrive` and therefore `ContentEndTick` grow with
  the route. It inserts one entry per corner at its arc-length tick. Each inserted entry must carry
  `Arrival = true`, because the cut rule (`RemoveAll(x => x.Arrival && ...)`) is what clears a run a later
  destination interrupts, and it has to clear the corners too. Each corner gets its own segment heading,
  not the single `runYaw`, or the token faces through the wall while it rounds a corner. The final arrival
  keeps the watched yaw.
- **Easing.** `TokenTrack` applies the step's interpolation per segment. Corners turn one eased move into
  thirty small stop-start ones. Either mark the corner segments linear and place the corner ticks along an
  eased arc-length curve, which keeps the ease over the whole run, or teach the track a multi-keyframe run.
  The first option is the smaller change.
- **Floors.** A corner's `LevelMinZ` is its area's floor key. `ZoneFloor.Key` and the annotation
  `levelMinZ` are the same quantity (`MapSpace.QuantizeZ`), so the token switches pane at the portal where
  the floor changes. Nuke shows this working.
- **Lurk walk** goes through the same `Run` with `Walk` set, so it is covered at 115 u/s. A lurk that walks
  through several of its areas routes leg by leg.
- **Fan spots.** `SpotFor` checks place containment but not walkability. Snap the fanned spot with
  `Locate(placeId)` and route to it. A spot that does not snap uses the place arrival.
- **Authored entries** are drag placements with fixed ticks. Their timing belongs to the author, so routing
  only bends the line: in a post-pass over the built track, insert corners between two authored keyframes
  at arc-length-proportional ticks inside that segment's window. Whether to do this at all is an owner
  question, listed below.
- **Off-mesh points.** A drop inside a wall snaps to the nearest area within 256 u. The route goes there,
  then straight to the point. A point that does not snap gets a straight line, as today.

**Drag preview.** `TrackWith` rebuilds one slot per pointer move, which means at most a few dozen queries
per move at 7 to 160 us each. Memoise per slot per (start area, end area, rounded endpoints) so a still
pointer costs nothing. Nothing else is needed.

**Export.** `StratExportJob` takes a built `StratSceneProjection`, so routes, longer arrivals and the later
`ContentEndTick` follow with no export change. The goldens and tests that pin arrival ticks move only where
the resolver is passed.

**Cost per edit.** A full projection build with 10 tokens and about 10 runs each is about 100 queries,
which is 2 to 9 ms on the UI thread. That fits within a frame, but it is not free, so the memo should be
shared across rebuilds. A projection-level cache keyed by query, cleared when the map changes, brings a
typical edit back to a handful of misses. Queries are not the whole cost: `TrackOf` rebuilds the slot's
track once per event, and every run adds 5 to 45 corner entries to it. That cost grows with the corner
count and should be measured in item 2.

## 5. Effort, in agent-sized items

1. **Productionise `NavPathfinder` in Playback2D.Core.** Pooled scratch, the `Locate` place filter, the
   memo, and unit tests over the shipped `zones.json` fixtures: routes stay on the mesh, floor keys change
   at the right portal, an island returns null. Small.
2. **Projection: `PathResolver` through `Build`, `Run` and `TrackOf`.** Arrival-flagged corners, per-corner
   yaw, eased arc-length ticks, floor keys, fan snapping, lurk legs, and tests beside
   `StratStepMotionTests` with a small synthetic graph. Medium, and the main item.
3. **App wiring.** Build the graph in `ZonePlaceResolverAdapter`, expose it like `PlaceArrival`, pass it from
   the canvas, drag preview and export, behind a setting until it is verified. Then a UiCapture or dv2d
   check on dust2 and nuke. Small.
4. **Authored-segment bending,** if the owner wants it. A post-pass over built tracks, with tests. Small.
5. **Optional baker change** (Windows lane, needs the vpk). Emit directed links, the portal edge id and
   ladders as additive `zones.json` fields, re-bake the ten maps, and drop the climb heuristic when the
   fields are present. Small to medium, plus a re-bake.

Items 1 to 3 give routed tokens. Item 5 can come later without blocking anything.

## 6. Risks

- **One-way drops read as two-way** until item 5. Drops sit on gap links, and the 64 u climb rule covers
  those. Mirage window, the case checked, now routes correctly. A drop whose areas' mean Z differ by 64 u
  or less, because the areas are sloped or large, can still be climbed in the preview. A real step-up
  whose mean Z differs by more than 64 u gets refused, and the route goes the long way. Only the baker
  change removes both errors.
- **No ladders.** Nuke, train and vertigo take the long way where a ladder is shorter. No route is lost
  outright, because every map is connected without them.
- **Stored strats retime.** Every travel run arrives 14 to 40% later, more on stacked maps, so the
  transport's end and the export length grow for existing documents.
- **Routes are shortest, not tactical.** A* has no notion of cover or the usual lane. An author who wants a
  specific route has to add an intermediate step, the same way "via Upper Tunnel" is done in the spike.
- **XY-overlapping levels on one floor key**, such as mirage Underpass under Mid. Snapping by XY alone can
  pick the wrong level. Snapping by place id, as the spike does, fixes it for place targets. A free point
  dropped over both levels picks the smaller of the areas containing it, which may be the wrong one.

## 7. Owner decisions (2026-10-01)

1. **Timing follows the real path.** Decided: yes. Runs walk the route at run and walk speed, so arrivals and
   `ContentEndTick` land later, existing strats included.
2. **Authored drag segments bend.** Decided: yes. Between two keyframes the user placed the token follows the route
   and still arrives at the keyframe's tick; its speed fits the time, and an impossible route still arrives on time.
   The same applies to every segment whose ticks a step fixed (a position verb's walk, a run cut short).
3. **No re-bake.** Decided: keep the heuristic (gap links climbed up to 64 u, shared-edge links two-way, no ladders).
   Item 5 stays open.
4. **Faint route line.** Decided: yes, on the canvas and the Detected preview, in the side colour at low alpha through
   theme tokens (`Pb2dCanvasRouteT`/`Ct`). The export draws it too: it is the same projection, the line is faint and
   only shows while a token moves, and it answers the question a viewer of the clip has.
5. **Via.** Decided: a step, or a line, can name places to go through, as `via` (places) and `viaPoints` (points), the
   `watch.points` shape. Routing goes through each in order.

## 8. What was built, and measured

- `NavPathfinder` (Playback2D.Core): per-thread search scratch with generation stamps, so a miss allocates only its
  answer (about 1 KB for a 43-point route on dust2) and a repeat is answered from a bounded memo of exact queries with
  no allocation. It also snaps a point onto the mesh and adds a point wherever a route crosses into another floor.
  `NavPathfinderTests` builds all ten shipped bakes and routes between each map's places.
- `PathResolver` / `NavPathResolver` (App, `Services/Strats`): place names to ids, start and end location with
  fallbacks. Built in `ZonePlaceResolverAdapter` with the zones, inside the queued "Strat places" read.
- The projection takes the resolver through `PlaceSet`, so `Build`, `Run`, `TrackOf` and `TrackWith` all see it.
  Null is the old straight lines; the feature `stratbook.routing` (on by default) decides what the canvas passes.
- Cost per edit, Execute B on dust2 (8 steps, 10 tracks, a lurk, two throws, opponents dragged at three steps), Debug
  build on the owner's Mac: a full projection build is 0.17 ms straight and 1.7 ms routed with a warm memo (3.1 ms
  on a fresh graph), and 1.7 ms after a one-token edit; the drag preview (`TrackWith`) is 0.14 ms per pointer move.
  Under a frame, so no per-slot cache was added. Keyframes go from 63 straight to 313 routed.
- UiCapture at 1280x800 (`strat-routing-execute-b`, `strat-routing-execute-b-push`, `strat-routing-via`):
  `docs/strat-book/token-pathing/routing-execute-b.png` (1:44, the split out of spawn: A, B and C on their way to the
  tunnels, D to top of mid, E walking to Middle, each with its faint line ahead),
  `routing-execute-b-push.png` (1:23, the push onto B through upper tunnel and the doors) and `routing-via.png` (C to B
  the shortest way, through the tunnels; B to B via Middle, with the via field in its step row).
