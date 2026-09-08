#region

using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     One slice of a <see cref="SegmentedBar" />: how much, what colour, and what to write on it.
/// </summary>
/// <param name="Value">Share of the whole. Non-finite and negative values are ignored.</param>
/// <param name="Brush">Slice fill. Null falls back to the bar's <see cref="SegmentedBar.FillBrush" />.</param>
/// <param name="Label">Text drawn on the slice when it is wide enough. Null draws nothing.</param>
public sealed record StatSegment(double Value, IBrush? Brush = null, string? Label = null);

/// <summary>
///     A stacked proportion bar: a whole split into labelled parts, each sized by its share. Serves both
///     densities in the reference design, the full-width utility breakdown strip and the in-cell
///     four-slice quad, from the same control.
///     <para>
///         Shares are computed from the values, so a caller passes counts (73 flashes, 63 smokes) and
///         never percentages. A slice too narrow for its label simply loses the label rather than
///         overflowing into its neighbour.
///     </para>
/// </summary>
public class SegmentedBar : TemplatedControl
{
    /// <summary>The slices, in draw order, left to right.</summary>
    public static readonly StyledProperty<IReadOnlyList<StatSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedBar, IReadOnlyList<StatSegment>?>(nameof(Segments));

    /// <summary>Tighter gap and corner radius, for the in-cell density.</summary>
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<SegmentedBar, bool>(nameof(Compact));

    /// <summary>Whether slices draw their <see cref="StatSegment.Label" />.</summary>
    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<SegmentedBar, bool>(nameof(ShowLabels), true);

    /// <summary>Pixels between slices. Ignored when <see cref="Compact" /> is set.</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(Gap), 3);

    /// <summary>Fallback slice fill for a segment with no brush of its own (<c>StatBarFill</c>).</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(FillBrush));

    /// <summary>The empty track drawn when there is nothing to show (<c>StatBarTrack</c>).</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(TrackBrush));

    /// <summary>Label colour. Null uses <c>Foreground</c>.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(LabelBrush));

    private const double CompactGap = 1.5;
    private const double CompactRadius = 2;

    /// <summary>Slack a label needs either side before it is allowed to draw inside its slice.</summary>
    private const double LabelPadding = 4;

    static SegmentedBar()
    {
        AffectsRender<SegmentedBar>(SegmentsProperty, CompactProperty, ShowLabelsProperty, GapProperty,
            FillBrushProperty, TrackBrushProperty, LabelBrushProperty, CornerRadiusProperty,
            ForegroundProperty, FontSizeProperty, FontFamilyProperty);
    }

    /// <inheritdoc cref="SegmentsProperty" />
    public IReadOnlyList<StatSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <inheritdoc cref="CompactProperty" />
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <inheritdoc cref="ShowLabelsProperty" />
    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    /// <inheritdoc cref="GapProperty" />
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <inheritdoc cref="FillBrushProperty" />
    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <inheritdoc cref="TrackBrushProperty" />
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty" />
    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <summary>Sum of the finite, positive slice values. Zero means there is nothing to draw.</summary>
    public double Total
    {
        get
        {
            double total = 0;
            foreach (StatSegment s in Segments ?? [])
            {
                if (double.IsFinite(s.Value) && s.Value > 0)
                {
                    total += s.Value;
                }
            }

            return total;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty)
        {
            AutomationProperties.SetName(this, DescribeForAutomation());
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

        double radius = Compact ? CompactRadius : CornerRadius.TopLeft;
        double total = Total;
        IReadOnlyList<StatSegment> segments = Segments ?? [];

        // Nothing to apportion: show the empty track rather than a blank hole, so a zero-utility round
        // still reads as a bar that happens to be empty.
        if (total <= 0 || segments.Count == 0)
        {
            if (TrackBrush is { } empty)
            {
                context.FillRectangle(empty, content, (float)radius);
            }

            return;
        }

        double gap = Compact ? CompactGap : Math.Max(0, Gap);
        int drawn = 0;
        foreach (StatSegment s in segments)
        {
            if (double.IsFinite(s.Value) && s.Value > 0)
            {
                drawn++;
            }
        }

        double available = Math.Max(0, content.Width - (gap * Math.Max(0, drawn - 1)));
        double x = content.X;
        foreach (StatSegment s in segments)
        {
            if (!double.IsFinite(s.Value) || s.Value <= 0)
            {
                continue;
            }

            double w = available * (s.Value / total);
            Rect slice = new(x, content.Y, w, content.Height);
            IBrush? fill = s.Brush ?? FillBrush;
            if (fill is not null)
            {
                context.FillRectangle(fill, slice, (float)radius);
            }

            if (ShowLabels && !string.IsNullOrEmpty(s.Label))
            {
                DrawLabel(context, slice, s.Label);
            }

            x += w + gap;
        }
    }

    private void DrawLabel(DrawingContext context, Rect slice, string label)
    {
        FormattedText text = new(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight), FontSize, LabelBrush ?? Foreground);

        // A slice narrower than its own label would otherwise spill the text over its neighbours, which
        // reads as a mis-attributed number. Dropping the label is the lesser loss: the slice width still
        // carries the share.
        if (text.Width + (LabelPadding * 2) > slice.Width || text.Height > slice.Height)
        {
            return;
        }

        context.DrawText(text, new Point(
            slice.X + ((slice.Width - text.Width) / 2),
            slice.Y + ((slice.Height - text.Height) / 2)));
    }

    private string DescribeForAutomation()
    {
        IReadOnlyList<StatSegment> segments = Segments ?? [];
        double total = Total;
        if (total <= 0 || segments.Count == 0)
        {
            return "empty";
        }

        List<string> parts = [];
        foreach (StatSegment s in segments)
        {
            if (!double.IsFinite(s.Value) || s.Value <= 0)
            {
                continue;
            }

            string share = (s.Value / total).ToString("P0", CultureInfo.CurrentCulture);
            parts.Add(string.IsNullOrEmpty(s.Label) ? share : $"{s.Label} {share}");
        }

        return string.Join(", ", parts);
    }
}
