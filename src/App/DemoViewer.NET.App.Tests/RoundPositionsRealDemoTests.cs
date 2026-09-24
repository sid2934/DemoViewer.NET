#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The positions file against a real Valve matchmaking demo. The consistency property the note
///     names: for every round and step, the token encoded from the positions file's tuples equals the
///     token the sidecar stored at that step. Rounds come from the clip authority and sides from the
///     tier-2 roster, as <see cref="RoundIndexRealDemoTests" /> does, since the engine writes no Round
///     Facts rows until CS2DemoKit #54; the alive rule is then a no-op on both sides and the property
///     holds regardless. The forty-thumbnail budget is measured here too, from the same file.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundPositionsRealDemoTests
{
    private const string WaitingOnEngine =
        "waiting on CS2DemoKit #54: a card's score, buys and end reason come from Round Facts rows, and the engine row source writes none yet";

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

    private static RoundIndexBuild Build()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        if (ClipRounds.Derive(parsed).Count == 0)
        {
            throw new SkipTestException("demo carries no rounds");
        }

        return RoundIndexBuilder.BuildWithPositions(parsed, SyntheticRows(parsed), RoundIndexOptions.Default,
            PawnPlaceSource.Instance);
    }

    [Test]
    public async Task EveryStepsTuples_EncodeToTheTokenTheSidecarStored()
    {
        RoundIndexBuild build = Build();
        RoundPositionsDocument positions = build.Positions;
        byte[] gz = positions.SerializeGzip();
        Console.WriteLine($"positions: {positions.Serialize().Length / 1024} KB json, {gz.Length / 1024} KB gz, " +
                          $"{positions.Rounds.Sum(r => r.Pos.Count)} steps, {positions.Places.Count} places");

        int checkedSteps = 0;
        foreach (RoundIndexRound indexRound in build.Index.Rounds)
        {
            RoundPositionsRound round = positions.Round(indexRound.Number)!;
            HashSet<int> ct = [.. round.Ct];
            await Assert.That(round.FreezeEndTick).IsEqualTo(indexRound.FreezeEndTick);
            foreach (RoundIndexRow row in build.Index.ExpandRows(indexRound))
            {
                IReadOnlyList<RoundPosition> tuples = round.At(row.Step);
                string ctToken = PlaceCountToken.EncodePlaces(tuples.Where(p => ct.Contains(p.Slot)).Select(p => positions.PlaceOf(p.PlaceId)));
                string tToken = PlaceCountToken.EncodePlaces(tuples.Where(p => !ct.Contains(p.Slot)).Select(p => positions.PlaceOf(p.PlaceId)));
                await Assert.That(ctToken).IsEqualTo(row.Ct).Because($"round {row.Step} step {row.Step} CT");
                await Assert.That(tToken).IsEqualTo(row.T).Because($"round {indexRound.Number} step {row.Step} T");
                await Assert.That(positions.StepFor(round, row.Tick)).IsEqualTo(row.Step);
                checkedSteps++;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(checkedSteps).IsEqualTo(build.Index.RowCount);
            await Assert.That(gz.Length).IsLessThan(160 * 1024).Because("measured 56 to 60 KB gzipped on the three demos");
            await Assert.That(RoundPositionsDocument.TryDeserializeGzip(gz)!.Rounds.Count).IsEqualTo(positions.Rounds.Count);
        }
    }

    /// <summary>The corpus budget: forty hits from one real positions file thumbnail on one worker well under the note's bar.</summary>
    [Test]
    public async Task FortyThumbnails_FromTheRealPositionsFile_RenderWellUnderTheBudget()
    {
        RoundIndexBuild build = Build();
        RoundPositionsDocument positions = build.Positions;
        List<(int Round, int Tick)> hits = [];
        foreach (RoundIndexRound round in build.Index.Rounds)
        {
            foreach (RoundIndexRow row in build.Index.ExpandRows(round).Where(r => r.Step % 7 == 3))
            {
                hits.Add((round.Number, row.Tick));
                if (hits.Count == 40)
                {
                    break;
                }
            }

            if (hits.Count == 40)
            {
                break;
            }
        }

        if (hits.Count < 40)
        {
            throw new SkipTestException($"demo yields {hits.Count} sampled steps, fewer than forty");
        }

        using SituationThumbnailRenderer renderer = new(map => MapAssetPipeline.TryLoad(map));
        string map = build.Index.Map;
        renderer.Render(map, positions, hits[0].Round, hits[0].Tick); // warm: the bundle load and the JIT

        Stopwatch watch = Stopwatch.StartNew();
        int rendered = 0;
        foreach ((int round, int tick) in hits)
        {
            if (renderer.Render(map, positions, round, tick) is { Length: > 0 })
            {
                rendered++;
            }
        }

        watch.Stop();
        Console.WriteLine($"forty thumbnails on {map}: {watch.ElapsedMilliseconds} ms, {rendered} rendered");

        using (Assert.Multiple())
        {
            await Assert.That(rendered).IsEqualTo(40);
            await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5))
                .Because("the note measured under 100 ms; the bar is forty results walked in two minutes");
        }
    }

    [Test]
    [Skip(WaitingOnEngine)]
    public Task ACard_ReadsScoreBuysAndEndReason_FromTheRealRows() => Task.CompletedTask;
}
