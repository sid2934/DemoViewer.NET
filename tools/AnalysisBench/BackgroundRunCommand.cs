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
///     <c>bg-run --list=&lt;file&gt;</c>: parses the listed demos back to back the way the processing queue
///     does, one at a time, each released before the next. The GC configuration is the process's own
///     <c>DOTNET_*</c> environment, so a driver runs one process per configuration.
///     <para>
///         <c>--read=mmap|bytes</c>: <c>MemoryMappedDemoSource.ParseFile</c> or
///         <c>DemoParser.Parse(File.ReadAllBytes)</c>. <c>--eval</c>: the highlights harvester's snapshot-free rules evaluation per demo.
///         <c>--compact=none|end|each</c>: the app's <c>HeapCompactor</c> sequence never, once after the last
///         demo (a queue drain), or after every demo. <c>--plan=&lt;name&gt;</c>: see <see cref="BackgroundPlans" />.
///     </para>
///     Emits one <c>@BGJOB</c> JSON line per demo and one <c>@BGRUN</c> line at the end.
/// </summary>
internal static class BackgroundRunCommand
{
    private const double Mb = 1024 * 1024;

    public static int Run(Dictionary<string, string> named, HashSet<string> flags)
    {
        string[] demos = File.ReadAllLines(named["--list"]).Where(l => l.Length > 0).ToArray();
        string read = named.GetValueOrDefault("--read", "mmap");
        string compact = named.GetValueOrDefault("--compact", "none");
        string? plan = named.GetValueOrDefault("--plan");
        bool eval = flags.Contains("--eval");

        RuleConfigLoadResult? rules = null;
        if (eval)
        {
            rules = YamlConfigLoader.TryLoadDirectory(FindRulesDir());
            if (!rules.Success)
            {
                throw new RuleConfigException(rules.Errors);
            }
        }

        PeakSampler sampler = new();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        long alloc0 = GC.GetTotalAllocatedBytes();
        long t0 = Stopwatch.GetTimestamp();

        for (int i = 0; i < demos.Length; i++)
        {
            long tj = Stopwatch.GetTimestamp();
            long sizeBytes = new FileInfo(demos[i]).Length;
            (double parseMs, int highlights) = Job(demos[i], read, plan, rules);
            if (compact == "each")
            {
                Compact();
            }

            GCMemoryInfo gi = GC.GetGCMemoryInfo();
            Console.WriteLine("@BGJOB " + JsonSerializer.Serialize(new
            {
                i,
                demo = Path.GetFileName(demos[i]),
                sizeMb = sizeBytes / Mb,
                parseMs,
                highlights,
                jobMs = Stopwatch.GetElapsedTime(tj).TotalMilliseconds,
                committedMb = gi.TotalCommittedBytes / Mb,
                heapMb = gi.HeapSizeBytes / Mb,
                fragMb = gi.FragmentedBytes / Mb,
                lohMb = gi.GenerationInfo[3].SizeAfterBytes / Mb,
                lohFreeMb = gi.GenerationInfo[3].FragmentationAfterBytes / Mb,
                wsMb = Environment.WorkingSet / Mb
            }));
        }

        double wallMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Snapshot preCompact = Snapshot.Take();
        if (compact == "end")
        {
            Compact();
        }

        Snapshot end = Snapshot.Take();
        sampler.Stop();

        Console.WriteLine("@BGRUN " + JsonSerializer.Serialize(new
        {
            server = GCSettings.IsServerGC,
            conserve = Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory"),
            datas = Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode"),
            read,
            compact,
            plan,
            eval,
            demos = demos.Length,
            wallMs,
            peakWsMb = sampler.PeakWs / Mb,
            peakHeapMb = sampler.PeakHeap / Mb,
            preCompact,
            end,
            gen0 = GC.CollectionCount(0) - gen0,
            gen1 = GC.CollectionCount(1) - gen1,
            gen2 = GC.CollectionCount(2) - gen2,
            pauseMs = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds,
            allocGb = (GC.GetTotalAllocatedBytes() - alloc0) / (Mb * 1024)
        }));
        return 0;
    }

    // NoInlining keeps the demo and the evaluation result out of the caller's frame, so they are
    // unreachable once the job returns, as they are when the queue moves on.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double ParseMs, int Highlights) Job(string path, string read, string? plan, RuleConfigLoadResult? rules)
    {
        AnalysisOptions bare = new()
        {
            CaptureSnapshots = false
        };
        long t = Stopwatch.GetTimestamp();
        if (read == "forward")
        {
            // The forward reader narrows the decode to the graph's own PlanDecode and keeps no frames.
            int n = DemoAnalysis.Run(path, rules!.Rulesets, bare).Highlights.Count;
            return (Stopwatch.GetElapsedTime(t).TotalMilliseconds, n);
        }

        ParsedDemo demo = plan is not null
            ? BackgroundPlans.Parse(path, read, plan)
            : read == "bytes"
                ? DemoParser.Parse(File.ReadAllBytes(path))
                : MemoryMappedDemoSource.ParseFile(path);
        double parseMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        if (rules is null)
        {
            return (parseMs, -1);
        }

        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);
        return (parseMs, DemoAnalysis.Evaluate(demo, build, bare).Highlights.Count);
    }

    private static void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    private static string FindRulesDir()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "rules");
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "rules");
    }

    // The last GC's figures, without forcing one: "end" with --compact=none is what the app would hold.
    internal sealed record Snapshot(double CommittedMb, double HeapMb, double FragMb, double LohMb, double LohFreeMb, double WsMb)
    {
        public static Snapshot Take()
        {
            GCMemoryInfo gi = GC.GetGCMemoryInfo();
            return new Snapshot(gi.TotalCommittedBytes / Mb, gi.HeapSizeBytes / Mb, gi.FragmentedBytes / Mb,
                gi.GenerationInfo[3].SizeAfterBytes / Mb, gi.GenerationInfo[3].FragmentationAfterBytes / Mb,
                Environment.WorkingSet / Mb);
        }
    }

    private sealed class PeakSampler
    {
        private volatile bool _stop;
        public long PeakHeap;
        public long PeakWs;

        public PeakSampler() => new Thread(() =>
        {
            while (!_stop)
            {
                PeakWs = Math.Max(PeakWs, Environment.WorkingSet);
                PeakHeap = Math.Max(PeakHeap, GC.GetTotalMemory(false));
                Thread.Sleep(20);
            }
        })
        {
            IsBackground = true
        }.Start();

        public void Stop() => _stop = true;
    }
}
