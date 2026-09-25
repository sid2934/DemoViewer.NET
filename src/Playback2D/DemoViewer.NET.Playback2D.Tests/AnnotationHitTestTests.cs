#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The eraser's hit test. Erase is stroke-level by design (§5.4 defers pixel erase explicitly), so
///     the only question is whether the eraser disc touches an element.
/// </summary>
public class AnnotationHitTestTests
{
    [Test]
    public async Task HitsWithinHalfWidthPlusRadius()
    {
        AnnotationElement stroke = Line(6f);

        await Assert.That(AnnotationHitTester.HitTest(stroke, 50, 7, 5)).IsTrue()
            .Because("7 is inside half the 6-unit width plus the 5-unit eraser radius");
        await Assert.That(AnnotationHitTester.HitTest(stroke, 50, 9, 5)).IsFalse();
    }

    [Test]
    public async Task MissesOutsideOutline()
    {
        AnnotationElement stroke = Line(6f);

        await Assert.That(AnnotationHitTester.HitTest(stroke, 50, 400, 4)).IsFalse();
        await Assert.That(AnnotationHitTester.HitTest(stroke, -400, 0, 4)).IsFalse();
    }

    [Test]
    public async Task WideStroke_InteriorPointHits()
    {
        AnnotationElement stroke = Line(24f);

        await Assert.That(AnnotationHitTester.HitTest(stroke, 50, 0, 0)).IsTrue()
            .Because("a point in the middle of a wide stroke is inside its outline polygon");
    }

    [Test]
    public async Task EmptyStroke_NeverHits()
    {
        AnnotationElement empty = new(Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default,
            new SpaceRef.World(0), TimeEnvelope.Static, [], null);

        await Assert.That(AnnotationHitTester.HitTest(empty, 0, 0, 100)).IsFalse();
    }

    [Test]
    public async Task TopmostWinsWhenOverlapping()
    {
        AnnotationDocument doc = new();
        AnnotationElement under = Line(6f);
        AnnotationElement over = Line(6f);
        doc.Apply(new DocDelta.Add(under, 0));
        doc.Apply(new DocDelta.Add(over, 1));

        List<Guid> hits = [];
        int count = AnnotationHitTester.HitTestAll(doc, 50, 0, 4, hits);

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(hits[0]).IsEqualTo(over.Id)
            .Because("the document draws oldest-first, so the LAST element is the one on top");
        await Assert.That(hits[1]).IsEqualTo(under.Id);
    }

    [Test]
    public async Task HitTestAll_NoHits_ReturnsZero_AndClearsTheList()
    {
        AnnotationDocument doc = new();
        doc.Apply(new DocDelta.Add(Line(6f), 0));

        List<Guid> hits = [Guid.NewGuid()];
        int count = AnnotationHitTester.HitTestAll(doc, 0, 5000, 4, hits);

        await Assert.That(count).IsEqualTo(0);
        await Assert.That(hits).IsEmpty();
    }

    [Test]
    [Arguments(AnnotationKind.Line)]
    [Arguments(AnnotationKind.Arrow)]
    public async Task LineAndArrow_HitAlongTheSegment_NotBesideIt(AnnotationKind kind)
    {
        AnnotationElement shape = Shape(kind, 0, 0, 200, 0);

        await Assert.That(AnnotationHitTester.HitTest(shape, 100, 6, 4)).IsTrue()
            .Because("6 is inside half the 6-unit width plus the 4-unit eraser radius");
        await Assert.That(AnnotationHitTester.HitTest(shape, 100, 20, 4)).IsFalse();
        await Assert.That(AnnotationHitTester.HitTest(shape, 260, 0, 4)).IsFalse();
    }

    [Test]
    public async Task Rect_HitsItsEdges_NotItsInterior()
    {
        AnnotationElement rect = Shape(AnnotationKind.Rect, 0, 0, 200, 100);

        await Assert.That(AnnotationHitTester.HitTest(rect, 100, 2, 4)).IsTrue();
        await Assert.That(AnnotationHitTester.HitTest(rect, 198, 50, 4)).IsTrue();
        await Assert.That(AnnotationHitTester.HitTest(rect, 100, 50, 4)).IsFalse()
            .Because("a rectangle has no ink in its middle, and its stored corners are its diagonal");
        await Assert.That(AnnotationHitTester.HitTest(rect, 300, 50, 4)).IsFalse();
    }

    [Test]
    public async Task Ellipse_HitsItsOutline_NotItsCentreOrCorners()
    {
        AnnotationElement ellipse = Shape(AnnotationKind.Ellipse, -100, -50, 100, 50);

        await Assert.That(AnnotationHitTester.HitTest(ellipse, 100, 0, 4)).IsTrue();
        await Assert.That(AnnotationHitTester.HitTest(ellipse, 0, 52, 4)).IsTrue();
        await Assert.That(AnnotationHitTester.HitTest(ellipse, 0, 0, 4)).IsFalse();
        await Assert.That(AnnotationHitTester.HitTest(ellipse, 98, 48, 4)).IsFalse()
            .Because("the bounding box's corner is well outside the inscribed ellipse");
    }

    [Test]
    public async Task FlatEllipse_IsHitAlongItsLine()
    {
        AnnotationElement flat = Shape(AnnotationKind.Ellipse, 0, 0, 200, 0);

        await Assert.That(AnnotationHitTester.HitTest(flat, 100, 3, 4)).IsTrue();
        await Assert.That(AnnotationHitTester.HitTest(flat, 100, 40, 4)).IsFalse();
    }

    [Test]
    public async Task Text_HitsItsLineBox_BelowAndRightOfTheAnchor()
    {
        AnnotationElement label = Label("A short", 0, 0);
        float em = AnnotationText.WorldSize(label.Style.WidthWorld);

        await Assert.That(AnnotationHitTester.HitTest(label, em, -em / 2, 0)).IsTrue()
            .Because("the text hangs below its anchor, which is lower world Y");
        await Assert.That(AnnotationHitTester.HitTest(label, em, em * 2, 4)).IsFalse();
        await Assert.That(AnnotationHitTester.HitTest(label, -em * 2, -em / 2, 4)).IsFalse();
        await Assert.That(AnnotationHitTester.HitTest(label, -em * 2, -em / 2, em * 3)).IsTrue()
            .Because("the box is inflated by the eraser radius");
    }

    [Test]
    public async Task EmptyText_IsHitAtItsAnchor()
    {
        AnnotationElement label = Label("", 50, 50);

        await Assert.That(AnnotationHitTester.HitTest(label, 52, 50, 4)).IsTrue();
        await Assert.That(AnnotationHitTester.HitTest(label, 150, 50, 4)).IsFalse();
    }

    private static AnnotationElement Shape(AnnotationKind kind, float x0, float y0, float x1, float y1) =>
        new(Guid.NewGuid(), kind, AnnotationStyle.Default, new SpaceRef.World(0), TimeEnvelope.Static,
            [new InkPoint(x0, y0, 0.5f), new InkPoint(x1, y1, 0.5f)], null);

    private static AnnotationElement Label(string text, float x, float y) =>
        new(Guid.NewGuid(), AnnotationKind.Text, AnnotationStyle.Default, new SpaceRef.World(0),
            TimeEnvelope.Static, [new InkPoint(x, y, 0.5f)], text);

    private static AnnotationElement Line(float width) =>
        new(Guid.NewGuid(), AnnotationKind.Freehand,
            AnnotationStyle.Default with
            {
                WidthWorld = width
            },
            new SpaceRef.World(0), TimeEnvelope.Static,
            [
                new InkPoint(0, 0, 0.5f), new InkPoint(25, 0, 0.5f), new InkPoint(50, 0, 0.5f),
                new InkPoint(75, 0, 0.5f), new InkPoint(100, 0, 0.5f)
            ],
            null);
}
