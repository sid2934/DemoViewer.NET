#region

using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     What every stat-presenting control shares: one number, the scale that judges it, and the four
///     accent brushes that judgement resolves to. <see cref="StatValue" />, <see cref="RingGauge" /> and
///     <see cref="StatTile" /> all derive from it so the sentiment rule lives in exactly one place.
///     <para>
///         <b>Drive it with a <see cref="Scale" />, or with the loose properties.</b> Setting
///         <see cref="Scale" /> wins; otherwise <see cref="Minimum" />, <see cref="Maximum" />,
///         <see cref="Polarity" /> and the neutral band compose the same thing. With no usable domain the
///         control shows its text and no judgement, which is what an uncatalogued column should look like.
///     </para>
///     <para>
///         <b>Theme contract:</b> every colour is a <see cref="StyledProperty{T}" /> fed from
///         <c>Styles/Stats.axaml</c> via <c>{DynamicResource Token}</c>, so a theme switch re-resolves the
///         brush and <c>AffectsRender</c> repaints. No brush is held in code.
///     </para>
///     <para>
///         <b>Neutral paints nothing.</b> A value inside the dead zone leaves <c>Foreground</c>
///         alone rather than picking a "neutral" token, so it inherits the surrounding text colour and
///         stays correct in every theme. Most cells in a healthy table are neutral; that is the point.
///     </para>
/// </summary>
public abstract class StatPresenter : TemplatedControl
{
    /// <summary>The raw number. Null renders <see cref="Text" /> (or nothing) with no judgement.</summary>
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<StatPresenter, double?>(nameof(Value));

    /// <summary>Display override. Null formats <see cref="Value" /> with <see cref="Format" />.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatPresenter, string?>(nameof(Text));

    /// <summary>Numeric format for <see cref="Value" />, invariant culture. Matches the board's default.</summary>
    public static readonly StyledProperty<string> FormatProperty =
        AvaloniaProperty.Register<StatPresenter, string>(nameof(Format), "0.##");

    /// <summary>The column's scale. Set this, or the loose <see cref="Minimum" />/<see cref="Maximum" /> pair.</summary>
    public static readonly StyledProperty<StatScale?> ScaleProperty =
        AvaloniaProperty.Register<StatPresenter, StatScale?>(nameof(Scale));

    /// <summary>Low end of the domain, when no <see cref="Scale" /> is set.</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<StatPresenter, double>(nameof(Minimum));

    /// <summary>High end of the domain, when no <see cref="Scale" /> is set.</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<StatPresenter, double>(nameof(Maximum));

    /// <summary>Which direction is good, when no <see cref="Scale" /> is set.</summary>
    public static readonly StyledProperty<StatPolarity> PolarityProperty =
        AvaloniaProperty.Register<StatPresenter, StatPolarity>(nameof(Polarity));

    /// <summary>Lower edge of the uncoloured dead zone, when no <see cref="Scale" /> is set.</summary>
    public static readonly StyledProperty<double?> NeutralLowProperty =
        AvaloniaProperty.Register<StatPresenter, double?>(nameof(NeutralLow));

    /// <summary>Upper edge of the uncoloured dead zone, when no <see cref="Scale" /> is set.</summary>
    public static readonly StyledProperty<double?> NeutralHighProperty =
        AvaloniaProperty.Register<StatPresenter, double?>(nameof(NeutralHigh));

    /// <summary>
    ///     Absolute sentiment at which the soft accent gives way to the strong one. Exposed rather than
    ///     hard-coded because the band that reads well is a per-surface judgement.
    /// </summary>
    public static readonly StyledProperty<double> StrongSentimentAtProperty =
        AvaloniaProperty.Register<StatPresenter, double>(nameof(StrongSentimentAt), 0.5);

    /// <summary>Strong-good accent (<c>StatPositive</c>).</summary>
    public static readonly StyledProperty<IBrush?> PositiveBrushProperty =
        AvaloniaProperty.Register<StatPresenter, IBrush?>(nameof(PositiveBrush));

    /// <summary>Mild-good accent (<c>StatPositiveSoft</c>).</summary>
    public static readonly StyledProperty<IBrush?> PositiveSoftBrushProperty =
        AvaloniaProperty.Register<StatPresenter, IBrush?>(nameof(PositiveSoftBrush));

    /// <summary>Mild-bad accent (<c>StatNegativeSoft</c>).</summary>
    public static readonly StyledProperty<IBrush?> NegativeSoftBrushProperty =
        AvaloniaProperty.Register<StatPresenter, IBrush?>(nameof(NegativeSoftBrush));

    /// <summary>Strong-bad accent (<c>StatNegative</c>).</summary>
    public static readonly StyledProperty<IBrush?> NegativeBrushProperty =
        AvaloniaProperty.Register<StatPresenter, IBrush?>(nameof(NegativeBrush));

    static StatPresenter()
    {
        AffectsRender<StatPresenter>(ValueProperty, TextProperty, FormatProperty, ScaleProperty,
            MinimumProperty, MaximumProperty, PolarityProperty, NeutralLowProperty, NeutralHighProperty,
            StrongSentimentAtProperty, PositiveBrushProperty, PositiveSoftBrushProperty,
            NegativeSoftBrushProperty, NegativeBrushProperty);
    }

    /// <inheritdoc cref="ValueProperty" />
    public double? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="TextProperty" />
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc cref="FormatProperty" />
    public string Format
    {
        get => GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    /// <inheritdoc cref="ScaleProperty" />
    public StatScale? Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <inheritdoc cref="MinimumProperty" />
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <inheritdoc cref="MaximumProperty" />
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <inheritdoc cref="PolarityProperty" />
    public StatPolarity Polarity
    {
        get => GetValue(PolarityProperty);
        set => SetValue(PolarityProperty, value);
    }

    /// <inheritdoc cref="NeutralLowProperty" />
    public double? NeutralLow
    {
        get => GetValue(NeutralLowProperty);
        set => SetValue(NeutralLowProperty, value);
    }

    /// <inheritdoc cref="NeutralHighProperty" />
    public double? NeutralHigh
    {
        get => GetValue(NeutralHighProperty);
        set => SetValue(NeutralHighProperty, value);
    }

    /// <inheritdoc cref="StrongSentimentAtProperty" />
    public double StrongSentimentAt
    {
        get => GetValue(StrongSentimentAtProperty);
        set => SetValue(StrongSentimentAtProperty, value);
    }

    /// <inheritdoc cref="PositiveBrushProperty" />
    public IBrush? PositiveBrush
    {
        get => GetValue(PositiveBrushProperty);
        set => SetValue(PositiveBrushProperty, value);
    }

    /// <inheritdoc cref="PositiveSoftBrushProperty" />
    public IBrush? PositiveSoftBrush
    {
        get => GetValue(PositiveSoftBrushProperty);
        set => SetValue(PositiveSoftBrushProperty, value);
    }

    /// <inheritdoc cref="NegativeSoftBrushProperty" />
    public IBrush? NegativeSoftBrush
    {
        get => GetValue(NegativeSoftBrushProperty);
        set => SetValue(NegativeSoftBrushProperty, value);
    }

    /// <inheritdoc cref="NegativeBrushProperty" />
    public IBrush? NegativeBrush
    {
        get => GetValue(NegativeBrushProperty);
        set => SetValue(NegativeBrushProperty, value);
    }

    /// <summary>The scale actually in force: an explicit <see cref="Scale" />, else the loose properties.</summary>
    public StatScale EffectiveScale =>
        Scale ?? new StatScale(Minimum, Maximum, Polarity, NeutralLow, NeutralHigh);

    /// <summary>
    ///     What is shown: the <see cref="Text" /> override, else the formatted <see cref="Value" />.
    ///     Negative zero is folded to positive zero first: a differential column that lands exactly on
    ///     nothing would otherwise print "-0", which reads as a real negative.
    /// </summary>
    public string DisplayText =>
        Text ?? (Value is { } v
            ? (v == 0 ? 0.0 : v).ToString(Format, CultureInfo.InvariantCulture)
            : string.Empty);

    /// <summary>Where <see cref="Value" /> sits on the bar channel, 0..1. Zero when there is no value.</summary>
    public double Fraction => Value is { } v ? EffectiveScale.Fraction(v) : 0;

    /// <summary>The colour channel for <see cref="Value" />, -1..+1. Zero when there is no value.</summary>
    public double Sentiment => Value is { } v ? EffectiveScale.Sentiment(v) : 0;

    /// <summary>
    ///     The accent this control's sentiment resolves to: strong beyond
    ///     <see cref="StrongSentimentAt" />, soft outside the dead zone, and null inside it so the
    ///     inherited <c>Foreground</c> stands.
    /// </summary>
    public IBrush? Accent
    {
        get
        {
            double sentiment = Sentiment;
            double strong = Math.Clamp(StrongSentimentAt, 0.01, 1);
            return sentiment switch
            {
                _ when sentiment >= strong => PositiveBrush ?? PositiveSoftBrush,
                _ when sentiment > 0 => PositiveSoftBrush ?? PositiveBrush,
                _ when sentiment <= -strong => NegativeBrush ?? NegativeSoftBrush,
                _ when sentiment < 0 => NegativeSoftBrush ?? NegativeBrush,
                _ => null
            };
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // These controls draw their own glyphs, so there is no TextBlock for a screen reader to find and
        // the control has to publish its own name. On change rather than in Render, so it is correct even
        // for a control that never paints.
        if (change.Property == ValueProperty || change.Property == TextProperty
                                             || change.Property == FormatProperty)
        {
            AutomationProperties.SetName(this, DisplayText);
        }
    }
}
