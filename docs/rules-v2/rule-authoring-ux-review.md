# Rule Authoring UX Review — the road to Rulesets v2

**Date:** 2026-07-09, revised 2026-07-10 on owner design notes: actor-anchored event views
replace the `by:` role keyword and `me` noun (the canonical binding noun is `player`);
scoreboard entries carry `group:` + `boards:`; `keep: list` compiles to a collection-valued
node — `List<T>` as the node's value type, no size declaration anywhere, the serialization
layer flattens, and the default/reset value is the shared immutable empty list, never null.
A second round the same day, after lifting the review's self-imposed "no engine
changes" constraint, added the engine track (declared reads, multi-source conditional edges,
rising-edge dirty routing), entity vocabulary as catalog data, and a follow-on
vocabulary/composition wave — see the round-2 additions in §3.1 and the restructured §3.2.
Owner ratifications same day: rising-edge dirty routing ships now; live `compute:` is deferred
to a golden-diff dry-run after the engine track lands; team aggregates approved for
implementation (single-writer index rule, `Connected`-flag disconnect fix, freeze-end relative
economy); environment-based per-player lowering adopted. Standing goldens policy: goldens are
the best-effort statement of correctness — when a defect made them wrong, the fix and
re-baseline always land.

The redesign this review proposed has since shipped. `rules-v2-spec.md` is the frozen as-built
contract, and the v2 engine + authoring surface are in the tree
(`DemoViewer.NET.Analysis.Rules`, `RulesCatalog/data/views.yaml`). The deferred lines below
were accurate as of writing; the standing residual gate is the live paper-test
(`rules-v2-paper-test-kit.md`). The document is retained because production code cites its
§3.3 as design rationale.

The review weighed the authoring surface, the engine constraints, the data surface, and app
integration against outside research (business rules engines, event-automation platforms,
detection DSLs, editor/language tooling), then judged three competing design directions —
lower-the-floor, language-architect, tooling-first — from three perspectives: a novice
advocate, an engine guardian, and an implementation pragmatist. This document is the
synthesis.

**The question under review:** the analysis engine's graph runtime — atomic-condition nodes,
activator/action edges, structural dedup, condition caching, parallel digests — is sound and
fast. But the *authoring surface* failed its original goal: less-technical users cannot write
rules without understanding event enrichment, entity-scoped nodes, per-player templating, and
evaluation-order subtleties. What is the optimal shape of the rules schema, the data-access
pattern, and the tooling? Backwards compatibility is explicitly **not** required.

---

## 0. Executive summary

Today's schema exposes the graph's *implementation vocabulary* (parents vs. triggers, transient
`enrich.*` nodes, fire-order contracts, `$logical` sigils, the hidden all-bools conjunction)
instead of the author's *domain vocabulary* (actor, weapon set, "the Nth kill", achievement).
The single most damning artifact is our own showcase example: the post-plant-double rule takes
**152 lines**, copy-pastes one condition **six times**, hand-writes **five** near-identical
capture rules because there is no list construct, and needs a comment explaining topological-sort
ordering for its correctness.

The proposed replacement — **Rulesets v2** — is a breaking redesign of the surface only. The
engine underneath keeps every node type, edge type, and optimization; v2 is a new compiler
front-end that *desugars* into the same graph. Three design laws drive everything:

1. **Structure over strings.** Everything a JSON schema can enumerate becomes a YAML key or enum
   (event views, actor roles, event facets, stat kinds, output destinations). Expressions
   survive at a handful of named leaf slots, in one grammar, over one typed namespace tree.
2. **Ids are map keys, not list items.** Duplicate ids become impossible; symbol tables,
   go-to-definition, and rename fall out of the document structure.
3. **One generated Catalog feeds everything.** Events + roles + facets + contexts + entity
   providers are emitted from the engine's own registries into a single typed, documented
   artifact that drives the JSON schema, the expression type-checker, the in-app data browser,
   and the reference docs. Nothing about the data surface is hand-maintained twice.

Headline outcomes:

- The 152-line post-plant-double example becomes **~40 lines with zero duplication** (§1.4).
- Actor slot boilerplate (`event.KillerSlot == player.slot`, present in nearly every per-player
  rule) disappears entirely: wire events are exposed as **actor-anchored views** (`kill` /
  `death` / `assist` are three views of `player_death`), and in a per-player ruleset the view's
  actor *is* the ruleset's player — `count: kill` is the whole rule for "my kills", and the
  wrong-slot-field silent failure becomes *inexpressible*.
- The `enrich.*` magic namespace becomes typed, event-scoped facet keys (`enemy:`, `trade:`)
  that simply don't exist under the wrong event — the stale-transient-read trap becomes a schema
  error.
- All fifteen silent-failure classes found by the audit (typo'd parent gates falling back to
  root, unknown `on_satisfied` targets, ignored net-message conditions, inert game-scope
  expression rules, …) stop being silent: they become either **working constructs**
  (net-message conditions are wired live in the first quick-wins wave; game-scope computes are
  legal in v2) or
  **loud, attributed errors** — at load time where catalog-checkable, via fire-count/coverage
  lints for the demo-dependent remainder.
- Tooling arrives in cost order: a generated JSON schema makes VS Code's stock YAML language
  server complete event names, facets, and whole-rule skeletons **before any custom editor code
  exists**; an in-app workbench with live fire-counts and Home-Assistant-style traces follows;
  a full LSP is deliberately last and may never be needed.

The plan is phased so each phase ships standalone value (§3.2), starting with a batch of quick
wins that need no schema change at all: per-rule fire-count badges with a "never fired" lint,
restored line/column diagnostics, the silently-ignored net-message `condition:` wired live, and
two latent player-materialization bug fixes.

---

## 1. The ideal authoring experience

### 1.1 Who we are designing for

Four segments, in priority order:

| Segment | Needs | Today's reality |
|---|---|---|
| **Consumers** — install a shared rule, never author | one-click install, a small form for parameters, trust/compat signals | drop a file in a folder; no parameters; no compat signals |
| **Enthusiasts** — copy-and-modify authors, no programming background | domain vocabulary, completion, examples, "why didn't it fire?" | must learn slot fields, enrich lifetimes, fire ordering from a 754-line doc |
| **Power authors** — comfortable with YAML + expressions | reuse primitives, repetition constructs, fast edit-test loop, CI | copy-paste as the documented pattern; edit → full re-run → read error |
| **Developers** — extend the vocabulary itself | add events/facets/providers with tests | C# edits at 3+ sites plus hand-updated doc appendices |

The original pitch — "atomic rules so less-technical users can write them" — failed not because
atomicity was wrong but because the atoms were graph atoms, not domain atoms. v2 keeps the
atomicity (every stat is still exactly one node) and changes what the atoms are made of.

### 1.2 The experience, end to end

**Authoring.** The author loads a demo and opens the Rules workbench (today's "📁 Rules" button
grows into a tab). *New Ruleset → Achievement* inserts a complete working skeleton from a schema
`defaultSnippet` — a capture, a count, a highlight, a scoreboard line, with placeholders. Typing
`on:` offers the event catalog with hover docs: *"`kill` — a player death, from the killer's
perspective; in a per-player ruleset the killer is this ruleset's player. Sibling views:
`death` (the victim's perspective), `assist` (the assister's). Facets: `enemy`, `headshot`,
`trade`, `weapon`… Fires on all demo sources."* Under `match:`, completion offers **exactly
that view's** facet vocabulary. Choosing `kill` already *is* the actor binding — there is no
slot arithmetic and no role keyword to get wrong; picking `death` instead is how you say "count
the times I was killed". A data browser alongside shows the
same catalog as a tree with **live values from the loaded demo** at the current seek position;
dragging a field into the editor inserts the correct path (the n8n lesson: nobody should ever
guess a field name).

**Feedback.** Save triggers a re-check (<1 s, static) and — with a demo loaded — a re-evaluation
(~3.5 s; the existing Re-run path already skips parsing). Every stat gains a **fire-count
badge**; zero-fire stats are flagged with the first unsatisfied gate named. Nothing in the file
can fail silently: unknown identifiers, wrong-scope reads, type mismatches are red squiggles at
edit time with did-you-mean, all carrying `file(line,col)`.

**Debugging.** The trace panel answers "why didn't round 12 count?" the way a non-programmer
thinks: it lists every candidate event with **per-clause verdicts and the values seen** —
`killer is the player ✓ · enemy ✗ (victim was a teammate)` — plus a near-miss view (rounds where the counter
moved but the highlight never rose). Selecting a firing highlights the taken path on the
existing MSAGL graph, scrubbing by message; clicking a tick seeks 2D playback. This is Home
Assistant's automation-trace model, the best-in-class reference, assembled largely from parts
this repo already has (the per-edge applied-fire recorder already on main, message snapshots,
the graph view).

**Sharing.** The ruleset carries `params:` — typed inputs with defaults. A teammate who installs
the file gets a **small auto-generated form** ("Minimum kills: [2]") and never touches logic.
*Add test* writes a paired `post_plant_double.test.yaml` pinning a demo fixture and expected
detections; `rules check --test` runs it in CI. Later, once the blueprint gallery exists (a
late, demand-gated phase), shipped and community rulesets surface as cards with forms — a true
zero-YAML on-ramp — where filling a form *generates a ruleset file* (one representation; "take
control" just means "open the file").

### 1.3 The schema

A rules file is **one ruleset** (`<id>.rules.yaml`). Top-level keys: `ruleset:` (id), `title:`,
`summary:`, `for: match | each_player`, `use:` (declares which installed rulesets' exports this
file may reference by qualified name — purely a validation allowlist, no aliasing), `params:`,
`define:`, `stats:`, `highlights:`, `show:`. The v2 loader dispatches on the top-level key
(`ruleset:` = v2, `chains:` = legacy) so both coexist during migration.

**`define:` — named triggers and lists** (Falco's macros + lists, the single feature that kills
the copy-paste disease):

```yaml
define:
  util_weapons: [hegrenade, inferno, molotov]
  my_util_hit:
    on: damage_dealt              # view of player_hurt from the attacker = the ruleset's player
    match: { enemy: true, weapon: in util_weapons }
```

User files can extend shipped lists and override shipped defines (append/override overlay, with
a blast-radius lint showing which shipped rulesets rebind).

**`params:` — typed inputs** (`{ type: int, default: 2, min: 2, max: 5 }`) that render as
consumer forms and compile as constants. ~80% of the blueprint value at ~5% of the cost.

**`for_each:` — compile-time expansion** for literal-variation families (the CTWins / TWins /
CTLosses / TLosses ×4 copy-paste): `for_each: { side: [CT, T] }` with `"{side}_wins"` keys.
Expansion happens before hashing, so dedup sees plain stats.

**`stats:` — a map of id → stat.** The *kind* is a discriminator key (which lets the generated
schema drive `if/then` completion and per-kind skeleton snippets). Each kind is exactly one
graph node:

| Kind | Meaning | Replaces (v1) |
|---|---|---|
| `flag: true` + `on:` | set when the trigger fires (optional `off:`) | triggered `bool` |
| `flag: when: <expr>` | true while an expression over sibling stats holds | parents-only auto-activate `bool` |
| `count: <trigger\|flag>` | +1 per match, or per rising edge of a flag | `counter`, `on_satisfied.increment` |
| `sum: <expr>` + `on:` | accumulate a value per match | the `set` + `"rule.value + …"` incantation |
| `capture: <expr>` + `on:` + `keep: first\|last\|list` | record value(s) at matches (default `last` — v1 `value` semantics); `list` = one collection-valued node, no size declaration | `value`, and the `tick_1..5` copy-paste |
| `compute: <expr>` | derived at round end (explicitly documented as round-end) | `expression` |
| `tally:` / `streak:` / `bucket:` | unchanged semantics, renamed | `threshold_tally` / `windowed_streak` / `keyed_counter` |

Common stat properties: `per: round | match` (replaces `reset:`), `on:` (event view or define
reference), `match:` (structured bindings), `where:` (expression escape hatch), `while:` (state
gate), `label:`.

**Triggers are structured, not strings.** The anatomy is
`on:` + `match:` + optional `where:`/`while:`:

- `on: kill` — a **curated, actor-anchored view** of a wire event, resolved per demo-source
  profile exactly like `$logical` today, first-wins guards included. The view's name says whose
  perspective it takes, and one wire event yields one view per role: `player_death` → `kill`
  (the killer's), `death` (the victim's), `assist` (the assister's); `player_hurt` →
  `damage_dealt` (the attacker's), `damage_taken` (the victim's). In a `for: each_player`
  ruleset the view's actor **is the ruleset's player** — `count: kill` means "my kills" with no
  binding written at all (rare escape hatch: `match: { actor: any }`); in a `for: match` ruleset
  there is no implicit binding. Views bake the obvious editorial filters (`kill` excludes
  suicides — v1's hand-written `KillerSlot != VictimSlot` clause), pinned by fixture tests. The
  `$` sigil and the author-facing `requires:` ritual die; a source that can't bind a view skips
  the stat with a visible coverage diagnostic instead of silently reading zero. Raw wire names
  stay available as `on: raw.player_death` (no actor convention) for the RE workbench and
  experts.
- `match: { enemy: true, weapon: in rifles }` — pure YAML keys, so the *stock* YAML language
  server completes them from the generated schema. There are no slot fields and no role keyword
  to pick: **choosing the view is the binding** (`kill` vs `death` vs `assist`), so the audit's
  #2 Critical pain — wrong slot field, silent — becomes inexpressible. Facet keys (`enemy`,
  `trade`, `headshot`, `capped_damage`…) are today's enrichments, but **typed, lexically scoped
  to their view, and defined relative to its actor** (`enemy: true` under `kill` = the victim
  was an enemy; under `death` = the killer was): `trade:` does not exist under `damage_dealt`,
  so the audit's #5 pain (reading `enrich.kill.*` during `player_hurt`, silently getting a
  stale default) becomes a schema error. The view's *other* roles stay readable as typed player
  handles (`victim.*` under `kill`, `killer.*` under `death`) in `where:` expressions. Values
  are unary tests (FEEL/ZEN style): literal, `in <list>`, `">= 5"`, `[2..5]`.
- `where: <expr>` — the expression escape hatch, about the event.
- `while: <ref>` — a state gate (a flag, a built-in context bool, or a capture's `.set`
  pseudo-member). Compiles to parent-as-edge-source, preserving the live-dispatch-key
  filtering.

**`highlights:` — explicit satisfaction.** Replaces `on_satisfied` and the hidden "AND of all
bool rules" conjunction (audit pain #7 — the reason opening-kills and opening-deaths must be
separate chains today). A highlight has an explicit `when:` expression, a `per:` key governing
its rising-edge scope (default `round` — one firing per round maximum), a `title:` template
rendered into the Highlights view (Kubernetes `messageExpression`-style), and an automatic
counter exposed as `<id>.count` — **always match-scoped** (total rising edges across the demo)
regardless of the highlight's own `per:`, so its default scoreboard board is the match board.

**`show:` — one surfacing block** replacing both `columns:` and `outputs:` (audit pain #9).
The built-in destination is `scoreboard`, whose entries are `{ stat, label, group, boards }`:
`group` names the display category the UI clusters columns under (`combat`, `utility`,
`economy`, `objectives`, …) and `boards` says which scoreboard(s) the column appears on —
`[round]`, `[match]`, or both — **defaulting from the stat's `per:`** (`per: round` → the round
board, `per: match` → the match board), so authors only write it to override. This untangles
v1's `group: round|game`, which did double duty as both sampling scope and display category.
Custom `tables:` take dimension keys from the closed registry; a capture list referenced in a
table stays a collection internally and is flattened by each format's serializer
(`<Label>Count` + `<Label>1..N` for CSV/tables, a real array for JSON — §3.1). The one
property v1 got right — one declaration reaches UI table + CSV/JSON export with zero wiring —
is preserved.

### 1.4 The centerpiece: post-plant-double rewritten

Today: `rules/achievement-post-plant-double.yaml`, 152 lines, the kill condition copied 6×, five
copy-pasted `pp_kill_tick_N` rules, a comment explaining topo-sort ordering, a dedicated gate
bool with a comment explaining why `bomb_status` doesn't work, and a double declaration across
`columns:` + `outputs:`. In v2:

```yaml
# yaml-language-server: $schema=./dv-rules.schema.json
ruleset: post_plant_double
title: Post-Plant Double
summary: 2+ enemy kills after the bomb plant in one round, with clip-ready tick context.
for: each_player

params:
  min_kills: { type: int, default: 2, min: 2, max: 5 }

define:
  post_plant_kill:                    # the condition the old file repeated six times
    on: kill                          # view of player_death from the killer — in a per-player
                                      # ruleset the killer IS the ruleset's player
    match: { enemy: true }
    while: round.bomb.was_planted     # sticky per-round built-in; survives defuse/detonation

stats:
  plant_tick:
    capture: event.tick
    on: bomb_planted
    per: round

  post_plant_kills:
    count: post_plant_kill
    per: round

  kill_ticks:
    capture: event.tick
    on: post_plant_kill
    keep: list                        # ordered per-round capture list — was five copy-pasted
    per: round                        # rules; one collection-valued node, no size to declare

highlights:
  post_plant_double:
    when: post_plant_kills >= params.min_kills
    per: round
    title: "{player.name} — {post_plant_kills} kills after the plant (round {round.number})"

show:
  scoreboard:
    # boards: is inferred from each stat's per: (here match) — write it only to override
    - { stat: post_plant_double.count, label: PostPlantDoubles, group: objectives }
  tables:
    post_plant_double_context:
      per: player_round
      columns:
        - { stat: post_plant_double,  label: Achieved }
        - { stat: post_plant_kills,   label: PostPlantKills }
        - { stat: plant_tick,         label: PlantTick }
        - { stat: kill_ticks,         label: KillTick }    # serializer flattens →
                                                           # KillTickCount, KillTick1..N
```

~40 lines, zero duplication, and — crucially — **nothing an author must know about evaluation
order, transient lifetimes, or hidden conjunctions.** The topo-sort write-before-read guarantee
that the old file's comments explained is now consumed by the compiler (the generated append
edge, with readers ordered after writers), never by the author. A teammate who installs this
sets `min_kills: 3` in a form.

The same constructs collapse the rest of the corpus: `kast.yaml`'s
`event.KillerSlot == player.slot && event.KillerSlot != event.VictimSlot` becomes simply
`count: kill` (and deaths/assists are `count: death` / `count: assist` — the view choice is the
binding); `player-stats.yaml`'s 10-pistol `||` chain becomes `weapon: in pistols` against a
shared list; its CT/T×win/loss families become one `for_each:`.

### 1.5 Data access and discovery

**One namespace tree, typed, catalog-backed, with a published per-site availability table**
(GitHub Actions' contexts design — the single artifact the IntelliSense ambition hangs on):

| Namespace | Contents | Legal in |
|---|---|---|
| `event.*` | canonical typed fields of the trigger's event | `where`, `capture`/`sum` values |
| facet keys | view-scoped facts (today's enrichments), defined relative to the view's actor: `enemy`, `trade`, `capped_damage`, `winner_side`… | `match:`/`where:` under a matching view only — statically checked |
| role handles | the view's *other* roles as typed player handles: `victim.*` under `kill`, `killer.*` under `death`, `assister.*` | `where:` and value sites, under views that expose them |
| `player.*` | the ruleset's player (v1's established noun, kept — implicitly bound as each view's actor): `slot`, `team`, `name`, `alive`, `survived`, `traded`; entity-backed `health`, `armor`, `equipment_value`, `active_weapon_class` (pre-frame digest semantics stated in hover docs) | per-player rulesets, all sites |
| `round.*` | `number`, `active`, `phase`, `bomb.status`, `bomb.was_planted` (sticky), `no_deaths_yet` | all sites |
| `match.*` | `map`, `live`, `regulation`, `half`, `tick` | all sites |
| bare stat id / `this` | sibling stats; own current value | `when`, `compute`, `where`, values |
| pseudo-members | `.set` (captured at least once; on a list ≡ `count > 0`), `.count` (a capture list's length / a highlight's total firings — a highlight's `.count` is always match-scoped), `[n]` (capture element) | reference sites |
| `params.*`, list names | compile-time constants | all sites |

This deletes v1's ~10 inconsistent name families with dual spellings (audit pain #6):
`context.player.traded` vs bare `traded`, `$round_end` vs `round_officially_ended`,
`enrich.hurt.capped_damage`, auto-created tally targets referenced but never declared, etc.

**Round-2 data-surface growth** (mechanics in §3.1's round-2 additions):

- **Entity vocabulary becomes catalog data, not C#** — a `providers:` catalog family declares
  generic field-path providers (`{ name, scope, entity, path, type, optional via: handle-hop }`)
  compiled by two generic classes over the existing seen-gated `EntityState` indexer. Adding
  `player.money` becomes a data-file line validated against the demo's schema at scanner prime.
- **Role handles gain entity-backed members**: `victim.health` / `victim.armor` /
  `victim.equipment_value` / `victim.active_weapon_class` under `kill`, `killer.*` under
  `death` — pre-frame digest reads keyed by the event's subject slot (the same substrate
  `enrich.hurt.victim_health_before` uses today). Legal in `where:` and value sites; pre-frame /
  dead-subject semantics in hover docs.
- **Team/enemy aggregates** (`round.team.alive`, `round.enemies.alive`, team counts, team
  equipment sums, clutch state as typed facets) — the round-1 vocabulary ceiling — are designed
  as a split architecture (event-driven alive counts, digest-sampled economy) and
  **settled for implementation** in the follow-on vocabulary wave; the three correctness
  decisions are recorded in §3.3 risk 1.
- **Cross-ruleset reads with `exports:`** — a ruleset declares its public stats/highlights
  (absent block = all exported, with a lint); other rulesets reference them as
  `ruleset.stat` (qualified form only); the Catalog surfaces installed rulesets' exports to
  completion and the data browser. `extends: <ruleset>` delta files append new stats, replace
  same-id stats whole (no sub-key patching — rejected), and `remove:` deletes, with
  blast-radius lints naming every reader.

**The expression language: rebuilt in-house, spec-first.** CEL, JSONata, and DynamicExpresso
were evaluated seriously; the verdict — weighing engine integrity hardest — is to keep an
owned language with CEL's *discipline* rather than CEL itself, because:

- Structural dedup requires **owning a canonical AST** — hashing a third-party parse tree
  is fragile, and hash-equal must mean behaviorally interchangeable (nodes are shared by
  reference identity).
- Logic-node condition caching requires provable purity and statically enumerable read
  sets — trivial to enforce in a small owned grammar, adversarial in the immature .NET CEL
  ports, impossible to guarantee on a C#-subset surface.
- The audience is non-programmers; C# semantics and error messages are the cliff to avoid.

What we steal from CEL: a published EBNF in the repo, a typed checker against the Catalog's
per-slot scope environments, no Turing-completeness, a closed function set, exception-free
evaluation. Fixes over today's compiler: `and/or/not` word forms, unary minus (currently a
parse error), `in` with list refs, `contains`/`startswith`, `min/max/abs`, uint↔int coercion,
FEEL-style null semantics (missing values are null, null comparisons false — a 60k-tick replay
never throws), and a **hard EOF check** (today's tokenizer silently drops unknown characters and
trailing tokens — a malformed condition can silently truncate).

**Discovery is triple-redundant from one artifact.** The generated Catalog feeds: (a) the JSON
schema — event views, facet keys per view, stat kinds, destinations as enums with
`markdownDescription`s including per-source availability; (b) the in-app data browser with live
demo values and drag-to-insert; (c) generated reference docs replacing the hand-maintained
appendices, with a CI test asserting docs == catalog (killing the drift the audit found between
docs, schema, and loader). View/facet definitions live in a curated data file (not C#) wherever
they map onto existing events and enrichments, so growing the vocabulary is data curation.

### 1.6 Tooling

Cost-ordered; the schema was shaped so the cheap tier is already transformative.

| Layer | What | Effort |
|---|---|---|
| **Catalog + schema generator** | new `tools/` generator (same pattern as the existing Codegen project) reflecting EventRegistry, enrichment declarations, BuiltinContexts, plus the curated view/facet/`providers:` data files → `catalog.json` + `dv-rules.schema.json` (enums, hover docs, `if/then` per kind, whole-stat `defaultSnippets`). Modeline-injected into provisioned files. VS Code + stock yaml-language-server then completes views, per-view `match:` keys, kinds, destinations — zero custom editor code. | **M** |
| **`rules check` CLI** | a verb on AnalysisBench (which already accepts a rules dir): full static validation without a demo; `--demo x.dem` adds coverage lints (never-fires, source gaps); `--test` runs paired `.test.yaml` fixtures (Semgrep/Falco convention: rule + recorded capture + expected matches), backed by the existing golden accuracy machinery. | **S–M** |
| **In-app workbench** | AvaloniaEdit (+TextMate injection grammar for expression slots — a **new dependency**, pure-managed, no WebView/Monaco per house constraints); completion popup fed **in-process** by the shared semantic core (no LSP hop); FileSystemWatcher → auto re-run; fire-count badges; diagnostics rows regain line/col and click-to-open passes `code --goto file:line` (both currently dropped). The breakpoint condition editor already prototypes registry-driven autocomplete, and `Building/StructuredCondition.cs` already proves lossless structured↔text round-tripping in-repo. | **M–L** |
| **Trace panel** | HA-trace model: candidate-event lists with per-clause verdicts, near-miss view, path-highlighted graph, message scrubbing. Round-2 correction: the opt-in per-edge applied recorder **is on main** (`StateGraphEvaluator.cs:762, 814-822` — null and byte-identical on normal runs), so this is cheaper than first budgeted. Clause-level verdicts are recorded **only** during a targeted re-run of the selected ruleset on a cloned instrumented subgraph (never always-on — always-on verdict capture was measured as the snapshot-alloc disaster at 1.1–1.7M-evaluation cardinality and is rejected, §3.1 round-2). Cheap always-on fire *counters* (a first-wave quick win) power the badges and never-fired lint on every ordinary run. | **M** |
| **Blueprint gallery** | shipped + community rulesets as cards; `params:` render as forms; filling a form generates a ruleset file (one representation, eject-only — no bidirectional form editing guarantees). Share metadata: `min_app_version`, catalog version. | **L, demand-gated** |
| **C# LSP** | OmniSharp.Extensions wrapper around the same semantic core for VS Code in-expression completion and go-to-def/rename on ids. Deliberately last: the schema was shaped so the free tier covers ~80% of it. | **M→L, only if demanded** |

The **shared semantic core** (parser, resolver, type checker, canonical-AST hasher) is the
load-bearing library: one implementation consumed by the engine load path, the workbench, the
CLI, and (if ever) the LSP. Never implement resolution twice.

---

## 2. How this differs from today

### 2.1 Pain → answer map

Evidence for every pain is in the corpus; the worst offenders cited inline.

| # | Today (v1) | v2 |
|---|---|---|
| 1 | **No repetition construct** — `pp_kill_tick_1..5` five copy-pasted rules (`achievement-post-plant-double.yaml:53-116`); CT/T win/loss ×4 families; 10-weapon `\|\|` chains | `keep: list` capture lists (one collection-valued node, unbounded); `for_each:` expansion; named `define:` lists with `in` |
| 2 | **Slot boilerplate**, wrong-field silent — `event.KillerSlot == player.slot` in nearly every per-player rule | actor-anchored views: `count: kill` / `count: death` / `count: assist` — the view choice *is* the binding; wrong field inexpressible |
| 3 | **Evaluation-order knowledge required** — capture pattern only works because increments topo-sort before dependent reads; documented in example-file comments | the compiler emits one collection-valued node + one append edge, and read-aware topo ordering puts readers after writers — ordering is the compiler's private business |
| 4 | **Conditions live in 3 places with 3 micro-grammars** (trigger `condition:`, parent `when:`, bare parents) and different powers | one anatomy: `match:` (structure) + `where:` (event expr) + `while:` (state gate); merged into one canonical condition AST at compile time |
| 5 | **`enrich.*` magic** — 24 names discoverable only in a doc appendix; reads under the wrong event silently yield stale defaults | typed event facets, lexically scoped; wrong-event use is a schema error; completion offers them per event |
| 6 | **~10 name families, dual spellings** (`context.player.traded` vs `traded`; `$round_end` vs bare-in-`requires:`) | one namespace tree with a published availability matrix |
| 7 | **Hidden satisfaction** = AND of all bool rules — forces chain proliferation; adding a helper bool silently changes satisfaction | explicit `highlights: when:` |
| 8 | **Bimodal errors** — good at load, absent semantically; 15 distinct silent-failure classes (typo'd parent on a triggered rule silently ungates to root — `RuleChainBuilder.cs:1981-1995`; unknown `on_satisfied` target no-ops; game-scope `expression` rules build inert bool nodes) | every catalog-checkable error is loud at load with `file(line,col)`; fire-count lints close the demo-dependent remainder |
| 9 | **`columns:` vs `outputs:` double declaration** (kast re-lists ~20 rules; player-stats 53) | one `show:` block |
| 10 | **Source portability is the author's job** (`$` sigils, `requires:` ritual, MM-only footnotes) | curated views resolve per profile below the surface; unbindable stats skip with a visible coverage lint |
| 11 | **Accumulation incantation** `action: set` + `value: "rule.value + …"` (8+ copies) | `sum:` kind; `this` replaces `rule.value` |
| 12 | **Entity access is expert territory** (4 curated `player.*` reads + 1 singleton — five providers total, the scope of the provider-parity gate) | same reads under `player.*` with pre-frame semantics in hover docs; expansion = a `providers:` catalog entry — a data-file line, schema-validated at scanner prime; no C# |
| 13 | **JSON schema validates shape, not meaning** — `on:` and every expression are opaque strings | generated schema enumerates views/facets/kinds; expressions type-checked by the semantic core |

### 2.2 What is deliberately preserved

From the audit's "keep" list: the state-graph execution model and all optimizations; the strict
loader with attributed errors and did-you-mean; the two-tier overlay with contained user errors
and disable stubs; enrichment *as a concept* (renamed facets); the built-in context library; the
one-declaration-reaches-everywhere output flow; compiled zero-interpretation expressions with
safe division; the purpose-built stat kinds (tally/streak/bucket replaced all 14 C# plugins —
that trajectory continues: new expressiveness arrives as new *kinds*, not plugin escape
hatches); the golden accuracy suite as the migration gate; YAML files as the git-diffable source
of truth (the Node-RED cautionary lesson: any visual editor is a *view*, never the store).

### 2.3 Behavior changes to sign off

1. **Satisfaction semantics**: explicit `when:` replaces the all-bools conjunction. The four
   shipped files must be ported and re-baselined against the golden accuracy suite.
2. **Dedup cache keys change**: `RuleHasher` (structural dedup) moves from trimmed-string
   hashing to canonical-AST hashing (§3.1). Hashes are per-run, so nothing persists, but the
   accuracy suite re-baselines once and a hash-equivalence gate battery becomes a shipping gate.
3. **Concrete-event degradation**: v1 loudly errors on unbound `$logical` but silently
   zero-reads absent concrete events; v2 uniformly skips-with-diagnostic.
4. **`requires:` disappears** as an author concept (subsumed by view availability metadata).
5. **Clutch and alive-count numbers change on demos containing disconnects** — the
   `Connected`-flag fix corrects the ghost-resurrection defect (`ResetRoundState` marks every
   registered player alive each round), so clutch detection and the new aggregates change on
   affected demos. Per the goldens policy (§3.2) this defect-driven re-baseline lands as a
   matter of course, in its own commit.

### 2.4 External patterns adopted (provenance)

- **Falco**: macros + lists with append/override; capture-file replay as the testing model.
- **FEEL/ZEN**: unary tests with implicit subject; null-not-exception evaluation.
- **GitHub Actions**: typed enumerable context namespaces + per-site availability table;
  actionlint as the model for a fast standalone checker; schema-driven VS Code completion.
- **Home Assistant**: trigger/condition/action chassis; blueprints with typed inputs and
  take-control ejection; automation traces as the debugging gold standard.
- **Sigma/EQL**: named selections + tiny combining condition; `sequence by` as the future
  temporal construct (deferred, §3.3).
- **Semgrep/OPA**: paired rule+fixture tests run by one CLI; playground with inline assertions.
- **Kubernetes CRD / ESLint / Taskfile**: the schema as a generated artifact of the registry.
- **json-rules-engine / GoRules**: structured condition leaves as the default tier; the
  simulator inside the editor.
- **Microsoft RulesEngine**: named condition fragments (GlobalParams) → our `define:`.
- **Node-RED / Grafana (negative)**: never make the visual editor the source of truth; never
  ship a one-way as-code round trip. **Splunk/Drools/Easy Rules (negative)**: a raw
  host-language query/expression surface is the authoring cliff to avoid.

---

## 3. How we implement it

### 3.1 Compilation: v2 lowers onto the unchanged engine

Pipeline: **parse → resolve** (inline defines/params, expand `for_each`, desugar `match:` +
`where:` + `while:` into one condition AST per edge, expand views per profile) → **canonical
AST** (normalized names, sorted commutative conjuncts, folded literals) → **check** (types,
slot legality, purity — round 1's decomposability check is deleted by the multi-source
conditional edges) → **plan** onto existing primitives → **hash** for dedup.

The optimization inventory (from the engine-constraints audit) and how each survives:

| Optimization | Contract | v2 status |
|---|---|---|
| typed dispatch index (`StateGraphEvaluator` edge buckets by message type) | every edge anchored to one statically-known event type | preserved: patterns always name one view; multi-bound views expand to multiple edges with first-wins guards, exactly like `$logical` today |
| live-dispatch-key filtering | edge eligibility = `Source.IsActive` only | preserved: `while:` compiles to parent-as-edge-source; the planner also hoists a single-bool conjunct of a merged condition to the edge source *after* canonicalization (so hoisting can never fragment dedup hashes) |
| declared-write topo ordering | every edge statically declares its complete write set | preserved, and **extended by the engine track**: edges additionally declare condition *read* sets (statically enumerable from v2's typed ASTs) and `TopologicalSortEdges` orders readers after writers — see the round-2 additions below |
| logic-node condition caching | conjunction inputs must be pure value predicates over *declared* input nodes | **resolved by the engine track**: conditional edges generalize to N declared sources (satisfied = all sources active ∧ predicate), so `a + b > 5` becomes legal and the decomposability rejection is deleted. Recompute-minimality holds by construction — dirty-marking and bucketing union all sources' writers, and `ConjunctionNode.Recompute` is already whole-node cached, so an N-ary predicate costs what N unary inputs cost |
| rising-edge declared writes | rising-edge-written counters not readable mid-message | **resolved by the engine track**: rising-edge action writes route through the dirty pipeline (with a per-message once-fired latch — re-checking showed a conjunction can flip true→false→true within one message, so the latch is mandatory, not optional), and `_risingEdgeActions` becomes multi-action per trigger (fixing today's silent last-wins). The checker restriction is deleted; `<highlight>.count` reads like any stat |
| snapshot dirty-tracking + row sharing | complete mutation taxonomy; nodes snapshot as scalars | unchanged — verified compatible with collection-valued nodes: `NodeSnapshot` never stores the raw value (only `IsActive` + display string + `float?` numeric, stringified at capture), so a list snapshots as its rendered value + `Count` with zero aliasing hazard; the append-by-replacement contract (below) keeps dirty tracking sound; trace recording is targeted-re-run-only so it can't erode the sharing win |
| structural dedup (`RuleHasher`) | hash-equal ⇒ behaviorally interchangeable (reference-identity sharing) | **improved**: preimage becomes (kind, value-type, per, resolved concrete-event set, canonical AST with defines inlined and match-bindings normalized, gate id, keep-spec; id-salt for input-less stats retained). `a>1` vs `a > 1` now merge; a define shared across rulesets dedups by construction; `List<T>` is just another value type in the preimage, so identical capture lists share one node like any scalar. **Round-2 hard requirement, caught on re-checking:** the preimage must embed each referenced stat's own **resolved structural hash** (recursive identity), not its name — `RuleHasher` hashes normalized *text* today, which is sound only because identifier resolution is effectively global; once multi-source predicates reference arbitrary stats under v2's scoped namespaces, identical text can resolve to different nodes and text-hashing silently **false-shares** them (wrong results, not a crash). **Shipping gate:** a property-test battery (hash-equal ⇒ identical evaluation on golden demos) — reference-identity sharing makes a normalization bug a *corruption* bug, and this project's history (parallel-digest gate battery) says test it exactly that way |
| parallel digest production | rule-visible entity data flows only through per-frame digests; cloneable frozen providers | untouched: `player.health` etc. still compile to fixed-slot pre-frame `GetPreFrameValue` reads |
| lazy scanner activation | entity references statically discoverable | **upgraded**: references enumerated from typed ASTs replace the token-boundary substring scan |
| payload pre-extraction | one payload type per edge | unchanged |

Construct → graph mapping (one new node kind, the collection-valued node; everything else
existing primitives): `flag` → `GenericBoolNode` (+ round-scoped variant); `count`/`sum`/
`capture keep: first|last` → scalar value nodes with game-event edges (`sum` = generated
self-add SetValue); `capture keep: list` → **one collection-valued node**
(`ValueNode<IReadOnlyList<T>>` — `ValueNode<T>` is already fully generic) + one append edge.
The append is **replace, never in-place mutation**: the edge calls `SetValue(old + item)`
(copy-on-append of a small array, or an immutable list), because the declared-write tracking,
the dirty cascade, and the snapshot re-read + display cache all key off `SetValue` — an
in-place `Add()` would change the value invisibly and all three go silently stale. The engine
generates this edge, so the contract is enforced by construction, and it buys immutability for
free: a round-end export samples a reference to *that round's final list*, which later rounds
can never retroactively mutate. `.count` reads `Count` (no hidden counter node exists at all —
the old file's count-gated `pp_kill_tick_N` machinery dies at the engine level, not just the
surface); `[n]` is a bounds-checked element read; the **default and reset value is the shared
immutable empty list, never null** — a capture list is always defined ("captures so far"),
`count == 0` carries the emptiness signal, generated predicates need no null branches, and
resets are allocation-free singleton assignments. List-typed stats are legal in expressions
only via `.count`/`[n]`/`.set` (no arithmetic on lists — checker-enforced). Remaining
mappings: `compute` → `ComputedStatNode` (still explicitly round-end); highlight → one
conjunction + rising-edge action appending the timeline event, with a **typed ruleset identity
replacing the `_chain_{id}` string convention** (charter-listed incidental); `for: each_player`
→ the same `PerPlayerNodeTemplate` multiplication, with each template's expressions compiled
**once** to environment-taking delegates (`Func<PlayerEnv, TEvent, …>`; `PlayerEnv` = slot,
materialization-frozen team, name, and a deterministic node-register array — **settled
2026-07-10, replacing slot-constant baking**; per-player dedup at template build is unchanged,
and the view's implicit actor binding lowers to the same slot-equality check, now against
`env.Slot`). Materializing a player allocates an environment and binds curried wrappers
instead of re-running ~180 `Expression.Compile` calls per player (compiled delegates counted
after profile expansion — conditions + value selectors + parent gates; ~34–314 µs each,
measured; ~60–120 ms per run today, hidden in the Eval bucket) — killing the
expressions×players compile scaling
before the blueprint gallery makes it a 0.5–1 s regression, and moving expression errors from
eval-time-per-player to load-time-once. Measured eval-side cost: +1.1 ns per condition
evaluation (≈ +0.5–2 ms/run; no additional conditions are checked). The corruption-class risk
(a wrong register index silently reads another stat's value) is gated by an
**env-vs-constant equivalence battery** on the golden demos — keep both lowerings compilable
behind a flag until it passes — plus a fixture pinning frozen-`player.team` semantics.

**Collections stay collections until serialization.** Internally — engine, snapshots,
expressions, UI — a capture list is one collection value. Only the *serialization layer* of
each output format flattens it, and it can measure the exact width post-eval: the CSV/table
formatters emit `<Label>Count` + `<Label>1..<Label>N` (N = max observed across the table's
rows, deterministic ordering so golden-fixture comparisons stay stable); JSON emits a real
`count` + array, which is strictly better for downstream consumers like the clip tool. No
width hints exist anywhere in the schema — width is a measured serialization detail, not an
authoring concern.

**One engine-level fix the review caught:** the `.set` pseudo-member must **not** compile to
`IsActive` on value nodes — `RoundScopedValueNode<T>` sets its value in the constructor and on
every reset, so `IsActive` is permanently true and a `.set` gate would silently be always-on
(the exact class of bug v2 exists to kill). `.set` on a **scalar** capture compiles to a hidden
round-scoped **bool** twin activated by the same trigger; on a **list** capture it is simply
`count > 0` — a pure value predicate, no twin needed. For the flagship post-plant case, add
the sticky `round.bomb.was_planted` built-in (a two-line addition to `BuiltinContexts`, which
the v1 example file's comments already wished existed).

### Round-2 additions (2026-07-10, owner lifted the no-engine-changes constraint)

Nine deep code investigations, re-checked against main as it stood that day. Everything in
this section is a **settled clear win** unless marked as an open decision.

**The "declared reads" engine track** (read-aware ordering + multi-source edges + rising-edge
routing as one coupled unit, ~M; sequencing them after a v2.0 hash freeze would reopen
dedup-corruption risk, so they co-release with the v2 core):

- **Read-aware topological ordering.** `StateEdge` gains `DeclaredReads` (emitted from the
  typed AST — the read set is exact and already computed for the lazy-scanner reference
  enumeration);
  `TopologicalSortEdges` orders readers after writers, generalizing the existing
  Deactivate-after-readers rule. Enrichment/facet ordering becomes a declared constraint
  instead of insertion-order luck (enrichment edges are inserted first and Kahn's queue keeps
  insertion order — a builder refactor or a per-player re-sort can legally reorder an
  unconstrained read past its writer today). Same-event read *cycles* are a build error naming
  both stats, with an `after: <stat>` fix-it as the explicit tie-break (fix-it-only discovery;
  not in the docs' main path). Honest framing, corrected on re-checking: the evaluator is
  already single-threaded and run-deterministic — what this buys is **invariance under config
  reordering and engine refactors** plus the enabling substrate for multi-source edges,
  rising-edge routing, and cross-ruleset reads, not a fix for an observed bug. Two required implementation details: **Source-encoding stays** where
  it exists (the count-gate pattern already routes its read through the edge's `Source`, which
  keeps the per-edge inactive-skip and dispatch-filter precision; `DeclaredReads` is additive,
  duplicate constraints harmless), and `AdditionalWrittenNodes` folds into
  `BuildLogicNodeIndex` *and* the per-player `RegisterConjunction`/`RegisterDisjunction` paths
  (a latent indexing gap found during review). Effort S; zero per-message cost.
- **Multi-source conditional edges.** `IConditionalEdge` generalizes to N declared sources
  with a compiled predicate; satisfied = all sources active ∧ predicate (the strict
  generalization — a 1-element edge is behavior-identical to today's). Deletes the
  decomposability wall (the condition-caching row above). Riders: the **resolved-identity hash
  preimage** (the structural-dedup row above) is non-negotiable; multi-source gates would
  quietly enable multi-parent triggered rules (currently a deliberate error) — **open decision,
  resolve before the merge; default: keep the error until a real use case arrives**; the trace panel shows operand values (an
  N-ary predicate is one verdict); a lint proposes
  hoisting a repeated multi-source subexpression into a named stat (helper stats were free
  documentation and dedup units). Effort S–M; ~zero runtime delta.
- **Rising-edge dirty routing + multi-action.** Rising-edge action writes route through
  the dirty pipeline and `_risingEdgeActions` becomes multi-action (the rising-edge row above).
  This is simultaneously the entire engine groundwork for a future `on: highlight(...)` trigger
  (the composition wave's highlight-as-trigger item) — one work item, not two. The per-message
  once-fired latch is mandatory (the duplicate-fire hazard above is real). Effort S.
- **Live `compute:` — disposition (2026-07-10): deferred, evidence-gated.**
  Rising-edge routing ships now; live compute does not gate the track. On re-checking, the
  proposed drain placement cannot deliver its own motivating case (it enqueues into a queue the
  logic pass clears, and rising-edge counters are written *after* the proposed drain point); a
  correct version needs a logic⇄computed fixpoint interleave, a duplicate-fire guard, and a
  **hard** frequency cap (today's round-end-only recompute is a structural cost bound; a lint
  is not a substitute for user-authored rules the Library analyzes unattended). After the
  engine track lands, run live compute as a golden-diff dry-run that reports exactly which
  ticks/values shift across the corpus; its fate is decided on that evidence.

**Entity vocabulary as catalog data.** Two generic classes
(~300 LOC) over existing APIs — the seen-gated dotted-path `EntityState` indexer (lane-mapped
and fallback fields read identically; Schema Lens finality is immaterial to correctness),
`GetFieldMeta` introspection, `PawnLookup` handle resolution — compile catalog `providers:`
entries onto the existing provider contracts. v1 scope: scalar types + single-hop `via:` handle
follow, per-player + curated singletons (data-defined singleton *marker-type minting* is the
one open design point — dynamic types vs a keyed dispatch extension — deferred to the core
wave's design). **Mandatory riders, all from re-checking:** reference-gated per-player activation
(today per-player providers activate unconditionally; without gating, catalog width multiplies
the *resident* precomputed-digest memory — ~35 MB boxing per int provider is held live, ~1 GB+
at 30 providers); release each precomputed digest after `Consume`; the prime-time validation
must check declared-type vs `RuntimeField.TypeName` compatibility with a **loud**
unexpected-type arm (the current coercion template's `_ => null` would make CS2 type drift
silent); the post-SendTables validation point is *new* machinery covering both the parallel and
sequential decode paths; and `GetPreFrameValue`'s linear provider `IndexOf` becomes a cached
index. A worker-clone hook (`CloneForWorker()`) replaces the `Activator.CreateInstance`
parameterless-ctor assumption — the code comment at `EntityChangeScanner.cs:234` already
anticipates it. Migration gate: re-express the five shipped providers as catalog entries and
prove byte-identical digests on the golden suite.

**Event-subject entity reads.** A third resolution branch at the two
existing subject-read grammar sites lowers role-handle members (`victim.health` under `kill`)
to `EntityChangeScanner.GetPreFrameValue(provider, subjectSlot)` — the exact read
`HurtTeamEnrichmentEdge` performs in C# today. Riders: compile-time provider-registration
assert and `GetPreFrameValue` throwing on unregistered providers (otherwise provider
reference-gating could silently zero every subject read); document the dead-subject divergence
between the live path (stale-retaining scanner snapshot) and the breakpoint host (frozen
`EntityValueCache`) — a user debugging a rule can otherwise see different values than the rule
computed. Effort S.

**`bucket:` restriction lifts.** `bucket:` (v1 `keyed_counter`) is the second collection
flavor and sheds three v1 restrictions. It becomes legal in `for: match` rulesets (the
per-player-only rule existed solely because `KeyedStatsProjector` enumerates only
materialized-player nodes). It accepts `per: round` via **archive-on-reset** — the node stays
snapshot-excluded (the snapshot contract untouched) and pushes a frozen bucket reference per
round reset, the same round-end reference-sampling contract capture lists use, with the final
live round read at end-of-eval and match-end/warmup fixture variants. And keys become any
string-typed expression including typed keys (canonicalized for bucketing, typed in the output
dimension) and — riding the v2 grammar's list/map lookup over `define:` lists — derived keys
like `category_of(event.weapon)`. The v2 schema shapes `key:` as a **list from day one** so
composite keys (weapon × hitgroup) ship later without breaking; the multi-dimension projector
and per-bucket `min`/`max`/`set` reducers are demand-gated (v2.1). The dedup preimage and its
gate battery grow to cover key parts, reducer, and `per:`; the risk-11 high-frequency lint
extends to bucket-key cardinality.

**Catalog-driven materialization.** The Catalog codegen also emits a per-event
slot-accessor table (event type → role slot getters, range-guarded 0..63); the evaluator's lazy
materialization consults it instead of the hand-written 4-event switch, and the
synthesized-entity-event loop performs the same check. This closes a verified silent data
loss: demos that start mid-match (HLTV segments, backup restores) exclude the entire roster
up-front (`PlayerInfo.Team` stays 0 without replayed `player_team` events), and today a
player's `weapon_fire`/flash/bomb events before their first death/hurt are silently dropped.
Two latent bugs found during review land in the **first wave** (~20 lines): the lazy path lacks the
0..63 sentinel guard (`VictimSlot`/`PlayerSlot` yielded unguarded), and synthesized entity
events never materialize at all. Scope honesty: this closes event *loss*, not side attribution
— mid-match-start demos still resolve `player.team` to 0, which belongs to risk 5's identity
workstream (roster/team seed from first-full-packet controller entities; interacts with the
protected `DemoParser.cs` post-pass).

**Durations and instants.** Bare ints remain ticks; `10s`/`0.5s`/`500ms`
suffix literals are legal in YAML duration slots, expression arithmetic, and unary tests;
`"m:ss[.frac]"` timespan scalars in YAML slots only. The checker gains catalog-backed `instant`
(`event.tick`, `match.tick`, captures thereof) and `duration` types with instant−instant =
duration algebra and one-way int→time coercion; all duration literals **fold to int tick
constants during canonicalization, before `RuleHasher`** (so `5s` ≡ `320` at 64/s and they
dedup together), using `ParsedDemo.TickRate`, or the parser's own 64 default with a stated
assumption in the demo-less `rules check` path (typing itself is rate-independent). Output
stays raw ticks by default; `show:` entries take opt-in `as: ticks|seconds|time`, applied only
in the serialization layer (JSON keeps the raw tick alongside any formatted string). Pin
`MidpointRounding.AwayFromZero` + a sub-tick/boundary lint in the golden battery; expose a
small `ParsedDemo` flag for the silent 1/64 ServerInfo fallback so mis-rated demos lint instead
of mis-folding — note the property lives on non-protected `ParsedDemo.cs` but is *populated*
inside the protected `DemoParser.cs` parse loop where the default lives (a one-line change
that needs the standing owner approval for that file; plan it as such). Zero runtime cost —
the evaluator only ever sees today's int constants.

**Composition.** (a) **Cross-ruleset reads + `exports:`** (§1.5) make v1's already
load-bearing cross-chain reads intentional: qualified `ruleset.stat` form only, an exports
graph checked at load, and overlay replacement/disable re-validated with attributed errors
replacing today's uncontained raw `Unknown identifier` build failure — user-tier build breakage
becomes skip-with-diagnostic. Mid-message cross-reads rely on the read-aware ordering (the
engine track, which lands on main *before* the core compiler — §3.2), so no restriction window
exists in the planned sequence; if the engine track ever slips a release, checker-restrict them
to `compute:` and `when:` sites in the interim. (b) **`extends:`** delta files (§1.5). (c)
**Highlight-as-trigger**: the engine groundwork is exactly the rising-edge multi-action + dirty routing;
the `on: highlight(<ruleset>.<id>)` author syntax is deferred to v2.1 behind demand — most of
its value (`<highlight>.count` reads) is already covered by (a); revisit when a request arrives
that needs capture-at-the-moment. Build-time cycle detection on the subscription graph and
same-scope-only subscription are specified now so the seam is stable. Sub-key condition
patching in `extends:` is **rejected** (merge ambiguity).

**Net-message seams.** The v2 trigger grammar closes over three families: curated views,
`raw.<game_event>`, and `net.<MessageName>` — the latter resolving against the net-message
registry (today two messages), with `match:`/`where:` over raw payload fields and no actor
convention. **The first wave wires the silently-ignored net-message `condition:`** — `OnNetMessage<T>`
already accepts a predicate and the compiler is payload-generic; `CreateNetMessageEdge` simply
never passed it (~5 lines; corpus contains no affected file). The catalog wave emits the
`netMessages` family with descriptor-derived fields, ProtoIndex source links, and a measured
`frequencyClass` enum (`per_match…per_tick`) on **every** trigger entry — the high-frequency
lints key on this field, never on hardcoded names (the cautionary number: the removed
`CNETMsg_Tick` plugin cost 123K+ edge evaluations per demo). Registering additional messages
later is a one-line registry tuple plus catalog regen; the evaluator already dispatches
arbitrary payload types through the typed dispatch index and live-dispatch-key filtering.

**Trace tiers, quantified.** Always-on **per-edge fire counters** — one int increment in
the applied branch (adjacent to the existing `edgesFired++`) plus the rising-edge site: zero
cost on the skip path, ~100–150K increments (<1 ms) per run, zero allocation; counters reset
per evaluation and power the first-wave badges/never-fired lint on every ordinary run, including
first demo load. Always-on **verdict rings are rejected with numbers**: clause-level verdicts
with captured values cost per *evaluation* (1.1–1.7M/demo) what snapshots cost per message —
the recorded 92%-of-eval/all-GC failure mode — and instrumented conditions would break condition
caching and dedup reference-sharing. Clause-level "why didn't it fire" data comes exclusively
from the workbench's targeted re-run on a cloned, instrumented subgraph of the selected ruleset
(instrumented nodes must never enter the deduped shared graph).

### 3.2 Phasing and work breakdown

Efforts: S = days, M = 1–3 weeks, L = 1–2 months. Each wave ships standalone value; the honest
total through the vocabulary wave is the original estimate plus ~8–11 weeks serialized for the
round-2 additions.

**Repo conventions binding every wave:** analysis-layer tests live in
`src/Analysis/DemoViewer.NET.Analysis.Tests`; heavy demo-replay test classes are
`[NotInParallel]` (the suite is RAM-bound); conditional skips throw `SkipTestException` (an
early `return` counts as *passed*); demo fixtures resolve via the existing `DEMO_PATH`/TestData
convention; the protected parser files (`DemoParser.cs`, `DemoFrame.cs`, `LEB128Utils.cs`,
`BitBuffer.cs`) require explicit owner approval before any edit — the only planned touch is
the durations work's one-line ServerInfo-fallback flag population (§3.1).

**First wave — silent-failure triage on v1, no schema change (S–M).** Four independent work
items, all on main, none touching protected files:

- **Always-on fire counters**: an `int` fire counter per edge in
  `StateGraphEvaluator.cs`, incremented in the applied branch (adjacent to the existing
  `edgesFired++` at `:808`) and at the rising-edge action site; counters reset at evaluation
  start. Test: a known-corpus run asserts nonzero counts on firing rules. Budget: <1 ms/run,
  zero allocation, bench-gated.
- **Badges + never-fired lint**: surface those counters as fire-count badges and a "never
  fired" lint row in the rule-diagnostics panel (`AnalysisViewModel` + its view). Test: a
  deliberately dead rule produces the lint (headless TUnit + Skia per house convention).
- **Line/col restoration**: thread `file(line,col)` from `YamlConfigLoader.cs` →
  `RuleConfigLoadResult` → `AnalysisViewModel` diagnostics rows (currently dropped at the
  mapping into `RuleDiagnostic`), and pass `code --goto file:line` on click (`OpenExternal`
  already supports it). Test: a malformed fixture asserts the position surfaces.
- **Latent-bug fixes**: (a) pass the compiled `trigger.Condition` in
  `RuleChainBuilder.CreateNetMessageEdge` (the net-message seam — ~5 lines;
  `OnNetMessage<T>` already accepts the predicate); test: a net-message rule with a false
  condition no longer fires. (b) hoist the 0..63 slot guard into
  `StateGraphEvaluator.MaterializeNewPlayers` and call materialization from the
  synthesized-event loop (materialization — ~20 lines); tests: a synthesized-event fixture
  materializes its player; a sentinel slot materializes nothing.

Exit: bench suite unchanged (± noise); all new tests green.

**Second wave — Catalog, schema, CLI (M+).**

- **Catalog generator**: new `tools/DemoViewer.NET.RulesCatalog/` console generator (the
  Codegen-project pattern: outputs are committed, a CI drift test asserts regen == committed).
  Inputs: `EventRegistry`, `BuiltinContexts` enrichment declarations, provider registries,
  demo-source profiles, `GameEventSemantics`, plus the curated view/facet/`providers:` data
  files as they land. Emits committed `rules/catalog.json`, embedded as a resource in
  `DemoViewer.NET.Analysis` for runtime use (data browser, load-time validation).
- **v1 schema retrofit**: the same generator injects event/enrich-name enums into
  `rules/analysis-rules.schema.json` and fixes the audit's schema/loader drift (e.g.
  `requires:` is missing — files following the docs currently validate as *invalid*). The full
  v2 schema (`rules/dv-rules.schema.json`, where the §1.4 modeline resolves) is a core-wave
  deliverable of the same tool.
- **`rules check` CLI**: a verb on AnalysisBench
  (`dotnet run --project tools/AnalysisBench -- rules check <dir>`). Scope for this wave: v1
  files via the existing `YamlConfigLoader` plus catalog-backed enum checks; `--demo x.dem` adds
  coverage lints (never-fires via the fire counters, source gaps); `--test` runs paired
  `.test.yaml` fixtures. The core wave swaps the semantic core in behind the same verb.
- **Generic entity providers**: `GenericPerPlayerFieldProvider` /
  `GenericSingletonFieldProvider` in `Plugins/` reading through the seen-gated `EntityState`
  indexer; a `CloneForWorker()` hook replacing the `Activator.CreateInstance`
  parameterless-ctor assumption in `EntityChangeScanner`; the `providers:` catalog family;
  two-tier validation (static name check at catalog load; a **new** post-SendTables prime hook
  — covering both the parallel and sequential decode paths — checking `GetFieldMeta` existence
  *and* declared-type vs `RuntimeField.TypeName` with a loud unexpected-type arm);
  reference-gated per-player activation; precomputed-digest release-after-`Consume`; a cached
  provider index in `GetPreFrameValue`. Gate: the five shipped providers (4 per-player + 1
  singleton) re-expressed as catalog entries with byte-identical digests
  (`ProviderDigestParityTests`).
- **`netMessages` catalog family**: descriptor-derived fields, ProtoIndex source
  links, and the measured `frequencyClass` enum on **every** trigger entry — measured from the
  bench suite by the generator, never hand-tagged.

Exit: VS Code completion lights up on v1 files via the retrofitted schema; `rules check` green
on the shipped corpus; the digest-parity battery green.

**Third wave — the Rulesets v2 core (L).**

- **Semantic core project** — the first work item; everything else in this wave builds on
  it. New `src/Analysis/DemoViewer.NET.Analysis.Rules/` (no UI dependencies): expression
  lexer/parser against the published EBNF, resolver, typed checker over the Catalog's per-slot
  scope environments, canonical AST + normalizer, and the new `RuleHasher` preimage
  (resolved-identity — the §3.1 structural-dedup row). Referenced by the v2 load path
  (`DemoViewer.NET.Analysis.Yaml`), AnalysisBench (`rules check`), and — later — the
  workbench. One implementation, three frontends.
- **v2 loader + compiler**: top-level-key dispatch (`ruleset:` = v2, `chains:` = legacy);
  resolve (defines/params/`for_each`/views-per-profile) → canonicalize (fold duration
  literals *before* hashing) → check (types, slot legality, purity) → plan onto the graph:
  `match:` desugar, `while:` → edge source, hoist-after-canonicalization, environment
  lowering (`PlayerEnv` + deterministic register allocation; the constant lowering stays
  compilable behind a flag until the equivalence battery passes), the collection-valued
  capture node + append edge + per-format serialization flatteners, scalar-capture `.set`
  bool twins, the typed ruleset identity replacing `_chain_{id}`, the sticky
  `round.bomb.was_planted` built-in, subject-read lowering (event-subject entity reads),
  and the `exports:` graph + attributed overlay revalidation.
- **View/facet curation**: the curated data file declaring the actor-anchored views
  (`kill`/`death`/`assist`, `damage_dealt`/`damage_taken`, …) with baked filters and
  facet↔enrichment mappings; **every view pinned by a fixture test against demofile-net ground
  truth** (the oracle-only rule stands: demofile-net is never a project dependency).
- **Shape freezes.** A freeze is discharged when the shape appears in all three artifacts,
  all landing in this wave: (1) `docs/rules-v2/rules-v2-spec.md` — the published EBNF, the namespace
  tree, and the `RuleHasher` preimage field list; (2) the shipped `dv-rules.schema.json`,
  which must already contain the reserved shapes even where the loader rejects them as
  "reserved, not yet implemented" — multi-source condition lists, bucket `key:`-as-list
  + reducer + `per:` axes, duration literal forms, the team-aggregate namespace
  (`round.team.*` / `round.enemies.*` / `round.alive.*`), the role-handle members
  (`victim.*` / `killer.*` / `assister.*`), the `net.*` trigger family, and the
  `catalog_version` / `min_app_version` fields; (3) a preimage-snapshot golden test
  (serialize the preimages for the shipped corpus; any diff is a deliberate, commit-audited
  re-baseline). Exit criterion: every frozen shape appears in all three artifacts.
- **Port + gates**: port the four shipped files; golden accuracy suite green;
  `HashEquivalenceGateTests` + `EnvVsConstantEquivalenceTests` + duration spelling-equivalence
  cases; the output-name-stability lint in `rules check`; the `2MUCH` display threshold picked
  from a one-off bench-suite list-length distribution run and recorded in the spec (UI
  rendering itself belongs to the workbench).
- **Tutorial**: the hands-on replacement for the retired v1 authoring guide (reference docs
  are generated from the Catalog; the tutorial is written by hand). Gates wave exit.
- **Pre-freeze de-risk (do first):** the paper-prototype — owner recruits 2–3 real
  non-programmers; the artifact is ten hand-written v2 YAML answers to Leetify/Scope.gg-style
  requests (ninja defuse, 1vX clutch record, save rounds, eco frags, trade windows in
  seconds); exit = every request is expressible without new engine work, or explicitly
  deferred with a named gap.

Legacy loader kept behind the top-level-key dispatch for one release window, then deleted.

**The engine track (M, ~2–3 weeks).** Read-aware topo ordering + multi-source
conditional edges + rising-edge dirty routing/multi-action as one coupled unit — §3.1
round-2 additions. **Merge order: the engine track lands on main *first*, engine-only** — the
v1 builder emits empty `DeclaredReads` (behavior-identical), 1-source conditional edges remain
the strict special case, and the batteries drive hand-built graphs plus
permuted-insertion-order v1 corpus builds (`ReadOrderingDeterminismTests`). The core compiler
then targets the already-merged contracts. "Co-release" means neither ships in a *release*
without the other, not lockstep development. Live `compute:` stays decoupled per the
disposition settled in §3.1.

**Vocabulary & composition wave (M–L; may trail v2.0 by a release *because* every
shape it needs froze with the core).**

- **First item: the live-`compute:` golden-diff dry-run** — run live-compute semantics
  across the corpus, produce the memo of exactly which ticks/values shift, and decide on the
  evidence.
- **Fixture-sourcing spike (S, do early):** scan the existing corpus (`demos/`,
  `demos/benchmarks/`) with a small AnalysisBench verb for the needed phenomena — disconnects,
  halftime, and double-kill-in-one-frame are very likely already present; synthesize the
  mid-match-start fixture by truncating an existing demo at a `DEM_FullPacket` boundary (small
  `tools/` utility). Store selections under `demos/fixtures/` with a manifest stating which
  phenomenon each pins; record new demos only as a last resort.
- **Team aggregates**, per the decisions settled in §3.3 risk 1: the single-writer
  maintenance edge (fold into the `MarkDead` edge; recompute from `PlayerContextIndex`), the
  `Connected` flag (defect fix — own commit + re-baseline per the goldens policy), freeze-end
  relative-economy writes; fixtures from the spike above.
- **Bucket lifts**: game scope, per-round archive-on-reset, typed keys;
  match-end/warmup fixture variants.
- **The catalog-driven materialization table** + the mid-match-start fixture asserting that a
  player absent from the up-front roster is materialized by their first role-bearing event of
  any type.
- **`extends:`** delta files with provenance and blast-radius lints.
- **v1→v2 mechanical translator** (best-effort, non-gating).

**Workbench + trace (M–L).** All in `src/App/DemoViewer.NET`; AvaloniaEdit enters
`Directory.Packages.props` (a new dependency, pure-managed). In-process completion from the
semantic core (no LSP hop); FileSystemWatcher → auto re-run; the data browser with live demo
values and drag-to-insert; catalog-version stamped into rulesets on workbench save; the
`2MUCH` display rendering. Trace panel: the per-edge *applied-fire* recorder is already on
main (`StateGraphEvaluator.cs:762, 814-822`) — the fresh work is clause-level verdict capture
on a **cloned, instrumented subgraph** of the selected ruleset (instrumented nodes never enter
the deduped shared graph). Treat the unmerged `feature/graph-breakpoints` branch as reference
reading only (stale base — do not merge). UI verified via headless TUnit + Skia capture in
`src/App/DemoViewer.NET.App.Tests`. Platform scoping: file watcher, `code --goto`, and the
workbench are desktop-only; WASM gets the read-only gallery + diagnostics.

**Blueprint gallery + sharing (L, demand-gated).**
Cards + forms rendered from `params:` (a form is just a params renderer; filling it generates a
ruleset file — eject-only, no bidirectional form editing); share metadata (`min_app_version`,
catalog version — both frozen as schema fields in the core wave's freeze); batch-test a ruleset
across the demo Library ("this demo contains no `bomb_defused` with enemies alive" —
distinguishing *phenomenon absent* from *rule broken*).

**Cut / deferred:** the `sequence:` temporal kind (EQL-style ordered steps with `within:` /
`until:` — design exists, ship on demand as v2.1; it depends on the read-aware ordering the
engine track provides); the C# LSP (only if external-editor authors materialize); Monaco/WebView
anything. **Round-2 rejections (investigated, dropped with evidence):** always-on verdict
rings (recreate the measured 92%-of-eval snapshot-alloc failure mode at evaluation
cardinality and break condition caching and dedup sharing); the v1 retrofit of
environment-based expression lowering (M effort for ~90 ms nobody feels, on code the v2 core
deletes); sub-key condition patching in `extends:` (merge ambiguity); per-bucket
`min`/`max`/`set` reducers and the composite-key projector — implementation demand-gated,
schema shapes frozen with the core.

**Gate-battery extensions (round-2, all required; all live in
`DemoViewer.NET.Analysis.Tests` as `[NotInParallel]` classes resolving demos via
`DEMO_PATH`/TestData and skipping via `SkipTestException`):** `ReadOrderingDeterminismTests` —
permuted-insertion-order builds produce identical outputs; `HashEquivalenceGateTests` —
the structural-dedup preimage battery, grown to cover multi-source condition lists, bucket
key/reducer/`per:` axes, and duration spelling equivalence (`5s` ≡ `320`);
`ProviderDigestParityTests` — byte-identical digests vs the five hand-written providers;
`EnvVsConstantEquivalenceTests` — the env-lowering corruption-class gate, both lowerings
compilable behind a flag until it passes; golden re-baselines per the goldens policy below —
defect fixes land as a matter of course (the disconnect hardening), deliberate semantic changes
remain evidence-gated decisions (live compute if adopted, budgeted at its measured +1–2 ms
estimate); perf gates: the fire-counter budget (<1 ms), the activated-provider count lint,
the bucket-cardinality lint, and the frequency-class lint keyed on catalog data; new
fixture demos: mid-match-start, halftime / mid-round-disconnect /
double-kill-in-one-frame, match-end variants for bucket archives
(archive-on-reset), and a frozen-`player.team` semantics pin.

**Goldens policy (owner, 2026-07-10):** the golden fixtures are the best-effort statement of
correctness, not a compatibility contract. When a defect is found that made goldens wrong —
the disconnect ghost-resurrection bug behind today's clutch numbers is the standing example —
the fix and its re-baseline **always land**; sign-off exists to audit the diff, not to decide
whether correctness ships. Deliberate *semantic* changes (live-compute-class) remain decisions, made on
evidence.

### 3.3 Risks and open questions

1. **The vocabulary ceiling — round-2 status: designed and settled; the three correctness
   decisions are recorded below.**
   The architecture settled as a split: alive/count aggregates are **event-driven** built-in
   enrichment nodes (a per-frame digest cannot sequence two deaths in one frame — clutch-start
   detection is per-event by nature), while team economy sums are **digest-sampled** (no driving
   event exists; the walk is already parallel). Starter set: team/enemy alive, team/enemy player
   counts, team/enemy equipment sums, and the existing clutch enrichment exposed as typed
   facets. Two pillars failed re-checking as originally specced; all three items are now
   **decided (owner ratification 2026-07-10)**:
   (a) **single-writer rule adopted** — alive-count maintenance folds into the edge that
   already performs `MarkDead`; aggregates are recomputed *from* `PlayerContextIndex`, never a
   second incremental store; index mutation is confined to that single writer (extending
   declared read/write sets to non-node state stays a future option, not a dependency);
   (b) **`Connected` flag settled** — excluded from `ResetRoundState`, set on connect/spawn,
   cleared on disconnect. This is a defect fix: `ResetRoundState` unconditionally resurrects
   every registered player each round, so **today's shipped clutch numbers are wrong in any
   demo containing a disconnect** (ghosts count alive in every subsequent round). Per the
   goldens policy (§3.2) the fix and its re-baseline land as a matter of course, isolated in
   their own commit so the diff is auditable;
   (c) **relative economy = freeze-end event-driven writes** — digest-side absolute team sums
   stay as designed; per-player relative nodes (`round.team.equipment`,
   `round.enemies.equipment`) are written once per player at `round_freeze_end` by a
   maintenance edge reading the absolute sums plus the player's team, so every downstream read
   is a pure single-node predicate (legal under condition caching) and the write cadence
   matches the corpus's freeze-end economy-sampling convention. Implementation lands in the
   vocabulary wave; the namespace freezes with the core regardless.
2. **Dedup-hash correctness is a corruption risk, not a perf risk.** Hash-equal nodes are shared
   by reference identity. The canonical-AST normalizer therefore ships behind a property-test
   battery on golden demos, as a hard gate.
3. **Catalog/semantic versioning.** Rulesets reference a catalog that changes every app release,
   and a facet definition change silently changes every shared ruleset's numbers. Ship: catalog
   version stamped into rulesets at save, `min_app_version` in share metadata, a deprecation
   policy for views/facets, and output-name stability lints (renaming a stat breaks downstream
   clip-tool joins — the exact use case the centerpiece example serves).
4. **Migration of *user* files, not just shipped ones.** v1 files in the wild get: detection at
   load ("this is a v1 file"), one release of legacy-loader coexistence, and a best-effort
   mechanical v1→v2 translator (M, does not gate shipping — vocabulary wave). Golden re-baselines
   follow the goldens policy (§3.2): defect-driven fixes land as a matter of course in their
   own auditable commit; only deliberate semantic changes are owner decisions.
5. **Identity/side semantics.** `player.team` across halftime swaps (works today via
   `PlayerContextIndex`), bot takeovers, and reconnect slot reuse need documented semantics +
   fixture demos — the CT/T stat family otherwise produces silently side-swapped numbers for
   exactly the user who can't debug it. Round-2 update: the 4-event lazy-materialization list
   is resolved (catalog-driven materialization, §3.1), which closes event *loss* but not
   side attribution — mid-match-start demos resolve `player.team` to 0 for everyone
   (`PlayerInfo.Team` is a last-`player_team`-wins post-pass); the fix (roster/team seeding
   from first-full-packet controller entities) is scoped to this risk's workstream and
   interacts with lazy scanner activation and the protected `DemoParser.cs` post-pass.
6. **Declared read sets — resolved by the engine track** (§3.1 round-2 additions):
   edges declare read sets emitted from the typed AST; readers order after writers; same-event
   read cycles are a build error with an `after: <stat>` fix-it. The remaining open edge of
   this family is live `compute:` — deferred, evidence-gated per §3.1. Non-node shared
   state (`PlayerContextIndex`) is governed by risk 1's single-writer rule; extending
   declared read/write sets to cover it remains a future option, not a dependency.
7. **Perf budgets.** Load-time canonicalization/checking, trace recording, and node-count deltas
   get budgets against the existing bench suite (eval ≤ 3.5 s ± noise on the benchmark demos;
   checker < 1 s on the shipped corpus), enforced as bench gates like every prior perf effort.
   Shared-ruleset node budgets (per-player × capture-list × `for_each`) get a load-time lint.
8. **Platform matrix.** File watcher, `code --goto`, and the workbench are desktop-only; WASM
   gets read-only gallery + diagnostics. Scope explicitly per target when the workbench lands.
9. **The how-to layer.** The Catalog kills *reference* drift, but the retired v1 guide's
   replacement — a tutorial for non-programmers — is real budgeted work in the core wave, not
   an afterthought.
10. **Expression-language ownership** is a permanent commitment (grammar, checker, docs, editor
    support), accepted deliberately over CEL. Revisit only if a mature .NET CEL with a canonical
    hashing story appears; the AST layer isolates the bet.
11. **High-frequency capture lists (settled 2026-07-10: collection-valued nodes, no caps).**
    `keep: list` compiles to a single `List<T>`-valued node (§3.1) — verified compatible with
    every optimization contract, and the serialization layer flattens with exact measured
    widths, so no size declaration or width hint exists anywhere. The one residual concern is
    unbounded growth on high-frequency triggers (a capture on every `weapon_fire` reaches
    hundreds of entries per round — never a correctness issue, but memory/export bloat and a
    silly display). Handling: a **loud static lint** when a capture list's trigger is in the
    catalog's high-frequency class, a post-run diagnostic when any list grew past a threshold,
    and in the UI the node's display value past the threshold renders as the placeholder
    **`2MUCH`**. Open: the threshold number itself (pick from bench-demo distributions).
12. **View curation fan-out.** Actor-anchored views multiply catalog entries (one wire event →
    2–3 views, each with baked filters like `kill` excluding suicides and actor-relative facet
    definitions). Each view is editorial content that silently mis-binds every rule using it if
    wrong — pin every view with a fixture test against demofile-net ground truth, and generate
    the mechanical parts (field projections) so curation is limited to the actor/filter/facet
    declarations.
