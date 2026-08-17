# Player Details — Dashboard UX Design

The Player Details dashboard shipped on main (`PlayerDetailsViewModel` / `PlayerDetailsView` +
tests). Some later polish items remain open/unverified (heat accents, the AchievementChip retrofit,
the cumulative-rating stretch). The brief, kept verbatim because it shaped the scope: *"a player
stats overview page — a more robust multi-panel dashboard that provides rich individual player
performance statistics. Single page (tabbed sections allowed). Richer details about that individual
player's performance."* It opens from the Stats tab when a player's name is double-clicked, or
right-click → **Details**.

This is the promotion of `docs/ui/stats-tab-ux-review.md` §7.9's *player drill-down card* — the
"scan-one-player-across-categories" workflow that the scoreboard deliberately does **not** optimise —
into a full page. It reads the **same `MetricTable`s the Stats tab already holds** (the export/golden
surface), filtered to one `player_slot`. No engine change, no new projector, no parity change, no
TreeDataGrid, no new dependency.

Grounded in:
- `src/App/DemoViewer.NET/ViewModels/Stats/StatsTabViewModel.cs` — holds every table (`_gameTable`,
  `_roundTable`, `_eventsTable`, `_visibilityPlayersTable`, `_visibilityPairsTable`, `_extraTables`)
  and the `StatsRow` / `StatCell` / `ColumnCatalogue` machinery this page reuses.
- `src/App/DemoViewer.NET/ViewModels/Stats/ColumnCatalogue.cs` — the display-name/group/format
  metadata, reused verbatim.
- `src/App/DemoViewer.NET/Views/Stats/StatsTabView.axaml` — the hand-rolled columnar patterns, chip
  pills (`statscat`), team-accent styles (`teamHeader`), and cell styles this page extends.
- Projector schemas (the authoritative column keys): `PlayerGameStatsProjector`,
  `PlayerRoundStatsProjector`, `RuleChainEventProjector`, `KeyedStatsProjector`,
  `VisibilityStatsProjector` under `src/Analysis/DemoViewer.NET.Analysis/Output/`.
- `src/App/DemoViewer.NET/Styles/DarkPalette.axaml` — the single-source palette.
- `rules/player-stats.yaml` (L358 — the HLTV formula), `rules/weapon-stats.yaml`.
- Rendered captures: `real-scoreboard.png`, `real-rounds.png` (the current visual language).

---

## 0. TL;DR

1. **It is an inline overlay inside the Stats tab, not a separate `Window`.** The WASM host has no
   OS windows — `BrowserWindowService.OpenParseChainInspector` is a deliberate no-op — so a real
   `Window` would break the browser build. The overlay spans the **entire `StatsTabView` root**,
   preserves the parent's state (sort/category untouched underneath), and is constructed by
   `StatsTabViewModel` from the tables it already holds. This is a *correctness* decision, not a taste
   one.
2. **The linchpin change is `player_slot` on `StatsRow`.** `player_slot` is the join key every table
   shares (`player_game_stats`, `player_round_stats`, `rule_chain_events`, `player_*_by_weapon`,
   `player_visibility_stats`, `visibility_pairs`). `StatsRow` currently drops it. Add it (P0) — names
   collide, slots don't.
3. **The dashboard is the landing surface.** A sticky **identity header + core stat strip**, then a
   default **Overview** page showing the good panels at once (per-round form timeline, weapon
   breakdown, opening duels, this-player achievements). Two genuinely dense surfaces get their own
   sub-sections: **Rounds** (round-by-round table) and **Vision** (visibility + per-opponent matrix).
4. **All visualisation is plain Avalonia shapes** — `Polyline` sparklines, `Rectangle` bar strips,
   colored dot strips — computed as geometry **in the view-model** (points, normalized fractions).
   XAML binds; it never computes. Per-player data is tiny (≤~30 rounds, ≤~10 weapons, ≤~20
   achievements): **no virtualization needed anywhere on this page.**
5. **HLTV-per-round is measured-adjacent, not synthesized.** The inputs exist in `player_round_stats`,
   but a per-round HLTV rating is *degenerate* (a 3K round yields rating ~3+; averaging per-round
   ratings ≠ the match rating) — see §2.3. The "form" line is **measured per-round Damage + a Kills
   sparkline + a KAST dot-strip**, all of which reconcile with the golden data.

---

## 1. Navigation & window model

### 1.1 Decision: inline overlay over the Stats tab (WASM-safe master-detail)

| Option | WASM | Context kept | State cost | Verdict |
|---|---|---|---|---|
| **Separate `Window`** | **breaks** (`BrowserWindowService` no-ops) | new surface | new lifecycle, `IWindowService` plumbing | **rejected** |
| **Replace tab content + back button** | ok | loses scoreboard underneath | resets nothing if VM lives | viable but jarring |
| **Inline overlay spanning the whole `StatsTabView`** | ok | scoreboard state frozen underneath | zero — same VM instance | **chosen** |

The chosen model is the Charles/Fiddler + React-DevTools **master-detail**: the list stays "behind"
(its sort, category chip, scroll all frozen), the detail slides over the top. Because the overlay is
just another region of `StatsTabView` bound to `StatsTabViewModel`, **no data is re-fetched or
re-projected** — the details VM is built from the tables the Stats VM already owns.

**Placement.** The overlay is a `Border` that is the **last child of the `StatsTabView` root `Grid`
with `Grid.RowSpan="4"`** (covers the view rail, context bar, category sub-rail, and content) so the
scoreboard chrome underneath is fully occluded and non-interactive. `IsVisible` binds
`IsPlayerDetailsOpen`. Give it the shell background (`ShellBg`) so it reads as a full surface, not a
popup.

```
StatsTabView root Grid (RowDefinitions="Auto,Auto,Auto,*")
├─ Row 0  view rail          ┐
├─ Row 1  context bar        │  frozen underneath, occluded
├─ Row 2  category sub-rail  │
├─ Row 3  scoreboard content ┘
└─ Border  Grid.RowSpan=4  IsVisible={IsPlayerDetailsOpen}   ← PlayerDetailsView overlay
```

### 1.2 Open / close / switch

- **Open — double-click:** `DoubleTapped` on the scoreboard row (`StatsRowTemplate`) →
  `OpenPlayerDetailsCommand` with the row's `PlayerSlot`. **Guard:** `IsTotals` rows must not open
  (they have no slot); wire the handler only on player rows, or early-return when `IsTotals`.
- **Open — context menu:** a `ContextMenu` on the same row with a single **Details** `MenuItem` →
  same command, same parameter. (Also a natural home later for "Copy row".) `MenuItem` has a
  `Command` property, so this path is pure XAML.
- **Wiring note (avoid a needless dependency):** Avalonia 11 exposes `DoubleTapped` as a *routed
  event*, not a bindable `Command`. Do **not** pull in `Avalonia.Xaml.Interactivity` (Behaviors) to
  bind it — that is a new NuGet and a WASM surface §10 forbids. Use **code-behind**, exactly as
  `StatsTabView.axaml.cs` already does for the export folder picker: a `DoubleTapped` handler that
  reads `((Control)sender).DataContext as StatsRow` and calls `vm.OpenPlayerDetails(row)`. Only the
  double-tap path needs code-behind; the context-menu path stays declarative.
- **Close — Esc / Back:** a `‹ Back` button in the identity header, plus a `KeyBinding`
  `Gesture="Escape"` on the overlay `Border` → `ClosePlayerDetailsCommand`. Back returns to the
  scoreboard exactly as left (nothing in the parent VM was touched).
- **Switch player without closing:** the identity header carries `◄ prev` / `next ►` steppers and a
  **player dropdown** (`ComboBox` over all players in the current game table, CT then T). Selecting a
  player rebuilds the details VM for the new slot **and keeps the current sub-section** (Overview /
  Rounds / Vision) and, within Rounds, the selected round.

### 1.3 Lifecycle coupling to the parent (must)

The parent `Update(...)` replaces **every** table with new instances (a new evaluation / new demo).
An overlay open across that boundary would point at a stale slot that may not exist in the new demo.
Mirror the existing `SetExtraTables` discipline: **the first line of `Update` closes the overlay and
nulls the details VM** (`IsPlayerDetailsOpen = false; PlayerDetails = null;`). No stale surface can
survive a re-evaluation.

---

## 2. Page information architecture

### 2.1 Full-page wireframe

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ‹ Back   ● ZywOo        CT · Vitality              ◄ prev   [ ZywOo            ▾ ]  next ►│  identity
├──────────────────────────────────────────────────────────────────────────────────────┤
│  Rating 1.42   │ K 24 │ D 14 │ A 5 │ ADR 92.1 │ KAST 78.6% │ K/D 1.71 │ HS 45.8% │ ...  │  core strip
├──────────────────────────────────────────────────────────────────────────────────────┤
│ [ Overview ]   [ Rounds ]   [ Vision ]                                                  │  sub-rail (statscat pills)
├──────────────────────────────────────────────────────────────────────────────────────┤
│  ┌─ Form (per-round) ───────────────────────────────────────────────────────────────┐ │
│  │  Kills  ╱╲   ╱╲__╱▔╲_        Damage ▁▃▂█▅▂▇▁▃▅▂█  KAST ●●○●●●○●●●●○●●●●●●○●●●●●●●○●● │ │  ← full width
│  └──────────────────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Weapon Breakdown ─────────────────┐  ┌─ Opening Duels ──────────────────────────┐ │
│  │ AK-47   ████████████████ 14        │  │  Duel Win %  ████████████░░░  62%         │ │
│  │ AWP     █████████ 8                 │  │  Opening K 8   Opening D 5   +/- +3       │ │  ← 2-col grid
│  │ Deagle  ███ 2   …                   │  │  CT  4K / 2D      T  4K / 3D              │ │
│  └────────────────────────────────────┘  └──────────────────────────────────────────┘ │
│  ┌─ Clutch & Multi-kills ─────────────┐  ┌─ Utility ────────────────────────────────┐ │
│  │  Clutches 2   Aces 1               │  │  Flashes 41  Enemies Flashed 52          │ │
│  │  2K ██ 6   3K █ 3   4K ▏1   Ace ▏1 │  │  HE 18  Smokes 34  Molotovs 9            │ │
│  └────────────────────────────────────┘  │  Flash Assists 6   Avg Blind 1.4s        │ │
│  ┌─ Damage & Accuracy ────────────────┐  └──────────────────────────────────────────┘ │
│  │  Enemy Dmg 2810   Accuracy 24.1%   │  ┌─ Achievements (12) ──────────────────────┐ │
│  │  Shots 512  Hits 124  HS 45.8%     │  │  r7  ace           tick 128340           │ │
│  │  Team Dmg 40   Self Dmg 12         │  │  r12 clutch_1v3    tick 210114           │ │
│  └────────────────────────────────────┘  │  r18 deagle_hs …                         │ │
│                                           └──────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

The Overview body is a **vertically-scrolling** `Grid` (2 columns, `*,*`) of `InspectorCard`s
(§5.1). The **Form** card spans both columns. There is no horizontal scroll anywhere on this page —
each panel fits its column.

### 2.2 Panel inventory (all grounded in existing data)

| # | Panel | Section | Source table(s) | Phase |
|---|---|---|---|---|
| P-1 | Identity header | (always) | `player_game_stats` (1 row) + demo player info | P0 |
| P-2 | Core stat strip | (always) | `player_game_stats` | P0 |
| P-3 | Form — per-round timeline | Overview (full width) | `player_round_stats` (rows for slot) | P0 |
| P-4 | Weapon breakdown | Overview | `player_kills_by_weapon`, `player_damage_by_weapon` | P0 |
| P-5 | Achievements (this player) | Overview | `rule_chain_events` | P0 |
| P-6 | Opening duels | Overview | `player_game_stats` | P1 |
| P-7 | Clutch & multi-kills | Overview | `player_game_stats` | P1 |
| P-8 | Utility | Overview | `player_game_stats` | P1 |
| P-9 | Damage & accuracy | Overview | `player_game_stats` | P1 |
| P-10 | Round-by-round table | **Rounds** sub-section | `player_round_stats` | P1 |
| P-11 | Visibility summary | **Vision** sub-section | `player_visibility_stats` | P2 |
| P-12 | Per-opponent matrix | **Vision** sub-section | `visibility_pairs` (both directions) | P2 |

### 2.3 The "form" line — why per-round Damage, not per-round HLTV

The brief asked for "HLTV per-round trend if derivable." The **inputs are present** in
`player_round_stats` (`Kills`, `Deaths`, `Assists`, `Damage`, `HasKAST`), so it is *derivable* — but
it is **misleading**, which is worse than unavailable. The shipped formula
(`rules/player-stats.yaml` L358) is:

```
HLTV = 0.73·(KAST/100) + 0.3591·KPR − 0.5329·DPR + 0.2372·IMPACT + 0.0032·ADR + 0.1587
IMPACT = 2.13·KPR + 0.42·APR − 0.41           KPR = kills / round_number   (cumulative)
```

Evaluated over a **single** round, KPR degenerates to that round's kill count (0–5) and DPR to 0/1.
A 3K round gives KPR=3 → IMPACT≈5.98 → rating ≈ **3.4**; a death-only round gives a large negative.
And a mean of per-round ratings does **not** equal the match rating (which uses match-cumulative
KPR/DPR), so the line would silently contradict the golden `HLTV` shown in the core strip. That is a
parity-violating optical.

**Decision:** the Form card plots what is *directly measured and reconciles* —

- **Kills sparkline** — `Polyline` over rounds, y = that round's `Kills`.
- **Damage bar strip** — one `Rectangle` per round, height ∝ `Damage` (ADR is literally per-round
  damage; this *is* the honest "impact form" line).
- **KAST dot-strip** — one dot per round, filled (`StatPositive`) when `HasKAST` is truthy, hollow
  (`TextDim`) otherwise.
- **Opening-duel ticks** (optional, P1) — per round, an up-tick (`StatPositive`) if `FK`>0, a
  down-tick (`AccentError`) if `FD`>0.

If a rating-shaped line is still wanted, the only defensible version is a **cumulative**
running HLTV (recompute the formula with cumulative kills/deaths/rounds up to round N) — that
*converges to* the golden match rating and is monotone-meaningful. Flag it as a P2 stretch, computed
in the VM from the same round rows, and label it "cumulative rating" so it is never mistaken for a
per-round rating.

---

## 3. Per-panel spec, data contract, and wireframe

Every contract below lists the **engine keys** (never display names). All game-scoped values come
from the **single** `player_game_stats` row whose `Dimensions["player_slot"] == slot`. Reuse
`StatCell` for formatting (doubles → 2 dp, null → empty) and `ColumnCatalogue.Resolve(key)` for
display name / tooltip / emphasis.

### P-1 / P-2 — Identity header + core strip

```
┌───────────────────────────────────────────────────────────────────────────────────┐
│ ‹ Back   ● ZywOo        CT · Vitality              ◄   [ ZywOo ▾ ]   ►               │
│  ┌────────┐ ┌────┬────┬────┬───────┬────────┬───────┬───────┬─────────┐             │
│  │ Rating │ │ K  │ D  │ A  │ ADR   │ KAST%  │ K/D   │ HS%   │ Surv%   │  StatTiles  │
│  │  1.42  │ │ 24 │ 14 │ 5  │ 92.1  │ 78.6   │ 1.71  │ 45.8  │ 63.3    │             │
│  └────────┘ └────┴────┴────┴───────┴────────┴───────┴───────┴─────────┘             │
└───────────────────────────────────────────────────────────────────────────────────┘
```

- **Identity:** `player_name` (UI font, `TextBright`), team bullet + label from `team`
  (2=T `AccentAmber`, 3=CT `AccentInteractive` — reuse `StatsRow.IsCt` / `TeamLabel` logic),
  optional team/org name if resolvable (else omit — no fake data). `map` from the row's `map`
  dimension shown as a quiet subtitle.
- **Core tiles** (`StatTile` = big value + small label): `HLTV`→"Rating" (emphasised, largest tile),
  then `TotalK`, `TotalD`, `TotalA`, `ADR`, `KAST%`, `KD`, `HS%`, `Surv%`. Order and display names
  from `ColumnCatalogue`. Rating/ADR/KAST tiles may carry the P2 heat accent (§5.4).
- **Data:** `player_game_stats` → the one row for `slot`. Keys: `HLTV, TotalK, TotalD, TotalA, ADR,
  KAST%, KD, HS%, Surv%`.
- **Empty state:** the row always exists if the player was in the match; if `slot` is somehow absent
  (spectator opened), show name + "No match stats for this player" and hide the tiles.

### P-3 — Form (per-round timeline)  [P0]

```
┌─ Form ─────────────────────────────────────────────────── rounds 1–30 ─┐
│ Kills   2 ╱╲    3                                                        │
│        1  ╲  ╱▔╲   1   ╱▔╲                (Polyline, y = round Kills)    │
│ Damage ▁▃▂█▅▂▇▁▃▅▂█▂▃▅▇▂▁▃█▅▂▇▃▁▅        (Rectangle strip, h ∝ Damage)  │
│ KAST   ●●○●●●○●●●●○●●●●●●○●●●●●●●○●●●     (dot per round; ● KAST ○ not)   │
│ Duel   ▲ ▔ ▔ ▼ ▔ ▲ ▔ ▔ ▲ ▼ …            (▲ opening K, ▼ opening D)      │
└─────────────────────────────────────────────────────────────────────────┘
```

- **Data:** `player_round_stats`, all rows where `player_slot == slot`, ordered by `round_number`.
  Keys per round: `Kills`, `Damage`, `HasKAST`, `FK`, `FD` (and `Deaths`, `Assists`, `HSKills`,
  `Flashed`, `EKills` available for the Rounds table P-10). These are `IRoundScopedNode` columns the
  projector confirms are emitted (Kills/Deaths round-scoped; KAST from `kast.yaml`).
- **Graceful degradation (documented contract):** read every per-round key via
  `Values.GetValueOrDefault(key)`; a missing key resolves to `null` → `StatCell` renders empty and
  the geometry treats it as 0. Any panel degrades cleanly on an absent column rather than throwing —
  so a reduced rule set (e.g. no KAST rule loaded) simply hides that strip.
- **Geometry (computed in VM):**
  - Kills sparkline: `Points` = `[(i·step, H − Kills_i/killMax·H) …]`, `killMax = max(1, maxKills)`.
  - Damage strip: per round a bar record `{ X, Width, Height = Damage_i/dmgMax·H, Y = H − Height }`,
    `dmgMax = max(1, maxDamage)`.
  - KAST dots: per round `{ X, Filled = HasKAST_i is truthy }`.
- **Interaction:** hovering a round column shows a tooltip "r{n}: {Kills}K {Assists}A {Deaths}D ·
  {Damage} dmg · KAST {✓/✗}". A click on a round column sets the Rounds sub-section's selected round
  (deep-link) — optional P1.
- **Empty state:** no round rows (warmup-only demo) → "No live rounds recorded."

### P-4 — Weapon breakdown  [P0]

```
┌─ Weapon Breakdown ───────────── kills │ damage ─┐
│ AK-47    ████████████████████ 14                │   share bars, sorted desc
│ AWP      ███████████ 8                           │   by the active metric
│ Deagle   ████ 3                                  │
│ USP-S    ██ 2                                     │
│ Knife    ▏1                                       │
└──────────────────────────────────────────────────┘
```

- **Data:** two keyed tables located **by `MetricTable.Name`** in the parent's `_extraTables`:
  `player_kills_by_weapon` and `player_damage_by_weapon`. **The value-column name is dynamic** —
  `KeyedStatsProjector` names it after the rule's `columns:` label or the rule id — so read
  `table.ValueColumns[0]`, never a hardcoded string. Rows: filter `Dimensions["player_slot"] == slot`;
  the weapon is `Dimensions["key"]`; the count is `Values[valueColumn]`.
- **Presentation:** a `[ kills | damage ]` toggle (two `statscat` pills) selects the metric. Bars are
  horizontal share bars (`BarRow`, §5.2), width = `value / maxValue`, sorted descending, weapon name
  in UI font + value in monospace.
- **Empty state (two variants):** (a) `weapon-stats.yaml` not loaded → neither table present → "Load
  the weapon-stats rules to see per-weapon breakdowns." (b) tables present but this player has no
  rows (no enemy kills) → "No weapon kills recorded."

### P-5 — Achievements (this player)  [P0]

```
┌─ Achievements (12) ───────────────────────────────┐
│ ┌──────┐                                           │
│ │ ace  │  round 7      tick 128340                 │   chain-type chip + round + tick
│ └──────┘                                           │
│ ┌────────────┐                                     │
│ │ clutch_1v3 │  round 12   tick 210114             │
│ └────────────┘                                     │
│ …                                                   │
└─────────────────────────────────────────────────────┘
```

- **Data:** `rule_chain_events`, rows where `Dimensions["player_slot"] == slot`. Fields:
  `chain` (satisfaction id, prefix already stripped by the projector), `round_number`, `tick`,
  `frame_index`. Order by `tick`. (Game-scoped chains have no `player_slot` and correctly never
  appear on a player's page.)
- **Presentation:** reuse the Highlights `AchievementChip` shape — a colored pill for the `chain`
  (accent by chain family: multi-kill / clutch / opening / weapon), round label ("round 7" /
  "warmup" via `HighlightRow.RoundLabel` logic), tick as a dim monospace locator. This is the one
  list-shaped surface; a simple `ItemsControl` (≤~20 items, no virtualization) suffices.
- **Empty state:** "No achievements recorded for this player."

### P-6 — Opening duels  [P1]

```
┌─ Opening Duels ──────────────────────────────┐
│  Duel Win %   ████████████░░░░░░  62%         │  single gauge bar (Duel%)
│  Opening K 8    Opening D 5    +/-  +3        │  TotalFK / TotalFD / FK±
│  CT   4 K / 2 D          T   4 K / 3 D        │  CTFK/CTFD  ·  TFK/TFD
└───────────────────────────────────────────────┘
```

- **Data:** `player_game_stats` keys `Duel%`, `TotalFK`, `TotalFD`, `FK±`, `CTFK`, `CTFD`, `TFK`,
  `TFD`. `FK±` carries `HeatGoodHigh`-style sign colouring (green ≥0 via `StatPositive`, red <0 via
  `AccentError`). The gauge is one `BarRow` with width = `Duel%/100`.
- **Empty state:** none needed — zeros are meaningful (a player who took no opening duels).

### P-7 — Clutch & multi-kills  [P1]

```
┌─ Clutch & Multi-kills ───────────────────────┐
│  Clutches 2      Rapid Kills 4     Revenge 3  │  Clutch / RapidKills / Revenge
│  2K ██████ 6   3K ███ 3   4K █ 1   Ace █ 1    │  MiniBarHistogram over 2K/3K/4K/5K
└───────────────────────────────────────────────┘
```

- **Data:** `player_game_stats` keys `Clutch`, `RapidKills`, `Revenge`, `2K`, `3K`, `4K`, `5K`
  (display "Ace"). The multi-kill histogram (`MiniBarHistogram`, §5.3) has four fixed buckets
  2K/3K/4K/Ace, height ∝ count, `max = max(1, maxBucket)`; `3K/4K/Ace` carry the `Positive` accent.
- **Empty state:** none — zeros are meaningful.

### P-8 — Utility  [P1]

```
┌─ Utility ────────────────────────────────────┐
│  Flashes 41    Enemies Flashed 52             │  Flash / EFlash
│  HE 18   Smokes 34   Molotovs 9               │  HE / Smokes / Molly
│  Flash Assists 6      Avg Blind 1.4 s         │  FlashAst / AvgBlind
└───────────────────────────────────────────────┘
```

- **Data:** `player_game_stats` keys `Flash`, `EFlash`, `HE`, `Smokes`, `Molly`, `FlashAst`,
  `AvgBlind`. Optional bar treatment on the grenade-count row (`BarRow` over HE/Flash/Smokes/Molly
  normalised to their own max). `KeyValueTable` (§5.5) is fine if bars feel heavy here.
- **Empty state:** none — zeros meaningful.

### P-9 — Damage & accuracy  [P1]

```
┌─ Damage & Accuracy ──────────────────────────┐
│  Enemy Dmg 2810        Accuracy 24.1%         │  EnemyDmg · HitFoe/Shots (derived)
│  Shots 512   Hits 124   HS % 45.8             │  Shots / HitFoe / HS%
│  Team Dmg 40   Self Dmg 12   Avg HP@Dmg 78    │  TeamDmg / SelfDmg / AvgHP→Dmg
└───────────────────────────────────────────────┘
```

- **Data:** `player_game_stats` keys `EnemyDmg`, `Shots`, `HitFoe`, `HS%`, `TeamDmg`, `SelfDmg`,
  `AvgHP→Dmg`, `TotalHS`. **Accuracy is derived in the VM:** `HitFoe / Shots · 100` (guard
  `Shots==0 → null`); it is a display-only convenience, not a golden column, so compute it locally
  and label it plainly. `TeamDmg` / `SelfDmg` carry the `Negative` accent (`AccentError`).
- **Empty state:** none.

### P-10 — Round-by-round table  [P1, Rounds sub-section]

```
┌ round │ K │ D │ A │ Dmg │ KAST │ HS │ Opn │ Flashed ┐
│  1    │ 1 │ 0 │ 0 │  92 │  ✓   │ 1  │  ▲  │   2     │
│  2    │ 0 │ 1 │ 0 │  14 │  ✗   │ 0  │  ▼  │   0     │
│  …                                                   │
└──────────────────────────────────────────────────────┘
```

- **Data:** `player_round_stats` rows for `slot`, ordered by `round_number`. Columns via
  `ColumnCatalogue`: `Kills`, `Deaths`, `Assists`, `Damage`, `HasKAST`, `HSKills`, `FK`/`FD` (as a
  single ▲/▼ "Opn" glyph), `Flashed`, `EKills`, `Traded`, `UtilDmg`, `DeagleHS`. Reuse the scoreboard
  cell styling (right-aligned monospace, `KeyValueTable`/columnar). ≤~30 rows → plain `ItemsControl`.
- **Cross-link:** the Form card's round columns (P-3) deep-link here; selecting a round scrolls/
  highlights it.
- **Empty state:** "No live rounds recorded."

### P-11 — Visibility summary  [P2, Vision sub-section]

```
┌─ Vision ─────────────────────────────────────┐
│  Exposed to enemies   ███████░░░░░  23.4 %    │  ExposedShare bar + ExposedToEnemiesSec
│  Could see an enemy   █████████░░░  31.1 %    │  VisionShare  bar + CouldSeeEnemySec
│      312.4 s exposed        414.9 s vision     │
└───────────────────────────────────────────────┘
```

- **Data:** `player_visibility_stats`, the row where `player_slot == slot`. Keys
  `ExposedToEnemiesSec`, `CouldSeeEnemySec`, `ExposedShare`, `VisionShare`. Two `BarRow`s (width =
  the share, already 0–1) + the raw seconds. Reuse `VisibilityRow`'s formatters.
- **Empty state (two variants — mirror the parent gate):**
  - **Not computed yet** but the map *has* a bake (`CanComputeVisibility` true): a CTA card
    "Compute 3-D line-of-sight for this demo →" whose button **cross-calls the parent's existing
    `ComputeVisibilityCommand`**. When it completes, the details VM re-reads the now-populated table.
  - **No collision bake** for this map (`CanComputeVisibility` false): "Visibility unavailable — no
    collision bake for {map}." (No button.)

### P-12 — Per-opponent matrix (directed)  [P2, Vision sub-section]

```
┌─ Per-opponent vision ──────── I saw them │ exposed to them ─┐
│  broky        41.2 s              88.5 s                     │
│  ropz         12.0 s              33.1 s                     │
│  rain          8.4 s              21.7 s   …                 │
└──────────────────────────────────────────────────────────────┘
```

- **Data (both directions — this is the correction that keeps the slice honest):**
  `visibility_pairs` is a **directed** viewer→target matrix.
  - **"I saw them"** = rows where `viewer_slot == slot` → `Values["could_see_sec"]`, keyed by
    `target_name`.
  - **"Exposed to them"** = rows where `target_slot == slot` → `Values["exposed_sec"]`, keyed by
    `viewer_name`.
  - Join the two by opponent name/slot into one row per opponent; sort by "I saw them" desc.
- **Empty state:** same gate as P-11 (only present once visibility is computed).

---

## 4. Visualization guidance (plain Avalonia shapes, no charting library)

**All geometry is computed in the view-model and bound as data.** XAML draws; it never measures data.
Every panel's data is tiny, so recompute on `slot` change is free.

| Viz | Control | VM output | Where |
|---|---|---|---|
| **Sparkline** (kills/round; cumulative-rating stretch) | `Polyline` in a fixed-size `Canvas`/`Border` | `Points : Avalonia.Points` (or `IList<Point>`) pre-scaled to the panel px box | P-3 |
| **Bar strip** (damage/round) | `ItemsControl` of `Rectangle` | `IReadOnlyList<Bar{ X,Y,Width,Height,Brush }>` normalised to panel height | P-3 |
| **Dot strip** (KAST/round) | `ItemsControl` of `Ellipse` | `IReadOnlyList<Dot{ X, Filled }>` → brush via style class | P-3 |
| **Duel ticks** | `ItemsControl` of `Path`/`Polygon` (▲/▼) | `IReadOnlyList<Tick{ X, Up }>` | P-3 |
| **Share bars** (weapons, utility, duel gauge, visibility) | `BarRow` UserControl (`Grid` label + `Rectangle` + value) | `Fraction (0–1)` per row | P-4/6/8/11 |
| **Mini histogram** (multi-kills) | `ItemsControl` of `Rectangle`, columns | `IReadOnlyList<HistBar{ Label, Fraction, Count, Positive }>` | P-7 |

**Rules the implementer must follow:**
- Normalise against a **per-panel max** (`max(1, maxValue)` to avoid divide-by-zero and flat empties).
- Fixed panel px dimensions (e.g. spark box 260×48, dot ⌀6, bar height 12) so scaling math is
  trivial and layout is stable; the page never needs responsive geometry.
- Colour from palette tokens only: bars `AccentInteractive`; positive `StatPositive`; negative
  `AccentError`; KAST filled `StatPositive` / hollow `TextDim`; CT `AccentInteractive` / T
  `AccentAmber`.
- **No axes, gridlines, or legends** — this is a dev/analyst glanceable dashboard, not a report. A
  hovered tooltip carries exact numbers; the bar/spark carries the shape.

---

## 5. Reusable component catalogue

Extract these once; use across panels (and back-port `AchievementChip` to the Stats Highlights view,
which currently hand-rolls four `TextBlock`s).

### 5.1 `InspectorCard` (templated `HeaderedContentControl` or UserControl)
- **Contract:** `Header : string`, optional `HeaderAccent : IBrush`, optional `HeaderRight` slot
  (for the weapon kills/damage toggle), `Content`.
- **Look:** `CardBg` (`#171726`) background, 1px `BorderSubtle`, left accent strip (2px) when
  `HeaderAccent` set (reuse the `teamHeader` accent idiom), header in monospace SemiBold `TextMid`.
- **Why:** every panel is one of these; single source for card chrome.

### 5.2 `BarRow` (UserControl)
- **Contract:** `Label : string`, `Fraction : double` (0–1), `ValueText : string`, `Fill : IBrush`.
- **Look:** `Grid ColumnDefinitions="Auto,*,Auto"` — label (UI font), track (`PanelHeaderBg`) with a
  `Rectangle` width-bound to `Fraction` (via a `*`-column inner grid or a width converter), value
  (monospace). One control serves weapons, utility, duel gauge, and visibility.

### 5.3 `MiniBarHistogram` (UserControl)
- **Contract:** `IReadOnlyList<HistBar>` where `HistBar { Label, Fraction, Count, Positive }`.
- **Look:** horizontal `ItemsControl`, each item a labelled vertical `Rectangle` (height ∝ Fraction)
  with the count above. Used by multi-kills; reusable for any small categorical count.

### 5.4 `StatTile` (UserControl)
- **Contract:** `Label`, `Value`, `Emphasis` (None/Positive/Negative/Heat), optional `IsHero` (larger
  Rating tile).
- **Look:** stacked value (large, `TextValue`/`TextBright`) over label (small, `TextLabel`), fixed
  min-width, right-aligned numeric. `Heat` maps a 0–1 goodness to a foreground ramp (P2).

### 5.5 `KeyValueTable` (UserControl or inline template)
- **Contract:** `IReadOnlyList<(string Label, string Value, Emphasis)>`.
- **Look:** two-column `Grid` (`Auto,*`), label `TextLabel`, value monospace right-aligned, emphasis
  → foreground. The default renderer for the non-viz rows (damage/accuracy, utility text rows).

### 5.6 `AchievementChip` (UserControl / DataTemplate)
- **Contract:** `ChainId : string`, `RoundLabel : string`, `Tick : int`.
- **Look:** rounded pill (reuse `statscat` corner radius) colored by chain family, round label, dim
  tick. Extracted from — and retrofitted into — the Stats Highlights view.

### 5.7 Reused as-is
- `statscat` pill style → the Overview/Rounds/Vision sub-rail and the weapon kills/damage toggle.
- `teamHeader` / `.ct` accent styles → identity header + `InspectorCard` accent.
- `StatCell` (display formatting) and `ColumnCatalogue.Resolve` (names/tooltips/emphasis) → every
  numeric value on the page.

---

## 6. View-models & data plumbing

### 6.1 `StatsRow` gains `PlayerSlot` (P0 linchpin)
- Add `int PlayerSlot` to the `StatsRow` record; populate it in `BuildRow` from
  `row.Dimensions.GetValueOrDefault("player_slot")` (Convert.ToInt32, invariant). Totals rows use a
  sentinel (`-1`) and are guarded from opening details.

### 6.2 `StatsTabViewModel` additions
- `[ObservableProperty] bool _isPlayerDetailsOpen;`
- `[ObservableProperty] PlayerDetailsViewModel? _playerDetails;`
- `[RelayCommand] void OpenPlayerDetails(StatsRow row)` — guard `row.IsTotals`; build
  `PlayerDetails = new PlayerDetailsViewModel(row.PlayerSlot, this)` and set `IsPlayerDetailsOpen`.
  The details VM reads the parent's tables (expose read-only accessors on the parent, or pass the
  `MetricTable`s in the ctor: `_gameTable`, `_roundTable`, `_eventsTable`, `_extraTables`,
  `_visibilityPlayersTable`, `_visibilityPairsTable`, plus `CanComputeVisibility` /
  `ComputeVisibilityCommand` for the Vision CTA).
- `[RelayCommand] void ClosePlayerDetails()` → `IsPlayerDetailsOpen = false; PlayerDetails = null;`.
- **In `Update(...)`:** first thing — `ClosePlayerDetails()` (lifecycle coupling, §1.3).
- Expose `IReadOnlyList<PlayerRef> DetailPlayers` (slot + name + team, CT then T, from `_gameTable`)
  for the header's prev/next/dropdown switcher; `OpenPlayerDetails(slot)` overload for switching.

### 6.3 `PlayerDetailsViewModel` (new, `ViewModels/Stats/`)
- Ctor takes `slot` + the tables (or the parent). On construction (and on `SetSlot(slot)`), it
  filters each table to the player and builds the panel VMs. Sub-VMs keep it out of god-object
  territory:
  - `IdentityViewModel` (P-1/2), `FormTimelineViewModel` (P-3, computes spark/bar/dot geometry),
    `WeaponBreakdownViewModel` (P-4, kills/damage toggle), `AchievementsViewModel` (P-5),
    `DuelsViewModel` (P-6), `ClutchViewModel` (P-7), `UtilityViewModel` (P-8),
    `DamageAccuracyViewModel` (P-9), `RoundTableViewModel` (P-10), `VisionViewModel` (P-11/12).
  - `[ObservableProperty] DetailSection _section = DetailSection.Overview;` (enum Overview/Rounds/
    Vision) drives the sub-rail; `IsOverview`/`IsRounds`/`IsVision` computed for `IsVisible`.
- All geometry (`Points`, bar lists, dot lists) is computed here and exposed as plain
  `IReadOnlyList<…>` / `Avalonia.Points` — the view binds, never computes.

### 6.4 Views (new, `Views/Stats/`)
- `PlayerDetailsView.axaml` — the overlay root (identity header + core strip + sub-rail + section
  content switch). One `DataTemplate` per panel; the reusable controls (§5) live in
  `Controls/` or `Views/Stats/Components/`.

---

## 7. State retention

| State | Rule |
|---|---|
| **Parent scoreboard (sort, category chip, scroll, round)** | Untouched — the overlay never mutates parent VM state; on close it is exactly as left. |
| **Open player** | Held on `PlayerDetailsViewModel`; survives switching sub-sections. |
| **Sub-section (Overview/Rounds/Vision)** | Persists across **player switch** (prev/next/dropdown). Resets to **Overview** on a fresh open (double-click a new row) — a new drill-down starts at the dashboard. |
| **Selected round (Rounds sub-section)** | Persists across player switch; clamp if the new player's demo has fewer rounds (it won't — same demo — but guard). |
| **Weapon metric toggle (kills/damage)** | Persists across player switch. |
| **New evaluation / new demo (`Update`)** | Overlay **closes**, details VM nulled (§1.3). Non-negotiable — the old slot may not exist. |
| **Visibility computed while overlay open** | The Vision CTA cross-calls the parent; on completion the details VM re-reads `player_visibility_stats`/`visibility_pairs` (observe the parent's `HasVisibilityStats`). |

---

## 8. Empty-state matrix (per panel)

| Panel | Condition | State |
|---|---|---|
| Identity / core | slot not in `player_game_stats` | name + "No match stats for this player"; hide tiles |
| Form | no rows in `player_round_stats` for slot | "No live rounds recorded." |
| Weapon | neither weapon table present (rules not loaded) | "Load weapon-stats rules to see per-weapon breakdowns." |
| Weapon | tables present, no rows for slot | "No weapon kills recorded." |
| Achievements | no `rule_chain_events` for slot | "No achievements recorded for this player." |
| Duels / Clutch / Utility / Damage | (never empty — zeros are meaningful) | render zeros |
| Rounds table | no round rows | "No live rounds recorded." |
| Vision | not computed, map **has** bake (`CanComputeVisibility`) | CTA "Compute 3-D line-of-sight →" → parent `ComputeVisibilityCommand` |
| Vision | not computed, **no** bake | "Visibility unavailable — no collision bake for {map}." |
| Per-opponent | not computed | (hidden until Vision computed) |

---

## 9. Implementation checklist (phased, S/M/L)

### Phase P0 — the "robust dashboard" feel (target: the deliverable's core)

| # | Item | Size |
|---|---|---|
| P0-1 | **`PlayerSlot` on `StatsRow` + `BuildRow`** (populate from `player_slot`; totals sentinel `-1`). *Do first — everything joins on this.* | **S** |
| P0-2 | `StatsTabViewModel`: `IsPlayerDetailsOpen`, `PlayerDetails`, `OpenPlayerDetailsCommand` (guard `IsTotals`), `ClosePlayerDetailsCommand`; close on `Update` (§1.3); `DetailPlayers` list. | **M** |
| P0-3 | Wire **double-tap + `ContextMenu` "Details"** on `StatsRowTemplate` → `OpenPlayerDetailsCommand`. Context-menu = declarative `MenuItem.Command`; double-tap = **code-behind** `DoubleTapped` handler (no `Avalonia.Xaml.Interactivity` dep — §1.2 wiring note). | **S** |
| P0-4 | `PlayerDetailsView.axaml` overlay (Grid.RowSpan=4, `ShellBg`, Esc `KeyBinding`) + identity header + core strip + prev/next/dropdown switcher. | **M** |
| P0-5 | `InspectorCard`, `StatTile`, `BarRow` reusable controls (§5). | **M** |
| P0-6 | **P-3 Form** — VM geometry (kills `Polyline`, damage `Rectangle` strip, KAST dots) + view. | **M** |
| P0-7 | **P-4 Weapon breakdown** — locate tables by `Name`, read `ValueColumns[0]`, kills/damage toggle, share bars, two empty states. | **M** |
| P0-8 | **P-5 Achievements** — `AchievementChip` + filtered `rule_chain_events` list; empty state. | **S** |

### Phase P1 — deepen the dashboard

| # | Item | Size |
|---|---|---|
| P1-1 | **P-6 Opening duels** (gauge + CT/T split). | **S** |
| P1-2 | **P-7 Clutch & multi-kills** (`MiniBarHistogram`). | **M** |
| P1-3 | **P-8 Utility** (`KeyValueTable` + optional grenade bars). | **S** |
| P1-4 | **P-9 Damage & accuracy** (derived accuracy; negative accents). | **S** |
| P1-5 | **P-10 Rounds sub-section** — round-by-round table; Form-card round deep-link. | **M** |
| P1-6 | Duel-tick row on the Form card (▲ FK / ▼ FD). | **S** |

### Phase P2 — vision + polish

| # | Item | Size |
|---|---|---|
| P2-1 | **P-11 Visibility summary** — bars + two empty states; CTA cross-calls parent compute. | **M** |
| P2-2 | **P-12 Per-opponent matrix** — both directions (`viewer_slot`/`target_slot`) joined by opponent. | **M** |
| P2-3 | Heat accents on Rating/ADR/KAST core tiles (min→max across the match's players). | **S** |
| P2-4 | Retrofit `AchievementChip` into the Stats Highlights view (dedupe). | **S** |
| P2-5 | *(stretch)* Cumulative running-rating line on the Form card (labelled "cumulative rating", converges to golden HLTV). | **M** |

**Total P0 ≈ one to two focused days.** No engine change, no parity change, no protected-file change,
no new dependency, no TreeDataGrid, no virtualization. Everything lands in `ViewModels/Stats/` +
`Views/Stats/` + a handful of reusable controls, reading the tables `StatsTabViewModel` already owns.

---

## 10. What must NOT change / cross-cutting constraints

- **`MetricTable.ValueColumns` keys** — this page reads them; it never renames them. Display names come
  from `ColumnCatalogue`.
- **No separate `Window`** — WASM parity (`BrowserWindowService` no-ops). Inline overlay only.
- **No TreeDataGrid** (AVLIC0001); no charting library. Hand-rolled columnar + plain shapes.
- **Weapon value-column is dynamic** — read `ValueColumns[0]`, never hardcode `kills_by_weapon`.
- **`visibility_pairs` is directed** — read both `viewer_slot` and `target_slot`.
- **Protected parser files** — untouched. This is App-project only.
- **Accuracy / cumulative-rating are display-only derived values** — computed in the VM, plainly
  labelled, never presented as golden columns.

---

## 11. Open questions

None — the two decisions that *look* like forks are settled on evidence, and neither changes the
design's direction:

1. **Window model** — decided **inline overlay** (WASM correctness, not preference). A floating,
   re-dockable desktop window would be a later `IWindowService` extension, and it would still have to
   degrade to the overlay on WASM.
2. **Per-round rating line** — decided **not** to synthesize a per-round HLTV (degenerate/misleading);
   Form shows measured Damage + Kills + KAST. A **cumulative** rating line (P2-5) is the only
   defensible rating-shaped curve if one is ever wanted.
</content>
</invoke>
