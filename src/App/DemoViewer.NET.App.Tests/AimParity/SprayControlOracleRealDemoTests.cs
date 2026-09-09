#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     The spray-control oracle against a real demo, folded from <c>bullet_damage</c>, which carries
///     the shooter's view angle, aim punch and recoil index on the wire.
///     <para>
///         <b>Why this arm and not the shipped one.</b> The board's SprayPitch and SprayYaw columns
///         are computed on the FIRED stream (<c>weapon_fire</c> plus per-tick entity reads), because
///         a shot that missed is still a spray-control measurement. That stream's inputs live inside
///         the evaluator and are not reachable from a test without re-hosting the digest. The LANDED
///         stream carries the same four quantities as plain event fields, so folding it gives a
///         genuinely independent spray-control number over a real match with no engine involvement
///         at all. It is a subset of the shipped population, not a replica of it, which is why the
///         engine's own columns are printed beside it as context rather than asserted against it.
///     </para>
///     <para>
///         <b>What is asserted.</b> Only the invariants any correct implementation must satisfy: the
///         anchor of every run is outside the measured population, the measured population never
///         exceeds the non-anchor shots, and no mean escapes the range an angle can occupy. Those
///         hold on any demo, which is what makes this runnable on the two Valve matchmaking demos
///         that carry <c>bullet_damage</c> even though neither has a Leetify partner.
///     </para>
///     <para>
///         <b>The bundled sample cannot run this.</b> <c>assets/tour/sample-de_nuke.dem</c> carries
///         206 <c>weapon_fire</c> and zero <c>bullet_damage</c>, so this skips there rather than
///         reporting green over an empty fold.
///     </para>
///     <para>
///         <b>Measured on the two matchmaking demos.</b> The fold produces mean absolute residuals
///         of 0.3 to 6.9 degrees of pitch over measured populations of 11 to 42 shots per player,
///         which is the magnitude spray compensation should have. Their aim punch therefore decodes
///         to real angles, unlike the bundled GOTV sample where the components cluster near -94 and
///         +89. The shipped columns cannot be compared against it on those two demos: see
///         <see cref="AimSchemaDriftException" />.
///     </para>
/// </summary>
[Category("RealDemo")]
[NotInParallel]
public class SprayControlOracleRealDemoTests
{
    /// <summary>
    ///     Folds the demo's landed shots and asserts the invariants, printing the per-player
    ///     spray-control table as the oracle's output.
    /// </summary>
    [Test]
    public async Task LandedShotFold_HoldsTheSprayInvariants_AndReportsThePopulation()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        List<OracleShot> shots = LandedShots(demo);
        if (shots.Count == 0)
        {
            throw new SkipTestException(
                "This demo carries no bullet_damage, so the landed arm has nothing to fold. "
                + "The trimmed GOTV sources routinely omit it; the Valve matchmaking demos carry it.");
        }

        IReadOnlyDictionary<int, SprayPlayerResult> results = SprayControlOracle.Fold(shots);

        Console.WriteLine($"── spray-control oracle: {demo.MapName}, {shots.Count} landed shots ──");
        List<string> violations = [];
        int measuredTotal = 0;

        foreach ((int slot, SprayPlayerResult result) in results.OrderBy(pair => pair.Key))
        {
            string name = demo.Players.TryGetValue(slot, out PlayerInfo? info) ? info.Name : $"slot{slot}";
            Console.WriteLine($"   {name,-24} {result.Render()}");
            measuredTotal += result.MeasuredShots;

            if (result.Shots > 0 && result.Runs < 1)
            {
                violations.Add($"{name}: {result.Shots} shots and no run, so no shot anchored anything");
            }

            if (result.MeasuredShots > result.Shots - result.Runs)
            {
                violations.Add(
                    $"{name}: measured {result.MeasuredShots} of {result.Shots} shots against {result.Runs} "
                    + "anchors, which counts an anchor into its own population");
            }

            // A residual is a difference of two angles, each of them a view angle plus twice a
            // punch the plausibility gate bounds. Anything outside this is not an angle.
            if (result.MeanPitchError is < 0 or > 360 || result.MeanYawError is < 0 or > 180)
            {
                violations.Add(
                    $"{name}: pitch {result.MeanPitchError:F2} / yaw {result.MeanYawError:F2} is outside "
                    + "the range a residual can occupy");
            }
        }

        Console.WriteLine($"   measured population across all players: {measuredTotal}");
        violations.ForEach(Console.WriteLine);

        await Assert.That(results).IsNotEmpty();
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Prints the oracle's landed-arm numbers beside the engine's fired-arm columns. Context,
    ///     not an assertion: the two measure different populations by design (see the class
    ///     summary), so a gap between them is information about coverage rather than a defect. It
    ///     earns its place because it is the only place the two ever appear side by side, and a
    ///     shipped SprayN far below the landed measured count is the shape a decode regression
    ///     takes.
    /// </summary>
    [Test]
    public async Task ShippedSprayColumns_AreReportedBesideTheOracle()
    {
        string demoPath = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        List<OracleShot> shots = LandedShots(demo);
        if (shots.Count == 0)
        {
            throw new SkipTestException("This demo carries no bullet_damage, so there is nothing to compare.");
        }

        IReadOnlyDictionary<int, SprayPlayerResult> oracle = SprayControlOracle.Fold(shots);
        AimRunResult run;
        try
        {
            run = LiveAimStats.Derive(Path.GetFileName(demoPath), demoPath, demo);
        }
        catch (AimSchemaDriftException drift)
        {
            // A source whose schema the aim providers do not resolve against produces no columns at
            // all, so there is nothing to sit beside the oracle. Skipped rather than failed BECAUSE
            // this arm runs on whichever demo the machine happens to have: on a source with no
            // Leetify partner a hard red says nothing the message does not, and the benchmark
            // parity cases let the same exception fail, which is where it means something.
            throw new SkipTestException(drift.Message);
        }

        Console.WriteLine("── spray control: oracle (landed arm) against the shipped columns (fired arm) ──");
        Console.WriteLine($"   {"player",-24}{"oracleN",8}{"SprayN",8}{"oraclePitch",13}{"SprayPitch",12}");
        int compared = 0;
        foreach ((string name, AimPlayerRow player) in run.Players.OrderBy(pair => pair.Value.Slot))
        {
            SprayPlayerResult? theirs = oracle.TryGetValue(player.Slot, out SprayPlayerResult? found) ? found : null;
            Console.WriteLine(
                $"   {name,-24}{theirs?.MeasuredShots ?? 0,8}{player.Read("spray_residual_shots") ?? 0,8:F0}"
                + $"{theirs?.MeanPitchError ?? 0,13:F2}{player.Read("spray_pitch_error") ?? 0,12:F2}");
            compared++;
        }

        await Assert.That(compared).IsGreaterThan(0)
            .Because("a report over no players says nothing about either arm");
    }

    /// <summary>
    ///     Turns the demo's <c>bullet_damage</c> stream into oracle shots. The four angular
    ///     quantities arrive as event fields here, which is what makes this fold independent of the
    ///     entity-decode path the shipped columns run on.
    /// </summary>
    private static List<OracleShot> LandedShots(ParsedDemo demo)
    {
        List<OracleShot> shots = [];
        int round = 0;
        bool matchStarted = false;

        // Stable ordering by frame keeps two events on one frame in parse order, which is the order
        // the evaluator would dispatch them in.
        foreach (GameEvent gameEvent in demo.AllGameEvents.OrderBy(e => e.FrameNumber))
        {
            switch (gameEvent.Payload)
            {
                case BeginNewMatchEvent:
                    matchStarted = true;
                    round = 0;
                    continue;
                case RoundFreezeEndEvent:
                    round++;
                    continue;
                case BulletDamageEvent landed:
                    if (!matchStarted || landed.Attacker < 0
                        || gameEvent.FrameNumber < 0 || gameEvent.FrameNumber >= demo.Frames.Count)
                    {
                        continue;
                    }

                    shots.Add(new OracleShot(
                        landed.Attacker,
                        round,
                        demo.Frames[gameEvent.FrameNumber].ServerTick,
                        landed.RecoilIndex,
                        landed.ShootAngX,
                        landed.ShootAngY,
                        landed.AimPunchX,
                        landed.AimPunchY));
                    continue;
                default:
                    continue;
            }
        }

        return shots;
    }
}
