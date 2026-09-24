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
///     Round facts against a real Valve matchmaking demo, as the design's §7 lists them. Every one of
///     these needs rows, and rows need the engine surfaces filed as CS2DemoKit #54 (a team subject on
///     the forward path, game-rules providers, a real round end, frame-clock ticks, team money, plant
///     site). Until that release is pinned and <c>rules/round_facts.rules.yaml</c> ships, the engine row
///     source writes nothing, so each test is skipped with that reason rather than failing or asserting
///     against nothing. Remove the skips when the pin bumps; the bodies are written against the record.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundFactsRealDemoTests
{
    private const string WaitingOnEngine =
        "waiting on CS2DemoKit #54: the round_facts ruleset needs engine surfaces the pinned release lacks, so no rows are written yet";

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
    [Skip(WaitingOnEngine)]
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
    [Skip(WaitingOnEngine)]
    public async Task EveryLiveRound_HasAnEnd_AWinner_AReason_AndTheOfficialEndSevenSecondsLater()
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

        using (Assert.Multiple())
        {
            foreach (RoundFacts round in rows.Rounds.Where(r => r.IsLive))
            {
                await Assert.That(round.EndTick).IsNotNull();
                await Assert.That(round.EndSource).IsEqualTo(RoundEndSource.WinStatus);
                await Assert.That(round.WinnerSide is 2 or 3).IsTrue();
                await Assert.That(round.EndReason).IsNotEqualTo(RoundEndReason.Unknown);
                int expected = round.EndTick!.Value + 7 * parsed.TickRate;
                await Assert.That(officiallyEnded.Any(t => Math.Abs(t - expected) <= 1)).IsTrue()
                    .Because($"round {round.Number}: the win panel is 7 s after the decision");
            }
        }
    }

    [Test]
    [Skip(WaitingOnEngine)]
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
    [Skip(WaitingOnEngine)]
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
    [Skip(WaitingOnEngine)]
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
    [Skip(WaitingOnEngine)]
    public async Task TheWorkedChecks_HoldAtTheDefaults()
    {
        // Dust2 build 10896: round 2 CT eco / T semi, round 3 CT semi / T full, round 5 T eco.
        string path = DemoTestHelper.RequireDemo("match730_003844252717140672725_0377894676_389.dem");
        ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        RoundFactsRows rows = RowsFor(parsed, path);
        RoundFacts Round(int n) => rows.Rounds.Single(r => r.Number == n);

        using (Assert.Multiple())
        {
            await Assert.That(Round(2).Ct.BuyType).IsEqualTo(BuyType.Eco);
            await Assert.That(Round(2).T.BuyType).IsEqualTo(BuyType.Semi);
            await Assert.That(Round(3).Ct.BuyType).IsEqualTo(BuyType.Semi);
            await Assert.That(Round(3).T.BuyType).IsEqualTo(BuyType.Full);
            await Assert.That(Round(5).T.BuyType).IsEqualTo(BuyType.Eco);
            await Assert.That(Round(5).T.EquipmentFreezeEnd).IsEqualTo(5000);
        }
    }
}
