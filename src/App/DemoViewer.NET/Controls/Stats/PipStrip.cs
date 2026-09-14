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
///         The same fallback fires when the marks simply do not FIT the width the strip was given,
///         whatever <see cref="MaxPips" /> allows, because the alternative is dropping marks off the
///         right edge and rendering nine, ten and twelve identically.
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
        AffectsMeasure<PipStrip>(CountProperty, MaxPipsProperty, PipRadiusProperty, PipGapProperty,
            PaddingProperty, FontFeaturesProperty);
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

    /// <summary>
    ///     True once the count is too large to draw and the number is written instead. This is the
    ///     DECLARED cap only; the arranged width can force the same fallback at a lower count, which
    ///     <see cref="Render" /> decides once it knows how much room it actually got.
    /// </summary>
    public bool IsOverflowing => Count > MaxPips;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double step = (PipRadius * 2) + PipGap;
        FormattedText text = BuildCountText();
        double width = IsOverflowing
            ? text.Width
            : Math.Max(1, Count) * step;

        // The number's LINE height, not FontSize, and reserved even when the marks fit the declared
        // cap. FontSize is the em size and a line stands taller than that, so measuring by it clipped
        // the digits; and Render falls back to the number whenever the ARRANGED width cannot hold the
        // marks, which is decided after this. A strip that goes on to draw dots pays a pixel or two for
        // the reservation, which is much the cheaper half of the trade.
        return new Size(width + Padding.Left + Padding.Right,
            Math.Max(PipRadius * 2, text.Height) + Padding.Top + Padding.Bottom);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The number is cached with its glyphs, its metrics AND its brush baked in, so every input to
        // any of the three has to drop it. PipBrush is in the list because that is what the number is
        // drawn with (Foreground is only the fallback): without it a theme switch repaints every pip
        // and leaves the overflow number in the old theme's colour.
        if (change.Property == CountProperty || change.Property == FontSizeProperty
                                             || change.Property == ForegroundProperty
                                             || change.Property == PipBrushProperty
                                             || change.Property == FontFamilyProperty
                                             || change.Property == FontWeightProperty
                                             || change.Property == FontStyleProperty
                                             || change.Property == FontFeaturesProperty)
        {
            _overflow = null;
        }

        if (change.Property == CountProperty)
        {
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

        double r = Math.Max(1, PipRadius);
        double step = (r * 2) + Math.Max(0, PipGap);
        double cy = content.Y + (content.Height / 2);

        // How many marks the ARRANGED width can hold: the first needs a diameter, each one after it a
        // further step. Measure asked for enough room, but a fixed-width column does not have to grant
        // it, and a strip that just stopped drawing at the edge reported nine, ten and twelve as the
        // same eight dots. Falling back to the number keeps the count readable at any width.
        int fits = (int)Math.Floor(((content.Width - (r * 2)) / step) + 1);
        if (IsOverflowing || Count > fits)
        {
            FormattedText text = BuildCountText();
            context.DrawText(text,
                new Point(content.X, content.Y + ((content.Height - text.Height) / 2)));
            return;
        }

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
            context.DrawEllipse(brush, null, new Point(content.X + r + (i * step), cy), r, r);
        }
    }

    /// <summary>The count written as a number, for whichever of the two overflow paths took it.</summary>
    private FormattedText BuildCountText()
    {
        if (_overflow is not null)
        {
            return _overflow;
        }

        _overflow = new FormattedText(Count.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight), FontSize, PipBrush ?? Foreground);
        // `tnum` again, for the same reason it is on every other number in the library: the strip sits
        // in a column, and an overflow row whose digits are proportionally spaced shifts against the
        // rows above and below it.
        if (FontFeatures is { Count: > 0 } features)
        {
            _overflow.SetFontFeatures(features);
        }

        return _overflow;
    }
}
