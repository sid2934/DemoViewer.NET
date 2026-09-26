#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using DemoViewer.NET.Modules.UtilityBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The grenade walk's pure steps on synthetic samples and events (grenade-walk.md §7, unit): the
///     named thresholds, jump-throw for every (coverage, press, ground flag) case, the console line, the
///     polyline thinning, the track fold, the thrower and release plan, the detonation per kind, and a
///     whole row assembled from hand-made pawn reads. No demo is parsed.
/// </summary>
public class GrenadeWalkerTests
{
    private const int Slot = 3;
    private const int Index = 120;
    private const int Serial = 7;

    private static readonly int[] BounceTicks = [117, 133, 151];
    private static readonly int[] FoldedTicks = [10, 12, 13];

    private static ProjectileSample Sample(int frame, int tick, string cls = GrenadeProjectileClasses.HEGrenade,
        int thrower = Slot, Vector3? position = null, int bounces = 0, bool created = false, bool removed = false,
        int index = Index, int serial = Serial) =>
        new(frame, tick, index, serial, cls, thrower, position,
            created ? new Vector3(0, 0, 64) : null,
            created ? new Vector3(500, 0, 100) : null,
            bounces, created, removed);

    private static GameEvent Fire(int slot, int frame, int tick, string weapon) =>
        new("weapon_fire", -1, frame, tick, tick, new WeaponFireEvent { UserId = slot, UserIdPawn = 0, Weapon = weapon, Silenced = false });

    private static GameEvent HeDetonate(int tick, int entity, Vector3 p) =>
        new("hegrenade_detonate", -1, tick, tick, tick,
            new HegrenadeDetonateEvent { EntityId = (short)entity, UserId = Slot, UserIdPawn = 0, X = p.X, Y = p.Y, Z = p.Z });

    private static GameEvent Inferno(int tick, int entity, Vector3 p) =>
        new("inferno_startburn", -1, tick, tick, tick,
            new InfernoStartburnEvent { EntityId = (short)entity, X = p.X, Y = p.Y, Z = p.Z });

    // One frame per tick: frame index == tick, the simplest clock that still exercises every lookup.
    private static int FrameAt(int tick) => tick < 0 ? -1 : tick;

    // An HE thrown at tick 100 (release 93), flying east, bouncing twice, going off at tick 204.
    private static List<ProjectileSample> HeFlight(int thrower = Slot)
    {
        List<ProjectileSample> samples = [Sample(100, 100, thrower: thrower, position: new Vector3(10, 0, 64), created: true)];
        int bounces = 0;
        for (int t = 101; t <= 203; t++)
        {
            if (t is 140 or 170)
            {
                bounces++;
            }

            float x = t <= 190 ? 10 + (t - 100) * 5 : 10 + 90 * 5; // at rest from 190
            samples.Add(Sample(t, t, thrower: thrower, position: new Vector3(x, 0, 64), bounces: bounces));
        }

        samples.Add(Sample(204, 204, thrower: thrower, position: new Vector3(460, 0, 64), bounces: bounces, removed: true));
        return samples;
    }

    // ── thresholds and classification ────────────────────────────────────────────────────────────────

    [Test]
    [Arguments(0f, MovementClass.Stationary)]
    [Arguments(9.99f, MovementClass.Stationary)]
    [Arguments(10f, MovementClass.Walking)]
    [Arguments(139.9f, MovementClass.Walking)]
    [Arguments(140f, MovementClass.Running)]
    [Arguments(245f, MovementClass.Running)]
    public async Task Movement_SplitsAtTheNamedThresholds(float speed, MovementClass expected) =>
        await Assert.That(GrenadeRules.ClassifyMovement(speed)).IsEqualTo(expected);

    [Test]
    public async Task Movement_WithoutASpeed_IsUnknown() =>
        await Assert.That(GrenadeRules.ClassifyMovement(null)).IsEqualTo(MovementClass.Unknown);

    [Test]
    [Arguments(1.0f, true, ThrowStrengthClass.Full)]
    [Arguments(0.95f, true, ThrowStrengthClass.Full)]
    [Arguments(0.5f, true, ThrowStrengthClass.Half)]
    [Arguments(0.0f, true, ThrowStrengthClass.Underhand)]
    [Arguments(0.8f, true, ThrowStrengthClass.Other)]
    [Arguments(1.0f, false, ThrowStrengthClass.Unknown)]
    public async Task Strength_ClassesAtTheNamedThresholds_AndUnknownOffAGrenade(float strength, bool held, ThrowStrengthClass expected) =>
        await Assert.That(GrenadeRules.ClassifyStrength(strength, held)).IsEqualTo(expected);

    [Test]
    public async Task JumpThrow_InputsDecideWhenTheyCoverTheWindow()
    {
        using (Assert.Multiple())
        {
            // Covered with a press: a jump-throw, whatever the ground flag says (the press-after-release bind
            // leaves the pawn grounded at the release but not at the spawn).
            await Assert.That(GrenadeRules.ClassifyJumpThrow(true, 95, true)).IsEqualTo((true, JumpThrowSource.Inputs, (int?)95));
            // Covered with no press: standing, even when the pawn is airborne (a ledge drop).
            await Assert.That(GrenadeRules.ClassifyJumpThrow(true, null, false)).IsEqualTo((false, JumpThrowSource.Inputs, (int?)null));
            // Not covered: the spawn frame's ground flag.
            await Assert.That(GrenadeRules.ClassifyJumpThrow(false, null, false)).IsEqualTo((true, JumpThrowSource.GroundFlag, (int?)null));
            await Assert.That(GrenadeRules.ClassifyJumpThrow(false, null, true)).IsEqualTo((false, JumpThrowSource.GroundFlag, (int?)null));
            // Nothing to read at all.
            await Assert.That(GrenadeRules.ClassifyJumpThrow(false, null, null)).IsEqualTo((false, JumpThrowSource.None, (int?)null));
        }
    }

    [Test]
    public async Task Kind_TellsAMolotovFromAnIncendiaryByTheWeaponFirst()
    {
        using (Assert.Multiple())
        {
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, "weapon_incgrenade", false)).IsEqualTo(GrenadeKind.Incendiary);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, "weapon_molotov", true)).IsEqualTo(GrenadeKind.Molotov);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, null, true)).IsEqualTo(GrenadeKind.Incendiary);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, null, null)).IsEqualTo(GrenadeKind.Molotov);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Flashbang, null, null)).IsEqualTo(GrenadeKind.Flash);
            await Assert.That(GrenadeRules.KindOf("CBaseEntity", null, null)).IsNull();
            await Assert.That(GrenadeRules.ProjectileClassOfWeapon("weapon_smokegrenade")).IsEqualTo(GrenadeProjectileClasses.Smoke);
            await Assert.That(GrenadeRules.ProjectileClassOfWeapon("incgrenade")).IsEqualTo(GrenadeProjectileClasses.Molotov);
            await Assert.That(GrenadeRules.ProjectileClassOfWeapon("weapon_ak47")).IsNull();
        }
    }

    [Test]
    public async Task Console_PrintsTwoDecimalsInTheInvariantCulture_AndNothingWithoutARelease()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            GrenadeRow row = new()
            {
                ReleasePosition = new WorldPoint(-2260f, -1036.004f, -414.456f),
                ReleaseEyePitch = -18.1249f,
                ReleaseEyeYaw = -15.3f
            };

            using (Assert.Multiple())
            {
                await Assert.That(GrenadeConsole.Format(row))
                    .IsEqualTo("setpos -2260.00 -1036.00 -414.46; setang -18.12 -15.30 0.00");
                await Assert.That(GrenadeConsole.Format(new GrenadeRow { ReleasePosition = new WorldPoint(1, 2, 3) })).IsNull()
                    .Because("no eye angles is no setang, never a zero one");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ── the polyline ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(1000)]
    public async Task Trajectory_KeepsTheFirstTheLastAndEveryBounceVertex_AtAnyStride(int stride)
    {
        TrajectoryBuilder builder = new(stride);
        builder.Start(100, new Vector3(0, 0, 64));
        int bounces = 0;
        for (int t = 101; t <= 160; t++)
        {
            if (t is 117 or 133 or 151)
            {
                bounces++;
            }

            builder.Offer(t, new Vector3((t - 100) * 3, 0, 64), bounces);
        }

        // At rest: the same point again is not movement.
        await Assert.That(builder.Offer(161, new Vector3(180, 0, 64), bounces)).IsFalse();
        List<TrajectoryPoint> points = builder.Finish();

        using (Assert.Multiple())
        {
            await Assert.That(points[0]).IsEqualTo(new TrajectoryPoint(100, 0, 0, 64, 0));
            await Assert.That(points[^1].Tick).IsEqualTo(160);
            foreach (int bounceTick in BounceTicks)
            {
                await Assert.That(points.Any(p => p.Tick == bounceTick)).IsTrue().Because($"bounce at {bounceTick}, stride {stride}");
            }

            await Assert.That(points.Zip(points.Skip(1)).All(pair => pair.First.Tick < pair.Second.Tick)).IsTrue();
            if (stride == 1)
            {
                await Assert.That(points.Count).IsEqualTo(61);
            }
        }
    }

    // ── the track fold ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Tracks_FoldCreationFlightAndRemoval_AndHoldTheFirstResolvedThrower()
    {
        List<ProjectileSample> samples =
        [
            Sample(10, 10, thrower: -1, position: null, created: true),
            // The sampler rejects a zero-cell read as null: it never enters the flight.
            Sample(11, 11, thrower: -1, position: null),
            Sample(12, 12, thrower: 5, position: new Vector3(20, 0, 64)),
            Sample(13, 13, thrower: -1, position: new Vector3(40, 0, 64), bounces: 1),
            Sample(14, 14, thrower: -1, position: new Vector3(40, 0, 64), bounces: 1),
            Sample(15, 15, thrower: -1, position: new Vector3(40, 0, 64), bounces: 1, removed: true),
            // The slot reused by the next projectile after the removal.
            Sample(15, 15, cls: GrenadeProjectileClasses.Flashbang, thrower: 2, position: new Vector3(1, 1, 1), created: true, serial: 8)
        ];

        List<ProjectileTrack> tracks = ProjectileTrack.Collect(samples, 1);

        using (Assert.Multiple())
        {
            await Assert.That(tracks.Count).IsEqualTo(2);
            ProjectileTrack he = tracks[0];
            await Assert.That(he.Id).IsEqualTo($"g{Index}-{Serial}");
            await Assert.That(he.ThrowerSlot).IsEqualTo(5).Because("held once resolved, as the sampler holds it");
            await Assert.That(he.SpawnTick).IsEqualTo(10);
            await Assert.That(he.Removed).IsTrue();
            await Assert.That(he.LastFrame).IsEqualTo(14).Because("the removal frame's values are the frame before's");
            await Assert.That(he.EndTick).IsEqualTo(15);
            await Assert.That(he.LastMovedTick).IsEqualTo(13);
            await Assert.That(he.MaxBounces).IsEqualTo(1);
            await Assert.That(he.Trajectory.Select(p => p.Tick)).IsEquivalentTo(FoldedTicks);
            await Assert.That(tracks[1].ClassName).IsEqualTo(GrenadeProjectileClasses.Flashbang);
            await Assert.That(tracks[1].Removed).IsFalse();
        }
    }

    // ── the plan: thrower and release ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Plan_AResolvedThrowerTakesItsLatestFireInTheLookback()
    {
        List<ProjectileTrack> tracks = ProjectileTrack.Collect(HeFlight(), 4);
        GrenadeEventIndex events = GrenadeEventIndex.From(
        [
            Fire(Slot, 40, 40, "weapon_hegrenade"), // outside the 40-tick lookback
            Fire(Slot, 90, 90, "weapon_hegrenade"),
            Fire(Slot, 93, 93, "weapon_hegrenade"),
            Fire(Slot, 95, 95, "weapon_flashbang"), // another family
            Fire(4, 94, 94, "weapon_hegrenade") // another slot
        ]);

        ThrowPlan plan = GrenadeWalker.Plan(tracks, events, FrameAt).Single();

        using (Assert.Multiple())
        {
            await Assert.That(plan.ThrowerSlot).IsEqualTo(Slot);
            await Assert.That(plan.ThrowerSource).IsEqualTo(ThrowerSource.Entity);
            await Assert.That(plan.ReleaseSource).IsEqualTo(ReleaseSource.WeaponFire);
            await Assert.That(plan.ReleaseTick).IsEqualTo(93);
            await Assert.That(plan.ReleaseFrame).IsEqualTo(93);
            await Assert.That(plan.SpeedFrame).IsEqualTo(85);
            await Assert.That(plan.Weapon).IsEqualTo("weapon_hegrenade");
        }
    }

    [Test]
    public async Task Plan_AnUnresolvedSmokeJoinsTheUnclaimedFireNearestTheSevenTickGap()
    {
        // Two smokes spawn at 200; one has its thrower, one lost it to the creation-packet defect (#56).
        List<ProjectileTrack> tracks = ProjectileTrack.Collect(
        [
            Sample(200, 200, GrenadeProjectileClasses.Smoke, thrower: 1, position: new Vector3(5, 5, 5), created: true, index: 1),
            Sample(200, 200, GrenadeProjectileClasses.Smoke, thrower: -1, position: null, created: true, index: 2)
        ], 4);
        GrenadeEventIndex events = GrenadeEventIndex.From(
        [
            Fire(1, 193, 193, "weapon_smokegrenade"),
            Fire(6, 190, 190, "weapon_smokegrenade"),
            Fire(8, 180, 180, "weapon_smokegrenade") // 20 ticks before: outside [spawn - 15, spawn - 6]
        ]);

        List<ThrowPlan> plans = GrenadeWalker.Plan(tracks, events, FrameAt);

        using (Assert.Multiple())
        {
            await Assert.That(plans[0].ThrowerSlot).IsEqualTo(1);
            await Assert.That(plans[0].ReleaseTick).IsEqualTo(193);
            await Assert.That(plans[1].ThrowerSlot).IsEqualTo(6).Because("slot 1's fire is claimed by its own smoke");
            await Assert.That(plans[1].ThrowerSource).IsEqualTo(ThrowerSource.WeaponFire);
            await Assert.That(plans[1].ReleaseTick).IsEqualTo(190);
        }
    }

    [Test]
    public async Task Plan_FallsBackToGrenadeThrown_ThenToTheSpawnOffset()
    {
        List<ProjectileTrack> tracks = ProjectileTrack.Collect(
        [
            Sample(300, 300, GrenadeProjectileClasses.Decoy, thrower: 2, position: new Vector3(1, 1, 1), created: true, index: 1),
            Sample(400, 400, GrenadeProjectileClasses.Decoy, thrower: 2, position: new Vector3(1, 1, 1), created: true, index: 2),
            Sample(500, 500, GrenadeProjectileClasses.Decoy, thrower: -1, position: new Vector3(1, 1, 1), created: true, index: 3)
        ], 4);
        GrenadeEventIndex events = GrenadeEventIndex.From(
        [
            new GameEvent("grenade_thrown", -1, 292, 292, 292, new GrenadeThrownEvent { UserId = 2, UserIdPawn = 0, Weapon = "decoy" })
        ]);

        List<ThrowPlan> plans = GrenadeWalker.Plan(tracks, events, FrameAt);

        using (Assert.Multiple())
        {
            await Assert.That(plans[0].ReleaseSource).IsEqualTo(ReleaseSource.GrenadeThrown);
            await Assert.That(plans[0].ReleaseTick).IsEqualTo(292);
            await Assert.That(plans[1].ReleaseSource).IsEqualTo(ReleaseSource.SpawnOffset);
            await Assert.That(plans[1].ReleaseTick).IsEqualTo(400 - GrenadeRules.ReleaseToSpawnTicks);
            await Assert.That(plans[2].ThrowerSlot).IsEqualTo(-1);
            await Assert.That(plans[2].ThrowerSource).IsEqualTo(ThrowerSource.None);
        }
    }

    // ── detonation per kind ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Detonation_TheEventByEntityWins_ThenTheEntityField_ThenTheLastSample()
    {
        ProjectileTrack he = ProjectileTrack.Collect(HeFlight(), 4).Single();
        Vector3 pop = new(460, 0, 64);

        (int? tick, Vector3? position, DetonationSource source, _) = GrenadeWalker.ResolveDetonation(he, default,
            GrenadeEventIndex.From([HeDetonate(204, Index, pop), HeDetonate(150, Index + 1, Vector3.Zero)]), 0, []);
        await Assert.That((tick, position, source)).IsEqualTo(((int?)204, (Vector3?)pop, DetonationSource.Event));

        // The same index reused by a later projectile: its event is not this one's.
        (_, _, source, _) = GrenadeWalker.ResolveDetonation(he, default, GrenadeEventIndex.From([HeDetonate(900, Index, pop)]), 0, []);
        await Assert.That(source).IsEqualTo(DetonationSource.LastSample);

        (tick, _, source, _) = GrenadeWalker.ResolveDetonation(he, new ProjectileRead(null, 1204, null, null),
            GrenadeEventIndex.From([]), 1000, []);
        await Assert.That((tick, source)).IsEqualTo(((int?)204, DetonationSource.Entity))
            .Because("the effect tick is server clock: minus the server start tick");
    }

    [Test]
    public async Task Detonation_ASmokeFallsBackToItsPopFields_AndANeverPoppedOneIsNone()
    {
        ProjectileTrack smoke = ProjectileTrack.Collect(
        [
            Sample(10, 10, GrenadeProjectileClasses.Smoke, position: new Vector3(1, 2, 3), created: true),
            Sample(11, 11, GrenadeProjectileClasses.Smoke, position: new Vector3(9, 2, 3))
        ], 4).Single();
        Vector3 at = new(100, 200, 30);

        (int? tick, Vector3? position, DetonationSource source, _) = GrenadeWalker.ResolveDetonation(smoke,
            new ProjectileRead(null, null, 70, at), GrenadeEventIndex.From([]), 10, []);
        await Assert.That((tick, position, source)).IsEqualTo(((int?)60, (Vector3?)at, DetonationSource.Entity));

        (tick, _, source, _) = GrenadeWalker.ResolveDetonation(smoke, default, GrenadeEventIndex.From([]), 0, []);
        await Assert.That((tick, source)).IsEqualTo(((int?)null, DetonationSource.None));
    }

    [Test]
    public async Task Detonation_AMolotovTakesTheNearestUnclaimedFireWithinFourTicks()
    {
        ProjectileTrack molotov = ProjectileTrack.Collect(
        [
            Sample(10, 10, GrenadeProjectileClasses.Molotov, position: new Vector3(0, 0, 0), created: true),
            Sample(40, 40, GrenadeProjectileClasses.Molotov, position: new Vector3(300, 0, 0)),
            Sample(41, 41, GrenadeProjectileClasses.Molotov, position: new Vector3(300, 0, 0), removed: true)
        ], 4).Single();
        GrenadeEventIndex events = GrenadeEventIndex.From(
        [
            Inferno(47, 900, new Vector3(300, 0, 0)), // six ticks after the removal
            Inferno(42, 901, new Vector3(2000, 0, 0)),
            Inferno(42, 902, new Vector3(305, 0, 0))
        ]);
        HashSet<GrenadeDetonation> claimed = [];

        (int? tick, _, DetonationSource source, int? inferno) = GrenadeWalker.ResolveDetonation(molotov, default, events, 0, claimed);

        using (Assert.Multiple())
        {
            await Assert.That((tick, source, inferno)).IsEqualTo(((int?)42, DetonationSource.Event, (int?)902));
            await Assert.That(claimed.Count).IsEqualTo(1);
        }

        (_, _, _, inferno) = GrenadeWalker.ResolveDetonation(molotov, default, events, 0, claimed);
        await Assert.That(inferno).IsEqualTo(901).Because("the first fire is claimed");
    }

    // ── a whole row ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Assemble_BuildsTheRowFromThePlanTheReadsTheEventsAndTheInputs()
    {
        List<ProjectileTrack> tracks = ProjectileTrack.Collect(HeFlight(), 4);
        GrenadeEventIndex events = GrenadeEventIndex.From([Fire(Slot, 93, 93, "weapon_hegrenade"), HeDetonate(204, Index, new Vector3(460, 0, 64))]);
        List<ThrowPlan> plans = GrenadeWalker.Plan(tracks, events, FrameAt);
        GrenadeReads reads = new();
        // Eight ticks before the release, 40 units east: 40 * 64 / 8 = 320 u/s, running.
        reads.Pawns[(85, Slot)] = new PawnRead(85, new Vector3(-40, 0, 0), 0, 0, 1, false, false, 2, null, false);
        reads.Pawns[(93, Slot)] = new PawnRead(93, new Vector3(0, 0, 0), -12.5f, 90f, 1, true, false, 2, 1.0f, true);
        reads.Pawns[(100, Slot)] = new PawnRead(100, new Vector3(20, 0, 30), -12.5f, 90f, 0, false, false, 2, 1.0f, true);
        ReconstructedInputSource inputs = new([(Slot, 100 - GrenadeRules.JumpWindowTicks, 100)]);
        inputs.Observe(Slot, 70, 0, false); // before the window: carries the button state only
        inputs.Observe(Slot, 94, GrenadeRules.JumpButton, false);
        inputs.Observe(Slot, 96, GrenadeRules.JumpButton, false);
        inputs.SetCoverage(1);
        PlayerInfo thrower = new(Slot, "thrower", 76561198000000001UL, 12, 2, false);

        GrenadeRow row = GrenadeWalker.Assemble(plans, reads, events, inputs, [new ClipRound(1, 10), new ClipRound(2, 90)],
            [thrower], 64, 0).Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.Kind).IsEqualTo(GrenadeKind.He);
            await Assert.That(row.ThrowerSteamId64).IsEqualTo("76561198000000001");
            await Assert.That(row.ThrowerTeam).IsEqualTo(2);
            await Assert.That(row.RoundNumber).IsEqualTo(2);
            await Assert.That(row.ReleaseTick).IsEqualTo(93);
            await Assert.That(row.ReleasePosition).IsEqualTo(new WorldPoint(0, 0, 0));
            await Assert.That(row.ReleaseEyePitch).IsEqualTo(-12.5f);
            await Assert.That(row.ReleaseEyeYaw).IsEqualTo(90f);
            await Assert.That(row.ReleaseOnGround).IsEqualTo(true);
            await Assert.That(row.ReleaseCrouched).IsEqualTo(true);
            await Assert.That(row.ThrowStrengthClass).IsEqualTo(ThrowStrengthClass.Full);
            await Assert.That(row.SpeedAtRelease).IsEqualTo(320f);
            await Assert.That(row.Movement).IsEqualTo(MovementClass.Running);
            await Assert.That(row.SpawnPosition).IsEqualTo(new WorldPoint(0, 0, 64));
            await Assert.That(row.ThrowerOnGroundAtSpawn).IsEqualTo(false);
            await Assert.That(row.JumpThrow).IsTrue().Because("the jump rose one tick after the release, inside the window");
            await Assert.That(row.JumpThrowSource).IsEqualTo(JumpThrowSource.Inputs);
            await Assert.That(row.JumpPressTick).IsEqualTo(94);
            await Assert.That(row.DetonationTick).IsEqualTo(204);
            await Assert.That(row.DetonationSource).IsEqualTo(DetonationSource.Event);
            await Assert.That(row.EndKind).IsEqualTo(GrenadeEndKind.Detonated);
            await Assert.That(row.BounceCount).IsEqualTo(2);
            await Assert.That(row.AirTimeTicks).IsEqualTo(90).Because("at rest from tick 190, before the fuse at 204");
            await Assert.That(row.Trajectory[0].Tick).IsEqualTo(100);
            await Assert.That(GrenadeConsole.Format(row)).IsEqualTo("setpos 0.00 0.00 0.00; setang -12.50 90.00 0.00");
        }
    }

    [Test]
    public async Task Assemble_WithoutInputsOrPawns_SaysSoRatherThanGuessing()
    {
        List<ProjectileTrack> tracks = ProjectileTrack.Collect(HeFlight(), 4);
        GrenadeEventIndex events = GrenadeEventIndex.From([]);
        List<ThrowPlan> plans = GrenadeWalker.Plan(tracks, events, FrameAt);
        ReconstructedInputSource inputs = new([]);

        GrenadeRow row = GrenadeWalker.Assemble(plans, new GrenadeReads(), events, inputs, [], [], 64, 0).Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.ReleaseSource).IsEqualTo(ReleaseSource.SpawnOffset);
            await Assert.That(row.ReleasePosition).IsNull();
            await Assert.That(row.ReleaseEyeYaw).IsNull();
            await Assert.That(row.Movement).IsEqualTo(MovementClass.Unknown);
            await Assert.That(row.ThrowStrengthClass).IsEqualTo(ThrowStrengthClass.Unknown);
            await Assert.That(row.JumpThrowSource).IsEqualTo(JumpThrowSource.None);
            await Assert.That(row.ThrowerTeam).IsEqualTo(0);
            await Assert.That(row.RoundNumber).IsEqualTo(0);
            await Assert.That(GrenadeConsole.Format(row)).IsNull();
        }
    }

    // ── inputs ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Inputs_SeeARiseAcrossTheWindowEdgeAndASubtickPress_AndCoverOnlyWhereCommandsDecoded()
    {
        ReconstructedInputSource inputs = new([(1, 100, 127), (2, 100, 127)]);
        inputs.Observe(1, 99, 0, false);
        inputs.Observe(1, 100, GrenadeRules.JumpButton, false); // rose on the window's first command
        inputs.Observe(1, 101, GrenadeRules.JumpButton, false); // held: not a new press
        inputs.Observe(2, 99, GrenadeRules.JumpButton, false);
        inputs.Observe(2, 110, GrenadeRules.JumpButton, false); // held since before the window
        inputs.Observe(2, 120, GrenadeRules.JumpButton, true); // the sub-tick move says it was pressed again
        inputs.Observe(3, 110, GrenadeRules.JumpButton, false); // no window for slot 3

        using (Assert.Multiple())
        {
            await Assert.That(inputs.FindJumpPress(1, 100, 127)).IsEqualTo(100);
            await Assert.That(inputs.FindJumpPress(2, 100, 127)).IsEqualTo(120);
            await Assert.That(inputs.HasCoverage(1, 100, 127)).IsTrue();
            await Assert.That(inputs.HasCoverage(1, 102, 127)).IsFalse();
            await Assert.That(inputs.HasCoverage(3, 100, 127)).IsFalse();
            await Assert.That(inputs.Decoder).IsEqualTo(ReconstructedInputSource.DecoderName);
        }
    }

    [Test]
    public async Task FrameAtOrBefore_FindsTheLastFrameAtOrBeforeATick()
    {
        DemoFrame[] frames = [RoundIndexTestData.Frame(0, 10), RoundIndexTestData.Frame(1, 10), RoundIndexTestData.Frame(2, 20)];

        using (Assert.Multiple())
        {
            await Assert.That(GrenadeWalker.FrameAtOrBefore(frames, 9)).IsEqualTo(-1);
            await Assert.That(GrenadeWalker.FrameAtOrBefore(frames, 10)).IsEqualTo(1);
            await Assert.That(GrenadeWalker.FrameAtOrBefore(frames, 19)).IsEqualTo(1);
            await Assert.That(GrenadeWalker.FrameAtOrBefore(frames, 99)).IsEqualTo(2);
        }
    }
}
