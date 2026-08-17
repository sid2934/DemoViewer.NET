#region

using Avalonia;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization.Tests;

/// <summary>
///     Unit tests for <see cref="EdgeGeometry" /> — the pure point-to-polyline math behind edge
///     hit-testing and the breakpoint-marker midpoint. All in logical coordinates; no layout, no
///     control.
/// </summary>
public class EdgeGeometryTests
{
    private const double Tol = 1e-9;

    // ── Point-to-segment / polyline distance ──────────────────────────────────

    /// <summary>A point off the side of a segment measures the perpendicular distance.</summary>
    [Test]
    public async Task PointToSegment_Perpendicular_IsPerpendicularDistance()
    {
        double d2 = EdgeGeometry.PointToSegmentDistanceSquared(5, 3, new Point(0, 0), new Point(10, 0));
        await Assert.That(d2).IsEqualTo(9.0).Within(Tol); // 3 units above the segment
    }

    /// <summary>A point on the segment has zero distance.</summary>
    [Test]
    public async Task PointToSegment_OnSegment_IsZero()
    {
        double d2 = EdgeGeometry.PointToSegmentDistanceSquared(5, 0, new Point(0, 0), new Point(10, 0));
        await Assert.That(d2).IsEqualTo(0.0).Within(Tol);
    }

    /// <summary>A point beyond an endpoint clamps to that endpoint (not the infinite line).</summary>
    [Test]
    public async Task PointToSegment_BeyondEndpoint_ClampsToEndpoint()
    {
        // (-2,0) is 2 left of the start (0,0): clamped distance 2 → sq 4 (not 0, which the infinite line gives).
        double d2 = EdgeGeometry.PointToSegmentDistanceSquared(-2, 0, new Point(0, 0), new Point(10, 0));
        await Assert.That(d2).IsEqualTo(4.0).Within(Tol);
    }

    /// <summary>A degenerate (a == b) segment measures distance to the single point.</summary>
    [Test]
    public async Task PointToSegment_DegenerateSegment_IsPointDistance()
    {
        double d2 = EdgeGeometry.PointToSegmentDistanceSquared(3, 4, new Point(0, 0), new Point(0, 0));
        await Assert.That(d2).IsEqualTo(25.0).Within(Tol); // 3-4-5
    }

    /// <summary>The polyline distance is the minimum over its segments (the bend nearest the point wins).</summary>
    [Test]
    public async Task Polyline_TakesNearestSegment()
    {
        // L-shape: (0,0)→(10,0)→(10,10). Point (12,5) is nearest the vertical segment (dist 2).
        List<Point> route = [new(0, 0), new(10, 0), new(10, 10)];
        double d2 = EdgeGeometry.MinDistanceSquaredToPolyline(12, 5, route);
        await Assert.That(d2).IsEqualTo(4.0).Within(Tol);
    }

    /// <summary>A &lt; 2-point polyline can't be hit.</summary>
    [Test]
    public async Task Polyline_Degenerate_IsInfinite()
    {
        await Assert.That(EdgeGeometry.MinDistanceSquaredToPolyline(0, 0, [new Point(1, 1)]))
            .IsEqualTo(double.PositiveInfinity);
    }

    // ── Midpoint ──────────────────────────────────────────────────────────────

    /// <summary>A straight segment's arc-length midpoint is its geometric centre.</summary>
    [Test]
    public async Task Midpoint_StraightSegment_IsCentre()
    {
        Point mid = EdgeGeometry.PolylineMidpoint([new Point(0, 0), new Point(10, 0)]);
        await Assert.That(mid.X).IsEqualTo(5.0).Within(Tol);
        await Assert.That(mid.Y).IsEqualTo(0.0).Within(Tol);
    }

    /// <summary>An L of equal legs has its arc-length midpoint exactly at the corner.</summary>
    [Test]
    public async Task Midpoint_EqualLegLShape_IsCorner()
    {
        // Total length 20, half = 10 → exactly the corner (10,0).
        Point mid = EdgeGeometry.PolylineMidpoint([new Point(0, 0), new Point(10, 0), new Point(10, 10)]);
        await Assert.That(mid.X).IsEqualTo(10.0).Within(Tol);
        await Assert.That(mid.Y).IsEqualTo(0.0).Within(Tol);
    }

    // ── Bézier ────────────────────────────────────────────────────────────────

    /// <summary>The cubic Bézier passes through its endpoints at t=0 and t=1.</summary>
    [Test]
    public async Task CubicBezier_EndpointsAtZeroAndOne()
    {
        Point p0 = new(0, 0), p1 = new(0, 10), p2 = new(10, 10), p3 = new(10, 0);
        Point at0 = EdgeGeometry.CubicBezierPoint(p0, p1, p2, p3, 0);
        Point at1 = EdgeGeometry.CubicBezierPoint(p0, p1, p2, p3, 1);
        await Assert.That(at0.X).IsEqualTo(0.0).Within(Tol);
        await Assert.That(at0.Y).IsEqualTo(0.0).Within(Tol);
        await Assert.That(at1.X).IsEqualTo(10.0).Within(Tol);
        await Assert.That(at1.Y).IsEqualTo(0.0).Within(Tol);
    }

    /// <summary>Sampling a 4-point Bézier yields a polyline of the requested length on the curve.</summary>
    [Test]
    public async Task SampleBezier_ProducesOnCurvePolyline()
    {
        List<Point> route = [new(0, 0), new(0, 10), new(10, 10), new(10, 0)];
        IReadOnlyList<Point> sampled = EdgeGeometry.SampleBezierRoute(route, 9);
        await Assert.That(sampled.Count).IsEqualTo(9);
        // Symmetric arch peaks at the middle sample (t=0.5): x = 5, y = 7.5.
        await Assert.That(sampled[4].X).IsEqualTo(5.0).Within(Tol);
        await Assert.That(sampled[4].Y).IsEqualTo(7.5).Within(Tol);
    }

    /// <summary>A non-4-point route is returned unchanged (only self-loops are Béziers).</summary>
    [Test]
    public async Task SampleBezier_NonFourPoint_ReturnsAsIs()
    {
        List<Point> route = [new(0, 0), new(10, 0)];
        await Assert.That(EdgeGeometry.SampleBezierRoute(route)).IsEquivalentTo(route);
    }
}
