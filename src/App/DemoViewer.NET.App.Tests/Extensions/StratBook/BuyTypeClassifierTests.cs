#region

using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The buy-type rule over its inputs: HLTV's bands at the shipped defaults, scaled per player, the
///     two pistol rounds at MR12 and MR15, never a pistol round in overtime, and Force only after a lost
///     round with a money read the guard trusts.
/// </summary>
public class BuyTypeClassifierTests
{
    private const int T = 2;
    private const int Ct = 3;

    [Test]
    [Arguments(Ct, 5, 0, null, false, false, 1, BuyType.Pistol, "round 1 is a pistol round whatever was bought")]
    [Arguments(T, 5, 21000, null, false, false, 1, BuyType.Pistol, "round 1 stays pistol even at full-buy equipment")]
    [Arguments(Ct, 5, 5000, null, false, false, 13, BuyType.Pistol, "round 13 opens the second half at MR12")]
    [Arguments(Ct, 5, 5000, null, false, false, 2, BuyType.Eco, "5,000 at five players is HLTV's full-eco ceiling")]
    [Arguments(Ct, 5, 5001, null, false, false, 2, BuyType.Semi, "one dollar over the eco ceiling is semi")]
    [Arguments(Ct, 5, 20000, null, false, false, 2, BuyType.Full, "20,000 at five players is HLTV's full-buy floor")]
    [Arguments(Ct, 5, 19999, null, false, false, 2, BuyType.Semi, "one dollar under the full floor is semi")]
    [Arguments(T, 5, 20000, null, false, false, 2, BuyType.Full, "the T floor defaults to the CT floor")]
    [Arguments(T, 5, 13550, null, false, false, 2, BuyType.Semi, "Dust2 round 2 T: 13,550 is semi")]
    [Arguments(Ct, 5, 4700, null, false, true, 2, BuyType.Eco, "Dust2 round 2 CT: 4,700 after a loss is eco, never force")]
    [Arguments(T, 5, 21350, null, false, false, 3, BuyType.Full, "Dust2 round 3 T: 21,350 is full")]
    [Arguments(T, 5, 5000, null, false, false, 5, BuyType.Eco, "Dust2 round 5 T: exactly the eco ceiling")]
    [Arguments(T, 5, 22550, null, false, false, 8, BuyType.Full, "Nuke round 8 T: 22,550 is full")]
    [Arguments(Ct, 5, 10000, 1999, true, true, 4, BuyType.Force, "lost, reliable, under 400 per player: force")]
    [Arguments(Ct, 5, 10000, 2000, true, true, 4, BuyType.Semi, "exactly 400 per player is not force")]
    [Arguments(Ct, 5, 10000, 1999, false, true, 4, BuyType.Semi, "unreliable money is never force")]
    [Arguments(Ct, 5, 10000, 1999, true, false, 4, BuyType.Semi, "a won round is never force")]
    [Arguments(Ct, 5, 10000, null, true, true, 4, BuyType.Semi, "no money read is never force")]
    [Arguments(Ct, 1, 1000, null, false, false, 4, BuyType.Eco, "one player: 1,000 is eco")]
    [Arguments(Ct, 1, 4000, null, false, false, 4, BuyType.Full, "one player: 4,000 is full")]
    [Arguments(T, 4, 16000, null, false, false, 4, BuyType.Full, "four players: 16,000 is full")]
    [Arguments(T, 4, 4000, null, false, false, 4, BuyType.Eco, "four players: 4,000 is eco")]
    [Arguments(Ct, 5, 0, null, false, false, 25, BuyType.Eco, "overtime round 25 is never pistol")]
    [Arguments(Ct, 5, 20000, null, false, false, 25, BuyType.Full, "overtime classifies by equipment")]
    [Arguments(Ct, 5, 0, null, false, false, null, BuyType.Eco, "no match round: no pistol, plain equipment rule")]
    [Arguments(Ct, 0, 0, null, false, false, 2, BuyType.Unknown, "no players on the side: unknown")]
    public async Task Classify_AtTheDefaults(int side, int players, int equipment, int? money, bool reliable,
        bool lostPrevious, int? matchRound, BuyType expected, string because)
    {
        BuyType actual = BuyTypeClassifier.Classify(side, players, equipment, money, reliable, lostPrevious,
            matchRound, BuyThresholds.Default);

        await Assert.That(actual).IsEqualTo(expected).Because(because);
    }

    [Test]
    [Arguments(24, 1, true)]
    [Arguments(24, 13, true)]
    [Arguments(24, 12, false)]
    [Arguments(24, 14, false)]
    [Arguments(24, 25, false)]
    [Arguments(30, 1, true)]
    [Arguments(30, 16, true)]
    [Arguments(30, 13, false)]
    [Arguments(30, 31, false)]
    public async Task IsPistolRound_FollowsTheRegulationLength(int regulation, int round, bool expected) =>
        await Assert.That(BuyTypeClassifier.IsPistolRound(round, regulation)).IsEqualTo(expected);

    [Test]
    public async Task TheCtTSplit_IsTwoParametersThatDefaultToOneValue()
    {
        BuyThresholds csDemoManager = BuyThresholds.Default with
        {
            FullMinPerPlayerCt = 4500
        };

        using (Assert.Multiple())
        {
            // Dust2 round 3 CT at 19,350: semi either way.
            await Assert.That(BuyTypeClassifier.Classify(Ct, 5, 19350, null, false, false, 3, BuyThresholds.Default))
                .IsEqualTo(BuyType.Semi);
            await Assert.That(BuyTypeClassifier.Classify(Ct, 5, 19350, null, false, false, 3, csDemoManager))
                .IsEqualTo(BuyType.Semi);
            // Between 20,000 and 22,500 the two rules differ for CT only.
            await Assert.That(BuyTypeClassifier.Classify(Ct, 5, 21000, null, false, false, 3, BuyThresholds.Default))
                .IsEqualTo(BuyType.Full);
            await Assert.That(BuyTypeClassifier.Classify(Ct, 5, 21000, null, false, false, 3, csDemoManager))
                .IsEqualTo(BuyType.Semi);
            await Assert.That(BuyTypeClassifier.Classify(T, 5, 21000, null, false, false, 3, csDemoManager))
                .IsEqualTo(BuyType.Full);
        }
    }

    [Test]
    public async Task HalfOf_IsDefinedFromTheMatchRound_NotObserved()
    {
        using (Assert.Multiple())
        {
            await Assert.That(BuyTypeClassifier.HalfOf(1, 24)).IsEqualTo(RoundHalf.First);
            await Assert.That(BuyTypeClassifier.HalfOf(12, 24)).IsEqualTo(RoundHalf.First);
            await Assert.That(BuyTypeClassifier.HalfOf(13, 24)).IsEqualTo(RoundHalf.Second);
            await Assert.That(BuyTypeClassifier.HalfOf(24, 24)).IsEqualTo(RoundHalf.Second);
            await Assert.That(BuyTypeClassifier.HalfOf(25, 24)).IsEqualTo(RoundHalf.Overtime);
            await Assert.That(BuyTypeClassifier.HalfOf(16, 30)).IsEqualTo(RoundHalf.Second);
            await Assert.That(BuyTypeClassifier.HalfOf(null, 24)).IsEqualTo(RoundHalf.Unknown);
        }
    }

    [Test]
    public async Task TheDefaults_AreHltvsBandsAtFivePlayers()
    {
        BuyThresholds defaults = BuyThresholds.Default;

        using (Assert.Multiple())
        {
            await Assert.That(defaults.EcoMaxPerPlayer * 5).IsEqualTo(5000).Because("HLTV full eco: 0-5k");
            await Assert.That(defaults.FullMinPerPlayerCt * 5).IsEqualTo(20000).Because("HLTV full buy: 20k+");
            await Assert.That(defaults.FullMinPerPlayerT).IsEqualTo(defaults.FullMinPerPlayerCt);
            await Assert.That(defaults.ForceMoneyMaxPerPlayer).IsEqualTo(400);
            await Assert.That(defaults.MoneySaneMax).IsEqualTo(16000);
            await Assert.That(defaults.RegulationRounds).IsEqualTo(24);
        }
    }
}
