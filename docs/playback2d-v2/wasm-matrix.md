# Playback2D v2 on the browser: what works, what degrades, what is absent

**Head:** `src/App/DemoViewer.NET.Browser` (`net10.0-browser`, Avalonia.Browser) ·
**CI:** the `wasm-build` job publishes this head on every PR.

**Verification state: read this before trusting a row.**

| | When | By what |
|---|---|---|
| **In-browser run** (the capability matrix and the checklist) | **2026-08-25, phase B5** | A human, against the published head in a real browser with a real demo. Nothing since has re-run it. |
| **Publish, payload size, boot-critical artefacts** | 2026-08-26, D6 round 3 | `dotnet publish` on this tree; figures below re-measured, artefact names listed. |
| **Rows the D and D6 tracks changed** | 2026-08-26, D6 round 3 | Read from the source that decides each one, and **labelled as such** where that is all the evidence there is. |

D6 round 3 re-stamped this file rather than re-verifying it, because re-verifying it means opening a
browser and this repo has no way to do that in CI. A stamp that says "verified 2026-08-25" over a
document the D track has since invalidated in four places is how this file got into the state the audit
found it in. Every row below now says which of the three rows above it rests on.

This is the per-capability record design §8 asks for. The B5 pass that filled it in was an actual run of
the **published** head in a real browser with a real demo loaded, not reading the code, and not a
`dotnet build` that succeeds whether or not the app can start.

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
| `WasmBuildNative=true` | Relinks the native runtime so `@(NativeFileReference)` items are linked in. `libSkiaSharp.a` arrives through **Avalonia.Browser** (and again through Playback2D.Core's SkiaSharp reference, at the same 2.88.9 pin). Without the relink the head throws `System.DllNotFoundException: libSkiaSharp` out of `SKImageInfo`'s static constructor at boot, whether or not *our* code touches Skia, because Avalonia's own browser renderer needs the same native. B0 D11 finding 1. |
| `PublishTrimmed=false` | A trimmed publish fails outright: ~30 `IL2026` sites plus `IL2104` for `CS2DemoKit.Parser`, `CS2DemoKit.Analysis` and `FFMpegCore`. They are three whole mechanisms, not incidental call sites: reflection-based `System.Text.Json` in eleven stores, `ConfigurationBinder.Get<AppSettings>()` (which *is* the settings layer), and Avalonia's reflection `ViewLocator` (which resolves every view). Suppressing the warnings produces a head that builds and then throws `JsonSerializerIsReflectionDisabled` on the way up. B0 D11 finding 3 saw exactly that. |

**The `wasm-tools` manifest must match the SDK's runtime pack.** Installing the newest manifest (10.0.111
against runtime 10.0.103) relinks against a `System.Private.CoreLib` the runtime rejects: *"Your mono
runtime and class libraries are out of sync."* Pin it, and bump it in the same commit as the SDK.

**Payload size**, re-measured 2026-08-26 (D6 round 3) on this tree: **63.5 MiB uncompressed, 16.4 MiB
brotli**. B5 recorded 63.1 / 16.3, so D4's two added packages cost about **+0.4 MiB** uncompressed and
**+0.1 MiB** over the wire.

**Units, because this figure has been quoted three different ways.** Those are *mebibytes*
(1024²); the same 63.5 MiB is **66.5 MB** decimal, which is where a "66.5" reading of this line comes
from. The method, so the next reading is comparable:

```bash
# uncompressed = every file under wwwroot EXCEPT the .br/.gz siblings
# brotli       = the .br files alone
# `du -sh wwwroot` reports ~102 MiB and is NOT either figure: it counts all three copies.
```

The publish emits `.br`/`.gz` beside every asset; serve them. That size is the price of
`PublishTrimmed=false`, and it is charged to a target the design already calls degraded. Revisit when the
JSON stores are source-generated: the day `dotnet publish` is green with that property removed is the day
to remove it.

**Boot-critical artefacts, present in that publish** (this is exactly what `ci.yml`'s payload check
asserts, run by hand here): `dotnet.native.3cndsq8pse.wasm`, `dotnet.js`,
`DemoViewer.NET.14br07f8wx.wasm`, `SkiaSharp.8dph0bad52.wasm`,
`Avalonia.Controls.ColorPicker.9p2k1rnx9s.wasm`, `AvaloniaEdit.bt1f0lthhe.wasm`. The hashes are
content-derived and will move; the names are the assertion.

---

## Capability matrix

Browser column values: `works`, `degraded`, `absent by design`, `broken`.

**The `degraded` legend used to read "degraded (and the UI says so)", and its own table contradicted it.**
Annotations say so; **Settings persistence** and **Demo library / cache / bookmarks** did not, and still
do not. "Silently degraded" is precisely the state this document exists to prevent, so the mark can no
longer smuggle a claim about the UI: whether the user is told is now stated per row, in the row.

### Core playback

| Capability | Browser | Mechanism |
|---|:-:|---|
| Shell boots, tabs render | works | Avalonia.Browser, software 2D canvas. See "Rendering" below. |
| Open a demo | works | File System Access picker → `ArrayBuffer` → the ordinary parse path. An 11.5 MB, 19 237-frame Nuke demo parses in-browser in well under a minute on the interpreter. |
| 2D scene: markers, labels, rings, trails, smoke/fire, bomb | works | The same scene layers as desktop, drawn by `SceneCompositor` through Avalonia's Skia lease. (This said "the same seven"; the catalog holds **eleven** ids since D3b added `hud.roster`, and B5 observed the seven world layers, which is what the row names.) |
| Kill feed, round HUD, clock | works | Same `TimelineHudDataSource`. |
| Roster cards (`hud.roster`, D3b) | unknown | Nothing platform-specific about it, and no reason it would differ, but it landed after the B5 run and **has not been seen on this head**. |
| Floor label + multi-level panes | works | `MapSpace` / `PaneSet` / `StackedLayout`. |
| Level strip (STACK · L0/L1 · AUTO) | works | B3's strip, present on a two-floor map. |
| Timeline: round bands, kill/bomb markers, scrub | works | Frame-index axis; a click seeks. |
| Follow player by card | works | Local follow only, see LiveSync below. |
| Keymap: the **shipped** gestures (transport, rounds, kills, tools) | works | Tunnelling handler on the view. B5 run. |
| Keymap: **rebinding** (D1's editor) | degraded | **Session only, and the UI says so** since D6 round 3. An override goes to the in-memory configuration provider and dies with the page; `SettingsView.axaml`'s `KeybindPersistenceNote` shows a caution line on the browser head and is collapsed on desktop. **And it now has a platform dependency**: `Playback2DKeymap.BrowserReservedGestures` refuses a rebind onto a gesture Chrome eats before the page sees it (`Ctrl+T`, `Ctrl+N`, `F12`), which `ShellReservedGestures` alone could not express. Both halves of this row's old text, "works" and *"no platform dependency"*, were false from D1 until round 3. *Source-verified only: neither the note nor a refusal has been seen in a browser.* |
| Camera modes (Fit / Alive / Map / Follow) | works | n/a |
| Overlay toggles | works | n/a |
| Settings → the five 2D feature rows, live | works | Toggling `playback2d.timeline` off removes the timeline **and the viewport reclaims the row**, with the status bar counter going 9 → 10 hidden. No restart. |

### Degraded

| Capability | Browser | What actually happens |
|---|:-:|---|
| **Annotations** | degraded | Draw, erase, undo/redo and the timeline track all work **for the session**. The sidecar write lands in the WASM runtime's in-memory virtual filesystem, which the next reload discards. The panel says so, in those words: *"session only — this browser tab forgets annotations when it reloads."* (Before B5 it named a path, `saving to /sample-de_nuke.dem.dvann.json`, which a user reads as a promise. Fixed; pinned by `Playback2DAnnotationPersistenceTests.Controller_OnBrowser_SaysTheTabForgets_RatherThanNamingAPath`.) |
| **Radar images** | degraded | Baked map art is loaded from disk beside the executable, and the browser head has no such directory. Every level falls back to the debug grid: the same visible no-radar state a map with no baked radar gets on desktop, so nothing is silently blank. Shipping the radar set as web assets is a real option, and is not B5's. |
| **Zone data** (`zones.json`, the user overlay, `playback2d.zones`) | absent | No asset directory on the browser host, so `ZoneAssetPipeline.TryLoad` returns null, `LoadedMapAsset.Zones` is null and the outline layer is never mounted; `AppPaths.ZonesDirectory` is null there too, so there is no overlay to read either. Every consumer degrades to its no-zones path (the Query Canvas snaps to sampled place centroids, the Grenade Index leaves the landing place empty, the Tolerance Slider offers exact match only). Shipping `zones.json` as a web asset is the same open option as the radar set. *Source-verified only: no browser run has been made with the zones layer in the build.* |
| **Settings persistence** | degraded | No settings file. Everything is held by the in-memory configuration provider `SettingsService.WriteInMemory` populates by hand, which is why **every** `Playback2DSettings` property must have a row there, and why `SettingsWasmRoundTripTests` reflects over the type rather than listing it. Preferences survive a tab switch, not a reload. **The UI says so only for the two surfaces that were asked to**: annotations (B5) and the keybinding editor (D6 round 3). The Settings screen as a whole still does not tell a browser user that *nothing* on it persists. |
| **Demo library / cache / bookmarks** | degraded | Same shape: the stores write, the writes do not outlive the tab. Pre-existing, not 2D-specific. **Nothing in the UI says so.** |
| **Round Facts** (the per-round rows in the demo cache) | degraded | The same store, so the same shape: the evaluator runs wherever a parse runs, its rows land in the in-memory record store, and they die with the tab. The timeline's round bands tint from them for the session. The engine row source evaluates the shipped `round_facts` ruleset from the shipped rules directory only, since the browser has no user rules directory to override it from. *Source-verified only*: no browser run has produced a row yet. |
| **Situation Search index** (the round index behind the Situations tab) | degraded | No queue and no filesystem, so no evaluator runs and no library index exists. The `SituationIndex` runs fully in memory: at most the demo open in the tab is searchable, for the session, and only if the coordinator's `FanOutParsed` fires on that head (unverified; it is `_openFanOutTask` on an interactive open). The strip says so, in those words: *"session only: no library index in the browser"*. `tab.situations` is not in `DesktopOnlyIds`: the tab renders and says what it cannot do, the `ExportUnavailableNote` shape. Both `SituationsSettings` keys carry a `WriteInMemory` row. The Query Canvas below the strip degrades with it: the map picker lists the maps the index rows name (none, with no library), and a picked map with no bundle directory arranges no pane and says *"no map art for &lt;map&gt; on this host"* over the empty surface. The Result Cards under the canvas follow the search: with no index there are no hits and no cards, and a card's thumbnail comes from the in-memory `.dvrp.json.gz` sibling the same store holds, never from a demo, so nothing here touches a filesystem, and Overlay all N stacks the same in-memory positions onto the canvas and touches none either. The filter rail renders in full: the fact fields read the in-memory rows, the opponent and our-side fields read the session-only Team Identity (so they list nothing until a team clusters in the session) and the source field reads the same session's labels; the live count on the Search button reads *"Search, 0 rounds"* with the coverage *"over 0 of 0 demos"*, which is the truth of the host rather than a blank. *Source-verified only.* |
| **Teams** (Team Identity's `teams.json` and `team-index.json`) | degraded | No config root, so `TeamIdentityService` keeps both files in memory: the rosters of whatever the in-memory cache holds cluster for the session, names, the us mark and the provenance pins of the Library card's label chip survive a tab switch and not a reload. The Teams tab says so, in the annotations panel's words: *"session only: this browser tab forgets teams when it reloads"*. `tab.teams` is not in `DesktopOnlyIds`: the tab renders and says what it cannot do. *Source-verified only.* |
| **Watched Situations** (`watched-situations.json` beside `teams.json`) | degraded | No config root, so `WatchedSituationsService` keeps the saved queries in memory: a watch saved in the tab re-evaluates on the in-memory index's `Indexed` hook for the session and is gone on reload. With no library index the hook fires at most for the demo open in the tab, so the "N new" badge on the tab header and the list almost never moves. The list says so, in the annotations panel's words: *"session only: watched situations are not saved in the browser"*. *Source-verified only.* |
| **Tags** (the Round Tagger's `tags/index.json` and `tags/demos/<sha256>.dvtag.json`) | degraded | No config root, so `TagStore` keeps every tag document in a dictionary: tagging, undo and redo work for the session, several demos' documents coexist, and a reload loses them all. `TagSession`'s status line says so, in the annotations panel's words: *"session only: this browser tab forgets tags when it reloads"*. `tab.tagger` and `playback2d.tagger` are not in `DesktopOnlyIds`. `TagQuery` (`Find`, `Pivot`, `At`) is pure over the documents it is handed and touches no filesystem, so on this host the Matrix and Label Mode read the session's in-memory documents and nothing else. The Matrix tab (`tagger.matrix`) shows the same line; its team field reads the session-only Team Identity, so it lists nothing until a team clusters in the session, and a count's clips go to the session-only Review Queue. The Tag Track draws the session's instances in a lane under the round bands; the 2D tab attaches the session with the shell's `DemoSha256`, hashed from the loaded bytes, so it does not need to read the file. The Tag Palette shows that line and tags as on the desktop, but offers only the built-in `cs2-default` palette: there is no `palettes/` directory to drop a team's palette into, and the active palette id is kept by the in-memory settings row `Playback2D:TagPaletteId` for the session. *Source-verified only.* |
| **Suggested tags** (`cache/suggestions/<StableKey>.json` proposals, `tags/verdicts/<sha256>.verdicts.json` verdicts, the Suggested track and the queue) | degraded | No cache root and no config root, so `ProposalStore` keeps each demo's proposals in memory and `TagStore` keeps the verdicts beside the session's tag documents: accept, edit and reject work for the session and a reload loses them, with the accepted instances. The evaluator builds the open demo from the parse the open already paid for, from a walk (there is no round index), and only once Round Facts has rows for it; no library sweep runs. The queue's header says *"session only"*, in the annotation panel's words. The profile is the embedded default and `site-regions.<map>.json` has no directory, so every map is site-only; there is no tuning view. `playback2d.suggestedtags` is not in `DesktopOnlyIds`. *Source-verified only*: the engine row source is parked on CS2DemoKit #54, so no browser run has produced a proposal yet. |
| **Review Queue** (`review-queue.json` beside `teams.json`, and the Review tab) | degraded | No config root, so `ReviewQueue` keeps the queue in memory: clips staged in the Reels tray, sent from the Result Cards or picked at the playhead sit in one list with their title cards and questions for the session, and a reload loses them. The Review tab says so, in the annotations panel's words: *"session only: this browser tab forgets the queue when it reloads"*. `tab.review` is not in `DesktopOnlyIds`: the tab renders and says what it cannot do. Open seeks through the same seam a Result Card uses. The Reels tray is a client of the same in-memory queue, so its staged clips are exactly as session-only as they were before the queue. *Source-verified only.* |
| **Strat Book** (`<config>/strats/`, the callouts tables and history logs beside it, and the Strat Book tab) | degraded | No config root, so `StratStore` keeps every book, strat and history log in memory: authoring, undo and the commit buffer work for the session, and a reload loses every strat. The tab says so, in the annotations panel's words: *"session only: this browser tab forgets strats when it reloads"* (`StratBookTabViewModel.BrowserNote`). `tab.stratbook` is not in `DesktopOnlyIds`: the tab renders and says what it cannot do. `StratEvidenceService`'s record pane reads the session-only Tag Store, and the book selector reads the session-only Team Identity, so both list nothing until the session has built them itself. The call sheet and the role sheets' HTML (`StratTextExporter`, `RoleSheetHtmlWriter`) are pure string builders and render the same on every host; only **LAN Print**'s launch degrades further, to *absent in effect*: `LanPrint.WriteTempFile` writes into the WASM runtime's in-memory filesystem same as any other file, but `LanPrint.WriteAndOpen`'s `Process.Start` throws on browser, caught by `OpenExternal.OpenUri`'s existing try/catch, so the export silently returns null rather than crashing; nothing in the UI yet tells the user why the print page never opened. *Source-verified only.* |
| **Grenade Walk, open demo** (`demos/<StableKey>.grenades.json` and `.grenades.paths.json` beside the demo's cache record) | degraded | No cache root, so `DemoCacheStore` holds both siblings in its in-memory map beside the record: the rows die with the tab. With no queue the only way a walk can run is `GrenadeIndexEvaluator.OnParsedOpportunistically` on the open demo, and only if the coordinator's `FanOutParsed` fires on that head (unverified, the Situation Search row's caveat). The Utility Book tab is the first reader and says "session only" for these rows (the Grenade Index row below). The walk decodes the demo about twice (the projectile sampler, then the pawn and command pass): measured 2.8 s over 154,869 frames native in Release (about 1.8 s per 100,000, input coverage 1.0 on the demo the App suite picks from the corpus), **unmeasured** on the interpreter, where the parse row above suggests tens of seconds. *Source-verified only.* |
| **Window title diagnostics** | degraded | `Process.GetCurrentProcess()` throws on browser, so the CPU/RAM/PID readout is not produced and the title stays the product name. Before B5 this threw from a **field initializer** during `MainViewModel` construction and the whole app came up black with one console line (`Process_PlatformNotSupported`). |

### Absent by design

| Capability | Browser | Why |
|---|:-:|---|
| **Video export** | absent | `playback2d.export` ANDs `!OperatingSystem.IsBrowser()` in exactly one place: `ShellModuleFeatureGate.DesktopOnlyIds` (B5 D4). The export dialog is unreachable and nothing calls `FfmpegDependency.Locate()`; there is no filesystem to write an mp4 to and no `System.Diagnostics.Process` to run ffmpeg with. **The button is gone but the slot now says why** (D6 round 3): `ExportUnavailableNote` renders *"Export video — unavailable in the browser"* where the button would be, because the same `CanExport` binding also hides it on desktop when no demo is open, and a user who cannot tell "not available here" from "open a demo first" has been told nothing. This row said "not in the toolbar at all" until D6; that was true and was the defect. |
| **Strat Export** (the Strat Book canvas to GIF or video) | absent | `stratbook.export` joins `playback2d.export` in `ShellModuleFeatureGate.DesktopOnlyIds`, for the same reason: an export writes a file and may drive ffmpeg, and the browser head has neither. `App.axaml.cs` wires no `StratExportHost` there, so `StratBookTabViewModel.CanExport` is false and the button's slot says *"Export strat: unavailable in the browser"* (`ExportUnavailableNote`) rather than vanishing. *Source-verified only.* |
| **Create Strat From Round** (a round band's right-click in 2D Playback) | works, session only | The parsed demo is in memory on both heads and the walk is a private `EntityTracker` over it, so the capture and the review run the same. `App.axaml.cs` wires `StratCaptureHost` in both lifetimes; the strat it commits lands in the session-only `StratStore` (the Strat Book row above), so it lives as long as the tab. *Source-verified only.* |
| **Live Sync (CS2)** | absent | `chrome.livesync`, desktop-only for the same reason (it drives a local CS2 install). `NotifySpectateTarget` is a no-op; the 2D follow still works locally. |
| **Processing queue** | absent | `chrome.processingQueue`, needs a filesystem. |
| **Grenade Walk, background index and "Index grenades"** | absent | The library sweep needs the processing queue. `GrenadesSettings` is desktop only (no `WriteInMemory` row; its Settings row sits in the desktop-only Highlights section), and `App.axaml.cs` leaves Match Overview's `IndexGrenades` null on the browser head, so the "Index grenades" chip is absent rather than inert. |
| **Grenade Index and the Utility Book tab** (every current `.grenades.json` in the cache, clustered by landing cell) | degraded | No cache root, so `GrenadeIndex` loads nothing at startup and merges only what the Grenade Walk writes to the in-memory cache this session, which on this head is the open demo at most (the row above). No zone data either (the Zone data row), so every landing place is empty: the landing grid still clusters and a place filter finds nothing. The tab says so: *"In the browser, grenades are indexed for the open demo only and this tab forgets them when it reloads."* (`UtilityBookTabViewModel.BrowserNote`). `tab.utilitybook` is not in `DesktopOnlyIds`. *Source-verified only.* |
| **`dv2d` CLI** | absent | A console tool. Not part of this head. |
| **GPU render backend** | absent | `RenderSurfaceProviderFactory` short-circuits to `CpuRaster` with reason `"browser"` **before any GPU probe runs**: there is no EGL to bind to. Pinned by `Playback2DWasmBudgetTests.ProviderFactory_ReturnsCpu_WhenBrowser_WithoutProbingTheGpu`. |

### Known cosmetic: closed

- ~~**The Video export row in Settings shows its toggle ON** on the browser~~: **fixed in D6 round 3.**
  The Settings list binds the raw `IFeatureGate`, which resolves catalog and override state and knows
  nothing about the platform, while modules read the same ids through `ShellModuleFeatureGate`, which
  ANDs `!IsBrowser()` in. So the browser showed a live, ON toggle for a capability forced off one layer
  out, and flipping it persisted an override nothing would ever honour. `FeatureToggleRow` now carries
  `IsPlatformUnavailable`, which makes the row non-interactive and labels it *"unavailable in the
  browser"*, the same wording the demo-folder picker already used. B5 recorded this as a D4 follow-up
  and D4 shipped without it, which is why it appears in the audit (D6 §4b). *Source-verified only.*

---

## Rendering: which path actually draws

Worth stating precisely, because "the browser uses the CPU provider" is nearly right and misleading.

- **On screen**, `Scene2DHost` draws through Avalonia's `ISkiaSharpApiLeaseFeature`, using *Avalonia's*
  `SKCanvas`, into whichever canvas render mode the browser gave Avalonia. B1's `WriteableBitmap`
  fallback is the second path, and it deliberately does **not** use `CpuSurfaceProvider` (registry §3.7).
- **Offscreen** (goldens, benchmarks, export): `CpuSurfaceProvider` is the only path that exists on this
  head, which is what the factory's browser short-circuit guarantees. B0's D11 spike proved a raster
  `SKSurface` works under the WASM runtime; B5 proved the app path around it.

Under **headless** Chrome the console logs, harmlessly:

```
Failed to create render target for mode 3 : HTMLCanvasElement.getContext returned null.
Failed to create render target for mode 2 : HTMLCanvasElement.getContext returned null.
```

That is Avalonia trying WebGL2 then WebGL and falling through to software 2D, which then renders
everything correctly. On a real browser with a GPU, mode 3 succeeds. **No console line mentions ffmpeg,
`GRContext`, a filesystem path, or a trimming failure**: that absence is part of the checklist.

---

## Automated coverage, and what it does not claim

| Lane | What it proves |
|---|---|
| `wasm-build` (CI) | The head still restores, compiles, relinks its native runtime and survives ILLink, and the boot-critical artefacts are in the payload. |
| `Playback2DWasmBudgetTests` (Core suite) | A **browser-shaped proxy**: the browser's CPU path at the browser's viewport (1280×720), with relaxed timings and the allocation gate still at zero. Measured: advance p99 **0.014 ms**, render p99 **1.254 ms**, combined **1.268 ms** (ceilings 4 / 24 / 32), **0 B/frame**. |
| `SettingsWasmRoundTripTests` (App suite) | No `Playback2DSettings` property can be added without a `WriteInMemory` row: the WASM data-loss trap, closed mechanically. |
| `ShellModuleFeatureGateTests` | `playback2d.export` resolves false on the browser branch and the other four are untouched. |

**None of these is an in-browser run.** This repo has no WASM test host and CI has no browser runner, so
claiming automated browser coverage would be false (B5 D5). The checklist below is the part a human does.

---

## Manual checklist: run once per release

**Last actually run: 2026-08-25 (B5). Not re-run since, and the boxes below are cleared to say so.**
Every item was ticked at B5 and the ticks stayed on through the whole D track, which shipped a
keybinding editor, a `Custom` envelope editor, two colour pickers and a settings-page change, so the
ticks had stopped meaning "someone looked" and started meaning "someone looked, once, at a different
build". D6 round 3 re-measured the publish (above) and re-read the source behind four rows, and could
not re-run these: they need a browser, and this repo has no way to drive one. **Re-run the whole list
and re-tick it in one sitting; do not tick individually as things are fixed.**

- [ ] The published head boots; the shell and tab strip render.
- [ ] A real demo opens and parses (`assets/tour/sample-de_nuke.dem`, 19 237 frames).
- [ ] The 2D tab renders markers, labels, kill feed, round HUD and the floor label.
- [ ] The timeline shows round bands and kill / bomb markers, and a click seeks.
- [ ] The level strip appears on a multi-floor map.
- [ ] Annotations draw and undo **in session**, and the panel says a reload loses them.
- [ ] **Video export is absent from the 2D toolbar**, and its Settings row is non-interactive and
      reads *"unavailable in the browser"* (D6 round 3, never seen in a browser).
- [ ] The keybinding editor shows the session-only caution, and refuses `Ctrl+T` / `F12` with a reason
      (D1 + D6 round 3, never seen in a browser).
- [ ] The annotation ink colour pickers render as real pickers rather than 46×24 of nothing
      (D4's `Avalonia.Controls.ColorPicker` include: the artefact ships, the control has not been seen).
- [ ] Settings lists all five 2D sub-features, and toggling one takes effect live with no restart.
- [ ] No console exception mentions ffmpeg, `GRContext`, a filesystem path, or trimming.
- [ ] Radar images render (**known degraded**, see the table above; expected to stay unticked).
