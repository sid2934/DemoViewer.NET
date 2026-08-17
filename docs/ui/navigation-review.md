# Navigation Review — tick / frame / round / game-event / breakpoint

This is an as-built record. The direction it recommends was picked and shipped on `main` — the single
shell-owned nav strip with a `SemanticNavigator` boundary service, converged event-filter flyout, and
retirement of the per-tab `SeekControls`. The code cites this doc's phase labels as design rationale
(`navigation-review Phase A–D` in `MainViewModel.cs`, `SemanticNavigator`, `NavStrip`, and their
tests). The options survey below is retained as the historical rationale for *why* that direction was
chosen — it is no longer a menu of open choices.
**Branch verified against:** `feature/modular-ui-framework` (checked out at time of review).
**Scope:** App-layer navigation only. Protected parser files (`DemoParser.cs`, `DemoFrame.cs`,
`BitBuffer.cs`, `LEB128Utils.cs`) are untouched by every option here.

---

## 1. Current-state map (verified, not assumed)

There are **three** position-movement surfaces and **two** independent game-event filter
mechanisms in the app today. Navigation is inconsistent because the most capable surface and the
better filter mechanism are **not rendered at all**.

### 1.1 The clock — already centralized (do not reinvent)

`PlaybackController` (`ViewModels/Playback/PlaybackController.cs`) is the single authoritative owner
of "current position." Every position move routes through it:

- Observable state: `CurrentFrameIndex` / `CurrentTick` / `IsPlaying` / `Speed`.
- Movement primitives: `SeekToFrame(int)`, `SeekToTick(int)`, `StepForward()`, `StepBack()`,
  `SeekToEnd()`, `Play()` / `Pause()` / `TogglePlay()`.
- Owns the authoritative `EntityTracker` and the `DispatcherTimer` play loop; fans out via
  shell-wired delegates (`ApplySeek` / `ApplyLightSeek`) under a re-entrancy guard.

This layer is done. Every option below **builds on it** and adds nothing to its movement contract
except, at most, two thin convenience entry points.

> Note: `docs/ui/modular-ui-design.md` §3.4 sketched a `StepTick(int dir)` on the controller. It was
> **never implemented** — the controller has no tick/round/event awareness today. That is the gap
> this review addresses, and §3.3 of that doc already endorses the consolidation direction (it
> consolidated the *clock* but explicitly left `SeekControlsViewModel` "keeps its view" and never
> resolved the round/event duplication).

### 1.2 Semantic navigation — the scattered layer (the actual problem)

"Semantic" nav = next/prev that must know where *boundaries* are (tick group / round / game-event).
Unlike the clock, this needs **content knowledge**, and it is duplicated across two view-models with
two different filter sources, only one of which is rendered.

**Surface A — `SeekControlsViewModel` (`ViewModels/SeekControlsViewModel.cs`), rendered via
`Views/SeekControls.axaml`.** Full symmetric prev/next pairs for frame / tick / round /
special(game-event), plus an editable frame-number box. Wired in the **shell**
(`MainViewModel` ctor, ~L340) to shell methods:

| Button | Shell method (`MainViewModel`) | Filter source |
|---|---|---|
| prev/next frame | `_playback.StepBack` / `_playback.StepForward` | — |
| prev/next tick | `PreviousFrameByTick` (L1921) / `NextFrameByTick` (L1552) | — |
| prev/next round | `PreviousFrameByRound` (L1909) / `NextFrameByRound` (L1538) | `round_*` name prefix |
| prev/next special | `PreviousSpecialFrame` (L1939) / `NextSpecialFrame` (L1572) | **`SeekControls.EventTypeFilters`** |

  Rendered **only on Parser and Analysis Engine tabs** (`ParserTabView.axaml:53`,
  `AnalysisTabView.axaml:46`). All eight buttons are functional and symmetric.

**Surface B — `ReplayTabViewModel` (`ViewModels/Replay/ReplayTabViewModel.cs`), owns its own
`ReplaySeekControls` instance.** A parallel set of tick-group navigators:

  - `PreviousTick` / `NextTick` — `[RelayCommand]`, symmetric.
  - `PreviousRoundTick` (L597) / `NextRoundTick` (L450) — plain methods, symmetric.
  - `PreviousSpecialTick` (L610) / `NextSpecialTick` (L464) — plain methods, symmetric, read
    **`ReplaySeekControls.EventTypeFilters`** (same hardcoded list as Surface A).
  - `NextGameEventTick` (L421) — a `[RelayCommand]`, **forward-only, no `Previous`**, and it reads a
    **different** filter source: **`GameEventFilters`** (`GameEventFilterItem`, `.IsEnabled`),
    populated from the demo's actual events (`MainViewModel` L1827), wired via
    `ReplayTab.GameEventFilterProvider` (L461).

  **`ReplaySeekControls` and `NextGameEventTick` render in zero views** (verified by grep across
  `Views/`). `ReplayTabViewModel` is instantiated (`MainViewModel` L226) and fully callback-wired,
  but **no view mounts it** — it is orphaned from the Replay→Entity-Tracking tab split. There is no
  "Replay" tab; the registered tabs are Parser / Entity Tracking / Analysis Engine / Diagnostics
  (`Modules/BuiltInTabsModule.cs`) plus the 2D Playback module tab.

**Surface C — the global toolbar (`Views/MainView.axaml` L36–144).** Minimal, always visible:
◀ ▶ (prev/next frame → `PreviousFrameCommand` / `NextFrameCommand`), ▶▶ ▶| ▶|| (Continue /
StepTick / StepRound **to breakpoint** → `ContinueToBreakpointCommand` / `StepTickToBreakpointCommand`
/ `StepRoundToBreakpointCommand`), and the ⏯ play/pause + speed combo. **No round / event / plain-tick
nav.** This is the only nav the **Entity Tracking, Diagnostics, and 2D Playback** tabs get.

### 1.3 Where each tab lands today

| Tab | Frame ◀▶ | Tick | Round | Game-event | Breakpoint step |
|---|:-:|:-:|:-:|:-:|:-:|
| Parser | ✓ (toolbar + SeekControls) | ✓ | ✓ | ✓ (hardcoded filter) | ✓ |
| Analysis Engine | ✓ (toolbar + SeekControls) | ✓ | ✓ | ✓ (hardcoded filter) | ✓ |
| Entity Tracking | ✓ (toolbar only) | ✗ | ✗ | ✗ | ✓ |
| Diagnostics | ✓ (toolbar only) | ✗ | ✗ | ✗ | ✓ |
| 2D Playback (module) | ✓ (toolbar only) | ✗ | ✗ | ✗ | ✓ |
| *(orphaned `ReplaySeekControls`)* | rendered nowhere — including the demo-derived game-event nav | | | | |

This table **is** the user's complaint: the "more complete" nav (SeekControls) exists on two tabs
and the better game-event mechanism (demo-derived `GameEventFilters` + `NextGameEventTick`) exists on
none.

### 1.4 Module access to navigation

Modules consume navigation through `IModuleContext`
(`DemoViewer.NET.Modules.Abstractions/IModuleContext.cs`): read-only `CurrentFrameIndex` /
`CurrentTick` / `IsPlaying` / `Speed` plus capability-gated `RequestSeekToFrame` /
`RequestSeekToTick` / `RequestPlay` / `RequestPause`. **No semantic nav** (next round / next event)
is exposed. The 2D Playback module can only frame-step via the global toolbar.

---

## 2. The framing: clock vs. semantic navigation

This is the key insight and the organizing principle for every option.

- **Clock = position movement.** "Go to frame N / tick T, step ±1, play, pause." Pure function of an
  index. **Already lifted** into the shell-owned `PlaybackController` — computed once, consumed
  everywhere (toolbar, SeekControls, command palette, Output panel, module context).

- **Semantic navigation = boundary movement.** "Go to the next round / next game-event of type X /
  next tick group." Requires *content knowledge* (where the boundaries are). **Currently stranded**
  in `MainViewModel`'s `*Frame*` methods (wired to the two rendered SeekControls) and duplicated, with
  a divergent filter, in the orphaned `ReplayTabViewModel`.

**The unification is the same move already made for the clock:** lift semantic navigation into a
shell-owned **navigation service**, computed once at load, consumed everywhere — beside the
`PlaybackController`, driving it. Name the parallel explicitly: *we already did this for the clock;
do it again for boundaries.*

Concretely, that service is a small VM (working name **`SemanticNavigator`**) holding precomputed
boundary indices and exposing symmetric `NextRound`/`PrevRound`, `NextEvent`/`PrevEvent`,
`NextTick`/`PrevTick`, each computed by binary-searching the precomputed lists from
`PlaybackController.CurrentFrameIndex` and calling `PlaybackController.SeekToFrame(...)`. It replaces
both the six `*Frame*` methods on the shell and the parallel `*Tick` methods on `ReplayTabViewModel`
with **one** implementation.

### Two distinct clusters — keep them distinct

Semantic nav (next round / event / tick) and **breakpoint stepping** (Continue / StepTick /
StepRound *to breakpoint*) look similar but are not the same feature. Breakpoint stepping
(`ContinueToBreakpoint` L1331, `StepRoundToBreakpoint` L2102, `StepTickToBreakpoint` L2141) is
debugger-aware: it scans `Debugger.CheckFrame` for breakpoint hits and halts at whichever comes
first. **This review does not redesign breakpoint stepping.** In every option below the breakpoint
cluster stays a visually and behaviorally separate group; it is *not* folded into next-round /
next-event. (It can share the same nav strip as a labeled sub-group, but it keeps its own commands
and its own `HasFile`/`CanDebugStep` gates.)

---

## 3. Options

### Option 1 (recommended) — navigation as shell chrome

Render **one** complete navigation surface, **once**, in the shell `DockPanel` — exactly as the
status strip (`MainView.axaml:147`) and Output panel (`:151`) are already docked. It is always
visible across every tab and drives `PlaybackController` + the new `SemanticNavigator`. The per-tab
`SeekControls` instances and the toolbar's nav buttons collapse into this single surface.

```
DockPanel (MainView)
├─ Top    : Toolbar  (Open Demo, Debugger/Output toggles, Bookmarks — NON-nav chrome only)
├─ Top    : ◀── NAV STRIP (NEW shell chrome) ──────────────────────────────────────────▶
│            [⏮ ◀◀ev ◀round ◀tick ◀frame] [ frame N / MAX | tick T ] [frame▶ tick▶ round▶ ev▶▶ ⏭]
│            [ ⏯ play  speed ▾ ]    ‖  [ ▶▶ continue  ▶| step-tick  ▶|| step-round  (to breakpoint) ]
│                                              └─ distinct breakpoint sub-group (unchanged) ─┘
│            [ ⚙ event-type filter ▾ ]  ← flyout, demo-derived event list
├─ Bottom : Output panel (existing)
├─ Bottom : Status strip (existing)
└─ Fill   : SplitView → TabControl (Parser | Entity Tracking | Analysis | Diagnostics | 2D)
```

- Consistency becomes **automatic** — there is one surface; it cannot drift between tabs.
- Entity Tracking, Diagnostics, and 2D Playback get full round/event/tick nav for free.
- Modules render no nav of their own; they keep consuming `IModuleContext` current-tick. The strip
  driving the shared clock means a module's view updates via the existing `Advanced` push with zero
  module-side work.
- The orphaned `ReplayTabViewModel` game-event logic is salvaged: its demo-derived filter becomes the
  strip's filter; its forward-only `NextGameEventTick` is replaced by the symmetric service.
- `SeekControlsViewModel`'s per-button `Show*` styled properties (`SeekControls.axaml.cs`) already
  allow hiding any button — useful for a compact embedding, but in this option the strip shows the
  full set everywhere.

**Cost / risk.** One always-on horizontal strip costs vertical space. Mitigation: the existing
compact (2-row) layout already in `SeekControls.axaml` can be reused, or the strip can merge into the
existing toolbar row. The bigger work is wiring: the strip's frame-box and per-tab "current frame"
semantics must read the controller (frame index) uniformly — today Parser uses frame index and the
orphaned replay surface used game-tick. Standardize on **frame index for movement, tick shown as a
read-only label** (the controller already exposes both).

### Option 2 — per-tab `SeekControls`, made complete and present everywhere

Keep the per-tab `SeekControls` pattern but (a) fix the filter, and (b) add the control to Entity
Tracking, Diagnostics, and the 2D module — each binding the same shared VM.

- Smaller conceptual change; reuses the existing control as-is.
- **But** it re-introduces the exact failure mode that produced this complaint: N rendered instances
  to keep configured identically. Each tab view must remember to include the control, bind the shared
  VM, and not drift its `Show*` flags. The Entity Tracking tab *deliberately removed*
  `SeekControls` (`EntityTrackingTabView.axaml:20` — "the end-user replay chrome … is gone"), so this
  option re-litigates a decision the team already made.
- The filter-source bug and precompute fix still have to be done — so Option 2 is strictly more total
  work than Option 1 for a worse consistency guarantee.

### Option 3 (hybrid) — global nav strip + opt-in tab affordance

Adopt Option 1's single shell nav strip as the source of truth, and additionally let a tab request an
*inline, scoped* nav affordance for context (e.g. Entity Tracking showing "next FullPacket" beside
its delta log) via the `SemanticNavigator` API — but **no tab re-renders the general
round/event/tick/frame controls.** Inline tab affordances are limited to *tab-specific* jumps the
global strip doesn't model.

- Keeps global consistency (one general surface) while leaving room for the forward-looking
  "jump to next event of my type" without re-creating the per-tab duplication of Option 2.
- Slightly more surface area than Option 1; defer the inline affordances until a concrete tab needs
  one (Entity Tracking's "next FullPacket" is the obvious first candidate, but out of scope here).

---

## 4. Recommendation — Option 1, with the `SemanticNavigator` service and load-time precompute

Render the complete navigation surface once as shell chrome (Option 1). It is the only option that
makes cross-tab consistency *structural* rather than *maintained*, it directly matches what the user
described, and it gives modules (and the otherwise nav-less Entity Tracking / Diagnostics tabs) the
full surface for free. Option 3's inline affordances are a clean future extension on top of the same
service; Option 2 is rejected because it rebuilds the very duplication that caused the complaint.

Two supporting decisions are part of this recommendation:

**(a) Precompute boundary indices once at load — filter-aware.** Today `Next*`/`Previous*` re-scan
every tick group / every frame on each press. Mirror the existing
`MainViewModel.BuildUnknownMessageCensus` pattern (drained once after parse, keyed structure handed
to the consumer). Build, once per load:

- `int[] RoundBoundaryFrames` — frame indices whose frame contains a `round_*` game event.
- `Dictionary<string, int[]> EventBoundaryFramesByName` — for each game-event name present in the
  demo, the sorted frame indices where it occurs. (This is the **demo-derived** event set — the same
  data `GameEventFilters` is populated from — so the filter and the precompute share one source.)
- `int[] TickBoundaryFrames` — first frame index of each distinct `ServerTick`.

`Next/PrevEvent` then unions the index arrays of the *selected* event names and binary-searches for
the first boundary strictly after / before `CurrentFrameIndex`. Cheap, allocation-light, and it
removes `ReplayTabViewModel`'s per-press scanning entirely. The arrays live on `SemanticNavigator`
(or are pushed into it the way the census is pushed into `ParserTab.UnknownByFrame`).

**(b) Converge on one filter mechanism, and give it a home on the strip.** Retire the hardcoded
7-event `EventTypeFilters` list baked into `SeekControlsViewModel`. The single filter is the
**demo-derived** `GameEventFilters` (`GameEventFilterItem`, populated from the actual demo at
`MainViewModel` L1827). Its home is a **flyout on the nav strip** (a ⚙/▾ button next to the
event-jump buttons), reusing the existing checkbox-list UI from `SeekControls.axaml`'s
`SpecialContextMenu` (Select-all / Deselect-all + per-type checkboxes), but bound to
`GameEventFilters` instead of `EventTypeFilters`. The filter config therefore does **not** vanish when
nav becomes global chrome — it moves into the strip's flyout and finally drives a *rendered* control.

**Module API: no change for v1.** Read-only current-tick consumption (`IModuleContext`) is sufficient.
Do **not** add semantic-nav request methods to `IModuleContext` now. Note the forward-looking
"jump to next event of my type" (e.g. 2D module → next kill) as a future capability so the
`SemanticNavigator` API is *shaped* to allow it (its `NextEvent(filter)` already takes a filter), but
do not design or expose that module API in this pass.

---

## 5. The game-event fix — root cause + exemplar of the recommended model

### 5.1 Root cause (corrected — the task's hypothesis was partly wrong)

The task hypothesized "some prev options don't exist (NextSpecialTick / NextRoundTick are forward-only,
no Previous)." **The code disproves that:** `PreviousSpecialTick` (`ReplayTabViewModel:610`) and
`PreviousRoundTick` (`:597`) both exist and are wired into `ReplaySeekControls`; the rendered Parser/
Analysis `SeekControls` likewise has fully symmetric prev/next special and round.

The real defect is twofold:

1. **The capable game-event nav is rendered nowhere.** The one demo-aware navigator,
   `NextGameEventTick` (`ReplayTabViewModel:421`) — which reads the **demo-derived** `GameEventFilters`
   — lives on `ReplaySeekControls`, which **mounts in zero views**. So from the user's seat, the
   "good" game-event option literally does not exist in the UI.

2. **The rendered game-event nav uses a stale, hardcoded filter.** The Parser/Analysis special-seek
   buttons read `SeekControls.EventTypeFilters` — a **hardcoded 7-event list**
   (`player_death, round_start, round_end, bomb_planted, bomb_defused, player_hurt, weapon_fire`,
   `SeekControlsViewModel` L96–104). If the loaded demo lacks one of those events, or the user
   deselects all but a missing one, next/prev-special silently **no-ops** — "an option that didn't
   exist." It can never offer event types the demo *does* have but the list omits.

One line: **the good (demo-derived) game-event navigator is orphaned in an unrendered view, while the
rendered one filters against a hardcoded list that may not match the demo.**

### 5.2 The fix (worked example of the recommended model)

- **One implementation, symmetric.** `SemanticNavigator.NextEvent(filter)` /
  `PrevEvent(filter)` — binary search over `EventBoundaryFramesByName` (precomputed §4a), unioned over
  the selected event names, then `PlaybackController.SeekToFrame(...)`. This single pair replaces
  `NextSpecialFrame`/`PreviousSpecialFrame` (shell), `NextSpecialTick`/`PreviousSpecialTick` and
  `NextGameEventTick` (`ReplayTabViewModel`) — five methods and two filter sources collapse to one
  method pair and one filter.
- **One filter, demo-derived.** The strip's event-jump buttons read `GameEventFilters` (populated from
  the demo). The hardcoded `EventTypeFilters` is deleted. Selecting nothing = "match any" (preserve
  the existing `anyFilter` convenience so the buttons always work).
- **Same treatment for round.** `NextRound`/`PrevRound` over `RoundBoundaryFrames`; replaces the four
  `*Round*` methods across the two VMs.
- **Rendered once, everywhere.** Because the strip is shell chrome (§3 Option 1), the fixed game-event
  nav is now present on Entity Tracking, Diagnostics, and 2D — not just Parser/Analysis.

---

## 6. Phased implementation outline (for the recommended Option 1)

Each phase is independently shippable and behavior-preserving until the cut-over phase.

- **Phase A — Precompute service (no UI change).** Add `SemanticNavigator` and build
  `RoundBoundaryFrames` / `EventBoundaryFramesByName` / `TickBoundaryFrames` once after parse, draining
  in the same place as `BuildUnknownMessageCensus`. Re-point the *existing* six shell `*Frame*` methods
  to call the service internally. Pure refactor; SeekControls behavior unchanged. Gate: prev/next
  round/tick/special on Parser+Analysis behave identically to before.

- **Phase B — Converge the filter.** Switch the rendered special-seek filter from the hardcoded
  `EventTypeFilters` to the demo-derived `GameEventFilters`; delete `EventTypeFilters`. Move the filter
  UI into a strip-ready flyout VM (reuse the `SpecialContextMenu` template). Gate: selecting an event
  the demo contains jumps to it; deselect-all still "matches any."

- **Phase C — Shell nav strip.** Add the single nav strip to `MainView.axaml`'s `DockPanel` (a new
  `NavStrip` control bound to `PlaybackController` + `SemanticNavigator` + the filter flyout). Keep the
  breakpoint sub-group (Continue / StepTick / StepRound to breakpoint) as a distinct, labeled group
  with its existing commands. Remove the per-tab `SeekControls` from `ParserTabView.axaml` and
  `AnalysisTabView.axaml`. Gate: all five tabs show identical, working nav; breakpoint stepping
  unchanged.

- **Phase D — Decommission the orphan.** Delete `ReplaySeekControls` and the parallel `*Tick` /
  `NextGameEventTick` nav from `ReplayTabViewModel` (its tick-group *presentation* state, if still used
  by any tab, stays; only the duplicated navigation goes). Remove the now-unused
  `ReplayTab.GameEventFilterProvider` plumbing in favor of the single service. Gate: build clean, no
  dead nav code, headless UI smoke (per the project's Skia frame-capture test convention) confirms the
  strip on every tab.

- **Phase E (deferred, not in this work) — module forward-nav.** Only if a module needs "jump to next
  event of my type," extend `SemanticNavigator` exposure to `IModuleContext`. Out of scope for v1.

---

## 7. Open items for the user to weigh

- **Vertical space vs. always-on visibility.** Option 1 adds a persistent horizontal strip. Decide:
  merge it into the existing toolbar row (tighter, busier) vs. its own row (cleaner, costs ~36px). The
  existing compact 2-row `SeekControls` layout is available either way.
- **Frame-index vs. game-tick as the strip's primary readout.** The orphaned replay surface navigated
  by game-tick; Parser navigates by frame index. Recommend: movement is frame-index based (matches the
  controller), with tick shown as a read-only label. Confirm this is acceptable for the entity/2D
  audiences who think in ticks.
- **Diagnostics tab nav.** Diagnostics is a `TabPlacement.Diagnostics` tab (`BuiltInTabsModule.cs:78`).
  Confirm it should get the same global nav strip (Option 1 gives it for free) or be excluded as a
  non-temporal tab.
