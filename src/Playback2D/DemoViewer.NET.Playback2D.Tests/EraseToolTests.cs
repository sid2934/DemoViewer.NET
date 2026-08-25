#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The eraser: whole strokes, one undo entry per drag, and nothing at all when the drag touched
///     nothing.
/// </summary>
public class EraseToolTests
{
    [Test]
    public async Task DragAcrossThreeStrokes_RemovesAll_InOneUndoEntry()
    {
        Harness h = new();
        h.AddStroke(0);
        h.AddStroke(200);
        h.AddStroke(400);
        int undoBefore = h.Document.UndoDepth;

        h.Press(0, 0);
        h.Move(200, 0);
        h.Move(400, 0);
        h.Release(400, 0);

        await Assert.That(h.Document.Elements).IsEmpty();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(undoBefore + 1);
    }

    [Test]
    public async Task NoHit_PushesNoUndoEntry()
    {
        Harness h = new();
        h.AddStroke(0);
        int undoBefore = h.Document.UndoDepth;
        int versionBefore = h.Document.Version;

        h.Press(0, 5000);
        h.Move(200, 5000);
        h.Release(400, 5000);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(h.Document.UndoDepth).IsEqualTo(undoBefore);
        await Assert.That(h.Document.Version).IsEqualTo(versionBefore);
    }

    [Test]
    public async Task Undo_RestoresAllErased()
    {
        Harness h = new();
        AnnotationElement a = h.AddStroke(0);
        AnnotationElement b = h.AddStroke(200);

        h.Press(0, 0);
        h.Move(200, 0);
        h.Release(200, 0);
        await Assert.That(h.Document.Elements).IsEmpty();

        h.Document.Undo();

        await Assert.That(h.Document.Elements.Count).IsEqualTo(2);
        await Assert.That(h.Document.Elements[0]).IsEqualTo(a);
        await Assert.That(h.Document.Elements[1]).IsEqualTo(b)
            .Because("a restored stroke must come back byte-identical AND in its original z-order");
    }

    [Test]
    public async Task RemovesSameStrokeOnce()
    {
        Harness h = new();
        h.AddStroke(0);

        h.Press(0, 0);
        for (int i = 0; i < 20; i++)
        {
            h.Move(i, 0);
        }

        h.Release(20, 0);

        await Assert.That(h.Document.Elements).IsEmpty();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(1);

        h.Document.Undo();
        await Assert.That(h.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(h.Document.RedoDepth).IsEqualTo(1);
    }

    [Test]
    public async Task Cancel_RestoresEverythingErasedSoFar_WithNoUndoEntry()
    {
        Harness h = new();
        h.AddStroke(0);
        h.AddStroke(200);

        h.Press(0, 0);
        h.Move(200, 0);
        await Assert.That(h.Document.Elements).IsEmpty();

        h.Tool.OnCancelled(h.Services);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(2);
        await Assert.That(h.Document.UndoDepth).IsEqualTo(0);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Pane = AnnotationFakes.Pane(600, 400);
            Document = new AnnotationDocument();
            Session = new AnnotationSession(Document)
            {
                EraserWorldRadius = 20f
            };
            Services = new FakeToolServices(Session, Pane);
            Tool = new EraseTool();
        }

        public LevelPane Pane { get; }

        public AnnotationDocument Document { get; }

        public AnnotationSession Session { get; }

        public FakeToolServices Services { get; }

        public EraseTool Tool { get; }

        public AnnotationElement AddStroke(float x)
        {
            AnnotationElement element = new(Guid.NewGuid(), AnnotationKind.Freehand,
                AnnotationStyle.Default, new SpaceRef.World(0), TimeEnvelope.Static,
                [new InkPoint(x, 0, 0.5f), new InkPoint(x + 20, 0, 0.5f), new InkPoint(x + 40, 0, 0.5f)],
                null);

            Document.Reset([.. Document.Elements, element]);
            return element;
        }

        public void Press(float x, float y) => Tool.OnPressed(Event(x, y), Services);

        public void Move(float x, float y) => Tool.OnMoved(Event(x, y), Services);

        public void Release(float x, float y) => Tool.OnReleased(Event(x, y), Services);

        private ToolPointerEvent Event(float x, float y) =>
            new()
            {
                Pane = Pane,
                Screen = Services.WorldToScreen(Pane, new SKPoint(x, y)),
                World = new SKPoint(x, y),
                Pressure = 0.5f,
                Button = ToolPointerButton.Left
            };
    }
}
