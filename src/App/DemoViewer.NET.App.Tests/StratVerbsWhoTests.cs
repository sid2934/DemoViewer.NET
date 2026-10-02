#region

using System.Text.Json;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.StratBook;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Watching by verb, the push and lurk verbs, and Who as a multi-select: each edit one undo entry written in the
///     stored shape, undone to the exact bytes; a lurk's model, checks, projection, exports and history.
/// </summary>
[NotInParallel]
public class StratVerbsWhoTests
{
    private const string LurkFixture = "schema-v1.lurk.dvstrat.json";

    private static readonly Guid LurkId = Guid.Parse("7e1f2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b");

    private static readonly CalloutResolver Mirage = new(CanonicalPlaces.Embedded("de_mirage"));

    private static bool Updating => Environment.GetEnvironmentVariable("PB2D_GOLDEN_UPDATE") == "1";

    private static string Bytes(StratBookTabViewModel vm) => StratStore.Serialize(vm.Session.Document!);

    private static StratStep Plain(double at, string actor, string verb, string? to = null) => new()
    {
        Id = Guid.NewGuid(), AtSeconds = at, Actor = actor, Verb = verb, To = to is null ? null : new PlaceRef { Place = to }
    };

    private static (StratBookTabViewModel Vm, StratStepRow Row, int Index) Open(StratStep step)
    {
        StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
        StratStepEditingTests.Seed(vm, step);
        int index = vm.Session.Document!.Steps.FindIndex(s => s.Id == step.Id);
        return (vm, vm.Editor.Steps[index], index);
    }

    private static void Pick(StratStepRow row, params string[] slots)
    {
        foreach (StratWhoOption option in row.WhoOptions)
        {
            option.IsChecked = slots.Contains(option.Slot);
        }

        row.CommitWho();
    }

    private static async Task UndoesTo(StratBookTabViewModel vm, string before, int entries)
    {
        for (int i = 0; i < entries; i++)
        {
            await Assert.That(vm.Session.Undo()).IsTrue();
        }

        await Assert.That(Bytes(vm)).IsEqualTo(before).Because("undo restores the exact bytes");
    }

    [Test]
    public async Task ALurksWatching_AndItsAreas_AreWrittenApart()
    {
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(Plain(100, "E", "lurk"));
        using StratBookTabViewModel scope = vm;
        row.GroupWatchValue = [new PlaceRef { Place = "Stairs" }, new PlaceRef { Place = "Connector" }];
        StratStep step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(step.Assignments!.Single().Watch!.Places).IsEquivalentTo(["Stairs", "Connector"]);
            await Assert.That(StratLocations.LurkAreas(step.Lurk)).IsEmpty().Because("watching writes no area");
        }

        row.LurkAreasValue = [new PlaceRef { Place = "PalaceInterior" }];
        step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(step.Lurk!.Areas).IsEquivalentTo(["PalaceInterior"]);
            await Assert.That(step.Assignments!.Single().Watch!.Places).IsEquivalentTo(["Stairs", "Connector"]).Because("an area writes no watch");
        }
    }

    // ── Watching by verb ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AVerbWithoutWatching_ClearsTheLinesWatch_ThroughTheLinesWriter_AsOneEntry()
    {
        StratStep hold = Plain(100, "B", "hold");
        hold.Assignments = [new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteA" }, Watch = new StepWatch { Places = ["Stairs"] } }];
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(hold);
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);
        int depth = vm.Session.UndoDepth;

        row.Verb = "move";
        vm.Editor.EndEditBurst();
        StratStep moved = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(moved.Assignments).IsNull().Because("one line with no watch is a plain step");
            await Assert.That(moved.Actor).IsEqualTo("B");
            await Assert.That(moved.To?.Place).IsEqualTo("BombsiteA").Because("a move keeps its place");
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
        }

        await UndoesTo(vm, before, 1);
    }

    [Test]
    public async Task PushAndLurk_ShowTheirFields_AndMoveWatchesNothing()
    {
        (StratBookTabViewModel vm, StratStepRow row, _) = Open(Plain(100, "B", "hold"));
        using StratBookTabViewModel scope = vm;

        row.Verb = "push";
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowFrom).IsTrue();
            await Assert.That(row.ShowGroupPlace).IsTrue();
            await Assert.That(row.ShowGroupWatch).IsTrue();
            await Assert.That(row.ShowLurk).IsFalse();
        }

        row.Verb = "lurk";
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowFrom).IsFalse();
            await Assert.That(row.ShowGroupPlace).IsFalse();
            await Assert.That(row.ShowGroupWatch).IsTrue();
            await Assert.That(row.ShowLurk).IsTrue();
        }

        row.Verb = "move";
        await Assert.That(row.ShowGroupWatch).IsFalse().Because("a moving player watches their path");
    }

    [Test]
    public async Task LeavingLurk_RemovesTheLurk_InTheSameEntry()
    {
        StratStep lurk = Plain(100, "E", "lurk");
        lurk.Lurk = new StepLurk { Areas = ["PalaceInterior"], Rotate = new LurkRotate { When = "on the call" } };
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(lurk);
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);

        row.Verb = "hold";
        vm.Editor.EndEditBurst();
        await Assert.That(vm.Session.Document!.Steps[index].Lurk).IsNull();
        await UndoesTo(vm, before, 1);
    }

    // ── Who ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PickingASecondPlayer_IsOneEntry_AndACompactEditWritesBoth()
    {
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(Plain(100, "B", "hold", "BombsiteA"));
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);
        int depth = vm.Session.UndoDepth;

        Pick(row, "B", "C");
        StratStep step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(step.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(step.To).IsNull();
            await Assert.That(step.Assignments!.Select(l => l.Slot + ">" + l.To?.Place)).IsEquivalentTo(["B>BombsiteA", "C>BombsiteA"]);
            await Assert.That(row.ShowCompact).IsTrue();
            await Assert.That(row.WhoText).IsEqualTo("B, C");
            await Assert.That(row.CanSplit).IsTrue();
        }

        row.GroupPlaceText = "BombsiteB";
        row.GroupWatchValue = [new PlaceRef { Place = "Stairs" }, new PlaceRef { Place = "Connector" }];
        step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(step.Assignments!.Select(l => l.To?.Place ?? "")).IsEquivalentTo(["BombsiteB", "BombsiteB"]);
            await Assert.That(step.Assignments!.All(l => l.Watch!.Places.SequenceEqual(["Stairs", "Connector"]))).IsTrue();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 3);
            await Assert.That(row.ShowCompact).IsTrue();
        }

        await UndoesTo(vm, before, 3);
    }

    [Test]
    public async Task Split_ShowsALinePerPlayer_AndJoinReturnsOnceTheyAgree()
    {
        StratStep step = Plain(100, StratVocabulary.ActorAll, "hold");
        step.Assignments = [new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteA" } }, new StepAssignment { Slot = "C", To = new PlaceRef { Place = "BombsiteA" } }];
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(step);
        using StratBookTabViewModel scope = vm;
        int depth = vm.Session.UndoDepth;

        row.SplitCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowCompact).IsFalse();
            await Assert.That(row.CanJoin).IsTrue().Because("the lines still agree");
            await Assert.That(vm.Editor.ShowsLinesApart(step.Id)).IsTrue();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth).Because("split is a view, not an edit");
        }

        row.Lines[1].PlaceText = "Connector";
        await Assert.That(row.CanJoin).IsFalse();
        row.Lines[1].PlaceText = "BombsiteA";
        await Assert.That(row.ShowCompact).IsFalse().Because("the row stays apart until joined");
        await Assert.That(row.CanJoin).IsTrue();

        row.JoinCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowCompact).IsTrue();
            await Assert.That(row.GroupPlaceText).IsEqualTo(vm.Editor.DisplayPlace("BombsiteA"));
            await Assert.That(vm.Session.Document!.Steps[index].Assignments!.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task SplittingAStepForEveryone_ShowsFiveLines_AndOneEditWritesFive()
    {
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(Plain(100, StratVocabulary.ActorAll, "hold", "BombsiteA"));
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);
        int depth = vm.Session.UndoDepth;

        row.SplitCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowApart).IsTrue();
            await Assert.That(row.Lines.Select(l => l.Slot ?? "")).IsEquivalentTo(StratVocabulary.Slots);
            await Assert.That(row.Lines.All(l => l.IsExplicit)).IsTrue();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth).Because("split writes nothing");
        }

        StratLineRow first = row.Lines[0];
        row.Lines[2].PlaceText = "Connector";
        StratStep step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(step.Assignments!.Select(l => l.Slot + ">" + l.To?.Place))
                .IsEquivalentTo(["A>BombsiteA", "B>BombsiteA", "C>Connector", "D>BombsiteA", "E>BombsiteA"]);
            await Assert.That(step.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(row.Lines[0]).IsSameReferenceAs(first).Because("the rows stay, so focus is kept");
        }

        row.Lines[2].PlaceText = "BombsiteA";
        step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(step.Assignments).IsNull().Because("five bare lines to one place fold back to all");
            await Assert.That(row.ShowApart).IsTrue().Because("the row stays apart until joined");
            await Assert.That(row.CanJoin).IsTrue();
        }

        int joined = vm.Session.UndoDepth;
        row.JoinCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowCompact).IsTrue();
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(joined).Because("join writes nothing");
        }

        await UndoesTo(vm, before, 2);
    }

    [Test]
    public async Task LinesThatDiffer_OpenApart()
    {
        StratStep step = Plain(100, StratVocabulary.ActorAll, "hold");
        step.Assignments = [new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteA" } }, new StepAssignment { Slot = "C" }];
        (StratBookTabViewModel vm, StratStepRow row, _) = Open(step);
        using StratBookTabViewModel scope = vm;
        using (Assert.Multiple())
        {
            await Assert.That(row.ShowCompact).IsFalse();
            await Assert.That(row.CanJoin).IsFalse();
            await Assert.That(row.WhoText).IsEqualTo("B, C");
        }
    }

    [Test]
    public async Task AllFive_FoldToAll_UnlessTheyShareAWatch()
    {
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(Plain(100, "B", "hold", "BombsiteA"));
        using StratBookTabViewModel scope = vm;
        string before = Bytes(vm);

        row.WhoAllCommand.Execute(null);
        row.CommitWho();
        StratStep step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(step.Assignments).IsNull();
            await Assert.That(step.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(step.To?.Place).IsEqualTo("BombsiteA");
            await Assert.That(row.WhoText).IsEqualTo(StratVocabulary.ActorAll);
        }

        int depth = vm.Session.UndoDepth;
        Pick(row, [.. StratVocabulary.Slots]);
        await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth).Because("everyone already takes the step");

        row.GroupWatchValue = [new PlaceRef { Place = "Stairs" }];
        step = vm.Session.Document!.Steps[index];
        using (Assert.Multiple())
        {
            await Assert.That(step.Assignments!.Count).IsEqualTo(5).Because("a shared watch lives on the lines");
            await Assert.That(step.Actor).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(step.To).IsNull();
            await Assert.That(row.ShowCompact).IsTrue();
        }

        await UndoesTo(vm, before, 2);
    }

    [Test]
    public async Task TheOpenFlyout_KeepsItsPicks_ThroughAReprojection()
    {
        (StratBookTabViewModel vm, StratStepRow row, int index) = Open(Plain(100, "B", "hold", "BombsiteA"));
        using StratBookTabViewModel scope = vm;
        row.BeginWho();
        row.WhoOptions.Single(o => o.Slot == "C").IsChecked = true;

        // An unrelated edit, and a reprojection like the one the zones landing runs.
        vm.Editor.Name = "renamed while picking";
        vm.Editor.Project();
        await Assert.That(row.WhoOptions.Single(o => o.Slot == "C").IsChecked).IsTrue().Because("the staged pick survives");

        int depth = vm.Session.UndoDepth;
        row.CommitWho();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.Document!.Steps[index].Assignments!.Select(l => l.Slot)).IsEquivalentTo(["B", "C"]);
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(vm.Session.Document!.Name).IsEqualTo("renamed while picking");
        }
    }

    [Test]
    public async Task TheFlyout_WritesNothing_WhenItsStepOrStratIsGone()
    {
        StratStep step = Plain(100, "B", "hold", "BombsiteA");
        (StratBookTabViewModel vm, StratStepRow row, _) = Open(step);
        using StratBookTabViewModel scope = vm;
        row.BeginWho();
        row.WhoOptions.Single(o => o.Slot == "C").IsChecked = true;
        vm.Editor.RemoveStepCommand.Execute(row);
        int depth = vm.Session.UndoDepth;
        string bytes = Bytes(vm);
        row.CommitWho();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth);
            await Assert.That(Bytes(vm)).IsEqualTo(bytes);
        }

        StratStep other = Plain(90, "B", "hold", "BombsiteA");
        StratStepEditingTests.Seed(vm, other);
        StratStepRow otherRow = vm.Editor.Steps.Single(r => r.Id == other.Id);
        otherRow.BeginWho();
        otherRow.WhoOptions.Single(o => o.Slot == "D").IsChecked = true;
        Guid first = vm.Session.Document!.Id;
        vm.NewStratCommand.Execute(null);
        await Assert.That(vm.Session.Document!.Id).IsNotEqualTo(first);
        StratStepEditingTests.Seed(vm, other);
        int switched = vm.Session.UndoDepth;
        otherRow.CommitWho();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(switched).Because("the flyout opened on another strat");
            await Assert.That(vm.Session.Document!.Steps.Single(s => s.Id == other.Id).Assignments).IsNull();
        }
    }

    [Test]
    public async Task AConeDrag_OnAVerbWithoutWatching_WritesThePositionsYaw()
    {
        StratDocument document = StratCanvasTestData.FiveSteps();
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [], post: a => a());

        // Step 2 is A's move: no watching.
        canvas.SelectStep(document.Steps[1].Id);
        canvas.BeginDrag("A", TokenGrip.Heading);
        canvas.MoveTo("A", new SkiaSharp.SKPoint(600, 100), 0);
        canvas.EndDrag(null);
        StratStep move = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(move.Assignments).IsNull();
            await Assert.That(move.Positions.Single(p => p.Slot == "A").YawDegrees ?? double.NaN).IsEqualTo(90).Within(0.01);
        }

        // Step 3 is B's peek, which watches: the turn is the line's angle.
        canvas.SelectStep(document.Steps[2].Id);
        canvas.BeginDrag("B", TokenGrip.Heading);
        canvas.MoveTo("B", new SkiaSharp.SKPoint(100, 1000), 0);
        canvas.EndDrag(null);
        await Assert.That(session.Document!.Steps[2].Assignments!.Single().Watch!.YawDegrees ?? double.NaN).IsEqualTo(90).Within(0.01);
    }

    [Test]
    public async Task SetOnMap_OnASplitStepForEveryone_WritesOnlyTheSelectedLine()
    {
        StratDocument document = StratCanvasTestData.FiveSteps();
        document.Steps[1].Actor = StratVocabulary.ActorAll;
        document.Steps[1].Verb = "hold";
        document.Steps[1].To = new PlaceRef { Place = "Hut" };
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(StratMapFirstTests.SyntheticZones()), post: a => a());
        canvas.LinesShownApart = id => id == document.Steps[1].Id;
        canvas.SelectStep(document.Steps[1].Id);
        canvas.SelectLine("C");
        await Assert.That(canvas.SetPlaceText).IsEqualTo("Set C's “at” on map");
        await Assert.That(canvas.BeginSetPlace()).IsTrue();
        canvas.TryTagPositionAt(StratMapFirstTests.Upper, 150, 50);
        await Assert.That(session.Document!.Steps[1].Assignments!.Select(l => l.Slot + ">" + l.To?.Place))
            .IsEquivalentTo(["A>Hut", "B>Hut", "C>Ramp", "D>Hut", "E>Hut"]);
    }

    [Test]
    public async Task NoPlayerPicked_WritesNothing()
    {
        (StratBookTabViewModel vm, StratStepRow row, _) = Open(Plain(100, "B", "hold"));
        using StratBookTabViewModel scope = vm;
        int depth = vm.Session.UndoDepth;
        Pick(row);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth);
            await Assert.That(row.WhoOptions.Single(o => o.IsChecked).Slot).IsEqualTo("B").Because("the flyout shows the step again");
        }
    }

    [Test]
    public async Task SetOnMap_OnACompactStep_WritesEveryLine_AndOnALurk_AddsAnArea()
    {
        StratDocument document = StratCanvasTestData.FiveSteps();
        document.Steps[1].Actor = StratVocabulary.ActorAll;
        document.Steps[1].Verb = "hold";
        document.Steps[1].To = null;
        document.Steps[1].Assignments = [new StepAssignment { Slot = "A" }, new StepAssignment { Slot = "C" }];
        document.Steps[3].Verb = "lurk";
        document.Steps[3].To = null;
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(StratMapFirstTests.SyntheticZones()), post: a => a());

        canvas.SelectStep(document.Steps[1].Id);
        await Assert.That(canvas.SetPlaceText).IsEqualTo("Set “at” on map").Because("the lines agree, so the row shows one place");
        await Assert.That(canvas.BeginSetPlace()).IsTrue();
        canvas.TryTagPositionAt(StratMapFirstTests.Upper, 150, 50);
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[1].Assignments!.Select(l => l.Slot + ">" + l.To?.Place)).IsEquivalentTo(["A>Ramp", "C>Ramp"]);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }

        canvas.SelectStep(document.Steps[3].Id);
        await Assert.That(canvas.PlaceTarget).IsEqualTo(StratPlaceTarget.LurkArea);
        await Assert.That(canvas.SetPlaceText).IsEqualTo("Set “lurk area” on map");
        await Assert.That(canvas.BeginSetPlace()).IsTrue();
        canvas.TryTagPositionAt(StratMapFirstTests.Upper, 50, 50);
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[3].Lurk!.Areas).IsEquivalentTo(["Hut"]);
            await Assert.That(session.UndoDepth).IsEqualTo(2);
        }
    }

    // ── Lurk: model, checks, projection, words ────────────────────────────────────────────────────

    internal static StratDocument WithLurk()
    {
        StratDocument document = Minimal(LurkId);
        StratStep lurk = Step(3, 70, "E", "lurk");
        lurk.Lurk = new StepLurk
        {
            Areas = ["PalaceInterior", "Connector"],
            Rotate = new LurkRotate
            {
                AtSeconds = 40, When = "on the call", To = new PlaceRef { Place = "BombsiteB" },
                Extra = new Dictionary<string, JsonElement> { ["futureRotate"] = JsonDocument.Parse("true").RootElement.Clone() }
            }
        };
        document.Steps.Add(lurk);
        return document;
    }

    [Test]
    public async Task AFileWithoutLurk_NeverWritesIt_AndALurkRoundTrips()
    {
        string json = StratStore.Serialize(SchemaSample());
        await Assert.That(json.Contains("\"lurk\":", StringComparison.Ordinal)).IsFalse();

        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = Path.Combine(repo, "tests", "fixtures", "strats", LurkFixture);
        if (Updating)
        {
            File.WriteAllText(path, StratStore.Serialize(WithLurk()) + "\n");
        }

        string original = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        StratDocument loaded = JsonSerializer.Deserialize(original, StratJsonContext.Default.StratDocument)!;
        using (Assert.Multiple())
        {
            await Assert.That(StratStore.Serialize(WithLurk()) + "\n").IsEqualTo(original).Because("this build writes the committed shape");
            await Assert.That(StratStore.Serialize(loaded) + "\n").IsEqualTo(original).Because("load and save is field-identical");
            await Assert.That(loaded.Steps[2].Lurk!.Rotate!.Extra!.ContainsKey("futureRotate")).IsTrue();
            await Assert.That(StratValidator.Validate(loaded, Mirage).Where(i => i.Severity != StratIssueSeverity.Info)).IsEmpty();
        }
    }

    [Test]
    public async Task TheLurkWriter_DropsAnEmptyRotateAndAnEmptyLurk()
    {
        StratStep step = Plain(70, "E", "lurk");
        List<PatchOp> add = StratLurkPatches.Edit(step, "/steps/0", l => l.Rotate = new LurkRotate { When = "  " });
        await Assert.That(add).IsEmpty().Because("a rotate that says nothing is no lurk at all");

        step.Lurk = new StepLurk { Areas = ["Connector"], Rotate = new LurkRotate { AtSeconds = 40 } };
        List<PatchOp> clear = StratLurkPatches.Edit(step, "/steps/0", l => l.Rotate!.AtSeconds = null);
        await Assert.That(clear.Select(o => o.Op + " " + o.Path)).IsEquivalentTo(["remove /steps/0/lurk/rotate"]);

        List<PatchOp> gone = StratLurkPatches.Edit(step, "/steps/0", l =>
        {
            l.Areas.Clear();
            l.Rotate = null;
        });
        await Assert.That(gone.Select(o => o.Op + " " + o.Path)).IsEquivalentTo(["remove /steps/0/lurk"]);
    }

    [Test]
    public async Task TheValidator_WarnsOnUnknownLurkPlaces_AndARotateNotLaterThanTheStep()
    {
        StratDocument document = WithLurk();
        StratStep lurk = document.Steps[2];
        lurk.Lurk!.Areas.Add("Nowhere");
        lurk.Lurk.Rotate!.To = new PlaceRef { Place = "Elsewhere" };
        lurk.Lurk.Rotate.AtSeconds = 70;
        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, Mirage);
        string Warned(string field) => string.Join("; ", issues.Where(i => i.Field == field).Select(i => i.Severity + " " + i.Message));
        using (Assert.Multiple())
        {
            await Assert.That(Warned("/steps/2/lurk/areas/2")).StartsWith("Warning 'Nowhere'");
            await Assert.That(Warned("/steps/2/lurk/rotate/to/place")).StartsWith("Warning 'Elsewhere'");
            await Assert.That(Warned("/steps/2/lurk/rotate/atSeconds")).StartsWith("Warning the rotate at 1:10 is not later");
            await Assert.That(issues.Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
            await Assert.That(StratValidator.Validate(document).Any(i => i.Field.Contains("/lurk/areas", StringComparison.Ordinal))).IsFalse()
                .Because("no places are checked while the zones load");
        }
    }

    [Test]
    public async Task TheRow_MarksLurkIssuesOnTheirFields()
    {
        StratStep lurk = Plain(100, "E", "lurk");
        lurk.Lurk = new StepLurk { Rotate = new LurkRotate { AtSeconds = 105, When = "on the call" } };
        (StratBookTabViewModel vm, StratStepRow row, _) = Open(lurk);
        using StratBookTabViewModel scope = vm;
        using (Assert.Multiple())
        {
            await Assert.That(row.RotateAtIssue?.Message).Contains("not later");
            await Assert.That(row.RowIssue).IsNull();
            await Assert.That(row.RotateAtText).IsEqualTo("1:45");
            await Assert.That(row.RotateWhenText).IsEqualTo("on the call");
        }

        row.RotateAtText = "0:40";
        await Assert.That(vm.Session.Document!.Steps.Single(s => s.Id == lurk.Id).Lurk!.Rotate!.AtSeconds).IsEqualTo(40);
        await Assert.That(row.RotateAtIssue).IsNull();
    }

    // E stands at the origin from 1:50; a lurk at 1:40 rotates at 1:30 to Ramp, 430 units east.
    private static readonly PlaceCentreResolver Centres = (place, _) => place == "Ramp" ? (430, 0) : null;

    private static StratDocument Rotating(double rotateAt = 90, StepPosition? later = null, double laterAt = 60)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_mirage", "T", "default", "lurk", Created);
        StratStep one = Step(1, 110, StratVocabulary.ActorAll, "hold");
        one.Positions = [StratCanvasTestData.Position("E", 0, 0, 90), StratCanvasTestData.Position("A", 500, 500)];
        StratStep two = Step(2, 100, "E", "lurk");
        two.Lurk = new StepLurk { Rotate = new LurkRotate { AtSeconds = rotateAt, To = new PlaceRef { Place = "Ramp" } } };
        StratStep three = Step(3, laterAt, "A", "hold");
        if (later is not null)
        {
            three.Positions = [later];
        }

        document.Steps = [one, two, three];
        return document;
    }

    private static TokenTrack Track(StratDocument document, string slot, PlaceCentreResolver? centres = null) =>
        StratSceneProjection.Build(document, StratPath.MainLine(document), null, centres ?? Centres).Tracks.Single(t => t.Slot == slot);

    private static (float X, float Y, float Yaw) At(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe k) ? (k.X, k.Y, k.YawDegrees) : (float.NaN, float.NaN, float.NaN);

    [Test]
    public async Task ALurkRotate_MovesTheLurkerTowardsThePlace_FromTheRotateTime()
    {
        StratDocument document = Rotating();
        TokenTrack e = Track(document, "E");
        int rotate = StepSchedule.TickFor(90, 115);
        int arrive = rotate + (int)Math.Ceiling(430 / StratSceneProjection.WalkUnitsPerSecond * 64);
        using (Assert.Multiple())
        {
            await Assert.That(At(e, rotate - 1).X).IsEqualTo(0f).Because("the lurk holds until the rotate");
            await Assert.That(At(e, rotate).X).IsEqualTo(0f);
            await Assert.That(At(e, rotate).Yaw).IsEqualTo(0f).Because("the lurker turns to where it runs");
            await Assert.That(At(e, (rotate + arrive) / 2).X).IsBetween(150f, 280f);
            await Assert.That(At(e, arrive).X).IsEqualTo(430f);
            await Assert.That(At(e, arrive + 640).X).IsEqualTo(430f).Because("it stays at the place");
            await Assert.That(Track(document, "A").Keyframes.Count).IsEqualTo(1).Because("only the lurker rotates");
        }

        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document), null, Centres);
        TokenTrack dragged = projection.TrackWith("E", 0, new TokenPlacement(0, 0, 0, 90));
        await Assert.That(At(dragged, arrive).X).IsEqualTo(430f).Because("a drag preview draws the same rotate");

        TokenTrack unknown = Track(document, "E", (_, _) => null);
        await Assert.That(At(unknown, arrive).X).IsEqualTo(0f).Because("without the place's centre nothing moves");
    }

    [Test]
    public async Task ALaterPosition_WinsOverTheRotate_AndARotateNotLaterIsIgnored()
    {
        // An authored entry for E at 1:29, before E could reach Ramp: the token heads there from the rotate instead.
        StratDocument blocked = Rotating(later: StratCanvasTestData.Position("E", 0, 300), laterAt: 89);
        TokenTrack e = Track(blocked, "E");
        int rotate = StepSchedule.TickFor(90, 115);
        using (Assert.Multiple())
        {
            await Assert.That(At(e, rotate).X).IsEqualTo(0f);
            await Assert.That(At(e, StepSchedule.TickFor(89, 115)).Y).IsEqualTo(300f);
            await Assert.That(e.Keyframes.Any(k => k.X == 430f)).IsFalse();
        }

        // One at the step's own time does not move the token.
        TokenTrack notLater = Track(Rotating(100), "E");
        await Assert.That(notLater.Keyframes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ARotateAfterTheLastStep_IsReachable_OnTheTransport_AndInTheExport()
    {
        // E at Hut's centre on the synthetic map's upper floor; the lurk rotates at 0:30 to Ramp, 100 units east,
        // after the last step at 1:00.
        StratDocument document = Rotating(30);
        document.Map = "de_synthetic";
        document.Steps[0].Positions = [new StepPosition { Slot = "E", X = 50, Y = 50, LevelMinZ = -512 }, StratCanvasTestData.Position("A", 150, 50)];
        document.Steps[0].Positions[1].LevelMinZ = -512;
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: _ => Task.FromResult<IZonePlaceResolver?>(StratMapFirstTests.SyntheticZones()), post: a => a());
        canvas.Transport.Seek(0);

        StratSceneProjection projection = canvas.Projection!;
        int rotate = StepSchedule.TickFor(30, 115);
        int arrive = rotate + (int)Math.Ceiling(100 / StratSceneProjection.WalkUnitsPerSecond * 64);
        using (Assert.Multiple())
        {
            await Assert.That(projection.LastTick).IsEqualTo(StepSchedule.TickFor(60, 115));
            await Assert.That(projection.ContentEndTick).IsEqualTo(arrive);
            await Assert.That(canvas.Transport.EndTick).IsEqualTo(arrive).Because("the playhead reaches the arrival");
        }

        canvas.Transport.Seek(arrive);
        await Assert.That(canvas.Transport.Tick).IsEqualTo(arrive);

        StratExportCapture capture = canvas.CaptureForExport()!;
        ExportRangeOption range = StratExportJob.Ranges(capture.Projection)[0];
        StratFrameSource source = new(StratExportJob.BuildSpec(capture, null, range.StartFrame, range.EndFrame, 64, 1.0));
        int frame = Enumerable.Range(0, source.FrameCount).First(i => source.TickAt(i) == arrive);
        PlayerMarker e = source.FrameAt(frame).Markers.Single(m => m.Slot == TokenSlots.OrderOf("E"));
        using (Assert.Multiple())
        {
            await Assert.That(range.EndFrame).IsEqualTo(arrive + StratExportJob.TailTicks);
            await Assert.That(e.WorldX).IsEqualTo(150f).Within(0.01f).Because("the export shows the lurker at Ramp");
        }
    }

    [Test]
    public async Task ARotateToAPlaceOnAnotherFloor_ArrivesOnThatFloor()
    {
        // Ramp has areas on the lower floor only; E stands on the upper one.
        ZoneSet zones = new("de_synthetic", "9f1c02aa", "075a27b3", null, 64,
            [new ZoneFloor(-512, -528, 100_000), new ZoneFloor(-2048, -100_000, -528)],
            [new ZonePlace(0, "Hut", PlaceOrigin.Baked), new ZonePlace(1, "Ramp", PlaceOrigin.Baked)],
            [],
            [
                new ZoneArea(1, 0, -512, true, -400, [0, 0, 100, 0, 100, 100, 0, 100]),
                new ZoneArea(2, 1, -2048, true, -1900, [100, 0, 300, 0, 300, 100, 100, 100])
            ],
            [(1, 2)], [(0, 1)], null);
        StratPlaceCentres centres = StratPlaceCentres.From(zones);
        await Assert.That(centres.Arrival("Ramp", -512)).IsEqualTo((200d, 50d, -2048d));
        await Assert.That(centres.Arrival("Hut", -512)).IsEqualTo((50d, 50d, -512d));

        StratDocument document = Rotating();
        document.Steps[0].Positions[0].LevelMinZ = -512;
        StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document), null,
            (place, level) => centres.Centre(place, level), (place, level) => centres.Arrival(place, level));
        TokenTrack track = projection.Tracks.Single(t => t.Slot == "E");
        using (Assert.Multiple())
        {
            await Assert.That(track.Keyframes[^1].LevelMinZ).IsEqualTo(-2048d);
            await Assert.That(track.Keyframes[^1].X).IsEqualTo(200f);
        }
    }

    [Test]
    public async Task ANewStep_CarriesTheLurkerOnlyUntilItsRotate()
    {
        // The rotate is at 1:30; a new step after the lurk at 1:35 comes before it, one at 1:25 after it.
        StratDocument document = Rotating();
        List<StepPosition> before = StratStepCarry.PositionsAt(document, 1, null, Centres, 95);
        List<StepPosition> after = StratStepCarry.PositionsAt(document, 1, null, Centres, 85);
        List<StepPosition> nowhere = StratStepCarry.PositionsAt(document, 1, null, (_, _) => null, 85);
        using (Assert.Multiple())
        {
            await Assert.That(before.Single(p => p.Slot == "E").X).IsEqualTo(0).Because("the lurker has not rotated yet");
            await Assert.That(after.Any(p => p.Slot == "E")).IsFalse().Because("the rotate has moved it");
            await Assert.That(after.Any(p => p.Slot == "A")).IsTrue();
            await Assert.That(nowhere.Single(p => p.Slot == "E").X).IsEqualTo(0).Because("a place that does not resolve moves nothing");
        }

        PatchOp added = StepAuthoringPatches.AddCarriedStep(document, 1, 85, Guid.NewGuid(), null, Centres);
        await Assert.That(added.Value!["positions"]!.AsArray().Any(p => p!["slot"]!.GetValue<string>() == "E")).IsFalse();
    }

    [Test]
    public async Task TheSheets_AndTheHistory_ReadTheLurk()
    {
        StratDocument document = WithLurk();
        string line = StratStepPhrasing.Phrase(document.Steps[2], null);
        using (Assert.Multiple())
        {
            await Assert.That(line).IsEqualTo("E lurks Palace Interior, Connector; rotate to Bombsite B at 0:40 or on the call");
            await Assert.That(StratTextExporter.CallSheet(document)).Contains("- **1:10** E lurks Palace Interior, Connector; rotate to Bombsite B at 0:40 or on the call");
            await Assert.That(RoleSheet.Derive(document, "E").Lines.Single().Text)
                .IsEqualTo("E lurks Palace Interior, Connector; rotate to Bombsite B at 0:40 or on the call");
            await Assert.That(StratStepPhrasing.LurkText(new StepLurk { Rotate = new LurkRotate { When = "bomb planted" } }, null))
                .IsEqualTo("rotate bomb planted");
        }

        System.Text.Json.Nodes.JsonNode before = StratHistory.ToNode(document);
        StratStep step = document.Steps[2];
        string Say(Action<StepLurk> edit) => StratDiffPhrasing.Summary(before, StratLurkPatches.Edit(step, "/steps/2", edit));
        using (Assert.Multiple())
        {
            await Assert.That(Say(l => l.Rotate!.AtSeconds = 35)).IsEqualTo("E's lurk: rotate time 0:40 → 0:35");
            await Assert.That(Say(l => l.Rotate!.When = "on contact")).IsEqualTo("E's lurk: rotate condition set to on contact");
            await Assert.That(Say(l => l.Rotate!.To!.Place = "BombsiteA")).IsEqualTo("E's lurk: rotate to Bombsite B → Bombsite A");
            await Assert.That(Say(l => l.Areas = ["Connector"])).IsEqualTo("E's lurk: lurk areas set to Connector");
            await Assert.That(Say(l =>
            {
                l.Areas.Clear();
                l.Rotate = null;
            })).IsEqualTo("E's lurk: lurk removed");
            await Assert.That(StratDiffPhrasing.Summary(StratHistory.ToNode(Minimal(LurkId)),
                    [PatchOp.AddOp("/steps/2", JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep))]))
                .IsEqualTo("step added: E lurks Palace Interior, Connector at 1:10");
        }
    }

    [Test]
    public async Task TheDefaultTemplate_LurksAndRotatesOnTheCall()
    {
        StratStep lurk = StratTemplates.BuildSteps(StratTemplates.Find("default")!).Single(s => s.Actor == "E");
        using (Assert.Multiple())
        {
            await Assert.That(lurk.Verb).IsEqualTo("lurk");
            await Assert.That(lurk.Lurk!.Rotate!.When).IsEqualTo("on the call");
            await Assert.That(lurk.Lurk.Areas).IsEmpty().Because("templates name only the bomb sites");
        }
    }
}
