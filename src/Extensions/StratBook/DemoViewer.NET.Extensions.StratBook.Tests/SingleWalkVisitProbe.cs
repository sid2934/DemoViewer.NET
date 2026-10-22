#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The pack-on visit harness: one demo, any number of scenarios and trials, one process, driven by
///     <c>tools/single-walk-bench/run.sh</c>. Each cell runs in its own fresh config directory and its own
///     booted app, and prints one <c>@SW_VISIT</c> JSON line with the visit's wall time, peak working set and
///     managed heap over the visit, allocated bytes and GC counts, the decode count from the engine's meter
///     split into analysis and shell decodes with the host hooks' count by site beside it, and per-pass time.
///     Wall time, memory and allocation are reported, never gated. The gate is the decode counters, which are
///     exact integers: every cell is run and printed first, then the test fails if any missed its target.
///     <para>
///         Skipped unless <c>SW_DEMO</c>, <c>SW_VISIT</c> (comma separated) and <c>SW_CONFIG_ROOT</c> are set.
///         <c>SW_TRIALS</c> (default 1) repeats each scenario; <c>SW_WARMUP=1</c> runs one extra leading cell
///         per scenario, printed with trial 0, for a wall-time run that wants JIT out of the first number.
///     </para>
/// </summary>
[NotInParallel]
public class SingleWalkVisitProbe
{
    [Test]
    [Category("Environmental")]
    public async Task Visit()
    {
        string demo = Require("SW_DEMO");
        string[] scenarios = Require("SW_VISIT").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string root = Require("SW_CONFIG_ROOT");
        foreach (string scenario in scenarios)
        {
            if (scenario is not (SingleWalkSession.Sweep or SingleWalkSession.Open or SingleWalkSession.OpenStale or SingleWalkSession.Recheck))
            {
                throw new ArgumentException(
                    $"SW_VISIT entries must be {SingleWalkSession.Sweep}, {SingleWalkSession.Open}, {SingleWalkSession.OpenStale} or {SingleWalkSession.Recheck}, not {scenario}");
            }
        }

        int trials = int.TryParse(Environment.GetEnvironmentVariable("SW_TRIALS"), out int t) && t > 0 ? t : 1;
        int first = Environment.GetEnvironmentVariable("SW_WARMUP") == "1" ? 0 : 1;

        List<string> misses = [];
        foreach (string scenario in scenarios)
        {
            for (int trial = first; trial <= trials; trial++)
            {
                SingleWalkSession.Visit visit = await RunCellAsync(demo, scenario, root, trial);
                string? miss = TargetMiss(scenario, visit);
                if (miss is not null)
                {
                    misses.Add($"{scenario} trial {trial}: {miss}");
                }
            }
        }

        await Assert.That(misses).IsEmpty().Because(string.Join("; ", misses));
    }

    private static async Task<SingleWalkSession.Visit> RunCellAsync(string demo, string scenario, string root, int trial)
    {
        string config = Path.Combine(root, "sw-" + Guid.NewGuid().ToString("N"));
        double fileMb = new FileInfo(demo).Length / 1024.0 / 1024.0;
        SingleWalkSession.WarmPageCache(demo);
        MemorySampler? sampler = null;
        long allocBase = 0;
        int[] gcBase = new int[3];
        try
        {
            SingleWalkSession.Visit visit = await SingleWalkSession.WithAppAsync(config,
                SingleWalkSession.Settings(Path.GetDirectoryName(demo)!, scenario is SingleWalkSession.Sweep or SingleWalkSession.Recheck),
                provider => SingleWalkSession.RunAsync(provider, scenario, demo, () =>
                {
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    allocBase = GC.GetTotalAllocatedBytes(true);
                    for (int g = 0; g < 3; g++)
                    {
                        gcBase[g] = GC.CollectionCount(g);
                    }

                    sampler = MemorySampler.Start();
                    return sampler;
                }));

            long allocated = GC.GetTotalAllocatedBytes(true) - allocBase;
            double peakWsMb = (sampler?.PeakRssBytes ?? 0) / 1024.0 / 1024.0;
            Console.WriteLine("@SW_VISIT " + JsonSerializer.Serialize(new
            {
                visit = scenario,
                trial,
                demo = Path.GetFileName(demo),
                fileMb = Math.Round(fileMb, 1),
                wallMs = Math.Round(visit.WallMs, 1),
                parseMs = Math.Round(visit.ParseMs, 1),
                // Process-wide: in a long-lived process a later cell starts from the earlier cells' floor.
                peakWsMb = Math.Round(peakWsMb, 1),
                // macOS counts resident mapped file pages in the working set.
                peakWsLessFileMb = Math.Round(peakWsMb - fileMb, 1),
                peakHeapMb = Math.Round((sampler?.PeakManagedHeapBytes ?? 0) / 1024.0 / 1024.0, 1),
                allocatedMb = Math.Round(allocated / 1024.0 / 1024.0, 1),
                gc0 = GC.CollectionCount(0) - gcBase[0],
                gc1 = GC.CollectionCount(1) - gcBase[1],
                gc2 = GC.CollectionCount(2) - gcBase[2],
                decodes = visit.Decodes,
                analysisDecodes = visit.AnalysisDecodes,
                shellDecodes = visit.ShellDecodes,
                shellSites = visit.ShellSites.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
                counterSource = "meter",
                meter = visit.Meter.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
                setupMeter = visit.SetupMeter.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
                hookDecodes = visit.HookDecodes,
                sites = visit.Sites.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value),
                setupSites = visit.SetupSites.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value),
                reads = visit.Reads,
                passMs = visit.Passes.Where(p => p.Outcome == PassOutcome.Ran)
                    .GroupBy(p => p.Pass).ToDictionary(g => g.Key, g => Math.Round(g.Sum(p => p.Ms), 1)),
                passes = visit.Passes.Select(p => $"{p.Pass}:{p.Outcome}").ToArray()
            }));
            return visit;
        }
        finally
        {
            try
            {
                Directory.Delete(config, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    // Open: the shell's rules run and nothing else. Sweep: one walk. Recheck: the library replay the visit keeps.
    private static string? TargetMiss(string scenario, SingleWalkSession.Visit visit)
    {
        string shellSites = string.Join(", ", visit.ShellSites.Select(kv => $"{kv.Key} x{kv.Value}"));
        if (scenario == SingleWalkSession.Open && visit.ShellDecodes != 0)
        {
            return $"shell decodes {visit.ShellDecodes}, expected 0 ({shellSites})";
        }

        return scenario is SingleWalkSession.Open or SingleWalkSession.Sweep or SingleWalkSession.Recheck && visit.AnalysisDecodes != 1
            ? $"analysis decodes {visit.AnalysisDecodes}, expected 1"
            : null;
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new SkipTestException(name + " not set");
}
