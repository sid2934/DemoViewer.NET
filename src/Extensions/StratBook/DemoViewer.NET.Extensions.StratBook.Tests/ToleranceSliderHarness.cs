#region

using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The SituationQueryTests fixture: two nuke demos whose transitions make BombsiteA, Hell and Ramp a
///     chain (Outside joins Ramp once both demos fold), one dust2 demo with no transitions, and a parsed
///     but unindexed train row so the picker offers a map with no graph. The canvas counts inline with no
///     debounce. Its own file (used by <c>ZonePlaceResolverSourceTests</c> in App.Tests too), linked into
///     App.Tests so it is not pulled in alongside <c>ToleranceSliderTests</c>'s own tests.
/// </summary>
internal sealed class ToleranceSliderHarness : IDisposable
{
    private const string DemoA = "/d/a.dem";
    private const string DemoB = "/d/b.dem";
    private const string DemoC = "/d/c.dem";
    private const string DemoTrain = "/d/train.dem";

    public ToleranceSliderHarness(IZonePlaceResolverSource? zones = null)
    {
        Cache = new DemoCacheStore(null);
        Sidecars = new RoundIndexStore(Cache.Data());
        Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
        Index = new SituationIndex(Cache, Sidecars, Sources, zones: zones);

        string fingerprint = Sources.FingerprintFor("de_nuke");
        RoundIndexDocument a = Document("de_nuke", fingerprint,
            (1, 10746, 17138,
            [
                new RoundIndexRun(0, 4, "CTSpawn:5", "TSpawn:5"),
                new RoundIndexRun(5, 9, "BombsiteA:2|Outside:3", "Lobby:3|Ramp:2"),
                new RoundIndexRun(10, 12, "BombsiteA:2|Outside:2", "Lobby:3|Ramp:1")
            ]),
            (2, 20000, 24000, [new RoundIndexRun(0, 3, "BombsiteA:3|Hell:2", "Ramp:5")]));
        a.Transitions = [new PlaceTransition("BombsiteA", "Hell", 4), new PlaceTransition("Hell", "Ramp", 3), new PlaceTransition("Outside", "Ramp", 1)];
        RoundIndexDocument b = Document("de_nuke", fingerprint,
            (1, 5000, 9000, [new RoundIndexRun(0, 2, "BombsiteA:4|Outside:1", "Lobby:3|Ramp:2")]));
        b.Transitions = [new PlaceTransition("Outside", "Ramp", 2)];
        RoundIndexDocument c = Document("de_dust2", Sources.FingerprintFor("de_dust2"),
            (1, 3000, 6000, [new RoundIndexRun(0, 1, "BombsiteA:2", "Ramp:3")]));

        Indexed(Cache, Sidecars, DemoA, a, computedAt: 100, modifiedTicks: 30);
        Indexed(Cache, Sidecars, DemoB, b, computedAt: 200, modifiedTicks: 20);
        Indexed(Cache, Sidecars, DemoC, c, computedAt: 300, modifiedTicks: 10);
        Cache.Upsert(ParsedRecord(DemoTrain, "de_train"));
        Index.Load();

        Vm = new QueryCanvasViewModel(Index, new QueryPlaceResolver(Index, zones), Cache, _ => null,
            dispose => dispose(), post: action => action(), countDelay: TimeSpan.Zero);
        Vm.Map = "de_nuke";
    }

    public DemoCacheStore Cache { get; }
    public RoundIndexStore Sidecars { get; }
    public RoundIndexPlaceSources Sources { get; }
    public SituationIndex Index { get; }
    public QueryCanvasViewModel Vm { get; }

    public void Dispose()
    {
        Vm.Dispose();
        Index.Dispose();
    }

    public void PlaceCt(params string[] places) => Place(QuerySide.Ct, places);

    public void PlaceT(params string[] places) => Place(QuerySide.T, places);

    /// <summary>Moves the slider to a stop the way a drag would and returns the live count it settles on.</summary>
    public async Task<int> CountAt(int stop)
    {
        Vm.ToleranceValue = stop;
        await Vm.Counter.Pending;
        return Vm.LiveCount ?? throw new InvalidOperationException("the live count did not settle");
    }

    private void Place(QuerySide side, string[] places)
    {
        for (int slot = 0; slot < places.Length; slot++)
        {
            Vm.Document.Place(new QueryToken(side, slot, 0, 0, 0, places[slot]));
        }
    }
}
