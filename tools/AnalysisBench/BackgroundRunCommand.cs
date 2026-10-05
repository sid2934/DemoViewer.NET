#region

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.DemoProcessing;

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
///     <para>
///         <c>--read=app-retained</c>: what the queue did per demo before the forward pass: a mapped parse
///         without user commands, the highlights build, a separate round_facts build, the library's
///         final-state replay and round derivation. <c>--read=app-forward</c>: the queue's forward pass for
///         the same three consumers (<c>ForwardDemoPass</c>, one merged build). Both imply <c>--eval</c>.
///         <c>--without=&lt;ruleset id&gt;</c> drops one ruleset from the read, to price it against a run with it.
///     </para>
///     <para>
///         <c>--with=&lt;file&gt;</c> adds one <c>*.rules.yaml</c> to the read, as an extension's ruleset rides the
///         merged run. <c>--outputs=&lt;a,b&gt;|none|all</c> sets the tables <c>app-forward</c> records: the queue
///         records only those of stamped rulesets whose stored outputs are stale, and omitting it keeps
///         <c>round_facts</c> alone.
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
        bool eval = flags.Contains("--eval") || read.StartsWith("app-", StringComparison.Ordinal);
        string? without = named.GetValueOrDefault("--without");
        string? with = named.GetValueOrDefault("--with");
        string? outputsArg = named.GetValueOrDefault("--outputs");

        RuleConfigLoadResult? rules = null;
        if (eval)
        {
            rules = YamlConfigLoader.TryLoadDirectory(FindRulesDir());
            if (!rules.Success)
            {
                throw new RuleConfigException(rules.Errors);
            }

            if (without is not null)
            {
                rules = rules with { Rulesets = [.. rules.Rulesets.Where(r => r.Id != without)] };
            }

            if (with is not null)
            {
                RuleConfigLoadResult extra = YamlConfigLoader.LoadDocuments([(Path.GetFileName(with), File.ReadAllText(with))]);
                if (!extra.Success)
                {
                    throw new RuleConfigException(extra.Errors);
                }

                rules = rules with { Rulesets = [.. rules.Rulesets, .. extra.Rulesets] };
            }
        }

        IReadOnlySet<string>? outputs = outputsArg switch
        {
            null => null,
            "none" => new HashSet<string>(StringComparer.Ordinal),
            "all" => new HashSet<string>(rules?.Rulesets.SelectMany(r => r.Show?.Tables.Select(t => t.Name) ?? []) ?? [], StringComparer.Ordinal),
            _ => new HashSet<string>(outputsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal)
        };

        PeakSampler sampler = new();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        long alloc0 = GC.GetTotalAllocatedBytes();
        long t0 = Stopwatch.GetTimestamp();

        for (int i = 0; i < demos.Length; i++)
        {
            long tj = Stopwatch.GetTimestamp();
            long sizeBytes = new FileInfo(demos[i]).Length;
            (double parseMs, int highlights) = Job(demos[i], read, plan, rules, outputs);
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
                tableRows = _lastTableRows,
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
            without,
            with,
            outputs = outputsArg,
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

    // Rows of the tables the last app-forward job recorded; -1 on every other read.
    private static int _lastTableRows = -1;

    // NoInlining keeps the demo and the evaluation result out of the caller's frame, so they are
    // unreachable once the job returns, as they are when the queue moves on.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double ParseMs, int Highlights) Job(string path, string read, string? plan, RuleConfigLoadResult? rules,
        IReadOnlySet<string>? outputs)
    {
        AnalysisOptions bare = new()
        {
            CaptureSnapshots = false
        };
        long t = Stopwatch.GetTimestamp();
        if (read == "app-forward")
        {
            using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
            ForwardDemoResult pass = ForwardDemoPass.Run(reader, ForwardNeeds.FinalState | ForwardNeeds.Rules, rules!.Rulesets, outputs: outputs);
            // The recorded tables are projected as the facts and Round Facts passes do, so their cost lands in the job.
            _lastTableRows = pass.Run!.ProjectConfiguredOutputs().Sum(table => table.Rows.Count);
            return (Stopwatch.GetElapsedTime(t).TotalMilliseconds, pass.Run!.Highlights.Count);
        }

        if (read == "app-retained")
        {
            return AppRetained(path, rules!, t);
        }

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

    // The pre-forward queue job: one retained parse, then each consumer on it in turn.
    private static (double ParseMs, int Highlights) AppRetained(string path, RuleConfigLoadResult rules, long t)
    {
        AnalysisOptions bare = new() { CaptureSnapshots = false };
        ParsedDemo demo = BackgroundPlans.Parse(path, "mmap", "no-usercmds");
        double parseMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;

        EntityTracker tracker = FinalTeamState.NewTracker();
        tracker.ReplayToIndex(demo.Frames.Count - 1, demo.Frames);
        _ = FinalTeamState.Read(tracker, demo.Players.Keys);
        _ = ClipRounds.Derive(demo);

        List<RulesetDoc> highlights = [.. rules.Rulesets.Where(r => r.Id != ForwardDemoPass.RoundFactsTable)];
        int n = DemoAnalysis.Evaluate(demo, DemoAnalysis.Build(demo, highlights), bare).Highlights.Count;
        RulesetDoc facts = rules.Rulesets.First(r => r.Id == ForwardDemoPass.RoundFactsTable);
        _ = DemoAnalysis.Evaluate(demo, DemoAnalysis.Build(demo, [facts]), bare).ProjectConfiguredOutputs(demo);
        return (parseMs, n);
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
