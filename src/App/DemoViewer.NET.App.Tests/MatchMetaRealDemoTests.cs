#region

using System.Globalization;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Game mode and rank off the forward read, on the five benchmark matchmaking demos (premier ×3,
///     competitive ×2): the mode, ten rank rows, the server's update on both reads, the last-full-packet
///     checkpoint of a rules read agreeing with the bare read's replay, and the premier prediction standing in
///     for an update the demo did not carry. Read in place from the <c>DEMO_PATH</c> folder, else
///     <c>demos/benchmarks</c>.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class MatchMetaRealDemoTests
{
    private static readonly MergedRulesBuild Rules = new(() =>
        YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null));

    [Test]
    [Arguments(0, "premier")]
    [Arguments(1, "premier")]
    [Arguments(2, "competitive")]
    [Arguments(3, "premier")]
    [Arguments(4, "competitive")]
    public async Task TheVisit_ReadsTheModeAndTenRankRows_OnBothReads(int which, string mode)
    {
        string path = BenchmarkDemo(which);
        ForwardDemoResult bare = Read(path, ForwardNeeds.FinalState);
        ForwardDemoResult ruled = Read(path, ForwardNeeds.FinalState | ForwardNeeds.Rules);
        (string? gameMode, string source, List<MatchRankRow> rows) = MatchMetaEvaluator.Capture(ruled.GameMode, ruled.Demo.Profile.SourceKind,
            ruled.Demo.ServerName, ruled.ControllerRanks, ruled.RankUpdates,
            () => MatchMetaEvaluator.WinningTeam(LibraryTeamTables.Read(ruled.Run, ruled.Demo.Players.Values, ruled.Overtime)));
        int? winner = MatchMetaEvaluator.WinningTeam(LibraryTeamTables.Read(ruled.Run, ruled.Demo.Players.Values, ruled.Overtime));
        List<MatchRankRow> predicted = MatchMetaEvaluator.RankRows(ruled.ControllerRanks, [], () => winner);
        Dictionary<string, MatchRankRow> fromUpdate = rows.ToDictionary(r => r.SteamId64);

        using (Assert.Multiple())
        {
            await Assert.That(bare.Run).IsNull();
            await Assert.That(ruled.FinalState).IsNull().Because("a rules read replays nothing in the tap; its ranks come off the checkpoint");
            await Assert.That(gameMode).IsEqualTo(mode);
            await Assert.That(bare.GameMode).IsEqualTo(mode);
            await Assert.That(source).IsEqualTo(nameof(DemoSourceKind.GotvMatchmaking));
            await Assert.That(ruled.RankUpdates.Count).IsEqualTo(10).Because("352 is sent once, with all ten humans");
            await Assert.That(bare.RankUpdates).IsEquivalentTo(ruled.RankUpdates);
            await Assert.That(ruled.ControllerRanks.OrderBy(r => r.SteamId64)).IsEquivalentTo(bare.ControllerRanks.OrderBy(r => r.SteamId64))
                .Because("the last-full-packet checkpoint agrees with the full replay");
            await Assert.That(ruled.ControllerRanks.Select(r => r.SteamId64).Order()).IsEquivalentTo(ruled.RankUpdates.Select(u => u.SteamId64).Order())
                .Because("every update joins a controller by account id + 76561197960265728");
            await Assert.That(rows.Count).IsEqualTo(10);
            await Assert.That(rows.All(r => r.RankSource == RankSource.ServerRankUpdate)).IsTrue();
            await Assert.That(rows.All(r => r.RankOld is null or > 0 && r.RankNew is null or > 0)).IsTrue().Because("rank 0 reads as unranked, never 0");
            await Assert.That(winner).IsNotNull();
            foreach (ControllerRank controller in ruled.ControllerRanks)
            {
                MatchRankRow update = fromUpdate[controller.SteamId64.ToString(CultureInfo.InvariantCulture)];
                await Assert.That(update.RankOld).IsEqualTo(controller.Ranking > 0 ? controller.Ranking : null);
                await Assert.That(update.RankType).IsEqualTo(controller.RankType);
                await Assert.That(update.IsUnranked).IsEqualTo(controller.Ranking == 0 && update.RankNew is null);
            }

            foreach (MatchRankRow row in predicted)
            {
                MatchRankRow update = fromUpdate[row.SteamId64];
                if (mode == "premier")
                {
                    await Assert.That(row.RankNew).IsEqualTo(update.RankNew).Because("where 352 is absent, the prediction for the result is the new rank");
                    await Assert.That(row.Wins).IsEqualTo(update.Wins);
                }
                else
                {
                    await Assert.That(row.RankNew).IsNull().Because("a competitive rank after the match is unknown without the update");
                }
            }
        }
    }

    // The queue's read: a capture over the file, the shipped rules when asked.
    private static ForwardDemoResult Read(string path, ForwardNeeds needs)
    {
        ServerRankCapture ranks = ServerRankCapture.OverFile(path);
        using DemoReader reader = DemoReader.OpenFile(path, ranks.Attach(ForwardDemoPass.ReaderOptions(CancellationToken.None)));
        return ForwardDemoPass.Run(reader, needs, (needs & ForwardNeeds.Rules) != 0 ? Rules.Docs : null, null, null, CollectorSet.Empty, ranks);
    }

    private static string BenchmarkDemo(int which)
    {
        string? resolved = DemoTestHelper.ResolveDemoPathOverride(
            Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar), null, out string? problem);
        if (problem is not null)
        {
            throw new SkipTestException(problem);
        }

        string? folder = resolved is null ? DemoTestHelper.RequireCorpusDir("benchmarks")
            : Path.GetDirectoryName(resolved)!;
        List<string> demos = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "match730_*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} Valve demos in the benchmark folder");
    }
}
