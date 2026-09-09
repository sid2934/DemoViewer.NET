#region

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>How a <see cref="Sparkline" /> draws its series.</summary>
public enum SparklineMode
{
    /// <summary>A polyline through the values, optionally filled to the floor.</summary>
    Line,

    /// <summary>One column per value, rising from the domain floor.</summary>
    Bars,

    /// <summary>One dot per value, coloured by whether the value is non-zero. For pass/fail series.</summary>
    Dots
}

/// <summary>
///     A per-round micro chart: the shape of a match rather than its total. Three modes, because the
///     three strips the player view needs (a kills line, a damage column strip, a KAST pass/fail dot row)
///     differ only in how the same normalised series is marked.
///     <para>
///         <b>Normalisation happens here, not in the view model.</b> The control owns the mapping from
///         values to pixels, so it re-lays-out on resize and a caller passes raw per-round numbers. This
///         replaces the hand-rolled strips whose pixel heights were computed once, at build time, in
///         <c>PlayerDetailsViewModel</c>.
///     </para>
///     <para>
///         <see cref="SparklineMode.Bars" /> pins the floor at zero unless <see cref="Minimum" /> says
///         otherwise, because a column strip whose floor floats with the data misreports small values as
///         large ones. <see cref="SparklineMode.Line" /> uses the data range, because there the shape is
///         the point.
///     </para>
/// </summary>
public class Sparkline : TemplatedControl
{
    /// <summary>The series, in order. Non-finite entries are skipped.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Line, bars or dots.</summary>
    public static readonly StyledProperty<SparklineMode> ModeProperty =
        AvaloniaProperty.Register<Sparkline, SparklineMode>(nameof(Mode));

    /// <summary>Domain floor. Null auto-fits (zero for <see cref="SparklineMode.Bars" />).</summary>
    public static readonly StyledProperty<double?> MinimumProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(Minimum));

    /// <summary>Domain ceiling. Null auto-fits to the largest value.</summary>
    public static readonly StyledProperty<double?> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(Maximum));

    /// <summary>Draws a hairline at this value, for a zero line or an average. Null draws none.</summary>
    public static readonly StyledProperty<double?> BaselineProperty =
        AvaloniaProperty.Register<Sparkline, double?>(nameof(Baseline));

    /// <summary>Line and bar colour (<c>AccentInteractive</c>).</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    /// <summary>Optional area fill under a <see cref="SparklineMode.Line" />.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Fill));

    /// <summary>Dot colour for a non-zero value (<c>StatPositive</c>).</summary>
    public static readonly StyledProperty<IBrush?> PositiveBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(PositiveBrush));

    /// <summary>Dot colour for a zero value (<c>StatBarTrack</c>).</summary>
    public static readonly StyledProperty<IBrush?> NegativeBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(NegativeBrush));

    /// <summary>Baseline hairline colour (<c>BorderSubtle</c>).</summary>
    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(BaselineBrush));

    /// <summary>Stroke width for <see cref="SparklineMode.Line" />.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 1.5);

    /// <summary>Pixels between bars or dots.</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Gap), 2);

    /// <summary>Dot radius for <see cref="SparklineMode.Dots" />.</summary>
    public static readonly StyledProperty<double> DotRadiusProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(DotRadius), 3);

    /// <summary>
    ///     One tooltip per value, shown for whichever point the pointer is over. A whole-strip tooltip
    ///     cannot say which round it is describing, and a strip of per-round data is worth nothing if the
    ///     reader cannot ask which round a spike was.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> PointTooltipsProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<string>?>(nameof(PointTooltips));

    /// <summary>A zero-value bar still draws this many pixels, so an empty round reads as present.</summary>
    private const double MinBarHeight = 1;

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, ModeProperty, MinimumProperty, MaximumProperty,
            BaselineProperty, StrokeProperty, FillProperty, PositiveBrushProperty, NegativeBrushProperty,
            BaselineBrushProperty, StrokeThicknessProperty, GapProperty, DotRadiusProperty);
    }

    /// <inheritdoc cref="ValuesProperty" />
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <inheritdoc cref="ModeProperty" />
    public SparklineMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <inheritdoc cref="MinimumProperty" />
    public double? Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <inheritdoc cref="MaximumProperty" />
    public double? Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <inheritdoc cref="BaselineProperty" />
    public double? Baseline
    {
        get => GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    /// <inheritdoc cref="StrokeProperty" />
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <inheritdoc cref="FillProperty" />
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <inheritdoc cref="PositiveBrushProperty" />
    public IBrush? PositiveBrush
    {
        get => GetValue(PositiveBrushProperty);
        set => SetValue(PositiveBrushProperty, value);
    }

    /// <inheritdoc cref="NegativeBrushProperty" />
    public IBrush? NegativeBrush
    {
        get => GetValue(NegativeBrushProperty);
        set => SetValue(NegativeBrushProperty, value);
    }

    /// <inheritdoc cref="BaselineBrushProperty" />
    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    /// <inheritdoc cref="StrokeThicknessProperty" />
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <inheritdoc cref="GapProperty" />
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <inheritdoc cref="DotRadiusProperty" />
    public double DotRadius
    {
        get => GetValue(DotRadiusProperty);
        set => SetValue(DotRadiusProperty, value);
    }

    /// <inheritdoc cref="PointTooltipsProperty" />
    public IReadOnlyList<string>? PointTooltips
    {
        get => GetValue(PointTooltipsProperty);
        set => SetValue(PointTooltipsProperty, value);
    }

    /// <summary>
    ///     Which value the given point in this control's coordinate space falls on, or -1 for none. The
    ///     control draws its series rather than realising a visual per point, so a consumer that wants a
    ///     click or a hover to mean "this round" has to ask; this is that question.
    /// </summary>
    public int IndexAt(Point position)
    {
        IReadOnlyList<double> values = Values ?? [];
        Rect content = new Rect(Bounds.Size).Deflate(Padding);
        if (values.Count == 0 || content.Width <= 0)
        {
            return -1;
        }

        double x = position.X - content.X;
        if (x < 0 || x > content.Width)
        {
            return -1;
        }

        // Line points sit ON the edges (the first at x=0, the last at the right edge) while bars and dots
        // sit in slots, so the two modes divide the width differently and rounding differs with them.
        int index = Mode == SparklineMode.Line && values.Count > 1
            ? (int)Math.Round(x / (content.Width / (values.Count - 1)))
            : (int)(x / (content.Width / values.Count));

        return Math.Clamp(index, 0, values.Count - 1);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        IReadOnlyList<string>? tips = PointTooltips;
        if (tips is null || tips.Count == 0)
        {
            return;
        }

        int index = IndexAt(e.GetPosition(this));
        ToolTip.SetTip(this, index >= 0 && index < tips.Count ? tips[index] : null);
    }

    /// <summary>
    ///     Drops the per-point tip on the way out. Without this the tip set by the last point the
    ///     pointer crossed stays attached to the control, so the next hover anywhere over the strip
    ///     flashes the PREVIOUS round's numbers before the move handler replaces them.
    /// </summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, null);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A tip names one point of the series that was showing when it was set. Swap the series (a
        // different player in the details overlay) and it describes a round this strip no longer draws,
        // so it goes with the data it belonged to.
        if (change.Property == ValuesProperty || change.Property == PointTooltipsProperty)
        {
            ToolTip.SetTip(this, null);
        }

        if (change.Property == ValuesProperty || change.Property == ModeProperty)
        {
            int count = Values?.Count ?? 0;
            AutomationProperties.SetName(this, $"{Mode} chart, {count} points");
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect content = new Rect(Bounds.Size).Deflate(Padding);
        IReadOnlyList<double> values = Values ?? [];
        if (content.Width <= 0 || content.Height <= 0 || values.Count == 0)
        {
            return;
        }

        (double lo, double hi) = Domain(values);

        if (Baseline is { } baseValue && BaselineBrush is { } baseBrush && hi > lo)
        {
            double y = YFor(baseValue, content, lo, hi);
            context.DrawLine(new Pen(baseBrush, 1), new Point(content.X, y), new Point(content.Right, y));
        }

        switch (Mode)
        {
            case SparklineMode.Bars:
                DrawBars(context, content, values, lo, hi);
                break;
            case SparklineMode.Dots:
                DrawDots(context, content, values);
                break;
            case SparklineMode.Line:
            default:
                DrawLine(context, content, values, lo, hi);
                break;
        }
    }

    /// <summary>
    ///     The vertical domain. Bars pin the floor at zero unless told otherwise; a column strip that
    ///     floats its floor draws the smallest round as empty and every comparison off it is wrong.
    /// </summary>
    private (double Lo, double Hi) Domain(IReadOnlyList<double> values)
    {
        double dataMin = double.PositiveInfinity;
        double dataMax = double.NegativeInfinity;
        foreach (double v in values)
        {
            if (!double.IsFinite(v))
            {
                continue;
            }

            dataMin = Math.Min(dataMin, v);
            dataMax = Math.Max(dataMax, v);
        }

        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
        {
            return (0, 0);
        }

        double lo = Minimum ?? (Mode == SparklineMode.Bars ? Math.Min(0, dataMin) : dataMin);
        double hi = Maximum ?? dataMax;
        return (lo, hi);
    }

    private static double YFor(double value, Rect content, double lo, double hi) =>
        hi > lo
            ? content.Bottom - (Math.Clamp((value - lo) / (hi - lo), 0, 1) * content.Height)
            : content.Y + (content.Height / 2);

    private void DrawBars(DrawingContext context, Rect content, IReadOnlyList<double> values,
        double lo, double hi)
    {
        if (Stroke is not { } brush)
        {
            return;
        }

        double gap = Math.Max(0, Gap);
        double slot = content.Width / values.Count;
        double width = Math.Max(1, slot - gap);
        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            if (!double.IsFinite(v))
            {
                continue;
            }

            // A collapsed domain (an all-zero round set) must draw the floor, not the centre line that
            // YFor returns for a flat series. Half-height bars for a series of zeroes would read as data.
            double top = hi > lo ? YFor(v, content, lo, hi) : content.Bottom;
            double height = Math.Max(MinBarHeight, content.Bottom - top);
            context.FillRectangle(brush,
                new Rect(content.X + (i * slot) + (gap / 2), content.Bottom - height, width, height));
        }
    }

    /// <summary>
    ///     A pass/fail dot row. A hit is a filled dot, a miss is a hollow ring rather than a dimmer fill:
    ///     the ring reads as "this round happened and the answer was no", where a faint dot reads as
    ///     missing data.
    /// </summary>
    private void DrawDots(DrawingContext context, Rect content, IReadOnlyList<double> values)
    {
        double r = Math.Max(1, DotRadius);
        double slot = content.Width / values.Count;
        double cy = content.Y + (content.Height / 2);
        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            bool hit = double.IsFinite(v) && v != 0;
            IBrush? brush = hit ? PositiveBrush : NegativeBrush;
            if (brush is null)
            {
                continue;
            }

            Point centre = new(content.X + (i * slot) + (slot / 2), cy);
            if (hit)
            {
                context.DrawEllipse(brush, null, centre, r, r);
            }
            else
            {
                context.DrawEllipse(null, new Pen(brush, 1), centre, r, r);
            }
        }
    }

    private void DrawLine(DrawingContext context, Rect content, IReadOnlyList<double> values,
        double lo, double hi)
    {
        List<Point> points = [];
        double step = values.Count > 1 ? content.Width / (values.Count - 1) : 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                continue;
            }

            points.Add(new Point(
                values.Count > 1 ? content.X + (i * step) : content.X + (content.Width / 2),
                YFor(values[i], content, lo, hi)));
        }

        if (points.Count == 0)
        {
            return;
        }

        // A single point has no line to draw, so it becomes a dot. Silently drawing nothing would look
        // identical to "no data", which is a different thing.
        if (points.Count == 1)
        {
            if (Stroke is { } only)
            {
                context.DrawEllipse(only, null, points[0], StrokeThickness, StrokeThickness);
            }

            return;
        }

        if (Fill is { } fill)
        {
            StreamGeometry area = new();
            using (StreamGeometryContext g = area.Open())
            {
                g.BeginFigure(new Point(points[0].X, content.Bottom), true);
                foreach (Point p in points)
                {
                    g.LineTo(p);
                }

                g.LineTo(new Point(points[^1].X, content.Bottom));
                g.EndFigure(true);
            }

            context.DrawGeometry(fill, null, area);
        }

        if (Stroke is not { } stroke)
        {
            return;
        }

        Pen pen = new(stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        for (int i = 1; i < points.Count; i++)
        {
            context.DrawLine(pen, points[i - 1], points[i]);
        }
    }
}
