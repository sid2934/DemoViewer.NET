namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     The world↔screen affine transform for the 2D viewport. Pure, allocation-free, and free of
///     any Avalonia dependency so the auto-fit / pan / zoom math is unit-testable in isolation.
///     <para>
///         The mapping is a uniform scale about a world centre, with Y inverted (world up = screen up),
///         plus a pan offset in screen pixels and a zoom multiplier:
///         <code>
///         screenX = (worldX - centerX) * scale * zoom + viewW/2 + panX
///         screenY = (centerY - worldY) * scale * zoom + viewH/2 + panY
///         </code>
///         where <c>scale</c> is the auto-fit base (recomputed only on fit), and <c>zoom</c>/<c>pan</c>
///         are the user gestures. Recompute only on fit / pan / zoom — never per tick.
///     </para>
/// </summary>
public readonly struct ViewportTransform
{
    /// <summary>Viewport width in device-independent pixels.</summary>
    public double ViewWidth { get; }

    /// <summary>Viewport height in device-independent pixels.</summary>
    public double ViewHeight { get; }

    /// <summary>World-space centre the view is anchored on.</summary>
    public double CenterX { get; }

    /// <summary>World-space centre the view is anchored on.</summary>
    public double CenterY { get; }

    /// <summary>The auto-fit base scale (screen px per world unit), before the user zoom.</summary>
    public double BaseScale { get; }

    /// <summary>User zoom multiplier (1 = auto-fit).</summary>
    public double Zoom { get; }

    /// <summary>User pan in screen pixels.</summary>
    public double PanX { get; }

    /// <summary>User pan in screen pixels.</summary>
    public double PanY { get; }

    /// <summary>Effective screen-pixels-per-world-unit (<see cref="BaseScale" /> × <see cref="Zoom" />).</summary>
    public double EffectiveScale => BaseScale * Zoom;

    public ViewportTransform(double viewWidth, double viewHeight, double centerX, double centerY,
        double baseScale, double zoom, double panX, double panY)
    {
        ViewWidth = viewWidth;
        ViewHeight = viewHeight;
        CenterX = centerX;
        CenterY = centerY;
        BaseScale = baseScale;
        Zoom = zoom;
        PanX = panX;
        PanY = panY;
    }

    /// <summary>World (x,y) → screen (x,y).</summary>
    public (double X, double Y) WorldToScreen(double worldX, double worldY)
    {
        double s = EffectiveScale;
        double sx = (worldX - CenterX) * s + ViewWidth / 2 + PanX;
        double sy = (CenterY - worldY) * s + ViewHeight / 2 + PanY;
        return (sx, sy);
    }

    /// <summary>
    ///     Screen (x,y) → world (x,y). Inverse of <see cref="WorldToScreen" />; supports hit-testing
    ///     and zoom-about-cursor. Returns the centre when the effective scale is degenerate.
    /// </summary>
    public (double X, double Y) ScreenToWorld(double screenX, double screenY)
    {
        double s = EffectiveScale;
        if (s <= 0)
        {
            return (CenterX, CenterY);
        }

        double wx = (screenX - ViewWidth / 2 - PanX) / s + CenterX;
        double wy = CenterY - (screenY - ViewHeight / 2 - PanY) / s;
        return (wx, wy);
    }

    /// <summary>
    ///     Builds an auto-fit transform that frames a world rectangle within the viewport with a margin
    ///. Uniform scale preserves aspect; zoom resets to 1 and pan to 0 (the "Fit" baseline).
    ///     A degenerate (zero-area) extent falls back to a unit scale so the grid still renders.
    /// </summary>
    public static ViewportTransform Fit(double viewWidth, double viewHeight,
        double worldMinX, double worldMinY, double worldMaxX, double worldMaxY, double margin = 0.08)
    {
        double w = worldMaxX - worldMinX;
        double h = worldMaxY - worldMinY;
        double centerX = (worldMinX + worldMaxX) / 2;
        double centerY = (worldMinY + worldMaxY) / 2;

        double usableW = Math.Max(1.0, viewWidth);
        double usableH = Math.Max(1.0, viewHeight);

        double baseScale;
        if (w <= double.Epsilon || h <= double.Epsilon)
        {
            baseScale = 1.0; // degenerate extent — keep the grid visible at unit scale
        }
        else
        {
            double scaleX = usableW / w;
            double scaleY = usableH / h;
            baseScale = Math.Min(scaleX, scaleY) * (1 - margin);
        }

        return new ViewportTransform(viewWidth, viewHeight, centerX, centerY, baseScale, 1.0, 0, 0);
    }

    /// <summary>Returns a copy with the same fit but resized viewport (re-centres the pan origin).</summary>
    public ViewportTransform WithViewport(double viewWidth, double viewHeight) =>
        new(viewWidth, viewHeight, CenterX, CenterY, BaseScale, Zoom, PanX, PanY);

    /// <summary>Returns a copy with an added pan delta (screen px).</summary>
    public ViewportTransform WithPanDelta(double dx, double dy) =>
        new(ViewWidth, ViewHeight, CenterX, CenterY, BaseScale, Zoom, PanX + dx, PanY + dy);

    /// <summary>
    ///     Zooms about a screen anchor (the cursor) by a multiplicative factor, clamping zoom to
    ///     [<paramref name="minZoom" />, <paramref name="maxZoom" />]. The world point under the cursor
    ///     stays fixed (the standard zoom-to-cursor behaviour), so pan is adjusted to compensate.
    /// </summary>
    public ViewportTransform ZoomAbout(double anchorScreenX, double anchorScreenY, double factor,
        double minZoom = 0.05, double maxZoom = 40.0)
    {
        double newZoom = Math.Clamp(Zoom * factor, minZoom, maxZoom);
        if (newZoom == Zoom)
        {
            return this;
        }

        // World point under the cursor before the zoom.
        (double wx, double wy) = ScreenToWorld(anchorScreenX, anchorScreenY);

        // Re-solve pan so that (wx,wy) maps back to the same screen anchor at the new zoom.
        double s = BaseScale * newZoom;
        double newPanX = anchorScreenX - ((wx - CenterX) * s + ViewWidth / 2);
        double newPanY = anchorScreenY - ((CenterY - wy) * s + ViewHeight / 2);

        return new ViewportTransform(ViewWidth, ViewHeight, CenterX, CenterY, BaseScale, newZoom, newPanX, newPanY);
    }
}
