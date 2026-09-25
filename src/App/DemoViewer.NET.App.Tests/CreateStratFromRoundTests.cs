#region

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Avalonia.Media;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Create Strat From Round (step-authoring.md §3.9) over synthetic captures: the cadence of O-25 (freeze-end,
///     our utility, the plant, the 10 s sweep at 200 units), the demo-to-slot precedence of strat-model.md §3.5, the
///     throw arrows, what a Round Facts row adds when the demo has one, and the review that saves the strat. The
///     tracker walk itself runs on real demos in <see cref="CreateStratFromRoundRealDemoTests" />.
/// </summary>
[NotInParallel]
public class CreateStratFromRoundTests
{
    private const int Freeze = 6400;
    private const int Rate = 64;

    // Ours are slots 0 to 4 (T), theirs 5 to 9 (CT); SteamIDs are 100 + slot.
    private static CapturedPawn Pawn(int slot, float x, float y, float z = 0, string? place = "TSpawn") =>
        new(slot, slot < 5 ? 2 : 3, (ulong)(100 + slot), "player" + slot, x, y, z, 90, place);

    private static List<CapturedPawn> Everyone(Func<int, (float X, float Y)>? at = null) =>
        [.. Enumerable.Range(0, 10).Select(s => at?.Invoke(s) is { } p ? Pawn(s, p.X, p.Y) : Pawn(s, s * 1000, 0))];

    private static int At(double seconds) => Freeze + (int)(seconds * Rate);

    private static RoundCapture Round(params CaptureMoment[] moments) =>
        new(7, Freeze, At(100), Rate, [new CaptureMoment(Freeze, CaptureTrigger.FreezeEnd, Everyone()), .. moments]);

    private static StratCaptureOptions Options(RoundCapture capture, int side = 2, bool arrows = true) =>
        new(side, StratFromRound.Tokens(capture.FreezeEnd.Pawns, side,
                StratFromRound.SlotMap(capture.FreezeEnd.Pawns.Where(p => p.Team == side), null, null)),
            StratClock.DefaultRoundSeconds, arrows, StratFromRound.QuantizedLevel);

    [Test]
    public async Task TheSweep_RunsEveryTenSeconds_ExceptWithinThreeOfAnotherStop()
    {
        // Stops at 8 s and 21.5 s: the 10 s sweep is 2 s from one and goes, 20 s is 1.5 s from the other and
        // goes, 30 s stays, and nothing is swept at or after the end.
        IReadOnlyList<int> sweeps = RoundCaptureWalker.SweepTicks([At(8), At(21.5)], Freeze, At(45), Rate);

        await Assert.That(sweeps).IsEquivalentTo(new[] { At(30), At(40) });
    }

    [Test]
    public async Task TheCadence_WritesFreezeEnd_OurThrows_ThePlant_AndMovesPastTwoHundredUnits()
    {
        // At 12 s slot 1 throws a smoke having moved 300 units; slot 2 moved 150 (no entry), slot 6 (theirs)
        // moved 250 (an entry). Their flash at 15 s is not our step. A fire with no owner at 18 s is kept with its
        // actor left for the user. Slot 3 plants at 40 s. The 30 s sweep finds nobody moved; the 50 s one finds
        // slot 4 alone, at 400 units.
        List<CapturedPawn> moved = Everyone(s => s switch
        {
            1 => (1300, 0),
            2 => (2150, 0),
            6 => (6250, 0),
            _ => (s * 1000, 0)
        });
        List<CapturedPawn> late = [.. moved.Select(p => p.PlayerSlot == 4 ? Pawn(4, 4400, 0, place: "BombsiteB") : p)];
        List<CapturedPawn> planting = [.. moved.Select(p => p.PlayerSlot == 3 ? p with { Place = "BombsiteB" } : p)];
        RoundCapture capture = Round(
            new CaptureMoment(At(12), CaptureTrigger.Utility, moved, "smoke", 1, 2, new Vector3(1500, 900, 64)),
            new CaptureMoment(At(15), CaptureTrigger.Utility, moved, "flash", 7, 3, new Vector3(0, 0, 0)),
            new CaptureMoment(At(18), CaptureTrigger.Utility, moved, "molotov", -1, 0, new Vector3(10, 20, 0)),
            new CaptureMoment(At(30), CaptureTrigger.Sweep, moved),
            new CaptureMoment(At(40), CaptureTrigger.Plant, planting, null, 3, 2),
            new CaptureMoment(At(50), CaptureTrigger.Sweep, late));

        List<StratStep> steps = StratFromRound.Steps(capture, Options(capture));

        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", steps.Select(s => s.Verb))).IsEqualTo("hold,throw,throw,plant,move");
            await Assert.That(string.Join(",", steps.Select(s => s.AtSeconds.ToString(CultureInfo.InvariantCulture)))).IsEqualTo("115,103,97,75,65");

            // Freeze-end: all ten, ours A..E and theirs O1..O5, in token order.
            await Assert.That(string.Join(",", steps[0].Positions.Select(p => p.Slot))).IsEqualTo("A,B,C,D,E,O1,O2,O3,O4,O5");

            // Our smoke: the thrower and the 250-unit opponent; 150 units is not a move.
            await Assert.That(steps[1].Actor).IsEqualTo("B");
            await Assert.That(steps[1].Utility!.Kind).IsEqualTo("smoke");
            await Assert.That(steps[1].Utility!.Landing!.X).IsEqualTo(1500);
            await Assert.That(steps[1].Utility!.Landing!.LevelMinZ).IsEqualTo(64);
            await Assert.That(string.Join(",", steps[1].Positions.Select(p => p.Slot))).IsEqualTo("B,O2");
            await Assert.That(steps[1].From!.Place).IsEqualTo("TSpawn");

            // The ownerless fire: kept, actor "all", nobody moved since, no thrower so no arrow.
            await Assert.That(steps[2].Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(steps[2].Utility!.Kind).IsEqualTo("molotov");
            await Assert.That(steps[2].Positions).IsEmpty();
            await Assert.That(steps[2].Strokes).IsEmpty();

            // The plant: the planter only, headed for the site.
            await Assert.That(steps[3].Actor).IsEqualTo("D");
            await Assert.That(string.Join(",", steps[3].Positions.Select(p => p.Slot))).IsEqualTo("D");
            await Assert.That(steps[3].To!.Place).IsEqualTo("BombsiteB");

            // The 50 s sweep: one of ours moved, so it is that slot's move to where it went.
            await Assert.That(steps[4].Actor).IsEqualTo("E");
            await Assert.That(string.Join(",", steps[4].Positions.Select(p => p.Slot))).IsEqualTo("E");
            await Assert.That(steps[4].To!.Place).IsEqualTo("BombsiteB");
        }
    }

    [Test]
    public async Task AThrowArrow_RunsFromTheThrowerToTheLanding_OnTheThrowersFloor()
    {
        List<CapturedPawn> pawns = Everyone(s => s == 1 ? (100, 200) : (s * 1000, 0));
        RoundCapture capture = Round(new CaptureMoment(At(12), CaptureTrigger.Utility, pawns, "flash", 1, 2, new Vector3(900, 800, 300)));

        StratStep withArrows = StratFromRound.Steps(capture, Options(capture))[1];
        StratStep without = StratFromRound.Steps(capture, Options(capture, arrows: false))[1];
        JsonObject arrow = withArrows.Strokes.Single();
        JsonArray points = arrow["points"]!.AsArray();

        using (Assert.Multiple())
        {
            await Assert.That(without.Strokes).IsEmpty();
            await Assert.That(arrow["kind"]!.GetValue<string>()).IsEqualTo("Arrow");
            await Assert.That(arrow["space"]!.GetValue<string>()).IsEqualTo("world");
            await Assert.That(arrow["levelMinZ"]!.GetValue<double>()).IsEqualTo(0);
            await Assert.That(string.Join(",", points.Select(p => p!.GetValue<float>().ToString(CultureInfo.InvariantCulture))))
                .IsEqualTo("100,200,0.5,900,800,0.5");
            await Assert.That(arrow.ContainsKey("fromTick")).IsFalse().Because("a stroke's time is its step's");
        }
    }

    [Test]
    public async Task TheSlotMap_TakesPins_ThenTheBookDefault_ThenControllerSlotOrder()
    {
        List<CapturedPawn> ours = [.. Everyone().Where(p => p.Team == 2)];
        List<StratSlot> pins = [new() { Slot = "D", SteamId = "104" }];
        Dictionary<string, string> defaults = new() { ["A"] = "103", ["D"] = "100", ["E"] = "999" };

        IReadOnlyDictionary<char, ulong> map = StratFromRound.SlotMap(ours, pins, defaults);

        using (Assert.Multiple())
        {
            await Assert.That(map['D']).IsEqualTo(104UL).Because("the pin wins over the book's 100");
            await Assert.That(map['A']).IsEqualTo(103UL).Because("the book default");
            await Assert.That(map['B']).IsEqualTo(100UL).Because("the rest in controller-slot order");
            await Assert.That(map['C']).IsEqualTo(101UL);
            await Assert.That(map['E']).IsEqualTo(102UL).Because("a default naming nobody in the round is skipped");
        }
    }

    [Test]
    public async Task TheDocument_IsAnExecuteAtThePlantSite_AndReadsRoundFactsWhenThereIsARow()
    {
        List<CapturedPawn> planting = [.. Everyone().Select(p => p.PlayerSlot == 3 ? p with { Place = "BombsiteA" } : p)];
        RoundCapture capture = Round(new CaptureMoment(At(40), CaptureTrigger.Plant, planting, null, 3, 2));
        StratOrigin origin = new() { DemoSha256 = "ab", Round = 7, FileName = "x.dem" };
        DateTime now = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

        StratDocument walked = StratFromRound.Document(capture, Options(capture), StratOwner.Me(), "de_mirage", "r7", origin, null, now);
        RoundFacts row = new() { Number = 7, PlantTick = At(40), PlantSite = BombSite.B, RoundTimeSeconds = 115 };
        row.T.BuyType = BuyType.Force;
        StratDocument withRow = StratFromRound.Document(capture, Options(capture), StratOwner.Me(), "de_mirage", "r7", origin, row, now);
        StratDocument ct = StratFromRound.Document(capture, Options(capture, side: 3), StratOwner.Me(), "de_mirage", "r7", origin, null, now);

        using (Assert.Multiple())
        {
            await Assert.That(walked.Type).IsEqualTo("execute");
            await Assert.That(walked.TargetSite).IsEqualTo("A").Because("the planter stood in BombsiteA");
            await Assert.That(walked.Economy).IsNull().Because("no row, no buy type");
            await Assert.That(walked.Origin).IsSameReferenceAs(origin);
            await Assert.That(walked.Slots.Select(s => s.Slot)).IsEquivalentTo(StratVocabulary.Slots);
            await Assert.That(withRow.TargetSite).IsEqualTo("B").Because("the row's plant site wins");
            await Assert.That(withRow.Economy).IsEqualTo("force");
            await Assert.That(ct.Side).IsEqualTo(StratVocabulary.SideCt);
            await Assert.That(ct.Type).IsEqualTo("setup");
            await Assert.That(StratValidator.Validate(walked).Where(i => i.Severity == StratIssueSeverity.Refusal)).IsEmpty();
            await Assert.That(StratSceneProjection.Build(walked, StratPath.MainLine(walked)).Tracks.Count).IsEqualTo(10);
        }
    }

    [Test]
    public async Task ALevelKey_IsTheBandsQuantizedFloor_TheNearestBandInAGap_OrTheZWithNoLevels()
    {
        MapLevel lower = new() { Id = MapSpace.IdForZMin(-500), Name = "lower", ZMin = -500, ZMax = -300 };
        MapLevel upper = new() { Id = MapSpace.IdForZMin(-100), Name = "upper", ZMin = -100, ZMax = 200 };
        Func<double, double> keys = StratFromRound.LevelKeys([lower, upper]);

        using (Assert.Multiple())
        {
            await Assert.That(keys(-400)).IsEqualTo(MapSpace.QuantizeZ(-500));
            await Assert.That(keys(50)).IsEqualTo(MapSpace.QuantizeZ(-100));
            await Assert.That(keys(-280)).IsEqualTo(MapSpace.QuantizeZ(-500)).Because("nearer the lower band's middle");
            await Assert.That(StratFromRound.LevelKeys(null)(100)).IsEqualTo(MapSpace.QuantizeZ(100));
        }
    }

    [Test]
    public async Task OurSide_IsTheSideHoldingMostOfTheKey_AndNoneOnATie()
    {
        List<CapturedPawn> pawns = Everyone();

        using (Assert.Multiple())
        {
            await Assert.That(CreateStratDialogViewModel.OurSideAt(pawns, ["100", "101", "102", "999"])).IsEqualTo(2);
            await Assert.That(CreateStratDialogViewModel.OurSideAt(pawns, ["105", "106", "107"])).IsEqualTo(3);
            await Assert.That(CreateStratDialogViewModel.OurSideAt(pawns, ["100", "105"])).IsNull();
            await Assert.That(CreateStratDialogViewModel.OurSideAt(pawns, [])).IsNull();
        }
    }

    [Test]
    public async Task TheReview_TakesTheSideFromTeamIdentity_SavesTheStrat_AndSeedsTheBooksDefault()
    {
        StratStore store = new(null);
        RoundCapture capture = Round(new CaptureMoment(At(12), CaptureTrigger.Utility, Everyone(), "smoke", 1, 2, new Vector3(1, 2, 3)));
        Guid? created = null;
        using CreateStratDialogViewModel review = new(Request(["100", "101", "102"]), (_, _) => capture, store);
        review.StratCreated += id => created = id;
        await review.Walking;

        using (Assert.Multiple())
        {
            await Assert.That(review.SelectedSide).IsEqualTo(StratVocabulary.SideT);
            await Assert.That(string.Join(",", review.Slots.Select(s => s.Selected!.PlayerSlot))).IsEqualTo("0,1,2,3,4");
            await Assert.That(review.StepLines.Count).IsEqualTo(2);
            await Assert.That(review.StepLines[1]).StartsWith("1:43  B throw smoke");
            await Assert.That(review.CanCreate).IsTrue();
        }

        // Moving player 3 into slot A swaps the two: A and D trade players, and the steps follow.
        review.Slots[0].Selected = review.Slots[3].Selected;
        await Assert.That(string.Join(",", review.Slots.Select(s => s.Selected!.PlayerSlot))).IsEqualTo("3,1,2,0,4");

        review.CreateCommand.Execute(null);
        StratDocument saved = store.TryLoad(created!.Value)!;
        StratBook book = store.LoadBook(StratOwner.Me());

        using (Assert.Multiple())
        {
            await Assert.That(saved.Revision).IsEqualTo(1);
            await Assert.That(saved.Origin!.Round).IsEqualTo(7);
            await Assert.That(saved.Steps.Count).IsEqualTo(2);
            await Assert.That(saved.Steps[0].Positions.Single(p => p.Slot == "A").X).IsEqualTo(3000);
            await Assert.That(book.SlotDefaults["me"]["A"]).IsEqualTo("103").Because("the first mapping of an epoch seeds it");
        }
    }

    [Test]
    public async Task TheReview_WithoutATeam_WaitsForTheSidePicker()
    {
        RoundCapture capture = Round();
        using CreateStratDialogViewModel review = new(Request([]), (_, _) => capture, new StratStore(null));
        await review.Walking;

        using (Assert.Multiple())
        {
            await Assert.That(review.SelectedSide).IsNull();
            await Assert.That(review.SideNote).Contains("pick the side");
            await Assert.That(review.CanCreate).IsFalse();
            await Assert.That(review.Slots).IsEmpty();
        }

        review.SelectedSide = StratVocabulary.SideCt;

        using (Assert.Multiple())
        {
            await Assert.That(review.CanCreate).IsTrue();
            await Assert.That(string.Join(",", review.Slots.Select(s => s.Selected!.PlayerSlot))).IsEqualTo("5,6,7,8,9");
        }
    }

    [Test]
    public async Task TheReview_SaysSo_WhenTheWalkFails()
    {
        using CreateStratDialogViewModel review = new(Request([]), (_, _) => throw new InvalidOperationException("no"),
            new StratStore(null));
        await review.Walking;

        using (Assert.Multiple())
        {
            await Assert.That(review.StatusLine).IsEqualTo("this round could not be read");
            await Assert.That(review.CanCreate).IsFalse();
        }
    }

    [Test]
    public async Task TheStratBook_OpensTheCreatedStrat_UnderItsBookAndMap()
    {
        StratStore store = new(null);
        RoundCapture capture = Round();
        StratDocument made = StratFromRound.Document(capture, Options(capture), StratOwner.Me(), "de_mirage", "r7",
            new StratOrigin { Round = 7 }, null, DateTime.UtcNow);
        store.Save(made, [], "created");
        using StratBookTabViewModel tab = new(store, null, null, false);
        tab.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        tab.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        tab.SelectedMap = "de_nuke";

        tab.OpenStrat(made.Id);

        using (Assert.Multiple())
        {
            await Assert.That(tab.Session.Document?.Id).IsEqualTo(made.Id);
            await Assert.That(tab.SelectedMap).IsEqualTo("de_mirage");
            await Assert.That(tab.SelectedStrat?.Id).IsEqualTo(made.Id);
        }
    }

    [Test]
    public async Task TheRoundBand_OffersTheCapture_OnlyForARoundAndOnlyWhenTheTabCan()
    {
        Playback2DTimelineViewModel timeline = new();
        List<TimelineBandViewModel> asked = [];
        timeline.CreateStratRequested += asked.Add;
        TimelineBandViewModel round = Band("round", "7");
        TimelineBandViewModel warmup = Band("round", "wu");

        timeline.RequestCreateStrat(round);
        timeline.CanCreateStrat = true;
        timeline.RequestCreateStrat(warmup);
        timeline.RequestCreateStrat(Band("tags", "3"));
        timeline.RequestCreateStrat(round);

        await Assert.That(asked.Count).IsEqualTo(1);
        await Assert.That(asked[0]).IsSameReferenceAs(round);
    }

    private static TimelineBandViewModel Band(string track, string label) =>
        new(new TimelineBand(track, 0, 10, label, label, 0), 0, 1, Brushes.Gray);

    private static StratCaptureRequest Request(IReadOnlyCollection<string> key) =>
        new("de_mirage", 7, Freeze, At(120), "ab", "match.dem", null, key, StratOwner.Me(), "me", StratOwner.MeKind,
            StratFromRound.QuantizedLevel);
}
