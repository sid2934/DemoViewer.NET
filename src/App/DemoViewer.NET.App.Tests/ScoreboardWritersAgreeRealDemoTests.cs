#region

using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The two tier-3 scoreboard writers on a benchmark demo whose map has a bake: the forced scan
///     (<see cref="MergedRulesBuild.FullRun" />, no visibility engine) and the open (the same core rulesets
///     with the bake loaded). The persisted rows must be the same, so neither the write order nor whether
///     the bake was available can change what a demo's cached scoreboard says.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ScoreboardWritersAgreeRealDemoTests
{
    // de_nuke. Every benchmark map carries a bake, so the bake-free forced scan stands in for a map without one.
    private const string Demo = "003816248937665266002_0544286934.dem";

    private const string FirstContacts = "aim_rating.first_contacts";

    private static (string Rows, int? Ct, int? T, double FirstContacts) Persisted(AnalysisRun run, ParsedDemo parsed)
    {
        EvaluationResult snapshots = run.Snapshots ?? throw new InvalidOperationException("the run captured no snapshots");
        MetricTable table = new PlayerGameStatsProjector { MatchId = Demo }.Project(snapshots, parsed).Single();
        (int? ct, int? t) = DemoCacheAnalysisProjector.ComputeSideWins(table);
        double contacts = snapshots.MaterializedPlayers
            .Sum(p => p.NodesByRuleId is { } byId && byId.TryGetValue(FirstContacts, out StateNode? node) ? node.GetNumericValue() ?? 0 : 0);
        return (JsonSerializer.Serialize(DemoCacheAnalysisProjector.ProjectScoreboard(table)), ct, t, contacts);
    }

    [Test]
    public async Task TheForcedScanScoreboard_EqualsTheOpenScoreboard_OnAMapWithABake()
    {
        string path = DemoTestHelper.RequireDemo(Demo);
        ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        string bake = CollisionSoup.Find(parsed.MapName)
                      ?? throw new InvalidOperationException($"no collision bake for {parsed.MapName}");
        string shipped = RuleSetLocator.ResolveShippedRulesDirectory();
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(shipped, null),
            () => [StampedRuleset.Core(RoundFactsFingerprint.RulesetId)]);

        (string Rows, int? Ct, int? T, double FirstContacts) forced = Persisted(rules.FullRun(parsed), parsed);

        AnalysisOptions withBake = new() { VisibilityEngine = CollisionSoup.Load(bake) };
        BuildResult build = DemoAnalysis.Build(parsed, rules.CoreDocs, withBake);
        (string Rows, int? Ct, int? T, double FirstContacts) open = Persisted(DemoAnalysis.Evaluate(parsed, build, withBake), parsed);

        using (Assert.Multiple())
        {
            await Assert.That(open.FirstContacts).IsGreaterThan(0).Because("the open arm must actually see enemy_spotted");
            await Assert.That(forced.FirstContacts).IsEqualTo(0).Because("the forced scan runs without the bake");
            await Assert.That(forced.Rows).IsEqualTo(open.Rows);
            await Assert.That(forced.Ct).IsEqualTo(open.Ct);
            await Assert.That(forced.T).IsEqualTo(open.T);
        }
    }
}
