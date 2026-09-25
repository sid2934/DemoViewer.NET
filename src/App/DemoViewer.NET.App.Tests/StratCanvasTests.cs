#region

using System.Text.Json.Nodes;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.Strats;
using SkiaSharp;
using static DemoViewer.NET.AppTests.StratCanvasTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Step Authoring canvas (step-authoring.md §3.8, §3.10, §9 step 5), driven through its view-model with a
///     hand-cranked clock and no window: a five-step strat plays back and scrubs; a token drag, a stroke, an erase
///     and each step key are one entry on the strat session's one undo stack, and undo reaches the ink as a
///     migration; a branch path plays the target's steps with positions inherited across the join; and the keys
///     come off the shared keymap.
/// </summary>
[NotInParallel]
public class StratCanvasTests
{
    private const int Step1 = 320, Step2 = 960, Step3 = 1600, Step4 = 2240, Step5 = 2880;

    // Highest index first, so each pointer still names the stroke it meant.
    private static readonly string[] _erasedPaths = ["/steps/3/strokes/2", "/steps/3/strokes/1", "/steps/3/strokes/0"];

    private static PlayerMarker Marker(StratCanvasViewModel canvas, string slot) =>
        canvas.CurrentFrame.Markers.Single(m => m.Label == slot);

    /// <summary>The plan's "done" for Step Authoring: a five-step strat plays back and scrubs.</summary>
    [Test]
    public async Task AFiveStepStrat_PlaysBack_AndScrubs()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        ManualTicker ticker = new();
        using StratCanvasViewModel canvas = Canvas(session, ticker);

        StratSceneProjection projection = canvas.Projection!;
        using (Assert.Multiple())
        {
            await Assert.That(projection.Ticks).IsEquivalentTo(new[] { Step1, Step2, Step3, Step4, Step5 });
            await Assert.That(canvas.Timeline.TotalFrames).IsEqualTo(Step5 + 1);
            await Assert.That(canvas.Timeline.Markers.Count(m => m.TrackId == StepTrack.TrackId)).IsEqualTo(5);
            await Assert.That(canvas.Transport.StartTick).IsEqualTo(Step1);
            await Assert.That(canvas.Transport.EndTick).IsEqualTo(Step5);
        }

        // Before the first step nothing is placed yet, so nothing is drawn.
        await Assert.That(canvas.CurrentFrame.Markers.Count).IsEqualTo(0);

        // ] walks the steps: each lands on its tick, is active, and shows its keyframes and its own strokes.
        await Assert.That(canvas.ExecuteAction(Playback2DAction.NextStep)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Transport.Tick).IsEqualTo(Step1);
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(0);
            await Assert.That(canvas.CurrentFrame.Time.IsDiscontinuity).IsTrue();
            await Assert.That(canvas.CurrentFrame.Markers.Count).IsEqualTo(6).Because("A to E and O1 are placed at step 1");
            await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(0f);
            await Assert.That(Marker(canvas, "1").Team).IsEqualTo(3).Because("an opponent draws in the other side's colour");
            await Assert.That(canvas.CurrentFrame.GameInfo.RoundTime).IsEqualTo("1:50");
            await Assert.That(Ink(canvas, ArrowId).Time.OpacityAt(Step1)).IsEqualTo(1.0);
            await Assert.That(Ink(canvas, TextId).Time.OpacityAt(Step1)).IsEqualTo(0.0);
        }

        for (int i = 1; i < 5; i++)
        {
            await Assert.That(canvas.ExecuteAction(Playback2DAction.NextStep)).IsTrue();
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(i);
            await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(600f * i);
        }

        await Assert.That(canvas.ExecuteAction(Playback2DAction.NextStep)).IsFalse().Because("the last step has no next");
        await Assert.That(Ink(canvas, TextId).Time.OpacityAt(Step3)).IsEqualTo(1.0);

        // Scrub back to the middle of step 1's window: A halfway along its move, B and C still where step 1
        // put them (the stationary rule), and the frame is a jump.
        canvas.Timeline.RequestSeekToFrame((Step1 + Step2) / 2);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Transport.Tick).IsEqualTo((Step1 + Step2) / 2);
            await Assert.That(canvas.ActiveStepIndex).IsEqualTo(0);
            await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(300f);
            await Assert.That(Marker(canvas, "B").WorldY).IsEqualTo(0f);
            await Assert.That(canvas.CurrentFrame.Time.IsDiscontinuity).IsTrue();
        }

        // B moves only in step 2's window, the segment ending at its next entry.
        canvas.Timeline.RequestSeekToFrame((Step2 + Step3) / 2);
        await Assert.That(Marker(canvas, "B").WorldY).IsEqualTo(450f);
        await Assert.That(Marker(canvas, "C").WorldY).IsEqualTo(0f);

        // Play from step 1: the clock advances at 64 ticks a wall second, frames are continuous, and it stops at
        // the last step with every token where step 5 put it.
        canvas.ExecuteAction(Playback2DAction.PrevStep);
        await Assert.That(canvas.Transport.Tick).IsEqualTo(Step2);
        canvas.Timeline.RequestSeekToFrame(Step1);
        await Assert.That(canvas.ExecuteAction(Playback2DAction.TogglePlay)).IsTrue();
        await Assert.That(canvas.IsPlaying).IsTrue();

        ticker.Fire(1.0);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Transport.Tick).IsEqualTo(Step1 + 64);
            await Assert.That(canvas.CurrentFrame.Time.IsDiscontinuity).IsFalse();
            await Assert.That(canvas.CurrentFrame.Time.Tick).IsEqualTo(Step1 + 64);
        }

        int frames = 1;
        while (ticker.Fire(0.5))
        {
            frames++;
        }

        using (Assert.Multiple())
        {
            await Assert.That(canvas.IsPlaying).IsFalse();
            await Assert.That(canvas.Transport.Tick).IsEqualTo(Step5);
            await Assert.That(frames).IsEqualTo(1 + (Step5 - Step1 - 64) / 32);
            await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(2400f);
            await Assert.That(Marker(canvas, "C").WorldY).IsEqualTo(-600f);
            await Assert.That(canvas.CurrentFrame.GameInfo.RoundTime).IsEqualTo("1:10");
        }

        // Scrubbing is pure in the tick: every sampled tick shows the same world backwards as forwards.
        List<(float X, float Y)> forward = [];
        for (int tick = Step1; tick <= Step5; tick += 160)
        {
            canvas.Timeline.RequestSeekToFrame(tick);
            forward.Add((Marker(canvas, "B").WorldX, Marker(canvas, "B").WorldY));
        }

        List<(float X, float Y)> backward = [];
        for (int tick = Step1 + (Step5 - Step1) / 160 * 160; tick >= Step1; tick -= 160)
        {
            canvas.Timeline.RequestSeekToFrame(tick);
            backward.Add((Marker(canvas, "B").WorldX, Marker(canvas, "B").WorldY));
        }

        backward.Reverse();
        await Assert.That(backward).IsEquivalentTo(forward);
    }

    [Test]
    public async Task ATokenDrag_OfFortyMoves_IsOneReplace_WithThePreDragFrom_AndUndoPutsItBack()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
        canvas.Timeline.RequestSeekToFrame(Step2 + 10);
        await Assert.That(canvas.ActiveStepIndex).IsEqualTo(1);

        List<IReadOnlyList<PatchOp>> applied = [];
        session.OpsApplied += applied.Add;
        int depth = session.UndoDepth;

        canvas.BeginDrag("A", TokenGrip.Body);
        await Assert.That(canvas.Transport.Tick).IsEqualTo(Step2).Because("a drag shows the moment it writes");
        for (int i = 1; i <= 40; i++)
        {
            canvas.MoveTo("A", new SKPoint(600 + i * 5, i * 10), 0);
        }

        await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(800f).Because("the drag shows before it commits");
        await Assert.That(applied.Count).IsEqualTo(0);
        canvas.EndDrag(null);

        PatchOp op = applied.Single().Single();
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(op.Op).IsEqualTo(PatchOp.Replace);
            await Assert.That(op.Path).IsEqualTo("/steps/1/positions/0");
            await Assert.That(op.From!["x"]!.GetValue<double>()).IsEqualTo(600);
            await Assert.That(op.Value!["x"]!.GetValue<double>()).IsEqualTo(800);
            await Assert.That(op.Value!["y"]!.GetValue<double>()).IsEqualTo(400);
            await Assert.That(session.Document!.Steps[1].Positions[0].X).IsEqualTo(800);
            await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(800f);
        }

        await Assert.That(canvas.ExecuteAction(Playback2DAction.Undo)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[1].Positions[0].X).IsEqualTo(600);
            await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(600f);
            await Assert.That(canvas.AnnotationSession!.Document.UndoDepth).IsEqualTo(0);
        }

        await Assert.That(canvas.ExecuteAction(Playback2DAction.Redo)).IsTrue();
        await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(800f);
    }

    [Test]
    public async Task ADrag_OnAStepWithNoEntryForTheSlot_AddsOne_AndACancelChangesNothing()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
        canvas.Timeline.RequestSeekToFrame(Step2);

        canvas.BeginDrag("D", TokenGrip.Body);
        canvas.MoveTo("D", new SKPoint(999, 999), 0);
        canvas.CancelDrag();
        using (Assert.Multiple())
        {
            await Assert.That(Marker(canvas, "D").WorldX).IsEqualTo(300f);
            await Assert.That(session.UndoDepth).IsEqualTo(0);
        }

        canvas.BeginDrag("D", TokenGrip.Body);
        canvas.MoveTo("D", new SKPoint(350, 50), 0);
        canvas.EndDrag(45);

        StepPosition added = session.Document!.Steps[1].Positions.Single(p => p.Slot == "D");
        using (Assert.Multiple())
        {
            await Assert.That(added.X).IsEqualTo(350);
            await Assert.That(added.YawDegrees).IsEqualTo(45).Because("a Shift release writes the facing");
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }

        // A heading drag turns without moving.
        canvas.BeginDrag("D", TokenGrip.Heading);
        canvas.MoveTo("D", new SKPoint(350, 150), 0);
        canvas.EndDrag(null);
        StepPosition turned = session.Document!.Steps[1].Positions.Single(p => p.Slot == "D");
        await Assert.That(turned.X).IsEqualTo(350);
        await Assert.That(turned.YawDegrees).IsEqualTo(90);
    }

    [Test]
    public async Task AStroke_IsOneAddOnTheActiveStep_AndUndoIsAMigration()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
        canvas.Timeline.RequestSeekToFrame(Step2 + 100);
        AnnotationSession ink = canvas.AnnotationSession!;

        // What a shape tool does: one gesture, one element stamped with the session's envelope.
        AnnotationElement arrow = new(Guid.NewGuid(), AnnotationKind.Arrow, AnnotationStyle.Default,
            new SpaceRef.World(0), ink.EnvelopeForNewElement(canvas.Transport.Tick),
            [new InkPoint(10, 10, 0.5f), new InkPoint(500, 500, 0.5f)], null);
        using (ink.Document.BeginGesture("arrow"))
        {
            ink.Document.Apply(new DocDelta.Add(arrow, ink.Document.Elements.Count));
        }

        using (Assert.Multiple())
        {
            await Assert.That(arrow.Time.FromTick).IsEqualTo(Step2).Because("the tool stamps the active step's window");
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(session.Document!.Steps[1].Strokes.Count).IsEqualTo(1);
            await Assert.That(session.Document.Steps[1].Strokes[0]["id"]!.GetValue<string>()).IsEqualTo(arrow.Id.ToString());
            await Assert.That(session.Document.Steps[1].Strokes[0].ContainsKey("fromTick")).IsFalse();
            await Assert.That(ink.Document.UndoDepth).IsEqualTo(0).Because("the session holds the history, not the ink");
            await Assert.That(Ink(canvas, arrow.Id).Time.UntilTick).IsEqualTo(Step3 - 1);
        }

        int version = ink.Document.Version;
        await Assert.That(session.Undo()).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(ink.Document.Elements.Any(e => e.Id == arrow.Id)).IsFalse();
            await Assert.That(ink.Document.UndoDepth).IsEqualTo(0);
            await Assert.That(ink.Document.Version).IsGreaterThan(version);
        }
    }

    [Test]
    public async Task AnEraseOfThreeStrokes_IsThreeRemoves_WithTheirFrom()
    {
        StratDocument document = FiveSteps();
        document.Steps[3].Strokes =
        [
            .. Enumerable.Range(0, 3).Select(i => JsonNode.Parse(
                $$"""{ "id": "{{Guid.NewGuid()}}", "kind": "Line", "space": "world", "levelMinZ": 0, "points": [{{i}}, 0, 0.5, {{i}}, 100, 0.5] }""")!.AsObject())
        ];
        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
        AnnotationDocument ink = canvas.AnnotationSession!.Document;

        List<IReadOnlyList<PatchOp>> applied = [];
        session.OpsApplied += applied.Add;
        List<Guid> erased = [.. ink.Elements.Where(e => e.Kind == AnnotationKind.Line).Select(e => e.Id)];
        using (ink.BeginGesture("erase"))
        {
            foreach (Guid id in erased)
            {
                ink.Apply(new DocDelta.Remove(id));
            }
        }

        IReadOnlyList<PatchOp> ops = applied.Single();
        using (Assert.Multiple())
        {
            await Assert.That(ops.Count).IsEqualTo(3);
            await Assert.That(ops.All(o => o.Op == PatchOp.Remove && o.From is JsonObject)).IsTrue();
            await Assert.That(ops.Select(o => o.Path)).IsEquivalentTo(_erasedPaths);
            await Assert.That(session.Document!.Steps[3].Strokes.Count).IsEqualTo(0);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }

        session.Undo();
        await Assert.That(ink.Elements.Count(e => e.Kind == AnnotationKind.Line)).IsEqualTo(3);
    }

    [Test]
    public async Task TheStepKeys_EachMakeOneUndoEntry()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
        canvas.Timeline.RequestSeekToFrame(Step2 + 320);

        // Shift+N: a step after the active one, at the playhead's round-clock time, made active.
        await Assert.That(canvas.ExecuteAction(Playback2DAction.AddStep)).IsTrue();
        StratStep inserted = session.Document!.Steps[2];
        using (Assert.Multiple())
        {
            await Assert.That(session.Document.Steps.Count).IsEqualTo(6);
            await Assert.That(inserted.AtSeconds).IsEqualTo(95.0);
            await Assert.That(canvas.ActiveStep!.Id).IsEqualTo(inserted.Id);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }

        // Ctrl+D on step 1: its positions and strokes 5 s later, the strokes under new ids.
        canvas.Timeline.RequestSeekToFrame(Step1);
        await Assert.That(canvas.ExecuteAction(Playback2DAction.DuplicateStep)).IsTrue();
        StratStep copy = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(copy.AtSeconds).IsEqualTo(105.0);
            await Assert.That(copy.Positions.Count).IsEqualTo(6);
            await Assert.That(copy.Strokes.Single()["id"]!.GetValue<string>()).IsNotEqualTo(ArrowId.ToString());
            await Assert.That(session.UndoDepth).IsEqualTo(2);
        }

        // Ctrl+X on the copy clears its strokes only.
        await Assert.That(canvas.ExecuteAction(Playback2DAction.ClearAnnotations)).IsTrue();
        await Assert.That(session.Document!.Steps[1].Strokes.Count).IsEqualTo(0);
        await Assert.That(session.Document.Steps[0].Strokes.Count).IsEqualTo(1);

        // Ctrl+Delete on step 2 of the original takes the branch that hangs from it too.
        canvas.Timeline.RequestSeekToFrame(canvas.Projection!.Ticks[2]);
        await Assert.That(canvas.ActiveStep!.Id).IsEqualTo(StratTestData.StepId(2));
        await Assert.That(canvas.ExecuteAction(Playback2DAction.DeleteStep)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps.Any(s => s.Id == StratTestData.StepId(2))).IsFalse();
            await Assert.That(session.Document.Branches.Count).IsEqualTo(0);
            await Assert.That(session.UndoDepth).IsEqualTo(4);
        }

        // Ctrl+Z walks them back in order, through the one history.
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(canvas.ExecuteAction(Playback2DAction.Undo)).IsTrue();
        }

        await Assert.That(session.Document!.Steps.Count).IsEqualTo(5);
        await Assert.That(session.Document.Branches.Count).IsEqualTo(1);
        await Assert.That(canvas.ExecuteAction(Playback2DAction.Undo)).IsFalse();
    }

    [Test]
    public async Task ABranchPath_PlaysTheTargetSteps_WithPositionsInheritedAcrossTheJoin()
    {
        (StratStore store, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker(), store);

        await Assert.That(canvas.HasBranches).IsTrue();
        int undo = session.UndoDepth;
        canvas.SelectedPath = canvas.PathOptions.Single(o => o.BranchId == BranchId);

        StratSceneProjection projection = canvas.Projection!;
        using (Assert.Multiple())
        {
            await Assert.That(projection.Path.Select(p => p.Step.Id))
                .IsEquivalentTo(new[] { StratTestData.StepId(1), StratTestData.StepId(2), StratTestData.StepId(4), StratTestData.StepId(5) });
            await Assert.That(session.UndoDepth).IsEqualTo(undo).Because("a path is view state, not an edit");
            await Assert.That(canvas.Timeline.Markers.Single(m => m.TrackId == StepTrack.TrackId && m.Tick == Step2).Glyph)
                .IsEqualTo("2" + StepTrack.ForkGlyph);
        }

        // Step 3 is skipped, so B never walks to its peek spot, and A goes from step 2's spot to step 4's.
        canvas.Timeline.RequestSeekToFrame(Step4);
        await Assert.That(Marker(canvas, "B").WorldY).IsEqualTo(0f);
        await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(1800f);
        canvas.Timeline.RequestSeekToFrame((Step2 + Step4) / 2);
        await Assert.That(Marker(canvas, "A").WorldX).IsEqualTo(1200f);

        canvas.SelectedPath = canvas.PathOptions[0];
        await Assert.That(canvas.Projection!.Path.Count).IsEqualTo(5);
    }

    [Test]
    public async Task PlaceTokens_PutsEveryUnplacedSlotOnTheMap_AsOneEdit()
    {
        StratDocument document = FiveSteps();
        foreach (StratStep step in document.Steps)
        {
            step.Positions.Clear();
        }

        (StratStore _, StratSession session) = Opened(document);
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
        canvas.ExecuteAction(Playback2DAction.NextStep);
        await Assert.That(canvas.CurrentFrame.Markers.Count).IsEqualTo(0);

        canvas.PlaceTokensCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[0].Positions.Count).IsEqualTo(10);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(canvas.CurrentFrame.Markers.Count).IsEqualTo(10);
        }
    }

    [Test]
    [Arguments(Key.V, KeyModifiers.None, Playback2DAction.ToolToken)]
    [Arguments(Key.N, KeyModifiers.Shift, Playback2DAction.AddStep)]
    [Arguments(Key.D, KeyModifiers.Control, Playback2DAction.DuplicateStep)]
    [Arguments(Key.Delete, KeyModifiers.Control, Playback2DAction.DeleteStep)]
    [Arguments(Key.OemOpenBrackets, KeyModifiers.None, Playback2DAction.PrevStep)]
    [Arguments(Key.OemCloseBrackets, KeyModifiers.None, Playback2DAction.NextStep)]
    public async Task TheStepAuthoringRows_Resolve_InBothScopes(Key key, KeyModifiers modifiers, Playback2DAction expected)
    {
        await Assert.That(Playback2DKeymap.TryResolve(key, modifiers, false, out Playback2DAction idle)).IsTrue();
        await Assert.That(idle).IsEqualTo(expected);
        await Assert.That(Playback2DKeymap.TryResolve(key, modifiers, true, out Playback2DAction drawing)).IsTrue();
        await Assert.That(drawing).IsEqualTo(expected);
        await Assert.That(Playback2DKeymap.FindConflicts(Playback2DKeymap.Default, Playback2DKeymap.ShellReservedGestures)).IsEmpty();
    }

    [Test]
    public async Task TheBracketRows_ReadAsBrackets()
    {
        await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.PrevStep)).IsEqualTo("[");
        await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.NextStep)).IsEqualTo("]");
        await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.AddStep)).IsEqualTo("Shift+N");
    }

    /// <summary>The same rows on the 2D Playback tab have nothing to act on and stay unhandled (§3.7's table).</summary>
    [Test]
    [Arguments(Playback2DAction.ToolToken)]
    [Arguments(Playback2DAction.AddStep)]
    [Arguments(Playback2DAction.DuplicateStep)]
    [Arguments(Playback2DAction.DeleteStep)]
    [Arguments(Playback2DAction.PrevStep)]
    [Arguments(Playback2DAction.NextStep)]
    public async Task OnThe2DTab_TheStepAuthoringRows_AreUnhandled(Playback2DAction action)
    {
        (Playback2DTabViewModel vm, _) = Playback2DActionDispatchTests.Activated();
        await Assert.That(vm.ExecuteAction(action)).IsFalse();
        await Assert.That(vm.Annotations.ActiveTool).IsEqualTo(ToolKind.PanZoom);
    }

    [Test]
    public async Task TheCanvasKeys_ComeOffTheUsersOverrides()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null,
            () => [Playback2DKeymapProfile.Row(Playback2DAction.ToolToken, Key.U, KeyModifiers.None)]);

        await Assert.That(canvas.Keymap.TryResolve(Key.U, KeyModifiers.None, false, out Playback2DAction action)).IsTrue();
        await Assert.That(action).IsEqualTo(Playback2DAction.ToolToken);
        await Assert.That(canvas.ExecuteAction(action)).IsTrue();
        await Assert.That(canvas.IsTokenToolSelected).IsTrue();
        await Assert.That(canvas.IsToolActive).IsTrue().Because("the token tool shadows Space and Esc like any tool");
    }

    [Test]
    public async Task TheCanvas_IsAFrameHostWithNoDemo()
    {
        (StratStore _, StratSession session) = Opened(FiveSteps());
        using StratCanvasViewModel canvas = Canvas(session, new ManualTicker());
        canvas.ExecuteAction(Playback2DAction.NextStep);

        using (Assert.Multiple())
        {
            await Assert.That(canvas.TokenEditor).IsSameReferenceAs(canvas);
            await Assert.That(canvas.IsAnnotationsEnabled).IsTrue();
            await Assert.That(canvas.VisionEngine).IsNull();
            await Assert.That(canvas.CurrentFrame.Map.NetworkedBounds).IsNotNull();
            await Assert.That(canvas.CurrentFrame.Map.ObservedBounds).IsEqualTo(canvas.CurrentFrame.Map.NetworkedBounds!.Value);
            await Assert.That(canvas.CurrentFrame.Map.MapName).IsEqualTo("de_mirage");
        }

        session.Close();
        await Assert.That(canvas.HasDocument).IsFalse();
        await Assert.That(canvas.CurrentFrame.Markers.Count).IsEqualTo(0);
    }

    private static AnnotationElement Ink(StratCanvasViewModel canvas, Guid id) =>
        canvas.AnnotationSession!.Document.Elements.Single(e => e.Id == id);
}
