# The rule graph: what it draws, what it should draw, and a node-based rule editor

**Status: plan FINAL. The graph fix is IMPLEMENTED** on `fix/analysis-graph-materialized-nodes`
(§3 records what shipped); the version bump, the readability pass and the node editor are not started. Written 2026-09-17 against `main` at `0eebe12`,
CS2DemoKit pinned to **0.11.0**. Covers issue [#15](https://github.com/sid2934/DemoViewer.NET/issues/15)
(the Analysis graph draws scaffolding and no rules), [#4](https://github.com/sid2934/DemoViewer.NET/issues/4)
(readability pass), [#3](https://github.com/sid2934/DemoViewer.NET/issues/3) (isolate a node's
sub-chain), and the proposal to adopt `nodify-avalonia` as a renderer and as the base for a visual
rule editor.

The four work streams are **the graph fix**, **the version bump**, **the readability pass** and
**the node editor**, in that merge order (§10).

It also carries the **Avalonia 12 and SkiaSharp 3 bump** as its own work stream
([§7](#7-the-version-bump-avalonia-12-and-skiasharp-3)), landing as a separate PR, because the
question came up while choosing a renderer and the answer decides the merge order of everything else.

Every count in this document was **measured**, not estimated. The probes are described in
[§11](#11-how-the-numbers-were-measured) so they can be re-run.

---

## 0. The three things a reader needs first

**0.1 The issue's line numbers do not apply to this tree.** Issue #15 cites
`feature/cs2demokit-0.12-streaming` at `9afd5235`. That branch does not exist on `origin`, and
`git cat-file -t 9afd5235` fails. The issue also describes a 0.12.0 engine bump; **0.12.0 is not
published** and nuget.org's latest CS2DemoKit is still 0.11.0. The engine's unreleased
`work/forward-only` branch carries the `AnalysisRun.MaterializedNodes` / `.FinalNodes` names the
issue uses, untagged and unmerged. Section 2 restates the defect against types that exist today.

**0.2 The fix does not need an engine bump.** `EvaluationResult.MaterializedPlayers[i].Nodes` and
`.EdgeDescriptors` are fully populated in pinned 0.11.0. The join that issue #15 asks for is
available now. What is **not** available at any version is `GroupHints`, `NodeChains` and `Chains`,
which are dead in the engine ([CS2DemoKit#50](https://github.com/CS2OpenDev/CS2DemoKit/issues/50))
and still dead on the unreleased branch.

**0.3 The fix makes the graph 62x bigger, and breaks node identity.** Measured on
`demos/pro/furia-vs-vitality-m1-mirage.dem` with the fifteen shipped rulesets:

| | nodes | edges |
|---|---|---|
| What the viewer draws today (`build.Nodes` / `build.Edges`) | **61** | **43** |
| Collapsed to one representative player | **434** | **297** |
| Naive expansion, all ten players | **3 791** | **2 583** |

Across that 3 791-node merged set there are **432 distinct names and 371 colliding names**, most of
them appearing exactly ten times (`Alive`, `Survived`, `Traded`, `round_team_alive`, and so on).
Graph breakpoints are keyed by node **name** and persisted to disk, so the fix silently converts
every per-player breakpoint into a ten-way match. **Identity has to be fixed in the same change as
the join, not after it.**

---

## 1. What the Analysis tab does today

Two graph builds run per analysis, and they produce the same node set.

- **Skeleton**, before evaluation: `AnalysisViewModel.RunAsync` calls `RenderGraphSkeletonAsync`
  (`ViewModels/AnalysisViewModel.cs:739`), which calls `RuleGraphSkeleton.Build(build)`
  (`ViewModels/RuleGraphSkeleton.cs:62-116`) and pushes the result through
  `GraphViewModel.SetGraphAsync` (`:1045-1048`). Reads `build.Nodes`, `build.Edges`,
  `build.NodeChains`, `build.GroupHints`.
- **Post-evaluation**, inline at `AnalysisViewModel.cs:794-920`: iterates **the same**
  `build.Nodes` (`:797`), `build.Edges` (`:817`) and `build.GroupHints` (`:883`).

So the progressive reveal reveals no new node. What does change between the two renders is
`TrackedIndex` (`-1` to a real snapshot column) and the live `IsActive` / `DisplayValue` read off
the mutable `StateNode` the evaluator writes into (`:807-808`). Calling the reveal a no-op overstates
it: colours and values do update. Calling it a **topology** reveal is what is wrong. The shape is
final before a single message is evaluated.

### 1.1 Where rows are dropped without a count

| Site | What is dropped |
|---|---|
| `RuleGraphSkeleton.cs:87-91` | edge whose endpoint misses the node map |
| `AnalysisViewModel.cs:819-827` | same, two explicit `continue`s (source, destination) |
| `AnalysisViewModel.cs:844-847` | edge with no `build.EdgeBacking` entry |
| `AnalysisViewModel.cs:1348` | **all** tables when `MaterializedPlayers.Count == 0` |
| `AnalysisViewModel.cs:1365` | table template when `players[0].ColumnAssignments` is empty |
| `AnalysisViewModel.cs:1427-1430` | column connector whose source is not a lifecycle node |
| `AnalysisViewModel.cs:1432-1435` | column connector whose source has no node view model |
| `AnalysisViewModel.cs:1447-1450` | column connector whose destination is not a column of the group |
| `Visualization/Internal/MsaglTranslator.cs:55-58` | self-loops, routed separately by `SelfLoopPass` |
| `Visualization/Internal/MsaglTranslator.cs:113-116` | edges MSAGL returns no curve for |

Nothing counts any of these, and nothing caps the total. The column schema and the connector edges
are taken from `players[0]` alone (`:1370-1373`, `:1386`, `:1425-1456`), so a roster whose first slot
materialises differently from the rest silently defines the table for everybody.

### 1.2 Two claims in issue #15 that do not survive checking

Recorded so they are not re-derived, and because both change what the fix has to preserve.

- **"`result.FinalTrackedNodes` is used only to stamp `TrackedIndex`" is false.** It also backs
  `_trackedNodesByColumn` (`:755`), the column authority for the whole seek and snapshot loop; the
  `_availableConditionIdentifiers` autocomplete pool (`:758`); and table-cell column resolution in
  `BuildPlayerTables` (`:1402-1409`, a linear scan per cell). Any change to how nodes are sourced
  has to keep all four working, and the snapshot **column index** must stay aligned with
  `FinalTrackedNodes` ordering or every breakpoint hit index moves.
- **"`MaterializedPlayer.Nodes` is never read" is false outside the Analysis tab.**
  `Modules/RuleWorkbench/WorkbenchTrace.cs:177` iterates it to attribute nodes to players. The claim
  holds for the Analysis graph specifically, which is what the issue is about.

### 1.3 Features reading collections the engine never fills

Confirmed against the engine source, not inferred:

- **`build.NodeChains` is always `null`.** Documented as such on `BuildResult`
  (`Graphs/BuildResult.cs:32-38`): the v1 chain layer was removed and the slot kept for a future v2
  surface. Consequence: in `PopulateFilter` (`AnalysisViewModel.cs:1299-1313`) every chain chip
  falls to `ChainScope.PerPlayer`, because the branch that would mark one game-scoped needs a
  non-empty map.
- **`build.GroupHints` is always empty.** The list is constructed and threaded through
  `RuleChainBuilder` and never appended to. Every cluster group in the Analysis graph is therefore
  a feature with no input.
- **`build.Chains` is always empty**, same shape: `conjunctions.Add` has no call site. This is what
  `tools/AnalysisBench/Program.cs:401` prints as `0 chains`.
- **`ChainIds` is empty on every node**, including down the authoring path, because
  `AuthoringGraph` feeds it from `NodeChains` (`AuthoringGraph.cs:132-135`). So
  `ShowSubGraphAsync` (`:2757-2829`), which selects nodes by overlapping `ChainIds`, collapses to
  column sources plus one hop upstream plus root. **This is also why issue #3 cannot be built as
  written**: per-player chain membership lives on `PerPlayerColumnAssignment.ChainId`, not on the
  node.

`tools/AnalysisBench/Program.cs:401` and `:615-617` print `build.Nodes/Edges/Chains` as the graph
size. That is the scaffolding count presented as the graph. `RuleWorkbenchGraphTests.cs:48-49`
asserts `skeleton.Nodes.Count == build.Nodes.Count`, which is tautological against
`RuleGraphSkeleton.Build`'s 1:1 mapping and cannot fail for the reason the test exists.

---

## 2. What the engine actually offers at 0.11.0

| Member | State |
|---|---|
| `BuildResult.Nodes` / `.Edges` | populated, **game-scope scaffolding only**: root, entity node, enrichments, built-in contexts. Per-player rule nodes live un-materialised as `PerPlayerNodeTemplate` on `Graph` |
| `BuildResult.NodeChains` | always `null` (documented) |
| `BuildResult.GroupHints` | always empty (dead write path) |
| `BuildResult.Chains` | always empty (dead write path) |
| `EvaluationResult.FinalTrackedNodes` | populated, superset of `build.Nodes` plus materialised per-player nodes. **3 661** on the reference demo |
| `EvaluationResult.MaterializedPlayers` | populated, **10** |
| `EvaluationResult.MaterializedEdgeDescriptors` | populated, **2 540**, zero references in this repo |
| `MaterializedPlayer.Nodes` / `.EdgeDescriptors` / `.ColumnAssignments` | populated: **373 / 254 / 147** per player |
| `AnalysisRun.MaterializedNodes` / `.FinalNodes` | **do not exist at 0.11.0**. Unreleased `work/forward-only` only |

Two facts shape every design below.

**Nodes have no id.** `StateNode` (`Abstractions/StateNode.cs`) is an abstract base whose identity is
`Name`, a string described as the unique display name. `GraphEdgeDescriptor` references endpoints by
**object reference**, and the engine's own maps key on `ReferenceEqualityComparer.Instance`. Object
identity is therefore reliable **in process**; the string is not unique once per-player nodes join
the set, and the string is what survives to disk.

**`IsPerPlayer` and `ChainIds` exist only on `AuthoringGraphNode`** (`Graphs/AuthoringGraph.cs:190-209`),
the engine's purpose-built viewer DTO, reached only through `AuthoringGraph.Build`, which the Rule
Workbench uses and the Analysis tab does not.

---

## 3. The graph fix: repair what the Analysis graph draws

Closes #15. No new dependency, no renderer change, nothing about nodify. This phase is worth
shipping alone.

**Shipped, with three of the seven altered in flight.** What actually landed, against what this
section asked for:

| Requirement | State |
|---|---|
| Join the real node set | as written |
| Stable identity, not the name | as written (`GraphNodeKey`) |
| Migrate persisted breakpoints | **changed, twice**: decision 2 in §9 chose to drop them; review then established the drop rested on a false premise, and they are MIGRATED. A pre-v2 file can only hold game-scope records, so `name` to `g:{name}` is exact. The proposal below (bind a legacy record to every slot) was never needed. The mechanism is a new filename rather than the schema version §9 describes |
| Collapse per-player copies by default | as written, plus a rebuild so the selector actually switches player |
| Count the drops, and show the count | **partial**: one counter over the graph-build edge sites, surfaced in the toolbar. The table sites and the two `MsaglTranslator` sites in §1.1 are still uncounted |
| Delete the dead features | as written |
| Fix what the bench and tests report | **changed**: the bench prints scaffolding and drawn counts separately. `RuleWorkbenchGraphTests` asserts root presence and name uniqueness rather than "a known rule id produces a node" |

§3.1's suggestion that graph construction be extracted out of `AnalysisViewModel` **did not happen**;
the file grew from 3 086 to about 3 500 lines. Three things found while building are in §3.2.

**Join the real node set.** Source nodes from `build.Nodes` joined with
`MaterializedPlayers[*].Nodes`, and edges from `build.Edges` joined with
`MaterializedEdgeDescriptors`. `AuthoringGraph.Build` already performs exactly this join for the
Workbench and is the model to copy, including its anchors-plus-upstream reduction.

**Stable identity, not the name.** Required by the measurement in
§0.3: 371 names collide ten ways. The identity has to be:

- unique across the merged game-scope and per-player set;
- **stable across runs of the same demo**, or persisted breakpoints do not survive a re-run;
- **derivable without the evaluation**, or the skeleton cannot carry it;
- writable to JSON.

The natural key is `(templateIndex, playerSlot, name)` for per-player nodes and `(null, null, name)`
for game-scope ones. Player **slot** rather than name or discovery order: slots are stable, and
0.12.0's roster change (discovery order, and no table row for a slot with no materialising event)
would otherwise reshuffle identity.

**Migrate persisted breakpoints.**
`%AppData%/DemoViewer.NET/GraphBreakpoints.json` is keyed by demo content SHA-256 and stores
`NodeName` plus the edge 4-tuple `(EdgeSource, EdgeDest, EdgeLabel, EdgeConditionLabel)`
(`Services/GraphBreakpointStore.cs`, `Debugging/GraphBreakpoint.cs:126-134`). A name-only record
that now matches ten nodes must resolve to something defined. Proposal: treat a legacy record as
**game-scope first**, and if the name resolves only per-player, bind it to **every** slot and say so
in the breakpoint list rather than silently picking slot 0. Write the new key on save.

The name-keyed sites to change are `EdgeKey` (`AnalysisViewModel.cs:1594-1595`, used at `:849`,
`:2116`, `:2538`, `:2576`), `InsertPickedNode` (`:1650`), `NodeColumnByName` (`:2128`, called from
`:2124` and `:2501`), and `GraphBreakpointService.cs:39-70` / `ConditionTarget.cs:56-57`.

**Collapse per-player copies by default.** 3 791 nodes is not a view
(§4.2). Default to one representative player with a selector, which is what the player filter at
`:1318-1322` already implies. "Expand this template across all ten" is a per-template action, not a
global mode.

**Count the drops, and show the count.** One counter per site in §1.1, surfaced in the status
line and the diagnostics panel. A graph that quietly omits 2 540 edges should say so.

**Delete the dead features**, or gate them off with a comment naming
CS2DemoKit#50: chain chips, cluster groups from `GroupHints`, and `ChainIds`-based sub-graph
selection. Shipping a filter chip that cannot filter is worse than not shipping it. **Issue #3 is
blocked on this** and should be re-scoped onto `PerPlayerColumnAssignment.ChainId`.

**Fix what the bench and tests report.** `AnalysisBench` should print the materialised
size next to the build size instead of presenting 61/43 as the graph. `RuleWorkbenchGraphTests`
should assert that a known rule id produces a node, which is the property the current tautology
was trying to express.

### 3.1 What the graph fix costs

`AnalysisViewModel.cs` is **3 086 lines**. Graph construction and filtering is roughly 850 to 950 of
them, and it is not cleanly partitioned: the filtering and projection block at `:2664-3086` handles
graph sub-graphs and table rows together, and the 770-line condition editor at `:1894-2664` reaches
node and edge identity through the same name-keyed helpers the identity work changes. Assume it touches the
builder, the filtering block, the condition editor, the breakpoint service, the store, the bench and
two test files. Extracting graph construction out of `AnalysisViewModel` into its own type is worth
doing as part of this, not after.

---

### 3.2 What building it turned up

**An off-thread theme read, latent since the per-player border was introduced.**
`GraphNodeViewModel.Style` resolved its border from `Application.ActualThemeVariant`, a
UI-thread-only `StyledProperty`, while MSAGL layout runs on a background thread. It never fired
because the getter returns `null` unless `IsPerPlayer`, and the Analysis graph had no per-player
nodes. Adding them turned it into an exception inside `RunAsync`, whose `catch` swallows into
`StatusText`, so `EvaluationCompleted` never fired and the Stats tab and Match Overview came up
empty. Four unrelated-looking test failures, one cause. The Workbench authoring graph sets
`IsPerPlayer` too, so it was reachable there as well.

**Descriptors do not describe most of the graph.** A `GraphEdgeDescriptor` is emitted only for a
trigger-backed rule edge; enrichment, resets, first-tick and round-end-compute wiring gets none.
Drawn from descriptors alone, **187 of 434 nodes had no edge at either end**. The runtime `StateEdge`
carries the wiring (`Source`, `WrittenNode`, `AdditionalWrittenNodes`), and
`MaterializedPlayer.Edges` is public, so the per-player half is recoverable: **187 orphans became
54**. The remaining 54 are 6 per-player and **48 game-scope enrichment nodes** whose `StateEdge`s sit
behind `StateGraph.Edges`, which is `internal`. Those need an engine change, and belong on
CS2DemoKit#50 with the rest of the dead graph surface.

**Every scoreboard column is now also a graph node.** Measured: **147 of the 434 drawn nodes** are
the `PerPlayerColumnAssignment.Node` of a table column, so each of those stats appears twice on the
canvas, once as a node with an incoming edge and once as a column with a connector from the same
source. This is a consequence of drawing what ran, not a defect in the join, and the two reasonable
answers (suppress the column node in the graph, or suppress the table for the rendered player) are
both presentation decisions. **Left as-is deliberately**: hiding 147 nodes to tidy the picture is a
smaller version of the defect this phase exists to fix. It belongs to the readability pass (§5).

## 4. The renderer decision

### 4.1 The package named in the proposal cannot be installed

There are two Avalonia ports of Nodify, and the difference is decisive.

| | `Nodify.Avalonia` (trrahul) | `NodifyAvalonia` (BAndysc) |
|---|---|---|
| Latest | 2.0.0, 2026-07-14 | 6.6.0, 2026-02-14 |
| Requires | **Avalonia >= 12.0.5** | **Avalonia >= 11.1.0** |
| TFM | net8.0 | netstandard2.0 |
| Tracks upstream | v7, one squashed re-port after a 2.3-year gap | v6.6.0, by merging upstream tags |
| Stars | 75 | 308 |
| Licence | MIT | MIT |
| Against our Avalonia 11.3.12 | **restore fails** | **builds clean** |

Verified, not inferred. A scratch project on Avalonia 11.3.12 referencing `Nodify.Avalonia` 2.0.0
fails with `NU1605: Detected package downgrade: Avalonia from 12.0.5 to 11.3.12`, and `NU1605` is
promoted to an error in `Directory.Build.props` deliberately. The same scratch project referencing
`NodifyAvalonia` 6.6.0 builds with 0 warnings against **both** `net10.0` and `net10.0-browser`, and
instantiates `NodifyEditor`, `Node`, `GroupingNode`, `Minimap` and `StepConnection`.

BAndysc's README opens with the warning that names this exact confusion: *"Please do not confuse
with `Nodify.Avalonia` which is a different package"*.

Adopting trrahul's port means moving the whole solution to Avalonia 12, which is **the version bump**
([§7](#7-the-version-bump-avalonia-12-and-skiasharp-3)). `NodifyAvalonia` does not: it targets
`netstandard2.0` against `Avalonia >= 11.1.0` with no upper bound, and it compiles clean against
**both** 11.3.12 and 12.1.2. So **nodify is reachable from either Avalonia major, and only
`NodifyAvalonia` is reachable from where we stand today.** Adopting a node editor is not a reason to
take the version bump, and taking the version bump is not a reason to switch ports.

**Correction to an earlier reading of this, recorded so it is not re-derived.** The
`Directory.Packages.props` note that SkiaSharp is a derived pin is right, and the hazard it describes
is bumping SkiaSharp *alone*: Playback2D takes Avalonia's own `SKCanvas` through
`ISkiaSharpApiLeaseFeature`, and a mismatched major is a different type identity. That is **one call
site**, `Modules/Playback2D/SceneDrawOperation.cs:79-85`, and an Avalonia bump moves both sides
together, so the lease is not the obstacle it first appears. §7 sizes what the bump actually costs.

### 4.2 Size is the real obstacle, and it is not MSAGL

Measured MSAGL cost with this repo's exact settings (Sugiyama, `Rotation(pi/2)`, `Rectilinear`,
`NodeSeparation=60`, `LayerSeparation=160`, `Padding=12`):

| graph | layout time (best of 2) |
|---|---|
| 61 / 43, today | 18 ms |
| 434 / 297, collapsed | 199 ms |
| 1 000 / 700 | 481 ms |
| 3 791 / 2 583, expanded | 1 299 ms |

Layout is **not** the problem. It already runs off the UI thread in `GraphViewModel.SetGraphAsync`.
The problem is the size of the resulting canvas, and that figure is now **measured on the real graph
rather than estimated**: the shipped 61-node graph lays out to **1 104 x 6 068 px**, and the fixed
434-node graph to **2 999 x 32 528 px**. That is 6.7 megapixels becoming 97.6, about **15x by
area**, with the same node size and the same separations; only the node count moved. 434 boxes of
180x46 cannot be made to fit a screen at a readable zoom, whatever draws them. This is why the graph
fix collapses per-player copies by default, and it is the substance of issue #4.

An earlier revision of this section guessed "on the order of 2 200 x 27 000" from a synthetic
topology. The height was close; the width was not by nearly a factor of two. Both renders, and the
capture recipe behind them, are in the comparison artifact.

### 4.3 What each renderer can carry

The current library is a single `Control` doing immediate-mode drawing: `GraphView : Control`
overrides `Render(DrawingContext)` and paints every node box, edge, arrowhead, label, group and
table cell itself (`GraphView.cs:105`, `:122-144`). There is **no Avalonia visual per node**. That
is the opposite shape from nodify, which wraps every `ItemsSource` entry in a real templated
`ItemContainer`.

| | current renderer | NodifyAvalonia |
|---|---|---|
| Per-node cost | one rect plus text in a shared pass | a full templated control |
| Virtualization | none | none, **by explicit design** (upstream FAQ) |
| Auto-layout | MSAGL, 6 passes, ~1 300 lines | none. Upstream's maintainer recommends MSAGL |
| Node dragging | none, layout is authoritative | first class, with `PreviewLocation` |
| Groups | `INodeGroup`, AABB overlay | `GroupingNode`, a real container |
| Minimap | none | built in |
| Undo/redo | none | none, explicitly the consumer's job |
| Tables (scoreboard) | bespoke `INodeTable` overlay with its own placement and routing | no equivalent, stays custom |
| Headless capture | `GraphView` does not settle geometry; `UiCapture` works around it | templated controls settle normally |
| Connection styles | rectilinear only (the Bezier path is dead code) | line, bezier, step, circuit |

The read on nodify's performance ceiling comes from its own issue tracker: BAndysc#10, still open,
reports ~50 nodes with most connections made becoming laggy, a contributor measuring ~100 nodes at
20-30 fps, and the maintainer attributing it to Avalonia lacking WPF's `CacheMode`. WPF Nodify
handles ~400 nodes / 1 000 connections in the same comparison. **434 nodes is at or past that
ceiling, and 3 791 is far past it.** Nodify is not a drop-in answer to a graph this size.

### 4.4 Recommendation

**Do not replace the Analysis viewer's renderer.** It draws a large read-only graph cheaply, and
that is the thing nodify is worst at. Spend the effort on the graph fix and on issue #4's readability work
inside the renderer we have.

**Do use `NodifyAvalonia` for the node-based rule editor**, where the graph is one ruleset rather
than a whole evaluation (a shipped file's authoring graph is tens of nodes, not hundreds), and where
per-node controls, dragging, real connectors and `GroupingNode` are exactly what the job needs. Keep
MSAGL for initial placement, which is what Nodify's own maintainer recommends and what we already
have.

That gives two graph surfaces with two renderers. `docs/ui/design-system.md` decision D3 already
says the breakpoint surfaces stay distinct and must not be merged, so this follows an existing
decision rather than cutting against one.

---

## 5. The readability pass (issue #4)

Inside the existing renderer, independent of nodify, and mostly unblocked by the graph fix.

- **Conditions off the edge.** A predicate rides on `IGraphEdge.ConditionLabel` and crowds the
  edge. Render it as a chip anchored to the edge midpoint with collapse-by-default, or as a small
  gate node. `LabelPlacementPass` (260 lines) already does greedy collision avoidance and is where
  this lands.
- **A per-player template layout.** The repeated per-player subtree is 373 nodes of identical
  shape. It should draw as one template with a slot selector, not ten copies. This is the visual
  half of collapsing per-player copies.
- **Spacing and routing.** `LayoutStyleConfig` has three knobs. Re-tune against
  `LayoutMetricsHardGateTests`, which already asserts six overlap and intersection metrics at zero
  across 13 fixtures, so a regression here is caught.
- **A fixture at the real size.** The largest stress fixture is `BuildBigStandard` at 120 nodes
  / ~200 edges. Add a 434-node fixture, or the gate keeps passing at a size we no longer ship.

---

## 6. The node editor

The Rule Workbench today is an AvaloniaEdit YAML editor
(`Views/RuleWorkbench/RuleWorkbenchView.axaml:217-222`) with a read-only MSAGL graph behind a
toggle. About 3 115 lines across the module and its views.

### 6.1 The blocking problem: there is no round-trip

`AuthoringGraph.Build` is a **one-way lossy reduction built for display**, and it lives in
CS2DemoKit, not this repo:

- it **drops** every node not reachable upstream from a declared anchor, by design;
- it **materialises per-player templates once at slot 0** as a preview trick, not a real expansion;
- `AuthoringGraphNode` carries `Name`, `Subtitle`, `IsRoot`, `IsPerPlayer`, `DisplayValue`,
  `ChainIds` and **no back-reference** to the YAML span, the `StatDef`, or anything editable;
- document-level constructs have no graph representation at all: `use:`, `exports:`, `params:`,
  `define:`, `title:`, `summary:`.

**So a node editor cannot be built on `AuthoringGraph`.** It needs a new editable model over
`RulesetDoc` / `StatDef` / `HighlightDef` / `TriggerDef`, with a YAML writer, and a stated position
on comments and key order, which any regenerating writer will lose. That is the largest single piece
of work in this document and it is **the thing to decide before anything else in the node editor**.

### 6.2 The vocabulary a node editor must represent

- **9 stat kinds**: `flag`, `count`, `sum`, `capture`, `compute`, `tally`, `streak`, `bucket`,
  `rate`, each with kind-specific fields (`thresholds:`, `window:`/`min_streak:`,
  `key:`/`value:`/`reduce:`, `of:`/`per:`, `keep:`).
- **Highlights**: `when:`, `title:`, `per:`, `score:`, `kind:` (highlight / funny / lowlight /
  hidden), `group:` supersession.
- **Triggers and predicates**: `on:`, `match:` (typed facet map), `where:`, `while:`, `off:`.
- **Scope**: `for: match | each_player` crossed with `per: round | match`.
- **Outputs**: `show.scoreboard` entries and `show.tables`.
- **The expression language**: full EBNF, six functions (`min`, `max`, `abs`, `floor`, `contains`,
  `startswith`), FEEL-style null propagation, duration literals. **This should stay textual inside a
  node field.** A visual editor for an expression grammar is a second product.

### 6.3 What can be reused as-is

`RulesetDocumentLoader.Load`, `RulesetComposition.ComposeDraft`, `RulesetDiagnostic` with its
`SourcePosition`, `WorkbenchDiagnostic.From`, `WorkbenchTraceModel.Build` for a per-node fire
inspector, and `CatalogResource.Load()` for the node-creation palette (it already drives completion
and the data browser). The 30 `Graph*` theme tokens in `docs/ui/theme-token-catalog.md` map onto a
nodify `ControlTheme`. **Gap**: diagnostics are positional (file, line, column), not node-keyed, so
a diagnostic-to-node mapping layer is new work.

### 6.4 Staging

- **Spike.** `NodifyAvalonia` in the app shell, desktop and browser, rendering a real ruleset's
  authoring graph read-only at MSAGL-computed positions. Answers the theme-include question, the
  WASM question and the per-node-control cost question for about a day's work. BAndysc ships a live
  WASM demo, so browser is plausible rather than proven for us.
- **The editable model and the YAML writer.** §6.1. Gate everything else on this.
- **Read-only nodify view of the open ruleset**, replacing the Workbench graph toggle only.
- **Editing**: drag, connect, add and delete against that model, YAML regenerated on save.
- **Undo and redo**, which nodify does not provide and which a node editor cannot ship without.

---

## 7. The version bump: Avalonia 12 and SkiaSharp 3

**Committed, and not on this document's account.** The graph work does not need it: §4.1 establishes
that `NodifyAvalonia` compiles against both Avalonia majors. The bump is taken because **SkiaSharp 3 is
worth having on its own**, and the clearest case is `tools/DemoViewer.NET.AssetBaker`, which cannot
be shipped as a first-class tool while the tree is on SkiaSharp 2.88.

Its own PR, carrying **no feature work**. That constraint is the point: a platform bump changes how
pixels rasterise, so it has to be the only thing in the diff when a golden moves.

### 7.0 Why SkiaSharp 3 is wanted independently: the AssetBaker fork

`ValveResourceFormat` 19.2.6339 depends on **SkiaSharp 3.119.2**, so AssetBaker is already on
SkiaSharp 3 and is fenced off from the rest of the tree to keep that from colliding with the app's
pinned 2.88.9. The fence is expensive, and every plank of it is a thing shipping has to undo:

| What the fence does | Where |
|---|---|
| Opts out of Central Package Management | `ManagePackageVersionsCentrally=false` |
| Pins its own SkiaSharp 3.119.2 outside the props file | `PackageReference Include="SkiaSharp"` |
| References **only** `SkiaSharp.NativeAssets.macOS` | so Windows needs an explicit `-r win-x64` |
| Hardcodes `RuntimeIdentifier=osx-arm64` | one developer's machine, in the csproj |
| Turns off warnings-as-errors and code-style enforcement | three properties |
| Removes `Nerdbank.GitVersioning` | so the tool has no version at all |

That is the whole cost of the SkiaSharp schism, paid in one project. **The version bump dissolves it**: Avalonia
12.1.2 resolves SkiaSharp **3.119.4**, VRF needs **>= 3.119.2**, so one version serves the entire
tree and AssetBaker rejoins CPM.

**Two of AssetBaker's own comments are stale and should be corrected in the same pass.** The csproj
says it is "NOT part of the main .slnx"; it is, at `DemoViewer.NET.slnx:47`. It cites
`docs/asset-pipeline/design.md §0.5`; that file does not exist. If AssetBaker is going to ship, that
design doc is where its shipping plan belongs, and this section is not a substitute for it.

**One real conflict to resolve, not to discover later.** VRF pulls
`SkiaSharp.NativeAssets.Linux.NoDependencies`, while `Directory.Packages.props` deliberately takes
the **full** `SkiaSharp.NativeAssets.Linux` because the golden corpus contains text layers and the
CI runner installs `libfontconfig1` for it. Unifying under CPM puts both in one graph. The full
package is the one to keep, and the props comment explaining why has to survive the merge.

### 7.1 There is no hard blocker

Verified by restoring the **whole** package set against Avalonia 12.1.2 in one scratch project:
`Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Fonts.Inter`, `.Controls.ColorPicker`, `.Skia`,
`.Headless`, `AvaloniaEdit` 12.0.0, `NodifyAvalonia` 6.6.0, `Cs2VideoGenerator.Core`, `Velopack`,
`SixLabors.ImageSharp` and `FFMpegCore`. Restore succeeds, no `NU1605`, no package without a 12-line
build. `Avalonia.Angle.Windows.Natives` has one too (2.1.27548.20260419).

**What the bump forces: SkiaSharp 2.88.9 to 3.119.4**, pulled transitively by Avalonia 12.1.2, along
with HarfBuzzSharp 8.3.1.3. That is the derived pin doing exactly what `Directory.Packages.props`
says it does.

### 7.2 The migration is narrower than the package count suggests

| Signal | Count | Reading |
|---|---|---|
| Files with `using SkiaSharp` | 120 | breadth, and 46 of them are `Playback2D.Tests` |
| `SKPaint.TextSize` | **0** | the SkiaSharp 3 removals we do not use |
| `SKPaint.FontMetrics` | **0** | same |
| `BreakText` | **0** | same |
| `SKFont` | 15 | **already on the API SkiaSharp 3 forces** (`Playback2D.Core/TextBlobCache.cs:253-260`) |
| `DrawText` | 33 | the real edit surface |
| `SKTextBlob` | 10 | ditto |
| `.Typeface` / `MeasureText` / `TextAlign` | 7 / 4 / 1 | ditto |
| `ISkiaSharpApiLeaseFeature` | **1 file** | `SceneDrawOperation.cs:79-85` |
| Committed goldens | **13 files, 884 KB** | `tests/fixtures/playback2d/goldens` |

The text migration is the expensive part of a SkiaSharp 2 to 3 move, and this tree already did the
hard half of it by building `TextBlobCache` on `SKFont`.

### 7.3 Two free wins and one wrinkle

- **`Avalonia.Diagnostics` has no 12.x** (stops at 11.3.22) and `AttachDevTools` is gone from
  Avalonia 12 core, confirmed by compiling against it. The app **never calls it**: the only
  `DevTools` hit in the tree is a comment about React DevTools in
  `Views/EntityTracking/EntityTrackingTabView.axaml:14`. It is a dead `PackageReference` in
  `DemoViewer.NET.csproj:31` and `DemoViewer.NET.Desktop.csproj:25`. **Delete it, do not replace it.**
- **`Avalonia.iOS` and `Avalonia.Android` pins are unused.** There are no mobile heads in `src/App/`.
  Drop them in the same pass.
- **`Avalonia.AvaloniaEdit` publishes only 12.0.0**, not 12.1.x. The Workbench editor runs a minor
  behind the rest of the family. Acceptable, worth a comment in the props file so the skew is
  deliberate rather than looking like a missed bump.

### 7.4 Requirements

**Inventory, before estimating.** Build the real solution against 12.1.2 on a scratch branch and
count compile errors by project. Everything above is restore-and-compile evidence from probes, not
from this codebase. Nothing else in the version bump should be scheduled until that inventory has a number.

**The package move.** `Directory.Packages.props` only: Avalonia family to 12.1.2, SkiaSharp and
its four native packages to 3.119.4, HarfBuzz as resolved, AvaloniaEdit to 12.0.0, ANGLE natives to
the 12-line build. Delete `Avalonia.Diagnostics`, `Avalonia.iOS`, `Avalonia.Android`. Re-read the
resolved versions from `artifacts/obj/.../project.assets.json` and rewrite the derived-pin comments
to match, because those comments are load-bearing and currently name 2.88.9.

**The SkiaSharp 3 source edits.** §7.2's surface. Confined to Playback2D and its tests plus the
one lease site.

**Re-baseline the golden corpus, once, in this PR.** 13 files. `SceneDeterminismTests` (byte-exact
against itself) and `GoldenParityTests` (delta distribution) both need re-capture, because a Skia
major shifts anti-aliasing. **A golden that moves in the version bump is expected; a golden that
moves in the readability pass is a bug.** That separation is why the bump is its own PR.

**Re-verify the two platform seams.** The ANGLE/EGL P/Invoke against `av_libglesv2.dll`
(`Playback2D.Core/Rendering/Interop/Egl.cs`) and the Browser head's `libSkiaSharp.a` linking, which
`DemoViewer.NET.Browser.csproj:19` documents by hand. Both are native-asset plumbing that a package
bump can silently rearrange.

**Re-measure, do not assume.** `AnalysisBench` drifts run to run, so quote plain mode and
interleave the before and after builds rather than comparing across sessions.

**Un-fork AssetBaker.** §7.0's table, reversed: rejoin CPM on the single SkiaSharp 3.119.4, add
the Win32 and Linux native packages beside macOS, drop the hardcoded `osx-arm64` RID, restore
`Nerdbank.GitVersioning`, and take the repo's warnings-as-errors and code-style settings back. Expect
VRF-interop nullability warnings to surface the moment that last one is restored: that is the point,
and fixing them is part of this rather than a reason to keep the escape hatch. Resolve the Linux
native-asset conflict in favour of the full package. Correct the two stale csproj comments.

**Un-forking AssetBaker is the one requirement here that may be deferred without hurting anything else.** It is the
reason the version bump is worth doing, not a prerequisite for the rest of it. If it grows, land the
rest and take the un-fork second; the fence stays standing one release longer and nothing else in this document notices.

---

## 8. Risk register

| # | Risk | Mitigation |
|---|---|---|
| R1 | The graph fix lands the join without the identity work, and every per-player breakpoint silently matches ten nodes | Identity and the join are one change. §0.3 is the test case |
| R2 | 434 nodes is unreadable even after the graph fix | It collapses per-player copies by default; the readability pass is scoped to exactly this |
| R3 | `NodifyAvalonia` 6.6.0 is 7 months stale and its Avalonia 12 request is open and unanswered | It is MIT and vendorable. It is also confined to the Workbench, so the Analysis tab is unaffected either way |
| R4 | A future Avalonia 12 move strands us: BAndysc's port has not moved, trrahul's is 12-only | The two ports converge on that bump rather than diverging. Re-evaluate then, not now |
| R5 | The YAML writer loses comments and key order on every save | Decide the position on the editable model first, before building on it. Round-trip preservation is a real cost |
| R6 | Nodify has no virtualization, ever, by design | Keep it off the Analysis graph. That is §4.4 |
| R7 | 0.12.0's roster change (discovery order, no row for a slot with no materialising event) reshuffles per-player identity | The identity key uses player **slot**, which that change does not move |
| R8 | The engine never fills `GroupHints` / `NodeChains`, so deleting the dead features is permanent | CS2DemoKit#50 is open. Deleting is reversible; shipping dead filters is what costs |
| R9 | A golden moves during the version bump and nothing says whether the platform or a feature did it | The bump carries no feature work and re-baselines once. This is why it is not folded into the readability pass |
| R10 | The real compile-error count is far above what the probes suggest | The inventory step exists to find that out before anything depends on the bump's timing. The graph fix does not, by §10 |
| R11 | `NodifyAvalonia` compiles against Avalonia 12 but its Avalonia-11-built `Themes/NodifyStyle.axaml` and `ControlTheme`s fail to load at runtime | Unproven either way; its tracker has an open, unanswered Avalonia 12 request. The nodify spike runs **after** the bump precisely to test this once, on the Avalonia we will actually ship |
| R12 | The version bump slips, and the later streams stall behind it | Only the readability pass and the node editor are behind it. The graph fix, the sole correctness fix, is not |
| R13 | Un-forking AssetBaker folds it back into CPM and VRF's `SkiaSharp.NativeAssets.Linux.NoDependencies` silently wins over the full Linux package the goldens need | Named in §7.0 as a decision, not a discovery. Keep the full package and keep the props comment that says why |
| R14 | Restoring warnings-as-errors on AssetBaker surfaces VRF-interop nullability warnings that are a project of their own | The un-fork is severable from the rest of the bump by design. Land the bump, take the un-fork second |

---

## 9. Decisions needed before implementation

1. ~~**Default view after the graph fix**~~ **Resolved 2026-09-17: one representative player plus a
   slot selector.** Scaffolding plus one player's nodes, 434 in total, reusing the player filter that
   already exists at `AnalysisViewModel.cs:1318-1322`. Expanding every player is a later, opt-in
   action, not the default.
2. ~~**Legacy breakpoints**~~ **Resolved twice.** First: drop them silently (2026-09-17). Then
   **reversed on review**, because that decision rested on a premise this document itself supplied
   and which is false. A pre-v2 file cannot contain an ambiguous record: before the per-player join
   the graph drew only the game-scope scaffolding, so every node a user could right-click came from
   `build.Nodes`, and `name` to `g:{name}` is an exact rewrite. **They are migrated**, then the old
   file is removed, from the Velopack after-update hook on Windows and from the store constructor
   everywhere else. The cost of the reversal was about fifteen lines; the cost of shipping the drop
   would have been every user's hand-authored breakpoint conditions, destroyed with no notice and no
   second chance.
3. ~~**Delete or gate** the three dead features~~ **Resolved 2026-09-17: delete.** The UI and the
   code paths go, leaving a comment naming CS2DemoKit#50 so the reason survives the deletion.
4. **YAML round-trip fidelity** in the editable model: preserve comments and key order (expensive, needs a
   CST-preserving writer), or regenerate and tell the user? This is the gate on the whole editor.
5. Is the node editor wanted **in the browser**, or desktop only? It changes how much the nodify spike
   has to prove, and it is the main thing riding on Avalonia 12's WASM story.
6. ~~**Does stream V happen at all**~~ **Resolved 2026-09-17: yes.** Not for anything in this
   document, but because SkiaSharp 3 is wanted on its own merits, AssetBaker being the clearest case
   (§7.0). The open sub-question is only whether **un-forking AssetBaker** rides along or follows.

---

## 10. Sequencing

Four streams, three PRs before any editor work: **the graph fix, then the version bump, then the
readability pass, then the node editor.**

**One of these orderings is a constraint and the rest are preferences.** Worth separating, because V
now has drivers outside this document (§7.0) and may want to move:

- **The version bump before the readability pass is a constraint.** The golden corpus argument below.
- **The graph fix before the version bump is a preference.** They are order-independent, and if
  shipping AssetBaker becomes the priority, the bump can go first at no cost to the graph fix.
- **The nodify spike after the version bump is a constraint** while the bump is happening at all. R11.

**The graph fix first, because it is the only stream that is Avalonia-free.** Every requirement in §3 is
ViewModel work: joining engine collections, keying node identity, migrating a JSON store, counting
drops. It reads no Avalonia API that version 12 changes, so it neither blocks V nor is blocked by it,
and it is the only stream with a user-facing defect behind it. Holding a correctness fix behind a
platform migration would be the wrong trade in either direction. That independence cuts both ways:
it is also what makes the graph fix cheap to reorder behind the bump.

**The version bump second, alone, before the rendering work.** The ordering argument is the golden
corpus: the bump re-baselines 13 goldens because Skia 3 rasterises differently, and the readability
pass changes layout and will move geometry. Run them in that order and each diff has one cause. Run
the readability pass first and you re-baseline twice;
run them together and a moved pixel has two candidate explanations and no cheap way to tell them
apart. R9 is this risk and this is its mitigation.

**The readability pass third, on the renderer we will actually ship.** It re-tunes `LayoutStyleConfig` and reworks
`LabelPlacementPass` against `LayoutMetricsHardGateTests`. That gate is pure geometry computed from
`LayoutResult` coordinates with no rasterisation, so it survives the bump untouched; but its drawing code
is Avalonia `DrawingContext` and `FormattedText`, which version 12 does touch. Tuning that twice is
waste.

**The node editor last, and its spike moves behind the bump.** This is a change from the first draft of this document, which
had the spike running in parallel. The single largest unknown about `NodifyAvalonia` is whether its
Avalonia-11-built XAML loads under Avalonia 12 (R11). Spiking on 11 answers a question we will not
have, and the spike would then have to be repeated. If the bump is deferred by decision 6, the spike runs on 11
instead and R11 becomes moot.

**What can overlap.** The graph fix and the version bump touch nearly disjoint trees, the first in
`src/App/DemoViewer.NET/ViewModels` and the second in `src/Playback2D`,
`tools/DemoViewer.NET.AssetBaker` and the package files, so the inventory step can run while the
graph fix is in review, and the two can genuinely proceed in parallel. Only the merge order relative
to the readability pass is fixed.

**Still blocked.** Issue #3 waits on the dead-feature deletion deciding what replaces `ChainIds`, not on any of this.

---

## 11. How the numbers were measured

Two throwaway console projects in a session scratchpad, neither committed.

- **Graph size** (§0.3, §2): references `CS2DemoKit.Parser` and `CS2DemoKit.Analysis` 0.11.0,
  reproduces the wiring in `tools/AnalysisBench/RulesCheckCommand.cs:125-150`
  (`YamlConfigLoader.TryLoadDirectory` on `rules/`, `RuleChainBuilder`, `RulesetComposition.Compose`,
  `DemoAnalysis.Evaluate`) against `demos/pro/furia-vs-vitality-m1-mirage.dem`, then counts
  `build.*`, `result.*` and every `MaterializedPlayer`, and groups the merged node set by `Name` to
  find collisions.
- **Layout cost** (§4.2): references `Msagl` 1.2.1 directly and reproduces `MsaglTranslator.cs`'s
  settings on synthetic graphs at each size. Topology is synthetic, so the times are sound and the
  bounding boxes are directional.
- **Package compatibility** (§4.1): two scratch projects pinning `Avalonia` 11.3.12, one per
  candidate package, built for `net10.0` and `net10.0-browser`.
- **Avalonia 12 feasibility** (§7.1, §7.3): one scratch project pinning `Avalonia` 12.1.2 with the
  whole package set, restored and listed with `dotnet list package --include-transitive` to read the
  resolved SkiaSharp and HarfBuzz versions; a second compiling `NodifyAvalonia` 6.6.0 against 12.1.2;
  a third compiling `Window.AttachDevTools()` against 12.1.2 to confirm it is gone. Counts in §7.2
  are `grep` over `src/` and `tools/`.

**What none of these prove.** Every probe is restore-and-compile. Nothing here was run: not the app
on Avalonia 12, not `NodifyAvalonia`'s XAML under either major, not the Browser head's native
linking. The inventory step and the nodify spike exist to close exactly that gap, and their estimates should not be treated as
firm until they have.

Re-running the first one against a second demo before implementation is worth the ten minutes: the
per-player node count is a function of the shipped rulesets, and all fifteen are `for: each_player`.
