#region

using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.TestSupport;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="ThrowerCollector" /> on the rules run against the stride-8 sampler walk it replaced, on the
///     benchmark demos: the same removed molotov and decoy rows, and so the same detonations named.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ThrowerCollectorRealDemoTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheCollectorRows_EqualTheSamplerWalkRows(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        string[] classes = [GrenadeProjectileClasses.Molotov, GrenadeProjectileClasses.Decoy];
        List<RemovedProjectile> walked =
        [
            .. ProjectileSampler.Walk(parsed, frameStride: 8).Where(s => s.Removed && classes.Contains(s.ClassName))
                .Select(RemovedProjectile.From)
        ];

        CollectorSet set = CollectorSet.Of([new RegisteredCollector(ThrowerCollector.Contribution, null)]);
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null))
        {
            CollectorsFor = _ => set
        };
        List<RemovedProjectile> collected = rules.Collected(parsed, path).Get<List<RemovedProjectile>>(ThrowerCollector.CollectorId)!;
        rules.Forget(parsed);

        List<PlacedEvent> fromWalk = DetonationEvents.From(parsed, walked);
        List<PlacedEvent> fromCollector = DetonationEvents.From(parsed, collected);
        Console.WriteLine($"{Path.GetFileName(path)}: {walked.Count} walked, {collected.Count} collected, "
                          + $"{fromCollector.Count(e => e.Kind is DetonationEvents.Inferno or DetonationEvents.Decoy && e.ThrowerSlot >= 0)} named");
        using (Assert.Multiple())
        {
            await Assert.That(walked.Count).IsGreaterThan(0);
            await Assert.That(Ordered(collected)).IsEquivalentTo(Ordered(walked), CollectionOrdering.Matching);
            await Assert.That(fromCollector).IsEquivalentTo(fromWalk, CollectionOrdering.Matching);
        }
    }

    // Rows within one frame need not come out in the sampler's order.
    private static List<RemovedProjectile> Ordered(IEnumerable<RemovedProjectile> rows) =>
    [
        .. rows.OrderBy(r => r.Tick).ThenBy(r => r.ClassName, StringComparer.Ordinal).ThenBy(r => r.ThrowerSlot)
            .ThenBy(r => r.Position?.X).ThenBy(r => r.Position?.Y).ThenBy(r => r.Position?.Z)
    ];

    private static string BenchmarkDemo(int which)
    {
        // The benchmarks are the oracle set; a folder DEMO_PATH stands in only where they are absent.
        return DemoTestHelper.TrySetDemo("benchmarks", which, out string? problem)
               ?? DemoTestHelper.DemoPathFolderDemo(which)
               ?? throw new SkipTestException($"no benchmark demos: {problem}");
    }
}
