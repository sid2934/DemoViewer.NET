# 3D Visibility ("time enemy was visible") — Implementation Plan

> Complete / as-built — Phases G/1/2/3 shipped and validated on main (the
> `DemoViewer.NET.Visibility` BVH engine + the `VisibilityAnalyzer`); retained as the coordinate-frame
> and smoke design rationale cited from the visibility code (flashbang modelling remains a minor open
> note). The second original goal (after 2D map flattening, now landed): compute,
> from recomputed **3D line of sight**, when an enemy was actually visible to a player — the engine-fidelity
> stat (NOT the demo's `spotted` bit). Mirrors the asset pipeline: the **baker** extracts collision
> geometry, the **app stays VRF-free** and raycasts against baked triangles.
>
> **Read order:** this doc, then `docs/research/cs2-map-assets.md` §5–6 (visibility facts, awpy precedent,
> `[UNCERTAIN]`s) and `docs/asset-pipeline/design.md` §0.5 (the ship-baked, app-VRF-free architecture this
> extends). (A related radar/floor pipeline plan — the former `2d-playback-assets-plan.md` — was
> retired once its work shipped.)
>
> **This plan is deliberately front-loaded.** Phases G and 1 are hard gates; everything downstream
> (stat definition, cadence, FOV, integration) is **provisional** and must not be detail-built until the
> geometry and the raycaster are proven. The coordinate check blocks trusting any visibility
> output — it's a gate, not a nicety.

---

## 0. The primitive

`bool IsVisible(Vector3 eye, Vector3 target)` — does the segment `eye→target` clear the map's **collision
geometry**? Ray-vs-triangle (Möller-Trumbore) accelerated by our own BVH. **Pure math — no VRF, no
Avalonia, no parser.** This is awpy's proven approach (`VisibilityChecker`); the raycaster is not the novel
part. The novel work is (a) proving the geometry lines up and (b) the eye/hitbox anchoring.

Architecture (mirrors the radar pipeline exactly):

```
 BAKER (VRF)                         BUNDLE                       ANALYSIS (VRF-free)
 world_physics.vmdl_c  ──PhysAggregateData──►  collision.tris  ──►  VisibilityEngine (triangles + BVH + raycast)
   (per map, one time)     triangle soup       + bundle.json ref        │  IsVisible(eye, target)
                                                                        ▼
                                                          visibility consumer: eye/hitbox model,
                                                          pair selection, cadence → the STAT
```

---

## G. Gate — prove the geometry lines up (blocking; do this first)

**The risk:** collision triangles come from VRF `PhysAggregateData`; player positions come from
`PositionUtil.CellToWorld`. If they don't share one world frame (origin / scale / Z datum), **every raycast
is garbage that looks plausible** — wrong visibility numbers, not an obvious crash. This is the
nav-Z-vs-player-Z datum risk from the floor episode, and "they're both source world units" is exactly the
*should* that nearly bit us there.

**The check (dependency-free, decisive):**
1. Baker extracts `collision.tris` for ONE map (dust2 — flat, single-floor, simplest to reason about).
2. A minimal raycaster (triangles + brute force is fine for the gate — no BVH yet).
3. Replay the dust2 demo; for a scatter of living-player `CellToWorld` positions across a round, **cast a
   ray straight down** and measure the distance to the first collision triangle below.
4. **Pass:** median distance-to-floor ≈ a small constant near 0 (feet sit on the floor). **Fail:** players
   float hundreds of units above all geometry, or hit nothing → there's a frame offset/scale mismatch to
   resolve before anything downstream is trustworthy.

Also here: **probe `pawn.TryGet("m_vecViewOffset", …)` on a real pawn** — it may be dict-readable even
though it's absent from the generated wrappers. Real eye-Z beats the +64/+46 approximation for LOS over
cover. One probe decides it.

**Nothing downstream is designed in detail until G passes.**

### G — result (2026-07-06): pass

Ran on dust2 (`vitality-vs-fut-m2-dust2.dem`, 444 living-player feet samples vs the baked 435,649-triangle
collision soup — `VisibilityFrameGateProbe.Dust2_PlayerFeet_SitOnCollisionFloor`):

- **100% hit rate**, **median feet-above-floor gap = 0.7u** (p10 = 0.2, p90 = 5.4; 93% within ±8u, 96%
  within ±32u). Collision triangles and `CellToWorld` positions are in **one world frame** — no offset,
  no scale, no Z-datum mismatch. The extraction applies **no transform** (physics `BindPose` is empty ⇒
  vertices already world-space); a non-empty bind pose now throws in the baker rather than silently
  mis-placing geometry.
- **Eye-height probe** (`Probe_EyeOffset_And_EyeAngles_Fields`): `m_vecViewOffset` is **NOT** in the pawn
  field dict (only `m_flDuckViewOffset` / `m_flBombPlantViewOffset` scalars). **But** `m_angEyeAngles`
  (Vector3, view dir) **is** readable, as is duck state (`m_flDuckAmount` 0→1, `m_bDucked`, `m_bDucking`).
  ⇒ **Decision (see §3.3):** eye Z = `origin.Z + lerp(64, 46, m_flDuckAmount)` (engine-faithful crouch
  interpolation), view dir from `m_angEyeAngles`.

---

## 1. Phase 1 — the raycaster primitive, validated (hard gate)

Build the real primitive and prove it *correct*, because wrong-LOS is easy not to notice and the whole
feature is worthless if `IsVisible` is wrong.

- **Baker collision extraction** (`tools/DemoViewer.NET.AssetBaker`, new `CollisionMesh.cs`): `world_physics
  .vmdl_c` → `PhysAggregateData.Parts` → Hull (convex, triangulate faces) + Mesh (triangles) shapes → world
  triangle soup. Emit `collision.tris` (binary: count + `float32[9]` per triangle) + a `collisionMesh`
  ref in `bundle.json`. (cs2-phys-extractor is the C# precedent, research §6.1.)
- **`VisibilityEngine`** (new, pure): load triangles, build a BVH (AABB tree, split on largest centroid
  spread — awpy's), `IsVisible(eye, target)`. **BVH build off the UI thread** (a multi-second build on it
  freezes Avalonia) — or bake the BVH in the baker. Where it lives: a parser-blind geometry component (it
  takes triangles + points); the *analysis* that supplies entity positions is the consumer.
- **Validation — two tiers, decoupled:**
  - **Primary gate (dependency-free):** a handful of **hand-verified pairs** read off a rendered radar
    frame — two players in the open with nothing between → `true`; one clearly behind a solid wall → `false`.
    Plus the Phase-G ray-down check. Catches gross errors with zero external setup.
  - **Rigorous second check (not the gate):** awpy's CS2 `VisibilityChecker` + its shipped `.tri` files.
    **Decouple the two failure modes** — run awpy's `.tri` through *our* raycaster and *our* `.tri` through
    our raycaster on the same point-pairs: agreement isolates a raycaster bug from an extraction bug.
    (awpy needs Python + its CDN, so it's the second check, never the gate.)
- **Perf spike (early, before designing cadence):** measure real per-ray + BVH-build cost on dust2. The
  research warns of "tens of minutes" for a naive full sweep — **know the number before designing around
  it.**

### 1 — result (2026-07-06): pass

New pure project `src/Analysis/DemoViewer.NET.Visibility` (System.Numerics only, VRF-free): `CollisionTris`
reader, `TriangleBvh` (median-split, stackalloc traversal, any-hit + nearest-hit), `VisibilityEngine`
(`IsVisible`, `RayDownDistance`).

- **Correctness — proven against a dependency-free brute-force oracle** (`VisibilityEngineTests`, dust2):
  ray-down **219/219 match**, segment **502/502 match** (21% visible — both truth-values represented),
  geometric invariants **through-floor 219/219 occluded, symmetry 990/990**. Zero disagreements ⇒ the BVH
  is correct relative to the triangle soup. awpy oracle deferred (Python/CDN) — not needed to gate.
- **Perf** (`tools/VisibilityBench`, Release, dust2 435k tris): **BVH build 485 ms**, **`IsVisible`
  0.376 µs/ray** (2.7 M/s). Pessimistic full all-pairs (10 players → 90 dirs × 3 anchors = 270 rays/tick)
  = 0.10 ms/tick ⇒ **~3.9 s/demo at 16 Hz** before any culling. Far from "tens of minutes" (that was
  pre-BVH). ⇒ **Decisions:** ship triangles + build BVH lazily off-thread (485 ms doesn't justify baking a
  BVH); **default cadence 16 Hz** (every 4th tick, ~62 ms time-visible resolution).

---

## 2. Phase 2 — the stat: computed + validated (2026-07-06)

Built in `src/Analysis/DemoViewer.NET.Analysis/Visibility/`:
- `PlayerVantage` / `ViewFrustum` (pure, in the Visibility lib): eye = `feet + lerp(64,46,duck)`; forward from
  `m_angEyeAngles` (QAngle pitch=X/yaw=Y degrees); 6 hitbox anchors (spine head/chest/pelvis/knee + ±16u
  shoulders **perpendicular to the horizontal sightline**), target heights duck-scaled; rectangular frustum
  (±53° yaw / ±37° pitch — *not* a cone, so vertical FOV stays ~74° for multi-level correctness).
- `VisibilityAnalyzer` (Analysis): standalone sequential replay (the main eval pass is digest-precomputed,
  not live-positioned — forced by the architecture), samples every 4th tick (16 Hz), directed enemy pairs
  only, injected position resolver (host `PositionUtil.CellToWorld` — no App dependency, no duplicated
  constant). Per pair: **exposed** (any anchor LOS clear) + **could-see** (exposed ∧ in-frustum); could-see ⊆
  exposed by construction. Aggregates: per-viewer `CouldSeeAnyEnemySeconds`, per-target `ExposedToAnyEnemySeconds`.

**Validation** (`PlayerVantageTests`, `VisibilityAnalyzerTests`) — on a single-plane map (dust2) AND a
**multi-level** map (nuke), since floor-correct cross-level occlusion is the whole reason for 3D:
- Pure conventions — exact-value tests (forward vectors, duck lerp, frustum bounds, lateral-anchor axis).
- **Kill-tick oracle (load-bearing):** direct (non-wallbang) kills, sampled 8 frames pre-death (killer
  mid-aim). **dust2: exposed 90% / could-see 90% (147); nuke: exposed 95% / could-see 95% (150).** Exposed and
  could-see being equal-and-high confirms the angle/FOV math (a pitch/yaw or deg/rad slip would collapse
  could-see far below exposed). Misses are grenade kills (no LOS needed) + a few close-range edge cases.
- **Invariants:** could-see ≤ exposed on every pair; all durations in [0, window] — hold on both maps.
- **Multi-level differentiator (the decisive 3D check):** on nuke, enemy pairs at similar XY but Z-separated
  across the floor band are **92% occluded (56/61)** — a 2D top-down projection would call all 61 visible.
  The 8% visible are the genuine openings (ramp/hole/vents). This is exactly the floor-correctness dust2
  cannot exercise.

**Phase 3 — surfacing: the 2D Playback "Vision" overlay (decided 2026-07-06).** A toggleable overlay
draws, at the current tick, a thin team-coloured sightline from each player to every enemy they currently
have 3D line of sight to (could-see), recomputed from baked collision via the *same*
`VisibilityAnalyzer.EvaluatePair` the stat uses. `PlayerMarker` carries pitch + duck; the VM lazily builds
the collision BVH off-thread behind the `ShowVision` toggle; the viewport draws sightlines per floor band
beneath the markers. `ZVisionOverlayRenderTests` renders nuke at a real kill frame → engine loads, 4
sightlines drawn (capture saved). Not a stats table — the per-player scalar columns / pair matrix remain
available future surfaces off the same `Report`.

---

### Original provisional outline (superseded by the above):

- **Eye/hitbox model (the novel work):** attacker eye = `CellToWorld(origin)` + eye-height (real
  `m_vecViewOffset` if the G-probe found it, else +64 standing / ~46 crouched via duck state); view
  direction from `m_angEyeAngles`. Target = **multiple hitbox anchors** around the target origin
  (head/chest/pelvis, ~±lateral) — **visible if ANY anchor ray is clear** (partial exposure = visible).
  awpy is point-to-point; the anchor set is our addition.
- **The stat — "time enemy was visible":** per directed living pair (A → E), accumulate **two** times —
  *exposed* (LOS clear) and *could-see* (LOS clear AND E inside A's view cone). **Downsampled** per-N-ticks
  (continuous "time visible") + distance-cull + living-pairs-only (+ optional `vvis` voxel pre-reject).
  Cadence set by the Phase-1 perf number. The FOV dot-product runs *before* the ray (cheap reject), so
  could-see adds an accumulator, not a second raycast.
- **Home:** `DemoViewer.NET.Analysis` (it's analysis, not rendering). Reuses the bundle-loading pattern the
  2D module already uses to get `collision.tris`.
- **Reuse for a 2D overlay (later):** the per-tick visibility (who-can-see-whom) is the same data a 2D
  Playback LOS overlay would draw — build the stat first, the overlay falls out.

---

## 3. Decisions to surface (yours / firmed at implementation)

1. **What "visible" means — decided 2026-07-06: compute both.** Track two per-pair stats —
   **exposed-time** (LOS-only: sight line geometrically clear, regardless of where A looked) and
   **could-see-time** (LOS + A's ~106° view cone from `m_angEyeAngles`: "you can't see behind you"). No
   definitional lock-in; the two together are more diagnostic. Cost: the FOV test is a cheap dot-product
   pre-filter *before* the raycast, so could-see is nearly free on top of exposed (FOV-reject skips the ray
   entirely); the extra work is only the separate accumulator. Both share one raycast per anchor.
2. **Bake triangles vs bake the BVH — decided: ship triangles, build the BVH lazily.** Measured build is
   485 ms (dust2), cheap enough to do once off-thread; a bespoke baked-BVH format isn't worth it.
3. **Eye height — decided by the Phase-G probe:** `m_vecViewOffset` is not surfaced, so eye Z =
   `origin.Z + lerp(64, 46, m_flDuckAmount)` (standing 64u, crouched 46u, interpolated by the live duck
   amount); view direction from `m_angEyeAngles`. Not the crude binary — the duck amount is networked.
4. **Cadence — decided: 16 Hz default** (every 4th tick). Perf spike: ~3.9 s/demo pessimistic full
   all-pairs; sub-100 ms time-visible resolution. Tunable; distance-cull + FOV reject + living-only lower
   the real cost.

## 4. Known limitations (named up front, not surprises)

- **Smoke occlusion — resolved 2026-07-06.** Active smoke clouds now occlude **could-see (vision) only**, never
  **exposed (line of fire)** — exposed is a triangle raycast and smoke isn't in the collision mesh, so it's
  smoke-blind *by construction*; a smoked-off enemy correctly reads *exposed but not seen* (exactly why
  through-smoke kills happen). Mechanism: pure `SmokeVolumes.SegmentBlocked` (3D segment-sphere, radius 144)
  fed into the could-see branch of `VisibilityAnalyzer.EvaluatePair`; smokes collected per tick from
  `CSmokeGrenadeProjectile` (`m_nSmokeEffectTickBegin > 0`, `m_vSmokeDetonationPos`) — the 2D overlay's proven
  gate. **Gotcha:** `m_nSmokeEffectTickBegin` is on a *different tick origin* than `DemoFrame.ServerTick`
  (per-demo offset ≈ pre-recording ticks) — an age cap comparing the two dropped *all* smokes; the entity
  lifecycle bounds the active window instead (regression-guarded by a bounded-concurrency test). Validated
  (App.Tests, 9/9): synthetic midpoint-smoke blocks 100% of geometry-clear+in-FOV kills; real ThroughSmoke
  kills flip could-see→false for the majority; exposed bit-identical with/without smoke; max concurrent smokes
  bounded, age-spread ~22 s, vision reduced (not collapsed). Flashbangs still not modelled (a flashed viewer
  still "sees") — deferred.
- **Hitbox anchors are an approximation** of the real animated hitboxes (which would need the model + anim
  replay — out of scope; player hitbox models were deliberately skipped in the local asset cache).

## 5. What this plan does NOT yet decide

- The pair-accumulation pipeline internals, the exact anchor set, the FOV angle, and the display surface —
  all **provisional**, firmed only after Phases G + 1 prove the geometry and the primitive. Do not
  detail-build them on an unproven raycaster.
