#region

using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The phase intervals are derived from a round's ticks, half-open on the frame clock, with and
///     without a plant, a kill and an end. Every other Strat Room design takes round bounds from here,
///     so each boundary is pinned to the tick it belongs to.
/// </summary>
public class RoundPhasesTests
{
    private static RoundFacts Round(int? opening = 1500, int? plant = 2000, int? end = 3000, bool live = true) => new()
    {
        Number = 1,
        IsLive = live,
        FreezeEndTick = 1000,
        OpeningKillTick = opening,
        PlantTick = plant,
        EndTick = end,
        EndSource = end is null ? RoundEndSource.None : RoundEndSource.WinStatus,
        Ct = new SideFacts
        {
            Side = 3,
            PlayersAtFreezeEnd = 5
        },
        T = new SideFacts
        {
            Side = 2,
            PlayersAtFreezeEnd = 5
        }
    };

    [Test]
    public async Task AFullRound_WalksEveryPhaseAtItsBoundary()
    {
        RoundFacts round = Round();

        using (Assert.Multiple())
        {
            await Assert.That(RoundPhases.At(round, 999)).IsEqualTo(RoundPhase.Freeze);
            await Assert.That(RoundPhases.At(round, 1000)).IsEqualTo(RoundPhase.Opening);
            await Assert.That(RoundPhases.At(round, 1499)).IsEqualTo(RoundPhase.Opening);
            await Assert.That(RoundPhases.At(round, 1500)).IsEqualTo(RoundPhase.MidRound);
            await Assert.That(RoundPhases.At(round, 1999)).IsEqualTo(RoundPhase.MidRound);
            await Assert.That(RoundPhases.At(round, 2000)).IsEqualTo(RoundPhase.PostPlant);
            await Assert.That(RoundPhases.At(round, 2999)).IsEqualTo(RoundPhase.PostPlant);
            await Assert.That(RoundPhases.At(round, 3000)).IsEqualTo(RoundPhase.PostRound);
            await Assert.That(RoundPhases.At(round, 3400)).IsEqualTo(RoundPhase.PostRound);
        }
    }

    [Test]
    public async Task ThePostPlantWindow_IsTheRetakeFromTheCtSide()
    {
        RoundFacts round = Round();

        using (Assert.Multiple())
        {
            await Assert.That(RoundPhases.At(round, 2500, 3)).IsEqualTo(RoundPhase.Retake);
            await Assert.That(RoundPhases.At(round, 2500, 2)).IsEqualTo(RoundPhase.PostPlant);
            await Assert.That(RoundPhases.At(round, 2500)).IsEqualTo(RoundPhase.PostPlant)
                .Because("a consumer that does not say which side gets the T view");
            await Assert.That(RoundPhases.At(round, 1200, 3)).IsEqualTo(RoundPhase.Opening)
                .Because("the side only matters inside the post-plant window");
        }
    }

    [Test]
    public async Task WithoutAPlant_MidRoundRunsToTheEnd()
    {
        RoundFacts round = Round(plant: null);

        using (Assert.Multiple())
        {
            await Assert.That(RoundPhases.At(round, 2500)).IsEqualTo(RoundPhase.MidRound);
            await Assert.That(RoundPhases.At(round, 2999)).IsEqualTo(RoundPhase.MidRound);
            await Assert.That(RoundPhases.At(round, 3000)).IsEqualTo(RoundPhase.PostRound);
        }
    }

    [Test]
    public async Task WithoutAnOpeningKill_OpeningRunsToThePlantOrTheEnd()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RoundPhases.At(Round(opening: null), 1999)).IsEqualTo(RoundPhase.Opening);
            await Assert.That(RoundPhases.At(Round(opening: null), 2000)).IsEqualTo(RoundPhase.PostPlant);
            await Assert.That(RoundPhases.At(Round(opening: null, plant: null), 2999)).IsEqualTo(RoundPhase.Opening);
            await Assert.That(RoundPhases.At(Round(opening: null, plant: null), 3000)).IsEqualTo(RoundPhase.PostRound);
        }
    }

    [Test]
    public async Task WithoutAnEnd_TheLastPhaseRunsToTheLastFrame()
    {
        RoundFacts truncated = Round(end: null);

        using (Assert.Multiple())
        {
            await Assert.That(RoundPhases.At(truncated, 2500)).IsEqualTo(RoundPhase.PostPlant);
            await Assert.That(RoundPhases.At(truncated, 999_999)).IsEqualTo(RoundPhase.PostPlant);
            await Assert.That(RoundPhases.At(Round(plant: null, end: null), 999_999)).IsEqualTo(RoundPhase.MidRound);
        }
    }

    [Test]
    public async Task WarmupAndNonLiveRounds_AreNotPhases()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RoundPhases.At(null, 500)).IsEqualTo(RoundPhase.Warmup)
                .Because("before the first freeze end there is no round to ask");
            await Assert.That(RoundPhases.At(Round(live: false), 2500)).IsEqualTo(RoundPhase.None);
        }
    }

    [Test]
    public async Task AliveAt_StartsAtTheFreezeEndCountAndFollowsTheKills()
    {
        RoundFacts round = Round();
        round.Kills =
        [
            new KillStep
            {
                Tick = 1500,
                VictimSide = 3,
                CtAlive = 4,
                TAlive = 5
            },
            new KillStep
            {
                Tick = 1800,
                VictimSide = 2,
                CtAlive = 4,
                TAlive = 4
            },
            new KillStep
            {
                Tick = 1800,
                VictimSide = 2,
                CtAlive = 4,
                TAlive = 3
            }
        ];

        using (Assert.Multiple())
        {
            await Assert.That(RoundPhases.AliveAt(round, 1000)).IsEqualTo((5, 5));
            await Assert.That(RoundPhases.AliveAt(round, 1499)).IsEqualTo((5, 5));
            await Assert.That(RoundPhases.AliveAt(round, 1500)).IsEqualTo((4, 5))
                .Because("a kill counts from its own tick");
            await Assert.That(RoundPhases.AliveAt(round, 1800)).IsEqualTo((4, 3))
                .Because("two kills on one tick both count");
            await Assert.That(RoundPhases.AliveAt(round, 9000)).IsEqualTo((4, 3));
        }
    }
}
