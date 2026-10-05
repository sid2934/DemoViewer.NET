#region

using System.Text.Json;
using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The projection from a synthetic <c>round_facts</c> table (two rows per round) onto the record:
///     pairing by side, the kill timeline from the list columns, known columns onto typed fields and
///     unknown ones into <c>Extra</c>, and the two kinds of "nothing here" a column can carry.
/// </summary>
public class RoundFactsProjectionTests
{
    private const int T = 2;
    private const int Ct = 3;

    private static readonly IReadOnlyList<ClipRound> _twoRounds = [new ClipRound(1, 1000), new ClipRound(2, 5000)];

    private static Dictionary<string, object?> Row(int round, int side, int freezeEnd,
        params (string Column, object? Value)[] cells)
    {
        Dictionary<string, object?> row = new(StringComparer.Ordinal)
        {
            [RoundFactsColumns.RoundNumber] = round,
            [RoundFactsColumns.Side] = side,
            [RoundFactsColumns.FreezeEndTick] = freezeEnd,
            [RoundFactsColumns.Slots] = side == Ct ? (int[])[1, 2, 3, 4, 5] : (int[])[6, 7, 8, 9, 10],
            [RoundFactsColumns.Players] = 5,
            [RoundFactsColumns.ScoreBefore] = 0,
            [RoundFactsColumns.Equipment] = 4000,
            [RoundFactsColumns.Money] = 4000,
            [RoundFactsColumns.MatchRound] = round - 1,
            [RoundFactsColumns.WinnerSide] = Ct,
            [RoundFactsColumns.EndReason] = (int)RoundEndReason.CTWin,
            [RoundFactsColumns.EndTick] = freezeEnd + 3000,
            [RoundFactsColumns.OfficiallyEndedTick] = freezeEnd + 3448,
            [RoundFactsColumns.RoundTime] = 115,
            [RoundFactsColumns.GamePhase] = 2
        };

        foreach ((string column, object? value) in cells)
        {
            row[column] = value;
        }

        return row;
    }

    private static RoundFactsTable Table(IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlySet<string>? unavailable = null, IReadOnlyDictionary<string, object?>? parameters = null) =>
        new([.. rows], unavailable ?? new HashSet<string>(StringComparer.Ordinal),
            parameters ?? new Dictionary<string, object?>(StringComparer.Ordinal), []);

    [Test]
    public async Task TwoRowsPerRound_PairBySideOntoOneRecord()
    {
        RoundFactsTable table = Table(
        [
            Row(1, T, 1000, (RoundFactsColumns.Equipment, 1000)),
            Row(1, Ct, 1000, (RoundFactsColumns.Equipment, 1100)),
            Row(2, Ct, 5000, (RoundFactsColumns.ScoreBefore, 1), (RoundFactsColumns.Equipment, 21000)),
            // Round 2's winner disagrees between the two rows on purpose: the CT row keeps the default.
            Row(2, T, 5000, (RoundFactsColumns.Equipment, 3000), (RoundFactsColumns.WinnerSide, T),
                (RoundFactsColumns.EndReason, (int)RoundEndReason.TerroristsWin))
        ]);

        RoundFactsRows rows = RoundFactsProjection.Project(_twoRounds, table);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Schema).IsEqualTo(1);
            await Assert.That(rows.Rounds.Count).IsEqualTo(2);
            await Assert.That(rows.Warnings).IsEmpty();

            RoundFacts first = rows.Rounds[0];
            await Assert.That(first.Number).IsEqualTo(1);
            await Assert.That(first.FreezeEndTick).IsEqualTo(1000);
            await Assert.That(first.IsLive).IsTrue();
            await Assert.That(first.MatchRoundNumber).IsEqualTo(1).Because("total_rounds_played + 1");
            await Assert.That(first.Half).IsEqualTo(RoundHalf.First);
            await Assert.That(first.EndTick).IsEqualTo(4000);
            await Assert.That(first.EndSource).IsEqualTo(RoundEndSource.WinStatus);
            await Assert.That(first.EndReason).IsEqualTo(RoundEndReason.CTWin);
            await Assert.That(first.WinnerSide).IsEqualTo(Ct);
            await Assert.That(first.RoundTimeSeconds).IsEqualTo(115);
            await Assert.That(first.GamePhase).IsEqualTo(2);
            await Assert.That(first.Ct.Side).IsEqualTo(Ct);
            await Assert.That(first.T.Side).IsEqualTo(T);
            await Assert.That(first.Ct.Slots).IsEquivalentTo([1, 2, 3, 4, 5]);
            await Assert.That(first.T.Slots).IsEquivalentTo([6, 7, 8, 9, 10]);
            await Assert.That(first.Ct.EquipmentFreezeEnd).IsEqualTo(1100);
            await Assert.That(first.T.EquipmentFreezeEnd).IsEqualTo(1000);
            await Assert.That(first.Ct.BuyType).IsEqualTo(BuyType.Pistol);
            await Assert.That(first.T.BuyType).IsEqualTo(BuyType.Pistol);
            await Assert.That(first.ProjectionWarnings).IsEmpty();

            RoundFacts second = rows.Rounds[1];
            await Assert.That(second.Ct.ScoreBefore).IsEqualTo(1);
            await Assert.That(second.Ct.WonPreviousRound).IsTrue();
            await Assert.That(second.T.WonPreviousRound).IsFalse();
            await Assert.That(second.Ct.BuyType).IsEqualTo(BuyType.Full);
            await Assert.That(second.T.BuyType).IsEqualTo(BuyType.Eco);
            await Assert.That(second.WinnerSide).IsEqualTo(Ct).Because("the CT row's value wins a disagreement");
            await Assert.That(second.ProjectionWarnings.Any(w => w.StartsWith("winner_side", StringComparison.Ordinal)))
                .IsTrue().Because("a disagreeing round-level column is a warning on the round, and the row still lands");
            await Assert.That(second.ProjectionWarnings.Any(w => w.StartsWith("end_reason", StringComparison.Ordinal)))
                .IsTrue();
        }
    }

    [Test]
    public async Task Kills_ComeFromTheFourListColumns_InTickOrder()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000,
                (RoundFactsColumns.KillTicks, (int[])[1500, 1800, 2200]),
                (RoundFactsColumns.KillVictimSide, (int[])[T, Ct, T]),
                (RoundFactsColumns.KillTeamAlive, (int[])[5, 4, 4]),
                (RoundFactsColumns.KillEnemyAlive, (int[])[4, 4, 3]),
                (RoundFactsColumns.KillVictimSlot, (int[])[6, 1, 7]),
                (RoundFactsColumns.KillAttackerSlot, (int[])[1, 6, -1]),
                (RoundFactsColumns.OpeningKillTick, 1500),
                (RoundFactsColumns.FirstContactTick, 1400)),
            Row(1, T, 1000,
                (RoundFactsColumns.KillTicks, (int[])[1500, 1800, 2200]),
                (RoundFactsColumns.KillVictimSide, (int[])[T, Ct, T]),
                (RoundFactsColumns.KillTeamAlive, (int[])[4, 4, 3]),
                (RoundFactsColumns.KillEnemyAlive, (int[])[5, 4, 4]),
                (RoundFactsColumns.KillVictimSlot, (int[])[6, 1, 7]),
                (RoundFactsColumns.KillAttackerSlot, (int[])[1, 6, -1]),
                (RoundFactsColumns.OpeningKillTick, 1500),
                (RoundFactsColumns.FirstContactTick, 1400))
        ]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.Kills.Count).IsEqualTo(3);
            await Assert.That(round.Kills[0].Tick).IsEqualTo(1500);
            await Assert.That(round.Kills[0].VictimSide).IsEqualTo(T);
            await Assert.That(round.Kills[0].VictimSlot).IsEqualTo(6);
            await Assert.That(round.Kills[0].AttackerSlot).IsEqualTo(1);
            await Assert.That(round.Kills[0].CtAlive).IsEqualTo(5);
            await Assert.That(round.Kills[0].TAlive).IsEqualTo(4);
            await Assert.That(round.Kills[1].CtAlive).IsEqualTo(4);
            await Assert.That(round.Kills[1].TAlive).IsEqualTo(4);
            await Assert.That(round.Kills[2].AttackerSlot).IsEqualTo(-1).Because("a world kill has no attacker");
            await Assert.That(round.Kills[2].TAlive).IsEqualTo(3);
            await Assert.That(round.OpeningKillTick).IsEqualTo(1500);
            await Assert.That(round.FirstContactTick).IsEqualTo(1400).Because("damage precedes the kill");
            await Assert.That(RoundPhases.AliveAt(round, 2000)).IsEqualTo((4, 4));
        }
    }

    [Test]
    public async Task Kills_FromTheTRowAlone_StillYieldBothCounts()
    {
        RoundFactsTable table = Table(
        [
            Row(1, T, 1000,
                (RoundFactsColumns.KillTicks, (int[])[1500]),
                (RoundFactsColumns.KillVictimSide, (int[])[Ct]),
                (RoundFactsColumns.KillTeamAlive, (int[])[5]),
                (RoundFactsColumns.KillEnemyAlive, (int[])[4]))
        ]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.Kills[0].CtAlive).IsEqualTo(4).Because("the T row's enemies are the CTs");
            await Assert.That(round.Kills[0].TAlive).IsEqualTo(5);
            await Assert.That(round.ProjectionWarnings).Contains("only the T row is present");
        }
    }

    [Test]
    public async Task MismatchedListLengths_WarnAndTruncate_NeverThrow()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000,
                (RoundFactsColumns.KillTicks, (int[])[1500, 1800, 2200]),
                (RoundFactsColumns.KillVictimSide, (int[])[T, Ct]),
                (RoundFactsColumns.KillTeamAlive, (int[])[5, 4, 4]),
                (RoundFactsColumns.KillEnemyAlive, (int[])[4, 4, 3])),
            Row(1, T, 1000,
                (RoundFactsColumns.KillTicks, (int[])[1500, 1800, 2200]),
                (RoundFactsColumns.KillVictimSide, (int[])[T, Ct]),
                (RoundFactsColumns.KillTeamAlive, (int[])[4, 4, 3]),
                (RoundFactsColumns.KillEnemyAlive, (int[])[5, 4, 4]))
        ]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.Kills.Count).IsEqualTo(2);
            await Assert.That(round.ProjectionWarnings.Any(w => w.Contains("kill_victim_side has 2 entries for 3 kills", StringComparison.Ordinal)))
                .IsTrue();
        }
    }

    [Test]
    public async Task AnUnavailableColumn_IsMarkedUnavailable_AndDistinctFromNull()
    {
        HashSet<string> unavailable = new(StringComparer.Ordinal)
        {
            RoundFactsColumns.Money,
            RoundFactsColumns.PlantSite
        };
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000, (RoundFactsColumns.Money, null), (RoundFactsColumns.Equipment, 8000),
                (RoundFactsColumns.PlantTick, null)),
            Row(1, T, 1000, (RoundFactsColumns.Money, null), (RoundFactsColumns.Equipment, 8000),
                (RoundFactsColumns.PlantTick, null)),
            Row(2, Ct, 5000, (RoundFactsColumns.Money, null), (RoundFactsColumns.Equipment, 8000)),
            Row(2, T, 5000, (RoundFactsColumns.Money, null), (RoundFactsColumns.Equipment, 8000))
        ], unavailable);

        RoundFactsRows rows = RoundFactsProjection.Project(_twoRounds, table);
        RoundFacts second = rows.Rounds[1];

        using (Assert.Multiple())
        {
            await Assert.That(second.Sources[RoundFactsColumns.Money]).IsEqualTo(RoundFactsSourceKind.Unavailable);
            await Assert.That(second.Sources[RoundFactsColumns.PlantSite]).IsEqualTo(RoundFactsSourceKind.Unavailable);
            await Assert.That(second.Sources[RoundFactsColumns.PlantTick]).IsEqualTo(RoundFactsSourceKind.Engine)
                .Because("a null plant tick from a provided column means the demo had no plant");
            await Assert.That(second.PlantTick).IsNull();
            await Assert.That(second.T.MoneyReliable).IsFalse();
            await Assert.That(second.T.MoneyAtFreezeEnd).IsNull();
            // 8,000 at five players after a lost round would be Force with money; without it, Semi.
            await Assert.That(second.T.BuyType).IsEqualTo(BuyType.Semi)
                .Because("Force needs a reliable money read; the three equipment classes never touch money");
            await Assert.That(second.Sources.Count).IsEqualTo(RoundFactsColumns.Known.Count);
        }
    }

    [Test]
    public async Task ForceReadsMoney_OnlyAfterALostRoundWithAReliableRead()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000),
            Row(1, T, 1000),
            // Round 2: CT won round 1 (the row default), T lost; T has 8,000 equipment and 1,500 in the bank.
            Row(2, Ct, 5000, (RoundFactsColumns.Equipment, 8000), (RoundFactsColumns.Money, 1500)),
            Row(2, T, 5000, (RoundFactsColumns.Equipment, 8000), (RoundFactsColumns.Money, 1500))
        ]);

        RoundFacts second = RoundFactsProjection.Project(_twoRounds, table).Rounds[1];

        using (Assert.Multiple())
        {
            await Assert.That(second.T.MoneyReliable).IsTrue();
            await Assert.That(second.T.MoneyAtFreezeEnd).IsEqualTo(1500);
            await Assert.That(second.T.BuyType).IsEqualTo(BuyType.Force);
            await Assert.That(second.Ct.BuyType).IsEqualTo(BuyType.Semi).Because("the CT side won the previous round");
        }
    }

    [Test]
    public async Task ADeclaredBuyTypeColumn_WinsOverTheAppSideClassifier()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000, (RoundFactsColumns.BuyType, "force")),
            Row(1, T, 1000, (RoundFactsColumns.BuyType, "SEMI"))
        ]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.Ct.BuyType).IsEqualTo(BuyType.Force);
            await Assert.That(round.T.BuyType).IsEqualTo(BuyType.Semi);
        }
    }

    [Test]
    public async Task Parameters_BecomeTheThresholdsOnEverySide()
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
        {
            ["eco_max_per_player"] = 2000,
            ["regulation_rounds"] = 30
        };
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000, (RoundFactsColumns.MatchRound, 15), (RoundFactsColumns.Equipment, 9000)),
            Row(1, T, 1000, (RoundFactsColumns.MatchRound, 15), (RoundFactsColumns.Equipment, 11000))
        ], parameters: parameters);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.Ct.Thresholds.EcoMaxPerPlayer).IsEqualTo(2000);
            await Assert.That(round.Ct.Thresholds.RegulationRounds).IsEqualTo(30);
            await Assert.That(round.Ct.Thresholds.FullMinPerPlayerCt).IsEqualTo(4000).Because("undeclared parameters keep the shipped default");
            await Assert.That(round.MatchRoundNumber).IsEqualTo(16);
            await Assert.That(round.Ct.BuyType).IsEqualTo(BuyType.Pistol).Because("round 16 opens the second half at MR15");
            await Assert.That(round.Half).IsEqualTo(RoundHalf.Second);
        }
    }

    [Test]
    public async Task UnknownColumns_LandInExtra_PerSideOnlyWhenTheyDiffer()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000, ("my_team_column", "yes"), ("shared_column", 7), ("only_ct", true)),
            Row(1, T, 1000, ("my_team_column", "no"), ("shared_column", 7))
        ]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.Extra["shared_column"]).IsEqualTo(7L);
            await Assert.That(round.Extra["my_team_column.ct"]).IsEqualTo("yes");
            await Assert.That(round.Extra["my_team_column.t"]).IsEqualTo("no");
            await Assert.That(round.Extra["only_ct"]).IsEqualTo(true);
            await Assert.That(round.Extra.ContainsKey("my_team_column")).IsFalse();
            await Assert.That(round.Extra.ContainsKey(RoundFactsColumns.Equipment)).IsFalse()
                .Because("known columns never leak into Extra");
        }
    }

    [Test]
    public async Task PlantSite_IsALetter_WithTheEntityKeptRaw()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000, (RoundFactsColumns.PlantSite, "BombsiteA"), (RoundFactsColumns.PlantSiteEntity, 173),
                (RoundFactsColumns.PlantTick, 2500), (RoundFactsColumns.PlanterSlot, 7)),
            Row(1, T, 1000, (RoundFactsColumns.PlantSite, "BombsiteA"), (RoundFactsColumns.PlantSiteEntity, 173),
                (RoundFactsColumns.PlantTick, 2500), (RoundFactsColumns.PlanterSlot, 7)),
            Row(2, Ct, 5000, (RoundFactsColumns.PlantSite, "b")),
            Row(2, T, 5000, (RoundFactsColumns.PlantSite, "b"))
        ]);

        RoundFactsRows rows = RoundFactsProjection.Project(_twoRounds, table);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Rounds[0].PlantSite).IsEqualTo(BombSite.A);
            await Assert.That(rows.Rounds[0].PlantSiteEntity).IsEqualTo(173);
            await Assert.That(rows.Rounds[0].PlantTick).IsEqualTo(2500);
            await Assert.That(rows.Rounds[0].PlanterSlot).IsEqualTo(7);
            await Assert.That(rows.Rounds[1].PlantSite).IsEqualTo(BombSite.B);
            await Assert.That(RoundPhases.At(rows.Rounds[0], 2600, Ct)).IsEqualTo(RoundPhase.Retake);
        }
    }

    [Test]
    public async Task TheEndFallsBackToTheOfficialEvent_AndAnUnfinishedLastRoundIsNotLive()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000, (RoundFactsColumns.EndTick, null), (RoundFactsColumns.EndReason, null)),
            Row(1, T, 1000, (RoundFactsColumns.EndTick, null), (RoundFactsColumns.EndReason, null)),
            Row(2, Ct, 5000, (RoundFactsColumns.EndTick, null), (RoundFactsColumns.OfficiallyEndedTick, null),
                (RoundFactsColumns.WinnerSide, null)),
            Row(2, T, 5000, (RoundFactsColumns.EndTick, null), (RoundFactsColumns.OfficiallyEndedTick, null),
                (RoundFactsColumns.WinnerSide, null))
        ]);

        RoundFactsRows rows = RoundFactsProjection.Project(_twoRounds, table);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Rounds[0].EndTick).IsEqualTo(4448);
            await Assert.That(rows.Rounds[0].EndSource).IsEqualTo(RoundEndSource.OfficiallyEndedEvent);
            await Assert.That(rows.Rounds[0].EndReason).IsEqualTo(RoundEndReason.Unknown);
            await Assert.That(rows.Rounds[0].IsLive).IsTrue().Because("round 1 has an end");
            await Assert.That(rows.Rounds[1].EndTick).IsNull();
            await Assert.That(rows.Rounds[1].EndSource).IsEqualTo(RoundEndSource.None);
            await Assert.That(rows.Rounds[1].WinnerSide).IsEqualTo(0);
            await Assert.That(rows.Rounds[1].IsLive).IsFalse().Because("no end and no next freeze-end: truncated");
        }
    }

    [Test]
    public async Task RowsWithoutADerivedRound_AndRoundsWithoutRows_AreTopLevelWarnings()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000),
            Row(1, T, 1000),
            Row(9, Ct, 90000),
            Row(9, T, 90000)
        ]);

        RoundFactsRows rows = RoundFactsProjection.Project(_twoRounds, table);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Rounds.Count).IsEqualTo(1);
            await Assert.That(rows.Warnings).Contains("round 2: no rows");
            await Assert.That(rows.Warnings).Contains("round 9: rows with no derived round were dropped");
        }
    }

    [Test]
    public async Task AFreezeEndThatDisagreesWithTheDeriver_IsAWarning_AndTheDeriverWins()
    {
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1001),
            Row(1, T, 1001)
        ]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.FreezeEndTick).IsEqualTo(1000);
            await Assert.That(round.ProjectionWarnings.Any(w => w.StartsWith("freeze_end_tick 1001", StringComparison.Ordinal)))
                .IsTrue();
        }
    }

    [Test]
    public async Task ARowWithoutASide_IsIgnoredWithAWarning()
    {
        Dictionary<string, object?> stray = Row(1, Ct, 1000);
        stray[RoundFactsColumns.Side] = null;
        RoundFactsTable table = Table([Row(1, Ct, 1000), Row(1, T, 1000), stray]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        await Assert.That(round.ProjectionWarnings).Contains("a row with side 'null' was ignored");
    }

    [Test]
    public async Task CellsThatArriveAsJson_CoerceLikeBoxedValues()
    {
        // The engine seam may hand back JsonElement cells (a table read back from a run's output).
        using JsonDocument doc = JsonDocument.Parse("""{"n": 21000, "list": [1500, 1900], "site": "BombsiteB", "flag": true}""");
        JsonElement root = doc.RootElement;
        RoundFactsTable table = Table(
        [
            Row(1, Ct, 1000, (RoundFactsColumns.Equipment, root.GetProperty("n")),
                (RoundFactsColumns.KillTicks, root.GetProperty("list")),
                (RoundFactsColumns.PlantSite, root.GetProperty("site")),
                (RoundFactsColumns.MoneyReliable, root.GetProperty("flag"))),
            Row(1, T, 1000, (RoundFactsColumns.Equipment, root.GetProperty("n")),
                (RoundFactsColumns.KillTicks, root.GetProperty("list")),
                (RoundFactsColumns.PlantSite, root.GetProperty("site")),
                (RoundFactsColumns.MoneyReliable, root.GetProperty("flag")))
        ]);

        RoundFacts round = RoundFactsProjection.Project([new ClipRound(1, 1000)], table).Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(round.Ct.EquipmentFreezeEnd).IsEqualTo(21000);
            await Assert.That(round.Kills.Count).IsEqualTo(2);
            await Assert.That(round.PlantSite).IsEqualTo(BombSite.B);
            await Assert.That(round.Ct.MoneyReliable).IsTrue();
        }
    }
}
