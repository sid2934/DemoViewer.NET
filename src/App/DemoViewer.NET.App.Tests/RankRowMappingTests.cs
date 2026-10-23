#region

using System.Text.Json;
using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.MatchMeta;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     How a demo's rank rows are made: the server's 352 update joined to SteamID64s, the controller's fields
///     where it sent none, premier predictions by the result, and rank 0 as unranked, never 0. No demo is read.
/// </summary>
public class RankRowMappingTests
{
    private const ulong A = 76561198000000001, B = 76561198000000002;

    private static ControllerRank Premier(ulong id, int team, int ranking = 14213, int wins = 300) =>
        new(id, team, ranking, wins, 11, ranking == 0 ? 0 : ranking + 189, ranking == 0 ? 0 : ranking - 150, ranking == 0 ? 0 : ranking + 10);

    [Test]
    public async Task TheUpdate_JoinsEachAccountToItsSteamId64()
    {
        CCSUsrMsg_ServerRankUpdate message = new()
        {
            RankUpdate =
            {
                new CCSUsrMsg_ServerRankUpdate.Types.RankUpdate { AccountId = 39734273, RankOld = 14213, RankNew = 14402, RankChange = 189, NumWins = 312, RankTypeId = 11 }
            }
        };

        ServerRankUpdate row = ServerRankCapture.Rows(message).Single();
        using (Assert.Multiple())
        {
            await Assert.That(row.SteamId64).IsEqualTo(39734273UL + 76561197960265728UL);
            await Assert.That(row).IsEqualTo(new ServerRankUpdate(76561198000000001, 14213, 14402, 189, 312, 11));
        }
    }

    [Test]
    public async Task TheUpdate_WinsOverTheController_AndRankZeroIsUnranked()
    {
        List<MatchRankRow> rows = MatchMetaEvaluator.RankRows(
            [Premier(A, 3), Premier(B, 2, ranking: 0, wins: 3)],
            [new ServerRankUpdate(A, 14213, 14402, 189, 301, 11), new ServerRankUpdate(B, 0, 0, 0, 3, 11)],
            () => throw new InvalidOperationException("every controller has an update; the result is never asked"));

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(r => r.SteamId64)).IsEquivalentTo(["76561198000000001", "76561198000000002"]);
            await Assert.That(rows[0].RankOld).IsEqualTo(14213);
            await Assert.That(rows[0].RankNew).IsEqualTo(14402);
            await Assert.That(rows[0].RankChange).IsEqualTo(189);
            await Assert.That(rows[0].Wins).IsEqualTo(301);
            await Assert.That(rows[0].RankSource).IsEqualTo(RankSource.ServerRankUpdate);
            await Assert.That(rows[1].RankOld).IsNull().Because("rank 0 is a placement account, never a rank of 0");
            await Assert.That(rows[1].RankNew).IsNull();
            await Assert.That(rows[1].RankChange).IsNull();
            await Assert.That(rows[1].IsUnranked).IsTrue();
        }
    }

    [Test]
    public async Task WithoutAnUpdate_APremierRankIsThePredictionForTheResult()
    {
        List<MatchRankRow> rows = MatchMetaEvaluator.RankRows([Premier(A, 3), Premier(B, 2)], [], () => 3);
        List<MatchRankRow> tie = MatchMetaEvaluator.RankRows([Premier(A, 3)], [], () => 0);

        using (Assert.Multiple())
        {
            await Assert.That(rows[0].RankNew).IsEqualTo(14213 + 189).Because("CT won: the win prediction");
            await Assert.That(rows[0].Wins).IsEqualTo(301).Because("the controller's wins are pre-match");
            await Assert.That(rows[0].RankSource).IsEqualTo(RankSource.Predicted);
            await Assert.That(rows[1].RankNew).IsEqualTo(14213 - 150).Because("T lost: the loss prediction");
            await Assert.That(rows[1].RankChange).IsEqualTo(-150);
            await Assert.That(rows[1].Wins).IsEqualTo(300);
            await Assert.That(tie[0].RankNew).IsEqualTo(14213 + 10);
        }
    }

    [Test]
    public async Task WithoutAnUpdate_AnUnknownResultOrACompetitiveRank_HasNoRankAfter()
    {
        List<MatchRankRow> unknown = MatchMetaEvaluator.RankRows([Premier(A, 3)], [], () => null);
        List<MatchRankRow> competitive = MatchMetaEvaluator.RankRows([new ControllerRank(A, 3, 8, 2, 12, 0, 0, 0)], [], () => 3);
        List<MatchRankRow> placement = MatchMetaEvaluator.RankRows([Premier(A, 3, ranking: 0, wins: 5)], [], () => 3);

        using (Assert.Multiple())
        {
            await Assert.That(unknown[0].RankOld).IsEqualTo(14213);
            await Assert.That(unknown[0].RankNew).IsNull();
            await Assert.That(unknown[0].Wins).IsNull();
            await Assert.That(unknown[0].RankSource).IsEqualTo(RankSource.Controller);
            await Assert.That(competitive[0].RankOld).IsEqualTo(8).Because("a competitive rank under 10 wins is still a rank");
            await Assert.That(competitive[0].RankNew).IsNull().Because("competitive predictions are all 0");
            await Assert.That(competitive[0].IsUnranked).IsFalse();
            await Assert.That(placement[0].IsUnranked).IsTrue();
        }
    }

    [Test]
    public async Task AControllerWithNoRankSystem_IsNoRow()
    {
        List<MatchRankRow> rows = MatchMetaEvaluator.RankRows([new ControllerRank(A, 3, 0, 0, 0, 0, 0, 0)], [], () => 3);

        await Assert.That(rows).IsEmpty();
    }

    [Test]
    [Arguments(13, 11, 3)]
    [Arguments(9, 13, 2)]
    [Arguments(16, 14, 3)]
    [Arguments(12, 12, 0)]
    [Arguments(7, 3, null)]
    public async Task TheWinner_IsAFinishedResultOrATie(int ct, int t, int? winner)
    {
        await Assert.That(MatchMetaEvaluator.WinningTeam(new LibraryTeamState(ct, t, null, null, []))).IsEqualTo(winner);
    }

    [Test]
    public async Task TheMode_FallsBackToTheRankSystem_AndAFaceitServerReadsAsFaceit()
    {
        (string? mode, string source, _) = MatchMetaEvaluator.Capture(null, DemoSourceKind.Unknown, "FACEIT.com register to play here",
            [new ControllerRank(A, 3, 8, 2, 12, 0, 0, 0), new ControllerRank(B, 2, 9, 4, 12, 0, 0, 0)], [], () => null);
        (string? named, string valve, _) = MatchMetaEvaluator.Capture("premier", DemoSourceKind.GotvMatchmaking, "Valve CS2", [], [], () => null);

        using (Assert.Multiple())
        {
            await Assert.That(mode).IsEqualTo("competitive");
            await Assert.That(source).IsEqualTo("Faceit");
            await Assert.That(named).IsEqualTo("premier");
            await Assert.That(valve).IsEqualTo("GotvMatchmaking");
        }
    }

    [Test]
    public async Task ThePass_WritesTheRankSection_TheIndexMirrorsIt_AndTheRecordPassKeepsIt()
    {
        const string path = "/d/match730_003826256877184877003_0981591541_410.dem";
        DemoCacheStore store = new(null);
        store.Upsert(new DemoCacheRecord { Path = path, Size = 1, ModifiedTicks = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
        MatchMetaEvaluator evaluator = new(store);
        bool wanted = evaluator.Wants(path);

        evaluator.EvaluateForward(path, new ForwardDemoResult
        {
            Demo = new DemoDescriptor("de_test", 64, 1f / 64, 1000, 0, "test", "test", 0, DemoProfile.Unknown, new Dictionary<int, PlayerInfo>()),
            Rounds = [],
            FrameCount = 10,
            FirstServerTick = 0,
            LastServerTick = 1000,
            GameMode = "premier",
            ControllerRanks = [Premier(A, 3)],
            RankUpdates = [new ServerRankUpdate(A, 14213, 14402, 189, 301, 11)]
        });
        DemoCacheRecord record = store.TryLoadRecord(path, false)!;
        DemoCacheIndexEntry row = store.TryGetIndex(path)!;
        MatchMetaPass matchMeta = new(store, _ => null, _ => new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc));
        matchMeta.Run(record, CancellationToken.None);
        DemoMatchMeta after = store.TryLoadRecord(path, false)!.MatchMeta!;
        string json = JsonSerializer.Serialize(after);

        using (Assert.Multiple())
        {
            await Assert.That(wanted).IsTrue();
            await Assert.That(evaluator.Wants(path)).IsFalse().Because("stamped current");
            await Assert.That(row.GameMode).IsEqualTo("premier");
            await Assert.That(row.MatchSource).IsEqualTo("Unknown");
            await Assert.That(after.MatchTimeSource).IsEqualTo(MatchTimeSource.FileWriteTime);
            await Assert.That(after.GameMode).IsEqualTo("premier").Because("the record pass keeps what the visit read");
            await Assert.That(after.Players!.Single().RankNew).IsEqualTo(14402);
            await Assert.That(json).Contains("\"RankSource\":\"ServerRankUpdate\"").Because("the source is written by name");
        }
    }

    [Test]
    public async Task AHeldVisit_TakesTheRanksOffItsOwnRead_AndNoneWithoutOne()
    {
        const string withUpdate = "/d/held-update.dem", withoutUpdate = "/d/held-bare.dem";
        DemoCacheStore store = new(null);
        MatchMetaEvaluator evaluator = new(store);
        ParsedDemo parsed = SyntheticParsedDemo.Create();
        foreach (string path in new[] { withUpdate, withoutUpdate })
        {
            store.Upsert(new DemoCacheRecord { Path = path, Size = 1, ModifiedTicks = 1, Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 } });
        }

        evaluator.Evaluate(withUpdate, new PassInput(new VisitedDemo(withUpdate), PassLevel.Background, parsed, null, CancellationToken.None)
        {
            RetainedRanks = [new ServerRankUpdate(A, 14213, 14402, 189, 312, 11)]
        });
        evaluator.Evaluate(withoutUpdate, new PassInput(new VisitedDemo(withoutUpdate), PassLevel.Background, parsed, null, CancellationToken.None));
        List<MatchRankRow> captured = [.. store.TryLoadRecord(withUpdate, false)!.MatchMeta!.Players!];
        DemoMatchMeta bare = store.TryLoadRecord(withoutUpdate, false)!.MatchMeta!;

        using (Assert.Multiple())
        {
            await Assert.That(captured.Count).IsEqualTo(1);
            await Assert.That(captured[0].RankSource).IsEqualTo(RankSource.ServerRankUpdate)
                .Because("a held visit reads the server's update, never a replay of the controllers");
            await Assert.That(captured[0].RankNew).IsEqualTo(14402);
            await Assert.That(bare.Players).IsNull().Because("a read that captured no update writes no rank rows");
        }
    }
}
