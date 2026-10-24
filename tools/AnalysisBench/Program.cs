#region

using DemoViewer.NET.Services;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnalysisBench;
using DemoViewer.NET.TestSupport;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.GoldenStats;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;

#endregion

// ── Test Suite ──────────────────────────────────────────────────────────────
// Discovered from demos/benchmarks/: any .dem file is a benchmark entry.

string benchDir = Path.Combine(FindRepoRoot(), "demos", "benchmarks");
TestCase[] testSuite = DiscoverTestSuite(benchDir);

HashSet<string> flags = new(args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && !a.Contains('=')),
    StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> namedArgs = args
    .Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('='))
    .Select(a => a.Split('=', 2))
    .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
string[] positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

// `rules check <dir> [--demo=<path>]` — the standalone rule-config checker verb;
// dispatches before every bench path. See RulesCheckCommand.
if (positional is ["rules", "check", ..])
{
    return RulesCheckCommand.Run(positional.Skip(2).ToArray(), namedArgs, flags);
}

// `bg-run --list=<file>`: sequential background parses under the process's own GC environment.
if (positional is ["bg-run", ..])
{
    return BackgroundRunCommand.Run(namedArgs, flags);
}

bool suiteMode = flags.Contains("--suite");
bool listSuite = flags.Contains("--list-suite");
// Writing golden stats is opt-in. Nothing in the repo reads ours.golden.json, so a plain run
// writes nothing; --golden puts it back for a hand comparison. --no-golden is still accepted and
// is now a no-op, so the command lines recorded in docs/profiling.md keep doing what they say.
bool writeGolden = flags.Contains("--golden");
// Diagnostic listeners — parsed here (not just in the single-demo path) so they compose with --suite
// too. Each is a runtime opt-in (no profile build needed); see docs/profiling.md.
bool enableTrace = flags.Contains("--trace");
bool enableCounters = flags.Contains("--counters");
bool enableTimeline = flags.Contains("--timeline");
// --profile (runtime, no special build): turns on the entity accumulator tree via the single
// Profiling.Enabled switch AND attaches the Meter + ActivitySource listeners (it implies --counters
// and --timeline, which construct one listener each, so neither double-attaches). Set HERE, before
// any read of a demo, so every read observes the flag (the set-before-run contract).
// DEMOVIEWER_PROFILE=1 resolves the same switch at process start.
// --mmap: the frame-walking debug modes below parse the whole demo, so they read it through a
// MemoryMappedDemoSource instead of File.ReadAllBytes and the raw bytes never enter the managed
// heap. See MemoryMappedDemoSource's ownership contract: the mapping is disposed as soon as the
// bytes are no longer needed.
bool useMmap = flags.Contains("--mmap");
// --ray-counters: turn on the visibility path's ray budget counters (VisibilityCounters, off by
// default in the library). Process-wide, set before any run like Profiling.Enabled; RunBench resets
// them before eval and snapshots them after, so --suite reports each demo on its own. Per-ray
// Stopwatch brackets add a few percent to the raycasting, so eval_ms from a counted run is not the
// baseline number; take that from a run without the flag.
bool enableRayCounters = flags.Contains("--ray-counters");
if (enableRayCounters)
{
    VisibilityCounters.Enabled = true;
}

bool enableProfile = flags.Contains("--profile");
if (enableProfile)
{
    Profiling.Enabled = true;
    enableCounters = true;
    enableTimeline = true;
}

if (listSuite)
{
    if (testSuite.Length == 0)
    {
        Console.WriteLine($"No .dem files found in {benchDir}");
        Console.WriteLine("Place demo files there to add them to the benchmark suite.");
        return 0;
    }

    Console.WriteLine($"Benchmark directory: {benchDir}");
    Console.WriteLine($"{"ID",-50} {"Size",8}");
    Console.WriteLine(new string('─', 70));
    foreach (TestCase tc in testSuite)
    {
        FileInfo fi = new(tc.DemoPath);
        string size = $"{fi.Length / 1024.0 / 1024.0:F0} MB";
        Console.WriteLine($"  {tc.Id,-48} {size,8}");
    }

    Console.WriteLine($"\n  {testSuite.Length} demo(s)");
    return 0;
}

if (suiteMode)
{
    string rulesDir = FindRulesDir();
    string suiteReportDir = namedArgs.GetValueOrDefault("--report-dir") ?? "bench-reports";
    Directory.CreateDirectory(suiteReportDir);
    string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    int passed = 0, skipped = 0, failed = 0;

    foreach (TestCase tc in testSuite)
    {
        if (!File.Exists(tc.DemoPath))
        {
            Console.WriteLine($"[SKIP] {tc.Id} — demo not found");
            skipped++;
            continue;
        }

        Console.WriteLine($"\n{"═══"} {tc.Id} {"═".PadRight(70 - tc.Id.Length, '═')}");
        string reportFile = Path.Combine(suiteReportDir, $"{tc.Id}_{timestamp}.json");
        try
        {
            int result = RunBench(tc.DemoPath, rulesDir, reportFile, enableTrace, false,
                writeGolden: writeGolden, enableCounters: enableCounters, enableTimeline: enableTimeline);
            if (result == 0)
            {
                passed++;
            }
            else
            {
                failed++;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] {tc.Id}: {ex.Message}");
            failed++;
        }
    }

    Console.WriteLine($"\n{"═══"} Suite Complete {"═".PadRight(58, '═')}");
    Console.WriteLine($"  Passed: {passed}  Skipped: {skipped}  Failed: {failed}");
    Console.WriteLine($"  Reports: {Path.GetFullPath(suiteReportDir)}/");
    // An EMPTY suite is a failure, not a pass. demos/**/*.dem is gitignored, so a fresh checkout or
    // a CI runner has zero cases and every earlier line here reads exactly like a clean run: no
    // failures, no skips, a report directory. Exiting 0 makes "the benchmark suite passed" and
    // "there was nothing to benchmark" indistinguishable, which is how a perf gate silently stops
    // gating.
    if (testSuite.Length == 0)
    {
        Console.Error.WriteLine($"[FAIL] no .dem files in {benchDir}: the suite ran zero cases.");
        Console.Error.WriteLine("       Place demos there, or use --list-suite to inspect without running.");
        return 1;
    }

    return failed > 0 ? 1 : 0;
}

if (positional.Length == 0)
{
    Console.Error.WriteLine("Usage: AnalysisBench <demo.dem> [rules-dir] [options]");
    Console.Error.WriteLine("       AnalysisBench --suite [--report-dir=<dir>]");
    Console.Error.WriteLine("       AnalysisBench --list-suite");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --trace              Enable EventSource listener (adds instrumentation overhead)");
    Console.Error.WriteLine("  --counters           Attach a MeterListener (dotnet-counters-equivalent) and print counter totals");
    Console.Error.WriteLine("  --timeline           Attach an ActivityListener and print the phase timeline (read/parse/build/eval)");
    Console.Error.WriteLine("  --bare               Run without snapshots, which is the default; --export and --state-trace turn them on");
    Console.Error.WriteLine("  --golden             Write tests/fixtures/<demo-id>/ours.golden.json; untracked, and nothing reads it");
    Console.Error.WriteLine("  --mmap               With --round-debug / --shots-debug: memory-map the .dem instead of File.ReadAllBytes");
    Console.Error.WriteLine("  --ray-counters       Count rays cast / frustum-rejected pairs on the visibility path and time the raycasting");
    Console.Error.WriteLine("  --round-debug        Detailed per-round event trace");
    Console.Error.WriteLine("  --report=<path>      Write JSON report to file");
    Console.Error.WriteLine("  --export=csv|json    Export per-(player,round) stats as a MetricTable (turns snapshots on; incompatible with --bare)");
    Console.Error.WriteLine("  --out=<path>         Output file for --export (default: ./player_round_stats.<ext>)");
    Console.Error.WriteLine("  --suite              Run all test suite entries");
    Console.Error.WriteLine("  --list-suite         List test suite entries and their status");
    Console.Error.WriteLine("  --report-dir=<dir>   Output directory for suite reports (default: bench-reports)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Profiling (OFF by default; see docs/profiling.md):");
    Console.Error.WriteLine("  --profile            (runtime) turn on the entity per-phase profile tree + attach Meter/Activity listeners");
    Console.Error.WriteLine("  DEMOVIEWER_PROFILE=1 (env) same runtime switch resolved at process start + combined report on exit");
    Console.Error.WriteLine("  --trace / --counters / --timeline and the above all compose with --suite");
    return 1;
}

{
    string demoPath = positional[0];
    string rulesDir = positional.Length > 1 ? positional[1] : FindRulesDir();
    bool bareMode = flags.Contains("--bare");
    bool roundDebug = flags.Contains("--round-debug");
    string? shotsDebugPlayer = namedArgs.GetValueOrDefault("--shots-debug");
    string? reportPath = namedArgs.GetValueOrDefault("--report");
    string? stateTraceArg = namedArgs.GetValueOrDefault("--state-trace");
    // Per-round MetricTable export (csv|json). Equals-form to match --report=<path>: the bare-token
    // parser puts `--export csv` → flags{--export} + positional[1]=csv (which positional[1] reads as
    // rules-dir), so the convention-matching form is `--export=csv [--out=<path>]`.
    string? exportFormat = namedArgs.GetValueOrDefault("--export");
    string? exportOut = namedArgs.GetValueOrDefault("--out");

    // Fail fast on a bad --export value, BEFORE the full parse+eval, so a typo doesn't waste a run.
    if (exportFormat is not null
        && !string.Equals(exportFormat.Trim(), "csv", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(exportFormat.Trim(), "json", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Unknown --export format '{exportFormat}'. Expected 'csv' or 'json'.");
        return 1;
    }

    if (roundDebug)
    {
        ParsedDemo debugDemo = useMmap
            ? MemoryMappedDemoSource.ParseFile(demoPath)
            : DemoParser.Parse(File.ReadAllBytes(demoPath));
        RunRoundDebug(debugDemo);
        return 0;
    }

    if (shotsDebugPlayer is not null)
    {
        ParsedDemo debugDemo = useMmap
            ? MemoryMappedDemoSource.ParseFile(demoPath)
            : DemoParser.Parse(File.ReadAllBytes(demoPath));
        RunShotsDebug(debugDemo, shotsDebugPlayer);
        return 0;
    }

    return RunBench(demoPath, rulesDir, reportPath, enableTrace, bareMode, stateTraceArg, writeGolden,
        enableCounters, enableTimeline, exportFormat, exportOut);
}

// ── Core Bench ─────────────────────────────────────────────────────────────

// One path: DemoAnalysis.Run over the file, every frame dropped behind the evaluation loop.
static int RunBench(string demoPath, string rulesDir, string? reportPath,
    bool enableTrace, bool bareMode, string? stateTraceArg = null, bool writeGolden = false,
    bool enableCounters = false, bool enableTimeline = false, string? exportFormat = null, string? exportOut = null)
{
    FileInfo demoFileInfo = new(demoPath);
    double demoSizeMb = demoFileInfo.Length / 1024.0 / 1024.0;
    // Snapshot rows are what the stream exists to drop, so they are kept only for the consumers that
    // read them (--export, --state-trace). The run records the choice as Provenance.SnapshotsCaptured.
    bool captureSnapshots = !bareMode && (exportFormat is not null || stateTraceArg is not null);

    Console.WriteLine($"Demo:  {demoPath} ({demoSizeMb:F1} MB)");
    Console.WriteLine($"Rules: {rulesDir}");
    Console.WriteLine("Path:  forward (DemoAnalysis.Run over the file)");
    Console.WriteLine($"Mode:  {(captureSnapshots ? "full (with snapshots)" : "bare (no snapshots)")}{(enableTrace ? " + trace" : "")}");

    if (reportPath is not null)
    {
        Console.WriteLine($"Report: {reportPath}");
    }

    Console.WriteLine();

    EvaluatorListener? listener = enableTrace ? new EvaluatorListener() : null;
    // --counters: attach a MeterListener BEFORE eval so EvaluatorMetrics.Enabled flips true and the
    // evaluator's guarded Counter.Add / FrameDurationMs.Record fire (mirrors a dotnet-counters session).
    MeterCollector? counters = enableCounters ? new MeterCollector() : null;
    // --timeline: attach an ActivityListener BEFORE the phases run so the bench's own spans and the
    // library's analysis.eval ⊃ analysis.precompute spans are captured into one nested timeline.
    PhaseTimeline? phaseTimeline = enableTimeline ? new PhaseTimeline() : null;
    // DEMOVIEWER_PROFILE=1 (env): the unified one-switch runtime profile. Attaches Meter + Activity
    // listeners for the whole run and dumps a combined report on exit (disposed at method scope). The
    // same library helper the Desktop app uses; independent of the explicit flags above.
    using ProfilingSession? session = ProfilingSession.StartFromEnvironment();

    // ── Memory high-water sampler ──────────────────────────────────────────
    // Started before the read so the demo buffer's contribution is inside the window. Managed-heap
    // peak is the number the forward path is meant to move; RSS is reported alongside because mapped
    // pages are still resident (file-backed and evictable), so RSS is NOT expected to drop by the
    // file size.
    using MemorySampler memSampler = MemorySampler.Start();
    long allocatedBefore = GC.GetTotalAllocatedBytes(true);

    // Load the whole shipped rules/ dir: v1 chains land in .Config, v2 rulesets in .Rulesets.
    // Post Rulesets v2 cutover the shipped stats are all v2 rulesets, so the bench MUST compose
    // them (the v2 overload) or it evaluates an empty graph. Keep the strict shipped-tier
    // hard-fail the old LoadDirectory gave.
    RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(rulesDir);
    if (!loaded.Success)
    {
        throw new RuleConfigException(loaded.Errors);
    }

    // Ray budget counters: zeroed before the bake loads, so a --suite run reports each demo alone
    // and the bake timing still lands in this demo's numbers.
    VisibilityCounters.Reset();
    BenchOutcome outcome = RunForward(demoPath, loaded.Rulesets, captureSnapshots);
    AnalysisRun run = outcome.Run;
    DemoDescriptor facts = run.Demo;
    AnalysisProvenance provenance = run.Provenance;
    PhaseTimings timings = outcome.Timings;

    Console.WriteLine($"Map:    {facts.MapName}  |  Source: {facts.Profile.SourceKind}  |  Build: {facts.Profile.BuildNumber}  |  Server: \"{facts.ServerName}\"  |  Client: \"{facts.ClientName}\"");
    Console.WriteLine($"Ran:    {provenance.Source} source  |  profile {provenance.Profile.GetType().Name} ({provenance.ProfileResolution})  |  digest {provenance.Digest}  |  snapshots {(provenance.SnapshotsCaptured ? "captured" : "not captured")}");
    if (provenance.Dialect.BoundMarkerNeverSeen)
    {
        Console.WriteLine($"        DIALECT MISMATCH: the profile's round marker never fired (round_officially_ended x{provenance.Dialect.RoundOfficiallyEndedSeen}, cs_pre_restart x{provenance.Dialect.CsPreRestartSeen})");
    }

    // ── Player tables ──────────────────────────────────────────────────────
    // Materialisation order is arrival order on both paths, so the tables and the goldens are
    // sorted by slot rather than left to it.
    List<PlayerReport> playerReports = new();
    foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in run.MaterializedPlayers
                 .OrderBy(p => p.TemplateIndex).ThenBy(p => p.PlayerSlot))
    {
        Dictionary<string, object?> stats = new();
        foreach (PerPlayerColumnAssignment col in mp.ColumnAssignments)
        {
            string? raw = col.Node.IsActive ? col.Node.GetDisplayValue() : null;
            stats[col.ColumnName] = ParseStatValue(raw);
        }

        int team = facts.Players.TryGetValue(mp.PlayerSlot, out PlayerInfo? pi) ? pi.Team : 0;
        playerReports.Add(new PlayerReport(mp.PlayerName, mp.PlayerSlot, team, mp.TemplateIndex, stats));
    }

    // Chain event summary
    Console.WriteLine();
    Console.WriteLine("─── Rule Chain Events ───────────────────────────────────");
    IOrderedEnumerable<IGrouping<string, RuleChainEvent>> chainCounts = run.Timeline.Events.GroupBy(e => e.ChainName).OrderBy(g => g.Key);
    foreach (IGrouping<string, RuleChainEvent> g in chainCounts)
    {
        Console.WriteLine($"  {g.Key,-30} {g.Count(),5} events");
    }

    // State-machine transition trace (--state-trace=name1,name2,...)
    if (!string.IsNullOrEmpty(stateTraceArg))
    {
        if (run.Snapshots is { } snapshots)
        {
            PrintStateTrace(snapshots, stateTraceArg);
        }
        else
        {
            Console.WriteLine("--state-trace needs snapshots; it is incompatible with --bare.");
        }
    }

    // Per-template player tables
    PrintPlayerTables(playerReports);

    VisibilityCountersSnapshot rays = VisibilityCounters.Snapshot();

    // ── Summary ────────────────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("─── Performance Summary ─────────────────────────────────");
    Console.WriteLine($"  Hash:   {timings.Read.TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  Bake:   {timings.Bake.TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  Run:    {timings.Eval.TotalMilliseconds,8:F1} ms  (open+build+decode+eval in one bracket)");
    Console.WriteLine($"  ── load (hash+run): {(timings.Read + timings.Total).TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  Total (run): {timings.Total.TotalMilliseconds,8:F1} ms");
    Console.WriteLine();
    Console.WriteLine($"  GC Gen0: {outcome.GcAfter.Gen0 - outcome.GcBefore.Gen0}  Gen1: {outcome.GcAfter.Gen1 - outcome.GcBefore.Gen1}  Gen2: {outcome.GcAfter.Gen2 - outcome.GcBefore.Gen2}");
    Console.WriteLine($"     Eval: Gen0={outcome.GcAfter.Gen0 - outcome.EvalGcBefore.Gen0}  Gen1={outcome.GcAfter.Gen1 - outcome.EvalGcBefore.Gen1}");
    Console.WriteLine($"  Eval allocated: {outcome.EvalAllocBytes / (1024.0 * 1024.0),8:F1} MiB on this thread, {outcome.AllocBytesAllThreads / (1024.0 * 1024.0),8:F1} MiB on all threads");

    // ── Memory ─────────────────────────────────────────────────────────────
    // RSS stays well above the heap figure: the reader maps the file, so its pages are resident but
    // file-backed and evictable rather than dirty heap the GC must trace.
    long allocatedTotal = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
    Console.WriteLine();
    Console.WriteLine($"  Peak managed heap:  {memSampler.PeakManagedHeapBytes / (1024.0 * 1024.0),9:F1} MiB");
    Console.WriteLine($"  Peak process RSS:   {memSampler.PeakRssBytes / (1024.0 * 1024.0),9:F1} MiB");
    Console.WriteLine($"  Total allocated:    {allocatedTotal / (1024.0 * 1024.0),9:F1} MiB");
    Console.WriteLine($"  Managed heap now:   {GC.GetTotalMemory(false) / (1024.0 * 1024.0),9:F1} MiB");

    // ── Entity-tracking sub-phase profile ────────────────────────────────────
    // Populated only when a profiled run captured entity data (Profiling.Enabled, runtime gated
    // Stopwatch accumulators). The intervals nest, so they are printed as a tree with an explicit
    // unattributed remainder at each level; the remainder is what reveals a missing sub-phase.
    EntityProfilingSnapshot prof = run.Build.EntityScanner?.Layer.Tracker.GetProfilingSnapshot() ?? default;
    ScannerProfilingSnapshot sprof = run.Build.EntityScanner?.GetProfilingSnapshot() ?? default;
    PrintEntityProfile(prof, sprof);
    PrintRayCounters(rays, timings.Eval, VisibilityCounters.Enabled);

    if (listener is not null)
    {
        Console.WriteLine();
        listener.PrintSummary();
        listener.Dispose();
    }

    if (counters is not null)
    {
        Console.WriteLine();
        counters.PrintSummary();
        counters.Dispose();
    }

    if (phaseTimeline is not null)
    {
        Console.WriteLine();
        phaseTimeline.PrintSummary();
        phaseTimeline.Dispose();
    }

    // ── JSON Report ────────────────────────────────────────────────────────
    if (reportPath is not null)
    {
        string gitCommit = GetGitCommit();
        BenchReport report = new(
            new ReportMetadata(
                DateTimeOffset.UtcNow,
                gitCommit,
                Path.GetFileName(demoPath),
                Math.Round(demoSizeMb, 1),
                outcome.Sha256,
                facts.MapName,
                facts.Players.Count(p => p.Value.Team is 2 or 3),
                facts.TickCount,
                facts.TickRate,
                Math.Round(facts.Duration.TotalSeconds, 1),
                GetMachineInfo()
            ),
            new ReportPerformance(
                Math.Round(timings.Eval.TotalMilliseconds, 1),
                Math.Round(timings.Total.TotalMilliseconds, 1),
                Math.Round(timings.Bake.TotalMilliseconds, 1),
                provenance.FramesConsumed,
                provenance.MessagesConsumed,
                run.Build.Nodes.Count,
                run.Build.Edges.Count,
                run.MaterializedPlayers.Count,
                run.Timeline.Events.Count,
                new GcReport(
                    outcome.GcAfter.Gen0 - outcome.GcBefore.Gen0,
                    outcome.GcAfter.Gen1 - outcome.GcBefore.Gen1,
                    outcome.GcAfter.Gen2 - outcome.GcBefore.Gen2,
                    outcome.GcAfter.Gen0 - outcome.EvalGcBefore.Gen0,
                    outcome.GcAfter.Gen1 - outcome.EvalGcBefore.Gen1,
                    outcome.EvalAllocBytes,
                    outcome.AllocBytesAllThreads
                ),
                BuildEntityProfileReport(prof, sprof),
                BuildRayReport(rays, timings.Eval, VisibilityCounters.Enabled)
            ),
            new ReportProvenance(
                provenance.Source.ToString(),
                provenance.Profile.GetType().Name,
                provenance.ProfileResolution.ToString(),
                provenance.Digest.ToString(),
                provenance.SnapshotsCaptured,
                provenance.FramesConsumed,
                provenance.MessagesConsumed,
                provenance.Dialect.RoundOfficiallyEndedSeen,
                provenance.Dialect.CsPreRestartSeen,
                provenance.Dialect.BoundMarkerNeverSeen
            ),
            playerReports
        );

        string json = JsonSerializer.Serialize(report, JsonOpts.Default);
        File.WriteAllText(reportPath, json);
        Console.WriteLine();
        Console.WriteLine($"Report written to {reportPath}");
    }

    // ── Golden-stats file (--golden) ──────────────────────────────────────
    if (writeGolden && playerReports.Count > 0)
    {
        WriteGoldenStatsFiles(demoPath, outcome.Sha256, facts, playerReports);
    }

    // ── Per-round MetricTable export (--export=csv|json [--out=<path>]) ──────
    // Runs the PlayerRoundStatsProjector over the snapshot result and writes one file per emitted
    // table. Needs snapshot mode (the projector reads MessageSnapshots), so it is incompatible with
    // --bare; on the forward path asking for it is what turns snapshots on.
    if (exportFormat is not null)
    {
        WriteRoundExport(exportFormat, exportOut, demoPath, run);
    }

    return 0;
}

// The forward path: DemoAnalysis.Run over the file. Open, resolve the profile, build, narrow the
// decode to what the graph consumes, evaluate while frames are dropped behind the loop. One
// bracket, since the reader and the evaluator overlap and no phase boundary exists to time.
static BenchOutcome RunForward(string demoPath, IReadOnlyList<RulesetDoc> rulesets, bool captureSnapshots)
{
    // The bake wants the map before the run and the run opens its own reader, so the header is
    // probed on a throwaway one: a mapping and the signon frames, nothing decoded past them.
    string mapName;
    using (DemoReader probe = DemoReader.OpenFile(demoPath))
    {
        mapName = probe.Enrichment.MapName;
    }

    // Hashed off a stream so the file never lands on the managed heap. The report and the golden
    // key on it.
    long hashStart = Stopwatch.GetTimestamp();
    string sha256;
    using (FileStream file = File.OpenRead(demoPath))
    {
        sha256 = Convert.ToHexStringLower(SHA256.HashData(file));
    }

    TimeSpan hashElapsed = Stopwatch.GetElapsedTime(hashStart);
    Console.WriteLine($"Hash:   {hashElapsed.TotalMilliseconds,8:F1} ms  |  {new FileInfo(demoPath).Length / 1024 / 1024} MB");

    (VisibilityEngine? visibility, TimeSpan bakeElapsed) = LoadBake(mapName);

    // ── Run ────────────────────────────────────────────────────────────────
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GcCounts gcBefore = GcCounts.Now();
    // The evaluator loop runs on this thread; the reader and the digest workers allocate on their
    // own, so the all-threads figure is the one to compare across runs.
    long allocAllBefore = GC.GetTotalAllocatedBytes(true);
    long runAllocBefore = GC.GetAllocatedBytesForCurrentThread();
    long runStart = Stopwatch.GetTimestamp();
    AnalysisRun run;
    using (AnalysisDiagnostics.ActivitySource.StartActivity("run"))
    {
        run = DemoAnalysis.Run(demoPath, rulesets, new AnalysisOptions
        {
            VisibilityEngine = visibility,
            CaptureSnapshots = captureSnapshots
        });
    }

    TimeSpan runElapsed = Stopwatch.GetElapsedTime(runStart);
    long runAllocBytes = GC.GetAllocatedBytesForCurrentThread() - runAllocBefore;
    long allocAllBytes = GC.GetTotalAllocatedBytes(true) - allocAllBefore;
    GcCounts gcAfter = GcCounts.Now();
    Console.WriteLine($"Run:    {runElapsed.TotalMilliseconds,8:F1} ms  |  {run.Provenance.FramesConsumed} frames, {run.Provenance.MessagesConsumed} messages, {run.Demo.Players.Count} players");
    Console.WriteLine($"Build:  {run.Build.Nodes.Count} scaffolding nodes, {run.Build.Edges.Count} edges  |  {run.MaterializedPlayers.Count} materialized players");
    PrintGraphSize(run);

    return new BenchOutcome(run, sha256,
        new PhaseTimings(hashElapsed, runElapsed, bakeElapsed),
        gcBefore, gcBefore, gcAfter, runAllocBytes, allocAllBytes);
}

// The bake is what lets the builder synthesize enemy_spotted. Without it every visibility-gated aim
// stat reads zero and the graph is materially cheaper than the one the app runs. Null when the map
// has no bake, which is a real and common shape rather than a failure.
static (VisibilityEngine? Engine, TimeSpan Elapsed) LoadBake(string mapName)
{
    long start = Stopwatch.GetTimestamp();
    string? tris = CollisionSoup.Find(mapName);
    VisibilityEngine? engine = tris is null ? null : CollisionSoup.Load(tris);
    TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
    Console.WriteLine(engine is null
        ? $"Visibility: no collision bake for {mapName} (visibility-gated stats stay empty)"
        : $"Visibility: bake loaded for {mapName} in {elapsed.TotalMilliseconds:F1} ms");
    return (engine, elapsed);
}

// State-machine transition trace over the snapshot rows (--state-trace=name1,name2,...).
static void PrintStateTrace(EvaluationResult result, string stateTraceArg)
{
    string[] wantedNames = stateTraceArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Console.WriteLine();
    Console.WriteLine("─── State Trace ─────────────────────────────────────────");
    for (int n = 0; n < result.FinalTrackedNodes.Count; n++)
    {
        StateNode node = result.FinalTrackedNodes[n];
        bool match = false;
        foreach (string w in wantedNames)
        {
            if (string.Equals(node.Name, w, StringComparison.OrdinalIgnoreCase))
            {
                match = true;
                break;
            }
        }

        if (!match)
        {
            continue;
        }

        Console.WriteLine($"  {node.Name}:");
        bool? prevActive = null;
        string? prevValue = null;
        int transitions = 0;
        for (int m = 0; m < result.MessageSnapshots.Count; m++)
        {
            if (n >= result.MessageSnapshots.Width)
            {
                continue;
            }

            NodeSnapshot s = result.MessageSnapshots[m, n];
            if (prevActive == s.IsActive && prevValue == s.DisplayValue)
            {
                continue;
            }

            MessageRef msg = result.Messages[m];
            Console.WriteLine($"    [tick {msg.Tick,8} msg {msg.Message.GetType().Name,-30}]  IsActive={s.IsActive,-5}  Value={s.DisplayValue}");
            prevActive = s.IsActive;
            prevValue = s.DisplayValue;
            transitions++;
        }

        Console.WriteLine($"    ({transitions} transitions)");
    }
}

// ── Golden-stats producer (--golden) ──────────────────────────────────────
//
// Writes tests/fixtures/<demo-id>/ours.golden.json, a snapshot of what this run produced. No test
// reads it, and the file is untracked: it is here for a hand comparison between two builds. The
// fixture directory's committed expected.golden.json is a different file and must not be touched,
// because its presence is what selects the benchmark demos (LiveAimStats.BenchmarkDemoIds).
static void WriteGoldenStatsFiles(
    string demoPath, string demoSha256, DemoDescriptor demo,
    List<PlayerReport> playerReports)
{
    string demoId = Path.GetFileNameWithoutExtension(demoPath);
    string fixturesRoot = Path.Combine(FindRepoRoot(), "tests", "fixtures", demoId);
    Directory.CreateDirectory(fixturesRoot);

    // ── ours.golden.json ───────────────────────────────────────────────────
    List<PlayerStatsInput> oursInputs = playerReports
        .Select(p => new PlayerStatsInput(
            p.Name,
            p.Team,
            p.Slot,
            p.Stats))
        .ToList();

    // The converter reads the match facts off a ParsedDemo; the descriptor carries the same two on
    // both paths, so they are set here and the converter gets none.
    GoldenStatsDocument ours = OursGoldenStatsConverter.Convert(
        Path.GetFileName(demoPath),
        demoSha256,
        null,
        oursInputs,
        GetGitCommit()) with
    {
        Match = new MatchMetadata(demo.MapName, demo.TickCount)
    };

    string oursPath = Path.Combine(fixturesRoot, "ours.golden.json");
    GoldenStatsSerializer.WriteToFile(ours, oursPath);
    Console.WriteLine($"Golden: {Path.GetRelativePath(FindRepoRoot(), oursPath)}");
}

// Runs the PlayerRoundStatsProjector over the snapshot result and writes one file per emitted
// MetricTable in the requested format. Sibling of WriteGoldenStatsFiles: per-round, not per-game.
static void WriteRoundExport(string format, string? outPath, string demoPath, AnalysisRun run)
{
    if (run.Snapshots is not { } result)
    {
        Console.Error.WriteLine("--export requires snapshot mode; it is incompatible with --bare.");
        return;
    }

    IOutputFormatter formatter = OutputFormatterRegistry.Get(format)
                                 ?? throw new ArgumentException(
                                     $"Unknown --export format '{format}'. Expected one of: {string.Join(", ", OutputFormatterRegistry.Ids)}.",
                                     nameof(format));

    PlayerRoundStatsProjector projector = new()
    {
        MatchId = Path.GetFileName(demoPath)
    };
    IReadOnlyList<MetricTable> tables = projector.Project(result, run.Demo);

    Console.WriteLine();
    foreach (MetricTable table in tables)
    {
        // --out names the file for a single-table projector; for multi-table output (future
        // projectors) it is treated as a directory. PlayerRoundStatsProjector emits exactly one table.
        string path;
        if (outPath is not null && tables.Count == 1)
        {
            path = outPath;
        }
        else
        {
            string dir = outPath ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"{table.Name}.{formatter.FileExtension}");
        }

        formatter.WriteToFile(table, path);
        Console.WriteLine($"Export: {path}  ({table.Rows.Count} rows, {table.ValueColumns.Count} value columns)");
    }
}

// ── Helpers ────────────────────────────────────────────────────────────────

// What the Analysis tab actually draws, which is the scaffolding plus ONE player's nodes, and what
// drawing every player would cost. Neither is derivable from the build counts: the per-player nodes
// only exist once evaluation has materialized them.
static void PrintGraphSize(AnalysisRun run)
{
    int materializedNodes = run.MaterializedPlayers.Sum(p => p.Nodes.Count);
    int lowestSlot = run.MaterializedPlayers.Count > 0 ? run.MaterializedPlayers.Min(p => p.PlayerSlot) : -1;
    int drawnNodes = run.Build.Nodes.Count
                     + run.MaterializedPlayers.Where(p => p.PlayerSlot == lowestSlot).Sum(p => p.Nodes.Count);
    Console.WriteLine($"Graph:  {drawnNodes,8} nodes drawn (scaffolding + slot {lowestSlot})  |  "
                      + $"{run.Build.Nodes.Count + materializedNodes} if every player expanded");
}

static void PrintPlayerTables(List<PlayerReport> playerReports)
{
    IOrderedEnumerable<IGrouping<int, PlayerReport>> byTemplate = playerReports.GroupBy(p => p.TemplateIndex).OrderBy(g => g.Key);
    foreach (IGrouping<int, PlayerReport> tpl in byTemplate)
    {
        Console.WriteLine();
        Console.WriteLine($"─── Template {tpl.Key} ──────────────────────────────────────");
        List<PlayerReport> players = tpl.ToList();
        if (players.Count == 0 || players[0].Stats.Count == 0)
        {
            continue;
        }

        List<string> colNames = players[0].Stats.Keys.ToList();
        int nameWidth = Math.Max(6, players.Max(p => p.Name.Length) + 1);
        List<int> colWidths = colNames.Select(c =>
            Math.Max(c.Length + 1, players.Max(p => FormatStat(p.Stats[c]).Length) + 1)
        ).ToList();

        Console.Write($"  {"Player".PadRight(nameWidth)}");
        for (int i = 0; i < colNames.Count; i++)
        {
            Console.Write(colNames[i].PadLeft(colWidths[i]));
        }

        Console.WriteLine();

        Console.Write($"  {"".PadRight(nameWidth, '─')}");
        for (int i = 0; i < colNames.Count; i++)
        {
            Console.Write("".PadLeft(colWidths[i], '─'));
        }

        Console.WriteLine();

        foreach (PlayerReport p in players.Where(p => p.Team is 2 or 3))
        {
            Console.Write($"  {p.Name.PadRight(nameWidth)}");
            for (int i = 0; i < colNames.Count; i++)
            {
                Console.Write(FormatStat(p.Stats[colNames[i]]).PadLeft(colWidths[i]));
            }

            Console.WriteLine();
        }
    }
}

static object? ParseStatValue(string? raw)
{
    if (raw is null or "-" or "")
    {
        return null;
    }

    if (raw == "ON")
    {
        return true;
    }

    if (int.TryParse(raw, out int i))
    {
        return i;
    }

    if (double.TryParse(raw, out double d))
    {
        return d;
    }

    return raw;
}

static string FormatStat(object? value)
{
    return value switch
    {
        null => "-",
        true => "ON",
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(d == Math.Truncate(d) ? "F0" : "F2", CultureInfo.InvariantCulture),
        string s => s,
        _ => value.ToString() ?? "-"
    };
}

static string GetGitCommit()
{
    return RunCommand("git", "rev-parse --short HEAD") ?? "unknown";
}

static void RunRoundDebug(ParsedDemo demo)
{
    bool live = false;
    int rn = 0;
    int ends = 0;
    Dictionary<string, int> dmgByPlayer = new();
    Dictionary<int, int> victimHpTracker = new();

    foreach (DemoFrame frame in demo.Frames)
    {
        foreach (NetMessage msg in frame.InnerMessages)
        {
            if (msg is not GameEventMessage gem)
            {
                continue;
            }

            GameEvent e = gem.DecodedEvent;
            if (e.Payload is BeginNewMatchEvent)
            {
                live = true;
                Console.WriteLine($"  [tick {frame.ServerTick,7}] begin_new_match");
            }

            if (e.Payload is RoundFreezeEndEvent)
            {
                if (live)
                {
                    rn++;
                }

                Console.WriteLine($"  [tick {frame.ServerTick,7}] round_freeze_end  (round {rn}{(live ? "" : " WARMUP")})");
            }

            if (e.Payload is RoundOfficiallyEndedEvent)
            {
                ends++;
                Console.WriteLine($"  [tick {frame.ServerTick,7}] round_officially_ended  (end #{ends})");
            }

            if (e.Payload is AnnouncePhaseEndEvent)
            {
                Console.WriteLine($"  [tick {frame.ServerTick,7}] match_end");
            }

            if (e.Payload is PlayerDeathEvent death)
            {
                string killer = demo.Players.TryGetValue(death.Attacker, out PlayerInfo? ki) ? ki.Name : $"slot{death.Attacker}";
                string victim = demo.Players.TryGetValue(death.UserId, out PlayerInfo? vi) ? vi.Name : $"slot{death.UserId}";
                string assister = death.Assister >= 0 && demo.Players.TryGetValue(death.Assister, out PlayerInfo? asi) ? asi.Name : "";
                Console.WriteLine($"    R{rn} kill: {killer} -> {victim}{(assister != "" ? $" (assist: {assister})" : "")}{(death.Headshot ? " HS" : "")} [{death.Weapon}]");
            }

            if (e.Payload is PlayerHurtEvent hurt && live)
            {
                string attacker = demo.Players.TryGetValue(hurt.Attacker, out PlayerInfo? ai) ? ai.Name : $"slot{hurt.Attacker}";
                if (!dmgByPlayer.ContainsKey(attacker))
                {
                    dmgByPlayer[attacker] = 0;
                }

                int preHp = victimHpTracker.GetValueOrDefault(hurt.UserId, 100);
                int capped = hurt.Health > 0 ? hurt.DmgHealth : Math.Min(hurt.DmgHealth, preHp);
                dmgByPlayer[attacker] += capped;
                victimHpTracker[hurt.UserId] = hurt.Health > 0 ? hurt.Health : 100;
            }

            if (e.Payload is RoundFreezeEndEvent && live)
            {
                victimHpTracker.Clear();
            }
        }
    }

    Console.WriteLine($"\n  Rounds started: {rn}, Rounds ended: {ends}, Missing ends: {rn - ends}");
    Console.WriteLine("\n  === Capped Damage Totals ===");
    foreach (KeyValuePair<string, int> kv in dmgByPlayer.OrderByDescending(kv => kv.Value))
    {
        Console.WriteLine($"    {kv.Key,-30} {kv.Value,6}");
    }
}

static void RunShotsDebug(ParsedDemo demo, string playerName)
{
    List<KeyValuePair<int, PlayerInfo>> matches = demo.Players.Where(kv => kv.Value.Name.Contains(playerName, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0)
    {
        Console.WriteLine($"  No players found matching '{playerName}'.");
        Console.WriteLine($"  Available: {string.Join(", ", demo.Players.Values.Select(p => p.Name))}");
        return;
    }

    int slot = matches[0].Key;
    string fullName = matches[0].Value.Name;
    Console.WriteLine($"  Player: {fullName} (slot {slot})\n");

    bool live = false;
    int rn = 0;
    bool roundActive = false;
    bool combatActive = false;
    bool aliveThisRound = true;
    int shotsCount = 0;
    int hitFoeCount = 0;
    int postDeathShots = 0;
    int rawDmgSum = 0; // sum of hurt.DmgHealth (uncapped)
    int cappedDmgSum = 0; // sum of cap(hurt.DmgHealth, preHitHp) using event-cache HP
    Dictionary<string, int> weaponHist = new();
    Dictionary<int, int> teamBySlot = new();
    Dictionary<int, int> hpBySlot = new(); // event-cache HP (resets to 100 on round start / death)
    foreach (KeyValuePair<int, PlayerInfo> kv in demo.Players)
    {
        teamBySlot[kv.Key] = kv.Value.Team;
        hpBySlot[kv.Key] = 100;
    }

    int lastHurtTick = -1;
    int lastHurtVictim = -1;

    foreach (DemoFrame frame in demo.Frames)
    {
        foreach (NetMessage msg in frame.InnerMessages)
        {
            if (msg is not GameEventMessage gem)
            {
                continue;
            }

            GameEvent e = gem.DecodedEvent;
            if (e.Payload is BeginNewMatchEvent)
            {
                live = true;
            }

            if (e.Payload is RoundFreezeEndEvent)
            {
                if (live)
                {
                    rn++;
                    roundActive = true;
                }
                else
                {
                    roundActive = true;
                }

                aliveThisRound = true;
                // Reset everyone to 100 HP at round start (matches PlayerContextIndex.ResetRoundState)
                foreach (int k in hpBySlot.Keys.ToList())
                {
                    hpBySlot[k] = 100;
                }
            }

            if (e.Payload is BuyTimeEndedEvent && live)
            {
                combatActive = true;
            }

            if (e.Payload is RoundOfficiallyEndedEvent)
            {
                roundActive = false;
                combatActive = false;
            }

            if (e.Payload is PlayerTeamEvent pt)
            {
                teamBySlot[pt.UserId] = pt.Team;
            }

            if (e.Payload is PlayerDeathEvent death && death.UserId == slot)
            {
                aliveThisRound = false;
                string suicide = death.Attacker == death.UserId ? " (SUICIDE)" : "";
                string noKiller = death.Attacker < 0 ? " (NO_KILLER/WORLD)" : "";
                string phaseTag = $"{(live ? "LIVE" : "WARMUP")} {(roundActive ? "ROUND-ACTIVE" : "ROUND-INACTIVE")}";
                Console.WriteLine($"  [tick {frame.GameTick,7}] R{rn,2} {phaseTag,-30} PLAYER_DEATH (killer=slot{death.Attacker} weapon={death.Weapon}){suicide}{noKiller}");
            }

            if (e.Payload is WeaponFireEvent fire && fire.UserId == slot)
            {
                string weapon = fire.Weapon ?? "?";
                string aliveTag = aliveThisRound ? "ALIVE" : "DEAD!";
                string phase = $"{(live ? "LIVE" : "WARMUP")} {(roundActive ? "ROUND" : "FREEZE")} {(combatActive ? "COMBAT" : "BUY")} {aliveTag}";
                shotsCount++;
                if (!aliveThisRound)
                {
                    postDeathShots++;
                }

                weaponHist[weapon] = weaponHist.GetValueOrDefault(weapon, 0) + 1;
                Console.WriteLine($"  [tick {frame.GameTick,7}] R{rn,2} {phase,-28} weapon_fire {weapon}  (#{shotsCount})");
            }

            if (e.Payload is PlayerHurtEvent hurt)
            {
                // Capture preHitHp BEFORE updating the event-cache (mirrors HurtTeamEnrichmentEdge).
                int preHitHp = hpBySlot.GetValueOrDefault(hurt.UserId, 100);

                // Damage accounting + verbose output for hits where this player is the attacker.
                if (hurt.Attacker == slot && hurt.UserId != slot)
                {
                    int aTeam = teamBySlot.GetValueOrDefault(hurt.Attacker, 0);
                    int vTeam = teamBySlot.GetValueOrDefault(hurt.UserId, 0);
                    bool isFoe = aTeam != vTeam && aTeam > 1;
                    bool dedup = lastHurtTick == frame.ServerTick && lastHurtVictim == hurt.UserId;

                    if (dedup)
                    {
                        Console.WriteLine($"  [tick {frame.GameTick,7}] R{rn,2} (DEDUP)                       player_hurt   {hurt.Weapon} -> slot{hurt.UserId}  pellet");
                    }
                    else
                    {
                        lastHurtTick = frame.ServerTick;
                        lastHurtVictim = hurt.UserId;
                        if (isFoe)
                        {
                            hitFoeCount++;
                            int capped = hurt.Health > 0 ? hurt.DmgHealth : Math.Min(hurt.DmgHealth, preHitHp);
                            rawDmgSum += hurt.DmgHealth;
                            cappedDmgSum += capped;
                            bool kill = hurt.Health == 0;
                            string phaseTag = $"{(live ? "LIVE" : "WARMUP")} {(roundActive ? "ROUND" : "FREEZE")}";
                            Console.WriteLine($"  [tick {frame.GameTick,7}] R{rn,2} {phaseTag,-22} FOE  {hurt.Weapon,-22} -> slot{hurt.UserId} preHP={preHitHp,3} dmg={hurt.DmgHealth,3} postHP={hurt.Health,3} cap={capped,3} {(kill ? "KILL!" : "")}");
                        }
                    }
                }

                // Always update the per-victim HP cache (matches HurtTeamEnrichmentEdge.SetHealth).
                hpBySlot[hurt.UserId] = hurt.Health > 0 ? hurt.Health : 100;
            }
        }
    }

    Console.WriteLine($"\n  TOTAL weapon_fire events for {fullName}: {shotsCount}");
    Console.WriteLine($"  TOTAL hit-foe (post-dedup): {hitFoeCount}");
    Console.WriteLine($"  TOTAL post-death shots (DEAD! tag): {postDeathShots}");
    Console.WriteLine($"  TOTAL enemy raw damage (sum DmgHealth):    {rawDmgSum}");
    Console.WriteLine($"  TOTAL enemy capped damage (current formula): {cappedDmgSum}");
    Console.WriteLine($"  Overkill cap reduces total by:              {rawDmgSum - cappedDmgSum}");
    Console.WriteLine("\n  Weapon histogram:");
    foreach (KeyValuePair<string, int> kv in weaponHist.OrderByDescending(kv => kv.Value))
    {
        Console.WriteLine($"    {kv.Key,-30} {kv.Value,4}");
    }
}

static MachineInfo GetMachineInfo()
{
    string cpu = "Unknown";
    int physicalCores = Environment.ProcessorCount;
    int logicalCores = Environment.ProcessorCount;

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        cpu = RunCommand("sysctl", "-n machdep.cpu.brand_string") ?? cpu;
        if (int.TryParse(RunCommand("sysctl", "-n hw.physicalcpu"), out int pc))
        {
            physicalCores = pc;
        }

        if (int.TryParse(RunCommand("sysctl", "-n hw.logicalcpu"), out int lc))
        {
            logicalCores = lc;
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        cpu = RunCommand("lscpu", "") ?? cpu;
    }

    return new MachineInfo(
        RuntimeInformation.OSDescription,
        Environment.OSVersion.VersionString,
        RuntimeInformation.OSArchitecture.ToString(),
        cpu,
        physicalCores,
        logicalCores,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        RuntimeInformation.FrameworkDescription
    );
}

static string? RunCommand(string command, string args)
{
    try
    {
        ProcessStartInfo psi = new(command, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using Process? proc = Process.Start(psi);
        string output = proc?.StandardOutput.ReadToEnd().Trim() ?? "";
        proc?.WaitForExit();
        return string.IsNullOrEmpty(output) ? null : output;
    }
    catch
    {
        return null;
    }
}

static string FindRepoRoot()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return AppContext.BaseDirectory;
}

static TestCase[] DiscoverTestSuite(string benchDir)
{
    if (!Directory.Exists(benchDir))
    {
        return [];
    }

    return Directory.GetFiles(benchDir, "*.dem")
        .Order(StringComparer.OrdinalIgnoreCase)
        .Select(demoPath =>
        {
            string id = Path.GetFileNameWithoutExtension(demoPath);
            return new TestCase(id, demoPath);
        })
        .ToArray();
}

// Shared resolution (env override → packaged rules/ → repo-walk); the bench deliberately loads
// the SHIPPED tier only — golden baselines and parity numbers must not vary with the local
// user's rule overlay. Pass an explicit rules dir positional to bench alternate rule sets.
static string FindRulesDir()
{
    return RuleSetLocator.ResolveShippedRulesDirectory();
}

// ── Entity-tracking profile ──────────────────────────────────────────────────

// Prints the entity-decode sub-phase breakdown as a nested tree. The intervals NEST
// (scanner seek ⊇ PacketEntities decode ⊇ field-path + field-value + descriptor-build), so
// each level shows its children indented plus an explicit unattributed remainder — the
// remainder is the tell for a sub-phase that isn't being captured.
static void PrintEntityProfile(EntityProfilingSnapshot prof, ScannerProfilingSnapshot sprof)
{
    Console.WriteLine();
    Console.WriteLine("─── Entity-Tracking Profile ─────────────────────────────");
    if (!prof.Enabled && !sprof.Enabled)
    {
        Console.WriteLine("  (no data — rerun with --profile or DEMOVIEWER_PROFILE=1 to enable)");
        return;
    }

    static double Ms(long ticks)
    {
        return (double)ticks / Stopwatch.Frequency * 1000.0;
    }

    static double Mib(long bytes)
    {
        return bytes / (1024.0 * 1024.0);
    }

    double seek = Ms(sprof.SeekTicks);
    double poll = Ms(sprof.ProviderPollTicks);
    double proj = Ms(sprof.ProjectileScanTicks);
    double snap = Ms(sprof.SnapshotTicks);
    double scannerTotal = seek + poll + proj + snap;
    double scannerAlloc = Mib(sprof.SeekAlloc + sprof.ProviderPollAlloc + sprof.ProjectileScanAlloc + sprof.SnapshotAlloc);

    double fold = Ms(sprof.FoldTicks);
    Console.WriteLine($"  Digest fold (Σ workers)    {fold,9:F1} ms  {Mib(sprof.FoldAlloc),9:F1} MiB   (producer worker time, not wall-clock)");
    Console.WriteLine($"  AdvanceAndPoll (Σ phases)  {scannerTotal,9:F1} ms  {scannerAlloc,9:F1} MiB   over {sprof.FramesPolled:N0} frames");
    Console.WriteLine($"    ├─ pre-frame snapshot    {snap,9:F1} ms  {Mib(sprof.SnapshotAlloc),9:F1} MiB");
    Console.WriteLine($"    └─ layer seek            {seek,9:F1} ms  {Mib(sprof.SeekAlloc),9:F1} MiB");

    if (!prof.Enabled)
    {
        return;
    }

    double pe = Ms(prof.PacketEntitiesTicks);
    double fpath = Ms(prof.FieldPathTicks);
    double fval = Ms(prof.FieldValueTicks);
    double dbuild = Ms(prof.DescriptorBuildTicks);

    Console.WriteLine($"        ├─ PacketEntities      {pe,9:F1} ms  {Mib(prof.PacketEntitiesAlloc),9:F1} MiB   over {prof.PacketEntitiesCount:N0} packets, {prof.EntityFieldReads:N0} entity reads");
    Console.WriteLine($"        │   ├─ field-path      {fpath,9:F1} ms  {Mib(prof.FieldPathAlloc),9:F1} MiB");
    Console.WriteLine($"        │   ├─ field-value     {fval,9:F1} ms  {Mib(prof.FieldValueAlloc),9:F1} MiB");
    Console.WriteLine($"        │   ├─ descriptor build{dbuild,9:F1} ms  {Mib(prof.DescriptorBuildAlloc),9:F1} MiB   ({prof.DescriptorBuilds:N0} builds)");
    Console.WriteLine($"        │   └─ prelude/other   {pe - fpath - fval - dbuild,9:F1} ms  {Mib(prof.PacketEntitiesAlloc - prof.FieldPathAlloc - prof.FieldValueAlloc - prof.DescriptorBuildAlloc),9:F1} MiB");
    Console.WriteLine($"        └─ other net msgs      {seek - pe,9:F1} ms  {Mib(sprof.SeekAlloc - prof.PacketEntitiesAlloc),9:F1} MiB");
}

// Converts the raw Stopwatch-tick snapshots into the milliseconds-based report record. Returns
// null (omitted from the JSON) when no profiled run captured entity data (Profiling.Enabled was off).
static ReportEntityProfile? BuildEntityProfileReport(EntityProfilingSnapshot prof, ScannerProfilingSnapshot sprof)
{
    if (!prof.Enabled && !sprof.Enabled)
    {
        return null;
    }

    static double Ms(long ticks)
    {
        return Math.Round((double)ticks / Stopwatch.Frequency * 1000.0, 2);
    }

    return new ReportEntityProfile(
        Ms(sprof.FoldTicks),
        Ms(sprof.SeekTicks),
        Ms(sprof.ProviderPollTicks),
        Ms(sprof.ProjectileScanTicks),
        Ms(sprof.SnapshotTicks),
        Ms(prof.PacketEntitiesTicks),
        Ms(prof.FieldPathTicks),
        Ms(prof.FieldValueTicks),
        Ms(prof.DescriptorBuildTicks),
        sprof.FoldAlloc,
        sprof.SeekAlloc,
        sprof.ProviderPollAlloc,
        sprof.ProjectileScanAlloc,
        sprof.SnapshotAlloc,
        prof.PacketEntitiesAlloc,
        prof.FieldPathAlloc,
        prof.FieldValueAlloc,
        prof.DescriptorBuildAlloc,
        sprof.FramesPolled,
        prof.PacketEntitiesCount,
        prof.EntityFieldReads,
        prof.DescriptorBuilds);
}

// ── Visibility ray budget ─────────────────────────────────────────────────

// The measured ray budget behind the aim-rating visibility path. Every figure comes from
// VisibilityCounters, which the library leaves off unless --ray-counters set it; without the flag
// this prints one line saying so. "Frustum-rejected pairs" are directed enemy pairs with no body
// anchor inside the viewer's frustum: a frustum-first gate would cast nothing for them, and the
// rays the current code spends on them are the ones such a gate removes outright.
static void PrintRayCounters(VisibilityCountersSnapshot rays, TimeSpan evalElapsed, bool enabled)
{
    Console.WriteLine();
    Console.WriteLine("─── Visibility Ray Budget ───────────────────────────────");

    // Printed before the ray-counter gate on purpose. Bake loading rides Profiling.Enabled, not
    // VisibilityCounters.Enabled, because it costs one Stopwatch pair per map rather than per ray, so
    // a --profile run without --ray-counters still accounts for it. On the biggest bake this is about
    // 0.9 s against a 2.3 s eval, which is too large a line item to leave off a profile.
    if (rays.BakesLoaded > 0)
    {
        Console.WriteLine(
            $"  Collision bakes loaded:     {rays.BakesLoaded,12:N0}   {rays.BakeTriangles:N0} triangles");
        Console.WriteLine(
            $"    read from disk:           {rays.BakeLoadMs,12:F1} ms");
        Console.WriteLine(
            $"    BVH build:                {rays.BvhBuildMs,12:F1} ms"
            + $"   ({(rays.BakeLoadMs + rays.BvhBuildMs) / Math.Max(evalElapsed.TotalMilliseconds, 1) * 100:F1}% of eval, once per map)");
    }

    if (!enabled)
    {
        Console.WriteLine("  (no ray data — rerun with --ray-counters to enable)");
        return;
    }

    if (rays.PairsEvaluated == 0)
    {
        Console.WriteLine("  no directed pairs evaluated (no collision bake, or no rule subscribed to enemy_spotted)");
        return;
    }

    static double Pct(long part, long whole)
    {
        return whole > 0 ? part * 100.0 / whole : 0.0;
    }

    double evalMs = evalElapsed.TotalMilliseconds;
    double raysPerSec = rays.RayMs > 0 ? rays.RaysCast / (rays.RayMs / 1000.0) : 0.0;
    Console.WriteLine($"  Sampled ticks:              {rays.SampledTicks,12:N0}");
    Console.WriteLine($"  Directed enemy pairs:       {rays.PairsEvaluated,12:N0}   ({(double)rays.PairsEvaluated / Math.Max(1, rays.SampledTicks):F1} per sampled tick)");
    Console.WriteLine($"    frustum-rejected (0 anchors in FOV): {rays.PairsNoAnchorInFrustum,12:N0}   {Pct(rays.PairsNoAnchorInFrustum, rays.PairsEvaluated),5:F1}% of pairs");
    Console.WriteLine($"    exposed:                  {rays.PairsExposed,12:N0}   {Pct(rays.PairsExposed, rays.PairsEvaluated),5:F1}%");
    Console.WriteLine($"    could-see:                {rays.PairsCouldSee,12:N0}   {Pct(rays.PairsCouldSee, rays.PairsEvaluated),5:F1}%");
    Console.WriteLine($"  Anchors in FOV:             {rays.AnchorsInFrustum,12:N0}   of {rays.AnchorsTotal:N0} ({Pct(rays.AnchorsInFrustum, rays.AnchorsTotal):F1}%)");
    Console.WriteLine($"  Rays cast:                  {rays.RaysCast,12:N0}   ({(double)rays.RaysCast / rays.PairsEvaluated:F2} per pair; {rays.RaysSkippedByEarlyExit:N0} anchors skipped by early exit)");
    Console.WriteLine($"    clear (no occluder):      {rays.RaysClear,12:N0}   {Pct(rays.RaysClear, rays.RaysCast),5:F1}%");
    Console.WriteLine($"    decided by occluder hint: {rays.RaysShortCircuited,12:N0}   {Pct(rays.RaysShortCircuited, rays.RaysCast),5:F1}% of rays cast   ({Pct(rays.RaysShortCircuited, rays.RaysCast - rays.RaysClear):F1}% of occluded; no BVH traversal)");
    Console.WriteLine($"  Anchors skipped by gate:    {rays.RaysSkippedByGate,12:N0}   {Pct(rays.RaysSkippedByGate, rays.AnchorsTotal),5:F1}% of anchors   ({rays.RaysSkippedBySmoke:N0} behind smoke, the rest outside the frustum)");
    Console.WriteLine($"    anchor outside FOV:       {rays.RaysCastOutsideFrustum,12:N0}   {Pct(rays.RaysCastOutsideFrustum, rays.RaysCast),5:F1}%   (per-anchor frustum gate would skip these)");
    Console.WriteLine($"    on frustum-rejected pairs:{rays.RaysCastOnFrustumRejectedPairs,12:N0}   {Pct(rays.RaysCastOnFrustumRejectedPairs, rays.RaysCast),5:F1}%   (per-pair frustum gate would skip these)");
    Console.WriteLine($"  Raycast wall-clock:         {rays.RayMs,12:F1} ms   {Pct((long)rays.RayMs, (long)evalMs),5:F1}% of eval   ({raysPerSec / 1e6:F2} MRay/s, single thread)");
    Console.WriteLine($"  Transition-scan wall-clock: {rays.SampleMs,12:F1} ms   {Pct((long)rays.SampleMs, (long)evalMs),5:F1}% of eval   (raycasts + frustum + anchors + crosshair test)");
}

// Null (omitted from the JSON) when the counters were not enabled for this run.
static ReportVisibilityRays? BuildRayReport(VisibilityCountersSnapshot rays, TimeSpan evalElapsed, bool enabled)
{
    if (!enabled && rays.BakesLoaded == 0)
    {
        return null;
    }

    double evalMs = evalElapsed.TotalMilliseconds;
    return new ReportVisibilityRays(
        rays.SampledTicks,
        rays.PairsEvaluated,
        rays.PairsNoAnchorInFrustum,
        rays.PairsExposed,
        rays.PairsCouldSee,
        rays.AnchorsTotal,
        rays.AnchorsInFrustum,
        rays.RaysCast,
        rays.RaysClear,
        rays.RaysCastOutsideFrustum,
        rays.RaysCastOnFrustumRejectedPairs,
        rays.RaysSkippedByEarlyExit,
        Math.Round(rays.RayMs, 2),
        Math.Round(rays.SampleMs, 2),
        evalMs > 0 ? Math.Round(rays.RayMs / evalMs, 4) : 0.0,
        evalMs > 0 ? Math.Round(rays.SampleMs / evalMs, 4) : 0.0,
        rays.RayMs > 0 ? Math.Round(rays.RaysCast / (rays.RayMs / 1000.0)) : 0.0,
        rays.RaysSkippedByGate,
        rays.RaysSkippedBySmoke,
        rays.RaysShortCircuited,
        Math.Round(rays.BakeLoadMs, 2),
        Math.Round(rays.BvhBuildMs, 2),
        rays.BakeTriangles,
        rays.BakesLoaded);
}

// ── Report Records ─────────────────────────────────────────────────────────

internal sealed record TestCase(string Id, string DemoPath);

internal sealed record BenchReport(ReportMetadata Metadata, ReportPerformance Performance, ReportProvenance Provenance, List<PlayerReport> Players);

internal sealed record ReportMetadata(
    DateTimeOffset Timestamp,
    string GitCommit,
    string DemoFile,
    double DemoSizeMb,
    string DemoSha256,
    string Map,
    int PlayerCount,
    int TickCount,
    int TickRate,
    double DurationSeconds,
    MachineInfo Machine);

internal sealed record MachineInfo(
    string Os,
    string OsVersion,
    string Architecture,
    string Cpu,
    int PhysicalCores,
    int LogicalCores,
    long RamBytes,
    string DotnetVersion);

// One bracket, DemoAnalysis.Run, reported as eval_ms. The rules load and the bake sit outside it,
// the bake with a figure of its own.
internal sealed record ReportPerformance(
    double EvalMs,
    double TotalMs,
    double BakeMs,
    int FrameCount,
    int MessageCount,
    int NodeCount,
    int EdgeCount,
    int MaterializedPlayers,
    int TimelineEvents,
    GcReport Gc,
    ReportEntityProfile? EntityProfile = null,
    ReportVisibilityRays? VisibilityRays = null);

// AllocBytes is the eval bracket on the calling thread; AllocBytesAllThreads adds the reader and the
// digest workers, which is the figure to compare across runs.
internal sealed record GcReport(int Gen0, int Gen1, int Gen2, int EvalGen0, int EvalGen1, long AllocBytes, long AllocBytesAllThreads);

// What the run reports about itself; see AnalysisProvenance in the engine.
internal sealed record ReportProvenance(
    string Source,
    string Profile,
    string ProfileResolution,
    string Digest,
    bool SnapshotsCaptured,
    int FramesConsumed,
    int MessagesConsumed,
    int RoundOfficiallyEndedSeen,
    int CsPreRestartSeen,
    bool BoundMarkerNeverSeen);

// Wall-clock per phase. Eval is the one bracket around DemoAnalysis.Run; Read is the hash, which
// sits outside Total.
internal sealed record PhaseTimings(TimeSpan Read, TimeSpan Eval, TimeSpan Bake)
{
    public TimeSpan Total => Eval;
}

internal readonly record struct GcCounts(int Gen0, int Gen1, int Gen2)
{
    public static GcCounts Now() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
}

// What the run hands to the shared reporting.
internal sealed record BenchOutcome(
    AnalysisRun Run,
    string Sha256,
    PhaseTimings Timings,
    GcCounts GcBefore,
    GcCounts EvalGcBefore,
    GcCounts GcAfter,
    long EvalAllocBytes,
    long AllocBytesAllThreads);

/// <summary>
///     Entity-decode sub-phase timings (all milliseconds) captured when a profiled run ran
///     (<see cref="CS2DemoKit.Parser.Profiling.Enabled" />). Null in the report when no profiled run captured data.
///     <para>
///         Two disjoint cost centres, not one tree. The per-frame tree nests
///         <c>ScannerSeekMs ⊇ PacketEntitiesMs ⊇ (FieldPathMs + FieldValueMs + DescriptorBuildMs)</c>,
///         each level plus an unattributed remainder. <see cref="DigestFoldMs" /> sits outside it: the
///         digest producer's fold time summed over its workers, on either source. Each worker owns its own
///         <c>EntityStateLayer</c>, so its decode never lands in the per-frame accumulators, and the eval
///         loop consumes digests rather than driving the layer, so the seek tree reads near zero and a
///         regression in the fold shows up in this field alone. Summed worker time is not wall-clock:
///         compare it across runs, not against <c>ReportPerformance.EvalMs</c>.
///     </para>
///     <para>
///         Take a fresh baseline before comparing these numbers. A run over a near-empty rule graph
///         (<c>edge_count</c> 9) measures almost no rule work, so its eval and fold numbers read as a
///         false regression against any real ruleset.
///     </para>
/// </summary>
internal sealed record ReportEntityProfile(
    double DigestFoldMs,
    double ScannerSeekMs,
    double ProviderPollMs,
    double ProjectileScanMs,
    double PreFrameSnapshotMs,
    double PacketEntitiesMs,
    double FieldPathMs,
    double FieldValueMs,
    double DescriptorBuildMs,
    long DigestFoldAllocBytes,
    long ScannerSeekAllocBytes,
    long ProviderPollAllocBytes,
    long ProjectileScanAllocBytes,
    long PreFrameSnapshotAllocBytes,
    long PacketEntitiesAllocBytes,
    long FieldPathAllocBytes,
    long FieldValueAllocBytes,
    long DescriptorBuildAllocBytes,
    int FramesPolled,
    int PacketEntitiesCount,
    int EntityFieldReads,
    int DescriptorBuilds);

internal sealed record PlayerReport(string Name, int Slot, int Team, int TemplateIndex, Dictionary<string, object?> Stats);

/// <summary>
///     The visibility path's measured ray budget for one run, from <c>VisibilityCounters</c>. Present
///     only on runs made with <c>--ray-counters</c>. <see cref="RayShareOfEval" /> is raycast
///     wall-clock over <c>ReportPerformance.EvalMs</c>; the raycasting runs single-threaded on the
///     eval thread, so that share is the ceiling on what a faster traversal can take off eval.
///     <para>
///         <see cref="RaysSkippedByGate" /> and <see cref="RaysSkippedBySmoke" /> arrived with the
///         engine's ray gating (0.11.0). From that version every anchor is cast, gate-skipped or
///         early-exit-skipped exactly once, so <c>RaysCast + RaysSkippedByGate + RaysSkippedByEarlyExit
///         == AnchorsTotal</c>. Reports written before it have no gate fields and a wider
///         <see cref="RaysSkippedByEarlyExit" /> (it then also covered the out-of-frustum anchors of an
///         exposed pair, which the gate now books); compare the two eras on <see cref="PairsCouldSee" />
///         and the players block, not on the skip columns.
///     </para>
///     <para>
///         <see cref="RaysShortCircuited" /> arrived with the last-occluder hint (also 0.11.0): of the
///         rays cast, those decided by the triangle that blocked the same sightline last sample, without
///         a BVH traversal. A short-circuited ray still counts in <see cref="RaysCast" /> and its time in
///         <see cref="RayMs" />, so the ray tallies are comparable with the pre-hint era and the
///         saving shows in <see cref="RayMs" />. <c>RaysShortCircuited / RaysCast</c> is the hit rate
///         the batch-precompute decision hangs on; reports written against 0.10.0 have no such
///         field.
///     </para>
/// </summary>
internal sealed record ReportVisibilityRays(
    long SampledTicks,
    long DirectedPairs,
    long PairsFrustumRejected,
    long PairsExposed,
    long PairsCouldSee,
    long AnchorsTotal,
    long AnchorsInFrustum,
    long RaysCast,
    long RaysClear,
    long RaysCastOutsideFrustum,
    long RaysCastOnFrustumRejectedPairs,
    long RaysSkippedByEarlyExit,
    double RayMs,
    double TransitionScanMs,
    double RayShareOfEval,
    double TransitionScanShareOfEval,
    double RaysPerSecond,
    long RaysSkippedByGate,
    long RaysSkippedBySmoke,
    long RaysShortCircuited,
    double BakeLoadMs = 0,
    double BvhBuildMs = 0,
    long BakeTriangles = 0,
    long BakesLoaded = 0);

internal static class JsonOpts
{
    /// <summary>Default.</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new StatValueConverter()
        }
    };
}

// ── JSON Converter for stat values ─────────────────────────────────────────

internal sealed class StatValueConverter : JsonConverter<object?>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(object);

    /// <inheritdoc />
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case int i: writer.WriteNumberValue(i); break;
            case double d: writer.WriteNumberValue(Math.Round(d, 2)); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case string s: writer.WriteStringValue(s); break;
            default: writer.WriteStringValue(value.ToString()); break;
        }
    }
}

// ── Meter Listener (in-proc equivalent of a dotnet-counters session) ─────────

internal sealed class MeterCollector : IDisposable
{
    private const string MeterName = "CS2DemoKit.Analysis.Evaluator";
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly MeterListener _listener = new();
    private long _histCount;
    private double _histMax;
    private string _histName = "";
    private double _histSum;

    public MeterCollector()
    {
        _listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == MeterName)
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        _listener.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
            _counters[inst.Name] = _counters.GetValueOrDefault(inst.Name) + measurement);
        _listener.SetMeasurementEventCallback<double>((inst, measurement, _, _) =>
        {
            _histName = inst.Name;
            _histCount++;
            _histSum += measurement;
            if (measurement > _histMax)
            {
                _histMax = measurement;
            }
        });
        _listener.Start();
    }

    /// <summary>Disposes the listener. Call after eval; measurement callbacks fire synchronously, so totals are final.</summary>
    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Prints accumulated counter totals + the frame-duration histogram summary.</summary>
    public void PrintSummary()
    {
        Console.WriteLine("─── Evaluator Counters (Meter) ─────────────────────────");
        foreach (KeyValuePair<string, long> kv in _counters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {kv.Key,-34} {kv.Value,14:N0}");
        }

        if (_histCount > 0)
        {
            Console.WriteLine(
                $"  {_histName,-34} {"",14}  n={_histCount:N0}  mean={_histSum / _histCount:F4} ms  max={_histMax:F3} ms");
        }
    }
}

// ── ActivitySource Listener (phase timeline) ─────────────────────────────────

internal sealed class PhaseTimeline : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<(string Name, DateTime Start, double Ms, int Depth)> _spans = [];

    public PhaseTimeline()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == AnalysisDiagnostics.SourceName,
            Sample = static (ref options) => ActivitySamplingResult.AllData,
            ActivityStopped = OnStopped
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    ///     Disposes the listener. Call after eval; every span has already Stop()ped on its using-exit, so the captured
    ///     timeline is final.
    /// </summary>
    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Prints the captured spans in start order, indented by nesting depth.</summary>
    public void PrintSummary()
    {
        Console.WriteLine("─── Phase Timeline (ActivitySource) ─────────────────────");
        if (_spans.Count == 0)
        {
            Console.WriteLine("  (no spans captured)");
            return;
        }

        foreach ((string name, _, double ms, int depth) in _spans.OrderBy(s => s.Start))
        {
            Console.WriteLine($"  {new string(' ', depth * 2)}{name,-22} {ms,9:F1} ms");
        }
    }

    // All spans here open/close on the parse thread (precompute brackets the orchestrator, not the
    // workers), so ActivityStopped fires single-threaded — no lock needed on _spans.
    private void OnStopped(Activity a)
    {
        int depth = 0;
        for (Activity? p = a.Parent; p is not null; p = p.Parent)
        {
            depth++;
        }

        _spans.Add((a.OperationName, a.StartTimeUtc, a.Duration.TotalMilliseconds, depth));
    }
}

// ── EventSource Listener ───────────────────────────────────────────────────

internal sealed class EvaluatorListener : EventListener
{
    private int _edgesRegistered;
    private int _framesProcessed;
    private int _logicNodesRecomputed;
    private int _messagesProcessed;
    private int _playersMaterialized;
    private int _roundResets;
    private int _slowestFrameIndex;
    private long _slowestFrameTicks;
    private long _slowestMessageTicks;
    private string _slowestMessageType = "";
    private long _totalEdgesEvaluated;
    private long _totalEdgesFired;

    /// <summary>Print summary.</summary>
    public void PrintSummary()
    {
        Console.WriteLine("─── Evaluator Diagnostics ──────────────────────────────");
        Console.WriteLine($"  Frames processed:       {_framesProcessed:N0}");
        Console.WriteLine($"  Messages processed:     {_messagesProcessed:N0}");
        Console.WriteLine($"  Total edges evaluated:  {_totalEdgesEvaluated:N0}");
        Console.WriteLine($"  Total edges fired:      {_totalEdgesFired:N0}");
        Console.WriteLine($"  Edge hit rate:          {(_totalEdgesEvaluated > 0 ? (double)_totalEdgesFired / _totalEdgesEvaluated * 100 : 0):F1}%");
        Console.WriteLine($"  Logic nodes recomputed: {_logicNodesRecomputed:N0}");
        Console.WriteLine($"  Players materialized:   {_playersMaterialized:N0}");
        Console.WriteLine($"  Edges registered:       {_edgesRegistered:N0}");
        Console.WriteLine($"  Round resets:           {_roundResets:N0}");
        if (_slowestFrameTicks > 0)
        {
            Console.WriteLine($"  Slowest frame:          #{_slowestFrameIndex} ({(double)_slowestFrameTicks / Stopwatch.Frequency * 1000.0:F3} ms)");
        }

        if (_slowestMessageTicks > 0)
        {
            Console.WriteLine($"  Slowest message:        {_slowestMessageType} ({(double)_slowestMessageTicks / Stopwatch.Frequency * 1000.0:F3} ms)");
        }

        if (_messagesProcessed > 0 && _totalEdgesEvaluated > 0)
        {
            Console.WriteLine($"  Avg edges/message:      {(double)_totalEdgesEvaluated / _messagesProcessed:F1}");
            Console.WriteLine($"  Avg fired/message:      {(double)_totalEdgesFired / _messagesProcessed:F1}");
        }
    }

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "CS2DemoKit.Analysis.Evaluator")
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        switch (e.EventId)
        {
            case 3:
                _framesProcessed++;
                long frameTicks = (long)e.Payload![2]!;
                if (frameTicks > _slowestFrameTicks)
                {
                    _slowestFrameTicks = frameTicks;
                    _slowestFrameIndex = (int)e.Payload[0]!;
                }

                break;
            case 4:
                _messagesProcessed++;
                _totalEdgesEvaluated += (int)e.Payload![2]!;
                _totalEdgesFired += (int)e.Payload[3]!;
                _logicNodesRecomputed += (int)e.Payload[4]!;
                long msgTicks = (long)e.Payload[5]!;
                if (msgTicks > _slowestMessageTicks)
                {
                    _slowestMessageTicks = msgTicks;
                    _slowestMessageType = (string)e.Payload[1]!;
                }

                break;
            case 7: _playersMaterialized++; break;
            case 2: _edgesRegistered++; break;
            case 10: _roundResets++; break;
        }
    }
}
