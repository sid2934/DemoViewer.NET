#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenSchema.Events;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The grenade rows off the rulesets against a real demo: the invariants they make checkable (a projectile
///     per grenade <c>weapon_fire</c>, a thrower on every row, the release from the event, the detonations
///     joined, no garbage cell in a path, the inputs agreeing with the ground flag). Skips without a demo, and
///     never reads the tour sample (it is incomplete).
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class GrenadeWalkRealDemoTests
{
    [Test]
    public async Task TheRows_HoldTheMeasuredInvariants()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        GrenadeWalk walk = GrenadeWalker.Assemble(GrenadeIndexEvaluator.Run(parsed, 4), ClipRounds.Derive(parsed), parsed.Players.Values,
            parsed.TickRate > 0 ? parsed.TickRate : 64, parsed.ServerStartTick);
        IReadOnlyList<GrenadeRow> rows = walk.Rows;
        if (rows.Count == 0)
        {
            throw new SkipTestException("demo carries no grenades");
        }

        Console.WriteLine($"grenade rows: {parsed.Frames.Count} frames, {rows.Count} rows, input coverage {walk.InputCoverage:0.0000}; " +
                          string.Join(", ", rows.GroupBy(r => r.Kind).Select(g => $"{g.Key} {g.Count()}")));

        Dictionary<string, int> fires = parsed.AllGameEvents
            .Select(e => e.Payload is WeaponFireEvent f ? GrenadeRules.ProjectileClassOfWeapon(f.Weapon) : null)
            .Where(c => c is not null)
            .GroupBy(c => c!)
            .ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, int> projectiles = rows
            .GroupBy(r => r.Kind switch
            {
                GrenadeKind.Smoke => GrenadeProjectileClasses.Smoke,
                GrenadeKind.He => GrenadeProjectileClasses.HEGrenade,
                GrenadeKind.Flash => GrenadeProjectileClasses.Flashbang,
                GrenadeKind.Decoy => GrenadeProjectileClasses.Decoy,
                _ => GrenadeProjectileClasses.Molotov
            })
            .ToDictionary(g => g.Key, g => g.Count());

        List<SmokeGrenadeDetonateEvent> smokePops = [.. parsed.AllGameEvents.Select(e => e.Payload).OfType<SmokeGrenadeDetonateEvent>()];
        List<double> yawErrors = [];
        foreach (GrenadeRow row in rows.Where(r => r.ReleaseOnGround == true && r.ReleaseEyeYaw is not null
                                                   && r.SpawnVelocity is { } v && MathF.Abs(v.X) + MathF.Abs(v.Y) > 1))
        {
            WorldPoint v = row.SpawnVelocity!.Value;
            double velocityYaw = Math.Atan2(v.Y, v.X) * 180 / Math.PI;
            double delta = Math.Abs(((velocityYaw - row.ReleaseEyeYaw!.Value) % 360 + 540) % 360 - 180);
            yawErrors.Add(delta);
        }

        yawErrors.Sort();

        using (Assert.Multiple())
        {
            await Assert.That(walk.InputDecoder).IsEqualTo(GrenadeWalker.InputDecoderName);
            foreach ((string cls, int count) in fires)
            {
                await Assert.That(Math.Abs(projectiles.GetValueOrDefault(cls) - count)).IsLessThanOrEqualTo(1)
                    .Because($"{cls}: one projectile per grenade weapon_fire");
            }

            await Assert.That(rows.Count(r => r.ThrowerSource == ThrowerSource.None)).IsEqualTo(0)
                .Because("the weapon_fire join names every thrower the chain lost");
            await Assert.That(rows.Count(r => r.ReleaseSource == ReleaseSource.WeaponFire) / (double)rows.Count).IsGreaterThanOrEqualTo(0.99);

            foreach (GrenadeRow row in rows.Where(r => r.ReleaseSource == ReleaseSource.WeaponFire))
            {
                await Assert.That(row.SpawnTick - row.ReleaseTick).IsGreaterThanOrEqualTo(GrenadeRules.ReleaseToSpawnMinTicks).Because(row.Id);
                await Assert.That(row.SpawnTick - row.ReleaseTick).IsLessThanOrEqualTo(GrenadeRules.ReleaseToSpawnMaxTicks).Because(row.Id);
            }

            foreach (GrenadeRow row in rows.Where(r => r.Kind == GrenadeKind.Smoke && r.DetonationSource == DetonationSource.Event))
            {
                WorldPoint p = row.DetonationPosition!.Value;
                await Assert.That(smokePops.Any(e => MathF.Abs(e.X - p.X) < 0.5f && MathF.Abs(e.Y - p.Y) < 0.5f && MathF.Abs(e.Z - p.Z) < 0.5f))
                    .IsTrue().Because(row.Id);
            }

            await Assert.That(rows.Count(r => r.Kind == GrenadeKind.He && r.DetonationSource != DetonationSource.Event))
                .IsLessThanOrEqualTo(1).Because("every HE's detonation is its hegrenade_detonate");

            if (yawErrors.Count > 10)
            {
                await Assert.That(yawErrors[(int)(yawErrors.Count * 0.9)]).IsLessThan(5)
                    .Because("the release eye yaw is the setang (p90 under five degrees on grounded throws)");
            }

            await Assert.That(rows.SelectMany(r => r.Trajectory)
                    .Count(p => MathF.Abs(p.X) > 16000 || MathF.Abs(p.Y) > 16000 || MathF.Abs(p.Z) > 16000))
                .IsEqualTo(0).Because("no zero-cell garbage in a path (#56)");
            await Assert.That(rows.Count(r => r.Trajectory.Count == 0)).IsEqualTo(0).Because("the observer kept every flight");

            if (walk.InputCoverage > 0.9)
            {
                List<GrenadeRow> read = [.. rows.Where(r => r.ThrowerOnGroundAtSpawn is not null)];
                await Assert.That(read.Count(r => r.JumpThrowSource != JumpThrowSource.Inputs)).IsLessThanOrEqualTo(read.Count / 100)
                    .Because("with commands on the lane, the inputs decide the jump-throw flag");
                int agree = read.Count(r => r.JumpThrow == (r.ThrowerOnGroundAtSpawn == false));
                await Assert.That(agree / (double)Math.Max(1, read.Count)).IsGreaterThanOrEqualTo(0.95)
                    .Because("the ground flag at spawn agrees with the jump press");
            }
        }
    }
}
