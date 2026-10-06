#region

using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using static DemoViewer.NET.AppTests.StratAssignmentsTests;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A token's facing from its line: an explicit view angle, else towards the first watched place's centre on
///     the token's level, else as before. A throw's lineup origin still wins, and a watch on a step with no
///     position for the slot turns the token where it stands without re-timing any move.
/// </summary>
[NotInParallel]
public class StratAssignmentsFacingTests
{
    // 1:50, 1:40, 1:30 and 1:20 on a 115 s clock.
    private const int One = 320, Two = 960, Three = 1600, Four = 2240;

    // Ramp's centre is up and to the right of B's spot at the origin: 45 degrees.
    private static readonly PlaceCentreResolver Centres = (place, _) => place == "Ramp" ? (100, 100) : null;

    // B stands at the origin from step 1 and walks north at step 4; step 2 has B's line and no position.
    private static StratDocument Watching(StepWatch watch, bool positionOnTwo = false)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "execute", "watch", Created);
        StratStep one = Step(1, 110, StratVocabulary.ActorAll, "hold");
        one.Positions = [Position("B", 0, 0, 10), Position("C", 50, 0)];
        StratStep two = Step(2, 100, StratVocabulary.ActorAll, "hold");
        two.Assignments = [new StepAssignment { Slot = "B", Watch = watch }, new StepAssignment { Slot = "C" }];
        if (positionOnTwo)
        {
            two.Positions = [Position("B", 0, 0, 10)];
        }

        StratStep three = Step(3, 90, "C", "hold");
        StratStep four = Step(4, 80, "B", "move", to: "BombsiteA");
        four.Positions = [Position("B", 0, 640)];
        document.Steps = [one, two, three, four];
        return document;
    }

    private static TokenTrack Track(StratDocument document, string slot, PlaceCentreResolver? centres,
        ThrowOriginResolver? origins = null) =>
        StratSceneProjection.Build(document, StratPath.MainLine(document), origins, centres).Tracks.Single(t => t.Slot == slot);

    private static float YawAt(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe sample) ? sample.YawDegrees : float.NaN;

    [Test]
    public async Task AWatchedPlace_TurnsTheToken_TowardsItsCentre()
    {
        TokenTrack b = Track(Watching(new StepWatch { Places = ["Ramp", "Hut"] }), "B", Centres);

        using (Assert.Multiple())
        {
            await Assert.That(YawAt(b, One)).IsEqualTo(10f).Because("before the step, the authored yaw");
            await Assert.That(YawAt(b, Two)).IsEqualTo(45f);
            await Assert.That(YawAt(b, Three)).IsEqualTo(45f).Because("the facing holds until the next entry");
        }
    }

    [Test]
    public async Task AWatchedPlaceTheTokenStandsOn_IsPassedOver_ForTheNextOne()
    {
        PlaceCentreResolver centres = (place, _) => place switch
        {
            "Hut" => (6, -8),
            "Ramp" => (100, 100),
            _ => null
        };
        TokenTrack b = Track(Watching(new StepWatch { Places = ["Hut", "Ramp"] }), "B", centres);

        await Assert.That(YawAt(b, Two)).IsEqualTo(45f).Because("B stands on Hut, so it faces Ramp");
    }

    [Test]
    public async Task AnExplicitAngle_OverridesTheWatchedPlace_AndAnAuthoredYaw()
    {
        TokenTrack b = Track(Watching(new StepWatch { Places = ["Ramp"], YawDegrees = -90 }, true), "B", Centres);
        TokenTrack authored = Track(Watching(new StepWatch { Places = ["Ramp"] }, true), "B", Centres);

        using (Assert.Multiple())
        {
            await Assert.That(YawAt(b, Two)).IsEqualTo(270f).Because("the angle is normalized to 0..360");
            await Assert.That(YawAt(authored, Two)).IsEqualTo(45f).Because("the watched place beats the position's yaw");
        }
    }

    [Test]
    public async Task WithNoPlaceCentres_OrAnUnknownPlace_NothingChanges()
    {
        StratDocument plain = Watching(new StepWatch());
        plain.Steps[1].Assignments = null;
        TokenTrack baseline = Track(plain, "B", null);

        TokenTrack noZones = Track(Watching(new StepWatch { Places = ["Ramp"] }), "B", null);
        TokenTrack unknown = Track(Watching(new StepWatch { Places = ["Nowhere", "Ramp"] }), "B", Centres);
        using (Assert.Multiple())
        {
            await Assert.That(noZones.Keyframes).IsEquivalentTo(baseline.Keyframes);
            await Assert.That(unknown.Keyframes).IsEquivalentTo(baseline.Keyframes).Because("only the first watched place is faced");
        }
    }

    [Test]
    public async Task TurningInPlace_DoesNotRetimeTheLaterMove()
    {
        StratDocument plain = Watching(new StepWatch());
        plain.Steps[1].Assignments = null;
        TokenTrack baseline = Track(plain, "B", null);
        TokenTrack turned = Track(Watching(new StepWatch { Places = ["Ramp"] }), "B", Centres);

        List<string> moved = [];
        for (int tick = 0; tick <= Four + 64; tick += 16)
        {
            baseline.TrySample(tick, out TokenKeyframe a);
            turned.TrySample(tick, out TokenKeyframe b);
            if (a.X != b.X || a.Y != b.Y)
            {
                moved.Add($"{tick}: {a.X},{a.Y} vs {b.X},{b.Y}");
            }
        }

        await Assert.That(moved).IsEmpty();
    }

    [Test]
    public async Task AThrowsLineupOrigin_StillPinsItsThrower_WithTheThrowsYaw()
    {
        StratDocument document = Watching(new StepWatch { Places = ["Ramp"], YawDegrees = 200 });
        StratStep two = document.Steps[1];
        two.Verb = "throw";
        two.Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        two.Assignments!.RemoveAt(1);
        two.Actor = "B";
        ThrowOriginResolver origins = (_, _) => new TokenPlacement(-300, -300, 0, 90);

        TokenTrack b = Track(document, "B", Centres, origins);
        await Assert.That(b.Keyframes.Single(k => k.Tick == Two)).IsEqualTo(new TokenKeyframe(Two, -300, -300, 0, 90));
    }

    [Test]
    public async Task ANewStep_CarriesTheFacingTheCanvasShows()
    {
        StratDocument document = Watching(new StepWatch { Places = ["Ramp"] });
        List<StepPosition> carried = StratStepCarry.PositionsAt(document, 1, null, Centres);
        List<StepPosition> without = StratStepCarry.PositionsAt(document, 1, null);

        using (Assert.Multiple())
        {
            await Assert.That(carried.Single(p => p.Slot == "B").YawDegrees).IsEqualTo(45);
            await Assert.That(without.Single(p => p.Slot == "B").YawDegrees).IsEqualTo(10);
            await Assert.That(StratStore.Serialize(Doc(carried.Single(p => p.Slot == "C"))))
                .IsEqualTo(StratStore.Serialize(Doc(document.Steps[0].Positions[1])))
                .Because("an entry the projection did not turn is carried as stored");
            await Assert.That(carried.All(p => p.Carried == true)).IsTrue().Because("every carried entry is marked");
            await Assert.That(document.Steps[0].Positions[1].Carried).IsNull().Because("the step it was copied from keeps its entry");
        }

        // One document around a position without its mark, so two entries compare by their bytes.
        StratDocument Doc(StepPosition position)
        {
            StratDocument holder = StratDocument.Create(Guid.Empty, Team, "de_mirage", "T", "execute", "x", Created);
            StepPosition copy = new() { Slot = position.Slot, X = position.X, Y = position.Y, LevelMinZ = position.LevelMinZ,
                YawDegrees = position.YawDegrees, Extra = position.Extra };
            holder.Steps = [new StratStep { Id = Guid.Empty, Positions = [copy] }];
            return holder;
        }
    }

    [Test]
    public async Task APlacesCentre_IsOnTheTokensFloor_WhenThePlaceIsThere()
    {
        ZoneSet zones = new("de_synthetic", "1", "1", null, 64,
            [new ZoneFloor(-512, -528, 100_000), new ZoneFloor(-2048, -100_000, -528)],
            [new ZonePlace(0, "Ramp", PlaceOrigin.Baked), new ZonePlace(1, "Hut", PlaceOrigin.Baked)],
            [],
            [
                new ZoneArea(1, 0, -512, true, -400, [0, 0, 100, 0, 100, 100, 0, 100]),
                new ZoneArea(2, 0, -2048, true, -2000, [1000, 0, 1100, 0, 1100, 100, 1000, 100]),
                new ZoneArea(3, 1, -512, true, -400, [200, 0, 400, 0, 400, 200, 200, 200])
            ],
            [], [], null);
        StratPlaceCentres centres = StratPlaceCentres.From(zones);

        using (Assert.Multiple())
        {
            await Assert.That(centres.Centre("Ramp", -512)).IsEqualTo((50d, 50d));
            await Assert.That(centres.Centre("Ramp", -2048)).IsEqualTo((1050d, 50d));
            await Assert.That(centres.Centre("Ramp", 0)).IsEqualTo((550d, 50d)).Because("no areas on that floor: over all of them");
            await Assert.That(centres.Centre("Hut", -2048)).IsEqualTo((300d, 100d));
            await Assert.That(centres.Centre("Nowhere", -512)).IsNull();
        }
    }

    [Test]
    public async Task TheCanvas_FacesTheWatchedPlace_OnceTheZonesLand()
    {
        StratDocument document = Watching(new StepWatch { Places = ["Ramp"] });
        TaskCompletionSource<IZonePlaceResolver?> zones = new();
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, StratTestKeymap.Shipped, readOnly: true,
            placesFor: _ => zones.Task, post: a => a());

        await Assert.That(YawAt(canvas.Tracks.Get("B")!, Two)).IsEqualTo(10f).Because("no centres until the zones load");

        // StratMapFirstTests' synthetic map: Ramp is the square from (100, 0) to (200, 100), on a floor B is not on.
        zones.SetResult(StratMapFirstTests.SyntheticZones());
        await Assert.That(YawAt(canvas.Tracks.Get("B")!, Two)).IsEqualTo(18.43f);
    }
}
