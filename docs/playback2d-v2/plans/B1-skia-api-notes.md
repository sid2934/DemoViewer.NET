# B1 T0 — SkiaSharp 2.88.9 API notes

**Deliverable of B1 T0** (plan §4 T0, risk R1). Probed by a throwaway TUnit case against the exact
assembly the app resolves — `SkiaSharp 2.88.0.0` (file version 2.88.9), the one
`Avalonia.Skia 11.3.12` brings and therefore the one whose `SKCanvas` an
`ISkiaSharpApiLeaseFeature` hands the custom draw op.

**Every later B1 task uses the overloads listed here and nothing else.** If a call site wants an
overload that is not in this file, re-probe first — do not assume the modern (3.x) shape.

---

## 1. The headline: this is the *pre*-`SKSamplingOptions` API

| Probe | Result |
|---|---|
| `SkiaSharp.SKSamplingOptions` | **absent** |
| `SKPaint.FilterQuality` | **present**, type `SkiaSharp.SKFilterQuality` |
| `SKCanvas.DrawImage(SKImage, SKRect, SKSamplingOptions, SKPaint)` | **absent** |

Consequence for `RadarLayer` (T6) and risk R4: image sampling is configured **on the paint**
(`paint.FilterQuality = SKFilterQuality.High`), never through a sampling-options argument.

## 2. Chosen overloads

### Pictures (`LayerPictureCache`, T2)

```csharp
SKPictureRecorder rec = new();
SKCanvas recording = rec.BeginRecording(SKRect cullRect);   // returns SKCanvas, not owned
SKPicture picture   = rec.EndRecording();                   // caller owns → Dispose

canvas.DrawPicture(SKPicture, SKPaint?);                    // PerCamera replay (pane-local space)
canvas.DrawPicture(SKPicture, ref SKMatrix, SKPaint?);      // Static replay under the camera matrix
```

`DrawPicture(picture, ref matrix, paint)` exists and takes the matrix **by ref** — the world-space
`Static` replay path (decision D-6) uses it rather than `Save`/`Concat`/`Restore`.

### Images (`RadarLayer`, T6)

```csharp
canvas.DrawImage(SKImage, SKRect dest, SKPaint?);           // the one B1 uses
canvas.DrawImage(SKImage, SKRect source, SKRect dest, SKPaint?);
```

The pre-v2 `PushOpacity(0.9)` + `DrawImage(bitmap, dest)` becomes one paint with
`Color = new SKColor(255, 255, 255, 229)` (`0.9 * 255 = 229.5 → 229`, matching `(byte)(0.9*255)`)
and `FilterQuality = SKFilterQuality.High`. Dest rect is still computed in **screen** space from
`WorldToScreen(MinX, MaxY)` → `WorldToScreen(MaxX, MinY)` (parity invariant 9), so R4's "sampled
under a world matrix" case never arises.

### Text (`TextBlobCache`, T8/T9)

```csharp
SKTextBlob? blob = SKTextBlob.Create(string, SKFont);       // NULLABLE — empty string returns null
canvas.DrawText(SKTextBlob, float x, float y, SKPaint);     // NOTE: SKPaint, not SKFont

int    n       = font.CountGlyphs(text);
font.GetGlyphs(text, glyphs);                               // Span<ushort>, stackalloc-able
float  advance = font.MeasureText(glyphs, out SKRect ink);  // the measurement B1 should have used
SKFontMetrics m = font.Metrics;                             // Ascent < 0, Descent > 0
```

> **Correction (fix/p2d-text-centering).** This section previously said `SKFont.MeasureText` "accepts
> **only** `ReadOnlySpan<ushort>` (glyph ids) — there is no `MeasureText(string, out SKRect)`", and
> concluded that measurement therefore had to come from `SKTextBlob.Bounds`. The premise is true and
> the conclusion does not follow: taking glyph ids is not the same as being unusable. `SKFont.GetGlyphs`
> converts a string to ids into a caller-provided `Span<ushort>`, so
> `MeasureText(ReadOnlySpan<ushort>, out SKRect, SKPaint?)` **is** reachable in 2.88.9 and is the only
> API here that returns tight ink.

**`SKTextBlob.Bounds` is not ink.** Skia computes a blob's bounds *conservatively*, from the font's
global glyph box rather than from the glyphs in the run. Measured on the embedded Inter Regular:

| | `blob.Bounds` | `MeasureText(glyphs, out ink)` |
|---|---|---|
| `"AA"` @ 10 px | `L=-7.386 T=-10.909 W=39.979 H=14.105` | `L=0 T=-8 W=13.761 H=8`, advance `13.523` |
| `"WW"` @ 10 px | `L=-7.386 T=-10.909 W=42.706 H=14.105` | `L=0 T=-8 W=19.489 H=8`, advance `18.977` |
| `"7"` @ 10 px | `L=-7.386 T=-10.909 W=33.217 H=14.105` | `L=0 T=-8 W=6.000 H=8`, advance `5.710` |

`Left` is the same `-0.7386 em` for every string; `Top`/`Bottom` are exactly `SKFontMetrics.Top`/
`Bottom`; the width runs 2.2–5.5× the real ink. Centring on `blob.Bounds.MidX` therefore drew every
marker label 4.2–6.2 px left of its 9 px disc — correct arithmetic over the wrong rectangle. What
`TextBlobCache` caches alongside the blob is now the tight ink, the advance, and `Ascent`/`Descent`.

`DrawText(SKTextBlob, x, y, SKPaint)` positions the blob's **baseline origin** at `(x, y)`; the
pre-v2 `context.DrawText(text, point)` positioned the text's **line-box top-left** (Avalonia's
`FormattedText.Width` is an advance and its `Height` is a line height — not an ink box). The
conversions are therefore `y - Ascent` for a top-left and `(cx - Advance/2, cy - (Ascent+Descent)/2)`
for a centre, which is exactly what the pre-v2 call did.

Note also that `SKFont.BaselineSnap` defaults to **true**, so the drawn baseline is rounded to a whole
pixel. Sub-pixel vertical placement is not available and is not wanted: it is what keeps a row of
labels on one line.

### Arcs (`BombLayer`, T7)

```csharp
path.ArcTo(SKPoint r, float xAxisRotate, SKPathArcSize, SKPathDirection, SKPoint xy);
```

This is the exact analogue of Avalonia's
`StreamGeometryContext.ArcTo(Point, Size, double rotationAngle, bool isLargeArc, SweepDirection)`:

| Avalonia (pre-v2, line 1385) | SkiaSharp 2.88.9 |
|---|---|
| `new Size(radius, radius)` | `new SKPoint(radius, radius)` |
| `0` (rotation) | `0f` |
| `sweep > 180` | `sweep > 180 ? SKPathArcSize.Large : SKPathArcSize.Small` |
| `SweepDirection.Clockwise` | `SKPathDirection.Clockwise` |
| `end` point | `new SKPoint(end.X, end.Y)` |

**Sweep semantics verified**, not assumed: `MoveTo(0,-16)` then
`ArcTo((16,16), 0, Small, Clockwise, (16,0))` yields `Bounds = {L=0, T=-16, W=16, H=16}` — the
quarter arc through the **+X/-Y** quadrant, i.e. clockwise from 12 o'clock in Skia's y-down screen
space, exactly as the pre-v2 code draws it. `SKPath.AddArc(SKRect, startAngle, sweepAngle)` also
exists but is **not** used: it implies a `MoveTo` and would change the sub-path structure.

### Surfaces (`CpuSurfaceProvider` already; T13's `WriteableBitmap` fallback)

```csharp
SKSurface.Create(SKImageInfo);                              // offscreen, Skia owns the memory
SKSurface.Create(SKImageInfo, IntPtr pixels, int rowBytes); // over a locked framebuffer — T13
```

Both present. T13 uses the second over `ILockedFramebuffer.Address` / `.RowBytes` with
`SKColorType.Bgra8888` + `SKAlphaType.Premul` (decision D-7 — no `ReadPixels` copy).

### Typefaces

```csharp
SKTypeface.FromStream(Stream, int index = 0);
SKTypeface.FromData(SKData, int index = 0);
```

Both present. `SKTypeface.Default` resolves to the **host's** UI font — `Segoe UI` on the probe
machine, something else entirely on the ubuntu CI runner — which is precisely why integrator
correction 6 forbids it.

---

## 3. The embedded typeface (integrator correction 6)

**Chosen: Inter Regular, embedded in `DemoViewer.NET.Playback2D.Core` as an `EmbeddedResource`.**

- *Why an embedded face at all:* `SKTypeface.Default` (and `FromFamilyName("Consolas,…")`, the
  pre-v2 choice) resolve differently on Windows and on the ubuntu golden lane, so any text-bearing
  golden would be machine-specific. Correction 6.
- *Why Inter:* it is already a dependency of this repo — `Avalonia.Fonts.Inter 11.3.12` is
  referenced by the app heads and ships the same faces — so no new supply chain, and it is
  SIL OFL 1.1, which permits redistribution with the notice. The binary was extracted from that
  package's `Avalonia.Fonts.Inter.dll` (embedded sfnt at offset 953535, `Inter-Regular`,
  309 828 bytes) rather than fetched from the network, so the vendored bytes are exactly the ones
  the app already renders with.
- *Why not the pre-v2 `Consolas,Menlo,monospace`:* Consolas is proprietary and cannot be vendored;
  a monospace fallback resolves to a different metric set on every host, which is the problem being
  fixed. Text parity is a **review** gate (plan D-17), not an assert, so the face change is
  documented in `B1-text-metrics-review.md` rather than absorbed by loosening a tolerance.
- Notice added to `THIRD-PARTY-NOTICES.md` as section **d**.

`TextBlobCache` owns exactly one `SKTypeface` (lazily loaded from the manifest resource), one
`SKFont` per size, and a bounded LRU of
`(text, size) → (SKTextBlob, SKRect tightInk, float advance, float ascent, float descent)`, cap 512.
All five are measured on the **miss** path; a hit copies the struct out and allocates nothing.
B1's sizes are the pre-v2 ones — **10 px** marker labels, **11 px** floor labels — plus B4's HUD text
at **14 px** and its countdown at **18.9 px**.

**One cache per scene, not one per layer.** Four layers draw text (markers, floor label, HUD clock,
kill feed). `Scene2DHost`, the test stage and `SceneLayerCatalog.CreateSceneStack` all pass one shared
instance — the headless factory did not until it was corrected, and four private caches meant four
copies of the embedded face. A shared cache is owned by the **compositor**
(`SceneCompositor.AddOwned`, disposed after every layer), never by one of the layers using it:
`SceneCompositor.Remove` disposes the layer it drops.

---

## 4. Things deliberately NOT used

| API | Why not |
|---|---|
| `SKCanvas.DrawText(string, …)` | allocates a blob per call; `TextBlobCache` exists to stop that |
| `SKPath.AddArc` | implies a `MoveTo`; changes sub-path structure vs the pre-v2 `BeginFigure`/`ArcTo` |
| `SKSamplingOptions` | does not exist at this pin |
| `SKTypeface.Default` / `FromFamilyName` | host-dependent; correction 6 |
| `SKCanvas.SetMatrix` for world→screen on dynamic layers | would scale stroke widths and marker radii; decision D-8 |
| `SKSurface.Snapshot` on the on-screen path | the fallback draws straight into the locked framebuffer; D-7 |
