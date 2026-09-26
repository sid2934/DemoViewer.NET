#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="ProjectileThrowerMatch" /> (#56, #59) over hand-built <see cref="ProjectileSample" /> rows: no
///     demo, no walk, the synthetic matching tests the wiring calls for. The two consumers
///     (<see cref="DemoViewer.NET.Services.Strats.RoundCaptureWalker" />, <see cref="DemoViewer.NET.Modules.SuggestedTags.DetonationEvents" />)
///     are covered where they live.
/// </summary>
public class ProjectileThrowerMatchTests
{
    private static ProjectileSample Removed(string className, int tick, Vector3 position, int throwerSlot = 4,
        Vector3? initialPosition = null) =>
        new(FrameIndex: tick, Tick: tick, EntityIndex: 20, Serial: 1, ClassName: className, ThrowerSlot: throwerSlot,
            Position: position, InitialPosition: initialPosition, InitialVelocity: null, Bounces: 0, Created: false, Removed: true);

    [Test]
    public async Task ARemovedSampleNearTheDetonation_NamesTheThrowerSlot()
    {
        ProjectileSample molotov = Removed(GrenadeProjectileClasses.Molotov, 1000, new Vector3(100, 200, 0), throwerSlot: 3);

        ProjectileSample? match = ProjectileThrowerMatch.Nearest([molotov], GrenadeProjectileClasses.Molotov, 1002, new Vector3(105, 205, 0));

        await Assert.That(match?.ThrowerSlot).IsEqualTo(3);
    }

    [Test]
    public async Task NoProjectileNearby_LeavesNoMatch()
    {
        ProjectileSample farAway = Removed(GrenadeProjectileClasses.Decoy, 1000, new Vector3(5000, 5000, 0));
        ProjectileSample lateInTime = Removed(GrenadeProjectileClasses.Decoy, 1000 + ProjectileThrowerMatch.TickTolerance + 1, new Vector3(100, 100, 0));

        using (Assert.Multiple())
        {
            await Assert.That(ProjectileThrowerMatch.Nearest([farAway], GrenadeProjectileClasses.Decoy, 1000, new Vector3(100, 100, 0)))
                .IsNull().Because("outside the position tolerance");
            await Assert.That(ProjectileThrowerMatch.Nearest([lateInTime], GrenadeProjectileClasses.Decoy, 1000, new Vector3(100, 100, 0)))
                .IsNull().Because("outside the tick tolerance");
        }
    }

    [Test]
    public async Task AWrongClassOrAnInFlightSample_NeverMatches()
    {
        ProjectileSample decoy = Removed(GrenadeProjectileClasses.Decoy, 1000, new Vector3(100, 100, 0));
        ProjectileSample inFlight = decoy with { Removed = false };

        using (Assert.Multiple())
        {
            await Assert.That(ProjectileThrowerMatch.Nearest([decoy], GrenadeProjectileClasses.Molotov, 1000, new Vector3(100, 100, 0)))
                .IsNull().Because("a decoy is not a molotov");
            await Assert.That(ProjectileThrowerMatch.Nearest([inFlight], GrenadeProjectileClasses.Decoy, 1000, new Vector3(100, 100, 0)))
                .IsNull().Because("only the Removed sample marks where a projectile ended");
        }
    }

    [Test]
    public async Task WithSeveralCandidates_TheClosestPositionWins()
    {
        ProjectileSample near = Removed(GrenadeProjectileClasses.HEGrenade, 998, new Vector3(110, 100, 0), throwerSlot: 1);
        ProjectileSample far = Removed(GrenadeProjectileClasses.HEGrenade, 1000, new Vector3(300, 100, 0), throwerSlot: 2);

        ProjectileSample? match = ProjectileThrowerMatch.Nearest([far, near], GrenadeProjectileClasses.HEGrenade, 1000, new Vector3(100, 100, 0));

        await Assert.That(match?.ThrowerSlot).IsEqualTo(1);
    }
}
