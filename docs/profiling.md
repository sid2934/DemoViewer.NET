# Profiling & Instrumentation — DemoViewer.NET

Performance instrumentation for the three perf-critical layers (**parser**, **entity tracking**,
**analysis engine**) is **off by default** and must be explicitly enabled. Enabling is now a **single
runtime switch** — there is no profiling build any more. A default run pays only a single predicted
branch per instrumented seam when the switch is off, and the runtime sources idle at a single branch
with no listener attached — the accepted price of having one uniform mechanism (no build-time `#if`
left anywhere).

## The single switch — `Profiling.Enabled`

All profiling is gated at runtime on one flag, `CS2DemoKit.Parser.Profiling.Enabled`, which lives
in the Parser package (the lowest common layer every other project references). When it is on, the
parse-pipeline and entity-decode accumulators populate; when it is off they are never touched. It is
turned on three equivalent ways:

| How | When | What it does |
|---|---|---|
| `CS2DEMOKIT_PROFILE=1` env var | at process start | `Profiling.Enabled` resolves to `true`; `ProfilingSession` also attaches the Meter + ActivitySource listeners and dumps a combined report on exit |
| `--profile` bench flag | before the bench run | sets `Profiling.Enabled = true` and attaches the Meter + Activity listeners for the run |
| `Profiling.Enabled = true` (API) | before a run (e.g. the Diagnostics tab) | flips the switch programmatically |

The env var accepts `1` / `true` / `yes` (case-insensitive). Default (unset / not flipped):
everything off.

> **Env-var spelling.** The switch moved into the CS2DemoKit packages, and its variable is
> `CS2DEMOKIT_PROFILE`. This document and `RuntimeEnvInfo` still name `DEMOVIEWER_PROFILE` in places;
> that spelling no longer flips `Profiling.Enabled` on its own. `dv2d` honours **both** (see below);
> elsewhere, prefer `CS2DEMOKIT_PROFILE`.

**Threading contract — set before the run.** `Profiling.Enabled` is read on `Parallel.For` worker
threads (parse pass-2, the parallel digest producer). Set it *before* the run it governs begins; the
`Parallel.For` fork is a full memory barrier, so every worker observes the pre-fork value. A plain
`bool` is sufficient under this contract (the hot fan-out sites additionally snapshot it into a local
before forking). One profiled run at a time — the accumulators are process-static and assume the
single-run convention.

### Runtime sources (always shipped, near-free when off)

The analysis evaluator publishes three runtime sources that cost a single branch when nobody listens
(these are unchanged by the migration — they were always runtime-gated):

- **`EventSource` `DemoViewer.Analysis.Evaluator`** — per-frame / per-message / lifecycle trace events.
- **`Meter` `DemoViewer.Analysis.Evaluator`** — counters (`analysis.messages.processed`,
  `analysis.edges.evaluated`, `analysis.edges.fired`, `analysis.logic_nodes.recomputed`,
  `analysis.players.materialized`) + the `analysis.frame.duration_ms` histogram.
- **`ActivitySource` `DemoViewer.Analysis`** — phase-timeline spans (`analysis.eval` ⊃
  `analysis.precompute`). `StartActivity` returns `null` when nothing is sampling, so the spans are
  near-free by default. The bench also spans `read`/`parse`/`build` so `--timeline` shows the full
  nested pipeline.

The evaluator's per-message `Counter.Add` is guarded on whether a `MeterListener` is actually
subscribed, so the default user path does **no** counter work; the frame-duration histogram records
whenever **either** an EventSource trace **or** a Meter listener is attached.

**Provider names (for filtering).** The evaluator's events and counters share the EventSource/Meter
name **`DemoViewer.Analysis.Evaluator`** (evaluator-scoped). The phase-timeline spans use the broader
ActivitySource name **`DemoViewer.Analysis`** (whole-pipeline-scoped — it brackets parse/build too, not
just the evaluator). Filter on the name matching the data you want; they are intentionally distinct
because their scopes differ.

## Running the bench (`tools/AnalysisBench`)

```sh
# Turn on the full per-phase profile trees + attach Meter/Activity listeners — no special build:
dotnet run --project tools/AnalysisBench -c Release -- <demo.dem> --profile --no-golden
#  → "Parse-Pipeline Profile" + "Entity-Tracking Profile" + counters + timeline blocks

# Equivalently via the env var (also dumps a combined ProfilingSession report on exit):
CS2DEMOKIT_PROFILE=1 dotnet run --project tools/AnalysisBench -c Release -- <demo.dem> --no-golden

# Individual runtime listeners (also work on their own, without --profile):
dotnet run --project tools/AnalysisBench -c Release -- <demo.dem> --counters --no-golden
#  → "Evaluator Counters (Meter)" block (also exercises the frame-duration histogram)
dotnet run --project tools/AnalysisBench -c Release -- <demo.dem> --trace --no-golden
#  → "Evaluator Diagnostics" block (EventSource)
dotnet run --project tools/AnalysisBench -c Release -- <demo.dem> --timeline --no-golden
#  → "Phase Timeline (ActivitySource)" block: read / parse / build / analysis.eval ⊃ analysis.precompute
```

`--profile` implies `--counters` and `--timeline` (they construct one listener each — no double-attach).
Always pass `--no-golden` on verification runs so they don't re-baseline the committed
`tests/fixtures/*/*.golden.json` oracle.

> Note: under the parallel precompute path the entity decode runs on throwaway worker trackers,
> so the `AdvanceAndPoll (Σ phases)` line and the tracker sub-tree (`PacketEntities`/`field-path`/…) read
> ~0 — the decode cost lands in `Parallel precompute` instead. This is expected, not a regression.

## Profiling the shipped app (no rebuild) — `dotnet-trace` / `dotnet-counters`

Because the runtime sources ship in the default binary, you can attach to a **running** Desktop app
(or the bench) with the standard .NET diagnostics CLI — nothing special needed:

```sh
dotnet tool install --global dotnet-trace      # one-time
dotnet tool install --global dotnet-counters    # one-time

# Live counters:
dotnet-counters monitor --name DemoViewer.NET.Desktop \
  --counters DemoViewer.Analysis.Evaluator,System.Runtime

# Collect a trace (our EventSource + CPU samples + GC) → open in PerfView / speedscope:
dotnet-trace collect --name DemoViewer.NET.Desktop \
  --providers DemoViewer.Analysis.Evaluator:0xFFFFFFFFFFFFFFFF:4,Microsoft-DotNETCore-SampleProfiler,System.Runtime
```

`--name` takes the process name (or use `--process-id`).

### One-env-var switch: `CS2DEMOKIT_PROFILE=1`

For an in-proc report **dumped on exit** (no external tooling), set the env var. The bench and the
Desktop app both honor it via the shared `ProfilingSession` helper — it attaches the Meter +
ActivitySource listeners for the whole session and prints a combined report (phase timeline + evaluator
counters) when the process exits. It also flips `Profiling.Enabled` (both resolve the same env var at
init), so the parse + entity accumulator trees populate too. Default (unset): no session, no listeners,
no cost.

```sh
# Bench — report prints after the run:
CS2DEMOKIT_PROFILE=1 dotnet run --project tools/AnalysisBench -c Release -- <demo.dem> --no-golden
# Desktop app — load/analyze a demo, then close the app; the report prints to the launching terminal:
CS2DEMOKIT_PROFILE=1 dotnet run --project DemoViewer.NET.Desktop
```

Notes: the report is written to `Console.Out`, so it is only visible when launched from a terminal — on
Windows the Desktop app is a `WinExe`, so a normally-launched GUI has no console (run it from a terminal
or via `dotnet run`; for a bundled GUI use `dotnet-trace`/`dotnet-counters` above instead). The app
report is a **whole-session aggregate** (every load summed, printed once at exit) — use `dotnet-counters`
for a live per-moment view.

### In-app — the Diagnostics tab

The Diagnostics tab flips `Profiling.Enabled` and re-runs the analysis to populate its panels. Because
the tab's **Re-run** reuses the already-parsed `ParsedDemo` (it does not re-parse):

- **Entity profiling** repopulates on a **Re-run** (entity decode happens during evaluation).
- **Parse profiling** can only capture from a load done with profiling already on — set
  `CS2DEMOKIT_PROFILE=1` at startup (or flip the switch before loading) and **reload** the demo.

## Playback2D scene capture (`dv2d --perf`)

The scene pipeline has its own per-layer / per-stage capture, because Core is banned from wall-clock
APIs and cannot instrument itself (design §5.1). `dv2d bench --perf` and `dv2d export --perf` attach a
recorder to `SceneCompositor`'s profiler seam and report, per stage and per layer, p50/p95/p99/total
and share-of-frame, plus picture-cache hit rates, the uncapped render-only fps, and a slowest-first
ranking.

```sh
# Which layer is expensive, and how fast could this scene possibly draw?
dv2d bench --name duel-mirage-b --frames 2000 --perf

# Where does an export's realtime ratio actually go: decode, raster, read-back, or the encoder?
dv2d export --demo match.dem --from t72000 --to t79680 --size 1280x720 --fps 60 --perf --json
```

`Profiling.Enabled` (i.e. `CS2DEMOKIT_PROFILE=1`, or the legacy `DEMOVIEWER_PROFILE=1`, both of which
`dv2d` reads) turns the scene capture on implicitly. The reverse is not true: `--perf` deliberately
does **not** set `Profiling.Enabled`, because the tracker decode is one of the stages being timed.
Full contract: [`playback2d-v2/dv2d.md`](playback2d-v2/dv2d.md#performance-capture---perf) and
[`playback2d-v2/plans/P1-perf-instrumentation.md`](playback2d-v2/plans/P1-perf-instrumentation.md).

## Micro-benchmarks (`tools/EntityMicroBench`)

BenchmarkDotNet harness for nanosecond-resolution entity-access primitives. Always `-c Release`:

```sh
dotnet run --project tools/EntityMicroBench -c Release
dotnet run --project tools/EntityMicroBench -c Release -- --filter '*ResolveHandle*'

# Cross-check: independent MemoryDiagnoser measurement of the parallel digest decode (~GiB/op,
# confirming the per-worker alloc fix). Slow (~seconds/op) so it is excluded from the default run:
dotnet run --project tools/EntityMicroBench -c Release -- --filter '*Precompute*' --job short
```

## Reading a snapshot programmatically

```csharp
// Turn profiling on before the run (or set CS2DEMOKIT_PROFILE=1 at startup):
CS2DemoKit.Parser.Profiling.Enabled = true;

// Parse pipeline (after a parse done with profiling on):
ParseProfilingSnapshot p = ParseProfilingSnapshot.Read();   // .Enabled == false if that parse was unprofiled
// Entity decode (after a run done with profiling on):
EntityProfilingSnapshot e = tracker.GetProfilingSnapshot();
ScannerProfilingSnapshot s = scanner.GetProfilingSnapshot();
```

Each snapshot's `.Enabled` reflects whether **that data was captured with profiling on** — it is latched
when the instrumented region begins, not read live at `Read()` time, so toggling the flag after a run
never misreports a snapshot. Tick fields are raw `Stopwatch` timestamps — convert with
`Stopwatch.GetElapsedTime` / `Stopwatch.Frequency`. `Pass2WallTicks` is the wall-clock of the parallel
decode span (its per-worker allocation is not isolable outside the loop; take a `dotnet-trace` CPU
sample for the decompress-vs-parse split).
```
