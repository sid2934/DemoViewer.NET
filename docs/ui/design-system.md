# DemoViewer.NET: UI Design System

The living style guide for DemoViewer.NET's Avalonia desktop/browser app: the single canonical UI
reference. Keep it current as the UI evolves.

This doc
**supersedes** the dangling "UI v2 review §X" citations scattered in the codebase. Those cite a
`docs/ui-v2-review.md` that **never existed on disk**. The still-live citations are:

| Citation on disk | File | What it meant | Resolved here |
|---|---|---|---|
| `UI v2 review §6 Phase 0, Sample #3` | `Styles/DarkPalette.axaml:7` | palette naming policy | [Token catalog → Naming policy](#naming-policy) |
| `UI v2 review §5.10` | `Controls/InspectorCard.axaml:68`, `Views/EntityTracking/EntityTrackingTabView.axaml:128` | do **not** default `IsExpanded=True` on the entity field tree (perf) | [Component contracts → InspectorCard](#inspectorcard) |

When you touch one of those files, replace the dangling citation with a link to this doc.

**Theming model (L0b structure + L1 real Light palette, 2026-07-16).** The palette is **theme-variant
aware**. `Styles/DarkPalette.axaml` holds the 98 colour tokens inside `ResourceDictionary.ThemeDictionaries`
keyed by `ThemeVariant`: a `Dark` dictionary (the canonical dev-tool values) and, **as of L1, a real
designed `Light` dictionary** (54 tokens re-authored for light; 44 held identical to Dark on purpose, see
[Light palette design (L1)](#light-palette-design-l1)). Every one of the 98 palette keys is referenced from
markup via **`{DynamicResource TokenName}`** (migrated from `{StaticResource}`), so a change to a window's /
the app's `RequestedThemeVariant` **re-resolves each token live**. `{StaticResource}` resolves once at load
and never reacts. Non-palette resources (the `BarRowTemplate`/`KvRowTemplate`/`StatsRowTemplate` DataTemplates,
converters) deliberately stay `{StaticResource}`. **Update (2026-07-16): the live theme selector
shipped.** The central theme system (a Settings theme picker + `ThemeRegistry`, built-in Dark / Light /
High Contrast / E-Girl, plus user JSON drop-ins) is on main; the token-authoring reference is
[`theme-token-catalog.md`](./theme-token-catalog.md). The L0b/L1 phase notes below are the historical
build order.
- **Dark-pixel-identical gate (PASSED).** All 9 headless capture variants (`primitives`, `chrome`,
  `tables`, `swatches`, `navstrip-real`, `library-populated`, `settings`, `workbench`, `framelist`)
  render **byte-identical (`cmp -s`)** at `--theme Dark` before → after the migration. The restructure +
  `DynamicResource` conversion changed **nothing** about how Dark looks. 51 App.Tests render at the app's
  `Default` variant and stay green (the palette resolves through `ThemeDictionaries` at `Default`).
- **Light is a no-op for the palette, NOT for Fluent's base controls (important for L0c/L1).** Because the
  Light dict equals Dark this phase, palette-painted surfaces are identical across variants. But the
  **`FluentTheme` is independently variant-aware**: its own un-classed base controls (a raw `Button`,
  `TabItem` foreground, `ComboBox`/`TextBox`/`CheckBox`/`ScrollBar` chrome) flip to Fluent's **light**
  rendering when the variant is Light, so `--theme Light` is **not** a full visual no-op (8/9 variants
  differ from Dark; only `framelist`, which has no un-classed Fluent controls, is identical). Observed:
  in this headless env `ThemeVariant.Default` resolves to **Light**, so the app's un-classed Fluent chrome
  already follows the variant today (a pre-existing condition, unchanged by L0b). **Consequence for L0c:**
  the plan's "Light stub = pure visual no-op" holds for the palette but not for Fluent chrome. Selecting
  Light today yields a half-light look until L1 authors the real light palette (and decides whether
  un-classed Fluent controls should be dark-pinned or embraced as light).
- **What the gate proves.** The load-bearing completeness proof is the **grep** (zero `{StaticResource
  paletteKey}` remain; all 570 palette refs are Dynamic; the 3 non-palette DataTemplate keys stay Static).
  The **Dark `cmp -s`** proves no value regression. `Light == Dark` was **not** used as proof of the
  dynamic path (L0a already proved that with a garish stub); the FluentTheme divergence above is why an
  identical-stub `Light` still differs from `Dark`.

_Superseded finding (P3.3): "the app is dark-only in practice … theme-awareness is aspirational, not
live." As of L0b the token layer is live-capable; **L0c** wires `AppSettings.Theme` →
`Application.RequestedThemeVariant`, and **L1 (below) ships the real Light palette**._

<a id="light-palette-design-l1"></a>
### Light palette design (L1, 2026-07-16)
The `ThemeDictionaries[Light]` block is a **genuinely-designed** comfortable light variant, **NOT** a naive
inversion. Only the `[Light]` dict was edited (`[Dark]` is untouched and renders **byte-identical**, 9/9
`cmp -s`). **54 tokens re-authored, 44 held identical to Dark.** Design rules:
- **Surfaces: a soft cool-neutral ramp (B only ~10 above R/G; near-neutral so large backgrounds don't read
  as a cheap tint).** The depth axis is preserved-by-inverting: `ShellBg #E7E8F2` is the recessed backdrop
  (a soft gray, **not** harsh `#FFFFFF` → less glare), `PanelBg #EFF0F7` sits forward/lighter, and
  `CardBg #FCFCFE` is the brightest/most-forward (cards pop). Section-header bands (`PanelHeaderBg #E9EAF3`,
  `HexBannerBg`) read as slightly **deeper** bands on light (the conventional light-theme pattern, inverting
  Dark where the header is lighter than the panel). **Hover/selected states DARKEN their base on light** (the
  opposite of Dark, where they lighten): `PanelHeaderHover`/`…HoverDeep`/`HdrActionHoverBg` step down in
  lightness; `FrameRowSelectedBg #D4D6EF` is a clear periwinkle selection wash, `FrameRowHoverBg #E6E7F3` a
  subtle one. (These hover/selected tokens are **not** in the headless capture set, set by reasoned value.)
- **Text: the Dark cool blue-purple ramp's VALUE axis flipped.** Dim/whisper labels → quiet light-slate;
  bright/primary values + card headers → deep indigo. Verified with a WCAG contrast script: body `TextMid
  #50507C` ~7:1 on a card; the **actionable** dim tokens (`TextDim` on `ctx-action`/`group-label`) held at
  ~4.7–4.9:1; whisper labels (`TextLabel`/`TextLabelAlt`/`TextHexGutter`) sit ~3–4:1 **by role** (as quiet on
  light as they are on dark). Darkest = `TextCardHeader #28284E` (~13:1).
- **Accents: identity kept, darkened for white legibility** (bright amber/green on white washes out):
  `AccentAmber #FFC107→#A86200` (4.6:1; still gold-attention, and reads over the dark Library thumbnail scrim
  + as small overridden-dot fills), `AccentError #E53935→#C62828` (5.5:1), `AccentErrorSoft #E57373→#C85454`
  (4.2:1), `AccentCaution #E0A030→#9A6A0F` (4.6:1), `StatPositive #4CAF50→#2E7D32` (5.0:1),
  `AccentInteractive #5050A0→#4A4A9E` (7.4:1). `PrimaryButtonBg/Hover/Border` become a soft periwinkle CTA
  face (`.primary` text is the shared dark `TextBright`, so the button is deliberately understated, matching
  its Dark character). **`BorderTranslucent` FLIPS** from white@20% (invisible on light) to a dark cool
  hairline `#2A141430` so the 7 RuleWorkbench pane outlines still show.

<a id="theme-independent-tokens"></a>
**Tokens held IDENTICAL to Dark (44). Three reasons:**
1. **THEME-INDEPENDENT: painted over a theme-independent surface (13 + the scrim gradient).** The
   **LibraryCard\*** overlay palette (`LibraryCardScrim` gradient + `…TextBright/Mid/Dim/Faint`,
   `…BadgeBg`, `…BusyTrack`, `…ScoreCt/T`) paints white text / a darkening scrim / dark badges **over a
   baked-dark radar THUMBNAIL** that never changes with the app theme. Flipping them would make them
   invisible on the image. The three **hex legend swatches** (`HexSwatchSelected/Parent/Ancestor`) are
   saturated semi-transparent highlight overlays that stay distinguishable on light. `TextOnAccent #12121E`
   sits on the code-held message-type `AccentBrush` (saturated) + amber badges: near-black reads on both.
   `AccentHighlight #9C27B0` already reads on light (~6:1) and still matches the code-held depth-4.
   `DeltaRowBg` (unused in live markup) is an amber tint that reads as a warm wash on both.
   > Verified in-render: `library-populated` at Light shows the white-on-thumbnail overlays + CT/T score
   > badges **fully legible** over the dark radar images, the theme-independent call is correct.
   > **Flagged (unsure):** the hex swatches over now-**dark** hex-cell text aren't in the L1 capture set
   > (`BinaryPane` isn't a variant); verify that pairing in-app at L2/L3.
2. **L1-DEFERRED: the 29 `Pb2d*` 2D-HUD tokens.** *Not* theme-independent (a `Pb2d*`-lit tab could go
   light) but deliberately kept dark: the 2D Playback tab wraps a still-dark **Skia** viewport
   (`Playback2DViewport.cs`, code-held). Lightening only the `Pb2d*` markup chrome now would make the tab a
   light HUD around a dark map canvas (an incoherent half-tab). Kept a **coherent dark island** and re-themed
   **together with the Skia viewport in L2**.
3. **Code-held, not palette (out of L1 by definition).** The message-type accent classifiers
   (`HarvestCardViewModel.HeaderBg`/`SelectedHeaderBg`/`AccentBrush`), the RuleWorkbench syntax highlighting
   (`WorkbenchYamlHighlighting`), the 2D Skia colors, and the **depth ramp + 0xC0-alpha depth brushes**
   (`MainViewModel.DepthBrushes` / `DepthHighlightConverter`, code-owned, **not** palette keys) stay dark in
   L1. That is L2. **Known L1 rough edge:** the InspectorCard message-card **header** uses the code-held-dark
   `HeaderBg`, so under Light its title (`TextCardHeader`, now dark) is **dark-on-dark / low-contrast** until
   L2 makes `HeaderBg` theme-aware. `TextCardHeader` **must** stay dark (it also paints the light
   `PlayerDetailsView` hero-tile values), so this is unfixable in the palette. It *is* the L2 "VM accent
   classifiers (HarvestCard)" task. The `tables` Dark-vs-Light comparison render shows this exactly.

**Rendered for sign-off:** all 9 variants at `--theme Light` (read + iterated) + 6 labeled Dark-vs-Light
comparison PNGs (`primitives`, `chrome`, `tables`, `settings`, `library-populated`, `workbench`) under
`%TEMP%/demoviewer-uitests/`-style paths. **Gate:** full `slnx -c Release` clean (analyzers-as-errors; WASM
head compiles); render App.Tests **94/94** green at Default→Light (all keys resolve, no magenta). NOTE: those
render assertions are floor-based (`nonBg > N`) so they pass under either variant but no longer validate the
canonical Dark look. L3 should pin the render harness to `ThemeVariant.Dark`.

**Capture-infra note (L1):** `UiCapture/CaptureHost` now sets `Application.Current.RequestedThemeVariant`
(not just the window) before building a variant, so code-built mock variants whose helpers resolve via
`Application.Current.ActualThemeVariant` (`Tok()`/`WrapInShell` in `Variants.cs`) honor `--theme` too.
Without it, `--theme Dark` on those 4 mocks (`primitives`/`chrome`/`tables`/`swatches`) pulled the
Default→Light dict and diverged from the Dark baseline (a harness artifact, not a Dark regression).

Foundation: **dark-only** `FluentTheme`; ~71 semantic brush tokens in `Styles/DarkPalette.axaml`; the
shared design-system style files **`Styles/Primitives.axaml` · `Cards.axaml` · `Tables.axaml` ·
`Chrome.axaml`** (P1.3, replaced the old 2-rule `Styles/Components.axaml`, now removed); strong shared
controls (`InspectorCard`, `KeyValueTable`, `NavStrip`, `BinaryPane`). WASM/browser head is a **hard
constraint**: no filesystem/native-dialog/thread/native-menu assumptions without an overlay/in-window
fallback.

### Styles/ file layout (P1.3 foundation)
Loaded by `App.axaml` **after** `FluentTheme` (so these are additive layers, never template replacements):
| File | Holds | Selector kinds |
|---|---|---|
| `Styles/DarkPalette.axaml` | Color tokens, keyed by `ThemeVariant` in `ResourceDictionary.ThemeDictionaries` (`Dark` canonical + a real designed `Light`, L1, see [Light palette design](#light-palette-design-l1)), merged into `App.Resources`. | `SolidColorBrush` keys (+ one `LinearGradientBrush`, `LibraryCardScrim`, P3.3) in each variant dict |
| `Styles/Primitives.axaml` | Interactive-control classes: Button/ToggleButton/TabItem/TextBox/ComboBox. | `.primary` `.ghost` `.chip` `.nav-btn` `.bp-btn` `.icon-btn` `.ctx-action` `.shell-tab` `.mono` `.field` |
| `Styles/Cards.axaml` | Reusable card/flyout **surfaces**. | `Border.card` `Border.card-flyout` |
| `Styles/Tables.axaml` | List/tabular primitives. | `ListBox.data-list` `ListBox.card-grid` `TextBlock.col-label` |
| `Styles/Chrome.axaml` | Shell/section furniture. | `Border.sectionHeader` `TextBlock.sectionLabel` `TextBlock.group-label` `Border.badge` `Rectangle.divider` |
| `Styles/Stats.axaml` | The stats component library's theme layer (v0.8.1): default property values for the four drawn controls, `ControlTheme`s for the two templated ones, and the table-furniture classes. | `stats\|StatValue` (+`.untracked` `.chip`) `stats\|SegmentedBar` (+`.compact`) `stats\|Sparkline` `stats\|RingGauge` `stats\|DivergingBar` `stats\|PipStrip` (+`.good` `.rare`) · `Button.stat-col-header` `TextBlock.stat-sort-caret` `Border.stat-group-header` `TextBlock.stat-group-label` |

**Design decision (P1.3):** these are **additive style classes**, NOT template-replacing
`ControlTheme`s. A full `<ControlTheme TargetType="Button">` would swap the Fluent template globally and
risk regressing every un-classed control; instead each class overrides only the setters that give a
role. **Dark Fluent stays the conservative base look.** This also matches the repo's existing pattern
(`.nav-btn`, `.primary`, `.sectionHeader` were all style classes). No base `ControlTheme` is set on any
built-in type: if you add one later, re-verify the Stats/PlayerDetails/NavStrip render tests, which
currently stay green precisely because nothing leaks onto un-classed controls.

---

## 1. Token catalog (`Styles/DarkPalette.axaml`)

Single source of truth for color. **Never inline hex in a view**: bind `{DynamicResource TokenName}`
(Dynamic, not Static, so the token tracks the `ThemeVariant`; see the Theming model above). The Dark
palette is a cool blue-purple dark ramp (shell near-black `#080816` → text near-white `#C0C0F0`); the
`Light` variant (L1) is a real designed light palette: cool-neutral surfaces + a value-flipped text ramp,
44 tokens held identical to Dark (see [Light palette design](#light-palette-design-l1)).

### Naming policy
(Supersedes the "UI v2 review §6" note in `DarkPalette.axaml`.)
- **Semantic names** for role-bound colors used ≥2× or with a clear role: surfaces, borders, text
  ramps, accents, the depth ramp. Prefer these.
- **Hex-value names** (e.g. a hypothetical `ColorD0CCF8`) only for a true one-off with no semantic
  role: preserves the exact value without inventing a fake role. Avoid creating new ones; promote to
  a semantic name the moment a second consumer appears.
- **Alpha is part of identity.** `#FFC107` (`AccentAmber`) and `#FFFFC107` (`AccentAmberOpaque`) are
  distinct tokens. The depth ramp's `0xC0` alpha is deliberate.
- **Adding a token:** add it here to the right group, give it a semantic name, and record the intended
  role in this section. If it duplicates an existing color, reuse the existing token instead.

### Surfaces / shell backgrounds (darkest → lightest)
| Token | Hex | Role: when to use |
|---|---|---|
| `ShellBg` | `#080816` | App/shell background; the TabControl surface. The darkest layer. |
| `DebuggerBg` | `#0A0A18` | Debugger rail (SplitView pane) background. |
| `FrameHeaderStripBg` | `#0A0A1A` | Frame-header info strip. |
| `PanelBg` | `#0C0C1A` | Standard panel body background. |
| `PanelHeaderBg` | `#0E0E1E` | Section-header band (see `Border.sectionHeader`); NavStrip background. |
| `PanelHeaderHover` | `#0E0E24` | Hover on header actions / `nav-btn`. |
| `HexBannerBg` | `#141428` | Hex header/footer banners (`BinaryPane`). |
| `PanelHeaderHoverDeep` | `#14142E` | Deeper hover (icon buttons, selected list rows). |
| `HdrActionHoverBg` | `#16163A` | Header-action hover (heavier). |
| `CardBg` | `#171726` | Card/flyout surface (`InspectorCard`, bookmark & filter flyouts, frame readout pill). |
| `PrimaryButtonBg` | `#1A1A38` | Filled "primary" button face (Debugger panel Add/Continue). |
| `ChainSummaryBadgeBg` | `#1A1A3A` | Parse-chain summary badge. |
| `FrameRowSelectedBg` | `#1C1C38` | Parser frame-list **selected** row fill. Near-dup of `PrimaryButtonBg` (unify candidate); kept exact for pixel-identity (P3.4). |
| `FrameRowHoverBg` | `#12122A` | Parser frame-list **hover** row fill. Near-dup of `PanelHeaderHover`/`Deep` (unify candidate); exact for pixel-identity (P3.4). |
| `FrameMsgBadgeBg` | `#16162E` | Parser frame-list message-count badge pill. Near-dup of `PanelHeaderHoverDeep` (unify candidate); exact (P3.4). |

### Borders / dividers
| Token | Hex | Role |
|---|---|---|
| `BorderSubtle` | `#1A1A32` | Default divider / panel edge. The workhorse border. |
| `BorderStrong` | `#1E1E34` | Heavier separators (section-header bottom, group dividers in NavStrip). |
| `HexRowSeparator` | `#1E1E38` | Hex row rule; card prop-row hover. |
| `HexBannerBorder` | `#252548` | Hex banner edges. |
| `BorderAccent` | `#252545` | Card outer border (`InspectorCard`). |
| `PrimaryButtonBorder` | `#2A2A54` | Primary button border. |
| `BorderTranslucent` | `#33FFFFFF` | Translucent white hairline: the **RuleWorkbench pane outlines** (7×). A deliberate fork from the blue-purple border ramp. **Value coincides with `Pb2dKillFeedBorder`** (a walled-off 2D-HUD domain token), **not** reused across the domain boundary (P3.3). |

### Text ramp (dim → bright; cool blue-purple)
Use the **dim** end for labels/metadata, the **bright** end for primary values. Common picks in bold.
| Token | Hex | Role |
|---|---|---|
| `TextLabel` / `TextLabelAlt` | `#30305A`/`#303060` | Tiny uppercase section/column labels. |
| `TextDim` / `TextDimAlt` | `#404068`/`#404070` | **De-emphasized metadata, disabled/placeholder text.** |
| `TextHexGutter` | `#44446A` | Hex offset/ASCII gutters. |
| `TextStatusBar` | `#44447A` | Status-strip text. |
| `TextEntityStatus` | `#4A4A72` | Entity status line. |
| `TextHeaderField` | `#505080` | Field-meta / hit-count. |
| `TextCardSize` | `#50508A` | Card byte-size label. |
| `TextChainSummary`/`TextChainBadge` | `#606080`/`#6060A8` | Parse-chain text/badges. |
| `TextFrameInfo` | `#6868A8` | **KeyValueTable keys, frame info.** |
| `TextMid` | `#7878A8` | **Default body text; `nav-btn` foreground.** |
| `TextHexBanner` | `#8080B8` | Hex banner text. |
| `TextHeaderHex` | `#9090C8` | Header hex value. |
| `TextFrameType` | `#9090C0` | Parser frame-list full type-name text. Near-dup of `TextHeaderHex` (unify candidate); exact for pixel-identity (P3.4). |
| `TextEntityFieldVal` | `#9898C8` | Entity field values; KeyValueTable value. |
| `TextBright` | `#A0A0D8` | **Emphasis text; breakpoint rows; frame-input.** |
| `TextFieldName` | `#A8A8D8` | Card field names. |
| `TextValue` | `#C0C0F0` | **Primary value text (brightest ramp).** |
| `TextHexCell` | `#C8C8E8` | Hex byte cells. |
| `TextCardHeader` | `#D0CCF8` | Card header / message-type name. |
| `TextOnAccent` | `#12121E` | Text ON an accent fill (badges), near-black. |

### Accents / highlights
| Token | Hex | Role |
|---|---|---|
| `AccentInteractive` | `#5050A0` | Interactive accent (selection, active affordances). |
| `AccentHighlight` | `#9C27B0` | Purple highlight (matches depth-4). |
| `AccentAmber` | `#FFC107` | **Attention/debug accent: breakpoint cluster, "stopped-at", jump-to-hit.** |
| `AccentAmberOpaque` | `#FFFFC107` | Opaque amber variant. |
| `DeltaRowBg` | `#25FFC107` | Amber-tinted delta-row background (12% alpha). |
| `AccentError` | `#E53935` | Errors (tracker/decode error text). |
| `AccentErrorSoft` | `#E57373` | Softer error red for secondary error text: RuleWorkbench diagnostic `file:line` location (P3.3). |
| `AccentCaution` | `#E0A030` | Muted caution gold: RuleWorkbench "shipped, read-only (use Save As)" indicator. Distinct from brighter `AccentAmber`. **Value coincides with `Pb2dTeamT`** (2D-HUD domain), **not** reused across the boundary (P3.3). |
| `StatPositive` | `#4CAF50` | Strong-good end of the stat heat ramp; matches depth-2 green. |
| `StatPositiveSoft` | `#5FA894` | Mild-good tier of the ramp (teal). |
| `StatNegativeSoft` | `#D98A3F` | Mild-bad tier (amber, **not** a desaturated red: the two bad tiers must differ in lightness too). |
| `StatNegative` | `#DC5A52` | Strong-bad tier. Deliberately calmer than `AccentError`: a bad stat is a judgement, not a fault. |
| `StatBarTrack` | `#212140` | Unfilled part of an in-cell stat bar. Lifted off the old `#16162E`, which sat at contrast 1.00 against `CardBg` and drew no track at all. |
| `StatBarFill` | `#33335E` | Filled part. **Neutral by design**, never tinted by sentiment. |
| `StatSlot0`-`StatSlot5` | see catalogue | The composition bars' **categorical** palette. A different job to the ramp: "which thing", not "how good". Not borrowed accents, so a monochrome theme cannot collapse them into one hue. |
| `PrimaryButtonHover` | `#252548` | Primary-button hover. |

### Hex legend swatches (semi-transparent, `BinaryPane`)
`HexSwatchSelected` `#CC4C9EF5` (selected), `HexSwatchParent` `#8855BB8A` (parent),
`HexSwatchAncestor` `#55C07C28` (ancestor).

<a id="playback2d-palette"></a>
### Playback2D palette (`Pb2d*` prefix, P3.3a)
The **2D Playback** tab reads like a game radar/HUD, **not** the parser dev-tool. It deliberately uses
its own **cool-grey HUD text ramp** + **game-semantic status colors**, distinct from the app's
blue-purple chrome ramp above. **Key finding:** *no* Playback2D value coincides (exact hex, alpha
included) with an existing app token, e.g. app `TextOnAccent #12121E` ≠ Playback2D on-team-chip
`Pb2dTextOnTeam #101418`. So P3.3a **reused 0 existing tokens and added 29**: that is correct (a
distinct domain palette), not a duplication miss. All are prefixed `Pb2d`; the chrome/domain split
below is a *role* attribute (furniture vs game-meaning), not a naming rule.

**Chrome (HUD furniture), 14:** surfaces/overlays: `Pb2dPanelBg` `#1A1E24` (right panel),
`Pb2dInfoBg` `#181C22` (game-info band), `Pb2dCardBg` `#20262E` (attribute card),
`Pb2dGridSplitter` `#22272E` (splitter; == viewport minor-grid), `Pb2dHudDivider` `#33404A`,
`Pb2dOverlayBg` `#CC15181C` (translucent HUD strip = viewport bg @ 80%), `Pb2dKillFeedBg` `#E6090B0E`,
`Pb2dKillFeedBorder` `#33FFFFFF` (white @ 20%); cool-grey text ramp: `Pb2dTextDim` `#5C6670`,
`Pb2dTextMid` `#9AA4AF` (== viewport `LabelBrush`), `Pb2dTextBright` `#C0C8D0`,
`Pb2dTextBrightest` `#DDE3EA`, `Pb2dTextOnTeam` `#101418`, `Pb2dGlyphBlind` `#E6E6E6`.

**2D-domain (game-semantic), 15:** `Pb2dTeamCt` `#4A90D9`, `Pb2dTeamT` `#E0A030`,
`Pb2dPositive` `#86C786` (phase/weapon/cash green), `Pb2dHealth` `#7ED07E`, `Pb2dArmor` `#7FB6E6`,
`Pb2dHeadshot` `#F44336`, `Pb2dWallbang` `#FF9800`, `Pb2dNoScope` `#00BCD4`,
`Pb2dFlashAssist` `#B66CD8`, `Pb2dAssist` `#5BC0BE`, `Pb2dDefuser` `#E0C040`, `Pb2dBomb` `#E08040`,
`Pb2dAdr` `#E0A878`, `Pb2dDefuseTime` `#5AB0E0`, `Pb2dMapApprox` `#C0A060`.

> **Scope + follow-up (P3.3a).** Only `Views/Playback2D/Playback2DView.axaml` was de-inlined
> (67 bare-inline markup literals → tokens). The Skia renderer `Modules/Playback2D/Playback2DViewport.cs`
> (~34 hex) and the `PlayerAttributes.TeamColor` VM (3 hex) **keep** their colors: they already hold them
> as **named `static readonly` brushes/pens** and a named property (already centralized, not bare-inline);
> converting Skia static-init `Color.Parse` fields to `FindResource` is init-order/WASM-fragile and would
> risk the Playback2D render-test battery for ~zero gain. **Known duplication (follow-up to unify):** team
> `#4A90D9`/`#E0A030` now live in `Pb2dTeamCt`/`Pb2dTeamT` **and** the renderer's `TeamCtBrush`/`TeamTBrush`
> **and** `PlayerAttributes.TeamColor`: the token is the canonical value the copies match **exactly**;
> because the tokens are app-level (not `UserControl.Resources`), a later pass can reference them from the
> renderer/VM to collapse the triplication. Same for viewport-bg/grid/label. **Verified no-op:** every
> token's hex == its original literal (value-equivalence table) **and** `Playback2DHeadlessSmokeTests`
> instantiates the real `Playback2DView` with a synthetic VM (no demo) → all `Pb2d*` `{StaticResource}`
> keys resolve at control-load; the rendered frame is correct (team chips / HP-green / grey HUD ramp).
>
> **Follow-up CLOSED (recorded v0.6.0):** the "keep their colors" state and the triplication described
> above no longer exist. The theme-system token promotion moved the renderer onto token resolution
> (`BuildPalette` + `ActualThemeVariantChanged`; the remaining hex are fallbacks) and a follow-up deleted
> `PlayerAttributes.TeamColor`. The correction block below covers the theme-reactivity half.

> **Correction (CSVG Phase-3, 2026-07-18): the `Pb2d*` palette is now fully THEME-REACTIVE: L2 has
> shipped; the "kept dark in every theme" / `{StaticResource}` phrasing above is stale.** The `[Light]`
> `ThemeDictionaries` block re-authors the 2D tokens (`Pb2dOverlayBg #CC15181C→#E6EDEFF3`, `Pb2dTextBright
> #C0C8D0→#363D47`, `Pb2dPositive #86C786→#2A7530`, `Pb2dCanvasBg #15181C→#E7E9ED`, …) and the Skia
> viewport re-resolves them on `ActualThemeVariantChanged` (`Playback2DViewport.BuildPalette`). So the 2D
> tab is a **dark island on dark, a light island on light**. Verified by rendering `playback2d-canvas`
> (light) as a light grid and `playback2d-livesync-hud` (dark/light/high-contrast). Any doc line that says
> a `Pb2d*` token is "held identical to Dark" is superseded by this.

<a id="pb2d-hud-dot"></a>
**`Ellipse.pb2dDot.*`: the walled-off HUD-dot styles (CSVG Phase-3, §5.3).** The 2D Playback tab's
in-context CS2 live-sync indicator (`Views/Playback2D/Playback2DView.axaml`, top-right HUD stack above the
kill feed) uses a **Pb2d-domain sibling of the app-chrome `Ellipse.dot.*`**: same bound-class→token
mechanism, but resolving `Pb2d*` tokens so it stays in the walled-off HUD palette (D21), NOT the
blue-purple chrome ramp. Defined in the view's local `UserControl.Styles` (a Pb2d-domain style belongs
with the tab, not in shared `Styles/*.axaml`): base `Ellipse.pb2dDot` + `.good` (`Pb2dPositive`) / `.working`
(`Pb2dTextBright` neutral) / `.degraded` (`Pb2dTeamT` = the "caution-equivalent"; its value deliberately
coincides with `AccentCaution`, D21) / `.error` (`Pb2dHeadshot`), a `.hollow` ring variant (inferred
pause), and a `.pulsing` opacity animation (Following = CS2 is the clock master). The dot bucket is a
`LiveSyncHudDot` enum on the module VM; the label reuses `Pb2dTextBright`. The indicator is **display-only /
non-interactive** (the whole HUD stack is `IsHitTestVisible=False`). See decision D-CSVG3 below.

<a id="librarycard-palette"></a>
### Library card overlay palette (`LibraryCard*` prefix, P3.3)
The **Library** tab paints text / scrim / badges **over a baked radar THUMBNAIL**, so, like Playback2D,
it needs a domain palette distinct from the app's blue-purple chrome ramp: a **white-on-image legibility
ramp**, a **darkening scrim**, and **dark translucent badges**. **None of the 9 values coincide with an
existing app token**, so P3.3 reused 0 and added 9 (correct, a distinct domain, not a duplication miss).
The card **title stays literal `White`** (the ramp's 100% top; not inline hex, so left as-is).

**Scrim (1 composed brush):** `LibraryCardScrim`: a `LinearGradientBrush` (stops `#C805050F`@0 /
`#5805050F`@0.34 / `#7005050F`@0.62 / `#EE05050F`@1) that replaced **four** inline gradient stops with one
token. **This is the only non-`SolidColorBrush` token in the palette** (a gradient can't be a `Color`
resource referenced from `GradientStop.Color`, and a single named brush is cleaner than 4 `Color` tokens).

**White-on-image text ramp (4):** `LibraryCardTextBright` `#ECFFFFFF` (players), `LibraryCardTextMid`
`#C6FFFFFF` (subtitle), `LibraryCardTextDim` `#B4FFFFFF` (meta ×3), `LibraryCardTextFaint` `#A0FFFFFF`
(score-badge colon). **Overlay furniture (2):** `LibraryCardBadgeBg` `#B0000010` (dark translucent
score-badge pill), `LibraryCardBusyTrack` `#33FFC107` (amber track behind the parse `ProgressBar`; the
foreground stays `AccentAmber`). **Score-badge team colours (2):** `LibraryCardScoreCt` `#5BA9F4` (CT
blue), `LibraryCardScoreT` `#F0B23C` (T gold).

> **Known team-colour fragmentation (follow-up to unify, NOT this pass: would shift pixels).**
> `LibraryCardScoreCt/T` are the app's **THIRD** CT/T pair, alongside Stats' `AccentInteractive`/`AccentAmber`
> team bullets and the `Pb2dTeamCt/T` HUD pair, each a **different** hex. Unifying is a deliberate colour
> change, out of scope for a pixel-identical de-inline; recorded here + in `DarkPalette.axaml`.

### Depth ramp (not in DarkPalette, code-owned, `MainViewModel.DepthBrushes` + `DepthHighlightConverter`)
10-step hue wheel at `0xC0` alpha, consecutive depths ≥85° apart. Duplicated in two
places that **must stay in sync**. Candidate for future promotion into the palette as an indexed
resource; not done here.

> **Token note (RESOLVED P1.3):** the `.primary`/`.ghost` button classes now live in `Primitives.axaml`
> bound to the existing `PrimaryButton*` / `TextMid` / `PanelHeaderHover` tokens. The DebuggerPanel no
> longer hand-rolls `Button.primary` and the NavStrip no longer re-declares `.nav-btn`. **No new tokens
> were required** for the entire P1.3 foundation: the palette already carried every color the promoted
> looks needed (`PrimaryButton*`, `AccentAmber`, `TextFrameInfo`, `ChainSummaryBadgeBg`, `TextLabelAlt`,
> `BorderSubtle`/`BorderStrong`). This is expected: P1.3 de-duplicated existing looks rather than
> inventing new ones. Adding a token remains the same process (right group + semantic name + role note).

---

## 2. Component contracts (shared controls)

Before adding UI, check here for an existing control. Promote any ≥2× pattern to a shared control and
record its contract. Current shared controls live in `src/App/DemoViewer.NET/Controls/`.

### InspectorCard
- **File:** `Controls/InspectorCard.axaml` (+ `.axaml.cs`). **DataContext:** `HarvestCardViewModel`.
- **Purpose:** the single adopted message-card surface: accent strip + category badge +
  click-to-select header (with byte size) + column-header row + collapsible payload `TreeView` whose
  per-row select drives the node→hex highlight loop.
- **Key structure:** `Border.msg-card` (CardBg, `BorderAccent`, radius 8); left 4px `AccentBrush`
  strip; header `Button` (ToggleExpand + select); body `TreeView.card-tree`.
- **Contract note (supersedes "UI v2 review §5.10"):** `TreeView.card-tree TreeViewItem` defaults
  `IsExpanded=True`: **safe only because card trees are <50 rows.** Do **NOT** copy this default onto
  the entity field tree (`EntityTrackingTabView.axaml`), which can be huge; it would force-realize
  every node and destroy virtualization.
- **Used in:** the 4 message-card list surfaces (Parser card list + descendants).

### Hosted tab sections (the Strat Book rail, the Library's Teams view)
- **Files:** `ViewModels/Shell/TabSectionHost.cs` (the list + selection + lifecycle) and
  `ViewModels/Shell/IHostTabViewModel.cs` (what a host tab's VM exposes) in the shell; the Strat Book hub is
  pack-owned: `Extensions/StratBook/ViewModels/StratBook/StratBookHubViewModel.cs`,
  `Extensions/StratBook/Views/StratBook/StratBookHubView.axaml` (the rail) and `StratBookPack.HubHostTab`
  (the contribution); the Demos / Teams toggle in `Views/Library/LibraryTabView.axaml`.
- **Purpose:** a module tab that belongs to a workflow rather than the strip. A descriptor names its host by
  id in `WorkspaceTabDescriptor.HostId`: `"stratbook.hub"` puts it on the Strat Book tab's left rail (164px,
  collapsible to a 32px strip, `PanelHeaderBg`, `sectionHeader` band bound to the host's rail label, "STRAT BOOK",
  `ListBox.strat-rail` items in the shell tab's monospace 13 with the header's badge on the right);
  `"builtin.library"` puts it behind the Library toolbar's Demos / Teams toggle. The strip went from four
  tabs to eleven when every Strat Room feature took its own; the rail is where such features go now.
- **Contract:** a host tab is a pack contribution (`IPackContributions.HostTab`, a `HostTabContribution`: host
  id, tab id, header, strip order, rail label, feature id, VM and view factories); the shell builds the host VM
  with the strip and keys one `TabSectionHost` per host id, the Library being the built-in host. The descriptor
  keeps its `TabId` and feature id, so `TrySelectTab`, the gate and the session file treat a section exactly as
  they treated the strip tab (the shell resolves a section id through its host and persists the section id as
  the active tab; a section of a hidden host answers false). A section is `Activate`d only while it is selected
  AND its host tab is, so the one-realized-View invariant holds one level down. A contributed host tab shows
  only while its feature id resolves on and some hosted section does, and the selection lands on Library when
  it goes away. A section naming a host nothing contributes is dropped with a module log line, never put on the
  strip.
- **Do not:** add a Main-strip tab for a Strat Book feature; add a section. Do not add a `TabPlacement`
  member for a new host; contribute a host tab and name it.

### Collapsible side pane (the Strat Book rail and the strat list)
- **Files:** `Extensions/StratBook/ViewModels/StratBook/StratBookLayout.cs` (the two flags and their toggle
  commands), `Extensions/StratBook/Views/StratBook/StratBookHubView.axaml` (the rail), the list column and the
  `StratPicker` header in `Extensions/StratBook/Views/StratBook/StratBookTabView.axaml`, `Button.pane-toggle` in
  `Styles/Primitives.axaml`.
- **Purpose:** give a working surface the room a navigation pane takes. The editor column was about 340 px at
  1280 wide with the rail (164) and the strat list (280) open.
- **Contract:** the pane's own header carries `«` (a `Button.pane-toggle`, 24 px). Collapsed, the pane becomes a
  32 px strip: `»` at the top, then either the pane's items read top to bottom (the rail keeps every section and
  its badge, rotated 90 degrees in a `LayoutTransformControl`, same `ItemsSource` and `SelectedItem`) or the
  pane's name when its items cannot be read that way (the list strip reads STRATS or DETECTED). Navigation the
  pane was the only way to reach moves next to the content while it is collapsed: a header over the list's
  neighbour column carries the Book / Detected toggle, a combo box that follows it (the book's strats or the
  detected patterns) and a Callouts button whose flyout holds the alias editor (`CalloutsEditorTemplate`, the
  same template the list column uses). List management (Delete, Find strats, Show settled) stays in the pane;
  `»` reopens it. The column is `Auto`; the open pane sets its own `Width`.
- **State:** one `StratBookLayout` is shared by the hub and the Strats section (a DI singleton, passed to both)
  and lives in the session file as `SessionPayload.StratBook`. It is restored with the active tab in
  `RestoreSession`, not through module-tab state, because module-tab state waits for a demo load and a
  descriptor's VM is built only on activation, so a collapsed pane would reopen whenever the user did not
  open a demo or the Strat Book that session.
- **Do not:** hide a pane without leaving the strip and its `»`; drive the collapse from a view-local flag
  (it would not persist); add a second copy of a pane's items unless the strip shows them.

### Strat step row (the Strats editor)
- **Files:** the `StepRows` template in `Views/StratBook/StratBookTabView.axaml`, `StratStepRow` in
  `ViewModels/StratBook/StratEditorViewModel.cs`, the table in `Services/Strats/StratStepFields.cs`.
- **Layout:** line one is `#`, at, who and verb in fixed columns (20, 56, 64, 84) with move up and move down
  stacked (`.icon-btn.stepMove`, 15 px each), duplicate and remove (`.icon-btn`) on the right; line two is a
  `WrapPanel` of label-over-field pairs indented under "at". At 1280 with the rail and the list open the editor is
  315 px and line one fills it: a fifth button or a wider column needs room taken from somewhere else.
  The editor's `ScrollViewer` has `HorizontalScrollBarVisibility="Disabled"`; every row in it must wrap or
  shrink (metadata is a `WrapPanel` of 132 px pairs, slots are `44,*,Auto,*`, record rows `*,32,...,64`).
  `StratEditorRoomTests` walks the editor's fields at 1280 wide and fails on any right edge past the viewport.
- **Fields by verb** (note is always there; a member the verb does not use still shows while it holds a
  value, so nothing an export prints is hidden):

  | Verb | Fields | `to` reads |
  |---|---|---|
  | move | from, to (the validator warns on a move without `to`), via | to |
  | rotate | from, to, via | to |
  | push | from, to, watching, via | to |
  | hold, peek | to, watching | at |
  | plant, defuse | to (`StratFromRound` writes a plant's place there) | site |
  | throw | utility, then lineup, then lands at only while no lineup is picked | |
  | fake | to, utility, watching | at |
  | lurk | watching, via, lurk areas, rotate at, or when, rotate to | |
  | wait, call | note only | |
  | other, or a verb outside the vocabulary | from, to, utility, watching, via | to |

  Move and rotate take no watching: a moving player watches their path, and the facing that matters belongs to
  the hold or push after it.

- **Who** (`Button.stratWho`, styled like the combo boxes beside it, with a `▾`): the step's players, `all` or
  `B, C`. Its flyout holds A to E as 30 px toggles and an `all` button; the toggles are staged and written when the
  flyout closes (`StratStepRow.CommitWho`, `StratLinePatches.SetWho`), so picking two players is one undo entry. None
  checked writes nothing and the toggles show the step again. Opening takes a snapshot (`BeginWho`); a reprojection
  while it is open (an edit elsewhere, the zones landing) leaves the toggles alone, and closing writes the players
  added and removed since the snapshot onto the step as it then is, by the step's id. A player unticked and ticked
  again in one session is no change, so their line is kept. The write is by slot: a remove per player dropped and
  an add per player added, never an index-wise rewrite that would hand one player's line to another. A step deleted, or a strat
  switched, while it is open writes nothing. A flyout rather than a wider control: the 64 px column
  is all line one has at 1280.
- **One place and watching for players who agree** (`ShowCompact`): while every line says the same apart from its
  slot (place, watch and angle), the row shows one `to` and one `watching` under line one, a `*,*,Auto` grid (a
  field takes both columns while the other is hidden), and an edit writes every line (`StratLinePatches.EditAll`).
  A step for everyone is five agreeing lines; one player is one. The compact `via` (travel verbs, or while the lines
  hold one) sits on a second row of the grid across both columns, labelled `via`, multi like watching
  (`GroupViaField`). `split` (shown with more than one player and a place, watching or via to differ in) shows the
  lines one per player; `join` under the lines shows once they agree again. The
  row only goes apart on split or on an edit that makes the lines differ (a cone drag on one player), and only
  comes back on join, so a commit never swaps the fields under the caret. Lines that already differ open apart.
  Split on a step for everyone shows five lines, and a line's edit writes all five (the others keep the step's place);
  editing them back to agree folds the file to `all` while the row stays apart until join.
- **Lines** (`StratLineRow`, written only through `Services/Strats/StratLinePatches.cs`): shown apart, under line one, indented
  like the fields, a `48,*,*,Auto` grid per player: slot, place (labelled with the verb's `to`, `at` or `site`, shown
  when the verb uses it or it holds one), watching, then the angle and remove buttons; a second row, when the verb
  travels or the line holds one, has a right-aligned `via` label under the slot and the line's via across the place
  and watching columns (`LineViaField`); a `who / to / watching` label row above, and `+ player` under them. A step for everyone has no lines and keeps its own `to` field; a one-player
  step shows one implicit line (the slot as text, changed with the actor combo, no remove); a step with stored lines
  greys the actor combo, whose value is their summary. `+ player` adds the first slot without a line: on a step for
  everyone that names the first player, who takes the step's place. The writer keeps the stored shape on every edit:
  no lines is a step for everyone, one line with no watch is a plain step (the actor rewritten, `assignments`
  removed), five bare lines to one place fold back into a step for everyone, anything else is `assignments` with the
  summary actor and no step-level `to`. A line's slot combo lists all five and is a burst field like the verb. The
  whole line moves to the slot picked, watch and angle included, and the player's position on that step moves with
  it; a slot another line holds swaps the two lines' slots and positions (`StratLinePatches.ChangeSlot`), so
  re-lettering players never needs a remove and `+ player`, which would drop the removed player's line. Swap rather
  than refuse: refusing left remove and re-add as the only way to re-letter. A split step for everyone lists only
  the line's own slot, since any change folds back to the same step. A combo pick that writes nothing reprojects the
  row, so a combo never shows a value the file does not hold. A line's watching shows when the verb uses it or the
  line holds one. Every place and watching field in the row (and from, lands at, lurk areas and rotate to) is a
  location field (see "Location field"): stored canonical, shown as callouts or coordinates, written on focus loss
  or a pick. The angle button (`135°`) shows only while `watch.yawDegrees` is set and clears it, so the cone faces
  the first watched place again. The line Set On Map writes has a 2 px `AccentInteractive` bar on its left
  (`Border.stratLine.lineSelected`); focus or a press in a line's field selects its step and the line. At 1280 with
  the rail and list open the line's fields are about 95 px each, so a watching list's chips wrap onto more lines and a
  long callout trims inside its chip; from, lands at and rotate to are 150 px, so a coordinate with both buttons reads
  whole.
- **Verb change:** the verb and a remove for each member the new verb does not use and the step has (a lurk
  included); each line's `to`, watch and via the verb does not use are cleared through `StratLinePatches` in the same
  entry, so a one-player line that loses its watch folds back into a plain step. Via goes with any verb that does
  not travel (hold, peek, fake, plant, defuse, throw, wait, call).
  Positions, strokes, hold and note are never touched. RoleSheet, StratTextExporter and LAN print print
  whatever is set, which is why the clear is not optional.
- **Combo box bursts:** a closed combo box changes value on the mouse wheel and on Up/Down, so the verb, kind
  and lineup combos pass through values the user never meant. Consecutive changes of one combo on one step are a
  burst (`StratEditorViewModel.ApplyInBurst`): the ops are computed from the step as it was when the burst began
  and replace the burst's own undo entry (`StratSession.ReplaceLast`). Passing through "none" or "wait" and back
  restores everything and leaves no entry; ending elsewhere leaves one. A burst ends when the combo loses focus
  (the view calls `EndEditBurst`) or on any other edit (the session's version moved).
- **Utility edits:** a kind change replaces `kind` and removes `lineupId` and `technique` (they belong to the
  old kind) but keeps the landing; a lineup change drops the old lineup's `technique`; typing "lands at"
  writes `landing/place` only, so a captured landing point survives, and clearing a place that was the landing's
  only member removes the landing. A lineup id the lookup does not offer is
  added to the row's options as its raw id (a combo box shows nothing for a selection outside its items).
  The lineup options are filtered by kind, so the kind always matches a picked lineup; a picker that sets
  both must write the kind first, in the same ops list, because a kind change drops the lineup.
- **Lineup and technique:** the lineup combo lists the strat map's lineups of the step's kind from
  `StratLineupCatalog`, which reads `LineupOriginSource`'s per-map grouping (off the UI thread; the editor
  re-projects on `Changed`). A stored id resolves through every alias id to its lineup's name; projection never
  rewrites it. A lineup thrown more than one way adds a "thrown" combo (label and throw count), a burst field like
  the others; a stored null technique shows the most thrown one without writing it, and the burst compares with
  that effective value, so wheeling back to the most thrown leaves no explicit copy. "Pick on map" sits last in the
  wrap and opens the lineup picker.
- **New step and duplicate:** Add step, and Enter, insert after the row focus was last in, or at the end, 5 s later
  on the strat's clock than the step before (a lower time on the round clock, a higher one from a trigger), held
  between its neighbours and, on the round clock, no earlier than -1:00 unless that step already is, so it is never
  refused. The first step goes at the start: 1:55, or +0:00 from a trigger. The new step is a move by `all` that starts with every token written where
  the projection has it at the step before, and no strokes (`StepAuthoringPatches.AddCarriedStep`, which the
  canvas's Add step uses too), each marked `carried`. A copy, not a reference: the stationary rule would show the same
  places without one, but then a later drag on the earlier step would move the new step's tokens too. The mark lets
  the new step's destination win over the copy; a token a destination has already sent somewhere is not carried. A thrower whose lineup resolves
  is written at the lineup origin (level on the canvas's floors, the throw's yaw); an unresolved lineup (still
  grouping, or a stale id) carries the thrower's last authored place, as the projection shows it. Duplicate is
  the canvas's (`StepAuthoringPatches.DuplicateStep`): the same fields, positions and strokes (strokes under new
  ids), a fresh step id, 5 s later held before the next step. Each is one undo entry and focuses the new row's time.
- **Times** follow the strat's clock (`StratClock.Format` and `TryParse` with the clock block): `1:15` and `+0:05`
  after the plant on the round clock, `+0:08` from a trigger, where `0:08` and `8` read the same and `-0:02` is before
  the trigger. The steps heading says which: `Steps (round clock remaining, m:ss; +m:ss after the timer stopped)` or
  `Steps (from the trigger, +m:ss)`.
- **Lurk fields** (`ShowLurk`): lurk areas (a multi location field like watching, 200 px), rotate at
  (56 px, a time on the strat's clock; text that is not one shows the stored time again), or when (an `AutoCompleteBox`
  suggesting `StratVocabulary.RotateConditions`, free text stored), rotate to (128 px, like `to`). All written
  through `StratLurkPatches` on focus loss, one entry each. The place fields are the plain ones the row uses for
  `to` and watching, named `LurkAreasField` and `RotateToField` (and `GroupPlaceField`, `GroupWatchField` on the
  compact row) so a shared location field can replace them by name.
- **Inline checks:** each row marks the validator's warnings and refusals beside the field their pointer names
  (`StratStepRow.SetIssues`): a `⚠` in `AccentCaution` for a warning, a filled `!` badge on `AccentError` for a
  refusal, the message in the tooltip, and every message again as a text line under the row for keyboard and
  screen-reader users. A pointer under `/steps/i/assignments/j` marks line j's slot, place or watching field (the
  text line names the slot and field, `(B to)`), and a one-player step's `/to` marks its implicit line's place.
  Unknown places (step `from`, `to` and landing, a line's place and watched callouts) are warnings, never refusals,
  checked against the map's zones the canvas already loaded (`StratSession.PlaceLookup` over
  `StratCanvasViewModel.LoadedPlaces`); none are shown while the zones load, and the rows revalidate when they land.
  A step-level `to` beside stored lines shows read-only with a clear button, which writes through `StratLinePatches`. Place fields carry the marker after their label; at, actor and verb on the field's top right
  corner. An issue about the step itself (`/positions`, `/interpolation`, unknown fields), or about a field the row
  hides (a technique warning while the "thrown" combo is hidden), marks the number. Infos stay in the Checks list
  only. `StratSession.Issues` validates at most once per document version, with the commit's rules, so the rows and
  the bottom Checks summary always agree; text fields write on focus loss, so a strat is validated per edit, never
  per keystroke. A lineup id is checked against `LineupOriginSource`'s grouping of the map (`LineupLookup`, from
  memory): "not checked" (an info) while the map groups, a warning on the lineup field once it is grouped and the id
  does not resolve. The grouping landing invalidates the cached issues.
- **Keys** (`StratBookTabView.axaml.cs`, a tunnel handler on `StepRows`, so a field does not see them first):

  | Key | Where | Does |
  |---|---|---|
  | Enter | a single-line field, or the row | commits the field, adds a step after the row (a location field's list a user has typed in picks instead) |
  | Ctrl+D | anywhere in the row but an open combo | commits the field, duplicates the row |
  | Delete | the row itself, never inside a field | removes the row (and its branches); focus stays at that index |
  | Escape | a field | leaves the field for its row, committing it (a location field's list a user has typed in closes first) |
  | Alt+Up / Alt+Down | a field or the row, not a combo (Alt+Down opens one) | moves the row |
  | Tab | | the visible fields left to right, then the next row's time |

  The row is the item container: focusable, not a Tab stop, bordered `AccentInteractive` while it has focus. The row
  buttons are not Tab stops either (the keys above do their jobs), so Tab never stops between two rows' fields.

### Start row (the Strats editor)
- **Files:** the `StartRow` border in `Views/StratBook/StratBookTabView.axaml`, `ViewModels/StratBook/StratStartRow.cs`,
  `Services/Strats/StratStartBlock.cs` (reading, the first write and every later one), `StratStartPhrasing`.
- **What:** where every token stands before step 1, and what starts the strat. Not a step: no number, time, who or
  verb. It sits under the steps heading and above the step columns, so the column labels stay with the steps.
- **Collapsed** (the default): one line, `Start` in the number's style, then the start and the trigger joined by `·`
  (`spawn · on the call`, `as captured`, `A Long Doors, B (1234, -561)`), trimmed with the whole line in the tooltip,
  and a `▸` button on the right that opens it. A strat with no start reads `no one placed: tokens appear at their first
  step`.
- **Open:** `what starts it`, the strat's `trigger.text` (it moved here from the metadata; the trigger stays where the
  file always had it); `players · spawn|custom|captured|from step 1` with a `Spawns` button (shown once the map's spawns are read,
  puts every token back, a `spawn` start, one entry); a `24,*` grid per token of A to E, then `opponents` and O1 to O5,
  each a single location field (`spawn, a callout or (x, y)`) with its map pick. At 1280 with the rail and list open the
  fields are about 250 px, so a coordinate reads whole. A field edit or pick writes that token's start, one entry, and
  makes the start `custom`; clearing a field takes the token's start away. An edit the canvas would not show (a step on
  the start's tick places the token, as a capture's freeze-end step does) or one that must wait for the map's spawns is
  not written: a one-line `AccentCaution` note under the row's first line says why (`StartNote`), and the field shows
  the stored value again. The `players ·` label says `from step 1` for an older file's start that is not the spawns.
- **Selection:** a press or focus anywhere in the row selects the start (`StratStepSelection.SelectStart`,
  `StratCanvasViewModel.SelectStart`): the transport pauses at tick 0, no step row is selected, and the row takes the
  step row's selected look (`PanelHeaderHoverDeep` fill, the 3 px `AccentInteractive` bar, a bold number). A token
  drag there writes the start (see "Map-first editing"). Leaving tick 0 or selecting a step ends it.
- **Older files:** the row shows the start the file implies (docs/strat-format.md, "The start") and writes nothing
  on open; the step entries it was read from leave their step's placed chips, since the row says the same. The first
  edit writes the block in one entry, the history line `start read from the round-start step`.

### Clock switch (the Strats editor's header)
- **Files:** the `ClockSwitch` combo in the metadata `WrapPanel`, `StratEditorViewModel.ClockChoice`,
  `StratClock.SwitchOps`.
- **What:** a 132 px `clock` pair like the others: `Round` (1:55 counting down) or `From trigger` (+0:00 counting up
  from what starts the strat). A choice rewrites every step time and lurk rotate time in one undo entry and moves no
  tick; the step rows, the step track's tooltips, the transport's clock and an export's range labels and burnt-in
  clock follow. After a switch a `TextMid` line under the metadata (`ClockNote`) says times written in notes, branch
  conditions and the trigger were not converted; it goes when another strat opens. A combo, not a toggle pair: it reads as the fields beside it and fits their 132 px.

### Lineup picker (the Strats editor)
- **Files:** `ViewModels/StratBook/LineupPickerViewModel.cs`, `Views/StratBook/LineupPickerView.axaml`, the
  `LineupPickerOverlay` in `Views/StratBook/StratBookTabView.axaml`, `OpenLineupPicker` in
  `StratBookTabViewModel`, `ApplyLineupPick` in `StratEditorViewModel`.
- **Purpose:** pick a throw step's lineup by where it lands and where it is thrown from, instead of by name.
- **Reuse, not a copy:** the picker hosts a `UtilityBookTabViewModel` and the Utility Book's `UtilityMapHost`.
  The VM takes two opt-in settings the tab never uses: `lockedMap` (the strat's map, never another, even with
  no lineups) and `QueryKinds` (a strat utility kind can be two grenade kinds: molotov and incendiary). `Reveal`
  focuses a stored lineup (alias ids answer) once the refresh lands, turning single throws on when the lineup
  was thrown only once; an id that is one lineup's own and another's alias names the first (`ByAnyId`'s
  precedence). The app builds it through the same DI helper as the tab (`UtilityBookFor`): same clip
  directory, and its reads go through the processing queue as the "Lineup picker" section, at user priority.
  It draws the strat canvas's already decoded bundle for the map (`ownsMapAsset: false`, never disposed by the
  picker), so opening it decodes nothing.
- **Layout:** a modal card over the whole Strats section on a `ModalScrim` border (no native window, so the
  browser head has it): title, kind combo and "positions thrown once" on top; the map left; on the right the
  focused group's positions (technique label, throws and demos) and the selected position's card (style,
  use counts, technique split, the clip when rendered); the hint line, Cancel and "Use this lineup" at the bottom.
- **Contract:** a step without a kind opens on smoke. Confirm writes kind, lineup and technique as one undo
  entry, kind first; the landing is kept. Confirming the lineup and technique the step already has (an alias
  id included, a null technique read as the most thrown) writes nothing and keeps the stored id; another
  technique of the same lineup writes only the technique. Cancel and Escape write nothing. Either way the hosted
  Utility Book VM is disposed (index subscription).
- **Modal:** the picker takes the focus when it opens (Escape works at once) and the tab body behind it is
  disabled (`TabBody`, `IsEnabled` bound to `!HasLineupPicker`). It cancels itself when the tab deactivates,
  another strat opens or none is, or its step is deleted.
- **Known:** the card's clip lookup (`LineupClipPlanner.FinishedGif`, two `File.Exists`) runs on the UI thread
  when a position is selected, as it does on the Utility Book tab.

### Location field (the Strats editor)
- **Files:** `Controls/PlaceField.axaml` (+ `.cs`), `Controls/PlaceFieldModel.cs` (the logic, `PlaceFieldOptions`),
  `Services/Strats/StratLocations.cs` (reading and printing a location).
- **Purpose:** one control for every location a step holds (from, to, lands at, watching, and the lurk and rotate
  fields), so each can be typed, picked from the map's callouts, or picked on the map.
- **In the Start row:** one per token, aimed at `StratStartBlock.FieldFor(slot)` (`StratLocationKind.Start`, no step).
- **In the step row:** `GroupPlaceField`, `GroupWatchField` and `GroupViaField` (the compact who's place, watching and
  via; they write every line, `StratLocationField.AllLines`), `LinePlaceField`, `LineWatchField` and `LineViaField`
  (one line's, by slot), `FromField`,
  `LandingField`, `LurkAreasField` (multi) and `RotateToField`; `StrayToField` shows a step-level `to` beside lines,
  disabled, beside its clear button. Each binds `Value` to the row's `*Value`, `PickCommandParameter` to its
  `*Target` descriptor, `PickCommand` to the tab's `PickOnMapCommand`, and `ArmedTarget` to `Canvas.ArmedField`, so
  the field that a map click will write shows it.
- **Value:** `Value` is a list of `PlaceRef`, at most one unless `IsMulti` (watching, via, lurk areas; see "Multi
  field" below). Places are stored canonical and shown by the owner's word; a place the map lacks shows as stored; a
  point alone shows as `(1234, -561)`, and in a single field keeps its point for as long as its text is unchanged. Typed text resolves through the owner's callouts, else is stored as
  typed, never numbers alone. A typed coordinate (`(1234, -560)`, `1234, -560` or `1234 -560`, negatives allowed) is a
  point: its level is the stored point's, else the strat's default canvas level, else none. A commit (focus loss,
  Enter or a click in the list) writes only when the value changed. A value pushed from outside (a map pick, an undo)
  shows at once unless the field holds an edit not yet committed.
- **List:** focus opens the map's callouts (`PlaceFieldOptions.For(resolver)`, built once per `CalloutResolver`
  and shared by every field; typing filters that cached list, names starting with the text first, then names
  containing it, matching display name, canonical name and every alias; an exact match comes first, so Enter and a
  blur store the same place). The stored callout is highlighted. The list
  opens below the field, or above when fewer than its rows (at most 200 px) fit below and more room is above
  (`PlaceField.ChooseUp`), chosen again once layout settles, since a field reached by Tab is scrolled into view
  after it opened. The list is drawn in the window's overlay layer. A multi field's list leaves out the callouts it
  already holds.
- **Multi field (chips):** watching, via and lurk areas show one chip per entry, in stored order, in a `WrapPanel`
  above one add field (the field's own text box, with the list, typed coordinates and `⌖`). The order is what the
  file holds: places first, then points, since each list is stored as two arrays (`places` and `points`, `areas`
  and `areaPoints`, `via` and `viaPoints`). The watch cone faces the first entry and via routes in order.
  - **Add:** a pick from the list, Enter on typed text (also with no list showing, so a typed coordinate never
    reaches the row's Enter) or a blur appends one entry and clears the field. A typed name resolves through the
    callouts; a coordinate takes the level of the list's first point, else the strat's default level. A duplicate
    (same place, or same x, y and level) is refused quietly and the field cleared. Text that names a chosen callout
    highlights nothing, so Enter cannot add a neighbour that merely starts the same. A map pick appends too.
  - **Chips:** `Border.placeChip`, focusable but not a tab stop, so Tab goes from the field before straight into the
    add field. The callout or short coordinate (`TextTrimming`, the full text in the tooltip) and a `✕` that removes
    it. Backspace in the empty add field focuses the last chip; Backspace or Delete there removes it and returns to
    the add field. Left and Right walk the chips (Right past the last returns to the add field). Alt+Left and
    Alt+Right move the focused chip one place, and it keeps the focus; a move past either end or between the places
    and the points is refused. The context menu has the same three actions for the mouse. No drag.
  - **Writes:** every add, remove or move stores the whole list through `Value`, so it is one
    `StratLocationPatches.Write` and one undo entry. Edits work on the `PlaceRef`s themselves, never on their text,
    so a point keeps its exact x, y and level. A list pushed from outside (an undo, a pick) rebuilds the chips at
    once and keeps any text typed in the add field.
- **Keys:** the list a focus opens is only a view: Enter and Esc there close it and go on to the row (Enter adds a
  step, Esc leaves the field), so the row's keys work as before. Once the user types, moves the highlight or opens the
  list with Down, the list holds Up, Down, Enter (pick the highlighted callout, or commit the text) and Esc (close the
  list), taking them at the window with a tunnel handler so the row's own tunnel handler never sees them. With no list
  showing (closed, or no callout matches the text) the keys are the row's; Down opens the list, and Esc while the
  field is armed on the map also cancels the pick (after the row's Esc, which it listens to handled). Picking the
  stored callout writes nothing.
- **Buttons:** inside the field's right edge, not tab stops: `⌖` pick on map (shown when `PickCommand` is set;
  `AccentInteractive` and bold while `IsPicking`), and `✕` clear, shown only while a single field holds a point
  without a place (a multi field removes per chip instead). The buttons are 16 px wide with 2 px padding: at 95 px with both showing, the text keeps at least 45 px
  (pinned by `StratLocationFieldTests`).
- **Tokens:** list `CardBg` with a `BorderSubtle` border, rows `TextValue`, buttons `TextMid` with
  `PanelHeaderHoverDeep` on hover. Chips `PanelHeaderHover` with a `BorderSubtle` border and `TextValue` text at
  11 px; a focused chip `PanelHeaderHoverDeep` with an `AccentInteractive` border.

### Map-first editing (the Strats editor and canvas)
- **Files:** `ViewModels/StratBook/StratStepSelection.cs`, `SelectStep`, `BeginSetPlace` and `TryTagPositionAt` in
  `Modules/StratBook/Canvas/StratCanvasViewModel.cs`, `Services/Strats/StratLocationPatches.cs` (the field descriptor
  and its writer), the row handlers in `Views/StratBook/StratBookTabView.axaml.cs`, the toolbar toggle in
  `StratCanvasView.axaml`.
- **One selected step:** the canvas's active step. A press or focus in a step row selects that step: the transport
  pauses and moves to its time, and the step stays active there even when the next step shares its tick (the
  schedule alone would give the later one). The transport, the step track, `[` `]`, a new step and playback move
  the selection, and the rows follow. `[` `]` walk the steps by index, so both steps of a shared tick are visited;
  a step-track marker click seeks by tick and lands on the later one. It holds through an edit of that step, its time included. The selected row
  has a `PanelHeaderHoverDeep` fill, a 3 px `AccentInteractive` bar over the number column and a bold
  `TextCardHeader` number; the bar overlays rather than takes width, because the row's fixed columns fill the
  315 px editor. A selection that moves within the open strat (the canvas, the step keys, playback) scrolls its row
  into view; opening or switching a strat, or deleting the selected step, does not scroll the editor.
- **Selected start:** the Start row selects tick 0 and no step (`StratCanvasViewModel.IsStartSelected`). A token drag
  there writes its start (`StratDragAction.Start`): a body drag its place and point as a single field stores them, a
  cone drag its facing, opponents included. A start pick (a Start row field's pick button) selects the start and the
  next click writes that token's start. With a step selected, a drag at tick 0 still writes the step, so a move at the
  start time takes the drop as its `to`; and a token nothing has placed since its start, at a wait or between steps,
  drags its start rather than refusing.
- **Selected line:** `StratCanvasViewModel.SelectedLineSlot`, the chosen slot when the active step has a line for it,
  else its first line; none on a step for everyone. The rows set it (`StratStepSelection.SelectLine`), and so does a
  press on one of the strat's own tokens. It is kept across steps.
- **Set on map:** a toggle in the canvas toolbar, shown only when the selected step takes a place from the map,
  labelled with the row's word for the field (`Set “to” on map`, `“at”`, `“site”`, or `Set landing on map`). A
  verb that uses `to` sets `to` (fake included); a verb that uses only utility sets the landing when it has a kind
  and no lineup (a lineup says where it lands); wait, call, a lineup throw and a throw with no kind offer nothing.
  One click, one write, one undo entry, then the mode ends. The click comes through `TryTagPositionAt`, ahead of
  the pointer tools, and resolves with `IZonePlaceResolver.ResolveOnFloor` on the clicked pane's floor key.
- **Any location field:** the mode aims at one `StratLocationField` (step, line slot, every line or none, and
  `from`, `to`, landing, watching, via, lurk area or rotate to). A compact who's place and watching write every line; a
  row shown apart writes the selected line; the toolbar's `to` follows the same rule, and a lurk's toolbar field is a
  lurk area. The toolbar toggle derives it from the verb as above; a location control's pick button
  aims at its own field through `StratStepSelection.PickOnMap`, which selects the step and line first and cancels
  when pressed again. `StratCanvasViewModel.ArmedField` says which field is armed; the toolbar then names it
  (`Set “from” on map`, `Set B's watching on map`).
- **Hits and misses:** a click inside a place stores the place and the point; a click in no place stores the point
  alone and drops the stored place, which named another spot. On watching, via and lurk areas, a click adds the place, or the point
  when it is in no place, and a place already watched adds nothing. With no zones for the map (none baked, or the
  read failed) a click sets the point and keeps the stored place. The status line says which.
- **Cancel:** Esc (the mode counts as an active tool, so Esc resolves to cancel even under pan; in the armed field
  itself, Esc with its list closed), the toggle or the field's pick button again, a change of selected step or
  strat, or an edit after which the field no longer applies (a verb that drops it, a landing without utility). A
  toolbar-armed pick also ends when the toolbar would pick another field. A click whose place lookup is still in
  flight is dropped by any of them.
- **Places off the UI thread:** the map's zones load through the processing queue (`SectionCompute`, user
  priority) when the canvas first shows the map, one read in flight per map across canvases; a click before they
  land waits for that item.
- **Set on map on lines:** on a step with lines shown apart the toggle names the line (`Set C's “to” on map`) and the
  click writes that line's place, one entry, adding nothing else. On lines the row shows as one it says `Set “to” on
  map` and writes every line's place (`StratCanvasViewModel.LinesShownApart`, wired to the editor's
  `ShowsLinesApart`). A step for everyone shown split counts as lines: the click writes the selected player's place
  and keeps the other four. A lurk offers `Set “lurk area” on map`: the click adds the place to the lurk's areas.
- **View cones:** every live token on the strat canvas and the Detected preview draws a wedge along its yaw
  (`MarkerLayer.DrawViewCones`, from `ISceneFrameHost.ShowViewCones`): 30 degrees either side, 36 px past the disc,
  the team colour at alpha 56, behind the heading stub. Replays and exports draw none. On the editing canvas a press
  in the wedge is a turn (`TokenHitTest.Classify(..., cone: true)`); a press on a disc always wins over another
  token's wedge. Only the pan and token tools offer it, as for a token drag. Turning one of A to E writes that slot's
  line `watch.yawDegrees` on the selected step when its verb watches (push, hold, peek, fake, lurk and `other`) or the
  line already does, adding the line (a step for everyone is read as a line per slot first, so the others stay in it).
  On a verb without watching (move, rotate, throw, plant, defuse, wait, call), and paused mid-run, the turn is refused
  with "a runner faces its run; turn it on the hold or push after". The drag shows the turned yaw, and one entry is
  written on release. Turning an opponent token writes its position's yaw as before, which its `placed` chip shows.
  The line's angle button clears it.
- **Motion from destinations:** a step's `to` (the step's, a line's or the compact who's) and a lurk's areas, in
  order, move the tokens it names. Move, push, rotate and other run there from the step's time at 215 u/s; hold, peek,
  fake, plant and defuse are there at the step's time; throw, wait and call do not move. Tokens at one place at the
  same time fan out inside it, each slot on its own spot, whichever steps sent them. A runner faces its run and
  turns to what it watches on arrival. An authored position on that step (an Alt-drag pin, or an old drag) wins over
  the destination and shows as a `placed` chip; setting a `to` drops the slot's carried entry in the same undo entry. docs/strat-format.md, "Motion on the canvas", has the rules.
- **Token drag:** a left press on a token drags it under the pan tool as well as the token tool
  (`InputToolRouter` offers the press to the token tool first and pans when it refuses; with no token editor, as
  on the 2D tab, it always refuses). The pen, eraser and shape tools keep their own press. A drag is a Set on map pick
  on the field the row shows, with the drop as the click (docs/strat-book/drag-semantics.md, option A;
  `StratDragTarget.Resolve`, `StratDragPatches.Ops`): move, push, rotate and `other` take the slot's `to`; hold,
  peek, fake, plant and defuse their `at` or `site`, and the slot's own position entry on the step goes with it; a lurk
  takes the drop as lurk area 1, or `rotate to` once its rotate has started at the playhead; throw, wait and call edit
  the step that placed the player; a player the step does not name joins it as a line; a lineup thrower is refused.
  Opponents still write their position entry. One undo entry, through the field writers; docs/strat-format.md, "What a
  token drag writes", has the table.
  - **No seek.** A press while playing pauses where it is. Paused on the selected step's tick, the drag aims at the
    step that wins that tick for the slot (the later one naming it). Paused between steps, a token mid-run gets a via
    on its run, inserted before the via it was heading for; a standing token edits the step that put it there. The
    step written becomes the selection only when it owns the playhead's tick, since selecting another would move the
    playhead; the label and the status line name it (`step 2 · A · to: Hut`).
  - **Snaps and keys.** Within 12 screen pixels of a place's arrival the drop stores the place alone, so tokens sent
    there fan out; elsewhere in a place the place and the point; outside every place the point. Shift stores the
    point alone. Alt pins: `positions[slot]` on the selected step, shown as a `placed` chip. A `Pin` toggle in the
    canvas toolbar does what Alt does for the next drag, for window managers that take Alt+drag (GNOME, KDE). Esc
    cancels and writes nothing. Shift no longer snaps a facing: the cone drag on a watching verb is the one way to set
    one.
  - **While dragging** (`SceneGuides`, drawn by `GuideLayer`, which only a host with a token editor mounts): a hollow
    dashed ring where the token stood; a dashed ghost route through what the release would store, from a projection
    of the ops applied to a copy, so it bends through a place's arrival, not the pointer, and follows the routes when
    routing is on; the place under the pointer outlined dashed; the token under the pointer. A label beside the
    pointer (`StratCanvasView`'s `DragLabel`, a `CardBg` plate with a `Pb2dCanvasDropTarget` border, `TextValue`
    over `TextMid`) reads `E · lurk area 1: Long Doors` with `Shift: point only   Alt: pin E here   Esc: cancel`
    under it, and the row field the drop writes is lit through `ArmedField`, the highlight Set on map gives it: an
    armed `PlaceField` outlines its box in `AccentInteractive` and bolds its pick glyph. A compact row's field stands
    for every line, so the highlight names the field the row shows even when the write goes to one line.
  - **A seen position** on a travel or lurk step goes with the drop, in the same undo entry; the status line ends
    "replaced the seen position". Add step, Duplicate and Delete step cancel an open drag.
  - **After the release** the token is drawn where it stands at the playhead, and the selected step's destination
    pins show where it sends each token it moves: a hollow ring in the side colour at the arrival and a solid route
    there in the ghost colour, with a small `Pb2dCanvasDropTarget` diamond at each via. Pins show while paused, until
    the token arrives; dragging a pin is the same edit as dragging its token. The status line says what changed:
    `E's lurk area 1 is now Long Doors (was Middle). Ctrl+Z undoes`. When the playhead is past the step's run for that
    token (a lurker at its last area), a faint ring at the run's arrival (the pin colour at 40%, no route) marks where the
    drop went, until the playhead moves.
  - **Tokens:** `Pb2dCanvasRouteGhostT` (`#B3E0A030` dark, `#B3A66A15` light) and `Pb2dCanvasRouteGhostCt`
    (`#B34A90D9`, `#B3285F9E`), the side colours at about 70%; `Pb2dCanvasDropTarget` (`#A99CF0` dark, `#5B4BC4`
    light, `#00E5FF` high contrast; high contrast also sets the ghost routes to its team colours at 90%), a violet light enough to read over the radar, where `AccentInteractive` is too
    dark. `ScenePalette.Dark` and `Light` carry the same values for headless renders.
- **Utility in flight:** the strat canvas and the Detected preview turn trails on
  (`StratCanvasViewModel.ShowTrails`), and an export names the trail layer (`StratExportJob.LayerIds`). A throw draws as a demo grenade does, through `TrailLayer` and
  `AreaEffectLayer`: the flight line and head dot in the thrower's side colour (`TeamT` / `TeamCt`), fading 2 s
  after it stops; then a smoke disc that blooms over 1 s, the fire cells, or a pop. A flash pops in `TrailFlash`,
  an HE bursts in `TrailHe`, both growing and fading over about half a second; a decoy is a `TrailDecoy` ring of at
  least 5 px. No new colour tokens. docs/strat-format.md, "Utility on the canvas", has the timings.

### Placed chips (the Strats editor's step row)
- **Files:** `ViewModels/StratBook/StratPlacedChips.cs` (`StratPlacedChip.For`, `StratPlacedEntry`), `StratStepRow.Placed`,
  the `placed` strip in `StratBookTabView.axaml`, `StratDragPatches.Convert` and `Clear`, `StratDepartureCheck`.
- **What:** a step's `positions[]` entries, which beat or stand in for its fields, so nothing moves a token unseen. A
  `placed` label over a `WrapPanel` of chips (max 280 px, so it wraps inside the 315 px editor that
  `StratEditorRoomTests` guards), above `note`, shown only when it has a chip.
- **One chip per entry** that overrides a field the row shows: `E leaves from (1374, 412)` for an authored entry on a
  travel step that names the slot (border `AccentCaution`: it re-times the leg before, and its tooltip carries the zip
  warning when there is one); `A pinned at (50, 50)` beside a position verb's non-empty place; `E seen at (x, y)` for a
  captured spot that disagrees with the step's place (a point elsewhere, or a place the map's zones say it is not in).
- **Grouped**, one chip per kind: `spots (n)` (authored spots with no field beside them), `seen (n)` and `opponents (n)`
  (an older file's round-start entries are its start and show in the Start row instead), whose flyout lists each entry with its own "make it …" and ✕ (`O2 at (2060, 0)
  135°`, an opponent's angle on its entry). A group of one reads as its entry. Carried entries never show.
- **Actions:** clicking a chip selects its step and player at the step's time; its ✕, or Delete or Backspace while it
  has focus, clears it (a group's ✕ clears every entry it lists); the context menu has `Make it the to` (`the at`,
  `the site`, `lurk area 1`) and `Clear`. Make it moves the point into the field the drag table names, with the place
  under it when the map's zones are loaded, and removes the entry. Each is one undo entry. Old files are shown as they
  are; nothing is rewritten on open.
- **Look:** `Border.placedChip` (`PanelHeaderHover` fill, `BorderSubtle` border, 3 px corners, 11 px `TextValue` text,
  a `TextMid` ✕), `.caution` swaps the border for `AccentCaution`. The zip warning also lands in the row's issue text.

### Strat routes on the canvas (the Strats canvas, the Detected preview, a strat export)
- **Files:** `AddRoutes` in `Playback2D.Pipeline/Frames/StratFrameSource.cs`, `DrawRoute` in
  `Playback2D.Core/Layers/MarkerLayer.cs`, `Playback2D.Core/TokenRouteLine.cs`, `ScenePalette.RouteT`/`RouteCt`.
- **What:** while a token moves, a line from where it is through each corner of the move to where it stops: its way
  ahead, so a viewer reads which way round a wall it goes. Gone on arrival, at a hold, and before a run starts (a
  step's hold). Only when the tracks are routed (`stratbook.routing` on and the map's nav in memory); off draws
  nothing new, and a demo frame never carries routes.
- **Look:** 3 px, round caps and joins, under the discs in the marker layer (no new layer id, so the scene stack, the
  goldens and `SceneLayerListParityTests` are unchanged). Colour is the side's route token at about 35% alpha:
  `Pb2dCanvasRouteT` (`#59E0A030` dark, `#66C9821C` light) and `Pb2dCanvasRouteCt` (`#594A90D9`, `#662F73BE`); a
  palette built without them falls back to the team fill at 35%. A leg is drawn on the pane of either end, as a
  grenade trail is, so a route down a ramp reads as one line across nuke's panes.
- **Export:** drawn too. The export is a teaching clip of the same projection, and the line answers the question a
  viewer has when a token disappears round a corner; it is faint, shows only while a token moves, and the last frames
  (everyone arrived) are clean. The spec flag (`StratSceneSpec.Routes`) is the one switch: the canvas and the export
  set it from the projection, so they cannot disagree.

### Review mode (2D Playback)
- **Files:** `Views/Playback2D/Playback2DView.axaml` (+ `.cs`, the right column's rows and the panel host),
  `Extensions/StratBook/Modules/RoundTagger/Review/ReviewPanelsPlaybackContribution.cs` (the panels and their
  behaviour), `Extensions/StratBook/Views/RoundTagger/ReviewPanelView.axaml` (toggle, editor, Labels list),
  `Views/RoundTagger/TagEditorView.axaml`, `Modules/RoundTagger/Review/ReviewPanelViewModel.cs`,
  `Modules/RoundTagger/Review/TagEditorViewModel.cs`, `Views/Playback2D/TimelineControl.axaml` (the lane's edit band).
- **Purpose:** labelling and suggestion review are a mode, off by default (the `Review` toolbar toggle, Shift+R,
  persisted as `Playback2D.ReviewMode`). Off: full player cards, no palette, queue or tag lanes, and C, Y, N, Enter
  and Ctrl+Y fall through. On: the cards collapse to a one-line strip (name, HP, money) and the contributed panels
  take the rest of the column: palette, a Suggested / Labels toggle, one editor, the queue or the Labels list.
- **Ownership:** the three panels are the Strat Book extension's right-column contributions
  (`IPlaybackSurface.AddPanel`), not the tab's: the tab hosts `Surface.Panels` in its third row and knows no panel
  by name. The toggle is offered only while a contributed panel's gate is on, so with the extension off the
  column is the game info and the cards alone. Variants `playback2d-review-panels` and
  `playback2d-review-panels-pack-off` (1280x900) render both.
- **Contract:** one `TagEditorViewModel` edits both a suggestion (Save accepts it with the edit) and a written tag
  (Save replaces it, Delete removes it through `TagDelta`, so both undo). Start and end are seconds from the round
  start and clamp to the round like the palette does. With an editor open, a map click adds a position and the lane
  draws the span with two handles; dragging a handle moves the draft and never seeks. Keys typed into the editor
  stay in it; Enter saves, Esc cancels.
- **Layout note:** the strip row is sized from the player count (22 px each plus 12, at most 10 rows) because a
  virtualizing `ListBox` in an `Auto` row measures to 0.
- **Do not:** show a tagging panel or lane outside the mode, or hide the lanes by writing the user's per-track toggle;
  use `SetTrackSuppressed`.
- **Generated content:** the Suggested tab lists new proposals only; accepted and dismissed ones come back under
  "Show settled (n)", where a dismissed one has Restore. N is Dismiss. The Labels tab lists hand-made labels
  under "Yours (n)" and machine-written ones (accepted suggestions, mined strat runs) under "From suggestions
  (n)", each saying which.

### Suggested section (the Strat Book rail)
- **Files:** `ViewModels/SuggestedTags/SuggestedInboxViewModel.cs`, `Views/SuggestedTags/SuggestedInboxView.axaml`,
  `Modules/SuggestedTags/SuggestedInboxService.cs` and `SuggestedInboxModule.cs` (tab `suggested.inbox`, feature
  `tab.suggested`, Order 6).
- **Purpose:** every demo's Suggested Tags proposals in one inbox, so they are not reachable only one open demo
  at a time. Filters: map, side, detector, minimum confidence; "Show settled (n)".
- **Contract:** a row reads code@site, confidence, detector (and state when settled), then map, demo, round and
  side, then why it fired. Open (2D Playback at the proposal, `ISituationPlayback`), Accept and Dismiss on a new
  row; Restore on a dismissed one. Accept goes through `SuggestedTagsService.Accept`, the Review-mode path. The
  first look reads the library as one `SuggestionsInbox` queue item; the badge is the index's pending count.
  The list is virtualized under its own ScrollViewer.
- **Do not:** accept more than one suggestion per click here; a bulk accept waits for preview before commit.

### Detected inbox (Strat Mining, the Strats section)
- **Files:** `ViewModels/StratBook/DetectedStratsViewModel.cs`, the Book / Detected toggle, the `DetectedList` and
  the `DetectedDetail` pane in `Views/StratBook/StratBookTabView.axaml`; the data is `Services/Strats/Mining/`.
- **Purpose:** repeated setups and executes the miner found in the library, offered as strats the user adds by
  hand. The toggle sits where the list's "Strats" label was and counts the patterns not yet in a book or
  dismissed. The list takes the tab's map, side and book filters; utility-compared patterns sort first, and a
  positions-only one says so in its summary line.
- **Contract:** the detail pane takes the editor's column while the inbox is up and gives back the editor on
  Add to book, which opens the new strat. Only new patterns show: dismissed and in-book ones come back under
  "Show settled (n)", the one generated-content toggle (`docs/strat-book/generated-content.md`). Dismiss is
  permanent across re-mines until Restore. Deleting a mined strat makes its pattern new again. A refused
  `strat-mining.json` is named above the list, and Add to book and Dismiss say why they did not save. A member round opens in 2D Playback through `ISituationPlayback`, the Utility map's seam.
- **Preview:** "Preview" on a pattern not yet in a book swaps the detail pane for `DetectedPreview` and the right
  column for `DetectedPreviewCanvas`: the strat Add to book would save (same `StratMiningService.Build`),
  metadata, slots and steps as plain text, and the canvas playing it with the transport, path picker, Fit and
  scrubber only (`StratCanvasViewModel` with `readOnly: true`; tools, Place tokens and the step buttons are
  hidden). The playing step reads "now" and turns semibold. The build is a `StratPreview` queue item; the pane
  says "Building…" meanwhile and says so in words when the medoid's cached files are gone. Add to book, Dismiss,
  Back to detected and each round's Open sit on the preview. Add to book from a preview saves the previewed
  document itself; if a re-mine changed the pattern's rounds or medoid since, it rebuilds the preview under
  "This pattern changed; review again." instead of saving. Another pattern, Back, or leaving Detected discards
  it.
- **Do not:** save a pattern into a book without the user's click, or show a pattern's win rate as a strat's
  record before it is added; until then the numbers are the rounds', not runs of a strat. Do not give the preview
  a text box, combo box or edit tool: it must not read as editable, and it runs on a throwaway in-memory store.

### KeyValueTable
- **File:** `Controls/KeyValueTable.axaml` (+ `.axaml.cs`). **Bindable props on `Root`:** `Rows`
  (`IReadOnlyList<KvpRow>`), `ShowDeltaOnly` (filters to changed rows → `VisibleRows`).
- **Purpose:** generic two-column key/value grid. Delta rows render `prev → curr` (strikethrough prev)
  with a tinted key. Virtualized via the default `ListBox` `VirtualizingStackPanel`.
- **`KvpRow`:** `Key`, `Value`, `PreviousValue`, `IsDelta`.
- **Used in:** ~8 sites (entity fields, watched values, diagnostics tables, etc.).

### NavStrip
- **File:** `Controls/NavStrip.axaml` (+ `.axaml.cs`). **DataContext:** `MainViewModel` (shell).
- **Purpose:** the single shell navigation surface (navigation-review **Option 1**, Phase C),
  rendered **once** as a docked row in `MainView`. Three groups in a **responsive `DockPanel`** (P3.1,
  see [Responsive layout](#navstrip-responsive) below):
  1. **CLOCK**: `◀` frame · editable `frame N / MAX · tick T` readout pill · `▶` · play/pause
     ToggleButton · speed ComboBox. Movement is **frame-index based**; tick is a read-only label
     (locked decision).
  2. **SEEK (`EVENT`)**: a single segmented event stepper `◀ <target chip ▾> ▶`
     (`NavPrev/NextEventCommand`); the chip is the merged target selector (presets + demo-derived
     `GameEventFilters` checklist via `EventFilterFlyout`). Replaced the old 6-button JUMP group + `⚙▾`
     flyout (the SEEK/EVENT consolidation, see [Consolidated SEEK/EVENT nav](#navstrip-redesign)).
     Tick/frame nav lives on the CLOCK `◀ ▶`; round nav = the chip's `Round` preset.
  3. **BREAKPOINT (`TO BREAKPOINT`)**: `▶▶` continue / `▶|` step-tick / `▶||` step-round, amber
     (`bp-btn`), **distinct** commands + `HasFile`/`CanDebugStep` gates. Kept visually separate on
     purpose (see [breakpoint coherence](#3-the-three-breakpoint-surfaces)).
- **Style classes (PROMOTED to the design system in P1.3):** `Button.nav-btn`, `Button.bp-btn`,
  `Button.ctx-action` now live in `Primitives.axaml`; `TextBlock.group-label` in `Chrome.axaml`. NavStrip
  keeps only `CheckBox.event-filter` locally (the sole CheckBox styling; CheckBox is not a P1.3
  primitive). Render verified byte-identical (default state) after the move (`navstrip-real`, nonBg
  2871); the moved `:pointerover` setters are still added after FluentTheme, so hover state is unchanged
  too (not separately captured).
- **Gating:** whole strip `IsVisible="{Binding HasFile}"`. The BREAKPOINT sub-group is a **dev/power**
  concern (see visibility matrix), gated as a unit by `IsBreakpointNavEnabled` (`chrome.breakpointNav`,
  P1.2 chunk B-ii). The responsive right-dock (P3.1) and the gate **compose**: an invisible right-docked
  child reserves no `DockPanel` space, so gating the cluster off simply reflows the JUMP fill to full
  width (no dangling gap).
- <a id="navstrip-responsive"></a>**Responsive layout (P3.1):** the outer
  container is a **`DockPanel`**, not a non-wrapping `StackPanel`. **CLOCK** is pinned `Dock="Left"`
  (playback always reachable); the amber **TO-BREAKPOINT** cluster is pinned `Dock="Right"` so it can
  **never clip**: the P0.3 defect (previously the trailing amber buttons clipped off the right edge,
  verified 880/1050/1300). **JUMP** fills the middle inside a horizontal `ScrollViewer`
  (`HorizontalScrollBarVisibility="Auto"`) so the semantic-nav buttons **scroll rather than clip** when
  the strip is genuinely too narrow. Everything fits with no scrollbar at ≥~940px; below that only the
  trailing `⚙▾` event-filter button scrolls (the six primary JUMP buttons stay clickable). The `Dock="Top"`
  row auto-sizes, so at narrow widths the row grows for the scrollbar rather than the bar overlapping the
  buttons. Commands/readouts/bindings/`x:Name`s are **byte-identical** to the pre-P3.1 strip (only the
  container reflows; `navstrip-real` still renders nonBg 2871 at the fitting 1280px width). Verified via
  `navstrip-real` at 880/1050/1300/1600 (§7).
- <a id="navstrip-redesign"></a>**Consolidated SEEK/EVENT nav (shipped 2026-07-16).**
  **Concept B (segmented pill)** won, with one change: **drop the tick stepper**: the CLOCK
  frame-stepper (`◀ ▶`) already covers tick/frame nav. So the old six-button JUMP group **+ the hidden
  `⚙▾` event-filter flyout** are replaced in `NavStrip.axaml` by **one segmented event stepper**:
  `◀ (NavPrevEventCommand)` · **target chip `▾`** · `▶ (NavNextEventCommand)`, labeled **`EVENT`**.
  Pass 1 (flag/ring/ruler `PathIcon`s) was rejected; that model change (not a letters→icons swap) is what
  shipped. **Net: 7 JUMP controls → 3**, and the thing "next event" seeks is always on-screen.
  - **Interaction model (production).** Tick/frame nav = CLOCK `◀ ▶` (`PreviousFrame`/`NextFrame`, ±1 in
    the raw `Frames` list). Event nav = the EVENT stepper's `◀ ▶` (`NavPrev/NextEventCommand` →
    `SelectedSpecialFilter()` over the demo-derived `GameEventFilters`). The **target chip** shows
    `EventFilterFlyout.TargetSummary` (live: `Any event` / `Round` / `<event name>` / `N events`) and, on
    click, opens the merged dropdown: **PRESETS** (`Any event`, `Round`, `Kills`, `Bomb`) then **EVENT
    TYPES** (`Select all`/`Deselect all` + the per-event `CheckBox` checklist). The chip **subsumes** the
    `⚙▾` flyout; the **`Round` preset** (`round_*` union) **subsumes** the removed `NavPrev/NextRound`
    (which keyed off the identical `StartsWith("round_")` set, reproduced exactly). `NavPrev/NextTick`
    and `NavPrev/NextRound` RelayCommands **remain on the VM** (API intact), just no longer bound to strip
    buttons. Losing the distinct-tick *skip* from the strip is an accepted trade (CLOCK reaches every tick,
    finer).
  - **Presets = named selections over `GameEventFilters`** (`EventFilterFlyoutViewModel.Preset*Command`):
    `Any event` clears all → navigator matches any; `Round` = `round_*`; `Kills` = `player_death`;
    `Bomb` = `bomb_*`. Nothing enabled falls through to match-any. **CS2 GOTV round lifecycle is
    `round_freeze_end`/`round_officially_ended`, NOT `round_start`**: the named preset is the discoverable
    way to reach rounds without knowing the exact event name.
  - **Styling (Concept B).** The stepper is a `Border` (CardBg / BorderAccent, radius 6) with hairline
    `BorderSubtle` dividers; chevrons are `Button.nav-btn`; the chip is `Button.ghost` (`TextBright`
    summary + `TextDim ▾`), `MaxWidth=128 + TextTrimming` so a long CS2 name can't blow out the strip.
    The dropdown reuses `Border.card-flyout` / `.ctx-action` / `.col-label` / `CheckBox.event-filter`
    (no new tokens or classes). CLOCK + amber TO-BREAKPOINT + the `IsBreakpointNavEnabled` gate + the P3.1
    responsive DockPanel are **unchanged** (only the JUMP fill swapped to SEEK/EVENT).
  - **Narrow behavior: improved by the consolidation.** The single event stepper (~200px) is far
    narrower than the old 6-button JUMP (~280px), so at **820px the whole EVENT stepper fits with room to
    spare EVEN with the dev breakpoint cluster on**: nothing scrolls (verified `navstrip-real` +
    `navstrip-real-target` at 820, default `Any event` and a picked `player_death`). CLOCK + TO-BREAKPOINT
    stay docked/never-clip as before. The pass-2 tick+event tight-corner is gone (tick stepper dropped).
    The fill still lives in a horizontal `ScrollViewer` as the ultimate narrow fallback.
  - <a id="navstrip-icon-approach"></a>**[HISTORICAL: pass 1 + pass 2, superseded by the shipped model
    above].** Pass 1 rendered three icon-only options (`navstrip-redesign-a|b|c`, event=flag / round=ring /
    tick=ruler `PathIcon`s), **rejected** ("icons just not good"). Pass 2 rendered two consolidated
    concepts that still carried a tick stepper (`navstrip-v2-a|b`, `-*-long`, `navstrip-v2-compare`),
    superseded when the tick stepper was dropped. **Those `navstrip-v2-*` mock variants were RETIRED
    from `UiCapture/Variants.cs`** (production `navstrip-real` now renders the shipped strip). The pass-1
    `navstrip-redesign-*` / `navstrip-icon-probe` mocks remain as history. **Dependency note (still valid):**
    inline `PathIcon` + `Geometry` path-data is the WASM-safe, dependency-free way to add icons if a future
    need arises; a permissive MIT Avalonia icon pack (e.g. `Projektanker.Icons.Avalonia`) is only worth the
    dep policy if a repo-wide icon system is adopted, not for the NavStrip, which ships **text-forward**
    (chevrons + the `TargetSummary` word) by deliberate, self-documenting preference.
  - <a id="game-icon-scope"></a>**A repo-wide icon system was NOT adopted, and the CS2 icon set is not
    one.** `docs/ui/game-icons.md` describes a baked set of CS2's own artwork (weapons, kill modifiers,
    round objects, sides, ranks, maps) reached through a `GameIcon` control. Its scope is **game
    vocabulary only** — things Valve drew because the game means them. It does not reopen the decision
    above: the NavStrip and the command palette stay text-forward, and stat column headers stay words.
    Every adopted site keeps a text `Fallback`, which is the same preference expressed one layer down —
    the text is the substrate, the icon is the enhancement.

<a id="timelinecontrol"></a>
### TimelineControl: the 2D playback timeline (Playback2D v2, phase A1)
- **Files:** `Views/Playback2D/TimelineControl.axaml(.cs)`. **DataContext:**
  `Modules/Playback2D/Timeline/Playback2DTimelineViewModel`. Docked as an `Auto` row under the viewport
  in `Playback2DView.axaml`'s left cell; the viewport keeps the `*` row.
- **Palette rule:** tokens come exclusively from the walled-off `Pb2d*` HUD ramp (see §1's Playback2D
  palette), never the app-chrome ramp. This is HUD furniture over the 2D canvas, and the two ramps
  must not be mixed inside the viewport column.
- **Three rows.**
  1. **Rounds band (18 px).** One `Border` per `TimelineBand`, labelled with the round number (`wu` for
     the pre-first-freeze-end warmup band). Won-by tint comes from `round_end`'s winner; a demo without
     `round_end` renders neutral. Clicking a band seeks to its FIRST frame, not to the pixel under the
     cursor.
     - **A right press is the band's menu, and it never seeks.** The control builds a `ContextMenu` from
       `Playback2DTimelineViewModel.MenuFor(band)`, which concatenates the `BandMenus` contributors in
       order: the tab's own lane menu (edit, delete, review on a lane band, Review mode only), then
       whatever the extension packs attached through `IPlaybackSurface.AddBandMenu` (Create Strat From
       Round on a round band, while the Strat Book pack is on). No entries, no menu. The control holds no
       entry text of its own; a new entry is a contributor, never a `MenuItem` in the code-behind.
  2. **Scrub bar (22 px).** A track rule, one glyph per `TimelineMarker` (`×` kill · `◆` plant ·
     `✂` defuse · `✸` explode), and the playhead. Press seeks; press-and-drag scrubs continuously.
     A **kill glyph is coloured by the side that got the kill**. See the marker-colour rule below.
  3. **Footer (26 px).** The current round label, `frame N / M · tick T`, the hover readout, the follow
     status, the speed-lock note, the `Status` readout **moved here from the floating bottom-left
     overlay**, and one `CheckBox` per available track.
     - **26 px, not 18 (changed in D5).** A stock Fluent `CheckBox` cannot be made to fit an 18 px row:
       its template hangs the 20×20 check box inside a `Grid` with a hard-coded `Height="32"`,
       top-aligned, which pins the box to **y = 6..26** of that band. A template part's `Height` is a
       *local* value, so it outranks a style setter and `MinHeight="0"` on the `CheckBox` cannot compress
       it. Measured, the box hung 8 px below the row, through the panel's own bottom padding and out of
       the control, where the window edge cut it off ("the toggles are partially hidden", D5 item 5.2).
       26 px is the box's exact extent. `Padding="4,6,0,0"` on the `CheckBox` re-centres the LABEL on the
       box: `Padding` is the Fluent template's content *margin*, and the box is centred on that 32 px
       band rather than on the row.
     - **The footer is a `DockPanel`, and the toggles are `Dock="Right"` and declared FIRST.** It was a
       `ColumnDefinitions="Auto,Auto,Auto,Auto,Auto,*,Auto"` grid with the toggles in the trailing `Auto`.
       `Auto` columns are measured unconstrained and never shrink, so during playback (six-digit frame
       and tick, a follow target, the Live-Sync speed note, five of the six readouts untrimmed
       monospace), the `*` status column collapsed to zero and the toggles were arranged **past the
       control's right edge**: measured 99 px past it at 1000 px and 279 px at 820 px, where
       `InputHitTest` handed every toggle's centre to the roster panel. Nothing clips, so it never
       showed as a cut. The `GridSplitter` and the roster panel are later siblings of `Playback2DView`'s
       root grid and take both the paint and the clicks, the identical failure mode as the
       [HUD corners](#playback2d-hud-corners). `DockPanel` measures each docked child against the space
       still unclaimed and the first child claims first, so declaring the toggles first makes their
       reservation **structural**; every readout then docks `Left` in priority order and the same
       remaining-space measure becomes their drop order, each taking its natural width if there is room
       and ellipsizing (`TextTrimming="CharacterEllipsis"` on all of them now, not just `Status`) into
       what is left if there is not. This is the shape D35's
       "[responsive horizontal strips MUST wrap or scroll](#responsive-strip)" prescribes and P3.1 already
       applied to the NavStrip. **Reordering the `ItemsControl` below the readouts silently restores the
       bug.**
- **Marker colour is the TRACK's to decide, and a track may not reach for a brush.**
  `TimelineMarker.Argb == 0` means "host, use the kind default", and `BrushForMarker` honours any non-zero
  value, so a track hands back ARGB and the `Timeline/` folder stays renderer-independent.
  `KillTrack` uses it to colour each `×` by **the attacker's side** (`TintTeamT` `0xFFE0A030` /
  `TintTeamCt` `0xFF4A90D9`, the `Pb2dTeamT` / `Pb2dTeamCt` hues), so a run of one colour reads as a side
  winning fights. **Full alpha, unlike `RoundTrack.ApplyWinnerTints`' `0x38` washes**: a wash reads as a
  side across a 300 px band and as *nothing drawn* on an eight-pixel glyph.
  - **The side comes from `TimelineEventKeys.Team`** (`"2"` = T, `"3"` = CT, the encoding `Winner`
    already uses), which `ModuleTimelineData` populates for any event naming an attacker. Team is per-tick
    state, not identity (it is deliberately absent from `PlayerRosterEntry` because it swaps at half), so
    the adapter resolves it **at the event's own tick** from the `player_team` timeline, the same record
    the parser's team post-pass is fed by. GOTV emits `player_team` only for the halftime swap, so a kill
    before the swap reads the swap's `OldTeam`.
  - **An unresolvable side leaves the key absent ⇒ `Argb = 0` ⇒ today's `Pb2dHeadshot` red.** No kill
    ever loses its marker over a missing team.
- **Layout model.** The item layers are plain `Panel`s and every band/marker positions itself with a left
  `Margin` from its own view-model. No attached property is set on a generated container, which is what
  keeps the templates free of `ContentPresenter` styling. The x-axis domain is **frame index**;
  tick-stamped events are converted once at build time via `IModuleContext.FrameIndexAtTick`.
- **Brushes are immutable.** `TimelineMarkerViewModel.Brush` / `TimelineBandViewModel.Brush` are
  `ImmutableSolidColorBrush` resolved through `ThemeColors.Get`. A `SolidColorBrush` is an
  `AvaloniaObject` whose constructor asserts UI-thread affinity, which would make the pure layout math
  untestable.
- **Density.** Two markers of one track landing within 2 px fold into a single visual whose tooltip
  carries the count (`3 kills`), so a 90 k-frame demo does not realize hundreds of glyphs.
- **Gating.** `playback2d.timeline` (`SubFeature`, parent `tab.playback2d`). The view-model folds the gate
  AND has-demo into `IsVisible`; because the row is `Auto`-sized, an off gate leaves no layout hole.
- **Focus.** `Focusable="False"`; the control must never steal the keymap's focus target.
- **Pinned by** `TimelineFooterLayoutTests`: every visible track toggle's *painted* extent inside the
  control's bounds and reachable via `InputHitTest` at 1400/1000/820 px, with every readout populated
  (blank readouts fit by eight pixels, so a test that leaves them blank proves nothing). Geometry is the
  assertion; a container-shape test passed on the broken tree, because the toggles were in the right
  container the whole time. Marker colour is pinned by `TimelineTrackTests` (ARGB, no host) and
  `TimelineLayoutTests` (the resolved `Brush`, including the fall-back-to-red case).

<a id="playback2d-hud-corners"></a>
<a id="playback2d-viewport-chrome"></a>
### The 2D viewport's chrome: what is docked, what floats, who owns which pixels (Playback2D v2, A4/B2/B3/B4, **restructured by D4**)

> **Superseded (D4, 2026-08-25).** Until D4 this section allocated four *corners* of one canvas cell,
> because every toolbar floated over the map. The corner allocation is gone: the persistent chrome is
> **docked in its own `Auto` grid row** and the canvas cell now holds three small, mode-scoped widgets and
> nothing else. The rules the corner contract existed to enforce did not go away (they moved down a
> level, from "which corner" to "which docked line"), so they are restated below rather than deleted.

**The left column is three rows: `RowDefinitions="Auto,*,Auto"`.**

```
┌ row 0 · ViewportToolbar · DOCKED (Pb2dPanelBg, Pb2dHudDivider hairline on the docked edge) ────┐
│ ▴  ································································  [ Overlays ▾ ][ Export… ] │  ChromeHeader   · always
│ ☑ Radar  ☑ Trails  ☑ Smoke/Fire  ☑ Bomb  ☑ Kills  ☐ Vision                                    │  OverlayToggles · IsOverlayBarOpen
│ ✋ ✎ ⌫ │ ▣ ▣ R⌫ ◼◼ w ──── α ── │ [Always ▾] Pin to now ☐ Track player │ ↶ ↷ Clear             │  AnnotationToolbarHost · gated
│ in ▢ out ▢ from ▢ until ▢ ⌖now          ·  status line                                        │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
┌ row 1 · the canvas ───────────────────────────────────────────────────────────────────────────┐
│ ▾ ChromeRestoreButton (only while collapsed)                       ● live-sync · kill feed ▸  │
│                                                                                    LevelStrip │
│ TransportBar ▸                                                                                │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
┌ row 2 · TimelineControl · DOCKED ─────────────────────────────────────────────────────────────┐
```

| Region | Owner | Row | Interactive? |
|---|---|---|---|
| **Top edge** | `ViewportToolbar`: `ChromeHeader`, then `OverlayToggles`, then `AnnotationToolbarHost` | 0 (`Auto`) | yes |
| **Canvas top-left** | `ChromeRestoreButton`, 26×17, present **only** while the toolbar is collapsed | 1 | yes |
| **Canvas top-right** | `HudStack`: live-sync dot ([`Ellipse.pb2dDot`](#pb2d-hud-dot)) over the A4 kill feed | 1 | **no** (`IsHitTestVisible=False`) |
| **Canvas bottom-left** | `TransportBar`: camera-mode `SplitButton`, mode label, kill nav | 1 | yes |
| **Canvas right centre** | `LevelStrip` (B3), vertical margins clearing the kill feed and the transport bar | 1 | yes |
| **Canvas right edge, full height** | The side panes: the export pane and the contributed side pane (`Surface.SidePane`), 360 px, `Pb2dPanelBg` over a `Pb2dGridSplitter` hairline, a `Close` button in their footer. One shows at a time: opening either closes the other. | 1 | yes |
| **Bottom edge** | [`TimelineControl`](#timelinecontrol): its own `Auto` grid row | 2 | yes |

- **The contributed side pane is one host, not one `Border` per feature.** `Playback2DView.axaml` binds a
  single pane host to `Playback2DTabViewModel.Surface.SidePane` (the open pane's view model, or null) and
  its `Close` to `Surface.CloseSidePaneCommand`. An extension pack adds a pane through
  `IPlaybackSurface.AddPane(PanePlacement.Side, order, factory)` and opens it from its own entry point (a
  band-menu entry, a toolbar item); the view comes from the `ViewLocator` convention, so the pane's view
  model derives from `ViewModelBase` and has a `…View`. The Create Strat From Round review is the first
  such pane; it used to be a second hardcoded `Border` bound to a pack-typed property on the tab. The tab
  closes any open side pane on deactivation and on a demo reset, and the view model of a closed pane is
  disposed. Variant `playback2d-create-strat-pane` (1280x800) renders the host with that review open.

- **The right column's third row is one panel host, not one view per feature.** `Playback2DView.axaml` lists
  `Playback2DTabViewModel.Surface.Panels` in an `ItemsControl` under the player cards, visible in Review mode;
  each item is a contributed control (or the view model, for the `ViewLocator`) under a 6 px gutter, shown
  while its own gate is on. An extension pack adds a panel through `IPlaybackSurface.AddPanel(order,
  viewModel, view, featureId)`; several are open at once, in order, and a panel can hold the keyboard
  (`IPanelHandle.HasKeyboard`), under which the pack's key handlers run before the tab's keymap. The Strat
  Book's three (Tag Palette, review panel, Suggestion Queue) were three hardcoded views bound to pack-typed
  properties on the tab; the cards' strip row and the game-info row keep their place around the host.

- **Docking, not reflow, is the answer to "the toolbars are always displayed".** D35's responsive rule
  ([wrap or scroll](#responsive-strip)) is about a strip that is too WIDE; the reported defect was chrome
  that was permanently over the MAP. A strip that reflows beautifully still covers the thing the user opened
  the tab to look at. So the tool row and the overlay toggles left the canvas cell entirely and took an
  `Auto` row at the top edge, structurally the mirror of what `TimelineControl` already does at the bottom,
  which is also the existing proof that an `Auto` row leaves **no layout hole** when its content is removed.
  The precedent is **Blender's viewport header**: an edge-docked region, a collapse arrow, and popovers for
  the display toggles. Rejected: a Krita/GIMP-style left tool rail (a vertical rail costs the *width* the
  820 px floor has least of, and this tab has one tool group, not thirty), and Figma's floating tool island
  (that is the shape being complained about).
- **The docked stack is ordered ALWAYS-PRESENT → OPTIONAL → GATED, top down, and that order is the
  contract.** A member may only ever move what is *below* it, so neither the `playback2d.annotations` gate
  nor the overlay-bar toggle can shove the header's own controls around. This is the same rule the old
  corner contract stated for its top-left stack, and it exists for the same reason: B2 mounted the
  annotation toolbar in a corner A4's strip already owned and covered its whole tool row (485×40 px),
  leaving Pan/Draw/Erase unclickable.
- **The header's right-hand cluster is `Dock="Right"` and declared FIRST**, which is D5's timeline-footer
  reservation trick: a `DockPanel` measures each child against what is still unclaimed, so declaring first
  makes the reservation structural. `Export video…` lives there rather than floating bottom-right. It is a
  viewport-level command and that corner is canvas now.
- **The six overlay toggles ship CLOSED, behind one `Overlays ▾` toggle.** They are read rarely and changed
  rarely and were the widest thing in the column (485 px of it, permanently on screen), the textbook
  overflow candidate D35 names first. The overflow is **inline (a revealed row), not a flyout**: a flyout's
  content lives in a popup, which is outside the viewport column (so the layout suite could no longer
  measure or hit-test it) and outside the tab's tunnelling `KeyDown` handler (so `Space` over a focused
  check box would stop meaning play/pause). Inline keeps both guarantees and costs one docked line while
  open.
- **Collapsing removes the row; the way back is mounted BY the collapsed state.** `ViewportToolbar`'s
  `IsVisible` binds `IsViewportToolbarOpen`, so a collapse gives the whole docked height back to the canvas
  (measured: 172 px → 0 at a 1000 px window with the overlay bar open). `ChromeRestoreButton` binds the
  negation and floats in the canvas's top-left corner, in the same place the collapse chevron occupies when
  expanded. Because the restore affordance is created by the collapsed state rather than by the toolbar it
  restores, a persisted "collapsed" **cannot** be a state with no exit: the hazard
  `MainViewModel.RestoreSession` guards against for the shell's output drawer and debugger rail, where the
  toggle lives in chrome a gate can take away.
- **Persisted as two `Playback2DSettings` bools:** `ViewportToolbarOpen` (default `true`) and
  `ViewportOverlayBarOpen` (default `false`), each with its own `SettingsService.WriteInMemory` row,
  because the 2D tab is WASM-reachable and an unflattened key there is a setting that silently forgets
  itself.
- **A gated control's `IsVisible` belongs on the MOUNTED element**, not only inside the control. A control
  that collapses its own inner `Border` still contributes the mount's slot to the stack. (Both copies are
  kept for the annotation toolbar: the inner one is what a standalone mount reads.)
- **Chrome is `Focusable="False"`; the check boxes deliberately are NOT.** The keymap owns `Space` and the
  arrows through a tunnelling handler, and a focusable *container* in that path eats them. The six overlay
  check boxes stay focusable on purpose: `Playback2DKeyRoutingTests` focuses one and presses `Space`, and
  making them unfocusable would retire the hazard instead of proving the handler still covers it.
- **The docked toolbar wears the TIMELINE's chrome, not the HUD's.** `Pb2dPanelBg` with a `Pb2dHudDivider`
  hairline on the docked edge, matching `TimelineControl` exactly. `Pb2dOverlayBg` (the translucent HUD
  strip) is what *floating* furniture wears; a translucent strip inside an opaque docked one reads as a HUD
  widget somebody forgot to unmount, which is the "feels a little strange" quality being fixed. `Pb2d*`
  tokens only, per [D21](#pb2d-hud-dot).
- **Overlays still reflow rather than clip.** The viewport column is **496 px** at an 820 px window and
  nothing here clips, so a fixed row wider than that runs under the splitter and the roster panel (later
  siblings, so they take both the paint and the clicks). The annotation tool row and the overlay row are
  both `WrapPanel`s, per [D35](#library-toolbar-reflow), which is also why the tool row gets its **own**
  docked line instead of sharing the header's fill slot: at 820 px it would be left ~250 px and four wrapped
  rows, where a full-width line wraps to two.
- **Every gesture the toolbar names comes off the resolved keymap**, never a literal; see
  [`Playback2DKeymapProfile`](#playback2d-keybind-profile). `AnnotationsPanelViewModel` exposes
  `DrawToolTip` / `EraseToolTip` / `UndoToolTip` / `RedoToolTip` / `ClearAllToolTip`, and
  `Playback2DTabViewModel.ApplyKeymapOverrides` pushes the profile in. Pushed rather than pulled through a
  `$parent` ancestor cast because the toolbar's `DataContext` is the panel and it is also mounted
  standalone; the panel seeds itself with `Playback2DKeymapProfile.Default`, so an unpushed panel still
  shows the shipped gestures instead of blanks.
- **`ColorPicker` needs its own control theme.** `FluentTheme` does not carry it (it ships in the
  `Avalonia.Controls.ColorPicker` package), so both annotation ink pickers were **templateless** from B2
  until D4: 46×24 of nothing that painted no swatch and took no click. `App.axaml` now includes
  `avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml` beside the AvaloniaEdit include, which
  is there for the identical reason. The pickers' hard-coded `Width`/`Height` went with it: they were
  authored against a control that rendered nothing, and the real template's 32 px drop-down button hung 4 px
  out of a 24 px box top and bottom.
- **Pinned by** `Playback2DHudLayoutTests`: pairwise non-overlap of the sibling chrome regions, **every**
  interactive control in the docked toolbar contained in the column and `InputHitTest`-reachable at
  1400/1000/820 px, the collapse actually returning height to the surface, gate-flip stability of everything
  above the gated member, the settings round trip through the fileless (WASM) path, and gesture hints
  following a rebind. Geometry is the assertion, for the reason given under
  [`TimelineControl`](#timelinecontrol)'s footer.

<a id="playback2d-keybinds"></a>
### Playback2D keymap (`Modules/Playback2D/Playback2DKeymap.cs`)
Declarative action→gesture table, conflict-checked in its own static constructor against itself AND
against `MainView.axaml`'s shell accelerators (`Ctrl+P/O/W/B/,` and `Ctrl+1..9`); a duplicate throws at
first touch instead of silently shadowing a key. Bound on `Playback2DView` with a **tunneling** KeyDown
handler so transport keys beat whatever inside the playback surface has focus, and skipped while a text
input has focus. Every mutation routes through `PlaybackController` commands or capability-gated
`IModuleContext.Request*`, the surfaces LiveSync's `SyncStateObserver` observes.

**This table is the source of truth the conflict test reads.** `Playback2DKeybindConflictTests` parses
`MainView.axaml` at test time for the shell's accelerators rather than mirroring a list, and pins the
resolutions below individually, so a later edit that re-introduces a collision fails there, not in a
user's hands. It is also **the shipped default the user's overrides are composed over**. The table below
is what an untouched install routes, not what every install routes.

**Item 19 (command ids) moved every Strat Book extension row out of this file.** `Ctrl+F` and `J`/`K`
below are two of them: their chord, scope and description now live in `StratBookCommands`
(`Extensions/StratBook/StratBookCommands.cs`), read by `CommandRegistry` through `IFeaturePack.Commands`
(DI-free, so a bare-constructed view model resolves them with no composition root), not
in `Playback2DKeymap.BuildDefault()`. `IPackContributions.Commands(...)` is a separate, parallel
declaration the composition root cross-checks against `IFeaturePack.Commands` so the two cannot drift;
it is not what the keymap itself reads. `Playback2DAction` keeps every member (every existing switch over
it is unchanged), and `CommandRegistry` (`Extensions/CommandRegistry.cs`) composes the pack's rows back
over the core table with no DI, so `Playback2DKeymapProfile` resolves them exactly as before: a bare
`new Playback2DTabViewModel()` with no container still opens on the full table. The Tag Palette,
Suggested Tags, Review Mode and Step Authoring rows (`FocusTagPalette`/`TagNote`/`TagClearSticky`/
`TagLabelMode`/`TagLabelGroupNext`, the six-key Suggested Tags queue, `ToggleReviewMode`, and
`ToolToken`/`AddStep`/`DuplicateStep`/`DeleteStep`/`PrevStep`/`NextStep`) moved the same way; they were
never listed in this table and still are not, see tag-store.md, suggested-tags.md and step-authoring.md
§3.7 for those. The keybind settings list groups a pack's rows under its label and hides them while the
Strat Book extension's master switch is off.

<a id="playback2d-keybind-profile"></a>
#### The gestures are configurable (D1): `Playback2DKeymapProfile`
The table above stays static, stays conflict-checked, and **still throws**: it is a compile-time contract
and a collision in it is a bug. What a running tab actually routes is a `Playback2DKeymapProfile`: that
table with `Playback2DSettings.KeybindOverrides` composed over it. **The two types exist separately
because only one of them may throw.** A `TypeInitializationException` raised from a hand-editable JSON
file would take the 2D tab down with no way to fix it from inside the app, and `Playback2DTabViewModel`
is constructed by a bare `new()` with no DI, so there is nowhere useful to catch it. The profile
therefore validates, **drops, and reports** instead. `FromOverrides(rows, out rejected)` never throws.

- **Persisted as `"Action=Gesture"` rows** (`"NextRound=Shift+R"`), flattened as indexed keys exactly like
  `AnnotationRecentColors`. Only rows that DIFFER from the shipped gesture are stored, so a later default
  change still reaches everyone who never rebound that action. Gestures are written with the tokens
  `KeyGesture.Parse` accepts, never the display text. `←` and `Esc` are for human eyes and would not
  survive the next load.
- **A row is refused for one of five reasons**, each reported on its own line: it is not an
  `Action=Gesture` pair; the action is unknown; the action is **`Reserved`** (a reservation exists to keep
  a gesture unclaimed, so it cannot be claimed by a settings file either); the gesture is a shell
  accelerator; or it duplicates another binding **within its scope**. Scope matters: a `WhenToolActive`
  row may share a key with an `Always` one, because that shadowing IS the mechanism that turns `Space`
  into hold-to-pan.
- **The whole accepted set is applied first, and only then checked.** Row-by-row validation cannot express
  a swap (`PrevRound=E` + `NextRound=Q`), because the first half collides with the second half's
  not-yet-replaced default. Only if the batch fails does it fall back to applying row by row, so the
  report names the offending row instead of condemning the file.
- **Live, not next-activation.** `Playback2DTabViewModel` re-resolves the profile from an
  `IOptionsMonitor<AppSettings>.OnChange` (resolved through the same lazy `App.Services` locator as
  `SettingsService`, since the descriptor's factory is a bare `new()`). A keymap is the one setting a
  user edits with the tab open and immediately tests; "applies next time" reads as the rebind having
  failed. The external-edit callback arrives on a **threadpool thread** and marshals before notifying.
- **`OnKeyUp` follows the binding.** Hold-to-pan is released by key, and nothing else ever clears the
  router's flag, so a hard-coded `Space` there would leave a user who rebound pan stuck in pan mode
  forever. `Playback2DKeymapProfile.BindingFor(HoldPan)` is what the release matches, on the KEY alone.
  Releasing Shift a frame early must not strand the surface either.
- **`GestureText(action)` is the display surface.** Settings rows and tooltips read it **off the resolved
  profile**, so a rebound key shows the user's gesture and not the shipped one.
- **The Settings section validates before it writes** (`ValidateOverride`), so a conflicting rebind is
  refused inline with its reason rather than persisted and silently dropped on the next load. A non-empty
  rejection note in that section therefore always means a hand-edited file.
- **Pinned by** `Playback2DKeymapProfileTests` (every refusal reason, the swap, scope-aware duplicates,
  the persisted-row round trip), `Playback2DKeybindSettingsTests` (persistence including the fileless WASM
  path, and the whole Settings rebind/reset/refuse flow) and `Playback2DKeybindRoutingTests` (real
  headless key events through the real view, including the `OnKeyUp` hazard above). The core/pack split
  itself is `CommandRegistryTests` (the registry in isolation: a fake pack's chord, a reported collision,
  pack-off resolving to nothing) and `StratBookCommandsTests` (the real 22-row table pinned against its
  pre-move gestures, and the keybind settings list's pack label and gate).

| Gesture | Scope | Action | Notes |
|---|---|---|---|
| `Space` | Always | Play / pause | **Unless a drawing tool is active**; see the row below. |
| `Space` (held) | WhenToolActive | Temporary pan | Shadows play/pause while `Draw`/`Erase` is selected, and a tap does **not** toggle playback: every pan would otherwise start by un-pausing the demo under the user's pen. |
| `←` / `→` | Always | Step one frame | Owned by the transport, not by the player-card `ItemsControl`. The cards became selectable in A1, and the tunnelling handler is what keeps arrows from being silently swallowed. |
| `↑` / `↓` | Always | Speed ladder `0.25 · 0.5 · 1 · 2 · 4 · 8` | Inert (with a footer note) while Live Sync pins the speed. A refused key is still CONSUMED, so it cannot fall through to the card list. |
| `Q` / `E` | Always | Previous / next round | Rounds open at `round_freeze_end`, not `round_start`. |
| `Shift+Q` / `Shift+E` | Always | Previous / next kill | |
| `F` / `Shift+F` | Always | Cycle the follow target | |
| `D` | Always | Draw tool (press again for pan) | |
| `X` | Always | **Erase** tool (press again for pan) | **Not `E`.** Design §7.5 assigned `E` to both round-nav and erase; the keybind audit resolved it in favour of round nav (market parity), and erase pairs coherently with `Ctrl+X` below. B5 D1. The values are pinned by `Playback2DKeymapTests`; that the two never collide again, whatever keys they carry, by `Playback2DKeybindConflictTests.Erase_AndRoundNav_AreDifferentGestures`. |
| `Ctrl+Z` / `Ctrl+Shift+Z` | Always | Undo / redo an annotation edit | Refused while a stroke is in flight; the gesture is the user's current intent. |
| `Ctrl+X` | Always | Clear every annotation | CS:DM parity. Collides with Cut inside a focused `TextBox`, which the text-input rule below resolves. |
| `Esc` | Always | Clear follow + re-fit the camera | |
| `Esc` | WhenToolActive | Cancel the in-progress gesture | |
| `Ctrl+F` | Always | Find rounds like this | **A `StratBookCommands` row, not a `Playback2DKeymap` one** (item 19). Snapshots the current tick's alive players by side onto the Situations tab's Query Canvas, through the same place source the round index mints its rows with, and switches to that tab. Not `F`: bare `F` is follow cycling (Strat Room plan D7). Also on the mode menu and the `⌕ Rounds like this` button beside the kill nav. Unhandled with nobody alive, no demo, or the Situations tab gated off. |
| `J` / `K` | Always | Next / previous situation result | **A `StratBookCommands` row, not a `Playback2DKeymap` one** (item 19). Walks the Situations tab's result set from inside playback: each step seeks to the next card ten seconds before its matched tick through the same shell funnels a card click takes (the shared load core for another demo, the controller's `SeekToTick`, the tab by id). Unhandled with no result set, and the ends of the set do not wrap. Strat Room, Result Cards And Walking. |
| `Home` | Always | Fit the camera (**reserved**, unbound) | Declared so the conflict checker guards the gesture before anything claims it. |

**Text-input suppression is one global rule, not a per-binding flag.** The tunnelling handler bails
when `FocusManager.GetFocusedElement()` is a text input, which covers every single-letter binding and
both `Ctrl+X`/`Ctrl+Z` at once, so Cut and Undo mean Cut and Undo inside the annotation Text tool and
the export dialog's filename box.

**Follow-by-card.** The right-hand attributes panel is a `ListBox` whose containers are stripped of Fluent
chrome (`Padding=0`, transparent, `Focusable=False`). Selecting a card runs the single follow funnel
(`Playback2DTabViewModel.NotifyFollowSlotChanged`), which marks exactly one card (`Border.playerCard.followed`
→ `Pb2dPositive` outline + a `⦿ requested` chip), mirrors the slot onto `Playback2DViewport.FollowSlot`, and
calls `IModuleContext.NotifySpectateTarget`. The wording is **"requested"**, never "confirmed". CS2
spectating has no readback. Gated by `playback2d.follow`.

### BinaryPane
- **File:** `Controls/BinaryPane.axaml` (+ `.axaml.cs`). **DataContext:** `HarvestHexViewModel`.
- **Purpose:** the single adopted hex viewer: offset gutter, 2×8 byte cells, ASCII gutter, multi-range
  innermost-brush highlights, byte-click→node loop, optional header/footer/status banners + a level
  legend (selected/parent/ancestor swatches). Windowed virtualization for large buffers.
- **VM surface:** `Load` / `SetSpans` / `Clear` / `ByteClicked`; `HasData`, `HasHeader`/`HasFooter`,
  `WindowRangeText`, `Rows`.
- **Gating:** hex decode is **expensive**: VM must also check `IFeatureGate.IsEnabled("parser.hex")`
  before populating and clear buffers on disable (plan P1.2), not just `IsVisible`.

<a id="statuschip"></a>
### StatusChip + StatusStrip chip region (CSVG integration, `the design notes in git history` §4.1)
- **Files (as-built):** `Controls/StatusChip.axaml(.cs)` (DataContext = `ViewModels/StatusChipViewModel`);
  `Controls/StatusStrip.axaml(.cs)` gained a right-aligned `ItemsControl` chip region (spacing 12) bound to a
  new `IEnumerable? Chips` styled property (between the perf ticker and `RightText`); the shared status-dot
  styles live in **`Styles/Primitives.axaml`** (`Ellipse.dot.*`, see the class table). First consumer:
  **Live Sync (F1)** via `ViewModels/LiveSync/LiveSyncStatusViewModel` + `Views/LiveSync/LiveSyncStatusView`
  (the flyout body, resolved by the app `ViewLocator`). **Second consumer: the Reel
  job (F3b)** via `ViewModels/Highlights/ReelJobStatusViewModel` + `Views/Highlights/ReelJobStatusView`: the
  ≥2× that justifies the shared control is now realized (see [Create-Reel dialog + Reel chip](#reel-surfaces)).
  **Third consumer (demo-processing-queue.md §12): the demo-processing queue** via
  `ViewModels/DemoProcessing/ProcessingQueueStatusViewModel` + `Views/DemoProcessing/ProcessingQueueStatusView`
  (see [Processing-queue chip + flyout](#processing-queue-surface)), the queue's live management surface.
  **Fourth consumer (v0.5.3 step 9, `highlights-matchoverview-redesign.md` §5 row 2): the
  library-wide highlight scan** via `ViewModels/Highlights/HighlightScanStatusViewModel` +
  `Views/Highlights/HighlightScanStatusView`: the home the retired card grid's `ScanQueueSummary` badge and
  its per-card scanning animation were re-assigned to. It **extended, did not fork**: zero changes to
  `StatusChip`/`StatusChipViewModel`, zero new tokens, flyout body resolved by the `ViewLocator` like the
  other three. Four consumers now share the control. **The 2D export chip and the Strat Book export chip
  are the fifth and sixth** (`Playback2DExportStatusViewModel`, mounted via `MainViewModel.AttachPlayback2DExportStatus`
  for the 2D chip, a core, dedicated slot). The Strat Book chip goes through a generic pack `StatusChip`
  contribution instead (item 14): `MainViewModel.AttachStatusChips(IReadOnlyList<StatusChipContribution>)`
  watches each contribution's `IContributedStatusChip` (`StratBookExportChipSlot` for this one) via
  `INotifyPropertyChanged`, keyed by the contribution's own id in a `_shownContributedChips` map. The Strat
  Book tab's export job mounts into the slot lazily, on the first Export.
  - **Flyout contents:** queue depth · outdated count (`Pending && Events.Count > 0`) · failed count ·
    `◐ scanning <name>` · `[Retry all failed]` · `[⟳ Rescan all]`. Counts are neutral `TextMid` labels with
    `TextValue` values, never tinted, per the contrast rule above.
  - **`Retry all failed` gave `HighlightScanService.RequestScan(path)` back a UI entry point**, which the
    Reels-dashboard entry below recorded as lost when the card grid's per-demo badges went away. It re-queues
    only `Failed` rows: a user retrying three broken demos has not asked to re-harvest the seven hundred
    that worked, which is the whole difference from `Rescan all`.
  - **⚠ Duplication is safe for THIS consumer only.** It is a *pure projection* of
    `HighlightScanService` + `DemoCacheStore` and holds no job state, so two instances cannot disagree
    (the shell may build its own before the lazily-created Reels tab exists). That is the opposite of
    `ReelJobStatusViewModel`, where §4.3 requires exactly one instance because it models a running *job*.
  - **Presence rule:** `IsRelevant` (`QueueDepth > 0 || IsScanning || FailedCount > 0`), AND
    `!OperatingSystem.IsBrowser()`. Ungated: it appears only while work is happening, and
    `chrome.processingQueue` already sets the precedent that background work on the user's behalf is visible
    to every category. Shell registration shipped (`App.axaml.cs` DI singleton →
    `MainViewModel.AttachHighlightScanStatus`, which re-evaluates the strip on `IsRelevant`).
  - **Capture variants:** `scanchip-flyout`, `scanchip-chips`.
- **Purpose:** the app's idiom for a **persistent, stateful, background-activity indicator** in the bottom
  status strip: a **dot + label** (not a filled pill) that opens a `Border.card-flyout` for detail +
  actions. Empty `Chips` ⇒ nothing rendered, so the strip reads exactly as before.
- **Contrast rule (computed across all 4 built-in themes, ux-design §3.2):** the **label is always neutral
  `TextMid`**: the only token that clears AA (4.5:1) on `PanelHeaderBg` in every theme (`AccentCaution` as a
  label fails Light at 3.95). **State is word-carried; the dot is a redundant colour cue** (WCAG 1.4.1), which
  legitimises the two sub-3:1 dark dots (working `AccentInteractive` 2.72, off `TextDim` 1.96). Never tint
  the label to signal state; never signal state by dot colour alone. **Verified in-render** (`livesync-chips`
  under dark/light/high-contrast): labels stay neutral, the hollow inferred ring reads distinct from every
  solid dot, and the tokens carry each theme with zero per-file changes.
- **VM surface (`StatusChipViewModel`, no brushes):** `{ StatusChipDotState DotState; bool IsPulsing; bool
  IsHollow; string Label; string? Tooltip; object? FlyoutContent; ICommand? PrimaryAction }` plus derived
  class-driving bools `IsState{Off,Working,Good,Degraded,Error}`. **Colour rule (resolved fork):** the VM
  holds **NO** `DotBrush`/`LabelBrush`: the dot colour is a **bound state→class selector whose setter is
  `{DynamicResource Token}`** (the `Border.teamChip{,.teamT,.teamCt}` pattern) in `Styles/Primitives.axaml`,
  so it re-themes live. (The ux-design §4.1 prop-list literally named `IBrush DotBrush/LabelBrush`; that
  contradicts the theme mandate, so the class-driven form is authoritative. This note supersedes it.)
  State→token map: working `AccentInteractive` (pulsing), good `StatPositive`, **inferred = `StatPositive`
  HOLLOW ring + `(inferred)` suffix** (distinct from Degraded), degraded `AccentCaution`, error `AccentError`,
  off/suspended `TextDim`. **Zero new tokens.**
- **Structure:** a `Button.ghost` body `[Ellipse.dot][TextBlock label]` (solid = `Ellipse.dot.stateX:not(.hollow)`
  → `Fill`; hollow = `Ellipse.dot.stateX.hollow` → `Stroke`+transparent, mutually exclusive so no reliance on
  Avalonia declaration-order; opacity-pulse `Style` when `.pulsing`) whose `Flyout` wraps `FlyoutContent` in
  `Border.card-flyout`. Click (not hover) opens the flyout; the chip is focusable (Enter opens, since a
  `Button` auto-opens its `Flyout`). The **flyout body reuses the same `Ellipse.dot.*` header dot**. No new
  palette entries.
- **CSVG Phase-3 additions to the Live Sync consumer (2026-07-18, `LiveSyncStatusViewModel` +
  `LiveSyncStatusView`):**
  - **v1.0-plugin capability note (§5.2 + plan §6.6).** When `ILiveSyncService.Capabilities?.IsV10Baseline`
    (the plugin advertised nothing), the Synced **and** Degraded flyout sections show a verbatim caution
    note *"plugin 1.0 — update CSVG for exact pause sync"* via `ShowV10BaselineNote`. Rendered as the
    **sibling treatment** (⚠ `AccentCaution` icon + neutral `TextMid` message in a wrapping `Grid Auto,*`),
    matching the existing "untested plugin/game pair" note: AA-safe on the `CardBg` flyout in dark / light /
    high-contrast (computed + rendered). A **partial** capability set shows **no** note (the flyout stays
    lean; no matrix enumeration in v1).
  - **Synced-only position refresh.** `PositionText` (from `LastCs2DemoTick`) is kept live by a
    `DispatcherTimer` at ~2 Hz (`DispatcherPriority.Background`) that runs **only** while a Synced sub-state
    is current: started from the state mapper (`MapSynced`), stopped on any non-Synced transition + on
    `Dispose`. The start/stop **decision** is a plain `IsPositionTimerRunning` flag (test seam) separate from
    the timer mechanism, so the pure-VM tests assert the mapping without a dispatcher pump.
  - **Second surface: the 2D-tab in-context indicator** (below).

<a id="processing-queue-surface"></a>
### Processing-queue chip + flyout (demo-processing-queue.md §12)
The live management surface for the global background demo-processing queue: the **third `StatusChip`
consumer**, placed in the bottom status strip because a queue is exactly the "persistent, stateful,
background-activity" the idiom exists for (out of the way for developers, present-by-default for power+dev,
hidden for consumers).
- **Files (as-built):** VM `ViewModels/DemoProcessing/ProcessingQueueStatusViewModel` (maps
  `Services.DemoProcessing.IDemoProcessingQueue` → the chip + flyout; owns `Chip` + `Rows`) + row wrapper
  `ViewModels/DemoProcessing/DemoQueueRowViewModel`; flyout body `Views/DemoProcessing/ProcessingQueueStatusView`
  (resolved by the app `ViewLocator`). Wired in `MainViewModel` (built in the ctor when a queue is injected;
  presence reconciled by `ReconcileQueueChip`, subscribed to `IDemoProcessingQueue.Changed` + re-run from
  `ApplyGateChange`). Gate shim `MainViewModel.IsProcessingQueueEnabled`.
- **Presence rule:** chip added to `MainViewModel.Chips` only while `chrome.processingQueue` is on (fail-CLOSED,
  ANDs `!IsBrowser()`) **AND** the queue has activity (`RunningCount>0 || QueuedCount>0`) or `IsPaused`, so an
  idle+enabled queue adds no strip clutter, and a paused queue always offers Resume (mirrors the Reel chip's
  conditional presence).
- **Chip mapping (no brushes; the shared `Ellipse.dot.*` state→token dots + neutral `TextMid` label):**
  paused → `Off` "Queue paused"; running>0 → `Working` **pulsing** "Processing N"; queued-only → `Working`
  steady "N queued"; idle → `Off` (hidden anyway). Tooltip = the full status line.
- **Flyout:** header + live status line ("N running · M queued", + " · paused" / " · background disabled"), a
  transient **Pause/Resume** `ghost` button + a **Settings** link (`OpenSettings`), then the item `ListBox`
  (`data-list`), one row per `DemoQueueItem`: **state dot + name (trim, path tooltip) + owner chip(s) +
  priority chip (only when elevated) + per-item ✕** (`icon-btn` → `RemoveByUser(Id)`). Empty ⇒ "Nothing queued."
- **Every kind of background job is a row** (header "BACKGROUND WORK", Pause/Resume queue): a job that is not a
  demo parse (lineup clips, strat mining, pack export, sidecar migration, heap compaction) shows its title, a
  `badge` kind chip in place of the owner chip, and while running a 3px determinate `ProgressBar`
  (`AccentInteractive`) under the title plus its `mono` detail ("48 of 366 demos") after the state word. ✕ on a
  running job cancels it at its next step. Render check: `ProcessingQueueViewTests` writes
  `queue-flyout-mixed.png`.
- **`DemoQueueRowViewModel`, the reuse win:** the six lifecycle states map onto the **existing five semantic
  `Ellipse.dot.*` states** (`Queued`/`Running`→`Working` [Running also `.pulsing`]; `Completed`→`Good`;
  `Failed`→`Error`; `Rejected`→`Degraded`; `Cancelled`→`Off`). **Zero new tokens, zero new styles.** The state
  **word** (`StateLabel`) is the accessible carrier; the dot is the redundant colour cue (WCAG 1.4.1), same as
  the chip. The row is a presentation-only wrapper: **no DemoProcessing service file was edited** (the queue
  item / enums are unchanged; the brief's "prefer not to touch `DemoQueueItem`" is honoured).
- **Theme/verify:** rendered across dark / light / high-contrast (`queue-flyout`, `queue-flyout-empty`,
  `queue-chips` variants in `UiCapture/Variants.cs`): the dots + chips + dark-indigo/white text carry each
  theme with zero per-file changes. Pure-VM coverage: `ProcessingQueueStatusViewModelTests` (8 cases).

### Live Sync 2D-tab indicator + `ILiveSyncHudState` seam (CSVG Phase-3, ux-design §5.3)
- **What:** a small **display-only** CS2 chip on the 2D Playback HUD (top-right, in the walled-off `Pb2d*`
  palette via [`Ellipse.pb2dDot.*`](#pb2d-hud-dot)), present only while `chrome.livesync` is on AND the 2D
  tab is active AND the session is non-Disconnected. Following pulses (CS2 is the clock master); inferred
  pause is a hollow green ring. It is the **second surface** of the single Live Sync chip VM, not a second
  control centre.
- **The seam (engine-free, decoupled):** `IModuleContext.LiveSyncHud` (`ILiveSyncHudState?`, default null) in
  `Modules.Abstractions`: a read-only projection `{ bool IsActive; LiveSyncHudDot Dot; bool IsPulsing;
  bool IsHollow; string Label; event Changed }` with **zero** dependency on the App-layer `Services.LiveSync`
  contract (WASM-poison). Precedent: `IModuleContext.NotifySpectateTarget` is already a CSVG seam here.
  `LiveSyncStatusViewModel` **implements** `ILiveSyncHudState`; the shell wires it once in `AttachLiveSync`
  via `ModuleContext.SetLiveSyncHud`. `IsActive` folds in the `chrome.livesync` gate (`isHudGateEnabled`
  func) so a gate flip while the 2D tab is active reflows the indicator (`NotifyHudGateChanged` from
  `ReconcileChips`); the seam is **never cleared** (presence ≠ gate, the gate lives in `IsActive`). The 2D
  VM captures the instance at `OnActivated`, subscribes to `Changed`, unsubscribes the **same** instance at
  `OnDeactivated`. Test doubles inherit the default null → indicator absent (no test churn).

<a id="reel-surfaces"></a>
### Create-Reel dialog + Reel job chip (CSVG integration, ux-design §8/§9)
The **Create-Highlight-Reel** flow (Highlights footer → modal dialog → background job → status chip). Two
VMs, both `ViewModelBase` (ViewLocator-resolved), both fully token-driven: **zero new tokens** (ux-design
§11.1: the reel palette maps onto existing accents).
- **`HighlightReelDialogViewModel`** (`ViewModels/Highlights/`, view `Views/Highlights/HighlightReelDialogView`,
  modal host `HighlightReelDialogWindow`, the **FirstRunWizard precedent**: `Window` DataContext = VM,
  `ContentControl Content="{Binding}"`, closes on the VM's `Closed` event). Turns the tab's
  `IReadOnlyList<HighlightSelection>` into a coalesced plan via `Modules/Highlights/ClipWindows` (Candidate →
  `Coalesce` → `ReelClip`), shows the merge **visibly** (§8.2, contributor rows + a `→ merged clip` summary
  line, header `N selected · M after merge`), edits padding/preset/output/encoding, validates inline (§8.6),
  and on Generate persists the edited reel defaults + calls `IReelJobService.Start` + raises `Closed` (§8.7).
  - **Injected seams (do not inline platform/FS calls; they hide test/capture branches):** `bool dryRunOnly`
    (macOS = the §8.9 "Dry run (mock)" primary + `DryRun=true`), `Func<string,bool> fileExists` (§8.6 demo-moved
    pre-flight, a per-path predicate so a mixed valid/moved plan renders), `Func<bool> isLiveSyncSessionActive`
    (§9 interlock), `Action<Action<AppSettings>> persistDefaults` (§10 "set once"; App passes `SettingsService.Write`).
  - **CRF ⊕ Bitrate** is UI-enforced: `UseCrf` drives `CrfEnabled`/`BitrateEnabled` and the request carries
    exactly one of `Crf`/`VideoBitrate` (mirrors `Cs2Compilation`). **SteamId → `long.TryParse` (0 on fail).**
  - **§9 interlock = an in-dialog confirm strip** (no toast system): Generate with a live session shows the
    verbatim §9 copy + Continue/Back; Continue starts. The strip is UX-only: the engine performs the suspend.
- **`ReelJobStatusViewModel`** (mirrors `LiveSyncStatusViewModel`): owns a `StatusChipViewModel Chip`
  (`FlyoutContent = this`), maps `ReelJobStatus` → dot/label + the flyout's per-clip list + Cancel/Retry/Dismiss.
  The shell (`MainViewModel.AttachReelJob`) reconciles the chip into `Chips` while the job runs **or** a
  finished result is not yet dismissed (`_reelDismissed`; a new running status un-dismisses); `DismissRequested`
  removes it. **Contract-faithful reductions to record:** the App `ReelJobStatus` carries no per-clip labels
  (non-active rows are "Clip k"; the active row gets `CurrentClipLabel`), no intra-clip percent (the §8.8 bar is
  **indeterminate**), and no `DryRun` flag (the chip renders identically for real vs mock; the dry-run framing
  lives only in the dialog). The dialog's granular §8.4 `Cs2ClipOptions` checkboxes collapse to a **Default /
  No-HUD preset radio**: the only display flag the App `ReelRequest` plumbs (`NoHudPreset`).
- **Wiring:** the footer `RequestCreateReel` event is subscribed by wrapping the Highlights VM factory in
  `App.BuildRegistry` (lazy shell resolve, like the other Highlights delegates); it reads the reel service +
  live-sync state + `OperatingSystem.IsMacOS()` at fire time, and no-ops when the service is null (Browser/tests)
  or a job is already running (§9). `IWindowService.ShowHighlightReelDialog` = desktop modal / WASM no-op.
- **Rendered + read** (`reel-dialog`, `reel-dialog-invalid`, `reel-dialog-macos`, `reel-chips` in
  `UiCapture/Variants.cs`): the valid dialog (dark + light) shows the merged-clip display; `reel-dialog-invalid`
  (dark) the §8.6 per-row "demo moved" + banner + disabled Generate; `reel-dialog-macos` (dark) the §8.9
  "Dry run (mock)" primary + caption; `reel-chips` (dark + light + high-contrast) the working/completed/failed
  states. All re-theme with zero per-file changes; the per-clip glyph colour is a bound state→class→token
  selector (theme mandate), never a code-held brush. (Contrast is token-vs-surface = the §3.2 computed table.)

### SpotlightScrim + TutorialView: first-run Visual Walkthrough overlay (feature/v0.5.1-guide, 2026-07-24)
The **presentation layer** of the coach-mark tour (the engine (anchor registry, tab-driving, wizard
trigger, persistence) is a separate follow-up phase built against this contract). Consumer-scoped content
for every audience; first-run + skippable.
- **`Controls/SpotlightScrim.cs`**: a code-drawn `Control` (one even-odd `GeometryGroup`: full-bounds rect
  + a rounded-rect hole) that dims the window with a spotlight cut-out + a token-coloured frame. **Theme
  contract:** every colour is a `StyledProperty<IBrush>` set from markup via `{DynamicResource Token}` and
  registered with `AffectsRender`, so it re-themes live with **no** cached brushes and **no** manual
  `ActualThemeVariantChanged` sub (unlike the Skia surfaces; there's nothing cached to rebuild). Props:
  `ScrimBrush` (`ShellBg`), `ScrimOpacity` (0.86, baked into the fill so the frame stays opaque),
  `HoleBorderBrush` (`AccentInteractive`), `HoleBorderThickness`, `HoleCornerRadius`, `HolePadding`,
  `HasHole`, `Hole` (`Rect`, overlay coords). Fully **hit-test visible across its whole bounds incl. the
  hole** → blocks click-through (the tour is Next-driven).
- <a id="spotlight-pulse"></a>**Breathing spotlight pulse (visual polish, 2026-07-24).** A visual-only
  `Pulse` `StyledProperty<double>` in `[0,1]` (0 dim trough … 1 bright peak), registered with
  `AffectsRender`. `Render` uses it to (a) fade a **soft outward glow**: 4 concentric rounded strokes at
  falling alpha faking a blur-free halo (DrawingContext has no blur), scaled by `Pulse`, and (b) breathe the
  crisp inner frame's opacity `0.72→1.0` so the highlight is a **soft breath, never a hard on/off flash**
  (it never fully drops out). **Colour stays theme-driven:** the glow/frame is `HoleBorderBrush`
  (`{DynamicResource AccentInteractive}`) with its **alpha** scaled via `SolidBorderAt(opacity)`: no colour
  is ever set in code, so the halo follows the token (indigo on dark/light, cyan on high-contrast, verified
  in-render). The breath itself is a **class-driven `Animation` on `Pulse`** (~1.6s, `SineEaseInOut`,
  `1.0→0.12→1.0`) in `TutorialView`'s `<UserControl.Styles>` under selector `controls|SpotlightScrim.pulsing`,
  the same idiom as the shared `Ellipse.dot.pulsing`. **Single owner:** the `.pulsing` class is applied by
  the code-behind (`UpdatePulse`, idempotent `Classes.Set`) **only while `IsActive && HasSpotlight`**, so it
  never animates on the centred welcome/outro cards and a step→step transition that keeps the spotlight up
  doesn't restart the breath. Do **not** also bind `Classes.pulsing` in XAML (two owners conflict). Capture
  hook: `TutorialView.AnimatePulse=false` + `SetStaticPulse(0|1)` pins a phase deterministically for review
  (the live loop settles near its bright Cue-0% end under the headless render pump).
- **`Views/Tutorial/TutorialView.axaml(.cs)`**: DataContext `ViewModels/Tutorial/TutorialViewModel`.
  Mounts as one more sibling `<Panel IsVisible="{Binding IsActive}">` in `MainView`'s root Panel, exactly
  like the Idle / Settings overlays; **never reparents** the real UI. Two layers: the `SpotlightScrim` +
  a `Border.card` callout on a `Canvas`. The only code-behind is **layout** (`PositionCallout`): places the
  bubble per `CalloutPlacement` (Above/Below/Left/Right/Center) relative to the padded hole and **clamps it
  fully on-screen** so a spotlight near any edge keeps its bubble visible (a render/geometry concern derived
  from bound state, not data-pushing). Callout = step indicator + `Skip tour` (`ghost`) · title
  (`TextCardHeader`) · body (`TextMid`, ~7.4:1 on `CardBg` in light, AA in all 3 themes) · `Back` (`ghost`,
  `IsVisible=CanGoBack`) + Next/Finish (`primary`, `IsVisible=!IsWaiting`). No new tokens.
- <a id="waiting-affordance"></a>**Waiting-state affordance (visual polish, 2026-07-24).** On the
  `WaitsForDemo` gateway step with no demo open (`IsWaiting`), the Next button is hidden and the
  `WaitingHint` is shown in a **calm bordered status box** (`Border`: `PanelHeaderHover` fill /
  `BorderSubtle` hairline / radius 6) pairing a **breathing "watching" dot** with a **neutral `TextMid`**
  italic hint. Colour rule follows the [StatusChip contrast contract](#statuschip):
  the state is **word-carried**, the hint text is neutral `TextMid` (the AA-safe-across-themes body token,
  ~4.6:1 on the box in dark; verified in-render), and the **`AccentInteractive` dot is the redundant colour
  cue** (WCAG 1.4.1). **Never tint the hint** to signal state. The dot **reuses the shared
  `Ellipse.dot.stateWorking.pulsing`** (Primitives.axaml: `AccentInteractive` fill + the opacity-pulse), so
  it echoes the spotlight breath and reads as "live, watching for the demo" rather than a dead line of text.
  Zero new tokens/controls: it extends the existing status-dot idiom.
- **Contract (`TutorialViewModel`, display-only, delegated-action pattern like `IdleViewModel`):** the VM
  holds NO navigation logic. Engine-set: `IsActive`, `CurrentStep` (`TutorialStep`: Title/Body/HasSpotlight/
  Target/Placement/NextLabelOverride), `SpotlightRect` (engine measures the anchor into overlay coords),
  `StepNumber`/`StepCount` (→ derived `StepIndicator` "2 of 8"), `CanGoBack`/`CanGoNext`, `NextLabel`,
  `IsWaiting`/`WaitingHint`; proxies `HasSpotlight`/`Placement` (from `CurrentStep`); commands
  `Back`/`Next`/`Skip` delegate to injected `Action`s. Content lives in `TutorialSteps.Default` (**8 steps**:
  4 `FirstRun` (welcome, tab-nav, library, open-demo gateway) + 4 `DemoLoaded` (stats, playback, transport,
  outro)), the engine's **input** (not VM-owned) so it can sequence the two segments (which fire at different
  times) and filter by `TutorialSegment`. Design-time ctor = welcome step for the previewer/captures.
- **Verified** in `UiCapture/Variants.cs` at `--size 1280x800` over a coarse app-like backdrop under
  **dark / light / high-contrast**. Capture variants (indices track `Default`: `[0]` welcome, `[1]` tab-nav,
  `[2]` library, `[3]` open-demo gateway, `[6]` transport): `tutorial-welcome` · `tutorial-tabnav` ·
  `tutorial-library` · `tutorial-waiting` (the gateway parked in its `IsWaiting` state) · `tutorial-transport`,
  plus `tutorial-tabnav-bright|dim` (forced-`Pulse` phases for reviewing both ends of the breath). Backdrop
  gained a right-docked **"Open Demo"** button (`_openDemoRect`) as the gateway's spotlight target. Findings:
  the breathing frame+halo follows the accent token in every theme (indigo on dark/light, cyan on
  high-contrast), the waiting box + pulsing dot read as intentional, edge-clamping flips the transport callout
  above / the gateway callout left. **NOTE:** the transport-strip rect is `Dock=Bottom`, so its seeded rect
  tracks window height: keep the capture `--size` (1280x800) and `_transportRect` in sync. NOT wired into
  MainView/MainViewModel yet (engine phase owns integration).

### FirstRunWizardView Extensions step: one card per FeatureScope.Pack row (feature/strat-book-ext-6-first-run, 2026-10-02)
Item 6 of the Strat Book extension plan (`docs/architecture/strat-book-plugin.md` section 6, section 8,
section 10 decision 2, section 11.1). Asks whether to turn each installed extension on, generically over
the catalog rather than a hardcoded Strat Book reference.
- **Placement.** A new step inserted before Done (`FirstRunWizardViewModel.WizardStep`: Welcome, Category,
  Folders, **Extensions**, Done), so every earlier index (`IsCategoryStep` = 1, `IsFoldersStep` = 2) stays
  stable whether or not the Extensions step exists this run.
- **Generic, not Strat Book specific.** The VM takes an optional `IEnumerable<FeatureDescriptor> packs`
  ctor param (default: `FeatureCatalog.All.Where(d => d.Scope == FeatureScope.Pack)`) and builds one
  `PackOptionViewModel` per row: `FeatureId`, `Title` (the descriptor's `Label`), `Copy`, `Enabled`
  (seeded from any existing override, else the catalog default for the current category). A second pack
  gets its own card from the same loop, with no new VM or view code.
- **Copy.** A small `_packCopy` lookup in the VM keyed by pack id supplies a richer "what it adds, what it
  costs" paragraph for a known pack (Strat Book's cites the M0 memory figure in round terms, "a few hundred
  MB of memory on a large library", `docs/architecture/strat-book-plugin.md` section 12); an id with no
  entry falls back to the descriptor's own one-line `Description`, which is what keeps a future pack's
  question working before anyone writes bespoke copy for it.
- **Visibility.** The step exists only when `SettingsService.NeedsFirstRun` is true at construction
  (captured once, before `Finish`/`Skip` can flip it) and at least one pack row exists. An upgrade whose
  settings.json predates a pack's key never sees the question: the gate's override-or-default resolution
  already lands on for a missing key, so the step would be asking something already answered.
- **Writes.** `Finish` writes every answer explicitly into `Features.Overrides[FeatureId]`, on or off, the
  same unconditional pattern as `UserCategory` and `Library.Folders` above it; `Skip` never touches pack
  overrides, so a skipped first run leaves every pack at its catalog default (on), matching `Skip`'s
  basis-preserving contract for the rest of the wizard.
- **Layout.** One `Border.card` per pack (title + wrapped copy left, a `CheckBox` right), inside an
  `ItemsControl` bound to `PackOptions`. No new tokens: `TextValue`/`TextMid`/`TextDim`/`card` reused as is.
- **Verified** in `UiCapture/Variants.cs` as `wizard-extensions` (`--size 1280x800`, a fresh temp-dir
  `SettingsService` parked on `CurrentStep=3`) and in `FirstRunWizardTests` (headless window render plus
  accept/decline/skip/upgrade coverage against a live `FeatureGate`).

<a id="stats-components"></a>
### Stats component library (`Controls/Stats/`, v0.8.1)

Nine controls plus a scale model and the abstract presenter they share, built for the Stats team and
player views. Full rationale, the
dependency survey that ruled out every charting library, and the open questions are in
[`stats-components.md`](./stats-components.md); this is the contract.

| Type | What it is | Key API |
|---|---|---|
| `StatScale` | Pure record, **no Avalonia reference**, so it unit-tests without a render harness. Maps a raw value onto two channels. | `Fraction(v)` → 0..1 bar, `Sentiment(v)` → -1..+1 colour. Factories: `Absolute` `FromPeers` `Banded` `Hybrid` — the four `StatScaleSpec.Resolve` calls. (`SignOf` and `Penalty` were both `Banded` in disguise with no caller, and are gone.) |
| `StatPresenter` | Abstract base holding the value, the scale and the four accent brushes. The sentiment→brush rule lives here **once**. | `Value` `Scale` (or `Minimum`/`Maximum`/`Polarity`/`NeutralLow`/`NeutralHigh`), `Accent`, `Fraction`, `Sentiment`, `StrongSentimentAt`. |
| `StatValue` | The workhorse cell: number over a bar, in a chip, or plain. | `Mode` = `Bar`/`Chip`/`Plain`, `IsLeader`, `TintBar`, `TextAlignment`, `BarAlignment` = `Left` (default)/`Center`/`Right`/`Auto`, `BarTrackBrush`, `BarFillBrush`. |
| `SegmentedBar` | Stacked proportion bar, both densities. | `Segments` (`StatSegment(Value, Brush, Label)`), `Compact`, `ShowLabels`, `Gap`. |
| `Sparkline` | Per-round micro chart. | `Values`, `Mode` = `Line`/`Bars`/`Dots`, `PointTooltips`, `IndexAt(point)`, `Baseline`. |
| `RingGauge` | One headline number on a circular track. | `Thickness`, `Caption`, `TrackBrush`. |
| `StatTile` | Templated KPI tile; the number is a nested `StatValue`. | `Label`, `Caption`, `IsHero` (`:hero`). |
| `TeamBadge` | Templated team header with a WIN/LOSS pill. | `Team` (CS2 wire: 2 = T, 3 = CT), `Label`, `Outcome`, `Detail`; pseudo-classes `:ct` `:t` `:win` `:loss` `:draw`. |
| `DivergingBar` | Won against lost about a break-even line. Replaces the four columns a duel record usually gets with one shape carrying all four. | `Negative`, `Positive`, `Extent` (the shared half-scale; without it each row self-scales and stops being comparable), `BarHeight`. |
| `PipStrip` | A small count as one mark per event, for magnitudes where a numeric column is mostly whitespace. | `Count`, `MaxPips` (past it the number is written instead), `PipRadius`, `PipGap`, `EmptyBrush`. |
| `SprayPlot` | One spray run as a 2D trace in degrees: the weapon's recoil pattern underneath as a faint reference, the player's own landed bullets over it. Pitch is deliberately **not** flipped (Source pitch is positive down and so is screen Y), and both series share one scale computed over both, so the shapes are comparable rather than each self-normalised. Gaps in the trace are the misses and are drawn as gaps. | `Pattern`, `Shots`, `PatternBrush`, `ShotBrush`, `AxisBrush`, `DotRadius`. |

**Previewing them.** All nine have a product call site now, so the gallery is no longer the only way to
see one — it is where the EDGE of each lives. The Diagnostics tab carries a collapsed
**"Stats component gallery (design)"** panel: every control, live, driven by sliders, reading
no demo and reachable from a cold start. It rides the `tab.diagnostics` gate and owns its own view
model. `UiCapture` variants: `stats-components`, `stats-components-edge`, `stats-gallery`.

**Invariants worth not breaking:**
- **Bar length and colour sentiment are separate channels.** `Min`/`Max` drive the bar;
  `NeutralLow`/`NeutralHigh` carve a dead zone out of the colour only. A single min/max/value triple
  cannot express a column whose bar is peer-relative but whose colour is banded on a fixed pivot,
  which is what a rating column needs.
- **`Fraction` is never polarity-inverted.** A deaths column draws its longest bar for the most
  deaths; only `Sentiment` flips. A big number over a short bar reads as a rendering fault.
- **Neutral paints nothing.** Inside the dead zone no foreground is set, so the value inherits the
  surrounding text colour and stays correct in every theme. Most cells in a healthy table are neutral;
  that is what makes the outliers legible.
- **Bars are not colour-coded.** `TintBar` is off by default so colour is not spent twice on the same
  fact. A fifteen-column board with tinted bars is a heat map, not a table.
- **No brush is held in code.** Every colour is a styled property fed from `Styles/Stats.axaml` via
  `{DynamicResource}`, so a variant switch re-resolves it and `AffectsRender` repaints.
  `StatsComponentRenderTests.Gallery_RepaintsWhenTheThemeVariantChanges` is the guard.
- **The four drawn controls set their own `AutomationProperties.Name`**, because they paint glyphs
  rather than hosting a `TextBlock` a screen reader could find.

### Other Controls/ (not full shared-design components, but shared)
`CommandPalette` (Ctrl+P overlay), `OutputPanel` (bottom drawer), `StatusStrip` (bottom status),
`ParseLinkChip` (source-link chip). `OpenExternal.cs` = VS Code / browser launch helper (desktop only,
WASM-guard needed).

### Shared style classes (P1.3, `Styles/Primitives|Cards|Tables|Chrome.axaml`)
Apply with `Classes="…"` (XAML) or `Classes.Add("…")` (code). All colors are DarkPalette tokens.
Each class was rendered + read this pass (variant in the last column; see §7).

| Class | Type | Purpose / look | Tokens | Variant |
|---|---|---|---|---|
| `.primary` | Button | Filled interactive CTA (Add/Continue/Apply). Border + darker face. | `PrimaryButtonBg/Border/Hover`, `TextBright` | `primitives` |
| `.ghost` | Button | Minimal transparent affordance: **color+hover only, no geometry** (layer your own padding/height). | `TextMid`, `PanelHeaderHover` | `primitives` |
| `.chip` | Button, ToggleButton | Compact toolbar size (FontSize 11 / Padding 8 3); **keeps Fluent chrome/color**. | None (size only) | `primitives` |
| `.nav-btn` | Button | Fixed 28px centered ghost nav button (CLOCK/JUMP groups). | `TextMid`, `PanelHeaderHover` | `primitives`, `chrome`, `navstrip-real` |
| `.bp-btn` | Button | Amber tint modifier for the dev-only TO-BREAKPOINT cluster (compose with `.nav-btn`). | `AccentAmber` | `primitives`, `chrome` |
| `.icon-btn` | Button | Small square glyph button (toggle dot, ✕); deeper hover for dense rows. | `TextFrameInfo`, `PanelHeaderHoverDeep` | `primitives` |
| `.pane-toggle` | Button | 24 px `«`/`»` that collapses and reopens a side pane (see Collapsible side pane). | `TextMid`, `PanelHeaderHoverDeep` | `strat-editor`, `strat-editor-collapsed` |
| `.ctx-action` | Button | Left-aligned, stretched flyout/menu action row. | `TextDim`, `PanelHeaderHover` | `chrome` |
| `.shell-tab` | TabItem | On-theme monospace tab header (mirrors MainView's local tab look, for module/sub-tab bars). | None (mono/size) | `primitives` |
| `.mono` | TextBox, ComboBox, Button, TextBlock | The Consolas/Menlo monospace family: the single most-repeated inline attribute (~200 sites). | — | `primitives`, `tables` |
| `.field` | TextBox, ComboBox | Compact form input = `.mono` + FontSize 11; keeps Fluent input chrome. | — | `primitives` |
| `Border.card` | Border | Raised content tile: the general card shell. | `CardBg`, `BorderAccent` | `chrome` |
| `Border.card-flyout` | Border | Transient popup surface (bookmark/event-filter flyouts). | `CardBg` | `chrome` |
| `ListBox.data-list` | ListBox | Transparent borderless tight virtualized list (+ selected-row highlight). | `PanelHeaderHoverDeep` | `tables` |
| `ListBox.card-grid` | ListBox | Transparent virtualized **card-grid** container: zero item padding/margin, **no** selection/hover fill (the radar cards carry their own). The 2× radar browsers (Library + Highlights master lists). Distinct from `.data-list` (which keeps row padding + a selection fill). | None (transparent only) | `library-populated` |
| `TextBlock.col-label` | TextBlock | Tiny uppercase **data-column** header (brighter than `.sectionLabel`). | `TextLabelAlt` | `tables` |
| `Border.sectionHeader` | Border | Section band with bottom rule (migrated verbatim from Components.axaml). | `PanelHeaderBg`, `BorderStrong` | `chrome` |
| `TextBlock.sectionLabel` | TextBlock | Whisper-quiet uppercase **section-band** label (intentionally dim). | `TextLabel` | `chrome` |
| `TextBlock.group-label` | TextBlock | 9px dim inline cluster heading ("JUMP", "TO BREAKPOINT"). | `TextDim` | `chrome`, `navstrip-real` |
| `Border.badge` | Border | Small rounded count/status pill (pair a `.mono` child in `TextChainBadge`). | `ChainSummaryBadgeBg` | `chrome` |
| `Rectangle.divider` | Rectangle | Hairline rule (color only; set Width/Height per use). `.divider.strong` = heavier. | `BorderSubtle`/`BorderStrong` | `primitives`, `chrome` |
| `Ellipse.dot` | Ellipse | StatusChip status dot (CSVG §3.1). State→token via `.stateOff/Working/Good/Degraded/Error`; `.hollow` = ring (Stroke); `.pulsing` = opacity animation. Consumer sets Width/Height. | `TextDim`/`AccentInteractive`/`StatPositive`/`AccentCaution`/`AccentError` | `livesync-chips`, `livesync-flyouts` |
| `stats\|StatValue.untracked` | StatValue | Drops the empty-bar track, which is **on by default**. For a one-off bar with no column to compare against. | `StatBarTrack` | `stats-components` |
| `stats\|StatValue.chip` | StatValue | The utility table's rating pill: centred, padded, tinted by its own sentiment. | ramp tokens | `stats-components` |
| `stats\|SegmentedBar.compact` | SegmentedBar | In-cell density: four counts in one table column. | — | `stats-components` |
| `Button.stat-col-header` | Button | Sortable leaf column header; `.sorted` switches label + caret to the interactive accent. | `PanelHeaderHover`, `AccentInteractive` | `stats-components` |
| `TextBlock.stat-sort-caret` | TextBlock | Sort-direction caret, dim until its column is the key. | `TextDim`, `AccentInteractive` | `stats-components` |
| `Border.stat-group-header` | Border | Band spanning several leaf columns ("Quality Rating Breakdown"); pair with `TextBlock.stat-group-label`. | `PanelHeaderBg`, `BorderSubtle` | `stats-components` |

**Note on `sectionHeader`/`sectionLabel` casing:** these keep their original **camelCase** (3 live
consumer views + the UiCapture `Section()` helper depend on the names). All *new* P1.3 classes use
**kebab-case** (`nav-btn`, `col-label`, `group-label`, `card-flyout`), the repo majority + task
convention. When you promote a new pattern, prefer kebab.

### Adoption guide for P2/P3 per-tab streams
The foundation is *defined + rendered*; broad *adoption* (swapping inline styling for these classes) is
P2/P3 per-tab work. When you touch a tab:
- **Buttons:** a confirm/apply CTA → `Classes="primary"`; a cancel/secondary → `Classes="ghost"`
  (add your own `Padding`); a compact toolbar button/toggle → `Classes="chip"`. Delete any local
  `Button.primary`/`Button.icon`/nav-button `<Style>` blocks. They're global now.
- **Mono text / inputs:** replace `FontFamily="Consolas,Menlo,monospace"` with `Classes="mono"`; a small
  form input → `Classes="field"` (drop the inline `FontFamily` + `FontSize="11"`).
- **Section headers:** `Border Classes="sectionHeader"` wrapping `TextBlock Classes="sectionLabel"`.
  Data-column headers use `TextBlock Classes="col-label"` (not `sectionLabel`).
- **Lists:** a transparent borderless list → `ListBox Classes="data-list"` (drop
  `Background="Transparent" BorderThickness="0"` + local `ListBoxItem` padding styles).
- **Cards/flyouts:** a raised tile → `Border Classes="card"`; a `Flyout` body → `Border Classes="card-flyout"`.
  Menu rows inside → `Button Classes="ctx-action"`.
- **Dividers/badges:** `Rectangle Classes="divider"` (add `.strong` for group boundaries);
  count pill → `Border Classes="badge"`.
- **Verify:** render the touched panel via UiCapture and read it (§7 / capture notes) before declaring done.
- **Do NOT** fork a new near-duplicate class; extend these or promote a genuinely new ≥2× pattern here
  (record its contract in the table above). **Do NOT** add a base `ControlTheme TargetType="…"`: it
  breaks the no-regression guarantee. InspectorCard/KeyValueTable internals stay private to their
  controls; compose around them.
- **When you add a global class name, reverse-check it** against existing `Classes="…"` in views you're
  not touching (`grep -rn 'Classes=' --include='*.axaml' src/App/DemoViewer.NET`). Avalonia matches
  **exact tokens**, so `msg-card`/`cat-badge`/`card-tree`/`filterchip`/`cardBusy` do **not** collide with
  `card`/`badge`/`chip`: a collision is only an exact-token match on a control type your selector
  targets. (P1.3's invented names were checked clean this way.)

---

## 3. The three breakpoint surfaces

Three surfaces intentionally serve **different jobs**, keep them coherent, do not merge:

| Surface | File | Job | Audience |
|---|---|---|---|
| **NavStrip `TO BREAKPOINT`** | `Controls/NavStrip.axaml` | **Action**: continue / step-to-breakpoint while navigating | dev / power |
| **Debugger panel** | `Views/DebuggerPanel.axaml` (SplitView right rail) | **Management**: add/list/enable/delete frame/tick/event breakpoints; status "stopped at"; Continue/Clear-all | dev / power |
| **Analysis graph-breakpoint** | `Views/Analysis/AnalysisTabView.axaml` | **Distinct**: breakpoints on rule-graph nodes/edges; own list overlay + conditional-editor; prev/next hit | dev only |

Coherence model: **NavStrip = act, Debugger panel = manage, Analysis = a different breakpoint domain
(rule graph, clearly labeled).** All three are developer-oriented chrome and should gate together as a
`graphDebug` / `parserDeepDive`-style group (plan P1.2). The Analysis graph surface is **not
headlessly renderable** (live MSAGL `GraphView` never settles geometry), iterate it via annotated
static mockup or `SvgExporter`, never a promised headless before/after.

---

## 4. Layout patterns

- **Shell chrome = `DockPanel`** (`MainView.axaml`): Top toolbar → Top NavStrip row → Bottom
  StatusStrip → Bottom OutputPanel drawer → Fill `SplitView`(right Debugger rail) → `TabControl`.
- **Tabs = one real `ItemsSource`-driven `TabControl`** over `WorkspaceTabDescriptor`s (built-ins +
  module tabs, one code path). Inactive tab Views are unrealized (single content presenter). Never
  fake tabs with `IsVisible`-toggled stacked grids.
- **Resizable panes = `Grid` + `GridSplitter`** with `*`/`Auto`/`px`. Avoid deep nested grids with
  hardcoded pixel widths that don't scale.
- <a id="master-detail-split"></a>**Master-detail split = `Grid ColumnDefinitions="*,Auto,1.4*"` + a
  `GridSplitter` in the `Auto` column** (CSVG integration, `the design notes in git history`
  §4.2/§7.2; **first + only app consumer is the Highlights tab**, `Views/Highlights/HighlightsTabView.axaml`,
  the Library having no details-pane precedent). List left (`*`), detail right (`1.4*`, content-denser).
  **Responsive rule (mandatory):** below a width breakpoint (~760px) it **collapses to a single column**
  showing list *or* detail (a `◀ Back to list` affordance returns), never a clipped both, driven by a bound
  `IsNarrow`/`ShowDetailPane` VM flag (reactive, not code-behind), the same responsive discipline as the
  horizontal-strip rule below. **All** filter / selection / expansion / scroll / splitter-position state lives
  in the **tab VM** (the View is torn down per `WorkspaceTabDescriptor` deactivation) and persists via
  `SnapshotState`/`RestoreState`.
  - **As-built collapse mechanics (Reels dashboard):** one `Grid`, not two stacked layouts. The tray
    `DockPanel` (`Grid.Column=0`) and the config `ScrollViewer` bind **`Grid.ColumnSpan` / `Grid.Column` to VM
    ints** (`TrayColumnSpan`, `ConfigColumn`, `ConfigColumnSpan`, narrow ⇒ span 3 / column 0), and each
    pane's `IsVisible` binds a VM bool (`TrayVisible`/`ConfigVisible`/`SplitterVisible`), so exactly one pane
    fills all three columns when narrow, both share the split when wide, with **no duplicated subtree**. The
    two star `ColumnDefinition.Width`s are `Mode=TwoWay`-bound to VM `GridLength` props, so a `GridSplitter`
    drag writes the ratio straight back to the VM for `SnapshotState` (persist the star weights, not pixels).
    A root `SizeChanged` → `SetViewportWidth` sets `IsNarrow`. ⚠ **v0.5.3 inverts the weights** (`1.4*` tray /
    `1*` config) and lands narrow on the TRAY, because the content-dense pane is on the left here, see
    [Reels dashboard](#reels-dashboard-v053).
  - ~~**Master list reuses the Library card machinery**~~. **REMOVED in v0.5.3.** The demo card grid and its
    chunked `HighlightCardRow` / `SetCardColumns` machinery are gone; that chunking existed only because
    `WrapPanel` has no virtualizing counterpart, and a flat tray list virtualizes trivially. The Library card
    machinery (`MapRadarConverter`, `LibraryCard*` overlay palette) is no longer used by this tab; the tray
    keeps only `MapAccentConverter` for its provenance dot. `DisplayText.Sanitize` on every rendered player
    name still holds (hostile bidi/combining-mark names crash the wrap splitter).
- **Section header = `Border.sectionHeader` + `TextBlock.sectionLabel`** (shared in `Components.axaml`).
- **Card list = `InspectorCard` per item**; **key/value = `KeyValueTable`**; **hex = `BinaryPane`**.
- **Empty state is per-tab, not global.** The old global no-file overlay collided with tab-local
  content (Library needs no demo; Stats has its own empty state) and was removed. Each tab owns its
  empty state. **The Library tab owns the primary landing** (see below); the Parser tab's no-file state
  is now a lightweight *pointer* ("No demo loaded — open one from the Library tab or the toolbar", still
  wired to `OpenFileCommand`) rather than a competing second open surface (P3.2b).
- <a id="landing-hero"></a>**Landing hero = the Library empty state (`Views/Library/LibraryTabView.axaml`,
  P3.2b).** When no folders are configured (`HasNoFolders`), the Library body is a **centered
  `Border.card` hero**: app title (`TextValue`) + tagline (`TextMid`) → a big **`Button.primary` "Open Demo…"**
  (`OpenDemoCommand`, the shared picker funnel) → a **`RECENT` list** (`TextBlock.col-label` + one clickable
  row per `RecentFileItem`, `OpenRecentCommand`) → a `Button.ghost` "+ Add a folder" → the drop hint. Recent
  rows show `FileName` (mono/`TextBright`) over `Meta` ("&lt;map&gt; · &lt;opened age&gt;", `TextDim`) on a
  subtle `PanelHeaderBg` inset so they read as tappable; a **missing file (`Exists==false`) renders at
  `RowOpacity` 0.4** and clicking it prunes it (the command handles the stale case). The recents list is
  `ScrollViewer`-capped (`MaxHeight`) so ≤10 recents never overflow the card. **The dense filter toolbar
  (search/map/player/sort/view/refresh) is gated `IsVisible="{Binding !HasNoFolders}"`** so the hero state
  shows only `LIBRARY` + `+ Add folder` above the card, not a wall of inert dropdowns.
  - <a id="library-card-score-repair"></a>**Card score badge has TWO states (v0.5.3 §9.8).** The scoreboard
    badge (`HasScore`) and a `score ?` badge (`NeedsScoreRepair`) share the same top-right slot and are
    mutually exclusive (`NeedsScoreRepair` ANDs `!HasScore`). Both reuse `LibraryCardBadgeBg`; the repair
    state is `LibraryCardTextFaint` at 11px. **Zero new tokens.** It exists because an absent score is
    otherwise SILENT: `HasScore` needs both sides, so a withheld half-score renders identically to a demo
    whose score genuinely cannot be resolved (warmup, truncated). Since re-deriving is on-demand (it costs a
    full parse per demo), hundreds of cards would sit there quietly badge-less with nothing saying why.
    **State is word-carried** (`score ?` + tooltip), not colour-only, per the same WCAG 1.4.1 rule the
    `StatusChip` dots follow. The paired action is a toolbar button (`ScoreRepairLabel` /
    `RepairScoresCommand`), `IsVisible`-bound to a non-zero count so it is absent in the normal case.
  - **Z-order gotcha (verified P3.2b):** the card/list bodies (`IsCardView` defaults **true**) stay realized
    even in the empty state, and a `ListBox.card-grid`'s `Background="Transparent"` **is hit-testable**
    in Avalonia (only `null` isn't). So the hero **must be the LAST child** of the body `Panel` (document
    order = topmost hit-test), or the empty ListBox swallows the CTA/recents clicks. **`ZIndex` alone did
    NOT reorder hit-testing here**: declare it last. Regression-guarded by a `GetVisualAt`(button-centre)
    assertion in `ZLibraryRenderTests` (do **not** gate the card/list bodies off, or `LargeLibrary_Realizes…`
    breaks; it injects 400 entries with no folders).
- **One landing surface, superset-safe.** When the library IS populated, the hero is replaced by the
  folder browser (unchanged), and a **persistent compact `Open Demo…` (`.primary`) + `Recent ▾`
  (`.chip` flyout, gated `ShowHeaderRecents = HasRecentFiles && !HasNoFolders`)** dock **right** on the
  count/folder-chips strip (now a `DockPanel`) so single-file open + recents stay reachable while browsing.
  Net: exactly one prominent Open-Demo per state (hero primary when empty, compact when populated) + the
  always-present toolbar button: no duplicate CTAs.
- <a id="drop-target"></a>**File-drop receive target (Avalonia 11.3 `IDataTransfer` API).** To accept a
  dropped file: set `DragDrop.AllowDrop="True"` on the tab root and `AddHandler(DragDrop.DragOverEvent/
  DragLeaveEvent/DropEvent, …)` in code-behind (drop handlers are a view concern; they only route to a VM
  command). Read files with the **new** `e.DataTransfer?.TryGetFiles()` + `DataFormat.File`, **not** the
  now-obsolete `e.Data.GetFiles()`/`DataFormats.Files` (CS0618 → error under analyzers-as-errors). Resolve a
  local path via `IStorageFile.TryGetLocalPath()`. The drag-over affordance is a **full-surface overlay**
  (scrim = `ShellBg` at `Opacity 0.82` + an `AccentInteractive` 2px framed "Drop …" prompt, `IsHitTestVisible=
  False`) bound to a VM `IsDragOver` bool the handlers toggle, a plain reactive binding (so a capture variant
  can render the drag-over look headlessly; a real drag can't be synthesized off-display). **WASM guard:**
  `TryGetFiles` is desktop-only, so the handlers no-op and the overlay + drop-hint are suppressed via a
  get-only `CanDropFiles = !OperatingSystem.IsBrowser()`.
- <a id="responsive-strip"></a>**Responsive horizontal strips MUST wrap or scroll.** A non-wrapping
  horizontal `StackPanel` of grouped controls clips its trailing groups at narrow width. Approved fixes,
  in preference order: (1) put trailing/optional groups in a right-aligned region that collapses into an
  **overflow `▾` flyout** below a breakpoint width; (2) `WrapPanel` the groups so they reflow to a second
  row; (3) horizontal `ScrollViewer`. Gating a group away (e.g. BREAKPOINT for consumers) also relieves
  width pressure.
  - **Applied (P3.1, NavStrip):** the pattern is a **`DockPanel`**: right-dock the trailing
    optional/critical group (TO-BREAKPOINT) so it structurally cannot clip, pin the critical-left group
    (CLOCK) `Dock="Left"`, and put the middle group (JUMP) in a fill `ScrollViewer` that scrolls rather
    than clips. This beat the overflow-`▾` flyout **here** because only one ~40px button overflows at the
    880px floor (scroll keeps 6/7 buttons directly clickable; a flyout would hide all seven) **and** a
    flyout needs a second copy of the JUMP buttons → duplicate `x:Name`s/bindings, which the "byte-identical
    behavior" constraint forbids. Right-dock (not flyout-collapse) the breakpoint cluster also keeps it
    "visually separate on purpose" (§2/§3). See [NavStrip → Responsive layout](#navstrip-responsive).
  - **Applied (Library filter toolbar):** the populated Library toolbar
    (`Views/Library/LibraryTabView.axaml`) uses **option (2), a fill `WrapPanel`**: was a fixed 9-column
    `Grid` (`Auto,Auto,*,Auto,…`) that crowded/clipped the search box + map chip + view toggle at ~700px.
    Now the `LIBRARY` section label is pinned `Dock="Left"` and every interactive control lives in a fill
    `WrapPanel`; the `map/player/sort/clear` and `view/refresh` clusters are each wrapped in a `StackPanel`
    so they **reflow as units** (never split mid-group) to a second row when the strip is too narrow.
    `Border.sectionHeader` has no fixed height, so the band grows to the wrapped row. **Chosen over the
    NavStrip DockPanel+ScrollViewer** because a horizontal scrollbar reads clunky in a filter toolbar and a
    scrolled search box loses its width; wrapping keeps the search box usably wide on its own row. The
    stretch-vs-wrap tension is real (a `WrapPanel` child can't stretch to fill), so search takes a
    `MinWidth=220`/`MaxWidth=360` instead of the Grid's `*`. Verified `library-populated` at 700/900/1200
    (700 = 2 rows, nothing clips) + `library-landing` (collapsed filters leave no gap). See
    [D35](#library-toolbar-reflow).
  - **Applied (Playback2D viewport chrome, D4):** all three options at once, plus the one this rule does not
    cover. Option (1) collapses the six overlay toggles behind an `Overlays ▾` toggle: **inline**, not a
    flyout, because a popup leaves the viewport column (where the layout suite measures) and the tab's
    tunnelling `KeyDown` handler (where `Space` still has to mean play). Option (2) `WrapPanel`s both the
    tool row and the overlay row. The header's `Export…`/`Overlays ▾`/collapse cluster right-docks and is
    declared first, the NavStrip's structural reservation. What the rule *cannot* say is the part that
    mattered most: the strip was over the CANVAS, so it moved into its own docked `Auto` row. See
    [the viewport chrome contract](#playback2d-viewport-chrome).

<a id="match-overview-page"></a>
> **SUPERSEDED (v0.5.3, redesign plan step 5): Match Overview is now THE PER-DEMO PAGE, not a landing
> page.** The section below documents the v0.5.1 landing layout (single 920px column, separate identity
> hero / 104px stage card / score card). It is kept because its *principles* still hold verbatim:
> skeleton-first, reserved slots, centered max-width column, team colour as a redundant non-text cue,
> computed contrast, but the **structure** has changed. See
> [Match Overview redesign](#match-overview-redesign-v053) directly below for what shipped.

<a id="match-overview-redesign-v053"></a>
### Match Overview redesign: the per-demo page (v0.5.3, plan `highlights-matchoverview-redesign.md` §3)

**Two jobs, one layout.** `OverviewMode { Empty, Live, Cached }`. **Live** is the push-fed landing (shell
calls `BeginOpening` → `SetSummary` → `BeginAnalysis` → `SetAnalysis`). **Cached** renders any indexed demo
from a `DemoCacheRecord` via `SetCachedRecord(record)` and **starts nothing**: no parse, no header read, no
queue. Because both modes paint the same sections from the same reserved slots, **opening a demo you were
previewing has no visual discontinuity: the cached render IS the skeleton the live fill lands into.** That
is a gate (`CachedRender_ReservesTheSameHeight_AsTheLivePage`, every tier × both widths).

- **Structure.** Merged **hero band** (fixed `MinHeight 168`: identity + score plate + completeness chip +
  a 3px progress rail + the three stage words) → **facts strip** → **two-column body** at ≥1000px
  (`ColumnDefinitions="1.3*,20,*"`, "the match" = rosters + scoreboard + side split + explore CTAs · "the
  moments" = highlights + the enrichment slot). Replaces the v0.5.1 single column; saves **318px of scroll**
  at wide widths (1216 → 898 measured).
- **Responsive collapse = ONE Grid, bound ints** (the Highlights master-detail precedent, no duplicated
  subtree): the moments group binds `Grid.Row`/`Grid.Column`/`Grid.ColumnSpan` to `MomentsRow`/
  `MomentsColumn`/`MomentsColumnSpan`, the match group binds `MatchColumnSpan`. Breakpoint 1000px, driven by
  the view's `SizeChanged` → `SetViewportWidth` (code-behind touches no control).
- <a id="completeness-chip"></a>**The completeness chip: the answer to partial fill.** With tier 3 opt-in,
  ~99% of a real library has rosters + score but no scoreboard, so the page **names the tier it has** and
  offers **one action**: `LIVE` · `FULL` · `INDEXED · stats not computed` · `NOT INDEXED` · `INDEX FAILED`,
  with `Compute full stats` / `Index this demo` / `Retry`. Reuses the shared **`Ellipse.dot.*`** classes
  (bound class → token, never a VM brush), so it re-themes free; **`INDEXED` is the HOLLOW good ring**: the
  established "partial / not the whole story" treatment. **Zero new tokens.** The label is never tinted by
  state (WCAG 1.4.1, the dot is the redundant cue).
- **Empty-slot rule.** A slot whose tier is missing shows **one sentence naming the tier + that same
  action**: never a wall of `—`, which on a cached page reads as "still loading" about something where
  nothing is running. `—` stays correct for a *single* value inside an otherwise-populated card.
  `RosterMessage` / `PlayerStatsMessage` / `HighlightsMessage` all render *inside* the reserved slot.
- **Contrast (computed, not eyeballed).** `.slotMessage` is **`TextBright`** (7.17:1 dark / 9.89:1 light),
  it started as `TextDim`, which measures **1.81:1 on `CardBg` in Dark**, the same defect that already moved
  this page's fact micro-labels off `TextDim`. In an empty card that sentence IS the primary content.
  `.hlMeta` is `TextMid` (4.26:1) not `TextFrameInfo` (3.48:1). Verified by render across dark / light /
  high-contrast.
- **Honesty properties, each gated.** (1) A cached page leaves **every stage pending at `Progress = 0`**:
  nothing ran, and marking the strip done would claim a pipeline that never executed; the chip carries the
  state. (2) `Accepts()` requires **`Mode == Live`** as well as a matching `SubjectKey`, so a live push
  cannot land on a cached page even when the keys agree. (3) A **migrated** row (names, no teams) shows
  "Team split needs a re-index" and the badges hold the placeholder rather than assert `0`, hence
  **`HasRoster`** as a gate distinct from `HasSummary`. (4) The explore CTAs are **mode-gated**: from a
  preview, Stats/2D hold a different demo, so the honest offer is `Open this demo`.
- **Highlight section.** Groups join `record.Highlights` → `record.Players` **by SLOT** (the unified record
  stores highlights by slot rather than repeating a name per event). Ordered **most moments first**:
  deliberately *not* the scoreboard's CT-block ordering, because this is a "what happened" list. Every
  rendered name goes through `DisplayText.Sanitize`. Verify-live is present per `chrome.livesync` but
  **`CanVerify` is false in cached mode** (the page's demo is not the one CS2 loaded), the Highlights tab's
  demo-identity rule arriving for free.
- **Reserved metrics (re-measured; `MatchOverviewLandingTests` asserts them against real layout):** hero band
  168 · hero action row 32 (**added**: the cached/failed-only buttons grew the band ~16px on a mode switch,
  which is the same defect as a load that moves the page) · roster cards 188 · scoreboard 268 · highlights
  300. Content height **898 wide / 1216 narrow**, identical across all three load states and all three
  cached tiers.
- **Capture variants:** `match-overview-{opening,parsed,ready,failed,spectators}` (live) +
  `match-overview-cached-{header,indexed,full,nosplit,failed}`. Drive width from the CLI to exercise the
  collapse (`--size 1400x1000` vs `--size 820x1400`).
- **Injected delegates (shell wiring pending):** `computeFullStats(path)`, `openDemo(path)`, `returnToLive`,
  `verifyMoment`, `isVerifyPresent`, plus the settable `LiveDemoName` that drives `◀ Back`. All null-safe:
  an absent delegate makes the affordance absent, never inert.

<a id="match-overview-landing"></a>**[HISTORICAL: v0.5.1 landing layout, superseded above.] Match Overview landing page (feature/v0.5.1-guide, 2026-07-24).** The
consumer demo-landing tab (`Views/MatchOverview/MatchOverviewTabView.axaml`, VM
`ViewModels/MatchOverview/MatchOverviewTabViewModel.cs`): shown the **instant** a demo opens, before the
multi-second parse, so a double-click has immediate visible feedback. Progressive fill: **identity hero →
loading → parsed summary** (or **failure**). Pattern rules, all reused elsewhere:
- **Centered max-width content column** (not stranded top-left): the body is a `ScrollViewer` →
  `StackPanel MaxWidth="920" HorizontalAlignment="Stretch"`. **Stretch + MaxWidth centers in Avalonia**
  (verified: at ≥920 the column centers with children stretched to 920; at <920 it fills, so it scales down
  without clipping, rendered at 720/1100). This is the reusable "centered document column" idiom; use it
  over `HorizontalAlignment="Center"` (which shrinks to content width, the pre-polish bug).
- **Identity hero shown in EVERY state** (loading/failure/summary all keep it): eyebrow (`TextMid` mono
  "MATCH OVERVIEW") → big map name (`TextCardHeader` 34/Bold) → a short `Rectangle.heroAccent` brand rule
  (`AccentInteractive`, 52×3) → server (`TextMid`) over file (`TextDim` mono). This satisfies the VM's
  "identity ASAP" contract.
- **Loading = calm liveness, not a spinner:** a pulsing `Ellipse.dot.stateWorking.pulsing` (the shared
  StatusChip dot) beside the `StatusText`, over a determinate `ProgressBar` (`Foreground=AccentInteractive`,
  `Background=PanelHeaderBg`). The dot carries "we're on it" through the parse black box; the bar carries the
  VM's coarse discrete `Progress` jumps. Honest (real progress) + alive (pulse), chosen over an
  indeterminate marquee.
- **Rosters carry team identity on the header's coloured LEFT strip + a team dot, NEVER header text.**
  `Border.rosterHeader{,.ct}` (left border 3px, `.ct`→`AccentInteractive`, base→`AccentAmber`; on
  `PanelHeaderBg`) + `Ellipse.teamDot{,.ct}`. Header text stays neutral `TextCardHeader`. **Why:** CT
  `AccentInteractive` #5050A0 as text on `CardBg` is ~2.5:1 (fails AA); the brighter blues that would pass
  (`Pb2dTeamCt`, `LibraryCardScoreCt`) are **domain-walled** and forbidden here. So team colour is the
  **redundant, non-text cue** and the word ("Counter-Terrorists"/"Terrorists") is the primary carrier, the
  same principle as [StatusChip](#statuschip). CT=`AccentInteractive`,
  T=`AccentAmber` is the app scoreboard convention (mirrors Stats' `teamHeader.ct`, which shares the same
  latent text-contrast issue this page deliberately avoids). **Accepted:** the CT strip/dot land at 2.72:1 on
  `PanelHeaderBg` in Dark (just under the 3:1 non-text bar), matches Stats, brighter blue is domain-walled,
  and the strip is not the sole cue. Bots get a `Border.botTag` outlined chip (`TextMid`, `BorderStrong`).
- **Contrast was COMPUTED across dark/light/high-contrast** (not eyeballed), HC passes everything; the fact
  micro-labels + eyebrow were moved `TextDim`→`TextMid` because `TextDim` labels that *name data* land at
  ~1.8–2.0:1 in Dark (the whisper-label convention is for decorative labels, not informational ones).
- **Quick-facts** = a `Border.card` with a `*,Auto,*,Auto,*` grid (`Rectangle.divider` verticals): scales,
  never clips. **CTAs** in a `WrapPanel` (`Button.primary` "View full stats" + `Button.chip` "Watch in 2D")
  so they reflow at narrow width. Capture variants: `match-overview-{loading,ready,failed}` (`Variants.cs`;
  the `ready` mock flips one player per side to `IsBot` so the BOT tag is actually verified).
- **Follow-up (noted, no VM change):** the summary has **no final score** yet (VM defers it). When added, a
  score belongs in/next to the quick-facts bar or as a hero sub-line: design around map+rosters+facts until
  then.

<a id="reels-dashboard-v053"></a>
### Reels dashboard: the Highlights tab, promoted (v0.5.3, plan `highlights-matchoverview-redesign.md` §4)

`Views/Highlights/HighlightsTabView.axaml` + `ViewModels/Highlights/HighlightsTabViewModel.cs`.
The library-wide demo **card grid is gone**; the tab is an *authoring* surface: an ordered **clip tray**
(left) + the **promoted reel config pane** (right) + an inline job strip + the §7 enrichment slot. Header
reads **"Reels"**: ⚠ `TabId "highlights.browser"` and feature id `"tab.highlights"` are **persisted keys**
and did NOT change; a header rename that touched either would silently reset users' tab state and feature
overrides.

- **Layout = the shipped master-detail pattern with the weights INVERTED.** `1.4*, Auto, 1*` +
  `GridSplitter`, same `SetViewportWidth`/`IsNarrow` collapse below 760px, same VM-held column-span
  mechanics, but the **tray** is the content-dense pane and the **landing** pane when narrow (the browser
  layout this pattern came from lands on the master list; the drill-in here is *into settings*, out via
  "◀ Back to clips"). **No new layout pattern was minted.**
- **The tray IS the plan: one computation, never two.** `ReelConfig.ClipGroups` is rendered directly by the
  tray. That is deliberate: the redesign's headline argument for promoting the modal is that coalescing
  feedback becomes visible *while* you build, and a parallel tray model could disagree with the plan it
  claims to describe. `CLIPS (7 staged · 5 after merge)` and the per-row `→ merged clip:` line therefore
  cost nothing extra.
- **Order lives in the TAB, not on the group VMs.** `HighlightsTabViewModel` holds
  `Dictionary<HighlightKey, HighlightSelection>` (O(1) `IsStaged` for Match Overview's `[ + ]`) **plus** a
  `List<HighlightKey>` order. `ClipGroups` is cleared and rebuilt on every lead-in/lead-out keystroke, so a
  ▲▼ that mutated the group objects would lose the user's arrangement mid-edit.
- **⚠ Reorder affects OUTPUT SEQUENCE ONLY.** Emission is sorted by *group first-appearance*, then
  `StartTick`. `ClipWindows.Coalesce` is untouched and stays order-independent, wiring order into it would
  make merge behaviour position-dependent, so two identical trays would render differently. **Verified end to
  end:** the ordered `_plan` → `ReelRequest.Clips` → `ReelJobService`'s index loop and
  `BuildCompilation`'s `request.Clips.Select(...)` → `Cs2Compilation.Clips`, with no re-sort anywhere in
  this repo, and the shipped `Cs2VideoGenerator.Core` XML docs close the last hop: `Cs2Compilation.Clips` is
  *"Ordered list of clips to capture. Processed sequentially"* and `ConcatenateClips` *"uses FFmpeg to combine
  clips in order."* Tray order is therefore reel order, all the way to the file.
- **Group-level ▲▼✕ + clip-level ✕** (not per-clip reorder). It matches the wireframe, and it has a
  technical reason worth keeping: `ReelJobService` issues a `LoadDemoAsync` only when `clip.DemoPath` changes
  between clips, so keeping each demo's clips contiguous avoids multiplying the most expensive step of a
  render. `MoveGroupTo` re-normalises the whole order list group-by-group to preserve that invariant however
  the tray was assembled. Drag-and-drop is the same operation (`Avalonia 11.3 DataTransfer` /
  `DoDragDropAsync`); the ▲▼ buttons are the keyboard-reachable path.
- **Provenance is mandatory and always visible** (a 12-clip cross-demo tray is otherwise unreadable): map
  accent dot (`MapAccentConverter`, bound converter, never a code-held brush) + map + demo file name +
  round + player + tick + estimated window + tray position.
- **Pre-flight at STAGING time.** `⚠ demo moved` renders on the clip the moment it is staged, not at
  Generate. Tinted `AccentError` **glyph** + neutral `TextMid` body: the established idiom (tinted body
  copy fails AA on the card surface under Light).
- **Two different emptinesses, never conflated.** "No clips staged" is always primary; *"Your library isn't
  indexed…"* + `Scan my library` is a SECONDARY line shown only when `AnyHighlightsIndexed` is false. Sending
  a user who already has highlights to a full library re-scan is the trap the plan's §5 row 5 names.
- **The empty page must not shout at itself.** `ReelConfig.ShowErrorBanner` (= `HasError && HasClips`) is
  split from `HasError` (which still gates Generate): the first render put a calm "No clips staged yet." card
  and a red "⚠ nothing to render" banner on the same screen. `ClipsHeader` collapses to a bare `CLIPS` at
  zero staged for the same reason.
- **Contrast, computed.** Tray metadata is **`TextMid`** (4.26:1 dark on `CardBg` / 7.37:1 light), matching
  the Match Overview `.hlMeta` precedent. It started as `TextDim` (**1.81:1 on `CardBg`**, 2.04:1 on
  `ShellBg`), the third recorded instance of that same token defect: **`TextDim` is for disabled/placeholder
  text, never for metadata a user reads.** Clip durations moved off `TextFrameInfo` (3.48:1, large-text
  only). The tray header uses a local `.trayHeader` (`col-label` metrics, `TextMid`) because `col-label`'s
  `TextLabelAlt` is **1.63:1**: fine for a decorative "OUTPUT" heading, illegible for the live coalescing
  count. `TextDim` survives in exactly one place: the drag grip, which is decorative and goes full-opacity on
  hover.
- **Long-lived-object hazards the promotion created** (the modal was per-invocation; this pane is not):
  the §9 interlock latch **re-arms** after every Generate, the output name **re-seeds** from the tray while
  it is still the suggested one, and **Generate is disabled while a job runs** (the shell used to prevent a
  second dialog from opening; an embedded pane has no open gate).
- **The tray holds cache-record OBJECTS, so it re-resolves on every store `Changed`.** `RefreshStagedAgainstStore`
  re-looks-up every staged key and drops what is gone with a one-line note. Without it a rescan would leave
  every window computing against a detached snapshot's tickRate / tickCount / rounds, and a demo deleted while
  the tab is open would still render as fine. ⚠ `DemoCacheStore` hands back a **fresh `DemoCacheRecord` per
  read**, so records are never comparable by reference; the tray learned this the hard way and compares
  `WindowInputs` by value. It early-outs when nothing changed: `Reproject` runs on every store `Changed`, i.e. repeatedly
  through a backfill, and rebuilding `ClipGroups` each time would tear the tray down under the user's pointer.
- **Enrichment slot (§7):** `EnrichmentSections` (`ObservableCollection<object>` → `ViewLocator`), registered
  **at composition only**, container gated on `HasEnrichments` so it is **zero height** when empty, gating
  the `ItemsControl` alone still contributes the parent `StackPanel`'s spacing. The collection **self-notifies**
  (`CollectionChanged` → raise `HasEnrichments`): an enrichment that registers and renders zero height because
  someone forgot an explicit raise is invisible by construction.
- **Shared-style fix this pass:** `Button.ghost:disabled` only set `Opacity`, so Fluent's own
  `:disabled` ContentPresenter setter painted `ButtonBackgroundDisabled`: a disabled ghost button grew a
  filled box that read as SELECTED. Now flattened on the ContentPresenter (`Styles/Primitives.axaml`).
- **⚠ Per-demo rescan lost its UI entry point.** `RescanDemoCommand` went with the card grid. Plan §5 rows
  3/4 name Match Overview's highlight header / completeness chip as the new home, but what step 5 actually
  built there is **`Compute full stats`** (an interactive enqueue on `IDemoProcessingQueue`), which supersedes
  a bare rescan: it fans out to every evaluator and fills parse gaps, scoreboard and highlights together.
  Net effect: `HighlightScanService.RequestScan(path)` is now reachable from no UI except `⟳ Rescan all`.
  Defensible and deliberate, recorded rather than left to be inferred from a table that promises a
  replacement. **Partly repaid in step 9:** the scan chip's `Retry all failed` calls `RequestScan(path)` per
  `Failed` row, so a failed scan is recoverable without re-harvesting the whole library. A *staleness*
  rescan of one arbitrary demo still has no button. `Compute full stats` on Match Overview is the path.
- **Not verified headlessly:** drag-to-reorder. The ▲▼ path is unit-tested end to end (tray → plan →
  `ReelRequest.Clips`); the drag gesture is an input protocol the headless host cannot drive. Both call the
  same `MoveGroupTo`.
- **Owed debt:** `HighlightReelDialogViewModel` / `HighlightReelDialogView` are no longer a dialog. The names
  are retained because a test guards the `ViewLocator` name mapping and the `--treenode-filter` gate is
  literal; rename to `ReelConfig*` when that gate is next touched.
- **Capture variants:** `highlights-{populated,empty,empty-library,moved,job,narrow}` (`Variants.cs`).
  Verified across **dark / light / high-contrast / e-girl**.

<a id="addclips-picker"></a>
#### Add-clips picker: the cross-demo entry point (v0.5.3 step 9, plan §4.4)

`Views/Highlights/AddClipsPickerView.axaml` + `ViewModels/Highlights/AddClipsPickerViewModel.cs`
(`AddClipsRowViewModel` per row). A flat, **virtualized highlight-ROW list** over every cached demo: the
reason multi-demo reels still work with the card grid gone. **The unit of work is a clip, not a demo**, which
is also why the chunked `CardRow` machinery is gone: it existed only because `WrapPanel` has no virtualizing
counterpart, a constraint that disappears with the grid.

- **An OVERLAY inside the tab, not a window.** A second window needs `IWindowService` (the surface the reel
  modal's retirement is stripping), and is unreachable on the browser host. It follows the shipped
  scrim idiom (MainView's Settings / first-run overlays): `ShellBg` at `Opacity 0.8` on its **own layer** so
  the card stays full opacity. **Zero new tokens.** Escape and a scrim click dismiss (both are gestures with
  no bindable command surface, the only code-behind).
- **The four orphaned filters live here** (plan §5 row 6), reusing `PlayerFilterItem` /
  `HighlightTypeFilterItem` / `MapFilterItem` (the last from `ViewModels/Library`) and the card grid's flyout
  markup verbatim, re-pointed at highlight rows. Filters **intersect** (AND). `Clear` + the §5 row-10
  no-filter-match empty state ship with the original copy.
- **Coverage counted by EVENTS, not `AnalysisState`.** A demo appears when `Highlights.Count > 0`. The
  measured library is 346/348 `Pending` with 267 events present (a re-queued row keeps its previous harvest), so
  counting `Indexed` would print "0 analysed demos" above hundreds of visible rows, the self-contradicting
  page the step-5 review caught. The footer's *"M of N cached demos"* clause is emitted only when `N > M`.
  ⚠ The wireframe's *"Only demos with full stats appear here"* is **false** under this definition and was
  replaced with *"Only demos that have been analysed for highlights appear here."*
- **Rows are SNAPSHOTTED at open; staged flags are the one live exception.** A backfill raises
  `DemoCacheStore.Changed` every few seconds, and re-projecting under an open picker resets scroll and
  wipes the multi-select mid-assembly. A picker is transient, so the honest answer is a snapshot plus a
  footer note when a scan is queued, not the always-on card grid's `SameProjection` stale-guard. Staged
  flags push live through the tab's single tray funnel (`PushTray` → `Picker.SyncStagedFlags`), so
  un-staging in the tray flips an open picker row from `✓` back to `+`.
- **Both interaction states live on the ROW view-model** (`IsStaged`, `IsPicked`), never on the container:
  the list virtualizes and `VirtualizingStackPanel` recycles, so container-held picks evaporate on scroll.
  Picks deliberately survive a filter change: a user who ticked three rows and then filtered meant all
  three. `AddSelected` stages the batch in **one** `StageRange` push (twenty adds would otherwise be twenty
  full plan recomputes and twenty `ClipGroups` rebuilds).
- **`[ + ]` / `[ ✓ ]` is an outlined button, and staged is ENABLED** (`StatPositive` outline; clicking
  removes). Never `ghost:disabled`: that reads as SELECTED, the defect already fixed once in
  `Primitives.axaml`. A `ghost` (borderless) variant was tried first and read as a bare glyph, not a control.
- **Layout defects the capture loop caught** (all fixed, both worth remembering):
  1. **Fixed-px columns overflowed the row below ~700px**: the star column collapsed to zero and the title
     painted through the round, duration and button. Text columns are star-sized now (`Auto,Auto,Auto,1.2*,2*,Auto,Auto,Auto`).
  2. **`TextTrimming` inside a horizontal `StackPanel` never engages**: a StackPanel measures children with
     infinite width in the stacking direction. The title needed a `Grid Auto,*`.
- **Surface + contrast.** Body is **`CardBg`** (not `PanelBg`, which on Light is within a few points of the
  scrimmed shell and left the overlay edgeless) + `BorderAccent`, the documented near-white-card-on-light-shell
  pairing. Bands are `PanelHeaderBg`. Computed on `CardBg`: `TextValue` 10.14/12.23, `TextMid` 4.26/7.37,
  `TextBright` 7.17/9.89, `StatPositive` 6.37/5.00 (dark/light), all AA. The overlay title uses a local
  `.pickTitle` (`sectionLabel` metrics, `TextValue`) because `sectionLabel`'s `TextLabel` is **1.54:1** on the
  header band: fine for a decorative "OUTPUT" caption, illegible for the title of the thing that just covered
  the screen, the same substitution `.trayHeader` already makes.
- **Filtering early-outs on an unchanged result.** Rows are stable instances, so the new filtered sequence is
  compared by reference and the `ObservableCollection` is left alone when it matches. At 240 rows a
  `Clear` + N `Add` re-realizes every virtualized container **and resets the user's scroll mid-search**,
  and the common keystroke (one that narrows nothing, or a backspace back to a set already shown) produces
  exactly that no-op.
- **Capture variants:** `addclips-{populated,staged,nofiltermatch,nothing-indexed,narrow,dense}`. All but
  `dense` open over an **empty tray** on purpose: a picker opened over a full tray shows every row already
  staged, hiding the `[ + ]` resting state the surface is built around. Verified across **dark / light /
  high-contrast / e-girl**.
  - **`addclips-dense` (240 rows over 8 demos) is the one that verifies the density claim.** Four mock rows
    prove nothing about virtualization; the dense render confirms the `VirtualizingStackPanel` engages, the
    overlay scrollbar does **not** shift the columns (it draws over the row, so the `[ + ]` stays put), and
    the footer stays pinned while the list scrolls under it.

### Capture / screenshot-review notes
- Tool: `dotnet run --project src/App/DemoViewer.NET.UiCapture -c Release -- <variant> [--out] [--size WxH]`;
  `ab <a> <b>` for side-by-side; `list` to enumerate. Output → `%TEMP%/demoviewer-uitests/`.
- **Real controls DO capture headlessly** when their DataContext is a plain VM. Proven this pass:
  `navstrip-real` renders the real `NavStrip` bound to `new MainViewModel { HasFile = true }`
  (all ctor params optional). Use real controls for "current state" everywhere **except** MSAGL views.
- **Do NOT render full `MainView`**: its SplitView+TabControl realizes the startup tab (Library scan;
  risk of the MSAGL Analysis view). Mock just the panel/row under study.
- **Convention:** `current` half of an A/B = the real control; `proposed` half = a hand-built variant
  in `Variants.cs` using the **same tokens/style-classes**, so the diff is layout, not styling drift.

---

## 5. Category-visibility matrix (feeds the P1.2 feature gate, NOT ad-hoc `IsVisible`)

Superset rule: **developer ⊇ power-user ⊇ consumer**. The category sets *defaults only*; every gated
feature stays user-toggleable in Settings. `●` default-visible, `○` default-hidden (enableable),
`R` = Required (always on). Recommendation for the gate's `FeatureDescriptor.Defaults`.

| Feature / surface | Scope | Consumer | Power | Dev | Notes |
|---|---|:-:|:-:|:-:|---|
| **Library** tab | Tab | R | R | R | Landing tab; needs no demo. |
| **Match Overview** tab | Tab | ● | ● | ● | **THE per-demo page (v0.5.3)**, two modes: *Live* (shown the instant a demo opens, before parse, so a double-click has instant feedback) and *Cached* (any indexed demo rendered from `DemoCacheRecord`, parse-free). A completeness chip names the tier and offers the one action that advances it. See [Match Overview redesign](#match-overview-redesign-v053). Cached mode is desktop-only (the cache needs a filesystem), guarded at the **entry point**, never with an `IsBrowser` branch in the view. |
| **Stats** tab | Tab | ● | ● | ● | Player-facing scoreboard: core viewing. |
| **2D Playback** tab | Tab | ● | ● | ● | Core viewing; module tab (gate at registration). Its five v2 sub-features follow immediately below, as one contiguous `_catalog` block. They are the rows Settings renders indented under this one. |
| 2D Playback **`playback2d.annotations`** | SubFeature | ● | ● | ● | **Playback2D v2 (B2).** Draw and erase over the surface; static or clock-anchored ink. `ParentId "tab.playback2d"`, no `GroupId`, `Defaults(true, true, true)`. Default-ON for every category for the `tab.highlights` reason: gating a release's headline payoff away from the audience most excited by it is the wrong trade (B5 D6). Reached through `IModuleContext.Features`, never a `IFeatureGate` injected into the tab VM. Note the id is *also* the ink LAYER id (registry §3.3): deliberate, and pinned by `Playback2DFeatureWiringTests`. |
| 2D Playback **`playback2d.timeline`** | SubFeature | ● | ● | ● | **Playback2D v2 (A1).** The scrubbable round / kill / bomb timeline under the viewport. The VM folds gate AND has-demo into `IsVisible`; the row is `Auto`-sized, so an off gate leaves no layout hole. Per-track visibility is a *setting* (`Playback2D:TimelineShow*`), not a second gate. |
| 2D Playback **`playback2d.levels.auto`** | SubFeature | ● | ● | ● | **Playback2D v2 (B3).** Auto-switch the shown floor to the followed player's, with hysteresis. With it off, manual floor picking and the level strip stay available; the gate removes the automation, not the feature. |
| 2D Playback **`playback2d.follow`** | SubFeature | ● | ● | ● | **Playback2D v2 (A1).** Selecting a player card follows them in the 2D camera, and mirrors to CS2 through `NotifySpectateTarget` while Live Sync is active. The wording is always "requested". CS2 spectating has no readback. |
| 2D Playback **`playback2d.export`** | SubFeature | ● | ● | ● | **Playback2D v2 (B4). Desktop only (ANDs `!IsBrowser()`).** It writes a file and drives an ffmpeg subprocess. That AND lives in exactly ONE place, `ShellModuleFeatureGate.DesktopOnlyIds`, never re-derived per call site (B5 D4); the same treatment `chrome.livesync` and `chrome.processingQueue` get. On the browser head the Export affordance is absent from the toolbar entirely. Known cosmetic: this row's Settings toggle still shows the user's stored preference there, because the platform AND is folded one layer further out. See `docs/playback2d-v2/wasm-matrix.md`. |
| **Parser** tab | Tab | ○ | ● | ● | Needs wire-format mental model → power+. |
| **Entity Tracking** tab | Tab | ○ | ● | ● | Entity-layer knowledge → power+. |
| **Analysis Engine** tab | Tab | ○ | ● | ● | Rule-graph knowledge → power+. |
| **Authoring / RuleWorkbench** tab | Tab | ○ | ● | ● | Rule editing → power+; module tab. |
| **Diagnostics** tab | Tab | ○ | ○ | ● | Dev diagnostics. |
| Toolbar **Open Demo** | Chrome | R | R | R | Always reachable (see decisions). |
| Toolbar **Bookmark / Bookmarks** | Chrome | ● | ● | ● | Safe, general. |
| Toolbar **Parse Chain** | SubFeature | ○ | ● | ● | Parser deep-dive (`parserDeepDive` group). |
| Toolbar **Debugger** toggle | Chrome | ○ | ○ | ● | Dev chrome (`graphDebug` group). |
| Toolbar **Output** toggle | Chrome | ○ | ○ | ● | Dev chrome (unknown-msg/decode errors). |
| NavStrip **CLOCK** group | Chrome | ● | ● | ● | Core playback nav. |
| NavStrip **SEEK/EVENT** group | Chrome | ● | ● | ● | Event stepper + target chip (presets incl. Round), useful to all. Tick/frame nav is on CLOCK. |
| NavStrip **TO BREAKPOINT** group | Chrome | ○ | ○ | ● | Dev chrome (`graphDebug` group). |
| Parser **hex `BinaryPane`** | SubFeature | ○ | ○ | ● | Expensive; dev-only; clear buffers on disable. |
| Analysis **graph breakpoints** | SubFeature | ○ | ○ | ● | Dev-only (`graphDebug` group). |
| **Reels** tab (was Highlights) | Tab | ● | ● | ● | **Reel AUTHORING dashboard (v0.5.3)**: clip tray + reel config, no longer a browser; per-game highlight exploration is on Match Overview. Unchanged defaults: ux-design §2.2 reasoned that gating reel generation to power+ *"would hide the feature's headline payoff from the audience most excited by it"*. ⚠ Feature id stays **`tab.highlights`** and TabId stays **`highlights.browser`**: persisted keys; only the header string changed. Desktop full; WASM **registered + degraded** (fork 7) with authoring-specific copy. See [Reels dashboard](#reels-dashboard-v053). |
| Reels **`highlights.encoding`** | SubFeature | ○ | ● | ● | **Shipped v0.5.3 (step 9).** `ParentId: "tab.highlights"`, `Defaults(false, true, true)`, no `GroupId` (so the parserDeepDive / graphDebug leader-lock ordering is undisturbed). CRF / bitrate / FPS / container are OBS encoder knobs a consumer cannot reason about, the textbook hidden-but-enableable tier, hidden not removed: every category can switch it on in Settings. Consumer face: tray + Default/No-HUD + folder/name + Generate, which never routes through it. Consumed via `ReelConfig.IsEncodingVisible` (a gate seam, **not** an inline category check), applied from an optional `IFeatureGate` ctor arg **and re-applied on `IFeatureGate.Changed`**: a one-shot read would leave the section wrong after the user toggled it. Null gate (tests / UiCapture) ⇒ visible: a missing gate must never silently remove a section. |
| Match Overview **highlight section** | — | ● | ● | ● | **Binds the existing `tab.highlights` gate: do NOT mint a new id.** Disabling Reels should coherently remove both surfaces. Note the axis: a *feature gate* is user-initiated and stable for a whole load, so it is legitimate `IsVisible`; gating on **load state** would violate skeleton-first. |
| **Live Sync (CS2)** chip/flyout/2D-indicator/speed-lock/F2 Verify | Chrome | ○ | ○ | ● | `chrome.livesync`. **Desktop only** (ANDs `!IsBrowser()`). Documented fork: default-off for power-users because it temporarily modifies the real CS2 install + inferred states + DV-restart-relaunch papercut → beta-grade; one Settings toggle away. See ux-design §2.3. |
| **Processing queue** chip/flyout | Chrome | ○ | ● | ● | `chrome.processingQueue`. **Desktop only** (ANDs `!IsBrowser()`, background work needs a filesystem). Managing the background parse/analyse queue (pause/resume, per-item remove, status) is a power-user+ concern; **opening a demo never requires it** (foreground is always awaitable). The status chip appears only while the queue has activity or is paused (idle+empty adds no strip clutter). demo-processing-queue.md §12. |
| Reels **Generate reel** | (ungated) | ● | ● | ● | Marquee action, visible to all; guarded by the reel dialog + platform check + the single-CS2 interlock (ux-design §8/§9). Real: Win/Linux; dry-run: macOS; absent: WASM. |
| Reels **background scan** | setting | ○ | ○ | ○ | Opt-in for **all** categories (≈30 min churn on 200 demos), a Settings default, not a category default. Desktop only. |
| **Visual Walkthrough** (first-run tour) | Overlay | ● | ● | ○ | `chrome.tutorial` (proposed). **Consumer-scoped content for everyone**, auto-shown **once** after first-time setup for Consumer/Power (skippable; re-runnable from Settings). Default-OFF auto-show for **Developer**: devs already know the app; never nag them (they can launch it from Settings). Not a per-tab feature; the overlay + `SpotlightScrim` are always present, the *auto-trigger default* is what this row gates. |

Groups: `parserDeepDive` = { Parse Chain, Parser hex }; `graphDebug` = { Debugger toggle, Output
toggle, NavStrip TO-BREAKPOINT, Analysis graph breakpoints }. "N features hidden" affordance drives a
dismissible status-strip banner (hidden at 0 → developers never see it).

**Two forks needed an explicit call (recommendations in the [decisions log](#6-decisions-log--open-questions); both resolved 2026-07-15).**

<a id="settings-layout"></a>
### Settings screen layout (contract, single scroll, 2026-07-16)
`Views/Settings/SettingsView.axaml` is a **single `UserControl` shared by both hosts**: the desktop
`SettingsWindow` (520×640, `MinWidth 380`/`MinHeight 360`) and the WASM in-shell overlay
(`MainView.axaml` `SettingsOverlay` panel, a `Border.card` capped `MaxWidth 560`/`MaxHeight 720`,
`Margin 32`, over a `ShellBg`@0.8 scrim). The `ViewLocator` resolves the same view for the
`SettingsViewModel` in both, so **any layout must render correctly in both and stay WASM-safe** (no
native window/menu/threading assumptions; the only code-behind is the desktop folder-picker
storage-provider handoff, no-op on WASM).
- **Structure:** a `DockPanel(LastChildFill)` over `PanelBg`. **Footer docked `Bottom`**: a
  `Border.sectionHeader` with `Re-run first-time setup` (`.ghost`) + `Close` (`.primary`), always visible
  (global, not per-section). **Body = one vertical `ScrollViewer` > `StackPanel(Spacing=0)`** of four
  `sectionHeader`/`sectionLabel`-banded sections, common-first: **USER CATEGORY** (category `data-list`),
  **LIBRARY FOLDERS** (folder `data-list` + `Add Folder…`, count `badge`), **THEME** (`.field` ComboBox),
  **FEATURES** (the P2a-ii override table; see below). Each section body is a `Border.card` (`Margin 12,10`).
- **Layout decision: defer a tabbed/rail split; keep the single scroll.** A `TabControl`
  split was built, rendered at both host sizes, and reverted: the three top sections are compact
  (**~450px** total < the ~590px window / ~616px overlay body), so they **already fit above the fold in
  both hosts**: tabbing only converts "scroll to FEATURES" into "click to FEATURES" (lateral), and does
  not shorten FEATURES. The `sectionHeader` bands are the scroll landmarks. Full rationale + the revisit
  trigger (>6 sections **or** a second long section → adopt the 2-tab `TabControl` proven clean, or a
  left-rail `SplitView`) live in the [Settings-layout decision note](#6-decisions-log--open-questions).

### Settings feature-toggle list (P2a-ii): the surface that exposes this matrix
`Views/Settings/SettingsView.axaml` "FEATURES" section, backed by `FeatureToggleRow` +
`SettingsViewModel`. One row per `FeatureCatalog` entry lets the user force any feature on/off regardless
of category; every write is an explicit `AppSettings.Features.Overrides[id]`.

- **Grouping + order:** two `Border.card`-framed blocks. **TABS & SUB-FEATURES**: each Tab in catalog
  order immediately followed by its `Children(tabId)` SubFeatures (`IndentLevel` 1 → 20px left indent),
  under a `.col-label`. Then a `Rectangle.divider` and **GLOBAL CHROME** (Chrome rows, indent 0). Two
  `ObservableCollection<FeatureToggleRow>` (`TabFeatureRows` / `ChromeFeatureRows`).
- **Row layout (compiled `DataTemplate`, `x:DataType=FeatureToggleRow`):** `Grid ColumnDefinitions="*,Auto,Auto"`,
  left margin = `IndentMargin`. Col 0 = Label (`TextValue`) + a `Border.badge` **scope chip** ("Tab"/"Sub"/"Chrome")
  + optional "required" hint + the overridden dot, over a dim `Description` (`TextDim`). Col 1 = the per-row
  **clear-override** `.icon-btn` (`↺`, `IsVisible=IsOverridden`). Col 2 = a `ToggleSwitch` bound
  `IsChecked=IsEnabled` / `IsEnabled=IsInteractive`.
- **Locked rows (Required + group-follower):** two kinds of row are non-interactive, both with a disabled
  toggle + a `LockHint` chip. (1) `IsRequired` → "required" (always-on). (2) A **non-leader group member**
  (`GroupId` set and not the `GroupLeader`) → "follows &lt;leader&gt;": the gate resolves a group's members
  from its LEADER, so a follower's own override is *inert*: offering an independent toggle would persist a
  phantom that snaps back. The follower's `IsEnabled` setter is a no-op that bounces to the gate state; the
  **leader's** toggle drives the whole group live. The disabled Fluent toggle reads muted: the chip carries
  the signal, since brightening a disabled toggle would need a forbidden `ControlTheme`.
- **Overridden affordance:** `IsOverridden` (the id is present in `Overrides`, i.e. differs from the
  category default) → a small **amber dot** (`AccentAmber`) + the per-row `↺` clear. A section-level
  **"Reset to <Category> defaults"** `.ghost` button clears all overrides (`Overrides.Clear()`).
- **Header:** `Border.sectionHeader` "FEATURES" + a `Border.badge` "N hidden"; a sub-line "N hidden for
  <Category>" (bound `HiddenCount` + gate `Category`).
- **Live-apply (the flagship):** a toggle writes an override → `IOptionsMonitor.OnChange` →
  `IFeatureGate.Changed` → **both** the shell's tabs/chrome reconcile (P1.2) **and** the rows refresh from
  the gate. The Settings VM and the shell share the **singleton** gate (composition root), that is the
  only reason a toggle here reconciles the live app.
- **Re-entrancy (locked decision):** the row refresh uses a **row-level `_applyingRefresh` guard, NOT the
  VM's `_writing`**: an *external* category change refreshes rows while `_writing` is false, so a
  `_writing`-only guard would materialise a spurious override for every row whose default shifted
  (settings corruption on a plain category switch). The actual override write still routes through the
  VM's `Persist` (`_writing`). Single-write; verified no double-write/thrash.
- **Accepted edge (not a bug):** toggling a SubFeature ON while its parent Tab is off writes the override,
  but the refresh re-reads `gate.IsEnabled` = false (cascade), so the switch snaps back with the overridden
  dot showing against an off toggle. This is *faithful* (the override is stored and takes effect once the
  parent is enabled), not thrash, toggle-disabling is scoped to `IsRequired` only, per the P2a-ii spec.

### Settings Extensions section (strat-book-plugin.md item 5, 2026-10-02)

New top-level group between FEATURES and LIVE CS2, `Expander IsVisible="{Binding ShowGroupExtensions}"`,
starting **expanded** (unlike FEATURES, which starts collapsed): the section is one master switch plus
today's two small relocated cards, not a wall of rows, so the switch is worth surfacing without a click.

- **The row list is `ExtensionsFeatureRows`**, one `ObservableCollection<FeatureToggleRow>` built from
  `FeatureCatalog.All` by scope and `OwnerPackId`, reusing the P2a-ii `FeatureRowTemplate` resource: every
  `FeatureScope.Pack` descriptor's own row (indent 0), then the tabs it parents directly (indent 1, each
  immediately followed by ITS `SubFeature` children at indent 2), then any sub-feature the pack docks in a
  CORE tab instead of one of its own (2D Playback's tag palette and Suggested Tags), flat at indent 1. A
  second pack's rows appear with no code change: nothing here names "Strat Book". These rows leave
  `TabFeatureRows` and `ChromeFeatureRows` (`BuildFeatureRows` now skips any descriptor `OwnerPackId` tags,
  at both the top-level Tab loop and the per-tab `Children` loop), so a pack is never listed twice.
- **`ScopeLabel` reads "Extension"** for a `Pack`-scope row (falls through to the enum name otherwise).
- **A pack child row locks while its pack is off**, not just cascades: `FeatureToggleRow.IsPackEnabled`
  (default `true`; mirrored from `OwnerPackId is {} p && gate.IsEnabled(p)` on every `Refresh`) joins
  `IsInteractive`/`HasLockHint`/`LockHint` (`NotifyPropertyChangedFor`, since unlike `IsRequired` this one
  changes live). The lock hint reads "extension is off". A stray programmatic set while locked bounces back
  without writing, the same treatment `IsRequired` and a group follower get: the row's own override (if
  any) is untouched either way, so it is honoured again once the pack is back on. The master row's own
  `IsPackEnabled` is never set (a pack does not own itself via `OwnerPackId`), so the switch that turns a
  pack off is never itself locked by that state.
- **`FeatureGate.HiddenCount` excludes a `Pack`-scope row.** It renders as its own live master switch, not
  a hidden feature; counting it too would double against the switch itself. Everything the master cascades
  off still counts (it is still rendered, under Extensions, disabled).
- **The two pack-only settings blocks are contributed pages now** (`IPackContributions.SettingsPage`,
  item 14), content unchanged: Suggested Tags tuning and the GRENADE INDEX card (`Walk library grenades
  in the background`, `Render lineup clips`). A `MountedSettingsPage` exists for every contribution from
  construction, but its VM and View build lazily, through `EnsureBuilt`, the first time its own
  `FeatureId` resolves on: a page must never construct whatever its VM pulls in while its pack is off
  (plan doc §8). `RefreshContributedPageVisibility` calls `EnsureBuilt` (on a filter keystroke and on a
  gate change alike) before setting `IsVisible`, so a page builds at most once and stays built once its
  pack has been on. `SettingsView.axaml` hosts the list through one
  `ItemsControl ItemsSource="{Binding ContributedSettingsPages}"` beneath the row list, each item a header
  `Border` plus a `ContentControl` over the page's (possibly still-null) `Content`.
  `SuggestedTagsTuningViewModel` and the new `GrenadeIndexSettingsViewModel` live under
  `DemoViewer.NET.Extensions.StratBook.ViewModels.Settings` (their Views under `...Views.Settings`), not
  the core `ViewModels.Settings`/`Views.Settings` namespaces, so `PackBoundaryTests`' scan polices the
  edge; both VMs derive from `ViewModelBase`, matching the contribution's `Func<ViewModelBase> ViewModelFactory`
  typing. `GrenadeIndexSettingsViewModel` owns its own `Persist`/`Reflect` echo-guard pair over the shared
  `SettingsService`; `GrenadesBackgroundIndex`/`GrenadesRenderLineupClips` left `SettingsViewModel`
  entirely. `SettingsViewModel`'s public ctor dropped `SuggestedTagsTuningViewModel suggestedTagsTuning`
  and gained `IReadOnlyList<SettingsPageContribution>? settingsPages` instead; `App.axaml.cs`'s
  `Func<SettingsViewModel>` factory now passes `PackContributionSet.SettingsPages`.
- **Search**: `ExtensionsSectionMatches` ORs the generic `_sectionKeywords` entry ("extension", "pack",
  "plugin", …) with a scan of every BUILT row's own `Label` (fuzzy `PartialRatio >= 80`, same threshold the
  rest of findability uses), so a pack's name and its tabs'/sub-features' names are searchable without
  listing them by hand. A contributed page's own `Keywords` string feeds the same fuzzy match
  (`RefreshContributedPageVisibility`), so the "Suggested Tags Tuning" and "Grenade Index" rows dropped
  from `_sectionKeywords`: a pack's page carries its own now. A second pack costs nothing here either.
- **The in-session toggle notice** (`StratBookToggleNotice`, architecture doc §8): null until a flip
  happens in THIS vm's lifetime (seeded from the gate before the ctor's first refresh, so plain startup
  shows nothing). On a transition detected in `RefreshFeatureRows` (so a self-write, Reset-to-defaults and
  an external edit all catch it): off → `"{Label} stops its background work. Its data stays on disk."`
  (`Label` read off the watched pack's own `FeatureCatalog` descriptor, "Strat Book extension" today, so
  the string is never hand-typed); on → "Counting…" then `"{N} demos will be re-indexed in the
  background."` once `IPackReindexEstimate.CountAsync()` lands. The notice is generic now (item 14):
  `SettingsViewModel` watches `IReadOnlyList<IPackReindexEstimate>? reindexEstimates`'s first entry rather
  than importing `Extensions.StratBook` for `StratBookPack.PackFeatureId` and a static
  `StratBookPendingReindexCount.ComputeAsync()`. The pack's own `StratBookPendingReindexCount` class
  implements `IPackReindexEstimate` and captures the real `IServiceProvider` (not `App.Services`) when
  `StratBookPack.Contribute` builds it, over the same `RoundIndexEvaluator`/`GrenadeIndexEvaluator`/
  `SuggestedTagsService` union as before. A generation counter still drops a result that lands after a
  later flip rather than overwriting a more recent notice.
- **Fits the real host width** (520-560px desktop/WASM, §settings-layout above) with no horizontal
  overflow at every indent level, verified at 560 and visually at 1280×800 (`settings-extensions-on/-off`
  UiCapture variants, Light + Dark; custom themes crash UiCapture per the open item on the editor-room
  decisions above).
- **Delete extension data (strat-book-plugin.md item 24, 2026-10-03):** one row per `IPackDataRemoval`
  below `ContributedSettingsPages`, `ItemsControl ItemsSource="{Binding ExtensionDataActions}"`. Unlike a
  contributed page it is NOT gated by the pack's `FeatureId`: it shows whether the pack is on or off,
  since deleting while off is the main use. No modal dialog exists anywhere in this app to reuse, so the
  confirmation is an inline `Border` that Arm reveals in place of the Delete button: `ConfirmationText`
  lists the user-work stores by label and size (`AccentError` on both the Delete and the confirm panel's
  Delete, `PanelHeaderHoverDeep` for the panel so it reads as a step inside the card rather than a second
  card), Cancel collapses it, Confirm runs the delete and leaves a status line. `StatusText` alone (no
  panel) covers "Counting…", "Nothing to delete." and the final count. `ExtensionDataActionViewModel` is
  its own small Arm/Confirm/Cancel state machine, one per row, built from the pack's `IPackDataRemoval`
  and a label read off `FeatureCatalog` the same way `StratBookToggleNotice` reads one.

---

## 6. Decisions log + open questions

### Decisions (Strat start and trigger clock, feature/strat-book-start-block, 2026-10-02)
- **A Start row, not a step 0.** A step needs a time, a verb and a who that mean nothing for where tokens begin; the
  row has none of them and collapses to one line, so it costs the 1280 editor one line until opened.
- **The trigger text moved into the Start row** in the editor only; the file keeps `trigger` at the top level, so no
  older file is rewritten for it.
- **The clock switch is a combo in the metadata wrap.** A segmented toggle would have needed its own row at 315 px.
- **Opened, the row lists all ten tokens** rather than our five with opponents behind another toggle: the opponents
  are as often where a setup begins, and the list scrolls with the editor.

### Decisions (Strat editor room and steps, feature/strat-book-editor-room, 2026-09-29)
- **The step row was the overflow, not the panes.** Its fixed columns summed to about 1100 px, and the metadata
  grid, slot rows and record rows also overflowed the 315 px editor at 1280 with both panes open. All of them
  now wrap or use star columns; collapsing panes is extra room, not the fix.
- **Kept the editor and canvas at `*,1.3*`.** The canvas needs the width for the map; the editor fits 315 px.
- **Collapsed rail shows its sections rotated, not an icon strip.** There are no section icons, and two
  sections start with S. Rotated labels keep one-click navigation and the badges.
- **Rail badge text is `LibraryCardTextBright`.** The badge fill (`LibraryCardBadgeBg`) is dark in both bases;
  the default foreground put dark text on it in Light.
- **Layout persists in `SessionPayload`, not module-tab state** (see Collapsible side pane).
- **Open:** UiCapture fails with a cross-thread error for any custom theme (`--theme high-contrast`,
  `egirl`), including `primitives`, so the new chrome was captured in Light and Dark only.

### Decisions (Reels dashboard, plan step 7, feature/v0.5.3, 2026-07-28)
- **D-RD1. The tray renders from the plan builder, not a parallel model.** `ReelConfig.ClipGroups` is both
  the coalescing display and the tray. Considered and rejected: a `StagedClipViewModel` collection beside it.
  A second model would let the tray and the plan disagree, which is the exact failure the promotion exists
  to remove, since "coalescing visible while you build" is the plan's headline argument for it.
- **D-RD2. Canonical order is a plain `List<HighlightKey>` on the TAB.** `ClipGroups` is cleared and re-added
  on every lead-in keystroke; ▲▼ that mutated those objects would corrupt or lose the arrangement mid-edit.
  The rebuilt group VMs read order back out (first-appearance), so a rebuild is idempotent.
- **D-RD3. ⚠ Reorder is emission-only, and it was VERIFIED to reach the output rather than assumed.** Traced
  `_plan` → `ReelRequest.Clips` → `ReelJobService` (indexed loop; `BuildCompilation`'s
  `request.Clips.Select(...)` → `Cs2Compilation.Clips`) with **no re-sort in this repo**; `ClipIndex` in the
  result is positional. Confirmed against the shipped `Cs2VideoGenerator.Core` XML docs, not assumed:
  `Cs2Compilation.Clips` is *"Ordered list of clips to capture. Processed sequentially"* and
  `ConcatenateClips` *"uses FFmpeg to combine clips in order."* `ClipWindows.Coalesce` was NOT touched and
  stays order-independent.
- **D-RD4. Group-level ▲▼✕, clip-level ✕.** Matches the wireframe, and `ReelJobService` reloads the demo
  whenever `clip.DemoPath` changes between clips, so group-contiguous ordering avoids multiplying the most
  expensive step of a render. `MoveGroupTo` re-normalises the order list to preserve that invariant.
- **D-RD5. Filters were NOT kept over the tray.** They were discovery affordances over a library-wide corpus;
  a staged tray is small by construction. `PlayerFilterItem` / `HighlightTypeFilterItem` are **parked** (kept,
  documented as parked) for the Add-clips picker rather than deleted and re-derived.
- **D-RD6. `RequestCreateReel` survives as an explicit-accessor NO-OP shim.** `App.axaml.cs` still subscribes
  and is not this step's to edit; a never-raised field-like event is CS0067, which this repo treats as an
  error. `add {} remove {}` compiles, is honest, and cannot accidentally re-open the retired modal. Same
  reason the four Verify/open-demo ctor params are accepted-and-discarded rather than removed. Both are
  listed for deletion in the plan's §9.5.
- **D-RD7. `HighlightReelDialogWindow` could not be deleted by the step that retired it**: `IWindowService`
  (not owned here) references it, and `App.axaml.cs` calls `ShowHighlightReelDialog`. Deleting it would break
  the build gate. It is dead-but-compiling and §9.5 owns its removal.
- **D-RD8. Tray persistence is implemented and INERT.** `IWorkspaceTabViewModel.SnapshotState()` has a
  default returning null and **zero call sites outside tests**: module tab state is not written to disk
  today. The tray survives tab switches; it does **not** survive a restart, which is fork 8's stated goal.
  Recorded as a shell obligation rather than silently "done".
- **D-RD9. `Clear tray` is a two-step inline confirm.** A cross-demo tray is minutes of curation with no
  undo and the button sits beside Generate. Inline strip, same idiom as the §9 interlock, never a modal.

### Decisions (Visual Walkthrough overlay, presentation layer, feature/v0.5.1-guide, 2026-07-24)
- **Spotlight = one code-drawn even-odd geometry, not 4 dim panels or a converter.** A `SpotlightScrim`
  `Control` (full rect + rounded-rect hole, `FillRule.EvenOdd`) gives the rounded hole + token frame the
  brief asks for in one repaint; theme-reactivity is free via `StyledProperty<IBrush>` + `AffectsRender`
  (no `ActualThemeVariantChanged` sub needed, nothing is cached, cf. `Playback2DViewport`). `ScrimOpacity`
  is baked into the fill colour (read from the `ISolidColorBrush` token) so the dim is translucent while the
  frame stroke stays crisp, avoids `PushOpacity` fragility and still re-themes (Render re-runs on token
  re-resolve).
- **VM is display-only; Back/Next/Skip delegate to injected `Action`s** (the `IdleViewModel` seam). No
  index math / advancement in the VM: the two tour segments fire at *different times* (first-run vs
  demo-loaded), so a linear index walk would prejudge the engine. The engine drives `CurrentStep` +
  `SpotlightRect` + counters and implements the three actions (advancement, tab-switch, teardown, persistence).
  This satisfies "don't wire up navigation logic" literally.
- **Content is a static `TutorialSteps.Default` provider, not VM-owned**: the engine's input, so it can
  filter by `TutorialSegment` and A/B copy without touching the view. Answers the "where does step content
  live" open question.
- **Callout positioning is code-behind (layout, not data).** Clamping to the window needs the callout's
  *measured* size, which a converter/MultiBinding can't see; `PositionCallout` runs on `LayoutUpdated` (settles
  under the headless render pump) + VM `PropertyChanged`. Same category as the `GraphView`/`Playback2DViewport`
  render code-behind, not a reactive-binding violation.
- ~~**Not integrated.**~~ **Shipped in v0.5.1** (the engine, then the sample-demo wiring). The
  walkthrough is live: `MainView`/`MainViewModel` host it, the wizard Done page offers it, it persists, and
  the gateway spotlights the Library's "Try a sample match" hero CTA, which opens the bundled 3-round
  sample (`assets/tour/*.dem`, resolved by `TourDemoLocator`). Gate: `chrome.tutorial` (auto-show ● consumer/
  power, ○ dev; always re-runnable from Settings), as proposed. The step-count fork was resolved in favour of
  global numbering; `StepNumber`/`StepCount` remain VM-exposed, so per-segment numbering is still a
  zero-view-change switch if it's ever wanted.

### Decisions (CSVG-integration UI review fixes, 2026-07-19)
Six confirmed review findings, all UI-surface, fixed on `feature/csvg-integration`. No new tokens; one new
shared class (`ListBox.card-grid`, above).
- **D-CSVG7. The open-remote-demo failure now surfaces (was swallowed).** `LiveSyncStatusViewModel`'s
  `OpenRemoteDemo` catch wrote `ReasonText` but **no view bound it** (dead property), and its adjacent
  `RestoreFailureText = null` clobbered the unrelated crash-recovery surface. Fix: bind `ReasonText`
  in the **Degraded** flyout section (`Views/LiveSync/LiveSyncStatusView.axaml`, after "Open in DemoViewer")
  as the **sibling error treatment**: a `⚠` `AccentError` glyph + neutral `TextMid` wrapping message in a
  `Grid Auto,*`, visible via `StringConverters.IsNotNullOrEmpty`, and drop the stray `RestoreFailureText`
  reset. `MapState` now clears `ReasonText` on any fresh transition (the catch doesn't re-map, so it would
  otherwise persist onto a later Degraded re-entry). **Deviation note:** the finding pointed at the bound
  `RestoreFailureText` block (72–74) as the model, but that block is `AccentError`-**tinted text**; the
  authoritative treatment per §3.2 / D-CSVG5 is **icon + neutral `TextMid`** (never tint the message), so
  `ReasonText` uses that, matching the reel dialog's demo-moved sibling, not the tinted `RestoreFailureText`.
- **D-CSVG8. The Synced flyout Speed row is now capability-aware (§5.6).** It hardcoded "locked to 1× while
  synced" + an "ⓘ why?" tooltip claiming *CS2 has no speed command*, false once the branch made the lock
  capability-driven. New `LiveSyncStatusViewModel.IsSpeedLocked` = `State.IsSynced && !(Capabilities?.TimescaleSet
  ?? false)`, the **exact predicate** as `MainViewModel.IsPlaybackSpeedLocked` and the `OnStateChanged`
  lock guard, is notified from `MapState`. The row shows the locked copy + a reworded tooltip ("…the connected
  CS2 plugin can't mirror playback speed. Update CSVG for two-way speed control.") when `IsSpeedLocked`, else
  **"mirrored to CS2"** (a v1.1 `TimescaleSet` plugin keeps Speed user-controlled).
- **D-CSVG9. The macOS dry-run caption no longer tints the label.** `HighlightReelDialogView.axaml`'s §8.9
  caption was a full sentence painted `AccentCaution` at FontSize 11 (fails AA on Light `ShellBg`, ≈3.9:1).
  Fix: the established sibling treatment: `⚠` `AccentCaution` glyph + neutral `TextMid` caption in a
  `Grid Auto,*` (wraps), matching the demo-moved error + v1.0-plugin notes in the same dialog. **Repo rule
  reaffirmed: never tint the label** (§3.2 / D-CSVG5).
- **D-CSVG10. Highlights WASM degrade now matches spec (§1/§7.1).** The reel footer, the filter toolbar, and
  the master-detail grid were gated only by `!ShowEmptyHero`, so on the browser host they rendered as
  permanently-disabled chrome. New reactive VM flag **`HighlightsTabViewModel.ShowBrowseSurface`** =
  `!ShowEmptyHero && !IsBrowser` gates all three off the browser (and off the empty hero). The WASM note copy
  was **dishonest** ("Showing highlights for the open demo only", open-demo rows can't populate on WASM, the
  harvest path needs a rooted on-disk file); reworded to the §1 Absent framing ("Highlights aren't available
  in the browser build. Highlight scanning, verification, and reels need a local demo library…").
- **D-CSVG11. The "transparent virtualized card-grid ListBox" look is promoted to `ListBox.card-grid`.**
  `HighlightsTabView`'s `hlVirtual` duplicated `LibraryTabView`'s `libraryVirtual` trio setter-for-setter (the
  2nd consumer → §2 ≥2× rule). Promoted the **superset** (transparent list + zero item padding/margin +
  transparent selected **and** pointerover presenter) into `Styles/Tables.axaml`; both views now consume
  `Classes="card-grid"` and their local style blocks are removed. Contract recorded in the §2 class table.
- **D-CSVG12. Two nits.** (a) The Create-Reel footer button's static "Select at least one highlight" tooltip
  is now dynamic: bound to new `HighlightsTabViewModel.CreateReelHint` (the hint while `!HasSelection`,
  `null` once ≥1 selected), the reel dialog's `ErrorBanner`-tooltip idiom. (b) `Controls/StatusChip.axaml`
  inlined `FontFamily="Consolas,Menlo,monospace"` on the label; switched to the shared `Classes="mono"`
  (Primitives.axaml) per the §2 adoption guide.

### Decisions (CSVG Highlights tab, 2026-07-18)
> ⚠ **PARTLY SUPERSEDED (v0.5.3, 2026-07-28).** The master **card grid**, the four filters over it, the
> per-demo details pane and the F2 Verify rows described below **no longer exist on this tab**: see
> [Reels dashboard](#reels-dashboard-v053) and plan §5 for where each function landed (Match Overview for
> per-game exploration + Verify; the Add-clips picker for the filters). What still holds: D-HL1's module +
> persisted-id wiring, D-HL2's delegate-injection pattern, the cache/scan service contracts, and D-HL3's
> `HighlightKey`-keyed selection set, which is exactly what the clip tray is a promotion OF.
- **D-HL1. Registration (per ux-design §7.1).** New `IWorkspaceModule` `Modules/Highlights/HighlightsModule`
  (TabId `"highlights.browser"`, Order 3, Placement Main), **`ViewModelFactory` never `DataContext`** (the
  DataContext branch skips the `OnActivated` lifecycle the tab-activation staleness trigger needs). Registered
  in `App.BuildRegistry` (alongside Playback2D/Workbench) + `tab.highlights` `FeatureDescriptor` (verbatim
  §2.1) + the `"highlights.browser" → "tab.highlights"` `TabFeatureIds` entry in `MainViewModel`.
- **D-HL2. Delegate-injection with lazy shell resolution (the Library precedent, adapted for a BuildRegistry
  module).** The Library VM is shell-built and handed in; a BuildRegistry module has no shell yet. Resolution:
  `BuildRegistry` now takes the `IServiceProvider`; `HighlightsModule` gets a `Func<HighlightsTabViewModel>`
  whose closure resolves the DI singletons (cache store / scanner / settings) **eagerly** (no MainViewModel
  dependency ⇒ no ModuleRegistry recursion) and the shell-bound behaviours **lazily**: `openInWorkspace`
  (`MainViewModel.OpenDemoInWorkspaceAsync`, a new public switch-to-Parser-then-load method) and the F2 Verify
  trio (`isVerifyPresent`/`canVerify`/`verifyMoment`) resolve `MainViewModel` from the provider only when
  invoked (tab-activation, long after the shell exists). Service-location is confined to the composition root,
  where it is idiomatic. The VM itself references no shell type (delegate-injected, testable headlessly).
- **D-HL3. Reel selection-set shape (what the reel flow consumes).** The tab VM's canonical selection is a
  `Dictionary<HighlightKey, HighlightSelection>` keyed by a composite `HighlightKey(FilePath, RulesetId,
  HighlightId, Tick, PlayerSlot)` so a checkbox survives detail-pane rebuilds AND spans demos. It is exposed
  as **`IReadOnlyList<HighlightSelection> SelectedHighlights`**, each `HighlightSelection(DemoCacheRecord
  Record, CachedHighlightEvent Highlight)` bundling the owning record (tickRate / tickCount / rounds / demo path
  / roster for the slot join) with the highlight (tick / round / rendered title), everything `ClipWindows.Compute` +
  `Coalesce` need, so the reel dialog needs zero further store access. Footer `CreateReelCommand` (enabled ≥1
  selected) raises **`event Action<IReadOnlyList<HighlightSelection>>? RequestCreateReel`**: the Phase-6
  drop-in point (currently a no-op stub beyond the event).
- **D-HL4. Verify "present vs enabled" (§6.2 two-level gate, mirrors `AnalysisViewModel.Verify`).** Each
  `HighlightEventRow` snapshots `VerifyPresent` (level-1: `chrome.livesync` + desktop → button `IsVisible`) at
  build; the `VerifyCommand` `CanExecute` reads `canVerify()` live (level-2: a Synced session) AND a per-row
  `IsVerifying` busy flag AND a VM-level `_verifyInFlight` guard (no double-invoke / one at a time). Tick is
  passed **frame-clock AS-IS**; spectate by the **RAW** cached name. *(Deviation vs §6.3: the disabled Verify
  button's tooltip is generic rather than the "ⓘ enable sync" inline prompt, the gating behaviour is exact;
  the prompt glyph is a deferred polish.)*
- **D-HL5. Staleness/scan chips from `ScanState` alone.** A card is `IsStale` (⚠ outdated, rescan, click →
  `RequestScan`) when `ScanState == Pending && Events.Count > 0`; `◐ scanning` marks the newest-Pending row
  while `scanner.IsScanning` (the single in-flight demo, approximated (the store carries no explicit
  in-flight marker)); other Pending ⇒ `queued`; `Failed` ⇒ retry. Background scan / reel / Verify are all
  suppressed under `OperatingSystem.IsBrowser()` (WASM degrades to open-demo-only + an explanatory note).

### Decisions (CSVG Phase-3 Live Sync polish, 2026-07-18)
- **D-CSVG3. The 2D-tab CS2 indicator is DISPLAY-ONLY / non-interactive** (`IsHitTestVisible=False` on the
  whole top-right HUD stack it shares with the kill feed). ux-design §5.3 offered "click → focuses/opens the
  shell sync flyout," but that flyout is owned by the `StatusChip` `Button` in the bottom `StatusStrip`;
  programmatically opening it from a control in the 2D viewport's visual tree is disproportionate wiring for
  a redundant affordance (the always-present shell chip IS the control centre, one click away). So the
  indicator is non-interactive: it also then never eats viewport pan/zoom gestures (same rationale as the
  kill feed). Recorded as the chosen deviation.
- **D-CSVG4. The 2D indicator reaches shell state through a decoupled `IModuleContext.LiveSyncHud` seam, not
  a direct VM reference.** Modules get `IModuleContext` by design; the seam (`ILiveSyncHudState`, engine-free,
  default-null interface member) keeps `Modules.Abstractions` free of the App-layer `Services.LiveSync`
  contract and keeps every existing `IModuleContext` test double compiling untouched. The gate is folded into
  `IsActive` (not into seam presence) so a live `chrome.livesync` toggle reflows the indicator without a tab
  re-activation. See the [seam contract](#live-sync-2d-tab-indicator--ilivesynchudstate-seam-csvg-phase-3-ux-design-53).
- **D-CSVG5. The v1.0-plugin flyout note uses the sibling caution treatment (⚠ `AccentCaution` + neutral
  `TextMid`), not tinted body text.** ux-design §5.2 asked for an "`AccentCaution`-toned note"; the tone is
  carried by the ⚠ icon (matching the existing "untested plugin/game pair" + §5.7 path notes), keeping the
  message text on the universally-AA-safe `TextMid` (a tinted `AccentCaution` message is razor-thin on the
  Light `CardBg`). Verified legible in dark / light / high-contrast renders.
- **D-CSVG6. F2 "Verify in CS2" (the UI half) rides the Analysis graph node/edge context menu on
  pointer-release** (ux-design §6.1's resolved fork; §12 table), a new item on the *same* imperatively-built
  menu as the breakpoint items (`Views/Analysis/AnalysisTabView.axaml.cs` `AddVerifyInCs2Item`). It is scoped
  to the rule-trigger surface: the `!IsBreakpointable` early return means only nodes + trigger-backed edges
  reach it (un-backed logic edges are not triggers, correctly get no Verify item). **Zero new tokens /
  controls / layout patterns.** Key as-built calls:
  - **Testable core on the VM, not the code-behind** (`ViewModels/AnalysisViewModel.Verify.cs`, a partial of
    the existing `AnalysisViewModel`). The code-behind only *shapes the menu item*; all gating + tick/name
    resolution + busy/failure handling is a `[RelayCommand] VerifyInCs2Async` + pure static resolvers
    (`ResolveFrameClockTick` / `ResolveSpectateName` / `CanVerify` / `NearestFireMessageIndex`). This is the
    standing answer to the §6.1 "MSAGL surface can't be screenshot-verified" caveat: the surface is
    un-renderable headlessly, so it is
    pinned by **17 pure headless tests** (`AnalysisVerifyInCs2Tests`) instead, no annotated-mockup PNG was
    produced (it would be a hand-drawn mock, not a render; the tests are the stronger artifact).
  - **Two-level gate via three shell-wired delegates** (same decoupled direction as `CardFactory` /
    `OnFrameSeeked`; the VM never references `Services.LiveSync`): `IsVerifyInCs2Present` = `IsLiveSyncEnabled`
    (present-vs-absent, no item at all when off, never shown-then-disabled), `CanVerifyMoment` =
    `IsLiveSyncEnabled && LiveSync.State.IsSynced` (enabled-vs-disabled+prompt), `VerifyMomentHandler` →
    `VerifyMomentAsync`. **Disabled-prompt lives in the always-visible header** (`"Verify in CS2 — enable
    Live Sync first"`), **not** a `ToolTip`: a disabled `MenuItem` gets no pointer-over in Avalonia 11.3.x so
    a tooltip on it never surfaces (verbatim §6.3 tooltip kept as belt-and-braces). The prompt shows only for
    the "no live session" disable; a transient in-flight disable keeps the plain header (never actively wrong).
    **No auto-launch of CS2 from here.** ("Synced for the current demo" is approximated by `IsSynced`, real
    demo divergence downgrades to `Degraded` (∉ IsSynced); only v1.0-invisible divergence is a known gap the
    engine can't detect either.)
  - **Tick = the trigger's firing tick, frame clock AS-IS** (§6.1). The right-clicked `ConditionTarget` is
    the command parameter: an **edge** is the trigger itself → its own recorded fire (`_appliedByEdgeKey`)
    nearest at-or-before the playhead, else its first; a **node** → the union of its incoming trigger edges'
    fires, same nearest rule; **fallback** = the current step-through position (a context/root node with no
    incoming trigger, per-player fires that live in the tables not `_appliedByEdgeKey`, or pre-analysis).
    Every branch maps a message index → `DemoFrame.ServerTick`, which shares `RuleChainEvent.Tick`'s space
    (the evaluator stamps events with `frame.ServerTick`, confirmed `StateGraphEvaluator.cs:500/530`);
    **never `−ServerStartTick`.** Spectate name = the graph filter's selected player's raw in-demo name
    (`Filter.SelectedPlayer`, real slot), else null (the graph node/edge VMs carry no per-slot attribution;
    spectate is optional per §6.1).
  - **Feedback (§6.4):** `IsVerifying` blocks re-entry via `CanExecute`; the shell chip's "Seeking…" is the
    primary busy/failure surface (engine-side); a `false` return additionally sets an inline `StatusText`
    note pointing at the CS2 chip. The Highlights-tab "Verify live" (§6.1 secondary) will reuse this same
    engine call + feedback when that tab lands (F3).

<a id="library-toolbar-reflow"></a>
### Decisions (Library filter toolbar reflow, 2026-07-16)
- **D35. The populated Library filter toolbar reflows via a fill `WrapPanel` instead of clipping at narrow
  widths.** `Views/Library/LibraryTabView.axaml`'s toolbar was a fixed 9-column `Grid`
  (`Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto`); at ~700px the fixed columns crowded and the search box /
  map chip / view toggle overlapped and clipped (verified BEFORE at 700px). Replaced the `Grid` with a
  **`DockPanel`**: the `LIBRARY` section label is pinned `Dock="Left"` (stable identity anchor) and every
  interactive control lives in a fill **`WrapPanel`**: `+ Add folder`, the search `TextBox`, a
  `map/player/sort/clear` group `StackPanel`, and a `view-toggle/refresh` group `StackPanel`. The two
  filter/view groups are wrapped in `StackPanel`s so they **reflow as units** (never split mid-group).
  `Border.sectionHeader` has no fixed height (`Padding 14 6`), so the band grows to the wrapped row.
  **Chosen approach = WrapPanel (§4 option 2), NOT the NavStrip DockPanel+ScrollViewer (§4 applied):** a
  horizontal scrollbar reads clunky in a filter toolbar, and a scrolled search box loses its width; wrapping
  keeps the search box usably wide on its own row. Discharges the toolbar-reflow forward-reference in D18 (P3.2b).
- **D36. Search box `MinWidth=220`/`MaxWidth=360`, no stretch: a deliberate, render-tuned trade-off.** In a
  `WrapPanel` a child can't stretch to fill the row (it's measured at desired width), so the Grid's
  `*`-stretch search is gone; a `MinWidth` is the lever. Verified from the three renders: **700px → 2 rows**
  (search near-alone on row 1, filters+view on row 2); **900px → 2 rows** (search+filters on row 1,
  view/refresh on row 2); **1200px → 1 row**, left-packed. Left-packing at wide reads as a conventional
  toolbar (and the count/folder strip below right-docks `Open Demo…`/`Recent ▾`, so the right side isn't
  visually empty). The right-dock-the-view-group alternative (balances wide but floats the cluster
  vertically over 2 rows at narrow) was kept in reserve; the renders didn't need it. `stretch↔wrap` are
  mutually exclusive in pure declarative Avalonia XAML (no width trigger), narrow-safety was chosen over
  edge-to-edge stretch.
- **D37. Bindings/commands preserved byte-identically: a layout reflow, not a feature change.** Every
  control keeps its exact binding (`SearchText`, `MapFilterSummary` + its map flyout, `AvailablePlayers`/
  `SelectedPlayer`, `SortIndex`, `ClearFiltersCommand` + `HasActiveFilters` visibility, the `IsCardView`/
  `IsListView` view-toggle, `RefreshCommand`) and its per-control `IsVisible="{Binding !HasNoFolders}"`
  (Clear = `HasActiveFilters`); the only additions are two grouping `StackPanel`s + per-item vertical
  margins for the wrapped row. **Collapsed `WrapPanel` children reserve no space** → the landing/empty state
  still reads as just `LIBRARY` + `Add folder` (verified `library-landing`). No new tokens/classes; inline
  `FontSize`/`Padding` kept as-is (no restyle, minimal churn). Library App.Tests **15/15** (incl.
  `ZLibraryRenderTests` body `GetVisualAt` assertions). No new UiCapture variant: reused `library-populated`
  at `--size 700x600/900x600/1200x600` + `library-landing`.

### Decisions (ShellTabs style-fork, RESOLVED: keep the named selector, 2026-07-16)
- **D38. MainView's `TabControl#ShellTabs > TabItem` selector is NOT migrated to `TabItem.shell-tab`; the
  fork is intentional, not debt.** The convergence pass (D34) flagged the shell-tab look living in two places:
  the named-control selector in `MainView.axaml` and the `TabItem.shell-tab` class in `Primitives.axaml`, as a
  candidate to unify. On inspection they are **value-identical** (`Consolas,Menlo,monospace` / `FontSize 13` /
  `Padding 18 6` / `MinHeight 0`), so there is no visual divergence to fix. But `#ShellTabs` is **data-generated**
  (`ItemsSource="{Binding Tabs}"`, no hand-declared `<TabItem>`s), and a generated container can only carry the
  `.shell-tab` class via a template-replacing `ItemContainerTheme` (against the additive-only rule **D6**) or by
  duplicating the setters anyway. Since the parent-scoped container selector is the **idiomatic** way to style
  generated tab containers and MainView isn't headless-renderable to verify a swap, migration would add machinery
  and unverifiable risk for **zero** pixel change. Resolution: `TabItem.shell-tab` stays the primitive for
  **hand-declared** tab headers (module sub-tab bars, currently no consumer yet, kept as vocabulary); MainView
  keeps its selector; the two setter blocks are kept in sync by convention. Docs/comment-only change; `src/` output
  unaffected (the `Primitives.axaml` edit is comment-only, inert at XAML compile).

### Decisions (NavStrip SEEK/EVENT consolidation, shipped 2026-07-16)
- **D24. The NavStrip semantic-nav collapses to a SINGLE event stepper + merged target chip.** Concept B
  (segmented pill) won and the tick stepper was dropped. The six JUMP buttons
  (`◀ev ◀rnd ◀tk tk▶ rnd▶ ev▶`) + the hidden `⚙▾` flyout are replaced in production `NavStrip.axaml` by
  `EVENT: ◀ <target chip ▾> ▶` (`NavPrev/NextEventCommand`). Tick/frame nav = CLOCK `◀ ▶`
  (`PreviousFrame`/`NextFrame`); the removed round buttons = the chip's **`Round` preset**. `NavPrev/Next`
  `Tick`/`Round` RelayCommands stay on the VM (API intact), just unbound from the strip. Net 7 → 3 strip
  controls, target always on-screen. See [Consolidated SEEK/EVENT nav](#navstrip-redesign).
- **D25. Presets are named selections over the existing `GameEventFilters`, on `EventFilterFlyoutViewModel`.**
  `PresetAnyEvent` (clear → match-any), `PresetRound` (`round_*` union, reproduces the removed
  `NavPrev/NextRound`, which key off the identical `StartsWith("round_")` set), `PresetKills`
  (`player_death`), `PresetBomb` (`bomb_*`). No parser change, **no protected files.** The `Round` preset
  is the discoverable path to round nav because **CS2 GOTV emits `round_freeze_end`/`round_officially_ended`,
  not `round_start`** (`DemoAnalyzer.DeriveRounds`), so a user shouldn't have to know the exact name.
- **D26. Chip label = `EventFilterFlyoutViewModel.TargetSummary` (new, live).** `Any event` (none/all
  selected) / `Round` (exactly the `round_*` set) / `<event name>` (one) / `N events` (subset). Distinct
  from the verbose `FilterTooltip` (kept for the hover). Both notify on any item/collection change.
- **D27. Concept B, dependency-free + no new tokens/classes.** The pill reuses `CardBg`/`BorderAccent`/
  `BorderSubtle` + `Button.nav-btn`/`.ghost` + the dropdown's `card-flyout`/`ctx-action`/`col-label`/
  `CheckBox.event-filter`. Chip `MaxWidth=128 + TextTrimming`. CLOCK, amber TO-BREAKPOINT, the
  `IsBreakpointNavEnabled` gate, and the P3.1 DockPanel are unchanged. **Narrow win:** the single stepper
  is narrower than the old JUMP group, so 820px fits with room to spare even in the dev config (breakpoint
  on), verified `navstrip-real` + `navstrip-real-target` at 1000/820. Tests: NavStrip/flyout/settings
  49/49 (incl. 3 new preset+summary tests). Pass-2 `navstrip-v2-*` mock variants retired from `Variants.cs`.

### Decisions (P3.4, HarvestFrameListControl de-inline + app-wide convergence)
- **D32. The MARKUP (`.axaml`) inline-hex sweep is COMPLETE: this is NOT "all colour lives in the
  palette."** `HarvestFrameListControl.axaml` (Parser tab's frame list, 21→0) was the **last** inline-hex
  *view* file. `grep -rE '#[0-9A-Fa-f]{6,8}' --include='*.axaml'` over `src/App/DemoViewer.NET` now returns
  **zero** outside `DarkPalette.axaml`, verified **byte-for-byte identical** BEFORE/AFTER (`framelist`
  variant), the strongest possible no-regression proof. **Scope correction (an earlier draft of this line
  overstated it):** the sweep converged *markup*, not *all* colour: a substantial body of **code-held
  colour** remains outside the palette, by design or as tracked follow-ups (enumerated in D34). So the
  accurate statement is "the app's XAML is fully tokenized," not "colour lives in one place."
- **D33. 17/21 occurrences reused exact-value EXISTING tokens; 4 genuinely-new values got scoped tokens.**
  Reused `PanelBg`/`BorderSubtle`/`PanelHeaderBg`/`BorderStrong`/`TextLabel`/`ShellBg`/`TextMid`/`TextDim`/
  `TextFrameInfo`/`BorderAccent`. The type-pill's dark **foreground** `#0C0C1A` maps to `PanelBg`: an exact
  value-match, so reused per the anti-duplication rule even though it's a surface token used as text (the
  pill text is the panel bg "carved out"; `TextOnAccent #12121E` would have shifted the pixel). Added 4
  `Frame*` tokens (`FrameRowSelectedBg`, `FrameRowHoverBg`, `FrameMsgBadgeBg`, `TextFrameType`) for values
  with **no** exact match: each a *near*-dup of a standard token, so a new token was the only pixel-identical
  option; all flagged as unify-candidates for a later (pixel-changing) pass.
- **D34. Convergence status: MARKUP colour is fully converged; CODE-held colour and a few style-class
  adoptions remain.** Two axes to report honestly:
  - **(i) Code-held colour (NOT converged, separate, larger question, never in the markup-sweep scope).**
    A `grep -rE '"#[0-9A-Fa-f]{6,8}"' --include='*.cs'` finds meaningful colour still defined in code:
    the **Playback2D Skia renderer** `Modules/Playback2D/Playback2DViewport.cs` (~30 hex) + `PlayerAttributes.cs`
    `TeamColor` (3), D21/D22, kept for init-order/WASM reasons + the team-triplication unify follow-up;
    `WorkbenchYamlHighlighting.cs` (12 syntax roles, D31); and a **family of VM accent-CLASSIFIER palettes**
    that map a semantic key → a hue in code: `HarvestCardViewModel` (17), `HarvestPropertyViewModel` (15),
    `FrameGameEventViewModel` (10), `SubTickEventViewModel` (8), `ParserTabViewModel` (8),
    `HarvestFrameRowViewModel` (8), `OutputPanelViewModel` (7), `CommandPaletteViewModel` (4),
    `AnalysisMessageViewModel` (4), `BoolToBrushConverter` (2). These classifiers (`net_`=blue / `svc_`=green
    / `DEM_`=orange … at `0xC0` alpha) are the **same code-owned family as the depth ramp**: a
    `switch(type) → Color`, not "inline hex in markup." Promoting them into `DarkPalette` as an indexed
    classifier palette generalizes the existing depth-ramp-promotion open-question; **out of scope for the
    markup hex-sweep and NOT done.** *Verdict:* XAML colour consistency is done; whole-app
    colour centralization is a further, deliberately-scoped effort.
    **v0.6.0 census update: the classifier promotion LANDED.** `HarvestPropertyViewModel` (15) had already
    been promoted pre-0.6.0. Promoted in v0.6.0 (live surfaces → theme tokens): `HarvestCardViewModel` (8),
    `HarvestFrameRowViewModel` (8), `OutputPanelViewModel` (7), `DiagnosticsTelemetryHub` (4),
    `CommandPaletteViewModel` (4), `BoolToBrushConverter` (2, converter **deleted**, class-styled dots
    replaced it), `GraphNodeViewModel` (2), `MapAccentConverter` (→ the `Theming/MapAccent` behavior), the
    hex depth ramp (`HexSwatchSelected/Parent/Ancestor` + a new `HexSwatchAncestorDeep`), and
    `WorkbenchCompletionData` (9 → `Syntax*` tokens). The remaining four census entries were **DEAD
    surfaces** (no view bound them), so they were recorded as such and no tokens were minted:
    `FrameGameEventViewModel` (10), `SubTickEventViewModel` (8), `AnalysisMessageViewModel` (4), and
    `ParserTabViewModel.GetAccentBrush` (8, **deleted**, zero call sites). The census's remaining
    Playback2D entries are RESOLVED history: `PlayerAttributes.TeamColor` no longer exists (deleted,
    `IsT`/`IsCt` class flags → `Pb2dTeamT/Ct` tokens), and the renderer's ~31 inline hex are
    `ThemeColors.Get` design-time fallbacks since the theme-token promotion, not owned colors, see the
    superseded note on D22 and the resolved triplication bullet in Open questions.
  - **(ii) Style-class forks.** Scanned Parser/Entity/Analysis views + `Controls/` for local `<Style>`/
    `<ControlTheme>` forks re-declaring a primitive the design system provides. Findings:
  - **`TabControl#ShellTabs > TabItem`** (`MainView.axaml`) vs the shared `.shell-tab`, a real fork, but
    **already tracked** as deferred (MainView isn't headless-renderable, so a `.shell-tab` migration can't
    be capture-verified). *Verdict:* adopt during a shell pass; not worth an unverifiable change now.
  - **`Button.filterchip`** (`AnalysisTabView`) **and** `Button.statscat` (`StatsTabView`) are **two
    independent selectable-pill patterns**: a genuine ≥2× "toggle-chip" pattern that could promote to a
    shared `.filter-chip` class. *Verdict:* both are already token-bound (no hex debt) and structurally
    divergent (`filterchip` styles a child `> Border`; `statscat` styles the ContentPresenter); promotion
    pays off only if a 3rd consumer appears: **defer, diminishing returns.**
  - **`Button.headerField`** (Parser), **`Button.hdr-action`** (`InspectorCardListView`), **`Button.prop-row`**
    (InspectorCard): bespoke local/shared-control looks, **NOT** forks of a provided class (unique roles,
    all token-bound). `prop-row` is a shared control's own internals. *Verdict:* leave.
  - **No `TextBox`/`ComboBox` forks, and no base `<ControlTheme TargetType>` template-replacements** exist:
    consistent with the "additive classes only, no base ControlTheme" rule (D6).
  - **Net:** the **markup** token-consistency goal (de-inline `.axaml` hex) is
    **fully converged and byte-verified**. Two separate, optional efforts remain, neither blocking: **(a)**
    promote the code-owned classifier palettes (Playback2D renderer + VM accent classifiers + depth ramp)
    into `DarkPalette`, a larger, deliberately-scoped consolidation, not part of the markup sweep; **(b)**
    the two style-class adoptions above (shell-tab TabItem; a shared toggle-chip), diminishing returns. No
    inline-`.axaml`-hex debt remains.

### Decisions (P3.3, Library / RuleWorkbench / Stats hex de-inline)
- **D28. De-inline is pixel-identical by construction; renders are confirmatory.** Every new token's
  `.Color` (or gradient stops/offsets) **exactly** equals the literal it replaced (alpha included), so each
  `{StaticResource}` `SolidColorBrush`/`LinearGradientBrush` renders identically: that value-equivalence is
  the load-bearing proof. `library-populated` BEFORE/AFTER read **pixel-identical** (and its `13 : 11` score
  badges exercise `LibraryCardScoreCt/T`, `LibraryCardTextFaint`, `LibraryCardBadgeBg`); the new `workbench`
  variant renders `BorderTranslucent` + `AccentCaution` ("🔒 shipped") + `AccentErrorSoft` (diagnostic
  location) all correct. Counts: Library 14→0, RuleWorkbench 9→0, Stats 1→0 inline hex.
- **D29. Pixel-identity OVERRIDES "adopt the shared class" when the class would shift pixels: tokenize
  instead.** The 7 RuleWorkbench panes (`#33FFFFFF` hairline, **no fill**, radius 4) are a legitimate fork
  of `Border.card` (which adds a `CardBg` fill + `BorderAccent` edge); adopting `.card` would change fill
  **and** border = a regression. So the pass tokenized the border (`BorderTranslucent`) and left the
  structure. General rule for a token-consistency pass: **anywhere a shared class would move a pixel,
  tokenize the color and leave the geometry** (the 7× pane `Border` is a *future* `.workbench-pane`
  candidate, but promoting a class is structural, out of scope here).
- **D30. Reuse an exact-value match only across the SAME domain.** Stats `#0E0E1E` == `PanelHeaderBg` (both
  app-chrome) → **reused** the token (0 new). But `#33FFFFFF` == `Pb2dKillFeedBorder` and `#E0A030` ==
  `Pb2dTeamT` are **Pb2d game-HUD domain** tokens (a walled-off palette, D21) → **added** app-chrome tokens
  (`BorderTranslucent`, `AccentCaution`) + recorded the value-coincidence rather than reaching across the
  domain boundary. Same discipline the P3.3a note applies to team-color duplication.
- **D31. `WorkbenchYamlHighlighting.cs` syntax colours STAY code-defined (12 hex, VS-Code Dark+ roles).**
  Assessed against the task's "theme-aware named resources if a real payoff" bar and **left as-is**, because:
  (1) AvaloniaEdit **builds and CACHES the `IHighlightingDefinition` once** (`_definition ??=`) and does not
  re-theme live, so even *with* a Light palette, resource-sourcing wouldn't recolor the editor without
  added invalidate/rebuild plumbing; (2) there is **no Light palette** wired (theme finding, §intro) → zero
  payoff today; (3) the colours are **already centralized** as named `<Color name=…>` XSHD roles in one file
  (not scattered inline in views); (4) the code→resource→XSHD-string round-trip is init-order-fragile for a
  static cached definition. **Verdict: no real payoff for an awkward lookup, leave code-defined.** Revisit
  only if a Light palette AND a definition-rebuild-on-theme-change path both land, then source the
  `<Color>` foregrounds from `SyntaxKeyword/String/Comment/…` tokens and rebuild the definition on change.


- **D21. Playback2D gets its OWN `Pb2d*` domain palette in `DarkPalette.axaml`, not forced into the app's
  blue-purple chrome tokens.** The 2D tab is a game-radar HUD (cool-grey ramp + game-semantic status
  colors); zero of its 29 values exact-match an app token, so "reused 0 / added 29" is correct. Home =
  `DarkPalette.axaml` (app-level) over `UserControl.Resources` **on purpose**: app-level tokens can later
  be referenced from the renderer/VM to collapse the team-color triplication; scoped resources can't. See
  [Playback2D palette](#playback2d-palette).
- **D22. Scope = the AXAML only; the Skia renderer + `TeamColor` VM keep their named color symbols.**
  `Playback2DViewport.cs` holds ~34 colors as `static readonly` brushes/pens and `PlayerAttributes.TeamColor`
  is a named property: already centralized, not the "bare-inline-in-markup" the P0.3 backlog flagged.
  Converting Skia static-field `Color.Parse` initializers to `FindResource` is init-order/WASM-fragile and
  would risk the large Playback2D render-test battery for ~zero gain; left as a documented follow-up (D21's
  triplication note). The AXAML tokens' values match the renderer/VM copies exactly, so unifying later is
  mechanical.
  > **Superseded (recorded v0.6.0; landed with the theme-token promotion).** Both premises are now false in the
  > best way: the renderer's static fields were REPLACED by the token-resolved `CanvasPalette` bundle
  > (instance-built, `ActualThemeVariantChanged`-refreshed, `ThemeColors.Get` fallbacks, the pattern
  > that made the init-order risk moot), and `PlayerAttributes.TeamColor` was deleted in favor of
  > `IsT`/`IsCt` class flags → `Pb2dTeamT/Ct` tokens. The renderer's remaining ~31 inline hex are
  > design-time FALLBACKS, not owned colors. See the resolved triplication bullet in Open questions.
- **D23. Verified no-op by value-equivalence + one executing view-instantiation, not by A/B render.** Each
  token's hex == its original literal (exact, alpha included); `Playback2DHeadlessSmokeTests` constructs the
  real `Playback2DView` with a synthetic VM (no demo, so it EXECUTES rather than skips) → proves every
  `{StaticResource Pb2dX}` resolves at control-load (a typo/missing-token would throw there, not at compile).
  The rendered frame reads correct (team chips, HP-green, grey HUD ramp). No new UiCapture variant was added
  (the viewport needs a demo to show markers; the smoke test already instantiates the full view).

### Decisions (P3.2b, Library landing: Open Demo + recents + drag-drop)
- **D16. The Library empty state is the primary landing hero (the "enhance the Library tab" call).** No new
  welcome tab: the Library tab's `HasNoFolders` body became a `Border.card` hero (title / tagline /
  `.primary` Open Demo / `RECENT` list / drop hint). See [Landing hero](#landing-hero). The `welcome-proposed`
  mock (Variants.cs) was *illustrative content*; it now lives on `LibraryTabView`, not a parallel surface.
- **D17. De-duplicated Open-Demo: hero primary (empty) XOR compact `.primary` on the actions strip
  (populated), plus the always-present toolbar button.** The Parser no-file state was demoted from a
  competing "Open Demo…" surface to a text pointer ("open from the Library tab or the toolbar"), the
  `TODO(p3.2b)` is discharged. One prominent CTA per state, never two.
- **D18. Filter toolbar gated to the populated state (`!HasNoFolders`).** search/map/player/sort/view/refresh
  are meaningless with zero demos and (pre-existing) crowd/clip a narrow toolbar; hiding them in the hero
  state is a clean landing AND sidesteps that density for the empty case. **The populated filter toolbar's
  pre-existing narrow-width clip (verified ~700px) is untouched here: it belongs to the toolbar-overflow
  stream, not P3.2b.** The new right-docked actions strip (Open Demo / Recent ▾) is a `DockPanel` and does
  **not** clip at 700px.
- **D19. Drag-drop uses the Avalonia 11.3 `IDataTransfer` receive API, not the obsolete `DataObject` one.**
  `e.DataTransfer.TryGetFiles()` + `DataFormat.File` (CS0618-clean); the drag-over overlay binds a VM
  `IsDragOver` bool (reactive + headless-renderable via the `library-dropover` variant). WASM degrades via
  `CanDropFiles`. See [File-drop receive target](#drop-target).
- **D20. `RecentFileItem` reuses `DemoEntry.RelativeTime` (promoted to `public static`): no forked
  relative-time formatter.** The projection carries `OpenedAtUtc` and computes `MapDisplay`/`DateDisplay`/
  `Meta`/`RowOpacity` on the record (composition over duplication). No new DarkPalette tokens were needed
  (hero/overlay use existing `CardBg`/`PanelHeaderBg`/`AccentInteractive`/`TextValue`/`TextDim`).

### Decisions (P3.1, NavStrip responsive layout)
- **D13. NavStrip is a `DockPanel`, not a single horizontal `StackPanel`.** TO-BREAKPOINT is
  `Dock="Right"` (structurally cannot clip), CLOCK is `Dock="Left"` (playback always reachable), JUMP is
  the fill inside a horizontal `ScrollViewer`. Fixes the P0.3 clip (trailing amber buttons off the right
  edge). Verified `navstrip-real` at 880/1050/1300/1600: nothing clips; no-scroll threshold ~940px.
- **D14. Scroll the JUMP middle rather than collapse it into a `▾` flyout: a deliberate deviation from
  the literal "overflow flyout" ask and §4's "ScrollViewer last resort," made because the task refined it.**
  The task scopes this as a byte-identical *layout* fix ("reflow instead of clip; preserve EVERY
  command/binding/x:Name"). At the 880px floor only the single ~40px `⚙▾` event-filter button overflows:
  scroll keeps the other six JUMP buttons directly clickable, whereas a JUMP flyout hides all seven and
  needs a **second copy** of the JUMP buttons (duplicate `x:Name`s/bindings) that the byte-identical
  constraint forbids. Since right-docking already makes the breakpoint cluster un-clippable, the flyout's
  only job (avoid clipping) is moot. The `Dock="Top"` row auto-sizes, so the scrollbar grows the row a few
  px at narrow width rather than overlapping the buttons (verified worst-case at 880×36, bar sits in the
  bottom margin, buttons stay readable). The overflow-`▾` flyout remains the §4 default for *other* strips
  where a whole group genuinely can't fit.
- **D15. Right-dock (not flyout-collapse) the breakpoint cluster.** Keeps it "visually separate on
  purpose" (§2/§3 coherence model) and composes cleanly with its `IsBreakpointNavEnabled` gate: an
  invisible right-docked child reserves no space, so the consumer/power (gated-off) case reflows JUMP to
  full width with no dangling gap. No command/readout/gate changed; `navstrip-real` still renders nonBg
  2871 at the fitting 1280px width (byte-identical to pre-P3.1).

### Decisions (P2a-ii, Settings feature-toggle list)
- **D9. Locked rows = a disabled ToggleSwitch + a `LockHint` chip: for BOTH Required and group-followers.**
  Required → "required". A non-leader group member → "follows &lt;leader&gt;": the gate resolves a group
  atomically from its LEADER, so a follower's own override is inert: an independent toggle would persist a
  phantom that snaps back (the same failure class as D11's cascade, but *permanently* inert). Followers are
  locked; the leader's toggle drives the group live. Both setters are no-op bounces to the gate state
  (guards the programmatic path). The disabled Fluent toggle reads muted: accepted (brightening it needs a
  forbidden base `ControlTheme`); the chip carries the signal.
- **D10. Row refresh guards with a row-level `_applyingRefresh`, NOT the VM's `_writing`.** A `_writing`-only
  guard corrupts settings: an *external* category change refreshes rows while `_writing` is false and would
  write a spurious override for every row whose default shifted. The override write itself still goes
  through `Persist` (`_writing`). Traced single-write, no thrash.
- **D11. Cascade-masked child toggle snaps back: accepted as faithful.** Toggling a SubFeature on while its
  parent tab is off stores the override but the gate still resolves it off (cascade), so the switch bounces
  with the overridden dot shown. Not a loop (the refresh set is guarded); toggle-disabling stays scoped to
  `IsRequired` only.
- **D12. Overridden = amber dot + per-row `↺` clear; section `.ghost` "Reset to <Category> defaults".** The
  Settings VM + shell share the **singleton** `IFeatureGate` (composition root) so a toggle live-reconciles
  the app chrome/tabs AND the rows. See §5 "Settings feature-toggle list".

### Decisions (P1.3, design-system foundation)
- **D5. `Styles/Components.axaml` split into four role files** (Primitives/Cards/Tables/Chrome), loaded
  explicitly by `App.axaml` after FluentTheme. Components.axaml removed (csproj globs AXAML; the only
  references were the App.axaml include + a DebuggerPanel comment, both updated).
- **D6. Shared looks are additive style classes, NOT template `ControlTheme`s.** Fluent stays the base;
  no base `ControlTheme` on any built-in type → no regression on un-classed controls. Rationale +
  re-verify rule in §1 "Styles/ file layout".
- **D7. Promoted (not just defined) the clean-win duplicates:** NavStrip `.nav-btn`/`.bp-btn`/
  `.ctx-action`/`.group-label` and DebuggerPanel `.primary` (+ `.icon`→`.icon-btn`) moved to the shared
  files verbatim; local blocks deleted. Renders confirmed byte-identical in the default state
  (`navstrip-real` nonBg 2871;
  DebuggerPanel classes exercised in `primitives`). **Judgment call:** the global `.primary` also styles
  AnalysisTabView's "Apply" button, whose `Classes="primary"` was previously a **no-op** (no local
  style): a strict improvement (intended-but-unstyled → styled; inline `Padding`/`FontSize` win over
  the setters, so no layout shift). **Unverifiable on that tab** (Analysis hosts MSAGL, not
  headless-renderable), the *class* is verified in isolation via `primitives`.
- **D8. No new DarkPalette tokens**: the palette already covered every promoted look (see §1 token note).

### Decisions (P0.3 review)
- **D1. `docs/ui/design-system.md` is the canonical UI reference.** Supersedes all "UI v2 review §X"
  citations (the cited file never existed).
- **D2. NavStrip = navigation-review Option 1, already shipped.** No re-litigation; remaining work is
  responsive layout + gating the BREAKPOINT group, not a redesign of the nav model.
- **D3. Three breakpoint surfaces stay distinct** (act / manage / rule-graph). Do not merge.
- **D4. "Current" A/B halves use real controls; "proposed" halves are token-matched mocks.**

### Decisions taken 2026-07-15 (supersede the recommendations below)
- **Enhance the Library tab.** The Library landing tab's empty-state becomes the primary
  open surface: a first-class single-file "Open Demo…" + recent files + drop target, alongside the
  folder library. One landing surface: no new welcome tab. Recents/drop are net-new to `LibraryTabView`.
  De-duplicate the toolbar-vs-empty-state Open Demo copies (one primary on Library + one compact chrome).
- **Adopt both chrome redesigns.** Toolbar: fold Debugger/Output/Parse-Chain into a gated
  in-window `View ▾` overflow flyout (WASM-safe). NavStrip: collapse the amber TO-BREAKPOINT cluster
  into a right-docked overflow (fixes the <~1400px clip). Both gate that dev chrome to power/dev.
- **Functional consumer tab set.** Consumer defaults to Library + Stats + 2D Playback;
  Parser/Entity/Analysis/Authoring default to power-user+. (Feeds the P1.2 default matrix; every tab
  stays user-enableable in Settings.)
- **Power-User skip-wizard fallback.** A user who skips the first-run category picker defaults to
  Power-User.

### NavStrip consolidation review: resolved + shipped 2026-07-16
**Concept B (segmented pill) won, the tick stepper was dropped, and it is implemented in production**
(`NavStrip.axaml` + `EventFilterFlyoutViewModel`). See [Decisions D24–D27](#6-decisions-log--open-questions)
and [Consolidated SEEK/EVENT nav](#navstrip-redesign). The concept/feasibility record below is retained as
history.
- **Pass 1 (icons flag/ring/ruler) was rejected**: the icons "are just not good," and the
  deeper direction is a **model change, not a visual swap**: *"the controls really just need to be
  prev/next tick and prev/next event. Since we can configure the event to seek the next player_death,
  round_start or any event we want we should be able to consolidate."* Pass 2 acts on that.
- **Pass 2: recommend Concept B (segmented stepper pills).** Collapse the
  6-button JUMP group (`◀ev ◀rnd ◀tk` / `tk▶ rnd▶ ev▶`) **+ the hidden `⚙▾` event-filter flyout** down
  to **prev/next TICK + prev/next EVENT**, where EVENT carries a **prominent, always-visible, one-click
  event-TARGET dropdown** (reads e.g. `player_death ▾` / `Any event ▾` / `Round ▾`). "Round" is no
  longer a dedicated pair: it is just `event target = Round`; the dropdown also **subsumes** the old
  gear flyout (one visible control replaces two). Net **7 JUMP controls → 5**, and the thing "next event"
  seeks is on-screen instead of buried. **Concept B (segmented pills)** makes each stepper an obvious
  self-documenting unit and the target chip a clear dropdown, best for the consumer→dev superset (this
  group is default-visible to **all three** audiences, §5). **Concept A (inline ghost dropdown)** is the
  denser dev-forward fallback. Both keep the P3.1 responsive DockPanel + the ASCII clock/breakpoint
  glyphs (only JUMP→SEEK changes), and are chip-`MaxWidth`-capped so a long CS2 event name cannot
  re-break the P3.1 no-clip fix. Behavior maps 1:1 onto **real commands** (see feasibility below).
  Compare render: `navstrip-v2-compare`. **Reading the compare:** in the CURRENT (top) row the amber
  breakpoint buttons render cut off at 1040px: that is the *real* strip's own docking behavior (its 6
  JUMP buttons + `⚙▾` are wider than the 5-control SEEK, so it pushes its right-docked cluster off-screen
  sooner). Both concepts show their amber cluster in full at the same width, i.e. **the consolidation's
  compactness is exactly what lets the dev breakpoint cluster fit**, a point in the redesign's favor.
- **FEASIBILITY (confirmed, no protected files):** `NavPrev/NextTickCommand` and
  `NavPrev/NextEventCommand` already exist and already do exactly what pass-2 needs; the event target is
  the demo-derived `GameEventFilters` (`SelectedSpecialFilter()` → `SemanticNavigator.NextEvent(filter)`),
  today hidden behind `⚙▾`: pass-2 just surfaces it as the dropdown. **Round is expressible TODAY with
  ZERO backing change**: the individual `round_*` events are in the target vocabulary whenever the demo
  fired them, so `target = round_officially_ended` seeks rounds. **The one real delta is density:** the
  removed `NavPrev/NextRoundCommand` jumps to the *union* of all `round_*` events (via
  `StartsWith("round_")`), so it stops more often than any single round event. Also, **CS2 GOTV does NOT
  reliably emit `round_start`/`round_end` as round-lifecycle markers** (`DemoAnalyzer.DeriveRounds`:
  `round_freeze_end` opens, `round_officially_ended` closes), so making a consumer hunt for the exact
  event name is a discoverability trap. **Recommendation:** ship a small **"Round" preset** at the top of
  the target dropdown that maps to the `round_*` union (exactly reproducing the old round buttons),
  alongside "Any event" and optional "Kills"/"Bomb" presets, then the full demo-derived checklist for
  power/dev. This is **UI/VM-only** (a named selection over the existing `GameEventFilters` +
  `SemanticNavigator`); **no protected parser files, no new parser hook.** If the target list should ever
  guarantee every entry is seekable, populate it from `SemanticNavigator.EventBoundaryFramesByName` keys
  (the navigator's own index) instead of `AllGameEvents`, same source, guaranteed non-empty targets.
- **Target-chip STATE TEXT** the dropdown label must render: nothing/all selected → `Any event ▾`; one
  preset → its name (`Round ▾`, `Kills ▾`); one event type → that name (`player_death ▾`); N types →
  `N event types ▾`. (`EventFilterFlyoutViewModel.FilterTooltip` already computes this exact summary:
  reuse it for the chip label, don't fork.)
- **Settings sections/tabs: defer (keep the single scroll); rendered and confirmed 2026-07-16.**
  `SettingsView.axaml` is one vertical `ScrollViewer` of four `sectionHeader`-banded sections (USER
  CATEGORY, LIBRARY FOLDERS, THEME, FEATURES). The top three are compact and glanceable; only **FEATURES**
  (the P2a-ii per-feature override *table*) is long/growing. The earlier note here was **analysis-only**;
  this pass **built the real `TabControl` alternative on-branch, rendered it at true host sizes, read it,
  and reverted it**: upgrading the recommendation from reasoned to empirically-backed. What the renders
  showed:
  - **3-tab split (General / Library / Features): REJECTED empirically.** A **Library-only tab** (a
    2-folder list + one `Add Folder…` button) reads as the **sparse-and-empty anti-pattern**: ~470px of
    void under ~130px of content in a 520×640 window. General had a smaller but real void. Only the
    Features tab read well (contained). Mixed result → not a win. (Rendered `settings`/`settings-library`/
    `settings-features` at 520×640.)
  - **2-tab split (General = category+folders+theme | Features): cleaner but LATERAL, still not a clear
    win.** Merging the three compact sections into "General" fixed the sparse tab (General fills to ~480px,
    balanced) and isolated the growing Features list (contained, scrolls in its own pane); the tab bar
    "General | Features" has near-zero "which tab?" cost. **But the load-bearing fold math kills the main
    ergonomic argument:** the three common sections measure **~450px**, and the desktop-window body is
    **~590px** (520×640 window − footer) while the WASM-overlay body is **~616px** (560×720 overlay,
    `Margin=32` + docked footer). So **the incumbent single scroll already shows every common setting
    above the fold in both hosts**: you only scroll to reach FEATURES. Tabbing therefore converts
    "scroll to Features" into "click to Features": a **lateral** trade, not an improvement. Tabbing also
    does **not** shorten FEATURES (intrinsically one long list, it scrolls on its own tab too).
  - **Net.** The one genuine remaining benefit is **future scalability** (a growing FEATURES list never
    lengthens a shared scroll if isolated), but that is exactly the case the revisit trigger below already
    governs, and it has not fired. "Cleaner in isolation" ≠ "clearly beats the incumbent"; the bar
    ("only change it if it genuinely reads better", "might be better *later*") is not met. **Call:
    defer.** No code change shipped; `SettingsView.axaml` + `UiCapture/Variants.cs` were
    reverted so the incumbent single scroll stands.
  - **Trigger to revisit (unchanged):** top-level sections exceed ~6, **or** a second long section appears
    (e.g. Keybindings/Advanced). At that point adopt the **2-tab `TabControl` split proven clean here**
    (General grab-bag | Features), or a **left-rail `SplitView` nav** if sections have multiplied, both
    WASM-safe in-window. Cheap interim before that trigger: sticky `col-label` sub-headers inside the
    FEATURES card. Render paths for this weigh-in were ephemeral (`%TEMP%`), not
    committed: the decision is closed, so no mock variant was kept (unlike the still-open navstrip/welcome/
    toolbar forks).

### Recommendations as originally presented (rendered A/B) [historical, resolved 2026-07-15, above]
- **Global "Open Demo" placement.** *Recommend:* keep a single **compact** persistent chrome
  entry in the toolbar (always reachable, incl. no-file), and make the **primary** large call-to-action
  live on the consumer-first landing state. **De-duplicate** the toolbar-vs-empty-state copies so there
  is one primary + one compact. Ties to the P2 first-run wizard as the first-run entry.
  - **⚠ The genuine fork (not pre-resolved at the time):** the true fresh-launch surface today is
    the **Library tab** (`builtin.library`, Order -1, selected on startup), whose own empty state is
    *"Your demo library is empty → Add a folder"*: a **folder-based** browser (search/map-filter/sort,
    double-click loads → Parser). Its only single-file entry is the toolbar "Open Demo…". So the fork
    is: (a) **enhance the existing Library landing empty-state** with a first-class single-file
    "Open Demo…" primary (and optionally recent files), keeping ONE landing surface; vs (b) a **new
    welcome surface** parallel to Library. *Lean:* **(a)**: Library already owns startup; a second
    landing competes with it. Verify against `LibraryTabView` before building any "recent files"
    affordance (it does **not** have one today, folder-based, no recents/drop). The `welcome-proposed`
    mock is **illustrative empty-landing content**, not a proposal for a second tab.
- **Global Debugger + Output toggles.** *Recommend:* move both out of the always-visible toolbar
  into a **View overflow `▾` flyout** (in-window, **not** a native menu bar, WASM-safe) and **gate to
  power/dev**. They are dev chrome on every tab today.
- **NavStrip responsive + gating.** *Recommend:* collapse the trailing **JUMP + TO-BREAKPOINT**
  groups into a right-aligned region that overflows into a `▾` flyout below a width breakpoint; gate
  the TO-BREAKPOINT group to power/dev. Fixes the verified clipping.
- **Consumer default tab set.** *Recommend:* **functional reading**: consumer sees
  Library + Stats + 2D Playback (the viewing tabs); Parser/Entity/Analysis/Authoring are power+.
  Rationale: those four require a wire-format/entity/rule-engine mental model, matching the persona's
  own "not consumer" rubric and the P1.2 default matrix. Reject the literal "everything built-in."
- **Skipped-wizard fallback category.** *Recommend:* **Power-User.** dev⊇power⊇consumer:
  consumer would hide advanced surfaces the likely early audience wants; developer would expose raw
  diagnostics to someone who didn't opt in. Power-User is the safe middle.

### Open questions (defer)
- ~~Depth ramp promotion into `DarkPalette` as an indexed resource (currently code-owned, duplicated)~~
  **Done (v0.6.0)**: the hex-view ramp is the `HexSwatchSelected/Parent/Ancestor` tokens plus a new
  `HexSwatchAncestorDeep`; `BinaryPane` resolves them on attach (and on live theme switch) via `SetPalette`.
- ~~A `.primary`/`.secondary`/`.ghost` button ControlTheme trio in `Primitives.axaml` (P1.3)~~ **Done
  (P1.3)**: `.primary`/`.ghost` shipped; `.chip` covers the compact "secondary" toolbar role. See §2.
- **Broad adoption of the P1.3 classes** across tabs (swap inline styling → classes): P2/P3 per-tab
  streams; see the adoption guide in §2. Not done this pass (foundation only, to avoid churning
  un-rendered tabs).
- **MainView's local `TabControl#ShellTabs > TabItem` → `.shell-tab` class**: deferred (MainView shell
  is not headless-renderable; migrate during a P2/P3 shell pass).
- Inline-`.axaml`-hex de-inlining backlog: **complete (app-wide markup, P3.4).** `Playback2DView` (P3.3a),
  `LibraryTabView`/`RuleWorkbenchView`/`StatsTabView` (P3.3), and `HarvestFrameListControl` (P3.4, the
  **last** one, 21→0) are all tokenized. **Verified: `grep -rE '#[0-9A-Fa-f]{6,8}' --include='*.axaml'`
  over the whole app returns zero outside `DarkPalette.axaml`.** This closes the **markup** goal only, the
  **code-held colour** item was separate, and has since narrowed: the VM accent classifiers + syntax roles
  were promoted in **v0.6.0** (see D34's census update); the Playback2D Skia renderer copies remain (the
  triplication item below). See D32/D34 + the (now-resolved) promotion question just below.
- ~~**Promote code-owned classifier palettes into `DarkPalette`** (generalizes the depth-ramp item above):
  the frame/message-type accent classifiers (`Harvest*ViewModel`, `FrameGameEventViewModel`,
  `SubTickEventViewModel`, `ParserTabViewModel`, `OutputPanelViewModel`, `CommandPaletteViewModel`,
  `AnalysisMessageViewModel`, `BoolToBrushConverter`) + the Playback2D renderer/`TeamColor` copies (D22) all
  define colour in code (`switch(key) → Color`). Consolidating them is a larger, pixel-sensitive effort
  (indexed/keyed resources + code→`FindResource`), deliberately **out of scope** for the markup hex-sweep.~~
  **Resolved (v0.6.0)**: every classifier palette a view actually binds was promoted to theme tokens
  (`Classifier*`, the severity ramps, `HexSwatch*`, `Syntax*`; VMs now expose kind/severity *flags* that
  class-based styles map to tokens), and the never-bound palettes (`FrameGameEventViewModel`,
  `SubTickEventViewModel`, `AnalysisMessageViewModel`, `ParserTabViewModel.GetAccentBrush`) were recorded
  as dead: no tokens minted. Full accounting: D34's v0.6.0 census update.
- ~~**Collapse Playback2D team-color triplication** (`Pb2dTeamCt`/`Pb2dTeamT` == renderer `TeamCtBrush`/
  `TeamTBrush` == `PlayerAttributes.TeamColor`) by referencing the app-level tokens from the renderer/VM
  at runtime (a property/`FindResource` path, NOT static-init), deferred from P3.3a as out-of-scope +
  init-order-risky. Same for viewport-bg/grid/label shared values.~~
  **Done, and had been for weeks when this was re-audited (v0.6.0):** the renderer half landed with
  the theme-system T1a work (`BuildPalette` resolves `Pb2dTeamT`/`Pb2dTeamCt` et al. via `ThemeColors.Get`,
  refreshed on `ActualThemeVariantChanged`; `TeamTBrush`/`TeamCtBrush` are now instance reads of the
  resolved bundle, no static-init anywhere), and the VM half followed (`PlayerAttributes.TeamColor`
  deleted: the roster chip is class-driven onto `{DynamicResource Pb2dTeamT/Ct}`). The init-order risk
  described here died with the static fields. **The one genuinely remaining team-hue duplication** is the
  six DERIVED tokens that re-encode the team RGB inside composite values (`Pb2dCanvasSightlineT/Ct`
  `#70…`, `Pb2dCanvasConeT/Ct` `#3C…`, `Pb2dCanvasMarkerRingT/Ct` darkened): kept as separate
  theme-authored tokens ON PURPOSE: deriving them in `BuildPalette` from `Pb2dTeamT/Ct` would silently
  remove a drop-in theme's ability to tune cone alpha / ring shade independently. Revisit only if theme
  authors ask for auto-derivation.

---

## 7. Rendered review artifacts (P0.3)

Reproduce via `dotnet run --project src/App/DemoViewer.NET.UiCapture -c Release -- <variant>`
(outputs → `%TEMP%/demoviewer-uitests/`). Variants live in `UiCapture/Variants.cs`.

| Awkward area | Variants | A/B command | Recommendation |
|---|---|---|---|
| NavStrip responsive | `navstrip-real` (**now the fix**), `navstrip-proposed` (historical mock) | `navstrip-real --size 880x56` (and 1050/1300/1600) | **Done (P3.1)**: `navstrip-real` now renders the DockPanel fix: TO-BREAKPOINT right-docked (never clips), CLOCK pinned left, JUMP scrolls when narrow. Nothing clips at 880/1050/1300/1600; no-scroll threshold ~940px. The old clip-below-~1400 note was superseded by empirical renders (real fit width ~920px). `navstrip-proposed` is the pre-build mock (single `▾` overflow stand-in), kept for history. |
| Open Demo + Debugger/Output chrome | `toolbar-current`, `toolbar-proposed` | `ab toolbar-current toolbar-proposed --size 560x140` | **Proposed**: compact Open + `View ▾` overflow (WASM-safe flyout) holding gated Debugger/Output/Parse-Chain. |
| Landing empty-state primary | `welcome-current`, `welcome-proposed` | `ab welcome-current welcome-proposed --size 460x460` | **Illustrative**: consumer-first landing content (big Open Demo + recent + drop). NB: the real fresh-launch surface is the **Library** tab's empty state; `welcome-current` shows the Parser prompt, not Library's *"Your demo library is empty"*. The (a)-vs-(b) landing fork decides where this content lands. |
| Three breakpoint surfaces (D3) | `breakpoints-map` | (single, annotated static) | Coherence map: act / manage / rule-graph. Analysis surface **not** headless-renderable (MSAGL). |
| **NavStrip SEEK/EVENT consolidation (shipped)** | `navstrip-real` (real production strip, default `Any event`), `navstrip-real-target` (real strip, picked `player_death`, proves the chip + `MaxWidth` truncation) | `navstrip-real --size 1000x56` (+ `820x56`); `navstrip-real-target --size 1000x56` (+ `820x56`) | **Shipped**: Concept B won, tick stepper dropped. Single segmented event stepper `◀ <target chip ▾> ▶` replacing the 6 JUMP buttons + `⚙▾`. Chip = merged target selector (presets Any/Round/Kills/Bomb + checklist). Both states fit at 820px with room to spare (breakpoint cluster on). Production `NavStrip.axaml` + `EventFilterFlyoutViewModel` changed. |
| **NavStrip earlier passes (historical)** | pass 1 (rejected): `navstrip-redesign-a/-b/-c`, `-compare`, `-jump-*`, `navstrip-icon-probe`. pass 2 (superseded, mocks retired): `navstrip-v2-*` | — | Pass-1 icons "just not good"; pass-2 tick+event concepts superseded when the tick stepper was dropped. `navstrip-v2-*` variants removed from `Variants.cs`; pass-1 mocks kept for history. |

**Capture-capability results (proven this pass):** real controls render headlessly with a plain VM
DataContext (`navstrip-real` = real `NavStrip` + `new MainViewModel { HasFile = true }`); `DebuggerPanel`
is likewise renderable (plain `DebuggerViewModel`). Full `MainView` and any MSAGL `GraphView`
(Analysis) are **not**: mock the panel or use an annotated static.

### P1.3 foundation variants (design-system smoke + verification)
Reproduce with `dotnet run --project src/App/DemoViewer.NET.UiCapture -c Release -- <variant> --size WxH`.
The single-render window defaults to **640×360**: pass `--size` for taller variants or the top clips.

| Variant | Size | Exercises | Read result |
|---|---|---|---|
| `primitives` | 700×380 | `.primary` `.ghost` `.chip` `.nav-btn` `.bp-btn` `.icon-btn` `.field` `.mono` `.shell-tab` | `.primary` reads as a raised CTA; `.ghost` clean text-only; amber `.bp-btn` clearly distinct from grey `.nav-btn`; `.field`/`.mono` keep Fluent input chrome; `.shell-tab` selected accent OK. |
| `chrome` | 540×450 | `sectionHeader`/`sectionLabel` `group-label` `badge` `divider` `card` `card-flyout` `ctx-action` | `.card`/`.card-flyout`/`.ctx-action`/`.badge` ("12 events" pill) read well; `sectionLabel`/`sectionHeader` band is **intentionally whisper-quiet** (dim `TextLabel` on `PanelHeaderBg`), faithful, framed by panel context in-app. |
| `tables` | 560×600 | `data-list` (+ selection) `col-label`; **regression:** real `KeyValueTable` + `InspectorCard` | `.col-label` correctly a touch brighter than `.sectionLabel`; `.data-list` selection highlight OK; KeyValueTable delta (`100 → 90`) + InspectorCard header render unchanged under the new global styles: **no regression**. |
| `swatches` | (dflt) | all DarkPalette tokens | every token resolves post-split: no magenta fallbacks. |
| `navstrip-real` | (dflt) | real NavStrip after the promotion | byte-identical to pre-P1.3 (nonBg 2871); `.nav-btn`/`.group-label`/pill all correct via the global classes. |
| `library-landing` | 900×640 | **real** `LibraryTabView` + real VM, no folders → the P3.2b hero (2 seeded recents, one missing/dimmed) | Clean hero: `LIBRARY`+`Add folder` toolbar only, centered card with title / tagline / `.primary` Open Demo / `RECENT` (mirage bright, nuke dimmed = missing) / `+ Add a folder` / drop hint. Clear primary CTA + readable map·date. |
| `library-populated` | 900×640 (+700×520) | same VM with folders + 3 demo entries | Folder browser + the persistent right-docked `Open Demo…`/`Recent ▾` actions strip. Actions strip does NOT clip at 700px (the *filter* toolbar's pre-existing clip is toolbar-reflow territory, D18). |
| `library-dropover` | 900×640 | the landing with `IsDragOver=true` forced | The full-surface drop overlay: `ShellBg`@0.82 scrim + `AccentInteractive` frame + "⤓ Drop a .dem file to open". (A real drag can't be synthesized headlessly: the variant forces the VM flag; see D19.) |
| `settings` | 660×1500 | **real** `SettingsView` + real `SettingsViewModel`/`FeatureGate` (temp-dir svc, 2 seeded overrides); the P2a-ii feature-toggle list | grouped **TABS & SUB-FEATURES** (indented children) + divider + **GLOBAL CHROME**; scope chips (Tab/Sub/Chrome), toggles right-aligned; overridden rows (Stats off, Hex pane on) show the amber dot + `↺`; required rows (Library, Frame list) show the "required" chip + locked toggle; "N hidden for Power-User" + Reset all read clearly. |
| `workbench` (**new P3.3**) | 1200×680 | **real** `RuleWorkbenchView` + real `RuleWorkbenchTabViewModel` (shipped file selected → editor content + read-only; one seeded diagnostic) | Exercises all 3 workbench de-inline tokens: `AccentCaution` muted-gold "🔒 shipped (read-only)", `AccentErrorSoft` soft-red diagnostic location, `BorderTranslucent` pane outlines. Also confirms the **code-defined** `WorkbenchYamlHighlighting` colours render (Section magenta / Literal teal / Facet blue), validating D31. AvaloniaEdit **does** settle headlessly here (same as `RuleWorkbenchModuleTests`). |
| `framelist` (**new P3.4**) | 460×340 | **real** `HarvestFrameListControl` (Parser frame list) + mock `HarvestFrameRowViewModel` rows over a `MainViewModel` (7 varied frame types; one breakpoint-set, one selected) | Exercises the P3.4 tokens: `FrameRowSelectedBg` (selected row), `FrameMsgBadgeBg` (×N badges), `TextFrameType` (NAME col), plus the reused header/label/dim tokens + per-type accent pills + the red-dim breakpoint dot. **BEFORE/AFTER byte-for-byte identical** (D32). |

**P3.3 gate results:** solution `-c Release` builds clean (analyzers-as-errors, 0 errors; WASM/Browser head
compiles, only the pre-existing NativeFileReference warning); target-view App.Tests **47/47** green
(`StatsTabTests`, `LibraryShellTests`/`LibraryFilterTests`/`LibraryScoreTests`/`ZLibraryRenderTests`,
`WorkbenchHighlightingTests`, `RuleWorkbenchModuleTests`: confirms every `{StaticResource}` key resolves).
`library-populated` BEFORE/AFTER pixel-identical.

**P3.4 gate results:** solution `-c Release` clean (0 errors, 1 pre-existing NativeFileReference warning);
`framelist` BEFORE/AFTER **byte-for-byte identical** (`cmp -s`); Parser-tab App.Tests **6/6** green
(`UnknownMessageCardTests`, `ModuleFrameworkPhase3Tests`). App-wide `.axaml` hex grep = 0 outside DarkPalette.

Gate results (P1.3): App + Browser(WASM) + App.Tests all build clean; render-adjacent tests green:
NavStripTests 3/3, HarnessSmokeTest 1/1, StatsTabTests 5/5, PlayerDetailsTests 9/9, StatsVisibilityTests
4/4, UnknownMessageCardTests 3/3.
