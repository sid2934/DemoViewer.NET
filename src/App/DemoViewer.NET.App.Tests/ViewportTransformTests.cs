#region

using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pure-math gates for the 2D viewport transform: world↔screen round-trip, auto-fit framing,
///     aspect preservation, and zoom-about-cursor invariance. No Avalonia / no demo — deterministic.
/// </summary>
public class ViewportTransformTests
{
    [Test]
    public async Task WorldToScreen_ScreenToWorld_RoundTrips()
    {
        ViewportTransform t = ViewportTransform.Fit(800, 600, -2000, -2000, 2000, 2000);

        foreach ((double wx, double wy) in new[]
                 {
                     (0.0, 0.0), (1234.0, -567.0), (-1999.0, 1999.0)
                 })
        {
            (double sx, double sy) = t.WorldToScreen(wx, wy);
            (double rx, double ry) = t.ScreenToWorld(sx, sy);
            await Assert.That(Math.Abs(rx - wx)).IsLessThan(1e-6);
            await Assert.That(Math.Abs(ry - wy)).IsLessThan(1e-6);
        }
    }

    [Test]
    public async Task Fit_CentersExtent_AtViewportCenter()
    {
        ViewportTransform t = ViewportTransform.Fit(800, 600, -1000, -500, 3000, 1500);

        // World centre is (1000, 500); it must map to the viewport centre (400, 300).
        (double sx, double sy) = t.WorldToScreen(1000, 500);
        await Assert.That(Math.Abs(sx - 400)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(sy - 300)).IsLessThan(1e-6);
    }

    [Test]
    public async Task Fit_IsUniformScale_PreservingAspect()
    {
        // A wide world extent in a square viewport must scale X and Y by the SAME factor (no stretch):
        // a unit step in world X and world Y both move the same number of screen pixels.
        ViewportTransform t = ViewportTransform.Fit(600, 600, -4000, -1000, 4000, 1000);

        (double cx, double cy) = t.WorldToScreen(0, 0);
        (double xx, _) = t.WorldToScreen(100, 0);
        (_, double yy) = t.WorldToScreen(0, 100);

        double dxPerUnit = Math.Abs(xx - cx) / 100.0;
        double dyPerUnit = Math.Abs(yy - cy) / 100.0;
        await Assert.That(Math.Abs(dxPerUnit - dyPerUnit)).IsLessThan(1e-9);
    }

    [Test]
    public async Task Fit_FramesExtentWithinViewport_WithMargin()
    {
        ViewportTransform t = ViewportTransform.Fit(800, 600, -2000, -2000, 2000, 2000);

        // Every extent corner must land inside the viewport (margin keeps it strictly within).
        foreach ((double wx, double wy) in new[]
                 {
                     (-2000.0, -2000.0), (2000.0, 2000.0), (-2000.0, 2000.0)
                 })
        {
            (double sx, double sy) = t.WorldToScreen(wx, wy);
            await Assert.That(sx).IsGreaterThanOrEqualTo(0);
            await Assert.That(sx).IsLessThanOrEqualTo(800);
            await Assert.That(sy).IsGreaterThanOrEqualTo(0);
            await Assert.That(sy).IsLessThanOrEqualTo(600);
        }
    }

    [Test]
    public async Task ZoomAbout_KeepsWorldPointUnderCursorFixed()
    {
        ViewportTransform t = ViewportTransform.Fit(800, 600, -2000, -2000, 2000, 2000);

        (double X, double Y) anchor = (X: 250.0, Y: 175.0);
        (double wxBefore, double wyBefore) = t.ScreenToWorld(anchor.X, anchor.Y);

        ViewportTransform zoomed = t.ZoomAbout(anchor.X, anchor.Y, 1.5);

        // The same world point must still be under the cursor after the zoom.
        (double sx, double sy) = zoomed.WorldToScreen(wxBefore, wyBefore);
        await Assert.That(Math.Abs(sx - anchor.X)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(sy - anchor.Y)).IsLessThan(1e-6);
        // And the effective scale increased.
        await Assert.That(zoomed.EffectiveScale).IsGreaterThan(t.EffectiveScale);
    }

    [Test]
    public async Task ZoomAbout_ClampsToBounds()
    {
        ViewportTransform t = ViewportTransform.Fit(800, 600, -2000, -2000, 2000, 2000);

        // Zoom out hard: clamp to the floor, never below.
        ViewportTransform z = t;
        for (int i = 0; i < 100; i++)
        {
            z = z.ZoomAbout(400, 300, 0.5);
        }

        await Assert.That(z.Zoom).IsGreaterThanOrEqualTo(0.05);

        // Zoom in hard: clamp to the ceiling.
        z = t;
        for (int i = 0; i < 100; i++)
        {
            z = z.ZoomAbout(400, 300, 2.0);
        }

        await Assert.That(z.Zoom).IsLessThanOrEqualTo(40.0);
    }

    [Test]
    public async Task Fit_DegenerateExtent_FallsBackToUnitScale()
    {
        // A zero-area extent (no positions yet) must not divide by zero; grid still renders at unit scale.
        ViewportTransform t = ViewportTransform.Fit(800, 600, 100, 100, 100, 100);
        await Assert.That(t.BaseScale).IsEqualTo(1.0);
        await Assert.That(double.IsFinite(t.WorldToScreen(100, 100).X)).IsTrue();
    }
}
