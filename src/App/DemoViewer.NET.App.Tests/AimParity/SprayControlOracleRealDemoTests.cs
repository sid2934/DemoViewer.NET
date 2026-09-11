#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     The spray-control oracle against a real demo, folded from <c>bullet_damage</c>, which carries
///     the shooter's shot direction, aim punch and recoil index on the wire.
///     <para>
///         <b>Which arm this is.</b> The shipped Spray column is measured on the LANDED arm too
///         (<c>on: shot_landed</c> in rules/aim_rating.rules.yaml): the engine's fired arm owns run
///         SEGMENTATION off per-tick entity reads that a test cannot reach without re-hosting the
///         digest, and the landed arm measures each landed bullet's 3D angle from the run's first
///         landed one. This fold sees that same landed stream as plain event fields, so it is a
///         genuinely independent number over a real match with no engine involvement. It is NOT a
///         replica of the shipped population: the oracle segments on the landed bullets alone, and a
///         miss inside a spray can open a run here that the engine keeps as one, which is why the
///         shipped columns are printed beside it rather than asserted against it.
///     </para>
///     <para>
///         <b>What is asserted.</b> The invariants any correct fold must satisfy (the anchor of every
///         run is outside the measured population, the population never exceeds the non-anchor
///         shots, no mean escapes the range an angle can occupy), and that the shipped column is
///         WIRED: on a demo carrying <c>bullet_damage</c>, some player has a measured SprayN and a
///         non-zero Spray. That second assertion is what makes the side-by-side a test: it read a
///         stat id the ruleset no longer declared for a while, and printed 0.00 under a plausible
///         header on every player.
///     </para>
///     <para>
///         <b>The bundled sample cannot run this.</b> <c>assets/tour/sample-de_nuke.dem</c> carries
///         206 <c>weapon_fire</c> and zero <c>bullet_damage</c>, so this skips there rather than
///         reporting green over an empty fold.
///     </para>
///     <para>
///         <b>Measured on demos/benchmarks/match730_003769462952671838367_0003107139_392.dem.</b>
///         The fold measures 7 to 19 landed bullets per player at a mean angle of 0.4 to 3.2 degrees,
///         the magnitude spray compensation should have. Where the engine's SprayN equals the
///         oracle's population the two means agree to the printed digit (7 shots, 1.27 against 1.27),
///         which says the shipped column and the oracle fold the same quantity. Where SprayN exceeds
///         it, Spray runs far above the oracle (26 against 19 shots, 22.86 against 0.41 degrees): the
///         extra bullets are measured against an anchor the oracle had already retired on a tick gap,
///         which is the engine's fired-arm segmentation keeping a run open across what the landed
///         stream reads as two. That gap is what this table is for; it is reported here, not fixed.
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
    ///     Prints the oracle's numbers beside the shipped Spray and SprayN, and asserts the shipped
    ///     column is wired. The two means are context, not an assertion against each other: they
    ///     segment differently by design (see the class summary), so a gap between them is
    ///     information about segmentation rather than a defect. What IS asserted is that the shipped
    ///     column produced a population and a non-zero mean on a demo that carries the event it
    ///     reads, and that no shipped mean escapes the range an angle can occupy.
    /// </summary>
    [Test]
    public async Task ShippedSprayColumn_IsWired_AndReportedBesideTheOracle()
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

        Console.WriteLine("── spray control: oracle (landed fold) against the shipped columns (landed arm, fired segmentation) ──");
        Console.WriteLine(
            $"   {"player",-24}{"oracleN",8}{"SprayN",8}{"oracleAngle",13}{"Spray",8}{"oraclePitch",13}{"oracleYaw",11}");
        int compared = 0;
        int wired = 0;
        List<string> violations = [];
        foreach ((string name, AimPlayerRow player) in run.Players.OrderBy(pair => pair.Value.Slot))
        {
            SprayPlayerResult? theirs = oracle.TryGetValue(player.Slot, out SprayPlayerResult? found) ? found : null;
            double shippedN = player.Read("spray_residual_shots") ?? 0;
            double shippedSpray = player.Read("spray_error_deg") ?? 0;
            Console.WriteLine(
                $"   {name,-24}{theirs?.MeasuredShots ?? 0,8}{shippedN,8:F0}"
                + $"{theirs?.MeanAngleError ?? 0,13:F2}{shippedSpray,8:F2}"
                + $"{theirs?.MeanPitchError ?? 0,13:F2}{theirs?.MeanYawError ?? 0,11:F2}");
            compared++;

            if (shippedN > 0 && shippedSpray > 0)
            {
                wired++;
            }

            if (shippedSpray is < 0 or > 180)
            {
                violations.Add($"{name}: Spray {shippedSpray:F2} is outside the range an angle can occupy");
            }
        }

        violations.ForEach(Console.WriteLine);

        await Assert.That(compared).IsGreaterThan(0)
            .Because("a report over no players says nothing about either arm");
        await Assert.That(violations).IsEmpty();
        await Assert.That(wired).IsGreaterThan(0)
            .Because("this demo carries bullet_damage, so a Spray of 0.0 over an empty SprayN on EVERY player means "
                     + "the column reads a stat the ruleset does not declare, or an arm that never fires");
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
