#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The step table's structural edits and inline checks: Duplicate, a new step that carries the tokens where
///     they stand, and the validator's issues on the row and field they name.
/// </summary>
[NotInParallel]
public class StratStepEditingTests
{
    internal static StratBookTabViewModel OpenNew()
    {
        StratBookTabViewModel vm = new(new StratStore(null), null, null, false);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        return vm;
    }

    internal static StratStep Step(double atSeconds, string actor, string verb, params StepPosition[] positions) => new()
    {
        Id = Guid.NewGuid(), AtSeconds = atSeconds, Actor = actor, Verb = verb, Positions = [.. positions],
        To = verb == "move" ? new PlaceRef { Place = "BombsiteA" } : null
    };

    internal static StepPosition At(string slot, double x, double y = 0) => new() { Slot = slot, X = x, Y = y, LevelMinZ = 0 };

    internal static void Seed(StratBookTabViewModel vm, params StratStep[] steps)
    {
        foreach (StratStep step in steps)
        {
            vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)));
        }
    }

    [Test]
    public async Task Duplicate_IsOneEntryAfterTheSource_WithAFreshIdAndItsFieldsPositionsAndStrokes()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStep source = Step(90, "B", "move", At("B", 100, 200));
        source.Note = "wide swing";
        source.Strokes.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["kind"] = "arrow" });
        Seed(vm, Step(100, "all", "move"), source, Step(88, "C", "move"));
        int depth = vm.Session.UndoDepth;
        Guid? focused = null;
        vm.Editor.StepFocusRequested += id => focused = id;

        vm.Editor.DuplicateStepCommand.Execute(vm.Editor.Steps[1]);

        StratDocument document = vm.Session.Document!;
        StratStep copy = document.Steps[2];
        using (Assert.Multiple())
        {
            await Assert.That(document.Steps.Count).IsEqualTo(4);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(copy.Id).IsNotEqualTo(source.Id);
            await Assert.That(document.Steps[1].Id).IsEqualTo(source.Id);
            await Assert.That(focused).IsEqualTo(copy.Id);
            await Assert.That(copy.Actor).IsEqualTo("B");
            await Assert.That(copy.Note).IsEqualTo("wide swing");
            await Assert.That(copy.To?.Place).IsEqualTo("BombsiteA");
            await Assert.That(copy.Positions.Single().X).IsEqualTo(100);
            await Assert.That(copy.Strokes.Single()["kind"]?.GetValue<string>()).IsEqualTo("arrow");
            await Assert.That(copy.Strokes.Single()["id"]?.GetValue<string>()).IsNotEqualTo(source.Strokes[0]["id"]!.GetValue<string>())
                .Because("two strokes with one id cannot both draw");
            await Assert.That(copy.AtSeconds).IsEqualTo(88).Because("5 s later would pass the next step at 1:28");
            await Assert.That(vm.Session.Issues.Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
        }

        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ANewStep_CarriesEveryTokenWhereItStands_AsACopy_ButNoStrokes()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStep first = Step(110, "all", "hold", At("A", 1), At("B", 2), At("O1", 9));
        first.Strokes.Add(new JsonObject { ["kind"] = "arrow" });
        StratStep throwing = Step(100, "B", "throw", At("C", 3));
        throwing.Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        Seed(vm, first, throwing, Step(80, "all", "move"));
        int depth = vm.Session.UndoDepth;

        vm.Editor.AddStepCommand.Execute(vm.Editor.Steps[1]);

        StratStep added = vm.Session.Document!.Steps[2];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(added.AtSeconds).IsEqualTo(95);
            await Assert.That(added.Strokes).IsEmpty();
            await Assert.That(added.Positions.Select(p => $"{p.Slot}={p.X}")).IsEquivalentTo(["A=1", "C=3", "O1=9"])
                .Because("B throws a lineup at the step before: it stays at the lineup origin, which is not written");
        }

        // A copy: dragging A on the first step leaves the new step's A where it was.
        vm.Session.Apply(PatchOp.ReplaceOp("/steps/0/positions/0/x", null, JsonValue.Create(500.0)));
        await Assert.That(vm.Session.Document!.Steps[2].Positions.Single(p => p.Slot == "A").X).IsEqualTo(1);
    }

    [Test]
    public async Task ANewStep_GoesAtTheEndWithoutARow_FiveSecondsOn_AndIsNeverRefused()
    {
        using StratBookTabViewModel vm = OpenNew();
        vm.Editor.AddStepCommand.Execute(null);
        await Assert.That(vm.Session.Document!.Steps.Single().AtSeconds).IsEqualTo(vm.Session.Document.Clock.RoundSeconds);

        Seed(vm, Step(-58, "E", "move"));
        vm.Editor.AddStepCommand.Execute(null);
        StratDocument document = vm.Session.Document!;
        using (Assert.Multiple())
        {
            await Assert.That(document.Steps.Count).IsEqualTo(3);
            await Assert.That(document.Steps[^1].AtSeconds).IsEqualTo(StratValidator.EarliestAfterTimerSeconds);
            await Assert.That(document.Steps[^1].Positions).IsEmpty();
            await Assert.That(vm.Session.Issues.Where(i => i.Severity == StratIssueSeverity.Refusal)).IsEmpty();
        }
    }

    [Test]
    public async Task AMoveWithoutTo_IsMarkedBesideTo_AndClearsWhenToIsSet()
    {
        using StratBookTabViewModel vm = OpenNew();
        vm.Editor.AddStepCommand.Execute(null);
        StratStepRow row = vm.Editor.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(row.ToIssue).IsNotNull().Because("the checks follow the document, not the last commit");
            await Assert.That(row.ToIssue!.IsRefusal).IsFalse();
            await Assert.That(row.ToIssue.Message).Contains("destination");
            await Assert.That(row.RowIssue).IsNull();
            await Assert.That(row.IssueText).IsEqualTo("warning: a move has no destination place (to)");
            await Assert.That(vm.Editor.Issues).Contains("warning: a move has no destination place (/steps/0/to)");
        }

        row.ToText = "BombsiteA";
        using (Assert.Multiple())
        {
            await Assert.That(vm.Editor.Steps[0].ToIssue).IsNull();
            await Assert.That(vm.Editor.Steps[0].HasIssues).IsFalse();
            await Assert.That(vm.Editor.Issues.Any(i => i.Contains("/steps/0/to", StringComparison.Ordinal))).IsFalse();
        }
    }

    [Test]
    public async Task AnIssueOnAFieldTheRowHides_OrOnTheStep_MarksTheRow_AndARefusalSaysSo()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStep throwing = Step(100, "B", "throw");
        throwing.Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid(), Technique = "sideways" };
        Seed(vm, throwing, Step(105, "A", "move"));
        StratStepRow thrower = vm.Editor.Steps[0];
        StratStepRow late = vm.Editor.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(thrower.ShowTechnique).IsFalse();
            await Assert.That(thrower.RowIssue?.Message).Contains("technique 'sideways'");
            await Assert.That(thrower.IssueText).DoesNotContain("not checked").Because("infos stay in the summary");
            await Assert.That(late.TimeIssue?.IsRefusal).IsTrue();
            await Assert.That(late.TimeIssue?.Glyph).IsNotEqualTo(thrower.RowIssue?.Glyph);
            await Assert.That(late.IssueText).StartsWith("refusal: ");
        }
    }
}
