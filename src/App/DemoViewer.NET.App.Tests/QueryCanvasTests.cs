#region

using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;
using SkiaSharp;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Query Canvas over a hand-built index: the done bar (a placed query encodes to the token the
///     index stores, and the index counts it), partial queries, an unresolved drop, the zones path
///     winning over the snap, the tool's gestures, the rail, and the stacked-map grab rule.
/// </summary>
public class QueryCanvasTests
{
    private const string Demo = "/d/a.dem";
    private const string CtToken = "BombsiteA:2|Outside:3";
    private const string TToken = "Lobby:3|Ramp:2";

    // Nuke-shaped centroids on one Z bucket, far enough apart that no drop is ambiguous at the
    // 512-unit snap radius.
    private static readonly Dictionary<string, (double X, double Y)> _centroids = new(StringComparer.Ordinal)
    {
        ["BombsiteA"] = (600, -400),
        ["Outside"] = (-1300, -900),
        ["Lobby"] = (-900, 200),
        ["Ramp"] = (1300, -1000)
    };

    private static readonly WorldBounds _bounds = new(-3453, -4281, 3715, 2887);

    [Test]
    public async Task APlacedQuery_EncodesToTheTokenTheIndexStores_AndTheIndexCountsIt()
    {
        using Harness h = new();

        h.Drop(QuerySide.Ct, 0, "BombsiteA");
        h.Drop(QuerySide.Ct, 1, "BombsiteA");
        h.Drop(QuerySide.Ct, 2, "Outside");
        h.Drop(QuerySide.Ct, 3, "Outside");
        h.Drop(QuerySide.Ct, 4, "Outside");
        h.Drop(QuerySide.T, 0, "Ramp");
        h.Drop(QuerySide.T, 1, "Lobby");
        h.Drop(QuerySide.T, 2, "Lobby");
        h.Drop(QuerySide.T, 3, "Ramp");
        h.Drop(QuerySide.T, 4, "Lobby");

        RoundIndexRun stored = h.Sidecars.TryRead(Demo)!.Rounds[0].Runs[0];
        SituationQuery query = h.Vm.Draft.ToQuery();

        using (Assert.Multiple())
        {
            // The done bar: one function applied twice.
            await Assert.That(h.Vm.CtToken).IsEqualTo(stored.Ct);
            await Assert.That(h.Vm.TToken).IsEqualTo(stored.T);
            await Assert.That(h.Vm.CtToken).IsEqualTo(CtToken);
            await Assert.That(PlaceCountToken.Encode(query.Ct.Select(p => ((string?)p.Place, p.Count)))).IsEqualTo(stored.Ct);
            await Assert.That(query.Map).IsEqualTo("de_nuke");
            await Assert.That(query.Tolerance).IsEqualTo(SituationTolerance.Exact);
            await Assert.That(h.Index.Count(query)).IsEqualTo(1);
        }

        h.Vm.SearchCommand.Execute(null);
        await Assert.That(h.Vm.ResultCount).IsEqualTo(1);
        await Assert.That(h.Vm.ResultLine).IsEqualTo("1 round over 1 indexed demos");

        // A token that moves invalidates the count: the number beside a new query must not be an
        // answer to the old one.
        h.Drop(QuerySide.Ct, 4, "Lobby");
        await Assert.That(h.Vm.ResultCount).IsNull();
        await Assert.That(h.Vm.CtToken).IsEqualTo("BombsiteA:2|Lobby:1|Outside:2");
        await Assert.That(h.Index.Count(h.Vm.Draft.ToQuery())).IsEqualTo(0);
    }

    [Test]
    public async Task PartialQueries_AreTheNormalCase()
    {
        using Harness h = new();

        await Assert.That(h.Vm.Draft.IsEmpty).IsTrue();
        h.Vm.SearchCommand.Execute(null);
        await Assert.That(h.Vm.ResultCount).IsEqualTo(1).Because("nothing placed matches every indexed round of the map");
        await Assert.That(h.Vm.ResultLine).Contains("nothing placed");

        h.Drop(QuerySide.T, 0, "Ramp");
        SituationQuery query = h.Vm.Draft.ToQuery();

        using (Assert.Multiple())
        {
            await Assert.That(query.Ct).IsEmpty().Because("an empty side is unconstrained");
            await Assert.That(query.T).IsEquivalentTo([new PlaceQuery("Ramp", 1)]);
            await Assert.That(h.Vm.CtToken).IsEqualTo("");
            await Assert.That(h.Vm.TToken).IsEqualTo("Ramp:1");
            await Assert.That(h.Index.Count(query)).IsEqualTo(0).Because("exact means exactly one on Ramp, and the row holds two");
        }

        h.Drop(QuerySide.T, 1, "Ramp");
        await Assert.That(h.Index.Count(h.Vm.Draft.ToQuery())).IsEqualTo(1);
    }

    [Test]
    public async Task ADropWithNothingNear_PlacesAnUnresolvedToken_ThatStaysOutOfTheQuery()
    {
        using Harness h = new();

        h.Arm(QuerySide.Ct, 0);
        h.Press(3000, 2500);
        h.Release(3000, 2500);

        QueryRailSlotViewModel slot = h.Vm.Slots.Single(s => s.Side == QuerySide.Ct && s.Slot == 0);
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Document.IsPlaced(QuerySide.Ct, 0)).IsTrue();
            await Assert.That(h.Vm.Document.Get(QuerySide.Ct, 0)!.Value.IsResolved).IsFalse();
            await Assert.That(h.Vm.CtToken).IsEqualTo("");
            await Assert.That(h.Vm.Draft.IsEmpty).IsTrue();
            await Assert.That(slot.IsPlaced).IsTrue();
            await Assert.That(slot.Status).IsEqualTo("no place");
            await Assert.That(h.Vm.HintLine).Contains("no place near");
            await Assert.That(h.Tool.LastHit).IsNull();
        }
    }

    [Test]
    public async Task TheZonesPath_WinsWhenTheMapHasZones_AndGetsTheFloorKey()
    {
        using Harness h = new();
        List<System.Numerics.Vector3> asked = [];
        RoundIndexBuilderTests.FakeZoneResolver nuke = new("zv-7", v =>
        {
            asked.Add(v);
            return "Custom";
        });
        QueryPlaceResolver withZones = new(h.Index, new RoundIndexEvaluatorTests.MapZones(("de_nuke", nuke)));
        QueryPlaceResolver withoutZones = new(h.Index);

        (double x, double y) = _centroids["BombsiteA"];
        QueryPlaceHit? zoned = withZones.Resolve("de_nuke", x, y, -528, 100000);
        QueryPlaceHit? snapped = withoutZones.Resolve("de_nuke", x + 30, y - 30, -100000, 100000);

        using (Assert.Multiple())
        {
            await Assert.That(zoned).IsEqualTo(new QueryPlaceHit("Custom", "zones:zv-7"));
            await Assert.That(asked.Single().Z).IsEqualTo((float)MapSpace.QuantizeZ(-528))
                .Because("a floor click resolves on the quantized floor key, the key a world-anchored stroke stores");
            await Assert.That(snapped).IsEqualTo(new QueryPlaceHit("BombsiteA", "index"));
            await Assert.That(withoutZones.Resolve("de_nuke", x, y, 0, 100000)).IsNull()
                .Because("the band above the samples' bucket folds nothing");
            await Assert.That(withoutZones.Resolve("de_mirage", x, y, -100000, 100000)).IsNull()
                .Because("a map with no indexed demo has no centroids");
        }
    }

    [Test]
    public async Task TheTool_MovesReResolvesLiftsAndCancels()
    {
        using Harness h = new();
        h.Drop(QuerySide.Ct, 0, "BombsiteA");

        // Nothing armed and nothing under the pointer: refused, so the host can hand the drag to pan.
        await Assert.That(h.Press(0, 2000)).IsFalse();

        // An armed slot places even on top of a token: the rail click said what the press means.
        h.Arm(QuerySide.Ct, 1);
        (double bx, double by) = _centroids["BombsiteA"];
        h.Press(bx, by);
        h.Release(bx, by);
        await Assert.That(h.Vm.CtToken).IsEqualTo("BombsiteA:2");
        h.Vm.Lift(h.Vm.Slots.Single(s => s.Side == QuerySide.Ct && s.Slot == 1));

        // Grab the token, carry it to Outside: the place is decided at the release, and the disc
        // goes hollow on the way.
        (double ax, double ay) = _centroids["BombsiteA"];
        (double ox, double oy) = _centroids["Outside"];
        await Assert.That(h.Press(ax + 5, ay)).IsTrue();
        h.Move(0, 0);
        await Assert.That(h.Vm.Document.Get(QuerySide.Ct, 0)!.Value.IsResolved).IsFalse();
        await Assert.That(h.Tool.Dragging).IsEqualTo((QuerySide.Ct, 0));
        h.Release(ox, oy);
        await Assert.That(h.Vm.CtToken).IsEqualTo("Outside:1");
        await Assert.That(h.Tool.LastHit!.Source).IsEqualTo("index");

        // Cancel mid-drag puts it back where the press found it.
        h.Press(ox, oy);
        h.Move(ax, ay);
        h.Tool.OnCancelled(h.Services);
        await Assert.That(h.Vm.Document.Get(QuerySide.Ct, 0)!.Value.Place).IsEqualTo("Outside");
        await Assert.That(h.Vm.Document.Get(QuerySide.Ct, 0)!.Value.WorldX).IsEqualTo((float)ox);

        // Cancel during a placement that never finished leaves the slot empty.
        h.Arm(QuerySide.T, 2);
        h.Press(ax, ay);
        h.Tool.OnCancelled(h.Services);
        await Assert.That(h.Vm.Document.IsPlaced(QuerySide.T, 2)).IsFalse();
        await Assert.That(h.Tool.Armed).IsNull();

        // A release over no band sends the token back to the rail.
        h.Press(ox, oy);
        h.Tool.OnReleased(h.At(null, 0, 0, ToolPointerButton.Left), h.Services);
        await Assert.That(h.Vm.Document.IsPlaced(QuerySide.Ct, 0)).IsFalse();

        // Right-press on a token lifts it in one gesture; right-press on empty map is refused.
        h.Drop(QuerySide.T, 0, "Ramp");
        (double rx, double ry) = _centroids["Ramp"];
        await Assert.That(h.Press(rx, ry, ToolPointerButton.Right)).IsTrue();
        await Assert.That(h.Vm.Document.IsPlaced(QuerySide.T, 0)).IsFalse();
        await Assert.That(h.Press(rx, ry, ToolPointerButton.Right)).IsFalse();
    }

    [Test]
    public async Task TheRail_ArmsPlacesAndLifts_AndFollowsTheDocument()
    {
        using Harness h = new();
        QueryRailSlotViewModel ct1 = h.Vm.Slots.Single(s => s.Side == QuerySide.Ct && s.Slot == 0);
        QueryRailSlotViewModel t5 = h.Vm.Slots.Single(s => s.Side == QuerySide.T && s.Slot == 4);

        await Assert.That(h.Vm.Slots.Count).IsEqualTo(10);
        await Assert.That(h.Vm.CtSlots.Count()).IsEqualTo(5);
        await Assert.That(t5.Label).IsEqualTo("T 5");
        await Assert.That(h.Vm.Map).IsEqualTo("de_nuke").Because("the one map the rows name is picked");

        ct1.ArmCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(ct1.IsArmed).IsTrue();
            await Assert.That(ct1.Status).IsEqualTo("click the map");
            await Assert.That(h.Tool.Armed).IsEqualTo((QuerySide.Ct, 0));
            await Assert.That(h.Vm.HintLine).Contains("CT 1");
        }

        t5.ArmCommand.Execute(null);
        await Assert.That(ct1.IsArmed).IsFalse().Because("one slot is armed at a time");
        await Assert.That(h.Tool.Armed).IsEqualTo((QuerySide.T, 4));

        t5.ArmCommand.Execute(null);
        await Assert.That(h.Tool.Armed).IsNull().Because("arming the armed slot disarms it");

        h.Drop(QuerySide.Ct, 0, "BombsiteA");
        using (Assert.Multiple())
        {
            await Assert.That(ct1.IsPlaced).IsTrue();
            await Assert.That(ct1.Place).IsEqualTo("BombsiteA");
            await Assert.That(ct1.Status).IsEqualTo("BombsiteA");
            await Assert.That(ct1.IsArmed).IsFalse();
            await Assert.That(h.Vm.ArmedSlot).IsNull();
            await Assert.That(h.Vm.HintLine).IsEqualTo("resolved to BombsiteA (index)");
            await Assert.That(ct1.LiftCommand.CanExecute(null)).IsTrue();
        }

        ct1.LiftCommand.Execute(null);
        await Assert.That(ct1.IsPlaced).IsFalse();
        await Assert.That(h.Vm.CtToken).IsEqualTo("");

        h.Drop(QuerySide.Ct, 0, "BombsiteA");
        h.Drop(QuerySide.T, 0, "Ramp");
        h.Vm.ClearTokensCommand.Execute(null);
        await Assert.That(h.Vm.Document.PlacedCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnAStackedMap_ATokenIsGrabbedOnlyThroughItsOwnPane()
    {
        using Harness h = new(floors: [new FloorSlice(-100000, -528), new FloorSlice(-528, 100000)]);
        await Assert.That(h.Panes.Panes.Count).IsEqualTo(2);
        LevelPane lower = h.Panes.Panes.Single(p => p.Level.ZMin < -528);
        LevelPane upper = h.Panes.Panes.Single(p => p.Level.ZMin >= -528);

        h.Arm(QuerySide.T, 0);
        h.Tool.OnPressed(h.At(lower, 0, 0, ToolPointerButton.Left), h.Services);
        h.Tool.OnReleased(h.At(lower, 0, 0, ToolPointerButton.Left), h.Services);
        QueryToken token = h.Vm.Document.Get(QuerySide.T, 0)!.Value;

        using (Assert.Multiple())
        {
            await Assert.That(token.LevelMinZ).IsEqualTo(MapSpace.QuantizeZ(lower.Level.ZMin));
            await Assert.That(h.Tool.OnPressed(h.At(upper, 0, 0, ToolPointerButton.Left), h.Services)).IsFalse()
                .Because("the same world XY on the other storey is empty map");
            await Assert.That(h.Tool.OnPressed(h.At(lower, 0, 0, ToolPointerButton.Left), h.Services)).IsTrue();
        }

        h.Tool.OnCancelled(h.Services);
    }

    [Test]
    public async Task ChangingTheMap_ClearsTheTokens_AndTheTabBuildsTheCanvasOverItsIndex()
    {
        using Harness h = new();
        h.Drop(QuerySide.Ct, 0, "BombsiteA");
        h.Vm.SearchCommand.Execute(null);
        int mapChanges = 0;
        h.Vm.MapChanged += () => mapChanges++;

        h.Vm.Map = "de_mirage";
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Document.MapName).IsEqualTo("de_mirage");
            await Assert.That(h.Vm.Document.PlacedCount).IsEqualTo(0);
            await Assert.That(h.Vm.ResultCount).IsNull();
            await Assert.That(h.Vm.IsMissingBundle).IsTrue().Because("the loader answers no bundle");
            await Assert.That(h.Vm.MissingBundleNote).Contains("de_mirage");
            await Assert.That(mapChanges).IsEqualTo(1);
        }

        using SituationsTabViewModel tab = new(h.Index, null, h.Cache, h.Sources, () => RoundIndexTokenSource.Pawn, isBrowser: false);
        await Assert.That(tab.Canvas.Maps).IsEquivalentTo(["de_nuke"]);
        await Assert.That(tab.Canvas.CanSearch).IsTrue();
    }

    /// <summary>
    ///     One indexed demo, one round, one run holding <see cref="CtToken" /> and <see cref="TToken" />,
    ///     with the four places' centroids on a single Z bucket, a real pane set over the level model,
    ///     and the tool driven straight from world points.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public Harness(IReadOnlyList<FloorSlice>? floors = null)
        {
            Cache = new DemoCacheStore(null);
            Sidecars = new RoundIndexStore(null, Cache);
            Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
            Index = new SituationIndex(Cache, Sidecars, Sources);

            RoundIndexDocument document = Document("de_nuke", Sources.FingerprintFor("de_nuke"),
                (1, 10746, 17138, [new RoundIndexRun(0, 4, CtToken, TToken)]));
            foreach ((string place, (double x, double y)) in _centroids)
            {
                document.Places[place] = new PlaceSampleSummary
                {
                    Count = 4,
                    Buckets = [new PlaceZBucketSum(-416, 4, x * 4, y * 4)]
                };
            }

            Indexed(Cache, Sidecars, Demo, document);
            Index.Load();

            Vm = new QueryCanvasViewModel(Index, new QueryPlaceResolver(Index, Sources.Zones), Cache, _ => null,
                dispose => dispose());
            Tool = Vm.Tool;

            MapSpaceFactory levels = new();
            levels.SetAuthoritativeFloors(floors ?? [new FloorSlice(-100000, 100000)]);
            levels.Update(Scene2DFrame.Empty);
            Panes = new PaneSet(new StackedLayout());
            Panes.Reconcile(levels.Space, LevelDisplayMode.Stacked, new SKSize(640, 640), _bounds);
            Services = new Services_(Panes);
        }

        public DemoCacheStore Cache { get; }
        public RoundIndexStore Sidecars { get; }
        public RoundIndexPlaceSources Sources { get; }
        public SituationIndex Index { get; }
        public QueryCanvasViewModel Vm { get; }
        public QueryTokenTool Tool { get; }
        public PaneSet Panes { get; }
        public IToolServices Services { get; }

        public void Dispose()
        {
            Vm.Dispose();
            Index.Dispose();
            Sidecars.Dispose();
        }

        public void Arm(QuerySide side, int slot) => Vm.Arm(Vm.Slots.Single(s => s.Side == side && s.Slot == slot));

        /// <summary>Arms a slot and drops it on a place's centroid, the way a user would.</summary>
        public void Drop(QuerySide side, int slot, string place)
        {
            (double x, double y) = _centroids[place];
            Arm(side, slot);
            Press(x, y);
            Release(x, y);
        }

        public bool Press(double worldX, double worldY, ToolPointerButton button = ToolPointerButton.Left) =>
            Tool.OnPressed(At(Panes.Panes[0], worldX, worldY, button), Services);

        public void Move(double worldX, double worldY) =>
            Tool.OnMoved(At(Panes.Panes[0], worldX, worldY, ToolPointerButton.Left), Services);

        public void Release(double worldX, double worldY) =>
            Tool.OnReleased(At(Panes.Panes[0], worldX, worldY, ToolPointerButton.Left), Services);

        public ToolPointerEvent At(LevelPane? pane, double worldX, double worldY, ToolPointerButton button)
        {
            SKPoint world = new((float)worldX, (float)worldY);
            SKPoint screen = pane is null ? default : Services.WorldToScreen(pane, world);
            return new ToolPointerEvent
            {
                Pane = pane,
                Screen = screen,
                PaneLocal = pane is null ? default : new SKPoint(screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top),
                World = world,
                Pressure = 0.5f,
                Button = button
            };
        }

        // Real panes and cameras, no window: the same seam the draw and erase tools are tested through.
        private sealed class Services_(PaneSet panes) : IToolServices
        {
            public AnnotationSession Session { get; } = new(new AnnotationDocument());
            public int CurrentTick => 0;
            public long NowMilliseconds => 0;
            public LevelPane? PaneAt(SKPoint screen) => panes.PaneAt(screen.X, screen.Y);

            public SKPoint ScreenToWorld(LevelPane pane, SKPoint screen)
            {
                (double x, double y) = pane.Camera.Current.ScreenToWorld(
                    screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);
                return new SKPoint((float)x, (float)y);
            }

            public SKPoint WorldToScreen(LevelPane pane, SKPoint world)
            {
                (double x, double y) = pane.Camera.Current.WorldToScreen(world.X, world.Y);
                return new SKPoint((float)x + pane.ViewportRect.Left, (float)y + pane.ViewportRect.Top);
            }

            public double WorldUnitsPerPixel(LevelPane pane) => 1 / pane.Camera.Current.EffectiveScale;

            public bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
                out ulong steamId, out float dx, out float dy)
            {
                steamId = 0;
                dx = 0;
                dy = 0;
                return false;
            }

            public bool TryResolveDrawOffset(LevelPane pane, AnnotationElement element,
                out float offsetX, out float offsetY)
            {
                offsetX = 0;
                offsetY = 0;
                return false;
            }

            public void RequestTextEdit(Guid elementId)
            {
            }

            public void RequestRender()
            {
            }
        }
    }
}
