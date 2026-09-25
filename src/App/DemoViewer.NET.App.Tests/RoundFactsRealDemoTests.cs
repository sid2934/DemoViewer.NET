#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Round facts against a real Valve matchmaking demo, as the design's §7 lists them, through the
///     production wiring: the shipped <c>rules/round_facts.rules.yaml</c> evaluated by the engine row
///     source and projected by the evaluator.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundFactsRealDemoTests
{
    private static ParsedDemo Parse()
    {
        string path = DemoTestHelper.RequireDemo();
        return DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
    }

    // The production wiring: the real identity over the shipped-plus-user rules and the engine row source,
    // writing into an in-memory store.
    private static RoundFactsRows RowsFor(ParsedDemo parsed, string path)
    {
        DemoCacheStore store = new(null);
        RoundFactsEvaluator evaluator = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        evaluator.OnParsedOpportunistically(path, parsed);
        return store.TryLoadRecord(path)?.RoundFacts
               ?? throw new InvalidOperationException("the evaluator wrote no rows");
    }

    [Test]
    public async Task FreezeEndTicks_AgreeWithTheRoundAuthority_ForEveryRound()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = Parse();
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);
        RoundFactsRows rows = RowsFor(parsed, path);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Rounds.Count).IsEqualTo(rounds.Count);
            for (int i = 0; i < rounds.Count; i++)
            {
                await Assert.That(rows.Rounds[i].Number).IsEqualTo(rounds[i].Number);
                await Assert.That(rows.Rounds[i].FreezeEndTick).IsEqualTo(rounds[i].StartTickFrameClock);
                await Assert.That(rows.Rounds[i].ProjectionWarnings).IsEmpty();
            }
        }
    }

    [Test]
    public async Task EveryLiveRound_HasAnEnd_AWinner_AReason_AndTheOfficialEndAfterTheWinPanel()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = Parse();
        RoundFactsRows rows = RowsFor(parsed, path);
        int[] officiallyEnded =
        [
            .. parsed.AllGameEvents
                .Where(e => string.Equals(e.Name, "round_officially_ended", StringComparison.Ordinal))
                .Select(e => e.GameTick)
        ];

        // The close follows the decision by the 7 s win panel, or 8.5 s at the end of a half, when the
        // break is waited out first; either can land a tick off. The match's last round has no
        // round_officially_ended at all: it closes on cs_win_panel_match.
        double[] panels = [7.0, 8.5];
        using (Assert.Multiple())
        {
            foreach (RoundFacts round in rows.Rounds.Where(r => r.IsLive))
            {
                await Assert.That(round.EndTick).IsNotNull();
                await Assert.That(round.EndSource).IsEqualTo(RoundEndSource.WinStatus);
                await Assert.That(round.WinnerSide is 2 or 3).IsTrue();
                await Assert.That(round.EndReason).IsNotEqualTo(RoundEndReason.Unknown);
                int decided = round.EndTick!.Value;
                if (!officiallyEnded.Any(t => t > decided))
                {
                    continue;
                }

                await Assert.That(officiallyEnded.Any(t => panels.Any(s => Math.Abs(t - (decided + s * parsed.TickRate)) <= 1))).IsTrue()
                    .Because($"round {round.Number}: the close is 7 s or 8.5 s after the decision");
            }
        }
    }

    [Test]
    public async Task EveryPlantedRound_HasASite_AndThePlantTickIsTheEventsGameTick()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = Parse();
        RoundFactsRows rows = RowsFor(parsed, path);
        HashSet<int> plants =
        [
            .. parsed.AllGameEvents
                .Where(e => string.Equals(e.Name, "bomb_planted", StringComparison.Ordinal))
                .Select(e => e.GameTick)
        ];

        using (Assert.Multiple())
        {
            await Assert.That(rows.Rounds.Count(r => r.PlantTick is not null)).IsEqualTo(plants.Count);
            foreach (RoundFacts round in rows.Rounds.Where(r => r.PlantTick is not null))
            {
                await Assert.That(round.PlantSite).IsNotEqualTo(BombSite.Unknown);
                await Assert.That(plants).Contains(round.PlantTick!.Value);
            }
        }
    }

    [Test]
    public async Task AliveCounts_StartAtTheFreezeEndCount_AndNeverRise()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = Parse();
        RoundFactsRows rows = RowsFor(parsed, path);

        using (Assert.Multiple())
        {
            foreach (RoundFacts round in rows.Rounds.Where(r => r.IsLive))
            {
                int ct = round.Ct.PlayersAtFreezeEnd;
                int t = round.T.PlayersAtFreezeEnd;
                foreach (KillStep kill in round.Kills)
                {
                    await Assert.That(kill.CtAlive).IsLessThanOrEqualTo(ct);
                    await Assert.That(kill.TAlive).IsLessThanOrEqualTo(t);
                    ct = kill.CtAlive;
                    t = kill.TAlive;
                }
            }
        }
    }

    [Test]
    public async Task ScoreBefore_FollowsTheWinner_AndEndsAtTheLibrarysFinalScore()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = Parse();
        RoundFactsRows rows = RowsFor(parsed, path);
        (int? ct, int? t, _, _) = DemoLibraryService.ExtractFinalScore(parsed);
        List<RoundFacts> live = [.. rows.Rounds.Where(r => r.IsLive)];

        using (Assert.Multiple())
        {
            for (int i = 1; i < live.Count; i++)
            {
                RoundFacts previous = live[i - 1];
                RoundFacts current = live[i];
                // The halftime swap moves the score with the teams; a per-side score therefore swaps too.
                bool swapped = current.Half != previous.Half;
                int expectedCt = (swapped ? previous.T.ScoreBefore : previous.Ct.ScoreBefore)
                                 + (previous.WinnerSide == (swapped ? 2 : 3) ? 1 : 0);
                int expectedT = (swapped ? previous.Ct.ScoreBefore : previous.T.ScoreBefore)
                                + (previous.WinnerSide == (swapped ? 3 : 2) ? 1 : 0);
                await Assert.That(current.Ct.ScoreBefore).IsEqualTo(expectedCt).Because($"round {current.Number} CT");
                await Assert.That(current.T.ScoreBefore).IsEqualTo(expectedT).Because($"round {current.Number} T");
            }

            RoundFacts last = live[^1];
            await Assert.That(last.Ct.ScoreBefore + (last.WinnerSide == 3 ? 1 : 0)).IsEqualTo(ct!.Value);
            await Assert.That(last.T.ScoreBefore + (last.WinnerSide == 2 ? 1 : 0)).IsEqualTo(t!.Value);
        }
    }

    [Test]
    public async Task TheWorkedChecks_HoldAtTheDefaults()
    {
        // Dust2 build 10896: round 2 CT eco / T semi, round 3 CT force / T full, round 5 T eco. The design's
        // worked check had round 3 CT as semi from equipment alone; the money read makes it force: the CT
        // side lost round 2 and holds $600 across five players at freeze end, under the $2,000 line.
        string path = DemoTestHelper.RequireDemo("match730_003844252717140672725_0377894676_389.dem");
        ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        RoundFactsRows rows = RowsFor(parsed, path);
        RoundFacts Round(int n) => rows.Rounds.Single(r => r.Number == n);

        using (Assert.Multiple())
        {
            await Assert.That(Round(2).Ct.BuyType).IsEqualTo(BuyType.Eco);
            await Assert.That(Round(2).T.BuyType).IsEqualTo(BuyType.Semi);
            await Assert.That(Round(3).Ct.BuyType).IsEqualTo(BuyType.Force);
            await Assert.That(Round(3).Ct.MoneyAtFreezeEnd).IsEqualTo(600);
            await Assert.That(Round(3).T.BuyType).IsEqualTo(BuyType.Full);
            await Assert.That(Round(5).T.BuyType).IsEqualTo(BuyType.Eco);
            await Assert.That(Round(5).T.EquipmentFreezeEnd).IsEqualTo(5000);
        }
    }
}
