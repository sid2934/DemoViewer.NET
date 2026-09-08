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
/// <param name="Brush">
///     Slice fill. Null falls back to the bar's slot palette (see <paramref name="Slot" />), and then to
///     <see cref="SegmentedBar.FillBrush" />.
/// </param>
/// <param name="Label">Text drawn on the slice when it is wide enough. Null draws nothing.</param>
/// <param name="Slot">
///     Index into the bar's slot palette, 0-5. This is how a view model names a colour without holding
///     one: the meaning ("flashes", "rifle kills") is the view model's, the pigment is the theme's.
///     Negative means no slot.
/// </param>
public sealed record StatSegment(double Value, IBrush? Brush = null, string? Label = null, int Slot = -1);

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

    /// <summary>
    ///     A whole that this bar is a part of. When set above <see cref="Total" />, the bar fills only
    ///     its share of the available width instead of stretching to fill it.
    ///     <para>
    ///         This is what lets a column of these bars be compared. Without it every bar is full width
    ///         and only the internal split differs, so a player who threw fifty grenades and one who
    ///         threw five draw the same size bar. With it, bar LENGTH is volume and the split is the mix,
    ///         which is two facts from one shape.
    ///     </para>
    /// </summary>
    public static readonly StyledProperty<double> MaxTotalProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(MaxTotal));

    /// <summary>Slot-palette entry 0. Fed a token from <c>Styles/Stats.axaml</c>, never held in code.</summary>
    public static readonly StyledProperty<IBrush?> Slot0BrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(Slot0Brush));

    /// <summary>Slot-palette entry 1.</summary>
    public static readonly StyledProperty<IBrush?> Slot1BrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(Slot1Brush));

    /// <summary>Slot-palette entry 2.</summary>
    public static readonly StyledProperty<IBrush?> Slot2BrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(Slot2Brush));

    /// <summary>Slot-palette entry 3.</summary>
    public static readonly StyledProperty<IBrush?> Slot3BrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(Slot3Brush));

    /// <summary>Slot-palette entry 4.</summary>
    public static readonly StyledProperty<IBrush?> Slot4BrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(Slot4Brush));

    /// <summary>Slot-palette entry 5.</summary>
    public static readonly StyledProperty<IBrush?> Slot5BrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(Slot5Brush));

    private const double CompactGap = 1.5;
    private const double CompactRadius = 2;

    /// <summary>Slack a label needs either side before it is allowed to draw inside its slice.</summary>
    private const double LabelPadding = 4;

    static SegmentedBar()
    {
        AffectsRender<SegmentedBar>(SegmentsProperty, CompactProperty, ShowLabelsProperty, GapProperty,
            FillBrushProperty, TrackBrushProperty, LabelBrushProperty, CornerRadiusProperty,
            ForegroundProperty, FontSizeProperty, FontFamilyProperty, MaxTotalProperty,
            Slot0BrushProperty, Slot1BrushProperty, Slot2BrushProperty, Slot3BrushProperty,
            Slot4BrushProperty, Slot5BrushProperty);
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

    /// <inheritdoc cref="MaxTotalProperty" />
    public double MaxTotal
    {
        get => GetValue(MaxTotalProperty);
        set => SetValue(MaxTotalProperty, value);
    }

    /// <inheritdoc cref="Slot0BrushProperty" />
    public IBrush? Slot0Brush
    {
        get => GetValue(Slot0BrushProperty);
        set => SetValue(Slot0BrushProperty, value);
    }

    /// <inheritdoc cref="Slot1BrushProperty" />
    public IBrush? Slot1Brush
    {
        get => GetValue(Slot1BrushProperty);
        set => SetValue(Slot1BrushProperty, value);
    }

    /// <inheritdoc cref="Slot2BrushProperty" />
    public IBrush? Slot2Brush
    {
        get => GetValue(Slot2BrushProperty);
        set => SetValue(Slot2BrushProperty, value);
    }

    /// <inheritdoc cref="Slot3BrushProperty" />
    public IBrush? Slot3Brush
    {
        get => GetValue(Slot3BrushProperty);
        set => SetValue(Slot3BrushProperty, value);
    }

    /// <inheritdoc cref="Slot4BrushProperty" />
    public IBrush? Slot4Brush
    {
        get => GetValue(Slot4BrushProperty);
        set => SetValue(Slot4BrushProperty, value);
    }

    /// <inheritdoc cref="Slot5BrushProperty" />
    public IBrush? Slot5Brush
    {
        get => GetValue(Slot5BrushProperty);
        set => SetValue(Slot5BrushProperty, value);
    }

    /// <summary>The brush a segment resolves to: its own, then its slot, then the flat fill.</summary>
    private IBrush? BrushFor(StatSegment segment) =>
        segment.Brush ?? segment.Slot switch
        {
            0 => Slot0Brush,
            1 => Slot1Brush,
            2 => Slot2Brush,
            3 => Slot3Brush,
            4 => Slot4Brush,
            5 => Slot5Brush,
            _ => null
        } ?? FillBrush;

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

        // A shared whole turns bar LENGTH into volume. Without it every bar is full width and only the
        // split differs, so fifty grenades and five draw the same size.
        double share = MaxTotal > 0 ? Math.Clamp(total / MaxTotal, 0, 1) : 1;
        int drawn = 0;
        foreach (StatSegment s in segments)
        {
            if (double.IsFinite(s.Value) && s.Value > 0)
            {
                drawn++;
            }
        }

        double available = Math.Max(0, (content.Width * share) - (gap * Math.Max(0, drawn - 1)));
        double x = content.X;
        foreach (StatSegment s in segments)
        {
            if (!double.IsFinite(s.Value) || s.Value <= 0)
            {
                continue;
            }

            double w = available * (s.Value / total);
            Rect slice = new(x, content.Y, w, content.Height);
            IBrush? fill = BrushFor(s);
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
