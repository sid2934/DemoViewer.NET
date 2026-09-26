---
name: skia3-linux-raster-cost
description: SkiaSharp 3 CPU rasterisation is ~1.8x slower on Linux than 2.88.9; Windows and the GL path are unaffected
metadata:
  type: project
---

Adopting SkiaSharp 3.119.4 (PR #19, 2026-09-20) made **CPU rasterisation on Linux about 1.8x
slower**. Windows is flat and the Linux GL path got faster, so this is specific to the CPU raster
backend on that platform.

`playback2d-budget` on CI Linux, render p99 against a 16 ms gate:

- main (2.88.9): 4.179 ms
- 3.119.4 with the sampling bug: 12.491 to 16.939, deciding on runner variance
- 3.119.4 with sampling corrected ([[skiasharp3-sampling-translation]]): **7.711**

**Ruled out, each by measurement:** Avalonia (the cost is in the raster backend), the ANGLE native
(not in the `--cpu` path, and pinning it back reproduces), the native package variant
(`NoDependencies` measures the same), and a GCC-vs-Clang build difference (both shipped
`libSkiaSharp.so` are clang built, 12.0.1 and 13.0.1). It is Skia's own software rasteriser
between the two engine versions. No matching public report was found.

**Which paths care:** on-screen playback leases Avalonia's canvas, so it is GPU and got ~26%
FASTER on Linux GL. **Video export is CPU by construction** and always will be until C2 Stage 1:
`SceneExportSession` refuses any provider whose backend is not `CpuRaster`, because its render loop
crosses threads while the GPU provider is thread-affine, which is why
`Playback2DSettings.RenderBackend` is deliberately not exposed. `linux-x64` is a first-party
publish target, so Linux export is the user-facing path that pays this.

**How to apply:** do not quote the ~3x figure from the PR's earlier revisions, it predates the
sampling fix. If this needs re-measuring, use [[wsl-linux-ci-repro]] rather than CI round-trips,
and `dv2d bench --perf --cpu` for the per-layer breakdown.
