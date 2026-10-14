#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Zones;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Detonation placement on the index path against the walk path it replaces on the open visit, over the
///     benchmark demos. The walk path is the map's zones plus the walk's cloud with no library index; the
///     index path is the zones plus the centroids of the demo's own index, then the cloud rebuilt from its
///     positions file.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class SuggestedTagsIndexPlacementRealDemoTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheIndexPath_PlacesEveryDetonationTheWalkPathPlaces(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheStore store = new(null);
        RoundFactsEvaluator facts = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.Evaluate(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("the evaluator wrote no rows");

        OccupancyBuild walk = RoundOccupancyBuilder.FromWalk(parsed, rows);
        RoundIndexBuild built = RoundIndexBuilder.BuildWithPositions(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance);
        RoundIndexDocument index = built.Index;
        List<PlaceSummary> places =
        [
            .. index.Places.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new PlaceSummary(p.Key, p.Value.Count,
                [.. p.Value.Buckets.Select(b => new PlaceZBucket(b.ZBucket, b.Count, b.SumX / b.Count, b.SumY / b.Count))]))
        ];
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet(parsed.MapName);

        DetonationPlaceResolver walkPath = new(zones, null, walk.Cloud);
        DetonationPlaceResolver centroidsOnly = new(zones, places, null);
        DetonationPlaceResolver indexPath = new(zones, places, DetonationCloud.FromPositions(built.Positions));
        List<PlacedEvent> events = DetonationEvents.From(parsed);

        int walkPlaced = 0, indexPlaced = 0, lost = 0, cloudOnly = 0, renamed = 0;
        foreach (PlacedEvent e in events)
        {
            string? byWalk = walkPath.Resolve(e.Position);
            string? byIndex = indexPath.Resolve(e.Position);
            walkPlaced += byWalk is null ? 0 : 1;
            indexPlaced += byIndex is null ? 0 : 1;
            lost += byWalk is not null && byIndex is null ? 1 : 0;
            renamed += byWalk is not null && byIndex is not null && byWalk != byIndex ? 1 : 0;
            cloudOnly += byIndex is not null && centroidsOnly.Resolve(e.Position) is null ? 1 : 0;
        }

        Console.WriteLine($"{Path.GetFileName(path)} {parsed.MapName} zones={(zones is null ? "no" : "yes")}: {events.Count} detonations, " +
                          $"walk placed {walkPlaced}, index placed {indexPlaced}, lost {lost}, cloud-only {cloudOnly}, renamed {renamed}");
        using (Assert.Multiple())
        {
            await Assert.That(events.Count).IsGreaterThan(0);
            await Assert.That(lost).IsEqualTo(0).Because("a detonation the walk's cloud placed keeps a place on the index path");
        }
    }

    private static string BenchmarkDemo(int which)
    {
        string? root = DemoTestHelper.FindRepoRoot();
        string folder = root is null ? "" : Path.Combine(root, "demos", "benchmarks");
        List<string> demos = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under demos/benchmarks");
    }
}
