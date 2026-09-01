#region

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace AnalysisBench;

/// <summary>
///     <c>gc-sweep</c>: measures parse/analysis cost AND resource footprint under every GC
///     configuration worth considering, so the Server-vs-Workstation decision is made on numbers
///     rather than on the framework default.
///     <para>
///         <b>Why this spawns child processes.</b> Server/Workstation, concurrent (background) GC,
///         RetainVM, heap count, ConserveMemory and DATAS are all read ONCE by the CLR at startup and
///         are immutable thereafter. No in-process API can change them. So each configuration must run
///         in its own process, launched with the matching <c>DOTNET_*</c> environment variables. The
///         parent enumerates the matrix, runs one child per config, and tabulates the results.
///     </para>
///     <para>
///         <b>Hex gotcha.</b> Numeric <c>DOTNET_GC*</c> knobs are parsed as HEX (the legacy
///         <c>COMPlus_</c> convention), not decimal. Every numeric value used here is ≤ 9, where hex and
///         decimal coincide. Keep it that way, or a "16" silently becomes 22.
///     </para>
///     <para>
///         The footprint numbers that matter are the FINAL ones: they are taken after the demo is
///         dropped and an aggressive compacting collect has run, which models "user closed the demo".
///         A configuration can release every managed byte and still hold gigabytes committed.
///     </para>
/// </summary>
internal static class GcSweepCommand
{
    private const double Mb = 1024 * 1024;

    private static readonly JsonSerializerOptions _indented = new()
    {
        WriteIndented = true
    };

    // The matrix. Server+Concurrent is what the Desktop app ships today (csproj sets
    // ServerGarbageCollection/ConcurrentGarbageCollection true), so it is the baseline to beat.
    private static readonly GcConfig[] _matrix =
    [
        GcConfig.Of("workstation, concurrent  (.NET client default)",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "1")),
        GcConfig.Of("workstation, NON-concurrent",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "0")),
        GcConfig.Of("workstation, concurrent, RetainVM",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCRetainVM", "1")),
        GcConfig.Of("workstation, concurrent, ConserveMemory=9",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCConserveMemory", "9")),

        // Workstation's throughput gap is a gen0-cadence problem, not a fundamental one: the default gen0
        // budget is a few MB, so a 3.5 GB-allocating parse takes ~420 gen0 collections where Server GC
        // takes 3. A bigger gen0 budget trades peak footprint for far fewer collections.
        // NOTE: DOTNET_GCgen0size is parsed as HEX bytes: 4000000 here is 0x4000000 = 64 MB, not 4 million.
        GcConfig.Of("workstation, concurrent, gen0=32MB",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCgen0size", "2000000")),
        GcConfig.Of("workstation, concurrent, gen0=64MB",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCgen0size", "4000000")),
        GcConfig.Of("workstation, concurrent, gen0=128MB",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCgen0size", "8000000")),
        GcConfig.Of("workstation, concurrent, gen0=256MB",
            ("DOTNET_gcServer", "0"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCgen0size", "10000000")),

        GcConfig.Of("SERVER, concurrent  ← SHIPPED TODAY",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1")),
        GcConfig.Of("SERVER, NON-concurrent",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "0")),
        GcConfig.Of("SERVER, concurrent, RetainVM",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCRetainVM", "1")),
        GcConfig.Of("SERVER, concurrent, DATAS off",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"),
            ("DOTNET_GCDynamicAdaptationMode", "0")),
        GcConfig.Of("SERVER, concurrent, ConserveMemory=5",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCConserveMemory", "5")),
        GcConfig.Of("SERVER, concurrent, ConserveMemory=9",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCConserveMemory", "9")),
        // Can Server GC's speed be kept while capping what it holds? A hard limit forces collection
        // rather than growth. HEX bytes: C0000000 = 3 GiB, 80000000 = 2 GiB. Peak managed heap is ~2.9 GB
        // even on Workstation, so a limit below that should be expected to OOM. That IS the finding.
        // The "squeeze when idle" idea: keep Server GC's throughput, then force it to hand memory back at
        // the moment we know the app is going idle (demo closed). GC.RefreshMemoryLimit re-reads
        // GCHeapHardLimit at RUNTIME, so a temporary low limit should compel the GC to shrink to fit.
        // GCSWEEP_SQUEEZE is our own env var, not a runtime knob. The probe reads it.
        GcConfig.Of("SERVER, concurrent + idle squeeze (RefreshMemoryLimit)",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("GCSWEEP_SQUEEZE", "1")),

        GcConfig.Of("SERVER, concurrent, HeapHardLimit=3GB",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCHeapHardLimit", "C0000000")),
        GcConfig.Of("SERVER, concurrent, HeapHardLimit=2GB",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCHeapHardLimit", "80000000")),

        GcConfig.Of("SERVER, concurrent, HeapCount=2",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCHeapCount", "2")),
        GcConfig.Of("SERVER, concurrent, HeapCount=4",
            ("DOTNET_gcServer", "1"), ("DOTNET_gcConcurrent", "1"), ("DOTNET_GCHeapCount", "4"))
    ];

    // ── Parent: run the matrix ────────────────────────────────────────────────

    public static int Run(string[] positional, Dictionary<string, string> named)
    {
        if (positional.Length == 0)
        {
            Console.Error.WriteLine("usage: AnalysisBench gc-sweep <demo.dem> [--filter=<substr>] [--json=<path>]");
            return 2;
        }

        string demoPath = positional[0];
        if (!File.Exists(demoPath))
        {
            Console.Error.WriteLine($"demo not found: {demoPath}");
            return 2;
        }

        string? host = Environment.ProcessPath;
        if (host is null)
        {
            Console.Error.WriteLine("cannot resolve Environment.ProcessPath — needed to spawn per-config children");
            return 2;
        }

        GcConfig[] configs = named.TryGetValue("--filter", out string? filter)
            ? [.. _matrix.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))]
            : _matrix;

        Console.WriteLine($"GC sweep — {configs.Length} configurations × 1 run");
        Console.WriteLine($"Demo:  {Path.GetFileName(demoPath)}");
        Console.WriteLine($"Host:  {host}");
        Console.WriteLine($"Cores: {Environment.ProcessorCount}   (Server GC creates one heap per core by default)");
        Console.WriteLine();

        List<ProbeResult> results = [];
        foreach (GcConfig config in configs)
        {
            Console.Write($"  running: {config.Name,-46} ");
            ProbeResult? r = RunChild(host, demoPath, config);
            if (r is null)
            {
                Console.WriteLine("FAILED");
                continue;
            }

            results.Add(r with
            {
                Config = config.Name
            });
            Console.WriteLine($"{r.TotalMs,7:F0} ms   final RSS {r.FinalRssMb,7:F0} MB");
        }

        Console.WriteLine();
        PrintTable(results);

        if (named.TryGetValue("--json", out string? jsonPath))
        {
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(results, _indented));
            Console.WriteLine($"\nJSON: {jsonPath}");
        }

        return 0;
    }

    private static ProbeResult? RunChild(string host, string demoPath, GcConfig config)
    {
        ProcessStartInfo psi = new(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("gc-sweep-probe");
        psi.ArgumentList.Add(demoPath);
        foreach ((string key, string value) in config.Env)
        {
            psi.Environment[key] = value;
        }

        try
        {
            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(20 * 60 * 1000);

            // The probe emits exactly one JSON line prefixed with a sentinel, so ordinary logging
            // from the parse/analysis path cannot be mistaken for the result.
            string? line = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("@GCPROBE ", StringComparison.Ordinal));
            if (line is null)
            {
                Console.Error.WriteLine($"    (no probe output; stderr: {stderr.Trim()})");
                return null;
            }

            return JsonSerializer.Deserialize<ProbeResult>(line["@GCPROBE ".Length..]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    child failed: {ex.Message}");
            return null;
        }
    }

    private static void PrintTable(List<ProbeResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        Console.WriteLine("PERFORMANCE (lower is better)");
        Console.WriteLine(
            $"{"",-46} {"parse ms",9} {"eval ms",9} {"total ms",9} {"gen0",6} {"gen2",5} {"pause ms",9} {"alloc GB",9}");
        foreach (ProbeResult r in results)
        {
            Console.WriteLine($"{r.Config,-46} {r.ParseMs,9:F0} {r.EvalMs,9:F0} {r.TotalMs,9:F0} "
                              + $"{r.Gen0,6} {r.Gen2,5} {r.PauseMs,9:F0} {r.AllocatedGb,9:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("FOOTPRINT (MB — 'final' is after the demo is dropped + an aggressive compacting collect)");
        Console.WriteLine(
            $"{"",-46} {"LIVE heap",9} {"peak heap",9} {"peak RSS",9} {"fin heap",9} {"fin commit",10} {"fin RSS",9}");
        foreach (ProbeResult r in results)
        {
            Console.WriteLine($"{r.Config,-46} {r.LiveHeapMb,9:F0} {r.PeakManagedMb,9:F0} {r.PeakRssMb,9:F0} "
                              + $"{r.FinalManagedMb,9:F0} {r.FinalCommittedMb,10:F0} {r.FinalRssMb,9:F0}");
        }

        ProbeResult best = results.MinBy(r => r.FinalRssMb)!;
        ProbeResult fastest = results.MinBy(r => r.TotalMs)!;
        Console.WriteLine();
        Console.WriteLine($"Lowest final RSS : {best.Config}  ({best.FinalRssMb:F0} MB, {best.TotalMs:F0} ms)");
        Console.WriteLine($"Fastest total    : {fastest.Config}  ({fastest.TotalMs:F0} ms, {fastest.FinalRssMb:F0} MB)");
    }

    // ── Child: measure one configuration ──────────────────────────────────────

    public static int RunProbe(string[] positional)
    {
        string demoPath = positional[0];

        // Sample RSS on a background thread: peak footprint is a transient the phase timings never see,
        // and it is the number that decides whether a machine swaps during a parse.
        using Sampler sampler = new();
        sampler.Start();

        Phase phase = Measure(demoPath, sampler);

        sampler.Stop();

        // Model the app's Close. Measure() confined every demo reference to ITS frame, which is gone by
        // now, so this collect sees the same reachability an idle app does after closing a demo. Nulling
        // locals in place is not enough: the `using` in a method introduces a try/finally that can keep
        // slots live to the end of the frame, which made an earlier version of this probe report the whole
        // 820 MB demo as "final heap".
        GCSettings.LargeObjectHeapCompactionMode =
            GCLargeObjectHeapCompactionMode.CompactOnce;
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }

        // Optional second stage: the "we know we are idle now, give it back" squeeze. Sets a low heap hard
        // limit at runtime and refreshes, which should force the GC to decommit down to it, then lifts the
        // limit again so a subsequent load is unconstrained.
        if (Environment.GetEnvironmentVariable("GCSWEEP_SQUEEZE") == "1")
        {
            try
            {
                AppContext.SetData("GCHeapHardLimit", (ulong)(256L * 1024 * 1024));
                GC.RefreshMemoryLimit();
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                Console.Error.WriteLine("[squeeze] applied 256 MB hard limit + RefreshMemoryLimit");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[squeeze] FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Process proc = Process.GetCurrentProcess();
        proc.Refresh();

        ProbeResult probe = new(
            "", phase.ReadMs, phase.ParseMs, phase.BuildMs, phase.EvalMs, phase.TotalMs,
            sampler.PeakManaged / Mb, sampler.PeakRss / Mb,
            GC.GetTotalMemory(true) / Mb, GC.GetGCMemoryInfo().TotalCommittedBytes / Mb, proc.WorkingSet64 / Mb,
            phase.Gen0, phase.Gen1, phase.Gen2, phase.PauseMs, phase.AllocatedGb, phase.LiveHeapMb);

        Console.WriteLine("@GCPROBE " + JsonSerializer.Serialize(probe));
        return 0;
    }

    /// <summary>
    ///     Runs read → parse → build → evaluate and returns ONLY scalars. Every reference to the demo, the
    ///     rule graph and the evaluation result lives and dies inside this frame, so the caller's collect
    ///     measures a genuinely closed demo. <see cref="MethodImplAttribute" /> with
    ///     <see cref="MethodImplOptions.NoInlining" /> keeps the JIT from hoisting those locals into the
    ///     caller and defeating the whole point.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Phase Measure(string demoPath, Sampler sampler)
    {
        long allocBefore = GC.GetTotalAllocatedBytes();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        int gen0Before = GC.CollectionCount(0), gen1Before = GC.CollectionCount(1), gen2Before = GC.CollectionCount(2);

        long t0 = Stopwatch.GetTimestamp();
        byte[] bytes = File.ReadAllBytes(demoPath);
        TimeSpan readElapsed = Stopwatch.GetElapsedTime(t0);

        long t1 = Stopwatch.GetTimestamp();
        ParsedDemo demo = DemoParser.Parse(bytes);
        TimeSpan parseElapsed = Stopwatch.GetElapsedTime(t1);

        long t2 = Stopwatch.GetTimestamp();
        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(FindRulesDir());
        if (!loaded.Success)
        {
            throw new RuleConfigException(loaded.Errors);
        }

        BuildResult build = DemoAnalysis.Build(demo, loaded.Rulesets);
        TimeSpan buildElapsed = Stopwatch.GetElapsedTime(t2);

        long t3 = Stopwatch.GetTimestamp();
        EvaluationResult result = DemoAnalysis.Evaluate(demo, build).Snapshots!;
        TimeSpan evalElapsed = Stopwatch.GetElapsedTime(t3);
        int messages = result.Messages.Count; // forces the result to be genuinely materialized
        TimeSpan total = Stopwatch.GetElapsedTime(t0);

        // LIVE heap with the demo + rule graph + evaluation still held, i.e. what the app occupies while
        // a demo is open. Distinct from peak (dominated by transient parse garbage) and from final (after
        // close). This is the number a cache-size change like lazy field descriptors moves.
        long liveHeap = GC.GetTotalMemory(true);

        return new Phase(
            readElapsed.TotalMilliseconds, parseElapsed.TotalMilliseconds, buildElapsed.TotalMilliseconds,
            evalElapsed.TotalMilliseconds, total.TotalMilliseconds,
            GC.CollectionCount(0) - gen0Before, GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds,
            (GC.GetTotalAllocatedBytes() - allocBefore) / 1024.0 / 1024.0 / 1024.0,
            messages, liveHeap / Mb);
    }

    private static string FindRulesDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "rules");
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "rules");
    }

    /// <summary>One GC configuration: a display name plus the environment it implies.</summary>
    private sealed record GcConfig(string Name, Dictionary<string, string> Env)
    {
        public static GcConfig Of(string name, params (string Key, string Value)[] vars) =>
            new(name, vars.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));
    }

    private sealed record Phase(
        double ReadMs,
        double ParseMs,
        double BuildMs,
        double EvalMs,
        double TotalMs,
        int Gen0,
        int Gen1,
        int Gen2,
        double PauseMs,
        double AllocatedGb,
        int Messages,
        double LiveHeapMb);

    /// <summary>Background peak-footprint sampler: peak RSS/heap are transients the phase timings miss.</summary>
    private sealed class Sampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public long PeakManaged;
        public long PeakRss;

        public void Dispose() => _cts.Dispose();

        public void Start()
        {
            Thread t = new(() =>
            {
                Process self = Process.GetCurrentProcess();
                while (!_cts.IsCancellationRequested)
                {
                    self.Refresh();
                    PeakRss = Math.Max(PeakRss, self.WorkingSet64);
                    PeakManaged = Math.Max(PeakManaged, GC.GetTotalMemory(false));
                    Thread.Sleep(25);
                }
            })
            {
                IsBackground = true
            };
            t.Start();
        }

        public void Stop() => _cts.Cancel();
    }

    internal sealed record ProbeResult(
        string Config,
        double ReadMs,
        double ParseMs,
        double BuildMs,
        double EvalMs,
        double TotalMs,
        double PeakManagedMb,
        double PeakRssMb,
        double FinalManagedMb,
        double FinalCommittedMb,
        double FinalRssMb,
        int Gen0,
        int Gen1,
        int Gen2,
        double PauseMs,
        double AllocatedGb,
        double LiveHeapMb);
}
