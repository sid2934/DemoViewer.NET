#region

using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tolerance slider on the Query Canvas over a hand-built index: each of the four stops
///     against the fixture's known counts, the live count never falling as the slider loosens (over
///     random arrangements, sliding both ways), the two-stop degraded form on a map with no graph, and
///     the adjacency seam naming zones when the map has them and the empirical graph when it does not.
/// </summary>
public class ToleranceSliderTests
{
    private const string DemoA = "/d/a.dem";
    private const string DemoB = "/d/b.dem";
    private const string DemoC = "/d/c.dem";
    private const string DemoTrain = "/d/train.dem";

    private static readonly string[] _nukePlaces = ["BombsiteA", "Hell", "Ramp", "Outside", "Lobby", "Heaven"];

    [Test]
    public async Task TheFourStops_CountTheFixture_AndTheCountFollowsTheSlider()
    {
        using ToleranceSliderHarness h = new();
        h.PlaceCt("BombsiteA", "BombsiteA", "BombsiteA");

        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.ToleranceStops).IsEquivalentTo(
                [SituationTolerance.Exact, SituationTolerance.Adjacent, SituationTolerance.TwoHops, SituationTolerance.AnyPlace]);
            await Assert.That(h.Vm.ToleranceMaximum).IsEqualTo(3);
            await Assert.That(h.Vm.ToleranceValue).IsEqualTo(0);
        }

        await Assert.That(await h.CountAt(0)).IsEqualTo(1).Because("only A round 2 has exactly three in A");
        await Assert.That(h.Vm.Draft.Tolerance).IsEqualTo(SituationTolerance.Exact);
        await Assert.That(h.Vm.ToleranceLabel).IsEqualTo("exact, exactly N");

        await Assert.That(await h.CountAt(1)).IsEqualTo(2).Because("A round 2 sums five over A and Hell; B has four in A");
        await Assert.That(h.Vm.Draft.Tolerance).IsEqualTo(SituationTolerance.Adjacent);
        await Assert.That(h.Vm.ToleranceLabel).IsEqualTo("adjacent places, at least N");

        await Assert.That(await h.CountAt(2)).IsEqualTo(2).Because("Ramp joins the neighbourhood but no CT stands there");
        await Assert.That(h.Vm.Draft.Tolerance).IsEqualTo(SituationTolerance.TwoHops);
        await Assert.That(h.Vm.ToleranceLabel).IsEqualTo("two hops, at least N");

        await Assert.That(await h.CountAt(3)).IsEqualTo(3).Because("every nuke round has at least three CTs alive");
        await Assert.That(h.Vm.Draft.Tolerance).IsEqualTo(SituationTolerance.AnyPlace);
        await Assert.That(h.Vm.ToleranceLabel).IsEqualTo("any place, at least N alive");

        // A search at the loosest stop returns the cards the button promised, and moving the slider
        // drops that statement, as a token move does.
        h.Vm.SearchCommand.Execute(null);
        await Assert.That(h.Vm.ResultCount).IsEqualTo(3);
        h.Vm.ToleranceValue = 2.4;
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Draft.Tolerance).IsEqualTo(SituationTolerance.TwoHops).Because("a drag between detents lands on the nearest stop");
            await Assert.That(h.Vm.ResultCount).IsNull();
        }

        await h.Vm.Counter.Pending;
        await Assert.That(h.Vm.LiveCount).IsEqualTo(2);
    }

    [Test]
    public async Task TheCount_NeverFalls_AsTheSliderLoosens_OverRandomArrangements()
    {
        using ToleranceSliderHarness h = new();
        Random random = new(20260924);

        for (int trial = 0; trial < 60; trial++)
        {
            h.Vm.Document.Clear();
            h.PlaceCt(RandomPlaces(random));
            h.PlaceT(RandomPlaces(random));

            int[] loosening = new int[h.Vm.ToleranceStops.Count];
            for (int stop = 0; stop < loosening.Length; stop++)
            {
                loosening[stop] = await h.CountAt(stop);
                await Assert.That(loosening[stop]).IsEqualTo(h.Index.Query(h.Vm.Draft.ToQuery()).Count)
                    .Because("the live count is the number of cards a search returns");
                if (stop > 0)
                {
                    await Assert.That(loosening[stop]).IsGreaterThanOrEqualTo(loosening[stop - 1])
                        .Because($"trial {trial}: CT {h.Vm.CtToken}, T {h.Vm.TToken}, stop {stop}");
                }
            }

            // Sliding back tightens through the same numbers.
            for (int stop = loosening.Length - 1; stop >= 0; stop--)
            {
                await Assert.That(await h.CountAt(stop)).IsEqualTo(loosening[stop]);
            }
        }
    }

    [Test]
    public async Task WithoutAGraph_TheSliderHasTwoStops_AndSaysWhy()
    {
        using ToleranceSliderHarness h = new();
        h.Vm.Map = "de_train";

        using (Assert.Multiple())
        {
            await Assert.That(h.Index.Adjacency("de_train")).IsNull().Because("train has a row but no indexed demo");
            await Assert.That(h.Vm.ToleranceStops).IsEquivalentTo([SituationTolerance.Exact, SituationTolerance.AnyPlace]);
            await Assert.That(h.Vm.ToleranceMaximum).IsEqualTo(1);
            await Assert.That(h.Vm.AdjacencySource).IsNull();
            await Assert.That(h.Vm.AdjacencyLine).IsEqualTo("no adjacency graph: exact or any place");
        }

        h.Vm.ToleranceValue = 1;
        await Assert.That(h.Vm.Draft.Tolerance).IsEqualTo(SituationTolerance.AnyPlace).Because("the second stop is the loose end");

        // A tolerance the stops do not offer (a watch saved at Adjacent) sits on Exact, which is what
        // the index runs it as, and the draft keeps it so a re-save does not change the watch.
        h.Vm.Tolerance = SituationTolerance.Adjacent;
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.ToleranceValue).IsEqualTo(0);
            await Assert.That(h.Vm.ToleranceLabel).IsEqualTo("exact, exactly N");
            await Assert.That(h.Vm.Draft.Tolerance).IsEqualTo(SituationTolerance.Adjacent);
        }

        // Back on a map with a graph, the four stops return and the draft's Adjacent is the second one.
        h.Vm.Map = "de_nuke";
        h.Vm.Tolerance = SituationTolerance.Adjacent;
        await Assert.That(h.Vm.ToleranceMaximum).IsEqualTo(3);
        await Assert.That(h.Vm.ToleranceValue).IsEqualTo(1);
    }

    [Test]
    public async Task TheSeam_NamesZones_WhenTheMapHasThem_AndEmpiricalOtherwise()
    {
        RoundIndexBuilderTests.FakeZoneResolver nuke = new("zv-1", _ => null,
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["BombsiteA"] = ["Heaven"],
                ["Heaven"] = ["BombsiteA"]
            });
        using ToleranceSliderHarness zoned = new(new RoundIndexEvaluatorTests.MapZones(("de_nuke", nuke)));
        using ToleranceSliderHarness plain = new();

        using (Assert.Multiple())
        {
            await Assert.That(zoned.Vm.Map).IsEqualTo("de_nuke");
            await Assert.That(zoned.Vm.AdjacencySource).IsEqualTo("zones");
            await Assert.That(zoned.Vm.AdjacencyLine).IsEqualTo("adjacency: zones");
            await Assert.That(zoned.Vm.ToleranceTip).Contains("zones:zv-1");
            await Assert.That(plain.Vm.AdjacencySource).IsEqualTo("empirical");
            await Assert.That(plain.Vm.AdjacencyLine).IsEqualTo("adjacency: empirical");
            await Assert.That(plain.Vm.ToleranceTip).Contains("index:2");
        }

        // The same query widens differently under each graph: the zone graph adds Heaven to A, where
        // nobody stands, while the empirical one adds Hell, where A round 2 has two more CTs. Both
        // counts still sit between exact and any place.
        zoned.PlaceCt("BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA");
        plain.PlaceCt("BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA");
        using (Assert.Multiple())
        {
            await Assert.That(await zoned.CountAt(1)).IsEqualTo(0).Because("no round holds five over A and Heaven");
            await Assert.That(await plain.CountAt(1)).IsEqualTo(1).Because("A round 2 holds three in A and two in Hell");
            await Assert.That(await zoned.CountAt(3)).IsEqualTo(3);
        }

        // A map without zones on the zoned host still gets the empirical graph.
        zoned.Vm.Map = "de_dust2";
        await Assert.That(zoned.Vm.AdjacencySource).IsEqualTo("empirical");
    }

    private static string[] RandomPlaces(Random random)
    {
        int count = random.Next(0, 4);
        return [.. Enumerable.Range(0, count).Select(_ => _nukePlaces[random.Next(_nukePlaces.Length)])];
    }

}
