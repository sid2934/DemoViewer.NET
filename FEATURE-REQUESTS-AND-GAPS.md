# Feature Requests & Gaps

A lightweight, curated tracker for outstanding desired features and small capability gaps
across the repo. This is the place to record "we want X, but it isn't built yet" with enough
context that whoever picks it up doesn't have to re-derive the problem.

This is not a bug tracker (use commits/issues for defects) and not a roadmap. It's a
durable index of known-missing capabilities, one section per core area of the codebase.

---

## How to use this doc

- Add an entry under the section for the area it most affects. If it spans areas, file it
  under the one that would do the work and mention the others.
- Give each entry a short descriptive bold title. Status: Open · Planned · In progress ·
  Done (leave Done entries a release or two for provenance, then prune).
- When a gap has a pre-wired landing spot in code or rules, tag it there with
  `TODO(<kebab-tag>)` and record the tag in the entry. One sweep finds every hook:

  ```sh
  grep -rn 'TODO(' rules/ src/ tools/
  ```

- Keep entries short. Context over completeness — a paragraph of "why + where it'd land"
  beats a spec.

---

## Parser (`src/Parser/Cs2DemoKit.Parser/`)

The core demo-parse pipeline. `DemoParser.cs`, `DemoFrame.cs`, `LEB128Utils.cs`, and
`BitBuffer.cs` are the stable, carefully optimised core — change them deliberately and
sparingly.

- **Parse-time perf lever.** Parse is ~4–5s (~52% of load) and is the next perf lever now
  that the load-perf work landed. It lives in `DemoParser.cs` (decompress vs. pass-1 split),
  the most sensitive file in the repo, so it deserves a dedicated, careful effort. An opt-in
  parse-profiling first step already exists (`ParseProfilingSnapshot` / the `--profile`
  `Parse-Pipeline` timing tree — see `docs/profiling.md`); only the optimization itself
  remains. _Status: Open._

- **Two further malformed-input hazards in the string-table decoder.** Found while fixing
  the unbounded entry-growth OOM (fixed in v0.5.1; `MaxEntriesPerTable` 4096 is the memory
  ceiling). Neither hazard below is fixed; both are recorded so they are not rediscovered
  from scratch.
  - `numEntries` hang — a declared entry count can drive a decode loop far longer than the
    payload justifies. Bounded in *memory* by `MaxEntriesPerTable`, but not in *time*.
  - Snappy decompression bomb — a few hundred compressed bytes expand to the 16 MiB
    `string_data` ceiling (the 3-bit sequential shorthand carries no entropy, so it
    compresses to almost nothing). This is why capping entry *count* rather than entry
    *index* was necessary when the table storage moved to a keyed map.
  - _Status: Open. Both belong to the broader malformed-demo policy question tracked in
    KNOWN-AND-SUSPECTED-ISSUES.md._

---

## Entity Tracking & Schema (`src/Parser/Cs2DemoKit.Parser/EntityTracking/`, `src/Parser/Cs2DemoKit.Parser/Entities/`)

Stateful entity replay and the SDK-derived Schema Lens.

- **Weapon-entity reads (ammo/clip + weapon state) surfaced to consumers.** The entity
  tracker can decode weapon entities, but there is no first-class, typed way to read a
  player's active-weapon ammo (`m_iClip1` / `m_iClip2` / reserve) or other weapon-entity
  fields. This is the data-layer half of the rules-vocabulary gap below. The
  `active_weapon_class` provider already does the single-hop handle follow
  (`m_hActiveWeapon` → weapon entity → class name); reading `m_iClip1` is the same follow
  with a different target field. _Status: Open._

---

## Analysis Engine & Rules (`src/Analysis/*`)

The state-graph evaluator and the Rulesets v2 authoring surface. Rule-authoring gaps often
have a pre-written gate in an example ruleset — see the `TODO()` hooks.

- **Weapon/ammo entity read in the rules vocabulary (e.g. "last bullet in the mag").**
  The v2 entity-read vocabulary exposes only pawn-level reads — `entity.pawn.health` /
  `.armor` / `.equipment_value` / `.active_weapon_class` and `entity.game.freeze_period`
  (`src/Analysis/Cs2DemoKit.Analysis/Plugins/BuiltinProviderSpecs.cs`). There is no read for
  the active weapon's ammo/clip, and the `kill` view carries no ammo facet
  (`tools/DemoViewer.NET.RulesCatalog/data/views.yaml`). This blocks rules like "a kill with
  the last bullet in the magazine."
  - Where it'd land: add a `ProviderSpec` (e.g. `entity.pawn.active_weapon_clip`) in
    `BuiltinProviderSpecs.cs` that follows the active-weapon handle to `m_iClip1` (mirroring
    `PawnActiveWeaponClass`'s handle follow), register it in the generic per-player provider
    list, add a `ProviderDigestParity` / `CatalogDrift` entry, and regen `rules/catalog.json`.
    Depends on the weapon-entity read gap above.
  - Pre-wired hook: `grep -rn 'TODO(entity-clip)' rules/` — the qualifying-kill gate in
    the `deagle_lastbullet_postplant_doubles` example (now in the CS2DemoKit repo) already has the intended
    `and killer.entity.pawn.active_weapon_clip == 0` clause commented in place.
  - _Status: Open._

- **Player positioning / place context (bombsite, named place, coordinates).** No v2 rules
  context exposes where a player is standing — no bombsite/site membership, no named place
  (`m_szLastPlaceName`), no coordinate read. Pawn world position *is* reconstructable from
  `CBodyComponent` cell coords via `PositionUtil.CellToWorld` — but that helper currently
  lives in the App layer (`src/App/DemoViewer.NET/Services/PositionUtil.cs`), so surfacing
  position to the engine likely means lifting the reconstruction into a shared
  (parser/analysis) location first. This blocks rules like "killer standing on B site,"
  zone-based stats, and site hold/retake analytics.
  - Where it'd land: a per-player provider (or synthesized context) exposing either raw
    position or a resolved place/bombsite. Raw coords are cheap (reconstruct from cell
    coords). A named place needs the entity's `m_szLastPlaceName` field or a map-zone
    lookup; bombsite membership likely needs map trigger volumes (see the asset-pipeline
    zone-data entry) or a plant-site heuristic. Register + catalog + drift-test + regen as
    in the ammo-read entry above.
  - Pre-wired hook: `grep -rn 'TODO(positioning)' rules/` — the same deagle example carries
    the intended `and killer.entity.place == "BombsiteB"` clause commented in place.
  - _Status: Open._

- **`rules check` doesn't validate `show:` references (checker vs build divergence).**
  The demo-less `rules check` path (`RulesetComposition.ComposeDraft`) does not run
  `ShowLowering`, so an invalid `show: scoreboard`/`tables:` entry — a reference to a
  non-existent stat/highlight/tally-target — passes `rules check` with 0 errors but throws
  at build/eval time (`ShowLowering.ResolveScoreboardRef`: "references neither a stat, a
  highlight, nor a tally target"). An author gets a clean linter and a later crash. Found
  2026-07-14 during the v2 cutover. Fix: run the scoreboard/table reference resolution (or
  a validation-only pass of it) inside the checker so `rules check` catches what the build
  catches. _Status: Open._

- **Share duplicated rule conditions as one node (predicate common-subexpression
  elimination).** Many stats gate on the *same* compiled predicate, but each stat's
  condition is compiled and evaluated independently rather than computed once in a shared
  node. Example (visible in the graph today): across `rules/kast.rules.yaml` and
  `rules/player_stats.rules.yaml`, several stats gate on "the current player is the
  assister on a `player_death`" — `AssisterSlot == player.slot && KillerSlot != VictimSlot`
  — and each stat node re-checks that predicate on every `player_death` instead of a single
  shared `IsPlayerTheAssister`-style node feeding them all. The engine already precomputes
  *some* shared booleans as enrichment nodes (`enrich.kill.was_enemy_kill`,
  `enrich.kill.was_trade_kill`) and dedups *whole* hash-equal stat nodes, but there is no
  CSE for author-expressed predicate subexpressions.
  - Where it'd land: hoist repeated compiled predicates into a shared boolean node
    (mirroring the enrichment-node pattern), keyed by the canonical expression, referenced
    by each consuming edge — in `src/Analysis/Cs2DemoKit.Analysis/Building/ExpressionCompiler.cs`
    / `RuleChainBuilder.RulesetsV2.cs`. For a simple slot-equality the win is small; it
    scales with expensive comparisons (entity reads, string compares, and future positional
    checks), where re-evaluating the same predicate N times per event is the cost.
  - Also verify + surface: confirm whether any dedup layer already shares these (structural
    dedup exists for whole nodes) — the authoring graph currently renders each stat's
    condition inline on its own edge, which suggests no sharing; if a layer *does* share,
    represent it in the graph (ties to the graph-refinement entry below).
  - _Status: Open._

---

## App / UI (`src/App/*`)

Avalonia desktop UI — ViewModels, Views, modules (2D playback, library, analysis).

- **Steam avatar player markers in 2D Playback.** The 2D Playback module renders player
  markers as coloured dots; the original roadmap called for real Steam avatars
  (`IPlayerMarkerVisual` → an `AvatarMarkerVisual`). The SteamID is already available on the
  roster entry (`PlayerRosterEntry.SteamId`); no avatar-fetch code exists in
  `src/App/DemoViewer.NET/Modules/Playback2D/` yet. Blocked on a decision: a Steam Web API
  key plus an image fetch/cache policy. _Status: Open (blocked on that decision)._

---

## Graph Visualization (`src/Visualization/DemoViewer.NET.Visualization/`)

MSAGL-based analysis-graph rendering. (Core layout/routing was root-caused and fixed; keep
MSAGL — these are refinements on top.)

- **Click a node to isolate the sub-chain it belongs to.** The Workbench authoring graph
  (`AuthoringGraph` + `RuleGraphSkeleton.BuildAuthoring`) renders the whole open ruleset;
  there's no way to click a node and collapse the view to just the sub-chain that node
  participates in (its chain + the events/gates that feed it). The plumbing largely exists:
  nodes carry `ChainIds` (`src/App/DemoViewer.NET/ViewModels/GraphNodeViewModel.cs`) and the
  Analysis tab already has a chain filter that dims nodes/edges outside a selected chain
  (`src/App/DemoViewer.NET/ViewModels/GraphFilterViewModel.cs`). The gap is wiring
  node-selection → isolate-its-chain in the Workbench graph.
  - Caveat: per-player chains do not appear in `NodeChains` (their footprint is
    column-keyed via `PerPlayerColumnAssignment.ChainId`), so per-player isolation needs
    that keying, not the game-scope `ChainIds` alone.
  - _Status: Open._

- **Layout + condition-placement refinement pass on the graph view.** Beyond the
  root-caused core layout, the graph wants a readability tuning pass — node/edge spacing,
  routing, and especially where conditions render. Today an edge's predicate rides on its
  `ConditionLabel` (e.g. `event.KillerSlot == player.slot && …`), which crowds the edge and
  is hard to read on complex rules. Options to explore: condition chips/nodes rather than
  edge labels, collapsible condition detail, better per-player-template layout. Lands in
  `src/Visualization/DemoViewer.NET.Visualization/` (layout/edge styling) plus how
  `AuthoringGraph` emits condition text. _Status: Open._

---

## 3D Visibility (`src/Analysis/Cs2DemoKit.Analysis/Visibility/`)

BVH line-of-sight engine and the visibility analyzer.

No tracked items currently — smoke occlusion and the visibility stat surfaces both shipped
(`SmokeVolumes.SegmentBlocked`; Stats tab `IsVisibilityView`). See `docs/3d-visibility/`.

---

## Asset Pipeline (`docs/asset-pipeline/`, separate baker)

Version-keyed, pre-baked Valve-derived map bundles (radar, nav, collision); the app stays
VRF-free and consumes PNG + JSON.

- **Named-place / bombsite zone data.** Map trigger volumes / nav zones that would let the
  engine resolve a world position to a named place or bombsite — the data dependency behind
  the positioning entry in the Analysis section. _Status: Open._

---

## Testing (`src/App/DemoViewer.NET.App.Tests`)

- **App-suite cold-start cascade: the first run after a rebuild intermittently fails en
  masse.** Observed repeatedly during the v0.6.0 work (2026-08-03): `dotnet run --project
  src/App/DemoViewer.NET.App.Tests` immediately after a build sometimes collapses to
  ~130–140 failures in ~6s; an immediate re-run of the same binary settles to the
  environmental baseline (~6). The cascade signature is a poisoned shared headless UI
  session: early failures show `HeadlessUnitTestSession.EnsureIsolatedApplication` throwing
  `TargetInvocationException`, after which every UI-touching test fails with
  `TypeInitializationException` on `Avalonia.StyledElement` or
  `InvalidOperationException: Call from invalid thread`. It is load-dependent, not
  code-dependent — reproduced with and without pending changes, and struck first or second
  runs under parallel build load. Suspects: first-run JIT/AV pressure racing the
  `HeadlessSession` lazy init, or a `[NotInParallel]` shell-construction test touching
  Avalonia statics before the session's isolated app exists. It has produced repeated false
  alarms; any CI would mistrust the suite. Resume here with: capture the inner exception of
  the `EnsureIsolatedApplication` failure (it is currently swallowed into the cascade), and
  consider hardening `HeadlessSession` init (retry-once, or an assembly-level
  ModuleInitializer that forces the session up before any test runs). _Status: Open.
  Workaround: re-run; treat only steady-state results as signal._

## Tooling & Benchmarks (`tools/*`)

AnalysisBench (perf + accuracy suite), Codegen, DemoSourceDetails, RulesCatalog.

- **Stat drift is invisible between manual re-baselines: the parity tests compare two
  committed fixtures, never live code.** `StatParityTests` runs
  `CompareStat(demoId, stat, "ours", "expected")` and `"ours"` vs `"leetify"`, and both
  providers resolve through `GoldenStatsTestHelper.LoadGolden`, which reads
  `{provider}.golden.json` off disk. So the fixture pair only ever proves itself
  self-consistent. Nothing re-derives `ours.golden.json` from current code, and it changes
  only on a deliberate `AnalysisBench --suite` re-baseline.
  - Why it matters, concretely: this is how the enemy-damage regression (an undercount that
    flipped to a +2..+66 overcount; fixed 2026-08-12) survived ~6 weeks and shipped in
    v0.5.0 with a fully green suite. The per-stat tolerance table is correctly calibrated —
    `EnemyDamage = 3.0` would have caught a +66 divergence instantly — it was simply never
    aimed at live output.
  - Where it'd land: a demo-gated test that evaluates the reference demo and compares the
    result to `ours.golden.json` within the existing tolerances, so a drift fails the suite
    instead of waiting for someone to eyeball a bench run. Cheap version: assert only the
    stats with pinned tolerances, on the one demo already used by `RulesV2ViewFixtureTests`.
  - _Status: Open. Independent of the regression itself — fixing it did not close this gap,
    and closing this gap would have prevented it._

---

_When you add an entry, keep the section list above complete — every core area gets a
heading even if it currently reads "no tracked items," so the doc stays a full map of the
repo._
