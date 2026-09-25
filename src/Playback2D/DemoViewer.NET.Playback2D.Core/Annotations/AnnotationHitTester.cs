#region

using DemoViewer.NET.Playback2D.Core.Ink;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Annotations;

/// <summary>
///     Stroke-level hit-testing for the eraser. Erase removes WHOLE elements; there is no pixel erase
///     anywhere in the codebase, so the only question this type answers is "does the eraser disc touch
///     this element".
///     <para>
///         Freehand takes three tiers, cheapest first: an axis-aligned bounds reject, a point-to-polyline
///         distance against the raw samples inflated by half the stroke width plus the eraser radius, and,
///         for a stroke wide enough that the inflation is a visible over-estimate, an exact test against
///         the derived outline polygon.
///     </para>
///     <para>
///         Every other kind is a branch of its own (step-authoring.md §3.2), because the eraser has to
///         hit what the layer DRAWS: a rectangle's two stored corners are its diagonal, which is exactly
///         where a rectangle has no ink. Line and Arrow test the segment, Rect its four edges, Ellipse its
///         outline, and Text the line box <see cref="AnnotationText" /> measures, inflated by the radius.
///     </para>
/// </summary>
public static class AnnotationHitTester
{
    /// <summary>
    ///     Stroke width at or above which the cheap inflated-polyline test is refined by an exact
    ///     point-in-outline test. Below it the two answers differ by less than a pixel at any sane zoom.
    /// </summary>
    private const float WideStrokeWorld = 8f;

    [ThreadStatic]
    private static List<StrokePoint>? _scratch;

    [ThreadStatic]
    private static List<SKPoint>? _outline;

    [ThreadStatic]
    private static InkPoint[]? _samples;

    // Per thread, like the scratch lists above, because TextBlobCache is not thread-safe by design. The
    // eraser runs on the UI thread, so in practice this is one cache for the life of the process.
    [ThreadStatic]
    private static TextBlobCache? _text;

    /// <summary>
    ///     Whether the eraser disc at <paramref name="worldX" />/<paramref name="worldY" /> with radius
    ///     <paramref name="worldRadius" /> touches <paramref name="element" />.
    /// </summary>
    /// <param name="element">The candidate element.</param>
    /// <param name="worldX">Eraser centre, world X.</param>
    /// <param name="worldY">Eraser centre, world Y.</param>
    /// <param name="worldRadius">Eraser radius in world units.</param>
    public static bool HitTest(AnnotationElement element, float worldX, float worldY, float worldRadius)
    {
        ArgumentNullException.ThrowIfNull(element);

        IReadOnlyList<InkPoint> points = element.Points;
        if (points.Count == 0)
        {
            return false;
        }

        float half = Math.Max(0f, element.Style.WidthWorld) / 2f;
        float slop = half + Math.Max(0f, worldRadius);

        switch (element.Kind)
        {
            case AnnotationKind.Line:
            case AnnotationKind.Arrow:
                return SegmentDistanceSquared(points[0], points[^1], worldX, worldY) <= slop * slop;

            case AnnotationKind.Rect:
                return WithinRectEdges(points[0], points[^1], worldX, worldY, slop);

            case AnnotationKind.Ellipse:
                return WithinEllipseOutline(points[0], points[^1], worldX, worldY, slop);

            case AnnotationKind.Text:
                return WithinText(element, worldX, worldY, Math.Max(0f, worldRadius), slop);
        }

        if (!WithinBounds(points, worldX, worldY, slop))
        {
            return false;
        }

        if (!WithinPolyline(points, worldX, worldY, slop))
        {
            return false;
        }

        if (element.Style.WidthWorld < WideStrokeWorld)
        {
            return true;
        }

        return WithinOutline(element, worldX, worldY, worldRadius);
    }

    /// <summary>
    ///     Every element the eraser disc touches, <b>topmost first</b>. The document draws oldest-first,
    ///     so the last element is the one the user sees on top and means to erase.
    /// </summary>
    /// <param name="doc">The document to search.</param>
    /// <param name="worldX">Eraser centre, world X.</param>
    /// <param name="worldY">Eraser centre, world Y.</param>
    /// <param name="worldRadius">Eraser radius in world units.</param>
    /// <param name="results">Caller-owned destination, cleared then filled.</param>
    /// <returns>How many elements were hit.</returns>
    public static int HitTestAll(AnnotationDocument doc, float worldX, float worldY, float worldRadius,
        List<Guid> results)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(results);

        results.Clear();
        IReadOnlyList<AnnotationElement> elements = doc.Elements;
        for (int i = elements.Count - 1; i >= 0; i--)
        {
            if (HitTest(elements[i], worldX, worldY, worldRadius))
            {
                results.Add(elements[i].Id);
            }
        }

        return results.Count;
    }

    private static bool WithinRectEdges(InkPoint a, InkPoint b, float x, float y, float slop)
    {
        float limit = slop * slop;
        return SegmentDistanceSquared(a.X, a.Y, b.X, a.Y, x, y) <= limit
               || SegmentDistanceSquared(b.X, a.Y, b.X, b.Y, x, y) <= limit
               || SegmentDistanceSquared(b.X, b.Y, a.X, b.Y, x, y) <= limit
               || SegmentDistanceSquared(a.X, b.Y, a.X, a.Y, x, y) <= limit;
    }

    // abs(normalized radius - 1) is the test, taken back into world units along the ray from the centre
    // so it compares against the same slop every other kind uses: a point at normalized radius r sits
    // |p - c| * |1 - 1/r| from the outline along that ray. Exact on a circle, and within the stroke width
    // of exact on any ellipse a coach draws. An ellipse flat on one axis is drawn as its rectangle's
    // edges, so it is hit as one.
    private static bool WithinEllipseOutline(InkPoint a, InkPoint b, float x, float y, float slop)
    {
        float rx = Math.Abs(b.X - a.X) / 2f;
        float ry = Math.Abs(b.Y - a.Y) / 2f;
        if (rx <= float.Epsilon || ry <= float.Epsilon)
        {
            return WithinRectEdges(a, b, x, y, slop);
        }

        double dx = x - (a.X + b.X) / 2.0;
        double dy = y - (a.Y + b.Y) / 2.0;
        double r = Math.Sqrt(dx / rx * (dx / rx) + dy / ry * (dy / ry));
        if (r <= 1e-9)
        {
            return Math.Min(rx, ry) <= slop;
        }

        double fromCentre = Math.Sqrt(dx * dx + dy * dy);
        return fromCentre * Math.Abs(1.0 - 1.0 / r) <= slop;
    }

    // The line box, inflated by the eraser radius alone: the stroke width means nothing to a glyph. A
    // label with no text draws nothing, so what remains to hit is its anchor.
    private static bool WithinText(AnnotationElement element, float x, float y, float radius, float slop)
    {
        TextBlobCache cache = _text ??= new TextBlobCache();
        if (!AnnotationText.TryWorldBounds(cache, element, out SKRect box))
        {
            InkPoint anchor = element.Points[0];
            float dx = anchor.X - x;
            float dy = anchor.Y - y;
            return dx * dx + dy * dy <= slop * slop;
        }

        return x >= box.Left - radius && x <= box.Right + radius
                                      && y >= box.Top - radius && y <= box.Bottom + radius;
    }

    private static bool WithinBounds(IReadOnlyList<InkPoint> points, float x, float y, float slop)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < points.Count; i++)
        {
            InkPoint p = points[i];
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return x >= minX - slop && x <= maxX + slop && y >= minY - slop && y <= maxY + slop;
    }

    private static bool WithinPolyline(IReadOnlyList<InkPoint> points, float x, float y, float slop)
    {
        float limit = slop * slop;

        if (points.Count == 1)
        {
            float dx = points[0].X - x;
            float dy = points[0].Y - y;
            return dx * dx + dy * dy <= limit;
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (SegmentDistanceSquared(points[i - 1], points[i], x, y) <= limit)
            {
                return true;
            }
        }

        return false;
    }

    private static bool WithinOutline(AnnotationElement element, float x, float y, float radius)
    {
        List<StrokePoint> scratch = _scratch ??= new List<StrokePoint>(256);
        List<SKPoint> outline = _outline ??= new List<SKPoint>(512);

        FreehandOptions options = FreehandOptions.ForWidth(element.Style.WidthWorld);

        int count = element.Points.Count;
        if (_samples is null || _samples.Length < count)
        {
            _samples = new InkPoint[Math.Max(256, count)];
        }

        for (int i = 0; i < count; i++)
        {
            _samples[i] = element.Points[i];
        }

        FreehandOutline.GetOutline(_samples.AsSpan(0, count), in options, scratch, outline);
        if (outline.Count < 3)
        {
            return true; // degenerate outline; the polyline test already said yes
        }

        if (PointInPolygon(outline, x, y))
        {
            return true;
        }

        float limit = Math.Max(0f, radius);
        limit *= limit;
        for (int i = 0; i < outline.Count; i++)
        {
            SKPoint a = outline[i];
            SKPoint b = outline[(i + 1) % outline.Count];
            if (SegmentDistanceSquared(a.X, a.Y, b.X, b.Y, x, y) <= limit)
            {
                return true;
            }
        }

        return false;
    }

    // Even-odd ray cast along +X. The outline is a closed polygon by construction, so the wrap-around
    // edge is included by indexing the previous vertex modulo the count.
    private static bool PointInPolygon(List<SKPoint> polygon, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            SKPoint a = polygon[i];
            SKPoint b = polygon[j];
            if (a.Y > y != b.Y > y &&
                x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float SegmentDistanceSquared(InkPoint a, InkPoint b, float x, float y) =>
        SegmentDistanceSquared(a.X, a.Y, b.X, b.Y, x, y);

    private static float SegmentDistanceSquared(float ax, float ay, float bx, float by, float x, float y)
    {
        float dx = bx - ax;
        float dy = by - ay;
        float lengthSquared = dx * dx + dy * dy;

        float t = lengthSquared <= float.Epsilon
            ? 0f
            : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0f, 1f);

        float px = ax + t * dx - x;
        float py = ay + t * dy - y;
        return px * px + py * py;
    }
}
