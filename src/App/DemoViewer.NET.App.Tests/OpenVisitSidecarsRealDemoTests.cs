#region

using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An open whose round facts and facts are stale hands the shell's run to its visit: snapshots on, the
///     map's bake loaded, every core ruleset beside the stamped ones. What the visit writes from it must be
///     what a background retained visit and a forward visit write, and the scoreboard and Stats tables the
///     open shows must be those of the core rulesets alone.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class OpenVisitSidecarsRealDemoTests
{
    private static readonly JsonSerializerOptions Fields = new() { IncludeFields = true };

    private static string BenchmarkDemo(int which)
    {
        string folder = Path.Combine(DemoTestHelper.FindRepoRoot() ?? "", "demos", "benchmarks");
        List<string> demos = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under demos/benchmarks");
    }

    // round_facts and the per-player facts fixture, both stamped and both stale.
    private static MergedRulesBuild Stale(string fixture)
    {
        MergedRulesBuild build = new(() => AnalysisFactsTests.WithFixture(fixture),
            () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId), StampedRuleset.Core(AnalysisFactsTests.FactsRuleset)]);
        build.OutputsFor = _ => build.StampedOutputs();
        return build;
    }

    private static string Tables(AnalysisRun run, IEnumerable<string> names)
    {
        HashSet<string> wanted = new(names, StringComparer.Ordinal);
        return JsonSerializer.Serialize(run.ProjectConfiguredOutputs().Where(t => wanted.Contains(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal), Fields);
    }

    private static string Scoreboard(AnalysisRun run, ParsedDemo parsed, string matchId)
    {
        MetricTable table = new PlayerGameStatsProjector { MatchId = matchId }.Project(run.Snapshots!, parsed).Single();
        return JsonSerializer.Serialize((DemoCacheAnalysisProjector.ProjectScoreboard(table), DemoCacheAnalysisProjector.ComputeSideWins(table)), Fields);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheOpenVisit_WritesWhatTheBackgroundVisitsWrite(int which)
    {
        string path = BenchmarkDemo(which);
        string fixture = AnalysisFactsTests.WriteFixture((AnalysisFactsTests.FactsRuleset, AnalysisFactsTests.FactsYaml));
        try
        {
            string[] stamped = [RoundFactsFingerprint.RulesetId, AnalysisFactsTests.FactsTable];
            ParsedDemo parsed = BackgroundPlanRealDemoTests.ParseMapped(path, DemoProcessingQueue.WithoutUserCommands);
            string bake = CollisionSoup.Find(parsed.MapName) ?? throw new InvalidOperationException($"no collision bake for {parsed.MapName}");
            AnalysisOptions withBake = new() { VisibilityEngine = CollisionSoup.Load(bake) };
            string matchId = Path.GetFileName(path);

            // The open: the shell's plan, build and snapshot run, seeded, then read by the visit's consumers.
            MergedRulesBuild open = Stale(fixture);
            OpenRunPlan plan = open.PlanOpen(AnalysisFactsTests.WithFixture(fixture).Rulesets, path);
            AnalysisRun shellRun = DemoAnalysis.Evaluate(parsed, plan.Cuts(DemoAnalysis.Build(parsed, plan.Docs, withBake)), withBake);
            bool seeded = open.Seed(parsed, path, plan, shellRun);
            AnalysisRun visitRun = open.BareRun(parsed, path);
            string openRows = JsonSerializer.Serialize(new EngineRoundFactsRowSource(new RulesRoundFactsRulesetIdentity(open)).Rows(parsed, path), Fields);

            // A background retained visit: its own bare run on the same parse.
            MergedRulesBuild retained = Stale(fixture);
            AnalysisRun bareRun = retained.BareRun(parsed, path);
            string retainedRows = JsonSerializer.Serialize(new EngineRoundFactsRowSource(new RulesRoundFactsRulesetIdentity(retained)).Rows(parsed, path), Fields);

            // A forward visit.
            MergedRulesBuild forwardRules = Stale(fixture);
            ForwardDemoResult forward;
            using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
            {
                forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, forwardRules.Docs, outputs: forwardRules.OutputsFor!(path));
            }

            string forwardRows = JsonSerializer.Serialize(new EngineRoundFactsRowSource(new RulesRoundFactsRulesetIdentity(forwardRules)).Rows(forward), Fields);

            // What the open showed before this change: the core rulesets alone, with the bake.
            AnalysisRun coreRun = DemoAnalysis.Evaluate(parsed, DemoAnalysis.Build(parsed, open.CoreDocs, withBake), withBake);

            using (Assert.Multiple())
            {
                await Assert.That(seeded).IsTrue();
                await Assert.That(visitRun).IsSameReferenceAs(shellRun).Because("the visit reads the open's run");
                await Assert.That(retainedRows.Length).IsGreaterThan(1000).Because("the comparison needs rows");
                await Assert.That(Tables(visitRun, [AnalysisFactsTests.FactsTable]).Length).IsGreaterThan(200).Because("and facts");
                await Assert.That(openRows).IsEqualTo(retainedRows).Because("round facts rows, open vs retained");
                await Assert.That(forwardRows).IsEqualTo(retainedRows).Because("round facts rows, forward vs retained");
                await Assert.That(Tables(visitRun, stamped)).IsEqualTo(Tables(bareRun, stamped)).Because("stamped tables, open vs retained");
                await Assert.That(Tables(forward.Run!, stamped)).IsEqualTo(Tables(bareRun, stamped)).Because("stamped tables, forward vs retained");
                await Assert.That(Scoreboard(shellRun, parsed, matchId)).IsEqualTo(Scoreboard(coreRun, parsed, matchId))
                    .Because("the scoreboard the open writes");
                await Assert.That(JsonSerializer.Serialize(plan.Shown(shellRun).ProjectConfiguredOutputs(parsed, matchId), Fields))
                    .IsEqualTo(JsonSerializer.Serialize(coreRun.ProjectConfiguredOutputs(parsed, matchId), Fields))
                    .Because("the Stats tab's configured tables");
                await Assert.That(JsonSerializer.Serialize(plan.Shown(shellRun).Highlights, Fields))
                    .IsEqualTo(JsonSerializer.Serialize(coreRun.Highlights, Fields)).Because("the open's highlight harvest");
            }
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }
}
