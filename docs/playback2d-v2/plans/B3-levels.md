# Phase B3 — Multi-level maps (implementation plan)

**Branch:** `feature/playback2d-v2` · **Design:** [`docs/playback2d-v2/design.md`](../design.md) §5.3, §7.3, §9, risk 5
· **Depends on:** B0, B1 (Core project + `MapSpace`/`LevelPane`/layers), B2 (`AnnotationDocument`,
`Playback2DSettings`, `AnnotationTrack`), A1 (`TimelineControl`, follow-by-card)

This plan is self-contained: it cites the current code it replaces and gives full signatures for
everything it introduces. A coding agent should be able to execute it without re-reading the design.

> ## Integrator corrections (BINDING — supersede anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry. The four items
> under "Two things B3 needs that other phases may not have planned" and D10 are resolved here.
>
> 1. **Project paths: `src/Playback2D/DemoViewer.NET.Playback2D.{Core,Pipeline}`**, slnx folder
>    `/src/Playback2D/` — **not** `src/Visualization/`. D10's assumption is overridden; substitute
>    the prefix everywhere in this plan. Tests go in the single project
>    `src/Playback2D/DemoViewer.NET.Playback2D.Tests` (B0 creates it; there is no
>    `…Core.Tests`), so the "if it does not exist, create it" section is dead and the run commands
>    become `dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release`.
> 2. **B1 has already adopted this plan's level model**, so T1 is a smaller task than written:
>    `MapLevelId`, the `MapLevel` class shape, `MapSpace.LevelQuantum`/`QuantizeZ`/`LastChange`, the
>    `LevelFor(double, MapLevelId?)` overload and `LevelDisplayMode { Stacked, Single, SideBySide }`
>    are declared in B1. B3 fills in the **bodies** — overlap-carry minting in `Rebuild`, the sticky
>    band in `LevelFor(z, prev)` — plus `LevelSetChange`, `LevelHysteresis`, `LevelSelection`,
>    `LevelCrossingTracker`, `SingleLayout`, `LevelLayouts`.
> 3. **`MapSpace.Rebuild` takes `IReadOnlyList<FloorSlice>`, not `(double MinZ, double MaxZ)`
>    tuples** — B1 T1 moves `FloorSplitter`/`FloorSlice` into Core, so the App-free objection no
>    longer applies. The canonical signature is
>    `LevelSetChange Rebuild(IReadOnlyList<FloorSlice> bands, IReadOnlyList<SKImage?>? radarByLevel
>    = null, RadarBindingQuality quality = RadarBindingQuality.None)`.
> 4. **`LevelPaneStore` is not a new type — it is behaviour added to B1's `PaneSet`.** B1 already
>    reconciles panes by `MapLevel.Id` and carries `SliceCamera`/`ManualOverride`/`Rig`; B3 adds
>    "retain state for levels that are not currently arranged (Stacked ⇄ Single), drop it only on
>    `Removed`". T4's `LevelPaneStore.cs` becomes edits to `PaneSet.cs`, and
>    `LevelPaneStoreTests` becomes `PaneSetLevelRetentionTests`.
> 5. **B2 ships `AnnotationDocument.ApplyMigration(DocDelta)`** — B3 consumes it and does not add it.
> 6. **`TickAxis` stays, with a domain warning.** A1's timeline lays out on the **frame-index** axis
>    (A1 D5, design §5.6), and A1 exposes `Playback2DTimelineViewModel.XForFrame`/`FrameIndexAt`
>    rather than an axis type. `TickAxis` is therefore Core-internal drag math only: the App builds
>    one per drag from A1's pixel mapping plus `ITimelineData.FrameIndexAtTick`, and **must not** be
>    used to lay out A1's control. Envelope edits are authored in ticks and converted at the seam.
> 7. **D7 is confirmed** — envelope drag handles are B3's, B2 ships read-only markers. This closes
>    design §12 open question 3; B2's plan already agrees.
> 8. **Feature-catalog placement:** B3 inserts `playback2d.levels.auto` as the third row of the one
>    contiguous v2 block (annotations · timeline · **levels.auto** · follow · export) that A1 creates
>    after `analysis.breakpoints`. Gate reads go through **`IModuleContext.Features`** (A1's seam),
>    never a directly injected `IFeatureGate`.
> 9. **Settings keys are B5's names:** `Playback2D:LevelDisplayMode` and
>    **`Playback2D:AutoLevelFollow`** (not `AutoFollowLevel`). B3 adds the two properties to the
>    existing `Playback2DSettings`; it does not create the class.
> 10. **Fixtures and goldens use C1's corpus layout** — `tests/fixtures/playback2d/scenes/` and
>     `tests/fixtures/playback2d/goldens/cpu/<name>@<w>x<h>.png` (there is no
>     `tests/fixtures/playback2d/golden/`). B3's scenes are the canonical corpus entries
>     **`nuke-multilevel`** (shared with B1 — do not author a second `nuke-two-level.json`),
>     `nuke-multilevel-noradar`, and `mirage-single-level` (shared with B1). The "byte-identical to
>     B1's stacked golden" assertion is then literally the same file.

---

## Scope & exit criterion (quoted from design §9)

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **B (core)** | B3 | Levels: `SingleLayout`, level strip, AutoFollow + hysteresis, buffer resets; `AnnotationTrack` time-edit handles | Levels shipped | 1 wk |

Supporting design text (§7.3, verbatim):

> `MapSpace` + `ILevelLayoutPolicy`. `StackedLayout` preserves today's all-floors view; `SingleLayout`
> adds a level strip (manual pick) and **AutoFollow** — switch to the followed player's level via
> `LevelFor(z)` with hysteresis. Explicit per-level radar binding with a visible no-radar state.

And §5.3:

> **Level identity is `ZMin`-keyed, and `MapSpace` is rebuildable.** … `MapSpace` re-derives on
> `FloorSplitter` change with stable quantized-`ZMin` level ids, remapping panes and annotations on
> rebuild. Radar binding is explicit per level, resolved at (re)build with a visible "no radar for
> this level" state — the silent per-frame count-match LINQ dies. Trail and smoothing buffers reset
> when an entity crosses levels.

Risk 5 (design §10) is this phase's headline risk and its mitigation is this phase's core work.

**Out of scope for B3:** `SideBySide` layout (reserved enum member only), adopting
`m_MinimapVerticalSectionHeights` as the split (deliberately deferred — see
`FloorSplitter.cs:267-274`), any change to `FloorSplitter`'s detection math.

---

## Ground truth — what exists today (read before writing code)

All paths absolute-from-repo-root.

**`src/App/DemoViewer.NET/Modules/Playback2D/FloorSplitter.cs`** (383 lines) — the floor-detection
authority, survives intact per design §5.3/§9.

- `FloorSplitter(double bucketWidth = 64, double gapThreshold = 180)` (`:63`). `BucketWidth` is the
  histogram quantum and **the only quantum any slice boundary is ever expressed in**:
  `ComputeSlices` emits `new FloorSlice((lo + loIdx) * BucketWidth, (lo + hiIdx) * BucketWidth)`
  (`:375`) and `SliceFromBuckets` emits `new(firstBucket * BucketWidth, (lastBucket + 1) * BucketWidth)`
  (`:381-382`). **Every histogram-derived `MinZ` is an exact integer multiple of 64.**
- Precedence chain, in `Slices` (`:85-110`): authoritative baked nav floors (`_authoritativeFloors`,
  set by `SetAuthoritativeFloors`, `:128`) bypass everything; otherwise the density-valley histogram
  with **sticky floor count** — `_slices = fresh.Count >= _slices.Count ? fresh : _slices;` (`:104`).
  The count only ever grows; **boundaries keep moving** as the histogram accumulates (a boundary is
  the integer midpoint between two peak buckets, `:354`).
- `SliceIndexFor(double z)` (`:234-263`): first containing slice, else nearest by `MidZ`. No memory,
  no hysteresis despite the doc comment's "hysteresis intent".
- Tuning constants that matter to us: `MinPeakFraction = 0.04` (`:32`), `ValleyDepthFraction = 0.25`
  (`:39`). Comment at `:283` records the empirical fact this plan's hysteresis is sized against:
  on Nuke **"the two floors are only ~90-160u apart"**.
- Authoritative floors come from `LoadedMapAsset.Floors` (`MapAssetLoader.cs:34-35`) → `FloorBandDto`
  (`CS2DemoKit.Analysis.Visibility.MapAssetBundle`, `MapAssetBundle.cs:34`) — **arbitrary doubles, not
  bucket-aligned.**

**`src/App/DemoViewer.NET/Modules/Playback2D/Playback2DViewport.cs`** (1438 lines) — the control B1
replaces with `Scene2DHost` + compositor. The parts B3 supersedes:

- `EnsureCameras(int sliceCount, double viewW, double bandHeight)` (`:492-523`) — camera lifecycle
  keyed by **array index**, the exact defect risk 5 names: when the slice count grows, index *i*
  silently means a different floor. Preserve-by-index (`:505-511`) becomes preserve-by-`MapLevelId`.
- `ApplyFitToAllSlices()` (`:525-532`), band layout in `Render` (`:546-600`) — `sectionCount - 1 - section`
  puts the **highest floor on the top band** (`:583`); the level strip must keep that ordering.
- `SliceIndexAtScreenY` / `ScreenSectionOffset` (`:464-488`) — band hit-testing, replaced by
  `LevelPane.ViewportRect` hit-testing.
- `TryFollow(int sliceIndex, out ViewportTransform target)` (`:789-817`) — follows `_followSlot`;
  crucially, **when the followed player is not on this slice it returns `false` and the slice holds**
  (`:805-808`). Under `SingleLayout` there is one pane, so "which level is shown" becomes a decision
  instead of a per-pane filter — that decision is AutoFollow.
- `TryFitAlive` (`:743-784`), `FollowHalfWorld = 900` (`:55`), `LerpResponse = 7.0` (`:56`).
- Marker smoothing: `_smoothedPos` keyed by slot (`:86`), `AdvanceMarkers` (`:648-698`) with
  `MarkerSnapDistanceSq = 250²` (`:64`) and `MarkerSettleEpsilonSq` (`:65`). Snap-on-teleport exists;
  **snap-on-level-crossing does not** — that is the boltobserv streak bug B3 fixes.
- `ResolveRadarImage(LoadedMapAsset asset, int sliceIndex)` (`:1096-1115`) — the doomed heuristic:
  per-band `OrderBy` LINQ, index-matches floors to radar layers only when `floors.Count == layers.Count`,
  else silently returns the highest layer. `TryDrawRadar` (`:1065-1090`) places it via
  `asset.Bundle.Bounds`. `RadarLayerDto(double MinZ, double MaxZ, string Image)` (`MapAssetBundle.cs:37`)
  already carries the Z band — the binding is derivable, it was just never done.

**`src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs`** — data the level model needs:
`SectionHeights` (`:226`), `MapBounds` (`:233`), `AuthoritativeFloors => MapAsset?.Floors` (`:240`),
`MapAsset` (`:243`), `Markers` (`:268`), `FollowablePlayers` (`:287`).
`PlayerMarker` (`PlayerMarker.cs:30-44`) carries `Slot, Team, WorldX/Y/Z, YawDegrees, …` — **no SteamId**;
level assignment is therefore keyed by `Slot` (annotations keep SteamId anchoring per §5.4, unaffected).

**`src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml`** — viewport chrome real estate:
root `Grid ColumnDefinitions="*,4,320"` (`:84`), viewport at `:87`, **bottom-left** transport/status
overlay (`:90-141`), **top-right** HUD stack — LiveSync dot + kill feed (`:146-230`), **top-left**
overlay-visibility toggles (`:235-253`). The right column (320px) is the roster/game-info panel.
The right-centre gutter of the viewport is the only unclaimed edge — see "UI placement" below.

---

## Decisions made

Recorded because the design left these open or ambiguous.

**D1 — Level quantum = 64 world units, identical to `FloorSplitter.BucketWidth`.**
`MapSpace.LevelQuantum = 64.0`. Every histogram-derived `MinZ` is already an exact multiple of 64
(`FloorSplitter.cs:375,381`), so quantization is the identity function on the common path and only
snaps the arbitrary-double authoritative nav bands (`FloorBandDto`). Rounding is
`Math.Floor(z / 64.0 + 0.5)` (not `Math.Round`) so behaviour is uniform across negative Z — CS2 maps
are routinely at negative Z (the probe demo's span is `[-416..-111]`, `FloorSplitter.cs:270`), and
banker's rounding there would be a silent identity change at exactly the boundary values.

**D2 — Quantized `ZMin` mints ids; overlap matching *carries* them.** A boundary that drifts by one
bucket must not re-key a level. So on rebuild, new bands are matched to old ones by **band-overlap**,
not by key equality, and a matched band **inherits the old level's id**. Quantized `ZMin` is only the
minting rule for genuinely new levels (with a collision bump). Algorithm in "Remap algorithm" below.

**D3 — `LevelFor` is split into a pure resolver and a stateful selector.** The design writes
`MapLevel LevelFor(double worldZ); // hysteresis band at boundaries`, but hysteresis needs memory and
time, and `MapSpace` must stay a pure, time-free Core type (design §5.1 bans wall clock in Core; a
mutable "last level" field would also make `MapSpace` unsafe for the export session's private replay).
Therefore: `MapSpace.LevelFor(double)` is stateless (mirrors `FloorSplitter.SliceIndexFor`);
`MapSpace.LevelFor(double, MapLevelId?)` applies the **spatial** sticky band given the caller's previous
answer; the **temporal** dwell lives in `LevelHysteresis`, which consumes `SceneTime.DeltaSeconds`.

**D4 — Hysteresis sizing (see justification below): spatial band
`H = clamp(0.25 × min(spanCurrent, spanCandidate), 32u, 128u)`, dwell `0.35 s` of scene time,
`SceneTime.IsDiscontinuity` ⇒ switch immediately with no dwell.** Per-entity level assignment
(markers, trails, radar filtering) uses the spatial band only, **no dwell** — a marker must never lag
its own level. Only AutoFollow's *view* decision uses the dwell.

**D5 — Level strip lives in the viewport's right-centre gutter**, vertical, highest level at the top
(matching `Playback2DViewport.cs:583`'s "highest floor on the top band"). See "UI placement".

**D6 — Annotation anchor remap is a migration, not an edit.** `SpaceRef.World(double LevelMinZ)`
anchors are rewritten in place on `MapSpace` rebuild **without** pushing a `DocDelta` onto the undo
stack and **without** bumping user-visible dirty state. Rationale: the user did not act; making a
histogram tick undoable would let Ctrl+Z restore an anchor to a level that no longer exists (design
§10 risk 13 — undo scope). `AnnotationDocument.Version` **is** bumped so the ink `SKPicture` re-records.

**D7 — `AnnotationTrack` envelope drag handles land in B3** (design open question 3). §9's B3 row
names them explicitly and §9's B2 row ships only "envelopes + timeline markers"; B2 therefore ships
read-only markers and B3 adds interaction. If B2 has already shipped drag handles, B3 reduces to
reviewing them against the contracts here.

**D8 — Feature gating.** Only `playback2d.levels.auto` exists per design §7.7 and it gates
**AutoFollow only**. The strip itself, `SingleLayout`, and manual level picking ship with the tab and
are **not** separately gated (a new gate id is a permanent persisted key — §7.7 — and a manual level
picker is not a feature worth one). With the gate off, the strip renders without its AUTO chip.

**D9 — Level strip hides itself when `MapSpace.Levels.Count <= 1`.** Single-floor maps (the vast
majority) see zero new chrome, exactly as they see no band splitting today.

**D10 — Core project location.** This plan writes Core files under
`src/Playback2D/DemoViewer.NET.Playback2D.Core/`, per the integrator correction; the `/src/Playback2D/` slnx
folder. If B0 placed Core elsewhere, only the path prefix in this document changes — no contract does.

---

### Hysteresis sizing — the justification (design asked for one)

Constraints, all derived from code or CS2 physics:

1. **Boundary jitter floor.** A histogram boundary is `(peakA + peakB) / 2` in *bucket* indices
   (`FloorSplitter.cs:354`), so a one-bucket shift of either peak moves the boundary by up to one
   bucket = **64u**, and integer division means it can move by half that in practice. A band narrower
   than half a bucket (32u) would be re-triggered by boundary drift alone → **H ≥ 32u**.
2. **Jump rejection.** CS2 jump velocity 301 u/s under `sv_gravity 800` gives an apex of
   301²/(2·800) ≈ **56.6u**; step-up height is 18u and the crouch delta ≈ 18u. A band of **≥ 64u**
   makes it geometrically impossible for a jump, a step, or a crouch to change the level.
3. **Band-interior ceiling.** The band must stay strictly inside both adjacent floors or a player
   standing normally on one floor could resolve to the other. Slices span valley-to-valley
   (`FloorSplitter.cs:371-376`), so 25% of the *thinner* adjacent span keeps the dead zone inside the
   middle half of both bands. Peak-to-peak separation on Nuke is as low as ~90u (`:283`), so a fixed
   64u band is unsafe on a degenerate thin band — hence the value is **relative with a cap**.

⇒ `H = clamp(0.25 × min(spanCurrent, spanCandidate), 32.0, 128.0)`. On real maps the 0.25×span term
exceeds the cap, so H = 128u (two buckets, comfortably above the 56.6u apex) and jumps/steps/crouches
can never flip a level. On a pathologically thin band H degrades toward 32u and the dwell carries it.

4. **Dwell = 0.35 s of scene time** (`SceneTime.DeltaSeconds`-accumulated, so export at 30 fps behaves
   identically to interactive at 144 fps — design §5.1). Chosen to match the camera's own settle:
   `LerpResponse = 7.0` (`Playback2DViewport.cs:56`) is a 1/7 ≈ 0.14 s time constant, ≈ 0.35 s to 92%
   convergence. The level switch and the camera re-fit therefore read as one motion rather than two.
   Shorter dwells let stair dither through; longer ones make a genuine stairwell transition (1–2 s of
   climbing) feel unresponsive.
5. **Discontinuity bypass.** On `SceneTime.IsDiscontinuity` (seek/jump) there is no continuity to
   protect: reset the dwell accumulator and adopt the resolved level immediately, or a scrub into
   another floor would show 0.35 s of the wrong level on every seek.

---

### Remap algorithm (design asked for one)

Runs inside `MapSpace.Rebuild(...)` whenever `FloorSplitter`'s slice list is observed to differ.

```
INPUT   old: IReadOnlyList<MapLevel>            (current levels, each with a stable Id)
        raw: IReadOnlyList<FloorSlice>          (FloorSplitter.Slices, ordered low→high)
OUTPUT  new: IReadOnlyList<MapLevel>            (ordered low→high, ids carried where possible)
        change: LevelSetChange                  (Added / Removed / Remapped / anchor remap fn)

1. Quantize:  foreach raw slice s → zMin = Q(s.MinZ), zMax = Q(s.MaxZ), where
              Q(z) = Math.Floor(z / 64.0 + 0.5) * 64.0.
              Drop degenerate bands where zMax <= zMin (can only arise from a malformed
              authoritative bundle) by widening zMax to zMin + 64.
2. Score:     for every (oldLevel o, newBand n) pair compute
              overlap = max(0, min(o.ZMax, n.ZMax) - max(o.ZMin, n.ZMin))
              score   = overlap / min(o.Span, n.Span)                    // ∈ [0,1]
3. Match:     take pairs in descending score, greedily, one-to-one, accepting a pair only while
              score >= 0.50. (Half of the thinner band must be shared. A boundary drifting by one or
              two buckets moves score by < 0.05 on any real band, so identity survives drift; a
              genuine 1→2 floor split scores < 0.5 on at least one side, so the new floor is Added.)
4. Carry:     matched n inherits o.Id → record in change.Remapped[o.Id] = n.Id (identity entry).
              Unmatched o → change.Removed.
5. Mint:      unmatched n gets Id = new MapLevelId(key) where key = (int)(Q(n.ZMin) / 64.0);
              while the key is already used by a level in `new` OR by any level ever minted for this
              MapSpace instance (a monotonically-growing HashSet<int> `_usedKeys`), key++.
              Record in change.Added.
6. Name:      Name = "L{ordinal}" by ascending ZMin ordinal, i.e. display names re-order freely;
              Ids never do. (Names are display-only; nothing persists them.)
7. Radar:     rebind per level — see "Radar binding" below.
8. Anchors:   change.TryRemapAnchor(oldLevelMinZ, out newLevelMinZ):
              a. if some new level contains oldLevelMinZ → that level's ZMin;
              b. else if the old level whose ZMin == oldLevelMinZ was matched → its successor's ZMin;
              c. else nearest new level by |MidZ - oldLevelMinZ| (mirrors FloorSplitter.SliceIndexFor's
                 nearest-by-mid fallback, :249-262);
              d. false only when there are no levels at all (pre-first-observation).
9. Panes:     LevelPaneStore.Reconcile carries SliceCamera + Rig + ManualOverride forward by Id;
              a level in change.Added gets a fresh Fit camera (mirrors Playback2DViewport.cs:513-516);
              a level in change.Removed has its pane state dropped.
10. Buffers:  LevelCrossingTracker.Reset() — every cached per-entity level assignment is stale, so
              the next frame re-resolves and (correctly) does not report a crossing for entities that
              merely got re-keyed. Trail/smoothing buffers are NOT reset by a rebuild (positions did
              not move); they reset only on a real crossing or a discontinuity.
```

Complexity is O(|old| × |new|), and both are ≤ ~4. Rebuild frequency is bounded by "the slice list
actually changed", which the sticky-count rule (`FloorSplitter.cs:104`) makes rare and monotone.

---

### Radar binding (kills `ResolveRadarImage`)

At build/rebuild, for each `MapLevel`, score every `RadarLayerDto` (`MinZ, MaxZ, Image`) by band
overlap fraction (same formula as step 2) and bind the best-scoring layer with overlap > 0. If the
bundle has **no** `RadarLayers` but has `RadarImages`, bind `RadarImages[0]` to **every** level (the
single-radar map case — one picture is correct for all floors). If nothing binds, the level's
`Radar` is `null` and `HasRadar` is false — **and the UI says so** (level strip chip shows a
"no radar" glyph + tooltip; `RadarLayer` falls back to the grid, which it already does at
`Playback2DViewport.cs:1074-1077`). No LINQ, no per-frame work, no silent fallback to "the highest
layer" (`:1114`). The decode itself (`MapAssetPipeline`, Pipeline project, per design §4) is B0/B1's;
B3 supplies the *binding*, and re-runs it on every rebuild.

---

### UI placement (design asked for a call)

The four viewport corners are taken: overlays top-left (`Playback2DView.axaml:235`), LiveSync + kill
feed top-right (`:146`), transport/status bottom-left (`:90`), and A1's `TimelineControl` claims the
whole bottom edge (design §5.6: it "absorb[s] the current bottom status bar"). The remaining free
real estate is the **right-centre gutter**.

```
┌──────────────────────────────────────────────────┬──────────┐
│ [overlays ☑radar ☑trails …]        [● live sync] │          │
│                                    [ kill feed ] │  roster  │
│                                                  │    +     │
│                                       ┌────────┐ │ game info│
│                 viewport              │  ⬒ L2  │ │  (320px) │
│                                       │  ▣ L1  │ │          │
│                                       │  ⌾ L0⃠ │ │          │  ⃠ = no radar
│                                       ├────────┤ │          │
│                                       │  AUTO  │ │          │
│ [Fit ▾] mode: Follow  …               └────────┘ │          │
├──────────────────────────────────────────────────┴──────────┤
│ A1 TimelineControl (rounds band + scrub + markers)          │
└─────────────────────────────────────────────────────────────┘
```

Specifics: `HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,64,10,64"` inside the
viewport `Grid` cell (column 0), so it clears the kill feed's growth downward and the transport bar's
growth upward without either needing to know about it. Buttons are 34×28, highest level first
(descending `ZMin`) to match the stacked-band ordering users already learned. A `LEVELS`/`STACK`
toggle button sits at the top of the strip (switches `LevelDisplayMode`); the `AUTO` chip sits at the
bottom, separated by a divider, and is hidden when `playback2d.levels.auto` is off (D8). The strip is
collapsed entirely when there is ≤ 1 level (D9). It is interactive, so — unlike the top-right HUD
stack (`:147 IsHitTestVisible="False"`) — it must **not** set `IsHitTestVisible="False"`; and because
it sits over the canvas, `Scene2DHost`'s pointer routing must not receive its clicks (Avalonia's
normal z-order hit-testing handles this: the strip is a later sibling in the `Grid` cell).

---

## Ordered work breakdown

Ordering constraints are stated per task. T1 → T2/T3/T4 can then run in parallel; T5 needs T1; T6
needs T2+T4; T8 needs T1; T9 is independent of T1–T8 and can start any time after A1 lands.

Paths marked **(new)** are created by this phase; others are modified.

### T1 — Level identity, quantization, and rebuild/remap (≈0.5 d) · *no dependencies beyond B1*

**Create**
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapLevelId.cs` (new)
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/LevelSetChange.cs` (new)

**Modify**
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapSpace.cs` — add `LevelQuantum`,
  `QuantizeZ`, `Rebuild`, `LastChange`, `_usedKeys`, and the sticky `LevelFor` overload. Keep the
  design's `IReadOnlyList<MapLevel> Levels`, `MapLevel LevelFor(double)`, `event Action? LevelSetChanged`
  members exactly as sketched.
- `src/Playback2D/DemoViewer.NET.Playback2D.Core/Levels/MapLevel.cs` — add `Id`, `Span`,
  `HasRadar`, `RadarImageName`, `Contains(double)`, `MidZ`.

Implement the remap algorithm above verbatim. `Rebuild` returns `LevelSetChange` **and** raises
`LevelSetChanged` (design's no-arg event) after `LastChange` is assigned, so a handler can read the
change off the property — do not raise before assigning.

`Rebuild` must be **idempotent**: if the incoming quantized band list is element-wise equal to the
current one, return `LevelSetChange.None`, raise nothing, and touch nothing. The caller (Pipeline's
`MapAssetPipeline`/`SceneFrameBuilder`) may call it every frame; today's viewport pulls
`SetAuthoritativeFloors` every push (`Playback2DViewport.cs:340`) for exactly the same reason.

### T2 — Hysteresis + AutoFollow selection (≈0.5 d) · *needs T1*

**Create**
- `.../Core/Levels/LevelHysteresis.cs` (new) — spatial band + dwell, per D4.
- `.../Core/Levels/LevelSelection.cs` (new) — the `Manual | AutoFollow` state machine that produces
  the active `MapLevelId` for `SingleLayout`.

`LevelSelection.Update` is the only place that reads the followed player's Z. It takes the followed
slot (or `null`), finds that slot's marker in `Scene2DFrame`, and feeds `LevelHysteresis`. When the
followed marker is absent it **holds** the current level (mirrors `TryFollow`'s graceful-orphan
behaviour, `Playback2DViewport.cs:816`); it does not fall back to level 0. A manual pick sets
`Mode = Manual`; re-arming AUTO sets `Mode = AutoFollow` and clears the dwell so the switch is
immediate.

### T3 — Level crossings and buffer resets (≈0.5 d) · *needs T1*

**Create**
- `.../Core/Levels/LevelCrossingTracker.cs` (new)

**Modify**
- `.../Core/Layers/MarkerLayer.cs` — in `Advance`, before smoothing: if
  `ctx.LevelCrossings.Crossed(slot)`, drop that slot's smoothed position so the next sample **snaps**.
  This is the same code path as the existing teleport snap (`Playback2DViewport.cs:668-673`) — reuse
  it, do not add a second snap mechanism.
- `.../Core/Layers/TrailLayer.cs` — on a crossing for a trail's owning entity, truncate the retained
  point buffer at the crossing sample (do not interpolate across the cut). Grenade trails already
  split their *drawing* across floors (`FloorSegmentRuns`, `Playback2DViewport.cs:1293-1296`); this
  adds the *buffer* half.
- `.../Core/SceneRenderContext.cs` — expose `LevelCrossings` and `Levels` (the `MapSpace`) so layers
  need no VM reference (design §5.2 purity).
- `.../Core/Layers/…` — any other layer holding per-entity temporal state adopts the same check.
  Audit list at time of writing: marker smoothing and grenade trails only.

Reset points for `LevelCrossingTracker`: `SceneTime.IsDiscontinuity`, demo/map change, and
`MapSpace.LevelSetChanged` (step 10 of the remap algorithm).

### T4 — `SingleLayout` + pane identity (≈0.5 d) · *needs T1*

**Create**
- `.../Core/Levels/SingleLayout.cs` (new) — `ILevelLayoutPolicy` returning exactly one pane covering
  the whole host rect, for the active level (falling back to the top-most level when the active id is
  unknown — e.g. immediately after a rebuild that removed it).
- `.../Core/Levels/LevelPaneStore.cs` (new) — carries `SliceCamera` / `Rig` / `ManualOverride`
  forward across `Arrange` calls **by `MapLevelId`**, replacing `EnsureCameras`'s by-index carry
  (`Playback2DViewport.cs:505-511`). New levels get `ViewportTransform.Fit(...)` over the observed
  extent, exactly as `:515`.

**Modify**
- `.../Core/Levels/LevelDisplayMode.cs` (created by B1 if `StackedLayout` shipped there; otherwise
  new) — ensure members `Stacked`, `Single`, and a reserved `SideBySide` that no factory returns yet.
- `.../Core/Levels/LevelLayouts.cs` (new or B1's) — `ILevelLayoutPolicy For(LevelDisplayMode)` factory.

Switching `Stacked → Single` must not lose the user's per-level pan/zoom: the store keeps state for
levels that are not currently arranged, keyed by id, and drops it only on `Removed`.

### T5 — Explicit per-level radar binding + no-radar state (≈0.5 d) · *needs T1*

**Modify**
- `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/MapAssetPipeline.cs` — add
  `BindRadars(MapSpace space, MapAssetBundle bundle, IReadOnlyDictionary<string, SKImage> decoded)`
  implementing "Radar binding" above; call it from the `MapSpace` factory **and** from the
  `LevelSetChanged` handler.
- `.../Core/Layers/RadarLayer.cs` — draw `pane.Level.Radar` (no resolution logic, no LINQ, no
  `sliceIndex`); when null, skip and let the grid show through, as today (`:1074-1077`).
- Delete `ResolveRadarImage` from the port target list — B1's `RadarLayer` must not carry it forward.
  If B1 already ported it, remove it in this task and update B1's goldens (they will be unchanged on
  single-radar maps, which is every current golden fixture).

### T6 — App: level strip UI + wiring (≈1 d) · *needs T2, T4*

**Create**
- `src/App/DemoViewer.NET/Modules/Playback2D/Levels/LevelStripViewModel.cs` (new)
- `src/App/DemoViewer.NET/Modules/Playback2D/Levels/LevelChipViewModel.cs` (new)

**Modify**
- `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml` — add the strip as the last child of
  the column-0 `Grid` (after the overlays `Border` at `:235-253`), per "UI placement". Use existing
  tokens only: `Pb2dOverlayBg` for the strip background, `Pb2dTextBright` / `Pb2dTextDim` for chip
  text, `Pb2dPositive` for the active chip, `Pb2dTextDim` for the no-radar glyph. **No new theme
  tokens** (they would need adding to both light and dark palettes; not worth it for this phase).
- `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml.cs` — no code-behind needed if the
  chips bind to `LevelStripViewModel.SelectCommand`; prefer that over click handlers (the existing
  mode menu uses handlers only because it rebuilds a `MenuFlyout` on open, `:40-44`).
- `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs` — expose `Levels`
  (`LevelStripViewModel`), and forward the followed slot into `LevelSelection`. The followed-slot
  source is A1's selectable player cards (design §7.4); until A1 lands, wire it to the existing
  `FollowSlot` setter path (`Playback2DViewport.cs:195-203`).

The strip's `IsVisible` binds to `LevelStripViewModel.HasMultipleLevels` (D9); the AUTO chip's to
`IsAutoAvailable` (feature gate, D8). Chip tooltip shows `z[{ZMin:F0}..{ZMax:F0}]`, matching the band
label format already used at `Playback2DViewport.cs:587`.

### T7 — Settings + feature gate (≈0.25 d) · *needs T6*

**Modify**
- `src/App/DemoViewer.NET/Configuration/AppSettings.cs` — add to B2's `Playback2DSettings`:
  `LevelDisplayMode` (string, `"Stacked"`), `AutoFollowLevel` (bool, `true`). If B2 has not yet
  created `Playback2DSettings`, create it here with just these two properties and a
  `public Playback2DSettings Playback2D { get; set; } = new();` on `AppSettings` (B2 then extends it).
- `src/App/DemoViewer.NET/Configuration/SettingsService.cs` — add both keys to `WriteInMemory`
  (`:419-448`) as `"Playback2D:LevelDisplayMode"` / `"Playback2D:AutoFollowLevel"`. **Non-negotiable:**
  the comment at `:410-418` documents that an unflattened key is silently discarded on WASM reload,
  and the 2D playback tab is fully WASM-reachable (design §8).
- `src/App/DemoViewer.NET/Features/FeatureCatalog.cs` — add one `SubFeature` descriptor with
  `ParentId = "tab.playback2d"`, id **`playback2d.levels.auto`** (a persisted key — never rename),
  label "Auto level switching", defaults visible to all three categories (it is a viewing-surface
  convenience, matching `tab.playback2d`'s `Defaults(true, true, true)` at `:49`). Insert it after the
  existing playback2d sub-features so no group-leader ordering is disturbed (`FeatureCatalog.cs:31-34`).

### T8 — Annotation anchor remap on rebuild (≈0.5 d) · *needs T1; needs B2's document*

**Create**
- `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Annotations/AnnotationLevelRemapper.cs` (new)

Subscribes to `MapSpace.LevelSetChanged`, reads `MapSpace.LastChange`, and rewrites every
`SpaceRef.World(levelMinZ)` whose `levelMinZ` no longer names a level, via
`LevelSetChange.TryRemapAnchor`. Per D6: mutate through an `AnnotationDocument.ApplyMigration(...)`
entry point that bumps `Version` and raises `Changed` but does **not** touch the undo stack — B2 must
expose it (see "Dependencies"). If B2's `AnnotationDocument` has no such entry point, add it in this
task as a 6-line method next to `Apply`.

Also: newly-created annotations must stamp the **quantized** `ZMin` (`MapSpace.QuantizeZ`), not the
raw slice `MinZ` — otherwise an anchor written before a rebuild can miss its own level by a fraction.
Verify B2's draw tool does this; fix if not.

### T9 — `AnnotationTrack` envelope drag handles (≈1 d) · *needs A1's timeline + B2's document*

**Create**
- `.../Core/Timeline/TickAxis.cs` (new) — pure tick↔pixel mapping.
- `.../Core/Timeline/EnvelopeDrag.cs` (new) — hit-test + pure drag session (preview + commit).
- `src/App/DemoViewer.NET/Modules/Playback2D/Timeline/AnnotationTrackInteraction.cs` (new) — the
  Avalonia-side pointer plumbing that drives `EnvelopeDrag` and paints handles.

**Modify**
- A1's `TimelineControl` (path per A1's plan; expected
  `src/App/DemoViewer.NET/Controls/TimelineControl.axaml{,.cs}`) — add an interaction overlay layer
  above the marker canvas for the annotation track, and route pointer press/move/release into
  `AnnotationTrackInteraction`.

Behaviour, locked here so B2/A1 don't re-litigate it:

- Each anchored element renders a body bar spanning `[FromTick, UntilTick]` with two 6px grab handles
  (start/end) and an 8px pointer slop; a static element (`TimeEnvelope.Static`, both bounds null)
  renders as a point marker with **no** handles.
- Dragging **Start** moves `FromTick`; **End** moves `UntilTick`; **Body** moves both (preserving
  duration). Clamp to `[firstTick, lastTick]` of the loaded demo and enforce
  `UntilTick - FromTick >= 1`. Fade ticks are preserved but clamped to ≤ half the resulting span.
- One drag = one undo entry: `doc.BeginGesture("edit annotation time")` on press, `Apply(delta)` on
  release, `Dispose` closes the mark; Esc mid-drag calls the gesture's `Bail()` (design §5.4) so no
  undo entry is created — the same contract as `IPointerTool.OnCancelled` (§5.5).
- Dragging does **not** seek. A double-click on the body seeks to `FromTick` via
  `RequestSeekToFrame` (design §5.6 — frame index is the movement contract; convert with A1's
  tick→frame mapper, never by scanning).

### T10 — Tests, fixtures, goldens (≈1 d) · *last*

See "Test plan". Write the direct-execution Core tests alongside each task (T1–T5, T9), and the
headless-Avalonia tests (T6) after the XAML lands.

---

## Public API contracts

**Binding for other phases.** Namespaces assume B0/B1's root namespace
`DemoViewer.NET.Playback2D.Core`. Style per repo `.editorconfig`: file-scoped namespaces, explicit
types (no `var`), Allman braces, 4-space indent, 120-col limit, `#region`-wrapped usings.

### `Core.Levels` — identity

```csharp
namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>Opaque, stable identity for one map level. Minted from a quantized ZMin, then CARRIED
/// across MapSpace rebuilds by band overlap — never re-derived from Z after minting.</summary>
public readonly record struct MapLevelId(int Key)
{
    public static MapLevelId None => new(int.MinValue);
    public bool IsNone => Key == int.MinValue;
    public override string ToString() => IsNone ? "none" : $"lv{Key}";
}
```

```csharp
/// <summary>One level of a map: a contiguous world-Z band with an optional bound radar image.</summary>
public sealed class MapLevel
{
    public required MapLevelId Id { get; init; }
    public required string Name { get; init; }          // display only; may change across rebuilds
    public required double ZMin { get; init; }          // quantized to MapSpace.LevelQuantum
    public required double ZMax { get; init; }          // quantized; always > ZMin
    public SKImage? Radar { get; internal set; }        // bound at (re)build; null = no radar
    public string? RadarImageName { get; internal set; }
    public bool HasRadar => Radar is not null;
    public double Span => ZMax - ZMin;
    public double MidZ => (ZMin + ZMax) / 2;
    public bool Contains(double z) => z >= ZMin && z <= ZMax;
}
```

```csharp
/// <summary>What one MapSpace rebuild did to the level set. Read from MapSpace.LastChange inside a
/// LevelSetChanged handler.</summary>
public sealed class LevelSetChange
{
    public static LevelSetChange None { get; }
    public bool IsEmpty { get; }
    public IReadOnlyList<MapLevel> Added { get; }
    public IReadOnlyList<MapLevel> Removed { get; }
    /// <summary>Old id → surviving new id, for every level that was matched across the rebuild.</summary>
    public IReadOnlyDictionary<MapLevelId, MapLevelId> Remapped { get; }
    /// <summary>Rebases a SpaceRef.World(LevelMinZ) annotation anchor onto the new level set.
    /// False only when the space has no levels at all.</summary>
    public bool TryRemapAnchor(double oldLevelMinZ, out double newLevelMinZ);
}
```

### `Core.Levels` — `MapSpace` (extends the design §5.3 sketch)

```csharp
public sealed class MapSpace
{
    /// <summary>World-unit quantum for level identity. 64 == FloorSplitter.BucketWidth, so every
    /// histogram-derived boundary is already an exact multiple of it.</summary>
    public const double LevelQuantum = 64.0;

    public static double QuantizeZ(double z) => Math.Floor(z / LevelQuantum + 0.5) * LevelQuantum;

    // ── design §5.3 sketch, preserved verbatim ──
    public IReadOnlyList<MapLevel> Levels { get; }
    public MapLevel LevelFor(double worldZ);            // stateless: containing, else nearest by MidZ
    public event Action? LevelSetChanged;

    // ── B3 additions ──
    /// <summary>Sticky resolution: keeps <paramref name="previous"/> until worldZ is at least the
    /// hysteresis band past the shared boundary. See LevelHysteresis.SpatialBand.</summary>
    public MapLevel LevelFor(double worldZ, MapLevelId? previous);

    /// <summary>Re-derives the level set from FloorSplitter's current slices. Idempotent: an
    /// unchanged (quantized) band list returns LevelSetChange.None and raises nothing.
    /// Correction 3: takes FloorSlice (B1 moved FloorSplitter into Core) and carries the radar
    /// binding B1/Pipeline computed, so there is one Rebuild call shape for both phases.</summary>
    public LevelSetChange Rebuild(IReadOnlyList<FloorSlice> bands,
        IReadOnlyList<SKImage?>? radarByLevel = null,
        RadarBindingQuality quality = RadarBindingQuality.None);

    /// <summary>The most recent non-empty change; valid to read inside a LevelSetChanged handler.</summary>
    public LevelSetChange LastChange { get; }

    public MapLevel? ById(MapLevelId id);
    public int IndexOf(MapLevelId id);                  // -1 when absent
}
```

> **Resolved:** B1 T1 moves `FloorSplitter`/`FloorSlice` into `…Core.Levels`, so `Rebuild` takes
> `IReadOnlyList<FloorSlice>` directly (correction 3) and Core still references nothing but SkiaSharp.

### `Core.Levels` — hysteresis and selection

```csharp
public readonly record struct LevelHysteresisOptions(
    double MinBand = 32.0,          // half a histogram bucket — below this, boundary drift re-triggers
    double MaxBand = 128.0,         // two buckets; > the 56.6u CS2 jump apex
    double BandFractionOfSpan = 0.25,
    double DwellSeconds = 0.35)     // ≈ the camera's own 92% settle time at LerpResponse 7.0
{
    public static LevelHysteresisOptions Default => new();
}

/// <summary>Stateful level chooser: spatial sticky band + temporal dwell. Time comes only from
/// SceneTime.DeltaSeconds (no wall clock — design §5.1), so interactive and export agree.</summary>
public sealed class LevelHysteresis
{
    public LevelHysteresis(LevelHysteresisOptions? options = null);
    public MapLevelId Current { get; }
    public double PendingSeconds { get; }               // dwell accumulated toward Pending
    public MapLevelId Pending { get; }                  // candidate awaiting dwell; None when settled

    /// <summary>Advances the chooser one scene frame. Returns the level to display. On
    /// time.IsDiscontinuity the dwell is bypassed and the resolved level adopted immediately.</summary>
    public MapLevelId Update(in SceneTime time, double worldZ, MapSpace space);

    public void Reset();                                // demo change / MapSpace rebuild / manual pick
    public void ForceTo(MapLevelId id);                 // manual pick, or AUTO re-arm

    /// <summary>The spatial half-band between two adjacent levels:
    /// clamp(BandFractionOfSpan × min(spans), MinBand, MaxBand). Pure; unit-tested directly.</summary>
    public static double SpatialBand(MapLevel a, MapLevel b, LevelHysteresisOptions options);
}
```

```csharp
public enum LevelSelectionMode { Manual, AutoFollow }

/// <summary>Owns "which level does SingleLayout show". Manual pick pins it; AutoFollow tracks the
/// followed player's Z through LevelHysteresis. Holds the current level when the followed marker is
/// absent (mirrors the viewport's graceful-orphan follow, Playback2DViewport.cs:816).</summary>
public sealed class LevelSelection
{
    public LevelSelection(MapSpace space, LevelHysteresisOptions? options = null);
    public LevelSelectionMode Mode { get; }
    public MapLevelId ActiveLevelId { get; }
    public int? FollowedSlot { get; set; }              // null = nothing followed
    public event Action? ActiveLevelChanged;

    /// <summary>Call once per scene frame, before layout. Returns true when the active level changed.</summary>
    public bool Update(in SceneTime time, Scene2DFrame frame);

    public void PickManually(MapLevelId id);            // → Mode = Manual
    public void EnableAutoFollow();                     // → Mode = AutoFollow, dwell cleared
    public void OnLevelSetChanged();                    // subscribe to MapSpace.LevelSetChanged
}
```

### `Core.Levels` — crossings

```csharp
/// <summary>Per-entity level assignment with change detection, so layers can drop temporal buffers
/// exactly when an entity changes level (the boltobserv streak-across-the-map pitfall, design §5.3).
/// Keyed by player SLOT (PlayerMarker carries no SteamId).</summary>
public sealed class LevelCrossingTracker
{
    /// <summary>Resolves and records this frame's level for one entity (sticky spatial band, NO dwell
    /// — a marker must never lag its own level). Returns true when it changed since the last frame.</summary>
    public bool Update(int slot, double worldZ, MapSpace space);

    /// <summary>True when Update reported a change for this slot on the CURRENT frame.</summary>
    public bool Crossed(int slot);

    public MapLevelId LevelOf(int slot);                // MapLevelId.None when unknown
    public IReadOnlyCollection<int> CrossedSlots { get; }

    /// <summary>Clears the per-frame crossing set. Called by the compositor after all layers Advance.</summary>
    public void EndFrame();

    /// <summary>Drops all assignments: demo change, MapSpace rebuild, or SceneTime.IsDiscontinuity.</summary>
    public void Reset();
}
```

### `Core.Levels` — layout

```csharp
public enum LevelDisplayMode { Stacked, Single, SideBySide /* reserved — no policy returns this */ }

/// <summary>One pane covering the whole host, showing exactly one level.</summary>
public sealed class SingleLayout : ILevelLayoutPolicy
{
    public MapLevelId ActiveLevelId { get; set; }       // driven by LevelSelection.ActiveLevelId
    public IReadOnlyList<LevelPane> Arrange(MapSpace space, LevelDisplayMode mode, SKSize host);
}

public static class LevelLayouts
{
    public static ILevelLayoutPolicy For(LevelDisplayMode mode);   // SideBySide → throws NotSupported
}

// Correction 4: NOT a new type. These are members B3 ADDS to B1's PaneSet, which already
// reconciles by MapLevel.Id and carries SliceCamera/ManualOverride/Rig across a rebuild. What is
// new is retention across a LAYOUT change (Stacked ⇄ Single), where a level is temporarily not
// arranged at all and must not lose the user's pan/zoom.
public sealed partial class PaneSet   // …Core.Levels, declared by B1
{
    /// <summary>Keeps camera state for levels that are not currently arranged; dropped only when
    /// the level appears in LevelSetChange.Removed.</summary>
    public void RetainUnarranged(LevelSetChange change);
    public void ResetAll();                             // "Fit" command — clears every ManualOverride
    public bool TryGetCamera(MapLevelId id, out SliceCamera camera);
}
```

### `Core.Timeline` — envelope editing (T9)

```csharp
namespace DemoViewer.NET.Playback2D.Core.Timeline;

/// <summary>Pure tick↔pixel mapping used INSIDE envelope drag math. No Avalonia, no state.
/// Correction 6: A1's timeline lays out on the FRAME-INDEX axis, so this type must not be used to
/// position anything in TimelineControl — the App constructs one per drag from A1's
/// XForFrame/FrameIndexAt plus ITimelineData.FrameIndexAtTick, and converts back at the seam.</summary>
public readonly record struct TickAxis(int FirstTick, int LastTick, double PixelWidth)
{
    public double XOf(int tick);
    public int TickAt(double x);                        // clamped to [FirstTick, LastTick]
    public double TicksPerPixel { get; }
}

public enum EnvelopeHandleKind { Start, End, Body }

public readonly record struct EnvelopeHandle(Guid ElementId, EnvelopeHandleKind Kind);

public static class EnvelopeHitTest
{
    /// <summary>Topmost handle under x for the given row, or null. slopPixels defaults to 8.</summary>
    public static EnvelopeHandle? At(IReadOnlyList<AnnotationElement> elements, double x,
        in TickAxis axis, double slopPixels = 8.0);
}

/// <summary>One in-progress envelope drag. Pure: Preview never mutates the document; Commit produces
/// the invertible delta the caller applies inside an AnnotationDocument gesture.</summary>
public sealed class EnvelopeDragSession
{
    public static EnvelopeDragSession? Begin(AnnotationElement element, EnvelopeHandleKind kind,
        int grabTick, int minTick, int maxTick);
    public Guid ElementId { get; }
    public TimeEnvelope Preview(int tick);              // clamped, min 1-tick span, fades re-clamped
    public DocDelta Commit(int tick);                   // replace-delta for the element
}
```

### App layer

```csharp
namespace DemoViewer.NET.Modules.Playback2D.Levels;

public sealed partial class LevelStripViewModel : ObservableObject
{
    public ObservableCollection<LevelChipViewModel> Chips { get; }   // ordered HIGHEST first
    public bool HasMultipleLevels { get; }               // strip IsVisible
    public bool IsAutoAvailable { get; }                 // feature gate playback2d.levels.auto
    public bool IsAutoEnabled { get; set; }              // AUTO chip
    public LevelDisplayMode DisplayMode { get; set; }    // persisted to Playback2DSettings
    public IRelayCommand<LevelChipViewModel> SelectCommand { get; }
    public IRelayCommand ToggleDisplayModeCommand { get; }
}

public sealed partial class LevelChipViewModel : ObservableObject
{
    public MapLevelId Id { get; }
    public string Label { get; }                         // "L2"
    public string ZRange { get; }                        // "z[-416..-111]" — same format as :587
    public bool HasRadar { get; }
    public string RadarTooltip { get; }                  // "no baked radar for this level" when false
    public bool IsActive { get; }
}
```

---

## Test plan

Two execution modes, per design §11: **direct-execution** Core tests (no Avalonia platform at all,
strictly preferred) and **headless-Avalonia** for the XAML strip only.

### Direct-execution — `src/Playback2D/DemoViewer.NET.Playback2D.Tests/`

Created by B0; if absent, create per "Build & wiring". TUnit, `[Test]`, `await Assert.That(...)`.

| Class | Cases |
|---|---|
| `MapSpaceQuantizationTests` | `QuantizeZ_IsIdentity_OnHistogramBoundaries` (feed `k*64` for k ∈ [-40,40]); `QuantizeZ_RoundsHalfUp_Symmetrically` (−96, −32, 0, 32, 96); `Rebuild_IsIdempotent_ForEqualBands` (no event, `LevelSetChange.None`) |
| `MapSpaceRemapTests` | `BoundaryDrift_OneBucket_PreservesIds` (bands `[0,640]/[640,1280]` → `[0,704]/[704,1280]`; both ids unchanged, `Added`/`Removed` empty); `SplitOneIntoTwo_KeepsLowerId_AddsUpper`; `MergeTwoIntoOne_RemovesOne`; `MintedKeys_NeverCollide_AfterRemoveThenAdd`; `TryRemapAnchor_Containing_Nearest_And_Empty` |
| `LevelHysteresisTests` | `SpatialBand_ClampsToMin_OnThinBands`; `SpatialBand_ClampsToMax_OnWideBands`; `JumpApex_56u_DoesNotSwitch_AtMaxBand`; `Dither_AcrossBoundary_DoesNotSwitch` (alternate ±10u for 2 s of scene time at dt = 1/64); `SustainedCrossing_SwitchesAfter_0_35s` (assert not switched at 0.34 s, switched at 0.36 s); `Discontinuity_SwitchesImmediately`; `Dwell_IsFrameRateIndependent` (same result at dt = 1/30 and dt = 1/144) |
| `LevelSelectionTests` | `AutoFollow_HoldsLevel_WhenFollowedMarkerAbsent`; `ManualPick_PinsLevel_AgainstFollowedPlayerMove`; `EnableAutoFollow_ClearsDwell`; `LevelSetChanged_WithRemovedActive_FallsBackToTopMost` |
| `LevelCrossingTrackerTests` | `Crossed_TrueOnlyOnTheFrameOfChange`; `EndFrame_ClearsCrossedSet`; `Reset_OnRebuild_DoesNotReportPhantomCrossings` |
| `LevelPaneStoreTests` | `CameraSurvives_LevelInsertedBelow` — the regression `EnsureCameras` cannot pass: build 1 level, pan it, rebuild with a NEW LOWER level, assert the panned camera is still on the original level and the new one is `Fit`; `ManualOverride_SurvivesStackedToSingleAndBack`; `RemovedLevel_DropsState` |
| `SingleLayoutTests` | `Arrange_ReturnsExactlyOnePane_CoveringHost`; `UnknownActiveId_FallsBackToTopMost` |
| `RadarBindingTests` | `BindsByOverlap_NotByCount` — the direct replacement for `ResolveRadarImage`'s count-match: 2 levels × 3 radar layers must still bind correctly; `SingleRadarImage_BindsToEveryLevel`; `NoOverlap_LeavesHasRadarFalse` |
| `EnvelopeDragTests` | `StartHandle_ClampsTo_MinOneTickSpan`; `BodyDrag_PreservesDuration`; `Drag_ClampsToDemoBounds`; `Fades_ClampedToHalfSpan`; `HitTest_PrefersHandleOverBody_WithinSlop`; `StaticElement_ExposesNoHandles` |
| `LevelGoldenTests` | Renders fixture scenes to the `CpuSurfaceProvider` and compares to goldens (below) |

Run: `dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Debug`
(TUnit test projects are `OutputType=Exe`). Filter one class with
`-- --treenode-filter "/*/*/MapSpaceRemapTests/*"`.

### Fixtures and goldens

- **Scene fixtures** (`tests/fixtures/playback2d/`, the corpus design §11 establishes):
  - `nuke-two-level.json` — two levels ~140u apart, 10 markers split across both, one mid-stairwell.
  - `nuke-two-level-noradar.json` — same, with the upper level's radar binding removed, to pin the
    visible no-radar state.
  - `mirage-single-level.json` — the strip-hidden / single-pane baseline.
- **Goldens** (`tests/fixtures/playback2d/golden/`, CPU provider, PNG, authored on the CPU provider
  per design §5.8): `single-layout-L0.png`, `single-layout-L1.png`, `single-layout-noradar.png`,
  `stacked-two-level.png` (must be **byte-identical to B1's existing stacked golden** — proving
  `SingleLayout` and the id-keyed pane store changed nothing about the stacked path).
- A real-demo probe (integration, skippable): reuse the existing pattern in
  `src/App/DemoViewer.NET.App.Tests/FloorSplitterMultiFloorTests.cs` —
  `DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem")`, `throw new SkipTestException(...)`
  when absent.

### Real-demo integration — `src/App/DemoViewer.NET.App.Tests/Playback2DLevelStabilityTests.cs` (new)

| Case | Assertion |
|---|---|
| `Nuke_LevelIds_AreStable_AcrossTheWholeDemo` | Replay Nuke forward, feeding `MapSpace.Rebuild` from `FloorSplitter.Slices` at every checkpoint (mirrors `FloorSplitterMultiFloorTests.AccumulateFloorCounts`); assert the id of the level containing a fixed reference Z never changes, and that ids are never reused for a different band. **This is the risk-5 gate.** |
| `Nuke_AutoFollow_SwitchCount_IsBounded` | Follow one player across a full round; assert the AutoFollow level changes ≤ 1 per genuine stairwell traversal and never more than ~20× per round (a dither regression shows up as hundreds) |
| `Dust2_StaysSingleLevel_StripHidden` | `HasMultipleLevels == false` for the whole demo |

### Headless-Avalonia — `src/App/DemoViewer.NET.App.Tests/Playback2DLevelStripTests.cs` (new)

Uses `HeadlessSession.RunOnUi(...)` and `[NotInParallel]`, per the existing
`Playback2DHeadlessSmokeTests` / `ZRadarRenderTests` pattern.

| Case | Assertion |
|---|---|
| `Strip_IsCollapsed_OnSingleLevelMap` | Strip control `IsVisible == false` |
| `Strip_OrdersChips_HighestFirst` | `Chips[0].Id` is the highest-`ZMin` level (matches `:583`) |
| `Strip_DoesNotOverlap_TimelineOrKillFeed` | Measured bounds of the strip do not intersect the timeline row's or the kill-feed stack's bounds at 1100×650 and at 700×420 |
| `ManualPick_SwitchesPane_AndDisablesAuto` | After clicking chip L0, `IsAutoEnabled == false` and the single pane's level is L0 |
| `AutoChip_Hidden_WhenFeatureGateOff` | With `playback2d.levels.auto` overridden off |
| `NoRadarChip_ShowsGlyphAndTooltip` | Bound to the no-radar fixture |

Run: `bash scripts/test-app-suite.sh -c Debug` (the App suite must run as batched processes — a
single process OOMs, see the script header) or, for one class,
`dotnet run --project src/App/DemoViewer.NET.App.Tests -c Debug -- --treenode-filter "/*/*/Playback2DLevelStripTests/*"`.

### Full-repo verification

```
dotnet build DemoViewer.NET.slnx -c Release          # TreatWarningsAsErrors + EnforceCodeStyleInBuild
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
bash scripts/test-app-suite.sh -c Release
dotnet run --project tools/DemoViewer.NET.Playback2D.Cli -c Release -- bench --fixture nuke-two-level.json --frames 2000 --cpu
```

The last command is C1's; if C1 has not landed, run B1's `dv2d bench` harness equivalent. B3 must not
regress the §6 budget: the per-frame level work is one `LevelSelection.Update` (a dictionary lookup
and two doubles) plus one `LevelCrossingTracker.Update` per marker — **allocation-free**, asserted by
the existing zero-allocation test over a 512-frame run.

---

## Build & wiring

**B3 introduces no new shipping project.** It adds files to Core, Pipeline, and the App, and *may*
need to create the Core test project if B0 did not.

### If `DemoViewer.NET.Playback2D.Core.Tests` does not exist, create it

`src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <!-- Distinct from the Core assembly's namespace to avoid type collisions. -->
        <RootNamespace>DemoViewer.NET.Playback2D.CoreTests</RootNamespace>
        <!-- CA1707: test method names conventionally use underscores. -->
        <NoWarn>$(NoWarn);CA1707</NoWarn>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="TUnit"/>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Core\DemoViewer.NET.Playback2D.Core.csproj"/>
        <ProjectReference Include="..\..\Testing\DemoViewer.NET.TestSupport\DemoViewer.NET.TestSupport.csproj"/>
    </ItemGroup>

</Project>
```

Note the deliberate absences: **no `Avalonia*` package references** (that is the architecture-test
contract, design §11) and **no GC tuning** (these tests parse no demos — unlike
`DemoViewer.NET.App.Tests`, which sets `System.GC.ConserveMemory`).

### slnx

`DemoViewer.NET.slnx`, inside the `<Folder Name="/src/Playback2D/">` block B0 creates:

```xml
<Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Core.Tests.csproj"/>
```

(B0 adds the Core and Pipeline `<Project>` lines; B3 only adds the test project if it is missing.)

### `Directory.Packages.props`

B3 adds **no new package**. Version policy note for whoever creates Core (B0) — record it here
because it is a warnings-as-errors trap in this repo:

- `SkiaSharp` must be pinned to **the exact version Avalonia 11.3.12 resolves transitively —
  `2.88.9`** (verified in `artifacts/obj/DemoViewer.NET.App.Tests/project.assets.json`). Root-level
  `WarningsAsErrors` includes `NU1608;NU1605`, so a Core pin above Avalonia's resolved version fails
  the build the moment the App references Core. Confirm before pinning with
  `dotnet list src/App/DemoViewer.NET/DemoViewer.NET.csproj package --include-transitive | grep -i skiasharp`.
- All `PackageReference` items go **without** a `Version=` attribute (central package management).

### CI

`.github/workflows/ci.yml` today runs one step —
`dotnet build src/App/DemoViewer.NET.Desktop -c Release` — and executes **no tests**. B3 does not
change that policy, but it **does** require that the new Core test project is reachable from the
solution build so a compile break is caught. Add nothing to `ci.yml` in this phase; the budget/golden
gates are B1's and C1's to wire. If B1's `dv2d bench` CI job already exists, add
`nuke-two-level.json` to its fixture list — one line, no new job.

### Settings/gates checklist (repeated because it is silently breakable)

- [ ] `Playback2D:LevelDisplayMode` and `Playback2D:AutoFollowLevel` added to
      `SettingsService.WriteInMemory` (`SettingsService.cs:419`) — else WASM writes vanish on reload.
- [ ] `playback2d.levels.auto` added to `FeatureCatalog._catalog` with
      `ParentId = "tab.playback2d"`, appended after existing playback2d sub-features so no group
      leader moves (`FeatureCatalog.cs:31-34`).
- [ ] Feature id never renamed afterwards (design §7.7 — persisted key).

---

## Dependencies

### Consumed from other phases

| Phase | API | Used by |
|---|---|---|
| B0 | `Scene2DFrame` (must expose per-marker `Slot` + `WorldZ`), `SceneTime` (`DeltaSeconds`, `IsDiscontinuity`), `CpuSurfaceProvider`, `SceneFixture` | T2, T3, T10 |
| B1 | `MapSpace` (base shape), `MapLevel`, `LevelPane { Level, Camera, Rig, ViewportRect }`, `ILevelLayoutPolicy.Arrange(MapSpace, LevelDisplayMode, SKSize)`, `StackedLayout`, `SceneRenderContext`, `ISceneLayer`, `MarkerLayer`, `TrailLayer`, `RadarLayer`, `FollowPlayerRig`, `Scene2DHost`, `SliceCamera`, `ViewportTransform` | T1, T3, T4, T5 |
| B1/Pipeline | `MapAssetPipeline` (radar decode → `SKImage` by image name), `SceneFrameBuilder` | T5 |
| B2 | `AnnotationDocument.BeginGesture(string)` / `Apply(DocDelta)` / `Version` / `Changed`, `AnnotationElement`, `TimeEnvelope`, `SpaceRef.World(double LevelMinZ)`, `DocDelta`, `AnnotationTrack : ITimelineTrack`, `Playback2DSettings` | T7, T8, T9 |
| A1 | `TimelineControl` (a pointer-routable overlay layer above its marker canvas), `ITimelineTrack` / `TimelineMarker`, `RequestSeekToFrame(int frameIndex)`, tick→frame mapping, selectable player cards → followed slot | T6, T9 |

**Two things B3 needs that other phases may not have planned:**

1. **B2 must expose a non-undoable migration entry point** on `AnnotationDocument`:
   ```csharp
   /// <summary>Applies a delta WITHOUT creating an undo entry (level-set rebase, schema migration).
   /// Bumps Version and raises Changed.</summary>
   public void ApplyMigration(DocDelta delta);
   ```
   Rationale in D6. If B2 declines, B3 adds it (6 lines next to `Apply`).
2. **A1 must expose a tick↔pixel axis** for its scrub bar. B3 defines `TickAxis` in Core as the
   canonical shape; if A1 already has an equivalent, **A1's wins** and `EnvelopeHitTest`/
   `EnvelopeDragSession` take A1's type instead — flagged for the integrator.

### Exported by B3 (who consumes)

| API | Consumer |
|---|---|
| `MapLevelId`, `MapLevel.Id/Span/HasRadar`, `MapSpace.QuantizeZ` / `LevelQuantum` | B2 (anchor stamping), B4 (export camera scripts naming a level), C1 (`dv2d --level`) |
| `MapSpace.Rebuild` / `LastChange` / `LevelSetChange.TryRemapAnchor` | Pipeline's `SceneFrameBuilder`, `AnnotationLevelRemapper`, B4's `TrackerFrameSource` |
| `LevelSelection`, `LevelHysteresis`, `LevelHysteresisOptions` | App VM; B4 (`CameraScript.FollowPlayer` must pick a level the same way, or export and screen disagree) |
| `LevelCrossingTracker` | Every layer holding per-entity temporal state (B1's `MarkerLayer`, `TrailLayer`; B2's entity-anchored `AnnotationLayer` should hide-on-cross too) |
| `SingleLayout`, `LevelLayouts.For`, `LevelPaneStore` | `Scene2DHost` (B1), `SceneExportSession` (B4), `dv2d render` (C1) |
| `TickAxis`, `EnvelopeHitTest`, `EnvelopeDragSession` | A1's `TimelineControl`; B2's `AnnotationTrack` |
| `LevelStripViewModel` / `LevelChipViewModel` | `Playback2DView.axaml` only |

---

## Risks & spikes

| # | Risk | Mitigation | Time-box |
|---|---|---|---|
| R1 | **Overlap-match threshold (0.50) mis-tuned** — a real 1→2 split gets matched instead of Added, silently welding two floors into one identity | Spike: replay the Nuke demo, log every `Rebuild` with old/new bands and scores to a CSV, eyeball the score distribution and confirm a bimodal gap around 0.5. If the gap is not clean, move the threshold to the observed valley and record the number here. | **3 h** (do this first — it decides T1's constant) |
| R2 | **No genuine multi-floor demo available.** `FloorSplitter.cs:270-274` records that the section-height probe was blocked on exactly this, and `FloorSplitterMultiFloorTests` skips when the Nuke demo is absent. Without it, AutoFollow is untested on real data. | Synthetic fixtures cover the math (all `LevelHysteresisTests` are dt-driven and demo-free). The real-demo tests use `SkipTestException`, matching repo convention. **Do not** loosen the synthetic assertions to compensate. | — |
| R3 | **A1's timeline shape unknown at planning time** (`docs/playback2d-v2/plans/A1-timeline-keybinds-follow.md` does not exist). T9's overlay/pointer-routing assumptions may not fit. | T9 is last and its Core half (`TickAxis`, `EnvelopeHitTest`, `EnvelopeDragSession`) is Avalonia-free and testable with zero timeline dependency. Only `AnnotationTrackInteraction` binds to A1. | Re-read A1's plan before starting T9; **1 h** reconciliation budget |
| R4 | **Hysteresis constants wrong on a map nobody has tested** (extreme thin bands) | All four constants live in `LevelHysteresisOptions` with a `Default`, so retuning is a one-line change with no API break. `SpatialBand` is `public static` and directly unit-tested. | — |
| R5 | **`LevelPane.Camera` is a struct field** in the design sketch (`public SliceCamera Camera;`), so `LevelPaneStore.Reconcile` must write through `LevelPane` references, not copies. A `foreach` over `IReadOnlyList<LevelPane>` copying `pane.Camera` into a local and mutating it is a silent no-op. | Explicit test `CameraSurvives_LevelInsertedBelow` catches it. Keep `LevelPane` a `class` (as the design sketches it) so reference semantics hold. | — |
| R6 | Level strip collides with A1's timeline or the kill feed at small window sizes | `Strip_DoesNotOverlap_TimelineOrKillFeed` asserts at 1100×650 and 700×420 | — |

---

## Acceptance checklist

Design exit criterion: **"Levels shipped."** Decomposed against §5.3, §7.3, §9 and this plan's own
additions.

**From the design (§7.3 / §5.3 / risk 5):**

- [x] `SingleLayout` implements `ILevelLayoutPolicy` and renders exactly one pane; the stacked render is
      **byte-identical after a Stacked → Single → Stacked round trip**
      (`LevelGoldenTests.StackedRender_IsByteIdentical_AfterASingleModeRoundTrip`), and B1's
      `nuke-multilevel` parity gate is unmoved (99.68 % within ±8 — the same number B1 recorded).
- [x] A level strip offers **manual pick** of any level, in the viewport, without overlapping the
      timeline, kill feed, transport bar, or overlay toggles
      (`Strip_DoesNotOverlap_TimelineOrKillFeed` at 1100×650 and 700×420).
- [x] **AutoFollow** switches the displayed level to the followed player's level via
      `MapSpace.LevelFor(z)` with hysteresis; gated by `playback2d.levels.auto`.
- [x] Hysteresis is a documented, tested formula (`SpatialBand` + 0.35 s dwell), frame-rate
      independent (`Dwell_IsFrameRateIndependent` at dt = 1/30 and 1/144), bypassed on
      `IsDiscontinuity`.
- [x] `MapSpace` **rebuilds** with **quantized-`ZMin`** ids that are **stable across boundary drift** —
      `Nuke_LevelIds_AreStable_AcrossTheWholeDemo` passes, and it fails if the bands do *not* drift, so
      it cannot pass vacuously.
- [x] Panes are remapped on rebuild **by id**, preserving pan/zoom/manual-override; a newly-appeared
      level is Fit, never inherits another level's camera (`CameraSurvives_LevelInsertedBelow`).
- [ ] Annotation `SpaceRef.World(LevelMinZ)` anchors are remapped on rebuild, without polluting undo —
      **blocked on B2** (deviation 6). `LevelSetChange.TryRemapAnchor` is implemented and tested; only
      the `AnnotationLevelRemapper` that drives it is missing.
- [x] Marker-smoothing buffers **reset on level crossing** (no streak across the map). Trail buffers do
      **not** — see deviation 3 for why that is the right answer rather than a gap.
- [x] Radar is bound **explicitly per level** at (re)build, by Z-band overlap; a level with no radar
      shows a **visible** no-radar state in the strip and falls back to the grid on the canvas
      (`nuke-multilevel-noradar` golden).
- [x] `ResolveRadarImage` and its per-band LINQ no longer exist in the **v2** render path (B1 already
      replaced it with `MapRadarBinder`; B3 replaced the count-match rule inside it). The pre-v2
      `Playback2DViewport.ResolveRadarImage` still exists behind the legacy escape hatch — deviation 4.
- [ ] `AnnotationTrack` markers carry **drag handles** — **blocked on B2** (deviation 6). The Core half
      that needs no annotation types (`TickAxis`) is shipped.

**Additional (this plan):**

- [x] Core additions have **zero Avalonia references** (`ArchitectureTests` green).
- [x] Zero steady-state allocations added to the per-frame path — the 512-frame budget assertion is
      **0 B/frame** with the crossing tracker wired into `SceneStage` exactly as `Scene2DHost` wires it,
      and `LevelSelection.Update`, `LevelCrossingTracker.Update` and single-mode `PaneSet.Reconcile`
      each carry their own zero-allocation case.
- [x] `Playback2D:LevelDisplayMode` + **`Playback2D:AutoLevelFollow`** (correction 9's name, not the
      body's `AutoFollowLevel`) are in `SettingsService.WriteInMemory`.
- [x] `playback2d.levels.auto` registered in `FeatureCatalog` with `ParentId = "tab.playback2d"`, as the
      third row of the contiguous v2 block, no group leader disturbed.
- [x] Single-level maps show **no** new chrome (`Strip_IsCollapsed_OnSingleLevelMap`).
- [x] `dotnet build DemoViewer.NET.slnx -c Release` clean.
- [x] Playback2D suite green (226/226); the App suite's level, scene-host, settings and gate classes
      green; real-demo tests skip cleanly when a demo is absent. See deviation 8 for the pre-existing
      Windows failures the batch runner reports and deviation 9 for the batch runner itself.

---

## Implementation notes (deviations)

Written at implementation time. Everything not listed here was built as the plan and the
`Integrator corrections` block specify.

### The quantum mints identity; it does not move bands

1. **`MapLevel.ZMin`/`ZMax` stay RAW — quantization is applied to the id key only.** The plan's remap
   algorithm step 1 quantizes the band itself, and `MapLevel.ZMin`'s doc comment said "quantized to
   `MapSpace.LevelQuantum`". Doing that would change **level assignment**: de_nuke's baked nav floors
   are `[-100000, -528]` and `[-528, 100000]`, and `Q(-528) = -512`, so a player standing at Z −520
   would move from the upper floor to the lower one. B1's parity invariant 1 is that
   `MapSpace.LevelIndexFor` answers exactly what `FloorSplitter.SliceIndexFor` answers — it is pinned by
   `MapSpaceTests.LevelIndexFor_MatchesFloorSplitter_OverAZTable` and it is what every golden contains.
   Design §5.3 asks for "stable **quantized-`ZMin` level ids**", which is about identity, not geometry,
   and identity is exactly what `IdForZMin` + overlap-carry provide. Pinned by
   `MapSpaceRemapTests.RebuiltBands_KeepTheirRawZ_SoAssignmentIsUnchanged`.

2. **`LevelHysteresisOptions` is a sealed record CLASS, not a `readonly record struct`.** The plan
   writes it as a record struct whose primary-constructor parameters all have defaults. That does not
   work: `new LevelHysteresisOptions()` on a record struct takes the implicit parameterless struct
   constructor and **zero-initializes**, so every caller silently gets `MinBand 0, DwellSeconds 0` — no
   hysteresis at all, and no error anywhere. It was caught by six failing tests on the first run.
   `Default` is a cached single instance, so the per-frame `SpatialBand` call still allocates nothing.

### Where the plan's mechanism did not fit the code it landed on

3. **Grenade-trail point buffers are NOT truncated on a crossing** (T3's second bullet). The trail
   buffer lives in `SceneFrameBuilder` and its points are *samples of an actual flight path*, not
   smoothed state — a nade thrown from Nuke upper to lower genuinely occupies both bands, and B1's
   parity invariant 4 draws the crossing segment on both, deliberately. Truncating the buffer at the
   crossing would delete the arc the split-drawing exists to show and would move the
   `nuke-multilevel` golden. The defect design §5.3 names — the streak across the map — is
   *interpolation* across a floor change, and the only interpolated per-entity state in the scene is
   `MarkerSmoother`, which now snaps. `LevelCrossingTracker` is exposed on `SceneRenderContext` for
   B2's entity-anchored annotations, which *do* hold temporal state.

4. **The pre-v2 `Playback2DViewport.ResolveRadarImage` is left in place.** T5 says it must "no longer
   exist anywhere in the render path"; it does not exist in the **v2** path (B1 replaced it with
   `MapRadarBinder`, and B3 replaced the count-match rule inside that). Deleting it from the legacy
   control would break the `DV_PLAYBACK2D_RENDERER=legacy` escape hatch, whose entire purpose is to be
   a working A/B against v2 for one release. It goes when the control does, in B5.

5. **`SceneRenderContext.LevelCrossings` is not propagated through `SceneSubmission`.** The registry
   requires the context member and it is there, but the submission crosses to Avalonia's render thread
   and `LevelCrossingTracker` is mutable UI-thread state — publishing it would be a data race for no
   gain, because `EndFrame()` has already cleared the per-frame set by the time `Render` runs. The
   production consumer is `MarkerSmoother.LevelCrossings`, read during `Advance` where crossings are
   live; the context member serves UI-thread callers that build a context directly (B2's hit-testing,
   tests).

### Blocked on B2, which has not landed

6. **T8 (`AnnotationLevelRemapper`) and the annotation half of T9 (`EnvelopeHitTest`,
   `EnvelopeDragSession`, `AnnotationTrackInteraction`) are not built.** They take `AnnotationElement`,
   `TimeEnvelope`, `DocDelta` and `AnnotationDocument.ApplyMigration`, none of which exist on this
   branch — there is no `Annotations` namespace anywhere in the tree. What B3 *could* land without
   them is landed: `LevelSetChange.TryRemapAnchor` implements the plan's four-rule rebase and is
   directly tested, and `TickAxis` ships with correction 6's domain warning on it. B2's implementer
   inherits a remap function that already works and a `MapSpace.QuantizeZ` to stamp anchors with.

### Additive API on B1's contracts

7. **`ILevelLayoutPolicy` gains a defaulted `int Revision => 0`.** `PaneSet.Reconcile` early-outs on the
   level-set version, the display mode and the host size — none of which move when the strip picks
   another floor, so without this a `SingleLayout` whose `ActiveLevelId` changed would never be asked
   to arrange again. `StackedLayout` takes the default, which is correct for a policy that is a pure
   function of its arguments. `PaneSet` also compares against the pane count the policy *last produced*
   rather than `space.Levels.Count`, which is no longer the same number under `Single`.
   <br>`SceneCompositor`'s "one pane means every level" sentinel became "a lone pane over a map with no
   other floor" for the same reason: under `SingleLayout` a single pane shows one of several levels, and
   the old rule would have drawn the other floor's players into it.
   <br>`ILevelSurface` (App) is a **new** interface rather than members on `IPlayback2DSurface`: the
   pre-v2 viewport has no level identity to honour, so stubbing them there would let the strip appear
   over a surface that cannot obey it.

### Test-environment notes

8. **Six App-suite failures on Windows are pre-existing, not B3's.** Verified by running the same
   classes at `f6ae1ab` in a scratch worktree: `DiagnosticsFileLogTests` ×3 (the temp directory is named
   from TUnit's `TestId`, which contains a `:` — illegal in a Windows path),
   `LibraryShellTests.SettingsBacked_AddRemoveFolder_WritesThroughToSettingsJson`, and
   `DemoLibraryServiceTests.Scan_DeduplicatesSameFile_AcrossSymlinkedFolders` (symlink creation needs
   elevation). `DemoProcessingQueueTests.QueuePath_PersistsCache_SoSecondLaunchDoesNotReparse` fails
   only inside a large batch and passes standalone on **both** `f6ae1ab` and this branch — an
   order-dependent flake, unrelated.

9. **`scripts/test-app-suite.sh` cannot run under bash.** Its shebang is `#!/bin/zsh` and its batch
   partition indexes `CLASSES` from 1, which is a zsh array convention; under bash the loop reads one
   past the end and `set -u` aborts the third batch (`CLASSES[$i]: unbound variable`), after silently
   skipping the first class. B3 ran all three batches by reproducing the partition with 0-based
   indexing. Not fixed here — it is not this phase's file and the fix is a one-line index change
   somebody should make deliberately.

### Carry-forward status (B0/B1 reviews)

10. **`duel-mirage-b` and `fitmap-mirage-eco` are still uncaptured**, exactly as B1 deviation 16 and 19
    record: both need a de_mirage demo and the only demo in the tree is
    `assets/tour/sample-de_nuke.dem`. Both skip cleanly. `mirage-single-level` is likewise absent, so
    B3's "strip hidden on a single-level map" case is covered by `Strip_IsCollapsed_OnSingleLevelMap`
    (headless, synthetic) and `Dust2_StaysSingleLevel_StripHidden` (skips without a dust2 demo)
    instead of by a mirage fixture. **Not a B3 blocker; still open for whoever stages a mirage demo.**

11. **B1 deviation 27 (`MapSpace.LevelFor` returns `MapLevel?` where registry §3.4 says `MapLevel`) is
    adopted, not narrowed.** Both overloads stay nullable: an empty `MapSpace` is representable — it is
    the state before the first push — and every call site already handles null. `LevelSelection`,
    `LevelHysteresis` and `LevelCrossingTracker` all treat "no levels yet" as "do nothing", which is
    what keeps the first frame of a demo from inventing a floor.

12. **B1 deviation 28 (`SceneCompositor.Add`/`Remove` are outside the gate) is untouched.** B3 registers
    no layers at runtime, so it is still not triggered. It remains B2's to take the gate around
    registration.

### Review findings (independent reviewer, on top of the implementation)

Four defects found by review, each pinned by a test that fails without the fix. Numbering continues the
list above.

13. **`LevelSetChange.TryRemapAnchor` sank every upper-floor anchor onto the floor below.** The plan's
    remap algorithm step 8 orders the rules *containment → identity → nearest*, and the implementation
    followed it. But real band lists are **contiguous** — `FloorSplitter` emits slice N's `MaxZ` as slice
    N+1's `MinZ` (`:375,381`), and de_nuke's baked bundle publishes `[-100000..-528]`/`[-528..100000]` —
    so an anchor stamped with a level's `ZMin` sits exactly on a shared boundary, which
    `MapLevel.Contains` answers **true** for on both sides. The containment scan takes the first match,
    which is the band *below*. Worse, once the boundary drifts (which `Nuke_LevelIds_AreStable…` proves
    it does, `drifted-while-retained=4`), containment beat identity even for a level whose id was
    carried: bands `[0,640]/[640,1280]` → `[0,704]/[704,1280]` rebased the upper level's anchor `640`
    to `0`. The plan's own tests missed it because both anchor fixtures use *non-contiguous* bands
    (`[0,640]` + `[1280,1920]`), the one shape real maps never have.
    <br>**Fixed:** identity first, then half-open containment (a shared boundary belongs to the band
    **above** — an anchor is a band's lower bound, never its top), then nearest centre. This inverts the
    plan's step 8, deliberately: identity-before-geometry is what design §5.3 asks for and what the rest
    of B3 already does. Pinned by `MapSpaceRemapTests.TryRemapAnchor_OnContiguousBands_FollowsTheIdentity_NotTheBandBelow`
    and `…_OnASharedBoundary_PrefersTheBandAbove`. B2 inherits the corrected function.

14. **`LevelHysteresisOptions`' three spatial knobs were wired to nothing.** `LevelHysteresis.Update`
    delegated the sticky band to `MapSpace.LevelFor(z, previous)`, which has no options parameter and
    reads `LevelHysteresisOptions.Default`. Since `Default` is a get-only static, *passing* an options
    instance is the only way to retune — and doing so silently changed only `DwellSeconds`, leaving
    `MinBand`, `MaxBand` and `BandFractionOfSpan` inert. That is exactly the mitigation plan risk R4
    claims ("all four constants live in `LevelHysteresisOptions`, so retuning is a one-line change"),
    so the risk was unmitigated as shipped. No test caught it because every test constructs `new()`.
    <br>**Fixed:** `Update` resolves statelessly and applies `SpatialBand` with **its own** `_options`.
    `MapSpace.LevelFor(z, previous)` keeps `Default`, which is correct for its option-less production
    caller (`LevelCrossingTracker`), and now says so. Pinned by
    `LevelHysteresisTests.Options_ReachTheSpatialBand_NotJustTheDwell`.

15. **`LevelLayouts.Parse` let an undefined `LevelDisplayMode` out.** `Enum.TryParse` accepts any number
    inside the underlying type's range, so a hand-edited `Playback2D:LevelDisplayMode` of `"7"` returned
    `(LevelDisplayMode)7` — which `LevelLayouts.For` throws `NotSupportedException` on, the exact "a typo
    must not stop the tab from opening" case the method's own doc comment promises to absorb. Not
    reachable to a crash today (the App maps anything-but-`Single` to `Stacked` before `For` is called),
    but `For` and `Parse` are exported to B4 and C1. **Fixed** with `Enum.IsDefined`; pinned in
    `SingleLayoutTests.LevelLayouts_For_ReturnsThePolicyAndRefusesTheReservedMode`.

16. **`MapSpace.Rebuild` was not idempotent for a degenerate band.** `Rebuild` widens a zero-width band
    to one quantum, but `IsUnchanged` compared against the **raw** `MaxZ`, so the same malformed band
    list fed twice never matched and rebuilt every call — raising `LevelSetChanged`, dropping every
    picture cache and re-arranging every pane, on every frame. `MapSpaceFactory.SameBands` shields the
    production path, so the exposure is direct `Rebuild` callers (B4's export replay, `dv2d`), but
    "idempotent" is the contract T1 states. **Fixed** by comparing against the normalized max; pinned by
    `MapSpaceRemapTests.Rebuild_IsIdempotent_ForADegenerateBand`.

17. **`Nuke_AutoFollow_SwitchCount_IsBounded` could not fail in the direction that matters.** It follows
    the first live slot it meets — slot 2 on this capture — and asserts only an upper bound on switches.
    Slot 2's Z bottoms out at −640, and the spatial band on de_nuke's baked floors is 128u, so it never
    clears −656: the test observes **zero** switches and a chooser hard-wired to never switch would pass
    it. Reviewer verification of the real demo through both floors found 8 of 10 players do traverse
    (Z down to −776), producing 16 genuine transitions. Added
    `Nuke_AutoFollow_SwitchesToTheFloorThePlayerIsOn_Deterministically`, which drives every player's own
    Z track through `LevelHysteresis` and asserts (a) floors are genuinely traversed, (b) every switch
    lands on a level that **contains** the player at that frame, and (c) two independent replays of the
    same track agree exactly. No production defect was found behind it — the chooser is correct; the
    gate was not.

18. **Nothing exercised the follow → level → pane seam end to end.** `LevelSelectionTests` pins the
    decision in isolation with a hand-built `Scene2DFrame`; `ManualPick_SwitchesPane_AndDisablesAuto`
    pins the strip but never follows anybody, so `Scene2DHost`'s one line joining them —
    `_levelSelection.FollowedSlot = _mode == CameraMode.FollowPlayer && _followSlot >= 0 ? … : null` —
    had no coverage at all, and a chooser wired to a slot that is never assigned is indistinguishable
    from one whose dwell has not elapsed. Verified by hand through the real wiring (VM follow funnel →
    host → selection → `SingleLayout.ActiveLevelId` → the arranged pane) and found **correct**; landed as
    `Playback2DLevelStripTests.AutoFollow_ShowsTheFollowedPlayersFloor_AndAManualPickOverridesUntilReleased`,
    which also covers "a manual pick holds against the followed player" and "re-arming AUTO releases it".
    It pushes frames until the outcome holds rather than a fixed count, because the dwell is scene time
    and the host's `dt` comes from the headless animation clock.

19. **`tests/fixtures/playback2d/scenes/nuke-multilevel.scene.json` is rewritten with CRLF by every test
    run**, leaving the worktree dirty with a content-free diff. Reproduced at `f6ae1ab`, so it is B1's,
    not B3's — recorded here only so the next phase does not re-diagnose it or commit the noise.

20. **B3's two new goldens were unmaintainable through the path their own error message names.**
    `LevelGoldenTests` tells the reader "Regenerate deliberately with
    `scripts/update-playback2d-goldens.sh`", but that script runs only `SceneGoldenTests`,
    `BudgetFixtureCorpusTests` and `Playback2DGoldenCaptureTests` — so `nuke-single-upper` and
    `nuke-multilevel-noradar` would never be rewritten by it, and a deliberate visual change to the
    single-pane path would leave whoever made it with a failing gate and a script that does nothing.
    **Fixed:** a third step running `LevelGoldenTests`, placed *after* the demo-derived capture, because
    both goldens are rendered from the `nuke-multilevel` scene that step produces — regenerating them
    first would re-baseline them against the previous capture. Verified by deleting
    `nuke-single-upper@900x900.png` and regenerating it: byte-identical to the committed file.

**Reviewer verification not turned into new tests** (all clean): the `nuke-multilevel` parity gate is
unmoved at 99.68 % within ±8 after all four fixes; `[budget] allocation 0 B/frame` and
`advance p99 0.002 ms` / `render p99 2.007 ms` are unchanged; `StackedRender_IsByteIdentical_AfterASingleModeRoundTrip`
still byte-matches; manual selection holds across rebuilds (`LevelSelection.Update`'s `Manual` branch
only re-picks when the pinned level is gone) and across the level-set-changed handler; `ViewportTransform
.WithViewport` preserves centre, scale, zoom and pan, so a mid-demo rebuild re-viewports without moving
the camera; `MapRadarBinder` binds de_nuke's two baked floors to `de_nuke_lower.png`/`de_nuke.png` by
overlap (scores 0.997 / 1.000) and reports `Exact`.

21. **Merge into `feature/playback2d-v2` (integration record).** Merged at `742b7ca`, which already
    carried B2, C1 and C2 — none of which existed at B3's diff base `f6ae1ab`. Six files conflicted
    (`AppSettings.cs`, `SettingsService.cs`, `Playback2DTabViewModel.cs`, `Scene2DHost.cs`,
    `Playback2DView.axaml.cs`, `HeadlessSceneRenderer.cs`); **every one was add/add and every one was
    resolved by keeping both sides**, ordered per §3.10 where the registry fixes an order
    (`Playback2DSettings` annotation properties before `LevelDisplayMode`/`AutoLevelFollow`; the same
    order in `WriteInMemory`). No side's behaviour was dropped. Three consequences worth naming:

    - **`Scene2DHost`'s rebuild branch now runs all three actions**: B2's
      `_annotationLayer?.InvalidateLevels()` alongside B3's `_crossings.Reset()` and
      `_panes.RetainUnarranged(...)`. The two phases wrote the same `if (_levels.Update(frame))` body
      independently; they compose, and dropping either would silently half-invalidate a rebuild.
    - **`HeadlessSceneRenderer` keeps C1's convenience members and B3's `Crossings`.** The C1 merge
      (correction 24) had already folded the CLI facade into this class; B3's crossing tracker is
      additive to it, and `UpdateCrossings` sits beside `RenderPng`/`RenderInto` rather than replacing
      any of them.
    - **T8 is no longer blocked.** Deviation 6 recorded T8 (`AnnotationLevelRemapper`) and T9's
      annotation half as blocked on B2; **B2 has now landed**, so `AnnotationDocument.ApplyMigration`
      and `DocDelta` exist in the merged tree alongside B3's `LevelSetChange.TryRemapAnchor` and
      `TickAxis`. Both halves are shipped and independently tested, and **nothing wires them together**:
      on a `MapSpace` rebuild, annotation anchors are still not rebased. This is now an ordinary
      unblocked task on B2's acceptance checklist, not a dependency wait.

    **`dv2d bench`'s allocation assertion is a pre-existing C1 carry-forward, not a B3 regression.**
    `BenchAllocationTests.SmallestDrawingFixture_AllocatesNothingPerFrame` reports 3336 B/frame both
    before and after the merge — byte-identical, and reproduced by running `dv2d bench` directly at
    `742b7ca` (`synthetic-tenplayers` 3336 B, `full-scene-budget` 6224 B, same numbers on both sides).
    Temporarily removing B3's `UpdateCrossings` call does not move it. The test's own doc comment names
    the owner: it is `[Category("Budget")]`, excluded from the correctness lane, and marked "expected to
    fail until `SceneLayerCatalog` registers B1's seven layers (C1 risk R6 / deviation 14)". B3's own
    allocation gate — the same `ScenePipelineBenchmark` driven through `SceneStage` with the crossing
    tracker wired exactly as `Scene2DHost` wires it — still measures **0 B/frame** at 1920x1080.
