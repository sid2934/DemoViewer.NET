#region

using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Result Cards over synthetic hits and rows: the facts on a card and "no data" without rows, the
///     seek offset, the click and the J / K walk in the index's order, the thumbnail cache keys, the
///     placeholder tile that never opens a demo, the shipped playback seam over its delegates, and the
///     tab wiring from a search to the cards and from a rebuild to the cache.
/// </summary>
public class ResultCardTests
{
    private const string DemoA = "/d/a.dem";
    private const string DemoB = "/d/b.dem";

    private static SituationHit Hit(string path, int round, int freezeEnd, int first, string? sha = null) =>
        new(path, DemoCacheStore.StableKey(path), sha, "de_nuke", round, freezeEnd, first, first + 128, 3);

    private static RoundFacts Row(int number, int freezeEnd, int? roundTime = 115)
    {
        RoundFacts round = Round(number, freezeEnd, freezeEnd + 7000);
        round.Ct.ScoreBefore = 7;
        round.T.ScoreBefore = 5;
        round.Ct.BuyType = BuyType.Full;
        round.T.BuyType = BuyType.Eco;
        round.EndReason = RoundEndReason.BombDefused;
        round.RoundTimeSeconds = roundTime;
        return round;
    }

    [Test]
    public async Task ACard_ReadsTheFactsFromTheRows_AndSaysNoDataWithout()
    {
        using Harness h = new();
        h.Cache.Upsert(ParsedRecord(DemoA, facts: Facts(Row(4, 1000))));
        h.Cache.Upsert(ParsedRecord(DemoB));
        h.Cache.UpdateExisting(DemoB, r => r.CtClan = "Alpha");
        h.Cache.UpdateExisting(DemoB, r => r.TClan = "Bravo");

        // 47 s into round 4 at 64 ticks: 1000 + 47 * 64.
        h.Vm.Load([Hit(DemoA, 4, 1000, 4008), Hit(DemoB, 9, 20000, 20640)]);
        await h.Vm.BatchTask;

        ResultCardViewModel withRows = h.Vm.Cards[0];
        ResultCardViewModel without = h.Vm.Cards[1];
        using (Assert.Multiple())
        {
            await Assert.That(withRows.MatchLabel).IsEqualTo("a").Because("no clans on the record: the file name");
            await Assert.That(withRows.RoundLabel).IsEqualTo("Round 4");
            await Assert.That(withRows.ScoreText).IsEqualTo("CT 7 : 5 T");
            await Assert.That(withRows.CtBuyText).IsEqualTo("full");
            await Assert.That(withRows.TBuyText).IsEqualTo("eco");
            await Assert.That(withRows.EndReasonText).IsEqualTo("bomb defused");
            await Assert.That(withRows.EndReasonIconKey).IsEqualTo("ui/defuse");
            await Assert.That(withRows.ElapsedText).IsEqualTo("0:47 in");
            await Assert.That(withRows.RoundClockText).IsEqualTo("1:08 left");
            await Assert.That(withRows.HasFacts).IsTrue();

            await Assert.That(without.MatchLabel).IsEqualTo("Alpha vs Bravo");
            await Assert.That(without.HasFacts).IsTrue().Because("the worker answered: there are no rows");
            await Assert.That(without.ScoreText).IsEqualTo(ResultCardViewModel.NoData);
            await Assert.That(without.CtBuyText).IsEqualTo(ResultCardViewModel.NoData);
            await Assert.That(without.TBuyText).IsEqualTo(ResultCardViewModel.NoData);
            await Assert.That(without.EndReasonText).IsEqualTo(ResultCardViewModel.NoData);
            await Assert.That(without.EndReasonIconKey).IsNull();
            await Assert.That(without.RoundClockText).IsEqualTo(ResultCardViewModel.NoData);
            await Assert.That(without.ElapsedText).IsEqualTo("0:10 in").Because("the hit alone says how far in");
            await Assert.That(h.Vm.HeaderLine).StartsWith("2 rounds · click a card to open it 10 s before the match");
        }
    }

    [Test]
    public async Task TheSeekOffset_IsTenSecondsBeforeTheMatch_InTheDemosTickRate_FlooredAtZero()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ResultCardViewModel.SeekTickFor(4008, 64)).IsEqualTo(3368);
            await Assert.That(ResultCardViewModel.SeekTickFor(4008, 128)).IsEqualTo(2728);
            await Assert.That(ResultCardViewModel.SeekTickFor(100, 64)).IsEqualTo(0);
            await Assert.That(ResultCardViewModel.SeekTickFor(4008, 0)).IsEqualTo(3368).Because("an unknown rate is 64");
        }

        // The record's tick rate reaches the card through the worker.
        using Harness h = new();
        DemoCacheRecord record = ParsedRecord(DemoA);
        record.TickRate = 128;
        h.Cache.Upsert(record);
        h.Vm.Load([Hit(DemoA, 1, 1000, 4008)]);
        await Assert.That(h.Vm.Cards[0].SeekTick).IsEqualTo(3368).Because("64 until the record is read");
        await h.Vm.BatchTask;
        await Assert.That(h.Vm.Cards[0].TickRate).IsEqualTo(128);
        await Assert.That(h.Vm.Cards[0].SeekTick).IsEqualTo(2728);
    }

    [Test]
    public async Task AClick_SeeksPlaybackTenSecondsBefore_AndJK_WalkTheSetInOrder_WithoutWrapping()
    {
        using Harness h = new();
        h.Vm.Load([Hit(DemoA, 1, 1000, 4008), Hit(DemoA, 2, 9000, 9640), Hit(DemoB, 1, 500, 1140)]);
        await h.Vm.BatchTask;

        await Assert.That(h.Vm.Walk(-1)).IsTrue().Because("with nothing selected, either key starts at the first card");
        await Assert.That(h.Vm.SelectedIndex).IsEqualTo(1);
        await Assert.That(h.Vm.Walk(-1)).IsFalse().Because("no card before the first: the key stays unhandled");
        await Assert.That(h.Vm.Walk(+1)).IsTrue();
        await Assert.That(h.Vm.Walk(+1)).IsTrue();
        await Assert.That(h.Vm.Walk(+1)).IsFalse().Because("no wrap at the end");

        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.SelectedIndex).IsEqualTo(3);
            await Assert.That(h.Vm.WalkLine).IsEqualTo("3 of 3");
            await Assert.That(h.Vm.Cards[2].IsSelected).IsTrue();
            await Assert.That(h.Vm.Cards[1].IsSelected).IsFalse();
            await Assert.That(h.Seam.Seeks).IsEquivalentTo([(DemoA, 3368), (DemoA, 9000), (DemoB, 500)]);
        }

        // A click on any card is the same funnel, and makes it the walk's current one.
        await h.Vm.Cards[1].OpenCommand.ExecuteAsync(null);
        await Assert.That(h.Vm.SelectedIndex).IsEqualTo(2);
        await Assert.That(h.Seam.Seeks[^1]).IsEqualTo((DemoA, 9000));

        // The seam could not show the tab: the line says so rather than pretending.
        h.Seam.Answer = false;
        await h.Vm.Cards[0].OpenCommand.ExecuteAsync(null);
        await Assert.That(h.Vm.WalkLine).IsEqualTo("1 of 3 · could not open a.dem");

        h.Vm.Clear();
        await Assert.That(h.Vm.Walk(+1)).IsFalse().Because("no result set");
        await Assert.That(h.Vm.HasCards).IsFalse();
    }

    [Test]
    public async Task ThumbnailKeys_NameTheDemoRoundTickAndFingerprint_AndTheCacheIsBoundedLru()
    {
        SituationThumbnailKey key = new("3f9c", 3, 11066, "ri1;cadence=1;token=1;rf=1;src=pawn;pos=1");
        using (Assert.Multiple())
        {
            await Assert.That(key).IsEqualTo(new SituationThumbnailKey("3f9c", 3, 11066, "ri1;cadence=1;token=1;rf=1;src=pawn;pos=1"));
            await Assert.That(key).IsNotEqualTo(key with { Tick = 11130 });
            await Assert.That(key).IsNotEqualTo(key with { Round = 4 });
            await Assert.That(key).IsNotEqualTo(key with { StableKey = "aaaa" });
            await Assert.That(key).IsNotEqualTo(key with { Fingerprint = "ri1;cadence=1;token=1;rf=1;src=zones;zv=1;pos=1" })
                .Because("a rebuilt index must never show through a picture of the old rows");
        }

        SituationThumbnailCache cache = new(2);
        cache.Put(key, [1]);
        cache.Put(key with { Tick = 1 }, [2]);
        await Assert.That(cache.TryGet(key)!.Single()).IsEqualTo((byte)1).Because("touched: now the most recent");
        cache.Put(key with { Tick = 2 }, [3]);

        using (Assert.Multiple())
        {
            await Assert.That(cache.Count).IsEqualTo(2);
            await Assert.That(cache.TryGet(key with { Tick = 1 })).IsNull().Because("the least recently used went");
            await Assert.That(cache.TryGet(key)).IsNotNull();
            await Assert.That(cache.TryGet(key with { Tick = 2 })).IsNotNull();
        }

        cache.Clear();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ACardWithNoPositionsFile_ShowsThePlaceholder_AndNeverOpensADemo()
    {
        using Harness h = new();
        h.Cache.Upsert(ParsedRecord(DemoA));
        h.Vm.Load([Hit(DemoA, 1, 1000, 4008)]);
        await h.Vm.BatchTask;

        ResultCardViewModel card = h.Vm.Cards[0];
        using (Assert.Multiple())
        {
            await Assert.That(card.HasThumbnail).IsFalse();
            await Assert.That(card.ThumbnailPng).IsNull();
            await Assert.That(card.ThumbnailNote).IsEqualTo(ResultCardsViewModel.NoPositionsNote);
            await Assert.That(card.HasThumbnailNote).IsTrue();
            await Assert.That(h.Seam.Seeks).IsEmpty().Because("nothing was clicked: no demo opens for a picture");
            await Assert.That(h.MapLoads).IsEmpty().Because("with no tuples there is nothing to draw, so not even the bundle is asked for");
            await Assert.That(h.Vm.Cache.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Category("Render")]
    public async Task ACardWithPositions_RendersFromTheTuples_AndTheCacheAnswersTheSecondTime()
    {
        using Harness h = new();
        h.Cache.Upsert(ParsedRecord(DemoA, sha: "abc"));
        RoundIndexBuild build = RoundIndexBuilder.BuildWithPositions(Demo(), Facts(Round(1, 1000, 1400)),
            RoundIndexOptions.Default, PawnPlaceSource.Instance, [.. Everyone(1000, "BombsiteA", "Ramp"), .. Placed(1064)]);
        build.Positions.Demo = new RoundPositionsDemo
        {
            StableKey = DemoCacheStore.StableKey(DemoA),
            Sha256 = "abc"
        };
        h.Sidecars.WritePositions(DemoA, build.Positions);

        h.Vm.Load([Hit(DemoA, 1, 1000, 1064, "abc"), Hit(DemoA, 1, 1000, 1200, "abc")]);
        await h.Vm.BatchTask;

        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Cards[0].ThumbnailPng).IsNotNull();
            await Assert.That(h.Vm.Cards[0].ThumbnailPng!.Length).IsGreaterThan(100);
            await Assert.That(h.Vm.Cards[0].ThumbnailNote).IsEqualTo("").Because("a picture, so no note even without a bitmap decoder");
            await Assert.That(h.Vm.Cards[1].ThumbnailPng).IsNull().Because("step 3 was not sampled");
            await Assert.That(h.Vm.Cards[1].ThumbnailNote).IsEqualTo(ResultCardsViewModel.NoSampleNote);
            await Assert.That(h.MapLoads).IsEquivalentTo(["de_nuke"]).Because("one bundle load per map per batch");
            await Assert.That(h.Vm.Cache.Count).IsEqualTo(1);
            await Assert.That(h.Vm.Cache.TryGet(new SituationThumbnailKey(DemoCacheStore.StableKey(DemoA), 1, 1064,
                h.Sources.FingerprintFor("de_nuke")))).IsNotNull();
        }

        // The same search again: the cache answers, the renderer is not asked.
        int renderers = h.RendererBuilds;
        h.Vm.Load([Hit(DemoA, 1, 1000, 1064, "abc")]);
        await h.Vm.BatchTask;
        await Assert.That(h.Vm.Cards[0].ThumbnailPng).IsNotNull();
        await Assert.That(h.MapLoads.Count).IsEqualTo(1).Because("a cached picture asks for no bundle");
        await Assert.That(h.RendererBuilds).IsEqualTo(renderers + 1).Because("one renderer per batch, cached or not");

        // A stale positions file (another fingerprint) is a placeholder, not a picture of old rows.
        build.Positions.Fingerprint = "ri1;cadence=2;token=1;rf=1;src=pawn;pos=1";
        h.Sidecars.WritePositions(DemoA, build.Positions);
        h.Vm.Cache.Clear();
        h.Vm.Load([Hit(DemoA, 1, 1000, 1064, "abc")]);
        await h.Vm.BatchTask;
        await Assert.That(h.Vm.Cards[0].ThumbnailNote).IsEqualTo(ResultCardsViewModel.NoPositionsNote);
    }

    [Test]
    public async Task TheShippedSeam_SkipsTheOpenForTheLoadedDemo_OpensAnother_AndReportsWhatItCouldNot()
    {
        string? loaded = DemoA;
        List<string> opened = [];
        List<int> seeks = [];
        List<string> tabs = [];
        bool openLands = true;
        bool tabOnStrip = true;
        SituationPlaybackSeek seam = new(() => loaded, path =>
        {
            opened.Add(path);
            if (openLands)
            {
                loaded = path;
            }

            return Task.FromResult(openLands);
        }, seeks.Add, id =>
        {
            tabs.Add(id);
            return tabOnStrip;
        });

        await Assert.That(await seam.SeekAsync(DemoA, 3368)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(opened).IsEmpty().Because("the loaded demo is not re-opened");
            await Assert.That(seeks).IsEquivalentTo([3368]);
            await Assert.That(tabs).IsEquivalentTo([SituationPlaybackSeek.PlaybackTabId]);
        }

        await Assert.That(await seam.SeekAsync(DemoB, 500)).IsTrue();
        await Assert.That(opened).IsEquivalentTo([DemoB]);
        await Assert.That(seeks).IsEquivalentTo([3368, 500]);

        // The open did not land: no seek on whatever is on the clock.
        openLands = false;
        await Assert.That(await seam.SeekAsync(DemoA, 1)).IsFalse();
        await Assert.That(seeks.Count).IsEqualTo(2);

        // The tab is gated off: the seek happened, the answer says the tab could not be shown.
        tabOnStrip = false;
        await Assert.That(await seam.SeekAsync(DemoB, 9)).IsFalse();
        await Assert.That(seeks[^1]).IsEqualTo(9);
    }

    [Test]
    public async Task TheTab_LoadsTheCardsFromASearch_ClearsThemWhenTheQueryMoves_AndDropsTheCacheOnRebuild()
    {
        using Harness h = new();
        RoundIndexDocument document = Document("de_nuke", h.Sources.FingerprintFor("de_nuke"),
            (1, 1000, 2000, [new RoundIndexRun(0, 3, "BombsiteA:5", "Ramp:5")]),
            (2, 3000, 4000, [new RoundIndexRun(0, 2, "Outside:5", "Lobby:5")]));
        Indexed(h.Cache, h.Sidecars, DemoA, document);
        h.Index.Load();
        RoundIndexEvaluator evaluator = new(h.Cache, h.Sidecars, h.Sources, () => true, walk: _ => []);
        using SituationsTabViewModel tab = new(h.Index, evaluator, h.Cache, h.Sources, () => RoundIndexTokenSource.Pawn,
            false, new QueryCanvasViewModel(h.Index, new QueryPlaceResolver(h.Index, h.Sources.Zones), h.Cache, _ => null,
                dispose => dispose()), h.Vm);

        tab.Canvas.Map = "de_nuke";
        tab.Canvas.SearchCommand.Execute(null);
        await tab.Results.BatchTask;

        using (Assert.Multiple())
        {
            await Assert.That(tab.Canvas.ResultCount).IsEqualTo(2);
            await Assert.That(tab.Results.Count).IsEqualTo(2).Because("the count is the card list's length by construction");
            await Assert.That(tab.Results.Cards.Select(c => c.Hit.RoundNumber)).IsEquivalentTo([1, 2]);
        }

        tab.Results.Cache.Put(new SituationThumbnailKey("k", 1, 1, "f"), [1]);
        tab.RebuildIndexCommand.Execute(null);
        await Assert.That(tab.Results.Cache.Count).IsEqualTo(0).Because("every picture was of rows about to be replaced");

        // The map moves: the canvas nulls its count and the cards, which described the old query, go.
        tab.Canvas.Map = "de_dust2";
        await Assert.That(tab.Results.HasCards).IsFalse();
    }

    private static IEnumerable<PositionSample> Placed(int tick)
    {
        foreach (int slot in CtSlots)
        {
            yield return Sample(tick, slot, "BombsiteA", 600 + slot * 20, -400, -416);
        }

        foreach (int slot in TSlots)
        {
            yield return Sample(tick, slot, "Ramp", 1300 + slot * 20, -1000, -700);
        }
    }

    /// <summary>Records every seek and answers as told.</summary>
    internal sealed class RecordingPlayback : ISituationPlayback
    {
        public List<(string Path, int Tick)> Seeks { get; } = [];

        public bool Answer { get; set; } = true;

        public Task<bool> SeekAsync(string demoPath, int tick)
        {
            Seeks.Add((demoPath, tick));
            return Task.FromResult(Answer);
        }
    }

    /// <summary>Cards over an in-memory cache and store, a recording seam, a bundle-less renderer and no bitmap decoder.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Cache = new DemoCacheStore(null);
            Sidecars = new RoundIndexStore(null, Cache);
            Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
            Index = new SituationIndex(Cache, Sidecars, Sources);
            Index.Load();
            Vm = new ResultCardsViewModel(Cache, Sidecars, Sources, () => Seam, new SituationThumbnailCache(),
                () =>
                {
                    RendererBuilds++;
                    return new SituationThumbnailRenderer(map =>
                    {
                        MapLoads.Add(map);
                        return null;
                    });
                },
                action => action(), _ => null);
        }

        public DemoCacheStore Cache { get; }
        public RoundIndexStore Sidecars { get; }
        public RoundIndexPlaceSources Sources { get; }
        public SituationIndex Index { get; }
        public ResultCardsViewModel Vm { get; }
        public RecordingPlayback Seam { get; } = new();
        public List<string> MapLoads { get; } = [];
        public int RendererBuilds { get; private set; }

        public void Dispose()
        {
            Index.Dispose();
            Sidecars.Dispose();
        }
    }
}
