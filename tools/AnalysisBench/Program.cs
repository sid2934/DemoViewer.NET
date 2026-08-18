#region

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnalysisBench;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.GoldenStats;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;

#endregion

// ── Test Suite ──────────────────────────────────────────────────────────────
// Discovered from demos/benchmarks/: any .dem file is a benchmark entry.
// If a matching <id>.leetify.json exists alongside it, stats comparison runs too.

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

// `gc-sweep <demo>` — run the parse+analysis under every GC configuration worth considering and
// tabulate cost vs footprint. Dispatched here, before any GC-sensitive work, because the sweep's whole
// job is to compare CLR startup configurations: each one has to be a fresh child process (Server vs
// Workstation, background GC, RetainVM, heap count, ConserveMemory and DATAS are all immutable after
// startup). `gc-sweep-probe` is the child half — one configuration, measured, emitted as JSON.
if (positional is ["gc-sweep", ..])
{
    return GcSweepCommand.Run(positional.Skip(1).ToArray(), namedArgs);
}

if (positional is ["gc-sweep-probe", ..])
{
    return GcSweepCommand.RunProbe(positional.Skip(1).ToArray());
}

bool suiteMode = flags.Contains("--suite");
bool listSuite = flags.Contains("--list-suite");
// --no-golden suppresses WriteGoldenStatsFiles so verification runs don't clobber the
// committed tests/fixtures/*/*.golden.json oracle. Golden regeneration is an explicit,
// reviewed re-baseline (a normal run without this flag).
bool noGolden = flags.Contains("--no-golden");
// Diagnostic listeners — parsed here (not just in the single-demo path) so they compose with --suite
// too. Each is a runtime opt-in (no profile build needed); see docs/profiling.md.
bool enableTrace = flags.Contains("--trace");
bool enableCounters = flags.Contains("--counters");
bool enableTimeline = flags.Contains("--timeline");
// --profile (runtime, no special build): turns on the parse + entity accumulator trees via the single
// Profiling.Enabled switch AND attaches the Meter + ActivitySource listeners (by implying --counters /
// --timeline, which construct one listener each — no double-attach). Set HERE, before any DemoParser.Parse
// on the suite / early-return / normal paths, so every parse observes the flag (the set-before-run
// contract). DEMOVIEWER_PROFILE=1 resolves the same switch at process start.
// --mmap: read the .dem through a MemoryMappedDemoSource instead of File.ReadAllBytes, so the raw
// file bytes never enter the managed heap. Same binary, same demo — toggling this flag is the
// before/after comparison for the memory-mapped-buffer work. See MemoryMappedDemoSource's ownership
// contract: the mapping is disposed as soon as the bytes are no longer needed.
bool useMmap = flags.Contains("--mmap");
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
        Console.WriteLine("Add a matching <id>.leetify.json for correctness comparison.");
        return 0;
    }

    Console.WriteLine($"Benchmark directory: {benchDir}");
    Console.WriteLine($"{"ID",-50} {"Size",8} {"Leetify",8}");
    Console.WriteLine(new string('─', 70));
    foreach (TestCase tc in testSuite)
    {
        FileInfo fi = new(tc.DemoPath);
        string size = $"{fi.Length / 1024.0 / 1024.0:F0} MB";
        string refStatus = tc.LeetifyJson is not null ? "yes" : "-";
        Console.WriteLine($"  {tc.Id,-48} {size,8} {refStatus,8}");
    }

    Console.WriteLine($"\n  {testSuite.Length} demo(s), {testSuite.Count(t => t.LeetifyJson is not null)} with reference data");
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
            int result = RunBench(tc.DemoPath, rulesDir, reportFile, tc.LeetifyJson, enableTrace, false,
                noGolden: noGolden, enableCounters: enableCounters, enableTimeline: enableTimeline,
                useMmap: useMmap);
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
    Console.Error.WriteLine("  --timeline           Attach an ActivityListener and print the phase timeline (read/parse/build/eval/precompute)");
    Console.Error.WriteLine("  --bare               Run Evaluate() without snapshots (measures pure eval cost)");
    Console.Error.WriteLine("  --no-golden          Skip writing tests/fixtures golden files (use for verification runs)");
    Console.Error.WriteLine("  --mmap               Memory-map the .dem instead of File.ReadAllBytes (keeps the file bytes off the managed heap)");
    Console.Error.WriteLine("  --round-debug        Detailed per-round event trace");
    Console.Error.WriteLine("  --report=<path>      Write JSON report to file");
    Console.Error.WriteLine("  --export=csv|json    Export per-(player,round) stats as a MetricTable (requires snapshot mode)");
    Console.Error.WriteLine("  --out=<path>         Output file for --export (default: ./player_round_stats.<ext>)");
    Console.Error.WriteLine("  --suite              Run all test suite entries");
    Console.Error.WriteLine("  --list-suite         List test suite entries and their status");
    Console.Error.WriteLine("  --report-dir=<dir>   Output directory for suite reports (default: bench-reports)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Profiling (OFF by default; see docs/profiling.md):");
    Console.Error.WriteLine("  --profile            (runtime) turn on parse + entity per-phase profile trees + attach Meter/Activity listeners");
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
    string? leetifyJson = null;

    // Fail fast on a bad --export value, BEFORE the full parse+eval, so a typo doesn't waste a run.
    if (exportFormat is not null
        && !string.Equals(exportFormat.Trim(), "csv", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(exportFormat.Trim(), "json", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Unknown --export format '{exportFormat}'. Expected 'csv' or 'json'.");
        return 1;
    }

    // Check if this demo is in the test suite and has a reference file
    string fullDemoPath = Path.GetFullPath(demoPath);
    TestCase? suiteMatch = testSuite.FirstOrDefault(tc => Path.GetFullPath(tc.DemoPath) == fullDemoPath);
    if (suiteMatch is not null)
    {
        leetifyJson = suiteMatch.LeetifyJson;
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

    return RunBench(demoPath, rulesDir, reportPath, leetifyJson, enableTrace, bareMode, stateTraceArg, noGolden,
        enableCounters, enableTimeline, exportFormat, exportOut, useMmap);
}

// ── Core Bench ─────────────────────────────────────────────────────────────

static int RunBench(string demoPath, string rulesDir, string? reportPath,
    string? leetifyJsonPath, bool enableTrace, bool bareMode, string? stateTraceArg = null, bool noGolden = false,
    bool enableCounters = false, bool enableTimeline = false, string? exportFormat = null, string? exportOut = null,
    bool useMmap = false)
{
    FileInfo demoFileInfo = new(demoPath);
    double demoSizeMb = demoFileInfo.Length / 1024.0 / 1024.0;

    Console.WriteLine($"Demo:  {demoPath} ({demoSizeMb:F1} MB)");
    Console.WriteLine($"Rules: {rulesDir}");
    Console.WriteLine($"Mode:  {(bareMode ? "bare (no snapshots)" : "full (with snapshots)")}{(enableTrace ? " + trace" : "")}");
    Console.WriteLine($"Buffer:{(useMmap ? " memory-mapped (off managed heap)" : " byte[] (File.ReadAllBytes)")}");
    if (reportPath is not null)
    {
        Console.WriteLine($"Report: {reportPath}");
    }

    if (leetifyJsonPath is not null && File.Exists(leetifyJsonPath))
    {
        Console.WriteLine($"Ref:    {leetifyJsonPath}");
    }

    Console.WriteLine();

    EvaluatorListener? listener = enableTrace ? new EvaluatorListener() : null;
    // --counters: attach a MeterListener BEFORE eval so EvaluatorMetrics.Enabled flips true and the
    // evaluator's guarded Counter.Add / FrameDurationMs.Record fire (mirrors a dotnet-counters session).
    MeterCollector? counters = enableCounters ? new MeterCollector() : null;
    // --timeline: attach an ActivityListener BEFORE the phases run so the read/parse/build spans (here)
    // and the library's analysis.eval ⊃ analysis.precompute spans are captured into one nested timeline.
    PhaseTimeline? phaseTimeline = enableTimeline ? new PhaseTimeline() : null;
    // DEMOVIEWER_PROFILE=1 (env) — the unified one-switch runtime profile: attaches Meter + Activity
    // listeners for the whole run and dumps a combined report on exit (disposed at method scope). This is
    // the same library helper the Desktop app uses; it is independent of the explicit flags above.
    using ProfilingSession? session = ProfilingSession.StartFromEnvironment();

    // ── Memory high-water sampler ──────────────────────────────────────────
    // Started before the read so the demo buffer's contribution is inside the window. Managed-heap peak
    // is the number the memory-mapped path is meant to move; RSS is reported alongside because mapped
    // pages are still resident (just file-backed and evictable), so RSS is NOT expected to drop by the
    // file size.
    using MemorySampler memSampler = MemorySampler.Start();
    long allocatedBefore = GC.GetTotalAllocatedBytes(true);

    // ── Read ───────────────────────────────────────────────────────────────
    // File read is NOT part of DemoParser.Parse but IS part of the end-to-end load the user feels (and is
    // disk-cache sensitive — cold vs warm). Timed separately so the Parse number stays comparable.
    // With --mmap the "read" is just the mmap syscall (near-instant); the pages fault in lazily during
    // the parse, so read time moves into parse time rather than disappearing.
    long readStart = Stopwatch.GetTimestamp();
    byte[]? bytes = null;
    // SYMMETRY, deliberate: the mapping is held to the END of this method — the same point the byte[]
    // is held to by the GC.KeepAlive below — so the two runs are apples-to-apples and both model the
    // app's "demo loaded, buffer still owned" state. Disposing right after Parse (which the ownership
    // contract permits, since nothing downstream references the bytes) would flatter the mapped run's
    // heap numbers by releasing its buffer while the byte[] run still holds its own.
    using MemoryMappedDemoSource? mapped = useMmap ? MemoryMappedDemoSource.Open(demoPath) : null;
    ReadOnlyMemory<byte> demoData;
    using (AnalysisDiagnostics.ActivitySource.StartActivity("read"))
    {
        if (mapped is not null)
        {
            demoData = mapped.Memory;
        }
        else
        {
            bytes = File.ReadAllBytes(demoPath);
            demoData = bytes;
        }
    }

    TimeSpan readElapsed = Stopwatch.GetElapsedTime(readStart);
    Console.WriteLine($"Read:   {readElapsed.TotalMilliseconds,8:F1} ms  |  {demoData.Length / 1024 / 1024} MB");
    string sha256 = Convert.ToHexStringLower(SHA256.HashData(demoData.Span));

    // ── Parse ──────────────────────────────────────────────────────────────

    GC.Collect(2, GCCollectionMode.Forced, true, true);
    int gc0Before = GC.CollectionCount(0), gc1Before = GC.CollectionCount(1), gc2Before = GC.CollectionCount(2);
    long parseStart = Stopwatch.GetTimestamp();
    ParsedDemo demo;
    using (AnalysisDiagnostics.ActivitySource.StartActivity("parse"))
    {
        demo = DemoParser.Parse(demoData);
    }

    // OWNERSHIP: this method opened the mapping, so this method disposes it — via the `using`
    // declaration above, on every exit path including exceptions. It would be legal to dispose right
    // here (nothing downstream of Parse references the mapped bytes; see MemoryMappedDemoSource's
    // contract) but the measurement wants both buffer strategies held for the same span.

    TimeSpan parseElapsed = Stopwatch.GetElapsedTime(parseStart);

    int roundsStarted = demo.AllGameEvents.Count(e => e.Payload is RoundFreezeEndEvent);
    int roundsEnded = demo.AllGameEvents.Count(e => e.Payload is RoundOfficiallyEndedEvent);
    int warmupRounds = CountWarmupRounds(demo);
    int liveRoundsStarted = roundsStarted - warmupRounds;


    Console.WriteLine($"Parse:  {parseElapsed.TotalMilliseconds,8:F1} ms  |  {demo.Frames.Count} frames, {demo.AllGameEvents.Count} events, {demo.Players.Count} players");
    Console.WriteLine($"Map:    {demo.MapName}  |  Rounds: {liveRoundsStarted} started, {roundsEnded} ended" +
                      (liveRoundsStarted != roundsEnded ? $"  (delta: {liveRoundsStarted - roundsEnded})" : ""));
    Console.WriteLine($"Source: {demo.Profile.SourceKind}  |  Build: {demo.Profile.BuildNumber}  |  Server: \"{demo.ServerName}\"  |  Client: \"{demo.ClientName}\"");


    // ── Build ──────────────────────────────────────────────────────────────
    long buildStart = Stopwatch.GetTimestamp();
    BuildResult buildResult;
    using (AnalysisDiagnostics.ActivitySource.StartActivity("build"))
    {
        // Load the whole shipped rules/ dir: v1 chains land in .Config, v2 rulesets in .Rulesets.
        // Post Rulesets v2 cutover the shipped stats are all v2 rulesets, so the bench MUST compose
        // them (the v2 overload) or it evaluates an empty graph. Keep the strict shipped-tier
        // hard-fail the old LoadDirectory gave.
        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(rulesDir);
        if (!loaded.Success)
        {
            throw new RuleConfigException(loaded.Errors);
        }

        // DemoAnalysis.Build supplies the default registries, including the entity-provider
        // registries that make RuleChainBuilder construct the EntityChangeScanner — so the
        // benchmark drives the same entity-tracking hot path as the app.
        buildResult = DemoAnalysis.Build(demo, loaded.Rulesets);
    }

    TimeSpan buildElapsed = Stopwatch.GetElapsedTime(buildStart);
    Console.WriteLine($"Build:  {buildElapsed.TotalMilliseconds,8:F1} ms  |  {buildResult.Nodes.Count} nodes, {buildResult.Edges.Count} edges, {buildResult.Chains.Count} chains");

    // ── Evaluate ───────────────────────────────────────────────────────────
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    int evalGc0 = GC.CollectionCount(0), evalGc1 = GC.CollectionCount(1);
    // Allocated-bytes bracket: Evaluate/EvaluateWithSnapshots run synchronously on this thread,
    // so GetAllocatedBytesForCurrentThread cleanly attributes every byte the eval phase (entity
    // tracking included) allocates. GC gen-counts alone can't distinguish a churny short-lived
    // path from a frugal one with the same collection cadence.
    long evalAllocBefore = GC.GetAllocatedBytesForCurrentThread();
    long evalStart = Stopwatch.GetTimestamp();

    int messageCount = 0, playerCount = 0, timelineEvents = 0;
    List<PlayerReport> playerReports = new();
    // Captured in full (snapshot) mode so the post-run --export block can feed it through a projector.
    EvaluationResult? evalResult = null;

    if (bareMode)
    {
        RuleChainTimeline timeline = DemoAnalysis
            .Evaluate(demo, buildResult, new AnalysisOptions
            {
                CaptureSnapshots = false
            })
            .Timeline;
        timelineEvents = timeline.Events.Count;
        TimeSpan evalElapsedBare = Stopwatch.GetElapsedTime(evalStart);
        Console.WriteLine($"Eval:   {evalElapsedBare.TotalMilliseconds,8:F1} ms  |  bare mode, {timelineEvents} timeline events");
    }
    else
    {
        EvaluationResult result = DemoAnalysis.Evaluate(demo, buildResult).Snapshots!;
        evalResult = result;
        messageCount = result.Messages.Count;
        playerCount = result.MaterializedPlayers.Count;
        timelineEvents = result.Timeline.Events.Count;
        TimeSpan evalElapsedFull = Stopwatch.GetElapsedTime(evalStart);

        Console.WriteLine($"Eval:   {evalElapsedFull.TotalMilliseconds,8:F1} ms  |  {messageCount} messages, {playerCount} materialized players");

        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            Dictionary<string, object?> stats = new();
            foreach (PerPlayerColumnAssignment col in mp.ColumnAssignments)
            {
                string? raw = col.Node.IsActive ? col.Node.GetDisplayValue() : null;
                stats[col.ColumnName] = ParseStatValue(raw);
            }

            int team = demo.Players.TryGetValue(mp.PlayerSlot, out PlayerInfo? pi) ? pi.Team : 0;
            playerReports.Add(new PlayerReport(mp.PlayerName, mp.PlayerSlot, team, mp.TemplateIndex, stats));
        }

        // Chain event summary
        Console.WriteLine();
        Console.WriteLine("─── Rule Chain Events ───────────────────────────────────");
        IOrderedEnumerable<IGrouping<string, RuleChainEvent>> chainCounts = result.Timeline.Events.GroupBy(e => e.ChainName).OrderBy(g => g.Key);
        foreach (IGrouping<string, RuleChainEvent> g in chainCounts)
        {
            Console.WriteLine($"  {g.Key,-30} {g.Count(),5} events");
        }

        // State-machine transition trace (--state-trace=name1,name2,...)
        if (!string.IsNullOrEmpty(stateTraceArg))
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

                    (DemoFrame frame, NetMessage msg) = result.Messages[m];
                    Console.WriteLine($"    [tick {frame.ServerTick,8} msg {msg.GetType().Name,-30}]  IsActive={s.IsActive,-5}  Value={s.DisplayValue}");
                    prevActive = s.IsActive;
                    prevValue = s.DisplayValue;
                    transitions++;
                }

                Console.WriteLine($"    ({transitions} transitions)");
            }
        }

        // Per-template player tables
        PrintPlayerTables(playerReports);
    }

    TimeSpan evalElapsed = Stopwatch.GetElapsedTime(evalStart);
    long evalAllocBytes = GC.GetAllocatedBytesForCurrentThread() - evalAllocBefore;
    int gc0After = GC.CollectionCount(0), gc1After = GC.CollectionCount(1), gc2After = GC.CollectionCount(2);

    // ── Summary ────────────────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("─── Performance Summary ─────────────────────────────────");
    Console.WriteLine($"  Read:   {readElapsed.TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  Parse:  {parseElapsed.TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  Build:  {buildElapsed.TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  Eval:   {evalElapsed.TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  ── load (read+parse+build+eval): {(readElapsed + parseElapsed + buildElapsed + evalElapsed).TotalMilliseconds,8:F1} ms");
    Console.WriteLine($"  Total (parse+build+eval): {(parseElapsed + buildElapsed + evalElapsed).TotalMilliseconds,8:F1} ms");
    Console.WriteLine();
    Console.WriteLine($"  GC Gen0: {gc0After - gc0Before}  Gen1: {gc1After - gc1Before}  Gen2: {gc2After - gc2Before}");
    Console.WriteLine($"     Eval: Gen0={gc0After - evalGc0}  Gen1={gc1After - evalGc1}");
    Console.WriteLine($"  Eval allocated: {evalAllocBytes / (1024.0 * 1024.0),8:F1} MiB ({evalAllocBytes:N0} bytes)");

    // ── Memory ─────────────────────────────────────────────────────────────
    // Peak managed heap is the target metric for the memory-mapped demo buffer: a File.ReadAllBytes
    // buffer is ~file-size of LOH that mapping removes entirely. Peak RSS is expected to move much
    // less — mapped pages are still resident, just file-backed, shared and evictable instead of dirty
    // anonymous heap the GC must trace, compact around and hold.
    long allocatedTotal = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
    Console.WriteLine();
    Console.WriteLine($"  Buffer strategy:    {(useMmap ? "memory-mapped" : "byte[]")}"
                      + $"   (buffer still held here: {(bytes is null ? $"{mapped?.Length / (1024.0 * 1024.0):F1} MiB mapped view" : $"{bytes.Length / (1024.0 * 1024.0):F1} MiB byte[]")})");
    Console.WriteLine($"  Peak managed heap:  {memSampler.PeakManagedHeapBytes / (1024.0 * 1024.0),9:F1} MiB");
    Console.WriteLine($"  Peak process RSS:   {memSampler.PeakRssBytes / (1024.0 * 1024.0),9:F1} MiB");
    Console.WriteLine($"  Total allocated:    {allocatedTotal / (1024.0 * 1024.0),9:F1} MiB");
    Console.WriteLine($"  Managed heap now:   {GC.GetTotalMemory(false) / (1024.0 * 1024.0),9:F1} MiB");
    // Keep BOTH strategies honest and symmetric: without these the JIT may drop either buffer early,
    // which would flatter whichever run got dropped first.
    GC.KeepAlive(bytes);
    GC.KeepAlive(mapped);

    // ── Entity-tracking sub-phase profile ────────────────────────────────────
    // Populated only when a profiled run captured entity data (Profiling.Enabled — runtime gated Stopwatch
    // accumulators). The intervals nest, so they are printed as a tree with an explicit
    // unattributed remainder at each level — the remainder is what reveals a missing sub-phase.
    EntityProfilingSnapshot prof = buildResult.EntityScanner?.Layer.Tracker.GetProfilingSnapshot() ?? default;
    ScannerProfilingSnapshot sprof = buildResult.EntityScanner?.GetProfilingSnapshot() ?? default;
    PrintParseProfile(ParseProfilingSnapshot.Read());
    PrintEntityProfile(prof, sprof);

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
                sha256,
                demo.MapName,
                demo.Players.Count(p => p.Value.Team is 2 or 3),
                liveRoundsStarted,
                roundsEnded,
                demo.TickCount,
                demo.TickRate,
                Math.Round(demo.Duration.TotalSeconds, 1),
                GetMachineInfo()
            ),
            new ReportPerformance(
                Math.Round(parseElapsed.TotalMilliseconds, 1),
                Math.Round(buildElapsed.TotalMilliseconds, 1),
                Math.Round(evalElapsed.TotalMilliseconds, 1),
                Math.Round((parseElapsed + buildElapsed + evalElapsed).TotalMilliseconds, 1),
                demo.Frames.Count,
                demo.AllGameEvents.Count,
                messageCount,
                buildResult.Nodes.Count,
                buildResult.Edges.Count,
                buildResult.Chains.Count,
                playerCount,
                timelineEvents,
                new GcReport(
                    gc0After - gc0Before,
                    gc1After - gc1Before,
                    gc2After - gc2Before,
                    gc0After - evalGc0,
                    gc1After - evalGc1,
                    evalAllocBytes
                ),
                BuildEntityProfileReport(prof, sprof)
            ),
            playerReports
        );

        string json = JsonSerializer.Serialize(report, JsonOpts.Default);
        File.WriteAllText(reportPath, json);
        Console.WriteLine();
        Console.WriteLine($"Report written to {reportPath}");
    }

    // ── Golden-stats files (fixtures consumed by parity tests) ───────────
    // Always written when playerReports are available, so a single `--suite`
    // run refreshes every provider's golden file. The bench is the canonical
    // producer for `ours`; leetify files are converted from the existing
    // <id>.leetify.json caches under demos/benchmarks/.
    if (!noGolden && playerReports.Count > 0)
    {
        WriteGoldenStatsFiles(demoPath, sha256, demo, playerReports);
    }

    // ── Per-round MetricTable export (--export=csv|json [--out=<path>]) ──────
    // Runs the PlayerRoundStatsProjector over the full evaluation result and writes one file per
    // emitted table. Requires snapshot mode (the projector reads MessageSnapshots) — incompatible
    // with --bare.
    if (exportFormat is not null)
    {
        WriteRoundExport(exportFormat, exportOut, demoPath, evalResult, demo, bareMode);
    }

    // ── Leetify Comparison ───────────────────────────────────────────────
    if (leetifyJsonPath is not null && File.Exists(leetifyJsonPath) && playerReports.Count > 0)
    {
        CompareWithLeetify(leetifyJsonPath, playerReports);
    }

    return 0;
}

// ── Leetify Comparison ────────────────────────────────────────────────────

static void CompareWithLeetify(string leetifyPath, List<PlayerReport> players)
{
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(leetifyPath));
    JsonElement root = doc.RootElement;
    if (!root.TryGetProperty("playerStats", out JsonElement playerStats))
    {
        return;
    }

    Dictionary<string, JsonElement> leetifyByName = new(StringComparer.OrdinalIgnoreCase);
    foreach (JsonElement p in playerStats.EnumerateArray())
    {
        if (p.TryGetProperty("name", out JsonElement name))
        {
            leetifyByName[name.GetString()!] = p;
        }
    }

    (string OurKey, string LeetifyKey, double Scale, string Fmt)[] statMappings = new (string OurKey, string LeetifyKey, double Scale, string Fmt)[]
    {
        ("TotalK", "totalKills", 1, "F0"), ("TotalD", "totalDeaths", 1, "F0"), ("TotalA", "totalAssists", 1, "F0"), ("EnemyDmg", "totalDamage", 1, "F0"), ("ADR", "dpr", 1, "F1"), ("HS%", "hsp", 100, "F0"), ("KD", "kdRatio", 1, "F2"), ("KAST%", "kast", 100, "F0"), ("HLTV", "hltvRating", 1, "F2"), ("2K", "multi2k", 1, "F0"), ("3K", "multi3k", 1, "F0"), ("4K", "multi4k", 1, "F0"),
        ("5K", "multi5k", 1, "F0"), ("Survived", "roundsSurvived", 1, "F0"), ("TrdK", "tradeKillsSucceeded", 1, "F0"),
        // CTW/TW: detect team mapping (demo team numbers may be swapped vs Leetify)
        ("CTW", "_ctw_auto", 1, "F0"), ("TW", "_tw_auto", 1, "F0"), ("HitFoe", "shotsHitFoe", 1, "F0"), ("Shots", "shotsFired", 1, "F0")
    };

    Console.WriteLine();
    Console.WriteLine("─── Leetify Comparison ─────────────────────────────────");

    int matched = 0, mismatched = 0, skipped = 0;
    List<(string Player, string Stat, double Ours, double Leetify, double Delta)> deltas = new();

    // Auto-detect CTW/TW team mapping by checking first player
    bool ctwSwapped = false;
    PlayerReport? firstPlayer = players.FirstOrDefault(p => p.Team is 2 or 3);
    if (firstPlayer is not null && leetifyByName.TryGetValue(firstPlayer.Name, out JsonElement firstLeet))
    {
        double ourCtw = firstPlayer.Stats.TryGetValue("CTW", out object? cv) && cv is int ci ? ci : 0;
        double ourTw = firstPlayer.Stats.TryGetValue("TW", out object? tv) && tv is int ti ? ti : 0;
        double leetCtw = firstLeet.TryGetProperty("ctRoundsWon", out JsonElement lc) ? lc.GetDouble() : 0;
        double leetTw = firstLeet.TryGetProperty("tRoundsWon", out JsonElement lt) ? lt.GetDouble() : 0;
        double directError = Math.Abs(ourCtw - leetCtw) + Math.Abs(ourTw - leetTw);
        double swappedError = Math.Abs(ourCtw - leetTw) + Math.Abs(ourTw - leetCtw);
        ctwSwapped = swappedError < directError;
    }

    foreach (PlayerReport player in players.Where(p => p.Team is 2 or 3))
    {
        if (!leetifyByName.TryGetValue(player.Name, out JsonElement leetifyPlayer))
        {
            skipped++;
            continue;
        }

        foreach ((string ourKey, string leetKey, double scale, string fmt) in statMappings)
        {
            string resolvedLeetKey = leetKey;
            if (leetKey == "_ctw_auto")
            {
                resolvedLeetKey = ctwSwapped ? "tRoundsWon" : "ctRoundsWon";
            }
            else if (leetKey == "_tw_auto")
            {
                resolvedLeetKey = ctwSwapped ? "ctRoundsWon" : "tRoundsWon";
            }

            if (!player.Stats.TryGetValue(ourKey, out object? ourVal) || ourVal is null)
            {
                continue;
            }

            if (!leetifyPlayer.TryGetProperty(resolvedLeetKey, out JsonElement leetVal))
            {
                continue;
            }

            double ours = ourVal switch
            {
                int i => i,
                double d => d,
                string s when double.TryParse(s, CultureInfo.InvariantCulture, out double sd) => sd,
                _ => double.NaN
            };
            if (double.IsNaN(ours))
            {
                continue;
            }

            double leetify = leetVal.ValueKind == JsonValueKind.Number ? leetVal.GetDouble() * scale : double.NaN;
            if (double.IsNaN(leetify))
            {
                continue;
            }

            double delta = ours - leetify;
            if (Math.Abs(delta) < 0.01)
            {
                matched++;
            }
            else
            {
                mismatched++;
                deltas.Add((player.Name, ourKey, ours, leetify, delta));
            }
        }
    }

    if (deltas.Count > 0)
    {
        int nameWidth = Math.Max(6, deltas.Max(d => d.Player.Length) + 1);
        Console.WriteLine($"  {"Player".PadRight(nameWidth)} {"Stat",-10} {"Ours",10} {"Leetify",10} {"Delta",10}");
        Console.WriteLine($"  {"".PadRight(nameWidth, '-')} {"".PadRight(10, '-')} {"".PadRight(10, '-')} {"".PadRight(10, '-')} {"".PadRight(10, '-')}");
        foreach ((string player, string stat, double ours, double leetify, double delta) in deltas.OrderBy(d => d.Player).ThenBy(d => d.Stat))
        {
            string sign = delta > 0 ? "+" : "";
            Console.WriteLine($"  {player.PadRight(nameWidth)} {stat,-10} {ours,10:F2} {leetify,10:F2} {sign + delta.ToString("F2", CultureInfo.InvariantCulture),10}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  Matched: {matched}  Mismatched: {mismatched}  Skipped: {skipped} players");
    if (matched + mismatched > 0)
    {
        Console.WriteLine($"  Accuracy: {(double)matched / (matched + mismatched) * 100:F1}%");
    }

    // ── Per-stat mismatch summary ────────────────────────────────────────────
    // The flat matched/mismatched count buries WHICH stats account for the
    // divergence. This breakdown shows the distribution so per-stat tolerance
    // tightening (or parser work) can be prioritised by impact.
    if (deltas.Count > 0)
    {
        var byStat = deltas
            .GroupBy(d => d.Stat)
            .Select(g => new
            {
                Stat = g.Key,
                Count = g.Count(),
                MeanDelta = g.Average(d => d.Delta),
                MaxAbs = g.Max(d => Math.Abs(d.Delta))
            })
            .OrderByDescending(s => s.Count)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("  Per-stat mismatch breakdown:");
        Console.WriteLine($"    {"Stat",-12} {"Count",5} {"MeanΔ",10} {"MaxAbsΔ",10}");
        foreach (var s in byStat)
        {
            Console.WriteLine($"    {s.Stat,-12} {s.Count,5} {s.MeanDelta,10:F3} {s.MaxAbs,10:F3}");
        }
    }
}

// ── Golden-stats producer ─────────────────────────────────────────────────
//
// Refreshes the canonical golden files consumed by parity tests under
// tests/fixtures/<demo-id>/. One file per provider:
//
//   tests/fixtures/<demo-id>/ours.golden.json     — produced from this run.
//
// leetify.golden.json is no longer written: CS2DemoKit.Analysis 0.9.1 retired the converter that
// produced it. The live Leetify comparison above is unaffected — it parses the raw cached JSON
// itself and never used the package.
//
// The directory pattern (rather than a flat layout) anticipates additional
// providers — `hltv.golden.json`, `expected.golden.json` — without renaming
// existing files.
static void WriteGoldenStatsFiles(
    string demoPath, string demoSha256, ParsedDemo demo,
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

    GoldenStatsDocument ours = OursGoldenStatsConverter.Convert(
        Path.GetFileName(demoPath),
        demoSha256,
        demo,
        oursInputs,
        GetGitCommit());

    string oursPath = Path.Combine(fixturesRoot, "ours.golden.json");
    GoldenStatsSerializer.WriteToFile(ours, oursPath);
    Console.WriteLine($"Golden: {Path.GetRelativePath(FindRepoRoot(), oursPath)}");
}

// Runs the PlayerRoundStatsProjector over the evaluation result and writes one file per emitted
// MetricTable in the requested format. Sibling of WriteGoldenStatsFiles — per-round (not per-game).
static void WriteRoundExport(string format, string? outPath, string demoPath, EvaluationResult? result, ParsedDemo demo, bool bareMode)
{
    if (bareMode || result is null)
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
    IReadOnlyList<MetricTable> tables = projector.Project(result, demo);

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

static int CountWarmupRounds(ParsedDemo demo)
{
    int warmup = 0;
    bool matchStarted = false;
    foreach (GameEvent e in demo.AllGameEvents)
    {
        if (e.Payload is BeginNewMatchEvent)
        {
            matchStarted = true;
            continue;
        }

        if (e.Payload is RoundFreezeEndEvent)
        {
            if (!matchStarted)
            {
                warmup++;
            }
        }
    }

    return warmup;
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
            string leetifyPath = Path.Combine(benchDir, $"{id}.leetify.json");
            return new TestCase(id, demoPath, File.Exists(leetifyPath) ? leetifyPath : null);
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

    double precompute = Ms(sprof.PrecomputeTicks);
    Console.WriteLine($"  Parallel precompute        {precompute,9:F1} ms  {Mib(sprof.PrecomputeAlloc),9:F1} MiB   (up-front chunked parallel decode)");
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

// ── Parse-pipeline profile ───────────────────────────────────────────────────

// Pass boundaries from DemoParser (gated at runtime on Profiling.Enabled), timed by brackets OUTSIDE the loops
// so the protected parse loop is never restructured. Pass-2 shows wall-clock only (its parallel workers'
// allocation has no correct outside-loop figure); passes 1 & 3 are sequential so their alloc is exact.
// Read via ParseProfilingSnapshot.Read(); Enabled=false (just the "no data" line) when the last parse was unprofiled.
static void PrintParseProfile(ParseProfilingSnapshot p)
{
    Console.WriteLine();
    Console.WriteLine("─── Parse-Pipeline Profile ──────────────────────────────");
    if (!p.Enabled)
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

    Console.WriteLine($"  Pass 1 header scan       {Ms(p.Pass1HeaderTicks),9:F1} ms  {Mib(p.Pass1Alloc),9:F1} MiB   ({p.FrameCount:N0} frames, {p.CompressedFrames:N0} compressed)");
    Console.WriteLine($"  Pass 2 parallel decode   {Ms(p.Pass2WallTicks),9:F1} ms  {"—",9}      (wall-clock; per-worker alloc not isolable outside the loop — dotnet-trace for the decompress/parse split)");
    Console.WriteLine($"  Pass 3 enrich            {Ms(p.Pass3EnrichTicks),9:F1} ms  {Mib(p.Pass3Alloc),9:F1} MiB");
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
        Ms(sprof.SeekTicks),
        Ms(sprof.ProviderPollTicks),
        Ms(sprof.ProjectileScanTicks),
        Ms(sprof.SnapshotTicks),
        Ms(prof.PacketEntitiesTicks),
        Ms(prof.FieldPathTicks),
        Ms(prof.FieldValueTicks),
        Ms(prof.DescriptorBuildTicks),
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

// ── Report Records ─────────────────────────────────────────────────────────

internal sealed record TestCase(string Id, string DemoPath, string? LeetifyJson);

internal sealed record BenchReport(ReportMetadata Metadata, ReportPerformance Performance, List<PlayerReport> Players);

internal sealed record ReportMetadata(
    DateTimeOffset Timestamp,
    string GitCommit,
    string DemoFile,
    double DemoSizeMb,
    string DemoSha256,
    string Map,
    int PlayerCount,
    int RoundsStarted,
    int RoundsEnded,
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

internal sealed record ReportPerformance(
    double ParseMs,
    double BuildMs,
    double EvalMs,
    double TotalMs,
    int FrameCount,
    int GameEventCount,
    int MessageCount,
    int NodeCount,
    int EdgeCount,
    int ChainCount,
    int MaterializedPlayers,
    int TimelineEvents,
    GcReport Gc,
    ReportEntityProfile? EntityProfile = null);

internal sealed record GcReport(int Gen0, int Gen1, int Gen2, int EvalGen0, int EvalGen1, long AllocBytes);

/// <summary>
///     Entity-decode sub-phase timings (all milliseconds) captured when a profiled run ran
///     (<see cref="CS2DemoKit.Parser.Profiling.Enabled" />). Null in the report when no profiled run captured data.
///     Intervals nest:
///     <c>
///         ScannerSeekMs ⊇ PacketEntitiesMs ⊇ (FieldPathMs + FieldValueMs +
///         DescriptorBuildMs)
///     </c>
///     .
/// </summary>
internal sealed record ReportEntityProfile(
    double ScannerSeekMs,
    double ProviderPollMs,
    double ProjectileScanMs,
    double PreFrameSnapshotMs,
    double PacketEntitiesMs,
    double FieldPathMs,
    double FieldValueMs,
    double DescriptorBuildMs,
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
