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
    public async Task NonFreehandKind_Throws()
    {
        AnnotationElement arrow = new(Guid.NewGuid(), AnnotationKind.Arrow, AnnotationStyle.Default,
            new SpaceRef.World(0), TimeEnvelope.Static, [new InkPoint(0, 0, 0.5f)], null);

        NotSupportedException? thrown = null;
        try
        {
            AnnotationHitTester.HitTest(arrow, 0, 0, 4);
        }
        catch (NotSupportedException e)
        {
            thrown = e;
        }

        await Assert.That(thrown).IsNotNull()
            .Because("a silent 'no hit' for a shape kind nobody implemented is an eraser that " +
                     "mysteriously refuses to erase");
    }

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
