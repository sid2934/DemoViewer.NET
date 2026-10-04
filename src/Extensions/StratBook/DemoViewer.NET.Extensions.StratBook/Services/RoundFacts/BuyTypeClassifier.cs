namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The buy-type rule, over the inputs the <c>round_facts</c> ruleset captures at freeze end. The
///     ruleset computes the same class in YAML; this is the app's copy for rows that predate the
///     <c>buy_type</c> column, for re-bucketing without a re-run, and for the classifier tests.
///     <para>
///         Pistol, Eco and Full never read money. Force does, which is why the reliability guard exists:
///         a side whose money read is implausible classifies as Semi and says so.
///     </para>
/// </summary>
public static class BuyTypeClassifier
{
    /// <summary>Classifies one side of one round.</summary>
    /// <param name="side">2 = T, 3 = CT; selects the full-buy floor.</param>
    /// <param name="players">n: players on the side at freeze end.</param>
    /// <param name="equipment">E: the side's equipment value at freeze end.</param>
    /// <param name="money">M: the side's account sum at the freeze-end sample, or null when absent.</param>
    /// <param name="moneyReliable">Whether every account behind <paramref name="money" /> is plausible.</param>
    /// <param name="lostPreviousRound">Whether this side lost the previous live round.</param>
    /// <param name="matchRound">The match round number (1-based), or null when the engine did not say.</param>
    /// <param name="thresholds">The parameter values in force.</param>
    public static BuyType Classify(
        int side,
        int players,
        int equipment,
        int? money,
        bool moneyReliable,
        bool lostPreviousRound,
        int? matchRound,
        BuyThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        if (players <= 0)
        {
            return BuyType.Unknown;
        }

        if (IsPistolRound(matchRound, thresholds.RegulationRounds))
        {
            return BuyType.Pistol;
        }

        if (equipment <= thresholds.EcoMaxPerPlayer * players)
        {
            return BuyType.Eco;
        }

        if (equipment >= thresholds.FullMinPerPlayer(side) * players)
        {
            return BuyType.Full;
        }

        if (lostPreviousRound
            && moneyReliable
            && money is int m
            && m < thresholds.ForceMoneyMaxPerPlayer * players)
        {
            return BuyType.Force;
        }

        return BuyType.Semi;
    }

    /// <summary>
    ///     The first round of each regulation half. Overtime starts at $10,000 and classifies by
    ///     equipment like any other round, so nothing past regulation is ever a pistol round.
    /// </summary>
    public static bool IsPistolRound(int? matchRound, int regulationRounds)
    {
        if (matchRound is not int round || round > regulationRounds)
        {
            return false;
        }

        return round == 1 || round == regulationRounds / 2 + 1;
    }

    /// <summary>The half a match round falls in under the regulation length in force.</summary>
    public static RoundHalf HalfOf(int? matchRound, int regulationRounds)
    {
        if (matchRound is not int round || round < 1)
        {
            return RoundHalf.Unknown;
        }

        if (round <= regulationRounds / 2)
        {
            return RoundHalf.First;
        }

        return round <= regulationRounds ? RoundHalf.Second : RoundHalf.Overtime;
    }
}
