#region

using System.Text.Json;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using static DemoViewer.NET.AppTests.StratStepEditingTests;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Changing who is in a step never moves or drops a player's line unless that player is removed: Who writes by
///     slot, a line's slot combo moves the whole line and swaps on a taken slot, and undo restores the exact bytes.
/// </summary>
[NotInParallel]
public class StratLineSlotTests
{
    private static string Bytes(StratBookTabViewModel vm) => StratStore.Serialize(vm.Session.Document!);

    private static string Lines(StratStep step) =>
        string.Join(" ", StratStepLines.Of(step).Select(l =>
            l.Slot + ">" + l.To?.Place + (l.Watch is { } w ? "/" + string.Join(",", w.Places) : "")
                     + (l.Extra is { Count: > 0 } extra ? "+" + string.Join(",", extra.Keys) : "")));

    private static StepAssignment Line(string slot, string? to, params string[] watch) => new()
    {
        Slot = slot, To = to is null ? null : new PlaceRef { Place = to }, Watch = watch.Length == 0 ? null : new StepWatch { Places = [.. watch] }
    };

    private static StratStep Doc(StratBookTabViewModel vm, Guid id) => vm.Session.Document!.Steps.Single(s => s.Id == id);

    private static StratStepRow Row(StratBookTabViewModel vm, Guid id) => vm.Editor.Steps.Single(r => r.Id == id);

    // Step 8 of the owner's "Execute B" as revision 7 left it.
    private static StratStep ExecuteBStep8() => new()
    {
        Id = Guid.NewGuid(), AtSeconds = 53, Actor = StratVocabulary.ActorAll, Verb = "move", Note = "entry, A holds under",
        Assignments =
        [
            Line("B", "BombsiteB"), Line("C", "BombsiteB"), Line("D", "BombsiteB"),
            new StepAssignment { Slot = "A", To = new PlaceRef { Place = "TunnelStairs", X = -1184.52, Y = 1126.99, LevelMinZ = -99968 } }
        ]
    };

    private static (StratBookTabViewModel Vm, Guid Id) Open(StratStep step)
    {
        StratBookTabViewModel vm = OpenNew();
        Seed(vm, step);
        vm.Session.Commit("seed");
        return (vm, step.Id);
    }

    // Revision 8 up to the watch: every op before "B removed" in the log.
    private static void UpToTheWatch(StratBookTabViewModel vm, Guid id)
    {
        Row(vm, id).Verb = "push";
        Row(vm, id).Lines[1].RemoveCommand.Execute(null);
        Row(vm, id).Lines[2].RemoveCommand.Execute(null);
        vm.Editor.AddLineCommand.Execute(Row(vm, id));
        Row(vm, id).Lines[2].Slot = "C";
        vm.Editor.EndEditBurst();
        Row(vm, id).Lines[2].PlaceText = "BombsiteB";
        Row(vm, id).Lines[0].WatchText = "Back, BombsiteB";
    }

    [Test]
    public async Task Revision8_Replayed_ShowsTheRemoveThatDroppedTheWatch_AndSwapsKeepItOnItsLine()
    {
        (StratBookTabViewModel vm, Guid id) = Open(ExecuteBStep8());
        using StratBookTabViewModel scope = vm;
        UpToTheWatch(vm, id);
        string watched = Bytes(vm);
        int depth = vm.Session.UndoDepth;
        await Assert.That(Lines(Doc(vm, id))).IsEqualTo("B>BombsiteB/Back,BombsiteB D>BombsiteB C>BombsiteB");

        // The log's tail: B's remove button, + player (the first free slot, A), then three re-letters.
        Row(vm, id).Lines[0].RemoveCommand.Execute(null);
        await Assert.That(Lines(Doc(vm, id))).IsEqualTo("D>BombsiteB C>BombsiteB").Because("the remove is what drops the watch");
        vm.Editor.AddLineCommand.Execute(Row(vm, id));
        foreach ((int line, string slot) in (List<(int, string)>)[(0, "B"), (1, "D"), (2, "C")])
        {
            Row(vm, id).Lines[line].Slot = slot;
            vm.Editor.EndEditBurst();
        }

        List<string> logged =
        [
            "replace /steps/0/verb", "remove /steps/0/assignments/1", "replace /steps/0/assignments/2", "replace /steps/0/assignments/2/slot",
            "replace /steps/0/assignments/2/to", "replace /steps/0/assignments/0/watch", "remove /steps/0/assignments/0",
            "add /steps/0/assignments/2", "replace /steps/0/assignments/0/slot", "replace /steps/0/assignments/1/slot",
            "replace /steps/0/assignments/2/slot"
        ];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.PendingOps.Select(o => o.Op + " " + o.Path)).IsEquivalentTo(logged,
                TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("the gestures reproduce revision 8's ops");
            await Assert.That(Lines(Doc(vm, id))).IsEqualTo("B>BombsiteB D>BombsiteB C>");
        }

        while (vm.Session.UndoDepth > depth)
        {
            vm.Session.Undo();
        }

        await Assert.That(Bytes(vm)).IsEqualTo(watched);

        // The same re-lettering with the combos alone: the watch stays on its line wherever the line goes.
        Row(vm, id).Lines[0].Slot = "D";
        vm.Editor.EndEditBurst();
        await Assert.That(Lines(Doc(vm, id))).IsEqualTo("D>BombsiteB/Back,BombsiteB B>BombsiteB C>BombsiteB");
        Row(vm, id).Lines[1].Slot = "D";
        vm.Editor.EndEditBurst();
        using (Assert.Multiple())
        {
            await Assert.That(Lines(Doc(vm, id))).IsEqualTo("B>BombsiteB/Back,BombsiteB D>BombsiteB C>BombsiteB");
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 2);
        }

        vm.Session.Undo();
        vm.Session.Undo();
        await Assert.That(Bytes(vm)).IsEqualTo(watched);
    }

    [Test]
    public async Task RemovedAndReAddedInOneFlyoutSession_KeepsTheLine_AndWritesNothing()
    {
        (StratBookTabViewModel vm, Guid id) = Open(ExecuteBStep8());
        using StratBookTabViewModel scope = vm;
        UpToTheWatch(vm, id);
        string watched = Bytes(vm);
        int depth = vm.Session.UndoDepth;

        StratStepRow row = Row(vm, id);
        row.BeginWho();
        row.WhoOptions.Single(o => o.Slot == "B").IsChecked = false;
        row.WhoOptions.Single(o => o.Slot == "A").IsChecked = true;
        row.WhoOptions.Single(o => o.Slot == "B").IsChecked = true;
        row.WhoOptions.Single(o => o.Slot == "A").IsChecked = false;
        row.CommitWho();
        using (Assert.Multiple())
        {
            await Assert.That(Bytes(vm)).IsEqualTo(watched);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth);
        }
    }

    [Test]
    public async Task WhoSwappingOnePlayerForAnother_WritesARemoveAndAnAdd_AndLeavesTheOthersLinesInPlace()
    {
        StratStep step = ExecuteBStep8();
        step.Verb = "push";
        step.Assignments = [Line("B", "BombsiteB", "Back"), Line("D", "Window"), Line("C", "BombsiteB")];
        step.Assignments[1].Extra = new Dictionary<string, JsonElement> { ["callNote"] = JsonSerializer.SerializeToElement("late") };
        (StratBookTabViewModel vm, Guid id) = Open(step);
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);

        StratStepRow row = Row(vm, id);
        row.BeginWho();
        row.WhoOptions.Single(o => o.Slot == "B").IsChecked = false;
        row.WhoOptions.Single(o => o.Slot == "A").IsChecked = true;
        row.CommitWho();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.PendingOps.Select(o => o.Op + " " + o.Path))
                .IsEquivalentTo(["remove /steps/0/assignments/0", "add /steps/0/assignments/2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(Lines(Doc(vm, id))).IsEqualTo("D>Window+callNote C>BombsiteB A>");
        }

        vm.Session.Undo();
        await Assert.That(Bytes(vm)).IsEqualTo(before);
    }

    [Test]
    public async Task WhileLinesAgree_AnAddedPlayer_CopiesTheSharedPlaceAndWatch()
    {
        StratStep step = ExecuteBStep8();
        step.Verb = "push";
        step.Assignments = [Line("B", "BombsiteB", "Back"), Line("C", "BombsiteB", "Back")];
        (StratBookTabViewModel vm, Guid id) = Open(step);
        using StratBookTabViewModel scope = vm;
        await Assert.That(Row(vm, id).ShowCompact).IsTrue();

        StratStepRow row = Row(vm, id);
        row.BeginWho();
        row.WhoOptions.Single(o => o.Slot == "B").IsChecked = false;
        row.WhoOptions.Single(o => o.Slot == "E").IsChecked = true;
        row.CommitWho();
        await Assert.That(Lines(Doc(vm, id))).IsEqualTo("C>BombsiteB/Back E>BombsiteB/Back");
    }

    [Test]
    public async Task ASlotChange_MovesTheWholeLine_ToAFreeSlot()
    {
        StratStep step = ExecuteBStep8();
        step.Verb = "push";
        step.Assignments = [Line("B", "BombsiteB", "Back", "Window"), Line("C", "Tunnels")];
        step.Assignments[0].Watch!.YawDegrees = 135;
        (StratBookTabViewModel vm, Guid id) = Open(step);
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);

        Row(vm, id).Lines[0].Slot = "E";
        StratStep after = Doc(vm, id);
        using (Assert.Multiple())
        {
            await Assert.That(Lines(after)).IsEqualTo("E>BombsiteB/Back,Window C>Tunnels");
            await Assert.That(after.Assignments![0].Watch!.YawDegrees).IsEqualTo(135);
            await Assert.That(vm.Session.PendingOps.Select(o => o.Op + " " + o.Path)).IsEquivalentTo(["replace /steps/0/assignments/0/slot"]);
        }

        vm.Session.Undo();
        await Assert.That(Bytes(vm)).IsEqualTo(before);
    }

    [Test]
    public async Task ASlotChange_ToATakenSlot_SwapsTheTwoLines_AndUndoRestoresTheBytes_CarriedPositionsIncluded()
    {
        StratStep step = ExecuteBStep8();
        step.Verb = "push";
        step.Assignments = [Line("B", "BombsiteB", "Back"), Line("D", "Window"), Line("C", "BombsiteB")];
        step.Positions =
        [
            new StepPosition { Slot = "B", X = 10, Y = 20, Carried = true }, new StepPosition { Slot = "D", X = 30, Y = 40 },
            new StepPosition { Slot = "C", X = 50, Y = 60, Carried = true }
        ];
        (StratBookTabViewModel vm, Guid id) = Open(step);
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);
        int depth = vm.Session.UndoDepth;

        await Assert.That(Row(vm, id).Lines[0].SlotOptions).IsEquivalentTo(StratVocabulary.Slots).Because("a taken slot is a swap");
        Row(vm, id).Lines[0].Slot = "D";
        StratStep swapped = Doc(vm, id);
        using (Assert.Multiple())
        {
            await Assert.That(Lines(swapped)).IsEqualTo("D>BombsiteB/Back B>Window C>BombsiteB");
            await Assert.That(swapped.Positions.Select(p => p.Slot)).IsEquivalentTo(["D", "C"])
                .Because("B's carried position goes with its new destination; D's was placed by hand; C is untouched");
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(Row(vm, id).Lines.Select(l => l.Slot ?? "")).IsEquivalentTo(["D", "B", "C"]);
        }

        // Wheeling on to a free slot and back through the swap is still one burst from where it began.
        Row(vm, id).Lines[0].Slot = "E";
        await Assert.That(Lines(Doc(vm, id))).IsEqualTo("E>BombsiteB/Back D>Window C>BombsiteB");
        Row(vm, id).Lines[0].Slot = "B";
        using (Assert.Multiple())
        {
            await Assert.That(Bytes(vm)).IsEqualTo(before);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth);
        }

        vm.Editor.EndEditBurst();
        Row(vm, id).Lines[2].Slot = "B";
        await Assert.That(Lines(Doc(vm, id))).IsEqualTo("C>BombsiteB/Back D>Window B>BombsiteB");
        vm.Session.Undo();
        await Assert.That(Bytes(vm)).IsEqualTo(before);
    }

    [Test]
    public async Task ASwapAndAWhoChange_Commit_PhraseBothSlots_AndTheLogReplaysToTheSameBytes()
    {
        StratDocument document = FiveSteps();
        StratStep step = document.Steps[0];
        step.To = null;
        step.Verb = "push";
        step.Assignments = [Line("B", "BombsiteB", "Back"), Line("D", "Window"), Line("C", "BombsiteB")];
        (StratStore store, StratSession session) = Opened(document);

        session.Apply(StratLinePatches.ChangeSlot(session.Document!.Steps[0], "/steps/0", 0, "D"));
        StratSaveResult swap = session.Commit()!;
        session.Apply(StratLinePatches.SetWho(session.Document!.Steps[0], "/steps/0", ["B", "D", "A"]));
        StratSaveResult who = session.Commit()!;
        IReadOnlyList<HistoryEntry> log = store.History(document.Id);
        using (Assert.Multiple())
        {
            await Assert.That(swap.Saved && who.Saved).IsTrue();
            await Assert.That(log[^2].Summary).Contains("B slot B → D").And.Contains("slot D → B");
            await Assert.That(log[^1].Summary).Contains("C removed").And.Contains("A added");
            await Assert.That(Lines(session.Document!.Steps[0])).IsEqualTo("D>BombsiteB/Back B>Window A>");
            await Assert.That(StratStore.Serialize(StratHistory.Materialize(log, log[^1].Revision)!))
                .IsEqualTo(StratStore.Serialize(session.Document!));
        }
    }

    [Test]
    public async Task ChangeSlot_OnTheWriter_SwapsAndKeepsUnknownFields_WithTheirLine()
    {
        StratDocument document = FiveSteps();
        StratStep step = document.Steps[0];
        step.To = null;
        step.Verb = "push";
        step.Assignments = [Line("A", "BombsiteA", "Ramp"), Line("B", "Connector")];
        step.Assignments[0].Extra = new Dictionary<string, JsonElement> { ["callNote"] = JsonSerializer.SerializeToElement("first") };
        (StratStore _, StratSession session) = Opened(document);
        string before = StratStore.Serialize(session.Document!);

        List<PatchOp> ops = StratLinePatches.ChangeSlot(session.Document!.Steps[0], "/steps/0", 0, "B");
        session.Apply(ops);
        using (Assert.Multiple())
        {
            await Assert.That(Lines(session.Document!.Steps[0])).IsEqualTo("B>BombsiteA/Ramp+callNote A>Connector");
            await Assert.That(StratLinePatches.ChangeSlot(session.Document!.Steps[0], "/steps/0", 0, "O1")).IsEmpty();
        }

        session.Undo();
        await Assert.That(StratStore.Serialize(session.Document!)).IsEqualTo(before);
    }
}
