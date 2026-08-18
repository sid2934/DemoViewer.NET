# Theme Token Catalogue — Authoring Reference

The contract for authoring a theme in the central theme system (see `the design notes in git history`).
A theme is **pure data**: a base palette it inherits plus a set of token overrides. Every colour the app
draws — markup panels via `{DynamicResource}`, the code-drawn 2D Skia canvas, the analysis graph, and the
syntax highlighter — resolves from the **one token namespace** below, so a new theme needs **zero per-file
changes**: override the tokens you care about, and everything else (including every FluentTheme base-control
colour) inherits your chosen base.

## Theme file format (JSON)

```jsonc
{
  "id": "egirl",                    // stable id — persisted in settings.json, used as the variant key
  "name": "E-Girl (Pink / Black)",  // display name shown in the Settings theme picker
  "base": "dark",                   // "light" | "dark" — Fluent + any omitted token inherits this
  "tokens": {
    "ShellBg":     "#0A0008",       // any subset of the token namespace below
    "AccentAmber": "#FF4FD8",       // ARGB or RGB hex: "#RRGGBB" or "#AARRGGBB"
    "TextBright":  "#FFC8F0"
    // …omitted tokens fall back to `base`
  }
}
```

- **Safe**: parsed to `Color`/`SolidColorBrush` in code — no object instantiation, no code execution. Unlike
  runtime AXAML, an untrusted drop-in can never run code.
- **Degrades gracefully**: an unparseable file is skipped; a single malformed token hex is dropped while the
  rest of the file loads; a missing `base` defaults to `dark`; a missing `name` defaults to the `id`.
- **Alpha is part of colour identity**: `#FFC107` and `#FFFFC107` are distinct. Many canvas/overlay tokens use
  an alpha channel deliberately (e.g. `Pb2dOverlayBg` `#CC15181C`).

## Adding a theme

**User drop-in** — drop a `<id>.json` into the themes folder and click **Reload themes** in Settings (no
restart):

- macOS: `~/Library/Application Support/DemoViewer.NET/themes/`
- Windows: `%APPDATA%\DemoViewer.NET\themes\`
- Linux: `$XDG_CONFIG_HOME/DemoViewer.NET/themes/` (default `~/.config/DemoViewer.NET/themes/`)

Then set `"Theme": "<id>"` in `settings.json` (or pick it in Settings). A user id that collides with a
built-in id is ignored (the built-in is protected).

**Built-in** — add a `NN-<id>.json` under `src/App/DemoViewer.NET/Themes/` (it is an `EmbeddedResource`); the
`ThemeRegistry` loads it at startup via the same parser. The `NN-` numeric prefix orders it in the picker
(built-ins load sorted by filename); the `id`/`name` come from the JSON, not the filename.
`01-high-contrast.json` and `02-egirl.json` are worked examples.

## What a theme can and cannot retint

- **Can**: every token below — all app surfaces, the text ramp, borders, accents, and the code-drawn 2D
  canvas / analysis graph / syntax highlighter.
- **Cannot** (inherits the base Light/Dark): FluentTheme's own base-control brushes (e.g. `ListBoxItem`
  selection, `ScrollBar`, default `SystemAccentColor`). A custom theme's controls therefore look like its
  base's Fluent controls. Extending the namespace to cover those is a possible future enhancement.

## Iterating visually

Render any app surface under a theme id (built-in or a drop-in) with the UiCapture tool:

```sh
dotnet run --project src/App/DemoViewer.NET.UiCapture -- swatches --theme high-contrast --out /tmp/hc.png
dotnet run --project src/App/DemoViewer.NET.UiCapture -- settings --theme egirl
# useful surfaces: swatches · settings · workbench (syntax) · playback2d-canvas · tables · library-populated
```

`--theme` accepts any registry id; a drop-in is re-scanned each run (set `DEMOVIEWER_CONFIG_DIR` to author
against a scratch folder).

## Token namespace (214 tokens)

Dark is the canonical base; the Dark/Light reference values below are what an omitted token inherits. High-
impact families for a new theme: **surfaces** (Shell/Panel/Card/Frame/Hex/Primary), the **Text ramp**,
**Borders**, **Accents**, and the code surfaces **Syntax**, **Graph**, **Pb2d canvas**.

### Shell — `Shell*` (1)

App window / root background.

| Token | Dark | Light |
|---|---|---|
| `ShellBg` | `#080816` | `#E7E8F2` |

### Debugger — `Debugger*` (1)

Parser debugger surface background.

| Token | Dark | Light |
|---|---|---|
| `DebuggerBg` | `#0A0A18` | `#E3E4EF` |

### Frame list — `Frame*` (4)

Parser frame-list row states + message badge.

| Token | Dark | Light |
|---|---|---|
| `FrameHeaderStripBg` | `#0A0A1A` | `#E5E6F1` |
| `FrameRowSelectedBg` | `#1C1C38` | `#D4D6EF` |
| `FrameRowHoverBg` | `#12122A` | `#E6E7F3` |
| `FrameMsgBadgeBg` | `#16162E` | `#E3E4F2` |

### Panels — `Panel*` (4)

Panel bodies + panel-header bars (+ hover states).

| Token | Dark | Light |
|---|---|---|
| `PanelBg` | `#0C0C1A` | `#EFF0F7` |
| `PanelHeaderBg` | `#0E0E1E` | `#E9EAF3` |
| `PanelHeaderHover` | `#0E0E24` | `#E1E2EE` |
| `PanelHeaderHoverDeep` | `#14142E` | `#DADCEA` |

### Hex view — `Hex*` (7)

RAW hex view banner/rows + the depth swatches (swatches are theme-independent — all four `HexSwatch*` tokens are held identical Dark/Light on purpose).

| Token | Dark | Light |
|---|---|---|
| `HexBannerBg` | `#141428` | `#E7E8F1` |
| `HexBannerBorder` | `#252548` | `#CBCCDD` |
| `HexRowSeparator` | `#1E1E38` | `#D8D9E6` |
| `HexSwatchSelected` | `#CC4C9EF5` | `#CC4C9EF5` |
| `HexSwatchParent` | `#8855BB8A` | `#8855BB8A` |
| `HexSwatchAncestor` | `#55C07C28` | `#55C07C28` |
| `HexSwatchAncestorDeep` | `#33907890` | `#33907890` |

### Header actions — `Hdr*` (1)

Header action button hover.

| Token | Dark | Light |
|---|---|---|
| `HdrActionHoverBg` | `#16163A` | `#D2D4E6` |

### Cards — `Card*` (1)

Message-card body background.

| Token | Dark | Light |
|---|---|---|
| `CardBg` | `#171726` | `#FCFCFE` |

### Primary button — `Primary*` (3)

Primary button fill/border/hover.

| Token | Dark | Light |
|---|---|---|
| `PrimaryButtonBg` | `#1A1A38` | `#DCDCF4` |
| `PrimaryButtonBorder` | `#2A2A54` | `#B0B2DE` |
| `PrimaryButtonHover` | `#252548` | `#D0D0EE` |

### Parse chain — `Chain*` (1)

Parse-chain summary badge.

| Token | Dark | Light |
|---|---|---|
| `ChainSummaryBadgeBg` | `#1A1A3A` | `#DEDFF2` |

### Borders — `Border*` (4)

The border ramp (subtle -> strong -> accent) + translucent hairline.

| Token | Dark | Light |
|---|---|---|
| `BorderSubtle` | `#1A1A32` | `#D3D4E2` |
| `BorderStrong` | `#1E1E34` | `#C6C7D8` |
| `BorderAccent` | `#252545` | `#C9CADD` |
| `BorderTranslucent` | `#33FFFFFF` | `#2A141430` |

### Text ramp — `Text*` (25)

The full text value ramp, dim -> bright, plus role-specific text tokens. TextOnAccent = text drawn on an accent fill.

| Token | Dark | Light |
|---|---|---|
| `TextHexGutter` | `#44446A` | `#7A7A98` |
| `TextLabel` | `#30305A` | `#8080A0` |
| `TextLabelAlt` | `#303060` | `#747490` |
| `TextDim` | `#404068` | `#6E6E90` |
| `TextDimAlt` | `#404070` | `#6E6E92` |
| `TextDimGray` | `#404060` | `#70708C` |
| `TextDimTeal` | `#40407A` | `#6C6C90` |
| `TextEntityStatus` | `#4A4A72` | `#64648A` |
| `TextStatusBar` | `#44447A` | `#66668E` |
| `TextHeaderField` | `#505080` | `#5E5E86` |
| `TextCardSize` | `#50508A` | `#5C5C88` |
| `TextChainSummary` | `#606080` | `#58587C` |
| `TextChainBadge` | `#6060A8` | `#56569C` |
| `TextMid` | `#7878A8` | `#50507C` |
| `TextHexBanner` | `#8080B8` | `#4E4E80` |
| `TextFrameInfo` | `#6868A8` | `#565688` |
| `TextHeaderHex` | `#9090C8` | `#46467E` |
| `TextFrameType` | `#9090C0` | `#46467C` |
| `TextEntityFieldVal` | `#9898C8` | `#42427A` |
| `TextBright` | `#A0A0D8` | `#3C3C70` |
| `TextFieldName` | `#A8A8D8` | `#3A3A6C` |
| `TextValue` | `#C0C0F0` | `#2E2E60` |
| `TextHexCell` | `#C8C8E8` | `#30305C` |
| `TextCardHeader` | `#D0CCF8` | `#28284E` |
| `TextOnAccent` | `#12121E` | `#12121E` |

### Accents — `Accent*` (8)

Interactive / highlight / amber / error / caution / info accents. `AccentInfo` is the log-severity Info accent (Diagnostics log).

| Token | Dark | Light |
|---|---|---|
| `AccentInteractive` | `#5050A0` | `#4A4A9E` |
| `AccentHighlight` | `#9C27B0` | `#9C27B0` |
| `AccentAmber` | `#FFC107` | `#A86200` |
| `AccentAmberOpaque` | `#FFFFC107` | `#FFA86200` |
| `AccentError` | `#E53935` | `#C62828` |
| `AccentErrorSoft` | `#E57373` | `#C85454` |
| `AccentCaution` | `#E0A030` | `#9A6A0F` |
| `AccentInfo` | `#26A69A` | `#00796B` |

### Classifier accents — `Classifier*` (16)

Accent-classifier roles promoted from code-held VM brushes (v0.6.0 code-color promotion). Two sub-families: 8 opaque bases and 8 `*Dim` variants.

> **The `*Dim` family carries `0xC0` alpha as part of its token identity.** Alpha is not a fade a
> consumer applies at draw time — `ClassifierRed` (`#F44336`) and `ClassifierRedDim` (`#C0F44336`)
> are distinct tokens (see "Alpha is part of colour identity" above). A theme overriding a `*Dim`
> token should supply its own 8-digit `#AARRGGBB` value; an opaque override changes the rendered
> weight, not just the hue.

| Token | Dark | Light |
|---|---|---|
| `ClassifierBlue` | `#2196F3` | `#1565C0` |
| `ClassifierGreen` | `#4CAF50` | `#2E7D32` |
| `ClassifierPurple` | `#9C27B0` | `#7B1FA2` |
| `ClassifierOrange` | `#FF9800` | `#E65100` |
| `ClassifierTeal` | `#009688` | `#00695C` |
| `ClassifierRed` | `#F44336` | `#C62828` |
| `ClassifierRedBright` | `#FF5252` | `#B71C1C` |
| `ClassifierSlate` | `#607D8B` | `#455A64` |
| `ClassifierRedDim` | `#C0F44336` | `#C0C62828` |
| `ClassifierOrangeDim` | `#C0FF9800` | `#C0E65100` |
| `ClassifierBlueDim` | `#C02196F3` | `#C01565C0` |
| `ClassifierPurpleDim` | `#C09C27B0` | `#C07B1FA2` |
| `ClassifierGreenDim` | `#C04CAF50` | `#C02E7D32` |
| `ClassifierTealDim` | `#C0009688` | `#C000695C` |
| `ClassifierSlateDim` | `#C0607080` | `#C0455A64` |
| `ClassifierSlateBlueDim` | `#C06060A0` | `#C04A4A9E` |

### Breakpoint — `Breakpoint*` (1)

RuleWorkbench breakpoint gutter: the disabled-breakpoint dot fill.

| Token | Dark | Light |
|---|---|---|
| `BreakpointDotDisabled` | `#303056` | `#9FA0C0` |

### Map accents — `Map*` (2)

Tuning tokens for the map-name-hash accent generator. `MapAccentNeutral` is the neutral/fallback map accent.

> **`MapAccentRef` is HUE-IGNORED.** Consumers decode ONLY its saturation and value (S/V) to tune
> the map-name-hash accent generator — the hue always comes from the map-name hash. Editing this
> token's hue does nothing; to change how vivid/bright generated map accents are, change its S/V.

| Token | Dark | Light |
|---|---|---|
| `MapAccentNeutral` | `#404068` | `#B0B0C8` |
| `MapAccentRef` | `#B85353` | `#853535` |

### Stat — `Stat*` (1)

Positive-delta stat colour.

| Token | Dark | Light |
|---|---|---|
| `StatPositive` | `#4CAF50` | `#2E7D32` |

### Delta — `Delta*` (1)

Delta-row highlight tint.

| Token | Dark | Light |
|---|---|---|
| `DeltaRowBg` | `#25FFC107` | `#25FFC107` |

### Library cards — `Library*` (8)

Library card overlay text/badges (painted over baked-dark thumbnails — usually left inheriting).

| Token | Dark | Light |
|---|---|---|
| `LibraryCardTextBright` | `#ECFFFFFF` | `#ECFFFFFF` |
| `LibraryCardTextMid` | `#C6FFFFFF` | `#C6FFFFFF` |
| `LibraryCardTextDim` | `#B4FFFFFF` | `#B4FFFFFF` |
| `LibraryCardTextFaint` | `#A0FFFFFF` | `#A0FFFFFF` |
| `LibraryCardBadgeBg` | `#B0000010` | `#B0000010` |
| `LibraryCardBusyTrack` | `#33FFC107` | `#33FFC107` |
| `LibraryCardScoreCt` | `#5BA9F4` | `#5BA9F4` |
| `LibraryCardScoreT` | `#F0B23C` | `#F0B23C` |

### 2D Playback — `Pb2d*` (58)

The 2D playback HUD + the code-drawn Skia canvas (grid, sightlines, rings, trails, smoke/fire, markers). CanvasBg/grid/label/team + panel/text are the high-impact ones.

| Token | Dark | Light |
|---|---|---|
| `Pb2dPanelBg` | `#1A1E24` | `#EDEFF3` |
| `Pb2dInfoBg` | `#181C22` | `#F2F3F6` |
| `Pb2dCardBg` | `#20262E` | `#FBFBFD` |
| `Pb2dGridSplitter` | `#22272E` | `#D2D7DE` |
| `Pb2dHudDivider` | `#33404A` | `#C6CCD4` |
| `Pb2dOverlayBg` | `#CC15181C` | `#E6EDEFF3` |
| `Pb2dKillFeedBg` | `#E6090B0E` | `#F0EDEFF3` |
| `Pb2dKillFeedBorder` | `#33FFFFFF` | `#22202020` |
| `Pb2dTextDim` | `#5C6670` | `#6A727C` |
| `Pb2dTextMid` | `#9AA4AF` | `#565E68` |
| `Pb2dTextBright` | `#C0C8D0` | `#363D47` |
| `Pb2dTextBrightest` | `#DDE3EA` | `#202429` |
| `Pb2dTextOnTeam` | `#101418` | `#101418` |
| `Pb2dGlyphBlind` | `#E6E6E6` | `#5C6BC0` |
| `Pb2dTeamCt` | `#4A90D9` | `#2F73BE` |
| `Pb2dTeamT` | `#E0A030` | `#C9821C` |
| `Pb2dPositive` | `#86C786` | `#86C786` |
| `Pb2dHealth` | `#7ED07E` | `#7ED07E` |
| `Pb2dArmor` | `#7FB6E6` | `#7FB6E6` |
| `Pb2dHeadshot` | `#F44336` | `#F44336` |
| `Pb2dWallbang` | `#FF9800` | `#FF9800` |
| `Pb2dNoScope` | `#00BCD4` | `#00BCD4` |
| `Pb2dFlashAssist` | `#B66CD8` | `#B66CD8` |
| `Pb2dAssist` | `#5BC0BE` | `#5BC0BE` |
| `Pb2dDefuser` | `#E0C040` | `#E0C040` |
| `Pb2dBomb` | `#E08040` | `#E08040` |
| `Pb2dAdr` | `#E0A878` | `#E0A878` |
| `Pb2dDefuseTime` | `#5AB0E0` | `#5AB0E0` |
| `Pb2dMapApprox` | `#C0A060` | `#C0A060` |
| `Pb2dCanvasBg` | `#15181C` | `#E7E9ED` |
| `Pb2dCanvasMinorGrid` | `#22272E` | `#D2D7DE` |
| `Pb2dCanvasMajorGrid` | `#2E3742` | `#BFC5CE` |
| `Pb2dCanvasLabel` | `#9AA4AF` | `#5A626C` |
| `Pb2dCanvasNeutral` | `#888888` | `#6E7178` |
| `Pb2dCanvasSightlineT` | `#70E0A030` | `#80C9821C` |
| `Pb2dCanvasSightlineCt` | `#704A90D9` | `#802F73BE` |
| `Pb2dCanvasConeT` | `#3CE0A030` | `#42C9821C` |
| `Pb2dCanvasConeCt` | `#3C4A90D9` | `#422F73BE` |
| `Pb2dCanvasConeNeutral` | `#2C888888` | `#386E7178` |
| `Pb2dCanvasRingShooting` | `#FFD400` | `#E0A800` |
| `Pb2dCanvasRingDamage` | `#F44336` | `#D32F2F` |
| `Pb2dCanvasRingBlinded` | `#FFFFFFFF` | `#5C6BC0` |
| `Pb2dCanvasRingDead` | `#555B62` | `#AAB0B7` |
| `Pb2dCanvasBomb` | `#F03A2E` | `#E0322A` |
| `Pb2dCanvasBombTrack` | `#40FFFFFF` | `#40202020` |
| `Pb2dCanvasBombDetonation` | `#FF5040` | `#E0322A` |
| `Pb2dCanvasBombDefuse` | `#40C4FF` | `#2F73BE` |
| `Pb2dCanvasSmoke` | `#66AEB6BD` | `#6C6F757F` |
| `Pb2dCanvasSmokeStroke` | `#88C8CED4` | `#8A565C66` |
| `Pb2dCanvasFire` | `#78FF6A1A` | `#82E85D18` |
| `Pb2dCanvasTrailHe` | `#FF5252` | `#D32F2F` |
| `Pb2dCanvasTrailFlash` | `#FFE082` | `#D69A00` |
| `Pb2dCanvasTrailSmoke` | `#B0BEC5` | `#78909C` |
| `Pb2dCanvasTrailMolotov` | `#FF7043` | `#E64A19` |
| `Pb2dCanvasTrailDecoy` | `#81C784` | `#43A047` |
| `Pb2dCanvasMarkerRingT` | `#C8881F` | `#A66A15` |
| `Pb2dCanvasMarkerRingCt` | `#357ABD` | `#285F9E` |
| `Pb2dCanvasMarkerRingNeutral` | `#666666` | `#8A8F96` |

### Message headers — `Msg*` (9)

Per-wire-family message-card header tints (net/svc/dem/cs/...).

| Token | Dark | Light |
|---|---|---|
| `MsgHeaderNet` | `#1A2D3F` | `#E3EDF8` |
| `MsgHeaderSvc` | `#1A2D1E` | `#E4F1E7` |
| `MsgHeaderDem` | `#2D2010` | `#F6EEDF` |
| `MsgHeaderCs` | `#221530` | `#EEE8F5` |
| `MsgHeaderClc` | `#0E2220` | `#E1F0EE` |
| `MsgHeaderGameEvent` | `#2D1414` | `#F7E7E7` |
| `MsgHeaderDefault` | `#1A1E22` | `#EAECF3` |
| `MsgHeaderUnknown` | `#2D1414` | `#F7E7E7` |
| `MsgHeaderSelected` | `#24243E` | `#D4D6EF` |

### Wire types — `Wt*` (14)

Wire-type chip bg/fg pairs (varint/fixed/bytes/string/bool/message/default).

| Token | Dark | Light |
|---|---|---|
| `WtVarintBg` | `#0E2040` | `#DCE6F5` |
| `WtVarintFg` | `#4E90E0` | `#2F73BE` |
| `WtFixedBg` | `#0E2A12` | `#DEEEE0` |
| `WtFixedFg` | `#4EB060` | `#2E7D32` |
| `WtBytesBg` | `#2A1800` | `#F4EBDF` |
| `WtBytesFg` | `#C07830` | `#A85E12` |
| `WtStringBg` | `#220030` | `#EFE4F5` |
| `WtStringFg` | `#A060C0` | `#8040A0` |
| `WtBoolBg` | `#001E28` | `#DEEEF2` |
| `WtBoolFg` | `#30A8C0` | `#14808F` |
| `WtMessageBg` | `#181830` | `#E6E6F0` |
| `WtMessageFg` | `#8080B0` | `#5252A0` |
| `WtDefaultBg` | `#1A1A1A` | `#E8E8EC` |
| `WtDefaultFg` | `#707070` | `#565656` |

### Property row — `Prop*` (1)

Selected property-row background.

| Token | Dark | Light |
|---|---|---|
| `PropRowSelectedBg` | `#28285A` | `#D0D2EC` |

### Syntax highlighting — `Syntax*` (13)

RuleWorkbench editor role colours (comment/section/kind/modifier/literal/event/facet/path/identifier/string/number/operator) plus `SyntaxPlain`, the 9th completion-badge role.

| Token | Dark | Light |
|---|---|---|
| `SyntaxComment` | `#6A9955` | `#008000` |
| `SyntaxSection` | `#C586C0` | `#AF00DB` |
| `SyntaxKind` | `#4FC1FF` | `#0070C1` |
| `SyntaxModifier` | `#569CD6` | `#0000FF` |
| `SyntaxLiteral` | `#4EC9B0` | `#267F99` |
| `SyntaxEvent` | `#DCDCAA` | `#795E26` |
| `SyntaxFacet` | `#9CDCFE` | `#001080` |
| `SyntaxPath` | `#9CDCFE` | `#001080` |
| `SyntaxIdentifier` | `#D7BA7D` | `#9B6C1E` |
| `SyntaxString` | `#CE9178` | `#A31515` |
| `SyntaxNumber` | `#B5CEA8` | `#098658` |
| `SyntaxOperator` | `#D4D4D4` | `#3B3B3B` |
| `SyntaxPlain` | `#9A9A9A` | `#6A6A6A` |

### Analysis graph — `Graph*` (30)

MSAGL analysis-graph canvas/nodes/edges/tables (active vs inactive vs root; edge kinds; per-player node border).

| Token | Dark | Light |
|---|---|---|
| `GraphCanvasBg` | `#0C0C1A` | `#F4F5F8` |
| `GraphGroupBg` | `#0E0E22` | `#ECEEF3` |
| `GraphGroupBorder` | `#1E1E42` | `#D0D4DE` |
| `GraphGroupLabel` | `#505080` | `#6A6E8C` |
| `GraphLabelBg` | `#F00C0C1A` | `#F0F4F5F8` |
| `GraphNodeActiveBg` | `#0A2550` | `#DCE8F8` |
| `GraphNodeActiveBorder` | `#3060A0` | `#3F72B8` |
| `GraphNodeActiveFg` | `#A0C8FF` | `#1A4A80` |
| `GraphNodeActiveSubFg` | `#4080C0` | `#3068A0` |
| `GraphNodeInactiveBg` | `#14143A` | `#EAECF3` |
| `GraphNodeInactiveBorder` | `#252545` | `#C8CCD8` |
| `GraphNodeInactiveFg` | `#606080` | `#4A4E68` |
| `GraphNodeInactiveSubFg` | `#30304A` | `#8286A0` |
| `GraphNodeRootBg` | `#141428` | `#E6E8F0` |
| `GraphNodeRootBorder` | `#303050` | `#C0C4D2` |
| `GraphNodeRootFg` | `#505068` | `#5A5E76` |
| `GraphNodePerPlayerBorder` | `#009688` | `#00695C` |
| `GraphEdgeActivate` | `#2E7D32` | `#2E7D32` |
| `GraphEdgeConjunction` | `#7986CB` | `#4A56A0` |
| `GraphEdgeDeactivate` | `#E65100` | `#D2691E` |
| `GraphEdgeDisjunction` | `#CE93D8` | `#9A4FB0` |
| `GraphEdgeLabel` | `#9090B0` | `#5A5E7C` |
| `GraphEdgeSetValue` | `#F9A825` | `#C8860A` |
| `GraphTableActiveCellBg` | `#0A2550` | `#DCE8F8` |
| `GraphTableBg` | `#0E0E22` | `#F0F1F5` |
| `GraphTableCellFg` | `#8090B0` | `#4A5270` |
| `GraphTableDimFg` | `#404060` | `#7A7E96` |
| `GraphTableGridLine` | `#1A1A3A` | `#D6DAE2` |
| `GraphTableHeaderBg` | `#141438` | `#E4E6EE` |
| `GraphTableHeaderFg` | `#A0C8FF` | `#1A4A80` |