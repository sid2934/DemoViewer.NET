#region

using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     Draws one spray as a 2D projection: the weapon's recoil pattern as a reference trace, and the
///     player's own bullets over it.
///     <para>
///         Both series are dots joined shot to shot, which is how a spray is read: the SHAPE of the
///         path carries the information, not the individual points. The reference is drawn first and
///         faint so the player's trace reads on top of it.
///     </para>
///     <para>
///         <b>Axes are degrees, and pitch is NOT flipped.</b> Source pitch is positive DOWN, so a
///         recoil kick is negative pitch, and screen Y also grows downward: the two conventions
///         already agree and a flip would draw every pattern upside down. Both series share one
///         scale, computed over both, so the traces are comparable by construction rather than each
///         normalised to its own extent.
///     </para>
///     <para>
///         <b>Missing indices are real.</b> <c>bullet_damage</c> fires only for bullets that hit, so a
///         run is the landed subsequence of a trigger pull and its path can jump. The gap is the
///         misses, and drawing through it is the honest rendering: interpolating would invent bullets.
///     </para>
/// </summary>
public class SprayPlot : TemplatedControl
{
    /// <summary>The weapon's recoil pattern, as the reference trace. Empty hides it.</summary>
    public static readonly StyledProperty<IReadOnlyList<SprayPatternPoint>?> PatternProperty =
        AvaloniaProperty.Register<SprayPlot, IReadOnlyList<SprayPatternPoint>?>(nameof(Pattern));

    /// <summary>The player's bullets for one run.</summary>
    public static readonly StyledProperty<IReadOnlyList<SpraySample>?> ShotsProperty =
        AvaloniaProperty.Register<SprayPlot, IReadOnlyList<SpraySample>?>(nameof(Shots));

    /// <summary>Brush for the reference pattern.</summary>
    public static readonly StyledProperty<IBrush?> PatternBrushProperty =
        AvaloniaProperty.Register<SprayPlot, IBrush?>(nameof(PatternBrush));

    /// <summary>Brush for the player's trace.</summary>
    public static readonly StyledProperty<IBrush?> ShotBrushProperty =
        AvaloniaProperty.Register<SprayPlot, IBrush?>(nameof(ShotBrush));

    /// <summary>Brush for the origin crosshair and frame.</summary>
    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<SprayPlot, IBrush?>(nameof(AxisBrush));

    /// <summary>Radius of a plotted bullet, in pixels.</summary>
    public static readonly StyledProperty<double> DotRadiusProperty =
        AvaloniaProperty.Register<SprayPlot, double>(nameof(DotRadius), 2.5);

    static SprayPlot()
    {
        AffectsRender<SprayPlot>(PatternProperty, ShotsProperty, PatternBrushProperty,
            ShotBrushProperty, AxisBrushProperty, DotRadiusProperty);
    }

    /// <summary>The weapon's recoil pattern.</summary>
    public IReadOnlyList<SprayPatternPoint>? Pattern
    {
        get => GetValue(PatternProperty);
        set => SetValue(PatternProperty, value);
    }

    /// <summary>The player's bullets.</summary>
    public IReadOnlyList<SpraySample>? Shots
    {
        get => GetValue(ShotsProperty);
        set => SetValue(ShotsProperty, value);
    }

    /// <summary>Reference-trace brush.</summary>
    public IBrush? PatternBrush
    {
        get => GetValue(PatternBrushProperty);
        set => SetValue(PatternBrushProperty, value);
    }

    /// <summary>Player-trace brush.</summary>
    public IBrush? ShotBrush
    {
        get => GetValue(ShotBrushProperty);
        set => SetValue(ShotBrushProperty, value);
    }

    /// <summary>Axis brush.</summary>
    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <summary>Bullet radius in pixels.</summary>
    public double DotRadius
    {
        get => GetValue(DotRadiusProperty);
        set => SetValue(DotRadiusProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect box = new Rect(Bounds.Size).Deflate(new Thickness(DotRadius + 2));
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        List<(double X, double Y)> pattern = [];
        if (Pattern is { Count: > 0 } pts)
        {
            foreach (SprayPatternPoint p in pts)
            {
                pattern.Add((p.PunchYawDeg, p.PunchPitchDeg));
            }
        }

        List<(double X, double Y)> shots = [];
        if (Shots is { Count: > 0 } ss)
        {
            foreach (SpraySample s in ss)
            {
                shots.Add((s.OffsetYawDeg, s.OffsetPitchDeg));
            }
        }

        if (pattern.Count == 0 && shots.Count == 0)
        {
            return;
        }

        // One scale over BOTH series, and square, so the two traces are comparable and a spray is not
        // silently stretched into a shape it does not have.
        double extent = 0.5;
        foreach ((double x, double y) in pattern.Concat(shots))
        {
            extent = Math.Max(extent, Math.Max(Math.Abs(x), Math.Abs(y)));
        }

        extent *= 1.15;
        double scale = Math.Min(box.Width, box.Height) / (2 * extent);
        Point centre = box.Center;

        Point Project(double yawDeg, double pitchDeg) =>
            new(centre.X + (yawDeg * scale), centre.Y + (pitchDeg * scale));

        IBrush axis = AxisBrush ?? Brushes.Gray;
        Pen axisPen = new(axis, 1);
        context.DrawLine(axisPen, new Point(box.Left, centre.Y), new Point(box.Right, centre.Y));
        context.DrawLine(axisPen, new Point(centre.X, box.Top), new Point(centre.X, box.Bottom));

        DrawTrace(context, pattern, PatternBrush ?? Brushes.SlateGray, Project, DotRadius * 0.8, 1.0);
        DrawTrace(context, shots, ShotBrush ?? Brushes.OrangeRed, Project, DotRadius, 1.6);
    }

    private static void DrawTrace(
        DrawingContext context, List<(double X, double Y)> series, IBrush brush,
        Func<double, double, Point> project, double radius, double thickness)
    {
        if (series.Count == 0)
        {
            return;
        }

        Pen pen = new(brush, thickness);
        Point previous = default;
        for (int i = 0; i < series.Count; i++)
        {
            // Pitch passes through UNNEGATED, deliberately. Source pitch is positive DOWN, so a recoil
            // kick is NEGATIVE pitch, and screen Y also grows downward: the conventions already agree,
            // and negating draws every pattern upside down while looking entirely plausible.
            Point p = project(series[i].X, series[i].Y);
            if (i > 0)
            {
                context.DrawLine(pen, previous, p);
            }

            context.DrawEllipse(brush, null, p, radius, radius);
            previous = p;
        }
    }
}
