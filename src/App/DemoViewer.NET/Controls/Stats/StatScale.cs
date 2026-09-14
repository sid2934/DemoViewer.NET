namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     Which direction is good for a stat. Flips the sign of <see cref="StatScale.Sentiment" /> without
///     touching <see cref="StatScale.Fraction" />: a bar always shows raw magnitude, only the colour
///     knows whether that magnitude is welcome.
/// </summary>
public enum StatPolarity
{
    /// <summary>More is better: kills, ADR, rating.</summary>
    HigherIsBetter,

    /// <summary>Less is better: deaths, team damage, opening deaths.</summary>
    LowerIsBetter,

    /// <summary>Neither: the value is descriptive, so <see cref="StatScale.Sentiment" /> is always 0.</summary>
    Neutral
}

/// <summary>
///     How one stat column maps a raw value onto the two channels a stat cell draws: a bar length and a
///     colour sentiment. Pure maths with no Avalonia reference, so it is unit-testable without a render
///     harness (see <c>StatScaleTests</c>).
///     <para>
///         <b>The two channels are deliberately independent.</b> <see cref="Min" />/<see cref="Max" />
///         drive the bar; <see cref="NeutralLow" />/<see cref="NeutralHigh" /> carve a dead zone out of
///         the colour only. That separation is required to reproduce the reference design, where a
///         rating column's bar is relative to the other players on screen while its colour is banded
///         around a fixed pivot. Setting only <see cref="Min" />/<see cref="Max" /> gives the simple
///         case: bar and colour both ramp across the whole domain.
///     </para>
///     <para>
///         <b><see cref="Fraction" /> is never polarity-inverted.</b> A "deaths" column draws its
///         longest bar for the most deaths, because a big number with a short bar reads as broken. The
///         judgement is carried entirely by <see cref="Sentiment" />.
///     </para>
/// </summary>
/// <param name="Min">Low end of the bar domain. Values at or below it draw an empty bar.</param>
/// <param name="Max">High end of the bar domain. Values at or above it draw a full bar.</param>
/// <param name="Polarity">Which direction <see cref="Sentiment" /> treats as good.</param>
/// <param name="NeutralLow">
///     Lower edge of the uncoloured dead zone, in raw units. Null pairs with
///     <paramref name="NeutralHigh" /> to put a zero-width dead zone at the domain midpoint.
/// </param>
/// <param name="NeutralHigh">Upper edge of the uncoloured dead zone, in raw units.</param>
/// <param name="ColourMin">
///     Low end of the COLOUR extent. Null falls back to <paramref name="Min" />. Set it when the bar is
///     peer-relative but the colour must not be: without it, sentiment ramps between the peer bounds and
///     the same value reads "strong" in a weak lobby and "mild" in a strong one.
/// </param>
/// <param name="ColourMax">High end of the colour extent. Null falls back to <paramref name="Max" />.</param>
public sealed record StatScale(
    double Min,
    double Max,
    StatPolarity Polarity = StatPolarity.HigherIsBetter,
    double? NeutralLow = null,
    double? NeutralHigh = null,
    double? ColourMin = null,
    double? ColourMax = null)
{
    /// <summary>True when the BAR domain is usable: finite and non-degenerate.</summary>
    public bool HasDomain => double.IsFinite(Min) && double.IsFinite(Max) && Max > Min;

    /// <summary>Low end of the colour extent, defaulting to the bar's.</summary>
    public double EffectiveColourMin => ColourMin ?? Min;

    /// <summary>High end of the colour extent, defaulting to the bar's.</summary>
    public double EffectiveColourMax => ColourMax ?? Max;

    /// <summary>
    ///     True when the COLOUR extent is usable. Checked separately from <see cref="HasDomain" /> on
    ///     purpose: a column where every player scored the same has no bar domain, but if its colour
    ///     extent is absolute it still knows whether that shared value is good.
    /// </summary>
    public bool HasColourDomain =>
        double.IsFinite(EffectiveColourMin) && double.IsFinite(EffectiveColourMax)
                                            && EffectiveColourMax > EffectiveColourMin;

    /// <summary>
    ///     The bar channel: where <paramref name="value" /> sits in <see cref="Min" />..<see cref="Max" />,
    ///     clamped to 0..1. A degenerate domain (every peer equal, or a non-finite bound) returns 0 rather
    ///     than 1, because a table where every bar is full says nothing and reads as a rendering fault.
    /// </summary>
    public double Fraction(double value)
    {
        if (!HasDomain || !double.IsFinite(value))
        {
            return 0;
        }

        return Math.Clamp((value - Min) / (Max - Min), 0, 1);
    }

    /// <summary>
    ///     The colour channel: -1 (worst) through 0 (neutral, draw no tint) to +1 (best), with the sign
    ///     already flipped for <see cref="StatPolarity.LowerIsBetter" />. Values inside the dead zone
    ///     return exactly 0.
    /// </summary>
    public double Sentiment(double value)
    {
        if (Polarity == StatPolarity.Neutral || !HasColourDomain || !double.IsFinite(value))
        {
            return 0;
        }

        // Measured against the COLOUR extent, which is the bar's unless the caller separated them.
        double cMin = EffectiveColourMin;
        double cMax = EffectiveColourMax;

        // An unspecified dead zone collapses to the extent's midpoint, which makes the default behaviour
        // a plain linear ramp from -1 at one end to +1 at the other. An edge given on only one side
        // mirrors to the other, so a caller can band just the top or just the bottom of a column.
        double mid = cMin + ((cMax - cMin) / 2);
        double low = NeutralLow ?? NeutralHigh ?? mid;
        double high = NeutralHigh ?? NeutralLow ?? mid;
        if (high < low)
        {
            (low, high) = (high, low);
        }

        double raw;
        if (value < low)
        {
            double span = low - cMin;
            raw = span > 0 ? -Math.Clamp((low - value) / span, 0, 1) : -1;
        }
        else if (value > high)
        {
            double span = cMax - high;
            raw = span > 0 ? Math.Clamp((value - high) / span, 0, 1) : 1;
        }
        else
        {
            return 0;
        }

        return Polarity == StatPolarity.LowerIsBetter ? -raw : raw;
    }

    /// <summary>
    ///     A fixed domain, for a metric whose bounds are a property of the metric rather than of the
    ///     lobby (a 0..100 rating, a percentage).
    /// </summary>
    public static StatScale Absolute(double min, double max, double? neutralLow = null,
        double? neutralHigh = null, StatPolarity polarity = StatPolarity.HigherIsBetter) =>
        new(min, max, polarity, neutralLow, neutralHigh);

    /// <summary>
    ///     A domain taken from the values actually on screen, so a bar reads as "how this player compares
    ///     to the others in this lobby". Returns null for an empty sequence or one with no finite value:
    ///     the caller then draws no bar, which is the honest result when there is nothing to compare
    ///     against.
    /// </summary>
    public static StatScale? FromPeers(IEnumerable<double> values,
        StatPolarity polarity = StatPolarity.HigherIsBetter,
        double? neutralLow = null, double? neutralHigh = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (double v in values)
        {
            if (!double.IsFinite(v))
            {
                continue;
            }

            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        return double.IsFinite(min) && double.IsFinite(max)
            ? new StatScale(min, max, polarity, neutralLow, neutralHigh)
            : null;
    }

    /// <summary>
    ///     A peer-relative bar with a colour band pinned to fixed thresholds: the shape an HLTV-style
    ///     rating wants, where the bar compares players but "good" means above 1.00 regardless of who
    ///     else is in the server.
    ///     <para>
    ///         The colour EXTENT still follows the bar here. Use <see cref="Hybrid" /> when the tint's
    ///         strength must also be lobby-independent.
    ///     </para>
    /// </summary>
    public static StatScale Banded(double min, double max, double neutralLow, double neutralHigh,
        StatPolarity polarity = StatPolarity.HigherIsBetter) =>
        new(min, max, polarity, neutralLow, neutralHigh);

    /// <summary>
    ///     The fully separated shape: bar from the peers on screen, colour entirely from fixed
    ///     benchmarks. This is what "the bar says who topped this server, the colour says whether that is
    ///     good" actually requires, and the two channels share nothing.
    /// </summary>
    /// <param name="peers">The values on screen, for the bar domain. An unusable set means no bar.</param>
    /// <param name="colourMin">Value at which the tint reaches full bad.</param>
    /// <param name="colourMax">Value at which the tint reaches full good.</param>
    /// <param name="neutralLow">Lower edge of the untinted band.</param>
    /// <param name="neutralHigh">Upper edge of the untinted band.</param>
    /// <param name="polarity">Which direction is good.</param>
    public static StatScale Hybrid(IEnumerable<double> peers, double colourMin, double colourMax,
        double? neutralLow = null, double? neutralHigh = null,
        StatPolarity polarity = StatPolarity.HigherIsBetter)
    {
        StatScale? bar = FromPeers(peers, polarity);

        // No usable peer spread still leaves a fully-formed colour scale: every player scoring the same
        // is exactly when an absolute benchmark earns its keep. Min == Max suppresses the bar on its own.
        return new StatScale(bar?.Min ?? 0, bar?.Max ?? 0, polarity, neutralLow, neutralHigh,
            colourMin, colourMax);
    }

}
