#region

using System.Diagnostics;
using System.Text.Json;
using DemoViewer.NET.Services.DemoProcessing;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The pack-on visit harness: one demo, one scenario, one process, driven by
///     <c>tools/single-walk-bench/run.sh</c>. Prints one <c>@SW_VISIT</c> JSON line with the visit's wall
///     time, peak working set and managed heap over the visit, the decode count by site, and per-pass time.
///     Skipped unless <c>SW_DEMO</c>, <c>SW_VISIT</c> and <c>SW_CONFIG_ROOT</c> are set.
/// </summary>
[NotInParallel]
public class SingleWalkVisitProbe
{
    [Test]
    [Category("Environmental")]
    public async Task Visit()
    {
        string demo = Require("SW_DEMO");
        string scenario = Require("SW_VISIT");
        string root = Require("SW_CONFIG_ROOT");
        if (scenario is not (SingleWalkSession.Sweep or SingleWalkSession.Open))
        {
            throw new ArgumentException($"SW_VISIT must be {SingleWalkSession.Sweep} or {SingleWalkSession.Open}, not {scenario}");
        }

        string config = Path.Combine(root, "sw-" + Guid.NewGuid().ToString("N"));
        double fileMb = new FileInfo(demo).Length / 1024.0 / 1024.0;
        SingleWalkSession.WarmPageCache(demo);
        MemorySampler? sampler = null;
        try
        {
            SingleWalkSession.Visit visit = await SingleWalkSession.WithAppAsync(config,
                SingleWalkSession.Settings(Path.GetDirectoryName(demo)!, scenario == SingleWalkSession.Sweep),
                provider => SingleWalkSession.RunAsync(provider, scenario, demo, () =>
                {
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    sampler = MemorySampler.Start(TimeSpan.FromMilliseconds(20));
                    return sampler;
                }));

            double peakWsMb = (sampler?.PeakRssBytes ?? 0) / 1024.0 / 1024.0;
            Console.WriteLine("@SW_VISIT " + JsonSerializer.Serialize(new
            {
                visit = scenario,
                demo = Path.GetFileName(demo),
                fileMb = Math.Round(fileMb, 1),
                wallMs = Math.Round(visit.WallMs, 1),
                parseMs = Math.Round(visit.ParseMs, 1),
                peakWsMb = Math.Round(peakWsMb, 1),
                // macOS counts resident mapped file pages in the working set.
                peakWsLessFileMb = Math.Round(peakWsMb - fileMb, 1),
                peakHeapMb = Math.Round((sampler?.PeakManagedHeapBytes ?? 0) / 1024.0 / 1024.0, 1),
                decodes = visit.Decodes,
                counterSource = "hooks",
                sites = visit.Sites.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value),
                setupSites = visit.SetupSites.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value),
                reads = visit.Reads,
                passMs = visit.Passes.Where(p => p.Outcome == PassOutcome.Ran)
                    .GroupBy(p => p.Pass).ToDictionary(g => g.Key, g => Math.Round(g.Sum(p => p.Ms), 1)),
                passes = visit.Passes.Select(p => $"{p.Pass}:{p.Outcome}").ToArray()
            }));
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

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new SkipTestException(name + " not set");

    /// <summary>
    ///     High-water marks of managed heap and working set while it runs, polled. A copy of
    ///     tools/AnalysisBench/MemorySampler.cs, which the test projects do not reference.
    /// </summary>
    private sealed class MemorySampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Process _process = Process.GetCurrentProcess();
        private long _peakHeap;
        private long _peakRss;

        private MemorySampler(TimeSpan interval) => _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                Sample();
                try
                {
                    await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Sample();
        });

        public long PeakManagedHeapBytes => Interlocked.Read(ref _peakHeap);

        public long PeakRssBytes => Interlocked.Read(ref _peakRss);

        public void Dispose()
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // best-effort telemetry
            }

            _process.Dispose();
        }

        public static MemorySampler Start(TimeSpan interval) => new(interval);

        private void Sample()
        {
            long heap = Math.Max(GC.GetGCMemoryInfo().HeapSizeBytes, GC.GetTotalMemory(false));
            Max(ref _peakHeap, heap);
            try
            {
                _process.Refresh();
                Max(ref _peakRss, _process.WorkingSet64);
            }
            catch (InvalidOperationException)
            {
                // metrics unavailable: keep what was seen
            }
        }

        private static void Max(ref long slot, long candidate)
        {
            long seen = Interlocked.Read(ref slot);
            while (candidate > seen)
            {
                long prior = Interlocked.CompareExchange(ref slot, candidate, seen);
                if (prior == seen)
                {
                    return;
                }

                seen = prior;
            }
        }
    }
}
