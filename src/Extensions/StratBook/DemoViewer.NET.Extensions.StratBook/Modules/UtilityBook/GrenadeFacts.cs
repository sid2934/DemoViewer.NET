#region

using System.Numerics;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>The thrower's pawn on one frame, as the rules run read it.</summary>
/// <param name="Tick">Frame clock.</param>
/// <param name="Position">The pawn origin, or null when it did not read.</param>
/// <param name="Pitch"><c>m_angEyeAngles</c> pitch.</param>
/// <param name="Yaw"><c>m_angEyeAngles</c> yaw.</param>
/// <param name="OnGround">The ground bit of <c>m_fFlags</c>.</param>
/// <param name="Ducked">The crouch bit of <c>m_fFlags</c>.</param>
/// <param name="Team">2 = T, 3 = CT, 0 when unread.</param>
/// <param name="ThrowStrength">The held grenade's <c>m_flThrowStrength</c>; null when the held weapon was not a grenade.</param>
/// <param name="HeldGrenade">Whether the held weapon's class was a grenade.</param>
public readonly record struct ThrowerRead(
    int Tick,
    Vector3? Position,
    float? Pitch,
    float? Yaw,
    bool? OnGround,
    bool? Ducked,
    int Team,
    float? ThrowStrength,
    bool HeldGrenade);

/// <summary>What the thrower's pawn and commands said on the frame the projectile's instance started.</summary>
/// <param name="Position">The pawn origin.</param>
/// <param name="OnGround">The ground bit.</param>
/// <param name="Team">2 = T, 3 = CT, 0 when unread.</param>
/// <param name="LastJumpTick">The frame-clock tick <c>IN_JUMP</c> last rose on, -1 before the first.</param>
/// <param name="Coverage">The thrower's commands over the last 64 ticks divided by 64; 0 says the lane carries none.</param>
public readonly record struct ThrowerAtSpawn(Vector3? Position, bool? OnGround, int Team, int LastJumpTick, double Coverage);

/// <summary>
///     One grenade as the rules run recorded it: the <c>grenade_projectiles</c> row of a grenade whose thrower
///     resolved, or the <c>grenade_landings</c> row of one whose thrower never did, which carries the landing alone.
/// </summary>
/// <param name="EntityIndex">The projectile's entity index.</param>
/// <param name="Serial">The entity serial; with the index, one projectile for its life.</param>
/// <param name="ClassName">The server class, one of <see cref="GrenadeProjectileClasses" />.</param>
/// <param name="ThrowerSlot">The thrower's slot held from its first resolution, or -1.</param>
/// <param name="SpawnTick">The frame-clock tick the projectile first appeared on.</param>
/// <param name="CreatedTick">The tick its instance started on: the spawn unless <c>projectile_created</c> waited for the thrower.</param>
/// <param name="EndTick">The removal frame's tick, or the run's last tick when it was still live.</param>
/// <param name="Removed">False for a grenade still in the world when the run ended.</param>
/// <param name="InitialPosition"><c>m_vInitialPosition</c>; on a landing row, from the observer's flight (<see cref="GrenadeWalker.WithFlights" />).</param>
/// <param name="InitialVelocity"><c>m_vInitialVelocity</c>; as <paramref name="InitialPosition" />.</param>
/// <param name="LastPosition">The last position read, or null when none decoded.</param>
/// <param name="LastMovedTick">The tick it came to rest on; on a landing row, the removal tick until the flight says.</param>
/// <param name="Bounces"><c>m_nBounces</c> as last seen.</param>
/// <param name="IsIncendiary"><c>m_bIsIncGrenade</c>; as <paramref name="InitialPosition" />.</param>
/// <param name="ExplodeTickBegin"><c>m_nExplodeEffectTickBegin</c> (server clock), or null while unset.</param>
/// <param name="SmokeTickBegin"><c>m_nSmokeEffectTickBegin</c> (server clock), or null while unset.</param>
/// <param name="SmokeDetonationPosition"><c>m_vSmokeDetonationPos</c>, or null until the smoke pops.</param>
/// <param name="AtSpawn">The thrower on the created frame; null on a landing row.</param>
/// <param name="OffsetRelease">The thrower seven ticks before the created frame, for a release no <c>weapon_fire</c> marked.</param>
/// <param name="OffsetBefore">The thrower's position eight ticks before <paramref name="OffsetRelease" />.</param>
public sealed record ProjectileFacts(
    int EntityIndex,
    int Serial,
    string ClassName,
    int ThrowerSlot,
    int SpawnTick,
    int CreatedTick,
    int EndTick,
    bool Removed,
    Vector3? InitialPosition,
    Vector3? InitialVelocity,
    Vector3? LastPosition,
    int LastMovedTick,
    int Bounces,
    bool? IsIncendiary,
    int? ExplodeTickBegin,
    int? SmokeTickBegin,
    Vector3? SmokeDetonationPosition,
    ThrowerAtSpawn? AtSpawn,
    ThrowerRead? OffsetRelease,
    Vector3? OffsetBefore)
{
    /// <summary>The row id: <c>g{index}-{serial}</c>.</summary>
    public string Id => $"g{EntityIndex}-{Serial}";

    /// <summary>
    ///     The tick of the last frame the projectile existed on. A removal is noticed on the first frame the
    ///     slot is empty, so a removed grenade was last seen the tick before on a per-tick recording.
    /// </summary>
    public int LastTick => Removed ? EndTick - 1 : EndTick;
}

/// <summary>A grenade <c>weapon_fire</c> with the thrower's pawn as the event arrived in it.</summary>
/// <param name="Slot">The thrower's slot.</param>
/// <param name="Tick">Frame clock.</param>
/// <param name="ProjectileClass">The projectile class the weapon throws.</param>
/// <param name="Weapon">The event's weapon name, which tells a molotov from an incendiary.</param>
/// <param name="Pawn">The thrower's pawn on that frame, the state before its entity update.</param>
/// <param name="Before">The thrower's position eight ticks earlier, or null when unread.</param>
public sealed record ReleaseFacts(int Slot, int Tick, string ProjectileClass, string Weapon, ThrowerRead Pawn, Vector3? Before);

/// <summary>A projectile with its thrower and release decided, before any row is built.</summary>
/// <param name="Projectile">The grenade.</param>
/// <param name="ThrowerSlot">The thrower's slot, or -1.</param>
/// <param name="ThrowerSource">Where the thrower came from.</param>
/// <param name="ReleaseTick">Frame clock.</param>
/// <param name="ReleaseSource">Where the release tick came from.</param>
/// <param name="Weapon">The release's weapon name, or null.</param>
/// <param name="Release">The thrower's pawn at the release, or null when nothing read it.</param>
/// <param name="Before">The thrower's position eight ticks before the release.</param>
public sealed record GrenadeThrow(
    ProjectileFacts Projectile,
    int ThrowerSlot,
    ThrowerSource ThrowerSource,
    int ReleaseTick,
    ReleaseSource ReleaseSource,
    string? Weapon,
    ThrowerRead? Release,
    Vector3? Before);

/// <summary>The three grenade tables, folded into what the walk joins.</summary>
/// <param name="Projectiles">Every grenade, in spawn order, the landings of unresolved throwers included.</param>
/// <param name="Releases">Every grenade <c>weapon_fire</c>, in tick order.</param>
/// <param name="Events">The detonation events.</param>
public sealed record GrenadeTableRows(
    IReadOnlyList<ProjectileFacts> Projectiles,
    IReadOnlyList<ReleaseFacts> Releases,
    GrenadeEventIndex Events);

/// <summary>Everything one demo's grenade rows are built from: the tables and the flights the observer kept.</summary>
/// <param name="Tables">The rules run's grenade tables.</param>
/// <param name="Flights">The trajectories, by projectile.</param>
public sealed record GrenadeWalkInputs(GrenadeTableRows Tables, GrenadeTrajectories Flights)
{
    /// <summary>The inputs of a pass's visit: its tables and its trajectory collection.</summary>
    /// <param name="context">The pass context.</param>
    public static GrenadeWalkInputs Read(IPassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GrenadeTrajectories flights = context.Collected<GrenadeTrajectories>(GrenadeTrajectoryCollector.CollectorId)
                                      ?? throw new InvalidOperationException("the grenade trajectory collector put no collection");
        return new GrenadeWalkInputs(GrenadeTables.Read(context.Table, context.Demo.ServerStartTick), flights);
    }

    /// <summary>The rounds a pass context describes, on the frame clock.</summary>
    /// <param name="context">The pass context.</param>
    public static IReadOnlyList<ClipRound> Rounds(IPassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [.. context.Rounds.Select(r => new ClipRound(r.Number, r.FreezeEndTick))];
    }
}
