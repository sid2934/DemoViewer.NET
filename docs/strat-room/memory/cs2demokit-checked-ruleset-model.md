---
name: cs2demokit-checked-ruleset-model
description: "CheckedRuleset is the document-derived graph model the node editor needs, and it is fully public; do not hand-roll reference extraction or use AuthoringGraph for authoring."
metadata: 
  node_type: memory
  type: project
  originSessionId: 7e2eaec4-3eba-4911-ae44-67628f7120d3
  modified: 2026-09-22T20:35:49.376Z
---

`RulesetComposition.Compose(docs, adapter, tickRate, profile).Rulesets` returns `CheckedRuleset`,
and it carries everything the Rule Workbench node editor needs. All of it is **public**, and the
Workbench already makes this call in `RenderGraph`.

`CheckedStat`: `StatId`, `Kind`, **`ValueType`** (the port type), `Scope`, **`ConcreteEvents`** (the
resolved `on:`, i.e. the header chips), `TriggerCondition` / `ValueSelector` / `WhileGate` (each a
`CheckedExpression`), **`DeclaredReads`** (the sibling stat ids it reads), **`Position`**, `Label`,
`Format`, `RateOf`, `RatePer`, `TallyThresholds`, `BucketKeyParts`.
`CheckedHighlight`: `When`, `DeclaredReads`, `Position`, `CountNodeId`, `Score`, `Kind`, `Group`.
`CheckedExpression.References` gives `ResolvedReference` with **`IsStatReference`** and
**`StatPath`**, so the engine itself says which references are stat references.

**Why:** `AuthoringGraph.Build` emits edges only from `GraphEdgeDescriptor`. A `compute:` lowers to
a round-end edge that emits none, so measured over `rules/` it draws **0 of 17** of `aim_rating`'s
compute stats with any edge, while 137 of 240 corpus edges fan out of four lifecycle hubs. The same
corpus through `CheckedRuleset.DeclaredReads` gives **17 of 17** connected, 106 edges that are all
stat-to-stat, isolated nodes down from 47% to 26%, every node carrying a `SourcePosition`, and
`player_stats` drawing **67 nodes instead of 0**.

**How to apply:** build the authoring canvas from `CheckedRuleset`, not `AuthoringGraph`.
`AuthoringGraph` is a display reduction and is right for the Analysis tab, wrong for authoring. Do
not write an identifier regex over expression text: `DeclaredReads` is precomputed and
`CheckedExpression.References` is the engine's own answer. Validity for a connect gesture comes from
composition and `ValueType`, not from `CatalogResource`.

See [[cs2demokit-local-checkout]] for why to verify this by reflection rather than the checkout.
