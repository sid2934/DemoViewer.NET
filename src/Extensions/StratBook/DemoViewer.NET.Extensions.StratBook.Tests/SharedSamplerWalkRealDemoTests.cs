#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using TUnit.Assertions.Enums;
using DemoViewer.NET.Extensions.StratBook.Services;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The shared projectile walk against the walks it replaces, sample for sample, on the benchmark demos:
///     the thrower fix-up's stride-8 Removed set and the grenade walk's stride-1 stream.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class SharedSamplerWalkRealDemoTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheFilteredStreams_EqualTheDirectSamplerStreams(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        List<ProjectileSample> direct8 = [.. ProjectileSampler.Walk(parsed, frameStride: 8).Where(s => s.Removed)];
        List<ProjectileSample> direct1 = [.. ProjectileSampler.Walk(parsed, 1)];

        SharedSamplerWalk suggestedFirst = new((_, _) => true, _ => true);
        List<ProjectileSample> removed = suggestedFirst.Removed(path, parsed);
        List<ProjectileSample> all = [.. suggestedFirst.All(path, parsed)];

        SharedSamplerWalk grenadesFirst = new((_, _) => true, _ => true);
        List<ProjectileSample> teed = [.. grenadesFirst.All(path, parsed)];
        List<ProjectileSample> teedRemoved = grenadesFirst.Removed(path, parsed);

        Console.WriteLine($"{Path.GetFileName(path)}: {direct1.Count} stride-1 samples, {direct8.Count} removed");
        using (Assert.Multiple())
        {
            await Assert.That(direct8.Count).IsGreaterThan(0);
            await Assert.That(direct1.Where(s => s.Removed)).IsEquivalentTo(direct8, CollectionOrdering.Matching);
            await Assert.That(removed).IsEquivalentTo(direct8, CollectionOrdering.Matching);
            await Assert.That(all).IsEquivalentTo(direct1, CollectionOrdering.Matching);
            await Assert.That(teed).IsEquivalentTo(direct1, CollectionOrdering.Matching);
            await Assert.That(teedRemoved).IsEquivalentTo(direct8, CollectionOrdering.Matching);
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
