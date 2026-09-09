#region

using DemoViewer.NET.Controls.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The stats component library's arithmetic. <see cref="StatScale" /> has no Avalonia reference on
///     purpose, so the part that decides whether a number reads as good or bad is testable without a
///     render harness, and every degenerate input has a pinned answer rather than an emergent one.
/// </summary>
public class StatScaleTests
{
    // ── Fraction: the bar channel ─────────────────────────────────────────────

    [Test]
    public async Task Fraction_MapsAcrossDomain_AndClampsOutside()
    {
        StatScale scale = new(0, 100);

        await Assert.That(scale.Fraction(0)).IsEqualTo(0);
        await Assert.That(scale.Fraction(50)).IsEqualTo(0.5);
        await Assert.That(scale.Fraction(100)).IsEqualTo(1);
        await Assert.That(scale.Fraction(-40)).IsEqualTo(0);
        await Assert.That(scale.Fraction(400)).IsEqualTo(1);
    }

    /// <summary>
    ///     Every peer equal is the single-row case and the all-tied case. Returning 1 would fill every bar
    ///     in the column, which says nothing and reads as a rendering fault; 0 says "nothing to compare".
    /// </summary>
    [Test]
    public async Task Fraction_DegenerateDomain_IsEmptyNotFull()
    {
        await Assert.That(new StatScale(5, 5).Fraction(5)).IsEqualTo(0);
        await Assert.That(new StatScale(10, 2).Fraction(6)).IsEqualTo(0);
        await Assert.That(new StatScale(double.NaN, 10).Fraction(5)).IsEqualTo(0);
        await Assert.That(new StatScale(0, double.PositiveInfinity).Fraction(5)).IsEqualTo(0);
    }

    [Test]
    public async Task Fraction_NonFiniteValue_IsZero()
    {
        StatScale scale = new(0, 100);

        await Assert.That(scale.Fraction(double.NaN)).IsEqualTo(0);
        await Assert.That(scale.Fraction(double.PositiveInfinity)).IsEqualTo(0);
    }

    /// <summary>
    ///     The load-bearing asymmetry: polarity moves the colour and leaves the bar alone. A deaths column
    ///     draws its longest bar for the most deaths; only the tint says that is bad.
    /// </summary>
    [Test]
    public async Task Fraction_IsNeverInvertedByPolarity()
    {
        StatScale higher = new(0, 10);
        StatScale lower = new(0, 10, StatPolarity.LowerIsBetter);

        await Assert.That(lower.Fraction(8)).IsEqualTo(higher.Fraction(8));
        await Assert.That(lower.Fraction(8)).IsEqualTo(0.8);
    }

    // ── Sentiment: the colour channel ─────────────────────────────────────────

    /// <summary>With no band declared the dead zone collapses to the midpoint, giving a plain ramp.</summary>
    [Test]
    public async Task Sentiment_NoNeutralBand_RampsFromWorstToBest()
    {
        StatScale scale = new(0, 100);

        await Assert.That(scale.Sentiment(0)).IsEqualTo(-1);
        await Assert.That(scale.Sentiment(50)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(100)).IsEqualTo(1);
        await Assert.That(scale.Sentiment(75)).IsEqualTo(0.5);
        await Assert.That(scale.Sentiment(25)).IsEqualTo(-0.5);
    }

    [Test]
    public async Task Sentiment_InsideDeadZone_IsExactlyZero()
    {
        StatScale scale = new(0, 2, StatPolarity.HigherIsBetter, 0.95, 1.10);

        await Assert.That(scale.Sentiment(0.95)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(1.00)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(1.10)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(1.11)).IsGreaterThan(0);
        await Assert.That(scale.Sentiment(0.94)).IsLessThan(0);
    }

    [Test]
    public async Task Sentiment_LowerIsBetter_FlipsTheSign()
    {
        StatScale scale = new(0, 20, StatPolarity.LowerIsBetter);

        await Assert.That(scale.Sentiment(0)).IsEqualTo(1);
        await Assert.That(scale.Sentiment(20)).IsEqualTo(-1);
        await Assert.That(scale.Sentiment(10)).IsEqualTo(0);
    }

    [Test]
    public async Task Sentiment_NeutralPolarity_IsAlwaysZero()
    {
        StatScale scale = new(0, 100, StatPolarity.Neutral);

        await Assert.That(scale.Sentiment(0)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(100)).IsEqualTo(0);
    }

    /// <summary>A band given on one side only mirrors, so a caller can tint just the top of a column.</summary>
    [Test]
    public async Task Sentiment_OneSidedBand_MirrorsToTheOtherEdge()
    {
        StatScale high = new(0, 100, StatPolarity.HigherIsBetter, NeutralHigh: 80);

        await Assert.That(high.Sentiment(80)).IsEqualTo(0);
        await Assert.That(high.Sentiment(90)).IsEqualTo(0.5);
        await Assert.That(high.Sentiment(79)).IsLessThan(0);
    }

    /// <summary>
    ///     A band that sits on the domain edge leaves no room to ramp. Anything past it is fully bad or
    ///     fully good rather than a divide by zero.
    /// </summary>
    [Test]
    public async Task Sentiment_BandOnDomainEdge_SaturatesInsteadOfDividingByZero()
    {
        // A penalty column: anything above zero is bad, so the band sits on the domain's own edge.
        StatScale penalty = StatScale.Banded(0, 50, 0, 0, StatPolarity.LowerIsBetter);

        await Assert.That(penalty.Sentiment(0)).IsEqualTo(0);
        await Assert.That(penalty.Sentiment(50)).IsEqualTo(-1);
        await Assert.That(double.IsNaN(penalty.Sentiment(25))).IsFalse();
        await Assert.That(penalty.Sentiment(25)).IsEqualTo(-0.5);
    }

    [Test]
    public async Task Sentiment_DegenerateDomain_IsZero()
    {
        await Assert.That(new StatScale(7, 7).Sentiment(7)).IsEqualTo(0);
        await Assert.That(new StatScale(0, 10).Sentiment(double.NaN)).IsEqualTo(0);
    }

    /// <summary>Inverted band edges are a caller slip, not a reason to produce nonsense.</summary>
    [Test]
    public async Task Sentiment_SwappedBandEdges_AreNormalised()
    {
        StatScale swapped = new(0, 100, StatPolarity.HigherIsBetter, 70, 30);

        await Assert.That(swapped.Sentiment(50)).IsEqualTo(0);
        await Assert.That(swapped.Sentiment(20)).IsLessThan(0);
        await Assert.That(swapped.Sentiment(90)).IsGreaterThan(0);
    }

    // ── Factories ─────────────────────────────────────────────────────────────

    [Test]
    public async Task FromPeers_TakesTheObservedRange()
    {
        StatScale? scale = StatScale.FromPeers([0.76, 1.43, 1.06, 0.91]);

        await Assert.That(scale).IsNotNull();
        await Assert.That(scale!.Min).IsEqualTo(0.76);
        await Assert.That(scale.Max).IsEqualTo(1.43);
        await Assert.That(scale.Fraction(1.43)).IsEqualTo(1);
    }

    /// <summary>Nothing to compare against is a null scale, so the caller draws no bar at all.</summary>
    [Test]
    public async Task FromPeers_WithNothingUsable_IsNull()
    {
        await Assert.That(StatScale.FromPeers([])).IsNull();
        await Assert.That(StatScale.FromPeers([double.NaN, double.PositiveInfinity])).IsNull();
    }

    [Test]
    public async Task FromPeers_SkipsNonFiniteEntries()
    {
        StatScale? scale = StatScale.FromPeers([double.NaN, 3, 9, double.NegativeInfinity]);

        await Assert.That(scale).IsNotNull();
        await Assert.That(scale!.Min).IsEqualTo(3);
        await Assert.That(scale.Max).IsEqualTo(9);
    }

    /// <summary>
    ///     The reference board's rating column: the bar compares the ten players on screen while the colour
    ///     is pinned to the 1.00 pivot, so a lobby of bad players does not turn its best member green.
    /// </summary>
    [Test]
    public async Task Banded_SeparatesTheBarDomainFromTheColourPivot()
    {
        StatScale scale = StatScale.Banded(0.41, 0.90, 0.95, 1.10);

        await Assert.That(scale.Fraction(0.90)).IsEqualTo(1);
        await Assert.That(scale.Sentiment(0.90)).IsLessThan(0);
    }

    /// <summary>A rating delta: tinted by direction, with small movements either way left alone.</summary>
    [Test]
    public async Task Banded_AroundZero_LeavesSmallMovementsAlone()
    {
        StatScale scale = StatScale.Banded(-12, 12, -0.5, 0.5);

        await Assert.That(scale.Sentiment(0)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(0.4)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(-0.4)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(12)).IsEqualTo(1);
        await Assert.That(scale.Sentiment(-12)).IsEqualTo(-1);
    }

    // ── Separated colour extent (the hybrid shape) ───────────────────────────

    /// <summary>
    ///     The whole reason the colour extent exists. Two lobbies, same ADR, different spreads: with the
    ///     extent tied to the peers, 92 would read strong in the weak lobby and mild in the strong one.
    ///     An absolute benchmark that moves with the lobby is not a benchmark.
    /// </summary>
    [Test]
    public async Task Hybrid_ColourIsIndependentOfThePeerSpread()
    {
        StatScale weakLobby = StatScale.Hybrid([57, 70, 80, 87, 92], 0, 120, 70, 82);
        StatScale strongLobby = StatScale.Hybrid([80, 92, 104, 112, 120], 0, 120, 70, 82);

        await Assert.That(weakLobby.Sentiment(92)).IsEqualTo(strongLobby.Sentiment(92));

        // ...while the BAR still says who topped that particular server.
        await Assert.That(weakLobby.Fraction(92)).IsEqualTo(1);
        await Assert.That(strongLobby.Fraction(92)).IsLessThan(0.5);
    }

    /// <summary>
    ///     Every player scoring the same kills the bar domain but not the judgement. This is exactly when
    ///     an absolute benchmark earns its keep, so sentiment must survive a collapsed peer spread.
    /// </summary>
    [Test]
    public async Task Hybrid_ColourSurvivesACollapsedPeerSpread()
    {
        StatScale scale = StatScale.Hybrid([80, 80, 80, 80, 80], 0, 120, 70, 82);

        await Assert.That(scale.HasDomain).IsFalse();
        await Assert.That(scale.Fraction(80)).IsEqualTo(0);
        await Assert.That(scale.HasColourDomain).IsTrue();
        await Assert.That(scale.Sentiment(95)).IsGreaterThan(0);
        await Assert.That(scale.Sentiment(50)).IsLessThan(0);
    }

    [Test]
    public async Task Hybrid_WithNoUsablePeers_StillColours()
    {
        StatScale scale = StatScale.Hybrid([], 0, 120, 70, 82);

        await Assert.That(scale.Fraction(90)).IsEqualTo(0);
        await Assert.That(scale.Sentiment(110)).IsGreaterThan(0);
    }

    /// <summary>An unset colour extent must behave exactly as before, or every existing scale shifts.</summary>
    [Test]
    public async Task ColourExtent_DefaultsToTheBarDomain()
    {
        StatScale plain = new(0, 100);

        await Assert.That(plain.EffectiveColourMin).IsEqualTo(0);
        await Assert.That(plain.EffectiveColourMax).IsEqualTo(100);
        await Assert.That(plain.Sentiment(75)).IsEqualTo(0.5);
    }

    [Test]
    public async Task Absolute_KeepsTheDeclaredDomainWhateverTheValue()
    {
        StatScale scale = StatScale.Absolute(0, 100, 45, 65);

        await Assert.That(scale.Min).IsEqualTo(0);
        await Assert.That(scale.Max).IsEqualTo(100);
        await Assert.That(scale.Sentiment(55)).IsEqualTo(0);
        await Assert.That(scale.Fraction(78)).IsEqualTo(0.78);
    }

    [Test]
    public async Task HasDomain_RejectsNonFiniteAndCollapsedRanges()
    {
        await Assert.That(new StatScale(0, 1).HasDomain).IsTrue();
        await Assert.That(new StatScale(1, 1).HasDomain).IsFalse();
        await Assert.That(new StatScale(2, 1).HasDomain).IsFalse();
        await Assert.That(new StatScale(double.NaN, 1).HasDomain).IsFalse();
    }
}
