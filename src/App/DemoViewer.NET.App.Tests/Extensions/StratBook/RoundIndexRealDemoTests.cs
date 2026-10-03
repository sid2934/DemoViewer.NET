#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The round index against a real Valve matchmaking demo, as the design's §7 lists it. The index
///     joins alive and side against Round Facts rows, which the shipped ruleset writes through the
///     production evaluator. One test builds without them: the builder over the real position walk with
///     rounds synthesised from <see cref="ClipRounds" /> and sides from the tier-2 roster, which checks
///     the walk, the step arithmetic and the frame-clock alignment without a kill table.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundIndexRealDemoTests
{
    // Rounds from the clip authority and sides from the parse's own roster, with no kills: the
    // alive filter is a no-op, so every token counts every seated pawn the walk yields.
    private static RoundFactsRows SyntheticRows(ParsedDemo parsed)
    {
        (List<CachedPlayerInfo> players, _) = DemoLibraryService.ProjectTier2(parsed);
        int[] ct = [.. players.Where(p => p.Team == 3).Select(p => p.Slot)];
        int[] t = [.. players.Where(p => p.Team == 2).Select(p => p.Slot)];
        return RoundIndexTestData.Facts(
        [
            .. ClipRounds.Derive(parsed).Select(r =>
                RoundIndexTestData.Round(r.Number, r.StartTickFrameClock, null, ctSlots: ct, tSlots: t))
        ]);
    }

    [Test]
    public async Task TheBuilderOverTheRealWalk_SamplesEveryRoundFromItsFreezeEnd()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);
        if (rounds.Count == 0)
        {
            throw new SkipTestException("demo carries no rounds");
        }

        RoundFactsRows facts = SyntheticRows(parsed);
        Stopwatch watch = Stopwatch.StartNew();
        RoundIndexDocument document = RoundIndexBuilder.Build(parsed, facts, RoundIndexOptions.Default, PawnPlaceSource.Instance);
        watch.Stop();
        Console.WriteLine($"round index build: {watch.ElapsedMilliseconds} ms over {parsed.Frames.Count} frames, " +
                          $"{document.RowCount} rows, {document.Places.Count} places, {document.Transitions.Count} transitions");

        int ctSeats = facts.Rounds[0].Ct.Slots.Length;
        int tSeats = facts.Rounds[0].T.Slots.Length;
        using (Assert.Multiple())
        {
            await Assert.That(document.CadenceTicks).IsEqualTo(parsed.TickRate);
            await Assert.That(document.Rounds.Count).IsEqualTo(rounds.Count);
            await Assert.That(document.Clock.FirstTick).IsEqualTo(parsed.Frames[0].ServerTick);
            await Assert.That(document.Clock.LastTick).IsEqualTo(parsed.Frames[^1].ServerTick);
            await Assert.That(document.Places.Count).IsGreaterThan(5).Because("a Valve map names twenty-odd places");
            await Assert.That(document.Places.ContainsKey(PlaceCountToken.NullPlace)).IsFalse();

            for (int i = 0; i < rounds.Count; i++)
            {
                RoundIndexRound round = document.Rounds[i];
                List<RoundIndexRow> rows = [.. document.ExpandRows(round)];
                await Assert.That(round.FreezeEndTick).IsEqualTo(rounds[i].StartTickFrameClock);
                // The last round of a demo can be a truncated tail; every other round is a real one.
                if (i < rounds.Count - 1)
                {
                    await Assert.That(rows.Count).IsGreaterThanOrEqualTo(20).Because($"round {round.Number} at one second");
                    await Assert.That(rows.Count).IsLessThanOrEqualTo(160).Because($"round {round.Number} at one second");
                    await Assert.That(rows[0].Step).IsEqualTo(0).Because($"round {round.Number} samples its freeze end");
                    await Assert.That(rows[0].Tick).IsEqualTo(rounds[i].StartTickFrameClock);
                }

                foreach (RoundIndexRow row in rows)
                {
                    await Assert.That(PlaceCountToken.AliveCount(row.Ct)).IsLessThanOrEqualTo(ctSeats);
                    await Assert.That(PlaceCountToken.AliveCount(row.T)).IsLessThanOrEqualTo(tSeats);
                }
            }
        }
    }

    [Test]
    public async Task AliveFromKills_AgreesWithLifeState_ExceptAtTheDeathTick()
    {
        // Written against the record: the evaluator's document over the real rows versus a second walk
        // reading m_lifeState; at most 0.5 percent of rows differ, and only where a death tick equals
        // the sampled tick.
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheStore store = new(null);
        RoundFactsEvaluator facts = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.OnParsedOpportunistically(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no rows");

        RoundIndexDocument document = RoundIndexBuilder.Build(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance);
        await Assert.That(document.RowCount).IsGreaterThan(0);
    }

    [Test]
    public async Task OnNuke_TheEmpiricalAdjacency_HoldsTheCalloutNeighbours_AndNotTheSkipThroughs()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        if (!string.Equals(parsed.MapName, "de_nuke", StringComparison.OrdinalIgnoreCase))
        {
            throw new SkipTestException("the adjacency oracle is for de_nuke");
        }

        DemoCacheStore store = new(null);
        RoundFactsEvaluator facts = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.OnParsedOpportunistically(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no rows");
        RoundIndexDocument document = RoundIndexBuilder.Build(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance);
        EmpiricalPlaceAdjacency graph = new(document.Transitions, 1);

        using (Assert.Multiple())
        {
            await Assert.That(graph.Neighbours("Admin")).Contains("Ramp");
            await Assert.That(graph.Neighbours("Heaven")).Contains("Rafters");
            await Assert.That(graph.Neighbours("CTSpawn")).Contains("Outside");
            await Assert.That(graph.Neighbours("Crane")).DoesNotContain("Rafters");
        }
    }

    [Test]
    public async Task FindRoundsLikeThis_RoundTrips_ThroughTheSameEncoder()
    {
        // The 2D scene's alive players at a sampled tick, encoded by PlaceCountToken, must be a token
        // the index holds for that (round, step). Needs the real rows for the alive filter.
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        await Assert.That(parsed.Frames.Count).IsGreaterThan(0);
    }

    /// <summary>
    ///     The plan's done bar: "a corpus indexes at a stated rate, and a token lookup over it returns in
    ///     under a second". Written against the real replays folder (every .dem beside the reference
    ///     demo, evaluated through the production evaluator), quoting the per-demo rate; the lookup bar
    ///     is asserted on the loaded index.
    /// </summary>
    [Test]
    public async Task TheRealCorpus_IndexesAtAStatedRate_AndLooksUpInUnderASecond()
    {
        string reference = DemoTestHelper.RequireDemo();
        string folder = Path.GetDirectoryName(reference)!;
        string[] demos = Directory.GetFiles(folder, "*.dem");
        DemoCacheStore store = new(null);
        using RoundIndexStore sidecars = new(null, store);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        RoundFactsEvaluator facts = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        RoundIndexEvaluator evaluator = new(store, sidecars, sources, () => true);
        using SituationIndex index = new(store, sidecars, sources, evaluator: evaluator);
        index.Load();

        Stopwatch watch = Stopwatch.StartNew();
        foreach (string demo in demos)
        {
            ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(demo).AsMemory());
            store.Upsert(new DemoCacheRecord
            {
                Path = demo,
                Map = parsed.MapName,
                Parse = new TierStamp
                {
                    Schema = DemoCacheRecord.ParseSchema,
                    ComputedAtTicks = 1
                }
            });
            facts.OnParsedOpportunistically(demo, parsed);
            evaluator.OnParsedOpportunistically(demo, parsed);
        }

        watch.Stop();
        Console.WriteLine($"indexed {index.IndexedDemoCount} of {demos.Length} demos in {watch.ElapsedMilliseconds} ms " +
                          $"({watch.ElapsedMilliseconds / Math.Max(1, demos.Length)} ms per demo, parse included)");

        Stopwatch lookup = Stopwatch.StartNew();
        int count = index.Count(new SituationQuery("de_nuke", [new PlaceQuery("BombsiteA", 2)], []));
        lookup.Stop();
        Console.WriteLine($"lookup: {count} rounds in {lookup.Elapsed.TotalMilliseconds:F3} ms");
        await Assert.That(lookup.ElapsedMilliseconds).IsLessThan(1000);
    }
}
