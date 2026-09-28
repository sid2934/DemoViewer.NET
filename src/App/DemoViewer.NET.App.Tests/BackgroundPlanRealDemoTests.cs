#region

using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The background parse drops user commands when no evaluator on the demo reads them. Every other
///     consumer must produce the same output from that parse as from the full one: Round Facts rows, the
///     Round Index sidecars, Suggested Tags proposals, bare highlights and the library's tier-2 fields.
///     Read in place from the <c>DEMO_PATH</c> folder (its three smallest demos), one parse at a time.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class BackgroundPlanRealDemoTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, IncludeFields = true };

    internal static string SmallestDemo(int which)
    {
        string? env = Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar);
        string? folder = Directory.Exists(env) ? env : File.Exists(env) ? Path.GetDirectoryName(env) : null;
        if (folder is null)
        {
            throw new SkipTestException($"{DemoTestHelper.DemoPathEnvVar} names no demo folder");
        }

        List<FileInfo> demos = [.. new DirectoryInfo(folder).EnumerateFiles("*.dem").OrderBy(f => f.Length)];
        return which < demos.Count
            ? demos[which].FullName
            : throw new SkipTestException($"fewer than {which + 1} demos in {folder}");
    }

    internal static ParsedDemo ParseMapped(string path, DecodePlan plan) =>
        MemoryMappedDemoSource.ParseFile(path, new ParseOptions { Plan = plan });

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task WithoutUserCommands_EveryOtherConsumer_WritesTheSameOutput(int which)
    {
        string path = SmallestDemo(which);

        Dictionary<string, string> full = Consume(path, DecodePlan.Everything);
        Dictionary<string, string> narrow = Consume(path, DemoProcessingQueue.WithoutUserCommands);

        using (Assert.Multiple())
        {
            await Assert.That(narrow.Keys).IsEquivalentTo(full.Keys);
            foreach ((string key, string value) in full)
            {
                await Assert.That(narrow[key]).IsEqualTo(value).Because($"{key} on {Path.GetFileName(path)}");
            }

            // An empty output on both sides proves nothing.
            foreach ((string key, string value) in full)
            {
                await Assert.That(value.Length).IsGreaterThan(40).Because($"{key}: {value}");
            }
        }

        Console.WriteLine($"{Path.GetFileName(path)}: " + string.Join(", ", full.Select(kv => $"{kv.Key} {kv.Value.Length}")));
    }

    // One parse per call; the parse is dropped before the next plan runs.
    private static Dictionary<string, string> Consume(string path, DecodePlan plan)
    {
        ParsedDemo parsed = ParseMapped(path, plan);
        Dictionary<string, string> outputs = [];

        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Sha256 = new string('c', 64),
            Map = parsed.MapName,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
        });

        new RoundFactsEvaluator(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity())
            .OnParsedOpportunistically(path, parsed);
        outputs["round facts"] = JsonSerializer.Serialize(cache.TryLoadRecord(path)?.RoundFacts, Json);

        RoundIndexStore index = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        new RoundIndexEvaluator(cache, index, sources, () => true).Evaluate(path, parsed);
        outputs["round index"] = index.TryReadText(path) ?? "null";
        outputs["round positions"] = JsonSerializer.Serialize(index.TryReadPositions(path), Json);

        using ProposalStore proposals = new(null, cache);
        SuggestedTagsService tags = new(cache, proposals, null, new SiteRegionStore(null),
            () => DetectorProfile.Default, () => true, () => true, index, sources);
        tags.Evaluate(path, parsed);
        outputs["suggested tags"] = JsonSerializer.Serialize(
            tags.Load(path).Entries.Select(e => e.Proposal).ToList(), Json);

        RuleConfigLoadResult rules = YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null);
        BuildResult build = DemoAnalysis.Build(parsed, RoundFactsFingerprint.WithoutRoundFacts(rules.Rulesets));
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = false });
        outputs["highlights"] = JsonSerializer.Serialize(run.Highlights, Json);

        // A forced rescan runs with snapshots and writes the scoreboard from them.
        AnalysisRun forced = DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = true });
        outputs["forced highlights"] = JsonSerializer.Serialize(forced.Highlights, Json);
        MetricTable table = new PlayerGameStatsProjector { MatchId = Path.GetFileName(path) }
            .Project(forced.Snapshots!, parsed).Single();
        outputs["forced scoreboard"] = JsonSerializer.Serialize(new
        {
            rows = DemoCacheAnalysisProjector.ProjectScoreboard(table),
            sides = DemoCacheAnalysisProjector.ComputeSideWins(table)
        }, Json);
        forced = null!;

        List<string> players = [.. parsed.Players.Values
            .Where(p => !p.IsBot && !p.IsHltv && !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name).Distinct()];
        (int? ct, int? t, string? ctClan, string? tClan, HashSet<int> coaches) = DemoLibraryService.ExtractFinalState(parsed);
        outputs["library"] = JsonSerializer.Serialize(new
        {
            players,
            parsed.Duration.TotalSeconds,
            parsed.MapName,
            ct,
            t,
            ctClan,
            tClan,
            coaches = coaches.Order().ToList()
        }, Json);
        return outputs;
    }
}
