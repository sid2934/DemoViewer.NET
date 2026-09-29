#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratAssignmentsTests;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lines in what people read: the call sheet lists each line under its step, a role sheet prints only its own
///     slot's line with what it watches, and the history names the slot a line edit touched.
/// </summary>
public class StratAssignmentsExportTests
{
    private static string Line(RoleSheetLine line) => (line.IsContext ? "context: " : "own: ") + line.Text;

    // WithLines, plus E moving on from where C's line ends.
    private static StratDocument WithFollower()
    {
        StratDocument document = WithLines();
        document.Steps.Add(Step(4, 50, "E", "move", "Connector", "BombsiteA"));
        return document;
    }

    [Test]
    public async Task TheCallSheet_ListsEachLine_UnderItsStep()
    {
        string sheet = StratTextExporter.CallSheet(WithLines());

        await Assert.That(sheet).Contains("""
                                          - **1:05** B, C, D move T Ramp
                                            - B → Palace Interior, watching Bombsite A, CT Spawn
                                            - C → Connector, watching Stairs
                                            - D → Stairs, watching T Ramp

                                          """.Replace("\r\n", "\n", StringComparison.Ordinal));
        await Assert.That(sheet).Contains("- **1:30** B throws T Ramp → Bombsite A\n").Because("a plain step prints as before");
    }

    [Test]
    public async Task AHoldLine_ReadsAt_AndFiveLinesReadAll()
    {
        StratStep step = Step(1, 115, StratVocabulary.ActorAll, "hold");
        step.Assignments = [.. StratVocabulary.Slots.Select(s => new StepAssignment { Slot = s, To = s == "A" ? Place("BombsiteA") : null })];

        using (Assert.Multiple())
        {
            await Assert.That(StratStepPhrasing.Phrase(step, null)).IsEqualTo("All hold");
            await Assert.That(StratStepPhrasing.PhraseLine(step.Assignments[0], "hold", null)).IsEqualTo("A at Bombsite A");
            await Assert.That(StratStepPhrasing.PhraseLine(step.Assignments[1], "hold", null)).IsEqualTo("B");
        }
    }

    [Test]
    public async Task ARoleSheet_PrintsOnlyItsOwnLine_WithWhatItWatches()
    {
        StratDocument document = WithFollower();
        RoleSheet b = RoleSheet.Derive(document, "B");
        RoleSheet d = RoleSheet.Derive(document, "D");
        RoleSheet a = RoleSheet.Derive(document, "A");

        using (Assert.Multiple())
        {
            await Assert.That(b.Lines.Select(Line)).IsEquivalentTo(
            [
                "own: B throws T Ramp → Bombsite A",
                "own: B moves T Ramp → Palace Interior, watching Bombsite A, CT Spawn"
            ]);
            await Assert.That(d.Lines.Select(Line)).IsEquivalentTo(["own: D moves T Ramp → Stairs, watching T Ramp"]);
            await Assert.That(d.Lines.Single().Actor).IsEqualTo("D");
            await Assert.That(a.Lines).IsEmpty().Because("A has no line, and the step's actor 'all' is only a summary");
        }
    }

    [Test]
    public async Task ALineThatFeedsAnotherSlot_IsItsContext()
    {
        RoleSheet e = RoleSheet.Derive(WithFollower(), "E");

        // The two throws land where E ends; the line step sends C where E starts.
        await Assert.That(e.Lines.Select(Line)).IsEquivalentTo(
        [
            "context: B throws T Ramp → Bombsite A",
            "context: C throws T Ramp → Bombsite A",
            "context: B, C, D move T Ramp: B → Palace Interior, watching Bombsite A, CT Spawn; C → Connector, watching Stairs; D → Stairs, watching T Ramp",
            "own: E moves Connector → Bombsite A"
        ]);
    }

    [Test]
    public async Task TheLanPrintPage_CarriesTheLine()
    {
        StratDocument document = WithLines();
        string html = RoleSheetHtmlWriter.Html(document, [RoleSheet.Derive(document, "C")]);

        await Assert.That(html).Contains("1:05 · C moves T Ramp → Connector, watching Stairs</li>");
    }

    [Test]
    public async Task TheHistory_NamesTheSlotALineEditTouched()
    {
        StratDocument document = WithLines();
        JsonNode before = StratHistory.ToNode(document);
        JsonNode line = JsonSerializer.SerializeToNode(new StepAssignment { Slot = "E", To = Place("Jungle") }, StratJsonContext.Default.StepAssignment)!;

        using (Assert.Multiple())
        {
            await Assert.That(StratDiffPhrasing.Summary(before, [PatchOp.AddOp("/steps/2/assignments/3", line)]))
                .IsEqualTo("B, C, D's move: E added → Jungle");
            await Assert.That(StratDiffPhrasing.Summary(before, [PatchOp.RemoveOp("/steps/2/assignments/1", null)]))
                .IsEqualTo("B, C, D's move: C removed");
            await Assert.That(StratDiffPhrasing.Summary(before, [PatchOp.AddOp("/steps/2/assignments/0/watch/places/2", JsonValue.Create("Jungle"))]))
                .IsEqualTo("B, C, D's move: B watching added: Jungle");
            await Assert.That(StratDiffPhrasing.Summary(before, [PatchOp.RemoveOp("/steps/2/assignments/0/watch/places/1", null)]))
                .IsEqualTo("B, C, D's move: B watching removed: CT Spawn");
            await Assert.That(StratDiffPhrasing.Summary(before,
                    [PatchOp.ReplaceOp("/steps/2/assignments/2/watch/yawDegrees", JsonValue.Create(135), JsonValue.Create(90))]))
                .IsEqualTo("B, C, D's move: D view angle set to 90°");
            await Assert.That(StratDiffPhrasing.Summary(before,
                    [PatchOp.RemoveOp("/steps/2/assignments/2/watch/yawDegrees", JsonValue.Create(135))]))
                .IsEqualTo("B, C, D's move: D view angle cleared");
            await Assert.That(StratDiffPhrasing.Summary(before,
                    [PatchOp.ReplaceOp("/steps/2/assignments/0/to/place", JsonValue.Create("PalaceInterior"), JsonValue.Create("Jungle"))]))
                .IsEqualTo("B, C, D's move: B to Palace Interior → Jungle");
            await Assert.That(StratDiffPhrasing.Summary(null,
                    [PatchOp.AddOp("/steps/0", JsonSerializer.SerializeToNode(document.Steps[2], StratJsonContext.Default.StratStep))]))
                .IsEqualTo("step added: B, C, D move to T Ramp at 1:05");
        }
    }
}
