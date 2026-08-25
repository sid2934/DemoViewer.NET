# Playback2D v2 on the browser — what works, what degrades, what is absent

**Head:** `src/App/DemoViewer.NET.Browser` (`net10.0-browser`, Avalonia.Browser) ·
**Verified:** 2026-08-25, phase B5 · **CI:** the `wasm-build` job publishes this head on every PR.

This is the per-capability record design §8 asks for. It is filled in from an actual run of the
**published** head in a real browser with a real demo loaded — not from reading the code, and not from
a `dotnet build` that succeeds whether or not the app can start.

---

## How the verification was done (repeat this once per release)

```bash
dotnet workload install wasm-tools --version 10.0.103   # pin: see "Boot requirements" below
dotnet publish src/App/DemoViewer.NET.Browser -c Release -o artifacts/wasm-publish

# any static server that serves .wasm as application/wasm
python -m http.server -d artifacts/wasm-publish/wwwroot 8765
```

Then open `http://127.0.0.1:8765/`, load a demo, and walk the checklist at the bottom.

**Publish, not build.** `dotnet build` of this head succeeds in states where the app cannot start: it
does not run ILLink, and until B5 it did not prove the native relink either. Both of the ways this head
has actually broken were invisible to a build. The CI job therefore publishes.

---

## Boot requirements (the two properties that make it start)

Both are set explicitly in `DemoViewer.NET.Browser.csproj` rather than inherited, because both are SDK
defaults today and a default that flips takes the app with it, silently, at runtime.

| Property | Why |
|---|---|
| `WasmBuildNative=true` | Relinks the native runtime so `@(NativeFileReference)` items are linked in. `libSkiaSharp.a` arrives through **Avalonia.Browser** (and again through Playback2D.Core's SkiaSharp reference, at the same 2.88.9 pin). Without the relink the head throws `System.DllNotFoundException: libSkiaSharp` out of `SKImageInfo`'s static constructor at boot — whether or not *our* code touches Skia, because Avalonia's own browser renderer needs the same native. B0 D11 finding 1. |
| `PublishTrimmed=false` | A trimmed publish fails outright: ~30 `IL2026` sites plus `IL2104` for `CS2DemoKit.Parser`, `CS2DemoKit.Analysis` and `FFMpegCore`. They are three whole mechanisms, not incidental call sites — reflection-based `System.Text.Json` in eleven stores, `ConfigurationBinder.Get<AppSettings>()` (which *is* the settings layer), and Avalonia's reflection `ViewLocator` (which resolves every view). Suppressing the warnings produces a head that builds and then throws `JsonSerializerIsReflectionDisabled` on the way up — B0 D11 finding 3 saw exactly that. |

**The `wasm-tools` manifest must match the SDK's runtime pack.** Installing the newest manifest (10.0.111
against runtime 10.0.103) relinks against a `System.Private.CoreLib` the runtime rejects: *"Your mono
runtime and class libraries are out of sync."* Pin it, and bump it in the same commit as the SDK.

**Payload size:** 63.1 MB uncompressed, **16.3 MB brotli** (the publish emits `.br`/`.gz` beside every
asset; serve them). That is the price of `PublishTrimmed=false`, and it is charged to a target the design
already calls degraded. Revisit when the JSON stores are source-generated — the day `dotnet publish` is
green with that property removed is the day to remove it.

---

## Capability matrix

`✅ works` · `⚠️ degraded (and the UI says so)` · `⛔ absent by design` · `❌ broken`

### Core playback

| Capability | Browser | Mechanism |
|---|:-:|---|
| Shell boots, tabs render | ✅ | Avalonia.Browser, software 2D canvas. See "Rendering" below. |
| Open a demo | ✅ | File System Access picker → `ArrayBuffer` → the ordinary parse path. An 11.5 MB, 19 237-frame Nuke demo parses in-browser in well under a minute on the interpreter. |
| 2D scene: markers, labels, rings, trails, smoke/fire, bomb | ✅ | The same seven `ISceneLayer`s as desktop, drawn by `SceneCompositor` through Avalonia's Skia lease. |
| Kill feed, round HUD, clock | ✅ | Same `TimelineHudDataSource`. |
| Floor label + multi-level panes | ✅ | `MapSpace` / `PaneSet` / `StackedLayout`. |
| Level strip (STACK · L0/L1 · AUTO) | ✅ | B3's strip, present on a two-floor map. |
| Timeline: round bands, kill/bomb markers, scrub | ✅ | Frame-index axis; a click seeks. |
| Follow player by card | ✅ | Local follow only — see LiveSync below. |
| Keymap (transport, rounds, kills, tools) | ✅ | Tunnelling handler on the view; no platform dependency. |
| Camera modes (Fit / Alive / Map / Follow) | ✅ | — |
| Overlay toggles | ✅ | — |
| Settings → the five 2D feature rows, live | ✅ | Toggling `playback2d.timeline` off removes the timeline **and the viewport reclaims the row**, with the status bar counter going 9 → 10 hidden. No restart. |

### Degraded

| Capability | Browser | What actually happens |
|---|:-:|---|
| **Annotations** | ⚠️ | Draw, erase, undo/redo and the timeline track all work **for the session**. The sidecar write lands in the WASM runtime's in-memory virtual filesystem, which the next reload discards. The panel says so, in those words: *"session only — this browser tab forgets annotations when it reloads."* (Before B5 it named a path — `saving to /sample-de_nuke.dem.dvann.json` — which a user reads as a promise. Fixed; pinned by `Playback2DAnnotationPersistenceTests.Controller_OnBrowser_SaysTheTabForgets_RatherThanNamingAPath`.) |
| **Radar images** | ⚠️ | Baked map art is loaded from disk beside the executable, and the browser head has no such directory. Every level falls back to the debug grid — the same visible no-radar state a map with no baked radar gets on desktop, so nothing is silently blank. Shipping the radar set as web assets is a real option, and is not B5's. |
| **Settings persistence** | ⚠️ | No settings file. Everything is held by the in-memory configuration provider `SettingsService.WriteInMemory` populates by hand — which is why **every** `Playback2DSettings` property must have a row there, and why `SettingsWasmRoundTripTests` reflects over the type rather than listing it. Preferences survive a tab switch, not a reload. |
| **Demo library / cache / bookmarks** | ⚠️ | Same shape: the stores write, the writes do not outlive the tab. Pre-existing, not 2D-specific. |
| **Window title diagnostics** | ⚠️ | `Process.GetCurrentProcess()` throws on browser, so the CPU/RAM/PID readout is not produced and the title stays the product name. Before B5 this threw from a **field initializer** during `MainViewModel` construction and the whole app came up black with one console line (`Process_PlatformNotSupported`). |

### Absent by design

| Capability | Browser | Why |
|---|:-:|---|
| **Video export** | ⛔ | `playback2d.export` ANDs `!OperatingSystem.IsBrowser()` in exactly one place — `ShellModuleFeatureGate.DesktopOnlyIds` (B5 D4). The Export affordance is not in the 2D toolbar at all, the export dialog is unreachable, and nothing calls `FfmpegDependency.Locate()`. There is no filesystem to write an mp4 to and no `System.Diagnostics.Process` to run ffmpeg with. |
| **Live Sync (CS2)** | ⛔ | `chrome.livesync`, desktop-only for the same reason (it drives a local CS2 install). `NotifySpectateTarget` is a no-op; the 2D follow still works locally. |
| **Processing queue** | ⛔ | `chrome.processingQueue`, needs a filesystem. |
| **`dv2d` CLI** | ⛔ | A console tool. Not part of this head. |
| **GPU render backend** | ⛔ | `RenderSurfaceProviderFactory` short-circuits to `CpuRaster` with reason `"browser"` **before any GPU probe runs** — there is no EGL to bind to. Pinned by `Playback2DWasmBudgetTests.ProviderFactory_ReturnsCpu_WhenBrowser_WithoutProbingTheGpu`. |

### Known cosmetic

- **The Video export row in Settings shows its toggle ON** on the browser, because the Settings list
  binds the shell `IFeatureGate` (the user's preference) and the platform AND is folded one layer further
  out, in the module projection. The row's own description says *"Desktop only."*, so it does not claim
  the feature is available — but the toggle state is uninformative there. Consolidating the two
  pre-existing `!IsBrowser()` call sites (`chrome.livesync`, `chrome.processingQueue`) and this one into
  the Settings surface is explicitly a follow-up, not a polish-phase refactor (B5 D4).

---

## Rendering: which path actually draws

Worth stating precisely, because "the browser uses the CPU provider" is nearly right and misleading.

- **On screen**, `Scene2DHost` draws through Avalonia's `ISkiaSharpApiLeaseFeature` — *Avalonia's*
  `SKCanvas`, into whichever canvas render mode the browser gave Avalonia. B1's `WriteableBitmap`
  fallback is the second path, and it deliberately does **not** use `CpuSurfaceProvider` (registry §3.7).
- **Offscreen** — goldens, benchmarks, export — `CpuSurfaceProvider` is the only path that exists on this
  head, which is what the factory's browser short-circuit guarantees. B0's D11 spike proved a raster
  `SKSurface` works under the WASM runtime; B5 proved the app path around it.

Under **headless** Chrome the console logs, harmlessly:

```
Failed to create render target for mode 3 : HTMLCanvasElement.getContext returned null.
Failed to create render target for mode 2 : HTMLCanvasElement.getContext returned null.
```

That is Avalonia trying WebGL2 then WebGL and falling through to software 2D, which then renders
everything correctly. On a real browser with a GPU, mode 3 succeeds. **No console line mentions ffmpeg,
`GRContext`, a filesystem path, or a trimming failure** — that absence is part of the checklist.

---

## Automated coverage, and what it does not claim

| Lane | What it proves |
|---|---|
| `wasm-build` (CI) | The head still restores, compiles, relinks its native runtime and survives ILLink, and the boot-critical artefacts are in the payload. |
| `Playback2DWasmBudgetTests` (Core suite) | A **browser-shaped proxy**: the browser's CPU path at the browser's viewport (1280×720), with relaxed timings and the allocation gate still at zero. Measured: advance p99 **0.014 ms**, render p99 **1.254 ms**, combined **1.268 ms** (ceilings 4 / 24 / 32), **0 B/frame**. |
| `SettingsWasmRoundTripTests` (App suite) | No `Playback2DSettings` property can be added without a `WriteInMemory` row — the WASM data-loss trap, closed mechanically. |
| `ShellModuleFeatureGateTests` | `playback2d.export` resolves false on the browser branch and the other four are untouched. |

**None of these is an in-browser run.** This repo has no WASM test host and CI has no browser runner, so
claiming automated browser coverage would be false (B5 D5). The checklist below is the part a human does.

---

## Manual checklist — run once per release

- [x] The published head boots; the shell and tab strip render.
- [x] A real demo opens and parses (`assets/tour/sample-de_nuke.dem`, 19 237 frames).
- [x] The 2D tab renders markers, labels, kill feed, round HUD and the floor label.
- [x] The timeline shows round bands and kill / bomb markers, and a click seeks.
- [x] The level strip appears on a multi-floor map.
- [x] Annotations draw and undo **in session**, and the panel says a reload loses them.
- [x] **Video export is absent from the UI entirely.**
- [x] Settings lists all five 2D sub-features, and toggling one takes effect live with no restart.
- [x] No console exception mentions ffmpeg, `GRContext`, a filesystem path, or trimming.
- [ ] Radar images render — **known degraded**, see the table above.
