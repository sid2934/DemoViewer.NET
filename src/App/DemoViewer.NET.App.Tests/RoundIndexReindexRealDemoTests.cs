#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Exceptions;
using AppSites = DemoViewer.NET.Services.DemoProcessing.DecodeSites;
using StratBookSites = DemoViewer.NET.Extensions.StratBook.Services.DecodeSites;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The visit every existing library takes first after a round index fingerprint bump: only the round index
///     wants the demo. It reads forward, so the visit streams the demo once, and the files it rewrites are the
///     sweep's, byte for byte.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundIndexReindexRealDemoTests
{
    [Test]
    public async Task AReindexAfterTheSweep_StreamsTheDemoOnce_AndRewritesTheSameFiles()
    {
        string demo = BenchmarkDemo();
        string root = Path.Combine(Path.GetTempPath(), "dvreindex_" + Guid.NewGuid().ToString("N"));
        try
        {
            await SingleWalkSession.WithAppAsync(root, SingleWalkSession.Settings(Path.GetDirectoryName(demo)!, true), async provider =>
            {
                await SingleWalkSession.RunAsync(provider, SingleWalkSession.Sweep, demo);
                SortedDictionary<string, string> swept = RoundIndexFiles(root, demo);

                DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
                int forwardReads = 0;
                var forward = queue.ForwardRead ?? throw new InvalidOperationException("no forward read wired");
                queue.ForwardRead = (path, needs, collectors, progress, ct) =>
                {
                    Interlocked.Increment(ref forwardReads);
                    return forward(path, needs, collectors, progress, ct);
                };

                RoundIndexEvaluator evaluator = (RoundIndexEvaluator)((ExtensionPassHost)provider.GetRequiredService<PackContributionSet>()
                    .Packs.SelectMany(p => p.Passes).Single(p => p.Id == RoundIndexEvaluator.EvaluatorId).Factory()).Inner;
                Dictionary<int, int> sites = [];
                Action<int> count = site =>
                {
                    lock (sites)
                    {
                        sites[site] = sites.GetValueOrDefault(site) + 1;
                    }
                };

                int readsBefore = queue.ParseCount(demo);
                AppSites.Entered = count;
                StratBookSites.Entered = count;
                try
                {
                    evaluator.RebuildAll();
                    await SingleWalkSession.SettleAsync(queue);
                }
                finally
                {
                    AppSites.Entered = null;
                    StratBookSites.Entered = null;
                }

                SortedDictionary<string, string> rebuilt = RoundIndexFiles(root, demo);
                using (Assert.Multiple())
                {
                    await Assert.That(queue.ParseCount(demo) - readsBefore).IsEqualTo(1);
                    await Assert.That(forwardReads).IsEqualTo(1).Because("only the forward round index wanted the demo");
                    await Assert.That(sites).IsEmpty().Because("no walk and no bare run over a held parse");
                    await Assert.That(swept.Count).IsGreaterThan(0);
                    await Assert.That(rebuilt).IsEquivalentTo(swept);
                }

                return 0;
            });
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static SortedDictionary<string, string> RoundIndexFiles(string root, string demo) =>
        new(SingleWalkOracleTests.Canonical(root, demo).Where(kv => kv.Key.Contains("/round-index/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);

    private static string BenchmarkDemo()
    {
        string? folder = DemoTestHelper.CorpusDir("benchmarks");
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.dem").Order(StringComparer.Ordinal).FirstOrDefault() is { } demo
            ? demo
            : throw new SkipTestException("no demos under demos/benchmarks");
    }
}
