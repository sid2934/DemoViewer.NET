#region

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>Which edge a <see cref="StatValue" />'s bar grows from.</summary>
public enum StatBarAlignment
{
    /// <summary>
    ///     Follow the text: a right-aligned number gets a right-anchored fill. Worth choosing where
    ///     there is no track behind the number to keep the two connected.
    /// </summary>
    Auto,

    /// <summary>Always grow from the left edge, whatever the text does.</summary>
    Left,

    /// <summary>Grow outward from the centre.</summary>
    Center,

    /// <summary>Always grow from the right edge.</summary>
    Right
}

/// <summary>How a <see cref="StatValue" /> presents its number.</summary>
public enum StatValueMode
{
    /// <summary>Number over a background bar whose length is the value's position in its domain.</summary>
    Bar,

    /// <summary>Number inside a filled rounded pill tinted by sentiment. Sizes to its text.</summary>
    Chip,

    /// <summary>Number only, coloured by sentiment. No bar, no fill.</summary>
    Plain
}

/// <summary>
///     One stat number carrying two channels at once: a colour that says good or bad, and a bar length
///     that says how far from the pack. The workhorse of the stats surfaces; every scoreboard cell, every
///     rating pill and every tile value is one of these.
///     <para>
///         <b>The bar is never polarity-inverted.</b> A deaths column draws its longest bar for the most
///         deaths, because a big number over a short bar reads as a rendering fault. Judgement is carried
///         entirely by the text colour. See <see cref="StatScale" />.
///     </para>
///     <para>
///         The bar always grows from the left edge while the text aligns per
///         <see cref="TextAlignment" /> (right by default, so magnitudes scan vertically down a column).
///     </para>
/// </summary>
public class StatValue : StatPresenter
{
    /// <summary>Bar, chip or plain text.</summary>
    public static readonly StyledProperty<StatValueMode> ModeProperty =
        AvaloniaProperty.Register<StatValue, StatValueMode>(nameof(Mode));

    /// <summary>Marks the best value in its column with a trailing star, as the reference board does.</summary>
    public static readonly StyledProperty<bool> IsLeaderProperty =
        AvaloniaProperty.Register<StatValue, bool>(nameof(IsLeader));

    /// <summary>
    ///     Tints the bar fill by sentiment instead of leaving it neutral. Off by default: the reference
    ///     keeps every bar the same slate so colour is not spent twice on the same fact, which is what
    ///     stops a fifteen-column table reading as a heat map.
    /// </summary>
    public static readonly StyledProperty<bool> TintBarProperty =
        AvaloniaProperty.Register<StatValue, bool>(nameof(TintBar));

    /// <summary>Text alignment inside the cell. Right by default, so magnitudes scan vertically.</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<StatValue, TextAlignment>(nameof(TextAlignment), TextAlignment.Right);

    /// <summary>
    ///     Which edge the fill grows from. Defaults to <see cref="StatBarAlignment.Left" />.
    ///     <para>
    ///         <b>Left, because every fill then starts from the same place.</b> That is what lets lengths
    ///         be compared down a column the way a bar chart's are; anchoring to the right gives each row
    ///         its own origin and the eye has to re-find it on every line.
    ///     </para>
    ///     <para>
    ///         The objection to left used to be real: a right-aligned number over a left-growing fill
    ///         separates from it exactly when the fill is shortest, leaving a value floating beside a stub
    ///         of colour. The TRACK removed it. With a full-width track behind the number, the value
    ///         always sits on something and the fill is read against the track rather than against the
    ///         number. Turning the track on is what made left-anchoring the better default.
    ///     </para>
    ///     <para>
    ///         <see cref="StatBarAlignment.Auto" /> remains for surfaces that want the fill to share the
    ///         text's edge.
    ///     </para>
    /// </summary>
    public static readonly StyledProperty<StatBarAlignment> BarAlignmentProperty =
        AvaloniaProperty.Register<StatValue, StatBarAlignment>(
            nameof(BarAlignment), StatBarAlignment.Left);

    /// <summary>The unfilled part of the bar (<c>StatBarTrack</c>). Null draws no track.</summary>
    public static readonly StyledProperty<IBrush?> BarTrackBrushProperty =
        AvaloniaProperty.Register<StatValue, IBrush?>(nameof(BarTrackBrush));

    /// <summary>The filled part of the bar (<c>StatBarFill</c>).</summary>
    public static readonly StyledProperty<IBrush?> BarFillBrushProperty =
        AvaloniaProperty.Register<StatValue, IBrush?>(nameof(BarFillBrush));

    /// <summary>The column-leader star (<c>AccentAmber</c>).</summary>
    public static readonly StyledProperty<IBrush?> LeaderBrushProperty =
        AvaloniaProperty.Register<StatValue, IBrush?>(nameof(LeaderBrush));

    /// <summary>Alpha applied to the sentiment colour when it fills a <see cref="StatValueMode.Chip" />.</summary>
    private const double ChipFillOpacity = 0.22;

    /// <summary>Gap between the number and the leader star.</summary>
    private const double LeaderGap = 3;

    /// <summary>The leader marker. A glyph rather than a drawn path, so it follows the control's font.</summary>
    private const string LeaderGlyph = "★";

    private FormattedText? _formatted;
    private FormattedText? _star;

    static StatValue()
    {
        AffectsRender<StatValue>(ModeProperty, IsLeaderProperty, TintBarProperty, TextAlignmentProperty,
            BarAlignmentProperty,
            BarTrackBrushProperty, BarFillBrushProperty, LeaderBrushProperty, BackgroundProperty,
            CornerRadiusProperty);
        AffectsMeasure<StatValue>(ValueProperty, TextProperty, FormatProperty, IsLeaderProperty,
            FontFamilyProperty, FontSizeProperty, FontWeightProperty, PaddingProperty);
    }

    /// <inheritdoc cref="ModeProperty" />
    public StatValueMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <inheritdoc cref="IsLeaderProperty" />
    public bool IsLeader
    {
        get => GetValue(IsLeaderProperty);
        set => SetValue(IsLeaderProperty, value);
    }

    /// <inheritdoc cref="TintBarProperty" />
    public bool TintBar
    {
        get => GetValue(TintBarProperty);
        set => SetValue(TintBarProperty, value);
    }

    /// <inheritdoc cref="TextAlignmentProperty" />
    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    /// <inheritdoc cref="BarAlignmentProperty" />
    public StatBarAlignment BarAlignment
    {
        get => GetValue(BarAlignmentProperty);
        set => SetValue(BarAlignmentProperty, value);
    }

    /// <summary>The anchor actually in force, with <see cref="StatBarAlignment.Auto" /> resolved.</summary>
    public StatBarAlignment EffectiveBarAlignment => BarAlignment switch
    {
        StatBarAlignment.Auto => TextAlignment switch
        {
            TextAlignment.Left => StatBarAlignment.Left,
            TextAlignment.Center => StatBarAlignment.Center,
            _ => StatBarAlignment.Right
        },
        var explicitly => explicitly
    };

    /// <inheritdoc cref="BarTrackBrushProperty" />
    public IBrush? BarTrackBrush
    {
        get => GetValue(BarTrackBrushProperty);
        set => SetValue(BarTrackBrushProperty, value);
    }

    /// <inheritdoc cref="BarFillBrushProperty" />
    public IBrush? BarFillBrush
    {
        get => GetValue(BarFillBrushProperty);
        set => SetValue(BarFillBrushProperty, value);
    }

    /// <inheritdoc cref="LeaderBrushProperty" />
    public IBrush? LeaderBrush
    {
        get => GetValue(LeaderBrushProperty);
        set => SetValue(LeaderBrushProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        FormattedText? text = BuildText();

        // Render CONSTRAINS this same cached instance (MaxTextWidth, then a one-line trim). Reading
        // Width straight back would return the ELLIPSED width, and the cell would measure narrower on
        // every pass until it had walked itself down to the width of the ellipsis. Clear the constraint
        // first, so what is measured is the natural width, which is what measure is being asked for.
        if (text is not null)
        {
            text.MaxTextWidth = double.PositiveInfinity;
        }

        Thickness pad = Padding;
        double w = (text?.Width ?? 0) + pad.Left + pad.Right;
        double h = (text?.Height ?? FontSize) + pad.Top + pad.Bottom;
        if (IsLeader)
        {
            w += LeaderGap + (BuildStar()?.Width ?? 0);
        }

        return new Size(w, h);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The drawn text is cached because a scoreboard rebuilds hundreds of these on every sort; anything
        // that changes its glyphs, metrics or colour has to drop the cache or the cell paints the old value.
        if (change.Property == ValueProperty || change.Property == TextProperty
                                             || change.Property == FormatProperty
                                             || change.Property == FontFamilyProperty
                                             || change.Property == FontSizeProperty
                                             || change.Property == FontWeightProperty
                                             || change.Property == FontStyleProperty
                                             || change.Property == ForegroundProperty
                                             || change.Property == FontFeaturesProperty
                                             || change.Property == LeaderBrushProperty)
        {
            _formatted = null;
            _star = null;
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (Background is { } back)
        {
            context.FillRectangle(back, bounds, (float)CornerRadius.TopLeft);
        }

        Rect content = bounds.Deflate(Padding);
        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }

        IBrush? accent = Accent;
        FormattedText? text = BuildText();
        double starWidth = IsLeader ? (BuildStar()?.Width ?? 0) + LeaderGap : 0;

        switch (Mode)
        {
            // A usable DOMAIN, not just a value. A cell with a number but no scale (a totals row, an
            // uncatalogued column) would otherwise draw a full-width track with nothing in it, which
            // reads as a measured zero rather than as "not measured". Same for a column where every
            // player tied: there is no comparison to draw.
            case StatValueMode.Bar when Value.HasValue && EffectiveScale.HasDomain:
                DrawBar(context, content, Fraction, accent);
                break;
            case StatValueMode.Chip when text is not null:
                DrawChip(context, content, text, starWidth, accent);
                break;
            case StatValueMode.Plain:
            default:
                break;
        }

        if (text is null)
        {
            return;
        }

        // The star sits in a reserved right-hand gutter so it never overlaps the number, whichever way the
        // text is aligned.
        Rect textArea = content.Deflate(new Thickness(0, 0, starWidth, 0));
        text.MaxTextWidth = Math.Max(0, textArea.Width);
        text.TextAlignment = TextAlignment;

        // MaxLineCount FIRST, then trimming. FormattedText WRAPS by default once MaxTextWidth is set, so
        // trimming alone leaves a six-digit value stacked over its own decimals inside a 48px column
        // instead of ellipsed. Pinning one line is what turns the constraint into a trim.
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;

        // Unconditionally, the null case included. The brush is baked into the CACHED text, so a cell
        // that was accented and then fell back into the neutral band (a re-sort, a new scale, a gated
        // column) would otherwise keep painting the colour it had before the judgement was withdrawn.
        text.SetForegroundBrush(accent ?? Foreground ?? Brushes.Transparent);

        context.DrawText(text, new Point(textArea.X, textArea.Y + ((textArea.Height - text.Height) / 2)));

        if (IsLeader && BuildStar() is { } star)
        {
            context.DrawText(star,
                new Point(content.Right - star.Width, content.Y + ((content.Height - star.Height) / 2)));
        }
    }

    /// <summary>
    ///     Draws the track, then the fill, anchored per <see cref="BarAlignment" />.
    ///     <para>
    ///         The track is what makes a fill legible: without a full-width reference the eye has nothing
    ///         to measure the fill against, and a half-filled bar is indistinguishable from a full bar in
    ///         a narrower cell.
    ///     </para>
    ///     <para>
    ///         Bar LENGTH is the signal, so the anchor carries no meaning of its own. It defaults to the
    ///         left because a shared origin is what makes lengths comparable down a column.
    ///     </para>
    /// </summary>
    private void DrawBar(DrawingContext context, Rect content, double fraction, IBrush? accent)
    {
        float radius = (float)CornerRadius.TopLeft;
        if (BarTrackBrush is { } track)
        {
            context.FillRectangle(track, content, radius);
        }

        if (fraction <= 0)
        {
            return;
        }

        IBrush? fill = TintBar ? accent ?? BarFillBrush : BarFillBrush;
        if (fill is null)
        {
            return;
        }

        double width = Math.Max(1, content.Width * fraction);
        Rect filled = EffectiveBarAlignment switch
        {
            StatBarAlignment.Right => content.WithX(content.Right - width).WithWidth(width),
            StatBarAlignment.Center => content.WithX(content.X + ((content.Width - width) / 2)).WithWidth(width),
            _ => content.WithWidth(width)
        };

        context.FillRectangle(fill, filled, radius);
    }

    private void DrawChip(DrawingContext context, Rect content, FormattedText text, double starWidth,
        IBrush? accent)
    {
        double width = Math.Min(content.Width, text.Width + Padding.Left + Padding.Right + starWidth);
        Rect chip = TextAlignment switch
        {
            TextAlignment.Left => content.WithWidth(width),
            TextAlignment.Center => content.WithX(content.X + ((content.Width - width) / 2)).WithWidth(width),
            _ => content.WithX(content.Right - width).WithWidth(width)
        };

        // The chip fill is the sentiment colour at low alpha, so one token drives both the number and the
        // pill behind it. Only a solid brush can be faded that way; anything else falls back to the flat
        // bar fill rather than painting an opaque block over the text.
        IBrush? fill = accent is ISolidColorBrush solid
            ? new ImmutableSolidColorBrush(solid.Color, ChipFillOpacity)
            : BarFillBrush;
        if (fill is not null)
        {
            context.FillRectangle(fill, chip, (float)Math.Max(3, CornerRadius.TopLeft));
        }
    }

    private FormattedText? BuildText()
    {
        string display = DisplayText;
        if (display.Length == 0)
        {
            return null;
        }

        if (_formatted is not null)
        {
            return _formatted;
        }

        _formatted = new FormattedText(display, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle, FontWeight), FontSize,
            Foreground);
        // Inherited from TemplatedControl, and the one that matters is `tnum`. The board runs in the
        // proportional UI face, and without tabular figures a column of numbers stops lining up under
        // itself, which is exactly the comparison a scoreboard exists to support.
        if (FontFeatures is { Count: > 0 } features)
        {
            _formatted.SetFontFeatures(features);
        }

        return _formatted;
    }

    private FormattedText BuildStar() =>
        _star ??= new FormattedText(LeaderGlyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight), FontSize * 0.85, LeaderBrush ?? Foreground);
}
