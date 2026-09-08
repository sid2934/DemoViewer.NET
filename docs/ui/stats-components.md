# Stats component library (v0.8.1)

Scope: the reusable UI primitives the overhauled Stats team view and player view will be built from.
Sections 1 to 8 are the research pass that decided what to build, what to buy, and what each piece owes
its caller. **Section 10 records what actually shipped**, including where the build diverged from the
plan. This document does **not** redesign the Stats screens; that is the next phase, and it can assume
everything here already exists.

Reference for the visual target: Leetify's match scoreboard and its utility breakdown table. What is
worth taking from them is not the styling, it is the **encoding**: a number carries two channels at
once (a colour that says good or bad, a bar length that says how far from the pack), and a third
channel, position in the table, stays free for sorting.

---

## 1. What the reference actually does

Decomposed, the two reference screens are five primitives repeated:

| Seen as | Primitive | Notes |
|---|---|---|
| Rating / HLTV / K/D / ADR / Aim cells | **Value with a background bar** | Text colour and bar length come from **different** scales. See below. |
| `67` `77` `58` rating pills in the utility table | **Value as a filled chip** | Same control, different presentation mode. |
| `28% / 24% / 27% / 21%` top strip; the `8 11 11 10` micro cell | **Stacked proportion bar** | Same control at two densities. |
| `+11.96` on the podium cards | **Ring gauge** | Single value on a circular track. |
| The gold star on the column leader | **Leader marker** | A per-cell flag, not a control. |

### The finding that matters: bar length and text colour are not the same scale

In the reference's Rating column the **bar** is proportional to the value's position among the ten
players in the lobby (peer relative), while the **text colour** is banded around zero: `+11.96` is
green, `-0.09` and `-0.62` and `-1.48` are plain white, `-2.82` is red. The HLTV column behaves the
same way around `1.00`. The Aim and Utility columns instead run their bars against an absolute `0..100`
domain.

Two consequences for the API:

1. A component that takes one `(Min, Max, Value)` triple and derives both channels from it cannot
   reproduce the reference. **Bar domain and colour sentiment have to be separately expressible**, even
   though the common case sets them together.
2. There is a **neutral dead zone**. Most values are uncoloured. That is why the table reads calmly and
   the outliers pop; a table where every cell is tinted communicates nothing. Any ramp we ship needs an
   explicit "no tint" band, and it should be wide.

### The other finding: bars are not colour coded

Every background bar in the reference is the same neutral slate, whatever the value's sentiment. Colour
lives on the text only. This avoids double encoding the same fact and keeps a fifteen column table from
turning into a heat map. We should copy it, with an opt in for the rare surface that wants a tinted bar.

---

## 2. What this repo already has

Relevant existing state, all in `src/App/DemoViewer.NET`:

| Where | What | Verdict |
|---|---|---|
| `ViewModels/Stats/ColumnCatalogue.cs` | `ColumnMeta` per column: display name, group, order, width, tooltip, `Emphasis`, aggregate. Already the single source of column presentation truth. | **Extend.** This is where a per column scale belongs. Its own comment already says "heat scales are Phase C". |
| `ViewModels/Stats/StatsTabViewModel.cs` `StatCell` | `Raw` plus `Meta`; derives `Display`, `Width`, `Alignment`, `IsPositive`, `IsNegative`. | **Extend.** `IsPositive`/`IsNegative` are the flat two state ancestor of the ramp. |
| `Views/Stats/StatsTabView.axaml` | `TextBlock.statsCell` with `.pos`/`.neg` classes; `StatsRowTemplate`. | Cell template becomes the new control. Row chrome stays. |
| `Views/Stats/PlayerDetailsView.axaml` | Hand rolled per round strips: a `Polyline` in a `Canvas` for kills, an `ItemsControl` of `Rectangle` for damage, a dot strip for KAST, `Rectangle.pdHistBar` for multi kills. | **Absorb.** One `Sparkline` with three modes replaces three of the four; the multi-kill histogram is the exception, see 10. |
| `ViewModels/Stats/PlayerDetailsViewModel.cs` | `FormBar(int Round, double Height, string Tooltip)`, `BarRowItem(..., double BarWidth, ...)`, `HistBarItem(..., double Height, ...)`. | **Delete the pixel maths.** These records carry **computed pixel heights and widths** in a view model. Normalisation is layout, it belongs in the control, and today it cannot react to a resize. Done for `FormBar` and `BarRowItem`; `HistBarItem` kept, see 10. |
| `Styles/DarkPalette.axaml` | 214 tokens per variant (219 after this work), `Dark` plus a designed `Light`, retintable by user themes. `Stat*` family currently holds exactly one token, `StatPositive`. Negative stat cells borrow `AccentError`. | **Extend `Stat*`.** Borrowing an *error* colour for a *stat* meaning is a live semantic overload worth splitting. |
| `Controls/` | 14 shared controls, flat, no subfolders. `SpotlightScrim.cs` is the precedent for a custom `Render(DrawingContext)` control. | New work went in `Controls/Stats/`, the first subfolder. |
| `DemoViewer.NET.UiCapture` | 60+ named variants, renders any control to PNG headless under any theme id. | The review loop for every component here. |
| `DemoViewer.NET.App.Tests` | Render smoke tests (`[Category("Render")]`, render then count non background pixels) plus plain unit tests. | Both tiers apply: scale maths is unit testable with no Avalonia at all. |

Nothing here is thrown away. The work is promotion: the hand rolled bar strips and the two state
colour rule become one scale model and a small set of controls.

---

## 3. Dependency survey

The repo policy is that a new NuGet reference has to earn itself (`Variants.cs`: "no new NuGet
dependency, a handful of glyphs does not justify a package"), and `Directory.Packages.props` pins
SkiaSharp at `2.88.9` as a **derived** pin that must equal what `Avalonia.Skia 11.3.12` resolves,
because the on screen path leases Avalonia's own `SKCanvas`.

Checked, September 2026:

| Candidate | License | State | Verdict |
|---|---|---|---|
| [`Avalonia.Controls.Charts`](https://avaloniaui.net/blog/charts) | **Commercial** | Included with Avalonia's Pro subscription tier. 70+ chart types, has gauges, heatmaps and a sparkline KPI card. | **Out.** Paid. This repo is MIT. |
| [`ScottPlot.Avalonia`](https://www.nuget.org/packages/ScottPlot.Avalonia) | MIT | `5.1.59` needs Avalonia `>= 12.0.0` and `ScottPlot >= 5.1.59`, which needs **SkiaSharp `>= 3.119.0`**. The last 11.x compatible build is `5.1.57` (Avalonia `>= 11.3.4`). | **Out.** ScottPlot 5.1 moved SkiaSharp 2.88 to 3.119; taking it means breaking the derived SkiaSharp pin, which breaks the Playback2D canvas lease. |
| [`LiveChartsCore.SkiaSharpView.Avalonia`](https://www.nuget.org/packages/LiveChartsCore.SkiaSharpView.Avalonia/) | MIT | `2.0.5`, needs Avalonia `>= 11.0.0`; `LiveChartsCore.SkiaSharpView 2.0.5` needs SkiaSharp `>= 2.88.9` plus `SkiaSharp.HarfBuzz`. | **Compatible, but wrong tool.** It is the only survivor of the pin check. It is still a chart library: the unit is a chart control with axes, series and a legend. A scoreboard needs 150 in-cell bars, not 150 charts. Revisit **only** if the player view later grows a real time series or a radar plot. |
| [`OxyPlot.Avalonia`](https://www.nuget.org/packages/OxyPlot.Avalonia) | MIT | Latest stable `2.1.0`, published **December 2022**, depends on Avalonia `0.10.11`. | **Out.** Three major Avalonia versions stale. |
| `Avalonia.Controls.DataGrid` | MIT | Official, has `FrozenColumnCount`. | **Out for now.** The current board is a hand rolled columnar `ItemsControl` "per the house pattern (EntityListView)" because the column set is dynamic and follows the loaded rules. Swapping the table engine is a much larger change than this phase, and it would not supply a single one of the five primitives above. |

**Conclusion: build, do not buy.** Not because the ecosystem is weak, but because the ecosystem
optimises for the wrong unit. Every candidate ships *charts*; what the reference screens are made of is
*cell sized encodings*, five of them, each well under 200 lines. The one library that would clear the
dependency gate would still leave all five to write.

---

## 4. The scale model

The core abstraction, and the thing that makes the components "automatic". Pure C#, no Avalonia
reference, therefore unit testable without a render harness.

```csharp
public enum StatPolarity { HigherIsBetter, LowerIsBetter, Neutral }

public sealed record StatScale(
    double Min,
    double Max,
    StatPolarity Polarity = StatPolarity.HigherIsBetter,
    double? NeutralLow  = null,
    double? NeutralHigh = null)
{
    double  Fraction(double value);   // 0..1, the BAR channel, clamped
    double  Sentiment(double value);  // -1..+1, the COLOUR channel, 0 inside the dead zone
}
```

`Min`/`Max` drive the bar. `NeutralLow`/`NeutralHigh` carve the dead zone out of the colour channel;
outside it, sentiment ramps linearly to the ends, sign flipped by `Polarity`. Both channels come from
one record, but they are independently steerable, which is what §1 established is required.

Factories cover the shapes actually needed:

| Factory | Reproduces |
|---|---|
| `StatScale.Absolute(0, 100, neutral: 45..65)` | Aim, Utility Rating: fixed domain. |
| `StatScale.FromPeers(values, polarity)` | Rating, ADR, K/D: bar relative to the other players on screen. |
| `StatScale.Banded(peerMin, peerMax, low, high)` | HLTV Rating: peer bar, colour banded around 1.00. |
| `StatScale.SignOf(zero, spread)` | Rating deltas: colour by sign with a dead zone at zero. |
| `StatScale.Penalty(max)` | Team damage, self damage: `LowerIsBetter`, anything above zero is bad. Replaces today's `Emphasis.Negative`. |

Peer scales are computed **per column across the rows currently on screen**, which means the view model
recomputes them whenever the row set or the category filter changes. That wiring is next phase work;
this phase only owes it `FromPeers`.

`ColumnMeta` gains an optional `StatScale?`. Columns without one keep exactly today's behaviour: no bar,
flat `Emphasis` accent. That keeps the change additive across all 74 catalogued columns and means user
authored columns degrade gracefully instead of rendering a meaningless bar.

---

## 5. Component inventory

Proposed home: `src/App/DemoViewer.NET/Controls/Stats/`. Six controls, one scale model, one style file.

| # | Component | Shape | Replaces / enables |
|---|---|---|---|
| 0 | **`StatPresenter`** | Abstract `TemplatedControl`. Holds `Value`, the scale properties, the four accent brushes, `Accent`/`Fraction`/`Sentiment`, and the automation name. | Added during the build, not in the plan. `StatValue`, `RingGauge` and `StatTile` all need the same twelve properties and the same sentiment-to-brush rule; three copies of it is exactly the near-duplicate the design system warns against. |
| 1 | **`StatValue`** | Custom drawn `Control`. `Value`, `Minimum`, `Maximum`, `Polarity`, `NeutralLow/High` (or one `Scale`), `Text` override, `Mode` = `Bar`/`Chip`/`Plain`, `IsLeader`, `TintBar`. | The workhorse. Every scoreboard cell, plus the utility table's rating pills, plus the tiles' values. |
| 2 | **`SegmentedBar`** | Custom drawn. `ItemsSource` of `(double Value, IBrush Brush, string Label)`, `Compact` bool, `ShowLabels`. | The `28%/24%/27%/21%` strip and the in-cell `8 11 11 10` quad. |
| 3 | **`Sparkline`** | Custom drawn. `Values`, `Mode` = `Line`/`Bars`/`Dots`, `Baseline`, `PointTooltips`, `IndexAt(point)`, own normalisation, resize aware. | Three hand rolled strips in `PlayerDetailsView` and the pixel maths in `FormBar`. `PointTooltips`/`IndexAt` were added during the build: the damage strip is a deep link, and a drawn control has no per-point visual for a tooltip or a click to land on. |
| 4 | **`RingGauge`** | Custom drawn. `Value`, `Scale`, `Thickness`, `Caption`. | The podium `+11.96` badge; any single headline number. |
| 5 | **`StatTile`** | Small templated control. `Label`, `Value`, `Delta`, `Scale`, `IsHero`. | Promotes the `pdTile`/`pdTileValue`/`pdTileLabel` style trio in `PlayerDetailsView` into a real thing with sentiment colouring. |
| 6 | **`TeamBadge`** | Small templated control. `Team` (CT/T), `Label`, `Outcome` (Win/Loss/None). | The `My Team [WIN]` / `Enemy Team [LOSS]` section header, and the CT/T bullet, both currently inline. |
| - | `StatScale` + `StatPolarity` | Records, no Avalonia | §4. |
| - | `Styles/Stats.axaml` | Style classes | Column header with sort caret, spanning group header, table row chrome. Style classes, not controls: they are setter collections, and the design system explicitly warns against forking near duplicate controls. |

**Deliberately not built this phase**

- No new table engine. `Avalonia.Controls.DataGrid` and column virtualisation are a separate decision.
- No column visibility gear per column (the reference has one). That is a settings surface, not a
  component, and it depends on where per demo view state gets persisted.
- No avatar, rank badge or agent art. Those are asset pipeline questions, not components.
- **No composite ratings.** The reference's Aim Rating and Utility Rating are Leetify's own models. This
  engine has no equivalent column in `ColumnCatalogue`. The components must therefore not assume a
  `0..100` domain exists; `StatScale.Absolute` is available for when such a metric is added, and nothing
  more is implied.

---

## 6. Colour

Five new tokens joining the existing `StatPositive` in the `Stat*` family, all retintable by a user
theme like every other token:

| Token | Role |
|---|---|
| `StatPositive` | **Exists.** Strong good. |
| `StatPositiveSoft` | Mild good. |
| `StatNegativeSoft` | Mild bad. |
| `StatNegative` | Strong bad. Splits the current overload where stat cells borrow `AccentError`, an *error* colour used for a *stat* meaning. |
| `StatBarTrack` | The unfilled part of a cell bar. |
| `StatBarFill` | The filled part. Neutral by default, per §1. |

**Neutral is the absence of a token, not a token.** A value inside the dead zone paints no foreground
override and inherits the row's text colour, which is already theme correct in Dark and Light. That
avoids duplicating an existing value, which the naming policy forbids.

Both variants got authored values and a WCAG contrast check against `CardBg`, `PanelBg` and
`PanelHeaderBg`, following the L1 light palette precedent. **Measured, worst surface first**
(`PanelHeaderBg`): Dark runs 4.7:1 to 7.1:1 across the four ramp stops; Light was darkened until every
stop cleared 4.5:1, landing at soft-good 4.99, soft-bad 4.56, bad 4.54. `StatPositive` was left alone
at its shipped value (4.28 on that band) rather than changed under existing consumers.

**On red and green.** Red to green is the convention every CS2 stats site uses and what players read
fluently, so it is the default. The accessibility mitigation is structural rather than chromatic:
the bar length is a **second, non chromatic encoding of the same fact**, the dead zone means most cells
carry no colour at all, and sentiment strength is also carried by how saturated the text is against a
constant background. On top of that, the theme system makes the ramp a **six token retint**, so a
blue/orange or purple/green drop in theme is a JSON file rather than a code change. Ship the
conventional ramp; ship a colour vision friendly built in theme if it is asked for.

---

## 7. Rendering approach

All five drawing components override `Render(DrawingContext)` rather than composing a template, which
matches `SpotlightScrim` and `Playback2DViewport`.

Why, concretely: a scoreboard is 10 rows by 15 columns and the player round table is 25 by 13, so cell
count runs 150 to 325. A templated cell is a `Border` plus a `Rectangle` plus a `TextBlock`, roughly
three visuals and a converter binding each; a drawn cell is one. Both are comfortably within what
Avalonia handles, so this is not a performance rescue, it is about the API: composing bar, text, chip
fill and leader marker in one `Render` is a few lines, whereas the templated equivalent needs a
converter per channel and a `MultiBinding` to combine them.

The costs are real and are accepted:

- **Text layout is manual.** `FormattedText` built from the control's own `FontFamily`/`FontSize`/
  `Foreground`, cached, invalidated on any of those changing. Trimming is on us.
- **Accessibility is manual.** Each control sets `AutomationProperties.Name` from its value and label,
  because there is no `TextBlock` to read. Non negotiable; write it in the first version, not later.
- **Theme changes must invalidate.** Brushes arrive as styled properties fed by `{DynamicResource}`, so a
  variant switch fires property changed, which calls `InvalidateVisual`. This is exactly the mechanism
  the palette's `DynamicResource` migration exists to support, and it needs a test.

`StatTile` and `TeamBadge` are ordinary templated controls; they are layout and text, with no custom
geometry to draw.

---

## 8. Verification

Three tiers, all of which already existed in the repo:

1. **Unit, no Avalonia.** `StatScaleTests`, 21 cases: clamping, dead zone, polarity inversion, one-sided
   bands, swapped band edges, and every degenerate range (`Min == Max`, empty peer set, `NaN`, a band
   sitting on the domain edge). This is where the correctness lives and it needs no render harness.
2. **Render smoke**, `StatsComponentRenderTests`, `[Category("Render")]`. Five cases. Two are worth
   calling out:
   - `Gallery_PaintsEveryTierOfTheHeatRamp` looks for the four Dark ramp values in the frame. Counting
     "non background" pixels does **not** work here: at the headless Default variant the whole canvas is
     light, so that count saturates at the window area and a blank window passes it.
   - `Gallery_RepaintsWhenTheThemeVariantChanges` asserts Dark and Light frames differ byte for byte.
     Identical frames would mean a colour is held in code rather than resolved from a token.
3. **Visual review**, `UiCapture`: `stats-components` (a board-shaped gallery, because what needs
   reviewing is whether the ramp reads down a realistic column) and `stats-components-edge`. Rendered
   and read under `--theme dark`, `--theme light` and `--theme egirl`; the custom theme is the one that
   proves the five new tokens retint like every other token.

**Harness bug found and fixed while writing tier 2.** The headless framebuffer hands back **RGBA**, not
the BGRA the existing render helpers assume. The existing helpers never noticed because they only ask
`r > 60 || g > 60 || b > 60`, which is channel-order independent. A colour assertion is not: with red
and blue swapped, `#4CAF50` still matched (its red and blue are 4 apart) while `#5FA894` silently found
zero pixels. `ToBytes` now normalises by the framebuffer's declared format.

---

## 9. Open questions for the next phase

- Where do peer scales get recomputed, and against which row set: the whole lobby, or only the rows the
  active category filter is showing? The reference uses the whole lobby. Filtering changes bar lengths
  under the user if we scope it to visible rows.
- Do totals rows participate in the peer min/max? They should not; a team total would flatten every
  player bar.
- Which of the 74 catalogued columns get a scale in the first pass, and what are the band thresholds for
  the judgement calls (ADR, KAST%, HS%)? Thresholds are a domain decision, not a UI one.
- The reference's per column gear implies persisted per view column selection. Out of scope here, but it
  determines whether `Styles/Stats.axaml`'s column header needs an action slot from day one.

---

## 10. What shipped

Everything in section 5, plus `StatPresenter`. Seven types in
`src/App/DemoViewer.NET/Controls/Stats/`, five new palette tokens in both variants,
`Styles/Stats.axaml`, two `UiCapture` variants, and 26 tests.

**The library has one real call site.** `PlayerDetailsView` was migrated off its hand rolled geometry,
which is what proves the components work in a live view rather than only against mock data:

| Was | Now | What that deleted |
|---|---|---|
| `Polyline` in a `Canvas`, points pre-measured in the view model | `Sparkline` (Line) | `FormTimelineViewModel.KillPoints`, and the `EmptyPoints` null-object that existed only because `PolylineGeometry` throws on a null point list **during render** |
| `ItemsControl` of `Rectangle`, heights pre-measured | `Sparkline` (Bars) | `FormBar` record, `SparkHeight`/`BarBoxHeight` constants |
| `ItemsControl` of `Ellipse` with `.filled` | `Sparkline` (Dots) | `FormDot` record, the `Ellipse.pdKastDot` style pair |
| `BarRowTemplate`'s `Rectangle Width="{Binding BarWidth}"` | `StatValue` (Bar) | `BarRowItem.BarWidth`, two `TrackWidth = 150` constants |
| Duel gauge `Rectangle Width="{Binding DuelGaugeWidth}"` | `StatValue` (Bar) | `DuelGaugeWidth`, `KvTrackWidth` |

No view model in `PlayerDetailsViewModel.cs` computes a pixel any more. The two `StatValue` bars above
pass `Polarity="Neutral"` and an empty `Text`, and keep their original track and fill brushes, so the
migration is behaviour-preserving: weapon kills are a magnitude, not a judgement.

**Deliberately not migrated: the multi-kill histogram** (`HistBarItem.Height`, the `2K/3K/4K/Ace`
columns). It was on the list, and it should not have been. It is four bars each carrying a count above
and a label below, and `Sparkline` draws a series with no per-point labels: converting it would drop
both labels to remove eight lines of arithmetic. None of the six components models a labelled bar chart,
and inventing a seventh for four bars is not worth it. `HistBarItem.Height` stays.

**Also unchanged:** the opening-duel tick strip is a row of glyphs (▲/▼/·) with per-round tooltips, not
a chart. It stays as an `ItemsControl`.

### Known rough edges

- `StatValue` sizes its own text with `FormattedText` and therefore owns trimming. `MaxLineCount = 1` is
  set explicitly, because `FormattedText` **wraps** by default once `MaxTextWidth` is set: without it a
  six digit value in a 48px column stacks over its own decimals instead of ellipsing. Found in the edge
  gallery, not in review.
- `Sparkline` in `Bars` mode draws the floor, not the vertical centre, when the domain collapses. The
  shared `YFor` answer (centre) is right for a line through a flat series and wrong for bars, where it
  made a set of all zero rounds render as half height columns that read as real data.
- `DisplayText` folds negative zero to zero. A differential column landing exactly on nothing otherwise
  prints `-0`.
- The scoreboard itself is untouched. `StatsTabView` still uses `TextBlock.statsCell`, and no column in
  `ColumnCatalogue` has a `StatScale` yet. That is section 9's work and it is deliberately not started.
