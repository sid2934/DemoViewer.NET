#region

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     A win-versus-loss quantity drawn about a centre line: losses to the left, wins to the right,
///     each arm's length being its own count.
///     <para>
///         This replaces the four columns a duel record usually gets (won, lost, net, rate) with one
///         shape that carries all four. The arms give the two counts, their difference is the visible
///         imbalance, and the total ink is the volume. A rate alone cannot say any of that: a player who
///         wins two duels out of two reads 100% and a player who wins nine of fourteen reads 64%, and
///         only one of them did anything.
///     </para>
///     <para>
///         <see cref="Extent" /> is the shared half-scale across every row, so arms are comparable down
///         the column. Left unset, each bar scales to its own larger arm and the comparison is lost.
///     </para>
/// </summary>
public class DivergingBar : TemplatedControl
{
    /// <summary>The losing count, drawn left of centre.</summary>
    public static readonly StyledProperty<double> NegativeProperty =
        AvaloniaProperty.Register<DivergingBar, double>(nameof(Negative));

    /// <summary>The winning count, drawn right of centre.</summary>
    public static readonly StyledProperty<double> PositiveProperty =
        AvaloniaProperty.Register<DivergingBar, double>(nameof(Positive));

    /// <summary>
    ///     The count that fills one whole arm, shared by every bar in the column. Zero or less falls
    ///     back to this bar's own larger arm, which makes it self-scaling and NOT comparable.
    /// </summary>
    public static readonly StyledProperty<double> ExtentProperty =
        AvaloniaProperty.Register<DivergingBar, double>(nameof(Extent));

    /// <summary>Fill for the losing arm (<c>StatNegative</c>).</summary>
    public static readonly StyledProperty<IBrush?> NegativeBrushProperty =
        AvaloniaProperty.Register<DivergingBar, IBrush?>(nameof(NegativeBrush));

    /// <summary>Fill for the winning arm (<c>StatPositive</c>).</summary>
    public static readonly StyledProperty<IBrush?> PositiveBrushProperty =
        AvaloniaProperty.Register<DivergingBar, IBrush?>(nameof(PositiveBrush));

    /// <summary>The break-even hairline at the centre (<c>BorderStrong</c>).</summary>
    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<DivergingBar, IBrush?>(nameof(AxisBrush));

    /// <summary>Height of the arms. The axis line always spans the full control height.</summary>
    public static readonly StyledProperty<double> BarHeightProperty =
        AvaloniaProperty.Register<DivergingBar, double>(nameof(BarHeight), 14);

    /// <summary>A non-zero arm never draws thinner than this, so a count of one stays visible.</summary>
    private const double MinArm = 2;

    static DivergingBar()
    {
        AffectsRender<DivergingBar>(NegativeProperty, PositiveProperty, ExtentProperty,
            NegativeBrushProperty, PositiveBrushProperty, AxisBrushProperty, BarHeightProperty,
            CornerRadiusProperty, PaddingProperty);
    }

    /// <inheritdoc cref="NegativeProperty" />
    public double Negative
    {
        get => GetValue(NegativeProperty);
        set => SetValue(NegativeProperty, value);
    }

    /// <inheritdoc cref="PositiveProperty" />
    public double Positive
    {
        get => GetValue(PositiveProperty);
        set => SetValue(PositiveProperty, value);
    }

    /// <inheritdoc cref="ExtentProperty" />
    public double Extent
    {
        get => GetValue(ExtentProperty);
        set => SetValue(ExtentProperty, value);
    }

    /// <inheritdoc cref="NegativeBrushProperty" />
    public IBrush? NegativeBrush
    {
        get => GetValue(NegativeBrushProperty);
        set => SetValue(NegativeBrushProperty, value);
    }

    /// <inheritdoc cref="PositiveBrushProperty" />
    public IBrush? PositiveBrush
    {
        get => GetValue(PositiveBrushProperty);
        set => SetValue(PositiveBrushProperty, value);
    }

    /// <inheritdoc cref="AxisBrushProperty" />
    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <inheritdoc cref="BarHeightProperty" />
    public double BarHeight
    {
        get => GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    /// <summary>The count that fills an arm: the shared <see cref="Extent" />, else this bar's own peak.</summary>
    public double EffectiveExtent =>
        Extent > 0 ? Extent : Math.Max(Math.Abs(Negative), Math.Abs(Positive));

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NegativeProperty || change.Property == PositiveProperty)
        {
            AutomationProperties.SetName(this, $"{Positive} won, {Negative} lost");
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect content = new Rect(Bounds.Size).Deflate(Padding);
        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }

        double centre = content.Center.X;
        double half = content.Width / 2;
        double extent = EffectiveExtent;

        if (extent <= 0)
        {
            DrawAxis(context, content, centre);
            return;
        }

        double h = Math.Min(Math.Max(1, BarHeight), content.Height);
        double y = content.Y + ((content.Height - h) / 2);
        float radius = (float)CornerRadius.TopLeft;

        // Arms are drawn away from the centre, so both share the break-even line and the eye compares
        // them against each other rather than against the control's edges.
        if (Negative > 0 && NegativeBrush is { } negative)
        {
            double w = Math.Max(MinArm, Math.Clamp(Negative / extent, 0, 1) * half);
            context.FillRectangle(negative, new Rect(centre - w, y, w, h), radius);
        }

        if (Positive > 0 && PositiveBrush is { } positive)
        {
            double w = Math.Max(MinArm, Math.Clamp(Positive / extent, 0, 1) * half);
            context.FillRectangle(positive, new Rect(centre, y, w, h), radius);
        }

        DrawAxis(context, content, centre);
    }

    /// <summary>
    ///     The break-even line, drawn LAST so it survives the arms. Drawn under them it is invisible on
    ///     every row that has both a win and a loss, which is most of them, and the one reference point
    ///     the whole form depends on would be missing exactly when it is needed.
    /// </summary>
    private void DrawAxis(DrawingContext context, Rect content, double centre)
    {
        if (AxisBrush is { } axis)
        {
            context.FillRectangle(axis, new Rect(centre - 0.5, content.Y, 1, content.Height));
        }
    }
}
