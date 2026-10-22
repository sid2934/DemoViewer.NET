#region

using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The round index's roster collector on a streamed read names the same slots as the held parse's connect and
///     disconnect events, so the positions file reads the same on either path.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundIndexRosterRealDemoTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheCollectorsRoster_EqualsTheParsesEvents(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        IReadOnlyDictionary<int, RosterEntry> held = RoundIndexRoster.Of(parsed.AllGameEvents);

        CollectorSet set = CollectorSet.Of([CollectorKit.Register(RoundIndexRosterCollector.Contribution)]);
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null));
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, null, null, set);
        IReadOnlyDictionary<int, RosterEntry>? read =
            forward.Collected.Get<RoundIndexRosterCollector.Collected>(RoundIndexRosterCollector.CollectorId)?.Slots;

        using (Assert.Multiple())
        {
            await Assert.That(held.Count).IsGreaterThan(0).Because("every benchmark demo has disconnects");
            await Assert.That(read).IsNotNull();
            await Assert.That(read!.OrderBy(kv => kv.Key)).IsEquivalentTo(held.OrderBy(kv => kv.Key));
        }
    }

    private static string BenchmarkDemo(int which)
    {
        string? folder = DemoTestHelper.CorpusDir("benchmarks");
        List<string> demos = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under demos/benchmarks");
    }
}
