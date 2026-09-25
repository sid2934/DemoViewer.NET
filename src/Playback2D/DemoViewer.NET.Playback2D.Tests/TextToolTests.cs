#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The text tool (step-authoring.md §3.2): press places a one-point Text element with an empty
///     string and asks the host for an editor; the typed string lands in the same gesture, and an empty
///     one leaves neither the element nor an undo entry behind.
/// </summary>
public class TextToolTests
{
    [Test]
    public async Task Press_PlacesAnEmptyLabel_AndAsksTheHostForAnEditor()
    {
        Harness h = new();

        h.Press(40, -20);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(1);
        AnnotationElement element = h.Document.Elements[0];
        await Assert.That(element.Kind).IsEqualTo(AnnotationKind.Text);
        await Assert.That(element.Points.Count).IsEqualTo(1);
        await Assert.That(element.Points[0].X).IsEqualTo(40f);
        await Assert.That(element.Points[0].Y).IsEqualTo(-20f);
        await Assert.That(element.Text).IsEqualTo("");

        await Assert.That(h.Services.TextEditRequests).IsEquivalentTo([element.Id]);
        await Assert.That(h.Tool.EditingElementId).IsEqualTo(element.Id);
        await Assert.That(h.Document.IsGestureOpen).IsTrue()
            .Because("the add and the typed text are one gesture, so the mark stays open for the editor");
    }

    [Test]
    public async Task Release_DoesNotCloseTheEdit()
    {
        Harness h = new();

        h.Press(0, 0);
        h.Release(0, 0);

        await Assert.That(h.Document.IsGestureOpen).IsTrue();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(0);
        await Assert.That(h.Document.Undo()).IsFalse()
            .Because("Ctrl+Z must not reach the ink while the editor has the keyboard");
    }

    [Test]
    public async Task Completing_WithText_IsOneUndoEntry()
    {
        Harness h = new();

        h.Press(0, 0);
        h.Release(0, 0);
        bool completed = h.Tool.CompleteEdit(h.Services, "B site smoke");

        await Assert.That(completed).IsTrue();
        await Assert.That(h.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(h.Document.Elements[0].Text).IsEqualTo("B site smoke");
        await Assert.That(h.Document.IsGestureOpen).IsFalse();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(1)
            .Because("placing a label and typing it is one Ctrl+Z");
        await Assert.That(h.Tool.EditingElementId).IsNull();

        h.Document.Undo();
        await Assert.That(h.Document.Elements).IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Completing_Empty_RemovesTheLabel_WithNoUndoEntry(string? typed)
    {
        Harness h = new();

        h.Press(0, 0);
        h.Release(0, 0);
        h.Tool.CompleteEdit(h.Services, typed);

        await Assert.That(h.Document.Elements).IsEmpty();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(0);
        await Assert.That(h.Document.IsGestureOpen).IsFalse();
    }

    [Test]
    public async Task Completing_WithNoEditOpen_ReturnsFalse()
    {
        Harness h = new();

        await Assert.That(h.Tool.CompleteEdit(h.Services, "x")).IsFalse();
    }

    [Test]
    public async Task ASecondPress_ClosesTheFirstEditBeforeOpeningItsOwn()
    {
        Harness h = new();

        h.Press(0, 0);
        h.Release(0, 0);
        h.Press(200, 0);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(1)
            .Because("the untouched first label is rolled back, not left as an invisible element");
        await Assert.That(h.Document.Elements[0].Points[0].X).IsEqualTo(200f);
        await Assert.That(h.Services.TextEditRequests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ADocumentSwapMidEdit_NeverWritesIntoTheNewDocument()
    {
        Harness h = new();

        h.Press(0, 0);
        h.Release(0, 0);

        AnnotationSession other = new(new AnnotationDocument());
        FakeToolServices swapped = new(other, h.Pane);
        h.Tool.CompleteEdit(swapped, "typed for the old demo");

        await Assert.That(other.Document.Elements).IsEmpty();
        await Assert.That(h.Tool.EditingElementId).IsNull();
    }

    [Test]
    public async Task Cancel_DuringThePress_RollsTheLabelBack()
    {
        Harness h = new();

        h.Press(0, 0);
        h.Tool.OnCancelled(h.Services);

        await Assert.That(h.Document.Elements).IsEmpty();
        await Assert.That(h.Document.IsGestureOpen).IsFalse();
    }

    [Test]
    public async Task Style_Space_AndEnvelope_ResolveLikeTheDrawTool()
    {
        Harness h = new();
        h.Session.DefaultVisibility = EnvelopeMode.Fade;
        h.Services.CurrentTick = 640;

        h.Press(0, 0, ToolPointerButton.Right);
        h.Tool.CompleteEdit(h.Services, "hold");

        AnnotationElement element = h.Document.Elements[0];
        await Assert.That(element.Style).IsEqualTo(h.Session.StyleFor(ToolPointerButton.Right));
        await Assert.That(element.Time).IsEqualTo(h.Session.EnvelopeForNewElement(640));
        await Assert.That(element.Space).IsEqualTo(new SpaceRef.World(MapSpace.QuantizeZ(h.Pane.Level.ZMin)));
    }

    private sealed class Harness
    {
        public Harness()
        {
            Pane = AnnotationFakes.Pane(600, 400);
            Document = new AnnotationDocument();
            Session = new AnnotationSession(Document);
            Services = new FakeToolServices(Session, Pane);
        }

        public LevelPane Pane { get; }

        public AnnotationDocument Document { get; }

        public AnnotationSession Session { get; }

        public FakeToolServices Services { get; }

        public TextTool Tool { get; } = new();

        public void Press(float x, float y, ToolPointerButton button = ToolPointerButton.Left) =>
            Tool.OnPressed(Event(x, y, button), Services);

        public void Release(float x, float y) =>
            Tool.OnReleased(Event(x, y, ToolPointerButton.Left), Services);

        private ToolPointerEvent Event(float x, float y, ToolPointerButton button) =>
            new()
            {
                Pane = Pane,
                Screen = Services.WorldToScreen(Pane, new SKPoint(x, y)),
                World = new SKPoint(x, y),
                Pressure = 0.5f,
                Button = button
            };
    }
}
