#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Keyframes;
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
    public async Task TheLurker_WalksToItsLine_ThenWalksItsRotate_AndNeverRuns()
    {
        IZonePlaceResolver map = Dust2();
        StratSceneProjection projection = Project(ExecuteB(), map);
        TokenTrack e = projection.Tracks.Single(t => t.Slot == "E");
        int there = WalkTicks(650 - -610, 140 - -800);
        int rotate = Tick(39);
        int back = rotate + WalkTicks(-580 - 650, 1435 - 140);
        using (Assert.Multiple())
        {
            await Assert.That(Sample(e, there - 1).X).IsLessThan(650f);
            await Assert.That((Sample(e, there).X, Sample(e, there).Y)).IsEqualTo((650f, 140f)).Because("the line's place, at a walk");
            await Assert.That((Sample(e, rotate).X, Sample(e, rotate).Y)).IsEqualTo((650f, 140f)).Because("it lurks there until the rotate");
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
        int push = Tick(53), later = Tick(45);
        using (Assert.Multiple())
        {
            await Assert.That((Sample(a, later).X, Sample(a, later).Y)).IsEqualTo((-1090f, 1105f)).Because("A holds under, at Tunnel Stairs");
            await Assert.That((Sample(e, later).X, Sample(e, later).Y)).IsEqualTo((650f, 140f)).Because("the lurker is not in the push");
            foreach (string slot in new[] { "B", "C", "D" })
            {
                TokenTrack track = projection.Tracks.Single(t => t.Slot == slot);
                TokenKeyframe end = track.Keyframes[^1];
                await Assert.That(map.ResolveOnFloor(end.X, end.Y, end.LevelMinZ)).IsEqualTo("BombsiteB").Because($"{slot} pushes onto the site");
                await Assert.That(end.Tick).IsGreaterThan(push);
            }
        }

        // The regroup at 1:15 names A to D only: E keeps lurking.
        await Assert.That((Sample(e, Tick(70)).X, Sample(e, Tick(70)).Y)).IsEqualTo((650f, 140f));
    }
}
