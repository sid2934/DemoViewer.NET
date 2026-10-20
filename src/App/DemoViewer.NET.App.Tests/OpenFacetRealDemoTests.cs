#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An interactive open writes the demo's <c>player_match</c> facet with background processing off: the open's
///     visit runs at the open's level on its own parse, so the switch that holds the backlog never holds it, and a
///     later cached render of the demo has a scoreboard.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class OpenFacetRealDemoTests
{
    [Test]
    public async Task AnOpenWithBackgroundProcessingOff_WritesThePlayerMatchFacet()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        StampedFacts stamped = new(PlayerMatchParityRealDemoTests.AppRules());
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
        });
        FactsEvaluator facts = new(cache, stamped);
        AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache));
        int fileParses = 0;
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = new(gate, a => a(),
            parseFile: _ =>
            {
                Interlocked.Increment(ref fileParses);
                return parsed;
            },
            parseBytes: _ => parsed)
        {
            BackgroundEnabled = false
        };
        using DemoScheduler scheduler = new([facts], queue, () => [path]);

        await Assert.That(library.TryGet(path, PlayerMatchFacet.Key)).IsNull();

        using (IDemoOpenTicket open = queue.BeginOpen(path, Path.GetFileName(path)))
        {
            ParsedDemo opened = await open.ParseAsync(new byte[] { 1 });
            await open.RunPassesAsync(opened, () => scheduler.PlanOpenDemo(path));
            open.Complete();
        }

        FactTable? facet = library.TryGet(path, PlayerMatchFacet.Key);
        using (Assert.Multiple())
        {
            await Assert.That(facet).IsNotNull().Because("the open's visit is not background work");
            await Assert.That(DemoCacheAnalysisProjector.ProjectScoreboard(facet!).Count).IsGreaterThan(0);
            await Assert.That(fileParses).IsEqualTo(0).Because("the facet came from the open's parse");
        }
    }
}
