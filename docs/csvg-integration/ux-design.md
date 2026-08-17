# CSVG Integration — UX Design Specification

The UX/UI spec for the DemoViewer.NET ↔ Cs2VideoGenerator ("CSVG") integration — F1 Live Sync,
F2 Analysis Verification, F3 Highlights tab, F3b Create-Reel dialog.

It defines layout, states, the exact existing controls / patterns / tokens to reuse (file-anchored),
interaction flows, error/empty/loading states, and per-user-category default visibility feeding the
feature-gating system. It does **not** design the sync-engine internals — those belong to
`docs/csvg-integration/implementation-plan.md`, whose §2 verified facts are authoritative.

Canonical UI references it must not contradict: `docs/ui/design-system.md` (tokens, shared controls,
category-visibility matrix §5, layout patterns §4) and `docs/ui/theme-token-catalog.md` (the 191-token
namespace; every colour resolves from it via `{DynamicResource}` — no inline hex, no `{StaticResource}` for
a palette token). The two shared patterns this spec introduces are recorded as **additions** to
`design-system.md` (see §11).

---

## 0. Design principles specific to this feature set

Four constraints shape every decision below.

1. **Heavyweight actions must be explicit and informed, never ambient.** Enabling Live Sync or generating a
   reel *launches a full CS2 game* (up to ~2 min to connect), and Live Sync *temporarily modifies the
   user's real CS2 install* (restored on stop). No surface may trigger a CS2 launch as a side effect of a
   normal viewing action. Every launch is preceded by an informed action (a confirm in a flyout or the reel
   dialog's `Generate`) whose copy states the cost.

2. **Honest state labelling.** Several CS2-side states are *inferred* today (e.g. "CS2 paused" from tick
   silence) until a future CSVG protocol version makes them exact. Inferred states get a **visually distinct
   treatment** (§5.1) so a user never mistakes a guess for a fact.

3. **Superset gating, with one deliberate override.** developer ⊇ power-user ⊇ consumer for default
   visibility; every gated surface stays user-toggleable in Settings. The single place this spec sets a
   *default* against the usual power-user lean (Live Sync default-off for power-users) is documented as a
   named fork in §2.3.

4. **One CS2 instance per machine.** Live Sync (F1) and reel generation (F3b) both drive the same single
   CSVG orchestrator on `localhost:50051`; they **cannot run simultaneously**. This mutual exclusion is a
   first-class UX concern, designed in §9 (the interlock), not an edge case.

Every coloured surface below resolves from the existing token namespace. **This spec adds zero new tokens**
(§10) — the sync-state palette maps onto existing accents — which is the point of the theme system.

---

## 1. Platform matrix (three-way, per surface)

There is no compile-time desktop/WASM split; all platform gating is runtime `OperatingSystem.IsBrowser()`
plus, for CS2-vs-mock, `OperatingSystem.IsWindows() || OperatingSystem.IsLinux()` (the app has a real
Browser head). Decide each surface explicitly:

| Surface | WASM (browser) | macOS | Windows / Linux |
|---|---|---|---|
| **F1 Live Sync** (chip, flyout, 2D CS2 indicator, speed lock) | **Absent** — never registered/shown; `chrome.livesync` shim also returns false under `IsBrowser()` | **Mock only** — session control runs against the CSVG mock server; every affordance is **developer-labelled** ("Mock CS2") | **Real CS2** |
| **F2 Verify** (Analysis node menu, Highlights "Verify live") | Absent (rides on F1) | Mock/dev-labelled (rides on F1) | Real |
| **F3 Highlights tab** | **Registered, degraded** — no library cache (`AppPaths.ConfigRoot` is null on browser), no background scan, no reel/verify; shows only the **in-memory highlights of the currently-open demo** (the open-demo harvest path) with an explanatory empty note | Full browse + scan; reel = **dry-run (mock)** only | Full |
| **F3b Create-Reel** (dialog, job) | Absent (no CS2, no filesystem output) | **Dry run (mock)** primary button — walks the clip plan without recording; developer/testing-labelled | **Real** generation (requires OBS) |

**Rationale for the Highlights-tab WASM call (registered-but-degraded, not unregistered):** it mirrors the
Library tab, which is registered on both hosts and degrades (no folder-add on WASM via `CanAddFolder`). A
consumer on the web build still gets "the highlights of the demo I just opened," which is coherent and
honest, whereas an unregistered tab that silently vanishes on one host is a worse mental model. Reel/verify
(which need CS2 + a filesystem) are simply absent there.

---

## 2. Feature-gate additions

New entries for `Features/FeatureCatalog.cs` (the single code-defined source of truth) and the shell's
`TabFeatureIds` map (`ViewModels/Shell/MainViewModel.cs:172-182`). Recommendations to the gating system —
**not** ad-hoc `IsVisible`. Defaults use the existing `Defaults(consumer, power, dev)` helper.

### 2.1 New `FeatureDescriptor`s

```csharp
// ---- TAB ----
new("tab.highlights", FeatureScope.Tab, "Highlights",
    "Browse analysis-generated highlights across your library and build highlight reels. Viewing surface.",
    ParentId: null, GroupId: null, Required: false, Defaults(consumer: true, power: true, dev: true)),

// ---- CHROME ----
new("chrome.livesync", FeatureScope.Chrome, "Live Sync (CS2)",
    "Two-way playback sync with a live CS2 game via CSVG. Launches a full CS2 instance (~2 min) and " +
    "temporarily modifies your CS2 install. Developer default; enable in Settings to use it.",
    ParentId: null, GroupId: null, Required: false, Defaults(consumer: false, power: false, dev: true)),
```

- `tab.highlights` → also add `"highlights.browser" → "tab.highlights"` to `TabFeatureIds` (else the tab is
  fail-open shown to everyone). Registered via a new `IWorkspaceModule`
  (`Modules/Highlights/HighlightsModule`, TabId `"highlights.browser"`, `Order` after Stats — see §7.1).
- `chrome.livesync` is a **Chrome** feature (no tab of its own). It governs the *presence* of the Live Sync
  chip + flyout, the 2D-tab CS2 indicator, the NavStrip speed-lock affordance, and the F2 Verify
  affordances. The shell exposes a gate shim `IsLiveSyncEnabled => _gate?.IsEnabled("chrome.livesync") ?? false`
  **AND** `!OperatingSystem.IsBrowser()`, following the `IsHexPaneEnabled` shim pattern
  (`MainViewModel.cs:799-817`). Note: default is `false` here even for the fail-open case, because a browser
  build must never see it — the shim ANDs the platform guard.

### 2.2 No sub-feature gate for reel generation

Reel generation (F3b) stays a **visible action on the Highlights tab footer for all categories** — a highlight
montage is exactly the marquee thing a consumer would intentionally do. It is guarded not by a gate but by
its own dialog + platform check + the informed `Generate` action (§8, §9). This is deliberate: gating it to
power+ would hide the feature's headline payoff from the audience most excited by it, and the launch is
already fully guarded. *(Alternative considered: a `highlights.reel` SubFeature gated power+. Rejected — the
dialog is the guard; a gate would only add friction to the marquee flow.)*

### 2.3 Documented fork — Live Sync default-off for power-users

This is the one place this spec sets a default **against** the usual power-user lean (which is
visible-with-mitigation).

- **Chosen:** `chrome.livesync` = `Defaults(consumer: false, power: false, dev: true)`.
- **Why, specifically (not merely "it's heavy"):**
  1. It **temporarily modifies the user's real CS2 install** — a side effect *outside the app's sandbox*,
     the one class of action the power-user rubric ("mitigate the damage a mistake could cause") most wants
     defaulted-off.
  2. Its CS2-side states are **honestly labelled as inferred** in v1 (§5.1) — it is not yet an exact,
     polished feature.
  3. The **DV-restart ⇒ CS2-relaunch papercut** is unresolved in v1 — a rough edge a
     power-user shouldn't hit by default.
  The superset rule is satisfied cleanly: **developers get it by default; power-users and consumers are one
  Settings toggle away** (§2.4).
- **Alternative (rejected):** `power: true`. It would surface a persistent chip that does nothing until an
  explicit launch, which is harmless — but it also advertises a beta, install-modifying feature as
  first-class to a tier explicitly designed for recoverability. The informed-launch guard protects against
  accidental launches; it does not make the feature production-grade. Defer power-default-on until the
  CSVG protocol items (exact states, plugin redial) land, at which point flip the default.

### 2.4 Enable entry points, per category (two-step for non-developers)

- **Developer:** the Live Sync chip is present by default (desktop). Enabling a session = click chip →
  flyout → **"Enable Live Sync…"** (informed confirm). One step from the always-present chip.
- **Power-user / Consumer:** `chrome.livesync` is off by default, so **no chip is shown**. The path is
  **two-step and deliberate**: Settings → "Live Sync" section → toggle **Enable Live Sync** on (this flips
  the `chrome.livesync` override) → the chip appears in the status strip → click chip → **"Enable Live
  Sync…"** to actually start a session. The Settings toggle is the "I opt into this beta, install-modifying
  feature" gate; the chip action is the "launch CS2 now" gate. Two distinct consents for a heavyweight,
  side-effecting feature is correct, not friction.
- There is **no toolbar button** for Live Sync. The status chip is the single home; the Settings section is
  the enable/opt-in. (Consistent with the design-system's "one prominent surface per role" discipline and
  the absence of a toast/notification system.)

### 2.5 Consolidated category-visibility matrix (append to design-system.md §5)

`●` default-visible · `○` default-hidden (enableable) · `R` Required. Platform column notes the hard gate.

| Feature / surface | Scope | Consumer | Power | Dev | Platform | Notes |
|---|---|:-:|:-:|:-:|---|---|
| **Highlights** tab | Tab | ● | ● | ● | Desktop full; WASM degraded (§1) | Viewing surface, Library-like. |
| **Live Sync** (chip/flyout/2D-indicator/speed-lock/F2 Verify) | Chrome | ○ | ○ | ● | **Desktop only** (`IsBrowser` guard) | See §2.3 fork. |
| Highlights **Create Reel** button | (ungated) | ● | ● | ● | Real: Win/Linux; dry-run: macOS; absent: WASM | Guarded by dialog + §9 interlock. |
| Highlights **background scan** | setting | ○ | ○ | ○ | Desktop only | Opt-in for **all** categories (30-min churn on 200 demos); not a category default. |

---

## 3. State model → visual language for Live Sync (F1)

The sync-state machine must be legible at a glance from a thin status-strip chip, and in full
from a flyout. This section fixes the **visual vocabulary**; §5 lays out the surfaces.

### 3.1 Dot + label mapping (zero new tokens)

The chip is a **status dot + text label** (not a filled pill). **Two accessibility rules, derived from the
measured contrast in §3.2, are load-bearing:**

- **The label is always the neutral `TextMid`** — it passes AA (4.5:1) on the status-strip surface
  (`PanelHeaderBg`) in every theme (§3.2). State colour is **not** carried by tinting the label (a caution
  tint fails AA on Light — §3.2).
- **The dot is a redundant colour cue; the *word* is the accessible carrier of state.** Every state row
  carries an explicit word ("Following", "Seek unconfirmed", "CS2 quit"), so state is never conveyed by
  colour alone (WCAG 1.4.1). This is what makes the few sub-3:1 dots (dark `AccentInteractive`, dark
  `TextDim` — §3.2) acceptable: they add redundant colour, they don't *carry* meaning.

| Engine state | Dot | Dot token | Label | Chip text (example) |
|---|---|---|---|---|
| Disconnected / Off | **solid** | `TextDim` | `TextMid` | `CS2  ·  Off` |
| HostStarting / LaunchingCs2 / Connecting / LoadingDemo | **pulsing** | `AccentInteractive` | `TextMid` | `CS2  ·  Launching…` / `Connecting…` / `Loading demo…` |
| ConnectedIdle (no demo in CS2) | solid | `AccentInteractive` | `TextMid` | `CS2  ·  Connected (no demo)` |
| Synced · Holding (both paused) | solid | `StatPositive` | `TextMid` | `CS2  ·  Synced (paused)` |
| Synced · Following (CS2 leads, servo locked) | solid | `StatPositive` | `TextMid` | `CS2  ·  Following` |
| Synced · SeekPending | solid + **spinner glyph** | `StatPositive` | `TextMid` | `CS2  ·  Seeking…` |
| **Inferred** sub-state (e.g. CS2 paused inferred) | **hollow ring** | `StatPositive` | `TextMid` + `(inferred)` | `CS2  ·  Paused (inferred)` |
| Degraded (genuinely uncertain) | solid | `AccentCaution` | `TextMid` | `CS2  ·  Seek unconfirmed` |
| Error / Faulted | solid | `AccentError` | `TextMid` | `CS2  ·  Disconnected — CS2 quit` |
| Suspended (reel render — §9) | solid | `TextDim` | `TextMid` | `CS2  ·  Paused for reel render` |

**The inferred distinction is load-bearing (principle 2).** A *believed-good-but-inferred* state
(inferred-pause is a `Synced·Holding` sub-state) stays **green** — we think it's fine — but
is the **only** state that uses a **hollow ring dot**, plus a `(inferred)` suffix, so it never reads as a
confirmed fact. **Off is a *solid* dim dot** (not hollow) precisely so "hollow" means exactly one thing:
"we're not certain." **Degraded** (`AccentCaution`, genuinely uncertain) is a *solid caution* dot — a
different colour and a solid shape from inferred's hollow green. Two honesty levels, two unmistakable
treatments.

- **Pulsing / spinner** are opacity/rotation animations on the dot/glyph (a plain reactive binding to the
  state), not colour changes — so they render headlessly for capture and re-theme correctly.

### 3.2 Measured contrast (computed, all four built-in themes)

Computed WCAG ratios (relative-luminance formula) for the actual token hex in `Styles/DarkPalette.axaml`
(`[Dark]`/`[Light]`) and the `Themes/01-high-contrast.json` / `02-egirl.json` overrides. This is *why* §3.1
uses neutral labels and word-carried state — not a deferral.

**Label on `PanelHeaderBg` (status-strip surface) — small text, needs AA 4.5:1:**

| Label token | Dark | Light | High Contrast | E-Girl |
|---|---|---|---|---|
| `TextMid` (chosen) | 4.59 ✓ | 6.31 ✓ | 15.6 ✓ | 6.22 ✓ |
| `AccentError` (rejected as label) | 4.52 ✓ (fragile) | 4.69 ✓ | 5.58 ✓ | 5.86 ✓ |
| `AccentCaution` (**rejected**) | 8.40 ✓ | **3.95 ✗** | 12.1 ✓ | 10.4 ✓ |

→ `AccentCaution` as a label **fails AA on Light** (3.95); `AccentError` as a label is fragile on Dark
(4.52). **`TextMid` is the only universally-safe label**, so the design carries state on the word + dot, not
the label colour.

**Dot on `PanelHeaderBg` — graphical object, target 3:1:**

| Dot token | Dark | Light | High Contrast | E-Girl |
|---|---|---|---|---|
| `AccentInteractive` (working) | **2.72 ✗** | 6.34 ✓ | 12.9 ✓ | 8.93 ✓ |
| `StatPositive` (good) | 6.87 ✓ | 4.28 ✓ | 11.9 ✓ | 12.0 ✓ |
| `AccentError` (faulted) | 4.52 ✓ | 4.69 ✓ | 5.58 ✓ | 5.86 ✓ |
| `AccentCaution` (degraded) | 8.40 ✓ | 3.95 ✓ | 12.1 ✓ | 10.4 ✓ |
| `TextDim` (off/suspended) | **1.96 ✗** | 2.79 (low) | 11.3 ✓ | 3.16 ✓ |

→ Only the **working `AccentInteractive` dot (dark, 2.72)** and the **off/suspended `TextDim` dot (dark,
1.96)** fall under 3:1 — both are *decorative* states whose word ("Connecting…", "Off") is the carrier, so
this is acceptable under the redundant-cue rule. If a future pass wants strict 3:1 on every dot, add a 1px
`BorderStrong` outline ring to the dot (lifts edge contrast without a token change) — noted, not required.

**Still to verify in-render (can't compute — code-drawn or rendered-glyph):** the 2D-tab `Pb2d*` indicator
on the dark HUD overlay (a dark island in every theme — low risk), and the actual anti-aliased dot glyphs at
8px. Everything token-vs-surface above is *computed*, not eyeballed.

---

## 4. New shared components (proposed for the design system)

Two ≥2× patterns emerge; both are proposed as **additions** to `design-system.md` (recorded in §11).

### 4.1 `StatusChip` + status-strip chip region (used ≥2×: Live Sync, Reel job)

A persistent, stateful, background-activity indicator that lives in the bottom `StatusStrip`, shows a
dot+label state, and opens a `card-flyout` for detail/actions. Both Live Sync (F1) and the Reel job (F3b)
are instances; the pattern is reusable for any future long-running/background activity.

- **StatusStrip extension.** `Controls/StatusStrip.axaml(.cs)` today is a 3-region styled-property control
  (`StatusText` / `PerfText` / `RightText` / `HiddenNote`). Add a **chip region**: a right-aligned
  horizontal `ItemsControl` (spacing 12) between the perf ticker and `RightText`, bound to a new
  `IEnumerable? Chips` styled property. Each item is a `StatusChip` shared control. The shell exposes an
  `ObservableCollection<StatusChipViewModel>` (`MainViewModel.Chips`, or on a slim `ShellStatusViewModel`)
  holding the Live Sync chip VM and, when active, the Reel job chip VM. Empty collection → no chips (the
  strip looks exactly as today for the no-CSVG case).
- **`StatusChip` control** (`Controls/StatusChip.axaml`). DataContext = `StatusChipViewModel`
  (`{ Brush DotBrush; bool IsPulsing; bool IsHollow; string Label; IBrush LabelBrush; object FlyoutContent; ICommand? PrimaryAction }`).
  Structure: a `Button.ghost` (chip body) → `[dot] [label]`; the dot is an `Ellipse` (`Fill=DotBrush` for
  solid, `Stroke=DotBrush`+transparent fill for hollow) with an opacity pulse `Style` when `IsPulsing`; the
  button's `Flyout` hosts `FlyoutContent` in a `Border.card-flyout`. All colours are `{DynamicResource}`
  tokens per §3.1; the VM exposes `DotBrush`/`LabelBrush` as **token brushes resolved live** (bind to
  `{DynamicResource}` in XAML via a small state→class selector, *not* a code-held brush — mirror the
  `Border.teamChip{,.teamT,.teamCt}` bound-class→token pattern the design system mandates).
- **Interaction:** click chip → flyout (states, versions, actions). No hover-only content (touch/keyboard
  reachable). Keyboard: chip is focusable; Enter opens the flyout.

### 4.2 Master-detail split layout pattern (used by Highlights; first in the app)

The Library has no details-pane precedent. Highlights needs list-left / details-right. This
is a net-new **layout pattern** (not a control): a `Grid ColumnDefinitions="*,Auto,1.4*"` with a
`GridSplitter` in the `Auto` column (the design-system's approved resizable-pane idiom, §4). Responsive
rule: below a width breakpoint (~760px) it **collapses to a single column** showing list *or* details (a
back affordance returns to the list) — never a clipped both. Detailed in §7.2; recorded as a pattern in §11.

---

## 5. F1 — Live Sync

### 5.1 Status chip (bottom StatusStrip)

The single always-visible home for Live Sync, present when `chrome.livesync` is enabled and desktop.
Rendered as a `StatusChip` (§4.1) in the StatusStrip chip region.

```
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ Parsing complete · 41,203 frames        CPU 4%  RAM 512MB       ● CS2 · Following   2 hidden│  ← StatusStrip
└───────────────────────────────────────────────────────────────────────────────────────────┘
   └ StatusText (left)                     └ PerfText (mid)        └ StatusChip     └ HiddenNote
```

Chip states are the §3.1 rows. Examples of the dot+label at the strip's 11px mono scale:

```
 ● CS2 · Off              (TextDim SOLID dim dot — idle; the word "Off" carries the state)
 ◍ CS2 · Connecting…      (AccentInteractive, pulsing)
 ● CS2 · Following        (StatPositive, solid)
 ◌ CS2 · Paused (inferred)(StatPositive HOLLOW RING — the ONLY hollow state; believed-good-but-inferred)
 ● CS2 · Seek unconfirmed (AccentCaution solid dot; label stays neutral TextMid — Degraded)
 ● CS2 · CS2 quit         (AccentError solid dot; label stays neutral TextMid — Faulted)
```

### 5.2 Sync flyout (chip → detail + actions)

`Border.card-flyout` (from `Styles/Cards.axaml`). Content adapts to state; the action set is
state-dependent. Sections: current state (with the honest label), demo binding, plugin/game versions, and
the primary actions.

**Disconnected / Off:**
```
┌─ Live Sync (CS2) ──────────────────────────────┐
│  ○ Off — not connected to CS2.                  │
│                                                 │
│  Enabling Live Sync launches a full CS2 game    │
│  (up to ~2 min to connect) and temporarily      │
│  modifies your CS2 install. It is restored      │
│  when you disable sync or quit.                 │
│                                                 │
│  Demo:  de_dust2 · faceit_2025.dem              │
│  ⚠ Requires a demo with a local file path.      │  ← only if DemoPath is a bare filename (§5.7)
│                                                 │
│   [ Enable Live Sync… ]  (.primary)             │  ← informed launch; disabled if no rooted path / WASM
│   [ Live Sync settings ] (.ghost)               │  ← opens Settings → Live Sync
└─────────────────────────────────────────────────┘
```

**Working (HostStarting → Connecting → LoadingDemo):**
```
┌─ Live Sync (CS2) ──────────────────────────────┐
│  ◍ Connecting…  (this can take up to ~2 min)    │
│  Step: Launching CS2 →  Waiting for plugin      │  ← reflects the sub-state; step text updates live
│  [====------------]  (indeterminate)            │  ← indeterminate ProgressBar (AccentInteractive)
│                                                 │
│   [ Cancel ]  (.ghost)                          │  ← StopSessionAsync (kills CS2, restores install)
└─────────────────────────────────────────────────┘
```

**Synced (Holding / Following):**
```
┌─ Live Sync (CS2) ──────────────────────────────┐
│  ● Following — CS2 is playing; DemoViewer is    │
│    following its tick.                           │
│  Demo:  de_dust2 · faceit_2025.dem  ✓ matched   │
│  Position:  tick 54,321  (round 7)              │
│  Speed:  locked to 1× while synced  (ⓘ why?)    │  ← tooltip: CS2 has no speed command yet (§5.6)
│  ───────────────────────────────────────────── │
│  Plugin  1.0.0-rc.42   ·   Game  14021          │  ← version handshake
│                                                 │
│   [ Re-sync ]  [ Disable Live Sync ]            │  ← Re-sync = re-push DV demo+position; Disable = stop
└─────────────────────────────────────────────────┘
```

**Inferred sub-state (CS2 paused inferred):** identical to Synced but the state line reads
`◌ Paused (inferred) — CS2 stopped sending ticks; it may be paused, at demo end, or unresponsive.` — the
honesty is explicit in prose, matching the hollow-ring dot.

**Degraded:**
```
┌─ Live Sync (CS2) ──────────────────────────────┐
│  ● Seek unconfirmed — CS2 did not confirm the   │  ← AccentCaution
│    last seek; showing CS2's reported position.  │
│  What this means: your seek may not have landed  │
│  exactly where DemoViewer is. Re-sync to realign.│
│                                                 │
│   [ Re-sync ]  (.primary)   [ Disable ] (.ghost)│
└─────────────────────────────────────────────────┘
```

**Error / Faulted:**
```
┌─ Live Sync (CS2) ──────────────────────────────┐
│  ● Disconnected — CS2 quit.                     │  ← AccentError; message varies per failure (§5.8)
│                                                 │
│  Reconnecting relaunches CS2 from scratch        │
│  (up to ~2 min).                                 │  ← sets the expectation the copy MUST set
│                                                 │
│   [ Reconnect (relaunch CS2) ]  (.primary)      │  ← StopSession + StartSession
│   [ Disable Live Sync ]  (.ghost)               │
└─────────────────────────────────────────────────┘
```

- **Reconnect copy is mandatory and specific** — "Reconnect" without "(relaunch CS2)" would misrepresent a
  ~2-min game relaunch as a quick redial (the plugin has no redial loop; reconnect = full
  relaunch). The button label carries the parenthetical; the body restates the ~2-min cost.
- **Versions** surface always in Synced/Degraded; an unknown/never-tested pair shows a small
  `AccentCaution` "untested plugin/game pair" note (a warning, never a block).
- Flyout rows reuse `.ctx-action` for the action buttons where a full-width menu row reads better; the
  primary CTA is `.primary`, secondary `.ghost`.

### 5.3 2D Playback tab integration

The 2D Playback tab (`Modules/Playback2D`) is the surface a user most often watches *while* CS2 leads, so it
gets a lightweight in-context indicator — but it must not duplicate the shell chip's control surface.

- **In-tab CS2 indicator (HUD).** A small chip on the 2D HUD overlay band (top-right of the viewport,
  alongside the existing game-info band), rendered in the **`Pb2d*` HUD palette** for coherence with the
  radar HUD (not the app-chrome ramp — the design-system's walled-off domain rule, D21). It shows only the
  compact state: `CS2 ● Following` / `CS2 ◌ paused (inferred)` / `CS2 ● unconfirmed`, using `Pb2dPositive`
  (green), `Pb2dTeamT`/caution-equivalent, and the HUD text ramp (`Pb2dTextBright`/`Pb2dTextMid`). It is
  **display-only** — clicking it focuses/opens the shell sync flyout (the control center), it does not host
  its own actions. Present only while `chrome.livesync` is enabled AND the 2D tab is active AND state ≠ Off.
- **Following feedback.** When `Following`, the indicator's dot pulses subtly so the user watching the map
  knows CS2 (not the local play loop's user) is the clock master.
- **No second transport.** The 2D tab has no transport of its own; the shell `NavStrip` is the single
  transport. So the speed-lock affordance is on the NavStrip (§5.6), not in the 2D tab.

```
2D Playback viewport (top-right HUD overlay):
                                        ┌──────────────────┐
                                        │  CS2 ● Following  │  ← Pb2d palette; click → shell flyout
                                        └──────────────────┘
```

### 5.4 NavStrip speed-lock affordance

While `Synced`, DV's Speed is locked to 1.0 (CS2 has no speed command yet). The NavStrip
speed `ComboBox` (`Controls/NavStrip.axaml:114-126`) is the affordance.

- Bind the ComboBox `IsEnabled` to a shell gate-shim `IsPlaybackSpeedLocked` (true while
  `LiveSyncState ∈ Synced{*}`), inverted: `IsEnabled="{Binding !IsPlaybackSpeedLocked}"`. On entering
  `Synced`, force `Playback.Speed = 1.0` (engine-side).
- **Locked affordance:** when locked, overlay a small lock glyph (`🔒` or an inline `PathIcon`) at the
  ComboBox's leading edge and set `ToolTip.Tip="Speed is locked to 1× while synced with CS2. CS2 has no
  speed command yet — a future CSVG release will mirror speed both ways."` The disabled Fluent ComboBox
  reads muted (accepted; brightening a disabled control needs a forbidden base `ControlTheme` — same
  decision as the Settings locked-toggle, design-system D9). The lock glyph + tooltip carry the *why*.
- Un-synced, the ComboBox behaves exactly as today. No new tokens (lock glyph is text/`PathIcon`).

```
NavStrip CLOCK group (synced):
  ◀  [ frame 41203 / 65120 · tick 54321 ]  ▶   ⏯   🔒[ 1× ▾ ]  ← disabled + lock glyph + tooltip
```

### 5.5 Enable / disable / lifecycle from the chip

- **Enable** (from Off): flyout `Enable Live Sync…` → engine `HostStarting → … → Synced`. Chip pulses
  through the working states. Disabled if the current demo has no rooted local path (§5.7) or on WASM.
- **Disable** (from any connected state): flyout `Disable Live Sync` → `StopSessionAsync` (kills CS2,
  restores install) → chip returns to `Off`. No confirm needed (disabling is safe and expected).
- **Persisted enable** = the `chrome.livesync` override (whether the *feature/chip* is available), stored in
  `AppSettings.Features.Overrides` via Settings. The **session** (whether CS2 is *running*) is **not**
  persisted — it never auto-launches on app start; the chip always starts `Off`.

### 5.6 Speed lock rationale surfaced (recap)

The tooltip copy in §5.4 is the required "explain why it's locked" affordance. When a future CSVG release
adds `SetDemoTimescaleCommand`, the lock is removed and Speed becomes a mirrored
control-plane property — a pure engine change, the ComboBox binding simply stops reporting locked.

### 5.7 Demo-path guard (no local path)

Some demos (WASM picker, non-local picker) yield a bare filename, not a rooted path
(`_loadedDemoPath = localPath ?? fileName`, MainViewModel.cs:2203). CSVG needs a real host path. When the
current demo's path is not `Path.IsPathRooted(...) && File.Exists(...)`, the flyout shows
`⚠ Requires a demo with a local file path` and the `Enable Live Sync…` / `Re-sync` buttons are disabled with
that tooltip. This is the `Degraded("demo has no local path")` state surfaced honestly.

### 5.8 Error / empty / loading states (F1)

| State | Chip | Flyout message | Actions |
|---|---|---|---|
| Loading (working) | pulsing `AccentInteractive`, step text | "Connecting… (up to ~2 min)" + sub-step + indeterminate bar | Cancel |
| Port 50051 in use | `AccentError` | "Another program is using the CS2 sync port (50051). Close other CSVG tools and retry." | Retry, Disable |
| CS2 launch failed / exited on startup | `AccentError` | "CS2 failed to start (exit code N)." | Reconnect (relaunch CS2), Disable |
| Plugin never READY (CS2 update broke it) | `AccentError` | "The CS2 plugin isn't responding — a CS2 update may have broken CSVG. DemoViewer keeps working normally without sync." | Reconnect, Disable |
| Incompatible plugin/game | `AccentError` | "This CS2 build isn't compatible with the installed CSVG plugin." + versions | Reconnect, Disable, (adv: force) |
| Bad demo path | `AccentCaution` (Degraded) | §5.7 | (Re-sync disabled) |
| CS2 quit / stream dropped | `AccentError` | "CS2 quit." | Reconnect (relaunch CS2), Disable |
| Seek unconfirmed | `AccentCaution` (Degraded) | §5.2 Degraded | Re-sync, Disable |
| Tick silence while believed playing | `StatPositive` **hollow** (inferred) | "Paused (inferred) — see above" | (informational) |
| CS2-side demo change (invisible) | `AccentCaution` (Degraded) | "CS2's demo state is unknown. Re-sync to re-push this demo and position." | Re-sync, Disable |
| App shutdown with live session | — | — | engine `DisposeAsync` on `ShutdownRequested` (no UI) |

**Empty state:** when `chrome.livesync` is enabled but no demo is loaded, the chip shows
`CS2 · Connected (no demo)` if a session is up, else `CS2 · Off`. When no demo AND off, the chip is still
present (feature enabled) but shows `Off`; the flyout's `Enable` is disabled with "Open a demo first."

### 5.9 Settings — Live Sync section

See §10 for the full Settings additions. The Live Sync section holds: **Enable Live Sync** (the
`chrome.livesync` opt-in toggle — the non-dev two-step entry), **Mock mode (developer)**, **CS2 install
path override**, and an **advanced** `Force incompatible plugin` toggle (dev-only, maps to
`ForceIncompatiblePlugin`).

---

## 6. F2 — Analysis Verification ("Verify in CS2")

Seek the live CS2 to a rule-trigger moment (with ~3 s pre-roll) spectating the relevant player, so the user
can eyeball whether the rule caught the right moment. Two affordances, **one shared
action/flow**, **two-level gating**.

### 6.1 Where the affordance lives

- **Analysis tab — rule-trigger nodes / event rows: a context menu on pointer-release.** The Analysis graph
  nodes already use context menus on pointer-release for graph breakpoints; "Verify in CS2" is a new item on
  that same menu — consistent with the established graph-node action idiom, and it avoids adding inline
  chrome to dense graph nodes. For the
  Analysis *event/trigger list rows* (non-graph), the same command is also a right-click context-menu item
  and, where a row already has a hover action rail, an inline `.icon-btn` (a small "CS2" glyph).
  - **MSAGL caveat (must state):** the Analysis graph hosts a live MSAGL `GraphView` that does **not** settle
    geometry headlessly (design-system §3, §4 capture notes). So this affordance **cannot be
    screenshot-verified** in the capture loop — validate it via an annotated static mockup or in-app, never a
    promised headless before/after. This is the same limitation already recorded for graph breakpoints.
- **Highlights tab — highlight rows: an inline "Verify live" button** (§7.4). A visible `.ghost` button on
  each highlight row (rows are not dense graph nodes; an always-visible button reads better than a hidden
  context menu here).

**Consistency requirement (task):** both affordances invoke the same
`ILiveSyncService.VerifyMomentAsync(gameTick, preRoll≈192, postRoll≈64, spectateName)` and present the
**same SeekPending → arrived feedback** (§6.4). The label differs by context ("Verify in CS2" on a rule
node; "Verify live" on a highlight row) but the flow is identical.

### 6.2 Two-level gating (present/absent vs enabled/disabled)

Spell both out — they blur otherwise:

1. **Present vs absent** is governed by `chrome.livesync`. When the feature is **off** (power/consumer
   default), the Verify affordances are **absent** — no menu item, no button. They are not shown-then-disabled
   for users who never opted into Live Sync.
2. **Enabled vs disabled+prompt** exists only when `chrome.livesync` is **on**. The affordance is:
   - **Enabled** when there is a live `Synced` session **for the current demo**.
   - **Disabled + prompt** otherwise — e.g. no session yet, or the session is on a different demo. The
     disabled control's tooltip / an inline prompt reads: *"Enable Live Sync to verify this moment in CS2."*
     Clicking the prompt opens the sync flyout at its `Enable Live Sync…` action (same engine paths — the
     verify request can queue behind the launch).

### 6.3 Disabled / prompt state (wireframe)

```
Highlight row (chrome.livesync ON, no session):
  ┌──────────────────────────────────────────────────────────────────────┐
  │ ☐  s1mple — 2 kills after the plant (round 7)   tick 54,321   ~20s    │
  │                                        [ Verify live ]  ⓘ enable sync │  ← button disabled, prompt tooltip
  └──────────────────────────────────────────────────────────────────────┘

Highlight row (chrome.livesync OFF):
  ┌──────────────────────────────────────────────────────────────────────┐
  │ ☐  s1mple — 2 kills after the plant (round 7)   tick 54,321   ~20s    │  ← no Verify affordance at all
  └──────────────────────────────────────────────────────────────────────┘
```

### 6.4 SeekPending → arrived feedback

When Verify fires:
- The invoking control enters a busy state: label → `Verifying…`, a small pulsing dot (reuse the SeekPending
  glyph from §3.1). The **shell sync chip** simultaneously shows `CS2 · Seeking…` (SeekPending), so the
  feedback is visible even if the user's cursor is elsewhere.
- On arrival (CSVG `PlayDemoTickRangeAsync` completes deterministically paused), the
  control returns to `Verify live` / `Verify in CS2`, and the chip returns to `Synced · Holding`. The
  Analysis/Highlights local playhead is remote-applied to the trigger frame so DV's own
  2D view lands on the same moment.
- On failure (session dropped mid-seek): the chip goes to `Error/Faulted`; the invoking control returns to
  its normal label; the prompt tooltip reappears. No separate error dialog (no toast system) — the chip is
  the failure surface.

---

## 7. F3 — Highlights tab

A new top-level workspace tab, Library-like, browsing analysis-generated highlights across the whole library,
with a net-new master-detail drill-in and a reel-generation footer.

### 7.1 Registration, gating, WASM

- New `IWorkspaceModule` `Modules/Highlights/HighlightsModule`, TabId `"highlights.browser"`, `Order` = 3
  (after Stats; Library is -1, Playback2D 4, Workbench 5), `Placement = Main`,
  **`ViewModelFactory`** (not `DataContext` — the DataContext branch skips the lifecycle),
  `ViewFactory = () => new HighlightsTabView()`. Registered in `App.BuildRegistry` (App.axaml.cs:300-313).
- Gate: `tab.highlights` (§2.1) + the `TabFeatureIds` entry. Default-visible to all (viewing surface).
- The VM is shell-constructed with delegates (Library precedent): the highlights cache store,
  `LoadDemoFromPathAsync` (open-in-workspace), `SwitchTo`, and the `ILiveSyncService` (for Verify) +
  `IWindowService.ShowHighlightReelDialog` (reel). All injected — the tab VM owns no engine.
- **WASM (registered, degraded):** on browser, the cache/scan are absent (`AppPaths.ConfigRoot` null); the
  tab shows only the currently-open demo's in-memory harvested highlights with an explanatory note.
  Filters, background-scan opt-in, reel, and verify are suppressed (`OperatingSystem.IsBrowser()` guards,
  mirroring `LibraryTabViewModel.CanDropFiles`).

### 7.2 Master-detail layout (net-new pattern)

```
┌─ Highlights ────────────────────────────────────────────────────────────────────────────────┐
│ Filters:  [ 🔍 search ]  [ Players ▾ (3) ]  [ Types ▾ ]  [ Maps ▾ ]     ⟳ scan: 12 queued    │  ← toolbar (WrapPanel, reflows)
├───────────────────────────────────┬──┬────────────────────────────────────────────────────────┤
│  MASTER LIST (demos w/ highlights) │▓▓│  DETAILS (selected demo)                               │
│  ┌───────────────────────────────┐ │▓▓│  de_dust2 · faceit_2025.dem · 2025-06-14 · 24 clips    │
│  │ [radar] de_dust2              │ │▓▓│  ─────────────────────────────────────────────────────│
│  │  faceit_2025.dem   ● 24       │ │▓▓│  ▸ s1mple            (T)   4 highlights                │  ← PlayerHighlightGroup
│  │  s1mple, ZywOo… · 6 players   │ │▓▓│    ☐ 2 kills after the plant (round 7) tick54321 ~20s  │
│  │  ⚠ highlights outdated        │ │▓▓│                              [ Verify live ]           │  ← HighlightEventRow
│  └───────────────────────────────┘ │▓▓│    ☐ ace (round 12)          tick61200  ~22s [Verify] │
│  ┌───────────────────────────────┐ │▓▓│  ▸ ZywOo             (CT)   2 highlights                │
│  │ [radar] de_nuke   ● 9         │ │▓▓│    ☐ 3k retake (round 4)     tick30110  ~18s [Verify]  │
│  │  scan_pending.dem  ◐ scanning │ │▓▓│                                                        │
│  └───────────────────────────────┘ │▓▓│                                                        │
│  … (virtualized card rows)        │▓▓│                                                        │
├───────────────────────────────────┴──┴────────────────────────────────────────────────────────┤
│  3 highlights selected                                          [ Create Highlight Reel ]  (.primary)│  ← footer
└───────────────────────────────────────────────────────────────────────────────────────────────┘
                                     └ GridSplitter (Auto col)
```

- **Grid:** `ColumnDefinitions="*,Auto,1.4*"`; `GridSplitter` in the `Auto` column (design-system §4). List
  gets `*`, details `1.4*` (details is content-denser). Splitter position persisted in the tab VM session
  state (`SnapshotState`/`RestoreState`).
- **Responsive collapse (~760px):** below the breakpoint, collapse to **one column** — show the master list;
  selecting a demo swaps to a details view with a `◀ Back to list` affordance. Never a clipped both. (Same
  discipline as the design-system's responsive-strip rule §4, applied to a split.) A bound
  `IsNarrow`/`ShowDetailPane` VM flag toggles the layout (reactive, not code-behind).
- **Footer** docked `Bottom` (`Border.sectionHeader` band): `N highlights selected` + `Create Highlight
  Reel` (`.primary`, enabled when ≥1 selected).

### 7.3 Master list (reuse Library patterns)

- **Cards** reuse the Library card machinery: `MapRadarConverter` radar thumbnail
  (`ViewModels/Library/MapRadarConverter.cs`), `MapAccentConverter` accent, `LibraryCard*` overlay palette
  (white-on-thumbnail ramp), and the **`CardRow` chunked virtualization** (`LibraryTabViewModel.cs:594-607`)
  driven by `SizeChanged` column recompute (`LibraryTabView.axaml.cs:109-125`) — WrapPanel has no
  virtualizing counterpart, so this is required for a 200+ demo library.
- **Player names** route through `DisplayText.Sanitize` (`Modules/Library/DisplayText.cs`) everywhere they
  render — hostile bidi/combining-mark names crash Avalonia's wrap splitter. This applies to
  card summaries, the Players filter, and every details-pane row.
- **Filters (toolbar):** reuse the Library filter idioms — multi-select **Players** (aggregated across all
  cache rows, keyed by `steamId64`, sanitized display, **with counts**), **highlight-type chips**
  (`{rulesetId}.{highlightId}`), **Maps** (`MapFilterItem` pattern, `LibraryTabViewModel.cs:53-62`), and
  free-text. Toolbar is a **fill `WrapPanel`** (design-system D35 — reflows to a second row at narrow width;
  chosen over scroll for a filter toolbar). Filter chips are `Button.chip`/checkbox flyouts reusing
  `card-flyout` + `.ctx-action` + `.col-label`.
- **Per-card badges:**
  - Highlight count pill (`Border.badge`).
  - **Staleness badge** — `⚠ highlights outdated — rescan` (`AccentCaution` text) when the demo's
    `configFingerprint` ≠ the current composed-rules fingerprint. Clicking it queues a
    rescan of that demo.
  - **scanState chip** — `Indexed` (no chip / subtle), `◐ scanning` (the Library's single animated amber
    bar / pulsing dot precedent, `DemoLibraryModels.cs:101-106`; at most one card scanning at a time),
    `queued`, `✕ Failed — retry` (`AccentError`, click to requeue).

### 7.4 Details pane

- **Player groups** — `PlayerHighlightGroup` per player: an expander header `▸ s1mple (T) · 4 highlights`
  (sanitized name, team chip via the bound-class→token team pattern, totals). Reuse the `.sectionHeader` /
  group-label idiom for the header band.
- **Highlight rows** — `HighlightEventRow`, one per firing:
  ```
  ☐  <rendered title>                          <round>   tick <T>   ~<Ns>   [ Verify live ]
  ☐  2 kills after the plant (round 7)         round 7   tick 54321  ~20s   [ Verify live ]
  ```
  - **Rendered title** = the `HighlightFired.RenderedTitle` (e.g. "s1mple — 2 kills after the plant
    (round 7)"), rendered at emission time. Note `StateNode.Name` is a display name, not the
    rule id — label from the rendered title, not the node id.
  - **Estimated clip window** (`~20s`) = the computed lead-in + lead-out around the event tick.
    Shown so the user understands what a reel clip will cover before selecting.
  - **Selection checkbox** feeds the reel selection set (footer count). Selection lives in the **tab VM**
    (§7.7).
  - **Verify live** button — F2 (§6.1/§6.2/§6.4). Present per `chrome.livesync`; enabled per live session.
- Details header: demo name, map, date, total clip count; a `Open in workspace` affordance (double-click a
  card, or a header button) → `LoadDemoFromPathAsync(path)` + switch tab (Library's `OpenEntryAsync`
  delegate pattern).

### 7.5 Background scan opt-in + progress

- **Opt-in setting.** A 200-demo library ≈ 30 min of background churn, so auto-scan is
  **opt-in via Settings → Highlights → "Scan library for highlights in the background"** (default off, all
  categories). Off = the tab still shows the currently-open demo's harvested highlights and any
  previously-cached rows; on = the coordinated backfill queue runs (`HeavyJobGate`, one heavy parse
  machine-wide).
- **Progress affordances (two, reusing precedents):**
  1. **Per-card** `◐ scanning` amber bar on the currently-indexing card (Library precedent — at most one at a
     time, `DemoLibraryModels.cs:101-106`).
  2. **Queue length in the toolbar / status strip** — `⟳ scan: 12 queued` in the tab toolbar, and (when the
     tab is inactive) the shell status strip can carry it via `StatusText`/`RightText`. No new
     job-queue framework — reuse the `IsIndexing`/`RaiseChanged` pattern.
- **Rescan triggers:** app start, tab activation (`OnActivated`, Library precedent
  `LibraryTabViewModel.cs:223-231`), rule-file save in the Authoring Workbench, and a manual **Rescan** (per
  card via the staleness badge, or a toolbar `⟳ Rescan all`).

### 7.6 Empty / loading / failed states

| State | Presentation |
|---|---|
| No cached highlights, scan off, no open demo | Centered `Border.card` hero (Library landing precedent): "No highlights indexed yet." → `[ Scan my library ]` (`.primary`, enables the opt-in + starts scan) + "or open a demo to see its highlights" pointer. |
| Scan on, indexing, nothing done yet | Same hero but "Indexing your library for highlights… 12 of 210 demos" + an amber bar; cards appear live as rows complete (`Changed` event subscription). |
| Rows present, a demo failed | That card shows `✕ Failed — retry` (`AccentError`); the rest browse normally. |
| Filters exclude everything | "No highlights match these filters." + `Clear filters` (`.ghost`) — per-tab empty state, not global (design-system §4). |
| WASM | "Library-wide highlights need the desktop app. Showing highlights for the open demo." + the open-demo rows only. |

### 7.7 Tab VM state retention

Per `WorkspaceTabDescriptor`, the View is torn down on every deactivation and the VM persists.
Therefore **all** of: filter selections, the selected demo, expanded player groups, the reel selection-set
(checkboxes), scroll position, and the splitter position live in `HighlightsTabViewModel` — never in
code-behind or view state. Losing any of them across a tab switch is a bug (persona: state retention).
`SnapshotState`/`RestoreState` persist the splitter width and active filters across sessions.

### 7.8 Footer → Create Highlight Reel

`Create Highlight Reel` (`.primary`) enabled when ≥1 highlight is selected → opens the modal reel dialog
(§8) with the selected clips. Disabled (with a tooltip "Select at least one highlight") when none selected.

---

## 8. F3b — Create-Reel dialog

Modal config dialog (precedent: `FirstRunWizard` via `IWindowService.ShowDialog(owner)`, closed by the VM's
`Completed` event — `Services/IWindowService.cs:121-143`). Add
`IWindowService.ShowHighlightReelDialog(HighlightReelViewModel)`. WASM: absent (whole feature desktop-gated).

### 8.1 Dialog layout

```
┌─ Create Highlight Reel ─────────────────────────────────────────────────────────────────┐
│  CLIPS (3 selected · 2 after merge)                                                       │
│  ┌──────────────────────────────────────────────────────────────────────────────────┐  │
│  │ s1mple · de_dust2                                                                   │  │
│  │  ┌ 2 kills after the plant (round 7)   ticks 54,105–54,650  (~8.5s)  ⎫             │  │  ← coalesced:
│  │  ┕ ace (round 7)                       ticks 54,400–54,980  (~9.1s)  ⎭ merged →     │  │    one clip
│  │      → merged clip: ticks 54,105–54,980  (~13.7s)                                   │  │
│  │ ZywOo · de_nuke                                                                     │  │
│  │  · 3k retake (round 4)                 ticks 29,900–30,500  (~9.4s)   ⚠ demo moved  │  │  ← per-row pre-flight error
│  └──────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                          │
│  PADDING           Lead-in [ 15 ]s   Lead-out [ 5 ]s     ☐ Don't cross round start       │
│  DISPLAY           Preset: ( Default ▾ )   ☑X-ray ☑HUD ☐True-view ☑Assists ☐Death-only  │
│  OUTPUT            Folder [ …/Reels ] [Browse]  Name [ dust2_s1mple ]  Format ( mp4 ▾ )   │
│                    FPS ( 60 ▾ )   ☑ Concatenate into one video   ☑ Capture audio         │
│  ENCODING          ( ● CRF [ 20 ]   ○ Bitrate [     ] kbps )   ← mutually exclusive       │
│                                                                                          │
│  ⚠ 1 clip has a problem (demo moved). Fix or deselect it to continue.                     │  ← inline pre-flight
│  ─────────────────────────────────────────────────────────────────────────────────────  │
│                                                        [ Cancel ]   [ Generate reel ]      │  ← platform-gated label
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

### 8.2 Selected clips + coalescing display

- The clip list is grouped by (player, demo). Each clip shows player, title, round, and the **computed tick
  window** + estimated seconds. Ticks shown in demo-tick space (the space CSVG uses).
- **Coalescing is shown, not silent.** Overlapping clips for the same player+round auto-coalesce.
  The dialog renders the merge visibly: the contributing rows are bracketed (`⎫⎭`) and collapsed under a
  `→ merged clip: ticks A–B (~Ns)` line, so the user sees *why* 3 selected highlights became 2 clips (the
  header reads `3 selected · 2 after merge`). A tooltip on the merge line lists the merged sources.

### 8.3 Padding controls

- **Lead-in** (default **15 s**) and **Lead-out** (default **5 s**) numeric fields (`.field`). Copy note
  under them: *"Highlights fire at the end of the action, so lead-in covers the build-up."*
- Optional **☐ Don't cross round start** — clamps lead-in to the round-start tick. Off by
  default (some highlights want pre-round context).
- Per-highlight-type default overrides are remembered in Settings (§10).

### 8.4 Display flags + presets

- Flags (`Cs2ClipOptions`): **X-ray, HUD, True-view, Assists-in-feed, Only-death-notices** —
  `CheckBox`es. Note the semantic: unset flags actively turn features **off** — so the preset
  seeds a sensible baseline.
- **Presets** `Default` / `NoHudDefault` (`ComboBox`, `.field`). Selecting a preset sets the checkboxes;
  editing a checkbox switches the preset label to `Custom`.

### 8.5 Output + encoding

- **Output:** Folder (`Browse` → `IStorageProvider` folder picker, desktop), Base file name (default
  suggestion `<demoName>_<playerName>`), Container `Format` (`ComboBox`), Frame rate `FPS` (`ComboBox`),
  **☑ Concatenate into one video** (default on), **☑ Capture audio**.
- **Encoding — CRF XOR Bitrate, enforced in the UI.** A two-radio group (`● CRF` / `○ Bitrate`); the
  inactive one's numeric field is disabled. Selecting one clears/disables the other so an invalid both-set
  state is structurally impossible (mirrors `Cs2Compilation`'s Crf⊕VideoBitrate exclusivity).

### 8.6 Pre-flight validation (inline)

- Run `Cs2Compilation.Validate()` locally on open and on any edit; surface issues **inline**, never as a
  post-submit throw:
  - **Demo moved/deleted** (`DemoFilePath` must exist) → a per-row `⚠ demo moved` error + a
    dialog-level banner; that clip is excluded from generation until fixed/deselected.
  - **Nothing selected / all excluded** → `Generate` disabled.
  - Bad tick window / output path issues → inline field errors.
- `Generate` is enabled only when the compilation validates and ≥1 clip remains.
- **Error styling (contrast-driven, §3.2):** `AccentError` as small *body text* on the dark card surface
  measures **4.19:1 — below AA** (§3.2 computed). So a per-row/banner error is an **`AccentError` icon (`⚠`)
  + the message in neutral `TextMid`/`TextValue`**, not red body text. The icon is a graphical object (3:1
  is enough, and `AccentError` clears it on every card surface); the message text stays high-contrast.

### 8.7 Generate → background job handoff (fork resolved)

**Resolved fork: the modal dialog is for configuration + pre-flight only; on `Generate` it validates, then
closes and hands the job to a background `ReelJobService` surfaced by a Reel status-chip (§8.8).**

- **Why not keep the dialog open with a progress page:** the dialog is modal (blocks the whole app,
  `FirstRunWizard` precedent). Reel generation launches CS2 + OBS and can run for *minutes*; a modal that
  locks the entire app for that long is hostile. Closing to a background job keeps the app usable (browse
  more highlights, inspect demos) during the render.
- **Why not a toast:** there is no toast/notification system. The status-strip chip + flyout is
  the app's idiom for persistent background activity — and it's the *same* pattern as the Live Sync chip
  (§4.1), so the user already knows how to read it.
- **Handoff moment:** on `Generate`, the dialog closes and the **Reel chip** appears in the status strip,
  pulsing, so the transition is visible (dialog gone → chip present). No auto-opening flyout (intrusive); the
  pulsing chip is the "it started" signal.
- *(Alternative kept in reserve: a non-modal progress panel docked like the Output drawer. Rejected for v1 —
  the chip+flyout reuses an existing pattern and costs no new chrome; revisit if reel jobs need richer
  always-visible progress.)*

### 8.8 Reel job chip + flyout (progress, cancel, retry)

A second `StatusChip` (§4.1) instance, present only while a reel job is active.

```
StatusStrip:  … PerfText …    ● CS2 · Following   ◐ Reel 3/8 · encoding…   2 hidden
                                                   └ Reel job chip (pulsing)

Reel chip flyout:
┌─ Highlight reel ───────────────────────────────┐
│  Rendering clip 3 of 8                          │
│  [==============------]  clip 3  (~9s)          │  ← intra-clip progress (CaptureProgressUpdated)
│  ─────────────────────────────────────────────  │
│  ✓ 1  s1mple · plant 2k          done           │
│  ✓ 2  s1mple · ace              done            │
│  ◐ 3  ZywOo · 3k retake         encoding…       │
│  · 4  …                          queued          │
│  … (per-clip status list)                        │
│  ─────────────────────────────────────────────  │
│   [ Cancel reel ]  (.ghost)                     │
└─────────────────────────────────────────────────┘
```

- **Progress model** = the `AnalysisViewModel.RunAsync` IsRunning/Progress/cancel template
  (`AnalysisViewModel.cs:485-540`): clip-level `k of N` from `CompilationClipStarted`/`Completed`, intra-clip
  bar from `CaptureProgressUpdated` (a synchronous `Action` on the gRPC read loop — the handler must only
  `Dispatcher.UIThread.Post`, never block).
- **Fail-fast surfacing.** Compilation is fail-fast on the first failed clip. On failure the
  chip goes `AccentError`, the flyout marks the failed clip `✕`, and offers **`Retry remaining`** (re-submits
  the unfinished clips as a new compilation). Note the cross-run concat caveat (CSVG doesn't concat old+new
  files across runs) — the flyout states that retried clips produce separate files unless DV
  runs its own concat.
- **Cancel** stops the job (`ct`) and, if a session was started for the reel, tears it down
  (`StopSessionAsync`).

### 8.9 Platform gating — macOS dry-run

- **Windows / Linux (real):** `Generate reel` runs `CaptureCompilationAsync` with
  `StartSessionAsync(initializeCapture:true)` (needs OBS). If OBS is unavailable, the button's pre-flight
  reports it inline and the primary is disabled with "Real reels need OBS on Windows or Linux."
- **macOS (dry run, developer-labelled):** the primary button becomes **`Dry run (mock)`** with a caption
  *"Developer/testing — walks the clip plan without recording."* It starts a mock session
  (`MockMode=true`, `initializeCapture:false`) and steps the clip plan
  (`SetDemoOptions → SetSpectatorTarget → PlayDemoTickRange(record:false)`), validating command plumbing +
  tick math end-to-end. Progress uses the same chip; the flyout header reads `Dry run` and each
  "done" is `✓ (mock)`. The `Dry run (mock)` styling is deliberately understated (`.ghost`-adjacent, a
  `AccentCaution` "mock" tag) so it never masquerades as a real render.
- **WASM:** the whole dialog is absent.

---

## 9. Cross-cutting — the F1 ↔ F3b single-CS2 interlock

Load-bearing: there is **one** CSVG orchestrator / port 50051 / CS2 instance per machine.
Live Sync starts its session with `initializeCapture:false`; reel generation **requires**
`initializeCapture:true`. They therefore **cannot coexist** — a reel while Live Sync is active is not an
"also record," it is a **full CS2 relaunch with recording that suspends Live Sync**. The UX must make this
explicit, not surprising.

**Two reel-start paths, designed:**

1. **Cold (no live session).** The reel `Generate` cold-starts its own session. Dialog copy on `Generate`
   hover / a pre-generate confirm line: *"This launches CS2 with recording (up to ~2 min)."* Straightforward.

2. **Hot-but-incompatible (Live Sync is active).** The reel needs recording, which the live session doesn't
   have. `Generate` shows an informed confirm:
   > *"Generating a reel restarts CS2 with recording and pauses Live Sync (up to ~2 min). Live Sync stays
   > paused until the reel finishes, then you can reconnect."*
   On confirm, `ReelJobService`: `StopSessionAsync` (kills the sync CS2, restores install) →
   `StartSessionAsync(initializeCapture:true)` → run compilation → `StopSessionAsync`. During this window:
   - The **Live Sync chip enters `Suspended (reel render)`** (§3.1 last row: `TextDim` dot, `CS2 · Paused for
     reel render`), and its flyout's actions are disabled with "Live Sync is paused while a reel renders."
   - The **Reel chip** drives the progress (§8.8).
   - After the reel finishes, Live Sync does **not** auto-relaunch (never auto-launch CS2, principle 1); the
     sync chip returns to `Off` with a `Reconnect` prompt (the user chooses to bring the game back).

**Mutual-exclusion guards:**
- While a reel job is active, the sync flyout's `Enable/Reconnect` is disabled ("A reel is rendering").
- While Live Sync is `Synced`, starting a reel routes through the hot-incompatible confirm above.
- Only **one reel job at a time**; the second `Create Reel` is disabled while a job runs.
- The **`HeavyJobGate`** pauses the background highlight scan for the duration (CS2 + OBS
  must not compete with a background parse on a 16 GB box).

This is why the two status chips are *coordinated*, not independent: the StatusStrip chip region (§4.1) shows
both, and the sync chip's `Suspended` state is driven by the reel job's lifecycle.

---

## 10. Settings additions

`Views/Settings/SettingsView.axaml` is one vertical `ScrollViewer` of `sectionHeader`-banded sections,
shared by the desktop window and the WASM overlay (design-system §5 "Settings screen layout"). Add two new
sections after the existing four; each body is a `Border.card`. Both are **desktop-only** — on WASM the
sections are suppressed (`OperatingSystem.IsBrowser()`), consistent with `CanManageThemes`/`CanAddFolder`.
Persist as new **defaulted** sections on `AppSettings` (binder-safe shapes); reel-config record
shapes use the `WriteSection`/`ReadSection` pattern.

**LIVE SYNC (CS2)** — desktop only:
```
┌─ LIVE SYNC (CS2) ───────────────────────────────────────────────┐
│  Live Sync watches your demo in a real CS2 game via CSVG.        │
│  It launches CS2 (~2 min) and temporarily modifies your install. │
│                                                                 │
│  Enable Live Sync                                    [ ⬤ on/off ]│  ← the chrome.livesync opt-in (non-dev 2-step)
│  Mock mode (developer)                               [ ○ on/off ]│  ← MockMode; dev-labelled
│  CS2 install path                    [ /path/to/cs2… ] [Browse]  │  ← Cs2RootInstallationDirectory override
│                                                                 │
│  ▸ Advanced (developer)                                          │  ← collapsed by default
│      Force incompatible plugin                       [ ○ on/off ]│  ← ForceIncompatiblePlugin
└─────────────────────────────────────────────────────────────────┘
```
- **Enable Live Sync** writes the `chrome.livesync` override (the live-reconcile path, design-system §5 →
  the chip appears/disappears). It does **not** start a session.
- **Mock mode** and **Advanced → Force incompatible plugin** are developer-labelled (small "developer" chip),
  visible to all but clearly marked as testing/expert controls.
- **CS2 install path** override (defaulted empty = auto-detect); `Browse` uses the storage-provider folder
  picker (desktop code-behind handoff, the only Settings code-behind).

**HIGHLIGHTS** — desktop only:
```
┌─ HIGHLIGHTS ────────────────────────────────────────────────────┐
│  Scan library for highlights in the background        [ ○ on/off]│  ← opt-in (all categories); default OFF
│  (A large library can take ~30 min to index.)                    │
│                                                                 │
│  REEL DEFAULTS                                                   │
│  Output folder                        [ …/Reels ]      [Browse]  │
│  Container format ( mp4 ▾ )   FPS ( 60 ▾ )   Lead-in [15]s  out[5]s│
│  ☑ Concatenate into one video    ☑ Capture audio                 │
│  Encoding ( ● CRF [20]  ○ Bitrate [ ] )                          │
└─────────────────────────────────────────────────────────────────┘
```
- **Background scan** is the §7.5 opt-in.
- **Reel defaults** seed the reel dialog (§8), so a user sets output/encoding preferences once. Per-highlight-
  type padding overrides are remembered here too.

**Revisit trigger (design-system §5):** these two new sections push the Settings section count to **six** —
right at the documented "adopt the 2-tab `TabControl` split" trigger. Flag for the next Settings pass: if a
seventh section or a second long section lands, adopt the proven 2-tab split (General grab-bag | Features),
or a left-rail `SplitView`. Not triggered *by* this change alone (six is the threshold, not past it), but
worth noting the headroom is now gone.

---

## 11. Theming, tokens, and design-system additions

### 11.1 Tokens — zero new required

The Live Sync state palette maps entirely onto existing accents (§3.1): `AccentInteractive` (working),
`StatPositive` (synced/following/holding), `AccentCaution` (degraded), `AccentError` (faulted), `TextDim`
(off/suspended), `TextMid` (labels). The reel chip reuses the same. The 2D-tab indicator uses the existing
`Pb2d*` HUD palette. The Highlights tab reuses the `LibraryCard*` overlay palette and app-chrome ramp. **No
new tokens.** Every colour is `{DynamicResource}`; stateful/per-state colour is a bound class → token
selector (the `Border.teamChip` pattern), never a code-held brush.

If, at implementation, a genuinely new colour is needed (e.g. a dedicated "beta/mock" tint distinct from
`AccentCaution`), add it to **both** the `[Dark]` and `[Light]` `ThemeDictionaries` in
`Styles/DarkPalette.axaml` (identical key sets) — it then flows to High Contrast / E-Girl / drop-ins
automatically. Do not edit `Themes/*.json`.

### 11.2 Cross-theme verification

**Token-vs-surface contrast is computed, not deferred (§3.2)** — across all four built-in themes, for every
label and dot pairing. Decisions it drove, so the implementer doesn't re-open them:
- **Labels are neutral `TextMid`** (only universally-AA-safe choice; `AccentCaution` label fails Light at
  3.95, `AccentError` label fragile on Dark at 4.52).
- **State is word-carried, dot is redundant colour** — legitimises the two sub-3:1 dark dots (working
  `AccentInteractive` 2.72, off `TextDim` 1.96).
- **Reel-dialog errors = `AccentError` icon + neutral message text** (`AccentError` body text on the dark
  card is 4.19, below AA — §8.6).

**Genuinely render-only items left for the screenshot loop** (can't be computed — code-drawn or AA'd glyphs):
- The 2D-tab `Pb2d*` indicator on the dark HUD overlay (a dark island in every theme — low risk; verify
  `Pb2dPositive` legibility).
- The anti-aliased **dot glyphs at 8px** and the hollow-ring vs solid-dot distinguishability (a shape
  distinction, verify it reads at small size).
Render each new coloured surface under `--theme light | dark | high-contrast | egirl` and read back
(design-system §12 loop). Capturable headlessly (plain-VM controls) **except** the F2 Analysis-graph context
menu (live MSAGL — §6.1).

### 11.3 design-system.md additions

Two new entries appended to `docs/ui/design-system.md` (no rewrites):
1. **`StatusChip` shared control + StatusStrip chip region** — component contract (§4.1 here): purpose,
   VM surface, token-driven states, the flyout idiom, the ≥2× justification (Live Sync + Reel job).
2. **Master-detail split layout pattern** — layout pattern (§4.2 / §7.2 here): `Grid *,Auto,1.4*` +
   `GridSplitter`, responsive single-column collapse below ~760px, state in the tab VM.
Plus the category-visibility matrix rows from §2.5.

---

## 12. Summary of resolved forks

| Fork | Options | Resolution | Rationale |
|---|---|---|---|
| Live Sync default category | power-on vs power-off | **power/consumer OFF, dev ON** (§2.3) | Modifies real CS2 install; inferred states; DV-restart papercut → beta-grade. Superset preserved (one Settings toggle). |
| Reel-generation gating | ungated vs `highlights.reel` power+ | **Ungated, visible to all** (§2.2) | Marquee consumer payoff; dialog + platform + interlock are the guards. |
| Reel progress home | modal stays open vs background chip | **Background job + Reel status-chip** (§8.7) | Multi-minute modal would lock the app; reuses the sync-chip pattern; no toast system. |
| F2 Analysis affordance | inline icon vs context menu | **Context menu on pointer-release** (graph nodes) + inline button (Highlights rows) (§6.1) | Matches the established graph-node action idiom; rows aren't dense. |
| Highlights on WASM | unregister vs degrade | **Register, degrade to open-demo-only** (§1, §7.1) | Mirrors the Library tab; honest minimal experience over a silently-vanishing tab. |
| Inferred vs Degraded visual | one caution colour vs distinct | **Hollow ring + `(inferred)` (green) ≠ solid caution (Degraded)** (§3.1) | Two honesty levels must read differently (principle 2). |
| Master-detail at narrow width | clipped both vs collapse | **Collapse to single column with Back** (§7.2) | Design-system responsive discipline; never a clipped both. |

## 13. Open questions / owner decisions owed

- **GPL-3.0 CSVG Core linked into DemoViewer.NET** has distribution licensing implications.
  Not a UX call, but it gates whether F1/F3b ship in-proc or via the CLI/JSON seam — flag for the
  owner. (If CLI-seam, the reel job's progress model changes from in-proc events to process stdout parsing;
  the chip UX is unaffected.)
- **Power-user default flip trigger:** when the CSVG protocol items land (exact `is_paused`/
  `is_playing_demo`, demo-identity event, plugin redial), reconsider `chrome.livesync`
  `power: true`. Re-evaluate §2.3 then.
- **Reel cross-run concat** (retry-remaining produces separate files, §8.8) — whether DV runs its own FFmpeg
  concat or CSVG adds resumable compilation affects the "Retry remaining" copy. v1 states the
  limitation; a later pass can hide it.
- **Verify-live spectator name fragility:** CSVG spectates by exact display name. Mid-match
  renames vs `ParsedDemo` final-state names can mis-target. v1 uses the raw stored name; note in the Verify
  flyout tooltip if a target can't be resolved. Removed when CSVG SteamID targeting lands.
