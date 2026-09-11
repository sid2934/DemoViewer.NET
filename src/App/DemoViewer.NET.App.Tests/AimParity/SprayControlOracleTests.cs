#region

using CS2DemoKit.Analysis.Edges;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     Pins for <see cref="SprayControlOracle" />: every branch of the spray-segmentation and
///     residual rule, driven by hand-built shot sequences so each case states one behaviour and its
///     expected numbers.
///     <para>
///         These are the assertions that make the oracle worth being an oracle. Spray control has no
///         external reference at all, so the only thing standing between the shipped Spray column and
///         a number nobody checked is a second implementation plus a set of cases that pin what it
///         should do.
///         Written from the rule as specified, not from the engine's code path.
///     </para>
///     <para>
///         Needs no demo and boots nothing, so these run in the fast tier and execute on a bare
///         checkout.
///     </para>
/// </summary>
public class SprayControlOracleTests
{
    private const int Shooter = 3;

    /// <summary>
    ///     The oracle deliberately copies five engine constants rather than importing them, because
    ///     an oracle that reads the value it is checking cannot catch a change to it. Copies drift,
    ///     so the drift is what gets asserted.
    /// </summary>
    [Test]
    public async Task OracleConstants_MatchTheEngine()
    {
        List<string> drift = [];
        Compare(drift, "recoil scale", SprayControlOracle.WeaponRecoilScale, AimShotContextEdge.WeaponRecoilScale);
        Compare(drift, "max plausible punch",
            SprayControlOracle.MaxPlausibleAimPunchDegrees, AimShotContextEdge.MaxPlausibleAimPunchDegrees);
        Compare(drift, "cycle tolerance",
            SprayControlOracle.SprayCycleTolerance, AimShotContextEdge.SprayCycleTolerance);
        Compare(drift, "open gap ticks",
            SprayControlOracle.SprayOpenGapTicks, AimShotContextEdge.SprayOpenGapTicks);
        Compare(drift, "recoil epsilon",
            SprayControlOracle.SprayRecoilEpsilon, AimShotContextEdge.SprayRecoilEpsilon);
        Compare(drift, "first-bullet epsilon",
            SprayControlOracle.FirstBulletRecoilEpsilon, AimShotContextEdge.FirstBulletRecoilEpsilon);

        drift.ForEach(Console.WriteLine);
        await Assert.That(drift).IsEmpty();
    }

    /// <summary>
    ///     A continuous burst is one run. The anchor carries no residual, so a three-shot spray has
    ///     a population of two, and the residual is the deviation from the FIRST shot rather than
    ///     from the previous one.
    /// </summary>
    [Test]
    public async Task ContinuousBurst_IsOneRun_WithTheAnchorOutsideThePopulation()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: -1f),
            Shot(108, recoil: 1f, pitch: -3f),
            Shot(116, recoil: 2f, pitch: -5f));

        using (Assert.Multiple())
        {
            await Assert.That(result.Shots).IsEqualTo(3);
            await Assert.That(result.Runs).IsEqualTo(1);
            await Assert.That(result.MeasuredShots).IsEqualTo(2);

            // 2 degrees then 4, both measured against the anchor at -1, so the mean is 3.
            await Assert.That(result.MeanPitchError).IsEqualTo(3.0).Within(1e-9);
            await Assert.That(result.MeanYawError).IsEqualTo(0.0).Within(1e-9);
        }
    }

    /// <summary>
    ///     A recoil index that DECAYS ends the run, which is the engine's definition of a spray: the
    ///     maximal window over which the recoil pattern is one continuous curve. A shot-count
    ///     heuristic gets this wrong in both directions.
    /// </summary>
    [Test]
    public async Task RecoilDecay_OpensANewRun_EvenInsideTheGapBound()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: -1f),
            Shot(108, recoil: 5f, pitch: -3f),
            Shot(116, recoil: 0f, pitch: -5f));

        using (Assert.Multiple())
        {
            await Assert.That(result.Runs).IsEqualTo(2);
            await Assert.That(result.MeasuredShots).IsEqualTo(1)
                .Because("the third shot anchors a new run, and an anchor has no residual");
        }
    }

    /// <summary>
    ///     Before a run has measured its own cycle time, the opening gap bound is the slowest
    ///     repeat-fire weapon in the game. A gap past it is two deliberate taps, not a spray.
    /// </summary>
    [Test]
    public async Task GapWiderThanTheOpeningBound_StartsASecondRun()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: -1f),
            Shot(100 + SprayControlOracle.SprayOpenGapTicks + 1, recoil: 1f, pitch: -3f));

        using (Assert.Multiple())
        {
            await Assert.That(result.Runs).IsEqualTo(2);
            await Assert.That(result.MeasuredShots).IsEqualTo(0);
        }
    }

    /// <summary>
    ///     Once a run has a second shot, its own spacing IS the weapon's cycle time, and the bound
    ///     tightens to it. That is what stops a fast weapon's run from swallowing a later tap that
    ///     the generous opening bound would have accepted.
    /// </summary>
    [Test]
    public async Task MeasuredCycleTime_TightensTheGapBound_BelowTheOpeningOne()
    {
        // Gaps of 4 then 10 ticks. Ten is inside the 16-tick opening bound and outside the
        // ceil(4 * 1.10) = 5 the run measured for itself, so the third shot opens a new run.
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: -1f),
            Shot(104, recoil: 1f, pitch: -2f),
            Shot(114, recoil: 2f, pitch: -3f));

        using (Assert.Multiple())
        {
            await Assert.That(result.Runs).IsEqualTo(2);
            await Assert.That(result.MeasuredShots).IsEqualTo(1);
        }
    }

    /// <summary>
    ///     Two shots on one tick continue the run. They share a pre-frame snapshot, so their recoil
    ///     readings are identical and only the gap bound could separate them, and separating them
    ///     would restart the run in the middle of a spray.
    /// </summary>
    [Test]
    public async Task TwoShotsOnOneTick_ContinueTheRun()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: -1f),
            Shot(100, recoil: 0f, pitch: -4f));

        using (Assert.Multiple())
        {
            await Assert.That(result.Runs).IsEqualTo(1);
            await Assert.That(result.MeasuredShots).IsEqualTo(1);
            await Assert.That(result.MeanPitchError).IsEqualTo(3.0).Within(1e-9);
        }
    }

    /// <summary>
    ///     An aim punch that does not decode to a physically possible angle leaves the residual
    ///     UNMEASURED rather than several hundred degrees wide, and unmeasured rather than zero:
    ///     zero would read as perfect spray control. Probed on the bundled GOTV sample the punch
    ///     components cluster near -94 and +89 degrees, so this is the common case on a real demo,
    ///     not a defensive branch.
    /// </summary>
    [Test]
    public async Task ImplausibleAimPunch_LeavesTheRunUnmeasured()
    {
        // 266 wraps to -94, which no weapon kicks.
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: -1f, punchPitch: 266f),
            Shot(108, recoil: 1f, pitch: -3f, punchPitch: 266f));

        using (Assert.Multiple())
        {
            await Assert.That(result.Shots).IsEqualTo(2);
            await Assert.That(result.Runs).IsEqualTo(1)
                .Because("segmentation runs off recoil and ticks, which decode fine");
            await Assert.That(result.MeasuredShots).IsEqualTo(0);
            await Assert.That(result.PitchErrorSum).IsEqualTo(0.0);
        }
    }

    /// <summary>
    ///     A small punch survives the plausibility gate and is folded in at the engine's recoil
    ///     scale, so the residual is a deviation of EFFECTIVE aim rather than of view angle. Without
    ///     the scale a player who compensates perfectly would score a residual equal to the kick.
    /// </summary>
    [Test]
    public async Task PlausiblePunch_IsFoldedInAtTheRecoilScale()
    {
        // Anchor effective pitch: 0 + 2*0 = 0. Second shot: -4 + 2*2 = 0, so a player whose view
        // moved 4 degrees to cancel a 2 degree kick has a residual of zero, not of 4.
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: 0f, punchPitch: 0f),
            Shot(108, recoil: 1f, pitch: -4f, punchPitch: 2f));

        using (Assert.Multiple())
        {
            await Assert.That(result.MeasuredShots).IsEqualTo(1);
            await Assert.That(result.MeanPitchError).IsEqualTo(0.0).Within(1e-9);
        }
    }

    /// <summary>
    ///     The landed-arm angle reads the raw shot direction and ignores the punch, because ShootAng
    ///     already carries it. The same two shots that give the fired-arm pair a residual of zero
    ///     (the view moved 4 degrees to cancel a 2 degree kick) are 4 degrees apart as directions.
    /// </summary>
    [Test]
    public async Task AngleError_MeasuresTheRawDirection_AndIgnoresThePunch()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: 0f, punchPitch: 0f),
            Shot(108, recoil: 1f, pitch: -4f, punchPitch: 2f));

        using (Assert.Multiple())
        {
            await Assert.That(result.MeasuredShots).IsEqualTo(1);
            await Assert.That(result.MeanPitchError).IsEqualTo(0.0).Within(1e-9);
            await Assert.That(result.MeanAngleError).IsEqualTo(4.0).Within(1e-6);
        }
    }

    /// <summary>
    ///     Yaw takes the short way round the circle. A player crossing the 180 degree seam has moved
    ///     two degrees, and an unwrapped subtraction would score them 358.
    /// </summary>
    [Test]
    public async Task YawResidual_TakesTheShortWayRoundTheSeam()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, yaw: 179f),
            Shot(108, recoil: 1f, yaw: -179f));

        using (Assert.Multiple())
        {
            await Assert.That(result.MeasuredShots).IsEqualTo(1);
            await Assert.That(result.MeanYawError).IsEqualTo(2.0).Within(1e-9);
        }
    }

    /// <summary>
    ///     A missing recoil column is not evidence of a new run, so the gap decides alone. Reading
    ///     an absent column as a decay would split every spray into single-shot runs and make every
    ///     shot look like a first bullet.
    /// </summary>
    [Test]
    public async Task MissingRecoilColumn_LetsTheGapDecideAlone()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: null, pitch: -1f),
            Shot(108, recoil: null, pitch: -3f));

        using (Assert.Multiple())
        {
            await Assert.That(result.Runs).IsEqualTo(1);
            await Assert.That(result.MeasuredShots).IsEqualTo(1);
        }
    }

    /// <summary>
    ///     A run never spans a round boundary. Welding two engagements together measures a residual
    ///     against an anchor from a different fight, which produces a plausible number with nothing
    ///     to flag it.
    /// </summary>
    [Test]
    public async Task RoundBoundary_ResetsTheRun_EvenOnATightGap()
    {
        SprayPlayerResult result = FoldOne(
            Shot(100, recoil: 0f, pitch: -1f, round: 1),
            Shot(104, recoil: 1f, pitch: -3f, round: 2));

        using (Assert.Multiple())
        {
            await Assert.That(result.Runs).IsEqualTo(2);
            await Assert.That(result.MeasuredShots).IsEqualTo(0);
        }
    }

    /// <summary>Two shooters keep separate runs, so one player's burst cannot anchor another's.</summary>
    [Test]
    public async Task TwoShooters_KeepSeparateRuns()
    {
        IReadOnlyDictionary<int, SprayPlayerResult> results = SprayControlOracle.Fold(
        [
            Shot(100, recoil: 0f, pitch: -1f),
            Shot(102, recoil: 0f, pitch: -20f) with { Slot = Shooter + 1 },
            Shot(108, recoil: 1f, pitch: -3f)
        ]);

        using (Assert.Multiple())
        {
            await Assert.That(results[Shooter].Runs).IsEqualTo(1);
            await Assert.That(results[Shooter].MeasuredShots).IsEqualTo(1);
            await Assert.That(results[Shooter].MeanPitchError).IsEqualTo(2.0).Within(1e-9);
            await Assert.That(results[Shooter + 1].Runs).IsEqualTo(1);
            await Assert.That(results[Shooter + 1].MeasuredShots).IsEqualTo(0);
        }
    }

    private static void Compare(List<string> drift, string what, double oracle, double engine)
    {
        if (Math.Abs(oracle - engine) > 1e-6)
        {
            drift.Add($"{what}: the oracle copies {oracle}, the engine ships {engine}");
        }
    }

    private static SprayPlayerResult FoldOne(params OracleShot[] shots) =>
        SprayControlOracle.Fold(shots)[Shooter];

    private static OracleShot Shot(
        int tick,
        float? recoil = 0f,
        float pitch = 0f,
        float yaw = 0f,
        float punchPitch = 0f,
        float punchYaw = 0f,
        int round = 1) =>
        new(Shooter, round, tick, recoil, pitch, yaw, punchPitch, punchYaw);
}
