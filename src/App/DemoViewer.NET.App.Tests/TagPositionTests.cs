#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Views.Playback2D;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Click To Tag Position (plan §3, tag-store.md §3.6): a click resolves through the map's zones when it has
///     them and through the nearest pawn on that floor when it does not; the first click on a tag is a
///     position and the second makes a movement; and the Done criterion, the coordinates are queryable
///     through <see cref="TagQuery" />'s position predicate, the clause Search Filters reads tags with.
/// </summary>
[NotInParallel]
public class TagPositionTests
{
    private const string DemoPath = "/d/match.dem";

    private static readonly List<CachedRound> _rounds = [new() { Number = 1, StartTickFrameClock = 0 }];

    // The upper floor of a two-floor map: band [-500, 100000), key QuantizeZ(-500) = -512.
    private static readonly MapLevel Upper = new() { Id = MapSpace.IdForZMin(-500), Name = "upper", ZMin = -500, ZMax = 100_000 };
    private static readonly MapLevel Lower = new() { Id = MapSpace.IdForZMin(-2000), Name = "lower", ZMin = -2000, ZMax = -500 };

    private static readonly string[] HutOnly = ["Hut"];
    private static readonly string[] RampOnly = ["Ramp"];

    // Hut is a 100-unit square at the origin on the upper floor; Ramp sits to its right.
    private static PlaceResolver Zones() => new(new ZoneSet("de_synthetic", "9f1c02aa", "075a27b3", null, 64,
        [new ZoneFloor(-512, -528, 100_000), new ZoneFloor(-2048, -100_000, -528)],
        [new ZonePlace(0, "Hut", PlaceOrigin.Baked), new ZonePlace(1, "Ramp", PlaceOrigin.Baked)],
        [],
        [
            new ZoneArea(1, 0, -512, true, -400, [0, 0, 100, 0, 100, 100, 0, 100]),
            new ZoneArea(2, 1, -512, true, -400, [100, 0, 200, 0, 200, 100, 100, 100])
        ],
        [(1, 2)], [(0, 1)], null));

    private static PlayerMarker Pawn(int slot, float x, float y, float z, string? place, bool alive = true) =>
        new(slot, 2, x, y, z, 0, RingState.Team, 1, $"p{slot}", alive, Place: place);

    private static async Task<TagSession> Attached()
    {
        TagSession session = new(null, _ => _rounds, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);
        return session;
    }

    private static TagPosition Point(double x, double y, string? place) =>
        new() { X = x, Y = y, LevelMinZ = -512, Tick = 100, Place = place, PlaceSource = place is null ? null : "pawn" };

    [Test]
    public async Task WithZones_TheClickResolvesOnTheClickedFloor_AndIsStampedWithTheEffectiveVersion()
    {
        PlaceResolver zones = Zones();
        TagPosition hut = TagPositionResolver.Resolve(50, 50, Upper, 4_000, zones, [Pawn(0, 150, 50, 0, "Ramp")]);
        TagPosition nowhere = TagPositionResolver.Resolve(5_000, 5_000, Upper, 4_000, zones, [Pawn(0, 5_010, 5_000, 0, "Ramp")]);

        using (Assert.Multiple())
        {
            await Assert.That(hut.Place).IsEqualTo("Hut").Because("the zones answer, not the pawn standing next door");
            await Assert.That(hut.PlaceSource).IsEqualTo("zones:9f1c02aa");
            await Assert.That(hut.LevelMinZ).IsEqualTo(-512).Because("the band floor quantized, the annotation anchor rule");
            await Assert.That(hut.X).IsEqualTo(50);
            await Assert.That(hut.Tick).IsEqualTo(4_000);
            await Assert.That(nowhere.Place).IsNull().Because("a zones miss is unresolved, not guessed from a pawn");
            await Assert.That(nowhere.PlaceSource).IsNull();
        }
    }

    [Test]
    public async Task WithoutZones_TheNearestAlivePawnOnThatFloor_NamesTheClick()
    {
        PlayerMarker[] markers =
        [
            Pawn(0, 10, 10, -1_000, "Tunnels"), // closest in XY, but on the floor below
            Pawn(1, 20, 0, 0, "Ramp", alive: false), // dead: the held marker
            Pawn(2, 300, 0, 0, "Outside"),
            Pawn(3, 200, 0, 0, "Hut"),
            Pawn(4, 50, 0, 0, null) // no named nav area
        ];

        TagPosition near = TagPositionResolver.Resolve(0, 0, Upper, 10, null, markers);
        TagPosition below = TagPositionResolver.Resolve(0, 0, Lower, 10, null, markers);
        TagPosition far = TagPositionResolver.Resolve(5_000, 0, Upper, 10, null, markers);

        using (Assert.Multiple())
        {
            await Assert.That(near.Place).IsEqualTo("Hut");
            await Assert.That(near.PlaceSource).IsEqualTo(TagPositionResolver.PawnSource);
            await Assert.That(below.Place).IsEqualTo("Tunnels");
            await Assert.That(below.LevelMinZ).IsEqualTo(MapSpace.QuantizeZ(-2000));
            await Assert.That(far.Place).IsNull().Because("no pawn within the snap radius");
            await Assert.That(far.PlaceSource).IsNull();
            await Assert.That(far.X).IsEqualTo(5_000).Because("an unresolved click still keeps its coordinates");
        }
    }

    [Test]
    public async Task OneClick_IsAPosition_TwoClicksAreAMovement_OnThePendingTag_InItsOneBatch()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = new(session, null, () => 5_000);
        palette.Focus();

        await Assert.That(palette.AttachPosition(Point(0, 0, "Ramp"))).IsFalse().Because("no tag to put it on yet");

        palette.TryHandleKey(Key.D1, KeyModifiers.None); // A execute, waiting on outcome
        await Assert.That(palette.AttachPosition(Point(-220, 1610, "Ramp"))).IsTrue();
        await Assert.That(palette.PendingText).Contains("@Ramp");
        palette.AttachPosition(Point(-1180, 2044, "BombsiteA"));
        palette.AttachPosition(Point(400, 400, null));
        palette.TryHandleKey(Key.W, KeyModifiers.None);
        palette.TryHandleKey(Key.A, KeyModifiers.None);

        TagInstance tag = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(tag.Movements.Count).IsEqualTo(1);
            await Assert.That(tag.Movements[0].From.Place).IsEqualTo("Ramp");
            await Assert.That(tag.Movements[0].To.Place).IsEqualTo("BombsiteA");
            await Assert.That(tag.Positions.Count).IsEqualTo(1).Because("the third click starts a new point");
            await Assert.That(tag.Positions[0].X).IsEqualTo(400);
            await Assert.That(session.UndoDepth).IsEqualTo(1).Because("the clicks ride the code press's batch");
            await Assert.That(palette.LastText).Contains("Ramp→BombsiteA");
            await Assert.That(palette.LastText).Contains("@400,400").Because("an unresolved point shows where it was clicked");
        }

        session.Undo();
        await Assert.That(session.Document!.Instances).IsEmpty();
    }

    [Test]
    public async Task ClicksAfterTheTagIsWritten_EditTheLastTag_OneUndoEntryEach()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = new(session, null, () => 5_000);
        palette.Focus();
        palette.TryHandleKey(Key.D3, KeyModifiers.None); // Default: written at once

        palette.AttachPosition(Point(10, 10, "Ramp"));
        await Assert.That(session.Document!.Instances[0].Positions.Count).IsEqualTo(1);

        palette.AttachPosition(Point(20, 20, "Hut"));
        TagInstance moved = session.Document!.Instances[0];
        using (Assert.Multiple())
        {
            await Assert.That(moved.Positions).IsEmpty();
            await Assert.That(moved.Movements.Single().From.Place).IsEqualTo("Ramp");
            await Assert.That(moved.Movements.Single().To.Place).IsEqualTo("Hut");
            await Assert.That(session.UndoDepth).IsEqualTo(3).Because("the code, then one entry per click");
        }

        // Undo the movement: the lone point is back. Undo that too, and the tag has no point at all.
        session.Undo();
        await Assert.That(session.Document!.Instances[0].Positions.Single().Place).IsEqualTo("Ramp");
        session.Undo();
        await Assert.That(session.Document!.Instances[0].Positions).IsEmpty();

        // With the point undone there is nothing to pair with: the next click is a fresh position.
        palette.AttachPosition(Point(30, 30, "Outside"));
        TagInstance fresh = session.Document!.Instances[0];
        await Assert.That(fresh.Movements).IsEmpty();
        await Assert.That(fresh.Positions.Single().Place).IsEqualTo("Outside");
    }

    [Test]
    public async Task TheCoordinates_AreQueryable_ByPlace_ByArea_AndByFloor_ThroughTheStoredDocument()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = new(session, null, () => 5_000);
        palette.Focus();
        palette.TryHandleKey(Key.D3, KeyModifiers.None);
        palette.AttachPosition(TagPositionResolver.Resolve(50, 50, Upper, 5_000, Zones(), null));
        palette.TryHandleKey(Key.D3, KeyModifiers.None);
        palette.AttachPosition(TagPositionResolver.Resolve(120, 50, Upper, 5_000, Zones(), null));
        palette.AttachPosition(TagPositionResolver.Resolve(900, 900, Lower, 5_000, null, null));

        // Through the stored shape, not the live objects: what a cross-demo reader gets off disk.
        List<TagDocument> docs = [session.Document!.Clone()];
        Guid hutTag = docs[0].Instances[0].Id;
        Guid moveTag = docs[0].Instances[1].Id;

        IReadOnlyList<TagInstanceRef> atHut = TagQuery.Find(docs,
            TagSlice.Everything with { Positions = [new PositionPredicate(new HashSet<string>(HutOnly))] });
        IReadOnlyList<TagInstanceRef> atRamp = TagQuery.Find(docs,
            TagSlice.Everything with { Positions = [new PositionPredicate(new HashSet<string>(RampOnly))] });
        IReadOnlyList<TagInstanceRef> inSquare = TagQuery.Find(docs,
            TagSlice.Everything with { Positions = [new PositionPredicate(null, [(0, 0), (100, 0), (100, 100), (0, 100)])] });
        IReadOnlyList<TagInstanceRef> belowAnywhere = TagQuery.Find(docs,
            TagSlice.Everything with { Positions = [new PositionPredicate(null, LevelMinZ: MapSpace.QuantizeZ(-2000))] });
        IReadOnlyList<TagInstanceRef> rampAndBelow = TagQuery.Find(docs, TagSlice.Everything with
        {
            Positions =
            [
                new PositionPredicate(new HashSet<string>(RampOnly)),
                new PositionPredicate(null, LevelMinZ: MapSpace.QuantizeZ(-2000))
            ]
        });
        IReadOnlyList<TagInstanceRef> rampBelow = TagQuery.Find(docs, TagSlice.Everything with
        {
            Positions = [new PositionPredicate(new HashSet<string>(RampOnly), LevelMinZ: MapSpace.QuantizeZ(-2000))]
        });

        using (Assert.Multiple())
        {
            await Assert.That(atHut.Select(r => r.Id)).IsEquivalentTo(new[] { hutTag });
            await Assert.That(atRamp.Select(r => r.Id)).IsEquivalentTo(new[] { moveTag })
                .Because("a movement's end counts as a point of the tag");
            await Assert.That(inSquare.Select(r => r.Id)).IsEquivalentTo(new[] { hutTag });
            await Assert.That(belowAnywhere.Select(r => r.Id)).IsEquivalentTo(new[] { moveTag });
            await Assert.That(rampAndBelow.Select(r => r.Id)).IsEquivalentTo(new[] { moveTag })
                .Because("separate predicates may be met by separate points");
            await Assert.That(rampBelow).IsEmpty().Because("one predicate's clauses must hold on the same point");
            await Assert.That(TagQuery.Find(docs, TagSlice.Everything).Count).IsEqualTo(2)
                .Because("a slice with no position clause reads as it did before");
        }
    }

    [Test]
    public async Task APolygonOfFewerThanThreeVertices_EnclosesNothing()
    {
        PositionPredicate line = new(null, [(0, 0), (100, 100)]);
        await Assert.That(line.Matches(Point(50, 50, null))).IsFalse();
    }

    /// <summary>
    ///     Through the mounted view: with the palette focused a left click on the map is a point for the tag,
    ///     named by the pawn standing there (the harness has no zones); the second click makes the movement.
    ///     Unfocused, the same click is the pointer tools' again and tags nothing.
    /// </summary>
    [Test]
    [Category("Render")]
    public async Task ClicksOnTheMap_WhilePaletteHasFocus_TagPositionsAndAMovement()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushPlacedMarkers((0, 2, -800f, 600f, 64f, "Ramp"), (1, 3, 900f, -500f, 64f, "BombsiteA"));
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            host.FitToExtent();
            Playback2DTimelineHarness.Pump();

            Point At(double worldX, double worldY)
            {
                (double sx, double sy) = host.PrimaryCameraTransform.WorldToScreen(worldX, worldY);
                return Playback2DTimelineHarness.ToWindow(host, window, sx, sy);
            }

            void Click(Point p)
            {
                window.MouseDown(p, MouseButton.Left);
                window.MouseUp(p, MouseButton.Left);
                Playback2DTimelineHarness.Pump();
            }

            // Unfocused: a click is the pan tool's, and there is no tag anyway.
            Click(At(-790, 610));

            view.Focus();
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.None); // Default: written at once
            Playback2DTimelineHarness.Pump();
            await Assert.That(vm.IsTagPaletteFocused).IsTrue();

            Click(At(-790, 610));
            Click(At(890, -490));

            TagInstance tag = vm.Tags.Document!.Instances.Single();
            TagMovement? move = tag.Movements.SingleOrDefault();
            Console.WriteLine($"[tag-position] positions={tag.Positions.Count} movements={tag.Movements.Count} "
                              + $"from={move?.From.Place}@({move?.From.X:0},{move?.From.Y:0}) "
                              + $"to={move?.To.Place}@({move?.To.X:0},{move?.To.Y:0}) tick={move?.To.Tick}");
            using (Assert.Multiple())
            {
                await Assert.That(tag.Positions).IsEmpty();
                await Assert.That(move).IsNotNull();
                await Assert.That(move!.From.Place).IsEqualTo("Ramp");
                await Assert.That(move.From.PlaceSource).IsEqualTo(TagPositionResolver.PawnSource);
                await Assert.That(move.To.Place).IsEqualTo("BombsiteA");
                await Assert.That(Math.Abs(move.From.X - -790)).IsLessThan(5.0);
                await Assert.That(Math.Abs(move.To.Y - -490)).IsLessThan(5.0);
                await Assert.That(move.To.Tick).IsEqualTo(ctx.CurrentTick).Because("the frame clock at the click");
            }

            // Esc leaves the palette; a click is the tools' again and adds nothing.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            Click(At(-790, 610));
            await Assert.That(vm.Tags.Document!.Instances.Single().Positions).IsEmpty();

            window.Close();
            vm.OnDeactivated();
            vm.Dispose();
        });
    }
}
