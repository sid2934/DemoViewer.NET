# B1 T18: parity review

**Deliverable of B1 T18** (plan §4 T18, decision D-17, design risk 1). The plan asks for text
differences to be *written up and reviewed, not auto-failed*. In practice the review has to cover the
whole image rather than text alone, for a reason worth stating up front.

---

## 1. What is being compared, and why byte-exact was never on the table

The corpus entry `nuke-multilevel` is a matched pair captured in one push
(`Playback2DGoldenCaptureTests`):

- `tests/fixtures/playback2d/goldens/cpu/nuke-multilevel@900x900.png`: what the **pre-v2
  `Playback2DViewport`** drew, through Avalonia's `DrawingContext`, in a headless window.
- `tests/fixtures/playback2d/scenes/nuke-multilevel.scene.json`: the `Scene2DFrame` behind it.

B1 re-renders the JSON through the v2 compositor with raw `SKCanvas` calls and compares.

The two pictures come from two different rasterisation stacks. Avalonia builds its own paints,
geometries and layer stacks before reaching Skia; the compositor calls Skia directly. That difference
shows up in three places, none of which is a defect:

1. **Anti-aliased edges.** Where sub-pixel coverage rounds the other way, a single boundary pixel can
   differ by the full amplitude between the two colours it sits between.
2. **Image resampling.** The radar is a 1024 px image drawn into a ~1314 px destination rect. Two
   resamplers that agree to ±1 on most pixels still disagree on most pixels.
3. **Glyphs.** B1 embeds its own typeface (integrator correction 6) precisely so the CI golden lane is
   reproducible; the pre-v2 control asked the host for `Consolas,Menlo,monospace`. Different outlines,
   different hinting, different metrics.

A byte-exact assertion across that boundary is a test that can only fail. The gate is therefore
written against the **delta distribution** (`GoldenImageComparer.Analyze`, added in B1), and the
byte-exact half of the exit criterion is carried by `SceneDeterminismTests`, which pins the v2
renderer against itself.

---

## 2. Measured

`nuke-multilevel`, 900×900, de_nuke bundle bound, 2 levels, radar binding `Exact`.

| Per-channel delta ≤ | With text | Text layers off |
|---|---|---|
| 0 (bit-identical) | 63.63 % | 63.72 % |
| 1 | 92.95 % | 93.07 % |
| 2 | 96.96 % | 97.08 % |
| 8 | 99.45 % | 99.57 % |
| 32 | 99.72 % | 99.81 % |
| worst pixel | 204 at (48, 300) | 201 at (63, 276) |

**Read that first column again**: nearly two thirds of the frame is bit-identical across two different
renderers, and 97 % is within ±2. The port reproduces the pre-v2 geometry.

The two worst pixels are both single pixels, and both are exactly the expected kind:

- **(48, 300), delta 204:** golden `#C0CCC7` (radar), ours `#000000`. A black label glyph landing one
  pixel outside where Consolas put it. Text.
- **(63, 276), delta 201:** golden `#004515` (radar), ours `#C9821C` (T fill). One anti-aliased pixel
  on a marker disc's edge. Coverage rounding.

Removing the text layers moves every bucket in the right direction and drops the worst pixel from 204
to 201, which is the check `Geometry_WithoutText_IsAtLeastAsCloseAsTheFullFrame` makes: if hiding the
glyphs ever *stopped* helping, the difference would not be the typeface and this write-up would be
wrong.

### Gate

`GoldenParityTests` asserts **≥ 99 % within ±8** and **≥ 99.5 % within ±32**, a little below the
measured 99.45 % / 99.72 %. A real regression (a mis-placed layer, a wrong colour, a dropped pass)
moves whole regions and blows through both; resampling noise does not.

---

## 3. Two findings from the comparison

### 3.1 The corpus was captured under the Light theme

The first parity run reported **100 % of pixels differing** on a picture that is otherwise
pixel-for-pixel correct. The cause: the headless app resolves the **Light** theme variant, so the
pre-v2 PNGs have a light canvas, while a headless re-render has no theme system to ask and B0 shipped
only `ScenePalette.Dark`.

B1 adds `ScenePalette.Light`, transcribed from the app's Light theme dictionary
(`Styles/DarkPalette.axaml`), and the parity test renders through it. Worth knowing for C1 and C2: a
headless render's palette is an input, not a default, and getting it wrong looks exactly like a total
regression.

### 3.2 `SKFilterQuality.High` is a measurement, not a habit

SkiaSharp 2.88.9 predates `SKSamplingOptions`, so image sampling is a paint property. All four values
were measured against the golden:

| `FilterQuality` | within ±1 | within ±8 |
|---|---|---|
| **High** | **93.07 %** | **99.57 %** |
| Medium | 78.86 % | 99.58 % |
| Low | 78.86 % | 99.58 % |
| None | 76.49 % | 96.96 % |

`High` is the closest match, which says Avalonia's `DrawImage` resamples the same way. It is pinned in
`RadarLayer` with a comment pointing here; changing it re-baselines every radar golden.

---

## 4. Text metrics: the specific differences

> **Correction (fix/p2d-text-centering).** The table and sign-off below were wrong on the point they
> were most confident about, and the error shipped. They said B1 centred labels on "the **ink**
> bounds" and signed that off as an improvement over the pre-v2 layout box. B1 was centring on
> `SKTextBlob.Bounds`, which is **neither**: Skia computes a blob's bounds conservatively from the
> font's *global* glyph box, so `Left` was a constant `-0.7386 em` for every string and the width ran
> 2.2–5.5× the tight ink. Every marker initial drew **4.2–6.2 px left of its 9 px disc**. The
> corrected rows are below the original; the reasoning that reached the wrong answer is left visible
> on purpose, because "we compared the new thing to the old thing and preferred the new thing" is what
> a review is supposed to catch and this one did not: the comparison was against a description of the
> code, not against the pixels.

### As reviewed (wrong)

| Aspect | Pre-v2 | B1 | Consequence |
|---|---|---|---|
| Typeface | host `Consolas,Menlo,monospace` | embedded Inter Regular | different outlines and advances; the whole point of correction 6 |
| Positioning | `DrawingContext.DrawText(text, point)`, ink **top-left** | `SKCanvas.DrawText(blob, x, y, paint)`, **baseline** | converted via the blob's `Bounds.Top`; see `ShapedText.OriginForTopLeft` |
| Marker label centring | `center - (FormattedText.Width/2, Height/2)`, the **layout box**, which includes line height | `ShapedText.OriginForCentre`, the **ink** bounds | B1's labels sit a little higher inside the disc; visually better centred, and different |
| Floor label | 11 px at pane-local (8, 6) | same size, same point, top-left preserved | matches |

*Signed off, in error:* "The label change is an improvement (ink-centred text is centred; box-centred
text is centred on a box with descender space the string may not use), and the typeface change is a
requirement rather than a preference. Neither is a regression to fix."

### Corrected

| Aspect | Pre-v2 | v2 today | Consequence |
|---|---|---|---|
| Typeface | host `Consolas,Menlo,monospace` | embedded Inter Regular | different outlines and advances; the whole point of correction 6. **This is the only text difference that remains** |
| Positioning | `DrawingContext.DrawText(text, point)`, **line-box** top-left, not ink top-left (Avalonia's `FormattedText.Width` is an advance and `Height` a line height) | `SKCanvas.DrawText(blob, x, y, paint)`, **baseline** | converted as `y - Ascent`; see `ShapedText.OriginForTopLeft` |
| Marker label centring | `center - (FormattedText.Width/2, Height/2)`, advance and line box | `ShapedText.OriginForCentre` = `(cx - Advance/2, cy - (Ascent+Descent)/2)` | **identical placement rule**; only the outlines differ |
| Floor label | 11 px at pane-local (8, 6) | same size, same point, line-box top-left preserved | matches (it did not before: it drew at x ≈ 16) |
| Measurement source | `FormattedText` | `SKFont.MeasureText(glyphs, out SKRect ink)` + `SKFont.Metrics`, cached per (string, size) | tight ink for "where did the pixels land", advance and metrics for "where should this go" |

**Signed off.** The placement rule is now the pre-v2 rule exactly, so the only remaining text
difference is the typeface, which is a requirement rather than a preference. The parity gate improved
on every tier when the placement was fixed (pixels over ±8: 2629 → 2562; over ±32: 2087 → 2018, of
810 000), a small movement, because two different typefaces cannot agree on glyph *shape* and only
placement was ever available to fix. The regression gate is
`SceneLayerTests.MarkerLayer_LabelInk_IsCentredOnTheDisc`, which measures the ink rather than reading
the code. Full write-up: `B1-compositor-port.md` deviation 29.

---

## 5. Corpus state

| Entry | State |
|---|---|
| `nuke-multilevel` | captured and gated. Two floors, both radar layers, 10 labelled markers. |
| `full-scene-budget` | authored in code (`SyntheticScenes`), committed as JSON, gated by `BudgetFixtureCorpusTests`. Deliberately synthetic: a budget fixture must make every layer do its worst, and a captured frame that happens to be quiet would let a regression through. |
| `mirage-single-level` | **not captured.** Needs a de_mirage demo; the only demo in the tree is `assets/tour/sample-de_nuke.dem`. |
| `duel-mirage-b`, `fitmap-mirage-eco` | **not captured**, same reason. Both cases skip cleanly rather than failing. |

The nuke demo cannot stand in for the mirage entries: the names encode the map, and a nuke capture
filed under a mirage name is a corpus that lies. The three remain `SkipTestException` until a mirage
demo is staged, at which point `scripts/update-playback2d-goldens.sh` captures all of them.

---

## 6. Reproducing

```bash
# the parity numbers in §2
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release \
  -- --treenode-filter "/*/*/GoldenParityTests/*"

# re-capture the pre-v2 corpus (needs the demo; writes into tests/fixtures/playback2d/)
scripts/update-playback2d-goldens.sh
```

Failing runs write `<name>.parity-actual.png` and `<name>.parity-diff.png` (matching pixels desaturated,
differing pixels red) into the test binary's `artifacts/` directory.
