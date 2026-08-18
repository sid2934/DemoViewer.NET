# Rulesets v2 — Loader + Compiler Plan

The loader + compiler this doc specifies shipped on main. The interface contract is
materialized in `tools/DemoViewer.NET.RulesCatalog/data/views.yaml` + the catalog generator,
and `rules-v2-spec.md` is the frozen as-built contract. Retained as the live interface spec
(cited by `views.yaml` and the RulesCatalog project).

This was the working contract for the loader + compiler stage of the authoring-UX plan
(`docs/rules-v2/rule-authoring-ux-review.md` §3.2), revised 2026-07-11 after a detailed design
review; everything the review verified is folded into the decisions below. The language core it
consumes is `docs/rules-v2/rules-v2-spec.md` (expression EBNF, types, namespaces,
canonicalization, resolved-identity hash preimage), implemented in
`DemoViewer.NET.Analysis.Rules` (the semantic core, on the branch). The engine contracts it
targets (declared reads, multi-source conditional edges, rising-edge routing + multi-action)
are the engine track, also on the branch. It is the build contract for the loader/compiler
work items and the interface contract the view/facet curation stage curates against.

---

## 0. Resolved design decisions (read first)

The review surfaced forking calls the original draft left implicit. These are now settled;
the rest of the document elaborates them. Where a decision amends the (then still draft)
`docs/rules-v2/rules-v2-spec.md`, it is a legitimate pre-freeze correction
and is cross-listed in §12 as a spec-artifact change to land before the preimage-snapshot
golden.

1. **Typed ruleset identity is deferred; this stage keeps `_chain_{id}` naming.** `RulesetId` is a
   compiler-internal IR/hasher/collision-validator type for now; its canonical string
   projection is `RulesetId.JoinKey => $"_chain_{ruleset_id}"`. This is stamped into every
   string-keyed surface of the shared `BuildResult` so the evaluator, the timeline
   (`RuleChainEvent.ChainName`), the output projector (`ConfiguredOutputProjector` reads the
   `_chain_` prefix), per-player column assignment, and the fire-count badge layer run v2
   rulesets with **zero evaluator/Abstractions changes**. The typed-record migration is a
   later phase. (Settles the highlight-identity and "same BuildResult" string-consumer
   findings.)

2. **Params bind to literal values at build, before any hashing.** A dedicated
   `ParamInliner` pre-pass (`RulesetsV2/Resolve/`) substitutes each `params.<name>`
   reference with its bound value as a typed literal `ExpressionNode`, so the
   `CheckedExpression` handed to `RuleNodeDescriptor` already carries literals. (Implementation
   note: this is not `NormalizerOptions.DefineLookup` as the draft assumed — that
   mechanism is head-keyed and cannot express a qualified `params.min_kills` tail; a purpose
   -built inliner was required. Same effect, correct mechanism.) Params stay symbolic on the
   demo-less load/`rules check` path (no hashing) and bind at the per-demo build re-pass. Two
   installs with different param values therefore produce different preimages, as spec §6
   requires. (Two separate findings had params hashing symbolically; this settles both.)

3. **Views declare a closed `binding:` mode**, not a bare actor field: `actor_slot` (default)
   | `team` | `none`. `actor_slot` = the ruleset player's slot equals the view's actor slot
   (v1's slot-equality, lowered against `env.Slot` under the env lowering); `team` = the
   ruleset player's **live** team (not frozen env — team_num swaps at halftime) compared to
   the round-end winner enrichment; `none` = no implicit binding (in `for: each_player`,
   every player's template fires on the event). bomb views are `binding: none`;
   round_won/round_lost are `binding: team`. (Settles the actorless bomb views,
   round_won/round_lost being unexpressible, and the `match: {actor: any}` escape hatch.)

4. **Tally and Streak become hashable kinds** (a semantic-core change, sequenced ahead of the
   planner): add `Tally`, `Streak` to `RuleNodeKind`, extend the keep-spec preimage row
   with their kind args (tally thresholds; streak window + min-streak), re-run the 117-test
   hasher battery, and re-baseline the preimage-snapshot golden. `RuleHasher.BuildPreimage`
   throws on an unset kind, so mapping them onto `Count` is not an option (it would also
   false-dedup a count against a same-trigger tally). (Three separate findings hit
   tally/streak being unhashable.)

5. **The preimage scope axis is compound.** Spec §6 row 3 widens from `match|round|player`
   to the full `(For × Per)` product: `match`, `round`, `player_match`, `player_round`. A
   per-player `per: round` stat and its `per: match` twin must not hash-equal (they differ
   only in reset scope). `ScopeAxis` in `Analysis.Rules` widens to match. (Settles the
   per-player preimage mapping and the (For,Per) collapse.)

6. **Composition is an explicit orchestration seam.** v1 chains and v2 rulesets build into
   one graph via `RuleChainBuilder.Build(RuleChainConfig, IReadOnlyList<CheckedRuleset>)` (or
   a `GraphBuildOrchestrator` wrapping it); entity-provider reference-gating and scanner
   construction **union** the v1 and v2 read sets before the scanner is built; the
   `BuildResult` lists are populated from both sources onto one `StateGraph`. "Same
   BuildResult" is refined to "same shape, additive optional members." (The finding:
   composition had no primitive.)

7. **`this`, `net.*`, and `define:` maps get concrete dispositions** (§4, §5, §12):
   `this` resolves as a non-stat Value symbol typed as the enclosing stat's value type,
   hashing as the fixed marker `(ref this)`, excluded from cycle edges; bare `net.<Message>`
   triggers are live (the spec's namespace-tree placement wins over the draft's over-broad
   reservation — only `net.*` `match:`/`where:` payload matching is reserved); **map-valued
   `define:` is deferred to the vocabulary wave** (the settled `define:` scope is "named
   triggers and lists"), reserved in the spec, so `DefineDef` carries no map body yet.

---

## 1. Deliverables

| Item | What ships | Verified by |
|---|---|---|
| document model | v2 document model + YAML mapping + structural validation (incl. `for_each:` expansion, `off:`, `actor:`) | demo-free battery |
| resolver | resolver (defines/params-bind/for_each-expanded/views/exports) + canonicalize + checker → CheckedRuleset IR | demo-free battery |
| planner | CheckedRuleset → graph on the engine-track contracts; `RuleNodeKind` Tally/Streak + preimage extension; env lowering behind a flag; cycle pre-pass; coverage skips | hand-built graphs + golden pilot |
| surfacing | `show:` surfacing (scoreboard/tables, highlight-column semantics) + list flatteners in CSV/JSON serializers | fixture serialization tests |
| schema | `rules/cs2demokit-rules.schema.json` generation (+ split reserved/live shape lists) + drift gate | drift test |
| `rules check` | v2 integration (demo-less checker dispatch + `--demo` compiling composed configs + fixture disposition) | CLI battery |
| pilot | `rules/examples/post_plant_double.rules.yaml` running end-to-end with a golden diff vs the v1 file | golden parity test |

**Pilot location.** The pilot lives in `rules/examples/`, not the auto-scanned
`rules/` root: it shares the logical id `post_plant_double` with the v1 example
`rules/achievement-post-plant-double.yaml`, and `YamlConfigLoader.TryLoadDirectory` does not
yet enforce the §8 cross-version chain/ruleset collision (it defers that to the overlay). A
non-recursive `*.yaml` scan skips `rules/examples/`, so the app's production load stays
collision-free while the golden + `rules check` tests load the pilot by explicit path. The
pilot is a compiler **proof**, not a shipped production ruleset — the corpus port is
where the v1 file is removed and the v2 form ships under the real id. §8 cross-version
collision enforcement in `TryLoadDirectory` belongs to the composition/overlay work in the
vocabulary wave.

Out of scope here (deliberate, reserved in the schema only): the workbench UI,
the blueprint gallery, team aggregates and bucket lifts (vocabulary wave), map-valued
defines (same wave), live compute (deferred, evidence-gated), deleting the legacy
loader (one release window after v2.0).

## 2. Architecture and placement

```
DemoViewer.NET.Analysis.Rules      (leaf; the semantic core — the only change here at this
        ▲                ▲          stage is decision 4/5: RuleNodeKind + ScopeAxis widening)
        │                │
DemoViewer.NET.Analysis  │         RulesetsV2/ namespace:
  - RulesetsV2/Model     │           document model records (YAML-independent)
  - RulesetsV2/Resolve   │           catalog scope environments, view expansion, exports
  - RulesetsV2/Compile   │           planner → StateNode/StateEdge/BuildResult
  - Catalog/ (existing)  │
        ▲                │
        │                │
DemoViewer.NET.Analysis.Yaml       v2 YAML → document model mapping + top-level dispatch
```

- The **document model lives in `DemoViewer.NET.Analysis`** (`RulesetsV2/Model/`), like v1's
  `Config/` records; `Analysis.Yaml` maps YAML to it and owns the top-level-key dispatch
  (`ruleset:` = v2, `chains:` = legacy) inside `YamlConfigLoader.TryLoadDirectory`.
- `DemoViewer.NET.Analysis` references `Analysis.Rules` (leaf, BCL-only — no cycle).

### 2.1 Composition mechanism (decision 6)

The build entry point becomes `RuleChainBuilder.Build(RuleChainConfig v1,
IReadOnlyList<CheckedRuleset> v2)` (a `GraphBuildOrchestrator` may wrap it for clarity; either
is acceptable, name the choice in the PR). Obligations, all load-bearing per the v1 builder's
current shape:

- **One `StateGraph`, two sources.** v1 chains and v2 ruleset nodes/edges are added to the
  same graph; `BuildResult.{Nodes, Edges, Chains, RelevantMessageTypes, EdgeBacking,
  per-player templates}` are populated across both.
- **Union entity gating before scanner construction.** The v1 builder freezes the
  `EntityChangeScanner` provider list and reference-gating from v1-config reads inside
  `Build`. The orchestrator must compute the union of v1 and v2 entity read sets **first**,
  then construct the scanner once — otherwise v2 `player.health` reads silently gate out.
- **Additive `BuildResult` members only** (no evaluator change): an optional
  `IReadOnlyList<RulesetCoverageDiagnostic>` (decision, §6 obligation 7) and the v2 node-map
  keys (§6 obligation 8). Existing members keep their shapes and semantics.

The compiler produces the same `BuildResult` **shape** the v1 `RuleChainBuilder` produces, so
`StateGraphEvaluator`, snapshots, breakpoints, fire counters, and the App layer run v2
rulesets with no evaluator changes.

## 3. Document model

Records under `RulesetsV2/Model/`, all position-carrying (`SourcePosition(file, line, col)` —
the §8 diagnostics contract of the language spec):

```
RulesetDoc   { Id, Title, Summary, For (Match|EachPlayer), Enabled=true, Use[], Params{},
               Defines{}, Stats{ordered}, Highlights{ordered}, Show, Position }
ParamDef     { Name, Type (int|float|bool|string|duration), Default, Min?, Max?, Position }
DefineDef    { Name, Body: ListLiteral<string|number> | TriggerDef | ExpressionText, Position }
             // no map body yet — map-valued defines come later (decision 7)
TriggerDef   { On: ViewRef|DefineRef|RawEventRef|NetMessageRef, Match{key→UnaryTest},
               Actor? (reserved key, only value `any`), Where?: text, While?: RefText, Position }
StatDef      { Id, Kind (Flag|Count|Sum|Capture|Compute|Tally|Streak|Bucket),
               KindArg, Per (Round|Match), Keep? (First|Last|List),
               Trigger?, OffTrigger?, ForEach? {key→values[]}, Label?, Position }
HighlightDef { Id, When: text, Per (Round|Match, default Round), Title: template,
               ForEach? {key→values[]}, Position }
ShowDef      { Scoreboard[]: {Stat, Label, Group, Boards[]?}, Tables{}: {Per, Columns[]} }
UnaryTest    { Literal | InListRef | InListLiteral | Comparison(op, literal) | Range(lo, hi) }
```

**`for_each:` (decision, model-carried).** `ForEach {key→values[]}` on `StatDef`/`HighlightDef`
(and, for `show:` families that need it, the entry). Stage-1 Expand multiplies the carrying
entry, substituting `{key}` into **ids, expression texts, labels, and title templates** (all
four surfaces — the draft only named ids and expression texts). Expansion precedes hashing
(spec §6) and precedes duplicate-id checking (expanded ids can collide, so the dup-check must
see the expanded doc).

**`off:` (decision).** `OffTrigger?` on `StatDef` lowers to a deactivate edge on the flag
node (same `TriggerDef` machinery, action = deactivate). Its presence enters the hash preimage
(a flag with `off:` is a different node than one without) — see §12 spec amendment.

**`actor:` (decision).** A reserved `match:` key on `TriggerDef`, outside the `UnaryTest`
grammar; its only legal v2.0 value is the keyword `any` (anything else is a structural
error at the document-model tier). In `for: each_player` it suppresses the view's implicit
actor binding.

Structural validation (pre-catalog): kind discriminator is exactly one of the eight; `keep:`
only under `capture:`; `for_each:` expansion; param typing and default-vs-min/max sanity;
duplicate ids across the shared stat/highlight/param/define namespace (post-expansion);
`title:` templates parse; unary-test parsing. Every error carries `file(line,col)`.

**Unary tests** (`match:` values) parse with the semantic core's lexer where they contain
expressions and desugar in the resolver (not here) to comparisons against the facet/field
value: `enemy: true` (literal), `weapon: in rifles` (list ref), `damage: ">= 5"` (one
comparison operator + one literal), `count: [2..5]` (inclusive range).

## 4. Pipeline and the load-vs-build boundary

```
YAML → RulesetDoc → expand(for_each)          ─┐  at directory LOAD (demo-less):
     → resolve(defines, params→literals, views,│  structural validation, resolution,
       exports, this)                          │  demo-less canonicalize + check. Shipped
     → canonicalize(demo-less: 64/s durations) │  tier hard-fails on any v2 error; user-tier
     → check → CheckedRulesetDraft             ─┘  errors are contained per file (ruleset
                                                   dropped/rolled back like a v1 chain).
     → [per graph BUILD, demo in hand:]        ─┐  at DemoAnalysis.Build:
       rebind params + re-fold durations at     │  the loaded demo's TickRate + active
       ParsedDemo.TickRate; per-profile view    │  source profile in hand; coverage skips
       binding; coverage skips → CheckedRuleset ─┘  decided here (profile-dependent).
```

**Lifetime rule (decision).** Documents are cached at directory load; the resolve→check
stages run demo-less there for diagnostics using the parser's 64/s default (typing is
rate-independent). Duration **folding** and param **binding** that feed the hash re-run at
graph build with the demo's real `TickRate`, so a non-64 demo never hashes 64-folded
constants. The `RuleConfigLoadResult` carries v2 check errors alongside v1 chain errors.

Stage contracts:

1. **Expand.** `for_each:` multiplication (decision above).
2. **Resolve.** Build scope environments from the Catalog + the document:
   - **Adapter (decision, §4.1)** maps catalog friendly-type strings → `RulesType`, injects
     loader-provided instants, and maps provider/context v1 names → v2 namespace paths.
   - `event.*` fields from the resolved view's wire event; facet keys + role handles from the
     view definition (catalog `views` family, §5); `player.*` from providers + PlayerEnv;
     `round.*`/`match.*` from contexts (+ sticky `round.bomb.was_planted`, added by the planner);
     sibling stats / `this` / highlight `.count` / `params.*` (as literals) / `use:`-scoped
     `ruleset.stat` from the export graph (§8).
   - **`this`** resolves as a non-stat Value symbol typed as the enclosing stat's value type
     (never a Stat symbol — that would make hashing self-recursive); it hashes as `(ref this)`
     and is excluded from stat-reference cycle edges. Shadowing a `this` by a stat named
     `this` is a structural error.
   - **Defines:** list-bodied and expression-bodied defines inline via
     `NormalizerOptions.DefineLookup` (they are NOT env symbols — an env-symbol list would
     hash by name and false-share same-named different-content lists across rulesets);
     trigger-bodied defines resolve at the doc-model `on:`-splicing level and error if used in
     expression position. (No map defines at this stage.)
   - View resolution is **per demo-source profile** (like v1 `$logical`): an unbindable view
     skips the stat with a `RulesetCoverageDiagnostic` (never silent zero).
3. **Canonicalize.** Semantic-core normalizer over every expression: defines inlined, `match:`
   bindings lowered, durations folded. **Composed-condition canonical order (decision, §4.2).**
4. **Check.** Semantic-core typed checker per slot with §4 scope rules; kind-specific result
   types (`when:`/`where:` → bool; `sum:`/`capture:` → scalar; `compute:` → numeric; bucket
   `key:` → string); purity + read-set extraction (feeds `DeclaredReads`); role-handle
   reads typed against provider types.

**CheckedRuleset IR** (planner input): per stat/highlight — resolved concrete event set,
canonical ASTs (trigger condition, value selector, while-gate — kept **separate**, see §6
obligation 3 for their preimage packing), `(For, Per)` compound scope, keep-spec, node-level
declared reads, entity-provider references, hash-preimage inputs, and the compiler-internal
`RulesetId(id, scope)` (JoinKey `_chain_{id}`, decision 1).

### 4.1 Catalog → scope-environment adapter (decision)

The mapping is **generator-emitted catalog data** (implemented with the view curation), not
hand-maintained in the resolver (consistent with the catalog charter): the RulesCatalog generator
emits, per event field / provider / context, a `v2Name` and `v2Type`. The resolver's friendly
-string → `RulesType` table: `bool→Bool`, `int`/`uint`/`ulong`/`long→Int`, `float`/`double→Float`,
`string→String` (no unsigned language type, spec §3.2); an unknown type string is a
**generator/build error, never a silent skip**. Loader-injected instants (`event.tick`,
`match.tick`) have no catalog event field and are added by the adapter. Net-message fields
carry no `v2Name` (payload matching is reserved). Providers map `entity.pawn.*→player.*` and
`entity.game.freeze_period→match.freeze_period`; the 13 v1 context ruleIds map via an explicit
generator dictionary (round-level→`round.*`, match-level→`match.*`, per-player→`player.*`;
e.g. `map_name→match.map`, `bomb_status→round.bomb_status`) — an unmapped context id is a
generator error, so the map can't silently drift.

### 4.2 Canonical conjunction order (decision — freeze-relevant)

A composed trigger condition can draw from five sources. The hash-frozen order is:

```
[lowered match: bindings in catalog key order]
  ∧ [view baked: filters in views.yaml order]
  ∧ [define where:]
  ∧ [site where:]
```

Merging the define's and the site's `match:` maps: union into one catalog-key-ordered lowering;
a duplicate key across define+site is an **error** (no silent last-wins). The **actor-binding
equality is NOT part of the hashed node AST** — it is a planner-side edge/source check outside
node identity (so two rulesets whose only difference is the implicit per-player actor bind
still dedup their shared stat nodes; per-player identity enters the preimage via the compound
scope axis, decision 5, not via the condition). `while:`-derived gates compose separately (§6
obligation 3). This order is added to spec §5 item 5 (§12).

## 5. Views & facets — the curation interface contract

Curated data file `tools/DemoViewer.NET.RulesCatalog/data/views.yaml` (checked in, read by the
generator; the app consumes only the generated catalog):

```yaml
views:
  kill:
    event: player_death
    binding: actor_slot          # actor_slot (default) | team | none
    actor: killer                # role whose perspective; required iff binding: actor_slot
    roles:                       # role → slot field on the event
      killer: KillerSlot
      victim: VictimSlot
      assister: AssisterSlot
    baked:                       # editorial filters, v2 expressions over event.*
      - "event.KillerSlot != event.VictimSlot"      # kill excludes suicides
    facets:
      enemy:    { type: bool, enrichment: enrich.kill.was_enemy_kill }
      teamkill: { type: bool, enrichment: enrich.kill.was_team_kill }
      trade:    { type: bool, enrichment: enrich.kill.was_trade_kill }
      headshot: { type: bool, field: IsHeadshot }   # NB: the wire field is IsHeadshot
      weapon:   { type: string, field: Weapon }
    availability: all            # or a profile list; drives coverage diagnostics
```

**`binding:` modes (decision 3):**
- `actor_slot` (default) — the view's `actor` role slot equals the ruleset player's slot; in
  `for: each_player` this lowers to the same slot-equality v1 uses, against `env.Slot`;
  in `for: match` there is no implicit binding. `match: {actor: any}` suppresses it.
- `team` — the ruleset player's **live** team (a `PlayerContextIndex`-backed per-event
  accessor, or an env refreshed on `player_team` — not frozen `env.Team`, which would invert
  after halftime) compared to the round-end winner. The winner comes from the existing
  `enrich.round.winner_team` gated by `enrich.round.has_winner` (v1 derives the winner from
  bomb/alive state — there is no trusted raw winner field on the wire), so it lowers through
  the `enrichment:` facet form with declared reads guaranteeing enrichment-before-read
  order. round_won/round_lost share the `round_end` event and a **`result: won|lost`**
  discriminator key in `views.yaml` (the curation convention: `won` = player.team ==
  winner, `lost` = player.team == loser). **Implemented gap:** `RoundEndEvent`
  is not emitted in GOTV demos (the winner is engine-derived from bomb/alive state, not on the
  wire), so the view fixtures pin the decided-round count + halftime-crossing structure only;
  **full per-player `binding: team` live-team attribution is deferred to the
  env-equivalence battery** (which must include a post-halftime round to pin the swap case).
- `none` — no implicit binding; in `for: each_player` every player's template fires on the
  event; roles (planter/defuser) stay readable as role handles. bomb_planted/bomb_defused
  use this — and the pilot's `plant_tick` parity depends on it (v1's `pp_plant_tick` fires on
  `bomb_planted` with no player condition, so every player's node captures the round's plant
  tick).

**Facet lowering forms** (closed set): `field:` (event field read), `enrichment:` (an
`enrich.*` output — existing machinery; ordering guaranteed by declared reads), `expr:`
(a v2 expression over `event.*` + roles). Curation owns the entries; the compiler implements
the three lowerings once.

**v2.0 curated view set** (each pinned by a fixture test vs demofile-net-derived golden
fixtures, oracle-only): `kill`/`death`/`assist` (player_death, `actor_slot`);
`damage_dealt`/`damage_taken` (player_hurt, `actor_slot`); `shot` (weapon_fire, `actor_slot`);
`blinded`/`blinded_enemy` (player_blind, `actor_slot`); `bomb_planted`/`bomb_defused` (bomb
events, `binding: none`); `round_won`/`round_lost` (round end, `binding: team`). `raw.<event>`
passes through with no actor convention; bare `net.<Message>` triggers are live (decision 7)
with `match:`/`where:` payload matching reserved.

## 6. Planner

Construct → graph, on existing primitives plus the one new collection-valued node. Numbered
obligations are pinned by the §10 graph-shape battery.

| Construct | Graph shape |
|---|---|
| `flag: true` + `on:` (+ optional `off:`) | `GenericBoolNode` (+ round-scoped variant per `per:`), trigger edge (+ deactivate edge for `off:`) |
| `flag: when:` | conjunction over declared inputs via a multi-source conditional edge (N sources ∧ predicate) |

**Flag-form disambiguation (resolver):** the document model stores both flag forms in one `KindArg` string, so `flag: true`+`on:` and the degenerate `flag: {when: "true"}` are indistinguishable by `KindArg` alone. Disambiguate by **`on:` presence** (`TriggerDef.On`, made nullable in the model): a flag with a trigger is the triggered form (`GenericBoolNode` + trigger edge); a flag with no trigger is the expression form (`KindArg` is the `when:` predicate over sibling stats). No model change needed — this is a spec-consistent rule (plan §1.3: `flag: true` always carries `on:`, `flag: when:` never does).
| `count: <trigger>` | value node + game-event increment edge |
| `count: <flag>` | value node + rising-edge action on the flag — **obligation 1** |
| `sum: <expr>` | value node + generated self-add `SetValue` edge |
| `capture:` first/last | scalar value node + guarded `SetValue` edge (first = write-once) |
| `capture:` list | collection-valued node `ValueNode<IReadOnlyList<T>>` + append edge = `SetValue(old + item)` (copy-on-append, never in-place); default/reset = shared immutable empty list |
| `compute:` | `ComputedStatNode`, round-end |
| `tally:`/`streak:` | v1 node kinds (renamed surface); now hashable (decision 4). **Model gap:** their threshold/window/min-streak kind-args are not yet on the `StatDef` model — the resolver gates them loudly (`resolve.unsupported-kind`). Compiling them needs a model extension + this planner row; a prerequisite for the corpus port, not a pilot blocker |
| `bucket:` | v1 `keyed_counter` node, v1 restrictions retained (the lifts come with the vocabulary wave). Same model gap: `key:` kind-arg not yet modeled; gated loudly; vocabulary wave |
| highlight | conjunction (a multi-source edge over its read set); its rising edge **auto-produces the timeline `RuleChainEvent` in the evaluator** — obligation 2; auto match-scoped `.count` node incremented by the registered rising-edge action |
| `while:` | parent-as-edge-source (live-dispatch-key filtering preserved); `.set` on scalar capture → hidden round-scoped bool twin; on a list → `count > 0` |
| `for: each_player` | `PerPlayerNodeTemplate` multiplication with env lowering (`Func<PlayerEnv, TEvent, …>`, behind `RulesetCompilerOptions.UseEnvLowering`) |

Obligations, in order:

0. **Bind params → literals** (decision 2): already done in resolve; the planner asserts the
   CheckedRuleset carries no residual `params.*` reference before hashing.
1. **Rising-edge targets must be logic nodes.** The engine invokes rising-edge actions only
   from `RecomputeLogicNode` (Conjunction/Disjunction). When `count: <flag>` targets a
   `flag: true`+`on:` flag (a `GenericBoolNode`), the planner wraps it in a single-input
   `ConjunctionNode` (`ConditionalEdge.From(bool)`) and routes the count off that. Anything
   registered via `AddRisingEdgeAction` (counts-on-flags, highlight `.count`) is a logic node.
2. **Emit `DeclaredReads`** on every v2 edge from the checker's read sets; emit
   multi-source edges via `ConditionalEdge.FromAll`; register the highlight `.count`
   increment via `AddRisingEdgeAction` with its written node declared — the **timeline event
   itself is produced by the evaluator's rising edge**, not by an action (obligation 2 in the
   highlight row). The v2 edge classes take `DeclaredReads` as a constructor/init parameter;
   v1 edge classes are untouched.
3. **Dedup with the resolved-identity hasher** (`Analysis.Rules` `RuleHasher` +
   `RuleNodeDescriptor`; stat-hash source = the planner's node table in dependency order).
   Preimage packing for multi-AST kinds: the **trigger condition AST** enters row 5 (the
   node's canonical expression); a **`while:` gate** enters row 7 (GateHash — the resolved
   hash of the gate node); a `sum:`/`capture:` **value selector** is a distinct hashed
   component appended to row 5's serialization (a documented two-slot form, `(cond … | value
   …)`), so a capture and a sum with the same trigger but different value selectors do not
   dedup. Compound scope axis (decision 5) is row 3. v1 chains keep the v1 hasher; **no
   cross-version dedup**.
4. **Cycle pre-pass.** Within-ruleset stat-reference cycle detection runs here (the checker is
   per-expression and cannot see cross-stat cycles): walk `CheckedExpression.References`
   (`IsStatReference`, including highlight `.count` reads), emit the spec's named-cycle-path
   build error. This makes the dependency-ordered hashing terminate. (Correct obligation 3's
   old "already rejected by the checker" claim.)
5. **Env-vs-constant duality**: both lowerings behind `UseEnvLowering`; the equivalence
   battery (identical evaluation, golden demos + a post-halftime round, both modes) gates the
   default flip. Constant lowering is the fallback until it passes.
6. **Subject reads** → `EntityChangeScanner.GetPreFrameValue(provider, subjectSlot)` with
   the compile-time provider-registration assert (loud-throw arm landed with the generic
   entity providers).
   - **At-fire vs settle-time entity reads (entity-read site parity).** A `player.entity.pawn.*`
     read has two distinct lowerings depending on the site it appears in, and both are correct:
     - **At-fire** (`where:` / value-selectors / `while:`): compiled through
       `ExpressionCompiler.CompileEventCondition` / `CompileEventValueSelector`, so the read
       resolves the subject slot's **pre-frame value relative to the in-flight event frame** —
       the moment the triggering event fires. This is the timing kill/damage stats want.
     - **Settle-time** (`compute:` — round-end/live — and `flag: when:` — flag-eval): these are
       pure node-logic sites with **no event frame**, so they cannot reach the at-fire seam. The
       planner materializes the subject's entity value as an always-active
       `EntityValuePullNode` (team-aggregate style; `Nodes/EntityValuePullNode.cs`) registered in
       `localLookup` under the read's path, read via the same `GetPreFrameValue` accessor at the
       settle point — the entity state at the most recent frame advance (the **round-end** state
       for a round-end `compute:`; the flag-eval point for a `when:`). A `compute:` remaps the
       read into the node-expression compiler like any sibling/context read; a `flag: when:`
       lowers to a `MultiSourceConditionalEdge` over the pull-node (the reflective-value path),
       and — because a pull-node is writer-less and would otherwise never be bucketed into the
       logic-recompute index — an `EntityPullNodeSettleEdge` fires on each `$round_end` to drive
       the flag's round-end recompute. Provider gating is shared: `UnionV2EntityReads` folds the
       read's provider into the scanner snapshot set regardless of site (its `EntityReads` lists
       it), and a no-demo build raises the identical "requires per-player entity providers and a
       player slot" marker the at-fire seam raises. **v2-only**: the pure-v1 lowering is untouched.
7. **Coverage skips** carry a `RulesetCoverageDiagnostic` on the optional `BuildResult` list
   (decision 6); consumers: `ComputeRuleDiagnostics` maps it to a `RuleDiagnostic` row; `rules
   check --demo` prints it. Never silent.
8. **Node-map keying** (decision 1 + badge coexistence): every compiled stat registers under
   `{ruleset_id}.{stat_id}` in the game/per-player node maps, **qualified-only, no bare-id
   fallback** — this is what makes the v1 bare-id collision hazard structurally impossible for
   v2, and what lets `ResolveRuleFireCount`/`ComputeRuleDiagnostics` resolve v2 fire counts.
9. **Sticky `round.bomb.was_planted`**: two-line `BuiltinContexts` addition (bool, set on
   `bomb_planted`, reset per round) + catalog regen. (Resolver interim: the adapter's
   inject-if-absent supplies it as a `Bool` scope symbol so the pilot *resolves*; the planner
   must add the **real** sticky context so the pilot *evaluates* — the adapter injection is a
   type-level stand-in only, it produces no runtime node. Regen `catalog.json` + its drift
   baseline in the same commit.)

## 7. `show:` surfacing

- `scoreboard:` entries map onto the existing column projection (v1 `columns:` path): stat ref
  → column with `label`, display `group`, and `boards`. A plain stat ref's `boards` default
  from its `per:`; a **highlight ref surfaces `<id>.count`, whose node is match-scoped**, so
  its board defaults to the match board regardless of the highlight's own `per:` (the auto
  `.count` node is the referent — this is consistent, not a contradiction).
- `tables:` map onto the per-round export machinery (v1 `outputs:` path) keyed by the closed
  dimension registry (`player_round` first). **A highlight referenced bare in a table column
  binds to its per-round fired state** (a bool: did the rising edge occur this round),
  distinct from the match-scoped `.count` a scoreboard ref means. The planner must give the
  table column a node to bind — the per-round highlight conjunction's activation. The pilot's
  `Achieved` column depends on this.
- **List flattening lives only in serializers**: CSV/table emit `<Label>Count` +
  `<Label>1..N` (N = max observed across rows, deterministic order); JSON emits `{ count,
  values[] }`. No width hints in the schema. The UI table binds the rendered string of the
  collection value (per the snapshot contract).

## 8. `exports:` / `use:` (composition)

- Absent `exports:` = all stats/highlights exported (advisory lint); `use:` lists the
  rulesets this file may reference — a validation allowlist.
- Directory load builds the export graph after all documents parse; qualified `ruleset.stat`
  references resolve against it. Distinct attributed errors: unknown ruleset, unknown stat,
  not-exported, not-in-`use:`.
- **Read-scope rule (decision, corrects the draft's mis-attribution):** same-scope and
  per-player→match references are legal; a **match→per-player reference is an attributed
  error** (no player binding exists at match scope). The "same-scope only" phrasing belonged
  to the (deferred) highlight-subscription seam, not these reads.
- Cross-ruleset cycles are a build error naming the cycle.
- **Two-tier overlay (decision):** v1 chain ids and v2 ruleset ids **share one id namespace
  per tier**; a same-id v1-chain/v2-ruleset pair within a tier is a duplicate error. A user
  ruleset may not silently override a shipped v1 chain (or vice versa). `RulesetDoc.Enabled`
  (default true) allows disabling; a disabled ruleset drops after overlay, and export
  re-resolution treats its exports like a removed ruleset (attributed error + the established
  user-tier containment).
- Mid-message cross-reads are legal from day one (read-aware ordering is on the branch).
- **Collision validation (decision, resolves the unverified `RuleIdCollisionValidator`
  concern):** v2 intra-document collisions are the §3 structural check; cross-version
  collisions are the shared-namespace duplicate error above; v2 per-player registration is
  qualified-key-only (obligation 8), so the v1 bare-id hazard cannot arise. The v1 validator
  tests stay; a new v2 collision battery pins the cross-version and qualified-key cases.

## 9. Schema generation

`cs2demokit-rules.schema.json` emission in the RulesCatalog generator: kind discriminators with
`if/then` per kind, per-view `match:` facet enums with `markdownDescription` + per-source
availability, destination enums, whole-stat `defaultSnippets`. **Two explicit shape lists
(decision, resolves the §9-conflation findings):**

- **Emitted live in v2.0** (schema-valid, loader-accepts): duration literal forms,
  role members, `binding:` modes, `off:`, `actor: any`, `for_each:`.
- **Emitted reserved (schema-present, loader-rejects "reserved — not yet implemented"):**
  multi-source condition lists, bucket `key:`-as-list + reducer + `per:` axes, the team
  namespaces, `net.*` `match:`/`where:` payload matching (bare `net.*` triggers are live),
  `show: as:`, the `"m:ss"` slot scalar, map-valued `define:`,
  `catalog_version`/`min_app_version`.

Committed, modeline-referenced, drift-gated (regen == committed). **Note (unverified finding,
kept):** adding the `views` family and `v2Name`/`v2Type` fields grows `catalog.json`, so the
`CatalogDriftTests` committed baseline must be regenerated in the same commit — the drift gate
will otherwise fail. The views/catalog workstream owns this regen.

## 10. Test strategy

- **Document-model and resolver batteries are demo-free** (document construction, resolution,
  checking against the embedded catalog, error positions/text). Filtered TUnit classes, memory
  rules apply.
- **Planner batteries**: hand-built CheckedRuleset IRs → graph shape assertions (node kinds,
  edge sources, `DeclaredReads` content, count-on-flag conjunction wrapping, highlight
  timeline vs `.count` action split); the **structural-dedup corruption-class property gates** —
  hash-equal ⇒ identical evaluation, plus explicit hash-**distinctness** cases: per-player
  `per:round` ≠ per-player `per:match` (decision 5), capture-value-selector ≠ sum-value
  -selector with the same trigger (obligation 3), a flag with `off:` ≠ without; the
  env-vs-constant equivalence battery incl. a post-halftime round; capture-list
  append/reset/serialization fixtures; `.set`-twin semantics; coverage-skip diagnostics.
- **Hasher-core re-baseline** (decision 4): after `RuleNodeKind` gains Tally/Streak and the
  preimage rows widen, re-run the 117-test `Analysis.Rules` battery and re-baseline the
  preimage-snapshot golden as a deliberate, commit-audited change.
- **Pilot golden**: `post_plant_double.rules.yaml` on the reference demo produces
  per-round/per-player results identical to v1 `achievement-post-plant-double.yaml` (modulo
  the documented v1 wart list, expected empty). The end-to-end exit gate for this stage.
- **View fixture pins are the curation gate** (each curated view vs demofile-net-derived
  fixtures; oracle-only rule stands).
- **`rules check` v2**: demo-less checker dispatch surfacing §8-contract diagnostics;
  `--demo` compiles composed v1+v2 configs; `*.test.yaml` either gains a `rulesets:`/`stats:`
  expectation vocabulary or v2 fixture support is explicitly deferred with the pilot
  gated by the TUnit golden only (name the choice in the PR).
- Full Analysis suite + golden bench + batched App suite on the merged tree at integration
  checkpoints (one heavy process at a time, per the machine memory rules).

## 11. Work split and sequencing

| Workstream | Items | Dependencies | Demo usage |
|---|---|---|---|
| document model | document model + YAML mapping + structural validation | spec + this doc | none |
| views/catalog | `views.yaml` + catalog `views` family + `v2Name`/`v2Type` adapter data + drift-baseline regen + oracle fixture pins | §4.1/§5 (this doc) | reference + post-halftime demo (owns the heavy slot) |
| hasher core | decision 4/5: `RuleNodeKind` Tally/Streak + `ScopeAxis` widening + preimage rows + hasher re-baseline | spec §12 amendments | none |
| resolver | resolver + adapter + checker integration | document model + the catalog shape + hasher core | none |
| planner | planner + composition seam + pilot port | resolver + hasher core + the engine track (landed) | reference demo (gate runs) |
| surfacing/schema/CLI | show + serializers; schema generation; rules check | resolver (shapes settled) | reference demo (rules check --demo) |

The document model and the hasher-core kind/axis change start first (both demo-free); the
views/catalog workstream holds the machine's only heavy slot. Schema view enums belong to the
surfacing/schema workstream, not views/catalog: curation owns `views.yaml` + the catalog
`views` family; the schema generator consumes that family to emit the per-view `match:` enums.
The resolver follows the document model, the catalog shape, and the hasher core; the planner
follows the resolver; surfacing overlaps the planner. Everything lands on
`feature/rules-v2-phase0`, cherry-picked and verified on the merged tree.

## 12. Spec amendments to land before the freeze

These are the pre-freeze corrections to `docs/rules-v2/rules-v2-spec.md` (still a draft at the
time) that this contract's decisions require; each must appear in all three freeze artifacts
(spec, `cs2demokit-rules.schema.json`, preimage-snapshot golden):

1. §6 row 1: add `tally`, `streak` to the kind list (decision 4).
2. §6 row 3: widen the scope axis to the compound `(For × Per)` product —
   `match | round | player_match | player_round` (decision 5).
3. §6 row 8 (keep-spec): add tally thresholds and streak window/min-streak kind args
   (decision 4).
4. §6 rows 5/7/8: document multi-AST packing — trigger condition in row 5 with an appended
   **length-framed** value-selector slot (`(cond <len>:<c> | value <len>:<v>)`, carried from
   the hasher-core implementation — the plain-delimiter form was a collision surface); `while:`
   gate in row 7; **tally thresholds serialize as `(min, target)` pairs** (the emit-target node
   id is behaviorally load-bearing — v1 hashes it — so Min-only would false-dedup two tallies
   emitting to different counters; corrected after review). **`off:` is
   deferred to the planner:** a flag with `off:` must hash differently from one
   without (the deactivate trigger changes behavior), but its descriptor slot is added when
   the `off:` model settles and the first `off:`-bearing rule is compiled; no shipped
   rule uses `off:` yet, so no live false-dedup exists in the interim. Until then the
   `flag off: ≠ flag without` distinctness pin is intentionally absent — the planner adds both.
5. §4 namespace tree: add `this` (non-stat Value symbol, enclosing stat's value type) with
   the `(ref this)` preimage marker (decision 7).
6. §4/§5: net-message family — bare `net.<Message>` triggers are live; only `match:`/`where:`
   payload matching is reserved (decision 7, spec's placement wins over the draft).
7. §5 item 5: the full composed-condition canonical conjunction order (§4.2 of this doc), and
   that the actor-binding equality is outside node identity.
8. §2/§3.4/§5.4: mark map-valued `define:` reserved for the vocabulary wave (decision 7).
