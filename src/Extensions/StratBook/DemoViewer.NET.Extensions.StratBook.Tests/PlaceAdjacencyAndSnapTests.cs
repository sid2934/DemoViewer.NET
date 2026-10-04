#region

using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The empirical adjacency graph (threshold, symmetry, the fold across demos) and the place snap
///     (nearest centroid on a band, two bands giving two centroids, the distance cap).
/// </summary>
public class PlaceAdjacencyAndSnapTests
{
    [Test]
    public async Task EmpiricalAdjacency_KeepsPairsAtTheThreshold_Symmetrically()
    {
        EmpiricalPlaceAdjacency graph = new(
        [
            new PlaceTransition("Admin", "Ramp", 62),
            new PlaceTransition("CTSpawn", "Outside", 115),
            new PlaceTransition("Crane", "Rafters", 1),
            new PlaceTransition("Heaven", "Rafters", 3),
            new PlaceTransition("Silo", "Silo", 9)
        ], 18);

        using (Assert.Multiple())
        {
            await Assert.That(graph.Source).IsEqualTo("index:18");
            await Assert.That(graph.Neighbours("Admin")).IsEquivalentTo(["Ramp"]);
            await Assert.That(graph.Neighbours("Ramp")).IsEquivalentTo(["Admin"]).Because("edges are undirected");
            await Assert.That(graph.Neighbours("Rafters")).IsEquivalentTo(["Heaven"]).Because("a skip-through seen once is not an edge; three is");
            await Assert.That(graph.Neighbours("Crane")).IsEmpty();
            await Assert.That(graph.Neighbours("Silo")).IsEmpty().Because("a place is not its own neighbour");
            await Assert.That(graph.Neighbours("Nowhere")).IsEmpty();
        }
    }

    [Test]
    public async Task TheFoldAcrossDemos_IsPlainAddition()
    {
        // Two demos each see a pair twice: neither alone reaches the threshold, together they do.
        Dictionary<(string, string), int> folded = [];
        foreach (PlaceTransition transition in new[]
                 {
                     new PlaceTransition("Lobby", "Outside", 2), new PlaceTransition("Lobby", "Outside", 2)
                 })
        {
            (string, string) key = (transition.A, transition.B);
            folded[key] = folded.GetValueOrDefault(key) + transition.Count;
        }

        EmpiricalPlaceAdjacency graph = new(folded.Select(f => new PlaceTransition(f.Key.Item1, f.Key.Item2, f.Value)), 2);
        await Assert.That(graph.Neighbours("Lobby")).Contains("Outside");
    }

    [Test]
    public async Task Snap_TakesTheNearestCentroidOnTheBand()
    {
        List<PlaceSummary> places =
        [
            new("Ramp", 20,
            [
                new PlaceZBucket(-448, 10, 100, 100), // lower storey
                new PlaceZBucket(64, 10, 900, 900) // upper storey
            ]),
            new("Hell", 5, [new PlaceZBucket(-448, 5, 300, 300)])
        ];

        (string Place, double Distance)? lower = PlaceSnap.Nearest(places, 120, 120, -512, 0);
        (string Place, double Distance)? upper = PlaceSnap.Nearest(places, 880, 880, 0, 512);
        (string Place, double Distance)? nearHell = PlaceSnap.Nearest(places, 280, 280, -512, 0);
        (string Place, double Distance)? tooFar = PlaceSnap.Nearest(places, 5000, 5000, -512, 0);
        (string Place, double Distance)? emptyBand = PlaceSnap.Nearest(places, 120, 120, 1000, 2000);

        using (Assert.Multiple())
        {
            await Assert.That(lower!.Value.Place).IsEqualTo("Ramp");
            await Assert.That(lower.Value.Distance).IsEqualTo(Math.Sqrt(800)).Within(1e-9);
            await Assert.That(upper!.Value.Place).IsEqualTo("Ramp").Because("the same place, the other storey's centroid");
            await Assert.That(upper.Value.Distance).IsEqualTo(Math.Sqrt(800)).Within(1e-9).Because("the upper storey's centroid is (900, 900)");
            await Assert.That(nearHell!.Value.Place).IsEqualTo("Hell");
            await Assert.That(tooFar).IsNull().Because("beyond the 512-unit default");
            await Assert.That(PlaceSnap.Nearest(places, 5000, 5000, -512, 0, maxDistance: 10000)!.Value.Place).IsEqualTo("Hell")
                .Because("with the cap lifted the nearest centroid on the band wins, and (300, 300) is nearer than (100, 100)");
            await Assert.That(emptyBand).IsNull();
        }
    }

    [Test]
    public async Task Snap_FoldsSeveralBucketsInsideOneBand_ByWeight()
    {
        List<PlaceSummary> places =
        [
            new("Outside", 4,
            [
                new PlaceZBucket(-448, 3, 0, 0),
                new PlaceZBucket(-384, 1, 400, 0)
            ])
        ];

        (string Place, double Distance)? hit = PlaceSnap.Nearest(places, 100, 0, -512, 0);

        await Assert.That(hit!.Value.Place).IsEqualTo("Outside");
        await Assert.That(hit.Value.Distance).IsEqualTo(0).Within(1e-9).Because("the weighted centroid is at x = 100");
    }
}
