#region

using System.Text.Json;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using SkiaSharp;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using static DemoViewer.NET.AppTests.StratStepEditingTests;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The step row's lines: adding and removing players, a line's place and watching, the stored shape each edit
///     leaves (no lines is for everyone, one bare line is a plain step), the verb's rules per line, one undo entry
///     per change, and the canvas's Set On Map, cone drag and new step working on lines.
/// </summary>
[NotInParallel]
public class StratLineEditingTests
{
    private static string Shape(StratStep step) => JsonSerializer.Serialize(step, StratJsonContext.Default.StratStep);

    private static StratStep Doc(StratBookTabViewModel vm, int index) => vm.Session.Document!.Steps[index];

    [Test]
    public async Task AddingAndRemovingPlayers_KeepsTheStoredShape_OneEntryEach()
    {
        using StratBookTabViewModel vm = OpenNew();
        Seed(vm, Step(100, "all", "move"));
        int step = vm.Session.Document!.Steps.Count - 1;
        string original = Shape(Doc(vm, step));
        int depth = vm.Session.UndoDepth;

        // A step for everyone gets its first player: a plain one-player step with the step's place.
        vm.Editor.AddLineCommand.Execute(vm.Editor.Steps[step]);
        using (Assert.Multiple())
        {
            await Assert.That(Doc(vm, step).Actor).IsEqualTo("A");
            await Assert.That(Doc(vm, step).To!.Place).IsEqualTo("BombsiteA");
            await Assert.That(Doc(vm, step).Assignments).IsNull();
            await Assert.That(vm.Editor.Steps[step].Lines.Single().IsImplicit).IsTrue();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
        }

        // A second player makes lines: the summary actor is all and the step's place moves into A's line.
        vm.Editor.AddLineCommand.Execute(vm.Editor.Steps[step]);
        StratStep two = Doc(vm, step);
        using (Assert.Multiple())
        {
            await Assert.That(two.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(two.To).IsNull();
            await Assert.That(two.Assignments!.Select(l => l.Slot + ">" + l.To?.Place)).IsEquivalentTo(["A>BombsiteA", "B>"]);
            await Assert.That(vm.Editor.Steps[step].CanEditActor).IsFalse();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 2);
        }

        // Back to one bare line: the plain shape, actor rewritten to the one left.
        vm.Editor.Steps[step].Lines[0].RemoveCommand.Execute(null);
        StratStep one = Doc(vm, step);
        using (Assert.Multiple())
        {
            await Assert.That(one.Actor).IsEqualTo("B");
            await Assert.That(one.Assignments).IsNull();
            await Assert.That(Shape(one)).DoesNotContain("assignments");
        }

        // The implicit line's place is the step's to; the actor combo back to all restores the file byte for byte.
        vm.Editor.Steps[step].Lines.Single().PlaceText = "BombsiteA";
        await Assert.That(Doc(vm, step).To?.Place).IsEqualTo("BombsiteA");
        vm.Editor.Steps[step].Actor = StratVocabulary.ActorAll;
        await Assert.That(Shape(Doc(vm, step))).IsEqualTo(original);
    }

    [Test]
    public async Task Watching_StoresCanonicalPlaces_ShowsCallouts_AndOnePlayerWithoutItStaysPlain()
    {
        using StratBookTabViewModel vm = OpenNew();
        Seed(vm, Step(100, "B", "move"));
        int step = vm.Session.Document!.Steps.Count - 1;
        string plain = Shape(Doc(vm, step));

        StratLineRow line = vm.Editor.Steps[step].Lines.Single();
        line.WatchText = "Bombsite A, ct spawn, Nowhere";
        StratStep watching = Doc(vm, step);
        using (Assert.Multiple())
        {
            await Assert.That(watching.Actor).IsEqualTo("B");
            await Assert.That(watching.To).IsNull().Because("the place moves into the line");
            await Assert.That(watching.Assignments!.Single().To!.Place).IsEqualTo("BombsiteA");
            await Assert.That(watching.Assignments!.Single().Watch!.Places).IsEquivalentTo(["BombsiteA", "CTSpawn", "Nowhere"]);
            await Assert.That(vm.Editor.Steps[step].Lines.Single().WatchText).IsEqualTo("Bombsite A, CT Spawn, Nowhere");
            await Assert.That(vm.Editor.Steps[step].Lines.Single().IsExplicit).IsTrue();
        }

        vm.Editor.Steps[step].Lines.Single().WatchText = "";
        await Assert.That(Shape(Doc(vm, step))).IsEqualTo(plain).Because("one player, no watch: the plain shape again");
    }

    [Test]
    public async Task ALineSlotCombo_WheeledThroughAndBack_LeavesNoEntry_AndTheVerbClearsLinePlacesInOneEntry()
    {
        using StratBookTabViewModel vm = OpenNew();
        Seed(vm, Step(100, "all", "move"));
        int step = vm.Session.Document!.Steps.Count - 1;
        vm.Editor.AddLineCommand.Execute(vm.Editor.Steps[step]);
        vm.Editor.AddLineCommand.Execute(vm.Editor.Steps[step]);
        vm.Editor.Steps[step].Lines[1].PlaceText = "Connector";
        string before = Shape(Doc(vm, step));
        int depth = vm.Session.UndoDepth;

        vm.Editor.Steps[step].Lines[1].Slot = "C";
        vm.Editor.Steps[step].Lines[1].Slot = "D";
        await Assert.That(Doc(vm, step).Assignments![1].Slot).IsEqualTo("D");
        await Assert.That(vm.Editor.Steps[step].Lines[1].SlotOptions).DoesNotContain("A").Because("A has a line");
        vm.Editor.Steps[step].Lines[1].Slot = "B";
        vm.Editor.ChangeLineSlot(step, 1, "A");
        using (Assert.Multiple())
        {
            await Assert.That(Shape(Doc(vm, step))).IsEqualTo(before);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth).Because("the burst came back to where it began");
        }

        vm.Editor.EndEditBurst();
        vm.Editor.Steps[step].Verb = "wait";
        StratStep wait = Doc(vm, step);
        using (Assert.Multiple())
        {
            await Assert.That(wait.Assignments!.All(l => l.To is null)).IsTrue();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(vm.Editor.Steps[step].Lines.All(l => !l.ShowPlace)).IsTrue();
        }
    }

    [Test]
    public async Task InlineChecks_MarkTheLineField_TheyPointAt()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStep step = Step(100, "all", "move");
        step.To = null;
        step.Assignments =
        [
            new StepAssignment { Slot = "B", To = new PlaceRef { Place = "Connector" } },
            new StepAssignment { Slot = "B" }
        ];
        Seed(vm, step);
        StratStepRow row = vm.Editor.Steps[^1];
        using (Assert.Multiple())
        {
            await Assert.That(row.Lines[1].SlotIssue?.IsRefusal).IsTrue();
            await Assert.That(row.Lines[1].PlaceIssue?.Message).Contains("destination");
            await Assert.That(row.Lines[0].SlotIssue).IsNull();
            await Assert.That(row.Lines[0].PlaceIssue).IsNull();
            await Assert.That(row.ToIssue).IsNull();
            await Assert.That(row.RowIssue).IsNull().Because("line issues mark the line, not the row");
            await Assert.That(row.IssueText).Contains("(B to)");
        }
    }

    [Test]
    public async Task SetOnMap_WritesTheSelectedLine()
    {
        StratDocument document = FiveSteps();
        document.Steps[1].Actor = StratVocabulary.ActorAll;
        document.Steps[1].To = null;
        document.Steps[1].Assignments = [new StepAssignment { Slot = "A" }, new StepAssignment { Slot = "C" }];
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(StratMapFirstTests.SyntheticZones()), post: a => a());
        canvas.SelectStep(document.Steps[1].Id);

        await Assert.That(canvas.SelectedLineSlot).IsEqualTo("A").Because("the first line until one is chosen");
        canvas.SelectLine("C");
        await Assert.That(canvas.SetPlaceText).IsEqualTo("Set C's “to” on map");
        await Assert.That(canvas.BeginSetPlace()).IsTrue();
        canvas.TryTagPositionAt(StratMapFirstTests.Upper, 150, 50);

        StratStep written = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(written.Assignments!.Select(l => l.Slot + ">" + l.To?.Place)).IsEquivalentTo(["A>", "C>Ramp"]);
            await Assert.That(written.To).IsNull();
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ACone_IsHitAroundTheToken_ItsDragSetsTheAngle_AndClearAngleFacesThePlaceAgain()
    {
        StratDocument document = FiveSteps();
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [], post: a => a());
        canvas.SelectStep(document.Steps[0].Id);

        // B at (100, 0) facing 0: well inside its cone, off its stub, is a turn; the disc is a move.
        TokenGrip? cone = TokenHitTest.Classify(100, 0, 0, new SKPoint(100 + 25, 8), 5, true);
        TokenGrip? noCone = TokenHitTest.Classify(100, 0, 0, new SKPoint(100 + 25, 8), 5);
        using (Assert.Multiple())
        {
            await Assert.That(cone).IsEqualTo(TokenGrip.Heading);
            await Assert.That(noCone).IsNull();
            await Assert.That(canvas.ShowViewCones).IsTrue();
        }

        canvas.BeginDrag("B", TokenGrip.Heading);
        canvas.MoveTo("B", new SKPoint(100, 100), 0);
        canvas.EndDrag(null);
        StratStep step = session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(step.Assignments).IsNotNull().Because("a step for all expands to a line per slot");
            await Assert.That(step.Assignments!.Count).IsEqualTo(5);
            await Assert.That(step.Assignments!.Single(l => l.Slot == "B").Watch!.YawDegrees).IsEqualTo(90);
            await Assert.That(step.Assignments!.Where(l => l.Slot != "B").All(l => l.To?.Place == "BombsiteA")).IsTrue();
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }

        // Clearing the angle folds the five bare lines back into the step for all it was.
        List<PatchOp> clear = StratLinePatches.WatchYaw(step, "/steps/0", "B", null);
        session.Apply(clear);
        StratStep back = session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(back.Assignments).IsNull();
            await Assert.That(back.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(back.To!.Place).IsEqualTo("BombsiteA");
        }
    }

    [Test]
    public async Task ClearAngle_OnALine_DropsOnlyTheAngle()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStep step = Step(100, "all", "hold");
        step.To = null;
        step.Assignments =
        [
            new StepAssignment { Slot = "B", Watch = new StepWatch { Places = ["BombsiteA"], YawDegrees = 135 } },
            new StepAssignment { Slot = "C" }
        ];
        Seed(vm, step);
        StratLineRow line = vm.Editor.Steps[^1].Lines[0];
        await Assert.That(line.AngleText).IsEqualTo("135°");
        int depth = vm.Session.UndoDepth;

        line.ClearAngleCommand.Execute(null);
        StepWatch watch = vm.Session.Document!.Steps[^1].Assignments![0].Watch!;
        using (Assert.Multiple())
        {
            await Assert.That(watch.YawDegrees).IsNull();
            await Assert.That(watch.Places).IsEquivalentTo(["BombsiteA"]);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(vm.Editor.Steps[^1].Lines[0].HasAngle).IsFalse();
        }
    }

    [Test]
    public async Task TheEditorsAddStep_CarriesAWatchingTokensFacing()
    {
        StratDocument document = FiveSteps();
        document.Steps[0].Actor = "B";
        document.Steps[0].To = null;
        document.Steps[0].Assignments = [new StepAssignment { Slot = "B", Watch = new StepWatch { Places = ["Ramp"] } }];
        (StratStore _, StratSession session) = Opened(document);
        StratEditorViewModel editor = new(session, placeCentres: (place, _) => place == "Ramp" ? (100, 100) : null);
        editor.Project();

        editor.AddStepCommand.Execute(editor.Steps[0]);
        StepPosition b = session.Document!.Steps[1].Positions.Single(p => p.Slot == "B");
        await Assert.That(b.YawDegrees).IsEqualTo(90).Because("B stands at (100, 0), Ramp is due north of it");
    }
}
