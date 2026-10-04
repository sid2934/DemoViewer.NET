#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Zones;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratStepMotionTests;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A B execute on de_dust2 shaped like one written before carried marks: the round-start seed turned into a move with
///     a line per player and the spawn positions still on it, a lurk on the same tick holding copies of every position,
///     a regroup, a lineup throw and a push by three of the five.
/// </summary>
[NotInParallel]
public class StratSeedMoveMotionTests
{
    private const double Level = -99968;

    private static readonly (string Slot, double X, double Y)[] Spawn =
        [("A", -750, -790), ("B", -735, -910), ("C", -775, -690), ("D", -865, -735), ("E", -610, -800)];

    private static IZonePlaceResolver Dust2() =>
        new AssetZonePlaceResolverSource(MapAssetBundleReader.FindBundleDirectory, () => null).TryGet("de_dust2")
        ?? throw new SkipTestException("de_dust2's zones are not in assets/");

    private static PlaceRef Point(string place, double x, double y) => new() { Place = place, X = x, Y = y, LevelMinZ = Level };

    private static StepPosition At(string slot, double x, double y) => new() { Slot = slot, X = x, Y = y, LevelMinZ = Level };

    private static List<StepPosition> Seeded() =>
    [
        .. Spawn.Select(s => At(s.Slot, s.X, s.Y)),
        .. StratVocabulary.OpponentSlots.Select((s, i) => At(s, 100 + 50 * i, 2250))
    ];

    internal static StratDocument ExecuteB()
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_dust2", "T", "execute", "B exec", Created);
        StratStep seed = Step(1, 115, StratVocabulary.ActorAll, "move");
        seed.Positions = Seeded();
        seed.Assignments =
        [
            new StepAssignment { Slot = "A", To = new PlaceRef { Place = "UpperTunnel" } },
            new StepAssignment { Slot = "B", To = Point("TopofMid", 25, 390) },
            new StepAssignment { Slot = "C", To = Point("LongDoors", 645, 480) },
            new StepAssignment { Slot = "D", To = Point("UpperTunnel", -1955, 1070) },
            new StepAssignment { Slot = "E", To = Point("OutsideLong", 650, 140) }
        ];

        List<string> watched = ["LongDoors", "OutsideLong", "TopofMid", "Catwalk", "Middle", "MidDoors"];
        StratStep lurk = Step(2, 115, "E", "lurk");
        lurk.Positions = Seeded();
        lurk.Assignments = [new StepAssignment { Slot = "E", Watch = new StepWatch { Places = [.. watched] } }];
        lurk.Lurk = new StepLurk { Areas = [.. watched], Rotate = new LurkRotate { AtSeconds = 39, To = Point("LowerTunnel", -580, 1435) } };

        StratStep regroup = Step(3, 75, StratVocabulary.ActorAll, "move");
        regroup.Assignments =
        [
            new StepAssignment { Slot = "A", To = Point("TunnelStairs", -1090, 1105) },
            new StepAssignment { Slot = "B", To = Point("UpperTunnel", -1750, 1365) },
            new StepAssignment { Slot = "C", To = Point("OutsideTunnel", -1310, 480) },
            new StepAssignment { Slot = "D", To = Point("UpperTunnel", -1795, 980) }
        ];

        StratStep smoke = Step(4, 62, "C", "throw");
        smoke.Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };

        StratStep push = Step(5, 53, StratVocabulary.ActorAll, "push");
        push.Assignments =
        [
            new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteB" } },
            new StepAssignment { Slot = "D", To = new PlaceRef { Place = "BombsiteB" } },
            new StepAssignment { Slot = "C", To = Point("BombsiteB", -2050, 3015) }
        ];

        document.Steps = [seed, lurk, regroup, smoke, push];
        return document;
    }

    internal static StratSceneProjection Project(StratDocument document, IZonePlaceResolver map) =>
        StratSceneProjection.Build(document, StratPath.MainLine(document), null, map.PlaceCentre, map.PlaceArrival,
            (place, x, y, level) => map.ResolveOnFloor(x, y, level) == place);

    private static TokenKeyframe Sample(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe k) ? k : throw new InvalidOperationException($"no sample at {tick}");

    private static int Tick(double seconds) => StepSchedule.TickFor(seconds, 115);

    // The lurk's first area. The lurk is the later step on the seed's tick, so it beats the seed's OutsideLong line.
    private static (float X, float Y) LongDoors(IZonePlaceResolver map)
    {
        (double x, double y, double _) = map.PlaceArrival("LongDoors", Level) ?? throw new SkipTestException("no LongDoors arrival");
        return ((float)x, (float)y);
    }

    // The lurk's last area, where it holds until the rotate.
    private static (float X, float Y) MidDoors(IZonePlaceResolver map)
    {
        (double x, double y, double _) = map.PlaceArrival("MidDoors", Level) ?? throw new SkipTestException("no MidDoors arrival");
        return ((float)x, (float)y);
    }

    // The same strat after this build has written one carried mark anywhere, which ends the legacy reading for the file.
    private static StratDocument Marked()
    {
        StratDocument document = ExecuteB();
        StepPosition copy = At("O1", 100, 2250);
        copy.Carried = true;
        document.Steps[2].Positions = [copy];
        return document;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TheSeedTurnedMove_SendsEveryoneFromSpawn_ThroughTheSameTickLurksCopies(bool marked)
    {
        IZonePlaceResolver map = Dust2();
        StratDocument document = marked ? Marked() : ExecuteB();
        await Assert.That(StratSceneProjection.IsLegacyCarry(document)).IsEqualTo(!marked);
        StratSceneProjection projection = Project(document, map);
        (string Slot, string Place, double X, double Y)[] lines =
            [("B", "TopofMid", 25, 390), ("C", "LongDoors", 645, 480), ("D", "UpperTunnel", -1955, 1070)];
        using (Assert.Multiple())
        {
            foreach ((string slot, double x, double y) in Spawn)
            {
                TokenKeyframe start = Sample(projection.Tracks.Single(t => t.Slot == slot), 0);
                await Assert.That((start.X, start.Y)).IsEqualTo(((float)x, (float)y)).Because($"{slot} leaves from spawn");
            }

            foreach ((string slot, string place, double x, double y) in lines)
            {
                TokenTrack track = projection.Tracks.Single(t => t.Slot == slot);
                (string _, double sx, double sy) = Spawn.Single(s => s.Slot == slot);
                int arrive = RunTicks(x - sx, y - sy);
                await Assert.That(Sample(track, arrive - 5).X).IsNotEqualTo((float)x).Because($"{slot} is still running");
                await Assert.That((Sample(track, arrive).X, Sample(track, arrive).Y)).IsEqualTo(((float)x, (float)y));
                await Assert.That(map.ResolveOnFloor(x, y, Level)).IsEqualTo(place);
            }

            TokenTrack a = projection.Tracks.Single(t => t.Slot == "A");
            await Assert.That(map.ResolveOnFloor(Sample(a, Tick(80)).X, Sample(a, Tick(80)).Y, Level)).IsEqualTo("UpperTunnel");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AStepAddedAfterTheLurk_CarriesNoneOfTheFive(bool marked)
    {
        IZonePlaceResolver map = Dust2();
        List<StepPosition> carried = StratStepCarry.PositionsAt(marked ? Marked() : ExecuteB(), 1, null, map.PlaceCentre, 110);
        using (Assert.Multiple())
        {
            await Assert.That(carried.Where(p => StratVocabulary.Slots.Contains(p.Slot))).IsEmpty().Because("every one of them is on the move");
            await Assert.That(carried.Count(p => StratVocabulary.OpponentSlots.Contains(p.Slot))).IsEqualTo(5);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TheLurker_WalksItsAreas_ThenWalksItsRotate_AndNeverRuns(bool marked)
    {
        IZonePlaceResolver map = Dust2();
        StratSceneProjection projection = Project(marked ? Marked() : ExecuteB(), map);
        TokenTrack e = projection.Tracks.Single(t => t.Slot == "E");
        (float lx, float ly) = LongDoors(map);
        (float mx, float my) = MidDoors(map);
        int there = WalkTicks(lx - -610, ly - -800);
        int rotate = Tick(39);
        int back = rotate + WalkTicks(-580 - mx, 1435 - my);
        using (Assert.Multiple())
        {
            await Assert.That((Sample(e, there - 1).X, Sample(e, there - 1).Y)).IsNotEqualTo((lx, ly));
            await Assert.That((Sample(e, there).X, Sample(e, there).Y)).IsEqualTo((lx, ly)).Because("the lurk's first area, at a walk");
            await Assert.That(map.ResolveOnFloor(lx, ly, Level)).IsEqualTo("LongDoors");
            await Assert.That(e.Keyframes.Any(k => k.X == 650f && k.Y == 140f)).IsFalse().Because("the seed's OutsideLong line is the earlier step");
            await Assert.That((Sample(e, rotate).X, Sample(e, rotate).Y)).IsEqualTo((mx, my)).Because("it walks on to its last area and holds there");
            await Assert.That(Sample(e, back - 1).X).IsGreaterThan(-580f);
            await Assert.That((Sample(e, back).X, Sample(e, back).Y)).IsEqualTo((-580f, 1435f));
            await Assert.That(projection.ContentEndTick).IsGreaterThanOrEqualTo(back).Because("the clock reaches the rotate's arrival");
        }

        double fastest = 0;
        for (int t = 1; t <= projection.ContentEndTick; t++)
        {
            TokenKeyframe p = Sample(e, t - 1), q = Sample(e, t);
            fastest = Math.Max(fastest, Math.Sqrt((q.X - p.X) * (double)(q.X - p.X) + (q.Y - p.Y) * (double)(q.Y - p.Y)) * 64);
        }

        await Assert.That(fastest).IsLessThanOrEqualTo(StratSceneProjection.WalkUnitsPerSecond + 1).Because("nothing about a lurk runs");
    }

    [Test]
    public async Task APushForThreeOfTheFive_LeavesTheOtherTwoWhereTheyAre()
    {
        IZonePlaceResolver map = Dust2();
        StratSceneProjection projection = Project(ExecuteB(), map);
        TokenTrack a = projection.Tracks.Single(t => t.Slot == "A"), e = projection.Tracks.Single(t => t.Slot == "E");
        (float mx, float my) = MidDoors(map);
        int push = Tick(53), later = Tick(45);
        using (Assert.Multiple())
        {
            await Assert.That((Sample(a, later).X, Sample(a, later).Y)).IsEqualTo((-1090f, 1105f)).Because("A holds under, at Tunnel Stairs");
            await Assert.That((Sample(e, later).X, Sample(e, later).Y)).IsEqualTo((mx, my)).Because("the lurker is not in the push");
            foreach (string slot in new[] { "B", "C", "D" })
            {
                TokenTrack track = projection.Tracks.Single(t => t.Slot == slot);
                TokenKeyframe end = track.Keyframes[^1];
                await Assert.That(map.ResolveOnFloor(end.X, end.Y, end.LevelMinZ)).IsEqualTo("BombsiteB").Because($"{slot} pushes onto the site");
                await Assert.That(end.Tick).IsGreaterThan(push);
            }
        }
    }

    private static readonly string[] LurkWatch = ["LongDoors", "TopofMid", "Catwalk", "Middle", "MidDoors"];

    /// <summary>
    ///     The same execute as it was later left: the seed's move has no line for E, and E lurks a second later
    ///     on its own step, via Long Doors, watching and working five areas. The lurk step holds unmarked copies of the
    ///     ten seed positions; <paramref name="dragged" /> replaces E's with a drag a few units from Long Doors' centre.
    /// </summary>
    internal static StratDocument OwnersLurk(IZonePlaceResolver map, bool dragged)
    {
        StratDocument document = ExecuteB();
        StratStep seed = document.Steps[0];
        seed.Assignments!.RemoveAll(a => a.Slot == "E");

        StratStep lurk = document.Steps[1];
        lurk.AtSeconds = 114;
        lurk.Positions = Seeded();
        if (dragged)
        {
            (double x, double y, double _) = map.PlaceArrival("LongDoors", Level) ?? throw new SkipTestException("no LongDoors arrival");
            lurk.Positions[4] = At("E", Math.Round(x - 7, 2), Math.Round(y - 9, 2));
        }

        lurk.Assignments = [new StepAssignment { Slot = "E", Via = ["LongDoors"], Watch = new StepWatch { Places = [.. LurkWatch] } }];
        lurk.Lurk = new StepLurk { Areas = [.. LurkWatch], Rotate = new LurkRotate { AtSeconds = 39, To = Point("LowerTunnel", -580, 1435) } };
        return document;
    }

    private static float YawTowards(IZonePlaceResolver map, string place, TokenKeyframe from)
    {
        (double x, double y) = map.PlaceCentre(place, from.LevelMinZ) ?? throw new SkipTestException($"no {place} centre");
        return (float)StratFromRound.NormalizeYaw(Math.Atan2(y - from.Y, x - from.X) * 180 / Math.PI);
    }

    // Over a quarter second: a route's corners sit on whole ticks, so one tick alone can read a little fast.
    private static double Fastest(TokenTrack track, int from, int until)
    {
        const int Window = 16;
        double fastest = 0;
        for (int t = from + Window; t <= until; t++)
        {
            double along = 0;
            for (int k = t - Window + 1; k <= t; k++)
            {
                TokenKeyframe p = Sample(track, k - 1), q = Sample(track, k);
                along += Math.Sqrt((q.X - p.X) * (double)(q.X - p.X) + (q.Y - p.Y) * (double)(q.Y - p.Y));
            }

            fastest = Math.Max(fastest, along * 64 / Window);
        }

        return fastest;
    }

    // The first tick E stands on each lurk area's arrival, in the areas' order; -1 for one it never reaches.
    private static int[] AreaArrivals(IZonePlaceResolver map, TokenTrack e, int until) =>
    [
        .. LurkWatch.Select(area =>
        {
            (double x, double y, double _) = map.PlaceArrival(area, Level)!.Value;
            for (int t = 0; t <= until; t++)
            {
                TokenKeyframe k = Sample(e, t);
                if (Math.Abs(k.X - x) < 1 && Math.Abs(k.Y - y) < 1)
                {
                    return t;
                }
            }

            return -1;
        })
    ];

    // A lurk leg's walk from one area's arrival to the next, as the projection times it: the route's length at a walk.
    private static int LegTicks(IZonePlaceResolver map, string from, string to)
    {
        (double fx, double fy, double fl) = map.PlaceArrival(from, Level)!.Value;
        (double tx, double ty, double tl) = map.PlaceArrival(to, Level)!.Value;
        double length = map.Paths!.Route(fx, fy, fl, tx, ty, tl, to) is { } route
            ? NavPathfinder.Length(route)
            : Math.Sqrt((tx - fx) * (tx - fx) + (ty - fy) * (ty - fy));
        return Math.Max(1, (int)Math.Ceiling(length / StratSceneProjection.WalkUnitsPerSecond * 64));
    }

    private static StratSceneProjection Routed(StratDocument document, IZonePlaceResolver map) => StratRoutingTests.Project(document, map);

    [Test]
    public async Task TheOwnersLurk_FromItsSpawnCopy_WalksEveryAreaInOrder_HoldsAtTheLast_ThenRotates()
    {
        IZonePlaceResolver map = Dust2();
        StratDocument document = OwnersLurk(map, false);
        await Assert.That(StratSceneProjection.IsLegacyCarry(document)).IsTrue();
        StratSceneProjection projection = Routed(document, map);
        TokenTrack e = projection.Tracks.Single(t => t.Slot == "E");
        (float lx, float ly) = LongDoors(map);
        (string _, double sx, double sy) = Spawn.Single(s => s.Slot == "E");
        double walk = NavPathfinder.Length(map.Paths!.Route(sx, sy, Level, lx, ly, Level, "LongDoors")!);
        int lurk = Tick(114), there = lurk + (int)Math.Ceiling(walk / StratSceneProjection.WalkUnitsPerSecond * 64);
        int rotate = Tick(39);
        int[] arrivals = AreaArrivals(map, e, projection.ContentEndTick);
        (float mx, float my) = MidDoors(map);
        Console.WriteLine($"[lurk areas] {string.Join(", ", LurkWatch.Zip(arrivals, (a, t) => $"{a} {115 - t / 64.0:F1}"))}");
        using (Assert.Multiple())
        {
            await Assert.That((Sample(e, lurk).X, Sample(e, lurk).Y)).IsEqualTo(((float)sx, (float)sy))
                .Because("the seed does not send E, and the lurk step's copy of spawn is carried, not a place to jump back to");
            await Assert.That(arrivals[0]).IsEqualTo(there).Because("the routed walk via Long Doors to the first area");
            for (int k = 1; k < arrivals.Length; k++)
            {
                await Assert.That(arrivals[k]).IsEqualTo(arrivals[k - 1] + LegTicks(map, LurkWatch[k - 1], LurkWatch[k]))
                    .Because($"{LurkWatch[k]} straight after {LurkWatch[k - 1]}, at a walk");
            }

            await Assert.That(arrivals[^1]).IsLessThan(rotate);
            await Assert.That((double)Sample(e, Tick(41)).YawDegrees).IsEqualTo(YawTowards(map, "LongDoors", Sample(e, Tick(41)))).Within(0.05)
                .Because("holding Mid Doors, it faces the first place it watches");
            await Assert.That(Fastest(e, 0, projection.ContentEndTick)).IsLessThanOrEqualTo(StratSceneProjection.WalkUnitsPerSecond * 1.1)
                .Because("a lurker walks every leg");
            await Assert.That((Sample(e, rotate).X, Sample(e, rotate).Y)).IsEqualTo((mx, my)).Because("it holds the last area until the rotate");
            await Assert.That((e.Keyframes[^1].X, e.Keyframes[^1].Y)).IsEqualTo((-580f, 1435f)).Because("the rotate to Lower Tunnel");
        }
    }

    [Test]
    public async Task TheOwnersLurk_DraggedToLongDoors_LeavesFromTheDrag()
    {
        IZonePlaceResolver map = Dust2();
        StratDocument document = OwnersLurk(map, true);
        StratSceneProjection projection = Routed(document, map);
        TokenTrack e = projection.Tracks.Single(t => t.Slot == "E");
        StepPosition drag = document.Steps[1].Positions[4];
        TokenKeyframe left = Sample(e, Tick(114));
        int[] arrivals = AreaArrivals(map, e, projection.ContentEndTick);
        using (Assert.Multiple())
        {
            await Assert.That((left.X, left.Y)).IsEqualTo(((float)drag.X, (float)drag.Y))
                .Because("on a travel verb a drag is where the token is at the step's time");
            await Assert.That(arrivals.Skip(1).Zip(arrivals.Skip(2)).All(p => p.First > 0 && p.First < p.Second)).IsTrue()
                .Because("from the drag it walks on through the other areas in order");
        }
    }

    [Test]
    public async Task TheOwnersLurk_Dragged_KeepsItsStepsHold_AndItsInterpolation_OnEveryLeg()
    {
        IZonePlaceResolver map = Dust2();
        StratDocument plain = OwnersLurk(map, true), held = OwnersLurk(map, true), jumping = OwnersLurk(map, true);
        held.Steps[1].HoldSeconds = 3;
        jumping.Steps[1].Interpolation = "hold";
        int[] before = AreaArrivals(map, Routed(plain, map).Tracks.Single(t => t.Slot == "E"), Tick(20));
        int[] after = AreaArrivals(map, Routed(held, map).Tracks.Single(t => t.Slot == "E"), Tick(20));
        TokenTrack jumps = Routed(jumping, map).Tracks.Single(t => t.Slot == "E");
        int[] jumped = AreaArrivals(map, jumps, Tick(20));
        (double tx, double ty, double _) = map.PlaceArrival("TopofMid", Level)!.Value;
        TokenKeyframe between = Sample(jumps, (jumped[1] + jumped[2]) / 2);
        using (Assert.Multiple())
        {
            for (int k = 1; k < before.Length; k++)
            {
                await Assert.That(after[k] - before[k]).IsEqualTo(3 * 64).Because($"the hold delays the walk, so {LurkWatch[k]} comes 3 s later");
            }

            await Assert.That(jumped.Skip(1)).IsEquivalentTo(before.Skip(1)).Because("a hold-interpolated leg arrives when a walk would");
            await Assert.That((double)between.X).IsEqualTo(tx).Within(0.01).Because("between areas it waits at Top of Mid, then jumps");
            await Assert.That((double)between.Y).IsEqualTo(ty).Within(0.01);
        }
    }

    [Test]
    public async Task AnAreaTheMapLacks_IsPassedOver_FirstOrLater()
    {
        IZonePlaceResolver map = Dust2();
        TokenTrack baseline = Routed(OwnersLurk(map, false), map).Tracks.Single(t => t.Slot == "E");
        foreach (int at in new[] { 0, 2 })
        {
            StratDocument document = OwnersLurk(map, false);
            document.Steps[1].Lurk!.Areas.Insert(at, "Nowhere");
            TokenTrack e = Routed(document, map).Tracks.Single(t => t.Slot == "E");
            await Assert.That(e.Keyframes).IsEquivalentTo(baseline.Keyframes).Because($"an unknown area at {at} is skipped");
        }
    }

    private static double DistanceToSegment(double x, double y, NavWaypoint a, NavWaypoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
        double t = length < 1e-9 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
        return Math.Sqrt((a.X + t * dx - x) * (a.X + t * dx - x) + (a.Y + t * dy - y) * (a.Y + t * dy - y));
    }

    [Test]
    public async Task ARotateBeforeTheLastArea_CutsTheWalk_BetweenTwoAreas()
    {
        IZonePlaceResolver map = Dust2();
        StratDocument document = OwnersLurk(map, false);
        document.Steps[1].Lurk!.Rotate!.AtSeconds = 60;
        StratSceneProjection projection = Routed(document, map);
        TokenTrack e = projection.Tracks.Single(t => t.Slot == "E");
        int[] arrivals = AreaArrivals(map, e, projection.ContentEndTick);
        int rotate = Tick(60);
        TokenKeyframe cut = Sample(e, rotate);
        (double tx, double ty, double tl) = map.PlaceArrival("TopofMid", Level)!.Value;
        (double cx, double cy, double cl) = map.PlaceArrival("Catwalk", Level)!.Value;
        IReadOnlyList<NavWaypoint> leg = map.Paths!.Route(tx, ty, tl, cx, cy, cl, "Catwalk")!;
        double off = leg.Zip(leg.Skip(1)).Min(s => DistanceToSegment(cut.X, cut.Y, s.First, s.Second));
        using (Assert.Multiple())
        {
            await Assert.That(arrivals[1]).IsGreaterThan(0).And.IsLessThan(rotate).Because("Top of Mid comes before 1:00");
            await Assert.That(arrivals[2]).IsEqualTo(-1).Because("Catwalk would come after it");
            await Assert.That(Math.Sqrt((cut.X - tx) * (cut.X - tx) + (cut.Y - ty) * (cut.Y - ty))).IsGreaterThan(16.0).Because("it has left Top of Mid");
            await Assert.That(Math.Sqrt((cut.X - cx) * (cut.X - cx) + (cut.Y - cy) * (cut.Y - cy))).IsGreaterThan(16.0);
            await Assert.That(off).IsLessThan(2.0).Because("at the rotate it is on the route from Top of Mid to Catwalk");
            await Assert.That((e.Keyframes[^1].X, e.Keyframes[^1].Y)).IsEqualTo((-580f, 1435f));
            await Assert.That(Fastest(e, 0, projection.ContentEndTick)).IsLessThanOrEqualTo(StratSceneProjection.WalkUnitsPerSecond * 1.1);
        }
    }

    private static StepAssignment Lurker(string slot) => new() { Slot = slot, Watch = new StepWatch { Places = [.. LurkWatch] } };

    [Test]
    public async Task TwoLurkersOnOneStep_HoldDistinctSpots_AtEveryArea()
    {
        IZonePlaceResolver map = Dust2();
        StratDocument document = OwnersLurk(map, false);
        StratStep seed = document.Steps[0], lurk = document.Steps[1];

        // D starts where E does and lurks with it, so the two are at every area together.
        seed.Positions[3] = At("D", -610, -800);
        lurk.Positions[3] = At("D", -610, -800);
        seed.Assignments!.RemoveAll(a => a.Slot == "D");
        lurk.Assignments = [Lurker("D"), Lurker("E")];
        lurk.Actor = StratVocabulary.ActorAll;
        foreach (StratStep later in document.Steps.Skip(2))
        {
            later.Assignments?.RemoveAll(a => a.Slot == "D");
        }

        StratSceneProjection projection = Routed(document, map);
        TokenTrack d = projection.Tracks.Single(t => t.Slot == "D"), e = projection.Tracks.Single(t => t.Slot == "E");
        using (Assert.Multiple())
        {
            foreach (string area in LurkWatch)
            {
                (double x, double y, double level) = map.PlaceArrival(area, Level)!.Value;
                foreach ((string slot, TokenTrack track) in new[] { ("D", d), ("E", e) })
                {
                    (double sx, double sy) = StratSceneProjection.SpotFor(area, (x, y, level), slot,
                        (place, px, py, l) => map.ResolveOnFloor(px, py, l) == place, map.Paths);
                    await Assert.That(track.Keyframes.Any(k => Math.Abs(k.X - sx) < 1 && Math.Abs(k.Y - sy) < 1)).IsTrue()
                        .Because($"{slot} stops on its own spot at {area}");
                }
            }

            TokenKeyframe dh = Sample(d, Tick(40)), eh = Sample(e, Tick(40));
            await Assert.That(Math.Sqrt((dh.X - eh.X) * (dh.X - eh.X) + (dh.Y - eh.Y) * (dh.Y - eh.Y))).IsGreaterThan(40.0)
                .Because("they hold Mid Doors apart");
        }
    }
}
