#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The two-point shape tools (step-authoring.md §3.2): press anchors, moves rubber-band the second
///     point through the wet stroke, release commits exactly <c>[first, last]</c> as one undo entry, Shift
///     constrains, a tap commits nothing, and space and envelope resolve as the pen's do.
/// </summary>
public class ShapeToolTests
{
    [Test]
    [Arguments(ToolKind.Line, AnnotationKind.Line)]
    [Arguments(ToolKind.Arrow, AnnotationKind.Arrow)]
    [Arguments(ToolKind.Rect, AnnotationKind.Rect)]
    [Arguments(ToolKind.Ellipse, AnnotationKind.Ellipse)]
    public async Task Drag_CommitsExactlyFirstAndLast_AsOneUndoEntry(ToolKind tool, AnnotationKind kind)
    {
        Harness h = new(tool);

        h.Press(0, 0);
        h.Move(40, 30);
        h.Move(90, 60);
        h.Move(120, 80);
        h.Release(150, 100);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(1);
        AnnotationElement element = h.Document.Elements[0];
        await Assert.That(element.Kind).IsEqualTo(kind);
        await Assert.That(element.Points.Count).IsEqualTo(2)
            .Because("a shape is its two defining points, never the path the pointer took");
        await Assert.That(element.Points[0].X).IsEqualTo(0f);
        await Assert.That(element.Points[0].Y).IsEqualTo(0f);
        await Assert.That(element.Points[1].X).IsEqualTo(150f);
        await Assert.That(element.Points[1].Y).IsEqualTo(100f);
        await Assert.That(element.Text).IsNull();
        await Assert.That(element.Timing).IsNull();

        await Assert.That(h.Document.UndoDepth).IsEqualTo(1);
        await Assert.That(h.Document.IsGestureOpen).IsFalse();
        await Assert.That(h.Session.Wet.IsActive).IsFalse();

        h.Document.Undo();
        await Assert.That(h.Document.Elements).IsEmpty();
    }

    [Test]
    public async Task Move_RubberBandsTheSecondPoint_ThroughTheWetStroke()
    {
        Harness h = new(ToolKind.Arrow);

        h.Press(10, 20);
        await Assert.That(h.Session.Wet.IsActive).IsTrue();
        await Assert.That(h.Session.Wet.Kind).IsEqualTo(AnnotationKind.Arrow);
        await Assert.That(h.Session.Wet.Points.Count).IsEqualTo(2);

        h.Move(50, 60);
        h.Move(200, 90);

        await Assert.That(h.Session.Wet.Points.Count).IsEqualTo(2)
            .Because("the rubber band replaces its second sample rather than accumulating a path");
        await Assert.That(h.Session.Wet.Points[0].X).IsEqualTo(10f);
        await Assert.That(h.Session.Wet.Points[1].X).IsEqualTo(200f);
        await Assert.That(h.Session.Wet.Points[1].Y).IsEqualTo(90f);
        await Assert.That(h.Document.Elements).IsEmpty()
            .Because("nothing is committed until the release");
    }

    [Test]
    [Arguments(ToolKind.Line)]
    [Arguments(ToolKind.Rect)]
    public async Task Tap_CommitsNothing_AndLeavesNoUndoEntry(ToolKind tool)
    {
        Harness h = new(tool);

        h.Press(100, 100);
        h.Release(100, 100);

        await Assert.That(h.Document.Elements).IsEmpty();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(0);
        await Assert.That(h.Document.IsGestureOpen).IsFalse();
        await Assert.That(h.Session.Wet.IsActive).IsFalse();
    }

    [Test]
    public async Task Cancel_DropsTheShape_AndTheGesture()
    {
        Harness h = new(ToolKind.Ellipse);

        h.Press(0, 0);
        h.Move(80, 80);
        h.Tool.OnCancelled(h.Services);

        await Assert.That(h.Document.Elements).IsEmpty();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(0);
        await Assert.That(h.Document.IsGestureOpen).IsFalse();
        await Assert.That(h.Session.Wet.IsActive).IsFalse();
    }

    [Test]
    [Arguments(ToolKind.Line)]
    [Arguments(ToolKind.Arrow)]
    public async Task Shift_SnapsALineToFortyFiveDegrees_KeepingItsLength(ToolKind tool)
    {
        Harness h = new(tool);

        h.Press(0, 0);
        h.Release(100, 90, ToolModifiers.Shift);

        InkPoint last = h.Document.Elements[0].Points[1];
        double length = Math.Sqrt(100.0 * 100 + 90.0 * 90);

        await Assert.That((double)last.X).IsEqualTo(length / Math.Sqrt(2)).Within(0.01);
        await Assert.That((double)last.Y).IsEqualTo(length / Math.Sqrt(2)).Within(0.01);

        Harness flat = new(tool);
        flat.Press(0, 0);
        flat.Release(200, 30, ToolModifiers.Shift);

        await Assert.That(flat.Document.Elements[0].Points[1].Y).IsEqualTo(0f)
            .Because("a shallow drag with Shift snaps to horizontal");
    }

    [Test]
    [Arguments(ToolKind.Rect)]
    [Arguments(ToolKind.Ellipse)]
    public async Task Shift_MakesABoxASquare_InThePointersQuadrant(ToolKind tool)
    {
        Harness h = new(tool);

        h.Press(0, 0);
        h.Release(-40, 120, ToolModifiers.Shift);

        InkPoint last = h.Document.Elements[0].Points[1];
        await Assert.That(last.X).IsEqualTo(-120f);
        await Assert.That(last.Y).IsEqualTo(120f);
    }

    [Test]
    public async Task Envelope_AndPen_AreResolvedAtPress_LikeTheDrawTool()
    {
        Harness h = new(ToolKind.Rect);
        h.Session.DefaultVisibility = EnvelopeMode.Fade;
        h.Session.HoldTicks = 100;
        h.Services.CurrentTick = 500;

        h.Press(0, 0, button: ToolPointerButton.Right);
        h.Services.CurrentTick = 900;
        h.Release(100, 100, button: ToolPointerButton.Right);

        AnnotationElement element = h.Document.Elements[0];
        await Assert.That(element.Time).IsEqualTo(h.Session.EnvelopeForNewElement(500));
        await Assert.That(element.Style).IsEqualTo(h.Session.StyleFor(ToolPointerButton.Right));
        await Assert.That(element.Space).IsEqualTo(new SpaceRef.World(MapSpace.QuantizeZ(h.Pane.Level.ZMin)));
    }

    [Test]
    public async Task StartedOnATrackedPlayer_TheShapeFollowsThem()
    {
        Harness h = new(ToolKind.Arrow);
        h.Session.AnchorToEntities = true;
        h.Services.Markers.Add(AnnotationFakes.Marker(76561198000000001, 10, 10));

        h.Press(20, 20);
        h.Release(300, 20);

        await Assert.That(h.Document.Elements[0].Space).IsEqualTo(new SpaceRef.Entity(76561198000000001, 10, 10));
    }

    [Test]
    public async Task ANonShapeKind_IsRefused()
    {
        ArgumentOutOfRangeException? thrown = null;
        try
        {
            _ = new ShapeTool(ToolKind.Draw);
        }
        catch (ArgumentOutOfRangeException e)
        {
            thrown = e;
        }

        await Assert.That(thrown).IsNotNull();
    }

    private sealed class Harness
    {
        public Harness(ToolKind kind)
        {
            Pane = AnnotationFakes.Pane(600, 400);
            Document = new AnnotationDocument();
            Session = new AnnotationSession(Document);
            Services = new FakeToolServices(Session, Pane);
            Tool = new ShapeTool(kind);
        }

        public LevelPane Pane { get; }

        public AnnotationDocument Document { get; }

        public AnnotationSession Session { get; }

        public FakeToolServices Services { get; }

        public ShapeTool Tool { get; }

        public void Press(float x, float y, ToolModifiers modifiers = ToolModifiers.None,
            ToolPointerButton button = ToolPointerButton.Left) =>
            Tool.OnPressed(Event(x, y, modifiers, button), Services);

        public void Move(float x, float y, ToolModifiers modifiers = ToolModifiers.None) =>
            Tool.OnMoved(Event(x, y, modifiers, ToolPointerButton.Left), Services);

        public void Release(float x, float y, ToolModifiers modifiers = ToolModifiers.None,
            ToolPointerButton button = ToolPointerButton.Left) =>
            Tool.OnReleased(Event(x, y, modifiers, button), Services);

        private ToolPointerEvent Event(float x, float y, ToolModifiers modifiers, ToolPointerButton button) =>
            new()
            {
                Pane = Pane,
                Screen = Services.WorldToScreen(Pane, new SKPoint(x, y)),
                World = new SKPoint(x, y),
                Pressure = 0.5f,
                Button = button,
                Modifiers = modifiers
            };
    }
}
