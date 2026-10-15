#region

using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The step 9 parity gate: the round index folded from <see cref="RoundIndexCollector" />'s samples, read on a
///     rules run through the host's collector adapter, is the index folded from <see cref="PositionSampler.Walk" />,
///     byte for byte, on the benchmark demos. On a miss it prints the first differing bytes and round.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundIndexObserverParityTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheObserverIndex_EqualsTheSamplerIndex(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        CollectorSet set = CollectorSet.Of([new RegisteredCollector(RoundIndexCollector.Contribution, null)]);
        (AnalysisOptions options, CollectorObserver? observer) = set.Declare(new AnalysisOptions { CaptureSnapshots = false });
        DemoAnalysis.Run(parsed, [], options);
        RoundIndexSamples collected = observer!.Results.Get<RoundIndexSamples>(RoundIndexCollector.CollectorId)
                                      ?? throw new InvalidOperationException("the collector put nothing");

        DemoCacheStore store = new(null);
        new RoundFactsEvaluator(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity()).Evaluate(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no round facts rows");

        RoundIndexDemoValues values = RoundIndexDemoValues.For(parsed);
        RoundIndexBuild walk = RoundIndexBuilder.BuildWithPositions(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance,
            PositionSampler.Walk(parsed, RoundIndexOptions.Default.FrameStride));
        RoundIndexBuild observed = RoundIndexBuilder.BuildWithPositions(values with { LastFrameTick = collected.LastFrameTick }, rows,
            RoundIndexOptions.Default, PawnPlaceSource.Instance, collected.Samples);

        string index = walk.Index.Serialize(), observedIndex = observed.Index.Serialize();
        string positions = Encoding.UTF8.GetString(walk.Positions.SerializeUtf8());
        string observedPositions = Encoding.UTF8.GetString(observed.Positions.SerializeUtf8());
        PrintFirstDiff("index", index, observedIndex);
        PrintFirstDiff("positions", positions, observedPositions);
        PrintFirstRound(walk.Index, observed.Index);

        using (Assert.Multiple())
        {
            await Assert.That(collected.LastFrameTick).IsEqualTo(values.LastFrameTick);
            await Assert.That(observedIndex).IsEqualTo(index);
            await Assert.That(observedPositions).IsEqualTo(positions);
            await Assert.That(observed.Positions.Players).IsNotEmpty();
            await Assert.That(observed.Positions.Players.Where(p => p.SteamId64 == 0 && p.Name.Length == 0)).IsEmpty()
                .Because("every slot a tuple names is on the demo's roster");
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    public async Task AForwardRead_FoldsTheRetainedReadsIndex(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        CollectorSet set = CollectorSet.Of([new RegisteredCollector(RoundIndexCollector.Contribution, null)]);
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null))
        {
            CollectorsFor = _ => set
        };

        RoundIndexSamples retained = rules.Collected(parsed, path).Get<RoundIndexSamples>(RoundIndexCollector.CollectorId)!;
        rules.Forget(parsed);
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, null, null, set);
        RoundIndexSamples read = forward.Collected.Get<RoundIndexSamples>(RoundIndexCollector.CollectorId)!;

        DemoCacheStore store = new(null);
        new RoundFactsEvaluator(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity()).Evaluate(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no round facts rows");
        RoundIndexBuild fromRetained = RoundIndexBuilder.BuildWithPositions(RoundIndexDemoValues.For(parsed), rows,
            RoundIndexOptions.Default, PawnPlaceSource.Instance, retained.Samples);
        RoundIndexBuild fromForward = RoundIndexBuilder.BuildWithPositions(RoundIndexDemoValues.For(new ForwardContext(path, forward), read.LastFrameTick),
            rows, RoundIndexOptions.Default, PawnPlaceSource.Instance, read.Samples);

        using (Assert.Multiple())
        {
            await Assert.That(read.Samples.Count).IsEqualTo(retained.Samples.Count);
            await Assert.That(fromForward.Index.Serialize()).IsEqualTo(fromRetained.Index.Serialize());
            await Assert.That(fromForward.Positions.Serialize()).IsEqualTo(fromRetained.Positions.Serialize());
        }
    }

    // What a forward pass's context answers, from the forward read alone.
    private sealed class ForwardContext(string path, ForwardDemoResult read) : IPassContext
    {
        public string DemoPath => path;
        public ParsedDemo? Parsed => null;
        public DemoDescriptor Demo => read.Demo;
        public int FrameCount => read.FrameCount;
        public int FirstServerTick => read.FirstServerTick;
        public int LastServerTick => read.LastServerTick;
        public IReadOnlyList<PassRound> Rounds => [];
        public CancellationToken CancellationToken => CancellationToken.None;
        public T? Collected<T>(string collectorId) where T : class => throw new NotSupportedException();
    }

    private static void PrintFirstDiff(string label, string expected, string actual)
    {
        int at = 0;
        while (at < Math.Min(expected.Length, actual.Length) && expected[at] == actual[at])
        {
            at++;
        }

        if (at < Math.Max(expected.Length, actual.Length))
        {
            int from = Math.Max(0, at - 150);
            Console.WriteLine($"{label} first diff at {at} of {expected.Length}/{actual.Length}:\n  walk     " +
                              $"{expected.Substring(from, Math.Min(300, expected.Length - from))}\n  observer " +
                              $"{actual.Substring(from, Math.Min(300, actual.Length - from))}");
        }
    }

    private static void PrintFirstRound(RoundIndexDocument walk, RoundIndexDocument observed)
    {
        for (int r = 0; r < Math.Min(walk.Rounds.Count, observed.Rounds.Count); r++)
        {
            string a = string.Join(";", walk.Rounds[r].Runs), b = string.Join(";", observed.Rounds[r].Runs);
            if (a != b)
            {
                Console.WriteLine($"round {walk.Rounds[r].Number} runs differ:\n  walk     {a}\n  observer {b}");
                return;
            }
        }
    }

    // The benchmarks are the oracle set; a folder DEMO_PATH stands in only where they are absent.
    private static string BenchmarkDemo(int which)
    {
        string? root = DemoTestHelper.FindRepoRoot();
        string folder = root is not null && Directory.Exists(Path.Combine(root, "demos", "benchmarks"))
            ? Path.Combine(root, "demos", "benchmarks")
            : Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar) is { } env && Directory.Exists(env)
                ? env
                : throw new SkipTestException("no benchmark demos");
        string[] demos = [.. Directory.EnumerateFiles(folder, "*.dem").Order(StringComparer.Ordinal)];
        return which < demos.Length ? demos[which] : throw new SkipTestException($"fewer than {which + 1} benchmark demos");
    }
}
