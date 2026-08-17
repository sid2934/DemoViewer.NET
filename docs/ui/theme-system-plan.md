# Central Theme System — Plan

## Goal (2026-07-16)
A centrally-managed theme layer where:
- **Adding a theme = one theme definition, with no per-file changes** — every current and future feature
  is themeable automatically.
- Built-in themes: **Dark, Light** (existing) + **High Contrast** + **E-Girl (pink/black)** as concrete goals.
- **Users drop a theme file into `<config>/themes/`** and reference it from `settings.json`; the app loads it.

## Status — shipped 2026-07-16, T1–T4 all landed
- **T1.** All ~71 code-held colours promoted to tokens; the syntax highlighter, 2D radar canvas, and
  Analysis graph resolve from the token namespace per variant (`ThemeColors` for App surfaces;
  `GraphStyle.FromTokens` for the Visualization graph). Dark byte-identical / logic-identical
  (per-surface gates). The "no per-file changes" foundation is in place.
  *Scope correction (v0.6.0):* an earlier draft of this line claimed "zero code-held theme colours remain" —
  overstated. T1 covered the radar canvas, Analysis graph, and syntax highlighter; the VM accent-classifier
  palettes (message/frame-type accents, severity ramps, hex depth ramp, palette result kinds) stayed
  code-held until v0.6.0, when they were promoted to the `Classifier*` / severity / `HexSwatch*` token
  families (see `theme-token-catalog.md`).
- **T2 core.** `Theme` model + `ThemeRegistry` (`RegisterCustom` → a
  `ThemeVariant(id, base)` + an override `ResourceDictionary` merged into `Application.Resources`).
- **T2 wiring.** `ThemeRegistry` is a DI singleton; `App.WireTheme` `Install`s it and
  resolves `AppSettings.Theme` (an id, case-insensitive) via `VariantFor` → `RequestedThemeVariant`; the
  Settings picker lists `registry.Themes` (DisplayName shown, Id persisted). No data migration (legacy
  capitalized values still resolve).
- **T3 drop-ins.** `ThemeJson` safe parser; `AppPaths.ThemesDirectory` +
  `EnsureThemesDirectory`; `ThemeRegistry.Reload()` scans `<config>/themes/*.json` (drops stale, protects
  built-in ids, reflects deletions); Settings "Reload themes" button + folder hint; the reload repaint =
  `WorkbenchYamlHighlighting.ClearCache()` + a **variant bounce** (Default → active), which re-resolves every
  `{DynamicResource}` and the code surfaces (they repaint on `ActualThemeVariantChanged`) — proven
  empirically in `ThemeReloadTests`.
- **T4 author.** **High Contrast** + **E-Girl** ship as embedded built-in token JSON
  (`src/App/DemoViewer.NET/Themes/*.json`), loaded by the registry ctor via the same parser as drop-ins — the
  proof that a built-in needs zero per-file changes. Visually verified (swatches / settings / workbench /
  2D canvas render correctly under both). Token-catalogue authoring doc: `docs/ui/theme-token-catalog.md`
  (191 tokens, families + roles + Dark/Light reference + the drop-in/iterate workflow). UiCapture `--theme`
  now accepts any registry id (built-in custom or drop-in).

## What Avalonia gives us natively (confirmed from source)
- **Custom `ThemeVariant` with inheritance:** `new ThemeVariant("egirl", ThemeVariant.Dark)` — the 2nd arg is
  the fallback base. Resource lookup walks **current variant → `InheritVariant` chain → `Default`**. So a
  custom theme authors only its *deltas*; every token it doesn't override — and every FluentTheme
  base-control colour — falls through to its base Light/Dark. (Cannot inherit `Default`.) This dissolves the
  "Fluent only understands Light/Dark" problem with zero extra work.
- **`ThemeDictionaries` is `IDictionary<ThemeVariant, IThemeVariantProvider>`** — custom-variant dictionaries
  can be registered **at runtime in code** (`themeDicts[variant] = resourceDictionary`).
- **`RequestedThemeVariant`** accepts any variant and re-resolves `{DynamicResource}` live.
- Runtime AXAML loading exists (`AvaloniaRuntimeXamlLoader`) but we **do NOT** use it for user themes:
  loading arbitrary AXAML from untrusted files = code execution. User themes = constrained **JSON**.

## The linchpin: one token namespace, resolved everywhere
"No per-file changes" holds only if every colour the app draws resolves from **one token namespace**:
- **Markup** already does — `{DynamicResource Token}` over the 98 palette tokens.
- **Code-held colours** — the 2D radar canvas (~30), the Analysis graph (~29), the syntax highlighter (~12)
  = **~71 colours** — are today local `Dark`/`Light` statics in three files. They must become **tokens**
  resolved from the app resources by key (a shared `ThemeColors` resolver against `ActualThemeVariant`,
  cached, re-applied on theme change).
- **After that there are zero code-held theme colours.** Any future feature that uses a token — in markup via
  `{DynamicResource}` or in code via `ThemeColors.Get(key)` — is themeable with no theme-specific work. **That
  is the guarantee.**

## Architecture
- **`Theme`** (model): `Id`, `DisplayName`, `Base` (Light|Dark), `Tokens` (key→colour), `Source` (builtin|user).
- **`ThemeRegistry`**: loads built-in themes (embedded) + scans `<config>/themes/*.json`; for each, builds a
  `ResourceDictionary` (SolidColorBrush + Color per token) and a `ThemeVariant(Id, base)`, and registers them
  into ONE app-owned `ThemeDictionaries`. Exposes the theme list + a `Reload()` for drop-ins.
- **`ThemeColors`** (code resolver): a cached key→brush/Color snapshot for the active variant + a `Changed`
  event; the three code homes consume this instead of local statics. One `ActualThemeVariantChanged` /
  registry hook refreshes the snapshot.
- **Wiring:** `AppSettings.Theme` = the theme **Id** (string). `App.WireTheme` resolves Id → `ThemeVariant` →
  `RequestedThemeVariant`. The Settings picker lists `ThemeRegistry.Themes` (built-in + user). A "Reload
  themes" affordance (or a folder watch) picks up drop-ins without a restart.
- **Token namespace = the theme CONTRACT.** The union of the 98 markup tokens + the ~71 promoted code tokens.
  A theme JSON supplies overrides for any subset; unspecified tokens inherit the base. This catalogue is the
  authoring reference.

## Theme file format (user drop-in) — JSON, safe
```jsonc
{
  "id": "egirl",
  "name": "E-Girl (Pink / Black)",
  "base": "dark",                 // light | dark — Fluent + unspecified tokens inherit this
  "tokens": {
    "ShellBg":      "#0A0008",
    "PanelBg":      "#140010",
    "AccentAmber":  "#FF4FD8",
    "TextBright":   "#FFC8F0"
    // …any subset of the token namespace; omitted tokens fall back to `base`
  }
}
```
Parsed to `Color`/`SolidColorBrush` in code — no object instantiation, no code execution. Built-in themes ship
as embedded JSON (or the existing Dark/Light AXAML dictionaries, wrapped by the registry).

## Phases
- **T1 — Token consolidation (the refactor, the bulk of the work).** Promote the ~71 code-held colours →
  tokens + a `ThemeColors` resolver; the 2D viewport / graph / syntax homes read tokens instead of local
  Dark/Light statics. **Gate: Dark byte-identical / logic-identical** (value-equivalence + the existing
  render gates). After T1, everything is token-driven.
- **T2 — Registry + variants.** `Theme` model + `ThemeRegistry` + the app-owned `ThemeDictionaries` populated
  with Dark/Light (re-expressed as themes) + custom-variant support. `WireTheme` resolves theme-Id; Settings
  lists themes. Dark/Light visually unchanged.
- **T3 — Drop-in user themes.** JSON schema + `<config>/themes/` scan + `settings.json` custom-theme refs +
  a reload affordance. Malformed/partial files degrade gracefully.
- **T4 — Author High Contrast + E-Girl + docs.** Two new built-in themes authored as pure token JSON — the
  proof that a theme needs zero per-file changes — plus a token-catalogue authoring guide.

## Decisions
1. **Theme file format = JSON** (safe, easy to author). Recommended over AXAML (which is code-exec on untrusted files).
2. **Do the full T1 token-promotion refactor** — required for the "no per-file changes" guarantee (otherwise the
   2D/graph/syntax surfaces still need per-theme edits). It's a refactor of working code, Dark-gated.
3. **Built-in set = Dark, Light, High Contrast, E-Girl.**

## Non-goals / risks
- Not touching protected parser files. Not using runtime AXAML for user themes (security).
- Risk: the T1 refactor touches three working, Dark-verified surfaces — mitigated by the Dark-identical gate
  per surface. The MSAGL graph isn't headless-renderable → its Dark-safety is logic-equivalence + in-app eyeballing.
- Custom-variant `ThemeDictionaries` must all live in ONE registry-owned collection so the inherit/fallback
  chain resolves across variants (design detail for T2).
