namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     Per-slice camera state for the 2D viewport (#1/#2). One of these exists per rendered floor slice, so
///     the user pans/zooms each floor band independently and the smooth camera modes (Alive / Follow) drive
///     each slice's transform toward its own target. Pure / allocation-free / no Avalonia dependency, so the
///     fit-toward-target lerp and follow-centre math are unit-testable in isolation (the same testability
///     contract <see cref="ViewportTransform" /> holds).
///     <para>
///         The <see cref="Current" /> transform is what renders this frame. A smooth mode computes a TARGET
///         transform (a fit of the live/observed bounds, or a centre on the followed player) and the driver
///         calls <see cref="StepToward" /> once per RENDER frame with an interpolation factor derived from
///         the real frame dt — never a hard snap per tick. A manual pan/zoom flips <see cref="ManualOverride" />,
///         which the viewport reads to pause the auto-mode for THIS slice (so the user isn't fighting the lerp);
///         re-selecting a mode clears it.
///     </para>
/// </summary>
public struct SliceCamera
{
    /// <summary>The transform that renders this slice this frame.</summary>
    public ViewportTransform Current { get; set; }

    /// <summary>
    ///     True once the user has manually panned/zoomed this slice — the auto-mode (Alive / Follow / Map)
    ///     is paused for this slice until a mode is re-selected, so the manual gesture isn't fought by the lerp.
    /// </summary>
    public bool ManualOverride { get; set; }

    public SliceCamera(ViewportTransform current)
    {
        Current = current;
        ManualOverride = false;
    }

    /// <summary>
    ///     Smoothly advances <see cref="Current" /> toward <paramref name="target" /> by <paramref name="t" />
    ///     ∈ [0,1] (an exponential-decay step computed by the driver from the real frame dt). The centre, the
    ///     base scale, the zoom, and the pan are each lerped, so a Fit-shaped target (zoom 1, pan 0) is
    ///     approached without snapping. <paramref name="t" /> = 1 lands exactly on the target;
    ///     <paramref name="t" /> = 0 is a no-op. Returns the stepped camera (value type — no mutation of the
    ///     caller's copy unless reassigned).
    /// </summary>
    public readonly SliceCamera StepToward(ViewportTransform target, double t)
    {
        double k = Math.Clamp(t, 0, 1);
        ViewportTransform c = Current;

        ViewportTransform stepped = new(
            target.ViewWidth, target.ViewHeight,
            Lerp(c.CenterX, target.CenterX, k),
            Lerp(c.CenterY, target.CenterY, k),
            Lerp(c.BaseScale, target.BaseScale, k),
            Lerp(c.Zoom, target.Zoom, k),
            Lerp(c.PanX, target.PanX, k),
            Lerp(c.PanY, target.PanY, k));

        return new SliceCamera(stepped)
        {
            ManualOverride = ManualOverride
        };
    }

    /// <summary>
    ///     True when <see cref="Current" /> renders within <paramref name="epsilonPixels" /> of
    ///     <paramref name="target" /> — the driver stops re-arming the render loop once every slice is settled,
    ///     so the viewport doesn't spin forever after convergence. Measured in SCREEN pixels (the user-visible
    ///     metric): the centre and pan deltas are compared after mapping through the effective scale, and the
    ///     scale itself is compared as a relative ratio. This keeps the threshold meaningful whether the camera
    ///     is zoomed far out (large world units / pixel) or far in.
    /// </summary>
    public readonly bool IsSettledAt(ViewportTransform target, double epsilonPixels = 0.75)
    {
        ViewportTransform c = Current;
        double scale = Math.Max(target.EffectiveScale, 1e-9);

        // Centre delta in screen pixels (world units × pixels-per-world-unit).
        double centrePx = Math.Max(Math.Abs(c.CenterX - target.CenterX),
            Math.Abs(c.CenterY - target.CenterY)) * scale;
        double panPx = Math.Max(Math.Abs(c.PanX - target.PanX), Math.Abs(c.PanY - target.PanY));
        double scaleRatio = Math.Abs(c.EffectiveScale - target.EffectiveScale) / scale;

        return centrePx < epsilonPixels && panPx < epsilonPixels && scaleRatio < 0.005;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
