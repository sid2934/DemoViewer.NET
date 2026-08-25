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

    /// <summary>
    ///     The eraser may only remove what the pane it is dragging in actually DRAWS. A stacked Nuke has
    ///     both floors on screen at once and the same world XY in both, so a hit-test that ignores the
    ///     pane's level deletes the other floor's callout from a band where it was never visible.
    /// </summary>
    [Test]
    public async Task EraserOnOneFloor_LeavesTheOtherFloorsInkAlone()
    {
        (MapSpace space, PaneSet panes) = AnnotationFakes.Panes(new SKSize(600, 400),
            new FloorSlice(-448, -384), new FloorSlice(-384, -128));

        AnnotationDocument document = new();
        AnnotationSession session = new(document) { EraserWorldRadius = 20f };
        FakeToolServices services = new(session, panes);
        EraseTool tool = new();

        AnnotationElement lower = AnnotationFakes.Stroke(
            space: new SpaceRef.World(MapSpace.QuantizeZ(-448)), x: 0, y: 0);
        AnnotationElement upper = AnnotationFakes.Stroke(
            space: new SpaceRef.World(MapSpace.QuantizeZ(-384)), x: 0, y: 0);
        document.Reset([lower, upper]);

        LevelPane lowerPane = panes.Panes[space.IndexOf(MapSpace.IdForZMin(MapSpace.QuantizeZ(-448)))];

        tool.OnPressed(AnnotationFakes.Press(lowerPane, new SKPoint(0, 0)), services);
        tool.OnReleased(AnnotationFakes.Press(lowerPane, new SKPoint(0, 0)), services);

        await Assert.That(document.Elements.Count).IsEqualTo(1)
            .Because("only the stroke the lower pane draws is erasable from the lower pane");
        await Assert.That(document.Elements[0].Id).IsEqualTo(upper.Id);
    }

    /// <summary>
    ///     An entity-anchored stroke is DRAWN at <c>marker + offset</c>, not at the coordinates it was
    ///     authored with. The eraser has to test the same place, or a telestration is un-erasable where
    ///     the user can see it and silently erasable where they cannot.
    /// </summary>
    [Test]
    public async Task EntityAnchored_IsErasedWhereItIsDrawn_NotWhereItWasAuthored()
    {
        Harness h = new();
        AnnotationElement stroke = AnnotationFakes.Stroke(
            space: new SpaceRef.Entity(7ul, 0, 0), x: 0, y: 0);
        h.Document.Reset([stroke]);

        // The player has since walked to (500, 0), so the stroke is drawn there.
        h.Services.Markers.Add(AnnotationFakes.Marker(7ul, 500, 0));

        h.Press(0, 0);
        h.Release(0, 0);
        await Assert.That(h.Document.Elements.Count).IsEqualTo(1)
            .Because("the authored coordinates are not where the stroke is any more");

        h.Press(500, 0);
        h.Release(500, 0);
        await Assert.That(h.Document.Elements).IsEmpty()
            .Because("the eraser must hit the stroke where the player is now");
    }

    /// <summary>You cannot erase what the envelope is not showing you.</summary>
    [Test]
    public async Task StrokeOutsideItsEnvelope_IsNotErased()
    {
        Harness h = new();
        h.Services.CurrentTick = 5000;
        h.Document.Reset([
            AnnotationFakes.Stroke(time: new TimeEnvelope(100, 200, 0, 0), x: 0, y: 0)
        ]);

        h.Press(0, 0);
        h.Release(0, 0);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(1)
            .Because("at tick 5000 the stroke is invisible; erasing it would delete something unseen");
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
