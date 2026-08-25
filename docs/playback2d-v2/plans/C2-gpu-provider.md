# Phase C2 — `GpuSurfaceProvider` (windowless GPU render surfaces)

**Design:** `docs/playback2d-v2/design.md` §5.8 (render surface providers), §6 (perf budget), §9 (phase
table), §10 risk 7, §11 (testing), §12 open question 2.
**Branch:** `feature/playback2d-v2` · **Effort:** 1.5 wk (7.5 working days) = **3-day spike + 4.5 days
implementation/validation** · **Runs in parallel with B2–B4.**

> **Read this first, implementing agent.** Roughly two days of the nominal spike have already been
> spent by this plan's recon and are recorded in §3 as *verified facts with evidence*. Do not
> re-litigate them: SkiaSharp is pinned at 2.88.9, its official native libraries contain **no Vulkan
> backend**, and **ANGLE binaries already ship inside this app's dependency graph**. The spike that
> remains is narrower and cheaper than §5.8 assumed.
>
> **Everything in §5 Stage 0 is executable on a machine with no GPU** — the interface, the EGL
> interop, the probe, the overrides, the CPU-fallback tests, the perceptual-diff harness, the CI
> lanes, the packaging. Only Stage 1 (the spike) and Stage 2 (validation) need real hardware, and
> every task below is tagged **[no-GPU]** or **[GPU]**.

> ## Integrator corrections (BINDING — supersede anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry. All four
> "Integrator conflicts" are resolved here.
>
> 1. **Conflict 1 resolved — §2.14's assumed layout is correct.** Core/Pipeline are
>    `src/Playback2D/DemoViewer.NET.Playback2D.{Core,Pipeline}`, tests are the single project
>    `src/Playback2D/DemoViewer.NET.Playback2D.Tests`, slnx folder `/src/Playback2D/`. Nothing to
>    shift.
> 2. **Conflict 2 resolved — B0 pins `SkiaSharp 2.88.9`** with the coherence comment; C2 must not
>    re-declare it. C2 adds exactly one `PackageVersion`: `Avalonia.Angle.Windows.Natives`.
> 3. **Namespace confirmed: `…Core.Rendering`** holds `RenderBackend`, `IRenderSurfaceProvider`,
>    `CpuSurfaceProvider`, `SceneRenderer` (B0 declares them there) and C2's
>    `RenderBackendPreference`, `RenderSurfaceProbe`, `RenderBackendPreferenceParser`,
>    `RenderSurfaceProviderFactory`, `GpuSurfaceProvider`. B5's `Core/Surfaces/…` path is corrected
>    to `Core/Rendering/…`.
> 4. **§6.3's `ImageComparison` / `ImageDiffOptions` / `ImageDiffResult` are WITHDRAWN.** There is
>    one image comparator in the repo: B0's `GoldenImageComparer` + `GoldenTolerance` +
>    `GoldenComparison` in `Pipeline/Goldens/` (signatures in `plans/C1-cli.md`). C2's contribution
>    is the **SSIM implementation inside that comparer** plus the four extra tolerance fields
>    (`OutlierChannelDelta`, `MaxAlphaDelta`, `MinMeanSsim` → the existing `MinSsim`,
>    `MinWindowSsim`), reachable as `GoldenTolerance.CrossBackend` (≡ `DefaultPerceptual`) and
>    `GoldenTolerance.ByteExact` (≡ the withdrawn `ImageDiffOptions.Exact`). `DemoViewer.NET
>    .TestSupport` gains no SkiaSharp reference and no imaging namespace; §8.2's TestSupport change
>    is dropped. Diff images come from `GoldenImageComparer.CreateDiffPng`.
> 5. **The settings key is `AppSettings.Playback2D.RenderBackend`** (§6.5) and it is the **only**
>    backend key — B4's `ExportBackendOverride` is withdrawn, so the export dialog's advanced option
>    writes this one. `Playback2DSettings` is created by whichever phase lands first (B1/B2/B4);
>    C2 adds one property to it. B5 D3 flattens the whole section into `WriteInMemory`, so the
>    "must be mirrored" note is already satisfied by B5's reflection-driven round-trip test.
> 6. **Conflict 3 resolved:** the §11 architecture test asserts Core's **managed** reference set is
>    `{ SkiaSharp } ∪ BCL`. A native-asset-only package referenced by a *head* or a *test* project
>    (ANGLE, `SkiaSharp.NativeAssets.*`) contributes no managed assembly and is therefore already
>    outside its scope — B0's implementation (walking `Assembly.GetReferencedAssemblies()`) has this
>    property by construction. No test change needed; add a comment saying so.
> 7. **Conflict 4 stands and is tracked**: the ≥2× realtime exit criterion cannot be *closed* until
>    B4 lands `SceneExportSession`. It is listed in `00-overview.md` §6 as a scheduling dependency,
>    not a blocker on C2's other work.
> 8. **Goldens live at `tests/fixtures/playback2d/goldens/{cpu,gpu}/<name>@<w>x<h>.png`** (C1's
>    corpus layout) — §9.1's `tests/goldens/playback2d/cpu/` does not exist. Fixture scenes are
>    `tests/fixtures/playback2d/scenes/<name>.scene.json`; if C2 runs ahead of the corpus, use the
>    canonical names (`duel-mirage-b`, `nuke-multilevel`, `full-scene-budget`) so its provisional
>    fixtures are the real ones.
> 9. **`dv2d probe` is a C1-owned surface addition** — record it in C1's command table and
>    `docs/playback2d-v2/dv2d.md` when it lands, so the `--help` parity test stays green.

---

## 1. Scope & exit criterion

The design's phase table row, quoted verbatim (§9):

| Track | Phase | Content | Exit criterion | Effort |
|---|---|---|---|---|
| **C (headless/CLI/GPU — parallel with B2–B4)** | C2 | `GpuSurfaceProvider`: time-boxed backend spike (ANGLE/EGL vs native GL vs Vulkan), probe + override flags, perceptual-diff validation vs CPU goldens, GPU lane in CI where runners allow | GPU export ≥ 2× realtime at 1080p on a baseline dGPU/iGPU; CPU parity within perceptual tolerance | 1.5 wk |

Supporting requirements this phase must satisfy, from §5.8:

- Probe order first-success-wins, **chosen once per process, logged**.
- Overrides everywhere: `dv2d --gpu | --cpu`, an export-dialog advanced option, and an env var for CI.
- **Backend equivalence policy:** CPU goldens are authoritative; GPU is validated by perceptual diff
  (per-channel tolerance + SSIM-style threshold), never byte equality.
- A CI job with GPU runners runs the perceptual suite; the byte-exact suite runs everywhere on CPU.
- CPU provider remains the contract baseline; **GPU is opportunistic and must never be required**
  (§10 risk 7).

**In scope:** Windows and Linux windowless GPU contexts, the probe/override/logging machinery, the
perceptual-diff harness, CI lanes, native-binary packaging + licensing.
**Out of scope (explicitly):** macOS Metal (§5.8 point 3 defers it), the browser/WASM GPU path (§8:
browser surfaces belong to Avalonia's compositor — CPU provider is the only offscreen path there),
the on-screen Avalonia Skia lease path (that is B1 and is already GPU-composited).

---

## 2. Decisions made

Ambiguities the design left open, resolved here. Each is binding for the phase unless the spike
produces evidence against it, in which case the decision record (§4.5) supersedes it.

1. **SkiaSharp is pinned to 2.88.9 and Vulkan is eliminated before the spike starts.** Evidence in
   §3.1/§3.2. `GRContext.CreateVulkan` exists in managed metadata but the official native libraries
   ship no Vulkan backend, and moving the whole app to SkiaSharp 3.x is a separate, repo-wide gate
   (see the AssetBaker csproj comment). The design's `RenderBackend.Vulkan` enum member stays
   declared (it is a persisted/logged identifier) but is unreachable in v1 and documented as such.
2. **ANGLE is the presumptive Windows winner and its binary is already shipped.**
   `Avalonia.Angle.Windows.Natives` is a transitive dependency of `Avalonia.Win32` ← `Avalonia.Desktop`
   and lays down `av_libglesv2.dll` (merged EGL + GLES2) in the Desktop publish today. The spike's
   job on Windows is to *confirm it works headlessly and fast enough*, not to choose a stack.
3. **No new managed dependency for EGL.** Core stays package-clean (the §11 architecture test says
   "Core references only SkiaSharp"). The ~10 EGL entry points are bound by hand with
   `NativeLibrary.TryLoad` + `NativeLibrary.TryGetExport` + `Marshal.GetDelegateForFunctionPointer` —
   **not** `DllImport`, so a missing DLL is a clean probe failure instead of a first-call throw, and
   not Silk.NET/OpenTK, which would add a dependency, break the architecture test, and fight the
   non-standard DLL name anyway.
4. **Core cannot take `Microsoft.Extensions.Logging`** (same architecture test). The probe logs
   through an `Action<string>? log` callback; the App adapts it to `ILogger`, the CLI to stderr.
5. **Override precedence (highest wins):** explicit API argument → CLI flag (`--cpu`/`--gpu`/
   `--backend <v>`) → env var `DV2D_RENDER_BACKEND` → persisted setting
   (`AppSettings.Playback2D.RenderBackend`) → auto-probe. Rationale: an operator standing at a
   terminal always beats a stored preference; CI sets the env var and expects it to beat whatever a
   settings file says.
6. **Env var grammar:** `DV2D_RENDER_BACKEND` ∈ `auto | cpu | gpu | angle | gl` (case-insensitive;
   `gpu` = "fail the run if GPU is unavailable" is *not* the meaning — see the enum in §6: `gpu` maps
   to `PreferGpu`, and `ForceGpu` is only reachable from the API/`--backend force-gpu` so CI can
   assert that a lane really used the GPU). An unrecognized value logs a warning and falls back to
   `Auto` — never throws, never fails a render.
7. **`GpuSurfaceProvider` is thread-affine.** An EGL context is current on exactly one thread. The
   provider captures its creating thread's id and throws `InvalidOperationException` from
   `CreateSurface`/`Flush`/`Dispose` on any other. `SceneExportSession` already runs on one
   background thread (§5.7), so this costs nothing and turns a class of undebuggable driver crashes
   into an immediate, attributable exception.
8. **Surface ownership:** the caller owns and disposes the `SKSurface` returned by `CreateSurface`.
   Export creates exactly one surface for a whole run (size is fixed by `ExportRequest`), so there is
   no per-frame surface churn to optimize.
9. **1×1 pbuffer, never surfaceless, on Windows.** `av_libglesv2.dll` exports
   `eglCreatePbufferSurface` and shows no `EGL_ANGLE_surfaceless_context` string; Skia renders into
   its own FBO-backed surfaces regardless, so the pbuffer is a formality. On Linux, surfaceless
   (`EGL_PLATFORM_SURFACELESS_MESA`) is tried first *because* it is what makes containers work, with a
   1×1 pbuffer as the fallback.
10. **Perceptual tolerance, concrete numbers** (§7.3): max per-channel |Δ| ≤ **8/255** for ≥ **99.5 %**
    of pixels, **no** pixel above **32/255**, alpha within **2/255** everywhere; global mean SSIM ≥
    **0.995** and minimum windowed SSIM ≥ **0.95** (luma, 11×11 Gaussian σ=1.5, standard constants).
    Justification and the calibration procedure that can move them are in §7.3.
11. **SSIM is implemented in-repo** (≈120 loc in `DemoViewer.NET.TestSupport`). No ImageSharp — it is
    not in `Directory.Packages.props`, and adding an image library to compare two `SKBitmap`s that
    SkiaSharp already gives us pixel access to is unjustified weight.
12. **CI GPU lane, honestly labelled.** GitHub-hosted runners have no GPU, but they *can* exercise the
    real code paths: `windows-latest` runs ANGLE over **D3D11 WARP** (software D3D), and
    `ubuntu-latest` runs EGL over **llvmpipe**. Those lanes gate **correctness and perceptual parity
    only, never performance**. The ≥ 2× realtime number is gated on an optional, label-triggered
    self-hosted lane. A CI lane that finds no GPU **skips** (TUnit `SkipTestException`) and stays
    green — it never fails.
13. **`dv2d probe` is added as a CLI subcommand** (a small addition to C1's surface). It prints the
    probe decision + reason as JSON. It is the diagnostic the whole phase hangs on, the cheapest CI
    assertion available, and the first thing to ask a user for in a bug report.
14. **Assumed source layout:** `src/Playback2D/DemoViewer.NET.Playback2D.Core/` and
    `…Pipeline/`, `…Tests/`, with a `/src/Playback2D/` slnx folder. The design names the projects but
    not their directory; B0 creates them. **If B0 chose a different directory, only the paths in this
    plan shift — the namespaces and type names in §6 are what bind.** Flagged for the integrator.

---

## 3. Verified ground truth (do not re-derive)

Every claim here was checked against the actual artifacts in this repo / the NuGet cache on
2026-08-24. Commands are given so a future agent can re-verify in seconds.

### 3.1 The app's SkiaSharp is 2.88.9, and that is not negotiable in this phase

`Avalonia.Skia 11.3.12` (the version pinned in `Directory.Packages.props:22`) depends on
`SkiaSharp 2.88.9`:

```
grep -A5 SkiaSharp ~/.nuget/packages/avalonia.skia/11.3.12/avalonia.skia.nuspec
→ <dependency id="SkiaSharp" version="2.88.9" …/>
```

B1's on-screen path uses `ISkiaSharpApiLeaseFeature` from `Avalonia.Skia`, which hands out
`SKCanvas`/`GRContext` instances **from that exact assembly**. Core must therefore reference
SkiaSharp 2.88.9 or the lease types will not unify. (`tools/DemoViewer.NET.AssetBaker` already
documents this as a repo-wide constraint: it opts *out* of central package management specifically so
its SkiaSharp 3.x cannot collide with "the app's Avalonia-pinned SkiaSharp 2.88.x".)

### 3.2 SkiaSharp 2.88.9's native libraries have no Vulkan backend → candidate W3 is dead

```
f=~/.nuget/packages/skiasharp.nativeassets.win32/2.88.9/runtimes/win-x64/native/libSkiaSharp.dll
grep -ac vulkan-1.dll  $f   → 0
grep -ac VULKAN        $f   → 0
grep -ac opengl32.dll  $f   → 1
g=~/.nuget/packages/skiasharp.nativeassets.linux/2.88.9/runtimes/linux-x64/native/libSkiaSharp.so
grep -ac libvulkan $g → 0 ;  grep -ac libGL.so $g → 1
```

The managed `GRContext.CreateVulkan(GRVkBackendContext)` exists, but there is no Vulkan
implementation behind it on desktop RIDs. Spending spike time on it would be spending it on a
guaranteed `null`.

### 3.3 Exact SkiaSharp 2.88.9 GPU API surface (reflected, not remembered)

These are the *only* signatures available — 2.88 differs from 3.x, so do not reach for
`FlushAndSubmit` or `SKSurface.Create(GRRecordingContext, …)` shortcuts you may remember:

```
GRContext.CreateGl()                                        GRContext.CreateGl(GRGlInterface)
GRContext.CreateGl(GRContextOptions)                        GRContext.CreateGl(GRGlInterface, GRContextOptions)
GRContext.CreateVulkan(GRVkBackendContext[, GRContextOptions])   // no native backend — see §3.2
GRContext.Flush()            GRContext.Flush(bool submit, bool synchronous)   GRContext.Submit(bool synchronous)
GRContext.AbandonContext(bool)   GRContext.PurgeResources()   GRContext.ResetContext(...)

GRGlInterface.Create()                       GRGlInterface.Create(GRGlGetProcedureAddressDelegate)
GRGlInterface.CreateAngle()                  GRGlInterface.CreateAngle(GRGlGetProcedureAddressDelegate)
GRGlInterface.CreateOpenGl(GRGlGetProcedureAddressDelegate)
GRGlInterface.CreateGles(GRGlGetProcedureAddressDelegate)
GRGlInterface.AssembleGlInterface / AssembleGlesInterface / AssembleAngleInterface(object?, GRGlGetProcDelegate)

delegate IntPtr GRGlGetProcedureAddressDelegate(string name);

SKSurface.Create(GRContext, bool budgeted, SKImageInfo)
SKSurface.Create(GRContext, bool budgeted, SKImageInfo, int sampleCount)
SKSurface.Create(GRContext, bool budgeted, SKImageInfo, int sampleCount, GRSurfaceOrigin)
SKSurface.Create(SKImageInfo)                                   // the CPU raster path
SKSurface.Flush()   SKSurface.Flush(bool submit, bool synchronous)
SKSurface.ReadPixels(SKImageInfo, IntPtr dstPixels, int dstRowBytes, int srcX, int srcY) → bool
SKSurface.Snapshot() → SKImage        SKSurface.PeekPixels() → SKPixmap

GRContextOptions { AvoidStencilBuffers, RuntimeProgramCacheSize, GlyphCacheTextureMaximumBytes,
                   AllowPathMaskCaching, DoManualMipmapping, BufferMapThreshold }
```

Re-verify with: `pwsh -c "[Reflection.Assembly]::LoadFrom(\"$env:USERPROFILE\.nuget\packages\skiasharp\2.88.9\lib\net6.0\SkiaSharp.dll\").GetType('SkiaSharp.GRContext').GetMethods('Public,Static,DeclaredOnly')"`.

### 3.4 ANGLE already ships with this app

`Avalonia.Win32 11.3.12` depends on **`Avalonia.Angle.Windows.Natives 2.1.25547.20250602`**
(`grep -n Angle ~/.nuget/packages/avalonia.win32/11.3.12/avalonia.win32.nuspec`), which contains:

```
runtimes/win-x64/native/av_libglesv2.dll     (5.4 MB)
runtimes/win-x86/native/av_libglesv2.dll
runtimes/win-arm64/native/av_libglesv2.dll
LICENSE   → "Copyright 2018 The ANGLE Project Authors. All rights reserved." (BSD-3-Clause)
nuspec    → repository https://github.com/AvaloniaUI/angle/ commit cb8b4e1307a9d8f5ff56b8c5973bea4158ffead8
```

Confirmed EGL exports inside `av_libglesv2.dll` (it is a **merged** EGL+GLESv2 build — there is no
separate `libEGL.dll` in the package, so bind to `av_libglesv2.dll`):
`eglGetProcAddress`, `eglGetPlatformDisplayEXT`, `eglChooseConfig`, `eglCreatePbufferSurface`,
`eglMakeCurrent`, `eglQuerySurface`, `glGetString`.

Consequence: on `win-x64`, the Desktop head's self-contained publish (docs/distribution §3–4) already
lays this DLL down, and Velopack packages whatever publish emits. **Zero packaging change is needed
for the app.** Only `dv2d` (which does not reference Avalonia) needs an explicit reference.

### 3.5 Repo conventions this phase must obey

- Solution is `DemoViewer.NET.slnx` (new XML format); projects are added as `<Project Path="…"/>` under
  a `<Folder>`.
- `Directory.Build.props`: `net10.0`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`,
  `AnalysisMode=Recommended`, `GenerateDocumentationFile=true`, artifacts output under `artifacts/`.
  **Style violations fail the build.**
- `.editorconfig`: file-scoped namespaces, Allman braces, **always braces**, **explicit types (no
  `var`)**, 4-space indent, LF, 120 col, `#region`-wrapped usings above the namespace.
- Central package management; `PackageReference` entries carry **no `Version=`**.
- Test framework is **TUnit** (`[Test]`, `await Assert.That(x).IsEqualTo(y)`, `[Category(...)]`,
  `SkipTestException` for skips, `[Before(HookType.Assembly)]`). Test csprojs are `OutputType=Exe`
  with `NoWarn=$(NoWarn);CA1707`.
- Missing-fixture pattern is `SkipTestException`, not a silent pass (`DemoTestHelper.RequireDemo`).
- CI (`.github/workflows/ci.yml`) today builds only the Desktop head on `ubuntu-latest` and **runs no
  tests** (the App UI suite is OOM-prone and batched via `scripts/test-app-suite.sh`).

---

## 4. The spike (Stage 1) — protocol

The spike is **not throwaway**. Stage 0 (§5) builds the real provider, the real EGL binding, and the
real probe first; the spike then *runs* that code on hardware for each candidate and records which
branch survives. A candidate "implementation" is a ~40-line addition to `Egl.cs`/`GpuSurfaceProvider`,
not a scratch project.

### 4.1 Candidate matrix

| Id | Platform | Stack | Context creation | Skia interface | Pre-spike verdict |
|---|---|---|---|---|---|
| **W1** | win-x64 | **ANGLE over D3D11**, `av_libglesv2.dll` | `eglGetPlatformDisplayEXT(EGL_PLATFORM_ANGLE_ANGLE, EGL_DEFAULT_DISPLAY, {EGL_PLATFORM_ANGLE_TYPE_ANGLE: D3D11})` → `eglInitialize` → `eglChooseConfig(PBUFFER\|ES2, RGBA8888)` → `eglCreatePbufferSurface(1×1)` → `eglCreateContext(ES3→ES2)` → `eglMakeCurrent` | `GRGlInterface.CreateAngle(eglGetProcAddress)` → `GRContext.CreateGl(iface)` | **Favored** — binary already ships (§3.4), Avalonia's own Windows default, works over RDP/service sessions, and falls back to **WARP** when no GPU (which is what makes a hosted-runner CI lane possible) |
| **W2** | win-x64 | **Hidden-context WGL** | register a class → 1×1 `WS_POPUP` invisible HWND (a message-only `HWND_MESSAGE` window cannot own a GL context) → `ChoosePixelFormat`/`SetPixelFormat` → `wglCreateContext` → `wglMakeCurrent` → optionally `wglCreateContextAttribsARB` for a core profile | `GRGlInterface.CreateOpenGl(name => wglGetProcAddress(name) ?? GetProcAddress(opengl32, name))` | **Fallback only** — needs a window handle and a message pump-less HWND, is at the mercy of the vendor ICD, and degrades to the Microsoft 1.1 software rasterizer in session-0/RDP contexts (silent, catastrophic perf) |
| ~~W3~~ | win-x64 | ~~SkiaSharp Vulkan~~ | — | — | **Eliminated pre-spike** (§3.2). Recorded, not attempted. |
| **L1** | linux-x64 | **EGL surfaceless** (`libEGL.so.1`) | `eglGetPlatformDisplayEXT(EGL_PLATFORM_SURFACELESS_MESA=0x31DD, EGL_DEFAULT_DISPLAY, null)` → `eglInitialize` → `eglBindAPI(EGL_OPENGL_ES_API)` → `eglCreateContext(config=EGL_NO_CONFIG_KHR or a pbuffer config)` → `eglMakeCurrent(EGL_NO_SURFACE, EGL_NO_SURFACE, ctx)` | `GRGlInterface.CreateGles(eglGetProcAddress)` | **Favored for containers** — no X, no DRM node needed under llvmpipe; the future "cloud highlight service on a Linux box" (§5.8) is this path |
| **L2** | linux-x64 | **EGL over GBM** | open `/dev/dri/renderD128` → `gbm_create_device` → `eglGetPlatformDisplayEXT(EGL_PLATFORM_GBM_KHR=0x31D7, gbmDevice, null)` | `CreateGles` | Fallback if L1 is unsupported by the installed driver; adds a `libgbm` dependency |
| ~~L3~~ | linux-x64 | ~~GLX hidden pbuffer~~ | needs an X display | — | **Last resort, not attempted** — an X dependency destroys the container story that motivates the Linux path at all |
| **M1** | macOS | CGL / Metal | — | — | **Deferred by design** (§5.8 point 3). Probe returns `CpuRaster` with reason `"macos-deferred"`. |

### 4.2 Evaluation criteria

Three are **gates** (fail ⇒ candidate rejected); three are **scores** (tie-breakers).

| # | Criterion | Type | Measurement | Threshold |
|---|---|---|---|---|
| G1 | **Context creation reliability** | Gate | 20 consecutive create → 1080p surface → render fixture → `ReadPixels` → dispose cycles in one process; plus create-after-dispose; plus one cycle on each available driver family | 20/20, zero leaks (`GRContext` resource count stable), no crash on dispose |
| G2 | **`GRContext` surface perf on the bench fixture** | Gate | `dv2d bench --demo <fixture> --frames 2000 --gpu` (§6 of design) and a 1080p export throughput run | p99 frame time ≤ CPU p99; **end-to-end export ≥ 2× realtime at 1080p = ≥ 128 fps incl. readback** (the design's exit number) |
| G3 | **Fidelity vs CPU goldens** | Gate | `BackendParityTests` over the whole fixture corpus, thresholds per §7.3 | all fixtures pass |
| S1 | **Binary / distribution weight** | Score | Δ MB in `dotnet publish -r win-x64 --self-contained`; new packages; new license obligations | lower is better; W1 = 0 MB (already shipped) |
| S2 | **CI-container behavior** | Score | Does `dv2d probe --json` report the backend on `windows-latest` and `ubuntu-latest` hosted runners? | a candidate that runs in hosted CI is worth a lot: it is the difference between a permanently-skipped suite and a real lane |
| S3 | **Interop code weight** | Score | loc added to `Egl.cs`/`Wgl.cs`, count of P/Invoke entry points | lower is better |

### 4.3 Time-box: 3 working days, hard stop

| Day | Work | Deliverable |
|---|---|---|
| **1** | W1 (ANGLE/D3D11) on the dev box: context, surface, parity render, 20-cycle reliability, bench numbers | W1 row of the decision record filled, with real numbers |
| **2** | W1 on a second Windows machine/driver family if available (iGPU vs dGPU); **then** W2 (WGL) only if W1 failed a gate, else 2 h timeboxed to record W2's numbers as the fallback's known state. Afternoon: L1 (EGL surfaceless) in a container (`docker run --rm -it mcr.microsoft.com/dotnet/sdk:10.0` + `apt-get install -y libegl1 libgles2`) | W2 + L1 rows filled |
| **3** | Perf runs at the final settings (sample counts, `GRContextOptions`, readback strategy), hosted-runner probes (S2), write the decision record, open the follow-up issues | `docs/playback2d-v2/c2-backend-decision.md` merged |

**Kill rules — enforce them:**

- A candidate that cannot create a context within **2 hours** of work is recorded as *failed* and
  abandoned. No "one more driver flag."
- No candidate gets more than **1 day**.
- **End of day 3 is the decision**, extension requires an explicit owner decision recorded in the doc.
- **If every GPU candidate fails a gate:** ship `CpuSurfaceProvider` as the only registered provider,
  keep `GpuSurfaceProvider` compiled but probe-disabled (`RenderSurfaceProbe.Reason =
  "all-backends-failed: <details>"`), and record the outcome. C2's exit criterion then formally
  degrades to *"CPU-only, documented, GPU deferred"* — which §10 risk 7 already sanctions ("CPU
  provider is the contract baseline — GPU is opportunistic"). This is a legitimate, pre-approved
  outcome, not a failure of the phase. Stage 0 and the perceptual harness ship either way and are
  what the follow-up would build on.

### 4.4 Spike environment requirements

- **Windows machine with a real GPU** (the owner's box; iGPU is acceptable — the exit criterion says
  "baseline dGPU/iGPU"). Needed for W1/W2 and for the ≥ 2× realtime number.
- **A Linux container** (Docker Desktop is enough for L1-under-llvmpipe; a GPU-enabled container is
  optional and only affects S2's Linux score).
- Everything else is machine-independent.

### 4.5 Decision record template

Create `docs/playback2d-v2/c2-backend-decision.md` from this skeleton. It closes design §12 open
question 2, so link it from that line.

```markdown
# C2 backend decision — windowless GPU surfaces

**Date:** YYYY-MM-DD · **Decider:** <name> · **Time-box:** 3 days (day 1 YYYY-MM-DD → day 3 YYYY-MM-DD)
**Closes:** design.md §12 open question 2 · **Supersedes:** nothing

## Decision
<One sentence: which backend `GpuSurfaceProvider` uses per platform, and what the probe order is.>

## Hardware / software tested
| Machine | OS | GPU | Driver | SkiaSharp | ANGLE |
|---|---|---|---|---|---|

## Results
| Candidate | G1 reliability | G2 p50/p95/p99 + export fps | G3 parity | S1 MB | S2 hosted CI | S3 loc | Verdict |
|---|---|---|---|---|---|---|---|
| W1 ANGLE/D3D11 | | | | 0 | | | |
| W2 WGL | | | | | | | |
| W3 Vulkan | not attempted | — | — | — | — | — | eliminated pre-spike (no native backend in SkiaSharp 2.88.9 — see plan §3.2) |
| L1 EGL surfaceless | | | | | | | |
| L2 EGL/GBM | | | | | | | |
| M1 macOS | deferred by design §5.8 | | | | | | |

## CPU baseline for comparison
p50/p95/p99 and export fps on the same fixture, same machine.

## What surprised us
<Anything a future reader would otherwise re-discover the hard way.>

## Rejected and why (do not revisit without new information)
- **SkiaSharp Vulkan:** …
- **Silk.NET / OpenTK EGL binding:** …
- **macOS Metal now:** …

## Follow-ups opened
- [ ] …
```

---

## 5. Ordered work breakdown

Every task is ≤ ~half a day. **[no-GPU]** tasks are executable by an agent with no graphics hardware;
**[GPU]** tasks require the spike machine.

### Stage 0 — skeleton, probe, harness, CI, packaging (≈3.5 days, all [no-GPU])

| # | Task | Files | Ordering |
|---|---|---|---|
| **C2.0** | **Verify the B0 seam.** Confirm `IRenderSurfaceProvider`, `RenderBackend`, `CpuSurfaceProvider` exist with the §6 signatures. If B0 has not landed them yet, create them exactly as in §6 and tell the integrator — do not invent a variant shape. Also confirm the fixture corpus location (`tests/fixtures/playback2d/`) and the CPU golden location (`tests/goldens/playback2d/cpu/`). | read `src/Playback2D/DemoViewer.NET.Playback2D.Core/Rendering/*.cs` | first |
| **C2.1** | **Probe + preference types.** Add `RenderBackendPreference`, `RenderSurfaceProbe`, `RenderBackendPreferenceParser`, `RenderSurfaceProviderFactory` (CPU-only registration for now, probe-once-per-process, never throws, single log line). Use an explicit lock + result record, **not** `Lazy<T>` — the repo already documents the exception-caching trap (`HeadlessSession.cs` comment). Include `internal static void ResetForTests()`. Browser short-circuits to `CpuRaster` with reason `"browser"`. | create `Core/Rendering/RenderBackendPreference.cs`, `Core/Rendering/RenderSurfaceProbe.cs`, `Core/Rendering/RenderSurfaceProviderFactory.cs`; edit `Core/…/InternalsVisibleTo` in the csproj | after C2.0 |
| **C2.2** | **EGL interop.** `Egl` internal static class: `NativeLibrary.TryLoad` over an ordered probe list (`DV2D_ANGLE_LIBRARY` env override → `av_libglesv2.dll` → `libEGL.dll` on Windows; `libEGL.so.1` → `libEGL.so` on Linux), `TryGetExport` + `GetDelegateForFunctionPointer` for the ~10 entry points, all constants, and `TryCreateContext(EglBackendKind kind, out EglContext?, out string reason)`. Returns failure as data; **never throws**. | create `Core/Rendering/Interop/Egl.cs`, `Core/Rendering/Interop/EglContext.cs` | after C2.1 |
| **C2.3** | **`GpuSurfaceProvider`.** `TryCreate` → EGL context → `GRGlInterface.CreateAngle/CreateGles(eglGetProcAddress)` → `GRContext.CreateGl(iface, options)`; `CreateSurface` → `SKSurface.Create(grContext, budgeted: false, info, sampleCount: 0, GRSurfaceOrigin.TopLeft)`; `Flush` → `surface.Flush(submit: true, synchronous: false)` then `grContext.Flush(true, false)` + `grContext.Submit(synchronous: true)` (exact call order to be confirmed on hardware in C2.10 — readback correctness depends on it); thread-affinity guard; `Dispose` tears down `GRContext` then the EGL context/display in that order. Register it in the factory's probe chain. | create `Core/Rendering/GpuSurfaceProvider.cs`; edit `RenderSurfaceProviderFactory.cs` | after C2.2 |
| **C2.4** | **Perceptual-diff harness.** `ImageComparison.Compare/CompareFiles/WriteDiffImage` + `ImageDiffOptions`/`ImageDiffResult` (per-channel stats + SSIM on luma, 11×11 Gaussian σ=1.5, C1=(0.01·255)², C2=(0.03·255)²). Add `PackageReference Include="SkiaSharp"` to TestSupport. | create `src/Testing/DemoViewer.NET.TestSupport/Imaging/ImageComparison.cs`; edit `DemoViewer.NET.TestSupport.csproj` | independent of C2.1–C2.3; can run in parallel |
| **C2.5** | **No-GPU tests.** `RenderBackendResolutionTests`, `RenderSurfaceProbeTests`, `CpuSurfaceProviderContractTests`, `ImageComparisonTests` (§7.1). All direct-execution TUnit; no Avalonia platform. | create `src/Playback2D/DemoViewer.NET.Playback2D.Tests/Rendering/*.cs` | after C2.1/C2.3/C2.4 |
| **C2.6** | **GPU tests, written now and skipped without hardware.** `GpuSurfaceProviderTests`, `BackendParityTests`, `GpuDeterminismTests`, all `[Category("Gpu")]` and skipping via `SkipTestException` when `RenderSurfaceProviderFactory.Probe().GpuAvailable` is false. On a no-GPU machine these must *skip cleanly*, which is itself a test of the probe. | same directory | after C2.5 |
| **C2.7** | **CLI overrides + `dv2d probe`.** `--cpu`, `--gpu`, `--backend <auto\|cpu\|gpu\|angle\|gl\|force-gpu>` on `render`/`export`/`bench`; new `probe` subcommand with `--json`. Routes into `RenderSurfaceProviderFactory.Create(preference, log)`. | edit `tools/DemoViewer.NET.Playback2D.Cli/Program.cs` (+ its options type) | after C2.1; **depends on C1** |
| **C2.8** | **App override + setting.** Add `RenderBackend` (string, default `"auto"`) to `Playback2DSettings`; **add the key to `SettingsService.WriteInMemory`** or WASM writes vanish (§5.4/§8); add an "Advanced ▸ Render backend" combo (Auto / GPU when available / CPU) to the export dialog, bound to the setting, with the resolved backend + reason shown as read-only text under it. | edit `src/App/DemoViewer.NET/Configuration/AppSettings.cs`, `Configuration/SettingsService.cs`, the B4 export dialog VM + axaml | **depends on B2** (introduces `Playback2DSettings`) and **B4** (the dialog) |
| **C2.9** | **Packaging + licensing.** CPM `PackageVersion` for `Avalonia.Angle.Windows.Natives` (exact version, with the "bump only with Avalonia" comment); explicit `PackageReference` in the CLI and the Playback2D test project; `THIRD-PARTY-NOTICES.md` section **d. ANGLE (BSD-3-Clause)** with the full license text from the package's `LICENSE`; verify `av_libglesv2.dll` lands in `dotnet publish -r win-x64 --self-contained` output for both Desktop and `dv2d`. | edit `Directory.Packages.props`, `tools/DemoViewer.NET.Playback2D.Cli/*.csproj`, `src/Playback2D/…Tests/*.csproj`, `THIRD-PARTY-NOTICES.md` | independent |
| **C2.10** | **CI lanes.** Add the `render-backends` job (§8.4). | edit `.github/workflows/ci.yml` | after C2.5/C2.6 |

### Stage 1 — the spike (3 days, [GPU])

Executed per §4.3. Produces `docs/playback2d-v2/c2-backend-decision.md` and, in code, at most a
handful of additions to `Egl.cs` (the L2/W2 branches) plus tuned `GRContextOptions`.

### Stage 2 — wire the winner and validate (≈1 day, [GPU])

| # | Task | Files |
|---|---|---|
| **C2.11** | **Confirm the flush/readback sequence and tune.** The exact `Flush`/`Submit` ordering, `budgeted`, `sampleCount`, and whether `ReadPixels` into pinned memory beats `Snapshot()`+`ReadPixels` — measured, not assumed. Record the numbers in the decision doc. | `Core/Rendering/GpuSurfaceProvider.cs` |
| **C2.12** | **Run the perceptual suite on real hardware**, calibrate the §7.3 thresholds if a legitimate difference exceeds them (procedure in §7.3 — thresholds move only with a recorded justification, never silently), regenerate nothing on the CPU side (CPU goldens are authoritative). | `…Tests/Rendering/BackendParityTests.cs`, thresholds in `ImageDiffOptions` defaults |
| **C2.13** | **Bench + exit-criterion evidence.** `dv2d bench --frames 2000 --gpu` and `--cpu` on the standard fixture; a 1080p round export both ways; paste p50/p95/p99 + fps into the decision record and into the acceptance checklist (§11). | `bench-reports/` (JSON, `<demoId>_<timestamp>.json` naming) + the decision doc |

### Ordering constraints (summary)

```
C2.0 ─→ C2.1 ─→ C2.2 ─→ C2.3 ─┬─→ C2.5 ─→ C2.6 ─→ C2.10
                              └─→ [Stage 1 spike] ─→ C2.11 ─→ C2.12 ─→ C2.13
C2.4 ────────────────────────────→ C2.5
C2.1 ─→ C2.7   (blocked on C1 landing the CLI)
C2.8 blocked on B2 (Playback2DSettings) + B4 (export dialog)
C2.9 independent — do it early, it is the only task with an external (legal) obligation
```

---

## 6. Public API contracts

**Binding for other phases.** Namespace `DemoViewer.NET.Playback2D.Core.Rendering` unless stated.
Signatures follow the repo's style rules (file-scoped namespaces, explicit types, Allman braces).

### 6.1 Restated from the design (§5.8) — C2 does not change these

```csharp
public enum RenderBackend
{
    CpuRaster,
    OpenGl,
    Angle,
    Vulkan   // declared for identifier stability; unreachable in v1 — SkiaSharp 2.88.9 ships no
             // Vulkan native backend (see docs/playback2d-v2/plans/C2-gpu-provider.md §3.2).
}

public interface IRenderSurfaceProvider : IDisposable
{
    RenderBackend Backend { get; }
    SKSurface CreateSurface(SKSizeI size);   // RGBA8888, premultiplied; CALLER disposes
    void Flush(SKSurface surface);           // GPU: flush + submit; CPU: no-op
}
```

### 6.2 New in C2

```csharp
/// <summary>How a consumer wants the backend chosen. Highest-precedence source wins (see §2.5).</summary>
public enum RenderBackendPreference
{
    Auto,       // probe; GPU if it works, CPU otherwise. The default everywhere.
    ForceCpu,   // never probe GPU. `--cpu`, DV2D_RENDER_BACKEND=cpu.
    PreferGpu,  // probe GPU first; fall back to CPU silently. `--gpu`, DV2D_RENDER_BACKEND=gpu.
    ForceGpu    // probe GPU; THROW if unavailable. Only from the API / `--backend force-gpu`,
                // so a CI lane can assert it really exercised the GPU path.
}

/// <summary>The once-per-process backend decision, as data. Never thrown, always loggable.</summary>
public readonly record struct RenderSurfaceProbe(
    RenderBackend Backend,
    bool GpuAvailable,
    string Reason,            // e.g. "angle-d3d11", "no-egl-library", "browser", "macos-deferred",
                              // "forced-cpu", "all-backends-failed: <detail>"
    string? Renderer,         // GL_RENDERER, when a GL context was made
    string? Vendor,           // GL_VENDOR
    string? Version,          // GL_VERSION
    TimeSpan Duration);

public static class RenderBackendPreferenceParser
{
    /// <summary>Parses auto|cpu|gpu|angle|gl|force-gpu, case-insensitive. False on anything else.</summary>
    public static bool TryParse(string? value, out RenderBackendPreference preference);

    /// <summary>Reads DV2D_RENDER_BACKEND. Unrecognized or unset → Auto (never throws).</summary>
    public static RenderBackendPreference FromEnvironment(string variable = "DV2D_RENDER_BACKEND");

    /// <summary>Applies the §2.5 precedence chain. Any argument may be null/absent.</summary>
    public static RenderBackendPreference Resolve(
        RenderBackendPreference? explicitArgument,
        string? commandLineValue,
        string? environmentValue,
        string? settingValue);
}

public static class RenderSurfaceProviderFactory
{
    /// <summary>
    ///     Probes once per process and caches the result — including failure. Never throws.
    ///     Thread-safe. The first call logs one line; later calls are silent.
    /// </summary>
    public static RenderSurfaceProbe Probe(Action<string>? log = null);

    /// <summary>
    ///     The single entry point every consumer uses. Honors <paramref name="preference"/>;
    ///     throws <see cref="InvalidOperationException"/> only for <see cref="RenderBackendPreference.ForceGpu"/>
    ///     when no GPU backend is available.
    /// </summary>
    public static IRenderSurfaceProvider Create(
        RenderBackendPreference preference = RenderBackendPreference.Auto,
        Action<string>? log = null);

    /// <summary>The always-available baseline. Never probes, never fails.</summary>
    public static IRenderSurfaceProvider CreateCpu();

    internal static void ResetForTests();   // InternalsVisibleTo the Playback2D test project
}

/// <summary>
///     Windowless GPU-backed surfaces via an EGL context (ANGLE/D3D11 on Windows, EGL surfaceless on
///     Linux). THREAD-AFFINE: every member must be called on the thread that created the instance.
/// </summary>
public sealed class GpuSurfaceProvider : IRenderSurfaceProvider
{
    /// <summary>Creates a provider or explains why it could not. Never throws.</summary>
    public static bool TryCreate(out GpuSurfaceProvider? provider, out string reason);

    public RenderBackend Backend { get; }
    public string RendererName { get; }        // GL_RENDERER, for logs and bug reports
    public SKSurface CreateSurface(SKSizeI size);
    public void Flush(SKSurface surface);
    public void Dispose();
}
```

### 6.3 Image comparison — **WITHDRAWN (integrator correction 4)**

There is one comparator in the repo: **B0's `GoldenImageComparer` / `GoldenTolerance` /
`GoldenComparison`** in `DemoViewer.NET.Playback2D.Pipeline.Goldens` (signatures in
`plans/C1-cli.md`). C2 contributes the **SSIM implementation inside it** (≈120 loc, luma, 11×11
Gaussian σ=1.5, standard constants — no ImageSharp) and the four extra tolerance fields, exposed as
`GoldenTolerance.CrossBackend` (the §7.3 numbers) and `GoldenTolerance.ByteExact`. Everywhere this
plan says `ImageComparison.Compare(expected, actual, ImageDiffOptions.CrossBackend)`, read
`GoldenImageComparer.Compare(expectedPng, actualPng, GoldenTolerance.CrossBackend)`; for
`ImageDiffOptions.Exact` read `GoldenTolerance.ByteExact`; for `WriteDiffImage` read
`GoldenImageComparer.CreateDiffPng`. `DemoViewer.NET.TestSupport` is **not** touched.

The withdrawn shape, kept only to show which fields moved into `GoldenTolerance`:

```csharp
public sealed record ImageDiffOptions(   // WITHDRAWN — fields folded into GoldenTolerance
    int MaxChannelDelta = 8,
    int OutlierChannelDelta = 32,
    double OutlierFraction = 0.005,
    int MaxAlphaDelta = 2,
    double MinMeanSsim = 0.995,
    double MinWindowSsim = 0.95)
{
    /// <summary>Byte-exactness, for same-backend determinism assertions.</summary>
    public static readonly ImageDiffOptions Exact = new(0, 0, 0.0, 0, 1.0, 1.0);

    /// <summary>The §7.3 cross-backend tolerance. The default.</summary>
    public static readonly ImageDiffOptions CrossBackend = new();
}

public sealed record ImageDiffResult(
    bool Passed,
    int MaxChannelDelta,
    double OutlierFraction,
    int MaxAlphaDelta,
    double MeanSsim,
    double MinWindowSsim,
    int Width,
    int Height,
    string Summary);   // one-line, assertion-message ready

public static class ImageComparison
{
    public static ImageDiffResult Compare(SKBitmap expected, SKBitmap actual, ImageDiffOptions? options = null);
    public static ImageDiffResult CompareFiles(string expectedPath, string actualPath, ImageDiffOptions? options = null);

    /// <summary>Writes an 8×-amplified absolute-difference PNG for eyeballing a failure.</summary>
    public static void WriteDiffImage(SKBitmap expected, SKBitmap actual, string path);
}
```

### 6.4 CLI surface added to `dv2d` (C1 owns the tool; C2 owns these flags)

```
dv2d probe [--json]
    → human: "backend=Angle renderer='ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0)' reason=angle-d3d11 probe=41ms"
    → --json: {"backend":"Angle","gpuAvailable":true,"reason":"angle-d3d11","renderer":"…","vendor":"…","version":"…","durationMs":41}
    → exit code 0 always (a CPU result is not an error); use `--require-gpu` to exit 1 when GPU is absent.

dv2d render|export|bench … [--cpu | --gpu | --backend <auto|cpu|gpu|angle|gl|force-gpu>]
```

### 6.5 App settings

```csharp
// DemoViewer.NET.Configuration — added to the Playback2DSettings class B2 introduces.
/// <summary>auto | cpu | gpu. Parsed by RenderBackendPreferenceParser; unknown values fall back to auto.</summary>
public string RenderBackend { get; set; } = "auto";
```

Must be mirrored in `SettingsService.WriteInMemory` (WASM has no filesystem — §8 of the design).

---

## 7. Test plan

All tests are **TUnit**. Everything in §7.1/§7.2 is **direct-execution** — no `HeadlessSession`, no
Avalonia platform, no dispatcher (design §11: "strictly faster and less flaky"). Nothing in this
phase needs headless-Avalonia; the only headless-Avalonia surface in Playback2D is `Scene2DHost`
(B1's problem).

### 7.1 No-GPU suites (run everywhere, gate every PR)

**`RenderBackendResolutionTests`** — `src/Playback2D/DemoViewer.NET.Playback2D.Tests/Rendering/RenderBackendResolutionTests.cs`

| Case | Assertion |
|---|---|
| `TryParse_KnownValues_MapToPreference` (`[Arguments]` table over auto/cpu/gpu/angle/gl/force-gpu, mixed case) | exact mapping |
| `TryParse_Garbage_ReturnsFalse` | `false`, `preference == Auto` |
| `FromEnvironment_Unset_IsAuto` | `Auto` |
| `FromEnvironment_Garbage_IsAutoAndDoesNotThrow` | `Auto` |
| `Resolve_ExplicitArgument_BeatsCommandLine_BeatsEnv_BeatsSetting` | full precedence table (4 rows) |
| `Resolve_AllNull_IsAuto` | `Auto` |

**`RenderSurfaceProbeTests`**

| Case | Assertion |
|---|---|
| `Probe_IsIdempotent` | two calls return an equal `RenderSurfaceProbe`; the log callback fires **once** |
| `Probe_NeverThrows_WhenEglLibraryMissing` | with `DV2D_ANGLE_LIBRARY` pointed at a nonexistent path → `Backend == CpuRaster`, `GpuAvailable == false`, `Reason` starts `"no-egl-library"` |
| `Create_ForceCpu_DoesNotProbeGpu` | probe not invoked (spy on the log callback / `ResetForTests` + a counter) |
| `Create_ForceGpu_WithoutGpu_Throws` | `InvalidOperationException` whose message contains the probe reason |
| `Probe_OnBrowser_ShortCircuits` | guarded by `OperatingSystem.IsBrowser()`; asserted by unit-testing the internal decision function with an injected platform flag rather than actually running on WASM |

**`CpuSurfaceProviderContractTests`** — the baseline contract, so the CPU path cannot rot (§5.8).

| Case | Assertion |
|---|---|
| `CreateSurface_HasRequestedSizeAndFormat` | `SKColorType.Rgba8888`, `SKAlphaType.Premul`, exact w/h |
| `Flush_IsNoOp` | no throw, pixels unchanged |
| `ReadPixels_RoundTripsAKnownFill` | draw `SKColors.Red` → read back `FF0000FF` |
| `Dispose_TwiceIsSafe` | no throw |
| `Backend_IsCpuRaster` | — |

**`ImageComparisonTests`** — the harness must be trustworthy before it judges anything.

| Case | Input | Expected |
|---|---|---|
| `Identical_Passes` | same bitmap twice | `Passed`, `MeanSsim == 1.0`, `MaxChannelDelta == 0` |
| `UniformPlusSix_Passes` | +6/255 on every channel | `Passed` (inside the 8/255 band) |
| `UniformPlusTwelve_Fails` | +12/255 everywhere | `!Passed`, failure names the channel-delta rule |
| `SparseHotPixels_Fail` | +40/255 on 2 % of pixels | `!Passed` (outlier rule) |
| `SparseHotPixels_UnderBudget_Pass` | +40/255 on 0.1 % of pixels but ≤ 32 delta | passes/fails per the exact rule — pin the boundary |
| `OnePixelShift_FailsOnSsim` | image translated 1 px | `!Passed`, `MeanSsim` well below 0.995 (this is the case per-channel tolerance alone would miss, and the reason SSIM is in the policy) |
| `AlphaDrift_Fails` | alpha +5 | `!Passed` |
| `SizeMismatch_Fails` | different dimensions | `!Passed`, no exception |
| `WriteDiffImage_ProducesFile` | — | file exists, non-zero, correct dimensions |

### 7.2 GPU suites (`[Category("Gpu")]`, skip cleanly without hardware)

Every one of these opens with the guard — copy it verbatim so the skip reason is uniform:

```csharp
RenderSurfaceProbe probe = RenderSurfaceProviderFactory.Probe();
if (!probe.GpuAvailable)
{
    throw new SkipTestException($"No GPU surface backend on this machine: {probe.Reason}");
}
```

**`GpuSurfaceProviderTests`**

| Case | Assertion |
|---|---|
| `CreateSurface_HasRequestedSizeAndFormat` | mirrors the CPU contract test exactly |
| `ReadPixels_RoundTripsAKnownFill` | red fill → `FF0000FF` after `Flush` (this is the test that catches a wrong flush/submit order) |
| `TwentyCycles_CreateRenderReadDispose_AreStable` | **G1's gate as a test**: 20 iterations at 1920×1080, no throw, no leak (`GRContext` purge count stable) |
| `CreateAfterDispose_Recovers` | dispose the provider, create a new one in the same process, render again |
| `CrossThreadUse_Throws` | call `CreateSurface` from a different thread → `InvalidOperationException` (the §2.7 guard) |

**`BackendParityTests`** — the phase's headline validation.

- For each fixture in `tests/fixtures/playback2d/*.json`, at 1280×720 **and** 1920×1080: render via
  `CpuSurfaceProvider` and via `GpuSurfaceProvider` through the same `SceneCompositor` call, compare
  with `ImageDiffOptions.CrossBackend`.
- On failure: write `cpu.png`, `gpu.png`, `diff.png` to the artifact directory and put
  `ImageDiffResult.Summary` in the assertion message.
- Also compare GPU output against the **committed CPU golden** (not just a live CPU render) for the
  canonical fixture, so a golden drift and a backend drift are distinguishable.
- Minimum corpus (create synthetic fixtures if B0/C1 have not landed theirs, and say so):
  `duel-mirage-b.json` (markers/trails — geometry AA), `smoke-molly-inferno.json` (area effects —
  alpha blending and blur, historically the worst raster/GPU divergence), `text-hud-nuke.json`
  (`SKTextBlob` HUD rows — glyph rasterization, the other worst case).

**`GpuDeterminismTests`** — design §11 requires "two export runs of the same request produce
byte-identical frame hashes (per backend)".

| Case | Assertion |
|---|---|
| `SameFixture_TwiceOnGpu_IsByteIdentical` | SHA-256 of the RGBA buffers equal — `ImageDiffOptions.Exact` |
| `SameFixture_TwiceOnCpu_IsByteIdentical` | same, CPU (runs everywhere, no `[Category("Gpu")]`) |

**`GpuExportThroughputTests`** — `[Category("Gpu")]`, `[NotInParallel]`. Exports 256 frames at 1080p
through `SceneExportSession` with a null sink, asserts ≥ 128 fps (the ≥ 2× realtime exit criterion).
Only meaningful on the self-hosted lane; skips when `probe.Renderer` matches the known software
renderers (`llvmpipe`, `Microsoft Basic Render Driver`, `SwiftShader`, WARP) with reason
`"software renderer — throughput not meaningful"`.

### 7.3 The perceptual-diff definition (the numbers, and how to change them)

**Algorithm.** Both images are decoded to RGBA8888 premultiplied.

1. **Per-channel:** for every pixel, `d = max(|ΔR|, |ΔG|, |ΔB|)`. Compute the fraction of pixels with
   `d > MaxChannelDelta` and the global max `d`. Alpha is checked separately with a tighter bound —
   a backend that disagrees about *coverage* is a real bug, not an AA difference.
2. **SSIM:** convert to luma (`0.2126R + 0.7152G + 0.0722B`, on unpremultiplied values), 11×11
   Gaussian window σ = 1.5, `C1 = (0.01·255)²`, `C2 = (0.03·255)²`, stride 1 (or stride 2 with a
   documented note if the 1080p run is too slow — measure before optimizing). Report mean SSIM over
   all windows and the single worst window.

**Pass rule** (all must hold):

| Metric | Threshold | Why this number |
|---|---|---|
| Fraction of pixels with per-channel Δ > 8 | ≤ 0.5 % | 8/255 ≈ 3 % is below the just-noticeable difference for a single flat-region step and comfortably covers GPU vs raster rounding on gradients and AA edges; 0.5 % allows the AA fringe of the scene's geometry (a 1080p scene's total edge pixels are well under 0.5 % of 2.07 M) without allowing a whole shifted or recoloured element |
| Max per-channel Δ anywhere | ≤ 32 | a single edge pixel can legitimately land on the other side of a coverage rounding; 32/255 ≈ 12 % is far too small to hide a wrong colour, a missing glyph, or a misplaced marker |
| Max alpha Δ anywhere | ≤ 2 | coverage must agree |
| Mean SSIM | ≥ 0.995 | structure must be effectively identical; 0.995 is the level where a 1 px translation or a dropped stroke fails while AA noise passes |
| Min windowed SSIM | ≥ 0.95 | catches a *localized* structural defect (one glyph missing, one cone absent) that a global mean would average away — this is the metric that makes the policy meaningful |

**Changing a threshold** requires: (a) a saved `diff.png` showing the difference is legitimate
(AA/rounding, not content), (b) a line in `docs/playback2d-v2/c2-backend-decision.md` under "What
surprised us" naming the fixture and the new number, (c) reviewer sign-off. Thresholds are never
loosened silently, and never loosened globally to fix one fixture — prefer a per-fixture override
carried in the fixture's own metadata.

### 7.4 Commands

```bash
# Everything C2 owns, no GPU needed (skips the Gpu category automatically via the probe guard):
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

# Force the CPU path even on a GPU machine (proves the fallback is exercised, not just present):
DV2D_RENDER_BACKEND=cpu dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

# GPU-only subset on the spike machine:
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release -- --treenode-filter "/*/*/*/*[Category=Gpu]"

# The probe, first thing to run on any new machine or container:
dotnet run --project tools/DemoViewer.NET.Playback2D.Cli -- probe --json

# Exit-criterion evidence:
dotnet run --project tools/DemoViewer.NET.Playback2D.Cli -c Release -- bench --demo demos/benchmarks/003816248937665266002_0544286934.dem --frames 2000 --gpu
dotnet run --project tools/DemoViewer.NET.Playback2D.Cli -c Release -- bench --demo demos/benchmarks/003816248937665266002_0544286934.dem --frames 2000 --cpu
```

(TUnit's filter syntax should be confirmed against the 0.25.21 in use; if the tree-node filter differs,
fall back to `--treenode-filter` per TUnit's docs or a `[Property]`-based selector — this is a
5-minute detail, not a design point.)

---

## 8. Build & wiring

### 8.1 `Directory.Packages.props` additions

Insert next to the Avalonia block, with the comment (the comment is load-bearing — it is the only
thing stopping a future dependency bot from floating this independently and breaking ANGLE/Avalonia
coherence):

```xml
<!-- ANGLE (EGL + GLES2 over D3D11) native binaries — the windowless GPU render-surface backend
     (docs/playback2d-v2/design.md §5.8, plans/C2-gpu-provider.md).

     This is ALREADY in the desktop graph: Avalonia.Win32 11.3.12 depends on exactly this version,
     and the Desktop publish has been shipping av_libglesv2.dll all along. The explicit entry exists
     so projects that do NOT reference Avalonia (the dv2d CLI, the Playback2D test project) get the
     same DLL.

     PINNED, NOT FLOATED: the version must equal what the pinned Avalonia.Win32 depends on, or the
     app would load two different ANGLE builds. Bump it only in the same commit as an Avalonia bump,
     after re-checking avalonia.win32.nuspec.

     License: BSD-3-Clause (The ANGLE Project Authors) — see THIRD-PARTY-NOTICES.md §d. Unlike
     ffmpeg, this is LINKED IN-PROCESS, so the "separate programs" posture does not apply; the
     obligation is reproducing the notice, which §d does. -->
<PackageVersion Include="Avalonia.Angle.Windows.Natives" Version="2.1.25547.20250602"/>

<!-- SkiaSharp 2.88.9 is B0's entry (integrator correction 2) — C2 must NOT re-declare it. It is the
     version Avalonia.Skia 11.3.12 depends on, and the on-screen ISkiaSharpApiLeaseFeature path
     hands out types from that exact assembly. Do not "upgrade" to 3.x without a whole-app
     migration (see the AssetBaker csproj). -->
```

**Version policy note.** Two different rules apply in this file and it matters which one:
`Avalonia.Angle.Windows.Natives` and `SkiaSharp` are **coherence-pinned** — their correct version is
*derived* from the Avalonia pin, not chosen. Treat a dependabot PR that bumps either alone as a
defect. Everything else in this phase adds no packages at all.

### 8.2 Project references

`tools/DemoViewer.NET.Playback2D.Cli/DemoViewer.NET.Playback2D.Cli.csproj` — add:

```xml
<ItemGroup>
    <!-- ANGLE for the windowless GPU provider. The CLI does not reference Avalonia, so it does not
         get this transitively the way the Desktop head does. Native assets only — no managed
         assembly, so the Core "SkiaSharp only" architecture test is unaffected. -->
    <PackageReference Include="Avalonia.Angle.Windows.Natives" Condition="'$(OS)' == 'Windows_NT' OR '$(RuntimeIdentifier)' == 'win-x64'"/>
</ItemGroup>
```

(If the conditional proves awkward for a cross-RID `dotnet publish`, drop the condition — the package
contributes only `runtimes/win-*/native/*` and costs nothing on other RIDs.)

`src/Playback2D/DemoViewer.NET.Playback2D.Tests/…csproj` — the same `PackageReference`, so the GPU
suite can find ANGLE without an Avalonia reference.

`src/Testing/DemoViewer.NET.TestSupport/…csproj` — **no change** (correction 4: the comparator lives
in Pipeline, which already references SkiaSharp; TestSupport stays as it is).

Core adds **no** package references (§2.3).

### 8.3 `DemoViewer.NET.slnx`

No new projects in C2 — B0 creates Core/Pipeline/Tests and C1 creates the CLI. If C2 runs ahead of
them and must create the test project, add it under the same folder B0 uses:

```xml
<Folder Name="/src/Playback2D/">
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Tests.csproj"/>
</Folder>
```

### 8.4 CI

Add to `.github/workflows/ci.yml`. The existing `build` job is untouched.

```yaml
  # Render-backend correctness (docs/playback2d-v2/plans/C2-gpu-provider.md §8.4).
  #
  # Deliberately narrow: this runs ONLY the Playback2D test project, which parses no demos and
  # allocates no multi-GB ParsedDemo graphs — so it does NOT inherit the OOM problem that keeps the
  # App UI suite out of CI. Hosted runners have no GPU, but they DO exercise the real GPU code
  # paths: Windows runs ANGLE over D3D11 WARP, Linux runs EGL over llvmpipe. These lanes gate
  # CORRECTNESS AND PERCEPTUAL PARITY ONLY. The >=2x-realtime throughput number is gated on the
  # optional self-hosted lane below, never here — a software rasterizer's fps means nothing.
  #
  # A lane that finds no GPU backend SKIPS its GPU tests (SkipTestException) and stays green. That
  # is the design's rule: "CPU provider is the contract baseline - GPU is opportunistic" (§10 r7).
  render-backends:
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: ubuntu-latest
            backend: egl-llvmpipe
          - os: windows-latest
            backend: angle-warp
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      # libSkiaSharp.so already needs libGL; libegl1/libgles2 are what the surfaceless EGL path
      # binds. mesa-utils is only for diagnosing a failing probe from the log.
      - name: Install EGL/Mesa (Linux)
        if: runner.os == 'Linux'
        run: sudo apt-get update && sudo apt-get install -y libegl1 libgles2 libgl1 libglx-mesa0 mesa-utils

      - name: Probe backend
        env:
          LIBGL_ALWAYS_SOFTWARE: '1'
          GALLIUM_DRIVER: llvmpipe
        run: dotnet run --project tools/DemoViewer.NET.Playback2D.Cli -c Release -- probe --json

      - name: Render-backend tests (CPU contract + perceptual parity)
        env:
          LIBGL_ALWAYS_SOFTWARE: '1'
          GALLIUM_DRIVER: llvmpipe
        run: dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

      # Second pass with the GPU path forced off: proves the fallback is a real, exercised path and
      # not just a branch that compiles.
      - name: Render-backend tests (forced CPU)
        env:
          DV2D_RENDER_BACKEND: cpu
        run: dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

      - uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: render-diffs-${{ matrix.backend }}
          path: artifacts/test-output/**/*.png
          if-no-files-found: ignore

  # Real-GPU lane. Opt-in only: label a PR `gpu-lane` or dispatch the workflow manually. Requires a
  # self-hosted runner with the labels below; when none exists the job is simply never scheduled,
  # which is why it must NEVER be in the required-checks set.
  render-backends-gpu:
    if: ${{ github.event_name == 'workflow_dispatch' || contains(github.event.pull_request.labels.*.name, 'gpu-lane') }}
    runs-on: [self-hosted, windows, gpu]
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Probe (must find a GPU)
        run: dotnet run --project tools/DemoViewer.NET.Playback2D.Cli -c Release -- probe --json --require-gpu
      - name: GPU parity + throughput
        env:
          DV2D_RENDER_BACKEND: gpu
        run: dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
      - name: Bench (records, does not gate the PR)
        run: dotnet run --project tools/DemoViewer.NET.Playback2D.Cli -c Release -- bench --frames 2000 --gpu
```

Also add `workflow_dispatch:` to the workflow's `on:` block if it is not already there.

### 8.5 Native binary acquisition & packaging

**What we link, where it comes from, how it ships** — and how this differs from ffmpeg, so nobody
copies the wrong posture.

| | ffmpeg (B4) | **ANGLE (C2)** |
|---|---|---|
| Linkage | separate **subprocess**, pipes | **linked in-process** (`NativeLibrary.Load` → EGL/GLES calls) |
| License | GPL/LGPL — the FSF "separate programs" posture is what keeps it clean | **BSD-3-Clause** — permissive; in-process linking is fine |
| Obligation | show license text + source link on download | **reproduce the copyright notice + disclaimer in our documentation** (THIRD-PARTY-NOTICES.md §d) |
| Acquisition | locate → download-on-demand → managed-GIF floor | **already in the NuGet graph**; nothing to download, ever |
| Failure mode | feature unavailable, explained in UI | **falls back to CPU silently, reason logged once** |

- **Windows:** `Avalonia.Angle.Windows.Natives 2.1.25547.20250602` → `runtimes/win-{x64,x86,arm64}/native/av_libglesv2.dll`,
  built from `github.com/AvaloniaUI/angle` commit `cb8b4e1307a9d8f5ff56b8c5973bea4158ffead8`. The
  per-RID self-contained publish (docs/distribution §3–4) already emits it into the app tree; Velopack
  packages the publish output verbatim, so **there is no packaging work for the Desktop head** — only
  a verification step (C2.9). Verify with:
  `dotnet publish src/App/DemoViewer.NET.Desktop -c Release -r win-x64 --self-contained && ls artifacts/publish/**/av_libglesv2.dll`.
- **Load order:** `Egl` probes `DV2D_ANGLE_LIBRARY` (absolute path, escape hatch for testing a
  different ANGLE build) → `av_libglesv2.dll` → `libEGL.dll` (a vendor/system ANGLE, e.g. next to a
  Chromium install the user has). Never scan PATH aggressively; never download anything.
- **Linux:** we ship **nothing**. `libEGL.so.1` is a system library provided by the driver stack —
  exactly like `libGL.so`, which `libSkiaSharp.so` already has a hard dependency on. Do **not** bundle
  an EGL into the AppImage; a bundled EGL that does not match the host driver is worse than no EGL
  (it breaks the CPU path too). If EGL is absent the probe returns `CpuRaster` and everything still
  works.
- **macOS:** nothing shipped, nothing linked, probe returns `CpuRaster` (`"macos-deferred"`).
- **Binary weight:** +0 MB for the Desktop head (already shipping). +5.4 MB for a `win-x64` `dv2d`
  publish that previously had no Avalonia reference — acceptable for a dev/CI tool.

`THIRD-PARTY-NOTICES.md` gets a new section after §c:

```markdown
## d. ANGLE (BSD-3-Clause)

The Windows build links ANGLE (`av_libglesv2.dll`) in-process to create windowless EGL/OpenGL ES
contexts for GPU-accelerated offscreen rendering (2D playback video export and headless rendering).
The binary is redistributed as published in the `Avalonia.Angle.Windows.Natives` NuGet package,
built from https://github.com/AvaloniaUI/angle. Upstream project: https://github.com/google/angle.

<full LICENSE text from
 ~/.nuget/packages/avalonia.angle.windows.natives/2.1.25547.20250602/LICENSE — begins
 "Copyright 2018 The ANGLE Project Authors. All rights reserved.">
```

---

## 9. Dependencies

### 9.1 Consumed from other phases

| From | API | Used by | If it has not landed |
|---|---|---|---|
| **B0** | `IRenderSurfaceProvider`, `RenderBackend`, `CpuSurfaceProvider`, the Core project itself | everything in C2 | C2.0 creates them to the §6.1 shape and hands them to B0 — **do not fork a second shape** |
| **B0** | `SceneCompositor.Render(SKCanvas, SceneRenderContext)` (or whatever B0's exact render entry is) | `BackendParityTests` renders a fixture through it | parity tests fall back to a hand-drawn synthetic scene (documented as provisional) |
| **B0/C1** | `SceneFixture` JSON loader + `tests/fixtures/playback2d/*.json` | parity/determinism tests | C2 authors 3 minimal fixtures and flags them provisional |
| **B0/B1** | CPU goldens under `tests/goldens/playback2d/cpu/` | parity-vs-golden test | compare GPU against a live CPU render only, and say so |
| **B4** | `SceneExportSession.RunAsync(..., IRenderSurfaceProvider surfaces, ...)` | `GpuExportThroughputTests`, the ≥ 2× realtime number | throughput measured against a stub loop; the exit criterion cannot be *closed* until B4 lands |
| **B4** | the export dialog VM | C2.8's advanced option | C2.8 waits; nothing else blocks |
| **B2** | `Playback2DSettings` on `AppSettings` | C2.8's `RenderBackend` key | C2.8 waits |
| **C1** | `dv2d` CLI + its option parsing | C2.7's flags and `probe` subcommand | C2.7 waits; the factory API is usable without it |
| **CS2DemoKit (packages)** | nothing | — | C2 touches no parser API |

### 9.2 Exported by C2 (who consumes them)

| API | Consumer |
|---|---|
| `RenderSurfaceProviderFactory.Create/Probe/CreateCpu` | **B4** (`SceneExportSession` call site chooses the provider), **C1** (`dv2d render/export/bench`), **B1** (on-screen CPU fallback when the Skia lease is absent), any future highlight service |
| `RenderBackendPreference`, `RenderBackendPreferenceParser` | **C1** (flag parsing), **B4**/App (settings → preference) |
| `RenderSurfaceProbe` | **C1** (`dv2d probe`), App diagnostics tab / bug reports, CI |
| `GpuSurfaceProvider` (+ `TryCreate`) | the factory; direct construction is legal but discouraged |
| `ImageComparison`, `ImageDiffOptions`, `ImageDiffResult` | **B0/B1 golden tests** (`ImageDiffOptions.Exact` for byte-exact CPU goldens), **B4** export-frame goldens, C2's parity suite |
| `AppSettings.Playback2D.RenderBackend` (string key) | App settings UI, `SettingsService` |
| `DV2D_RENDER_BACKEND` env var (a **public contract** — CI and users depend on the spelling) | CI lanes, support instructions |
| `docs/playback2d-v2/c2-backend-decision.md` | closes design §12 Q2 |

---

## 10. Risks & spikes

| # | Risk | L | I | Mitigation | Time-box |
|---|---|---|---|---|---|
| R1 | **Windowless GPU context flaky across drivers/CI** (design §10 risk 7) | M | M | CPU is the contract baseline; ANGLE-first; `--cpu` everywhere; perceptual (not byte) parity; probe failures are data, never exceptions | the whole 3-day spike is this mitigation |
| R2 | **ANGLE loads but renders through WARP** on a machine that *has* a GPU (silent 20× perf loss, and the user sees "GPU" in the log) | M | M | `RenderSurfaceProbe.Renderer` carries `GL_RENDERER`; `dv2d probe` prints it; the throughput test *skips* on known software renderers rather than failing; log the renderer string at INFO on every export | 1 h (implemented in C2.3) |
| R3 | **GPU readback (`ReadPixels`) dominates and eats the 2× win.** A 1080p RGBA frame is 8.3 MB; a synchronous stall per frame can cost more than the draw | M | H | Measure readback separately from draw in C2.11; if it dominates, evaluate (a) `synchronous: false` + a 1-frame pipeline depth, (b) a second surface ping-ponged, (c) reading into pinned/pooled memory the sink already owns. Do **not** design any of this before measuring | 0.5 day inside C2.11; if unresolved, record it and ship the honest number |
| R4 | **`GRContext` + SkiaSharp 2.88.9 has a bug the 3.x line fixed** (2.88 is a 2024 build) | L | M | If and only if a specific blocker is found: record it, do **not** attempt a SkiaSharp 3.x migration inside C2 — that is a repo-wide change gated by Avalonia (§3.1) and would be its own phase | 2 h to diagnose, then stop |
| R5 | **Thread-affinity violation from a future caller** (an export session that hops threads, a test that parallelizes) | M | H | The §2.7 guard turns it into an immediate exception; `[NotInParallel]` on GPU test classes; XML docs state it on every member | built into C2.3 |
| R6 | **Perceptual thresholds are wrong** — too tight (flaky CI) or too loose (misses real regressions) | M | M | Calibrate on real hardware in C2.12 against a *deliberate* defect (delete one layer, shift one marker 1 px) and confirm the suite fails; thresholds move only with the §7.3 procedure | 2 h in C2.12 |
| R7 | **B0's provider seam differs from §6.1** (C2 is planned in parallel with B0's implementation) | M | M | C2.0 is exactly this check, and it is task #1. The design's §5.8 sketch is the shared source of truth for both phases | 0.5 h |
| R8 | **CI GPU lane never runs** because no self-hosted runner exists, so the ≥ 2× number is never re-verified after the spike | H | L | Accepted. The hosted lanes still gate correctness and parity; the throughput number is recorded in the decision doc + `bench-reports/` as a point-in-time measurement, exactly like the repo's existing perf-sweep practice | — |
| R9 | **ANGLE version drifts from Avalonia's** via an independent bump | L | H | The pinning comment in §8.1 plus a one-line check in the Avalonia-bump checklist | — |

---

## 11. Acceptance checklist

Maps 1:1 to the design's exit criterion plus this plan's additions.

**Design exit criterion — "GPU export ≥ 2× realtime at 1080p on a baseline dGPU/iGPU; CPU parity within perceptual tolerance"**

- [ ] `dv2d bench --frames 2000 --gpu` and `--cpu` both run to completion on the spike machine, numbers recorded in `docs/playback2d-v2/c2-backend-decision.md` and `bench-reports/`.
- [ ] A 1080p round export through `SceneExportSession` on `GpuSurfaceProvider` sustains **≥ 128 frames/s end-to-end (≥ 2× the 64 fps realtime rate), readback and sink included**, on a baseline dGPU or iGPU — measured, not extrapolated. *(Or: the fallback outcome of §4.3's kill rule is recorded and signed off.)*
- [ ] `BackendParityTests` passes on every fixture at 720p and 1080p under `ImageDiffOptions.CrossBackend` (§7.3 numbers), on real hardware.
- [ ] GPU output also passes against the committed **CPU goldens**, not only against a live CPU render.

**§5.8 requirements**

- [ ] Probe runs **once per process**, is thread-safe, never throws, and emits exactly **one** log line carrying backend + reason + `GL_RENDERER`.
- [ ] Probe order matches §5.8: Windows ANGLE→WGL, Linux EGL surfaceless→GBM, macOS CPU, anything-failed → CPU with the reason logged.
- [ ] `dv2d --gpu` / `--cpu` / `--backend <v>` work on `render`, `export`, and `bench`.
- [ ] `DV2D_RENDER_BACKEND` is honored, with the §2.5 precedence, and an unrecognized value warns and falls back to `Auto` without failing the run.
- [ ] The export dialog's advanced option is present, persists to `AppSettings.Playback2D.RenderBackend`, **and the key is in `SettingsService.WriteInMemory`**.
- [ ] `CpuSurfaceProvider` remains the always-available baseline: the full test suite passes with `DV2D_RENDER_BACKEND=cpu` on the GPU machine.

**§11 testing requirements**

- [ ] Determinism: two GPU runs of the same fixture are byte-identical; two CPU runs likewise.
- [ ] `ImageComparison` is unit-tested against synthetic cases including a 1-px shift that per-channel tolerance alone would pass (proves SSIM is doing work).
- [ ] Every GPU test **skips** with an informative reason on a machine without a backend; the suite is green on a no-GPU machine.

**This plan's additions**

- [ ] Core still references **only SkiaSharp** — the §11 architecture test passes unchanged (the EGL binding adds no package).
- [ ] `GpuSurfaceProvider` throws on cross-thread use, and a test proves it.
- [ ] 20 consecutive create→render→readback→dispose cycles at 1080p are stable, and create-after-dispose works.
- [ ] `dv2d probe --json` prints the decision; `--require-gpu` exits non-zero when there is no GPU.
- [ ] `THIRD-PARTY-NOTICES.md` §d carries the full ANGLE BSD-3-Clause text; `av_libglesv2.dll` is confirmed present in a `win-x64` self-contained publish of both the Desktop head and `dv2d`.
- [ ] `Directory.Packages.props` pins `Avalonia.Angle.Windows.Natives` to Avalonia's exact version, with the coherence comment.
- [ ] The `render-backends` CI job is green on both `ubuntu-latest` and `windows-latest`, and the GPU lane is **not** in the required-checks set.
- [ ] `docs/playback2d-v2/c2-backend-decision.md` exists, is filled in (including the `W3 Vulkan — eliminated pre-spike` row), and design §12 open question 2 links to it.
