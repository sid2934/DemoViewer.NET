---
name: skiasharp3-sampling-translation
description: SKFilterQuality.High is NOT one resampler; translating it to cubic Mitchell blurs 1:1 blits and costs 3x on downscales
metadata:
  type: project
---

SkiaSharp 3 moved sampling off `SKPaint.FilterQuality` onto the `DrawImage` call. The obvious
translation, `new SKSamplingOptions(SKCubicResampler.Mitchell)`, is **wrong**, and an independent
review of PR #19 caught it after I had shipped it into the branch.

**Measured against 2.88.9, same source and destination rects:**

| draw | 2.88.9 `High` | 3.x Mitchell | 3.x `Linear + MipmapNearest` |
|---|---|---|---|
| 1:1 blit, integer origin | 0 of 16384 bytes differ | 3844 differ, max delta 20 | 0 differ |
| downscale 0.50 | checksum 615424 | 615996 | 615424, byte-identical |

So `High` was cubic Mitchell **only when magnifying**. Minifying it is linear plus a nearest
mipmap, and at 1:1 it short-circuits to a copy, which Mitchell cannot because B = 1/3 misses
Skia's identity test.

**Why it mattered more than it looks:** `SceneExportSession` sets `RadarLayer.CacheScaledImage` for
every video export, so that path resamples into a whole-pixel intermediate AND blits it out. Both
steps were 1:1 when the pane matched the radar, so exported video was filtered twice and visibly
softer. Nothing caught it: the golden corpus renders with that flag OFF, and the only test that
turns it on asserted cache bookkeeping, never pixels.

**How to apply:** pick sampling by direction (`RadarLayer.SamplingFor`), and pass `default` where
the code has already established a true integer 1:1. Do not use rounded-size equality to detect
1:1 on a FRACTIONAL destination rect: a 1234.7-wide rect against a 1235 px image is still a
resample and needs a filter.

A regression test exists now: `TheCachedRadarPath_CopiesAtOneToOne_RatherThanResampling` in
`Playback2D.Tests/RenderCorrectnessTests.cs`. A checkerboard is the probe, since filtering invents
intermediate levels (6 with Mitchell: 0,14,27,228,241,255) where a copy keeps 2.

Related: [[skia3-linux-raster-cost]]
