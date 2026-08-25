# Phase C1 — `dv2d` CLI

**Track:** C (headless/CLI/GPU, parallel with B2–B4) · **Branch:** `feature/playback2d-v2`
**Design:** `docs/playback2d-v2/design.md` (§4, §5.7, §5.8, §6, §7.7, §9, §11)

This plan is self-contained: a coding agent that has not read the design doc can execute it top to
bottom. Where it restates the design it says so; where the design was silent, the call is recorded
under **Decisions made**.

> ## Integrator corrections (BINDING — supersede anything below that disagrees)
>
> Cross-phase reconciliation; `plans/00-overview.md` §3 is the canonical registry. All four
> "Conflicts for the integrator" items are resolved here.
>
> 1. **Project location resolved: `src/Playback2D/DemoViewer.NET.Playback2D.{Core,Pipeline}`**, slnx
>    folder `/src/Playback2D/` — **not** `src/Visualization/`. Fix the `ProjectReference` path in the
>    csproj and every `src/Visualization` mention. Core/Pipeline tests live in the single project
>    `src/Playback2D/DemoViewer.NET.Playback2D.Tests`; C1 still owns its own
>    `tools/DemoViewer.NET.Playback2D.Cli.Tests`.
> 2. **Ownership conflict 1 resolved, three ways.**
>    (a) **`TrackerFrameSource` — C1 owns it**, in `Pipeline/Frames/`, and B4 consumes it. Use the
>    canonical merged signature (a public constructor `(frames, builder, startFrame, endFrame, fps,
>    speed, tickRate, createTracker = null, throwOnNonSequentialAccess = false)` +
>    `Prepare(CancellationToken)` + `TimeAt`/`FrameAt`/`DemoFrameIndexOf` +
>    `static FrameIndexForTick`), not the `static Create(...)` factory sketched below — B4's export
>    session needs the fps/speed/tickRate that shape `SceneTime`, and a background `Prepare` phase.
>    (b) **`HeadlessSceneRenderer` — C1 owns it**, but it is a *facade over Core's `SceneRenderer`*
>    (B0's `SceneRenderer.Render(compositor, frame, time, ctx, size)` + `WritePng`), not a second
>    render implementation. B1's `ScenePipelineBenchmark` renders through it.
>    (c) **The golden comparator — B0 owns `GoldenImageComparer`, `GoldenTolerance`,
>    `GoldenComparison`** (Pipeline `Goldens/`, to the signatures below, which stand). B0 authors the
>    corpus and needs a comparator on day one; C1 owns `GoldenCorpus`/`GoldenCorpusEntry`/
>    `GoldenBudget`, the manifest schema, and the `golden` command on top. C2 extends the *same*
>    comparer with SSIM and does **not** add `ImageComparison`/`ImageDiffOptions`/`ImageDiffResult`
>    to TestSupport — `GoldenTolerance` grows the fields it needs
>    (`OutlierChannelDelta`, `MaxAlphaDelta`, `MinMeanSsim`, `MinWindowSsim`) and
>    `GoldenTolerance.CrossBackend` is the alias for C2's `DefaultPerceptual` numbers.
> 3. **Conflict 2 (font determinism) accepted as a requirement on B1** — Core resolves typefaces
>    from an embedded font asset, never `SKTypeface.Default`. It is recorded in B1's corrections; the
>    T6 spike still runs, and `--tolerance perceptual` remains the documented fallback for the CI
>    lane if B1 slips.
> 4. **Conflict 3 (SkiaSharp) resolved: B0 owns the `SkiaSharp 2.88.9` pin and the policy comment,
>    and also `SkiaSharp.NativeAssets.Linux`.** C1 adds only `SkiaSharp.NativeAssets.Win32` and
>    `…macOS` (the CLI has no Avalonia to bring them). Do not re-declare the other two.
> 5. **`SceneFixture`'s real shape** (B0 owns it, extended for C1): `SchemaVersion`, `Frame`,
>    **`SceneTime Time`**, **`ViewportTransform Camera`** (not `CameraScript?` — that is B4's type
>    and a fixture must not depend on it), **`SKSizeI Size`**, **`string? MapName`**,
>    **`string? MapVersion`**, **`JsonElement? Annotations`** (not `AnnotationDocument?` — the
>    fixture stays serializer-only; B2's store deserializes it), `SourceDemoId`, `Notes`, plus
>    `static Load(string)` / `Save(string)` over `SceneFixtureSerializer`.
> 6. **`SceneFrameBuilder` keeps `Build(in SceneFrameInput)`.** B0's `Modules.Abstractions.Ui` split
>    makes that signature legal in a headless process; the tracker→snapshot adaptation is B4's
>    Pipeline-side `TrackerSceneSnapshot` (`PawnLookup`-based), which `TrackerFrameSource` calls.
>    C1 does **not** require an `(EntityTracker, DemoFrame)` overload.
> 7. **Bench harness names are B1's** — `ScenePipelineBenchmark`, `BenchmarkRequest`,
>    `BenchmarkReport`, `FrameTimeStats`, `BudgetPolicy`. C1's `SceneBenchHarness`/
>    `SceneBenchRequest`/`SceneBenchResult` names are withdrawn; `dv2d bench` wraps B1's types and
>    keeps its own JSON shape. If B1 has not landed, C1 implements *those* types in Pipeline.
> 8. **Corpus layout below is canonical for the whole track** — B0, B1, B2, B3, B4 and C2 all write
>    into `tests/fixtures/playback2d/{scenes,goldens/cpu,goldens/gpu,annotations}` +
>    `manifest.json`. There is no `tests/goldens/`, no `…/golden/`, no `…/goldens/export/`.
>    Canonical entry names: `synthetic-empty`, `synthetic-tenplayers`, `synthetic-utility`,
>    `fitmap-mirage-eco`, `duel-mirage-b`, `mirage-single-level`, `nuke-multilevel`,
>    `nuke-multilevel-noradar`, `nuke-single-upper`, `bomb-planted-inferno`, `annotated-mirage-b`,
>    `full-scene-budget`. C1 seeds the manifest; each phase adds the entries it authors.
> 9. **CI: extend B0's `playback2d-tests` job, do not add a parallel one.** C1's `golden verify` and
>    `bench --gate` steps join that job (its `libfontconfig1` apt step already exists); the CLI
>    unit/architecture tests get their own step in the same job. C2 adds the separate
>    `render-backends` matrix, B5 adds `wasm-build`.

---

## Scope & exit criterion

Quoting the design's phase table (§9, row C1) verbatim:

> | **C (headless/CLI/GPU — parallel with B2–B4)** | C1 | `dv2d` tool: `render` (single frame → PNG from demo or fixture), `export` (CLI front-end to the session), `bench` promoted from harness to command; fixture library for design iteration | A designer/dev renders any tick to PNG in <1 s without launching the app; CI uses `dv2d` for goldens + budgets | 1 wk |

Supporting design text that constrains this phase:

- §4: *"`tools/DemoViewer.NET.Playback2D.Cli` (`dv2d`) — references Pipeline. Headless rendering,
  export, and benchmarking from the command line; no UI window ever."*
- §5.8: *"`dv2d render` gives a sub-second edit-render-look loop… `dv2d bench` gives CI-enforceable
  frame-time numbers on both backends"*; *"The probe result is overridable everywhere it matters:
  `dv2d --gpu | --cpu`."*
- §6: *"`dv2d bench --demo <fixture> --frames 2000 [--gpu|--cpu]` … reports frame-time p50/p95/p99
  and allocated bytes/frame. CI gates on p99 ≤ budget."*
- §7.7: *"Feature gates govern the app; the CLI takes explicit flags instead (a headless tool
  shouldn't read UI feature state)."*
- §11: *"`SceneFixture` files … live under `tests/fixtures/playback2d/`. `dv2d render --fixture
  duel-mirage-b.json --out /tmp/f.png` re-renders in well under a second … The same fixtures are the
  golden-test corpus."*
- §5.7: *"The CLI has no such constraint — it owns its whole process"* (no `HeavyJobGate`, no
  LiveSync refusal in the CLI).

**Non-goals for C1:** the GPU backend itself (C2 owns `GpuSurfaceProvider`; C1 only ships the
`--gpu`/`--cpu` flags and routes them to the provider factory), any Avalonia code, any feature-gate
reads, video-encoder implementation (B4 owns the sinks; C1 is a front-end).

---

## Decisions made

Ambiguities the design left open, resolved here. These are binding for C1 unless a later phase
review overturns them.

1. **Assembly/binary name is `dv2d`.** Project directory and root namespace stay
   `DemoViewer.NET.Playback2D.Cli` (repo convention), but `<AssemblyName>dv2d</AssemblyName>` so the
   produced executable is the `dv2d` the design refers to everywhere.
2. **No external CLI library.** The repo has no `System.CommandLine` / `CommandLineParser` entry in
   `Directory.Packages.props`, and both existing tools hand-roll parsing
   (`tools/DemoViewer.NET.DemoTrimmer/Program.cs:219-228` `StringOption`/`IntOption`;
   `tools/AnalysisBench/Program.cs:31-38` flags/`--key=value`/positional split). C1 ships one small
   internal `CliArgs` type that supports **both** repo styles (`--name value` and `--name=value`)
   and is unit-tested — no new dependency.
3. **`TrackerFrameSource` is owned by C1, not B4.** `render --demo` and `bench --demo` need a demo →
   `Scene2DFrame` source before B4 exists, and it is the same type B4's export path needs (design §4
   lists it under Pipeline, §9 lists "seek-core extraction" under B4). C1 implements it in Pipeline;
   B4 consumes it unchanged. Recorded as a cross-phase conflict for the integrator.
4. **`HeadlessSceneRenderer` facade is owned by C1** (Pipeline), and B1's bench harness is refactored
   onto it. If B1 lands an equivalent facade first, C1 adopts B1's type and drops this contract —
   the CLI must not carry a second render entry point.
5. **Golden-image comparison lives in Pipeline, owned by C1** (`GoldenImageComparer`,
   `GoldenCorpus`), so the CLI *and* B0/B1's direct-execution golden tests share one comparator.
   §11 requires "CI uses `dv2d` for goldens"; a comparator inside a test assembly cannot be called
   from a tool.
6. **Map assets reach the headless renderer through `--assets <dir>`**, pointing at the **baked
   `assets/` root that `tools/DemoViewer.NET.AssetBaker` writes** (`AssetBaker/Program.cs:37` bakes
   into `<parent-of-cs2-assets>/assets`, one subdirectory per map holding `bundle.json` + radar
   PNGs — e.g. the committed `assets/de_mirage/{bundle.json,de_mirage.png}`). Resolution order:
   `--assets` → `DV2D_ASSETS` env var → walk-up probe for `assets/` from `AppContext.BaseDirectory`
   (same shape as `MapAssetBundleReader.FindBundleDirectory`, used by
   `src/App/DemoViewer.NET/Modules/Playback2D/MapAssetLoader.cs:78`). `--no-radar` renders geometry
   only. **A fixture records the map's `bundle.json` `mapVersion` (CRC32 hex) it was captured
   against**; `golden verify` fails with exit 4 on mismatch rather than silently diffing against
   re-baked radar art.
7. **Exit codes** (design silent): `0` success · `1` usage/argument error · `2` required input
   missing (demo, fixture, assets, ffmpeg) · `3` runtime failure (decode/render/encode threw) ·
   `4` **gate failure** (golden mismatch or budget exceeded — the only code CI treats as "the change
   is bad" rather than "the run is broken") · `5` cancelled (Ctrl+C) · `6` requested environment
   unavailable (`--gpu` with a failed probe, when `--strict-backend` is set).
8. **`--json` puts a single JSON object on stdout and moves every human line to stderr.** Progress
   during `export` is newline-delimited JSON on **stderr**. Schemas are `snake_case` with a
   `schema_version` field, mirroring `bench-reports/*.json` and `tests/fixtures/*.golden.json`.
9. **Budgets are per-fixture data, not CLI constants.** `manifest.json` in the corpus carries a
   `budget` block per fixture seeded from §6 (render p99 ≤ 8 ms, advance p99 ≤ 2 ms, 0 bytes/frame);
   `--budget-scale` (env `DV2D_BUDGET_SCALE`) multiplies the time budgets so a slow CI runner can
   gate without re-writing the design's numbers. The ubuntu CI lane starts at `2.0` with a TODO to
   tighten once a baseline exists. The allocation budget is **never** scaled (0 is 0).
10. **CI fixture corpus must be demo-free.** No `.dem` is committed (`demos/` is gitignored except
    `demos/benchmarks/*.leetify.json`), so every CI-run fixture is a serialized `SceneFixture` JSON
    plus committed `assets/<map>/` art. `--demo` paths in CI are forbidden; `dv2d` prints a warning
    and CI never passes them.
11. **`--layers` takes stable layer ids** (`ISceneLayer.Id`, §5.2), comma-separated; `--exclude-layers`
    subtracts. Default = every layer the compositor registers with `IsEnabled = true`, i.e. the CLI
    never consults `FeatureCatalog`/`FeatureGate` (§7.7). An unknown layer id is exit 1, not a silent
    no-op — a typo in a CI golden invocation must fail loudly.

---

## Ordered work breakdown

Each task is ≤ ~half a day. "Blocked on" names the phase whose API must exist first; tasks with no
block can start immediately.

| # | Task | Blocked on | Files |
|---|---|---|---|
| T0 | Project skeleton + build wiring | — | new csproj, slnx, CPM |
| T1 | `CliArgs` + dispatch + usage + exit codes + `--json` discipline | T0 | Cli |
| T2 | Asset-root resolution (`--assets`/env/walk-up/`--no-radar`) | T0 | Cli |
| T3 | `HeadlessSceneRenderer` + `dv2d render --fixture` | B0, T1, T2 | Pipeline, Cli |
| T4 | `TrackerFrameSource` + `dv2d render --demo --tick/--frame` | B0, T3 | Pipeline, Cli |
| T5 | Fixture corpus layout + `dv2d fixture capture/list/verify` + seed fixtures | T4 | Cli, tests/fixtures |
| T6 | `GoldenImageComparer` + `GoldenCorpus` (+ font-determinism spike) | T3 | Pipeline |
| T7 | `dv2d golden verify \| update` + diff artifacts | T5, T6 | Cli |
| T8 | `dv2d bench` (promote B1 harness) + percentiles + allocation + `--gate` + report JSON | B1, T3 | Cli |
| T9 | Test project + **no-Avalonia architecture tests** + determinism test | T3 | Cli.Tests |
| T10 | CI job (`goldens` + `bench --gate`), `scripts/dv2d.sh` | T7, T8, T9 | .github, scripts |
| T11 | `dv2d export` front-end (formats, cancel, progress) | **B4**, T4 | Cli |
| T12 | `docs/playback2d-v2/dv2d.md` reference + corpus README + `--help` parity test | T11 | docs |

**Ordering constraints**

- T0 → T1 → everything. T3 is the spine: T6, T8, T9 all need a working single-frame render.
- T11 is the only task blocked on another *track* phase (B4). If B4 slips, C1 ships T0–T10 and T12
  and `dv2d export` prints `error: export requires the B4 export session` with exit 6. Do **not**
  stub a second encoder path in the CLI.
- T8 is blocked on B1's bench harness only for the *measurement loop*; if B1 has not landed,
  implement the loop against `HeadlessSceneRenderer` directly and hand it to B1 to absorb (see
  Dependencies).
- T9's architecture tests must run in CI from the first CI change (T10), not later.

### T0 — Project skeleton + build wiring (0.5 d)

Create `tools/DemoViewer.NET.Playback2D.Cli/DemoViewer.NET.Playback2D.Cli.csproj` (contents in
**Build & wiring**), a `GlobalUsings.cs` (repo pattern: `#region` / `global using` / `#endregion`),
and a `Program.cs` that prints usage and returns 1 for empty args. Add the project to
`DemoViewer.NET.slnx` under `/tools/`. Add the SkiaSharp package entries to
`Directory.Packages.props`. Acceptance: `dotnet build tools/DemoViewer.NET.Playback2D.Cli -c Release`
green with `TreatWarningsAsErrors=true`, and `dotnet run --project … --` prints usage and exits 1.

### T1 — Argument parsing, dispatch, output discipline (0.5 d)

New files in the CLI project:

- `CliArgs.cs` — internal parser (contract below). Handles `--name value`, `--name=value`, bare
  flags, positional verbs/sub-verbs, `--` terminator, `--help`/`-h`, and **rejects unknown options**
  per command (exit 1) so CI typos fail.
- `ExitCode.cs` — the code table from Decision 7 as an `internal enum` + `ToInt()`.
- `ConsoleOut.cs` — `Info/Warn/Error` write to stderr when `--json` is on, stdout otherwise; `Json(obj)`
  writes the single stdout object with `JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = SnakeCaseLower }`.
- `Program.cs` — usage const (DemoTrimmer style, raw string literal) + `switch` over verb.

### T2 — Asset-root resolution (0.5 d)

`AssetsRootResolver.cs` in the CLI: implements Decision 6's ladder, returns the resolved root plus a
`Source` enum (`Flag`/`Env`/`Probe`/`None`) that every `--json` payload reports, so a golden failure
caused by a different assets root is diagnosable from CI logs alone. `--no-radar` short-circuits to
`None`. Missing/unreadable root with radar required → exit 2 with the probed paths listed.

### T3 — `HeadlessSceneRenderer` + `dv2d render --fixture` (0.5 d)

Add the Pipeline file
`…/DemoViewer.NET.Playback2D.Pipeline/Headless/HeadlessSceneRenderer.cs` (contract below): owns an
`IRenderSurfaceProvider`, a `SceneCompositor`, and an `ILevelLayoutPolicy`; renders one
`Scene2DFrame` at a given `SceneTime` into an `SKSurface` and returns an `SKImage`/encoded PNG.
CLI `RenderCommand.cs` wires flags → `SceneFixture.Load` → `MapAssetPipeline` → renderer → PNG file.
Acceptance: `dv2d render --fixture tests/fixtures/playback2d/scenes/duel-mirage-b.scene.json --out f.png`
produces a non-blank PNG of the requested size in **< 1 s wall clock** on a warm run (the design's
exit criterion) — measured and printed as `elapsed_ms`.

### T4 — `TrackerFrameSource` + `render --demo` (0.5 d)

`…/Pipeline/Frames/TrackerFrameSource.cs`. Construction follows the verified seek-core sequence:

1. Hold the immutable `IReadOnlyList<DemoFrame>` from `DemoParser.Parse(...).Frames` (read-only,
   safe to walk concurrently — `EntityTracker` is `sealed` with per-instance state; parallel
   independent trackers are an existing supported pattern).
2. Build a **private** tracker factory `() => new EntityTracker()` — deliberately *not* the app's
   `MainViewModel.CreateTracker`, which wires the interactive Tier-3 debugger.
3. `new EntitySeekService(factory).SeekToFrameNoSnapshot(startFrame, frames)` on a background thread
   to seed; retain `SeekResult.Tracker` privately, **never** publish it through
   `PlaybackController.PublishTracker` (§5.7: "Export never touches the shared app clock").
4. Step forward with `tracker.AdvanceOneFrame(frames[i])` (O(1)); read `tracker.CurrentEntities` /
   `CurrentTick` into `SceneFrameBuilder` → `Scene2DFrame`.

`--tick N` resolves to a frame index by **binary search** over `frames` (`ServerTick` is monotone);
`--frame N` is used as-is. Out-of-range → exit 1 with the demo's frame/tick span in the message.

### T5 — Fixture corpus + authoring helper (0.5 d)

Create `tests/fixtures/playback2d/` (layout below), `FixtureCommand.cs`, and seed **six** fixtures
covering the layer matrix: `fitmap-mirage-eco` (markers only), `duel-mirage-b` (markers + trails +
vision), `nuke-multilevel` (stacked layout, two levels, both radar images), `nuke-single-upper`
(single layout + level pick), `bomb-planted-inferno` (bomb + area effect + clock HUD),
`annotated-mirage-b` (annotation document with one static + one time-anchored stroke; the B2 schema
— add in B2 if annotations are not yet serializable, and keep the manifest entry marked
`"pending": true` so `golden verify` skips it rather than failing).

### T6 — Golden comparator + font-determinism spike (0.5 d)

`…/Pipeline/Goldens/{GoldenImageComparer.cs,GoldenCorpus.cs,GoldenTolerance.cs}`. Byte-exact mode
(CPU, the authoritative policy per §5.8) plus perceptual mode (per-channel delta + mismatched-pixel
fraction + SSIM) for C2's GPU lane. Produces a diff PNG (red-tinted mismatch mask over a desaturated
expected image).

**Spike, time-boxed 2 h (inside this task):** verify that a `SceneCompositor` text draw produces
byte-identical output on Windows and on the ubuntu CI image. If it does not (near-certain with
system font fallback), file the requirement on B1: **Core must resolve typefaces from an embedded
font asset, never `SKTypeface.Default`.** If B1 cannot land that before C1's CI job, ship the CI
golden lane with `--tolerance perceptual` and a tracking note; do not delete the text layers from
the corpus.

### T7 — `dv2d golden verify | update` (0.5 d)

`GoldenCommand.cs`: enumerates the corpus manifest, renders each entry, compares, writes diffs into
`--diff-dir` (default `artifacts/playback2d-goldens/`), exits 4 on any mismatch/missing golden.
`update` rewrites the PNGs and prints a summary intended for review in the PR diff. `--name` limits
to one fixture for the local loop.

### T8 — `dv2d bench` (0.5 d)

`BenchCommand.cs`: warmup N frames (default 128), then N measured frames (default 2000, per §6),
timing `Advance` and `Render` separately with `Stopwatch.GetTimestamp` **in the CLI, not in Core**
(Core bans wall-clock APIs, §5.1 — the harness measures from outside). Percentiles by sorting the
sample array (no allocation inside the loop). Allocation via
`GC.GetAllocatedBytesForCurrentThread()` deltas across the measured window plus
`GC.CollectionCount(0..2)`. `--gate` compares against the manifest budget × `--budget-scale` and
exits 4 with a per-violation list. `--report-dir` writes
`bench-reports/dv2d-<source-id>_<yyyyMMdd-HHmmss>.json` using the existing report's metadata/machine
block shape.

### T9 — Test project + architecture tests (0.5 d)

`tools/DemoViewer.NET.Playback2D.Cli.Tests/` (TUnit, direct execution — **no `HeadlessSession`**).
See **Test plan**. The no-Avalonia assertion is three-pronged: deps.json scan, subprocess loaded-
assembly dump, and the test project's own reference closure.

### T10 — CI wiring (0.5 d)

New `playback2d` job in `.github/workflows/ci.yml` (yaml below) + `scripts/dv2d.sh` convenience
wrapper. The existing `build` job is untouched.

### T11 — `dv2d export` (0.5 d, blocked on B4)

`ExportCommand.cs`: flags → `ExportRequest` → `SceneExportSession.RunAsync` with a
`TrackerFrameSource`, a sink chosen by `--format` (`FfmpegFrameSink` for webm/mp4,
`ManagedGifSink` for gif and as the no-ffmpeg floor), and the provider from `--cpu/--gpu`.
`Console.CancelKeyPress` → `CancellationTokenSource.Cancel()`, `await sink.DisposeAsync()` so ffmpeg
is killed, exit 5. `--ffmpeg <path>` overrides `FfmpegDependency.Locate()`; absent ffmpeg with a
non-gif format → exit 2 naming the download instruction (never auto-download in a CLI).

### T12 — Docs (0.5 d)

`docs/playback2d-v2/dv2d.md`: every command, every flag, the exit-code table, the JSON schemas, the
CI recipes, and the iteration loop ("edit layer → `dv2d render --fixture` → look"). Add
`tests/fixtures/playback2d/README.md` in the shape of `tests/fixtures/README.md` (what each file is,
how to refresh it, schema version). A test asserts the `Usage` string lists every implemented verb.

---

## Public API contracts

**Binding for other phases.** Signatures match the design's §5 sketches where given.

### Introduced in `DemoViewer.NET.Playback2D.Pipeline` (owned by C1)

```csharp
namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     One-shot headless render of a single <see cref="Scene2DFrame" />. The single render entry
///     point for every non-Avalonia consumer: dv2d render, dv2d bench, dv2d golden, export, tests.
/// </summary>
public sealed class HeadlessSceneRenderer : IDisposable
{
    public HeadlessSceneRenderer(
        IRenderSurfaceProvider surfaces,
        SceneCompositor compositor,
        ILevelLayoutPolicy layout,
        MapSpace? map);

    public RenderBackend Backend { get; }

    /// <summary>Advance + Render into a surface the caller owns (bench reuses one surface).</summary>
    public void RenderInto(SKSurface surface, Scene2DFrame frame, in SceneTime time,
        RenderPurpose purpose = RenderPurpose.Thumbnail);

    /// <summary>Advance + Render into a fresh provider surface; caller disposes the image.</summary>
    public SKImage Render(Scene2DFrame frame, in SceneTime time, SKSizeI size,
        RenderPurpose purpose = RenderPurpose.Thumbnail);

    /// <summary>Convenience: <see cref="Render" /> encoded as PNG.</summary>
    public byte[] RenderPng(Scene2DFrame frame, in SceneTime time, SKSizeI size);

    /// <summary>Separated timings for the bench command; both phases measured by the caller.</summary>
    public bool Advance(in SceneTime time, Scene2DFrame frame);
    public void Render(SKSurface surface, Scene2DFrame frame, RenderPurpose purpose);

    public void Dispose();
}
```

```csharp
namespace DemoViewer.NET.Playback2D.Pipeline.Frames;

/// <summary>
///     An <see cref="ISceneFrameSource" /> backed by a PRIVATE checkpoint-replay tracker over a
///     parsed demo. Never publishes its tracker to the app's PlaybackController (design §5.7).
/// </summary>
// Correction 2a: canonical merged shape — B4's export session consumes THIS.
public sealed class TrackerFrameSource : ISceneFrameSource, IDisposable
{
    /// <param name="frames">The immutable post-parse frame list; read-only, shared safely.</param>
    /// <param name="builder">Turns tracker state into a Scene2DFrame, via Pipeline's
    ///     TrackerSceneSnapshot adapter (B4) over SceneFrameBuilder.Build(in SceneFrameInput).</param>
    /// <param name="startFrame">Seeded via EntitySeekService.SeekToFrameNoSnapshot.</param>
    /// <param name="endFrame">Inclusive last frame index.</param>
    /// <param name="fps">Export/bench frame rate; with <paramref name="speed"/> it fixes
    ///     SceneTime.DeltaSeconds = speed / fps (design §5.1 determinism).</param>
    /// <param name="createTracker">Defaults to <c>() =&gt; new EntityTracker()</c>.
    ///     NEVER MainViewModel.CreateTracker (it wires the Tier-3 debugger + UI dispatch).</param>
    /// <param name="throwOnNonSequentialAccess">true in tests: turns a caller that makes the
    ///     session non-monotonic into a failure instead of a silent 100× re-seed.</param>
    public TrackerFrameSource(IReadOnlyList<DemoFrame> frames, SceneFrameBuilder builder,
        int startFrame, int endFrame, int fps, double speed, int tickRate,
        Func<EntityTracker>? createTracker = null, bool throwOnNonSequentialAccess = false);

    public int FrameCount { get; }
    public int StartFrame { get; }

    /// <summary>The one-time from-zero replay to StartFrame. Blocking; call off the UI thread.</summary>
    public void Prepare(CancellationToken ct);

    public SceneTime TimeAt(int frameIndex);      // frameIndex is source-relative (0-based)
    public Scene2DFrame FrameAt(int frameIndex);  // sequential access is O(1); rewind re-seeds
    public int DemoFrameIndexOf(int frameIndex);
    public void Dispose();

    /// <summary>Binary search over ServerTick; -1 when the tick is outside the demo.</summary>
    public static int FrameIndexForTick(IReadOnlyList<DemoFrame> frames, int serverTick);
}
```

```csharp
namespace DemoViewer.NET.Playback2D.Pipeline.Goldens;

public enum GoldenMode { ByteExact, Perceptual }

// Correction 2c: OWNED BY B0 (Pipeline/Goldens/), extended by C2 with the SSIM fields, so there is
// exactly one image comparator in the repo. TestSupport gets no ImageComparison type.
public readonly record struct GoldenTolerance(
    GoldenMode Mode,
    int MaxChannelDelta,            // Perceptual only; 0 for ByteExact
    double MaxMismatchedFraction,   // e.g. 0.002 = 0.2% of pixels may differ
    double MinSsim,                 // mean SSIM, e.g. 0.995
    int OutlierChannelDelta = 32,   // C2: no pixel may exceed this
    int MaxAlphaDelta = 2,          // C2
    double MinWindowSsim = 0.95)    // C2: worst 11x11 window
{
    public static readonly GoldenTolerance ByteExact;
    public static readonly GoldenTolerance DefaultPerceptual; // (Perceptual, 8, 0.005, 0.995, 32, 2, 0.95)
    /// <summary>Alias for DefaultPerceptual — the name C2's cross-backend parity lane uses.</summary>
    public static GoldenTolerance CrossBackend => DefaultPerceptual;
}

public readonly record struct GoldenComparison(
    bool Match, int MaxChannelDelta, double MismatchedFraction, double Ssim,
    int Width, int Height, string? FailureReason);

public static class GoldenImageComparer
{
    public static GoldenComparison Compare(
        ReadOnlySpan<byte> expectedPng, ReadOnlySpan<byte> actualPng, GoldenTolerance tolerance);

    /// <summary>Red-tinted mismatch mask over a desaturated expected image; null when Match.</summary>
    public static byte[]? CreateDiffPng(
        ReadOnlySpan<byte> expectedPng, ReadOnlySpan<byte> actualPng);
}

/// <summary>The tests/fixtures/playback2d corpus: manifest + scene + golden paths.</summary>
public sealed class GoldenCorpus
{
    public static GoldenCorpus Load(string corpusDirectory);
    public static string? FindDefaultCorpusDirectory();   // walk up for DemoViewer.NET.slnx
    public string Directory { get; }
    public int SchemaVersion { get; }
    public IReadOnlyList<GoldenCorpusEntry> Entries { get; }
    public GoldenCorpusEntry? Find(string name);
}

public sealed record GoldenCorpusEntry(
    string Name, string ScenePath, SKSizeI Size,
    string? MapName, string? MapVersion,
    IReadOnlyList<string>? Layers,
    GoldenBudget Budget, bool Pending)
{
    public string GoldenPath(RenderBackend backend);   // goldens/cpu/<name>@<w>x<h>.png
}

public readonly record struct GoldenBudget(
    double RenderP99Ms, double AdvanceP99Ms, long BytesPerFrame);
```

### Introduced in the CLI (internal; `InternalsVisibleTo` the test project)

```csharp
namespace DemoViewer.NET.Playback2D.Cli;

internal enum ExitCode
{
    Success = 0, Usage = 1, InputMissing = 2, RuntimeFailure = 3,
    GateFailure = 4, Cancelled = 5, EnvironmentUnavailable = 6
}

internal sealed class CliArgs
{
    /// <summary>Parses "--name value", "--name=value", bare flags and positional verbs.</summary>
    public static CliArgs Parse(string[] args);
    public IReadOnlyList<string> Positional { get; }
    public string? Verb { get; }            // Positional[0]
    public string? SubVerb { get; }         // Positional[1]
    public bool Flag(string name);          // consumes; unknown-option detection uses consumption
    public string? String(string name);
    public string Require(string name);     // throws CliUsageException
    public int Int(string name, int fallback);
    public double Double(string name, double fallback);
    public SKSizeI Size(string name, SKSizeI fallback);            // "1920x1080"
    public IReadOnlyList<string>? List(string name);               // comma-separated
    public void ThrowIfUnconsumed();                               // unknown option → Usage
}

internal sealed class CliUsageException : Exception { public CliUsageException(string message); }

internal static class Program { public static int Main(string[] args); }

internal static class RenderCommand  { public static int Run(CliArgs a); }
internal static class ExportCommand  { public static Task<int> RunAsync(CliArgs a, CancellationToken ct); }
internal static class BenchCommand   { public static int Run(CliArgs a); }
internal static class GoldenCommand  { public static int Run(CliArgs a); }
internal static class FixtureCommand { public static int Run(CliArgs a); }

internal sealed record AssetsRoot(string? Path, AssetsRootSource Source);
internal enum AssetsRootSource { Flag, Env, Probe, Disabled, NotFound }
internal static class AssetsRootResolver { public static AssetsRoot Resolve(CliArgs a); }
```

### Command surface (binding — CI scripts and docs depend on it)

```
dv2d render   --fixture <path> | --demo <path> (--tick N | --frame N)
              [--out <png>]              default: ./dv2d-render.png
              [--size WxH]               default: 1920x1080
              [--layers a,b] [--exclude-layers a,b]
              [--camera fit-map|fit-alive|follow:<steamId>|fixed:<x>,<y>,<zoom>]
              [--layout stacked|single] [--level <levelId>]
              [--assets <dir>] [--no-radar]
              [--cpu | --gpu | --backend <auto|cpu|gpu|angle|gl|force-gpu>]
              [--strict-backend]
              [--json] [--quiet] [--diag-assemblies]

dv2d export   --demo <path> (--from N --to N | --round N)
              [--out <file>] [--format webm|mp4|gif]   default: webm
              [--fps N]                  default: 60   (64 = tick-native)
              [--size WxH] [--speed X]
              [--layers ...] [--camera ...] [--assets <dir>]
              [--ffmpeg <path>] [--cpu | --gpu | --backend <name>] [--strict-backend]
              [--json] [--progress]

dv2d bench    (--fixture <path> | --name <corpusEntry> | --demo <path> [--from N])
              [--frames N]               default: 2000
              [--warmup N]               default: 128
              [--size WxH] [--layers ...] [--assets <dir>]
              [--cpu | --gpu | --backend <name>] [--strict-backend]
              [--gate] [--budget-scale X] [--budget-p99-ms X]
              [--budget-advance-p99-ms X] [--budget-bytes-per-frame N]
              [--report-dir <dir>] [--json]

dv2d golden   verify | update
              [--corpus <dir>] [--name <fixture>]
              [--cpu | --gpu | --backend <name>] [--strict-backend]
              [--tolerance byte-exact|perceptual] [--diff-dir <dir>] [--json]

dv2d fixture  capture --demo <path> (--tick N | --frame N) --name <id>
                      [--corpus <dir>] [--size WxH] [--camera ...]
                      [--annotations <path>] [--layers ...] [--json]
              list   [--corpus <dir>] [--json]
              verify [--corpus <dir>] [--json]       # schema round-trip, no rendering

dv2d probe    [--json] [--require-gpu] [--require-hardware] [--quiet]
              # C2-owned surface addition (C2.7, plans/C2-gpu-provider.md §6.4), landed at
              # the C2 merge. Reports the render-surface backend and why; exit 0 on a CPU
              # answer, exit 6 under --require-gpu / --require-hardware.
```

### JSON output schemas (stdout with `--json`; `schema_version: 1`)

```jsonc
// render
{"schema_version":1,"command":"render","ok":true,"out":"f.png","width":1920,"height":1080,
 "backend":"CpuRaster","assets_root":"/repo/assets","assets_source":"probe",
 "map":"de_mirage","map_version":"3f2a91c0","tick":72110,"frame_index":41902,
 "layers":["radar","trails","markers","vision"],"png_sha256":"…","elapsed_ms":412.7}

// bench
{"schema_version":1,"command":"bench","ok":true,"backend":"CpuRaster",
 "source":{"kind":"fixture","name":"duel-mirage-b"},
 "frames":2000,"warmup":128,"size":{"width":1920,"height":1080},
 "advance_ms":{"p50":0.31,"p95":0.62,"p99":0.88,"max":2.10},
 "render_ms":{"p50":3.4,"p95":5.9,"p99":7.2,"max":11.8},
 "frame_ms":{"p50":3.8,"p95":6.4,"p99":7.9,"mean":4.1},
 "allocated_bytes_per_frame":0,"gc":{"gen0":0,"gen1":0,"gen2":0},
 "budget":{"scale":1.0,"render_p99_ms":8.0,"advance_p99_ms":2.0,"bytes_per_frame":0},
 "gate":{"enabled":true,"passed":true,"violations":[]},
 "metadata":{"timestamp":"…","git_commit":"…","machine":{"os":"…","architecture":"X64",
   "cpu":"…","logical_cores":8,"ram_bytes":0,"dotnet_version":".NET 10.0.0"}}}

// golden verify
{"schema_version":1,"command":"golden","action":"verify","ok":false,"backend":"CpuRaster",
 "tolerance":{"mode":"byte-exact"},
 "counts":{"total":6,"matched":5,"mismatched":1,"missing":0,"skipped":0},
 "results":[{"name":"nuke-multilevel","status":"mismatch","mismatched_fraction":0.013,
   "max_channel_delta":37,"ssim":0.981,
   "golden":"tests/fixtures/playback2d/goldens/cpu/nuke-multilevel@1920x1080.png",
   "actual":"artifacts/playback2d-goldens/nuke-multilevel.actual.png",
   "diff":"artifacts/playback2d-goldens/nuke-multilevel.diff.png"}]}

// export progress (stderr, newline-delimited)
{"schema_version":1,"event":"progress","frames_done":312,"frames_total":1920,"fps":71.4}
// export summary (stdout)
{"schema_version":1,"command":"export","ok":true,"out":"round7.webm","format":"webm",
 "frames":1920,"fps":60,"width":1920,"height":1080,"backend":"CpuRaster",
 "encode_fps":71.4,"realtime_factor":1.19,"bytes":8123456,"elapsed_ms":26890}
```

### Fixture corpus layout (binding — B0/B1 tests read the same tree)

```
tests/fixtures/playback2d/
├── README.md
├── manifest.json                       # schema_version, entries[] (name, scene, size, map,
│                                       #   map_version, layers, budget{}, pending)
├── scenes/
│   ├── duel-mirage-b.scene.json        # SceneFixture (B0's serializer)
│   └── …
├── annotations/
│   └── annotated-mirage-b.dvann.json   # B2 schema; referenced from a scene entry
└── goldens/
    ├── cpu/duel-mirage-b@1920x1080.png
    └── gpu/…                           # C2 only; perceptual lane
```

---

## Test plan

Project: `tools/DemoViewer.NET.Playback2D.Cli.Tests` (TUnit, `OutputType=Exe`). **Direct execution —
no Avalonia platform, no `HeadlessSession`, no dispatcher** (§11). It must not reference any
Avalonia package, transitively or otherwise; that is itself asserted.

Run: `dotnet run -c Release --project tools/DemoViewer.NET.Playback2D.Cli.Tests`
(or `dotnet test tools/DemoViewer.NET.Playback2D.Cli.Tests`).

| Class | Cases | Notes |
|---|---|---|
| `CliArgsTests` | `--name value` and `--name=value` equivalence; bare flags; `WxH` size parse (+ malformed → `CliUsageException`); comma lists; `--` terminator; unknown option → `ThrowIfUnconsumed` throws; missing required → usage | pure unit |
| `ProgramDispatchTests` | no args → 1 + usage on stdout; `--help` → 0; unknown verb → 1; usage text lists every implemented verb (T12 parity check) | in-process `Program.Main` |
| `AssetsRootResolverTests` | flag wins over env wins over probe; `--no-radar` → `Disabled`; missing root → `NotFound`; reported `Source` is accurate | temp dirs |
| `RenderFixtureTests` | every non-`pending` corpus entry renders; PNG header + exact `size`; not uniformly blank; `elapsed_ms` < 1000 on the second (warm) render of the smallest fixture — the design's exit criterion; unknown `--layers` id → exit 1 | uses committed `assets/de_mirage`, `assets/de_nuke` |
| `RenderDeterminismTests` | same fixture rendered twice in one process → identical SHA-256; and again from a **fresh subprocess** → identical SHA-256 (guards static/JIT-order leaks and §5.1 wall-clock leaks) | subprocess |
| `GoldenComparerTests` | identical images match byte-exact; 1-pixel `+1` channel change fails byte-exact and passes `DefaultPerceptual`; size mismatch → `FailureReason`; `CreateDiffPng` non-null exactly when `!Match` | synthetic `SKBitmap`s |
| `GoldenCommandTests` | `verify` on a pristine corpus → 0; corrupt one golden in a temp copy → exit 4 + diff written + `--json` `counts.mismatched == 1`; `update` rewrites and a following `verify` → 0; `pending: true` entry is skipped, not failed | temp corpus copy |
| `BenchCommandTests` | `--frames 8 --warmup 2 --json` emits every documented field, percentiles monotone (p50 ≤ p95 ≤ p99 ≤ max); `--gate --budget-p99-ms 0.0001` → exit 4 with a violation naming `render_p99_ms`; `--budget-scale 100` on the same run → 0; `--report-dir` writes one JSON | fixture source only |
| `BenchAllocationTests` | `allocated_bytes_per_frame == 0` over a ≥512-frame run on the smallest fixture — the §6 zero-allocation contract. **Marked `[Category("Budget")]`; it is expected to fail until B1's allocation cleanup lands** — wire it into CI in the same PR that closes B1 | slow (~10 s) |
| `TrackerFrameSourceTests` | `FrameIndexForTick` binary search matches a linear scan on a real demo; sequential `FrameAt` matches a from-zero replay at three sampled indices; the source never touches a shared tracker (asserted by construction — it builds its own) | `DemoTestHelper.RequireDemo()` → `SkipTestException` when no demo present, so CI (no demos) skips |
| `FixtureCommandTests` | `capture` writes a scene that `Load` round-trips field-for-field; `verify` on the committed corpus → 0; `list --json` names every entry | capture needs a demo → skip-guarded |
| `ExportCommandTests` (T11) | gif export of 12 frames via `ManagedGifSink` produces a decodable GIF of the right size (no ffmpeg needed); webm/mp4 cases **skip** when `FfmpegDependency.Locate()` is null; cancel mid-export → exit 5 and no orphaned ffmpeg process; missing ffmpeg + `--format mp4` → exit 2 | skip-guarded |
| **`NoAvaloniaArchitectureTests`** | (a) parse `artifacts/bin/dv2d/<config>/dv2d.deps.json` — no library or runtime assembly name starts with `Avalonia` (ordinal-ignore-case); (b) run `dv2d render --fixture … --diag-assemblies` as a **subprocess** and assert the dumped loaded-assembly list contains **no** `Avalonia*` **and does** contain `SkiaSharp` (proving the render actually happened through Skia rather than short-circuiting); (c) this test assembly's own `.deps.json` has no `Avalonia*`; (d) the Pipeline and Core deps.json likewise (design §11 architecture tests, asserted from the one project that can see all three) | the phase's hard constraint |
| `JsonContractTests` | with `--json`, stdout parses as exactly one JSON object and stderr carries the human lines; `schema_version == 1` in every command's payload; snake_case keys | subprocess, stdout/stderr captured |

`--diag-assemblies` is a documented flag on `render`: after the render completes it writes
`{"schema_version":1,"event":"loaded_assemblies","assemblies":[…]}` to stderr. It exists for the
architecture test and for support triage; it is not hidden.

**Fixtures needed:** the six corpus scenes (T5) + their CPU goldens (T7 `update`), the committed
`assets/de_mirage/` and `assets/de_nuke/` bundles (already in the repo), and — for the
demo-dependent tests only — any `.dem` resolvable by `DemoTestHelper` (`DEMO_PATH`, `TestData/`,
`demos/benchmarks/`, `demos/`), which is absent in CI and therefore skipped there.

---

## Build & wiring

### `tools/DemoViewer.NET.Playback2D.Cli/DemoViewer.NET.Playback2D.Cli.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <!--
      dv2d — headless Playback2D render / export / bench (docs/playback2d-v2/design.md §4, §5.8).
      HARD CONSTRAINT: this tool loads ZERO Avalonia assemblies. It references Pipeline only, and
      NoAvaloniaArchitectureTests asserts the deps graph and the runtime loaded-assembly set. Do not
      add an Avalonia package here, and do not reference src/App/*.
    -->
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <AssemblyName>dv2d</AssemblyName>
        <RootNamespace>DemoViewer.NET.Playback2D.Cli</RootNamespace>
        <LangVersion>latest</LangVersion>
        <!-- A demo replay holds raw bytes + ParsedDemo + a private EntityTracker; the same
             single-demo-at-a-time posture as DemoTrimmer. -->
        <ServerGarbageCollection>false</ServerGarbageCollection>
        <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
        <!-- Deterministic render output is a contract (design §5.1): no tiered-JIT-shaped drift in
             the golden lane. Cheap for a short-lived tool. -->
        <TieredCompilationQuickJitForLoops>false</TieredCompilationQuickJitForLoops>
        <InvariantGlobalization>true</InvariantGlobalization>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="../../src/Playback2D/DemoViewer.NET.Playback2D.Pipeline/DemoViewer.NET.Playback2D.Pipeline.csproj"/>
    </ItemGroup>

    <ItemGroup>
        <!-- Native Skia: the app gets libSkiaSharp via Avalonia.Skia, which this tool must not
             reference — so the RID-specific native assets are declared here explicitly. The
             SkiaSharp and NativeAssets.Linux PackageVersion entries are B0's (correction 4); this
             project only adds the Win32/macOS ones. -->
        <PackageReference Include="SkiaSharp"/>
        <PackageReference Include="SkiaSharp.NativeAssets.Win32"/>
        <PackageReference Include="SkiaSharp.NativeAssets.Linux"/>
        <PackageReference Include="SkiaSharp.NativeAssets.macOS"/>
    </ItemGroup>

    <ItemGroup>
        <InternalsVisibleTo Include="DemoViewer.NET.Playback2D.Cli.Tests"/>
    </ItemGroup>

</Project>
```

> **Pipeline project path — resolved:** B0 places the two library projects under
> `src/Playback2D/DemoViewer.NET.Playback2D.{Core,Pipeline}/` with a `/src/Playback2D/` slnx folder.
> Every `src/Visualization` path in this plan reads as `src/Playback2D`.

### `tools/DemoViewer.NET.Playback2D.Cli.Tests/DemoViewer.NET.Playback2D.Cli.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <AssemblyName>DemoViewer.NET.Playback2D.Cli.Tests</AssemblyName>
        <RootNamespace>DemoViewer.NET.Playback2D.Cli.Tests</RootNamespace>
        <!-- CA1707: test method names conventionally use underscores. -->
        <NoWarn>$(NoWarn);CA1707</NoWarn>
        <ServerGarbageCollection>false</ServerGarbageCollection>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="TUnit"/>
        <PackageReference Include="SkiaSharp"/>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="../DemoViewer.NET.Playback2D.Cli/DemoViewer.NET.Playback2D.Cli.csproj"/>
        <!-- Correction 1: src/Playback2D, not src/Visualization. -->
        <!-- TestSupport is Avalonia-free (TUnit + CS2DemoKit.Parser only) — safe for the
             direct-execution lane, and gives DemoTestHelper's skip-on-missing-demo behaviour. -->
        <ProjectReference Include="../../src/Testing/DemoViewer.NET.TestSupport/DemoViewer.NET.TestSupport.csproj"/>
    </ItemGroup>

</Project>
```

### `DemoViewer.NET.slnx`

Add inside the existing `<Folder Name="/tools/">`:

```xml
        <Project Path="tools/DemoViewer.NET.Playback2D.Cli/DemoViewer.NET.Playback2D.Cli.csproj"/>
        <Project Path="tools/DemoViewer.NET.Playback2D.Cli.Tests/DemoViewer.NET.Playback2D.Cli.Tests.csproj"/>
```

### `Directory.Packages.props`

Add **only the Win32 and macOS native-asset entries** — `SkiaSharp` and
`SkiaSharp.NativeAssets.Linux` are B0's (correction 4). The full block, for reference:

```xml
        <!--
            SkiaSharp for the Avalonia-free Playback2D Core/Pipeline/dv2d stack (design §4).
            VERSION POLICY: pin to EXACTLY the version Avalonia.Skia 11.3.12 resolves transitively
            (2.88.9 today — verify with `dotnet list src/App/DemoViewer.NET.Desktop package
            --include-transitive | grep -i skiasharp` before changing it). The App loads Core in the
            same process as Avalonia's Skia, and two libSkiaSharp natives in one process is a hard
            crash, not a warning. Bump ONLY in lockstep with the Avalonia block above.
            The NativeAssets packages exist for dv2d, which has no Avalonia to bring them.
            (tools/DemoViewer.NET.AssetBaker's SkiaSharp 3.119.2 is unrelated: it opts out of CPM
            and interops over a file boundary.)
        -->
        <PackageVersion Include="SkiaSharp" Version="2.88.9"/>
        <PackageVersion Include="SkiaSharp.NativeAssets.Win32" Version="2.88.9"/>
        <PackageVersion Include="SkiaSharp.NativeAssets.Linux" Version="2.88.9"/>
        <PackageVersion Include="SkiaSharp.NativeAssets.macOS" Version="2.88.9"/>
```

If B0 has already added `SkiaSharp`, do not duplicate — only add the three NativeAssets entries.

### CI — `.github/workflows/ci.yml`

Append a second job (leave `build` untouched):

```yaml
  playback2d:
    name: playback2d goldens + budgets
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0          # Nerdbank.GitVersioning needs full history

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      # SkiaSharp.NativeAssets.Linux links against fontconfig; without it every text draw in the
      # golden corpus falls back or throws. (NoDependencies would avoid this at the cost of font
      # fallback — not acceptable while the corpus contains text layers.)
      - name: Install native deps
        run: sudo apt-get update && sudo apt-get install -y libfontconfig1

      - name: Build dv2d
        run: dotnet build tools/DemoViewer.NET.Playback2D.Cli -c Release

      - name: CLI unit + architecture tests
        run: dotnet run -c Release --project tools/DemoViewer.NET.Playback2D.Cli.Tests

      # Goldens: fixture-only (no .dem is committed), CPU provider is the authoritative baseline.
      - name: Golden images
        run: >
          dotnet run -c Release --project tools/DemoViewer.NET.Playback2D.Cli --
          golden verify --cpu --json --diff-dir artifacts/playback2d-goldens

      # Budget gate. DV2D_BUDGET_SCALE relaxes the §6 time budgets for a shared CI runner; the
      # allocation budget (0 bytes/frame) is never scaled. Tighten toward 1.0 once a baseline exists.
      - name: Frame-budget gate
        env:
          DV2D_BUDGET_SCALE: '2.0'
        run: >
          dotnet run -c Release --project tools/DemoViewer.NET.Playback2D.Cli --
          bench --name duel-mirage-b --frames 512 --cpu --gate --json
          --report-dir artifacts/bench-reports

      - name: Upload render diffs + bench report
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: playback2d-artifacts
          path: |
            artifacts/playback2d-goldens/
            artifacts/bench-reports/
          if-no-files-found: ignore
```

### `scripts/dv2d.sh`

```sh
#!/usr/bin/env sh
# Convenience wrapper: scripts/dv2d.sh render --fixture … --out /tmp/f.png
exec dotnet run -c Release --project "$(dirname "$0")/../tools/DemoViewer.NET.Playback2D.Cli" -- "$@"
```

(`chmod +x`. Windows users run the `dotnet run` line directly, or
`artifacts/bin/dv2d/release/dv2d.exe`.)

---

## Dependencies

### Consumed from other phases (binding on them)

| Phase | API | Used by |
|---|---|---|
| B0 | `SceneFixture` in Pipeline — needs `static SceneFixture Load(string path)`, `void Save(string path)`, and properties `Scene2DFrame Frame`, `SceneTime Time`, `CameraScript? Camera`, `AnnotationDocument? Annotations`, `string? MapName`, `string? MapVersion`, `SKSizeI Size`, `int SchemaVersion` | `render --fixture`, `bench`, `golden`, `fixture` |
| B0 | `CpuSurfaceProvider : IRenderSurfaceProvider` with a public parameterless ctor; `IRenderSurfaceProvider` exactly as §5.8 | every command |
| B0/B1 | `SceneCompositor` — needs `bool Advance(in SceneTime, Scene2DFrame)`, `void Render(SKCanvas, SceneRenderContext)`, `IReadOnlyList<ISceneLayer> Layers { get; }`, and a way to construct with an explicit enabled-layer id set (the CLI never reads feature gates, §7.7) | `HeadlessSceneRenderer` |
| B0 | `ILevelLayoutPolicy.Arrange(MapSpace, LevelDisplayMode, SKSize)` + `StackedLayout`/`SingleLayout` | `--layout`, `--level` |
| B0 | `MapAssetPipeline` — needs an **explicit-root** entry point `static MapSpace? TryLoad(string assetsRoot, string mapName)` plus `static string? TryLocateAssetsRoot()`. Today's app-side equivalent is `MapAssetBundleReader.FindBundleDirectory(mapName)` + `MapAssetLoader.TryLoadFromDirectory(dir)`; the Pipeline version must decode to `SKImage` and must accept a root the caller chose (`--assets`) | `--assets` |
| B0 | `SceneFrameBuilder` — needs a form callable with `(EntityTracker tracker, DemoFrame frame)` or an equivalent snapshot, returning `Scene2DFrame`, with no Avalonia/VM types in the signature | `TrackerFrameSource` |
| B1 | Bench harness. C1 promotes it to the `bench` command; the required shape is `SceneBenchResult SceneBenchHarness.Run(SceneBenchRequest)` with `SceneBenchRequest(ISceneFrameSource Source, HeadlessSceneRenderer Renderer, SKSizeI Size, int Warmup, int Frames)` and `SceneBenchResult` exposing per-phase timing samples (`ReadOnlySpan<double> AdvanceMs/RenderMs`) + allocated bytes + GC counts. **If B1 has not landed, C1 implements exactly this in Pipeline and B1 adopts it** — there must be one harness, not two | `bench` |
| B1 | Embedded-typeface text rendering in Core (no `SKTypeface.Default`), or CI goldens run perceptual-only. See Risks | `golden` |
| B4 | `SceneExportSession.RunAsync(ExportRequest, ISceneFrameSource, IFrameSink, IRenderSurfaceProvider, IProgress<ExportProgress>, CancellationToken)`; `ExportRequest` exactly as §5.7; `FfmpegFrameSink`, `ManagedGifSink`, `CameraScript`, `FfmpegDependency.Locate()` | `export` |
| C2 | `GpuSurfaceProvider` + its probe factory (`IRenderSurfaceProvider CreateProvider(RenderBackend preference, out string reason)`) | `--gpu` |
| Existing (CS2DemoKit.Parser 0.10.0) | `DemoParser.Parse`, `ParsedDemo.Frames`, `EntityTracker` (`AdvanceOneFrame`, `CurrentEntities`, `CurrentTick`), `EntitySeekService(Func<EntityTracker>)` / `SeekToFrameNoSnapshot(int, IReadOnlyList<DemoFrame>)` → `SeekResult` | `TrackerFrameSource` |

### Exported by C1 (who consumes them)

| API | Consumer |
|---|---|
| `TrackerFrameSource` (Pipeline) | **B4** export session; C1's own `render --demo`/`bench --demo` |
| `HeadlessSceneRenderer` (Pipeline) | **B1** bench harness, **B0/B1** golden tests, **B4** export, C2's perceptual lane |
| `GoldenImageComparer` / `GoldenCorpus` / `GoldenTolerance` (Pipeline) | **B0** and **B1** golden-image tests, **C2** perceptual GPU lane, CI |
| `tests/fixtures/playback2d/` corpus + manifest schema | **B0/B1/B2** tests, CI, designers |
| `dv2d` command surface + JSON schemas + exit codes | CI workflow, `scripts/dv2d.sh`, future cloud highlight batch jobs |

---

## Risks & spikes

| # | Risk | L·I | Mitigation / time-box |
|---|---|---|---|
| R1 | **Font rasterization differs Windows ↔ ubuntu CI**, making byte-exact text goldens impossible | H·M | **Spike, 2 h in T6**: render one text fixture on both. Fix is B1-side (embedded typeface); fallback is `--tolerance perceptual` on the CI golden lane, with the byte-exact lane kept for text-free fixtures. Do not solve it by deleting text from the corpus |
| R2 | **SkiaSharp version skew** — Core/Pipeline on a different SkiaSharp than Avalonia.Skia loads → two native libs in one process | M·H | CPM pin equal to Avalonia's transitive resolve + a comment stating the policy; a `dotnet list package --include-transitive` check documented in `dv2d.md`. A future Avalonia bump must move both |
| R3 | `SkiaSharp.NativeAssets.Linux` needs `libfontconfig1`; a bare CI container fails at first draw with a `DllNotFoundException` | M·M | apt step in the CI job (above); if the runner image changes, `NoDependencies` + a bundled font is the escape hatch (couples to R1's fix) |
| R4 | **B4 slips**, `dv2d export` cannot land in C1's week | M·L | T11 is last and independently droppable; `export` prints a clear exit-6 message. C1's exit criterion does not mention export |
| R5 | Budget gate flaps on shared CI runners → PRs blocked by noise | M·M | `--budget-scale` starting at 2.0 + gate on **p99 only** (not max) + `--frames 512` in CI rather than 2000. Revisit after 20 runs of collected `bench-reports` |
| R6 | Zero-allocation assertion fails until B1's allocation cleanup lands | H·L | `BenchAllocationTests` ships `[Category("Budget")]` and is enabled in CI by the PR that closes B1; the CLI still *reports* bytes/frame from day one |
| R7 | Fixture schema churn during B2 (annotations) invalidates committed goldens | M·M | `schema_version` in every scene + `pending: true` entries skipped by `golden verify`; a schema bump is a reviewed `golden update` commit |
| R8 | An Avalonia dependency sneaks in transitively (e.g. someone adds a ProjectReference to App for one helper) | M·H | The three-pronged architecture test (T9) runs in CI from day one and fails the build, not a lint |
| R9 | `--tick` → frame mapping ambiguity when several frames share a `ServerTick` | M·L | Binary search returns the **first** frame with that tick; documented, and `render --json` always echoes the resolved `frame_index` |
| R10 | `render --demo` on a 400 MB demo blows the 1 s target (parse dominates) | H·L | Not the exit criterion — that is `--fixture`. `render --demo` prints a parse-time breakdown so the difference is obvious; the fixture loop is the advertised iteration path |

---

## Acceptance checklist

Design exit criterion, split into verifiable items (1–2), plus C1's own additions.

1. **"A designer/dev renders any tick to PNG in <1 s without launching the app"**
   - [ ] `dv2d render --fixture tests/fixtures/playback2d/scenes/duel-mirage-b.scene.json --out f.png`
         writes a correct PNG; `RenderFixtureTests` asserts warm `elapsed_ms` < 1000.
   - [ ] `dv2d render --demo <dem> --tick N --out f.png` renders any tick of a real demo (frame
         resolved by binary search, echoed in `--json`).
   - [ ] No Avalonia assembly is loaded during either (subprocess assertion), and no app process is
         started.
2. **"CI uses `dv2d` for goldens + budgets"**
   - [ ] `golden verify` runs in CI against the committed corpus and fails the job (exit 4) on a
         pixel regression, uploading actual/diff PNGs.
   - [ ] `bench … --gate` runs in CI and fails the job (exit 4) when p99 exceeds the scaled budget,
         emitting the `bench-reports` JSON.

Additions:

3. - [ ] All five verbs implemented (`render`, `export`*, `bench`, `golden`, `fixture`) with the
         documented flags; `--help` and the usage string list them (asserted by test).
         *`export` may be deferred with exit 6 if B4 has not landed — recorded in the PR.
4. - [ ] Exit-code table implemented and covered by tests (0/1/2/3/4/5/6).
5. - [ ] `--json` on every command: one object on stdout, humans on stderr, `schema_version: 1`,
         snake_case (asserted by `JsonContractTests`).
6. - [ ] The CLI reads **no** feature gate, `AppSettings`, or `FeatureCatalog` value — grep-clean and
         guaranteed by having no reference to `src/App/*`.
7. - [ ] `--assets` / `DV2D_ASSETS` / walk-up probe resolve the AssetBaker output root; the resolved
         root and its source appear in `--json`; `--no-radar` renders without art.
8. - [ ] **Zero Avalonia assemblies**: `NoAvaloniaArchitectureTests` covers deps.json (CLI, Tests,
         Pipeline, Core) and the runtime loaded-assembly set, and runs in CI.
9. - [ ] `tests/fixtures/playback2d/` exists with ≥ 6 scenes, a manifest, CPU goldens, and a README
         in the shape of `tests/fixtures/README.md`; `dv2d fixture capture` authors a new one from a
         demo in one command.
10. - [ ] Render determinism: identical SHA-256 across two runs and across two processes.
11. - [ ] `TrackerFrameSource` never publishes its tracker to `PlaybackController` and never uses
          `MainViewModel.CreateTracker` (§5.7) — asserted by construction and by review.
12. - [ ] `docs/playback2d-v2/dv2d.md` documents every flag, exit code, JSON schema, and both CI
          recipes; `scripts/dv2d.sh` works.
13. - [ ] Solution builds clean under `TreatWarningsAsErrors` with the new projects in the slnx, and
          `dotnet build src/App/DemoViewer.NET.Desktop -c Release` (the existing CI job) still passes
          with the new CPM entries.

---

## Implementation notes (deviations)

Recorded per the phase ground rules. Every item is a deviation from the plan body or from the §3
registry, with why. Nothing here was decided silently.

1. **`HeadlessSceneRenderer`'s constructor drops `ILevelLayoutPolicy` and `MapSpace?`.**
   The contract in *Public API contracts* is
   `(IRenderSurfaceProvider, SceneCompositor, ILevelLayoutPolicy, MapSpace?)`, but **neither of those
   two types exists at this commit** — B1 declares `MapSpace`/`ILevelLayoutPolicy` (registry §3.4) and
   B0 shipped neither. As-built:
   `HeadlessSceneRenderer(IRenderSurfaceProvider surfaces, SceneCompositor compositor,
   bool ownsCompositor = false, bool ownsProvider = false)` plus settable `Camera`
   (`ViewportTransform`) and `Palette` (`ScenePalette`) — a render context needs a transform and a
   palette, and the sketched signature supplied neither. **B1 adds the layout/map parameters** when it
   lands those types; the single-pane body is already isolated in `ContextFor`.

2. **`--layout single` and `--level` exit 6 rather than rendering one pane.**
   Same cause as (1). Accepting the flags and quietly rendering a single all-levels pane would produce
   a golden captured "with `--level 1`" that in fact shows every level — the worst available outcome.
   `SceneRenderPlan.RequireSingleLevelLayout` makes it an honest `ExitCode.EnvironmentUnavailable`
   with the owning phase named. B1/B3 replace that method.

3. **`TrackerFrameSource` does not declare `: ISceneFrameSource`.**
   That interface is B4's (registry §3.8) and does not exist yet. The four members it names —
   `FrameCount`, `TimeAt`, `FrameAt`, `Dispose` — are implemented with exactly the merged signature,
   so **B4 adds the interface to the declaration and nothing else changes**. A comment on the type
   says so.

4. **C1 lands `TrackerSceneSnapshot` (`Pipeline/Frames/`), which registry §3.8 assigns to B4.**
   `render --demo` and `fixture capture` cannot build a `Scene2DFrame` from an `EntityTracker` without
   it, and B4 has not started. It is exactly the described shape: a pooled, re-aimed
   `PawnLookup.ForEachLivePawn` join presenting `IReadOnlyList<IPlayerState>` +
   `IReadOnlyEntityView`, mirroring the App's `ModuleContext` join field for field. Landing it here
   rather than writing a throwaway avoids two competing adapters. **B4 consumes it as-is**; if B4
   needs more (kill-feed injection, follow-slot plumbing), it extends this type.

5. **C1 lands `MapAssetPipeline` + `LoadedMapAssets` (`Pipeline/Assets/`), listed in the plan's
   Dependencies table as B0's.** B0 did not ship it. The explicit-root entry point the plan specifies
   is honoured — `TryLoad(string assetsRoot, string mapName)` — but it returns a Pipeline
   `LoadedMapAssets` (decoded `MapRadarImage` list + floors + bounds + `mapVersion`) rather than
   `MapSpace?`, because `MapSpace` does not exist (see 1). `TryReadMapVersion` and
   `TryLocateAssetsRoot` are additions the fixture/golden staleness check needs.

6. **`SceneLayerCatalog` (`Pipeline/Headless/`) is new and unlisted.**
   The CLI reads no feature gate (design §7.7), so the set of layers a render *can* contain has to be
   enumerable from Pipeline. Today it registers B0's `DebugGridLayer` (which is `internal` to Core
   with `InternalsVisibleTo` for Pipeline — no visibility was widened). **This is the single place B1
   registers the real seven layers**; `--layers radar,markers` starts working with no CLI change.
   `--layers` accepts bare or `playback2d.`-prefixed ids and always reports the prefixed form.

7. **The bench measurement loop lives in `BenchCommand.Measure`, not in
   `Pipeline.Benchmarking.ScenePipelineBenchmark`.** Registry §3.9 assigns the harness to B1 but pins
   only the type *names*, not a single member signature — insufficient to compile a wrapper against —
   and B1 is being built concurrently in another tree, so declaring those types here would guarantee a
   merge collision. The plan's own T8 anticipates this ("if B1 has not landed, implement the loop
   against `HeadlessSceneRenderer` directly and hand it to B1 to absorb"). The loop is marked as a
   seam in the class doc-comment. **Merge action for B1: move
   `Measure`/`FrameTimeStats`/`BenchResult` into `ScenePipelineBenchmark`/`BenchmarkReport`/
   `FrameTimeStats` and have `BenchCommand` call it. The JSON shape and the gate stay here.**
   `dv2d bench --gate` is fully functional in the meantime, which is what CI needs.

8. **`dv2d export` is deferred (T11, risk R4) — exit 6 with a message naming the missing B4 types.**
   No second encoder path was stubbed, per the plan's explicit instruction. `ExportCommand`'s
   doc-comment spells out the body B4 fills in.

9. **`--gpu` degrades to CPU with a printed reason instead of failing, unless `--strict-backend`.**
   `BackendResolver` implements the §5.8 precedence ladder minus its `AppSettings` rung (a headless
   tool reads no UI state, §7.7). **C2 replaces the single construction site with
   `RenderSurfaceProviderFactory`.**

10. **`golden verify` defaults to `perceptual`, not byte-exact.** The plan calls byte-exact the CPU
    policy, but B0 had already committed its CPU goldens at `DefaultPerceptual` and documented why
    (corpus README: "CPU rasterisation of anti-aliased edges can differ by a least-significant bit
    between SIMD paths"). Rather than override that with a flag, tolerance became **per-entry
    manifest data** (`"tolerance": "byte-exact" | "perceptual"`, default perceptual) with
    `--tolerance` overriding globally. An entry opts into byte-exact once B1's embedded typeface makes
    it safe. *Measured, for the record:* `dv2d` reproduces B0's three committed synthetic goldens
    **byte-identically** (maxDelta 0, 0.0000% of pixels) — `golden update` left them unmodified in
    git.

11. **`GoldenCorpusEntry` gains `Tolerance`, `Notes`, `SceneRelativePath` and `CorpusDirectory` as
    init-only properties.** The positional record signature from the plan is unchanged (so the
    constructor is exactly as specified); the extra state is what `GoldenPath` and the manifest writer
    need. `GoldenCorpus.Upsert` is an addition `fixture capture` requires.

12. **`GoldenComparerTests` was not duplicated.** B0 already ships `GoldenImageComparerTests` in
    `src/Playback2D/DemoViewer.NET.Playback2D.Tests` for the comparator it owns (correction 2c). C1's
    test project covers `GoldenCorpus` instead (`GoldenCorpusTests`). One comparator, one test class.

13. **The T6 font-determinism spike was not run.** It is time-boxed to compare a *text* draw across
    Windows and the ubuntu CI image, and **no layer in this build draws text** — B0's smoke layer
    deliberately does not, and B1's text layers have not landed. Running it would measure nothing.
    The requirement it exists to file is already recorded on B1 (correction 3), and the corpus already
    defaults to `perceptual` (see 10), which is the documented fallback. **B1 runs the spike when it
    adds the first text layer.**

14. **CI's budget gate passes `--budget-bytes-per-frame 4096`.** Design §6's allocation budget is 0
    and the manifest says 0, but today's smoke layer allocates its `SKPaint`s inside `Render`
    (measured: ~2.7 KB/frame), exactly as risk R6 predicted. The gate therefore runs against a stated
    temporary ceiling rather than being switched off. `BenchAllocationTests` ships
    `[Category("Budget")]` and the CI test step filters `Category!=Budget`. **The PR that closes B1's
    allocation cleanup drops both the flag and the filter** — that is written in the workflow comment
    and in `dv2d.md`.

15. **Corpus contents differ from T5's six-fixture list.** Committed and green: `synthetic-empty`,
    `synthetic-tenplayers`, `synthetic-utility` (B0's), plus C1's `fitmap-mirage-eco`,
    `duel-mirage-b`, `bomb-planted-inferno` (hand-authored on real map coordinates and keyed to the
    committed bundles) and `nuke-single-upper` (captured from `assets/tour/sample-de_nuke.dem` by
    `dv2d fixture capture`). Seven scenes, seven CPU goldens. Registered but `pending`:
    `nuke-multilevel` (B1's parity pair — its golden came from the pre-v2 control's two-pane
    900x450-in-900x900 layout and cannot be reproduced single-pane), `annotated-mirage-b` (B2's
    document), `full-scene-budget` (B1's worst-case 1080p bench scene). `pending` entries are skipped
    by `golden verify` and `fixture verify`, and may have no scene file at all — documented in the
    corpus README.

16. **`RenderPurpose.Export` is used for every CLI render.** The sketched defaults say `Thumbnail`; a
    headless tool is not drawing thumbnails, and one value across `render`/`golden`/`bench` is what
    keeps a golden and a bench run drawing the same thing. No current layer reads `Purpose`, so this
    changes no pixels today. **B1: if a layer starts branching on `Purpose`, the goldens re-baseline
    once, deliberately.**

17. **`Program.Main` maps `ArgumentException` to exit 3 (runtime failure).** The plan's table has no
    row for it. Unknown-layer errors are converted to `CliUsageException` (exit 1) at the call site so
    the common case still lands on the documented code.

**Not implemented, with reasons:** `dv2d export` (8 — blocked on B4); the T6 spike (13 — nothing to
measure yet); `--layout` / `--level` (2 — blocked on B1/B3); GPU rendering (9 — C2 owns it).

### Review fixes (independent review, 2026-08-24)

Three defects found by the phase review, each with a regression test in
`tools/DemoViewer.NET.Playback2D.Cli.Tests/ReviewRegressionTests.cs` that fails without its fix.

18. **`HeadlessSceneRenderer.RenderInto` discarded the injected clock.** `RenderInto(surface, frame,
    time)` passed `time` to `Advance` but then called
    `Render(SKSurface, Scene2DFrame, RenderPurpose)`, which stamps the `SceneRenderContext` from
    `frame.Time`. On the fixture path the two are the same value, which is why
    `HeadlessSceneRendererTests.Render_MatchesRenderInto` could not see it — it passes `fixture.Time`
    for a frame whose own `Time` *is* that value. They are **not** the same on the demo path:
    `TrackerFrameSource.TimeAt` derives `DeltaSeconds` from fps/speed and authors `IsDiscontinuity`,
    while the frame's `Time` comes from `SceneFrameBuilder`. Since `bench` renders through
    `RenderInto`'s split pair and `golden` renders through `Render(frame, time, size)`, the two
    commands would have drawn different scenes from the same input the moment a B1 layer read
    `ctx.Time` — a silent §5.1 determinism break. **Fix:** added
    `Render(SKSurface, Scene2DFrame, in SceneTime, RenderPurpose)`; `RenderInto` and both
    `BenchCommand.Measure` loops now pass the source's clock. The 3-argument overload is unchanged
    (it is the plan's contract signature) and delegates with `frame.Time`. No pixels move today — no
    layer reads `ctx.Time` — and the committed goldens verified byte-identical after the change.
    **B1: absorb this overload when the bench loop moves to `ScenePipelineBenchmark`.**

19. **`ResolvedBackend.Backend` was a use-after-dispose in `GoldenCommand`.** It was declared
    `=> Provider.Backend`, and `GoldenCommand` captures the first entry's `ResolvedBackend`
    (`backend ??= plan.Backend`) but disposes each entry's `SceneRenderPlan` — and with it that
    provider — at the end of every loop iteration, then reads `backend.Backend` when it builds the
    summary payload. Inert against `CpuSurfaceProvider` (constant property, no-op `Dispose`), a fault
    against C2's `GpuSurfaceProvider`, which owns an EGL context and which deviation (9) hands over
    through this very type. **Fix:** the backend is captured at construction
    (`public RenderBackend Backend { get; } = Provider.Backend;`). **C2: this is why the type is safe
    to hand a real GPU provider.**

20. **`TrackerSceneSnapshot.Refresh` allocated a delegate on every frame.** The callback was written
    inline as `PawnLookup.ForEachLivePawn(tracker, (slot, pawn) => _pawnBySlot[slot] = pawn)`. That
    lambda captures `this`, and Roslyn caches only fully non-capturing lambdas, so a fresh delegate
    was allocated per call — in the one adapter that runs once per exported frame, straight onto the
    §6 budget B4's export loop is measured against. **Fix:** the callback is built once in the
    constructor and held in a field. Measured on the committed `assets/tour` demo: **424 → 360
    bytes/frame**, the delegate being exactly 64 of them. The same inline pattern exists at
    `src/App/DemoViewer.NET/Modules/ModuleContext.cs:307`; it was left alone as out of C1's scope.

    **Carry-forward for B4:** the residual 360 bytes/frame is **not** Pipeline's and cannot be closed
    here. Bisected on the same demo: 72 bytes inside `PawnLookup.ForEachLivePawn` and ~24 bytes per
    boxed `EntityState` indexer read, both inside the pinned CS2DemoKit 0.10.0 package. Design §6's
    zero-allocation contract for the demo-backed frame source needs a package-side allocation-free
    read path. `ReviewRegressionTests.TrackerSceneSnapshot_Refresh_AllocatesNoPerFrameDelegate` pins
    the current figure at ≤ 384 bytes/frame and carries `[Category("Budget")]`, for the same reason
    `BenchAllocationTests` does (risk R6): an allocation figure must never flap a required CI check.

### Merge into `feature/playback2d-v2` (2026-08-25)

C1 was merged after B1, which had landed concurrently in the same tree. Three files collided and two
of C1's own deviations were closed by the merge. Numbering continues from the review fixes.

21. **`HeadlessSceneRenderer`: B1's implementation wins; C1's single-pane facade is withdrawn.**
    Both phases shipped a public `Pipeline.Headless.HeadlessSceneRenderer` — an add/add conflict, and
    the registry's "one headless render entry point, never a second render path" (§3.7) forbids
    resolving it by keeping both under different names. B1's is the completed form deviation (1)
    predicted ("**B1 adds the layout/map parameters** when it lands those types"): it takes
    `(SceneCompositor, IRenderSurfaceProvider, ILevelLayoutPolicy, ScenePalette)`, derives the level
    set, reconciles `PaneSet`, and draws a multi-pane `SceneSubmission`. The convenience surface
    `dv2d` was written against was re-expressed **on** it as thin wrappers rather than as a parallel
    loop: a two-argument `(surfaces, compositor)` constructor defaulting to `StackedLayout` +
    `ScenePalette.Dark`, `Compositor`, `Backend`, a nullable `Camera` re-applied after each `Advance`
    (so a pinned camera survives the pane reconciliation the first advance performs),
    `Render(SKSurface)`, `RenderInto`, `Render(frame, time, size)` and `RenderPng`. C1's determinism
    fix (18) needed no re-application: B1's `Advance(frame, in SceneTime)` already stamps the
    submission from the injected clock, and `RenderInto` sizes the pane layout from the caller's
    surface. **Carry-forward for B1** — deviation 18's "absorb this overload" is closed.

22. **`MapAssetPipeline`: B1's `LoadedMapAsset` wins; C1's explicit-root entry points were added to
    it.** Both phases shipped a `Pipeline.Assets.MapAssetPipeline` (C1 deviation (5) landed it for
    B0; B1 landed its own in T5 and the App consumes that one through `MapAssetLoader`). B1's type is
    the one with production callers, so C1's parallel `LoadedMapAssets` was withdrawn and the three
    members the CLI actually needs moved onto B1's class: `TryLoad(assetsRoot, mapName)` (the
    explicit-root overload — `FindBundleDirectory`'s walk-up from `AppContext.BaseDirectory` is the
    wrong answer for a tool that was told where the art lives), `TryReadMapVersion` and
    `TryLocateAssetsRoot`. The CLI reads `Bundle.MapName`/`Bundle.MapVersion` and describes radars
    once through `DescribeRadars`.

23. **Deviation (7)'s merge action is done: `dv2d bench` no longer owns a measurement loop.**
    `BenchCommand.Measure`, and the CLI-internal `FrameTimeStats`/`BenchResult`, are deleted;
    `BenchCommand` now calls `Pipeline.Benchmarking.ScenePipelineBenchmark.Run` over a
    `PlanFrameSource` adapter (`SceneProvider` + the plan's radar enrichment, presented as
    `ISceneFrameSource`). What the deviation said would stay here stayed here: the JSON shape, the
    budget resolution and the `--gate` exit. Two small additions to B1's harness were needed and are
    justified in place — a settable `Camera` (a bench and its golden must frame the scene identically,
    or "bench got slower" can just mean "the camera got wider"; with it set, rig advance and the
    first-frame `FitAll` are suppressed) and GC collection counts on `BenchmarkReport`, defaulted so
    B1's own construction site and `BudgetTests` are unchanged, because `dv2d`'s documented JSON
    carries a `gc` block.

24. **`dv2d` now renders through the pane pipeline, which moved exactly one committed golden.**
    `SceneRenderPlan` binds the bundle's nav floors and a `MapRadarBinder` the way `SceneStage` and
    `Scene2DHost` do, so the CLI derives the same level set the app does. `golden update` rewrote all
    six verifiable entries; five came back **byte-identical** (single-level scenes arrange one
    full-host pane, which is the transform C1's facade used). The one that changed is
    `nuke-single-upper`: with de_nuke's two nav floors it is now two stacked bands with the live
    players in the upper one — the picture the app draws, and the reason C1 had to mark
    `nuke-multilevel` pending. Its golden is re-baselined in the merge commit.

25. **`synthetic-empty` is marked `pending`, with the reason recorded in the manifest.** Under the
    pane pipeline a frame with no players and no bundle derives no floor band, so it has no pane and
    renders background only; B0's committed golden for it came from the single-pane `SceneRenderer`
    path, which `SceneGoldenTests` still pins byte-exact. Rather than overwrite a golden another
    suite depends on, or leave the gate red, the entry is skipped the same way `nuke-multilevel` is.
    **Whether an empty level set should lay out one whole-host pane is B1's/B3's call, not a merge
    agent's** — the alternative (synthesising a phantom `MapLevelId` in `StackedLayout`) touches level
    identity, which is design risk 5. Re-enable the entry when it is answered.
    Three C1 tests moved with it, none weakened: the two `ReviewRegressionTests` clock probes hand
    their frame one marker so a pane exists (the assertion is unchanged),
    `RenderFixtureTests.EveryCorpusEntry_RendersToACorrectNonBlankPng` exempts the entry literally
    named "empty", and `GoldenCommandTests.Verify_MissingGolden_ExitsFour` deletes
    `synthetic-tenplayers`' golden instead — a pending entry is skipped before its golden is looked
    for, so that case would otherwise have passed for the wrong reason.

26. **`BenchAllocationTests` now benches a fixture that actually draws.** It targeted
    `synthetic-empty`, which under the pane pipeline reports 0 B/frame because it renders nothing —
    a green light measuring nothing. It targets `synthetic-tenplayers` (3336 B/frame today) and is
    renamed `SmallestDrawingFixture_AllocatesNothingPerFrame`. It still carries `[Category("Budget")]`
    and still fails, for the reason deviation (14) gives — but the attribution has changed: **B1's
    seven layers are allocation-clean** (the core suite's `full-scene-budget` run reports 0 B/frame),
    and what allocates is B0's smoke layer, which is all `SceneLayerCatalog` registers. The CI comment
    and `dv2d.md` now name that, and the flag drops when the catalog is extended, not when B1's
    cleanup lands (it has).

27. **CI: C1's five steps were appended to B0's `playback2d-tests` job as planned**, above B1's
    `playback2d-budget` lane and keeping B1's `dotnet run --treenode-filter "…[Category!=Budget]"`
    invocation of the core suite (C1's branch still had the older `dotnet test` line). The CLI's
    `Category=Budget` lane is still not wired into any job — unchanged from C1 as merged, and it
    would be red today; it joins `playback2d-budget` in the same PR that extends the catalog.

**Left for the owning phases after this merge:** `dv2d export` (B4) · the seven real layers in
`SceneLayerCatalog` (B1) · `--layout single` / `--level` (B3) · `--gpu` (C2) · the empty-level-set
layout question in (25) (B1/B3) · the CS2DemoKit-side allocation floor under `TrackerSceneSnapshot`
(B4, review carry-forward 1).
