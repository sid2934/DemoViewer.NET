#region

using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     A count drawn as one mark per event rather than as a number.
///     <para>
///         For the magnitudes a scoreboard's rarer columns actually carry (aces, plants, defuses, 4Ks:
///         usually zero, occasionally three) a numeric column is mostly whitespace punctuated by a
///         digit, and zero and one look almost identical while scanning. Pips make the count a length,
///         so a row with six reads as more than a row with two without being read at all, and zero reads
///         as visibly empty rather than as a character.
///     </para>
///     <para>
///         Past <see cref="MaxPips" /> the strip stops drawing marks and writes the number instead: a
///         row of forty dots is worse than "40", and the whole point was legibility at small counts.
///     </para>
/// </summary>
public class PipStrip : TemplatedControl
{
    /// <summary>How many marks to draw.</summary>
    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<PipStrip, int>(nameof(Count));

    /// <summary>Above this the strip renders the number instead of drawing marks.</summary>
    public static readonly StyledProperty<int> MaxPipsProperty =
        AvaloniaProperty.Register<PipStrip, int>(nameof(MaxPips), 12);

    /// <summary>Mark fill.</summary>
    public static readonly StyledProperty<IBrush?> PipBrushProperty =
        AvaloniaProperty.Register<PipStrip, IBrush?>(nameof(PipBrush));

    /// <summary>Drawn instead of a mark when the count is zero. Null draws nothing at all.</summary>
    public static readonly StyledProperty<IBrush?> EmptyBrushProperty =
        AvaloniaProperty.Register<PipStrip, IBrush?>(nameof(EmptyBrush));

    /// <summary>Mark radius.</summary>
    public static readonly StyledProperty<double> PipRadiusProperty =
        AvaloniaProperty.Register<PipStrip, double>(nameof(PipRadius), 3.5);

    /// <summary>Gap between mark centres, beyond the diameter.</summary>
    public static readonly StyledProperty<double> PipGapProperty =
        AvaloniaProperty.Register<PipStrip, double>(nameof(PipGap), 4);

    private FormattedText? _overflow;

    static PipStrip()
    {
        AffectsRender<PipStrip>(CountProperty, MaxPipsProperty, PipBrushProperty, EmptyBrushProperty,
            PipRadiusProperty, PipGapProperty, ForegroundProperty, FontSizeProperty);
        AffectsMeasure<PipStrip>(CountProperty, MaxPipsProperty, PipRadiusProperty, PipGapProperty);
    }

    /// <inheritdoc cref="CountProperty" />
    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <inheritdoc cref="MaxPipsProperty" />
    public int MaxPips
    {
        get => GetValue(MaxPipsProperty);
        set => SetValue(MaxPipsProperty, value);
    }

    /// <inheritdoc cref="PipBrushProperty" />
    public IBrush? PipBrush
    {
        get => GetValue(PipBrushProperty);
        set => SetValue(PipBrushProperty, value);
    }

    /// <inheritdoc cref="EmptyBrushProperty" />
    public IBrush? EmptyBrush
    {
        get => GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    /// <inheritdoc cref="PipRadiusProperty" />
    public double PipRadius
    {
        get => GetValue(PipRadiusProperty);
        set => SetValue(PipRadiusProperty, value);
    }

    /// <inheritdoc cref="PipGapProperty" />
    public double PipGap
    {
        get => GetValue(PipGapProperty);
        set => SetValue(PipGapProperty, value);
    }

    /// <summary>True once the count is too large to draw and the number is written instead.</summary>
    public bool IsOverflowing => Count > MaxPips;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double step = (PipRadius * 2) + PipGap;
        double width = IsOverflowing
            ? BuildOverflow()?.Width ?? 0
            : Math.Max(1, Count) * step;
        return new Size(width + Padding.Left + Padding.Right,
            Math.Max(PipRadius * 2, FontSize) + Padding.Top + Padding.Bottom);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CountProperty || change.Property == FontSizeProperty
                                             || change.Property == ForegroundProperty)
        {
            _overflow = null;
            AutomationProperties.SetName(this, Count.ToString(CultureInfo.InvariantCulture));
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

        if (IsOverflowing)
        {
            if (BuildOverflow() is { } text)
            {
                context.DrawText(text,
                    new Point(content.X, content.Y + ((content.Height - text.Height) / 2)));
            }

            return;
        }

        double r = Math.Max(1, PipRadius);
        double step = (r * 2) + Math.Max(0, PipGap);
        double cy = content.Y + (content.Height / 2);

        // Zero draws a hollow placeholder rather than nothing, so an empty row still reads as a row
        // that was measured rather than one that failed to load.
        if (Count <= 0)
        {
            if (EmptyBrush is { } empty)
            {
                context.DrawEllipse(null, new Pen(empty, 1), new Point(content.X + r, cy), r, r);
            }

            return;
        }

        if (PipBrush is not { } brush)
        {
            return;
        }

        for (int i = 0; i < Count; i++)
        {
            double cx = content.X + r + (i * step);
            if (cx + r > content.Right)
            {
                break;
            }

            context.DrawEllipse(brush, null, new Point(cx, cy), r, r);
        }
    }

    private FormattedText? BuildOverflow()
    {
        if (!IsOverflowing)
        {
            return null;
        }

        return _overflow ??= new FormattedText(Count.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight), FontSize, PipBrush ?? Foreground);
    }
}
