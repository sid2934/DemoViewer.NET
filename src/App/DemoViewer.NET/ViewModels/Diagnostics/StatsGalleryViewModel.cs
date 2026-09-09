#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Controls.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>One cell of the live column preview: a fixed value seen through the current scale.</summary>
/// <param name="Value">The raw number, held constant while the scale is dragged around it.</param>
/// <param name="Scale">The scale currently in force.</param>
/// <param name="IsLeader">Whether this cell is the column's best value under the current polarity.</param>
/// <param name="BarAlignment">Which edge the fill grows from, so the trade can be seen both ways.</param>
public sealed record GalleryCell(double Value, StatScale Scale, bool IsLeader,
    StatBarAlignment BarAlignment);

/// <summary>
///     Drives the Diagnostics tab's component gallery: a dev-only surface for the
///     <see cref="Controls.Stats" /> library (docs/ui/stats-components.md). Not a product feature and not
///     on any user path; it rides the existing <c>tab.diagnostics</c> gate.
///     <para>
///         It exists because four of the six controls had no call site in the app, so the only way to
///         look at them was a captured PNG. A PNG cannot answer the questions that actually decide the
///         next phase: where the dead zone should sit, how wide a band reads as "normal", and whether a
///         ramp still separates when you drag one value through it. Those need a slider.
///     </para>
///     <para>
///         The column preview is the point of the whole panel. A single cell tells you a colour; five
///         cells on one scale tell you whether the column READS, which is the only thing that matters on
///         a scoreboard.
///     </para>
/// </summary>
public sealed partial class StatsGalleryViewModel : ObservableObject
{
    /// <summary>The fixed column the preview judges. Shaped like a real HLTV rating spread.</summary>
    private static readonly double[] _columnValues = [1.43, 1.06, 0.91, 0.86, 0.76];

    private static readonly string[] _polarityNames = ["Higher is better", "Lower is better", "Neutral"];

    private static readonly string[] _barAlignmentNames =
        ["Bar: auto (follows text)", "Bar: left", "Bar: centre", "Bar: right"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(ColumnCells), nameof(FractionText),
        nameof(SentimentText), nameof(TierText))]
    private double _value = 1.43;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(ColumnCells), nameof(FractionText),
        nameof(SentimentText), nameof(TierText))]
    private double _minimum = 0.76;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(ColumnCells), nameof(FractionText),
        nameof(SentimentText), nameof(TierText))]
    private double _maximum = 1.43;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(ColumnCells), nameof(FractionText),
        nameof(SentimentText), nameof(TierText))]
    private double _neutralLow = 0.95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(ColumnCells), nameof(FractionText),
        nameof(SentimentText), nameof(TierText))]
    private double _neutralHigh = 1.10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(ColumnCells), nameof(PolarityName),
        nameof(FractionText), nameof(SentimentText), nameof(TierText))]
    private int _polarityIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TierText))]
    private double _strongSentimentAt = 0.5;

    [ObservableProperty]
    private bool _showTrack = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarAlignment), nameof(ColumnCells))]
    private int _barAlignmentIndex;

    [ObservableProperty]
    private bool _tintBar;

    [ObservableProperty]
    private bool _showLeader = true;

    // ── SegmentedBar ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Segments))]
    private double _flashes = 73;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Segments))]
    private double _smokes = 63;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Segments))]
    private double _heGrenades = 70;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Segments))]
    private double _mollies = 55;

    // ── Sparkline ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<double> _series = [0, 2, 1, 3, 0, 1, 4, 2, 0, 1, 2, 3, 1, 0, 2];

    [ObservableProperty]
    private IReadOnlyList<double> _damageSeries =
        [12, 80, 45, 130, 0, 66, 150, 90, 20, 40, 75, 110, 35, 0, 95];

    [ObservableProperty]
    private IReadOnlyList<double> _kastSeries = [1, 1, 0, 1, 0, 0, 1, 1, 1, 0, 1, 1, 0, 1, 1];

    /// <summary>The three polarity choices, for the combo. Static: the list never varies by instance.</summary>
    public static IReadOnlyList<string> PolarityNames => _polarityNames;

    /// <summary>Fill-anchor choices, for the combo.</summary>
    public static IReadOnlyList<string> BarAlignmentNames => _barAlignmentNames;

    /// <summary>The selected fill anchor.</summary>
    public StatBarAlignment BarAlignment => (StatBarAlignment)Math.Clamp(BarAlignmentIndex, 0, 3);

    /// <summary>The selected polarity as the enum the controls take.</summary>
    public StatPolarity Polarity => (StatPolarity)Math.Clamp(PolarityIndex, 0, 2);

    /// <summary>Human-readable polarity, for the readout line.</summary>
    public string PolarityName => _polarityNames[Math.Clamp(PolarityIndex, 0, 2)];

    /// <summary>The scale every previewed control is driven by.</summary>
    public StatScale Scale => new(Minimum, Maximum, Polarity, NeutralLow, NeutralHigh);

    /// <summary>
    ///     The fixed column seen through the current scale. Leader is the best value under the CURRENT
    ///     polarity, so flipping to "lower is better" moves the star, which is the sort of thing that is
    ///     obvious in a gallery and easy to get backwards in a view model.
    /// </summary>
    public IReadOnlyList<GalleryCell> ColumnCells
    {
        get
        {
            StatScale scale = Scale;
            double best = Polarity == StatPolarity.LowerIsBetter
                ? _columnValues.Min()
                : _columnValues.Max();
            return _columnValues
                .Select(v => new GalleryCell(v, scale,
                    Polarity != StatPolarity.Neutral && Math.Abs(v - best) < 1e-9, BarAlignment))
                .ToList();
        }
    }

    /// <summary>The bar channel for the driven value, shown as a number so the maths is visible.</summary>
    public string FractionText =>
        Scale.Fraction(Value).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>The colour channel for the driven value.</summary>
    public string SentimentText =>
        Scale.Sentiment(Value).ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);

    /// <summary>Which accent tier that sentiment lands in, named rather than left to the eye.</summary>
    public string TierText
    {
        get
        {
            double s = Scale.Sentiment(Value);
            double strong = Math.Clamp(StrongSentimentAt, 0.01, 1);
            return s switch
            {
                _ when s >= strong => "StatPositive (strong good)",
                _ when s > 0 => "StatPositiveSoft (mild good)",
                _ when s <= -strong => "StatNegative (strong bad)",
                _ when s < 0 => "StatNegativeSoft (mild bad)",
                _ => "none (neutral, inherits Foreground)"
            };
        }
    }

    /// <summary>The utility split, rebuilt whenever one of the four counts moves.</summary>
    public IReadOnlyList<StatSegment> Segments =>
    [
        new(Flashes, null, "Flashes"),
        new(Smokes, null, "Smokes"),
        new(HeGrenades, null, "HEs"),
        new(Mollies, null, "Molotovs")
    ];

    /// <summary>Resets every driver to the reference board's HLTV rating column.</summary>
    [RelayCommand]
    private void ResetToRatingColumn()
    {
        Minimum = 0.76;
        Maximum = 1.43;
        NeutralLow = 0.95;
        NeutralHigh = 1.10;
        Value = 1.43;
        PolarityIndex = 0;
        StrongSentimentAt = 0.5;
    }

    /// <summary>Switches the drivers to an absolute 0..100 rating, the other shape in the reference.</summary>
    [RelayCommand]
    private void ResetToAbsolute()
    {
        Minimum = 0;
        Maximum = 100;
        NeutralLow = 45;
        NeutralHigh = 65;
        Value = 78;
        PolarityIndex = 0;
        StrongSentimentAt = 0.5;
    }

    /// <summary>
    ///     Fresh random series for the three sparklines. Shape matters more than the numbers when what
    ///     you are judging is whether a strip stays legible, and one fixed series flatters itself.
    /// </summary>
    [RelayCommand]
    private void ShuffleSeries()
    {
        Series = Sample(16, 0, 5);
        DamageSeries = Sample(16, 0, 160);
        KastSeries = Sample(16, 0, 2).Select(v => v > 0.5 ? 1d : 0d).ToList();
    }

    private static List<double> Sample(int count, double min, double max)
    {
        List<double> values = new(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(min + (Random.Shared.NextDouble() * (max - min)));
        }

        return values;
    }
}
