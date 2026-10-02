#region

using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using SkiaSharp;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A token drag edits the step's own fields (docs/strat-book/drag-semantics.md, option A): per verb the field the row
///     shows, as one undo entry; Alt pins, Shift stores a point, Esc writes nothing; paused between steps a run gets a via
///     and a standing token edits the step that placed it; and the ghost and label say what the release stores.
/// </summary>
[NotInParallel]
public class StratDragFieldsTests
{
    // The synthetic map's upper floor: Hut is the square at the origin, Ramp the one to its right, arrivals at their centres.
    private const double Floor = -512;
    private const int S1 = 320, S2 = 960;

    private static StepPosition At(string slot, double x, double y) => new() { Slot = slot, X = x, Y = y, LevelMinZ = Floor };

    private static StratStep Step(double at, string actor, string verb, PlaceRef? to = null) =>
        new() { Id = Guid.NewGuid(), AtSeconds = at, Actor = actor, Verb = verb, To = to };

    private static PlaceRef Place(string place) => new() { Place = place };

    // Everyone at spawn on a hold at 1:55, then the steps given.
    private static StratDocument Strat(params StratStep[] steps)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_synthetic", "T", "execute", "drag", Created);
        document.Canvas = new StratCanvas { DefaultLevelMinZ = Floor };
        StratStep seed = Step(115, StratVocabulary.ActorAll, "hold");
        seed.Positions = [At("A", 10, 10), At("B", 20, 10), At("C", 30, 10), At("D", 40, 10), At("E", 50, 10), At("O1", 190, 90)];
        document.Steps = [seed, .. steps];
        return document;
    }

    private static StratCanvasViewModel Canvas(StratSession session) =>
        new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(StratMapFirstTests.SyntheticZones()), post: a => a())
        {
            Timeline = { PixelWidth = 6000 }
        };

    private static string Json(StratSession session) => StratHistory.ToNode(session.Document!).ToJsonString();

    private static void Drag(StratCanvasViewModel canvas, string slot, double x, double y, ToolModifiers modifiers = ToolModifiers.None)
    {
        canvas.BeginDrag(slot, TokenGrip.Body);
        canvas.MoveTo(slot, new SKPoint((float)x - 5, (float)y - 5), Floor, modifiers);
        canvas.MoveTo(slot, new SKPoint((float)x, (float)y), Floor, modifiers);
        canvas.EndDrag(modifiers);
    }

    // One entry, and undo and redo give back the document byte for byte.
    private static async Task OneEntryByteExact(StratCanvasViewModel canvas, StratSession session, string before, int depth)
    {
        string after = Json(session);
        await Assert.That(session.UndoDepth).IsEqualTo(depth + 1);
        await Assert.That(canvas.ExecuteAction(Playback2DAction.Undo)).IsTrue();
        await Assert.That(Json(session)).IsEqualTo(before);
        await Assert.That(canvas.ExecuteAction(Playback2DAction.Redo)).IsTrue();
        await Assert.That(Json(session)).IsEqualTo(after);
    }

    [Test]
    [Arguments("move")]
    [Arguments("push")]
    [Arguments("rotate")]
    [Arguments("other")]
    public async Task ATravelVerb_TakesTheDropAsItsTo_PlaceAndPoint(string verb)
    {
        StratDocument document = Strat(Step(110, "A", verb, Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);
        string before = Json(session);

        Drag(canvas, "A", 20, 80);
        PlaceRef to = session.Document!.Steps[1].To!;
        using (Assert.Multiple())
        {
            await Assert.That(to.Place).IsEqualTo("Hut");
            await Assert.That(to.X).IsEqualTo(20);
            await Assert.That(to.Y).IsEqualTo(80);
            await Assert.That(to.LevelMinZ).IsEqualTo(Floor);
            await Assert.That(session.Document!.Steps[1].Positions).IsEmpty().Because("a drag never writes a hidden position");
            await Assert.That(canvas.Transport.Tick).IsEqualTo(S1);
        }

        await OneEntryByteExact(canvas, session, before, 0);
    }

    [Test]
    [Arguments("hold", "at")]
    [Arguments("peek", "at")]
    [Arguments("fake", "at")]
    [Arguments("plant", "site")]
    [Arguments("defuse", "site")]
    public async Task APositionVerb_TakesTheDropAsItsAtOrSite_AndDropsThePlayersOwnEntry(string verb, string word)
    {
        StratStep step = Step(110, "A", verb, Place("Ramp"));
        step.Positions = [At("B", 150, 50), At("A", 180, 60)];
        StratDocument document = Strat(step);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);
        string before = Json(session);

        canvas.BeginDrag("A", TokenGrip.Body);
        canvas.MoveTo("A", new SKPoint(20, 80), Floor);
        await Assert.That(canvas.DragLabel).IsEqualTo($"A · {word}: Hut");
        canvas.EndDrag();

        StratStep written = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(written.To!.Place).IsEqualTo("Hut");
            await Assert.That(written.Positions.Select(p => p.Slot)).IsEquivalentTo(["B"]).Because("the entry would beat the field");
            await Assert.That(canvas.StatusLine).Contains($"A's {word} is now Hut");
        }

        await OneEntryByteExact(canvas, session, before, 0);
    }

    [Test]
    public async Task NearAPlacesCentre_TheDropIsThePlaceAlone_AndShift_IsThePointAlone()
    {
        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);

        Drag(canvas, "A", 52, 49);
        PlaceRef snapped = session.Document!.Steps[1].To!;
        using (Assert.Multiple())
        {
            await Assert.That(snapped.Place).IsEqualTo("Hut");
            await Assert.That(snapped.X).IsNull().Because("tokens sent to one place fan out round it");
        }

        Drag(canvas, "A", 52, 49, ToolModifiers.Shift);
        PlaceRef point = session.Document!.Steps[1].To!;
        using (Assert.Multiple())
        {
            await Assert.That(point.Place).IsNull();
            await Assert.That(point.X).IsEqualTo(52);
            await Assert.That(point.Y).IsEqualTo(49);
            await Assert.That(session.UndoDepth).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Alt_PinsTheSpot_AsAPositionEntry_AndLeavesTheFieldAlone()
    {
        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);
        string before = Json(session);

        canvas.BeginDrag("A", TokenGrip.Body);
        canvas.MoveTo("A", new SKPoint(60, 70), Floor, ToolModifiers.Alt);
        await Assert.That(canvas.DragLabel).IsEqualTo("A · pinned: (60, 70)");
        canvas.EndDrag(ToolModifiers.Alt);

        StratStep step = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(step.To!.Place).IsEqualTo("Ramp");
            await Assert.That(step.To!.X).IsNull();
            await Assert.That(step.Positions.Single().Slot).IsEqualTo("A");
            await Assert.That(step.Positions.Single().X).IsEqualTo(60);
        }

        await OneEntryByteExact(canvas, session, before, 0);

        // The toolbar's Pin does what Alt does, for one drag.
        canvas.PinNextDrag = true;
        Drag(canvas, "A", 70, 70);
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[1].Positions.Single().X).IsEqualTo(70);
            await Assert.That(canvas.PinNextDrag).IsFalse();
        }
    }

    [Test]
    public async Task Esc_CancelsTheDrag_AndWritesNothing()
    {
        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);
        string before = Json(session);

        canvas.BeginDrag("A", TokenGrip.Body);
        canvas.MoveTo("A", new SKPoint(60, 70), Floor);
        await Assert.That(canvas.Guides.Ghost).IsNotNull();
        canvas.CancelDrag();
        using (Assert.Multiple())
        {
            await Assert.That(Json(session)).IsEqualTo(before);
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(canvas.DragLabel).IsEqualTo("");
            await Assert.That(canvas.Guides.Ghost).IsNull();
            await Assert.That(canvas.ArmedField).IsNull();
        }
    }

    [Test]
    public async Task ALurk_TakesTheDropAsAreaOne_KeepingTheOthersAfterIt()
    {
        StratStep lurk = Step(110, "E", "lurk");
        lurk.Lurk = new StepLurk { Areas = ["Ramp"] };
        StratDocument document = Strat(lurk);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);
        string before = Json(session);

        canvas.BeginDrag("E", TokenGrip.Body);
        canvas.MoveTo("E", new SKPoint(50, 50), Floor);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.DragLabel).IsEqualTo("E · lurk area 1: Hut");
            await Assert.That(canvas.ArmedField).IsEqualTo(new StratLocationField(lurk.Id, null, StratLocationKind.LurkArea));
        }

        canvas.EndDrag();
        await Assert.That(session.Document!.Steps[1].Lurk!.Areas).IsEquivalentTo(["Hut", "Ramp"]);
        await Assert.That(session.Document!.Steps[1].Lurk!.Areas[0]).IsEqualTo("Hut");
        await OneEntryByteExact(canvas, session, before, 0);

        // A place already listed moves to first.
        Drag(canvas, "E", 150, 50);
        await Assert.That(string.Join(",", session.Document!.Steps[1].Lurk!.Areas)).IsEqualTo("Ramp,Hut");
    }

    [Test]
    public async Task ALurkerWhoseRotateHasStarted_TakesTheDropAsRotateTo()
    {
        StratStep lurk = Step(110, "E", "lurk");
        lurk.Lurk = new StepLurk { Areas = ["Hut"], Rotate = new LurkRotate { AtSeconds = 105, To = Place("Ramp") } };
        StratDocument document = Strat(lurk);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);

        // E rotated at 1:45 (tick 640) and is walking to Ramp.
        const int end = 650;
        canvas.Timeline.RequestSeekToFrame(end);
        string before = Json(session);
        Drag(canvas, "E", 20, 80);
        StepLurk written = session.Document!.Steps[1].Lurk!;
        using (Assert.Multiple())
        {
            await Assert.That(written.Rotate!.To!.Place).IsEqualTo("Hut");
            await Assert.That(written.Rotate!.To!.X).IsEqualTo(20);
            await Assert.That(written.Areas).IsEquivalentTo(["Hut"]);
            await Assert.That(canvas.Transport.Tick).IsEqualTo(end);
        }

        await OneEntryByteExact(canvas, session, before, 0);
    }

    [Test]
    [Arguments("throw")]
    [Arguments("wait")]
    [Arguments("call")]
    public async Task AStillVerb_EditsTheStepThatPlacedThePlayer(string verb)
    {
        StratStep still = Step(100, "A", verb);
        if (verb == "throw")
        {
            still.Utility = new UtilityRef { Kind = "smoke" };
        }

        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")), still);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(still.Id);
        string before = Json(session);

        canvas.BeginDrag("A", TokenGrip.Body);
        canvas.MoveTo("A", new SKPoint(20, 80), Floor);
        await Assert.That(canvas.DragLabel).IsEqualTo("step 2 · A · to: Hut").Because("the label names the step it writes");
        canvas.EndDrag();

        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Hut");
            await Assert.That(session.Document!.Steps[2].Positions).IsEmpty();
            await Assert.That(canvas.Transport.Tick).IsEqualTo(S2).Because("the step it wrote is on another tick, so the playhead stays");
        }

        await OneEntryByteExact(canvas, session, before, 0);
    }

    [Test]
    public async Task AnOpponent_IsStillAPositionEntry()
    {
        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);
        string before = Json(session);

        Drag(canvas, "O1", 160, 40);
        StepPosition o1 = session.Document!.Steps[1].Positions.Single();
        using (Assert.Multiple())
        {
            await Assert.That(o1.Slot).IsEqualTo("O1");
            await Assert.That(o1.X).IsEqualTo(160);
            await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Ramp");
        }

        await OneEntryByteExact(canvas, session, before, 0);
    }

    [Test]
    public async Task PausedMidRun_TheDropIsAViaOnThatRun_AndThePlayheadStays()
    {
        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.Timeline.RequestSeekToFrame(S1 + 20);
        await Assert.That(canvas.Projection!.RunAt("A", S1 + 20)).IsNotNull();
        string before = Json(session);

        canvas.BeginDrag("A", TokenGrip.Body);
        canvas.MoveTo("A", new SKPoint(60, 90), Floor);
        await Assert.That(canvas.DragLabel).IsEqualTo("A · via +: Hut");
        canvas.EndDrag();
        StratStep step = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(step.Via).IsEquivalentTo(["Hut"]);
            await Assert.That(step.To!.Place).IsEqualTo("Ramp");
            await Assert.That(canvas.Transport.Tick).IsEqualTo(S1 + 20);
        }

        await OneEntryByteExact(canvas, session, before, 0);
    }

    [Test]
    public async Task PausedMidRun_OnAStepForEveryone_TheViaKeepsTheStepsOthers()
    {
        StratStep all = Step(110, StratVocabulary.ActorAll, "move", Place("Ramp"));
        all.Via = ["Hut"];
        StratDocument document = Strat(all);
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.Timeline.RequestSeekToFrame(S1 + 20);
        await Assert.That(canvas.Projection!.RunAt("A", S1 + 20)).IsNotNull();

        Drag(canvas, "A", 300, 300);
        StepAssignment a = session.Document!.Steps[1].Assignments!.Single(l => l.Slot == "A");
        using (Assert.Multiple())
        {
            await Assert.That(a.Via).IsEquivalentTo(["Hut"]);
            await Assert.That(a.ViaPoints!.Single().X).IsEqualTo(300);
            await Assert.That(session.Document!.Steps[1].Assignments!.Single(l => l.Slot == "B").Via).IsEquivalentTo(["Hut"]);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }
    }

    [Test]
    public async Task PausedWhileStanding_TheDragEditsTheStepThatPutThePlayerThere()
    {
        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")), Step(100, "B", "move", Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.Timeline.RequestSeekToFrame(S2 + 20);
        await Assert.That(canvas.ActiveStepIndex).IsEqualTo(2);
        await Assert.That(canvas.Projection!.RunAt("A", S2 + 20)).IsNull().Because("A has long arrived");

        Drag(canvas, "A", 20, 80);
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Hut");
            await Assert.That(session.Document!.Steps[2].To!.Place).IsEqualTo("Ramp");
            await Assert.That(StratStepLines.Involves(session.Document!.Steps[2], "A")).IsFalse();
            await Assert.That(canvas.Transport.Tick).IsEqualTo(S2 + 20);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }
    }

    [Test]
    public async Task WhileDragging_TheGhostRoute_RunsThroughWhatIsStored_AndAPinShowsAfterTheRelease()
    {
        StratDocument document = Strat(Step(110, "A", "move", Place("Ramp")));
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = Canvas(session);
        canvas.SelectStep(document.Steps[1].Id);

        canvas.BeginDrag("A", TokenGrip.Body);
        canvas.MoveTo("A", new SKPoint(52, 49), Floor);
        SceneGuides guides = canvas.Guides;
        using (Assert.Multiple())
        {
            await Assert.That(guides.Ghost!.Value.X).IsEqualTo(10f).Because("hollow where A stood");
            await Assert.That(guides.GhostRoute.Count).IsGreaterThanOrEqualTo(2);
            await Assert.That(guides.GhostRoute[^1].X).IsEqualTo(50f).Because("a snapped drop stores the place: the route ends at its arrival");
            await Assert.That(guides.GhostRoute[^1].Y).IsEqualTo(50f);
            await Assert.That(guides.DropOutline.Count).IsGreaterThan(0);
            await Assert.That(canvas.DragLabel).IsEqualTo("A · to: Hut");
            await Assert.That(canvas.DragHint).Contains("Shift: point only");
            await Assert.That(canvas.DragHint).Contains("Alt: pin A here");
            await Assert.That(canvas.ArmedField).IsEqualTo(new StratLocationField(document.Steps[1].Id, StratLocationField.AllLines, StratLocationKind.To));
            await Assert.That(canvas.CurrentFrame.Markers.Single(m => m.Slot == 0).WorldX).IsEqualTo(52f);
        }

        canvas.EndDrag();
        GuidePin pin = canvas.Guides.Pins.Single();
        using (Assert.Multiple())
        {
            await Assert.That(pin.At.Label).IsEqualTo("A");
            await Assert.That(pin.At.X).IsEqualTo(50f);
            await Assert.That(pin.At.Y).IsEqualTo(50f);
            await Assert.That(canvas.Guides.Ghost).IsNull();
            await Assert.That(canvas.DragLabel).IsEqualTo("");
            await Assert.That(canvas.ArmedField).IsNull();
        }
    }
}
