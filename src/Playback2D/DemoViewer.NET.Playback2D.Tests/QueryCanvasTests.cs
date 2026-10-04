#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Query;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The Query Canvas's Core half: the ten-slot document, the token layer's pane assignment and
///     shapes, the fixture format's round trip, and the catalog's opt-in registration of the layer.
/// </summary>
public class QueryCanvasTests
{
    private static QueryToken Token(QuerySide side, int slot, float x = 0, float y = 0,
        double levelMinZ = 0, string? place = "BombsiteA") => new(side, slot, x, y, levelMinZ, place);

    [Test]
    public async Task TheDocument_HoldsTenSlots_AndBumpsOnEveryChange()
    {
        QueryCanvasDocument document = new();
        int changes = 0;
        document.Changed += () => changes++;

        document.Place(Token(QuerySide.Ct, 0));
        document.Place(Token(QuerySide.T, 4, place: "Ramp"));
        document.Place(Token(QuerySide.Ct, 0, 10, 20)); // a move replaces the slot

        using (Assert.Multiple())
        {
            await Assert.That(document.PlacedCount).IsEqualTo(2);
            await Assert.That(document.Get(QuerySide.Ct, 0)!.Value.WorldX).IsEqualTo(10f);
            await Assert.That(document.IsPlaced(QuerySide.T, 4)).IsTrue();
            await Assert.That(document.IsPlaced(QuerySide.T, 0)).IsFalse();
            await Assert.That(document.Placed.Select(t => (t.Side, t.Slot)).ToArray())
                .IsEquivalentTo([(QuerySide.Ct, 0), (QuerySide.T, 4)]);
            await Assert.That(changes).IsEqualTo(3);
            await Assert.That(document.Version).IsEqualTo(3);
        }

        await Assert.That(document.Lift(QuerySide.Ct, 0)).IsTrue();
        await Assert.That(document.Lift(QuerySide.Ct, 0)).IsFalse().Because("lifting an empty slot is a no-op");
        await Assert.That(changes).IsEqualTo(4);

        document.Clear();
        await Assert.That(document.PlacedCount).IsEqualTo(0);
        document.Clear();
        await Assert.That(changes).IsEqualTo(5).Because("clearing an empty document changes nothing");

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Place(Token(QuerySide.Ct, 5)));
    }

    [Test]
    public async Task ResolvedPlaces_LeaveOutUnresolvedTokens_AndFollowSlotOrder()
    {
        QueryCanvasDocument document = new();
        document.Place(Token(QuerySide.Ct, 3, place: "Outside"));
        document.Place(Token(QuerySide.Ct, 1, place: null));
        document.Place(Token(QuerySide.Ct, 0, place: "BombsiteA"));
        document.Place(Token(QuerySide.T, 0, place: "Ramp"));

        await Assert.That(document.ResolvedPlaces(QuerySide.Ct)).IsEquivalentTo(["BombsiteA", "Outside"]);
        await Assert.That(document.ResolvedPlaces(QuerySide.T)).IsEquivalentTo(["Ramp"]);
        await Assert.That(document.Get(QuerySide.Ct, 1)!.Value.IsResolved).IsFalse();
    }

    [Test]
    public async Task ChangingTheMap_ClearsTheTokens_SinceTheyMeanNothingElsewhere()
    {
        QueryCanvasDocument document = new()
        {
            MapName = "de_nuke"
        };
        document.Place(Token(QuerySide.Ct, 0));

        document.MapName = "de_nuke";
        await Assert.That(document.PlacedCount).IsEqualTo(1).Because("the same map is not a change");

        document.MapName = "de_mirage";
        await Assert.That(document.PlacedCount).IsEqualTo(0);
        await Assert.That(document.MapName).IsEqualTo("de_mirage");
    }

    /// <summary>
    ///     Two nav floors, one token per floor at the same world XY: each token draws on its own pane
    ///     and nowhere else, a resolved one filled in its side's colour and an unresolved one hollow.
    /// </summary>
    [Test]
    [Category("Render")]
    public async Task TheLayer_DrawsEachToken_OnThePaneOfItsLevelKey_FilledWhenResolved()
    {
        QueryCanvasDocument document = new();
        double upperKey = MapSpace.QuantizeZ(-300);
        double lowerKey = MapSpace.QuantizeZ(-1000);
        document.Place(new QueryToken(QuerySide.Ct, 0, 0, 0, upperKey, "BombsiteA"));
        document.Place(new QueryToken(QuerySide.T, 4, 0, 0, lowerKey, "Ramp"));
        document.Place(new QueryToken(QuerySide.T, 1, 900, 900, upperKey, null));

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = new();
        compositor.Add(new QueryTokenLayer(document));
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Size = new SKSizeI(400, 400)
        };
        renderer.Levels.SetAuthoritativeFloors([new FloorSlice(-1000, -300), new FloorSlice(-300, 500)]);

        WorldBounds bounds = new(-2000, -2000, 2000, 2000);
        Scene2DFrame frame = new()
        {
            Map = new SceneMapInfo
            {
                MapName = "synthetic",
                NetworkedBounds = bounds,
                ObservedBounds = bounds
            }
        };
        SceneTime time = new(0, 0, 0, 1 / 64.0, true);

        using SKBitmap bitmap = SKBitmap.Decode(renderer.RenderPng(frame, in time, renderer.Size));
        IReadOnlyList<LevelPaneSnapshot> panes = renderer.LastSubmission.Panes;
        await Assert.That(panes.Count).IsEqualTo(2);

        LevelPaneSnapshot lower = panes.Single(p => p.Level.ZMin < -500);
        LevelPaneSnapshot upper = panes.Single(p => p.Level.ZMin >= -500);
        ScenePalette palette = renderer.Palette;

        // Nine pixels below the centre: inside the disc, clear of the tally strokes and the ring.
        using (Assert.Multiple())
        {
            await Assert.That(Near(Probe(bitmap, upper, 0, 0, 0, 9), palette.TeamCt)).IsTrue()
                .Because("the CT token on the upper key fills the upper pane in the CT colour");
            await Assert.That(Near(Probe(bitmap, lower, 0, 0, 0, 9), palette.TeamT)).IsTrue()
                .Because("the T token on the lower key fills the lower pane in the T colour");
            await Assert.That(Near(Probe(bitmap, upper, 900, 900, 0, 9), palette.Background)).IsTrue()
                .Because("an unresolved token is hollow");
            await Assert.That(Near(Probe(bitmap, upper, 900, 900, QueryTokenLayer.Radius, 0), palette.Background)).IsFalse()
                .Because("but its ring is drawn");
            await Assert.That(Near(Probe(bitmap, lower, 900, 900, 0, 9), palette.Background)).IsTrue()
                .Because("a token keyed to the upper level never reaches the lower pane");
        }
    }

    [Test]
    public async Task TheFixtureStore_RoundTrips_AndReadsAsFarAsItParses()
    {
        QueryCanvasDocument document = new()
        {
            MapName = "de_nuke"
        };
        document.Place(new QueryToken(QuerySide.Ct, 0, 620.5f, -420.25f, -512, "BombsiteA"));
        document.Place(new QueryToken(QuerySide.T, 4, 2600, 900, -512, null));

        using MemoryStream first = new();
        QueryFixtureStore.Write(document, first);
        first.Position = 0;
        QueryCanvasDocument again = QueryFixtureStore.Read(first);

        using (Assert.Multiple())
        {
            await Assert.That(again.MapName).IsEqualTo("de_nuke");
            await Assert.That(again.Placed).IsEquivalentTo(document.Placed);
            await Assert.That(again.Get(QuerySide.T, 4)!.Value.Place).IsNull();
        }

        using MemoryStream second = new();
        QueryFixtureStore.Write(again, second);
        await Assert.That(second.ToArray()).IsEquivalentTo(first.ToArray()).Because("a fixture round-trips byte for byte");
        await Assert.That(System.Text.Encoding.UTF8.GetString(first.ToArray())).DoesNotContain("\r\n");

        const string partial = """
                               {"schemaVersion":"dvquery/1","map":"de_nuke","tokens":[
                                 {"side":"ct","slot":1,"worldX":1,"worldY":2,"levelMinZ":0,"place":"Ramp"},
                                 {"side":"spectator","slot":0,"worldX":0,"worldY":0,"levelMinZ":0},
                                 {"side":"t","slot":9,"worldX":0,"worldY":0,"levelMinZ":0}]}
                               """;
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(partial));
        QueryCanvasDocument tolerant = QueryFixtureStore.Read(stream);
        await Assert.That(tolerant.PlacedCount).IsEqualTo(1).Because("an unknown side and a slot out of range are dropped, not fatal");
        await Assert.That(tolerant.Get(QuerySide.Ct, 1)!.Value.Place).IsEqualTo("Ramp");
    }

    [Test]
    public async Task TheCatalog_RegistersTheQueryLayer_OnlyWhenNamedAndFed()
    {
        await Assert.That(SceneLayerCatalog.SceneStackIds).Contains(SceneLayerIds.Query);
        await Assert.That(SceneLayerIds.OptIn).Contains(SceneLayerIds.Query);

        QueryCanvasDocument document = new();
        using SceneCompositor byDefault = SceneLayerCatalog.CreateSceneStack(query: document);
        using SceneCompositor named = SceneLayerCatalog.CreateSceneStack(["radar", "query"], query: document);
        using SceneCompositor starved = SceneLayerCatalog.CreateSceneStack(["radar", "query"]);

        using (Assert.Multiple())
        {
            await Assert.That(byDefault.Find(SceneLayerIds.Query)).IsNull().Because("opt-in: absent unless named");
            await Assert.That(named.Find(SceneLayerIds.Query)).IsTypeOf<QueryTokenLayer>();
            await Assert.That(((QueryTokenLayer)named.Find(SceneLayerIds.Query)!).Document).IsSameReferenceAs(document);
            await Assert.That(starved.Find(SceneLayerIds.Query)).IsNull().Because("named with nothing to draw is skipped, like the ink");
        }
    }

    private static SKColor Probe(SKBitmap bitmap, LevelPaneSnapshot pane, float worldX, float worldY,
        float dx, float dy)
    {
        (double sx, double sy) = pane.Transform.WorldToScreen(worldX, worldY);
        int x = (int)Math.Round(sx + pane.ViewportRect.Left + dx);
        int y = (int)Math.Round(sy + pane.ViewportRect.Top + dy);
        return bitmap.GetPixel(x, y);
    }

    private static bool Near(SKColor actual, SKColor expected) =>
        Math.Abs(actual.Red - expected.Red) <= 3
        && Math.Abs(actual.Green - expected.Green) <= 3
        && Math.Abs(actual.Blue - expected.Blue) <= 3;
}
