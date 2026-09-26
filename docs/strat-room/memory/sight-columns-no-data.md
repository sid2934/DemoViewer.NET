---
name: sight-columns-no-data
description: "The 17 stat columns that need line-of-sight geometry, and how the board now says \"no data\" instead of rendering zero"
metadata: 
  node_type: memory
  type: project
  originSessionId: 0571847b-02ea-40d0-8e50-97679ce52e3a
  modified: 2026-09-12T04:03:27.647Z
---

Seventeen shipped stat columns can only be computed from a line-of-sight pass, so a run with no collision geometry makes every one of them read a hard `0` that is indistinguishable from a measurement. On the Aim Quality board that is seven of the eight visible columns, which looks like ten players with a 0 ms reaction time rather than a missing asset.

The set, verified empirically by running one real demo with and without its soup rather than by reading the rules: `Preaim`, `XPlace`, `FlickErr`, `TTS`, `TTD`, `AimRx`, `TTK`, `SAcc%`, `SprayAcc%`, `FB%`, the hidden denominators `Spots`, `XShots`, `TTSn`, `TTDn`, `AimRxn`, `TTKn`, and the round board's `Spot`. Unaffected: `Spray`, `SprayN`, `CSAtt`, `Acc%`, `HSAcc%`, `HSDmg%`, `CS%`, `CSAll%`, `Linear%`.

**Why:** the columns are anchored on a synthesized `enemy_spotted` event, which the builder emits only when an engine was handed to it. Everything else is computed from the shots themselves.

**How to apply:** `ColumnCatalogue` marks these with `sight: true` (`ColumnMeta.RequiresVisibility`); a new column anchored on first contact must be marked too, or it will silently join the zeros. `StatsTabViewModel.SightIsUnavailable` decides availability from the **data first** (`ColumnCatalogue.VisibilityAnchorColumn`, the `Spots` count, zero for every player on the board) and falls back to the bake file only when the table never declared that column. Data-first is load-bearing: it also catches a bake that failed to load and a demo source that cannot support the pass, and it must not be reversed, because a board with positive counts demonstrably has the data whether or not a bake resolves from this machine. Unavailable cells carry a null `Raw` so they drop out of peer domains, leader stars and sorting, render an en dash, and raise a board notice naming the map and the columns.

Related: [[assetbaker-run-windows]] for why four of the nine shipped maps had no soup in the first place.
