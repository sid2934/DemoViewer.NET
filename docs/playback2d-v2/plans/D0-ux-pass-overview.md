# D: the UX pass (registry + shared contracts)

**Design authority:** this document, subordinate to [`../design.md`](../design.md) · **Registry:**
[`00-overview.md`](00-overview.md) §1 · **Branch:** `feature/playback2d-v2` · **Status:** in flight.

The A/B/C/P tracks built the 2D playback module and made it fast. Track **D** is the first pass driven
by *using* it. Five item groups came in as a user report; reconnaissance turned three of them into
defect findings and two into design work. This document is the registry the five D plans point at, in
the same shape `00-overview.md` uses for A/B/C/P.

---

## 1. Master index

| Plan | Owns | Exit criterion |
|---|---|---|
| **D1**: configurable keybindings | `Playback2DKeymapProfile`, `Playback2DSettings.KeybindOverrides`, the Settings "2D Playback controls" section, gesture text in tooltips | A user rebinds an action, it survives a restart, and a bad override degrades to the default instead of throwing |
| **D2**: the drawing tools | Per-button tool + ink binding, middle/Ctrl pan under any tool, the `Custom` envelope editor, recent-colour swatches, the opacity control | Right-drag draws the secondary ink, middle-drag pans while the pen is active, and `Custom` is no longer a synonym for `Always` |
| **D3a**: export, made to work | `CanExport` notification, the `playback2d.annotations` include-id crash, annotations actually burned in, the export entry point | An export started from the 2D tab with shipped defaults produces a file, with the ink in it |
| **D3b**: the export HUD | `HudPlayerRow` + `HudSnapshot.Roster`, `hud.roster` (player cards both sides), a re-composed `hud.clock`, a team-coloured `hud.killfeed` | A 720p export reads as a broadcast clip: rosters, score, clock, kill feed |
| **D4**: viewport chrome | The docked viewport toolbar, the overlay overflow, the collapse affordance, `design-system.md`'s HUD-corner contract | The canvas is not permanently covered by floating chrome, and every overlay is still reachable at 820 px |
| **D5**: timeline fit-and-finish | Kill markers coloured by the crediting side, the footer's clipped toggles | Kill colour reads at a glance; every track toggle is fully visible and clickable at 820 px |

**Dependency order.** `D3a → D3b` (D3a fixes the catalog D3b extends). `D2 → D4` and `D1 → D4`
(D4 mounts what they expose). `D5` and `D3a` are independent of everything.

```
wave 1:  D3a      D5      D2
            \             / \
wave 2:      D3b        D1   |
                 \       |   /
wave 3:            \     |  /
                      D4 ◄┘
```

---

## 2. What reconnaissance found (the part that changed the plan)

Three of the five reported items are **defects**, not enhancements. They are recorded here because
each was reported as a UI complaint and would otherwise be fixed as one.

### 2.1 The 2D tab's video export is broken by default: **verified, not inferred**

`Playback2DExportDialogViewModel.BuildLayerIds()` adds the literal `"playback2d.annotations"` whenever
*Include annotations* is checked. That box is checked by default
(`Playback2DSettings.ExportIncludeAnnotations = true`). The id is **not** in
`SceneLayerCatalog.SceneStackIds`, and `CreateSceneStack` throws on an unknown include id:

```
ArgumentException: unknown layer id(s): playback2d.annotations.
Known: playback2d.radar, …, hud.clock, hud.killfeed (Parameter 'include')
```

Reproduced directly against `CreateSceneStack` with the exact id set the dialog produces under shipped
defaults. The comment at `Playback2DExportDialogViewModel.cs:347-350` asserts the opposite
(*"CreateSceneStack ignores ids it does not know how to build"*), and `SceneLayerCatalog.cs:183-190`
is the code that disproves it. Both go.

### 2.2 …and annotations could never have been exported anyway

`AnnotationLayer` is registered by `Scene2DHost` (the window) and by nothing else. There is no
reference to it in `Services/Export/` or `Pipeline/Export/`, it is absent from `SceneStackIds`, and
`SceneLayerCatalog.BuildLayer` has no case for it. So *Include annotations* is a checkbox that, once
it stops throwing, still does nothing, while design §1 goal 2 promises *"render 2D playback (with
annotations) to gif/webm/mp4"*. D3a closes the gap rather than deleting the checkbox.

### 2.3 `CanExport` never raises a change notification

`Playback2DTabViewModel.CanExport` (`:297`) is a computed property over `_context.HasDemo`. The
identifier appears exactly twice in the 2 015-line file: its definition and one guard. No
`OnPropertyChanged(nameof(CanExport))` exists anywhere. `ExportButton`'s `IsVisible` binding therefore
latches whatever it read first, which is why the export entry point "isn't there": item 3.2 as
reported.

### 2.4 `Custom` visibility is a synonym for `Always`

`AnnotationSession.NewElementEnvelope` is **never assigned in production code**. Declaration, one
read, and two doc mentions are its only occurrences. `EnvelopeForNewElement` returns it for
`EnvelopeMode.Custom`; it is `TimeEnvelope.Static`, which is `default`, which is constant opacity 1.
Selecting *Custom* changes one persisted string and nothing else. Item 2.4, exactly as reported.

Adjacent, same cause: `InkOpacity`, `FadeInTicks`, `FadeOutTicks`, `HoldTicks` and
`AnnotationRecentColors` all have view-model properties **and** persisted settings keys **and** no UI
control anywhere. The plumbing was built; the controls were not.

### 2.5 `ToolPointerEvent.Button` is captured and never read

`Scene2DHost.ButtonOf` (`:623-646`) correctly resolves Left/Right/Middle onto every pointer sample.
Nothing downstream inspects it: not the router, not `DrawTool`, not `EraseTool`. A right-drag with
the pen draws identical ink to a left-drag, and every test harness hard-codes `Left`. Items 2.2 and
2.3 are therefore additive: the signal already reaches the router's door.

---

## 3. Shared API registry (D-track additions)

**One signature, one owner.** Anything below is referenced by two or more D plans.

### 3.1 Layer ids (extends `00-overview.md` §3.3)

| Id | Owner | Slot / Order | Notes |
|---|---|---|---|
| `hud.roster` | D3b | `Hud` / 65 | Player cards down both pane edges. Opt-in by name, like the other two HUD layers. |

`playback2d.annotations` is **not new**. It already exists as `SceneLayerIds.Annotations`; D3a adds it
to `SceneStackIds` and gives `BuildLayer` a case for it.

**Three hard-coded pair-lists must learn every new HUD id together.** They are the trap D3a and D3b
both walk into:

1. `SceneLayerCatalog.CreateSceneStack`: `bool isHud = id is HudClock or HudKillFeed;` (`:212`).
   A new HUD id missing here is treated as a scene layer and turned **on by default for every export
   and every `dv2d export`**.
2. `SceneExportSession.OptInLayerIds` (`:114-115`).
3. `ExportRequest.LayerIds`' doc comment, which names the opt-in layers prose-style.

D3a replaces all three with one source: `SceneLayerIds.OptIn` (an `IReadOnlySet<string>`), so the
next HUD layer cannot be added to two of the three.

### 3.2 HUD roster (D3b)

```csharp
// …Core.Hud — a Core type, because IHudDataSource returns it and Core cannot see Pipeline.
public readonly record struct HudPlayerRow(
    int Slot, int Team, string Name, bool IsAlive,
    int Health, int Armor, bool HasHelmet, bool HasDefuser,
    string Weapon, int Money, int Kills, int Deaths, int Assists);
```

`HudSnapshot` gains `IReadOnlyList<HudPlayerRow> Roster`, borrowed on the same terms as `KillRows`
(valid until the next `At()` on that source) and `[]` on `HudSnapshot.Empty`.

**Where the values come from.** `PlayerMarker` carries name/team/alive and nothing else; health,
armour, weapon and money exist only app-side in `PlayerAttributes`, read off
`m_iHealth`, `m_ArmorValue`, `m_pItemServices.*`, `m_pInGameMoneyServices.m_iAccount`,
`m_pActionTrackingServices.*` and `m_pWeaponServices.m_hActiveWeapon`. D3b lifts those reads into
Pipeline beside `SceneFrameBuilder.BuildMarkers` (which already reads `m_iHealth` for `RingState` and
discards it) and surfaces them as `TrackerFrameSource.LastRoster`.

**`Scene2DFrame` does not change.** Adding a member to B0's frame record is what
`IHudDataSource`'s own doc calls *"a guaranteed merge conflict for no gain"*, and `BudgetTests`
`FullScene_SteadyState_AllocatesNothing` polices the per-frame path. The roster follows the
established precedent instead: `ClockReading.From(src.LastGameInfo)` already ignores its `tick`
argument and reads the source's last-built frame, and the roster reader does the same.

### 3.3 Keymap profile (D1)

```csharp
// The shipped table stays static, stays conflict-checked, and STILL THROWS — it is a compile-time
// contract. User overrides never route through it.
public sealed class Playback2DKeymapProfile
{
    public static Playback2DKeymapProfile Default { get; }
    public static Playback2DKeymapProfile FromOverrides(IEnumerable<string> overrides,
        out IReadOnlyList<string> rejected);       // never throws; a bad row is dropped and reported
    public bool TryResolve(Key key, KeyModifiers mods, bool toolActive, out Playback2DAction action);
    public IReadOnlyList<Playback2DBinding> Bindings { get; }
    public string GestureText(Playback2DAction action);
}
```

**Why a second type rather than making the static table mutable.** `Playback2DKeymap`'s static
constructor throws `InvalidOperationException` on a conflicting table. That is correct for a table
shipped in the binary and fatal for one assembled from a hand-editable JSON file: a typo would become
a `TypeInitializationException` that takes the tab down with no way to fix it from inside the app.
The profile validates, drops, and reports.

Persisted as `Playback2DSettings.KeybindOverrides`, a `string[]` of `"Action=Gesture"` rows
(`"NextRound=Shift+R"`), flattened as indexed keys exactly like `AnnotationRecentColors`, which is the
existing array precedent in `SettingsService.WriteInMemory` and the one shape
`SettingsWasmRoundTripTests`' array carve-out already handles.

### 3.4 Per-button tools and ink (D2)

```csharp
// …Core.Input — the router owns the button→tool map; the session owns the button→style map.
public sealed class InputToolRouter
{
    public ToolKind? SecondaryTool { get; set; }   // right button; null = same as Active
    public bool PanOnMiddleButton { get; set; } = true;
    public bool PanOnControlDrag  { get; set; } = true;
}

// …Core.Annotations
public sealed class AnnotationSession
{
    public AnnotationStyle SecondaryStyle { get; set; }
    public AnnotationStyle StyleFor(ToolPointerButton button);   // Right → Secondary, else Style
    public ToolKind? SecondaryTool { get; set; }                 // mirrors the router's; see below
}
```

**Why `SecondaryTool` lives on the session as well as the router.** The session is the *only* seam
the annotation panel and the router share: the panel edits session state, and `Scene2DHost` refreshes
`_router.SecondaryTool` from it at press time, the one moment the router reads it, so it cannot go
stale on a paused tab. The alternative was a new panel→view→host call path through
`Playback2DView.axaml.cs`, which buys nothing and adds a third owner of the value. The router remains
the authority *during* a gesture; the session is where the preference is authored and persisted
(`Playback2DSettings.AnnotationSecondaryTool`).

The diversion decision stays exactly where it already is: one expression in
`InputToolRouter.OnPressed` (`:111-112`), sampled at press time only. `SpaceHeld_DoesNotHijackOpenGesture`
is the invariant that must survive: a gesture already in flight is never re-routed.

### 3.5 Timeline event team (D5)

`TimelineEventKeys` gains `Team` (`"team"`), populated by `ModuleTimelineData.Flatten` for
`player_death` from the **attacker's** side, with `"2"` = T and `"3"` = CT, the encoding
`TimelineEventKeys.Winner` already uses. `KillTrack` then hands back a non-zero `Argb` tint
(`TintTeamT` / `TintTeamCt`, mirroring `RoundTrack.ApplyWinnerTints`) instead of today's `0`.

**A track may not reach for a brush.** `TimelineMarker.Argb == 0` means "host, use the kind default",
and `BrushForMarker` already honours a non-zero value. The team colour therefore travels as ARGB
through the existing contract and is themed by `ThemeColors.Get` on the host side, per D21.
Unknown team ⇒ `0` ⇒ today's `Pb2dHeadshot` red. **No kill loses a marker over a missing team.**

---

## 4. Standing constraints every D plan inherits

- **Persisted keys are forever.** Layer ids, feature-gate ids, `EnvelopeMode` member names and every
  `Playback2DSettings` property name appear in users' files. Adding is free; renaming is not.
- **Every new `Playback2DSettings` property needs a `SettingsService.WriteInMemory` row**, or it
  silently forgets itself on WASM. `SettingsWasmRoundTripTests` is reflection-driven over the class
  and will fail the moment a property is added without one.
- **`Pb2d*` tokens only inside the viewport column** (D21). Code-drawn surfaces resolve them through
  `ThemeColors.Get(key, variant, fallback)`; view-models hand back `ImmutableSolidColorBrush`, never
  `SolidColorBrush` (its constructor asserts UI-thread affinity and would make layout math
  untestable).
- **Overlays reflow, never clip** (D35). The viewport column is ~500 px at an 820 px window; anything
  wider runs under the splitter and the roster panel, which are later siblings and therefore take
  both the paint and the clicks.
- **Tiers are defined by exclusion.** An untagged test is in every tier, so a new unit test needs no
  attribute. Only tag a test that is genuinely expensive, and only from `TestTiers`' vocabulary.
  `TestTierContractTests` fails on any category string outside it.
- **Geometry is the assertion.** `Playback2DHudLayoutTests` measures rectangles and hit-tests rather
  than container shapes. D4 and D5 extend that suite in the same style.

---

## 5. As-built notes

### 5.1 D4: viewport chrome (shipped)

**The corner allocation is gone.** `Playback2DView`'s left column is `RowDefinitions="Auto,*,Auto"`: a
docked `ViewportToolbar` at the top edge, the canvas, and `TimelineControl` at the bottom. The tool row and
the overlay toggles left the canvas cell entirely, so the map is no longer under a widget. The canvas cell
keeps only `TransportBar`, `LevelStrip`, the display-only `HudStack`, and, *only while the toolbar is
collapsed*, a 26 px restore chevron. The full contract, with the layout sketch, is
[`design-system.md` → the viewport chrome](../../ui/design-system.md#playback2d-viewport-chrome).

- **Docked, not reflowed.** §4's "overlays reflow, never clip" answers *too wide*; the report was *in the
  way*. Precedent is Blender's viewport header (edge-docked region + collapse arrow + popovers for display
  toggles). A Krita/GIMP left tool rail was rejected (a vertical rail spends the width the 820 px floor has
  least of), and so was a Figma-style floating island, which is the shape being complained about.
- **Two new persisted bools**, both with `WriteInMemory` rows: `ViewportToolbarOpen` (`true`) and
  `ViewportOverlayBarOpen` (**`false`**; "always displayed" was the defect). Collapsing removes the whole
  `Auto` row (172 px → 0, measured at 1000 px with the overlay bar open) and the restore chevron is mounted
  *by the collapsed state*, so a persisted "collapsed" can never be a state with no exit.
- **Stack order is the gate contract**: always-present header → optional overlay row → gated annotation
  toolbar, so a gate flip can only move what is below it. The header's right cluster right-docks and is
  declared first (D5's footer reservation).
- **D1's five hard-coded tooltips are bound.** `AnnotationsPanelViewModel` exposes `DrawToolTip` /
  `EraseToolTip` / `UndoToolTip` / `RedoToolTip` / `ClearAllToolTip` off the resolved
  `Playback2DKeymapProfile`, pushed from `ApplyKeymapOverrides`. The draw hint's `Space` and `Esc` were
  literals too and are bound with them; the sweep found no others in the 2D views.
- **Found and fixed on the way (pre-existing, since B2):** `FluentTheme` does not carry the `ColorPicker`
  control theme, so **both annotation ink pickers were templateless**: 46×24 of nothing that painted no
  swatch and took no click. D4's new hit-test sweep over the docked toolbar is what caught it;
  `App.axaml` now includes `avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml` beside the
  AvaloniaEdit include, which is there for the identical reason.
