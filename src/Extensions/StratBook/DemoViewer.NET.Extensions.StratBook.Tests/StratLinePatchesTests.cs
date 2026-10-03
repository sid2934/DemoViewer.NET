#region

using System.Text.Json;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="StratLinePatches.Write" /> on its own: the folds that keep the stored shape, and undo back to the
///     exact bytes; then the place warnings the editor shows once the canvas has the map's zones.
/// </summary>
[NotInParallel]
public class StratLinePatchesTests
{
    private static string Shape(StratStep step) => JsonSerializer.Serialize(step, StratJsonContext.Default.StratStep);

    // Step 1 of FiveSteps as five bare lines to BombsiteA, with its positions kept.
    private static StratDocument FiveLines(string? oddPlace = null)
    {
        StratDocument document = FiveSteps();
        StratStep step = document.Steps[0];
        step.To = null;
        step.Assignments =
        [
            .. StratVocabulary.Slots.Select(s => new StepAssignment
            {
                Slot = s, To = new PlaceRef { Place = s == "E" && oddPlace is not null ? oddPlace : "BombsiteA" }
            })
        ];
        return document;
    }

    private static List<PatchOp> Rewrite(StratStep step) => StratLinePatches.Write(step, "/steps/0", StratLinePatches.Copy(step));

    [Test]
    public async Task FiveBareLinesToOnePlace_FoldToAStepForAll()
    {
        StratDocument document = FiveLines();
        StratStep step = document.Steps[0];
        List<PatchOp> ops = Rewrite(step);

        using (Assert.Multiple())
        {
            await Assert.That(ops.Select(o => o.Op + " " + o.Path)).IsEquivalentTo(
                ["replace /steps/0/to", "remove /steps/0/assignments"], TUnit.Assertions.Enums.CollectionOrdering.Any);
            await Assert.That(step.Actor).IsEqualTo(StratVocabulary.ActorAll).Because("five lines already summarise as all");
        }

        StratStep folded = StratHistory.Apply(document, ops).Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(folded.Assignments).IsNull();
            await Assert.That(folded.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(folded.To!.Place).IsEqualTo("BombsiteA");
            await Assert.That(folded.Positions.Count).IsEqualTo(step.Positions.Count).Because("positions are not lines");
        }
    }

    [Test]
    public async Task OneLineToAnotherPlace_StaysAsLines()
    {
        StratStep step = FiveLines("Connector").Steps[0];
        await Assert.That(Rewrite(step)).IsEmpty();
    }

    [Test]
    public async Task PositionsOnTheStep_DoNotChangeTheFold()
    {
        StratDocument with = FiveLines();
        StratDocument without = FiveLines();
        without.Steps[0].Positions = [];
        await Assert.That(with.Steps[0].Positions.Count).IsGreaterThan(0);

        string Ops(StratDocument d) => string.Join(";", Rewrite(d.Steps[0]).Select(o => o.Op + " " + o.Path + " " + o.Value?.ToJsonString()));
        await Assert.That(Ops(with)).IsEqualTo(Ops(without));
    }

    [Test]
    public async Task UndoOfAFold_RestoresTheExactBytes()
    {
        StratDocument document = FiveLines();
        (StratStore _, StratSession session) = Opened(document);
        string before = StratStore.Serialize(session.Document!);

        session.Apply(Rewrite(session.Document!.Steps[0]));
        await Assert.That(session.Document!.Steps[0].Assignments).IsNull();
        session.Undo();
        await Assert.That(StratStore.Serialize(session.Document!)).IsEqualTo(before);
    }

    // ── Place warnings from the canvas's zones ───────────────────────────────────────────────────

    private static StratBookTabViewModel TabWith(TaskCompletionSource<IZonePlaceResolver?> zones)
    {
        StratBookTabViewModel vm = new(new StratStore(null), null, a => a(), false, canvasMapLoader: _ => null,
            canvasPlaces: _ => zones.Task);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        StratStep everyone = StratStepEditingTests.Step(100, "all", "move");
        everyone.To = new PlaceRef { Place = "Nowhere" };
        StratStep lines = StratStepEditingTests.Step(90, "all", "hold");
        lines.To = null;
        lines.Assignments =
        [
            new StepAssignment { Slot = "B", To = new PlaceRef { Place = "Ramp" }, Watch = new StepWatch { Places = ["Hut", "Elsewhere"] } },
            new StepAssignment { Slot = "C", To = new PlaceRef { Place = "Nowhere" } }
        ];
        StratStepEditingTests.Seed(vm, everyone, lines);
        return vm;
    }

    [Test]
    public async Task UnknownPlaces_WarnOnTheirFields_OnceTheZonesLoad_KnownOnesAreClean()
    {
        TaskCompletionSource<IZonePlaceResolver?> zones = new();
        using StratBookTabViewModel vm = TabWith(zones);
        StratStepRow everyone = vm.Editor.Steps[^2];
        StratStepRow lines = vm.Editor.Steps[^1];

        using (Assert.Multiple())
        {
            await Assert.That(everyone.ToIssue).IsNull().Because("no place is checked while the zones load");
            await Assert.That(lines.Lines[1].PlaceIssue).IsNull();
            await Assert.That(vm.Session.Issues.Any(i => i.Message.Contains("is not a place", StringComparison.Ordinal))).IsFalse();
        }

        // StratMapFirstTests' synthetic map has two places, Hut and Ramp.
        zones.SetResult(StratMapFirstTests.SyntheticZones());
        everyone = vm.Editor.Steps[^2];
        lines = vm.Editor.Steps[^1];
        using (Assert.Multiple())
        {
            await Assert.That(everyone.ToIssue?.Message).Contains("'Nowhere' is not a place");
            await Assert.That(everyone.ToIssue!.IsRefusal).IsFalse();
            await Assert.That(lines.Lines[0].PlaceIssue).IsNull().Because("Ramp is a place");
            await Assert.That(lines.Lines[0].WatchIssue?.Message).Contains("'Elsewhere'");
            await Assert.That(lines.Lines[0].WatchIssue!.Message).DoesNotContain("Hut");
            await Assert.That(lines.Lines[1].PlaceIssue?.Message).Contains("'Nowhere'");
            await Assert.That(vm.Session.Issues.Where(i => i.Message.Contains("is not a place", StringComparison.Ordinal))
                .All(i => i.Severity == StratIssueSeverity.Warning)).IsTrue();
        }
    }

    [Test]
    public async Task AStrayStepTo_BesideLines_IsReadOnly_AndItsClearIsOneEntry()
    {
        TaskCompletionSource<IZonePlaceResolver?> zones = new();
        using StratBookTabViewModel vm = TabWith(zones);
        int index = vm.Session.Document!.Steps.Count - 1;
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{index}/to", null, System.Text.Json.Nodes.JsonNode.Parse("""{ "place": "BombsiteA" }""")));
        StratStepRow row = vm.Editor.Steps[index];
        int depth = vm.Session.UndoDepth;

        using (Assert.Multiple())
        {
            await Assert.That(row.IsStrayTo).IsTrue();
            await Assert.That(row.ShowTo).IsTrue();
            await Assert.That(row.ToIssue?.Message).Contains("not used");
        }

        row.ToText = "Connector";
        await Assert.That(vm.Session.Document!.Steps[index].To!.Place).IsEqualTo("BombsiteA").Because("typing there writes nothing");
        await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth);

        vm.Editor.Steps[index].ClearStrayToCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.Document!.Steps[index].To).IsNull();
            await Assert.That(vm.Session.Document!.Steps[index].Assignments!.Count).IsEqualTo(2);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(vm.Editor.Steps[index].ShowTo).IsFalse();
        }
    }
}
