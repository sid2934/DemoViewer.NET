# Phase A1: Timeline, keybinds, follow-by-card, binary-search `SeekToTick`

**Track A (UX on the *current* control, renderer-independent).** Ships BEFORE the Core/Pipeline
projects exist. Nothing in this phase may depend on SkiaSharp, on `Scene2DFrame`, or on any type the
B-track introduces.

Branch: `feature/playback2d-v2`. Repo root: `C:\dev\DemoViewer.NET`.
Authoritative design: `docs/playback2d-v2/design.md` (§5.6 timeline, §7.4 follow, §7.5 keybinds,
§7.7 feature gates, §9 phase table).

> ## Integrator corrections (BINDING: supersede anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry. A1's three
> flagged conflicts are resolved here.
>
> 1. **D6 confirmed by B2 and B5**: `Q`/`E` are round nav and **`X` is erase** (`Ctrl+X` clears
>    all). B5's keybind audit reaches the same conclusion independently; A1's reserved-`X` entry
>    stands and B2 un-reserves it. **D7 (`Space` scope) and D8 (`Esc` scope) confirmed**: B2's
>    `HoldPan`/`CancelGesture` register at `WhenToolActive` and win while a tool is active.
> 2. **The feature-gate seam is `IModuleContext.Features`, and A1 ships it** (this was B5-2; it is
>    pulled forward because A1 already touches all three files and because B2/B3/B4 need one seam,
>    not four). Replace T14's `IFeatureGate`-into-the-module-constructor wiring with:
>    - **Create** `src/App/DemoViewer.NET.Modules.Abstractions/IModuleFeatureGate.cs`:
>      `bool IsEnabled(string featureId)` + `event Action? Changed`; an unknown id fails **open**.
>    - **Modify** `IModuleContext.cs`: add the sixth additive default member
>      `IModuleFeatureGate? Features => null;` (null fails open, so every hand-rolled test double
>      keeps compiling).
>    - **Create** `src/App/DemoViewer.NET/Features/ShellModuleFeatureGate.cs`: wraps the singleton
>      `IFeatureGate` and owns `static IReadOnlySet<string> DesktopOnlyIds` (the single
>      `!OperatingSystem.IsBrowser()` AND site; empty in A1, gains `"playback2d.export"` in B4).
>    - **Modify** `ModuleContext.cs`: `public IModuleFeatureGate? Features { get; private set; }`
>      + `public void SetFeatures(IModuleFeatureGate?)`, mirroring `SetLiveSyncHud`.
>    - **Modify** `App.axaml.cs`: `ctx.SetFeatures(new ShellModuleFeatureGate(gate))` next to the
>      existing `SetLiveSyncHud` wiring. `Playback2DModule`'s parameterless `ViewModelFactory` and
>      `Playback2DTabViewModel`'s parameterless ctor are then **unchanged**, and
>      `IsTimelineEnabled`/`IsFollowEnabled` read
>      `_context?.Features?.IsEnabled("playback2d.timeline") ?? true`, re-resolving on
>      `Features.Changed`. B5-2 becomes an audit.
> 3. **Catalog placement:** A1 creates the one contiguous
>    `// ---- 2D PLAYBACK v2 SUB-FEATURES ----` block **after the `analysis.breakpoints` entry and
>    before the `// ---- CHROME` comment** (not appended at the end of `_catalog`), holding
>    `playback2d.timeline` and `playback2d.follow`. B2/B3/B4 insert `annotations` / `levels.auto` /
>    `export` into the same block; final order is annotations · timeline · levels.auto · follow ·
>    export. All five keep `GroupId = null`, so the `parserDeepDive`/`graphDebug` leader lock is
>    untouched either way. This is purely so the five rows read as one group in Settings.
> 4. **`Playback2DKeymap.Default` is the canonical enumerable** (with `Active`/`Reserved`). B5's
>    conflict test has been retargeted off the non-existent `All`, and A1's global text-input
>    suppression (D12) is the canonical mechanism. There is no per-binding suppression flag.
> 5. **B1 moves the `Timeline/` folder into `…Core.Timeline`** and deletes
>    `TimelineCoreCleanTests`. §4.2's signatures are binding on B1 **and on B2**: `AnnotationTrack`
>    implements all six members (`Id`, `DisplayName`, `IsAvailable`, `BuildMarkers`, `BuildBands`,
>    `MarkersChanged`) and places its markers on the frame-index axis via
>    `ITimelineData.FrameIndexAtTick`. Track ids stay bare words: `round`, `kill`, `bomb`,
>    `annotation`.
> 6. **`ContractVersion 1.2.0` is the whole release's bump** (B5 D7). A1 sets it; B2/B4 must not
>    bump it again. B5-9 audits the comment against what was actually consumed.
> 7. **`DemoTestHelper.FindRepoRoot()` → public** is confirmed and is also used by B5's
>    `Playback2DFeatureWiringTests` and C1's corpus locator.

---

## 1. Scope & exit criterion

Quoted from design §9, the phase table row:

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **A (UX, on the *current* control, renderer-independent)** | A1 | `TimelineControl` + round/kill/bomb tracks; keymap + keybinds; selectable player cards → follow + spectate; binary-search `SeekToTick` | Scrub + keys + follow-by-card shipped | 1.5 wk |

Four deliverables:

1. **`TimelineControl`**: XAML chrome docked at the bottom of the 2D Playback viewport column: a
   rounds band + an intra-round scrub bar carrying event markers, a playhead, hover tooltips and
   drag-scrub. Backed by a declarative `ITimelineTrack` contract with three tracks shipped:
   `RoundTrack`, `KillTrack`, `BombTrack`.
2. **`Playback2DKeymap`**: a declarative action→gesture table, conflict-checked at registration,
   bound on the focusable 2D host. Every playback mutation routes through `PlaybackController`
   commands or capability-gated `IModuleContext.Request*` so LiveSync's `SyncStateObserver` keeps
   observing them.
3. **Selectable player attribute cards**: the right-hand `ItemsControl` becomes a selection surface;
   selecting a card follows that player in the 2D camera AND relays the pick down the existing
   `NotifyFollowSlotChanged` → `IModuleContext.NotifySpectateTarget` → `SyncStateObserver` →
   `SetDesiredSpectator(name)` chain. UI says **"requested"**, never "confirmed".
4. **Binary-search `SeekToTick`**: `PlaybackController.SeekToTick`'s linear scan becomes a binary
   search over the frame list, exposed as a reusable `FrameIndexAtTick(int)` that the timeline uses
   to place tick-stamped markers on a frame-index axis.

**Out of scope for A1** (explicitly, so nobody drifts): annotations and their tools/undo (B2), the
`AnnotationTrack` (B2/B3), levels (B3), export (B4), any Skia work (B1), the CS2 ghost cursor on the
timeline (needs additive `ILiveSyncHudState` members, deferred, see Decisions D10), checkpoint
density / near-playhead seek caching (design §10 risk 4: A1 ships with the existing 150 ms debounce;
see Risks R2).

---

## 2. Decisions made

The design left these open or ambiguous. These calls are binding for A1 and are the ones B1/B2
planners must match.

**D1: `ITimelineTrack` lives app-side in A1, at `DemoViewer.NET.Modules.Playback2D.Timeline`.**
The design places `ITimelineTrack`/`TimelineMarker` in `DemoViewer.NET.Playback2D.Core` (§4), but
Core does not exist until B0. A1 defines the contract in the App project under
`src/App/DemoViewer.NET/Modules/Playback2D/Timeline/`, namespace
`DemoViewer.NET.Modules.Playback2D.Timeline`. **Every type in that folder is written Core-clean**:
no Avalonia types, no `DemoViewer.NET.Modules.Abstractions` types, no parser types, no
`DateTime`/`Stopwatch`/`Random`. B1 moves the folder to Core with a namespace rewrite and nothing
else: no type aliases, no shims. The App-side *consumers* (view-models, the control, the
`ModuleTimelineData` adapter) stay app-side and are excluded from the move; `ModuleTimelineData`
becomes a Pipeline type in B4 if export needs it. An architecture test (§5, `TimelineCoreCleanTests`)
enforces the cleanliness now so the move can't rot.

**D2: `ITimelineTrack` gains `DisplayName`, `IsAvailable(data)` and `BuildBands(data)` beyond the
design's sketch.** The design's sketch is `Id` + `BuildMarkers(ITimelineData)` + `MarkersChanged`.
Rounds are *ranges*, not points, so a band-producing member is required; `IsAvailable` implements
§7.6's "markers only for events the demo has"; `DisplayName` feeds the track-toggle chrome. Signatures
in §4 are binding.

**D3: `ITimelineData` is defined over primitives only, and the adapter does the demo-domain work.**
`GetEventTimeline` returns `GameEventView` (an Abstractions type, illegal in Core), so `ITimelineData`
exposes `TimelineEventRecord(int Tick, int FrameIndex, IReadOnlyDictionary<string,string> Fields)`.
The app-side `ModuleTimelineData` adapter resolves player-slot fields to display names via
`IModuleContext.Players`, normalizes the field keys, and formats values with
`CultureInfo.InvariantCulture`. Tracks never see a `GameEventView`.

**D4: Rounds open at `round_freeze_end`.** Band *i* spans `[frame(round_freeze_end[i]),
frame(round_freeze_end[i+1]) - 1]`; the last band runs to `TotalFrames - 1`. Anything before the
first `round_freeze_end` is one band labelled `warmup` (round number 0). Round numbers are 1-based
ordinals over the freeze-end list. The timeline does **not** read `m_totalRoundsPlayed` (that would
reintroduce a per-frame entity read into chrome). A winner tint is applied from `round_end`'s
`winner` field when that event exists; absent, bands render neutral.

**D5: Marker placement uses `IModuleContext.FrameIndexAtTick`, not tick-space layout.** The
timeline's x-axis domain is **frame index** (design §5.6: "frame index is the movement contract").
Kill/bomb events are tick-stamped, so each marker converts once at track-build time via the new
binary search. A marker whose tick resolves to `-1` (past the end of the frame list) is dropped.

**D6: Round nav is `Q`/`E`; erase is `X`.** Design §7.5 lists both "Q/E round nav" and "E erase":
a direct collision. §1.1's table-stakes line (the CS:DM parity source) lists CS:DM's drawing keys as
`D`, `Esc`, `Ctrl+Z / Ctrl+Shift+Z / Ctrl+X` and does **not** include `E`. So `Q`/`E` keep round nav
and B2's erase tool takes bare `X` (which does not collide with `Ctrl+X` = clear-all). `X` is
declared in the A1 keymap as a **reserved** binding (present in the table, unbound at runtime) so the
conflict checker already protects it.

**D7: `Space` is play/pause in A1; B2's hold-Space-to-pan is tool-scoped.** Design §5.5 wants
hold-Space-to-pan while drawing and §7.5 wants Space = play/pause. A1 has no draw tool, so Space is
unconditionally play/pause. The keymap carries a `Scope` on each binding
(`Playback2DBindingScope.Always` / `.WhenToolActive`) so B2 can register `HoldPan` at
`WhenToolActive` and the router prefers the tool-scoped binding while a Draw/Erase tool is active.
A1 registers only `Always` bindings.

**D8: `Esc` clears follow in A1.** Design says "Esc exit/bail": with no gesture to bail, Esc's A1
meaning is: clear the follow target and return the camera to `Fit`. B2 gives the in-progress gesture
first claim on Esc (via the same `Scope` mechanism), leaving clear-follow as the fallback.

**D9: Kill nav is bound to `Shift+Q` / `Shift+E`.** The design binds no key to the tab's existing
prev/next-kill commands. These are the natural siblings of round nav and collide with nothing.

**D10: No CS2 ghost cursor in A1.** §5.6 calls it optional. It needs `LastCs2DemoTick` +
`TickMapper` exposure through `ILiveSyncHudState`/`IModuleContext`, which is a LiveSync contract
change with its own review surface. Deferred; the timeline VM leaves a single `GhostFrameIndex`
nullable property and the control leaves the visual slot, both unbound.

**D11: No new persisted settings in A1.** Track visibility toggles and timeline show/hide are
session-only (the feature gate is the persisted control). This deliberately avoids the
`SettingsService.WriteInMemory` WASM trap (design §5.4, §8) until B2 introduces `Playback2DSettings`
properly.

**D12: Keys are routed by a TUNNELING handler on the `Playback2DView` root.** Transport keys must
win over the focused control inside the playback surface (a focused overlay `CheckBox` must not eat
`Space`; the new player-card `ListBox` must not eat `↑`/`↓`). The handler skips resolution when the
focused element is a text-input (`TextBox`, `AutoCompleteBox`, or anything with
`TextInputMethodClientRequestedEvent` handling) so a future in-tab text field still types. The
NavStrip frame `TextBox` is outside this subtree and is unaffected either way.

**D13: A1 adds only the two feature-gate ids it ships.** `playback2d.timeline` and
`playback2d.follow`. `playback2d.annotations`, `playback2d.levels.auto` and `playback2d.export` are
added by B2/B3/B4 respectively (ids are persisted keys: adding them early with nothing behind them
puts dead toggles in Settings). Both new entries are appended at the END of `FeatureCatalog._catalog`
with `GroupId = null` so the `parserDeepDive` / `graphDebug` leader-lock ordering is untouched
(`FeatureGateTests` asserts those leaders).

**D14: The timeline is an additional docked row, not a replacement for the bottom-left overlay.**
Design §5.6 says the timeline "absorb[s] the current bottom status bar". A1 adds a ~64 px
`TimelineControl` row at the bottom of the viewport column and moves the `Status` readout into its
footer; the camera-mode `SplitButton` and kill-nav buttons stay in the existing floating overlay
(they are camera/nav controls, not status). Full absorption is B1's host rework.

**D15: Scrubbing pauses playback.** `PlaybackController.SeekToFrame` already calls `StopTimer()`
when `IsPlaying`. A drag-scrub therefore stops auto-play on its first push. This is the existing,
correct behaviour of every discrete seek; the plan does not add a resume-after-scrub.

---

## 3. Ordered work breakdown

Effort figures are for a single implementer. Ordering constraints are called out per task; anything
not constrained can be reordered freely.

### T1. `PlaybackController`: binary-search tick lookup (0.5 d)

**Modify** `src/App/DemoViewer.NET/ViewModels/Playback/PlaybackController.cs`.

Replace the linear scan at `PlaybackController.cs:276-291`:

```csharp
    /// <summary>Selects the first frame whose server tick is at or after <paramref name="tick" />.</summary>
    public void SeekToTick(int tick)
    {
        if (_frames is not { } f) { return; }
        for (int i = 0; i < f.Count; i++)
        {
            if (f[i].ServerTick >= tick) { SeekToFrame(i); return; }
        }
    }
```

with `FrameIndexAtTick` (a `lower_bound` over `DemoFrame.ServerTick`, mirroring
`SemanticNavigator.LowerBound` at `SemanticNavigator.cs:280-297`) plus a `SeekToTick` that delegates.
`ServerTick` is non-decreasing across the frame list, the same assumption the deleted linear scan and
`TickBoundaries.FrameIndices` already make; T15's real-demo test pins it.

Signatures: §4.1. No behaviour change other than complexity (`O(log n)` vs `O(n)`) and the new
public accessor.

*No ordering constraint. Blocks T2.*

### T2: `IModuleContext` additive surface + host wiring (0.5 d)

**Modify** `src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs`: five new
default-implemented members (`TotalFrames`, `FrameIndexAtTick`, `EventFrames`, `IsSpeedLocked`,
`RequestSpeed`). Default implementations keep every existing host/test-double compiling untouched
(the doubles in `Playback2DCameraModeTests.cs:269` etc. implement `IModuleContext` by hand).

**Modify** `src/App/DemoViewer.NET/Modules/ModuleContext.cs`:
- `TotalFrames => _controller.TotalFrames;`
- `FrameIndexAtTick(tick) => _controller.FrameIndexAtTick(tick);`
- `EventFrames(name)` reads `_navigator?.EventBoundaryFramesByName` (already a
  `IReadOnlyDictionary<string,int[]>` at `SemanticNavigator.cs:55`); empty array when the navigator
  is null or the name is absent.
- `IsSpeedLocked => _speedLocked?.Invoke() ?? false;` over a new
  `private Func<bool>? _speedLocked;` plus `public void SetSpeedLock(Func<bool>? isLocked)`, the same
  host-wiring pattern as `SetLiveSyncHud` (`ModuleContext.cs:215`).
- `RequestSpeed(double speed)`: clamp-free (the controller clamps in `OnSpeedChanged`,
  `PlaybackController.cs:175`), no-ops while `IsSpeedLocked`.

**Modify** `src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs` at
`BuildWorkspaceTabs` (`MainViewModel.cs:2167`), immediately after the `new ModuleContext(...)` call:

```csharp
        // The 2D tab's ↑/↓ speed keys must honour the same Live Sync speed lock the NavStrip
        // ComboBox binds its IsEnabled to (MainViewModel.IsPlaybackSpeedLocked, :1129). A parallel
        // path would let a keypress desync a Synced session.
        _moduleContext.SetSpeedLock(() => IsPlaybackSpeedLocked);
```

**Modify** `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DModule.cs`: bump
`ContractVersion` from `new(1, 1, 0)` to `new(1, 2, 0)` and update the comment (design §7.7 requires
a bump "for any additive context consumption").

*Depends on T1. Blocks T4, T11.*

### T3: Timeline contracts (Core-clean) (0.5 d)

**Create** under `src/App/DemoViewer.NET/Modules/Playback2D/Timeline/`:
- `ITimelineTrack.cs`: `ITimelineTrack`, `TimelineMarkerKind`.
- `TimelineMarker.cs`: `TimelineMarker`, `TimelineBand`.
- `ITimelineData.cs`: `ITimelineData`, `TimelineEventRecord`, `TimelineEventKeys` (the normalized
  field-key constants the adapter writes and the tracks read).

All five files: file-scoped namespace `DemoViewer.NET.Modules.Playback2D.Timeline`, `#region`/using
wrapper (repo convention, see any file under `Modules/Playback2D/`), Allman braces, explicit types
(no `var`), 4-space indent, ≤120 cols, LF endings. **No `using Avalonia.*`, no
`using DemoViewer.NET.Modules.Abstractions;`** in this folder (D1).

Signatures: §4.2.

*No ordering constraint. Blocks T4, T5, T6, T7.*

### T4: `ModuleTimelineData` adapter (0.5 d)

**Create** `src/App/DemoViewer.NET/Modules/Playback2D/Timeline/ModuleTimelineData.cs` (app-side:
this file is NOT part of the B1 move).

- Wraps an `IModuleContext`.
- `TotalFrames`, `TickRate`, `FrameIndexAtTick` delegate straight through.
- `FramesForEvent(name)` → `context.EventFrames(name)`.
- `HasEvent(name)` → `context.AvailableEventNames.Contains(name)` (ordinal-ignore-case; the shell's
  dictionary is `StringComparer.OrdinalIgnoreCase`, `SemanticNavigator.cs:55`).
- `EventsOfType(name)` → builds once per name and caches in a
  `Dictionary<string, TimelineEventRecord[]>`:
  - source `context.GetEventTimeline(name)` (host-cached, `ModuleContext.cs:158`);
  - **sort by `Tick`**: `GetEventTimeline` explicitly does not guarantee order
    (`IModuleContext.cs:160`);
  - `FrameIndex = context.FrameIndexAtTick(view.Tick)`; drop records that resolve to `-1`;
  - flatten `view.Fields` into `IReadOnlyDictionary<string,string>` with
    `Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""`;
  - additionally write **resolved display names** for the known slot-bearing keys
    (`attacker`/`userid`/`assister`/`userid_pawn` etc.) under the normalized keys in
    `TimelineEventKeys` (`Attacker`, `Victim`, `Assister`, `Weapon`, `Headshot`, `Site`, `Winner`),
    resolving slot→name against `context.Players`. Reuse the slot-reading shape already in
    `Playback2DTabViewModel.ReadSlot` (`Playback2DTabViewModel.cs:731`). Copy it, do not make the
    VM helper public.
- `Invalidate()` clears the cache (called on `DemoReset`).

*Depends on T2, T3. Blocks T5, T6, T9.*

### T5: `RoundTrack` (0.5 d)

**Create** `src/App/DemoViewer.NET/Modules/Playback2D/Timeline/RoundTrack.cs`.

- `Id = "round"`, `DisplayName = "Rounds"`.
- `IsAvailable(data) => data.HasEvent("round_freeze_end")`.
- `BuildBands(data)`: frames from `data.FramesForEvent("round_freeze_end")` (already sorted,
  de-duplicated: `SemanticNavigator.Build`, `:96-111`). Band construction per **D4**: optional
  leading `warmup` band `[0, firstFreezeEnd-1]` when `firstFreezeEnd > 0`; band *i* =
  `[freeze[i], freeze[i+1]-1]`; last band ends at `TotalFrames - 1`. Label = `"1"`, `"2"`, … (`"wu"`
  for warmup); tooltip = `"Round N"` + winner when known.
- Winner tint: `data.EventsOfType("round_end")`, matched to the band containing its `FrameIndex`,
  `TimelineEventKeys.Winner` → `2` = T ARGB, `3` = CT ARGB, else neutral. `Argb = 0` means "track
  default" and the VM resolves it to a theme token (D1: no brushes in this folder).
- `BuildMarkers(data)` returns empty (rounds are bands only).
- `MarkersChanged` is declared and never raised in A1 (round data is fixed after parse); the event
  exists so B2's `AnnotationTrack` implements the same interface.

*Depends on T3, T4.*

### T6: `KillTrack` + `BombTrack` (0.5 d)

**Create** `KillTrack.cs` and `BombTrack.cs` in the same folder.

- `KillTrack`: `Id = "kill"`, available iff `HasEvent("player_death")`. One marker per record,
  `Kind = TimelineMarkerKind.Kill`, `Glyph = "×"`, tooltip
  `"{Attacker} → {Victim} ({Weapon}){ HS}"`. `Argb = 0` (VM themes it).
- `BombTrack`: `Id = "bomb"`, available iff any of `bomb_planted` / `bomb_defused` / `bomb_exploded`
  exist. One marker per record of each present name with kinds `BombPlant` / `BombDefuse` /
  `BombExplode` and glyphs `"◆"` / `"✂"` / `"✸"`; tooltip includes the site when present.
- Both are stateless and allocate only inside `BuildMarkers` (called once per demo).

*Depends on T3, T4.*

### T7: `Playback2DTimelineViewModel` (0.5 d)

**Create** `src/App/DemoViewer.NET/Modules/Playback2D/Timeline/Playback2DTimelineViewModel.cs`
(app-side; `ObservableObject`, `CommunityToolkit.Mvvm`).

Responsibilities:
- Holds the registered `ITimelineTrack` list, per-track enable flags, and the built band/marker
  view-models (`TimelineBandViewModel`, `TimelineMarkerViewModel`, same file or siblings).
- `Rebuild(ITimelineData data)`: re-runs `IsAvailable`/`BuildBands`/`BuildMarkers` for each enabled
  track, resolves `Argb == 0` to the theme token brush for that kind, and recomputes layout.
- Layout math (pure, unit-testable, no Avalonia needed for the numbers):
  `XForFrame(i) = TotalFrames <= 1 ? 0 : i / (double)(TotalFrames - 1) * PixelWidth`;
  `FrameIndexAt(x) = Math.Clamp((int)Math.Round(x / PixelWidth * (TotalFrames - 1)), 0, TotalFrames-1)`.
  `PixelWidth` is set by the control on size change and re-runs layout only (no track rebuild).
- `UpdatePlayhead(int frameIndex)`: sets `CurrentFrameIndex`, `PlayheadX`, `CurrentRoundLabel`
  (binary search over the band list).
- `RequestSeek(double x)` raises `SeekRequested(frameIndex)`; the owner VM forwards to
  `IModuleContext.RequestSeekToFrame` (design §5.6: raw pushes are safe. The 150 ms debounce plus
  latest-wins coalescing downstream absorb drag bursts).
- **Marker culling:** when two markers of the same track land within 2 px, keep the first and fold
  the rest into its tooltip (`"3 kills"`), so a 90 k-frame demo does not realize 400 visuals.
- `GhostFrameIndex` (nullable, always null in A1, D10).

Signatures: §4.3.

*Depends on T3. Blocks T8, T9.*

### T8: `TimelineControl` XAML (0.5 d)

**Create** `src/App/DemoViewer.NET/Views/Playback2D/TimelineControl.axaml` + `.axaml.cs`
(`UserControl`, `x:DataType="timeline:Playback2DTimelineViewModel"`).

Three rows inside a `Border` using the walled-off `Pb2d*` HUD tokens (design-system D21: do NOT use
the app-chrome ramp; follow `Playback2DView.axaml`'s existing `DynamicResource Pb2d*` usage):

1. **Rounds band** (18 px): `ItemsControl` over `Bands`, `ItemsPanel` = `Canvas`,
   `Canvas.Left`/`Width` bound; each band is a `Border` with the round label, `ToolTip.Tip` bound,
   click → `RequestSeek(band start)`.
2. **Scrub bar** (22 px): a background track `Rectangle`, an `ItemsControl` over `Markers` on a
   `Canvas` (glyph `TextBlock`s, `Canvas.Left` bound, `ToolTip.Tip` bound), and the playhead
   (`Rectangle`, `Canvas.Left` bound to `PlayheadX`).
3. **Footer** (18 px): the `Status` text moved from the floating overlay (D14), the
   `frame N / M · tick T` readout, and per-track `CheckBox`es bound to `Tracks[i].IsEnabled`.

Code-behind (`.axaml.cs`) owns only pointer/size plumbing:
- `SizeChanged` → `vm.PixelWidth = scrubBar.Bounds.Width`.
- `PointerPressed` on the scrub bar → capture, `vm.RequestSeek(pos.X)`.
- `PointerMoved` while captured → `vm.RequestSeek(pos.X)`.
- `PointerReleased` → release capture.
- `PointerMoved` without capture → `vm.HoverFrameIndex` for the hover readout.
- Accessibility: `AutomationProperties.Name` on the scrub bar; the control is `Focusable=false` so
  it never steals the keymap's focus target (T12).

*Depends on T7. Blocks T9.*

### T9: Wire the timeline into the tab (0.25 d)

**Modify** `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs`:
- New `public Playback2DTimelineViewModel Timeline { get; }` built in the ctor with the three tracks
  registered; `Timeline.SeekRequested += i => _context?.RequestSeekToFrame(i);`.
- In `ResyncToCurrentDemo()` (`:439`): build/refresh a `ModuleTimelineData` over `_context` and call
  `Timeline.Rebuild(data)`. This already runs on activation AND on `DemoReset` (`:433`), which is
  exactly the two moments the demo's event set can change.
- In `OnAdvanced(IPlaybackSnapshot)` (`:616`): one call `Timeline.UpdatePlayhead(snapshot.FrameIndex)`
  at the end of the handler. Cheap (a binary search + two property sets); no per-frame allocation.
- In `OnDeactivated()` (`:325`): nothing to unsubscribe (the timeline holds no context
  subscriptions of its own), but null out the adapter so the context isn't retained.

**Modify** `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml`:
- Change the left cell (`<Grid Grid.Column="0">`, line 86) to `RowDefinitions="*,Auto"`, put the
  existing viewport + overlays in row 0, and add
  `<p2d:TimelineControl Grid.Row="1" DataContext="{Binding Timeline}" IsVisible="{Binding $parent[UserControl].((vm:Playback2DTabViewModel)DataContext).IsTimelineEnabled}" />`
  (or simpler: bind `IsVisible` to a `Timeline.IsVisible` flag the VM sets from the gate: preferred,
  avoids the ancestor binding).
- Remove the `Status` `TextBlock` from the bottom-left overlay (lines 124-126); it now lives in the
  timeline footer.

*Depends on T4, T7, T8, T14.*

### T10: `Playback2DKeymap` (0.5 d)

**Create** `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DKeymap.cs`: app-side (it is UI
chrome; it stays in the App project through B1, unlike the Timeline folder).

- `Playback2DAction` enum, `Playback2DBindingScope` enum, `Playback2DBinding` record struct, and the
  static `Playback2DKeymap` table with `Default`, `Active`, `Reserved`, `ShellReservedGestures`,
  `TryResolve`, `FindConflicts`, `GestureText`.
- The A1 table (bound, `Scope = Always`):

  | Gesture | Action | Routes through |
  |---|---|---|
  | `Space` | `TogglePlay` | `IsPlaying ? RequestPause() : RequestPlay()` |
  | `Left` | `StepBack` | `RequestSeekToFrame(CurrentFrameIndex - 1)` |
  | `Right` | `StepForward` | `RequestSeekToFrame(CurrentFrameIndex + 1)` |
  | `Up` | `SpeedUp` | `RequestSpeed(next preset)` |
  | `Down` | `SpeedDown` | `RequestSpeed(prev preset)` |
  | `Q` | `PrevRound` | `RequestPrevEvent(["round_freeze_end"])` |
  | `E` | `NextRound` | `RequestNextEvent(["round_freeze_end"])` |
  | `Shift+Q` | `PrevKill` | existing `PrevKillCommand` → `RequestPrevEvent(["player_death"])` |
  | `Shift+E` | `NextKill` | existing `NextKillCommand` |
  | `F` | `CycleFollowNext` | VM follow funnel (T13) |
  | `Shift+F` | `CycleFollowPrev` | VM follow funnel |
  | `Escape` | `ClearFollow` | VM follow funnel (D8) |

- Reserved (declared, **not** bound in A1, the conflict checker still guards them):
  `D`→`ToolDraw`, `X`→`ToolErase` (D6), `Ctrl+Z`→`Undo`, `Ctrl+Shift+Z`→`Redo`,
  `Ctrl+X`→`ClearAnnotations`, `Space`@`WhenToolActive`→`HoldPan` (D7), `Escape`@`WhenToolActive`→
  `CancelGesture` (D8), `Home`→`FitCamera`.
- `ShellReservedGestures` mirrors `MainView.axaml:22-37`: `Ctrl+P`, `Ctrl+O`, `Ctrl+W`,
  `Ctrl+OemComma`, `Ctrl+B`, `Ctrl+D1`…`Ctrl+D9`.
- `FindConflicts` returns human-readable strings for (a) two bindings sharing a gesture within the
  same scope and (b) any binding colliding with `ShellReservedGestures`. The static ctor calls it and
  **throws `InvalidOperationException`** on a non-empty result: "conflict-checked at registration"
  per design §7.5, failing at first touch rather than silently shadowing.

Signatures: §4.4.

*No ordering constraint. Blocks T11, T12.*

### T11: VM action dispatch + follow funnel (0.5 d)

**Modify** `Playback2DTabViewModel.cs`.

- `public bool ExecuteAction(Playback2DAction action)`: the single dispatch switch. Returns `false`
  for an action it cannot service now (no context, no demo, gate off, reserved action), which the
  view uses to decide whether to mark the key `Handled`.
- Speed presets: `0.25, 0.5, 1, 2, 4, 8` (the same list as `NavStrip.axaml:125-132`). `SpeedUp`/
  `SpeedDown` step within the list from the nearest current value; both no-op when
  `_context.IsSpeedLocked` (T2) and set `SpeedLockNote` for a one-shot footer hint.
- Follow funnel: **one** method everything calls:
  ```csharp
  internal void NotifyFollowSlotChanged(int slot)   // existing signature preserved
  ```
  now sets `FollowedSlot`, updates `PlayerAttributes.IsFollowed` on every row, sets `SelectedPlayer`,
  raises the existing `FollowSlotChanged` event (`:420`) and calls
  `_context?.NotifySpectateTarget(slot)` (`:426`), i.e. **the LiveSync chain is byte-identical**;
  only the callers grow. `FollowPlayerCommand(int slot)`, `ClearFollowCommand()`, `CycleFollow(int
  direction)` all funnel through it (`ClearFollow` passes `-1` and skips the spectate notify).
- `public event Action? FitRequested;`: raised by `ClearFollow` so the view can call
  `Playback2DViewport.FitToExtent()` (`Playback2DViewport.cs:394`); the VM never touches the control.
- `FollowStatus` string: `"following {name} · requested"` / `""`, the design's §7.4
  "requested, not confirmed" wording, shown in the timeline footer.

*Depends on T2, T10.*

### T12: Key routing on the view (0.25 d)

**Modify** `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml.cs`:

```csharp
        // Tunnel, not bubble: transport keys must win over whatever inside the playback surface has
        // focus (an overlay CheckBox would otherwise eat Space; the player-card ListBox would eat
        // Up/Down). Skipped while a text input has focus so a future in-tab field still types.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
```

`OnKeyDown` → bail when `e.Handled`, bail when `TopLevel.GetTopLevel(this)?.FocusManager?
.GetFocusedElement()` is a `TextBox`/`AutoCompleteBox`, then
`Playback2DKeymap.TryResolve(e.Key, e.KeyModifiers, out Playback2DAction action)` and
`e.Handled = vm.ExecuteAction(action)`.

Also make the view focusable and give it focus on pointer press over the viewport, so the tab
receives keys after a click (`Playback2DViewport` is already `Focusable = true`,
`Playback2DViewport.cs:127`, a focus request on the viewport is enough because the tunnel handler
sits on the ancestor).

Subscribe `vm.FitRequested` → `_viewport?.FitToExtent()` in the same place the view already
attaches to the VM.

*Depends on T10, T11.*

### T13: Selectable player cards (0.5 d)

**Modify** `src/App/DemoViewer.NET/Modules/Playback2D/PlayerAttributes.cs`: add
`[ObservableProperty] private bool _isFollowed;` (drives the card's selected/followed treatment).

**Modify** `Playback2DView.axaml` attributes panel (lines 304-404):
- `ItemsControl` → `ListBox` with `ItemsSource="{Binding Attributes}"`,
  `SelectedItem="{Binding SelectedPlayer, Mode=TwoWay}"`, `SelectionMode="Single"`,
  `Background="Transparent"`, `BorderThickness="0"`, and a `ListBoxItem` style that strips the Fluent
  chrome (`Padding=0`, transparent background, `Focusable=False`) so the existing card `Border`
  template renders unchanged.
- The card `Border` gains `Classes.followed="{Binding IsFollowed}"` and a local style giving it the
  `Pb2dPositive` border + a `⦿ following (requested)` chip in the header row.
- Keep `IsVisible="{Binding InMatch}"`. A hidden container must still not be selectable; add
  `IsHitTestVisible="{Binding InMatch}"` on the item, or filter in the VM. (Prefer the existing
  `IsVisible`. Avalonia's ListBox will not select an invisible item via pointer, and the keyboard
  path is disabled by D12.)

**Modify** `Playback2DTabViewModel.cs`: `[ObservableProperty] private PlayerAttributes?
_selectedPlayer;` with `partial void OnSelectedPlayerChanged(PlayerAttributes? value)` calling the
follow funnel (guarded against re-entrancy when the funnel sets `SelectedPlayer` itself).

**Modify** `Playback2DView.axaml.cs`: subscribe to the VM's `FollowSlotChanged` and mirror it onto
`_viewport.FollowSlot` (which implies `CameraMode.FollowPlayer`, `Playback2DViewport.cs:195-203`);
`-1` instead calls `FitToExtent()`. The existing `FollowSlot(int, string)` code-behind method
(`Playback2DView.axaml.cs:151`) is reduced to `vm.NotifyFollowSlotChanged(slot)`. The SplitButton
menu keeps working and now goes through the same funnel.

*Depends on T11. Touches the same axaml as T9. Do T9 first.*

### T14: Feature gates (0.25 d)

**Modify** `src/App/DemoViewer.NET/Features/FeatureCatalog.cs`: create the
`// ---------------- 2D PLAYBACK v2 SUB-FEATURES ----------------` block after the
`analysis.breakpoints` entry and before the `// ---------------- CHROME` comment (integrator
correction 3), holding these two entries. `FeatureScope.SubFeature`, `ParentId = "tab.playback2d"`,
`GroupId = null`, `Defaults(true, true, true)`; B2/B3/B4 insert their rows into the same block:

```csharp
        new(
            "playback2d.timeline", FeatureScope.SubFeature, "Playback timeline",
            "Scrubbable round / kill / bomb timeline under the 2D playback view.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.follow", FeatureScope.SubFeature, "Follow player",
            "Select a player card to follow them in the 2D camera (and in CS2 while Live Sync is active).",
            "tab.playback2d", null, false, Defaults(true, true, true))
```

**Ship the module gate seam** per integrator correction 2: `IModuleFeatureGate` (Abstractions),
`IModuleContext.Features` (additive, default `null`), `ShellModuleFeatureGate` (+ `DesktopOnlyIds`),
`ModuleContext.SetFeatures`, and the `ctx.SetFeatures(...)` call in `App.axaml.cs` beside the
existing `SetLiveSyncHud` wiring. **`Playback2DModule` and `Playback2DTabViewModel` keep their
parameterless constructors**. No `IFeatureGate` is injected anywhere, so `Playback2DModule.cs:44`'s
factory and every existing test double are untouched.

`IsTimelineEnabled` / `IsFollowEnabled` read
`_context?.Features?.IsEnabled("playback2d.timeline") ?? true`. Gate reads **fail open** on a null
projection (matching `MainViewModel.IsTabEnabled`'s documented null-gate behaviour at
`App.axaml.cs:673`) and re-resolve on `Features.Changed` (the `highlights.encoding` lesson at
`FeatureCatalog.cs:122`: a one-shot read leaves the surface wrong until the tab is rebuilt).

*Blocks T9 (the timeline's `IsVisible`) and T13 (follow gating).*

### T15: Tests (1.0 d, split as needed)

Per §5. Add the new test classes; they are auto-discovered by `scripts/test-app-suite.sh`'s
source-grep partitioner (no registration needed).

*Depends on everything it covers.*

### T16: Docs (0.25 d)

- `docs/ui/design-system.md`: a `TimelineControl` section (tokens used, the three rows, the
  Pb2d-palette rule) alongside the existing NavStrip section.
- A keybind table in the same doc (or `docs/playback2d-v2/keybinds.md`) generated from
  `Playback2DKeymap.Default` so it can't drift; a test asserts the doc's row count matches
  `Active.Count` (optional, skip if it feels precious).

---

## 4. Public API contracts

**These are binding for other phases.** Signatures below are exact; XML doc comments are required by
`GenerateDocumentationFile=true` on every public member (`CS1591` is in `NoWarn`, but the repo
documents public API anyway. Follow the surrounding style).

### 4.1 `PlaybackController` (modified)

`src/App/DemoViewer.NET/ViewModels/Playback/PlaybackController.cs`

```csharp
/// <summary>
///     The frame index of the FIRST frame whose <see cref="DemoFrame.ServerTick"/> is at or after
///     <paramref name="tick"/>, or -1 when no demo is loaded or every frame precedes it. O(log n)
///     binary search (std lower_bound) over the frame list, which is tick-ordered by construction,
///     the same invariant <c>TickBoundaries.FrameIndices</c> and the previous linear scan relied on.
///     Pure: it moves nothing. The 2D timeline uses it to place tick-stamped event markers on the
///     frame-index axis.
/// </summary>
public int FrameIndexAtTick(int tick);

/// <summary>Selects the first frame whose server tick is at or after <paramref name="tick" />.</summary>
public void SeekToTick(int tick);   // unchanged signature; now delegates to FrameIndexAtTick
```

### 4.2 Timeline contracts (A1: app-side; B1: moved to Core verbatim)

`src/App/DemoViewer.NET/Modules/Playback2D/Timeline/` · namespace
`DemoViewer.NET.Modules.Playback2D.Timeline`

```csharp
public enum TimelineMarkerKind
{
    Round, Kill, BombPlant, BombDefuse, BombExplode, Annotation, Custom
}

/// <summary>A point event on the timeline. ARGB 0 = "use the track/kind default" (the host themes it).</summary>
public readonly record struct TimelineMarker(
    string TrackId,
    int FrameIndex,
    int Tick,
    TimelineMarkerKind Kind,
    string Glyph,
    string Tooltip,
    uint Argb);

/// <summary>A half-open-inclusive frame range on the timeline (rounds today; segments later).</summary>
public readonly record struct TimelineBand(
    string TrackId,
    int StartFrameIndex,
    int EndFrameIndex,
    string Label,
    string Tooltip,
    uint Argb);

/// <summary>Normalized field keys <see cref="ITimelineData"/> adapters write and tracks read.</summary>
public static class TimelineEventKeys
{
    public const string Attacker = "attacker";
    public const string Victim = "victim";
    public const string Assister = "assister";
    public const string Weapon = "weapon";
    public const string Headshot = "headshot";
    public const string Site = "site";
    public const string Winner = "winner";
}

/// <summary>One decoded demo event, already resolved onto the frame axis and flattened to strings.</summary>
public readonly record struct TimelineEventRecord(
    int Tick,
    int FrameIndex,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
///     The demo-shaped facts a track needs, in primitives only: no parser, host or UI types, so the
///     contract moves to Core unchanged. Implementations cache; a track may call any member freely.
/// </summary>
public interface ITimelineData
{
    int TotalFrames { get; }
    int TickRate { get; }

    /// <summary>First frame index at/after <paramref name="tick"/>, or -1.</summary>
    int FrameIndexAtTick(int tick);

    /// <summary>Sorted, de-duplicated frame indices carrying <paramref name="eventName"/>; empty when absent.</summary>
    IReadOnlyList<int> FramesForEvent(string eventName);

    /// <summary>Every occurrence of <paramref name="eventName"/>, sorted by tick; empty when absent.</summary>
    IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName);

    /// <summary>Whether this demo carries <paramref name="eventName"/> at all.</summary>
    bool HasEvent(string eventName);
}

/// <summary>
///     One contributor of timeline content. Registration order is display order within its row.
///     Implementations are stateless w.r.t. the demo. Everything comes from <see cref="ITimelineData"/>.
/// </summary>
public interface ITimelineTrack
{
    /// <summary>Stable key: feature gates, settings, track toggles. Never renamed once shipped.</summary>
    string Id { get; }

    /// <summary>Human-readable name for the track-toggle chrome.</summary>
    string DisplayName { get; }

    /// <summary>False when this demo carries none of the events the track needs (design §7.6).</summary>
    bool IsAvailable(ITimelineData data);

    /// <summary>Point markers, ascending by frame index. Empty for band-only tracks.</summary>
    IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data);

    /// <summary>Range bands, ascending and non-overlapping. Empty for point-only tracks.</summary>
    IReadOnlyList<TimelineBand> BuildBands(ITimelineData data);

    /// <summary>Raised when the track's content changed and the host must re-query it.</summary>
    event Action? MarkersChanged;
}

public sealed class RoundTrack : ITimelineTrack { public RoundTrack(); }
public sealed class KillTrack  : ITimelineTrack { public KillTrack();  }
public sealed class BombTrack  : ITimelineTrack { public BombTrack();  }
```

### 4.3 Timeline view-model (app-side; stays app-side through B1)

```csharp
namespace DemoViewer.NET.Modules.Playback2D.Timeline;

public sealed partial class Playback2DTimelineViewModel : ObservableObject
{
    public Playback2DTimelineViewModel();

    public IReadOnlyList<TimelineTrackToggle> Tracks { get; }
    public ObservableCollection<TimelineBandViewModel> Bands { get; }
    public ObservableCollection<TimelineMarkerViewModel> Markers { get; }

    /// <summary>Feature-gate + has-demo visibility for the whole control.</summary>
    public bool IsVisible { get; set; }

    public int TotalFrames { get; }
    public int CurrentFrameIndex { get; }
    public int CurrentTick { get; }
    public double PlayheadX { get; }
    public string CurrentRoundLabel { get; }
    public string StatusText { get; set; }        // moved from the floating overlay (D14)
    public string FollowStatus { get; set; }      // "following X · requested" (design §7.4)
    public int? GhostFrameIndex { get; set; }     // reserved, always null in A1 (D10)

    /// <summary>Scrub-bar width in px; set by the control on size change. Re-lays out, never rebuilds.</summary>
    public double PixelWidth { get; set; }

    public void RegisterTrack(ITimelineTrack track);
    public void Rebuild(ITimelineData? data);
    public void UpdatePlayhead(int frameIndex, int tick);
    public void SetTrackEnabled(string trackId, bool enabled);

    public double XForFrame(int frameIndex);
    public int FrameIndexAt(double x);

    /// <summary>Raised on click / drag-scrub with the target frame index. The owner forwards it to
    /// <c>IModuleContext.RequestSeekToFrame</c>. The timeline never moves the clock itself.</summary>
    public event Action<int>? SeekRequested;

    public void RequestSeek(double x);
}

public sealed partial class TimelineMarkerViewModel : ObservableObject
{
    public string TrackId { get; }
    public int FrameIndex { get; }
    public int Tick { get; }
    public TimelineMarkerKind Kind { get; }
    public string Glyph { get; }
    public string Tooltip { get; }
    public IBrush Brush { get; }
    public double X { get; }
}

public sealed partial class TimelineBandViewModel : ObservableObject
{
    public string TrackId { get; }
    public int StartFrameIndex { get; }
    public int EndFrameIndex { get; }
    public string Label { get; }
    public string Tooltip { get; }
    public IBrush Brush { get; }
    public double X { get; }
    public double Width { get; }
}

public sealed partial class TimelineTrackToggle : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }
    public bool IsAvailable { get; }
    public bool IsEnabled { get; set; }
}
```

### 4.4 Keymap

```csharp
namespace DemoViewer.NET.Modules.Playback2D;

public enum Playback2DAction
{
    None,
    TogglePlay, StepBack, StepForward, SpeedUp, SpeedDown,
    PrevRound, NextRound, PrevKill, NextKill,
    CycleFollowNext, CycleFollowPrev, ClearFollow,
    FitCamera,
    // Declared in A1, bound by B2:
    ToolDraw, ToolErase, CancelGesture, Undo, Redo, ClearAnnotations, HoldPan
}

/// <summary>When a binding applies. B2's tool-scoped bindings take precedence while a tool is active.</summary>
public enum Playback2DBindingScope { Always, WhenToolActive }

public readonly record struct Playback2DBinding(
    Playback2DAction Action,
    Key Key,
    KeyModifiers Modifiers,
    Playback2DBindingScope Scope,
    string Description,
    bool IsReserved);

public static class Playback2DKeymap
{
    /// <summary>Every declared binding, bound and reserved. Conflict-checked in the static ctor.</summary>
    public static IReadOnlyList<Playback2DBinding> Default { get; }

    /// <summary>The subset actually routed in this build (<c>IsReserved == false</c>).</summary>
    public static IReadOnlyList<Playback2DBinding> Active { get; }

    /// <summary>Declared-but-unbound bindings future phases will claim.</summary>
    public static IReadOnlyList<Playback2DBinding> Reserved { get; }

    /// <summary>The shell accelerators from MainView.axaml the tab must never shadow.</summary>
    public static IReadOnlyList<(Key Key, KeyModifiers Modifiers)> ShellReservedGestures { get; }

    /// <summary>Resolves a keypress to an action. Pure: the primary, Avalonia-event-free overload.</summary>
    public static bool TryResolve(Key key, KeyModifiers modifiers, bool toolActive,
        out Playback2DAction action);

    /// <summary>Convenience overload for the view's KeyDown handler.</summary>
    public static bool TryResolve(KeyEventArgs e, bool toolActive, out Playback2DAction action);

    /// <summary>
    ///     Human-readable conflict list: duplicate gestures within a scope, and collisions with
    ///     <see cref="ShellReservedGestures"/>. Empty = clean. The static ctor throws on non-empty.
    /// </summary>
    public static IReadOnlyList<string> FindConflicts(
        IEnumerable<Playback2DBinding> bindings,
        IEnumerable<(Key Key, KeyModifiers Modifiers)> shellReserved);

    /// <summary>Display text for an action's gesture (e.g. "Shift+E"), "" when unbound. For tooltips.</summary>
    public static string GestureText(Playback2DAction action);
}
```

### 4.5 `IModuleContext` additions (additive, all default-implemented)

`src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs`

```csharp
    /// <summary>Total frames in the loaded demo, 0 when none. The timeline's x-axis domain.</summary>
    int TotalFrames => 0;

    /// <summary>
    ///     First frame index at/after <paramref name="tick"/>, or -1 when unknown / past the end.
    ///     Binary search on the host; the seam that lets a module place tick-stamped events on the
    ///     frame-index movement axis without re-scanning frames.
    /// </summary>
    int FrameIndexAtTick(int tick) => -1;

    /// <summary>
    ///     Sorted, de-duplicated FRAME indices carrying <paramref name="eventName"/>, the module-facing
    ///     projection of the shell's SemanticNavigator index (the same array its Next/Prev use). Empty
    ///     when the demo lacks the event or the host exposes no navigator.
    /// </summary>
    IReadOnlyList<int> EventFrames(string eventName) => Array.Empty<int>();

    /// <summary>
    ///     True while playback speed is pinned by the host (a Live Sync session without the plugin's
    ///     timescale capability). A module surfaces the lock rather than fighting it.
    /// </summary>
    bool IsSpeedLocked => false;

    /// <summary>
    ///     Requests a playback-speed change (capability-gated; clamped host-side to [0.25, 8]).
    ///     No-op while <see cref="IsSpeedLocked"/>.
    /// </summary>
    void RequestSpeed(double speed) { }
```

Plus the sixth additive member, the module feature-gate projection (integrator correction 2: this
was B5-2, pulled forward so the whole track has one seam):

```csharp
    /// <summary>
    ///     The live feature-gate projection, or <c>null</c> for a host / test double that does not
    ///     gate. <b>Null fails OPEN.</b> The shell folds platform ANDs (desktop-only ids) in on its
    ///     side, so a module never re-derives them.
    /// </summary>
    IModuleFeatureGate? Features => null;
```

```csharp
namespace DemoViewer.NET.Modules.Abstractions;

public interface IModuleFeatureGate
{
    /// <summary>Live answer; re-query on <see cref="Changed"/>, never cache for a tab's lifetime.
    /// An id the host does not know fails OPEN.</summary>
    bool IsEnabled(string featureId);

    /// <summary>Raised on the UI thread when any gate answer may have changed.</summary>
    event Action? Changed;
}
```

`ModuleContext` (host) additionally gains:

```csharp
    /// <summary>Wires the host's speed-lock predicate (mirrors <see cref="SetLiveSyncHud"/>).</summary>
    public void SetSpeedLock(Func<bool>? isLocked);

    /// <summary>Sets the shell's feature projection once at composition (mirrors SetLiveSyncHud).</summary>
    public void SetFeatures(IModuleFeatureGate? features);
```

and the App gains `ShellModuleFeatureGate : IModuleFeatureGate, IDisposable` with
`public static IReadOnlySet<string> DesktopOnlyIds { get; }`: the single
`!OperatingSystem.IsBrowser()` AND site for module features (empty in A1; B4 adds
`"playback2d.export"`).

### 4.6 `Playback2DTabViewModel` additions

```csharp
    public Playback2DTabViewModel();              // UNCHANGED: gates arrive via IModuleContext.Features

    public Playback2DTimelineViewModel Timeline { get; }
    public bool IsTimelineEnabled { get; }        // playback2d.timeline, fail-open, live on gate Changed
    public bool IsFollowEnabled { get; }          // playback2d.follow,   fail-open, live on gate Changed

    /// <summary>The followed roster slot, -1 = none. Set only through the follow funnel.</summary>
    public int FollowedSlot { get; }

    /// <summary>Two-way bound to the player-card ListBox; setting it follows that player.</summary>
    public PlayerAttributes? SelectedPlayer { get; set; }

    [RelayCommand] public void FollowPlayer(int slot);
    [RelayCommand] public void ClearFollow();
    public void CycleFollow(int direction);       // +1 next, -1 previous, over FollowablePlayers

    /// <summary>Dispatches a keymap action. False = not serviceable now (the view leaves the key unhandled).</summary>
    public bool ExecuteAction(Playback2DAction action);

    /// <summary>Asks the view to re-fit the camera (the VM never touches the control).</summary>
    public event Action? FitRequested;

    // UNCHANGED, still the single LiveSync spectate funnel:
    public event Action<int>? FollowSlotChanged;
    internal void NotifyFollowSlotChanged(int slot);
```

`PlayerAttributes` gains `public bool IsFollowed { get; set; }` (`[ObservableProperty]`).

---

## 5. Test plan

Everything lives in `src/App/DemoViewer.NET.App.Tests` (TUnit; `[Test]`,
`await Assert.That(x).IsEqualTo(y)`; `[NotInParallel]` on anything touching the headless session;
underscored method names are allowed via the project's `NoWarn=CA1707`).

**Prerequisite:** make `DemoTestHelper.FindRepoRoot()` public
(`src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs:230`: currently `private static string?`).
`Playback2DKeybindConflictTests` needs it to read `MainView.axaml` from source.

### Direct-execution tests (no Avalonia platform, no `HeadlessSession`)

| Class | Cases |
|---|---|
| `PlaybackControllerTickSeekTests` | `FrameIndexAtTick_EmptyController_ReturnsMinusOne`; `FrameIndexAtTick_ExactTick_ReturnsFirstFrameOfThatTick`; `FrameIndexAtTick_BetweenTicks_ReturnsNextFrame`; `FrameIndexAtTick_BeyondLastTick_ReturnsMinusOne`; `FrameIndexAtTick_BeforeFirstTick_ReturnsZero`; `SeekToTick_MovesToSameFrameAsLinearScan` (property-style over a synthetic 5 000-frame tick-repeating list, the exact oracle the deleted scan implemented) |
| `TimelineTrackTests` | `RoundTrack_BuildsOneBandPerFreezeEnd`; `RoundTrack_PrependsWarmupBandWhenFirstFreezeEndIsNotFrameZero`; `RoundTrack_LastBandEndsAtLastFrame`; `RoundTrack_TintsBandFromRoundEndWinner`; `RoundTrack_UnavailableWhenDemoHasNoFreezeEnd`; `KillTrack_MarkerPerDeath_SortedByFrame`; `KillTrack_TooltipCarriesAttackerVictimWeapon`; `KillTrack_DropsEventsPastEndOfFrameList`; `BombTrack_ProducesPlantDefuseExplodeKinds`; `BombTrack_UnavailableWithoutBombEvents`. All against a hand-rolled `FakeTimelineData : ITimelineData`. |
| `TimelineLayoutTests` | `XForFrame_MapsZeroToLeftEdgeAndLastToRightEdge`; `FrameIndexAt_RoundTripsWithXForFrame`; `FrameIndexAt_ClampsOutOfRange`; `SingleFrameDemo_DoesNotDivideByZero`; `ZeroPixelWidth_ProducesNoNaN`; `Rebuild_WithNullData_ClearsBandsAndMarkers`; `Markers_WithinTwoPixels_AreCoalescedIntoOne`; `UpdatePlayhead_SetsRoundLabelFromBand` |
| `Playback2DKeymapTests` | `DefaultTable_HasNoInternalConflicts`; `ActiveBindings_ExcludeReserved`; `TryResolve_SpaceIsTogglePlay`; `TryResolve_ShiftE_IsNextKill_NotNextRound` (D6/D9 regression); `TryResolve_ReservedGesture_ReturnsFalseInA1`; `TryResolve_ToolActive_PrefersToolScopedBinding` (D7: proves the scope mechanism before B2 needs it); `GestureText_FormatsModifiers` |
| `Playback2DKeybindConflictTests` | `Keymap_DoesNotCollideWithShellAccelerators`: reads `src/App/DemoViewer.NET/Views/MainView.axaml` from the repo root, regexes `Gesture="([^"]+)"` out of the `UserControl.KeyBindings` block, parses each with `KeyGesture.Parse`, asserts disjointness from `Playback2DKeymap.Default`. **This is the guard that catches a future shell binding stealing a 2D key** (design §7.5). Skips with `SkipTestException` if the file can't be located. |
| `Playback2DActionDispatchTests` | Against a recording `IModuleContext` double (copy the `ModeFakeContext` shape from `Playback2DCameraModeTests.cs:269`): `TogglePlay_CallsRequestPlayThenRequestPause`; `StepForward_RequestsCurrentPlusOne`; `StepBack_AtFrameZero_DoesNotRequestNegative`; `SpeedUp_WalksThePresetLadder`; `SpeedUp_WhenLocked_DoesNotRequestSpeed` (LiveSync interlock); `NextRound_RequestsNextEventWithFreezeEndFilter` (proves D4 + the "route through Request*" rule); `CycleFollowNext_WrapsAroundFollowablePlayers`; `ClearFollow_RaisesFitRequestedAndDoesNotNotifySpectate` |
| `Playback2DFollowFunnelTests` | `SelectingCard_RaisesFollowSlotChangedOnce`; `SelectingCard_CallsNotifySpectateTarget` (**the LiveSync chain regression test**: `SyncStateObserver.OnSpectateTargetChanged`, `SyncStateObserver.cs:91`, is what consumes it); `MenuPickAndCardPick_TakeTheSameFunnel`; `FollowedSlot_SetsIsFollowedOnExactlyOneRow`; `ClearFollow_ResetsEveryIsFollowed`; `FollowStatus_SaysRequested_NeverConfirmed` (design §7.4 wording) |
| `ModuleTimelineDataTests` | `EventsOfType_SortsByTick` (GetEventTimeline order is explicitly unguaranteed, `IModuleContext.cs:160`); `EventsOfType_CachesPerName`; `EventsOfType_ResolvesSlotFieldsToRosterNames`; `EventsOfType_DropsUnresolvableTicks`; `Invalidate_ClearsCache` |
| `TimelineCoreCleanTests` | **Architecture test (design §11).** Reflects over every type in the
`DemoViewer.NET.Modules.Playback2D.Timeline` namespace whose source file lives in
`Modules/Playback2D/Timeline/` *except* the app-side allow-list (`ModuleTimelineData`,
`Playback2DTimelineViewModel`, `Timeline*ViewModel`, `TimelineTrackToggle`) and asserts: no member
signature or field references `Avalonia.*`, `DemoViewer.NET.Modules.Abstractions.*`, `CS2DemoKit.*`,
`System.DateTime`, `System.Diagnostics.Stopwatch` or `System.Random`. This is what makes B1's move
mechanical; **B1 deletes this test and replaces it with Core's own reference test.** |

### Headless-Avalonia tests (`HeadlessSession.RunOnUi`, `[NotInParallel]`)

| Class | Cases |
|---|---|
| `Playback2DTimelineRenderTests` | `Timeline_RendersNonBlank_WithRoundsAndKills`: build a `Playback2DView` with a fake context carrying synthetic freeze-end/kill events, `window.Show()`, pump `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`, `window.CaptureRenderedFrame()`, scan the timeline row's pixel band for non-background pixels (reuse `ScanNonBackground` from `Playback2DCameraModeTests.cs:245`). Save the frame to `HeadlessSession.ArtifactDir` (the `ZRadarRenderTests` pattern). `Timeline_HiddenWhenFeatureGateOff`. |
| `Playback2DTimelineScrubTests` | `PointerPressOnScrubBar_RequestsSeekToProportionalFrame`; `PointerDragAcrossScrubBar_PushesMonotonicallyIncreasingFrames`; `ClickOnRoundBand_SeeksToBandStart`: assert on the recording context's `RequestSeekToFrame` log, not on pixels. |
| `Playback2DKeyRoutingTests` | `SpaceOverFocusedCheckbox_TogglesPlay_NotTheCheckbox` (**the D12 regression**: focus an overlay `CheckBox`, send `Space` via `window.KeyPressQmk`/`KeyPress`, assert `RequestPlay` fired and `ShowRadar` is unchanged); `ArrowKeys_DoNotChangeListBoxSelection`; `TextBoxFocused_KeysAreNotIntercepted` (temporarily add a `TextBox` to the tested tree, or assert the guard directly by focusing the NavStrip-shaped input in a synthetic host); `EscapeClearsFollowAndRefits`. |
| `Playback2DFollowCardRenderTests` | `SelectingCard_HighlightsExactlyOneCard_AndSetsViewportFollowSlot`: reuses `FindViewport` from `Playback2DCameraModeTests.cs:225` and asserts `viewport.FollowSlot` + `viewport.Mode == CameraMode.FollowPlayer`. |

### Real-demo test (skip-if-absent)

| Class | Cases |
|---|---|
| `Playback2DTimelineRealDemoTests` | `[Category("Integration")]`. `DemoTestHelper.RequireDemo()` → `GetOrParse` (throws `SkipTestException` when no demo is staged, the repo's golden/probe pattern). `RoundTrack_BandCount_MatchesFreezeEndCount`; `FrameIndexAtTick_MatchesLinearScan_AcrossWholeDemo` (the binary-search oracle on real, real-world tick data: this is what actually proves the monotonic-`ServerTick` assumption T1 rests on); `KillTrack_MarkerCount_EqualsPlayerDeathCount`. |

### Commands

```bash
# Whole App suite, batched (single-process is OOM-prone: see the script header)
scripts/test-app-suite.sh -c Release

# One class while iterating
dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- \
  --treenode-filter "/*/*/Playback2DKeymapTests/*"

# The A1 set
dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- --treenode-filter \
  "/*/*/(PlaybackControllerTickSeekTests|TimelineTrackTests|TimelineLayoutTests|Playback2DKeymapTests|Playback2DKeybindConflictTests|Playback2DActionDispatchTests|Playback2DFollowFunnelTests|ModuleTimelineDataTests|TimelineCoreCleanTests|Playback2DTimelineRenderTests|Playback2DTimelineScrubTests|Playback2DKeyRoutingTests|Playback2DFollowCardRenderTests|Playback2DTimelineRealDemoTests)/*"

# What CI actually runs today (build only)
dotnet build src/App/DemoViewer.NET.Desktop -c Release
```

No golden images in A1 (the golden corpus starts at B0 on the CPU surface provider). The render tests
here are non-blank/artifact probes, matching `Playback2DCameraModeTests`.

---

## 6. Build & wiring

**No new projects. No new packages. No `.slnx` change. No CI change.** A1 is entirely additions to
existing projects, which is the reason it can ship ahead of the Core port.

Files created (all inside existing projects):

```
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/ITimelineTrack.cs
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/TimelineMarker.cs
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/ITimelineData.cs
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/ModuleTimelineData.cs
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/RoundTrack.cs
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/KillTrack.cs
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/BombTrack.cs
src/App/DemoViewer.NET/Modules/Playback2D/Timeline/Playback2DTimelineViewModel.cs
src/App/DemoViewer.NET/Modules/Playback2D/Playback2DKeymap.cs
src/App/DemoViewer.NET/Views/Playback2D/TimelineControl.axaml
src/App/DemoViewer.NET/Views/Playback2D/TimelineControl.axaml.cs
src/App/DemoViewer.NET.App.Tests/*.cs                      (per §5)
```

Files modified:

```
src/App/DemoViewer.NET.Modules.Abstractions/IModuleContext.cs     (+5 default members — §4.5)
src/App/DemoViewer.NET/ViewModels/Playback/PlaybackController.cs  (FrameIndexAtTick; SeekToTick)
src/App/DemoViewer.NET/Modules/ModuleContext.cs                   (implement the 5 + SetSpeedLock)
src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs          (SetSpeedLock wiring, ~:2171)
src/App/DemoViewer.NET/Modules/Playback2D/Playback2DModule.cs     (gate ctor; ContractVersion 1.2.0)
src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs
src/App/DemoViewer.NET/Modules/Playback2D/PlayerAttributes.cs     (IsFollowed)
src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml      (timeline row; ListBox; status move)
src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml.cs   (key tunnel; follow mirror; FitRequested)
src/App/DemoViewer.NET/Features/FeatureCatalog.cs                 (+2 appended entries)
src/App/DemoViewer.NET/App.axaml.cs                               (:778 — pass the gate to the module)
src/Testing/DemoViewer.NET.TestSupport/DemoTestHelper.cs          (FindRepoRoot → public)
docs/ui/design-system.md                                          (TimelineControl + keybind table)
```

**Package version policy note (nothing to add, stated for completeness):** the repo uses Central
Package Management (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`). A
`PackageReference` in a csproj carries **no `Version=` attribute**. Avalonia sub-packages are all
pinned to the same version (`11.3.12`) and must be bumped together; CS2DemoKit's three packages are
exact-pinned to each other and bump in lockstep. A1 needs none of that.

**Style requirements that will fail the build if missed** (`TreatWarningsAsErrors=true`,
`EnforceCodeStyleInBuild=true`, `AnalysisMode=Recommended`): file-scoped namespaces, Allman braces,
braces always, explicit types (no `var`), 4-space indent, LF, `max_line_length=120`, XML docs on
public members, the `#region` using-block header. New `[ObservableProperty]` backing fields follow
the existing `private bool _isFollowed;` naming.

**CI:** `.github/workflows/ci.yml` builds `src/App/DemoViewer.NET.Desktop -c Release` only and runs
no tests (the App suite is single-process/OOM-prone and runs via `scripts/test-app-suite.sh`). A1
adds nothing to CI. Wiring the suite into CI remains the standing follow-up recorded in that file,
**out of scope here**, but note that A1's direct-execution tests (§5, first table) are cheap and
Avalonia-platform-free, so they are the natural first batch if someone does pull that forward.

---

## 7. Dependencies

### Consumed from other phases

**None.** A1 is the first phase on the branch and ships before B0. Everything it consumes already
exists in `main`:

| API | Source | Signature used |
|---|---|---|
| `IModuleContext.GetEventTimeline` | existing shell | `IReadOnlyList<GameEventView> GetEventTimeline(string eventName)` |
| `IModuleContext.RequestSeekToFrame` / `RequestSeekToTick` / `RequestPlay` / `RequestPause` | existing shell | `void RequestSeekToFrame(int)` etc. |
| `IModuleContext.RequestNextEvent` / `RequestPrevEvent` / `AvailableEventNames` | existing shell (Phase E nav) | `void RequestNextEvent(IReadOnlyCollection<string>?)` |
| `IModuleContext.NotifySpectateTarget` | existing shell → `SyncStateObserver.OnSpectateTargetChanged` (`SyncStateObserver.cs:91`) | `void NotifySpectateTarget(int slot)` |
| `SemanticNavigator.EventBoundaryFramesByName` | existing shell | `IReadOnlyDictionary<string,int[]>` |
| `Playback2DViewport.FollowSlot` / `.Mode` / `.FitToExtent()` | existing control (`Playback2DViewport.cs:164,195,394`) | unchanged |
| `IFeatureGate.IsEnabled` / `.Changed` | existing | unchanged |

### Exported to other phases

| API | Consumed by | Notes |
|---|---|---|
| `ITimelineTrack`, `TimelineMarker`, `TimelineBand`, `ITimelineData`, `TimelineEventRecord`, `TimelineEventKeys`, `TimelineMarkerKind` (§4.2) | **B1** moves them to `DemoViewer.NET.Playback2D.Core` verbatim (namespace change only, D1). **B2** implements `AnnotationTrack : ITimelineTrack` and raises `MarkersChanged` on `AnnotationDocument.Changed`; **B3** adds envelope drag-handles over the same markers. | B1's planner must match §4.2 exactly. |
| `RoundTrack` / `KillTrack` / `BombTrack` | **B1** (moved), **B4** (export range = "current round" comes from `RoundTrack`'s bands). | |
| `Playback2DTimelineViewModel` (§4.3) | **B2** (annotation markers + "pin to now"), **B3** (envelope handles), **B4** (export range selection reads `Bands`). Stays app-side. | |
| `Playback2DKeymap` / `Playback2DAction` / `Playback2DBindingScope` (§4.4) | **B2** un-reserves `ToolDraw`/`ToolErase`/`Undo`/`Redo`/`ClearAnnotations`/`CancelGesture`/`HoldPan` and registers tool-scoped bindings; **B5**'s keybind-conflict audit runs `FindConflicts`. | The reserved entries and `Scope` exist so B2 adds behaviour, not table structure. |
| `PlaybackController.FrameIndexAtTick` (§4.1) | **B4** (`TrackerFrameSource` resolving an export range given in ticks), any LiveSync tick→frame work. | |
| `IModuleContext.TotalFrames` / `FrameIndexAtTick` / `EventFrames` / `IsSpeedLocked` / `RequestSpeed` (§4.5) | **B2**/**B3**/**B4** module surfaces; `ContractVersion` is already at 1.2.0 for them. | Additive default members. Every existing implementer keeps compiling. |
| `Playback2DTabViewModel.FollowedSlot` / `ExecuteAction` / `FitRequested` (§4.6) | **B1** re-hosts the same VM against `Scene2DHost`; **B3** drives `FollowPlayerRig` + AutoFollow off `FollowedSlot`. | The follow funnel (`NotifyFollowSlotChanged`) keeps its existing name and signature so LiveSync wiring is untouched. |

---

## 8. Risks & spikes

| # | Risk | L | I | Mitigation / time-box |
|---|---|---|---|---|
| R1 | **Key routing fights focused controls.** A tunneling handler that swallows keys too eagerly breaks a future in-tab text field or an accessibility path. | M | M | D12's text-input guard; `Playback2DKeyRoutingTests` covers the three interesting focus states. **Time-box: 2 h.** If the tunnel proves too blunt, fall back to `UserControl.KeyBindings` on `Playback2DView` plus explicit `IsTabStop=False` on the overlay checkboxes and `ListBox`, a strictly smaller behaviour change. |
| R2 | **Drag-scrub feels coarse on long demos**: every push runs the debounced checkpoint replay (design §10 risk 4, called "likely, not optional"). | H | M | Ships as-is in A1 (the debounce + latest-wins already absorb bursts). **Explicitly measure it** on the reference demo during T15 and record the p50 seek latency in the PR. If it is unusable (> ~600 ms per settle), the near-playhead checkpoint cache is the immediate Track-A follow-up (a separate A2), not an A1 scope grab. |
| R3 | **Marker density**: a 90 k-frame demo has ~200 kills; on a 600 px bar that is one marker per 3 px. | M | L | The 2 px coalescing rule in T7 (folded tooltips). Verified by `TimelineLayoutTests`. |
| R4 | **`ServerTick` monotonicity**: the binary search assumes it; a demo with out-of-order frames would silently mis-seek where the old linear scan mis-seeked differently. | L | M | `Playback2DTimelineRealDemoTests.FrameIndexAtTick_MatchesLinearScan_AcrossWholeDemo` compares the two implementations frame-by-frame on a real demo. **Time-box: 1 h.** If a real demo violates it, keep the linear scan behind an `#if`-free runtime check (`isSorted` computed once at `LoadDemo`). |
| R5 | **`ListBox` conversion regresses the card panel** (Fluent chrome, selection visuals, `InMatch` hiding, dead-player opacity). | M | M | The card `Border` template is copied verbatim; only the container changes. `Playback2DFollowCardRenderTests` plus a visual check. **Time-box: 3 h** before reverting to an `ItemsControl` + per-card click handler (which also satisfies the exit criterion). |
| R6 | **B1 contract drift**: B1's planner defines `ITimelineTrack` differently and A1's tracks need rewriting. | M | M | §4.2 is declared binding and `TimelineCoreCleanTests` mechanically enforces move-ability. Hand §4.2 to B1's planner as an input. |
| R7 | **WASM**: the timeline is pure XAML chrome and the keymap is pure input, so both work in browser; the `SplitButton`/`ListBox`/`Canvas` combination is already in use elsewhere in the app. | L | L | Confirm in the B5 WASM verification pass; no A1-specific work. |

**No spikes required.** Every A1 API already exists or is a two-line addition; nothing here is
technology selection.

---

## 9. Acceptance checklist

Design exit criterion: **"Scrub + keys + follow-by-card shipped."** Mapped 1:1, plus the phase's own
additions.

**Scrub**
- [ ] A `TimelineControl` is visible at the bottom of the 2D Playback tab whenever a demo is loaded
      and `playback2d.timeline` is on; hidden otherwise, with no layout hole.
- [ ] The rounds band shows one segment per `round_freeze_end` (plus a warmup segment when the demo
      starts before the first freeze end), labelled with the 1-based round number.
- [ ] Kill markers appear for `player_death` and bomb markers for
      `bomb_planted`/`bomb_defused`/`bomb_exploded`; a demo lacking an event shows no track for it
      and no empty toggle.
- [ ] Clicking anywhere on the scrub bar seeks; dragging scrubs continuously; clicking a round band
      seeks to that round's first frame. All of it goes through `IModuleContext.RequestSeekToFrame`
      (verified by test, not by inspection).
- [ ] The playhead tracks the shared clock during play, step, NavStrip nav, command-palette jumps and
      LiveSync-driven seeks, i.e. it follows `Advanced`, never a private clock.
- [ ] Hovering a marker shows a tooltip naming the event (attacker → victim (weapon) for kills).

**Keys** (focus inside the 2D tab)
- [ ] `Space` play/pause · `←`/`→` step · `↑`/`↓` speed · `Q`/`E` prev/next round ·
      `Shift+Q`/`Shift+E` prev/next kill · `F`/`Shift+F` cycle follow · `Esc` clear follow.
- [ ] Every one of those mutations lands on `PlaybackController` via a `Request*` call: no key
      writes `CurrentFrameIndex`, `Speed`, or the viewport directly (LiveSync observability).
- [ ] `↑`/`↓` are inert while `IModuleContext.IsSpeedLocked` (a Synced session without the timescale
      capability), matching the NavStrip ComboBox's disabled state.
- [ ] `Playback2DKeymap.FindConflicts` returns empty for the shipped table, and the static ctor throws
      if it ever doesn't.
- [ ] No 2D binding collides with `Ctrl+1..9`, `Ctrl+P`, `Ctrl+O`, `Ctrl+B`, `Ctrl+W`,
      `Ctrl+OemComma`, asserted against `MainView.axaml`'s own text, not a copy.
- [ ] Keys reach the tab after clicking the map, and do not fire while another tab is selected.

**Follow-by-card**
- [ ] Clicking a player card selects it, highlights exactly that card, and follows the player in the
      2D camera (`Playback2DViewport.FollowSlot` set, `Mode == FollowPlayer`).
- [ ] The pick reaches `IModuleContext.NotifySpectateTarget(slot)` → `SyncStateObserver` →
      `SetDesiredSpectator(name)`, the existing chain, unchanged and covered by a test.
- [ ] The UI reads **"requested"**, never "following (confirmed)" or similar (spectate has no
      readback).
- [ ] The existing camera-mode SplitButton "Follow Player" submenu goes through the same funnel and
      produces identical state.
- [ ] `Esc` / `ClearFollow` clears the highlight, resets the camera to `Fit`, and does NOT push a
      spectate change.
- [ ] `playback2d.follow` off ⇒ cards are not selectable and the follow keys are inert.

**Binary-search `SeekToTick`**
- [ ] `PlaybackController.SeekToTick` produces the same frame as the removed linear scan for every
      tick in a real demo.
- [ ] `FrameIndexAtTick` returns `-1` (never a clamped index) past the last frame, and `0` before the
      first.

**Phase hygiene**
- [ ] No file under `Modules/Playback2D/Timeline/` (excluding the app-side allow-list) references
      Avalonia, `Modules.Abstractions`, `CS2DemoKit`, `DateTime`, `Stopwatch` or `Random`:
      `TimelineCoreCleanTests` green.
- [ ] `Playback2DModule.ContractVersion == 1.2.0` and every new `IModuleContext` member has a default
      implementation (existing test doubles compile untouched).
- [ ] Two new `FeatureCatalog` entries appended at the end, `GroupId = null`;
      `FeatureGateTests` group-leader assertions still green.
- [ ] `scripts/test-app-suite.sh -c Release` green, and `dotnet build src/App/DemoViewer.NET.Desktop
      -c Release` warning-free (`TreatWarningsAsErrors` means warning-free is the only green).
- [ ] No new persisted settings keys (D11): nothing to add to `SettingsService.WriteInMemory`.
- [ ] Scrub latency on the reference demo measured and recorded in the PR (R2 input for the A2
      decision).

---

## Implementation notes (deviations)

Everything in the ordered work breakdown (T1-T16) shipped. The list below is what differs from the plan
body as written, and why.

### Wiring / placement

1. **`SetFeatures` and `SetSpeedLock` are wired in `MainViewModel.BuildWorkspaceTabs`, not `App.axaml.cs`.**
   Integrator correction 2 says "next to the existing `SetLiveSyncHud` wiring". That call site is
   `MainViewModel.cs:1930`, not `App.axaml.cs`; `_moduleContext` is shell-private and the composition root
   has no `ctx` handle to call `SetFeatures` on. Both calls therefore sit immediately after the
   `new ModuleContext(...)` in `BuildWorkspaceTabs`, which is also where the plan's own T2 puts
   `SetSpeedLock`. The shell already holds the `IFeatureGate`, so `ShellModuleFeatureGate` is constructed
   there and disposed in `MainViewModel.Dispose`. Everything else in correction 2 is unchanged:
   `Playback2DModule` and `Playback2DTabViewModel` keep parameterless constructors, and no `IFeatureGate`
   is injected anywhere.

2. **Item layers are `Panel` + left `Margin`, not `Canvas` + `Canvas.Left` (T8).** Avalonia wraps templated
   items in a generated `ContentPresenter`, so `Canvas.Left` inside a `DataTemplate` positions nothing; the
   documented alternative is a container-targeting `Style`/`ControlTheme` with its own `x:DataType`.
   Positioning each band/marker by its own `Margin` inside a plain `Panel` needs no container styling at
   all, which is both smaller and less fragile under compiled bindings. `TimelineBandViewModel` /
   `TimelineMarkerViewModel` therefore expose a `Thickness Offset` alongside the specified `X`, and the
   view-model exposes `Thickness PlayheadOffset` alongside `PlayheadX`.

### Additions to §4.3 (`Playback2DTimelineViewModel`)

All additive; nothing specified was removed.

3. **`RequestSeekToFrame(int)`**: a round band must seek to its FIRST frame, which cannot round-trip
   through `RequestSeek(double)`'s pixel mapping without an off-by-one.
4. **`PositionText`**: the footer's `frame N / M · tick T` readout (T8 specifies the readout but §4.3
   listed no property for it).
5. **Hover is `HoverText` + `UpdateHover(double)` / `ClearHover()`**, not the `HoverFrameIndex` T8 names.
   The control needs a rendered string, and the frame index is already reachable through `FrameIndexAt`.
6. **`TimelineTrackToggle.IsAvailable` is settable** (`[ObservableProperty]`). §4.3 declares `{ get; }`,
   but availability is per-demo and `Rebuild` has to refresh it.
7. **`TimelineMarkerViewModel` / `TimelineBandViewModel` are plain sealed classes**, not
   `partial : ObservableObject`. Every value is fixed at construction and the collections are rebuilt on
   layout, so there is nothing for change notification to carry.
8. **Brushes are `ImmutableSolidColorBrush` behind a `Dispatcher.UIThread.CheckAccess()` guard.**
   `SolidColorBrush` derives from `AvaloniaObject`, whose constructor calls `VerifyAccess()`: building
   one off the UI thread throws once a headless application exists, which made the whole layout suite
   fail when run alongside the UI tests. `Application.ActualThemeVariant` has the same affinity, hence the
   guard before the `ThemeColors.Get` lookup, falling back to the dark-theme literal.

### Behaviour

9. **`RoundTrack` emits low-alpha team ARGB constants for the won-by tint** (mirroring `Pb2dTeamT` /
   `Pb2dTeamCt`), with `0` meaning "host default". D4 requires the track to distinguish T from CT while
   D1 forbids brushes in the folder, and `uint` ARGB is the only channel the contract offers. The constants
   are the dark-palette values; a band wash is the one place that small a theme mismatch is acceptable.
10. **`SpeedUp` / `SpeedDown` return `true` while `IsSpeedLocked`** (setting `SpeedLockNote`) rather than
    `false`. T11's false-list is "no context, no demo, gate off, reserved action"; a locked speed is a
    deliberate refusal, and returning `false` would leave the key unhandled and let `↑`/`↓` fall through
    to the player-card list, exactly what D12 exists to prevent.
11. **`FollowStatus` is mirrored onto `Timeline.FollowStatus`, and `Status` onto `Timeline.StatusText`.**
    D14 moves the status readout into the timeline footer; mirroring keeps the tab's single `Status` string
    authoritative instead of splitting it.
12. **`Playback2DView.axaml.cs`'s `FollowSlot(int, string)` became `FollowSlot(int)`.** The display name is
    now resolved inside the `FollowSlotChanged` handler, so the menu pick and the card pick produce
    identical mode-label state through one path.
13. **`ITimelineTrack.MarkersChanged` carries `#pragma warning disable CS0067`** in the three A1 tracks.
    The event is declared-but-never-raised by design (round/kill/bomb data is fixed after parse) and
    `TreatWarningsAsErrors` would otherwise reject it. B2's `AnnotationTrack` raises it for real.

### Tests

14. **`Playback2DKeymapTests.TryResolve_ToolActive_PrefersToolScopedBinding` asserts the shadow, not a
    hit.** Every tool-scoped binding is reserved in A1, so the observable proof of the scope mechanism is
    that `Space` resolves to `TogglePlay` with `toolActive: false` and resolves to nothing with
    `toolActive: true`. That is the behaviour B2 depends on.
15. **`ArrowKeys_DoNotChangeListBoxSelection` sets `cards.Focusable = true` first.** The shipped list is
    not focusable (its containers are `Focusable=False`), so without this the test would pass vacuously.
    The assertion is made to hold for the worst case rather than for the case the template prevents.
16. **`Timeline_HiddenWhenFeatureGateOff` asserts the viewport reclaims the row's height** rather than
    `timeline.Bounds.Height == 0`: Avalonia leaves the last measured bounds on a collapsed control, and
    "no layout hole" is the property that actually matters.
17. **Two tests were added beyond §5's list:** `Playback2DKeybindConflictTests
    .ShellReservedGestures_MatchesMainViewAxaml` (the keymap's own copy of the shell list is what its
    static ctor checks against, so the two drifting apart would quietly weaken the guarantee), and
    `Playback2DKeymapTests.FindConflicts_DetectsADuplicateGestureAndAShellCollision` (without it the
    clean-table assertion is vacuous).
18. **T16's optional doc-row-count test was skipped**, per the plan's own "skip if it feels precious".

### Not done

19. **`Playback2DTimelineRealDemoTests` skipped in this tree**: `demos/` holds only a `.dem.info`
    sidecar, no `.dem`, so `DemoTestHelper.RequireDemo()` raises `SkipTestException`. The three cases
    (including the `FrameIndexAtTick` vs linear-scan oracle that closes R4) are written and compile; they
    need a staged demo to run.
20. **R2's scrub-latency measurement was not taken**, for the same reason: it needs the reference demo.
    The A2 decision it feeds therefore stays open.
21. **Six App-suite tests fail on this machine and were failing before this branch's changes**
    (`DiagnosticsFileLogTests` ×3, `DemoLibraryServiceTests.Scan_DeduplicatesSameFile_AcrossSymlinkedFolders`:
    requires the symlink privilege, `SettingsBacked_AddRemoveFolder_WritesThroughToSettingsJson`,
    `DemoProcessingQueueTests.QueuePath_PersistsCache_SoSecondLaunchDoesNotReparse`). Verified against a
    stashed tree; untouched.

---

## Review findings (A1 sign-off)

Three defects found in review and fixed on this branch. All three are the same shape: state the VM holds
correctly never reaches the surface that was supposed to show it.

**R-1: a retained follow did not survive tab deactivation (functional).** `WorkspaceTabDescriptor`
DESTROYS the View on deactivation and rebuilds it from `ViewFactory` on the next activation, keeping the
cached VM. `Playback2DView.BindViewModel` re-aimed its subscriptions but never re-projected the VM's
current `FollowedSlot`, so after a tab switch the followed card stayed highlighted and the footer still
said *"following X · requested"* over a fresh viewport sitting in `Fit`. The follow looked live and was
dead. `BindViewModel` now replays `OnFollowSlotChanged(FollowedSlot)` on bind. Regression test:
`Playback2DFollowCardRenderTests.RebuiltView_ReprojectsTheRetainedFollowOntoTheFreshViewport` (fails
without the fix: `vm=2 viewport=-1`).

**R-2: a re-templating `ListBox` silently cleared the follow (functional).** The two-way
`SelectedItem` binding writes a transient `null` while the list re-templates, which happens on every
view rebuild. `OnSelectedPlayerChanged(null)` took that for a user deselect and ran `ClearFollow()`,
dropping `FollowedSlot`, the row flags and the camera in one go. A null now clears only once the followed
row has actually left `Attributes`; otherwise the funnel re-asserts the selection. Regression tests:
`Playback2DFollowFunnelTests.StraySelectionNull_KeepsARetainedFollow` and
`.SelectionNull_AfterTheRowLeavesTheRoster_ClearsTheFollow`.

**R-3: `HoverText` and `SpeedLockNote` were computed but never displayed (missing T8 / T11
affordance).** `Playback2DTimelineViewModel.UpdateHover`/`ClearHover` and the tab's `SpeedLockNote` were
both set, tested and bound to nothing. The speed one matters: a refused `↑`/`↓` is deliberately CONSUMED
(deviation 5) so it cannot fall through to the card list, which leaves a dead key with no reason unless
the footer says one. Both now have a footer column; `SpeedLockNote` is mirrored onto the timeline VM the
same way `Status` and `FollowStatus` already were. Tests:
`Playback2DTimelineScrubTests.HoverOverScrubBar_ShowsTheTargetFrameInTheFooter` and the extended
`Playback2DActionDispatchTests.SpeedUp_WhenLocked_DoesNotRequestSpeed`.

**Verified, not taken on trust:** `dotnet build DemoViewer.NET.slnx`, 0 errors, 1 pre-existing WASM
workload warning. A1 set: 87 total / 84 passed / 3 skipped (the real-demo class; no `.dem` staged).
Full App suite: 794 total / 692 passed / 96 skipped / 6 failed, the same 6 environmental failures
recorded in deviation 21, none in a subsystem A1 touches.

**Audited clean:** every keymap action routes through `IModuleContext.Request*` or an existing
`PlaybackController` command (nothing bypasses `SyncStateObserver`); `ShellReservedGestures` matches
`MainView.axaml`'s 14 accelerators exactly and no 2D binding collides with them; the timeline's axis is
frame index throughout and every seek exits via `RequestSeekToFrame`; rounds key on `round_freeze_end` in
both `RoundTrack` and the `Q`/`E` event filter, off one shared constant; the two catalog ids match the
strings `IsTimelineEnabled`/`IsFollowEnabled` read; `OnAdvanced`'s added work is a binary search plus
property sets on the existing per-push string path: no new per-frame allocation class.
