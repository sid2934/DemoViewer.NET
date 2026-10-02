#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Zones;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Tokens follow the nav round walls (docs/strat-format.md, "Motion on the canvas"): runs stay on the mesh and
///     arrive after the route's length at their speed, fixed-time segments bend and keep their ticks, nuke's floors
///     change where the route crosses, fanned spots stand on the mesh, a lurk routes each leg, and a via is gone
///     through in order. Over the committed dust2 and nuke zones; skipped without them.
/// </summary>
[NotInParallel]
public class StratRoutingTests
{
    private const int One = 0, Two = 640;

    private static readonly Lazy<ZonePlaceResolverAdapter?> Dust2 = new(() => Load("de_dust2"));
    private static readonly Lazy<ZonePlaceResolverAdapter?> Nuke = new(() => Load("de_nuke"));

    internal static ZonePlaceResolverAdapter? Load(string map) => new AssetZonePlaceResolverSource(MapAssetBundleDir, () => null).TryGet(map) as ZonePlaceResolverAdapter;

    private static string? MapAssetBundleDir(string map) => MapAssetBundleReader.FindBundleDirectory(map);

    private static ZonePlaceResolverAdapter Map(Lazy<ZonePlaceResolverAdapter?> map) =>
        map.Value ?? throw new SkipTestException("no committed zones for this map in this checkout");

    internal static StratSceneProjection Project(StratDocument document, IZonePlaceResolver map, bool routed = true) =>
        StratSceneProjection.Build(document, StratPath.MainLine(document), null, map.PlaceCentre, map.PlaceArrival,
            (place, x, y, level) => map.ResolveOnFloor(x, y, level) == place, null, routed ? map.Paths : null);

    private static TokenTrack Track(StratSceneProjection projection, string slot) => projection.Tracks.Single(t => t.Slot == slot);

    private static TokenKeyframe At(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe k) ? k : throw new InvalidOperationException($"no sample at {tick}");

    private static StepPosition At(string slot, (double X, double Y, double Level) p) => new() { Slot = slot, X = p.X, Y = p.Y, LevelMinZ = p.Level };

    private static (double X, double Y, double Level) Arrival(IZonePlaceResolver map, string place, double level = -99968) =>
        map.PlaceArrival(place, level) ?? throw new InvalidOperationException($"no {place}");

    // A at a place's arrival from 1:55; at 1:45 the step sends A on.
    private static StratDocument Sent(string map, string from, string verb, string to, double level, IZonePlaceResolver zones)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, map, "T", "execute", "routing", Created);
        StratStep one = Step(1, 115, "A", "hold");
        one.Positions = [At("A", Arrival(zones, from, level))];
        StratStep two = Step(2, 105, "A", verb, to: to);
        document.Steps = [one, two];
        return document;
    }

    // The share of samples, every 4 ticks over [from, until], more than `tolerance` off the nav on their floor.
    private static double OffMesh(TokenTrack track, PathResolver paths, int from, int until, double tolerance = 12)
    {
        int off = 0, total = 0;
        for (int t = from; t <= until; t += 4)
        {
            TokenKeyframe k = At(track, t);
            total++;
            if (paths.Snap(k.X, k.Y, k.LevelMinZ, null, tolerance) is null)
            {
                off++;
            }
        }

        return off / (double)total;
    }

    [Test]
    public async Task ARoutedRun_StaysOnTheMesh_WhereTheStraightOneCrossesWalls()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = Sent("de_dust2", "TSpawn", "move", "LongDoors", -99968, map);
        TokenTrack routed = Track(Project(document, map), "A");
        TokenTrack straight = Track(Project(document, map, false), "A");

        double off = OffMesh(routed, map.Paths, Two, routed.Keyframes[^1].Tick);
        double offStraight = OffMesh(straight, map.Paths, Two, straight.Keyframes[^1].Tick);
        Console.WriteLine($"[routing] T spawn to Long Doors: routed {routed.Keyframes.Count} keys, off-mesh {off:P1}; straight off-mesh {offStraight:P1}");
        await Assert.That(off).IsLessThanOrEqualTo(0.02);
        await Assert.That(offStraight).IsGreaterThan(0.2);
        await Assert.That(routed.Keyframes.Count).IsGreaterThan(straight.Keyframes.Count);
    }

    [Test]
    public async Task ARun_ArrivesAfterItsRoutesLength_AtRunSpeed()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = Sent("de_dust2", "TSpawn", "move", "LongDoors", -99968, map);
        TokenTrack a = Track(Project(document, map), "A");
        (double X, double Y, double Level) start = Arrival(map, "TSpawn");
        (double X, double Y, double Level) end = Arrival(map, "LongDoors");
        IReadOnlyList<NavWaypoint> route = map.Paths.Route((float)start.X, (float)start.Y, start.Level, (float)end.X, (float)end.Y, end.Level, "LongDoors")!;
        double length = NavPathfinder.Length(route);
        int arrive = Two + (int)Math.Ceiling(length / StratSceneProjection.RunUnitsPerSecond * 64);
        Console.WriteLine($"[routing] route {length:F0} u, arrives at tick {arrive}; straight would be {Two + StratStepMotionTests.RunTicks(end.X - start.X, end.Y - start.Y)}");

        TokenKeyframe last = a.Keyframes[^1];
        await Assert.That(last.Tick).IsEqualTo(arrive);
        await Assert.That((double)last.X).IsEqualTo(end.X).Within(0.01);
        await Assert.That((double)last.Y).IsEqualTo(end.Y).Within(0.01);
        await Assert.That(Project(document, map).ContentEndTick).IsEqualTo(arrive);

        // One speed over the whole run: every corner is at its share of the length.
        double along = 0;
        for (int i = 1; i < a.Keyframes.Count - 1; i++)
        {
            TokenKeyframe k = a.Keyframes[i], p = a.Keyframes[i - 1];
            if (k.Tick <= Two)
            {
                continue;
            }

            along += Math.Sqrt((k.X - p.X) * (double)(k.X - p.X) + (k.Y - p.Y) * (double)(k.Y - p.Y));
            double expected = Two + (arrive - Two) * along / length;
            await Assert.That((double)k.Tick).IsEqualTo(expected).Within(1.5);
        }
    }

    [Test]
    public async Task AHoldInterpolatedRun_JumpsAtTheRoutesArrival_WithNoCorners()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = Sent("de_dust2", "TSpawn", "move", "LongDoors", -99968, map);
        document.Steps[1].Interpolation = "hold";
        TokenTrack a = Track(Project(document, map), "A");
        TokenTrack linear = Track(Project(Sent("de_dust2", "TSpawn", "move", "LongDoors", -99968, map), map), "A");
        await Assert.That(a.Keyframes[^1].Tick).IsEqualTo(linear.Keyframes[^1].Tick);
        await Assert.That(a.Keyframes.Count(k => k.Tick > Two)).IsEqualTo(1);
    }

    [Test]
    public async Task AnAuthoredSegment_Bends_AndKeepsItsTicks()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_dust2", "T", "execute", "drag", Created);
        StratStep one = Step(1, 115, "A", "hold");
        one.Positions = [At("A", Arrival(map, "TSpawn"))];
        StratStep two = Step(2, 105, "A", "hold");
        two.Positions = [At("A", Arrival(map, "LongDoors"))];
        document.Steps = [one, two];

        TokenTrack bent = Track(Project(document, map), "A");
        TokenTrack straight = Track(Project(document, map, false), "A");
        Console.WriteLine($"[routing] drag segment: {straight.Keyframes.Count} keys straight, {bent.Keyframes.Count} routed");
        await Assert.That(bent.Keyframes.Count).IsGreaterThan(straight.Keyframes.Count);
        await Assert.That(bent.Keyframes[0]).IsEqualTo(straight.Keyframes[0]);
        await Assert.That(bent.Keyframes[^1]).IsEqualTo(straight.Keyframes[^1]).Because("it arrives where and when the drag put it");
        await Assert.That(OffMesh(bent, map.Paths, One, Two)).IsLessThanOrEqualTo(0.02);
    }

    [Test]
    [Arguments(115.0, 114.9)]
    [Arguments(115.0, 114.5)]
    public async Task AShortAuthoredSegment_WithAWindingRoute_StaysOnTheMesh(double from, double to)
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_dust2", "T", "execute", "short drag", Created);
        StratStep one = Step(1, from, "A", "hold");
        one.Positions = [At("A", Arrival(map, "TSpawn"))];
        StratStep two = Step(2, to, "A", "hold");
        two.Positions = [At("A", Arrival(map, "LongDoors"))];
        document.Steps = [one, two];

        TokenTrack a = Track(Project(document, map), "A");
        int end = a.Keyframes[^1].Tick;
        int routeCorners = map.Paths.Route(a.Keyframes[0].X, a.Keyframes[0].Y, -99968, a.Keyframes[^1].X, a.Keyframes[^1].Y, -99968, null)!.Count - 2;
        int off = 0, segments = 0;
        for (int i = 1; i < a.Keyframes.Count; i++)
        {
            TokenKeyframe p = a.Keyframes[i - 1], q = a.Keyframes[i];
            segments++;
            off += map.Paths.Clear(p.X, p.Y, q.X, q.Y, p.LevelMinZ, 24) ? 0 : 1;
        }

        Console.WriteLine($"[routing] {end} ticks for {routeCorners} bends: {a.Keyframes.Count} keyframes, {off}/{segments} drawn segments off the mesh");
        await Assert.That(a.Keyframes.Count).IsEqualTo(Math.Min(end + 1, routeCorners + 2)).Because("a bend on every free tick");
        for (int t = 0; t <= end; t++)
        {
            TokenKeyframe k = At(a, t);
            await Assert.That(map.Paths.Snap(k.X, k.Y, k.LevelMinZ, null, 12)).IsNotNull().Because($"tick {t} is on the mesh");
        }

        await Assert.That(off).IsEqualTo(0);
    }

    [Test]
    public async Task ARouteAcrossNukesFloors_SwitchesLevelAtTheCrossing()
    {
        ZonePlaceResolverAdapter map = Map(Nuke);
        StratDocument document = Sent("de_nuke", "Outside", "move", "BombsiteB", -512, map);
        document.Steps[1].Via = ["Ramp"];
        TokenTrack a = Track(Project(document, map), "A");
        await Assert.That(At(a, Two).LevelMinZ).IsEqualTo(-512d);
        await Assert.That(a.Keyframes[^1].LevelMinZ).IsEqualTo(-99968d);

        // Every change of level is between two adjacent ticks, and each side of it stands on its own floor's mesh.
        int changes = 0;
        for (int t = Two; t < a.Keyframes[^1].Tick; t++)
        {
            TokenKeyframe before = At(a, t), after = At(a, t + 1);
            if (before.LevelMinZ == after.LevelMinZ)
            {
                continue;
            }

            changes++;
            await Assert.That(map.Paths.Snap(before.X, before.Y, before.LevelMinZ, null, 12)).IsNotNull();
            await Assert.That(map.Paths.Snap(after.X, after.Y, after.LevelMinZ, null, 12)).IsNotNull();
        }

        Console.WriteLine($"[routing] nuke Outside to B via Ramp: {changes} floor change(s), arrives at {a.Keyframes[^1].Tick}");
        await Assert.That(changes).IsGreaterThanOrEqualTo(1);
        await Assert.That(OffMesh(a, map.Paths, Two, a.Keyframes[^1].Tick)).IsLessThanOrEqualTo(0.03);
    }

    [Test]
    public async Task TokensSentToOnePlace_FanOutOntoTheMesh()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_dust2", "T", "execute", "fan", Created);
        StratStep one = Step(1, 115, StratVocabulary.ActorAll, "hold");
        one.Positions = [.. StratVocabulary.Slots.Select((s, i) => At(s, Arrival(map, "TSpawn") with { X = Arrival(map, "TSpawn").X + 30 * i }))];
        document.Steps = [one, Step(2, 105, StratVocabulary.ActorAll, "move", to: "BombsiteB")];

        StratSceneProjection projection = Project(document, map);
        foreach (TokenTrack track in projection.Tracks)
        {
            TokenKeyframe spot = track.Keyframes[^1];
            await Assert.That(map.Paths.OnMesh(spot.X, spot.Y, spot.LevelMinZ)).IsTrue().Because($"{track.Slot}'s spot stands on the nav");
            await Assert.That(map.ResolveOnFloor(spot.X, spot.Y, spot.LevelMinZ)).IsEqualTo("BombsiteB");
        }

        await Assert.That(projection.Tracks.Select(t => (t.Keyframes[^1].X, t.Keyframes[^1].Y)).Distinct().Count()).IsEqualTo(5);
    }

    [Test]
    public async Task ALurk_RoutesItsWalk_ThenItsRotate_LegByLeg()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = Sent("de_dust2", "TSpawn", "lurk", "Middle", -99968, map);
        document.Steps[1].To = null;
        document.Steps[1].Lurk = new StepLurk
        {
            Areas = ["Middle"], Rotate = new LurkRotate { AtSeconds = 60, To = new PlaceRef { Place = "BombsiteB" } }
        };
        TokenTrack a = Track(Project(document, map), "A");
        int rotate = StepSchedule.TickFor(60, 115);
        (double X, double Y, double Level) middle = Arrival(map, "Middle");
        (double X, double Y, double Level) site = Arrival(map, "BombsiteB");

        TokenKeyframe atRotate = At(a, rotate);
        await Assert.That((double)atRotate.X).IsEqualTo(middle.X).Within(0.01).Because("the walk to the area arrived before the rotate");
        await Assert.That((double)a.Keyframes[^1].X).IsEqualTo(site.X).Within(0.01);
        await Assert.That(a.Keyframes.Count(k => k.Tick > Two && k.Tick < rotate)).IsGreaterThan(1).Because("the walk to Middle bends");
        await Assert.That(a.Keyframes.Count(k => k.Tick > rotate)).IsGreaterThan(1).Because("the rotate bends too");
        await Assert.That(OffMesh(a, map.Paths, Two, a.Keyframes[^1].Tick)).IsLessThanOrEqualTo(0.02);

        double walk = NavPathfinder.Length(map.Paths.Route(atRotate.X, atRotate.Y, atRotate.LevelMinZ, (float)site.X, (float)site.Y, site.Level, "BombsiteB")!);
        await Assert.That(a.Keyframes[^1].Tick).IsEqualTo(rotate + (int)Math.Ceiling(walk / StratSceneProjection.WalkUnitsPerSecond * 64))
            .Because("the rotate walks its own route from where the lurker stands");
    }

    [Test]
    [Arguments("OutsideLong", "Middle")]
    [Arguments("Middle", "OutsideLong")]
    public async Task AMoveGoesThroughItsVias_InOrder(string first, string second)
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = Sent("de_dust2", "TSpawn", "move", "BombsiteB", -99968, map);
        document.Steps[1].Via = [first, second];
        TokenTrack a = Track(Project(document, map), "A");

        int Closest(string place)
        {
            (double X, double Y, double Level) p = Arrival(map, place);
            return a.Keyframes.Select((k, i) => (D: (k.X - p.X) * (k.X - p.X) + (k.Y - p.Y) * (k.Y - p.Y), i)).MinBy(x => x.D).i;
        }

        int i1 = Closest(first), i2 = Closest(second);
        (double X, double Y, double Level) p1 = Arrival(map, first);
        TokenKeyframe k1 = a.Keyframes[i1];
        await Assert.That(i1).IsLessThan(i2);
        await Assert.That(Math.Sqrt((k1.X - p1.X) * (k1.X - p1.X) + (k1.Y - p1.Y) * (k1.Y - p1.Y))).IsLessThan(1.0);
        await Assert.That(OffMesh(a, map.Paths, Two, a.Keyframes[^1].Tick)).IsLessThanOrEqualTo(0.03);
    }

    [Test]
    [Arguments("move")]
    [Arguments("lurk")]
    public async Task AViaThatIsTheDestination_IsTheRunWithoutIt(string verb)
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument Doc(bool via)
        {
            StratDocument document = Sent("de_dust2", "TSpawn", verb, "LongDoors", -99968, map);
            if (verb == "lurk")
            {
                document.Steps[1].To = null;
                document.Steps[1].Lurk = new StepLurk { Areas = ["LongDoors"] };
            }

            document.Steps[1].Via = via ? ["LongDoors"] : null;
            return document;
        }

        TokenTrack plain = Track(Project(Doc(false), map), "A"), through = Track(Project(Doc(true), map), "A");
        await Assert.That(through.Keyframes).IsEquivalentTo(plain.Keyframes).Because("the run ends facing its last leg, not the straight line from spawn");
    }

    [Test]
    public async Task TheCanvas_RoutesWhenTheFeatureIsOn_AndDrawsTodaysTracksWhenOff()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = ExecuteB(map);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        bool routing = false;
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(map), post: a => a(), routing: () => routing);

        StratSceneProjection off = canvas.Projection!;
        StratSceneProjection today = Project(session.Document!, map, false);
        await Assert.That(off.Routed).IsFalse();
        await Assert.That(off.Tracks.Count).IsEqualTo(today.Tracks.Count);
        for (int i = 0; i < today.Tracks.Count; i++)
        {
            await Assert.That(off.Tracks[i].Keyframes.SequenceEqual(today.Tracks[i].Keyframes)).IsTrue();
            await Assert.That(off.Tracks[i].HoldTicks.SequenceEqual(today.Tracks[i].HoldTicks)).IsTrue();
        }

        routing = true;
        canvas.Transport.Seek(0);
        session.Apply(PatchOp.ReplaceOp("/name", null, System.Text.Json.Nodes.JsonValue.Create("routed")));
        StratSceneProjection on = canvas.Projection!;
        StratSceneProjection routed = Project(session.Document!, map);
        await Assert.That(on.Routed).IsTrue();
        await Assert.That(on.ContentEndTick).IsEqualTo(routed.ContentEndTick);
        await Assert.That(on.ContentEndTick).IsGreaterThan(off.ContentEndTick);
        for (int i = 0; i < routed.Tracks.Count; i++)
        {
            await Assert.That(on.Tracks[i].Keyframes.SequenceEqual(routed.Tracks[i].Keyframes)).IsTrue();
        }
    }

    [Test]
    public async Task ARouteLine_ShowsWhileATokenMoves_AndIsGoneOnArrival()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = Sent("de_dust2", "TSpawn", "move", "LongDoors", -99968, map);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(map), post: a => a(), routing: () => true);
        TokenTrack a = canvas.Projection!.Tracks.Single(t => t.Slot == "A");
        int arrive = a.Keyframes[^1].Tick;
        int mid = (Two + arrive) / 2;

        canvas.Transport.Seek(mid);
        TokenRouteLine line = canvas.CurrentFrame.Routes.Single();
        TokenKeyframe now = At(a, mid);
        await Assert.That(line.Team).IsEqualTo(2);
        await Assert.That(line.Points[0].X).IsEqualTo(now.X);
        await Assert.That(line.Points[^1].X).IsEqualTo(a.Keyframes[^1].X);
        await Assert.That(line.Points.Count).IsEqualTo(a.Keyframes.Count(k => k.Tick > mid) + 1).Because("every corner still ahead");

        canvas.Transport.Seek(Two - 10);
        await Assert.That(canvas.CurrentFrame.Routes.Count).IsEqualTo(0).Because("standing before the run");
        canvas.Transport.Seek(arrive);
        await Assert.That(canvas.CurrentFrame.Routes.Count).IsEqualTo(0).Because("gone on arrival");

        // The export draws it too: the same spec flag, off the same projection.
        StratExportCapture capture = canvas.CaptureForExport()!;
        StratFrameSourceProbe(capture, mid, out int routes);
        await Assert.That(routes).IsEqualTo(1);
    }

    private static void StratFrameSourceProbe(StratExportCapture capture, int tick, out int routes)
    {
        Playback2D.Pipeline.Frames.StratFrameSource source = new(StratExportJob.BuildSpec(capture, null, tick, tick, 64, 1.0));
        routes = source.FrameAt(0).Routes.Count;
    }

    /// <summary>
    ///     Execute B on dust2: eight steps, ten tokens, a lurk, two throws, and opponents dragged at three
    ///     steps, so every kind of move is in it: runs, position walks, a lurk's walk and rotate, and fixed-time drags.
    /// </summary>
    internal static StratDocument ExecuteB(IZonePlaceResolver map)
    {
        const double Floor = -99968;
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_dust2", "T", "execute", "Execute B", Created);
        (double X, double Y, double Level) spawn = Arrival(map, "TSpawn"), ct = Arrival(map, "CTSpawn"), site = Arrival(map, "BombsiteB");
        (double X, double Y, double Level) doors = Arrival(map, "BDoors"), mid = Arrival(map, "Middle"), window = Arrival(map, "BombsiteB");

        StratStep seed = Step(1, 115, StratVocabulary.ActorAll, "hold");
        seed.Positions =
        [
            .. StratVocabulary.Slots.Select((s, i) => At(s, (spawn.X - 120 + 60 * i, spawn.Y, Floor))),
            At("O1", (ct.X, ct.Y, Floor)), At("O2", (ct.X + 60, ct.Y, Floor)), At("O3", (site.X, site.Y, Floor)),
            At("O4", (doors.X, doors.Y, Floor)), At("O5", (mid.X, mid.Y, Floor))
        ];
        StratStep split = Step(2, 108, StratVocabulary.ActorAll, "move");
        split.Assignments =
        [
            new StepAssignment { Slot = "A", To = new PlaceRef { Place = "UpperTunnel" } },
            new StepAssignment { Slot = "B", To = new PlaceRef { Place = "UpperTunnel" } },
            new StepAssignment { Slot = "C", To = new PlaceRef { Place = "OutsideTunnel" } },
            new StepAssignment { Slot = "D", To = new PlaceRef { Place = "TopofMid" } }
        ];
        StratStep lurk = Step(3, 108, "E", "lurk");
        lurk.Lurk = new StepLurk { Areas = ["Middle"], Rotate = new LurkRotate { AtSeconds = 70, To = new PlaceRef { Place = "BombsiteB" } } };
        StratStep stack = Step(4, 96, StratVocabulary.ActorAll, "move");
        stack.Assignments =
        [
            new StepAssignment { Slot = "A", To = new PlaceRef { Place = "UpperTunnel" } },
            new StepAssignment { Slot = "C", To = new PlaceRef { Place = "UpperTunnel" } },
            new StepAssignment { Slot = "D", To = new PlaceRef { Place = "LowerTunnel" } }
        ];
        stack.Positions = [At("O3", (window.X + 150, window.Y + 80, Floor)), At("O5", (mid.X + 200, mid.Y + 300, Floor))];
        StratStep smoke = Step(5, 90, "B", "throw");
        smoke.Utility = new UtilityRef { Kind = "smoke", Landing = new UtilityLanding { Place = "BDoors" } };
        StratStep flash = Step(6, 88, "C", "throw");
        flash.Utility = new UtilityRef { Kind = "flash", Landing = new UtilityLanding { Place = "BombsiteB" } };
        StratStep push = Step(7, 85, StratVocabulary.ActorAll, "push");
        push.Assignments =
        [
            new StepAssignment { Slot = "A", To = new PlaceRef { Place = "BombsiteB" } },
            new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteB" } },
            new StepAssignment { Slot = "C", To = new PlaceRef { Place = "BDoors" } },
            new StepAssignment { Slot = "D", To = new PlaceRef { Place = "BombsiteB" } }
        ];
        push.Positions = [At("O1", (doors.X + 200, doors.Y - 200, Floor)), At("O2", (mid.X - 150, mid.Y + 400, Floor))];
        StratStep plant = Step(8, 75, "A", "plant", to: "BombsiteB");
        plant.Positions = [At("O1", (doors.X, doors.Y + 100, Floor)), At("O4", (site.X - 100, site.Y, Floor))];
        document.Steps = [seed, split, lurk, stack, smoke, flash, push, plant];
        return document;
    }

    [Test]
    public async Task TheCostPerEdit_OnExecuteB_IsMeasured()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument document = ExecuteB(map);
        IReadOnlyList<StratPathStep> path = StratPath.MainLine(document);
        PathResolver fresh = new NavPathResolver(map.Resolver.Zones);

        StratSceneProjection Build(PathResolver? paths) =>
            StratSceneProjection.Build(document, path, null, map.PlaceCentre, map.PlaceArrival,
                (place, x, y, level) => map.ResolveOnFloor(x, y, level) == place, null, paths);

        double Median(Func<double> sample, int n)
        {
            double[] runs = new double[n];
            for (int i = 0; i < n; i++)
            {
                runs[i] = sample();
            }

            Array.Sort(runs);
            return runs[n / 2];
        }

        double Time(Action action)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            return System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        for (int i = 0; i < 20; i++)
        {
            Build(null);
            Build(map.Paths);
        }

        double cold = Time(() => Build(fresh));
        double straight = Median(() => Time(() => Build(null)), 40);
        double warm = Median(() => Time(() => Build(map.Paths)), 40);

        // An edit: one authored opponent moves a little each time, so its two segments miss and the rest hit.
        StepPosition dragged = document.Steps[3].Positions[0];
        double baseX = dragged.X;
        int edits = 0;
        double edit = Median(() =>
        {
            dragged.X = baseX + ++edits;
            return Time(() => Build(map.Paths));
        }, 40);
        dragged.X = baseX;

        StratSceneProjection projection = Build(map.Paths);
        int moves = 0;
        double drag = Median(() => Time(() => projection.TrackWith("O3", 3,
            new TokenPlacement((float)(baseX + 500 + ++moves), (float)dragged.Y, dragged.LevelMinZ, null))), 40);
        double dragStill = Median(() => Time(() => projection.TrackWith("O3", 3,
            new TokenPlacement((float)(baseX + 500), (float)dragged.Y, dragged.LevelMinZ, null))), 40);
        int keys = projection.Tracks.Sum(t => t.Keyframes.Count);
        int keysStraight = Build(null).Tracks.Sum(t => t.Keyframes.Count);

        Console.WriteLine($"[routing cost] Execute B: {document.Steps.Count} steps, {projection.Tracks.Count} tracks, {keysStraight} keyframes straight, {keys} routed");
        Console.WriteLine($"[routing cost] build straight {straight:F2} ms, routed cold {cold:F2} ms, routed warm {warm:F2} ms, routed after a one-token edit {edit:F2} ms");
        Console.WriteLine($"[routing cost] drag preview (TrackWith) moving {drag:F3} ms, still {dragStill:F3} ms");
        await Assert.That(warm).IsLessThan(50);
        await Assert.That(drag).IsLessThan(16);
    }

    [Test]
    public async Task WithNoResolver_AViaIsIgnored_AndTheTrackIsTodays()
    {
        ZonePlaceResolverAdapter map = Map(Dust2);
        StratDocument plain = Sent("de_dust2", "TSpawn", "move", "BombsiteB", -99968, map);
        StratDocument via = Sent("de_dust2", "TSpawn", "move", "BombsiteB", -99968, map);
        via.Steps[1].Via = ["Middle"];
        TokenTrack a = Track(Project(plain, map, false), "A");
        TokenTrack b = Track(Project(via, map, false), "A");
        await Assert.That(b.Keyframes.SequenceEqual(a.Keyframes)).IsTrue();
    }
}
