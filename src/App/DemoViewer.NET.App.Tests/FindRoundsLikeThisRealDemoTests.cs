#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Find Rounds Like This round-trip over a real Valve matchmaking demo (round-index design §7):
///     the 2D scene's alive players at a sampled tick encode to the token the index stored for that
///     (round, step). The index joins alive and side against the production Round Facts rows.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class FindRoundsLikeThisRealDemoTests
{
    [Test]
    public async Task TheSceneAtASampledTick_EncodesToTheTokenTheIndexStored()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheStore cache = new(null);
        RoundFactsEvaluator facts = new(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.OnParsedOpportunistically(path, parsed);
        RoundFactsRows rows = cache.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no rows");

        RoundIndexDocument document = RoundIndexBuilder.Build(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance);

        // A run in the middle of a live round with both sides placed: its first step is the tick the
        // scene is built for. The first step of a round is avoided on purpose: the scene decides alive
        // from m_lifeState and the builder from the kill timeline, and the two differ only on a death
        // tick, which a mid-run step is not by construction.
        RoundIndexRound round = document.Rounds.First(r => r.Runs.Count > 1);
        RoundIndexRun run = round.Runs.First(r =>
            r.FromStep > 0 && r.Ct.Length > 0 && !r.Ct.Contains('?') && r.T.Length > 0 && !r.T.Contains('?'));
        int tick = round.FreezeEndTick + run.FromStep * document.CadenceTicks;
        int frameIndex = FrameIndexAtTick(parsed.Frames, tick);
        int tickRate = parsed.TickRate > 0 ? (int)Math.Round((double)parsed.TickRate) : 64;

        // The scene at that tick, through the export pipeline's frame source: the same SceneFrameBuilder
        // the tab's pushes go through, so the markers carry the place the pawn reported.
        using TrackerFrameSource source = new(parsed.Frames, new SceneFrameBuilder(), frameIndex, frameIndex, 60, 1.0,
            tickRate)
        {
            MapName = parsed.MapName
        };
        source.Prepare(CancellationToken.None);
        Scene2DFrame frame = source.FrameAt(0);

        SituationSnapshot snapshot = SituationSnapshot.Capture(parsed.MapName, frame.Time, frame.Markers,
            PawnPlaceSource.Instance);

        await Assert.That(snapshot.TokenFor(QuerySide.Ct)).IsEqualTo(run.Ct);
        await Assert.That(snapshot.TokenFor(QuerySide.T)).IsEqualTo(run.T);
    }

    // The first frame at or past the tick, the way the builder's walk lands on a sampled second.
    private static int FrameIndexAtTick(IReadOnlyList<DemoFrame> frames, int tick)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].ServerTick >= tick)
            {
                return i;
            }
        }

        return frames.Count - 1;
    }
}
