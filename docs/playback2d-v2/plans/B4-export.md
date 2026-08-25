# Phase B4 — Video export (implementation plan)

**Branch:** `feature/playback2d-v2` · **Owns design open question 1** (checkpoint-replay seek-core
boundary) · **Depends on:** B0 (Core + Pipeline projects, `Scene2DFrame`, `SceneFrameBuilder`,
`CpuSurfaceProvider`), B1 (layer stack, `SceneTime` plumbing, `MapSpace`/`LevelPane`).
**Parallel with:** C1 (`dv2d` CLI — consumes everything here), C2 (`GpuSurfaceProvider`).

This plan is self-contained: a coding agent should be able to execute it without reading
`design.md`. Where the design pinned a signature (§5.7, §5.8) it is reproduced verbatim and marked
**BINDING**. Where the design was silent, this plan makes the call and records it under
**Decisions made**.

> ## Integrator corrections (BINDING — supersede anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry. R1 and the
> `Playback2DSettings` ordering note are resolved here.
>
> 1. **R1 resolved, and `SceneFrameBuilder` is NOT re-shaped.** B0's D1 splits the Avalonia-typed
>    half of `Modules.Abstractions` into `…Abstractions.Ui`, so Pipeline *can* reference
>    `IPlaybackSnapshot`/`IPlayerState`/`IReadOnlyEntityView` and `SceneFrameBuilder.Build(in
>    SceneFrameInput)` stays as B0 shipped it. B4 instead adds
>    `Pipeline/Export/TrackerSceneSnapshot.cs`: a Pipeline-side adapter that presents an
>    `EntityTracker`'s state as `IReadOnlyList<IPlayerState>` + `IReadOnlyEntityView`, doing the
>    pawn↔slot join with `CS2DemoKit.Analysis.Plugins.PawnLookup`. That is the *only* new join, it
>    lives next to its single consumer, and C1's CLI gets it for free. Pipeline's
>    `CS2DemoKit.Analysis` reference is confirmed (B0 already declares it).
> 2. **`TrackerFrameSource` is C1's, not B4's.** C1 needs it in its first week for
>    `dv2d render --demo`/`bench --demo`, and B4 lands after it. Delete B4.4's "Create"; B4
>    **consumes** it. The canonical signature merges both plans and is in `00-overview.md` §3 — it
>    keeps B4's `Prepare(CancellationToken)`, `throwOnNonSequentialAccess`, and fps/speed/tickRate
>    `SceneTime` construction, plus C1's `DemoFrameIndexOf` and `static FrameIndexForTick`. If C1
>    slips past B4.4, B4 creates it **to that signature** and C1 consumes it — first lander wins,
>    but the shape does not change either way.
> 3. **`KillFeedRow` is a Core type, defined by B0** (`Tick, Attacker, Assister, Victim, Weapon,
>    Headshot, Penetrated, NoScope, ThroughSmoke, AttackerBlind, AttackerInAir, AssistedFlash`).
>    `Scene2DFrame.KillFeed` already carries it. **Do not declare a second `KillFeedRow` in
>    Pipeline** — B4.9's Pipeline record and its `KillerName`/`IsHeadshot`/`IsWallbang`/`IsNoScope`/
>    `IsThroughSmoke`/`IsFlashAssist` names are withdrawn; use B0's members. `KillFeedTimeline`
>    stays in Pipeline and windows over the Core record.
> 4. **`HudSnapshot` moves to `…Core.Hud`.** `IHudDataSource.At(int tick)` returns it and lives in
>    Core, so a Pipeline `HudSnapshot` will not compile. `TimelineHudDataSource` stays in Pipeline.
> 5. **Level identity is `MapLevelId`, not `string`.** `PaneCameraSnapshot` becomes
>    `(MapLevelId LevelId, ViewportTransform Transform, bool ManualOverride)` and
>    `CameraScript.Fixed` takes `IReadOnlyDictionary<MapLevelId, ViewportTransform> PaneTransforms`.
> 6. **`Playback2DSettings` is flat and fully flattened.** No nested `Playback2DExportSettings`: the
>    canonical properties are `ExportFormatId`, `ExportFps`, `ExportWidth`, `ExportHeight`,
>    `ExportOutputDirectory`, `ExportIncludeHud`, `ExportIncludeAnnotations` (B5's list), plus C2's
>    `RenderBackend` — B4's `ExportBackendOverride` is withdrawn in favour of that one key. B5 D3
>    also **reverses** B4.12's `WriteInMemory` decision: the whole `Playback2D` section is flattened,
>    export keys included. Delete the "DELIBERATELY PARTIAL … `Playback2D:Export:*` excluded" comment.
> 7. **One test project: `src/Playback2D/DemoViewer.NET.Playback2D.Tests`** (B0 creates it).
>    `…Core.Tests` / `…Pipeline.Tests` do not exist; both of B4's direct-execution tables land
>    there, and B4.17 adds **no** CI step — B0's `playback2d-tests` job already runs that project.
> 8. **Feature-catalog placement:** `playback2d.export` is the fifth row of the one contiguous v2
>    block A1 creates after `analysis.breakpoints`. The `!OperatingSystem.IsBrowser()` AND lives in
>    exactly one place, `ShellModuleFeatureGate.DesktopOnlyIds` (B5 D4) — B4 does not add a second
>    shim. Gate reads go through `IModuleContext.Features`.
> 9. **`ContractVersion` is bumped once per release, by A1 (to 1.2.0) and audited by B5.** B4 does
>    not bump it.
> 10. **Goldens live at `tests/fixtures/playback2d/goldens/cpu/`** (C1's corpus layout), not
>     `tests/fixtures/playback2d/goldens/export/`; name them `hud-clock@…`, `hud-killfeed-6rows@…`.
>     Comparison goes through B0's `GoldenImageComparer`/`GoldenTolerance`.

---

## Scope & exit criterion

The design's phase table row (§9), quoted exactly:

> | | B4 | Export: seek-core extraction, `SceneExportSession`, ffmpeg sink + GIF floor, dialog, settings, gates, HUD layers + snapshot tests | 1080p round export ≥ realtime on CPU; cancel-safe; refuses under LiveSync | 2 wk |

Supporting rules from §5.7 that this phase must satisfy:

- Export never touches the shared app clock; `TrackerFrameSource` owns a **private** tracker replay
  on a background thread.
- Fixed timestep `dt = 1/fps` through the same layer stack (`RenderPurpose.Export`), rendering to an
  `SKSurface` from an `IRenderSurfaceProvider`, `ReadPixels` → sink.
- Export enables two Core HUD layers (`ClockLayer`, `KillFeedLayer`) fed by the same pre-built kill
  timeline as the XAML HUD; snapshot tests pin export-HUD rows to the same VM data.
- In-app export runs under `HeavyJobGate` and **refuses to start** while a LiveSync session or reel
  job is active. The CLI has no such constraint.
- Sinks: `FfmpegFrameSink` via FFMpegCore (MIT) piping rawvideo RGBA over stdin to an ffmpeg
  **subprocess**. Defaults WebM/VP9 (`-c:v libvpx-vp9 -pix_fmt yuv420p -an`), MP4/H.264
  (`libx264 -crf`), GIF via `palettegen`/`paletteuse`. Progress frames-done based; cancel kills
  ffmpeg.
- ffmpeg acquisition ladder: `FfmpegDependency.Locate()` → download-on-demand pinned BtbN build with
  license text + source link → `ManagedGifSink` (ImageSharp) as the no-ffmpeg floor. Never
  Xabe.FFmpeg. Settings mirror `HighlightsSettings`.
- WASM: export feature-gated off in v1.

**Out of scope for B4:** `GpuSurfaceProvider` (C2), the `dv2d` command-line front end (C1 — B4 ships
the callable seam and a smoke test that drives it in-process), WebCodecs sink, audio of any kind,
highlight-reel auto-`CameraScript` emission.

---

## Decisions made

Numbered so later phases and reviews can cite them.

### D1 — Open question 1 is resolved: **there is nothing to extract from `MainViewModel`.**

The checkpoint-replay seek core is already a standalone, stateless, package-level type:

```
CS2DemoKit.Parser.EntityTracking.EntitySeekService     (+ readonly record struct SeekResult)
```

sourced from the `CS2DemoKit.Parser` NuGet package (pinned `0.10.0` in `Directory.Packages.props`).
`MainViewModel` owns only **an instance** of it:

- `src/App/DemoViewer.NET/ViewModels/Shell/MainViewModel.cs:205-206` — field
  `private readonly EntitySeekService? _seekService;`, commented *"Stateless checkpoint-replay seek
  core, shared by EntityTab's three seek pipelines."*
- `MainViewModel.cs:698-701` — constructed as `new EntitySeekService(CreateTracker)` and handed to
  `EntityTab.SeekService`.
- `MainViewModel.CreateTracker` (`MainViewModel.cs:2736-2751`) wires the new tracker's
  `PacketProcessed` to the Tier-3 `Debugger` and `DecodeErrorRaised` to the Output panel via
  `Dispatcher.UIThread.Post`. **Export must never use this factory** — it is UI/debugger-specific and
  would marshal to the UI thread from the export thread.

Its full surface (all three methods build a brand-new `EntityTracker` from the injected factory and
replay it **from frame 0**):

```csharp
public sealed class EntitySeekService
{
    public EntitySeekService(Func<EntityTracker> createTracker);
    public SeekResult SeekToFrame(int frameIndex, IReadOnlyList<DemoFrame> frames);
    public SeekResult SeekToFrameNoSnapshot(int frameIndex, IReadOnlyList<DemoFrame> frames);
    public SeekResult SeekToFrameWithSnapshotAt(int snapshotAt, int endFrameIndex, bool takeSnapshot,
        IReadOnlyList<DemoFrame> frames);
}
public readonly record struct SeekResult(EntityTracker Tracker,
    Dictionary<int, Dictionary<string, object?>>? PrevSnapshot);
```

**Therefore the "seek-core extraction" line item becomes:**

1. `MainViewModel` is **not modified**. Zero risk to the interactive seek path. (Task B4.0 is a
   documentation change only.)
2. `TrackerFrameSource` (Pipeline) constructs its **own** `EntitySeekService` with a bare
   `Func<EntityTracker>` factory (`() => new EntityTracker()`, optionally + a decode-error callback
   for CLI logging).
3. Design §12 open question 1 is marked resolved with a pointer to this document; the misleading
   phrase in §5.7 ("extract it from `MainViewModel`'s wiring") gets a one-line correction note.

**Concurrency safety (verified):** `EntityTracker` is `sealed`; every field is a per-instance
`Dictionary`/`List`. Its only static is `_decodeErrorConsoleLock`, whose own comment states *"Each
worker is a separate `EntityTracker` instance… Static so it spans all workers in a parallel decode"* —
multiple independent trackers over the same frame list is an already-supported production pattern.
The `IReadOnlyList<DemoFrame>` is immutable post-parse, so a second tracker walking it concurrently
with `PlaybackController.AuthoritativeTracker` is safe.

### D2 — One from-zero replay at export start; O(1) per frame after.

`§5.7` hoped to avoid "paying a from-zero replay to reach `StartFrame`". No checkpoint cache exists
today (every `EntitySeekService` call replays from 0, by design and by doc comment). B4 **accepts**
one from-zero replay, on a background thread, surfaced as an `ExportPhase.Seeking` progress phase.
It is amortized across hundreds-to-thousands of exported frames and is strictly less work than the
alternatives. Every subsequent frame steps once via `EntityTracker.AdvanceOneFrame(frames[i])`
(sub-millisecond), mirroring `PlaybackController.cs:346`. If risk-4's checkpoint-density cache ever
lands in Track A, `TrackerFrameSource` picks it up by swapping the seed call — no contract change.

### D3 — `ISceneFrameSource` is random-access by contract, forward-only by implementation.

`TrackerFrameSource` keeps a monotonic cursor:
`FrameAt(cursor)` → cached; `FrameAt(cursor + 1)` → one `AdvanceOneFrame` (the fast path
`SceneExportSession` always takes); any other index → full re-seed from 0 (correct, slow, logged
once per occurrence). A `ThrowOnNonSequentialAccess` ctor flag (default `false` in the app, `true`
in tests) turns a regression that makes the session non-monotonic into a test failure instead of a
silent 100× slowdown.

### D4 — HUD layers are fed by an `IHudDataSource`, **not** by a new `Scene2DFrame` field.

Adding a member to B0's `Scene2DFrame` record from B4 is a guaranteed merge conflict, and the design
already describes the HUD as fed by *"the same pre-built kill timeline as the XAML HUD"* — i.e. a
timeline queried by tick, not per-frame world state. `ClockLayer`/`KillFeedLayer` are therefore
constructed with an `IHudDataSource` that answers `HudSnapshot At(int tick)`. Pure function of tick →
deterministic → satisfies §5.1. Works headlessly in the CLI. No B0 coupling.

### D5 — The kill-feed **window** function moves to Pipeline; the **row builder** stays at the event source.

`Playback2DTabViewModel.BuildKillTimeline()` (`:653-686`) reads `GameEventView` from
`IModuleContext.GetEventTimeline("player_death")` — a `DemoViewer.NET.Modules.Abstractions` type,
and that project references Avalonia, so Pipeline cannot consume it. Only the **windowing** is the
drift-prone half, so only that moves:

- `KillFeedRow` (Pipeline) — identical shape to today's `KillFeedEntry`.
- `KillFeedTimeline.Window(rows, nowTick, tickRate, windowSeconds, maxRows)` (Pipeline, pure static)
  — the exact algorithm from `UpdateKillFeedWindow` (`:693-726`), including the load-bearing
  `k.Tick > lowTick && k.Tick <= nowTick` inclusive-upper bound.
- `Playback2DTabViewModel` is retargeted to `KillFeedRow` and calls `KillFeedTimeline.Window`;
  `KillFeedEntry` is deleted. Property names are identical, so the XAML bindings in
  `src/App/DemoViewer.NET/Views/Playback2D/Playback2DView.axaml` are unchanged.

Result: risk 8 (dual-HUD drift) becomes structurally impossible for the row *set*; the snapshot test
then only has to pin **formatting**. **Fallback if the retarget entangles more than expected
(time-box 3 h):** keep `KillFeedEntry`, add a `KillFeedRow` ⇄ `KillFeedEntry` projection in the App,
and rely on the snapshot test alone.

### D6 — GIF is a single ffmpeg invocation using `split` + `palettegen` + `paletteuse`.

A literal two-pass needs the input twice; over a stdin pipe that would force a multi-GB rawvideo
temp file. The standard single-input equivalent buffers inside one `-filter_complex`:

```
-filter_complex "[0:v]fps={fps},scale={w}:-1:flags=lanczos,split[a][b];
                 [a]palettegen=stats_mode=diff[p];
                 [b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle"
-loop 0
```

Semantically the design's two-pass; one process, no temp file. Because `palettegen` buffers the whole
stream, GIF export is capped (see D7).

### D7 — GIF has its own fps list and its own size/length caps.

GIF frame delay is an integer number of centiseconds, so only fps values dividing 100 are exact:
**{10, 20, 25, 50}**, default **20**. The 30/60/64 presets are video-only. GIF also defaults to
≤ 640 px wide and refuses > 1800 frames (`ExportValidationException`) — `palettegen` buffering plus
ImageSharp's in-memory `Image` make longer GIFs an OOM, not a slow export.

### D8 — Even dimensions are enforced for `yuv420p` formats.

`libvpx-vp9`/`libx264` with `-pix_fmt yuv420p` require even width and height. `ExportRequest`
validation throws `ExportValidationException` for odd dimensions when `FormatId` is `webm` or `mp4`
(GIF unaffected). The dialog's presets are all even; the custom-size fields snap down to even.

### D9 — The managed ffmpeg download is the **LGPL** BtbN build, Windows + Linux only.

WebM/VP9 (the default format) is present in LGPL builds; H.264 is not. Downloading the LGPL variant
keeps the redistribution story trivial and matches the design's "WebM/LGPL default" mitigation for
risk 9. If a user picks MP4 with only a managed LGPL ffmpeg, the dialog explains that H.264 needs a
GPL build on PATH and offers WebM instead. macOS gets install instructions (`brew install ffmpeg`)
and the GIF floor — no managed download (no comparably pinnable/hash-verifiable macOS artifact).

Archives are pinned by **exact release tag + SHA-256**; the hash is verified before extraction. The
consent gate shows the LGPL 2.1 text extracted from the archive plus the BtbN source link, and the
user must explicitly accept. Extraction target is `FfmpegDependency.ManagedDirectory`
(`<config>/tools/ffmpeg`), so `Locate()` finds it afterwards **and** `CsvgWebHost` reuses it for
reels — one download serves both features.

### D10 — Export takes a *new* `HeavyJobGate` session kind, not an interactive or background slot.

`AcquireInteractiveAsync` would block a user's demo open for the whole export; `AcquireBackgroundAsync`
would sit behind (and then in front of) interactive work. Neither matches an export's resource shape
(CPU-bound, one extra `EntityTracker`, no multi-GB parse). B4 adds `EnterExportSessionAsync`, which:

- **refuses** (throws `ReelInProgressException`) if a reel session is active;
- pauses **background** parses for its duration (they would steal CPU from a realtime-target encode)
  — i.e. `_exportSessions > 0` behaves like a reel for `CanStartBackground`/`AcquireBackgroundAsync`;
- does **not** block `AcquireInteractiveAsync` (the user's foreground demo load still wins);
- makes reel start **refuse** while held, via a new symmetric `ExportInProgressException` thrown from
  `EnterReelSessionAsync`.

This is the smallest change to a well-tested type that honors "runs under `HeavyJobGate` and refuses
under reel" without a UX regression.

### D11 — LiveSync refusal is a pre-flight check in the job service, before the gate.

`HeavyJobGate` knows nothing about LiveSync. The refusal mirrors `ReelJobService.cs:156`:
`liveSync.State.IsSessionActive || liveSync.OwnsSessionResources` → refuse with a clear message
(`ExportRefusedException`). Checked at `Start` and re-checked immediately after the gate is entered
(a session could start during the drain). If LiveSync starts *mid-export*, the export **continues**
(it never touches the app clock, so it cannot corrupt sync) — refusal is start-time only, exactly as
§5.7 specifies ("refuses to start").

### D12 — `CameraScript.MirrorLiveView` is a **capture**, taken once at export start.

Defined precisely: at the moment the user presses Start in the export dialog (before any frame is
rendered), the App snapshots the live `Scene2DHost`'s pane list into
`ImmutableArray<PaneCameraSnapshot>` — for each pane, its `MapLevel.Id`, its
`SliceCamera.Current` (`ViewportTransform`), and its `ManualOverride` flag — plus the host's current
`LevelDisplayMode`. That snapshot is embedded in the `CameraScript.MirrorLiveView` record and is
**never re-read**: subsequent live panning/zooming during the export changes nothing. The transforms
are re-fitted to the export size via `ViewportTransform.WithViewport(exportPaneW, exportPaneH)` so a
1080p export of a 700 px pane keeps the same world framing, not the same pixel scale.

`MirrorLiveView` is therefore behaviourally identical to `Fixed` once captured; it stays a distinct
case so (a) the dialog can label it, (b) a serialized CLI request can reject it with a clear
"mirror-live-view has no meaning headlessly — it is captured from a running window" message rather
than silently rendering a default camera.

### D13 — Determinism is asserted on **pre-encode RGBA frame hashes**, not on encoded bytes.

`libvpx-vp9` and `libx264` are not bit-reproducible across thread counts/versions. The determinism
test wraps the real sink in `HashingFrameSink` (a decorator that XXH/SHA-256s each RGBA buffer and
forwards) and asserts the two runs' hash sequences are byte-identical. Encoded-file equality is
explicitly not a contract.

### D14 — `FfmpegLocator` implementation moves to Pipeline; `FfmpegDependency` stays as a shim.

`FfmpegDependency` (App, `Services/Dependencies/`) has three existing consumers
(`App.axaml.cs:610`, `HighlightReelDialogViewModel`, `CsvgWebHost.cs:177`) and depends on
`AppPaths.ConfigRoot`. Pipeline needs the same PATH scan headlessly. Rather than duplicating it,
the scan body moves to `Pipeline/Ffmpeg/FfmpegLocator.cs` taking an explicit managed directory, and
`FfmpegDependency.Locate()` becomes a delegating shim that keeps `FfmpegStatus`/`FfmpegSource` and
their namespace unchanged. Zero churn for the three consumers.

### D15 — Layer ids for the export HUD are `hud.clock` and `hud.killfeed`.

Ids are persisted keys (they appear in `ExportRequest.LayerIds` and in saved export presets) — chosen
once, never renamed.

---

## Ordered work breakdown

Each task is ≤ ~half a day. **Ordering constraints** are stated per task; tasks with no stated
predecessor beyond the listed one can be parallelised.

### B4.0 — Record the seek-core boundary (docs only) · no predecessor
- **Modify** `docs/playback2d-v2/design.md` §12 item 1: append
  `**Resolved (B4):** the core is CS2DemoKit.Parser.EntityTracking.EntitySeekService — a package
  type, already standalone. MainViewModel owns only an instance. See plans/B4-export.md D1.`
- **Modify** `docs/playback2d-v2/design.md` §5.7 first bullet: append the same one-line correction so
  the parenthetical no longer implies App-side surgery.
- No code changes. Explicitly assert in the commit message that `MainViewModel.cs` is untouched.

### B4.1 — Packages, project references, solution, notices · predecessor: B0 (projects exist)
- **Modify** `Directory.Packages.props` — add (see *Build & wiring* for exact XML + comments):
  `FFMpegCore`, `SixLabors.ImageSharp`.
- **Modify** `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj`
  — add `<PackageReference Include="FFMpegCore"/>`, `<PackageReference Include="SixLabors.ImageSharp"/>`,
  and (if B0 didn't) `<PackageReference Include="CS2DemoKit.Analysis"/>` (needed for `PawnLookup`).
- **Modify** `src/App/DemoViewer.NET/DemoViewer.NET.csproj` — `ProjectReference` to Pipeline (if B1
  hasn't already added it).
- **Modify** `DemoViewer.NET.slnx` — ensure the `/src/Playback2D/` folder lists Core, Pipeline and
  their `.Tests` projects (create the folder block if B0 didn't).
- **Modify** `THIRD-PARTY-NOTICES.md` §c — add `FFMpegCore` (MIT) and `SixLabors.ImageSharp`
  (Six Labors Split License 1.0 — Apache-2.0 terms for open-source projects, which this repo is);
  add a new §d **"ffmpeg (not redistributed)"** stating: DemoViewer invokes ffmpeg as a **separate
  program** over a pipe and ships no ffmpeg binary; an optional in-app download fetches a pinned
  **LGPL** BtbN build, displays its license, and links its source.
- **Verify:** `dotnet build src/App/DemoViewer.NET.Desktop -c Release` succeeds.

### B4.2 — `FfmpegLocator` in Pipeline + `FfmpegDependency` shim · predecessor: B4.1
- **Create** `src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/Ffmpeg/FfmpegLocator.cs` — the PATH
  scan lifted verbatim from `FfmpegDependency.FindOnPath` (`FfmpegDependency.cs:57-81`), plus the
  managed-directory probe; returns `FfmpegLocation`.
- **Modify** `src/App/DemoViewer.NET/Services/Dependencies/FfmpegDependency.cs` — `Locate()` becomes
  a delegating shim mapping `FfmpegLocation` → the existing `FfmpegStatus`. Keep `ManagedDirectory`,
  `FfmpegStatus`, `FfmpegSource` and the namespace exactly as they are; keep the doc comment and add
  a line pointing at the Pipeline implementation.
- **Verify:** existing `HighlightReelDialogViewModel` ffmpeg pre-flight tests still pass.

### B4.3 — `FfmpegAcquisition` (download ladder) · predecessor: B4.2
- **Create** `Pipeline/Ffmpeg/FfmpegAcquisition.cs` — pinned-build table (tag + URL + SHA-256 per
  RID), `Offer()` (returns `FfmpegDownloadOffer` or null on unsupported OS), `AcquireAsync(offer,
  consent, progress, ct)`: download → verify SHA-256 → extract `bin/ffmpeg[.exe]` and
  `bin/ffprobe[.exe]` into the target directory → re-`Locate()`. Consent is an injected
  `Func<FfmpegDownloadOffer, CancellationToken, Task<bool>>`; the license text is read out of the
  archive (`LICENSE.txt`) and handed to the consent callback so no license text is vendored here.
- **Create** `Pipeline/Ffmpeg/FfmpegDownloadOffer.cs` (record) — see *Public API contracts*.
- Cancellation: a cancelled/failed download leaves **no** partial file (download to `*.part`, rename
  on success).
- **Verify:** unit test with a local `file://`-ish injected `HttpMessageHandler`; no network in CI.

### B4.4 — `TrackerFrameSource` · predecessors: B0 (`SceneFrameBuilder`, `Scene2DFrame`), B4.1
- **Create** `Pipeline/Export/TrackerFrameSource.cs` implementing `ISceneFrameSource` per D1/D2/D3.
  Owns: `IReadOnlyList<DemoFrame>`, its own `EntitySeekService(() => new EntityTracker())`, a
  `SceneFrameBuilder`, a monotonic cursor, the last built `Scene2DFrame`.
- `Prepare(int startFrame, CancellationToken)` performs the one-time `SeekToFrameNoSnapshot` seed
  (blocking, called from the export background thread).
- `TimeAt(i)` computes `SceneTime` from the frame's `ServerTick`, the demo tick rate, the request's
  `Fps`/`Speed` (`DeltaSeconds = Speed / Fps`), and `IsDiscontinuity = (i == StartFrame)`.
- **Never** publishes its tracker to `PlaybackController.PublishTracker` — assert this with a code
  comment and an architecture test (B4.15).

### B4.5 — Core export contracts + `SceneExportSession` · predecessors: B0 (`IRenderSurfaceProvider`, `SceneCompositor`), B1 (layer stack)
- **Create** in `src/Playback2D/DemoViewer.NET.Playback2D.Core/Export/`:
  `ISceneFrameSource.cs`, `IFrameSink.cs`, `ExportRequest.cs`, `CameraScript.cs`,
  `ExportProgress.cs`, `ExportValidationException.cs`, `SceneExportSession.cs`.
- `SceneExportSession.RunAsync` loop: validate request → create surface once (`SKSizeI Size`) →
  for `i` in `[StartFrame, EndFrame]`: `src.TimeAt(i)`, `src.FrameAt(i)`, resolve cameras from the
  `CameraScript`, `layer.Advance(time, frame)` for enabled layers, `compositor.Render(canvas, ctx)`
  with `RenderPurpose.Export`, `provider.Flush(surface)`, `surface.PeekPixels().ReadPixels(...)` into
  a pooled RGBA buffer, `await sink.WriteAsync(...)`, report progress, `ct.ThrowIfCancellationRequested()`.
- Layer filtering by `ExportRequest.LayerIds` (empty set = "all enabled layers"); `hud.clock` /
  `hud.killfeed` are off unless listed.
- One surface, one `SKBitmap`-free read path, `ArrayPool<byte>.Shared` for the RGBA staging buffer —
  the ≥ realtime budget requires no per-frame allocation here either.

### B4.6 — Camera scripts · predecessor: B4.5, B1 (`ICameraRig`, `LevelPane`)
- **Create** `Core/Export/CameraScriptResolver.cs` — turns a `CameraScript` + `MapSpace` +
  `Scene2DFrame` + export size into the per-pane `ViewportTransform`s for the current export frame:
  - `Fixed` → the stored per-level transforms, re-fitted with `WithViewport`.
  - `FollowPlayer(steamId)` → drive `FollowPlayerRig`, stepping each pane's `SliceCamera` with
    `StepToward(target, t)` where `t` derives from `SceneTime.DeltaSeconds` (deterministic — the
    same settle constant the interactive path uses, so an export looks like the live view).
    Unresolvable/dead target → hold the last transform (never snap to origin).
  - `MirrorLiveView` → per D12: identical handling to `Fixed`, from the captured snapshot.
- **Create** `Core/Export/PaneCameraSnapshot.cs`.

### B4.7 — `FfmpegFrameSink` + the push→pull pump · predecessor: B4.5
- **Create** `Pipeline/Export/FfmpegFrameSink.cs` and `Pipeline/Export/ChannelVideoFrameSource.cs`.
- **The load-bearing detail:** FFMpegCore's `RawVideoPipeSource` is a **pull** source — it takes an
  `IEnumerator<IVideoFrame>` and drains it on the ffmpeg pump task. `IFrameSink.WriteAsync` is a
  **push**. Bridge them with a bounded `Channel<PooledRgbaFrame>` (capacity **4**, `SingleReader`,
  `SingleWriter`, `FullMode = Wait`) exposed through `ChannelVideoFrameSource : IEnumerator<IVideoFrame>`:
  the sink rents from `ArrayPool<byte>`, copies the RGBA span in, and `await`s the channel write
  (natural backpressure — the renderer never outruns the encoder); the enumerator returns the buffer
  to the pool after `Serialize`. `DisposeAsync` completes the channel and awaits the ffmpeg task.
- Argument construction (explicit, never inferred):
  - input: `-f rawvideo -pix_fmt rgba -video_size {w}x{h} -framerate {fps} -i -`
  - `webm`: `-c:v libvpx-vp9 -b:v 0 -crf {crf} -pix_fmt yuv420p -row-mt 1 -an`
  - `mp4`: `-c:v libx264 -preset {preset} -crf {crf} -pix_fmt yuv420p -movflags +faststart -an`
  - `gif`: per D6.
- Cancellation: `.CancellableThrough(ct)` on the FFMpegCore processor; on cancel the partial output
  file is deleted unless `ExportRequest`'s settings say otherwise.
- `FFMpegCore.GlobalFFOptions.Configure(new FFOptions { BinaryFolder = <located dir> })` is **not**
  used (process-global mutable state); the sink passes the binary folder per-invocation via the
  `FFOptions` overload so a CLI and an in-app export can disagree.

### B4.8 — `ManagedGifSink` (ImageSharp floor) · predecessor: B4.5
- **Create** `Pipeline/Export/ManagedGifSink.cs` — accumulates frames into an ImageSharp
  `Image<Rgba32>` (`WuQuantizer`, global palette, `GifFrameMetadata.FrameDelay` in centiseconds from
  D7's fps list, `RepeatCount = 0`), writes on `DisposeAsync`. Enforces the D7 caps and throws
  `ExportValidationException` above them **before** rendering starts (the session validates the
  request first, so the user never renders 2000 frames into a refusal).

### B4.9 — HUD data source + `ClockLayer` + `KillFeedLayer` · predecessors: B1 (layer contract), D5
- **Create** `Pipeline/Hud/KillFeedRow.cs`, `Pipeline/Hud/KillFeedTimeline.cs` (pure static
  `Window(...)`), `Pipeline/Hud/HudSnapshot.cs`, `Core/Hud/IHudDataSource.cs`,
  `Pipeline/Hud/TimelineHudDataSource.cs`.
- **Create** `Core/Layers/ClockLayer.cs` (`Id = "hud.clock"`, `Slot = Hud`, `Cache = Dynamic`) and
  `Core/Layers/KillFeedLayer.cs` (`Id = "hud.killfeed"`, `Slot = Hud`, `Cache = Dynamic`). Both draw
  with keyed `SKTextBlob`/`SKFont` caches — no per-frame text shaping (§6 allocation contract).
  `ClockLayer` renders round number, T/CT score, and the main countdown (round clock, or C4 countdown
  when `BombTicking`), matching `GameInfo`'s semantics; `KillFeedLayer` renders up to 6 rows with the
  headshot/wallbang/noscope/smoke/blind/air/flash-assist modifier glyphs.
- **Modify** `src/App/DemoViewer.NET/Modules/Playback2D/Playback2DTabViewModel.cs` — retarget
  `_allKills`/`_killWindow`/`KillFeed` to `KillFeedRow`; replace the body of `UpdateKillFeedWindow`
  (`:693-726`) with a call to `KillFeedTimeline.Window` + the existing unchanged-slice short-circuit.
- **Delete** `src/App/DemoViewer.NET/Modules/Playback2D/KillFeedEntry.cs`.
- **Modify** `src/App/DemoViewer.NET.App.Tests/Playback2DKillFeedTests.cs` (+
  `Playback2DKillFeedRenderTests.cs`) for the type rename.

### B4.10 — `HeavyJobGate` export session · predecessor: none (independent)
- **Modify** `src/App/DemoViewer.NET/Services/HeavyJobGate.cs` per D10: add `_exportSessions`,
  `IsExportActive`, `EnterExportSessionAsync`; include `_exportSessions == 0` in `CanStartBackground`
  and in `AcquireBackgroundAsync`'s admission test; throw `ExportInProgressException` from
  `EnterReelSessionAsync` when an export is active. **Do not** touch `AcquireInteractiveAsync`.
- **Create** `ExportInProgressException` next to `ReelInProgressException` (same file).
- **Modify** `src/App/DemoViewer.NET.App.Tests/HeavyJobGateTests.cs` — new cases (B4.15).

### B4.11 — `ExportJobService` (App-side orchestration) · predecessors: B4.4, B4.5, B4.7, B4.8, B4.10
- **Create** `src/App/DemoViewer.NET/Services/Export/IExportJobService.cs` and
  `src/App/DemoViewer.NET/Services/Export/ExportJobService.cs` — the shape deliberately mirrors
  `IReelJobService`/`ReelJobService`: single-flight `Start(Scene2DExportRequest)`, `CancelAsync()`,
  `Status`, `StatusChanged` (marshalled to the UI thread via `Dispatcher.UIThread.Post` when one
  exists). The whole job runs on `Task.Run`; the **only** UI-thread work is status marshalling.
- Order inside `RunAsync`: LiveSync pre-flight refusal (D11) → `EnterExportSessionAsync` →
  re-check LiveSync → resolve ffmpeg (`FfmpegLocator` → offer download → GIF floor) → build
  `TrackerFrameSource` → `Prepare` → `SceneExportSession.RunAsync` → dispose sink → publish terminal
  status **after** the gate is released (the `ReelJobService.cs:138-141` "IsRunning stays true until
  the machine is free" pattern).
- **Modify** `src/App/DemoViewer.NET/Services/AppHostHooks.cs` — no new hook needed (the service is
  platform-neutral managed code); it is constructed in `App.axaml.cs` alongside the reel service.

### B4.12 — Settings · predecessor: none (but see the B2 ordering note)
- **Modify** `src/App/DemoViewer.NET/Configuration/AppSettings.cs` — add
  `public Playback2DSettings Playback2D { get; set; } = new();` and, inside `Playback2DSettings`,
  `public Playback2DExportSettings Export { get; set; } = new();`.
  **Ordering:** B2 also creates `Playback2DSettings`. Whichever phase lands first creates the class;
  the second adds only its own property. If B4 lands first, create the class with a comment naming
  B2's incoming annotation-tool properties.
- **Modify** `src/App/DemoViewer.NET/Configuration/SettingsService.cs` `WriteInMemory`
  (`:419-448`) — add a comment to the existing "DELIBERATELY PARTIAL" block naming
  `Playback2D:Export:*` as excluded (export is feature-gated off on WASM, exactly like LiveSync and
  Highlights). **B2's tool prefs are WASM-reachable and MUST be flattened** — say so in the comment
  so B2 doesn't miss it.
- **Modify** `src/App/DemoViewer.NET/ViewModels/Settings/SettingsViewModel.cs` + its view — one new
  section mirroring the Highlights/reel section: output directory, default format, default fps,
  default resolution, CRF, "prefer GPU when available" (advanced), "managed ffmpeg directory" +
  Re-check.

### B4.13 — Feature gate · predecessor: none
- **Modify** `src/App/DemoViewer.NET/Features/FeatureCatalog.cs` — append (order matters: the
  catalog's leader-lock test keys off position, so add **after** the existing group members, next to
  the other Playback2D sub-features B2/B3 introduce):
  ```
  new("playback2d.export", FeatureScope.SubFeature, "Video export",
      "Render the 2D playback to webm/mp4/gif. Desktop only.",
      "tab.playback2d", null, false, Defaults(true, true, true)),
  ```
- **Modify** the shell shim that resolves it to additionally AND `!OperatingSystem.IsBrowser()` —
  same treatment as `chrome.livesync` (`FeatureCatalog.cs:158-167` documents the pattern).
- Id is a persisted key — never rename.

### B4.14 — Export dialog (thin) · predecessors: B4.11, B4.12, B4.13
- **Create** `src/App/DemoViewer.NET/ViewModels/Playback2D/Playback2DExportDialogViewModel.cs` and
  `src/App/DemoViewer.NET/Views/Playback2D/Playback2DExportDialogView.axaml(.cs)`.
  Naming follows the ViewLocator `…ViewModel` → `…View` mapping.
- The VM is **thin by constraint (b)**: it collects range (current round / timeline selection /
  custom frames), format, preset size, fps, camera script choice, layer toggles, output path; it
  validates via `SceneExportSession`'s own request validator (no duplicated rules); it calls
  `IExportJobService.Start`. Every reusable rule lives in Pipeline/Core so `dv2d export` gets it free.
- Mirrors `HighlightReelDialogViewModel`'s proven seams: injected `Func<FfmpegLocation>` locator
  (null ⇒ assume present, keeping pure-VM tests filesystem-free), injected `Func<bool>`
  `isLiveSyncSessionActive`, injected `Action<Action<AppSettings>>` `persistDefaults`, injected
  `Func<string,bool>` `fileExists`.
- The ffmpeg strip mirrors `HighlightReelDialogViewModel.cs:400-460`: missing-ffmpeg message,
  instructions, **Download (LGPL)** button (opens the consent sheet from B4.3), Re-check, and a
  "GIF only, without ffmpeg" fallback affordance.
- Progress + cancel live on a status chip bound to `IExportJobService.Status` — **not** a modal
  (same rationale as the reel job).

### B4.15 — Tests · predecessors: all of the above (write alongside each task; land as one suite)
See *Test plan*.

### B4.16 — CLI seam smoke · predecessor: B4.11
- No `dv2d` project is created here (C1 owns it). B4 adds one Pipeline-level test that drives
  `SceneExportSession` + `TrackerFrameSource` + `ManagedGifSink` with **no App types referenced at
  all**, proving the seam C1 will consume is complete. If C1 has already landed, additionally add
  `dv2d export --demo … --from … --to … --format gif` to that test as a process invocation.

### B4.17 — CI + docs · predecessor: B4.15
- **Modify** `.github/workflows/ci.yml` — add a `dotnet run --project
  src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release --
  --treenode-filter "/*/*/(Export*|Ffmpeg*|KillFeed*)/*"` step. This is the first automated test
  execution in this repo's CI; it is safe to add here because these tests are direct-execution (no
  Avalonia platform, no headless session, no demo parse) and therefore not the OOM-prone shape the
  CI comment warns about. Do **not** add the App UI suite.
- **Create** `docs/playback2d-v2/export.md` — user-facing: formats, presets, the ffmpeg ladder, the
  LiveSync/reel refusal, and the GIF caps.

---

## Public API contracts

**BINDING for other phases.** Signatures marked *(design §5.7 verbatim)* must not be altered.

### Core — `DemoViewer.NET.Playback2D.Core.Export`

```csharp
// (design §5.7 verbatim)
public interface ISceneFrameSource
{
    int FrameCount { get; }
    SceneTime TimeAt(int frameIndex);
    Scene2DFrame FrameAt(int frameIndex);
}

// (design §5.7 verbatim)
public interface IFrameSink : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct);
}

// (design §5.7 verbatim)
public sealed record ExportRequest(int StartFrame, int EndFrame, int Fps, SKSizeI Size, double Speed,
    string FormatId /* webm | mp4 | gif */, IReadOnlySet<string> LayerIds, CameraScript Camera);

/// <summary>Well-known <see cref="ExportRequest.FormatId"/> values. Persisted keys.</summary>
public static class ExportFormats
{
    public const string WebM = "webm";
    public const string Mp4  = "mp4";
    public const string Gif  = "gif";
}

// (design §5.7: "Fixed(transform) | FollowPlayer(steamId) | MirrorLiveView")
public abstract record CameraScript
{
    /// <summary>Per-level transforms held for the whole export. Key = MapLevelId (correction 5).</summary>
    public sealed record Fixed(IReadOnlyDictionary<MapLevelId, ViewportTransform> PaneTransforms) : CameraScript;

    /// <summary>Follow one player by SteamId; hides/holds while unresolvable or dead.</summary>
    public sealed record FollowPlayer(ulong SteamId, double DeadzoneHalfExtentWorld = 900d) : CameraScript;

    /// <summary>Captured once at export start from the live host — see plan D12. Never re-read.</summary>
    public sealed record MirrorLiveView(
        ImmutableArray<PaneCameraSnapshot> Panes,
        LevelDisplayMode DisplayMode) : CameraScript;
}

public readonly record struct PaneCameraSnapshot(MapLevelId LevelId, ViewportTransform Transform,
    bool ManualOverride);

public enum ExportPhase { Preparing, Seeking, Rendering, Finalizing, Completed, Cancelled, Failed }

public readonly record struct ExportProgress(
    ExportPhase Phase,
    int FramesDone,
    int FramesTotal,
    double FramesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Eta,
    string? Detail);

/// <summary>A request the session refuses before rendering (odd dims, GIF caps, empty range, …).</summary>
public sealed class ExportValidationException(string message) : InvalidOperationException(message);

// (design §5.7 verbatim, plus the static validator the dialog and CLI both call)
public sealed class SceneExportSession
{
    public SceneExportSession(SceneCompositor compositor);

    public Task RunAsync(ExportRequest req, ISceneFrameSource src, IFrameSink sink,
        IRenderSurfaceProvider surfaces,
        IProgress<ExportProgress> progress, CancellationToken ct);

    /// <summary>Throws <see cref="ExportValidationException"/>; called by the dialog AND the CLI.</summary>
    public static void Validate(ExportRequest req);

    /// <summary>The fps values a format supports (GIF: 10/20/25/50 — see plan D7).</summary>
    public static IReadOnlyList<int> SupportedFps(string formatId);
}
```

### Core — `DemoViewer.NET.Playback2D.Core.Hud`

```csharp
/// <summary>Pure function of tick → HUD state. Deterministic; no wall clock (design §5.1).</summary>
public interface IHudDataSource
{
    HudSnapshot At(int tick);
}

/// <summary>Correction 4: HudSnapshot is a CORE type (IHudDataSource returns it and Core cannot
/// see Pipeline). KillFeedRow is B0's Core record — Pipeline must not redeclare it.</summary>
public readonly record struct HudSnapshot(
    int Tick, string RoundNumber, int TScore, int CtScore,
    double CountdownSeconds, bool BombTicking, bool DefuseInProgress, double DefuseSeconds,
    IReadOnlyList<KillFeedRow> KillRows);

public sealed class ClockLayer : ISceneLayer      // Id "hud.clock",    Slot Hud, Cache Dynamic
{
    public ClockLayer(IHudDataSource data, HudStyle? style = null);
}

public sealed class KillFeedLayer : ISceneLayer   // Id "hud.killfeed", Slot Hud, Cache Dynamic
{
    public KillFeedLayer(IHudDataSource data, HudStyle? style = null);
}

public sealed record HudStyle(float FontSizePx = 14f, float MarginPx = 12f, uint TextArgb = 0xFFF2F2F2u,
    uint PanelArgb = 0x99101010u);
```

### Pipeline — `DemoViewer.NET.Playback2D.Pipeline.Export`

```csharp
// CORRECTION 2: owned by C1 (it needs it a phase earlier); B4 CONSUMES this. The signature below
// is the canonical merge of both plans — whoever lands first writes exactly this.
public sealed class TrackerFrameSource : ISceneFrameSource, IDisposable
{
    /// <param name="frames">The immutable post-parse frame list. Read-only; shared safely.</param>
    /// <param name="builder">B0's frame builder, fed through Pipeline's TrackerSceneSnapshot adapter.</param>
    /// <param name="createTracker">Defaults to <c>() =&gt; new EntityTracker()</c>. NEVER MainViewModel.CreateTracker.</param>
    public TrackerFrameSource(IReadOnlyList<DemoFrame> frames, SceneFrameBuilder builder,
        int startFrame, int endFrame, int fps, double speed, int tickRate,
        Func<EntityTracker>? createTracker = null, bool throwOnNonSequentialAccess = false);

    public int FrameCount { get; }
    public int StartFrame { get; }

    /// <summary>The one-time from-zero replay to <see cref="StartFrame"/> (plan D2). Blocking; call off the UI thread.</summary>
    public void Prepare(CancellationToken ct);

    public SceneTime TimeAt(int frameIndex);      // frameIndex is source-relative (0-based)
    public Scene2DFrame FrameAt(int frameIndex);  // sequential O(1); rewind re-seeds
    public int DemoFrameIndexOf(int frameIndex);
    public void Dispose();

    /// <summary>Binary search over ServerTick; -1 when the tick is outside the demo.</summary>
    public static int FrameIndexForTick(IReadOnlyList<DemoFrame> frames, int serverTick);
}

public sealed class FfmpegFrameSink : IFrameSink
{
    public FfmpegFrameSink(FfmpegSinkOptions options);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct);
    public ValueTask DisposeAsync();
}

public sealed record FfmpegSinkOptions(
    string OutputPath,
    string FormatId,
    int Width,
    int Height,
    int Fps,
    string? BinaryFolder = null,      // from FfmpegLocator; null = rely on PATH
    int Crf = 30,                     // VP9 default; 20 for H.264
    string H264Preset = "medium",
    bool DeletePartialOnCancel = true,
    Action<string>? Log = null);

public sealed class ManagedGifSink : IFrameSink
{
    public ManagedGifSink(string outputPath, int fps, int maxFrames = 1800);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct);
    public ValueTask DisposeAsync();
}

/// <summary>Determinism-test decorator (plan D13): hashes each RGBA frame, then forwards.</summary>
public sealed class HashingFrameSink : IFrameSink
{
    public HashingFrameSink(IFrameSink? inner = null);
    public IReadOnlyList<string> FrameHashes { get; }
    public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct);
    public ValueTask DisposeAsync();
}
```

### Pipeline — `DemoViewer.NET.Playback2D.Pipeline.Ffmpeg`

```csharp
public enum FfmpegOrigin { None, SystemPath, Managed }

public readonly record struct FfmpegLocation(bool Found, string? Directory, FfmpegOrigin Origin);

public static class FfmpegLocator
{
    /// <summary>PATH scan first (a user-chosen install always wins), then <paramref name="managedDirectory"/>. Never throws.</summary>
    public static FfmpegLocation Locate(string? managedDirectory);
}

public sealed record FfmpegDownloadOffer(
    string Url, string ArchiveSha256, string ReleaseTag, string SourceUrl,
    string LicenseName /* "LGPL-2.1" */, long ApproxBytes, string TargetDirectory);

public static class FfmpegAcquisition
{
    /// <summary>Null on macOS/browser/unsupported RIDs (plan D9).</summary>
    public static FfmpegDownloadOffer? Offer(string targetDirectory);

    /// <param name="consent">Shown the offer plus the license text read from the archive; false aborts.</param>
    public static Task<FfmpegLocation> AcquireAsync(
        FfmpegDownloadOffer offer,
        Func<FfmpegDownloadOffer, string /*licenseText*/, CancellationToken, Task<bool>> consent,
        IProgress<double>? progress, HttpClient? http, CancellationToken ct);
}
```

### Pipeline — `DemoViewer.NET.Playback2D.Pipeline.Hud`

```csharp
// CORRECTION 3: KillFeedRow is NOT declared here. It is B0's Core record, already carried on
// Scene2DFrame.KillFeed:
//     public readonly record struct KillFeedRow(
//         int Tick, string Attacker, string? Assister, string Victim, string Weapon,
//         bool Headshot, bool Penetrated, bool NoScope, bool ThroughSmoke,
//         bool AttackerBlind, bool AttackerInAir, bool AssistedFlash);
// D5's retarget of Playback2DTabViewModel maps KillFeedEntry onto THESE member names, and the XAML
// bindings in Playback2DView.axaml are updated with it (Attacker/Victim/Headshot/…).

public static class KillFeedTimeline
{
    public const int DefaultWindowSeconds = 8;
    public const int DefaultMaxRows = 6;

    /// <summary>Rows with <c>Tick &gt; nowTick - windowSeconds*tickRate</c> AND <c>Tick &lt;= nowTick</c>,
    /// sorted by tick, most recent <paramref name="maxRows"/> kept. Inclusive upper bound is load-bearing:
    /// a kill AHEAD of the playhead must never appear when paused or seeking.</summary>
    public static void Window(IReadOnlyList<KillFeedRow> all, int nowTick, int tickRate,
        List<KillFeedRow> into, int windowSeconds = DefaultWindowSeconds, int maxRows = DefaultMaxRows);
}

// HudSnapshot is declared in Core.Hud (correction 4) — see above.

public sealed class TimelineHudDataSource : IHudDataSource
{
    public TimelineHudDataSource(IReadOnlyList<KillFeedRow> allKills, int tickRate,
        Func<int, (string Round, int T, int Ct, double Countdown, bool BombTicking,
                   bool Defusing, double DefuseSeconds)> clockAt);
    public HudSnapshot At(int tick);
}
```

### App — `DemoViewer.NET.Services.Export`

```csharp
public interface IExportJobService
{
    ExportJobStatus Status { get; }
    event EventHandler<ExportJobStatus>? StatusChanged;

    /// <exception cref="ExportRefusedException">A LiveSync session or reel job is active.</exception>
    /// <exception cref="InvalidOperationException">An export is already running.</exception>
    void Start(Scene2DExportRequest request);

    Task CancelAsync();
}

/// <summary>The App-level hand-off: the Core request plus the App-only bits (output path, source demo).</summary>
public sealed record Scene2DExportRequest(
    ExportRequest Core, string OutputPath, string DemoPath, bool AllowFfmpegDownload);

public readonly record struct ExportJobStatus(
    ExportPhase Phase, int FramesDone, int FramesTotal, double FramesPerSecond,
    TimeSpan Elapsed, string? OutputPath, string? Error)
{
    public bool IsRunning => Phase is ExportPhase.Preparing or ExportPhase.Seeking
                                   or ExportPhase.Rendering or ExportPhase.Finalizing;
}

public sealed class ExportRefusedException(string message) : InvalidOperationException(message);
```

### App — `HeavyJobGate` additions (modified type)

```csharp
public sealed class HeavyJobGate : IDisposable
{
    // ... existing members unchanged ...

    /// <summary>True while a 2D video export owns the machine's spare CPU (background parses pause).</summary>
    public bool IsExportActive { get; }

    /// <summary>
    ///     Marks a 2D-export session. Pauses BACKGROUND parses for its duration but never blocks an
    ///     INTERACTIVE demo load (an export is CPU-bound, not multi-GB-RAM-bound — see plan D10).
    /// </summary>
    /// <exception cref="ReelInProgressException">A reel session is active.</exception>
    public Task<IDisposable> EnterExportSessionAsync(CancellationToken cancellationToken = default);

    // EnterReelSessionAsync now additionally throws:
}

/// <summary>A reel was refused because a 2D video export is rendering. The message is user-facing copy.</summary>
public sealed class ExportInProgressException() : InvalidOperationException(
    "A 2D video export is rendering — try again when it finishes.");
```

### Consumers of the above

| API | Consumed by |
|---|---|
| `ExportRequest`, `CameraScript`, `SceneExportSession`, `IFrameSink`, `ISceneFrameSource` | C1 (`dv2d export`), future highlight generator |
| `TrackerFrameSource`, `FfmpegFrameSink`, `ManagedGifSink` | C1, App `ExportJobService` |
| `FfmpegLocator`, `FfmpegAcquisition` | C1, App export dialog, App `FfmpegDependency` shim |
| `KillFeedRow`, `KillFeedTimeline`, `IHudDataSource` | B1/B2's `Playback2DTabViewModel`, C1 |
| `ClockLayer`, `KillFeedLayer` | `SceneExportSession`, C1 `dv2d render --hud` |
| `IExportJobService` | The export dialog, the status chip |
| `HeavyJobGate.EnterExportSessionAsync` / `IsExportActive` / `ExportInProgressException` | `ExportJobService`, `ReelJobService` |

---

## Test plan

Two execution modes, per design §11:

- **Direct-execution** (no Avalonia platform, no window, no dispatcher, no demo parse) — the default.
  These live in `src/Playback2D/DemoViewer.NET.Playback2D.Tests` and `…Core.Tests`.
- **Headless-Avalonia** (`HeadlessSession`) — only for tests that genuinely need the App: the dialog
  VM, the kill-feed XAML snapshot comparison, the gate/refusal wiring. These live in
  `src/App/DemoViewer.NET.App.Tests`.

Fixtures: `tests/fixtures/playback2d/` (B0's `SceneFixture` JSON corpus) plus one small demo resolved
through `DemoTestHelper.FindDemoPath()` (skips via `SkipTestException` when absent). Goldens for this
phase live in `tests/fixtures/playback2d/goldens/export/`.

### Core.Tests (direct execution)

| Class | Cases |
|---|---|
| `ExportRequestValidationTests` | odd width/height rejected for webm+mp4, accepted for gif (D8); `EndFrame < StartFrame` rejected; unsupported fps per format rejected (D7); GIF frame cap rejected; `SupportedFps("gif")` == `[10,20,25,50]` |
| `SceneExportSessionLoopTests` | frame count written == `EndFrame - StartFrame + 1`; buffers are `width*height*4`; `RenderPurpose.Export` reaches every layer; `LayerIds` filtering (empty = all enabled, `hud.*` off unless listed) |
| `SceneExportSessionCancellationTests` | cancel mid-run → `OperationCanceledException`, sink disposed exactly once, terminal progress `Phase == Cancelled`; cancel before first frame; cancel token already-cancelled |
| `SceneExportSessionProgressTests` | monotonic `FramesDone`; `FramesTotal` constant; final report `Completed` with `FramesDone == FramesTotal`; `Eta` null until ≥ 2 frames |
| `CameraScriptResolverTests` | `Fixed` re-fits with `WithViewport` (world framing preserved across a 700→1920 px change); `FollowPlayer` holds last transform on an unresolvable SteamId; `MirrorLiveView` ignores post-capture mutation of the source pane list (D12) |
| `ExportDeterminismTests` | two `RunAsync` calls over the same fixture + request produce identical `HashingFrameSink.FrameHashes` (D13); a `SceneTime.DeltaSeconds` change produces *different* hashes (the test's own negative control) |
| `HudLayerGoldenTests` | `ClockLayer` + `KillFeedLayer` over a fixed `HudSnapshot` on `CpuSurfaceProvider` vs `goldens/export/hud-clock.png`, `hud-killfeed-6rows.png`; zero-row and bomb-ticking variants |
| `ExportAllocationTests` | `GC.GetAllocatedBytesForCurrentThread()` across a 512-frame headless export (null sink) is zero after warm-up (§6 contract) |

### Pipeline.Tests (direct execution)

| Class | Cases |
|---|---|
| `TrackerFrameSourceTests` | `Prepare` seeds to `StartFrame` (assert `tracker.CurrentFrameIndex`); sequential `FrameAt` costs one `AdvanceOneFrame` (assert via a counting tracker factory); repeat `FrameAt(cursor)` is cached; `throwOnNonSequentialAccess: true` throws on a rewind; **the private tracker is never the app's** (reference-inequality against a supplied `PlaybackController`-held tracker). Needs a demo → `SkipTestException` when absent. |
| `TrackerFrameSourceIsolationTests` | running a `TrackerFrameSource` concurrently with a second tracker over the same frame list yields identical entity state at the same index (D1's concurrency claim, made executable) |
| `KillFeedTimelineTests` | ported verbatim from the current `Playback2DKillFeedTests` window cases + a kill exactly at `nowTick` is included, a kill at `nowTick + 1` is not, a kill at exactly `lowTick` is not; > 6 kills keeps the newest 6 in tick order |
| `FfmpegArgumentTests` | the built argument strings for webm/mp4/gif contain `-f rawvideo`, `-pix_fmt rgba`, `-video_size`, `-framerate`, `-an`, and the per-format codec flags; **no** `-i` twice for gif (D6); no `GlobalFFOptions` mutation |
| `ChannelVideoFrameSourceTests` | backpressure (writer blocks at capacity 4); every rented buffer is returned to the pool; completing the channel ends the enumerator; disposal after a faulted encoder does not deadlock |
| `ManagedGifSinkTests` | produces a decodable GIF with N frames and centisecond delays matching D7; refuses > `maxFrames`; refuses a non-D7 fps |
| `FfmpegFrameSinkIntegrationTests` | `[Category("Integration")]`; skips via `SkipTestException` when `FfmpegLocator.Locate` finds nothing; encodes 30 synthetic frames to webm, asserts the file exists, is > 1 KB, and `ffprobe` reports the expected frame count/size |
| `FfmpegAcquisitionTests` | offer is null on macOS; SHA-256 mismatch aborts and leaves no file; declined consent leaves no file; success extracts both binaries and re-locates (all against an injected `HttpMessageHandler` serving a fixture zip — **no network**) |
| `ExportSeamHeadlessTests` | the B4.16 no-App-types smoke: fixture → `SceneExportSession` → `ManagedGifSink` → a GIF on disk, asserting the test assembly loads no `Avalonia.*` assembly (architecture assertion) |

### App.Tests (headless-Avalonia where noted)

| Class | Mode | Cases |
|---|---|---|
| `HeavyJobGateTests` (extend existing) | direct | export session pauses background but not interactive; reel refused while export active (`ExportInProgressException`); export refused while reel active (`ReelInProgressException`); `IsExportActive` clears on dispose; double dispose is safe |
| `ExportJobServiceTests` | direct | refuses while `IsSessionActive`; refuses while `OwnsSessionResources`; refuses a second concurrent `Start`; terminal status publishes only after the gate is released; `CancelAsync` before the first frame completes cleanly; a LiveSync session starting mid-export does **not** abort it (D11) |
| `Playback2DExportDialogTests` | direct (pure VM) | `CanStart` false without an output path / with an invalid range / while a job runs / while ffmpeg is missing **and** format ≠ gif; GIF stays available with no ffmpeg; format switch re-lists fps per `SupportedFps`; custom size snaps to even; `MirrorLiveView` capture happens on Start, not on selection |
| `Playback2DExportHudSnapshotTests` | **headless** | at N sampled ticks over a real demo, `KillFeedTimeline.Window(...)` (what `KillFeedLayer` draws) equals `Playback2DTabViewModel.KillFeed` row-for-row; and the clock fields equal `GameInfo`'s (`RoundNumber`, `TScore`, `CtScore`, `RoundSeconds`, `BombTicking`). This is design §11's "export HUD rows vs XAML HUD data" snapshot test |
| `Playback2DExportFeatureGateTests` | direct | `playback2d.export` cascades off with `tab.playback2d`; forced off when `OperatingSystem.IsBrowser()`; the id string is exactly `"playback2d.export"` (persisted-key lock, mirroring the existing id-lock tests) |
| `Playback2DExportRoundTripTests` | **headless**, `[Category("Integration")]` | the exit-criterion test: export one round at 1920×1080 to webm from a real demo, assert wall-clock elapsed ≤ frame count / 64 s (≥ realtime on CPU) and the file decodes. Skips without a demo or ffmpeg |

### Commands

```bash
# Direct-execution suites (fast; these are what CI runs)
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests     -c Release
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release

# One class
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release -- \
  --treenode-filter "/*/*/TrackerFrameSourceTests/*"

# The App-side additions (batched runner — the App suite is OOM-prone as one process)
scripts/test-app-suite.sh -c Release -n 3
# or, targeted:
dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- \
  --treenode-filter "/*/*/(ExportJobServiceTests|Playback2DExportDialogTests|Playback2DExportHudSnapshotTests|HeavyJobGateTests)/*"

# Integration lanes (need ffmpeg and/or a demo; they skip cleanly without)
DEMO_PATH=/path/to/demo.dem dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release -- \
  --treenode-filter "/*/*/Playback2DExportRoundTripTests/*"
```

---

## Build & wiring

### `Directory.Packages.props` additions

Insert next to the existing encoding/tooling entries, with the comments (this repo documents *why*
every pin exists):

```xml
<!-- 2D-playback video export (docs/playback2d-v2/design.md §5.7). FFMpegCore is MIT and only
     BUILDS ARGUMENTS and pipes rawvideo to an ffmpeg SUBPROCESS — no ffmpeg code is linked, which
     is what keeps the GPL/LGPL posture clean (FSF "separate programs"). We never ship an ffmpeg
     binary; see THIRD-PARTY-NOTICES.md §d. Xabe.FFmpeg is CC BY-NC-SA and must never be added. -->
<PackageVersion Include="FFMpegCore" Version="5.2.0"/>
<!-- The no-ffmpeg GIF floor (ManagedGifSink). Six Labors Split License 1.0: Apache-2.0 terms for
     open-source projects, which this repository is (MIT, see LICENSE). A downstream closed-source
     redistribution would need a commercial Six Labors license — record that before any such change. -->
<PackageVersion Include="SixLabors.ImageSharp" Version="3.1.12"/>
```

**Version policy.** This repo pins exact versions, never floating ranges, and documents the reason
inline. The two versions above are the newest stable at plan time; the implementer **must** confirm
them at implementation start (`dotnet package search FFMpegCore`,
`dotnet package search SixLabors.ImageSharp`) and pin whatever the newest stable
`FFMpegCore` 5.x / `SixLabors.ImageSharp` 3.1.x is then, recording the resolved version in the
commit message. Do **not** move ImageSharp to a 4.x major without re-reading its license terms.
`TreatWarningsAsErrors=true` + `WarningsAsErrors` including `NU1608;NU1605` means a transitive
version conflict fails the build — resolve it by pinning, never by `NoWarn`.

SkiaSharp itself is **B0's** addition, not B4's.

### New/modified project files

B4 creates **no new projects** if B0 created `…Core`, `…Pipeline`, `…Core.Tests` and
`…Pipeline.Tests`. If `…Pipeline.Tests` does not exist yet, create it as:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <LangVersion>latest</LangVersion>
        <!-- Distinct from the Pipeline assembly's own namespace to avoid a type collision. -->
        <RootNamespace>DemoViewer.NET.Playback2D.PipelineTests</RootNamespace>
        <!-- CA1707: test method names conventionally use underscores (Method_Condition_Expected). -->
        <NoWarn>$(NoWarn);CA1707</NoWarn>
    </PropertyGroup>

    <ItemGroup>
        <!-- Entity replay over a real demo churns multi-hundred-MB transients. -->
        <RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="5"/>
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="TUnit"/>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\DemoViewer.NET.Playback2D.Pipeline\DemoViewer.NET.Playback2D.Pipeline.csproj"/>
        <ProjectReference Include="..\..\Testing\DemoViewer.NET.TestSupport\DemoViewer.NET.TestSupport.csproj"/>
    </ItemGroup>

</Project>
```

Deliberately **no** `Avalonia*` reference — that absence is what makes `ExportSeamHeadlessTests`'
architecture assertion meaningful.

### `DemoViewer.NET.slnx`

Add (or extend, if B0 created it) — the `/src/Playback2D/` folder block:

```xml
<Folder Name="/src/Playback2D/">
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Core/DemoViewer.NET.Playback2D.Core.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj"/>
    <Project Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Core.Tests.csproj"/>
    <Project
            Path="src/Playback2D/DemoViewer.NET.Playback2D.Tests/DemoViewer.NET.Playback2D.Pipeline.Tests.csproj"/>
</Folder>
```

### CI

`.github/workflows/ci.yml` — append one step after the existing build:

```yaml
      - name: Playback2D export suites (direct execution, no Avalonia platform)
        run: |
          dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
          dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c Release
```

Rationale to put in the step's comment: these are direct-execution suites (no demo parse, no Avalonia
platform, no headless session), so they are not the OOM-prone single-process shape the file's header
comment warns about. Integration-category tests skip on the runner (no demo, no ffmpeg) by design.

### Code style (enforced — `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`)

File-scoped namespaces; Allman braces, always braced; **explicit types, never `var`**; 4-space
indent; LF endings; 120-col soft limit; the `#region` / `using …` / `#endregion` header block before
the namespace; XML doc comments on every public member (`GenerateDocumentationFile=true`,
`CS1591` is *not* in `NoWarn` for new projects unless inherited).

---

## Dependencies

### Consumed from other phases — **B4 cannot start without these**

| From | API B4 needs | Note |
|---|---|---|
| **B0** | `Scene2DFrame` (immutable per-frame world state) | passed through `ISceneFrameSource` |
| **B0** | `SceneTime(int Tick, int FrameIndex, double DemoSeconds, double DeltaSeconds, bool IsDiscontinuity)`, `enum RenderPurpose { Interactive, Export, Thumbnail }` | design §5.1 verbatim |
| **B0** | `IRenderSurfaceProvider` + `CpuSurfaceProvider` — `RenderBackend Backend`, `SKSurface CreateSurface(SKSizeI)`, `void Flush(SKSurface)` | design §5.8 verbatim |
| **B0** | `ViewportTransform` (incl. `WithViewport`), `SliceCamera` (incl. `StepToward`, `ManualOverride`) moved verbatim into Core | needed by `CameraScript`/`PaneCameraSnapshot` |
| **B0/B1** | **`SceneFrameBuilder` must build a `Scene2DFrame` from an `EntityTracker` alone** — proposed: `Scene2DFrame Build(EntityTracker tracker, int frameIndex)`, doing the pawn↔slot join itself via `CS2DemoKit.Analysis.Plugins.PawnLookup` | **⚠ integrator action — see Risks R1.** The App's join lives in `ModuleContext` (`:266-330`) behind `IPlaybackSnapshot`, which is defined in `DemoViewer.NET.Modules.Abstractions` — a project that references **Avalonia**, so Pipeline cannot consume it. `PawnLookup` is in the `CS2DemoKit.Analysis` package, so Pipeline *can* do the join |
| **B1** | `ISceneLayer` (`Id`, `Slot`, `Order`, `Cache`, `IsEnabled`, `bool Advance(in SceneTime, Scene2DFrame)`, `void Render(SKCanvas, SceneRenderContext)`), `SceneCompositor`, `SceneRenderContext`, `LayerSlot`, `LayerCacheHint` | design §5.2 verbatim |
| **B1** | `MapSpace`, `MapLevel` (`Id` is quantized-`ZMin`-keyed), `LevelPane`, `LevelDisplayMode`, `ICameraRig`, `FollowPlayerRig` | design §5.3 |
| **B2** | `Playback2DSettings` on `AppSettings` — B4 nests `Export` inside it | either phase may create the class; see B4.12 |
| **B2** | `AnnotationLayer`'s layer id, so the dialog's layer toggles can list it | display only |
| **B3** | `LevelDisplayMode` values for the `MirrorLiveView` capture | display only |
| **package** | `CS2DemoKit.Parser.EntityTracking.{EntitySeekService, SeekResult, EntityTracker, EntitySet}`, `CS2DemoKit.Parser.DemoFrame` | already pinned at `0.10.0` |
| **package** | `CS2DemoKit.Analysis.Plugins.PawnLookup` | requires Pipeline → `CS2DemoKit.Analysis` reference |
| **existing App** | `HeavyJobGate` (modified by B4.10), `LiveSyncState.IsSessionActive`, `LiveSyncService.OwnsSessionResources`, `IReelJobService.Status.IsRunning`, `FeatureCatalog`, `SettingsService.WriteInMemory`, `FfmpegDependency.ManagedDirectory` | |

### Exported by B4 — who consumes them

- **C1 (`dv2d`)** consumes the entire Core `Export` namespace, `TrackerFrameSource`,
  `FfmpegFrameSink`, `ManagedGifSink`, `FfmpegLocator`, `FfmpegAcquisition`, `ClockLayer`,
  `KillFeedLayer` — `dv2d export` is `SceneExportSession` with argument parsing and nothing more.
- **C2** consumes `SceneExportSession.RunAsync`'s `IRenderSurfaceProvider` parameter unchanged
  (a `GpuSurfaceProvider` drops in with no B4 change) and the `ExportDeterminismTests` harness for
  its perceptual-diff lane.
- **B1/B2's `Playback2DTabViewModel`** consumes `KillFeedRow` + `KillFeedTimeline.Window`.
- **`ReelJobService`** (LiveSync project) newly observes `ExportInProgressException` from
  `HeavyJobGate.EnterReelSessionAsync` — its existing catch-all already surfaces it as a Failed
  status with the exception's user-facing message, so no change is strictly required there; add a
  one-line comment naming the new exception.
- **A future highlight generator** consumes `CameraScript` + `SceneExportSession` wholesale.

---

## Risks & spikes

| # | Risk | Mitigation | Time-box |
|---|---|---|---|
| **R1** | **`SceneFrameBuilder` ships App-coupled** (taking `IPlaybackSnapshot`), leaving Pipeline unable to build frames headlessly — which also breaks C1's exit criterion, not just B4's | Raise with the integrator **before B4.4**. Contingency: `TrackerFrameSource` gains an injected `Func<EntityTracker, int, Scene2DFrame>` so the App can pass its own builder; B4 ships, CLI export is deferred to C1 with a note. Do not silently duplicate the join in two places | 2 h to confirm with B0's author; 3 h to implement the contingency |
| **R2** | **FFMpegCore pull-vs-push impedance** (`RawVideoPipeSource` takes an `IEnumerator<IVideoFrame>`) mis-modelled → a deadlock or an unbounded frame queue | Spike `ChannelVideoFrameSource` first, standalone, against synthetic frames before wiring the compositor; assert backpressure and pool return in tests; a `DisposeAsync` timeout (30 s) turns a deadlock into a diagnosable failure | **4 h spike, before B4.7** |
| **R3** | 1080p CPU export misses ≥ realtime (the exit criterion). Vision-cone `Advance` is §6's biggest consumer | Measure early with `ExportAllocationTests`' harness at 1080p on the B1 layer stack; levers in order: (1) exclude `world.vision` from the export's default `LayerIds`, (2) run `Advance` for vision one frame ahead on a worker (the layer contract permits it), (3) accept 720p as the shipped default preset and document 1080p as GPU-preferred (C2) | 1 day of measurement mid-phase; escalate to the integrator if all three are needed |
| **R4** | ffmpeg absent on the target machine → export silently degrades or hard-fails | The three-rung ladder is the mitigation and is itself tested (`FfmpegAcquisitionTests`); the dialog never offers a format the located ffmpeg cannot produce; GIF always works | — |
| **R5** | BtbN pinned URL/asset naming drifts (they re-tag `-latest-` assets) | Pin the **immutable dated release tag**, not a `-latest-` asset, and verify SHA-256. A 404 or hash mismatch degrades to the GIF floor with a clear message, never a crash. Recheck the pin each release | 2 h at implementation; annual |
| **R6** | ImageSharp license posture questioned downstream | Recorded in `THIRD-PARTY-NOTICES.md` §c with the OSS-terms rationale; ImageSharp is used **only** by `ManagedGifSink`, so dropping it costs one class if the posture ever changes | — |
| **R7** | WASM head fails to publish because Pipeline now carries FFMpegCore/ImageSharp (both managed, but `System.Diagnostics.Process` is unsupported in browser) | Both are managed-only and restore fine for `net10.0`; nothing calls `Process` unless a sink is constructed, and `playback2d.export` is gated off on browser. Verify with an actual `dotnet publish` of the Browser head during B4.17; if trimming complains, the escape hatch is moving the two sinks to a `…Pipeline.Encoders` assembly the Browser head does not reference | **3 h verification, in B4.17** |
| **R8** | The `KillFeedEntry` → `KillFeedRow` retarget (D5) drags in more App surface than expected | Time-boxed; documented fallback (projection + snapshot test only) | **3 h**, then fall back |
| **R9** | `HeavyJobGate` change destabilises a well-tested, concurrency-sensitive type | The change is additive: one counter, one property, one method, two admission-test conjuncts. Existing `HeavyJobGateTests` must pass unmodified; new cases are additions | — |

---

## Acceptance checklist

**Maps 1:1 to the design's exit criterion** — *"1080p round export ≥ realtime on CPU; cancel-safe;
refuses under LiveSync"* — plus this plan's own additions.

Design exit criterion:

- [ ] **≥ realtime on CPU at 1080p — NOT MET at 60 fps; met at 30 fps and at 720p60.** Measured with
      `dv2d export --json` on `assets/tour/sample-de_nuke.dem`, WebM/VP9, the shipped layer set:
      **1280×720@60 → 109.8 fps (1.83×)**, **1920×1080@30 → 53.0 fps (1.77×)**,
      **1920×1080@60 → 58.4 fps (0.97×)**. R3's third lever is taken and the shipped default preset
      is 720p. The 1080p60 number is 2.7× what it was before deviation 8's radar fix (21.4 fps) and
      the remaining gap is one full-frame image composite — see deviations 8 and 9. **Open.**
- [x] **Cancel-safe.** `SceneExportSessionCancellationTests` (already-cancelled token, cancel
      mid-render, a sink that throws) and `ExportJobServiceTests` (cancel before the first frame).
      The sink is disposed exactly once on every path, which is what kills ffmpeg and removes the
      partial file; the gate is released before the terminal status is published. Verified in the
      review against a **real** ffmpeg subprocess: cancelling mid-encode leaves zero `ffmpeg`
      processes and no output file. **Cancel-safe was true; fail-safe was not** — see deviations
      21-23 for the deadlock, the mute failure and the `Completed`-on-a-broken-file report the
      review found and fixed, and for why the missing `FfmpegFrameSinkIntegrationTests` is what let
      all three through.
- [x] **Refuses under LiveSync.** `ExportJobService.Start` throws `ExportRefusedException`, re-checked
      after the gate is entered; a session starting mid-export does not abort it (D11), pinned by
      `ALiveSyncSessionStartingMidExport_DoesNotAbortIt`.
- [x] **Refuses under a reel job.** Both directions, pinned in `HeavyJobGateTests`.

Plan additions:

- [x] **D1 recorded and `MainViewModel.cs` untouched.** `git diff 477cbd4..HEAD -- …/MainViewModel.cs`
      is empty; design §12 item 1 and §5.7's parenthetical both carry the correction.
- [x] **Private tracker.** `TrackerFrameSource` is unchanged in this respect (C1 built it that way);
      the export builds its own compositor and its own surface too.
- [x] **Background thread.** The only `Dispatcher.UIThread` reference in the export path is
      `ExportJobService.SetStatus`.
- [x] **Determinism.** `ExportDeterminismTests`, on pre-encode RGBA hashes (D13). The negative control
      moves a marker rather than the timestep — see deviation 6.
- [x] **Zero steady-state allocation.** `ExportAllocationTests`: 512-frame and 1024-frame runs differ
      by **48 bytes total**, which is B1 deviation 14's characterised JIT-tiering artefact. A
      companion case reports the **701 bytes/frame** the level derivation costs when no map bundle is
      supplied — B1's, not B4's, and named as a carry-forward rather than swallowed.
- [x] **Sinks.** All three produce decodable files (`ManagedGifSinkTests`, `ExportSeamHeadlessTests`,
      and a real `dv2d export` to WebM verified with `ffprobe`: 154 frames, 640×360, 60/1).
      Re-verified independently at review: full-demo WebM/VP9 1280x720@60 (17998 frames, 299.97 s,
      3.63 MB), 1920x1080@60 (17998 frames, 4.81 MB), MP4/H.264 720p60 (3601 frames, 60.02 s),
      ffmpeg GIF (401 frames, 20.05 s) and the managed ImageSharp GIF with `PATH` stripped
      (201 frames, 10.05 s) all decode under `ffprobe` with the expected codec, size, rate and frame
      count, one stream and no audio.
      `FfmpegArgumentTests` pins `-an` on every format, the fully specified rawvideo input, one `-i`
      for GIF, and that `GlobalFFOptions` is never mutated.
- [x] **GIF floor.** Verified end to end on the real demo with `PATH` stripped of ffmpeg:
      51 frames, `encoder=imagesharp-gif`. CI runs the same command on a runner that has no ffmpeg.
- [x] **ffmpeg ladder.** `FfmpegAcquisitionTests` covers the null offer off Windows-x64, a checksum
      mismatch, declined consent, a 404, and a success that extracts both binaries and leaves no
      `*.part`. The pin is a dated release tag with the SHA-256 read from the GitHub release API.
- [x] **HUD layers.** Opt-in by name (`SceneExportSessionLoopTests`), drawn once per host rather than
      per band (`HudLayerTests`), and `Playback2DKillFeedTests.AtEverySampledTick_…` compares the
      exported rows against the XAML feed's at 36 sampled ticks. No HUD *goldens* — see deviation 7.
- [x] **Single kill-feed builder.** `KillFeedEntry` is deleted; the VM, the XAML `DataTemplate` and
      `KillFeedLayer` all read Core's `KillFeedRow`, windowed by the one `KillFeedTimeline.Window`.
      The R8 fallback was not needed.
- [x] **Camera scripts.** `CameraScriptResolverTests`, including "`MirrorLiveView` ignores later
      mutation of the live panes" and "`FollowPlayer` holds rather than snapping to the origin".
- [x] **Settings.** Seven flat `Export*` properties, all flattened into `WriteInMemory`. The
      exclusion comment the plan asked for is **not** there, deliberately: B5 D3 reversed that call
      and the keys are written like every other Playback2D key.
- [x] **Feature gate.** `Playback2DExportFeatureGateTests` locks the id, the parent, the position in
      the contiguous block, and its membership of `ShellModuleFeatureGate.DesktopOnlyIds`.
- [x] **Dialog is thin.** Every format/fps/size/range rule routes through
      `SceneExportSession.Validate`; `ExportSeamHeadlessTests` renders a fixture to a GIF and asserts
      no `Avalonia*` assembly is loaded in the process.
- [x] **Reachable in the app.** `Playback2DTabViewModel.OpenExportCommand` composes the runner, the
      job service and the dialog; the view carries an "Export video…" button and a side pane. Both are
      **hidden** unless the feature is on, a demo is loaded and the shell wired an export host — a
      button whose refusals silently did not apply would be worse than no button. See deviations 18–20
      for the seam, and 20 for the one thing still missing (a live pane snapshot for `MirrorLiveView`).
- [x] **Build & style.** `dotnet build DemoViewer.NET.slnx` clean, 0 warnings, 0 errors.
      **R7 verified with a caveat:** `dotnet publish src/App/DemoViewer.NET.Browser -c Release` fails
      — and fails **identically at the base commit** (`IL2104` trim warnings from
      `CS2DemoKit.Analysis` and `CS2DemoKit.Parser`, then `NETSDK1144`). Neither FFMpegCore nor
      ImageSharp appears anywhere in the diagnostics, and `dotnet build` of the Browser head is clean.
      The trimmed publish was already red; it is B5's `wasm-build` job to own.
- [x] **Notices.** §c gains FFMpegCore and ImageSharp with their terms stated; §e is the new
      "ffmpeg (not redistributed)" section.

---

## Implementation notes (deviations)

Written at implementation time. Everything not listed here was built as the plan and the
`Integrator corrections` block specify.

### Where things ended up

1. **`SceneExportSession` lives in `…Pipeline.Export`, not `…Core.Export`.** Registry §3.7 says there
   is **one** headless render entry point and never a second render path — and that entry point,
   `HeadlessSceneRenderer`, is Pipeline's. A Core session would have had to re-implement level
   derivation, pane reconciliation and the multi-pane submission, which is the second render path the
   registry forbids; it is also the only reason a two-floor Nuke export shows two bands. Everything
   §5.7 pins as a *contract* — `ISceneFrameSource`, `IFrameSink`, `ExportRequest`, `CameraScript`,
   `PaneCameraSnapshot`, `ExportProgress`, `ExportValidationException`, `CameraScriptResolver` — is
   in Core exactly as specified. Only the loop moved, and every type it takes and throws is Core's.

2. **`SceneLayerCatalog.CreateSceneStack` is a second entry point beside `Create`.** The seven real
   layers had to become buildable from Pipeline for `dv2d export` to draw anything (C1 deviation 6
   left that to B1). Adding them to `Create`'s registration table would change what `Create()` with
   no arguments returns — which is what `dv2d render` and every committed CPU golden are built on, so
   every golden in the corpus would have moved in a commit about video export. Two tables, one file,
   with the unification pointed at B1's eventual re-baseline PR.

3. **The managed ffmpeg download is Windows-x64 only, not "Windows + Linux" (D9).** BtbN publishes
   its Linux builds as `.tar.xz`; neither .NET nor this repository has an xz decoder, and taking a
   compression dependency to unpack a binary that every distribution already packages (`apt install
   ffmpeg`) is the wrong trade. Linux joins macOS on the instructions-plus-GIF-floor rung. The pin
   table is data, and a Linux row goes in the day an xz decoder earns its place.

### Additive API, agreed shapes unchanged

4. **`IPaneCameraPolicy` (Core.Cameras) + `HeadlessSceneRenderer.CameraPolicy`.** The generalisation
   of B1's `Camera` pin: an export needs a different transform per level, and a follow script needs to
   *step* them, and both must land inside the same `Advance` call — a camera written after the
   submission snapshot is one frame late, and one written before reconciliation is discarded by it.
   Null by default, so B1's own construction sites and `dv2d` are unchanged.

5. **Small additions to consumed types.** `IPreparableFrameSource` (Core.Export) so the session can
   report an `ExportPhase.Seeking` for a source that needs a from-zero replay, without Core knowing
   what a tracker is. `TrackerFrameSource.Radars` and `static OutputFrameCount` — the first so an
   exported frame carries the bundle's radar art, the second so the dialog sizes a range with the
   same arithmetic the source uses (a dialog that computed its own frame count would eventually
   disagree, and the disagreement would surface as a GIF cap that refuses one length and encodes
   another). `SceneExportSession.AuthoritativeFloors`/`RadarBinder`, bound exactly as `Scene2DHost`
   and `dv2d` bind them. `KillFeedRow.HasAssist`, because the XAML feed's assist chip binds it.
   `Scene2DExportRequest` carries the demo frame range explicitly rather than overloading
   `ExportRequest`'s source-relative indices with a second meaning.

### Test-plan deviations

6. **The determinism negative control moves a marker, not the timestep.** The plan asked for "a
   `SceneTime.DeltaSeconds` change produces different hashes". It does not, over a repeated static
   frame: the marker smoother settles after the first frame and then has nothing left to interpolate,
   so identical pixels are the *correct* answer and asserting otherwise would be asserting a bug. The
   control moves a marker instead, which proves the same thing — that the harness hashes content.

7. **No HUD golden PNGs.** `HudLayerGoldenTests` would pin the two layers against committed images at
   `tests/fixtures/playback2d/goldens/cpu/hud-*.png`. Instead `HudLayerTests` asserts the properties
   that would make such a golden meaningful — the layers draw, an empty feed draws nothing, a bomb
   countdown renders differently from a round clock, the HUD appears in exactly one band, and a row's
   text carries every modifier — and `KillFeedLayer.Format` is asserted directly. A committed
   picture of text is the most re-baseline-prone artefact in the corpus and B1's own text-metrics
   review is the reason the parity lane is tolerance-based; adding two more of them to gate a HUD
   whose *content* is already pinned against the XAML feed buys a maintenance cost, not coverage.

8. **`Playback2DExportRoundTripTests` (the headless-Avalonia integration lane) was not written.**
   Its assertion — a full round at 1080p, timed — is exactly what `dv2d export --json` reports, and
   the CLI can do it without an Avalonia platform, without the OOM-prone App suite, and while
   printing the number instead of hiding it in a pass/fail. The measurements in the checklist above
   come from it. CI runs the export end to end through the same command.

### The measurement, and the one thing it found

9. **The radar was five sixths of the frame, and `RadarLayer.CacheScaledImage` is the fix.**
   R3 predicted the vision solve would be the problem and listed three levers. Bisecting with
   `--no-encode` and `--layers` said otherwise: the encoder was never the bottleneck (21.6 exported
   fps at 1080p with *no encoder at all*), vision cost nothing measurable, and one layer cost
   everything. `RadarLayer` draws the baked radar as a single `DrawImage` at `SKFilterQuality.High`,
   and `LayerCacheHint.PerCamera` caches the **picture**, not its pixels — so replaying that picture
   re-ran a bicubic resample of a ~2 000 px bundle layer on every frame of the video. The same scene
   with no map bundle rendered at 143.7 fps.
   <br>`CacheScaledImage` resamples once per (image, on-screen size) and blits after. It is **off by
   default** and turned on only by `SceneExportSession`, restored on dispose: the cached path
   resamples into a whole-pixel intermediate rather than straight into a fractional rectangle, and
   B1's pre-v2 parity gate can see the difference — with it on globally, `GoldenParityTests` drops
   from 99.45 % to 98.70 % of pixels within ±8 and fails. An interactive frame has 8 ms of budget and
   does not need it; an export renders thousands back to back and does. **B1 should decide whether
   the cached path is simply better and re-baseline, in which case the flag disappears.**

10. **`SceneExportSession`'s `Stopwatch` moved into a named `ExportClock`, and
    `…Pipeline.Export` joined `BannedApiTests`' exemption.** An export's elapsed time, throughput and
    ETA are wall-clock quantities by definition and reach no layer, which is the same justification
    the benchmark harness has. The separate type exists so the reference is attributed to a class
    under the exempt namespace rather than to the compiler-generated state machine `RunAsync` becomes
    — a namespace rule stays a rule; a carve-out for a generated name does not.

11. **`ExportJobService` uses a direct `IProgress<T>`, not `Progress<T>`.** `Progress<T>` captures the
    `SynchronizationContext` of whoever constructed it, and the job is constructed on a thread-pool
    thread — so it posts to the pool, and a report queued mid-render can arrive **after** the terminal
    status and overwrite "Completed" with "Rendering", leaving the status claiming the export is still
    running forever. Found by `ALiveSyncSessionStartingMidExport_DoesNotAbortIt`. Marshalling to the
    UI thread is `SetStatus`'s job and happens once, at the end of the chain, where ordering is fixed.

12. **`FfmpegAcquisition.AcquireAsync` returns the managed location, not a fresh `Locate()`.** The
    caller asked for a download and got one; `Locate` would hand back whatever is on `PATH`, which is
    a different binary, possibly under a different licence, and not the thing whose checksum was just
    verified.

13. **The dialog's `BuildRequest` takes an optional camera.** Validation runs on every keystroke, and
    it used to capture the live view each time — which would have made D12's "captured once, at
    Start" false. Validation now passes a placeholder (the camera cannot make a request invalid) and
    only `Start` captures. Pinned by `TheLiveCamera_IsCapturedOnStart_NotOnSelection`.

14. **FFMpegCore spells two flags differently, and the tests assert what is emitted.** `-r`/`-s` for
    the input rate and size rather than `-framerate`/`-video_size` (aliases, same meaning), and
    `-movflags faststart` without the `+` (which only matters when combining several). Transport is
    FFMpegCore's named pipe rather than literal stdin — same subprocess, same separateness, and it is
    what the library supports.

### The composition root, and the file it could not touch

18. **The export host reaches the 2D tab through `ModuleContext.SetExportHost`, not through
    `IModuleContext`.** An export needs the parsed frame list, and `IModuleContext`'s opening
    doc-comment is explicit that it "deliberately does NOT expose the live `EntityTracker`, the raw
    byte buffer, the `DemoParser`, or any mutator — a module simply has no API to corrupt state (the
    primary, real guardrail)". Handing every module the frame list to give one module a video export
    is a bad trade. `Playback2DExportHost` is a first-party capability the shell hands one tab, the
    same shape as `SetLiveSyncHud` and `SetSpeedLock`, and it carries the three things the tab cannot
    otherwise see: the frames, the `HeavyJobGate`, and the "is something else using the machine"
    predicates.
    <br>It is wired in `App.axaml.cs` — B4.11's stated site — through `MainViewModel.ModuleContext`,
    which was **already public**, so `MainViewModel.cs` stayed untouched as D1 requires. The only
    other addition is `PlaybackController.Frames`, a read-only accessor over a list that is immutable
    post-parse.

19. **The live-sync predicate is `State.IsSessionActive`, not
    `IsSessionActive || OwnsSessionResources` (D11).** `LiveSyncService.OwnsSessionResources` is
    `internal` to the desktop-only LiveSync project, which the App project cannot reference — the
    reel job can use it only because it *is* that project. The narrower predicate refuses every case
    a user can create deliberately; the gap is a *faulted* session still holding the gRPC host for
    fast retry, which costs a port rather than the CPU an export is competing for. `ExportJobService`
    takes the predicate as a delegate, so a future host that can see both flags passes both with no
    change here.

20. **`CameraScript.MirrorLiveView` is not yet reachable from the app.** The dialog captures a camera
    on Start exactly as D12 requires, and `CameraScriptResolver` implements all three cases with the
    capture-immutability case tested — but the capture is an empty `Fixed` script today, because
    there is no live pane list to read: `Scene2DHost` owns its `PaneSet` privately and exposes no
    snapshot. Every exported pane therefore keeps the fit its own level was born with, which is the
    correct framing for a whole round and the wrong one for a user who had zoomed in. **The one-line
    fix is a pane snapshot accessor on `Scene2DHost` (B1/B3's file), and the resolver already
    consumes it.**

### Not built, and why

15. **No `Playback2DExportHudSnapshotTests` class.** The test it names lives in
    `Playback2DKillFeedTests` instead, next to the feed cases it is about, because D5 made it a
    comparison between two consumers of one function rather than between two implementations.

16. **`dv2d export` does not offer the managed download.** A headless tool prompting for consent to
    fetch a 147 MB binary is not a thing a CI step can answer. It uses `PATH` or the GIF floor.

17. **The GPU ≥2× realtime criterion transferred from C2 is NOT closed.** It cannot be: C2's
    `GpuSurfaceProvider` is not in this build (its Stages 1–2 are deferred), and
    `SceneExportSession.RunAsync` takes the provider as a parameter precisely so one drops in with no
    change here. `dv2d export --no-encode` is the measurement to compare against when it lands —
    62.6 exported fps at 1080p on `CpuRaster` today, render only. **Open, and owned by C2.**

---

### Found by the independent review, fixed there (2026-08-25)

21. **A failing ffmpeg deadlocked the export forever — R2's predicted failure mode, shipped.**
    `dv2d export … --out /a/directory/that/does/not/exist/out.webm` never returned; it was still
    blocked after ten minutes, with ffmpeg long gone. The cause: `ChannelVideoFrameSource` is drained
    only by FFMpegCore's pump task, so once ffmpeg exits nothing reads the queue again; the queue
    fills at four frames and `FfmpegFrameSink.WriteAsync` parks on
    `_channel.Writer.WriteAsync(frame, ct)` with a token nobody will cancel. The
    `_encoder.IsFaulted` check that was meant to catch exactly this sits **after** that write, so it
    is unreachable once the queue is full, and `DisposeAsync`'s 30 s timeout never runs because
    disposal is never reached. `ChannelVideoFrameSource.Fault` existed from the first commit and had
    **no caller** — it was written for this and never wired.
    <br>**Fix:** a continuation on the encoder task ends the frame stream when the encoder ends —
    `Fault` on a faulted encoder, `Complete` on one that exited early, nothing on a cancelled one
    (its token is the caller's own, and faulting would race a clean `OperationCanceledException`
    into a `ChannelClosedException`). A parked writer is released at once and rethrows the encoder's
    failure. Pinned by `ExportFailureTests.AnFfmpegThatCannotOpenItsOutput_FailsTheWrites_…`
    (`[Category("Integration")]`, skips with no ffmpeg) and
    `ChannelVideoFrameSourceFaultTests.FaultingTheChannel_ReleasesAWriterAlreadyBlocked…`. The same
    CLI invocation now exits 3 in about a second. **This is also the gap that let it ship:** the plan's
    `FfmpegFrameSinkIntegrationTests` was never written and never recorded as a deviation, so the only
    sink that runs a subprocess had `DescribeArguments` coverage and no execution coverage at all.

22. **The failure said "Pipe is broken", which is true and useless.** The named pipe breaks before
    FFMpegCore observes the process exit, so the raw fault beats ffmpeg's own explanation to the
    caller. The sink already received the stderr lines through `NotifyOnError` and threw them away.
    `FfmpegFrameSink` now keeps the last six and wraps an encoder failure in `FfmpegEncodeException`
    carrying them, so the CLI prints `ffmpeg failed: Error opening output …: No such file or
    directory` instead. Asserted in the same test.

23. **`SceneExportSession` could report `Completed` on an export that produced nothing playable, and
    could report no terminal phase at all.** Disposal of the sink happened inside the `finally`, so a
    throw from it escaped **before** `Report(terminal, …)` — a caller driving a progress bar off
    `ExportProgress.Phase` saw `Rendering` as the last word on a failed export. And because muxing
    happens on close, "every frame was written" is not "a file exists that decodes": a sink that
    failed only at finalisation had already been reported as progressing normally. Disposal is now
    its own step after the `finally`: its failure becomes the run's failure (`failure ??= ex`),
    exactly one terminal report is made on every path, and the original exception is rethrown through
    `ExceptionDispatchInfo` so its stack survives. Pinned by
    `AThrowingDisposal_StillReportsATerminalPhase` and
    `ASinkThatFailsOnlyWhenClosed_FailsTheRun_AndNeverReportsCompleted`.

24. **`ExportJobService`'s single-flight had a window a double-click fits through.** `Start` guarded
    on `Status.IsRunning`, but the job body runs on the thread pool, so `Status` is still `Idle` for a
    moment after `Start` returns. Two `Start` calls with no await between them both got through: two
    exports to one output path, with the first job's `_cts` overwritten and its `_job` unreachable by
    `CancelAsync`. A `_running` latch is now set inside the same lock that starts the task and cleared
    after the terminal status is published. Pinned by
    `ExportJobServiceTests.ASecondStart_InTheWindowBeforeTheFirstJobPublishesAnything_IsStillRefused`;
    the existing `ASecondStart_WhileOneIsRunning_IsRefused` waits for the runner to enter and so never
    touched the window.

25. **`dv2d export`'s usage text advertised four options that do not exist and omitted three that
    do.** `--round N`, `--camera …`, `--ffmpeg <path>` and `--progress` were all in the help and all
    rejected by the parser with `unknown option` (exit 1) — documented invocations that cannot run —
    while `--hud`, `--no-encode` and `--ffmpeg-log`, which are real and were used to produce the
    measurements in this plan, were unmentioned. The block now matches the parser, and
    `ProgramDispatchTests.EveryOptionTheExportUsageAdvertises_IsAnOptionExportAccepts` runs every
    `--name` token in the export block through the parser so it cannot drift again. **Note for a
    follow-up:** `--camera` and `--round` are worth *implementing* rather than only un-documenting —
    a headless export is stuck with the default fit today, and `CameraScriptResolver` already
    supports `FollowPlayer`.

26. **Merge-integration (B4 → `feature/playback2d-v2`): `SceneExportSession` now refuses a non-CPU
    surface provider, and `dv2d export`'s backend chain ends at `force-cpu` rather than `auto`.**
    B4 was built at `477cbd4`, where `GpuSurfaceProvider` did not exist and export could only ever be
    handed a `CpuSurfaceProvider`. C2 Stage 0 had merged into the branch by the time B4 landed
    (`2475f93`), so `BackendResolver`'s auto-probe now finds ANGLE — and `SceneExportSession.RunAsync`
    awaits its sink between frames, resuming on whatever pool thread the continuation lands on, while
    `GpuSurfaceProvider` is bound to the thread that created its EGL context. The result on any
    developer machine with a GPU was that CI's own export step died part-way through with
    *"GpuSurfaceProvider is thread-affine: it was created on thread 2 and was used from thread 33"* —
    a true statement about an internal invariant, arriving after the replay, that a user can do
    nothing with. Making it work means pinning the render loop to one thread, which is a redesign of
    `RunAsync` and is the same work C2 Stage 1 needs for the ≥2× throughput number; it is emphatically
    not an integration change. So: the session refuses any `Backend != CpuRaster` up front with an
    `ExportValidationException` naming the backend (this covers the app path, which reaches the
    session without going through the CLI), `ExportCommand` re-raises the same refusal as
    `BackendUnavailableException` so it lands on exit 6 — the "requested environment unavailable"
    channel `--layout single` already uses for a real-but-not-in-this-build feature — and the CLI's
    fallback preference becomes `ForceCpu`, precisely as the golden lane already pins it, so the
    *default* invocation is never an auto-probe into a guaranteed refusal. Pinned by
    `ANonCpuSurfaceProvider_IsRefused_BeforeAnythingIsRendered` (with a `MislabelledBackendProvider`
    fake, so the refusal is testable on a runner with no GPU),
    `ExportLane_DefaultsToCpu_EvenOnAGpuMachine` and
    `ExportOnAnExplicitGpu_ExitsSix_RatherThanFailingMidRun`. Recorded in `dv2d.md`'s limitations
    table and owned by C2 Stage 1.

27. **Merge-integration: neither export sink created its output directory, so CI's own export step
    failed on a clean checkout.** The workflow B4 added writes to
    `artifacts/playback2d-export/ci-smoke.gif`, and that directory does not exist in a fresh clone.
    ffmpeg does not create it (`Error opening output …: No such file or directory`) and neither does
    ImageSharp's `Image.Save(path)` (`DirectoryNotFoundException`) — and because a GIF is written and
    a container is muxed only at *close*, both refusals arrive after the entire range has been
    replayed and drawn. `Pipeline/Export/ExportOutputPath.EnsureDirectory` is now called from both
    sinks' constructors, so a path that cannot be prepared fails before the first frame instead of
    after the last. This shifted the premise of review commit `fbcb4a7`'s R2 regression test, which
    used a missing directory to make ffmpeg exit immediately: it now deletes the directory *after*
    constructing the sink, which reproduces the same early exit and documents that the constructor's
    courtesy is not a write-time guarantee. New coverage:
    `AnOutputDirectoryThatDoesNotExistYet_IsCreated_NotDiscoveredAtTheEnd`.

28. **Merge-integration: `SceneExportSessionCancellationTests` collected progress reports in a
    `List<T>` that `Progress<T>` appended to from the thread pool.** With no synchronization context
    under the test runner every callback is posted to the pool, so a report still in flight landed
    while the assertions enumerated the list — `InvalidOperationException: Collection was modified`,
    reproducing on 2 of 6 consecutive suite runs. This is the test-side twin of the `Progress<T>`
    defect deviation 23 fixed in `ExportJobService`; the collection is now a `ConcurrentQueue<T>`
    snapshotted with `ToArray()` before iteration. Six consecutive green runs after the change.

29. **Merge-integration: `THIRD-PARTY-NOTICES.md` section letters were assigned in landing order, as
    §4.3 directs.** The registry sketched §e as FFMpegCore/ImageSharp/ffmpeg and §f as ANGLE, but C2
    and B2 landed first, so the file reads §d Inter, §e ANGLE, §f perfect-freehand, and B4's ffmpeg
    notice became **§g**. The two cross-references that named it were updated: §c's "See §e" and the
    `FFMpegCore` comment in `Directory.Packages.props`. C2's own ANGLE notice pointed at "§d" (the
    Inter font) for its licence text; corrected to §e in the same pass.

### Post-merge defects (found after landing on `feature/playback2d-v2`)

30. **Post-merge: the two HUD layers inherited a bad text measurement, and `ClockLayer`'s panel is
    now derived rather than fudged.** `ShapedText.Width`/`Height` were `SKTextBlob.Bounds`, which
    Skia computes conservatively from the font's global glyph box rather than from the run — see
    `B1-compositor-port.md` deviation 29 for the full account. The scoreboard panel was therefore
    ~37 px wider and ~3 px taller than its content and the text inside it sat visibly left of centre,
    and each kill-feed row's panel overhung its text on the left by roughly 1.5 em. Neither layer's
    *code* was wrong: `Width` now means the advance and `Height` one line box, which is what both were
    already asking for, so the drawing calls are unchanged. The one deliberate change here is
    `ClockLayer`'s panel height, rewritten from `score + countdown + MarginPx * 2.2f` to
    `padY + score + gap + countdown + padY` — the lump constant left 7.2 px of padding above the text
    and 13.2 px below it, and a panel whose height is not derived from the rows it wraps will drift
    again the next time a font size moves.

    *(Merge note: this deviation and the two below were written in parallel worktrees — package A's
    text-metrics fix and package B's export fixes — and both claimed the number 30. A's kept it;
    B's were renumbered 31 and 32 when the two branches were integrated.)*

31. **The HUD clock was never wired to a demo's game rules in EITHER front end — D4's "pure function
    of tick" was satisfied by two functions that were not of the tick.** `SceneFrameBuilder` has
    always read the round off `CCSGameRulesProxy.m_pGameRules.m_totalRoundsPlayed` and the scores off
    the two `CCSTeam.m_iScore` entities, and `ClockLayer` has always drawn whatever its data source
    answered. The join was the defect, twice over:

    - `dv2d export --hud` built `new TimelineHudDataSource([], tickRate, static _ =>
      ClockReading.Unknown)` — a **constant**. Every frame of every CLI export, at any point in any
      match, read `Round —  T 0 : 0 CT`. The comment above it called this "the frame's own GameInfo,
      projected once per tick", which was simply not what the code did.
    - `Playback2DTabViewModel.BuildExportHud` closed over the tab's own `_frame` — the **live
      viewport's** frame. The video therefore carried the scoreboard as it stood when Start was
      pressed, on every frame; and because the closure was live rather than a copy, resuming playback
      while the export rendered made the burnt-in round drift with the viewport instead of with the
      video. `IncludeHud` defaults on, so this shipped in the default export.

    The fix gives the export's own frame source the answer: `TrackerFrameSource.LastGameInfo`, stamped
    at the end of `FrameAt`, read by both front ends' clock delegates. The ordering is safe by
    construction — `SceneExportSession.RunAsync` is strictly `TimeAt` → `FrameAt` → `Advance` →
    `Render` per output frame and `ClockLayer` asks during `Advance`, so the last frame built is
    always the frame being drawn. `ExportSceneSetup.Hud` became
    `Func<TrackerFrameSource, IHudDataSource>?` for the same reason: a *value* on that record can only
    be built from state the tab has, and the tab does not have the export's frame.

    **Why the suite missed it.** `Playback2DKillFeedTests.TheExportedClock_ProjectsTheSameGameInfoTheXamlPanelShows`
    asserts `ClockReading.From` against a hand-built `SceneGameInfo` and, separately, that the VM
    publishes a `GameInfo` — both true, neither executing the closure that joins them. Deviation 7's
    reasoning (no HUD golden PNGs; pin the *content* instead) is still right, but the content was
    pinned one level below the bug. The new cases execute the production delegates:
    `ExportHudClockTests` (CLI, `ExportCommand.BuildHud` made `internal` so the test cannot rebuild a
    look-alike that would have passed against the constant) and `Playback2DExportHudSourceTests`
    (App, asserting the reading is the SOURCE's while the live viewport is pushed forward underneath
    it). Both fail on the pre-fix closures.

    **Still open:** the CLI's kill feed. `TimelineHudDataSource` gets `[]` for its rows, because kill
    rows come from a parsed event timeline the App builds off `AllGameEvents` and `dv2d` has no
    equivalent. `--hud` on the CLI now draws a **true clock over an empty feed**; the code comment and
    `dv2d.md`'s limitations table say so rather than claiming the whole HUD is a layout check.

32. **Every export was framed by `WorldBounds.Default`, the ±3000 placeholder.** `PaneSet.Reconcile`
    fits a newly appeared level to the extent it is handed, and on frame one that extent is whatever
    the frame carries before anything has been read — the placeholder. Nothing in the export path
    ever re-framed it afterwards: `CameraScriptResolver` holds transforms keyed by `MapLevelId` and
    the *default* script is an empty `Fixed` in both front ends (the CLI never had a `--camera` for
    `export`, and the App falls back to empty without a mounted v2 surface), `SceneExportSession` sets
    `AdvanceCameras = false` so no rig steps, and `FitAll` was never called. The live window has the
    step this was missing — `Scene2DHost`'s one-shot "auto-fit once real positions exist" — and the
    headless renderer simply did not.

    de_nuke exports looked plausible and hid it: two stacked bands halve each pane's height, which
    halves the fit scale, which happened to land near the map. A one-band map does not get that
    coincidence — a 1280×720 de_inferno export was clipped off three edges of the frame.

    `HeadlessSceneRenderer.AutoFitOnFirstMapBounds` is the offscreen twin of the host's fit:
    **opt-in** (a golden's and a `dv2d render`'s camera is data, and a fit would silently re-baseline
    the corpus), set only by `SceneExportSession`, and applied immediately after `Panes.Reconcile` and
    **before** the `Camera` pin and `CameraPolicy.Apply` — so a pinned camera or "mirror the live
    view" still has the last word on the same frame. The birth extent passed to `Reconcile` also
    became `NetworkedBounds ?? ObservedBounds`, so a level born mid-export (a player taking the lift
    on Nuke) is fitted to the map rather than to how far the players have wandered. New coverage:
    `ExportInitialFitTests`, including the negative control (flag off → the placeholder framing
    survives, which is the shipped bug) and the explicit-camera-wins case. Every committed golden is
    unchanged: they pin `Camera`, which is applied after the fit.

    **Which half does the work, established by reverting each one separately against a real export**
    (`--from 60000 --to 62000`, 1280×720, de_inferno). Both changes are load-bearing, on disjoint
    cases, and neither is redundant:

    - Reverting *only* `AutoFitOnFirstMapBounds` left a mid-range export **correctly framed**. On a
      range that starts mid-match the tracker is seeded past the point where `CCSGameRulesProxy`
      published `m_vMinimapMins/Maxs`, so output frame 0 already carries `NetworkedBounds` and the
      panes are *born* fitted to it inside `Reconcile`. The birth-extent change alone fixes that case.
    - Reverting the birth extent as well (back to `ObservedBounds`) reproduced the shipped bug in
      full: `ObservedBounds` at output frame 0 is a tight box round wherever the ten players happen to
      be standing, so the map was blown up and spilled off all four edges.
    - The flag is what covers the other case — a range that *starts* at frame 0, where no extent
      exists yet. The pane is already born on the placeholder before any frame is read, and
      `Reconcile` never re-frames a surviving pane, so only `FitAll` can move it. Measured on a
      whole-demo export: frame 0 is placeholder-framed, frame 1 onward is the fitted framing, and it
      never moves again (content box `x 168..468, y 6..319` at 640×360, identical from frame 1 to
      frame 300). `dv2d.md`'s `export` section now says this out loud, because "the first frame of a
      whole-demo export is composed differently from the rest" is a thing a caller can see.
