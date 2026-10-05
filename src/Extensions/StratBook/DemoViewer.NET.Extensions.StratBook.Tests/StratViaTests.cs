#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A travel's <c>via</c> (docs/strat-format.md, "Via"): the stored shape round-trips and old files keep their bytes,
///     the writers keep it on the step or the line as <c>to</c> is kept, the validator warns on an unknown place, the
///     sheets and the history print it, and the editor shows it for travel verbs only.
/// </summary>
[NotInParallel]
public class StratViaTests
{
    private static CalloutResolver Places() => new(["TSpawn", "LongDoors", "OutsideLong", "Middle", "BombsiteB"]);

    private static StratDocument Moving()
    {
        StratDocument document = Minimal(map: "de_dust2");
        document.Steps = [Step(1, 105, "B", "move", "TSpawn", "LongDoors")];
        document.Steps[0].Via = ["OutsideLong"];
        return document;
    }

    [Test]
    public async Task AVia_RoundTrips_AndAFileWithoutOne_WritesNoKey()
    {
        StratDocument document = Moving();
        document.Steps[0].ViaPoints = [new PlaceRef { X = 120, Y = -40, LevelMinZ = -99968 }];
        string json = StratStore.Serialize(document);
        StratDocument read = System.Text.Json.JsonSerializer.Deserialize(json, StratJsonContext.Default.StratDocument)!;

        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", read.Steps[0].Via!)).IsEqualTo("OutsideLong");
            await Assert.That(read.Steps[0].ViaPoints!.Single().X).IsEqualTo(120d);
            await Assert.That(StratStore.Serialize(read)).IsEqualTo(json);
            await Assert.That(StratStore.Serialize(Minimal())).DoesNotContain("via");
        }
    }

    [Test]
    public async Task TheWriter_KeepsViaOnAPlainStep_MovesItOntoTheLine_AndBack()
    {
        StratDocument document = Moving();
        StratStep step = document.Steps[0];

        // A pick outside every place adds a point; a place already there is not added twice.
        StratLocationField plain = new(step.Id, null, StratLocationKind.Via);
        await Assert.That(StratLocationPatches.Applies(step, plain)).IsTrue();
        await Assert.That(string.Join(",", StratLocationPatches.Read(step, plain).Select(p => p.Place)!)).IsEqualTo("OutsideLong");
        List<PatchOp> pick = StratLocationPatches.Pick(document, 0, plain, null, 300, 400, -99968, true);
        await Assert.That(pick.Single().Path).IsEqualTo("/steps/0/viaPoints");
        await Assert.That(StratLocationPatches.Pick(document, 0, plain, "OutsideLong", 0, 0, -99968, true)).IsEmpty();

        // A watch makes B a line: the via goes with the to.
        List<PatchOp> lined = StratLinePatches.WatchYaw(step, "/steps/0", "B", 90);
        StratDocument after = StratHistory.Apply(document, lined);
        StratStep onLine = after.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(onLine.Via).IsNull();
            await Assert.That(string.Join(",", onLine.Assignments!.Single().Via!)).IsEqualTo("OutsideLong");
            await Assert.That(StratStepLines.ViaFor(onLine, "B").Single().Place).IsEqualTo("OutsideLong");
        }

        // The line's own field writes the line; clearing the angle folds it back to a plain step, via and all.
        StratLocationField line = new(step.Id, "B", StratLocationKind.Via);
        List<PatchOp> two = StratLocationPatches.Write(after, 0, line, [new PlaceRef { Place = "OutsideLong" }, new PlaceRef { Place = "Middle" }]);
        await Assert.That(two.Single().Path).IsEqualTo("/steps/0/assignments/0/via");
        StratDocument folded = StratHistory.Apply(after, StratLinePatches.WatchYaw(onLine, "/steps/0", "B", null));
        await Assert.That(folded.Steps[0].Assignments).IsNull();
        await Assert.That(string.Join(",", folded.Steps[0].Via!)).IsEqualTo("OutsideLong");
    }

    [Test]
    public async Task OnAStepForEveryone_TheGroupVia_FoldsBackOntoTheStep()
    {
        StratDocument document = Minimal(map: "de_dust2");
        document.Steps = [Step(1, 105, StratVocabulary.ActorAll, "move", to: "BombsiteB")];
        StratLocationField all = new(document.Steps[0].Id, StratLocationField.AllLines, StratLocationKind.Via);
        List<PatchOp> ops = StratLocationPatches.Write(document, 0, all, [new PlaceRef { Place = "Middle" }]);
        StratDocument after = StratHistory.Apply(document, ops);
        await Assert.That(ops.Single().Path).IsEqualTo("/steps/0/via");
        await Assert.That(after.Steps[0].Assignments).IsNull();
        await Assert.That(string.Join(",", after.Steps[0].Via!)).IsEqualTo("Middle");
    }

    [Test]
    public async Task TheValidator_WarnsOnAnUnknownViaPlace_OnTheStepAndOnALine()
    {
        StratDocument document = Moving();
        document.Steps[0].Via = ["OutsideLong", "Nowhere"];
        StratStep lined = Step(2, 100, StratVocabulary.ActorAll, "push");
        lined.Assignments = [new StepAssignment { Slot = "C", To = new PlaceRef { Place = "BombsiteB" }, Via = ["Atlantis"] }];
        lined.Actor = "C";
        lined.Via = ["Middle"];
        document.Steps.Add(lined);

        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, Places());
        using (Assert.Multiple())
        {
            await Assert.That(issues.Any(i => i.Field == "/steps/0/via/1" && i.Severity == StratIssueSeverity.Warning)).IsTrue();
            await Assert.That(issues.Any(i => i.Field == "/steps/0/via/0")).IsFalse();
            await Assert.That(issues.Any(i => i.Field == "/steps/1/assignments/0/via/0" && i.Message.Contains("Atlantis"))).IsTrue();
            await Assert.That(issues.Any(i => i.Field == "/steps/1/via" && i.Message.Contains("not used"))).IsTrue();
            await Assert.That(StratValidator.Validate(document).Any(i => i.Field.Contains("/via/"))).IsFalse()
                .Because("place warnings wait for the map's zones");
        }
    }

    [Test]
    public async Task TheSheets_PrintTheVia_AfterTheDestination()
    {
        StratDocument document = Moving();
        StratStep push = Step(2, 100, StratVocabulary.ActorAll, "push");
        push.Assignments =
        [
            new StepAssignment { Slot = "C", To = new PlaceRef { Place = "BombsiteB" }, Via = ["Middle"] },
            new StepAssignment { Slot = "D", To = new PlaceRef { Place = "BombsiteB" } }
        ];
        document.Steps.Add(push);

        string call = StratTextExporter.CallSheet(document);
        RoleSheet b = RoleSheet.Derive(document, "B");
        RoleSheet c = RoleSheet.Derive(document, "C");
        string html = RoleSheetHtmlWriter.Html(document, [b, c]);
        using (Assert.Multiple())
        {
            await Assert.That(StratStepPhrasing.Phrase(document.Steps[0], null)).IsEqualTo("B moves T Spawn → Long Doors via Outside Long");
            await Assert.That(StratStepPhrasing.PhraseLine(push.Assignments[0], "push", null)).IsEqualTo("C → Bombsite B via Middle");
            await Assert.That(StratStepPhrasing.PhraseLine(push.Assignments[1], "push", null)).IsEqualTo("D → Bombsite B");
            await Assert.That(call).Contains("B moves T Spawn → Long Doors via Outside Long");
            await Assert.That(call).Contains("C → Bombsite B via Middle");
            await Assert.That(html).Contains("Long Doors via Outside Long");
            await Assert.That(html).Contains("Bombsite B via Middle");
        }
    }

    [Test]
    public async Task TheHistory_PhrasesAViaEdit_AndAStepWithOne()
    {
        StratDocument document = Moving();
        StratStep plain = document.Steps[0];
        List<PatchOp> set = StratLocationPatches.Write(document, 0, new StratLocationField(plain.Id, null, StratLocationKind.Via),
            [new PlaceRef { Place = "OutsideLong" }, new PlaceRef { Place = "Middle" }]);
        StratDocument after = StratHistory.Apply(document, set);
        string summary = StratDiffPhrasing.SummaryAfter(after, set);

        StratDocument empty = Minimal(map: "de_dust2");
        empty.Steps = [];
        PatchOp add = PatchOp.AddOp("/steps/0", System.Text.Json.JsonSerializer.SerializeToNode(plain, StratJsonContext.Default.StratStep));
        string added = StratDiffPhrasing.SummaryAfter(StratHistory.Apply(empty, [add]), [add]);

        List<PatchOp> cleared = StratLocationPatches.Write(after, 0, new StratLocationField(plain.Id, null, StratLocationKind.Via), []);
        string clearedSummary = StratDiffPhrasing.SummaryAfter(StratHistory.Apply(after, cleared), cleared);
        Console.WriteLine($"[via] {summary} | {added} | {clearedSummary}");
        using (Assert.Multiple())
        {
            await Assert.That(summary).Contains("via set to Outside Long, Middle");
            await Assert.That(added).Contains("B moves to Long Doors via Outside Long at 1:45");
            await Assert.That(clearedSummary).Contains("via cleared");
        }
    }

    [Test]
    public async Task AVerbThatDoesNotTravel_ClearsTheVia_InTheSameEntry()
    {
        using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
        StratStep step = StratStepEditingTests.Step(100, "B", "move");
        step.To = new PlaceRef { Place = "LongDoors" };
        step.Via = ["OutsideLong"];
        StratStepEditingTests.Seed(vm, step);
        StratStepRow row = vm.Editor.Steps[0];
        await Assert.That(row.ShowGroupVia).IsTrue();
        await Assert.That(row.GroupViaText).IsNotEmpty();

        int depth = vm.Session.UndoDepth;
        row.Verb = "hold";
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.Document!.Steps[0].Via).IsNull();
            await Assert.That(vm.Session.Document!.Steps[0].To!.Place).IsEqualTo("LongDoors");
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(row.ShowGroupVia).IsFalse();
        }
    }

    [Test]
    public async Task TheEditorsViaField_WritesTheStep_AndALineWhenSplit()
    {
        using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
        StratStep step = StratStepEditingTests.Step(100, StratVocabulary.ActorAll, "push");
        step.Assignments =
        [
            new StepAssignment { Slot = "A", To = new PlaceRef { Place = "BombsiteB" } },
            new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteB" } }
        ];
        step.Actor = StratVocabulary.ActorAll;
        StratStepEditingTests.Seed(vm, step);
        StratStepRow row = vm.Editor.Steps[0];

        row.GroupViaValue = [new PlaceRef { Place = "Middle" }];
        StratStep written = vm.Session.Document!.Steps[0];
        await Assert.That(written.Assignments!.All(l => l.Via is { Count: 1 } via && via[0] == "Middle")).IsTrue();

        row.SplitCommand.Execute(null);
        StratLineRow b = row.Lines.Single(l => l.Slot == "B");
        await Assert.That(b.ShowVia).IsTrue();
        b.ViaValue = [new PlaceRef { Place = "OutsideLong" }, new PlaceRef { Place = "Middle" }];
        written = vm.Session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", written.Assignments!.Single(l => l.Slot == "A").Via!)).IsEqualTo("Middle");
            await Assert.That(string.Join(",", written.Assignments!.Single(l => l.Slot == "B").Via!)).IsEqualTo("OutsideLong,Middle");
            await Assert.That(row.ShowCompact).IsFalse();
        }
    }
}
