#region

using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Search Filters And Live Count over synthetic rows and hits: every fact field against the index
///     (the tick-anchored ones narrowing the hit's window), our side through the us team, the opponent,
///     date and source fields narrowing the demo set, the rail writing the draft, the count equalling
///     the result set on a fixture, the stated empty result, and the counter's debounce and
///     cancellation. Rows come from a fake source: the engine row source writes none until CS2DemoKit
///     #54, and the real-demo variant is skipped with that reason.
/// </summary>
[NotInParallel]
public class SearchFiltersTests
{
    private const string DemoA = "/d/a.dem";
    private const string DemoB = "/d/b.dem";
    private const string DemoC = "/d/c.dem";
    private const string ValveServer = "Valve Counter-Strike 2 eu_west Server (srcds1234)";

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    // Slots as the record seats them: the T roster first (0 to 4), the CT roster after (5 to 9).
    private static readonly int[] _firstFive = [0, 1, 2, 3, 4];
    private static readonly int[] _secondFive = [5, 6, 7, 8, 9];

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static long Day(int n) => new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local).AddDays(n).Ticks;

    // A round of about a hundred seconds with an opening kill at 500 ticks (a T falls: 5v4), the
    // plant at 1,500 (site A) and a second kill at 3,000 (a CT falls: 4v4). Phases and bands on the
    // 64-tick cadence: Opening steps 0 to 7, MidRound 8 to 23, PostPlant from 24; Early 0 to 29,
    // Middle 30 to 74, Late from 75.
    private static RoundFacts LongRound(int number, int freezeEnd, int[] ctSlots, int[] tSlots, BuyType ctBuy, BuyType tBuy,
        int ctScore, int tScore)
    {
        RoundFacts round = Round(number, freezeEnd, freezeEnd + 6400, ctSlots: ctSlots, tSlots: tSlots, kills:
        [
            new KillStep
            {
                Tick = freezeEnd + 500,
                VictimSlot = tSlots[0],
                VictimSide = 2,
                CtAlive = 5,
                TAlive = 4
            },
            new KillStep
            {
                Tick = freezeEnd + 3000,
                VictimSlot = ctSlots[0],
                VictimSide = 3,
                CtAlive = 4,
                TAlive = 4
            }
        ]);
        round.OpeningKillTick = freezeEnd + 500;
        round.PlantTick = freezeEnd + 1500;
        round.PlantSite = BombSite.A;
        round.Ct.BuyType = ctBuy;
        round.T.BuyType = tBuy;
        round.Ct.ScoreBefore = ctScore;
        round.T.ScoreBefore = tScore;
        return round;
    }

    // A quiet forty-second round: no kill, no plant.
    private static RoundFacts ShortRound(int number, int freezeEnd, int[] ctSlots, int[] tSlots, BuyType ctBuy, BuyType tBuy,
        int ctScore, int tScore)
    {
        RoundFacts round = Round(number, freezeEnd, freezeEnd + 2560, ctSlots: ctSlots, tSlots: tSlots);
        round.Ct.BuyType = ctBuy;
        round.T.BuyType = tBuy;
        round.Ct.ScoreBefore = ctScore;
        round.T.ScoreBefore = tScore;
        return round;
    }

    private static RoundIndexDocument DocA(string fingerprint) =>
        Document("de_nuke", fingerprint,
            (1, 10000, 16400, [new RoundIndexRun(0, 99, "BombsiteA:2|Outside:3", "Lobby:3|Ramp:2")]),
            (2, 20000, 22560, [new RoundIndexRun(0, 39, "Ramp:5", "TSpawn:5")]));

    private static RoundIndexDocument DocB(string fingerprint) =>
        Document("de_nuke", fingerprint, (1, 5000, 11400, [new RoundIndexRun(0, 99, "BombsiteA:2|Outside:3", "Lobby:3|Ramp:2")]));

    private static RoundIndexDocument DocC(string fingerprint) =>
        Document("de_nuke", fingerprint, (1, 3000, 5560, [new RoundIndexRun(0, 39, "BombsiteA:2|Outside:3", "Lobby:3|Ramp:2")]));

    private static SituationQuery Q(RoundFactsFilter? facts = null, IReadOnlySet<string>? demos = null, PlaceQuery[]? ct = null) =>
        new("de_nuke", ct ?? [], [], Facts: facts, Demos: demos);

    private static async Task<IReadOnlyList<SituationHit>> HitsAndCheck(ISituationIndex index, SituationQuery query)
    {
        IReadOnlyList<SituationHit> hits = index.Query(query);
        await Assert.That(index.Count(query)).IsEqualTo(hits.Count).Because("the count is the query with the materialisation skipped");
        return hits;
    }

    private static IEnumerable<(string Demo, int Round)> Keys(IEnumerable<SituationHit> hits) =>
        hits.Select(h => (h.DemoPath, h.RoundNumber)).OrderBy(k => k.DemoPath, StringComparer.Ordinal).ThenBy(k => k.RoundNumber);

    private static List<string> Keys(params string[] paths) =>
        [.. paths.Select(DemoCacheStore.StableKey).Order(StringComparer.Ordinal)];

    private static List<string>? Sorted(IEnumerable<string>? keys) => keys?.Order(StringComparer.Ordinal).ToList();

    [Test]
    public async Task EveryFactField_NarrowsTheHits_AndTheTickAnchoredOnesNarrowTheWindow()
    {
        using Harness h = await Harness.Create();
        ISituationIndex index = h.Index;

        IReadOnlyList<SituationHit> ctFull = await HitsAndCheck(index, Q(new RoundFactsFilter { BuyCt = BuyType.Full }));
        IReadOnlyList<SituationHit> tEco = await HitsAndCheck(index, Q(new RoundFactsFilter { BuyT = BuyType.Eco }));
        IReadOnlyList<SituationHit> both = await HitsAndCheck(index, Q(new RoundFactsFilter { BuyCt = BuyType.Full, BuyT = BuyType.Full }));
        IReadOnlyList<SituationHit> postPlant = await HitsAndCheck(index, Q(new RoundFactsFilter { Phase = RoundPhase.PostPlant }));
        IReadOnlyList<SituationHit> retake = await HitsAndCheck(index, Q(new RoundFactsFilter { Phase = RoundPhase.Retake }));
        IReadOnlyList<SituationHit> opening = await HitsAndCheck(index, Q(new RoundFactsFilter { Phase = RoundPhase.Opening }));
        IReadOnlyList<SituationHit> late = await HitsAndCheck(index, Q(new RoundFactsFilter { ClockBand = ClockBand.Late }));
        IReadOnlyList<SituationHit> early = await HitsAndCheck(index, Q(new RoundFactsFilter { ClockBand = ClockBand.Early }));
        IReadOnlyList<SituationHit> ctUp = await HitsAndCheck(index, Q(new RoundFactsFilter { ManCount = ManCountState.CtUp }));
        IReadOnlyList<SituationHit> even = await HitsAndCheck(index, Q(new RoundFactsFilter { ManCount = ManCountState.Even }));
        IReadOnlyList<SituationHit> tUp = await HitsAndCheck(index, Q(new RoundFactsFilter { ManCount = ManCountState.TUp }));
        IReadOnlyList<SituationHit> tied = await HitsAndCheck(index, Q(new RoundFactsFilter { Score = ScoreSituation.Tied }));
        IReadOnlyList<SituationHit> ctLeading = await HitsAndCheck(index, Q(new RoundFactsFilter { Score = ScoreSituation.CtLeading }));
        IReadOnlyList<SituationHit> matchPoint = await HitsAndCheck(index, Q(new RoundFactsFilter { Score = ScoreSituation.MatchPoint }));
        IReadOnlyList<SituationHit> lateAndUp = await HitsAndCheck(index,
            Q(new RoundFactsFilter { ClockBand = ClockBand.Late, ManCount = ManCountState.CtUp }));

        using (Assert.Multiple())
        {
            await Assert.That(Keys(ctFull)).IsEquivalentTo([(DemoA, 1), (DemoB, 1)]);
            await Assert.That(Keys(tEco)).IsEquivalentTo([(DemoA, 1)]);
            await Assert.That(Keys(both)).IsEquivalentTo([(DemoB, 1)]);

            // Post-plant: the two long rounds, with the window narrowed to the steps from the plant on.
            await Assert.That(Keys(postPlant)).IsEquivalentTo([(DemoA, 1), (DemoB, 1)]);
            await Assert.That(postPlant.Single(x => x.DemoPath == DemoA).FirstMatchTick).IsEqualTo(10000 + 24 * 64)
                .Because("the first sampled step at or after the plant at 1,500 ticks");
            await Assert.That(postPlant.Single(x => x.DemoPath == DemoA).MatchedSteps).IsEqualTo(76);
            await Assert.That(Keys(retake)).IsEquivalentTo(Keys(postPlant)).Because("retake is the same interval seen from the CT side");
            await Assert.That(Keys(opening)).IsEquivalentTo([(DemoA, 1), (DemoA, 2), (DemoB, 1), (DemoC, 1)])
                .Because("every round opens; the quiet rounds never leave the opening");
            await Assert.That(opening.Single(x => x.DemoPath == DemoA && x.RoundNumber == 1).LastMatchTick).IsEqualTo(10000 + 7 * 64);

            // Bands: only the hundred-second rounds reach 1:15; every round has an early window.
            await Assert.That(Keys(late)).IsEquivalentTo([(DemoA, 1), (DemoB, 1)]);
            await Assert.That(late.Single(x => x.DemoPath == DemoA).FirstMatchTick).IsEqualTo(10000 + 75 * 64);
            await Assert.That(Keys(early)).IsEquivalentTo([(DemoA, 1), (DemoA, 2), (DemoB, 1), (DemoC, 1)]);
            await Assert.That(early.Single(x => x.DemoPath == DemoA && x.RoundNumber == 1).MatchedSteps).IsEqualTo(30);

            // Man count: 5v4 between the two kills, even before the first and after the second.
            await Assert.That(Keys(ctUp)).IsEquivalentTo([(DemoA, 1), (DemoB, 1)]);
            await Assert.That(ctUp.Single(x => x.DemoPath == DemoA).FirstMatchTick).IsEqualTo(10000 + 8 * 64);
            await Assert.That(ctUp.Single(x => x.DemoPath == DemoA).LastMatchTick).IsEqualTo(10000 + 46 * 64);
            await Assert.That(Keys(even)).IsEquivalentTo([(DemoA, 1), (DemoA, 2), (DemoB, 1), (DemoC, 1)]);
            await Assert.That(tUp).IsEmpty();

            // Score before the round.
            await Assert.That(Keys(tied)).IsEquivalentTo([(DemoA, 1), (DemoC, 1)]);
            await Assert.That(Keys(ctLeading)).IsEquivalentTo([(DemoA, 2), (DemoB, 1)]);
            await Assert.That(Keys(matchPoint)).IsEquivalentTo([(DemoB, 1)]).Because("12 up under MR12 is one round from the win");

            // Two tick-anchored fields must hold at the same tick: late is after the second kill, so CT up never coincides.
            await Assert.That(lateAndUp).IsEmpty();
        }
    }

    [Test]
    public async Task OurSide_JoinsThroughTheUsTeam_PerRound()
    {
        using Harness h = await Harness.Create();
        SearchFiltersViewModel rail = h.Filters;

        rail.Side.Selected = rail.Side.Options.Single(o => o.Value == 2);
        IReadOnlyList<SituationHit> usT = await HitsAndCheck(h.Index, Q(rail.ToFacts()));
        rail.Side.Selected = rail.Side.Options.Single(o => o.Value == 3);
        IReadOnlyList<SituationHit> usCt = await HitsAndCheck(h.Index, Q(rail.ToFacts()));

        using (Assert.Multiple())
        {
            await Assert.That(rail.HasUs).IsTrue();
            await Assert.That(rail.ShowNoUsNote).IsFalse();
            await Assert.That(Keys(usT)).IsEquivalentTo([(DemoA, 1), (DemoC, 1)]).Because("our five seat the T slots there");
            await Assert.That(Keys(usCt)).IsEquivalentTo([(DemoA, 2), (DemoB, 1)]).Because("A switched sides at its second round");
            await Assert.That(rail.ActiveLine).IsEqualTo("Us on CT");
        }

        // Unsetting us takes the side field with it: the filter has nothing to join through.
        h.Teams.SetUs(null);
        using (Assert.Multiple())
        {
            await Assert.That(rail.HasUs).IsFalse();
            await Assert.That(rail.ShowNoUsNote).IsTrue();
            await Assert.That(rail.Side.IsAny).IsTrue();
            await Assert.That(rail.ToFacts()).IsNull();
        }
    }

    [Test]
    public async Task Opponent_Date_AndSource_NarrowTheDemos_AndIntersect()
    {
        using Harness h = await Harness.Create();
        SearchFiltersViewModel rail = h.Filters;

        await Assert.That(rail.ToDemos()).IsNull().Because("nothing set narrows no demo");
        await Assert.That(rail.Opponent.Options.Select(o => o.Display)).IsEquivalentTo(["Any opponent", "Falcons"])
            .Because("the us team is not an opponent");

        rail.Opponent.Selected = rail.Opponent.Options.Single(o => o.Display == "Falcons");
        IReadOnlySet<string>? against = rail.ToDemos();
        rail.Opponent.Reset();

        rail.Source.Selected = rail.Source.Options.Single(o => o.Value == DemoProvenanceLabel.Official);
        IReadOnlySet<string>? official = rail.ToDemos();
        rail.Source.Selected = rail.Source.Options.Single(o => o.Value == DemoProvenanceLabel.OurScrim);
        IReadOnlySet<string>? ourScrim = rail.ToDemos();
        rail.Source.Selected = rail.Source.Options.Single(o => o.Value == DemoProvenanceLabel.Matchmaking);
        IReadOnlySet<string>? matchmaking = rail.ToDemos();
        rail.Source.Selected = rail.Source.Options.Single(o => o.Value == SearchFiltersViewModel.Unlabeled);
        IReadOnlySet<string>? unlabeled = rail.ToDemos();
        rail.Source.Reset();

        rail.From = new DateTime(Day(3));
        IReadOnlySet<string>? fromDay3 = rail.ToDemos();
        rail.To = new DateTime(Day(5));
        IReadOnlySet<string>? day3To5 = rail.ToDemos();
        rail.From = null;
        IReadOnlySet<string>? toDay5 = rail.ToDemos();

        // Intersection: the official demo (B, day 5) against Falcons up to day 5 is B; from day 9 it is nobody.
        rail.Opponent.Selected = rail.Opponent.Options.Single(o => o.Display == "Falcons");
        rail.Source.Selected = rail.Source.Options.Single(o => o.Value == DemoProvenanceLabel.Official);
        IReadOnlySet<string>? officialFalconsToDay5 = rail.ToDemos();
        rail.From = new DateTime(Day(9));
        rail.To = null;
        IReadOnlySet<string>? officialFalconsFromDay9 = rail.ToDemos();
        string activeLine = rail.ActiveLine;
        rail.Clear();

        using (Assert.Multiple())
        {
            await Assert.That(Sorted(against)).IsEquivalentTo(Keys(DemoA, DemoB)).Because("C was played against strangers");
            await Assert.That(Sorted(official)).IsEquivalentTo(Keys(DemoB)).Because("only B carries both clan tags");
            await Assert.That(Sorted(ourScrim)).IsEquivalentTo(Keys(DemoA)).Because("us against a team the library knows, no tags");
            await Assert.That(Sorted(matchmaking)).IsEquivalentTo(Keys(DemoC)).Because("us against strangers on a Valve server");
            await Assert.That(unlabeled).IsEmpty();
            await Assert.That(Sorted(fromDay3)).IsEquivalentTo(Keys(DemoB, DemoC));
            await Assert.That(Sorted(day3To5)).IsEquivalentTo(Keys(DemoB)).Because("the upper bound is inclusive of its day");
            await Assert.That(Sorted(toDay5)).IsEquivalentTo(Keys(DemoA, DemoB));
            await Assert.That(Sorted(officialFalconsToDay5)).IsEquivalentTo(Keys(DemoB));
            await Assert.That(officialFalconsFromDay9).IsEmpty();
            await Assert.That(activeLine).IsEqualTo("vs Falcons · from 2026-03-10 · official");
            await Assert.That(rail.IsActive).IsFalse().Because("Clear puts every field back to any");
            await Assert.That(rail.ToDemos()).IsNull();
        }

        // The demo set narrows the same hit set the tokens produce, and the count follows it.
        rail.Opponent.Selected = rail.Opponent.Options.Single(o => o.Display == "Falcons");
        IReadOnlyList<SituationHit> hits = await HitsAndCheck(h.Index, Q(demos: rail.ToDemos()));
        await Assert.That(Keys(hits)).IsEquivalentTo([(DemoA, 1), (DemoA, 2), (DemoB, 1)]);
    }

    [Test]
    public async Task TheRail_WritesTheDraft_AndTheCountEqualsTheResultSet()
    {
        using Harness h = await Harness.Create();
        QueryCanvasViewModel canvas = h.Canvas;
        await canvas.Counter.Pending;

        using (Assert.Multiple())
        {
            await Assert.That(canvas.Map).IsEqualTo("de_nuke");
            await Assert.That(canvas.LiveCount).IsEqualTo(4).Because("nothing placed and nothing set counts every indexed round");
            await Assert.That(canvas.SearchLabel).IsEqualTo("Search, 4 rounds");
            await Assert.That(canvas.CoverageLine).IsEqualTo("over 3 of 3 demos");
            await Assert.That(canvas.Draft.Facts).IsNull();
            await Assert.That(canvas.Draft.Demos).IsNull();
        }

        // A fact field writes the draft's filter; the count follows, off the caller's thread.
        h.Filters.BuyCt.Selected = h.Filters.BuyCt.Options.Single(o => o.Value == BuyType.Full);
        await canvas.Counter.Pending;
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Draft.Facts?.BuyCt).IsEqualTo(BuyType.Full);
            await Assert.That(canvas.LiveCount).IsEqualTo(2);
            await Assert.That(canvas.LiveCount).IsEqualTo(h.Index.Query(canvas.Draft.ToQuery()).Count);
            await Assert.That(canvas.SearchLabel).IsEqualTo("Search, 2 rounds");
        }

        // Tokens and a demo field on top: the three narrow one hit set.
        canvas.Document.Place(new QueryToken(QuerySide.Ct, 0, 0, 0, 0, "BombsiteA"));
        canvas.Document.Place(new QueryToken(QuerySide.Ct, 1, 0, 0, 0, "BombsiteA"));
        h.Filters.Source.Selected = h.Filters.Source.Options.Single(o => o.Value == DemoProvenanceLabel.Official);
        await canvas.Counter.Pending;
        int counted = canvas.LiveCount ?? -1;
        canvas.SearchCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.CtToken).IsEqualTo("BombsiteA:2");
            await Assert.That(Sorted(canvas.Draft.Demos)).IsEquivalentTo(Keys(DemoB));
            await Assert.That(counted).IsEqualTo(1).Because("B's round holds two in A, is a CT full buy and is official");
            await Assert.That(canvas.ResultCount).IsEqualTo(counted).Because("the button's number is the number of cards");
            await Assert.That(canvas.SearchLabel).IsEqualTo("Search, 1 round");
            await Assert.That(canvas.ResultLine).IsEqualTo("1 round over 3 indexed demos");
        }

        // A contradiction states its emptiness and what to loosen; the page is never blank.
        h.Filters.BuyT.Selected = h.Filters.BuyT.Options.Single(o => o.Value == BuyType.Pistol);
        await canvas.Counter.Pending;
        canvas.SearchCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.LiveCount).IsEqualTo(0);
            await Assert.That(canvas.SearchLabel).IsEqualTo("Search, 0 rounds");
            await Assert.That(canvas.ResultCount).IsEqualTo(0);
            await Assert.That(canvas.ResultLine).IsEqualTo("no rounds match over 3 indexed demos; clear a filter or lift a token");
        }

        // Clearing the rail clears the draft, and the count comes back to the tokens alone.
        h.Filters.Clear();
        await canvas.Counter.Pending;
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Draft.Facts).IsNull();
            await Assert.That(canvas.Draft.Demos).IsNull();
            await Assert.That(canvas.LiveCount).IsEqualTo(3).Because("A round 1, B round 1 and C round 1 hold two in A");
        }
    }

    [Test]
    public async Task TheCrossDemoQuery_AsksAnyTickOfTheLiveWindow_ForATickAnchoredField()
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
            RoundFacts = Facts(
                LongRound(1, 10000, _secondFive, _firstFive, BuyType.Full, BuyType.Eco, 0, 0),
                ShortRound(2, 20000, _firstFive, _secondFive, BuyType.Eco, BuyType.Full, 1, 0))
        });
        RoundFactsSource source = new(store);

        using (Assert.Multiple())
        {
            await Assert.That(source.Query(new RoundFactsFilter { Phase = RoundPhase.PostPlant }).Select(x => x.Round.Number)).IsEquivalentTo([1]);
            await Assert.That(source.Query(new RoundFactsFilter { ClockBand = ClockBand.Late }).Select(x => x.Round.Number)).IsEquivalentTo([1])
                .Because("a forty-second round never reaches 1:15");
            await Assert.That(source.Query(new RoundFactsFilter { ManCount = ManCountState.CtUp }).Select(x => x.Round.Number)).IsEquivalentTo([1]);
            await Assert.That(source.Query(new RoundFactsFilter { ClockBand = ClockBand.Late, ManCount = ManCountState.CtUp })).IsEmpty()
                .Because("both must hold at one tick, and the second kill evens the round before 1:15");
            await Assert.That(source.Query(new RoundFactsFilter { Phase = RoundPhase.Opening }).Select(x => x.Round.Number)).IsEquivalentTo([1, 2]);
            await Assert.That(source.Query(new RoundFactsFilter { Score = ScoreSituation.CtLeading }).Select(x => x.Round.Number)).IsEquivalentTo([2]);
            await Assert.That(source.Query(new RoundFactsFilter { Where = (_, r) => r.Number == 2 }).Select(x => x.Round.Number)).IsEquivalentTo([2]);
        }
    }

    [Test]
    public async Task TheCounter_FoldsABurstIntoOneCount_ForTheNewestQuery()
    {
        CountingIndex index = new();
        List<(SituationQuery Query, int Count)> counted = [];
        using SituationLiveCount counter = new(index, action => action(), TimeSpan.FromMilliseconds(80));
        counter.Counted += (query, count) => counted.Add((query, count));

        counter.Request(Q(ct: [new PlaceQuery("A", 1)]));
        counter.Request(Q(ct: [new PlaceQuery("A", 1), new PlaceQuery("B", 1)]));
        Task last = counter.Pending;
        counter.Request(Q(ct: [new PlaceQuery("A", 1), new PlaceQuery("B", 1), new PlaceQuery("C", 1)]));
        await counter.Pending;
        await last;

        using (Assert.Multiple())
        {
            await Assert.That(index.Counts).IsEqualTo(1).Because("the first two requests were cancelled inside their delay");
            await Assert.That(counted.Count).IsEqualTo(1);
            await Assert.That(counted[0].Count).IsEqualTo(3).Because("the fake counts the queried pairs");
        }
    }

    [Test]
    public async Task TheCounter_DiscardsAnAnswerThatOutlivedItsRequest()
    {
        CountingIndex index = new()
        {
            Gate = new SemaphoreSlim(0)
        };
        List<(SituationQuery Query, int Count)> counted = [];
        using SituationLiveCount counter = new(index, action => action(), TimeSpan.Zero);
        counter.Counted += (query, count) => counted.Add((query, count));

        // The first count is already inside the index when the second request arrives, so its
        // cancellation cannot stop it; the sequence check must.
        counter.Request(Q(ct: [new PlaceQuery("A", 1)]));
        Task first = counter.Pending;
        await index.Entered.WaitAsync();
        counter.Request(Q(ct: [new PlaceQuery("A", 1), new PlaceQuery("B", 1)]));
        Task second = counter.Pending;
        await index.Entered.WaitAsync();
        index.Gate.Release(2);
        await first;
        await second;

        using (Assert.Multiple())
        {
            await Assert.That(index.Counts).IsEqualTo(2);
            await Assert.That(counted.Count).IsEqualTo(1).Because("the first answer is stale and never shown");
            await Assert.That(counted[0].Count).IsEqualTo(2);
        }

        // Cancel drops a pending request outright.
        counter.Request(Q(ct: [new PlaceQuery("A", 1)]));
        Task third = counter.Pending;
        await index.Entered.WaitAsync();
        counter.Cancel();
        index.Gate.Release();
        await third;
        await Assert.That(counted.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     Three indexed nuke demos with rows, a us team, an opponent the library knows and three
    ///     provenance labels. A: us on T in round 1 and on CT in round 2, against Falcons, tagless on a
    ///     Valve server (our scrim), day 1. B: us on CT against Falcons with both clan tags (official),
    ///     day 5, at match point. C: us on T against strangers on a Valve server (matchmaking), day 9.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private Harness()
        {
            Cache = new DemoCacheStore(null);
            Sidecars = new RoundIndexStore(null, Cache);
            Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
            Facts = new FakeFacts();
            Teams = new TeamIdentityService(null, Cache, Facts, run: _inline);
            Provenance = new DemoProvenanceSource(Cache, Teams);
            Index = new SituationIndex(Cache, Sidecars, Sources, Facts);
        }

        public DemoCacheStore Cache { get; }
        public RoundIndexStore Sidecars { get; }
        public RoundIndexPlaceSources Sources { get; }
        public FakeFacts Facts { get; }
        public TeamIdentityService Teams { get; }
        public DemoProvenanceSource Provenance { get; }
        public SituationIndex Index { get; }
        public SearchFiltersViewModel Filters { get; private set; } = null!;
        public QueryCanvasViewModel Canvas { get; private set; } = null!;

        public static async Task<Harness> Create()
        {
            Harness h = new();
            string[] us = Ids(1, 2, 3, 4, 5);
            string[] falcons = Ids(11, 12, 13, 14, 15);
            string[] strangers = Ids(21, 22, 23, 24, 25);
            string fingerprint = h.Sources.FingerprintFor("de_nuke");

            h.Facts.Rows[DemoA] = RoundIndexTestData.Facts(
                LongRound(1, 10000, _secondFive, _firstFive, BuyType.Full, BuyType.Eco, 0, 0),
                ShortRound(2, 20000, _firstFive, _secondFive, BuyType.Eco, BuyType.Full, 1, 0));
            h.Facts.Rows[DemoB] = RoundIndexTestData.Facts(
                LongRound(1, 5000, _secondFive, _firstFive, BuyType.Full, BuyType.Full, 12, 3));
            h.Facts.Rows[DemoC] = RoundIndexTestData.Facts(
                ShortRound(1, 3000, _secondFive, _firstFive, BuyType.Semi, BuyType.Force, 0, 0));

            await h.Teams.StartAsync();
            using (h.Cache.BeginBatch())
            {
                h.Indexed(DemoA, 1, t: us, ct: falcons, DocA(fingerprint));
                h.Indexed(DemoB, 5, t: falcons, ct: us, DocB(fingerprint), tClan: "FLC", ctClan: "US");
                h.Indexed(DemoC, 9, t: us, ct: strangers, DocC(fingerprint));
            }

            await h.Teams.Idle;
            h.Index.Load();
            h.Teams.SetUs(h.Teams.TeamOnSide(DemoA, 2)!.Id);
            h.Teams.Rename(h.Teams.TeamOnSide(DemoA, 3)!.Id, "Falcons");

            h.Filters = new SearchFiltersViewModel(h.Cache, h.Teams, h.Provenance);
            h.Canvas = new QueryCanvasViewModel(h.Index, new QueryPlaceResolver(h.Index, h.Sources.Zones), h.Cache, _ => null,
                dispose => dispose(), h.Filters, action => action(), TimeSpan.Zero);
            return h;
        }

        // A parsed record with a seated roster (T first, then CT), stamped Indexed over a written sidecar.
        private void Indexed(string path, int day, string[] t, string[] ct, RoundIndexDocument document,
            string? tClan = null, string? ctClan = null)
        {
            Sidecars.Write(path, document);
            DemoCacheRecord record = new()
            {
                Path = path,
                Size = 1000,
                ModifiedTicks = Day(day),
                Map = "de_nuke",
                Server = ValveServer,
                TClan = tClan,
                CtClan = ctClan
            };
            int slot = 0;
            foreach (string id in t)
            {
                record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
            }

            foreach (string id in ct)
            {
                record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
            }

            DemoCacheStore.StampParse(record);
            record.RoundFactsFingerprint = "rf-A";
            record.RoundIndex = new TierStamp
            {
                Schema = DemoCacheRecord.RoundIndexSchema,
                ComputedAtTicks = 100
            };
            record.RoundIndexState = RoundIndexState.Indexed;
            record.RoundIndexFingerprint = document.Fingerprint;
            record.RoundIndexRowCount = document.RowCount;
            Cache.Upsert(record);
        }

        public void Dispose()
        {
            Canvas.Dispose();
            Index.Dispose();
            Provenance.Dispose();
            Teams.Dispose();
            Sidecars.Dispose();
        }
    }

    private sealed class FakeFacts : IRoundFactsSource
    {
        public Dictionary<string, RoundFactsRows> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Schema => DemoCacheRecord.RoundFactsSchema;

        public event Action<string>? Updated
        {
            add { }
            remove { }
        }

        public RoundFactsRows? TryGet(string demoPath) => Rows.GetValueOrDefault(demoPath);

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
            TryGet(demoPath) is { } rows ? RoundFactsSource.FindRound(rows.Rounds, frameClockTick) : null;

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];
    }

    /// <summary>An index whose count is the number of CT pairs queried, optionally held at a gate so a test can order two runs.</summary>
    private sealed class CountingIndex : ISituationIndex
    {
        private int _counts;

        public SemaphoreSlim? Gate { get; init; }

        public SemaphoreSlim Entered { get; } = new(0);

        public int Counts => Volatile.Read(ref _counts);

        public bool IsReady => true;

        public int IndexedDemoCount => 0;

        public int StaleDemoCount => 0;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public event Action<RoundIndexedEvent>? Indexed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<SituationHit> Query(SituationQuery query) => [];

        public int Count(SituationQuery query)
        {
            Interlocked.Increment(ref _counts);
            Entered.Release();
            Gate?.Wait();
            return query.Ct.Count;
        }

        public IReadOnlyList<PlaceSummary> Places(string map) => [];

        public IPlaceAdjacency? Adjacency(string map) => null;
    }
}
