# Modular UI Tab Framework — Design

The module framework and playback clock shipped on main; this is the as-built design record. Forward
items remain (third-party runtime plugins §7.2, sub-tick interpolation §3.4/§3.6). The §1.5
"no Microsoft.Extensions.DI" assumption is superseded (annotated inline; the app now uses a bare
Microsoft.Extensions DI container).
**Scope:** A first-party module framework that lets an injected module contribute one or more
tabs to the Avalonia shell, wired into the parser / entity-tracking / analysis layers and driven
by a single shared **playback clock**. Explicit forward-looking guardrails for future
third-party runtime-loaded modules.
**Pilot consumer:** a 2D playback module (`docs/2d-playback/2d-playback-module-requirements.md`, authored in
parallel — not present at time of writing; this design does not block on it). The 2D module's
headline feature — "players move in real time as playback continues" — is the forcing function for
the playback-clock section.

---

## 0. TL;DR (read this first)

1. **One clock, one position.** Today an *informal* clock already exists, scattered across
   `MainViewModel._selectedFrameIndex`, `HandleFrameSelectedFromParserTab` (the fan-out to
   `EntityTab.SeekEntitiesAsync` / `Analysis.SeekToFirstMessageOfFrame` / `SeekControls.SetCurrentFrame`),
   and `FrameNavigationViewModel`. We **extract that fan-out into an explicit `PlaybackController`**
   and add a timer-paced play loop on top. `SeekControlsViewModel` and `ReplayTabViewModel` keep their
   UIs but their callbacks now write **through** the controller. This is the single most important
   decision: it converts "build a clock" into "centralize the seek fan-out we already have," which is
   low-risk and directly answers the requirement to not end up with two competing playback notions.
2. **Modules implement `IWorkspaceModule`**, returning one or more `WorkspaceTabDescriptor`s. The
   descriptor holds a lazily-built ViewModel (persists across tab switches → state retention) and a
   View factory (realized on activation, dropped on deactivation → preserves the inactive-content-unload
   invariant).
3. **Modules read state through `IModuleContext`** — a read-only, push/observable, render-frame-coalesced
   surface. They never touch the live `EntityTracker` directly (it exposes mutators), the raw byte buffer,
   or `DemoParser`.
4. **The TabControl becomes `ItemsSource`-driven**, bound to `MainViewModel.Tabs`
   (`ObservableCollection<WorkspaceTabDescriptor>`). The four existing tabs become registered descriptors
   for uniformity. Inactive-content unloading is preserved (Avalonia's single content presenter already
   gives this).
5. **Play loop is in scope now**, forward-only, snap-to-tick, paced at demo tickrate (≈64 Hz default).
   Interpolation is explicitly deferred.
6. **One protected-adjacent recommendation** (NOT a protected file): add an additive
   `EntityTracker.AdvanceOneFrame(DemoFrame)` so the play loop can step the authoritative tracker forward
   in O(1) per tick instead of the current O(N)-from-zero `AdvanceToIndex`. `EntityTracker.cs` lives in
   `DemoViewer.NET.Parser.EntityTracking` and is **not** on the protected list — but it is a deliberate API
   addition and is flagged for owner sign-off (§8.2).

---

## 1. Verified current state (grounding)

Everything below was read directly, not assumed.

### 1.1 The shell and tabs
- `src/App/DemoViewer.NET/Views/MainView.axaml` hosts `TabControl x:Name="ShellTabs"`,
  `SelectedIndex="{Binding SelectedMainTab}"`, with **four hard-coded `TabItem`s** (Parser, Entity
  Tracking, Analysis Engine, Diagnostics). The comment at line 149 — *"Real TabControl — inactive tab
  content is unloaded (F1.1, F2.2)"* — names the **central performance invariant** the
  `ItemsSource`-driven host MUST preserve.
- `src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs` (~1991 lines) is the shell. It owns
  `ParserTab`, `EntityTab` (`EntityTrackingTabViewModel`), `AnalysisTab`, `ReplayTab`, `Diagnostics`,
  `SeekControls`, `Palette` (command palette), `Bookmarks`, `Output`, and `DebuggerPanel`.
- Each tab VM is constructed with the shared `FrameNavigationViewModel` (`Navigation`) and wired to the
  shell via **callback delegates** (Func/Action), never a back-reference to `MainViewModel`. This is the
  established dependency direction and the module framework follows it exactly.

### 1.2 The (informal) playback seam
- The master selection is `ParserTabViewModel.SelectedFrame`. Its setter (`OnSelectedFrameChanged`,
  `ParserTabViewModel.cs:1299`) calls `OnFrameSelected?.Invoke(idx)`, wired in the shell ctor to
  `HandleFrameSelectedFromParserTab` (`MainViewModel.cs:1209`).
- `HandleFrameSelectedFromParserTab` fans out: sets `_selectedFrameIndex`, `SeekControls.SetCurrentFrame(idx)`,
  `EntityTab.SeekEntitiesAsync(idx)`, refreshes debugger command `CanExecute`, and
  `Analysis.SeekToFirstMessageOfFrame(idx)`.
- `FrameNavigationViewModel` (`ViewModels/Common/FrameNavigationViewModel.cs`) is the shared seam:
  `SeekToFrame(int)`, `SeekToTick(int)`, `RevealClass(string)`, and the `SelectedFrameChanged` event.
  The shell wires `SeekToFrameHandler → SeekToFrameIndex`, `SeekToTickHandler → SeekToServerTick`.
- **There is no real-time play loop anywhere.** A solution-wide grep for `DispatcherTimer` / `IsPlaying`
  / `PlaybackSpeed` / `PlayCommand` finds only `MainViewModel._perfTimer` (a 1 Hz CPU/RAM stats poll).
  Navigation today is exclusively discrete: `PreviousFrame` / `NextFrame` / `StepTickToBreakpoint` /
  `StepRoundToBreakpoint` / `ContinueToBreakpoint`. **This is the gap the framework fills.**

### 1.3 Entity layer — the critical performance fact
- `EntityTracker` (`src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs`) is **stateful**:
  it tracks `CurrentTick` (line 201) and `CurrentFrameIndex` (line 198), and exposes the live entity set
  via `CurrentEntities` (`EntitySet`, line 189). `EntitySet.All()` / `AllInPvs()` / `OfClass(name)` /
  `AllIndexed()` enumerate the live entities — exactly what the 2D module needs (read `CCSPlayerPawn`
  origins at the current tick).
- **But the only public forward methods replay from the start of the passed list:**
  `AdvanceToIndex(frameIndex, frames)` (line 254) does `for (int i = 0; i < limit; i++) ProcessFrame(frames[i])`
  — **O(frameIndex) every call, from zero.** `EntityTab.SeekEntitiesAsync` (`EntityTrackingTabViewModel.cs:225`)
  additionally calls `CreateTracker()` to make a **fresh** tracker per seek and replays from a FullPacket
  checkpoint. This is correct and fine for discrete clicks. **It is fatal at 64 Hz.**
- The per-frame step primitive `ProcessFrame(DemoFrame)` is **private** (line 1720). There is no public
  single-frame forward step from the current position. → §8.2 recommends adding one (additive, non-protected).

### 1.4 What modules can read (existing public API, no new parser surface required for read paths)
- **Demo / frames:** `MainViewModel._allFrames` (`List<DemoFrame>`), `DemoFrame` public contract
  (`Tick`/`ServerTick`/`GameTick`/`Command`/`InnerMessages`/`FrameNumber`).
- **Entities at current tick:** `EntityTracker.CurrentEntities` → `EntitySet.All()` etc.;
  `EntityState.ClassName`, `EntityState["m_vecOrigin"]`, `EntityState.Get<T>(path)`,
  `EntityState.TryGet<T>(path)`, `EntityState.IsInPvs`, `EntityState.Serial`. Typed snapshots:
  `EntityTracker.Snapshot<T>(slot)`, `SnapshotNode(slot)`, `ResolveHandle<T>(handle)`.
- **Analysis / game events:** `AnalysisTabViewModel.Analysis` (`AnalysisViewModel`), and
  per-frame game events via `ReplayTabViewModel.FrameGameEvents` /
  `DemoFrame.InnerMessages` `GameEventMessage.DecodedEvent`. The shell already resolves
  `parsed.AllGameEvents` and a `DemoContext` (`_replayDemoContext`).
- **Player roster:** the shell holds `_players` (`IReadOnlyDictionary<int, PlayerInfo>`),
  `_playersByUserId`, and `_nameByUserId`; `SlotToName(userId)` is the canonical name resolver.

### 1.5 Hosts and construction (no MS.DI)
> **SUPERSEDED (P1.1 chunk C).** The app now uses a bare Microsoft.Extensions DI container (no Hosting) as
> the single composition root; `App.BuildServices` constructs and HOLDS `ModuleRegistry` as a singleton and
> injects it into `MainViewModel` (both hosts incl. WASM). Rationale: the settings foundation needs a
> long-lived `SettingsService` + `IOptionsMonitor<AppSettings>` for live-reload (P1.1). The prose below is
> retained for history.

- `App.axaml.cs` constructs `new MainViewModel(windowService)` directly for **both** the desktop
  (`IClassicDesktopStyleApplicationLifetime`) and browser (`ISingleViewApplicationLifetime`) lifetimes.
  There is **no Microsoft.Extensions.DependencyInjection container.** Registration must therefore be a
  plain registry populated in `App.axaml.cs` (or a static composition root), passed into `MainViewModel`.
- The browser host has **no filesystem and no assembly probing.** Third-party runtime assembly loading
  is **desktop-only**; first-party static registration is the only cross-host path (§7).

---

## 2. Architecture overview

```
                         ┌─────────────────────────────────────────────────────────┐
   App.axaml.cs ────────▶│  ModuleRegistry (first-party static; desktop adds plugins)│
   (composition root)    └───────────────────────┬─────────────────────────────────┘
                                                  │ enumerated once at shell init
                                                  ▼
   ┌──────────────────────────────────────────────────────────────────────────────┐
   │  MainViewModel (shell)                                                          │
   │   • Tabs : ObservableCollection<WorkspaceTabDescriptor>  ◀── ItemsSource        │
   │   • SelectedTab : WorkspaceTabDescriptor                                         │
   │   • PlaybackController  (owns the clock + the authoritative tracker)            │
   │   • IModuleContext  (read-only facade over controller + demo + analysis)        │
   └───────┬───────────────────────────────┬──────────────────────────────────┬─────┘
           │ activation/deactivation        │ push (coalesced, 1/render frame) │
           ▼                                ▼                                   ▼
   ┌───────────────┐   ┌────────────────────────────────┐   ┌─────────────────────────┐
   │ Built-in tabs │   │ PlaybackController             │   │ Module(s)               │
   │ (also         │   │  • IsPlaying / Speed / Tick     │   │  IWorkspaceModule        │
   │  descriptors) │   │  • Play/Pause/Step/SeekToFrame  │   │   → tab VM + View         │
   │  Parser …     │   │  • authoritative EntityTracker  │   │   subscribes IModuleContext│
   └───────────────┘   │  • DispatcherTimer play loop    │   └─────────────────────────┘
                       │  • background advance + snapshot │
                       └────────────────────────────────┘
```

Three new types do all the work: **`PlaybackController`** (the clock), **`IModuleContext`** (the
read-only push surface), and **`IWorkspaceModule` + `WorkspaceTabDescriptor`** (the contribution
contract). Everything else is wiring.

---

## 3. The playback clock — the critical shared seam

### 3.1 Decision
**The framework owns the clock. `PlaybackController` is the single authoritative owner of "current
position" and the single thing that advances the authoritative `EntityTracker`.** Modules, the
toolbar, `SeekControlsViewModel`, and `ReplayTabViewModel` all become **subscribers/delegators**.
Modules MUST NOT invent their own playback or own a tracker.

**Real-time pacing is in scope now**, with these iteration-1 simplifications (matching the task's
steer):
- **Forward-only** auto-play. Reverse playback is not supported; seeking backward is a discrete re-seek.
- **Snap-to-tick.** No sub-tick interpolation — the clock lands the tracker on whole ticks. Smooth
  interpolation between ticks is a module-side concern and is **deferred** (the context exposes enough
  for a module to lerp later; see §3.6).
- **Paced at demo tickrate.** Default 64 Hz; derived from the demo header when available, else 64. The
  loop self-throttles if a step over-runs the budget (§5).

### 3.2 Why extract rather than invent
The fan-out in `HandleFrameSelectedFromParserTab` already *is* a clock tick handler — it just fires on
manual selection instead of on a timer. We lift that fan-out body into `PlaybackController.SeekToFrame`
and have both the timer loop and the manual UI call it. This guarantees there is exactly one code path
that moves "current position," eliminating the two-competing-notions risk by construction.

### 3.3 Consolidating `SeekControlsViewModel` / `ReplayTabViewModel`
- **`SeekControlsViewModel` keeps its view and its commands** (frame box, prev/next/round/special
  buttons). Its callback delegates — currently `() => NextFrameCommand.Execute(null)`,
  `seekToFrame: idx => SelectedFrame = Frames[idx]`, etc. — are **rewired to the controller**:
  `nextFrame: controller.StepForward`, `seekToFrame: controller.SeekToFrame`,
  `previousTick: controller.StepBackTick`, and so on. No XAML change; only the wiring in the shell ctor.
- **`ReplayTabViewModel`** keeps its tick-group presentation, but its tick-navigation commands
  (`NextTick`/`PreviousTick`/`SeekToGameTick`) also delegate to the controller. The Replay tab's
  `ReplaySeekControls` instance points at the same controller. There is no longer a separate "replay
  position" — there is one position, expressed two ways (frame index in Parser, game tick in Replay).
- **`FrameNavigationViewModel`** stays as the shared seam but its `SeekToFrameHandler` /
  `SeekToTickHandler` are wired to `controller.SeekToFrame` / `controller.SeekToTick`. Command palette and
  Output-panel navigation thus also route through the one clock. `SelectedFrameChanged` is raised **by the
  controller** after each move (so existing subscribers keep working unchanged).

### 3.4 `PlaybackController` surface

Lives at `src/App/DemoViewer.NET/ViewModels/Playback/PlaybackController.cs` (CommunityToolkit
`ObservableObject`, App project — not the abstractions assembly, because it holds the live tracker and
frame list).

```csharp
public sealed partial class PlaybackController : ObservableObject, IDisposable
{
    // ── Authoritative state (read-only to the outside via IModuleContext) ──
    [ObservableProperty] private int      _currentFrameIndex = -1;  // 0-based into the frame list
    [ObservableProperty] private int      _currentTick;             // server tick at the current frame
    [ObservableProperty] private bool     _isPlaying;
    [ObservableProperty] private double   _speed = 1.0;             // 0.25 … 8.0 (clamped)
    public int    TotalFrames    { get; private set; }
    public int    TickRate       { get; private set; } = 64;        // from header, else 64
    public bool   HasDemo        => TotalFrames > 0;

    // ── Operations (the ONLY way to move position) ──
    [RelayCommand] public void Play();          // starts the DispatcherTimer loop (no-op if at end)
    [RelayCommand] public void Pause();         // stops the loop
    [RelayCommand] public void TogglePlay();
    public void StepForward();                  // +1 frame (incremental on the authoritative tracker)
    public void StepBack();                     // −1 frame (discrete re-seek; see §3.5)
    public void StepTick(int dir);              // ±1 tick boundary
    public void SeekToFrame(int frameIndex);    // discrete seek (checkpoint-replay)
    public void SeekToTick(int tick);           // first frame at/after tick → SeekToFrame
    public void SeekToEnd();
    public void Reset();                         // on new demo load / unload

    // ── Render-frame-coalesced push to subscribers (the active module + tabs) ──
    public event Action<PlaybackFrame>? Advanced;   // fired on the UI thread, ≤1 per render frame

    // Snapshot/seam the IModuleContext wraps:
    internal EntityTracker? AuthoritativeTracker { get; }   // NEVER exposed publicly
}
```

`PlaybackFrame` is a lightweight transient passed to `Advanced` — see §4.3.

### 3.5 Hybrid advance model (the performance crux)

**Tracker ownership (decided — see §3.7).** The `PlaybackController` owns the **single authoritative
`EntityTracker`.** `EntityTrackingTabViewModel` stops *owning* a tracker and becomes a pure reader that
rebuilds its UI from `controller.AuthoritativeTracker` after each move. The checkpoint-replay seek logic
(today inside `EntityTab.SeekEntitiesAsync`) is **extracted into a shared seek service** the controller
calls; EntityTab no longer constructs trackers.

Two distinct paths, chosen by operation:

| Operation | Mechanism | Cost |
|---|---|---|
| **Play loop / `StepForward`** | `AuthoritativeTracker.AdvanceOneFrame(nextFrame)` — incremental, one frame from current position | **O(1) per tick** (see §8.2 for the required additive API) |
| **Discrete `SeekToFrame` (jump, scrub, click)** | controller builds a fresh tracker via the shared seek service (the checkpoint-replay logic lifted from `EntityTab.SeekEntitiesAsync`) and **swaps it in** as the new authoritative tracker | O(distance from nearest FullPacket) — acceptable for one-off jumps |
| **`StepBack` / reverse** | Discrete re-seek to `index − 1` (same swap-in path) | Same as discrete seek |

The play loop never uses the O(N)-from-zero path. When the user scrubs or jumps, the controller builds a
freshly-replayed tracker, swaps it in as the authoritative instance, and the loop resumes incremental
`AdvanceOneFrame` stepping from the new `CurrentFrameIndex`. Because **only the controller** ever creates,
swaps, or steps the tracker, "incremental step" and "discrete seek" can never interleave on the same
instance — the swap is atomic from the loop's perspective (the loop is paused during a user seek and
resumes on the new instance).

> **If §8.2's additive API is rejected**, the iteration-1 fallback is: the loop calls
> `AdvanceTo(currentTick + 1, frames.AsSpan(currentFrameIndex+1…))`-style slicing — but slicing
> `IReadOnlyList<DemoFrame>` cheaply and correctly is awkward and the controller would have to track its
> own offset. The clean additive method is strongly preferred; the fallback is documented only so the
> framework is not blocked on the API decision.

### 3.6 The play loop itself
```
DispatcherTimer @ interval = 1000 / (TickRate * Speed) ms
on Tick:
  if not IsPlaying or at end → Pause(); return
  schedule advance work on a background worker:           // §5: tracker step off the UI thread
     tracker.AdvanceOneFrame(frames[CurrentFrameIndex+1]) // mutates authoritative tracker
     build a transient PlaybackFrame view (no deep copy)  // §4.3
  back on UI thread (coalesced — at most one per render frame):
     CurrentFrameIndex++; CurrentTick = frame.ServerTick
     raise Advanced(playbackFrame)                        // active module + built-in subscribers
     Navigation.RaiseSelectedFrameChanged(idx)            // keeps SeekControls / Parser in sync
```

Coalescing rule: if the background advance for tick *N+1* has not been consumed by a render frame before
tick *N+2* fires, drop the intermediate push and advance again (the tracker is still stepped — we never
skip *decoding* a frame, we only skip *notifying* about an intermediate one). This keeps entity state
correct while never flooding the UI thread. At `Speed > 1`, the controller may advance K frames per timer
tick and push once.

**Threading decision (made explicit).** A single `ProcessFrame` is sub-millisecond — the
`project_entity_profiling_phase3` measurement is ~2.6s of entity decode across an *entire* demo
(thousands of frames), i.e. well under the ~15.6 ms per-tick budget at 64 Hz. **Therefore iteration 1
advances the tracker synchronously on the UI thread inside the timer tick** (`AdvanceOneFrame` →
build transient facade → raise `Advanced`). This is race-free by construction: the module reads the live
`EntitySet` on the same thread that mutated it, with no concurrent writer. The "background worker"
described in PM-4 is reserved for the *exceptional* heavy frame — a `DEM_FullPacket` checkpoint or a
discrete seek that replays many frames — where the controller offloads to a worker and shows a busy state
rather than blocking. This removes the mutate-during-read race (§9.1 R3-adjacent) for the common play
path while keeping the escape hatch for the heavy case.

The "DispatcherTimer vs. background pacing" trade-off is resolved in favor of a `DispatcherTimer` for the
*cadence* (it is the idiomatic Avalonia per-frame-ish timer and auto-marshals to the UI thread). WASM
note: `DispatcherTimer` works on the browser host; `Task.Run`/threads are constrained under WASM — the
UI-thread-advance default (above) is in fact the *only* viable path on WASM, so no separate fallback is
needed there (see §5.4).

### 3.7 Tracker ownership — the single owner (resolves §9.1 R1)
**Decision: the `PlaybackController` is the sole owner of the authoritative `EntityTracker`.** This
resolves the ownership question end-to-end so the seek/step paths can never desync:

- **Today** `EntityTrackingTabViewModel` owns the tracker: `CreateTracker()` builds it,
  `CurrentTrackerInternal` holds it, and every other consumer reads it via
  `ParserTab.EntityTrackerSource = () => EntityTab.CurrentTrackerInternal`.
- **After** the controller owns it. The tracker-*construction + checkpoint-replay* logic currently inside
  `EntityTab.SeekEntitiesAsync` is **extracted into a shared `EntitySeekService`** (App project) that both
  the controller (for discrete seeks) and Phase-0 compatibility call. `EntityTab.CurrentTrackerInternal`
  becomes a thin pass-through to `controller.AuthoritativeTracker` (so `ParserTab.EntityTrackerSource` and
  the command palette keep working with no edit).
- **EntityTab's role narrows to "reader/UI-builder."** After each controller move, EntityTab rebuilds its
  groups/fields/delta UI from `controller.AuthoritativeTracker` (the same UI-building code it already has —
  only the *ownership* of the tracker instance moves, not the tree-building logic). This is the precise
  split §6.3 intends: reuse EntityTab's VM for UI; move tracker ownership to the controller.

This is why §6.3 ("built-in VMs stay constructed in `MainViewModel`, descriptors just reference them")
and controller-ownership are *not* in conflict: the VM is reused for its UI; the one thing lifted out of
it is tracker ownership, which it should never have held once a play loop exists.

---

## 4. Module contract & host context

### 4.1 `IWorkspaceModule` (the contribution contract)
Lives in a thin **abstractions assembly** so it is the stable reference target for future third-party
modules. **Recommendation:** create `src/App/DemoViewer.NET.Modules.Abstractions/` targeting **net10.0** (matching
the solution; netstandard2.0 would fight the Avalonia 11 `Control`/`UserControl` refs the `ViewFactory`
needs — keep ns2.0 in mind only if a future third-party SDK target demands the broadest reach). It may start *inside* the App project and be extracted later if that is faster for Wave 2 —
note the extraction explicitly so the namespace is stable from day one.

```csharp
public interface IWorkspaceModule
{
    /// Stable, unique id (reverse-DNS recommended, e.g. "net.demoviewer.playback2d").
    /// Used for registration de-dup, session persistence keys, and capability grants.
    string Id { get; }

    /// Human title shown if the module's own tabs don't override it.
    string DisplayName { get; }

    /// Semantic version of the CONTRACT this module was built against (see §7.2).
    Version ContractVersion { get; }

    /// Produce the tabs this module contributes. Usually one; may be many.
    /// Called once at shell init (first-party) or post-load (third-party).
    IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host);
}
```

A module yields `IEnumerable<WorkspaceTabDescriptor>` so the **descriptor**, not the module, is the unit
of placement. One tab is the common case (the 2D module returns one); a module *may* return several
(e.g. a "net diagnostics" module contributing both a "wire stats" and a "decode-as" tab).

### 4.2 `WorkspaceTabDescriptor` (the unit of placement & lifecycle)

```csharp
public sealed class WorkspaceTabDescriptor
{
    public required string   TabId   { get; init; }   // unique within the module
    public required string   Header  { get; init; }   // tab header text
    public object?           Icon    { get; init; }   // optional (Geometry/StreamGeometry/path key)
    public int               Order   { get; init; }   // sort key in the tab strip
    public TabPlacement      Placement { get; init; } = TabPlacement.Main; // Main | Diagnostics-group

    /// Lazy VM factory. The VM is built on FIRST activation and then RETAINED
    /// (this is what gives state retention across tab switches).
    public required Func<IWorkspaceTabViewModel> ViewModelFactory { get; init; }

    /// View factory. The View is built on EACH activation and DROPPED on deactivation
    /// (this is what preserves the inactive-content-unload invariant).
    public required Func<Control> ViewFactory { get; init; }
}
```

```csharp
public interface IWorkspaceTabViewModel
{
    /// Called when the tab becomes the selected tab. Subscribe to clock pushes here.
    void OnActivated(IModuleContext context);

    /// Called when another tab is selected. Unsubscribe; do zero per-tick work after this.
    void OnDeactivated();

    /// Optional opaque blob for session persistence (mirrors existing SnapshotState/RestoreState).
    object? SnapshotState() => null;
    void    RestoreState(object? state) { }
}
```

**Lifecycle is gated on `OnActivated`/`OnDeactivated`, NOT View `Loaded`/`Unloaded`.** This is
deliberate: View load/unload is a rendering concern and is non-deterministic across
Avalonia versions; the controller subscription must be deterministically tied to *selection*. The host
calls `OnActivated` when the descriptor becomes `SelectedTab` and `OnDeactivated` when it stops being so.

### 4.3 `IModuleContext` (the read-only push surface)
The one object a module is handed. It is **read-only**, **push/observable**, and **coalesced to the
render frame**. It deliberately does NOT expose the live `EntityTracker`, the raw byte buffer, the
`DemoParser`, or any mutator.

```csharp
public interface IModuleContext
{
    // ── Identity / lifecycle ──
    bool   HasDemo { get; }
    string? DemoPath { get; }           // null on WASM / not-yet-loaded
    int    TickRate { get; }

    // ── Clock (read-only view of PlaybackController) ──
    int    CurrentFrameIndex { get; }
    int    CurrentTick { get; }
    bool   IsPlaying { get; }
    double Speed { get; }

    // Operations a module is ALLOWED to request (it asks the clock; it never moves itself):
    void RequestSeekToFrame(int frameIndex);
    void RequestSeekToTick(int tick);
    void RequestPlay();
    void RequestPause();
    // NOTE: granted only to modules holding the Playback.Control capability (§7.3).
    //       A read-only visualizer module gets the getters but not these.

    // ── Per-frame push (the hot path) ──
    /// Fires on the UI thread, at most once per render frame, ONLY while the
    /// module's tab is active. Carries a transient snapshot valid ONLY for the
    /// duration of the callback — copy what you need, do not retain it.
    event Action<IPlaybackSnapshot> Advanced;

    // ── Pull access (for on-activation resync and lazy detail) ──
    /// Read-only entity view at the current tick. Backed by the authoritative
    /// EntitySet but exposed through a read-only facade (no mutators).
    IReadOnlyEntityView Entities { get; }

    /// Player roster (resolved name lookups, team, slot). Stable across the demo.
    IReadOnlyList<PlayerRosterEntry> Players { get; }

    /// Read-only analysis/game-event access for the current frame.
    IReadOnlyAnalysisView Analysis { get; }
}

public interface IPlaybackSnapshot
{
    int FrameIndex { get; }
    int Tick { get; }
    /// Transient — valid only inside the Advanced callback.
    IReadOnlyEntityView Entities { get; }
    /// Game events that occurred in THIS frame (for event-driven modules).
    IReadOnlyList<GameEventView> FrameEvents { get; }
}

public interface IReadOnlyEntityView
{
    IEnumerable<IReadOnlyEntity> All();
    IEnumerable<IReadOnlyEntity> OfClass(string className);   // e.g. "CCSPlayerPawn"
    IReadOnlyEntity? BySerial(int serial);
}

public interface IReadOnlyEntity
{
    string ClassName { get; }
    int    Serial { get; }
    bool   IsInPvs { get; }
    object? this[string fieldPath] { get; }      // e.g. ["m_vecOrigin"]
    bool TryGet<T>(string fieldPath, out T value);
}
```

`IReadOnlyEntityView` is a **thin wrapper over the live `EntitySet`** (no copy) when used inside the
`Advanced` callback, and over a captured snapshot when used for on-activation resync. The 2D module's loop
becomes: subscribe to `Advanced` → in the callback, `snapshot.Entities.OfClass("CCSPlayerPawn")` → read
`e["m_vecOrigin"]` and `e["m_angEyeAngles"]` into the module's own draw buffer → invalidate its canvas.
No allocation on the framework side per tick (§5).

**Explicitly NOT exposed** (and why):
- The raw `EntityTracker` — exposes `RegisterEntityFactory`, `BindLensResolver`,
  `ResetEntitiesKeepSchema`, `ProcessFullPacketCheckpoint` (mutators that would corrupt the authoritative
  state).
- `byte[] _demoBytes`, `DemoParser`, `BitBuffer` — wire-level access is a Parser-tab concern, not a
  module concern, and would couple modules to protected internals.
- Any `set`/`Advance`/`Process` method — modules request via the capability-gated `Request*` methods only.

### 4.4 `IModuleHost` (handed to `CreateTabs`)
Minimal: lets a module register tab descriptors and obtain the context. Kept separate from
`IModuleContext` so the *creation-time* surface (host services, logging, capability query) is distinct
from the *runtime* surface (clock + state).

```csharp
public interface IModuleHost
{
    IModuleContext Context { get; }
    bool HasCapability(string capability);    // §7.3
    void Log(ModuleLogLevel level, string message);   // routes to the Output panel channel
}
```

---

## 5. Performance model (measurable invariants)

The requirement "adding tabs must not degrade UI responsiveness" is made concrete:

- **PM-1 (active-only work).** Only the **active** module receives `Advanced` pushes. Inactive modules
  are unsubscribed in `OnDeactivated` and do **zero** per-tick work. *Invariant:* with a demo playing and
  the Parser tab active, a registered-but-inactive 2D module shows 0 CPU attributable to it (verify via a
  per-module stopwatch in debug builds).
- **PM-2 (coalesced push).** At most **one** `Advanced` notification per render frame regardless of
  `Speed`. The tracker may step K frames between pushes; the UI is notified once. *Invariant:* push count
  per second ≤ display refresh rate, independent of `Speed`.
- **PM-3 (no per-tick allocations on the framework hot path).** The `IPlaybackSnapshot` and
  `IReadOnlyEntityView` handed to `Advanced` are **transient facades** over the live `EntitySet`, reused
  across pushes (a single pooled instance whose backing pointer is re-aimed each frame). The framework
  allocates nothing per tick; if a module needs to retain data it copies into its own buffer. *Invariant:*
  steady-state play loop shows flat Gen0 attributable to the framework (the existing
  `project_load_perf_investigation` methodology — deterministic alloc + back-to-back — applies).
- **PM-4 (heavy-frame escape hatch off the UI thread).** Per §3.6, the common per-tick `AdvanceOneFrame`
  runs **synchronously on the UI thread** (sub-millisecond; race-free). Only *exceptional* heavy work — a
  `DEM_FullPacket` checkpoint or a multi-frame discrete seek/scrub — is offloaded to a background worker
  with a busy state. *Invariant:* a normal play-loop tick never exceeds the per-frame budget on the UI
  thread; a heavy seek never blocks the UI thread for its full duration.
- **PM-5 (lazy view/VM construction).** A tab's View is built on first activation; its VM on first
  activation and retained. Registering N modules adds N descriptors (cheap structs/objects) and **zero**
  realized Views until activated. *Invariant:* startup cost is O(module count) for descriptor creation,
  not O(module count) for View construction.
- **PM-6 (self-throttling loop).** If a step over-runs its frame budget (slow demo section), the
  `DispatcherTimer` interval naturally serializes — the loop falls behind in *wall-clock* but never
  queues unbounded work (the coalescing rule in §3.6 drops intermediate pushes, not decodes). *Invariant:*
  the UI stays responsive (input still processed) even if playback can't keep real-time pace.

### 5.4 WASM caveat
Under the browser host, threads are constrained. The play loop's background advance must degrade to
**synchronous on the UI thread** when `OperatingSystem.IsBrowser()` is true, with a *lower* default
tickrate cap (e.g. 32 Hz) to keep the page responsive. This is a runtime branch inside
`PlaybackController`, not a separate implementation. Modules are unaffected (they still just receive
`Advanced`). Flag for Wave 2: validate 2D-module playback smoothness on WASM separately; snap-to-tick at
32 Hz is the accepted iteration-1 target there.

---

## 6. The TabControl refactor (before / after)

### 6.1 After — `MainView.axaml`
Replace the four hard-coded `TabItem`s with an `ItemsSource`-driven `TabControl`:

```xml
<TabControl x:Name="ShellTabs"
            ItemsSource="{Binding Tabs}"
            SelectedItem="{Binding SelectedTab, Mode=TwoWay}"
            Background="{StaticResource ShellBg}">
  <TabControl.ItemTemplate>            <!-- the tab header -->
    <DataTemplate x:DataType="modules:WorkspaceTabDescriptor">
      <StackPanel Orientation="Horizontal" Spacing="6">
        <ContentControl Content="{Binding Icon}" IsVisible="{Binding Icon, Converter={x:Static ...NotNull}}"/>
        <TextBlock Text="{Binding Header}"/>
      </StackPanel>
    </DataTemplate>
  </TabControl.ItemTemplate>
  <TabControl.ContentTemplate>         <!-- the tab body -->
    <DataTemplate x:DataType="modules:WorkspaceTabDescriptor">
      <ContentControl Content="{Binding ActiveContent}"/>   <!-- realized View, see §6.3 -->
    </DataTemplate>
  </TabControl.ContentTemplate>
</TabControl>
```

**Inactive-content unloading is preserved for free:** Avalonia's `TabControl` keeps a *single* content
presenter, so the View of any non-selected descriptor is not in the visual tree. That single-presenter
behavior IS the unload invariant the F1.1/F2.2 comment depends on — the `ItemsSource` form keeps it.

### 6.2 After — `MainViewModel`
```csharp
public ObservableCollection<WorkspaceTabDescriptor> Tabs { get; } = [];

[ObservableProperty] private WorkspaceTabDescriptor? _selectedTab;
partial void OnSelectedTabChanged(WorkspaceTabDescriptor? oldTab, WorkspaceTabDescriptor? newTab)
{
    oldTab?.Deactivate();                    // VM.OnDeactivated + drop View
    newTab?.Activate(ModuleContext);         // VM built-on-first-use + View realized + VM.OnActivated
}
```

The existing `SelectedMainTab` (an `int`) is replaced by `SelectedTab` (the descriptor). Session
persistence keys on `descriptor.TabId` instead of an index (more robust to reordering / added modules).

### 6.3 Built-in tabs become descriptors
The four current tabs are registered as descriptors by a first-party `BuiltInTabsModule`, so the shell
has exactly one code path for all tabs:

| Current `TabItem` | Descriptor `TabId` | ViewModel | View |
|---|---|---|---|
| Parser | `builtin.parser` | `ParserTabViewModel` (existing) | `ParserTabView` |
| Entity Tracking | `builtin.entity` | `EntityTrackingTabViewModel` | `EntityTrackingTabView` |
| Analysis Engine | `builtin.analysis` | `AnalysisTabViewModel` | `AnalysisTabView` |
| Diagnostics | `builtin.diagnostics` | `DiagnosticsTabViewModel` | `DiagnosticsTabView` |

These VMs already implement the `SnapshotState`/`RestoreState` and activation patterns informally; the
adapter to `IWorkspaceTabViewModel` is thin. **Recommendation:** keep their construction in
`MainViewModel` (they need the rich shell callback wiring) and have `BuiltInTabsModule.CreateTabs` return
descriptors whose `ViewModelFactory` returns the *already-constructed* shell instances. This avoids
re-plumbing the dozens of existing callbacks while still unifying the tab strip. The registry thus
**coexists with** the shell's existing tab ownership rather than replacing it wholesale — lower risk, and
explicitly recommended over a full inversion.

**One exception to "no re-plumbing":** tracker *ownership* moves out of `EntityTrackingTabViewModel` into
`PlaybackController` per §3.7. The EntityTab VM is still reused wholesale for its UI (tree-building, delta
display, class browser); only the tracker-construction/seek logic is lifted into the shared
`EntitySeekService`, and `EntityTab.CurrentTrackerInternal` becomes a pass-through to
`controller.AuthoritativeTracker`. This is a small, targeted change — not a re-plumb of the callback web.

`ActiveContent`/`Activate`/`Deactivate` are helper members on `WorkspaceTabDescriptor` that realize the
View via `ViewFactory`, set its `DataContext` to the (cached) VM, call `OnActivated`/`OnDeactivated`, and
null out `ActiveContent` on deactivation so the View is collectible.

---

## 7. Registration, discovery, and the third-party future

### 7.1 First-party static registration (now)
```csharp
// ModuleRegistry — App project, owned by the composition root.
public sealed class ModuleRegistry
{
    private readonly List<IWorkspaceModule> _modules = [];
    public void Register(IWorkspaceModule module);          // de-dup by Id
    public IReadOnlyList<IWorkspaceModule> Modules => _modules;
}
```
Populated in `App.axaml.cs` *before* `new MainViewModel(...)`, then passed in:
```csharp
var registry = new ModuleRegistry();
registry.Register(new BuiltInTabsModule());        // the four existing tabs
registry.Register(new Playback2DModule());         // the pilot
// (desktop only) registry.Register(...discovered plugins...);   // §7.2
var viewModel = new MainViewModel(windowService, registry);
```
`MainViewModel` ctor enumerates `registry.Modules`, calls `CreateTabs(host)` on each, sorts the resulting
descriptors by `(Placement, Order)`, and fills `Tabs`. **Both hosts** use this path; only first-party
modules are registered on WASM.

### 7.2 Third-party runtime loading (forward-looking, desktop-only)
- **Discovery:** scan a `plugins/` directory next to the executable for `*.dll` accompanied by a JSON
  **manifest** (`module.json`: `id`, `displayName`, `entryType`, `contractVersion`, requested
  `capabilities`). Load each via a dedicated `AssemblyLoadContext` (collectible, so a plugin can be
  unloaded). **Desktop only** — the browser host skips this entirely (no filesystem, no probing).
- **Versioned contract:** `IWorkspaceModule.ContractVersion` is checked against the host's supported
  range at load. Mismatch → the module is listed in the Output panel as "incompatible" and not activated.
  The abstractions assembly (§4.1) is the versioned reference target; SemVer rules: additive context
  members = minor; removals/renames = major.
- **Manifest-declared capabilities:** the manifest lists what the module *requests*; the host grants a
  subset (default: read-only). Granted capabilities are surfaced via `IModuleHost.HasCapability`.

### 7.3 Capability / permission / guardrail model — be honest about net10.0
**There is no CAS and no AppDomain security boundary in net10.0. `AssemblyLoadContext` provides
load/unload, NOT a security sandbox.** This design states that plainly and does not pretend otherwise.
Realistic, *enforceable* guardrails:

1. **Read-only context by construction.** The only handle a module ever gets is `IModuleContext`, which
   exposes no mutators and never the raw tracker/bytes/parser. This is the primary boundary and it is
   real (a module simply has no API to corrupt state).
2. **Capability gating on the few write-ish operations.** `RequestSeekToFrame/Play/Pause` require the
   `Playback.Control` capability (default-denied for third-party; granted to first-party). A read-only
   visualizer never gets to move the clock.
3. **Failure isolation.** Every call into a module (`OnActivated`, `OnDeactivated`, the `Advanced`
   handler, `CreateTabs`) is wrapped in try/catch at the host boundary. An exception disables that module
   (circuit-breaker) and reports it to the Output panel — it never crashes the shell.
4. **UI-thread starvation watchdog.** The `Advanced` dispatch to a module is time-boxed: if a module's
   handler exceeds a budget (e.g. 50 ms) repeatedly, the circuit-breaker trips and the module is
   deactivated. This is the only defense against a misbehaving handler hanging the UI thread, and it is
   best-effort (cooperative — a tight infinite loop on the UI thread cannot be preempted in-process).
5. **Capability list (initial):** `Demo.Read` (frames/metadata), `Entities.Read`, `Analysis.Read`,
   `Playback.Observe` (subscribe to `Advanced`), `Playback.Control` (move the clock), `UI.Contribute`
   (add tabs). First-party modules get all; third-party default to `*.Read` + `Playback.Observe` +
   `UI.Contribute`.
6. **True isolation = out-of-process.** The only way to actually sandbox untrusted native-capable code is
   to run it in a separate process and marshal `IModuleContext` over IPC. This is **explicitly deferred**
   and noted as the path if untrusted third-party plugins ever become a real requirement. In-process
   plugins are a *trust-the-author* model with the cooperative guardrails above — say so to the user.

---

## 8. Protected files & required API additions

### 8.1 Protected files — untouched
`DemoParser.cs`, `DemoFrame.cs`, `BitBuffer.cs`, `LEB128Utils.cs` are the protected parser files. This design
**reads** `DemoFrame`'s public contract and **touches none of them.** All entity/playback state is
consumed through existing public APIs of the **non-protected** `EntityTracker` /`EntityState`/`EntitySet`.

### 8.2 Required additive API (NON-protected file — flagged for owner sign-off)
**File:** `src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs` (NOT on the protected list).
**Why:** the play loop needs an O(1) single-frame forward step from the current position. Today the only
public forward methods (`AdvanceToIndex`, `AdvanceTo`) replay from the start of the passed list, and the
per-frame primitive `ProcessFrame(DemoFrame)` is private. Without this, real-time playback is O(N) per
tick and infeasible.

**Proposed addition (thin, additive — wraps the existing private primitive):**
```csharp
/// <summary>
///   Advances the tracker by exactly one frame from its current position, mutating
///   CurrentEntities / CurrentTick / CurrentFrameIndex in place. Caller guarantees
///   <paramref name="frame"/> is the immediate successor of the last processed frame.
///   Enables real-time playback without the O(N)-from-zero cost of AdvanceToIndex.
/// </summary>
public void AdvanceOneFrame(DemoFrame frame) => ProcessFrame(frame);
```
This is a one-line, behavior-preserving exposure of existing logic. It changes no parse semantics and adds
no state. **Flagged for explicit owner approval** per the "deliberate API addition" bar, even though the
file is not formally protected. If declined, see the §3.5 slicing fallback (less clean, still shippable).

No other parser/entity API additions are required for iteration 1. (A future "interpolation" iteration may
want a `CurrentTick`-relative timing accessor, but snap-to-tick needs nothing new.)

---

## 9. Known tradeoffs

### 9.1 Risks
- **R1 — Authoritative-tracker correctness under mixed seek/step.** The biggest correctness risk:
  interleaving discrete `SeekToFrame` (fresh tracker, checkpoint-replay) with incremental `AdvanceOneFrame`.
  Resolved structurally by §3.7: **the controller is the sole tracker owner** and the only thing that
  creates, swaps, or steps it. A discrete seek **pauses the loop**, builds a freshly-replayed tracker via
  the shared `EntitySeekService`, atomically swaps it in as the authoritative instance, then the loop
  resumes incremental stepping on the new instance. There is never a retained-but-also-replayed instance,
  so step and seek cannot desync. Wave 2 must still add the equivalence test as a gate: "play to frame N"
  must yield the same `EntitySet` as "seek to frame N" (reuse the existing `ParallelDigestEquivalence`
  methodology from `project_load_perf_investigation`).
- **R2 — Coalescing drops a frame the module *needed* to see.** A push-only module that derives state
  *incrementally* from each frame's events would break if intermediate pushes are dropped. Mitigation: the
  snapshot carries `FrameEvents`, but a strictly incremental module should pull, not rely on every push.
  Document: "if your module must observe every frame's events, it needs `Playback.Observe` + a contract
  that the framework will *not* coalesce decodes (it doesn't — only notifications)." The 2D module is
  state-based (reads current positions), so coalescing is harmless for the pilot.
- **R3 — Transient snapshot retained or mutated-during-read.** Two sub-cases. (a) *Retention:* a module
  that stashes the `IReadOnlyEntityView` and reads it after the callback gets stale/garbage data —
  mitigation: document loudly; in debug builds, invalidate the facade after the callback returns and throw
  on access. (b) *Mutate-during-read:* a background writer mutating the `EntitySet` while the module
  iterates it. **Eliminated for the common path** by the §3.6 synchronous-advance decision (same thread
  mutates then reads; no concurrent writer). For the heavy-frame worker path (PM-4), the controller uses
  **strict ping-pong**: it never starts the next advance until the current UI push has been consumed, so
  the worker is never writing the set the module is reading. (Double-buffering the entity view is the
  documented alternative if ping-pong ever proves too coarse; not needed for iteration 1.)
- **R4 — WASM threading.** The background-advance path doesn't exist under WASM. Mitigation: §5.4
  synchronous fallback. Residual risk: jank on large frames at the browser host — accepted for iteration 1.
- **R5 — Built-in-tabs-as-descriptors regresses the dozens of existing shell callbacks.** Mitigation:
  §6.3 keeps the built-in VMs constructed in `MainViewModel` with all current wiring; the descriptor just
  *references* them. No callback re-plumbing (the one targeted exception is tracker ownership, §3.7).
- **R6 — EntityTab rebuilds its tree every render frame while playing (PM-1 spirit violation).** If the
  user plays with the **Entity Tracking** tab active, its groups/fields tree rebuilds on every `Advanced`
  push — expensive, and the one built-in that doesn't honor "cheap per-tick." Accepted known limit for
  iteration 1: real-time playback is expected on the 2D module's tab, not the entity tree. Mitigation if it
  bites: EntityTab can throttle its own rebuild (e.g. rebuild on pause / on a slower cadence) while playing;
  the framework already gives it `IsPlaying` to gate on.

### 9.2 Alternatives rejected
- **A1 — A new clock VM that owns its own tracker, parallel to `EntityTab`'s.** Rejected: that is
  precisely the "two competing playback notions" the task warns against. The controller owns *the*
  tracker; `EntityTab` *reads* it.
- **A2 — Modules get the raw `EntityTracker`.** Rejected: it exposes mutators
  (`RegisterEntityFactory`, `BindLensResolver`, `ResetEntitiesKeepSchema`) that violate the read-only
  boundary and the future capability model.
- **A3 — MS.DI container for module registration.** Rejected: neither host uses MS.DI today; both `new`
  the shell directly. A plain `ModuleRegistry` matches the existing composition style and is cross-host.
  **SUPERSEDED (P1.1 chunk C):** the app now uses a bare Microsoft.Extensions DI container as the single
  composition root; it constructs and holds `ModuleRegistry` as a singleton and injects it into
  `MainViewModel` (both hosts incl. WASM). Rationale: the settings foundation / `IOptionsMonitor<AppSettings>`
  live-reload (P1.1). `ModuleRegistry` stays a plain de-dup list — the container owns its single instance.
- **A4 — `Dock.Avalonia` / docking framework for tabs.** Rejected for iteration 1: a real `TabControl`
  already satisfies the requirement and preserves the unload invariant; a docking lib is a large
  dependency and a separate decision.
- **A5 — Sub-tick interpolation in the framework.** Rejected for iteration 1 per the task steer;
  snap-to-tick keeps the clock simple and the entity reads exact. Interpolation can live module-side later.

### 9.3 Weakest points (where the implementer should be most careful)
- The **coalescing + background-advance + transient-facade** interaction (§3.6, §5, R3) is the subtle
  core. Get the thread hand-off and facade lifetime right first, with the equivalence test (R1) as the
  gate, before wiring any module.
- The **`SelectedTab` ↔ activation lifecycle** must be exactly-once: no double-activate on session
  restore, no missed deactivate on demo unload. Write the state machine explicitly. **Named edge:** the
  *first* tab. When the `ItemsSource` TabControl auto-selects index 0, it may set the bound `SelectedItem`
  without round-tripping through your `OnSelectedTabChanged` setter timing — verify the first descriptor
  actually receives `OnActivated` (explicitly activate the initial `SelectedTab` after `Tabs` is populated
  if the binding doesn't fire it).
- **Session restore ordering:** descriptors must exist before `SelectedTab` is restored; the pending-restore
  pattern already in `MainViewModel` (`_pendingRestore` consumed after `HasFile` flips) is the template.

### 9.4 Phased implementation outline (Wave 2)
**Phase 0 — clock extraction + tracker-ownership migration, no behavior change.** Introduce
`PlaybackController`. Extract the checkpoint-replay seek logic from `EntityTab.SeekEntitiesAsync` into a
shared `EntitySeekService`; make the controller the sole tracker owner (§3.7) with
`EntityTab.CurrentTrackerInternal` repointed at `controller.AuthoritativeTracker`. Move the
`HandleFrameSelectedFromParserTab` fan-out body into `controller.SeekToFrame`. Rewire
`SeekControls`/`ReplayTab`/`Navigation` callbacks to the controller. **No play loop yet.** Existing
discrete navigation must behave identically (regression gate: manual prev/next/step/continue unchanged;
EntityTab still rebuilds correctly after each seek). *The clock lands before it is wired to play, so seek
behavior never regresses.*

**Phase 1 — the additive API + incremental step.** Land `EntityTracker.AdvanceOneFrame` (§8.2, after
owner sign-off). Add `StepForward` on the controller stepping the now-controller-owned authoritative
tracker. Add the play/seek equivalence test (R1).

**Phase 2 — the play loop.** Add the `DispatcherTimer`, `Play`/`Pause`/`Speed`, coalesced `Advanced`,
background advance + transient facade. WASM synchronous fallback (§5.4). Add a Play/Pause/Speed control to
the toolbar (or the Replay tab). Verify PM-1…PM-6 invariants.

**Phase 3 — module framework.** Add the abstractions (`IWorkspaceModule`, `WorkspaceTabDescriptor`,
`IModuleContext`, `IModuleHost`, `IWorkspaceTabViewModel`) and `ModuleRegistry`. Convert the four built-in
tabs to descriptors via `BuiltInTabsModule`. Refactor `MainView`/`MainViewModel` to the `ItemsSource`
TabControl (§6). Verify inactive-content unload still holds (headless Skia test per
`feedback_ui_testing_headless_skia`).

**Phase 4 — the pilot.** Register `Playback2DModule`. Validate end-to-end: play → players move →
inactive-module-zero-cost (PM-1) → state retention across tab switches → WASM smoke test.

**Phase 5 — third-party scaffolding (forward-looking, optional).** `AssemblyLoadContext` discovery +
manifest + capability grants + circuit-breaker/watchdog (§7). Desktop only. Land only when a real
third-party requirement exists; the abstractions assembly is already the stable target from Phase 3.

---

## 10. File / type manifest (what Wave 2 creates)

| New file | Type | Notes |
|---|---|---|
| `src/App/DemoViewer.NET/ViewModels/Playback/PlaybackController.cs` | `PlaybackController` | the clock; owns authoritative tracker + play loop |
| `src/App/DemoViewer.NET/Services/EntitySeekService.cs` | `EntitySeekService` | checkpoint-replay seek logic extracted from `EntityTab.SeekEntitiesAsync`; called by the controller for discrete seeks (§3.7) |
| `src/App/DemoViewer.NET.Modules.Abstractions/IWorkspaceModule.cs` | interface | stable third-party target |
| `…/WorkspaceTabDescriptor.cs` | sealed class | unit of placement + lifecycle |
| `…/IWorkspaceTabViewModel.cs` | interface | `OnActivated`/`OnDeactivated`/snapshot |
| `…/IModuleContext.cs`, `IPlaybackSnapshot.cs`, `IReadOnlyEntityView.cs`, `IReadOnlyEntity.cs` | interfaces | read-only push surface |
| `…/IModuleHost.cs` | interface | creation-time surface + capability query |
| `src/App/DemoViewer.NET/Modules/ModuleRegistry.cs` | sealed class | first-party static registry |
| `src/App/DemoViewer.NET/Modules/BuiltInTabsModule.cs` | `IWorkspaceModule` | wraps the four existing tabs |
| `src/App/DemoViewer.NET/Modules/ModuleContext.cs` | concrete `IModuleContext` | facade over controller/demo/analysis |

| Modified file | Change |
|---|---|
| `src/App/DemoViewer.NET/Views/MainView.axaml` | hard-coded `TabItem`s → `ItemsSource`-driven `TabControl` (§6.1) |
| `src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs` | `Tabs` collection + `SelectedTab`; construct `PlaybackController` + `ModuleContext`; consume `ModuleRegistry`; rewire `SeekControls`/`ReplayTab`/`Navigation` to the controller |
| `src/App/DemoViewer.NET/App.axaml.cs` | build `ModuleRegistry`, register first-party modules, pass into `MainViewModel` (both hosts) |
| `src/App/DemoViewer.NET/ViewModels/SeekControlsViewModel.cs` | (no structural change) callbacks rewired by the shell to the controller |
| `src/App/DemoViewer.NET/ViewModels/Replay/ReplayTabViewModel.cs` | tick-nav commands delegate to the controller |
| `src/App/DemoViewer.NET/ViewModels/EntityTracking/EntityTrackingTabViewModel.cs` | tracker construction/seek lifted into `EntitySeekService`; `CurrentTrackerInternal` repointed at `controller.AuthoritativeTracker` (§3.7); UI-build logic unchanged |

| Recommended addition (owner sign-off) | Change |
|---|---|
| `src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs` | additive `public void AdvanceOneFrame(DemoFrame)` (§8.2) — **not** a protected file, but a deliberate API addition |

---

## 11. Wave-1.5 reconciliation addendum — interface sufficiency for the pilot

> Written after cross-reading this doc against `docs/2d-playback/2d-playback-module-requirements.md`
> (the parallel pilot spec). These amendments are **binding on Wave 2A** (build the amended surface,
> not the §4 draft). They close a contract-sufficiency gap that would otherwise force an interface
> change mid-pilot (Wave 3). Decisions here supersede the conflicting prose in §1.4 / §4.3 of this doc.

### 11.0 Why this addendum exists
The §4 read surface (`IReadOnlyEntityView` = `All`/`OfClass`/`BySerial`, `IModuleContext.Players` =
identity roster) is **under-powered for the pilot's *verified* read needs**, in three ways the
2D-module data audit proved empirically:

1. **No handle resolution.** The pilot must follow handles — `m_hActiveWeapon` and
   `m_hMyWeapons[N]` (active weapon + grenade/inventory), and `m_hController` (pawn↔player). A CS2
   handle masks to an entity **index**, not a serial, so `BySerial` cannot resolve it.
2. **No pawn↔slot join, and `PawnLookup` is unreachable.** The correct pawn-for-slot lookup is the
   reverse `m_hController` scan in `Analysis.Plugins.PawnLookup` (`controller.m_hPawn` is stale
   across deaths). `PawnLookup` takes a concrete `EntityTracker`, which the read-only view
   deliberately hides — so a module restricted to `IReadOnlyEntityView` cannot reach it.
3. **Position is a phantom field.** This doc's §1.4 / §4.3 use `e["m_vecOrigin"]` as the canonical
   read example. **`m_vecOrigin` does not exist as a leaf on `CCSPlayerPawn`, and the generated
   `CSPlayerPawn.Origin` getter reads an unwritten slot → returns `null`.** World position must be
   reconstructed from `CBodyComponent.m_cell{X,Y,Z}` + `m_vec{X,Y,Z}` (see 2D doc §4.1). That
   reconstruction — and its load-bearing `WORLD_HALF_EXTENT` constant — must live in **one**
   host-side place, not be re-rolled (and re-mis-constanted) per module.

**Resolution principle:** the **host performs the CS2 player-join once per tick** and hands every
module a pawn-joined, position-reconstructed `PlayerState` list on the snapshot; the entity view
gains minimal `ResolveHandle`/`ByIndex` for the remaining one-hop weapon lookups. This closes the
gap, puts the constant-sensitive position math in one verified place, and **strengthens** the
third-party guardrail (§7): modules consume a clean join instead of traversing the raw entity graph.

### 11.1 Amended `IReadOnlyEntityView` (add handle/index resolution)
```csharp
public interface IReadOnlyEntityView
{
    IEnumerable<IReadOnlyEntity> All();
    IEnumerable<IReadOnlyEntity> OfClass(string className);   // e.g. "CCSPlayerPawn"
    IReadOnlyEntity? BySerial(int serial);
    IReadOnlyEntity? ByIndex(int entityIndex);                // NEW — entity-array index
    IReadOnlyEntity? ResolveHandle(ulong handle);             // NEW — masks low 14 bits → ByIndex
}
```
`ResolveHandle` is the **only** raw-graph traversal the pilot still needs after the host join, and
it is display-only/one-hop (weapon class + `m_iItemDefinitionIndex` → name). `IReadOnlyEntity`'s
indexer maps to the **allocation-free** `EntityState["path"]` accessor — **never** `EntityState.Fields`
(which rebuilds a full dict per entity; the dominant entity-tracking alloc per
`project_entity_profiling_phase3`). Handle unboxing follows `project_cs2_wire_encoding` (handles
arrive as `UInt64`; coerce, don't `is uint`).

### 11.2 New `PlayerState` (per-tick, host-joined) + amended snapshot/roster
The pivotal amendment. **Two non-overlapping types, split by lifetime — identity vs. state:**

```csharp
// ── Per-tick, host-joined, TRANSIENT (valid only inside the Advanced callback). ──
//    Shape lives in the abstractions assembly; the JOIN LOGIC lives in App's ModuleContext.
public interface PlayerState
{
    int     Slot { get; }                 // stable identity (also on the roster)
    int     Team { get; }                 // VOLATILE — side-swap at half / spectate; lives here, NOT on roster
    bool    HasLivePawn { get; }          // false for spectators/unassigned/pre-spawn → module skips (FR-13)
    IReadOnlyEntity? Pawn { get; }        // current pawn via PawnLookup reverse m_hController join
    IReadOnlyEntity? Controller { get; }  // bound controller
    (float X, float Y, float Z)? WorldPosition { get; }  // reconstructed cell+offset → world; null if no pawn
}

public interface IPlaybackSnapshot
{
    int FrameIndex { get; }
    int Tick { get; }
    IReadOnlyEntityView Entities { get; }
    IReadOnlyList<GameEventView> FrameEvents { get; }
    IReadOnlyList<PlayerState> Players { get; }   // NEW — host did the pawn-join + position once, shared by all modules
}

// ── Stable across the demo: IDENTITY ONLY (team removed — it is volatile, see PlayerState). ──
public sealed class PlayerRosterEntry   // IModuleContext.Players
{
    public int    Slot { get; init; }
    public ulong  SteamId { get; init; }
    public string Name { get; init; }
    // NO Team here. Team is per-tick → PlayerState.Team.
}
```

This makes the two `Players` surfaces **non-redundant by construction**: `IModuleContext.Players`
is stable identity (slot/steamID/name); `snapshot.Players` is volatile per-tick state
(team/pawn/position). The pilot reads `snapshot.Players` each push for markers + the attributes
panel, and joins to `IModuleContext.Players` by `Slot` for name/SteamID.

**The host-join must cover pawn + controller + position so the module never touches `m_hController`
itself.** What remains module-side: weapon/nade resolution via `snapshot.Entities.ResolveHandle(...)`
(§11.1), and the ring-colour delta cache (the module keeps its own per-player `(health, shotsFired)`
history; reset on backward seek per 2D doc NFR-2).

### 11.3 Layering & lifetime (binding on Wave 2A)
- **Shape in abstractions:** `PlayerState`, the amended `IReadOnlyEntityView`/`IPlaybackSnapshot`,
  and `PlayerRosterEntry` live in `DemoViewer.NET.Modules.Abstractions` (kept clean — no Analysis ref).
- **Join logic in App:** the concrete `ModuleContext` (App project, which already references the
  Analysis layer) performs the per-tick join using `PawnLookup` (reverse `m_hController`) and a new
  shared `PositionUtil.CellToWorld(...)`. Abstractions never references `PawnLookup`.
- **Transient + pooled:** `PlayerState` instances are valid **only inside the `Advanced` callback**;
  modules copy out the scalars they need (mirrors the §4.3 transient-facade rule). **Allocation
  decision flagged for 2A:** either pool the ~10 `PlayerState` instances (re-aim backing each push →
  honors PM-3's zero-per-tick-framework-alloc invariant) **or** consciously accept ~10 small
  allocs/push and relax PM-3's wording to "O(players), not O(entities)." Pooling is preferred; make
  the choice explicit, don't leave it implicit.

### 11.4 `WORLD_HALF_EXTENT` — the highest-leverage correctness gate (Wave 2A, gated)
Position reconstruction now lives in the host (`PositionUtil`), so the constant lives there too.
The 2D doc's #1 risk ("until this constant is verified, every plotted position is suspect") is the
single thing most likely to make the pilot render markers in the wrong place — i.e. *not* "mostly
working." **Do not back-solve it from scratch.** Lift the exact constant + formula from the
**demofile-net oracle** (this project's ground-truth, memory: oracle at v0.44.1): read how it
reconstructs cell-coordinate positions (`CNetworkOriginCellCoordQuantizedVector`) and copy its
constant (there is real ambiguity — `1<<14` vs `1<<15`, and how cell width interacts with
quantization — that one oracle read settles deterministically). **Wave 2A gate:** assert a known
decoded pawn position lands on-radar (within map bounds) before any module trusts the join; ship
the assert as a test. `CELL_WIDTH = 1024` is already confirmed from the schema (`m_vec*` range
`[0,1024]`).

### 11.5 Corrections to this doc's earlier prose
- §1.4 and §4.3's `e["m_vecOrigin"]` example is **wrong** and is superseded by §11.2: modules read
  position from `snapshot.Players[i].WorldPosition` (host-reconstructed). A module reaching for
  `m_vecOrigin`/`.Origin` directly gets `null`. The generic `IReadOnlyEntity` indexer still works
  for *other* fields (e.g. `m_iHealth`, `m_flFlashDuration`, `m_iShotsFired`) read off the joined
  `Pawn`.
- §10's file manifest gains: `src/App/DemoViewer.NET/Services/PositionUtil.cs` (cell→world, owns the
  verified constant) and the `PlayerState`/amended-interface files in the abstractions assembly.

### 11.6 Superseded items in the 2D-module doc (for Wave 2B to bind)
The 2D doc was written before this contract was settled and correctly deferred the mechanics (its
§6 "open dependency"). Wave 2B binds these to the as-built interface; the resolved answers are:
- **The module does NOT own an `EntityTracker` and does NOT call `AdvanceToIndex`** (2D doc §6/§10).
  It reads `snapshot.Players` (position/hp/team/pawn) and `snapshot.Entities.ResolveHandle(...)`
  (weapons) inside the `Advanced` callback; the host already advanced the authoritative tracker.
- **Position** comes from `snapshot.Players[i].WorldPosition`, not module-side cell math (2D doc
  §4.1 analysis is what `PositionUtil` implements).
- **Pawn↔slot** comes from the host join (2D doc §8 risk #6 dissolved — module never calls
  `PawnLookup`).
- Clock control (`RequestSeekToFrame`/`RequestPlay`/…) is capability-gated `Playback.Control`,
  granted to this first-party module (§7.3).

---

## 12. Implementation log (Wave 2A — framework only)

Branch `feature/modular-ui-framework` off `main`. Per-phase commits; build + all test
suites green at each phase (only the pre-existing WASM workload warning).

| Phase | Notes / deviations |
|---|---|
| docs | Design docs carried onto the branch. |
| Phase 0 — clock extraction + position fan-out | **Deviation (approved):** the literal tracker-INSTANCE ownership swap was deferred to Phase 1b; Phase 0 extracted `EntitySeekService` and made `PlaybackController` the single position-move code path, honoring the hard "no behavior change" gate. |
| Phase 1a — additive `AdvanceOneFrame` + R1 equivalence gate | Isolated single-purpose change to `EntityTracker.cs` (non-protected). Play-to-N ≡ seek-to-N proven byte-identical. |
| Phase 1b — tracker-instance ownership flip + incremental `StepForward` | Controller is now the sole owner; `StepForward` is genuinely O(1) incremental (discriminating `ReferenceEquals` test). Delta highlighting preserved via snapshot-before-step. `InternalsVisibleTo(App.Tests)` added per the project's test-seam convention. |
| Phase 2 — `DispatcherTimer` play loop | Lean per-tick fan-out (PM-1) — the loop never touches discrete tabs; `Pause()` snaps them. Coalesced `Advanced` (PM-2). Synchronous UI-thread advance (§3.6 threading decision; no worker built). WASM = 32 Hz cap. |
| Phase 3a — abstractions assembly + `PositionUtil` gate | **Deviation (oracle wins):** §11.4's `CELL_WIDTH = 1024` "confirmed" is **wrong**. The demofile-net oracle (`CNetworkOriginCellCoordQuantizedVector`) gives `world = (cell − 32) * 512 + offset` — `CELL_WIDTH = 512`, `WORLD_HALF_EXTENT = 32*512 = 16384 = 1<<14`. The cell multiplier (512 vs 1024) was the real bug the ambiguity hid. Gate test empirically verifies it against off-center decoded pawns (max\|X/Y\| = 1971, Dust2-scale). `PlayerState` keeps its non-`I` spec name (CA1715 suppressed). |
| Phase 3b — `ModuleRegistry` + ItemsSource TabControl + `ModuleContext` host-join | **Allocation decision (§11.3): POOLED** — entity view, per-entity facades, and ~10 `PlayerState` instances are re-aimed each push (zero per-tick framework alloc; PM-3). DataContext model: the three shell-routed built-in views keep the shell as DataContext (descriptor `DataContext = shell`), so no binding rewrites. Inactive-content unload preserved (headless test: exactly one realized View). A `PlaceholderModule` proves registration → activation → inactive-zero-cost (PM-1) without a viewport. |
| Phase 3 fixups (post-review) | **(1) Pause guard:** the lean play loop never sets `SelectedFrame` (PM-1), so the `Pause()` snap's light fan-out sets it, whose setter echoes into `SeekToFrame`. The snap was NOT under the `_applying` guard → the echo would kick the heavy async re-seek (swapping a fresh tracker over the stepped instance) on every Pause. Fixed by wrapping the snap in the guard (same mechanism `StepForward` uses); validated by a deterministic pure-controller re-entrancy test (confirmed to fail when the guard is inert). **(2) Placeholder not shipped:** `PlaceholderModule` is registered only by its PM-1 test, NOT by the production `App.axaml.cs BuildRegistry()` — the shipping shell stays built-ins-only (no empty "Sandbox" tab for users). |

**As-built signatures Wave 3 (the 2D module) should know:**
- World position: `PlayerState.WorldPosition` (host-reconstructed; do NOT re-roll cell math or read `.Origin`/`m_vecOrigin` → null).
- The host already advanced the authoritative tracker; the module reads `snapshot.Players` + `snapshot.Entities.ResolveHandle(...)` inside `Advanced`. It does NOT own a tracker or call `AdvanceToIndex`.
- `IModuleContext.Players` = stable identity (slot/steamID/name, no team); `snapshot.Players` / `IModuleContext.CurrentPlayers` = per-tick `PlayerState` (team/pawn/position). Join by `Slot`.
- `IPlaybackSnapshot` and `PlayerState` are TRANSIENT (pooled) — copy out scalars inside the callback; never retain.
- Clock control (`RequestSeekToFrame`/`RequestPlay`/`RequestPause`) routes to the controller; capability-gated `Playback.Control` (first-party modules granted all of `ModuleHost.FirstPartyCapabilities`).
- `WorkspaceTabDescriptor` for a module-owned tab: set `ViewModelFactory` (lazy, retained per-tab VM implementing `IWorkspaceTabViewModel`) OR `DataContext`; the realized View's DataContext = `DataContext ?? TabViewModel`.

**Carryover (not in this wave's scope):**
- The 2D viewport module itself (Wave 3) — only a no-op placeholder ships here.
- Third-party `AssemblyLoadContext` discovery + manifest + circuit-breaker/watchdog (§7.2/§7.3 Phase 5) — deferred until a real third-party requirement.
- `ReadOnlyEntityView.All()/OfClass()` allocate a facade per element (on-demand reads, not the per-tick hot path); poolable later if a module hammers them.
- WASM 2D-module playback-smoothness validation at 32 Hz (§5.4) — for Wave 3.
