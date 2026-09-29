#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A step row shows the fields its verb uses (<see cref="StratStepFields" />) and a verb change clears the
///     rest in the same undo entry, so exports never print a value the row hides.
/// </summary>
[NotInParallel]
public class StratStepFieldsTests
{
    // Members every step row edits whatever the verb, or the canvas does; empty is the step itself.
    private static readonly HashSet<string> _alwaysOnTheRow =
        new(["", "id", "atSeconds", "actor", "verb", "note", "positions", "strokes", "holdSeconds", "interpolation"], StringComparer.Ordinal);

    // Read through the JSON so it holds whether technique is a typed member or still in Extra.
    private static bool HasTechnique(StratStep step) =>
        JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)?["utility"] is JsonObject utility
        && utility.ContainsKey("technique");

    private static string StepJson(StratStep step) =>
        JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)!.ToJsonString();

    private static StratBookTabViewModel OpenNew()
    {
        StratBookTabViewModel vm = new(new StratStore(null), null, null, false);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        return vm;
    }

    private static StratStepRow AddStep(StratBookTabViewModel vm)
    {
        vm.Editor.AddStepCommand.Execute(null);
        return vm.Editor.Steps[^1];
    }

    [Test]
    public async Task EveryMemberTheValidatorAsksOfAVerb_IsInThatVerbsFieldSet()
    {
        foreach (string verb in StratVocabulary.Verbs)
        {
            StratDocument document = StratDocument.Create(Guid.NewGuid(), StratOwner.Me(), "de_mirage", "T", "default", "fields", DateTime.UtcNow);
            document.Steps.Add(new StratStep { Id = Guid.NewGuid(), AtSeconds = 100, Verb = verb });
            foreach (StratIssue issue in StratValidator.Validate(document))
            {
                if (!issue.Field.StartsWith("/steps/0", StringComparison.Ordinal))
                {
                    continue;
                }

                string member = issue.Field.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "";
                StratStepField asked = member switch
                {
                    "from" => StratStepField.From,
                    "to" => StratStepField.To,
                    "utility" => StratStepField.Utility,
                    _ when _alwaysOnTheRow.Contains(member) => StratStepField.None,
                    _ => throw new InvalidOperationException(
                        $"the validator names step member '{member}' ({issue.Field}); map it to a row field")
                };
                await Assert.That(StratStepFields.Uses(verb, asked)).IsTrue()
                    .Because($"the validator says '{issue.Message}' about a {verb}, whose row must show that field");
            }
        }
    }

    [Test]
    public async Task TheFieldTable_MatchesTheDesign()
    {
        using (Assert.Multiple())
        {
            await Assert.That(StratStepFields.For("move")).IsEqualTo(StratStepField.From | StratStepField.To);
            await Assert.That(StratStepFields.For("rotate")).IsEqualTo(StratStepField.From | StratStepField.To);
            await Assert.That(StratStepFields.For("hold")).IsEqualTo(StratStepField.To);
            await Assert.That(StratStepFields.For("peek")).IsEqualTo(StratStepField.To);
            await Assert.That(StratStepFields.For("plant")).IsEqualTo(StratStepField.To);
            await Assert.That(StratStepFields.For("defuse")).IsEqualTo(StratStepField.To);
            await Assert.That(StratStepFields.For("throw")).IsEqualTo(StratStepField.Utility);
            await Assert.That(StratStepFields.For("fake")).IsEqualTo(StratStepField.To | StratStepField.Utility);
            await Assert.That(StratStepFields.For("wait")).IsEqualTo(StratStepField.None);
            await Assert.That(StratStepFields.For("call")).IsEqualTo(StratStepField.None);
            await Assert.That(StratStepFields.For("other")).IsEqualTo(StratStepField.All);
            await Assert.That(StratStepFields.For("teleport")).IsEqualTo(StratStepField.All)
                .Because("a verb the validator refuses still shows everything it holds");
            await Assert.That(StratStepFields.ToLabel("plant")).IsEqualTo("site");
            await Assert.That(StratStepFields.ToLabel("hold")).IsEqualTo("at");
            await Assert.That(StratStepFields.ToLabel("move")).IsEqualTo("to");
        }
    }

    [Test]
    public async Task ARow_ShowsItsVerbsFields_AndAnyFieldHoldingAValue()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStepRow row = AddStep(vm);
        row.Verb = "throw";
        row = vm.Editor.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowFrom).IsFalse();
            await Assert.That(row.ShowTo).IsFalse();
            await Assert.That(row.ShowUtility).IsTrue();
            await Assert.That(row.ShowLineup).IsFalse().Because("a lineup is picked after the kind");
            await Assert.That(row.ShowLanding).IsFalse();
        }

        row.UtilityKind = "smoke";
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowLineup).IsTrue();
            await Assert.That(row.ShowLanding).IsTrue().Because("without a lineup, lands at says where it goes");
        }

        // A captured throw carries its thrower's place in from: the row shows it though throw does not use it.
        vm.Session.Apply(PatchOp.ReplaceOp("/steps/0/from", null, new JsonObject { ["place"] = "TSpawn" }));
        await Assert.That(vm.Editor.Steps[0].ShowFrom).IsTrue();

        StratStepRow wait = AddStep(vm);
        wait.Verb = "wait";
        wait = vm.Editor.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(wait.ShowFrom || wait.ShowTo || wait.ShowUtility).IsFalse().Because("wait is a note");
        }
    }

    [Test]
    public async Task AVerbChange_ClearsWhatTheNewVerbDoesNotUse_AsOneUndoEntry()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStepRow row = AddStep(vm);
        row.FromText = "TSpawn";
        row.ToText = "TopofMid";
        vm.Editor.Steps[0].UtilityKind = "smoke";
        vm.Editor.Steps[0].Note = "fast";
        int depth = vm.Session.UndoDepth;

        vm.Editor.Steps[0].Verb = "hold";
        StratStep step = vm.Session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1).Because("one gesture, one entry");
            await Assert.That(step.Verb).IsEqualTo("hold");
            await Assert.That(step.From).IsNull();
            await Assert.That(step.To?.Place).IsEqualTo("TopofMid").Because("hold keeps its place");
            await Assert.That(step.Utility).IsNull();
            await Assert.That(step.Note).IsEqualTo("fast").Because("every verb keeps its note");
        }

        vm.UndoCommand.Execute(null);
        step = vm.Session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(step.Verb).IsEqualTo("move");
            await Assert.That(step.From?.Place).IsEqualTo("TSpawn");
            await Assert.That(step.Utility?.Kind).IsEqualTo("smoke");
            await Assert.That(vm.Editor.Steps[0].Verb).IsEqualTo("move");
        }
    }

    [Test]
    public async Task AVerbChange_ToAVerbUsingEverythingSet_ChangesOnlyTheVerb()
    {
        using StratBookTabViewModel vm = OpenNew();
        StratStepRow row = AddStep(vm);
        row.FromText = "TSpawn";
        vm.Editor.Steps[0].ToText = "TopofMid";

        vm.Editor.Steps[0].Verb = "rotate";
        StratStep step = vm.Session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(step.From?.Place).IsEqualTo("TSpawn");
            await Assert.That(step.To?.Place).IsEqualTo("TopofMid");
        }
    }

    [Test]
    public async Task AKindChange_KeepsTheLanding_AndDropsTheLineupAndTechnique()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).Verb = "throw";
        vm.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null, new JsonObject
        {
            ["kind"] = "smoke",
            ["lineupId"] = Guid.NewGuid().ToString(),
            ["technique"] = "jumpthrow",
            ["landing"] = new JsonObject { ["x"] = 120.5, ["y"] = -400.0, ["levelMinZ"] = -160.0 }
        }));

        vm.Editor.Steps[0].UtilityKind = "molotov";
        UtilityRef utility = vm.Session.Document!.Steps[0].Utility!;
        using (Assert.Multiple())
        {
            await Assert.That(utility.Kind).IsEqualTo("molotov");
            await Assert.That(utility.LineupId).IsNull();
            await Assert.That(HasTechnique(vm.Session.Document!.Steps[0])).IsFalse();
            await Assert.That(utility.Landing?.X).IsEqualTo(120.5);
        }

        vm.Editor.Steps[0].LandingText = "Connector";
        utility = vm.Session.Document!.Steps[0].Utility!;
        using (Assert.Multiple())
        {
            await Assert.That(utility.Landing?.Place).IsEqualTo("Connector");
            await Assert.That(utility.Landing?.X).IsEqualTo(120.5).Because("typing a place keeps the captured point");
        }

        vm.Editor.Steps[0].UtilityKind = StratEditorViewModel.None;
        await Assert.That(vm.Session.Document!.Steps[0].Utility).IsNull();
    }

    [Test]
    public async Task ALineupChange_DropsTheOldLineupsTechnique()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).Verb = "throw";
        vm.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = Guid.NewGuid().ToString(), ["technique"] = "jumpthrow" }));

        vm.Editor.Steps[0].Lineup = StratLineupOption.None;
        UtilityRef utility = vm.Session.Document!.Steps[0].Utility!;
        using (Assert.Multiple())
        {
            await Assert.That(utility.LineupId).IsNull();
            await Assert.That(HasTechnique(vm.Session.Document!.Steps[0])).IsFalse();
            await Assert.That(utility.Kind).IsEqualTo("smoke");
        }
    }

    [Test]
    public async Task WheelingTheKind_ThroughNone_AndBack_LeavesTheUtilityAsItWas_AndNoEntry()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).Verb = "throw";
        vm.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null, new JsonObject
        {
            ["kind"] = "smoke",
            ["lineupId"] = Guid.NewGuid().ToString(),
            ["technique"] = "jumpthrow",
            ["landing"] = new JsonObject { ["x"] = 120.5, ["y"] = -400.0, ["levelMinZ"] = -160.0 }
        }));
        string before = StepJson(vm.Session.Document!.Steps[0]);
        int depth = vm.Session.UndoDepth;

        StratStepRow row = vm.Editor.Steps[0];
        row.UtilityKind = StratEditorViewModel.None;
        await Assert.That(vm.Session.Document!.Steps[0].Utility).IsNull();
        row.UtilityKind = "molotov";
        row.UtilityKind = "smoke";

        using (Assert.Multiple())
        {
            await Assert.That(StepJson(vm.Session.Document!.Steps[0])).IsEqualTo(before);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth).Because("arriving back leaves nothing to undo");
        }
    }

    [Test]
    public async Task WheelingTheVerb_ThroughWait_AndBack_KeepsFromAndTo()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).FromText = "TSpawn";
        vm.Editor.Steps[0].ToText = "TopofMid";
        string before = StepJson(vm.Session.Document!.Steps[0]);
        int depth = vm.Session.UndoDepth;

        StratStepRow row = vm.Editor.Steps[0];
        row.Verb = "wait";
        await Assert.That(vm.Session.Document!.Steps[0].From).IsNull();
        row.Verb = "hold";
        row.Verb = "move";

        using (Assert.Multiple())
        {
            await Assert.That(StepJson(vm.Session.Document!.Steps[0])).IsEqualTo(before);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth);
        }
    }

    [Test]
    public async Task ABurst_IsOneUndoEntry_ComputedFromWhereItStarted_AndRedoes()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).FromText = "TSpawn";
        vm.Editor.Steps[0].ToText = "TopofMid";
        int depth = vm.Session.UndoDepth;

        StratStepRow row = vm.Editor.Steps[0];
        row.Verb = "wait";
        row.Verb = "hold";
        row.Verb = "peek";
        StratStep step = vm.Session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(step.Verb).IsEqualTo("peek");
            await Assert.That(step.To?.Place).IsEqualTo("TopofMid").Because("wait cleared it on the way, peek uses it");
            await Assert.That(step.From).IsNull();
        }

        vm.UndoCommand.Execute(null);
        step = vm.Session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(step.Verb).IsEqualTo("move");
            await Assert.That(step.From?.Place).IsEqualTo("TSpawn");
            await Assert.That(step.To?.Place).IsEqualTo("TopofMid");
        }

        vm.RedoCommand.Execute(null);
        step = vm.Session.Document!.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(step.Verb).IsEqualTo("peek");
            await Assert.That(step.From).IsNull();
            await Assert.That(step.To?.Place).IsEqualTo("TopofMid");
        }
    }

    [Test]
    public async Task ABurst_EndsOnFocusLossOrAnotherEdit()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).FromText = "TSpawn";
        int depth = vm.Session.UndoDepth;

        vm.Editor.Steps[0].Verb = "hold";
        vm.Editor.EndEditBurst();
        vm.Editor.Steps[0].Verb = "move";
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 2);
            await Assert.That(vm.Session.Document!.Steps[0].From).IsNull().Because("the second burst began after from was cleared");
        }

        vm.Editor.Steps[0].Note = "other edit";
        int afterNote = vm.Session.UndoDepth;
        vm.Editor.Steps[0].Verb = "wait";
        vm.Editor.Steps[0].Note = "again";
        vm.Editor.Steps[0].Verb = "call";
        await Assert.That(vm.Session.UndoDepth).IsEqualTo(afterNote + 3);
    }

    [Test]
    public async Task AKindChange_Redoes()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).Verb = "throw";
        vm.Editor.EndEditBurst();
        vm.Editor.Steps[0].UtilityKind = "smoke";
        vm.Editor.EndEditBurst();
        vm.Editor.Steps[0].UtilityKind = "flash";

        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Session.Document!.Steps[0].Utility?.Kind).IsEqualTo("smoke");
        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Session.Document!.Steps[0].Utility).IsNull();
        vm.RedoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.Document!.Steps[0].Utility?.Kind).IsEqualTo("flash");
            await Assert.That(vm.Editor.Steps[0].UtilityKind).IsEqualTo("flash");
        }
    }

    [Test]
    public async Task ClearingATypedLanding_RemovesTheNode_ButKeepsACapturedPoint()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).Verb = "throw";
        vm.Editor.Steps[0].UtilityKind = "smoke";
        vm.Editor.Steps[0].LandingText = "Connector";
        await Assert.That(vm.Session.Document!.Steps[0].Utility?.Landing?.Place).IsEqualTo("Connector");

        vm.Editor.Steps[0].LandingText = "";
        await Assert.That(vm.Session.Document!.Steps[0].Utility?.Landing).IsNull().Because("no `landing: {}` left behind");

        vm.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility/landing", null,
            new JsonObject { ["place"] = "Connector", ["x"] = 10.0, ["y"] = 20.0 }));
        vm.Editor.Steps[0].LandingText = "";
        UtilityLanding? landing = vm.Session.Document!.Steps[0].Utility?.Landing;
        using (Assert.Multiple())
        {
            await Assert.That(landing?.Place).IsNull();
            await Assert.That(landing?.X).IsEqualTo(10.0);
        }
    }

    [Test]
    public async Task ALineupTheLookupDoesNotOffer_ShowsAsItsId()
    {
        using StratBookTabViewModel vm = OpenNew();
        AddStep(vm).Verb = "throw";
        Guid id = Guid.NewGuid();
        vm.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null, new JsonObject { ["kind"] = "flash", ["lineupId"] = id.ToString() }));

        StratStepRow row = vm.Editor.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(row.Lineup?.Id).IsEqualTo(id);
            await Assert.That(row.LineupOptions).Contains(row.Lineup!).Because("a combo box shows nothing for a selection outside its items");
            await Assert.That(row.ShowLanding).IsFalse().Because("the lineup says where it lands");
        }
    }
}
