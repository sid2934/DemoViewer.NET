# Stats Tab — UX Review & Redesign

The Stats tab redesign shipped on main (Phase A; `ColumnCatalogue` + the category sub-rail). Later
phases carry open/verify items (Weapons player×weapon matrix, Rounds stepper, Vision restyle,
Phase C polish). The verdict that started this: the current stats page is honestly bad.
**Scope:** the Stats tab — the user-facing half of the dual-audience split (the Analysis Engine tab
stays a developer graph-debugger; the Stats tab serves a CS2 player/analyst who wants
scoreboard / round / weapon / visibility numbers).

Grounded in:
- `src/App/DemoViewer.NET/Views/Stats/StatsTabView.axaml` (current view)
- `src/App/DemoViewer.NET/ViewModels/Stats/StatsTabViewModel.cs` (current VM)
- `src/App/DemoViewer.NET/Views/EntityTracking/EntityListView.axaml` (the house columnar pattern)
- `src/App/DemoViewer.NET/Styles/DarkPalette.axaml` (palette, single source of truth)
- `rules/player-stats.yaml`, `rules/kast.yaml`, `rules/weapon-stats.yaml` (column source)
- `src/Analysis/DemoViewer.NET.Analysis/Config/ColumnDef.cs`,
  `src/Analysis/DemoViewer.NET.Analysis/Output/MetricTable.cs` (projection surface)
- rendered capture: `demoviewer-uitests/stats-tab.png` (headless `StatsTabTests.PopulatedView*`)

---

## 0. TL;DR

1. The tab renders every stat as a uniform **92 px monospace cell** with a **9 px dim header** and
   no grouping, alignment, team sections, hover, zebra, or visible sort. With the shipped rule set
   that is ~55 game columns × 92 px + ~210 px identity ≈ **5,300 px wide in a ~1,200 px viewport —
   ~4.5 screens of horizontal scroll**, with K/D/A/ADR buried mid-scroll. This is the core problem.
2. **Two constraints shape every recommendation.** (a) `MetricTable.ValueColumns` are the
   **golden/export keys** — the CSV/JSON parity contract depends on them, so **display renaming is
   view-only; never rename the underlying column keys.** (b) TreeDataGrid is commercially licensed
   (AVLIC0001) and unavailable; build on `Grid` + `ListBox` + `VirtualizingStackPanel`.
3. **The `group:` metadata the task points at does not carry the taxonomy the analyst needs.** See
   §2.0 — it is dropped in projection *and* only distinguishes `game`/`round`. The rich
   Combat/Damage/Utility/Objective grouping lives in a new **app-side `ColumnCatalogue`** (§2.1),
   the single source of truth for display name, group, order, alignment, format, emphasis, tooltip.
4. **Rendering split (decided):** Match & Rounds are ≤~12 rows → **one non-virtualized `Grid` with
   real `ColumnDefinitions`** (true alignment, right-aligned numerics, sticky-left for free).
   Highlights and extra/keyed tables can be large → **virtualized `ListBox`**. `SharedSizeGroup`
   fights virtualization — that is *why* the small tables use one Grid and the large ones do not.
5. **Phase A (one day)** delivers the 80%: friendly headers, canonical order, CT/T team sections
   with score, right-aligned numerics, zebra + hover, a visible sort indicator, and a segmented
   view switch. See §5.

---

## 1. Layout & Information Architecture

### 1.1 What is wrong now (file-anchored)

- **Toolbar overcrowding.** `StatsTabView.axaml:24-96` packs 3–5 `ToggleButton`s + a "Compute
  visibility…" button + progress bar + 2 `ComboBox`es (extra-table, round) + a status `TextBlock`
  + 2 export buttons into **one horizontal `StackPanel`**. Mutually-exclusive toggles hand-wired
  through `OnIsRoundViewChanged`/`OnIsHighlightsViewChanged`/… (VM:109-225) are a segmented control
  reimplemented as loose buttons.
- **No hierarchy.** Every scoreboard cell is `Classes="statsCell"` at `FontSize=11`,
  `Foreground=TextEntityFieldVal`, `Width=92` (view:294-296). Header is `statsHead` at `FontSize=9`,
  `Foreground=TextLabel` (`#30305A`, nearly invisible on `PanelBg`). Player names, integer counts,
  ratios, and percentages are visually identical.
- **No team grouping / no score.** Rows are a flat `ItemsControl` (view:276-303) sorted by one
  column. `StatsRow` already carries `TeamLabel`/`TeamSort` (VM:688-705) but the Match view never
  groups on it. A CS2 analyst reads scoreboards as **two teams with a score**, not a flat list.
- **Numbers left-aligned.** `TextBlock.statsCell` has no `TextAlignment` — "21" and "Alice" align
  the same way, defeating fast column scanning.
- **Sort is invisible.** `_sortColumnIndex`/`_sortDescending` are **private fields**
  (VM:37-38). Nothing observable exists for a header to render ▲/▼ or highlight the active column.
- **Secondary views are bare.** Highlights is a 4-`TextBlock` row list (view:159-178); Visibility a
  hand-built table (view:183-233); extra/keyed tables render as generic **120 px** columns
  (view:110-147) with the raw column key as both header and tooltip.

### 1.2 Target IA — scoreboard-first, view switch as a segmented rail

The analyst's mental model (Leetify / CS2 end-of-match / HLTV): **scoreboard first**, two team
sections with a score, a small set of headline stats, then optional detail. Secondary surfaces
(Rounds, Weapons, Highlights, Visibility) are *sibling views*, not competing toolbar toggles.

**Chrome layout (DockPanel):**

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  [ Scoreboard ] [ Rounds ] [ Weapons ] [ Highlights ] [ Vision ]     ⌄ overflow │  ← primary view switch (segmented)
├──────────────────────────────────────────────────────────────────────────────┤
│  context bar (per view):  round ◄ 7 ►  ·  columns: [Core|All|Custom]  ·  ⧉ Export│  ← secondary, contextual
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                                │
│                          << active view content >>                             │
│                                                                                │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Primary switch = one segmented control** (styled `RadioButton` group or a real inner
  `TabControl`/`ListBox` with `IsVisible`-driven content). Replaces the loose `ToggleButton`s and
  their manual mutual-exclusion. `Weapons` is promoted from the anonymous "More tables…" combo to a
  first-class view because per-weapon kills/damage is a headline analyst want (`weapon-stats.yaml`).
- **Secondary/context bar** shows *only what the active view needs*: the round `◄ N ►` stepper for
  Rounds, the column-set preset picker for Scoreboard, the "Compute visibility…" action for Vision.
  Export moves to a single quiet `⧉ Export ⌄` split-button (CSV/JSON in its flyout) — it is not a
  primary action and should stop consuming two top-row slots.
- The extra-table `ComboBox` (arbitrary declared `outputs:`/keyed tables) stays but relocates into
  an **overflow "⌄"** on the primary rail — it is a power-user surface, not a peer of Scoreboard.

### 1.3 Scoreboard view — the main state

Two team sections, each with a section header carrying **side + round-win score**, a frozen
identity/core block, and a horizontally-scrollable grouped-detail region. Totals/average row per
team.

```
 SCOREBOARD                                          map de_mirage · 30 rounds
┌───────────────────┬────┬────┬────┬─────┬──────┬───────┐┊┌── Combat ──┬── Damage ──┬─ Utility ─ ...
│ CT  Vitality   13 │ K ▾│ D  │ A  │ ADR │ KAST%│ HLTV  │┊│ HS  FA  ...│ Dmg  Self  │ HE  Fl ...
├───────────────────┼────┼────┼────┼─────┼──────┼───────┤┊├────────────┼────────────┼──────────
│ ● ZywOo           │ 24 │ 14 │  5 │ 92.1│ 78.6 │ 1.42🟢│┊│ 11   2     │ 2810   40  │  3   6
│ ● apEX            │ 18 │ 16 │  7 │ 80.3│ 71.4 │ 1.08  │┊│  7   1     │ 2402  120  │  4   9
│  … 3 more                                             ┊
│ ─ team totals     │ 88 │ 74 │ 31 │ 84.0│ 74.2 │ 1.15  │┊│ 39  …
├───────────────────┼────┼────┼────┼─────┼──────┼───────┤┊
│ T   FaZe       11 │    │    │    │     │      │       │┊
│ ● broky           │ 21 │ 17 │  4 │ 88.5│ 69.0 │ 1.21  │┊
│  …                                                    ┊
└───────────────────┴────┴────┴────┴─────┴──────┴───────┘┊
  ▲ frozen: identity + core (never scrolls)              ▲ grouped detail (one H-scroll, header+rows together)
```

- **Frozen core block** (left, never scrolls): Player · **K · D · A · ADR · KAST% · HLTV** — the
  task's mandated headline order, app-imposed regardless of YAML union order.
- **Grouped detail region** (right, one shared horizontal scroll for its header + rows): the rest,
  clustered into collapsible/labelled group bands (§2). Collapsed by default to `Core` preset.
- **Team section header** carries side badge (CT blue / T gold) + team name + score. Score is
  derivable client-side (§1.5).
- **Per-team totals row** (sum of counts, average of rate columns) — analysts expect it.
- Sort applies within the whole board but rows stay under their team section (sort *inside* each
  team). Default sort **Kills desc** (already the VM default, VM:305-311).

### 1.4 Rounds / Weapons / Highlights / Vision

- **Rounds** — same scoreboard grid, `group:round` columns (Kills/Assists/Deaths/Damage/…), with a
  `◄ round N ►` stepper (replaces the bare `ComboBox`, view:74-79) plus a compact **round-outcome
  strip** (who won, CT/T, bomb/time/elim) if derivable; otherwise just the stepper. Same team
  sections.
- **Weapons** — promote `player_kills_by_weapon` / `player_damage_by_weapon` (keyed tables from
  `weapon-stats.yaml`, currently buried in the extra-table combo) to a first-class **matrix**:
  players as rows, weapons as columns, kills (and a damage toggle) as cells. Reuse the scoreboard
  grid with a weapon-keyed column set. This is a headline analyst view, not an "extra table."
- **Highlights** — keep the chain-satisfaction log but give it structure: group by round, a small
  **chain-type chip** (2K/3K/clutch/deagle-HS) with an accent colour, player name emphasised, tick
  as a secondary locator. Currently 4 flat `TextBlock`s (view:159-178). This is the one genuinely
  list-shaped surface → virtualized `ListBox` with a card-ish `ItemTemplate`.
- **Vision** — the current per-player LOS table (view:183-233) is fine structurally; restyle it to
  the shared scoreboard grid (team sections, right-aligned numerics, exposed%/vision% as bars). Keep
  the on-demand "Compute visibility…" gating (`CanComputeVisibility`, VM:544) — but move the button
  into the Vision view's context bar, shown only when that view is selected, with an inline
  empty/CTA state ("Compute 3-D line-of-sight for this demo →") instead of a top-toolbar button that
  is meaningless while other views are active.

### 1.5 Team score — derivable client-side (verify before trusting)

Per-player `CTWins`/`TWins` (label `CTW`/`TW`, `player-stats.yaml:573-575`) let you derive each
team's round-win total: any live player's `CTW+TW` equals their team's rounds won. Sum is not needed
— take a representative player per team, or `max` across the team to be robust to substitutions.
**Flag for the implementer:** validate against half-time side-swap and rejoining players before
asserting a number; if it doesn't reconcile cleanly, show side + name without a score rather than a
wrong score. Do **not** invent a plumbing change for this in Phase A — it is a nice-to-have on the
section header.

---

## 2. Column presentation system

### 2.0 The `group:` divergence — read this first

The task says to group columns "using the existing `group:` metadata." Two verified facts make that
literal instruction unworkable as-is, so the design diverges deliberately:

1. **`group:` is dropped in projection.** `ColumnDef` carries `RuleId`/`Label`/`Group`
   (`Config/ColumnDef.cs:7`), but `MetricTable` exposes only `ValueColumns : IReadOnlyList<string>`
   (`Output/MetricTable.cs:15-19`) — a flat list of label strings. The Stats VM consumes
   `_gameTable.ValueColumns` (VM:300-303) and never sees a group. **The runtime UI has no group
   signal at all.**
2. **The shipped taxonomy is only `game`/`round`.** Every `player-stats.yaml` column is
   `group: game`; `kast.yaml` mixes `game`/`round`. `rule-authoring.md:317` confirms this *is* the
   convention ("`round` for per-round values, `game` for match totals"). The task's parenthetical
   "(game/round/objective/utility/…)" describes an *intended* taxonomy that does not exist in the
   data. After fix #1 (Match shows only genuine match-totals) the Match view is uniformly
   `group:game` → `group:` gives **zero** intra-scoreboard sub-grouping.

**Decision:** the rich analyst grouping lives in an **app-side `ColumnCatalogue`** keyed by the
engine label (the unchanged `ValueColumns` key). It is the single source of truth for display name,
group, canonical order, alignment, numeric format, emphasis rule, and tooltip. `group:` in YAML
remains the game/round *scope* signal (which already drives the Match/Rounds split). Phase B may
plumb a richer `group:` through `MetricTable` for user-authored columns (parity-safe — `group:` is
not in the export); until then, unknown labels fall through to an `Other` group with sensible
defaults (right-align if numeric, label as display name).

### 2.1 `ColumnCatalogue` contract

New app-side type (e.g. `ViewModels/Stats/ColumnCatalogue.cs`). Keyed by the **engine label string**
(the `ValueColumns` entry). View-only — it never touches `MetricTable` keys.

```csharp
public sealed record ColumnMeta(
    string Key,          // engine label = ValueColumns entry (e.g. "TotalK") — NEVER changed
    string Display,      // friendly header (e.g. "K")
    StatGroup Group,     // rich analyst group (Rating/Combat/Damage/…)
    int Order,           // canonical position within the board
    bool Numeric,        // right-align + numeric sort
    string? Format,      // "F1"/"F2"/"P0" hint (usually already formatted by StatCell)
    Emphasis Emphasis,   // None | HeatGoodHigh | HeatGoodLow | Positive | Negative
    string Tooltip);     // full description (source: §2.4)

public enum StatGroup { Core, Rating, Combat, Damage, OpeningDuels,
                        Weapons, SpecialKills, Utility, Objectives,
                        Economy, MultiKill, RoundWins, Survival, Other }
```

`StatColumn` (VM:673) gains a resolved `ColumnMeta` (lookup by `Label`, default when absent). The
header `ItemsControl` (view:253-268) binds `Display`/`Tooltip`/`Group`/`Numeric`; cells bind
`Numeric` → `TextAlignment=Right` and `Emphasis` → foreground.

### 2.2 Full display-name + group mapping (shipped columns)

**Invariant: `Key` is the export/golden key and is never renamed. `Display` is view-only.** Canonical
order below is grouped; the **frozen core** (Player, K, D, A, ADR, KAST%, HLTV) is pinned left ahead
of everything regardless of YAML union order.

| Key (ValueColumns) | Display | Group | Align | Emphasis |
|---|---|---|---|---|
| *(player_name)* | Player | Core | L | — |
| TotalK | K | Core | R | — |
| TotalD | D | Core | R | — |
| TotalA | A | Core | R | — |
| ADR | ADR | Core | R | HeatGoodHigh |
| KAST% | KAST% | Core | R | HeatGoodHigh |
| HLTV | Rating | Core | R | HeatGoodHigh |
| KD | K/D | Rating | R | HeatGoodHigh |
| KPR | KPR | Rating | R | — |
| HS% | HS % | Rating | R | — |
| Surv% | Survival % | Rating | R | — |
| TotalHS | HS Kills | Combat | R | — |
| FlashAst | Flash Assists | Combat | R | — |
| TrdK | Trade Kills | Combat | R | — |
| TradedD | Traded Deaths | Combat | R | — |
| Clutch | Clutches Won | Combat | R | Positive |
| RapidKills | Rapid Kills | Combat | R | — |
| EnemyDmg | Enemy Dmg | Damage | R | — |
| TeamDmg | Team Dmg | Damage | R | Negative |
| SelfDmg | Self Dmg | Damage | R | Negative |
| HitFoe | Shots Hit (foe) | Damage | R | — |
| HitTeam | Shots Hit (team) | Damage | R | Negative |
| Shots | Shots Fired | Damage | R | — |
| AvgHP→Dmg | Avg HP @ Dmg | Damage | R | — |
| TotalFK | Opening Kills | OpeningDuels | R | Positive |
| TotalFD | Opening Deaths | OpeningDuels | R | Negative |
| FK± | Opening +/- | OpeningDuels | R | HeatGoodHigh |
| Duel% | Opening Duel % | OpeningDuels | R | HeatGoodHigh |
| CTFK | CT Open Kills | OpeningDuels | R | — |
| CTFD | CT Open Deaths | OpeningDuels | R | — |
| TFK | T Open Kills | OpeningDuels | R | — |
| TFD | T Open Deaths | OpeningDuels | R | — |
| AWP | AWP Kills | Weapons | R | — |
| Pistol | Pistol Kills | Weapons | R | — |
| Rifle | Rifle Kills | Weapons | R | — |
| SMG | SMG Kills | Weapons | R | — |
| Knife | Knife Kills | Weapons | R | — |
| DeagleHSRnds | Deagle-HS Rounds | Weapons | R | — |
| NoScope | No-scope Kills | SpecialKills | R | — |
| WB | Wallbang Kills | SpecialKills | R | — |
| Smoke | Smoke Kills | SpecialKills | R | — |
| Blind | Blind Kills | SpecialKills | R | — |
| Revenge | Revenge Kills | SpecialKills | R | — |
| FlashK | Flash Kills | SpecialKills | R | — |
| HE | HE Thrown | Utility | R | — |
| Flash | Flashes Thrown | Utility | R | — |
| Smokes | Smokes Thrown | Utility | R | — |
| Molly | Molotovs Thrown | Utility | R | — |
| EFlash | Enemies Flashed | Utility | R | — |
| AvgBlind | Avg Blind (s) | Utility | R | — |
| Plants | Bomb Plants | Objectives | R | — |
| Defuses | Bomb Defuses | Objectives | R | — |
| Equip | Avg Equip $ | Economy | R | — |
| Armor | Armor Rounds | Economy | R | — |
| 2K | 2K | MultiKill | R | — |
| 3K | 3K | MultiKill | R | Positive |
| 4K | 4K | MultiKill | R | Positive |
| 5K | Ace | MultiKill | R | Positive |
| CTW | CT Wins | RoundWins | R | — |
| CTL | CT Losses | RoundWins | R | — |
| TW | T Wins | RoundWins | R | — |
| TL | T Losses | RoundWins | R | — |
| Survived | Rounds Survived | Survival | R | — |

**Rounds view (`group:round`) labels** (same treatment, subset): Kills→K, Assists→A, Deaths→D,
Traded→Traded, Damage→Dmg, UtilDmg→Util Dmg, HSKills→HS, FlashAst→Flash Ast, NoScope, WB→Wallbang,
Smoke, Shots, EKills→Enemy K, Flashed→Enemies Flashed, HasKAST→KAST (✓), FK→Opening K, FD→Opening D,
DeagleHS→Deagle HS. Group these as Combat/Damage/Utility identically.

### 2.3 Width, alignment, grouping, emphasis

- **Width strategy — tiered, not uniform 92 px.** Identity `*`-ish min 160 px; core numeric columns
  `Auto` with a sensible min (48–64 px); rate columns (ADR/KAST%/HLTV) 64–72 px; wide-value columns
  (Enemy Dmg, Avg Equip $) `Auto`. In a real `Grid` (§3) `Auto` measures to content — no more
  truncated headers or oversized integer cells. Reserve fixed widths only for the virtualized tables
  where `Auto` can't cross rows.
- **Numeric right-alignment.** `Numeric` columns set `TextAlignment=Right` (both header and cell) and
  a fixed-tabular monospace already helps; keep the monospace for numbers, but use the **UI font for
  the Player identity column** so names read naturally.
- **Group bands.** In the detail region, render each `StatGroup` as a labelled band: a thin group
  header row (`Combat`, `Damage`, …) spanning its columns, with the band **collapsible** (toggle
  hides the group's columns). Ship two presets: **Core** (frozen block only + Rating) and **All**.
  A `Custom` preset persists the user's collapsed-group set (§4). This is how you tame 55 columns
  without hiding data.
- **Emphasis / heat — sparingly.** Apply a 3-stop heat scale to **rating columns only**
  (HLTV/ADR/KAST%/FK±/Duel%), not every cell (avoid a rainbow scoreboard). `HeatGoodHigh` maps the
  column's per-match min→max to Negative→Neutral→Positive. `Positive`/`Negative` are flat accents for
  intrinsically good/bad columns (clutches, self/team dmg). Palette gap: DarkPalette has
  `AccentError` (`#E53935`, red) and `AccentAmber` (`#FFC107`) but **no green** — add one semantic
  token per the palette's naming policy:

  ```xml
  <SolidColorBrush x:Key="StatPositive" Color="#4CAF50" />  <!-- matches the depth-ramp depth-2 green -->
  <!-- StatNegative → reuse AccentError; StatNeutral → reuse TextMid -->
  ```

---

## 3. Avalonia implementation notes (no TreeDataGrid)

Shapes, not tutorials — the implementer is competent.

### 3.1 Match / Rounds — one real `Grid`, not the 92 px hack

Match & Rounds are **≤~12 rows** → do **not** virtualize; use a single `Grid` with real
`ColumnDefinitions` so header and every row share one column vector (true alignment, `Auto` sizing,
right-align per column). Two ways to share the column vector across header + rows:

- **Preferred:** put the header row and all data rows in **one `Grid`** (rows = `Auto` each). One set
  of `ColumnDefinitions`; team-section headers and totals are just extra rows with `ColumnSpan`.
  Because it's one Grid, columns auto-size together with zero `SharedSizeGroup` cost.
- **Alternative:** separate header Grid + per-row Grids joined by
  `Grid.IsSharedSizeScope` + `SharedSizeGroup` on each column. Works at ~12 rows; **avoid at scale**
  (it defeats virtualization and re-measures globally).

Bind the column set dynamically: the column count follows the loaded rules. Since XAML
`ColumnDefinitions` aren't trivially `ItemsSource`-bound, either (a) build the `Grid`
`ColumnDefinitions` + children in a small code-behind/attached-behaviour from `Columns`, or (b) keep
the `ItemsControl`-of-cells shape but drive each cell's width from `ColumnMeta` and switch the panel
to a `Grid`-backed shared-size scope. (a) is cleaner for the frozen-core block (a known, fixed 7
columns) + (b) for the dynamic detail region.

### 3.2 Sticky first column / frozen core

Split the board into **two regions inside a `DockPanel`/`Grid`**:

- **Left frozen region** — identity + core (Player·K·D·A·ADR·KAST%·HLTV). Never in a horizontal
  `ScrollViewer`; always visible.
- **Right detail region** — grouped columns inside **one horizontal `ScrollViewer`** that contains
  *both* its header and its rows (exactly the current whole-table trick at view:236-306, but scoped
  to the detail region only). Header and rows scroll together because they share the ScrollViewer.

Vertical scrolling: both regions share one vertical `ScrollViewer` (or sync offsets). At ≤12 rows
there's rarely vertical overflow, so a single outer vertical `ScrollViewer` wrapping both regions is
simplest.

### 3.3 Highlights / Weapons / extra tables — virtualized `ListBox`

Follow the **EntityListView** house pattern (`EntityListView.axaml:31-67`): `ListBox` +
`VirtualizingStackPanel` + a header `Grid` above with matching fixed `ColumnDefinitions`
(`52,*,72,64,72` there). For these potentially-large surfaces, fixed/tiered column widths are
required (can't `Auto`-measure across virtualized realizations) — but tiered, not uniform.

### 3.4 Copy / selection

Swap scoreboard cell `TextBlock` → **`SelectableTextBlock`** for copy-cell out of the box. Add a
row `ContextMenu` with **Copy row (TSV)** and **Copy row as CSV** (build from `StatsRow.Cells`,
tab/comma-joined) — cheap and expected of a stats surface.

### 3.5 State the tab already exposes / must add

- Sort state must become **observable** (see §4).
- `IsMatchView`/`IsRoundView`/… (VM:105-225) stay as the view switch's backing — but the manual
  cross-clearing (setting the other three false in every handler) should collapse into a single
  `enum StatsView { Scoreboard, Rounds, Weapons, Highlights, Vision, Extra }` observable property.
  Cleaner, fewer `OnPropertyChanged` fan-outs, and it binds directly to the segmented control.

---

## 4. Interaction polish

| Item | Shape | Notes |
|---|---|---|
| **Sort indicator** | `▲`/`▼` glyph + accent on the active header; dim others | Needs VM change: expose `SortColumnKey` + `SortDescending` as `[ObservableProperty]` (today private, VM:37-38). Header template binds a converter → glyph + `Foreground=AccentInteractive`. |
| **Hover row** | `ListBoxItem:pointerover` / `Grid` row `:pointerover` bg = `PanelHeaderHover` | Match EntityListView's selected-row style (`PanelHeaderHoverDeep`, EntityListView:63-65). |
| **Zebra** | Alternating row bg via index parity → `PanelBg` / `#0E0E1E` | Subtle; helps horizontal scanning across many columns. |
| **Copy cell / row** | `SelectableTextBlock` + row `ContextMenu` (§3.4) | TSV + CSV. |
| **Tooltips** | Per-column `ToolTip.Tip = ColumnMeta.Tooltip` (full description) | Not the label echoed as tooltip (current view:265). Source §2.4. |
| **Empty / loading** | Distinct states, not one status string | `StatusMessage` (VM:98) currently conflates "load a demo", "no stats", "no collision bake", "computing". Split into: **No demo** (CTA: open a demo), **Computing** (spinner + "Analysing…"), **No stats** (explain), **Vision not computed** (inline CTA). |
| **Export placement** | One `⧉ Export ⌄` split-button in the context bar; CSV/JSON in flyout | Frees two top-row slots (view:86-95); export is secondary. Keep the folder-picker code-behind (`StatsTabView.axaml.cs:29`) — it needs the `TopLevel` (WASM: folder-picker degrades; guard as elsewhere). |
| **Column presets** | `Core` / `All` / `Custom` toggle in context bar | `Custom` persists collapsed-group set + sort. |

### 4.1 Tooltip source

Per-stat descriptions come from the rules themselves and (historically) the retired v1 guide `docs/rule-authoring.md` (git history):
- Chain/rule `description:` fields and the inline YAML comments (e.g. the HLTV formula note
  `player-stats.yaml:358-363`, the AvgHealthWhileDamaging note :57-63).
- `rule-authoring.md` Appendix A (game-event catalogue) and Appendix D (`enrich.*` values,
  e.g. `capped_damage` = "damage capped at remaining HP — use for ADR", :674) explain what feeds
  ADR/Damage/KAST.
Author the `Tooltip` strings once, inline in `ColumnCatalogue` (they are display copy, not data).

---

## 5. Phased rollout (S / M / L)

### Phase A — highest-impact restyle (target: one day)

The 80% of perceived quality. No engine/parity changes; app-only.

| # | Item | Size |
|---|---|---|
| A1 | `ColumnCatalogue` type + full shipped mapping (§2.1-2.2) — display names, group, order, align, emphasis, tooltip | **M** |
| A2 | Canonical column order + friendly headers wired into `Columns`/header template (replaces raw label + label-as-tooltip) | **S** |
| A3 | Right-align numerics; UI font for Player col; tiered widths (drop uniform 92 px) | **S** |
| A4 | **CT/T team sections** with side badge + derived score + per-team totals row (§1.3, §1.5) | **M** |
| A5 | Visible **sort indicator** — expose `SortColumnKey`/`SortDescending` observable, header glyph + highlight | **S** |
| A6 | Zebra + hover row styling (§4) | **S** |
| A7 | Replace loose `ToggleButton`s with a **segmented view switch** + single `StatsView` enum; move round stepper/export/visibility into a contextual second bar | **M** |
| A8 | Split empty/loading/no-bake states (§4) | **S** |

### Phase B — structural

| # | Item | Size |
|---|---|---|
| ~~B1~~ | ~~**Frozen core + horizontally-scrolling grouped detail** region (§3.2); collapsible group bands~~ **— superseded by §7 (category sub-tabs).** The round-2 verdict rejected the one-wide-scroll grouped-detail model. Category filtering removes the wide horizontal scroll, so the sticky-core split and collapsible bands are no longer needed. The frozen-core *concept* survives only as an **optional** fallback for the rare category (Core+OpeningDuels ≈ 14 cols) that grazes the viewport — see §7.6. | ~~L~~ **cut** |
| ~~B2~~ | ~~Column **presets** (Core/All/Custom) with persisted collapsed-group set + sort~~ **— superseded by §7.** The category chips *are* the preset mechanism (each chip = Core anchor + one group). "All" (every column at once) is deliberately dropped — it is the exact thing round 2 rejected. "Custom / persisted collapsed set" collapses to a single persisted `SelectedCategory` (§7.5). | ~~M~~ **cut** |
| B3 | **Weapons** view — promote keyed weapon tables to a player×weapon matrix (§1.4) | **M** |
| B4 | Rounds view: round `◄ N ►` stepper + round-outcome strip; reuse scoreboard grid for `group:round` cols | **M** |
| B5 | Vision view restyled to the shared scoreboard grid + inline compute CTA (§1.4) | **M** |
| B6 | Copy cell / row (`SelectableTextBlock` + `ContextMenu`) | **S** |
| B7 | *(optional, engine-side)* plumb `ColumnDef.Group` through `MetricTable` (parity-safe) so **user-authored** columns self-group; app catalogue stays the override for shipped columns | **M** |

> **Round-2 amendment:** B1 and B2 above are superseded. The column-taming mechanism is now **§7
> (category sub-tabs)**, not frozen-core + one-wide-horizontal-scroll. The `StatGroup` taxonomy the
> old B-items assumed (already shipped in `ColumnCatalogue`) is reused verbatim — only the *surfacing*
> changes. §7 carries its own S/M/L checklist that replaces the B1/B2 rows.

### Phase C — polish

| # | Item | Size |
|---|---|---|
| C1 | Heat scale on rating columns (min→max, `StatPositive` token) (§2.3) | **S** |
| C2 | Highlights view restyle — round grouping + chain-type chips (§1.4) | **M** |
| C3 | Per-team MVP / round-impact accents; sparkline of per-round HLTV in the identity cell | **M** |
| C4 | Keyboard: sort with arrows, `[`/`]` to step rounds, `Ctrl+C` copy row | **S** |
| C5 | Column drag-reorder within a group (persisted) | **L** |

---

## 6. What must NOT change

- **`MetricTable.ValueColumns` keys** — export/golden parity contract. Display renaming is a
  view-only lookup (`ColumnCatalogue.Display`); the underlying key is untouched.
- **Export data path** — `ExportTo` (VM:398) writes the same `MetricTable`s the bench does; leave the
  data, only re-place the affordance.
- **No TreeDataGrid** (AVLIC0001) — everything above is `Grid` + `ListBox` + `VirtualizingStackPanel`.
- **Protected parser files** — none touched; this is App-project + optional non-protected
  Analysis-projection plumbing (B7 only).
- **WASM** — folder-picker export already degrades via `TopLevel`; no new filesystem/native deps are
  introduced.

---

## 7. Category layout (round 2)

The round-2 verdict, after living with Phase A: far too many columns in the stats tables. Break the
table into categories and either have sub-tabs, a dropdown, or multiple sections we can scroll
(vertically) to view the distinct groups. This section supersedes Phase-B B1/B2 (frozen-core +
one-wide-horizontal scroll). Phase A already shipped the `ColumnCatalogue` `StatGroup` taxonomy (Core, Rating, Combat,
Damage, OpeningDuels, Weapons, SpecialKills, Utility, Objectives, Economy, MultiKill, RoundWins,
Survival, Other) with a per-column group assignment for every shipped label; §7 only changes how
those groups reach the screen. No new taxonomy, no engine change, no parity change.

### 7.1 Honest evaluation of the three mechanisms

The scoreboard's fundamental shape is **players-as-rows, stats-as-columns, comparison down a
column** — this is how Leetify, the CS2 end-of-match panel, and HLTV all read. All three options
keep players-as-rows; they differ only in how the ~13 column groups are exposed. The two analyst
workflows to weigh:

- **Compare players within a category** — "who has the best opening duels / utility / trades" — read
  one group's columns *down* across all players. This is the **dominant** analyst action.
- **Scan one player across categories** — "ZywOo's full profile" — read one player *across* all
  groups. Secondary; genuinely a drill-down need.

| Mechanism | Compare within a category | Scan one player across | Team sections + totals | Per-category sort | H-scroll | Discoverability | Machinery reuse |
|---|---|---|---|---|---|---|---|
| **(a) Category sub-tabs** (chip rail) | ★★★ one glance, no scroll | ★★ click through chips | native — same grid, filtered cols | clean (sort the visible set) | eliminated | high — chips always visible | high — rebuild cols+rows to visible set; row template untouched |
| **(b) Category dropdown** | ★★★ | ★★ | native | clean | eliminated | **medium** — categories hidden behind a click | high (same as a) |
| **(c) Vertical stacked sections** | ★★ (per mini-table) | ★★★ scroll down the page | **repeated ×13** (each section re-emits CT/T + totals + identity) | **ambiguous / effectively none** | eliminated | high | medium — 13× section build; long page |

**Option (c) taken seriously (it is the most detailed of the three suggestions).** Its real strength is the
*scan-one-player* workflow: stack one small table per group vertically, each = identity columns +
that group's stat columns, and a player's whole profile unfolds as you scroll — no horizontal scroll
anywhere. To stay coherent it **must** use one *global fixed row order* (team → name, no per-section
sort) so a given player sits in the same row in every section down the page; the moment you allow
per-section sort, the rows stop lining up and the scan-down benefit evaporates. That is exactly the
cost: (c) **sacrifices per-section column sort** (the dominant comparison action degrades to "eyeball
the mini-table"), **repeats CT/T headers + totals + the identity column ~13 times** (a ~150-row page
for a 10-player match), and optimizes the *rarer* workflow. The one-player-profile need is better
met by a dedicated **player drill-down card** (click a row → full stat line), not by reshaping the
whole scoreboard into 13 stacked mini-scoreboards. So (c) is *considered and set aside*, not
dismissed — and its scan-down value is redirected to a future drill-down (§7.9, Phase C candidate).

**Option (b)** is mechanically identical to (a) — same visible-column model, same sort, same team
sections — but hides the category set behind a combo. At ~13 groups a dropdown is strictly worse for
discoverability and costs an extra click per switch; dropdowns earn their keep at 25+ options, not
13. It is the **runner-up**, and it is not wasted: it returns as the **overflow affordance** for (a)
when the chip rail is wider than the viewport (§7.5).

### 7.2 Decision

**Primary: (a) category sub-tabs — a second-level chip rail under the view rail.** It wins the
dominant *compare-within-a-category* workflow outright (one glance, all players, zero horizontal
scroll, fully sortable), preserves the scoreboard's team-section/totals structure natively (it is the
**same grid with a filtered column set**), and is the smallest change to the shipped machinery.
**Runner-up: (b) dropdown**, reused as the chip-rail overflow. **Relegated: (c) vertical stack** →
future player drill-down card.

### 7.3 Group → chip mapping (strict 1:1, no special case)

One chip per `StatGroup` present in the active view's column set, in catalogue group order, **Core
first**. The visible column set for the selected chip is always:

```
visibleColumns(selectedGroup)  =  { columns where Meta.Group == Core }        ← the anchor, always
                               ∪  { columns where Meta.Group == selectedGroup } ← swaps with the chip
```

- The **Core chip** (labelled **"Overview"**) resolves to `Core ∪ Core = Core` — anchor only. No
  folding, no special case: every chip is `Core ∪ <its group>`; Overview is just the degenerate case.
- **Rating is its own chip** (not folded into Overview) — keeps the 1:1 rule clean. Overview =
  K·D·A·ADR·KAST%·Rating(HLTV); Rating chip adds K/D·KPR·HS%·Survival%.
- The **Core anchor** is exactly the six `StatGroup.Core` columns, so the mandated
  K·D·A·ADR·KAST%·Rating identity block is present in **every** category context for free — it falls
  out of the set union, not a hard-coded prefix.

**Chips available per view are derived from that view's columns** (their sets differ):

- **Scoreboard (Match):** Overview · Rating · Combat · Damage · Opening · Weapons · Special ·
  Utility · Objectives · Economy · Multi-Kill · Round Wins · Survival  (≈13).
- **Rounds:** Overview · Combat · Damage · Opening · Weapons · Utility  (≈6 — the round table only
  emits `group:round` columns spanning those groups; Rating/Economy/etc. simply produce no chip).

Compute `Categories` as the distinct `Meta.Group` of `CurrentColumns`, ordered by group, Core forced
first. Empty groups produce no chip automatically — the rail self-sizes to each view.

### 7.4 Wireframe (Scoreboard, "Utility" chip active)

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│ [ Scoreboard ] [ Rounds ] [ Highlights ] [ Vision ]                     More ⌄    │ row 0  view rail
├─────────────────────────────────────────────────────────────────────────────────┤
│  (context bar — export right; round stepper / vision CTA / status left)           │ row 1
├─────────────────────────────────────────────────────────────────────────────────┤
│ ‹ Overview │ Rating │ Combat │ Damage │ Opening │ Weapons │[Utility]│ Objectives …›│ row 2  CATEGORY SUB-RAIL ← NEW
├─────────────────────────────────────────────────────────────────────────────────┤
│   ← identity + Core anchor (always) →           ← Utility columns (swap w/ chip) → │ row 3  content
│ ┌───────────────────┬───┬───┬───┬─────┬──────┬──────┐┌── Utility ─────────────────┐│
│ │ Player      (sort)│ K▾│ D │ A │ ADR │KAST% │Rating││ HE  Flash Smoke Molly  EFl…││
│ ├───────────────────┼───┼───┼───┼─────┼──────┼──────┤├────────────────────────────┤│
│ │ CT  Vitality  13  │   │   │   │     │      │      ││                            ││  ← team header + score survive
│ │ ● ZywOo           │24 │14 │ 5 │92.1 │ 78.6 │ 1.42 ││  3    6    2    1    14 …  ││
│ │ ● apEX            │18 │16 │ 7 │80.3 │ 71.4 │ 1.08 ││  4    9    1    2    11 …  ││
│ │ ─ team totals     │88 │74 │31 │84.0 │ 74.2 │ 1.15 ││ 14   27    6    5    52 …  ││  ← totals survive (over visible cols)
│ ├───────────────────┼───┼───┼───┼─────┼──────┼──────┤├────────────────────────────┤│
│ │ T   FaZe      11  │   │   │   │     │      │      ││                            ││
│ │ ● broky           │21 │17 │ 4 │88.5 │ 69.0 │ 1.21 ││ …                          ││
│ └───────────────────┴───┴───┴───┴─────┴──────┴──────┘└────────────────────────────┘│
│    ▲ Core anchor — identical in every category         ▲ this block swaps per chip │
└─────────────────────────────────────────────────────────────────────────────────┘
```

Switching chips changes **only the right block**. Team sections, the derived score in each team
header, the per-team totals row, the sort glyph, zebra/hover — all unchanged.

### 7.5 Interaction spec

**Selection model.**
- New `[ObservableProperty] StatGroup _selectedCategory = StatGroup.Core;` on `StatsTabViewModel`.
- New `IReadOnlyList<CategoryChip> Categories` (record `CategoryChip(StatGroup Group, string Label,
  bool IsSelected)`), rebuilt whenever `CurrentColumns` changes (i.e. on `Update` and on Match↔Rounds
  switch). Chip `IsSelected = Group == SelectedCategory`.
- `OnSelectedCategoryChanged` → recompute the visible column order (§7.3) and call the existing
  `RebuildGameRows()` / `RebuildRoundRows()`. No new render path — the rebuild is the same one sort
  already triggers.
- Chip control = a styled `RadioButton` in a `GroupName` (single-select, checked-state visual for
  free), reusing the `ToggleButton.statsview` look — a `statscat` class one weight lighter/smaller so
  the sub-rail reads as secondary to the view rail. Bind `IsChecked` to `IsSelected` (one-way) +
  `Command`/`CommandParameter` to set `SelectedCategory`.

**Core anchor.** Always the six `StatGroup.Core` columns (§7.3). Never filtered out, in either view.

**Sort — per category, per view.**
- **Scoreboard (Match): sortable within the visible set.** Sort must become **key-based, not
  index-based** (see §7.6 — this is the one non-obvious refactor). On a chip switch: if the current
  sort key is still in the visible set, **keep it**; else fall back to the view default (**Kills /
  `TotalK` descending**, else player name). Because `TotalK` is a Core column it is in the visible
  prefix of *every* category, so the default sort survives every switch for free, and any Core-column
  sort (K/D/A/ADR/KAST%/Rating) persists across all chips. Sorting a category-specific column (e.g.
  `Duel%` under Opening) then leaving that chip reverts to the default — deterministic, no stale key.
  Rows still sort globally but stay grouped under their team section (unchanged from today).
- **Rounds: no column sort** (as today — fixed team → name order). Chips only *filter* columns; the
  round browser keeps its stable order so the `◄ N ►` stepper reads consistently. (Enabling round
  sort is a later, separate item.)

**State retention.**
- `SelectedCategory` **persists across Match↔Rounds** *iff* the group exists in the target view's
  chip set; otherwise reset to `Core` (Overview). E.g. sitting on Economy in Match then switching to
  Rounds (no Economy chip) lands on Overview; switching back does **not** auto-restore Economy —
  keep it simple, land on the last valid selection.
- `SelectedCategory` **persists across leaving/returning the Stats tab** (the VM instance lives).
- Default on first load / new evaluation: `Overview` (Core), Kills-desc sort — matches today's landing.

**Overflow (runner-up (b), reused).** P0: render all chips in a horizontally-scrolling
`StackPanel` inside a thin `ScrollViewer` (`HorizontalScrollBarVisibility="Auto"`) — 13 chips graze
but rarely exceed the viewport, and gentle scroll of a thin rail is acceptable. Optional (M): a
trailing **"⌄ More"** `MenuFlyout` holding the less-scanned tail groups (Economy, Round Wins,
Survival, Special Kills, Objectives); selecting one sets `SelectedCategory` exactly like a chip, and
if the active category lives in the overflow the "More" button shows its name highlighted. Ship P0
first; add the flyout only if the rail feels cramped in the running app.

**Sub-rail visibility.** The category sub-rail (row 2) shows **only for the two stat-table views**
(`IsTableView` — Match/Rounds). Highlights, Vision, and the Extra-table view have their own,
non-catalogued column shapes and get **no** category rail (they already fit / are list-shaped).

### 7.6 Why this reuses the existing machinery (and the one refactor it needs)

**Rows and columns are both rebuilt to the visible set, so the row template is unchanged.** Today
`RebuildGameRows`/`BuildRow`/`BuildStatColumns`/`BuildTotalsRow` all iterate a *column-order list*
(`_gameColumnOrder`). Point them at a **filtered** order — `_visibleGameColumnOrder = Core keys +
SelectedCategory keys, in catalogue order` — and:

- `BuildStatColumns` emits headers only for visible columns (re-based indices 0..n).
- `BuildRow` emits `Cells` only for visible columns, in the same order.
- Header `ItemsControl{CurrentColumns}` and row `ItemsControl{Cells}` stay parallel; sort's
  `a.Cells[col].Raw` indexes the visible list consistently.
- `BuildTotalsRow` sums/averages over the visible columns — correct (totals of what's shown).

The `StatsRowTemplate` (view:15-48), the header template (view:320-346), zebra/hover/totals styles,
team-section `ItemsControl` (view:356-372) — **all untouched.** The only new markup is the sub-rail
itself.

**The load-bearing refactor — key-based sort (must, not should).** Today `_sortColumnIndex` (VM:37)
is an *int index* into the full column/cell list, and `SortByColumn` compares `a.Cells[col].Raw`
(VM:388-398, 467-474). The instant rows+columns are re-based to a *per-category subset*, a stored
index is meaningless across chip switches (index 3 in Utility ≠ index 3 in Combat). Replace it with a
**sort key**:

- `private string? _sortKey;` (`null` = player-name sort), replacing `_sortColumnIndex` + the
  `-1`/`-2` sentinels.
- Resolve `_sortKey` → the current visible index *at build time* inside `RebuildGameRows`
  (`_visibleGameColumnOrder.IndexOf(_sortKey)`; `< 0` ⇒ apply the fallback and reset `_sortKey`).
- `StatColumn.IsSorted` becomes `Label == _sortKey` (drop the passed-in `sortIndex`); the glyph logic
  is otherwise unchanged.
- `SortByColumn(StatColumn c)` sets `_sortKey = c.Label` (flip direction if already the key);
  `SortByPlayer` sets `_sortKey = null`.

This is the change an implementer will otherwise get subtly wrong; it is what makes "keep sort if the
key is still visible, else fall back" actually work.

**Team score decoupling (must).** The derived round-win score in each team header currently reads
`Cells[ctwIdx]`/`Cells[twIdx]` (VM:491-517). `CTW`/`TW` are `RoundWins`-group columns, so under any
non-Round-Wins chip they are **not in the visible cells** and the score would silently vanish —
violating "team sections + totals must survive." Fix: **precompute the per-team score once in
`Update`** from the full table (`MetricRow.Values["CTW"]` + `["TW"]` per player → distinct per team →
score only when unanimous, same logic as today) into a `Dictionary<int teamSort, int? score>`, and
have `BuildTeamSections` consume that map instead of reading visible cells. Score then survives every
category. (Per-team *totals* need no such fix — totals over the visible columns are exactly what
should show.)

**Horizontal scroll / frozen-core is now optional.** Category filtering caps the widest case at Core
(6) + OpeningDuels (8) = 14 columns ≈ 1,184 px — it may graze a ~1,200 px viewport but never the
4.5-screen scroll that motivated old B1. Keep the **existing single horizontal `ScrollViewer`**
(view:298-301) as-is; it bounds the rare graze harmlessly. The frozen sticky-core split from old B1
is **demoted to optional / not-P0** — only revisit it if a wide category feels cramped in-app.

### 7.7 Match vs Rounds — the mechanism works for both

The mechanism is column-set-agnostic: it filters whatever `CurrentColumns` holds. Both views already
own their own ordered column list (`_gameColumnOrder` / `_roundColumnOrder`, VM:342-344) and both go
through the same `BuildStatColumns`/`BuildRow`. Concretely:

- **Match** gets the full chip set (§7.3), sortable columns, CT/T team sections + totals + derived
  score, all preserved under filtering.
- **Rounds** gets its ~6-chip subset (auto-derived), keeps its flat team→name order and the `◄ N ►`
  round stepper, and filters columns identically. The round Core anchor is whatever the round table
  marks `StatGroup.Core` (Kills→K, Deaths→D, Assists→A, HasKAST→KAST) — the union rule yields the
  right per-view anchor with no code branch.

### 7.8 Chip label copy

Use short rail labels (the tooltip carries the full group meaning): **Overview · Rating · Combat ·
Damage · Opening · Weapons · Special · Utility · Objectives · Economy · Multi-Kill · Round Wins ·
Survival**. Map `StatGroup` → label in one small switch next to the catalogue (display copy, not
data). `OpeningDuels`→"Opening", `SpecialKills`→"Special", `MultiKill`→"Multi-Kill",
`RoundWins`→"Round Wins", `Core`→"Overview".

### 7.9 What gets cut / relegated

- **Cut:** the "All columns at once" preset (old B2) — it is the exact overload round 2 rejected.
- **Cut:** collapsible group bands + sticky-core two-region split (old B1) — unnecessary once one
  category shows at a time; the per-column `GroupLabel` band (VM:854, view:335-336) becomes redundant
  with the chip and should be **suppressed in category mode** (the chip already names the group).
- **Relegated to a future player drill-down (Phase C candidate):** the *scan-one-player-across-
  categories* workflow that option (c) optimized — a click-a-row → full stat-line card, which serves
  that need far better than 13 stacked mini-scoreboards.

### 7.10 Implementation checklist (supersedes B1/B2)

| # | Item | Size |
|---|---|---|
| §7-1 | **Key-based sort refactor** — replace `_sortColumnIndex:int` with `_sortKey:string?`; resolve key→visible-index at build; `StatColumn.IsSorted = Label == _sortKey`; update `SortByColumn`/`SortByPlayer`. *Do this first — everything else depends on it.* (§7.6) | **M** |
| §7-2 | `SelectedCategory` `[ObservableProperty]` (default `Core`) + `OnSelectedCategoryChanged` → recompute visible order + `RebuildGameRows`/`RebuildRoundRows` | **S** |
| §7-3 | Compute `_visibleGameColumnOrder` / `_visibleRoundColumnOrder = Core keys ∪ SelectedCategory keys` (catalogue order) from the full order; point `BuildStatColumns`/`BuildRow`/`BuildTotalsRow` at it | **S** |
| §7-4 | `Categories` chip list = distinct `Meta.Group` of `CurrentColumns`, Core first; `CategoryChip` record + `StatGroup`→label switch (§7.8); rebuild on `Update` and Match↔Rounds switch | **S** |
| §7-5 | **Team-score decoupling** — precompute `Dictionary<int,int?>` score map in `Update` from full `MetricRow.Values["CTW"/"TW"]`; `BuildTeamSections` consumes it, not `Cells[ctwIdx]` (§7.6) | **S** |
| §7-6 | Sort-key survival on chip switch — keep if visible, else fall back to `TotalK` desc / player (§7.5) | **S** |
| §7-7 | XAML: add category sub-rail as a new grid row (`RowDefinitions="Auto,Auto,Auto,*"`, content → row 3); horizontally-scrolling `RadioButton` chips bound to `Categories`; `IsVisible="{Binding IsTableView}"`; `statscat` style class | **S** |
| §7-8 | `SelectedCategory` persistence + fallback-to-Overview when the group is absent in the target view (§7.5) | **S** |
| §7-9 | Suppress the per-column `GroupLabel` band in category mode (redundant with the chip) (§7.9) | **S** |
| §7-10 | *(optional)* "⌄ More" overflow `MenuFlyout` for tail groups (runner-up (b) reused) (§7.5) | **M** |

Total ≈ one focused day. No engine, no parity, no protected-file, no new dependency, no TreeDataGrid
change. Everything lands in `StatsTabViewModel.cs` + `StatsTabView.axaml` (+ a `StatGroup`→label
helper).
