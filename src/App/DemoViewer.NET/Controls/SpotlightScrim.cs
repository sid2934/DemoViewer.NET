#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     A full-surface dimming scrim with an optional rounded "spotlight" cut-out, the coach-mark backdrop
///     for the first-run Visual Walkthrough (<see cref="Views.Tutorial.TutorialView" />). Code-drawn (one
///     even-odd geometry) rather than four dim panels, so the hole is a single crisp rounded rectangle with
///     a token-coloured frame.
///     <para>
///         <b>Theme contract:</b> every colour is a <see cref="StyledProperty{T}" /> set from markup via
///         <c>{DynamicResource Token}</c>: assigning the token re-resolves the brush on a theme switch and
///         <c>AffectsRender</c> repaints. There are no cached / code-held brushes, so unlike a
///         Skia surface it needs no manual <c>ActualThemeVariantChanged</c> subscription.
///     </para>
///     <para>
///         <b>Input:</b> the control fills the overlay and is hit-test visible across its whole bounds
///         (including over the hole), so it fully blocks click-through to the app beneath: the tour is
///         Next-driven, the only interactive things are the callout's own buttons drawn above this.
///     </para>
/// </summary>
public sealed class SpotlightScrim : Control, ICustomHitTest
{
    /// <summary>The dim fill (a palette token brush, e.g. <c>{DynamicResource ShellBg}</c>).</summary>
    public static readonly StyledProperty<IBrush?> ScrimBrushProperty =
        AvaloniaProperty.Register<SpotlightScrim, IBrush?>(nameof(ScrimBrush));

    /// <summary>Opacity applied to the scrim fill (0..1). The hole frame is drawn at full opacity.</summary>
    public static readonly StyledProperty<double> ScrimOpacityProperty =
        AvaloniaProperty.Register<SpotlightScrim, double>(nameof(ScrimOpacity), 0.85);

    /// <summary>Whether to cut a hole. False → a plain full-surface dim (welcome / outro steps).</summary>
    public static readonly StyledProperty<bool> HasHoleProperty =
        AvaloniaProperty.Register<SpotlightScrim, bool>(nameof(HasHole));

    /// <summary>The target rectangle, in this control's coordinate space, to reveal.</summary>
    public static readonly StyledProperty<Rect> HoleProperty =
        AvaloniaProperty.Register<SpotlightScrim, Rect>(nameof(Hole));

    /// <summary>Breathing room added around <see cref="Hole" /> so the frame doesn't crowd the target.</summary>
    public static readonly StyledProperty<double> HolePaddingProperty =
        AvaloniaProperty.Register<SpotlightScrim, double>(nameof(HolePadding), 8);

    /// <summary>Corner radius of the cut-out.</summary>
    public static readonly StyledProperty<double> HoleCornerRadiusProperty =
        AvaloniaProperty.Register<SpotlightScrim, double>(nameof(HoleCornerRadius), 10);

    /// <summary>The hole frame / glow brush (a token, e.g. <c>{DynamicResource AccentInteractive}</c>).</summary>
    public static readonly StyledProperty<IBrush?> HoleBorderBrushProperty =
        AvaloniaProperty.Register<SpotlightScrim, IBrush?>(nameof(HoleBorderBrush));

    /// <summary>Thickness of the hole frame stroke.</summary>
    public static readonly StyledProperty<double> HoleBorderThicknessProperty =
        AvaloniaProperty.Register<SpotlightScrim, double>(nameof(HoleBorderThickness), 2);

    /// <summary>
    ///     Visual-only breathing phase in <c>[0,1]</c> driving the highlight border/glow intensity: <c>0</c> =
    ///     dim trough, <c>1</c> = bright peak. Animated by the <c>.pulsing</c> style (see
    ///     <see cref="Views.Tutorial.TutorialView" />) so the spotlight breathes while a step is on screen; it
    ///     stays at a static value when unanimated (e.g. a forced phase in a headless capture).
    ///     <b>Not a data / theme property</b>: the colour still comes from <see cref="HoleBorderBrush" />
    ///     (a <c>{DynamicResource}</c> token); this only scales its alpha. <c>AffectsRender</c> repaints each
    ///     tick.
    /// </summary>
    public static readonly StyledProperty<double> PulseProperty =
        AvaloniaProperty.Register<SpotlightScrim, double>(nameof(Pulse), 1.0);

    /// <summary>
    ///     When true, clicks over the spotlight <see cref="Hole" /> pass THROUGH the scrim to the real control
    ///     beneath (see <see cref="HitTest" />), so a waiting gateway step can let the user click the very
    ///     control it highlights (e.g. the Open-Demo button). False (default) → the scrim blocks everywhere.
    ///     Input-only, so it is not an <c>AffectsRender</c> property.
    /// </summary>
    public static readonly StyledProperty<bool> InteractiveHoleProperty =
        AvaloniaProperty.Register<SpotlightScrim, bool>(nameof(InteractiveHole));

    static SpotlightScrim()
    {
        AffectsRender<SpotlightScrim>(
            ScrimBrushProperty, ScrimOpacityProperty, HasHoleProperty, HoleProperty, HolePaddingProperty,
            HoleCornerRadiusProperty, HoleBorderBrushProperty, HoleBorderThicknessProperty, PulseProperty);
    }

    /// <inheritdoc cref="ScrimBrushProperty" />
    public IBrush? ScrimBrush
    {
        get => GetValue(ScrimBrushProperty);
        set => SetValue(ScrimBrushProperty, value);
    }

    /// <inheritdoc cref="ScrimOpacityProperty" />
    public double ScrimOpacity
    {
        get => GetValue(ScrimOpacityProperty);
        set => SetValue(ScrimOpacityProperty, value);
    }

    /// <inheritdoc cref="HasHoleProperty" />
    public bool HasHole
    {
        get => GetValue(HasHoleProperty);
        set => SetValue(HasHoleProperty, value);
    }

    /// <inheritdoc cref="HoleProperty" />
    public Rect Hole
    {
        get => GetValue(HoleProperty);
        set => SetValue(HoleProperty, value);
    }

    /// <inheritdoc cref="HolePaddingProperty" />
    public double HolePadding
    {
        get => GetValue(HolePaddingProperty);
        set => SetValue(HolePaddingProperty, value);
    }

    /// <inheritdoc cref="HoleCornerRadiusProperty" />
    public double HoleCornerRadius
    {
        get => GetValue(HoleCornerRadiusProperty);
        set => SetValue(HoleCornerRadiusProperty, value);
    }

    /// <inheritdoc cref="HoleBorderBrushProperty" />
    public IBrush? HoleBorderBrush
    {
        get => GetValue(HoleBorderBrushProperty);
        set => SetValue(HoleBorderBrushProperty, value);
    }

    /// <inheritdoc cref="HoleBorderThicknessProperty" />
    public double HoleBorderThickness
    {
        get => GetValue(HoleBorderThicknessProperty);
        set => SetValue(HoleBorderThicknessProperty, value);
    }

    /// <inheritdoc cref="PulseProperty" />
    public double Pulse
    {
        get => GetValue(PulseProperty);
        set => SetValue(PulseProperty, value);
    }

    /// <inheritdoc cref="InteractiveHoleProperty" />
    public bool InteractiveHole
    {
        get => GetValue(InteractiveHoleProperty);
        set => SetValue(InteractiveHoleProperty, value);
    }

    /// <summary>
    ///     Custom hit-testing (<see cref="ICustomHitTest" />). The scrim blocks input everywhere by default so
    ///     the Next-driven tour can't be clicked through. The exception: when <see cref="InteractiveHole" /> is
    ///     set, a point inside the (padded) spotlight hole reports "not hit", so Avalonia continues the hit-test
    ///     past the scrim to the real control beneath, letting the user click the highlighted Open-Demo button.
    /// </summary>
    public bool HitTest(Point point)
    {
        if (InteractiveHole && HasHole && Hole is { Width: > 0, Height: > 0 }
            && Inflate(Hole, HolePadding).Contains(point))
        {
            return false; // pass through, the real highlighted control handles this click
        }

        return true; // block everywhere else
    }

    public override void Render(DrawingContext context)
    {
        Rect full = new(Bounds.Size);
        if (full.Width <= 0 || full.Height <= 0)
        {
            return;
        }

        IBrush scrim = DimBrush();

        // No cut-out (welcome / outro) or no measured target yet → plain full dim.
        if (!HasHole || Hole.Width <= 0 || Hole.Height <= 0)
        {
            context.FillRectangle(scrim, full);
            return;
        }

        Rect hole = Inflate(Hole, HolePadding).Intersect(full);
        double radius = Math.Min(HoleCornerRadius, Math.Min(hole.Width, hole.Height) / 2);
        StreamGeometry holeGeo = RoundedRect(hole, radius);

        // Dim everything EXCEPT the hole: an even-odd group of the full rect + the hole punches it through.
        GeometryGroup group = new()
        {
            FillRule = FillRule.EvenOdd
        };
        group.Children.Add(new RectangleGeometry(full));
        group.Children.Add(holeGeo);
        context.DrawGeometry(scrim, null, group);

        // Breathing frame + soft outward glow so the highlighted region reads as "focused" and gently
        // pulses to draw the eye (Pulse 0..1, animated by the .pulsing style while a step is on screen).
        if (HoleBorderBrush is not null && HoleBorderThickness > 0)
        {
            double pulse = Math.Clamp(Pulse, 0, 1);

            // Soft breathing halo: a few concentric rounded strokes fading outward, their alpha scaled by the
            // pulse. DrawingContext has no blur, so several low-alpha rings fake a glow. Needs the token's
            // Color to fade alpha, so only for a solid brush; otherwise just the crisp frame below.
            if (HoleBorderBrush is ISolidColorBrush)
            {
                const int layers = 4;
                const double glowPeak = 0.42; // halo alpha at the brightest phase (inner-most ring)
                for (int i = layers; i >= 1; i--)
                {
                    double spread = i * (HoleBorderThickness + 2.0);
                    double falloff = 1.0 - (i - 1) / (double)layers; // inner rings brighter than outer
                    double alpha = glowPeak * pulse * falloff;
                    if (alpha <= 0.004 || SolidBorderAt(alpha) is not { } ring)
                    {
                        continue;
                    }

                    StreamGeometry ringGeo = RoundedRect(Inflate(hole, spread), radius + spread);
                    context.DrawGeometry(null, new Pen(ring, HoleBorderThickness * 1.5), ringGeo);
                }
            }

            // Crisp inner frame, its brightness breathes with the pulse but never drops out (0.72..1.0), so
            // the highlight is a soft breath, not a hard on/off flash.
            double frameOpacity = 0.72 + 0.28 * pulse;
            IBrush frame = SolidBorderAt(frameOpacity) ?? HoleBorderBrush;
            context.DrawGeometry(null, new Pen(frame, HoleBorderThickness), holeGeo);
        }
    }

    // The hole-border token faded to a given opacity (multiplying its own alpha), so the glow/frame stay
    // theme-reactive (Render re-runs when the DynamicResource re-resolves). Null for a non-solid brush.
    private ImmutableSolidColorBrush? SolidBorderAt(double opacity)
    {
        if (HoleBorderBrush is ISolidColorBrush s)
        {
            Color c = s.Color;
            byte a = (byte)Math.Clamp(c.A * opacity, 0, 255);
            return new ImmutableSolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        }

        return null;
    }

    // Bake ScrimOpacity into the fill so the dim is translucent while the frame stroke stays opaque. Reading
    // the token's colour keeps it theme-reactive (Render re-runs when the DynamicResource re-resolves).
    private IBrush DimBrush()
    {
        IBrush? brush = ScrimBrush;
        if (brush is ISolidColorBrush solid)
        {
            Color c = solid.Color;
            byte a = (byte)Math.Clamp(c.A * ScrimOpacity, 0, 255);
            return new ImmutableSolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        }

        return brush ?? new ImmutableSolidColorBrush(Color.FromArgb((byte)(255 * ScrimOpacity), 0, 0, 0));
    }

    private static Rect Inflate(Rect r, double d) =>
        new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private static StreamGeometry RoundedRect(Rect r, double radius)
    {
        double rad = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        StreamGeometry geo = new();
        using StreamGeometryContext ctx = geo.Open();
        Size corner = new(rad, rad);

        ctx.BeginFigure(new Point(r.X + rad, r.Y), true);
        ctx.LineTo(new Point(r.Right - rad, r.Y));
        ctx.ArcTo(new Point(r.Right, r.Y + rad), corner, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(r.Right, r.Bottom - rad));
        ctx.ArcTo(new Point(r.Right - rad, r.Bottom), corner, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(r.X + rad, r.Bottom));
        ctx.ArcTo(new Point(r.X, r.Bottom - rad), corner, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(r.X, r.Y + rad));
        ctx.ArcTo(new Point(r.X + rad, r.Y), corner, 0, false, SweepDirection.Clockwise);
        ctx.EndFigure(true);
        return geo;
    }
}
