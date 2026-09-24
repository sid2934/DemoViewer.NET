#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The read API over cached rows: one sidecar read per demo, the round a tick falls in by its
///     window, the cross-demo query, the label list in the vocabulary overview correction 10 pins, and
///     the evaluator's <c>Updated</c> forwarded to consumers.
/// </summary>
public class RoundFactsSourceTests
{
    private const string DemoA = "/d/a.dem";
    private const string DemoB = "/d/b.dem";

    private static RoundFacts Round(int number, int freezeEnd, int winner, BuyType ctBuy, BuyType tBuy,
        bool live = true, BombSite site = BombSite.Unknown) => new()
    {
        Number = number,
        MatchRoundNumber = number,
        Half = RoundHalf.First,
        IsLive = live,
        FreezeEndTick = freezeEnd,
        OpeningKillTick = freezeEnd + 500,
        PlantTick = site == BombSite.Unknown ? null : freezeEnd + 1000,
        PlantSite = site,
        PlanterSlot = site == BombSite.Unknown ? null : 7,
        EndTick = live ? freezeEnd + 3000 : null,
        EndSource = live ? RoundEndSource.WinStatus : RoundEndSource.None,
        EndReason = winner == 3 ? RoundEndReason.CTWin : RoundEndReason.TerroristsWin,
        WinnerSide = winner,
        RoundTimeSeconds = 115,
        Ct = new SideFacts
        {
            Side = 3,
            Slots = [1, 2, 3, 4, 5],
            PlayersAtFreezeEnd = 5,
            ScoreBefore = number - 1,
            EquipmentFreezeEnd = 21000,
            MoneyAtFreezeEnd = 3000,
            MoneyReliable = true,
            BuyType = ctBuy
        },
        T = new SideFacts
        {
            Side = 2,
            Slots = [6, 7, 8, 9, 10],
            PlayersAtFreezeEnd = 5,
            EquipmentFreezeEnd = 4500,
            BuyType = tBuy
        },
        Kills =
        [
            new KillStep
            {
                Tick = freezeEnd + 500,
                VictimSide = 2,
                CtAlive = 5,
                TAlive = 4
            }
        ],
        Extra = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pace"] = "slow"
        }
    };

    private static DemoCacheStore Store()
    {
        DemoCacheStore store = new(null);
        store.Upsert(new DemoCacheRecord
        {
            Path = DemoA,
            Size = 1,
            ModifiedTicks = 2,
            Parse = new TierStamp
            {
                Schema = 1,
                ComputedAtTicks = 1
            },
            RoundFactsFingerprint = "rf",
            RoundFacts = new RoundFactsRows
            {
                Schema = DemoCacheRecord.RoundFactsSchema,
                Rounds =
                [
                    Round(1, 1000, 3, BuyType.Pistol, BuyType.Pistol),
                    Round(2, 5000, 2, BuyType.Full, BuyType.Eco, site: BombSite.B),
                    Round(3, 9000, 0, BuyType.Semi, BuyType.Force, false)
                ]
            }
        });
        store.Upsert(new DemoCacheRecord
        {
            Path = DemoB,
            Size = 1,
            ModifiedTicks = 2,
            Parse = new TierStamp
            {
                Schema = 1,
                ComputedAtTicks = 1
            },
            RoundFactsFingerprint = "rf",
            RoundFacts = new RoundFactsRows
            {
                Schema = DemoCacheRecord.RoundFactsSchema,
                Rounds = [Round(1, 1000, 2, BuyType.Pistol, BuyType.Pistol), Round(2, 5000, 3, BuyType.Full, BuyType.Full)]
            }
        });
        store.Upsert(new DemoCacheRecord
        {
            Path = "/d/no-rows.dem",
            Size = 1,
            ModifiedTicks = 2
        });
        return store;
    }

    [Test]
    public async Task TryGet_ReadsTheRows_AndNullWithoutThem()
    {
        RoundFactsSource source = new(Store());

        using (Assert.Multiple())
        {
            await Assert.That(source.TryGet(DemoA)?.Rounds.Count).IsEqualTo(3);
            await Assert.That(source.TryGet("/d/no-rows.dem")).IsNull();
            await Assert.That(source.TryGet("/d/unknown.dem")).IsNull();
            await Assert.That(source.Schema).IsEqualTo(DemoCacheRecord.RoundFactsSchema);
        }
    }

    [Test]
    public async Task RoundAt_UsesTheRoundWindow_FromFreezeEndToTheNextFreezeEnd()
    {
        RoundFactsSource source = new(Store());

        using (Assert.Multiple())
        {
            await Assert.That(source.RoundAt(DemoA, 999)).IsNull().Because("before the first freeze end is warmup");
            await Assert.That(source.RoundAt(DemoA, 1000)?.Number).IsEqualTo(1);
            await Assert.That(source.RoundAt(DemoA, 4999)?.Number).IsEqualTo(1)
                .Because("the post-round window belongs to the round that ended");
            await Assert.That(source.RoundAt(DemoA, 5000)?.Number).IsEqualTo(2);
            await Assert.That(source.RoundAt(DemoA, 999_999)?.Number).IsEqualTo(3)
                .Because("the last round runs to the end of the demo");
            await Assert.That(source.RoundAt("/d/no-rows.dem", 1000)).IsNull();
        }
    }

    [Test]
    public async Task Query_CrossesDemos_AndAndsItsFields()
    {
        RoundFactsSource source = new(Store());

        IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> all = source.Query(new RoundFactsFilter());
        IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> ctFull = source.Query(new RoundFactsFilter
        {
            BuyCt = BuyType.Full
        });
        IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> tWinsOnB = source.Query(new RoundFactsFilter
        {
            WinnerSide = 2,
            Demos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DemoB }
        });
        IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> siteB = source.Query(new RoundFactsFilter
        {
            PlantSite = BombSite.B,
            EndReason = RoundEndReason.TerroristsWin
        });
        IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> extra = source.Query(new RoundFactsFilter
        {
            Extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pace"] = "slow"
            },
            LiveOnly = false
        });

        using (Assert.Multiple())
        {
            await Assert.That(all.Count).IsEqualTo(4).Because("live rounds only by default: 2 on A, 2 on B");
            await Assert.That(ctFull.Select(h => (h.Demo.Path, h.Round.Number)))
                .IsEquivalentTo([(DemoA, 2), (DemoB, 2)]);
            await Assert.That(tWinsOnB.Select(h => h.Round.Number)).IsEquivalentTo([1]);
            await Assert.That(siteB.Select(h => (h.Demo.Path, h.Round.Number))).IsEquivalentTo([(DemoA, 2)]);
            await Assert.That(extra.Count).IsEqualTo(5).Because("every round carries the extra column; LiveOnly off admits the unfinished one");
        }
    }

    [Test]
    public async Task FactsFor_IsTheOneLabelList_AbsolutePerSide()
    {
        RoundFactsSource source = new(Store());

        IReadOnlyList<FactLabel> facts = source.FactsFor(DemoA, 2);
        IReadOnlyList<FactLabel> atTick = source.FactsFor(DemoA, 2, 6200);
        Dictionary<string, string> byKey = facts.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        Dictionary<string, string> byKeyAtTick = atTick.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

        using (Assert.Multiple())
        {
            // Correction 10's list, in its spelling.
            await Assert.That(byKey["round"]).IsEqualTo("2");
            await Assert.That(byKey["matchRound"]).IsEqualTo("2");
            await Assert.That(byKey["half"]).IsEqualTo("first");
            await Assert.That(byKey["buy.ct"]).IsEqualTo("full").Because("buy values are the five classes, lower-cased");
            await Assert.That(byKey["buy.t"]).IsEqualTo("eco");
            await Assert.That(byKey["score.ct"]).IsEqualTo("1");
            await Assert.That(byKey["score.t"]).IsEqualTo("0");
            await Assert.That(byKey["winner"]).IsEqualTo("T");
            await Assert.That(byKey["endReason"]).IsEqualTo("TerroristsWin");
            await Assert.That(byKey["plantSite"]).IsEqualTo("b");
            await Assert.That(byKey["plantTick"]).IsEqualTo("6000");
            await Assert.That(byKey["roundTime"]).IsEqualTo("115");
            // The design's per-side facts, in the same shape.
            await Assert.That(byKey["slots.ct"]).IsEqualTo("1,2,3,4,5");
            await Assert.That(byKey["slots.t"]).IsEqualTo("6,7,8,9,10");
            await Assert.That(byKey["equipment.ct"]).IsEqualTo("21000");
            await Assert.That(byKey["money.ct"]).IsEqualTo("3000");
            await Assert.That(byKey.ContainsKey("money.t")).IsFalse().Because("an unreliable money read is not a fact");
            await Assert.That(byKey["moneyReliable.t"]).IsEqualTo("false");
            await Assert.That(byKey["endTick"]).IsEqualTo("8000");
            await Assert.That(byKey["plantSlot"]).IsEqualTo("7");
            await Assert.That(byKey["openingKillTick"]).IsEqualTo("5500");
            await Assert.That(byKey["pace"]).IsEqualTo("slow");
            await Assert.That(facts.Single(f => f.Key == "pace").Group).IsEqualTo("extra");
            // Nothing relative: no us, them, side or opponent.
            await Assert.That(facts.Any(f => f.Key.Contains("us", StringComparison.Ordinal)
                                             || f.Key.Contains("them", StringComparison.Ordinal)
                                             || f.Key == "side")).IsFalse();
            // The tick-anchored group appears only with a tick.
            await Assert.That(byKey.ContainsKey("phase")).IsFalse();
            await Assert.That(byKeyAtTick["phase"]).IsEqualTo("postPlant");
            await Assert.That(byKeyAtTick["manCount.ct"]).IsEqualTo("5");
            await Assert.That(byKeyAtTick["manCount.t"]).IsEqualTo("4");
            await Assert.That(source.FactsFor(DemoA, 99)).IsEmpty();
            await Assert.That(source.FactsFor("/d/no-rows.dem", 1)).IsEmpty();
        }
    }

    [Test]
    public async Task Updated_ForwardsTheEvaluatorsEvent_AfterAWrite()
    {
        DemoCacheStore store = Store();
        RoundFactsTable oneRound = new(
        [
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RoundFactsColumns.RoundNumber] = 1,
                [RoundFactsColumns.Side] = 3,
                [RoundFactsColumns.WinnerSide] = 3
            },
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RoundFactsColumns.RoundNumber] = 1,
                [RoundFactsColumns.Side] = 2,
                [RoundFactsColumns.WinnerSide] = 3
            }
        ], new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, object?>(StringComparer.Ordinal), []);
        RoundFactsEvaluator evaluator = new(store, new FixedSource(oneRound), new FixedIdentity("rf-new"));
        RoundFactsSource source = new(store, evaluator);
        List<string> seen = [];
        source.Updated += seen.Add;

        evaluator.Evaluate("/d/no-rows.dem", SyntheticParsedDemo.Create(
            allGameEvents: [TestSupport.TestGameEvents.RoundFreezeEnd(gameTick: 1000)]));

        using (Assert.Multiple())
        {
            await Assert.That(seen).IsEquivalentTo(["/d/no-rows.dem"]);
            await Assert.That(source.TryGet("/d/no-rows.dem")?.Rounds.Single().WinnerSide).IsEqualTo(3);
        }
    }

    private sealed class FixedIdentity(string value) : IRoundFactsRulesetIdentity
    {
        public string? Fingerprint(int tickRate) => value;
    }

    private sealed class FixedSource(RoundFactsTable table) : IRoundFactsRowSource
    {
        public RoundFactsTable Rows(CS2DemoKit.Parser.ParsedDemo parsed) => table;
    }
}
